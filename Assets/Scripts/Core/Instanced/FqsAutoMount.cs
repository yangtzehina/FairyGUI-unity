using System;
using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// M8 follow-on: zero-line bake integration. When enabled, every
    /// package-created GComponent looks up a baked FQS blob at construction:
    /// found blobs mount automatically (source-hash gated, see FqsMount) and a
    /// component whose blob carries at least deferLeafThreshold leaves
    /// constructs inside an NGraphics.deferRenderers scope (M8-5), so the
    /// manual two-line integration disappears entirely. Everything stays a
    /// discardable accelerator: lookup misses, hash mismatches and bind
    /// failures leave the component on the ordinary runtime walk.
    /// </summary>
    public static class FqsAutoMount
    {
        /// <summary>Master opt-in switch. Set before creating UI.</summary>
        public static bool enabled;

        /// <summary>
        /// Blob leaf count at which construction defers renderer creation
        /// (design §4.5: below ~10 leaves the deferral bookkeeping costs more
        /// than it saves). Negative disables the defer scope; mounting itself
        /// is unaffected.
        /// </summary>
        public static int deferLeafThreshold = 10;

        /// <summary>
        /// Resolves blob bytes for a package item; return null for "no blob".
        /// null provider = the default: Resources.Load from
        /// "Baked/{package}/{item}[_{id}].fqs.bytes" (the bake menu's output
        /// location and naming). Projects shipping blobs via bundles or
        /// Addressables plug their loader in here.
        /// </summary>
        public static Func<UIPackage, PackageItem, byte[]> blobProvider;

        /// <summary>Passed through to FqsMount for External texRefs.</summary>
        public static Func<string, Texture> externalTextureResolver;

        /// <summary>Components auto-mounted / refused since startup (diagnostics).</summary>
        public static int mountedCount, refusedCount;

        /// <summary>
        /// Bake and parity infrastructure sets this while creating instances
        /// that must stay on the runtime walk (a bake instance carrying a
        /// stale mount would splice old quads into the new blob).
        /// </summary>
        public static bool suppressed;

        class Entry
        {
            public byte[] bytes; //null = miss or locked-in refusal
            public int leafCount;
        }

        static readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>();
        static readonly Dictionary<string, ulong> _pkgHash = new Dictionary<string, ulong>();
        static readonly Dictionary<string, Dictionary<string, int>> _nameCounts =
            new Dictionary<string, Dictionary<string, int>>();
        static readonly HashSet<string> _warnedGateOff = new HashSet<string>();

        /// <summary>
        /// Drops every cached lookup (blob bytes, package hashes, name maps).
        /// Call after re-baking or when packages are unloaded/reloaded — a
        /// refused blob is otherwise remembered as a miss so later instances
        /// of the same item don't re-parse and re-warn every construction.
        /// </summary>
        public static void ClearCache()
        {
            _cache.Clear();
            _pkgHash.Clear();
            _nameCounts.Clear();
            _warnedGateOff.Clear();
        }

        /// <summary>
        /// FNV-1a over the package's _fui descriptor loaded via Resources —
        /// the same hash the bake menu embeds, recomputed from the CURRENT
        /// package so stale blobs refuse. 0 when unavailable (non-Resources
        /// package): the gate is then disabled for that package's blobs.
        /// </summary>
        public static ulong PackageSourceHash(UIPackage pkg)
        {
            if (_pkgHash.TryGetValue(pkg.id, out ulong h))
                return h;
            h = 0;
            if (!string.IsNullOrEmpty(pkg.assetPath))
            {
                var ta = Resources.Load<TextAsset>(pkg.assetPath + "_fui");
                if (ta != null)
                    h = FqsBlob.Hash(ta.bytes);
            }
            _pkgHash[pkg.id] = h;
            return h;
        }

        /// <summary>
        /// Mirrors the bake menu's file naming: an exported component name
        /// that is unique within its package bakes to "{name}", duplicated
        /// names ALL bake to "{name}_{id}" — so a name-based lookup can never
        /// resolve to a same-named sibling's blob.
        /// </summary>
        static bool ExportedNameUnique(UIPackage pkg, string name)
        {
            if (!_nameCounts.TryGetValue(pkg.id, out var counts))
            {
                counts = new Dictionary<string, int>();
                var items = pkg.GetItems();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it.type == PackageItemType.Component && it.exported)
                        counts[it.name] = counts.TryGetValue(it.name, out int c) ? c + 1 : 1;
                }
                _nameCounts[pkg.id] = counts;
            }
            return counts.TryGetValue(name, out int n) && n == 1;
        }

        static byte[] DefaultProvider(UIPackage pkg, PackageItem item)
        {
            string file = ExportedNameUnique(pkg, item.name) ? item.name : item.name + "_" + item.id;
            var ta = Resources.Load<TextAsset>("Baked/" + pkg.name + "/" + file + ".fqs");
            return ta != null ? ta.bytes : null;
        }

        static Entry Lookup(PackageItem item)
        {
            string key = item.owner.id + "/" + item.id;
            if (_cache.TryGetValue(key, out var e))
                return e;
            byte[] bytes = blobProvider != null ? blobProvider(item.owner, item) : DefaultProvider(item.owner, item);
            e = new Entry { bytes = bytes, leafCount = FqsBlob.PeekLeafCount(bytes) };
            if (e.leafCount < 0)
                e.bytes = null; //unreadable header: a miss (Mount would refuse anyway)
            _cache[key] = e;
            return e;
        }

        /// <summary>
        /// Head of package-component construction. Returns true when it opened
        /// a defer-renderers scope the matching EndConstruct must close; never
        /// opens one inside an existing scope (a nested construction rides the
        /// outer component's deferral).
        /// </summary>
        internal static bool BeginConstruct(GComponent comp)
        {
            if (!enabled || suppressed || comp.packageItem == null)
                return false;
            var e = Lookup(comp.packageItem);
            if (e.bytes == null || deferLeafThreshold < 0 || e.leafCount < deferLeafThreshold)
                return false;
            if (NGraphics.deferRenderers)
                return false;
            NGraphics.deferRenderers = true;
            return true;
        }

        /// <summary>
        /// Tail of package-component construction: closes the defer scope and
        /// mounts the blob now that the subtree is final. A refusal is locked
        /// in for the item (cleared by ClearCache) so a stale blob warns once,
        /// not once per instance.
        /// </summary>
        internal static void EndConstruct(GComponent comp, bool openedDefer)
        {
            if (openedDefer)
                NGraphics.deferRenderers = false;
            if (!enabled || suppressed || comp.packageItem == null)
                return;
            var e = Lookup(comp.packageItem);
            if (e.bytes == null)
                return;
            UIPackage pkg = comp.packageItem.owner;
            ulong expected = PackageSourceHash(pkg);
            if (expected == 0 && _warnedGateOff.Add(pkg.id))
                Debug.LogWarning($"FQS auto-mount: no source hash for package '{pkg.name}' — the staleness gate is DISABLED for its blobs.");
            if (FqsMount.Mount(comp, e.bytes, expected, externalTextureResolver))
                mountedCount++;
            else
            {
                refusedCount++;
                e.bytes = null;
            }
        }
    }
}
