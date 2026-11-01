using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace BetterGraphics
{
    // Câmera principal (deferred, HDR, anti-aliasing) e o volume PPv2 do mod. Com o pós nativo
    // desligado (Nativo), este volume é a pilha inteira: o que não é definido aqui fica neutro.
    internal static class Pos
    {
        static Camera _cam;
        static RenderingPath _oPath; static bool _oHdr;
        static PostProcessLayer _layer; static PostProcessLayer.Antialiasing _oAA; static bool _aaSalvo, _oLayerLigado, _avisouLayer;
        static Il2CppRadiantGI.RadiantGlobalIllumination _rgi; static bool _oRgi;

        static GameObject _go;
        static PostProcessVolume _vol;
        static AutoExposure _exp; static ColorGrading _cor; static AmbientOcclusion _ao; static ScreenSpaceReflections _ssr; static Bloom _bloom;

        internal static PostProcessVolume Volume => _vol;
        internal static Camera Cam => _cam;

        internal static void CenaMudou() { _cam = null; _layer = null; _rgi = null; _aaSalvo = false; }

        internal static void Manter()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (cam != _cam)
            {
                _cam = cam;
                _oPath = cam.renderingPath; _oHdr = cam.allowHDR;
                _layer = cam.GetComponent<PostProcessLayer>();
                _rgi = cam.GetComponent<Il2CppRadiantGI.RadiantGlobalIllumination>();
                if (_rgi != null) _oRgi = _rgi.enabled;
                _aaSalvo = false;
                Log.Msg($"Câmera: {cam.name} ({_oPath}, HDR {_oHdr}), PostProcessLayer {(_layer != null ? "sim" : "não")}, RadiantGI {(_rgi != null ? (_oRgi ? "ligado" : "desligado") : "não")}");
            }

            var path = Cfg.Deferred.Value ? RenderingPath.DeferredShading : _oPath;
            if (cam.renderingPath != path) cam.renderingPath = path;
            if (!cam.allowHDR) cam.allowHDR = true;

            if (_layer == null) return;
            if (!_aaSalvo) { _oAA = _layer.antialiasingMode; _oLayerLigado = _layer.enabled; _aaSalvo = true; }
            // O jogo desliga o PostProcessLayer (opção de pós-processamento do menu, ou o trocador de perfis que
            // o Nativo desliga): sem ele NADA do volume do mod roda (exposição, curva, cor, AO, SSR, bloom) e o
            // HDR sai cortado em 255. Com o mod ativo ele fica sempre ligado.
            if (!_layer.enabled)
            {
                _layer.enabled = true;
                if (!_avisouLayer) { _avisouLayer = true; Log.Msg("PostProcessLayer estava desligado pelo jogo: religado"); }
            }
            var aa = Cfg.AA.Value.ToUpperInvariant() switch
            {
                "TAA" => PostProcessLayer.Antialiasing.TemporalAntialiasing,
                "SMAA" => PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
                "FXAA" => PostProcessLayer.Antialiasing.FastApproximateAntialiasing,
                _ => PostProcessLayer.Antialiasing.None,
            };
            if (_layer.antialiasingMode != aa) _layer.antialiasingMode = aa;
            if (!_flagsLidos) { _flagsLidos = true; Log.Msg($"PostProcessLayer: finalBlit {_layer.finalBlitToCameraTarget} breakBeforeColorGrading {_layer.breakBeforeColorGrading} stopNaN {_layer.stopNaNPropagation}"); }
            if (Cfg.CorrigirLayer.Value)
            {
                if (_layer.finalBlitToCameraTarget) _layer.finalBlitToCameraTarget = false;
                if (_layer.breakBeforeColorGrading) _layer.breakBeforeColorGrading = false;
            }

            CriarVolume();
            Configurar();
        }

        static void CriarVolume()
        {
            int camada = 0;
            int mask = _layer.volumeLayer.value;
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) { camada = i; break; }
            if (_go == null)
            {
                _go = new GameObject("BetterGraphics_Volume") { layer = camada };
                UnityEngine.Object.DontDestroyOnLoad(_go);
                var perfil = ScriptableObject.CreateInstance<PostProcessProfile>();
                perfil.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _exp = perfil.AddSettings(Il2CppType.Of<AutoExposure>()).Cast<AutoExposure>();
                _cor = perfil.AddSettings(Il2CppType.Of<ColorGrading>()).Cast<ColorGrading>();
                _ao = perfil.AddSettings(Il2CppType.Of<AmbientOcclusion>()).Cast<AmbientOcclusion>();
                _ssr = perfil.AddSettings(Il2CppType.Of<ScreenSpaceReflections>()).Cast<ScreenSpaceReflections>();
                _bloom = perfil.AddSettings(Il2CppType.Of<Bloom>()).Cast<Bloom>();
                _vol = _go.AddComponent<PostProcessVolume>();
                _vol.isGlobal = true; _vol.priority = 1000f; _vol.weight = 1f;
                _vol.sharedProfile = perfil;
            }
            if (_go.layer != camada) _go.layer = camada;
            if (!_go.activeSelf) _go.SetActive(true);
            // Experimento: usar o volume que o próprio jogo tem na câmera como portador do perfil.
            if (Cfg.VolumeDoJogo.Value)
            {
                var vj = _cam.GetComponent<PostProcessVolume>();
                if (vj != null)
                {
                    if (vj.sharedProfile != _vol.sharedProfile) vj.sharedProfile = _vol.sharedProfile;
                    vj.isGlobal = true; vj.priority = 1000f; vj.weight = 1f;
                    if (!vj.enabled) vj.enabled = true;
                    VolumeJogo = vj;
                }
            }
            else VolumeJogo = null;
        }

        internal static PostProcessVolume VolumeJogo;
        static bool _flagsLidos;

        static void Configurar()
        {
            // Adaptação do olho: a média de luz da tela (ignorando os 50% mais escuros e os 5% mais
            // claros) é levada ao cinza médio, dentro da faixa de EV permitida.
            _exp.active = Cfg.AutoExposicao.Value;
            Liga(_exp);
            // Na cabine metade da tela é interior escuro e puxava a exposição para cima, estourando a rua
            // pelo para-brisa. Lá dentro a medição ignora mais das áreas escuras (expõe pelo lado de fora).
            bool dentro = _cam != null && Veiculo.Dentro(_cam.transform.position);
            Set(_exp.filtering, dentro ? new Vector2(Cfg.CabineMedicao.Value, 97f) : new Vector2(50f, 95f));
            Set(_exp.minLuminance, Cfg.ExposicaoMin.Value);
            Set(_exp.maxLuminance, Cfg.ExposicaoMax.Value);
            Set(_exp.keyValue, 1f);
            Set(_exp.eyeAdaptation, EyeAdaptation.Progressive);
            Set(_exp.speedUp, 2.5f);   // para o claro: rápido (saindo do túnel)
            Set(_exp.speedDown, 1.0f); // para o escuro: mais lento, como o olho

            _cor.active = true;
            Liga(_cor);
            Set(_cor.gradingMode, GradingMode.HighDefinitionRange);
            string tm = Cfg.Tonemapper.Value.ToLowerInvariant();
            Set(_cor.tonemapper, tm == "neutral" ? Tonemapper.Neutral : tm == "nenhum" ? Tonemapper.None : tm == "custom" ? Tonemapper.Custom : Tonemapper.ACES);
            // Curva de filme própria (por canal, sem a matriz do ACES que puxa azul para roxo):
            // pé firme nas sombras, ombro suave nos claros.
            // Na cabine o pé é mais suave: a tela tem rua clara e painel escuro ao mesmo tempo, e o olho se
            // adapta localmente (o painel não some no preto como numa foto exposta pela rua).
            Set(_cor.toneCurveToeStrength, dentro ? Cfg.CabinePe.Value : Cfg.CurvaPe.Value);
            Set(_cor.toneCurveToeLength, Cfg.CurvaPeAlcance.Value);
            Set(_cor.toneCurveShoulderStrength, Cfg.CurvaOmbro.Value);
            Set(_cor.toneCurveShoulderLength, Cfg.CurvaOmbroAlcance.Value);
            Set(_cor.toneCurveShoulderAngle, Cfg.CurvaOmbroAngulo.Value);
            Set(_cor.toneCurveGamma, 1f);
            // Chuva forte é escura: a adaptação não deve deixar com cara de nublado claro.
            // Na cabine o olho se adapta ao interior; a média medida é dominada pela rua clara pelo
            // para-brisa, e o painel afundava no preto.
            float cabine = dentro ? Cfg.CabineExposicao.Value : 0f;
            // Chuva de dia: céu fechado claro e chão escuro; a adaptação automática levaria o chão molhado ao
            // cinza médio. Um pouco menos de exposição mantém o "dia escuro". À noite não: a cena já está no
            // limite da adaptação e ficava preta.
            Set(_cor.postExposure, Cfg.Exposicao.Value + Cfg.ChuvaExposicao.Value * Chao.Umidade * GI.DiaFator() + cabine);
            Set(_cor.contrast, Cfg.Contraste.Value);
            Set(_cor.saturation, Cfg.Saturacao.Value);
            Set(_cor.temperature, Cfg.Temperatura.Value);

            _ao.active = Cfg.AO.Value;
            Liga(_ao);
            Set(_ao.mode, AmbientOcclusionMode.MultiScaleVolumetricObscurance);
            Set(_ao.intensity, Cfg.AOIntensidade.Value);
            Set(_ao.thicknessModifier, 1f);
            Set(_ao.ambientOnly, true);
            Set(_ao.color, Color.black);

            _ssr.active = Cfg.SSR.Value && Cfg.Deferred.Value;
            Liga(_ssr);
            Set(_ssr.preset, ScreenSpaceReflectionPreset.High);
            Set(_ssr.resolution, ScreenSpaceReflectionResolution.Downsampled);
            Set(_ssr.maximumMarchDistance, 100f);
            Set(_ssr.distanceFade, 0.3f);
            Set(_ssr.vignette, 0.5f);

            _bloom.active = Cfg.Bloom.Value > 0f;
            Liga(_bloom);
            Set(_bloom.intensity, Cfg.Bloom.Value);
            Set(_bloom.threshold, Cfg.BloomLimiar.Value);
            Set(_bloom.softKnee, 0.5f);
            Set(_bloom.diffusion, 7f);
        }

        // ParameterOverride<T>.value fica numa classe genérica, e o acessor do Il2CppInterop lê/escreve no
        // endereço errado (valores lixo; nenhum ajuste chegava ao efeito). O deslocamento real do campo
        // vem do próprio IL2CPP, pela classe concreta do parâmetro, e a escrita é direta na memória.
        static readonly System.Collections.Generic.Dictionary<IntPtr, int> _desloc = new();

        internal static unsafe void Escrever<T>(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase p, T v) where T : struct
            => System.Runtime.CompilerServices.Unsafe.Write((byte*)p.Pointer + Desloc(p.Pointer), v);

        internal static unsafe T Ler<T>(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase p) where T : struct
            => System.Runtime.CompilerServices.Unsafe.Read<T>((byte*)p.Pointer + Desloc(p.Pointer));

        static int Desloc(IntPtr obj)
        {
            IntPtr cls = IL2CPP.il2cpp_object_get_class(obj);
            if (_desloc.TryGetValue(cls, out int d)) return d;
            IntPtr f = IL2CPP.il2cpp_class_get_field_from_name(cls, "value"); // procura também nas classes-pai
            if (f == IntPtr.Zero) throw new InvalidOperationException("campo value não encontrado");
            return _desloc[cls] = (int)IL2CPP.il2cpp_field_get_offset(f);
        }

        static void Liga(PostProcessEffectSettings e) { e.enabled.overrideState = true; Escrever<byte>(e.enabled, 1); }
        static void Set(FloatParameter p, float v) { p.overrideState = true; Escrever(p, v); }
        static void Set(BoolParameter p, bool v) { p.overrideState = true; Escrever<byte>(p, (byte)(v ? 1 : 0)); }
        static void Set(ColorParameter p, Color v) { p.overrideState = true; Escrever(p, v); }
        static void Set(Vector2Parameter p, Vector2 v) { p.overrideState = true; Escrever(p, v); }
        static void Set(EyeAdaptationParameter p, EyeAdaptation v) { p.overrideState = true; Escrever(p, (int)v); }
        static void Set(GradingModeParameter p, GradingMode v) { p.overrideState = true; Escrever(p, (int)v); }
        static void Set(TonemapperParameter p, Tonemapper v) { p.overrideState = true; Escrever(p, (int)v); }
        static void Set(AmbientOcclusionModeParameter p, AmbientOcclusionMode v) { p.overrideState = true; Escrever(p, (int)v); }
        static void Set(ScreenSpaceReflectionPresetParameter p, ScreenSpaceReflectionPreset v) { p.overrideState = true; Escrever(p, (int)v); }
        static void Set(ScreenSpaceReflectionResolutionParameter p, ScreenSpaceReflectionResolution v) { p.overrideState = true; Escrever(p, (int)v); }

        internal static string Estado()
        {
            var sb = new System.Text.StringBuilder("vol: ");
            sb.Append($"cam {(_cam != null ? _cam.name : "-")} layer {(_layer != null ? $"finalBlit {_layer.finalBlitToCameraTarget} break {_layer.breakBeforeColorGrading} enabled {_layer.enabled} AA {_layer.antialiasingMode} volLayer {_layer.volumeLayer.value} trigger {(_layer.volumeTrigger != null ? _layer.volumeTrigger.name : "-")}" : "-")}");
            if (_vol == null) return sb.Append(" | sem volume").ToString();
            sb.Append($" | volume ativo {_vol.isActiveAndEnabled} go {_go.activeInHierarchy} camada {_go.layer} global {_vol.isGlobal} prio {_vol.priority} peso {_vol.weight}");
            var perfil = _vol.sharedProfile;
            sb.Append($" | perfil {(perfil != null ? perfil.settings.Count.ToString() : "nulo")} efeitos");
            if (perfil != null)
                foreach (var e in perfil.settings)
                    sb.Append($" [{e.GetIl2CppType().Name} active {e.active} enabled {e.enabled.value}/{e.enabled.overrideState}]");
            sb.Append($" | exp min {Ler<float>(_exp.minLuminance)} max {Ler<float>(_exp.maxLuminance)} filtro {Ler<Vector2>(_exp.filtering)} | cor post {Ler<float>(_cor.postExposure)} tonemap {Ler<int>(_cor.tonemapper)} modo {Ler<int>(_cor.gradingMode)} | ao {Ler<float>(_ao.intensity)} modo {Ler<int>(_ao.mode)} | bloom {Ler<float>(_bloom.intensity)} limiar {Ler<float>(_bloom.threshold)}");
            var mgr = PostProcessManager.instance;
            sb.Append($" | manager {(mgr != null ? "ok" : "nulo")}");
            return sb.ToString();
        }

        internal static void Restaurar()
        {
            if (_cam != null) { _cam.renderingPath = _oPath; _cam.allowHDR = _oHdr; }
            if (_layer != null && _aaSalvo) { _layer.antialiasingMode = _oAA; _layer.enabled = _oLayerLigado; }
            if (_rgi != null) _rgi.enabled = _oRgi;
            if (_go != null) _go.SetActive(false);
            CenaMudou();
        }
    }
}
