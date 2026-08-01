using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// M8 follow-on: zero-line bake integration. When enabled, every
    /// package-created GComponent looks up a baked FQS blob at construction;
    /// a component whose blob carries at least deferLeafThreshold leaves
    /// constructs inside an NGraphics.deferRenderers scope (M8-5), and the
    /// blob is ARMED on the container rather than mounted.
    ///
    /// Arming, not mounting, is the whole safety story. A mount binds the blob
    /// to live NGraphics objects, and the only thing that can invalidate that
    /// binding when the subtree changes is InstancedUIStream._NotifyStructure,
    /// which is inert while no in-place stream is live. A mount created at
    /// construction time — unparented, before any stream exists — would
    /// therefore survive every structure edit made between construction and
    /// the first extract, and splice frozen quads over a subtree that no
    /// longer matches. Instead the armed blob is realized by the enclosing
    /// stream at extract (see _Realize), so the bind always runs against the
    /// structure the stream is actually looking at; edits after that point are
    /// covered by the ordinary invalidation ladder.
    ///
    /// Everything stays a discardable accelerator: lookup misses, hash
    /// mismatches and bind failures leave the component on the runtime walk.
    /// </summary>
    public static class FqsAutoMount
    {
        /// <summary>Master opt-in switch. Set before creating UI.</summary>
        public static bool enabled;

        /// <summary>
        /// Blob leaf count at which construction defers renderer creation
        /// (design §4.5: below ~10 leaves the deferral bookkeeping costs more
        /// than it saves). Negative disables the defer scope; arming itself is
        /// unaffected.
        /// </summary>
        public static int deferLeafThreshold = 10;

        /// <summary>
        /// Refuse blobs that cannot be checked against their source package
        /// (PackageSourceHash == 0 — a non-Resources load, i.e. every bundle
        /// and Addressables deployment). Default ON: the staleness gate is the
        /// bake line's whole safety net, and silently waiving it for exactly
        /// the deployments that ship blobs separately from packages is how
        /// stale UI reaches players. Projects that carry their own source
        /// hash (or accept the risk) set this false deliberately.
        /// </summary>
        public static bool requireSourceHash = true;

        /// <summary>
        /// Resolves blob bytes for a package item; return null for "no blob".
        /// The item passed is the BRANCH-RESOLVED item — the one whose rawData
        /// actually built the component. null provider = the default: load
        /// "Baked/{package}/{name}_{id}" from Resources (the bake menu's
        /// output location and naming). Projects shipping blobs via bundles or
        /// Addressables plug their loader in here.
        /// </summary>
        public static Func<UIPackage, PackageItem, byte[]> blobProvider;

        /// <summary>Passed through to FqsMount for External texRefs.</summary>
        public static Func<string, Texture> externalTextureResolver;

        /// <summary>Diagnostics: blobs armed, mounted, and refused since startup.</summary>
        public static int armedCount, mountedCount, refusedCount;

        /// <summary>
        /// Bake and parity infrastructure sets this while creating instances
        /// that must stay on the runtime walk (a bake instance carrying a
        /// stale mount would splice old quads into the new blob).
        /// </summary>
        public static bool suppressed;

        /// <summary>A blob armed on a container, awaiting realization at extract.</summary>
        internal sealed class Pending
        {
            internal byte[] bytes;
            internal ulong expectedHash;
            internal Entry entry;
        }

        internal sealed class Entry
        {
            internal byte[] bytes;      //null = miss, or a byte-deterministic refusal
            internal int leafCount;
            internal bool warned;       //non-deterministic refusal: warn once, keep trying
        }

        //per-package state hangs off the UIPackage INSTANCE, so unloading and
        //reloading a package (a new instance) starts clean — an id-keyed cache
        //would validate a fresh package's blobs against the old package's hash
        static readonly ConditionalWeakTable<UIPackage, PackageCache> _byPackage =
            new ConditionalWeakTable<UIPackage, PackageCache>();

        sealed class PackageCache
        {
            internal bool hashComputed;
            internal ulong hash;
            internal bool warnedGateOff;
            internal readonly Dictionary<string, Entry> items = new Dictionary<string, Entry>();
        }

        /// <summary>
        /// Drops every cached lookup. Call after re-baking; package unload does
        /// NOT need it (per-package state dies with the package instance).
        /// </summary>
        public static void ClearCache()
        {
            //ConditionalWeakTable has no Clear on this runtime: swap contents
            //by walking the packages we can still see, then drop stragglers
            foreach (var pkg in UIPackage.GetPackages())
                _byPackage.Remove(pkg);
        }

        static PackageCache CacheOf(UIPackage pkg)
        {
            if (!_byPackage.TryGetValue(pkg, out var c))
            {
                c = new PackageCache();
                _byPackage.Add(pkg, c);
            }
            return c;
        }

        /// <summary>
        /// FNV-1a over the package's _fui descriptor loaded via Resources —
        /// the same hash the bake menu embeds, recomputed from the CURRENT
        /// package so stale blobs refuse. 0 when unavailable (non-Resources
        /// package); see requireSourceHash for what that means.
        /// </summary>
        public static ulong PackageSourceHash(UIPackage pkg)
        {
            var c = CacheOf(pkg);
            if (c.hashComputed)
                return c.hash;
            c.hash = 0;
            if (!string.IsNullOrEmpty(pkg.assetPath))
            {
                var ta = Resources.Load<TextAsset>(pkg.assetPath + "_fui");
                if (ta != null)
                    c.hash = FqsBlob.Hash(ta.bytes);
            }
            c.hashComputed = true;
            return c.hash;
        }

        /// <summary>
        /// The item that actually built the component. A branched package
        /// builds from the branch variant, so keying anything on the base item
        /// would serve (and validate) the wrong branch's geometry — the
        /// package source hash cannot tell branches apart, and baked pathHashes
        /// are pure child-index chains that a re-skin does not disturb.
        /// </summary>
        public static PackageItem ResolveItem(PackageItem item)
        {
            return item.getBranch() ?? item;
        }

        /// <summary>
        /// Blob file name for an item: name for humans, id for identity. The
        /// id alone decides uniqueness, which is what makes the name-based
        /// hazards impossible — duplicate exported names, a NON-exported
        /// component shadowing an exported one, and names differing only by
        /// case on a case-insensitive filesystem all resolve to distinct files.
        /// </summary>
        public static string BlobFileName(PackageItem resolved)
        {
            string n = resolved.name ?? "";
            var b = new System.Text.StringBuilder(n.Length + 8);
            foreach (char ch in n)
                b.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return b.Append('_').Append(resolved.id).ToString();
        }

        static byte[] DefaultProvider(UIPackage pkg, PackageItem resolved)
        {
            var ta = Resources.Load<TextAsset>("Baked/" + pkg.name + "/" + BlobFileName(resolved) + ".fqs");
            return ta != null ? ta.bytes : null;
        }

        static Entry Lookup(PackageItem item)
        {
            //only exported components are ever baked; a non-exported component
            //must never reach a name-shaped lookup
            if (!item.exported)
                return null;
            UIPackage pkg = item.owner;
            PackageItem resolved = ResolveItem(item);
            var cache = CacheOf(pkg);
            if (cache.items.TryGetValue(resolved.id, out var e))
                return e;
            byte[] bytes = blobProvider != null ? blobProvider(pkg, resolved) : DefaultProvider(pkg, resolved);
            e = new Entry { bytes = bytes, leafCount = FqsBlob.PeekLeafCount(bytes) };
            if (e.leafCount < 0)
                e.bytes = null; //not a current-version blob: byte-deterministic miss
            cache.items[resolved.id] = e;
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
            if (e == null || e.bytes == null || deferLeafThreshold < 0 || e.leafCount < deferLeafThreshold)
                return false;
            if (NGraphics.deferRenderers)
                return false;
            NGraphics.deferRenderers = true;
            return true;
        }

        /// <summary>
        /// Tail of package-component construction: closes the defer scope and
        /// ARMS the blob on the container. The mount itself is deferred to the
        /// first extract (see the class remarks) — arming is pure data, so a
        /// subtree edited between here and then simply fails to bind and stays
        /// on the runtime walk instead of rendering frozen quads.
        /// </summary>
        internal static void EndConstruct(GComponent comp, bool openedDefer)
        {
            if (openedDefer)
                NGraphics.deferRenderers = false;
            if (!enabled || suppressed || comp.packageItem == null)
                return;
            var e = Lookup(comp.packageItem);
            if (e == null || e.bytes == null)
                return;
            UIPackage pkg = comp.packageItem.owner;
            ulong expected = PackageSourceHash(pkg);
            if (expected == 0)
            {
                var cache = CacheOf(pkg);
                if (!cache.warnedGateOff)
                {
                    cache.warnedGateOff = true;
                    Debug.LogWarning($"FQS auto-mount: no source hash for package '{pkg.name}' (non-Resources load)"
                        + (requireSourceHash
                            ? " — blobs REFUSED. Set FqsAutoMount.requireSourceHash = false to accept unverifiable blobs."
                            : " — the staleness gate is DISABLED for its blobs (requireSourceHash = false)."));
                }
                if (requireSourceHash)
                    return;
            }
            ((Container)comp.displayObject)._fqsPending =
                new Pending { bytes = e.bytes, expectedHash = expected, entry = e };
            armedCount++;
        }

        /// <summary>
        /// Realizes an armed blob against the CURRENT live subtree. Called by
        /// an enclosing in-place stream at extract, immediately before it would
        /// splice. One attempt per arming: a bind failure means this instance's
        /// structure does not match the bake, which no later extract changes.
        /// </summary>
        internal static void _Realize(Container c)
        {
            Pending p = c._fqsPending;
            c._fqsPending = null;
            if (p == null)
                return;
            bool savedQuiet = FqsMount.quietRefusals;
            FqsMount.quietRefusals = p.entry.warned;
            bool ok;
            try
            {
                ok = FqsMount.Mount(c, p.bytes, p.expectedHash, externalTextureResolver);
            }
            finally
            {
                FqsMount.quietRefusals = savedQuiet;
            }
            if (ok)
            {
                mountedCount++;
                return;
            }
            refusedCount++;
            //a refusal here is about THIS instance (structure, textures) or the
            //source hash, not about the bytes: keep them cached so healthy
            //instances still mount, but do not warn once per instance
            p.entry.warned = true;
        }
    }
}
