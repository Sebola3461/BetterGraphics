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
// No salão do ônibus do jogador a luz vem das janelas: mapa 3D de visibilidade do Interior (céu que entra por cada
// direção) + rebote de paredes e teto. A lataria por fora (voltada para fora da casca) fica no ramo de fora.
// Este passe é o único que escreve a luz ambiente: o Interior só fornece o mapa.
// Antes de tudo, o albedo é validado: refletância máxima de 0,85 no branco e menor quanto mais saturada a cor
// (tinta real não chega no azul ou verde puro de textura). O G-buffer e a luz já somada são corrigidos juntos.
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
            sampler2D _BG_AmbG0;               // cópia do G-buffer 0 (albedo, oclusão)
            sampler2D _CameraGBufferTexture2;
            float _BG_AlbedoLimit;
            sampler2D _BG_AmbL;                 // cópia de RT3
            sampler2D _BG_AmbShelter;           // mapa de abrigo (altura do topo)
            float4 _BG_AmbShelterArea;          // x, z do centro, 1/lado, 1 se existe
            float4x4 _BG_AmbInvVP;
            sampler3D _BG_WinVis;               // visibilidade das janelas (rgb = direção, a = constante)
            float4x4 _BG_WinGrade;              // mundo -> salão (0 a 1)
            float4x4 _BG_WinRot;                // rotação mundo -> ônibus
            float3 _BG_WinSize;                 // salão (m)
            float3 _BG_WinRebate;               // rebote de paredes e teto
            float _BG_WinScale, _BG_WinForce, _BG_WinReady;
            float4 _BG_SHAr, _BG_SHAg, _BG_SHAb, _BG_SHBr, _BG_SHBg, _BG_SHBb, _BG_SHC;   // sonda da Unity, como a usada no G-buffer
            float _BG_AmbSkyScale;              // céu próprio = sonda x isto (tira a escala de câmera e cabine)
            float _BG_AmbSkySat;                // saturação do céu na luz ambiente (1 = a da sonda)
            float3 _BG_AmbBounce;               // radiância do chão iluminado (sol x albedo do chão)
            float _BG_AmbWall;                  // quanto do rebote o que tapa o céu devolve
            float3 _BG_AmbCityGlow;             // brilho do céu da cidade (noite)
            float _BG_AmbStrength;
            float _BG_AmbDebug;

            struct v2f { float4 pos : SV_POSITION; };
            struct output { float4 g0 : SV_Target0; float4 l : SV_Target1; };

            // Teto de refletância pela saturação: 0,85 no branco, 0,55 na cor pura.
            float3 ValidAlbedo(float3 a)
            {
                float mx = max(a.r, max(a.g, a.b)), mn = min(a.r, min(a.g, a.b));
                float sat = 1.0 - mn / max(mx, 1e-4);
                float cap = lerp(0.85, 0.55, sat * sat);
                return mx > cap ? a * (cap / mx) : a;
            }
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

            output frag(v2f i)
            {
                float2 uv = i.pos.xy * (_ScreenParams.zw - 1.0);
                output o;
                float4 L = tex2D(_BG_AmbL, uv);
                float4 g0 = tex2D(_BG_AmbG0, uv);
                o.g0 = g0; o.l = L;
                float d = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                #if UNITY_REVERSED_Z
                    if (d <= 0.0) return o;
                    float nz = d;
                #else
                    if (d >= 1.0) return o;
                    float nz = d * 2.0 - 1.0;
                #endif
                // Albedo validado; a luz ambiente que o G-buffer já somou acompanha (é proporcional ao albedo).
                if (_BG_AlbedoLimit > 0.5)
                {
                    float3 a = ValidAlbedo(g0.rgb);
                    L.rgb *= (a + 1e-4) / (g0.rgb + 1e-4);
                    g0.rgb = a;
                    o.g0 = g0; o.l = L;
                }
                #if UNITY_UV_STARTS_AT_TOP
                    float ny = 1.0 - 2.0 * uv.y;
                #else
                    float ny = 2.0 * uv.y - 1.0;
                #endif
                float4 w = mul(_BG_AmbInvVP, float4(uv.x * 2.0 - 1.0, ny, nz, 1.0));
                float3 p = w.xyz / w.w;

                float3 n = normalize(tex2D(_CameraGBufferTexture2, uv).xyz * 2.0 - 1.0);
                float3 diff = g0.rgb;
                float occ = g0.a;

                float3 unity = SH(n) * diff * occ;
                // A parcela da Unity sai na mesma proporção nos três canais: se outro passe já mexeu em RT3 (luz das
                // janelas), tirar canal a canal zerava um antes dos outros e a cor girava.
                float3 ratio = L.rgb / max(unity, 1e-5);
                float k = saturate(min(ratio.r, min(ratio.g, ratio.b)));
                float3 rest = max(L.rgb - unity * k, 0.0);

                float vis = SkyVisibility(p, n);
                float3 sky = SH(n) * _BG_AmbSkyScale;
                float lum = dot(sky, float3(0.2126, 0.7152, 0.0722));
                sky = lerp(lum.xxx, sky, _BG_AmbSkySat) + _BG_AmbCityGlow * saturate(n.y * 0.5 + 0.5);
                float3 bounce = _BG_AmbBounce * ((1.0 - n.y) * 0.5 + (1.0 - vis) * _BG_AmbWall * (n.y * 0.5 + 0.5));
                float3 amb = (sky * vis + bounce) * _BG_AmbStrength;

                // Salão: troca pela luz das janelas, esmaecendo nos últimos 10 cm da caixa.
                if (_BG_WinReady > 0.5)
                {
                    float3 g = mul(_BG_WinGrade, float4(p, 1.0)).xyz;
                    if (all(g >= 0.0) && all(g <= 1.0))
                    {
                        float3 nl = normalize(mul((float3x3)_BG_WinRot, n));
                        float3 c = g - 0.5;
                        bool outward = (abs(c.x) > 0.35 && nl.x * sign(c.x) > 0.5) || (c.y > 0.35 && nl.y > 0.5)
                                    || (abs(c.z) > 0.45 && nl.z * sign(c.z) > 0.5);
                        if (!outward)
                        {
                            float3 edge = min(g, 1.0 - g) * _BG_WinSize / 0.1;
                            float w = saturate(min(edge.x, min(edge.y, edge.z))) * _BG_WinForce;
                            float4 v = tex3D(_BG_WinVis, g);
                            float vn = saturate(v.w + dot(v.xyz, nl));
                            float3 inside = vn * SH(n) * _BG_WinScale + (1.0 - vn) * _BG_WinRebate;
                            amb = lerp(amb, inside, w);
                        }
                    }
                }

                if (_BG_AmbDebug > 7.5) { o.l = float4(0.5, 0.5, 0.5, 1); return o; }
                if (_BG_AmbDebug > 6.5) { o.l = float4(saturate(dot(L.rgb, 0.333) / max(dot(unity, 0.333), 1e-5) * 0.5).xxx, 1); return o; }
                if (_BG_AmbDebug > 5.5) return o;
                if (_BG_AmbDebug > 4.5) { o.l = float4(diff, 1); return o; }
                if (_BG_AmbDebug > 3.5) { o.l = float4(occ.xxx, 1); return o; }
                if (_BG_AmbDebug > 2.5) { o.l = float4(amb, 1); return o; }
                if (_BG_AmbDebug > 1.5) { o.l = float4(vis.xxx, 1); return o; }
                if (_BG_AmbDebug > 0.5) { o.l = float4(rest, 1); return o; }
                o.l = float4(rest + amb * diff * occ, L.a);
                return o;
            }
            ENDCG
        }
    }
}
