using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// Mesh 批处理管理器
    /// 参考 Unity UGUI Canvas 的批处理机制，将同材质的 UI 元素合并渲染
    /// </summary>
    public class MeshBatcher
    {
        private Dictionary<Material, BatchGroup> _batchGroups;
        private Transform _root;
        private List<Material> _materialsToRemove;

        public MeshBatcher(Transform root)
        {
            _root = root;
            _batchGroups = new Dictionary<Material, BatchGroup>();
            _materialsToRemove = new List<Material>();
        }

        /// <summary>
        /// 重建批处理
        /// </summary>
        public void RebuildBatch(List<NGraphics> elements)
        {
            // 清理现有批次
            foreach (var group in _batchGroups.Values)
            {
                group.Clear();
            }

            // 按材质分组
            foreach (var graphics in elements)
            {
                if (graphics == null || graphics.material == null) continue;

                var mat = graphics.material;

                if (!_batchGroups.TryGetValue(mat, out var group))
                {
                    group = new BatchGroup(mat, _root);
                    _batchGroups[mat] = group;
                }

                // 获取世界变换矩阵
                var worldMatrix = graphics.gameObject.transform.localToWorldMatrix;
                group.AddMember(graphics, worldMatrix);
            }

            // 重建所有脏 Mesh
            foreach (var group in _batchGroups.Values)
            {
                if (group.members.Count > 0)
                {
                    group.RebuildMesh();
                    group.gameObject.SetActive(true);
                }
                else
                {
                    group.gameObject.SetActive(false);
                }
            }

            // 清理空的批次组
            CleanupEmptyGroups();
        }

        /// <summary>
        /// 清理空的批次组
        /// </summary>
        private void CleanupEmptyGroups()
        {
            _materialsToRemove.Clear();

            foreach (var kvp in _batchGroups)
            {
                if (kvp.Value.members.Count == 0)
                {
                    _materialsToRemove.Add(kvp.Key);
                }
            }

            foreach (var mat in _materialsToRemove)
            {
                if (_batchGroups.TryGetValue(mat, out var group))
                {
                    group.Dispose();
                    _batchGroups.Remove(mat);
                }
            }
        }

        /// <summary>
        /// 设置渲染顺序
        /// </summary>
        public void SetRenderingOrder(UpdateContext context)
        {
            foreach (var group in _batchGroups.Values)
            {
                if (group.members.Count > 0 && group.meshRenderer != null)
                {
                    group.meshRenderer.sortingOrder = context.renderingOrder++;
                }
            }
        }

        /// <summary>
        /// 标记元素需要更新
        /// </summary>
        public void MarkDirty(NGraphics graphics)
        {
            if (graphics != null && graphics.material != null &&
                _batchGroups.TryGetValue(graphics.material, out var group))
            {
                group.isDirty = true;
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            foreach (var group in _batchGroups.Values)
            {
                group.Dispose();
            }
            _batchGroups.Clear();
        }

        /// <summary>
        /// 批次组数量
        /// </summary>
        public int BatchCount => _batchGroups.Count;

        /// <summary>
        /// 获取总顶点数（调试用）
        /// </summary>
        public int TotalVertexCount
        {
            get
            {
                int count = 0;
                foreach (var group in _batchGroups.Values)
                {
                    if (group.combinedMesh != null)
                        count += group.combinedMesh.vertexCount;
                }
                return count;
            }
        }
    }
}
