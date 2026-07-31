# 批 3 施工设计：增量化主线（颜色 tier / Extract 增量化 / transform 槽 tier-1）

状态：**已落地**（2026-07-25，全套验证绿）。执行结果见文末。承接 docs/design/instanced-renderer-v4.md §4.2/§6 与优化审计
（第 1/2 批已落地：MVVM 重入、认领短路、字典查找、合并上传、按需推送、文本 slack）。

对象文件：`Assets/Scripts/Core/Instanced/InstancedUIStream.cs`、
`Assets/Scripts/Core/NGraphics.cs`、`Assets/Resources/Shaders/FairyGUI-InstancedUI.shader`、
`FairyGUI-InstancedUIAttribs.shader`、`Assets/Scripts/Core/Instanced/QuadVertex.cs`（只读，misc.x 已带 transformIndex）。

---

## 3a 颜色 tier

现状：认领叶的 alpha/tint 变化走 NGraphics.Tint/ChangeAlpha —— 全顶点 mesh 颜色回写
（renderer 是 forceRenderingOff，白做）→ _QueueLeafUpdate → UpdateLeaf 全量
GetVertices/GetUVs/GetColors/GetTriangles + QuadReassembler 重组。为一个乘进 color 的标量做两遍全量。

改法：
1. `NGraphics.Tint()`（_meshDirty 检查之后）与 `ChangeAlpha()` 开头加认领分支：
   `_colorStale = true`（Tint 另加 `_tintStale = true`）、`_contentVersion++`、
   `_instancedBy._QueueLeafColor(this)`、return——不碰 mesh。
2. `LeafRange` 加 `float bakedAlpha`（该叶 quads 当前烘入的 context alpha）。
   捕获点：ExtractLeaf 时存入 PendingLeaf（= graphics._currentAlpha），BuildSegments 转存；
   UpdateLeaf 重组后更新。
3. 流侧 `_QueueLeafColor` 队列（独立于 _dirtyLeaves，去重 HashSet；结构分支和 Extract 时一并清空；
   处理时 `g._instancedBy != this` 跳过；若同帧已在 _dirtyLeafSet 里则跳过——全量重组已含颜色）。
   Flush 处理：`UpdateLeafColor(g)`：
   - `bakedAlpha <= 0` 或（`g._tintStale` 且 range.sdf||range.curve）→ 退全量 `UpdateLeaf(g, true)`。
   - 否则对 `[start, start+liveCount)`：`c = _quads[i].color`；tintStale 时 `c.rgb = _color.rgb`；
     `c.a = c.a / bakedAlpha * newAlpha`；写回 _quads/_uploadArray/顶点镜像；段脏区间标记
     （复用批 2 UploadAllDirtyRanges）。收尾 `bakedAlpha = newAlpha`（newAlpha==0 时置 0，
     下次变化走全量恢复）。
   数学依据：QuadReassembler 每 quad 只取首顶点颜色；native 语义 col.a = _alpha × _alphaBackup[i]，
   除旧乘新等价于换 _alpha、保 backup。长 fade 的乘除漂移 ~1e-5/600帧，8bit 输出不可见。
4. `NGraphics._RestoreNativeColors()`：`_colorStale` 时按 native 公式回写 mesh 颜色
   （tintStale → rgb=_color，否则保留原 rgb；a = _alpha × backup），清标记。
   调用点：`_ClearInstancedOwner`（释放交还 native 前）、UpdateLeaf mesh 路径读 GetColors 前、
   ExtractLeaf 读 mesh 前（防脏数据进流）。_meshDirty 或 mesh 空时只清标记
   （UpdateMeshNow 全新烘焙，顺带清 _colorStale/_tintStale）。

## 3b Extract 增量化

1. **buffer/数组容量复用**：`_buffer/_clipBuffer` 加容量字段，pow2 增长、不缩即复用
   （SetData 局部写，shader 只读 _InstanceStart/Count 内）；`_uploadArray/_vertexUpload/_clipUploadArray`
   同策略 + `List.CopyTo`，消灭每次 Extract 的 ToArray/new。
2. **段保留（索引+纹理匹配）**：Extract 开头不再全量 Release；旧段搬去 _prevSegments，
   BuildSegments 后按同下标同纹理转移 go/filter/renderer/mesh/props/lastSortingOrder/lastLayer
   （z=-0.5×下标，同下标即同 z，transform 不动、免 SetParent/SetActive 往返）；
   未匹配旧段 Release 回池、新段照旧 Claim。材质同纹理即同对象，sharedMaterial 不变则跳过赋值。
3. **顶点路径 mesh 参数跳过**：Segment 加 `meshQuadCap`；转移且 count 不变时只
   SetVertexBufferData，跳过 SetVertexBufferParams/index/submesh 重传；新领的 mesh 走全量。
4. **MPB/Segment 池化**：props 随段转移复用；新段从 MPB 池取（Clear 后用）；Segment 对象池化。
5. 概率探针：`public int extractCount`（验证与基准都要用）。

## 3c transform 槽 tier-1

现状：非流根 Container 的任何变换 → _structureDirty → 全量 Extract。容器 tween/内层 ScrollPane
滚动 = 每帧全树重编译（审计悬崖项）。

方案（自适应热提升，无需 API）：
1. **数据**：`_TransformSlots[16]` float4x4 数组（0 号恒等）双 shader uniform；
   buffer 路径 QuadInstance.transformIndex 已在 80B 内，顶点路径 QuadVertex.misc.x 已透传。
   顶点变换：`raw = mul(_TransformSlots[idx], float4(raw,0,1)).xy`，先槽后 _ScrollOffset；
   sdfPos/coverage 在 quad 局部空间计算，槽旋转/等比缩放下 SDF 视觉正确（等比放大）。
2. **热提升**：`_NotifyTransform` 容器分支：命中槽（`_slotIndices.ContainsKey(c)`）→
   `_slotsDirty = true` 返回（不重编译）；未命中 → `_hotContainers[c] = frameCount` + 结构脏。
   首动一次重编译并入槽，后续动 = 写矩阵。Extract 时热表按最近活跃取前 15 个分槽，
   过期项（如 >3000 帧未动）剔除。仅 _inPlace 流启用（副本流 drawOffset 语义不变）。
3. **烘焙空间**：走树携带 slotIndex + 当前烘焙用 worldToLocal。进入槽容器：
   slotIndex = 分配值、worldToLocal = 槽容器自己的 worldToLocalMatrix（嵌套槽同理，内层槽矩阵
   经 Unity 矩阵直取全链，天然复合）。叶 AABB（邻接排序/clip 夹紧用）仍用根空间矩阵
   （`_rootWorldToLocal × leaf.l2w`）。staging 后 Stamp 时连 clipIndex 一起盖 transformIndex；
   LeafRange 记 slotIndex，UpdateLeaf 重组矩阵改用 `_slotOwners[slot].worldToLocalMatrix × leaf.l2w`，
   写回时补盖 transformIndex。
4. **槽矩阵**：`M_slot = _container.w2l × slotOwner.l2w`（Unity 矩阵，取当前值）。
   Extract 末尾全算一遍；_slotsDirty 时（Render 内、Flush 后）全量重算 16 个并入 pushProps
   （`SetMatrixArray("_TransformSlots", ...)` 常驻 pushProps 块，数组长度恒 16）。
5. **槽内 clip**：PushClip 保持产出根空间折叠矩形，另存 `_clipMeta[i] = {owner Container,
   原始 Rect, soft, parentIndex, slotIndex}`；dedup 键加 slotIndex（不同槽不合并）。
   槽矩阵变化且存在 slotIndex≠0 的条目时，按序（parent 先于 child 入表）重算：
   `rect_i = fold(TransformClipRect(meta.rect, _container.w2l × owner.l2w), rect_parent)`
   ——owner 矩阵直取现值，旋转自动退化为 AABB（与 Extract 同法，native 同等语义）。
   重算后 buffer 路径 _clipBuffer.SetData、顶点路径并入 pushProps 数组重填。
   非槽 clip owner 自己动 → 本来就走结构脏全量重编译，元数据不会过期。
6. **不变式**：fallback 叶靠原生层级跟随槽容器（DisplayObject setter 照写 Unity transform）；
   run/sortingOrder 协议与变换无关；外窗口每帧重算不受影响。

## 交互与风险清单（供核查）

- R1 颜色 tier × 文本 slack：色写只覆盖 liveCount，slack 尾部退化 quad 保持零 → 无残影。
- R2 颜色 tier × M4 内容通道：UpdateMeshNow 清 stale（全新烘焙已含 _alpha/_color）→ 恢复不重复。
- R3 释放路径：_ClearInstancedOwner 现有 sortingOrder 回同步之上加颜色恢复，顺序无依赖。
- R4 槽 × 文本 churn：UpdateLeaf 用槽空间矩阵，quad 落槽空间，shader 槽矩阵送回根空间 → 位置正确。
- R5 槽 × 邻接排序：AABB 用提取时刻根空间值；槽后续移动不重排——与 native FairyBatching
  「移动不触发 InvalidateBatchingState」同语义，接受。
- R6 段保留 × 认领 diff：段转移不影响 _claimed 集合逻辑（叶级，独立）。
- R7 槽表满（>15 热容器）：溢出容器维持现状（每动重编译），加 slotOverflow 计数探针。
- R8 副本流（非 in-place）永不入槽/热表；_QueueLeafColor 仅 in-place 会发生（认领才改道）。
- R9 pushProps 集合扩容：slots 矩阵数组 + 槽重算 clip 数组都并入现有 elision 门，静止帧仍零推送。
- R10 嵌套槽：内层槽矩阵为全链矩阵，外层动 → _slotsDirty 全量重算 16 槽 → 内层自动跟随。

---

## 执行结果（2026-07-25）

> 本节所述的逐项验证已固化为仓库内脚本：
> `Assets/Examples/InstancedPoC/Validation/InstancedBatch3Suite.cs`
> （c1-c6 / t1-t10 / e1-e3，跑法见同目录 `README.md`）。


全部三步落地，批 3 专项 19/19：颜色 tier c1-c6（fade/tint 零重编译、量化 quad 颜色、释放后
native 像素还原）、transform 槽 t1-t11（首动一次重编译入槽、后续移动/缩放零重编译、槽内 clip
窗口跟随、槽上文本 churn tier-2、像素跟随验证）、Extract 增量化 e1-e3（跨重编译段 GO 同一、
像素完好）。回归：M4 19/19、批2 8/8、批1 14/14、MVVM 11/11。

量化（顶点路径）：
- 容器 tween 120 帧（41 叶 56 quads）：槽路径 **0.007ms/帧、0 次重编译** vs
  每帧重编译 0.127ms/帧 —— 18×，且槽路径成本与内容规模无关。
- 三相基准与批 2 持平：idle Render 2.9µs、scroll 83ns、churn LeafUpdate 9.8µs —— 新机制零回归。

施工中修正两件事：
- Dispose 的段 GO 先 SetActive(false) 再 Destroy（Destroy 帧末生效，否则已销毁流当帧仍渲染）。
- 环境注意：UniCli `exec Compile` 不保证重导入 .shader 资产——shader 改动后需
  `AssetDatabase.ImportAsset(..., ForceUpdate)`，否则新 uniform 读到零值（本次 _TransformSlots
  全零导致槽 quad 退化不可见，排查半小时）。

## 3d 跨图集段键（尾项，同日落地）

段键从单纹理升级为 ≤4 纹理组合：Segment 带 4 槽纹理集（_MainTex+_Tex1..3），
BuildSegments 集合未满即并段、quad 拷入时盖 flags bits16-17 texIndex，材质按纹理组合键缓存，
段转移改集合匹配；双 shader sdfMW 扩为 float3 携带槽号，fragment 分支链选采样器
（texIndex 逐 quad 常量 → 分支在图元内 uniform，导数合法；GLES3 无动态采样索引问题）。
tier-2 重写经 LeafRange.texIndexBits 补盖，颜色 tier 天然保位。

验证 10/10：shape+动态字体文本合 1 段、6 纹理正确裂 2 段（4+2）、各槽像素精确、
等长 churn 保持 tier-2 且槽位不丢、grayed 位共存（0.59 绿亮度）。回归五套件
19+8+14+19+11 全绿。基准：VirtualList **3 段 → 1 段**，draws 12→10，
scroll Render 7.2→5.25µs、idle 2.9→2.5µs（段少推送少）。
