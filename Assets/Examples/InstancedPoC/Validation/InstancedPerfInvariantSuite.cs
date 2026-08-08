#if UNITY_2020_1_OR_NEWER
using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

/// <summary>
/// Performance INVARIANTS (tier A of the perf gate): the structural facts the
/// measured speedups actually rest on, asserted as exact counters — never as
/// microseconds. The 18x transform-slot number is 18x because recompiles go
/// 120 -> 0; the 7x mount number is 7x because the subtree walk disappears;
/// the 50% window-open cut is renderers not being created. Counters are
/// deterministic, so this suite belongs with the behavioural ones and can never
/// flake the way a timing threshold does.
///
/// Wall-clock ratios live in the separate fresh-session entry
/// (FairyGUIEditor.InstancedPerfCI) — see Validation/README.md. Absolute
/// microsecond numbers are recorded there, never gated.
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedPerfInvariantSuite
{
    static int SegmentGameObjects(Container c)
    {
        int n = 0;
        Transform t = c.cachedTransform;
        for (int i = 0; i < t.childCount; i++)
            if (t.GetChild(i).name == "InstancedUISegment")
                n++;
        return n;
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        bool prevDefer = NGraphics.deferRenderers;
        try
        {
            env.WarmGlyphs("PerfInvariant0123456789");

            GComponent root = env.root;
            GGraph rectA = env.Rect(root, 20, 20, 60, 40, Color.red);
            GTextField tf = env.Text(root, 20, 70, 240, 34, "AAAA", 24);
            GComponent mover = new GComponent();
            mover.SetSize(120, 60);
            root.AddChild(mover);
            mover.SetXY(20, 120);
            env.Rect(mover, 5, 5, 60, 40, Color.white);
            Image img;
            env.ImageLeaf(root, 300, 20, new Color32(0, 200, 255, 255), out img);
            env.Step(2);

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);

            //--- p1: the transform-slot tier — the basis of the 18x ---------
            mover.SetXY(22, 122);
            env.Step(1);           //first move: one promoting recompile
            int ePromoted = stream.extractCount;
            for (int i = 0; i < 120; i++)
            {
                mover.SetXY(22 + (i % 20), 122 + (i % 13));
                env.Step(1);
            }
            env.Check($"p1.120-frame container tween costs ZERO recompiles after promotion (slots={stream.slotCount})",
                stream.extractCount == ePromoted && stream.slotCount == 1);

            //--- p2: the scroll tier is a uniform write ---------------------
            int e0 = stream.extractCount;
            for (int i = 0; i < 60; i++)
            {
                stream.SetScrollOffset(new Vector2(0, i * 7));
                env.Step(1);
            }
            stream.SetScrollOffset(Vector2.zero);
            env.Step(1);
            env.Check("p2.60 scroll steps cost zero recompiles", stream.extractCount == e0);

            //--- p3: the color tier ------------------------------------------
            e0 = stream.extractCount;
            for (int i = 0; i < 60; i++)
            {
                rectA.alpha = 0.2f + 0.8f * ((i % 10) / 10f);
                env.Step(1);
            }
            rectA.alpha = 1f;
            env.Step(1);
            env.Check("p3.60 alpha changes cost zero recompiles", stream.extractCount == e0);

            //--- p4: text slack keeps same-length churn off the recompiler ---
            e0 = stream.extractCount;
            for (int i = 0; i < 40; i++)
            {
                tf.text = (i & 1) == 0 ? "8888" : "9999";
                env.Step(1);
            }
            env.Check("p4.40 same-length text churns cost zero recompiles",
                stream.extractCount == e0);

            //--- p5: cross-atlas keys keep alternating content in ONE segment
            //shape + dynamic-font text + an image atlas = 3 textures, one draw
            env.Check($"p5.shape+text+image share ONE segment (segments={stream.segmentCount}, runs={stream.runCount})",
                stream.segmentCount == 1 && stream.runCount == 1);

            //--- p6: segment renderers are POOLED across recompiles ---------
            int gosBefore = SegmentGameObjects(InstancedValidationEnv.C(root));
            for (int i = 0; i < 12; i++)
            {
                var tmp = env.Rect(root, 400, 200, 10, 10, Color.gray);
                env.Step(1);
                tmp.Dispose();
                env.Step(1);
            }
            int gosAfter = SegmentGameObjects(InstancedValidationEnv.C(root));
            env.Check($"p6.24 recompiles do not grow the segment GameObject pool ({gosBefore} -> {gosAfter})",
                gosAfter <= gosBefore + 1);

            //--- p7: an idle Render allocates nothing -----------------------
            //(batch 2's push elision: idle frames must skip every SetVector /
            //SetPropertyBlock call, which is also where the garbage would be)
            stream.Render();
            stream.Render();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++)
                stream.Render();
            long idleBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            env.Check($"p7.50 idle Render calls allocate {idleBytes} bytes",
                idleBytes == 0);

            //--- p8: renderless leaves create no Unity render objects -------
            var deferHost = new GComponent();
            deferHost.SetSize(300, 120);
            root.AddChild(deferHost);
            deferHost.SetXY(20, 200);
            var deferred = new List<GGraph>();
            NGraphics.deferRenderers = true;
            try
            {
                for (int i = 0; i < 40; i++)
                    deferred.Add(env.Rect(deferHost, (i % 10) * 28, (i / 10) * 26, 24, 22, Color.green));
            }
            finally { NGraphics.deferRenderers = prevDefer; }
            env.Step(2);
            int renderers = 0, meshes = 0;
            foreach (var d in deferred)
            {
                NGraphics g = InstancedValidationEnv.G(d);
                if (g.meshRenderer != null) renderers++;
                if (g.mesh != null) meshes++;
            }
            env.Check($"p8.40 deferred leaves create 0 renderers while claimed (renderers={renderers}, meshes={meshes})",
                renderers == 0);

            //--- p9: a mounted subtree costs zero walk and zero recompiles ---
            var mountHost = new GComponent();
            mountHost.SetSize(180, 90);
            root.AddChild(mountHost);
            mountHost.SetXY(320, 200);
            GGraph mountLeaf = env.Rect(mountHost, 5, 5, 70, 35, Color.red);
            env.Rect(mountHost, 85, 5, 70, 35, Color.blue);
            env.Rect(mountHost, 5, 45, 70, 35, Color.yellow);
            env.Step(2);
            string reason;
            byte[] blob = FqsBaker.Bake(InstancedValidationEnv.C(mountHost), 0x9100UL, out reason, false);
            bool mounted = blob != null && FqsMount.Mount(mountHost, blob, 0x9100UL);
            stream.Extract();
            env.Step(1);
            int eMount = stream.extractCount;
            for (int i = 0; i < 60; i++)
            {
                mountHost.SetXY(320 + (i % 15), 200 + (i % 9));
                env.Step(1);
            }
            env.Check($"p9.60-frame move of a MOUNTED subtree costs zero recompiles (mounted={mounted})",
                mounted && stream.extractCount == eMount);

            //--- p10: the visibility tier — MOUNT-SCOPED by design ----------
            //outside a mount a toggle goes through the structure channel and
            //recompiles (measured: 40 toggles = 40 recompiles); inside a valid
            //spliced mount it rewrites quad ranges in place
            int eVis = stream.extractCount;
            for (int i = 0; i < 40; i++)
            {
                mountLeaf.visible = (i & 1) == 0;
                env.Step(1);
            }
            mountLeaf.visible = true;
            env.Step(1);
            env.Check($"p10.40 visibility toggles inside a mount cost {stream.extractCount - eVis} recompiles",
                stream.extractCount == eVis);

            //--- p11: draw count does not grow with leaf count --------------
            int segsBefore = stream.segmentCount;
            var extras = new List<GGraph>();
            for (int i = 0; i < 30; i++)
                extras.Add(env.Rect(root, 20 + (i % 15) * 12, 330 + (i / 15) * 14, 10, 12, Color.white));
            env.Step(2);
            env.Check($"p11.+30 leaves do not add segments ({segsBefore} -> {stream.segmentCount})",
                stream.segmentCount == segsBefore);

            //--- p12: tier-2 rewrites do not recompile at scale -------------
            e0 = stream.extractCount;
            for (int i = 0; i < 30; i++)
            {
                extras[i].DrawRect(10, 12, 0, Color.clear,
                    (i & 1) == 0 ? Color.magenta : Color.white);
                env.Step(1);
            }
            env.Check($"p12.30 leaf content rewrites cost {stream.extractCount - e0} recompiles",
                stream.extractCount == e0);

            //--- p13: the vertex-path upload width is what we say it is -----
            //The vertex backend pays 4 vertices per quad, so this number IS
            //its bandwidth. Asserted rather than commented because a widened
            //field costs 4 bytes per vertex silently — the pixel gates would
            //stay green while every WebGL frame got heavier.
            int declared = InstancedUIStream.vertexUploadStride;
            int fromLayout = InstancedUIStream.vertexUploadLayoutSize;
            int marshalled = InstancedUIStream.vertexUploadStructSize;
            //measured is what Unity actually allocated for the segment mesh —
            //the other three are all restatements of our own declaration, so
            //without it p13 could only catch us disagreeing with ourselves
            int measured = stream.measuredVertexStride;
            env.Check($"p13.vertex upload is {declared}B/vertex = {declared * 4}B/quad"
                + $" (layout={fromLayout} struct={marshalled} mesh={measured}, was 104/416)",
                declared == 72 && fromLayout == declared && marshalled == declared
                && (declared & 3) == 0
                && (measured == declared || !InstancedValidationEnv.useVertexBackend));

            //--- p14: the packed glyph index survives the byte round trip ---
            //Under FlagCurveGlyph those four UNorm8 bytes are not radii, they
            //are a glyph index the shader rebuilds as b0 + b1*256 + b2*65536.
            //Checked here on the C# side because the packing is ours; the
            //shader's half of it is exact only because of its floor(x*255+0.5).
            bool glyphRoundTrip = true;
            var one = new QuadInstance[1];
            foreach (uint idx in new uint[] { 0u, 1u, 255u, 256u, 4095u, 65535u })
            {
                one[0] = new QuadInstance { padding = idx, flags = QuadInstance.FlagCurveGlyph };
                uint back = InstancedUIStream.PackedRadiiForDiagnostics(in one[0]);
                if (back != idx) glyphRoundTrip = false;
            }
            env.Check("p14.curve-glyph index survives the packed-byte round trip (0..65535)",
                glyphRoundTrip);

            //--- p15: identical churn does not grow resident bytes ----------
            //Capacities only ever grow by design (pools, slack, buffers); the
            //invariant that keeps long window-churn sessions honest is that a
            //SECOND identical cycle finds every capacity already sized —
            //bytes after cycle 2 must not exceed bytes after cycle 1 (audit:
            //the panel had counts, never bytes, and nobody watched growth)
            long AfterCycle()
            {
                var win = new GComponent();
                win.SetSize(300, 200);
                env.root.AddChild(win);
                for (int i = 0; i < 12; i++)
                    env.Rect(win, (i % 4) * 70 + 5, (i / 4) * 60 + 5, 60, 50,
                        new Color(0.2f + 0.05f * i, 0.4f, 0.6f));
                env.Step(2);
                long bytes = stream.approxResidentBytes;
                win.Dispose();
                env.Step(2);
                return bytes;
            }
            long cycle1 = AfterCycle();
            long cycle2 = AfterCycle();
            env.Check($"p15.identical window churn re-uses capacity (cycle1 {cycle1}B, cycle2 {cycle2}B)",
                cycle1 > 0 && cycle2 <= cycle1);
        }
        finally
        {
            NGraphics.deferRenderers = prevDefer;
            if (stream != null) stream.Dispose();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
