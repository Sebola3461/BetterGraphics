// Céu físico (Atmosphere.cginc) no lugar do skybox do Enviro. As nuvens continuam do Enviro (passe de imagem dele);
// estrelas, galáxia e lua vêm das texturas dele, por baixo da luz da atmosfera. Por ser o skybox da cena, a luz
// ambiente e as sondas de reflexo da Unity passam a sair deste céu.
Shader "BetterGraphics/PhysicalSky"
{
    Properties
    {
        _Stars ("Estrelas", Cube) = "black" {}
        _Galaxy ("Galáxia", Cube) = "black" {}
        _MoonTex ("Lua", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "Atmosphere.cginc"

            samplerCUBE _Stars, _Galaxy;
            sampler2D _MoonTex;
            float4x4 _BG_StarsMatrix;
            float _BG_StarsIntensity, _BG_GalaxyIntensity;
            float3 _BG_MoonDir;
            float _BG_MoonAngle;            // meio ângulo do disco da lua (rad)
            float _BG_MoonBrightness;
            float3 _BG_MoonGlow;            // halo da lua: a luz dela espalhada pelo aerossol
            float _BG_SkyScale;             // pi x intensidade do sol da cena: a atmosfera sai com sol de iluminância 1
            float _BG_SunDiskMax;           // teto da radiância do disco (o real estoura o bloom e pisca no TAA)
            float3 _BG_SkyCityGlow;         // poluição luminosa: luz da cidade espalhada no céu à noite
            float4 _BG_SkyWeather;          // rgb = cor do céu fechado, a = quanto do céu está fechado

            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            float3 MoonDisk(float3 dir)
            {
                float3 m = normalize(_BG_MoonDir);
                float c = dot(dir, m);
                if (c < cos(_BG_MoonAngle * 1.5)) return 0.0;
                float3 right = normalize(cross(abs(m.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0), m));
                float3 up = cross(m, right);
                float2 uv = float2(dot(dir, right), dot(dir, up)) / (2.0 * tan(_BG_MoonAngle)) + 0.5;
                return tex2Dlod(_MoonTex, float4(uv, 0.0, 0.0)).rgb * _BG_MoonBrightness;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                float r = BG_R_GROUND + max(0.001, _BG_AtmCameraHeight);
                float3 sun = normalize(_BG_AtmSunDir);

                float2 h = dir.xz, hs = sun.xz;
                float lh = length(h), ls = length(hs);
                float lightViewCos = (lh > 1e-4 && ls > 1e-4) ? dot(h / lh, hs / ls) : 1.0;
                float3 L = tex2Dlod(_BG_SkyViewLut, float4(BGSkyViewUv(r, dir.y, lightViewCos), 0.0, 0.0)).rgb * _BG_SkyScale;

                // Abaixo do horizonte: chão cinza (albedo do chão sob o sol e o céu), não a neblina iluminada pelo sol.
                // A luz ambiente e as sondas saem deste céu inteiro: com a neblina laranja embaixo, a saia da lataria e
                // os pneus (voltados para baixo) ficavam marrons. O rebote de verdade do asfalto é da GI de tela.
                if (dir.y < 0.0)
                {
                    float3 zenith = tex2Dlod(_BG_SkyViewLut, float4(BGSkyViewUv(r, 1.0, 1.0), 0.0, 0.0)).rgb * _BG_SkyScale;
                    float3 sunT = BGTransmittance(r, max(sun.y, 0.0));
                    float sunGround = _BG_SkyScale / UNITY_PI * dot(sunT, float3(0.2126, 0.7152, 0.0722)) * saturate(sun.y);
                    float ground = _BG_AtmGroundAlbedo * (sunGround + dot(zenith, float3(0.2126, 0.7152, 0.0722)));
                    L = lerp(L, ground.xxx, smoothstep(0.0, -0.08, dir.y));
                }

                // Disco do sol (0,27°), escurecido na borda e atenuado pela atmosfera na direção dele.
                float cosSun = dot(dir, sun);
                const float sunAngle = 0.00465;
                if (cosSun > cos(sunAngle))
                {
                    float rr = saturate(acos(saturate(cosSun)) / sunAngle);
                    float limb = 1.0 - 0.6 * (1.0 - sqrt(saturate(1.0 - rr * rr)));
                    float3 T = BGTransmittance(r, dir.y);
                    float radiance = min(_BG_SkyScale / (UNITY_PI * sunAngle * sunAngle), _BG_SunDiskMax);
                    L += T * radiance * limb;
                }

                // Céu noturno: as estrelas e a lua aparecem por transparência da atmosfera (somem com o céu claro).
                float3 Tview = BGTransmittance(r, max(dir.y, 0.0));
                // Com poluição luminosa a Via Láctea some e só as estrelas mais fortes passam do brilho do céu.
                float glow = max(_BG_SkyCityGlow.g, 0.0);
                float3 sd = mul((float3x3)_BG_StarsMatrix, dir);
                float3 night = texCUBElod(_Stars, float4(sd, 0.0)).rgb * _BG_StarsIntensity / (1.0 + glow * 30.0)
                             + texCUBElod(_Galaxy, float4(sd, 0.0)).rgb * _BG_GalaxyIntensity / (1.0 + glow * 1000.0)
                             + MoonDisk(dir);
                L += night * Tview * step(0.0, dir.y);
                L += _BG_MoonGlow * BGMiePhase(dot(dir, normalize(_BG_MoonDir)), _BG_AtmMieG) * _BG_AtmAerosol;

                // Cidade: luz dos postes e fachadas espalhada no ar, mais forte perto do horizonte.
                L += _BG_SkyCityGlow * (0.35 + 0.65 * exp(-max(dir.y, 0.0) * 4.0));

                // Tempo fechado: o céu vai para a cor das nuvens de baixo (o Enviro manda quanto e qual cor).
                float lum = dot(L, float3(0.2126, 0.7152, 0.0722));
                L = lerp(L, lum * _BG_SkyWeather.rgb, _BG_SkyWeather.a);
                return float4(L, 1.0);
            }
            ENDCG
        }
    }
}
