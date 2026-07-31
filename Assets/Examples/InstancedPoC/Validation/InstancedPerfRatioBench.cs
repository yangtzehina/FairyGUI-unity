#if UNITY_2020_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using FairyGUI;
using UnityEngine;

/// <summary>
/// Tier B of the perf gate: wall-clock A/B RATIOS, never absolute numbers.
///
/// Why ratios, and why interleaved: the M8-5 incident (m8-bake-line.md §6) had
/// a real 45% improvement read as 18% inside a long session — and that gate was
/// ALREADY a ratio. The reason is that A and B were measured in sequence, so
/// only B carried the session's GC/driver debt. Here every pair is measured
/// ABAB…, one variant immediately after the other, and the ratio is taken over
/// per-round medians: drift that grows during the run hits both sides equally
/// and cancels out of the ratio.
///
/// Thresholds sit at roughly a third of the historically measured advantage.
/// This gate exists to catch a COLLAPSE (a change that quietly puts the slot
/// path back on the recompiler), not a 10% wobble — a gate that goes red for
/// 10% gets ignored within a month, which is worse than no gate.
///
/// Absolute microseconds are recorded in the report for trend-watching and are
/// NOT gated: they need re-calibration per machine, per Unity version, per CI
/// runner.
///
/// Thresholds were set from measured data, not guessed (4 consecutive runs,
/// M4/Metal, 2022.3.62f3):
///   slot-move        54.1 / 55.0 / 55.1 / 55.4 x   -> gate 5x
///   mounted extract   5.21 / 5.40 / 5.55 / 5.57 x  -> gate 3x
///   tier-2 rewrite   74.7 / 78.5 / 80.3 / 80.4 x   -> gate 3x
///   renderless open  22.5 / 22.7 / 23.9 / 25.5 %   -> gate 15%
/// The renderless gate has a measured BROKEN value too: an earlier version of
/// this bench opened the deferred window with no in-place stream to claim it,
/// so every renderer materialized on the first update and the cut collapsed to
/// 9.1%. 15% sits between that and the healthy band with margin on both sides —
/// which is what a gate is for.
///
/// Run through FairyGUIEditor.InstancedPerfCI (fresh batchmode session) or
/// InstancedPerfRatioBench.Run() from a freshly entered Play mode.
/// </summary>
public static class InstancedPerfRatioBench
{
    const int kRounds = 7;        //odd: median without interpolation
    const int kInner = 12;        //repeats inside one timed sample

    public class Gate
    {
        public string name;
        public string unit;         //what one sample measures
        public double slowMedianMs; //variant A (the path we are beating)
        public double fastMedianMs; //variant B
        public double ratio;        //slow / fast, or the cut fraction
        public double minRatio;     //threshold
        public double spreadPct;    //max deviation of per-round ratios from the median
        public bool isCut;          //true: the gate is a % reduction, not a factor
        public bool Passed { get { return ratio >= minRatio; } }
    }

    static double Median(List<double> xs)
    {
        var c = new List<double>(xs);
        c.Sort();
        return c[c.Count / 2];
    }

    /// <summary>
    /// Runs slow/fast alternately for kRounds rounds and returns the ratio of
    /// the per-round medians plus how much the per-round ratios spread.
    /// </summary>
    /// <summary>
    /// cleanup, when given, runs between repeats OUTSIDE the stopwatch: costs
    /// both variants share (tearing a fixture down) only dilute the ratio.
    /// </summary>
    static Gate Measure(string name, string unit, double minRatio,
        Action slow, Action fast, bool isCut = false, Action cleanup = null)
    {
        var slowMs = new List<double>();
        var fastMs = new List<double>();
        var ratios = new List<double>();

        //warm both paths (JIT, first-touch allocations, GPU buffer creation)
        slow(); cleanup?.Invoke();
        fast(); cleanup?.Invoke();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var sw = new Stopwatch();
        for (int r = 0; r < kRounds; r++)
        {
            sw.Reset();
            for (int i = 0; i < kInner; i++)
            {
                sw.Start(); slow(); sw.Stop();
                cleanup?.Invoke();
            }
            double s = sw.Elapsed.TotalMilliseconds / kInner;

            sw.Reset();
            for (int i = 0; i < kInner; i++)
            {
                sw.Start(); fast(); sw.Stop();
                cleanup?.Invoke();
            }
            double f = sw.Elapsed.TotalMilliseconds / kInner;

            slowMs.Add(s);
            fastMs.Add(f);
            ratios.Add(f > 1e-9 ? (isCut ? 1.0 - f / s : s / f) : double.MaxValue);
        }

        double med = Median(ratios);
        double spread = 0;
        foreach (var x in ratios)
            spread = Math.Max(spread, Math.Abs(x - med) / Math.Max(med, 1e-9));

        return new Gate
        {
            name = name,
            unit = unit,
            slowMedianMs = Median(slowMs),
            fastMedianMs = Median(fastMs),
            ratio = med,
            minRatio = minRatio,
            spreadPct = spread * 100,
            isCut = isCut,
        };
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv(700, 500);
        var gates = new List<Gate>();
        InstancedUIStream stream = null;
        bool prevDefer = NGraphics.deferRenderers;
        try
        {
            //=== gate 1: transform slot vs recompile-on-move =================
            //(batch 3 measured 18x on a 41-leaf subtree)
            {
                var host = new GComponent();
                host.SetSize(320, 180);
                env.root.AddChild(host);
                host.SetXY(10, 10);
                var slotted = new GComponent();
                slotted.SetSize(300, 160);
                host.AddChild(slotted);
                slotted.SetXY(5, 5);
                for (int i = 0; i < 40; i++)
                    env.Rect(slotted, (i % 10) * 29, (i / 10) * 38, 26, 34,
                        new Color(0.2f + 0.02f * i, 0.5f, 0.9f));
                env.Step(2);
                stream = new InstancedUIStream(InstancedValidationEnv.C(host), default, true, true);
                env.Step(1);
                //promote the container into a slot
                slotted.SetXY(6, 6);
                env.Step(2);

                int nudge = 0;
                gates.Add(Measure("slot-move vs recompile", "one interior container move", 5.0,
                    () => { stream.Extract(); },                       //what a non-slotted move costs
                    () =>                                              //what the slot path costs
                    {
                        slotted.SetXY(5 + (++nudge % 7), 5 + (nudge % 5));
                        stream.Flush();
                        stream.Render();
                    }));
                stream.Dispose();
                stream = null;
                host.Dispose();
                env.Step(1);
            }

            //=== gate 2: mounted splice vs runtime walk =====================
            //(M8-2 measured 7.03x on a 200-leaf subtree)
            {
                var host = new GComponent();
                host.SetSize(340, 200);
                env.root.AddChild(host);
                host.SetXY(10, 10);
                var sub = new GComponent();
                sub.SetSize(330, 190);
                host.AddChild(sub);
                sub.SetXY(5, 5);
                for (int i = 0; i < 60; i++)
                    env.Rect(sub, (i % 12) * 27, (i / 12) * 37, 24, 33,
                        new Color(0.9f, 0.3f + 0.01f * i, 0.2f));
                env.Step(2);

                string reason;
                byte[] blob = FqsBaker.Bake(InstancedValidationEnv.C(sub), 0xB1UL, out reason, false);
                stream = new InstancedUIStream(InstancedValidationEnv.C(host), default, true, true);
                env.Step(1);

                if (blob != null)
                {
                    gates.Add(Measure("mounted extract vs runtime walk", "one full Extract", 3.0,
                        () =>
                        {
                            FqsMount.Unmount(InstancedValidationEnv.C(sub));
                            stream.Extract();
                        },
                        () =>
                        {
                            if (FqsMount.Of(sub) == null)
                                FqsMount.Mount(sub, blob, 0xB1UL);
                            stream.Extract();
                        }));
                }
                stream.Dispose();
                stream = null;
                host.Dispose();
                env.Step(1);
            }

            //=== gate 3: tier-2 leaf rewrite vs full recompile ==============
            {
                var host = new GComponent();
                host.SetSize(340, 200);
                env.root.AddChild(host);
                host.SetXY(10, 10);
                var leaves = new List<GGraph>();
                for (int i = 0; i < 60; i++)
                    leaves.Add(env.Rect(host, (i % 12) * 27, (i / 12) * 37, 24, 33, Color.gray));
                env.Step(2);
                stream = new InstancedUIStream(InstancedValidationEnv.C(host), default, true, true);
                env.Step(1);

                int k = 0;
                //UpdateLeaf IS the tier-2 path (re-reads the live mesh and
                //uploads just that range). Driving it directly keeps the
                //measurement on the stream instead of on a stage walk — and
                //avoids the trap of "rewriting" a mesh that was never rebuilt.
                gates.Add(Measure("tier-2 rewrite vs recompile", "one leaf rewrite", 3.0,
                    () => { stream.Extract(); },
                    () =>
                    {
                        var leaf = leaves[(++k) % leaves.Count];
                        stream.UpdateLeaf(InstancedValidationEnv.G(leaf));
                    }));
                stream.Dispose();
                stream = null;
                host.Dispose();
                env.Step(1);
            }

            //=== gate 4: renderless open vs ordinary open ===================
            //(M8-5 measured a 50% cut at 80 leaves; the charter gate was >=40%)
            //
            //The window MUST open under a live in-place stream: the renderless
            //grace (`liveInPlaceCount > 0 && ++_renderlessUpdates <= 2`) only
            //holds while something can claim the leaves. Opening deferred
            //content with no stream materializes every renderer on the first
            //update — measured 9% "saving", i.e. none, which is the honest
            //behaviour and not what this gate is about.
            {
                var openHost = new GComponent();
                openHost.SetSize(360, 220);
                env.root.AddChild(openHost);
                openHost.SetXY(10, 250);
                openHost.instancedRendering = true; //the claimer
                env.Step(2);

                GComponent pending = null;
                Action<bool> open = defer =>
                {
                    var win = new GComponent();
                    win.SetSize(350, 210);
                    openHost.AddChild(win);
                    bool prev = NGraphics.deferRenderers;
                    NGraphics.deferRenderers = defer;
                    try
                    {
                        for (int i = 0; i < 80; i++)
                        {
                            var g = new GGraph();
                            win.AddChild(g);
                            g.SetXY((i % 16) * 21, (i / 16) * 39);
                            var shape = new Shape();
                            g.SetNativeObject(shape);
                            shape.DrawRect(0, Color.clear, Color.green);
                            shape.SetSize(18, 35);
                        }
                    }
                    finally { NGraphics.deferRenderers = prev; }
                    Stage.inst.ForceUpdate(); //claim + first update
                    pending = win;
                };
                //closing the window is a cost BOTH variants pay — timing it
                //only dilutes the ratio, so it runs outside the stopwatch
                Action closeWindow = () =>
                {
                    if (pending == null)
                        return;
                    pending.Dispose();
                    pending = null;
                    Stage.inst.ForceUpdate();
                };
                //15%: healthy measures 22.5-25.5%, the broken path (nothing
                //claims the deferred leaves, so they all materialize) measures
                //9.1%. Per-open timing is dominated by GameObject churn, so the
                //round spread is wide — the 7-round median is what stabilises it
                gates.Add(Measure("renderless open vs ordinary open", "80-leaf window open", 0.15,
                    () => open(false), () => open(true), isCut: true, cleanup: closeWindow));
                openHost.Dispose();
                env.Step(1);
            }
        }
        finally
        {
            NGraphics.deferRenderers = prevDefer;
            if (stream != null) stream.Dispose();
            env.Dispose();
        }

        //--- report ------------------------------------------------------
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        foreach (var g in gates)
        {
            if (g.Passed) pass++; else fail++;
            string got = g.isCut ? $"{g.ratio * 100:F1}% cut" : $"{g.ratio:F2}x";
            string want = g.isCut ? $">={g.minRatio * 100:F0}% cut" : $">={g.minRatio:F1}x";
            sb.Append(g.Passed ? "PASS " : "FAIL ").Append(g.name)
              .Append(": ").Append(got).Append(" (").Append(want).Append(") ")
              .Append($"[A {g.slowMedianMs * 1000:F1}µs vs B {g.fastMedianMs * 1000:F1}µs per {g.unit}, ")
              .Append($"round spread {g.spreadPct:F1}%]").Append('\n');
        }
        sb.Append($"\n{kRounds} interleaved rounds x {kInner} repeats; ratios from per-round medians.\n");
        sb.Append("Absolute microseconds are recorded, never gated.\n");
        sb.Insert(0, $"RESULT pass={pass} fail={fail}\n");
        return sb.ToString();
    }
}
#endif
