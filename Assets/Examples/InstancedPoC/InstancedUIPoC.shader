// Proof-of-concept GPU-instanced UI quad shader (ProGPU-style unit-quad expansion).
// Each instance carries rect (container-local), four corner UVs (rotation-proof for
// rotated atlas sprites), and a color. A shared scroll offset and rect clip live in
// uniforms, so scrolling costs one vector write instead of a CPU rebake.
Shader "FairyGUI/InstancedUIPoC"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _AlphaTexMode ("Alpha texture mode", Float) = 0
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
                float4 rect;   //xy = min corner (container local), zw = size
                float4 uvA;    //xy = uv at corner (0,0), zw = uv at corner (1,0)
                float4 uvB;    //xy = uv at corner (0,1), zw = uv at corner (1,1)
                float4 color;
            };

            StructuredBuffer<QuadInstance> _Instances;
            float4x4 _ContainerL2W;
            float4 _ScrollOffset;  //xy applied to every quad
            float4 _ClipRect;      //container local: xMin yMin xMax yMax
            float _SegZ;           //per-segment depth for transparent draw ordering
            sampler2D _MainTex;
            float _AlphaTexMode;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 localPos : TEXCOORD1;
                fixed4 color : COLOR;
            };

            v2f vert(float4 vertex : POSITION, uint iid : SV_InstanceID)
            {
                QuadInstance d = _Instances[iid];
                float2 c = vertex.xy; //unit quad corner in 0..1

                float2 local = d.rect.xy + d.rect.zw * c + _ScrollOffset.xy;
                float2 uv = lerp(lerp(d.uvA.xy, d.uvA.zw, c.x),
                                 lerp(d.uvB.xy, d.uvB.zw, c.x), c.y);

                v2f o;
                float4 world = mul(_ContainerL2W, float4(local, 0.0, 1.0));
                world.z += _SegZ;
                o.pos = mul(UNITY_MATRIX_VP, world);
                o.uv = uv;
                o.localPos = local;
                o.color = d.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (i.localPos.x < _ClipRect.x || i.localPos.y < _ClipRect.y ||
                    i.localPos.x > _ClipRect.z || i.localPos.y > _ClipRect.w)
                    discard;

                fixed4 tex = tex2D(_MainTex, i.uv);
                if (_AlphaTexMode > 0.5)
                    tex = fixed4(1, 1, 1, tex.a);
                return i.color * tex;
            }
            ENDCG
        }
    }
}
