using System;
using UnityEngine;

namespace BetterGraphics
{
    // Ônibus do jogador e a caixa da carroceria dele (no espaço local).
    //  - A raiz não vem da câmera: a câmera livre se desprende do ônibus. A sonda própria do ônibus
    //    (refProbeReflexosBus) e a câmera do GPS ficam sempre nele.
    //  - A caixa mede só a carroceria (pintura "matvazio" e vidros), sem filtro de camada (na cabine o
    //    jogo esconde a camada da carroceria) e sem a seta do GPS que flutua sobre o teto.
    internal static class Veiculo
    {
        static Transform _raiz, _medido;
        static Bounds _caixa = new Bounds(new Vector3(0f, 1.6f, 0f), new Vector3(2.5f, 3.2f, 12f));
        static float _proximaBusca, _proximaMedida;

        internal static void CenaMudou() { _raiz = null; _medido = null; _proximaBusca = 0f; _proximaMedida = 0f; }

        internal static Transform Raiz()
        {
            if (_raiz != null) return _raiz;
            if (Time.unscaledTime < _proximaBusca) return null;
            _proximaBusca = Time.unscaledTime + 1f;
            // A câmera do GPS só existe no ônibus do jogador (os do tráfego usam o mesmo modelo, com a
            // mesma sonda refProbeReflexosBus, mas sem GPS).
            foreach (var c in Camera.allCameras)
                if (c.name == "camGPS") return _raiz = c.transform.root;
            var cam = Camera.main;
            if (cam != null && cam.transform.parent != null)
                foreach (var p in cam.transform.root.GetComponentsInChildren<ReflectionProbe>(true))
                    if (p.name.StartsWith("refProbeReflexosBus", StringComparison.Ordinal)) return _raiz = cam.transform.root;
            return null;
        }

        internal static Bounds Caixa
        {
            get { Medir(); return _caixa; }
        }

        static void Medir()
        {
            if (_medido != null && _medido != _raiz) _medido = null;
            var bus = Raiz();
            if (bus == null || bus == _medido || Time.unscaledTime < _proximaMedida) return;
            bool tem = false; var b = new Bounds();
            foreach (var r in bus.GetComponentsInChildren<Renderer>())
            {
                if (!Carroceria(r)) continue;
                var rb = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var canto = new Vector3((i & 1) == 0 ? rb.min.x : rb.max.x, (i & 2) == 0 ? rb.min.y : rb.max.y, (i & 4) == 0 ? rb.min.z : rb.max.z);
                    var l = bus.InverseTransformPoint(canto);
                    if (!tem) { b = new Bounds(l, Vector3.zero); tem = true; } else b.Encapsulate(l);
                }
            }
            if (!tem) { _proximaMedida = Time.unscaledTime + 2f; return; }
            _caixa = b; _medido = bus;
            Log.Msg($"Ônibus: {bus.name}, carroceria centro {b.center} tamanho {b.size}");
        }

        internal static bool Carroceria(Renderer r)
        {
            if (!r.enabled || r.name.StartsWith("setaGPS", StringComparison.Ordinal) || r.bounds.size.magnitude > 25f) return false;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                if (m.name.StartsWith("matvazio", StringComparison.Ordinal)) return true;
                var sh = m.shader != null ? m.shader.name : "";
                if (sh == "Glass Reflective" || sh.StartsWith("OneClick Glass", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        internal static Vector3 Centro()
        {
            var bus = Raiz();
            return bus != null ? bus.TransformPoint(Caixa.center) : Vector3.zero;
        }

        // Ponto 1 m acima do teto (ar-condicionado incluso).
        internal static Vector3 AcimaDoTeto()
        {
            var bus = Raiz();
            var c = Caixa;
            return bus.TransformPoint(c.center) + Vector3.up * (c.extents.y + 1f);
        }

        // Câmera dentro da carroceria (cabine ou salão). Laterais encolhidas: retrovisores ficam fora.
        internal static bool Dentro(Vector3 mundo)
        {
            var bus = Raiz();
            if (bus == null) return false;
            var c = Caixa;
            var p = bus.InverseTransformPoint(mundo);
            float mx = Mathf.Min(0.45f, c.extents.x * 0.3f);
            return Mathf.Abs(p.x - c.center.x) < c.extents.x - mx && p.y > c.min.y && p.y < c.max.y
                && Mathf.Abs(p.z - c.center.z) < c.extents.z - 0.3f;
        }

        internal static float Distancia(Vector3 mundo)
        {
            var bus = Raiz();
            if (bus == null) return 999f;
            return Mathf.Sqrt(Caixa.SqrDistance(bus.InverseTransformPoint(mundo)));
        }
    }
}
