// Curve-text tables as RGBAFloat data textures (batch 5) — one source of
// truth for the coverage math shared by FairyGUI-InstancedUI (buffer path),
// FairyGUI-InstancedUIAttribs (vertex-stream path) and FairyGUI/CurveText
// (native). Bound as globals by CurveFontStore.EnsureBuffers. Layout, linear
// texel index i at width 1024 -> texel (i & 1023, i >> 10):
//   _CurvePtsTex:     2 texels per curve — (A.x A.y B.x B.y), (C.x C.y - -)
//   _CurveBandsTex:   1 texel per band   — (start count - -), glyphIndex*8+band
//   _CurveBandIdxTex: 4 curve indices per texel, channel i&3
//   _CurveGlyphsTex:  1 texel per glyph — banding bbox (em units)
// texelFetch semantics everywhere: WebGL2/GLES3.0 needs no SSBO, no filtering.
#ifndef FAIRYGUI_CURVE_COMMON
#define FAIRYGUI_CURVE_COMMON

Texture2D<float4> _CurvePtsTex;
Texture2D<float4> _CurveBandsTex;
Texture2D<float4> _CurveBandIdxTex;
Texture2D<float4> _CurveGlyphsTex;

#define CURVE_LOAD(tex, i) tex.Load(int3(int((i) & 1023u), int((i) >> 10u), 0))

float2 curveEvalQ(float2 a, float2 b, float2 cpt, float t)
{
    float it = 1.0 - t;
    return it * it * a + 2.0 * it * t * b + t * t * cpt;
}

//analytic glyph coverage from quadratic outlines — winding by Lengyel's
//sign-class table (0x2E74), AA from sampled-then-refined nearest distance
float curveCoverage(float2 gp, uint glyphIndex)
{
    float4 bbox = CURVE_LOAD(_CurveGlyphsTex, glyphIndex);
    float bh = max(bbox.w - bbox.y, 1.0);
    int band = clamp((int)((gp.y - bbox.y) / bh * 8.0), 0, 7);
    float4 bt4 = CURVE_LOAD(_CurveBandsTex, glyphIndex * 8u + (uint)band);
    uint bStart = (uint)(bt4.x + 0.5);
    uint bCount = (uint)(bt4.y + 0.5);

    int winding = 0;
    float best = 1e12;
    for (uint k = 0; k < bCount; k++)
    {
        uint ii = bStart + k;
        float4 it4 = CURVE_LOAD(_CurveBandIdxTex, ii >> 2u);
        uint ch = ii & 3u;
        float civ = ch == 0u ? it4.x : (ch == 1u ? it4.y : (ch == 2u ? it4.z : it4.w));
        uint ci = (uint)(civ + 0.5);
        float4 p01 = CURVE_LOAD(_CurvePtsTex, ci * 2u);
        float4 p2 = CURVE_LOAD(_CurvePtsTex, ci * 2u + 1u);
        float2 A = p01.xy - gp;
        float2 B = p01.zw - gp;
        float2 C = p2.xy - gp;

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
            if ((code & 1u) != 0u && curveEvalQ(A, B, C, t1).x > 0.0)
                winding += 1;
            if (code > 1u && curveEvalQ(A, B, C, t2).x > 0.0)
                winding -= 1;
        }

        float bt = 0.0;
        float bd = dot(A, A);
        [unroll]
        for (int sIdx = 1; sIdx <= 6; sIdx++)
        {
            float t = sIdx / 6.0;
            float2 q = curveEvalQ(A, B, C, t);
            float dd = dot(q, q);
            if (dd < bd) { bd = dd; bt = t; }
        }
        [unroll]
        for (int it2 = 0; it2 < 2; it2++)
        {
            float2 q = curveEvalQ(A, B, C, bt);
            float2 dq = 2.0 * ((B - A) + (A - 2.0 * B + C) * bt);
            float denom = dot(dq, dq);
            if (denom > 1e-9)
                bt = clamp(bt - dot(q, dq) / denom, 0.0, 1.0);
        }
        float2 qf = curveEvalQ(A, B, C, bt);
        best = min(best, dot(qf, qf));
    }

    float dist = sqrt(best);
    float emPerPx = max(length(float2(ddx(gp.x), ddy(gp.x))), 1e-6);
    float signedPx = (winding != 0 ? dist : -dist) / emPerPx;
    return saturate(0.5 + signedPx);
}

#endif
