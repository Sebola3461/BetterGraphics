// Tabelas da atmosfera física (Atmosphere.cginc), desenhadas com Graphics.Blit em RenderTextures (sem compute):
//  0: transmitância 256x64 (só muda com o aerossol);
//  1: espalhamento múltiplo 32x32 (idem);
//  2: vista do céu 192x108 (a cada quadro: sol e altura da câmera).
Shader "Hidden/BetterGraphics/AtmosphereLut"
{
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "Atmosphere.cginc"

    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

    v2f vert(appdata_img v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }
    ENDCG

    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        Pass // 0: transmitância
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target
            {
                float r, mu;
                BGUvToTransmittance(i.uv, r, mu);
                float3 ro = float3(0.0, r, 0.0);
                float3 rd = float3(sqrt(saturate(1.0 - mu * mu)), mu, 0.0);
                float t = max(0.0, BGRaySphere(ro, rd, BG_R_TOP));
                const int N = 40;
                float dt = t / N;
                float3 depth = 0.0;
                for (int k = 0; k < N; k++)
                {
                    float h = length(ro + rd * ((k + 0.5) * dt)) - BG_R_GROUND;
                    depth += BGSampleMedium(h).ext * dt;
                }
                return float4(exp(-depth), 1.0);
            }
            ENDCG
        }

        Pass // 1: espalhamento múltiplo (ordens 2 em diante como série geométrica, Hillaire seção 5.5)
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target
            {
                float sunMu = i.uv.x * 2.0 - 1.0;
                float r = BG_R_GROUND + max(0.01, i.uv.y * (BG_R_TOP - BG_R_GROUND));
                float3 ro = float3(0.0, r, 0.0);
                float3 sunDir = float3(sqrt(saturate(1.0 - sunMu * sunMu)), sunMu, 0.0);
                const float isotropic = 1.0 / (4.0 * UNITY_PI);
                const int SQRT_DIRS = 8, STEPS = 20;
                float3 L2 = 0.0, fms = 0.0;
                for (int a = 0; a < SQRT_DIRS; a++)
                for (int b = 0; b < SQRT_DIRS; b++)
                {
                    float cosT = 1.0 - 2.0 * (b + 0.5) / SQRT_DIRS;
                    float sinT = sqrt(saturate(1.0 - cosT * cosT));
                    float phi = 2.0 * UNITY_PI * (a + 0.5) / SQRT_DIRS;
                    float3 rd = float3(sinT * cos(phi), cosT, sinT * sin(phi));
                    float tTop = BGRaySphere(ro, rd, BG_R_TOP);
                    float tGround = BGRaySphere(ro, rd, BG_R_GROUND);
                    float t = tGround > 0.0 ? min(tTop, tGround) : tTop;
                    float dt = max(t, 0.0) / STEPS;
                    float3 L = 0.0, F = 0.0, T = 1.0;
                    for (int k = 0; k < STEPS; k++)
                    {
                        float3 p = ro + rd * ((k + 0.5) * dt);
                        float rr = length(p);
                        float mu = dot(p / rr, sunDir);
                        BGMedium m = BGSampleMedium(rr - BG_R_GROUND);
                        float3 scat = m.scatR + m.scatM;
                        float lit = BGRaySphere(p, sunDir, BG_R_GROUND) > 0.0 ? 0.0 : 1.0;
                        float3 stepT = exp(-m.ext * dt);
                        float3 integ = (1.0 - stepT) / max(m.ext, 1e-7);
                        L += T * scat * isotropic * BGTransmittance(rr, mu) * lit * integ;
                        F += T * scat * integ;
                        T *= stepT;
                    }
                    if (tGround > 0.0)
                    {
                        float3 p = ro + rd * tGround;
                        float3 n = normalize(p);
                        L += T * BGTransmittance(BG_R_GROUND, dot(n, sunDir)) * saturate(dot(n, sunDir)) * _BG_AtmGroundAlbedo / UNITY_PI;
                    }
                    L2 += L;
                    fms += F;
                }
                float n = SQRT_DIRS * SQRT_DIRS;
                // Esfera inteira com fase isotrópica: 4 pi x 1/(4 pi) x média das direções.
                L2 /= n;
                fms /= n;
                return float4(L2 / max(1.0 - fms, 1e-3), 1.0);
            }
            ENDCG
        }

        Pass // 2: vista do céu da câmera
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target
            {
                float r = BG_R_GROUND + max(0.001, _BG_AtmCameraHeight);
                float viewZenithCos, lightViewCos;
                BGUvToSkyView(i.uv, r, viewZenithCos, lightViewCos);
                float sunMu = clamp(_BG_AtmSunDir.y, -1.0, 1.0);
                float3 sunDir = float3(sqrt(saturate(1.0 - sunMu * sunMu)), sunMu, 0.0);
                float sinV = sqrt(saturate(1.0 - viewZenithCos * viewZenithCos));
                float3 rd = float3(sinV * lightViewCos, viewZenithCos, sinV * sqrt(saturate(1.0 - lightViewCos * lightViewCos)));
                float3 T;
                float3 L = BGIntegrateScattering(float3(0.0, r, 0.0), rd, sunDir, 1e9, 32, T);
                return float4(L, 1.0);
            }
            ENDCG
        }
    }
}
