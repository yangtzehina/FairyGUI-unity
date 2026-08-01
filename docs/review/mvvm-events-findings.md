# 多代理评审遗留缺陷清单（MVVM / 事件 / 生成器）

来源：2026-07 的多代理代码评审（15 条 CONFIRMED）。渲染层的 M 编号缺陷
（M1/M2/M8/M9/M10/M11/M12/M13/M14、W2）根因是 MergedBatch 的"CPU 烘焙副本
失效通道数不全"——**已由 v4 实例流整类取代**（MergedBatch 已标记
[Obsolete] 并与流互斥），不再逐条跟踪。本文件跟踪与渲染无关、必须独立
修复的部分。

## 状态

| 编号 | 摘要 | 状态 |
|---|---|---|
| V1 | Binder.Flush 快照 dirtyMask 后调用无参 ClearDirty() 整体清零：apply 回调期间对同一 VM 的新写入被吞掉且下帧不补刷 | **已修（第 1 批）**：ViewModel.ClearDirty(ulong mask) 按快照位清除；apply 期间的级联写入保留到下一次 Flush |
| V6 | Flush 按缓存的 cnt 索引遍历 _groups：apply 回调中 Unbind（RemoveAt）导致跳组或 ArgumentOutOfRange；Clear 同理 | **已修（第 1 批）**：Flush/ApplyAll 迭代快照列表，Unbind/Clear 对组打 unbound 墓碑；回调中 Bind 的新组从下一次 Flush 生效（经核实 Bind 半边本就是良性的） |
| U1 | BaseFont.textRebuildFlag 触发 Stage 整树双遍 Update，字体图集重建帧全树成本 ×2（上游原有行为，被认领叶放大浪费） | **已结（2026-08-01，实测推翻原描述）**：双遍走树只占重建帧的约 12%，"成本 ×2"不成立；真正的开销是图集重建本身。详见下方 §U1 |
| E1 | 事件层——原定义已丢失，2026-08-01 重审后**重新定义为三条**（E1a/E1b/E1c，见 §E1） | **已修**：三条全部修复，E1a 是我们自己引入的回归 |
| S2 / S5 | Source Generator——原定义已丢失，重审后**重新定义为九条**（见 §S2/S5） | **已修（第二批）**：九条全修，常驻行为门 `tools/FairyGUI.Mvvm.Generator.Tests`（18 项，对修前代码 16 红、修后 18 绿双向验证） |
| V7 | Flush/ApplyAll 共用一个 `_flushScratch` 快照字段：apply 回调里再调 Flush/ApplyAll 会清空外层正在索引的缓冲 → `ArgumentOutOfRangeException`，且外层组的脏位已被消费，UI 永久失步 | **已修（本批）**：改为按嵌套深度租用快照 + **组级 `applying` 闸**（见 §V7 的事故记） |
| V8 | 墓碑只在组**之间**生效：`Unbind`/`Clear` 拦不住正在 apply 的那一组，同组后续 entry 仍会写进已 Dispose 的视图——正是 `Unbind` 文档承诺的语义 | **已修（本批）**：entry 循环内逐条重测 `unbound` |
| V9 | `BindList` 按值捕获集合实例，属性重新赋值后永远渲染旧集合 | **已修（第二批）**：主 API 改为取值委托 `Func<IReadOnlyList<T>>`；实例重载保留、降级为"仅就地变更"并借道委托版实现 |
| V10 | 虚拟列表 itemRenderer 用 GList 的陈旧索引空间去索引活模型 | **已修（第二批）**：renderer 内对**当前**集合做范围检查，越界静默跳过（下一次 Flush 会重对齐重渲染）；实例重载借道委托版后同样受益 |
| V11 | `KeyedListDiffer` 先记新 key 再 render：render 抛异常会让该行被永久标记为干净 | **已修（第二批）**：两个分支都改为 render 先、记账后——抛异常则旧 key 保留，下一次 Apply 自动重试 |

## E1 重新定义（2026-08-01 重审，三条全部已修）

原条款只留下"事件层（EventTypeRegistry/int-ID 化 相关）"一句。重审后确认它指向的
**很可能是 E1a**——因为那正是 int-ID 化那次提交（`e96f994`）引入的回归。

**E1a（major，已修）——空回调从"被忽略"变成"派发时抛 NRE"。**
上游 `EventBridge.Add` 用的是多播委托：`_callback1 -= callback; _callback1 += callback;`。
`Delegate.Combine(d, null)` 返回 `d`，所以 `Add(null)` 是彻底的空操作，`isEmpty` 保持 true，
派发根本不会发生。`e96f994` 把存储换成 `List<T>` 后没有补空值守卫，于是 null 进了列表，
而 `isEmpty` 是**按 Count 判断**的——它开始说谎，派发分支被走进，`CallInternal` 在
`snapshot[i](context)` 上抛 NullReferenceException。

比"一个回调失效"严重：null 落在索引 0，抛在第一个 entry 上，**该对象该事件类型上后加的
所有真实监听器一起失效**，而调用栈指向 `EventBridge.CallInternal`，离注册点十万八千里。
仓库内就有漏斗：`PopupMenu.CreateItem` 里 `callback is EventCallback0` 对 null 恒为 false
（C# 语义），于是 null 走 else 分支被强转成 `(EventCallback1)null` 交进去——
`menu.AddItem(label, e.handler)` 里 handler 字段为空就中招。

修法：`Add`/`AddCapture` 三个重载各加一句 `if (callback == null) return;`，恢复上游契约。
守卫放在**桥**上而不是 `EventListener` 上，因为 `AddEventListener` 也直连桥。

**E1b（minor，已修）——静态触摸链在异常后污染后续所有帧。**
`Stage.TouchInfo.sHelperChain` 是**静态**的、被五个 TouchInfo 槽共享，而 `Move()`/`End()`
里 `BubbleEvent(...)` 之后才 `Clear()`，中间没有 try/finally。BubbleEvent 会执行任意用户
handler；只要有一个抛异常，陈旧的 bridge 就永久留在链里，此后每一次触摸都会派发给它们——
包括早已拆除的子树上的对象。修法：两处都套 try/finally。

**E1c（minor，已修）——池化的 EventContext 归还时仍持有 callChain。**
`callChain` 只在下一次派发**开始时**清空，所以躺在静态池里的 context 会一直握着上一次广播
收集到的全部 EventBridge，连带让一棵已被销毁的子树无法回收。修法：`Return` 时清空。

## V7 事故记：修复本身差点更糟（值得留档）

V7 的第一版只做了"按嵌套深度租用快照"——缓冲区不再互相踩踏了。为它写的回归测试
（apply 回调里调 `ApplyAll`）**连续三次把编辑器崩掉**，崩溃签名是
`EXC_BAD_ACCESS ... stack guard region`，栈顶在 Mono 分配路径：**栈溢出，即无限递归**。

根因不在快照，在语义：**`ApplyAll` 按设计不看脏位**，所以"apply 回调调 ApplyAll"会把
这个回调自己再应用一遍 → 再调 ApplyAll → 无穷。`Flush` 那一侧碰巧不会，因为脏位在
apply 之前就被消费掉了；只有 `ApplyAll` 露出这个洞。

最终修法是两层：按深度租用快照（解决缓冲区）**加上组级 `applying` 闸**——一个组正在
apply 时，嵌套的 Flush/ApplyAll 跳过它。这既终止递归，又保留了嵌套调用的正当用途
（"子视图重建了，重新同步一下"仍能刷新**其他**组）。

教训：**递归性质的修复要先在独立进程里验证终止**。改用 `dotnet run` 编译
`Assets/MVVM/{Binder,ViewModel}.cs` 跑同样四个场景，几秒钟拿到结论，比崩三次编辑器便宜得多。

## S2/S5 重新定义（2026-08-01 重审，九条，未修）

三个 Roslyn 生成器的缺陷都表现为**用户工程里的生成代码出错**。九条按影响排序：

| # | 生成器 | 缺陷 |
|---|---|---|
| 1 | Bind | 无嵌套/泛型容器守卫：partial 被发到**命名空间**层，指向的不是用户那个类型（`ObservableGenerator` 有 FGM005 守卫，另两个没有） |
| 2 | Bind | `type.GetMembers()` 只返回**本类型声明**的成员：基类上的 `[Bind]` 被静默忽略，`BindTo` 发出空方法体，编译干净但 UI 永不更新 |
| 3 | Bind | 文档写的"GObject 派生字段 + bool → `.visible`"规则对 `GTextField`/`GProgressBar`/`GSlider` **不可达**——按字段类型分支的早返回把它挡在后面 |
| 4 | FuiView | **生成的视图会过期**：管线只携带 `AdditionalText.Path`，.fui 内容变了而路径没变时 Roslyn 认为输入未变，走缓存不重新生成 |
| 5 | FuiView | 名为 `component` 的子元件**遮蔽 Bind 的参数**，其后每个字段都绑错 |
| 6 | FuiView | 非法 C# 标识符的子元件名被静默丢弃，无诊断——**本仓库 Basics 包里现存两个** |
| 7 | FuiView | `[FuiView]` 标在嵌套类上会把 partial 发到错误作用域；hint name 冲突会**中止整个生成器** |
| 8 | Observable | `DerivePropertyName` 不校验标识符合法性：`_2ndSlot` 生成语法非法的文件（FuiView 有 `IsValidIdentifier`，它没有） |
| 9 | Observable | 属性索引按 `SourceSpan` 原始偏移排序、不区分文件：partial ViewModel 拆多文件时，**无关空白改动会让 `public const int XxxProperty` 的值变化** |

九条已全部修复（2026-08-01 第二批）。验收是常驻行为门
`tools/FairyGUI.Mvvm.Generator.Tests`（`dotnet run` 驱动 CSharpGeneratorDriver 跑**真实
生成器** + 真实 Basics 包字节，18 项，判定行 `RESULT pass=N fail=N`）：每条缺陷一项，
并以**双向验证**立门——修后代码 18 绿，把三个生成器换回修前版本实测 **16 红**
（含审计描述过的 FGM103 误报与 CS8785 生成器整体中止原样复现）。

两条实现注记：

- **`AdditionalText.GetText()` 对二进制文件在真 csc 宿主里直接报 CS2015**——测试宿主
  静默解码，所以这个调用在 18/18 全绿之后才在 Unity 编译里炸出来。.fui 是二进制，
  内容版本改为自读字节算 FNV。测试宿主与真宿主的行为差异，只有真编译一遍才能发现。
- g4（内容过期）必须用**同一个 driver 增量重跑**来测：新 driver 每次全量执行，测不到
  缓存缺陷；且诱变字节要打在**文件头**——打中间可能落在解析器根本不读的段里。

## 待办：本次重审新发现、尚未修的三条

- **V9 `BindList` 按值捕获集合**：两个重载都把 `IReadOnlyList<T> items` 捕进两个长命闭包，
  传入的属性索引只当脏位选择器用，**没有任何地方重新读属性**。视图模型把列表属性整体换成
  新实例后，界面永远渲染旧集合。修法需要 API 决策（改成传取值委托，或每次 apply 重读属性）。
- **V10 虚拟列表索引空间**：`itemRenderer` 直接 `items[index]`，而 `index` 来自 GList 缓存的
  `_numItems`；GList 自己滚动时就会重渲染，binder 只在 Flush 时把两边对齐。缩表后滚动可越界。
- **V11 `KeyedListDiffer` 先记 key 后 render**：render 抛异常（如 GLoader 指向缺失资源）后，
  差分器已认定该行显示的是新 key，该行从此不再更新。修法是把记账挪到 render 之后。

## U1 结案：双遍走树不是成本中心（2026-08-01 实测）

原条款按结构推断得出"Update 跑两遍 → 整树成本 ×2"。实测不支持这个推断。

测量方式：61 个认领叶 + 60 个文本字段的实例流场景，逐帧记录 `Stage.ForceUpdate()`
耗时，并按该帧 `BaseFont.version` 是否变化（即是否真的重建了图集）**分组**——
关键在于分组：把重建帧与非重建帧平均在一起会把结论稀释掉，我头两次测量就是
这么失败的（还有一次更基础的失误：用的 CJK 字形早被会话预热过，`version` 增量为 0，
测的全是空气）。

| 帧形态 | 耗时 | 样本 |
|---|---|---|
| A：文本全量改写，无新字形 | 0.64 ms | 10 |
| B-plain：有新字形，未触发图集重建 | 1.64 ms | 35 |
| **B-rebuild：图集增长 → 双遍走树** | **23.43 ms** | 5 |

重建帧确实是一次实打实的掉帧（60 fps 预算 16.7 ms）。但把 `Stage.cs` 里第二遍
`Update` 临时注释掉再测同一场景，重建帧仍要 **20.76 ms**——

> **双遍走树 ≈ 2.7 ms，占重建帧溢价（21.8 ms）的约 12%。**

其余约 19 ms 是图集重建本身：Unity 的字体纹理重分配，加上 `version` 变更导致
**全部**文本网格失效重建（那是第一遍就付掉的，与第二遍无关）。

结论与处置：

- **不摘除第二遍**。它换来的是"本帧文字不显示错位 UV"，代价只有约 12%；
  摘掉它省不了多少，却会在每次图集增长时闪一帧错误文字。
- 原条款提到的"被认领叶放大浪费"同样不成立：认领叶在第二遍里没有网格可重建，
  正是它们让第二遍便宜。
- 附带核实的 `Stats` 口径问题（`UpdateContext.Begin()` 重置计数器，重建帧跑两遍
  → 第一遍的计数被丢弃）**只影响 `Stats.Merged*` 四个计数器**，而它们的唯一写入方
  是已废弃的 `MergedBatch`（本仓库 `[Obsolete]` 且与实例流互斥）。不值得动代码。
- 真正值得写进使用指引的是**图集重建本身**：新字形成批出现的那一帧会有约 20 ms
  尖峰。缓解手段是开屏前预热字形（验证 harness 的 `env.WarmGlyphs` 就是这个用途），
  以及把字体图集初始尺寸调够——都属于工程接入约定，不是引擎代码缺陷。

## 教训

评审产出必须当天落盘到仓库（本文件即补救）。E1/S2/S5 因只存在于会话记忆
而无法验收，重审成本高于当初落盘成本一个数量级——**这次重审花了 30 个代理、
210 万 token，产出 20 条确认（去重后 16 条）**，而当初落盘只需要几分钟。

第二条教训（2026-08-01 新增）：**递归性质的修复先在独立进程验证终止再进编辑器**。
V7 的回归测试崩了三次编辑器才让我看清递归的真正来源，而同样的四个场景用
`dotnet run` 编译 `Assets/MVVM/*.cs` 跑，几秒就有结论。

## 验收方式

`Assets/Examples/Mvvm/BinderReentrancyCheck.cs`，一条
`eval "return BinderReentrancyCheck.Run();"` 跑完，随 `InstancedValidationAll` 一并执行。
**V1/V6 11 项 + V7/V8 7 项 = 18 项**，全绿；全量门禁双后端 522/522。
后续任何评审修复都应附带同等验证。
