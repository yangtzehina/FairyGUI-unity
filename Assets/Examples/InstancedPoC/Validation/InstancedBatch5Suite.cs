#if UNITY_2020_1_OR_NEWER
using System.Collections;
using System.IO;
using System.Reflection;
using FairyGUI;
using UnityEngine;

/// <summary>
/// Batch 5 curve-text suite (10 checks), rebuilt from commits dd9f335
/// (CurveBaseFont: registration + shader wiring, the NGraphics side table, UBB
/// per-run color reaching the stream, the underline solid quad, glyph slack)
/// and 6a42c0f (curve data textures: the vertex-stream backend CLAIMS curve
/// text instead of treating it as a barrier, and instanced curve glyphs
/// actually render). Returns a "RESULT pass=N fail=N" report.
///
/// The suite needs a TTF on disk (the store is a single-font singleton); it
/// tries the A/B verdict's typeface first and falls back to other system
/// faces, reporting which one it used.
/// </summary>
public static class InstancedBatch5Suite
{
    static readonly string[] kFontCandidates =
    {
        "/Library/Fonts/Arial Unicode.ttf",
        "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/System/Library/Fonts/Supplemental/Times New Roman.ttf",
    };

    const string kFontName = "CurveValidationFont";

    /// <summary>NGraphics._curveGlyphs is internal to the FairyGUI assembly.</summary>
    static readonly FieldInfo sCurveGlyphsField =
        typeof(NGraphics).GetField("_curveGlyphs", BindingFlags.NonPublic | BindingFlags.Instance);

    static IList SideTable(NGraphics g)
    {
        return sCurveGlyphsField != null ? sCurveGlyphsField.GetValue(g) as IList : null;
    }

    /// <summary>glyphIndex of side-table entry i (-1 = solid underline rect).</summary>
    static int GlyphIndexAt(IList table, int i)
    {
        object entry = table[i];
        return (int)entry.GetType().GetField("glyphIndex").GetValue(entry);
    }

    static Color32 ColorAt(IList table, int i)
    {
        object entry = table[i];
        return (Color32)entry.GetType().GetField("color").GetValue(entry);
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        try
        {
            string ttf = null;
            foreach (var c in kFontCandidates)
            {
                if (File.Exists(c)) { ttf = c; break; }
            }
            if (ttf == null)
            {
                env.Check("b5.font file available (suite prerequisite)", false);
                env.Note("no candidate TTF found: " + string.Join(", ", kFontCandidates));
                return env.Report();
            }

            //--- v1: registration + store load ------------------------------
            CurveBaseFont registered = CurveBaseFont.Register(kFontName, ttf);
            BaseFont looked = FontManager.GetFont(kFontName);
            //FontManager.GetFont FABRICATES a DynamicFont for unknown names, so
            //presence must be tested by TYPE, never against null (dd9f335)
            env.Check($"v1.CurveBaseFont registers and resolves by name [{Path.GetFileName(ttf)}]",
                registered != null && looked is CurveBaseFont && ReferenceEquals(looked, registered)
                && CurveFontStore.loaded && CurveFontStore.unitsPerEm > 0);

            GComponent root = env.root;
            GTextField tf = env.Text(root, 20, 20, 420, 60, "", 40);
            TextFormat fmt = tf.textFormat;
            fmt.font = kFontName;
            fmt.color = Color.white;
            tf.textFormat = fmt;
            tf.text = "HHHH";
            env.Step(2);

            NGraphics gT = InstancedValidationEnv.G(tf);

            //--- v2: native shader wiring -----------------------------------
            env.Check($"v2.leaf is wired to the curve shader [{gT.shader}]",
                gT.shader == "FairyGUI/CurveText" && gT.texture != null);

            //--- v3: the NGraphics side table ------------------------------
            IList table = SideTable(gT);
            env.Check($"v3.side table mirrors one quad per glyph (n={(table != null ? table.Count : -1)})",
                sCurveGlyphsField != null && table != null && table.Count == 4
                && GlyphIndexAt(table, 0) >= 0);

            //--- native baseline pixels before the takeover -----------------
            var pxNative = env.Capture();
            bool nativeGlyphs = env.AnyBright(pxNative, tf, 2, 2, 200, 55);

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);

            //--- v4: the vertex path CLAIMS curve text (6a42c0f) -----------
            //before the data-texture rewrite this leaf was a fallback barrier
            env.Check($"v4.{InstancedValidationEnv.expectedBackend} backend claims the curve leaf (no barrier)",
                stream.backendName == InstancedValidationEnv.expectedBackend && stream.IsClaimed(gT)
                && stream.runCount == 1 && stream.lastSkippedPairs == 0
                && gT.meshRenderer.forceRenderingOff);

            //--- v5: instanced curve glyphs actually render (6a42c0f) -------
            var px = env.Capture();
            bool instancedGlyphs = env.AnyBright(px, tf, 2, 2, 200, 55);
            //glyphs, not a solid bar: demand dark gaps between the strokes
            bool hasGaps = !env.AllNear(px, tf, 2, 2, 200, 55, new Color32(255, 255, 255, 255), 40);
            env.Check("v5.instanced curve glyph pixels render (with gaps)",
                nativeGlyphs && instancedGlyphs && hasGaps);

            //--- v6: glyph slack keeps length changes on tier-2 ------------
            int e0 = stream.extractCount;
            var beforeChurn = px;
            tf.text = "HHH"; //4 -> 3, inside the pow2(4) slack
            env.Step(2);
            px = env.Capture();
            env.Check("v6.glyph-count slack keeps a shorter run on tier-2",
                stream.extractCount == e0 && stream.IsClaimed(gT)
                && env.DiffCount(beforeChurn, px, tf, 2, 2, 200, 55) > 10
                && env.AnyBright(px, tf, 2, 2, 120, 55));

            //--- v7: same-length churn is a tier-2 rewrite -----------------
            beforeChurn = px;
            tf.text = "OOO";
            env.Step(2);
            px = env.Capture();
            env.Check("v7.same-length churn rewrites in place (tier-2)",
                stream.extractCount == e0
                && env.DiffCount(beforeChurn, px, tf, 2, 2, 200, 55) > 10
                && env.AnyBright(px, tf, 2, 2, 200, 55));

            //--- v8: UBB per-run color reaches the stream ------------------
            tf.UBBEnabled = true;
            tf.text = "[color=#FF0000]OO[/color]OO";
            env.Step(2);
            table = SideTable(gT);
            bool tableTwoColors = false;
            if (table != null && table.Count >= 4)
            {
                Color32 first = ColorAt(table, 0);
                Color32 last = ColorAt(table, table.Count - 1);
                tableTwoColors = first.r > 200 && first.g < 60 && last.g > 200;
            }
            px = env.Capture();
            //the red run occupies the leading glyphs, the default run the tail
            bool redInStream = false, whiteInStream = false;
            for (float x = 4; x < 200; x += 2)
                for (float y = 6; y < 52; y += 2)
                {
                    Color32 c = env.Probe(px, tf, x, y);
                    if (c.r > 150 && c.g < 70 && c.b < 70) redInStream = true;
                    else if (c.r > 180 && c.g > 180 && c.b > 180) whiteInStream = true;
                }
            env.Check("v8.UBB per-run color reaches the side table and the stream",
                tableTwoColors && redInStream && whiteInStream);

            //--- v9: underline emits a solid (glyphIndex -1) quad ----------
            tf.UBBEnabled = false;
            tf.text = "HHHH";
            fmt = tf.textFormat;
            fmt.underline = true;
            tf.textFormat = fmt;
            env.Step(2);
            table = SideTable(gT);
            int solids = 0, glyphs = 0;
            if (table != null)
            {
                for (int i = 0; i < table.Count; i++)
                {
                    if (GlyphIndexAt(table, i) < 0) solids++; else glyphs++;
                }
            }
            px = env.Capture();
            //the underline is a horizontal solid run: a row far below the glyph
            //bodies that is continuously lit across the text width
            bool solidRow = false;
            for (float y = 40; y <= 56 && !solidRow; y += 1)
            {
                bool all = true;
                for (float x = 6; x <= 60; x += 2)
                {
                    Color32 c = env.Probe(px, tf, x, y);
                    if (c.r + c.g + c.b < 300) { all = false; break; }
                }
                solidRow = all;
            }
            env.Check($"v9.underline emits a solid quad (solids={solids} glyphs={glyphs})",
                solids == 1 && glyphs == 4 && solidRow);
            fmt = tf.textFormat;
            fmt.underline = false;
            tf.textFormat = fmt;
            env.Step(2);

            //--- v10: release restores native curve rendering --------------
            stream.Dispose();
            stream = null;
            env.Step(2);
            px = env.Capture();
            env.Check("v10.release restores native curve-shader rendering",
                !gT.meshRenderer.forceRenderingOff
                && env.AnyBright(px, tf, 2, 2, 200, 55)
                && !env.AllNear(px, tf, 2, 2, 200, 55, new Color32(255, 255, 255, 255), 40));
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
