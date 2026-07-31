#if UNITY_2020_1_OR_NEWER
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M8-5 acceptance suite (14 checks), rebuilt from commit a679096's record:
/// renderless leaves. Inside the NGraphics.deferRenderers scope, leaves skip
/// the MeshFilter/MeshRenderer/Mesh triple and all mesh building — GameObjects
/// and transforms stay, so tier-2 matrices and FairyGUI hit testing are
/// untouched. The stream builds a leaf's mesh on demand; the RENDERER
/// materializes only on release (_ClearInstancedOwner -> _EnsureNative) or
/// when a deferred leaf ends up unclaimed (2-frame grace, then it renders
/// natively). Pixel gate: renderless + claimed renders identically to the
/// ordinary runtime reference.
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedM85Suite
{
    static bool HasRenderer(GObject o)
    {
        var g = InstancedValidationEnv.G(o);
        return g.meshRenderer != null;
    }

    static bool HasMesh(GObject o)
    {
        return InstancedValidationEnv.G(o).mesh != null;
    }

    /// <summary>Builds shapes inside the defer scope; returns the holder component.</summary>
    static GComponent BuildDeferred(InstancedValidationEnv env, GComponent parent,
        float x, float y, out GGraph a, out GGraph b, out GGraph c)
    {
        var comp = new GComponent();
        comp.SetSize(220, 120);
        parent.AddChild(comp);
        comp.SetXY(x, y);
        bool prev = NGraphics.deferRenderers;
        NGraphics.deferRenderers = true;
        try
        {
            a = env.Rect(comp, 5, 5, 60, 40, Color.red);
            b = env.Rect(comp, 70, 5, 60, 40, Color.green);
            c = env.Rect(comp, 5, 55, 60, 40, Color.blue);
        }
        finally { NGraphics.deferRenderers = prev; }
        return comp;
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        bool prevDefer = NGraphics.deferRenderers;
        try
        {
            //--- reference: the same content built normally ------------------
            var refComp = new GComponent();
            refComp.SetSize(220, 120);
            env.root.AddChild(refComp);
            refComp.SetXY(20, 20);
            env.Rect(refComp, 5, 5, 60, 40, Color.red);
            env.Rect(refComp, 70, 5, 60, 40, Color.green);
            env.Rect(refComp, 5, 55, 60, 40, Color.blue);
            env.Step(2);
            var pxReference = env.Capture();
            refComp.visible = false;
            env.Step(1);

            //--- r1/r2: the defer scope --------------------------------------
            GGraph da, db, dc;
            GComponent comp = BuildDeferred(env, env.root, 20, 20, out da, out db, out dc);
            env.Check("r1.deferred leaves are created without renderer or mesh",
                !HasRenderer(da) && !HasRenderer(db) && !HasRenderer(dc)
                && !HasMesh(da) && !HasMesh(db) && !HasMesh(dc));

            var outside = env.Rect(env.root, 400, 250, 20, 20, Color.gray);
            env.Check("r2.the scope is exited: later leaves are ordinary",
                HasRenderer(outside) && !NGraphics.deferRenderers);

            //--- r3: the stream claims them and builds meshes on demand ------
            stream = new InstancedUIStream(InstancedValidationEnv.C(env.root), default, true, true);
            env.Step(1);
            env.Check("r3.deferred leaves are claimed and their meshes built on demand",
                stream.IsClaimed(InstancedValidationEnv.G(da))
                && stream.IsClaimed(InstancedValidationEnv.G(db))
                && HasMesh(da) && HasMesh(db));

            //--- r4: THE PIXEL GATE ------------------------------------------
            var px = env.Capture();
            int diff = env.DiffCount(pxReference, px, comp, 0, 0, comp.width, comp.height);
            env.Check($"r4.renderless+claimed renders identically to the reference (diff={diff})",
                diff == 0);

            //--- r5: still no renderers while claimed ------------------------
            env.Check("r5.no renderer materialized while claimed",
                !HasRenderer(da) && !HasRenderer(db) && !HasRenderer(dc));

            //--- r6/r7: tier-2 and the color tier with no renderer -----------
            int e0 = stream.extractCount;
            var beforeChurn = px;
            da.DrawRect(60, 40, 0, Color.clear, Color.magenta);
            env.Step(2);
            px = env.Capture();
            env.Check("r6.content tier-2 rewrite works with no renderer",
                !HasRenderer(da)
                && env.DiffCount(beforeChurn, px, da, 0, 0, 60, 40) > 100
                && InstancedValidationEnv.NearRGB(env.Probe(px, da, 30, 20), 255, 0, 255, 6));

            db.alpha = 0.5f;
            env.Step(1);
            px = env.Capture();
            env.Check("r7.color tier works with no renderer, zero recompile",
                stream.extractCount == e0 && !HasRenderer(db)
                && InstancedValidationEnv.NearRGB(env.Probe(px, db, 30, 20), 10, 137, 10, 8));
            db.alpha = 1f;
            env.Step(1);

            //--- r8: a transform on a deferred leaf still works --------------
            dc.SetXY(8, 58);
            env.Step(1);
            px = env.Capture();
            env.Check("r8.transform on a deferred leaf rides tier-2",
                !HasRenderer(dc)
                && InstancedValidationEnv.NearRGB(env.Probe(px, dc, 30, 20), 0, 0, 255));

            //--- r9: hit testing lands on deferred content -------------------
            //(GameObjects and transforms stay: interaction semantics survive)
            Vector2 stagePt = dc.LocalToGlobal(new Vector2(30, 20));
            GObject hit = GRoot.inst.touchTarget; //not driven here; use the display tree
            DisplayObject dispHit = Stage.inst.HitTest(stagePt, true);
            env.Check($"r9.hit test lands on deferred content [{(dispHit != null ? dispHit.gameObject.name : "null")}]",
                dispHit == dc.displayObject);

            //--- r10/r11: release materializes the renderer ------------------
            int soExpect = InstancedValidationEnv.G(da).renderingOrder;
            stream.Dispose();
            stream = null;
            env.Step(2);
            env.Check("r10.release materializes renderers (_EnsureNative)",
                HasRenderer(da) && HasRenderer(db) && HasRenderer(dc)
                && !InstancedValidationEnv.G(da).meshRenderer.forceRenderingOff);
            px = env.Capture();
            env.Check("r11.materialized leaves render natively with the right order",
                InstancedValidationEnv.G(da).meshRenderer.sortingOrder == soExpect
                && InstancedValidationEnv.NearRGB(env.Probe(px, da, 30, 20), 255, 0, 255, 6)
                && InstancedValidationEnv.NearRGB(env.Probe(px, dc, 30, 20), 0, 0, 255));

            //--- r12: deferred content with no stream at all -----------------
            //nothing claims it: after the 2-frame grace it must render natively
            GGraph ua, ub, uc;
            GComponent unclaimed = BuildDeferred(env, env.root, 20, 150, out ua, out ub, out uc);
            bool bornRenderless = !HasRenderer(ua);
            env.Step(4);
            px = env.Capture();
            env.Check("r12.unclaimed deferred content materializes and renders natively",
                bornRenderless && HasRenderer(ua)
                && InstancedValidationEnv.NearRGB(env.Probe(px, ua, 30, 20), 255, 0, 0));

            //--- r13: deferred content re-enters a NEW stream ----------------
            GGraph ra, rb, rc;
            GComponent second = BuildDeferred(env, env.root, 250, 150, out ra, out rb, out rc);
            bool secondRenderless = !HasRenderer(ra);
            stream = new InstancedUIStream(InstancedValidationEnv.C(env.root), default, true, true);
            env.Step(1);
            px = env.Capture();
            env.Check("r13.a new stream claims deferred content before the grace expires",
                secondRenderless && stream.IsClaimed(InstancedValidationEnv.G(ra))
                && !HasRenderer(ra)
                && InstancedValidationEnv.NearRGB(env.Probe(px, ra, 30, 20), 255, 0, 0));

            //--- r14: teardown of renderless content is clean ----------------
            bool threw = false;
            try
            {
                second.Dispose();
                env.Step(2);
                comp.Dispose();
                env.Step(2);
            }
            catch { threw = true; }
            px = env.Capture();
            env.Check("r14.disposing renderless content is clean",
                !threw && InstancedValidationEnv.NearRGB(env.Probe(px, ua, 30, 20), 255, 0, 0));
        }
        finally
        {
            NGraphics.deferRenderers = prevDefer;
            if (stream != null) stream.Dispose();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
