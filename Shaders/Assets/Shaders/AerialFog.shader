// Névoa por perspectiva aérea (Atmosphere.cginc): o que está longe ganha a luz que a atmosfera espalha entre a câmera
// e ele (a mesma do céu naquela direção) e perde a própria luz pela transmitância. Substitui a névoa de distância e
// de altura do Enviro; a densidade do clima entra como a neblina da atmosfera (_BG_AtmHaze).
//  0: tabela 2048x32 = 32 fatias de distância de 64x32 (azimute em relação ao sol x zênite), refeita a cada quadro;
//  1: aplica na tela pela profundidade (antes dos efeitos do Enviro: as nuvens dele ficam por cima).
Shader "Hidden/BetterGraphics/AerialFog"
{
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "Atmosphere.cginc"

    #define BG_FOG_SLICES 32.0
    #define BG_FOG_W 64.0
    #define BG_FOG_MAX_KM 8.0

    sampler2D _BG_AerialLut;
    sampler2D_float _CameraDepthTexture;
    float4x4 _BG_FogInvViewProj;
    float _BG_AtmSunScale;          // pi x sol no topo, com o corte do tempo fechado
    float3 _BG_SkyCityGlow;
    float4 _BG_SkyWeather;
    float3 _BG_FogAmbient;          // luz ambiente média da cena (sonda da Unity): ilumina a neblina grossa
    float _BG_FogHazeShare;         // fração da extinção perto do chão que é neblina (o resto é ar limpo)
    float _BG_FogDebug;             // lab: 1 = profundidade crua, 2 = distância (vermelho a cada 100 m), 3 = transmitância

    float BGFogSliceDistance(float slice) { float k = (slice + 1.0) / BG_FOG_SLICES; return BG_FOG_MAX_KM * k * k; }
    ENDCG

    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        Pass // 0: tabela
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

            float4 frag(v2f i) : SV_Target
            {
                float x = i.uv.x * BG_FOG_SLICES * BG_FOG_W;
                float slice = floor(x / BG_FOG_W);
                float u = (x - slice * BG_FOG_W) / BG_FOG_W;
                float lightViewCos = -(u * u * 2.0 - 1.0);
                float viewZenithCos = i.uv.y * 2.0 - 1.0;
                float r = BG_R_GROUND + max(0.001, _BG_AtmCameraHeight);
                float sunMu = clamp(_BG_AtmSunDir.y, -1.0, 1.0);
                float3 sunDir = float3(sqrt(saturate(1.0 - sunMu * sunMu)), sunMu, 0.0);
                float sinV = sqrt(saturate(1.0 - viewZenithCos * viewZenithCos));
                float3 rd = float3(sinV * lightViewCos, viewZenithCos, sinV * sqrt(saturate(1.0 - lightViewCos * lightViewCos)));
                float3 T;
                float3 L = BGIntegrateScattering(float3(0.0, r, 0.0), rd, sunDir, BGFogSliceDistance(slice), 12, T);
                return float4(L, dot(T, float3(0.2126, 0.7152, 0.0722)));
            }
            ENDCG
        }

        Pass // 1: aplica (cor = cena x transmitância + luz espalhada)
        {
            Blend One SrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = float4(v.vertex.xy, 0.0, 1.0);
                o.uv = v.vertex.xy * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    o.uv.y = 1.0 - o.uv.y;
                #endif
                return o;
            }

            float4 SampleSlice(float slice, float2 dirUv)
            {
                float x = (slice * BG_FOG_W + clamp(dirUv.x * BG_FOG_W, 0.5, BG_FOG_W - 0.5)) / (BG_FOG_SLICES * BG_FOG_W);
                return tex2Dlod(_BG_AerialLut, float4(x, dirUv.y, 0.0, 0.0));
            }

            float4 frag(v2f i) : SV_Target
            {
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                if (_BG_FogDebug > 0.5 && _BG_FogDebug < 1.5) return float4(raw * 50.0, 0.0, 0.0, 0.0);
                #if UNITY_REVERSED_Z
                    if (raw <= 1e-6) return float4(0.0, 0.0, 0.0, 1.0);   // céu: já tem a atmosfera
                #else
                    if (raw >= 1.0 - 1e-6) return float4(0.0, 0.0, 0.0, 1.0);
                #endif
                float2 ndc = i.uv * 2.0 - 1.0;
                #if UNITY_UV_STARTS_AT_TOP
                    ndc.y = -ndc.y;
                #endif
                float4 w = mul(_BG_FogInvViewProj, float4(ndc, raw, 1.0));
                float3 toP = w.xyz / w.w - _WorldSpaceCameraPos;
                float dist = length(toP) * 0.001;   // km
                float3 dir = toP / max(length(toP), 1e-4);

                float3 sun = normalize(_BG_AtmSunDir);
                float2 h = dir.xz, hs = sun.xz;
                float lh = length(h), ls = length(hs);
                float lightViewCos = (lh > 1e-4 && ls > 1e-4) ? dot(h / lh, hs / ls) : 1.0;
                float2 dirUv = float2(sqrt(saturate(-lightViewCos * 0.5 + 0.5)), dir.y * 0.5 + 0.5);

                // Fatias em distância quadrática: interpola entre as duas vizinhas (antes da primeira, a partir de zero).
                float s = sqrt(saturate(dist / BG_FOG_MAX_KM)) * BG_FOG_SLICES - 1.0;
                float s0 = floor(s), f = s - s0;
                float4 a = s0 < 0.0 ? float4(0.0, 0.0, 0.0, 1.0) : SampleSlice(min(s0, BG_FOG_SLICES - 1.0), dirUv);
                float4 b = SampleSlice(clamp(s0 + 1.0, 0.0, BG_FOG_SLICES - 1.0), dirUv);
                float4 fog = lerp(a, b, s0 < 0.0 ? saturate(s + 1.0) : f);

                if (_BG_FogDebug > 1.5 && _BG_FogDebug < 2.5) return float4(frac(dist * 10.0), 0.0, 0.0, 0.0);
                if (_BG_FogDebug > 2.5) return float4(fog.aaa, 0.0);
                // A neblina grossa espalha muitas vezes a luz do céu e das nuvens e brilha como uma parede branca à luz
                // ambiente; o espalhamento múltiplo da tabela vem só do sol direto, que quase não chega ao fundo dela.
                float3 L = fog.rgb * _BG_AtmSunScale + (_BG_FogAmbient * 0.9 * _BG_FogHazeShare + _BG_SkyCityGlow) * (1.0 - fog.a);
                float lum = dot(L, float3(0.2126, 0.7152, 0.0722));
                L = lerp(L, lum * _BG_SkyWeather.rgb, _BG_SkyWeather.a);
                return float4(L, fog.a);
            }
            ENDCG
        }
    }
}
