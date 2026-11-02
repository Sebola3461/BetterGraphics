// Respingos das gotas que batem nas superfícies, sem estado e presos ao mundo. O chão em volta da câmera é uma
// grade fixa de células (coordenadas absolutas): cada célula tem uma vaga de respingo que se repete num ciclo
// próprio e, a cada ciclo, cai num ponto sorteado dentro dela, na altura do topo da superfície (mapa de abrigo).
// Andar com a câmera só muda quais células são desenhadas; o respingo de cada célula continua no mesmo lugar.
// Cada respingo:
//  - parte 0: anel deitado na superfície, abrindo por ~0,14 s (alinhado ao chão, não gira com a câmera);
//  - partes 1 a 5: gotículas lançadas em leque, em parábola (gravidade real), desenhadas como riscos pela
//    velocidade relativa à câmera, como a chuva.
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

            float4 _BG_RF_Splash;      // células por lado, tamanho da célula (m), ciclo (s), fração visível
            float4 _BG_RF_Wind;        // w = tempo (s)
            float4 _BG_RF_Fall;        // y = esticamento, w = densidade da névoa
            float4 _BG_RF_CamVel;
            sampler2D _BG_RF_Shelter;
            float4 _BG_RF_ShelterArea;
            float4x4 _BG_RF_Bus;
            float _BG_RF_HasBus;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 uv : TEXCOORD0;      // anel: posição em raios (x, y); gotícula: lado, ao longo; z = 0 anel, 1 gotícula
                float4 col : TEXCOORD1;
            };

            // Hash de Dave Hoskins: estável com coordenadas de célula grandes.
            float Hash21(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            v2f Hidden() { v2f o = (v2f)0; o.pos = float4(2, 2, 2, 1); return o; }

            v2f vert(float4 vertex : POSITION, float2 corner : TEXCOORD0, float2 index : TEXCOORD1)
            {
                if (_BG_RF_ShelterArea.w < 0.5) return Hidden();
                float id = index.x;
                float slot = floor(id / 6.0);
                float part = id - slot * 6.0;
                float K = _BG_RF_Splash.x, cellSize = _BG_RF_Splash.y;
                float2 cell = floor(_WorldSpaceCameraPos.xz / cellSize) + float2(fmod(slot, K), floor(slot / K)) - floor(K * 0.5);

                float cycle = _BG_RF_Splash.z * lerp(0.7, 1.3, Hash21(cell));
                float t = _BG_RF_Wind.w / cycle + Hash21(cell + 7.7);
                float n = floor(t);
                float tau = frac(t) * cycle;                    // segundos desde o impacto
                float2 seed = cell + n * float2(13.1, 7.9);
                if (Hash21(seed + 3.3) > _BG_RF_Splash.w) return Hidden();

                float2 xz = (cell + float2(Hash21(seed), Hash21(seed + 1.7))) * cellSize;
                float2 suv = (xz - _BG_RF_ShelterArea.xy) * _BG_RF_ShelterArea.z + 0.5;
                if (any(suv < 0.0) || any(suv > 1.0)) return Hidden();
                float y = tex2Dlod(_BG_RF_Shelter, float4(suv, 0, 0)).r;
                if (y < -5000.0 || abs(y - _WorldSpaceCameraPos.y) > 40.0) return Hidden();
                float3 base = float3(xz.x, y + 0.003, xz.y);
                if (_BG_RF_HasBus > 0.5)
                {
                    float3 g = mul(_BG_RF_Bus, float4(base + float3(0, 0.05, 0), 1.0)).xyz;
                    if (g.x > 0.0 && g.x < 1.0 && g.z > 0.0 && g.z < 1.0 && g.y < 1.0) return Hidden();
                }

                float3 toCam = _WorldSpaceCameraPos - base;
                float dist = length(toCam);
                float radius = K * cellSize * 0.5;
                float fade = (1.0 - smoothstep(radius * 0.5, radius * 0.95, dist)) * exp(-dist * _BG_RF_Fall.w * 1.5);
                float energy = lerp(0.6, 1.2, Hash21(seed + 5.1));   // gota maior respinga mais alto e mais longe
                v2f o = (v2f)0;
                float3 pos;
                if (part < 0.5)
                {
                    const float ringLife = 0.14;
                    if (tau > ringLife) return Hidden();
                    float k = tau / ringLife;
                    float r = (0.012 + 0.07 * pow(k, 0.6)) * energy;
                    float2 q = (corner * 2.0 - 1.0) * 1.25;
                    pos = base + float3(q.x, 0.0, q.y) * r;
                    o.uv = float3(q, 0.0);
                    o.col = float4(RainLight(base, -toCam / max(dist, 1e-3)) * 1.2, 0.3 * (1.0 - k) * (1.0 - k) * fade);
                }
                else
                {
                    float j = part;
                    float a = Hash21(seed + j * 3.17) * 6.2832;
                    float vh = lerp(0.3, 1.0, Hash21(seed + j * 5.71)) * energy;
                    float v0 = lerp(0.7, 1.6, Hash21(seed + j * 7.43)) * energy;
                    float life = 2.0 * v0 / 9.81;
                    if (tau > life) return Hidden();
                    float2 d = float2(cos(a), sin(a));
                    float3 p = base + float3(d.x * vh * tau, v0 * tau - 4.905 * tau * tau, d.y * vh * tau);
                    float3 vel = float3(d.x * vh, v0 - 9.81 * tau, d.y * vh);
                    float3 vr = vel - _BG_RF_CamVel.xyz;
                    float speed = length(vr);
                    float3 axis = vr / max(speed, 1e-3);
                    float3 view = normalize(p - _WorldSpaceCameraPos);
                    float pixel = length(p - _WorldSpaceCameraPos) * 2.0 / (unity_CameraProjection._m11 * _ScreenParams.y);
                    float len = max(speed * 0.008 * _BG_RF_Fall.y, pixel * 2.0);
                    float width0 = 0.0009 * energy;
                    float width = max(width0, pixel * 1.3);
                    float3 side = normalize(cross(axis, view));
                    pos = p + axis * (corner.y - 0.5) * len + side * (corner.x - 0.5) * width;
                    o.uv = float3(corner, 1.0);
                    o.col = float4(RainLight(p, view) * 1.2, 0.6 * fade * sqrt(width0 / width) * (1.0 - tau / life * 0.5));
                }
                o.pos = mul(UNITY_MATRIX_VP, float4(pos, 1.0));
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float a;
                if (i.uv.z < 0.5)
                {
                    float r = length(i.uv.xy);
                    a = smoothstep(0.22, 0.0, abs(r - 1.0)) + smoothstep(1.0, 0.0, r) * 0.15;
                }
                else
                {
                    float across = 1.0 - abs(i.uv.x * 2.0 - 1.0);
                    a = across * across * smoothstep(0.0, 0.3, i.uv.y) * smoothstep(1.0, 0.7, i.uv.y);
                }
                return float4(i.col.rgb, i.col.a * a);
            }
            ENDCG
        }
    }
}
