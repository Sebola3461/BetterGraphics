using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterGraphics
{
    // Luz do mundo, aplicada logo depois de cada Update/LateUpdate do Enviro (que reescreve tudo):
    //  - Ambiente: o Enviro usa modo Flat (uma cor vinda de todas as direções), que ilumina o lado na
    //    sombra igual ao lado no sol. No modo "céu" a luz ambiente é calculada do próprio céu do Enviro
    //    (DynamicGI): azulada de cima, mais fraca dos lados, escura de baixo. No modo "três faixas" a
    //    cor do Enviro vira céu/horizonte/chão. Os dois respeitam as zonas de interior do Enviro (túneis).
    //  - Sol: multiplicador e sombra física (o preenchimento da sombra fica só por conta do céu).
    //  - Atmosfera: luz volumétrica e raios do sol do Enviro.
    internal static class Luz
    {
        static Il2Cpp.EnviroSky _sky;
        static Color _baseFlat, _escrito; static bool _temBase;
        static Light _sol; static float _solBase, _solEscrito = float.NaN, _sombraBase, _sombraEscrita = float.NaN;
        static float _proximoGI, _proximaBusca;
        static bool _qualSalva; static float _oDist; static int _oCasc, _oLuzes; static ShadowResolution _oRes; static ShadowQuality _oSomb; static ShadowProjection _oProj;
        static bool _atmSalva; static bool _oVol, _oDirVol, _oShafts; static float _oScatter, _oAniso, _oShaftInt;
        static bool _reflSalvo; static float _oRefl;
        static bool _fogSalvo; static float _oFogDist, _oFogAlt;

        internal static Il2Cpp.EnviroSky Sky
        {
            get
            {
                // Busca no máximo a cada 2 s (nos menus não há Enviro; FindObjectOfType todo frame pesa).
                if (_sky == null && Time.unscaledTime >= _proximaBusca)
                {
                    _proximaBusca = Time.unscaledTime + 2f;
                    _sky = UnityEngine.Object.FindObjectOfType<Il2Cpp.EnviroSky>();
                }
                return _sky;
            }
        }

        internal static void CenaMudou()
        {
            _sky = null; _proximaBusca = 0f; _temBase = false; _sol = null; _solEscrito = float.NaN; _sombraEscrita = float.NaN; _atmSalva = false; _proximoGI = 0f;
            _neblinaOrig.Clear(); _neblinaAplicada = -1f; _fogSalvo = false; // presets e névoa são da cena anterior
        }

        // A cada segundo: qualidade de sombra (o menu de opções do jogo mexe nela).
        internal static void Manter()
        {
            if (!_qualSalva)
            {
                _qualSalva = true;
                _oDist = QualitySettings.shadowDistance; _oCasc = QualitySettings.shadowCascades; _oRes = QualitySettings.shadowResolution;
                _oSomb = QualitySettings.shadows; _oProj = QualitySettings.shadowProjection; _oLuzes = QualitySettings.pixelLightCount;
            }
            int casc = Cfg.SombraCascatas.Value >= 4 ? 4 : Cfg.SombraCascatas.Value >= 2 ? 2 : 1;
            if (QualitySettings.shadows != ShadowQuality.All) QualitySettings.shadows = ShadowQuality.All;
            if (!Mathf.Approximately(QualitySettings.shadowDistance, Cfg.SombraDistancia.Value)) QualitySettings.shadowDistance = Cfg.SombraDistancia.Value;
            if (QualitySettings.shadowCascades != casc)
            {
                QualitySettings.shadowCascades = casc;
                QualitySettings.shadowCascade4Split = new Vector3(0.04f, 0.12f, 0.34f); // nitidez perto (cabine, carros ao lado)
                QualitySettings.shadowCascade2Split = 0.2f;
            }
            var res = (ShadowResolution)Mathf.Clamp(Cfg.SombraResolucao.Value, 0, 3);
            if (QualitySettings.shadowResolution != res) QualitySettings.shadowResolution = res;
            if (QualitySettings.shadowProjection != ShadowProjection.StableFit) QualitySettings.shadowProjection = ShadowProjection.StableFit;
            if (QualitySettings.pixelLightCount != Cfg.LuzesPorPixel.Value) QualitySettings.pixelLightCount = Cfg.LuzesPorPixel.Value;
            if (!QualitySettings.realtimeReflectionProbes) QualitySettings.realtimeReflectionProbes = true;
        }

        internal static void Aplicar(Il2Cpp.EnviroSky sky)
        {
            if (!Cfg.Ativo.Value) return;
            sky ??= Sky;
            if (sky == null) return; // menus: sem Enviro, sem mexer na luz
            Ambiente(sky);
            Sol();
            Atmosfera(sky);
        }

        static void Ambiente(Il2Cpp.EnviroSky sky)
        {
            // Cor que o Enviro calculou (se a atual não é a que escrevemos, é dele). Comparar só a cor:
            // se ele voltar o modo para Flat sem mudar a cor, a cor ainda é a nossa.
            var atual = RenderSettings.ambientSkyColor;
            if (!_temBase || !Igual(atual, _escrito)) { _baseFlat = atual; _temBase = true; }

            // Zona de interior do Enviro (túnel, garagem): ele escurece a cor ambiente; o modo céu segue junto.
            float interior = 1f;
            if (sky != null && sky.interiorMode)
            {
                var m = sky.currentInteriorAmbientLightMod;
                interior = Mathf.Clamp01((m.r + m.g + m.b) / 3f);
            }

            if (Cfg.AmbienteCeu.Value)
            {
                RenderSettings.ambientMode = AmbientMode.Skybox;
                // Dentro do ônibus: muito vidro, luz entrando e rebatendo no piso, bancos e painel. Sem GI
                // na engine, o teto e as colunas (virados para baixo/na sombra) ficavam pretos.
                var cam = Pos.Cam;
                float cabine = cam != null && Veiculo.Dentro(cam.transform.position) ? Cfg.CabineAmbiente.Value : 1f;
                float ceu = Ceu.Fator();
                RenderSettings.ambientIntensity = Cfg.AmbienteForca.Value * interior * cabine * ceu * (1f + 0.25f * ChuvaAgora());
                if (!_reflSalvo) { _reflSalvo = true; _oRefl = RenderSettings.reflectionIntensity; }
                RenderSettings.reflectionIntensity = _oRefl * ceu;
                if (Time.unscaledTime >= _proximoGI)
                {
                    _proximoGI = Time.unscaledTime + 1.5f;
                    DynamicGI.UpdateEnvironment();
                }
                _escrito = RenderSettings.ambientSkyColor;
            }
            else
            {
                var b = _baseFlat;
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = Mul(b, Cfg.AmbCeu.Value) * new Color(0.97f, 0.99f, 1.03f);
                RenderSettings.ambientEquatorColor = Mul(b, Cfg.AmbHorizonte.Value);
                RenderSettings.ambientGroundColor = Mul(b, Cfg.AmbChao.Value) * new Color(1.05f, 1f, 0.92f);
                _escrito = RenderSettings.ambientSkyColor;
            }
        }

        static void Sol()
        {
            if (_sol == null) _sol = RenderSettings.sun;
            if (_sol == null) return;
            if (float.IsNaN(_solEscrito) || !Mathf.Approximately(_sol.intensity, _solEscrito)) _solBase = _sol.intensity;
            // Chuva: as nuvens carregadas cortam o sol direto; a luz que sobra chega espalhada pelo céu (Ambiente).
            _sol.intensity = _solBase * Cfg.SolForca.Value * (1f - 0.6f * ChuvaAgora());
            _solEscrito = _sol.intensity;
            if (float.IsNaN(_sombraEscrita) || !Mathf.Approximately(_sol.shadowStrength, _sombraEscrita)) _sombraBase = _sol.shadowStrength;
            _sol.shadowStrength = Mathf.Lerp(_sombraBase, 1f, Cfg.SombraForca.Value);
            _sombraEscrita = _sol.shadowStrength;
            if (_sol.shadows == LightShadows.Hard) _sol.shadows = LightShadows.Soft;
        }

        static void Atmosfera(Il2Cpp.EnviroSky sky)
        {
            if (sky == null) return;
            var vs = sky.volumeLightSettings;
            var ls = sky.lightshaftsSettings;
            if (!_atmSalva)
            {
                _atmSalva = true;
                _oVol = sky.volumeLighting; _oDirVol = vs.dirVolumeLighting; _oScatter = vs.ScatteringCoef; _oAniso = vs.Anistropy;
                _oShafts = sky.LightShafts.sunLightShafts; _oShaftInt = ls.intensity;
            }
            if (Cfg.Volumetrica.Value)
            {
                sky.volumeLighting = true; vs.dirVolumeLighting = true;
                // Na chuva o ar cheio de gotículas espalha mais: aparecem os cones dos postes e dos faróis.
                vs.ScatteringCoef = Cfg.VolumetricaForca.Value * (1f + 3f * ChuvaAgora() * Mathf.Clamp01(Cfg.RainAirHumidity.Value));
                vs.Anistropy = 0.7f; // espalha para a frente: os raios aparecem olhando para o lado do sol
            }
            var r = sky.EnviroSkyRender;
            if (r != null) { r.volumeLighting = sky.volumeLighting; r.dirVolumeLighting = vs.dirVolumeLighting; }
            // Raios radiais em tela: borram o céu claro pelas frestas (faixas amarelas cortando sombras,
            // véu bege contra o sol). Desligados, a luz volumétrica (recortada pelas sombras) faz o papel.
            sky.LightShafts.sunLightShafts = Cfg.RaiosSol.Value;
            if (Cfg.RaiosSol.Value) ls.intensity = Cfg.RaiosSolForca.Value;
            Neblina(sky);
            // A névoa do Enviro é iluminada pelo céu: dentro de um túnel virava um véu cinza claro.
            // Acompanha a luz do céu que chega na câmera.
            var fs = sky.fogSettings;
            if (!_fogSalvo) { _fogSalvo = true; _oFogDist = fs.distanceFogIntensity; _oFogAlt = fs.heightFogIntensity; }
            float k = Ceu.Fator();
            fs.distanceFogIntensity = _oFogDist * k;
            fs.heightFogIntensity = _oFogAlt * k;
        }

        // Névoa de distância mínima em todos os climas. O "céu limpo" do Enviro vem com densidade 1e-5
        // (nada): prédios a 1 km com o mesmo contraste dos de perto. Ar de cidade tem visibilidade de
        // ~10 km, o que dá o véu azulado da perspectiva aérea. Climas com névoa maior ficam como estão.
        static readonly System.Collections.Generic.Dictionary<IntPtr, float> _neblinaOrig = new();
        static float _neblinaAplicada = -1f;

        static void Neblina(Il2Cpp.EnviroSky sky)
        {
            float min = Cfg.Neblina.Value;
            float umidadeAr = Mathf.Max(Cfg.RainAirHumidity.Value, 0f) / 0.95f; // 0,95 = padrão do schema, névoa calibrada
            float chave = min + Cfg.ChuvaNeblina.Value * 1000f + umidadeAr * 7f;
            if (Mathf.Approximately(chave, _neblinaAplicada) && _neblinaOrig.Count > 0) return;
            var lista = sky.Weather.weatherPresets;
            if (lista == null || lista.Count == 0) return;
            for (int i = 0; i < lista.Count; i++)
            {
                var p = lista[i];
                if (p == null) continue;
                if (!_neblinaOrig.TryGetValue(p.Pointer, out float o)) _neblinaOrig[p.Pointer] = o = p.fogDensity;
                // Chuva: cortina d'água no ar. A névoa sobe com a intensidade do clima (wetnessLevel 0 a 1):
                // chuva forte fica com ~1,5 km de visibilidade, garoa com a névoa de cidade.
                float chuva = Mathf.Clamp01(p.wetnessLevel);
                p.fogDensity = Mathf.Max(o, Mathf.Lerp(min, Cfg.ChuvaNeblina.Value * umidadeAr, chuva * chuva));
            }
            _neblinaAplicada = chave;
        }

        static void RestaurarNeblina()
        {
            var sky = Sky;
            if (sky != null)
            {
                var lista = sky.Weather.weatherPresets;
                for (int i = 0; lista != null && i < lista.Count; i++)
                    if (lista[i] != null && _neblinaOrig.TryGetValue(lista[i].Pointer, out float o)) lista[i].fogDensity = o;
            }
            _neblinaOrig.Clear(); _neblinaAplicada = -1f;
        }

        internal static void Restaurar()
        {
            if (_qualSalva)
            {
                QualitySettings.shadowDistance = _oDist; QualitySettings.shadowCascades = _oCasc; QualitySettings.shadowResolution = _oRes;
                QualitySettings.shadows = _oSomb; QualitySettings.shadowProjection = _oProj; QualitySettings.pixelLightCount = _oLuzes;
                _qualSalva = false;
            }
            if (_temBase) { RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientSkyColor = _baseFlat; }
            if (_sol != null && !float.IsNaN(_solEscrito)) { _sol.intensity = _solBase; _sol.shadowStrength = _sombraBase; }
            var sky = Sky;
            if (_atmSalva && sky != null)
            {
                sky.volumeLighting = _oVol; sky.volumeLightSettings.dirVolumeLighting = _oDirVol;
                sky.volumeLightSettings.ScatteringCoef = _oScatter; sky.volumeLightSettings.Anistropy = _oAniso;
                sky.LightShafts.sunLightShafts = _oShafts; sky.lightshaftsSettings.intensity = _oShaftInt;
            }
            if (_fogSalvo && sky != null) { sky.fogSettings.distanceFogIntensity = _oFogDist; sky.fogSettings.heightFogIntensity = _oFogAlt; }
            RestaurarNeblina();
            if (_reflSalvo) { RenderSettings.reflectionIntensity = _oRefl; _reflSalvo = false; }
            CenaMudou();
        }

        // Chuva atual (0 a 1) já com o rain_intensity do schema.
        static float ChuvaAgora() => Mathf.Clamp01(Chuva.Intensidade() * Mathf.Max(Cfg.RainIntensity.Value, 0f));

        static Color Mul(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, 1f);
        static bool Igual(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 1e-4f && Mathf.Abs(a.g - b.g) < 1e-4f && Mathf.Abs(a.b - b.b) < 1e-4f;
    }

    [HarmonyPatch(typeof(Il2Cpp.EnviroSky), "Update")]
    static class PatchEnviroUpdate
    {
        static void Postfix(Il2Cpp.EnviroSky __instance) { try { Luz.Aplicar(__instance); } catch (Exception e) { Log.Erro("Luz", e); } }
    }

    [HarmonyPatch(typeof(Il2Cpp.EnviroSky), "LateUpdate")]
    static class PatchEnviroLateUpdate
    {
        static void Postfix(Il2Cpp.EnviroSky __instance) { try { Luz.Aplicar(__instance); } catch (Exception e) { Log.Erro("Luz", e); } }
    }
}
