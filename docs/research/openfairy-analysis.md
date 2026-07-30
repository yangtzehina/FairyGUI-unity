# OpenFairy-SDK-uGUI 精读：可借鉴清单

来源：https://github.com/OpenFairyGUI/OpenFairy-SDK-uGUI （clone 于 ~/ECS/OpenFairy-SDK-uGUI，2026-07-30，
六领域多代理精读：codegen/rendering/motion/testing/controls/strategy）。

**定位**：与本仓库互补的另一条 FairyGUI 现代化路线——**烘焙优先**：编辑期把 FairyGUI 工程 XML
转成 uGUI prefab + 强类型 C#（~2.7k 行转换器 + ~6k 行运行时），运行时零解析、零 `ui://` 残留。
成熟度校准：134/134 双运行时对照测试**只覆盖 Basics/Transition 两个 demo**；其余 30+ 官方包仅证明
"能迁移能编译"（199 组件），未做渲染/交互验证。引用它作 M8 可行性证据时须按此口径。

---

## 一、立即可抄（对当前 v4 主线）

### 1. 验证基建（最高价值——正中"临时 eval 脚本"痛点）

他们的 134/134 对照测试设计，逐条可搬：

- **双运行时同帧 lockstep**：参照方与被测方在同一帧创建、同一帧 t0 起播，
  `Time.captureDeltaTime = 1/60` 锁相位，`runInBackground = true`；FairyGUI 系 tween 必须
  `ignoreEngineTimeScale = false`（我们的 GTween 同源同坑）。我们比他们更便宜：legacy
  DisplayObject 渲染器和 v4 流**在同一个仓库里**，同进程同帧对照即可。
- **官方 ImageAssert 口径**：`com.unity.testframework.graphics`，
  `IncorrectPixelsCount + DeltaGamma`、`PerPixelGammaThreshold = 2/255`，按页经验校准
  坏像素**比例**阈值——不是均值误差。容忍 1px 文本/贴图边缘 halo，抓得住布局/内容错误。
- **金图不入库**：gitignore，测前本地同 GPU 重生成；缺金图 = 硬失败并提示生成命令。
  绕开而非解决跨机稳定性——代价是金图门禁需要有 GPU 的 runner。
- **alpha 模型中和**：对比前两边都清成同一不透明底色——straight vs premultiplied 的数值差
  在透明底上会假阳（我们 v4 shader vs 原生 shader 完全同病）。
- **双层拆分**：像素金图证"任意状态渲染一致"；交互测试只比**几何快照**
  （节点路径 + 设计像素 rect + activeSelf 的 .geo.json），不重复比像素。
  v4 版本："actual" 从编译后的 quad 流/transform 槽 dump（实例 rect/可见性/clip/段），
  "reference" 从活 DisplayObject 树 dump——直接测编译器正确性。
- **单一数据驱动 catalog**：`ParityPage/InteractionCase` readonly struct 数组同时喂金图生成器和
  `[ValueSource]` 测试，加 case = 加一行。
- **枚举不抽样**（真实事故后立的规矩）：结构层扫每个组件源，断言声明的交互扩展都烘成了
  交互类型；v4 对应物：枚举每个编译子树，断言每个 DisplayObject 都被记账为
  实例/段/显式 fallback 三者之一。
- **菜单 + 结果文件协议**：TestRunnerApi 固定菜单入口、`[InitializeOnLoad]` 每次域重载重挂回调、
  结果写 `Temp/...Results.txt` 首行机读。
- **人查性工件**：金图生成顺带输出 film strip（缓动起始到静止的 12 个前密采样时刻，
  参照行/被测行/diff 行）——验证轨迹相位，端帧金图验不了。

### 2. 工程纪律（AGENTS.md 模式）

- 「明确不做」段（代理不再反复重议范围）+「踩坑」段（根因化的陷阱记录）；
- **三菜单纪律**：公开工具入口只留 Migrate / Generate Golden / Run Tests，临时调试菜单提交前删；
- 状态文档带日期验证行（"2026-07-10 Migrate 101 组件、134/134"），未验证状态显式标注。

### 3. 渲染细节两则

- **平铺图单 quad 化**：tiled = 1 个 quad + UV 跨 N 重复 + repeat 采样 + 顶边相位公式
  （v0/v1 同移 floor(v1)-v1，整块格线对齐顶边、碎格落右下——FairyGUI 从顶起铺）。
  v4 流里 tiled GImage 目前走网格重组出 N 个 quad；单实例化直接省实例数，
  段键差异（repeat 采样标志）注意。
- **文本布局缓存键清单**：(text 引用, rect 尺寸, fontSize, fontStyle, 对齐, h/v overflow,
  leading, letterSpacing, font 引用) 全等才复用布局；仅 color 变化走免重排快路径。
  这是我们推送通道文本脏分类的现成规格——批 3 颜色 tier 的 tint-不触发-重排已对齐，
  此清单可作通道分类完备性检查表。

### 4. API 糖一则

- **可等待弹窗**：ShowPopup/Window.Show 返回 task（池化 completion source），关闭即完成——
  `await dialog` 替代关闭事件簿记，与 MVVM binder 天然配对。

## 二、M8（source-gen 线）的证据与蓝图

### 可行性证据（M8a）

- 全 FairyGUI 授权面（controller/gear/relation/transition/list/位图字体/movieclip）**确实**可
  编译为静态数据 + 生成代码，运行时零字符串零反射（反射显式隔离在烘焙期）。
- 规模校准：完整烘焙管线 ≈ 3k 行，不是子系统重写级——de-risk M8a 预算。
- 容器 relation 证实可仿射折叠（27 种 RelationSide → anchor 公式）；**不可折叠边界**同样
  清晰：兄弟目标 relation、百分比 pivot 组合等留运行时残差。
- gear 语义折叠为按页常量表（pages[] + values[] 平行数组、'-' 默认哨兵）——
  正是 §15 "per-pageIndex 分支发射" 的实证。
- transition 折叠为 TransitionItem[]（24fps→秒、ease 枚举、NaN=播放时捕获当前值约定、
  烘焙期坐标折叠、陈旧目标剪除）——现成的预编译动效格式蓝本。

### 生成类形态（M8b 直接采用）

- 组件类内嵌套 controller enum（页=成员），`Controller<TEnum>` struct 字段
  （非 MonoBehaviour），gear 表泛型化挂在 controller 上；
- **int-switch 虚桥**：`Get/SetControllerPage(int)` 生成 switch 转发到类型化字段——
  非泛型引擎代码零反射触达强类型 controller；
- 烘焙接线一律 `[SerializeField] internal` + `InternalsVisibleTo`（烘焙与测试程序集），
  不进用户 API 面；
- codegen 边界用例表（直接当我们 emitter 的测试集）：C# 关键字/标点折叠碰撞、
  基类成员遮蔽（`new enum`）、已删页引用剪除、跨包 `global::` 防遮蔽、幂等重迁移清理。

### codegen 死锁解法（照抄，~30 行）

内容比较写盘（无谓重编译归零）→ 有变化或编译中则 SessionState 记 pending 返回 →
`[DidReloadScripts]` + delayCall 恢复执行 → TypeCache 从新程序集解析生成类型再烘 prefab。
恢复手册：sed 生成脚本旧类型名 + `CompilationPipeline.RequestScriptCompilation(CleanBuildCache)`。

### 战略裁决（写回 §15）

- **纯烘焙后端（M8b 全量替换）的代价是明确且不可谈判的**：无 CreateObject、无运行时包加载、
  列表虚拟化被推迟、文本残差长存——OpenFairy 把这些列为设计决策而非欠账。
  我们保留 DisplayObject 运行时 + v4 流的路线在动态内容上保有结构性优势。
- 因此 M8 的正确形态是**混合**：静态子树烘焙成预编译 quad 流 + 生成强类型外观类，
  动态部分（虚拟列表、运行时装配、文本）留给现有 v4 运行时流——两条路线共享段/槽协议。
- **输入分歧**：他们解析编辑器工程 XML；我们应烘焙**二进制包字节**——运行时 parser
  已在库内百战，且不要求用户交出编辑器工程。
- **烘焙工件脆性**（设计约束）：`[SerializeReference]` 跨程序集/命名空间移动即碎（须重烘或
  迁移）；运行时类型身份即 ABI；烘焙产物加版本戳；烘焙流程 day-one 就做两阶段域重载安全。
- **命中测试教训**：烘焙子树没有逐节点对象后，hit 面必须作为显式数据编入流
  （他们给按钮根烘透明全 rect raycast 面）——v4 烘焙子树同理要编 per-control hit quad。

## 三、不抄的（及原因）

- **uGUI 组件映射层**：我们保留原生 NGraphics 网格（其 Graph/Text 移植是照抄 FairyGUI 公式，
  信息量为零——我们就是原件）。
- **依赖赌注**：UniTask/ZLinq/DOTween 三个手装前置对一个库是负资产；v4 保持零依赖
  （GTween/GPath 在树内）。awaitable API 若做，走可选 asmdef。
- **运行时 controller 替换**：enum 模型预设 per-component codegen + 域重载，杀运行时包加载
  和 `GetController("name")` API——只用于 M8 生成类，不动运行时。
- **ScrollRect**：连 uGUI 系 SDK 都弃用 ScrollRect 自写 400 行（0.967/帧衰减、0.25 橡皮筋）
  ——反向确认我们保留自家 ScrollPane 是对的。
- **真弧长样条**（负面教训转正面纪律）：他们换 com.unity.splines 时刻意保持 FairyGUI GPath
  参数化口径（1001 采样点最大偏差 6.3e-5px）——"换底层实现不换行为口径"，与我们
  双后端像素等价纪律同源。
