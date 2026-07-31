#if UNITY_2020_1_OR_NEWER
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M7 SDF-primitive suite (17 checks), rebuilt from commit 1fc360d's record:
/// "claims, quad counts, packed payloads, pixel probes: interior exact,
/// corners clipped, border band, native fallback visible".
///
/// Rounded rects, borders and equal-axis circles bypass mesh reassembly and
/// emit 1-2 analytic quads; pies, gradient ellipses, true ellipses, rotated
/// and non-uniformly scaled leaves keep the native triangulated path (exactness
/// preserved). Per the record's own correction, the acceptance for SDF arcs is
/// NOT per-pixel equality with the native polygon approximation (analytic AA is
/// deliberately better) — it is interior-exact + corner-clipped + border-band
/// probes.
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedM7SdfSuite
{
    static readonly List<QuadInstance> sQuads = new List<QuadInstance>();

    /// <summary>The compiled quads of one leaf, via the diagnostics probe.</summary>
    static List<QuadInstance> QuadsOf(InstancedUIStream s)
    {
        s.CopyQuadsForDiagnostics(sQuads);
        return sQuads;
    }

    static GGraph RoundRect(InstancedValidationEnv env, GComponent parent, float x, float y,
        float w, float h, Color fill, float radius, float lineWidth = 0, Color line = default)
    {
        var g = new GGraph();
        parent.AddChild(g);
        g.SetXY(x, y);
        g.SetSize(w, h);
        var shape = new Shape();
        g.SetNativeObject(shape);
        shape.DrawRoundRect(lineWidth, line, fill, radius, radius, radius, radius);
        shape.SetSize(w, h);
        return g;
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        try
        {
            GComponent root = env.root;

            //--- fixtures ---------------------------------------------------
            GGraph plain = RoundRect(env, root, 20, 20, 100, 60, Color.red, 16);
            GGraph bordered = RoundRect(env, root, 140, 20, 100, 60, Color.blue, 12, 6, Color.white);

            var circleG = new GGraph();
            root.AddChild(circleG);
            circleG.SetXY(260, 20);
            circleG.SetSize(60, 60);
            var circleShape = new Shape();
            circleG.SetNativeObject(circleShape);
            circleShape.DrawEllipse(Color.green);
            circleShape.SetSize(60, 60);

            //fallback fixtures: pie, gradient ellipse, true ellipse
            var pieG = new GGraph();
            root.AddChild(pieG);
            pieG.SetXY(340, 20);
            pieG.SetSize(60, 60);
            var pieShape = new Shape();
            pieG.SetNativeObject(pieShape);
            pieShape.DrawEllipse(0, Color.clear, Color.clear, Color.yellow, 0, 220);
            pieShape.SetSize(60, 60);

            var gradG = new GGraph();
            root.AddChild(gradG);
            gradG.SetXY(420, 20);
            gradG.SetSize(60, 60);
            var gradShape = new Shape();
            gradG.SetNativeObject(gradShape);
            gradShape.DrawEllipse(0, Color.white, Color.clear, Color.cyan, 0, 360);
            gradShape.SetSize(60, 60);

            var ovalG = new GGraph();
            root.AddChild(ovalG);
            ovalG.SetXY(20, 100);
            ovalG.SetSize(90, 50);
            var ovalShape = new Shape();
            ovalG.SetNativeObject(ovalShape);
            ovalShape.DrawEllipse(Color.magenta);
            ovalShape.SetSize(90, 50);

            GGraph rotated = RoundRect(env, root, 140, 100, 80, 50, Color.white, 10);
            rotated.rotation = 30;
            GGraph nonUniform = RoundRect(env, root, 250, 100, 80, 50, Color.white, 10);
            nonUniform.SetScale(2f, 1f);
            GGraph hugeRadius = RoundRect(env, root, 20, 170, 700, 700, Color.gray, 300);
            hugeRadius.visible = false; //keep it off-screen visually, still extracted
            hugeRadius.visible = true;
            hugeRadius.SetSize(700, 700);
            env.Step(2);

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);

            NGraphics gPlain = InstancedValidationEnv.G(plain);
            NGraphics gBorder = InstancedValidationEnv.G(bordered);
            NGraphics gCircle = InstancedValidationEnv.G(circleG);
            NGraphics gPie = InstancedValidationEnv.G(pieG);
            NGraphics gGrad = InstancedValidationEnv.G(gradG);
            NGraphics gOval = InstancedValidationEnv.G(ovalG);
            NGraphics gRot = InstancedValidationEnv.G(rotated);
            NGraphics gNu = InstancedValidationEnv.G(nonUniform);
            NGraphics gHuge = InstancedValidationEnv.G(hugeRadius);

            //--- s1-s3: claims ----------------------------------------------
            env.Check("s1.rounded rect is claimed analytically", stream.IsClaimed(gPlain));
            env.Check("s2.bordered rounded rect is claimed", stream.IsClaimed(gBorder));
            env.Check("s3.equal-axis circle is claimed", stream.IsClaimed(gCircle));

            //--- s4-s8: the fallback list -----------------------------------
            env.Check("s4.pie keeps the native path", !stream.IsClaimed(gPie));
            env.Check("s5.gradient ellipse keeps the native path", !stream.IsClaimed(gGrad));
            env.Check("s6.true ellipse keeps the native path", !stream.IsClaimed(gOval));
            env.Check("s7.rotated leaf keeps the native path", !stream.IsClaimed(gRot));
            env.Check("s8.non-uniformly scaled leaf keeps the native path", !stream.IsClaimed(gNu));

            //--- s9: radii beyond the packed byte range fall back ------------
            env.Check("s9.radius beyond the 255px packed range falls back",
                !stream.IsClaimed(gHuge));

            //--- s10/s11: quad counts ---------------------------------------
            //find the compiled quads by their rect origin: SDF quads carry the
            //leaf's stream-local rect
            var quads = QuadsOf(stream);
            int fillOnly = 0, borderQuads = 0, sdfTotal = 0;
            foreach (var q in quads)
            {
                if ((q.flags & QuadInstance.FlagSdfFill) != 0) { fillOnly++; sdfTotal++; }
                if ((q.flags & QuadInstance.FlagSdfBorder) != 0) { borderQuads++; sdfTotal++; }
            }
            env.Check($"s10.borderless shapes emit ONE fill quad each (fills={fillOnly})",
                fillOnly == 3); //plain + bordered + circle all have a fill
            env.Check($"s11.a border emits a SECOND quad (borders={borderQuads}, sdf total={sdfTotal})",
                borderQuads == 1 && sdfTotal == 4);

            //--- s12/s13: packed payloads ------------------------------------
            QuadInstance plainQuad = default, borderQuad = default;
            bool foundPlain = false, foundBorder = false;
            foreach (var q in quads)
            {
                if ((q.flags & QuadInstance.FlagSdfFill) != 0 && !foundPlain
                    && Mathf.Abs(q.rect.z - 100) < 0.5f && q.color.b < 0.1f && q.color.r > 0.9f)
                { plainQuad = q; foundPlain = true; }
                if ((q.flags & QuadInstance.FlagSdfBorder) != 0 && !foundBorder)
                { borderQuad = q; foundBorder = true; }
            }
            uint expectRadii = QuadInstance.PackRadii(16, 16, 16, 16);
            env.Check($"s12.corner radii pack into padding (got 0x{plainQuad.padding:X8}, want 0x{expectRadii:X8})",
                foundPlain && plainQuad.padding == expectRadii);
            uint borderWidth = (borderQuad.flags >> 8) & 0xFF;
            env.Check($"s13.border width packs into flags bits 8-15 (got {borderWidth}, want 6)",
                foundBorder && borderWidth == 6);

            //--- s14: fill and border carry their own colors -----------------
            env.Check("s14.fill and border quads carry fillColor / lineColor",
                foundPlain && foundBorder
                && plainQuad.color.r > 0.9f && plainQuad.color.g < 0.1f
                && borderQuad.color.r > 0.9f && borderQuad.color.g > 0.9f && borderQuad.color.b > 0.9f);

            //--- s15: pixel probes — interior exact, corner clipped ---------
            var px = env.Capture();
            bool interior = InstancedValidationEnv.NearRGB(env.Probe(px, plain, 50, 30), 255, 0, 0);
            //(2,2) sits outside a 16px corner radius: the SDF must clip it
            bool cornerClipped = InstancedValidationEnv.Near(
                env.Probe(px, plain, 2, 2), InstancedValidationEnv.BG, 8);
            //well inside the same corner region, on the diagonal, is filled
            bool cornerInside = InstancedValidationEnv.NearRGB(env.Probe(px, plain, 18, 18), 255, 0, 0);
            env.Check("s15.interior exact, corner clipped, corner interior filled",
                interior && cornerClipped && cornerInside);

            //--- s16: the border band ---------------------------------------
            //x=3 on the vertical midline is inside the 6px border band (white),
            //x=30 is the inset fill (blue)
            bool band = InstancedValidationEnv.NearRGB(env.Probe(px, bordered, 3, 30), 255, 255, 255, 40);
            bool inset = InstancedValidationEnv.NearRGB(env.Probe(px, bordered, 40, 30), 0, 0, 255, 40);
            env.Check($"s16.border band is line color, fill is inset under it",
                band && inset);

            //--- s17: native fallback content is still visible ---------------
            //the pie is a wedge, so scan its box for solid fill rather than
            //guessing a point on the sector; the oval is solid at its center
            bool pieVisible = false;
            for (float yy = 4; yy < 56 && !pieVisible; yy += 2)
                for (float xx = 4; xx < 56; xx += 2)
                {
                    Color32 c = env.Probe(px, pieG, xx, yy);
                    if (c.r > 180 && c.g > 160 && c.b < 80) { pieVisible = true; break; }
                }
            bool ovalVisible = InstancedValidationEnv.NearRGB(env.Probe(px, ovalG, 45, 25), 255, 0, 255, 30);
            env.Check($"s17.native fallback shapes still render, interleaved as runs (runs={stream.runCount}, skipped pairs={stream.lastSkippedPairs})",
                pieVisible && ovalVisible && stream.runCount > 1 && stream.lastSkippedPairs > 0);
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
