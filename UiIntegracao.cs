using System;
using MelonLoader;

[assembly: MelonAdditionalDependencies("PBSLoaderCommonUtils", "PBSLoaderCommonInterface")]

namespace BetterGraphics
{
    // Coloca o mod no menu da PBSLoaderCommonInterface (ações sem atalho de teclado e sliders com faixa). Ela é base do PBSLoader,
    // sempre instalada junto com a PBSLoaderCommonUtils.
    internal static class UiIntegracao
    {
        internal static void Registrar()
        {
            try { Fazer(); }
            catch (Exception e) { MelonLogger.Warning("Integração com a PBSLoaderCommonInterface falhou: " + e.Message); }
        }

        static void Fazer()
        {
            const string C = "Realismo";
            PBSLoaderCommonInterface.PainelMods.Nome(C, "GráficosPlus");
            PBSLoaderCommonInterface.PainelMods.Acao(C, "Ligar ou desligar", () => BetterGraphicsMod.Alternar(!Cfg.Ativo.Value), "Compare com os gráficos originais");
            PBSLoaderCommonInterface.PainelMods.Acao(C, "Gravar diagnóstico", Diag.Gravar, "UserData/BetterGraphics_diagnostico.txt");
            PBSLoaderCommonInterface.PainelMods.Faixa(C, "MateriaisDoMapaBrilho", 0f, 1f, 0.05f);
            PBSLoaderCommonInterface.PainelMods.Faixa(C, "ChaoBrilhoSeco", 0f, 1f, 0.05f);
            PBSLoaderCommonInterface.PainelMods.Faixa(C, "ChaoBrilhoMolhado", 0f, 1f, 0.05f);
            PBSLoaderCommonInterface.PainelMods.Faixa(C, "ChaoBaseUmida", 0f, 1f, 0.02f);
            PBSLoaderCommonInterface.PainelMods.Faixa(C, "ProfundidadeAbertura", 1.8f, 16f, 0.1f);
            PBSLoaderCommonInterface.PainelMods.Faixa(C, "ProfundidadeFocal", 24f, 85f, 1f);
            PBSLoaderCommonInterface.PainelMods.Faixa(C, "ChuvaFrio", 0f, 30f, 1f);
        }
    }
}
