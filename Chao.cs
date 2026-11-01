using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterGraphics
{
    // Asfalto e calçadas. Os mapas usam materiais "wet" com mapa de poças (Wet_Asphalt_*: smoothness no
    // alfa, AO, altura), mas com _GLOSSYREFLECTIONS_OFF: nunca refletem nada, nem na chuva. Aqui o
    // reflexo é ligado e a smoothness acompanha a umidade do Enviro: seco fica fosco (só um brilho
    // leve), molhado as poças aparecem e refletem céu, prédios e faróis (sonda + reflexos em tela).
    //
    // O mapa de poças original tem bordas salpicadas de ruído fino, que repete a cada tile da textura:
    // molhado, o chão virava um granulado de pontinhos brilhantes. Ele é refeito uma vez por textura:
    // reduzido (borra o salpico) e remapeado para base úmida lisa + poças espelhadas. Mapa uniforme
    // (calçada "flooded") vira só filme d'água, não espelho. Com a umidade o relevo também baixa: a água
    // preenche as rugosidades e o reflexo fica contínuo.
    internal static class Chao
    {
        sealed class Orig { public Material m; public bool kwOff; public float refl, escala, gloss, relevo, parallax; public Color cor; public Texture mapa; }
        static readonly Dictionary<IntPtr, Orig> _orig = new();
        static readonly Dictionary<IntPtr, Texture2D> _pocas = new();
        // Para os respingos: quanto de poça (0 a 1) em cada ponto do mapa, por textura original.
        static readonly Dictionary<IntPtr, float[]> _pocaCpu = new();
        const int NP = 512;
        static float _molhado = -1f, _chave = -1f;

        internal static void CenaMudou() { }

        // Material de chão: nome "wet*" ou mapa de poças Wet_Asphalt (nome já lido pela Varredura).
        internal static bool EhChao(Material m, string nome)
        {
            if (nome.StartsWith("wet", StringComparison.OrdinalIgnoreCase)) return true;
            var mg = m.HasProperty("_MetallicGlossMap") ? m.GetTexture("_MetallicGlossMap") : null;
            return mg != null && mg.name.Contains("Wet_Asphalt", StringComparison.OrdinalIgnoreCase);
        }

        // Chamado pela Varredura para cada material de chão encontrado.
        internal static void Registrar(Material m)
        {
            if (!Cfg.ChaoAtivo.Value || _orig.ContainsKey(m.Pointer)) return;
            var o = _orig[m.Pointer] = new Orig
            {
                m = m, kwOff = m.IsKeywordEnabled("_GLOSSYREFLECTIONS_OFF"),
                refl = m.HasProperty("_GlossyReflections") ? m.GetFloat("_GlossyReflections") : 0f,
                escala = m.GetFloat("_GlossMapScale"), gloss = m.GetFloat("_Glossiness"), cor = m.GetColor("_Color"),
                relevo = m.HasProperty("_BumpScale") ? m.GetFloat("_BumpScale") : 1f,
                parallax = m.HasProperty("_Parallax") ? m.GetFloat("_Parallax") : 0f,
                mapa = m.HasProperty("_MetallicGlossMap") ? m.GetTexture("_MetallicGlossMap") : null,
            };
            m.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
            if (m.HasProperty("_GlossyReflections")) m.SetFloat("_GlossyReflections", 1f);
            if (Cfg.ChaoPocas.Value && o.mapa != null && m.IsKeywordEnabled("_METALLICGLOSSMAP"))
            {
                var nova = Pocas(o.mapa);
                if (nova != null) m.SetTexture("_MetallicGlossMap", nova);
            }
            _molhado = -1f; // força aplicar a umidade atual
        }

        // Mapa de poças refeito (uma vez por textura original, compartilhado entre materiais).
        static Texture2D Pocas(Texture src)
        {
            if (_pocas.TryGetValue(src.Pointer, out var t) && t != null) return t;
            const int N = NP;
            var rt = RenderTexture.GetTemporary(N, N, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(src, rt); // 2048 -> 512: média de 4x4, some o salpico fino
            var ant = RenderTexture.active; RenderTexture.active = rt;
            var ler = new Texture2D(N, N, TextureFormat.RGBA32, false, true);
            ler.ReadPixels(new Rect(0, 0, N, N), 0, 0); ler.Apply();
            RenderTexture.active = ant; RenderTexture.ReleaseTemporary(rt);
            var px = ler.GetPixels32();
            UnityEngine.Object.Destroy(ler);

            var a = new float[px.Length];
            double soma = 0, soma2 = 0;
            for (int i = 0; i < px.Length; i++) { a[i] = px[i].a / 255f; soma += a[i]; soma2 += a[i] * a[i]; }
            double media = soma / a.Length, desvio = Math.Sqrt(Math.Max(0, soma2 / a.Length - media * media));
            // Mais um borrão leve (caixa 5x5, repetindo nas bordas: a textura é ladrilhada).
            var b = new float[a.Length];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float s = 0;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            s += a[((y + dy + N) % N) * N + (x + dx + N) % N];
                    b[y * N + x] = s / 25f;
                }
            float baseUmida = Cfg.ChaoBaseUmida.Value;
            var saida = new Color32[a.Length];
            var cpu = new float[a.Length];
            for (int i = 0; i < b.Length; i++)
            {
                float lisa;
                if (desvio < 0.03) lisa = 0.85f; // mapa uniforme: filme d'água
                else
                {
                    float poca = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.62f, 0.9f, b[i]));
                    cpu[i] = poca;
                    lisa = Mathf.Lerp(baseUmida, 0.97f, poca);
                }
                saida[i] = new Color32(0, 0, 0, (byte)Mathf.RoundToInt(lisa * 255f));
            }
            t = new Texture2D(N, N, TextureFormat.RGBA32, true, true) { name = "BetterGraphics_Pocas_" + src.name, wrapMode = TextureWrapMode.Repeat };
            t.SetPixels32(saida);
            t.Apply(true, false);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _pocaCpu[src.Pointer] = cpu;
            Log.Msg($"Poças: {src.name} refeito (média {media:0.00}, desvio {desvio:0.00}{(desvio < 0.03 ? ", uniforme" : "")})");
            return _pocas[src.Pointer] = t;
        }

        internal static void Manter()
        {
            if (!Cfg.ChaoAtivo.Value) { if (_orig.Count > 0) { Restaurar(); Varredura.Reiniciar(); } return; }

            var sky = Luz.Sky;
            float w = sky != null ? Mathf.Clamp01(sky.Weather.curWetness) : 0f;
            float chave = Cfg.ChaoSeco.Value + Cfg.ChaoMolhado.Value * 10f + Cfg.ChaoEscurecer.Value * 100f;
            if (Mathf.Abs(w - _molhado) < 0.01f && Mathf.Approximately(chave, _chave)) return;
            _molhado = w; _chave = chave;
            // A curva sobe rápido no começo: chão escurece e brilha logo que começa a chover.
            float k = Mathf.Sqrt(w);
            float escala = Mathf.Lerp(Cfg.ChaoSeco.Value, Cfg.ChaoMolhado.Value, k);
            // Água nos poros escurece concreto e asfalto (bem mais que só o brilho).
            float esc = Mathf.Lerp(1f, Cfg.ChaoEscurecer.Value, k);
            foreach (var o in _orig.Values)
            {
                if (o.m == null) continue;
                o.m.SetFloat("_GlossMapScale", escala);
                o.m.SetColor("_Color", new Color(o.cor.r * esc, o.cor.g * esc, o.cor.b * esc, o.cor.a));
                if (o.m.HasProperty("_BumpScale")) o.m.SetFloat("_BumpScale", o.relevo * Mathf.Lerp(1f, 0.35f, k));
                if (o.m.HasProperty("_Parallax")) o.m.SetFloat("_Parallax", o.parallax * Mathf.Lerp(1f, 0.3f, k));
            }
        }

        internal static void Restaurar()
        {
            foreach (var o in _orig.Values)
            {
                try
                {
                    if (o.m == null) continue;
                    if (o.kwOff) o.m.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
                    if (o.m.HasProperty("_GlossyReflections")) o.m.SetFloat("_GlossyReflections", o.refl);
                    o.m.SetFloat("_GlossMapScale", o.escala);
                    o.m.SetColor("_Color", o.cor);
                    if (o.m.HasProperty("_BumpScale")) o.m.SetFloat("_BumpScale", o.relevo);
                    if (o.m.HasProperty("_Parallax")) o.m.SetFloat("_Parallax", o.parallax);
                    if (o.mapa != null) o.m.SetTexture("_MetallicGlossMap", o.mapa);
                }
                catch { }
            }
            _orig.Clear(); _molhado = -1f; _chave = -1f;
        }

        internal static float Umidade => Mathf.Max(0f, _molhado);

        // Quanto de poça (0 a 1) no ponto de UV de um renderer de chão; -1 se não é chão conhecido.
        internal static float Poca(Renderer r, Vector2 uv)
        {
            if (r == null) return -1f;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || !_orig.TryGetValue(m.Pointer, out var o) || o.mapa == null) continue;
                if (!_pocaCpu.TryGetValue(o.mapa.Pointer, out var cpu)) return 0f;
                var st = m.GetTextureScale("_MainTex"); var off = m.GetTextureOffset("_MainTex");
                float u = uv.x * st.x + off.x, v = uv.y * st.y + off.y;
                u -= Mathf.Floor(u); v -= Mathf.Floor(v);
                return cpu[Mathf.Min(NP - 1, (int)(v * NP)) * NP + Mathf.Min(NP - 1, (int)(u * NP))];
            }
            return -1f;
        }

        internal static string Estado() => $"chão: {_orig.Count} materiais, umidade {_molhado:0.00}";
    }
}
