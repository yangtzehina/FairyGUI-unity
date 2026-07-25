// v4 instanced UI quad shader, vertex-pulling form: each segment is a real
// MeshRenderer (child of the stream container) whose mesh is capacity-many
// dummy quads; SV_VertexID selects the QuadInstance (must match
// Core/Instanced/QuadInstance.cs, 80 bytes) and the corner, quads beyond
// _InstanceCount collapse to degenerate triangles. Being a renderer gives the
// segment sortingOrder (interleaving with native fallback content) and layer
// membership (CaptureCamera filter captures) for free; the object-to-world
// matrix comes from the transform, so moving/scrolling the container is free.
// Segments share one buffer and address it via _InstanceStart. Corner UVs are
// stored explicitly (rotation-proof); flags bit 0 selects alpha-only sampling
// (dynamic font atlas).
//
// Clipping (M3): _ClipRect/_ClipSoft is the external window (a ScrollPane mask
// around the stream root) tested against the SCROLLED position — the window
// stays put while content moves. Per-instance clipIndex selects an internal
// ClipEntry (must match Core/Instanced/ClipEntry.cs, 32 bytes) tested against
// the UNSCROLLED position — internal clip windows travel with the content.
// Soft edges attenuate alpha over the softness distance in pixels.
Shader "FairyGUI/InstancedUI"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Tex1 ("Texture slot 1", 2D) = "white" {}
        _Tex2 ("Texture slot 2", 2D) = "white" {}
        _Tex3 ("Texture slot 3", 2D) = "white" {}
    }
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
            //vertex-stage StructuredBuffer is not portable beyond these: WebGL has
            //no SSBO at all and GLES3.1 may report 0 vertex SSBO slots — those
            //platforms use FairyGUI/InstancedUIAttribs (vertex-stream backend)
            #pragma only_renderers d3d11 metal vulkan
            #include "UnityCG.cginc"

            struct QuadInstance
            {
                float4 rect;          //xy = min corner (container local), zw = size
                float4 uvA;           //xy = uv at corner (0,0), zw = uv at corner (1,0)
                float4 uvB;           //xy = uv at corner (0,1), zw = uv at corner (1,1)
                float4 color;
                uint transformIndex;  //reserved (M1: 0)
                uint clipIndex;       //-> _Clips
                uint flags;           //bit 0 = alpha-only texture
                uint padding;
            };

            struct ClipEntry
            {
                float4 rect;          //xMin yMin xMax yMax, unscrolled stream-local
                float4 soft;          //softness px toward min/max edges
            };

            StructuredBuffer<QuadInstance> _Instances;
            StructuredBuffer<ClipEntry> _Clips;
            //M9b curve text (CurveFontStore): quadratic outline points, per-glyph
            //band lists (base = glyphIndex*8) and per-glyph em bounding boxes
            StructuredBuffer<float2> _CurvePts;
            StructuredBuffer<uint2> _CurveBands;
            StructuredBuffer<uint> _CurveBandIdx;
            StructuredBuffer<float4> _CurveGlyphs;
            uint _InstanceStart;
            uint _InstanceCount;
            //transform slots (design 4.2 tier 1): slot-baked quads are mapped
            //back to stream-root space here; index 0 is identity
            float4x4 _TransformSlots[16];
            float4 _ScrollOffset;  //xy applied to every quad
            float4 _ClipRect;      //external window: xMin yMin xMax yMax
            float4 _ClipSoft;      //external window softness px toward min/max edges
            sampler2D _MainTex;
            //cross-atlas segments (batch 3d): up to 4 textures per segment,
            //selected per quad by flags bits 16-17 (sdfMW.z carries the index)
            sampler2D _Tex1;
            sampler2D _Tex2;
            sampler2D _Tex3;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 scrolledPos : TEXCOORD1;
                float3 rawPos : TEXCOORD2;   //xy = unscrolled pos, z = alphaTex flag
                float4 clipRect : TEXCOORD3; //per-instance ClipEntry, flat across quad
                float4 clipSoft : TEXCOORD4;
                float4 sdfPos : TEXCOORD5;   //xy = quad-local pos px, zw = half size
                float4 sdfRadii : TEXCOORD6; //corner radii px: BL BR TL TR
                float3 sdfMW : TEXCOORD7;    //x = border width px, y = mode, z = texture slot
                fixed4 color : COLOR;
            };

            v2f vert(uint vid : SV_VertexID)
            {
                uint quad = vid >> 2;
                if (quad >= _InstanceCount)
                {
                    //beyond this segment's range: collapse to a degenerate triangle
                    v2f dead = (v2f)0;
                    return dead;
                }
                QuadInstance d = _Instances[quad + _InstanceStart];
                //vertex k of each quad: corner (k&1, k>>1) in 0..1
                float2 c = float2(vid & 1u, (vid >> 1) & 1u);

                float2 raw = d.rect.xy + d.rect.zw * c;
                if (d.transformIndex != 0u)
                    raw = mul(_TransformSlots[d.transformIndex], float4(raw, 0.0, 1.0)).xy;
                float2 local = raw + _ScrollOffset.xy;
                float2 uv = lerp(lerp(d.uvA.xy, d.uvA.zw, c.x),
                                 lerp(d.uvB.xy, d.uvB.zw, c.x), c.y);

                ClipEntry ce = _Clips[d.clipIndex];

                v2f o;
                float4 world = mul(unity_ObjectToWorld, float4(local, 0.0, 1.0));
                o.pos = mul(UNITY_MATRIX_VP, world);
                o.uv = uv;
                o.scrolledPos = local;
                //z packs two per-quad bits: +1 alpha-only texture, +2 grayed
                o.rawPos = float3(raw, ((d.flags & 1u) != 0u ? 1.0 : 0.0)
                    + ((d.flags & 16u) != 0u ? 2.0 : 0.0));
                o.clipRect = ce.rect;
                o.clipSoft = ce.soft;
                //M7 SDF primitives: mode from flags bits 1-2, border width from
                //bits 8-15, corner radii from the packed padding bytes
                o.sdfPos = float4(c * d.rect.zw, d.rect.zw * 0.5);
                o.sdfRadii = float4(d.padding & 0xFFu, (d.padding >> 8) & 0xFFu,
                                    (d.padding >> 16) & 0xFFu, (d.padding >> 24) & 0xFFu);
                float mode = (d.flags & 2u) != 0u ? 1.0 : ((d.flags & 4u) != 0u ? 2.0
                    : ((d.flags & 8u) != 0u ? 3.0 : 0.0));
                //mode 3 (curve glyph): x carries the glyph index instead of a width
                o.sdfMW = float3(mode == 3.0 ? d.padding : ((d.flags >> 8) & 0xFFu), mode,
                    (d.flags >> 16) & 3u);
                o.color = d.color;
                return o;
            }

            //rounded-rect SDF (single-quadrant fold), border band [edge-w, edge],
            //fill inset by w — mirrors RoundedRectMesh geometry
            float sdfCoverage(float4 sdfPos, float4 radii, float2 mw)
            {
                float2 p = sdfPos.xy - sdfPos.zw;
                float r = (p.x < 0.0) ? ((p.y < 0.0) ? radii.x : radii.z)
                                      : ((p.y < 0.0) ? radii.y : radii.w);
                float2 q = abs(p) - sdfPos.zw + r;
                float dist = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
                float aa = max(fwidth(dist), 1e-4);
                return (mw.y > 1.5)
                    ? saturate(0.5 - dist / aa) * saturate(0.5 + (dist + mw.x) / aa)
                    : saturate(0.5 - (dist + mw.x) / aa);
            }

            float2 evalQ(float2 a, float2 b, float2 cpt, float t)
            {
                float it = 1.0 - t;
                return it * it * a + 2.0 * it * t * b + t * t * cpt;
            }

            //M9b: analytic glyph coverage from quadratic outlines — winding by
            //Lengyel's sign-class table (0x2E74), AA from sampled nearest distance
            float curveCoverage(float2 gp, uint glyphIndex)
            {
                float4 bbox = _CurveGlyphs[glyphIndex];
                float bh = max(bbox.w - bbox.y, 1.0);
                int band = clamp((int)((gp.y - bbox.y) / bh * 8.0), 0, 7);
                uint2 bc = _CurveBands[glyphIndex * 8 + (uint)band];

                int winding = 0;
                float best = 1e12;
                for (uint k = 0; k < bc.y; k++)
                {
                    uint ci = _CurveBandIdx[bc.x + k];
                    float2 A = _CurvePts[ci * 3 + 0] - gp;
                    float2 B = _CurvePts[ci * 3 + 1] - gp;
                    float2 C = _CurvePts[ci * 3 + 2] - gp;

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

            //0 at the rect edge -> 1 at softness px inside; 1 everywhere when soft=0
            float softFactor(float2 p, float4 rect, float4 soft)
            {
                float4 d = float4(p - rect.xy, rect.zw - p);
                float4 f = d / max(soft, 1e-4);
                return saturate(min(min(f.x, f.y), min(f.z, f.w)));
            }

            fixed4 frag(v2f i) : SV_Target
            {
                //external window (static while content scrolls)
                if (i.scrolledPos.x < _ClipRect.x || i.scrolledPos.y < _ClipRect.y ||
                    i.scrolledPos.x > _ClipRect.z || i.scrolledPos.y > _ClipRect.w)
                    discard;

                //internal clip region (travels with content)
                if (i.rawPos.x < i.clipRect.x || i.rawPos.y < i.clipRect.y ||
                    i.rawPos.x > i.clipRect.z || i.rawPos.y > i.clipRect.w)
                    discard;

                //branch is uniform across the quad (texIndex is per-quad constant),
                //so derivatives inside tex2D stay valid
                int texSlot = (int)(i.sdfMW.z + 0.5);
                fixed4 tex;
                if (texSlot == 0)
                    tex = tex2D(_MainTex, i.uv);
                else if (texSlot == 1)
                    tex = tex2D(_Tex1, i.uv);
                else if (texSlot == 2)
                    tex = tex2D(_Tex2, i.uv);
                else
                    tex = tex2D(_Tex3, i.uv);
                if (fmod(i.rawPos.z, 2.0) >= 1.0)
                    tex = fixed4(1, 1, 1, tex.a);
                fixed4 col = i.color * tex;
                if (i.rawPos.z > 1.5)
                {
                    //grayed (flags bit4), same weights as FairyGUI's GRAYED variant
                    fixed grey = dot(col.rgb, fixed3(0.299, 0.587, 0.114));
                    col.rgb = fixed3(grey, grey, grey);
                }
                if (i.sdfMW.y > 2.5)
                    col.a *= curveCoverage(i.uv, (uint)(i.sdfMW.x + 0.5)); //uv = em-space pos
                else if (i.sdfMW.y > 0.5)
                    col.a *= sdfCoverage(i.sdfPos, i.sdfRadii, i.sdfMW);
                col.a *= softFactor(i.scrolledPos, _ClipRect, _ClipSoft);
                col.a *= softFactor(i.rawPos.xy, i.clipRect, i.clipSoft);
                return col;
            }
            ENDCG
        }
    }
}
