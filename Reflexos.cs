using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterGraphics
{
    // Sonda de reflexo em tempo real e reflexo nas pinturas.
    //  - A sonda fica 1 m acima do teto do ônibus do jogador: a pintura e os vidros refletem o céu e o
    //    entorno do próprio ônibus. Enxerga só o que a câmera principal enxerga (a seta do GPS fica numa
    //    camada só do minimapa). Uma face por frame, para espalhar o custo.
    //  - As pinturas Standard dos veículos ("matvazio_...") vêm com _GLOSSYREFLECTIONS_OFF e ficam
    //    chapadas; o reflexo é religado com a smoothness limitada (sem virar cromado).
    internal static class Reflexos
    {
        static GameObject _go;
        static ReflectionProbe _sonda;
        static Vector3 _ultimaPosicao = Vector3.positiveInfinity;
        static float _ultimoCubo = -100f;
        static int _id = -1; static float _proxima;

        sealed class PinturaOrig { public Material m; public float gloss = -1f, escala = -1f; }
        static readonly Dictionary<IntPtr, PinturaOrig> _pinturas = new();
        static float _brilhoAplicado = -1f;

        internal static ReflectionProbe Sonda => _sonda != null && _go.activeSelf ? _sonda : null;

        internal static void CenaMudou() { _id = -1; }

        internal static void Manter()
        {
            CriarSonda();
            ManterLataria();
            if (!Cfg.ReflexoPintura.Value) { if (_pinturas.Count > 0) { RestaurarPinturas(); Varredura.Reiniciar(); } return; }
            if (!Mathf.Approximately(_brilhoAplicado, Cfg.BrilhoMax.Value))
            {
                if (_pinturas.Count > 0) { RestaurarPinturas(); Varredura.Reiniciar(); }
                _brilhoAplicado = Cfg.BrilhoMax.Value;
            }
        }

        // Chamado pela Varredura para cada pintura candidata (Standard com reflexo desligado).
        internal static void Pintura(Material m)
        {
            if (!Cfg.ReflexoPintura.Value || _pinturas.ContainsKey(m.Pointer)) return;
            var p = new PinturaOrig { m = m };
            m.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
            if (m.HasProperty("_GlossyReflections")) m.SetFloat("_GlossyReflections", 1f);
            float max = Cfg.BrilhoMax.Value;
            if (m.HasProperty("_Glossiness") && m.GetFloat("_Glossiness") > max) { p.gloss = m.GetFloat("_Glossiness"); m.SetFloat("_Glossiness", max); }
            if (m.HasProperty("_GlossMapScale") && m.GetFloat("_GlossMapScale") > max) { p.escala = m.GetFloat("_GlossMapScale"); m.SetFloat("_GlossMapScale", max); }
            _pinturas[m.Pointer] = p;
        }

        static void CriarSonda()
        {
            if (!Cfg.Sonda.Value) { if (_go != null) _go.SetActive(false); return; }
            if (_go == null)
            {
                _go = new GameObject("BetterGraphics_Sonda");
                UnityEngine.Object.DontDestroyOnLoad(_go);
                _sonda = _go.AddComponent<ReflectionProbe>();
                _sonda.mode = ReflectionProbeMode.Realtime;
                _sonda.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
                _sonda.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;   // render completo por chamada (IndividualFaces deixava o cubo incompleto entre faces: o reflexo piscava e sumia)
                _sonda.hdr = true;
                // Projeção em caixa: as laterais do ônibus refletem a rua com paralaxe (sem ela o reflexo
                // parece pintado na lataria e não anda quando a câmera se move).
                _sonda.boxProjection = true;
                _sonda.importance = 100;          // vence as sondas do mapa e a fixa do ônibus
                // Só o entorno do ônibus usa esta sonda: com caixa de 800 m, um prédio a 300 m refletia a rua
                // em volta do ônibus. Fora dela vale a sonda do mapa ou o céu.
                _sonda.size = new Vector3(80f, 40f, 80f);
                _sonda.blendDistance = 15f;
                _sonda.nearClipPlane = 0.3f;
                _sonda.farClipPlane = 400f;
                // Sem sombras no cubo: cada face redesenharia os mapas de sombra (no processador, o gargalo do jogo), e
                // nos reflexos da lataria e dos vidros a sombra quase não aparece.
                _sonda.shadowDistance = 0f;
                _sonda.clearFlags = ReflectionProbeClearFlags.Skybox;
            }
            int res = Mathf.ClosestPowerOfTwo(Mathf.Clamp(Cfg.SondaResolucao.Value, 64, 1024));
            if (_sonda.resolution != res) _sonda.resolution = res;
            var cam = Pos.Cam;
            if (cam != null)
            {
                int mask = cam.cullingMask & ~(1 << 5);
                if (_sonda.cullingMask != mask) { _sonda.cullingMask = mask; _id = -1; }
            }
            if (!_go.activeSelf) { _go.SetActive(true); _id = -1; }
        }

        // Todo frame: posição e atualização da sonda.
        internal static void Quadro()
        {
            QuadroDentro();
            if (_sonda == null || !_go.activeSelf) return;
            var cam = Pos.Cam;
            if (Veiculo.Raiz() != null) _go.transform.position = Veiculo.AcimaDoTeto();
            else if (cam != null) _go.transform.position = cam.transform.position + Vector3.up * 3f;
            else return;
            if (Time.unscaledTime < _proxima) return;
            if (_id != -1 && !_sonda.IsFinishedRendering(_id)) return;
            // Parada (menos de 2 m desde o último cubo), a vista quase não muda: refaz a cada 2 s, só pelo sol e pelo
            // trânsito. Cada cubo é a cena desenhada 6 vezes no processador, o gargalo do jogo.
            var aqui = _go.transform.position;
            if ((aqui - _ultimaPosicao).sqrMagnitude < 4f && Time.unscaledTime < _ultimoCubo + 2f) return;
            _ultimaPosicao = aqui; _ultimoCubo = Time.unscaledTime;
            _id = _sonda.RenderProbe();
            _proxima = Time.unscaledTime + Mathf.Max(0f, Cfg.SondaIntervalo.Value);
            if (_sonda.realtimeTexture == null) Log.Aviso("Sonda: textura de reflexo nula após render (será renderizada de novo)");
        }

        static void RestaurarPinturas()
        {
            foreach (var p in _pinturas.Values)
            {
                try
                {
                    if (p.m == null) continue;
                    p.m.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
                    if (p.m.HasProperty("_GlossyReflections")) p.m.SetFloat("_GlossyReflections", 0f);
                    if (p.gloss >= 0f) p.m.SetFloat("_Glossiness", p.gloss);
                    if (p.escala >= 0f) p.m.SetFloat("_GlossMapScale", p.escala);
                }
                catch { }
            }
            _pinturas.Clear();
            CenaMudou();
        }

        internal static void Restaurar()
        {
            RestaurarPinturas();
            RestaurarLataria();
            if (_go != null) _go.SetActive(false);
            if (_goDentro != null) _goDentro.SetActive(false);
        }

        // ---- sonda do salão ----
        // A sonda de cima do teto faz o piso de chapa, as colunas e os painéis refletirem céu aberto, e o
        // interior inteiro ganha o mesmo brilho azulado. Em deferred a sonda vale por pixel, pela caixa
        // dela: uma segunda sonda com a caixa do salão faz o que está dentro refletir o próprio interior
        // (bancos, teto, janelas claras). A caixa não gira com o ônibus (a Unity só tem caixa alinhada ao
        // mundo), então ela só liga com a câmera dentro: de fora, numa curva, ela pegaria a lataria.
        static GameObject _goDentro;
        static ReflectionProbe _sondaDentro;
        static int _idDentro = -1; static float _proximaDentro;

        static void QuadroDentro()
        {
            var bus = Veiculo.Raiz();
            var cam = Pos.Cam;
            bool ligar = Cfg.SondaSalao.Value && bus != null && cam != null && Veiculo.Dentro(cam.transform.position);
            if (!ligar) { if (_goDentro != null && _goDentro.activeSelf) _goDentro.SetActive(false); return; }
            if (_goDentro == null)
            {
                _goDentro = new GameObject("BetterGraphics_SondaSalao");
                UnityEngine.Object.DontDestroyOnLoad(_goDentro);
                _sondaDentro = _goDentro.AddComponent<ReflectionProbe>();
                _sondaDentro.mode = ReflectionProbeMode.Realtime;
                _sondaDentro.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
                _sondaDentro.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
                _sondaDentro.hdr = true;
                _sondaDentro.boxProjection = true;
                _sondaDentro.importance = 101;
                _sondaDentro.blendDistance = 0.2f;
                _sondaDentro.nearClipPlane = 0.05f;
                // Só o salão importa nesta sonda: além de 40 m entra o céu, e sem sombras (ver a sonda de fora).
                _sondaDentro.farClipPlane = 40f;
                _sondaDentro.shadowDistance = 0f;
                _sondaDentro.clearFlags = ReflectionProbeClearFlags.Skybox;
                _sondaDentro.resolution = 128;
            }
            if (!_goDentro.activeSelf) { _goDentro.SetActive(true); _idDentro = -1; _proximaDentro = 0f; }
            if (_sonda != null && _sondaDentro.cullingMask != _sonda.cullingMask) _sondaDentro.cullingMask = _sonda.cullingMask;

            // Caixa: o salão no espaço do mundo, sem a casca (as laterais encolhidas) e acima do piso.
            var c = Veiculo.Caixa;
            var min = new Vector3(c.min.x + 0.12f, c.min.y + 0.75f, c.min.z + 0.12f);
            var max = new Vector3(c.max.x - 0.12f, c.max.y - 0.15f, c.max.z - 0.12f);
            var wmin = Vector3.positiveInfinity; var wmax = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var p = bus.TransformPoint(new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z));
                wmin = Vector3.Min(wmin, p); wmax = Vector3.Max(wmax, p);
            }
            // Ponto de captura: no corredor, na altura dos olhos, à frente/atrás onde a câmera está.
            var local = bus.InverseTransformPoint(cam.transform.position);
            var origem = bus.TransformPoint(new Vector3(c.center.x, Mathf.Clamp(local.y, min.y + 0.3f, max.y - 0.3f), Mathf.Clamp(local.z, min.z + 0.5f, max.z - 0.5f)));
            _goDentro.transform.position = origem;
            var centro = (wmin + wmax) * 0.5f;
            _sondaDentro.center = centro - origem;
            _sondaDentro.size = wmax - wmin;

            if (Time.unscaledTime < _proximaDentro) return;
            if (_idDentro != -1 && !_sondaDentro.IsFinishedRendering(_idDentro)) return;
            _idDentro = _sondaDentro.RenderProbe();
            // O interior quase não muda: o cubo inteiro a cada 2 s (no máximo) custava ~10 ms por quadro a cada meio segundo.
            _proximaDentro = Time.unscaledTime + Mathf.Max(2f, Cfg.SondaIntervalo.Value);
        }

        internal static int Pinturas => _pinturas.Count;

        // Lataria dos veículos (objetos "_ref"): o jogo usa smoothness 0,6, que numa pintura clara dá
        // um reflexo tão borrado que ela parece fosca. Pintura com verniz é dielétrica e lisa (~0,85):
        // reflete céu, prédios e a rua com nitidez, forte de lado (Fresnel) e fraco de frente.
        static readonly Dictionary<IntPtr, (Material m, float gloss, float metal)> _latarias = new();
        static float _latariaAplicada = -1f;

        internal static void Lataria(Material m)
        {
            if (Cfg.LatariaBrilho.Value <= 0f || _latarias.ContainsKey(m.Pointer) || !m.HasProperty("_Glossiness")) return;
            _latarias[m.Pointer] = (m, m.GetFloat("_Glossiness"), m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f);
            m.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
            if (m.HasProperty("_GlossyReflections")) m.SetFloat("_GlossyReflections", 1f);
            AplicarLataria(m);
        }

        static void ManterLataria()
        {
            float chave = Cfg.LatariaBrilho.Value + Cfg.LatariaVerniz.Value * 10f + (ClearCoat.Active ? 100f + Cfg.LightingPaintBaseSmoothness.Value : 0f);
            if (Mathf.Approximately(_latariaAplicada, chave)) return;
            _latariaAplicada = chave;
            foreach (var (m, g, mt) in _latarias.Values)
                if (m != null && !_borrachas.Contains(m.Pointer)) { if (Cfg.LatariaBrilho.Value > 0f) AplicarLataria(m); else { m.SetFloat("_Glossiness", g); m.SetFloat("_Metallic", mt); } }
        }

        // O Standard não tem verniz (uma camada lisa por cima da cor): com tinta branca o reflexo de 4%
        // some. Um pouco de "metal" faz o papel do verniz: reflexo visível da rua e do céu sem cromar.
        static void AplicarLataria(Material m)
        {
            // Com o verniz de verdade (ClearCoat) a tinta embaixo é dielétrica e meio fosca; o brilho é da película.
            if (ClearCoat.Active)
            {
                m.SetFloat("_Glossiness", Mathf.Clamp01(Cfg.LightingPaintBaseSmoothness.Value));
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                return;
            }
            m.SetFloat("_Glossiness", Cfg.LatariaBrilho.Value);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", Cfg.LatariaVerniz.Value);
        }

        static readonly string[] WheelWords = { "wheel", "roda", "pneu", "tire", "tyre" };

        internal static bool IsWheel(string name)
        {
            foreach (var w in WheelWords) if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Roda: o pneu domina a área e é borracha fosca, sem reflexo nenhum. O Standard metálico sempre reflete ao
        // menos 4%: a roda passa ao Standard (Specular setup) com especular preto e smoothness 0. Os shaders deferred
        // do mod não dão brilho a especular zero (nem o de ângulo rasante).
        static Shader _specSetup;
        static readonly Dictionary<IntPtr, (Material m, Shader shader)> _borrachaShader = new();

        internal static void Borracha(Material m)
        {
            if (!m.HasProperty("_Glossiness") || _borrachas.Contains(m.Pointer)) return;
            if (!_latarias.ContainsKey(m.Pointer))
                _latarias[m.Pointer] = (m, m.GetFloat("_Glossiness"), m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f);
            _borrachas.Add(m.Pointer);
            if (_specSetup == null)
            {
                _specSetup = Shader.Find("Standard (Specular setup)");
                Log.Msg(_specSetup != null ? "Rodas: borracha sem reflexo (Standard Specular)" : "Rodas: Standard (Specular setup) não está no jogo; borracha com o mínimo de 4% de reflexo");
            }
            if (_specSetup != null)
            {
                _borrachaShader[m.Pointer] = (m, m.shader);
                int queue = m.renderQueue;
                m.shader = _specSetup;
                m.renderQueue = queue;
                m.SetColor("_SpecColor", Color.black);
                m.DisableKeyword("_SPECGLOSSMAP");
            }
            else if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Glossiness", 0f);
            m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            m.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
            if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 0f);
            if (m.HasProperty("_GlossyReflections")) m.SetFloat("_GlossyReflections", 0f);
        }

        static readonly HashSet<IntPtr> _borrachas = new();

        static void RestaurarLataria()
        {
            foreach (var (m, sh) in _borrachaShader.Values)
                try { if (m != null && sh != null) { int q = m.renderQueue; m.shader = sh; m.renderQueue = q; m.DisableKeyword("_SPECULARHIGHLIGHTS_OFF"); m.DisableKeyword("_GLOSSYREFLECTIONS_OFF"); } } catch { }
            foreach (var (m, g, mt) in _latarias.Values)
                try { if (m != null) { m.SetFloat("_Glossiness", g); if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", mt); } } catch { }
            _latarias.Clear(); _borrachas.Clear(); _borrachaShader.Clear(); _latariaAplicada = -1f;
        }
    }
}
