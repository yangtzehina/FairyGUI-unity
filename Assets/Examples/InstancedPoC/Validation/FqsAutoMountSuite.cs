#if UNITY_2020_1_OR_NEWER
using System.Collections.Generic;
using System.Reflection;
using FairyGUI;
using UnityEngine;

/// <summary>
/// Auto-mount suite (M8 follow-on, 12 checks): FqsAutoMount turns the manual
/// two-line bake integration into a per-project switch — package-created
/// components look up blobs through a pluggable provider at construction,
/// mount behind the source-hash gate, and blobs at/over the leaf threshold
/// construct inside an M8-5 defer-renderers scope. Covers the disabled and
/// suppressed paths, the provider cache and locked-in refusals, the defer
/// threshold, nested-scope safety, pixel parity of an auto-mounted instance
/// against its runtime-walk twin, and the staleness refusal with runtime-walk
/// fallback. Uses the real "Basics" example package (auto-mount only engages
/// on package-created components).
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class FqsAutoMountSuite
{
    static readonly FieldInfo sSpliced =
        typeof(FqsMount).GetField("spliced", BindingFlags.NonPublic | BindingFlags.Instance);

    static bool Spliced(FqsMount m) { return m != null && sSpliced != null && (bool)sSpliced.GetValue(m); }

    static void CollectLeaves(Container c, List<NGraphics> outList)
    {
        int n = c.numChildren;
        for (int i = 0; i < n; i++)
        {
            DisplayObject ch = c.GetChildAt(i);
            if (ch is Container cc)
                CollectLeaves(cc, outList);
            else if (ch.graphics != null && ch.graphics.texture != null)
                outList.Add(ch.graphics);
        }
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        bool savedEnabled = FqsAutoMount.enabled;
        int savedThreshold = FqsAutoMount.deferLeafThreshold;
        var savedProvider = FqsAutoMount.blobProvider;
        bool savedSuppressed = FqsAutoMount.suppressed;
        bool prevLog = Debug.unityLogger.logEnabled;
        GComponent reference = null, inst = null;
        try
        {
            FqsAutoMount.enabled = false;
            FqsAutoMount.suppressed = false;
            FqsAutoMount.ClearCache();

            UIPackage pkg = UIPackage.GetByName("Basics") ?? UIPackage.AddPackage("UI/Basics");
            ulong srcHash = FqsAutoMount.PackageSourceHash(pkg);

            //find the first bakeable exported component; its instance stays on
            //as the runtime-walk twin for the pixel checks
            PackageItem chosen = null;
            byte[] blob = null;
            Debug.unityLogger.logEnabled = false; //mute per-item refusal logs
            foreach (var item in pkg.GetItems())
            {
                if (item.type != PackageItemType.Component || !item.exported)
                    continue;
                var cand = UIPackage.CreateObjectFromURL("ui://" + pkg.id + item.id) as GComponent;
                if (cand == null)
                    continue;
                env.root.AddChild(cand);
                cand.SetXY(10, 10);
                env.Step(1);
                blob = FqsBaker.Bake((Container)cand.displayObject, srcHash, out _);
                if (blob != null)
                {
                    chosen = item;
                    reference = cand;
                    break;
                }
                cand.Dispose();
            }
            Debug.unityLogger.logEnabled = prevLog;

            int leafCount = FqsBlob.PeekLeafCount(blob);
            env.Check($"c1.a bakeable exported component exists ({(chosen != null ? pkg.name + "/" + chosen.name : "none")}, leaves={leafCount})",
                chosen != null && leafCount > 0 && srcHash != 0);
            if (chosen == null)
                return env.Report();

            //fit the twins inside the capture area
            if (reference.width > 290 || reference.height > 400)
            {
                float fit = Mathf.Min(290f / reference.width, 400f / reference.height);
                reference.SetScale(fit, fit);
            }

            env.Check("c2.PeekLeafCount rejects non-blob bytes",
                FqsBlob.PeekLeafCount(null) == -1 && FqsBlob.PeekLeafCount(new byte[10]) == -1
                && FqsBlob.PeekLeafCount(new byte[64]) == -1);

            int providerCalls = 0;
            byte[] served = blob;
            FqsAutoMount.blobProvider = (p, it) =>
            {
                if (p == pkg && it == chosen)
                {
                    providerCalls++;
                    return served;
                }
                return null;
            };

            GComponent Create()
            {
                var c = UIPackage.CreateObjectFromURL("ui://" + pkg.id + chosen.id) as GComponent;
                env.root.AddChild(c);
                c.SetXY(320, 10);
                c.SetScale(reference.scaleX, reference.scaleY);
                return c;
            }

            //--- c3: master switch off -> no lookup, no mount ----------------
            inst = Create();
            env.Check("c3.disabled: no mount and the provider is never asked",
                FqsMount.Of(inst) == null && providerCalls == 0);
            inst.Dispose();

            //--- c4: enabled, threshold above blob -> mount, no defer --------
            FqsAutoMount.enabled = true;
            FqsAutoMount.deferLeafThreshold = leafCount + 1;
            int m0 = FqsAutoMount.mountedCount;
            inst = Create();
            var leaves = new List<NGraphics>();
            CollectLeaves((Container)inst.displayObject, leaves);
            bool allNative = leaves.Count > 0 && leaves.TrueForAll(g => g.meshRenderer != null);
            env.Check("c4.mounts under the defer threshold with ordinary renderers",
                FqsMount.Of(inst) != null && FqsAutoMount.mountedCount == m0 + 1 && allNative);
            inst.Dispose();

            //--- c5: the cache serves repeat instances -----------------------
            int calls0 = providerCalls;
            inst = Create();
            env.Check("c5.repeat instance mounts from cache (provider not re-asked)",
                FqsMount.Of(inst) != null && providerCalls == calls0);
            inst.Dispose();

            //--- c6: defer scope engages and the splice claims the leaves ----
            FqsAutoMount.deferLeafThreshold = 1;
            inst = Create();
            leaves.Clear();
            CollectLeaves((Container)inst.displayObject, leaves);
            bool renderless = leaves.Count > 0 && leaves.TrueForAll(g => g.meshRenderer == null);
            env.root.instancedRendering = true;
            env.Step(2);
            bool stillRenderless = leaves.TrueForAll(g => g.meshRenderer == null);
            env.Check("c6.threshold defers renderers and the splice claims them",
                renderless && Spliced(FqsMount.Of(inst)) && stillRenderless);

            //--- c7: pixel parity vs the runtime-walk twin -------------------
            //same spot, alternating visibility (the parity-runner pattern)
            inst.visible = false;
            env.Step(1);
            var pxRef = env.Capture();
            reference.visible = false;
            inst.visible = true;
            inst.SetXY(10, 10);
            env.Step(1);
            var pxAuto = env.Capture();
            double mean, badPct;
            env.DiffStats(pxRef, pxAuto, reference, 2, 2, reference.width - 2, reference.height - 2, out mean, out badPct);
            env.Check($"c7.auto-mounted pixels match the runtime walk (mean={mean:F3} bad={badPct:F3}%)",
                mean < 1.5 && badPct < 0.5);
            inst.Dispose();

            //--- c8: stale blob refused, runtime walk still renders ----------
            FqsAutoMount.ClearCache();
            var tampered = (byte[])blob.Clone();
            tampered[8] ^= 0xFF; //first fuiHash byte: parses fine, gate trips
            served = tampered;
            int r0 = FqsAutoMount.refusedCount;
            Debug.unityLogger.logEnabled = false;
            inst = Create();
            Debug.unityLogger.logEnabled = prevLog;
            inst.SetXY(10, 10);
            env.Step(1);
            var pxStale = env.Capture();
            env.DiffStats(pxRef, pxStale, reference, 2, 2, reference.width - 2, reference.height - 2, out mean, out badPct);
            env.Check($"c8.stale blob refused, fallback renders (mean={mean:F3} bad={badPct:F3}%)",
                FqsMount.Of(inst) == null && FqsAutoMount.refusedCount == r0 + 1
                && mean < 1.5 && badPct < 0.5);

            //--- c9: the refusal is locked in --------------------------------
            int calls1 = providerCalls;
            int r1 = FqsAutoMount.refusedCount;
            var inst2 = Create();
            env.Check("c9.refusal locked in: no re-parse, no second refusal",
                FqsMount.Of(inst2) == null && providerCalls == calls1
                && FqsAutoMount.refusedCount == r1);
            inst2.Dispose();

            //--- c10: suppression wins over the master switch ----------------
            FqsAutoMount.ClearCache();
            served = blob;
            FqsAutoMount.suppressed = true;
            var inst3 = Create();
            env.Check("c10.suppressed: bake/parity instances never auto-mount",
                FqsMount.Of(inst3) == null);
            FqsAutoMount.suppressed = false;
            inst3.Dispose();

            //--- c11: an outer defer scope is left untouched -----------------
            NGraphics.deferRenderers = true;
            var inst4 = Create();
            bool scopeKept = NGraphics.deferRenderers;
            NGraphics.deferRenderers = false;
            env.Check("c11.nested construction rides an outer defer scope",
                scopeKept && FqsMount.Of(inst4) != null);
            inst4.Dispose();

            //--- c12: the staleness gate is live -----------------------------
            env.Check("c12.blob hash equals the recomputed package source hash",
                FqsBlob.Read(blob).fuiHash == FqsAutoMount.PackageSourceHash(pkg));
            inst = null;
        }
        finally
        {
            Debug.unityLogger.logEnabled = prevLog;
            FqsAutoMount.enabled = savedEnabled;
            FqsAutoMount.deferLeafThreshold = savedThreshold;
            FqsAutoMount.blobProvider = savedProvider;
            FqsAutoMount.suppressed = savedSuppressed;
            FqsAutoMount.ClearCache();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
