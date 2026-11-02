// Vidro molhado dos ônibus (troca o "OneClick Glass/Steamy Refract" que o jogo liga na chuva). O fundo atrás do
// vidro é capturado e visto através da água:
//  - película: a água fina entre as gotas borra e ondula a vista. No para-brisa vem da grade simulada (o arco
//    da palheta fica nítido); nas outras janelas é proporcional à chuva;
//  - janelas: gotas procedurais em duas camadas, com a mesma lente das gotas simuladas do para-brisa (estas são
//    desenhadas à parte, pelo RainDrops).
Shader "BetterGraphics/RainGlass"
{
    Properties
    {
        _Color ("Tom", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        GrabPass { "_BG_Fundo" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite Off Cull Off
            Blend One Zero

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "RainLens.cginc"

            float4 _Color;
            float4x4 _BG_WorldToBus;
            sampler2D _BG_RainFilm;
            float4 _BG_RainZone;
            float _BG_RainMiddleZ;      // meio do ônibus em z: separa para-brisa de traseira
            float _BG_RainHasZone;
            float _BG_Rain;
            float _BG_RainDebug;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 grab : TEXCOORD0;
                float4 scr : TEXCOORD5;
                float3 local : TEXCOORD1;
                float3 nLocal : TEXCOORD2;
                float3 world : TEXCOORD3;
                float3 nWorld : TEXCOORD4;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.grab = ComputeGrabScreenPos(o.pos);
                o.scr = ComputeScreenPos(o.pos);
                float3 w = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.world = w;
                o.nWorld = UnityObjectToWorldNormal(v.normal);
                o.local = mul(_BG_WorldToBus, float4(w, 1.0)).xyz;
                o.nLocal = mul((float3x3)_BG_WorldToBus, o.nWorld);
                return o;
            }

            float Hash(float2 p) { p = frac(p * float2(233.34, 851.73)); p += dot(p, p + 23.45); return frac(p.x * p.y); }

            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), u.x), u.y);
            }

            // Vista através da película: f = 0 seco ou limpo pela palheta, 1 encharcado. O borrão cresce com a
            // distância entre a película e o que está atrás (a palheta, encostada no vidro, fica nítida).
            float3 FilmView(float2 gu, float f, float2 p, float4 scr)
            {
                float3 base = tex2D(_BG_Fundo, gu).rgb;
                f *= saturate((SceneDepth(scr.xy / scr.w) - scr.w) / 1.5);
                if (f < 0.01) return base;
                // A película escorre devagar: a ondulação desce com o tempo.
                float2 wob = float2(Noise(p * 60.0 + float2(0, _Time.y * 0.3)), Noise(p * 60.0 + 17.3 + float2(0, _Time.y * 0.3))) - 0.5;
                float2 b = gu + wob * f * 3.0 * _BG_Fundo_TexelSize.xy;
                float2 t = _BG_Fundo_TexelSize.xy * (1.0 + 3.0 * f);
                float3 s = 0;
                s += tex2D(_BG_Fundo, b + float2( 1.0,  0.0) * t).rgb; s += tex2D(_BG_Fundo, b + float2(-1.0,  0.0) * t).rgb;
                s += tex2D(_BG_Fundo, b + float2( 0.0,  1.0) * t).rgb; s += tex2D(_BG_Fundo, b + float2( 0.0, -1.0) * t).rgb;
                s += tex2D(_BG_Fundo, b + float2( 0.7,  0.7) * t).rgb; s += tex2D(_BG_Fundo, b + float2(-0.7,  0.7) * t).rgb;
                s += tex2D(_BG_Fundo, b + float2( 0.7, -0.7) * t).rgb; s += tex2D(_BG_Fundo, b + float2(-0.7, -0.7) * t).rgb;
                float3 c = lerp(base, s / 8.0, saturate(f * 1.3));
                // A água espalha um pouco de luz: o contraste cai.
                return lerp(c, dot(c, 0.3333) * 1.02 + 0.01, f * 0.12);
            }

            // Uma camada de gotas paradas numa grade de "cell" metros, uma por célula sorteada. Devolve a inclinação
            // da água no plano (x, y) e a cobertura suavizada da borda.
            float3 DropLayer(float2 p, float cell, float density, float seed, float mpp)
            {
                float2 q = p / cell + seed;
                float2 id = floor(q), f = frac(q);
                float h1 = Hash(id), h2 = Hash(id + 7.3), h3 = Hash(id + 2.1), h4 = Hash(id + 5.9);
                if (h3 > density) return 0;
                float rr = lerp(0.12, 0.36, h4 * h4);
                float2 dq = (f - (0.5 + (float2(h1, h2) - 0.5) * (1.0 - 2.0 * rr))) / rr;
                float d2 = dot(dq, dq);
                if (d2 >= 1.0) return 0;
                float3 cap = Cap(dq, _BG_ContactAngle);
                float edge = 1.0 - sqrt(d2);
                float alpha = saturate(edge / max(fwidth(edge), 1e-4));
                // Menor que ~1,5 pixel, a gota vira cintilação: some aos poucos.
                alpha *= saturate(rr * cell / max(mpp, 1e-6) - 0.75);
                return float3(cap.xy, alpha);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 gu = i.grab.xy / i.grab.w;
                float3 n = normalize(i.nLocal);
                float2 zuv = (i.local.xy - _BG_RainZone.xy) / _BG_RainZone.zw;
                bool front = _BG_RainHasZone > 0.5 && abs(n.z) > 0.5 && i.local.z > _BG_RainMiddleZ && all(zuv > 0.0) && all(zuv < 1.0);
                bool sideGlass = abs(n.x) > abs(n.z);
                float2 plane = sideGlass ? i.local.zy : i.local.xy;
                float film = front ? tex2D(_BG_RainFilm, zuv).r : saturate(_BG_Rain) * 0.55;
                if (_BG_RainDebug > 0.5) return float4(front ? float3(film, 0.2, 0.2) : float3(0.2, film, 0.2), 1);

                float3 color = FilmView(gu, film, plane, i.scr);
                if (!front && _BG_Rain > 0.01)
                {
                    float mpp = max(length(ddx(i.local)), length(ddy(i.local)));
                    float3 nw = normalize(i.nWorld);
                    float3 uLocal = sideGlass ? float3(0, 0, 1) : float3(1, 0, 0);
                    float3 uw = normalize(mul(transpose((float3x3)_BG_WorldToBus), uLocal));
                    uw = normalize(uw - nw * dot(uw, nw));
                    float3 vw = normalize(cross(nw, uw));
                    vw *= sign(vw.y + 1e-4);
                    float dens = saturate(_BG_Rain) * 0.8;
                    float3 d = DropLayer(plane, 0.007, dens, 0.0, mpp);
                    float3 d2 = DropLayer(plane, 0.0115, dens * 0.6, 3.7, mpp);
                    if (d2.z > d.z) d = d2;
                    if (d.z > 0.0)
                    {
                        float3 nOut = normalize(nw - (d.x * uw + d.y * vw));
                        color = lerp(color, LensColor(i.world, nOut, nw, i.grab, i.scr, 0.6), d.z);
                    }
                }
                return float4(color * _Color.rgb, 1.0);
            }
            ENDCG
        }
    }
}
