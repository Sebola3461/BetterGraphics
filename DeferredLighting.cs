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

        static Shader _shader, _reflShader;
        static bool _applied, _noShader, _reflApplied, _noReflShader;

        sealed class Original { public Light L; public float Range, Intensity; public Color Color; public LightShadows Shadows; }
        static readonly Dictionary<IntPtr, Original> _changed = new();
        static readonly List<Light> _lights = new();
        static float _nextScan, _nextPick;
        static readonly HashSet<IntPtr> _shadowed = new();

        internal static void CenaMudou() { _changed.Clear(); _lights.Clear(); _shadowed.Clear(); _farOff.Clear(); _nextScan = 0f; }

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
            Reflections();
            float m = Mathf.Max(Cfg.LightingRangeMultiplier.Value, 1f);
            Shader.SetGlobalFloat("_BG_LightK", BaseK * Mathf.Max(Cfg.LightingBrightness.Value, 0f) / (m * m));
            Shader.SetGlobalFloat("_BG_PhysicalFalloff", Cfg.LightingPhysicalFalloff.Value ? 1f : 0f);
            Shader.SetGlobalFloat("_BG_SunRadius", 0.0047f);
        }

        // Reflexos deferred próprios (shader DeferredReflections), com preferência própria.
        static void Reflections()
        {
            bool want = Cfg.LightingCustomReflections.Value;
            if (want && !_reflApplied && !_noReflShader)
            {
                _reflShader ??= Pacote.Shader("DeferredReflections");
                if (_reflShader == null) { _noReflShader = true; return; }
                GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredReflections, _reflShader);
                GraphicsSettings.SetShaderMode(BuiltinShaderType.DeferredReflections, BuiltinShaderMode.UseCustom);
                _reflApplied = true;
                Log.Msg("Reflexos deferred próprios ligados");
            }
            else if (!want && _reflApplied)
            {
                GraphicsSettings.SetShaderMode(BuiltinShaderType.DeferredReflections, BuiltinShaderMode.UseBuiltin);
                _reflApplied = false;
            }
        }

        static int _reflDebug;
        internal static string ReflDebug(int v) { _reflDebug = v; return "reflexos debug " + v; }

        // Todo quadro (barato: só refaz a escolha a cada 0,5 s).
        internal static void Quadro()
        {
            if (_reflApplied)
            {
                var cam0 = Pos.Cam;
                bool shelter = Molhado.Abrigo(cam0, out var map, out var area);
                if (shelter) Shader.SetGlobalTexture("_BG_ReflShelter", map);
                Shader.SetGlobalVector("_BG_ReflShelterArea", shelter ? area : Vector4.zero);
                // O Luz já escalou o reflexo do céu pela visibilidade medida da câmera; aqui é por ponto.
                Shader.SetGlobalFloat("_BG_ReflSkyUndo", Cfg.AmbienteCeu.Value ? 1f / Mathf.Max(Ceu.Fator(), 0.05f) : 1f);
                Shader.SetGlobalFloat("_BG_ReflDebug", _reflDebug);
            }
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
                _changed[l.Pointer] = o = new Original { L = l, Range = l.range, Intensity = l.intensity, Color = l.color, Shadows = l.shadows };
            var cam = Pos.Cam;
            float d = cam != null ? Vector3.Distance(l.transform.position, cam.transform.position) : 0f;
            ApplyRange(l, o, d);
            if (IsStreetlight(l) && Cfg.LightingStreetlightKelvin.Value > 0f) l.color = Kelvin(Cfg.LightingStreetlightKelvin.Value);
        }

        // Alcance longo só perto da câmera: cada luz cuja esfera toca a vista é desenhada à parte (no processador, o
        // gargalo), e com o alcance x3 entravam centenas de postes distantes. Além de 250 m a luz apaga: a poça dela tem
        // poucos pixels e está na névoa, mas a chamada de desenho custava igual.
        const float LongRangeDistance = 80f, OffDistance = 250f;
        static readonly HashSet<IntPtr> _farOff = new();

        // O brilho da queda física é definido no alcance (k global dividido pelo multiplicador ao quadrado): a luz de alcance
        // curto recebe a intensidade de volta na mesma proporção, para mudar só até onde vai, não o quanto ilumina.
        // Só nos postes do mapa: a intensidade das luzes dos veículos é do jogo (freio, piscas).
        static void ApplyRange(Light l, Original o, float distance)
        {
            float mult = Mathf.Max(Cfg.LightingRangeMultiplier.Value, 1f);
            bool poste = IsStreetlight(l);
            float m = poste && distance >= LongRangeDistance ? Mathf.Min(mult, 1.4f) : mult;
            float r = o.Range * m;
            if (Mathf.Abs(l.range - r) > 0.01f) l.range = r;
            if (!poste) return;
            float k = mult / m, i = o.Intensity * k * k;
            if (Mathf.Abs(l.intensity - i) > 0.001f) l.intensity = i;
        }

        // Sombra nos N postes ligados mais perto da câmera; os outros voltam ao original. Também o alcance de cada luz.
        static void PickShadows()
        {
            var cam = Pos.Cam;
            int n = Mathf.Max(Cfg.LightingShadowedStreetlights.Value, 0);
            var best = new List<(float d, Light l)>();
            if (cam != null)
            {
                var c = cam.transform.position;
                foreach (var l in _lights)
                {
                    if (l == null || l.WasCollected) continue;
                    float d = Vector3.Distance(l.transform.position, c);
                    if (_changed.TryGetValue(l.Pointer, out var orig))
                    {
                        ApplyRange(l, orig, d);
                        bool longe = d > OffDistance && IsStreetlight(l);
                        // Só apaga o que estava aceso (o jogo liga e desliga os postes); acende de volta o que o mod apagou.
                        if (longe && l.enabled) { l.enabled = false; _farOff.Add(l.Pointer); }
                        else if (!longe && _farOff.Remove(l.Pointer)) l.enabled = true;
                    }
                    if (n > 0 && d < 45f && IsStreetlight(l) && l.enabled && l.gameObject.activeInHierarchy) best.Add((d, l));
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
            if (_reflApplied)
            {
                GraphicsSettings.SetShaderMode(BuiltinShaderType.DeferredReflections, BuiltinShaderMode.UseBuiltin);
                _reflApplied = false;
            }
            foreach (var o in _changed.Values)
                try
                {
                    if (o.L == null || o.L.WasCollected) continue;
                    o.L.range = o.Range; o.L.intensity = o.Intensity; o.L.color = o.Color; o.L.shadows = o.Shadows;
                    if (_farOff.Contains(o.L.Pointer)) o.L.enabled = true;
                }
                catch { }
            _changed.Clear(); _shadowed.Clear(); _farOff.Clear(); _nextScan = 0f;
        }

        internal static string Estado() =>
            $"deferred próprio {(_applied ? "ligado" : _noShader ? "shader falhou" : "desligado")}, reflexos {(_reflApplied ? "próprios" : _noReflShader ? "shader falhou" : "da Unity")}, {_changed.Count} luzes ajustadas, {_shadowed.Count} postes com sombra, k {BaseK * Cfg.LightingBrightness.Value / Mathf.Pow(Mathf.Max(Cfg.LightingRangeMultiplier.Value, 1f), 2f):0.0000}";
    }
}
