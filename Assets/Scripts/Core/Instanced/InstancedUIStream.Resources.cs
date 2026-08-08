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
    /// RESOURCE layer (design §3): segment GameObjects/meshes/materials and
    /// their pools, and the instance-data uploads for both backends.
    /// </summary>
    public partial class InstancedUIStream
    {

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
    }
}
