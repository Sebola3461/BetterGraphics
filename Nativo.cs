using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace BetterGraphics
{
    // Desliga a pilha de efeitos do próprio jogo para o mod ser a única:
    //  - volumes PPv2 do jogo (perfis efeitoPadraoPC_* da câmera e os "POST PROCESS BLOOM/AO/SSR/
    //    MOTION BLUR" que as opções gráficas e a chuva ligam);
    //  - o trocador de perfis (TrocaPostProcessingRuntime) e o que liga efeito só na chuva;
    //  - filtros de imagem na câmera (Beautify, SleekRender, Grayscale, LUT, efeitos legados...).
    // Roda a cada segundo porque o jogo religa coisas ao mudar opção, clima ou câmera.
    internal static class Nativo
    {
        static readonly Dictionary<IntPtr, Behaviour> _desligados = new();
        static float _proximaBusca; static int _rodada;
        internal static void CenaMudou() { _proximaBusca = 0f; }
        static readonly string[] Filtros =
        {
            "Beautify", "SleekRender", "Grayscale", "SimpleLUT", "LUT", "XFrame", "ImageEffect", "Bloom", "Vignett",
            "ColorCorrection", "Tonemap", "Antialias", "Blur", "SSAO", "ScreenSpaceAmbient", "Contrast", "Sepia",
            "NoiseAndGrain", "Fisheye", "Twirl", "Vortex", "EdgeDetect", "CreaseShading", "SunShafts", "GlobalFog",
        };

        internal static void Manter()
        {
            if (!Cfg.DesligarNativo.Value) { if (_desligados.Count > 0) Restaurar(); return; }

            // Busca na cena toda só a cada 10 s (custava ~13 ms todo segundo); no meio, só reaplica.
            // Uma busca por vez, alternando (cada uma percorre a cena inteira).
            if (Time.unscaledTime >= _proximaBusca)
            {
                _proximaBusca = Time.unscaledTime + (_rodada == 2 ? 8f : 1f);
                switch (_rodada)
                {
                    case 0:
                        foreach (var v in UnityEngine.Object.FindObjectsOfType<PostProcessVolume>())
                            if (v.enabled && v != Pos.Volume && v != Pos.VolumeJogo) Desligar(v);
                        break;
                    case 1: foreach (var t in UnityEngine.Object.FindObjectsOfType<Il2Cpp.TrocaPostProcessingRuntime>()) if (t.enabled) Desligar(t); break;
                    default: foreach (var t in UnityEngine.Object.FindObjectsOfType<Il2Cpp.AtivaPostProcessSoComChuva>()) if (t.enabled) Desligar(t); break;
                }
                _rodada = (_rodada + 1) % 3;
            }
            else
                foreach (var b in _desligados.Values)
                    if (b != null && b.enabled && b != Pos.VolumeJogo) b.enabled = false;

            // Filtros de imagem ficam nas câmeras.
            foreach (var cam in Camera.allCameras)
                foreach (var b in cam.GetComponents<Behaviour>())
                {
                    if (b == null || !b.enabled) continue;
                    string n = b.GetIl2CppType().Name;
                    foreach (var f in Filtros)
                        if (n.Contains(f, StringComparison.Ordinal)) { Desligar(b); break; }
                }
        }

        static void Desligar(Behaviour b)
        {
            if (!_desligados.ContainsKey(b.Pointer))
                Log.Msg($"Nativo desligado: {b.GetIl2CppType().Name} em {b.gameObject.name}");
            _desligados[b.Pointer] = b;
            b.enabled = false;
        }

        internal static void Restaurar()
        {
            foreach (var b in _desligados.Values)
                try { if (b != null) b.enabled = true; } catch { }
            _desligados.Clear();
            _proximaBusca = 0f;
        }

        internal static IEnumerable<Behaviour> Desligados => _desligados.Values;
    }
}
