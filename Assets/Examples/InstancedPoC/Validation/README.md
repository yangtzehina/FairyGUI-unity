# Instanced 渲染器验证套件

v4 实例流渲染器（`Assets/Scripts/Core/Instanced/`）的行为回归套件。历次迭代的验证
最初都以会话内临时 eval 脚本存在，随会话清理丢失过一次——**新增验证请直接写进本目录**，
不要留在会话里。

## 跑法

**标准跑法（Unity Test Framework，任何环境）**：套件已挂进 UTF PlayMode——
`Assets/Tests/Validation/ValidationSuiteTests.cs` 给每套一个测试、按后端双
fixture 实例化（22 套 × 2 = 44 项，包着与下述全量相同的检查）。三个入口：

- Test Runner 窗口（PlayMode 页签）；
- 无编辑器 CI：`Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults Logs/utf-results.xml`
  （**不要 -nographics**，像素探针要读回渲染）；
- 编辑器内一键：菜单 `Tools/FairyGUI/Run Validation Suites (UTF)` 或
  `FairyGUIEditor.ValidationTestRunnerCI.Run()`——报告写
  `Logs/UtfValidationResults.txt`，判定行 `UTF VALIDATION VERDICT: PASS|FAIL pass=N fail=M`，
  文件出现即完成（驱动方以此轮询）。门可红已做过篡改验证（故意失败的探针
  测试把 FAIL 与断言消息一路带进报告）。

程序集拓扑（迁移的实质）：套件从 Assembly-CSharp 搬进 `FairyGUI.Validation`
asmdef（BinderReentrancyCheck 进 `FairyGUI.Mvvm.Validation`），测试程序集
`FairyGUI.Validation.Tests` 只在 UNITY_INCLUDE_TESTS 下编译、不进 player。
注意：UTF 按字母序跑，**套件必须保持相互独立**（各自建 env、finally 清理）；
带顺序语义的全量回归仍走下面的 eval 入口。

**有序全量（unicli eval）**：

```
UNICLI_PROJECT=/Users/ai/ECS/FairyGUI-unity
unicli exec Scene.Open '{"path":"Assets/Examples/InstancedPoC/Validation/ValidationScene.unity"}'
unicli exec PlayMode.Enter
unicli exec eval '{"code":"return InstancedValidationAll.Run();"}'
```

首行 `ALL RESULT pass=N fail=0` 即全绿；单套同理（`M4ScenarioSuite.Run()` 等）。
每套返回 `RESULT pass=N fail=N` 加逐项 PASS/FAIL 明细，失败项的探针实测值就印在项名里。

改过脚本后 `exec Compile` **不会**导入新文件——先在 edit 模式跑一次
`AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport)` 再 Compile。

## 双后端

顶点流后端与 buffer 后端（顶点级 StructuredBuffer）是**同一语义的两套 shader +
上传实现**——只跑一条,覆盖率就是名义上的一半。

```
InstancedValidationAll.RunOn(true)     // 顶点流(默认)
InstancedValidationAll.RunOn(false)    // buffer
InstancedValidationAll.RunBothBackends()
```

历史:本机编辑器的 buffer 路径曾**静默不出图**(shader 编译通过、caps=31、无报错、
无像素),套件因此一律钉在顶点流。**2026-07-31 复测不再复现**——新启动编辑器上
buffer 路径正常,且以对照实验坐实像素来自实例 draw(关段渲染器像素消失、开回来复现,
而叶子 `forceRenderingOff=true`,原生渲染器不参与)。全套 227 项在 buffer 后端一次
全绿,双后端合计 **454/454**（批5b 并入后 19 套 282 项/双后端 **564/564** 复验全绿；
作用域栅栏套件并入后 20 套 297 项/双后端 **594/594**；栅栏改绝对语义（无限盒）+ 对抗评审二轮回归并入后 20 套 308 项/双后端 **616/616**；曲线换字体回归 + 事件语义套件并入后 21 套 319 项/双后端 **638/638**；ColorFilter 认领缺口套件并入后 22 套 327 项/双后端 **654/654**；祖先 grayed + 四角渐变 + 档位命名回归并入后 22 套 334 项/双后端 **668/668**；对抗评审三轮加固并入后 22 套 340 项/双后端 **680/680**；MergedBatch 删除后 22 套 338 项/双后端 **676/676**；容量稳定不变量并入后 22 套 339 项/双后端 **678/678**，2026-08-08 实测）。

**当初的触发条件未查明**,所以默认仍是顶点流(怪癖若复发,验证照样跑得起来);
但正式验收请跑 `-ciBackend both`。

## 无头跑（CI）

```
Unity -batchmode -projectPath . \
      -executeMethod FairyGUIEditor.InstancedValidationCI.Run \
      [-ciOutput Logs/InstancedValidationResults.txt] \
      [-ciBackend vertex|buffer|both]
```

入口自己开验证场景、进 Play、跑汇总、写报告，然后**按结果设退出码**（全绿 0，
有失败 1）。日志与报告首行是可 grep 的判定行：

```
INSTANCED VALIDATION VERDICT: PASS pass=339 fail=0
```

harness 注意事项（都踩过）：

- **不要传 `-quit`**：入口必须活过进 Play 触发的域重载，它自己调
  `EditorApplication.Exit`。
- **不要传 `-nographics`**：套件要回读渲染像素。
- **不要把输出放 `Temp/`**：Unity 退出时会清空该目录，报告会在 CI 读到之前消失
  （默认已改为 `Logs/`）。
- 同一工程不能同时被 GUI 编辑器打开——无头跑前先关掉编辑器。

交互式跑用菜单 `Tools/FairyGUI/Run Validation Suites`（需已在 Play 模式）。

## 套件清单

| 套件 | 项数 | 覆盖 |
|---|---|---|
| `InstancedReassemblerSuite` | 19 | M1 quad 重组器合成数据：规范/替代索引模式、scale9 16 顶点→9 quad、UV 按包围盒角映射、90° 旋转、45° 拒绝、退化对、扇形伪阳性、色缺省、offset/flags/stride、**四角渐变拒绝**（压平回归：非均匀顶点色跳过）。**无场景无像素,验证栈的底座** |
| `InstancedClipStackSuite` | 10 | M3 裁剪栈：窗口变换含 y 取反、嵌套折叠(交集)、软裁剪非对称映射 (1,2,3,4)→(1,4,3,2)、去重、逐叶 clipIndex 盖章、遮罩子树跳过、裁剪区增多而 draw 数持平 |
| `InstancedScopeBarrierSuite` | 26 | 容器级作用域栅栏（审计缺口 + 对抗评审二轮回归）：stencil mask / painting / GoWrapper 三类三明治的**像素 z 序**（上蓝下红中间作用域，三探针）、作用域关 run 计数、运行时在 clip 容器上设 mask 触发重编译（notify 缺口回归）、root-mask 挂起认领/移除恢复（每挂起期各警一次）、**双 renderer GoWrapper**（块尾槽数学 _MaxRenderingOrder）、**fairyBatching 宿主**（eraser 走 SetRenderingOrderAll 赋序）、**相邻双作用域**（中间 run 被两栅栏夹持）、reversedMask 栅栏 + **运行时翻转通知**（属性化回归） |
| `InstancedM7SdfSuite` | 17 | M7 SDF：圆角/描边/正圆认领、饼图/渐变/真椭圆/旋转/非均匀缩放/超 255px 半径退回原生、quad 计数、半径与描边宽打包、像素探针(内部精确/角落裁掉/描边带)、原生回退可见并按 run 交织 |
| `M4ScenarioSuite` | 19 | 推送脏协议：隐藏/显示、滤镜开关、`graphics.enabled`、重父级即时自恢复、跨根移动单一 owner、多边形回退与再入、文本增长、dispose 恢复 |
| `InstancedColorFilterSuite` | 8 | ColorFilter 认领缺口（对抗评审确认的预存缺陷）：已认领 Image 叶**运行时**挂 ColorFilter（游戏图标置灰写法）→ 结构通道释放走原生、**灰度像素实测 ~(150,150,150)**、回退叶成 run 栅栏、无关重编译不再认领（谓词回归）、摘除滤镜后**全 null keyword 数组**仍可重新认领、自定义材质半边谓词双向 |
| `InstancedBatch1Suite` | 21 | blend 回退与 run 屏障、grayed 子树继承与精确 luma、**祖先 grayed**（流根上方置灰经通知+根链吸收，像素级）、**渐变矩形/渐变文本回退与再入**（含"生来即拒"叶的再入对等、被拒叶 alpha push 零重编译门、混合网格回滚零泄漏、reparent 穿越灰祖先）、重复流互斥（MergedBatch 半边随其删除退役） |
| `InstancedBatch2Suite` | 8 | 文本 slack 生命周期（缩/涨/越界/等长 churn、幽灵尾）、认领叶 sortingOrder 省写与释放回同步 |
| `InstancedBatch3Suite` | 19 | 颜色 tier c1-c6、transform 槽 t1-t10（含槽内 clip 跟随、溢出探针、嵌套槽）、Extract 增量化 e1-e3 |
| `InstancedBatch3dSuite` | 10 | 跨图集段键：合段、6 纹理 4+2 裂段、逐槽像素精确、churn 保槽位、grayed 共存 |
| `InstancedBatch4Suite` | 12 | 产品化：`instancedRendering` 开关、Stage 自动驱动与原生像素等价、内层移动/滚动走槽、开关往返、dispose 拆除 |
| `InstancedBatch5Suite` | 11 | 曲线文本：CurveBaseFont 注册与 shader 接线、side table、UBB 分段色、下划线实心 quad、字形 slack、顶点路径认领与实例字形像素、**换字体清侧表**（陈旧字形回归：Clear 移入 UpdateMeshNow） |
| `InstancedM81Suite` | 15 | M8-1 烘焙线：FQS1 blob 往返与逐位 quad 等价、字节级可重现、敌意输入拒绝、拒烘矩阵（text/MovieClip 存在性、root 遮罩、回退屏障、被遮罩子树、非包纹理）、SDF 标记 |
| `InstancedM82Suite` | 19 | M8-2 mount 融合：陈旧/篡改/敌意计数拒挂、拼接、**像素闸门 diff=0**、mount 走槽、烘焙叶上的内容/颜色 tier、同帧自愈、失效阶梯与回退 |
| `InstancedM84Suite` | 20 | M8-4 分层：可见性 g1-g16（叶/容器隐显零重编译、隐藏态跨重拼接存活、blob 外内容显示优雅失效）、内层变换 tier、t1-t4 六状态过场像素全等且零重编译 |
| `InstancedM85Suite` | 14 | M8-5 无渲染器叶：defer 作用域、认领态零渲染器、**像素闸门 diff=0**、tier-2/颜色 tier 免渲染器、命中测试、释放物化、宽限期回退 |
| `FqsAutoMountSuite` | 27 | CreateObject 自动挂载：装填/兑现时序、provider 缓存、defer 阈值、嵌套作用域、**像素对照 diff=0**、陈旧回退。其中 **8 项是以对抗审查确认缺陷命名的回归项**：删除内容不留幽灵（构造期绑定的核心缺陷）、id 定身份、非导出组件不查 blob、分支解析键、contentScaleLevel 门（含 **.sN 档位命名契约与 provider 候选序**）、无源哈希拒绝、实例级拒绝不锁定、Bake 恢复调用方 mount。另有 c14/c21-c24 验源哈希门：覆盖**全部加载路径**（真走一遍字节数组/bundle 形态并与 Resources 比同值），且门禁值**跨依赖包**（blob 会冻结别的包的几何与图集 UV） |
| `FqsSupersetSuite` | 13 | M8-7 超集烘焙：隐藏页进 blob（2→4 叶）、字节级重烘一致（还原纯度）、**首次显示零重编译**、六连翻页零重编译 + 逐状态像素全等、**命中测试三件套**（当前页可点/隐藏页不可点/翻页互换）、超集关闭时优雅降级 |
| `CurveEffectsSuite` | 8 | 批5b 曲线字体效果：假粗体加宽（uv 位）、描边环成色/干净关闭（property block 复位）、阴影方向、组合、**跨带接缝双向注入证明**（'三'+10px 描边：故障 ringed=0）、实例流 barrier/认领分流 + 接管像素 |
| `InstancedPerfInvariantSuite` | 15 | **性能不变量(确定性)**：各 tier 零重编译(槽/滚动/颜色/文本 slack/挂载移动/挂载内隐显)、三纹理合 1 段、段 GO 池化、idle Render 零分配、40 个 renderless 叶零渲染器、加叶不加段、**顶点上传宽度 72B/顶点**（声明值/布局求和/结构体 marshal 三者一致——字段被悄悄加宽时像素门全绿也会被抓到）、**相同开关两轮容量不增**（approxResidentBytes 账本） |
| `EventSemanticsSuite` | 10 | 事件层语义（e96f994 的 7 项断言落盘 + 3 项扩展）：双 Add 去重、派发中增/删走快照（只影响下一轮）、capture→target→bubble 次序、嵌套异型/同型派发不腐蚀外层快照、isDispatching 计数器语义（嵌套后外层仍 true）、派发中 RemoveEventListeners、context data/sender、未知类型查询安静。E1a 类回归的常设门 |
| `BinderReentrancyCheck` | 18 | MVVM Binder 重入（在 `Assets/Examples/Mvvm/`，由 `InstancedValidationAll` 一并调用） |

**性能门分两层。** 上表最后一套是**第一层:确定性不变量**——把速度承诺里不含时间的部分
断言成精确计数(18× 之所以是 18×,根因是重编译 120→0,不是某个 µs 数)。计数是确定的,
所以它和行为套件跑在一起,永远不会 flake。

**GPU 成本测量工具**（不是门，不进 ALL）：`CurveGpuCostBench` —— 曲线文本 fragment
成本实测，`FairyGUIEditor.CurveGpuCostCI.BuildMac()` 出 macOS player 后
`Build/MacCurveGpu.app/Contents/MacOS/* -curvegpu -curvegpuOut <path>` 跑，判定行
`CURVEGPU VERDICT`（门槛=测量有效性，数字本身是交付物）。编辑器里
`CurveGpuCostBench.StartInEditor()` 可调通 harness（编辑器 Metal 也报 GPU 时间，
仅作趋势）。方法学注意：60fps 封顶 + 跨轮取最小（AGENTS 坑位 23）。

**第二层是墙钟比值门**(`InstancedPerfRatioBench`,入口 `FairyGUIEditor.InstancedPerfCI`)。

```
Unity -batchmode -projectPath . \
      -executeMethod FairyGUIEditor.InstancedPerfCI.Run \
      [-ciOutput Logs/InstancedPerfResults.txt]
```

判定行 `INSTANCED PERF VERDICT: PASS|FAIL pass=N fail=M`,退出码随结果。
**必须单独一个 Unity 进程跑**,不能接在 339 项行为套件后面——那 338 项自己就会留下
GC/驱动债,而 batchmode 冷启动天然就是"新鲜会话"。

方法学(针对 M8-5 那次「45% 被读成 18%」的事故):

- **只测 A/B 比值,绝不测绝对值**;绝对 µs 照记进报告当趋势线,不设门。
- **ABAB 交替测量**,比值取逐轮中位数。那次事故的比值门本身没错,错在 A、B 先后测:
  只有 B 背了会话累积的债。交替之后漂移同等作用于两侧,从比值里消掉。
- **共同成本移出计时区**(如关窗口的 `Dispose`):两侧都付的钱只会稀释比值。
- 阈值取实测值的约三分之一。门是用来抓**塌方**的,不是抓 10% 波动——抓 10% 的门
  一个月内就会被无视。

实测标定(M4/Metal,2022.3.62f3,连续 4 轮 + 1 次无头新鲜会话):

| 门 | 实测 | 阈值 |
|---|---|---|
| 槽移动 vs 重编译 | 53.7-55.4× | ≥5× |
| 挂载 extract vs 走树 | 5.21-5.57× | ≥3× |
| tier-2 重写 vs 重编译 | 74.7-83.5× | ≥3× |
| renderless 开窗 vs 普通开窗 | 省 22.2-25.5% | ≥15% |

最后一条的阈值有**双向实测依据**:健康态 22-26%,而损坏态(早期版本让 deferred 内容
在无流可认领时开窗,渲染器首帧全部物化)实测 9.1%。15% 落在两者之间,两侧都有余量。

M8 线的另两站不在这里，因为它们在 **editor 程序集**、本目录（Assembly-CSharp）够不着：

- **M8-3 代码生成**：`Assets/Editor/FqsViewGenerator.cs`，验收要跨域重载两阶段
  （生成 → 重载 → 类型加载/枚举翻页），由菜单 `Tools/FairyGUI/Bake Packages`
  与 `[DidReloadScripts]` 校验器自带；负向编译探针（删页应 CS0117）本就无法在
  单次 eval 内表达。
- **M8-6 常驻对拍闸门**：`Assets/Editor/FqsParityRunner.cs`（菜单
  `Tools/FairyGUI/Run FQS Parity` 或 CI 调 `Run()`，结果写
  `Temp/FqsParityResults.txt`）。它枚举已加载包的**每个**导出组件做
  runtime vs mounted 对拍，与本目录的程序化夹具互补：那边覆盖真实包资产，
  这边覆盖机制边界。

## 写新套件时的约定

`InstancedValidationEnv` 是共享 harness，照抄现有套件的骨架即可：

- **后端由 env 统一钉住**，别自己写 `forceVertexPath`。默认顶点流;
  `InstancedValidationEnv.useVertexBackend = false` 切 buffer 路径,
  断言里比对后端名用 `InstancedValidationEnv.expectedBackend`(别写死字符串)。
  历史原因见下方「双后端」一节。
- **`env.Step(n)` 驱帧**，内部是 `Stage.ForceUpdate()`——整套可以在一条 eval 里跑完，
  不需要 MonoBehaviour 跨帧。
- **像素探针一律走 `env.Probe(px, obj, lx, ly)`**（内部 `LocalToGlobal`）。
  绝不手算 stage 坐标：GameView 分辨率与 `UIContentScaler` 会让 GRoot 带缩放
  （常见 0.55），手算必错。
- **区域扫描用 `AnyBright`/`AllNear`/`DiffCount`/`DiffStats`**。它们先换算角点再直接
  索引截图数组；逐点调 `LocalToGlobal` 是原生调用，全区域扫会把主线程卡到形似死机。
- **文本断言前先 `env.WarmGlyphs("...")`**：动态字体首次遇到新字形会扩图集，
  `NTexture.onSizeChanged` 会打脏流并触发重编译，把 `extractCount` 断言弄花。
- **滤镜 painting 捕获与 blendMode 材质切换隔 1-2 帧生效**，像素断言前多驱两帧。
- **不要对未拆分容器 `new ScrollPane(gcomp)`**：会把 rootContainer 挂进自己子孙，
  显示树成环、遍历死循环卡死编辑器。参照 `InstancedBatch4Suite.ScrollHost` 先复刻
  `SetupScroll` 的容器拆分。
