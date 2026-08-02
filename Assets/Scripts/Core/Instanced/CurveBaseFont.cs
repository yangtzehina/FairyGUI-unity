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
    /// Batch 5b effects: faux bold (a ~0.024em analytic stem expansion, encoded
    /// per glyph in the uv so UBB [b] segments work), outline and shadow (both
    /// analytic, single pass — the signed distance the coverage math already
    /// computes, band-widened; NOT the 4/8-copy vertex duplication other fonts
    /// use, which would also never reach the instanced side table). Outline
    /// and shadow are field-level (TextFormat has no per-segment syntax for
    /// them) and ride a MaterialPropertyBlock; a field using them keeps its
    /// native renderer under an instanced stream (sort barrier, like rotated
    /// curve leaves) while bold-only fields stay claimable.
    ///
    /// v1 limits that remain: single font file (the store is a singleton);
    /// effect widths cap at one glyph band height (distance-field reach);
    /// faux bold does not widen advances; point-matched composite glyphs
    /// render .notdef.
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
        bool _bold;
        Vector4 _fx;          //x = outline px, zw = shadow offset px
        Color _fxOutline;
        Color _fxShadow;
        CurveFontStore.GlyphInfo _glyph;
        NGraphics _target;
        static readonly Color32[] sVertexColors = new Color32[4];
        static readonly int sFxProp = Shader.PropertyToID("_CurveFx");
        static readonly int sFxOutlineProp = Shader.PropertyToID("_CurveOutlineColor");
        static readonly int sFxShadowProp = Shader.PropertyToID("_CurveShadowColor");

        public CurveBaseFont(string name)
        {
            this.name = name;
            this.canTint = true;
            this.keepCrisp = false;     //resolution-independent by construction
            //both stay false ON PURPOSE: true would make TextField draw 4/8
            //offset VERTEX COPIES (VertexBuffer.GenerateOutline/Shadow) — per
            //copy a full coverage evaluation, chunky rings, and the copies
            //never reach the instanced side table. The analytic path below
            //renders bold/outline/shadow in ONE pass instead.
            this.customBold = false;
            this.customOutline = false;
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
            _bold = format.bold;
            //field-level: TextFormat has no UBB syntax for these, so every
            //segment of one field carries the same values
            _fx = new Vector4(format.outline, 0,
                format.shadowOffset.x, format.shadowOffset.y);
            _fxOutline = format.outlineColor;
            _fxShadow = format.shadowColor;
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

            //batch 5b: outline/shadow ride a per-renderer property block. Push
            //when active, and ALSO once more when they just turned off — a
            //block outlives the format, and stale values would keep painting
            bool fxActive = _fx.x > 0 || _fx.z != 0 || _fx.w != 0;
            if (fxActive || graphics._curveFxActive)
            {
                var pb = graphics.materialPropertyBlock;
                pb.SetVector(sFxProp, fxActive ? _fx : Vector4.zero);
                pb.SetColor(sFxOutlineProp, _fxOutline);
                pb.SetColor(sFxShadowProp, _fxShadow);
            }
            graphics._curveFxActive = fxActive;
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

            //x8 stride: glyphIndex*8 + bold*4 + nu*2 (see FairyGUI/CurveText)
            float gx = _glyph.index * 8f + (_bold ? 4f : 0f);
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
                    color = sVertexColors[0],
                    bold = _bold
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
