#if UNITY_2020_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M8-1 acceptance suite (15 checks), rebuilt from commit a7cfb0e's record:
/// the FQS1 blob format and the strict baker. Bitwise quad parity against a
/// live Extract, byte-identical re-bakes, hostile-input rejection as clean
/// InvalidDataException BEFORE any allocation (the type M8-2's loader catches
/// to fall back to the runtime walk), the full refusal matrix (root
/// mask/painting, PRESENCE of text/MovieClip, fallback barriers, masked child
/// subtrees, non-package textures), and the SDF kind marker.
///
/// Data-level only — no pixels — so it is the cheapest suite to run.
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedM81Suite
{
    //FQS1 header: magic(4) version(4) fuiHash(8) flags(4) then five int counts
    const int kOffVersion = 4;
    const int kOffQuadCount = 20;

    static bool Rejects(byte[] blob, Action<byte[]> tamper)
    {
        var copy = (byte[])blob.Clone();
        tamper(copy);
        try
        {
            FqsBlob.Read(copy);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    static bool SameQuads(QuadInstance[] a, IList<QuadInstance> b)
    {
        if (a == null || b == null || a.Length != b.Count)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            QuadInstance x = a[i], y = b[i];
            if (x.rect != y.rect || x.uvA != y.uvA || x.uvB != y.uvB
                || x.color != y.color || x.flags != y.flags || x.padding != y.padding
                || x.clipIndex != y.clipIndex || x.transformIndex != y.transformIndex)
                return false;
        }
        return true;
    }

    static byte[] BakeOf(GComponent comp, out string reason, ulong hash = 0, bool external = false)
    {
        return FqsBaker.Bake(InstancedValidationEnv.C(comp), hash, out reason, external);
    }

    public static string Run()
    {
        var env = new InstancedValidationEnv();
        bool prevLog = Debug.unityLogger.logEnabled;
        try
        {
            //--- bakeable reference content: shapes only (NTexture.Empty) ----
            GComponent bakeable = new GComponent();
            bakeable.SetSize(200, 140);
            env.root.AddChild(bakeable);
            bakeable.SetXY(20, 20);
            env.Rect(bakeable, 5, 5, 60, 40, Color.red);
            env.Rect(bakeable, 70, 5, 60, 40, Color.green);
            var rr = new GGraph();
            rr.DrawRoundRect(80, 40, Color.blue, new float[] { 10, 10, 10, 10 });
            bakeable.AddChild(rr);
            rr.SetXY(5, 55);
            GComponent inner = new GComponent();
            inner.SetSize(90, 40);
            bakeable.AddChild(inner);
            inner.SetXY(100, 55);
            InstancedValidationEnv.C(inner).clipRect = new UnityEngine.Rect(0, 0, 90, 40);
            env.Rect(inner, 0, 0, 140, 30, Color.yellow);
            env.Step(2);

            //--- b1: the baker accepts shape-only content --------------------
            string reason;
            byte[] blob = BakeOf(bakeable, out reason);
            env.Check($"b1.bake accepts shape-only content (reason={reason ?? "none"})",
                blob != null && reason == null && blob.Length > 44);
            if (blob == null)
                return env.Report();

            //--- b2: blob round-trips ---------------------------------------
            FqsBlob.Data d = FqsBlob.Read(blob);
            env.Check($"b2.blob round-trips (quads={d.quads.Length} segs={d.segs.Length} leaves={d.leaves.Length} clips={d.clips.Length} tex={d.texRefs.Length})",
                d.quads.Length > 0 && d.segs.Length > 0 && d.leaves.Length == 4
                && d.clips.Length >= 2 && d.texRefs.Length >= 1);

            //--- b3: bitwise quad parity against a live Extract --------------
            bool savedForce = InstancedUIStream.forceVertexPath;
            InstancedUIStream.forceVertexPath = false; //the baker pins the buffer path
            var live = new InstancedUIStream(InstancedValidationEnv.C(bakeable), Vector2.zero, true, false);
            InstancedUIStream.forceVertexPath = savedForce;
            var liveQuads = new List<QuadInstance>();
            try
            {
                live.Extract();
                live.CopyQuadsForDiagnostics(liveQuads);
            }
            finally { live.Dispose(); }
            env.Check($"b3.bitwise quad parity vs live Extract ({d.quads.Length} vs {liveQuads.Count})",
                SameQuads(d.quads, liveQuads));

            //--- b4: byte-identical re-bake ---------------------------------
            byte[] blob2 = BakeOf(bakeable, out reason);
            bool identical = blob2 != null && blob2.Length == blob.Length;
            for (int i = 0; identical && i < blob.Length; i++)
                identical &= blob[i] == blob2[i];
            env.Check("b4.re-bake is byte-identical", identical);

            //--- b5: header fields and the staleness flag -------------------
            byte[] hashed = BakeOf(bakeable, out reason, 0xABCDEF1234567890UL);
            FqsBlob.Data dh = hashed != null ? FqsBlob.Read(hashed) : null;
            env.Check("b5.header carries version/hash; no-hash bakes flag NoSourceHash",
                d.formatVersion == FqsBlob.FormatVersion
                && (d.flags & FqsBlob.BlobFlags.NoSourceHash) != 0 && d.fuiHash == 0
                && dh != null && dh.fuiHash == 0xABCDEF1234567890UL
                && (dh.flags & FqsBlob.BlobFlags.NoSourceHash) == 0);

            //--- b6-b10: hostile input rejected as InvalidDataException -----
            env.Check("b6.bad magic rejected", Rejects(blob, b => b[0] ^= 0xFF));
            env.Check("b7.unknown format version rejected",
                Rejects(blob, b => BitConverter.GetBytes(99u).CopyTo(b, kOffVersion)));
            env.Check("b8.truncated blob rejected", Rejects(blob, b => { }) == false
                && Rejects(blob.AsSpan(0, blob.Length / 2).ToArray(), b => { }));
            env.Check("b9.hostile counts rejected (negative and oversized)",
                Rejects(blob, b => BitConverter.GetBytes(-1).CopyTo(b, kOffQuadCount))
                && Rejects(blob, b => BitConverter.GetBytes(1 << 25).CopyTo(b, kOffQuadCount))
                //in-range but larger than the payload: caught by the size ladder
                && Rejects(blob, b => BitConverter.GetBytes(d.quads.Length + 64).CopyTo(b, kOffQuadCount)));
            var padded = new byte[blob.Length + 8];
            blob.CopyTo(padded, 0);
            env.Check("b10.trailing bytes rejected", Rejects(padded, b => { }));

            //--- b11/b12: PRESENCE of dynamic objects refuses ---------------
            //(flag-based checks were nondeterministic under font-atlas warmth)
            GTextField tf = env.Text(bakeable, 5, 100, 120, 30, "");
            env.Step(2);
            byte[] withEmptyText = BakeOf(bakeable, out reason);
            string emptyTextReason = reason;
            tf.text = "hi";
            env.Step(2);
            byte[] withText = BakeOf(bakeable, out reason);
            env.Check($"b11.text object PRESENCE refuses, even with empty text [{emptyTextReason}]",
                withEmptyText == null && withText == null
                && emptyTextReason != null && emptyTextReason.Contains("text object present"));
            tf.Dispose();
            env.Step(1);

            var mc = new MovieClip();
            InstancedValidationEnv.C(bakeable).AddChild(mc);
            env.Step(2);
            byte[] withMc = BakeOf(bakeable, out reason);
            env.Check($"b12.movieclip presence refuses [{reason}]",
                withMc == null && reason != null && reason.Contains("movieclip present"));
            mc.Dispose();
            env.Step(2);

            //--- b13: root mask/painting scope refuses ----------------------
            bakeable.filter = new ColorFilter();
            env.Step(2);
            byte[] painted = BakeOf(bakeable, out reason);
            string paintReason = reason;
            bakeable.filter = null;
            env.Step(2);
            env.Check($"b13.root painting/mask scope refuses [{paintReason}]",
                painted == null && paintReason != null
                && paintReason.Contains("root mask/painting"));

            //--- b14: fallback barriers and masked child subtrees refuse ----
            var poly = new GGraph();
            poly.DrawPolygon(50, 40, new[] {
                new Vector2(25, 0), new Vector2(50, 30), new Vector2(0, 30) }, Color.cyan);
            bakeable.AddChild(poly);
            poly.SetXY(140, 5);
            env.Step(2);
            byte[] withPoly = BakeOf(bakeable, out reason);
            string polyReason = reason;
            poly.Dispose();
            env.Step(2);

            GComponent maskedChild = new GComponent();
            maskedChild.SetSize(60, 40);
            bakeable.AddChild(maskedChild);
            maskedChild.SetXY(5, 100);
            var maskShape = env.Rect(maskedChild, 0, 0, 60, 40, Color.white);
            InstancedValidationEnv.C(maskedChild).mask = maskShape.displayObject;
            env.Rect(maskedChild, 0, 0, 60, 40, Color.red);
            env.Step(2);
            byte[] withMask = BakeOf(bakeable, out reason);
            string maskReason = reason;
            maskedChild.Dispose();
            env.Step(2);
            env.Check($"b14.fallback barrier [{polyReason}] and masked subtree [{maskReason}] refuse",
                withPoly == null && polyReason != null && polyReason.Contains("fallback barrier")
                && withMask == null && maskReason != null && maskReason.Contains("masked"));

            //--- b15: SDF kind marker + external-texture policy -------------
            byte[] fresh = BakeOf(bakeable, out reason);
            FqsBlob.Data df = fresh != null ? FqsBlob.Read(fresh) : null;
            int sdfLeaves = 0;
            if (df != null)
            {
                foreach (var l in df.leaves)
                    if ((l.flags & FqsLeafRecord.KindSdf) != 0) sdfLeaves++;
            }
            //a created (non-package) texture is session state: refused by default
            Image img;
            env.ImageLeaf(bakeable, 140, 5, new Color32(255, 0, 255, 255), out img);
            env.Step(2);
            Debug.unityLogger.logEnabled = false;
            byte[] extDefault = BakeOf(bakeable, out reason);
            string extReason = reason;
            byte[] extAllowed = BakeOf(bakeable, out reason, 0, true);
            Debug.unityLogger.logEnabled = prevLog;
            env.Check($"b15.SDF kind marker ({sdfLeaves} leaf) + external texture refused unless opted in [{extReason}]",
                sdfLeaves == 1
                && extDefault == null && extReason != null && extReason.Contains("not a package atlas")
                && extAllowed != null);
        }
        finally
        {
            Debug.unityLogger.logEnabled = prevLog;
            env.Dispose();
        }
        return env.Report();
    }
}
#endif
