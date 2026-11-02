using System;
using MelonLoader;
using PBSLoaderCommonUtils;
using UnityEngine;

[assembly: MelonInfo(typeof(BetterGraphics.BetterGraphicsMod), "BetterGraphics", "1.6.0", "nzrn")]
[assembly: MelonGame(null, "Proton Bus Simulator")]

namespace BetterGraphics
{
    // Visual realista para o Proton (Unity 2020.3, pipeline Built-in, Enviro, PPv2).
    // Módulos:
    //   Nativo     desliga o pós-processamento/filtros do próprio jogo (o mod vira a única pilha)
    //   Luz        luz ambiente do céu, sol, sombras (roda logo depois do Enviro)
    //   Atmosfera  luz volumétrica e raios do sol do Enviro
    //   Pos        câmera (deferred, HDR, AA) e volume PPv2 próprio (exposição, cor, AO, SSR, bloom)
    //   Reflexos   sonda em tempo real sobre o ônibus e reflexo nas pinturas
    //   Vidros     reflexo/fumê dos vidros dos veículos
    //   Lab        automação por arquivo de comandos (--graficos-lab) para testar sem jogar
    public class BetterGraphicsMod : MelonMod
    {
        float _proxima;

        public override void OnLateInitializeMelon() => UiIntegracao.Registrar();


        public override void OnInitializeMelon()
        {
            Cfg.Criar();
            Lab.Iniciar();
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            _proxima = 0f;
            Veiculo.CenaMudou();
            Luz.CenaMudou();
            Pos.CenaMudou();
            Reflexos.CenaMudou();
            Vidros.CenaMudou();
            Materiais.CenaMudou();
            Nativo.CenaMudou();
            GI.CenaMudou();
            Varredura.Reiniciar();
            Chao.CenaMudou();
            Chuva.CenaMudou();
            Interior.CenaMudou();
            Molhado.CenaMudou();
            WindshieldRain.CenaMudou();
            RainFall.CenaMudou();
            DeferredLighting.CenaMudou();
        }

        public override void OnUpdate()
        {
            Lab.Atualizar();

            if (!Cfg.Ativo.Value) return;
            if (Time.unscaledTime >= _proxima)
            {
                _proxima = Time.unscaledTime + 1f;
                Seguro(Nativo.Manter, "Nativo");
                Seguro(Luz.Manter, "Luz");
                Seguro(Materiais.Manter, "Materiais");
                Seguro(Chao.Manter, "Chao");
                Seguro(Pos.Manter, "Pos");
                Seguro(Reflexos.Manter, "Reflexos");
                Seguro(GI.Manter, "GI");
                Seguro(Vidros.Manter, "Vidros");
                Seguro(Interior.Manter, "Interior");
                Seguro(Molhado.Manter, "Molhado");
                Seguro(WindshieldRain.Manter, "ChuvaVidro");
                Seguro(DeferredLighting.Manter, "Deferred");
            }
            Seguro(Varredura.Quadro, "Varredura");
            Seguro(Reflexos.Quadro, "Sonda");
            Seguro(Vidros.Quadro, "Vidros");
            Seguro(Chuva.Quadro, "Chuva");
            if (Cfg.Ativo.Value) Seguro(DeferredLighting.Quadro, "DeferredQuadro");
        }

        public override void OnLateUpdate()
        {
            Lab.Camera();
            if (Cfg.Ativo.Value) Seguro(Pos.Quadro, "DofQuadro");
            if (Cfg.Ativo.Value) Seguro(GI.Quadro, "GIQuadro");
            if (Cfg.Ativo.Value) Seguro(Interior.Quadro, "InteriorQuadro");
            if (Cfg.Ativo.Value) Seguro(Molhado.Quadro, "MolhadoQuadro");
            if (Cfg.Ativo.Value) Seguro(WindshieldRain.Quadro, "ChuvaVidroQuadro");
            if (Cfg.Ativo.Value) Seguro(RainFall.Quadro, "ChuvaGPU"); else RainFall.CenaMudou();
            if (Cfg.Ativo.Value) Seguro(() => Luz.Aplicar(null), "LuzQuadro"); // reforço caso o patch no Enviro não rode
        }

        public override void OnPreferencesLoaded()
        {
            if (Cfg.Ativo == null) return;
            if (!Cfg.Ativo.Value) Alternar(false);
        }

        internal static void Alternar(bool ligar)
        {
            Cfg.Ativo.Value = ligar;
            MelonPreferences.Save();
            Melon<BetterGraphicsMod>.Logger.Msg(ligar ? "Ligado" : "Desligado (visual original)");
            if (ligar) return; // o próximo Manter reaplica tudo
            Seguro(Nativo.Restaurar, "Nativo");
            Seguro(Pos.Restaurar, "Pos");
            Seguro(Luz.Restaurar, "Luz");
            Seguro(Reflexos.Restaurar, "Reflexos");
            Seguro(Vidros.Restaurar, "Vidros");
            Seguro(Materiais.Restaurar, "Materiais");
            Seguro(Chao.Restaurar, "Chao");
            Seguro(GI.Restaurar, "GI");
            Seguro(Interior.Restaurar, "Interior");
            Seguro(Molhado.Restaurar, "Molhado");
            Seguro(WindshieldRain.Restaurar, "ChuvaVidro");
            Seguro(DeferredLighting.Restaurar, "Deferred");
            Seguro(Chuva.CenaMudou, "Chuva");
            Varredura.Reiniciar(); // ao religar, tudo é reaplicado
        }

        static readonly System.Diagnostics.Stopwatch _sw = new();

        // Executa um módulo isolando erros e medindo o tempo: o que passar de 3 ms vai para o log
        // (no máximo a cada 5 s por módulo) e para as estatísticas do Lab.
        internal static void Seguro(Action a, string onde)
        {
            _sw.Restart();
            try { a(); }
            catch (Exception e) { Log.Erro(onde, e); }
            double ms = _sw.Elapsed.TotalMilliseconds;
            Lab.Tempo(onde, ms);
            if (ms > 3.0) Log.Lento(onde, ms);
        }
    }

    internal static class Log
    {
        static readonly System.Collections.Generic.Dictionary<string, float> _ultimo = new();
        internal static void Msg(string s) => Melon<BetterGraphicsMod>.Logger.Msg(s);
        internal static void Aviso(string s) => Melon<BetterGraphicsMod>.Logger.Warning(s);
        static readonly System.Collections.Generic.Dictionary<string, float> _lento = new();
        internal static void Lento(string onde, double ms)
        {
            if (_lento.TryGetValue(onde, out float t) && Time.unscaledTime - t < 5f) return;
            _lento[onde] = Time.unscaledTime;
            Melon<BetterGraphicsMod>.Logger.Msg($"[lento] {onde}: {ms:0.0} ms");
        }
        // Erros repetidos (todo frame) aparecem no máximo a cada 10 s.
        internal static void Erro(string onde, Exception e)
        {
            if (_ultimo.TryGetValue(onde, out float t) && Time.unscaledTime - t < 10f) return;
            _ultimo[onde] = Time.unscaledTime;
            // Primeira linha da pilha que é do mod (as de cima costumam ser do Il2CppInterop).
            string onde2 = "";
            foreach (var l in (e.StackTrace ?? "").Split('\n'))
                if (l.Contains("BetterGraphics.", StringComparison.Ordinal)) { onde2 = l.Trim(); break; }
            Melon<BetterGraphicsMod>.Logger.Warning($"{onde}: {e.GetType().Name}: {e.Message} | {onde2}");
        }
    }

    internal static class Cfg
    {
        internal static MelonPreferences_Category Cat;
        internal static MelonPreferences_Entry<bool> Ativo, DesligarNativo, Deferred, AmbienteCeu, AutoExposicao, AO, SSR,
            Sonda, ReflexoPintura, VidrosAtivo, Volumetrica, RaiosSol, RadiantGI, ChaoAtivo, VolumeDoJogo, CorrigirLayer, VidroSemSombra, CeuOclusao, ChaoPocas, ChuvaEfeitos, Dof, SondaSalao, LuzJanela, ChuvaGlobal;
        internal static MelonPreferences_Entry<float> VarreduraMs, GIIntensidade, GIIntensidadeDentro, GINoite, GIAtenuacao, GIAlcance, GIMapaSombraForca;
        internal static MelonPreferences_Entry<bool> GIAtivo, GIRebater, GISondas, GIMapaSombra;
        internal static MelonPreferences_Entry<int> GIRaios, GIQualidade, ChuvaEmissoesQuadro;
        internal static Atalho TeclaAlternar, TeclaDiag;
        internal static MelonPreferences_Entry<string> AA, MapaMateriais, Tonemapper;
        internal static MelonPreferences_Entry<int> SombraCascatas, SombraResolucao, LuzesPorPixel, SondaResolucao;
        internal static MelonPreferences_Entry<float> SombraDistancia, AmbienteForca, AmbCeu, AmbHorizonte, AmbChao, SolForca, SombraForca,
            Exposicao, ExposicaoMin, ExposicaoMax, Contraste, Saturacao, Temperatura, Bloom, BloomLimiar, AOIntensidade,
            SondaIntervalo, BrilhoMax, VidroReflexo, VidroReflexoPerto, VidroReflexoDentro, VidroVeu, VidroVeuPerto, VidroTom,
            VolumetricaForca, RaiosSolForca, MapaBrilho, ChaoSeco, ChaoMolhado, CurvaPe, CurvaOmbro, ChaoEscurecer, ChuvaExposicao, CabineExposicao, CabineAmbiente, CabineMedicao, ChaoBaseUmida, ChuvaPneus, ChuvaPocas, ChuvaSarjeta,
            Neblina, CeuOclusaoMin, CeuOclusaoAlcance, LatariaBrilho, LatariaVerniz, CurvaPeAlcance, CurvaOmbroAlcance, CurvaOmbroAngulo, CabinePe, DofAbertura, DofFocal, ChuvaFrio, JanelaRebate, JanelaForca;

        internal static void Criar()
        {
            var c = Cat = MelonPreferences.CreateCategory("Realismo", "BetterGraphics (realismo)");
            Ativo = c.CreateEntry("Ativo", true, "Liga o mod");
            TeclaAlternar = Atalhos.Registrar("Realismo", "Alternar", "Ligar ou desligar o BetterGraphics", "Ctrl+Alt+F9",
                () => BetterGraphicsMod.Alternar(!Ativo.Value), "Compara com o visual original do jogo");
            TeclaDiag = Atalhos.Registrar("Realismo", "Diagnostico", "Diagnóstico do BetterGraphics", "Ctrl+Alt+Shift+F9", Diag.Gravar,
                "Grava UserData/BetterGraphics_diagnostico.txt");
            DesligarNativo = c.CreateEntry("DesligarPosNativo", true, "Desliga o pós-processamento, filtros e presets de efeito do próprio jogo");

            MapaMateriais = c.CreateEntry("MateriaisDoMapa", "original", "fosco = paredes/calçadas sem o metal falso do carregador de mapas; semmetal = só zera o metal; original");
            MapaBrilho = c.CreateEntry("MateriaisDoMapaBrilho", 0.2f, "Smoothness dos materiais do mapa no modo fosco");

            ChaoAtivo = c.CreateEntry("ChaoMolhado", true, "Asfalto com poças que refletem conforme a chuva");
            ChaoSeco = c.CreateEntry("ChaoBrilhoSeco", 0.3f, "Brilho do mapa de poças com tempo seco (0 a 1)");
            ChaoMolhado = c.CreateEntry("ChaoBrilhoMolhado", 1.0f, "Brilho do mapa de poças com chuva forte");
            ChaoPocas = c.CreateEntry("ChaoPocas", true, "Refaz o mapa de poças do asfalto (sem o granulado de pontinhos brilhantes)");
            ChaoBaseUmida = c.CreateEntry("ChaoBaseUmida", 0.72f, "Smoothness do asfalto molhado fora das poças (encharcado)");
            ChuvaGlobal = c.CreateEntry("ChuvaGlobal", true, "Na chuva tudo que fica exposto molha: escurece, brilha, forma poças com ondulações (embaixo de telhado e túnel fica seco). Usa a umidade do ChaoMolhado");
            ChuvaEfeitos = c.CreateEntry("ChuvaEfeitos", true, "Respingo dos pneus, splash nas poças e água correndo na sarjeta");
            ChuvaPneus = c.CreateEntry("ChuvaPneus", 1.0f, "Quantidade de spray/névoa levantada pelos pneus no molhado");
            ChuvaPocas = c.CreateEntry("ChuvaPocas", 1.0f, "Força do splash ao passar numa poça");
            ChuvaSarjeta = c.CreateEntry("ChuvaSarjeta", 0f, "Água correndo junto ao meio-fio na chuva (desativada; acima de 0 liga)");
            ChuvaEmissoesQuadro = c.CreateEntry("ChuvaEmissoesQuadro", 140, "Máximo de partículas criadas por quadro pelos efeitos de chuva (desempenho)");
            ChaoEscurecer = c.CreateEntry("ChaoEscurecerMolhado", 0.65f, "Cor do chão encharcado em relação ao seco");
            ChuvaExposicao = c.CreateEntry("ChuvaExposicao", -0.35f, "Exposição a mais (EV) com chuva forte de dia (some à noite)");
            ChuvaFrio = c.CreateEntry("ChuvaFrio", 8f, "Quanto a chuva de dia puxa a cor para o frio (0 a 100)");

            VarreduraMs = c.CreateEntry("VarreduraMsPorQuadro", 1.0f, "Tempo máximo por quadro procurando vidros/pinturas/chão novos (ms)");

            GIAtivo = c.CreateEntry("GI", true, "Iluminação global (Radiant GI): a luz rebate nas superfícies");
            GIIntensidade = c.CreateEntry("GIIntensidade", 1.0f, "Força da luz rebatida (fora do ônibus)");
            GIIntensidadeDentro = c.CreateEntry("GIIntensidadeDentro", 1.7f, "Força da luz rebatida com a câmera dentro do ônibus");
            GINoite = c.CreateEntry("GINoite", 0.5f, "Multiplicador do GI à noite");
            GIAtenuacao = c.CreateEntry("GIAtenuacao", 0.5f, "Quanto o GI enfraquece com a distância (evita fachadas ao fundo estourando)");
            GIRebater = c.CreateEntry("GIRebater", true, "Vários rebatimentos (a luz rebatida rebate de novo)");
            GIRaios = c.CreateEntry("GIRaios", 2, "Raios por pixel (1 a 4; mais = menos ruído, mais pesado)");
            GIAlcance = c.CreateEntry("GIAlcance", 8f, "Distância máxima que a luz rebatida alcança (m)");
            GISondas = c.CreateEntry("GISondas", true, "Usa a sonda de reflexo quando o raio sai da tela");
            GIMapaSombra = c.CreateEntry("GIMapaSombra", true, "Mapa de sombra refletivo: rebatimento do sol mesmo de superfícies fora da tela");
            GIMapaSombraForca = c.CreateEntry("GIMapaSombraForca", 0.15f, "Força do rebatimento vindo do mapa de sombra (acima de ~0.3 superfícies coloridas no sol acendem manchas em paredes distantes)");
            GIQualidade = c.CreateEntry("GIQualidade", 1, "0 metade da resolução (leve), 1 resolução cheia");

            Deferred = c.CreateEntry("Deferred", true, "Câmera em deferred (reflexos em tela)");
            VolumeDoJogo = c.CreateEntry("UsarVolumeDoJogo", false, "Experimental: aplica o perfil do mod pelo volume que já existe na câmera");
            CorrigirLayer = c.CreateEntry("CorrigirPostProcessLayer", true, "Desliga finalBlitToCameraTarget/breakBeforeColorGrading da câmera (senão a cor final é descartada)");
            AA = c.CreateEntry("AntiAliasing", "TAA", "TAA (mais estável em fios e folhas), SMAA ou FXAA");
            SombraDistancia = c.CreateEntry("SombraDistancia", 180f, "Distância das sombras (m)");
            SombraCascatas = c.CreateEntry("SombraCascatas", 4, "1, 2 ou 4");
            SombraResolucao = c.CreateEntry("SombraResolucao", 3, "0 baixa .. 3 muito alta");
            LuzesPorPixel = c.CreateEntry("LuzesPorPixel", 8, "Luzes por pixel (faróis, postes)");
            RadiantGI = c.CreateEntry("RadiantGI", true, "(antigo, use GI)");

            AmbienteCeu = c.CreateEntry("AmbienteDoCeu", true, "Luz ambiente calculada do céu do Enviro (true) ou em três faixas a partir da cor dele (false)");
            AmbienteForca = c.CreateEntry("AmbienteForca", 1.0f, "Força da luz ambiente do céu");
            AmbCeu = c.CreateEntry("AmbienteFaixaCeu", 1.0f, "Modo três faixas: luz vinda de cima");
            AmbHorizonte = c.CreateEntry("AmbienteFaixaHorizonte", 0.75f, "Modo três faixas: faces verticais");
            AmbChao = c.CreateEntry("AmbienteFaixaChao", 0.4f, "Modo três faixas: luz vinda de baixo");
            SolForca = c.CreateEntry("SolForca", 1.0f, "Multiplica o sol do Enviro");
            SombraForca = c.CreateEntry("SombraForca", 1.0f, "0 = sombra do Enviro, 1 = sombra física (o preenchimento vem só do céu)");

            AutoExposicao = c.CreateEntry("AutoExposicao", true, "Adaptação do olho (túnel, abrigo, cabine)");
            Tonemapper = c.CreateEntry("Tonemapper", "Custom", "ACES (filme, puxa azuis para o roxo) ou Neutral (sem desvio de cor)");
            CurvaPe = c.CreateEntry("CurvaPe", 0.6f, "Tonemapper Custom: força do pé (sombras firmes)");
            CurvaOmbro = c.CreateEntry("CurvaOmbro", 1.8f, "Tonemapper Custom: força do ombro (até onde os claros vão antes de comprimir)");
            CurvaPeAlcance = c.CreateEntry("CurvaPeAlcance", 0.5f, "Tonemapper Custom: até onde vai o pé (0 a 1)");
            CurvaOmbroAlcance = c.CreateEntry("CurvaOmbroAlcance", 2.2f, "Tonemapper Custom: margem dos claros em EV antes de saturar (curto = branco estoura)");
            CurvaOmbroAngulo = c.CreateEntry("CurvaOmbroAngulo", 0f, "Tonemapper Custom: inclinação final do ombro (0 a 1)");
            Exposicao = c.CreateEntry("Exposicao", -1f, "Compensação de exposição (EV)");
            ExposicaoMin = c.CreateEntry("ExposicaoMin", -3.5f, "Luminância média mínima considerada (EV100): abaixo disso a cena fica escura de verdade. -3.5 deixa a rua à noite no brilho das fotos de referência (-2.5 escurecia demais)");
            ExposicaoMax = c.CreateEntry("ExposicaoMax", 5f, "Luminância média máxima considerada (EV100); sol do meio-dia fica perto de +3,5");
            CabineExposicao = c.CreateEntry("CabineExposicao", 0.3f, "Exposição a mais (EV) com a câmera dentro do ônibus");
            CabinePe = c.CreateEntry("CabinePe", 0.3f, "Pé da curva com a câmera dentro do ônibus (mais suave: painel visível)");
            CabineMedicao = c.CreateEntry("CabineMedicao", 60f, "Na cabine, % mais escura da tela ignorada pela adaptação (maior = expõe pelo lado de fora)");
            LuzJanela = c.CreateEntry("LuzJanela", true, "Luz ambiente do interior vindo só pelas janelas (sem ela o salão fica chapado, iluminado como se não tivesse teto)");
            JanelaRebate = c.CreateEntry("JanelaRebate", 0.35f, "Luz que paredes, teto e piso do interior devolvem, em relação à média de fora");
            JanelaForca = c.CreateEntry("JanelaForca", 1f, "Quanto da luz das janelas substitui a luz de céu aberto no interior (0 a 1)");
            CabineAmbiente = c.CreateEntry("CabineAmbiente", 1.0f, "Luz ambiente dentro do ônibus (luz que entra pelos vidros e rebate)");
            Contraste = c.CreateEntry("Contraste", 0f, "-100 a 100");
            Saturacao = c.CreateEntry("Saturacao", -22f, "-100 a 100");
            Temperatura = c.CreateEntry("Temperatura", 0f, "-100 frio a 100 quente");
            Bloom = c.CreateEntry("Bloom", 0.22f, "Intensidade do bloom");
            BloomLimiar = c.CreateEntry("BloomLimiar", 1.8f, "Brilho a partir do qual há bloom");
            AO = c.CreateEntry("OclusaoAmbiente", true, "Oclusão de ambiente");
            AOIntensidade = c.CreateEntry("OclusaoIntensidade", 0.8f, "Intensidade da oclusão");
            SSR = c.CreateEntry("ReflexosTela", true, "Reflexos em tela (chão molhado, superfícies lisas)");
            Dof = c.CreateEntry("Profundidade", true, "Profundidade de campo: foco automático no que a câmera está olhando");
            DofAbertura = c.CreateEntry("ProfundidadeAbertura", 7.1f, "Abertura do diafragma em f/: menor desfoque mais forte (1.8 desfoque máximo, 22 tudo em foco)");
            DofFocal = c.CreateEntry("ProfundidadeFocal", 35f, "Distância focal da lente (mm): 24 a 50 para foto, acima de 85 teleobjetiva (mais desfoque)");

            Sonda = c.CreateEntry("SondaReflexo", true, "Sonda de reflexo em tempo real sobre o ônibus");
            SondaSalao = c.CreateEntry("SondaSalao", true, "Com a câmera dentro do ônibus, piso, colunas e painéis refletem o próprio interior (não o céu de cima do teto)");
            SondaResolucao = c.CreateEntry("SondaResolucao", 256, "128, 256 ou 512");
            SondaIntervalo = c.CreateEntry("SondaIntervalo", 0.5f, "Segundos entre atualizações da sonda (0.5 = cubo completo a cada meio segundo)");
            ReflexoPintura = c.CreateEntry("ReflexoPintura", true, "Liga reflexos nas pinturas dos veículos (vêm desligados)");
            BrilhoMax = c.CreateEntry("PinturaBrilhoMax", 0.75f, "Limite de smoothness das pinturas (1 = cromado; abaixo de ~0.6 a tinta fica fosca, com cara de papel)");

            LatariaBrilho = c.CreateEntry("LatariaBrilho", 0.9f, "Smoothness da lataria dos veículos (objetos _ref; o jogo usa 0.6, fosco). 0 = original");
            LatariaVerniz = c.CreateEntry("LatariaVerniz", 0.25f, "Reflexo extra da lataria imitando o verniz (metallic; 0 = tinta pura, acima de 0.3 parece cromado)");
            VidrosAtivo = c.CreateEntry("Vidros", true, "Reflexo e fumê nos vidros dos veículos");
            VidroSemSombra = c.CreateEntry("VidroSemSombra", true, "Vidros não bloqueiam o sol (o salão do ônibus fica iluminado como de verdade)");
            VidroReflexo = c.CreateEntry("VidroReflexo", 0.55f, "Reflexo visto de longe");
            VidroReflexoPerto = c.CreateEntry("VidroReflexoPerto", 0.12f, "Reflexo com a câmera colada no vidro");
            VidroReflexoDentro = c.CreateEntry("VidroReflexoDentro", 0.04f, "Reflexo visto de dentro do veículo");
            VidroVeu = c.CreateEntry("VidroVeu", 0.55f, "Quanto o vidro visto de longe esconde o interior");
            VidroVeuPerto = c.CreateEntry("VidroVeuPerto", 0.05f, "O mesmo, com a câmera colada no vidro");
            VidroTom = c.CreateEntry("VidroTom", 0.75f, "Luz que passa pelos vidros (1 = claro, 0.6 = fumê forte)");

            Volumetrica = c.CreateEntry("LuzVolumetrica", true, "Luz do sol no ar, recortada pelas sombras");
            VolumetricaForca = c.CreateEntry("LuzVolumetricaForca", 0.1f, "Densidade do ar iluminado");
            Neblina = c.CreateEntry("Neblina", 0.00012f, "Névoa de distância mínima em todos os climas (perspectiva aérea; 0.00012 = ar de cidade sem velar o que está perto)");
            CeuOclusao = c.CreateEntry("CeuOclusao", true, "Escurece a luz do céu e os reflexos em túneis, garagens e coberturas (mede quanto céu a câmera vê)");
            CeuOclusaoMin = c.CreateEntry("CeuOclusaoMin", 0.15f, "Luz do céu que sobra num lugar totalmente coberto");
            CeuOclusaoAlcance = c.CreateEntry("CeuOclusaoAlcance", 60f, "Distância (m) até onde um teto conta como cobertura");
            RaiosSol = c.CreateEntry("RaiosSol", false, "Raios radiais em tela do Enviro (vazam o céu pelas frestas: faixas amarelas e véu contra o sol)");
            RaiosSolForca = c.CreateEntry("RaiosSolForca", 0.5f, "Intensidade dos raios radiais");
            CreateRainEntries();
            CreateLightingEntries();
        }

        internal static MelonPreferences_Entry<bool> RainGlassEnabled, WiperActive, RainFallEnabled, RainCollision, LightingCustomDeferred, LightingPhysicalFalloff;
        internal static MelonPreferences_Entry<float> LightingRangeMultiplier, LightingBrightness, LightingStreetlightKelvin;
        internal static MelonPreferences_Entry<int> LightingShadowedStreetlights;
        internal static MelonPreferences_Entry<float[]> RainWindVelocity;
        internal static MelonPreferences_Entry<float> RainIntensity, RainGlobalWetness, RainAirHumidity, RainParticleDensity, RainTerminalVelocity, RainMotionBlurStretch;
        internal static MelonPreferences_Entry<int[]> RainGridResolution;
        internal static MelonPreferences_Entry<float> RainEvaporation, RainGravityWeight, RainWindWeight, RainContactAngle, RainRoughnessWet, RainIor,
            WiperSweepSpeed, WiperMinAngle, WiperMaxAngle, WiperBladeWidth, WiperAccumulation;

        // Seções e chaves com os nomes do schema RainSystemConfig (uma seção por bloco).
        static void CreateLightingEntries()
        {
            var l = MelonPreferences.CreateCategory("Lighting", "BetterGraphics: iluminação");
            LightingCustomDeferred = l.CreateEntry("custom_deferred", true, "Iluminação deferred própria (BRDF GGX com compensação de energia, queda física das luzes)");
            LightingPhysicalFalloff = l.CreateEntry("physical_falloff", true, "Luzes pontuais e spot caem pelo inverso do quadrado (desligado: queda da Unity)");
            LightingRangeMultiplier = l.CreateEntry("light_range_multiplier", 1.4f, "Multiplica o alcance das luzes: a cauda fraca da queda física chega ao poste vizinho");
            LightingBrightness = l.CreateEntry("light_brightness", 2f, "Brilho das luzes pontuais e spot (1 = igual ao da Unity a 40% do alcance original; 2 = calibrado contra fotos de rua à noite)");
            LightingStreetlightKelvin = l.CreateEntry("streetlight_kelvin", 4500f, "Cor dos postes do mapa em kelvin (4000 a 5000 = LED; 2000 = sódio laranja; 0 = cor original do mapa)");
            LightingShadowedStreetlights = l.CreateEntry("shadowed_streetlights", 2, "Quantos postes perto da câmera projetam sombra (cada um custa um mapa de sombra cúbico)");
        }

        static void CreateRainEntries()
        {
            var e = MelonPreferences.CreateCategory("Rain_Environment", "BetterGraphics: ambiente de chuva");
            RainIntensity = e.CreateEntry("rain_intensity", 0.85f, "Multiplica a intensidade da chuva do clima do jogo (1 = a do clima)");
            RainWindVelocity = e.CreateEntry("wind_velocity", new[] { 2.5f, -9.8f, 4.1f }, "Vento base (m/s) em x e z, multiplicado pela força do vento do clima e com rajadas; a queda vem de terminal_velocity (y não é usado)");
            RainGlobalWetness = e.CreateEntry("global_wetness", 0.9f, "Quanto as superfícies expostas molham (multiplica a umidade do chão)");
            RainAirHumidity = e.CreateEntry("air_humidity", 0.95f, "Umidade do ar: névoa e luz volumétrica mais densas na chuva");
            var f = MelonPreferences.CreateCategory("Rain_FallingDrops", "BetterGraphics: gotas caindo");
            RainFallEnabled = f.CreateEntry("enabled", true, "Chuva calculada na GPU com respingos (troca a chuva do Enviro)");
            RainParticleDensity = f.CreateEntry("particle_density", 5000f, "Gotas desenhadas por 1000 m³ de ar em volta da câmera, na chuva mais forte");
            RainTerminalVelocity = f.CreateEntry("terminal_velocity", 12f, "Velocidade de queda (m/s)");
            RainMotionBlurStretch = f.CreateEntry("motion_blur_stretch", 2.5f, "Comprimento do risco de cada gota (borrão de movimento)");
            RainCollision = f.CreateEntry("collision_with_objects", true, "A gota para em telhados, marquises e no ônibus (mapa de abrigo); respingos só onde a chuva bate");
            var g = MelonPreferences.CreateCategory("Rain_WindshieldGrid", "BetterGraphics: água nos vidros do ônibus");
            RainGlassEnabled = g.CreateEntry("enabled", true, "Gotas simuladas no para-brisa, película de água e gotas nas janelas (troca as manchas paradas do jogo)");
            RainGridResolution = g.CreateEntry("texture_resolution", new[] { 256, 256 }, "Células da grade da película no para-brisa (largura, altura)");
            RainEvaporation = g.CreateEntry("evaporation_rate", 0.02f, "Fração da película que seca por segundo; sem chuva, as gotas também secam nesse ritmo");
            RainGravityWeight = g.CreateEntry("gravity_weight", 0.5f, "Peso da gravidade no escorrimento (0.5 = calibrado)");
            RainWindWeight = g.CreateEntry("wind_deflection_weight", 0.8f, "Peso do vento da velocidade do ônibus, que empurra as gotas para cima e para os lados (0.8 = calibrado)");
            var s = MelonPreferences.CreateCategory("Rain_WindshieldGrid_SurfaceProperties", "BetterGraphics: superfície do vidro molhado");
            RainContactAngle = s.CreateEntry("contact_angle", 75f, "Ângulo de contato da água (graus): 75 vidro limpo; 100 a 120 encerado (gota redonda que escorrega); 30 a 50 sujo (achatada, gruda)");
            RainRoughnessWet = s.CreateEntry("roughness_wet", 0.05f, "Aspereza da superfície da água (borra o reflexo do céu nas gotas)");
            RainIor = s.CreateEntry("ior", 1.33f, "Índice de refração da água");
            var w = MelonPreferences.CreateCategory("Rain_WiperSystem", "BetterGraphics: limpador de para-brisa");
            WiperActive = w.CreateEntry("is_active", true, "A palheta do limpador limpa o vidro (segue a palheta do modelo; sem ela, um limpador virtual com os valores abaixo)");
            WiperSweepSpeed = w.CreateEntry("sweep_speed", 1.5f, "Limpador virtual: ciclos de ida e volta por segundo dividido por 2 (o rápido é 1.6 vez isso)");
            WiperMinAngle = w.CreateEntry("min_angle", -45f, "Limpador virtual: ângulo de parada a partir da vertical (graus)");
            WiperMaxAngle = w.CreateEntry("max_angle", 45f, "Limpador virtual: ângulo no fim da varrida (graus)");
            WiperBladeWidth = w.CreateEntry("blade_width", 0.03f, "Largura da faixa que a palheta limpa (m)");
            WiperAccumulation = w.CreateEntry("accumulation_factor", 1.2f, "Quanto da água varrida se junta na ponta da palheta e escorre pela borda do arco");
        }

        // Para o Lab: muda uma preferência pelo nome.
        internal static bool Definir(string nome, string valor)
        {
            // "Seção.chave" muda uma preferência de outra seção (ex.: Lighting.light_range_multiplier).
            int ponto = nome.IndexOf('.');
            var e = ponto > 0 ? MelonPreferences.GetCategory(nome.Substring(0, ponto))?.GetEntry(nome.Substring(ponto + 1)) : Cat.GetEntry(nome);
            if (e == null) return false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (e)
            {
                case MelonPreferences_Entry<bool> b: b.Value = valor == "1" || valor.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case MelonPreferences_Entry<float> f: f.Value = float.Parse(valor, inv); break;
                case MelonPreferences_Entry<int> i: i.Value = int.Parse(valor, inv); break;
                case MelonPreferences_Entry<string> s: s.Value = valor; break;
                default: return false;
            }
            return true;
        }
    }
}
