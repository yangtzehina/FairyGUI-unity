#if UNITY_2020_1_OR_NEWER
using System;
using System.Text;
using FairyGUI;
using UnityEngine;
using Unity.Profiling;

/// <summary>
/// Measures the GPU-instanced PoC against the same phases as MergedBatchBenchmark:
/// idle (no updates), scroll (one uniform write per frame), and textchurn (partial
/// instance-buffer upload per frame). Marker medians land in JSON alongside the
/// engine counters so the numbers line up with the earlier MergedBatch runs.
/// Drive the FairyGUI TextField externally? No — this benchmark mutates the same
/// TextField the merged benchmark used, then mirrors it into the instance buffer.
/// </summary>
[DefaultExecutionOrder(1000)]
public class InstancedPoCBenchmark : MonoBehaviour
{
    public static void Run(Container container, TextField churnText, string outputPath, int samplesPerPhase = 60)
    {
        GameObject go = new GameObject("InstancedPoCBenchmark");
        var b = go.AddComponent<InstancedPoCBenchmark>();
        b._container = container;
        b._churnText = churnText;
        b._path = outputPath;
        b._samples = samplesPerPhase;
    }

    Container _container;
    TextField _churnText;
    string _path;
    int _samples;

    InstancedUIRenderer _renderer;
    int _phase = -1;
    int _frame;
    float _scroll;
    StringBuilder _json;

    static readonly string[] CounterNames =
    {
        "Draw Calls Count", "Batches Count", "SetPass Calls Count", "Vertices Count",
        "GC Allocated In Frame", "Main Thread",
        "InstancedUI.Scroll", "InstancedUI.LeafUpdate", "InstancedUI.Render", "InstancedUI.Extract"
    };

    ProfilerRecorder[] _recorders;
    long[][] _values;

    const int PhaseCount = 3;
    const int Warmup = 5;

    void Start()
    {
        _renderer = new InstancedUIRenderer(_container, new Vector2(600, 0));
        _renderer.Extract();
        Debug.Log($"InstancedPoC: groups={_renderer.groupCount} quads={_renderer.quadCount}");
        _json = new StringBuilder();
        _json.Append("{\n");
        _json.Append("  \"groups\": ").Append(_renderer.groupCount).Append(",\n");
        _json.Append("  \"quads\": ").Append(_renderer.quadCount).Append(",\n");
        NextPhase();
    }

    void NextPhase()
    {
        _phase++;
        while (_phase == 2 && _churnText == null)
            _phase++;
        if (_phase >= PhaseCount)
        {
            Finish();
            return;
        }
        _frame = -Warmup;
        _scroll = 0;
        _renderer.SetScrollOffset(Vector2.zero);
        StartRecorders();
    }

    void LateUpdate()
    {
        if (_phase >= PhaseCount || _renderer == null)
            return;

        if (_phase == 1 && _frame >= -1)
        {
            _scroll += 130;
            if (_scroll > 5000) _scroll = 0;
            //y-down container: scrolling down moves content up (+y in local coords)
            _renderer.SetScrollOffset(new Vector2(0, _scroll));
        }

        if (_phase == 2 && _frame >= -1)
        {
            _churnText.text = (_frame & 1) == 0 ? "888" : "999";
            //the TextField's mesh rebuilt during this frame's stage update; mirror it
            if (!_renderer.UpdateLeaf(_churnText.graphics))
                _renderer.Extract();
        }

        _renderer.Render();

        if (_frame >= 0)
        {
            for (int r = 0; r < _recorders.Length; r++)
                _values[r][_frame] = _recorders[r].Valid ? _recorders[r].LastValue : -1;
        }

        _frame++;
        if (_frame >= _samples)
        {
            EmitPhase();
            StopRecorders();
            NextPhase();
        }
    }

    void EmitPhase()
    {
        string name = _phase == 0 ? "instanced_idle" : _phase == 1 ? "instanced_scroll" : "instanced_textchurn";
        _json.Append("  \"").Append(name).Append("\": {\n");
        for (int r = 0; r < CounterNames.Length; r++)
            _json.Append("    \"").Append(CounterNames[r]).Append("\": ").Append(Median(_values[r]))
                 .Append(r == CounterNames.Length - 1 ? "\n" : ",\n");
        _json.Append("  },\n");
    }

    void Finish()
    {
        _json.Append("  \"samplesPerPhase\": ").Append(_samples).Append("\n}\n");
        System.IO.File.WriteAllText(_path, _json.ToString());
        Debug.Log("InstancedPoCBenchmark done: " + _path);
        _renderer.Dispose();
        _renderer = null;
        Destroy(gameObject);
    }

    void OnDestroy()
    {
        if (_renderer != null)
        {
            _renderer.Dispose();
            _renderer = null;
        }
    }

    void StartRecorders()
    {
        _recorders = new ProfilerRecorder[CounterNames.Length];
        _recorders[0] = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _recorders[1] = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        _recorders[2] = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        _recorders[3] = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
        _recorders[4] = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        _recorders[5] = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
        _recorders[6] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "InstancedUI.Scroll");
        _recorders[7] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "InstancedUI.LeafUpdate");
        _recorders[8] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "InstancedUI.Render");
        _recorders[9] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "InstancedUI.Extract");

        _values = new long[CounterNames.Length][];
        for (int i = 0; i < _values.Length; i++)
            _values[i] = new long[_samples];
    }

    void StopRecorders()
    {
        for (int i = 0; i < _recorders.Length; i++)
            _recorders[i].Dispose();
    }

    static long Median(long[] values)
    {
        long[] copy = (long[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }
}
#endif
