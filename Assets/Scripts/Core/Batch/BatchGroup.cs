using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// 管理同材质元素的合并 Mesh
    /// 将多个 NGraphics 的顶点数据合并到一个 Mesh 中，减少 DrawCall
    /// </summary>
    public class BatchGroup
    {
        public Material material;
        public Mesh combinedMesh;
        public MeshRenderer meshRenderer;
        public MeshFilter meshFilter;
        public GameObject gameObject;

        public List<BatchMember> members = new List<BatchMember>();
        public bool isDirty = true;

        // 顶点数据缓冲（预分配以减少 GC）
        private List<Vector3> _vertices = new List<Vector3>(256);
        private List<Color32> _colors = new List<Color32>(256);
        private List<Vector2> _uvs = new List<Vector2>(256);
        private List<int> _triangles = new List<int>(384);

        public BatchGroup(Material mat, Transform parent)
        {
            material = mat;

            // 创建渲染 GameObject
            gameObject = new GameObject("BatchGroup_" + (mat != null ? mat.name : "null"));
            gameObject.transform.SetParent(parent, false);
            gameObject.layer = parent.gameObject.layer;

            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            combinedMesh = new Mesh();
            combinedMesh.name = "CombinedMesh_" + (mat != null ? mat.name : "null");
            combinedMesh.MarkDynamic();
            meshFilter.sharedMesh = combinedMesh;

            // 设置隐藏标志
            meshFilter.hideFlags = DisplayObject.hideFlags;
            meshRenderer.hideFlags = DisplayObject.hideFlags;
            combinedMesh.hideFlags = DisplayObject.hideFlags;
        }

        /// <summary>
        /// 添加成员到批处理组
        /// </summary>
        public void AddMember(NGraphics graphics, Matrix4x4 worldMatrix)
        {
            var member = new BatchMember
            {
                graphics = graphics,
                worldMatrix = worldMatrix,
                vertexOffset = 0,
                indexOffset = 0
            };
            members.Add(member);
            isDirty = true;
        }

        /// <summary>
        /// 清空所有成员
        /// </summary>
        public void Clear()
        {
            foreach (var member in members)
            {
                if (member.graphics != null)
                    member.graphics.SetBatchedState(false);
            }
            members.Clear();
            isDirty = true;
        }

        /// <summary>
        /// 重建合并 Mesh
        /// </summary>
        public void RebuildMesh()
        {
            if (!isDirty) return;
            isDirty = false;

            _vertices.Clear();
            _colors.Clear();
            _uvs.Clear();
            _triangles.Clear();

            int vertexOffset = 0;

            foreach (var member in members)
            {
                member.vertexOffset = vertexOffset;

                // 从 NGraphics 导出顶点数据
                var vb = member.graphics.ExportVertices(member.worldMatrix);
                if (vb == null) continue;

                // 追加顶点数据
                int vertCount = vb.vertices.Count;
                for (int i = 0; i < vertCount; i++)
                {
                    _vertices.Add(vb.vertices[i]);
                    _colors.Add(vb.colors[i]);
                    _uvs.Add(vb.uvs[i]);
                }

                // 追加索引数据（偏移）
                member.indexOffset = _triangles.Count;
                int triCount = vb.triangles.Count;
                for (int i = 0; i < triCount; i++)
                {
                    _triangles.Add(vb.triangles[i] + vertexOffset);
                }

                vertexOffset += vertCount;

                // 归还 VertexBuffer 到池
                vb.End();

                // 标记为已批处理
                member.graphics.SetBatchedState(true);
            }

            // 更新合并 Mesh
            combinedMesh.Clear();
            if (_vertices.Count > 0)
            {
                combinedMesh.SetVertices(_vertices);
                combinedMesh.SetColors(_colors);
                combinedMesh.SetUVs(0, _uvs);
                combinedMesh.SetTriangles(_triangles, 0);
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Clear();
            if (combinedMesh != null)
            {
                Object.Destroy(combinedMesh);
                combinedMesh = null;
            }
            if (gameObject != null)
            {
                Object.Destroy(gameObject);
                gameObject = null;
            }
        }
    }

    /// <summary>
    /// 批处理成员，记录单个 NGraphics 在合并 Mesh 中的位置
    /// </summary>
    public class BatchMember
    {
        public NGraphics graphics;
        public Matrix4x4 worldMatrix;
        public int vertexOffset;
        public int indexOffset;
    }
}
