using System.IO;
using FairyGUI;
using UnityEditor;
using UnityEngine;

namespace FairyGUIEditor
{
    /// <summary>
    /// M8-1: bakes every exported component of every loaded UIPackage into an
    /// FQS1 blob under Assets/Baked/&lt;package&gt;/. Runs the real stream compiler
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
            foreach (var pkg in pkgs)
            {
                ulong hash = SourceHash(pkg);
                if (hash == 0)
                    Debug.LogWarning($"FQS: no source hash for package '{pkg.name}' (non-Resources load) — the staleness gate is DISABLED for its blobs.");
                var usedNames = new System.Collections.Generic.HashSet<string>();
                var usedClassNames = new System.Collections.Generic.HashSet<string>();
                foreach (var item in pkg.GetItems())
                {
                    if (item.type != PackageItemType.Component || !item.exported)
                        continue;
                    try
                    {
                    //create by ID: duplicate exported names resolve wrong by name
                    GObject obj = UIPackage.CreateObjectFromURL("ui://" + pkg.id + item.id);
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
                    string dir = $"Assets/Baked/{pkg.name}";
                    Directory.CreateDirectory(dir);
                    string fileName = usedNames.Add(item.name) ? item.name : item.name + "_" + item.id;
                    File.WriteAllBytes($"{dir}/{fileName}.fqs.bytes", blob);
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
            if (string.IsNullOrEmpty(pkg.assetPath))
                return 0;
            var ta = Resources.Load<TextAsset>(pkg.assetPath + "_fui");
            return ta != null ? FqsBlob.Hash(ta.bytes) : 0;
        }
    }
}
