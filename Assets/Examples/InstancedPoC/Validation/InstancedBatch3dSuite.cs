#if UNITY_2020_1_OR_NEWER
using FairyGUI;
using UnityEngine;

/// <summary>
/// Batch 3d cross-atlas segment-key suite (10 checks), rebuilt from commit
/// b03f141's Validation record: a segment carries up to 4 textures
/// (_MainTex + _Tex1.._Tex3) with per-quad sampler selection via flags bits
/// 16-17 — shape + dynamic-font text merge into ONE segment, six distinct
/// textures split 4+2, per-slot pixel colors are exact, same-length text churn
/// keeps its texture slot on tier-2, and grayed (0.59 green luminance) coexists
/// with the slot bits. Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedBatch3dSuite
{
    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        try
        {
            env.WarmGlyphs("HiaYo");

            GComponent root = env.root;
            GTextField tf = env.Text(root, 20, 80, 200, 40, "Hi Hi");
            Image iR, iG, iB, iY, iM;
            GGraph imgR = env.ImageLeaf(root, 20, 20, new Color32(255, 0, 0, 255), out iR);
            GGraph imgG = env.ImageLeaf(root, 70, 20, new Color32(0, 255, 0, 255), out iG);
            GGraph imgB = env.ImageLeaf(root, 120, 20, new Color32(0, 0, 255, 255), out iB);
            GGraph imgY = env.ImageLeaf(root, 170, 20, new Color32(255, 255, 0, 255), out iY);
            GGraph imgM = env.ImageLeaf(root, 220, 20, new Color32(255, 0, 255, 255), out iM);
            imgR.visible = imgG.visible = imgB.visible = imgY.visible = imgM.visible = false;
            env.Step(2);

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);

            //--- d1: the headline merge — shape + text, one segment ---------
            env.Check($"d1.shape + dynamic-font text merge to ONE segment (segs={stream.segmentCount})",
                stream.segmentCount == 1 && stream.runCount == 1 && stream.quadCount > 0);

            //--- d2: four distinct textures still one segment ---------------
            imgR.visible = imgG.visible = true;
            env.Step(1);
            env.Check($"d2.four textures share the segment (segs={stream.segmentCount})",
                stream.segmentCount == 1);

            //--- d3: six distinct textures split 4+2 ------------------------
            imgB.visible = imgY.visible = true;
            env.Step(1);
            env.Check($"d3.six textures split into two segments (segs={stream.segmentCount})",
                stream.segmentCount == 2);

            //--- d4/d5: per-slot pixel colors exact -------------------------
            var px = env.Capture();
            env.Check("d4.first-segment texture slots sample exactly (red/green)",
                InstancedValidationEnv.NearRGB(env.Probe(px, imgR, 16, 16), 255, 0, 0, 3)
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgG, 16, 16), 0, 255, 0, 3));
            env.Check("d5.second-segment texture slots sample exactly (blue/yellow)",
                InstancedValidationEnv.NearRGB(env.Probe(px, imgB, 16, 16), 0, 0, 255, 3)
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgY, 16, 16), 255, 255, 0, 3));

            //--- d6: shape + text slots in the merged segment ---------------
            bool bgExact = InstancedValidationEnv.Near(
                env.Probe(px, env.bg, 400, 300), InstancedValidationEnv.BG, 3);
            bool textBright = env.AnyBright(px, tf, 2, 4, 70, 32);
            //if the glyph quads sampled a wrong (solid) texture slot, the whole
            //row would be an opaque bar — demand dark gaps too
            bool textHasGaps = !env.AllNear(px, tf, 2, 4, 70, 32, new Color32(255, 255, 255, 255), 40);
            env.Check("d6.shape stays exact and glyphs sample the font slot",
                bgExact && textBright && textHasGaps);

            //--- d7: same-length churn keeps the texture slot on tier-2 -----
            int e = stream.extractCount;
            var before = px;
            tf.text = "Yo Yo";
            env.Step(2);
            px = env.Capture();
            env.Check("d7.same-length churn stays tier-2 and keeps its slot",
                stream.extractCount == e
                && env.DiffCount(before, px, tf, 2, 4, 90, 32) > 10
                && env.AnyBright(px, tf, 2, 4, 90, 32)
                && !env.AllNear(px, tf, 2, 4, 90, 32, new Color32(255, 255, 255, 255), 40));

            //--- d8: grayed coexists with the texture-slot bits -------------
            imgG.grayed = true;
            env.Step(1);
            px = env.Capture();
            Color32 gray = env.Probe(px, imgG, 16, 16);
            env.Check($"d8.grayed green -> 0.59 luminance gray {InstancedValidationEnv.Fmt(gray)}",
                Mathf.Abs(gray.r - gray.g) <= 4 && Mathf.Abs(gray.g - gray.b) <= 4
                && gray.g >= 138 && gray.g <= 162); //0.587*255 = 150
            imgG.grayed = false;
            env.Step(1);

            //--- d9: a 7th texture keeps the 2-segment split ----------------
            imgM.visible = true;
            env.Step(1);
            px = env.Capture();
            env.Check($"d9.seventh texture stays in segment 2 (segs={stream.segmentCount})",
                stream.segmentCount == 2
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgM, 16, 16), 255, 0, 255, 3)
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgR, 16, 16), 255, 0, 0, 3));

            //--- d10: recompile transfer with texture-set matching ----------
            imgM.visible = false;
            env.Step(1);
            px = env.Capture();
            env.Check("d10.recompile back to 4+2 keeps every slot exact",
                stream.segmentCount == 2
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgR, 16, 16), 255, 0, 0, 3)
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgG, 16, 16), 0, 255, 0, 3)
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgB, 16, 16), 0, 0, 255, 3)
                && InstancedValidationEnv.NearRGB(env.Probe(px, imgY, 16, 16), 255, 255, 0, 3)
                && InstancedValidationEnv.Near(env.Probe(px, env.bg, 400, 300), InstancedValidationEnv.BG, 3));
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
