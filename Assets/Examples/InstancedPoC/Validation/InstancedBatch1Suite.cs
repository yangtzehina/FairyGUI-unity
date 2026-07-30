#if UNITY_2020_1_OR_NEWER
using FairyGUI;
using UnityEngine;

/// <summary>
/// Review batch-1 correctness suite (14 checks), rebuilt from commit 593d583's
/// Validation record: non-Normal blendMode leaves stay native and act as sort
/// barriers (recompile on change, both directions), grayed accumulates down the
/// subtree and desaturates baked instances, and the MergedBatch / duplicate
/// stream mutual exclusion. The MVVM half of batch 1 lives in
/// BinderReentrancyCheck (11 checks). Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedBatch1Suite
{
    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null, s2 = null, s3 = null;
        try
        {
            GComponent root = env.root;
            GGraph rectR = env.Rect(root, 20, 20, 80, 50, Color.red);
            GGraph rectS = env.Rect(root, 120, 20, 80, 50, new Color(1, 165 / 255f, 0)); //orange
            GComponent subD = new GComponent();
            subD.SetSize(200, 60);
            root.AddChild(subD);
            subD.SetXY(20, 90);
            GGraph rectD = env.Rect(subD, 0, 0, 60, 40, Color.magenta);
            GGraph rectE = env.Rect(subD, 70, 0, 60, 40, Color.cyan);
            GComponent comp2 = env.Sibling(380, 20, 160, 80);
            GGraph rectF = env.Rect(comp2, 10, 10, 50, 30, Color.white);
            env.Step(2);

            NGraphics gR = InstancedValidationEnv.G(rectR);
            NGraphics gS = InstancedValidationEnv.G(rectS);
            NGraphics gD = InstancedValidationEnv.G(rectD);
            NGraphics gE = InstancedValidationEnv.G(rectE);
            NGraphics gF = InstancedValidationEnv.G(rectF);

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);

            //--- blend fallback (b1-b5) -------------------------------------
            env.Check("b1.baseline: one run, one segment, all claimed",
                stream.IsClaimed(gR) && stream.IsClaimed(gS) && stream.IsClaimed(gD)
                && stream.IsClaimed(gE) && stream.runCount == 1 && stream.segmentCount == 1
                && stream.lastSkippedPairs == 0);

            rectS.displayObject.blendMode = BlendMode.Add;
            env.Step(1);
            env.Check("b2.non-Normal blend releases the leaf", !stream.IsClaimed(gS)
                && !gS.meshRenderer.forceRenderingOff);
            env.Check("b3.blend leaf is a sort barrier (runs split)", stream.runCount >= 2);
            env.Step(2); //the native blend-material swap lands one frame later
            var px = env.Capture();
            env.Check("b4.blend leaf renders natively additive",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectS, 40, 25), 255, 185, 20, 14));
            rectS.displayObject.blendMode = BlendMode.Normal;
            env.Step(1);
            px = env.Capture();
            env.Check("b5.blend back to Normal re-claims, runs merge",
                stream.IsClaimed(gS) && stream.runCount == 1
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectS, 40, 25), 255, 165, 0, 6));

            //--- grayed propagation (g6-g10) --------------------------------
            rectR.grayed = true;
            env.Step(1);
            bool claimedDuringGray = stream.IsClaimed(gR);
            px = env.Capture();
            Color32 grayR = env.Probe(px, rectR, 40, 25);
            env.Check($"g6.grayed leaf desaturates in-stream {InstancedValidationEnv.Fmt(grayR)}",
                Mathf.Abs(grayR.r - grayR.g) <= 4 && Mathf.Abs(grayR.g - grayR.b) <= 4
                && grayR.r >= 62 && grayR.r <= 90); //luma(red)=0.299*255=76

            subD.grayed = true;
            env.Step(1);
            claimedDuringGray &= stream.IsClaimed(gD) && stream.IsClaimed(gE);
            px = env.Capture();
            Color32 grayD = env.Probe(px, rectD, 30, 20);
            Color32 grayE = env.Probe(px, rectE, 30, 20);
            env.Check($"g7.grayed container inherits down {InstancedValidationEnv.Fmt(grayD)} {InstancedValidationEnv.Fmt(grayE)}",
                Mathf.Abs(grayD.r - grayD.b) <= 4 && grayD.r >= 93 && grayD.r <= 117   //luma(magenta)=105
                && Mathf.Abs(grayE.g - grayE.b) <= 4 && grayE.g >= 167 && grayE.g <= 191); //luma(cyan)=179
            env.Check("g8.sibling outside the grayed subtree keeps color",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectS, 40, 25), 255, 165, 0, 6));

            rectR.grayed = false;
            subD.grayed = false;
            env.Step(1);
            px = env.Capture();
            env.Check("g9.un-gray restores exact colors",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectR, 40, 25), 255, 0, 0)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectD, 30, 20), 255, 0, 255));
            env.Check("g10.grayed leaves stayed claimed throughout", claimedDuringGray);

            //--- MergedBatch / duplicate-stream mutex (m11-m14) -------------
            int claimedBefore = stream.claimedLeafCount;
            bool prevLog = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false; //the mutex paths LogError by design
            s2 = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            Debug.unityLogger.logEnabled = prevLog;
            env.Step(1);
            env.Check("m11.duplicate in-place stream disposes the old one",
                InstancedValidationEnv.C(root).instancedRendering
                && stream.claimedLeafCount == 0 && s2.claimedLeafCount == claimedBefore
                && s2.IsClaimed(gR));

#pragma warning disable 618
            Debug.unityLogger.logEnabled = false;
            InstancedValidationEnv.C(root).mergedBatching = true;
            Debug.unityLogger.logEnabled = prevLog;
            env.Check("m12.mergedBatching refused on a stream container",
                !InstancedValidationEnv.C(root).mergedBatching);

            InstancedValidationEnv.C(comp2).mergedBatching = true;
            bool mergedWasOn = InstancedValidationEnv.C(comp2).mergedBatching;
            Debug.unityLogger.logEnabled = false;
            s3 = new InstancedUIStream(InstancedValidationEnv.C(comp2), default, true, true);
            Debug.unityLogger.logEnabled = prevLog;
            env.Step(1);
            env.Check("m13.new stream force-disables mergedBatching",
                mergedWasOn && !InstancedValidationEnv.C(comp2).mergedBatching
                && s3.IsClaimed(gF));
#pragma warning restore 618

            s3.Dispose();
            s3 = null;
            s2.Dispose();
            s2 = null;
            stream = null; //already disposed by the m11 mutex
            env.Step(1);
            px = env.Capture();
            env.Check("m14.teardown restores native rendering + sortingOrder",
                !gR.meshRenderer.forceRenderingOff
                && gR.meshRenderer.sortingOrder == gR.renderingOrder
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectR, 40, 25), 255, 0, 0)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectF, 25, 15), 255, 255, 255));
        }
        finally
        {
            if (s3 != null) s3.Dispose();
            if (s2 != null) s2.Dispose();
            if (stream != null) stream.Dispose();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
