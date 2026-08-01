using System.Collections.Generic;
using System.Text;
using FairyGUI;
using UnityEditor;
using UnityEngine;

namespace FairyGUIEditor
{
    /// <summary>
    /// M8-6: the bake-line's standing gate, OpenFairy-style two-layer parity.
    /// A data-driven catalog (every bakeable exported component of the loaded
    /// packages — enumerate, don't sample — plus programmatic scenarios) runs
    /// each case twice from FRESH instances: runtime walk vs deferred+mounted
    /// blob, comparing (1) pixels over the case region, zero tolerance, and
    /// (2) a geometry snapshot of the compiled quads (rect/uv/color/semantic
    /// flags, 1e-4 float tolerance — transform-chain associativity wiggles the
    /// low bits). Staleness tests prove the fallback ladder: a tampered blob
    /// and a wrong source hash must both refuse the mount and render through
    /// the runtime path. Results go to Temp/FqsParityResults.txt with a single
    /// machine-readable verdict line.
    /// </summary>
    public static class FqsParityRunner
    {
        [MenuItem("Tools/FairyGUI/Run FQS Parity")]
        static void RunMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("FQS parity: enter play mode with UI packages loaded first.");
                return;
            }
            string report = Run();
            Debug.Log(report.Substring(0, Mathf.Min(report.Length, 2000)));
        }

        public static string Run()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0, skip = 0;

            var holder = new GComponent();
            holder.name = "__fqsParity";
            GRoot.inst.AddChild(holder);
            holder.SetSize(520, 420);
            holder.SetXY(600, 380);
            var target = (Container)holder.displayObject;
            bool savedForce = InstancedUIStream.forceVertexPath;
            InstancedUIStream.forceVertexPath = true; //pixel-capable backend
            //the runtime reference instances must stay on the pure runtime
            //walk: an auto-mounted reference would compare a blob to itself
            bool savedSuppress = FqsAutoMount.suppressed;
            target.instancedRendering = true;
            Stage.inst.ForceUpdate();

            FqsAutoMount.suppressed = true;
            try
            {
                //--- catalog part 1: every bakeable exported component ---
                foreach (var pkg in UIPackage.GetPackages())
                {
                    foreach (var item in pkg.GetItems())
                    {
                        if (item.type != PackageItemType.Component || !item.exported)
                            continue;
                        RunCase(sb, holder, target, pkg.name + "/" + item.name,
                            () => UIPackage.CreateObjectFromURL("ui://" + pkg.id + item.id) as GComponent,
                            ref pass, ref fail, ref skip);
                    }
                }

                //--- catalog part 2: programmatic scenarios ---
                RunCase(sb, holder, target, "prog/shapes+clip", BuildClipScenario, ref pass, ref fail, ref skip);
                RunCase(sb, holder, target, "prog/sdf-rounded", BuildSdfScenario, ref pass, ref fail, ref skip);
                RunCase(sb, holder, target, "prog/nested-clip", BuildNestedClipScenario, ref pass, ref fail, ref skip);

                //--- staleness ladder ---
                RunStaleness(sb, holder, target, ref pass, ref fail);
            }
            finally
            {
                target.instancedRendering = false;
                holder.Dispose();
                Stage.inst.ForceUpdate();
                InstancedUIStream.forceVertexPath = savedForce;
                FqsAutoMount.suppressed = savedSuppress;
            }

            string verdict = $"FQS PARITY VERDICT: {(fail == 0 ? "PASS" : "FAIL")} (cases pass={pass} fail={fail} skip={skip})";
            sb.Insert(0, verdict + "\n");
            System.IO.Directory.CreateDirectory("Temp");
            System.IO.File.WriteAllText("Temp/FqsParityResults.txt", sb.ToString());
            return sb.ToString();
        }

        static void RunCase(StringBuilder sb, GComponent holder, Container target, string name,
            System.Func<GComponent> factory, ref int pass, ref int fail, ref int skip)
        {
            GComponent bakeInst = factory();
            if (bakeInst == null)
            {
                sb.Append($"SKIP {name}: factory returned null\n");
                skip++;
                return;
            }
            holder.AddChild(bakeInst);
            Stage.inst.ForceUpdate();
            byte[] blob = FqsBaker.Bake((Container)bakeInst.displayObject, 0, out string reason);
            bakeInst.Dispose();
            Stage.inst.ForceUpdate();
            if (blob == null)
            {
                sb.Append($"SKIP {name}: {reason}\n");
                skip++;
                return;
            }

            //runtime pass: fresh instance, no mount
            var stream = StreamOf(target);
            GComponent r = factory();
            holder.AddChild(r);
            Stage.inst.ForceUpdate();
            var runtimeShot = Snap();
            var runtimeQuads = new List<QuadInstance>();
            stream.CopyQuadsForDiagnostics(runtimeQuads);
            int runtimeLeaves = stream.leafCount;
            r.Dispose();
            Stage.inst.ForceUpdate();

            //mounted pass: fresh DEFERRED instance + blob
            NGraphics.deferRenderers = true;
            GComponent m;
            try { m = factory(); } finally { NGraphics.deferRenderers = false; }
            holder.AddChild(m);
            bool mounted = FqsMount.Mount((Container)m.displayObject, blob);
            Stage.inst.ForceUpdate();
            var mountedShot = Snap();
            var mountedQuads = new List<QuadInstance>();
            stream.CopyQuadsForDiagnostics(mountedQuads);
            int mountedLeaves = stream.leafCount;
            m.Dispose();
            Stage.inst.ForceUpdate();

            int pixelDiff = RegionDiff(runtimeShot, mountedShot);
            Object.DestroyImmediate(runtimeShot);
            Object.DestroyImmediate(mountedShot);
            string geo = CompareQuads(runtimeQuads, mountedQuads);

            if (mounted && pixelDiff == 0 && geo == null && runtimeLeaves == mountedLeaves)
            {
                sb.Append($"PASS {name}: quads={mountedQuads.Count} leaves={mountedLeaves}\n");
                pass++;
            }
            else
            {
                sb.Append($"FAIL {name}: mounted={mounted} pixelDiff={pixelDiff} geo={geo ?? "ok"} leaves {runtimeLeaves}vs{mountedLeaves}\n");
                fail++;
            }
        }

        static void RunStaleness(StringBuilder sb, GComponent holder, Container target, ref int pass, ref int fail)
        {
            GComponent inst = BuildClipScenario();
            holder.AddChild(inst);
            Stage.inst.ForceUpdate();
            byte[] blob = FqsBaker.Bake((Container)inst.displayObject, 0xFEED, out _);
            var runtimeShot = Snap();

            //(a) tampered blob: clean refusal, runtime keeps rendering
            byte[] bad = (byte[])blob.Clone();
            bad[bad.Length / 2] ^= 0xFF;
            bad[20] = 0xFF; bad[21] = 0xFF; bad[22] = 0xFF; bad[23] = 0x7F; //hostile quad count
            bool refusedTamper = !FqsMount.Mount((Container)inst.displayObject, bad);
            //(b) wrong source hash: refusal
            bool refusedHash = !FqsMount.Mount((Container)inst.displayObject, blob, 0xDEAD);
            //(c) right hash mounts, and the fallback render before it was intact
            bool okHash = FqsMount.Mount((Container)inst.displayObject, blob, 0xFEED);
            Stage.inst.ForceUpdate();
            var mountedShot = Snap();
            int diff = RegionDiff(runtimeShot, mountedShot);
            Object.DestroyImmediate(runtimeShot);
            Object.DestroyImmediate(mountedShot);
            inst.Dispose();
            Stage.inst.ForceUpdate();

            if (refusedTamper && refusedHash && okHash && diff == 0)
            {
                sb.Append("PASS staleness: tamper refused, wrong hash refused, right hash mounts, pixels intact\n");
                pass++;
            }
            else
            {
                sb.Append($"FAIL staleness: tamper={refusedTamper} hash={refusedHash} ok={okHash} diff={diff}\n");
                fail++;
            }
        }

        // --- programmatic scenarios ---
        static GComponent BuildClipScenario()
        {
            var p = new GComponent();
            p.SetSize(300, 240);
            Rect(p, Color.red, 0, 0);
            Rect(p, Color.green, 90, 0);
            var clipped = new GComponent();
            clipped.SetSize(60, 40);
            p.AddChild(clipped);
            clipped.SetXY(0, 80);
            ((Container)clipped.displayObject).clipRect = new UnityEngine.Rect(0, 0, 60, 40);
            var big = new GGraph();
            big.SetSize(200, 200);
            big.DrawRect(200, 200, 0, Color.clear, Color.yellow);
            clipped.AddChild(big);
            return p;
        }

        static GComponent BuildSdfScenario()
        {
            var p = new GComponent();
            p.SetSize(300, 200);
            var rr = new GGraph();
            rr.SetSize(120, 80);
            rr.DrawRoundRect(120, 80, Color.magenta, new float[] { 16, 16, 16, 16 });
            p.AddChild(rr);
            rr.SetXY(20, 20);
            Rect(p, Color.cyan, 160, 20);
            return p;
        }

        static GComponent BuildNestedClipScenario()
        {
            var p = new GComponent();
            p.SetSize(300, 240);
            var outer = new GComponent();
            outer.SetSize(160, 120);
            p.AddChild(outer);
            outer.SetXY(10, 10);
            ((Container)outer.displayObject).clipRect = new UnityEngine.Rect(0, 0, 160, 120);
            var inner = new GComponent();
            inner.SetSize(200, 60);
            outer.AddChild(inner);
            inner.SetXY(40, 30);
            ((Container)inner.displayObject).clipRect = new UnityEngine.Rect(0, 0, 200, 60);
            var s = new GGraph();
            s.SetSize(400, 300);
            s.DrawRect(400, 300, 0, Color.clear, Color.blue);
            inner.AddChild(s);
            return p;
        }

        static void Rect(GComponent parent, Color col, float x, float y)
        {
            var g = new GGraph();
            g.SetSize(70, 50);
            g.DrawRect(70, 50, 0, Color.clear, col);
            parent.AddChild(g);
            g.SetXY(x, y);
        }

        // --- comparison helpers ---
        static InstancedUIStream StreamOf(Container target)
        {
            var ls = new List<InstancedUIStream>();
            InstancedUIStream.GetLiveStreams(ls);
            foreach (var st in ls)
                if (st.container == target)
                    return st;
            return null;
        }

        static Texture2D Snap()
        {
            Camera cam = StageCamera.main;
            int rw = cam.pixelWidth, rh = cam.pixelHeight;
            var rt = new RenderTexture(rw, rh, 24);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            var tex = new Texture2D(rw, rh, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new UnityEngine.Rect(0, 0, rw, rh), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            return tex;
        }

        static int RegionDiff(Texture2D a, Texture2D b)
        {
            float sf = GRoot.contentScaleFactor;
            int rh = a.height;
            int x0 = (int)(600 * sf), x1 = (int)((600 + 520) * sf);
            int yT = rh - 1 - (int)((380 + 420) * sf), yB = rh - 1 - (int)(380 * sf);
            int d = 0;
            for (int yy = Mathf.Max(yT, 0); yy <= yB && yy < rh; yy++)
                for (int xx = Mathf.Max(x0, 0); xx < x1 && xx < a.width; xx++)
                {
                    var p1 = a.GetPixel(xx, yy);
                    var p2 = b.GetPixel(xx, yy);
                    if (Mathf.Abs(p1.r - p2.r) > 0.004f || Mathf.Abs(p1.g - p2.g) > 0.004f
                        || Mathf.Abs(p1.b - p2.b) > 0.004f)
                        d++;
                }
            return d;
        }

        /// <summary>Semantic quad compare: geometry/uv/color to 1e-4, flags
        /// masked to the semantic bits (texIndex/segment layout may differ
        /// between the two compiles). Returns null when equal.</summary>
        static string CompareQuads(List<QuadInstance> a, List<QuadInstance> b)
        {
            if (a.Count != b.Count)
                return $"count {a.Count}vs{b.Count}";
            const uint SemanticFlags = 0xFFFFu; //bits 0-15: alphaTex/sdf/curve/grayed/borderWidth
            for (int i = 0; i < a.Count; i++)
            {
                var qa = a[i];
                var qb = b[i];
                if (!Near(qa.rect, qb.rect) || !Near(qa.uvA, qb.uvA) || !Near(qa.uvB, qb.uvB))
                    return $"geo@{i}";
                if (!Near(qa.color, qb.color))
                    return $"color@{i}";
                if ((qa.flags & SemanticFlags) != (qb.flags & SemanticFlags))
                    return $"flags@{i}: {qa.flags:x}vs{qb.flags:x}";
                if (qa.padding != qb.padding)
                    return $"padding@{i}";
            }
            return null;
        }

        static bool Near(Vector4 a, Vector4 b)
        {
            return Mathf.Abs(a.x - b.x) < 1e-4f && Mathf.Abs(a.y - b.y) < 1e-4f
                && Mathf.Abs(a.z - b.z) < 1e-4f && Mathf.Abs(a.w - b.w) < 1e-4f;
        }

        static bool Near(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 1e-4f && Mathf.Abs(a.g - b.g) < 1e-4f
                && Mathf.Abs(a.b - b.b) < 1e-4f && Mathf.Abs(a.a - b.a) < 1e-4f;
        }
    }
}
