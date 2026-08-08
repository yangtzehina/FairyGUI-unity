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

        //batch 5 data textures: the four tables live in RGBAFloat textures so
        //every backend (incl. WebGL2/GLES3, no SSBO anywhere) fetches them with
        //texelFetch. Layout, addressed as linear texel index i -> (i & 1023,
        //i >> 10) at width 1024:
        //  pts:     2 texels per curve  — (A.x A.y B.x B.y), (C.x C.y - -)
        //  bands:   1 texel per band    — (start count - -), glyph*8 + band
        //  bandIdx: 4 indices per texel — index i at texel i>>2, channel i&3
        //  glyphs:  1 texel per glyph   — banding bbox
        const int TexW = 1024;
        static Texture2D sPtsTex, sBandsTex, sBandIdxTex, sGlyphTex;
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
            sUpCurves = sUpBands = sUpIdx = sUpGlyphs = 0;
            try
            {
                ParseHeader();
            }
            catch
            {
                //a half-parsed font must not read as loaded: HasChar and
                //every consumer key off sFontData
                sFontData = null;
                sFontPath = null;
                throw;
            }
        }

        /// <summary>
        /// Pure cmap probe: does this font map the character? No baking, no
        /// allocation — TextField font-fallback probes through this, and the
        /// old constant-true answer suppressed fallback for every missing
        /// character (audit: rendered .notdef or nothing).
        /// </summary>
        public static bool HasChar(char ch)
        {
            return sFontData != null && GlyphId(ch) != 0;
        }

        public static GlyphInfo GetGlyph(char ch)
        {
            if (sGlyphs.TryGetValue(ch, out var g))
                return g;
            g = BakeGlyph(ch);
            sGlyphs[ch] = g;
            return g;
        }

        /// <summary>Kept for callers that bound per-property-block buffers before
        /// the data-texture backend; the tables are globals now.</summary>
        public static void Bind(MaterialPropertyBlock props)
        {
            EnsureBuffers();
        }

        //incremental upload state (audit: every new glyph re-filled and
        //re-uploaded all four tables — O(n^2) accumulated upload, a 3-4MB
        //frame spike once thousands of CJK glyphs were live). The tables are
        //append-only (LoadFont resets everything), so each EnsureBuffers is
        //either "tail entries appended" (region copy of the touched rows) or
        //"texture grew" (full upload, amortized by doubling growth).
        static int sUpCurves, sUpBands, sUpIdx, sUpGlyphs;
        static float[] sPtsData, sBandsData, sIdxData, sGlyphData;
        static Texture2D sStagingTex;
        static int sCanRegionCopy = -1;

        static bool CanRegionCopy
        {
            get
            {
                if (sCanRegionCopy < 0)
                    sCanRegionCopy = (SystemInfo.copyTextureSupport
                        & UnityEngine.Rendering.CopyTextureSupport.Basic) != 0 ? 1 : 0;
                return sCanRegionCopy == 1;
            }
        }

        /// <summary>
        /// Texture + CPU mirror sized for texels, height grown by doubling so
        /// full re-uploads amortize to O(log n). True = recreated (caller must
        /// refill from 0 and full-upload).
        /// </summary>
        static bool EnsureTexAndData(ref Texture2D tex, ref float[] data, int texels)
        {
            int h = Mathf.Max(1, (texels + TexW - 1) / TexW);
            bool recreate = tex == null || tex.height < h;
            if (recreate)
            {
                if (tex != null)
                    UnityEngine.Object.Destroy(tex);
                h = Mathf.NextPowerOfTwo(h);
                tex = new Texture2D(TexW, h, TextureFormat.RGBAFloat, false, true);
                tex.name = "CurveFontTable";
                tex.hideFlags = HideFlags.DontSave;
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                data = new float[TexW * h * 4];
            }
            return recreate;
        }

        /// <summary>
        /// Ships [fromTexel, toTexel) to the GPU: whole texture when it was
        /// recreated (or the device cannot CopyTexture), else only the touched
        /// rows via a staging texture region copy.
        /// </summary>
        static void Push(Texture2D tex, float[] data, bool full, int fromTexel, int toTexel)
        {
            if (toTexel <= fromTexel && !full)
                return;
            if (full || !CanRegionCopy)
            {
                tex.SetPixelData(data, 0);
                tex.Apply(false, false);
                return;
            }
            int r0 = fromTexel / TexW;
            int rows = (toTexel - 1) / TexW - r0 + 1;
            if (sStagingTex == null || sStagingTex.height < rows)
            {
                if (sStagingTex != null)
                    UnityEngine.Object.Destroy(sStagingTex);
                sStagingTex = new Texture2D(TexW, Mathf.NextPowerOfTwo(rows), TextureFormat.RGBAFloat, false, true);
                sStagingTex.name = "CurveFontStaging";
                sStagingTex.hideFlags = HideFlags.DontSave;
            }
            //SetPixelData reads staging-texture-sized elements from data at the
            //row offset — no intermediate scratch; rows beyond our range carry
            //garbage in staging but only [0, rows) is region-copied out
            sStagingTex.SetPixelData(data, 0, r0 * TexW * 4);
            sStagingTex.Apply(false, false);
            Graphics.CopyTexture(sStagingTex, 0, 0, 0, 0, TexW, rows, tex, 0, 0, 0, r0);
        }

        /// <summary>(Re)builds the data textures when new glyphs were baked.</summary>
        public static void EnsureBuffers()
        {
            if (!sDirty || sGlyphMeta.Count == 0)
                return;
            sDirty = false;

            int nCurves = sPts.Count / 3;
            bool full = EnsureTexAndData(ref sPtsTex, ref sPtsData, Mathf.Max(nCurves * 2, 1));
            int from = full ? 0 : sUpCurves;
            for (int c = from; c < nCurves; c++)
            {
                int b = c * 8;
                sPtsData[b + 0] = sPts[c * 3].x;     sPtsData[b + 1] = sPts[c * 3].y;
                sPtsData[b + 2] = sPts[c * 3 + 1].x; sPtsData[b + 3] = sPts[c * 3 + 1].y;
                sPtsData[b + 4] = sPts[c * 3 + 2].x; sPtsData[b + 5] = sPts[c * 3 + 2].y;
            }
            Push(sPtsTex, sPtsData, full, from * 2, nCurves * 2);
            sUpCurves = nCurves;

            full = EnsureTexAndData(ref sBandsTex, ref sBandsData, Mathf.Max(sBands.Count, 1));
            from = full ? 0 : sUpBands;
            for (int i = from; i < sBands.Count; i++)
            {
                sBandsData[i * 4] = sBands[i].x;
                sBandsData[i * 4 + 1] = sBands[i].y;
            }
            Push(sBandsTex, sBandsData, full, from, sBands.Count);
            sUpBands = sBands.Count;

            full = EnsureTexAndData(ref sBandIdxTex, ref sIdxData, Mathf.Max((sBandIdx.Count + 3) / 4, 1));
            //indices pack 4 per texel: re-fill from the texel boundary so a
            //partially-filled texel is rewritten whole
            from = full ? 0 : sUpIdx & ~3;
            for (int i = from; i < sBandIdx.Count; i++)
                sIdxData[i] = sBandIdx[i];
            Push(sBandIdxTex, sIdxData, full, from / 4, (sBandIdx.Count + 3) / 4);
            sUpIdx = sBandIdx.Count;

            full = EnsureTexAndData(ref sGlyphTex, ref sGlyphData, Mathf.Max(sGlyphMeta.Count, 1));
            from = full ? 0 : sUpGlyphs;
            for (int i = from; i < sGlyphMeta.Count; i++)
            {
                sGlyphData[i * 4] = sGlyphMeta[i].x;     sGlyphData[i * 4 + 1] = sGlyphMeta[i].y;
                sGlyphData[i * 4 + 2] = sGlyphMeta[i].z; sGlyphData[i * 4 + 3] = sGlyphMeta[i].w;
            }
            Push(sGlyphTex, sGlyphData, full, from, sGlyphMeta.Count);
            sUpGlyphs = sGlyphMeta.Count;

            version++;
            //one set of globals serves all three consumers: the buffer-path
            //instance shader, the vertex-stream shader and FairyGUI/CurveText
            Shader.SetGlobalTexture("_CurvePtsTex", sPtsTex);
            Shader.SetGlobalTexture("_CurveBandsTex", sBandsTex);
            Shader.SetGlobalTexture("_CurveBandIdxTex", sBandIdxTex);
            Shader.SetGlobalTexture("_CurveGlyphsTex", sGlyphTex);
        }

        public static void ReleaseBuffers()
        {
            if (sPtsTex != null) { UnityEngine.Object.Destroy(sPtsTex); sPtsTex = null; }
            if (sBandsTex != null) { UnityEngine.Object.Destroy(sBandsTex); sBandsTex = null; }
            if (sBandIdxTex != null) { UnityEngine.Object.Destroy(sBandIdxTex); sBandIdxTex = null; }
            if (sGlyphTex != null) { UnityEngine.Object.Destroy(sGlyphTex); sGlyphTex = null; }
            if (sStagingTex != null) { UnityEngine.Object.Destroy(sStagingTex); sStagingTex = null; }
            sPtsData = sBandsData = sIdxData = sGlyphData = null;
            sUpCurves = sUpBands = sUpIdx = sUpGlyphs = 0;
            sDirty = true; //next EnsureBuffers rebuilds from the lists
        }

        /// <summary>
        /// Resident bytes of the store: the raw TTF, the four GPU tables and
        /// their CPU mirrors (audit: the panel had counts, never bytes).
        /// </summary>
        public static long residentBytes
        {
            get
            {
                long n = sFontData != null ? sFontData.Length : 0;
                if (sPtsTex != null) n += (long)sPtsTex.width * sPtsTex.height * 16 * 2;
                if (sBandsTex != null) n += (long)sBandsTex.width * sBandsTex.height * 16 * 2;
                if (sBandIdxTex != null) n += (long)sBandIdxTex.width * sBandIdxTex.height * 16 * 2;
                if (sGlyphTex != null) n += (long)sGlyphTex.width * sGlyphTex.height * 16 * 2;
                return n;
            }
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
            //CFF/OTF fonts (Source Han/Noto Sans SC among them) carry CFF
            //outlines, no glyf table — refuse with the actionable message
            //instead of a KeyNotFoundException three fields later (audit)
            if (!tables.ContainsKey("glyf"))
                throw new IOException(
                    "CurveFontStore: '" + sFontPath + "' has no glyf table — CFF/OTF outlines are "
                    + "unsupported, a TrueType (.ttf with quadratic outlines) face is required.");
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
