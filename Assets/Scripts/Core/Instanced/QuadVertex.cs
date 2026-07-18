using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FairyGUI
{
    /// <summary>
    /// One vertex of the M6 vertex-stream backend (88 bytes): a QuadInstance
    /// baked out to its four corners for platforms without vertex-stage
    /// StructuredBuffer (WebGL2 / low-end GLES). Field order MUST follow Unity's
    /// VertexAttribute enum order (Position, Color, TexCoord0..3) and must match
    /// both Layout below and FairyGUI-InstancedUIAttribs.shader.
    ///
    /// misc packs the integer instance fields as floats (exact below 2^24):
    /// x = transformIndex, y = clipIndex (indexes _ClipRects/_ClipSofts uniform
    /// arrays, max 16), z = flags (bit 0 = alpha-only texture, bits 1-2 = SDF
    /// mode), w = border width px. sdfRadii carries the padding bytes decoded
    /// to floats so the GLES shader needs no bit ops.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct QuadVertex
    {
        public Vector2 corner;
        public Color color;
        public Vector4 rect;
        public Vector4 uvA;
        public Vector4 uvB;
        public Vector4 misc;
        public Vector4 sdfRadii; //M7: corner radii px (BL BR TL TR), decoded C#-side

        public const int Stride = 104;

        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float32, 4),
        };

        /// <summary>Writes the four corner vertices of one quad into dst at dstQuad*4.</summary>
        public static void WriteQuad(QuadVertex[] dst, int dstQuad, in QuadInstance q)
        {
            var v = new QuadVertex
            {
                color = q.color,
                rect = q.rect,
                uvA = q.uvA,
                uvB = q.uvB,
                misc = new Vector4(q.transformIndex, q.clipIndex, q.flags, (q.flags >> 8) & 0xFFu),
                sdfRadii = new Vector4(q.padding & 0xFFu, (q.padding >> 8) & 0xFFu,
                    (q.padding >> 16) & 0xFFu, (q.padding >> 24) & 0xFFu)
            };
            int b = dstQuad * 4;
            v.corner = new Vector2(0, 0); dst[b] = v;
            v.corner = new Vector2(1, 0); dst[b + 1] = v;
            v.corner = new Vector2(0, 1); dst[b + 2] = v;
            v.corner = new Vector2(1, 1); dst[b + 3] = v;
        }
    }
}
