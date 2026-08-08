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
    public partial class InstancedUIStream : IDisposable
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

        /// <summary>
        /// Ancestor-state change (grayed flip, reparent): the other notify
        /// entries walk UP from the changed object, which can never find a
        /// stream rooted BELOW it — native content inherits the new state
        /// through context accumulation while claimed quads keep the baked
        /// one (the audit's whole-window-gray split). Marks every in-place
        /// stream whose root sits inside the changed container; streams are
        /// few and the flips are rare, so the walk is not a hot path.
        /// </summary>
        internal static void _NotifyDescendantStreams(Container changed)
        {
            if (liveInPlaceCount == 0)
                return;
            for (int i = sLiveStreams.Count - 1; i >= 0; i--)
            {
                InstancedUIStream s = sLiveStreams[i];
                if (!s._inPlace)
                    continue;
                for (DisplayObject p = s._container; p != null; p = p.parent)
                {
                    if (ReferenceEquals(p, changed))
                    {
                        //recompile only when the effective root grayed actually
                        //changed — a plain reparent (window hide/show is
                        //RemoveChild+AddChild) between non-gray parents would
                        //otherwise pay a full Extract for nothing
                        if (_ChainGrayed(s._container) != s._lastRootGrayed)
                            s._MarkStructureDirty();
                        break;
                    }
                }
            }
        }

        //own grayed OR any ancestor's — the native pass gets the same value
        //through context accumulation
        static bool _ChainGrayed(Container c)
        {
            for (DisplayObject a = c; a != null; a = a.parent)
                if (a.grayed)
                    return true;
            return false;
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
        internal bool _inPlace;
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
        //root grayed as of the last Extract (own flag OR any ancestor):
        //_NotifyDescendantStreams compares against it so ancestry events that
        //do not change the effective value (reparent between non-gray parents)
        //skip the full recompile
        bool _lastRootGrayed;
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
                //container — both would toggle the same forceRenderingOff
                //flags on the leaves
                if (container._instancedStream != null)
                {
                    Debug.LogError("InstancedUIStream: container already has an in-place stream; disposing the old one.");
                    container._instancedStream.Dispose();
                }
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
