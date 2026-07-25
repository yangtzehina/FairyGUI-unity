// v4 instanced UI quad shader, vertex-stream form (M6): for platforms without
// vertex-stage StructuredBuffer (WebGL2, low-end GLES). Each segment is a real
// MeshRenderer whose mesh carries the instance data baked per corner vertex
// (must match Core/Instanced/QuadVertex.cs, 88 bytes: Position2/Color4/
// TexCoord0-3). Internal clip regions live in uniform arrays indexed by
// misc.y; the external window stays _ClipRect/_ClipSoft uniforms. Semantics
// are identical to FairyGUI-InstancedUI.shader (the buffer form) — the two
// must stay pixel-equivalent, which the editor regression enforces via
// InstancedUIStream.forceVertexPath.
Shader "FairyGUI/InstancedUIAttribs"
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
            #pragma target 3.5
            #include "UnityCG.cginc"

            #define MAX_CLIPS 16
            float4 _ClipRects[MAX_CLIPS]; //xMin yMin xMax yMax, unscrolled stream-local
            float4 _ClipSofts[MAX_CLIPS]; //softness px toward min/max edges
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

            struct appdata
            {
                float2 corner : POSITION;  //unit quad corner in 0..1
                float4 color : COLOR;
                float4 rect : TEXCOORD0;   //xy = min corner (container local), zw = size
                float4 uvA : TEXCOORD1;    //xy = uv at corner (0,0), zw = uv at corner (1,0)
                float4 uvB : TEXCOORD2;    //xy = uv at corner (0,1), zw = uv at corner (1,1)
                float4 misc : TEXCOORD3;   //x = transformIndex, y = clipIndex, z = flags, w = border width px
                float4 sdfRadii : TEXCOORD4; //corner radii px: BL BR TL TR (M7)
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 scrolledPos : TEXCOORD1;
                float3 rawPos : TEXCOORD2;   //xy = unscrolled pos, z = alphaTex flag
                float4 clipRect : TEXCOORD3; //per-instance clip entry, flat across quad
                float4 clipSoft : TEXCOORD4;
                float4 sdfPos : TEXCOORD5;   //xy = quad-local pos px, zw = half size
                float4 sdfRadii : TEXCOORD6; //corner radii px: BL BR TL TR
                float3 sdfMW : TEXCOORD7;    //x = border width px, y = mode, z = texture slot
                fixed4 color : COLOR;
            };

            v2f vert(appdata a)
            {
                float2 c = a.corner;
                float2 raw = a.rect.xy + a.rect.zw * c;
                int slotIdx = (int)(a.misc.x + 0.5);
                if (slotIdx != 0)
                    raw = mul(_TransformSlots[slotIdx], float4(raw, 0.0, 1.0)).xy;
                float2 local = raw + _ScrollOffset.xy;
                float2 uv = lerp(lerp(a.uvA.xy, a.uvA.zw, c.x),
                                 lerp(a.uvB.xy, a.uvB.zw, c.x), c.y);

                int clipIndex = (int)(a.misc.y + 0.5);
                int flags = (int)(a.misc.z + 0.5);
                //packs two per-quad bits: +1 alpha-only texture, +2 grayed
                float alphaTex = ((flags & 1) != 0 ? 1.0 : 0.0)
                    + ((flags & 16) != 0 ? 2.0 : 0.0);

                v2f o;
                float4 world = mul(unity_ObjectToWorld, float4(local, 0.0, 1.0));
                o.pos = mul(UNITY_MATRIX_VP, world);
                o.uv = uv;
                o.scrolledPos = local;
                o.rawPos = float3(raw, alphaTex);
                o.clipRect = _ClipRects[clipIndex];
                o.clipSoft = _ClipSofts[clipIndex];
                o.sdfPos = float4(c * a.rect.zw, a.rect.zw * 0.5);
                o.sdfRadii = a.sdfRadii;
                float mode = (flags & 2) != 0 ? 1.0 : ((flags & 4) != 0 ? 2.0 : 0.0);
                o.sdfMW = float3(a.misc.w, mode, (flags >> 16) & 3);
                o.color = a.color;
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
                if (i.sdfMW.y > 0.5)
                    col.a *= sdfCoverage(i.sdfPos, i.sdfRadii, i.sdfMW);
                col.a *= softFactor(i.scrolledPos, _ClipRect, _ClipSoft);
                col.a *= softFactor(i.rawPos.xy, i.clipRect, i.clipSoft);
                return col;
            }
            ENDCG
        }
    }
}
