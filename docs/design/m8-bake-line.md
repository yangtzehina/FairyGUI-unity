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
| 2026-07-31 | M8-6 | ✅ | **烘焙线收官**。FqsParityRunner 常设门禁（菜单 Tools/FairyGUI/Run FQS Parity + `Run()` CI 入口 + Temp/FqsParityResults.txt + 机读判定行）：catalog = 加载包全部导出组件**枚举不抽样**（不可烘按精确理由 SKIP，非失败）+ 3 个程序化场景（clip/SDF/嵌套 clip）；每案两遍全新实例——运行时走树 vs deferred+挂载——双层断言：像素区域 diff=0 + 几何快照（quad rect/uv/color 容差 1e-4、语义 flags 掩码比对——texIndex/段布局两次编译可合法不同）+ 叶数相等。篡改阶梯：坏 blob（含敌意计数）拒绝、错源哈希拒绝、正确哈希挂载、全程像素完好。首跑判定 **PASS（9 过 0 败 24 跳）**。回归 15+19+16+14+11 全绿（m8_5 的 r7 性能门在长会话内一次误报 18%、新鲜 Play 会话复测 45%——**性能门必须在新鲜会话跑**，长会话的 GC/驱动债务会扭曲微基准，已列为运行纪律）。FQS 菜单共两个入口（Bake / Parity），守住三菜单纪律。 |
