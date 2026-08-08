using System.IO;
using FairyGUI;
using UnityEditor;
using UnityEngine;

namespace FairyGUIEditor
{
    /// <summary>
    /// M8-1: bakes every exported component of every loaded UIPackage into an
    /// FQS1 blob under Assets/Resources/Baked/&lt;package&gt;/ (the auto-mount
    /// default provider loads exactly there). Runs the real stream compiler
    /// on a live instance, so it needs play mode with the packages loaded.
    /// Components outside the bakeable subset (text, barriers, masks) are
    /// refused per-item with the reason — never silently degraded.
    /// </summary>
    public static class FqsBakeMenu
    {
        [MenuItem("Tools/FairyGUI/Bake Packages (FQS)")]
        static void Bake()
        {
            BakeAll();
        }

        /// <summary>
        /// One pass over every loaded package's exported components: FQS blob
        /// per bakeable component (M8-1) AND a typed view facade per component
        /// regardless of blob bakeability (M8-3 — text components deserve
        /// views too). Returns (blobsBaked, blobsRefused, viewsWritten).
        /// </summary>
        public static Vector3Int BakeAll()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("FQS bake: enter play mode with UI packages loaded first.");
                return default;
            }

            int baked = 0, refused = 0, viewsWritten = 0;
            var pkgs = UIPackage.GetPackages();
            //bake instances must never auto-mount (a stale mount would splice
            //its old quads into the new blob), and a re-bake invalidates every
            //cached lookup on both sides of the pass
            FqsAutoMount.ClearCache();
            bool savedSuppress = FqsAutoMount.suppressed;
            FqsAutoMount.suppressed = true;
            try
            {
                foreach (var pkg in pkgs)
                {
                    ulong hash = pkg.sourceHash;
                    if (hash == 0)
                        Debug.LogWarning($"FQS: package '{pkg.name}' reports no source hash — the staleness gate would be DISABLED for its blobs.");
                    var usedClassNames = new System.Collections.Generic.HashSet<string>();
                    foreach (var item in pkg.GetItems())
                    {
                        if (item.type != PackageItemType.Component || !item.exported)
                            continue;
                        try
                        {
                        //create by ID: duplicate exported names resolve wrong by name
                        GObject obj = UIPackage.CreateObjectFromURL("ui://" + pkg.id + item.id);
                        //a branched package builds from the branch variant, so
                        //that is the identity the blob must carry
                        PackageItem contentItem = FqsAutoMount.ResolveItem(item);
                        var com = obj as GComponent;
                        if (com == null)
                        {
                            obj?.Dispose();
                            continue;
                        }
                        //parent under the UNSCALED stage: GRoot's UIContentScaler
                        //scale would leak view-size-dependent float low bits into
                        //the baked quads and break reproducible re-bakes
                        Stage.inst.AddChild(com.displayObject);
                        Stage.inst.ForceUpdate();

                        //M8-3: view facade for EVERY exported component
                        string src = FqsViewGenerator.GenerateSource(pkg, item, com, usedClassNames, out string clsName);
                        if (FqsViewGenerator.WriteIfChanged($"Assets/BakedViews/{pkg.name}/{clsName}.cs", src))
                            viewsWritten++;

                        byte[] blob = FqsBaker.Bake((Container)com.displayObject, hash, out string reason);
                        com.Dispose();
                        if (blob == null)
                        {
                            refused++;
                            Debug.Log($"FQS refused {pkg.name}/{item.name}: {reason}");
                            //a refusal must not leave last version's blob
                            //behind: it would keep mounting stale quads the
                            //current compiler refuses to produce (the v3
                            //gradient rule made this real — review round 3)
                            string staleDir = $"Assets/Resources/Baked/{pkg.name}";
                            string stale = $"{staleDir}/{FqsAutoMount.BlobFileName(contentItem, GRoot.contentScaleLevel)}.fqs.bytes";
                            if (File.Exists(stale))
                            {
                                File.Delete(stale);
                                Debug.LogWarning($"FQS deleted stale blob for refused {pkg.name}/{item.name}: {stale}");
                            }
                            continue;
                        }
                        string dir = $"Assets/Resources/Baked/{pkg.name}";
                        Directory.CreateDirectory(dir);
                        //name for humans, id for identity (FqsAutoMount.
                        //BlobFileName is the single source of this naming, and
                        //the default provider reads it back the same way).
                        //Identity by id is what makes duplicate exported names,
                        //a non-exported component shadowing an exported one,
                        //and case-only differences on a case-insensitive
                        //filesystem all resolve to distinct files.
                        File.WriteAllBytes($"{dir}/{FqsAutoMount.BlobFileName(contentItem, GRoot.contentScaleLevel)}.fqs.bytes", blob);
                        baked++;
                        }
                        catch (System.Exception e)
                        {
                            //one broken component must not abort the whole pass
                            refused++;
                            Debug.LogWarning($"FQS bake threw on {pkg.name}/{item.name}: {e.Message}");
                        }
                    }

                    //orphan report (no deletion): renamed/deleted/un-exported
                    //components leave files behind that ship with Resources
                    //forever and read as "baked" — list anything not matching
                    //a current exported component at ANY level
                    string pkgDir = $"Assets/Resources/Baked/{pkg.name}";
                    if (Directory.Exists(pkgDir))
                    {
                        var known = new System.Collections.Generic.HashSet<string>();
                        foreach (var it2 in pkg.GetItems())
                            if (it2.type == PackageItemType.Component && it2.exported)
                                known.Add(FqsAutoMount.BlobFileName(FqsAutoMount.ResolveItem(it2)));
                        foreach (var file in Directory.GetFiles(pkgDir, "*.fqs.bytes"))
                        {
                            string baseName = Path.GetFileName(file);
                            baseName = baseName.Substring(0, baseName.Length - ".fqs.bytes".Length);
                            int dot = baseName.IndexOf(".s");
                            if (dot >= 0)
                                baseName = baseName.Substring(0, dot); //strip the level suffix
                            if (!known.Contains(baseName))
                                Debug.LogWarning($"FQS orphan blob (no matching exported component): {file}");
                        }
                    }
                }
            }
            finally
            {
                FqsAutoMount.suppressed = savedSuppress;
                FqsAutoMount.ClearCache();
            }
            //two-phase codegen (charter §3): changed views must COMPILE before
            //anything can consume the types — mark pending, let the domain
            //reload happen, verify from fresh assemblies in OnViewsReloaded
            if (viewsWritten > 0)
                SessionState.SetBool("Fqs.ViewsPending", true);
            AssetDatabase.Refresh();
            int bakeLevel = GRoot.contentScaleLevel;
            //the level names the OUTPUT FILES (level>0 gets an .sN suffix a
            //level-0 device never loads) — an oversized editor Game view
            //silently baking an _s1 set was indistinguishable from "never
            //baked" on phones (review round 3), so the level is always echoed
            //and a non-zero one warns
            string summary = $"FQS bake: {baked} blobs baked, {refused} refused, {viewsWritten} views written, {pkgs.Count} packages, contentScaleLevel={bakeLevel}";
            if (bakeLevel > 0)
                Debug.LogWarning(summary + $" — output carries the .s{bakeLevel} suffix; level-0 devices will NOT load it. Shrink the Game view (or set UIContentScaler.scaleLevel = 0 and reload packages) for the base set.");
            else
                Debug.Log(summary);
            return new Vector3Int(baked, refused, viewsWritten);
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        static void OnViewsReloaded()
        {
            if (!SessionState.GetBool("Fqs.ViewsPending", false))
                return;
            SessionState.SetBool("Fqs.ViewsPending", false);
            int n = 0;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic)
                    continue;
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Namespace != null && t.Namespace.StartsWith("FairyGUI.Baked"))
                            n++;
                }
                catch (System.Reflection.ReflectionTypeLoadException) { }
            }
            Debug.Log($"FQS views settled: {n} generated types compiled and loadable.");
        }

    }
}
