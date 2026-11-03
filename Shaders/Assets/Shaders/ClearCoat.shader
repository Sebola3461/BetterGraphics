// Verniz da pintura dos veículos: segunda camada desenhada por cima da lataria ("_ref"), sem trocar o material do
// veículo (cada mod traz o seu). A tinta embaixo fica fosca e dielétrica; esta camada é a película lisa de verniz:
//  - Fresnel do verniz (F0 0,04): quase nada de frente, espelho de lado;
//  - reflexo da sonda com projeção em caixa, brilho do sol com a sombra dele e brilho das luzes locais;
//  - a tinta embaixo recebe só o que o verniz deixa passar (Blend One OneMinusSrcAlpha, alfa = Fresnel).
// Sem passe de sombra (a lataria já projeta a dela) e sem passe deferred: desenha depois da luz da cena.
Shader "BetterGraphics/ClearCoat"
{
    Properties
    {
        _BG_CoatSmoothness ("Lisura do verniz", Range(0, 1)) = 0.95
        _BG_CoatStrength ("Força", Range(0, 1)) = 1
    }
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "Lighting.cginc"
    #include "AutoLight.cginc"
    #include "UnityStandardUtils.cginc"

    float _BG_CoatSmoothness, _BG_CoatStrength;

    struct v2f
    {
        float4 pos : SV_POSITION;
        float3 world : TEXCOORD0;
        float3 n : TEXCOORD1;
        UNITY_LIGHTING_COORDS(2, 3)
    };

    v2f vert(appdata_full v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
        o.n = UnityObjectToWorldNormal(v.normal);
        UNITY_TRANSFER_LIGHTING(o, v.texcoord1);
        return o;
    }

    float Roughness()
    {
        float r = 1.0 - _BG_CoatSmoothness;
        // Piso no GGX: com o verniz liso demais o sol vira um ponto que pisca a cada quadro do TAA.
        return max(r * r, 0.02);
    }

    // Brilho de uma luz no verniz (GGX + Smith), na convenção da Unity (x pi, a difusa não divide por pi).
    float CoatSpecular(float3 N, float3 V, float3 L)
    {
        float3 H = normalize(L + V);
        float NoL = saturate(dot(N, L)), NoV = saturate(dot(N, V)) + 1e-4;
        float NoH = saturate(dot(N, H)), LoH = saturate(dot(L, H));
        float a = Roughness(), a2 = a * a;
        float d = NoH * NoH * (a2 - 1.0) + 1.0;
        float D = a2 / (UNITY_PI * d * d);
        float vis = 0.5 / (NoL * (NoV * (1.0 - a) + a) + NoV * (NoL * (1.0 - a) + a) + 1e-5);
        float F = 0.04 + 0.96 * pow(1.0 - LoH, 5.0);
        return D * vis * F * NoL * UNITY_PI;
    }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Geometry+10" "RenderType" = "Opaque" "IgnoreProjector" = "True" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite Off
            Offset -1, -1
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma target 3.5

            float4 frag(v2f i) : SV_Target
            {
                float3 N = normalize(i.n);
                float3 V = normalize(_WorldSpaceCameraPos - i.world);
                float NoV = saturate(dot(N, V));
                float pr = 1.0 - _BG_CoatSmoothness;
                // Fresnel com rugosidade: o verniz gasto não vira espelho total no ângulo rasante.
                float F = 0.04 + (max(1.0 - pr, 0.04) - 0.04) * pow(1.0 - NoV, 5.0);

                float3 dir = reflect(-V, N);
                if (unity_SpecCube0_ProbePosition.w > 0.0)
                    dir = BoxProjectedCubemapDirection(dir, i.world, unity_SpecCube0_ProbePosition, unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
                float mip = pr * (1.7 - 0.7 * pr) * UNITY_SPECCUBE_LOD_STEPS;
                float3 env = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, dir, mip), unity_SpecCube0_HDR);

                UNITY_LIGHT_ATTENUATION(atten, i, i.world);
                float3 sun = _LightColor0.rgb * CoatSpecular(N, V, normalize(_WorldSpaceLightPos0.xyz)) * atten;

                float k = _BG_CoatStrength;
                return float4((env * F + sun) * k, F * k);
            }
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }
            ZWrite Off
            Offset -1, -1
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdadd_fullshadows
            #pragma target 3.5

            float4 frag(v2f i) : SV_Target
            {
                float3 N = normalize(i.n);
                float3 V = normalize(_WorldSpaceCameraPos - i.world);
                float3 L = normalize(UnityWorldSpaceLightDir(i.world));
                UNITY_LIGHT_ATTENUATION(atten, i, i.world);
                return float4(_LightColor0.rgb * CoatSpecular(N, V, L) * atten * _BG_CoatStrength, 0.0);
            }
            ENDCG
        }
    }
}
