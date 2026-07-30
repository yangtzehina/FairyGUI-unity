#if UNITY_2020_1_OR_NEWER
using FairyGUI;
using UnityEngine;

/// <summary>
/// Perf batch-2 behavioral suite (8 checks), rebuilt from commit 8f32136's
/// Validation record ("slack lifecycle + sortingOrder resync"): text leaves
/// reserve power-of-two quad slack so glyph-count changes within the slack stay
/// on the tier-2 leaf-update path (no recompile) with no ghost tail; claimed
/// leaves skip the native sortingOrder write and resync on release. Returns a
/// "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedBatch2Suite
{
    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        try
        {
            env.WarmGlyphs("MWKBA");

            GComponent root = env.root;
            GTextField tf1 = env.Text(root, 20, 20, 320, 40, "MMMM");
            GTextField tf2 = env.Text(root, 20, 70, 320, 40, "AAAA");
            GGraph rect = env.Rect(root, 20, 130, 80, 50, Color.red);
            env.Step(2);
            float w4 = tf1.textWidth;

            NGraphics gT1 = InstancedValidationEnv.G(tf1);
            NGraphics gT2 = InstancedValidationEnv.G(tf2);
            NGraphics gR = InstancedValidationEnv.G(rect);

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);
            int e0 = stream.extractCount;

            //--- slack lifecycle (s1-s6) ------------------------------------
            tf1.text = "MM"; //4 glyphs -> 2, inside the pow2(4) slack
            env.Step(2);
            float w2 = tf1.textWidth;
            var px = env.Capture();
            env.Check("s1.shrink within slack stays tier-2",
                stream.extractCount == e0 && stream.IsClaimed(gT1)
                && env.AnyBright(px, tf1, 4, 6, w2 - 2, 30));
            bool tailClean = true;
            foreach (float y in new[] { 8f, 13f, 18f, 23f })
                for (float x = w2 + 5; x <= w4 - 3; x += 2)
                    tailClean &= InstancedValidationEnv.Near(env.Probe(px, tf1, x, y), InstancedValidationEnv.BG, 8);
            env.Check("s2.slack tail leaves no ghost glyphs", tailClean);

            tf1.text = "MMM"; //back up inside the slack
            env.Step(2);
            float w3 = tf1.textWidth;
            px = env.Capture();
            env.Check("s3.regrow within slack stays tier-2",
                stream.extractCount == e0 && env.AnyBright(px, tf1, w2 + 2, 6, w3 - 2, 30));

            tf1.text = "MMMMM"; //5 > slack 4: one recompile, slack becomes 8
            env.Step(2);
            float w5 = tf1.textWidth;
            px = env.Capture();
            env.Check("s4.growth beyond slack recompiles once",
                stream.extractCount == e0 + 1 && stream.IsClaimed(gT1)
                && env.AnyBright(px, tf1, w4 + 2, 6, w5 - 2, 30));

            var before = px;
            tf1.text = "WWWWW"; //same length: zero slack tax
            env.Step(2);
            px = env.Capture();
            env.Check("s5.same-length churn stays tier-2 and lands",
                stream.extractCount == e0 + 1
                && env.DiffCount(before, px, tf1, 2, 4, w5 + 4, 32) > 30);

            before = px;
            tf1.text = "KKKKK";
            tf2.text = "BBBB";
            env.Step(2);
            px = env.Capture();
            env.Check("s6.two leaves change in one frame (coalesced upload)",
                stream.extractCount == e0 + 1
                && env.DiffCount(before, px, tf1, 2, 4, w5 + 4, 32) > 30
                && env.DiffCount(before, px, tf2, 2, 4, tf2.textWidth + 4, 32) > 30);

            //--- sortingOrder elision + resync (s7-s8) ----------------------
            int soBefore = gR.meshRenderer.sortingOrder;
            var rectX = env.Rect(root, 380, 200, 10, 10, Color.gray);
            root.SetChildIndex(rectX, 0); //shifts every later renderingOrder
            env.Step(1);
            env.Check("s7.claimed leaf skips the native sortingOrder write",
                stream.IsClaimed(gR)
                && gR.renderingOrder != soBefore
                && gR.meshRenderer.sortingOrder == soBefore
                && gR.meshRenderer.sortingOrder != gR.renderingOrder);

            stream.Dispose();
            stream = null;
            env.Step(1);
            px = env.Capture();
            env.Check("s8.release resyncs sortingOrder and restores native pixels",
                gR.meshRenderer.sortingOrder == gR.renderingOrder
                && InstancedValidationEnv.NearRGB(env.Probe(px, rect, 40, 25), 255, 0, 0)
                && env.AnyBright(px, tf1, 4, 6, 60, 30));
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
