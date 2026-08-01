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
                    ulong hash = SourceHash(pkg);
                    if (hash == 0)
                        Debug.LogWarning($"FQS: no source hash for package '{pkg.name}' (non-Resources load) — the staleness gate is DISABLED for its blobs.");
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
                        File.WriteAllBytes($"{dir}/{FqsAutoMount.BlobFileName(contentItem)}.fqs.bytes", blob);
                        baked++;
                        }
                        catch (System.Exception e)
                        {
                            //one broken component must not abort the whole pass
                            refused++;
                            Debug.LogWarning($"FQS bake threw on {pkg.name}/{item.name}: {e.Message}");
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
            Debug.Log($"FQS bake: {baked} blobs baked, {refused} refused, {viewsWritten} views written, {pkgs.Count} packages");
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

        static ulong SourceHash(UIPackage pkg)
        {
            return FqsAutoMount.PackageSourceHash(pkg);
        }
    }
}
