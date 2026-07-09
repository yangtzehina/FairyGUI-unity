#if UNITY_2020_1_OR_NEWER
using System;
using System.Text;
using UnityEngine;
using FairyGUI;
using Unity.Profiling;

/// <summary>
/// Runtime A/B benchmark for Container.mergedBatching.
///
/// Runs four phases (merged off/on x idle/scroll), N frames each. Sampling happens in
/// LateUpdate ordered after StageEngine (DefaultExecutionOrder), so FairyGUI.Stats values
/// are the current frame's; the stage camera is rendered to an offscreen RenderTexture
/// each sampled frame so draw counters reflect pure UI cost and do not depend on the game
/// view repainting. Engine counters come from ProfilerRecorder — enable the profiler
/// (or UnityEditor.Profiling.ProfilerDriver.enabled = true) for them to tick.
/// Writes per-phase medians as JSON.
///
/// Usage: MergedBatchBenchmark.Run(container, "/path/out.json", 60, optionalScrollPane);
/// </summary>
[DefaultExecutionOrder(1000)]
public class MergedBatchBenchmark : MonoBehaviour
{
    public static void Run(Container target, string outputPath, int samplesPerPhase = 60, ScrollPane scrollPane = null)
    {
        GameObject go = new GameObject("MergedBatchBenchmark");
        MergedBatchBenchmark b = go.AddComponent<MergedBatchBenchmark>();
        b._target = target;
        b._path = outputPath;
        b._samples = samplesPerPhase;
        b._scrollPane = scrollPane;
    }

    Container _target;
    string _path;
    int _samples;
    ScrollPane _scrollPane;

    static readonly string[] CounterNames =
    {
        "Draw Calls Count", "Batches Count", "SetPass Calls Count", "Vertices Count",
        "GC Allocated In Frame", "Main Thread", "MergedBatch.Sync", "MergedBatch.Build",
        "MergedBatch.Rebake"
    };

    ProfilerRecorder[] _recorders;
    long[][] _values;

    int _phase = -1;
    int _frame;
    bool _originalMerged;
    float _originalPosY;
    RenderTexture _rt;
    Camera _cam;
    StringBuilder _json;
    long _statRuns, _statElements, _statRebuilds, _statRebakes;
    TextField _churnText;
    string _churnOriginal;

    const int PhaseCount = 5;
    const int Warmup = 5;

    void Start()
    {
        _cam = StageCamera.main;
        _rt = new RenderTexture(1136, 640, 24);
        _originalMerged = _target.mergedBatching;
        _originalPosY = _scrollPane != null ? _scrollPane.posY : 0;
        _churnText = FindTextField(_target);
        _churnOriginal = _churnText != null ? _churnText.text : null;
        _json = new StringBuilder();
        _json.Append("{\n");
        NextPhase();
    }

    static TextField FindTextField(Container root)
    {
        int cnt = root.numChildren;
        for (int i = 0; i < cnt; i++)
        {
            DisplayObject child = root.GetChildAt(i);
            if (child is TextField tf)
                return tf;
            if (child is Container c)
            {
                TextField found = FindTextField(c);
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    void NextPhase()
    {
        _phase++;
        while (_phase < PhaseCount
               && (((_phase == 1 || _phase == 3) && _scrollPane == null)
                   || (_phase == 4 && _churnText == null)))
            _phase++;

        if (_phase >= PhaseCount)
        {
            Finish();
            return;
        }

        _target.mergedBatching = _phase >= 2;
        if (_scrollPane != null)
            _scrollPane.SetPosY(_originalPosY, false);
        _frame = -Warmup;
        _statRuns = _statElements = _statRebuilds = _statRebakes = 0;
        StartRecorders();
    }

    void LateUpdate()
    {
        if (_phase >= PhaseCount)
            return;

        bool scroll = _phase == 1 || _phase == 3;
        if (scroll && _frame >= -1)
        {
            //large enough step to survive snapToItem lists
            float step = 130;
            _scrollPane.SetPosY(_scrollPane.posY >= _scrollPane.contentHeight * 0.5f ? 0 : _scrollPane.posY + step, false);
        }
        if (_phase == 4 && _frame >= -1)
            _churnText.text = (_frame & 1) == 0 ? "888" : "999";

        RenderOnce();

        if (_frame >= 0)
        {
            for (int r = 0; r < _recorders.Length; r++)
                _values[r][_frame] = _recorders[r].Valid ? _recorders[r].LastValue : -1;
            _statRuns += Stats.MergedRuns;
            _statElements += Stats.MergedElements;
            _statRebuilds += Stats.MergedRebuilds;
            _statRebakes += Stats.MergedRebakes;
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
        string name;
        switch (_phase)
        {
            case 0: name = "baseline_idle"; break;
            case 1: name = "baseline_scroll"; break;
            case 2: name = "merged_idle"; break;
            case 3: name = "merged_scroll"; break;
            default: name = "merged_textchurn"; break;
        }
        _json.Append("  \"").Append(name).Append("\": {\n");
        for (int r = 0; r < CounterNames.Length; r++)
            _json.Append("    \"").Append(CounterNames[r]).Append("\": ").Append(Median(_values[r])).Append(",\n");
        _json.Append("    \"Stats.MergedRuns(avg)\": ").Append(_statRuns / _samples).Append(",\n");
        _json.Append("    \"Stats.MergedElements(avg)\": ").Append(_statElements / _samples).Append(",\n");
        _json.Append("    \"Stats.MergedRebuilds(total)\": ").Append(_statRebuilds).Append(",\n");
        _json.Append("    \"Stats.MergedRebakes(total)\": ").Append(_statRebakes).Append("\n");
        _json.Append("  },\n");
    }

    void Finish()
    {
        _json.Append("  \"samplesPerPhase\": ").Append(_samples).Append("\n}\n");

        _target.mergedBatching = _originalMerged;
        if (_scrollPane != null)
            _scrollPane.SetPosY(_originalPosY, false);

        UnityEngine.Object.Destroy(_rt);
        System.IO.File.WriteAllText(_path, _json.ToString());
        Debug.Log("MergedBatchBenchmark done: " + _path);
        Destroy(gameObject);
    }

    void RenderOnce()
    {
        RenderTexture old = _cam.targetTexture;
        _cam.targetTexture = _rt;
        _cam.Render();
        _cam.targetTexture = old;
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
        _recorders[6] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MergedBatch.Sync");
        _recorders[7] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MergedBatch.Build");
        _recorders[8] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MergedBatch.Rebake");

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
