// M9a Slug-style curve text PoC: per-pixel coverage from quadratic Bezier
// outlines. Winding number by solving each band curve's quadratic in y along a
// +x ray (derivative sign gives direction); antialiasing from the sampled
// nearest distance to the outline, converted to pixels via derivatives.
// Buffer backend only (editor/desktop PoC; the data-texture form is M9b).
Shader "FairyGUI/CurveTextPoC"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma only_renderers d3d11 metal vulkan
            #include "UnityCG.cginc"

            #define BANDS 8

            struct GlyphQuad
            {
                float4 rect;   //container-local: xy min corner, zw size
                float4 bbox;   //glyph space em units: xMin yMin xMax yMax
                float4 color;
                uint bandBase;
                uint pad0, pad1, pad2;
            };

            StructuredBuffer<float2> _Pts;        //3 points per quadratic
            StructuredBuffer<uint2> _Bands;       //per glyph: BANDS x (start,count)
            StructuredBuffer<uint> _BandCurves;
            StructuredBuffer<GlyphQuad> _Glyphs;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 gpos : TEXCOORD0;  //glyph-space position (em units)
                float4 color : COLOR;
                nointerpolation uint bandBase : TEXCOORD1;
                float4 bbox : TEXCOORD2;
            };

            v2f vert(uint vid : SV_VertexID)
            {
                uint quad = vid >> 2;
                GlyphQuad g = _Glyphs[quad];
                float2 c = float2(vid & 1u, (vid >> 1) & 1u);
                float2 local = g.rect.xy + g.rect.zw * c;

                v2f o;
                float4 world = mul(unity_ObjectToWorld, float4(local, 0.0, 1.0));
                o.pos = mul(UNITY_MATRIX_VP, world);
                //container y is negated: quad bottom (min y) maps to bbox min y
                o.gpos = float2(lerp(g.bbox.x, g.bbox.z, c.x), lerp(g.bbox.y, g.bbox.w, c.y));
                o.color = g.color;
                o.bandBase = g.bandBase;
                o.bbox = g.bbox;
                return o;
            }

            float2 evalQ(float2 a, float2 b, float2 cpt, float t)
            {
                float it = 1.0 - t;
                return it * it * a + 2.0 * it * t * b + t * t * cpt;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.gpos;
                float bh = max(i.bbox.w - i.bbox.y, 1.0);
                int band = clamp((int)((p.y - i.bbox.y) / bh * BANDS), 0, BANDS - 1);
                uint2 bc = _Bands[i.bandBase + (uint)band];

                int winding = 0;
                float best = 1e12; //squared distance, em units

                for (uint k = 0; k < bc.y; k++)
                {
                    uint ci = _BandCurves[bc.x + k];
                    float2 A = _Pts[ci * 3 + 0] - p;
                    float2 B = _Pts[ci * 3 + 1] - p;
                    float2 C = _Pts[ci * 3 + 2] - p;

                    //winding: Lengyel's sign-class table (JCGT 2017) — the 0x2E74
                    //magic encodes, per (sign p0.y, sign p1.y, sign p2.y) class,
                    //whether the first/second root contributes, which makes shared
                    //curve endpoints on the ray count exactly once
                    uint code = (0x2E74u >> (((A.y > 0.0) ? 2u : 0u)
                        + ((B.y > 0.0) ? 4u : 0u) + ((C.y > 0.0) ? 8u : 0u))) & 3u;
                    if (code != 0u)
                    {
                        float ay = A.y - 2.0 * B.y + C.y;
                        float by = A.y - B.y;
                        float cy = A.y;
                        float t1, t2;
                        if (abs(ay) > 1e-6)
                        {
                            float dsc = sqrt(max(by * by - ay * cy, 0.0));
                            t1 = (by - dsc) / ay;
                            t2 = (by + dsc) / ay;
                        }
                        else
                        {
                            t1 = t2 = cy / (2.0 * by);
                        }
                        if ((code & 1u) != 0u && evalQ(A, B, C, t1).x > 0.0)
                            winding += 1;
                        if (code > 1u && evalQ(A, B, C, t2).x > 0.0)
                            winding -= 1;
                    }

                    //nearest distance: coarse samples + one refinement
                    float bt = 0.0;
                    float bd = dot(A, A);
                    [unroll]
                    for (int sIdx = 1; sIdx <= 6; sIdx++)
                    {
                        float t = sIdx / 6.0;
                        float2 q = evalQ(A, B, C, t);
                        float dd = dot(q, q);
                        if (dd < bd) { bd = dd; bt = t; }
                    }
                    [unroll]
                    for (int it2 = 0; it2 < 2; it2++)
                    {
                        float2 q = evalQ(A, B, C, bt);
                        float2 dq = 2.0 * ((B - A) + (A - 2.0 * B + C) * bt);
                        float denom = dot(dq, dq);
                        if (denom > 1e-9)
                            bt = clamp(bt - dot(q, dq) / denom, 0.0, 1.0);
                    }
                    float2 qf = evalQ(A, B, C, bt);
                    best = min(best, dot(qf, qf));
                }

                float dist = sqrt(best);
                //em-per-pixel from screen derivatives of the glyph-space position
                float emPerPx = max(length(float2(ddx(p.x), ddy(p.x))), 1e-6);
                float dpx = dist / emPerPx;
                float signedPx = (winding != 0) ? dpx : -dpx;
                float alpha = saturate(0.5 + signedPx);

                fixed4 col = i.color;
                col.a *= alpha;
                return col;
            }
            ENDCG
        }
    }
}
