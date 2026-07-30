using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// M8-2: a parsed, validated, live-bound FQS blob attached to the container
    /// it was baked from. When an enclosing in-place stream extracts, a valid
    /// mount SPLICES its precompiled quads/segments instead of walking the
    /// subtree — the mount container gets a transform slot (so moving/scaling
    /// the baked subtree is a tier-1 matrix write), its clip owners re-enter
    /// the stream's ordinary PushClip fold, and every baked leaf is registered
    /// as a live LeafRange so the push channels (content tier-2, color tier)
    /// keep working. The blob is a discardable accelerator: any validation
    /// failure, structure change inside the subtree, or count-changing rewrite
    /// invalidates the mount and the subtree silently returns to the runtime
    /// walk.
    /// </summary>
    public sealed class FqsMount
    {
        internal FqsBlob.Data data;
        internal Container root;
        internal NGraphics[] leafGraphics;   //per blob leaf record
        internal Container[] clipOwners;     //per blob clip record ([0] = sentinel: null)
        internal Texture[] textures;         //resolved texRefs (-1 slots stay null)
        internal Vector4 localBounds;        //quad AABB in mount-local space (sort entry)
        internal bool invalid;
        //leaves rewritten via tier-2 since mount: a re-splice restores stale
        //blob quads for them, so the splice re-queues these for a fresh rewrite
        internal readonly HashSet<NGraphics> rewritten = new HashSet<NGraphics>();

        /// <summary>The mount attached to a component's container, if any.</summary>
        public static FqsMount Of(GComponent comp)
        {
            return ((Container)comp.displayObject)._fqsMount;
        }

        public static bool Mount(GComponent comp, byte[] blob,
            ulong expectedFuiHash = 0, Func<string, Texture> resolveExternal = null)
        {
            return Mount((Container)comp.displayObject, blob, expectedFuiHash, resolveExternal);
        }

        /// <summary>
        /// Parses, validates and binds a blob onto the container it was baked
        /// from. expectedFuiHash != 0 enforces the staleness gate (mismatch
        /// refuses the mount — the runtime walk is always the safe fallback).
        /// External texRefs need resolveExternal.
        /// </summary>
        public static bool Mount(Container root, byte[] blob,
            ulong expectedFuiHash = 0, Func<string, Texture> resolveExternal = null)
        {
            FqsBlob.Data d;
            try
            {
                d = FqsBlob.Read(blob);
            }
            catch (InvalidDataException e)
            {
                Debug.LogWarning("FQS mount refused: " + e.Message);
                return false;
            }

            if (expectedFuiHash != 0)
            {
                if ((d.flags & FqsBlob.BlobFlags.NoSourceHash) != 0 || d.fuiHash != expectedFuiHash)
                {
                    Debug.LogWarning("FQS mount refused: source hash mismatch (stale blob) — subtree stays on the runtime walk.");
                    return false;
                }
            }

            var m = new FqsMount { data = d, root = root };
            if (!m.Bind(resolveExternal))
                return false;
            root._fqsMount = m;
            return true;
        }

        public static void Unmount(Container root)
        {
            root._fqsMount = null;
        }

        bool Bind(Func<string, Texture> resolveExternal)
        {
            textures = new Texture[data.texRefs.Length];
            for (int i = 0; i < data.texRefs.Length; i++)
            {
                var tr = data.texRefs[i];
                switch (tr.kind)
                {
                    case FqsBlob.TexRefKind.Empty:
                        textures[i] = NTexture.Empty.nativeTexture;
                        break;
                    case FqsBlob.TexRefKind.Package:
                    {
                        int slash = tr.url.IndexOf('/');
                        UIPackage pkg = slash > 0 ? UIPackage.GetById(tr.url.Substring(0, slash)) : null;
                        PackageItem item = pkg?.GetItem(tr.url.Substring(slash + 1));
                        if (item == null)
                        {
                            Debug.LogWarning($"FQS mount refused: package texture '{tr.url}' not loaded.");
                            return false;
                        }
                        if (item.texture == null)
                            pkg.GetItemAsset(item);
                        if (item.texture == null || item.texture.nativeTexture == null)
                        {
                            Debug.LogWarning($"FQS mount refused: package texture '{tr.url}' has no native texture.");
                            return false;
                        }
                        textures[i] = item.texture.nativeTexture;
                        break;
                    }
                    case FqsBlob.TexRefKind.External:
                    {
                        Texture t = resolveExternal?.Invoke(tr.url);
                        if (t == null)
                        {
                            Debug.LogWarning($"FQS mount refused: external texture '{tr.url}' unresolved.");
                            return false;
                        }
                        textures[i] = t;
                        break;
                    }
                }
            }

            //rebind pathHash -> live objects by mirroring the baker's walk;
            //any mismatch means the live structure differs from the bake
            var graphicsPaths = new Dictionary<NGraphics, ulong>();
            var containerPaths = new Dictionary<Container, ulong>();
            containerPaths[root] = FqsBaker.FnvSeed;
            FqsBaker.MapPaths(root, FqsBaker.FnvSeed, graphicsPaths, containerPaths);

            var byGraphicsPath = new Dictionary<ulong, NGraphics>();
            foreach (var kv in graphicsPaths)
            {
                if (byGraphicsPath.ContainsKey(kv.Value))
                {
                    Debug.LogWarning("FQS mount refused: path hash collision in live subtree.");
                    return false;
                }
                byGraphicsPath[kv.Value] = kv.Key;
            }
            var byContainerPath = new Dictionary<ulong, Container>();
            foreach (var kv in containerPaths)
                byContainerPath[kv.Value] = kv.Key;

            leafGraphics = new NGraphics[data.leaves.Length];
            for (int i = 0; i < data.leaves.Length; i++)
            {
                if (!byGraphicsPath.TryGetValue(data.leaves[i].pathHash, out leafGraphics[i]))
                {
                    Debug.LogWarning("FQS mount refused: baked leaf not found in live subtree (structure differs).");
                    return false;
                }
            }
            clipOwners = new Container[data.clips.Length];
            for (int i = 1; i < data.clips.Length; i++)
            {
                if (!byContainerPath.TryGetValue(data.clips[i].ownerPathHash, out clipOwners[i]))
                {
                    Debug.LogWarning("FQS mount refused: baked clip owner not found in live subtree.");
                    return false;
                }
            }

            var mn = new Vector2(float.MaxValue, float.MaxValue);
            var mx = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < data.quads.Length; i++)
            {
                Vector4 r = data.quads[i].rect;
                if (r.z <= 0 || r.w <= 0)
                    continue; //slack padding
                mn = Vector2.Min(mn, new Vector2(r.x, r.y));
                mx = Vector2.Max(mx, new Vector2(r.x + r.z, r.y + r.w));
            }
            localBounds = new Vector4(mn.x, mn.y, mx.x, mx.y);
            return true;
        }
    }
}
