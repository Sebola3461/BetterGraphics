using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BetterGraphics
{
    // Shaders próprios do mod, compilados no projeto Unity em Shaders/ e embutidos na DLL
    // (Recursos/bettergraphics.shaders). O IL2CPP do jogo removeu a classe AssetBundle do C#, mas as
    // funções nativas continuam no motor: o pacote é aberto e lido por elas, e o objeto devolvido é
    // tratado só como ponteiro.
    internal static unsafe class Pacote
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr DAbrir(IntPtr bytes, uint crc);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr DCarregar(IntPtr pacote, IntPtr nome, IntPtr tipo);

        static IntPtr _pacote;
        static bool _tentou;

        internal static bool Disponivel => Abrir();

        static bool Abrir()
        {
            if (_pacote != IntPtr.Zero) return true;
            if (_tentou) return false;
            _tentou = true;
            try
            {
                using var s = typeof(Pacote).Assembly.GetManifestResourceStream("BetterGraphics.shaders");
                if (s == null) { Log.Aviso("Pacote de shaders não embutido na DLL"); return false; }
                var bytes = new byte[s.Length];
                for (int lido = 0, n; lido < bytes.Length && (n = s.Read(bytes, lido, bytes.Length - lido)) > 0;) lido += n;
                var abrir = Funcao<DAbrir>("UnityEngine.AssetBundle::LoadFromMemory_Internal");
                if (abrir == null) return false;
                Il2CppStructArray<byte> arr = bytes;
                _pacote = abrir(arr.Pointer, 0);
                if (_pacote == IntPtr.Zero) { Log.Aviso("Pacote de shaders: a Unity recusou o arquivo"); return false; }
                Log.Msg($"Pacote de shaders aberto ({bytes.Length / 1024} KB)");
                return true;
            }
            catch (Exception e) { Log.Aviso("Pacote de shaders: " + e.Message); return false; }
        }

        // nome: arquivo do shader no projeto, sem pasta e extensão (Interior -> assets/shaders/interior.shader).
        internal static Shader Shader(string nome)
        {
            if (!Abrir()) return null;
            var carregar = Funcao<DCarregar>("UnityEngine.AssetBundle::LoadAsset_Internal");
            if (carregar == null) return null;
            IntPtr p = carregar(_pacote, IL2CPP.ManagedStringToIl2Cpp($"assets/shaders/{nome.ToLowerInvariant()}.shader"), Il2CppType.Of<Shader>().Pointer);
            if (p == IntPtr.Zero) { Log.Aviso($"Shader {nome} não está no pacote"); return null; }
            var sh = new Shader(p);
            if (!sh.isSupported) { Log.Aviso($"Shader {nome} não roda nesta placa/API"); return null; }
            return sh;
        }

        static T Funcao<T>(string nome) where T : Delegate
        {
            var p = IL2CPP.il2cpp_resolve_icall(nome);
            if (p == IntPtr.Zero) { Log.Aviso("Função nativa não encontrada: " + nome); return null; }
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
    }
}
