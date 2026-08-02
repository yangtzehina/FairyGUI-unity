#if UNITY_2020_1_OR_NEWER
using System.IO;
using FairyGUI;
using UnityEngine;

/// <summary>
/// Batch 5b: analytic bold / outline / shadow for CurveBaseFont. The coverage
/// math already yields a signed distance; these effects reuse it in ONE pass —
/// faux bold shifts the threshold by ~0.024em (per glyph, encoded in the uv so
/// UBB [b] works), outline tints the distance band, shadow re-evaluates at an
/// offset — instead of the 4/8 vertex-copy passes other fonts pay.
///
/// The checks pin the pieces that could silently rot:
///  - bold widens coverage, per FIELD format (the uv bit path);
///  - the outline ring exists, carries its color, and turns OFF cleanly (the
///    property block outlives the format — the reset path is the bug magnet);
///  - the shadow lands on the CORRECT side (screen down-right for +x,+y);
///  - the distance band is continuous across glyph BANDS (neighbour-band
///    reach: without it strokes tear at band seams);
///  - under an instanced stream, outline/shadow fields keep their native
///    renderer (sort barrier) while bold-only fields are claimed WITH correct
///    pixels (padding bit 20 — bit 24 would round odd glyph indices in the
///    vertex path's float rebuild).
///
/// Needs a TTF on disk (same candidates as batch 5); skips with one FAIL if
/// none. Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class CurveEffectsSuite
{
    const string kFontName = "CurveFxFont";
    static readonly string[] kFontCandidates =
    {
        "/Library/Fonts/Arial Unicode.ttf",
        "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/System/Library/Fonts/Supplemental/Times New Roman.ttf",
    };

    delegate bool Px(Color32 c);

    static int Count(InstancedValidationEnv env, Color32[] px, GTextField tf, Px pred,
        out Vector2 centroid)
    {
        int n = 0; float sx = 0, sy = 0;
        for (int y = 0; y < (int)tf.height; y += 2)
        {
            for (int x = 0; x < (int)tf.width; x += 2)
            {
                Color32 c = env.Probe(px, tf, x, y);
                if (pred(c)) { n++; sx += x; sy += y; }
            }
        }
        centroid = n > 0 ? new Vector2(sx / n, sy / n) : Vector2.zero;
        return n;
    }

    static bool White(Color32 c) { return c.r > 180 && c.g > 180 && c.b > 180; }
    static bool Red(Color32 c) { return c.r > 140 && c.g < 90 && c.b < 90; }
    static bool Blue(Color32 c) { return c.b > 140 && c.r < 90 && c.g < 90; }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        try
        {
            string ttf = null;
            foreach (var c in kFontCandidates)
                if (File.Exists(c)) { ttf = c; break; }
            if (ttf == null)
            {
                env.Check("f0.font file available (suite prerequisite)", false);
                return env.Report();
            }
            CurveBaseFont.Register(kFontName, ttf);

            GTextField Make(float x, float y, string text, int size,
                System.Action<TextFormat> tweak = null, float h = 70)
            {
                //field height must hug the text: probe regions are field-local
                //rectangles, and an oversized field makes one row's counter
                //read the NEXT row's glyphs (the first run of f1 did exactly
                //that — "bold" lost to "plain" by counting plain+bold together)
                GTextField tf = env.Text(env.root, x, y, 260, h, "", size);
                TextFormat fmt = tf.textFormat;
                fmt.font = kFontName;
                fmt.color = Color.white;
                tweak?.Invoke(fmt);
                tf.textFormat = fmt;
                tf.text = text;
                return tf;
            }

            //--- f1: faux bold widens coverage ------------------------------
            GTextField plain = Make(10, 10, "HH", 48);
            GTextField bold = Make(10, 85, "HH", 48, f => f.bold = true);
            env.Step(2);
            var px = env.Capture();
            int nPlain = Count(env, px, plain, White, out _);
            int nBold = Count(env, px, bold, White, out _);
            env.Check($"f1.bold widens stems ({nPlain} -> {nBold} px)",
                nPlain > 20 && nBold > nPlain * 11 / 10);

            //--- f2: outline ring in its own color --------------------------
            GTextField outlined = Make(290, 10, "HH", 48, f =>
            {
                f.outline = 2;
                f.outlineColor = Color.red;
            });
            env.Step(2);
            px = env.Capture();
            int nRing = Count(env, px, outlined, Red, out _);
            int nFill = Count(env, px, outlined, White, out _);
            int nRingPlain = Count(env, px, plain, Red, out _);
            env.Check($"f2.outline ring renders red around a white fill (ring={nRing} fill={nFill})",
                nRing > 20 && nFill > 20 && nRingPlain < 3);

            //--- f3: turning the outline OFF resets the property block ------
            TextFormat offFmt = outlined.textFormat;
            offFmt.outline = 0;
            outlined.textFormat = offFmt;
            env.Step(2);
            px = env.Capture();
            int nRingOff = Count(env, px, outlined, Red, out _);
            env.Check($"f3.outline off leaves no stale ring (red={nRingOff})", nRingOff < 3);

            //--- f4: shadow on the correct side -----------------------------
            GTextField shadowed = Make(290, 85, "HH", 48, f =>
            {
                f.shadowOffset = new Vector2(5, 5);
                f.shadowColor = Color.blue;
            });
            env.Step(2);
            px = env.Capture();
            int nSh = Count(env, px, shadowed, Blue, out Vector2 shC);
            Count(env, px, shadowed, White, out Vector2 fillC);
            env.Check($"f4.shadow renders blue (n={nSh}) offset down-right (d={shC - fillC})",
                nSh > 20 && shC.x > fillC.x + 1f && shC.y > fillC.y + 1f);

            //--- f5: combined effects keep the fill intact ------------------
            GTextField combo = Make(10, 160, "HH", 48, f =>
            {
                f.bold = true;
                f.outline = 2;
                f.outlineColor = Color.red;
                f.shadowOffset = new Vector2(4, 4);
                f.shadowColor = Color.blue;
            });
            env.Step(2);
            px = env.Capture();
            env.Check("f5.bold+outline+shadow compose (fill/ring/shadow all present)",
                Count(env, px, combo, White, out _) > 20
                && Count(env, px, combo, Red, out _) > 20
                && Count(env, px, combo, Blue, out _) > 10);

            //--- f6: band-seam continuity of the ring -----------------------
            //'三' (three horizontal bars): the gaps between bars are empty
            //distance BANDS, so each bar's outer ring depends on neighbour-band
            //reach — precisely the seam case. ('H' cannot see this fault: its
            //stems cross every band, so the own-band distance always suffices.)
            //Detector: every bar must carry ring red within 4px above AND below.
            //outline 10px at size 120 ~= one full distance band (bbox/8), so
            //the OUTER half of the ring must come from neighbour bands — a 3px
            //ring hides inside the edge's own band and cannot see the fault
            GTextField tall = Make(290, 160, "三", 120, f =>
            {
                f.outline = 10;
                f.outlineColor = Color.red;
            }, 160);
            env.Step(2);
            px = env.Capture();
            int H = (int)tall.height, W = (int)tall.width;
            var fillRow = new bool[H];
            var redRow = new bool[H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x += 1)
                {
                    Color32 c = env.Probe(px, tall, x, y);
                    if (White(c)) fillRow[y] = true;
                    if (Red(c)) redRow[y] = true;
                }
            }
            int bars = 0, ringedBars = 0;
            for (int y = 0; y < H; y++)
            {
                if (!fillRow[y] || (y > 0 && fillRow[y - 1]))
                    continue; //not the top row of a bar
                int top = y, bot = y;
                while (bot + 1 < H && fillRow[bot + 1]) bot++;
                bars++;
                //near ring (1..4px: the edge's own band) AND far ring (7..10px:
                //necessarily a neighbour band at this width) on both sides
                bool nearA = false, farA = false, nearB = false, farB = false;
                for (int k = 1; k <= 4; k++)
                {
                    if (top - k >= 0 && redRow[top - k]) nearA = true;
                    if (bot + k < H && redRow[bot + k]) nearB = true;
                }
                for (int k = 7; k <= 10; k++)
                {
                    if (top - k >= 0 && redRow[top - k]) farA = true;
                    if (bot + k < H && redRow[bot + k]) farB = true;
                }
                if (nearA && nearB && farA && farB) ringedBars++;
            }
            env.Check($"f6.every bar keeps near AND far ring across band seams (bars={bars} ringed={ringedBars})",
                bars >= 3 && ringedBars == bars);

            //--- f7: instanced stream — barrier vs claim --------------------
            var pxBefore = env.Capture();
            env.root.instancedRendering = true;
            env.Step(2);
            px = env.Capture();
            NGraphics gBold = InstancedValidationEnv.G(bold);
            NGraphics gCombo = InstancedValidationEnv.G(combo);
            bool boldClaimed = gBold.meshRenderer.forceRenderingOff;
            bool comboNative = !gCombo.meshRenderer.forceRenderingOff;
            env.Check($"f7.bold-only field is claimed, effects field stays native (bold={boldClaimed} fx={comboNative})",
                boldClaimed && comboNative);

            //--- f8: pixels survive the takeover on both sides --------------
            double mean, badPct;
            env.DiffStats(pxBefore, px, bold, 2, 2, bold.width - 4, bold.height - 4, out mean, out badPct);
            bool boldSame = mean < 1.5 && badPct < 1.0;
            double mean2, badPct2;
            env.DiffStats(pxBefore, px, combo, 2, 2, combo.width - 4, combo.height - 4, out mean2, out badPct2);
            env.Check($"f8.takeover keeps bold pixels (claimed, mean={mean:F3}) and effect pixels (native, mean={mean2:F3})",
                boldSame && mean2 < 1.5 && badPct2 < 1.0);
        }
        finally
        {
            env.root.instancedRendering = false;
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
