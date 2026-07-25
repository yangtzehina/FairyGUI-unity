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
                for (Container p = c.parent; p != null; p = p.parent)
                {
                    if (p._instancedStream != null)
                    {
                        p._instancedStream._structureDirty = true;
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
                if (p is Container c && c._instancedStream != null)
                {
                    c._instancedStream._structureDirty = true;
                    return;
                }
            }
        }

        class Segment
        {
            public Texture texture;
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
            public int lastSortingOrder = int.MinValue;
            public int lastLayer = -1;
        }

        class LeafRange
        {
            public NGraphics graphics;
            public int start;
            public int count;
            public int segIndex;
            public uint flags;
            public uint clipIndex;
            public bool sdf;   //M7: tier-2 updates re-emit analytically, not from mesh
            public bool curve; //M9b: same, from the CurveTextMesh layout
        }

        struct PendingLeaf
        {
            public NGraphics graphics;
            public Texture texture;
            public uint flags;
            public uint clipIndex;
            public int stageStart;    //quads pre-built into _staging at walk time
            public int stageCount;
            public bool instanceable; //false: non-quad topology -> native fallback
            public bool sdf;          //M7: quads carry analytic SDF coverage
            public bool curve;        //M9b: quads are curve-text glyphs
        }

        readonly List<Segment> _segments = new List<Segment>();
        readonly List<LeafRange> _leaves = new List<LeafRange>();
        readonly List<PendingLeaf> _pending = new List<PendingLeaf>();
        readonly List<AdjacencyEntry> _entries = new List<AdjacencyEntry>();
        //internal clip regions, content-space at scroll 0, WITHOUT drawOffset
        //(offset added at upload); entry 0 is the "no clip" sentinel
        readonly List<ClipEntry> _clipEntries = new List<ClipEntry>();
        readonly List<QuadInstance> _quads = new List<QuadInstance>();
        readonly List<QuadInstance> _staging = new List<QuadInstance>();
        //graphics of the fallback leaf that CLOSES run r (null for the last run);
        //its native renderingOrder separates the runs each frame
        readonly List<NGraphics> _runBarriers = new List<NGraphics>();
        readonly List<GameObject> _segmentPool = new List<GameObject>();
        readonly List<Mesh> _meshPool = new List<Mesh>();
        readonly List<int> _runOrderScratch = new List<int>();
        Mesh _pullMesh;
        int _pullCapacity;
        bool _vertexPath;
        QuadVertex[] _vertexUpload;
        Vector4[] _clipRectArr;
        Vector4[] _clipSoftArr;
        bool _clipOverflowWarned;
        readonly Dictionary<Texture, Material> _materialCache = new Dictionary<Texture, Material>();

        Container _container;
        bool _sortAdjacency;
        bool _inPlace;
        bool _structureDirty;
        readonly List<NGraphics> _dirtyLeaves = new List<NGraphics>();
        readonly HashSet<NGraphics> _dirtyLeafSet = new HashSet<NGraphics>();
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
        Vector2 _drawOffset;
        int _skippedPairs;
        int _maskedSubtrees;

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
            for (int i = 0; i < _leaves.Count; i++)
                if (_leaves[i].graphics == graphics)
                    return _leaves[i].clipIndex;
            return 0;
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
                Extract();
                return;
            }

            if (_dirtyLeaves.Count > 0)
            {
                for (int i = 0; i < _dirtyLeaves.Count; i++)
                {
                    NGraphics g = _dirtyLeaves[i];
                    if (_inPlace && g._instancedBy != this)
                        continue; //released while queued
                    if (!UpdateLeaf(g))
                        _structureDirty = true;
                }
                _dirtyLeaves.Clear();
                _dirtyLeafSet.Clear();
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
                foreach (var seg in _segments)
                    ReleaseSegmentRenderer(seg);
                _segments.Clear();
                _leaves.Clear();
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

                Matrix4x4 worldToLocal = _container.cachedTransform.worldToLocalMatrix;
                ExtractContainer(_container, worldToLocal, 0, _container.grayed);

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

                if (_buffer != null)
                    _buffer.Release();
                _buffer = null;
                if (_clipBuffer != null)
                    _clipBuffer.Release();
                _clipBuffer = null;
                _vertexUpload = null;
                if (_quads.Count == 0)
                    return;

                if (_vertexPath)
                {
                    //vertex-stream backend: instance data baked x4 into per-segment
                    //mesh vertices; no compute buffers anywhere
                    _uploadArray = _quads.ToArray();
                    _vertexUpload = new QuadVertex[_quads.Count * 4];
                    for (int i = 0; i < _uploadArray.Length; i++)
                        QuadVertex.WriteQuad(_vertexUpload, i, in _uploadArray[i]);
                }
                else
                {
                    _uploadArray = _quads.ToArray();
                    _buffer = new ComputeBuffer(_uploadArray.Length, QuadInstance.Stride, ComputeBufferType.Structured);
                    _buffer.SetData(_uploadArray);

                    _clipUploadArray = _clipEntries.ToArray();
                    _clipBuffer = new ComputeBuffer(_clipUploadArray.Length, ClipEntry.Stride, ComputeBufferType.Structured);
                    _clipBuffer.SetData(_clipUploadArray);

                    int maxCount = 0;
                    foreach (var seg in _segments)
                        if (seg.count > maxCount) maxCount = seg.count;
                    EnsurePullMesh(maxCount);
                }

                foreach (var seg in _segments)
                {
                    Material mat;
                    if (!_materialCache.TryGetValue(seg.texture, out mat))
                    {
                        mat = new Material(_shader);
                        mat.hideFlags = HideFlags.DontSave;
                        mat.mainTexture = seg.texture;
                        _materialCache.Add(seg.texture, mat);
                    }
                    seg.material = mat;
                    ClaimSegmentRenderer(seg);
                    seg.props = new MaterialPropertyBlock();
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
            }
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
            mesh.SetVertexBufferParams(seg.count * 4, QuadVertex.Layout);
            mesh.SetVertexBufferData(_vertexUpload, seg.start * 4, 0, seg.count * 4, 0, kNoMeshChecks);
            EnsureIndexCache(seg.count);
            mesh.SetIndexBufferParams(seg.count * 6, IndexFormat.UInt32);
            mesh.SetIndexBufferData(sIndexCache, 0, 0, seg.count * 6, kNoMeshChecks);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, seg.count * 6), kNoMeshChecks);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
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

        void ExtractContainer(Container container, Matrix4x4 worldToLocal, uint clipIndex, bool grayed)
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
                //through their own capture pipeline — leave them native (M5 will
                //interleave; review M12)
                if (child._paintingMode > 0)
                {
                    _maskedSubtrees++;
                    continue;
                }

                if (child.graphics != null && child.graphics.texture != null)
                    ExtractLeaf(child.graphics, worldToLocal, clipIndex, childGrayed);

                if (child.graphics != null && child.graphics.subInstances != null)
                {
                    foreach (var sub in child.graphics.subInstances)
                        if (sub.texture != null)
                            ExtractLeaf(sub, worldToLocal, clipIndex, childGrayed);
                }

                if (child is Container c)
                {
                    //stencil-masked scopes go to the fallback renderer (M5); skip
                    if (c.mask != null)
                    {
                        _maskedSubtrees++;
                        continue;
                    }
                    uint childClip = clipIndex;
                    if (c.clipRect != null)
                        childClip = PushClip(c, worldToLocal, clipIndex);
                    ExtractContainer(c, worldToLocal, childClip, childGrayed);
                }
            }
        }

        /// <summary>
        /// Registers an internal clip region: the container's clipRect transformed to
        /// stream-local space, folded (intersected) with the enclosing region — same
        /// semantics as UpdateContext.EnterClipping. Identical regions are deduped.
        /// </summary>
        uint PushClip(Container c, Matrix4x4 worldToLocal, uint parentIndex)
        {
            Matrix4x4 m = worldToLocal * c.cachedTransform.localToWorldMatrix;
            Vector4 rect = TransformClipRect((Rect)c.clipRect, m);

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
                if (_clipEntries[i].rect == rect && _clipEntries[i].soft == soft)
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
            return (uint)(_clipEntries.Count - 1);
        }

        void ExtractLeaf(NGraphics graphics, Matrix4x4 worldToLocal, uint clipIndex, bool grayed)
        {
            Mesh mesh = graphics.mesh;
            if (mesh == null || mesh.vertexCount == 0)
                return;
            //enabled is an admission condition (review M10/M14); its setter pushes
            if (graphics.meshRenderer != null && !graphics.meshRenderer.enabled)
                return;
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
            //offset space as the clip entries
            Bounds mb = mesh.bounds;
            Vector2 c0 = m.MultiplyPoint3x4(new Vector3(mb.min.x, mb.min.y, 0));
            Vector2 c1 = m.MultiplyPoint3x4(new Vector3(mb.max.x, mb.min.y, 0));
            Vector2 c2 = m.MultiplyPoint3x4(new Vector3(mb.min.x, mb.max.y, 0));
            Vector2 c3 = m.MultiplyPoint3x4(new Vector3(mb.max.x, mb.max.y, 0));
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
            var p = new PendingLeaf { graphics = graphics, texture = tex, flags = flags, clipIndex = clipIndex, stageStart = _staging.Count };

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

            //M9b: curve-text leaves emit one analytic glyph quad per character
            int curveCount = EmitCurveQuads(graphics, m, _staging, flags & QuadInstance.FlagGrayed);
            if (curveCount > 0)
            {
                p.stageCount = curveCount;
                p.instanceable = true;
                p.curve = true;
                StampClipIndex(_staging, p.stageStart, p.stageCount, clipIndex);
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
                StampClipIndex(_staging, p.stageStart, p.stageCount, clipIndex);
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
                    p.instanceable = true;
                    StampClipIndex(_staging, p.stageStart, p.stageCount, clipIndex);
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
            if (_vertexPath || !(graphics.meshFactory is CurveTextMesh ctm) || ctm.glyphQuads.Count == 0)
                return 0;
            if (Mathf.Abs(m.m01) > 1e-4f || Mathf.Abs(m.m10) > 1e-4f)
                return 0;

            Color col = ctm.color;
            col.a *= graphics._currentAlpha;
            int n = 0;
            foreach (var gq in ctm.glyphQuads)
            {
                Vector2 pa = m.MultiplyPoint3x4(new Vector3(gq.rect.xMin, -gq.rect.yMax, 0));
                Vector2 pb = m.MultiplyPoint3x4(new Vector3(gq.rect.xMax, -gq.rect.yMin, 0));
                Vector2 mn = Vector2.Min(pa, pb) + _drawOffset;
                Vector2 sz = Vector2.Max(pa, pb) - Vector2.Min(pa, pb);
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
                    padding = (uint)gq.glyphIndex
                });
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
                    _runBarriers.Add(leaf.graphics);
                    runIndex++;
                    seg = null;
                    continue;
                }

                if (seg == null || seg.texture != leaf.texture)
                {
                    seg = new Segment
                    {
                        texture = leaf.texture,
                        z = -0.5f * _segments.Count,
                        start = _quads.Count,
                        runIndex = runIndex
                    };
                    _segments.Add(seg);
                }

                var range = new LeafRange
                {
                    graphics = leaf.graphics,
                    start = _quads.Count,
                    count = leaf.stageCount,
                    segIndex = _segments.Count - 1,
                    flags = leaf.flags,
                    clipIndex = leaf.clipIndex,
                    sdf = leaf.sdf,
                    curve = leaf.curve
                };
                for (int q = 0; q < leaf.stageCount; q++)
                    _quads.Add(_staging[leaf.stageStart + q]);
                seg.count = _quads.Count - seg.start;
                _leaves.Add(range);
            }
        }

        static void StampClipIndex(List<QuadInstance> quads, int start, int count, uint clipIndex)
        {
            if (clipIndex == 0)
                return;
            for (int i = start; i < start + count; i++)
            {
                QuadInstance q = quads[i];
                q.clipIndex = clipIndex;
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
#if UNITY_2020_1_OR_NEWER
            using (sLeafUpdate.Auto())
#endif
            {
                LeafRange range = null;
                for (int i = 0; i < _leaves.Count; i++)
                {
                    if (_leaves[i].graphics == graphics)
                    {
                        range = _leaves[i];
                        break;
                    }
                }
                if (range == null || (_vertexPath ? _vertexUpload == null : _buffer == null))
                    return false;

                Mesh mesh = graphics.mesh;
                if (mesh == null)
                    return false;

                Matrix4x4 m = _container.cachedTransform.worldToLocalMatrix
                            * graphics.gameObject.transform.localToWorldMatrix;

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
                    rebuilt = EmitCurveQuads(graphics, m, sLeafScratch, range.flags & QuadInstance.FlagGrayed);
                    if (rebuilt != range.count)
                        return false;
                }
                else
                {
                    mesh.GetVertices(sVerts);
                    mesh.GetUVs(0, sUVs);
                    mesh.GetColors(sColors);
                    mesh.GetTriangles(sTris, 0);

                    int skipped;
                    rebuilt = QuadReassembler.Append(sLeafScratch, sVerts, sUVs, sColors, sTris, m, _drawOffset, range.flags, out skipped);
                    if (rebuilt != range.count || skipped > 0)
                        return false; //count changed or topology went non-quad: recompile
                }

                for (int i = 0; i < rebuilt; i++)
                {
                    QuadInstance q = sLeafScratch[i];
                    q.clipIndex = range.clipIndex;
                    _quads[range.start + i] = q;
                    _uploadArray[range.start + i] = q;
                    if (_vertexPath)
                        QuadVertex.WriteQuad(_vertexUpload, range.start + i, in q);
                }
                if (_vertexPath)
                {
                    //tier-2 partial path survives the backend switch: range upload
                    //into the owning segment's vertex buffer
                    Segment seg = _segments[range.segIndex];
                    seg.mesh.SetVertexBufferData(_vertexUpload, range.start * 4,
                        (range.start - seg.start) * 4, rebuilt * 4, 0, kNoMeshChecks);
                }
                else
                    _buffer.SetData(_uploadArray, range.start, range.start, rebuilt);
                return true;
            }
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
                ComputeRunOrders();

                if (_vertexPath)
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

                    seg.props.SetVector("_ScrollOffset", _scrollOffset);
                    seg.props.SetVector("_ClipRect", _clipRect);
                    seg.props.SetVector("_ClipSoft", _clipSoft);
                    if (_vertexPath)
                    {
                        seg.props.SetVectorArray("_ClipRects", _clipRectArr);
                        seg.props.SetVectorArray("_ClipSofts", _clipSoftArr);
                    }
                    else if (CurveFontStore.loaded)
                        CurveFontStore.Bind(seg.props); //rebind every frame: buffers may rebuild on new chars
                    seg.renderer.SetPropertyBlock(seg.props);
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
                int barrierOrder = r < _runBarriers.Count ? _runBarriers[r].renderingOrder : int.MaxValue;
                int o = leaf.graphics.renderingOrder;
                if (o < barrierOrder && o > _runOrderScratch[r])
                    _runOrderScratch[r] = o;
            }

            for (int r = 0; r < runCount; r++)
            {
                if (_runOrderScratch[r] == int.MinValue)
                {
                    //run has no usable slot (nothing in it overlaps its barrier):
                    //sit just above the previous barrier
                    _runOrderScratch[r] = r > 0 ? _runBarriers[r - 1].renderingOrder + 1 : 0;
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
                    DestroyObject(seg.go);
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
