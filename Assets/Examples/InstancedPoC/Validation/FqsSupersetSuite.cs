#if UNITY_2020_1_OR_NEWER
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M8-7 superset-bake suite. The baker temporarily unhides the whole subtree
/// so controller pages / button states / designer-hidden content get REAL
/// quads in the blob; at splice the stateless visibility replay zeroes what
/// the live flags say is hidden, and a later show becomes a range rewrite
/// instead of "absent from blob -> invalidate the mount".
///
/// The checks fall into four groups:
///  - bake side: the superset actually enters the blob, the knob isolates the
///    behavior, and the visibility toggles restore EXACTLY (byte-identical
///    re-bake — the renderingOrder-purity worry from the charter, asserted
///    rather than argued);
///  - splice side: hidden pages render as nothing, page flips are ZERO-extract
///    range rewrites with per-state pixel parity against a runtime twin —
///    including the FIRST-EVER show of content that never rendered before;
///  - hit testing: the mount is a rendering accelerator only, so the current
///    page's button must resolve under Stage.HitTest (the same call a real
///    touch goes through) and BubbleEvent must reach its listener, while the
///    hidden page's button at its own stage point must NOT resolve;
///  - regression: without superset, showing unbaked content still degrades
///    gracefully (invalidate -> runtime walk, pixels stay right).
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class FqsSupersetSuite
{
    /// <summary>Two-page component: page A (cyan band + red "button"), page B
    /// (magenta band + blue "button"), page B hidden since birth — the exact
    /// shape GearDisplay produces, driven by the same visible flag it drives.</summary>
    static GComponent BuildPaged(InstancedValidationEnv env, out GComponent pageA,
        out GComponent pageB, out GGraph btnA, out GGraph btnB)
    {
        var host = new GComponent();
        host.SetSize(240, 140);
        env.root.AddChild(host);

        pageA = new GComponent();
        pageA.SetSize(240, 140);
        host.AddChild(pageA);
        env.Rect(pageA, 10, 10, 200, 40, Color.cyan);
        btnA = env.Rect(pageA, 30, 70, 80, 40, Color.red);

        pageB = new GComponent();
        pageB.SetSize(240, 140);
        host.AddChild(pageB);
        env.Rect(pageB, 10, 10, 200, 40, Color.magenta);
        btnB = env.Rect(pageB, 130, 70, 80, 40, Color.blue);
        pageB.visible = false; //hidden since birth: meshes never built

        return host;
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        bool savedSuperset = FqsBaker.supersetVisibility;
        try
        {
            //--- baseline instance used only for baking --------------------
            GComponent bakeHost = BuildPaged(env, out var bakePageA, out var bakePageB, out _, out _);
            bakeHost.SetXY(10, 10);
            env.Step(2);

            FqsBaker.supersetVisibility = false;
            byte[] visibleOnly = FqsBaker.Bake((Container)bakeHost.displayObject, 0xFEED, out string r1);
            FqsBaker.supersetVisibility = true;
            byte[] superset = FqsBaker.Bake((Container)bakeHost.displayObject, 0xFEED, out string r2);

            int leavesVisibleOnly = FqsBlob.PeekLeafCount(visibleOnly);
            int leavesSuperset = FqsBlob.PeekLeafCount(superset);

            //--- s1: the superset actually enters the blob -----------------
            env.Check($"s1.superset blob carries the hidden page (leaves {leavesVisibleOnly} -> {leavesSuperset})",
                visibleOnly != null && superset != null && leavesSuperset > leavesVisibleOnly
                && leavesSuperset == 4);

            //--- s2: the toggles restore exactly ----------------------------
            bool stateRestored = !bakePageB.displayObject.visible && bakePageA.displayObject.visible;
            byte[] again = FqsBaker.Bake((Container)bakeHost.displayObject, 0xFEED, out _);
            bool byteIdentical = again != null && superset != null && again.Length == superset.Length;
            if (byteIdentical)
                for (int i = 0; i < again.Length; i++)
                    if (again[i] != superset[i]) { byteIdentical = false; break; }
            env.Check($"s2.bake restores visibility and re-bakes byte-identical (restored={stateRestored})",
                stateRestored && byteIdentical);
            bakeHost.Dispose();
            env.Step(1);

            //--- runtime twin + mounted instance, side by side --------------
            GComponent twin = BuildPaged(env, out var twinA, out var twinB, out _, out _);
            twin.SetXY(10, 10);
            GComponent inst = BuildPaged(env, out var instA, out var instB, out var instBtnA, out var instBtnB);
            inst.SetXY(300, 10);

            bool mounted = FqsMount.Mount((Container)inst.displayObject, superset, 0xFEED);
            env.root.instancedRendering = true;
            env.Step(2);
            var stream = InstancedValidationEnv.StreamOf(env.root);

            //--- s3: default state renders identically ----------------------
            env.Check($"s3.superset blob mounts and splices (mounted={mounted})",
                mounted && FqsMount.Of(inst) != null);
            env.Check("s3b.default page pixel-identical to the runtime twin ("
                + PixelSummary(env, twin, inst, out bool same0) + ")", same0);

            //--- s4: FIRST-EVER show of never-rendered content, zero extract -
            //Measure the MOUNTED instance's flip alone: the runtime twin pays a
            //recompile for its own flips — that is the runtime walk's standing
            //price and exactly the delta this station removes. (The first
            //version of this suite flipped both in one window and read the
            //twin's recompiles as the mount's.)
            int e0 = stream.extractCount;
            instA.visible = false; instB.visible = true;
            env.Step(2);
            env.Check($"s4.first-ever page flip costs zero recompiles ({stream.extractCount - e0})",
                stream.extractCount == e0 && FqsMount.Of(inst) != null);
            //now bring the twin to the same state (unmeasured) for the pixels
            twinA.visible = false; twinB.visible = true;
            env.Step(2);
            env.Check("s4b.page B pixel-identical to the runtime twin ("
                + PixelSummary(env, twin, inst, out bool sameB) + ")", sameB);

            //--- s5: flips keep riding the tier; twin recompiles do not evict -
            //Interleave: each twin flip RECOMPILES and re-splices the mount, so
            //this also proves the stateless visibility replay survives被动重拼接
            //— the mounted flips must stay zero-cost measured around their own
            //window regardless.
            int mountedFlipCost = 0;
            for (int i = 0; i < 6; i++)
            {
                bool showA = (i & 1) == 0;
                int ef = stream.extractCount;
                instA.visible = showA; instB.visible = !showA;
                env.Step(1);
                mountedFlipCost += stream.extractCount - ef;
                twinA.visible = showA; twinB.visible = !showA; //unmeasured
                env.Step(1);
            }
            env.Check($"s5.six more mounted flips cost zero recompiles ({mountedFlipCost})",
                mountedFlipCost == 0 && FqsMount.Of(inst) != null);
            env.Check("s5b.settled state still pixel-identical ("
                + PixelSummary(env, twin, inst, out bool sameS) + ")", sameS);

            //--- s6: hit testing — the user's question, asserted -------------
            //state right now: page A visible (i=5 ended with showA=false ->
            //flip once more to a known state)
            twinA.visible = true; twinB.visible = false;
            instA.visible = true; instB.visible = false;
            env.Step(1);

            Vector2 onBtnA = instBtnA.LocalToGlobal(new Vector2(40, 20));
            Vector2 onBtnB = instBtnB.LocalToGlobal(new Vector2(40, 20));

            DisplayObject hitA = Stage.inst.HitTest(onBtnA, true);
            DisplayObject hitB = Stage.inst.HitTest(onBtnB, true);
            env.Check("s6.visible page's button resolves under Stage.HitTest",
                hitA != null && IsSelfOrDescendantOf(hitA, instBtnA.displayObject));
            env.Check("s6b.hidden page's button does NOT resolve at its own point",
                hitB == null || !IsSelfOrDescendantOf(hitB, instBtnB.displayObject));

            //event arrival through the REAL post-hit path: Stage bubbles from
            //the hit target, listeners fire on the way up
            int clicksA = 0, clicksB = 0;
            instBtnA.onClick.Add(() => clicksA++);
            instBtnB.onClick.Add(() => clicksB++);
            hitA.BubbleEvent("onClick", null);
            env.Check($"s6c.click bubbled from the hit target reaches the listener (n={clicksA})",
                clicksA == 1 && clicksB == 0);

            //flip and re-verify both directions
            twinA.visible = false; twinB.visible = true;
            instA.visible = false; instB.visible = true;
            env.Step(1);
            DisplayObject hitB2 = Stage.inst.HitTest(onBtnB, true);
            DisplayObject hitA2 = Stage.inst.HitTest(onBtnA, true);
            bool bNowHittable = hitB2 != null && IsSelfOrDescendantOf(hitB2, instBtnB.displayObject);
            bool aNowNot = hitA2 == null || !IsSelfOrDescendantOf(hitA2, instBtnA.displayObject);
            if (bNowHittable)
                hitB2.BubbleEvent("onClick", null);
            env.Check($"s6d.after the flip the roles swap exactly (B hit={bNowHittable}, A blocked={aNowNot}, clicksB={clicksB})",
                bNowHittable && aNowNot && clicksB == 1 && clicksA == 1);

            inst.Dispose();
            twin.Dispose();
            env.Step(1);

            //--- s7: without superset, absent-show still degrades gracefully -
            GComponent twin2 = BuildPaged(env, out var t2A, out var t2B, out _, out _);
            twin2.SetXY(10, 10);
            GComponent inst2 = BuildPaged(env, out var i2A, out var i2B, out _, out _);
            inst2.SetXY(300, 10);
            bool mounted2 = FqsMount.Mount((Container)inst2.displayObject, visibleOnly, 0xFEED);
            env.Step(2);
            t2A.visible = false; t2B.visible = true;
            i2A.visible = false; i2B.visible = true;
            env.Step(2);
            env.Check("s7.visible-only blob: showing unbaked content falls back and still renders right ("
                + PixelSummary(env, twin2, inst2, out bool same7) + ")",
                mounted2 && same7);
            inst2.Dispose();
            twin2.Dispose();
            env.Step(1);
        }
        finally
        {
            FqsBaker.supersetVisibility = savedSuperset;
            env.root.instancedRendering = false;
            env.Dispose();
        }
        return env.Report();
    }

    static bool IsSelfOrDescendantOf(DisplayObject obj, DisplayObject root)
    {
        for (DisplayObject t = obj; t != null; t = t.parent)
            if (t == root)
                return true;
        return false;
    }

    /// <summary>Pixel-compares the two components' equally-sized regions and
    /// reports mean/bad% so a failure carries its own numbers.</summary>
    static string PixelSummary(InstancedValidationEnv env, GComponent a, GComponent b, out bool same)
    {
        var px = env.Capture();
        double mean = 0, badPct = 0;
        int bad = 0, n = 0;
        for (int y = 2; y < (int)a.height - 2; y++)
        {
            for (int x = 2; x < (int)a.width - 2; x++)
            {
                Color32 ca = env.Probe(px, a, x, y);
                Color32 cb = env.Probe(px, b, x, y);
                int d = Mathf.Abs(ca.r - cb.r) + Mathf.Abs(ca.g - cb.g) + Mathf.Abs(ca.b - cb.b);
                mean += d;
                if (d > 24) bad++;
                n++;
            }
        }
        mean /= Mathf.Max(1, n);
        badPct = 100.0 * bad / Mathf.Max(1, n);
        same = mean < 1.5 && badPct < 0.5;
        return $"mean={mean:F3} bad={badPct:F3}%";
    }
}
#endif
