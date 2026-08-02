#if UNITY_2020_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FairyGUI;
using UnityEngine;

/// <summary>
/// Batch 5 leftover, half 8a: measures the real GPU fragment cost of analytic
/// curve-text coverage on Metal. Apple Silicon is a TBDR GPU of the same
/// family as every iPhone, so these numbers answer the "is the per-pixel
/// curve loop affordable on a mobile-class tiler" question for the Apple half;
/// the Android/Vulkan half (Mali/Adreno driver behaviour) stays open as 8b.
///
/// Method, per the repository's perf discipline:
///  - GPU time from FrameTimingManager (needs enableFrameTimingStats, set by
///    the build entry), medians over sampled frames, vsync off;
///  - RATIOS only, ABAB-interleaved: text wall vs an equal-footprint wall of
///    plain quads (fragments are what cost — coverage runs over every pixel
///    of each glyph's PADDED em box, so the baseline mirrors the same boxes);
///  - phases: text wall (CJK body ~11 curves/band), 龘 wall (58 curves in a
///    single band — the known worst case), outline wall (band±1 distance scan,
///    the batch-5b 3x loop) — each interleaved with its comparator.
///
/// The verdict gates MEASUREMENT VALIDITY (nonzero GPU timings, all phases
/// sampled), not budgets: the numbers themselves are the deliverable, written
/// as "CURVEGPU VERDICT/DATA" lines to the console and -curvegpuOut <path>.
///
/// Boots itself in a player launched with -curvegpu; in the editor call
/// StartInEditor() and poll <see cref="report"/>. A frame-driven state
/// machine end to end — the editor harness's Step() cannot be used from
/// inside the loop (AGENTS.md pitfall 20).
/// </summary>
public class CurveGpuCostBench : MonoBehaviour
{
    const string kFontName = "CurveGpuBenchFont";
    static readonly string[] kFontCandidates =
    {
        "/Library/Fonts/Arial Unicode.ttf",
        "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
    };

    /// <summary>Set when the run finishes (editor polling).</summary>
    public static string report;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "-curvegpu") < 0)
            return;
        var go = new GameObject("CurveGpuCostBench");
        DontDestroyOnLoad(go);
        go.AddComponent<CurveGpuCostBench>();
    }

    public static void StartInEditor()
    {
        report = null;
        new GameObject("CurveGpuCostBench").AddComponent<CurveGpuCostBench>();
    }

    //phase plan: warmup, then comparator-interleaved pairs, two rounds each
    //(ABAB: session drift lands on both sides of every ratio)
    enum Kind { Warmup, Quads, Text, Long, Outline }
    struct Phase { public Kind kind; public string label; }
    static readonly Phase[] kPlan =
    {
        new Phase { kind = Kind.Warmup, label = "warmup" },
        new Phase { kind = Kind.Text, label = "text.1" },
        new Phase { kind = Kind.Quads, label = "quads.1" },
        new Phase { kind = Kind.Text, label = "text.2" },
        new Phase { kind = Kind.Quads, label = "quads.2" },
        new Phase { kind = Kind.Long, label = "long.1" },
        new Phase { kind = Kind.Quads, label = "quads.3" },
        new Phase { kind = Kind.Long, label = "long.2" },
        new Phase { kind = Kind.Quads, label = "quads.4" },
        new Phase { kind = Kind.Outline, label = "outline.1" },
        new Phase { kind = Kind.Text, label = "text.3" },
        new Phase { kind = Kind.Outline, label = "outline.2" },
        new Phase { kind = Kind.Text, label = "text.4" },
    };
    const int kSettleFrames = 12;
    const int kSampleFrames = 60;

    int _phase = -1;
    int _frame;
    GComponent _content;
    readonly List<double> _gpu = new List<double>(kSampleFrames);
    readonly List<double> _cpu = new List<double>(kSampleFrames);
    readonly Dictionary<string, (double gpu, double cpu)> _results
        = new Dictionary<string, (double, double)>();
    readonly FrameTiming[] _timing = new FrameTiming[1];
    string _ttf;

    void Start()
    {
        QualitySettings.vSyncCount = 0;
        //60fps CAP, not uncapped: the first uncapped run cooked the (fanless)
        //M4 — the heavy 龘/outline phases heated the GPU into downclocking and
        //identical text phases drifted 2.5 -> 10.8ms in execution order while
        //the trivial quads baseline sat at 0.068ms throughout (too brief to
        //hold clocks down). A 60fps duty cycle keeps thermals flat; residual
        //throttle is handled by taking the MIN across rounds in the summary.
        Application.targetFrameRate = 60;

        foreach (var c in kFontCandidates)
            if (File.Exists(c)) { _ttf = c; break; }
        if (_ttf == null)
        {
            Finish("CURVEGPU VERDICT: FAIL reason=no-ttf");
            return;
        }
        if (!(FontManager.GetFont(kFontName) is CurveBaseFont))
            CurveBaseFont.Register(kFontName, _ttf);
        NextPhase();
    }

    void NextPhase()
    {
        if (_content != null)
        {
            _content.Dispose();
            _content = null;
        }
        _phase++;
        if (_phase >= kPlan.Length)
        {
            Summarise();
            return;
        }
        _frame = 0;
        _gpu.Clear();
        _cpu.Clear();
        BuildPhase(kPlan[_phase].kind);
    }

    void BuildPhase(Kind kind)
    {
        _content = new GComponent();
        float w = GRoot.inst.width, h = GRoot.inst.height;
        _content.SetSize(w, h);
        GRoot.inst.AddChild(_content);

        switch (kind)
        {
            case Kind.Warmup:
            case Kind.Text:
                FillText(w, h, "汉字样张曲线覆盖分析测量文本墙壁排版引擎", 28, false);
                break;
            case Kind.Long:
                FillText(w, h, "龘龘龘龘龘龘龘龘龘龘", 48, false);
                break;
            case Kind.Outline:
                FillText(w, h, "汉字样张曲线覆盖分析测量文本墙壁排版引擎", 28, true);
                break;
            case Kind.Quads:
                //equal-footprint comparator: the same rows, but plain solid
                //rows of GGraph quads — fragment count matches the text wall's
                //padded glyph boxes to first order, shading is flat
                for (float y = 4; y < h - 40; y += 36)
                {
                    var g = new GGraph();
                    g.SetSize(w - 8, 32);
                    _content.AddChild(g);
                    g.SetXY(4, y);
                    g.DrawRect(w - 8, 32, 0, Color.clear, new Color(0.3f, 0.3f, 0.35f, 1f));
                }
                break;
        }
    }

    void FillText(float w, float h, string line, int size, bool outline)
    {
        for (float y = 4; y < h - size - 12; y += size + 8)
        {
            var tf = new GTextField();
            tf.SetSize(w - 8, size + 6);
            _content.AddChild(tf);
            tf.SetXY(4, y);
            TextFormat fmt = tf.textFormat;
            fmt.font = kFontName;
            fmt.size = size;
            fmt.color = Color.white;
            if (outline)
            {
                fmt.outline = 2;
                fmt.outlineColor = new Color(0.8f, 0.1f, 0.1f, 1f);
            }
            tf.textFormat = fmt;
            tf.text = line;
        }
    }

    void Update()
    {
        if (_phase < 0 || _phase >= kPlan.Length)
            return;
        _frame++;
        if (_frame <= kSettleFrames)
        {
            FrameTimingManager.CaptureFrameTimings(); //prime the ring buffer
            return;
        }

        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, _timing) == 1)
        {
            if (_timing[0].gpuFrameTime > 0)
                _gpu.Add(_timing[0].gpuFrameTime);
            if (_timing[0].cpuFrameTime > 0)
                _cpu.Add(_timing[0].cpuFrameTime);
        }

        if (_frame >= kSettleFrames + kSampleFrames)
        {
            _results[kPlan[_phase].label] = (Median(_gpu), Median(_cpu));
            NextPhase();
        }
    }

    static double Median(List<double> v)
    {
        if (v.Count == 0)
            return -1;
        v.Sort();
        return v[v.Count / 2];
    }

    //MIN across rounds, not median: each round's value is itself a median of
    //60 frames (noise-immune); across rounds the only systematic error is
    //thermal throttling, which is strictly one-sided — the least-throttled
    //round is the closest to the hardware's true cost
    static double BestOf(Dictionary<string, (double gpu, double cpu)> r, string prefix)
    {
        double best = -1;
        foreach (var kv in r)
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value.gpu > 0
                && (best < 0 || kv.Value.gpu < best))
                best = kv.Value.gpu;
        return best;
    }

    void Summarise()
    {
        double text = BestOf(_results, "text.");
        double quads = BestOf(_results, "quads.");
        double lng = BestOf(_results, "long.");
        double outline = BestOf(_results, "outline.");

        var sb = new StringBuilder();
        sb.AppendLine($"CURVEGPU ENV: gfx={SystemInfo.graphicsDeviceType} dev={SystemInfo.graphicsDeviceName}"
            + $" screen={Screen.width}x{Screen.height} font={Path.GetFileName(_ttf)}");
        foreach (var kv in _results)
            sb.AppendLine($"CURVEGPU RAW: {kv.Key} gpu={kv.Value.gpu:F3}ms cpu={kv.Value.cpu:F3}ms");
        sb.AppendLine($"CURVEGPU DATA: text={text:F3}ms quads={quads:F3}ms long={lng:F3}ms outline={outline:F3}ms");
        if (quads > 0)
            sb.AppendLine($"CURVEGPU RATIO: text/quads={text / quads:F2} long/quads={lng / quads:F2}"
                + $" outline/text={(text > 0 ? outline / text : -1):F2}");

        //the gate is measurement VALIDITY — the numbers are the deliverable
        bool valid = text > 0 && quads > 0 && lng > 0 && outline > 0;
        sb.AppendLine($"CURVEGPU VERDICT: {(valid ? "PASS" : "FAIL")}"
            + (valid ? "" : " reason=gpu-timings-unavailable (FrameTimingManager returned no gpu times"
                + " — editor, or enableFrameTimingStats off)"));
        Finish(sb.ToString());
    }

    void Finish(string text)
    {
        report = text;
        Debug.Log(text);
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-curvegpuOut");
        if (i >= 0 && i + 1 < args.Length)
        {
            try { File.WriteAllText(args[i + 1], text); }
            catch (Exception e) { Debug.LogWarning("CURVEGPU: write failed: " + e.Message); }
            Application.Quit(text.Contains("VERDICT: PASS") ? 0 : 1);
        }
        if (_content != null)
        {
            _content.Dispose();
            _content = null;
        }
        Destroy(gameObject);
    }
}
#endif
