#if UNITY_2020_1_OR_NEWER
using FairyGUI;
using UnityEngine;

/// <summary>
/// M4 push-protocol scenario regression (19 checks), rebuilt from commit
/// 1f25a37's Validation record: hide/show, filter on/off, enabled toggle,
/// reparent with immediate leaf-side recovery, cross-root move with a single
/// owner at every step, polygon fallback + re-admission, text growth via the
/// content push, dispose recovery. Invoke M4ScenarioSuite.Run() from a Play
/// mode eval; returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class M4ScenarioSuite
{
    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null, stream2 = null;
        try
        {
            env.WarmGlyphs("ABCD");

            GComponent root = env.root;
            GGraph rectA = env.Rect(root, 20, 20, 80, 50, Color.red);
            GGraph rectB = env.Rect(root, 120, 20, 80, 50, Color.blue);
            GGraph poly = env.Rect(root, 220, 20, 80, 50, Color.yellow);
            GTextField tf = env.Text(root, 20, 90, 200, 40, "AB");
            GComponent subComp = new GComponent();
            subComp.SetSize(140, 70);
            root.AddChild(subComp);
            subComp.SetXY(20, 150);
            GGraph rectC = env.Rect(subComp, 0, 0, 60, 40, Color.green);
            GComponent other = env.Sibling(400, 20, 180, 90);
            GComponent comp2 = env.Sibling(400, 140, 200, 120);
            env.Step(2);
            float wAB = tf.textWidth;

            NGraphics gA = InstancedValidationEnv.G(rectA);
            NGraphics gB = InstancedValidationEnv.G(rectB);
            NGraphics gC = InstancedValidationEnv.G(rectC);
            NGraphics gP = InstancedValidationEnv.G(poly);
            NGraphics gT = InstancedValidationEnv.G(tf);

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);

            //--- 1: baseline claim ------------------------------------------
            env.Check("s1.baseline claim (rects+text+subtree claimed)",
                stream.IsClaimed(gA) && stream.IsClaimed(gB) && stream.IsClaimed(gC)
                && stream.IsClaimed(gT) && stream.quadCount > 0 && stream.segmentCount >= 1);

            //--- 2/3/4: visible push, both directions (review M8) -----------
            rectA.visible = false;
            env.Step(1);
            env.Check("s2.hide releases claim + native renderer restored",
                !stream.IsClaimed(gA) && !gA.meshRenderer.forceRenderingOff);
            var px = env.Capture();
            env.Check("s3.hide removes pixels", InstancedValidationEnv.Near(
                env.Probe(px, root, 60, 45), InstancedValidationEnv.BG));
            rectA.visible = true;
            env.Step(1);
            px = env.Capture();
            env.Check("s4.show re-claims and re-renders",
                stream.IsClaimed(gA) && gA.meshRenderer.forceRenderingOff
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectA, 40, 25), 255, 0, 0));

            //--- 5/6/7: filter (painting scope) on/off ----------------------
            subComp.filter = new ColorFilter();
            env.Step(1);
            env.Check("s5.filter releases the painting subtree", !stream.IsClaimed(gC));
            env.Step(2); //the painting capture pipeline settles one frame later
            px = env.Capture();
            env.Check("s6.filtered subtree still renders natively",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectC, 30, 20), 0, 255, 0, 10));
            subComp.filter = null;
            env.Step(1);
            px = env.Capture();
            env.Check("s7.filter off re-claims",
                stream.IsClaimed(gC)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectC, 30, 20), 0, 255, 0));

            //--- 8/9: graphics.enabled admission (review M10/M14) -----------
            gB.enabled = false;
            env.Step(1);
            px = env.Capture();
            env.Check("s8.enabled=false releases and stops pixels",
                !stream.IsClaimed(gB) && InstancedValidationEnv.Near(
                    env.Probe(px, rectB, 40, 25), InstancedValidationEnv.BG));
            gB.enabled = true;
            env.Step(1);
            px = env.Capture();
            env.Check("s9.enabled=true re-admits",
                stream.IsClaimed(gB)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectB, 40, 25), 0, 0, 255));

            //--- 10/11/12: reparent, immediate leaf-side recovery (M13/M9) --
            other.AddChild(rectA);
            env.Check("s10.reparent recovers the leaf IMMEDIATELY",
                !gA.meshRenderer.forceRenderingOff);
            rectA.SetXY(10, 10);
            env.Step(1);
            px = env.Capture();
            env.Check("s11.reparented leaf renders natively at new home",
                !stream.IsClaimed(gA)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectA, 40, 25), 255, 0, 0));
            root.AddChild(rectA);
            rectA.SetXY(20, 20);
            env.Step(1);
            px = env.Capture();
            env.Check("s12.reparent back re-claims",
                stream.IsClaimed(gA) && gA.meshRenderer.forceRenderingOff
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectA, 40, 25), 255, 0, 0));

            //--- 13/14/15: cross-root move, single owner at every step ------
            stream2 = new InstancedUIStream(InstancedValidationEnv.C(comp2), default, true, true);
            env.Step(1);
            int claimedBefore = stream.claimedLeafCount;
            comp2.AddChild(rectB);
            bool immediateRelease = !gB.meshRenderer.forceRenderingOff;
            rectB.SetXY(10, 10);
            env.Step(1);
            env.Check("s13.cross-root move transfers ownership",
                immediateRelease && !stream.IsClaimed(gB) && stream2.IsClaimed(gB));
            env.Check("s14.single owner at every step (counts)",
                stream.claimedLeafCount == claimedBefore - 1 && stream2.claimedLeafCount == 1);
            root.AddChild(rectB);
            rectB.SetXY(120, 20);
            env.Step(1);
            env.Check("s15.cross-root move back",
                stream.IsClaimed(gB) && !stream2.IsClaimed(gB) && stream2.claimedLeafCount == 0);

            //--- 16/17: non-quad fallback and re-admission ------------------
            poly.DrawPolygon(80, 50, new[] {
                new Vector2(40, 0), new Vector2(80, 36), new Vector2(66, 50),
                new Vector2(14, 50), new Vector2(0, 36) }, Color.yellow);
            env.Step(1);
            px = env.Capture();
            env.Check("s16.polygon topology falls back to native",
                !stream.IsClaimed(gP) && stream.lastSkippedPairs > 0
                && InstancedValidationEnv.NearRGB(env.Probe(px, poly, 40, 30), 255, 235, 4, 24));
            poly.DrawRect(80, 50, 0, Color.clear, Color.yellow);
            env.Step(1);
            px = env.Capture();
            env.Check("s17.rect topology re-admits via content push",
                stream.IsClaimed(gP)
                && InstancedValidationEnv.NearRGB(env.Probe(px, poly, 40, 30), 255, 235, 4, 24));

            //--- 18: text growth through the content push -------------------
            int e = stream.extractCount;
            tf.text = "ABCD";
            env.Step(2);
            px = env.Capture();
            env.Check("s18.text growth recompiles by push and renders",
                stream.IsClaimed(gT) && stream.extractCount > e
                && env.AnyBright(px, tf, wAB + 4, 6, tf.width - 4, 30));

            //--- 19: dispose recovery ---------------------------------------
            stream.Dispose();
            bool segsGone = InstancedValidationEnv.SegmentIds(InstancedValidationEnv.C(root)).Count == 0;
            env.Step(1);
            px = env.Capture();
            env.Check("s19.dispose recovers all leaves to native",
                stream.claimedLeafCount == 0 && segsGone
                && !gA.meshRenderer.forceRenderingOff && !gB.meshRenderer.forceRenderingOff
                && !gT.meshRenderer.forceRenderingOff
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectA, 40, 25), 255, 0, 0)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectC, 30, 20), 0, 255, 0));
            stream = null;
        }
        finally
        {
            if (stream != null) stream.Dispose();
            if (stream2 != null) stream2.Dispose();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
