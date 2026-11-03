using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterGraphics
{
    // Materiais do mapa. O carregador de mapas do jogo cria cada material como cópia de um modelo
    // "basicwhite" (Standard) com metallic 0.6 e _METALLICGLOSSMAP ligado: quase todo o mapa (paredes,
    // calçadas, prédios) vira semimetal, sem cor difusa própria e refletindo o céu. Por isso o visual
    // "plástico" e acinzentado; uma telha azul vira semimetal que reflete o céu tingido de azul (néon).
    // Modo "fosco": dielétrico (metallic 0) com smoothness baixa, como concreto, reboco, asfalto e pintura de parede.
    // Modo "fisico" (padrão): o mesmo, só nos objetos do mapa (veículos ficam como estão), e o que a textura diz que é
    // metal de verdade (inox, aço, alumínio, galvanizado, cromo) vira metal pleno.
    internal static class Materiais
    {
        sealed class Orig { public Material m; public bool kwMetal, kwSpec; public float metal, gloss, escala; }
        static readonly Dictionary<IntPtr, bool> _vehicleRoot = new();

        // Peça de veículo: tem um corpo físico acima dela (ônibus e tráfego) ou é de um modelo de ônibus (.ini).
        // O cenário estático não tem; testar a raiz inteira pegava o mapa (algum cone ou placa com física).
        internal static bool IsVehicle(Renderer r)
        {
            var root = r.transform.root;
            if (_vehicleRoot.TryGetValue(root.Pointer, out bool v) && v) return true;
            if (root.name.Contains(".ini", StringComparison.OrdinalIgnoreCase)) { _vehicleRoot[root.Pointer] = true; return true; }
            return r.GetComponentInParent<Rigidbody>() != null;
        }

        static readonly string[] MetalWords = { "metal", "inox", "aco", "aço", "steel", "chrome", "cromo", "alumin", "galv" };

        static bool LooksMetal(Material m)
        {
            var t = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            string n = (t != null ? t.name : "") + " " + m.name;
            foreach (var w in MetalWords) if (n.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        static readonly Dictionary<IntPtr, Orig> _orig = new();
        static string _modoAplicado = "";
        static float _brilhoAplicado = -1f, _proxima;

        internal static void CenaMudou() { _proxima = 0f; _vehicleRoot.Clear(); }

        internal static void Manter()
        {
            string modo = Cfg.MapaMateriais.Value;
            if (modo != _modoAplicado || !Mathf.Approximately(_brilhoAplicado, Cfg.MapaBrilho.Value))
            {
                bool primeira = _modoAplicado.Length == 0;
                Restaurar();
                _modoAplicado = modo; _brilhoAplicado = Cfg.MapaBrilho.Value; _proxima = 0f;
                if (!primeira) Varredura.Reiniciar(); // os materiais voltam pela varredura com o modo novo
            }
            if (modo == "original" || _novos == 0 || Time.unscaledTime < _proxima) return;
            _proxima = Time.unscaledTime + 15f;
            Log.Msg($"Materiais do mapa ({modo}): {_novos} novos, {_orig.Count} no total");
            _novos = 0;
        }

        static int _novos;

        // Chamado pela Varredura para cada material de cada renderer novo (ela já percorre a cena aos poucos; uma segunda
        // varredura inteira aqui, a cada 15 s, travava ~60 ms).
        // Folhas do Tree Creator sem o mapa de translucidez (o Tree Creator gera junto com a árvore; mapas exportados vêm
        // sem): o shader assume translucidez total e a copa inteira acende como folha contra o sol (verde-menta, o dobro
        // do brilho das copas nas fotos). A cor escurece junto com a translucidez.
        static readonly Dictionary<IntPtr, (Material m, Color cor)> _folhas = new();

        static void Folha(Material m)
        {
            if (_folhas.ContainsKey(m.Pointer) || !m.HasProperty("_TranslucencyMap") || m.GetTexture("_TranslucencyMap") != null) return;
            var cor = m.GetColor("_Color");
            _folhas[m.Pointer] = (m, cor);
            float k = Mathf.Clamp01(Cfg.FolhasSemMapa.Value);
            m.SetColor("_Color", new Color(cor.r * k, cor.g * k, cor.b * k, cor.a));
        }

        internal static void Considerar(Renderer r, Material m, string shader)
        {
            if (shader == "Nature/Tree Creator Leaves" || shader == "Nature/Tree Creator Leaves Fast") Folha(m);
            string modo = Cfg.MapaMateriais.Value;
            if (modo == "original" || _orig.ContainsKey(m.Pointer)) return;
            if (shader != "Standard" || !m.name.StartsWith("basicwhite", StringComparison.Ordinal)) return;
            if (modo == "fisico" && IsVehicle(r)) return;
            _orig[m.Pointer] = new Orig
            {
                m = m, kwMetal = m.IsKeywordEnabled("_METALLICGLOSSMAP"), kwSpec = m.IsKeywordEnabled("_SPECGLOSSMAP"),
                metal = m.GetFloat("_Metallic"), gloss = m.GetFloat("_Glossiness"), escala = m.GetFloat("_GlossMapScale"),
            };
            Aplicar(m, modo);
            _novos++;
        }

        static void Aplicar(Material m, string modo)
        {
            if (modo == "fisico")
            {
                m.DisableKeyword("_METALLICGLOSSMAP");
                m.DisableKeyword("_SPECGLOSSMAP");
                bool metal = LooksMetal(m);
                m.SetFloat("_Metallic", metal ? 1f : 0f);
                m.SetFloat("_Glossiness", metal ? 0.6f : Cfg.MapaBrilho.Value);
            }
            else if (modo == "fosco")
            {
                m.DisableKeyword("_METALLICGLOSSMAP");
                m.DisableKeyword("_SPECGLOSSMAP");
                m.SetFloat("_Metallic", 0f);
                m.SetFloat("_Glossiness", Cfg.MapaBrilho.Value);
            }
            else if (modo == "semmetal") // mantém a smoothness do mapa de textura, só zera o metal
            {
                m.SetFloat("_Metallic", 0f);
                m.SetFloat("_GlossMapScale", Mathf.Min(m.GetFloat("_GlossMapScale"), Cfg.MapaBrilho.Value));
            }
        }

        internal static void Restaurar()
        {
            foreach (var o in _orig.Values)
            {
                try
                {
                    if (o.m == null) continue;
                    if (o.kwMetal) o.m.EnableKeyword("_METALLICGLOSSMAP");
                    if (o.kwSpec) o.m.EnableKeyword("_SPECGLOSSMAP");
                    o.m.SetFloat("_Metallic", o.metal); o.m.SetFloat("_Glossiness", o.gloss); o.m.SetFloat("_GlossMapScale", o.escala);
                }
                catch { }
            }
            _orig.Clear();
            foreach (var (m, cor) in _folhas.Values) try { if (m != null) m.SetColor("_Color", cor); } catch { }
            _folhas.Clear();
            _modoAplicado = "";
        }

        // Para o Lab: todas as propriedades de até n materiais cujo nome contém o texto.
        internal static string Inspecionar(string texto, int n)
        {
            var sb = new System.Text.StringBuilder();
            var vistos = new HashSet<IntPtr>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !m.name.Contains(texto, StringComparison.OrdinalIgnoreCase) || !vistos.Add(m.Pointer)) continue;
                    sb.Append(Descrever(m, r));
                    if (vistos.Count >= n) return sb.ToString();
                }
            return sb.Length > 0 ? sb.ToString() : "nenhum material com " + texto;
        }

        internal static string Descrever(Material m, Renderer r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"mat {m.name} [{(m.shader != null ? m.shader.name : "-")}] fila {m.renderQueue} em {r.transform.root.name}/{r.name} kw={string.Join(",", m.shaderKeywords)} | ");
            var sh = m.shader;
            for (int i = 0; sh != null && i < sh.GetPropertyCount(); i++)
            {
                string p = sh.GetPropertyName(i);
                try
                {
                    switch (sh.GetPropertyType(i))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color: sb.Append($"{p}={m.GetColor(p)} "); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector: sb.Append($"{p}={m.GetVector(p)} "); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            var t = m.GetTexture(p);
                            if (t != null) sb.Append($"{p}={t.name}({t.width}x{t.height}) ");
                            break;
                        default: sb.Append($"{p}={m.GetFloat(p):0.###} "); break;
                    }
                }
                catch { }
            }
            return sb.AppendLine().ToString();
        }
    }
}
