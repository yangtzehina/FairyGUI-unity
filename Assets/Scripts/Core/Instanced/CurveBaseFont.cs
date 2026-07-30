using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// Batch 5: the curve-text pipeline as a real FairyGUI font. Register once
    /// (<see cref="Register"/>) and any TextField/GTextField selecting it by
    /// name gets resolution-independent glyphs — layout (wrapping, UBB colors,
    /// sub/superscript, underline) runs through the standard TextField engine,
    /// while rendering goes analytic:
    ///
    /// - Native path: DrawGlyph emits the glyph's padded em box with an encoded
    ///   uv (x = glyphIndex*4 + nu*2 with nu the horizontal 0..1 position in the
    ///   padded box, y = raw em Y); FairyGUI/CurveText evaluates coverage from
    ///   the CurveFontStore tables bound as global buffers. Solid rects
    ///   (underline/strikethrough) carry uv.x = -4.
    /// - Instanced path: every built quad is mirrored into the owning
    ///   NGraphics' side table; the stream emits FlagCurveGlyph instances from
    ///   it and never touches the encoded mesh.
    ///
    /// v1 limits (documented, fall back gracefully): single font file (the
    /// store is a singleton); no bold synthesis, outline or shadow; the native
    /// shader needs fragment StructuredBuffer (WebGL waits on the data-texture
    /// backend); glyphs missing from the font render .notdef.
    /// </summary>
    public class CurveBaseFont : BaseFont
    {
        /// <summary>AA margin baked around each glyph quad, in font units — a
        /// fixed-em pad keeps the shader's uv decode deterministic (vs the
        /// per-scale pixel pad CurveTextMesh uses). ~0.1 em covers the ±0.5px
        /// AA fringe for any size above ~5px.</summary>
        public const float PadEm = 200f;

        float _size;
        float _scale;
        CurveFontStore.GlyphInfo _glyph;
        NGraphics _target;
        static readonly Color32[] sVertexColors = new Color32[4];

        public CurveBaseFont(string name)
        {
            this.name = name;
            this.canTint = true;
            this.keepCrisp = false;     //resolution-independent by construction
            this.customBold = false;
            this.customOutline = false; //outline/shadow unsupported in v1
            this.shader = "FairyGUI/CurveText";
            this.mainTexture = NTexture.Empty;
        }

        /// <summary>
        /// Loads the ttf into the (single-font) CurveFontStore and registers the
        /// font under the given name for TextFormat.font selection.
        /// </summary>
        public static CurveBaseFont Register(string name, string ttfPath)
        {
            CurveFontStore.LoadFont(ttfPath);
            var font = new CurveBaseFont(name);
            FontManager.RegisterFont(font);
            return font;
        }

        override public void SetFormat(TextFormat format, float fontSizeScale)
        {
            float size = format.size * fontSizeScale;
            if (format.specialStyle == TextFormat.SpecialStyle.Subscript
                || format.specialStyle == TextFormat.SpecialStyle.Superscript)
                size *= SupScale;
            _size = Mathf.Max(size, 1);
            _scale = _size / CurveFontStore.unitsPerEm;
            format.FillVertexColors(sVertexColors);
        }

        override public bool GetGlyph(char ch, out float width, out float height, out float baseline)
        {
            _glyph = CurveFontStore.GetGlyph(ch);
            float asc = CurveFontStore.ascent * _scale;
            width = Mathf.RoundToInt(_glyph.advance * _scale);
            height = Mathf.RoundToInt(asc - CurveFontStore.descent * _scale);
            baseline = Mathf.RoundToInt(asc);
            return true;
        }

        override public void StartDraw(NGraphics graphics)
        {
            //measure (BuildLines/GetGlyph) has baked every glyph this field
            //needs by now — push the tables to the GPU before the draw phase,
            //or the native shader reads stale buffers past their old length
            CurveFontStore.EnsureBuffers();
            _target = graphics;
            if (graphics._curveGlyphs == null)
                graphics._curveGlyphs = new List<CurveTextMesh.GlyphQuad>();
            graphics._curveGlyphs.Clear();
        }

        override public void DrawGlyph(VertexBuffer vb, float x, float y)
        {
            if (_glyph == null || _glyph.index < 0)
                return; //space/composite-less carrier: advance only

            Vector4 bb = _glyph.bbox;
            var pb = new Vector4(bb.x - PadEm, bb.y - PadEm, bb.z + PadEm, bb.w + PadEm);
            float x0 = x + pb.x * _scale, x1 = x + pb.z * _scale;
            float yTop = y + pb.w * _scale, yBot = y + pb.y * _scale;

            vb.vertices.Add(new Vector3(x0, yBot));
            vb.vertices.Add(new Vector3(x0, yTop));
            vb.vertices.Add(new Vector3(x1, yTop));
            vb.vertices.Add(new Vector3(x1, yBot));

            float gx = _glyph.index * 4f;
            vb.uvs.Add(new Vector2(gx, pb.y));
            vb.uvs.Add(new Vector2(gx, pb.w));
            vb.uvs.Add(new Vector2(gx + 2f, pb.w));
            vb.uvs.Add(new Vector2(gx + 2f, pb.y));

            vb.colors.Add(sVertexColors[0]);
            vb.colors.Add(sVertexColors[1]);
            vb.colors.Add(sVertexColors[2]);
            vb.colors.Add(sVertexColors[3]);

            if (_target != null && _target._curveGlyphs != null)
            {
                _target._curveGlyphs.Add(new CurveTextMesh.GlyphQuad
                {
                    rect = new Rect(x0, -yTop, x1 - x0, yTop - yBot),
                    bbox = pb,
                    glyphIndex = _glyph.index,
                    color = sVertexColors[0]
                });
            }
        }

        override public void DrawLine(VertexBuffer vb, float x, float y, float width, int fontSize, int type)
        {
            float thickness = Mathf.Max(1, fontSize / 16f);
            //underline sits halfway into the descent, strikethrough at ~0.35 ascent
            float offset = type == 0
                ? CurveFontStore.descent * _scale * 0.5f
                : CurveFontStore.ascent * _scale * 0.35f;
            float yTop = y + offset, yBot = yTop - thickness;

            vb.vertices.Add(new Vector3(x, yBot));
            vb.vertices.Add(new Vector3(x, yTop));
            vb.vertices.Add(new Vector3(x + width, yTop));
            vb.vertices.Add(new Vector3(x + width, yBot));

            //uv.x = -4: the native shader's "solid, coverage 1" sentinel
            for (int i = 0; i < 4; i++)
                vb.uvs.Add(new Vector2(-4f, 0f));

            vb.colors.Add(sVertexColors[0]);
            vb.colors.Add(sVertexColors[1]);
            vb.colors.Add(sVertexColors[2]);
            vb.colors.Add(sVertexColors[3]);

            if (_target != null && _target._curveGlyphs != null)
            {
                _target._curveGlyphs.Add(new CurveTextMesh.GlyphQuad
                {
                    rect = new Rect(x, -yTop, width, yTop - yBot),
                    glyphIndex = -1,
                    color = sVertexColors[0]
                });
            }
        }

        override public bool HasCharacter(char ch)
        {
            return CurveFontStore.loaded;
        }

        override public int GetLineHeight(int size)
        {
            return Mathf.CeilToInt((CurveFontStore.ascent - CurveFontStore.descent)
                * size / CurveFontStore.unitsPerEm);
        }
    }
}
