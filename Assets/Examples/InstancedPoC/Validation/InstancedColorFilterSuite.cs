#if UNITY_2020_1_OR_NEWER
using FairyGUI;
using UnityEngine;

/// <summary>
/// ColorFilter-on-claimed-leaf regression (8 checks), from the adversarial
/// review of the container-scope barrier batch. The pre-existing gap:
/// ColorFilter on an Image/MovieClip leaf only toggles the COLOR_FILTER
/// shader keyword plus a property-block matrix — no stream notification
/// existed and ExtractLeaf never looked at keywords, so a filter applied to
/// a claimed leaf at runtime (the game-icon graying idiom) silently never
/// rendered, and every later recompile claimed the leaf right back.
/// Covers: runtime filter on a claimed leaf → released to native with the
/// filter visible in pixels; the fallback is a run barrier; unrelated
/// recompiles keep it native; filter removal — which leaves an all-null
/// _shaderKeywords array behind — re-claims; and the custom-material half
/// of the ExtractLeaf predicate, both directions. Invoke
/// InstancedColorFilterSuite.Run() from a Play mode eval.
/// </summary>
public static class InstancedColorFilterSuite
{
    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        Material customMat = null;
        try
        {
            GComponent root = env.root;
            env.Rect(root, 20, 20, 60, 40, Color.red); //neighbour keeping the stream non-trivial
            Image img1, img2;
            GGraph holder1 = env.ImageLeaf(root, 120, 20, new Color32(0, 255, 0, 255), out img1, 48);
            GGraph holder2 = env.ImageLeaf(root, 220, 20, new Color32(0, 0, 255, 255), out img2, 48);
            env.Step(2);

            NGraphics g1 = img1.graphics;
            NGraphics g2 = img2.graphics;

            stream = new InstancedUIStream(InstancedValidationEnv.C(root), default, true, true);
            env.Step(1);

            //--- 1: baseline — both image leaves claimed and instanced --------
            var px = env.Capture();
            env.Check("cf1.baseline image leaves claimed and instanced",
                stream.IsClaimed(g1) && stream.IsClaimed(g2)
                && g1.meshRenderer.forceRenderingOff
                && InstancedValidationEnv.NearRGB(env.Probe(px, holder1, 24, 24), 0, 255, 0));

            //--- 2: runtime ColorFilter on the CLAIMED leaf: the filter's
            //structure poke must recompile the stream and release the leaf
            int runsBefore = stream.runCount;
            int extractsBefore = stream.extractCount;
            var filter = new ColorFilter();
            filter.AdjustSaturation(-1); //grayscale — the game-icon disable idiom
            holder1.filter = filter;
            env.Step(2);
            env.Check("cf2.filter on claimed leaf recompiles and releases it",
                stream.extractCount > extractsBefore
                && !stream.IsClaimed(g1) && !g1.meshRenderer.forceRenderingOff
                && stream.IsClaimed(g2));

            //--- 3: the released leaf renders THROUGH the filter: pure green
            //(0,255,0) grayscales to luma 0.587*255 ≈ 150 on every channel
            px = env.Capture();
            Color32 c3 = env.Probe(px, holder1, 24, 24);
            env.Note($"cf3 probe {InstancedValidationEnv.Fmt(c3)} (want ~150,150,150)");
            env.Check("cf3.grayscale filter visible in pixels",
                InstancedValidationEnv.NearRGB(c3, 150, 150, 150, 12));

            //--- 4: the fallback leaf is a run barrier, same as blend ---------
            env.Check("cf4.filtered leaf splits a run", stream.runCount == runsBefore + 1);

            //--- 5: an unrelated recompile must NOT claim it back (pre-fix,
            //ExtractLeaf had no keyword predicate, so any recompile re-claimed)
            env.Rect(root, 320, 20, 40, 30, Color.yellow);
            env.Step(2);
            px = env.Capture();
            env.Check("cf5.unrelated recompile keeps the filtered leaf native",
                !stream.IsClaimed(g1) && !g1.meshRenderer.forceRenderingOff
                && InstancedValidationEnv.NearRGB(env.Probe(px, holder1, 24, 24), 150, 150, 150, 12));

            //--- 6: filter removal — ToggleKeyword(off) nulls the array slot
            //without shrinking, so the leftover all-null array must re-claim
            holder1.filter = null;
            env.Step(2);
            px = env.Capture();
            env.Check("cf6.filter removal re-claims and restores raw color",
                stream.IsClaimed(g1) && g1.meshRenderer.forceRenderingOff
                && InstancedValidationEnv.NearRGB(env.Probe(px, holder1, 24, 24), 0, 255, 0));

            //--- 7: custom-material half of the predicate. Assigning a material
            //has no notification channel of its own, so poke structure by hand
            customMat = new Material(Shader.Find(ShaderConfig.imageShader));
            g2.material = customMat;
            img2.InvalidateBatchingState();
            env.Step(2);
            env.Check("cf7.custom material rejects the claim at recompile",
                !stream.IsClaimed(g2) && !g2.meshRenderer.forceRenderingOff);

            //--- 8: clearing the custom material re-admits -------------------
            g2.material = null;
            img2.InvalidateBatchingState();
            env.Step(2);
            px = env.Capture();
            env.Check("cf8.custom material cleared re-claims",
                stream.IsClaimed(g2) && g2.meshRenderer.forceRenderingOff
                && InstancedValidationEnv.NearRGB(env.Probe(px, holder2, 24, 24), 0, 0, 255));
        }
        finally
        {
            if (stream != null) stream.Dispose();
            if (customMat != null) Object.Destroy(customMat);
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
