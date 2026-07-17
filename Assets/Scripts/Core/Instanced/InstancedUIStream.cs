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
    /// Current limits: single clip rect (M3), no fallback interleaving markers (M5).
    /// Elements whose triangle pairs are not quads are skipped and reported via
    /// lastSkippedPairs.
    /// </summary>
    public class InstancedUIStream : IDisposable
    {
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
        }

        struct PendingLeaf
        {
            public NGraphics graphics;
            public Matrix4x4 matrix;
            public Texture texture;
            public uint flags;
        }

        readonly List<Segment> _segments = new List<Segment>();
        readonly List<LeafRange> _leaves = new List<LeafRange>();
        readonly List<PendingLeaf> _pending = new List<PendingLeaf>();
        readonly List<AdjacencyEntry> _entries = new List<AdjacencyEntry>();
        readonly List<QuadInstance> _quads = new List<QuadInstance>();
        readonly Dictionary<Texture, Material> _materialCache = new Dictionary<Texture, Material>();

        Container _container;
        bool _sortAdjacency;
        Mesh _quadMesh;
        Shader _shader;
        ComputeBuffer _buffer;
        QuadInstance[] _uploadArray;
        Vector2 _scrollOffset;
        Vector4 _clipRect;
        Vector2 _drawOffset;
        int _skippedPairs;

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
        /// Triangle pairs that could not be reassembled as quads in the last Extract —
        /// content for the mesh fallback path (M5).
        /// </summary>
        public int lastSkippedPairs { get { return _skippedPairs; } }

        /// <summary>
        /// drawOffset shifts the stream in container space (0 for in-place rendering,
        /// non-zero for side-by-side verification replicas). sortAdjacency applies the
        /// FairyBatching adjacency sort during Extract to shrink segment count.
        /// </summary>
        public InstancedUIStream(Container container, Vector2 drawOffset = default, bool sortAdjacency = true)
        {
            _container = container;
            _drawOffset = drawOffset;
            _sortAdjacency = sortAdjacency;
            _shader = Shader.Find("FairyGUI/InstancedUI");

            _quadMesh = new Mesh();
            _quadMesh.vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(0, 1, 0), new Vector3(1, 1, 0)
            };
            _quadMesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            _quadMesh.UploadMeshData(false);

            Rect? clip = container.clipRect;
            if (clip != null)
            {
                Rect r = (Rect)clip;
                //container-local Unity coords are y-down (FairyGUI negates y)
                _clipRect = new Vector4(r.xMin + drawOffset.x, -r.yMax + drawOffset.y,
                    r.xMax + drawOffset.x, -r.yMin + drawOffset.y);
            }
            else
                _clipRect = new Vector4(float.MinValue, float.MinValue, float.MaxValue, float.MaxValue);
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
                _skippedPairs = 0;

                Matrix4x4 worldToLocal = _container.cachedTransform.worldToLocalMatrix;
                ExtractContainer(_container, worldToLocal);

                if (_sortAdjacency)
                    AdjacencySorter.Sort(_entries);
                for (int i = 0; i < _entries.Count; i++)
                    AppendLeaf(_pending[_entries[i].payload]);

                if (_buffer != null)
                    _buffer.Release();
                _buffer = null;
                if (_quads.Count == 0)
                    return;

                _uploadArray = _quads.ToArray();
                _buffer = new ComputeBuffer(_uploadArray.Length, QuadInstance.Stride, ComputeBufferType.Structured);
                _buffer.SetData(_uploadArray);

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
                    seg.props.SetInt("_InstanceStart", seg.start);
                }
            }
        }

        void ExtractContainer(Container container, Matrix4x4 worldToLocal)
        {
            int cnt = container.numChildren;
            for (int i = 0; i < cnt; i++)
            {
                DisplayObject child = container.GetChildAt(i);
                if (!child.visible)
                    continue;

                if (child.graphics != null && child.graphics.texture != null)
                    ExtractLeaf(child.graphics, worldToLocal);

                if (child.graphics != null && child.graphics.subInstances != null)
                {
                    foreach (var sub in child.graphics.subInstances)
                        if (sub.texture != null)
                            ExtractLeaf(sub, worldToLocal);
                }

                if (child is Container c)
                    ExtractContainer(c, worldToLocal);
            }
        }

        void ExtractLeaf(NGraphics graphics, Matrix4x4 worldToLocal)
        {
            Mesh mesh = graphics.mesh;
            if (mesh == null || mesh.vertexCount == 0)
                return;
            Texture tex = graphics.texture.nativeTexture;
            if (tex == null)
                return;

            bool alphaTex = graphics.shader == ShaderConfig.textShader;
            uint flags = alphaTex ? QuadInstance.FlagAlphaTexture : 0u;
            Matrix4x4 m = worldToLocal * graphics.gameObject.transform.localToWorldMatrix;

            //container-local AABB for the overlap test (transform the mesh AABB's
            //4 xy corners so rotated leaves stay conservative)
            Bounds mb = mesh.bounds;
            Vector2 c0 = m.MultiplyPoint3x4(new Vector3(mb.min.x, mb.min.y, 0));
            Vector2 c1 = m.MultiplyPoint3x4(new Vector3(mb.max.x, mb.min.y, 0));
            Vector2 c2 = m.MultiplyPoint3x4(new Vector3(mb.min.x, mb.max.y, 0));
            Vector2 c3 = m.MultiplyPoint3x4(new Vector3(mb.max.x, mb.max.y, 0));
            Vector2 bmin = Vector2.Min(Vector2.Min(c0, c1), Vector2.Min(c2, c3));
            Vector2 bmax = Vector2.Max(Vector2.Max(c0, c1), Vector2.Max(c2, c3));

            _entries.Add(new AdjacencyEntry
            {
                key = tex,
                x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                payload = _pending.Count
            });
            _pending.Add(new PendingLeaf { graphics = graphics, matrix = m, texture = tex, flags = flags });
        }

        void AppendLeaf(PendingLeaf leaf)
        {
            //segment on texture change in (sorted) submission order
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
            }

            Mesh mesh = leaf.graphics.mesh;
            mesh.GetVertices(sVerts);
            mesh.GetUVs(0, sUVs);
            mesh.GetColors(sColors);
            mesh.GetTriangles(sTris, 0);

            var range = new LeafRange { graphics = leaf.graphics, start = _quads.Count, flags = leaf.flags };
            int skipped;
            range.count = QuadReassembler.Append(_quads, sVerts, sUVs, sColors, sTris, leaf.matrix, _drawOffset, leaf.flags, out skipped);
            _skippedPairs += skipped;
            seg.count = _quads.Count - seg.start;
            _leaves.Add(range);
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
                if (rebuilt != range.count)
                    return false;

                for (int i = 0; i < rebuilt; i++)
                {
                    _quads[range.start + i] = sLeafScratch[i];
                    _uploadArray[range.start + i] = sLeafScratch[i];
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
                if (_buffer == null)
                    return;

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
            if (_buffer != null)
            {
                _buffer.Release();
                _buffer = null;
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
