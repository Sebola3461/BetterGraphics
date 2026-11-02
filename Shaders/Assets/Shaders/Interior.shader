// Luz ambiente do interior do ônibus vinda das janelas (passe deferred, antes dos reflexos).
// O G-buffer já trouxe em RT3 a luz ambiente de céu aberto (albedo · oclusão · SH(normal)). Para cada
// pixel dentro do salão, este passe soma a diferença para a luz que de fato chega ali: o céu só pelo
// tanto que o ponto enxerga de vidro naquela direção (mapa 3D de visibilidade, no espaço do ônibus) e,
// no resto, o rebatimento fraco de paredes, teto e piso. Fora da caixa do salão não faz nada.
Shader "Hidden/BetterGraphics/Interior"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            sampler2D _CameraGBufferTexture0;
            sampler2D _CameraGBufferTexture2;
            sampler3D _BG_Vis;              // xyz: parte direcional, w: média (visibilidade com peso do cosseno)
            float4x4 _BG_InvVP;              // clip -> mundo (matriz da GPU para render em textura)
            float4x4 _BG_MundoGrade;         // mundo -> coordenadas da grade (0..1 dentro do salão)
            float4x4 _BG_MundoBus;           // mundo -> ônibus (só rotação usada, para a normal)
            float4 _BG_GradeTam;             // tamanho do salão em metros (para o esmaecer nas bordas)
            float4 _BG_SHAr, _BG_SHAg, _BG_SHAb, _BG_SHBr, _BG_SHBg, _BG_SHBb, _BG_SHC;
            float4 _BG_Rebate;               // irradiância/π que paredes e teto devolvem (rgb)
            float _BG_Forca;
            float _BG_Debug;

            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = float4(vertex.xy, 0.5, 1.0); // triângulo que cobre a tela, já em clip space
                return o;
            }

            float3 Ambiente(float3 n)
            {
                float4 n4 = float4(n, 1.0);
                float3 r;
                r.r = dot(_BG_SHAr, n4); r.g = dot(_BG_SHAg, n4); r.b = dot(_BG_SHAb, n4);
                float4 b = n.xyzz * n.yzzx;
                r.r += dot(_BG_SHBr, b); r.g += dot(_BG_SHBg, b); r.b += dot(_BG_SHBb, b);
                r += _BG_SHC.rgb * (n.x * n.x - n.y * n.y);
                return max(r, 0.0);
            }

            float4 frag(v2f i) : SV_Target
            {
                // Coordenada de textura direto do pixel: bate com o G-buffer em qualquer API.
                float2 uv = i.pos.xy * (_ScreenParams.zw - 1.0);
                float d = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                #if UNITY_UV_STARTS_AT_TOP
                    float ny = 1.0 - 2.0 * uv.y;
                #else
                    float ny = 2.0 * uv.y - 1.0;
                #endif
                #if UNITY_REVERSED_Z
                    float nz = d;
                    if (d <= 0.0) return 0;
                #else
                    float nz = d * 2.0 - 1.0;
                    if (d >= 1.0) return 0;
                #endif
                float4 w = mul(_BG_InvVP, float4(uv.x * 2.0 - 1.0, ny, nz, 1.0));
                w.xyz /= w.w;
                float3 g = mul(_BG_MundoGrade, float4(w.xyz, 1.0)).xyz;
                if (any(g < 0.0) || any(g > 1.0)) return 0;
                // Esmaece nos últimos 10 cm da caixa: sem costura entre dentro e fora.
                float3 borda = min(g, 1.0 - g) * _BG_GradeTam.xyz / 0.1;
                float peso = saturate(min(borda.x, min(borda.y, borda.z))) * _BG_Forca;

                float4 gb0 = tex2D(_CameraGBufferTexture0, uv);
                float3 n = normalize(tex2D(_CameraGBufferTexture2, uv).xyz * 2.0 - 1.0);
                float3 nl = normalize(mul((float3x3)_BG_MundoBus, n));
                // Superfície voltada para fora da casca (lataria, teto por fora): não é interior. A caixa do salão inclui a
                // casca, e a lataria recebia a "luz das janelas" (perdia parte da luz do céu).
                // Por eixo (num articulado o centro fica a 9 m da frente: a direção a partir dele não serve).
                float3 c = g - 0.5;
                bool outward = (abs(c.x) > 0.35 && nl.x * sign(c.x) > 0.5)
                            || (c.y > 0.35 && nl.y > 0.5)
                            || (abs(c.z) > 0.45 && nl.z * sign(c.z) > 0.5);
                if (outward) return 0;
                float4 v = tex3D(_BG_Vis, g);
                float vn = saturate(v.w + dot(v.xyz, nl));
                if (_BG_Debug > 0.5) return float4(vn, vn, vn, 0) * 0.5;

                float3 fora = Ambiente(n);
                float3 novo = vn * fora + (1.0 - vn) * _BG_Rebate.rgb;
                return float4(gb0.rgb * gb0.a * (novo - fora) * peso, 0.0);
            }
            ENDCG
        }
    }
}
