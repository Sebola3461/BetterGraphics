using UnityEngine;

namespace BetterGraphics
{
    // Céu físico (shaders AtmosphereLut e PhysicalSky) no lugar do skybox do Enviro. Roda logo depois de cada Update do
    // Enviro (pela Luz): ele não volta a pôr o céu dele por cima, e a direção do sol é a do quadro.
    // A transmitância e o espalhamento múltiplo só mudam com o aerossol, o Mie e o albedo do chão; a vista do céu
    // depende do sol e é refeita todo quadro (192x108, barata).
    internal static class PhysicalSky
    {
        static Material _lutMat, _skyMat, _enviroSky;
        static RenderTexture _transmittance, _multiScat, _skyView;
        static bool _noShader;
        static float _builtAerosol = -1f, _builtMieG = -1f, _builtAlbedo = -1f;
        static bool _skyFogSaved;
        static float _origSkyFog;
        static int _frame = -1;

        const float CameraHeightKm = 0.05f;

        static bool Ensure()
        {
            if (_noShader) return false;
            if (_skyMat != null) return true;
            var lut = Pacote.Shader("AtmosphereLut");
            var sky = Pacote.Shader("PhysicalSky");
            if (lut == null || sky == null) { _noShader = true; return false; }
            _lutMat = new Material(lut) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            _skyMat = new Material(sky) { name = "BetterGraphics_PhysicalSky", hideFlags = HideFlags.DontUnloadUnusedAsset };
            _transmittance = Lut(256, 64, "BetterGraphics_AtmTransmittance");
            _multiScat = Lut(32, 32, "BetterGraphics_AtmMultiScat");
            _skyView = Lut(192, 108, "BetterGraphics_AtmSkyView");
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
            if (!_transmittance.IsCreated()) { _transmittance.Create(); _multiScat.Create(); _skyView.Create(); _builtAerosol = -1f; }

            var cur = RenderSettings.skybox;
            if (cur != null && cur != _skyMat) _enviroSky = cur;

            Shader.SetGlobalFloat("_BG_AtmAerosol", Mathf.Max(0f, Cfg.SkyAerosol.Value));
            Shader.SetGlobalFloat("_BG_AtmMieG", Mathf.Clamp(Cfg.SkyMieG.Value, 0f, 0.95f));
            Shader.SetGlobalFloat("_BG_AtmGroundAlbedo", Mathf.Clamp01(Cfg.SkyGroundAlbedo.Value));
            Shader.SetGlobalFloat("_BG_AtmCameraHeight", CameraHeightKm);
            Shader.SetGlobalVector("_BG_AtmSunDir", SunDirection(sky));
            Shader.SetGlobalTexture("_BG_TransmittanceLut", _transmittance);
            Shader.SetGlobalTexture("_BG_MultiScatLut", _multiScat);
            Shader.SetGlobalTexture("_BG_SkyViewLut", _skyView);

            if (_builtAerosol != Cfg.SkyAerosol.Value || _builtMieG != Cfg.SkyMieG.Value || _builtAlbedo != Cfg.SkyGroundAlbedo.Value)
            {
                Graphics.Blit(null, _transmittance, _lutMat, 0);
                Graphics.Blit(null, _multiScat, _lutMat, 1);
                _builtAerosol = Cfg.SkyAerosol.Value; _builtMieG = Cfg.SkyMieG.Value; _builtAlbedo = Cfg.SkyGroundAlbedo.Value;
            }
            Graphics.Blit(null, _skyView, _lutMat, 2);

            SetSkyMaterial(sky);
            if (RenderSettings.skybox != _skyMat) RenderSettings.skybox = _skyMat;

            // A névoa do Enviro pintava o céu dele perto do horizonte; com a atmosfera própria o céu já tem a dele.
            var fs = sky.fogSettings;
            if (!_skyFogSaved) { _skyFogSaved = true; _origSkyFog = fs.skyFogIntensity; }
            fs.skyFogIntensity = 0f;
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
            m.SetFloat("_BG_SkyScale", Mathf.PI * Mathf.Max(0f, Cfg.SkySunIntensity.Value));
            m.SetFloat("_BG_SunDiskMax", Mathf.Max(0f, Cfg.SkySunDiskMax.Value));
            float glow = Mathf.Max(0f, Cfg.SkyCityGlow.Value);
            m.SetVector("_BG_SkyCityGlow", new Vector4(glow * 0.85f, glow * 0.95f, glow * 1.1f, 0f));
            var w = sky.currentWeatherSkyMod;
            m.SetVector("_BG_SkyWeather", new Vector4(w.r, w.g, w.b, w.a));

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
            if (_skyFogSaved && sky != null) sky.fogSettings.skyFogIntensity = _origSkyFog;
            _skyFogSaved = false;
        }

        internal static void SceneChanged() { _enviroSky = null; _skyFogSaved = false; }

        internal static string State() =>
            $"céu: {(_noShader ? "sem shader" : RenderSettings.skybox == _skyMat && _skyMat != null ? "físico" : "Enviro")}, aerossol {Cfg.SkyAerosol.Value:0.0}, sol {Cfg.SkySunIntensity.Value:0.00}, skybox {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "-")}";
    }
}
