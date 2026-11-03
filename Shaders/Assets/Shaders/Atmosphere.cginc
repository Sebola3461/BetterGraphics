// Atmosfera física (Hillaire 2020, "A Scalable and Production Ready Sky and Atmosphere Rendering Technique"):
// Rayleigh, Mie (aerossol) e ozônio sobre um planeta esférico, em km. Três tabelas:
//  - transmitância do ponto até o topo da atmosfera (r, mu);
//  - espalhamento múltiplo de todas as ordens, como fonte isotrópica por altura e ângulo do sol;
//  - vista do céu da câmera (zênite x azimute relativo ao sol), refeita a cada quadro.
// Tudo com iluminância do sol = 1 no topo; quem desenha multiplica pela intensidade do sol da cena.
#ifndef BG_ATMOSPHERE_INCLUDED
#define BG_ATMOSPHERE_INCLUDED

#define BG_R_GROUND 6360.0
#define BG_R_TOP 6460.0

sampler2D _BG_TransmittanceLut, _BG_MultiScatLut, _BG_SkyViewLut;
float _BG_AtmAerosol;          // densidade do aerossol relativa ao ar limpo (cidade: 2 a 5)
float _BG_AtmMieG;             // anisotropia do Mie (0,8: brilho forte em volta do sol)
float _BG_AtmGroundAlbedo;     // albedo médio do chão visto de cima (rebate para o céu)
float _BG_AtmCameraHeight;     // km acima do chão
float3 _BG_AtmSunDir;          // direção do sol (vale com ele abaixo do horizonte)

static const float3 BG_RayleighScattering = float3(5.802, 13.558, 33.1) * 1e-3;
static const float BG_RayleighHeight = 8.0;
static const float BG_MieScattering = 3.996e-3;
static const float BG_MieAbsorption = 0.444e-3;
static const float BG_MieHeight = 1.2;
static const float3 BG_OzoneAbsorption = float3(0.650, 1.881, 0.085) * 1e-3;

struct BGMedium
{
    float3 scatR;
    float scatM;
    float3 ext;
};

BGMedium BGSampleMedium(float h)
{
    BGMedium m;
    float dR = exp(-h / BG_RayleighHeight);
    float dM = exp(-h / BG_MieHeight) * _BG_AtmAerosol;
    float dO = max(0.0, 1.0 - abs(h - 25.0) / 15.0);   // camada de ozônio em tenda, centro a 25 km
    m.scatR = BG_RayleighScattering * dR;
    m.scatM = BG_MieScattering * dM;
    m.ext = m.scatR + (BG_MieScattering + BG_MieAbsorption) * dM + BG_OzoneAbsorption * dO;
    return m;
}

// Distância até a esfera de raio dado (centro na origem), a primeira à frente; -1 sem interseção.
float BGRaySphere(float3 ro, float3 rd, float radius)
{
    float b = dot(ro, rd);
    float c = dot(ro, ro) - radius * radius;
    float disc = b * b - c;
    if (disc < 0.0) return -1.0;
    float s = sqrt(disc);
    float t0 = -b - s, t1 = -b + s;
    if (t0 > 0.0) return t0;
    if (t1 > 0.0) return t1;
    return -1.0;
}

// Parametrização de Bruneton para a transmitância: x = distância ao topo normalizada, y = altura.
float2 BGTransmittanceUv(float r, float mu)
{
    float H = sqrt(BG_R_TOP * BG_R_TOP - BG_R_GROUND * BG_R_GROUND);
    float rho = sqrt(max(0.0, r * r - BG_R_GROUND * BG_R_GROUND));
    float disc = r * r * (mu * mu - 1.0) + BG_R_TOP * BG_R_TOP;
    float d = max(0.0, -r * mu + sqrt(max(0.0, disc)));
    float dMin = BG_R_TOP - r, dMax = rho + H;
    return float2(saturate((d - dMin) / (dMax - dMin)), saturate(rho / H));
}

void BGUvToTransmittance(float2 uv, out float r, out float mu)
{
    float H = sqrt(BG_R_TOP * BG_R_TOP - BG_R_GROUND * BG_R_GROUND);
    float rho = H * uv.y;
    r = sqrt(rho * rho + BG_R_GROUND * BG_R_GROUND);
    float dMin = BG_R_TOP - r, dMax = rho + H;
    float d = dMin + uv.x * (dMax - dMin);
    mu = d == 0.0 ? 1.0 : (H * H - rho * rho - d * d) / (2.0 * r * d);
    mu = clamp(mu, -1.0, 1.0);
}

float3 BGTransmittance(float r, float mu)
{
    return tex2Dlod(_BG_TransmittanceLut, float4(BGTransmittanceUv(r, mu), 0.0, 0.0)).rgb;
}

float3 BGMultiScat(float r, float sunMu)
{
    float2 uv = float2(sunMu * 0.5 + 0.5, saturate((r - BG_R_GROUND) / (BG_R_TOP - BG_R_GROUND)));
    return tex2Dlod(_BG_MultiScatLut, float4(uv, 0.0, 0.0)).rgb;
}

float BGRayleighPhase(float c) { return 3.0 / (16.0 * UNITY_PI) * (1.0 + c * c); }

float BGMiePhase(float c, float g)
{
    // Cornette-Shanks: Henyey-Greenstein com o termo (1 + c²), melhor para gotas pequenas.
    float g2 = g * g;
    float k = 3.0 / (8.0 * UNITY_PI) * (1.0 - g2) / (2.0 + g2);
    return k * (1.0 + c * c) / pow(max(1e-4, 1.0 + g2 - 2.0 * g * c), 1.5);
}

// Luz espalhada para a câmera ao longo do raio (sol de iluminância 1): espalhamento simples com a sombra da Terra e
// o múltiplo da tabela. tMax limita o raio (névoa até um objeto).
float3 BGIntegrateScattering(float3 ro, float3 rd, float3 sunDir, float tMax, int steps, out float3 transmittance)
{
    float tTop = BGRaySphere(ro, rd, BG_R_TOP);
    float tGround = BGRaySphere(ro, rd, BG_R_GROUND);
    float t = tTop;
    if (tGround > 0.0) t = min(t, tGround);
    t = min(max(t, 0.0), tMax);

    float c = dot(rd, sunDir);
    float pR = BGRayleighPhase(c), pM = BGMiePhase(c, _BG_AtmMieG);
    float3 L = 0.0, T = 1.0;
    float dt = t / steps;
    for (int i = 0; i < steps; i++)
    {
        float3 p = ro + rd * ((i + 0.5) * dt);
        float r = length(p);
        float sunMu = dot(p / r, sunDir);
        BGMedium m = BGSampleMedium(r - BG_R_GROUND);
        float lit = BGRaySphere(p, sunDir, BG_R_GROUND) > 0.0 ? 0.0 : 1.0;
        float3 S = (m.scatR * pR + m.scatM * pM) * BGTransmittance(r, sunMu) * lit + (m.scatR + m.scatM) * BGMultiScat(r, sunMu);
        float3 stepT = exp(-m.ext * dt);
        // Integral exata do trecho com meio constante: não escurece com passos grandes.
        L += T * (S - S * stepT) / max(m.ext, 1e-7);
        T *= stepT;
    }
    transmittance = T;
    return L;
}

// Mapeamento da vista do céu (Hillaire): metade de cima até o horizonte, metade de baixo até o nadir, com mais
// resolução perto do horizonte; u = ângulo até o sol no plano horizontal, com mais resolução perto dele.
float BGZenithHorizonAngle(float r, out float beta)
{
    float vHorizon = sqrt(max(0.0, r * r - BG_R_GROUND * BG_R_GROUND));
    beta = acos(clamp(vHorizon / r, -1.0, 1.0));
    return UNITY_PI - beta;
}

float2 BGSkyViewUv(float r, float viewZenithCos, float lightViewCos)
{
    float beta;
    float zh = BGZenithHorizonAngle(r, beta);
    float a = acos(clamp(viewZenithCos, -1.0, 1.0));
    float v;
    if (a < zh)
    {
        float k = 1.0 - sqrt(max(0.0, 1.0 - a / zh));
        v = 0.5 * k;
    }
    else
    {
        v = 0.5 + 0.5 * sqrt(saturate((a - zh) / beta));
    }
    float u = sqrt(saturate(-lightViewCos * 0.5 + 0.5));
    return float2(u, v);
}

void BGUvToSkyView(float2 uv, float r, out float viewZenithCos, out float lightViewCos)
{
    float beta;
    float zh = BGZenithHorizonAngle(r, beta);
    float a;
    if (uv.y < 0.5)
    {
        float k = 1.0 - 2.0 * uv.y;
        k = 1.0 - k * k;
        a = zh * k;
    }
    else
    {
        float k = uv.y * 2.0 - 1.0;
        a = zh + beta * k * k;
    }
    viewZenithCos = cos(a);
    float u = uv.x * uv.x;
    lightViewCos = -(u * 2.0 - 1.0);
}
#endif
