// Reflexos deferred próprios (troca o Internal-DeferredReflections da Unity). Um passe por sonda de reflexo
// (e um de tela cheia com o reflexo padrão do céu):
//  - projeção em caixa quando a sonda tem caixa, mip pela rugosidade;
//  - energia do especular pela mesma aproximação da luz direta (Karis) com compensação dos espalhamentos
//    múltiplos: verniz e metal não ficam mais escuros que o certo;
//  - oclusão especular da oclusão do material (Lagarde): fresta e canto não refletem o céu;
//  - reflexo do céu (sonda padrão, sem caixa) pela fração de céu que o ponto enxerga, medida no mapa de abrigo:
//    embaixo da marquise não aparece céu aberto. A escala única da câmera (Ceu) é desfeita aqui.
// A mistura entre sondas e o passe que soma o resultado à luz continuam como os da Unity.
Shader "Hidden/BetterGraphics/DeferredReflections"
{
    Properties
    {
        _SrcBlend ("", Float) = 1
        _DstBlend ("", Float) = 1
    }
    SubShader
    {
        // 1: reflexo de uma sonda
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend [_SrcBlend] [_DstBlend]

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_deferred
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "UnityDeferredLibrary.cginc"
            #include "UnityStandardUtils.cginc"
            #include "UnityGBuffer.cginc"
            #include "UnityImageBasedLighting.cginc"

            sampler2D _CameraGBufferTexture0;
            sampler2D _CameraGBufferTexture1;
            sampler2D _CameraGBufferTexture2;
            sampler2D _BG_ReflShelter;
            float4 _BG_ReflShelterArea;   // x, z do centro, 1/lado, 1 se existe
            float _BG_ReflSkyUndo;        // 1 / escala de céu da câmera já aplicada ao reflexo padrão
            float _BG_ReflDebug;

            half3 DistanceFromAABB(half3 p, half3 mn, half3 mx) { return max(max(p - mx, mn - p), half3(0, 0, 0)); }

            float Height(float2 xz)
            {
                float2 uv = (xz - _BG_ReflShelterArea.xy) * _BG_ReflShelterArea.z + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) return -10000.0;
                return tex2Dlod(_BG_ReflShelter, float4(uv, 0, 0)).r;
            }

            // Céu visto na direção do reflexo: o que estiver acima da linha de visão até 12 m tapa.
            float SkyAlong(float3 p, float3 r)
            {
                if (_BG_ReflShelterArea.w < 0.5) return 1.0;
                float3 o = p + r * 0.2;
                float2 h = normalize(r.xz + 1e-4);
                float rise = r.y / max(length(r.xz), 0.05);   // subida por metro na horizontal
                float open = 1.0;
                [unroll] for (int k = 1; k <= 6; k++)
                {
                    float s = k * k * 0.35;   // 0,35 a 12,6 m
                    float above = Height(o.xz + h * s) - (o.y + rise * s);
                    open = min(open, saturate(1.0 - above / 0.5));
                }
                // Teto bem em cima, reflexo para o alto: some.
                float over = saturate((Height(o.xz) - o.y - 0.2) / 0.5) * saturate(r.y * 2.0);
                return open * (1.0 - over);
            }

            float2 EnvBrdf(float perceptual, float NoV)
            {
                const float4 c0 = float4(-1.0, -0.0275, -0.572, 0.022);
                const float4 c1 = float4(1.0, 0.0425, 1.04, -0.04);
                float4 r = perceptual * c0 + c1;
                float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
                return float2(-1.04, 1.04) * a004 + r.zw;
            }

            half4 frag(unity_v2f_deferred i) : SV_Target
            {
                i.ray = i.ray * (_ProjectionParams.z / i.ray.z);
                float2 uv = i.uv.xy / i.uv.w;
                float depth = Linear01Depth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float4 viewPos = float4(i.ray * depth, 1);
                float3 worldPos = mul(unity_CameraToWorld, viewPos).xyz;

                half4 g0 = tex2D(_CameraGBufferTexture0, uv);
                half4 g1 = tex2D(_CameraGBufferTexture1, uv);
                half4 g2 = tex2D(_CameraGBufferTexture2, uv);
                UnityStandardData data = UnityStandardDataFromGbuffer(g0, g1, g2);
                float3 V = -normalize(worldPos - _WorldSpaceCameraPos);
                float3 N = data.normalWorld;
                float3 R = reflect(-V, N);
                float perceptual = 1.0 - data.smoothness;
                float NoV = max(dot(N, V), 1e-4);

                // Projeção em caixa (sondas com caixa); a sonda padrão do céu não tem.
                bool boxed = unity_SpecCube0_ProbePosition.w > 0.0;
                float blendDistance = unity_SpecCube1_ProbePosition.w;
                float3 dir = R;
                if (boxed)
                {
                    float3 bmin = unity_SpecCube0_BoxMin.xyz - blendDistance, bmax = unity_SpecCube0_BoxMax.xyz + blendDistance;
                    float3 rbmax = (bmax - worldPos) / R;
                    float3 rbmin = (bmin - worldPos) / R;
                    float3 rbminmax = (R > 0.0f) ? rbmax : rbmin;
                    float fa = min(min(rbminmax.x, rbminmax.y), rbminmax.z);
                    dir = worldPos - unity_SpecCube0_ProbePosition.xyz + R * fa;
                }
                float mip = perceptualRoughnessToMipmapLevel(perceptual);
                float3 env = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, dir, mip), unity_SpecCube0_HDR);

                if (!boxed) env *= _BG_ReflSkyUndo * SkyAlong(worldPos, R);

                float2 ab = EnvBrdf(perceptual, NoV);
                float3 F0 = data.specularColor;
                float3 energy = 1.0 + F0 * (1.0 / max(ab.x + ab.y, 0.05) - 1.0);
                float a = perceptual * perceptual;
                float so = saturate(pow(NoV + data.occlusion, exp2(-16.0 * a - 1.0)) - 1.0 + data.occlusion);
                float3 rgb = env * (F0 * ab.x + ab.y) * energy * so;
                if (_BG_ReflDebug > 0.5) rgb = boxed ? float3(0, so, 0) : float3(so * SkyAlong(worldPos, R), 0, 0);

                half3 dist = DistanceFromAABB(worldPos, unity_SpecCube0_BoxMin.xyz, unity_SpecCube0_BoxMax.xyz);
                half falloff = saturate(1.0 - length(dist) / blendDistance);
                return half4(rgb, falloff);
            }
            ENDCG
        }

        // 2: soma o buffer de reflexos à luz
        Pass
        {
            ZWrite Off
            ZTest Always
            Blend [_SrcBlend] [_DstBlend]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile ___ UNITY_HDR_ON
            #include "UnityCG.cginc"

            sampler2D _CameraReflectionsTexture;
            struct v2f { float2 uv : TEXCOORD0; float4 pos : SV_POSITION; };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.uv = ComputeScreenPos(o.pos).xy;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 c = tex2D(_CameraReflectionsTexture, i.uv);
                #ifdef UNITY_HDR_ON
                    return float4(c.rgb, 0.0f);
                #else
                    return float4(exp2(-c.rgb), 0.0f);
                #endif
            }
            ENDCG
        }
    }
    Fallback Off
}
