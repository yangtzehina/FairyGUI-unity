#if UNITY_2020_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using FairyGUI;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// M9a: Slug-style curve text PoC (Lengyel's algorithm, public domain since
/// 2026-03; simplified). Parses quadratic outlines straight from a TrueType
/// font's glyf table, bakes per-glyph horizontal band lists, and renders
/// glyphs as quads whose fragment coverage is computed analytically from the
/// curves — no atlas, resolution-independent at any scale.
///
/// PoC scope: buffer backend only (editor/desktop), simple glyphs (composites
/// and CFF fonts fall out), winding by quadratic root solving + sampled
/// nearest-distance AA. Stream integration (flags bit + per-instance
/// glyphIndex) is M9b.
/// </summary>
public class CurveTextPoC : IDisposable
{
    // ---------- TTF parsing ----------

    public class Glyph
    {
        public List<Vector2> pts = new List<Vector2>(); //3 points per quadratic (p0, ctrl, p1), em units
        public Vector4 bbox;                            //xMin yMin xMax yMax
        public float advance;
    }

    public class Font
    {
        public float unitsPerEm;
        public float ascent;
        public readonly Dictionary<char, Glyph> glyphs = new Dictionary<char, Glyph>();
        public int compositesSkipped;
    }

    static ushort U16(byte[] d, int o) { return (ushort)((d[o] << 8) | d[o + 1]); }
    static short S16(byte[] d, int o) { return (short)U16(d, o); }
    static uint U32(byte[] d, int o) { return ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3]; }

    public static Font Parse(string path, string chars)
    {
        byte[] d = File.ReadAllBytes(path);
        int numTables = U16(d, 4);
        var tables = new Dictionary<string, (int off, int len)>();
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string tag = System.Text.Encoding.ASCII.GetString(d, rec, 4);
            tables[tag] = ((int)U32(d, rec + 8), (int)U32(d, rec + 12));
        }

        var font = new Font();
        int head = tables["head"].off;
        font.unitsPerEm = U16(d, head + 18);
        int indexToLoc = S16(d, head + 50);
        int hhea = tables["hhea"].off;
        font.ascent = S16(d, hhea + 4);
        int numHMetrics = U16(d, hhea + 34);
        int hmtx = tables["hmtx"].off;
        int loca = tables["loca"].off;
        int glyf = tables["glyf"].off;

        //cmap format 4 (BMP)
        int cmap = tables["cmap"].off;
        int sub4 = -1;
        int cmapCount = U16(d, cmap + 2);
        for (int i = 0; i < cmapCount; i++)
        {
            int rec = cmap + 4 + i * 8;
            int platform = U16(d, rec);
            int encoding = U16(d, rec + 2);
            int off = (int)U32(d, rec + 4);
            if ((platform == 3 && (encoding == 1 || encoding == 10)) || platform == 0)
            {
                if (U16(d, cmap + off) == 4) { sub4 = cmap + off; break; }
            }
        }
        if (sub4 < 0) throw new Exception("no cmap format 4");
        int segCount = U16(d, sub4 + 6) / 2;
        int endBase = sub4 + 14, startBase = endBase + segCount * 2 + 2,
            deltaBase = startBase + segCount * 2, rangeBase = deltaBase + segCount * 2;

        foreach (char ch in chars)
        {
            if (font.glyphs.ContainsKey(ch)) continue;
            int gid = 0;
            for (int s = 0; s < segCount; s++)
            {
                if (ch <= U16(d, endBase + s * 2))
                {
                    int start = U16(d, startBase + s * 2);
                    if (ch >= start)
                    {
                        int ro = U16(d, rangeBase + s * 2);
                        if (ro == 0)
                            gid = (ch + S16(d, deltaBase + s * 2)) & 0xFFFF;
                        else
                        {
                            int idx = rangeBase + s * 2 + ro + (ch - start) * 2;
                            gid = U16(d, idx);
                            if (gid != 0) gid = (gid + S16(d, deltaBase + s * 2)) & 0xFFFF;
                        }
                    }
                    break;
                }
            }

            var g = new Glyph();
            int hm = gid < numHMetrics ? gid : numHMetrics - 1;
            g.advance = U16(d, hmtx + hm * 4);

            int go, gl;
            if (indexToLoc == 0)
            {
                go = U16(d, loca + gid * 2) * 2;
                gl = U16(d, loca + gid * 2 + 2) * 2 - go;
            }
            else
            {
                go = (int)U32(d, loca + gid * 4);
                gl = (int)U32(d, loca + gid * 4 + 4) - go;
            }
            if (gl > 0)
            {
                int p = glyf + go;
                int contours = S16(d, p);
                if (contours < 0)
                    font.compositesSkipped++; //composite: out of PoC scope
                else
                    ParseSimpleGlyph(d, p, contours, g);
            }
            font.glyphs[ch] = g;
        }
        return font;
    }

    static void ParseSimpleGlyph(byte[] d, int p, int contours, Glyph g)
    {
        g.bbox = new Vector4(S16(d, p + 2), S16(d, p + 4), S16(d, p + 6), S16(d, p + 8));
        int o = p + 10;
        var ends = new int[contours];
        for (int i = 0; i < contours; i++) { ends[i] = U16(d, o); o += 2; }
        int nPts = ends[contours - 1] + 1;
        o += 2 + U16(d, o); //skip instructions

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
            else if ((f & 16) == 0) { v += S16(d, o); o += 2; }
            xs[i] = v;
        }
        v = 0;
        for (int i = 0; i < nPts; i++)
        {
            byte f = flags[i];
            if ((f & 4) != 0) { byte dy = d[o++]; v += (f & 32) != 0 ? dy : (short)-dy; }
            else if ((f & 32) == 0) { v += S16(d, o); o += 2; }
            ys[i] = v;
        }

        int startPt = 0;
        for (int c = 0; c < contours; c++)
        {
            int endPt = ends[c];
            int n = endPt - startPt + 1;
            if (n < 2) { startPt = endPt + 1; continue; }

            //expand TrueType implied on-curve midpoints into a clean point ring
            var ring = new List<(Vector2 pt, bool on)>();
            for (int i = 0; i < n; i++)
            {
                int idx = startPt + i;
                ring.Add((new Vector2(xs[idx], ys[idx]), (flags[idx] & 1) != 0));
            }
            //rotate so ring[0] is on-curve (insert midpoint if none adjacent)
            int first = ring.FindIndex(r => r.on);
            if (first < 0)
            {
                var mid = ((ring[0].pt + ring[1].pt) * 0.5f, true);
                ring.Insert(1, mid);
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
                    //straight segment as a degenerate quadratic
                    g.pts.Add(cur); g.pts.Add((cur + seq[k].pt) * 0.5f); g.pts.Add(seq[k].pt);
                    cur = seq[k].pt;
                    k++;
                }
                else
                {
                    Vector2 ctrl = seq[k].pt;
                    Vector2 next;
                    if (k + 1 < seq.Count && !seq[k + 1].on)
                        next = (ctrl + seq[k + 1].pt) * 0.5f; //implied on-curve midpoint
                    else if (k + 1 < seq.Count)
                        next = seq[k + 1].pt;
                    else
                        next = seq[0].pt;
                    g.pts.Add(cur); g.pts.Add(ctrl); g.pts.Add(next);
                    cur = next;
                    k += (k + 1 < seq.Count && seq[k + 1].on) ? 2 : 1;
                }
            }
            startPt = endPt + 1;
        }
    }

    // ---------- baking + rendering ----------

    const int Bands = 8;

    struct GlyphQuad
    {
        public Vector4 rect;   //screen-local (GRoot px, y-negated space)
        public Vector4 bbox;   //glyph em units
        public Color color;
        public uint bandBase;
        public uint pad0, pad1, pad2;
    }

    GameObject _go;
    Mesh _mesh;
    Material _mat;
    ComputeBuffer _pts, _bands, _bandCurves, _quads;
    public int glyphCount, curveCount;

    /// <summary>Lays out and renders text at GRoot coordinates via curve evaluation.</summary>
    public static CurveTextPoC Draw(Font font, string text, Vector2 pos, float sizePx, Color color)
    {
        var r = new CurveTextPoC();
        float scale = sizePx / font.unitsPerEm;

        //shared curve data across the used glyphs
        var pts = new List<Vector2>();
        var bands = new List<Vector2Int>();
        var bandCurves = new List<uint>();
        var glyphBand = new Dictionary<char, (int bandBase, Glyph g)>();
        foreach (char ch in text)
        {
            if (glyphBand.ContainsKey(ch) || !font.glyphs.TryGetValue(ch, out var g) || g.pts.Count == 0)
                continue;
            int curveBase = pts.Count / 3;
            pts.AddRange(g.pts);
            int nCurves = g.pts.Count / 3;
            int bandBase = bands.Count;
            float y0 = g.bbox.y, y1 = g.bbox.w, bh = Mathf.Max(y1 - y0, 1);
            for (int b = 0; b < Bands; b++)
            {
                float by0 = y0 + bh * b / Bands, by1 = y0 + bh * (b + 1) / Bands;
                int s = bandCurves.Count;
                for (int c = 0; c < nCurves; c++)
                {
                    float cy0 = Mathf.Min(g.pts[c * 3].y, Mathf.Min(g.pts[c * 3 + 1].y, g.pts[c * 3 + 2].y));
                    float cy1 = Mathf.Max(g.pts[c * 3].y, Mathf.Max(g.pts[c * 3 + 1].y, g.pts[c * 3 + 2].y));
                    if (cy1 >= by0 && cy0 <= by1)
                        bandCurves.Add((uint)(curveBase + c));
                }
                bands.Add(new Vector2Int(s, bandCurves.Count - s));
            }
            glyphBand[ch] = (bandBase, g);
        }

        //layout: one quad per glyph at the baseline
        var quads = new List<GlyphQuad>();
        float penX = pos.x;
        float baseline = pos.y + font.ascent * scale;
        foreach (char ch in text)
        {
            if (!font.glyphs.TryGetValue(ch, out var g)) continue;
            if (g.pts.Count > 0 && glyphBand.TryGetValue(ch, out var gb))
            {
                //pad the quad 1px around the bbox so AA has room
                float x0 = penX + g.bbox.x * scale - 1, x1 = penX + g.bbox.z * scale + 1;
                float yTop = baseline - g.bbox.w * scale - 1, yBot = baseline - g.bbox.y * scale + 1;
                quads.Add(new GlyphQuad
                {
                    //y-negated container space: rect.xy = min corner
                    rect = new Vector4(x0, -yBot, x1 - x0, yBot - yTop),
                    bbox = new Vector4(g.bbox.x - 1 / scale, g.bbox.y - 1 / scale, g.bbox.z + 1 / scale, g.bbox.w + 1 / scale),
                    color = color,
                    bandBase = (uint)gb.bandBase
                });
            }
            penX += g.advance * scale;
        }

        r.glyphCount = quads.Count;
        r.curveCount = pts.Count / 3;
        if (quads.Count == 0)
            return r;

        r._pts = new ComputeBuffer(pts.Count, 8);
        r._pts.SetData(pts);
        r._bands = new ComputeBuffer(bands.Count, 8);
        r._bands.SetData(bands);
        r._bandCurves = new ComputeBuffer(Mathf.Max(bandCurves.Count, 1), 4);
        r._bandCurves.SetData(bandCurves);
        r._quads = new ComputeBuffer(quads.Count, 64);
        r._quads.SetData(quads);

        r._mesh = new Mesh();
        var verts = new Vector3[quads.Count * 4];
        var tris = new int[quads.Count * 6];
        for (int q = 0; q < quads.Count; q++)
        {
            int vtx = q * 4, t = q * 6;
            tris[t] = vtx; tris[t + 1] = vtx + 1; tris[t + 2] = vtx + 2;
            tris[t + 3] = vtx + 2; tris[t + 4] = vtx + 1; tris[t + 5] = vtx + 3;
        }
        r._mesh.vertices = verts;
        r._mesh.triangles = tris;
        r._mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));

        r._mat = new Material(Shader.Find("FairyGUI/CurveTextPoC"));
        r._mat.hideFlags = HideFlags.DontSave;
        r._mat.SetBuffer("_Pts", r._pts);
        r._mat.SetBuffer("_Bands", r._bands);
        r._mat.SetBuffer("_BandCurves", r._bandCurves);
        r._mat.SetBuffer("_Glyphs", r._quads);

        r._go = new GameObject("CurveTextPoC");
        r._go.hideFlags = HideFlags.DontSave;
        var mf = r._go.AddComponent<MeshFilter>();
        var mr = r._go.AddComponent<MeshRenderer>();
        mf.sharedMesh = r._mesh;
        mr.sharedMaterial = r._mat;
        mr.sortingOrder = 30000;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        var parent = GRoot.inst.displayObject.cachedTransform;
        r._go.transform.SetParent(parent, false);
        r._go.layer = GRoot.inst.displayObject.gameObject.layer;
        return r;
    }

    public void Dispose()
    {
        _pts?.Release(); _bands?.Release(); _bandCurves?.Release(); _quads?.Release();
        if (_mat != null) UnityEngine.Object.Destroy(_mat);
        if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
        if (_go != null) UnityEngine.Object.Destroy(_go);
    }
}
#endif
