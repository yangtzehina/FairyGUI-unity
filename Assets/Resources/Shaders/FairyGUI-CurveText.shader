// Batch 5: native rendering for CurveBaseFont — the standard FairyGUI text
// shader skeleton (stencil, CLIPPED/SOFT_CLIPPED/GRAYED variants, material
// blend factors) with the sampled-atlas fragment replaced by analytic curve
// coverage from the CurveFontStore tables (bound as global buffers by
// EnsureBuffers). Vertex uv encodes x = glyphIndex*4 + nu*2 (nu = horizontal
// 0..1 across the glyph's PADDED em box; pad = CurveBaseFont.PadEm font units)
// and y = raw em Y; uv.x < 0 marks solid rects (underline). Batch 5b widened
// the stride: x = glyphIndex*8 + bold*4 + nu*2 (bold = faux-bold bit; exact in
// float32 to two million glyphs). The tables are
// RGBAFloat data textures (batch 5) — no SSBO, runs on WebGL2/GLES3.0.
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

        //batch 5b effects, normally driven per-field via MaterialPropertyBlock
        //(CurveBaseFont.StartDraw); material defaults keep plain text plain.
        //x = outline width px, y = unused, zw = shadow offset px (screen down+)
        _CurveFx ("Curve FX", Vector) = (0,0,0,0)
        _CurveOutlineColor ("Curve Outline Color", Color) = (0,0,0,1)
        _CurveShadowColor ("Curve Shadow Color", Color) = (0,0,0,1)
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
                #pragma target 3.5

                #include "UnityCG.cginc"
                //tables as data textures (batch 5): texelFetch everywhere,
                //no SSBO — this shader now runs on WebGL2/GLES3.0 too
                #include "FairyGUI-CurveCommon.cginc"

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

                float4 _CurveFx;
                fixed4 _CurveOutlineColor;
                fixed4 _CurveShadowColor;

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


                fixed4 frag (v2f i) : SV_Target
                {
                    fixed4 col = i.color;
                    if (i.texcoord.x >= 0.0)
                    {
                        //decode (x8 stride): glyph index, faux-bold bit, then
                        //the em position across the PADDED box
                        float gx = i.texcoord.x * 0.125;
                        float gi = floor(gx);
                        uint glyphIndex = (uint)gi;
                        float f8 = (gx - gi) * 8.0;
                        float boldF = f8 >= 4.0 ? 1.0 : 0.0;
                        float nu = (f8 - boldF * 4.0) * 0.5;
                        float4 bbox = CURVE_LOAD(_CurveGlyphsTex, glyphIndex);
                        float pbx = bbox.x - CURVE_PAD;
                        float pbz = bbox.z + CURVE_PAD;
                        float2 gp = float2(pbx + nu * (pbz - pbx), i.texcoord.y);

                        float epp = curveEmPerPx(gp);
                        //faux bold: ~0.024em of stem each side — em-defined so
                        //it scales with size exactly like a real weight would
                        float bandEm = curveBandEm(glyphIndex);
                        float boldEm = min(boldF * 24.0, bandEm);
                        //effect widths cap at one band height: beyond it the
                        //distance field cannot see the nearest curve (banding)
                        float owEm = min(_CurveFx.x * epp, max(bandEm - boldEm, 0.0));
                        float sEm = curveSignedEm(gp, glyphIndex, boldEm + owEm);
                        float fillCov = saturate(0.5 + (sEm + boldEm) / epp);

                        //premultiplied coverage stack: fill over outline over
                        //shadow, resolved back to straight alpha at the end
                        float aP = fillCov;
                        float3 rgbP = col.rgb * fillCov;
                        if (owEm > 0.0)
                        {
                            float ringCov = saturate(0.5 + (sEm + boldEm + owEm) / epp);
                            float oA = ringCov * _CurveOutlineColor.a * (1.0 - fillCov);
                            rgbP += _CurveOutlineColor.rgb * oA;
                            aP += oA;
                        }
                        if (abs(_CurveFx.z) + abs(_CurveFx.w) > 0.0)
                        {
                            //offset is screen px, down-positive; em y runs up
                            float2 gpS = gp + float2(-_CurveFx.z, _CurveFx.w) * epp;
                            float sCov = saturate(0.5
                                + (curveSignedEm(gpS, glyphIndex, boldEm) + boldEm) / epp);
                            float shA = sCov * _CurveShadowColor.a * (1.0 - aP);
                            rgbP += _CurveShadowColor.rgb * shA;
                            aP += shA;
                        }
                        col.rgb = aP > 1e-4 ? rgbP / aP : col.rgb;
                        col.a *= aP;
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
