#if UNITY_2020_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.Reflection;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M8-2 acceptance suite (19 checks), rebuilt from commit a2209fc's record:
/// FqsMount parses/validates/binds a blob onto the container it was baked from
/// and the enclosing in-place stream SPLICES it at extract instead of walking
/// the subtree. Covers the staleness ladder, the pixel gate deferred from M8-1
/// (mounted vs runtime render pixel-identical), the mount-as-transform-slot
/// tier, push channels through baked leaves (content tier-2, color tier from
/// the record's bakedAlpha), the same-frame self-heal, and the invalidation
/// ladder that silently returns the subtree to the runtime walk.
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedM82Suite
{
    static readonly FieldInfo sSpliced =
        typeof(FqsMount).GetField("spliced", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo sInvalid =
        typeof(FqsMount).GetField("invalid", BindingFlags.NonPublic | BindingFlags.Instance);

    static bool Spliced(FqsMount m) { return m != null && sSpliced != null && (bool)sSpliced.GetValue(m); }
    static bool Invalid(FqsMount m) { return m == null || sInvalid == null || (bool)sInvalid.GetValue(m); }

    /// <summary>Builds the bakeable fixture: shapes, an SDF leaf and a clipped interior.</summary>
    static GComponent BuildFixture(InstancedValidationEnv env, GComponent parent, float x, float y,
        out GGraph rectA, out GGraph rectB, out GComponent clipOwner, out GGraph wide)
    {
        var comp = new GComponent();
        comp.SetSize(200, 140);
        parent.AddChild(comp);
        comp.SetXY(x, y);
        rectA = env.Rect(comp, 5, 5, 60, 40, Color.red);
        rectB = env.Rect(comp, 70, 5, 60, 40, Color.green);
        var rr = new GGraph();
        rr.DrawRoundRect(80, 40, Color.blue, new float[] { 10, 10, 10, 10 });
        comp.AddChild(rr);
        rr.SetXY(5, 55);
        clipOwner = new GComponent();
        clipOwner.SetSize(90, 40);
        comp.AddChild(clipOwner);
        clipOwner.SetXY(100, 55);
        InstancedValidationEnv.C(clipOwner).clipRect = new UnityEngine.Rect(0, 0, 90, 40);
        wide = env.Rect(clipOwner, 0, 0, 160, 30, Color.yellow);
        return comp;
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        bool prevLog = Debug.unityLogger.logEnabled;
        try
        {
            GGraph rectA, rectB, wide;
            GComponent clipOwner;
            GComponent comp = BuildFixture(env, env.root, 20, 20, out rectA, out rectB, out clipOwner, out wide);
            env.Step(2);

            string reason;
            byte[] blob = FqsBaker.Bake(InstancedValidationEnv.C(comp), 0xF00DUL, out reason, false);
            if (blob == null)
            {
                env.Check("m0.fixture bakes (suite prerequisite)", false);
                env.Note("refused: " + reason);
                return env.Report();
            }

            //--- runtime reference render (no mount) ------------------------
            stream = new InstancedUIStream(InstancedValidationEnv.C(env.root), default, true, true);
            env.Step(1);
            int runtimeLeaves = stream.leafCount, runtimeQuads = stream.quadCount;
            var pxRuntime = env.Capture();

            //--- m1/m2/m3: the staleness ladder -----------------------------
            Debug.unityLogger.logEnabled = false;
            bool staleRefused = !FqsMount.Mount(comp, blob, 0xBADBADUL);
            var tampered = (byte[])blob.Clone();
            tampered[0] ^= 0xFF;
            bool tamperRefused = !FqsMount.Mount(comp, tampered, 0xF00DUL);
            var hostile = (byte[])blob.Clone();
            BitConverter.GetBytes(1 << 25).CopyTo(hostile, 20); //quad count
            bool hostileRefused = !FqsMount.Mount(comp, hostile, 0xF00DUL);
            Debug.unityLogger.logEnabled = prevLog;
            env.Check("m1.stale source hash refuses the mount", staleRefused && FqsMount.Of(comp) == null);
            env.Check("m2.tampered blob refuses the mount", tamperRefused);
            env.Check("m3.hostile counts refuse the mount", hostileRefused && FqsMount.Of(comp) == null);

            //--- m4: correct hash mounts and binds --------------------------
            bool mounted = FqsMount.Mount(comp, blob, 0xF00DUL);
            FqsMount fm = FqsMount.Of(comp);
            env.Check("m4.matching hash mounts and binds", mounted && fm != null && !Invalid(fm));

            //--- m5: the stream splices it ----------------------------------
            stream.Extract();
            env.Step(1);
            env.Check($"m5.extract splices the mount (leaves {runtimeLeaves}->{stream.leafCount}, quads {runtimeQuads}->{stream.quadCount})",
                Spliced(fm) && !Invalid(fm)
                && stream.leafCount == runtimeLeaves && stream.quadCount == runtimeQuads);

            //--- m6: THE PIXEL GATE (deferred from M8-1) --------------------
            var pxMounted = env.Capture();
            int diff = env.DiffCount(pxRuntime, pxMounted, comp, 0, 0, comp.width, comp.height);
            env.Check($"m6.mounted render is pixel-identical to runtime (diff={diff})", diff == 0);

            //--- m7: the mount rides a transform slot -----------------------
            int e0 = stream.extractCount;
            comp.SetXY(30, 30);
            env.Step(1);
            var px = env.Capture();
            env.Check("m7.mount move is a tier-1 matrix write (zero extract)",
                stream.extractCount == e0 && !Invalid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectA, 30, 20), 255, 0, 0));

            comp.SetScale(1.4f, 1.4f);
            env.Step(1);
            px = env.Capture();
            env.Check("m8.mount scale rides the slot too",
                stream.extractCount == e0
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectA, 30, 20), 255, 0, 0));
            comp.SetScale(1f, 1f);
            comp.SetXY(20, 20);
            env.Step(1);

            //--- m9: baked clips fold with LIVE owner rects -----------------
            px = env.Capture();
            env.Check("m9.interior clip window clips the baked subtree",
                InstancedValidationEnv.NearRGB(env.Probe(px, clipOwner, 45, 15), 255, 235, 4, 24)
                && InstancedValidationEnv.Near(env.Probe(px, clipOwner, 120, 15), InstancedValidationEnv.BG, 6));

            //--- m10: a slot-riding clip owner moves ------------------------
            clipOwner.SetXY(102, 57);
            env.Step(1);
            int eClip = stream.extractCount;
            clipOwner.SetXY(105, 60);
            env.Step(1);
            px = env.Capture();
            env.Check("m10.clip owner move re-derives its window without recompile",
                stream.extractCount == eClip
                && InstancedValidationEnv.NearRGB(env.Probe(px, clipOwner, 45, 15), 255, 235, 4, 24)
                && InstancedValidationEnv.Near(env.Probe(px, clipOwner, 120, 15), InstancedValidationEnv.BG, 6));

            //--- m11: color tier rescales from the record's bakedAlpha ------
            int e1 = stream.extractCount;
            rectA.alpha = 0.5f;
            env.Step(1);
            px = env.Capture();
            env.Check("m11.color tier works on a baked leaf (bakedAlpha basis)",
                stream.extractCount == e1 && !Invalid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectA, 30, 20), 137, 10, 10, 8));
            rectA.alpha = 1f;
            env.Step(1);

            //--- m12: content tier-2 rewrite re-reads the live mesh ---------
            rectB.DrawRect(60, 40, 0, Color.clear, Color.magenta);
            env.Step(2);
            px = env.Capture();
            env.Check("m12.content tier-2 rewrite of a baked leaf lands",
                !Invalid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectB, 30, 20), 255, 0, 255, 6));

            //--- m13: the splice self-heals a rewritten leaf ----------------
            //force a re-splice: the blob would restore the ORIGINAL green quads,
            //so the mount must re-queue the rewritten leaf in the SAME frame
            stream.Extract();
            env.Step(1);
            px = env.Capture();
            env.Check("m13.re-splice self-heals rewritten leaves the same frame",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectB, 30, 20), 255, 0, 255, 6));

            //--- m14: alpha divergence also re-queues on re-splice ----------
            rectA.alpha = 0.35f;
            env.Step(1);
            stream.Extract();
            env.Step(1);
            px = env.Capture();
            env.Check("m14.re-splice re-queues color state diverging from the bake",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectA, 30, 20), 96, 13, 13, 10));
            rectA.alpha = 1f;
            env.Step(1);

            //--- m15: pixels still identical to a runtime walk --------------
            var pxBeforeUnmount = env.Capture();
            FqsMount.Unmount(InstancedValidationEnv.C(comp));
            stream.Extract();
            env.Step(1);
            px = env.Capture();
            diff = env.DiffCount(pxBeforeUnmount, px, comp, 0, 0, comp.width, comp.height);
            env.Check($"m15.unmount returns to the runtime walk with identical pixels (diff={diff})",
                FqsMount.Of(comp) == null && diff == 0);

            //--- m16: structure change inside the subtree invalidates -------
            FqsMount.Mount(comp, blob, 0xF00DUL);
            fm = FqsMount.Of(comp);
            stream.Extract();
            env.Step(1);
            bool splicedAgain = Spliced(fm);
            var extra = env.Rect(comp, 140, 100, 20, 20, Color.white);
            env.Step(2);
            px = env.Capture();
            env.Check("m16.structure change inside the mount invalidates, runtime walk renders",
                splicedAgain && Invalid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, extra, 10, 10), 255, 255, 255)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectA, 30, 20), 255, 0, 0));

            //--- m17: a blob whose live structure differs fails to bind -----
            //path hashes are child-index CHAINS, so appending a child keeps the
            //prefix intact and still binds — it takes an index SHIFT to diverge
            Debug.unityLogger.logEnabled = false;
            bool appendStillBinds = FqsMount.Mount(comp, blob, 0xF00DUL);
            FqsMount.Unmount(InstancedValidationEnv.C(comp));
            comp.SetChildIndex(extra, 0); //every later child shifts by one
            env.Step(1);
            bool bindRefused = !FqsMount.Mount(comp, blob, 0xF00DUL);
            Debug.unityLogger.logEnabled = prevLog;
            env.Check("m17.index shift refuses the bind (append alone still binds)",
                appendStillBinds && bindRefused && FqsMount.Of(comp) == null);
            extra.Dispose();
            env.Step(2);

            //--- m18: count-changing rewrite invalidates -------------------
            FqsMount.Mount(comp, blob, 0xF00DUL);
            fm = FqsMount.Of(comp);
            stream.Extract();
            env.Step(1);
            bool validBefore = !Invalid(fm) && Spliced(fm);
            //a rounded rect gaining a border goes 1 quad -> 2: the tier-2 path
            //cannot express it and the mount drops to the runtime walk
            var rrObj = comp.GetChildAt(2) as GGraph;
            rrObj.DrawRoundRect(80, 40, Color.blue, new float[] { 10, 10, 10, 10 });
            var rrShape = rrObj.displayObject as Shape;
            rrShape.DrawRoundRect(4, Color.white, Color.blue, 10, 10, 10, 10);
            env.Step(2);
            px = env.Capture();
            env.Check("m18.count-changing rewrite invalidates and still renders",
                validBefore
                && InstancedValidationEnv.NearRGB(env.Probe(px, rrObj, 40, 20), 0, 0, 255, 6));

            //--- m19: mounted extract is not slower than the runtime walk ---
            FqsMount.Unmount(InstancedValidationEnv.C(comp));
            stream.Extract(); //warm
            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();
            for (int i = 0; i < 20; i++) stream.Extract();
            sw.Stop();
            double runtimeMs = sw.Elapsed.TotalMilliseconds / 20;
            FqsMount.Mount(comp, blob, 0xF00DUL);
            stream.Extract(); //warm + splice
            sw.Reset(); sw.Start();
            for (int i = 0; i < 20; i++) stream.Extract();
            sw.Stop();
            double mountedMs = sw.Elapsed.TotalMilliseconds / 20;
            env.Check($"m19.mounted extract beats the runtime walk ({mountedMs:F3}ms vs {runtimeMs:F3}ms)",
                mountedMs < runtimeMs);
            env.Note($"extract speedup {(runtimeMs / Mathf.Max((float)mountedMs, 1e-6f)):F2}x on this fixture");
        }
        finally
        {
            Debug.unityLogger.logEnabled = prevLog;
            if (stream != null) stream.Dispose();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
