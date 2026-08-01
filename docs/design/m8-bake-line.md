# M8 烘焙线施工设计：静态子树预编译 quad 流 + 生成外观类

状态：**全线完工**（2026-07-31，M8-1..M8-6 全绿，状态表见文末）。立项 2026-07-30。承接 docs/design/instanced-renderer-v4.md §15
（M8a/M8b 候选与 CreateObject 成本测量）与 docs/research/openfairy-analysis.md（烘焙优先
路线实证、可抄清单、脆性目录）。

---

## 1. 定位与裁决（不再重议）

- **形态 = 混合，不是替换**。OpenFairy 证明了纯烘焙后端的对价（无 CreateObject、无运行时
  包加载、虚拟化推迟）。M8 只烘**编译期可证明语义静态的子树**为预编译 quad 流 + 生成
  强类型外观类；动态内容（文本、装载器、虚拟列表、运行时装配）留在 v4 运行时流——
  **同一条流、同一套段/槽/clip 协议，按叶粒度混合**（§15 混合粒度原则）。
- **输入 = 二进制 .fui 包字节**，不是编辑器工程 XML。烘焙器在编辑器里复用运行时
  UIPackage parser（百战、零反射），不要求用户交出编辑器工程。
- **运行时 controller 不动**。生成类是 enum **外观**（facade）：嵌套页 enum → 内部按
  index 驱动现有 Controller；`GetController("name")` 字符串 API 原样保留。
- **收益目标**（§15 测量基线）：窗口打开 ≈8ms，其中一半是 Unity 对象创建——M8a
  （跳过网格构建+Extract）+ M8b（无对象装饰叶）合计目标砍半。

## 2. 序列化分层（本次立项的决策，2026-07-30 定）

| 层 | 内容 | 方案 | 理由 |
|---|---|---|---|
| **热 blob** | quad 数组、段表、叶表、clip 表 | **手写 reader**：版本头 + 原始 span（`MemoryMarshal.Cast`），~50 行 | 与任何序列化库同速（都是 memcpy）；核心运行时**零依赖**不破例 |
| **结构化元数据** | gear 按页常量表、transition 项、多态 union | 先手写；出现 union/版本演进复杂度后切 **MemoryPack**（SG 零反射，`[MemoryPackUnion]` tag 稳定整数——不复刻 SerializeReference 的类型身份脆性） | 手写多态版本化格式易错；MemoryPack 最低 Unity 2022.3.12（本工程 62f3 ✓）、IL2CPP ✓ |
| **归属** | — | MemoryPack 只进 **烘焙线可选 asmdef**（M8 本来就 opt-in） | 依赖纪律：OpenFairy 三手装依赖是负面教训 |
| SerializerFoundation | — | **观望不用**：pre-1.0 底层基建（IRead/WriteBuffer），LangVersion 14，无文档 | 等它落进 MemoryPack v2 顺流受益 |

### FQS1 热 blob 格式（v1）

```
header:  magic "FQS1" | u32 formatVersion | u64 fuiContentHash | u32 flags
counts:  quadCount segCount leafCount clipCount texRefCount
段区（16 字节对齐的原始 span，MemoryMarshal.Cast 直读）:
  QuadInstance[quadCount]   ← 80B，与 shader 侧 ABI 同一布局（本来就冻结）
  SegRecord[segCount]       { start,count,runIndex, texRef0..3, z }
  LeafRecord[leafCount]     { elementId, start,count, flags, clipIndex, slotIndex, bakedAlpha }
  ClipRecord[clipCount]     { rect, soft, parentIndex, slotIndex, ownerElementId }
  TexRef[texRefCount]       { 包内 item id → 装载时解析为 NTexture }
```

约束（OpenFairy 脆性目录换来的纪律）：
- **布局即 ABI**：QuadInstance/各 Record 一经发行不可改字段；演进走 formatVersion 分支或新段区；
- **过期检测**：装载时校验 fuiContentHash 与当前加载包一致，不一致回退运行时 Extract 并告警
  （烘焙产物永远只是加速缓存，语义真源是 .fui——双实现漂移风险的兜底）；
- 纹理等 UnityEngine.Object 永不进 blob，走 TexRef 表装载期解析。

## 3. 生成外观类形态（OpenFairy 形态 + 我们的运行时约束）

```csharp
public partial class 确认按钮View   //per exported component, opt-in 输出目录
{
    public enum button { up, down }                    //页 = 成员，改名/删页编译期报错
    public button buttonPage
    {
        get => (button)_com.GetControllerAt(0).selectedIndex;
        set => _com.GetControllerAt(0).selectedIndex = (int)value;   //外观，不动运行时
    }
    //m_ 前缀类型化子引用（烘焙期按名绑定一次，运行时零查找）
    public GTextField m_title;
    ...
}
```

- int-switch 桥、`[SerializeField] internal` + InternalsVisibleTo 接线按 OpenFairy 原样采用；
- **codegen 死锁解法照抄**（~30 行）：内容比较写盘 → SessionState pending → `[DidReloadScripts]`
  恢复 → TypeCache 取新类型；恢复手册（sed + CleanBuildCache）随文档；
- **边界用例表进测试集**：C# 关键字/标点折叠碰撞、基类成员遮蔽（`new enum`）、已删页引用
  剪除、跨包 `global::` 防遮蔽、幂等重烘。

## 4. 里程碑与验收门

沿用 M1-M9 纪律：每站独立提交、桌面编译门 + Unity 套件 + 像素对照全绿才过。

| 站 | 内容 | 验收门 |
|---|---|---|
| **M8-1 烘焙器骨架 + 热 blob** | 编辑器菜单 `Tools/FairyGUI/Bake Package`（三菜单纪律）；复用运行时 parser 读 .fui；单个导出组件、纯静态叶（图/形状，文本除外）→ FQS1 .bytes | blob 重建 quads 与同组件 live Extract **逐位一致**（浮点钳 1e-5）；像素对照零差异 |
| **M8-2 装载融合** | blob 挂进 in-place 流作预编译段；动态叶同流运行时提取；推送通道对烘焙叶继续生效（elementId→NGraphics 绑定，颜色 tier/槽提升可用） | 混合组件（静态背景+动态文本）行为与纯运行时流一致（现有套件语义复用）；Extract 耗时对比数字入文档 |
| **M8-3 生成外观类** | enum facade + m_ 引用 + int-switch 桥；两阶段域重载烘焙 | 生成类零警告；改名/删页编译期报错演示；幂等重烘（二次 Bake 零写盘）；边界用例表全绿 |
| **M8-4 gear/transition 常量表** | 按页常量表烘入元数据层（先手写，union 出现即切 MemoryPack asmdef）；应用走颜色/transform tier | gear 切页零 Extract（extractCount 探针）；transition 播放与运行时解析路径像素一致 |
| **M8-5 无对象装饰叶（M8b）** | 可证明安全的静态叶跳过 GameObject/DisplayObject 创建；hit 面烘为显式数据入流 | 窗口打开成本实测对比 §15 基线（目标 ≥40% 降幅）；命中测试等价（含整块可点） |
| **M8-6 对照 CI 化** | parity catalog（烘焙 vs 运行时逐组件，OpenFairy 双层法：像素 + 几何快照）；fuiContentHash 过期检测测试 | catalog 驱动的逐组件对照全绿；篡改 .fui 后装载正确回退运行时路径 |

依赖关系：M8-1 → M8-2 → {M8-3, M8-4} → M8-5 → M8-6（M8-3 与 M8-4 可并行）。
预算校准：OpenFairy 全管线 ≈3k 行（含 uGUI 侧运行时）；我们复用 parser 与流协议，
烘焙器+装载器预计更小，M8-5 是最大单站。

## 4.5 使用指引（2026-07-31，真实包 dogfood 实测后定）

**接入方式**（全部真实验证过的流程）：

```csharp
// 编辑期：Tools/FairyGUI/Bake Packages (FQS) 一键出 blob + 视图
// 运行时：
((Container)win.displayObject).instancedRendering = true;   // 窗口级一次

NGraphics.deferRenderers = true;                            // 仅装饰重组件加这两行
var c = UIPackage.CreateObject("Basics", "MyPanel") as GComponent;
NGraphics.deferRenderers = false;
FqsMount.Mount(c, blobBytes, srcHash);                      // 源哈希门禁
```

**何时用什么**（Basics dogfood 实测校准）：

| 组件形态 | 建议 | 依据 |
|---|---|---|
| **≥10 叶装饰重**（背景框/边饰/成排静态图的面板、窗口） | mount + defer 全上 | 80 叶实测打开 **-50%**；收益与叶数成正比 |
| **<10 叶小件**（单图组件、滚动条、小控件） | 只 mount，**不要 defer** | PopupMenu（1 叶）0%、Component12（4 叶）**-11%**——挂载绑定开销吃掉了三件套节省 |
| 任意可烘组件 | mount 本身总是值得 | Extract 免走树（7×）+ 段合并 + 全套 tier（移动/翻页/隐显零重编译） |
| 带文本/动画组件 | 不烘，同流运行时路径 | 混合同流零成本共存（dogfood：5 挂载组件 + 整页文本组件 = 1 段 550 quads） |

**主战场判断**：烘焙线的收益画像 = 装饰重的面板/窗口——真实项目 UI 量最大的部分；
全是小交互件的包（如 Basics 演示包，24/29 因文本/动画被拒）不是它的用武之地，也无须强求。

## 4.6 自动挂载（2026-08-01；对抗审查后重做，见文末事故记）

`FqsAutoMount` 把 §4.5 的手工三件套折叠成一个工程级开关：

```csharp
FqsAutoMount.enabled = true;   // 启动时一行；此后 CreateObject 全自动
```

之后每个包创建的 GComponent 在构造收尾**装填**（arm）自己的 blob；blob 叶数 ≥
`deferLeafThreshold`（默认 10，即 §4.5 校准值）的组件整个构造期自动进入
`NGraphics.deferRenderers` 作用域。

### 装填而非挂载：这是整个安全性的支点

第一版在构造收尾直接 `FqsMount.Mount`。对抗审查判它 critical，理由成立且已逐行核实：

> 让 mount 失效的唯一机制是 `InstancedUIStream._NotifyStructure`，而它开头就是
> `if (liveInPlaceCount == 0) return;`。构造期组件尚未挂到显示树、通常也还没有任何
> in-place 流存在——**从构造完成到首次 extract 这段窗口里，子树的任何结构改动都无人
> 通知 mount**。等到流真的起来做 splice 时，冻结的 blob quad 会盖住已经变了的子树：
> 删掉的子节点仍被画出来（幽灵），内层子节点的 grayed 丢失。这不是"可弃加速器静默
> 回退"，是静默画错。

现在的形态：构造期只把字节**装填**在容器上（`Container._fqsPending`，纯数据），由外层
流在 extract 时调 `FqsAutoMount._Realize` 才真正绑定。绑定发生在**流正在走这棵树的那一刻**，
所以构造后的任何改动天然被看见——结构对不上就 bind 失败、回退运行时走树；对得上才 splice。
此后的改动由既有失效阶梯负责（那时 `liveInPlaceCount > 0`，通道是活的）。

### 其余审查修正

| 缺陷 | 修法 |
|---|---|
| blob 按 `packageItem` 取，组件却由 `packageItem.getBranch()` 构建 → 分支包（本地化）挂到错分支几何，源哈希分辨不了 | 查找与烘焙两侧统一按**分支解析后**的 item（`FqsAutoMount.ResolveItem`） |
| 默认 provider 按**名字**查，重名去重表又只统计导出项 → 非导出组件被喂到同名导出组件的 blob（拓扑相同就 bind 成功，静默错图） | 只对 `exported` 项查找；文件名 = `名字_id`，**id 定身份**。重名、非导出遮蔽、大小写不敏感文件系统冲突一次全消 |
| `GRoot.contentScaleLevel` 决定构造期选哪套图集项，却不在过期键里 → 高 DPI 设备静默渲染 x1 美术 | 烘焙把档位写进 `BlobFlags` 位 8-15，挂载时不符即拒 |
| 缓存按包 **id** 存活 → 卸载重载同名包后，用旧哈希校验旧 blob | 全部改挂在 **UIPackage 实例**上（`ConditionalWeakTable`），包一走缓存自然消亡 |
| 任何拒绝都清空缓存字节 → 一个实例的偶发失败，永久剥夺后续健康实例的 blob | 只有**字节确定性**失败（不是本版本 blob）才锁定；实例级拒绝只静默重复警告 |
| 非 Resources 包（bundle/Addressables）哈希为 0 → 过期门禁静默失效，而这恰是 blob 与包分开发布的部署形态 | 新增 `requireSourceHash`（**默认 true**）：算不出哈希就拒绝挂载，要接受风险须显式关掉 |
| `FqsBaker.Bake` 直接清空调用方活树上的 mount，且拒绝路径也不恢复 | 改为**摘下-恢复**（try/finally），烘焙纯净性不变而调用方的树完好 |
| 烘焙菜单 `suppressed` 硬置回 false、对拍门在 try 外设置 | 两处都改为保存-恢复，且置位挪进受保护区 |

### 机制要点

- **钩子**：`GComponent.ConstructFromResource` 首尾（try/finally 保证 defer 静态作用域
  异常安全）。同步/异步/嵌套子组件构造全覆盖；嵌套构造骑外层不重复开作用域。
- **blob 来源**：`blobProvider` 可插拔（bundle/Addressables 工程自接），默认
  `Resources.Load("Baked/{包名}/{组件名}_{id}")`，与烘焙菜单输出（`Assets/Resources/Baked/`，
  已 gitignore）同一套命名，唯一来源是 `FqsAutoMount.BlobFileName`。
- **失败即回退**：查不到 / 头无法解析 / 哈希不符 / 档位不符 / 绑定失败——全部静默走运行时
  树遍历，与手工 Mount 的可弃加速器语义一致。
- **已知取舍**：装填了 blob 的组件若最终**没有**进入任何 in-place 流，其 renderless 叶会
  在 M8-5 宽限期（2 帧）后才物化渲染器。烘过的组件本就是打算走流的，可接受；不想要就把
  `deferLeafThreshold` 设为 -1（只挂载不 defer）。

验收：`FqsAutoMountSuite` 20 项，其中 8 项是**以审查确认的缺陷为名**的回归项
（装填/兑现时序、删除内容不留幽灵、id 身份、非导出不查、分支键、档位门、无哈希拒绝、
烘焙恢复调用方 mount），并入全量门禁。

## 4.7 源哈希门覆盖全部加载路径（2026-08-01）

过期检测（§2「blob 永远是可丢弃加速缓存，语义真源是 .fui」的执行机构）原来由烘焙线
自己算：`Resources.Load<TextAsset>(pkg.assetPath + "_fui")`。这对**非 Resources 加载的包
一律得 0**——AssetBundle、Addressables、字节数组，恰恰是把 blob 与包分开发布、因而最
需要门禁的部署形态。自动挂载把 `requireSourceHash` 默认打开后，这些工程会直接一个 blob
都挂不上；关掉它则门禁静默失效。两条都不可接受。

**修法：哈希算在包自己身上。** `UIPackage.sourceHash` 在 `LoadPackage(ByteBuffer, string)`
开头算出——那是五个 `AddPackage` 重载（AssetBundle ×3、Resources/路径、自定义 loadFunc、
字节数组同步/异步）**唯一的汇合点**。哈希取 ByteBuffer 的 `[bufferOffset, +length)` 窗口
（`ByteBuffer` 为此新增 `bufferOffset` 访问器，因为底层数组可以更大且共享），
`FqsBlob.Hash` 相应增加带范围的重载。

要点：

- **哈希标识的是描述符内容，不是投递方式**——同一个发布出来的包，编辑器走 Resources 烘焙、
  运行时走 bundle 加载，两侧必须得到同一个值，门禁才不会误拒。
- **`UIPackage.sourceHash` 的数值与旧算法逐位一致**（c21 断言的就是这个）——单看这一半，
  既有 blob 不会失效。**但下一节的组合哈希改变了 blob 里存的值，所以本次改动整体是要重烘的**，
  见下方迁移说明。
- `FqsAutoMount.PackageSourceHash` 退化成 `pkg.sourceHash` 的转发，烘焙菜单同源；
  原来那份按包 id 记忆化的哈希缓存一并删除（缓存本身也是上一轮审查里跨包重载的缺陷源）。
- `requireSourceHash` 保留为**兜底**：哈希为 0 现在只意味着包没加载完，不再是常态。

### 门禁还要跨过依赖包（同批修，来自审查）

单有「包自己的描述符哈希」仍然不够：**blob 是整棵子树的扁平 quad 表**，里面可以含别的包
来的组件——`FqsBaker.ResolveTex` 扫描全部已加载包，发出的 texRef 就是 `别的包id/图集项id`。
于是：Main 引用 Common/Btn，烘 Main 得到 Btn 的 quad + Common 的图集 UV；美术只重发
Common（图集重排、Btn 的 rect 变了），Main 的描述符逐字节未变 → 门禁放行 → 冻结的 quad
仍按旧 rect 采样**新图集** → 那个按钮画出隔壁精灵的像素，无警告、结构也没变所以 pathHash
照样绑上。讽刺的是 Common 自己的 blob 会被正确拒绝，Main 的却不会。

修法：blob 里存的 `fuiHash` 改为**组合值**——owner 的 sourceHash 链上每个被引用包的
sourceHash（包 id 去重后按 ordinal 排序，两侧同一个 `FqsBlob.CombineWithReferences`）。
挂载方仍只传 owner 哈希，由 `FqsMount.Mount` 自己按 blob 的 texRef 表重算组合值再比对。
被引用包未加载则贡献 0，与烘焙时不符 → 拒绝（失败方向安全）；`ownerHash == 0`
（程序化烘焙、NoSourceHash）保持 0，语义不变。

**残留（明写，免得被当成已覆盖）**：只贡献**无纹理叶**（纯 Shape 组件）的依赖包不会留下
Package texRef，因而不在这条链里。有纹理的依赖——绝大多数——都覆盖到了，且由于链上取的是
依赖包的**描述符**哈希，依赖包里纯几何的改动（挪个 4px）同样会移动门禁值。

### 迁移：本次必须重烘（`FormatVersion` 1 → 2）

组合哈希改的是 `fuiHash` 这个字段的**含义**（原来＝owner 包哈希，现在＝owner 链上被引用包），
布局一字未动。但含义改变同样是格式演进——若不升版本，v1 老 blob 会以「**源哈希不符（陈旧
blob）**」被拒，把格式问题伪装成内容过期，团队会去追一次根本没发生过的重新导出。实测本仓库
自己的产物即如此：旧值 `668D00CB44B58CDD`，新值 `2AF8B0EAECBF80FD`，永远不可能相等。

因此按 §2「演进走 formatVersion」把 `FqsBlob.FormatVersion` 升到 **2**：老 blob 直接以
`FQS: format 1 != 2` 被明确拒绝，自说明、可排查。**升级运行时后必须重跑
`Tools/FairyGUI/Bake Packages (FQS)`**；blob 是 gitignore 的生成物，重烘无代价。

验收：`FqsAutoMountSuite` c21/c22/c23/c24 + c14——c21 钉住「包哈希 == 其描述符的 FNV」，
c22 真正走一遍字节数组加载（bundle 形态）并断言**与 Resources 加载同值**（这才是生产
环境成立的前提，非同义反复），c23 断言存储值确实是组合而非裸 owner 哈希，c24 断言被引用
包的哈希一变（重发或丢失）门禁值就变，c14 断言 mount 侧只拿 owner 哈希也能重算出同一组合值。

## 5. 风险与对策

- **双实现漂移**（§15 已直说）：烘焙器复刻 FairyGUI 布局语义（relations/pivot/旋转/group）——
  与"CPU 副本失效"同类风险，但漂移发生在编译期：M8-1 的逐位一致门 + M8-6 的逐组件
  parity CI + fuiContentHash 过期回退三层兜底。**烘焙产物永远可丢弃**（真源是 .fui）。
- **不可折叠边界**（OpenFairy 实证清单）：兄弟目标 relation、百分比 pivot 组合、autoSize
  文本→一律不烘，留运行时叶。宁可少烘，不可错烘。
- **域重载死锁**：解法已入 §3；恢复手册随文档。
- **命中测试**：无对象子树的 hit 面必须编成显式数据（OpenFairy 用"按钮点不动"事故换来的）。

## 6. 状态表

| 日期 | 站 | 状态 | 验证 |
|---|---|---|---|
| 2026-07-30 | 立项 | ✅ 本文档 | — |
| 2026-07-30 | M8-1 | ✅ | 专项 15/15：逐位 quad 一致、字节级确定性、篡改/敌意计数干净拒绝、拒绝规则全套（根级 mask/文本对象存在即拒/movieclip/外部纹理默认拒/blend 栅栏）；真实包冒烟 Basics 5/28 组件烘出（全 Package TexRef + 源哈希），其余按精确理由拒绝。像素门随装载移至 M8-2。三代理对抗核查 1 blocker + 7 must-fix 全修：根级 mask 漏拒、Read 无前置校验（敌意计数 OOM）、LeafRecord 隐式尾 padding 显式化、烘焙后端未钉死（顶点路径 clip 粗化会冻进 blob）、External TexRef 会话态身份、图集冷热致文本拒绝不确定（改存在即拒）、菜单重名覆盖（改 ID 创建）、GRoot 缩放漂入 quad 低位（改无缩放 Stage 挂载）。 |
| 2026-07-30 | M8-2 | ✅ | 专项 19/19：**挂载即转换槽**（blob quads 天然组件局部空间 → 挂载容器分配槽，移动/缩放 tier-1 零重编译）；M8-1 顺延像素门通过（挂载 vs 运行时全区域 diff=0）；混合行为（动态兄弟 churn tier-2、烘焙叶颜色 tier 按 bakedAlpha 精确重标定、tier-2 重写 + 重拼接同帧自愈）；失效协议（挂载内结构变化 → 失效 → 运行时回退照常渲染；哈希门拒绝陈旧 blob）。实测 200 叶子树 Extract：挂载 0.111ms vs 运行时 0.781ms = **7.03×**。施工修正：Flush 结构分支 Extract 后跌落队列处理段（拼接自愈同帧结算，不闪帧）。环境教训：多轮 Play 循环后编辑器可能落在空场景（相机无引导、全屏零渲染）——像素验证前须确认演示场景已打开。 |
| 2026-07-30 | M8-3 | ✅ | 生成器专项 12/12（phase A 7 + phase B 5）：29 个导出组件全量生成 enum facade（嵌套页 enum + 类型化 {ctrl}Page 属性 + m_ 子引用按索引构造期绑定 + int-switch 桥）；幂等重烘二次零写盘；边界用例（关键字 @escape、数字前缀、标点折叠 dedup）；域重载后 35 个生成类型可加载，Demo_ButtonView 实测 31 子引用全非空、enum 翻页直驱运行时控制器。编辑器与桌面双编译零警告；**编译期报错演示**：删页/改名探针 4 个 CS0117/CS1061。与 OpenFairy 形态的偏差已记：我们是纯类 facade 构造期绑定（无 prefab 序列化接线，[SerializeField] internal 不适用）；两阶段域重载为 SessionState pending + DidReloadScripts 结算日志（本站无烘焙期消费生成类型的环节，机制为后站备用）。 |
| 2026-07-31 | M8-4 | ✅ | 范围裁定后交付两条**挂载 tier**（gear 数值本经既有通道已是 tier-2，复刻按页常量表反而引入第二套 gear 实现的漂移风险——常量表顺延至真正需要它的 M8-5 无对象叶）：①可见性 tier——visible 切换在有效挂载内改走段区改写（隐=清零、显=补队列同帧重建），setter 重构为 tier 先行、接管则跳过 InvalidateBatchingState/结构通知；拼接期按活标志无状态重放；blob 缺席内容的显示→优雅失效回退运行时（守卫不依赖 mesh 构建态）。②挂载内容器变换 tier——M8-2 的失效降级为逐叶 tier-2 重写（槽相对矩阵天然精确），并置 _slotsDirty 刷新骑槽 clip 窗口。验收：g 系列 16/16（隐/显/容器隐/内部移动全零 Extract、mount 保持有效、重拼接后隐藏态无状态保持、缺席显示优雅失效）；t 系列 4/4——六状态脚本序列（移动/隐显/alpha/clip 移动）**逐状态像素零差**且挂载播放全程零 Extract（= transition 播放路径等价性门）。回归 15+19+10+11 全绿。 |
| 2026-07-31 | M8-5 | ✅ | 形态：**renderless NGraphics**（`NGraphics.deferRenderers` 作用域内创建的叶跳过 MeshFilter/MeshRenderer/Mesh 三件套与 mesh 构建；GameObject/transform 保留——tier-2 矩阵与命中测试的依据）。物化阶梯：流读取按需建 mesh（ExtractLeaf/UpdateLeaf）、释放/未认领宽限期后物化 renderer、认领期永不物化。奠基发现：**跨实例挂载开箱即用**（blob 烘自 A 挂到结构相同的新实例 B——生产 CreateObject 流）；成本分解 create 74%+firstUpdate 24%、M8-2 机制≈免费。验收 14/14：renderless 认领像素与运行时参照 diff=0、tier-2/颜色 tier 全程无 renderer、命中测试无 renderer 可点（整块可点门）、释放物化后原生渲染、无挂载时运行时走树认领回退。**成本门：80 叶打开 1.85→0.93ms = 50% 降幅**（门槛 ≥40%）。备注：显式 hit 面数据留给未来"全无对象"形态（本形态保留 GameObject，FairyGUI 命中天然工作）；常量表（承 M8-4 顺延）在本形态同样未被需要——活对象 gear 机制照常驱动 renderless 叶。 |
| 2026-08-01 | 自动挂载 | ✅ | **一次事故记，值得留档**：首版（构造期直接 Mount）三道门全绿——桌面/Unity 编译、专项 12/12、全量 239/239 双后端 478/478、对拍 PASS、真实包 dogfood 零拒绝——然后被 28 代理对抗审查判出 **18 条确认缺陷（4 条 critical）**。教训不是"门不够"，是**门只验了我想到的场景**：全部 12 项断言都建立在"构造完立刻挂载"这个前提上，而缺陷恰恰在这个前提本身。critical 的共性是**跨越"流尚未存在"窗口的绑定**（`_NotifyStructure` 在 `liveInPlaceCount == 0` 时是死通道）与**按名字而非 id 认身份**（非导出组件/分支/大小写全是同一个洞）。重做形态见 §4.6：装填-兑现分离 + id 定身份 + 档位门 + 实例级缓存 + 默认拒绝无哈希 blob。验收 `FqsAutoMountSuite` 20 项（8 项以缺陷命名的回归项）+ 全量门禁 + 双后端。 |
| 2026-07-31 | M8-6 | ✅ | **烘焙线收官**。FqsParityRunner 常设门禁（菜单 Tools/FairyGUI/Run FQS Parity + `Run()` CI 入口 + Temp/FqsParityResults.txt + 机读判定行）：catalog = 加载包全部导出组件**枚举不抽样**（不可烘按精确理由 SKIP，非失败）+ 3 个程序化场景（clip/SDF/嵌套 clip）；每案两遍全新实例——运行时走树 vs deferred+挂载——双层断言：像素区域 diff=0 + 几何快照（quad rect/uv/color 容差 1e-4、语义 flags 掩码比对——texIndex/段布局两次编译可合法不同）+ 叶数相等。篡改阶梯：坏 blob（含敌意计数）拒绝、错源哈希拒绝、正确哈希挂载、全程像素完好。首跑判定 **PASS（9 过 0 败 24 跳）**。回归 15+19+16+14+11 全绿（m8_5 的 r7 性能门在长会话内一次误报 18%、新鲜 Play 会话复测 45%——**性能门必须在新鲜会话跑**，长会话的 GC/驱动债务会扭曲微基准，已列为运行纪律）。FQS 菜单共两个入口（Bake / Parity），守住三菜单纪律。 |
