using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using FairyGUI;

namespace FairyGUIEditor
{
    /// <summary>
    /// Mesh 合并控制面板
    /// 用于运行时动态开关 Mesh 合并功能，方便对比优化效果
    /// </summary>
    public class MeshBatchingWindow : EditorWindow
    {
        // 批处理模式
        private enum BatchingMode { Off, MeshBatching, GenerationalBatching }
        private BatchingMode _currentMode = BatchingMode.Off;

        // UI 元素引用
        private Label _statusLabel;
        private Label _statsLabel;
        private Button _offBtn;
        private Button _meshBtn;
        private Button _genBtn;
        private Toggle _fairyBatchingToggle;

        [MenuItem("Tools/FairyGUI/Mesh Batching Control Panel")]
        public static void ShowWindow()
        {
            var window = GetWindow<MeshBatchingWindow>();
            window.titleContent = new GUIContent("Mesh Batching");
            window.minSize = new Vector2(350, 400);
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingTop = 10;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingBottom = 10;

            // 标题
            var title = new Label("FairyGUI Mesh Batching Control");
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 15;
            root.Add(title);

            // 说明文字
            var description = new Label("运行时动态切换 Mesh 合并模式，配合 Profiler 观察 DrawCall 变化");
            description.style.marginBottom = 15;
            description.style.whiteSpace = WhiteSpace.Normal;
            root.Add(description);

            // FairyBatching 开关
            var fairyBatchingBox = new Box();
            fairyBatchingBox.style.marginBottom = 15;
            fairyBatchingBox.style.paddingTop = 10;
            fairyBatchingBox.style.paddingBottom = 10;
            fairyBatchingBox.style.paddingLeft = 10;
            fairyBatchingBox.style.paddingRight = 10;

            _fairyBatchingToggle = new Toggle("FairyBatching (基础批处理)");
            _fairyBatchingToggle.value = true;
            _fairyBatchingToggle.RegisterValueChangedCallback(OnFairyBatchingToggleChanged);
            _fairyBatchingToggle.tooltip = "关闭后可对比完全无批处理的 DrawCall 数量";
            fairyBatchingBox.Add(_fairyBatchingToggle);

            var fairyBatchingHint = new Label("关闭此选项可完全禁用 FairyGUI 批处理，用于对比基准 DrawCall");
            fairyBatchingHint.style.fontSize = 11;
            fairyBatchingHint.style.color = new Color(0.6f, 0.6f, 0.6f);
            fairyBatchingHint.style.marginTop = 5;
            fairyBatchingHint.style.whiteSpace = WhiteSpace.Normal;
            fairyBatchingBox.Add(fairyBatchingHint);

            root.Add(fairyBatchingBox);

            // 模式选择区域
            var modeBox = new Box();
            modeBox.style.marginBottom = 15;
            modeBox.style.paddingTop = 10;
            modeBox.style.paddingBottom = 10;
            modeBox.style.paddingLeft = 10;
            modeBox.style.paddingRight = 10;

            var modeLabel = new Label("Batching Mode:");
            modeLabel.style.marginBottom = 8;
            modeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            modeBox.Add(modeLabel);

            // 模式按钮组
            var buttonGroup = new VisualElement();
            buttonGroup.style.flexDirection = FlexDirection.Row;

            _offBtn = new Button(() => SetMode(BatchingMode.Off)) { text = "Off" };
            _meshBtn = new Button(() => SetMode(BatchingMode.MeshBatching)) { text = "Mesh Batching" };
            _genBtn = new Button(() => SetMode(BatchingMode.GenerationalBatching)) { text = "Generational" };

            _offBtn.style.flexGrow = 1;
            _meshBtn.style.flexGrow = 1;
            _genBtn.style.flexGrow = 1;

            buttonGroup.Add(_offBtn);
            buttonGroup.Add(_meshBtn);
            buttonGroup.Add(_genBtn);
            modeBox.Add(buttonGroup);

            root.Add(modeBox);

            // 状态显示
            _statusLabel = new Label("Current Mode: Off");
            _statusLabel.style.marginBottom = 10;
            _statusLabel.style.fontSize = 14;
            root.Add(_statusLabel);

            // 统计信息区域
            var statsContainer = new Box();
            statsContainer.style.paddingTop = 10;
            statsContainer.style.paddingBottom = 10;
            statsContainer.style.paddingLeft = 10;
            statsContainer.style.paddingRight = 10;
            statsContainer.style.flexGrow = 1;

            var statsTitle = new Label("Statistics:");
            statsTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            statsTitle.style.marginBottom = 8;
            statsContainer.Add(statsTitle);

            _statsLabel = new Label("Press Play to see stats...");
            _statsLabel.style.whiteSpace = WhiteSpace.Normal;
            statsContainer.Add(_statsLabel);

            root.Add(statsContainer);

            // 按钮区域
            var buttonContainer = new VisualElement();
            buttonContainer.style.flexDirection = FlexDirection.Row;
            buttonContainer.style.marginTop = 15;

            var refreshBtn = new Button(UpdateStats) { text = "Refresh Stats" };
            refreshBtn.style.flexGrow = 1;
            buttonContainer.Add(refreshBtn);

            var resetBtn = new Button(ResetBatching) { text = "Reset" };
            resetBtn.style.flexGrow = 1;
            resetBtn.style.marginLeft = 5;
            buttonContainer.Add(resetBtn);

            root.Add(buttonContainer);

            // 定时更新
            EditorApplication.update += OnEditorUpdate;

            // 初始化按钮状态
            UpdateButtonStyles();
        }

        private void OnDestroy()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private float _lastUpdateTime;
        private void OnEditorUpdate()
        {
            if (!Application.isPlaying) return;
            if (Time.realtimeSinceStartup - _lastUpdateTime > 0.5f)
            {
                UpdateStats();
                _lastUpdateTime = Time.realtimeSinceStartup;
            }
        }

        private void SetMode(BatchingMode mode)
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("提示", "请先运行游戏再切换模式", "确定");
                return;
            }

            var container = GRoot.inst?.container;
            if (container == null)
            {
                EditorUtility.DisplayDialog("错误", "未找到 GRoot.inst.container", "确定");
                return;
            }

            // 关闭 Mesh 合并模式
            container.meshBatching = false;
            container.generationalBatching = false;

            // 启用选择的模式
            _currentMode = mode;
            switch (mode)
            {
                case BatchingMode.Off:
                    // 保持 fairyBatching 原有设置
                    break;

                case BatchingMode.MeshBatching:
                    // meshBatching 是 fairyBatching 的增强，需要先开启 fairyBatching
                    container.fairyBatching = true;
                    container.meshBatching = true;
                    break;

                case BatchingMode.GenerationalBatching:
                    // generationalBatching 也需要 fairyBatching 作为基础
                    container.fairyBatching = true;
                    container.generationalBatching = true;
                    break;
            }

            _statusLabel.text = $"Current Mode: {GetModeDisplayName(mode)}";
            UpdateButtonStyles();
            UpdateStats();
        }

        private string GetModeDisplayName(BatchingMode mode)
        {
            switch (mode)
            {
                case BatchingMode.Off: return "Off (默认)";
                case BatchingMode.MeshBatching: return "Mesh Batching (Mesh 合并)";
                case BatchingMode.GenerationalBatching: return "Generational (分代批处理)";
                default: return mode.ToString();
            }
        }

        private void UpdateButtonStyles()
        {
            if (_offBtn == null) return;

            // 重置所有按钮样式
            _offBtn.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
            _meshBtn.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
            _genBtn.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);

            // 高亮当前选中的按钮
            var highlightColor = new Color(0.2f, 0.5f, 0.8f);
            switch (_currentMode)
            {
                case BatchingMode.Off:
                    _offBtn.style.backgroundColor = highlightColor;
                    break;
                case BatchingMode.MeshBatching:
                    _meshBtn.style.backgroundColor = highlightColor;
                    break;
                case BatchingMode.GenerationalBatching:
                    _genBtn.style.backgroundColor = highlightColor;
                    break;
            }
        }

        private void OnFairyBatchingToggleChanged(ChangeEvent<bool> evt)
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("提示", "请先运行游戏再切换", "确定");
                // 恢复原值
                _fairyBatchingToggle.SetValueWithoutNotify(!evt.newValue);
                return;
            }

            var container = GRoot.inst?.container;
            if (container == null)
            {
                EditorUtility.DisplayDialog("错误", "未找到 GRoot.inst.container", "确定");
                _fairyBatchingToggle.SetValueWithoutNotify(!evt.newValue);
                return;
            }

            container.fairyBatching = evt.newValue;

            // 如果关闭 fairyBatching，也关闭 meshBatching 和 generationalBatching
            if (!evt.newValue)
            {
                container.meshBatching = false;
                container.generationalBatching = false;
                _currentMode = BatchingMode.Off;
                UpdateButtonStyles();
            }

            UpdateStats();
        }

        private void UpdateStats()
        {
            if (_statsLabel == null) return;

            if (!Application.isPlaying)
            {
                _statsLabel.text = "Press Play to see stats...\n\n" +
                    "提示：运行游戏后可以切换不同的批处理模式，\n" +
                    "配合 Window > Analysis > Frame Debugger\n" +
                    "观察 DrawCall 的变化。";
                _statusLabel.text = "Current Mode: Not Playing";
                _currentMode = BatchingMode.Off;
                UpdateButtonStyles();
                if (_fairyBatchingToggle != null)
                    _fairyBatchingToggle.SetValueWithoutNotify(true);
                return;
            }

            var container = GRoot.inst?.container;
            var sb = new System.Text.StringBuilder();

            sb.AppendLine($"Graphics Count: {Stats.GraphicsCount}");
            sb.AppendLine($"Object Count: {Stats.ObjectCount}");

            if (container != null)
            {
                // 同步 toggle 状态
                if (_fairyBatchingToggle != null)
                    _fairyBatchingToggle.SetValueWithoutNotify(container.fairyBatching);

                sb.AppendLine($"\nFairyBatching: {(container.fairyBatching ? "ON" : "OFF")}");
                sb.AppendLine($"MeshBatching: {(container.meshBatching ? "ON" : "OFF")}");
                sb.AppendLine($"GenerationalBatching: {(container.generationalBatching ? "ON" : "OFF")}");

                if (_currentMode == BatchingMode.GenerationalBatching &&
                    container.generationalBatcher != null)
                {
                    var b = container.generationalBatcher;
                    sb.AppendLine($"\n--- Generational Batching Stats ---");
                    sb.AppendLine($"Gen0 (Young): {b.Gen0Count}");
                    sb.AppendLine($"Gen1 (Middle): {b.Gen1Count}");
                    sb.AppendLine($"Gen2 (Old/Batched): {b.Gen2Count}");
                    sb.AppendLine($"Total Elements: {b.TotalCount}");
                    sb.AppendLine($"Batch Groups: {b.BatchCount}");
                }
                else if (_currentMode == BatchingMode.MeshBatching &&
                         container.meshBatcher != null)
                {
                    var b = container.meshBatcher;
                    sb.AppendLine($"\n--- Mesh Batching Stats ---");
                    sb.AppendLine($"Batch Groups: {b.BatchCount}");
                    sb.AppendLine($"Total Vertices: {b.TotalVertexCount}");
                }
            }
            else
            {
                sb.AppendLine("\nGRoot.inst.container is null");
            }

            _statsLabel.text = sb.ToString();
        }

        private void ResetBatching()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("提示", "请先运行游戏", "确定");
                return;
            }

            var container = GRoot.inst?.container;
            if (container == null) return;

            // 如果使用分代批处理，重置分代状态
            if (container.generationalBatcher != null)
            {
                container.generationalBatcher.Reset();
            }

            // 重新请求批处理
            container.InvalidateBatchingState(true);

            UpdateStats();
        }
    }
}
