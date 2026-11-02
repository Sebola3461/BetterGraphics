// Ótica de uma gota d'água sobre o vidro, usada pelo RainDrops (gotas simuladas do para-brisa) e pelo
// RainGlass (gotas procedurais das janelas). A gota é uma calota esférica com o ângulo de contato do vidro.
#ifndef BG_RAIN_LENS
#define BG_RAIN_LENS

#include "UnityCG.cginc"

sampler2D _BG_Fundo;
float4 _BG_Fundo_TexelSize;
UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
float _BG_Ior;              // da água (1,33)
float _BG_RainRoughness;    // da superfície da água: borra o reflexo do céu
float _BG_ContactAngle;     // radianos, até 89°
float _BG_RainCamInside;    // 1 = olho dentro do ônibus: vê a gota por trás do vidro

// Calota de base de raio 1 no ponto q (|q| < 1): devolve a inclinação (dh/dqx, dh/dqy) e a altura, em raios.
float3 Cap(float2 q, float angle)
{
    float s = max(sin(angle), 0.05);
    float R = 1.0 / s;
    float h0 = tan(angle * 0.5);
    float root = sqrt(max(R * R - dot(q, q), 1e-4));
    return float3(-q / root, max(root - (R - h0), 0.0));
}

// Distância da câmera ao que está desenhado na tela em "screenUv" (m).
float SceneDepth(float2 screenUv) { return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, screenUv)); }

// Cor vista através da gota. O raio do olho atravessa a água e sai (ou entra) pela superfície curva com a lei de
// Snell. O desvio vira deslocamento na tela medindo onde cairia um ponto distante: desvio pequeno mostra o fundo
// quase no lugar, desvio grande mostra um pedaço longe dele (a gota inverte a imagem, com o céu embaixo). Onde a
// luz não consegue sair (reflexão interna total) fica a borda escura.
// A tela capturada também tem o que está do lado de cá do vidro (coluna, painel), que a gota nunca mostra: se o
// ponto desviado cai em algo mais perto que a gota, o desvio encurta até achar o lado de fora.
// O raio passa por duas interfaces: a base plana (vidro/água; o vidro de faces paralelas não muda a direção final)
// e a calota (água/ar). De dentro do ônibus, entra pela base e sai pela calota; de fora, o contrário.
float3 LensColor(float3 worldPos, float3 nOut, float3 nGlass, float4 grabPos, float4 screenPos, float blurPx)
{
    float3 V = normalize(worldPos - _WorldSpaceCameraPos);
    float ior = max(_BG_Ior, 1.0);
    float3 nf = dot(V, nOut) < 0.0 ? nOut : -nOut;
    float3 gf = dot(V, nGlass) < 0.0 ? nGlass : -nGlass;
    float3 T;
    if (_BG_RainCamInside > 0.5)
    {
        float3 inWater = refract(V, gf, 1.0 / ior);
        T = refract(inWater, dot(inWater, nf) < 0.0 ? nf : -nf, ior);
    }
    else
    {
        float3 inWater = refract(V, nf, 1.0 / ior);
        T = refract(inWater, dot(inWater, gf) < 0.0 ? gf : -gf, ior);
    }
    float tir = dot(T, T) < 1e-6 ? 1.0 : 0.0;
    if (tir > 0.5) T = reflect(V, nf);

    const float D = 25.0;
    float4 k0 = mul(UNITY_MATRIX_VP, float4(worldPos + V * D, 1.0));
    float4 k1 = mul(UNITY_MATRIX_VP, float4(worldPos + T * D, 1.0));
    if (k1.w < 1e-3) k1 = k0;
    float4 g0 = ComputeGrabScreenPos(k0), g1 = ComputeGrabScreenPos(k1);
    float4 s0 = ComputeScreenPos(k0), s1 = ComputeScreenPos(k1);
    float2 grabShift = g1.xy / g1.w - g0.xy / g0.w;
    float2 screenShift = s1.xy / s1.w - s0.xy / s0.w;
    float2 screenBase = screenPos.xy / screenPos.w;
    float dropDepth = screenPos.w;
    float f = 0.0;
    [unroll] for (int k = 0; k < 4; k++)
    {
        float tryF = k == 0 ? 1.0 : k == 1 ? 0.5 : k == 2 ? 0.25 : 0.1;
        if (f == 0.0 && SceneDepth(saturate(screenBase + screenShift * tryF)) > dropDepth + 0.25) f = tryF;
    }
    float2 uv = clamp(grabPos.xy / grabPos.w + grabShift * f, 0.002, 0.998);

    // A gota fica fora de foco para quem olha a rua: um pouco de desfoque.
    float2 o = _BG_Fundo_TexelSize.xy * blurPx;
    float3 seen = (tex2D(_BG_Fundo, uv + float2(o.x, o.y)).rgb + tex2D(_BG_Fundo, uv - float2(o.x, o.y)).rgb +
                   tex2D(_BG_Fundo, uv + float2(o.x, -o.y)).rgb + tex2D(_BG_Fundo, uv + float2(-o.x, o.y)).rgb) * 0.25;
    // Na borda a luz fica presa dentro da gota; ela escurece mas ainda devolve um pouco do que está em volta.
    float3 color = seen * lerp(0.94, 0.3, tir);

    // Reflexo do céu na superfície (forte para quem olha de fora, fraco de dentro).
    float3 r = reflect(V, nf);
    float3 sky = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, r, _BG_RainRoughness * 6.0), unity_SpecCube0_HDR);
    float fres = 0.02 + 0.98 * pow(1.0 - saturate(abs(dot(V, nf))), 5.0);
    color += sky * fres * (_BG_RainCamInside > 0.5 ? 0.35 : 1.0);
    return color;
}

#endif
