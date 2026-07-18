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
            #pragma target 3.0
            #include "UnityCG.cginc"

            #define MAX_CLIPS 16
            float4 _ClipRects[MAX_CLIPS]; //xMin yMin xMax yMax, unscrolled stream-local
            float4 _ClipSofts[MAX_CLIPS]; //softness px toward min/max edges

            float4 _ScrollOffset;  //xy applied to every quad
            float4 _ClipRect;      //external window: xMin yMin xMax yMax
            float4 _ClipSoft;      //external window softness px toward min/max edges
            sampler2D _MainTex;

            struct appdata
            {
                float2 corner : POSITION;  //unit quad corner in 0..1
                float4 color : COLOR;
                float4 rect : TEXCOORD0;   //xy = min corner (container local), zw = size
                float4 uvA : TEXCOORD1;    //xy = uv at corner (0,0), zw = uv at corner (1,0)
                float4 uvB : TEXCOORD2;    //xy = uv at corner (0,1), zw = uv at corner (1,1)
                float4 misc : TEXCOORD3;   //x = transformIndex, y = clipIndex, z = flags
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 scrolledPos : TEXCOORD1;
                float3 rawPos : TEXCOORD2;   //xy = unscrolled pos, z = alphaTex flag
                float4 clipRect : TEXCOORD3; //per-instance clip entry, flat across quad
                float4 clipSoft : TEXCOORD4;
                fixed4 color : COLOR;
            };

            v2f vert(appdata a)
            {
                float2 c = a.corner;
                float2 raw = a.rect.xy + a.rect.zw * c;
                float2 local = raw + _ScrollOffset.xy;
                float2 uv = lerp(lerp(a.uvA.xy, a.uvA.zw, c.x),
                                 lerp(a.uvB.xy, a.uvB.zw, c.x), c.y);

                int clipIndex = (int)(a.misc.y + 0.5);
                float alphaTex = fmod(a.misc.z, 2.0) >= 1.0 ? 1.0 : 0.0; //flags bit 0

                v2f o;
                float4 world = mul(unity_ObjectToWorld, float4(local, 0.0, 1.0));
                o.pos = mul(UNITY_MATRIX_VP, world);
                o.uv = uv;
                o.scrolledPos = local;
                o.rawPos = float3(raw, alphaTex);
                o.clipRect = _ClipRects[clipIndex];
                o.clipSoft = _ClipSofts[clipIndex];
                o.color = a.color;
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
