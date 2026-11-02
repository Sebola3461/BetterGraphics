using System.IO;
using UnityEditor;
using UnityEngine.Rendering;

// Gera o pacote de shaders que o mod embute na DLL (Recursos/bettergraphics.shaders).
// Uso: Unity.exe -batchmode -nographics -quit -projectPath Shaders -executeMethod Construir.Pacote
public static class Construir
{
    public static void Pacote()
    {
        string saida = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Recursos"));
        Directory.CreateDirectory(saida);
        // O jogo abre em DX11 por padrão, mas os atalhos dele também usam OpenGL e Vulkan: o pacote leva as três.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
            new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore, GraphicsDeviceType.Vulkan });
        var arquivos = Directory.GetFiles("Assets/Shaders", "*.shader");
        for (int i = 0; i < arquivos.Length; i++) arquivos[i] = arquivos[i].Replace('\\', '/');
        var pacote = new AssetBundleBuild { assetBundleName = "bettergraphics.shaders", assetNames = arquivos };
        BuildPipeline.BuildAssetBundles(saida, new[] { pacote }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        // Sobra do BuildAssetBundles que o mod não usa.
        foreach (var f in new[] { "Recursos", "Recursos.manifest", "bettergraphics.shaders.manifest" })
            if (File.Exists(Path.Combine(saida, f))) File.Delete(Path.Combine(saida, f));
    }
}
