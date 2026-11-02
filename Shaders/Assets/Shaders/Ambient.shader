// Luz ambiente própria, por pixel (passe deferred antes dos reflexos). A da Unity é uma só para a cena inteira
// (harmônicos esféricos do céu) e não sabe de teto nem de rebote. Aqui:
//  - a parcela da Unity que o G-buffer já somou (ambiente x albedo x oclusão) é tirada de RT3; partículas e vidros,
//    desenhados depois em forward, continuam com a dela;
//  - céu: o mesmo céu da Unity, com a saturação controlada, vezes a fração de céu que o ponto enxerga. Ela vem do
//    mapa de abrigo (altura do que está em cima, visto de cima): horizonte em 8 direções a até 10 m, pesado pela
//    normal. Embaixo da marquise e em rua estreita entra menos céu;
//  - rebote: o chão iluminado pelo sol devolve luz de baixo (sol x albedo do chão), e o que tapa o céu (paredes,
//    teto) devolve uma parte dela no lugar. É o que deixa a sombra de dia clara e quase neutra;
//  - noite: brilho do céu da cidade, neutro, no lugar do luar azul.
// Dentro da carroceria do ônibus do jogador não mexe (o Interior cuida da luz que entra pelas janelas).
Shader "Hidden/BetterGraphics/Ambient"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            sampler2D _CameraGBufferTexture0, _CameraGBufferTexture2;
            sampler2D _BG_AmbL;                 // cópia de RT3
            sampler2D _BG_AmbShelter;           // mapa de abrigo (altura do topo)
            float4 _BG_AmbShelterArea;          // x, z do centro, 1/lado, 1 se existe
            float4x4 _BG_AmbInvVP;
            float4x4 _BG_AmbBus;                // mundo -> caixa da carroceria (0 a 1)
            float _BG_AmbHasBus;
            float4 _BG_SHAr, _BG_SHAg, _BG_SHAb, _BG_SHBr, _BG_SHBg, _BG_SHBb, _BG_SHC;   // sonda da Unity, como a usada no G-buffer
            float _BG_AmbSkyScale;              // céu próprio = sonda x isto (tira a escala de câmera e cabine)
            float _BG_AmbSkySat;                // saturação do céu na luz ambiente (1 = a da sonda)
            float3 _BG_AmbBounce;               // radiância do chão iluminado (sol x albedo do chão)
            float _BG_AmbWall;                  // quanto do rebote o que tapa o céu devolve
            float3 _BG_AmbCityGlow;             // brilho do céu da cidade (noite)
            float _BG_AmbStrength;
            float _BG_AmbDebug;

            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(float4 vertex : POSITION) { v2f o; o.pos = float4(vertex.xy, 0.5, 1.0); return o; }

            float3 SH(float3 n)
            {
                float4 n4 = float4(n, 1.0);
                float3 x1 = float3(dot(_BG_SHAr, n4), dot(_BG_SHAg, n4), dot(_BG_SHAb, n4));
                float4 vB = n.xyzz * n.yzzx;
                float3 x2 = float3(dot(_BG_SHBr, vB), dot(_BG_SHBg, vB), dot(_BG_SHBb, vB));
                float vC = n.x * n.x - n.y * n.y;
                return max(x1 + x2 + _BG_SHC.rgb * vC, 0.0);
            }

            float Height(float2 xz)
            {
                float2 uv = (xz - _BG_AmbShelterArea.xy) * _BG_AmbShelterArea.z + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) return -10000.0;
                return tex2Dlod(_BG_AmbShelter, float4(uv, 0, 0)).r;
            }

            // Fração do céu que o ponto enxerga, ponderada pela normal (cos² do ângulo do horizonte em cada direção).
            float SkyVisibility(float3 p, float3 n)
            {
                if (_BG_AmbShelterArea.w < 0.5) return 1.0;
                float3 o = p + n * 0.25;
                float sum = 0.0, wsum = 0.0;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float a = k * 0.785398;
                    float2 d = float2(cos(a), sin(a));
                    float w = saturate(0.35 + n.y * 0.65 + dot(d, n.xz) * 0.65);
                    float t = 0.0;
                    t = max(t, (Height(o.xz + d * 0.5) - o.y) / 0.5);
                    t = max(t, (Height(o.xz + d * 1.5) - o.y) / 1.5);
                    t = max(t, (Height(o.xz + d * 4.0) - o.y) / 4.0);
                    t = max(t, (Height(o.xz + d * 10.0) - o.y) / 10.0);
                    sum += w / (1.0 + t * t);
                    wsum += w;
                }
                // Teto bem em cima (marquise, viaduto): o céu do alto some mesmo com os lados abertos.
                float over = saturate((Height(o.xz) - o.y - 0.2) / 0.5);
                return sum / max(wsum, 1e-3) * lerp(1.0, 0.45, over * saturate(n.y));
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.pos.xy * (_ScreenParams.zw - 1.0);
                float4 L = tex2D(_BG_AmbL, uv);
                float d = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                #if UNITY_REVERSED_Z
                    if (d <= 0.0) return L;
                    float nz = d;
                #else
                    if (d >= 1.0) return L;
                    float nz = d * 2.0 - 1.0;
                #endif
                #if UNITY_UV_STARTS_AT_TOP
                    float ny = 1.0 - 2.0 * uv.y;
                #else
                    float ny = 2.0 * uv.y - 1.0;
                #endif
                float4 w = mul(_BG_AmbInvVP, float4(uv.x * 2.0 - 1.0, ny, nz, 1.0));
                float3 p = w.xyz / w.w;
                if (_BG_AmbHasBus > 0.5)
                {
                    float3 g = mul(_BG_AmbBus, float4(p, 1.0)).xyz;
                    if (all(g > 0.0) && all(g < 1.0)) return L;
                }
                float4 g0 = tex2D(_CameraGBufferTexture0, uv);
                float3 n = normalize(tex2D(_CameraGBufferTexture2, uv).xyz * 2.0 - 1.0);
                float3 diff = g0.rgb;
                float occ = g0.a;

                float3 unity = SH(n) * diff * occ;
                float3 rest = max(L.rgb - unity, 0.0);

                float vis = SkyVisibility(p, n);
                float3 sky = SH(n) * _BG_AmbSkyScale;
                float lum = dot(sky, float3(0.2126, 0.7152, 0.0722));
                sky = lerp(lum.xxx, sky, _BG_AmbSkySat) + _BG_AmbCityGlow * saturate(n.y * 0.5 + 0.5);
                float3 bounce = _BG_AmbBounce * ((1.0 - n.y) * 0.5 + (1.0 - vis) * _BG_AmbWall * (n.y * 0.5 + 0.5));
                float3 amb = (sky * vis + bounce) * _BG_AmbStrength;

                if (_BG_AmbDebug > 7.5) return float4(0.5, 0.5, 0.5, 1);
                if (_BG_AmbDebug > 6.5) return float4(saturate(dot(L.rgb, 0.333) / max(dot(unity, 0.333), 1e-5) * 0.5).xxx, 1);
                if (_BG_AmbDebug > 5.5) return float4(L.rgb, 1);
                if (_BG_AmbDebug > 4.5) return float4(diff, 1);
                if (_BG_AmbDebug > 3.5) return float4(occ.xxx, 1);
                if (_BG_AmbDebug > 2.5) return float4(amb, 1);
                if (_BG_AmbDebug > 1.5) return float4(vis.xxx, 1);
                if (_BG_AmbDebug > 0.5) return float4(rest, 1);
                return float4(rest + amb * diff * occ, L.a);
            }
            ENDCG
        }
    }
}
