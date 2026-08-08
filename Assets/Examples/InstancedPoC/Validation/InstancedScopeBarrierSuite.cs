#if UNITY_2020_1_OR_NEWER
using FairyGUI;
using UnityEngine;

/// <summary>
/// Container-level scope barriers (26 checks): stencil masks, painting scopes
/// and GoWrappers keep their native renderers, and each must emit an ABSOLUTE
/// sort barrier (infinite box — native breakBatch semantics) so stream content
/// on BOTH sides interleaves correctly by z. The audit gap was that
/// ExtractContainer skipped them without one; adversarial review round 2 then
/// killed the tight-AABB variant (frozen bounds go stale with no invalidation
/// channel: mask tween, wrapped-content animation, filter extend growth).
///
/// Sandwich fixtures share one shape in host-local coordinates: a bottom red
/// quad (10,10,180,140), a scope at (50,40), and a top blue quad
/// (30,25,60,45) overlapping both. Pixel probes are host-local unless the
/// probed object is passed, and stay >=10px from every geometry edge
/// (pitfall 17).
///
/// b1-b14  three basic sandwiches + runtime-mask notify + root-mask guard
/// b15-b17 dual-renderer GoWrapper (block-END order math: _MaxRenderingOrder)
/// b18-b20 fairyBatching host (eraser order via the SetRenderingOrderAll path)
/// b21-b24 adjacent double scopes (middle run straddled by two barriers)
/// b25-b26 reversedMask: barrier present + runtime flip notifies (property)
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedScopeBarrierSuite
{
    //the bottom quad goes in before the scope, the top quad after it
    static GGraph BottomQuad(InstancedValidationEnv env, GComponent host)
    {
        return env.Rect(host, 10, 10, 180, 140, Color.red);
    }

    static GGraph TopQuad(InstancedValidationEnv env, GComponent host)
    {
        return env.Rect(host, 30, 25, 60, 45, Color.blue);
    }

    static GameObject WrapQuad(Transform parent, Color color, Vector3 pos, Vector3 scale, int sortingOrder, ref Material matSlot)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        matSlot = new Material(Shader.Find("Sprites/Default"));
        matSlot.color = color;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = matSlot;
        r.sortingOrder = sortingOrder;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        return go;
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream sA = null, sB = null, sC = null, sD = null,
            sG = null, sH = null, sI = null, sF = null;
        GameObject quadGO = null, wrapRoot = null;
        Material quadMat = null, matY = null, matG = null;
        try
        {
            GComponent root = env.root;

            //--- fixture A: stencil-mask sandwich ---------------------------
            var hostA = new GComponent();
            hostA.SetSize(240, 200);
            root.AddChild(hostA);
            hostA.SetXY(10, 10);
            BottomQuad(env, hostA);
            var mpanel = new GComponent();
            mpanel.SetSize(120, 90);
            hostA.AddChild(mpanel);
            mpanel.SetXY(50, 40);
            var maskA = env.Rect(mpanel, 0, 0, 70, 60, Color.white);
            env.Rect(mpanel, 0, 0, 120, 90, Color.magenta);
            InstancedValidationEnv.C(mpanel).mask = maskA.displayObject;
            GGraph blueA = TopQuad(env, hostA);
            env.Step(2);
            sA = new InstancedUIStream(InstancedValidationEnv.C(hostA), default, true, true);
            env.Step(2);

            var px = env.Capture();
            Color32 pAll = env.Probe(px, blueA, 35, 30);
            env.Check($"b1.masked sandwich: top quad above masked content {InstancedValidationEnv.Fmt(pAll)}",
                InstancedValidationEnv.NearRGB(pAll, 0, 0, 255));
            Color32 pMid = env.Probe(px, mpanel, 55, 45);
            env.Check($"b2.masked content above the bottom quad {InstancedValidationEnv.Fmt(pMid)}",
                InstancedValidationEnv.NearRGB(pMid, 255, 0, 255));
            Color32 pClip = env.Probe(px, mpanel, 95, 75);
            env.Check($"b3.stencil still clips outside the mask window {InstancedValidationEnv.Fmt(pClip)}",
                InstancedValidationEnv.NearRGB(pClip, 255, 0, 0));
            env.Check($"b4.mask scope closes a run (runs={sA.runCount} masked={sA.lastMaskedSubtrees})",
                sA.runCount == 2 && sA.lastMaskedSubtrees == 1);

            //--- fixture B: painting-scope (ColorFilter) sandwich -----------
            var hostB = new GComponent();
            hostB.SetSize(240, 200);
            root.AddChild(hostB);
            hostB.SetXY(320, 10);
            BottomQuad(env, hostB);
            var pcomp = new GComponent();
            pcomp.SetSize(120, 90);
            hostB.AddChild(pcomp);
            pcomp.SetXY(50, 40);
            env.Rect(pcomp, 0, 0, 120, 90, Color.green);
            pcomp.filter = new ColorFilter(); //painting scope from birth
            GGraph blueB = TopQuad(env, hostB);
            env.Step(2);
            sB = new InstancedUIStream(InstancedValidationEnv.C(hostB), default, true, true);
            env.Step(2); //the painting capture pipeline settles one frame later

            px = env.Capture();
            pAll = env.Probe(px, blueB, 35, 30);
            env.Check($"b5.painting sandwich: top quad above the blit {InstancedValidationEnv.Fmt(pAll)}",
                InstancedValidationEnv.NearRGB(pAll, 0, 0, 255));
            pMid = env.Probe(px, pcomp, 55, 45);
            env.Check($"b6.filtered content above the bottom quad {InstancedValidationEnv.Fmt(pMid)}",
                InstancedValidationEnv.NearRGB(pMid, 0, 255, 0, 10));
            env.Check($"b7.painting scope closes a run (runs={sB.runCount} masked={sB.lastMaskedSubtrees})",
                sB.runCount == 2 && sB.lastMaskedSubtrees == 1);

            //--- fixture C: GoWrapper sandwich ------------------------------
            var hostC = new GComponent();
            hostC.SetSize(240, 200);
            root.AddChild(hostC);
            hostC.SetXY(10, 225);
            BottomQuad(env, hostC);
            quadGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadMat = new Material(Shader.Find("Sprites/Default"));
            quadMat.color = Color.yellow;
            quadGO.GetComponent<MeshRenderer>().sharedMaterial = quadMat;
            var wrapper = new GoWrapper(quadGO);
            //cover scope-local (0,0)-(120,60): quad is a centered unit square
            quadGO.transform.localPosition = new Vector3(60, -30, 0);
            quadGO.transform.localScale = new Vector3(120, 60, 1);
            var holder = new GGraph();
            hostC.AddChild(holder);
            holder.SetXY(50, 40);
            holder.SetNativeObject(wrapper);
            GGraph blueC = TopQuad(env, hostC);
            env.Step(2);
            sC = new InstancedUIStream(InstancedValidationEnv.C(hostC), default, true, true);
            env.Step(2);

            px = env.Capture();
            pMid = env.Probe(px, holder, 55, 45);
            env.Check($"b8.GoWrapper content above the bottom quad {InstancedValidationEnv.Fmt(pMid)}",
                InstancedValidationEnv.NearRGB(pMid, 255, 235, 4, 20));
            pAll = env.Probe(px, blueC, 35, 30);
            env.Check($"b9.top quad above the GoWrapper {InstancedValidationEnv.Fmt(pAll)}",
                InstancedValidationEnv.NearRGB(pAll, 0, 0, 255));
            env.Check($"b10.GoWrapper closes a run (runs={sC.runCount} masked={sC.lastMaskedSubtrees})",
                sC.runCount == 2 && sC.lastMaskedSubtrees == 1);

            //--- fixture D: mask set at runtime on a clip container ---------
            //regression for the notify gap: UpdateBatchingFlags only fires
            //InvalidateBatchingState when BatchingRoot FLIPS, so adding a mask
            //to a container that already clips must notify the stream directly
            var hostD = new GComponent();
            hostD.SetSize(240, 200);
            root.AddChild(hostD);
            hostD.SetXY(320, 225);
            BottomQuad(env, hostD);
            var dpanel = new GComponent();
            dpanel.SetSize(120, 90);
            hostD.AddChild(dpanel);
            dpanel.SetXY(50, 40);
            InstancedValidationEnv.C(dpanel).clipRect = new UnityEngine.Rect(0, 0, 120, 90);
            var maskD = env.Rect(dpanel, 0, 0, 70, 60, Color.white);
            env.Rect(dpanel, 0, 0, 120, 90, new Color(1f, 0.5f, 0f));
            TopQuad(env, hostD);
            env.Step(2);
            sD = new InstancedUIStream(InstancedValidationEnv.C(hostD), default, true, true);
            env.Step(2);
            int ecBefore = sD.extractCount;
            InstancedValidationEnv.C(dpanel).mask = maskD.displayObject;
            env.Step(2);
            env.Check($"b11.runtime mask on a clip container recompiles (extract {ecBefore}->{sD.extractCount}, runs={sD.runCount})",
                sD.extractCount > ecBefore && sD.runCount == 2 && sD.lastMaskedSubtrees == 1);
            px = env.Capture();
            pClip = env.Probe(px, dpanel, 95, 75);
            pMid = env.Probe(px, dpanel, 55, 45);
            env.Check($"b12.the new mask clips and interleaves {InstancedValidationEnv.Fmt(pClip)}/{InstancedValidationEnv.Fmt(pMid)}",
                InstancedValidationEnv.NearRGB(pClip, 255, 0, 0)
                && InstancedValidationEnv.NearRGB(pMid, 255, 128, 0, 12));

            //--- root-mask guard: a stream root with a mask claims nothing --
            var rootMask = env.Rect(hostA, 0, 0, 240, 200, Color.white);
            InstancedValidationEnv.C(hostA).mask = rootMask.displayObject;
            env.Step(2);
            px = env.Capture();
            pMid = env.Probe(px, mpanel, 55, 45);
            env.Check($"b13.root mask suspends claiming, native renders on (claimed={sA.claimedLeafCount}) {InstancedValidationEnv.Fmt(pMid)}",
                sA.claimedLeafCount == 0 && InstancedValidationEnv.NearRGB(pMid, 255, 0, 255));
            InstancedValidationEnv.C(hostA).mask = null;
            rootMask.visible = false;
            env.Step(2);
            env.Check($"b14.root mask removed re-claims (claimed={sA.claimedLeafCount})",
                sA.claimedLeafCount > 0);

            //free A/B/C so the review-round-2 fixtures can reuse their screen
            //regions (captures probe absolute pixels; dead hosts must not draw)
            sA.Dispose(); sA = null;
            sB.Dispose(); sB = null;
            sC.Dispose(); sC = null;
            hostA.Dispose();
            hostB.Dispose();
            hostC.Dispose(); //disposes the GoWrapper, which destroys quadGO
            env.Step(2);

            //--- fixture G: dual-renderer GoWrapper (block-END order math) --
            //two wrapped quads on DIFFERENT native sortingOrders consume two
            //stream-side order slots; the barrier order must be the LAST slot
            //(_MaxRenderingOrder) — were it the first, the next run's floor
            //would land inside the block and the top quad could sink under the
            //overlay renderer
            var hostG = new GComponent();
            hostG.SetSize(240, 200);
            root.AddChild(hostG);
            hostG.SetXY(320, 10);
            BottomQuad(env, hostG);
            wrapRoot = new GameObject("dualwrap");
            WrapQuad(wrapRoot.transform, Color.yellow, new Vector3(60, -30, 0), new Vector3(120, 60, 1), 0, ref matY);
            WrapQuad(wrapRoot.transform, Color.green, new Vector3(35, -15, 0), new Vector3(70, 30, 1), 1, ref matG);
            var wrapperG = new GoWrapper(wrapRoot);
            var holderG = new GGraph();
            hostG.AddChild(holderG);
            holderG.SetXY(50, 40);
            holderG.SetNativeObject(wrapperG);
            TopQuad(env, hostG);
            env.Step(2);
            sG = new InstancedUIStream(InstancedValidationEnv.C(hostG), default, true, true);
            env.Step(2);

            px = env.Capture();
            //green overlay covers host (50,40)-(120,70); outside blue (x>90)
            pMid = env.Probe(px, hostG, 105, 55);
            env.Check($"b15.overlay renderer draws above the base renderer {InstancedValidationEnv.Fmt(pMid)}",
                InstancedValidationEnv.NearRGB(pMid, 0, 255, 0, 20));
            //blue overlaps BOTH wrapped layers at host (50..90, 40..70)
            pAll = env.Probe(px, hostG, 65, 52);
            env.Check($"b16.top quad above both wrapped renderers {InstancedValidationEnv.Fmt(pAll)}",
                InstancedValidationEnv.NearRGB(pAll, 0, 0, 255));
            env.Check($"b17.dual-renderer GoWrapper closes one run (runs={sG.runCount} masked={sG.lastMaskedSubtrees})",
                sG.runCount == 2 && sG.lastMaskedSubtrees == 1);

            //--- fixture H: fairyBatching host + mask sandwich --------------
            //under a batching root the stencil eraser gets its order through
            //SetRenderingOrderAll (batch-element replay), not the plain child
            //walk — the barrier's RunBarrier.order must hold there too
            var hostH = new GComponent();
            hostH.SetSize(240, 200);
            root.AddChild(hostH);
            hostH.SetXY(10, 10);
            InstancedValidationEnv.C(hostH).fairyBatching = true;
            BottomQuad(env, hostH);
            var hpanel = new GComponent();
            hpanel.SetSize(120, 90);
            hostH.AddChild(hpanel);
            hpanel.SetXY(50, 40);
            var maskH = env.Rect(hpanel, 0, 0, 70, 60, Color.white);
            env.Rect(hpanel, 0, 0, 120, 90, Color.magenta);
            InstancedValidationEnv.C(hpanel).mask = maskH.displayObject;
            TopQuad(env, hostH);
            env.Step(2);
            sH = new InstancedUIStream(InstancedValidationEnv.C(hostH), default, true, true);
            env.Step(2);

            px = env.Capture();
            pAll = env.Probe(px, hostH, 65, 52);
            env.Check($"b18.fairyBatching host: top quad above masked content {InstancedValidationEnv.Fmt(pAll)}",
                InstancedValidationEnv.NearRGB(pAll, 0, 0, 255));
            pMid = env.Probe(px, hostH, 105, 85);
            env.Check($"b19.fairyBatching host: masked content above the bottom quad {InstancedValidationEnv.Fmt(pMid)}",
                InstancedValidationEnv.NearRGB(pMid, 255, 0, 255));
            env.Check($"b20.fairyBatching host: mask scope closes a run (runs={sH.runCount} masked={sH.lastMaskedSubtrees})",
                sH.runCount == 2 && sH.lastMaskedSubtrees == 1);

            //--- fixture I: adjacent double scopes --------------------------
            //red < masked(magenta) < white < painting(green) < blue: the white
            //quad's run is straddled by two barriers — its segment order must
            //land above the eraser and below the blit
            var hostI = new GComponent();
            hostI.SetSize(240, 200);
            root.AddChild(hostI);
            hostI.SetXY(10, 225);
            BottomQuad(env, hostI);
            var ipanel = new GComponent();
            ipanel.SetSize(120, 90);
            hostI.AddChild(ipanel);
            ipanel.SetXY(50, 40);
            var maskI = env.Rect(ipanel, 0, 0, 70, 60, Color.white);
            env.Rect(ipanel, 0, 0, 120, 90, Color.magenta);
            InstancedValidationEnv.C(ipanel).mask = maskI.displayObject;
            env.Rect(hostI, 25, 35, 115, 100, Color.white); //middle claimed quad
            var ppanelI = new GComponent();
            ppanelI.SetSize(100, 60);
            hostI.AddChild(ppanelI);
            ppanelI.SetXY(95, 105);
            env.Rect(ppanelI, 0, 0, 100, 60, Color.green);
            ppanelI.filter = new ColorFilter();
            TopQuad(env, hostI);
            env.Step(2);
            sI = new InstancedUIStream(InstancedValidationEnv.C(hostI), default, true, true);
            env.Step(2);

            px = env.Capture();
            pMid = env.Probe(px, hostI, 105, 60);
            env.Check($"b21.middle quad above the masked scope {InstancedValidationEnv.Fmt(pMid)}",
                InstancedValidationEnv.NearRGB(pMid, 255, 255, 255));
            Color32 pTop = env.Probe(px, hostI, 110, 120);
            env.Check($"b22.painting blit above the middle quad {InstancedValidationEnv.Fmt(pTop)}",
                InstancedValidationEnv.NearRGB(pTop, 0, 255, 0, 10));
            pAll = env.Probe(px, hostI, 65, 52);
            env.Check($"b23.top quad above everything {InstancedValidationEnv.Fmt(pAll)}",
                InstancedValidationEnv.NearRGB(pAll, 0, 0, 255));
            env.Check($"b24.two scopes, three runs (runs={sI.runCount} masked={sI.lastMaskedSubtrees})",
                sI.runCount == 3 && sI.lastMaskedSubtrees == 2);

            //--- fixture F: reversedMask barrier + runtime flip notify ------
            //created after the LAST capture: its region overlaps A/C/D areas.
            //reversedMask is now a notifying property (review round 2: bare
            //field flips reached the native pass but never the stream)
            var hostF = new GComponent();
            hostF.SetSize(200, 150);
            root.AddChild(hostF);
            hostF.SetXY(200, 150);
            var fpanel = new GComponent();
            fpanel.SetSize(100, 70);
            hostF.AddChild(fpanel);
            var maskF = env.Rect(fpanel, 0, 0, 40, 30, Color.white);
            env.Rect(fpanel, 0, 0, 100, 70, Color.cyan);
            InstancedValidationEnv.C(fpanel).mask = maskF.displayObject;
            env.Rect(hostF, 120, 90, 50, 40, Color.gray);
            env.Step(2);
            sF = new InstancedUIStream(InstancedValidationEnv.C(hostF), default, true, true);
            env.Step(1);
            env.Check($"b25.mask scope emits a barrier before the flip (runs={sF.runCount} masked={sF.lastMaskedSubtrees})",
                sF.runCount == 2 && sF.lastMaskedSubtrees == 1);
            int ecFlip = sF.extractCount;
            InstancedValidationEnv.C(fpanel).reversedMask = true;
            env.Step(2);
            env.Check($"b26.runtime reversedMask flip recompiles (extract {ecFlip}->{sF.extractCount}, runs={sF.runCount})",
                sF.extractCount > ecFlip && sF.runCount == 2 && sF.lastMaskedSubtrees == 1);
        }
        finally
        {
            if (sF != null) sF.Dispose();
            if (sI != null) sI.Dispose();
            if (sH != null) sH.Dispose();
            if (sG != null) sG.Dispose();
            if (sD != null) sD.Dispose();
            if (sC != null) sC.Dispose();
            if (sB != null) sB.Dispose();
            if (sA != null) sA.Dispose();
            env.Dispose();
            //normally destroyed by their GoWrapper's Dispose; kept as a leak
            //guard for exceptions between CreatePrimitive and SetNativeObject
            //(repeat Destroy on a dead object is a no-op)
            if (quadGO != null) Object.Destroy(quadGO);
            if (wrapRoot != null) Object.Destroy(wrapRoot);
            if (quadMat != null) Object.Destroy(quadMat);
            if (matY != null) Object.Destroy(matY);
            if (matG != null) Object.Destroy(matG);
        }
        return env.Report();
    }
}
#endif
