// Umidade da chuva em toda a cena (passe deferred, antes dos reflexos). Lê cópias do G-buffer e
// reescreve albedo, especular/smoothness, normal e a luz ambiente já somada (RT3):
//  - só onde a chuva chega: o mapa de altura vista de cima diz o que está coberto (telhado, túnel,
//    abrigo, interior do ônibus); paredes consultam o ponto um pouco à frente delas;
//  - água nos poros escurece o que é poroso (metal não) e alisa a superfície;
//  - no plano, poças: espelho d'água com normal para cima e ondulações das gotas;
//  - na vertical, molha menos e em faixas (a água escorre).
// O reflexo em si vem depois, da sonda e do SSR, que leem a smoothness e a normal novas.
Shader "Hidden/BetterGraphics/Molhado"
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
            sampler2D _BG_G0, _BG_G1, _BG_G2, _BG_L;
            sampler2D _BG_Altura;
            float4x4 _BG_InvVP;
            float4x4 _BG_OnibusGrade;   // mundo -> caixa do salão do ônibus do jogador (0..1)
            float4 _BG_AlturaArea;      // x, z do centro, 1/(lado), 1 se o mapa existe
            float _BG_Umidade;          // 0 seco .. 1 encharcado
            float _BG_Chuva;            // força das gotas caindo agora (ondulações)
            float _BG_TemOnibus;
            float _BG_Debug;

            struct v2f { float4 pos : SV_POSITION; };
            struct saida { float4 g0 : SV_Target0; float4 g1 : SV_Target1; float4 g2 : SV_Target2; float4 l : SV_Target3; };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = float4(vertex.xy, 0.5, 1.0);
                return o;
            }

            float Hash(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }

            float Ruido(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), u.x), u.y);
            }

            float Fbm(float2 p) { return Ruido(p) * 0.55 + Ruido(p * 2.13 + 7.1) * 0.3 + Ruido(p * 4.37 + 3.3) * 0.15; }

            // Ondulações: em cada célula uma gota cai num ponto e numa hora aleatórios; o anel cresce e
            // some. Devolve a inclinação (xz) da superfície da água.
            float2 Ondas(float2 p, float t)
            {
                float2 soma = 0;
                [unroll] for (int camada = 0; camada < 3; camada++)
                {
                    float2 q = p * (1.7 + camada * 0.6) + camada * 17.3;
                    float2 cel = floor(q);
                    float h = Hash(cel), h2 = Hash(cel + 31.7);
                    float fase = frac(t * (0.9 + h2 * 0.5) + h);
                    float2 d = frac(q) - (0.25 + float2(h, h2) * 0.5);
                    float r = length(d);
                    float x = r - fase * 0.45;
                    float onda = sin(clamp(x * 60.0, -6.2832, 6.2832)) * step(abs(x), 0.105);
                    soma += d / max(r, 1e-3) * onda * (1.0 - fase) * (1.0 - fase);
                }
                return soma;
            }

            saida frag(v2f i)
            {
                float2 uv = i.pos.xy * (_ScreenParams.zw - 1.0);
                saida o;
                o.g0 = tex2D(_BG_G0, uv); o.g1 = tex2D(_BG_G1, uv); o.g2 = tex2D(_BG_G2, uv); o.l = tex2D(_BG_L, uv);

                float d = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                #if UNITY_UV_STARTS_AT_TOP
                    float ny = 1.0 - 2.0 * uv.y;
                #else
                    float ny = 2.0 * uv.y - 1.0;
                #endif
                #if UNITY_REVERSED_Z
                    float nz = d;
                    if (d <= 0.0) return o;
                #else
                    float nz = d * 2.0 - 1.0;
                    if (d >= 1.0) return o;
                #endif
                float4 w = mul(_BG_InvVP, float4(uv.x * 2.0 - 1.0, ny, nz, 1.0));
                float3 pos = w.xyz / w.w;
                float3 n = normalize(o.g2.xyz * 2.0 - 1.0);

                // Dentro do ônibus do jogador nunca chove (o mapa de cima atrasa quando ele anda).
                if (_BG_TemOnibus > 0.5)
                {
                    float3 g = mul(_BG_OnibusGrade, float4(pos, 1.0)).xyz;
                    if (all(g > 0.0) && all(g < 1.0)) return o;
                }

                // Exposição à chuva: parede consulta o ponto 35 cm à frente dela (a chuva cai inclinada).
                float exposto = 1.0;
                if (_BG_AlturaArea.w > 0.5)
                {
                    float2 xz = pos.xz + n.xz * 0.35;
                    float2 auv = (xz - _BG_AlturaArea.xy) * _BG_AlturaArea.z + 0.5;
                    if (all(auv > 0.0) && all(auv < 1.0))
                    {
                        float2 px = float2(_BG_AlturaArea.z, _BG_AlturaArea.z) * 0.12;
                        float e = 0;
                        e += saturate((pos.y - tex2D(_BG_Altura, auv).r) / 0.15 + 1.0);
                        e += saturate((pos.y - tex2D(_BG_Altura, auv + float2(px.x, 0)).r) / 0.15 + 1.0);
                        e += saturate((pos.y - tex2D(_BG_Altura, auv - float2(px.x, 0)).r) / 0.15 + 1.0);
                        e += saturate((pos.y - tex2D(_BG_Altura, auv + float2(0, px.y)).r) / 0.15 + 1.0);
                        e += saturate((pos.y - tex2D(_BG_Altura, auv - float2(0, px.y)).r) / 0.15 + 1.0);
                        exposto = e / 5.0;
                    }
                }
                float molhado = _BG_Umidade * exposto;
                if (_BG_Debug > 2.5)
                {
                    float2 a3 = (pos.xz - _BG_AlturaArea.xy) * _BG_AlturaArea.z + 0.5;
                    float dh = tex2D(_BG_Altura, a3).r - pos.y;
                    o.l = float4(saturate(dh / 3.0), saturate(-dh / 3.0), any(a3 < 0.0) || any(a3 > 1.0) ? 1 : 0, 1);
                    return o;
                }
                if (_BG_Debug > 1.5) { o.g0 = float4(1, 0, 0, 1); return o; }
                if (_BG_Debug > 0.5) { o.l = float4(molhado, exposto, 0, 1) * 2.0; return o; }
                if (molhado <= 0.001) return o;

                float3 albedo = o.g0.rgb;
                float3 f0 = o.g1.rgb;
                float liso = o.g1.a;
                // Metal só o cromado de verdade (F0 alto): o carregador de mapas do jogo põe metallic 0,6
                // em quase tudo. A smoothness também não serve de porosidade (o Chao já a subiu na chuva).
                float metal = saturate((max(f0.r, max(f0.g, f0.b)) - 0.5) / 0.3);
                float poroso = (1.0 - metal) * lerp(1.0, 0.6, saturate((liso - 0.85) / 0.1));
                float plano = saturate((n.y - 0.75) / 0.2);
                float vertical = 1.0 - saturate(abs(n.y) / 0.6);
                // Na parede a água escorre em faixas; embaixo de beirais e em faces para baixo quase nada.
                float faixas = lerp(1.0, lerp(0.45, 1.0, Fbm(float2(pos.x + pos.z, pos.y * 0.15) * 3.0)), vertical);
                float m = molhado * faixas * saturate(n.y * 0.5 + 0.75);

                // Poças: no plano e onde não é vegetação (verde saturado), crescem com a umidade.
                float lum = dot(albedo, float3(0.3, 0.59, 0.11));
                float sat = (max(albedo.r, max(albedo.g, albedo.b)) - min(albedo.r, min(albedo.g, albedo.b))) / max(lum, 1e-3);
                float limiar = lerp(0.92, 0.6, _BG_Umidade);
                // Material que já é liso (asfalto "wet" do mapa, tratado pelo Chao com mapa de poças próprio)
                // não ganha poça inventada: o relevo dele é que estica o reflexo dos postes em faixas.
                float jaMolhado = saturate((liso - 0.6) / 0.15);
                float poca = smoothstep(limiar, limiar + 0.08, Fbm(pos.xz * 0.35)) * plano * molhado * saturate(1.6 - sat) * (1.0 - jaMolhado);
                // Poça que o próprio material do chão já marcou: só recebe as ondulações.
                float pocaMapa = smoothstep(0.93, 0.98, liso) * plano * molhado;

                // Albedo: água nos poros escurece (até ~45% num material muito poroso); a poça cobre tudo.
                float3 albedoNovo = albedo * lerp(1.0, lerp(0.88, 0.45, poroso), m);
                albedoNovo *= lerp(1.0, 0.75, poca);
                // Smoothness e F0: filme d'água liso por cima; poça vira espelho.
                // Plano: a película d'água chega a roughness 0,05; na parede a água escorre e fica mais áspera.
                float lisoNovo = lerp(liso, max(liso, lerp(0.82, 0.95, plano)), m);
                lisoNovo = lerp(lisoNovo, 0.97, poca);
                float3 f0Novo = lerp(f0, max(f0, 0.02), max(m, poca));

                // Normal: a água nivela a poça e as gotas a ondulam.
                float3 nNovo = normalize(lerp(n, float3(0, 1, 0), poca * 0.75)); // um resto do relevo: o reflexo dos postes se estica em faixas
                float2 onda = Ondas(pos.xz, _Time.y) * _BG_Chuva * max(poca, pocaMapa) * 0.15;
                nNovo = normalize(nNovo + float3(onda.x, 0, onda.y));

                o.g0.rgb = albedoNovo;
                o.g1 = float4(f0Novo, lisoNovo);
                o.g2.xyz = nNovo * 0.5 + 0.5;
                // A luz ambiente do G-buffer (RT3) foi somada com o albedo antigo.
                o.l.rgb *= (albedoNovo + 0.002) / (albedo + 0.002);
                return o;
            }
            ENDCG
        }
    }
}
