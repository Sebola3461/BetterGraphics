using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterGraphics
{
    // Iluminação deferred própria (shader DeferredShading no lugar do Internal-DeferredShading da Unity) e ajuste
    // das luzes locais para ela:
    //  - alcance multiplicado: com a queda física a luz não some no alcance original; a cauda fraca chega ao poste
    //    vizinho (o brilho perto da lâmpada não muda: a constante é dividida pelo quadrado do multiplicador);
    //  - postes do mapa ("_real_") com a cor de LED em kelvin (os de São Paulo são brancos, não amarelos);
    //  - sombra suave nos postes mais próximos da câmera (sombra de poste custa um mapa cúbico por luz).
    internal static class DeferredLighting
    {
        // Queda física igual à da Unity a 40% do alcance original (lá as duas valem ~0,2).
        const float BaseK = 0.0325f;

        static Shader _shader;
        static bool _applied, _noShader;

        sealed class Original { public Light L; public float Range; public Color Color; public LightShadows Shadows; }
        static readonly Dictionary<IntPtr, Original> _changed = new();
        static readonly List<Light> _lights = new();
        static float _nextScan, _nextPick;
        static readonly HashSet<IntPtr> _shadowed = new();

        internal static void CenaMudou() { _changed.Clear(); _lights.Clear(); _shadowed.Clear(); _nextScan = 0f; }

        internal static void Manter()
        {
            if (!Cfg.LightingCustomDeferred.Value) { if (_applied || _changed.Count > 0) Restaurar(); return; }
            if (_noShader) return;
            if (!_applied)
            {
                _shader ??= Pacote.Shader("DeferredShading");
                if (_shader == null) { _noShader = true; return; }
                GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredShading, _shader);
                GraphicsSettings.SetShaderMode(BuiltinShaderType.DeferredShading, BuiltinShaderMode.UseCustom);
                _applied = true;
                Log.Msg("Iluminação deferred própria ligada");
            }
            float m = Mathf.Max(Cfg.LightingRangeMultiplier.Value, 1f);
            Shader.SetGlobalFloat("_BG_LightK", BaseK * Mathf.Max(Cfg.LightingBrightness.Value, 0f) / (m * m));
            Shader.SetGlobalFloat("_BG_PhysicalFalloff", Cfg.LightingPhysicalFalloff.Value ? 1f : 0f);
            Shader.SetGlobalFloat("_BG_SunRadius", 0.0047f);
        }

        // Todo quadro (barato: só refaz a escolha a cada 0,5 s).
        internal static void Quadro()
        {
            if (!_applied) return;
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 10f;
                _lights.Clear();
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                    if (l.type == LightType.Point || l.type == LightType.Spot) _lights.Add(l);
                foreach (var l in _lights) Adjust(l);
            }
            if (Time.unscaledTime < _nextPick) return;
            _nextPick = Time.unscaledTime + 0.5f;
            PickShadows();
        }

        static bool IsStreetlight(Light l) => l.name.IndexOf("_real_", StringComparison.OrdinalIgnoreCase) >= 0;

        static void Adjust(Light l)
        {
            if (l == null || l.WasCollected) return;
            if (!_changed.TryGetValue(l.Pointer, out var o))
                _changed[l.Pointer] = o = new Original { L = l, Range = l.range, Color = l.color, Shadows = l.shadows };
            l.range = o.Range * Mathf.Max(Cfg.LightingRangeMultiplier.Value, 1f);
            if (IsStreetlight(l) && Cfg.LightingStreetlightKelvin.Value > 0f) l.color = Kelvin(Cfg.LightingStreetlightKelvin.Value);
        }

        // Sombra nos N postes ligados mais perto da câmera; os outros voltam ao original.
        static void PickShadows()
        {
            var cam = Pos.Cam;
            int n = Mathf.Max(Cfg.LightingShadowedStreetlights.Value, 0);
            var best = new List<(float d, Light l)>();
            if (cam != null && n > 0)
            {
                var c = cam.transform.position;
                foreach (var l in _lights)
                {
                    if (l == null || l.WasCollected || !IsStreetlight(l) || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                    float d = Vector3.Distance(l.transform.position, c);
                    if (d < 45f) best.Add((d, l));
                }
                best.Sort((a, b) => a.d.CompareTo(b.d));
            }
            var want = new HashSet<IntPtr>();
            for (int i = 0; i < best.Count && i < n; i++) want.Add(best[i].l.Pointer);
            foreach (var p in _shadowed)
                if (!want.Contains(p) && _changed.TryGetValue(p, out var o) && o.L != null && !o.L.WasCollected) o.L.shadows = o.Shadows;
            foreach (var p in want)
                if (_changed.TryGetValue(p, out var o) && o.L != null) { o.L.shadows = LightShadows.Soft; o.L.shadowStrength = 1f; }
            _shadowed.Clear();
            foreach (var p in want) _shadowed.Add(p);
        }

        // Cor de corpo negro em kelvin (aproximação de Tanner Helland), normalizada no canal mais forte.
        internal static Color Kelvin(float k)
        {
            float t = Mathf.Clamp(k, 1000f, 15000f) / 100f;
            float r = t <= 66f ? 255f : 329.698727446f * Mathf.Pow(t - 60f, -0.1332047592f);
            float g = t <= 66f ? 99.4708025861f * Mathf.Log(t) - 161.1195681661f : 288.1221695283f * Mathf.Pow(t - 60f, -0.0755148492f);
            float b = t >= 66f ? 255f : t <= 19f ? 0f : 138.5177312231f * Mathf.Log(t - 10f) - 305.0447927307f;
            var c = new Color(Mathf.Clamp(r, 0f, 255f) / 255f, Mathf.Clamp(g, 0f, 255f) / 255f, Mathf.Clamp(b, 0f, 255f) / 255f);
            float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            return new Color(c.r / mx, c.g / mx, c.b / mx);
        }

        internal static void Restaurar()
        {
            if (_applied)
            {
                GraphicsSettings.SetShaderMode(BuiltinShaderType.DeferredShading, BuiltinShaderMode.UseBuiltin);
                _applied = false;
            }
            foreach (var o in _changed.Values)
                try { if (o.L != null && !o.L.WasCollected) { o.L.range = o.Range; o.L.color = o.Color; o.L.shadows = o.Shadows; } } catch { }
            _changed.Clear(); _shadowed.Clear(); _nextScan = 0f;
        }

        internal static string Estado() =>
            $"deferred próprio {(_applied ? "ligado" : _noShader ? "shader falhou" : "desligado")}, {_changed.Count} luzes ajustadas, {_shadowed.Count} postes com sombra, k {BaseK * Cfg.LightingBrightness.Value / Mathf.Pow(Mathf.Max(Cfg.LightingRangeMultiplier.Value, 1f), 2f):0.0000}";
    }
}
