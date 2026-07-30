#if UNITY_2020_1_OR_NEWER
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

/// <summary>
/// Perf batch-3 incrementalization suite (19 checks), rebuilt from commit
/// 95ea9b1's Validation record and docs/design/batch3-incremental.md:
/// color tier c1-c6 (fade/tint with zero recompiles, exact alpha-basis rescale,
/// release restores native colors), transform slots t1-t10 (first move
/// recompiles into a slot, later move/scale/rotation are matrix writes,
/// slot-riding clip windows follow, text churn on a slot stays tier-2, slot
/// overflow probe, nested slots), extract incrementalization e1-e3 (segment GO
/// identity across recompiles, pixels intact, dispose stops rendering the same
/// frame). Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedBatch3Suite
{
    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        try
        {
            env.WarmGlyphs("XY");

            GComponent root = env.root;
            Image img;
            //white texture: tint modulates the sample, so the tint IS the pixel
            GGraph imgHolder = env.ImageLeaf(root, 20, 20, new Color32(255, 255, 255, 255), out img);
            GGraph rrect = new GGraph();
            rrect.DrawRoundRect(100, 60, new Color(1, 165 / 255f, 0), new float[] { 12, 12, 12, 12 });
            root.AddChild(rrect);
            rrect.SetXY(70, 20);

            GComponent mover = new GComponent();
            mover.SetSize(140, 80);
            root.AddChild(mover);
            mover.SetXY(20, 120);
            GGraph rectM = env.Rect(mover, 10, 10, 60, 40, Color.white);
            GTextField textM = env.Text(mover, 10, 52, 80, 26, "XX", 18);

            GComponent clipComp = new GComponent();
            clipComp.SetSize(120, 50);
            root.AddChild(clipComp);
            clipComp.SetXY(180, 120);
            InstancedValidationEnv.C(clipComp).clipRect = new UnityEngine.Rect(0, 0, 120, 50);
            GGraph rectN = env.Rect(clipComp, 0, 0, 200, 36, Color.white);

            GComponent outer = new GComponent();
            outer.SetSize(140, 90);
            root.AddChild(outer);
            outer.SetXY(340, 120);
            GComponent inner = new GComponent();
            inner.SetSize(100, 60);
            outer.AddChild(inner);
            inner.SetXY(10, 10);
            GGraph rectO = env.Rect(inner, 5, 5, 50, 30, Color.white);

            GGraph flick = env.Rect(root, 560, 20, 10, 10, Color.gray);
            env.Step(2);

            NGraphics gImg = InstancedValidationEnv.G(imgHolder);
            NGraphics gRr = InstancedValidationEnv.G(rrect);

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);
            int e0 = stream.extractCount;

            //--- color tier (c1-c5; c6 = release restore, at the end) -------
            imgHolder.alpha = 0.5f;
            env.Step(1);
            var px = env.Capture();
            env.Check("c1.alpha fade: zero recompile, blended pixel",
                stream.extractCount == e0 && stream.IsClaimed(gImg)
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgHolder, 16, 16), 137, 137, 137, 6));

            img.color = Color.green; //Image.color -> graphics.Tint() -> color tier
            env.Step(1);
            px = env.Capture();
            env.Check("c2.tint: zero recompile, rgb rewritten",
                stream.extractCount == e0
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgHolder, 16, 16), 10, 137, 10, 6));

            imgHolder.alpha = 0.25f;
            env.Step(1);
            imgHolder.alpha = 1f;
            env.Step(1);
            px = env.Capture();
            env.Check("c3.alpha basis rescale is exact after 0.5->0.25->1",
                stream.extractCount == e0
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgHolder, 16, 16), 0, 255, 0, 3));

            imgHolder.alpha = 0f;
            env.Step(1);
            px = env.Capture();
            bool zeroInvisible = InstancedValidationEnv.Near(
                env.Probe(px, imgHolder, 16, 16), InstancedValidationEnv.BG, 4);
            imgHolder.alpha = 0.8f;
            env.Step(1);
            px = env.Capture();
            env.Check("c4.alpha 0 loses the basis, next change takes the full leaf path",
                zeroInvisible && stream.extractCount == e0
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgHolder, 16, 16), 4, 208, 4, 7));

            bool sdfClaimed = stream.IsClaimed(gRr);
            gRr.color = new Color(0.6f, 0, 1);
            gRr.Tint(); //deferred tint on an analytic leaf: full re-emit path once
            env.Step(1);
            px = env.Capture();
            env.Check("c5.tint on SDF-claimed leaf re-emits, zero recompile",
                sdfClaimed && stream.extractCount == e0
                && InstancedValidationEnv.NearRGB(env.Probe(px, rrect, 50, 30), 153, 0, 255, 5));

            //--- transform slots (t1-t8) ------------------------------------
            mover.SetXY(30, 130);
            env.Step(1);
            env.Check("t1.first move recompiles and assigns a slot",
                stream.extractCount == e0 + 1 && stream.slotCount == 1);

            //the old top-left corner probe stays clear of the moved rect's new area
            Vector2 oldCorner = rectM.LocalToGlobal(new Vector2(2, 2));
            mover.SetXY(50, 150);
            env.Step(1);
            env.Check("t2.second move is a matrix write (no recompile)",
                stream.extractCount == e0 + 1 && stream.slotCount == 1);
            px = env.Capture();
            env.Check("t3.pixels follow the slot matrix",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectM, 30, 20), 255, 255, 255)
                && InstancedValidationEnv.Near(env.ProbeStage(px, oldCorner), InstancedValidationEnv.BG, 6));

            //a fixed stage point inside the scaled rect but outside the unscaled
            //one — computed BEFORE the scale so both captures probe the same pixel
            Vector2 beyondOld = mover.LocalToGlobal(new Vector2(95, 45));
            bool beyondWasBg = InstancedValidationEnv.Near(env.ProbeStage(px, beyondOld), InstancedValidationEnv.BG, 6);
            mover.SetScale(1.5f, 1.5f);
            env.Step(1);
            px = env.Capture();
            env.Check("t4.scale rides the slot (pixel extends, no recompile)",
                stream.extractCount == e0 + 1 && beyondWasBg
                && InstancedValidationEnv.NearRGB(env.ProbeStage(px, beyondOld), 255, 255, 255));
            mover.SetScale(1f, 1f);
            env.Step(1);

            mover.rotation = 90;
            env.Step(1);
            px = env.Capture();
            env.Check("t5.rotation rides the slot",
                stream.extractCount == e0 + 1
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectM, 30, 20), 255, 255, 255));
            mover.rotation = 0;
            env.Step(1);

            clipComp.SetXY(185, 125);
            env.Step(1);
            int eClip = stream.extractCount;
            bool clipPromoted = stream.slotCount == 2;
            clipComp.SetXY(200, 140);
            env.Step(1);
            px = env.Capture();
            env.Check("t6.slot-riding clip window follows without recompile",
                clipPromoted && stream.extractCount == eClip
                && InstancedValidationEnv.NearRGB(env.Probe(px, clipComp, 60, 18), 255, 255, 255)
                && InstancedValidationEnv.Near(env.Probe(px, clipComp, 140, 18), InstancedValidationEnv.BG, 6));

            var beforeChurn = env.Capture();
            textM.text = "YY";
            env.Step(2);
            px = env.Capture();
            env.Check("t7.text churn on a slotted subtree stays tier-2 (R4)",
                stream.extractCount == eClip
                && env.DiffCount(beforeChurn, px, textM, 2, 2, 60, 24) > 10
                && env.AnyBright(px, textM, 2, 2, 60, 24));

            inner.SetXY(12, 12);
            env.Step(1);
            outer.SetXY(342, 122);
            env.Step(1);
            int eNest = stream.extractCount;
            outer.SetXY(346, 126);
            env.Step(1);
            px = env.Capture();
            env.Check("t8.nested slots: outer slot move carries the inner (R10)",
                stream.extractCount == eNest && stream.slotCount == 4
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectO, 25, 15), 255, 255, 255));

            //--- t9: slot overflow probe ------------------------------------
            var minis = new List<GComponent>();
            for (int i = 0; i < 17; i++)
            {
                var mc = new GComponent();
                mc.SetSize(12, 10);
                root.AddChild(mc);
                mc.SetXY(20 + i * 24, 230);
                env.Rect(mc, 2, 1, 8, 8, Color.white);
                minis.Add(mc);
            }
            env.Step(1);
            for (int i = 0; i < minis.Count; i++)
                minis[i].SetXY(minis[i].x, 232);
            env.Step(1);
            env.Check($"t9.slot table overflow (slots={stream.slotCount} overflow={stream.slotOverflow})",
                stream.slotCount == InstancedUIStream.MaxTransformSlots - 1
                && stream.slotOverflow == 4 + minis.Count - (InstancedUIStream.MaxTransformSlots - 1));
            foreach (var mc in minis)
                mc.Dispose();
            env.Step(1);

            //--- extract incrementalization (e1-e2) -------------------------
            var idsBefore = InstancedValidationEnv.SegmentIds(InstancedValidationEnv.C(root));
            var beforeRecompile = env.Capture();
            flick.visible = false; //cheap structure change forces a recompile
            env.Step(1);
            var idsAfter = InstancedValidationEnv.SegmentIds(InstancedValidationEnv.C(root));
            bool sameIds = idsBefore.Count == idsAfter.Count && idsBefore.Count > 0;
            for (int i = 0; sameIds && i < idsBefore.Count; i++)
                sameIds &= idsBefore[i] == idsAfter[i];
            env.Check($"e1.segment renderers transfer in place across a recompile ({idsAfter.Count} segs)",
                sameIds);
            px = env.Capture();
            env.Check("e2.pixels intact after the incremental recompile",
                InstancedValidationEnv.NearRGB(env.Probe(px, imgHolder, 16, 16), 4, 208, 4, 7)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectM, 30, 20), 255, 255, 255)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rrect, 50, 30), 153, 0, 255, 5)
                && env.DiffCount(beforeRecompile, px, imgHolder, 0, 0, 32, 32) == 0);

            //--- e3 + c6 + t10: one dispose observed three ways -------------
            stream.Dispose();
            bool segsGoneSameFrame = InstancedValidationEnv.SegmentIds(InstancedValidationEnv.C(root)).Count == 0;
            env.Check("e3.dispose deactivates segments the same frame", segsGoneSameFrame);
            var s = stream;
            stream = null;
            env.Step(1);
            px = env.Capture();
            env.Check("c6.release restores native colors (deferred tint+alpha settled)",
                InstancedValidationEnv.NearRGB(env.Probe(px, imgHolder, 16, 16), 4, 208, 4, 7));
            env.Check("t10.release restores native transforms and clipping",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectM, 30, 20), 255, 255, 255)
                && InstancedValidationEnv.NearRGB(env.Probe(px, clipComp, 60, 18), 255, 255, 255)
                && InstancedValidationEnv.Near(env.Probe(px, clipComp, 140, 18), InstancedValidationEnv.BG, 6));
        }
        finally
        {
            if (stream != null) stream.Dispose();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
