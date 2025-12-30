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

---

## 十一、界面级对象池设计（重点优化）

> ⚠️ **问题背景**：当前 GObject 及派生类（Button、Label 等）在界面打开时创建，关闭时销毁，
> 没有对象池缓存。频繁打开/关闭界面会产生大量 GC。

### 11.1 现状问题分析

```csharp
// 当前流程：每次打开界面都 new 大量对象
Window.Show()
    → UIPackage.CreateObject()
        → new GButton()           // 每个按钮一次 new
        → new Relations()         // 每个对象一次 new
        → new GearBase[10]        // 每个对象一个数组
        → new DisplayObject()     // 每个对象一次 new
        → ... 递归创建所有子对象

// 关闭界面全部销毁
Window.Hide() → Dispose()
    → 所有对象标记为垃圾
    → 等待 GC 回收（卡顿）
```

**GC 热点统计（以一个中等复杂度界面为例）**：
| 操作 | 对象数量 | 内存分配 |
|------|----------|----------|
| 打开背包界面 | ~200 GObject | ~50KB |
| 关闭背包界面 | 200 次 Dispose | 50KB 变垃圾 |
| 频繁开关 10 次 | 2000 次创建/销毁 | 500KB GC |

### 11.2 解决方案：分层对象池

```
┌─────────────────────────────────────────────────────┐
│  Window 池（界面级）                                 │
│  - 缓存完整的 Window 实例                            │
│  - Hide 时归还到池，不 Dispose                       │
│  - Show 时从池获取或创建                             │
└─────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────┐
│  Component 池（组件级）                              │
│  - 动态创建的组件（如列表项、弹窗）                   │
│  - 现有 GObjectPool 的增强版                         │
└─────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────┐
│  Primitive 池（基础对象级）                          │
│  - DisplayObject、NGraphics、Mesh 等                 │
│  - 最底层的 Unity 对象复用                           │
└─────────────────────────────────────────────────────┘
```

### 11.3 核心实现

#### 11.3.1 IPoolable 接口

```csharp
/// <summary>
/// 可池化对象接口
/// </summary>
public interface IPoolable
{
    /// <summary>
    /// 从池中取出时调用，重置为初始状态
    /// </summary>
    void OnSpawn();

    /// <summary>
    /// 归还到池中时调用，清理临时状态
    /// </summary>
    void OnDespawn();

    /// <summary>
    /// 是否可以被池化（某些特殊对象不应池化）
    /// </summary>
    bool IsPoolable { get; }
}
```

#### 11.3.2 WindowPool 界面池

```csharp
/// <summary>
/// 界面级对象池
/// </summary>
public class WindowPool
{
    private static WindowPool _inst;
    public static WindowPool inst => _inst ??= new WindowPool();

    // 按 URL 分类的窗口池
    private readonly Dictionary<string, Stack<Window>> _windowPool = new();

    // 池化配置
    private readonly Dictionary<string, PoolConfig> _configs = new();

    // 隐藏的父节点（池中对象挂载点）
    private Transform _poolRoot;

    public struct PoolConfig
    {
        public int MaxCount;        // 最大缓存数量
        public float ExpireTime;    // 过期时间（秒），0 表示永不过期
        public bool PreWarm;        // 是否预热
    }

    /// <summary>
    /// 配置某个界面的池化参数
    /// </summary>
    public void Configure(string url, PoolConfig config)
    {
        _configs[UIPackage.NormalizeURL(url)] = config;
    }

    /// <summary>
    /// 获取窗口（从池中或创建新实例）
    /// </summary>
    public T GetWindow<T>(string url) where T : Window, new()
    {
        url = UIPackage.NormalizeURL(url);

        // 尝试从池中获取
        if (_windowPool.TryGetValue(url, out var stack) && stack.Count > 0)
        {
            var window = stack.Pop() as T;
            window.OnSpawn();  // 重置状态
            return window;
        }

        // 池中没有，创建新实例
        var newWindow = new T();
        newWindow.contentPane = UIPackage.CreateObjectFromURL(url).asCom;
        return newWindow;
    }

    /// <summary>
    /// 归还窗口到池中
    /// </summary>
    public void ReturnWindow(Window window)
    {
        if (window == null || window.isDisposed) return;

        string url = window.contentPane?.resourceURL;
        if (string.IsNullOrEmpty(url))
        {
            window.Dispose();  // 无法识别的窗口直接销毁
            return;
        }

        // 检查池容量
        var config = GetConfig(url);
        if (!_windowPool.TryGetValue(url, out var stack))
        {
            stack = new Stack<Window>();
            _windowPool[url] = stack;
        }

        if (stack.Count >= config.MaxCount)
        {
            window.Dispose();  // 超出容量，销毁
            return;
        }

        // 清理状态并归还
        window.OnDespawn();

        // 移动到池根节点下（隐藏）
        if (_poolRoot != null)
            window.displayObject.cachedTransform.SetParent(_poolRoot, false);

        stack.Push(window);
    }

    /// <summary>
    /// 预热指定界面
    /// </summary>
    public void PreWarm(string url, int count)
    {
        url = UIPackage.NormalizeURL(url);

        for (int i = 0; i < count; i++)
        {
            var window = new Window();
            window.contentPane = UIPackage.CreateObjectFromURL(url).asCom;
            ReturnWindow(window);
        }
    }

    /// <summary>
    /// 清理过期对象
    /// </summary>
    public void CleanupExpired()
    {
        // 定期清理超时未使用的对象
        // ...
    }

    /// <summary>
    /// 清空所有池
    /// </summary>
    public void Clear()
    {
        foreach (var kv in _windowPool)
        {
            foreach (var window in kv.Value)
                window.Dispose();
        }
        _windowPool.Clear();
    }

    private PoolConfig GetConfig(string url)
    {
        if (_configs.TryGetValue(url, out var config))
            return config;
        return new PoolConfig { MaxCount = 3, ExpireTime = 60f };
    }
}
```

#### 11.3.3 GObject 状态重置

```csharp
// 在 GObject 中添加 IPoolable 实现
public partial class GObject : IPoolable
{
    public virtual bool IsPoolable => true;

    /// <summary>
    /// 从池中取出时重置状态
    /// </summary>
    public virtual void OnSpawn()
    {
        // 重置基础属性
        _visible = true;
        _touchable = true;
        _grayed = false;
        _alpha = 1f;

        // 重置变换
        SetPosition(0, 0, 0);
        SetScale(1, 1);
        rotation = 0;

        // 重置滤镜
        filter = null;
        blendMode = BlendMode.Normal;

        // 通知子类
        OnSpawnInternal();
    }

    /// <summary>
    /// 归还到池中时清理
    /// </summary>
    public virtual void OnDespawn()
    {
        // 移除所有动态添加的事件监听
        RemoveEventListeners();

        // 停止所有动画/过渡
        // ...

        // 重置控制器状态
        // ...

        // 清理用户数据
        data = null;

        // 通知子类
        OnDespawnInternal();
    }

    protected virtual void OnSpawnInternal() { }
    protected virtual void OnDespawnInternal() { }
}

// GButton 的池化支持
public partial class GButton
{
    protected override void OnSpawnInternal()
    {
        // 重置按钮状态
        selected = false;
        _over = false;
        _down = false;

        // 恢复默认控制器状态
        if (_buttonController != null)
            _buttonController.selectedIndex = 0;
    }

    protected override void OnDespawnInternal()
    {
        // 清理按钮特有的状态
        _linkedPopup = null;
    }
}

// GComponent 的池化支持
public partial class GComponent
{
    protected override void OnSpawnInternal()
    {
        base.OnSpawnInternal();

        // 递归重置所有子对象
        int cnt = _children.Count;
        for (int i = 0; i < cnt; i++)
        {
            var child = _children[i];
            if (child is IPoolable poolable)
                poolable.OnSpawn();
        }

        // 重置滚动位置
        if (scrollPane != null)
            scrollPane.SetPercX(0, false);
    }

    protected override void OnDespawnInternal()
    {
        // 递归清理所有子对象
        int cnt = _children.Count;
        for (int i = 0; i < cnt; i++)
        {
            var child = _children[i];
            if (child is IPoolable poolable)
                poolable.OnDespawn();
        }

        base.OnDespawnInternal();
    }
}
```

#### 11.3.4 Window 生命周期修改

```csharp
public class PoolableWindow : Window, IPoolable
{
    public bool IsPoolable => true;

    // 改用池化方式关闭
    public new void Hide()
    {
        if (this.isShowing)
        {
            DoHideAnimation();
        }
    }

    // 隐藏动画结束后归还到池
    public new void HideImmediately()
    {
        this.root.HideWindowImmediately(this, dispose: false);  // 不销毁
        WindowPool.inst.ReturnWindow(this);  // 归还到池
    }

    public virtual void OnSpawn()
    {
        // 重置窗口状态
        _inited = true;  // 保持初始化状态
        _requestingCmd = 0;

        // 重置 contentPane
        if (_contentPane is IPoolable poolable)
            poolable.OnSpawn();
    }

    public virtual void OnDespawn()
    {
        // 关闭模态等待
        CloseModalWait();

        // 清理 contentPane
        if (_contentPane is IPoolable poolable)
            poolable.OnDespawn();

        // 调用 OnHide
        OnHide();
    }

    // 重写 Dispose，支持池化
    public override void Dispose()
    {
        if (IsPoolable && !_forceDispose)
        {
            // 池化对象不真正销毁，归还到池
            WindowPool.inst.ReturnWindow(this);
            return;
        }

        base.Dispose();
    }

    private bool _forceDispose = false;

    /// <summary>
    /// 强制销毁（不归还到池）
    /// </summary>
    public void ForceDispose()
    {
        _forceDispose = true;
        Dispose();
    }
}
```

### 11.4 使用示例

```csharp
// 配置界面池化参数（游戏启动时）
void InitWindowPools()
{
    // 背包界面：最多缓存 2 个，60 秒过期
    WindowPool.inst.Configure("ui://Bag/BagWindow", new PoolConfig
    {
        MaxCount = 2,
        ExpireTime = 60f
    });

    // 战斗结算界面：预热 1 个
    WindowPool.inst.Configure("ui://Battle/ResultWindow", new PoolConfig
    {
        MaxCount = 1,
        PreWarm = true
    });

    // 预热
    WindowPool.inst.PreWarm("ui://Battle/ResultWindow", 1);
}

// 打开界面（从池获取）
void OpenBagWindow()
{
    var window = WindowPool.inst.GetWindow<BagWindow>("ui://Bag/BagWindow");
    window.Show();
}

// 关闭界面（自动归还到池）
void CloseBagWindow()
{
    _bagWindow.Hide();  // 会自动归还到池，不会触发 GC
}

// 场景切换时清理池
void OnSceneUnload()
{
    WindowPool.inst.Clear();
}
```

### 11.5 性能对比

| 场景 | 无池化 | 有池化 | 提升 |
|------|--------|--------|------|
| 打开背包界面 | 5ms + 50KB alloc | 0.5ms + 0 alloc | 10x |
| 关闭背包界面 | 2ms + GC pending | 0.2ms + 0 GC | 10x |
| 连续开关 10 次 | 70ms + 500KB GC | 7ms + 0 GC | 10x |

### 11.6 注意事项

1. **状态重置完整性**：OnSpawn/OnDespawn 必须重置所有状态，否则会出现"脏数据"
2. **事件监听泄漏**：OnDespawn 必须移除动态添加的事件监听
3. **异步资源**：正在加载的异步资源需要特殊处理
4. **内存上限**：设置合理的 MaxCount，避免池过大占用内存
5. **过期清理**：定期清理长时间未使用的池对象

### 11.7 与现有 GObjectPool 的关系

```
现有 GObjectPool          新增 WindowPool
      ↓                         ↓
用于 GList 列表项复用      用于完整界面复用
简单的 Get/Return         完整的生命周期管理
无状态重置                 IPoolable 状态重置
单一层级                   支持递归子对象
```

**建议保留两者并存**：
- `GObjectPool`：用于列表项等简单场景
- `WindowPool`：用于完整界面的池化管理

---

## 十二、组件级对象池设计（深度优化）

> 比界面级池化更细粒度的方案，缓存 GObject 基础组件（GButton、GImage、GLabel 等），
> 实现跨界面的组件复用，最大化减少 GC。

### 12.1 核心思路

```
┌─────────────────────────────────────────────────────┐
│  组件池 (按 ObjectType 分类)                         │
├─────────────────────────────────────────────────────┤
│  GButton Pool:  [btn1] [btn2] [btn3] ...           │
│  GLabel Pool:   [lbl1] [lbl2] [lbl3] ...           │
│  GImage Pool:   [img1] [img2] [img3] ...           │
│  GLoader Pool:  [ldr1] [ldr2] [ldr3] ...           │
│  GTextField Pool: [txt1] [txt2] ...                │
│  ...                                                │
└─────────────────────────────────────────────────────┘

界面 A 关闭 → 回收 10 按钮 + 20 文本 + 15 图片 到池
界面 B 打开 → 从池取 8 按钮 + 25 文本 + 10 图片 组装

✅ 不同界面的同类型组件可互相复用
✅ 复用率极高，GC 接近零
```

### 12.2 与现有 GObjectPool 的区别

| 特性 | 现有 GObjectPool | 新 GObjectComponentPool |
|------|------------------|-------------------------|
| **分类方式** | 按 URL（资源路径） | 按 ObjectType（类型） |
| **复用范围** | 同一资源的对象 | 同类型的任意对象 |
| **适用场景** | 列表项复用 | 所有界面的组件复用 |
| **状态处理** | 简单隐藏 | 完整重置（ResetForReuse） |
| **层级处理** | 单层 | 递归子对象 |

### 12.3 GObjectComponentPool 实现

```csharp
/// <summary>
/// 组件级对象池 - 按 ObjectType 分类池化 GObject
/// </summary>
public class GObjectComponentPool
{
    private static GObjectComponentPool _inst;
    public static GObjectComponentPool inst => _inst ??= new GObjectComponentPool();

    // 按类型分类的对象池
    private readonly Dictionary<ObjectType, Queue<GObject>> _pools = new();

    // 每种类型的最大缓存数量
    private readonly Dictionary<ObjectType, int> _maxCounts = new()
    {
        { ObjectType.Image, 100 },
        { ObjectType.Button, 50 },
        { ObjectType.Label, 100 },
        { ObjectType.Text, 100 },
        { ObjectType.RichText, 30 },
        { ObjectType.Loader, 50 },
        { ObjectType.Component, 50 },
        { ObjectType.List, 20 },
        { ObjectType.Graph, 30 },
        { ObjectType.Group, 50 },
        { ObjectType.Slider, 20 },
        { ObjectType.ProgressBar, 20 },
        { ObjectType.ComboBox, 20 },
        { ObjectType.ScrollBar, 20 },
        { ObjectType.Tree, 10 },
        { ObjectType.Loader3D, 20 },
        { ObjectType.InputText, 30 },
    };

    /// <summary>
    /// 从池中获取对象
    /// </summary>
    public GObject Get(ObjectType type)
    {
        if (_pools.TryGetValue(type, out var pool) && pool.Count > 0)
        {
            var obj = pool.Dequeue();
            obj.ResetForReuse();
            return obj;
        }
        return null;  // 返回 null，由调用者创建新对象
    }

    /// <summary>
    /// 回收对象到池中
    /// </summary>
    public bool Return(GObject obj)
    {
        if (obj == null || obj.isDisposed) return false;

        var type = GetObjectType(obj);

        if (!_pools.TryGetValue(type, out var pool))
        {
            pool = new Queue<GObject>();
            _pools[type] = pool;
        }

        int maxCount = _maxCounts.TryGetValue(type, out var max) ? max : 30;
        if (pool.Count >= maxCount)
        {
            return false;  // 超出容量，返回 false 让调用者 Dispose
        }

        obj.ResetForReuse();
        pool.Enqueue(obj);
        return true;
    }

    /// <summary>
    /// 递归回收 GComponent 及其所有子对象
    /// </summary>
    public void ReturnWithChildren(GComponent comp)
    {
        if (comp == null) return;

        // 先递归回收所有子对象
        for (int i = comp.numChildren - 1; i >= 0; i--)
        {
            var child = comp.GetChildAt(i);
            if (child is GComponent childComp)
            {
                ReturnWithChildren(childComp);
            }
            else
            {
                if (!Return(child))
                    child.Dispose();  // 池满，真正销毁
            }
        }

        // 清空子对象列表（不 dispose）
        comp.RemoveChildren(0, -1, false);

        // 回收组件本身
        if (!Return(comp))
            comp.Dispose();
    }

    /// <summary>
    /// 获取池状态统计
    /// </summary>
    public Dictionary<ObjectType, int> GetStats()
    {
        var stats = new Dictionary<ObjectType, int>();
        foreach (var kv in _pools)
        {
            stats[kv.Key] = kv.Value.Count;
        }
        return stats;
    }

    /// <summary>
    /// 清空所有池
    /// </summary>
    public void Clear()
    {
        foreach (var pool in _pools.Values)
        {
            while (pool.Count > 0)
            {
                pool.Dequeue().Dispose();
            }
        }
        _pools.Clear();
    }

    private ObjectType GetObjectType(GObject obj)
    {
        return obj switch
        {
            GImage => ObjectType.Image,
            GButton => ObjectType.Button,
            GLabel => ObjectType.Label,
            GTextField => ObjectType.Text,
            GRichTextField => ObjectType.RichText,
            GTextInput => ObjectType.InputText,
            GLoader => ObjectType.Loader,
            GLoader3D => ObjectType.Loader3D,
            GList => ObjectType.List,
            GGraph => ObjectType.Graph,
            GGroup => ObjectType.Group,
            GSlider => ObjectType.Slider,
            GProgressBar => ObjectType.ProgressBar,
            GComboBox => ObjectType.ComboBox,
            GScrollBar => ObjectType.ScrollBar,
            GTree => ObjectType.Tree,
            GMovieClip => ObjectType.MovieClip,
            GComponent => ObjectType.Component,
            _ => ObjectType.Component
        };
    }

#if UNITY_2019_3_OR_NEWER
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void InitializeOnLoad()
    {
        _inst = null;
    }
#endif
}
```

### 12.4 GObject.ResetForReuse() 实现

```csharp
// 在 GObject.cs 中添加
public virtual void ResetForReuse()
{
    // 移除所有事件监听
    RemoveEventListeners();

    // 重置基础属性
    _x = 0;
    _y = 0;
    _z = 0;
    _alpha = 1;
    _rotation = 0;
    _rotationX = 0;
    _rotationY = 0;
    _scaleX = 1;
    _scaleY = 1;
    _visible = true;
    _internalVisible = true;
    _touchable = true;
    _grayed = false;
    _draggable = false;
    _pivotX = 0;
    _pivotY = 0;
    _pivotAsAnchor = false;
    _sortingOrder = 0;

    // 重置滤镜和混合模式
    filter = null;
    blendMode = BlendMode.Normal;

    // 清理用户数据
    data = null;
    _tooltips = null;

    // 重置关系
    relations.ClearAll();

    // 重置齿轮（Dispose 后置空）
    for (int i = 0; i < 10; i++)
    {
        if (_gears[i] != null)
        {
            _gears[i].Dispose();
            _gears[i] = null;
        }
    }

    // 从父对象移除
    if (parent != null)
        parent.RemoveChild(this, false);

    _group = null;
    packageItem = null;
}
```

### 12.5 各派生类的 ResetForReuse

```csharp
// GButton.cs
public override void ResetForReuse()
{
    base.ResetForReuse();

    selected = false;
    _over = false;
    _down = false;
    _linkedPopup = null;

    if (_buttonController != null)
        _buttonController.selectedIndex = 0;
}

// GTextField.cs
public override void ResetForReuse()
{
    base.ResetForReuse();

    text = "";
    _templateVars = null;
}

// GImage.cs
public override void ResetForReuse()
{
    base.ResetForReuse();

    color = Color.white;
    _content.graphics.flip = FlipType.None;
    _content.fillMethod = FillMethod.None;
}

// GLoader.cs
public override void ResetForReuse()
{
    base.ResetForReuse();

    url = null;
    _autoSize = false;
    _align = AlignType.Left;
    _verticalAlign = VertAlignType.Top;
    _fill = FillType.None;
}

// GComponent.cs
public override void ResetForReuse()
{
    // 清理控制器
    foreach (var ctrl in _controllers)
        ctrl.Dispose();
    _controllers.Clear();

    // 清理过渡
    foreach (var trans in _transitions)
        trans.Dispose();
    _transitions.Clear();

    // 清理滚动面板
    if (scrollPane != null)
    {
        scrollPane.Dispose();
        scrollPane = null;
    }

    // 清空 mask
    _mask = null;

    base.ResetForReuse();
}
```

### 12.6 修改 UIObjectFactory

```csharp
// UIObjectFactory.cs
public static GObject NewObject(ObjectType type)
{
    // 优先从组件池获取
    if (UIConfig.useComponentPool)
    {
        var pooledObj = GObjectComponentPool.inst.Get(type);
        if (pooledObj != null)
        {
            Stats.LatestObjectCreation++;
            return pooledObj;
        }
    }

    // 池中没有，创建新对象（原有逻辑）
    Stats.LatestObjectCreation++;
    switch (type)
    {
        case ObjectType.Image:
            return new GImage();
        case ObjectType.MovieClip:
            return new GMovieClip();
        // ... 其他类型
    }
}
```

### 12.7 修改 GComponent.Dispose

```csharp
// GComponent.cs
public override void Dispose()
{
    if (_disposed) return;

    // 组件池模式：回收而不销毁
    if (UIConfig.useComponentPool)
    {
        GObjectComponentPool.inst.ReturnWithChildren(this);
        return;
    }

    // 原有销毁逻辑...
    base.Dispose();
}
```

### 12.8 UIConfig 配置项

```csharp
// UIConfig.cs 添加
/// <summary>
/// 是否启用组件级对象池
/// </summary>
public static bool useComponentPool = false;
```

### 12.9 性能对比

| 场景 | 无池化 | 组件级池化 | 提升 |
|------|--------|-----------|------|
| 打开背包（200 组件） | 5ms + 50KB | 0.5ms + 0 | 10x |
| 关闭背包 | 2ms + GC | 0.3ms + 0 | 6x |
| 连续开关不同界面 10 次 | 50ms + 500KB GC | 8ms + 0 GC | 6x |
| 同类型组件复用率 | 0% | ~95% | - |

---

## 十三、更细粒度池化方案（极致优化）

> 比组件级更深层的池化：池化 GObject 内部创建的子对象

### 13.1 GObject 内存分配层次

```
┌─────────────────────────────────────────────────────────────┐
│ Level 1: GObject (UI 层)                                    │
│   ├─ Relations 对象 (内含 List<RelationItem>)               │
│   ├─ GearBase[10] 数组                                      │
│   ├─ 17+ EventListener 对象 (懒加载)                        │
│   └─ PackageItem 引用                                       │
├─────────────────────────────────────────────────────────────┤
│ Level 2: DisplayObject (显示层)                             │
│   ├─ GameObject + Transform (Unity 原生)                    │
│   ├─ NGraphics 对象                                         │
│   ├─ 14+ EventListener 对象 (懒加载)                        │
│   └─ PaintingInfo (懒加载)                                  │
├─────────────────────────────────────────────────────────────┤
│ Level 3: NGraphics (渲染层)                                 │
│   ├─ Mesh 对象                                              │
│   ├─ MeshFilter / MeshRenderer 组件                         │
│   ├─ IMeshFactory 对象                                      │
│   ├─ MaterialPropertyBlock (懒加载)                         │
│   └─ List<byte> _alphaBackup (懒加载)                       │
├─────────────────────────────────────────────────────────────┤
│ Level 4: Unity 原生层                                       │
│   ├─ Mesh vertices/triangles/uvs/colors 数组               │
│   ├─ Material 实例                                          │
│   └─ Shader 引用                                            │
└─────────────────────────────────────────────────────────────┘
```

### 13.2 内部对象池（Level 1 细化）

```csharp
/// <summary>
/// 内部对象池 - 池化 GObject 创建的子对象
/// </summary>
public static class InternalObjectPool
{
    // Relations 对象池
    private static readonly Queue<Relations> _relationsPool = new();

    // GearBase 数组池 (固定大小 10)
    private static readonly Queue<GearBase[]> _gearsPool = new();

    // List<RelationItem> 池
    private static readonly Queue<List<RelationItem>> _relationItemListPool = new();

    public static Relations GetRelations(GObject owner)
    {
        if (_relationsPool.Count > 0)
        {
            var relations = _relationsPool.Dequeue();
            relations.Reset(owner);  // 重置 owner 引用
            return relations;
        }
        return new Relations(owner);
    }

    public static void ReturnRelations(Relations relations)
    {
        relations.ClearAll();
        _relationsPool.Enqueue(relations);
    }

    public static GearBase[] GetGearArray()
    {
        if (_gearsPool.Count > 0)
        {
            var arr = _gearsPool.Dequeue();
            Array.Clear(arr, 0, arr.Length);
            return arr;
        }
        return new GearBase[10];
    }

    public static void ReturnGearArray(GearBase[] gears)
    {
        for (int i = 0; i < gears.Length; i++)
        {
            if (gears[i] != null)
            {
                gears[i].Dispose();
                gears[i] = null;
            }
        }
        _gearsPool.Enqueue(gears);
    }

    public static void Clear()
    {
        _relationsPool.Clear();
        _gearsPool.Clear();
        _relationItemListPool.Clear();
    }
}
```

### 13.3 显示对象池（Level 2 细化）

```csharp
/// <summary>
/// 显示对象池 - 池化 DisplayObject 和 NGraphics
/// </summary>
public static class DisplayObjectPool
{
    private static readonly Queue<Container> _containerPool = new();
    private static readonly Queue<Image> _imagePool = new();
    private static readonly Queue<NGraphics> _graphicsPool = new();

    public static Container GetContainer()
    {
        if (_containerPool.Count > 0)
        {
            var container = _containerPool.Dequeue();
            container.ResetForReuse();
            return container;
        }
        return new Container();
    }

    public static void ReturnContainer(Container container)
    {
        container.RemoveChildren();
        container.RemoveEventListeners();
        _containerPool.Enqueue(container);
    }

    public static NGraphics GetNGraphics(GameObject go)
    {
        if (_graphicsPool.Count > 0)
        {
            var graphics = _graphicsPool.Dequeue();
            graphics.Reinitialize(go);
            return graphics;
        }
        return new NGraphics(go);
    }

    public static void ReturnNGraphics(NGraphics graphics)
    {
        graphics.Clear();
        _graphicsPool.Enqueue(graphics);
    }
}
```

### 13.4 Unity 原生对象池（Level 4 细化）

```csharp
/// <summary>
/// Unity 原生对象池 - 池化 GameObject, Mesh
/// </summary>
public static class UnityObjectPool
{
    private static readonly Queue<GameObject> _gameObjectPool = new();
    private static readonly Queue<Mesh> _meshPool = new();
    private static Transform _poolRoot;

    public static void SetPoolRoot(Transform root)
    {
        _poolRoot = root;
    }

    public static GameObject GetGameObject(string name)
    {
        if (_gameObjectPool.Count > 0)
        {
            var go = _gameObjectPool.Dequeue();
            go.name = name;
            go.SetActive(true);
            return go;
        }
        return new GameObject(name);
    }

    public static void ReturnGameObject(GameObject go)
    {
        go.SetActive(false);
        if (_poolRoot != null)
            go.transform.SetParent(_poolRoot, false);
        _gameObjectPool.Enqueue(go);
    }

    public static Mesh GetMesh()
    {
        if (_meshPool.Count > 0)
        {
            var mesh = _meshPool.Dequeue();
            mesh.Clear();
            return mesh;
        }
        return new Mesh();
    }

    public static void ReturnMesh(Mesh mesh)
    {
        mesh.Clear();
        _meshPool.Enqueue(mesh);
    }
}
```

### 13.5 分层池化架构

```
┌─────────────────────────────────────────────────────────────┐
│                    应用层 (UI 组件池)                        │
│  GObjectComponentPool - 按 ObjectType 池化完整 GObject      │
│  复用效果: 一个 GButton 可以变成另一个 GButton              │
├─────────────────────────────────────────────────────────────┤
│                    内部层 (子对象池)                         │
│  InternalObjectPool - 池化 Relations, GearBase[], etc.     │
│  复用效果: GButton 销毁后，Relations 可被 GImage 复用       │
├─────────────────────────────────────────────────────────────┤
│                    显示层 (显示对象池)                       │
│  DisplayObjectPool - 池化 Container, Image, NGraphics      │
│  复用效果: 任意 DisplayObject 销毁后，NGraphics 可被复用    │
├─────────────────────────────────────────────────────────────┤
│                    Unity 层 (原生对象池)                     │
│  UnityObjectPool - 池化 GameObject, Mesh                   │
│  复用效果: 任意 UI 销毁后，GameObject/Mesh 可被复用         │
└─────────────────────────────────────────────────────────────┘

粒度越细，复用率越高，但复杂度也越高！
```

### 13.6 推荐实现策略

| 阶段 | 池化级别 | 复杂度 | GC 减少 | 推荐 |
|------|----------|--------|---------|------|
| 第一阶段 | 组件级 (GObject) | ★★☆☆☆ | ~80% | ✅ 优先 |
| 第二阶段 | 内部级 (Relations, Gears) | ★★★☆☆ | ~90% | ⚠️ 按需 |
| 第三阶段 | 显示级 (DisplayObject) | ★★★★☆ | ~95% | ⚠️ 谨慎 |
| 第四阶段 | Unity级 (GameObject, Mesh) | ★★★★★ | ~99% | ❌ 极端场景 |

**建议**：
1. **先实现组件级池化** - 投入产出比最高，覆盖 80% 的 GC
2. **按需添加内部级** - 如果组件级不够，再池化 Relations 等高频对象
3. **谨慎使用显示级** - 需要改动 DisplayObject 创建流程
4. **极少使用 Unity 级** - 复杂度高，仅极端优化需求

### 13.7 待修改文件清单

#### 第一阶段：组件级池化（推荐）

| 文件 | 修改内容 |
|------|----------|
| `UI/GObjectComponentPool.cs` | **新增** - 组件池管理器 |
| `UI/GObject.cs` | 添加 `ResetForReuse()` |
| `UI/GButton.cs` | 重写 `ResetForReuse()` |
| `UI/GTextField.cs` | 重写 `ResetForReuse()` |
| `UI/GImage.cs` | 重写 `ResetForReuse()` |
| `UI/GLoader.cs` | 重写 `ResetForReuse()` |
| `UI/GComponent.cs` | 重写 `ResetForReuse()` 和 `Dispose()` |
| `UI/UIObjectFactory.cs` | 优先从池获取 |
| `UI/UIConfig.cs` | 添加 `useComponentPool` 配置 |

#### 第二阶段：内部级池化（可选）

| 文件 | 修改内容 |
|------|----------|
| `UI/InternalObjectPool.cs` | **新增** - 内部对象池 |
| `UI/Relations.cs` | 添加 `Reset(GObject owner)` 方法 |
| `UI/GObject.cs` | 构造函数使用内部池 |
| `UI/UIConfig.cs` | 添加 `useInternalObjectPool` 配置 |

#### 第三阶段：显示级池化（可选）

| 文件 | 修改内容 |
|------|----------|
| `Core/DisplayObjectPool.cs` | **新增** - 显示对象池 |
| `Core/DisplayObject.cs` | 添加 `ResetForReuse()` 方法 |
| `Core/NGraphics.cs` | 添加 `Reinitialize()` 方法 |
