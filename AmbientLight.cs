using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterGraphics
{
    // Luz ambiente própria por pixel (shader Ambient), num passe antes dos reflexos: céu pela fração visível de
    // cada ponto (mapa de abrigo do Molhado), rebote do chão iluminado pelo sol e brilho do céu da cidade à noite.
    internal static unsafe class AmbientLight
    {
        // RenderSettings.ambientProbe foi removido do C# pelo IL2CPP; a função nativa continua no motor.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DProbe(float* sh);
        static DProbe _probe;
        static readonly float[] _sh = new float[27];   // 9 coeficientes de R, depois de G, depois de B
        static float P(int c, int i) => _sh[c * 9 + i];

        static Material _mat;
        static Mesh _screen;
        static CommandBuffer _cb;
        static Camera _cbCam;
        static bool _noShader;
        static int _debug;
        static readonly int LId = Shader.PropertyToID("_BG_AmbL"), G0Id = Shader.PropertyToID("_BG_AmbG0");

        internal static void CenaMudou() => Remove();

        // O passe está pendurado na câmera: ele é quem escreve a luz ambiente (o Interior só fornece o mapa).
        internal static bool Ativo => _cb != null;

        internal static void Manter()
        {
            if (!Cfg.LightingCustomAmbient.Value || _noShader) { Remove(); return; }
            if (_mat == null)
            {
                var sh = Pacote.Shader("Ambient");
                if (sh == null) { _noShader = true; return; }
                _mat = new Material(sh) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            }
            var cam = Pos.Cam;
            if (cam == _cbCam && _cb != null) return;
            Remove();
            if (cam == null || cam.actualRenderingPath != RenderingPath.DeferredShading) return;
            _screen ??= Molhado.Tela();
            _cb = new CommandBuffer { name = "BetterGraphics Ambiente" };
            // Cópias para ler e escrever no mesmo passe (G-buffer 0 corrigido + luz). Albedo em float linear: a cópia
            // sRGB de 8 bits voltava codificada duas vezes (ver Molhado).
            _cb.GetTemporaryRT(LId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _cb.GetTemporaryRT(G0Id, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _cb.Blit(BuiltinRenderTextureType.CameraTarget, LId);
            _cb.Blit(BuiltinRenderTextureType.GBuffer0, G0Id);
            RenderTargetIdentifier[] targets = { BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.CameraTarget };
            _cb.SetRenderTarget(targets, BuiltinRenderTextureType.CameraTarget);
            _cb.DrawMesh(_screen, Matrix4x4.identity, _mat, 0, 0);
            _cb.ReleaseTemporaryRT(LId);
            _cb.ReleaseTemporaryRT(G0Id);
            cam.AddCommandBuffer(CameraEvent.BeforeReflections, _cb);
            _cbCam = cam;
        }

        static void Remove()
        {
            if (_cb != null && _cbCam != null) try { _cbCam.RemoveCommandBuffer(CameraEvent.BeforeReflections, _cb); } catch { }
            _cb = null; _cbCam = null;
        }

        internal static void Restaurar() => Remove();

        // Todo quadro (LateUpdate).
        internal static void Quadro()
        {
            if (_cb == null || _mat == null) return;
            var cam = _cbCam;
            if (cam == null || cam.WasCollected) { Remove(); return; }
            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            _mat.SetMatrix("_BG_AmbInvVP", vp.inverse);

            // A mesma sonda que o G-buffer usou (para tirar a parcela da Unity), no formato das constantes do shader.
            _probe ??= Pacote.Funcao<DProbe>("UnityEngine.RenderSettings::get_ambientProbe_Injected");
            if (_probe == null) return;
            fixed (float* f = _sh) _probe(f);
            var a = new Vector4[3]; var b = new Vector4[3];
            for (int c = 0; c < 3; c++)
            {
                a[c] = new Vector4(P(c, 3), P(c, 1), P(c, 2), P(c, 0) - P(c, 6));
                b[c] = new Vector4(P(c, 4), P(c, 5), P(c, 6) * 3f, P(c, 7));
            }
            _mat.SetVector("_BG_SHAr", a[0]); _mat.SetVector("_BG_SHAg", a[1]); _mat.SetVector("_BG_SHAb", a[2]);
            _mat.SetVector("_BG_SHBr", b[0]); _mat.SetVector("_BG_SHBg", b[1]); _mat.SetVector("_BG_SHBb", b[2]);
            _mat.SetVector("_BG_SHC", new Vector4(P(0, 8), P(1, 8), P(2, 8), 1f));

            // A sonda já vem com a escala de céu da câmera e da cabine (Luz); aqui a visibilidade é por ponto.
            float intensity = Mathf.Max(RenderSettings.ambientIntensity, 1e-3f);
            _mat.SetFloat("_BG_AmbSkyScale", Cfg.AmbienteForca.Value / intensity);
            _mat.SetFloat("_BG_AmbSkySat", Mathf.Clamp(Cfg.LightingAmbientSkySaturation.Value, 0f, 2f));
            _mat.SetFloat("_BG_AmbStrength", Mathf.Max(Cfg.LightingAmbientStrength.Value, 0f));
            _mat.SetFloat("_BG_AmbWall", Mathf.Clamp01(Cfg.LightingAmbientWallBounce.Value));

            // Rebote: irradiância do sol no chão (na convenção da Unity, sem 1/pi) vezes o albedo médio do chão.
            // Sol e noite vêm do Enviro: a luz direcional dele vira a lua à noite, e RenderSettings.sun pode estar vazio
            // ao carregar. Com ele vazio o passe achava que era noite: sem rebote e com o brilho laranja da cidade em
            // pleno dia (lateral embaixo de cobertura vinho/roxa).
            var sun = SunLight(out bool isNight);
            var bounce = Color.black;
            float sunUp = 0f;
            if (sun != null && !isNight && sun.isActiveAndEnabled)
            {
                sunUp = Mathf.Max(-sun.transform.forward.y, 0f);
                bounce = sun.color * (sun.intensity * sunUp * Mathf.Clamp01(Cfg.LightingGroundAlbedo.Value));
            }
            _mat.SetVector("_BG_AmbBounce", new Vector4(bounce.r, bounce.g, bounce.b, 0f));
            // Céu da cidade à noite; no crepúsculo entra aos poucos, pela altura do sol.
            float night = isNight ? 1f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.05f, 0.15f, sunUp));
            var glow = DeferredLighting.Kelvin(3500f) * (Mathf.Max(Cfg.LightingCityGlow.Value, 0f) * night);
            _mat.SetVector("_BG_AmbCityGlow", new Vector4(glow.r, glow.g, glow.b, 0f));

            bool shelter = Molhado.Abrigo(cam, out var map, out var area);
            if (shelter) _mat.SetTexture("_BG_AmbShelter", map);
            _mat.SetVector("_BG_AmbShelterArea", shelter ? area : Vector4.zero);
            // Salão do ônibus: luz que entra pelas janelas (mapa do Interior) + rebote de paredes e teto.
            bool win = Interior.Janelas(out var vis, out var grade, out var rot, out var size, out float opening);
            _mat.SetFloat("_BG_WinReady", win ? 1f : 0f);
            if (win)
            {
                _mat.SetTexture("_BG_WinVis", vis);
                _mat.SetMatrix("_BG_WinGrade", grade);
                _mat.SetMatrix("_BG_WinRot", rot);
                _mat.SetVector("_BG_WinSize", size);
                var camPos = cam.transform.position;
                float scale = Cfg.AmbienteForca.Value / intensity * (Veiculo.Dentro(camPos) ? Cfg.CabineAmbiente.Value : 1f);
                _mat.SetFloat("_BG_WinScale", scale);
                float reb = Cfg.JanelaRebate.Value * Mathf.Clamp01(opening * 4f) * scale;
                _mat.SetVector("_BG_WinRebate", new Vector4(P(0, 0) * reb, P(1, 0) * reb, P(2, 0) * reb, 0f));
                _mat.SetFloat("_BG_WinForce", Mathf.Clamp01(Cfg.JanelaForca.Value));
            }
            _mat.SetFloat("_BG_AmbDebug", _debug);
            _mat.SetFloat("_BG_AlbedoLimit", Cfg.LightingPhysicalAlbedo.Value ? 1f : 0f);
            // Para o Estado: irradiância num chão aberto, do céu (normal para cima) e do sol.
            // Mesma avaliação do shader com a normal (0, 1, 0): P0 + P1 - P6 - P8 em cada canal.
            float up(int c) => P(c, 0) + P(c, 1) - P(c, 6) - P(c, 8);
            _skyUp = Mathf.Max(up(0) * 0.2126f + up(1) * 0.7152f + up(2) * 0.0722f, 0f) * Cfg.AmbienteForca.Value / intensity * Cfg.LightingAmbientStrength.Value;
            _sunUp = sun != null && !isNight && sun.isActiveAndEnabled ? (sun.color.grayscale * sun.intensity * sunUp) : 0f;
        }

        static float _skyUp, _sunUp;

        // Luz direcional do Enviro (sol de dia, lua de noite) e se é noite; sem Enviro, a luz do sol da cena.
        static Light SunLight(out bool isNight)
        {
            isNight = false;
            var sky = Luz.Sky;
            if (sky != null)
            {
                try
                {
                    isNight = sky.isNight;
                    var t = sky.Components != null ? sky.Components.DirectLight : null;
                    var l = t != null ? t.GetComponent<Light>() : null;
                    if (l != null) return l;
                }
                catch { }
            }
            return RenderSettings.sun;
        }

        // 1 = o que sobra de RT3 sem a ambiente da Unity (deve ficar preto, fora o que emite luz); 2 = céu visível;
        // 3 = luz ambiente própria.
        internal static string Debug(int v) { _debug = v; return "ambiente debug " + v; }

        internal static string Estado() =>
            $"ambiente próprio {(_cb != null ? "ligado" : _noShader ? "shader falhou" : "desligado")}, intensidade da Unity {RenderSettings.ambientIntensity:0.00}, modo {RenderSettings.ambientMode} | chão aberto: céu {_skyUp:0.000}, sol {_sunUp:0.000}, sol+céu / céu = {(_sunUp + _skyUp) / Mathf.Max(_skyUp, 1e-4f):0.0}";
    }
}
