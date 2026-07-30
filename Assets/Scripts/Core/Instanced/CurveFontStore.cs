using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// M9b: global store of curve-text font data. Parses quadratic outlines from a
    /// TrueType glyf table on demand (per character), bakes 8 horizontal bands per
    /// glyph, and owns the GPU buffers every stream segment binds: _CurvePts
    /// (3 float2 per quadratic), _CurveBandIdx (band curve lists) and _CurveGlyphs
    /// (per glyph: band table base is glyphIndex*8 into _CurveBands, bbox in em).
    ///
    /// Buffers rebuild lazily when new characters are baked; version lets renderers
    /// rebind. PoC-stage limits: one font (composites supported since batch 5; point-matching alignment still renders
    /// as empty), CFF fonts unsupported.
    /// </summary>
    public static class CurveFontStore
    {
        public class GlyphInfo
        {
            public int index = -1;      //-1: no outline (space/composite)
            public Vector4 bbox;        //em units
            public float advance;
        }

        const int Bands = 8;

        static byte[] sFontData;
        static string sFontPath;
        public static float unitsPerEm { get; private set; }
        public static float ascent { get; private set; }
        public static float descent { get; private set; }

        static readonly Dictionary<char, GlyphInfo> sGlyphs = new Dictionary<char, GlyphInfo>();
        static readonly List<Vector2> sPts = new List<Vector2>();
        static readonly List<Vector2Int> sBands = new List<Vector2Int>();
        static readonly List<uint> sBandIdx = new List<uint>();
        static readonly List<Vector4> sGlyphMeta = new List<Vector4>();

        static ComputeBuffer sPtsBuf, sBandsBuf, sBandIdxBuf, sGlyphBuf;
        static bool sDirty;

        /// <summary>Bumps whenever the GPU buffers are recreated.</summary>
        public static int version { get; private set; }

        public static bool loaded { get { return sFontData != null; } }

        public static void LoadFont(string ttfPath)
        {
            if (sFontPath == ttfPath && sFontData != null)
                return;
            sFontData = File.ReadAllBytes(ttfPath);
            sFontPath = ttfPath;
            sGlyphs.Clear();
            sPts.Clear();
            sBands.Clear();
            sBandIdx.Clear();
            sGlyphMeta.Clear();
            sDirty = true;
            ParseHeader();
        }

        public static GlyphInfo GetGlyph(char ch)
        {
            if (sGlyphs.TryGetValue(ch, out var g))
                return g;
            g = BakeGlyph(ch);
            sGlyphs[ch] = g;
            return g;
        }

        /// <summary>Binds the store's buffers; call per frame per property block.</summary>
        public static void Bind(MaterialPropertyBlock props)
        {
            EnsureBuffers();
            if (sPtsBuf == null)
                return;
            props.SetBuffer("_CurvePts", sPtsBuf);
            props.SetBuffer("_CurveBands", sBandsBuf);
            props.SetBuffer("_CurveBandIdx", sBandIdxBuf);
            props.SetBuffer("_CurveGlyphs", sGlyphBuf);
        }

        public static void EnsureBuffers()
        {
            if (!sDirty || sGlyphMeta.Count == 0)
                return;
            sDirty = false;
            ReleaseBuffers();
            sPtsBuf = new ComputeBuffer(Mathf.Max(sPts.Count, 1), 8);
            sPtsBuf.SetData(sPts);
            sBandsBuf = new ComputeBuffer(Mathf.Max(sBands.Count, 1), 8);
            sBandsBuf.SetData(sBands);
            sBandIdxBuf = new ComputeBuffer(Mathf.Max(sBandIdx.Count, 1), 4);
            sBandIdxBuf.SetData(sBandIdx);
            sGlyphBuf = new ComputeBuffer(Mathf.Max(sGlyphMeta.Count, 1), 16);
            sGlyphBuf.SetData(sGlyphMeta);
            version++;
        }

        public static void ReleaseBuffers()
        {
            sPtsBuf?.Release(); sPtsBuf = null;
            sBandsBuf?.Release(); sBandsBuf = null;
            sBandIdxBuf?.Release(); sBandIdxBuf = null;
            sGlyphBuf?.Release(); sGlyphBuf = null;
        }

        // ---------- TTF parsing (glyf quadratics; PoC origin: Examples/CurveTextPoC, archived in batch 4 — M9b integrated this path) ----------

        static int tHead, tLoca, tGlyf, tHmtx, tCmap4;
        static int sIndexToLoc, sNumHMetrics, sSegCount;

        static ushort U16(int o) { return (ushort)((sFontData[o] << 8) | sFontData[o + 1]); }
        static short S16(int o) { return (short)U16(o); }
        static uint U32(int o) { return ((uint)sFontData[o] << 24) | ((uint)sFontData[o + 1] << 16) | ((uint)sFontData[o + 2] << 8) | sFontData[o + 3]; }

        static void ParseHeader()
        {
            int numTables = U16(4);
            var tables = new Dictionary<string, int>();
            for (int i = 0; i < numTables; i++)
            {
                int rec = 12 + i * 16;
                tables[System.Text.Encoding.ASCII.GetString(sFontData, rec, 4)] = (int)U32(rec + 8);
            }
            tHead = tables["head"]; tLoca = tables["loca"]; tGlyf = tables["glyf"]; tHmtx = tables["hmtx"];
            unitsPerEm = U16(tHead + 18);
            sIndexToLoc = S16(tHead + 50);
            int hhea = tables["hhea"];
            ascent = S16(hhea + 4);
            descent = S16(hhea + 6);
            sNumHMetrics = U16(hhea + 34);

            int cmap = tables["cmap"];
            int count = U16(cmap + 2);
            tCmap4 = -1;
            for (int i = 0; i < count; i++)
            {
                int rec = cmap + 4 + i * 8;
                int platform = U16(rec);
                int encoding = U16(rec + 2);
                int off = (int)U32(rec + 4);
                if ((platform == 3 && (encoding == 1 || encoding == 10)) || platform == 0)
                {
                    if (U16(cmap + off) == 4) { tCmap4 = cmap + off; break; }
                }
            }
            sSegCount = tCmap4 >= 0 ? U16(tCmap4 + 6) / 2 : 0;
        }

        static int GlyphId(char ch)
        {
            if (tCmap4 < 0) return 0;
            int endBase = tCmap4 + 14, startBase = endBase + sSegCount * 2 + 2,
                deltaBase = startBase + sSegCount * 2, rangeBase = deltaBase + sSegCount * 2;
            for (int s = 0; s < sSegCount; s++)
            {
                if (ch <= U16(endBase + s * 2))
                {
                    int start = U16(startBase + s * 2);
                    if (ch < start) return 0;
                    int ro = U16(rangeBase + s * 2);
                    if (ro == 0)
                        return (ch + S16(deltaBase + s * 2)) & 0xFFFF;
                    int idx = rangeBase + s * 2 + ro + (ch - start) * 2;
                    int gid = U16(idx);
                    return gid == 0 ? 0 : (gid + S16(deltaBase + s * 2)) & 0xFFFF;
                }
            }
            return 0;
        }

        static GlyphInfo BakeGlyph(char ch)
        {
            var info = new GlyphInfo();
            int gid = GlyphId(ch);
            int hm = gid < sNumHMetrics ? gid : sNumHMetrics - 1;
            info.advance = U16(tHmtx + hm * 4);

            int go, gl;
            if (sIndexToLoc == 0)
            {
                go = U16(tLoca + gid * 2) * 2;
                gl = U16(tLoca + gid * 2 + 2) * 2 - go;
            }
            else
            {
                go = (int)U32(tLoca + gid * 4);
                gl = (int)U32(tLoca + gid * 4 + 4) - go;
            }
            if (gl <= 0)
                return info;
            int p = tGlyf + go;
            //the glyf header carries the bbox for simple AND composite glyphs
            Vector4 bbox = new Vector4(S16(p + 2), S16(p + 4), S16(p + 6), S16(p + 8));

            var curvePts = new List<Vector2>();
            if (!CollectOutline(gid, curvePts, 1, 0, 0, 1, 0, 0, 0) || curvePts.Count == 0)
                return info; //unsupported construct (point-matching align): ghost fallback

            //append to the shared tables: curves, then 8 bands over the bbox
            int curveBase = sPts.Count / 3;
            sPts.AddRange(curvePts);
            int nCurves = curvePts.Count / 3;
            float y0 = bbox.y, bh = Mathf.Max(bbox.w - bbox.y, 1);
            for (int b = 0; b < Bands; b++)
            {
                float by0 = y0 + bh * b / Bands, by1 = y0 + bh * (b + 1) / Bands;
                int s = sBandIdx.Count;
                for (int c = 0; c < nCurves; c++)
                {
                    float cy0 = Mathf.Min(curvePts[c * 3].y, Mathf.Min(curvePts[c * 3 + 1].y, curvePts[c * 3 + 2].y));
                    float cy1 = Mathf.Max(curvePts[c * 3].y, Mathf.Max(curvePts[c * 3 + 1].y, curvePts[c * 3 + 2].y));
                    if (cy1 >= by0 && cy0 <= by1)
                        sBandIdx.Add((uint)(curveBase + c));
                }
                sBands.Add(new Vector2Int(s, sBandIdx.Count - s));
            }
            info.index = sGlyphMeta.Count;
            info.bbox = bbox;
            sGlyphMeta.Add(bbox);
            sDirty = true;
            return info;
        }

        static float F2Dot14(int o)
        {
            return S16(o) / 16384f;
        }

        /// <summary>
        /// Collects a glyph's quadratic outline in compound space, recursing into
        /// composite components (batch 5): each component contributes its simple
        /// outline transformed by the accumulated affine (x' = a·x + c·y + e,
        /// y' = b·x + d·y + f). Offsets are compound-space (the common MS/Apple
        /// default); the rare point-matching alignment returns false → the caller
        /// keeps the native ghost fallback. Mirrored components flip contour
        /// orientation, which the nonzero winding rule absorbs.
        /// </summary>
        static bool CollectOutline(int gid, List<Vector2> outPts,
            float a, float b, float c, float d, float e, float f, int depth)
        {
            if (depth > 4)
                return false;
            int go, gl;
            if (sIndexToLoc == 0)
            {
                go = U16(tLoca + gid * 2) * 2;
                gl = U16(tLoca + gid * 2 + 2) * 2 - go;
            }
            else
            {
                go = (int)U32(tLoca + gid * 4);
                gl = (int)U32(tLoca + gid * 4 + 4) - go;
            }
            if (gl <= 0)
                return true; //empty component (e.g. space carrier) is fine
            int p = tGlyf + go;
            int contours = S16(p);

            if (contours >= 0)
            {
                bool identity = a == 1 && b == 0 && c == 0 && d == 1 && e == 0 && f == 0;
                int start = outPts.Count;
                ParseSimple(p, contours, outPts, out _);
                if (!identity)
                {
                    for (int i = start; i < outPts.Count; i++)
                    {
                        Vector2 pt = outPts[i];
                        outPts[i] = new Vector2(a * pt.x + c * pt.y + e, b * pt.x + d * pt.y + f);
                    }
                }
                return true;
            }

            var data = sFontData;
            int o = p + 10;
            while (true)
            {
                int flags = U16(o); o += 2;
                int cgid = U16(o); o += 2;
                float dx, dy;
                if ((flags & 0x0001) != 0) //ARG_1_AND_2_ARE_WORDS
                {
                    dx = S16(o); o += 2;
                    dy = S16(o); o += 2;
                }
                else
                {
                    dx = (sbyte)data[o]; o++;
                    dy = (sbyte)data[o]; o++;
                }
                if ((flags & 0x0002) == 0) //not ARGS_ARE_XY_VALUES: point matching
                    return false;

                float ca = 1, cb = 0, cc = 0, cd = 1;
                if ((flags & 0x0008) != 0) //WE_HAVE_A_SCALE
                {
                    ca = cd = F2Dot14(o); o += 2;
                }
                else if ((flags & 0x0040) != 0) //X_AND_Y_SCALE
                {
                    ca = F2Dot14(o); o += 2;
                    cd = F2Dot14(o); o += 2;
                }
                else if ((flags & 0x0080) != 0) //TWO_BY_TWO
                {
                    ca = F2Dot14(o); cb = F2Dot14(o + 2);
                    cc = F2Dot14(o + 4); cd = F2Dot14(o + 6);
                    o += 8;
                }

                //full = parent ∘ (component matrix, compound-space offset)
                float na = a * ca + c * cb, nb = b * ca + d * cb;
                float nc = a * cc + c * cd, nd = b * cc + d * cd;
                float ne = a * dx + c * dy + e, nf = b * dx + d * dy + f;
                if (!CollectOutline(cgid, outPts, na, nb, nc, nd, ne, nf, depth + 1))
                    return false;

                if ((flags & 0x0020) == 0) //MORE_COMPONENTS
                    break;
            }
            return true;
        }

        static void ParseSimple(int p, int contours, List<Vector2> outPts, out Vector4 bbox)
        {
            var d = sFontData;
            bbox = new Vector4(S16(p + 2), S16(p + 4), S16(p + 6), S16(p + 8));
            int o = p + 10;
            var ends = new int[contours];
            for (int i = 0; i < contours; i++) { ends[i] = U16(o); o += 2; }
            int nPts = ends[contours - 1] + 1;
            o += 2 + U16(o);

            var flags = new byte[nPts];
            for (int i = 0; i < nPts;)
            {
                byte f = d[o++];
                flags[i++] = f;
                if ((f & 8) != 0)
                {
                    int rep = d[o++];
                    for (int r = 0; r < rep; r++) flags[i++] = f;
                }
            }
            var xs = new short[nPts];
            var ys = new short[nPts];
            short v = 0;
            for (int i = 0; i < nPts; i++)
            {
                byte f = flags[i];
                if ((f & 2) != 0) { byte dx = d[o++]; v += (f & 16) != 0 ? dx : (short)-dx; }
                else if ((f & 16) == 0) { v += S16(o); o += 2; }
                xs[i] = v;
            }
            v = 0;
            for (int i = 0; i < nPts; i++)
            {
                byte f = flags[i];
                if ((f & 4) != 0) { byte dy = d[o++]; v += (f & 32) != 0 ? dy : (short)-dy; }
                else if ((f & 32) == 0) { v += S16(o); o += 2; }
                ys[i] = v;
            }

            int startPt = 0;
            for (int c = 0; c < contours; c++)
            {
                int endPt = ends[c];
                int n = endPt - startPt + 1;
                if (n < 2) { startPt = endPt + 1; continue; }

                var ring = new List<(Vector2 pt, bool on)>();
                for (int i = 0; i < n; i++)
                {
                    int idx = startPt + i;
                    ring.Add((new Vector2(xs[idx], ys[idx]), (flags[idx] & 1) != 0));
                }
                int first = ring.FindIndex(r => r.on);
                if (first < 0)
                {
                    ring.Insert(1, ((ring[0].pt + ring[1].pt) * 0.5f, true));
                    first = 1;
                }
                var seq = new List<(Vector2 pt, bool on)>();
                for (int i = 0; i <= ring.Count; i++)
                    seq.Add(ring[(first + i) % ring.Count]);

                Vector2 cur = seq[0].pt;
                int k = 1;
                while (k < seq.Count)
                {
                    if (seq[k].on)
                    {
                        outPts.Add(cur); outPts.Add((cur + seq[k].pt) * 0.5f); outPts.Add(seq[k].pt);
                        cur = seq[k].pt;
                        k++;
                    }
                    else
                    {
                        Vector2 ctrl = seq[k].pt;
                        Vector2 next;
                        bool implied = k + 1 < seq.Count && !seq[k + 1].on;
                        if (implied)
                            next = (ctrl + seq[k + 1].pt) * 0.5f;
                        else if (k + 1 < seq.Count)
                            next = seq[k + 1].pt;
                        else
                            next = seq[0].pt;
                        outPts.Add(cur); outPts.Add(ctrl); outPts.Add(next);
                        cur = next;
                        k += implied ? 1 : 2;
                    }
                }
                startPt = endPt + 1;
            }
        }
    }
}
