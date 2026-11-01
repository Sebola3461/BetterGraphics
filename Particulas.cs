using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BetterGraphics
{
    // Ajustes de ParticleSystem pelas funções nativas da Unity. Os módulos (main, emission, shape,
    // collision) são structs que guardam só o ponteiro do sistema; o Il2CppInterop os representa como
    // classes e os setters estouram (ObjectCollectedException), e boa parte deles nem existe no build
    // (removidos pelo IL2CPP). Aqui o "módulo" é montado à mão (um ponteiro na pilha) e a função interna
    // é chamada direto.
    internal static unsafe class Particulas
    {
        // ParticleSystem.MinMaxCurve na memória: modo, multiplicador, curvas (ponteiros), constantes.
        [StructLayout(LayoutKind.Sequential)]
        struct Curva { public int modo; public float mult; public IntPtr curvaMin, curvaMax; public float min, max; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DBool(IntPtr modulo, byte v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DInt(IntPtr modulo, int v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DFloat(IntPtr modulo, float v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DCurva(IntPtr modulo, Curva* c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DVetor(IntPtr modulo, Vector3* v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DMascara(IntPtr modulo, int* m);

        static DBool _emissao, _loop, _forma, _colisao;
        static DInt _espaco, _max, _tipoForma, _tipoColisao, _modoColisao, _qualidade;
        static DFloat _gravidade, _perdaVida, _raioColisao;
        static DCurva _taxa, _velocidade, _tamanho, _vida;
        static DVetor _escalaForma;
        static DMascara _colideCom;
        static bool _resolvido;

        static T R<T>(string nome) where T : Delegate
        {
            try
            {
                var p = IL2CPP.il2cpp_resolve_icall(nome);
                if (p == IntPtr.Zero) { Log.Aviso("Partículas: função nativa não encontrada: " + nome); return null; }
                return Marshal.GetDelegateForFunctionPointer<T>(p);
            }
            catch (Exception e) { Log.Aviso($"Partículas: {nome}: {e.Message}"); return null; }
        }

        static void Resolver()
        {
            if (_resolvido) return;
            _resolvido = true;
            const string P = "UnityEngine.ParticleSystem/";
            _emissao = R<DBool>(P + "EmissionModule::set_enabled_Injected");
            _taxa = R<DCurva>(P + "EmissionModule::set_rateOverTime_Injected");
            _loop = R<DBool>(P + "MainModule::set_loop_Injected");
            _espaco = R<DInt>(P + "MainModule::set_simulationSpace_Injected");
            _max = R<DInt>(P + "MainModule::set_maxParticles_Injected");
            _gravidade = R<DFloat>(P + "MainModule::set_gravityModifierMultiplier_Injected");
            _velocidade = R<DCurva>(P + "MainModule::set_startSpeed_Injected");
            _tamanho = R<DCurva>(P + "MainModule::set_startSize_Injected");
            _vida = R<DCurva>(P + "MainModule::set_startLifetime_Injected");
            _forma = R<DBool>(P + "ShapeModule::set_enabled_Injected");
            _tipoForma = R<DInt>(P + "ShapeModule::set_shapeType_Injected");
            _escalaForma = R<DVetor>(P + "ShapeModule::set_scale_Injected");
            _colisao = R<DBool>(P + "CollisionModule::set_enabled_Injected");
            _tipoColisao = R<DInt>(P + "CollisionModule::set_type_Injected");
            _modoColisao = R<DInt>(P + "CollisionModule::set_mode_Injected");
            _qualidade = R<DInt>(P + "CollisionModule::set_quality_Injected");
            _perdaVida = R<DFloat>(P + "CollisionModule::set_lifetimeLossMultiplier_Injected");
            _raioColisao = R<DFloat>(P + "CollisionModule::set_radiusScale_Injected");
            _colideCom = R<DMascara>(P + "CollisionModule::set_collidesWith_Injected");
        }

        static IntPtr M(ParticleSystem ps, IntPtr* buf) { *buf = ps.Pointer; return (IntPtr)buf; }

        // Emissão automática desligada (o mod emite com Emit), mundo como espaço, laço, máximo e gravidade.
        internal static void Configurar(ParticleSystem ps, int max, float gravidade)
        {
            Resolver();
            IntPtr b; var m = M(ps, &b);
            _emissao?.Invoke(m, 0);
            _loop?.Invoke(m, 1);
            _espaco?.Invoke(m, (int)ParticleSystemSimulationSpace.World);
            _max?.Invoke(m, max);
            _gravidade?.Invoke(m, gravidade);
        }

        // Emissão nativa (sem custo de CPU por partícula): taxa por segundo, caixa como forma, velocidade,
        // tamanho e vida sorteados entre dois valores.
        internal static void Emissor(ParticleSystem ps, int max, Vector3 caixa, float velMin, float velMax, float tamMin, float tamMax, float vidaMin, float vidaMax)
        {
            Resolver();
            IntPtr b; var m = M(ps, &b);
            _loop?.Invoke(m, 1);
            _espaco?.Invoke(m, (int)ParticleSystemSimulationSpace.World);
            _max?.Invoke(m, max);
            _gravidade?.Invoke(m, 0f);
            _emissao?.Invoke(m, 1);
            _forma?.Invoke(m, 1);
            _tipoForma?.Invoke(m, (int)ParticleSystemShapeType.Box);
            var c = caixa; _escalaForma?.Invoke(m, &c);
            Curva k;
            k = DoisValores(velMin, velMax); _velocidade?.Invoke(m, &k);
            k = DoisValores(tamMin, tamMax); _tamanho?.Invoke(m, &k);
            k = DoisValores(vidaMin, vidaMax); _vida?.Invoke(m, &k);
        }

        internal static void Caixa(ParticleSystem ps, Vector3 tamanho)
        {
            Resolver();
            IntPtr b; var m = M(ps, &b);
            var v = tamanho; _escalaForma?.Invoke(m, &v);
        }

        internal static void Taxa(ParticleSystem ps, float porSegundo)
        {
            Resolver();
            IntPtr b; var m = M(ps, &b);
            var k = DoisValores(porSegundo, porSegundo); _taxa?.Invoke(m, &k);
        }

        internal static void Vida(ParticleSystem ps, float min, float max)
        {
            Resolver();
            IntPtr b; var m = M(ps, &b);
            var k = DoisValores(min, max); _vida?.Invoke(m, &k);
        }

        // Partícula morre ao bater em qualquer colisor (teto do ônibus, coberturas, prédios).
        internal static void MorrerAoColidir(ParticleSystem ps)
        {
            Resolver();
            IntPtr b; var m = M(ps, &b);
            _colisao?.Invoke(m, 1);
            _tipoColisao?.Invoke(m, (int)ParticleSystemCollisionType.World);
            _modoColisao?.Invoke(m, (int)ParticleSystemCollisionMode.Collision3D);
            _qualidade?.Invoke(m, (int)ParticleSystemCollisionQuality.Low);
            _perdaVida?.Invoke(m, 1f);
            _raioColisao?.Invoke(m, 0.1f);
            int todas = ~0; _colideCom?.Invoke(m, &todas);
        }

        // Qualidade da colisão: High testa cada partícula contra os colisores de verdade (inclusive os que se
        // movem); Low usa um cache de vóxeis que não acompanha colisor em movimento (a laje do teto do ônibus).
        internal static void QualidadeColisao(ParticleSystem ps, bool alta)
        {
            Resolver();
            IntPtr b; var m = M(ps, &b);
            _qualidade?.Invoke(m, (int)(alta ? ParticleSystemCollisionQuality.High : ParticleSystemCollisionQuality.Low));
        }

        static Curva DoisValores(float a, float b) => new Curva { modo = (int)ParticleSystemCurveMode.TwoConstants, mult = 1f, min = a, max = b };
    }
}
