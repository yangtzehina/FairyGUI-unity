using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2020_1_OR_NEWER
using Unity.Profiling;
#endif

/// <summary>
/// Proof-of-concept GPU-instanced renderer for FairyGUI content (ProGPU-style).
///
/// Extract() walks a container's visible leaves, decomposes their meshes into quads
/// (rect + four corner UVs + color, in container-local space) and groups them by
/// texture. Render() then issues ONE instanced draw per texture group from a shared
/// unit quad. Scrolling is a uniform write (SetScrollOffset), a single text change is
/// a partial instance-buffer upload (UpdateLeaf) — no CPU vertex baking, which is the
/// architectural claim under test versus MergedBatch.
///
/// PoC limits (documented, acceptable for measurement): draw order is per texture
/// group (fine when cross-texture content does not overlap); quads are assumed
/// axis-aligned (no rotated elements); per-quad color comes from the first vertex.
/// </summary>
public class InstancedUIRenderer : IDisposable
{
    [Serializable]
    public struct QuadInstance
    {
        public Vector4 rect;
        public Vector4 uvA;
        public Vector4 uvB;
        public Color color;
    }

    class Group
    {
        public Texture texture;
        public bool alphaTexMode;
        public float z;
        public readonly List<QuadInstance> quads = new List<QuadInstance>();
        public QuadInstance[] uploadArray;
        public ComputeBuffer buffer;
        public Material material;
        public MaterialPropertyBlock props;
    }

    class LeafRange
    {
        public NGraphics graphics;
        public Group group;
        public int start;
        public int count;
    }

    readonly List<Group> _groups = new List<Group>();
    readonly List<LeafRange> _leaves = new List<LeafRange>();
    readonly Dictionary<Texture, Material> _materialCache = new Dictionary<Texture, Material>();
    Container _container;
    Mesh _quadMesh;
    Shader _shader;
    Vector2 _scrollOffset;
    Vector4 _clipRect;
    Vector2 _drawOffset;

    static readonly List<Vector3> sVerts = new List<Vector3>();
    static readonly List<Vector2> sUVs = new List<Vector2>();
    static readonly List<Color32> sColors = new List<Color32>();
    static readonly List<int> sTris = new List<int>();
    static readonly int[] sQuadIndices = new int[4];

#if UNITY_2020_1_OR_NEWER
    static readonly ProfilerMarker sExtract = new ProfilerMarker("InstancedUI.Extract");
    static readonly ProfilerMarker sScroll = new ProfilerMarker("InstancedUI.Scroll");
    static readonly ProfilerMarker sLeafUpdate = new ProfilerMarker("InstancedUI.LeafUpdate");
    static readonly ProfilerMarker sRender = new ProfilerMarker("InstancedUI.Render");
#endif

    public int groupCount { get { return _groups.Count; } }

    public int quadCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _groups.Count; i++)
                n += _groups[i].quads.Count;
            return n;
        }
    }

    /// <summary>
    /// drawOffset shifts the replica in container space so it can render beside the
    /// original for visual comparison.
    /// </summary>
    public InstancedUIRenderer(Container container, Vector2 drawOffset)
    {
        _container = container;
        _drawOffset = drawOffset;
        _shader = Shader.Find("FairyGUI/InstancedUIPoC");

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

    public void Extract()
    {
#if UNITY_2020_1_OR_NEWER
        using (sExtract.Auto())
#endif
        {
            //segments are rebuilt from scratch: layout may change between extracts
            foreach (var g in _groups)
            {
                if (g.buffer != null) g.buffer.Release();
            }
            _groups.Clear();
            _leaves.Clear();

            Matrix4x4 worldToLocal = _container.cachedTransform.worldToLocalMatrix;
            ExtractContainer(_container, worldToLocal);

            foreach (var g in _groups)
            {
                if (g.quads.Count == 0)
                    continue;
                g.uploadArray = g.quads.ToArray();
                g.buffer = new ComputeBuffer(g.uploadArray.Length, 16 * 4, ComputeBufferType.Structured);
                g.buffer.SetData(g.uploadArray);
                Material mat;
                if (!_materialCache.TryGetValue(g.texture, out mat))
                {
                    mat = new Material(_shader);
                    mat.hideFlags = HideFlags.DontSave;
                    mat.mainTexture = g.texture;
                    mat.SetFloat("_AlphaTexMode", g.alphaTexMode ? 1f : 0f);
                    _materialCache.Add(g.texture, mat);
                }
                g.material = mat;
                g.props = new MaterialPropertyBlock();
                g.props.SetBuffer("_Instances", g.buffer);
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

        //segment on texture change, preserving tree draw order; segments are separated
        //by a small z step so transparent sorting keeps them in submission order.
        //(a real v4 would run FairyBatching-style adjacency sorting to shrink segments)
        Group group = _groups.Count > 0 ? _groups[_groups.Count - 1] : null;
        if (group == null || group.texture != tex)
        {
            group = new Group
            {
                texture = tex,
                alphaTexMode = graphics.shader == ShaderConfig.textShader,
                z = -0.5f * _groups.Count
            };
            _groups.Add(group);
        }

        var range = new LeafRange { graphics = graphics, group = group, start = group.quads.Count };
        Matrix4x4 m = worldToLocal * graphics.gameObject.transform.localToWorldMatrix;
        AppendQuads(group, mesh, m);
        range.count = group.quads.Count - range.start;
        _leaves.Add(range);
    }

    void AppendQuads(Group group, Mesh mesh, Matrix4x4 toLocal)
    {
        mesh.GetVertices(sVerts);
        mesh.GetUVs(0, sUVs);
        mesh.GetColors(sColors);
        mesh.GetTriangles(sTris, 0);

        //reconstruct quads from consecutive triangle pairs: handles both independent
        //4-vertex quads (text/images) and shared-vertex grids (scale9 = 16 verts,
        //9 quads sharing corners); a pair not covering exactly 4 distinct vertices
        //is skipped (non-quad geometry needs the mesh fallback path in a real v4)
        for (int t = 0; t + 5 < sTris.Count; t += 6)
        {
            int distinct = 0;
            for (int k = 0; k < 6; k++)
            {
                int idx = sTris[t + k];
                bool seen = false;
                for (int d = 0; d < distinct; d++)
                {
                    if (sQuadIndices[d] == idx) { seen = true; break; }
                }
                if (!seen)
                {
                    if (distinct == 4) { distinct = 5; break; }
                    sQuadIndices[distinct++] = idx;
                }
            }
            if (distinct != 4)
                continue;

            Vector2 p0 = toLocal.MultiplyPoint3x4(sVerts[sQuadIndices[0]]);
            Vector2 p1 = toLocal.MultiplyPoint3x4(sVerts[sQuadIndices[1]]);
            Vector2 p2 = toLocal.MultiplyPoint3x4(sVerts[sQuadIndices[2]]);
            Vector2 p3 = toLocal.MultiplyPoint3x4(sVerts[sQuadIndices[3]]);

            Vector2 min = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3));
            Vector2 max = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));
            Vector2 size = max - min;
            if (size.x <= 0 || size.y <= 0)
                continue;

            //assign each source vertex's UV to the unit-quad corner it lands on,
            //so rotated atlas sprites keep correct sampling
            Vector2 uv00 = Vector2.zero, uv10 = Vector2.zero, uv01 = Vector2.zero, uv11 = Vector2.zero;
            for (int k = 0; k < 4; k++)
            {
                Vector2 p = k == 0 ? p0 : k == 1 ? p1 : k == 2 ? p2 : p3;
                bool right = p.x > min.x + size.x * 0.5f;
                bool top = p.y > min.y + size.y * 0.5f;
                Vector2 uv = sUVs[sQuadIndices[k]];
                if (!right && !top) uv00 = uv;
                else if (right && !top) uv10 = uv;
                else if (!right && top) uv01 = uv;
                else uv11 = uv;
            }

            group.quads.Add(new QuadInstance
            {
                rect = new Vector4(min.x + _drawOffset.x, min.y + _drawOffset.y, size.x, size.y),
                uvA = new Vector4(uv00.x, uv00.y, uv10.x, uv10.y),
                uvB = new Vector4(uv01.x, uv01.y, uv11.x, uv11.y),
                color = sColors.Count > 0 ? (Color)sColors[sQuadIndices[0]] : Color.white
            });
        }
    }

    /// <summary>
    /// The architectural claim under test: scrolling merged content costs one vector.
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
    /// Re-extracts one leaf's quads and uploads only that instance range (same-count
    /// content changes, e.g. fixed-length text churn).
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
            if (range == null)
                return false;

            Group group = range.group;
            int before = group.quads.Count;
            Matrix4x4 m = _container.cachedTransform.worldToLocalMatrix
                        * graphics.gameObject.transform.localToWorldMatrix;

            //rebuild into the tail, then copy over the existing range if counts match
            AppendQuads(group, graphics.mesh, m);
            int rebuilt = group.quads.Count - before;
            if (rebuilt != range.count)
            {
                group.quads.RemoveRange(before, rebuilt);
                return false; //count changed: caller should do a full Extract
            }

            for (int i = 0; i < range.count; i++)
                group.uploadArray[range.start + i] = group.quads[before + i];
            group.quads.RemoveRange(before, rebuilt);
            group.buffer.SetData(group.uploadArray, range.start, range.start, range.count);
            return true;
        }
    }

    public void Render()
    {
#if UNITY_2020_1_OR_NEWER
        using (sRender.Auto())
#endif
        {
            Matrix4x4 l2w = _container.cachedTransform.localToWorldMatrix;
            var bounds = new Bounds(Vector3.zero, new Vector3(100000, 100000, 100000));

            for (int i = 0; i < _groups.Count; i++)
            {
                Group g = _groups[i];
                if (g.buffer == null || g.quads.Count == 0)
                    continue;

                g.props.SetMatrix("_ContainerL2W", l2w);
                g.props.SetVector("_ScrollOffset", _scrollOffset);
                g.props.SetVector("_ClipRect", _clipRect);
                g.props.SetFloat("_SegZ", g.z);

                var segBounds = new Bounds(new Vector3(0, 0, g.z), new Vector3(100000, 100000, 1));
                var rp = new RenderParams(g.material)
                {
                    matProps = g.props,
                    worldBounds = segBounds,
                    layer = _container.gameObject.layer,
                    receiveShadows = false,
                    shadowCastingMode = ShadowCastingMode.Off
                };
                Graphics.RenderMeshPrimitives(rp, _quadMesh, 0, g.quads.Count);
            }
        }
    }

    public void Dispose()
    {
        foreach (var g in _groups)
        {
            if (g.buffer != null) g.buffer.Release();
        }
        foreach (var kv in _materialCache)
            UnityEngine.Object.Destroy(kv.Value);
        _materialCache.Clear();
        _groups.Clear();
        _leaves.Clear();
        if (_quadMesh != null)
            UnityEngine.Object.Destroy(_quadMesh);
    }
}
