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

## 套件清单

| 套件 | 项数 | 覆盖 |
|---|---|---|
| `M4ScenarioSuite` | 19 | 推送脏协议：隐藏/显示、滤镜开关、`graphics.enabled`、重父级即时自恢复、跨根移动单一 owner、多边形回退与再入、文本增长、dispose 恢复 |
| `InstancedBatch1Suite` | 14 | blend 回退与 run 屏障、grayed 子树继承与精确 luma、MergedBatch/重复流互斥 |
| `InstancedBatch2Suite` | 8 | 文本 slack 生命周期（缩/涨/越界/等长 churn、幽灵尾）、认领叶 sortingOrder 省写与释放回同步 |
| `InstancedBatch3Suite` | 19 | 颜色 tier c1-c6、transform 槽 t1-t10（含槽内 clip 跟随、溢出探针、嵌套槽）、Extract 增量化 e1-e3 |
| `InstancedBatch3dSuite` | 10 | 跨图集段键：合段、6 纹理 4+2 裂段、逐槽像素精确、churn 保槽位、grayed 共存 |
| `InstancedBatch4Suite` | 12 | 产品化：`instancedRendering` 开关、Stage 自动驱动与原生像素等价、内层移动/滚动走槽、开关往返、dispose 拆除 |
| `InstancedBatch5Suite` | 10 | 曲线文本：CurveBaseFont 注册与 shader 接线、side table、UBB 分段色、下划线实心 quad、字形 slack、顶点路径认领与实例字形像素 |
| `BinderReentrancyCheck` | 11 | MVVM Binder 重入（在 `Assets/Examples/Mvvm/`，由 `InstancedValidationAll` 一并调用） |

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
