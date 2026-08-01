using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FairyGUI
{
    /// <summary>
    /// One vertex of the M6 vertex-stream backend (72 bytes): a QuadInstance
    /// baked out to its four corners for platforms without vertex-stage
    /// StructuredBuffer (WebGL2 / low-end GLES). Field order MUST follow Unity's
    /// VertexAttribute enum order (Position, Color, TexCoord0..4) and must match
    /// both Layout below and FairyGUI-InstancedUIAttribs.shader.
    ///
    /// This path pays 4 vertices per quad against the buffer path's single
    /// 80-byte instance, so the per-vertex width IS the bandwidth story: at the
    /// original 104 bytes a quad uploaded 416 bytes, 5.2x the buffer path.
    /// Three fields were carrying air:
    ///
    /// - color was float4 where the source is byte-valued and Unity's own
    ///   meshes use UNorm8 anyway — 16 bytes to say what 4 say exactly;
    /// - sdfRadii held four 0-255 BYTES widened to floats, historically so the
    ///   GLES shader needed no bit ops — but the shader already tests flag bits,
    ///   so that rationale had lapsed;
    /// - misc.w held the border width, which flags bits 8-15 already carry.
    ///
    /// corner is only ever 0 or 1, both exact in half. 72 bytes = 288/quad,
    /// a 31% cut.
    ///
    /// Stated precisely, because "everything narrowed is exact" would be too
    /// strong: corner and sdfRadii ARE exact. color is not, quite — tint and
    /// the M8 bakedAlpha rescaling produce arbitrary floats, so alpha and any
    /// rescaled channel quantise to 1/255 with a worst-case error of 1/510
    /// (0.2%). That is the same precision Unity's own mesh color channel has
    /// used all along, and the buffer backend's float color is the outlier
    /// rather than the reference — but it is a quantisation, not an identity.
    ///
    /// rect (container-local pixels, unbounded) and uvA/uvB (texture UVs — but
    /// glyph-space em coordinates under FlagCurveGlyph, which range outside
    /// 0..1) stay float32. Narrowing those would be a precision claim rather
    /// than a repacking, and the remaining 68-byte target in the audit assumed
    /// deriving corner from SV_VertexID, which §15 rules out.
    ///
    /// misc packs the integer instance fields as floats (exact below 2^24):
    /// x = transformIndex, y = clipIndex (indexes _ClipRects/_ClipSofts uniform
    /// arrays, max 16), z = flags (bit 0 = alpha-only texture, bits 1-3 = SDF
    /// mode / curve glyph, bit 4 = grayed, bits 8-15 = border width px,
    /// bits 16-17 = segment texture slot).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct QuadVertex
    {
        //the only two values a unit-quad corner takes, as half bit patterns
        const ushort Half0 = 0x0000;
        const ushort Half1 = 0x3C00;

        public ushort cornerX, cornerY; //Float16 x2
        public Color32 color;           //UNorm8 x4
        public Vector4 rect;
        public Vector4 uvA;
        public Vector4 uvB;
        public Vector3 misc;            //w dropped: border width lives in flags
        //M7 corner radii px (BL BR TL TR) kept as the bytes they always were;
        //the shader recovers them with floor(x*255+0.5). Under FlagCurveGlyph
        //these same four bytes ARE the glyph index, whose top byte the shader
        //weights by 16777216 — hence that rounding, without which a UNorm8
        //round-trip's ~1e-5 would land the lookup ~167 glyphs away.
        public byte radBL, radBR, radTL, radTR;

        public const int Stride = 72;

        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float16, 2),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.UNorm8, 4),
        };

        /// <summary>
        /// Rounds to nearest instead of truncating. Color's implicit Color32
        /// conversion floors, which would pull every channel down by up to one
        /// step against the buffer path's float color — a systematic darkening,
        /// not a wash, and exactly the kind of thing a diff==0 gate catches.
        /// </summary>
        static byte ToByte(float v)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        }

        /// <summary>Writes the four corner vertices of one quad into dst at dstQuad*4.</summary>
        public static void WriteQuad(QuadVertex[] dst, int dstQuad, in QuadInstance q)
        {
            var v = new QuadVertex
            {
                color = new Color32(ToByte(q.color.r), ToByte(q.color.g),
                    ToByte(q.color.b), ToByte(q.color.a)),
                rect = q.rect,
                uvA = q.uvA,
                uvB = q.uvB,
                misc = new Vector3(q.transformIndex, q.clipIndex, q.flags),
                radBL = (byte)(q.padding & 0xFFu),
                radBR = (byte)((q.padding >> 8) & 0xFFu),
                radTL = (byte)((q.padding >> 16) & 0xFFu),
                radTR = (byte)((q.padding >> 24) & 0xFFu),
            };
            int b = dstQuad * 4;
            v.cornerX = Half0; v.cornerY = Half0; dst[b] = v;
            v.cornerX = Half1; v.cornerY = Half0; dst[b + 1] = v;
            v.cornerX = Half0; v.cornerY = Half1; dst[b + 2] = v;
            v.cornerX = Half1; v.cornerY = Half1; dst[b + 3] = v;
        }
    }
}
