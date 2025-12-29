# 高性能 UI 框架重构方案

> 目标：保留 FairyGUI 的游戏 UI 优势，融入现代框架架构理念，打造下一代高性能 UI 框架

---

## 一、重构目标

### 性能目标
- 万级 UI 元素 60fps 稳定
- DrawCall < 50（复杂界面）
- 内存零 GC（稳态运行）
- 布局计算 < 1ms

### 架构目标
- 声明式 API + 响应式状态
- 数据驱动渲染
- 增量更新（Dirty Flag + Diff）
- 多线程布局计算

### 开发体验目标
- 强类型 + 编译时检查
- 运行时 DevTools
- 热重载支持
- 组合式组件复用

---

## 二、架构选型分析

### 2.0 ECS vs MVVM：为什么 UI 不适合 ECS？

> ⚠️ **重要澄清**：虽然下文保留了 ECS 的示例代码作为参考，但 **不建议将完整 ECS 用于 UI 框架**。

#### 主流 UI 框架的架构选择

| 框架 | 架构模式 | 核心理念 |
|------|----------|----------|
| React | 单向数据流 + 组件化 | UI = f(State) |
| Vue | MVVM + 响应式 | 数据驱动视图 |
| Avalonia/WPF | MVVM + 绑定 | 视图与逻辑分离 |
| SwiftUI | 声明式 + 状态管理 | State → View |
| Flutter | 组件树 + 不可变 | Widget 重建 |

**没有一个主流 UI 框架使用 ECS！**

#### UI 与 ECS 的本质冲突

**ECS 适合的场景：**
```
✅ 大量同质化实体（子弹、粒子、敌人）
✅ 批量数据处理（物理模拟、AI）
✅ 组件组合高度动态
✅ 性能敏感的热路径
```

**UI 的本质特征：**
```
❌ 元素类型多样，行为差异大（按钮≠列表≠输入框）
❌ 强调层级关系（父子、兄弟）
❌ 事件驱动，而非帧驱动
❌ 状态-视图绑定是核心
❌ 开发体验 > 极致性能
```

#### ECS 用于 UI 的问题

```csharp
// ECS 风格：数据分散，关系难以表达
struct PositionComponent { float x, y; }
struct TextComponent { string text; }
struct ClickableComponent { Action onClick; }

// 问题1：父子关系怎么表达？
// 问题2：事件冒泡怎么实现？
// 问题3：样式继承怎么做？
// 问题4：条件渲染怎么处理？

// MVVM 风格：关系清晰，语义明确
class Button : UIElement
{
    public string Text { get; set; }
    public ICommand Command { get; set; }
    public Button Parent { get; }
    public Style Style { get; set; }  // 可继承
}
```

#### 正确的分层策略

| 层级 | 推荐架构 | 原因 |
|------|----------|------|
| **业务逻辑层** | MVVM / MVI | 状态管理清晰 |
| **组件层** | 组件化 + 组合 | 复用性强 |
| **渲染层** | 可考虑 DoD | 性能热点 |
| **布局层** | 可考虑并行 | CPU 密集 |

> DoD = Data-Oriented Design（数据导向设计），ECS 是其实现之一

#### 结论

**不需要 ECS，但可以借鉴其思想：**

1. **渲染层数据导向** - 顶点数据连续存储、材质排序合批
2. **布局计算并行化** - 同层节点可并行，使用 Job System
3. **保持 MVVM 架构** - 引入响应式（Signal）、声明式 API

---

### 2.1 ~~从显示列表到 ECS 架构~~ （仅供参考，不推荐）

> ⚠️ 以下 ECS 方案仅作为技术参考，**不建议用于 UI 框架上层架构**。
> 可在渲染层局部采用数据导向设计（DoD）优化性能。

**现状问题：**
```
DisplayObject 继承树过深，职责耦合
├── 位置/变换
├── 渲染
├── 事件
├── 动画
└── 布局
```

**重构方案：Entity-Component-System**

```csharp
// Entity：纯 ID
public readonly struct UIEntity
{
    public readonly int Id;
    public readonly int Version;  // 防止野指针
}

// Component：纯数据
public struct TransformComponent
{
    public float X, Y, Z;
    public float ScaleX, ScaleY;
    public float Rotation;
    public float PivotX, PivotY;
}

public struct RenderComponent
{
    public int MaterialId;
    public int TextureId;
    public Color32 Color;
    public BlendMode BlendMode;
}

public struct LayoutComponent
{
    public float Width, Height;
    public LayoutType LayoutType;
    public Padding Padding;
    public Margin Margin;
}

public struct InteractiveComponent
{
    public bool Touchable;
    public HitArea HitArea;
}

// System：纯逻辑
public class TransformSystem : IUISystem
{
    public void Update(Span<UIEntity> entities) { ... }
}

public class RenderSystem : IUISystem
{
    public void Update(Span<UIEntity> entities) { ... }
}
```

**优势：**
- 数据局部性好，缓存友好
- 组件可独立复用
- 易于并行处理
- 内存布局可控

---

### 2.2 响应式状态管理

**现状问题：**
```csharp
// 命令式更新，手动同步
button.text = "New Text";
button.icon = newIcon;
controller.selectedIndex = 1;
```

**重构方案：Signal-based Reactivity**

```csharp
// 定义响应式状态
public class PlayerHUD : UIComponent
{
    // Signal：细粒度响应式
    private readonly Signal<int> _hp = new(100);
    private readonly Signal<int> _mp = new(50);
    private readonly Signal<string> _name = new("Player");

    // Computed：派生状态
    private readonly Computed<float> _hpPercent;
    private readonly Computed<bool> _isLowHp;

    public PlayerHUD()
    {
        _hpPercent = new(() => _hp.Value / 100f);
        _isLowHp = new(() => _hp.Value < 20);
    }

    // 声明式 UI 描述
    protected override UINode Render() =>
        VStack(
            Label(_name),                              // 自动订阅 _name
            ProgressBar(_hpPercent, color: _isLowHp.Value ? Red : Green),
            ProgressBar(() => _mp.Value / 100f)
        );

    // 状态变更自动触发精确更新
    public void TakeDamage(int damage) => _hp.Value -= damage;
}
```

**响应式引擎实现：**

```csharp
public class Signal<T> : ISignal
{
    private T _value;
    private readonly HashSet<IEffect> _subscribers = new();

    public T Value
    {
        get
        {
            // 自动依赖收集
            ReactiveRuntime.TrackDependency(this);
            return _value;
        }
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
            // 批量通知，避免级联更新
            ReactiveRuntime.ScheduleUpdate(_subscribers);
        }
    }
}

public class ReactiveRuntime
{
    private static readonly Queue<IEffect> _pendingEffects = new();
    private static bool _isBatching;

    // 批处理：一帧内的多次状态变更合并为一次更新
    public static void Batch(Action action)
    {
        _isBatching = true;
        action();
        _isBatching = false;
        FlushEffects();
    }
}
```

---

### 2.3 声明式 UI DSL

**现状问题：**
```csharp
// 命令式构建 UI
var panel = new GComponent();
var title = UIPackage.CreateObject(...);
title.SetPosition(10, 10);
panel.AddChild(title);
var button = UIPackage.CreateObject(...);
button.onClick.Add(...);
panel.AddChild(button);
```

**重构方案：声明式 DSL**

```csharp
// 方案 A：Fluent Builder（当前 C# 友好）
public UINode Build() =>
    Panel()
        .Size(400, 300)
        .Children(
            Label("Settings")
                .FontSize(24)
                .Align(Alignment.Center),

            VStack(gap: 10)
                .Children(
                    Toggle("Sound", _soundEnabled),
                    Slider("Volume", _volume, 0, 100),
                    Button("Apply", OnApply)
                ),

            HStack(gap: 20)
                .Align(Alignment.Bottom)
                .Children(
                    Button("Cancel", OnCancel),
                    Button("OK", OnConfirm).Primary()
                )
        );

// 方案 B：Source Generator（编译时转换）
[UI]
public partial class SettingsPanel : UIComponent
{
    // 编译时生成高效的构建代码
    /*
    <Panel Size="400,300">
        <Label Text="Settings" FontSize="24" Align="Center"/>
        <VStack Gap="10">
            <Toggle Label="Sound" Value="{Bind SoundEnabled}"/>
            <Slider Label="Volume" Value="{Bind Volume}" Min="0" Max="100"/>
        </VStack>
    </Panel>
    */
}
```

---

### 2.4 增量渲染引擎

**现状问题：**
```
每帧遍历整个 DisplayObject 树
即使只有一个节点变化，也要检查所有节点
```

**重构方案：Dirty Region + Retained Mode**

```csharp
public class IncrementalRenderer
{
    // 脏区域追踪
    private readonly DirtyRegionTracker _dirtyTracker = new();

    // 渲染命令缓存
    private readonly RenderCommandBuffer _commandBuffer = new();

    public void Update()
    {
        // 1. 收集脏节点（O(脏节点数)，而非 O(总节点数)）
        var dirtyNodes = _dirtyTracker.GetDirtyNodes();

        if (dirtyNodes.Count == 0) return;  // 无变化，跳过渲染

        // 2. 增量更新渲染命令
        foreach (var node in dirtyNodes)
        {
            UpdateRenderCommands(node);
        }

        // 3. 只重绘脏区域
        var dirtyRect = _dirtyTracker.GetDirtyRect();
        _commandBuffer.ExecuteRegion(dirtyRect);

        _dirtyTracker.Clear();
    }
}

// 属性变更自动标记脏
public struct TransformComponent
{
    private float _x;
    public float X
    {
        get => _x;
        set
        {
            if (_x == value) return;
            _x = value;
            DirtyFlags |= DirtyFlag.Transform;
        }
    }

    public DirtyFlag DirtyFlags;
}
```

---

### 2.5 多线程布局计算

**现状问题：**
```
布局计算在主线程
复杂布局（如虚拟列表）可能造成卡顿
```

**重构方案：Job System 并行布局**

```csharp
// Unity Job System 实现
[BurstCompile]
public struct LayoutJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<LayoutConstraint> Constraints;
    [ReadOnly] public NativeArray<float2> ParentSizes;
    public NativeArray<Rect> Results;

    public void Execute(int index)
    {
        var constraint = Constraints[index];
        var parentSize = ParentSizes[constraint.ParentIndex];

        // 计算布局（Burst 编译优化）
        Results[index] = CalculateLayout(constraint, parentSize);
    }
}

public class LayoutSystem
{
    public void ScheduleLayout(NativeArray<UIEntity> entities)
    {
        // 1. 拓扑排序（确保父节点先计算）
        var sortedEntities = TopologicalSort(entities);

        // 2. 分层并行
        foreach (var layer in GroupByDepth(sortedEntities))
        {
            var job = new LayoutJob
            {
                Constraints = GetConstraints(layer),
                ParentSizes = GetParentSizes(layer),
                Results = new NativeArray<Rect>(layer.Length, Allocator.TempJob)
            };

            // 并行执行
            var handle = job.Schedule(layer.Length, 64);
            handle.Complete();

            ApplyResults(layer, job.Results);
        }
    }
}
```

---

## 三、渲染优化重构

### 3.1 统一渲染管线

```csharp
public class UIRenderPipeline
{
    // 阶段 1：收集可见元素（视锥剔除）
    private readonly VisibilitySystem _visibility;

    // 阶段 2：排序（深度 + 材质）
    private readonly SortingSystem _sorting;

    // 阶段 3：合批
    private readonly BatchingSystem _batching;

    // 阶段 4：提交渲染
    private readonly RenderSubmitter _submitter;

    public void Render(Camera camera)
    {
        // Culling（剔除不可见元素）
        var visibleEntities = _visibility.Cull(camera.ViewRect);

        // Sort（材质排序 + 深度排序）
        _sorting.Sort(visibleEntities);

        // Batch（相同材质合并）
        var batches = _batching.CreateBatches(visibleEntities);

        // Submit（GPU Instancing / SRP Batcher）
        _submitter.Submit(batches);
    }
}
```

### 3.2 GPU Instancing 优化

```csharp
// 相同材质的元素使用 GPU Instancing
public class InstancedBatch
{
    private readonly Material _material;
    private readonly Mesh _mesh;
    private readonly Matrix4x4[] _matrices;
    private readonly Vector4[] _colors;
    private readonly Vector4[] _uvRects;

    private int _count;

    public void Add(UIEntity entity)
    {
        _matrices[_count] = entity.WorldMatrix;
        _colors[_count] = entity.Color;
        _uvRects[_count] = entity.UVRect;
        _count++;
    }

    public void Flush()
    {
        if (_count == 0) return;

        // 一次 DrawCall 渲染所有实例
        _materialPropertyBlock.SetVectorArray("_Colors", _colors);
        _materialPropertyBlock.SetVectorArray("_UVRects", _uvRects);

        Graphics.DrawMeshInstanced(_mesh, 0, _material, _matrices, _count, _materialPropertyBlock);
        _count = 0;
    }
}
```

### 3.3 文本渲染优化

```csharp
// SDF 文本 + 文本图集
public class SDFTextRenderer
{
    // 动态文本图集
    private readonly DynamicAtlas _glyphAtlas;

    // 文本网格缓存（避免每帧重建）
    private readonly Dictionary<int, CachedTextMesh> _meshCache;

    public void RenderText(string text, TextStyle style)
    {
        var hash = HashText(text, style);

        if (!_meshCache.TryGetValue(hash, out var cached))
        {
            // 缓存未命中：生成网格
            cached = GenerateTextMesh(text, style);
            _meshCache[hash] = cached;
        }

        // 使用 SDF Shader 渲染
        // - 支持任意缩放不失真
        // - 支持描边、阴影、渐变
        DrawMesh(cached.Mesh, _sdfMaterial);
    }
}
```

---

## 四、内存优化重构

### 4.1 对象池化

```csharp
// 通用对象池
public class UIObjectPool<T> where T : class, new()
{
    private readonly Stack<T> _pool = new();
    private readonly Action<T> _onGet;
    private readonly Action<T> _onRelease;

    public T Get()
    {
        var obj = _pool.Count > 0 ? _pool.Pop() : new T();
        _onGet?.Invoke(obj);
        return obj;
    }

    public void Release(T obj)
    {
        _onRelease?.Invoke(obj);
        _pool.Push(obj);
    }
}

// 预分配的组件存储
public class ComponentStorage<T> where T : struct
{
    private T[] _components;
    private readonly BitArray _occupied;

    public ref T Get(int entityId) => ref _components[entityId];
}
```

### 4.2 零 GC 事件系统

```csharp
// 使用结构体 + 委托缓存避免 GC
public struct UIEvent
{
    public UIEntity Target;
    public UIEntity CurrentTarget;
    public EventType Type;
    public EventPhase Phase;
    public InputData Input;  // 结构体，非引用类型
}

public class ZeroGCEventDispatcher
{
    // 预分配事件对象
    private readonly UIEvent[] _eventPool = new UIEvent[32];
    private int _eventIndex;

    // 委托缓存（避免每次创建新委托）
    private readonly Dictionary<(UIEntity, EventType), Action<UIEvent>> _handlerCache;

    public void Dispatch(UIEntity target, EventType type, InputData input)
    {
        ref var evt = ref _eventPool[_eventIndex++ % _eventPool.Length];
        evt.Target = target;
        evt.Type = type;
        evt.Input = input;

        // 冒泡传播
        var current = target;
        while (current.IsValid)
        {
            evt.CurrentTarget = current;
            if (_handlerCache.TryGetValue((current, type), out var handler))
            {
                handler(evt);
            }
            current = GetParent(current);
        }
    }
}
```

### 4.3 NativeArray 替代 List

```csharp
// 使用 NativeArray 管理大量 UI 元素
public class UIEntityManager
{
    // 连续内存布局
    private NativeArray<TransformComponent> _transforms;
    private NativeArray<RenderComponent> _renders;
    private NativeArray<LayoutComponent> _layouts;

    // 支持 Burst 编译和 Job System
    [BurstCompile]
    public struct UpdateTransformsJob : IJobParallelFor
    {
        public NativeArray<TransformComponent> Transforms;
        [ReadOnly] public NativeArray<TransformComponent> ParentTransforms;

        public void Execute(int index)
        {
            // 向量化计算
            var local = Transforms[index];
            var parent = ParentTransforms[index];
            // ... 矩阵计算
        }
    }
}
```

---

## 五、开发体验重构

### 5.1 强类型绑定

```csharp
// Source Generator 生成类型安全的绑定代码
[UIBinding]
public partial class InventorySlot : UIComponent
{
    // 自动生成属性绑定
    [Bind("ItemIcon")] private GLoader _icon;
    [Bind("ItemName")] private GTextField _name;
    [Bind("ItemCount")] private GTextField _count;

    // 编译时检查绑定是否存在
    // 自动生成：
    // partial void InitBindings() {
    //     _icon = GetChild<GLoader>("ItemIcon");
    //     _name = GetChild<GTextField>("ItemName");
    //     _count = GetChild<GTextField>("ItemCount");
    // }
}
```

### 5.2 运行时 DevTools

```csharp
public class UIDevTools
{
    // UI 树检查器
    public void InspectTree()
    {
        ImGui.Begin("UI Hierarchy");
        DrawTreeRecursive(UIRoot);
        ImGui.End();
    }

    // 性能面板
    public void ShowPerformance()
    {
        ImGui.Begin("UI Performance");
        ImGui.Text($"Entity Count: {_entityCount}");
        ImGui.Text($"Draw Calls: {_drawCalls}");
        ImGui.Text($"Batches: {_batchCount}");
        ImGui.Text($"Layout Time: {_layoutTime:F2}ms");
        ImGui.Text($"Render Time: {_renderTime:F2}ms");
        ImGui.PlotLines("Frame Times", _frameTimes);
        ImGui.End();
    }

    // 元素高亮
    public void HighlightElement(UIEntity entity)
    {
        var bounds = GetWorldBounds(entity);
        DebugDraw.Rect(bounds, Color.yellow);
    }

    // 实时属性编辑
    public void EditProperties(UIEntity entity)
    {
        ref var transform = ref GetComponent<TransformComponent>(entity);
        ImGui.DragFloat2("Position", ref transform.X);
        ImGui.DragFloat2("Scale", ref transform.ScaleX);
        ImGui.DragFloat("Rotation", ref transform.Rotation);
    }
}
```

### 5.3 热重载

```csharp
public class UIHotReload
{
    private FileSystemWatcher _watcher;

    public void Watch(string uiResourcePath)
    {
        _watcher = new FileSystemWatcher(uiResourcePath, "*.bytes");
        _watcher.Changed += OnUIResourceChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnUIResourceChanged(object sender, FileSystemEventArgs e)
    {
        // 在主线程执行重载
        MainThreadDispatcher.Enqueue(() =>
        {
            var packageName = Path.GetFileNameWithoutExtension(e.Name);

            // 1. 卸载旧资源
            UIPackage.RemovePackage(packageName);

            // 2. 加载新资源
            UIPackage.AddPackage(e.FullPath);

            // 3. 重建受影响的 UI
            RebuildAffectedUI(packageName);

            Debug.Log($"[Hot Reload] {packageName} reloaded");
        });
    }
}
```

---

## 六、兼容性与迁移

### 6.1 渐进式迁移策略

```
阶段 1: 核心层重构（不影响上层 API）
├── 替换渲染引擎 → 增量渲染
├── 替换布局引擎 → 多线程布局
└── 替换事件系统 → 零 GC 事件

阶段 2: 引入新 API（与旧 API 并存）
├── 新增 Signal 响应式
├── 新增声明式 Builder
└── 新增 Source Generator 绑定

阶段 3: 迁移工具
├── 自动转换工具（旧代码 → 新代码）
├── 兼容层（旧 API → 新 API 适配器）
└── 文档和迁移指南

阶段 4: 废弃旧 API
├── 标记 [Obsolete]
├── 编译警告
└── 最终移除
```

### 6.2 API 适配器

```csharp
// 旧 API 适配到新架构
[Obsolete("Use UIComponent.Render() instead")]
public class GComponent : ILegacyAdapter
{
    private readonly UIEntity _entity;

    // 旧 API 转发到新架构
    public float x
    {
        get => ECS.GetComponent<TransformComponent>(_entity).X;
        set => ECS.GetComponent<TransformComponent>(_entity).X = value;
    }

    public void AddChild(GObject child)
    {
        ECS.SetParent(child._entity, _entity);
    }
}
```

---

## 七、性能基准目标

| 指标 | 当前 | 目标 | 提升 |
|------|------|------|------|
| 10000 元素渲染 | 45fps | 60fps | +33% |
| DrawCall（复杂UI） | 80+ | <30 | -60% |
| 布局计算时间 | 3ms | <0.5ms | -83% |
| 内存 GC | 每帧 | 零 GC | 100% |
| 首次加载时间 | 200ms | <50ms | -75% |

---

## 八、总结

### 修正后的重构优先级

```
P0（高收益低风险）:
├── 响应式状态（Signal）       ← 现代 MVVM 核心
├── 增量渲染（Dirty Region）   ← 性能提升明显
└── 对象池化                   ← 消除 GC

P1（中等收益）:
├── 声明式 API                 ← 开发体验
├── 强类型绑定                 ← 编译时安全
└── 渲染层 DoD                 ← 底层优化

P2（谨慎评估）:
├── 多线程布局                 ← 复杂度高
└── 完整 ECS                   ← ❌ 不推荐用于 UI
```

### 风险评估

| 风险 | 影响 | 缓解措施 |
|------|------|----------|
| 兼容性破坏 | 高 | 渐进迁移 + 适配层 |
| 学习成本 | 中 | 完善文档 + 示例 |
| 性能退化 | 中 | 持续基准测试 |
| 编辑器兼容 | 高 | 保持资源格式不变 |

### 技术选型建议

- **响应式**: 参考 Vue 3 / Solid.js 的 Signal 模型
- **布局**: 参考 Yoga (Facebook) / Taffy (Rust)
- **渲染**: 参考 Flutter Impeller / Skia
- **架构**: 保持 MVVM/组件化，渲染层可借鉴 DoD 思想

### 业界实践参考

| 框架 | 渲染层 | 逻辑层 |
|------|--------|--------|
| Unity UGUI | Mesh + Canvas | MonoBehaviour |
| Flutter | Skia/Impeller | Widget 树 |
| React Native | Native View | JS 组件 |
| Qt Quick | Scene Graph | QML + C++ |
| GPUI Component | GPU 加速 | RenderOnce 组件 |

**共同点：渲染层优化，逻辑层保持组件化**

---

## 九、gpui-component 对比分析

> 参考：[longbridge/gpui-component](https://github.com/longbridge/gpui-component) - 基于 Zed GPUI 框架的 Rust UI 组件库

### 9.1 gpui-component 核心设计

#### 技术栈
- **语言**: Rust（内存安全、零成本抽象）
- **渲染引擎**: GPUI（Zed 编辑器的 GPU 加速 UI 框架）
- **规模**: 60+ 跨平台桌面 UI 组件

#### 架构特点

```
gpui-component 架构
├── RenderOnce 组件模型     ← 无状态，每次渲染重建
├── Entity<State> 状态外置  ← 数据与 UI 分离
├── 委托模式数据源          ← ListDelegate trait
├── 变体驱动样式            ← ButtonVariant 枚举
└── 虚拟化列表              ← 仅渲染可见项
```

#### 状态管理：Entity<State> 模式

```rust
// gpui-component 的状态外置模式
pub struct Input {
    state: Entity<InputState>,  // 状态外置，可多组件共享
    // ...配置字段
}

// 多个 UI 组件可共享同一状态实例
let shared_state: Entity<InputState> = cx.new_entity(...);
let input1 = Input::new(shared_state.clone());
let input2 = InputPreview::new(shared_state.clone());  // 共享状态
```

**优势**：
- 数据与 UI 完全分离
- 多组件可共享同一状态
- 便于跨组件通信
- 状态可独立测试

#### 组件模型：RenderOnce + 方法链

```rust
// gpui-component 的声明式构建
Button::new("submit")
    .label("Submit")
    .variant(ButtonVariant::Primary)
    .size(ButtonSize::Medium)
    .disabled(is_loading)
    .on_click(|_, _, _| { ... })
    .when(show_icon, |btn| btn.icon(Icon::Check))  // 条件构建
    .when_some(tooltip, |btn, tip| btn.tooltip(tip))
```

**核心 API**：
- `when(condition, builder)` - 条件应用
- `when_some(option, builder)` - Option 条件应用
- 链式调用，类型安全

#### 虚拟列表实现

```rust
// 委托模式数据源
pub trait ListDelegate {
    fn sections_count(&self) -> usize;
    fn items_count(&self, section_ix: usize) -> usize;
    fn render_item(&self, ix: usize) -> impl IntoElement;
}

// 虚拟化渲染
pub struct List {
    delegate: Arc<dyn ListDelegate>,
    visible_range: Range<usize>,     // 仅渲染可见范围
    rows_cache: RowsCache,           // 行缓存复用
    deferred_scroll_to_index: Option<usize>,  // 延迟滚动
}
```

**性能优化**：
- 按需加载：`load_more_if_need()`
- 搜索防抖：100ms 延迟避免闪烁
- 缓存复用：避免重复计算

#### 变体驱动样式系统

```rust
// 预设样式变体
enum ButtonVariant {
    Primary, Secondary, Danger, Info, Success,
    Warning, Ghost, Link, Text, Custom(...)
}

// 四态样式计算
impl ButtonVariant {
    fn normal(&self) -> Style { ... }
    fn hovered(&self) -> Style { ... }
    fn active(&self) -> Style { ... }
    fn disabled(&self) -> Style { ... }
}
```

### 9.2 对比分析

| 方面 | REFACTOR_PROPOSAL | gpui-component | 分析 |
|------|-------------------|----------------|------|
| **语言/平台** | C# / Unity | Rust / 原生 | Unity 有 GC，Rust 零成本 |
| **渲染策略** | 增量渲染 + Dirty Region | GPU 每帧重建 | 各有优势，需按场景选择 |
| **状态管理** | Signal 响应式 | Entity<State> 外置 | 可融合：Signal + 外置 |
| **组件模型** | 声明式 DSL | RenderOnce | 相似，都是声明式 |
| **虚拟化** | 未详细定义 | 完整实现 | **需补充** |
| **样式系统** | 未详细定义 | 变体驱动 | **需补充** |
| **事件处理** | 零 GC 事件 | 闭包回调 | 各有侧重 |
| **性能度量** | DevTools | 可选追踪 | 可融合 |

### 9.3 值得借鉴的设计

#### ✅ 1. Entity<State> 状态外置模式

**当前方案**：Signal 内嵌于组件
```csharp
public class PlayerHUD : UIComponent
{
    private readonly Signal<int> _hp = new(100);  // 状态内嵌
}
```

**借鉴改进**：支持状态外置 + 共享
```csharp
// 状态可独立定义
public class PlayerState : UIState
{
    public Signal<int> HP { get; } = new(100);
    public Signal<int> MP { get; } = new(50);
}

// 多组件共享同一状态
var state = new PlayerState();
var hud = new PlayerHUD(state);
var miniMap = new MiniMapPlayer(state);  // 共享状态
```

#### ✅ 2. 完整的虚拟列表实现

**需补充的功能**：
```csharp
// 委托模式数据源
public interface IListDelegate<T>
{
    int SectionCount { get; }
    int GetItemCount(int section);
    UINode RenderItem(int index, T data);
}

// 虚拟列表组件
public class VirtualList<T> : UIComponent
{
    private readonly IListDelegate<T> _delegate;
    private readonly Range _visibleRange;
    private readonly Dictionary<int, UINode> _nodeCache;

    // 延迟滚动（避免频繁更新）
    private int? _deferredScrollIndex;

    // 按需加载
    public Action<int> OnLoadMore { get; set; }
}
```

#### ✅ 3. 变体驱动样式系统

**需补充的功能**：
```csharp
// 预设样式变体
public enum ButtonVariant
{
    Primary, Secondary, Danger, Success,
    Warning, Ghost, Link, Text
}

// 尺寸变体
public enum UISize { XS, SM, MD, LG }

// 四态样式
public interface IVariantStyle
{
    Style Normal { get; }
    Style Hovered { get; }
    Style Active { get; }
    Style Disabled { get; }
}

// 使用示例
Button("Submit")
    .Variant(ButtonVariant.Primary)
    .Size(UISize.MD)
```

#### ✅ 4. 条件构建 API（when/when_some）

**需补充的功能**：
```csharp
// 条件构建扩展
public static class UIBuilderExtensions
{
    public static T When<T>(this T node, bool condition, Func<T, T> builder)
        where T : UINode
        => condition ? builder(node) : node;

    public static T WhenSome<T, V>(this T node, V? value, Func<T, V, T> builder)
        where T : UINode where V : class
        => value != null ? builder(node, value) : node;
}

// 使用示例
Button("Submit")
    .When(isLoading, btn => btn.Loading(true))
    .WhenSome(tooltip, (btn, tip) => btn.Tooltip(tip))
    .When(showIcon, btn => btn.Icon(Icons.Check))
```

#### ✅ 5. 两层事件防护机制

**需补充的功能**：
```csharp
public class InteractiveComponent : UIComponent
{
    private bool _isDisabled;
    private bool _isLoading;

    // 第一层：鼠标按下时检查
    protected override void OnMouseDown(MouseEvent evt)
    {
        if (_isDisabled || _isLoading)
        {
            evt.StopPropagation();  // 阻止事件继续
            return;
        }
        base.OnMouseDown(evt);
    }

    // 第二层：点击时再次验证
    protected override void OnClick(ClickEvent evt)
    {
        if (!IsClickable()) return;
        _onClick?.Invoke(evt);
    }

    private bool IsClickable() => !_isDisabled && !_isLoading;
}
```

#### ✅ 6. 性能度量系统增强

**需补充的功能**：
```csharp
// 可选的性能追踪
public static class UIMetrics
{
    private static bool _enabled = Environment.GetEnvironmentVariable("UI_METRICS") != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IDisposable Measure([CallerMemberName] string name = "")
    {
        if (!_enabled) return NullDisposable.Instance;
        return new MetricScope(name);
    }
}

// 使用示例
public void UpdateLayout()
{
    using (UIMetrics.Measure())  // 自动记录耗时
    {
        // 布局计算...
    }
}
```

#### ✅ 7. 搜索防抖机制

**需补充的功能**：
```csharp
public class SearchInput : UIComponent
{
    private readonly Signal<string> _searchText = new("");
    private CancellationTokenSource _debounceToken;
    private const int DebounceMs = 100;

    private async void OnTextChanged(string text)
    {
        _debounceToken?.Cancel();
        _debounceToken = new CancellationTokenSource();

        try
        {
            await Task.Delay(DebounceMs, _debounceToken.Token);
            _searchText.Value = text;  // 防抖后更新
        }
        catch (TaskCanceledException) { }
    }
}
```

### 9.4 不适用于本方案的设计

| 设计 | 原因 |
|------|------|
| **Rust 所有权** | C# 有 GC，无需手动管理 |
| **每帧重建** | Unity 场景下增量更新更高效 |
| **GPUI 渲染** | 使用 Unity 渲染管线 |
| **Rope 文本结构** | 游戏 UI 文本场景较简单 |

### 9.5 融合方案

```
融合后的架构
├── 状态层
│   ├── Signal 响应式（细粒度更新）
│   ├── Entity<State> 外置（跨组件共享）  ← 新增
│   └── Computed 派生状态
├── 组件层
│   ├── 声明式 DSL Builder
│   ├── When/WhenSome 条件构建  ← 新增
│   └── 变体驱动样式系统  ← 新增
├── 渲染层
│   ├── 增量渲染 + Dirty Region
│   ├── 虚拟列表（委托模式）  ← 新增
│   └── GPU Instancing
└── 工具层
    ├── DevTools 检查器
    ├── 性能度量系统  ← 增强
    └── 热重载
```

---

## 十、更新后的重构优先级

```
P0（高收益低风险）:
├── 响应式状态（Signal + Entity 外置） ← 融合
├── 增量渲染（Dirty Region）
├── 对象池化
└── 虚拟列表（委托模式）  ← 新增

P1（中等收益）:
├── 声明式 API + When 条件构建  ← 增强
├── 变体驱动样式系统  ← 新增
├── 强类型绑定
└── 渲染层 DoD

P2（工具支持）:
├── 性能度量系统  ← 增强
├── DevTools
└── 热重载

P3（谨慎评估）:
├── 多线程布局
└── 完整 ECS（不推荐）
```
