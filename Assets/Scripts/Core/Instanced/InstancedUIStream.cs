using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2020_1_OR_NEWER
using Unity.Profiling;
#endif

namespace FairyGUI
{
    /// <summary>
    /// v4 core stream: extracts a container's visible leaves into GPU-resident quad
    /// instances (QuadReassembler), adjacency-sorts them FairyBatching-style so leaves
    /// sharing a texture become contiguous where draw order may legally change (M2),
    /// and segments the result on texture change.
    ///
    /// Submission (M5): each segment is a real MeshRenderer child of the container,
    /// vertex-pulling quads from the shared instance buffer by SV_VertexID. Real
    /// renderers give the stream sortingOrder interleaving with NATIVE fallback
    /// content (non-quad topology, stencil-mask/painting scopes keep their own
    /// renderers): fallback leaves act as immovable sort barriers splitting the
    /// stream into runs; a run's segments share the free sortingOrder slot of a
    /// claimed leaf below the barrier's own order, and a per-segment transform z
    /// step orders segments within a run. SetChildrenLayer carries the segment
    /// renderers along, so CaptureCamera filter/painting captures of scopes
    /// CONTAINING the stream include instanced content (review M12).
    ///
    /// Update tiers (design §4.2): whole-stream movement/scrolling is the container
    /// transform (free — segments are its children); a single leaf's content change
    /// is UpdateLeaf (partial instance-buffer upload); structure changes are Extract
    /// (full recompile), driven by the M4 push channels in in-place mode.
    ///
    /// Clipping (M3): the external window (the container's or its mask parent's
    /// clipRect) is a uniform tested against the scrolled position; internal nested
    /// rect clips are folded by intersection into ClipBuffer entries referenced per
    /// instance, so one segment can span many clip regions — draw count does not
    /// grow with clip region count. Both support FairyGUI clipSoftness (literal
    /// pixel fade; native's screen-relative scale renders sub-pixel).
    /// </summary>
    public class InstancedUIStream : IDisposable
    {
        //fast guard so the DisplayObject-level push hooks cost one static int
        //compare when no in-place stream exists
        internal static int liveInPlaceCount;

        //productization (batch 4): every in-place stream registers here; the
        //Stage drives them all after each update pass, so users only toggle
        //Container.instancedRendering and never call Render() themselves
        static readonly List<InstancedUIStream> sLiveStreams = new List<InstancedUIStream>();

        /// <summary>Copies the live in-place streams into results (diagnostics).</summary>
        public static void GetLiveStreams(List<InstancedUIStream> results)
        {
            results.Clear();
            results.AddRange(sLiveStreams);
        }

        /// <summary>
        /// Renders every live in-place stream. The Stage calls this after its
        /// update pass (renderingOrder must be assigned first); a second call in
        /// the same frame is a cheap no-op (queues empty, uniforms unchanged).
        /// </summary>
        public static void RenderAll()
        {
            for (int i = sLiveStreams.Count - 1; i >= 0; i--)
                sLiveStreams[i].Render();
        }

        /// <summary>
        /// Editor A/B switch: force new streams onto the vertex-stream backend even
        /// where vertex StructuredBuffers are available (M6 validation ladder 1).
        /// </summary>
        public static bool forceVertexPath;

        static int sVertexBufferCaps = -1;

        /// <summary>
        /// The vertex-stream backend serves platforms without vertex-stage
        /// StructuredBuffer: WebGL / mini-games (no SSBO at all) and GLES3.x
        /// devices reporting too few vertex SSBO slots. Decided per stream at
        /// construction; instance data is baked x4 into each segment's mesh
        /// vertices (QuadVertex) and internal clips become uniform arrays.
        /// </summary>
        public static bool useVertexPath
        {
            get
            {
                if (forceVertexPath)
                    return true;
                if (sVertexBufferCaps < 0)
                    sVertexBufferCaps = SystemInfo.maxComputeBufferInputsVertex;
                return sVertexBufferCaps < 2; //the buffer path binds _Instances + _Clips
            }
        }

        /// <summary>Max internal clip regions on the vertex path (attribs shader uniform array size).</summary>
        public const int MaxVertexPathClips = 16;

        //shared local-index pattern (0,1,2)(2,1,3) per quad, grown on demand
        static int[] sIndexCache;

        static void EnsureIndexCache(int quadCount)
        {
            if (sIndexCache != null && sIndexCache.Length >= quadCount * 6)
                return;
            int capacity = Mathf.NextPowerOfTwo(quadCount);
            var tris = new int[capacity * 6];
            for (int q = 0; q < capacity; q++)
            {
                int v = q * 4, t = q * 6;
                tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v + 2; tris[t + 4] = v + 1; tris[t + 5] = v + 3;
            }
            sIndexCache = tris;
        }

        /// <summary>
        /// Transform channel: leaf changes queue a tier-2 rewrite on their owning
        /// stream; container changes mark the enclosing stream structure-dirty —
        /// except the stream root itself, whose matrix is read fresh every Render
        /// (tier 0), which is what makes in-place ScrollPane scrolling free.
        /// </summary>
        internal static void _NotifyTransform(DisplayObject obj)
        {
            if (liveInPlaceCount == 0)
                return;

            NGraphics g = obj.graphics;
            if (g != null)
            {
                if (g._instancedBy != null)
                    g._instancedBy._QueueLeafUpdate(g);
                if (g.subInstances != null)
                {
                    foreach (var sub in g.subInstances)
                        if (sub._instancedBy != null)
                            sub._instancedBy._QueueLeafUpdate(sub);
                }
            }

            if (obj is Container c)
            {
                if (c._instancedStream != null)
                    return; //stream root: tier 0, nothing to do
                FqsMount viaMount = null;
                for (Container p = c.parent; p != null; p = p.parent)
                {
                    if (viaMount == null && p._fqsMount != null)
                        viaMount = p._fqsMount;
                    if (p._instancedStream != null)
                    {
                        //M8-4: a container moved INSIDE a valid spliced mount is
                        //serviced as per-leaf tier-2 rewrites (slot-relative
                        //matrices make them exact); only unserviceable cases
                        //invalidate and recompile
                        if (viaMount != null && !viaMount.invalid && viaMount.spliced)
                        {
                            if (p._instancedStream._OnMountInteriorTransform(c))
                                return;
                            viaMount.invalid = true;
                        }
                        p._instancedStream._OnInteriorContainerMoved(c);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Structure channel: walk up from the object (inclusive) to the enclosing
        /// stream root and mark it for recompile.
        /// </summary>
        internal static void _NotifyStructure(DisplayObject obj)
        {
            if (liveInPlaceCount == 0)
                return;
            for (DisplayObject p = obj; p != null; p = p.parent)
            {
                if (p is Container c)
                {
                    //structure changed inside a baked mount: blob no longer
                    //matches the live subtree — runtime walk takes over
                    if (c._fqsMount != null)
                        c._fqsMount.invalid = true;
                    if (c._instancedStream != null)
                    {
                        c._instancedStream._structureDirty = true;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Visibility channel (M8-4): a toggle INSIDE a valid baked mount is a
        /// quad-range rewrite (hide = zero the ranges, show = rebuild from the
        /// live meshes) — gear paging and transition display locks stop
        /// recompiling. Everything else keeps the structure semantics.
        /// </summary>
        internal static bool _NotifyVisible(DisplayObject obj)
        {
            if (liveInPlaceCount == 0)
                return false;
            FqsMount mount = obj is Container oc && oc._fqsMount != null ? oc._fqsMount : null;
            InstancedUIStream stream = null;
            for (DisplayObject p = obj.parent; p != null; p = p.parent)
            {
                if (p is Container c)
                {
                    if (mount == null && c._fqsMount != null)
                        mount = c._fqsMount;
                    if (c._instancedStream != null)
                    {
                        stream = c._instancedStream;
                        break;
                    }
                }
            }
            return mount != null && !mount.invalid && mount.spliced && stream != null
                && stream._OnMountVisibility(obj);
        }

        class Segment
        {
            //cross-atlas key (batch 3d): a segment carries up to 4 textures
            //(_MainTex + _Tex1.._Tex3); quads select by flags bits 16-17, so
            //text/image atlas alternation no longer cuts segments
            public readonly Texture[] textures = new Texture[MaxSegmentTextures];
            public int texCount;
            public float z;
            public int start;
            public int count;
            public int runIndex;      //fallback barriers delimit runs (M5)
            public Material material;
            public MaterialPropertyBlock props;
            //the segment IS a renderer: a plain child GameObject of the container
            //whose MeshRenderer pulls quads from the instance buffer by SV_VertexID.
            //Being a real renderer gives it sortingOrder (interleaving with native
            //fallback leaves) and CaptureCamera compatibility for free.
            public GameObject go;
            public MeshFilter filter;
            public MeshRenderer renderer;
            public Mesh mesh; //vertex path only: per-segment mesh with baked QuadVertex data
            public int meshQuadCap = -1; //quad count the mesh's params/indices are laid out for
            public int lastSortingOrder = int.MinValue;
            public int lastLayer = -1;
            //deferred tier-2 uploads coalesce into one range per segment (batch 2)
            public int dirtyMin = int.MaxValue;
            public int dirtyMax = -1;
        }

        class LeafRange
        {
            public NGraphics graphics;
            public int start;
            public int count;     //reserved range (text leaves include slack)
            public int liveCount; //quads actually in use within the range
            public float bakedAlpha; //context alpha baked into the quads (color tier)
            public uint slotIndex;   //transform slot the quads are baked against
            public FqsMount mount;   //M8-2: owning mount (rewrite/invalidate protocol)
            public bool hidden;      //M8-4: visibility tier zeroed this range
            public uint texIndexBits; //segment texture-slot bits (flags 16-17)
            public int segIndex;
            public uint flags;
            public uint clipIndex;
            public bool sdf;   //M7: tier-2 updates re-emit analytically, not from mesh
            public bool curve; //M9b: same, from the CurveTextMesh layout
        }

        struct PendingLeaf
        {
            public NGraphics graphics;
            public DisplayObject scope; //container-level fallback scope (mask/painting/GoWrapper); graphics is the blit quad or null
            public Texture texture;
            public uint flags;
            public uint clipIndex;
            public int stageStart;    //quads pre-built into _staging at walk time
            public int stageCount;
            public bool instanceable; //false: non-quad topology -> native fallback
            public bool sdf;          //M7: quads carry analytic SDF coverage
            public bool curve;        //M9b: quads are curve-text glyphs
            public float bakeAlpha;   //graphics alpha at stage time (color tier)
            public uint slotIndex;    //transform slot the quads are baked against
            public FqsMount mount;    //M8-2: whole-subtree splice from a baked blob
            public uint[] mountClipMap; //blob clip index -> stream clip index
        }

        readonly List<Segment> _segments = new List<Segment>();
        readonly List<LeafRange> _leaves = new List<LeafRange>();
        readonly Dictionary<NGraphics, LeafRange> _leafLookup = new Dictionary<NGraphics, LeafRange>();
        readonly List<PendingLeaf> _pending = new List<PendingLeaf>();
        readonly List<AdjacencyEntry> _entries = new List<AdjacencyEntry>();
        //internal clip regions, content-space at scroll 0, WITHOUT drawOffset
        //(offset added at upload); entry 0 is the "no clip" sentinel
        readonly List<ClipEntry> _clipEntries = new List<ClipEntry>();
        readonly List<QuadInstance> _quads = new List<QuadInstance>();
        readonly List<QuadInstance> _staging = new List<QuadInstance>();
        //the fallback that CLOSES run r (absent for the last run): a leaf's
        //graphics, or a container-level scope (stencil mask / painting capture /
        //GoWrapper) whose native renderers occupy a contiguous order block.
        //order is the LAST slot of that block: the ceiling test excludes leaves
        //sorted past the barrier either way (claimed leaves never hold orders
        //inside the block), and the empty-run floor (order+1) must land ABOVE
        //the whole block, not inside it
        struct RunBarrier
        {
            public NGraphics graphics;  //leaf fallback, or a painting scope's blit quad
            public DisplayObject scope; //masked container / GoWrapper when graphics is null

            public int order
            {
                get
                {
                    if (graphics != null)
                        return graphics.renderingOrder;
                    Container mc = scope as Container;
                    if (mc != null && mc.mask != null && mc.mask.graphics != null)
                        return mc.mask.graphics._StencilEraserOrder; //the eraser draws LAST in a masked subtree
                    GoWrapper gw = scope as GoWrapper;
                    if (gw != null)
                        return gw._MaxRenderingOrder;
                    return scope != null ? scope.renderingOrder : 0;
                }
            }
        }
        readonly List<RunBarrier> _runBarriers = new List<RunBarrier>();
        readonly List<GameObject> _segmentPool = new List<GameObject>();
        readonly List<Mesh> _meshPool = new List<Mesh>();
        //batch 3: recompiles reuse instead of reallocate — prior segments transfer
        //their renderer/MPB/mesh in place when the texture layout matches by index
        readonly List<Segment> _prevSegments = new List<Segment>();
        readonly List<Segment> _segmentObjPool = new List<Segment>();
        readonly List<MaterialPropertyBlock> _mpbPool = new List<MaterialPropertyBlock>();
        int _bufferCapacity;
        int _clipBufferCapacity;
        readonly List<int> _runOrderScratch = new List<int>();
        Mesh _pullMesh;
        int _pullCapacity;
        bool _vertexPath;
        QuadVertex[] _vertexUpload;
        Vector4[] _clipRectArr;
        Vector4[] _clipSoftArr;
        bool _clipOverflowWarned;
        struct TexSetKey : IEquatable<TexSetKey>
        {
            public Texture t0, t1, t2, t3;
            public bool Equals(TexSetKey o) { return t0 == o.t0 && t1 == o.t1 && t2 == o.t2 && t3 == o.t3; }
            public override bool Equals(object o) { return o is TexSetKey k && Equals(k); }
            public override int GetHashCode()
            {
                int h = t0 != null ? t0.GetInstanceID() : 0;
                h = (h * 397) ^ (t1 != null ? t1.GetInstanceID() : 0);
                h = (h * 397) ^ (t2 != null ? t2.GetInstanceID() : 0);
                h = (h * 397) ^ (t3 != null ? t3.GetInstanceID() : 0);
                return h;
            }
        }

        public const int MaxSegmentTextures = 4;
        readonly Dictionary<TexSetKey, Material> _materialCache = new Dictionary<TexSetKey, Material>();

        Container _container;
        bool _sortAdjacency;
        bool _inPlace;
        bool _structureDirty;
        readonly List<NGraphics> _dirtyLeaves = new List<NGraphics>();
        readonly HashSet<NGraphics> _dirtyLeafSet = new HashSet<NGraphics>();
        //color tier (batch 3): alpha/tint-only changes touch just the color field
        readonly List<NGraphics> _colorLeaves = new List<NGraphics>();
        readonly HashSet<NGraphics> _colorLeafSet = new HashSet<NGraphics>();
        HashSet<NGraphics> _claimed = new HashSet<NGraphics>();
        HashSet<NGraphics> _claimScratch = new HashSet<NGraphics>();
        readonly HashSet<NTexture> _watchedTextures = new HashSet<NTexture>();
        Action<NTexture> _onWatchedTexture;
        Shader _shader;
        ComputeBuffer _buffer;
        ComputeBuffer _clipBuffer;
        ClipEntry[] _clipUploadArray;
        QuadInstance[] _uploadArray;
        Vector2 _scrollOffset;
        Vector4 _clipRect;
        Vector4 _clipSoft;
        //per-frame sync elision (batch 2): push property blocks only when the
        //stream-level uniforms actually changed (or after an Extract)
        bool _propsDirty;
        Vector2 _lastScroll;
        Vector4 _lastClipRect, _lastClipSoft;
        int _lastCurveFontVersion = -1;
        int _lastRunOrderProbe = int.MinValue;
        Vector2 _drawOffset;
        int _skippedPairs;
        int _maskedSubtrees;
        bool _rootMaskWarned;
        //transform slots (batch 3, design §4.2 tier 1): interior containers that
        //keep moving get promoted to a slot — their subtree quads are baked in
        //SLOT-local space and the shader multiplies by the slot matrix, so a
        //container tween/interior scroll is a matrix write, not a recompile.
        //Adaptive: the FIRST move of a container recompiles and marks it hot,
        //the recompile assigns it a slot, every later move is tier 1.
        public const int MaxTransformSlots = 16; //index 0 = identity
        readonly Dictionary<Container, int> _slotIndices = new Dictionary<Container, int>();
        readonly Container[] _slotOwners = new Container[MaxTransformSlots];
        readonly Matrix4x4[] _slotMatrixArr = new Matrix4x4[MaxTransformSlots];
        readonly Dictionary<Container, int> _hotContainers = new Dictionary<Container, int>();
        static readonly List<Container> sHotScratch = new List<Container>();
        bool _slotsDirty;
        bool _hasSlottedClips;
        Matrix4x4 _rootWorldToLocal;
        //internal clip metadata for slot-aware recompute: entry i's rect can be
        //re-derived from its owner's CURRENT transform when a slot moves
        struct ClipMeta
        {
            public Container owner;
            public Rect rect;
            public uint parentIndex;
            public uint slotIndex;
        }
        readonly List<ClipMeta> _clipMeta = new List<ClipMeta>();

        static readonly List<Vector3> sVerts = new List<Vector3>();
        static readonly List<Vector2> sUVs = new List<Vector2>();
        static readonly List<Color32> sColors = new List<Color32>();
        static readonly List<int> sTris = new List<int>();
        static readonly List<QuadInstance> sLeafScratch = new List<QuadInstance>();

#if UNITY_2020_1_OR_NEWER
        static readonly ProfilerMarker sExtract = new ProfilerMarker("InstancedUI.Extract");
        static readonly ProfilerMarker sScroll = new ProfilerMarker("InstancedUI.Scroll");
        static readonly ProfilerMarker sLeafUpdate = new ProfilerMarker("InstancedUI.LeafUpdate");
        static readonly ProfilerMarker sRender = new ProfilerMarker("InstancedUI.Render");
#endif

        public int segmentCount { get { return _segments.Count; } }

        /// <summary>The stream's root container (diagnostics).</summary>
        public Container container { get { return _container; } }

        /// <summary>Which backend this stream compiled against (diagnostics).</summary>
        public string backendName { get { return _vertexPath ? "vertex-stream" : "buffer"; } }

        /// <summary>Leaves currently claimed away from native rendering (diagnostics).</summary>
        public int claimedLeafCount { get { return _claimed.Count; } }

        /// <summary>Validation/diagnostics probe: full recompiles since construction.</summary>
        public int extractCount { get; private set; }

        /// <summary>Transform slots currently assigned (hot interior containers).</summary>
        public int slotCount { get { return _slotIndices.Count; } }

        /// <summary>Hot containers that could not get a slot in the last Extract (>15).</summary>
        public int slotOverflow { get; private set; }

        /// <summary>Diagnostics/CI probe: compiled leaf ranges.</summary>
        public int leafCount { get { return _leaves.Count; } }

        //--- vertex-backend upload width (diagnostics) ----------------------
        //The vertex path pays 4 vertices per quad, so this width IS its upload
        //bandwidth. Exposed because QuadVertex is internal and a validation
        //gate in another assembly has to be able to pin the number: a field
        //widened by 4 bytes costs every WebGL frame silently while the pixel
        //gates stay green.
        public static int vertexUploadStride { get { return QuadVertex.Stride; } }

        public static int vertexUploadStructSize
        {
            get { return System.Runtime.InteropServices.Marshal.SizeOf<QuadVertex>(); }
        }

        /// <summary>
        /// The stride Unity ACTUALLY allocated for this stream's segment
        /// meshes, read back from the mesh. The constants above are what we
        /// declared; this is what the GPU got. -1 when no segment mesh exists
        /// (buffer backend, or nothing compiled yet).
        /// </summary>
        public int measuredVertexStride
        {
            get
            {
                for (int i = 0; i < _segments.Count; i++)
                {
                    Mesh m = _segments[i].mesh;
                    if (m != null && m.vertexCount > 0)
                        return m.GetVertexBufferStride(0);
                }
                return -1;
            }
        }

        /// <summary>
        /// Round-trips one instance through the vertex writer and rebuilds the
        /// four packed bytes the way the shader does. Exposed for validation:
        /// under FlagCurveGlyph those bytes are a glyph index, not radii, so
        /// the packing has to be lossless in a way plain radii would forgive.
        /// </summary>
        public static uint PackedRadiiForDiagnostics(in QuadInstance q)
        {
            var v = new QuadVertex[4];
            QuadVertex.WriteQuad(v, 0, in q);
            return v[0].radBL | ((uint)v[0].radBR << 8)
                 | ((uint)v[0].radTL << 16) | ((uint)v[0].radTR << 24);
        }

        /// <summary>Bytes the declared attribute layout actually describes —
        /// must agree with both of the above or the GPU reads shifted fields.</summary>
        public static int vertexUploadLayoutSize
        {
            get
            {
                int n = 0;
                var layout = QuadVertex.Layout;
                for (int i = 0; i < layout.Length; i++)
                {
                    var f = layout[i].format;
                    n += layout[i].dimension * (f == VertexAttributeFormat.Float32 ? 4
                        : f == VertexAttributeFormat.Float16 || f == VertexAttributeFormat.UNorm16 ? 2 : 1);
                }
                return n;
            }
        }

        /// <summary>Diagnostics/CI probe (M8-6 parity): copies the compiled quad
        /// stream out for comparison harnesses.</summary>
        public void CopyQuadsForDiagnostics(List<QuadInstance> into)
        {
            into.Clear();
            into.AddRange(_quads);
        }
        public int quadCount { get { return _quads.Count; } }

        /// <summary>
        /// Submission runs delimited by fallback barriers (segments of one run share
        /// a sortingOrder slot; native fallback renderers draw between runs).
        /// </summary>
        public int runCount { get { return _runBarriers.Count + 1; } }

        /// <summary>
        /// Internal clip regions found by the last Extract, including the entry-0
        /// "no clip" sentinel.
        /// </summary>
        public int clipEntryCount { get { return _clipEntries.Count; } }

        /// <summary>
        /// Triangle pairs that could not be reassembled as quads in the last Extract —
        /// content for the mesh fallback path (M5).
        /// </summary>
        public int lastSkippedPairs { get { return _skippedPairs; } }

        /// <summary>
        /// Stencil-masked subtrees skipped by the last Extract — content for the
        /// fallback scope path (M5).
        /// </summary>
        public int lastMaskedSubtrees { get { return _maskedSubtrees; } }

        /// <summary>Validation probe: the clip entry index a leaf was stamped with.</summary>
        public uint GetLeafClipIndex(NGraphics graphics)
        {
            return _leafLookup.TryGetValue(graphics, out LeafRange r) ? r.clipIndex : 0;
        }

        /// <summary>Validation probe: a clip entry by index.</summary>
        public ClipEntry GetClipEntry(int index)
        {
            return _clipEntries[index];
        }

        /// <summary>Validation probe: whether a leaf is currently claimed (in-place).</summary>
        public bool IsClaimed(NGraphics graphics)
        {
            return _claimed.Contains(graphics);
        }

        /// <summary>
        /// M8-1 (FqsBaker): copies the compiled stream state out for baking.
        /// Pure read — the stream stays usable afterwards.
        /// </summary>
        internal void _CaptureBakeSnapshot(List<QuadInstance> quads,
            List<FqsSegSnap> segs, List<FqsLeafSnap> leaves, List<FqsClipSnap> clips)
        {
            quads.AddRange(_quads);
            for (int i = 0; i < _segments.Count; i++)
            {
                Segment sg = _segments[i];
                segs.Add(new FqsSegSnap
                {
                    start = sg.start, count = sg.count, runIndex = sg.runIndex, z = sg.z,
                    t0 = sg.texCount > 0 ? sg.textures[0] : null,
                    t1 = sg.texCount > 1 ? sg.textures[1] : null,
                    t2 = sg.texCount > 2 ? sg.textures[2] : null,
                    t3 = sg.texCount > 3 ? sg.textures[3] : null,
                });
            }
            for (int i = 0; i < _leaves.Count; i++)
            {
                LeafRange l = _leaves[i];
                leaves.Add(new FqsLeafSnap
                {
                    graphics = l.graphics,
                    start = l.start, count = l.count, liveCount = l.liveCount,
                    flags = l.flags, clipIndex = l.clipIndex, slotIndex = l.slotIndex,
                    bakedAlpha = l.bakedAlpha, sdf = l.sdf, curve = l.curve,
                });
            }
            for (int i = 0; i < _clipEntries.Count; i++)
            {
                clips.Add(new FqsClipSnap
                {
                    rect = _clipEntries[i].rect, soft = _clipEntries[i].soft,
                    parentIndex = i < _clipMeta.Count ? _clipMeta[i].parentIndex : 0,
                    slotIndex = i < _clipMeta.Count ? _clipMeta[i].slotIndex : 0,
                    owner = i < _clipMeta.Count ? _clipMeta[i].owner : null,
                });
            }
        }

        /// <summary>Structure channel consumer: recompile on the next Flush/Render.</summary>
        internal void _MarkStructureDirty()
        {
            _structureDirty = true;
        }

        /// <summary>Content/transform channel consumer: tier-2 rewrite on next Flush.</summary>
        internal void _QueueLeafUpdate(NGraphics graphics)
        {
            if (_dirtyLeafSet.Add(graphics))
                _dirtyLeaves.Add(graphics);
        }

        /// <summary>Color tier consumer (batch 3): alpha/tint rewrite on next Flush.</summary>
        internal void _QueueLeafColor(NGraphics graphics)
        {
            if (_colorLeafSet.Add(graphics))
                _colorLeaves.Add(graphics);
        }

        /// <summary>
        /// Transform channel, interior container: slotted containers take tier 1
        /// (matrix write); everything else recompiles — and in in-place mode the
        /// container is marked hot so the recompile promotes it to a slot.
        /// </summary>
        internal void _OnInteriorContainerMoved(Container c)
        {
            if (_slotIndices.ContainsKey(c))
            {
                _slotsDirty = true;
                return;
            }
            if (_inPlace)
                _hotContainers[c] = Time.frameCount;
            _structureDirty = true;
        }

        void OnWatchedTextureChanged(NTexture texture)
        {
            //sub-atlas movement / atlas rebuild: segment textures are stale
            _structureDirty = true;
        }

        /// <summary>
        /// Applies queued push notifications: structure-dirty recompiles; dirty
        /// leaves get tier-2 partial rewrites (falling back to a recompile when a
        /// leaf's quad count changed). Render() calls this automatically in
        /// in-place mode.
        /// </summary>
        public void Flush()
        {
            if (_structureDirty)
            {
                _structureDirty = false;
                _dirtyLeaves.Clear();
                _dirtyLeafSet.Clear();
                _colorLeaves.Clear();
                _colorLeafSet.Clear();
                Extract();
                //fall through: a mount splice may have re-queued rewritten or
                //color-stale leaves (M8-2 self-heal) — settle them THIS frame
                //so a re-splice never renders stale blob data even for one frame
            }

            if (_dirtyLeaves.Count > 0 || _colorLeaves.Count > 0)
            {
                for (int i = 0; i < _dirtyLeaves.Count; i++)
                {
                    NGraphics g = _dirtyLeaves[i];
                    if (_inPlace && g._instancedBy != this)
                        continue; //released while queued
                    bool ok = UpdateLeaf(g, true);
                    //M8-2 mount protocol: successful rewrites are remembered so
                    //a later re-splice refreshes them over the stale blob data;
                    //a failed rewrite (count change) invalidates the whole mount
                    if (_leafLookup.TryGetValue(g, out LeafRange mlr) && mlr.mount != null)
                    {
                        if (ok)
                            mlr.mount.rewritten.Add(g);
                        else
                            mlr.mount.invalid = true;
                    }
                    if (!ok)
                        _structureDirty = true;
                }
                for (int i = 0; i < _colorLeaves.Count; i++)
                {
                    NGraphics g = _colorLeaves[i];
                    if (_inPlace && g._instancedBy != this)
                        continue;
                    if (_dirtyLeafSet.Contains(g))
                        continue; //the full rewrite above already re-baked colors
                    UpdateLeafColor(g);
                }
                //one coalesced upload per touched segment instead of one per leaf
                UploadAllDirtyRanges();
                _dirtyLeaves.Clear();
                _dirtyLeafSet.Clear();
                _colorLeaves.Clear();
                _colorLeafSet.Clear();
                if (_structureDirty)
                {
                    _structureDirty = false;
                    Extract();
                }
            }
        }

        /// <summary>
        /// drawOffset shifts the stream in container space (0 for in-place rendering,
        /// non-zero for side-by-side verification replicas). sortAdjacency applies the
        /// FairyBatching adjacency sort during Extract to shrink segment count.
        ///
        /// inPlace makes the stream REPLACE native rendering: extracted leaves stop
        /// rendering through their own MeshRenderer (claim mark lives on the leaf,
        /// which self-recovers on dispose/reparent), and the push channels — content,
        /// transform, visible, structure — drive updates automatically; call Render()
        /// once per frame after the stage update and everything else follows.
        /// </summary>
        public InstancedUIStream(Container container, Vector2 drawOffset = default, bool sortAdjacency = true, bool inPlace = false)
        {
            _container = container;
            _drawOffset = drawOffset;
            _sortAdjacency = sortAdjacency;
            _inPlace = inPlace;
            _onWatchedTexture = OnWatchedTextureChanged;
            if (inPlace)
            {
                //mutual exclusion (review batch 1): one in-place stream per
                //container, and never on top of the deprecated MergedBatch —
                //both toggle the same forceRenderingOff flags on the leaves
                if (container._instancedStream != null)
                {
                    Debug.LogError("InstancedUIStream: container already has an in-place stream; disposing the old one.");
                    container._instancedStream.Dispose();
                }
#pragma warning disable 618
                if (container.mergedBatching)
                {
                    Debug.LogError("InstancedUIStream: container has mergedBatching enabled (deprecated); disabling it.");
                    container.mergedBatching = false;
                }
#pragma warning restore 618
                liveInPlaceCount++;
                container._instancedStream = this;
                sLiveStreams.Add(this);
                _structureDirty = true;
            }
            _vertexPath = useVertexPath;
            _shader = Shader.Find(_vertexPath ? "FairyGUI/InstancedUIAttribs" : "FairyGUI/InstancedUI");
            if (_vertexPath)
            {
                _clipRectArr = new Vector4[MaxVertexPathClips];
                _clipSoftArr = new Vector4[MaxVertexPathClips];
            }
        }

        /// <summary>
        /// The external window: a clip on the container itself, or on its parent
        /// (a ScrollPane's mask container). It does NOT scroll with the content, so
        /// it stays a uniform tested against the scrolled position; internal clips
        /// found during the walk go to the ClipBuffer instead.
        /// </summary>
        void ComputeExternalWindow()
        {
            _clipRect = new Vector4(-1e30f, -1e30f, 1e30f, 1e30f);
            _clipSoft = Vector4.zero;

            Container clipOwner = null;
            if (_container.clipRect != null)
                clipOwner = _container;
            else if (_container.parent != null && _container.parent.clipRect != null)
                clipOwner = _container.parent;
            if (clipOwner == null)
                return;

            Matrix4x4 m = clipOwner == _container
                ? Matrix4x4.identity
                : _container.cachedTransform.worldToLocalMatrix * clipOwner.cachedTransform.localToWorldMatrix;
            _clipRect = TransformClipRect((Rect)clipOwner.clipRect, m);
            if (clipOwner.clipSoftness != null)
            {
                //FairyGUI (left,top,right,bottom) in y-down space -> our
                //(minX,minY,maxX,maxY) with y negated: top becomes yMax
                Vector4 s = (Vector4)clipOwner.clipSoftness;
                _clipSoft = new Vector4(s.x, s.w, s.z, s.y);
            }
        }

        /// <summary>
        /// FairyGUI clip Rect (y-down local space) -> (xMin,yMin,xMax,yMax) AABB in
        /// stream-container-local space including drawOffset.
        /// </summary>
        Vector4 TransformClipRect(Rect r, Matrix4x4 m)
        {
            Vector2 c0 = m.MultiplyPoint3x4(new Vector3(r.xMin, -r.yMax, 0));
            Vector2 c1 = m.MultiplyPoint3x4(new Vector3(r.xMax, -r.yMax, 0));
            Vector2 c2 = m.MultiplyPoint3x4(new Vector3(r.xMin, -r.yMin, 0));
            Vector2 c3 = m.MultiplyPoint3x4(new Vector3(r.xMax, -r.yMin, 0));
            Vector2 bmin = Vector2.Min(Vector2.Min(c0, c1), Vector2.Min(c2, c3));
            Vector2 bmax = Vector2.Max(Vector2.Max(c0, c1), Vector2.Max(c2, c3));
            return new Vector4(bmin.x + _drawOffset.x, bmin.y + _drawOffset.y,
                bmax.x + _drawOffset.x, bmax.y + _drawOffset.y);
        }

        /// <summary>
        /// Full recompile: walk visible leaves, adjacency-sort them (segment count
        /// shrinks where draw order may legally change), reassemble quads, slice
        /// segments, upload the instance buffer.
        /// </summary>
        public void Extract()
        {
#if UNITY_2020_1_OR_NEWER
            using (sExtract.Auto())
#endif
            {
                extractCount++;
                //batch 3: same-shape recompiles transfer renderers in place
                //instead of the SetParent/SetActive round trip through the pool
                _prevSegments.Clear();
                _prevSegments.AddRange(_segments);
                _segments.Clear();
                _leaves.Clear();
                _leafLookup.Clear();
                _quads.Clear();
                _staging.Clear();
                _pending.Clear();
                _entries.Clear();
                _runBarriers.Clear();
                _clipEntries.Clear();
                _clipEntries.Add(ClipEntry.None);
                _skippedPairs = 0;
                _maskedSubtrees = 0;

                foreach (var t in _watchedTextures)
                    t.onSizeChanged -= _onWatchedTexture;
                _watchedTextures.Clear();

                _rootWorldToLocal = _container.cachedTransform.worldToLocalMatrix;
                _clipMeta.Clear();
                _clipMeta.Add(default);
                _slotIndices.Clear();
                for (int i = 0; i < MaxTransformSlots; i++)
                    _slotOwners[i] = null;
                slotOverflow = 0;
                _hasSlottedClips = false;
                if (_hotContainers.Count > 0)
                {
                    //hot containers that stopped moving (or died) age out
                    sHotScratch.Clear();
                    foreach (var kv in _hotContainers)
                        if (kv.Key.isDisposed || Time.frameCount - kv.Value > 3000)
                            sHotScratch.Add(kv.Key);
                    foreach (var c in sHotScratch)
                        _hotContainers.Remove(c);
                }

                //a stream root with a stencil mask cannot be expressed: claimed
                //leaves leave the native pass, so the mask would neither write
                //stencil against them nor clip the segments — claim nothing and
                //let the whole subtree render natively (wrong batching beats
                //wrong pixels; the claim diff below releases earlier claims)
                if (_inPlace && _container.mask != null)
                {
                    if (!_rootMaskWarned)
                    {
                        _rootMaskWarned = true;
                        Debug.LogWarning("InstancedUIStream: the stream root has a stencil mask; instanced rendering is suspended until the mask is removed.", _container.gameObject);
                    }
                }
                else
                {
                    //re-arm the warning: each suspension period warns once
                    //(Extract only runs on structure dirt, so no spam)
                    _rootMaskWarned = false;
                    ExtractContainer(_container, _rootWorldToLocal, 0, _container.grayed, 0);
                }

                if (_sortAdjacency)
                    AdjacencySorter.Sort(_entries);
                BuildSegments();

                if (_inPlace)
                {
                    //claim diff: leaves that left the stream resume native
                    //rendering; new leaves stop rendering natively
                    _claimScratch.Clear();
                    for (int i = 0; i < _leaves.Count; i++)
                        _claimScratch.Add(_leaves[i].graphics);
                    foreach (var g in _claimed)
                    {
                        //ownership check: another stream may have claimed this
                        //leaf since it left us (cross-root move, review M9)
                        if (!_claimScratch.Contains(g) && g._instancedBy == this)
                            g._ClearInstancedOwner();
                    }
                    foreach (var g in _claimScratch)
                    {
                        if (!_claimed.Contains(g))
                            g._SetInstancedOwner(this);
                    }
                    var tmp = _claimed;
                    _claimed = _claimScratch;
                    _claimScratch = tmp;
                }

                if (_quads.Count == 0)
                {
                    for (int i = 0; i < _prevSegments.Count; i++)
                        RecycleSegment(_prevSegments[i]);
                    _prevSegments.Clear();
                    return;
                }

                EnsureUploadCapacity(_quads.Count);
                _quads.CopyTo(_uploadArray);
                if (_vertexPath)
                {
                    //vertex-stream backend: instance data baked x4 into per-segment
                    //mesh vertices; no compute buffers anywhere
                    for (int i = 0; i < _quads.Count; i++)
                        QuadVertex.WriteQuad(_vertexUpload, i, in _uploadArray[i]);
                }
                else
                {
                    //capacity-grown, never shrunk: segments read only within
                    //_InstanceStart/Count, so a larger buffer is harmless
                    if (_buffer == null || _bufferCapacity < _quads.Count)
                    {
                        if (_buffer != null)
                            _buffer.Release();
                        _bufferCapacity = Mathf.NextPowerOfTwo(Mathf.Max(_quads.Count, 256));
                        _buffer = new ComputeBuffer(_bufferCapacity, QuadInstance.Stride, ComputeBufferType.Structured);
                    }
                    _buffer.SetData(_uploadArray, 0, 0, _quads.Count);

                    if (_clipUploadArray == null || _clipUploadArray.Length < _clipEntries.Count)
                        _clipUploadArray = new ClipEntry[Mathf.NextPowerOfTwo(Mathf.Max(_clipEntries.Count, 16))];
                    _clipEntries.CopyTo(_clipUploadArray);
                    if (_clipBuffer == null || _clipBufferCapacity < _clipEntries.Count)
                    {
                        if (_clipBuffer != null)
                            _clipBuffer.Release();
                        _clipBufferCapacity = Mathf.NextPowerOfTwo(Mathf.Max(_clipEntries.Count, 16));
                        _clipBuffer = new ComputeBuffer(_clipBufferCapacity, ClipEntry.Stride, ComputeBufferType.Structured);
                    }
                    _clipBuffer.SetData(_clipUploadArray, 0, 0, _clipEntries.Count);

                    int maxCount = 0;
                    foreach (var seg in _segments)
                        if (seg.count > maxCount) maxCount = seg.count;
                    EnsurePullMesh(maxCount);
                }

                //pass 1: transfer renderers where the texture layout matches by
                //index (z is index-derived, so the transform needs no touch)
                int transferable = Mathf.Min(_prevSegments.Count, _segments.Count);
                for (int i = 0; i < transferable; i++)
                {
                    Segment prev = _prevSegments[i];
                    Segment seg = _segments[i];
                    if (prev.go == null || !SameTextureSet(prev, seg))
                        continue;
                    seg.go = prev.go;
                    seg.filter = prev.filter;
                    seg.renderer = prev.renderer;
                    seg.mesh = prev.mesh;
                    seg.props = prev.props;
                    seg.meshQuadCap = prev.meshQuadCap;
                    seg.lastSortingOrder = prev.lastSortingOrder;
                    seg.lastLayer = prev.lastLayer;
                    prev.go = null;
                    prev.filter = null;
                    prev.renderer = null;
                    prev.mesh = null;
                    prev.props = null;
                }
                //pass 2: unconsumed old segments feed the pools before new claims
                for (int i = 0; i < _prevSegments.Count; i++)
                    RecycleSegment(_prevSegments[i]);
                _prevSegments.Clear();

                //pass 3: resolve materials, claim what did not transfer, upload
                foreach (var seg in _segments)
                {
                    Material mat;
                    var texKey = new TexSetKey { t0 = seg.textures[0], t1 = seg.textures[1], t2 = seg.textures[2], t3 = seg.textures[3] };
                    if (!_materialCache.TryGetValue(texKey, out mat))
                    {
                        mat = new Material(_shader);
                        mat.hideFlags = HideFlags.DontSave;
                        mat.mainTexture = seg.textures[0];
                        if (seg.textures[1] != null) mat.SetTexture("_Tex1", seg.textures[1]);
                        if (seg.textures[2] != null) mat.SetTexture("_Tex2", seg.textures[2]);
                        if (seg.textures[3] != null) mat.SetTexture("_Tex3", seg.textures[3]);
                        _materialCache.Add(texKey, mat);
                    }
                    seg.material = mat;
                    if (seg.go == null)
                    {
                        ClaimSegmentRenderer(seg);
                        if (_mpbPool.Count > 0)
                        {
                            seg.props = _mpbPool[_mpbPool.Count - 1];
                            _mpbPool.RemoveAt(_mpbPool.Count - 1);
                        }
                        else
                            seg.props = new MaterialPropertyBlock();
                    }
                    else if (seg.renderer.sharedMaterial != mat)
                        seg.renderer.sharedMaterial = mat;

                    if (_vertexPath)
                    {
                        UploadSegmentMesh(seg);
                    }
                    else
                    {
                        seg.props.SetBuffer("_Instances", _buffer);
                        seg.props.SetBuffer("_Clips", _clipBuffer);
                        seg.props.SetInt("_InstanceStart", seg.start);
                        seg.props.SetInt("_InstanceCount", seg.count);
                    }
                    seg.renderer.SetPropertyBlock(seg.props);
                }

                //fresh property blocks need the shared uniforms pushed once; run
                //orders and curve-font bindings re-evaluate after a recompile
                RecomputeSlotMatrices();
                _slotsDirty = false;
                _propsDirty = true;
                _lastRunOrderProbe = int.MinValue;
            }
        }

        void EnsureUploadCapacity(int quadCount)
        {
            if (_uploadArray == null || _uploadArray.Length < quadCount)
                _uploadArray = new QuadInstance[Mathf.NextPowerOfTwo(Mathf.Max(quadCount, 256))];
            if (_vertexPath && (_vertexUpload == null || _vertexUpload.Length < quadCount * 4))
                _vertexUpload = new QuadVertex[Mathf.NextPowerOfTwo(Mathf.Max(quadCount, 256)) * 4];
        }

        void RecycleSegment(Segment seg)
        {
            ReleaseSegmentRenderer(seg);
            if (seg.props != null)
            {
                seg.props.Clear();
                _mpbPool.Add(seg.props);
                seg.props = null;
            }
            for (int i = 0; i < MaxSegmentTextures; i++)
                seg.textures[i] = null;
            seg.texCount = 0;
            seg.material = null;
            _segmentObjPool.Add(seg);
        }

        Segment TakeSegment()
        {
            if (_segmentObjPool.Count == 0)
                return new Segment();
            Segment seg = _segmentObjPool[_segmentObjPool.Count - 1];
            _segmentObjPool.RemoveAt(_segmentObjPool.Count - 1);
            seg.meshQuadCap = -1;
            seg.lastSortingOrder = int.MinValue;
            seg.lastLayer = -1;
            seg.dirtyMin = int.MaxValue;
            seg.dirtyMax = -1;
            return seg;
        }

        static int IndexOfOrAddTexture(Segment seg, Texture tex)
        {
            for (int i = 0; i < seg.texCount; i++)
                if (seg.textures[i] == tex)
                    return i;
            if (seg.texCount >= MaxSegmentTextures)
                return -1;
            seg.textures[seg.texCount] = tex;
            return seg.texCount++;
        }

        static bool SameTextureSet(Segment a, Segment b)
        {
            if (a.texCount != b.texCount)
                return false;
            for (int i = 0; i < a.texCount; i++)
                if (a.textures[i] != b.textures[i])
                    return false;
            return true;
        }

        const MeshUpdateFlags kNoMeshChecks = MeshUpdateFlags.DontRecalculateBounds
            | MeshUpdateFlags.DontValidateIndices
            | MeshUpdateFlags.DontNotifyMeshUsers
            | MeshUpdateFlags.DontResetBoneBounds;

        /// <summary>
        /// Vertex path: (re)build one segment's mesh from its slice of the baked
        /// vertex array. Exact-size buffers — no pull-mesh capacity padding.
        /// </summary>
        void UploadSegmentMesh(Segment seg)
        {
            Mesh mesh = seg.mesh;
            if (seg.meshQuadCap == seg.count)
            {
                //same layout: params, index buffer and submesh already fit —
                //only the vertex payload changes
                mesh.SetVertexBufferData(_vertexUpload, seg.start * 4, 0, seg.count * 4, 0, kNoMeshChecks);
                return;
            }
            mesh.SetVertexBufferParams(seg.count * 4, QuadVertex.Layout);
            mesh.SetVertexBufferData(_vertexUpload, seg.start * 4, 0, seg.count * 4, 0, kNoMeshChecks);
            EnsureIndexCache(seg.count);
            mesh.SetIndexBufferParams(seg.count * 6, IndexFormat.UInt32);
            mesh.SetIndexBufferData(sIndexCache, 0, 0, seg.count * 6, kNoMeshChecks);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, seg.count * 6), kNoMeshChecks);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
            seg.meshQuadCap = seg.count;
        }

        /// <summary>
        /// The shared pull mesh: capacity quads of dummy vertices whose only job is
        /// providing SV_VertexID topology (4 verts / 6 indices per quad); positions
        /// come from the instance buffer in the vertex shader. Quads beyond a
        /// segment's _InstanceCount collapse to degenerate triangles.
        /// </summary>
        void EnsurePullMesh(int quadCount)
        {
            if (_pullMesh != null && _pullCapacity >= quadCount)
                return;
            int capacity = Mathf.Max(_pullCapacity != 0 ? _pullCapacity : 512, 1);
            while (capacity < quadCount)
                capacity *= 2;

            if (_pullMesh == null)
            {
                _pullMesh = new Mesh();
                _pullMesh.name = "InstancedUIPull";
                _pullMesh.hideFlags = HideFlags.DontSave;
            }
            _pullMesh.Clear();
            _pullMesh.indexFormat = capacity * 4 > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            var verts = new Vector3[capacity * 4];
            var tris = new int[capacity * 6];
            for (int q = 0; q < capacity; q++)
            {
                int v = q * 4, t = q * 6;
                tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v + 2; tris[t + 4] = v + 1; tris[t + 5] = v + 3;
            }
            _pullMesh.vertices = verts;
            _pullMesh.triangles = tris;
            _pullMesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
            _pullMesh.UploadMeshData(false);
            _pullCapacity = capacity;
        }

        void ClaimSegmentRenderer(Segment seg)
        {
            GameObject go;
            if (_segmentPool.Count > 0)
            {
                go = _segmentPool[_segmentPool.Count - 1];
                _segmentPool.RemoveAt(_segmentPool.Count - 1);
            }
            else
            {
                go = new GameObject("InstancedUISegment");
                go.hideFlags = DisplayObject.hideFlags;
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            }
            seg.go = go;
            seg.filter = go.GetComponent<MeshFilter>();
            seg.renderer = go.GetComponent<MeshRenderer>();
            if (_vertexPath)
            {
                if (_meshPool.Count > 0)
                {
                    seg.mesh = _meshPool[_meshPool.Count - 1];
                    _meshPool.RemoveAt(_meshPool.Count - 1);
                }
                else
                {
                    seg.mesh = new Mesh();
                    seg.mesh.name = "InstancedUISegMesh";
                    seg.mesh.hideFlags = HideFlags.DontSave;
                    seg.mesh.MarkDynamic();
                }
                seg.filter.sharedMesh = seg.mesh;
                seg.meshQuadCap = -1; //pooled mesh: layout unknown, force full upload
            }
            else
                seg.filter.sharedMesh = _pullMesh;
            seg.renderer.sharedMaterial = seg.material;
            seg.lastSortingOrder = int.MinValue;
            seg.lastLayer = -1;
            var t = go.transform;
            t.SetParent(_container.cachedTransform, false);
            //the z step lives on the TRANSFORM: same-sortingOrder renderers are
            //depth-sorted by their transform position, which shader-side offsets
            //cannot influence — and the shader picks it up via unity_ObjectToWorld
            t.localPosition = new Vector3(0, 0, seg.z);
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            go.SetActive(true);
        }

        void ReleaseSegmentRenderer(Segment seg)
        {
            if (seg.go == null)
                return;
            if (seg.mesh != null)
            {
                _meshPool.Add(seg.mesh);
                seg.mesh = null;
            }
            seg.go.SetActive(false);
            seg.go.transform.SetParent(null, false);
            _segmentPool.Add(seg.go);
            seg.go = null;
            seg.filter = null;
            seg.renderer = null;
        }

        void ExtractContainer(Container container, Matrix4x4 worldToLocal, uint clipIndex, bool grayed, uint slotIndex)
        {
            int cnt = container.numChildren;
            for (int i = 0; i < cnt; i++)
            {
                DisplayObject child = container.GetChildAt(i);
                if (!child.visible)
                    continue;
                //grayed inherits down, mirroring UpdateContext.grayed accumulation
                bool childGrayed = grayed || child.grayed;

                //painting scopes (filter/blend/perspective/cacheAsBitmap) render
                //through their own capture pipeline — leave them native (review
                //M12), but as a sort BARRIER: the blit quad interleaves with the
                //stream in the native sorting space (design §9)
                if (child._paintingMode > 0)
                {
                    _maskedSubtrees++;
                    AddScopeBarrier(child, child.paintingGraphics);
                    continue;
                }

                //GoWrapper renders through self-managed external renderers and
                //has no graphics: without a barrier its 3D content could sort
                //anywhere relative to the stream's segments
                if (child is GoWrapper)
                {
                    _maskedSubtrees++;
                    AddScopeBarrier(child, null);
                    continue;
                }

                if (child.graphics != null && child.graphics.texture != null)
                    ExtractLeaf(child.graphics, worldToLocal, clipIndex, childGrayed, slotIndex);

                if (child.graphics != null && child.graphics.subInstances != null)
                {
                    foreach (var sub in child.graphics.subInstances)
                        if (sub.texture != null)
                            ExtractLeaf(sub, worldToLocal, clipIndex, childGrayed, slotIndex);
                }

                if (child is Container c)
                {
                    //stencil-masked scopes go to the fallback renderer, and the
                    //subtree's native block needs a barrier so overlapping stream
                    //content cannot sort across it (design §9)
                    if (c.mask != null)
                    {
                        _maskedSubtrees++;
                        AddScopeBarrier(c, null);
                        continue;
                    }
                    //M8-2: a valid baked mount splices its precompiled stream
                    //instead of walking the subtree (in-place streams only —
                    //the baker's replica must always walk real content)
                    if (_inPlace && c._fqsPending != null)
                        FqsAutoMount._Realize(c); //bind against the tree we are walking now
                    if (_inPlace && c._fqsMount != null)
                    {
                        FqsMount fm = c._fqsMount;
                        if (!fm.invalid && SpliceMount(c, fm, clipIndex, childGrayed))
                            continue;
                        fm.invalid = true; //failed splice: stop retrying every extract
                    }
                    //slot promotion BEFORE the clip push: a moving clip owner's
                    //window must ride its own slot
                    uint childSlot = slotIndex;
                    Matrix4x4 childW2L = worldToLocal;
                    if (_inPlace && _hotContainers.ContainsKey(c))
                        TryPromoteSlot(c, ref childSlot, ref childW2L);
                    uint childClip = clipIndex;
                    if (c.clipRect != null)
                        childClip = PushClip(c, clipIndex, childSlot);
                    ExtractContainer(c, childW2L, childClip, childGrayed, childSlot);
                }
            }
        }

        /// <summary>
        /// Emits a sort barrier for a container-level fallback scope (stencil
        /// mask / painting capture / GoWrapper): the subtree keeps its native
        /// renderers, and a null-key entry with an INFINITE box keeps ALL
        /// stream content from sorting across it — the same absolute-barrier
        /// semantics native fairyBatching gives breakBatch elements. A tight
        /// AABB would merge better, but every tight bound goes stale with no
        /// invalidation channel to catch it (mask tween, wrapped-content
        /// animation, filter extend growth — adversarial review round 2), and
        /// the native batcher never merged across these scopes either.
        /// The scope closes a run in BuildSegments, so the neighboring runs'
        /// sortingOrders straddle its native order block (see RunBarrier.order).
        /// </summary>
        void AddScopeBarrier(DisplayObject scope, NGraphics blit)
        {
            _entries.Add(new AdjacencyEntry
            {
                key = null,
                x0 = -1e30f, y0 = -1e30f, x1 = 1e30f, y1 = 1e30f,
                payload = _pending.Count
            });
            _pending.Add(new PendingLeaf { graphics = blit, scope = scope, instanceable = false });
        }

        /// <summary>
        /// Assigns (or reuses) a transform slot for a hot container: its subtree
        /// bakes in the container's own local space from here on.
        /// </summary>
        void TryPromoteSlot(Container c, ref uint slotIndex, ref Matrix4x4 worldToLocal)
        {
            int idx;
            if (!_slotIndices.TryGetValue(c, out idx))
            {
                idx = -1;
                for (int i = 1; i < MaxTransformSlots; i++)
                {
                    if (_slotOwners[i] == null)
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx < 0)
                {
                    slotOverflow++;
                    return; //no slot left: stays recompile-on-move
                }
                _slotIndices[c] = idx;
                _slotOwners[idx] = c;
            }
            slotIndex = (uint)idx;
            worldToLocal = c.cachedTransform.worldToLocalMatrix;
        }

        /// <summary>
        /// M8-2: allocates a transform slot for a mounted container regardless
        /// of hot state — baked quads live in mount-local space, so the slot IS
        /// the placement (and mount movement becomes tier-1 for free).
        /// </summary>
        int AllocMountSlot(Container c)
        {
            if (_slotIndices.TryGetValue(c, out int idx))
                return idx;
            for (int i = 1; i < MaxTransformSlots; i++)
            {
                if (_slotOwners[i] == null)
                {
                    _slotIndices[c] = i;
                    _slotOwners[i] = c;
                    return i;
                }
            }
            slotOverflow++;
            return -1;
        }

        /// <summary>
        /// M8-2: splices a mounted blob into the walk — quads copy with clip
        /// remap/slot stamp/grayed OR, blob clip owners re-enter the ordinary
        /// PushClip fold (live rects, slot-riding), and the whole mount becomes
        /// ONE adjacency entry (unique key: internal order is frozen).
        /// Returns false to fall back to the runtime walk.
        /// </summary>
        bool SpliceMount(Container c, FqsMount fm, uint parentClip, bool grayed)
        {
            int slot = AllocMountSlot(c);
            if (slot < 0)
                return false; //no slot left: runtime walk still renders correctly

            uint rootClip = parentClip;
            if (c.clipRect != null)
                rootClip = PushClip(c, parentClip, (uint)slot);

            var clipMap = new uint[fm.data.clips.Length];
            clipMap[0] = rootClip;
            for (int i = 1; i < fm.data.clips.Length; i++)
            {
                Container owner = fm.clipOwners[i];
                if (owner == null || owner.isDisposed || owner.clipRect == null)
                    return false;
                clipMap[i] = PushClip(owner, clipMap[fm.data.clips[i].parentIndex], (uint)slot);
            }

            int stageStart = _staging.Count;
            var quads = fm.data.quads;
            for (int i = 0; i < quads.Length; i++)
            {
                QuadInstance q = quads[i];
                q.transformIndex = (uint)slot;
                q.clipIndex = clipMap[q.clipIndex];
                if (grayed)
                    q.flags |= QuadInstance.FlagGrayed;
                _staging.Add(q);
            }

            //root-space AABB of the mount for the adjacency sort
            Matrix4x4 m = _rootWorldToLocal * c.cachedTransform.localToWorldMatrix;
            Vector4 lb = fm.localBounds;
            Vector2 c0 = m.MultiplyPoint3x4(new Vector3(lb.x, lb.y, 0));
            Vector2 c1 = m.MultiplyPoint3x4(new Vector3(lb.z, lb.y, 0));
            Vector2 c2 = m.MultiplyPoint3x4(new Vector3(lb.x, lb.w, 0));
            Vector2 c3 = m.MultiplyPoint3x4(new Vector3(lb.z, lb.w, 0));
            Vector2 bmin = Vector2.Min(Vector2.Min(c0, c1), Vector2.Min(c2, c3));
            Vector2 bmax = Vector2.Max(Vector2.Max(c0, c1), Vector2.Max(c2, c3));

            _entries.Add(new AdjacencyEntry
            {
                key = fm, //unique: never merges with other entries
                x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                payload = _pending.Count
            });
            _pending.Add(new PendingLeaf
            {
                mount = fm,
                mountClipMap = clipMap,
                stageStart = stageStart,
                stageCount = quads.Length,
                instanceable = true,
                slotIndex = (uint)slot,
                clipIndex = rootClip,
                flags = grayed ? QuadInstance.FlagGrayed : 0u,
                bakeAlpha = 1f,
            });
            return true;
        }

        /// <summary>
        /// Slot matrices map slot-local quads back to CURRENT stream-root space:
        /// M = root.worldToLocal x owner.localToWorld, exact at bake time and
        /// re-derived from the live transforms whenever a slot moves.
        /// </summary>
        void RecomputeSlotMatrices()
        {
            Matrix4x4 rootW2L = _container.cachedTransform.worldToLocalMatrix;
            _slotMatrixArr[0] = Matrix4x4.identity;
            for (int i = 1; i < MaxTransformSlots; i++)
            {
                Container owner = _slotOwners[i];
                _slotMatrixArr[i] = owner == null || owner.isDisposed
                    ? Matrix4x4.identity
                    : rootW2L * owner.cachedTransform.localToWorldMatrix;
            }
        }

        /// <summary>
        /// Re-derives the root-space rect of every slot-riding internal clip from
        /// its owner's CURRENT transform (rotation degrades to the AABB exactly
        /// like Extract does), refolding with the parent — parents are always
        /// registered before children, so one in-order pass suffices.
        /// </summary>
        void RecomputeSlottedClips()
        {
            Matrix4x4 rootW2L = _container.cachedTransform.worldToLocalMatrix;
            for (int i = 1; i < _clipEntries.Count && i < _clipMeta.Count; i++)
            {
                ClipMeta meta = _clipMeta[i];
                if (meta.slotIndex == 0 || meta.owner == null || meta.owner.isDisposed)
                    continue;
                Matrix4x4 m = rootW2L * meta.owner.cachedTransform.localToWorldMatrix;
                Vector4 rect = TransformClipRect(meta.rect, m);
                if (meta.parentIndex != 0)
                {
                    Vector4 pr = _clipEntries[(int)meta.parentIndex].rect;
                    rect = new Vector4(Mathf.Max(rect.x, pr.x), Mathf.Max(rect.y, pr.y),
                        Mathf.Min(rect.z, pr.z), Mathf.Min(rect.w, pr.w));
                }
                ClipEntry e = _clipEntries[i];
                e.rect = rect;
                _clipEntries[i] = e;
            }
        }

        /// <summary>
        /// Registers an internal clip region: the container's clipRect transformed to
        /// stream-local space, folded (intersected) with the enclosing region — same
        /// semantics as UpdateContext.EnterClipping. Identical regions are deduped.
        /// </summary>
        uint PushClip(Container c, uint parentIndex, uint slotIndex)
        {
            //the ENTRY rect is always root-space (the shader's clip test runs on
            //slot-transformed positions); at extract time the slot matrix is the
            //live transform, so the root-relative matrix bakes the same value
            Rect clipRect = (Rect)c.clipRect;
            Matrix4x4 m = _rootWorldToLocal * c.cachedTransform.localToWorldMatrix;
            Vector4 rect = TransformClipRect(clipRect, m);

            if (parentIndex != 0)
            {
                Vector4 p = _clipEntries[(int)parentIndex].rect;
                rect = new Vector4(Mathf.Max(rect.x, p.x), Mathf.Max(rect.y, p.y),
                    Mathf.Min(rect.z, p.z), Mathf.Min(rect.w, p.w));
            }

            Vector4 soft = Vector4.zero;
            if (c.clipSoftness != null)
            {
                Vector4 s = (Vector4)c.clipSoftness;
                soft = new Vector4(s.x, s.w, s.z, s.y);
            }

            for (int i = 1; i < _clipEntries.Count; i++)
            {
                //slot-riding entries only dedup within the same slot: same slot
                //means the merged owners move together (diverging owners fire the
                //structure channel and recompile)
                if (_clipEntries[i].rect == rect && _clipEntries[i].soft == soft
                    && _clipMeta[i].slotIndex == slotIndex)
                    return (uint)i;
            }

            //vertex path: internal clips live in a fixed uniform array; on overflow
            //reuse the enclosing region (correct but coarser: the child clips only
            //by its parent window) and warn once
            if (_vertexPath && _clipEntries.Count >= MaxVertexPathClips)
            {
                if (!_clipOverflowWarned)
                {
                    _clipOverflowWarned = true;
                    Debug.LogWarning($"InstancedUIStream: more than {MaxVertexPathClips - 1} internal clip regions on the vertex-stream backend; extra regions clip by their parent window.");
                }
                return parentIndex;
            }

            _clipEntries.Add(new ClipEntry { rect = rect, soft = soft });
            _clipMeta.Add(new ClipMeta { owner = c, rect = clipRect, parentIndex = parentIndex, slotIndex = slotIndex });
            if (slotIndex != 0)
                _hasSlottedClips = true;
            return (uint)(_clipEntries.Count - 1);
        }

        void ExtractLeaf(NGraphics graphics, Matrix4x4 worldToLocal, uint clipIndex, bool grayed, uint slotIndex)
        {
            //M8-5: deferred (renderless) leaves build their mesh on first read
            if (graphics._renderless)
                graphics._EnsureMeshBuilt();
            Mesh mesh = graphics.mesh;
            if (mesh == null || mesh.vertexCount == 0)
                return;
            //enabled is an admission condition (review M10/M14); its setter pushes
            if (graphics.meshRenderer != null && !graphics.meshRenderer.enabled)
                return;
            //color tier (batch 3): deferred alpha/tint must land in the mesh
            //before the quads are staged from it
            graphics._RestoreNativeColors();
            Texture tex = graphics.texture.nativeTexture;
            if (tex == null)
                return;

            if (_inPlace && _watchedTextures.Add(graphics.texture))
                graphics.texture.onSizeChanged += _onWatchedTexture;

            bool alphaTex = graphics.shader == ShaderConfig.textShader;
            uint flags = alphaTex ? QuadInstance.FlagAlphaTexture : 0u;
            if (grayed)
                flags |= QuadInstance.FlagGrayed;
            Matrix4x4 m = worldToLocal * graphics.gameObject.transform.localToWorldMatrix;

            //stream-local AABB for the overlap test (transform the mesh AABB's
            //4 xy corners so rotated leaves stay conservative), in the same
            //offset space as the clip entries — for slot-baked leaves the quads
            //are slot-local but the SORT still runs in root space
            Matrix4x4 mb2root = slotIndex == 0 ? m
                : _rootWorldToLocal * graphics.gameObject.transform.localToWorldMatrix;
            Bounds mb = mesh.bounds;
            Vector2 c0 = mb2root.MultiplyPoint3x4(new Vector3(mb.min.x, mb.min.y, 0));
            Vector2 c1 = mb2root.MultiplyPoint3x4(new Vector3(mb.max.x, mb.min.y, 0));
            Vector2 c2 = mb2root.MultiplyPoint3x4(new Vector3(mb.min.x, mb.max.y, 0));
            Vector2 c3 = mb2root.MultiplyPoint3x4(new Vector3(mb.max.x, mb.max.y, 0));
            Vector2 bmin = Vector2.Min(Vector2.Min(c0, c1), Vector2.Min(c2, c3)) + _drawOffset;
            Vector2 bmax = Vector2.Max(Vector2.Max(c0, c1), Vector2.Max(c2, c3)) + _drawOffset;

            //clamp sort bounds to the clip window: parts a clip discards cannot
            //cause visual overlap, so they should not block segment merging either
            if (clipIndex != 0)
            {
                Vector4 cr = _clipEntries[(int)clipIndex].rect;
                bmin = Vector2.Max(bmin, new Vector2(cr.x, cr.y));
                bmax = Vector2.Min(bmax, new Vector2(cr.z, cr.w));
            }

            //stage the quads NOW (mesh is hot); non-quad topology becomes a
            //fallback barrier: it keeps its native renderer, and a null sort key
            //makes it immovable — others may still sort past it when they do not
            //overlap, exactly the legality rule native FairyBatching uses
            var p = new PendingLeaf { graphics = graphics, texture = tex, flags = flags, clipIndex = clipIndex, stageStart = _staging.Count, bakeAlpha = graphics._currentAlpha, slotIndex = slotIndex };

            //non-Normal blend modes cannot join the stream (its blend state is
            //fixed): keep the native renderer, act as a sort barrier (review batch 1)
            if (graphics.blendMode != BlendMode.Normal)
            {
                p.stageCount = 0;
                p.instanceable = false;
                _entries.Add(new AdjacencyEntry
                {
                    key = null,
                    x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                    payload = _pending.Count
                });
                _pending.Add(p);
                return;
            }

            //shader keywords (COLOR_FILTER on Image/MovieClip, TMP effects) and
            //custom materials live on the native material/property block, which
            //an instanced quad cannot carry — same native-renderer barrier as
            //blend (ColorFilter audit; the filter pokes the structure channel
            //so an already-claimed leaf gets released on the next extract)
            if (graphics._hasKeywordOrCustomMaterial)
            {
                p.stageCount = 0;
                p.instanceable = false;
                _entries.Add(new AdjacencyEntry
                {
                    key = null,
                    x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                    payload = _pending.Count
                });
                _pending.Add(p);
                return;
            }

            //M9b/batch 5: curve-text leaves (standalone CurveTextMesh or a
            //TextField on a CurveBaseFont) emit one analytic glyph quad per
            //character. Where the stream cannot express them — vertex-stream
            //backend, rotated leaves — the ENCODED native mesh must not be
            //reassembled into the stream: the leaf keeps its native renderer
            //(FairyGUI/CurveText) and becomes a sort barrier.
            bool curveLeaf = (graphics.meshFactory is CurveTextMesh cmProbe && cmProbe.glyphQuads.Count > 0)
                || (graphics._curveGlyphs != null && graphics._curveGlyphs.Count > 0);
            //batch 5b: outline/shadow live in the leaf's property block, which
            //an instanced quad cannot carry — same native-renderer barrier as
            //rotation (bold is fine: it rides padding bit 20)
            if (curveLeaf && (Mathf.Abs(m.m01) > 1e-4f || Mathf.Abs(m.m10) > 1e-4f
                || graphics._curveFxActive))
            {
                p.stageCount = 0;
                p.instanceable = false;
                _entries.Add(new AdjacencyEntry
                {
                    key = null,
                    x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                    payload = _pending.Count
                });
                _pending.Add(p);
                return;
            }
            int curveCount = EmitCurveQuads(graphics, m, _staging, flags & QuadInstance.FlagGrayed);
            if (curveCount > 0)
            {
                //glyph-count slack, same as alpha-texture text (batch 2): pad to
                //the next power of two with degenerate quads so length changes
                //stay on the tier-2 path
                int curveSlack = Mathf.NextPowerOfTwo(curveCount);
                for (int k = curveCount; k < curveSlack; k++)
                    _staging.Add(default);
                p.stageCount = curveSlack;
                p.instanceable = true;
                p.curve = true;
                StampIndices(_staging, p.stageStart, p.stageCount, clipIndex, slotIndex);
                _entries.Add(new AdjacencyEntry
                {
                    key = tex,
                    x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                    payload = _pending.Count
                });
                _pending.Add(p);
                return;
            }

            //M7: rounded/stroked rect and circle shapes bypass their triangulated
            //mesh entirely — analytic SDF coverage from 1-2 quads
            int sdfCount = EmitSdfQuads(graphics, m, _staging, flags & QuadInstance.FlagGrayed);
            if (sdfCount > 0)
            {
                p.stageCount = sdfCount;
                p.instanceable = true;
                p.sdf = true;
                StampIndices(_staging, p.stageStart, p.stageCount, clipIndex, slotIndex);
            }
            else
            {
                mesh.GetVertices(sVerts);
                mesh.GetUVs(0, sUVs);
                mesh.GetColors(sColors);
                mesh.GetTriangles(sTris, 0);
                int skipped;
                p.stageCount = QuadReassembler.Append(_staging, sVerts, sUVs, sColors, sTris, m, _drawOffset, flags, out skipped);
                if (skipped > 0)
                {
                    _skippedPairs += skipped;
                    _staging.RemoveRange(p.stageStart, p.stageCount);
                    p.stageCount = 0;
                    p.instanceable = false;
                }
                else
                {
                    //text slack (batch 2): glyph counts jitter ('9'->'10'), so
                    //text leaves round their range up to a power of two and pad
                    //with degenerate quads (zero size renders nothing) — length
                    //changes within the slack stay on the microsecond tier-2
                    //path instead of forcing a full recompile
                    if (alphaTex && p.stageCount > 0)
                    {
                        int slack = Mathf.NextPowerOfTwo(p.stageCount);
                        for (int k = p.stageCount; k < slack; k++)
                            _staging.Add(default);
                        p.stageCount = slack;
                    }
                    p.instanceable = true;
                    StampIndices(_staging, p.stageStart, p.stageCount, clipIndex, slotIndex);
                }
            }

            _entries.Add(new AdjacencyEntry
            {
                key = p.instanceable ? tex : null,
                x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                payload = _pending.Count
            });
            _pending.Add(p);
        }

        /// <summary>
        /// Consumes the sorted entry list: instanceable leaves append their staged
        /// quads into the final stream (segmenting on texture change), fallback
        /// barriers close the current segment and delimit a RUN — segments of one
        /// run share a sortingOrder slot below the barrier's own order, so native
        /// fallback renderers interleave correctly between runs.
        /// </summary>
        /// <summary>
        /// M7: shapes whose silhouette is an analytic SDF skip mesh reassembly —
        /// a rounded/stroked rect is 1-2 quads (fill inset by the border width,
        /// border band [edge-width, edge], per RoundedRectMesh's geometry), a full
        /// circle is a rounded rect with maxed radii. Returns emitted quad count,
        /// 0 when the factory is not SDF-expressible (gradient ellipses, pies,
        /// rotated/non-uniformly-scaled leaves fall back to the mesh path).
        /// </summary>
        int EmitSdfQuads(NGraphics graphics, Matrix4x4 m, List<QuadInstance> dst, uint extraFlags)
        {
            float rBL, rBR, rTL, rTR, lineWidth;
            Color32 lineColor;
            Color fill;
            Rect local;

            if (graphics.meshFactory is RoundedRectMesh rrm)
            {
                local = rrm.drawRect != null ? (Rect)rrm.drawRect : graphics.contentRect;
                fill = rrm.fillColor != null ? (Color)(Color32)rrm.fillColor : graphics._tintColor;
                lineWidth = rrm.lineWidth;
                lineColor = rrm.lineColor;
                //corner names map directly: FairyGUI's visual top-left is the
                //(minX, maxY) quadrant of our y-negated quad-local space
                float rMax = Mathf.Min(local.width, local.height) * 0.5f;
                rTL = Mathf.Min(rrm.topLeftRadius, rMax);
                rTR = Mathf.Min(rrm.topRightRadius, rMax);
                rBL = Mathf.Min(rrm.bottomLeftRadius, rMax);
                rBR = Mathf.Min(rrm.bottomRightRadius, rMax);
            }
            else if (graphics.meshFactory is EllipseMesh el)
            {
                local = el.drawRect != null ? (Rect)el.drawRect : graphics.contentRect;
                if (el.centerColor != null || el.startDegree != 0 || el.endDegreee != 360
                    || Mathf.Abs(local.width - local.height) > 0.01f)
                    return 0; //gradients, pies and true ellipses stay on the mesh path
                fill = el.fillColor != null ? (Color)(Color32)el.fillColor : graphics._tintColor;
                lineWidth = el.lineWidth;
                lineColor = el.lineColor;
                rBL = rBR = rTL = rTR = local.width * 0.5f;
            }
            else
                return 0;

            //transform to stream-local; the SDF encodes an axis-aligned rect, so a
            //rotated leaf falls back; non-uniform scale would need elliptical
            //corners — fall back there too (native triangulation stays exact)
            Vector2 p0 = m.MultiplyPoint3x4(new Vector3(local.xMin, -local.yMax, 0));
            Vector2 p1 = m.MultiplyPoint3x4(new Vector3(local.xMax, -local.yMin, 0));
            if (Mathf.Abs(m.m01) > 1e-4f || Mathf.Abs(m.m10) > 1e-4f)
                return 0;
            Vector2 bmin = Vector2.Min(p0, p1) + _drawOffset;
            Vector2 size = Vector2.Max(p0, p1) - Vector2.Min(p0, p1);
            if (size.x <= 0 || size.y <= 0)
                return 0;
            float scaleX = local.width > 0 ? size.x / local.width : 1;
            float scaleY = local.height > 0 ? size.y / local.height : 1;
            if (Mathf.Abs(scaleX - scaleY) > 0.01f)
                return 0;
            float s = scaleX;
            if (Mathf.Max(Mathf.Max(rBL, rBR), Mathf.Max(rTL, rTR)) * s > 255 || lineWidth * s > 255)
                return 0; //beyond the packed byte range: keep native

            float alpha = graphics._currentAlpha;
            uint radii = QuadInstance.PackRadii(rBL * s, rBR * s, rTL * s, rTR * s);
            var rect = new Vector4(bmin.x, bmin.y, size.x, size.y);
            uint wBits;

            //fill is always emitted (quad count stays stable under tint changes);
            //the border quad appears only when a line width exists
            Color fc = fill;
            fc.a *= alpha;
            wBits = QuadInstance.PackBorderWidth(QuadInstance.FlagSdfFill | extraFlags, lineWidth * s);
            dst.Add(new QuadInstance { rect = rect, color = fc, flags = wBits, padding = radii });
            if (lineWidth > 0)
            {
                Color lc = lineColor;
                lc.a *= alpha;
                wBits = QuadInstance.PackBorderWidth(QuadInstance.FlagSdfBorder | extraFlags, lineWidth * s);
                dst.Add(new QuadInstance { rect = rect, color = lc, flags = wBits, padding = radii });
                return 2;
            }
            return 1;
        }

        /// <summary>
        /// M9b: CurveTextMesh leaves emit one quad per glyph; coverage comes from
        /// the quadratic outlines in CurveFontStore. The corner-UV channel carries
        /// the glyph-space mapping, padding carries the glyph index. Vertex-stream
        /// backend and rotated leaves keep the native ghost fallback for now.
        /// </summary>
        int EmitCurveQuads(NGraphics graphics, Matrix4x4 m, List<QuadInstance> dst, uint extraFlags)
        {
            IReadOnlyList<CurveTextMesh.GlyphQuad> quads;
            if (graphics.meshFactory is CurveTextMesh ctm)
                quads = ctm.glyphQuads;
            else if (graphics._curveGlyphs != null)
                quads = graphics._curveGlyphs; //batch 5: CurveBaseFont side table
            else
                return 0;
            if (quads.Count == 0)
                return 0;
            if (Mathf.Abs(m.m01) > 1e-4f || Mathf.Abs(m.m10) > 1e-4f)
                return 0;

            float alpha = graphics._currentAlpha;
            int n = 0;
            for (int qi = 0; qi < quads.Count; qi++)
            {
                CurveTextMesh.GlyphQuad gq = quads[qi];
                Vector2 pa = m.MultiplyPoint3x4(new Vector3(gq.rect.xMin, -gq.rect.yMax, 0));
                Vector2 pb = m.MultiplyPoint3x4(new Vector3(gq.rect.xMax, -gq.rect.yMin, 0));
                Vector2 mn = Vector2.Min(pa, pb) + _drawOffset;
                Vector2 sz = Vector2.Max(pa, pb) - Vector2.Min(pa, pb);
                Color col = gq.color;
                col.a *= alpha;
                if (gq.glyphIndex < 0)
                {
                    //solid rect (underline/strikethrough): plain quad sampling
                    //the leaf texture's center (Empty/white for curve fonts)
                    Rect uvr = graphics.texture.uvRect;
                    var cuv = new Vector4((uvr.xMin + uvr.xMax) * 0.5f, (uvr.yMin + uvr.yMax) * 0.5f,
                        (uvr.xMin + uvr.xMax) * 0.5f, (uvr.yMin + uvr.yMax) * 0.5f);
                    dst.Add(new QuadInstance
                    {
                        rect = new Vector4(mn.x, mn.y, sz.x, sz.y),
                        uvA = cuv,
                        uvB = cuv,
                        color = col,
                        flags = extraFlags
                    });
                }
                else
                {
                    Vector4 bb = gq.bbox;
                    dst.Add(new QuadInstance
                    {
                        rect = new Vector4(mn.x, mn.y, sz.x, sz.y),
                        //quad corner (0,0) sits at the SMALLEST unity y = glyph bottom
                        //(em bbox min y), so the interpolated uv is the em position
                        uvA = new Vector4(bb.x, bb.y, bb.z, bb.y),
                        uvB = new Vector4(bb.x, bb.w, bb.z, bb.w),
                        color = col,
                        flags = QuadInstance.FlagCurveGlyph | extraFlags,
                        //bold at bit 20, NOT 24: the vertex path rebuilds this
                        //through float32 (exact to 2^24) — index|1<<24 would
                        //round odd indices to the neighbouring glyph
                        padding = (uint)gq.glyphIndex | (gq.bold ? 1u << 20 : 0u)
                    });
                }
                n++;
            }
            return n;
        }

        void BuildSegments()
        {
            Segment seg = null;
            int runIndex = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                PendingLeaf leaf = _pending[_entries[i].payload];
                if (!leaf.instanceable)
                {
                    //_runBarriers[r] closes run r; the final run has no closer
                    _runBarriers.Add(new RunBarrier { graphics = leaf.graphics, scope = leaf.scope });
                    runIndex++;
                    seg = null;
                    continue;
                }

                //M8-2: a mount splices its own frozen segments/leaves wholesale
                if (leaf.mount != null)
                {
                    SpliceMountSegments(leaf, runIndex);
                    seg = null; //never merge neighbors into spliced segments
                    continue;
                }

                int texIdx = seg != null ? IndexOfOrAddTexture(seg, leaf.texture) : -1;
                if (texIdx < 0)
                {
                    seg = TakeSegment();
                    seg.z = -0.5f * _segments.Count;
                    seg.start = _quads.Count;
                    seg.count = 0;
                    seg.runIndex = runIndex;
                    _segments.Add(seg);
                    texIdx = IndexOfOrAddTexture(seg, leaf.texture);
                }
                uint texBits = (uint)texIdx << QuadInstance.TexIndexShift;

                var range = new LeafRange
                {
                    graphics = leaf.graphics,
                    start = _quads.Count,
                    count = leaf.stageCount,
                    liveCount = leaf.stageCount,
                    bakedAlpha = leaf.bakeAlpha,
                    slotIndex = leaf.slotIndex,
                    texIndexBits = texBits,
                    segIndex = _segments.Count - 1,
                    flags = leaf.flags,
                    clipIndex = leaf.clipIndex,
                    sdf = leaf.sdf,
                    curve = leaf.curve
                };
                for (int q = 0; q < leaf.stageCount; q++)
                {
                    QuadInstance qi = _staging[leaf.stageStart + q];
                    qi.flags |= texBits;
                    _quads.Add(qi);
                }
                seg.count = _quads.Count - seg.start;
                _leaves.Add(range);
                _leafLookup[range.graphics] = range;
            }
        }

        /// <summary>
        /// M8-2: turns a mount's staged quads (already clip-remapped, slot-
        /// stamped, grayed) into stream segments and live LeafRanges. Baked
        /// leaves get full push-channel service: tier-2 rewrites re-read the
        /// live meshes, the color tier rescales from bakedAlpha, and rewrites
        /// recorded before a re-splice are re-queued so stale blob quads never
        /// linger.
        /// </summary>
        void SpliceMountSegments(PendingLeaf leaf, int runIndex)
        {
            FqsMount fm = leaf.mount;
            int quadBase = _quads.Count;
            for (int q = 0; q < leaf.stageCount; q++)
                _quads.Add(_staging[leaf.stageStart + q]);

            var segs = fm.data.segs;
            int segBase = _segments.Count;
            for (int i = 0; i < segs.Length; i++)
            {
                Segment sg = TakeSegment();
                sg.z = -0.5f * _segments.Count;
                sg.start = quadBase + segs[i].start;
                sg.count = segs[i].count;
                sg.runIndex = runIndex;
                sg.texCount = 0;
                AddSegTex(sg, fm, segs[i].tex0);
                AddSegTex(sg, fm, segs[i].tex1);
                AddSegTex(sg, fm, segs[i].tex2);
                AddSegTex(sg, fm, segs[i].tex3);
                _segments.Add(sg);
            }

            var leaves = fm.data.leaves;
            for (int i = 0; i < leaves.Length; i++)
            {
                FqsLeafRecord lr = leaves[i];
                NGraphics g = fm.leafGraphics[i];
                int segIndex = segBase;
                for (int si = 0; si < segs.Length; si++)
                {
                    if (lr.start >= segs[si].start && lr.start < segs[si].start + segs[si].count)
                    {
                        segIndex = segBase + si;
                        break;
                    }
                }
                var range = new LeafRange
                {
                    graphics = g,
                    start = quadBase + lr.start,
                    count = lr.count,
                    liveCount = lr.liveCount,
                    bakedAlpha = lr.bakedAlpha,
                    slotIndex = leaf.slotIndex,
                    texIndexBits = _quads[quadBase + lr.start].flags & (3u << QuadInstance.TexIndexShift),
                    segIndex = segIndex,
                    flags = (lr.flags & ~(FqsLeafRecord.KindSdf | FqsLeafRecord.KindCurve)) | leaf.flags,
                    clipIndex = leaf.mountClipMap[lr.clipIndex],
                    sdf = (lr.flags & FqsLeafRecord.KindSdf) != 0,
                    curve = false,
                    mount = fm,
                };
                _leaves.Add(range);
                _leafLookup[g] = range;

                //self-heal staleness: content rewritten since the bake, or a
                //color/alpha state differing from the baked value, refreshes
                //through the ordinary push channels right after this extract
                if (fm.rewritten.Contains(g))
                    _QueueLeafUpdate(g);
                else if (g._colorStale || g._currentAlpha != lr.bakedAlpha)
                    _QueueLeafColor(g);
            }

            //M8-4: visibility is re-derived from the LIVE flags every splice
            //(stateless — hidden branches zero their ranges again)
            {
                int cnt = fm.root.numChildren;
                for (int i = 0; i < cnt; i++)
                {
                    DisplayObject child = fm.root.GetChildAt(i);
                    if (!child.visible)
                        HideSubtree(child, true);
                    else
                        HideInvisibleBelow(child);
                }
            }
            fm.spliced = true;
        }

        void HideInvisibleBelow(DisplayObject obj)
        {
            if (obj is Container c)
            {
                int cnt = c.numChildren;
                for (int i = 0; i < cnt; i++)
                {
                    DisplayObject child = c.GetChildAt(i);
                    if (!child.visible)
                        HideSubtree(child, true);
                    else
                        HideInvisibleBelow(child);
                }
            }
        }

        /// <summary>
        /// M8-4 visibility tier: hide zeroes the subtree's mounted quad ranges
        /// in place; show clears the flag and requeues tier-2 rebuilds (settled
        /// in the same frame's Flush). Showing content ABSENT from the blob
        /// (invisible at bake) invalidates the mount — the runtime walk renders
        /// it, correctness over speed.
        /// </summary>
        internal bool _OnMountVisibility(DisplayObject obj)
        {
            if (_leaves.Count == 0)
                return false;
            if (obj.visible)
            {
                if (!ShowSubtree(obj))
                    return false;
            }
            else
                HideSubtree(obj, false);
            return true;
        }

        /// <summary>
        /// M8-4: services a container transform inside a valid mount as exact
        /// per-leaf rewrites (slot-relative matrices re-read the live
        /// transforms). Returns false when an unclaimed visible leaf exists
        /// under it (content the stream does not carry).
        /// </summary>
        internal bool _OnMountInteriorTransform(Container c)
        {
            if (_leaves.Count == 0)
                return false;
            if (!QueueSubtreeUpdates(c))
                return false;
            //the moved container (or a descendant) may OWN slot-riding clip
            //entries — their root-space rects re-derive from live transforms
            //through the slot-dirty recompute path
            _slotsDirty = true;
            return true;
        }

        bool QueueSubtreeUpdates(DisplayObject obj)
        {
            NGraphics g = obj.graphics;
            if (g != null && g.texture != null)
            {
                if (_leafLookup.TryGetValue(g, out LeafRange r))
                {
                    if (!r.hidden)
                        _QueueLeafUpdate(g);
                }
                else if (obj.visible)
                    return false; //visible content the stream does not carry
            }
            if (obj is Container c)
            {
                int cnt = c.numChildren;
                for (int i = 0; i < cnt; i++)
                {
                    DisplayObject child = c.GetChildAt(i);
                    if (!child.visible)
                        continue;
                    if (!QueueSubtreeUpdates(child))
                        return false;
                }
            }
            return true;
        }

        void HideSubtree(DisplayObject obj, bool inExtract)
        {
            if (obj.graphics != null)
                HideLeaf(obj.graphics, inExtract);
            if (obj is Container c)
            {
                int cnt = c.numChildren;
                for (int i = 0; i < cnt; i++)
                    HideSubtree(c.GetChildAt(i), inExtract);
            }
        }

        void HideLeaf(NGraphics g, bool inExtract)
        {
            if (!_leafLookup.TryGetValue(g, out LeafRange range) || range.hidden)
                return;
            range.hidden = true;
            for (int i = 0; i < range.liveCount; i++)
            {
                _quads[range.start + i] = default;
                if (!inExtract)
                {
                    _uploadArray[range.start + i] = default;
                    if (_vertexPath)
                        QuadVertex.WriteQuad(_vertexUpload, range.start + i, default);
                }
            }
            if (!inExtract && range.liveCount > 0)
            {
                Segment owner = _segments[range.segIndex];
                if (range.start < owner.dirtyMin) owner.dirtyMin = range.start;
                int last = range.start + range.liveCount - 1;
                if (last > owner.dirtyMax) owner.dirtyMax = last;
                UploadDirtyRange(owner);
            }
        }

        bool ShowSubtree(DisplayObject obj)
        {
            NGraphics g = obj.graphics;
            //NOTE: no mesh guards here — a leaf hidden since birth has an
            //UNBUILT mesh, and that is exactly the "absent from the blob"
            //case that must invalidate (the runtime walk claims it properly)
            if (g != null && g.texture != null)
            {
                if (!_leafLookup.TryGetValue(g, out LeafRange range))
                    return false; //absent from the blob: only the runtime walk can render it
                if (range.hidden)
                {
                    range.hidden = false;
                    _QueueLeafUpdate(g);
                }
            }
            if (obj is Container c)
            {
                int cnt = c.numChildren;
                for (int i = 0; i < cnt; i++)
                {
                    DisplayObject child = c.GetChildAt(i);
                    if (!child.visible)
                        continue;
                    if (!ShowSubtree(child))
                        return false;
                }
            }
            return true;
        }

        void AddSegTex(Segment sg, FqsMount fm, int texRef)
        {
            if (texRef < 0)
                return;
            sg.textures[sg.texCount++] = fm.textures[texRef];
        }

        static void StampIndices(List<QuadInstance> quads, int start, int count, uint clipIndex, uint slotIndex)
        {
            if (clipIndex == 0 && slotIndex == 0)
                return;
            for (int i = start; i < start + count; i++)
            {
                QuadInstance q = quads[i];
                q.clipIndex = clipIndex;
                q.transformIndex = slotIndex;
                quads[i] = q;
            }
        }

        /// <summary>
        /// Scroll tier: one vector write, applied in the vertex shader.
        /// </summary>
        public void SetScrollOffset(Vector2 offset)
        {
#if UNITY_2020_1_OR_NEWER
            using (sScroll.Auto())
#endif
            {
                _scrollOffset = offset;
            }
        }

        /// <summary>
        /// Content tier: re-reassemble one leaf and upload only its instance range.
        /// Returns false when the quad count changed (caller should Extract).
        /// </summary>
        public bool UpdateLeaf(NGraphics graphics)
        {
            return UpdateLeaf(graphics, false);
        }

        bool UpdateLeaf(NGraphics graphics, bool deferUpload)
        {
#if UNITY_2020_1_OR_NEWER
            using (sLeafUpdate.Auto())
#endif
            {
                if (!_leafLookup.TryGetValue(graphics, out LeafRange range))
                    return false;
                if (_vertexPath ? _vertexUpload == null : _buffer == null)
                    return false;
                if (range.hidden)
                    return true; //stays zeroed; the show path requeues a rebuild

                if (graphics._renderless)
                    graphics._EnsureMeshBuilt(); //deferred leaf: build on demand
                Mesh mesh = graphics.mesh;
                if (mesh == null)
                    return false;

                Matrix4x4 baseW2L;
                if (range.slotIndex == 0)
                    baseW2L = _container.cachedTransform.worldToLocalMatrix;
                else
                {
                    Container slotOwner = _slotOwners[range.slotIndex];
                    if (slotOwner == null || slotOwner.isDisposed)
                        return false; //slot died: recompile re-bakes in root space
                    baseW2L = slotOwner.cachedTransform.worldToLocalMatrix;
                }
                Matrix4x4 m = baseW2L * graphics.gameObject.transform.localToWorldMatrix;

                sLeafScratch.Clear();
                int rebuilt;
                if (range.sdf)
                {
                    //analytic leaf: re-emit from the factory parameters (a factory
                    //swap or a border toggling on/off changes the count -> recompile)
                    rebuilt = EmitSdfQuads(graphics, m, sLeafScratch, range.flags & QuadInstance.FlagGrayed);
                    if (rebuilt != range.count)
                        return false;
                }
                else if (range.curve)
                {
                    //curve leaves carry glyph-count slack (batch 5), so any
                    //length within the reserved range stays tier-2
                    rebuilt = EmitCurveQuads(graphics, m, sLeafScratch, range.flags & QuadInstance.FlagGrayed);
                    if (rebuilt == 0 || rebuilt > range.count)
                        return false;
                }
                else
                {
                    //color tier (batch 3): deferred alpha/tint must land in the
                    //mesh before it is re-read
                    graphics._RestoreNativeColors();
                    mesh.GetVertices(sVerts);
                    mesh.GetUVs(0, sUVs);
                    mesh.GetColors(sColors);
                    mesh.GetTriangles(sTris, 0);

                    int skipped;
                    rebuilt = QuadReassembler.Append(sLeafScratch, sVerts, sUVs, sColors, sTris, m, _drawOffset, range.flags, out skipped);
                    if (skipped > 0)
                        return false; //topology went non-quad: recompile
                    //text leaves carry slack (batch 2): any length within the
                    //reserved range stays tier-2; the tail is padded below
                    bool slackLeaf = (range.flags & QuadInstance.FlagAlphaTexture) != 0;
                    if (slackLeaf ? rebuilt > range.count : rebuilt != range.count)
                        return false;
                }

                //write the rebuilt quads; clear only the tail that was live in a
                //previously longer text (same-length churn touches exactly the
                //glyphs it has)
                int touch = rebuilt > range.liveCount ? rebuilt : range.liveCount;
                for (int i = 0; i < touch; i++)
                {
                    QuadInstance q = i < rebuilt ? sLeafScratch[i] : default;
                    q.clipIndex = range.clipIndex;
                    q.transformIndex = range.slotIndex;
                    q.flags |= range.texIndexBits;
                    _quads[range.start + i] = q;
                    _uploadArray[range.start + i] = q;
                    if (_vertexPath)
                        QuadVertex.WriteQuad(_vertexUpload, range.start + i, in q);
                }
                range.liveCount = rebuilt;
                range.bakedAlpha = graphics._currentAlpha;

                //coalesce (batch 2): mark the owning segment's dirty range; the
                //Flush loop uploads each segment once, direct callers keep the
                //old upload-immediately semantics
                Segment owner = _segments[range.segIndex];
                if (range.start < owner.dirtyMin) owner.dirtyMin = range.start;
                int last = range.start + touch - 1;
                if (last > owner.dirtyMax) owner.dirtyMax = last;
                if (!deferUpload)
                    UploadDirtyRange(owner);
                return true;
            }
        }

        /// <summary>
        /// Color tier (batch 3): alpha/tint change on a claimed leaf rewrites only
        /// the color field of its live quads — no native mesh read, no reassembly.
        /// The alpha basis is rescaled from the baked value (QuadReassembler takes
        /// one color per quad, so this is exact); a leaf baked at alpha 0 has no
        /// recoverable basis and takes the full path once, as does a deferred tint
        /// on an analytic (SDF/curve) leaf whose quads mix fill/border colors.
        /// </summary>
        void UpdateLeafColor(NGraphics graphics)
        {
            if (!_leafLookup.TryGetValue(graphics, out LeafRange range))
                return;
            if (_vertexPath ? _vertexUpload == null : _buffer == null)
                return;

            if (range.hidden)
                return; //zeroed by the visibility tier
            float alpha = graphics._currentAlpha;
            bool tint = graphics._tintStale;
            if (range.bakedAlpha <= 0f || (tint && (range.sdf || range.curve)))
            {
                if (!UpdateLeaf(graphics, true))
                    _structureDirty = true;
                return;
            }

            float scale = alpha / range.bakedAlpha;
            Color tintRgb = graphics._tintColor;
            bool tintRewrite = tint && !range.sdf && !range.curve;
            for (int i = 0; i < range.liveCount; i++)
            {
                QuadInstance q = _quads[range.start + i];
                Color c = q.color;
                if (tintRewrite)
                {
                    c.r = tintRgb.r;
                    c.g = tintRgb.g;
                    c.b = tintRgb.b;
                }
                c.a *= scale;
                q.color = c;
                _quads[range.start + i] = q;
                _uploadArray[range.start + i] = q;
                if (_vertexPath)
                    QuadVertex.WriteQuad(_vertexUpload, range.start + i, in q);
            }
            range.bakedAlpha = alpha;

            if (range.liveCount > 0)
            {
                Segment owner = _segments[range.segIndex];
                if (range.start < owner.dirtyMin) owner.dirtyMin = range.start;
                int last = range.start + range.liveCount - 1;
                if (last > owner.dirtyMax) owner.dirtyMax = last;
            }
        }

        /// <summary>
        /// Uploads a segment's coalesced dirty quad range and resets it (batch 2).
        /// </summary>
        void UploadDirtyRange(Segment seg)
        {
            if (seg.dirtyMax < seg.dirtyMin)
                return;
            int start = seg.dirtyMin;
            int count = seg.dirtyMax - seg.dirtyMin + 1;
            if (_vertexPath)
                seg.mesh.SetVertexBufferData(_vertexUpload, start * 4,
                    (start - seg.start) * 4, count * 4, 0, kNoMeshChecks);
            else
                _buffer.SetData(_uploadArray, start, start, count);
            seg.dirtyMin = int.MaxValue;
            seg.dirtyMax = -1;
        }

        void UploadAllDirtyRanges()
        {
            for (int i = 0; i < _segments.Count; i++)
                UploadDirtyRange(_segments[i]);
        }

        /// <summary>
        /// Submission tier: per-frame sync of the segment renderers. The segments
        /// are real MeshRenderers (children of the container), so the camera draws
        /// them like any other UI renderer; this call keeps their sortingOrder,
        /// layer and shared uniforms in step. Call once per frame AFTER the stage
        /// update (native renderingOrder must be assigned first).
        /// </summary>
        public void Render()
        {
#if UNITY_2020_1_OR_NEWER
            using (sRender.Auto())
#endif
            {
                if (_inPlace)
                {
                    if (_container.isDisposed || !_container.visible || !_container.gameObject.activeInHierarchy)
                    {
                        SetSegmentsVisible(false);
                        return;
                    }
                    Flush();
                }

                if ((_vertexPath ? _vertexUpload == null : _buffer == null) || _segments.Count == 0)
                    return;

                //recomputed every frame: in in-place mode the container itself moves
                //when its ScrollPane scrolls, while the mask window stays put
                ComputeExternalWindow();

                //run orders shift only when the native renderingOrder assignment
                //moved (tree changes outside the stream shift our block uniformly,
                //changes inside recompile via Extract) — probe first leaf plus
                //EVERY barrier order instead of walking every leaf every frame
                //(batch 2; endpoint-only sampling missed compensating same-frame
                //shifts between middle barriers, e.g. two GoWrappers trading a
                //renderer count — barriers are few, the loop is a handful of
                //int reads)
                int runProbe = _leaves.Count > 0 ? _leaves[0].graphics.renderingOrder : 0;
                for (int bi = 0; bi < _runBarriers.Count; bi++)
                    runProbe = (runProbe * 397) ^ _runBarriers[bi].order;
                if (runProbe != _lastRunOrderProbe)
                {
                    ComputeRunOrders();
                    _lastRunOrderProbe = runProbe;
                }

                //transform slots (batch 3): a slot container moved — re-derive the
                //slot matrices (and any slot-riding internal clips) from the live
                //transforms; the refreshed values ride the property push below
                if (_slotsDirty)
                {
                    _slotsDirty = false;
                    RecomputeSlotMatrices();
                    if (_hasSlottedClips)
                    {
                        RecomputeSlottedClips();
                        if (!_vertexPath && _clipBuffer != null)
                        {
                            _clipEntries.CopyTo(_clipUploadArray);
                            _clipBuffer.SetData(_clipUploadArray, 0, 0, _clipEntries.Count);
                        }
                    }
                    _propsDirty = true;
                }

                //stream-level uniforms: push property blocks only when something
                //they carry actually changed (batch 2)
                int curveVer = -1;
                if (CurveFontStore.loaded)
                {
                    //data textures (batch 5): both backends read the tables as
                    //globals — refresh them when new glyphs were baked
                    CurveFontStore.EnsureBuffers();
                    curveVer = CurveFontStore.version;
                }
                bool pushProps = _propsDirty
                    || _scrollOffset != _lastScroll
                    || _clipRect != _lastClipRect
                    || _clipSoft != _lastClipSoft
                    || curveVer != _lastCurveFontVersion;

                if (pushProps && _vertexPath)
                {
                    //internal clips as uniform arrays (always full-size: Unity pins
                    //array length at first use per shader)
                    for (int i = 0; i < MaxVertexPathClips; i++)
                    {
                        if (i < _clipEntries.Count)
                        {
                            _clipRectArr[i] = _clipEntries[i].rect;
                            _clipSoftArr[i] = _clipEntries[i].soft;
                        }
                        else
                        {
                            _clipRectArr[i] = ClipEntry.None.rect;
                            _clipSoftArr[i] = Vector4.zero;
                        }
                    }
                }

                //layer protocol: follow a claimed leaf's gameObject — painting
                //captures flip leaves via SetChildrenLayer, and the segments must
                //flip with them so CaptureCamera sees them and the main camera
                //does not (review M12)
                int layer = _container.gameObject.layer;
                if (_leaves.Count > 0 && _leaves[0].graphics.gameObject != null)
                    layer = _leaves[0].graphics.gameObject.layer;

                for (int i = 0; i < _segments.Count; i++)
                {
                    Segment seg = _segments[i];
                    if (seg.renderer == null || seg.count == 0)
                        continue;

                    if (!seg.go.activeSelf)
                        seg.go.SetActive(true);

                    //interleaving: all segments of a run share the sortingOrder slot
                    //just below their closing barrier; within a run the z step keeps
                    //segment order (Unity breaks sortingOrder ties by depth)
                    int order = _runOrderScratch[seg.runIndex];
                    if (order != seg.lastSortingOrder)
                    {
                        seg.renderer.sortingOrder = order;
                        seg.lastSortingOrder = order;
                    }

                    if (layer != seg.lastLayer)
                    {
                        seg.go.layer = layer;
                        seg.lastLayer = layer;
                    }

                    if (pushProps)
                    {
                        seg.props.SetVector("_ScrollOffset", _scrollOffset);
                        seg.props.SetVector("_ClipRect", _clipRect);
                        seg.props.SetVector("_ClipSoft", _clipSoft);
                        seg.props.SetMatrixArray("_TransformSlots", _slotMatrixArr);
                        if (_vertexPath)
                        {
                            seg.props.SetVectorArray("_ClipRects", _clipRectArr);
                            seg.props.SetVectorArray("_ClipSofts", _clipSoftArr);
                        }
                        seg.renderer.SetPropertyBlock(seg.props);
                    }
                }

                if (pushProps)
                {
                    _propsDirty = false;
                    _lastScroll = _scrollOffset;
                    _lastClipRect = _clipRect;
                    _lastClipSoft = _clipSoft;
                    _lastCurveFontVersion = curveVer;
                }
            }
        }

        /// <summary>
        /// Layer-flip protocol: SetChildrenLayer carries the segment renderers along
        /// with the DisplayObject children (CaptureCamera's synchronous
        /// flip-render-flip must include them for filter/painting captures).
        /// </summary>
        internal void _SetSegmentLayers(int layer)
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                Segment seg = _segments[i];
                if (seg.go != null)
                {
                    seg.go.layer = layer;
                    seg.lastLayer = layer;
                }
            }
        }

        void SetSegmentsVisible(bool visible)
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                Segment seg = _segments[i];
                if (seg.go != null && seg.go.activeSelf != visible)
                    seg.go.SetActive(visible);
            }
        }

        /// <summary>
        /// Per-run sortingOrder: the highest claimed-leaf renderingOrder in the run
        /// that is still below the closing barrier's order. Claimed leaves consume
        /// order slots in the native assignment pass anyway, so the slot is free;
        /// overlapping content cannot have been sorted across a barrier, which makes
        /// this order correct wherever it is visually observable.
        /// </summary>
        void ComputeRunOrders()
        {
            int runCount = _runBarriers.Count + 1;
            _runOrderScratch.Clear();
            for (int r = 0; r < runCount; r++)
                _runOrderScratch.Add(int.MinValue);

            for (int i = 0; i < _leaves.Count; i++)
            {
                LeafRange leaf = _leaves[i];
                int r = _segments[leaf.segIndex].runIndex;
                int barrierOrder = r < _runBarriers.Count ? _runBarriers[r].order : int.MaxValue;
                int o = leaf.graphics.renderingOrder;
                if (o < barrierOrder && o > _runOrderScratch[r])
                    _runOrderScratch[r] = o;
            }

            for (int r = 0; r < runCount; r++)
            {
                if (_runOrderScratch[r] == int.MinValue)
                {
                    //run has no usable slot (nothing in it overlaps its barrier):
                    //sit just above the previous barrier's LAST native slot
                    _runOrderScratch[r] = r > 0 ? _runBarriers[r - 1].order + 1 : 0;
                }
            }
        }

        public void Dispose()
        {
            if (_inPlace)
            {
                foreach (var g in _claimed)
                {
                    if (g._instancedBy == this)
                        g._ClearInstancedOwner();
                }
                _claimed.Clear();
                if (_container._instancedStream == this)
                    _container._instancedStream = null;
                liveInPlaceCount--;
                sLiveStreams.Remove(this);
                _inPlace = false;
            }
            foreach (var t in _watchedTextures)
                t.onSizeChanged -= _onWatchedTexture;
            _watchedTextures.Clear();
            if (_buffer != null)
            {
                _buffer.Release();
                _buffer = null;
            }
            if (_clipBuffer != null)
            {
                _clipBuffer.Release();
                _clipBuffer = null;
            }
            foreach (var kv in _materialCache)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(kv.Value);
                else
                    UnityEngine.Object.DestroyImmediate(kv.Value);
            }
            _materialCache.Clear();
            foreach (var seg in _segments)
            {
                if (seg.go != null)
                {
                    //Destroy defers to end of frame: deactivate first so a
                    //disposed stream stops rendering IMMEDIATELY
                    seg.go.SetActive(false);
                    DestroyObject(seg.go);
                }
            }
            foreach (var seg2 in _segments)
            {
                if (seg2.mesh != null)
                {
                    DestroyObject(seg2.mesh);
                    seg2.mesh = null;
                }
            }
            _segments.Clear();
            foreach (var go in _segmentPool)
                DestroyObject(go);
            _segmentPool.Clear();
            foreach (var m in _meshPool)
                DestroyObject(m);
            _meshPool.Clear();
            _prevSegments.Clear();
            _segmentObjPool.Clear();
            _mpbPool.Clear();
            _leaves.Clear();
            _quads.Clear();
            _runBarriers.Clear();
            if (_pullMesh != null)
            {
                DestroyObject(_pullMesh);
                _pullMesh = null;
                _pullCapacity = 0;
            }
        }

        static void DestroyObject(UnityEngine.Object o)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(o);
            else
                UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
