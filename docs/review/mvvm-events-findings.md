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
| U1 | BaseFont.textRebuildFlag 触发 Stage 整树双遍 Update，字体图集重建帧全树成本 ×2（上游原有行为，被认领叶放大浪费） | 未修——列入性能 backlog（原生残余热路径） |
| E1 | 事件层（EventTypeRegistry/int-ID 化 相关）——**具体定义在评审会话中未落盘，已丢失** | 需重审：对 Assets/Scripts/Event/ 重跑一次针对性评审再定条款 |
| S2 / S5 | Source Generator（tools/FairyGUI.Mvvm.Generator）——**具体定义未落盘，已丢失** | 需重审：对生成器（Observable/Bind/FuiView 三个 generator）重跑针对性评审 |

## 教训

评审产出必须当天落盘到仓库（本文件即补救）。E1/S2/S5 因只存在于会话记忆
而无法验收，重审成本高于当初落盘成本一个数量级。

## 验收方式

V1/V6 的行为化验证脚本见提交说明（play mode eval：级联写入存活、Flush 中
Unbind 不越界且余组照常 apply）。后续任何评审修复都应附带同等验证。
