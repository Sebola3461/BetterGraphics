using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace BetterGraphics
{
    // Ctrl+Alt+Shift+F9 / "diag" no Lab: grava UserData/BetterGraphics_diagnostico.txt com o estado da renderização.
    internal static class Diag
    {
        internal static void Gravar()
        {
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine($"== BetterGraphics diagnóstico {DateTime.Now:yyyy-MM-dd HH:mm:ss} | mod {(Cfg.Ativo.Value ? "ligado" : "desligado")}");
                int q = QualitySettings.GetQualityLevel();
                sb.AppendLine($"Qualidade: {q} '{QualitySettings.names[q]}' sombras {QualitySettings.shadows} {QualitySettings.shadowResolution} dist {QualitySettings.shadowDistance} casc {QualitySettings.shadowCascades} | luzes/pixel {QualitySettings.pixelLightCount} | AA {QualitySettings.antiAliasing} aniso {QualitySettings.anisotropicFiltering} lodBias {QualitySettings.lodBias} | vsync {QualitySettings.vSyncCount}");
                sb.AppendLine($"Ambiente: {RenderSettings.ambientMode} intens {RenderSettings.ambientIntensity} céu {RenderSettings.ambientSkyColor} horiz {RenderSettings.ambientEquatorColor} chão {RenderSettings.ambientGroundColor} | reflexo {RenderSettings.defaultReflectionMode} {RenderSettings.reflectionIntensity} | névoa {RenderSettings.fog} | skybox {Nome(RenderSettings.skybox)} | sol {Nome(RenderSettings.sun)}");
                var sky = Luz.Sky;
                if (sky != null)
                    sb.AppendLine($"Enviro: {sky.GameTime.Hours}:{sky.GameTime.Minutes:00} clima {sky.Weather.currentActiveWeatherPreset?.Name} molhado {sky.Weather.curWetness:0.00} interior {sky.interiorMode} | volumétrica {sky.volumeLighting} scatter {sky.volumeLightSettings.ScatteringCoef} | shafts {sky.LightShafts.sunLightShafts}");

                sb.AppendLine("-- Câmeras");
                foreach (var c in Camera.allCameras)
                {
                    sb.AppendLine($"{Caminho(c.transform)} | main {c == Camera.main} | {c.renderingPath}->{c.actualRenderingPath} HDR {c.allowHDR} MSAA {c.allowMSAA} | mask {c.cullingMask} | alvo {Nome(c.targetTexture)} | far {c.farClipPlane}");
                    sb.AppendLine("   " + Componentes(c.gameObject));
                    var l = c.GetComponent<PostProcessLayer>();
                    if (l != null) sb.AppendLine($"   PostProcessLayer: volumeLayer {l.volumeLayer.value} AA {l.antialiasingMode} fog {l.fog.enabled}");
                }

                sb.AppendLine("-- Volumes PPv2");
                foreach (var v in UnityEngine.Object.FindObjectsOfType<PostProcessVolume>(true))
                {
                    sb.AppendLine($"{Caminho(v.transform)} | ativo {v.isActiveAndEnabled} prio {v.priority} | perfil {Nome(v.sharedProfile)}");
                    if (v.sharedProfile != null)
                        foreach (var s in v.sharedProfile.settings)
                            if (s.active) sb.AppendLine($"   {s.GetIl2CppType().Name}");
                }
                sb.AppendLine("-- Nativo desligado pelo mod");
                foreach (var b in Nativo.Desligados) if (b != null) sb.AppendLine($"   {b.GetIl2CppType().Name} em {Caminho(b.transform)}");

                sb.AppendLine("-- Luzes com sombra / direcionais");
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                    if (l.type == LightType.Directional || l.shadows != LightShadows.None)
                        sb.AppendLine($"{Caminho(l.transform)} | {l.type} intens {l.intensity:0.###} cor {l.color} sombras {l.shadows} força {l.shadowStrength:0.##} | ativa {l.isActiveAndEnabled}");
                int pontuais = 0; foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>()) if (l.type != LightType.Directional && l.isActiveAndEnabled) pontuais++;
                sb.AppendLine($"luzes pontuais/spot ativas: {pontuais}");

                sb.AppendLine("-- Sondas de reflexo");
                foreach (var p in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>())
                    sb.AppendLine($"{Caminho(p.transform)} | {p.mode} {p.refreshMode} res {p.resolution} imp {p.importance} tam {p.size} intens {p.intensity} | pos {p.transform.position} | ativa {p.isActiveAndEnabled}");
                sb.AppendLine($"pinturas com reflexo religado: {Reflexos.Pinturas}");

                sb.AppendLine("-- Vidros");
                sb.Append(Vidros.Estado());

                Materiais(sb);

                string arq = System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "BetterGraphics_diagnostico.txt");
                System.IO.File.WriteAllText(arq, sb.ToString());
                Log.Msg("Diagnóstico gravado em " + arq);
            }
            catch (Exception e) { Log.Aviso("Diagnóstico: " + e); }
        }

        static void Materiais(StringBuilder sb)
        {
            var bus = Veiculo.Raiz();
            if (bus != null)
            {
                sb.AppendLine($"-- Materiais do ônibus ({bus.name}) caixa {Veiculo.Caixa.center} {Veiculo.Caixa.size}");
                var vistos = new HashSet<string>();
                foreach (var r in bus.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                        if (m != null && vistos.Add(m.name) && vistos.Count <= 80)
                            sb.AppendLine($"{m.name} | {Nome(m.shader)} | {Props(m)} | em {r.name}");
            }

            // Shaders da cena e distribuição de metallic/smoothness nos Standard.
            var cont = new Dictionary<string, int>();
            var ex = new Dictionary<string, Material>();
            var standard = new HashSet<IntPtr>();
            int[] met = new int[5], gl = new int[5];
            var metAlto = new List<string>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    string s = m.shader.name;
                    cont[s] = cont.TryGetValue(s, out int n) ? n + 1 : 1;
                    if (!ex.ContainsKey(s)) ex[s] = m;
                    if (s == "Standard" && standard.Add(m.Pointer))
                    {
                        float mt = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
                        float g = m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : 0f;
                        met[Mathf.Clamp((int)(mt * 5f), 0, 4)]++; gl[Mathf.Clamp((int)(g * 5f), 0, 4)]++;
                        if (mt >= 0.4f && metAlto.Count < 40) metAlto.Add($"{m.name}(m{mt:0.##} g{g:0.##}{(m.IsKeywordEnabled("_METALLICGLOSSMAP") ? " mapa" : "")})");
                    }
                }
            var lista = new List<KeyValuePair<string, int>>(cont);
            lista.Sort((a, b) => b.Value.CompareTo(a.Value));
            sb.AppendLine("-- Shaders mais usados");
            for (int i = 0; i < lista.Count && i < 25; i++)
                sb.AppendLine($"{lista[i].Value,6}  {lista[i].Key} | ex. {ex[lista[i].Key].name}: {Props(ex[lista[i].Key])}");
            sb.AppendLine($"-- Standard: {standard.Count} materiais | metallic 0-.2/.2-.4/.4-.6/.6-.8/.8-1: {string.Join("/", met)} | smoothness: {string.Join("/", gl)}");
            sb.AppendLine("   metallic >= 0.4: " + string.Join(", ", metAlto));
        }

        static string Props(Material m)
        {
            var sb = new StringBuilder();
            foreach (var p in new[] { "_Glossiness", "_Metallic", "_GlossMapScale", "_Shininess" })
                if (m.HasProperty(p)) sb.Append(p).Append('=').Append(m.GetFloat(p).ToString("0.##")).Append(' ');
            foreach (var p in new[] { "_MetallicGlossMap", "_BumpMap", "_OcclusionMap", "_Cube" })
                if (m.HasProperty(p) && m.GetTexture(p) != null) sb.Append(p).Append(' ');
            if (m.HasProperty("_Color")) sb.Append("cor=").Append(m.color.ToString()).Append(' ');
            var kw = m.shaderKeywords;
            if (kw != null && kw.Length > 0) sb.Append("kw=").Append(string.Join(",", kw));
            return sb.ToString();
        }

        static string Nome(UnityEngine.Object o) => o == null ? "-" : o.name;

        static string Caminho(Transform t)
        {
            string s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        static string Componentes(GameObject go)
        {
            var sb = new StringBuilder();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var b = c.TryCast<Behaviour>();
                sb.Append(c.GetIl2CppType().Name).Append(b != null && !b.enabled ? "(off) " : " ");
            }
            return sb.ToString();
        }
    }
}
