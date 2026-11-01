using System;
using UnityEngine;

namespace BetterGraphics
{
    // Quanto do céu a câmera enxerga. A luz ambiente do Unity (e os reflexos do céu) não sabe de teto:
    // dentro de um túnel, garagem ou embaixo da cobertura de um terminal tudo ficava iluminado como a
    // céu aberto. Raios de física num hemisfério para cima (distribuição cosseno, como a luz difusa
    // chega) medem a fração livre; o próprio ônibus do jogador é ignorado (a cabine tem ajuste próprio).
    // Poucos raios por quadro, com a média suavizada no tempo.
    internal static class Ceu
    {
        const int N = 32;
        const int PorQuadro = 4;
        static readonly Vector3[] _dir = Criar();
        static readonly bool[] _livre = Todos();
        static bool[] Todos() { var a = new bool[N]; for (int i = 0; i < N; i++) a[i] = true; return a; }
        static int _i, _quadro = -1;
        static float _vis = 1f, _suave = 1f;

        static Vector3[] Criar()
        {
            // Espiral de Fibonacci no disco, projetada no hemisfério: densidade proporcional ao cosseno.
            var d = new Vector3[N];
            float ouro = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < N; i++)
            {
                float r = Mathf.Sqrt((i + 0.5f) / N);
                float a = i * ouro;
                float x = r * Mathf.Cos(a), z = r * Mathf.Sin(a);
                d[i] = new Vector3(x, Mathf.Sqrt(Mathf.Max(0f, 1f - r * r)), z);
            }
            return d;
        }

        internal static float Visibilidade => _suave;

        // Multiplicador da luz do céu (1 a céu aberto, CeuOclusaoMin num túnel fechado).
        internal static float Fator()
        {
            if (!Cfg.CeuOclusao.Value) return 1f;
            Atualizar();
            return Mathf.Lerp(Cfg.CeuOclusaoMin.Value, 1f, _suave);
        }

        static void Atualizar()
        {
            if (_quadro == Time.frameCount) return; // Aplicar roda mais de uma vez por quadro
            _quadro = Time.frameCount;
            var cam = Pos.Cam;
            if (cam == null) return;
            var origem = cam.transform.position;
            var bus = Veiculo.Raiz();
            float alcance = Cfg.CeuOclusaoAlcance.Value;
            for (int k = 0; k < PorQuadro; k++)
            {
                _livre[_i] = !Bloqueado(origem, _dir[_i], alcance, bus);
                _i = (_i + 1) % N;
            }
            int livres = 0;
            for (int j = 0; j < N; j++) if (_livre[j]) livres++;
            _vis = livres / (float)N;
            // Entrar no túnel escurece em ~0,7 s; a exposição automática faz o resto da adaptação.
            _suave = Mathf.MoveTowards(_suave, _vis, Time.unscaledDeltaTime * 1.5f);
        }

        static bool Bloqueado(Vector3 origem, Vector3 dir, float alcance, Transform bus)
        {
            var hits = Physics.RaycastAll(origem, dir, alcance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i].collider;
                if (c == null) continue;
                if (bus != null && c.transform.root == bus) continue;
                return true;
            }
            return false;
        }
    }
}
