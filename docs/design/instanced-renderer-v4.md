# FairyGUI Quad 实例流渲染器（v4）设计书

状态：设计定稿，未施工。依据：ProGPU 管线源码精读（`~/ECS/ProGPU`）、
本仓库 PoC 实测（`Assets/Examples/InstancedPoC`，分支 `poc/gpu-instanced-ui`）、
MergedBatch v2 的多代理评审结论（15 条 CONFIRMED，其中 7 条渲染层结构性缺陷）。

---

## 1. 背景与依据

### 1.1 为什么需要 v4

MergedBatch（合批 v2）用 CPU 把叶子网格烘焙进合并 Mesh。评审证实这个架构有一类
同源缺陷：**凡是"CPU 烘焙副本"需要感知的状态变化，都必须有一条失效通道，而通道
永远数不全**——已确认的七条（隐藏叶子仍显示、滤镜捕获丢内容、重父级永久隐形、
fairyBatching 关闭卡死、跨根双渲染、变不可合并不升级、子图集残影）全是失效通道
缺失的具体化。打补丁只能逐条修，换架构则整类消失。

### 1.2 PoC 实测数字（VirtualList，237 quads，10 段）

| 指标 | MergedBatch v2 | Quad 实例流 PoC |
|---|---|---|
| 滚动一帧渲染路径成本 | 97.8µs（全量重烘） | **42ns**（写一个 uniform） |
| 单文本变更 | 45.6µs（局部重烘+3 mesh 上传） | **11.1µs**（局部实例上传） |
| 常驻提交 | 4.5µs 脏检查 | 13.9µs（10 段 × ~1.4µs，聚段后 ~3-6µs） |
| 列表内容 draw 数 | 3 | 10（聚段后 2-4） |

### 1.3 ProGPU 管线精读结论（三问）

**Q1 场景图怎么编译成 GPU 命令**：Visual 树每节点持保留 `RenderCommand` 流
（Push/Pop 状态命令内联在流中）；每帧遍历展开到共享顶点/索引列表；
`CommitPendingDrawCalls` **游标切段**——纹理/clip/blend/mask 任一状态变化即提交
`[pendingStart, now)` 为一个 `CompositorDrawCall`（索引区间 + 完整状态快照）。

**Q2 增量怎么做**：ProGPU 对控件 UI 是**每帧全量重编译**，增量性全在缓存层
（命令级 GeometryCache、字形/路径 atlas 带 Generation 协议、图表 SeriesCacheKey）。
但对海量内容它另有一条路：`RetainedGlyphInstance`（Transform+Color+Bounds+Metadata
的定长实例**常驻 GPU**，`Draw(6, N)` 一次画完，视口变化只改相机 uniform）。

**Q3 裁剪/层怎么编码**：编译期 CPU 栈；矩形 clip = per-draw-call `ClipRect`
（scissor 语义）；透明度烘进顶点色；几何遮罩 = 离屏 mask 纹理 per-draw 绑定；
混合模式 = per-draw 管线变体。**不是 per-instance**——桌面窗口裁剪区少，够用。

**对 FairyGUI 的映射判断**：FairyGUI 本来就是保留网格模型 + 手游低端机预算，
不适合"每帧全量重编译"；正确的对位是 ProGPU 的 **retained instance 模式**——
把整个 UI 当它的"海量保留内容"处理。三处我们要比 ProGPU 更进一步：
裁剪升级为 per-instance 索引（手游列表多裁剪区并存、要跨裁剪合批）；
变换升级为 transform buffer 分级（不止相机 uniform）；
更新协议升级为推送式（评审教训：轮询数不全）。

---

## 2. 目标与非目标

**目标**
- G1 滚动/位移/透明度变化的渲染路径成本 ≤ 1µs/帧（PoC 已证 42ns 量级）。
- G2 消灭"CPU 烘焙副本失效管理"缺陷类（评审 M2/M8/M9/M11/M12/M13 及 W2 无对应物）。
- G3 列表/面板类 UI 的 draw 数 = 排序后材质段数（目标 2-6）。
- G4 叶子数据源管线零改动（网格生成/材质选择/命中测试/事件照旧）。
- G5 移动端可用（GLES3.0+/Metal/Vulkan；Built-in RP 先行）。

**非目标**
- 不做矢量路径 GPU 光栅化（ProGPU 的 PathAtlas 一套；FairyGUI 无此需求）。
- 不替换滤镜/遮罩渲染（走 fallback 通道 + 层协议）。
- 不动 UIPackage/GObject/事件/MVVM 层。
- 不追求首版 SRP；预留适配点。

---

## 3. 总体架构

```
DisplayObject 树（不动）
      │ 推送式脏通知（§6：content/transform/visible/structure 四通道）
      ▼
┌─ 编译层 InstancedCompiler ────────────────────────────┐
│ FairyBatching 相邻性排序 → 状态键切段（纹理×blend×层）  │
│ 叶网格 → quad 重组（三角形对法，PoC 验证）              │
│ 不可实例内容 → fallback 通道登记（§9）                  │
└──────┬────────────────────────────────────────────────┘
       ▼
┌─ 资源层 ──────────────────────────────────────────────┐
│ QuadInstanceBuffer（分段区间分配，局部 SetData）        │
│ TransformBuffer（容器级矩阵，索引引用）                 │
│ ClipBuffer（裁剪矩形数组，索引引用）                    │
│ 段表 SegmentTable（区间+材质+z+layer）                  │
└──────┬────────────────────────────────────────────────┘
       ▼
┌─ 提交层 ──────────────────────────────────────────────┐
│ 每段一次 RenderMeshPrimitives（共享单位 quad）          │
│ fallback renderer 按段 z 协议穿插                       │
└────────────────────────────────────────────────────────┘
```

挂接点与 MergedBatch 相同：`Container.SetRenderingOrderAll` 之后（复用
`_batchElements` 排序结果），开关为 `Container.instancedRendering`（与
`mergedBatching` 互斥，后者进入废弃期）。

---

## 4. 数据模型

### 4.1 QuadInstance（80 B，16 字节对齐）

```
float4 rect;        // xy = 容器局部 min 角, zw = size（PoC 同款）
float4 uvA;         // corner(0,0) 与 corner(1,0) 的 UV —— 按角归位，旋转图集安全
float4 uvB;         // corner(0,1) 与 corner(1,1) 的 UV
float4 color;       // 顶点色×透明度（编译期烘入 opacity，同 ProGPU）
uint   transformIndex;  // → TransformBuffer（§4.2）
uint   clipIndex;       // → ClipBuffer（§4.3）
uint   flags;           // bit0 字体alpha采样 bit1 灰度 bit2.. 保留
uint   _pad;
```

依据：PoC 的 64B 版本视觉验证通过（含九宫格共享网格、旋转斜章、字体 alpha）；
新增 transform/clip 索引是 v4 的两项升级。四角 UV 方案保留（三角形对重组 +
按角归位，是 PoC 用两轮视觉失败换来的结论，不要退回"每 4 顶点+UV min/max"）。

### 4.2 TransformBuffer 与三级更新

实例不存完整矩阵（80B 已够大），存 `transformIndex` 指向容器级矩阵表：

- **0 级（uniform 级）**：整棵实例流的根容器矩阵，`_ContainerL2W` uniform。
  根容器整体移动零成本（PoC 已验证）。
- **1 级（transform 槽）**：滚动容器、常动的中间容器各占一个槽。
  ScrollPane 滚动 = 写一个 float4（槽内 offset），即 PoC 的 42ns 路径推广。
  槽分配策略：编译期把"拥有 ScrollPane 的容器 + 带 gearXY/Tween 标记的容器"
  提升为槽；其余容器的变换烘进 rect。
- **2 级（实例重写）**：叶子自身变换/内容变化 → 重算该叶的 quads → 局部
  `SetData`（PoC 的 11.1µs 路径）。

取舍说明：不做全层级 GPU 矩阵链（每实例一条祖先链在 shader 里乘不划算，
ProGPU 也没做）；两级足以覆盖"滚动"与"个体动画"两大高频场景，其余走 2 级重写。

### 4.3 ClipBuffer（超越 ProGPU 的 per-draw）

裁剪矩形数组（容器局部空间，随所属 transform 槽联动），实例带 `clipIndex`。
fragment 阶段按索引取矩形判弃（PoC 的单矩形版已验证 shader 侧裁剪可行）。

- 收益:同图集内容跨多个裁剪区**合并为一段**（多面板+列表场景 draw 数不随
  裁剪区数增长)——这是相对 MergedBatch（clip 不同 → 材质不同 → 断段）和
  ProGPU（per-draw scissor）的双重升级。
- 软裁剪（clipSoftness）：clip 矩形扩展为 `rect + softness`，fragment 里
  smoothstep 衰减 alpha——顺带把 FairyGUI 的 SOFT_CLIPPED 关键字变体消掉。
- 嵌套矩形裁剪：编译期把裁剪栈**交集折叠**为单矩形（FairyGUI 现行
  EnterClipping 已是交集语义，照搬）。
- stencil 遮罩：不进实例流，整个遮罩作用域 fallback（§9）。

### 4.4 段表

```
Segment { int instanceStart, instanceCount; Material material;
          float z; int ownerLayerProbe; }
```
段 = 排序后同（纹理×blend×字体模式）连续区间。段 z 步进保透明序（PoC 方案）。
`ownerLayerProbe`：每帧从 owner 容器读 layer——并且**段必须注册进
SetChildrenLayer 的翻层名单**（评审 M12 的教训：CaptureCamera 翻层只走显示
子节点；v4 的段对象要么挂成显示子节点参与翻层，要么在 CaptureCamera.Capture
处加一个"外部渲染者"回调协议。取后者：`Stage.onLayerFlip` 事件，段提交层订阅）。

---

## 5. 编译：场景图 → 实例流

1. 输入沿用 `_batchElements`（FairyBatching 排序已解决"同材质相邻且不破坏
   遮挡序"——这正是 ProGPU CommitPendingDrawCalls 想要的相邻性，我们有现成的）。
2. 遍历排序结果，状态键 =（纹理, blend, 字体模式）；键变化 → 切段
   （ProGPU 游标法）。目标段数 2-6（vs PoC 树序分段的 10）。
3. 每元素：三角形对重组 quad（16 顶点共享网格/独立 quad 网格通吃），
   UV 按角归位，颜色×opacity 烘入，写 transformIndex/clipIndex。
4. 不可实例元素（§9 清单）→ 关闭其实例化、保留自身 renderer、
   在段表插 fallback 标记（占一个 z 槽保序）。
5. 编译产物写入 QuadInstanceBuffer；段区间连续，为 2 级局部更新预留
   每叶 `LeafRange{segment, start, count}`（PoC 已有同构记录）。

结构变化（增删子、可见性、材质变化）→ 重编译。重编译成本 = PoC Extract
（237 quads 不足 1ms 的量级，仅结构变化帧发生，可接受；后续可做段级局部重编译）。

---

## 6. 脏更新协议（推送式——评审教训的根治）

MergedBatch 靠轮询（hasChanged）+ 自身生命周期恢复状态，评审证明这条路失效
通道数不全。v4 全部改**推送**，在 DisplayObject 层加三个通知（对称于本分支
已加的 `NGraphics._contentVersion`）：

| 通道 | 触发点（施工位置） | 消费动作 |
|---|---|---|
| content | `NGraphics._contentVersion`（已存在） | 2 级：重算该叶 quads，局部 SetData |
| transform | `DisplayObject.SetPosition/SetScale/rotation/skew`（8 个 setter，与 OutlineChanged 同点）`_transformVersion++` + 上抛 | 槽容器：写 transform 槽；普通叶：2 级重写 |
| visible | `visible` setter **双向**（评审 M8：现行只在显示分支失效） | 结构重编译（或 flags 位隐藏——首版走重编译） |
| structure | `InvalidateBatchingState`（已存在） | 重排序+重切段+重编译 |

关键差异：**没有"恢复 forceRenderingOff"这类跨生命周期状态**——叶子 renderer
的关闭改为"实例化选中即关、编译期全量重算名单"，且关闭动作在叶子自己的
`NGraphics` 上打标（`_instancedBy` 弱引用），任何一方 Dispose/重父级时由
**叶子侧**主动清标恢复（评审 M9/M13 的根治：状态跟叶子走，不跟批走）。

字体图集重建：沿用 `BaseFont.textRebuildFlag` 语义，对齐 ProGPU 的 Generation
协议——图集 Generation 变化 → 所有字体段 2 级重写（文本 mesh 已由第二遍
InternalUpdate 重建，直接重提取）。注意 Stats 双遍重置问题（评审 U1）此处一并修。

---

## 7. 层与滤镜交互

- 段提交带 `layer`（每帧从 owner 读，PoC 已做）；
- **CaptureCamera 翻层协议**（M12 根治）：`SetChildrenLayer` 增加对外部渲染者
  的通知（或段注册为 capture 名单成员），保证滤镜/cacheAsBitmap 捕获包含实例
  内容且主相机不重复画；
- 滤镜作用域内的内容**整体 fallback**（首版）：painting 容器已是 BatchingRoot
  天然栅栏，行为与 MergedBatch 相同但通过层协议保证捕获正确。

---

## 8. 文本

quad 流直入（PoC 已验证位图与动态字体）；`flags.bit0` 字体 alpha 采样替代
FairyGUI-Font 材质；描边/阴影 = 多 quad（本来就是）。对照 ProGPU：它的
RetainedGlyphInstance 是矢量轮廓实例（CAD 场景），我们不需要——图集采样足够。

## 9. 非实例内容 fallback 清单（保留自身 renderer，按段 z 穿插）

vertexMatrix（3D 透视）、MaterialPropertyBlock、stencil 遮罩者与其作用域、
painting/滤镜作用域、GoWrapper（breakBatch）、非 quad 拓扑网格（三角形对重组
失败的命令，如任意多边形 Shape——PoC 中自动跳过）、`graphics.enabled` 参与
mergeability（评审 M10/M14 教训：enabled 也是实例化准入条件且变化要推送）。

## 10. 命中测试与事件

零改动。命中测试从来不走 renderer（DisplayObject 树 + contentRect/hitArea），
实例化不影响；事件系统本分支已 int-ID 化。

## 11. 平台矩阵

| 平台 | 实例数据通道 | 备注 |
|---|---|---|
| macOS/Win/iOS(Metal)/Vulkan | StructuredBuffer（PoC 路径） | 首选 |
| GLES 3.0/3.1 低端 | per-instance vertex attributes（两个 stream：静态 corner + 实例流） | ProGPU 浏览器路径同款取舍 |
| Built-in RP | RenderMeshPrimitives（PoC 已验证） | 首版 |
| URP | 同 API 可用；材质换 URP unlit 模板 | 适配点已知 |

注意（PoC 实测坑）：编辑器后台 GameView 不重绘时，RenderMeshPrimitives 需与
手动 `cam.Render()` 同帧才被消费——验证脚本已固化此手法。

## 12. 与现状的关系

- MergedBatch：进入废弃期。评审 15 条中 M1/M2/M8/M9/M10/M11/M12(部分)/M13/W2
  由 v4 架构消解；**V1/V6（Binder）、E1（事件注册表）、S2/S5（生成器）、V5
  （IntStringTable）与渲染无关，照修**；U1/U2 独立小修。
- 若近期需要 MergedBatch 顶用：只修 M8（visible 双向失效）+ M1（IndexFormat
  一行）+ W2（fairyBatching 互锁），其余等 v4。

## 13. 里程碑与验证（沿用现有基准/模拟校验设施）

1. **M1 核心流**：编译层+资源层+提交层，纹理切段（不排序），透明度/裁剪单矩形。
   验证：PoC 同款视觉并排对比 + 三相基准 ≥ PoC 数字；模拟数据校验 quad 重组器
   （构造 16 顶点共享网格/旋转 UV/退化三角的合成 mesh 断言输出）。
2. **M2 排序聚段**：接入 FairyBatching 排序，段数 10→2-4；draw 与提交成本回归。
3. **M3 裁剪索引化**：ClipBuffer + 软裁剪；验证多列表同屏 draw 数不随裁剪区增长。
4. **M4 推送脏协议**：DisplayObject 三通道 + 叶侧状态自恢复；用评审的 7 个
   失败场景做回归清单（隐藏/重父级/滤镜/关开关/跨根/变不可合并/子图集移动）。
5. **M5 fallback+层协议**：滤镜捕获含实例内容的截图对比。
6. **M6 移动端 attribute 路径**：GLES 目标真机或模拟器帧捕获。

候选（未排期，依据 GPUI 研究，见 §15）：

7. **M7 SDF primitive 化（借鉴 GPUI）**：QuadInstance 扩展圆角半径/描边宽度
   （`padding` 4B + flags 高位是现成扩展点；若需独立描边色再扩到 96B），
   fragment 用有向距离场判圆角矩形（对称性折到单象限）、描边（|sdf| 带宽）、
   阴影（Evan Wallace 解析高斯 erf，零纹理）。收益：圆角/描边 Shape 从
   fallback 名单移除（现在是多边形三角化 → 非 quad → 原生渲染），阴影不再
   需要九宫格贴图。验收：圆角/描边/阴影三明治场景 0.000% 像素对比 +
   lastSkippedPairs 计数下降。单 shader 静态分支，不做变体生成（变体断批，
   与 v4 目标相反）。
8. **M8 SG 静态烘焙（编译期 quad 发射器）**：见 §15。

## 15. 编译期生成的边界（Source Generator 能与不能）

已有设施：MVVM 管线的 FuiViewGenerator 通过 csc.rsp additionalfile 读 .fui，
FuiReader 解析组件树与图集 sprite 矩形——**布局结构与图集 UV 在编译期已知**。

### 能：per-组件 quad 发射器（M8 候选）

对静态内容（Image/Shape/九宫格/装饰帧），.fui 完全决定 quad 流：位置尺寸是
(width, height) 的仿射函数（relations/九宫格展开都是），图集 UV 是常量。SG 可
生成直线代码 `EmitQuads(ref QuadWriter w, float width, float height, int page)`
直接写 QuadInstance——跳过整条运行时链：NGraphics 网格构建（GameObject/Mesh
分配）、mesh 读回（GetVertices 等）、三角形对重组、邻接排序（生成器用同一
合法性规则在编译期排好）、切段（按纹理预分段）。

- **打击点是构建期而非稳态**：早期测量的结论是"解析 151ns/节点可忽略、
  构建才是大头"——静态镶边的窗口可以近零网格工作打开（一次数组写 + SetData）。
  稳态收益小（M4 推送协议已经把稳态压到位）。
- **混合粒度**：发射器只接管生成器能证明语义的叶子子集；文本（字体图集
  运行时才定）、装载器、动态列表内容仍走 M1-M5 运行时提取——同一条实例流，
  按叶粒度混合，fallback 语义与 M5 屏障一致。
- **风险要直说**：这是在生成器里复刻 FairyGUI 布局语义（relations/pivot/
  旋转/group/gear）——和评审揭示的"CPU 副本失效"同类的双实现漂移风险，
  只是漂移发生在**编译期**，可被既有 0.000% 像素对比设施在 CI 里逐组件
  抓住（生成器同时嵌入 .fui 内容哈希做过期检测）。命中测试不受影响
  （从不走 renderer，§10）。

### 不能/不必：生成 shader

- Roslyn SG 只能产 C#；Unity shader 走 ShaderLab/SRP 导入管线，代码生成
  shader 得做编辑器资产管线，是另一件工具（且没有需求支撑）。
- 单 shader + flags 静态分支已覆盖 primitive 集（M7 的 SDF 也是加字段不加
  变体）；平台差异走 multi_compile（M6）。per-组件生成 shader 变体 = 变体
  切换断批，恰是 v4 要消灭的东西。

## 14. 风险

- 段 z 步进与既有内容 z 交互（fallback renderer 穿插精度）——M1 即验证；
- transform 槽提升策略的启发式失误（该提升没提升 → 退化为 2 级重写，正确但慢；
  监控 Stats 计数）；
- 每叶 LeafRange 记录在大 UI 的内存（80B/quad + 记录，1 万 quad ≈ 1MB，可接受）；
- URP 下 RenderMeshPrimitives 的相机注入细节（首版不做，适配点已标）。
