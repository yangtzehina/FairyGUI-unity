#if UNITY_2020_1_OR_NEWER
using System.Collections.Generic;
using System.Reflection;
using FairyGUI;
using UnityEngine;

/// <summary>
/// Auto-mount suite (M8 follow-on, 25 checks). FqsAutoMount turns the manual
/// two-line bake integration into a per-project switch: package-created
/// components look up blobs through a pluggable provider at construction, ARM
/// them on the container, and the enclosing stream realizes the mount at
/// extract. Blobs at/over the leaf threshold construct inside an M8-5
/// defer-renderers scope.
///
/// Half of these checks exist because an adversarial review of the first
/// implementation confirmed 18 defects in it; each such check names the
/// hazard it pins down, so a regression reads as the original bug:
///  - arm-then-realize (structure edited between construction and the first
///    extract must NOT splice frozen quads — the original mounted at
///    construction, where _NotifyStructure is inert with no live stream)
///  - identity by item id (a NON-exported component must never be served an
///    exported component's blob; duplicate/case-colliding names likewise)
///  - branch-resolved keying, content-scale-level gating, per-package-INSTANCE
///    caches, and refusing blobs with no verifiable source hash
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class FqsAutoMountSuite
{
    static readonly FieldInfo sSpliced =
        typeof(FqsMount).GetField("spliced", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo sPending =
        typeof(Container).GetField("_fqsPending", BindingFlags.NonPublic | BindingFlags.Instance);

    static bool Spliced(FqsMount m) { return m != null && sSpliced != null && (bool)sSpliced.GetValue(m); }
    static bool Armed(GComponent c) { return sPending != null && sPending.GetValue((Container)c.displayObject) != null; }

    /// <summary>Stands in for a bundle/Addressables loader: the package arrives
    /// as raw descriptor bytes plus a resolver for its assets.</summary>
    static object BundleShapedLoader(string name, string extension, System.Type type, out DestroyMethod dm)
    {
        dm = DestroyMethod.Unload;
        return Resources.Load(name, type);
    }

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
        bool savedRequire = FqsAutoMount.requireSourceHash;
        int savedScale = UIContentScaler.scaleLevel;
        bool prevLog = Debug.unityLogger.logEnabled;
        GComponent reference = null, inst = null;
        try
        {
            FqsAutoMount.enabled = false;
            FqsAutoMount.suppressed = false;
            FqsAutoMount.requireSourceHash = true;
            FqsAutoMount.ClearCache();

            UIPackage pkg = UIPackage.GetByName("Basics") ?? UIPackage.AddPackage("UI/Basics");
            ulong srcHash = FqsAutoMount.PackageSourceHash(pkg);

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
            PackageItem lastAsked = null;
            FqsAutoMount.blobProvider = (p, it) =>
            {
                lastAsked = it;
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

            //--- c3: master switch off -> no lookup, no arming ---------------
            inst = Create();
            env.Check("c3.disabled: nothing armed and the provider is never asked",
                !Armed(inst) && FqsMount.Of(inst) == null && providerCalls == 0);
            inst.Dispose();

            //--- c4: enabled -> ARMED at construction, NOT mounted -----------
            //(the original implementation mounted here; that is the defect the
            //arm/realize split exists to remove)
            FqsAutoMount.enabled = true;
            FqsAutoMount.deferLeafThreshold = leafCount + 1;
            inst = Create();
            env.Check("c4.construction arms the blob and does NOT bind it yet",
                Armed(inst) && FqsMount.Of(inst) == null);

            //--- c5: the enclosing stream realizes it at extract -------------
            int m0 = FqsAutoMount.mountedCount;
            env.root.instancedRendering = true;
            env.Step(2);
            env.Check("c5.the enclosing stream realizes the mount at extract",
                FqsMount.Of(inst) != null && !Armed(inst)
                && FqsAutoMount.mountedCount == m0 + 1 && Spliced(FqsMount.Of(inst)));
            inst.Dispose();
            env.Step(1);

            //--- c6: cache serves repeat instances ---------------------------
            int calls0 = providerCalls;
            inst = Create();
            env.Step(2);
            env.Check("c6.repeat instance mounts from cache (provider not re-asked)",
                FqsMount.Of(inst) != null && providerCalls == calls0);
            inst.Dispose();
            env.Step(1);

            //--- c7: REGRESSION — content REMOVED before the first extract ---
            //The hazard is a mount bound while no stream is live: with
            //liveInPlaceCount == 0 the _NotifyStructure invalidation channel is
            //inert, so the removal goes unseen and the blob keeps painting the
            //removed child — a ghost. (Appending a child is NOT the test: it
            //leaves every existing child index, hence every baked pathHash,
            //intact, so the bind legitimately still succeeds.)
            //Compared against a runtime-walk twin carrying the same edit.
            reference.visible = false;
            FqsAutoMount.enabled = false;
            env.root.instancedRendering = false;
            env.Step(1);
            var twin = Create();
            twin.SetXY(10, 10);
            twin.RemoveChildAt(0);
            env.root.instancedRendering = true;
            env.Step(2);
            var pxTwin = env.Capture();
            twin.Dispose();
            env.root.instancedRendering = false;
            env.Step(1);

            FqsAutoMount.enabled = true;
            inst = Create();
            inst.SetXY(10, 10);
            inst.RemoveChildAt(0); //structure edit before any extract
            env.root.instancedRendering = true;
            env.Step(2);
            var pxEdited = env.Capture();
            double m7, b7;
            env.DiffStats(pxTwin, pxEdited, reference, 2, 2, reference.width - 2, reference.height - 2, out m7, out b7);
            env.Check($"c7.content removed before the first extract leaves no ghost (mean={m7:F3} bad={b7:F3}%)",
                FqsMount.Of(inst) == null && m7 < 1.5 && b7 < 0.5);
            inst.Dispose();
            reference.visible = true;
            env.Step(1);

            //--- c8: defer scope engages and the splice claims the leaves ----
            FqsAutoMount.deferLeafThreshold = 1;
            inst = Create();
            var leaves = new List<NGraphics>();
            CollectLeaves((Container)inst.displayObject, leaves);
            bool renderless = leaves.Count > 0 && leaves.TrueForAll(g => g.meshRenderer == null);
            env.Step(2);
            bool stillRenderless = leaves.TrueForAll(g => g.meshRenderer == null);
            env.Check("c8.threshold defers renderers and the splice claims them",
                renderless && Spliced(FqsMount.Of(inst)) && stillRenderless);

            //--- c9: pixel parity vs the runtime-walk twin -------------------
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
            env.Check($"c9.auto-mounted pixels match the runtime walk (mean={mean:F3} bad={badPct:F3}%)",
                mean < 1.5 && badPct < 0.5);
            inst.Dispose();
            env.Step(1);

            //--- c10: stale blob refused, runtime walk still renders ---------
            FqsAutoMount.ClearCache();
            var tampered = (byte[])blob.Clone();
            tampered[8] ^= 0xFF; //first fuiHash byte: parses fine, gate trips
            served = tampered;
            int r0 = FqsAutoMount.refusedCount;
            Debug.unityLogger.logEnabled = false;
            inst = Create();
            inst.SetXY(10, 10);
            env.Step(2);
            Debug.unityLogger.logEnabled = prevLog;
            var pxStale = env.Capture();
            env.DiffStats(pxRef, pxStale, reference, 2, 2, reference.width - 2, reference.height - 2, out mean, out badPct);
            env.Check($"c10.stale blob refused, fallback renders (mean={mean:F3} bad={badPct:F3}%)",
                FqsMount.Of(inst) == null && FqsAutoMount.refusedCount == r0 + 1
                && mean < 1.5 && badPct < 0.5);
            inst.Dispose();
            env.Step(1);

            //--- c11: REGRESSION — a per-instance refusal is not locked in ---
            //The original nulled the cached bytes on any refusal, so one
            //unlucky instance denied the blob to every later healthy one.
            served = blob;
            FqsAutoMount.ClearCache();
            Debug.unityLogger.logEnabled = false;
            var bad = Create();
            bad.RemoveChildAt(0); //make THIS instance unbindable (a baked leaf is gone)
            env.Step(2);
            bool badRefused = FqsMount.Of(bad) == null;
            bad.Dispose();
            env.Step(1);
            var good = Create();
            env.Step(2);
            Debug.unityLogger.logEnabled = prevLog;
            env.Check("c11.an instance-specific refusal does not deny the blob to healthy instances",
                badRefused && FqsMount.Of(good) != null);
            good.Dispose();
            env.Step(1);

            //--- c12: suppression wins over the master switch ----------------
            FqsAutoMount.suppressed = true;
            var inst3 = Create();
            env.Step(2);
            env.Check("c12.suppressed: bake/parity instances never arm or mount",
                !Armed(inst3) && FqsMount.Of(inst3) == null);
            FqsAutoMount.suppressed = false;
            inst3.Dispose();
            env.Step(1);

            //--- c13: an outer defer scope is left untouched -----------------
            NGraphics.deferRenderers = true;
            var inst4 = Create();
            bool scopeKept = NGraphics.deferRenderers;
            NGraphics.deferRenderers = false;
            env.Step(2);
            env.Check("c13.nested construction rides an outer defer scope",
                scopeKept && FqsMount.Of(inst4) != null);
            inst4.Dispose();
            env.Step(1);

            //--- c14: the mount side recomputes the same combination ---------
            //Callers pass only the OWNING package's hash; Mount must fold in
            //the referenced packages itself and arrive at the stored value.
            //(c23 checks the bake side of the same identity.)
            var gateProbe = Create();
            Debug.unityLogger.logEnabled = false;
            bool gateAccepts = FqsMount.Mount(gateProbe, blob, pkg.sourceHash);
            FqsMount.Unmount((Container)gateProbe.displayObject);
            bool gateRejects = !FqsMount.Mount(gateProbe, blob, pkg.sourceHash + 1);
            Debug.unityLogger.logEnabled = prevLog;
            gateProbe.Dispose();
            env.Step(1);
            env.Check("c14.Mount recomputes the combined gate value from the owner hash alone",
                gateAccepts && gateRejects);

            //--- c15: REGRESSION — identity is the item id, not the name -----
            //Two components with the same name (one exported, one not) must
            //never resolve to the same blob file.
            PackageItem other = null;
            foreach (var it in pkg.GetItems())
                if (it.type == PackageItemType.Component && it != chosen) { other = it; break; }
            env.Check("c15.blob file identity carries the item id, so same-named items never collide",
                other != null
                && FqsAutoMount.BlobFileName(chosen) != FqsAutoMount.BlobFileName(other)
                && FqsAutoMount.BlobFileName(chosen).EndsWith("_" + chosen.id));

            //--- c16: REGRESSION — non-exported components are never looked up
            lastAsked = null;
            int callsBefore = providerCalls;
            PackageItem nonExported = null;
            foreach (var it in pkg.GetItems())
                if (it.type == PackageItemType.Component && !it.exported) { nonExported = it; break; }
            bool nonExportedClean = true;
            if (nonExported != null)
            {
                var ne = UIPackage.CreateObjectFromURL("ui://" + pkg.id + nonExported.id) as GComponent;
                if (ne != null)
                {
                    nonExportedClean = !Armed(ne) && FqsMount.Of(ne) == null && providerCalls == callsBefore;
                    ne.Dispose();
                }
            }
            env.Check($"c16.non-exported components are never served a blob (found={(nonExported != null)})",
                nonExportedClean);

            //--- c17: REGRESSION — branch-resolved item keys the lookup ------
            env.Check("c17.lookup keys on the branch-resolved item",
                FqsAutoMount.ResolveItem(chosen) == chosen.getBranch());

            //--- c18: REGRESSION — content scale level gates the mount -------
            //A blob baked at another level holds the other level's atlas rects
            //and the package source hash cannot tell the difference.
            int bakedLevel = FqsBlob.DecodeScaleLevel(FqsBlob.Read(blob).flags);
            UIContentScaler.scaleLevel = bakedLevel + 1;
            Debug.unityLogger.logEnabled = false;
            var scaleInst = Create();
            env.Step(2);
            Debug.unityLogger.logEnabled = prevLog;
            bool scaleRefused = FqsMount.Of(scaleInst) == null;
            scaleInst.Dispose();
            UIContentScaler.scaleLevel = savedScale;
            env.Step(1);
            env.Check($"c18.a blob baked at another contentScaleLevel is refused (baked={bakedLevel})",
                bakedLevel == savedScale && scaleRefused);

            //--- c19: REGRESSION — an unverifiable blob cannot pass the gate -
            //requireSourceHash exists because bundle/Addressables deployments
            //are exactly the ones that ship blobs apart from their packages.
            //A blob baked without a source hash carries NoSourceHash, and that
            //must never satisfy a caller supplying a real expected hash.
            var noHashSrc = Create();
            env.Step(2);
            Debug.unityLogger.logEnabled = false;
            byte[] unverifiable = FqsBaker.Bake((Container)noHashSrc.displayObject, 0, out _);
            noHashSrc.Dispose();
            env.Step(1);
            var probe = Create();
            env.Step(2);
            bool refusedUnverifiable = unverifiable != null
                && (FqsBlob.Read(unverifiable).flags & FqsBlob.BlobFlags.NoSourceHash) != 0
                && !FqsMount.Mount(probe, unverifiable, srcHash)
                && FqsMount.Mount(probe, unverifiable); //no expectation -> accepted
            probe.Dispose();
            Debug.unityLogger.logEnabled = prevLog;
            env.Step(1);
            env.Check("c19.a blob with no source hash is refused whenever a hash is expected",
                FqsAutoMount.requireSourceHash && refusedUnverifiable);

            //--- c21/c22: the gate covers NON-Resources load paths -----------
            //The hash used to be computed by reaching into Resources from the
            //bake line, so AssetBundle/Addressables/raw-byte deployments —
            //exactly the ones that ship blobs apart from their packages — got
            //0 and had the staleness gate silently waived. UIPackage now hashes
            //its descriptor inside LoadPackage, the one point every AddPackage
            //overload funnels through. Proven end to end on the raw-byte path
            //(the shape a bundle deployment uses) with a package not otherwise
            //in play, then removed again.
            //capture the descriptor FIRST: assetPath is set only by the
            //path-based overload, so on a bundle/raw-byte load this Resources
            //lookup returns null — a check must FAIL there, not throw and take
            //the other 21 results down with it
            TextAsset ownDesc = string.IsNullOrEmpty(pkg.assetPath)
                ? null : Resources.Load<TextAsset>(pkg.assetPath + "_fui");
            env.Check($"c21.the package's hash equals FNV over its own descriptor (desc={(ownDesc != null)})",
                pkg.sourceHash != 0 && ownDesc != null
                && pkg.sourceHash == FqsBlob.Hash(ownDesc.bytes));

            //c22 is the property the whole gate rests on in production: the
            //editor bakes against a Resources load, the player loads the same
            //published package from a bundle, and the two MUST hash alike or
            //the gate refuses every valid blob on device. Comparing the raw-
            //byte path against FNV of those same bytes would be near-tautology;
            //this loads one real package BOTH ways and compares.
            var bagDesc = Resources.Load<TextAsset>("UI/Bag_fui");
            ulong viaResources = 0, viaRawBytes = 0;
            if (bagDesc != null && UIPackage.GetByName("Bag") == null)
            {
                UIPackage viaRes = UIPackage.AddPackage("UI/Bag");
                if (viaRes != null)
                {
                    viaResources = viaRes.sourceHash;
                    UIPackage.RemovePackage(viaRes.id);
                }
                UIPackage viaRaw = UIPackage.AddPackage(bagDesc.bytes, "UI/Bag", BundleShapedLoader);
                if (viaRaw != null)
                {
                    viaRawBytes = viaRaw.sourceHash;
                    UIPackage.RemovePackage(viaRaw.id);
                }
            }
            env.Check($"c22.the same package hashes alike loaded from Resources vs raw bytes (bundle shape) ({viaResources:X16})",
                viaResources != 0 && viaResources == viaRawBytes
                && viaResources != pkg.sourceHash);

            //--- c23/c24: the gate spans DEPENDENCY packages too --------------
            //A blob flattens the whole subtree, so a component from another
            //package contributes baked geometry and atlas UVs. Gating on the
            //owning package alone would let a dependency be re-published (atlas
            //repacks) while the frozen quads keep sampling the old rects.
            var bd = FqsBlob.Read(blob);
            ulong recombined = FqsBlob.CombineWithReferences(srcHash, bd.texRefs, FqsBlob.LivePackageHash);
            bool refsPackages = false;
            foreach (var tr in bd.texRefs)
                if (tr.kind == FqsBlob.TexRefKind.Package) refsPackages = true;
            env.Check($"c23.the stored gate value folds in every referenced package (refs={refsPackages})",
                refsPackages && bd.fuiHash == recombined && bd.fuiHash != srcHash);

            //sensitivity: the SAME texRefs hash differently once a referenced
            //package's hash changes — which is what re-publishing it does
            var fakeRefs = new FqsBlob.TexRef[]
            {
                new FqsBlob.TexRef { kind = FqsBlob.TexRefKind.Package, url = pkg.id + "/x" },
                new FqsBlob.TexRef { kind = FqsBlob.TexRefKind.Package, url = "OTHERPKG/y" },
            };
            ulong withDep = FqsBlob.CombineWithReferences(srcHash, fakeRefs,
                id => id == "OTHERPKG" ? 0xDEADBEEFUL : FqsBlob.LivePackageHash(id));
            ulong depRepublished = FqsBlob.CombineWithReferences(srcHash, fakeRefs,
                id => id == "OTHERPKG" ? 0xFEEDFACEUL : FqsBlob.LivePackageHash(id));
            ulong depMissing = FqsBlob.CombineWithReferences(srcHash, fakeRefs, FqsBlob.LivePackageHash);
            env.Check("c24.re-publishing (or losing) a referenced package moves the gate value",
                withDep != depRepublished && withDep != depMissing && depRepublished != depMissing
                && FqsBlob.CombineWithReferences(0, fakeRefs, FqsBlob.LivePackageHash) == 0);

            //--- c25: REGRESSION — a stale FORMAT refuses as a format problem -
            //The combined gate value changed what fuiHash MEANS, so a blob from
            //before it would fail the hash compare and be reported as "stale
            //content" — sending a team hunting for a re-export that never
            //happened. FormatVersion carries the change instead.
            var v1 = (byte[])blob.Clone();
            v1[4] = 1; v1[5] = 0; v1[6] = 0; v1[7] = 0; //formatVersion := 1
            string formatMsg = null;
            try { FqsBlob.Read(v1); }
            catch (System.IO.InvalidDataException e) { formatMsg = e.Message; }
            env.Check($"c25.a previous-format blob refuses by VERSION, not as staleness ({formatMsg})",
                FqsBlob.FormatVersion >= 2 && formatMsg != null
                && formatMsg.Contains("format") && FqsBlob.PeekLeafCount(v1) == -1);

            //--- c20: REGRESSION — Bake restores the caller's mounts ---------
            //Bake detaches mounts so it never freezes an accelerator into a new
            //blob, but the caller's live tree must survive the call intact.
            var keeper = Create();
            env.Step(2);
            FqsMount before = FqsMount.Of(keeper);
            FqsBaker.Bake((Container)keeper.displayObject, srcHash, out _);
            env.Check("c20.Bake leaves the caller's live mount attached",
                before != null && FqsMount.Of(keeper) == before);
            keeper.Dispose();
            env.Step(1);
            inst = null;
        }
        finally
        {
            Debug.unityLogger.logEnabled = prevLog;
            UIContentScaler.scaleLevel = savedScale;
            NGraphics.deferRenderers = false;
            FqsAutoMount.enabled = savedEnabled;
            FqsAutoMount.deferLeafThreshold = savedThreshold;
            FqsAutoMount.blobProvider = savedProvider;
            FqsAutoMount.suppressed = savedSuppressed;
            FqsAutoMount.requireSourceHash = savedRequire;
            FqsAutoMount.ClearCache();
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
