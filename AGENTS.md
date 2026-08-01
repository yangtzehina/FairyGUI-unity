# AGENTS.md

本项目的 AI/协作者开工约定。开始工作前先读本文件，再按需读 `docs/design/` 下的设计文档。
有实质进展时同步更新对应状态表（本文件、`docs/design/m8-bake-line.md` §6、
`docs/design/instanced-renderer-v4.md` 里程碑注记）。

## 项目是什么

FairyGUI-unity 的现代化 fork（分支 `poc/gpu-instanced-ui`，名字已名不副实——它是完整功能线）：

- **v4 quad 实例流**：子树编译成 GPU 常驻 quad 实例流，推送式脏协议（content/transform/
  visible/structure 四通道），transform 槽（内层移动 tier-1）、跨图集段键（≤4 纹理/段）、
  per-instance 裁剪、颜色 tier。设计书 `docs/design/instanced-renderer-v4.md`。
- **曲线文本**：TTF glyf → GPU 解析覆盖（`CurveBaseFont` 一行注册），数据纹理后端全平台，
  CJK 小字号实测优于原生动态字体。
- **MVVM binder**：`Assets/MVVM/`，重入语义有 11 项行为测试（`BinderReentrancyCheck.Run()`）。
- **M8 烘焙线**：FQS1 blob（编辑期跑真编译器冻结输出）+ 挂载融合 + enum 外观 codegen +
  可见性/内变换 tier + renderless 叶。设计书与状态表 `docs/design/m8-bake-line.md`。

核心运行时**零第三方依赖**。所有里程碑均带验收门（编译门 + Unity 套件 + 像素对照）。

## 环境

- **驱动编辑器**：UniCli。`export UNICLI_PROJECT=/Users/ai/ECS/FairyGUI-unity` 后
  `~/bin/unicli exec Compile / PlayMode.Enter / PlayMode.Exit`、`~/bin/unicli eval "<C#>"`
  （eval 编译进临时程序集，可访问 UnityEditor；`--no-focus` 不抢焦点）。
- **Unity**：2022.3.62f3（Hub 安装，含 WebGL/iOS 模块）。`ProjectSettings/ProjectVersion.txt`
  必须保持 2022.3.62f3——见踩坑第 1 条。
- **桌面编译门**（快速 C# 语法关，跑在 Unity 编译之前）：netstandard2.1 csproj 引
  `UnityEngine.Modules 2021.3.33`，glob `Assets/Scripts/**` + `Assets/MVVM/**`。
  DefineConstants 必须带完整链
  `UNITY_5_6_OR_NEWER;UNITY_2017_1_OR_NEWER;...;UNITY_2021_3_OR_NEWER`——
  缺 `UNITY_2017_1_OR_NEWER` 会走进 `EventType.scrollWheel` 的 obsolete-error 分支（CS0619）。
- **.NET SDK** 在 `~/.dotnet`（不在 PATH）。git 全局代理 7890 已死，
  用 `git -c http.proxy= -c https.proxy= <cmd>` 直连；push 走 `ssh://git@ssh.github.com:443/`。

## 标准工具入口（三菜单纪律）

公开菜单只保留固定入口，临时调试菜单提交前删：

1. `Tools/FairyGUI/Bake Packages (FQS)` —— blob + 生成视图一趟出（play mode + 包已加载）。
2. `Tools/FairyGUI/Run FQS Parity` —— 常设对照门禁（枚举不抽样 + 像素/几何双层 + 篡改阶梯），
   结果写 `Temp/FqsParityResults.txt`，判定行 `FQS PARITY VERDICT: PASS|FAIL`。
3. `FairyGUI/Instanced UI Streams` —— 流诊断面板（段/quads/槽/认领/重编译计数）。
4. `Tools/FairyGUI/Run Validation Suites` —— 跑仓库内的 253 项行为/像素/不变量套件
   （需已在 Play 模式；无头形态见下）。
5. `Tools/FairyGUI/Run Perf Gates` —— 墙钟比值门（需新鲜 Play 会话）。

CI 类入口（非菜单）：

- `FairyGUIEditor.InstancedCIBuild.BuildWebGL` —— WebGL 构建，M6CHECK 在浏览器
  控制台输出 `M6CHECK VERDICT` 判定行。
- `FairyGUIEditor.InstancedValidationCI.Run` —— 无头跑全套验证：自己开场景、进
  Play、写报告到 `Logs/InstancedValidationResults.txt`、按结果设退出码，判定行
  `INSTANCED VALIDATION VERDICT: PASS|FAIL pass=N fail=M`。
  **不要传 `-quit`**（入口要活过域重载并自行 Exit）、**不要传 `-nographics`**
  （要回读像素）；同工程不能与 GUI 编辑器同时打开。
- `FairyGUIEditor.InstancedPerfCI.Run` —— 无头跑比值性能门，报告
  `Logs/InstancedPerfResults.txt`，判定行 `INSTANCED PERF VERDICT: PASS|FAIL`。
  同上三条禁忌，且**必须与行为套件分进程**（见验证纪律）。

## 验证纪律

- **每个里程碑三道门**：桌面编译门 0 错 → Unity 编译 0 错 0 警 → 行为/像素套件全绿。
  绿了才提交，提交信息里写验收数字。
- **后端覆盖：两条都要跑**。历史上本机编辑器的 buffer 路径（顶点级 StructuredBuffer）
  静默不出图，所以套件默认 `forceVertexPath = true`。**2026-07-31 复测该怪癖不再复现**
  （对照实验确认像素来自实例 draw），buffer 后端 227/227 全绿，双后端 454/454（自动挂载并入后 253 项，双后端 506/506）。
  默认仍留顶点流（怪癖若复发验证照跑），但**验收应跑 `-ciBackend both`**：两条后端是
  同一语义的两套 shader + 上传实现，只跑一条等于覆盖了一半。切换用
  `InstancedValidationEnv.useVertexBackend` / `InstancedValidationAll.RunOn(bool)` /
  `RunBothBackends()`；断言里要比对后端名时用 `InstancedValidationEnv.expectedBackend`，
  别写死字符串。真实 WebGL 已单独验证顶点路径逐像素正确。
- **性能门必须在新鲜 Play 会话跑**：长会话的 GC/驱动债务会扭曲微基准
  （实测同一测试 45% 降幅在长会话里读出 18% 误报）。**已按此重构为两层**：
  第一层 `InstancedPerfInvariantSuite`（各 tier 零重编译/零渲染器/零分配等**计数**
  断言，确定性，随行为套件跑）；第二层 `InstancedPerfRatioBench` 只测 **A/B 比值**
  且 **ABAB 交替**（那次事故的比值门错在 A、B 先后测，只有 B 背了债），共同成本移出
  计时区，阈值取实测的约 1/3，绝对 µs 只记录不设门。入口
  `FairyGUIEditor.InstancedPerfCI`，**必须单独一个 Unity 进程**（batchmode 冷启动
  = 天然新鲜会话），不能接在行为套件之后。
- **像素探针坐标**：`(逻辑坐标) × GRoot.contentScaleFactor` → 屏幕像素，y 翻转
  `RH-1-y`。验证前确认演示场景已打开（见踩坑第 3 条）。
- **验证套件已全部固化进仓库**：`Assets/Examples/InstancedPoC/Validation/`
  （17 套 253 项：M1 重组器 17、M3 裁剪栈 10、M7 SDF 17、M4 场景 19、批1-4
  14/8/19/10/12、批5 曲线文本 10、M8-1/2/4/5 15/19/20/14、自动挂载 25、
  性能不变量 13、MVVM 11）。
  跑法、harness 约定与坑见该目录 `README.md`；一条
  `eval "return InstancedValidationAll.Run();"` 跑完全部。
  **新增验证写进该目录**——早期套件只留在会话里，丢过一次，是靠提交信息重建的。

## 明确不做（终结重复讨论）

- **不替换运行时 Controller/字符串 API**。enum 类型化只存在于 M8 生成的外观类
  （facade 包装 index 驱动），运行时包加载和 `GetController("name")` 永远保留。
- **不做 per-组件生成 shader**（§15 结论）：变体切换断批，恰是 v4 要消灭的。
  单 shader + flags 静态分支覆盖 primitive 集。
- **换实现不换口径**：替换底层库/算法时保持原行为口径（参照 OpenFairy 换样条库
  保持 GPath 参数化、我们双后端像素等价的纪律）。
- **核心运行时零第三方依赖**。可等待 API、MemoryPack 等只进可选 asmdef；
  MemoryPack 仅在烘焙元数据出现 union 时启用（`m8-bake-line.md` §2 已裁决）。
- **烘焙器宁可少烘，不可错烘**：拒绝规则（文本/动画对象存在即拒、根 mask、
  非包纹理默认拒、blend 栅栏）不为覆盖率放松；blob 永远是可丢弃加速缓存，
  语义真源是 .fui。
- **MergedBatch 已废弃**（`[Obsolete]` + 与实例流互斥），不再修它的 bug。

## 踩坑（每条都真实炸过，多数炸过不止一次）

1. **永不 stash/提交 `Packages/manifest.json`、`ProjectSettings/`**。stash 循环曾把
   ProjectVersion 拉回 2022.3.17f1c1（中国版），触发强制重导入循环 + 编辑器实例损坏
   （整版黑屏事故的根因之一）。局部 stash 用 `git stash push -- Assets/...`。
2. **`unicli exec Compile` 不保证导入新文件/改过的 .shader**。新增 .cs 会让引用它的
   编译"成功地"失败（旧清单）；改 shader 后新 uniform 静默读零（曾致 `_TransformSlots`
   全零、槽 quad 全退化排查半小时）。修改后必须
   `AssetDatabase.Refresh(ForceSynchronousImport)` / `ImportAsset(..., ForceUpdate)` 再编译。
3. **多轮 PlayMode 循环后编辑器可能落在空场景**：相机无引导、全屏零像素，所有探针
   黑屏——先像素 sanity（原生红块），黑了就
   `EditorSceneManager.OpenScene("Assets/Examples/Scenes/Example 15 - VirtualList.unity")`。
4. **eval 里的 `delayCall`/`update` 委托会蒸发**：委托活在 eval 临时程序集里，被下一次
   eval 编译覆盖后静默消失（WebGL 构建"没在打包"的根因）。长操作用**同步 eval** +
   客户端后台等待（编辑器侧会执行完）。
5. **`FontManager.GetFont(name)` 对未注册名自动创建 DynamicFont**——存在性检查必须
   按类型（`is CurveBaseFont`），按空值永远为真。
6. **DrawRect 家族签名**：`GGraph.DrawRect(w,h,line,lineCol,fillCol)` 5 参；
   核心 `Shape.DrawRect(line,lineCol,fillCol)` 3 参；`GGraph.DrawRoundRect(w,h,fill,radii[])` 4 参。
7. **烘焙实例必须挂无缩放 Stage**（`Stage.inst.AddChild(displayObject)`）：GRoot 的
   UIContentScaler 会把 GameView 尺寸漂进烘焙浮点低位，破坏跨机重烘的字节确定性。
8. **动态内容拒绝按"对象存在"判定**，不按渲染态 flag：字体图集冷热会让文本 mesh
   为空、leaf 根本不进流，flag 检查在冷图集下漏判（同一组件冷烘热拒的不确定性事故）。
9. **`Object.Destroy` 帧末生效**：同帧 eval 里已 Dispose 的流曾以僵尸段继续渲染污染
   探针（引擎侧已修：段 GO 先 `SetActive(false)` 再 Destroy），写探针时仍需留意此类
   帧末语义。
10. **autoSize 文本尺寸变化走结构通道**（`OnSizeChanged → InvalidateBatchingState`），
    绕过文本 slack 照常重编译——by design，slack 只对固定框文本生效。
11. **zsh 陷阱**：`echo ===X===` 会被当作参数解析报错；`grep -c` 计数为 0 时退出码 1
    断掉 `&&` 链。脚本里用 `echo "===X==="` 加引号、`grep -c ... ; echo DONE` 分号续行。
    还有一条更隐蔽的：**双引号里的 `!` 会触发历史展开**——`unicli eval "... if (x != null) ..."`
    会被 zsh 改写成语法错误的 C#（报 CS1001 之类，看着像代码写错）。含 `!` 的 eval
    一律先写进文件再 `eval "$(cat file)"`（heredoc 用 `<<'EOF'` 引号形式禁展开）。
12. **python 批量编辑仓库文件**：断言锚点 + 末尾统一写盘（失败零半态）；锚点必须
    对着**当前**文件内容取（批次迭代后旧锚点常失效，见 batch3/M8 多次返工）。
    **改文件前先看它的字节编码**：上游带来的文件（如 `Assets/Scripts/UI/UIPackage.cs`）
    是 BOM + CRLF，用 `open(p,'w',encoding='utf-8')` 写回会把整份文件换成无 BOM + LF——
    16 行的改动会显示成 3400 行全文件 diff，既没法审阅也污染历史。这类文件一律
    `open(p,'rb')` 读、按字节替换（换行写成 `\r\n`）、`open(p,'wb')` 写。
    改完 `git diff --stat` 看行数是否与改动量相称。
13. **`Temp/` 在编辑器退出时被清空**：菜单跑法（FqsParityRunner）无碍，但
    **batchmode 把结果写进 `Temp/` 会在 CI 读到之前消失**——无头结果一律写
    `Logs/`（已 gitignore）。实测过：退出码与判定行都对，文件却不存在。
14. **像素探针不要手算 stage 坐标**：GameView 分辨率 + `UIContentScaler` 会让
    GRoot 带缩放（实测 0.55），手算必错；一律走 `GObject.LocalToGlobal`。区域扫描
    要先换算角点再直接索引截图数组——逐点调 `LocalToGlobal` 是原生调用，全区域扫
    会把主线程卡到形似死机（排查过一次，误以为编辑器崩了）。
15. **裸 `new ScrollPane(gcomp)` 会让显示树成环**（rootContainer 被挂进自己子孙）→
    遍历死循环卡死编辑器。必须先复刻 `SetupScroll` 的容器拆分，见
    `InstancedBatch4Suite.ScrollHost`。
16. **`deferRenderers` 内容必须有流可认领**：宽限期条件是
    `liveInPlaceCount > 0 && ++_renderlessUpdates <= 2`——没有活跃 in-place 流时
    `_EnsureNative()` 首帧就把渲染器全建出来，省不到任何东西（写基准时踩过：
    开窗节省从 22% 掉到 9%，一度以为是分层退化）。
17. **像素探针不要压几何边缘**：滚动/矩阵写有半像素取整，探针落在色带边界
    （或色带间隙旁）会随会话状态翻转。探针至少离边缘 10px，且 Check 消息里
    带上实测 RGB（a7 教训：三腿合一的裸 bool 无法定位失败腿）。
18. **跨越"流尚未存在"窗口的绑定必须推迟**：`_NotifyStructure` / `_NotifyTransform`
    开头都是 `if (liveInPlaceCount == 0) return;`——**没有活跃 in-place 流时，推送通道
    是死的**。任何在这个窗口里建立、却指望通道来失效的绑定（mount、认领、缓存的
    叶引用）都会带着过期状态活到流起来那一刻，然后静默画错。构造期只允许留**纯数据**，
    真正的绑定交给流在 extract 时做——那一刻它正走着这棵树，看到的必然是当前结构。
19. **身份用 id，不用名字**：FairyGUI 允许同名 item（`_itemsByName` 后者覆盖前者），
    非导出组件也能与导出组件同名，分支变体是另一个 item，而 macOS 文件系统大小写
    不敏感。任何按名字建立的产物↔运行时对应关系（烘焙文件名、缓存键）都会在这四条
    里的某一条上静默串味。文件名要给人看就写 `名字_id`，让 id 定身份。

20. **验证 harness 不能从 player loop 内部驱动**：`InstancedValidationEnv.Step()` 走
    `Stage.inst.ForceUpdate()`，编辑器里是从循环**外**（eval）调用的。在 player 里从
    `Update`/`LateUpdate`/协程调用同一套，会触发
    `An abnormal situation has occurred: the PlayerLoop internal function has been called
    recursively`——Unity 直接中止主循环，页面还活着、画布保留最后一帧，**看起来像卡死或超时，
    其实是重入**（排查时误判成 WebGL 的 ReadPixels 太慢）。真机内的验证要写成
    逐帧状态机（见 `M6WebGLCheck`），不要复用编辑器 harness。
21. **浏览器会缓存 Unity 构建产物**：重新构建后 `navigate(force:true)` 不够——Unity loader
    自己拼 `Build/*.wasm|data|framework.js` 的 URL，index.html 上的 query 打不到它们，
    于是跑的还是旧构建（表现为"改的代码没生效"）。换一个端口起 http.server 即换缓存键。
    **同一标签页的控制台缓冲还会跨导航残留**：重跑后读到的可能是上一轮的日志（曾因此
    把"故障已修复"的构建读成仍然 FAIL——两轮数字一模一样才发现）。A/B 对照一律
    `tabs_create` 开新标签页读。

## 文档地图

| 文档 | 内容 |
|---|---|
| `docs/design/instanced-renderer-v4.md` | v4 总设计 + M1-M9 里程碑执行注记 + §15 编译期生成边界 |
| `docs/design/m8-bake-line.md` | 烘焙线立项书 + 六站状态表 + §4.5 使用指引（≥10 叶再 defer） |
| `docs/design/batch3-incremental.md` | 增量化主线（颜色 tier/Extract 增量化/transform 槽）设计与执行 |
| `docs/review/mvvm-events-findings.md` | MVVM/事件审计台账（V1/V6 已修；E1/S2/S5 定义丢失待重审） |
| `docs/research/openfairy-analysis.md` | OpenFairy 烘焙路线精读：可抄清单 + M8 混合形态裁决依据 |
| `docs/review/curve-text-ab/` | 曲线文本 A/B 裁决图与真机成像存档 |
