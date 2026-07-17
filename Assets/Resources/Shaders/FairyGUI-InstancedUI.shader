// v4 instanced UI quad shader: expands a shared unit quad per instance from the
// QuadInstance stream (must match Core/Instanced/QuadInstance.cs, 80 bytes).
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
            uint _InstanceStart;
            float4x4 _ContainerL2W;
            float4 _ScrollOffset;  //xy applied to every quad
            float4 _ClipRect;      //external window: xMin yMin xMax yMax
            float4 _ClipSoft;      //external window softness px toward min/max edges
            float _SegZ;           //per-segment depth for transparent draw ordering
            sampler2D _MainTex;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 scrolledPos : TEXCOORD1;
                float3 rawPos : TEXCOORD2;   //xy = unscrolled pos, z = alphaTex flag
                float4 clipRect : TEXCOORD3; //per-instance ClipEntry, flat across quad
                float4 clipSoft : TEXCOORD4;
                fixed4 color : COLOR;
            };

            v2f vert(float4 vertex : POSITION, uint iid : SV_InstanceID)
            {
                QuadInstance d = _Instances[iid + _InstanceStart];
                float2 c = vertex.xy; //unit quad corner in 0..1

                float2 raw = d.rect.xy + d.rect.zw * c;
                float2 local = raw + _ScrollOffset.xy;
                float2 uv = lerp(lerp(d.uvA.xy, d.uvA.zw, c.x),
                                 lerp(d.uvB.xy, d.uvB.zw, c.x), c.y);

                ClipEntry ce = _Clips[d.clipIndex];

                v2f o;
                float4 world = mul(_ContainerL2W, float4(local, 0.0, 1.0));
                world.z += _SegZ;
                o.pos = mul(UNITY_MATRIX_VP, world);
                o.uv = uv;
                o.scrolledPos = local;
                o.rawPos = float3(raw, (d.flags & 1u) != 0u ? 1.0 : 0.0);
                o.clipRect = ce.rect;
                o.clipSoft = ce.soft;
                o.color = d.color;
                return o;
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

                fixed4 tex = tex2D(_MainTex, i.uv);
                if (i.rawPos.z > 0.5)
                    tex = fixed4(1, 1, 1, tex.a);
                fixed4 col = i.color * tex;
                col.a *= softFactor(i.scrolledPos, _ClipRect, _ClipSoft);
                col.a *= softFactor(i.rawPos.xy, i.clipRect, i.clipSoft);
                return col;
            }
            ENDCG
        }
    }
}
