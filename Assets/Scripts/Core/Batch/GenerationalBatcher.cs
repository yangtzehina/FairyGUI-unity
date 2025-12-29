using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// 分代批处理管理器
    /// 借鉴分代 GC 思想，对稳定的 UI 元素进行 Mesh 合并
    ///
    /// 分代模型：
    /// - Gen0 (年轻代): 新创建或刚变化的元素，独立渲染
    /// - Gen1 (中间代): 稳定一段时间的元素，候选合批
    /// - Gen2 (老年代): 长时间稳定的元素，合并 Mesh 渲染
    /// </summary>
    public class GenerationalBatcher
    {
        /// <summary>
        /// Gen0 → Gen1 晋升阈值（帧数）
        /// 默认 30 帧，约 0.5 秒 @ 60fps
        /// </summary>
        public int PromotionThresholdGen1 = 30;

        /// <summary>
        /// Gen1 → Gen2 晋升阈值（帧数）
        /// 默认 120 帧，约 2 秒 @ 60fps
        /// </summary>
        public int PromotionThresholdGen2 = 120;

        private List<NGraphics> _gen0Elements = new List<NGraphics>();
        private List<NGraphics> _gen1Elements = new List<NGraphics>();
        private List<NGraphics> _gen2Elements = new List<NGraphics>();

        private MeshBatcher _gen2Batcher;
        private Transform _root;
        private bool _gen2Dirty;

        // 临时列表，避免迭代时修改
        private List<NGraphics> _tempPromoteList = new List<NGraphics>();

        public GenerationalBatcher(Transform root)
        {
            _root = root;
            _gen2Batcher = new MeshBatcher(root);
        }

        /// <summary>
        /// 添加元素（初始为 Gen0）
        /// </summary>
        public void AddElement(NGraphics graphics)
        {
            if (graphics == null) return;

            graphics._generation = 0;
            graphics._stableFrameCount = 0;
            graphics._isDirtyThisFrame = false;
            _gen0Elements.Add(graphics);
        }

        /// <summary>
        /// 移除元素
        /// </summary>
        public void RemoveElement(NGraphics graphics)
        {
            if (graphics == null) return;

            switch (graphics._generation)
            {
                case 0:
                    _gen0Elements.Remove(graphics);
                    break;
                case 1:
                    _gen1Elements.Remove(graphics);
                    break;
                case 2:
                    _gen2Elements.Remove(graphics);
                    graphics.SetBatchedState(false);
                    _gen2Dirty = true;
                    break;
            }

            graphics._generation = 0;
            graphics._stableFrameCount = 0;
        }

        /// <summary>
        /// 标记元素变化（触发降级）
        /// </summary>
        public void MarkDirty(NGraphics graphics)
        {
            if (graphics == null) return;

            // 从当前代移除
            if (graphics._generation == 2)
            {
                _gen2Elements.Remove(graphics);
                graphics.SetBatchedState(false);
                _gen2Dirty = true;
            }
            else if (graphics._generation == 1)
            {
                _gen1Elements.Remove(graphics);
            }

            // 降级到 Gen0
            if (graphics._generation != 0)
            {
                graphics._generation = 0;
                if (!_gen0Elements.Contains(graphics))
                    _gen0Elements.Add(graphics);
            }

            graphics._stableFrameCount = 0;
            graphics._isDirtyThisFrame = true;
        }

        /// <summary>
        /// 每帧更新，处理晋升和重建
        /// </summary>
        public void Update()
        {
            bool hasPromotion = false;
            _tempPromoteList.Clear();

            // 1. 更新 Gen0，检查晋升到 Gen1
            for (int i = _gen0Elements.Count - 1; i >= 0; i--)
            {
                var elem = _gen0Elements[i];
                if (elem == null)
                {
                    _gen0Elements.RemoveAt(i);
                    continue;
                }

                if (!elem._isDirtyThisFrame)
                {
                    elem._stableFrameCount++;
                    if (elem._stableFrameCount >= PromotionThresholdGen1)
                    {
                        _tempPromoteList.Add(elem);
                    }
                }
                elem._isDirtyThisFrame = false;
            }

            // 执行 Gen0 → Gen1 晋升
            foreach (var elem in _tempPromoteList)
            {
                _gen0Elements.Remove(elem);
                _gen1Elements.Add(elem);
                elem._generation = 1;
            }

            _tempPromoteList.Clear();

            // 2. 更新 Gen1，检查晋升到 Gen2
            for (int i = _gen1Elements.Count - 1; i >= 0; i--)
            {
                var elem = _gen1Elements[i];
                if (elem == null)
                {
                    _gen1Elements.RemoveAt(i);
                    continue;
                }

                if (!elem._isDirtyThisFrame)
                {
                    elem._stableFrameCount++;
                    if (elem._stableFrameCount >= PromotionThresholdGen2)
                    {
                        _tempPromoteList.Add(elem);
                        hasPromotion = true;
                    }
                }
                elem._isDirtyThisFrame = false;
            }

            // 执行 Gen1 → Gen2 晋升
            foreach (var elem in _tempPromoteList)
            {
                _gen1Elements.Remove(elem);
                _gen2Elements.Add(elem);
                elem._generation = 2;
            }

            // 3. 如果 Gen2 有变化，重建合并 Mesh
            if (hasPromotion || _gen2Dirty)
            {
                RebuildGen2();
                _gen2Dirty = false;
            }
        }

        /// <summary>
        /// 重建 Gen2 的合并 Mesh
        /// </summary>
        private void RebuildGen2()
        {
            _gen2Batcher.RebuildBatch(_gen2Elements);
        }

        /// <summary>
        /// 设置渲染顺序
        /// </summary>
        public void SetRenderingOrder(UpdateContext context)
        {
            // Gen0 和 Gen1: 独立设置
            foreach (var elem in _gen0Elements)
            {
                if (elem != null && elem.meshRenderer != null && elem.meshRenderer.enabled)
                    elem.meshRenderer.sortingOrder = context.renderingOrder++;
            }
            foreach (var elem in _gen1Elements)
            {
                if (elem != null && elem.meshRenderer != null && elem.meshRenderer.enabled)
                    elem.meshRenderer.sortingOrder = context.renderingOrder++;
            }

            // Gen2: 通过 Batcher 设置
            _gen2Batcher.SetRenderingOrder(context);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            foreach (var elem in _gen2Elements)
            {
                if (elem != null)
                    elem.SetBatchedState(false);
            }
            _gen0Elements.Clear();
            _gen1Elements.Clear();
            _gen2Elements.Clear();
            _gen2Batcher.Dispose();
        }

        /// <summary>
        /// 强制将所有元素重置到 Gen0
        /// </summary>
        public void Reset()
        {
            // 将 Gen2 元素恢复独立渲染
            foreach (var elem in _gen2Elements)
            {
                if (elem != null)
                {
                    elem.SetBatchedState(false);
                    elem._generation = 0;
                    elem._stableFrameCount = 0;
                    _gen0Elements.Add(elem);
                }
            }
            _gen2Elements.Clear();

            // 将 Gen1 元素移回 Gen0
            foreach (var elem in _gen1Elements)
            {
                if (elem != null)
                {
                    elem._generation = 0;
                    elem._stableFrameCount = 0;
                    _gen0Elements.Add(elem);
                }
            }
            _gen1Elements.Clear();

            _gen2Dirty = true;
        }

        // 统计信息
        public int Gen0Count => _gen0Elements.Count;
        public int Gen1Count => _gen1Elements.Count;
        public int Gen2Count => _gen2Elements.Count;
        public int BatchCount => _gen2Batcher.BatchCount;
        public int TotalCount => Gen0Count + Gen1Count + Gen2Count;
    }
}
