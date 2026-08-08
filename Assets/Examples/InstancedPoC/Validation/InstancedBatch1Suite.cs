#if UNITY_2020_1_OR_NEWER
using FairyGUI;
using UnityEngine;

/// <summary>
/// Review batch-1 correctness suite (21 checks), rebuilt from commit 593d583's
/// Validation record: non-Normal blendMode leaves stay native and act as sort
/// barriers (recompile on change, both directions), grayed accumulates down the
/// subtree and desaturates baked instances, and the duplicate-stream mutual
/// exclusion (the MergedBatch half retired with MergedBatch itself). The MVVM half of batch 1 lives in
/// BinderReentrancyCheck (11 checks). Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedBatch1Suite
{
    //two quads, one vertex disagreeing: exercises the staged-range rollback
    //(stageCount > 0 AND skipped > 0) that single-quad fixtures never hit
    class TwoQuadFactory : IMeshFactory
    {
        public void OnPopulateMesh(VertexBuffer vb)
        {
            vb.AddQuad(new Rect(0, 0, 30, 30), (Color32)Color.white);
            vb.AddQuad(new Rect(40, 0, 30, 30), (Color32)Color.white);
            vb.colors[5] = new Color32(255, 0, 0, 255);
            vb.AddTriangles();
        }
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null, s2 = null, s3 = null, s4 = null;
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

            //--- duplicate-stream mutex + teardown (m11, m14) ---------------
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

            //(m12/m13 tested the MergedBatch mutex; MergedBatch is deleted —
            //the duplicate-stream half of the mutex lives on in m11)
            s3 = new InstancedUIStream(InstancedValidationEnv.C(comp2), default, true, true);
            env.Step(1);

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

            //--- ancestor grayed (g15-g16): the audit blind spot ------------
            //graying a container ABOVE the stream root reaches native content
            //via context accumulation but claimed quads only via the root
            //grayed chain walk + _NotifyDescendantStreams
            var outerG = env.Sibling(20, 130, 200, 90);
            var innerG = new GComponent();
            innerG.SetSize(180, 70);
            outerG.AddChild(innerG);
            innerG.SetXY(10, 10);
            GGraph rectG = env.Rect(innerG, 10, 10, 60, 40, Color.red);
            env.Step(2);
            s3 = new InstancedUIStream(InstancedValidationEnv.C(innerG), default, true, true);
            env.Step(1);
            int ecG = s3.extractCount;
            outerG.grayed = true;
            env.Step(1);
            px = env.Capture();
            var grayG = env.Probe(px, rectG, 30, 20);
            env.Check($"g15.ancestor gray recompiles ({ecG}->{s3.extractCount}) and desaturates in-stream {InstancedValidationEnv.Fmt(grayG)}",
                s3.extractCount > ecG
                && Mathf.Abs(grayG.r - grayG.g) <= 4 && Mathf.Abs(grayG.g - grayG.b) <= 4
                && grayG.r >= 62 && grayG.r <= 90); //luma(red)=76
            outerG.grayed = false;
            env.Step(1);
            px = env.Capture();
            env.Check("g16.ancestor un-gray restores exact color",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectG, 30, 20), 255, 0, 0));

            //--- corner gradient (g17-g18): the flatten regression ----------
            //an instance carries one color — a 4-corner gradient used to
            //flatten to one topology-dependent vertex; it must fall back
            GGraph rectH = env.Rect(innerG, 100, 15, 60, 40, Color.white);
            var rmH = rectH.shape.graphics.GetMeshFactory<RectMesh>();
            rmH.colors = new Color32[]
            {
                new Color32(255, 0, 0, 255), new Color32(255, 0, 0, 255),
                new Color32(0, 0, 255, 255), new Color32(0, 0, 255, 255),
            };
            rectH.shape.graphics.SetMeshDirty();
            env.Step(2);
            px = env.Capture();
            NGraphics gH = InstancedValidationEnv.G(rectH);
            var cNear = env.Probe(px, rectH, 12, 12);
            var cFar = env.Probe(px, rectH, 48, 28);
            int delta = Mathf.Abs(cNear.r - cFar.r) + Mathf.Abs(cNear.b - cFar.b);
            env.Check($"g17.gradient rect falls back and still renders the gradient {InstancedValidationEnv.Fmt(cNear)}/{InstancedValidationEnv.Fmt(cFar)}",
                !s3.IsClaimed(gH) && s3.lastSkippedPairs > 0 && delta > 80);
            rmH.colors = null;
            rectH.shape.graphics.SetMeshDirty();
            env.Step(1);
            px = env.Capture();
            env.Check("g18.uniform colors re-admit via content push",
                s3.IsClaimed(gH)
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectH, 30, 20), 255, 255, 255));

            //--- g19/g20: reparent across a grayed ancestor (review round 3:
            //the InternalSetParent hook had no test — only the setter path)
            var grayPanel = env.Sibling(240, 130, 90, 80);
            grayPanel.grayed = true;
            env.Step(1);
            int ecR = s3.extractCount;
            grayPanel.AddChild(innerG); //stream root subtree moves under gray
            env.Step(1);
            px = env.Capture();
            var reGray = env.Probe(px, rectG, 30, 20);
            env.Check($"g19.reparent under a grayed panel recompiles ({ecR}->{s3.extractCount}) and desaturates {InstancedValidationEnv.Fmt(reGray)}",
                s3.extractCount > ecR
                && Mathf.Abs(reGray.r - reGray.g) <= 4 && reGray.r >= 62 && reGray.r <= 90);
            outerG.AddChild(innerG); //and back out
            env.Step(1);
            px = env.Capture();
            env.Check("g20.reparent back out restores exact color",
                InstancedValidationEnv.NearRGB(env.Probe(px, rectG, 30, 20), 255, 0, 0));

            //--- g21-g23: their own host + stream ---------------------------
            env.WarmGlyphs("AB");
            var hostT = env.Sibling(340, 130, 200, 110);
            GTextField gradTf = env.Text(hostT, 5, 5, 190, 40, "AB", 30);
            GGraph rectT = env.Rect(hostT, 10, 55, 60, 30, Color.white);
            env.Step(2);
            s4 = new InstancedUIStream(InstancedValidationEnv.C(hostT), default, true, true);
            env.Step(1);
            NGraphics gTf = InstancedValidationEnv.G(gradTf);
            NGraphics gT2 = InstancedValidationEnv.G(rectT);

            //g21: per-glyph gradient text falls back at stream level, re-admits
            bool claimedPlain = s4.IsClaimed(gTf);
            var tfmt = gradTf.textFormat;
            tfmt.gradientColor = new Color32[]
            {
                new Color32(255, 0, 0, 255), new Color32(255, 0, 0, 255),
                new Color32(0, 0, 255, 255), new Color32(0, 0, 255, 255),
            };
            gradTf.textFormat = tfmt;
            env.Step(2);
            bool releasedOnGradient = !s4.IsClaimed(gTf) && s4.lastSkippedPairs > 0;
            tfmt.gradientColor = null;
            gradTf.textFormat = tfmt;
            env.Step(2);
            env.Check($"g21.gradient text releases (claimed {claimedPlain}->{releasedOnGradient}) and re-admits ({s4.IsClaimed(gTf)})",
                claimedPlain && releasedOnGradient && s4.IsClaimed(gTf));

            //g22: pushes on a REJECTED leaf — alpha must not recompile (review
            //round 3 high: a tween frame was a full Extract), a mesh rebuild must
            var rmT = rectT.shape.graphics.GetMeshFactory<RectMesh>();
            rmT.colors = new Color32[]
            {
                new Color32(255, 0, 0, 255), new Color32(255, 0, 0, 255),
                new Color32(0, 0, 255, 255), new Color32(0, 0, 255, 255),
            };
            rectT.shape.graphics.SetMeshDirty();
            env.Step(2);
            bool rejected = !s4.IsClaimed(gT2);
            int ecT = s4.extractCount;
            rectT.alpha = 0.5f;
            env.Step(2);
            bool alphaQuiet = s4.extractCount == ecT;
            rectT.shape.graphics.SetMeshDirty();
            env.Step(1);
            env.Check($"g22.rejected leaf: alpha push is quiet ({alphaQuiet}), mesh push recompiles ({ecT}->{s4.extractCount})",
                rejected && alphaQuiet && s4.extractCount > ecT);
            rectT.alpha = 1f;
            rmT.colors = null;
            rectT.shape.graphics.SetMeshDirty();
            env.Step(1);

            //g23: mixed mesh in ONE leaf — the uniform quad must not leak into
            //the stream when the gradient quad rejects the whole pair set
            //(the staged-range rollback had no non-zero-length coverage)
            int quadsBefore = s4.quadCount;
            rectT.shape.graphics.meshFactory = new TwoQuadFactory();
            rectT.shape.graphics.SetMeshDirty();
            env.Step(2);
            px = env.Capture();
            env.Check($"g23.mixed mesh rejects whole leaf, staged rollback leaks nothing (quads {quadsBefore}->{s4.quadCount}) {InstancedValidationEnv.Fmt(env.Probe(px, rectT, 15, 15))}",
                !s4.IsClaimed(gT2) && s4.lastSkippedPairs > 0
                && s4.quadCount <= quadsBefore
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectT, 15, 15), 255, 255, 255));

            s4.Dispose();
            s4 = null;
            s3.Dispose();
            s3 = null;
        }
        finally
        {
            if (s4 != null) s4.Dispose();
            if (s3 != null) s3.Dispose();
            if (s2 != null) s2.Dispose();
            if (stream != null) stream.Dispose();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
