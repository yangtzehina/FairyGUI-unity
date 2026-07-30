// Batch 5: native rendering for CurveBaseFont — the standard FairyGUI text
// shader skeleton (stencil, CLIPPED/SOFT_CLIPPED/GRAYED variants, material
// blend factors) with the sampled-atlas fragment replaced by analytic curve
// coverage from the CurveFontStore tables (bound as global buffers by
// EnsureBuffers). Vertex uv encodes x = glyphIndex*4 + nu*2 (nu = horizontal
// 0..1 across the glyph's PADDED em box; pad = CurveBaseFont.PadEm font units)
// and y = raw em Y; uv.x < 0 marks solid rects (underline). Requires
// fragment-stage StructuredBuffer — WebGL waits on the data-texture backend.
Shader "FairyGUI/CurveText"
{
    Properties
    {
        _MainTex ("Alpha (A)", 2D) = "white" {}

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255

        _ColorMask ("Color Mask", Float) = 15

        _BlendSrcFactor ("Blend SrcFactor", Float) = 5
        _BlendDstFactor ("Blend DstFactor", Float) = 10
    }

    SubShader
    {
        LOD 100

        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Fog { Mode Off }
        Blend [_BlendSrcFactor] [_BlendDstFactor]
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
                #pragma multi_compile _ GRAYED
                #pragma multi_compile _ CLIPPED SOFT_CLIPPED
                #pragma vertex vert
                #pragma fragment frag
                #pragma target 4.5
                #pragma only_renderers d3d11 metal vulkan

                #include "UnityCG.cginc"

                //CurveFontStore tables (global): quadratic outline points, 8
                //band lists per glyph (base = glyphIndex*8), banding bboxes
                StructuredBuffer<float2> _CurvePtsG;
                StructuredBuffer<uint2> _CurveBandsG;
                StructuredBuffer<uint> _CurveBandIdxG;
                StructuredBuffer<float4> _CurveGlyphsG;

                #define CURVE_PAD 200.0 //must match CurveBaseFont.PadEm

                struct appdata_t
                {
                    float4 vertex : POSITION;
                    fixed4 color : COLOR;
                    float4 texcoord : TEXCOORD0;
                };

                struct v2f
                {
                    float4 vertex : SV_POSITION;
                    fixed4 color : COLOR;
                    float4 texcoord : TEXCOORD0;

                    #ifdef CLIPPED
                    float2 clipPos : TEXCOORD1;
                    #endif

                    #ifdef SOFT_CLIPPED
                    float2 clipPos : TEXCOORD1;
                    #endif
                };

                CBUFFER_START(UnityPerMaterial)
                #ifdef CLIPPED
                float4 _ClipBox = float4(-2, -2, 0, 0);
                #endif

                #ifdef SOFT_CLIPPED
                float4 _ClipBox = float4(-2, -2, 0, 0);
                float4 _ClipSoftness = float4(0, 0, 0, 0);
                #endif
                CBUFFER_END

                v2f vert (appdata_t v)
                {
                    v2f o;
                    o.vertex = UnityObjectToClipPos(v.vertex);
                    o.texcoord = v.texcoord;
                    #if !defined(UNITY_COLORSPACE_GAMMA) && (UNITY_VERSION >= 550)
                    o.color.rgb = GammaToLinearSpace(v.color.rgb);
                    o.color.a = v.color.a;
                    #else
                    o.color = v.color;
                    #endif

                    #ifdef CLIPPED
                    o.clipPos = mul(unity_ObjectToWorld, v.vertex).xy * _ClipBox.zw + _ClipBox.xy;
                    #endif

                    #ifdef SOFT_CLIPPED
                    o.clipPos = mul(unity_ObjectToWorld, v.vertex).xy * _ClipBox.zw + _ClipBox.xy;
                    #endif

                    return o;
                }

                float2 evalQ(float2 a, float2 b, float2 cpt, float t)
                {
                    float it = 1.0 - t;
                    return it * it * a + 2.0 * it * t * b + t * t * cpt;
                }

                //identical math to FairyGUI-InstancedUI.shader's curveCoverage:
                //winding by Lengyel's sign-class table, AA from nearest distance
                float curveCoverage(float2 gp, uint glyphIndex)
                {
                    float4 bbox = _CurveGlyphsG[glyphIndex];
                    float bh = max(bbox.w - bbox.y, 1.0);
                    int band = clamp((int)((gp.y - bbox.y) / bh * 8.0), 0, 7);
                    uint2 bc = _CurveBandsG[glyphIndex * 8 + (uint)band];

                    int winding = 0;
                    float best = 1e12;
                    for (uint k = 0; k < bc.y; k++)
                    {
                        uint ci = _CurveBandIdxG[bc.x + k];
                        float2 A = _CurvePtsG[ci * 3 + 0] - gp;
                        float2 B = _CurvePtsG[ci * 3 + 1] - gp;
                        float2 C = _CurvePtsG[ci * 3 + 2] - gp;

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
                                t1 = t2 = cy / (2.0 * by);
                            if ((code & 1u) != 0u && evalQ(A, B, C, t1).x > 0.0)
                                winding += 1;
                            if (code > 1u && evalQ(A, B, C, t2).x > 0.0)
                                winding -= 1;
                        }

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
                    float emPerPx = max(length(float2(ddx(gp.x), ddy(gp.x))), 1e-6);
                    float signedPx = (winding != 0 ? dist : -dist) / emPerPx;
                    return saturate(0.5 + signedPx);
                }

                fixed4 frag (v2f i) : SV_Target
                {
                    fixed4 col = i.color;
                    if (i.texcoord.x >= 0.0)
                    {
                        //decode: glyph index, then the em position from the
                        //normalized horizontal across the PADDED box
                        float gx = i.texcoord.x * 0.25;
                        float gi = floor(gx);
                        uint glyphIndex = (uint)gi;
                        float nu = (gx - gi) * 2.0;
                        float4 bbox = _CurveGlyphsG[glyphIndex];
                        float pbx = bbox.x - CURVE_PAD;
                        float pbz = bbox.z + CURVE_PAD;
                        float2 gp = float2(pbx + nu * (pbz - pbx), i.texcoord.y);
                        col.a *= curveCoverage(gp, glyphIndex);
                    }
                    //uv.x < 0: solid rect (underline/strikethrough), coverage 1

                    #ifdef GRAYED
                    fixed grey = dot(col.rgb, fixed3(0.299, 0.587, 0.114));
                    col.rgb = fixed3(grey, grey, grey);
                    #endif

                    #ifdef SOFT_CLIPPED
                    float2 factor;
                    float2 condition = step(i.clipPos.xy, 0);
                    float4 clip_softness = _ClipSoftness * float4(condition, 1 - condition);
                    factor.xy = (1.0 - abs(i.clipPos.xy)) * (clip_softness.xw + clip_softness.zy);
                    col.a *= clamp(min(factor.x, factor.y), 0.0, 1.0);
                    #endif

                    #ifdef CLIPPED
                    float2 factor = abs(i.clipPos);
                    col.a *= step(max(factor.x, factor.y), 1);
                    #endif

                    return col;
                }
            ENDCG
        }
    }
}
