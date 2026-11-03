using System.Runtime.InteropServices;
using UnityEngine;

namespace BetterGraphics
{
    // Céu físico (shaders AtmosphereLut e PhysicalSky) no lugar do skybox do Enviro. Roda logo depois de cada Update do
    // Enviro (pela Luz): ele não volta a pôr o céu dele por cima, e a direção do sol é a do quadro.
    // A transmitância e o espalhamento múltiplo só mudam com o aerossol, a neblina, o Mie e o albedo do chão; a vista
    // do céu e a tabela da névoa dependem do sol e são refeitas todo quadro (pequenas, baratas).
    // A neblina é a densidade de névoa que o Enviro calcula para o clima (RenderSettings.fogDensity x a intensidade de
    // distância dele): a visibilidade de cada clima fica a mesma, e a cor passa a sair da atmosfera.
    internal static unsafe class PhysicalSky
    {
        // RenderSettings.ambientProbe foi removido do C# pelo IL2CPP; a função nativa continua no motor.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DProbe(float* sh);
        static DProbe _getProbe;
        static bool _noProbe;
        static readonly float[] _probe = new float[27];   // 9 coeficientes de R, depois de G, depois de B
        static Material _lutMat, _skyMat, _enviroSky;
        static RenderTexture _transmittance, _multiScat, _skyView, _aerial;
        static bool _noShader;
        static float _builtAerosol = -1f, _builtMieG = -1f, _builtAlbedo = -1f, _builtHaze = -1f;
        static float _haze;
        static bool _skyFogSaved;
        static float _origSkyFog;
        static bool _origDistanceFog, _origHeightFog;
        static int _frame = -1;

        const float CameraHeightKm = 0.05f;

        static bool Ensure()
        {
            if (_noShader) return false;
            if (_skyMat != null) return true;
            var lut = Pacote.Shader("AtmosphereLut");
            var sky = Pacote.Shader("PhysicalSky");
            if (lut == null || sky == null || !AerialFog.Prepare()) { _noShader = true; return false; }
            _lutMat = new Material(lut) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            _skyMat = new Material(sky) { name = "BetterGraphics_PhysicalSky", hideFlags = HideFlags.DontUnloadUnusedAsset };
            _transmittance = Lut(256, 64, "BetterGraphics_AtmTransmittance");
            _multiScat = Lut(32, 32, "BetterGraphics_AtmMultiScat");
            _skyView = Lut(192, 108, "BetterGraphics_AtmSkyView");
            _aerial = Lut(2048, 32, "BetterGraphics_AtmAerial");
            return true;
        }

        static RenderTexture Lut(int w, int h, string name)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            rt.Create();
            return rt;
        }

        internal static void AfterEnviro(Il2Cpp.EnviroSky sky)
        {
            if (!Cfg.SkyPhysical.Value) { Restore(sky); return; }
            if (sky == null || !Ensure()) return;
            // A Luz chama depois do Update e do LateUpdate do Enviro: as tabelas uma vez por quadro, o skybox nas duas.
            if (Time.frameCount == _frame) { if (RenderSettings.skybox != _skyMat) RenderSettings.skybox = _skyMat; return; }
            _frame = Time.frameCount;
            if (!_transmittance.IsCreated()) { _transmittance.Create(); _multiScat.Create(); _skyView.Create(); _aerial.Create(); _builtAerosol = -1f; }

            var cur = RenderSettings.skybox;
            if (cur != null && cur != _skyMat) _enviroSky = cur;

            Shader.SetGlobalFloat("_BG_AtmAerosol", Mathf.Max(0f, Cfg.SkyAerosol.Value));
            Shader.SetGlobalFloat("_BG_AtmMieG", Mathf.Clamp(Cfg.SkyMieG.Value, 0f, 0.95f));
            Shader.SetGlobalFloat("_BG_AtmGroundAlbedo", Mathf.Clamp01(Cfg.SkyGroundAlbedo.Value));
            Shader.SetGlobalFloat("_BG_AtmCameraHeight", CameraHeightKm);
            Shader.SetGlobalVector("_BG_AtmSunDir", SunDirection(sky));
            var fs = sky.fogSettings;
            _haze = Mathf.Max(0f, RenderSettings.fogDensity * fs.distanceFogIntensity * 1000f * Cfg.SkyFogMatch.Value);
            Shader.SetGlobalFloat("_BG_AtmHaze", _haze);
            Shader.SetGlobalFloat("_BG_AtmHazeHeight", Mathf.Max(0.01f, Cfg.SkyHazeHeight.Value));
            Shader.SetGlobalTexture("_BG_TransmittanceLut", _transmittance);
            Shader.SetGlobalTexture("_BG_MultiScatLut", _multiScat);
            Shader.SetGlobalTexture("_BG_SkyViewLut", _skyView);

            // A neblina muda devagar com o clima: refaz as tabelas fixas quando ela anda mais de 1%.
            bool hazeMoved = Mathf.Abs(_haze - _builtHaze) > 0.01f * Mathf.Max(_builtHaze, 0.01f);
            if (hazeMoved || _builtAerosol != Cfg.SkyAerosol.Value || _builtMieG != Cfg.SkyMieG.Value || _builtAlbedo != Cfg.SkyGroundAlbedo.Value)
            {
                Graphics.Blit(null, _transmittance, _lutMat, 0);
                Graphics.Blit(null, _multiScat, _lutMat, 1);
                _builtAerosol = Cfg.SkyAerosol.Value; _builtMieG = Cfg.SkyMieG.Value; _builtAlbedo = Cfg.SkyGroundAlbedo.Value; _builtHaze = _haze;
            }
            Graphics.Blit(null, _skyView, _lutMat, 2);

            SetSkyMaterial(sky);
            if (RenderSettings.skybox != _skyMat) RenderSettings.skybox = _skyMat;

            if (!_skyFogSaved) { _skyFogSaved = true; _origSkyFog = fs.skyFogIntensity; _origDistanceFog = fs.distanceFog; _origHeightFog = fs.heightFog; }
            // A névoa do Enviro pintava o céu dele perto do horizonte; com a atmosfera própria o céu já tem a dele.
            fs.skyFogIntensity = 0f;
            bool fog = Cfg.SkyAerialFog.Value;
            if (fog)
            {
                Graphics.Blit(null, _aerial, AerialFog.Material, 0);
                Shader.SetGlobalTexture("_BG_AerialLut", _aerial);
            }
            if (fog) SetFogAmbient();
            fs.distanceFog = fog ? false : _origDistanceFog;
            fs.heightFog = fog ? false : _origHeightFog;
            AerialFog.Frame(fog);
        }

        // Média da sonda ambiente em todas as direções (só o termo constante sobrevive à média) e a fração da extinção
        // perto do chão que é neblina.
        static void SetFogAmbient()
        {
            var amb = Vector3.zero;
            if (!_noProbe)
            {
                _getProbe ??= Pacote.Funcao<DProbe>("UnityEngine.RenderSettings::get_ambientProbe_Injected");
                if (_getProbe == null) _noProbe = true;
                else
                {
                    fixed (float* f = _probe) _getProbe(f);
                    amb = new Vector3(_probe[0], _probe[9], _probe[18]);
                }
            }
            Shader.SetGlobalVector("_BG_FogAmbient", amb);
            float clean = 0.0331f + MieExtinction * Mathf.Max(0f, Cfg.SkyAerosol.Value);
            Shader.SetGlobalFloat("_BG_FogHazeShare", _haze / (_haze + clean));
        }

        // Sol que ilumina a atmosfera: o do topo, cortado pela chuva como a luz direcional (a névoa da chuva é cinza
        // escura, iluminada pelas nuvens, não pelo sol aberto).
        static float SunScale() => Mathf.PI * Mathf.Max(0f, Cfg.SkySunIntensity.Value) * (1f - 0.7f * Luz.ChuvaAgora());

        // Mesmas constantes do Atmosphere.cginc (km): a luz do sol na cena é a do topo da atmosfera vezes o que passa dela.
        static readonly Vector3 RayleighScattering = new(5.802e-3f, 13.558e-3f, 33.1e-3f);
        static readonly Vector3 OzoneAbsorption = new(0.650e-3f, 1.881e-3f, 0.085e-3f);
        const float RGround = 6360f, RTop = 6460f, MieExtinction = 4.44e-3f;
        static Vector3 _sunColor = Vector3.one;
        static float _sunIntensity;

        // Transmitância do topo da atmosfera até a câmera na direção do sol (0 com o sol atrás da Terra).
        static Vector3 SunTransmittance(float mu, float aerosol)
        {
            float r = RGround + CameraHeightKm;
            float b = r * mu, disc = b * b - (r * r - RGround * RGround);
            if (disc > 0f && -b - Mathf.Sqrt(disc) > 0f) return Vector3.zero;
            float t = -b + Mathf.Sqrt(b * b - (r * r - RTop * RTop));
            const int N = 48;
            float dt = t / N;
            Vector3 depth = Vector3.zero;
            for (int i = 0; i < N; i++)
            {
                float s = (i + 0.5f) * dt;
                float h = Mathf.Sqrt(r * r + s * s + 2f * r * mu * s) - RGround;
                float ozone = Mathf.Max(0f, 1f - Mathf.Abs(h - 25f) / 15f);
                float mie = MieExtinction * aerosol * Mathf.Exp(-h / 1.2f) + _haze * Mathf.Exp(-h / Mathf.Max(0.01f, Cfg.SkyHazeHeight.Value));
                depth += (RayleighScattering * Mathf.Exp(-h / 8f) + Vector3.one * mie + OzoneAbsorption * ozone) * dt;
            }
            return new Vector3(Mathf.Exp(-depth.x), Mathf.Exp(-depth.y), Mathf.Exp(-depth.z));
        }

        // Luz do sol com a cor e a força que sobram depois da atmosfera: branca ao meio-dia, dourada e fraca no fim da
        // tarde. Roda antes da Luz.Sol (que multiplica pela força do mod e corta na chuva). À noite a luz é a lua do Enviro.
        internal static void DriveSun(Il2Cpp.EnviroSky sky)
        {
            if (!Cfg.SkyPhysical.Value || !Cfg.SkyPhysicalSun.Value || _noShader || sky == null || sky.isNight) return;
            var c = sky.Components;
            var light = c != null && c.DirectLight != null ? c.DirectLight.GetComponent<Light>() : null;
            if (light == null) return;
            var dir = SunDirection(sky);
            var T = SunTransmittance(dir.y, Mathf.Max(0f, Cfg.SkyAerosol.Value));
            // O disco (0,27°) entra no horizonte aos poucos.
            T *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.0047f, 0.0047f, dir.y));
            float max = Mathf.Max(T.x, Mathf.Max(T.y, T.z));
            _sunColor = max > 1e-6f ? T / max : Vector3.one;
            _sunIntensity = Mathf.Max(0f, Cfg.SkySunIntensity.Value) * max;
            light.color = new Color(_sunColor.x, _sunColor.y, _sunColor.z, 1f);
            light.intensity = _sunIntensity;
        }

        // Objeto Sun do Enviro: segue o sol também à noite (a luz direcional vira a lua).
        static Vector3 SunDirection(Il2Cpp.EnviroSky sky)
        {
            var c = sky.Components;
            if (c != null && c.Sun != null) return (c.Sun.transform.position - sky.transform.position).normalized;
            var sun = RenderSettings.sun;
            return sun != null ? -sun.transform.forward : Vector3.up;
        }

        static void SetSkyMaterial(Il2Cpp.EnviroSky sky)
        {
            var m = _skyMat;
            m.SetFloat("_BG_SkyScale", SunScale());
            Shader.SetGlobalFloat("_BG_AtmSunScale", SunScale());
            m.SetFloat("_BG_SunDiskMax", Mathf.Max(0f, Cfg.SkySunDiskMax.Value));
            float glow = Mathf.Max(0f, Cfg.SkyCityGlow.Value);
            var glowColor = new Vector4(glow * 0.85f, glow * 0.95f, glow * 1.1f, 0f);
            m.SetVector("_BG_SkyCityGlow", glowColor);
            Shader.SetGlobalVector("_BG_SkyCityGlow", glowColor);
            var w = sky.currentWeatherSkyMod;
            m.SetVector("_BG_SkyWeather", new Vector4(w.r, w.g, w.b, w.a));
            Shader.SetGlobalVector("_BG_SkyWeather", new Vector4(w.r, w.g, w.b, w.a));

            // Estrelas, galáxia e lua do Enviro: texturas do material dele e valores que ele põe nos globais.
            var e = _enviroSky;
            if (e != null)
            {
                if (e.HasProperty("_Stars")) m.SetTexture("_Stars", e.GetTexture("_Stars"));
                if (e.HasProperty("_Galaxy")) m.SetTexture("_Galaxy", e.GetTexture("_Galaxy"));
                if (e.HasProperty("_MoonTex")) m.SetTexture("_MoonTex", e.GetTexture("_MoonTex"));
            }
            m.SetMatrix("_BG_StarsMatrix", Pick(e, "_StarsMatrix"));
            m.SetFloat("_BG_StarsIntensity", PickFloat(e, "_StarsIntensity"));
            m.SetFloat("_BG_GalaxyIntensity", PickFloat(e, "_GalaxyIntensity"));
            m.SetFloat("_BG_MoonBrightness", Mathf.Max(PickFloat(e, "_MoonBrightness"), 0f));
            m.SetFloat("_BG_MoonAngle", Mathf.Clamp(Cfg.SkyMoonAngle.Value, 0.001f, 0.2f));
            var c = sky.Components;
            var moonDir = c != null && c.Moon != null ? (c.Moon.transform.position - sky.transform.position).normalized : Vector3.down;
            m.SetVector("_BG_MoonDir", moonDir);
            // Halo só com a lua acima do horizonte; a força acompanha o brilho que o Enviro dá à lua (fase).
            float moonUp = Mathf.Clamp01(moonDir.y * 5f);
            float halo = Mathf.Max(0f, Cfg.SkyMoonGlow.Value) * moonUp * Mathf.Max(PickFloat(e, "_MoonBrightness"), 0f);
            m.SetVector("_BG_MoonGlow", new Vector4(halo * 0.9f, halo * 0.95f, halo, 0f));
        }

        // O Enviro põe alguns valores no material e outros como globais: vale o que estiver preenchido.
        static Matrix4x4 Pick(Material e, string name)
        {
            if (e != null)
            {
                var v = e.GetMatrix(name);
                if (v != Matrix4x4.zero) return v;
            }
            var g = Shader.GetGlobalMatrix(name);
            return g != Matrix4x4.zero ? g : Matrix4x4.identity;
        }

        static float PickFloat(Material e, string name)
        {
            if (e != null)
            {
                float v = e.GetFloat(name);
                if (v != 0f) return v;
            }
            return Shader.GetGlobalFloat(name);
        }

        internal static void Restore(Il2Cpp.EnviroSky sky)
        {
            if (_enviroSky != null && RenderSettings.skybox == _skyMat) RenderSettings.skybox = _enviroSky;
            if (_skyFogSaved && sky != null)
            {
                sky.fogSettings.skyFogIntensity = _origSkyFog;
                sky.fogSettings.distanceFog = _origDistanceFog;
                sky.fogSettings.heightFog = _origHeightFog;
            }
            _skyFogSaved = false;
            AerialFog.Remove();
        }

        internal static void SceneChanged() { _enviroSky = null; _skyFogSaved = false; AerialFog.Remove(); }

        internal static string State() =>
            $"céu: {(_noShader ? "sem shader" : RenderSettings.skybox == _skyMat && _skyMat != null ? "físico" : "Enviro")}, aerossol {Cfg.SkyAerosol.Value:0.0}, neblina {_haze:0.000}/km (ambiente {_probe[0]:0.000} {_probe[9]:0.000} {_probe[18]:0.000}), sol no topo {Cfg.SkySunIntensity.Value:0.00}, sol na cena {_sunIntensity:0.000} cor ({_sunColor.x:0.000}, {_sunColor.y:0.000}, {_sunColor.z:0.000}), skybox {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "-")}";
    }
}
