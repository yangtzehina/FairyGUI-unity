using System.Collections.Generic;
using UnityEngine;
using FairyGUI.Utils;

namespace FairyGUI
{
    /// <summary>
    /// M9b producer: a mesh factory that lays out a string as curve-text glyph
    /// quads (metrics straight from CurveFontStore's TTF — no OS rasterizer).
    /// Under an instanced stream the leaf is intercepted and rendered analytically
    /// from the outlines; the native OnPopulateMesh fallback draws translucent
    /// ghost boxes so unclaimed leaves are visibly placeholders, never silently
    /// missing. Assign to a Shape's graphics via SetText.
    /// </summary>
    public class CurveTextMesh : IMeshFactory
    {
        public struct GlyphQuad
        {
            public Rect rect;      //leaf-local, FairyGUI y-down px
            public Vector4 bbox;   //glyph em box (padded for AA)
            public int glyphIndex; //-1: solid rect (underline/strikethrough)
            public Color32 color;  //per-quad (UBB runs color independently)
        }

        public string text { get; private set; }
        public float fontSize { get; private set; }
        public Color color { get; private set; }
        readonly List<GlyphQuad> _quads = new List<GlyphQuad>();

        public IReadOnlyList<GlyphQuad> glyphQuads { get { return _quads; } }

        public void SetText(string value, float size, Color col)
        {
            text = value;
            fontSize = size;
            color = col;
            Layout();
        }

        void Layout()
        {
            _quads.Clear();
            if (string.IsNullOrEmpty(text) || !CurveFontStore.loaded)
                return;
            float scale = fontSize / CurveFontStore.unitsPerEm;
            float penX = 0;
            float baseline = CurveFontStore.ascent * scale;
            float lineH = (CurveFontStore.ascent - CurveFontStore.descent) * scale * 1.2f;
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    penX = 0;
                    baseline += lineH;
                    continue;
                }
                var g = CurveFontStore.GetGlyph(ch);
                if (g.index >= 0)
                {
                    //pad ~1px so analytic AA has room outside the ink box
                    float pad = 1f / Mathf.Max(scale, 1e-6f);
                    Vector4 bb = new Vector4(g.bbox.x - pad, g.bbox.y - pad, g.bbox.z + pad, g.bbox.w + pad);
                    _quads.Add(new GlyphQuad
                    {
                        //FairyGUI y-down: glyph top (bbox max y) is the smaller y
                        rect = new Rect(penX + bb.x * scale, baseline - bb.w * scale,
                            (bb.z - bb.x) * scale, (bb.w - bb.y) * scale),
                        bbox = bb,
                        glyphIndex = g.index,
                        color = color
                    });
                }
                penX += g.advance * scale;
            }
        }

        /// <summary>Native fallback: translucent ghost boxes marking glyph positions.</summary>
        public void OnPopulateMesh(VertexBuffer vb)
        {
            Color ghost = color;
            ghost.a *= 0.15f;
            foreach (var q in _quads)
                vb.AddQuad(q.rect, ghost);
            vb.AddTriangles();
        }
    }
}
