# Instanced 渲染器验证套件

v4 实例流渲染器（`Assets/Scripts/Core/Instanced/`）的行为回归套件。历次迭代的验证
最初都以会话内临时 eval 脚本存在，随会话清理丢失过一次——**新增验证请直接写进本目录**，
不要留在会话里。

## 跑法

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

## 无头跑（CI）

```
Unity -batchmode -projectPath . \
      -executeMethod FairyGUIEditor.InstancedValidationCI.Run \
      [-ciOutput Logs/InstancedValidationResults.txt]
```

入口自己开验证场景、进 Play、跑汇总、写报告，然后**按结果设退出码**（全绿 0，
有失败 1）。日志与报告首行是可 grep 的判定行：

```
INSTANCED VALIDATION VERDICT: PASS pass=215 fail=0
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
| `InstancedReassemblerSuite` | 17 | M1 quad 重组器合成数据：规范/替代索引模式、scale9 16 顶点→9 quad、UV 按包围盒角映射、90° 旋转、45° 拒绝、退化对、扇形伪阳性、色缺省、offset/flags/stride。**无场景无像素,验证栈的底座** |
| `InstancedClipStackSuite` | 10 | M3 裁剪栈：窗口变换含 y 取反、嵌套折叠(交集)、软裁剪非对称映射 (1,2,3,4)→(1,4,3,2)、去重、逐叶 clipIndex 盖章、遮罩子树跳过、裁剪区增多而 draw 数持平 |
| `InstancedM7SdfSuite` | 17 | M7 SDF：圆角/描边/正圆认领、饼图/渐变/真椭圆/旋转/非均匀缩放/超 255px 半径退回原生、quad 计数、半径与描边宽打包、像素探针(内部精确/角落裁掉/描边带)、原生回退可见并按 run 交织 |
| `M4ScenarioSuite` | 19 | 推送脏协议：隐藏/显示、滤镜开关、`graphics.enabled`、重父级即时自恢复、跨根移动单一 owner、多边形回退与再入、文本增长、dispose 恢复 |
| `InstancedBatch1Suite` | 14 | blend 回退与 run 屏障、grayed 子树继承与精确 luma、MergedBatch/重复流互斥 |
| `InstancedBatch2Suite` | 8 | 文本 slack 生命周期（缩/涨/越界/等长 churn、幽灵尾）、认领叶 sortingOrder 省写与释放回同步 |
| `InstancedBatch3Suite` | 19 | 颜色 tier c1-c6、transform 槽 t1-t10（含槽内 clip 跟随、溢出探针、嵌套槽）、Extract 增量化 e1-e3 |
| `InstancedBatch3dSuite` | 10 | 跨图集段键：合段、6 纹理 4+2 裂段、逐槽像素精确、churn 保槽位、grayed 共存 |
| `InstancedBatch4Suite` | 12 | 产品化：`instancedRendering` 开关、Stage 自动驱动与原生像素等价、内层移动/滚动走槽、开关往返、dispose 拆除 |
| `InstancedBatch5Suite` | 10 | 曲线文本：CurveBaseFont 注册与 shader 接线、side table、UBB 分段色、下划线实心 quad、字形 slack、顶点路径认领与实例字形像素 |
| `InstancedM81Suite` | 15 | M8-1 烘焙线：FQS1 blob 往返与逐位 quad 等价、字节级可重现、敌意输入拒绝、拒烘矩阵（text/MovieClip 存在性、root 遮罩、回退屏障、被遮罩子树、非包纹理）、SDF 标记 |
| `InstancedM82Suite` | 19 | M8-2 mount 融合：陈旧/篡改/敌意计数拒挂、拼接、**像素闸门 diff=0**、mount 走槽、烘焙叶上的内容/颜色 tier、同帧自愈、失效阶梯与回退 |
| `InstancedM84Suite` | 20 | M8-4 分层：可见性 g1-g16（叶/容器隐显零重编译、隐藏态跨重拼接存活、blob 外内容显示优雅失效）、内层变换 tier、t1-t4 六状态过场像素全等且零重编译 |
| `InstancedM85Suite` | 14 | M8-5 无渲染器叶：defer 作用域、认领态零渲染器、**像素闸门 diff=0**、tier-2/颜色 tier 免渲染器、命中测试、释放物化、宽限期回退 |
| `BinderReentrancyCheck` | 11 | MVVM Binder 重入（在 `Assets/Examples/Mvvm/`，由 `InstancedValidationAll` 一并调用） |

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

- **一律走 `forceVertexPath`**（env 构造时自动开、Dispose 时还原）。本机编辑器上
  buffer 路径（顶点 SSBO）的 draw 静默不出像素，像素探针只有顶点流后端可信。
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
