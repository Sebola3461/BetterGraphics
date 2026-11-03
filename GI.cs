using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using RGI = Il2CppRadiantGI.RadiantGlobalIllumination;
using RSM = Il2CppRadiantGI.RadiantShadowMap;

namespace BetterGraphics
{
    // Iluminação global (Radiant GI, já incluso no jogo): a luz rebate nas superfícies e ilumina o
    // entorno — o chão ensolarado clareia a lateral do ônibus, o piso ilumina o teto do salão, uma
    // parede verde tinge a calçada.
    //  - RadiantGlobalIllumination (câmera): rebatimento em tela, com raios, múltiplos rebatimentos e
    //    acumulação temporal.
    //  - RadiantShadowMap (no sol): mapa de sombra "refletivo" que traz o rebatimento de superfícies
    //    fora da tela (sem ele, a luz rebatida some quando a fonte sai do quadro).
    // Os ajustes são aplicados quando mudam (não todo segundo), para o Lab poder explorar parâmetros
    // pelo nome (rgi / rgiset) sem o módulo desfazer.
    internal static class GI
    {
        static RGI _rgi;
        static RSM _rsm;
        static GameObject _rsmGo;
        static string _aplicado = "";
        static bool _oLigado; static bool _salvo;
        static float _proximaBuscaVol;
        static readonly System.Collections.Generic.HashSet<Il2CppRadiantGI.RadiantProfile> _perfis = new();
        static readonly System.Collections.Generic.List<(Il2CppRadiantGI.RadiantProfile p, float[] v)> _origPerfis = new();

        // Os mesmos ajustes no perfil do volume (é ele que vale na renderização).
        // Dentro do ônibus a luz rebatida é a principal (piso ensolarado clareando teto e bancos); fora,
        // a mesma força estourava a calçada. A troca acompanha a câmera.
        internal static float Intensidade()
        {
            var cam = Pos.Cam;
            float i = cam != null && Veiculo.Dentro(cam.transform.position) ? Cfg.GIIntensidadeDentro.Value : Cfg.GIIntensidade.Value;
            // À noite as fontes são postes e faróis: o GI em tela amplificava fachadas grandes ao fundo.
            return i * Mathf.Lerp(Cfg.GINoite.Value, 1f, DiaFator());
        }

        // 0 com o sol abaixo do horizonte, 1 com ele acima de 12°. A altura vem do objeto Sun do Enviro: a luz direcional
        // dele vira a lua à noite, e a altura dela dava "dia" às 21h.
        internal static float DiaFator()
        {
            float elev = AlturaSol();
            if (float.IsNaN(elev)) return 1f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 12f, elev));
        }

        // 0 até o sol 2° abaixo do horizonte, 1 com ele 10° abaixo (noite fechada). A hora azul fica entre os dois.
        internal static float NoiteFechada()
        {
            float elev = AlturaSol();
            return float.IsNaN(elev) ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-2f, -10f, elev));
        }

        // Altura do sol em graus (NaN sem sol nenhum).
        internal static float AlturaSol()
        {
            var sky = Luz.Sky;
            if (sky != null) return Mathf.Asin(Mathf.Clamp(PhysicalSky.SunDirection(sky).y, -1f, 1f)) * Mathf.Rad2Deg;
            var sol = RenderSettings.sun;
            return sol != null ? Mathf.Asin(Mathf.Clamp(-sol.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg : float.NaN;
        }

        // Força do mapa de sombra refletivo pela altura do sol: à noite a luz direcional do Enviro vira a
        // lua, e o mapa rebatia essa luz como se fosse sol (fachadas brancas, estouradas).
        internal static float ForcaMapaSombra()
        {
            return Cfg.GIMapaSombraForca.Value * DiaFator();
        }

        static void Perfil(Il2CppRadiantGI.RadiantProfile p)
        {
            p.indirectIntensity = Intensidade();
            p.rayBounce = Cfg.GIRebater.Value;
            p.rayCount = Cfg.GIRaios.Value;
            p.rayMaxLength = Cfg.GIAlcance.Value;
            p.fallbackReflectionProbes = Cfg.GISondas.Value;
            p.fallbackReflectiveShadowMap = Cfg.GIMapaSombra.Value;
            p.reflectiveShadowMapIntensity = ForcaMapaSombra();
            p.fallbackReflectiveShadowMap = Cfg.GIMapaSombra.Value && p.reflectiveShadowMapIntensity > 0.001f;
            p.downsampling = Cfg.GIQualidade.Value >= 1 ? 1f : 2f;
            p.indirectDistanceAttenuation = Cfg.GIAtenuacao.Value;
            p.temporalReprojection = true;
        }

        static float[] Copiar(Il2CppRadiantGI.RadiantProfile p) => new[]
        {
            p.indirectIntensity, p.rayBounce ? 1f : 0f, p.rayCount, p.rayMaxLength, p.fallbackReflectionProbes ? 1f : 0f,
            p.fallbackReflectiveShadowMap ? 1f : 0f, p.reflectiveShadowMapIntensity, p.downsampling, p.temporalReprojection ? 1f : 0f,
        };

        static void Devolver(Il2CppRadiantGI.RadiantProfile p, float[] v)
        {
            p.indirectIntensity = v[0]; p.rayBounce = v[1] > 0f; p.rayCount = (int)v[2]; p.rayMaxLength = v[3];
            p.fallbackReflectionProbes = v[4] > 0f; p.fallbackReflectiveShadowMap = v[5] > 0f; p.reflectiveShadowMapIntensity = v[6];
            p.downsampling = v[7]; p.temporalReprojection = v[8] > 0f;
        }

        internal static Il2CppRadiantGI.RadiantProfile PerfilAtivo { get { foreach (var p in _perfis) return p; return null; } }

        internal static RGI Componente => _rgi;
        internal static RSM MapaSombra => _rsm;

        internal static void CenaMudou() { _rgi = null; _rsm = null; _aplicado = ""; _salvo = false; _proximaBuscaVol = 0f; _perfis.Clear(); _origPerfis.Clear(); }

        internal static void Manter()
        {
            var cam = Pos.Cam;
            if (cam == null) return;
            if (_rgi == null)
            {
                _rgi = cam.GetComponent<RGI>();
                if (_rgi == null) return;
                if (!_salvo) { _salvo = true; _oLigado = _rgi.enabled; }
                _aplicado = "";
            }
            if (_rgi.enabled != Cfg.GIAtivo.Value) _rgi.enabled = Cfg.GIAtivo.Value;
            if (!Cfg.GIAtivo.Value) return;

            // O jogo controla o GI por um RadiantVolume (MepMobileControls.scriptVolumeRadiantGI) cujo perfil
            // vale sobre o componente da câmera (e deixa a intensidade em 0: GI efetivamente desligado).
            // Desligar o volume não adianta (o componente volta aos valores-base, também 0); então o mod
            // escreve no PERFIL do volume e o próprio Radiant aplica todo quadro.
            if (Time.unscaledTime >= _proximaBuscaVol)
            {
                _proximaBuscaVol = Time.unscaledTime + 10f;
                foreach (var v in UnityEngine.Object.FindObjectsOfType<Il2CppRadiantGI.RadiantVolume>())
                    if (v.profile != null && _perfis.Add(v.profile))
                    {
                        _origPerfis.Add((v.profile, Copiar(v.profile)));
                        Log.Msg($"GI: usando o perfil do volume do jogo ({v.gameObject.name}, {v.profile.name}, intensidade original {v.profile.indirectIntensity})");
                    }
            }
            foreach (var pf in _perfis) if (pf != null) Perfil(pf);

            string chave = string.Join("|", Cfg.GIIntensidade.Value, Cfg.GIRebater.Value, Cfg.GIRaios.Value, Cfg.GIAlcance.Value,
                Cfg.GISondas.Value, Cfg.GIMapaSombra.Value, Cfg.GIMapaSombraForca.Value, Cfg.GIQualidade.Value);
            // O jogo também escreve direto no componente (a intensidade vinha 0): os principais são
            // reaplicados todo segundo; o log só sai quando os ajustes mudam.
            if (chave != _aplicado || _rgi.indirectIntensity != Cfg.GIIntensidade.Value || _rgi.rayBounce != Cfg.GIRebater.Value
                || _rgi.fallbackReflectiveShadowMap != Cfg.GIMapaSombra.Value)
            {
                bool mudou = chave != _aplicado;
                _aplicado = chave;
                _rgi.indirectIntensity = Cfg.GIIntensidade.Value;
                _rgi.rayBounce = Cfg.GIRebater.Value;
                _rgi.rayCount = Cfg.GIRaios.Value;
                _rgi.rayMaxLength = Cfg.GIAlcance.Value;
                _rgi.fallbackReflectionProbes = Cfg.GISondas.Value;
                _rgi.fallbackReflectiveShadowMap = Cfg.GIMapaSombra.Value;
                _rgi.reflectiveShadowMapIntensity = Cfg.GIMapaSombraForca.Value;
                // Qualidade: 0 metade da resolução, 1 resolução cheia com mais amostras.
                _rgi.downsampling = Cfg.GIQualidade.Value >= 1 ? 1f : 2f;
                _rgi.temporalReprojection = true;
                if (mudou) Log.Msg($"GI: intensidade {Cfg.GIIntensidade.Value} rebater {Cfg.GIRebater.Value} raios {Cfg.GIRaios.Value} alcance {Cfg.GIAlcance.Value} mapa de sombra {Cfg.GIMapaSombra.Value}");
            }
            MapaDeSombra();
        }

        // LateUpdate, todo quadro: o jogo (MepMobileControls) escreve no componente no Update dele (a
        // intensidade ficava 0, a opção de GI do menu). Escrevendo depois, vale o do mod na renderização.
        internal static void Quadro()
        {
            if (_rgi == null || !Cfg.GIAtivo.Value) return;
            _rgi.indirectIntensity = Intensidade();
            foreach (var pf in _perfis) if (pf != null) pf.indirectIntensity = _rgi.indirectIntensity;
            _rgi.rayBounce = Cfg.GIRebater.Value;
            _rgi.rayCount = Cfg.GIRaios.Value;
            _rgi.rayMaxLength = Cfg.GIAlcance.Value;
            _rgi.fallbackReflectionProbes = Cfg.GISondas.Value;
            _rgi.indirectDistanceAttenuation = Cfg.GIAtenuacao.Value;
            _rgi.fallbackReflectiveShadowMap = Cfg.GIMapaSombra.Value;
            _rgi.reflectiveShadowMapIntensity = ForcaMapaSombra();
            _rgi.fallbackReflectiveShadowMap = Cfg.GIMapaSombra.Value && _rgi.reflectiveShadowMapIntensity > 0.001f;
        }

        // O RadiantShadowMap fica no objeto do sol e captura uma área em volta do alvo (o ônibus).
        static void MapaDeSombra()
        {
            if (!Cfg.GIMapaSombra.Value) { if (_rsm != null && _rsm.enabled) _rsm.enabled = false; return; }
            var sol = RenderSettings.sun;
            if (sol == null) return;
            if (_rsm == null || _rsmGo != sol.gameObject)
            {
                _rsmGo = sol.gameObject;
                _rsm = sol.GetComponent<RSM>() ?? sol.gameObject.AddComponent<RSM>();
                Log.Msg($"GI: mapa de sombra refletivo no sol ({sol.name}), instalado {RSM.installed}");
            }
            if (!_rsm.enabled) _rsm.enabled = true;
            var alvo = Veiculo.Raiz() ?? (Pos.Cam != null ? Pos.Cam.transform : null);
            if (alvo != null && _rsm.target != alvo) _rsm.target = alvo;
        }

        internal static void Restaurar()
        {
            if (_rgi != null && _salvo) _rgi.enabled = _oLigado;
            if (_rsm != null) _rsm.enabled = false;
            foreach (var (p, v) in _origPerfis) try { if (p != null) Devolver(p, v); } catch { }
            _perfis.Clear(); _origPerfis.Clear(); _proximaBuscaVol = 0f;
            _aplicado = "";
        }

        // ---- Lab: leitura/escrita de qualquer parâmetro pelo nome (propriedades do Il2CppInterop) ----

        internal static string Listar(object alvo)
        {
            if (alvo == null) return "componente não encontrado";
            var sb = new StringBuilder();
            foreach (var p in alvo.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                var t = p.PropertyType;
                if (!(t.IsPrimitive || t.IsEnum || t == typeof(Color) || t == typeof(Vector3) || t == typeof(string))) continue;
                try { sb.Append(p.Name).Append('=').Append(Convert.ToString(p.GetValue(alvo), CultureInfo.InvariantCulture)).Append(' '); } catch { }
            }
            return sb.ToString();
        }

        internal static string Definir(object alvo, string nome, string valor)
        {
            if (alvo == null) return "componente não encontrado";
            var p = alvo.GetType().GetProperty(nome, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) return "sem propriedade " + nome;
            var t = p.PropertyType;
            object v = t.IsEnum ? Enum.Parse(t, valor, true)
                : t == typeof(bool) ? (valor == "1" || valor.Equals("true", StringComparison.OrdinalIgnoreCase))
                : Convert.ChangeType(valor, t, CultureInfo.InvariantCulture);
            p.SetValue(alvo, v);
            return $"ok {nome}={Convert.ToString(p.GetValue(alvo), CultureInfo.InvariantCulture)}";
        }
    }
}
