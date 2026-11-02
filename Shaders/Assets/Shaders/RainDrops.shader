// Gotas simuladas do para-brisa (RainDropSim), desenhadas na câmera principal: um quadradinho por gota sobre a
// face de fora do vidro. A gota i está no texel (i % 256, i / 256) de _BG_Drops: x, y (m na zona), raio (m) e a
// direção em graus com o alongamento na fração. O mapa do vidro dá o z e a normal em cada ponto da zona.
// Parada, a gota é uma calota; andando, um cometa (cabeça um pouco comprida e cauda afinando).
Shader "Hidden/BetterGraphics/RainDrops"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        GrabPass { "_BG_Fundo" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite Off ZTest LEqual Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "RainLens.cginc"

            sampler2D _BG_Drops;
            float4 _BG_Drops_TexelSize;
            sampler2D _BG_RainGlassMap;   // z, nx, ny, cobertura
            float4 _BG_RainZone;          // canto (x, y) e tamanho da zona, no espaço do ônibus
            float _BG_RainDebug;
            float _BG_DropScale;          // tamanho desenhado / tamanho físico

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 grab : TEXCOORD0;
                float4 scr : TEXCOORD6;
                float4 q : TEXCOORD1;       // x, y no quadrado (em raios), alongamento, raio (m)
                float3 world : TEXCOORD2;
                float3 n : TEXCOORD3;       // normal do vidro (mundo)
                float3 side : TEXCOORD4;    // eixo de lado da gota (mundo)
                float3 fwd : TEXCOORD5;     // eixo para onde ela anda (mundo)
            };

            // Vetor do plano da zona levado ao plano do vidro (z sai da inclinação local).
            float3 OnGlass(float2 v, float3 n) { return float3(v, -(n.x * v.x + n.y * v.y) / max(n.z, 0.2)); }

            v2f vert(float4 vertex : POSITION, float2 corner : TEXCOORD0, float2 index : TEXCOORD1)
            {
                v2f o = (v2f)0;
                float4 d = tex2Dlod(_BG_Drops, float4((index + 0.5) * _BG_Drops_TexelSize.xy, 0, 0));
                float r = d.z * max(_BG_DropScale, 1.0);
                float2 zuv = d.xy / _BG_RainZone.zw;
                float4 g = tex2Dlod(_BG_RainGlassMap, float4(zuv, 0, 0));
                if (r <= 0.0 || g.w < 0.25) { o.pos = float4(2, 2, 2, 1); return o; }

                float a = floor(d.w) * (UNITY_PI / 180.0);
                float T = frac(d.w) * 1.4;   // alongamento (0 a 0,7) vezes 2: cauda de até 2,4 raios
                float2 dir = float2(cos(a), sin(a));
                float2 side = float2(-dir.y, dir.x);
                // 15% de folga em volta para a borda suavizada caber.
                float x = (corner.x * 2.0 - 1.0) * 1.15;
                float y = lerp(-(1.0 + 2.0 * T), 1.0, corner.y) * 1.15;
                float2 off = (side * x + dir * y) * r;

                float3 n = float3(g.y, g.z, sqrt(saturate(1.0 - g.y * g.y - g.z * g.z)));
                float3 p = float3(_BG_RainZone.xy + d.xy, g.x) + OnGlass(off, n) + n * 0.003;
                o.pos = UnityObjectToClipPos(float4(p, 1.0));
                o.grab = ComputeGrabScreenPos(o.pos);
                o.scr = ComputeScreenPos(o.pos);
                o.world = mul(unity_ObjectToWorld, float4(p, 1.0)).xyz;
                o.n = normalize(mul((float3x3)unity_ObjectToWorld, n));
                o.side = normalize(mul((float3x3)unity_ObjectToWorld, OnGlass(side, n)));
                o.fwd = normalize(mul((float3x3)unity_ObjectToWorld, OnGlass(dir, n)));
                o.q = float4(x, y, T, r);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float x = i.q.x, y = i.q.y, T = i.q.z;
                float head = 1.0 + 0.15 * saturate(T);
                float2 c = float2(x, y / head);
                float3 cap = dot(c, c) < 1.0 ? Cap(c, _BG_ContactAngle) : float3(0, 0, 0);
                float2 slope = float2(cap.x, cap.y / head);
                float h = cap.z;
                float edge = 1.0 - length(c);
                // Cauda: atrás da cabeça, afinando até a ponta e mais baixa que ela.
                if (T > 0.01 && y < 0.0)
                {
                    float t = saturate(-y / (1.0 + 2.0 * T));
                    float w = lerp(0.55, 0.04, t);
                    float qx = x / w;
                    float k = 0.45 * (1.0 - t);
                    if (abs(qx) < 1.0)
                    {
                        float3 tail = Cap(float2(qx, 0.0), _BG_ContactAngle);
                        if (tail.z * k > h) { h = tail.z * k; slope = float2(tail.x * k / w, 0.0); }
                    }
                    edge = max(edge, (1.0 - abs(qx)) * w * saturate((1.0 - t) * 4.0));
                }
                float alpha = saturate(edge / max(fwidth(edge), 1e-4));
                // Menor que ~1 pixel, a gota vira cintilação: some aos poucos.
                alpha *= saturate(i.q.w / max(length(fwidth(i.world)), 1e-6) - 0.5);
                if (alpha <= 0.0) discard;
                if (_BG_RainDebug > 0.5) return float4(1, 0.1, 0.1, alpha);

                float3 nOut = normalize(i.n - (slope.x * i.side + slope.y * i.fwd));
                float3 color = LensColor(i.world, nOut, i.n, i.grab, i.scr, 0.8);
                return float4(color, alpha);
            }
            ENDCG
        }
    }
}
