using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterGraphics
{
    // Vidros dos veículos. Os shaders do jogo só SOMAM o reflexo por cima do interior (que é claro),
    // e o reflexo some; no vidro de verdade, o que reflete deixa de atravessar (Fresnel). Então:
    //  - véu: material preto translúcido desenhado antes do vidro, a luz que "não atravessa";
    //  - reflexo: para-brisa (Glass Reflective) usa a sonda em tempo real com força maior; as janelas
    //    (OneClick Glass/Steamy Refract, que ignoram sondas) ganham uma camada com cópia do para-brisa;
    //  - fumê: o _Color das janelas multiplica o que aparece através delas.
    // Força pela distância da câmera ao ônibus: colado no vidro se vê através, de longe vira espelho.
    // Dentro do ônibus quase nada reflete e não há véu.
    // Só peças pequenas dentro de veículos: na chuva o jogo liga outros objetos com o mesmo shader.
    // Com o vidro físico ligado (Lighting.physical_glass) o vidro seco vira o VehicleGlass, sem véu nem camada: Fresnel de
    // verdade e transmissão por objeto (para-brisa claro, janelas laterais fumê).
    internal static class Vidros
    {
        sealed class Vidro { public Material m; public bool janela; public Color refl, cor; public Texture cube; }
        static readonly Dictionary<IntPtr, Vidro> _vidros = new();
        static readonly Dictionary<IntPtr, Renderer> _renderers = new();
        static readonly Dictionary<int, Material> _veus = new();
        static readonly Dictionary<IntPtr, UnityEngine.Rendering.ShadowCastingMode> _sombraOrig = new();
        static Material _modelo, _camada;
        static Shader _shaderVeu;
        internal static void CenaMudou() { }

        static Shader _glassShader;
        static bool _noGlassShader;
        static readonly Dictionary<IntPtr, Material> _glassClone = new();     // vidro do jogo -> VehicleGlass
        static readonly Dictionary<IntPtr, Material> _glassOriginal = new();  // VehicleGlass -> vidro do jogo

        static Material GlassClone(Material game)
        {
            if (_glassClone.TryGetValue(game.Pointer, out var c) && c != null) return c;
            c = new Material(_glassShader) { name = "BetterGraphics_VehicleGlass", renderQueue = game.renderQueue, hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (game.HasProperty("_Color")) c.SetColor("_Color", game.GetColor("_Color"));
            _glassClone[game.Pointer] = c;
            _glassOriginal[c.Pointer] = game;
            return c;
        }

        // Vidro voltado para a frente ou para trás do veículo (fino em z no espaço dele): para-brisa, vigia, farol.
        static bool FacesFrontOrBack(Renderer r)
        {
            var root = r.transform.root;
            var b = r.bounds;
            Vector3 mn = Vector3.one * float.MaxValue, mx = Vector3.one * float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = root.InverseTransformPoint(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
            }
            var s = mx - mn;
            return s.z < 0.4f * Mathf.Max(s.x, s.y);
        }

        static bool PhysicalGlass(Renderer r, Material vidro)
        {
            if (!Cfg.LightingPhysicalGlass.Value || _noGlassShader) return false;
            if (_glassShader == null)
            {
                _glassShader = Pacote.Shader("VehicleGlass");
                if (_glassShader == null) { _noGlassShader = true; return false; }
            }
            r.sharedMaterials = new[] { GlassClone(vidro) };
            var block = new MaterialPropertyBlock();
            block.SetFloat("_BG_GlassT", FacesFrontOrBack(r) ? Cfg.LightingGlassFrontTransmission.Value : Cfg.LightingGlassSideTransmission.Value);
            r.SetPropertyBlock(block);
            return true;
        }

        internal static void Manter()
        {
            if (!Cfg.VidrosAtivo.Value && (_vidros.Count > 0 || _renderers.Count > 0)) { Restaurar(); Varredura.Reiniciar(); }
        }

        // Chamado pela Varredura para renderers de material único com shader de vidro. Devolve true se
        // é vidro de veículo (aí a Varredura também traz as versões inativas das janelas do veículo).
        internal static bool Registrar(Renderer r, Material m, bool janela)
        {
            if (!Cfg.VidrosAtivo.Value || _renderers.ContainsKey(r.Pointer)) return false;
            // Peça pequena, fora da câmera: na chuva o jogo liga outros objetos com o mesmo shader.
            if (r.bounds.size.magnitude > 16f) return false;
            var cam = Pos.Cam;
            if (cam != null && r.transform.IsChildOf(cam.transform)) return false;
            if (!_vidros.ContainsKey(m.Pointer))
                _vidros[m.Pointer] = !janela
                    ? new Vidro { m = m, refl = m.HasProperty("_ReflectColor") ? m.GetColor("_ReflectColor") : Color.clear, cube = m.HasProperty("_Cube") ? m.GetTexture("_Cube") : null }
                    : new Vidro { m = m, janela = true, cor = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white };
            bool primeiroModelo = !janela && _modelo == null;
            if (primeiroModelo) _modelo = m;
            Camadas(r, m, janela);
            if (primeiroModelo)
            {
                foreach (var (rr, vv) in _semModelo) if (rr != null) Camadas(rr, vv, true);
                _semModelo.Clear();
            }
            return true;
        }

        static readonly List<(Renderer r, Material m)> _semModelo = new();

        static void Camadas(Renderer r, Material vidro, bool janela)
        {
            if (!janela && PhysicalGlass(r, vidro))
            {
                _renderers[r.Pointer] = r;
                if (Cfg.VidroSemSombra.Value)
                {
                    if (!_sombraOrig.ContainsKey(r.Pointer)) _sombraOrig[r.Pointer] = r.shadowCastingMode;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                return;
            }
            var lista = new List<Material>();
            var veu = Veu(vidro, !janela);
            if (veu != null) lista.Add(veu);
            lista.Add(vidro);
            if (janela && _modelo != null)
            {
                if (_camada == null)
                {
                    _camada = new Material(_modelo) { name = "BetterGraphics_ReflexoJanela" };
                    _camada.hideFlags = HideFlags.DontUnloadUnusedAsset;
                }
                lista.Add(_camada);
            }
            else if (janela) { _semModelo.Add((r, vidro)); return; } // completa quando o para-brisa (modelo) aparecer
            r.sharedMaterials = lista.ToArray();
            _renderers[r.Pointer] = r;
            // Vidro não bloqueia o sol: com sombra ligada cada janela vira parede para a luz, e o salão
            // (que num ônibus é cheio de vidro e bem claro) ficava inteiro na sombra.
            if (Cfg.VidroSemSombra.Value)
            {
                if (!_sombraOrig.ContainsKey(r.Pointer)) _sombraOrig[r.Pointer] = r.shadowCastingMode;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        // Véu por fila de renderização e por tipo. O vidro seco (Glass Reflective) não tem cor para fazer
        // o fumê, então o véu dele carrega também o escurecimento do fumê: seco e molhado ficam iguais
        // e a troca que o jogo faz com a chuva não pisca.
        static readonly HashSet<IntPtr> _veusSecos = new();

        static Material Veu(Material vidro, bool seco)
        {
            if (_shaderVeu == null) _shaderVeu = Shader.Find("Legacy Shaders/Transparent/Diffuse");
            if (_shaderVeu == null) return null;
            int fila = vidro.renderQueue - 1;
            int chave = fila * 2 + (seco ? 1 : 0);
            if (_veus.TryGetValue(chave, out var v) && v != null) return v;
            v = new Material(_shaderVeu) { name = "BetterGraphics_VeuVidro", color = new Color(0f, 0f, 0f, 0f), renderQueue = fila };
            v.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (seco) _veusSecos.Add(v.Pointer);
            return _veus[chave] = v;
        }

        static bool Nosso(Material m) => m != null && m.name.StartsWith("BetterGraphics_", StringComparison.Ordinal);

        // Todo frame: força conforme a posição da câmera.
        internal static void Quadro()
        {
            if (_vidros.Count == 0) return;
            var cam = Pos.Cam;
            if (cam == null) return;
            var pos = cam.transform.position;
            bool dentro = Veiculo.Dentro(pos);
            float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.5f, 14f, Veiculo.Distancia(pos)));
            float f = R(dentro ? Cfg.VidroReflexoDentro.Value : Mathf.Lerp(Cfg.VidroReflexoPerto.Value, Cfg.VidroReflexo.Value, k));
            float a = dentro ? 0f : R(Mathf.Lerp(Cfg.VidroVeuPerto.Value, Cfg.VidroVeu.Value, k));
            float t = Mathf.Clamp01(Cfg.VidroTom.Value);
            var tom = new Color(t * 0.92f, t, t * 0.97f, 1f); // fumê levemente esverdeado
            var sonda = Reflexos.Sonda;
            Texture tex = sonda != null ? sonda.realtimeTexture : null;

            // Seco: o véu soma o fumê (passa t da luz, depois o véu de reflexo). Molhado: o fumê vem do _Color.
            // De dentro, a sonda do salão vê as janelas claras e o para-brisa mostrava um fantasma do interior; o que fica
            // atrás do para-brisa de verdade (painel, colunas) é escuro.
            Shader.SetGlobalFloat("_BG_GlassReflect", dentro ? Mathf.Clamp01(Cfg.LightingGlassInsideReflection.Value) : 1f);
            float aSeco = R(1f - (1f - a) * t);
            foreach (var v in _veus.Values) if (v != null) Cor(v, "_Color", new Color(0f, 0f, 0f, _veusSecos.Contains(v.Pointer) ? aSeco : a));
            if (_camada != null)
            {
                Cor(_camada, "_ReflectColor", new Color(f, f, f, 0f));
                if (tex != null && _camada.GetTexture("_Cube") != tex) _camada.SetTexture("_Cube", tex);
            }
            foreach (var v in _vidros.Values)
            {
                if (v.m == null) continue;
                if (v.janela) { if (v.m.HasProperty("_Color")) Cor(v.m, "_Color", new Color(v.cor.r * tom.r, v.cor.g * tom.g, v.cor.b * tom.b, v.cor.a)); continue; }
                var alvo = tex ?? v.cube;
                if (v.m.HasProperty("_Cube") && v.m.GetTexture("_Cube") != alvo) v.m.SetTexture("_Cube", alvo);
                if (v.m.HasProperty("_ReflectColor"))
                    Cor(v.m, "_ReflectColor", dentro ? new Color(f, f, f, v.refl.a) : new Color(Mathf.Max(v.refl.r, f), Mathf.Max(v.refl.g, f), Mathf.Max(v.refl.b, f), v.refl.a));
            }
        }

        static float R(float x) => Mathf.Round(Mathf.Clamp01(x) * 100f) / 100f; // evita regravar por diferenças mínimas
        static void Cor(Material m, string p, Color c) { if (m.GetColor(p) != c) m.SetColor(p, c); }

        internal static void Restaurar()
        {
            foreach (var r in _renderers.Values)
            {
                try
                {
                    if (r == null) continue;
                    var orig = new List<Material>();
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m != null && _glassOriginal.TryGetValue(m.Pointer, out var game) && game != null) orig.Add(game);
                        else if (!Nosso(m)) orig.Add(m);
                    }
                    r.sharedMaterials = orig.ToArray();
                    r.SetPropertyBlock(null);
                    if (_sombraOrig.TryGetValue(r.Pointer, out var sm)) r.shadowCastingMode = sm;
                }
                catch { }
            }
            foreach (var v in _vidros.Values)
            {
                try
                {
                    if (v.m == null) continue;
                    if (v.janela) { if (v.m.HasProperty("_Color")) v.m.SetColor("_Color", v.cor); }
                    else
                    {
                        if (v.m.HasProperty("_Cube")) v.m.SetTexture("_Cube", v.cube);
                        if (v.m.HasProperty("_ReflectColor")) v.m.SetColor("_ReflectColor", v.refl);
                    }
                }
                catch { }
            }
            _renderers.Clear(); _vidros.Clear(); _sombraOrig.Clear(); _semModelo.Clear();
            CenaMudou();
        }

        internal static string Estado()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"vidros {_vidros.Count}, renderers {_renderers.Count}, véus {_veus.Count}{(_shaderVeu == null ? " (shader do véu não achado)" : "")}, modelo {(_modelo != null ? _modelo.name : "-")}");
            foreach (var r in _renderers.Values)
            {
                if (r == null) continue;
                var nomes = new List<string>();
                foreach (var m in r.sharedMaterials) nomes.Add(m == null ? "-" : $"{m.name}[{m.renderQueue}]");
                sb.AppendLine($"   {r.transform.root.name}/{r.name} | tam {r.bounds.size} | {string.Join(", ", nomes)}");
            }
            return sb.ToString();
        }
    }
}
