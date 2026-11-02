// Respingos das gotas que batem nas superfícies, sem estado: o respingo i se repete num ciclo curto e, a cada
// ciclo, cai num ponto sorteado em volta da câmera, na altura do topo da superfície ali (mapa de abrigo). Assim só
// respinga onde a chuva chega: no telhado sim, embaixo dele não. Cada respingo é uma coroa de gotículas que sobem
// e caem em parábola, com o anel no chão abrindo, num quadro de frente para a câmera (girando só em volta do eixo
// vertical).
Shader "Hidden/BetterGraphics/RainSplash"
{
    SubShader
    {
        Tags { "Queue" = "Transparent-19" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite Off ZTest LEqual Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "RainLights.cginc"

            float4 _BG_RF_Splash;      // quantos, duração do ciclo (s), raio da área (m), fração visível
            float4 _BG_RF_Wind;        // w = tempo (s)
            float4 _BG_RF_Fall;        // w = densidade da névoa
            sampler2D _BG_RF_Shelter;
            float4 _BG_RF_ShelterArea;
            float4x4 _BG_RF_Bus;
            float _BG_RF_HasBus;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;      // -1 a 1 de lado, 0 a 1 de altura
                float4 col : TEXCOORD1;
                float2 life : TEXCOORD2;    // fase do respingo (0 a 1) e semente
            };

            float Hash(float n) { return frac(sin(n * 12.9898) * 43758.5453); }

            v2f vert(float4 vertex : POSITION, float2 corner : TEXCOORD0, float2 index : TEXCOORD1)
            {
                v2f o = (v2f)0;
                float id = index.x;
                if (id / max(_BG_RF_Splash.x, 1.0) > _BG_RF_Splash.w || _BG_RF_ShelterArea.w < 0.5) { o.pos = float4(2, 2, 2, 1); return o; }
                float cycle = _BG_RF_Splash.y * lerp(0.8, 1.2, Hash(id + 5.3));
                float t = _BG_RF_Wind.w / cycle + Hash(id);
                float n = floor(t), f = frac(t);
                float seed = id * 7.13 + n * 3.71;
                // Mais respingos perto da câmera, onde dá para ver cada um.
                float ang = Hash(seed) * 6.2832;
                float r = lerp(0.6, _BG_RF_Splash.z, Hash(seed + 1.9) * Hash(seed + 1.9));
                float2 xz = _WorldSpaceCameraPos.xz + float2(cos(ang), sin(ang)) * r;
                float2 suv = (xz - _BG_RF_ShelterArea.xy) * _BG_RF_ShelterArea.z + 0.5;
                if (any(suv < 0.0) || any(suv > 1.0)) { o.pos = float4(2, 2, 2, 1); return o; }
                float y = tex2Dlod(_BG_RF_Shelter, float4(suv, 0, 0)).r;
                float3 base = float3(xz.x, y + 0.005, xz.y);
                if (y < -5000.0 || abs(y - _WorldSpaceCameraPos.y) > 40.0) { o.pos = float4(2, 2, 2, 1); return o; }
                if (_BG_RF_HasBus > 0.5)
                {
                    float3 g = mul(_BG_RF_Bus, float4(base + float3(0, 0.05, 0), 1.0)).xyz;
                    if (g.x > 0.0 && g.x < 1.0 && g.z > 0.0 && g.z < 1.0 && g.y < 1.0) { o.pos = float4(2, 2, 2, 1); return o; }
                }

                float size = lerp(0.07, 0.16, Hash(seed + 4.4));
                float3 toCam = _WorldSpaceCameraPos - base;
                float3 side = normalize(cross(float3(0, 1, 0), toCam));
                float3 pos = base + side * (corner.x * 2.0 - 1.0) * size + float3(0, 1, 0) * corner.y * size * 0.9;
                o.pos = mul(UNITY_MATRIX_VP, float4(pos, 1.0));
                o.uv = float2(corner.x * 2.0 - 1.0, corner.y);
                o.life = float2(f, Hash(seed + 8.8));
                float dist = length(toCam);
                o.col = float4(RainLight(base + float3(0, 0.05, 0), -toCam / max(dist, 1e-3)), 0.55 * exp(-dist * _BG_RF_Fall.w * 1.5) * saturate(1.5 - dist / _BG_RF_Splash.z));
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float f = i.life.x;
                if (f > 0.6) discard;   // o resto do ciclo o respingo já acabou
                float k = f / 0.6;
                float a = 0.0;
                // Coroa: gotículas lançadas em leque, subindo e caindo em parábola.
                [unroll] for (int j = 0; j < 7; j++)
                {
                    float s = frac(i.life.y * 13.7 + j * 0.618);
                    float dir = (j / 6.0) * 2.0 - 1.0 + (s - 0.5) * 0.2;
                    float2 p = float2(dir * k * 0.85, 4.0 * (0.35 + 0.5 * s) * k * (1.0 - k) * 0.9);
                    float d = length((i.uv - p) * float2(1.0, 1.4));
                    a = max(a, smoothstep(0.09, 0.03, d));
                }
                // Anel na superfície abrindo (visto de lado vira uma faixa rente ao chão).
                float ring = smoothstep(0.12, 0.0, abs(abs(i.uv.x) - k * 0.95)) * smoothstep(0.12, 0.0, i.uv.y) * (1.0 - k);
                a = max(a, ring * 0.7);
                return float4(i.col.rgb, i.col.a * a * (1.0 - k * k));
            }
            ENDCG
        }
    }
}
