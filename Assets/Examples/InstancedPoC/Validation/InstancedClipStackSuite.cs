#if UNITY_2020_1_OR_NEWER
using FairyGUI;
using UnityEngine;

/// <summary>
/// M3 clip-stack synthetics (10 checks), rebuilt from commit bb77436's record:
/// "window transform incl. y negation, nested fold, asymmetric softness
/// mapping (1,2,3,4)->(1,4,3,2), dedupe, per-leaf stamps, masked-subtree skip"
/// plus the acceptance property — draw count stays flat as clip regions grow
/// (three clipped lists side by side compile to the same segment count as one)
/// and hard-clip windows are pixel-exact.
///
/// Clip entries are read through the stream's validation probes
/// (clipEntryCount / GetClipEntry / GetLeafClipIndex), so this suite asserts
/// the COMPILED clip table, not just the rendered result.
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedClipStackSuite
{
    static bool V4Near(Vector4 a, Vector4 b, float eps = 0.01f)
    {
        return Mathf.Abs(a.x - b.x) < eps && Mathf.Abs(a.y - b.y) < eps
            && Mathf.Abs(a.z - b.z) < eps && Mathf.Abs(a.w - b.w) < eps;
    }

    static string F(Vector4 v)
    {
        return $"({v.x:F1},{v.y:F1},{v.z:F1},{v.w:F1})";
    }

    /// <summary>A clipped container with one over-wide child, at a known place.</summary>
    static GComponent ClipBox(InstancedValidationEnv env, GComponent parent,
        float x, float y, float w, float h, Color fill, out GGraph wide)
    {
        var c = new GComponent();
        c.SetSize(w, h);
        parent.AddChild(c);
        c.SetXY(x, y);
        InstancedValidationEnv.C(c).clipRect = new UnityEngine.Rect(0, 0, w, h);
        wide = env.Rect(c, 0, 0, w * 2, h - 10, fill);
        return c;
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null, multi = null;
        try
        {
            GComponent root = env.root;

            //--- fixture: an outer clip with a nested inner clip -------------
            var outer = new GComponent();
            outer.SetSize(120, 80);
            root.AddChild(outer);
            outer.SetXY(30, 30);
            InstancedValidationEnv.C(outer).clipRect = new UnityEngine.Rect(0, 0, 120, 80);
            var outerLeaf = env.Rect(outer, 0, 0, 200, 20, Color.red);

            var inner = new GComponent();
            inner.SetSize(60, 60);
            outer.AddChild(inner);
            inner.SetXY(80, 30); //extends past the outer window on purpose
            InstancedValidationEnv.C(inner).clipRect = new UnityEngine.Rect(0, 0, 60, 60);
            var innerLeaf = env.Rect(inner, 0, 0, 90, 30, Color.green);

            //two containers with the SAME window: the dedupe case
            var dupA = new GComponent();
            dupA.SetSize(40, 30);
            root.AddChild(dupA);
            dupA.SetXY(200, 30);
            InstancedValidationEnv.C(dupA).clipRect = new UnityEngine.Rect(0, 0, 40, 30);
            var dupLeafA = env.Rect(dupA, 0, 0, 80, 20, Color.blue);
            var dupB = new GComponent();
            dupB.SetSize(40, 30);
            root.AddChild(dupB);
            dupB.SetXY(200, 30); //identical position AND size -> identical folded rect
            InstancedValidationEnv.C(dupB).clipRect = new UnityEngine.Rect(0, 0, 40, 30);
            var dupLeafB = env.Rect(dupB, 0, 0, 80, 20, Color.cyan);

            //a stencil-masked subtree: skipped, counted, never clip-folded
            var masked = new GComponent();
            masked.SetSize(50, 40);
            root.AddChild(masked);
            masked.SetXY(300, 30);
            var maskShape = env.Rect(masked, 0, 0, 50, 40, Color.white);
            InstancedValidationEnv.C(masked).mask = maskShape.displayObject;
            env.Rect(masked, 0, 0, 50, 40, Color.magenta);

            var unclipped = env.Rect(root, 30, 150, 60, 30, Color.white);
            env.Step(2);

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);

            NGraphics gOuter = InstancedValidationEnv.G(outerLeaf);
            NGraphics gInner = InstancedValidationEnv.G(innerLeaf);
            NGraphics gDupA = InstancedValidationEnv.G(dupLeafA);
            NGraphics gDupB = InstancedValidationEnv.G(dupLeafB);
            NGraphics gFree = InstancedValidationEnv.G(unclipped);

            //--- c1: the entry table starts with the None sentinel -----------
            ClipEntry none = stream.GetClipEntry(0);
            env.Check($"c1.entry 0 is the infinite 'no clip' sentinel {F(none.rect)}",
                none.rect.x < -1e29f && none.rect.z > 1e29f && none.soft == Vector4.zero);

            //--- c2: a clip window transforms into stream space with y NEGATED
            uint outerIdx = stream.GetLeafClipIndex(gOuter);
            Vector4 outerRect = stream.GetClipEntry((int)outerIdx).rect;
            //FairyGUI rect (0,0,120,80) at (30,30) -> y-down becomes y-negated:
            //x [30,150], y [-110,-30]
            env.Check($"c2.window transforms with y negation {F(outerRect)}",
                outerIdx != 0 && V4Near(outerRect, new Vector4(30, -110, 150, -30)));

            //--- c3: nested clips fold by INTERSECTION ----------------------
            uint innerIdx = stream.GetLeafClipIndex(gInner);
            Vector4 innerRect = stream.GetClipEntry((int)innerIdx).rect;
            //inner window is x [110,170], y [-120,-60]; folded with the outer
            //window x [30,150], y [-110,-30] -> x [110,150], y [-110,-60]
            env.Check($"c3.nested window folds by intersection {F(innerRect)}",
                innerIdx != 0 && innerIdx != outerIdx
                && V4Near(innerRect, new Vector4(110, -110, 150, -60)));

            //--- c4: an unclipped leaf keeps the sentinel index --------------
            env.Check("c4.leaves outside any clip stay on entry 0",
                stream.GetLeafClipIndex(gFree) == 0);

            //--- c5: identical windows dedupe to one entry ------------------
            uint dA = stream.GetLeafClipIndex(gDupA), dB = stream.GetLeafClipIndex(gDupB);
            env.Check($"c5.identical folded windows dedupe (idx {dA} == {dB})",
                dA != 0 && dA == dB);

            //--- c6: per-leaf stamps are distinct where the windows differ --
            env.Check($"c6.per-leaf clip stamps (outer={outerIdx} inner={innerIdx} dup={dA} free=0)",
                outerIdx != innerIdx && outerIdx != dA && innerIdx != dA
                && stream.clipEntryCount >= 4);

            //--- c7: asymmetric softness maps (l,t,r,b) -> (minX,minY,maxX,maxY)
            InstancedValidationEnv.C(outer).clipSoftness = new Vector4(1, 2, 3, 4);
            //clipSoftness is a plain FIELD (no setter, so nothing can push
            //invalidation): internal clip entries bake it at Extract time, and a
            //bare runtime write is only picked up by the next recompile. The
            //stream's EXTERNAL window re-reads it every Render, so this affects
            //nested clips only.
            bool softBeforeRecompile = stream.GetClipEntry(
                (int)stream.GetLeafClipIndex(gOuter)).soft == Vector4.zero;
            stream.Extract();
            env.Step(1);
            outerIdx = stream.GetLeafClipIndex(gOuter);
            Vector4 soft = stream.GetClipEntry((int)outerIdx).soft;
            //FairyGUI (left,top,right,bottom) in y-down -> our y-negated
            //(minX,minY,maxX,maxY): top becomes yMax, bottom becomes yMin
            env.Check($"c7.asymmetric softness (1,2,3,4) -> (1,4,3,2) {F(soft)}",
                V4Near(soft, new Vector4(1, 4, 3, 2)));
            if (softBeforeRecompile)
                env.Note("clipSoftness is a field, not a property: nested-clip softness changes land on the next recompile (external window re-reads it live)");
            InstancedValidationEnv.C(outer).clipSoftness = null;
            stream.Extract();
            env.Step(1);

            //--- c8: stencil-masked subtrees are skipped and counted --------
            env.Check($"c8.masked subtree skipped and counted (masked={stream.lastMaskedSubtrees})",
                stream.lastMaskedSubtrees == 1);

            //--- c9: hard-clip windows are pixel-exact ----------------------
            var px = env.Capture();
            bool insideOuter = InstancedValidationEnv.NearRGB(env.Probe(px, outer, 60, 10), 255, 0, 0);
            bool beyondOuter = InstancedValidationEnv.Near(env.Probe(px, outer, 160, 10), InstancedValidationEnv.BG, 6);
            bool insideInner = InstancedValidationEnv.NearRGB(env.Probe(px, inner, 20, 15), 0, 255, 0);
            //past the FOLDED edge: inside the inner window but outside the outer one
            bool beyondFold = InstancedValidationEnv.Near(env.Probe(px, inner, 50, 15), InstancedValidationEnv.BG, 6);
            env.Check("c9.hard-clip windows are pixel-exact, including the fold",
                insideOuter && beyondOuter && insideInner && beyondFold);

            //--- c10: draw count stays FLAT as clip regions grow ------------
            //three clipped boxes side by side must compile to the same segment
            //count as one — that is the whole point of indexed clipping
            var solo = new GComponent();
            solo.SetSize(400, 60);
            env.root.AddChild(solo);
            solo.SetXY(20, 220);
            GGraph w1, w2, w3;
            ClipBox(env, solo, 0, 0, 100, 50, Color.red, out w1);
            env.Step(2);
            multi = new InstancedUIStream(InstancedValidationEnv.C(solo), default, true, true);
            env.Step(1);
            int segsOne = multi.segmentCount, clipsOne = multi.clipEntryCount;
            ClipBox(env, solo, 120, 0, 100, 50, Color.green, out w2);
            ClipBox(env, solo, 240, 0, 100, 50, Color.blue, out w3);
            env.Step(2);
            int segsThree = multi.segmentCount, clipsThree = multi.clipEntryCount;
            env.Check($"c10.draw count flat as clip regions grow (segments {segsOne}->{segsThree}, entries {clipsOne}->{clipsThree})",
                segsThree == segsOne && clipsThree == clipsOne + 2);
        }
        finally
        {
            if (multi != null) multi.Dispose();
            if (stream != null) stream.Dispose();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
