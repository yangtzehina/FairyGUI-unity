#if UNITY_2020_1_OR_NEWER
using System.Reflection;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M8-4 acceptance suite (20 checks: g-series 16 + t-series 4), rebuilt from
/// commit 1ee0a88's record. Gear paging / transition playback without a single
/// recompile: the recompile drivers were visibility toggles and interior
/// container transforms, so both became tiers on a valid spliced mount —
/// hide/show rewrites quad ranges in place (the visible setter consults the
/// tier FIRST and skips InvalidateBatchingState when serviced), hidden state
/// re-derives from live flags on a re-splice, showing content ABSENT from the
/// blob invalidates gracefully, and an interior container move services as
/// exact per-leaf tier-2 rewrites with slot-dirty clip re-derivation.
///
/// The t-series is the transition-parity gate: a six-state scripted sequence
/// renders pixel-identical mounted vs runtime at every state, with zero
/// extracts during mounted playback.
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedM84Suite
{
    static readonly FieldInfo sInvalid =
        typeof(FqsMount).GetField("invalid", BindingFlags.NonPublic | BindingFlags.Instance);

    static bool Valid(FqsMount m) { return m != null && sInvalid != null && !(bool)sInvalid.GetValue(m); }

    class Fixture
    {
        public GComponent comp;
        public GGraph rectA, rectB, wide, hiddenAtBake;
        public GComponent group, clipOwner;
        public byte[] blob;
    }

    /// <summary>
    /// Builds the fixture. hiddenAtBake is invisible at bake time on purpose:
    /// showing it later is the "content absent from the blob" case.
    /// </summary>
    static Fixture Build(InstancedValidationEnv env, GComponent parent, float x, float y)
    {
        var f = new Fixture();
        f.comp = new GComponent();
        f.comp.SetSize(220, 150);
        parent.AddChild(f.comp);
        f.comp.SetXY(x, y);
        f.rectA = env.Rect(f.comp, 5, 5, 60, 40, Color.red);
        f.group = new GComponent();
        f.group.SetSize(120, 45);
        f.comp.AddChild(f.group);
        f.group.SetXY(70, 5);
        f.rectB = env.Rect(f.group, 0, 0, 55, 40, Color.green);
        f.clipOwner = new GComponent();
        f.clipOwner.SetSize(90, 40);
        f.comp.AddChild(f.clipOwner);
        f.clipOwner.SetXY(5, 55);
        InstancedValidationEnv.C(f.clipOwner).clipRect = new UnityEngine.Rect(0, 0, 90, 40);
        f.wide = env.Rect(f.clipOwner, 0, 0, 160, 30, Color.yellow);
        f.hiddenAtBake = env.Rect(f.comp, 110, 55, 50, 40, Color.cyan);
        f.hiddenAtBake.visible = false;
        return f;
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        InstancedUIStream stream = null;
        bool prevLog = Debug.unityLogger.logEnabled;
        try
        {
            Fixture f = Build(env, env.root, 20, 20);
            env.Step(2);

            string reason;
            //superset OFF for this fixture: hiddenAtBake must genuinely be
            //ABSENT from the blob — g16 tests the graceful-invalidation ladder,
            //and with M8-7's default the baker would dutifully include it
            bool savedSuperset = FqsBaker.supersetVisibility;
            FqsBaker.supersetVisibility = false;
            try
            {
                f.blob = FqsBaker.Bake(InstancedValidationEnv.C(f.comp), 0x8400UL, out reason, false);
            }
            finally { FqsBaker.supersetVisibility = savedSuperset; }
            if (f.blob == null)
            {
                env.Check("g0.fixture bakes (suite prerequisite)", false);
                env.Note("refused: " + reason);
                return env.Report();
            }

            stream = new InstancedUIStream(InstancedValidationEnv.C(env.root), default, true, true);
            env.Step(1);
            FqsMount.Mount(f.comp, f.blob, 0x8400UL);
            stream.Extract();
            env.Step(1);
            FqsMount fm = FqsMount.Of(f.comp);
            int e0 = stream.extractCount;
            var pxAll = env.Capture();

            //--- g1/g2: leaf hide + show, zero extract ----------------------
            f.rectA.visible = false;
            env.Step(1);
            var px = env.Capture();
            env.Check("g1.leaf hide zeroes its range with zero extract",
                stream.extractCount == e0 && Valid(fm)
                && InstancedValidationEnv.Near(env.Probe(px, f.rectA, 30, 20), InstancedValidationEnv.BG, 6));

            f.rectA.visible = true;
            env.Step(1);
            px = env.Capture();
            env.Check("g2.leaf show requeues tier-2, settled same frame, zero extract",
                stream.extractCount == e0 && Valid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.rectA, 30, 20), 255, 0, 0));

            //--- g3: show restores pixels exactly ---------------------------
            int diff = env.DiffCount(pxAll, px, f.comp, 0, 0, f.comp.width, f.comp.height);
            env.Check($"g3.hide+show round-trip is pixel-exact (diff={diff})", diff == 0);

            //--- g4/g5: container hide + show -------------------------------
            f.group.visible = false;
            env.Step(1);
            px = env.Capture();
            env.Check("g4.container hide zeroes the whole subtree, zero extract",
                stream.extractCount == e0 && Valid(fm)
                && InstancedValidationEnv.Near(env.Probe(px, f.rectB, 25, 20), InstancedValidationEnv.BG, 6));

            f.group.visible = true;
            env.Step(1);
            px = env.Capture();
            env.Check("g5.container show restores the subtree, zero extract",
                stream.extractCount == e0 && Valid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.rectB, 25, 20), 0, 255, 0));

            //--- g6: the show branch does not recompile via batching --------
            //(the setter used to InvalidateBatchingState even when serviced)
            for (int i = 0; i < 3; i++)
            {
                f.rectA.visible = false; env.Step(1);
                f.rectA.visible = true; env.Step(1);
            }
            px = env.Capture();
            env.Check("g6.repeated toggles never recompile (batching invalidation skipped)",
                stream.extractCount == e0 && Valid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.rectA, 30, 20), 255, 0, 0));

            //--- g7: a clipped leaf hides/shows inside its window -----------
            f.wide.visible = false;
            env.Step(1);
            px = env.Capture();
            bool wideGone = InstancedValidationEnv.Near(env.Probe(px, f.clipOwner, 45, 15), InstancedValidationEnv.BG, 6);
            f.wide.visible = true;
            env.Step(1);
            px = env.Capture();
            env.Check("g7.clipped leaf hide/show stays inside its window",
                stream.extractCount == e0 && wideGone
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.clipOwner, 45, 15), 255, 235, 4, 24)
                && InstancedValidationEnv.Near(env.Probe(px, f.clipOwner, 120, 15), InstancedValidationEnv.BG, 6));

            //--- g8/g9: hidden state survives a re-splice (stateless heal) --
            f.rectA.visible = false;
            env.Step(1);
            stream.Extract(); //re-splice would restore the blob's live quads
            env.Step(1);
            px = env.Capture();
            env.Check("g8.hidden state re-derives from live flags on a re-splice",
                Valid(fm)
                && InstancedValidationEnv.Near(env.Probe(px, f.rectA, 30, 20), InstancedValidationEnv.BG, 6));
            f.rectA.visible = true;
            env.Step(1);
            px = env.Capture();
            env.Check("g9.show after a re-splice still works",
                InstancedValidationEnv.NearRGB(env.Probe(px, f.rectA, 30, 20), 255, 0, 0));

            //--- g10: a hidden leaf's content change stays hidden -----------
            f.rectB.visible = false;
            env.Step(1);
            f.rectB.DrawRect(55, 40, 0, Color.clear, Color.magenta);
            env.Step(2);
            px = env.Capture();
            Color32 hiddenPx = env.Probe(px, f.rectB, 25, 20);
            int hiddenExtracts = stream.extractCount - e0;
            env.Check("g10.content change on a hidden leaf does not resurrect pixels",
                Valid(fm) && InstancedValidationEnv.Near(hiddenPx, InstancedValidationEnv.BG, 6));
            //measured nuance, NOT part of the zero-extract guarantee (which
            //covers visibility toggles and interior moves): mutating a HIDDEN
            //leaf costs one recompile — its mesh is never rebuilt while
            //invisible, so the tier-2 rewrite cannot express the change
            env.Note($"content push on a hidden leaf costs {hiddenExtracts} recompile(s) — hide/show itself stays at 0");
            e0 = stream.extractCount;
            f.rectB.visible = true;
            env.Step(2);
            px = env.Capture();
            env.Check("g11.showing it afterwards renders the NEW content",
                InstancedValidationEnv.NearRGB(env.Probe(px, f.rectB, 25, 20), 255, 0, 255, 6));

            //--- g12/g13: interior container transform is a tier now --------
            int eMove = stream.extractCount;
            f.group.SetXY(75, 8);
            env.Step(1);
            px = env.Capture();
            env.Check("g12.interior container move services as tier-2 (zero extract)",
                stream.extractCount == eMove && Valid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.rectB, 25, 20), 255, 0, 255, 6));

            f.clipOwner.SetXY(8, 58);
            env.Step(1);
            px = env.Capture();
            env.Check("g13.moving a clip owner re-derives its window (slot-dirty path)",
                stream.extractCount == eMove && Valid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.clipOwner, 45, 15), 255, 235, 4, 24)
                && InstancedValidationEnv.Near(env.Probe(px, f.clipOwner, 120, 15), InstancedValidationEnv.BG, 6));

            //--- g14: a leaf transform inside the mount stays tier-2 -------
            f.rectA.SetXY(8, 8);
            env.Step(1);
            px = env.Capture();
            env.Check("g14.leaf move inside the mount is tier-2",
                stream.extractCount == eMove && Valid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.rectA, 30, 20), 255, 0, 0));

            //--- g15: alpha inside the mount rides the color tier ----------
            f.rectA.alpha = 0.5f;
            env.Step(1);
            px = env.Capture();
            env.Check("g15.alpha inside the mount rides the color tier",
                stream.extractCount == eMove && Valid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.rectA, 30, 20), 137, 10, 10, 8));
            f.rectA.alpha = 1f;
            env.Step(1);

            //--- g16: showing content ABSENT from the blob invalidates ------
            f.hiddenAtBake.visible = true;
            env.Step(2);
            px = env.Capture();
            env.Check("g16.showing content absent from the blob invalidates gracefully",
                !Valid(fm)
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.hiddenAtBake, 25, 20), 0, 255, 255)
                && InstancedValidationEnv.NearRGB(env.Probe(px, f.rectA, 30, 20), 255, 0, 0));

            //=== t-series: the transition-parity gate =======================
            //a fresh fixture, driven through six states twice — once on the
            //runtime walk, once mounted — comparing pixels at every state
            f.comp.Dispose();
            env.Step(1);
            Fixture t = Build(env, env.root, 20, 20);
            env.Step(2);
            byte[] tblob = FqsBaker.Bake(InstancedValidationEnv.C(t.comp), 0x8401UL, out reason, false);

            void ApplyState(Fixture x, int s)
            {
                switch (s)
                {
                    case 0: break;                                   //initial
                    case 1: x.group.SetXY(78, 10); break;            //interior move
                    case 2: x.rectA.visible = false; break;          //hide
                    case 3: x.rectA.visible = true; break;           //show
                    case 4: x.rectA.alpha = 0.4f; break;             //alpha
                    case 5: x.clipOwner.SetXY(10, 60); break;        //clip move
                }
            }

            var runtimeStates = new Color32[6][];
            stream.Extract();
            env.Step(1);
            for (int s = 0; s < 6; s++)
            {
                ApplyState(t, s);
                env.Step(2);
                runtimeStates[s] = env.Capture();
            }

            //reset and replay mounted
            t.comp.Dispose();
            env.Step(1);
            Fixture t2 = Build(env, env.root, 20, 20);
            env.Step(2);
            bool mounted = FqsMount.Mount(t2.comp, tblob, 0x8401UL);
            stream.Extract();
            env.Step(1);
            FqsMount fm2 = FqsMount.Of(t2.comp);
            int eT = stream.extractCount;
            int worstDiff = 0;
            bool allValid = true;
            for (int s = 0; s < 6; s++)
            {
                ApplyState(t2, s);
                env.Step(2);
                var pxs = env.Capture();
                int dd = env.DiffCount(runtimeStates[s], pxs, t2.comp, 0, 0, t2.comp.width, t2.comp.height);
                if (dd > worstDiff) worstDiff = dd;
                allValid &= Valid(fm2);
            }
            env.Check($"t1.six-state sequence is pixel-identical mounted vs runtime (worst diff={worstDiff})",
                mounted && worstDiff == 0);
            env.Check("t2.zero extracts during mounted playback",
                stream.extractCount == eT);
            env.Check("t3.the mount stays valid through the whole sequence", allValid);

            //back to the initial state: rendering returns to the start frame
            t2.rectA.alpha = 1f;
            t2.group.SetXY(70, 5);
            t2.clipOwner.SetXY(5, 55);
            env.Step(2);
            var pxFinal = env.Capture();
            int backDiff = env.DiffCount(runtimeStates[0], pxFinal, t2.comp, 0, 0, t2.comp.width, t2.comp.height);
            env.Check($"t4.returning to the initial state restores the initial frame (diff={backDiff})",
                backDiff == 0 && stream.extractCount == eT);
        }
        finally
        {
            Debug.unityLogger.logEnabled = prevLog;
            if (stream != null) stream.Dispose();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
