// Chuva caindo, toda calculada na GPU, sem estado: a gota i nasce num ponto sorteado por hash, cai na velocidade
// final com o vento e é reaproveitada numa caixa em volta da câmera (a posição é presa ao mundo; só dá a volta na
// caixa). Duas camadas: perto (riscos finos e muitos) e longe (mais espaçados, até a névoa).
//  - Vento: base do clima com rajadas (CPU) + turbulência de ruído Simplex 3D que desvia cada gota.
//  - Risco: esticado pela velocidade relativa à câmera (andando, a chuva vem inclinada de frente), largura de no
//    mínimo um pixel com a transparência compensada (risco fino de longe não cintila).
//  - Colisão: o mapa de abrigo (topo das superfícies, visto de cima) corta o risco na superfície; dentro da
//    coluna do ônibus do jogador não chove.
//  - Cor: o que a gota mostra é o ambiente refratado (luz do céu) mais o sol espalhado para a frente.
Shader "Hidden/BetterGraphics/RainFall"
{
    SubShader
    {
        Tags { "Queue" = "Transparent-20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite Off ZTest LEqual Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "RainLights.cginc"
            #include "SimplexNoise.cginc"

            float4 _BG_RF_Near;        // tamanho da caixa (m) e quantas gotas são da camada de perto
            float4 _BG_RF_Far;         // tamanho da caixa de longe e total de gotas
            float4 _BG_RF_Wind;        // vento (m/s) em x, z; força da turbulência (m); tempo (s, dá a volta em 1000)
            float4 _BG_RF_Fall;        // velocidade final, esticamento, fração de gotas visíveis, densidade da névoa
            float4 _BG_RF_CamVel;      // velocidade da câmera (m/s)
            sampler2D _BG_RF_Shelter;  // altura do topo das superfícies
            float4 _BG_RF_ShelterArea; // x, z do centro, 1/lado, 1 se existe
            float4x4 _BG_RF_Bus;       // mundo -> caixa do ônibus do jogador (0 a 1)
            float _BG_RF_HasBus;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;      // x de lado (0 a 1), y ao longo (0 a 1)
                float4 col : TEXCOORD1;     // cor e transparência
                float2 cut : TEXCOORD2;     // y do mundo neste vértice e altura do abrigo
            };

            float Hash(float n) { return frac(sin(n * 12.9898) * 43758.5453); }
            float3 Hash3(float n) { return float3(Hash(n), Hash(n + 17.31), Hash(n + 41.77)); }

            float ShelterHeight(float2 xz)
            {
                if (_BG_RF_ShelterArea.w < 0.5) return -10000.0;
                float2 uv = (xz - _BG_RF_ShelterArea.xy) * _BG_RF_ShelterArea.z + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) return -10000.0;
                return tex2Dlod(_BG_RF_Shelter, float4(uv, 0, 0)).r;
            }

            float3 Turbulence(float3 w, float t)
            {
                float3 q = w * 0.06 + float3(0.0, t * 0.35, 0.0);
                return float3(snoise(q), 0.0, snoise(q + 31.4)) * _BG_RF_Wind.z;
            }

            v2f vert(float4 vertex : POSITION, float2 corner : TEXCOORD0, float2 index : TEXCOORD1)
            {
                v2f o = (v2f)0;
                float id = index.x;
                bool far = id >= _BG_RF_Near.w;
                float3 box = far ? _BG_RF_Far.xyz : _BG_RF_Near.xyz;
                // Fração visível: garoa usa só as primeiras gotas de cada camada.
                float order = far ? (id - _BG_RF_Near.w) / max(_BG_RF_Far.w - _BG_RF_Near.w, 1.0) : id / max(_BG_RF_Near.w, 1.0);
                if (order > _BG_RF_Fall.z) { o.pos = float4(2, 2, 2, 1); return o; }

                float t = _BG_RF_Wind.w;
                float3 h = Hash3(id);
                float fall = _BG_RF_Fall.x * lerp(0.8, 1.1, Hash(id + 3.7));   // gotas menores caem mais devagar
                float3 vel = float3(_BG_RF_Wind.x, -fall, _BG_RF_Wind.y);
                float3 p = h * box + vel * t;
                float3 rel = (frac((p - _WorldSpaceCameraPos) / box + 0.5) - 0.5) * box;
                float3 w = _WorldSpaceCameraPos + rel;
                float3 turb = Turbulence(w, t);
                vel += (turb - Turbulence(w, t - 0.1)) / 0.1;
                w += turb;

                // Esmaece em esfera em volta da câmera, bem antes da borda da caixa (onde a gota dá a volta): com o
                // esmaecimento só na borda, a caixa aparecia como um quadrado de chuva em volta do jogador.
                float dist = length(w - _WorldSpaceCameraPos);
                float radius = min(box.x, box.y) * 0.5;
                float fade = 1.0 - smoothstep(radius * 0.45, radius * 0.95, dist);
                // A camada de longe entra onde a de perto sai: a densidade muda aos poucos, sem degrau.
                if (far) fade *= smoothstep(_BG_RF_Near.x * 0.25, _BG_RF_Near.x * 0.5, dist);
                fade *= exp(-dist * _BG_RF_Fall.w * 1.5);
                if (dist < 0.35) fade = 0.0;   // colada na lente seria uma barra na tela

                // Dentro da coluna do ônibus do jogador (do chão ao teto) não chove.
                if (_BG_RF_HasBus > 0.5)
                {
                    float3 g = mul(_BG_RF_Bus, float4(w, 1.0)).xyz;
                    if (g.x > 0.0 && g.x < 1.0 && g.z > 0.0 && g.z < 1.0 && g.y < 1.0) fade = 0.0;
                }
                if (fade <= 0.001) { o.pos = float4(2, 2, 2, 1); return o; }

                // Risco: comprimento pela velocidade relativa à câmera no tempo de exposição; largura de gota.
                float3 vr = vel - _BG_RF_CamVel.xyz;
                float speed = length(vr);
                float3 axis = vr / max(speed, 1e-3);
                float pixel = dist * 2.0 / (unity_CameraProjection._m11 * _ScreenParams.y);   // metros por pixel
                float len = max(speed * 0.008 * _BG_RF_Fall.y, pixel * 2.0);
                float width0 = far ? 0.004 : lerp(0.0012, 0.0024, Hash(id + 9.1));
                float width = max(width0, pixel * 1.3);
                // Risco mais fino que um pixel: alargado, com a transparência compensada pela raiz (linear, a chuva
                // de longe sumia; o olho percebe o risco fino mais do que a área dele).
                float alphaScale = sqrt(width0 / width);
                float3 view = normalize(w - _WorldSpaceCameraPos);
                float3 side = normalize(cross(axis, view));
                float3 pos = w + axis * (corner.y - 0.5) * len + side * (corner.x - 0.5) * width;
                o.pos = mul(UNITY_MATRIX_VP, float4(pos, 1.0));
                o.uv = corner;
                o.cut = float2(pos.y, ShelterHeight(pos.xz));

                // A gota mostra o céu refratado e espalha para a frente a luz do sol e das luzes próximas.
                float3 col = RainLight(w, view);
                float base = far ? 0.25 : 0.5;
                o.col = float4(col, base * fade * saturate(alphaScale));
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // A gota para no topo da superfície (telhado, marquise, chão).
                if (i.cut.x < i.cut.y) discard;
                float across = 1.0 - abs(i.uv.x * 2.0 - 1.0);
                float along = smoothstep(0.0, 0.25, i.uv.y) * smoothstep(1.0, 0.6, i.uv.y);
                float a = i.col.a * across * across * along;
                return float4(i.col.rgb, a);
            }
            ENDCG
        }
    }
}
