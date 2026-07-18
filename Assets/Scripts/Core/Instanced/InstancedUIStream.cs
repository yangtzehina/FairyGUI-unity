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
    /// segments the result on texture change (segments z-stepped so transparent
    /// sorting preserves submission order), and draws each segment with one instanced
    /// call from a shared unit quad.
    ///
    /// Update tiers (design §4.2): whole-stream movement is the container matrix
    /// (free); scrolling is SetScrollOffset (one uniform); a single leaf's content
    /// change is UpdateLeaf (partial instance-buffer upload); structure changes are
    /// Extract (full recompile). Dirty channels are driven by the caller in M1;
    /// the push protocol arrives in M4.
    ///
    /// Clipping (M3): the external window (the container's or its mask parent's
    /// clipRect) is a uniform tested against the scrolled position; internal nested
    /// rect clips are folded by intersection into ClipBuffer entries referenced per
    /// instance, so one segment can span many clip regions — draw count does not
    /// grow with clip region count. Both support FairyGUI clipSoftness. Stencil-
    /// masked subtrees are skipped and counted (fallback scope arrives in M5).
    ///
    /// Current limits: no fallback interleaving markers (M5). Elements whose
    /// triangle pairs are not quads are skipped and reported via lastSkippedPairs.
    /// </summary>
    public class InstancedUIStream : IDisposable
    {
        //fast guard so the DisplayObject-level push hooks cost one static int
        //compare when no in-place stream exists
        internal static int liveInPlaceCount;

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
            public Material material;
            public MaterialPropertyBlock props;
        }

        class LeafRange
        {
            public NGraphics graphics;
            public int start;
            public int count;
            public uint flags;
            public uint clipIndex;
        }

        struct PendingLeaf
        {
            public NGraphics graphics;
            public Matrix4x4 matrix;
            public Texture texture;
            public uint flags;
            public uint clipIndex;
        }

        readonly List<Segment> _segments = new List<Segment>();
        readonly List<LeafRange> _leaves = new List<LeafRange>();
        readonly List<PendingLeaf> _pending = new List<PendingLeaf>();
        readonly List<AdjacencyEntry> _entries = new List<AdjacencyEntry>();
        //internal clip regions, content-space at scroll 0, WITHOUT drawOffset
        //(offset added at upload); entry 0 is the "no clip" sentinel
        readonly List<ClipEntry> _clipEntries = new List<ClipEntry>();
        readonly List<QuadInstance> _quads = new List<QuadInstance>();
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
        Mesh _quadMesh;
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
                liveInPlaceCount++;
                container._instancedStream = this;
                _structureDirty = true;
            }
            _shader = Shader.Find("FairyGUI/InstancedUI");

            _quadMesh = new Mesh();
            _quadMesh.vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(0, 1, 0), new Vector3(1, 1, 0)
            };
            _quadMesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            _quadMesh.UploadMeshData(false);
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
                _segments.Clear();
                _leaves.Clear();
                _quads.Clear();
                _pending.Clear();
                _entries.Clear();
                _clipEntries.Clear();
                _clipEntries.Add(ClipEntry.None);
                _skippedPairs = 0;
                _maskedSubtrees = 0;

                foreach (var t in _watchedTextures)
                    t.onSizeChanged -= _onWatchedTexture;
                _watchedTextures.Clear();

                Matrix4x4 worldToLocal = _container.cachedTransform.worldToLocalMatrix;
                ExtractContainer(_container, worldToLocal, 0);

                if (_sortAdjacency)
                    AdjacencySorter.Sort(_entries);
                for (int i = 0; i < _entries.Count; i++)
                    AppendLeaf(_pending[_entries[i].payload]);

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
                if (_quads.Count == 0)
                    return;

                _uploadArray = _quads.ToArray();
                _buffer = new ComputeBuffer(_uploadArray.Length, QuadInstance.Stride, ComputeBufferType.Structured);
                _buffer.SetData(_uploadArray);

                _clipUploadArray = _clipEntries.ToArray();
                _clipBuffer = new ComputeBuffer(_clipUploadArray.Length, ClipEntry.Stride, ComputeBufferType.Structured);
                _clipBuffer.SetData(_clipUploadArray);

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
                    seg.props = new MaterialPropertyBlock();
                    seg.props.SetBuffer("_Instances", _buffer);
                    seg.props.SetBuffer("_Clips", _clipBuffer);
                    seg.props.SetInt("_InstanceStart", seg.start);
                }
            }
        }

        void ExtractContainer(Container container, Matrix4x4 worldToLocal, uint clipIndex)
        {
            int cnt = container.numChildren;
            for (int i = 0; i < cnt; i++)
            {
                DisplayObject child = container.GetChildAt(i);
                if (!child.visible)
                    continue;

                //painting scopes (filter/blend/perspective/cacheAsBitmap) render
                //through their own capture pipeline — leave them native (M5 will
                //interleave; review M12)
                if (child._paintingMode > 0)
                {
                    _maskedSubtrees++;
                    continue;
                }

                if (child.graphics != null && child.graphics.texture != null)
                    ExtractLeaf(child.graphics, worldToLocal, clipIndex);

                if (child.graphics != null && child.graphics.subInstances != null)
                {
                    foreach (var sub in child.graphics.subInstances)
                        if (sub.texture != null)
                            ExtractLeaf(sub, worldToLocal, clipIndex);
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
                    ExtractContainer(c, worldToLocal, childClip);
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
            _clipEntries.Add(new ClipEntry { rect = rect, soft = soft });
            return (uint)(_clipEntries.Count - 1);
        }

        void ExtractLeaf(NGraphics graphics, Matrix4x4 worldToLocal, uint clipIndex)
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

            _entries.Add(new AdjacencyEntry
            {
                key = tex,
                x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                payload = _pending.Count
            });
            _pending.Add(new PendingLeaf { graphics = graphics, matrix = m, texture = tex, flags = flags, clipIndex = clipIndex });
        }

        void AppendLeaf(PendingLeaf leaf)
        {
            //segment on texture change in (sorted) submission order
            bool segCreated = false;
            Segment seg = _segments.Count > 0 ? _segments[_segments.Count - 1] : null;
            if (seg == null || seg.texture != leaf.texture)
            {
                seg = new Segment
                {
                    texture = leaf.texture,
                    z = -0.5f * _segments.Count,
                    start = _quads.Count
                };
                _segments.Add(seg);
                segCreated = true;
            }

            Mesh mesh = leaf.graphics.mesh;
            mesh.GetVertices(sVerts);
            mesh.GetUVs(0, sUVs);
            mesh.GetColors(sColors);
            mesh.GetTriangles(sTris, 0);

            var range = new LeafRange { graphics = leaf.graphics, start = _quads.Count, flags = leaf.flags, clipIndex = leaf.clipIndex };
            int skipped;
            range.count = QuadReassembler.Append(_quads, sVerts, sUVs, sColors, sTris, leaf.matrix, _drawOffset, leaf.flags, out skipped);
            if (skipped > 0)
            {
                //non-quad topology: all-or-nothing — roll the leaf back so its
                //native renderer keeps drawing it whole (fallback path)
                _skippedPairs += skipped;
                _quads.RemoveRange(range.start, range.count);
                if (segCreated && _quads.Count == seg.start)
                    _segments.RemoveAt(_segments.Count - 1);
                else
                    seg.count = _quads.Count - seg.start;
                return;
            }
            StampClipIndex(range.start, range.count, leaf.clipIndex);
            seg.count = _quads.Count - seg.start;
            _leaves.Add(range);
        }

        void StampClipIndex(int start, int count, uint clipIndex)
        {
            if (clipIndex == 0)
                return;
            for (int i = start; i < start + count; i++)
            {
                QuadInstance q = _quads[i];
                q.clipIndex = clipIndex;
                _quads[i] = q;
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
                if (range == null || _buffer == null)
                    return false;

                Mesh mesh = graphics.mesh;
                if (mesh == null)
                    return false;

                mesh.GetVertices(sVerts);
                mesh.GetUVs(0, sUVs);
                mesh.GetColors(sColors);
                mesh.GetTriangles(sTris, 0);

                Matrix4x4 m = _container.cachedTransform.worldToLocalMatrix
                            * graphics.gameObject.transform.localToWorldMatrix;

                sLeafScratch.Clear();
                int skipped;
                int rebuilt = QuadReassembler.Append(sLeafScratch, sVerts, sUVs, sColors, sTris, m, _drawOffset, range.flags, out skipped);
                if (rebuilt != range.count || skipped > 0)
                    return false; //count changed or topology went non-quad: recompile

                for (int i = 0; i < rebuilt; i++)
                {
                    QuadInstance q = sLeafScratch[i];
                    q.clipIndex = range.clipIndex;
                    _quads[range.start + i] = q;
                    _uploadArray[range.start + i] = q;
                }
                _buffer.SetData(_uploadArray, range.start, range.start, rebuilt);
                return true;
            }
        }

        /// <summary>
        /// Submission tier: one instanced draw per segment. Call once per frame after
        /// the stage update.
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
                        return;
                    Flush();
                }

                if (_buffer == null)
                    return;

                //recomputed every frame: in in-place mode the container itself moves
                //when its ScrollPane scrolls, while the mask window stays put
                ComputeExternalWindow();

                Matrix4x4 l2w = _container.cachedTransform.localToWorldMatrix;
                int layer = _container.gameObject.layer;

                for (int i = 0; i < _segments.Count; i++)
                {
                    Segment seg = _segments[i];
                    if (seg.count == 0)
                        continue;

                    seg.props.SetMatrix("_ContainerL2W", l2w);
                    seg.props.SetVector("_ScrollOffset", _scrollOffset);
                    seg.props.SetVector("_ClipRect", _clipRect);
                    seg.props.SetVector("_ClipSoft", _clipSoft);
                    seg.props.SetFloat("_SegZ", seg.z);

                    var bounds = new Bounds(new Vector3(0, 0, seg.z), new Vector3(100000, 100000, 1));
                    var rp = new RenderParams(seg.material)
                    {
                        matProps = seg.props,
                        worldBounds = bounds,
                        layer = layer,
                        receiveShadows = false,
                        shadowCastingMode = ShadowCastingMode.Off
                    };
                    Graphics.RenderMeshPrimitives(rp, _quadMesh, 0, seg.count);
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
            _segments.Clear();
            _leaves.Clear();
            _quads.Clear();
            if (_quadMesh != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_quadMesh);
                else
                    UnityEngine.Object.DestroyImmediate(_quadMesh);
                _quadMesh = null;
            }
        }
    }
}
