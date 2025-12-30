# FairyGUI-Unity DrawCall 优化方案对比分析

## 当前架构问题

### 渲染机制
FairyGUI 当前使用 **每个 UI 元素 = 独立 GameObject + MeshRenderer** 的架构：

```
DisplayObject
  └── NGraphics
        ├── GameObject
        ├── MeshFilter + Mesh (动态)
        └── MeshRenderer + Material
```

### DrawCall 产生原因

| 原因 | 影响程度 | 说明 |
|------|---------|------|
| 独立 MeshRenderer | **极高** | 每个 UI 元素都是独立渲染单元 |
| 材质变体 (18种) | 高 | Shader 关键字组合导致材质切换 |
| 纹理切换 | 高 | 不同图集导致材质不同 |
| BlendMode 变化 | 中 | 混合模式改变需要新材质 |
| 裁剪状态 | 中 | 进入/离开裁剪区域改变材质 |

### 现有 FairyBatching 的局限

现有的 `fairyBatching` 只是**重排序 sortingOrder**，依赖 Unity 动态合批：
- Unity 动态合批限制：顶点数 < 300，相同材质
- 无法跨材质合批
- 每个元素仍是独立 DrawCall

---

## 优化方案对比

### 方案一：Mesh 合并 (推荐)

**原理**：将同材质的多个 UI 元素合并为单个 Mesh，使用一个 MeshRenderer 渲染。

```
优化前: 100个图片 → 100个 MeshRenderer → 100 DrawCall
优化后: 100个图片 → 1个合并 Mesh → 1-2 DrawCall
```

#### 实现方式

```csharp
// 新增 BatchedMeshRenderer 类
public class BatchedMeshRenderer
{
    private Mesh _combinedMesh;
    private MeshRenderer _renderer;

    public void RebuildBatch(List<NGraphics> elements)
    {
        // 按材质分组
        var groups = GroupByMaterial(elements);

        // 合并顶点数据（带世界坐标变换）
        foreach (var group in groups)
        {
            CombineVertices(group);
        }
    }
}

// Container.cs 修改
public bool meshBatching { get; set; } // 新增属性，默认 false
```

#### 优点
- **DrawCall 减少 90%+**
- 兼容所有渲染管线 (Built-in/URP/HDRP)
- 可以 opt-in 启用，向后兼容
- 不需要修改 Shader

#### 缺点
- UI 变化时需要重建 Mesh（有 CPU 开销）
- 内存略增（合并后的大 Mesh）
- 实现复杂度中等

#### 向后兼容性
| 方面 | 兼容性 |
|------|--------|
| 现有 API | ✅ 完全兼容，新增 `meshBatching` 属性 |
| 现有行为 | ✅ 默认关闭，不影响现有项目 |
| 升级成本 | ✅ 零成本，可选启用 |

#### 关键代码改动

| 文件 | 改动 |
|------|------|
| `Core/NGraphics.cs` | 新增 `ExportToBatch()` 方法，导出顶点数据 |
| `Core/Container.cs` | 新增 `meshBatching` 属性和合批逻辑 |
| 新增 `Core/BatchedMeshRenderer.cs` | 合并 Mesh 管理 |
| 新增 `Core/MeshBatcher.cs` | 顶点合并算法 |

---

### 方案二：SRP Batcher 兼容

**原理**：修改 Shader 使其符合 SRP Batcher 要求，让 Unity 自动优化。

#### SRP Batcher 要求
1. 所有材质属性必须在 `CBUFFER_START(UnityPerMaterial)` 中
2. 不能使用 `MaterialPropertyBlock`
3. 相同 Shader 变体可以合批

#### 当前问题
```hlsl
// 当前 Shader 只有部分属性在 CBUFFER 中
CBUFFER_START(UnityPerMaterial)
    float4 _ClipBox;      // ✅ 在 CBUFFER 中
    float4 _ClipSoftness; // ✅ 在 CBUFFER 中
CBUFFER_END

float4x4 _ColorMatrix;    // ❌ 不在 CBUFFER 中
float4 _ColorOffset;      // ❌ 不在 CBUFFER 中
```

```csharp
// NGraphics.cs 使用了 MaterialPropertyBlock
_propertyBlock = new MaterialPropertyBlock();  // ❌ 破坏 SRP Batcher
```

#### 需要的修改

```hlsl
// 修改后的 Shader
CBUFFER_START(UnityPerMaterial)
    float4 _ClipBox;
    float4 _ClipSoftness;
    float4x4 _ColorMatrix;  // 移入 CBUFFER
    float4 _ColorOffset;    // 移入 CBUFFER
    float _ColorOption;     // 移入 CBUFFER
CBUFFER_END
```

```csharp
// 移除 MaterialPropertyBlock 使用
// 改为直接设置材质属性或使用顶点属性
```

#### 优点
- 改动量小
- Unity 自动处理
- URP/HDRP 下效果好

#### 缺点
- **只支持 URP/HDRP**，Built-in 无效
- DrawCall 减少有限 (30-50%)
- 需要移除 MaterialPropertyBlock

#### 向后兼容性
| 方面 | 兼容性 |
|------|--------|
| 现有 API | ✅ 完全兼容 |
| 现有行为 | ⚠️ Shader 改动可能影响自定义 Shader |
| Built-in 管线 | ❌ 无优化效果 |

---

### 方案三：GPU Instancing

**原理**：对相同 Mesh 的元素使用 `Graphics.DrawMeshInstanced`。

```csharp
// 适用场景：列表中的重复项
Graphics.DrawMeshInstanced(mesh, 0, material, matrices, count);
```

#### 需要的修改

```hlsl
// Shader 需要支持 Instancing
#pragma multi_compile_instancing

UNITY_INSTANCING_BUFFER_START(Props)
    UNITY_DEFINE_INSTANCED_PROP(float4, _InstanceColor)
    UNITY_DEFINE_INSTANCED_PROP(float4, _InstanceUVRect)
UNITY_INSTANCING_BUFFER_END(Props)
```

#### 优点
- 对列表/网格等重复元素效果极佳
- GPU 开销低

#### 缺点
- **适用场景有限**（只对相同 Mesh 有效）
- 需要修改 Shader
- 需要额外的实例数据管理

#### 向后兼容性
| 方面 | 兼容性 |
|------|--------|
| 现有 API | ✅ 兼容，新增 Instancing 路径 |
| 现有行为 | ✅ 不影响非实例化渲染 |
| 适用范围 | ⚠️ 仅对 GList 等场景有效 |

---

### 方案四：完整 GPU UI 重构

**原理**：彻底重构为类似 Unity UI (UGUI) 的架构，使用 Canvas + CanvasRenderer。

#### 架构变化

```
当前架构:
DisplayObject → NGraphics → MeshRenderer (每个元素独立)

重构后:
UIPanel (Canvas) → 收集所有子元素 → 合并为少量 Mesh → 统一渲染
```

#### 优点
- 理论上性能最优
- 接近 Unity UI 的合批效率

#### 缺点
- **改动量巨大**，几乎重写渲染层
- 开发周期长
- 测试工作量大
- 可能破坏大量现有功能

#### 向后兼容性
| 方面 | 兼容性 |
|------|--------|
| 现有 API | ❌ 可能有大量不兼容 |
| 现有行为 | ❌ 渲染行为可能不同 |
| 升级成本 | ❌ 高，需要大量测试 |

---

## 方案对比总结

| 方案 | DrawCall 减少 | 实现难度 | 向后兼容 | 适用管线 | 推荐度 |
|------|-------------|---------|---------|---------|--------|
| Mesh 合并 | 90%+ | 中 | ✅ 完全 | 全部 | ⭐⭐⭐⭐⭐ |
| SRP Batcher | 30-50% | 低 | ✅ 高 | URP/HDRP | ⭐⭐⭐ |
| GPU Instancing | 50-80%* | 中 | ✅ 高 | 全部 | ⭐⭐⭐ |
| 完整重构 | 95%+ | 极高 | ❌ 低 | 全部 | ⭐⭐ |

*GPU Instancing 仅对特定场景（重复元素）有效

---

## 推荐实施路径

### 阶段一：快速优化 (可选)
如果使用 URP/HDRP，先实施 **SRP Batcher 兼容** 方案，改动小见效快。

### 阶段二：核心优化 (推荐)
实施 **Mesh 合并** 方案：

1. 新增 `BatchedMeshRenderer` 类
2. 修改 `Container` 支持 Mesh 合并
3. 新增 `meshBatching` 属性 (默认 false)
4. 实现脏标记系统优化重建性能

### 阶段三：场景优化 (可选)
对 `GList` 等列表组件实施 **GPU Instancing**。

---

## 关键文件清单

| 文件路径 | 当前作用 | 需要的改动 |
|---------|---------|-----------|
| `Core/NGraphics.cs` | Mesh 和材质管理 | 新增顶点导出方法 |
| `Core/Container.cs` | 批处理调度 | 新增 Mesh 合并逻辑 |
| `Core/Mesh/VertexBuffer.cs` | 顶点数据管理 | 新增世界坐标变换支持 |
| `Core/MaterialManager.cs` | 材质缓存 | 可能需要分组优化 |
| `Resources/Shaders/*.shader` | 渲染 Shader | SRP Batcher/Instancing 支持 |

---

## 风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|-------|------|---------|
| Mesh 重建导致卡顿 | 中 | 中 | 增量更新 + 脏标记 |
| 视觉差异 | 低 | 高 | 充分测试 + 回退机制 |
| 内存增加 | 中 | 低 | Mesh 池化复用 |
| 兼容性问题 | 低 | 中 | opt-in 设计 + 渐进启用 |

---

---

# 详细实现方案：Mesh 合并 + SRP Batcher 优化

> **目标渲染管线**: URP (Universal Render Pipeline)
> **预期效果**: DrawCall 减少 90%+，同时利用 SRP Batcher 进一步优化

---

## 技术原理

### 核心思想

参考 [Unity UGUI 源码](https://github.com/Unity-Technologies/uGUI) 的 Canvas 批处理机制：
- Canvas 收集所有子元素的顶点数据
- 按材质分组合并为少量 Mesh
- 使用少量 MeshRenderer 渲染

### Unity Mesh.CombineMeshes API

根据 [Unity 官方文档](https://docs.unity3d.com/ScriptReference/Mesh.CombineMeshes.html)：

```csharp
public void CombineMeshes(
    CombineInstance[] combine,    // 要合并的网格数组
    bool mergeSubMeshes = true,   // 是否合并为单个子网格
    bool useMatrices = true,      // 是否应用变换矩阵
    bool hasLightmapData = false  // 光照贴图数据
);
```

### SRP Batcher 兼容性

根据 [SRP Batcher 文档](https://docs.unity3d.com/Manual/SRPBatcher.html)，需要满足：
1. 所有材质属性在 `CBUFFER_START(UnityPerMaterial)` 中
2. 不使用 `MaterialPropertyBlock`
3. 相同 Shader 变体可以合批

---

## 架构设计

### 类图

```
Container
  ├── fairyBatching: bool (现有)
  ├── meshBatching: bool (新增)
  └── _meshBatcher: MeshBatcher (新增)

MeshBatcher (新增)
  ├── _batchGroups: Dictionary<Material, BatchGroup>
  ├── RebuildBatch(List<NGraphics>)
  ├── UpdateDirtyElements()
  └── Dispose()

BatchGroup (新增)
  ├── material: Material
  ├── combinedMesh: Mesh
  ├── meshRenderer: MeshRenderer
  ├── members: List<BatchMember>
  ├── isDirty: bool
  └── RebuildMesh()

BatchMember (新增)
  ├── graphics: NGraphics
  ├── vertexOffset: int
  ├── indexOffset: int
  └── worldMatrix: Matrix4x4

NGraphics (修改)
  ├── _isBatched: bool (新增)
  ├── ExportVertices(VertexBuffer, Matrix4x4) (新增)
  └── SetBatchedState(bool) (新增)
```

### 数据流

```
Container.Update()
  │
  ├─ if (meshBatching && batchingDepth == 1)
  │     │
  │     ├─ CollectBatchableElements()
  │     │     └─ 收集所有 NGraphics，按材质分组
  │     │
  │     ├─ _meshBatcher.RebuildBatch(groups)
  │     │     ├─ 遍历每个 BatchGroup
  │     │     ├─ 收集成员顶点数据（世界坐标）
  │     │     ├─ 合并到 combinedMesh
  │     │     └─ 设置成员 _isBatched = true
  │     │
  │     └─ SetRenderingOrder (只设置 BatchGroup 的 renderer)
  │
  └─ else (原有逻辑)
        └─ 每个 NGraphics 独立渲染
```

---

## 核心代码实现

### 1. BatchGroup.cs (新增)

```csharp
// Assets/Scripts/Core/Batch/BatchGroup.cs
using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// 管理同材质元素的合并 Mesh
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

        // 顶点数据缓冲
        private List<Vector3> _vertices = new List<Vector3>(256);
        private List<Color32> _colors = new List<Color32>(256);
        private List<Vector2> _uvs = new List<Vector2>(256);
        private List<int> _triangles = new List<int>(384);

        public BatchGroup(Material mat, Transform parent)
        {
            material = mat;

            // 创建渲染 GameObject
            gameObject = new GameObject("BatchGroup_" + mat.name);
            gameObject.transform.SetParent(parent, false);
            gameObject.layer = parent.gameObject.layer;

            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            combinedMesh = new Mesh();
            combinedMesh.name = "CombinedMesh_" + mat.name;
            combinedMesh.MarkDynamic();
            meshFilter.sharedMesh = combinedMesh;
        }

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

        public void Clear()
        {
            foreach (var member in members)
            {
                member.graphics.SetBatchedState(false);
            }
            members.Clear();
            isDirty = true;
        }

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
                for (int i = 0; i < vb.vertices.Count; i++)
                {
                    _vertices.Add(vb.vertices[i]);
                    _colors.Add(vb.colors[i]);
                    _uvs.Add(vb.uvs[i]);
                }

                // 追加索引数据（偏移）
                member.indexOffset = _triangles.Count;
                for (int i = 0; i < vb.triangles.Count; i++)
                {
                    _triangles.Add(vb.triangles[i] + vertexOffset);
                }

                vertexOffset += vb.vertices.Count;

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

    public class BatchMember
    {
        public NGraphics graphics;
        public Matrix4x4 worldMatrix;
        public int vertexOffset;
        public int indexOffset;
    }
}
```

### 2. MeshBatcher.cs (新增)

```csharp
// Assets/Scripts/Core/Batch/MeshBatcher.cs
using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// Mesh 批处理管理器
    /// 参考 Unity UGUI Canvas 的批处理机制
    /// </summary>
    public class MeshBatcher
    {
        private Dictionary<Material, BatchGroup> _batchGroups;
        private Transform _root;
        private bool _enabled;

        public MeshBatcher(Transform root)
        {
            _root = root;
            _batchGroups = new Dictionary<Material, BatchGroup>();
        }

        /// <summary>
        /// 重建批处理
        /// </summary>
        public void RebuildBatch(List<BatchElement> elements)
        {
            // 清理现有批次
            foreach (var group in _batchGroups.Values)
            {
                group.Clear();
            }

            // 按材质分组
            foreach (var element in elements)
            {
                if (element.owner is NGraphics graphics && graphics.material != null)
                {
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
            }

            // 重建所有脏 Mesh
            foreach (var group in _batchGroups.Values)
            {
                group.RebuildMesh();
            }
        }

        /// <summary>
        /// 设置渲染顺序
        /// </summary>
        public void SetRenderingOrder(UpdateContext context)
        {
            foreach (var group in _batchGroups.Values)
            {
                if (group.members.Count > 0)
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
            if (graphics.material != null &&
                _batchGroups.TryGetValue(graphics.material, out var group))
            {
                group.isDirty = true;
            }
        }

        public void Dispose()
        {
            foreach (var group in _batchGroups.Values)
            {
                group.Dispose();
            }
            _batchGroups.Clear();
        }

        public int BatchCount => _batchGroups.Count;
    }
}
```

### 3. NGraphics.cs 修改

```csharp
// 在 NGraphics 类中新增以下成员和方法

internal bool _isBatched;  // 是否已被批处理

/// <summary>
/// 导出顶点数据用于批处理合并
/// </summary>
public VertexBuffer ExportVertices(Matrix4x4 worldMatrix)
{
    if (_texture == null || _meshFactory == null)
        return null;

    VertexBuffer vb = VertexBuffer.Begin();
    vb.contentRect = _contentRect;
    vb.uvRect = _texture.uvRect;
    vb.vertexColor = new Color32(
        (byte)(_color.r * 255),
        (byte)(_color.g * 255),
        (byte)(_color.b * 255),
        (byte)(_color.a * _alpha * 255)
    );

    _meshFactory.OnPopulateMesh(vb);

    // 变换顶点到世界坐标
    for (int i = 0; i < vb.vertices.Count; i++)
    {
        vb.vertices[i] = worldMatrix.MultiplyPoint3x4(vb.vertices[i]);
    }

    return vb;
}

/// <summary>
/// 设置批处理状态
/// </summary>
public void SetBatchedState(bool batched)
{
    _isBatched = batched;
    if (meshRenderer != null)
    {
        meshRenderer.enabled = !batched;  // 禁用独立渲染
    }
}
```

### 4. Container.cs 修改

```csharp
// 在 Container 类中新增以下成员和修改

private MeshBatcher _meshBatcher;

/// <summary>
/// 启用 Mesh 合并批处理（比 fairyBatching 更激进的优化）
/// </summary>
public bool meshBatching
{
    get { return (_flags & Flags.MeshBatching) != 0; }
    set
    {
        if (value)
            _flags |= Flags.MeshBatching;
        else
            _flags &= ~Flags.MeshBatching;

        if (value && _meshBatcher == null)
            _meshBatcher = new MeshBatcher(cachedTransform);
        else if (!value && _meshBatcher != null)
        {
            _meshBatcher.Dispose();
            _meshBatcher = null;
        }
    }
}

// 在 Flags 枚举中新增
// MeshBatching = 1 << 12,  // 或其他未使用的位

// 修改 SetRenderingOrderAll 方法
private void SetRenderingOrderAll(UpdateContext context)
{
    if ((_flags & Flags.BatchingRequested) != 0)
        DoFairyBatching();

    // 新增：Mesh 合并批处理
    if (meshBatching && _meshBatcher != null && _batchElements != null)
    {
        _meshBatcher.RebuildBatch(_batchElements);
        _meshBatcher.SetRenderingOrder(context);
        return;  // 跳过逐元素设置
    }

    // 原有逻辑...
    if (_mask != null)
        _mask.SetRenderingOrder(context, false);

    // ...
}
```

### 5. Shader 修改 (SRP Batcher 兼容)

```hlsl
// FairyGUI-Image.shader 修改
// 将所有材质属性移入 CBUFFER

CBUFFER_START(UnityPerMaterial)
    float4 _ClipBox;
    float4 _ClipSoftness;
    float4x4 _ColorMatrix;
    float4 _ColorOffset;
    float _ColorOption;
    float _BlendSrcFactor;
    float _BlendDstFactor;
CBUFFER_END
```

---

## 文件改动清单

| 文件 | 操作 | 改动说明 |
|------|------|---------|
| `Core/Batch/BatchGroup.cs` | 新增 | 批处理组管理 |
| `Core/Batch/MeshBatcher.cs` | 新增 | Mesh 合并管理器 |
| `Core/NGraphics.cs` | 修改 | 新增导出方法和批处理状态 |
| `Core/Container.cs` | 修改 | 新增 meshBatching 属性和逻辑 |
| `Core/DisplayObject.cs` | 修改 | Flags 枚举新增 MeshBatching |
| `Resources/Shaders/FairyGUI-Image.shader` | 修改 | SRP Batcher 兼容 |
| `Resources/Shaders/FairyGUI-Text.shader` | 修改 | SRP Batcher 兼容 |
| `Resources/Shaders/FairyGUI-BMFont.shader` | 修改 | SRP Batcher 兼容 |

---

## 使用方式

```csharp
// 启用 Mesh 合并批处理
GComponent panel = UIPackage.CreateObject("包名", "组件名").asCom;
panel.container.meshBatching = true;  // 启用
GRoot.inst.AddChild(panel);

// 或者在 UIPanel 上设置
UIPanel uiPanel = GetComponent<UIPanel>();
uiPanel.container.meshBatching = true;
```

---

## 性能预期

| 场景 | 优化前 DrawCall | 优化后 DrawCall | 减少比例 |
|------|----------------|----------------|---------|
| 100 个图片 (同图集) | 100 | 1 | 99% |
| 50 图片 + 50 文字 | 100 | 2 | 98% |
| 复杂面板 (10种材质) | 200 | 10 | 95% |
| 虚拟列表 1000 项 | 20 | 2-5 | 75-90% |

---

## 参考来源

1. [Unity Draw Call Batching 官方文档](https://docs.unity3d.com/Manual/DrawCallBatching.html)
2. [Unity Mesh.CombineMeshes API](https://docs.unity3d.com/ScriptReference/Mesh.CombineMeshes.html)
3. [Unity UGUI 源码 - Graphic.cs](https://github.com/Unity-Technologies/uGUI/blob/main/com.unity.ugui/Runtime/UGUI/UI/Core/Graphic.cs)
4. [SRP Batcher 官方文档](https://docs.unity3d.com/Manual/SRPBatcher.html)
5. [Unity UI 优化指南](https://learn.unity.com/tutorial/optimizing-unity-ui)
6. [UGUI 批处理源码分析](https://blog.titanwolf.in/a?ID=00700-1b3699d5-a36e-4522-9e03-f9ed2e0a92c0)
7. [SRP Batcher 速度提升](https://unity.com/blog/engine-platform/srp-batcher-speed-up-your-rendering)
8. [URP 性能优化配置](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@14.0/manual/configure-for-better-performance.html)

---

## 注意事项

1. **向后兼容**: `meshBatching` 默认为 `false`，不影响现有项目
2. **动态内容**: UI 变化时会触发 Mesh 重建，频繁变化的 UI 不建议启用
3. **裁剪处理**: 不同裁剪区域的元素会分到不同 BatchGroup
4. **内存开销**: 合并 Mesh 会增加一定内存，但减少了 GameObject 数量的开销

---

## 编辑器调试工具

### Mesh Batching Control Panel

提供运行时控制面板，方便对比不同批处理模式的 DrawCall 效果。

#### 打开方式
菜单：`Tools > FairyGUI > Mesh Batching Control Panel`

#### 功能说明

| 功能 | 说明 |
|------|------|
| FairyBatching Toggle | 开关基础批处理，关闭后可获取无任何批处理的基准 DrawCall |
| Off 按钮 | 关闭 Mesh 合并，仅使用 FairyBatching |
| Mesh Batching 按钮 | 启用 Mesh 合并批处理 |
| Generational 按钮 | 启用分代批处理 |
| Refresh Stats | 刷新统计信息 |
| Reset | 重置分代状态 |

#### 统计信息

- Graphics Count: UI 图形元素数量
- Object Count: 对象数量
- FairyBatching/MeshBatching/GenerationalBatching: 当前开关状态
- Batch Groups: 批处理组数量
- Total Vertices: 总顶点数
- Gen0/Gen1/Gen2 Count: 分代元素数量（分代模式）

#### 使用流程

1. 运行游戏
2. 打开控制面板
3. 打开 `Window > Analysis > Frame Debugger`
4. 切换不同模式，观察 DrawCall 变化
5. 对比优化效果

#### 对比测试建议

| 测试场景 | 操作 | 观察点 |
|---------|------|-------|
| 基准值 | 关闭 FairyBatching | 原始 DrawCall 数量 |
| FairyBatching | 开启 FairyBatching，Off 模式 | Unity 动态合批效果 |
| Mesh 合并 | Mesh Batching 模式 | Mesh 合并后 DrawCall |
| 分代批处理 | Generational 模式，等待 2 秒 | Gen2 合批效果 |

---

# 分代批处理设计 (Generational Batching)

## 设计思想

借鉴 **分代垃圾回收 (Generational GC)** 的思想：
- 新创建/刚变化的对象（年轻代）变化频率高
- 存活时间长的对象（老年代）变化频率低

应用到 UI 批处理：
- **频繁变化的 UI** → 保持独立渲染，响应性好
- **长时间稳定的 UI** → 合并 Mesh，减少 DrawCall

---

## 分代模型

```
┌─────────────────────────────────────────────────────────┐
│                    UI 元素生命周期                        │
├─────────────────────────────────────────────────────────┤
│                                                         │
│  Gen0 (年轻代)        Gen1 (中间代)        Gen2 (老年代)   │
│  ┌─────────┐         ┌─────────┐         ┌─────────┐    │
│  │ 独立渲染 │ ──────▶ │ 候选合批 │ ──────▶ │ 合并Mesh │    │
│  │         │  稳定N帧  │         │  稳定M帧  │         │    │
│  └─────────┘         └─────────┘         └─────────┘    │
│       ▲                   │                   │         │
│       │                   │                   │         │
│       └───────────────────┴───────────────────┘         │
│                     发生变化时降级                        │
└─────────────────────────────────────────────────────────┘
```

### 代的定义

| 代 | 名称 | 稳定帧数 | 渲染方式 | 说明 |
|---|------|---------|---------|------|
| Gen0 | 年轻代 | 0 | 独立 MeshRenderer | 新创建或刚变化的元素 |
| Gen1 | 中间代 | > N 帧 (如 30) | 独立渲染但标记为候选 | 观察期，准备合批 |
| Gen2 | 老年代 | > M 帧 (如 120) | 合并到 BatchGroup | 稳定元素，完全合批 |

### 晋升与降级规则

**晋升 (Promotion)**:
- Gen0 → Gen1: 连续 N 帧无变化
- Gen1 → Gen2: 连续 M 帧无变化，触发 Mesh 合并

**降级 (Demotion)**:
- 任何代 → Gen0: 发生属性变化（位置、颜色、纹理等）
- 降级时从 BatchGroup 中移除，恢复独立渲染

---

## 架构设计

### 新增类

```
GenerationalBatcher (新增)
  ├── _gen0Elements: List<NGraphics>     // 年轻代，独立渲染
  ├── _gen1Elements: List<NGraphics>     // 中间代，候选合批
  ├── _gen2Batcher: MeshBatcher          // 老年代，已合批
  ├── _stableFrameCounts: Dictionary<NGraphics, int>  // 稳定帧计数
  │
  ├── PromotionThresholdGen1: int = 30   // Gen0→Gen1 阈值
  ├── PromotionThresholdGen2: int = 120  // Gen1→Gen2 阈值
  │
  ├── Update()                           // 每帧更新，处理晋升
  ├── MarkDirty(NGraphics)               // 标记变化，触发降级
  └── RebuildGen2()                      // 重建老年代 Mesh
```

### NGraphics 扩展

```csharp
// 在 NGraphics 中新增
internal int _generation;           // 当前代: 0, 1, 2
internal int _stableFrameCount;     // 稳定帧计数
internal bool _isDirtyThisFrame;    // 本帧是否变化

public void MarkDirty()
{
    _isDirtyThisFrame = true;
    _stableFrameCount = 0;

    // 如果在 Gen2，需要从 BatchGroup 移除
    if (_generation == 2)
    {
        DemoteToGen0();
    }
    _generation = 0;
}
```

### 数据流

```
每帧更新流程:
┌────────────────────────────────────────────────────────────┐
│ GenerationalBatcher.Update()                               │
├────────────────────────────────────────────────────────────┤
│                                                            │
│ 1. 遍历所有元素，更新稳定帧计数                               │
│    foreach (element in allElements)                        │
│        if (!element._isDirtyThisFrame)                     │
│            element._stableFrameCount++                     │
│        element._isDirtyThisFrame = false                   │
│                                                            │
│ 2. 处理晋升                                                 │
│    Gen0 → Gen1: stableFrameCount >= PromotionThresholdGen1 │
│    Gen1 → Gen2: stableFrameCount >= PromotionThresholdGen2 │
│                                                            │
│ 3. 如果有元素晋升到 Gen2，重建老年代 Mesh                     │
│    if (hasNewGen2Elements)                                 │
│        _gen2Batcher.RebuildBatch(_gen2Elements)            │
│                                                            │
│ 4. 设置渲染顺序                                             │
│    - Gen0/Gen1: 独立设置 sortingOrder                       │
│    - Gen2: 通过 BatchGroup 设置                             │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

---

## 核心代码实现

### GenerationalBatcher.cs (新增)

```csharp
// Assets/Scripts/Core/Batch/GenerationalBatcher.cs
using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// 分代批处理管理器
    /// 借鉴分代 GC 思想，对稳定的 UI 元素进行 Mesh 合并
    /// </summary>
    public class GenerationalBatcher
    {
        // 晋升阈值（帧数）
        public int PromotionThresholdGen1 = 30;   // ~0.5秒 @ 60fps
        public int PromotionThresholdGen2 = 120;  // ~2秒 @ 60fps

        private List<NGraphics> _gen0Elements = new List<NGraphics>();
        private List<NGraphics> _gen1Elements = new List<NGraphics>();
        private List<NGraphics> _gen2Elements = new List<NGraphics>();

        private MeshBatcher _gen2Batcher;
        private Transform _root;
        private bool _gen2Dirty;

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
            graphics._generation = 0;
            graphics._stableFrameCount = 0;
            _gen0Elements.Add(graphics);
        }

        /// <summary>
        /// 移除元素
        /// </summary>
        public void RemoveElement(NGraphics graphics)
        {
            switch (graphics._generation)
            {
                case 0: _gen0Elements.Remove(graphics); break;
                case 1: _gen1Elements.Remove(graphics); break;
                case 2:
                    _gen2Elements.Remove(graphics);
                    graphics.SetBatchedState(false);
                    _gen2Dirty = true;
                    break;
            }
        }

        /// <summary>
        /// 标记元素变化（触发降级）
        /// </summary>
        public void MarkDirty(NGraphics graphics)
        {
            if (graphics._generation == 2)
            {
                // 从 Gen2 移除
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

            // 1. 更新 Gen0，检查晋升到 Gen1
            for (int i = _gen0Elements.Count - 1; i >= 0; i--)
            {
                var elem = _gen0Elements[i];
                if (!elem._isDirtyThisFrame)
                {
                    elem._stableFrameCount++;
                    if (elem._stableFrameCount >= PromotionThresholdGen1)
                    {
                        // 晋升到 Gen1
                        _gen0Elements.RemoveAt(i);
                        _gen1Elements.Add(elem);
                        elem._generation = 1;
                    }
                }
                elem._isDirtyThisFrame = false;
            }

            // 2. 更新 Gen1，检查晋升到 Gen2
            for (int i = _gen1Elements.Count - 1; i >= 0; i--)
            {
                var elem = _gen1Elements[i];
                if (!elem._isDirtyThisFrame)
                {
                    elem._stableFrameCount++;
                    if (elem._stableFrameCount >= PromotionThresholdGen2)
                    {
                        // 晋升到 Gen2
                        _gen1Elements.RemoveAt(i);
                        _gen2Elements.Add(elem);
                        elem._generation = 2;
                        hasPromotion = true;
                    }
                }
                elem._isDirtyThisFrame = false;
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
            // 构建 BatchElement 列表
            var elements = new List<BatchElement>();
            foreach (var graphics in _gen2Elements)
            {
                if (graphics._batchElement == null)
                    graphics._batchElement = new BatchElement();

                graphics._batchElement.owner = graphics;
                graphics._batchElement.material = graphics.material;
                elements.Add(graphics._batchElement);
            }

            _gen2Batcher.RebuildBatch(elements);
        }

        /// <summary>
        /// 设置渲染顺序
        /// </summary>
        public void SetRenderingOrder(UpdateContext context)
        {
            // Gen0 和 Gen1: 独立设置
            foreach (var elem in _gen0Elements)
            {
                if (elem.meshRenderer != null)
                    elem.meshRenderer.sortingOrder = context.renderingOrder++;
            }
            foreach (var elem in _gen1Elements)
            {
                if (elem.meshRenderer != null)
                    elem.meshRenderer.sortingOrder = context.renderingOrder++;
            }

            // Gen2: 通过 Batcher 设置
            _gen2Batcher.SetRenderingOrder(context);
        }

        public void Dispose()
        {
            foreach (var elem in _gen2Elements)
            {
                elem.SetBatchedState(false);
            }
            _gen0Elements.Clear();
            _gen1Elements.Clear();
            _gen2Elements.Clear();
            _gen2Batcher.Dispose();
        }

        // 统计信息
        public int Gen0Count => _gen0Elements.Count;
        public int Gen1Count => _gen1Elements.Count;
        public int Gen2Count => _gen2Elements.Count;
        public int BatchCount => _gen2Batcher.BatchCount;
    }
}
```

### NGraphics.cs 新增字段

```csharp
// 分代批处理相关
internal int _generation;           // 当前代: 0, 1, 2
internal int _stableFrameCount;     // 稳定帧计数
internal bool _isDirtyThisFrame;    // 本帧是否变化
```

### 触发 MarkDirty 的时机

在以下属性变化时调用 `MarkDirty()`:

```csharp
// NGraphics.cs
public Color color
{
    set
    {
        if (_color != value)
        {
            _color = value;
            _meshDirty = true;
            MarkDirtyForBatching();  // 新增
        }
    }
}

public float alpha
{
    set
    {
        if (_alpha != value)
        {
            _alpha = value;
            MarkDirtyForBatching();  // 新增
        }
    }
}

// 其他需要触发的属性: texture, contentRect, 等
```

---

## 使用方式

```csharp
// 启用分代批处理
GComponent panel = UIPackage.CreateObject("包名", "组件名").asCom;
panel.container.generationalBatching = true;  // 新属性
GRoot.inst.AddChild(panel);

// 可选：调整晋升阈值
panel.container.generationalBatcher.PromotionThresholdGen1 = 60;  // 1秒
panel.container.generationalBatcher.PromotionThresholdGen2 = 180; // 3秒
```

---

## 性能预期

### 场景示例：游戏主界面

```
假设界面有 200 个 UI 元素:
- 背景、边框、图标等静态元素: 150 个 → 2秒后进入 Gen2
- 血条、能量条等低频更新: 30 个 → 保持在 Gen1
- 伤害数字、特效等高频更新: 20 个 → 保持在 Gen0

优化效果:
┌─────────────────────────────────────────────┐
│ 优化前: 200 DrawCall                         │
├─────────────────────────────────────────────┤
│ 优化后:                                      │
│   Gen0 (独立): 20 DrawCall                   │
│   Gen1 (独立): 30 DrawCall                   │
│   Gen2 (合并): 2-5 DrawCall (按材质分组)      │
│   ─────────────────────                     │
│   总计: ~55 DrawCall (减少 72%)              │
└─────────────────────────────────────────────┘
```

### 与简单 Mesh 合并的对比

| 方案 | 静态 UI | 动态 UI | 重建频率 | 响应性 |
|-----|--------|--------|---------|-------|
| 简单合并 | ✅ 高效 | ❌ 频繁重建 | 高 | 差 |
| 分代批处理 | ✅ 高效 | ✅ 独立渲染 | 低 | 好 |

---

## 文件改动清单（更新）

| 文件 | 操作 | 改动说明 |
|------|------|---------|
| `Core/Batch/BatchGroup.cs` | 新增 | 批处理组管理 |
| `Core/Batch/MeshBatcher.cs` | 新增 | Mesh 合并管理器 |
| `Core/Batch/GenerationalBatcher.cs` | **新增** | 分代批处理管理器 |
| `Core/NGraphics.cs` | 修改 | 新增分代字段和 MarkDirty |
| `Core/Container.cs` | 修改 | 新增 generationalBatching 属性 |
| `Core/DisplayObject.cs` | 修改 | Flags 枚举新增标志 |
| `Resources/Shaders/*.shader` | 修改 | SRP Batcher 兼容 |

---

## 参考来源（补充）

9. [.NET GC 分代回收](https://docs.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals)
10. [游戏 UI 性能优化实践](https://unity.com/how-to/unity-ui-optimization-tips)
