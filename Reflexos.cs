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
                _sonda.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
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
                _sonda.shadowDistance = 60f;
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
            if (_sonda == null || !_go.activeSelf) return;
            var cam = Pos.Cam;
            if (Veiculo.Raiz() != null) _go.transform.position = Veiculo.AcimaDoTeto();
            else if (cam != null) _go.transform.position = cam.transform.position + Vector3.up * 3f;
            else return;
            if (Time.unscaledTime < _proxima) return;
            if (_id != -1 && !_sonda.IsFinishedRendering(_id)) return;
            _id = _sonda.RenderProbe();
            _proxima = Time.unscaledTime + Mathf.Max(0f, Cfg.SondaIntervalo.Value);
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
            float chave = Cfg.LatariaBrilho.Value + Cfg.LatariaVerniz.Value * 10f;
            if (Mathf.Approximately(_latariaAplicada, chave)) return;
            _latariaAplicada = chave;
            foreach (var (m, g, mt) in _latarias.Values)
                if (m != null) { if (Cfg.LatariaBrilho.Value > 0f) AplicarLataria(m); else { m.SetFloat("_Glossiness", g); m.SetFloat("_Metallic", mt); } }
        }

        // O Standard não tem verniz (uma camada lisa por cima da cor): com tinta branca o reflexo de 4%
        // some. Um pouco de "metal" faz o papel do verniz: reflexo visível da rua e do céu sem cromar.
        static void AplicarLataria(Material m)
        {
            m.SetFloat("_Glossiness", Cfg.LatariaBrilho.Value);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", Cfg.LatariaVerniz.Value);
        }

        static void RestaurarLataria()
        {
            foreach (var (m, g, mt) in _latarias.Values)
                try { if (m != null) { m.SetFloat("_Glossiness", g); if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", mt); } } catch { }
            _latarias.Clear(); _latariaAplicada = -1f;
        }
    }
}
