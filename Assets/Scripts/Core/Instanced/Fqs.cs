using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// M8-1: the FQS1 baked quad-stream blob (design docs/design/m8-bake-line.md §2).
    /// A bake runs the REAL InstancedUIStream compiler once in the editor and
    /// freezes its output — no second implementation of layout semantics exists,
    /// so bake-time drift is structurally impossible. The hot sections are raw
    /// spans of unmanaged records read back with a single copy each
    /// (layout-as-ABI: records must never change; evolve via formatVersion).
    /// The blob is a DISCARDABLE accelerator: fuiHash gates staleness against
    /// the source package and the loader falls back to runtime Extract.
    /// </summary>
    public static class FqsBlob
    {
        //the format is little-endian only (every Unity target is LE; a BE
        //reader fails the magic check, which is the intended rejection)
        public const uint Magic = 0x31535146; //'FQS1' little-endian

        /// <summary>
        /// 2 (2026-08-01): fuiHash stopped meaning "the owning package's source
        /// hash" and started meaning "that hash CHAINED WITH every referenced
        /// package's" (see CombineWithReferences). Nothing about the LAYOUT
        /// moved, but a field's meaning did — and a v1 blob read by a v2 runtime
        /// would fail the gate as if its CONTENT were stale, sending teams
        /// hunting for a re-export that never happened. The version bump turns
        /// that into an explicit, self-describing refusal: re-bake.
        /// </summary>
        public const uint FormatVersion = 2;

        [Flags]
        public enum BlobFlags : uint
        {
            None = 0,
            NoSourceHash = 1, //programmatic bake or fui bytes unavailable
            //bits 8-15 carry the bake-time GRoot.contentScaleLevel. It selects
            //which image items a component builds from (PackageItem.
            //getHighResolution), so a blob baked at one level holds the other
            //level's atlas rects — and the package source hash cannot tell:
            //it is one hash over one _fui for every level.
            ScaleLevelMask = 0xFF00,
        }

        public static uint EncodeScaleLevel(int level)
        {
            return ((uint)Mathf.Clamp(level, 0, 255) << 8) & (uint)BlobFlags.ScaleLevelMask;
        }

        public static int DecodeScaleLevel(BlobFlags flags)
        {
            return (int)(((uint)flags & (uint)BlobFlags.ScaleLevelMask) >> 8);
        }

        public enum TexRefKind : byte
        {
            None = 0,     //unused segment texture slot
            Empty = 1,    //NTexture.Empty (white)
            Package = 2,  //url = "<packageId>/<itemId>"
            External = 3, //url = texture name; caller must resolve at mount
        }

        public struct TexRef
        {
            public TexRefKind kind;
            public string url;
        }

        public sealed class Data
        {
            public uint formatVersion;
            public ulong fuiHash;
            public BlobFlags flags;
            public QuadInstance[] quads;
            public FqsSegRecord[] segs;
            public FqsLeafRecord[] leaves;
            public FqsClipRecord[] clips;
            public TexRef[] texRefs;
        }

        public static byte[] Write(Data d)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Magic);
            w.Write(d.formatVersion);
            w.Write(d.fuiHash);
            w.Write((uint)d.flags);
            w.Write(d.quads.Length);
            w.Write(d.segs.Length);
            w.Write(d.leaves.Length);
            w.Write(d.clips.Length);
            w.Write(d.texRefs.Length);
            WriteSection(ms, w, d.quads);
            WriteSection(ms, w, d.segs);
            WriteSection(ms, w, d.leaves);
            WriteSection(ms, w, d.clips);
            Pad16(ms, w);
            for (int i = 0; i < d.texRefs.Length; i++)
            {
                w.Write((byte)d.texRefs[i].kind);
                byte[] url = Encoding.UTF8.GetBytes(d.texRefs[i].url ?? "");
                if (url.Length > ushort.MaxValue)
                    throw new InvalidDataException("FQS: texRef url too long");
                w.Write((ushort)url.Length);
                w.Write(url);
            }
            w.Flush();
            return ms.ToArray();
        }

        public static Data Read(byte[] bytes)
        {
            //hostile/corrupt input must surface as InvalidDataException BEFORE
            //any allocation — an M8-2 loader catches exactly that type to fall
            //back to runtime Extract
            if (bytes == null || bytes.Length < 44)
                throw new InvalidDataException("FQS: too short");
            var s = new ReadOnlySpan<byte>(bytes);
            int o = 0;
            if (ReadU32(s, ref o) != Magic)
                throw new InvalidDataException("FQS: bad magic");
            var d = new Data();
            d.formatVersion = ReadU32(s, ref o);
            if (d.formatVersion != FormatVersion)
                throw new InvalidDataException($"FQS: format {d.formatVersion} != {FormatVersion}");
            d.fuiHash = MemoryMarshal.Read<ulong>(s.Slice(o)); o += 8;
            d.flags = (BlobFlags)ReadU32(s, ref o);
            int nq = ReadI32(s, ref o), ns = ReadI32(s, ref o), nl = ReadI32(s, ref o),
                nc = ReadI32(s, ref o), nt = ReadI32(s, ref o);
            const int MaxCount = 1 << 24;
            if (nq < 0 || ns < 0 || nl < 0 || nc < 0 || nt < 0
                || nq > MaxCount || ns > MaxCount || nl > MaxCount || nc > MaxCount || nt > MaxCount)
                throw new InvalidDataException("FQS: count out of range");
            long need = Align16(o);
            need = Align16L(need + (long)nq * SizeOf<QuadInstance>());
            need = Align16L(need + (long)ns * SizeOf<FqsSegRecord>());
            need = Align16L(need + (long)nl * SizeOf<FqsLeafRecord>());
            need = Align16L(need + (long)nc * SizeOf<FqsClipRecord>());
            need = Align16L(need) + nt * 3L; //per texRef: kind + u16 len minimum
            if (need > bytes.Length)
                throw new InvalidDataException("FQS: truncated blob");

            d.quads = ReadSection<QuadInstance>(s, ref o, nq);
            d.segs = ReadSection<FqsSegRecord>(s, ref o, ns);
            d.leaves = ReadSection<FqsLeafRecord>(s, ref o, nl);
            d.clips = ReadSection<FqsClipRecord>(s, ref o, nc);
            o = Align16(o);
            d.texRefs = new TexRef[nt];
            for (int i = 0; i < nt; i++)
            {
                if (o + 3 > bytes.Length)
                    throw new InvalidDataException("FQS: truncated texRef table");
                var kind = (TexRefKind)s[o]; o += 1;
                int len = s[o] | (s[o + 1] << 8); o += 2;
                if (o + len > bytes.Length)
                    throw new InvalidDataException("FQS: truncated texRef url");
                d.texRefs[i] = new TexRef
                {
                    kind = kind,
                    url = len > 0 ? Encoding.UTF8.GetString(bytes, o, len) : ""
                };
                o += len;
            }
            if (o != bytes.Length)
                throw new InvalidDataException("FQS: trailing bytes");
            return d;
        }

        static int SizeOf<T>() where T : struct
        {
            return MemoryMarshal.AsBytes(sOne<T>.arr.AsSpan()).Length;
        }

        static class sOne<T> where T : struct
        {
            public static readonly T[] arr = new T[1];
        }

        static long Align16L(long o) { return (o + 15) & ~15L; }

        static void WriteSection<T>(MemoryStream ms, BinaryWriter w, T[] arr) where T : struct
        {
            Pad16(ms, w);
            if (arr.Length == 0)
                return;
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(arr.AsSpan());
            w.Flush();
            ms.Write(bytes.ToArray(), 0, bytes.Length);
        }

        static T[] ReadSection<T>(ReadOnlySpan<byte> s, ref int o, int count) where T : struct
        {
            o = Align16(o);
            var arr = new T[count];
            if (count == 0)
                return arr;
            Span<byte> dst = MemoryMarshal.AsBytes(arr.AsSpan());
            s.Slice(o, dst.Length).CopyTo(dst);
            o += dst.Length;
            return arr;
        }

        static void Pad16(MemoryStream ms, BinaryWriter w)
        {
            w.Flush();
            while (ms.Length % 16 != 0)
                ms.WriteByte(0);
            ms.Position = ms.Length;
        }

        static int Align16(int o) { return (o + 15) & ~15; }
        static uint ReadU32(ReadOnlySpan<byte> s, ref int o) { uint v = MemoryMarshal.Read<uint>(s.Slice(o)); o += 4; return v; }
        static int ReadI32(ReadOnlySpan<byte> s, ref int o) { int v = MemoryMarshal.Read<int>(s.Slice(o)); o += 4; return v; }

        /// <summary>
        /// Reads the leaf count straight from a blob header, no full parse —
        /// auto-mount sizes its defer-renderers decision BEFORE construction.
        /// -1 when the bytes are not a current-version FQS blob.
        /// </summary>
        public static int PeekLeafCount(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 44)
                return -1;
            var s = new ReadOnlySpan<byte>(bytes);
            int o = 0;
            if (ReadU32(s, ref o) != Magic || ReadU32(s, ref o) != FormatVersion)
                return -1;
            o += 12; //fuiHash + flags
            o += 8;  //quad + seg counts
            int n = ReadI32(s, ref o);
            return n >= 0 && n <= (1 << 24) ? n : -1;
        }

        /// <summary>FNV-1a 64 over a byte payload — the fui source-staleness gate.</summary>
        public static ulong Hash(byte[] bytes)
        {
            return Hash(bytes, 0, bytes != null ? bytes.Length : 0);
        }

        /// <summary>
        /// The value stored in a blob's fuiHash: the owning package's source
        /// hash CHAINED WITH the source hash of every other package the blob
        /// references.
        ///
        /// A blob is a flattened quad list of the whole subtree, so a component
        /// from another package contributes baked geometry and atlas UVs to it,
        /// and ResolveTex records that package in the texRef table. Gating on
        /// the owning package alone therefore misses the case where a
        /// DEPENDENCY is re-published: its atlas repacks, the mount resolves
        /// the new texture live, and the frozen quads keep sampling the old
        /// rects — a button rendering a neighbouring sprite's pixels, with the
        /// gate satisfied and nothing warning.
        ///
        /// Deterministic on both sides: distinct package ids, ordinal-sorted.
        /// A referenced package that is not loaded contributes 0, which cannot
        /// match a bake taken while it was loaded — refusing is the safe
        /// direction. ownerHash == 0 (a programmatic bake, NoSourceHash) stays
        /// 0: there is no expectation to combine with.
        ///
        /// Residual, stated so it is not mistaken for covered: a dependency
        /// that contributes only UNTEXTURED leaves (all-shape components) leaves
        /// no Package texRef and so stays outside this chain.
        /// </summary>
        public static ulong CombineWithReferences(ulong ownerHash, TexRef[] refs, Func<string, ulong> pkgHash)
        {
            if (ownerHash == 0 || refs == null)
                return ownerHash;
            //HashSet, not List.Contains: this runs inside FqsMount.Mount BEFORE
            //any structural validation, and Read admits a texRef count the blob
            //itself chooses — a linear-scan dedupe would turn a corrupt or
            //hostile blob into an O(n^2) main-thread hang instead of the cheap
            //refusal it is supposed to be
            HashSet<string> seen = null;
            List<string> ids = null;
            for (int i = 0; i < refs.Length; i++)
            {
                if (refs[i].kind != TexRefKind.Package)
                    continue;
                int slash = refs[i].url.IndexOf('/');
                if (slash <= 0)
                    continue;
                string pid = refs[i].url.Substring(0, slash);
                if (seen == null)
                {
                    seen = new HashSet<string>(StringComparer.Ordinal);
                    ids = new List<string>();
                }
                if (seen.Add(pid))
                    ids.Add(pid);
            }
            if (ids == null)
                return ownerHash;
            ids.Sort(StringComparer.Ordinal);
            ulong h = ownerHash;
            for (int i = 0; i < ids.Count; i++)
            {
                ulong ph = pkgHash(ids[i]);
                for (int b = 0; b < 8; b++)
                    h = (h ^ ((ph >> (b * 8)) & 0xFF)) * 0x100000001b3UL;
            }
            return h;
        }

        /// <summary>Package hash lookup used by CombineWithReferences: 0 when the package is gone.</summary>
        public static ulong LivePackageHash(string pkgId)
        {
            UIPackage p = UIPackage.GetById(pkgId);
            return p != null ? p.sourceHash : 0UL;
        }

        /// <summary>
        /// FNV-1a 64 over a byte RANGE. UIPackage hashes its descriptor through
        /// this so every load path (Resources, AssetBundle, raw bytes) produces
        /// the same gate value for the same descriptor content.
        /// </summary>
        public static ulong Hash(byte[] bytes, int offset, int count)
        {
            ulong h = 0xcbf29ce484222325UL;
            if (bytes == null)
                return h;
            int end = offset + count;
            for (int i = offset; i < end; i++)
                h = (h ^ bytes[i]) * 0x100000001b3UL;
            return h;
        }
    }

    //layout-as-ABI: these records are frozen once shipped (FQS formatVersion 1).
    //All fields are 4/8-byte primitives so Sequential layout has no padding
    //surprises across platforms.
    [StructLayout(LayoutKind.Sequential)]
    public struct FqsSegRecord
    {
        public int start;
        public int count;
        public int runIndex;
        public float z;
        public int tex0, tex1, tex2, tex3; //-> texRef table, -1 = unused slot
    }

    //mount-time recovery (decided for v1, do not add fields): texIndexBits are
    //bits 16-17 of quads[start].flags (uniform across a leaf range incl. slack),
    //segIndex is recovered by segment start/count containment.
    [StructLayout(LayoutKind.Sequential)]
    public struct FqsLeafRecord
    {
        public ulong pathHash;   //child-index chain FNV from the baked root
        public int start;
        public int count;
        public uint flags;       //LeafRange.flags | KindSdf/KindCurve marker bits
        public uint clipIndex;
        public uint slotIndex;
        public float bakedAlpha;
        public int liveCount;
        public uint _pad0;       //explicit: freezes the 8-align tail hole in the ABI

        public const uint KindSdf = 1u << 30;
        public const uint KindCurve = 1u << 29;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FqsClipRecord
    {
        public Vector4 rect;     //folded, root-space (entry 0 = the None sentinel)
        public Vector4 soft;
        public uint parentIndex;
        public uint slotIndex;
        public ulong ownerPathHash; //0 for the sentinel
    }

    //bake-time capture DTOs filled by InstancedUIStream._CaptureBakeSnapshot
    internal struct FqsSegSnap
    {
        public int start, count, runIndex;
        public float z;
        public Texture t0, t1, t2, t3;
    }

    internal struct FqsLeafSnap
    {
        public NGraphics graphics;
        public int start, count, liveCount;
        public uint flags, clipIndex, slotIndex;
        public float bakedAlpha;
        public bool sdf, curve;
    }

    internal struct FqsClipSnap
    {
        public Vector4 rect, soft;
        public uint parentIndex, slotIndex;
        public Container owner;
    }

    /// <summary>
    /// M8-1 baker: runs a replica-mode InstancedUIStream Extract on a live,
    /// already-updated container and freezes the compiled state into an FQS1
    /// blob. Strict scope (charter M8-1): components with fallback barriers,
    /// masked/painting/GoWrapper subtrees, atlas-font text or curve text are
    /// REFUSED (GoWrapper used to be a silent skip: the blob baked WITHOUT the
    /// wrapped content — scope barriers made it countable, so it now refuses) —
    /// never bake something the stream could not fully express, and never
    /// bake session-dependent data (dynamic font atlas UVs, curve glyph ids).
    /// </summary>
    public static class FqsBaker
    {
        /// <summary>
        /// Bakes root's subtree. The container must be on stage with meshes
        /// built (Stage.ForceUpdate first). Returns null with a reason when the
        /// content is outside the bakeable subset.
        /// </summary>
        public static byte[] Bake(Container root, ulong fuiHash, out string refuseReason)
        {
            return Bake(root, fuiHash, out refuseReason, false);
        }

        /// <summary>
        /// allowExternalTextures: textures not resolvable to a package item bake
        /// as name-keyed External refs — session-dependent identity, only safe
        /// for programmatic/testing content whose mount supplies textures.
        /// </summary>
        /// <summary>
        /// M8-7: bake the SUPERSET — before extracting, every hidden object in
        /// the subtree is temporarily made visible (and restored afterwards), so
        /// controller pages, button states and designer-hidden content all get
        /// real quads in the blob. At splice the stateless visibility replay
        /// zeroes whatever the LIVE flags say is hidden, and a later show is a
        /// range rewrite instead of "absent from blob -> invalidate the mount".
        /// Without this, the first press of a mounted button (its down state is
        /// a controller page) silently degraded the whole mount to the runtime
        /// walk. Off = bake exactly what is visible, the pre-M8-7 behavior.
        /// </summary>
        public static bool supersetVisibility = true;

        public static byte[] Bake(Container root, ulong fuiHash, out string refuseReason, bool allowExternalTextures)
        {
            //the ROOT can be masked/painting itself (overflow!=visible sets the
            //mask on the very container we bake) — Extract only counts CHILD
            //scopes, so check here or a stencil-masked component bakes unmasked
            if (root.mask != null || root._paintingMode > 0)
            { refuseReason = "root mask/painting scope: stencil content cannot replay from the stream"; return null; }

            //dynamic-object refusal must not depend on font-atlas warmth or the
            //frame a single update landed on: a cold atlas leaves text MESHES
            //empty (leaf never enters the stream) and a MovieClip bakes whatever
            //frame it showed — the PRESENCE of the object means runtime state
            if (FindDynamicObject(root) is string dynWhere)
            { refuseReason = dynWhere + ": content is runtime state"; return null; }

            //a bake is ground truth from the runtime walk: any mount (or armed
            //blob) in the subtree would SPLICE old quads into the new blob.
            //Detach them for the extract and put them back on every exit path
            //— Bake may be called on a live tree the caller still renders.
            var savedMounts = new List<KeyValuePair<Container, FqsMount>>();
            var savedPending = new List<KeyValuePair<Container, FqsAutoMount.Pending>>();
            DetachMounts(root, savedMounts, savedPending);
            //superset: unhide everything for the extract, restore EXACTLY the
            //objects we touched. The property setter (not the raw flag) both
            //ways: SetActive must fire so the next update builds the meshes,
            //and the restore must retrace the same side effects. renderingOrder
            //is per-frame transient state, so the toggles leave nothing behind
            //— rebaking the same instance is byte-identical (asserted by the
            //station suite).
            var unhidden = new List<DisplayObject>();
            if (supersetVisibility)
            {
                CollectHidden(root, unhidden);
                for (int i = 0; i < unhidden.Count; i++)
                    unhidden[i].visible = true;
                if (unhidden.Count > 0 && Stage.inst != null)
                    Stage.inst.ForceUpdate(); //build the newly-visible meshes
            }
            try
            {
                return BakeCore(root, fuiHash, out refuseReason, allowExternalTextures);
            }
            finally
            {
                for (int i = 0; i < unhidden.Count; i++)
                    unhidden[i].visible = false;
                for (int i = 0; i < savedMounts.Count; i++)
                    savedMounts[i].Key._fqsMount = savedMounts[i].Value;
                for (int i = 0; i < savedPending.Count; i++)
                    savedPending[i].Key._fqsPending = savedPending[i].Value;
            }
        }

        static void CollectHidden(Container c, List<DisplayObject> outList)
        {
            int cnt = c.numChildren;
            for (int i = 0; i < cnt; i++)
            {
                DisplayObject child = c.GetChildAt(i);
                if (!child.visible)
                    outList.Add(child);
                if (child is Container cc)
                    CollectHidden(cc, outList);
            }
        }

        static byte[] BakeCore(Container root, ulong fuiHash, out string refuseReason, bool allowExternalTextures)
        {

            //pin the compile backend: the vertex path coarsens clip regions past
            //its uniform-array limit, and a bake must not depend on the editor's
            //rendering backend (the replica stream never renders anyway)
            bool savedForce = InstancedUIStream.forceVertexPath;
            InstancedUIStream.forceVertexPath = false;
            var stream = new InstancedUIStream(root, Vector2.zero, true, false);
            InstancedUIStream.forceVertexPath = savedForce;
            try
            {
                stream.Extract();

                if (stream.quadCount == 0)
                { refuseReason = "empty: no instanceable content"; return null; }
                //scope fallbacks first (they close a run too, so the generic
                //barrier check would misattribute their reason); GoWrapper is
                //in this count — before scope barriers it was a silent skip
                //and the blob baked without the wrapped content
                if (stream.lastMaskedSubtrees > 0)
                { refuseReason = "masked/painting/GoWrapper subtree(s): content missing from the stream"; return null; }
                if (stream.runCount > 1)
                { refuseReason = "fallback barrier(s): non-quad topology or non-normal blend"; return null; }

                var quads = new List<QuadInstance>();
                var segs = new List<FqsSegSnap>();
                var leaves = new List<FqsLeafSnap>();
                var clips = new List<FqsClipSnap>();
                stream._CaptureBakeSnapshot(quads, segs, leaves, clips);

                foreach (var l in leaves)
                {
                    if ((l.flags & QuadInstance.FlagAlphaTexture) != 0)
                    { refuseReason = "atlas-font text leaf (dynamic atlas UVs are session state)"; return null; }
                    if (l.curve)
                    { refuseReason = "curve text leaf (glyph indices are session state)"; return null; }
                }

                //path hashes: stable child-index chains, so M8-2 can rebind
                //baked leaves to live objects created from the same package
                var graphicsPaths = new Dictionary<NGraphics, ulong>();
                var containerPaths = new Dictionary<Container, ulong>();
                containerPaths[root] = FnvSeed;
                MapPaths(root, FnvSeed, graphicsPaths, containerPaths);

                var texRefs = new List<FqsBlob.TexRef>();
                var texIndex = new Dictionary<Texture, int>();
                string texRefuse = null;

                var d = new FqsBlob.Data
                {
                    formatVersion = FqsBlob.FormatVersion,
                    fuiHash = fuiHash,
                    flags = (fuiHash == 0 ? FqsBlob.BlobFlags.NoSourceHash : FqsBlob.BlobFlags.None)
                        | (FqsBlob.BlobFlags)FqsBlob.EncodeScaleLevel(GRoot.contentScaleLevel),
                    quads = quads.ToArray(),
                    segs = new FqsSegRecord[segs.Count],
                    leaves = new FqsLeafRecord[leaves.Count],
                    clips = new FqsClipRecord[clips.Count],
                };
                for (int i = 0; i < segs.Count; i++)
                {
                    var s = segs[i];
                    d.segs[i] = new FqsSegRecord
                    {
                        start = s.start, count = s.count, runIndex = s.runIndex, z = s.z,
                        tex0 = ResolveTex(s.t0, texRefs, texIndex, allowExternalTextures, ref texRefuse),
                        tex1 = ResolveTex(s.t1, texRefs, texIndex, allowExternalTextures, ref texRefuse),
                        tex2 = ResolveTex(s.t2, texRefs, texIndex, allowExternalTextures, ref texRefuse),
                        tex3 = ResolveTex(s.t3, texRefs, texIndex, allowExternalTextures, ref texRefuse),
                    };
                }
                if (texRefuse != null)
                { refuseReason = texRefuse; return null; }
                for (int i = 0; i < leaves.Count; i++)
                {
                    var l = leaves[i];
                    if (!graphicsPaths.TryGetValue(l.graphics, out ulong ph))
                    { refuseReason = "internal: leaf graphics not reachable by tree walk"; return null; }
                    d.leaves[i] = new FqsLeafRecord
                    {
                        pathHash = ph,
                        start = l.start, count = l.count, liveCount = l.liveCount,
                        flags = l.flags | (l.sdf ? FqsLeafRecord.KindSdf : 0),
                        clipIndex = l.clipIndex, slotIndex = l.slotIndex,
                        bakedAlpha = l.bakedAlpha,
                    };
                }
                for (int i = 0; i < clips.Count; i++)
                {
                    var c = clips[i];
                    ulong oph = 0;
                    if (c.owner != null && !containerPaths.TryGetValue(c.owner, out oph))
                    { refuseReason = "internal: clip owner not reachable by tree walk"; return null; }
                    d.clips[i] = new FqsClipRecord
                    {
                        rect = c.rect, soft = c.soft,
                        parentIndex = c.parentIndex, slotIndex = c.slotIndex,
                        ownerPathHash = oph,
                    };
                }
                d.texRefs = texRefs.ToArray();
                //gate on every package this blob froze geometry from, not just
                //the one that owns the component (see CombineWithReferences)
                d.fuiHash = FqsBlob.CombineWithReferences(d.fuiHash, d.texRefs, FqsBlob.LivePackageHash);

                refuseReason = null;
                return FqsBlob.Write(d);
            }
            finally
            {
                stream.Dispose();
            }
        }

        static string FindDynamicObject(Container c)
        {
            int cnt = c.numChildren;
            for (int i = 0; i < cnt; i++)
            {
                DisplayObject child = c.GetChildAt(i);
                if (child is TextField)
                    return "text object present ('" + child.gameObject.name + "')";
                if (child is MovieClip)
                    return "movieclip present ('" + child.gameObject.name + "')";
                if (child is Container cc)
                {
                    string r = FindDynamicObject(cc);
                    if (r != null)
                        return r;
                }
            }
            return null;
        }

        static void DetachMounts(Container c,
            List<KeyValuePair<Container, FqsMount>> mounts,
            List<KeyValuePair<Container, FqsAutoMount.Pending>> pending)
        {
            if (c._fqsMount != null)
            {
                mounts.Add(new KeyValuePair<Container, FqsMount>(c, c._fqsMount));
                c._fqsMount = null;
            }
            if (c._fqsPending != null)
            {
                pending.Add(new KeyValuePair<Container, FqsAutoMount.Pending>(c, c._fqsPending));
                c._fqsPending = null;
            }
            int cnt = c.numChildren;
            for (int i = 0; i < cnt; i++)
                if (c.GetChildAt(i) is Container cc)
                    DetachMounts(cc, mounts, pending);
        }

        internal const ulong FnvSeed = 0xcbf29ce484222325UL;

        static ulong FnvStep(ulong h, int v)
        {
            return (h ^ (uint)v) * 0x100000001b3UL;
        }

        internal static void MapPaths(Container c, ulong h,
            Dictionary<NGraphics, ulong> graphics, Dictionary<Container, ulong> containers)
        {
            int cnt = c.numChildren;
            for (int i = 0; i < cnt; i++)
            {
                DisplayObject child = c.GetChildAt(i);
                ulong ch = FnvStep(h, i);
                if (child.graphics != null)
                {
                    graphics[child.graphics] = ch;
                    if (child.graphics.subInstances != null)
                        for (int j = 0; j < child.graphics.subInstances.Count; j++)
                            graphics[child.graphics.subInstances[j]] = FnvStep(ch, 0x8000 + j);
                }
                if (child is Container cc)
                {
                    containers[cc] = ch;
                    MapPaths(cc, ch, graphics, containers);
                }
            }
        }

        static int ResolveTex(Texture t, List<FqsBlob.TexRef> refs, Dictionary<Texture, int> index,
            bool allowExternal, ref string refuse)
        {
            if (t == null)
                return -1;
            if (index.TryGetValue(t, out int idx))
                return idx;

            var r = new FqsBlob.TexRef { kind = FqsBlob.TexRefKind.External, url = t.name };
            if (NTexture.Empty != null && NTexture.Empty.nativeTexture == t)
                r = new FqsBlob.TexRef { kind = FqsBlob.TexRefKind.Empty, url = "" };
            else
            {
                //deterministic identity: match ATLAS items only (image items
                //share the atlas native texture and load lazily, which would
                //make the chosen url depend on session history)
                var pkgs = UIPackage.GetPackages();
                for (int p = 0; p < pkgs.Count && r.kind == FqsBlob.TexRefKind.External; p++)
                {
                    var items = pkgs[p].GetItems();
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        if (it.type != PackageItemType.Atlas)
                            continue;
                        if (it.texture == null)
                            pkgs[p].GetItemAsset(it);
                        if (it.texture != null && it.texture.nativeTexture == t)
                        {
                            r = new FqsBlob.TexRef
                            { kind = FqsBlob.TexRefKind.Package, url = pkgs[p].id + "/" + it.id };
                            break;
                        }
                    }
                }
                if (r.kind == FqsBlob.TexRefKind.External && !allowExternal)
                {
                    refuse = "texture '" + t.name + "' is not a package atlas: External refs are session state (pass allowExternalTextures for test content)";
                }
            }
            idx = refs.Count;
            refs.Add(r);
            index[t] = idx;
            return idx;
        }
    }
}
