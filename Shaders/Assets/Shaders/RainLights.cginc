// Luz que uma gota de chuva (ou respingo) recebe: céu, sol e as luzes próximas (postes, faróis), com espalhamento
// para a frente. É por isso que à noite a chuva aparece nos cones de luz e brilha olhando contra o poste.
#ifndef BG_RAIN_LIGHTS
#define BG_RAIN_LIGHTS

#include "UnityCG.cginc"
#include "Lighting.cginc"

#define BG_RAIN_LIGHT_COUNT 8
float4 _BG_RF_LightPos[BG_RAIN_LIGHT_COUNT];   // posição e alcance (alcance 0 = vazia)
float4 _BG_RF_LightCol[BG_RAIN_LIGHT_COUNT];   // cor x intensidade e cosseno do cone externo (-2 = pontual)
float4 _BG_RF_LightDir[BG_RAIN_LIGHT_COUNT];   // direção do spot e cosseno do cone interno

// Henyey-Greenstein: g > 0 espalha para a frente.
float RainPhase(float mu, float g) { return (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * mu, 1e-3), 1.5) / 12.566; }

// view: da câmera para a gota (normalizado).
float3 RainLight(float3 w, float3 view)
{
    float3 sky = ShadeSH9(float4(normalize(float3(0.0, 1.0, 0.0) + view * 0.4), 1.0));
    float3 col = sky * 1.5 + _LightColor0.rgb * RainPhase(dot(view, normalize(_WorldSpaceLightPos0.xyz)), 0.7) * 0.6;
    [unroll] for (int i = 0; i < BG_RAIN_LIGHT_COUNT; i++)
    {
        float4 lp = _BG_RF_LightPos[i];
        if (lp.w <= 0.0) continue;
        float3 toLight = lp.xyz - w;
        float d = length(toLight);
        float3 ln = toLight / max(d, 1e-3);
        float att = saturate(1.0 - d / lp.w);
        att *= att;
        float4 lc = _BG_RF_LightCol[i];
        if (lc.w > -1.5)
        {
            float4 sd = _BG_RF_LightDir[i];
            att *= smoothstep(lc.w, sd.w, dot(-ln, sd.xyz));
        }
        col += lc.rgb * att * (0.15 + 2.5 * RainPhase(dot(view, ln), 0.6));
    }
    return col;
}

#endif
