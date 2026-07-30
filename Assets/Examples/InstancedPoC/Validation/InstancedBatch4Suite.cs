#if UNITY_2020_1_OR_NEWER
using FairyGUI;
using UnityEngine;

/// <summary>
/// Batch 4 productization suite (12 checks), rebuilt from commit 3a22896's
/// Validation record: GComponent.instancedRendering is the whole API (targeting
/// the clip owner when one exists), the Stage auto-drives every live stream
/// (no manual Render calls anywhere), auto-driven pixels match native
/// rendering, interior moves/scrolls ride the slot path, toggling off restores
/// native rendering, and component dispose tears the stream down. Returns a
/// "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedBatch4Suite
{
    /// <summary>
    /// A code-built scrollable component: replicates GComponent.SetupScroll's
    /// container split (content container under the mask) without package data.
    /// Calling ScrollPane's ctor on an UNSPLIT component would parent the root
    /// container under its own child — a display-tree cycle.
    /// </summary>
    class ScrollHost : GComponent
    {
        public ScrollPane EnableScroll()
        {
            if (container == rootContainer)
            {
                var content = new Container();
                rootContainer.AddChild(content);
                container = content;
            }
            var sp = new ScrollPane(this);
            //scrollPane's setter is private; UpdateBounds needs it to push content size
            typeof(GComponent).GetProperty("scrollPane").SetValue(this, sp);
            //a bare ScrollPane defaults to Horizontal (enum 0): enable vertical
            typeof(ScrollPane).GetField("_scrollType",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(sp, ScrollType.Vertical);
            return sp;
        }
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        GComponent comp2 = null;
        try
        {
            env.WarmGlyphs("AutoDrive");
            int streams0 = InstancedValidationEnv.LiveStreamCount();

            GComponent comp = env.root;
            GGraph rectA = env.Rect(comp, 20, 20, 80, 50, Color.red);
            GTextField tf = env.Text(comp, 20, 90, 240, 40, "Auto");
            GComponent mover = new GComponent();
            mover.SetSize(140, 80);
            comp.AddChild(mover);
            mover.SetXY(20, 150);
            GGraph rectM = env.Rect(mover, 10, 10, 60, 40, Color.white);

            var scrollHost = new ScrollHost();
            comp2 = scrollHost;
            GRoot.inst.AddChild(scrollHost);
            scrollHost.SetXY(400, 20);
            ScrollPane sp = scrollHost.EnableScroll();
            scrollHost.SetSize(140, 100); //size change installs the mask clipRect
            env.Rect(scrollHost, 10, 10, 100, 110, Color.cyan);
            env.Rect(scrollHost, 10, 130, 100, 90, Color.yellow);
            env.Step(2);

            NGraphics gA = InstancedValidationEnv.G(rectA);
            var pxNative = env.Capture();

            //--- a1: the toggle is the whole API ----------------------------
            comp.instancedRendering = true;
            env.Step(1);
            InstancedUIStream s = InstancedValidationEnv.StreamOf(comp);
            env.Check("a1.instancedRendering toggle creates a driven stream",
                comp.instancedRendering && s != null
                && InstancedValidationEnv.LiveStreamCount() == streams0 + 1
                && s.claimedLeafCount > 0 && s.backendName == "vertex-stream");

            //--- a2: auto-driven pixels match native ------------------------
            var px = env.Capture();
            double mean, badPct;
            env.DiffStats(pxNative, px, comp, 2, 2, comp.width - 2, comp.height - 2, out mean, out badPct);
            env.Check($"a2.auto-drive parity with native pixels (mean={mean:F3} bad={badPct:F3}%)",
                mean < 1.5 && badPct < 0.5);

            //--- a3: content updates flow with no manual calls --------------
            var before = px;
            tf.text = "Drive";
            env.Step(2);
            px = env.Capture();
            env.Check("a3.text update lands through the auto-driven flush",
                env.DiffCount(before, px, tf, 2, 4, 120, 32) > 20);

            //--- a4: interior move rides the slot path ----------------------
            mover.SetXY(30, 160);
            env.Step(1);
            int e1 = s.extractCount;
            mover.SetXY(50, 170);
            env.Step(1);
            px = env.Capture();
            env.Check("a4.interior move promotes once then rides the slot",
                s.extractCount == e1 && s.slotCount >= 1
                && InstancedValidationEnv.NearRGB(env.Probe(px, rectM, 30, 20), 255, 255, 255));

            //--- a5: the wrapper targets the clip owner ---------------------
            comp2.instancedRendering = true;
            env.Step(1);
            InstancedUIStream s2 = InstancedValidationEnv.StreamOf(comp2);
            env.Check("a5.scroll view: stream sits on the clip owner (mask = external window)",
                comp2.instancedRendering && s2 != null && s2.container.clipRect != null);

            //--- a6/a7: interior scrolling promotes then rides a slot -------
            px = env.Capture();
            bool cyanBefore = InstancedValidationEnv.NearRGB(env.Probe(px, comp2, 60, 50), 0, 255, 255, 6);
            sp.posY = 40;
            env.Step(1);
            int e2 = s2.extractCount;
            env.Check("a6.first scroll promotes the content container",
                s2.slotCount >= 1);
            sp.posY = 80;
            env.Step(1);
            px = env.Capture();
            env.Check("a7.later scrolls are matrix writes and pixels track",
                s2.extractCount == e2 && cyanBefore
                && InstancedValidationEnv.NearRGB(env.Probe(px, comp2, 60, 50), 255, 235, 4, 24));

            //--- a8: the mask stays the stream's external window ------------
            //below the mask the env root's backdrop shows through — content
            //(band2 would cover this point unclipped) must not paint there
            env.Check("a8.content clips at the mask window",
                InstancedValidationEnv.Near(
                    env.ProbeStage(px, comp2.LocalToGlobal(new Vector2(60, 112))),
                    InstancedValidationEnv.BG, 6));

            //--- a9: toggle off restores native rendering -------------------
            var beforeOff = env.Capture();
            comp.instancedRendering = false;
            env.Step(1);
            px = env.Capture();
            env.DiffStats(beforeOff, px, comp, 2, 2, comp.width - 2, comp.height - 2, out mean, out badPct);
            env.Check($"a9.toggle-off restores native (mean={mean:F3} bad={badPct:F3}%)",
                !comp.instancedRendering && !gA.meshRenderer.forceRenderingOff
                && InstancedValidationEnv.SegmentIds(InstancedValidationEnv.C(comp)).Count == 0
                && mean < 1.5 && badPct < 0.5);

            //--- a10: re-toggle is robust -----------------------------------
            comp.instancedRendering = true;
            env.Step(1);
            s = InstancedValidationEnv.StreamOf(comp);
            var afterOn = env.Capture();
            env.DiffStats(px, afterOn, comp, 2, 2, comp.width - 2, comp.height - 2, out mean, out badPct);
            env.Check($"a10.re-toggle re-takes over cleanly (mean={mean:F3})",
                s != null && s.claimedLeafCount > 0 && mean < 1.5 && badPct < 0.5);

            //--- a11: manual Render() calls are harmless no-ops -------------
            s.Render();
            s.Render();
            px = env.Capture();
            env.DiffStats(afterOn, px, comp, 2, 2, comp.width - 2, comp.height - 2, out mean, out badPct);
            env.Check("a11.manual Render() is a harmless no-op", mean < 0.05);

            //--- a12: component dispose tears the stream down ---------------
            comp2.Dispose();
            env.Step(1);
            bool exception = false;
            try { env.Step(2); }
            catch { exception = true; }
            env.Check("a12.dispose tears down the registered stream",
                !exception && InstancedValidationEnv.LiveStreamCount() == streams0 + 1);
        }
        finally
        {
            if (comp2 != null && !comp2.isDisposed)
                comp2.Dispose();
            env.Dispose(); //disposes comp (env.root) and with it the last stream
        }
        return env.Report();
    }
}
#endif
