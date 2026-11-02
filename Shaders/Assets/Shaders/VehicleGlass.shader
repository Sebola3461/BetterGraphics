// Vidro seco dos veículos (troca o "Glass Reflective" do jogo e o véu preto que o mod punha antes dele). Uma lâmina
// fina com duas faces:
//  - reflexo de Fresnel do vidro (F0 0,04 por face, somadas as duas), da sonda de reflexo com projeção em caixa,
//    mais o brilho do sol: de lado o vidro vira espelho, de frente quase só deixa ver;
//  - o resto atravessa com o fumê: janelas laterais escuras (~30%), para-brisa quase claro. Cada objeto de vidro
//    recebe a sua transmissão do mod (_BG_GlassT), decidida pela orientação dele no veículo.
// A mistura é uma só: cor = reflexo + fundo x transmissão (Blend One SrcAlpha).
Shader "BetterGraphics/VehicleGlass"
{
    Properties
    {
        _Color ("Tom", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite Off Cull Off
            Blend One SrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "UnityStandardUtils.cginc"

            float4 _Color;
            float _BG_GlassT;                // transmissão deste vidro (por objeto)
            float _BG_GlassReflect;          // escala do reflexo (1 = físico)

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float3 n : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.n = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float4 frag(v2f i, fixed facing : VFACE) : SV_Target
            {
                float3 N = normalize(i.n) * (facing > 0 ? 1.0 : -1.0);
                float3 V = normalize(_WorldSpaceCameraPos - i.world);
                if (dot(N, V) < 0.0) N = -N;
                float NoV = saturate(dot(N, V));
                float F = 0.04 + 0.96 * pow(1.0 - NoV, 5.0);
                float Rpane = 2.0 * F / (1.0 + F);   // duas faces

                float3 R = reflect(-V, N);
                float3 dir = R;
                if (unity_SpecCube0_ProbePosition.w > 0.0)
                    dir = BoxProjectedCubemapDirection(R, i.world, unity_SpecCube0_ProbePosition, unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
                float3 env = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, dir, 0.0), unity_SpecCube0_HDR);

                // Brilho do sol no vidro liso (GGX bem fechado).
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 H = normalize(L + V);
                float NoH = saturate(dot(N, H)), NoL = saturate(dot(N, L));
                const float a2 = 0.0004;
                float d = NoH * NoH * (a2 - 1.0) + 1.0;
                float sun = a2 / (UNITY_PI * d * d) * 0.25 * NoL;

                float T = _BG_GlassT * dot(_Color.rgb, float3(0.333, 0.333, 0.333));

                float3 rgb = (env + _LightColor0.rgb * sun) * Rpane * _BG_GlassReflect;
                return float4(rgb, (1.0 - Rpane) * T);
            }
            ENDCG
        }
    }
}
