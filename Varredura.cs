using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace BetterGraphics
{
    // Varredura única e incremental dos renderers da cena, compartilhada por pinturas, vidros e chão.
    // Antes cada módulo percorria a cena inteira a cada 10-15 s lendo a lista de materiais de milhares
    // de objetos (até 1,3 s num quadro só: o "stutter" periódico). Agora:
    //  - cada renderer é visto uma vez na vida (novos entram pela lista atualizada a cada 10 s);
    //  - o tipo de cada material (vidro, pintura, chão) é decidido uma vez e guardado;
    //  - o trabalho é dividido entre quadros, com orçamento de tempo por quadro;
    //  - a versão inativa das janelas (seca/molhada) entra quando o veículo dela aparece.
    internal static class Varredura
    {
        enum Tipo : byte { Nada, Para, Janela, Pintura, PinturaMapa, Chao, Nosso }

        static readonly Dictionary<IntPtr, Tipo> _tipo = new();
        static readonly HashSet<IntPtr> _vistos = new();
        static readonly HashSet<IntPtr> _raizesComVidro = new();
        static readonly Queue<Renderer> _fila = new();
        static readonly Stopwatch _sw = new();
        static float _proximaLista;

        internal static int Pendentes => _fila.Count;
        internal static int Vistos => _vistos.Count;

        // Recomeça do zero (troca de cena, mod religado, ajuste que exige reaplicar).
        internal static void Reiniciar()
        {
            _tipo.Clear(); _vistos.Clear(); _raizesComVidro.Clear(); _fila.Clear(); _proximaLista = 0f;
        }

        internal static void Quadro()
        {
            if (Time.unscaledTime >= _proximaLista)
            {
                _proximaLista = Time.unscaledTime + 10f;
                // Lê os ponteiros crus do array do IL2CPP: percorrer com foreach cria um objeto de interop
                // por elemento (~6000), e isso sozinho custava 15-25 ms. Só os novos viram Renderer.
                var arr = UnityEngine.Object.FindObjectsOfType<Renderer>();
                IntPtr dados = arr.Pointer + 0x20; // cabeçalho do array IL2CPP (classe, monitor, limites, tamanho)
                int n = arr.Length;
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = System.Runtime.InteropServices.Marshal.ReadIntPtr(dados, i * IntPtr.Size);
                    if (e != IntPtr.Zero && _vistos.Add(e)) _fila.Enqueue(new Renderer(e));
                }
            }
            if (_fila.Count == 0) return;
            _sw.Restart();
            double orcamento = Cfg.VarreduraMs.Value;
            while (_fila.Count > 0 && _sw.Elapsed.TotalMilliseconds < orcamento)
            {
                var r = _fila.Dequeue();
                try { if (r != null) Processar(r); }
                catch (Exception e) { Log.Erro("Varredura", e); }
            }
        }

        static void Processar(Renderer r)
        {
            var mats = r.sharedMaterials;
            if (mats.Length == 0) return;
            var bus = Veiculo.Raiz();
            bool doOnibus = bus != null && r.transform.root == bus;
            // "_ref" no nome do objeto é a marca de lataria refletiva dos veículos (pintura com verniz).
            bool lataria = r.name.Contains("_ref", StringComparison.Ordinal);
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (lataria && m.shader != null && m.shader.name == "Standard") Reflexos.Lataria(m);
                switch (Classificar(m))
                {
                    case Tipo.Pintura: if (doOnibus) Reflexos.Pintura(m); break;
                    case Tipo.PinturaMapa: Reflexos.Pintura(m); break;
                    case Tipo.Chao: Chao.Registrar(m); break;
                    case Tipo.Para:
                    case Tipo.Janela:
                        if (mats.Length == 1 && Vidros.Registrar(r, m, _tipo[m.Pointer] == Tipo.Janela))
                            ExpandirRaiz(r.transform.root);
                        break;
                }
            }
        }

        // Veículo com vidro: entram também os renderers inativos (versões seca/molhada das janelas).
        static void ExpandirRaiz(Transform raiz)
        {
            if (!_raizesComVidro.Add(raiz.Pointer)) return;
            var arr = raiz.GetComponentsInChildren<Renderer>(true);
            IntPtr dados = arr.Pointer + 0x20;
            for (int i = 0; i < arr.Length; i++)
            {
                IntPtr e = System.Runtime.InteropServices.Marshal.ReadIntPtr(dados, i * IntPtr.Size);
                if (e != IntPtr.Zero && _vistos.Add(e)) _fila.Enqueue(new Renderer(e));
            }
        }

        static Tipo Classificar(Material m)
        {
            if (_tipo.TryGetValue(m.Pointer, out var t)) return t;
            t = Tipo.Nada;
            string nome = m.name;
            var shader = m.shader;
            string sh = shader != null ? shader.name : "";
            if (nome.StartsWith("BetterGraphics_", StringComparison.Ordinal)) t = Tipo.Nosso;
            else if (sh == "Glass Reflective") t = Tipo.Para;
            else if (sh.StartsWith("OneClick Glass", StringComparison.Ordinal)) t = Tipo.Janela;
            else if (sh == "Standard")
            {
                if (Chao.EhChao(m, nome)) t = Tipo.Chao;
                else if (m.IsKeywordEnabled("_GLOSSYREFLECTIONS_OFF"))
                    t = nome.StartsWith("matvazio", StringComparison.Ordinal) ? Tipo.PinturaMapa : Tipo.Pintura;
            }
            _tipo[m.Pointer] = t;
            return t;
        }
    }
}
