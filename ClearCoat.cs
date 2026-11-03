using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterGraphics
{
    // Verniz da lataria: um material a mais em cada renderer "_ref" desenha a película lisa por cima da tinta
    // (shader ClearCoat). O material do veículo não é trocado: cada mod de ônibus ou carro traz o seu.
    internal static class ClearCoat
    {
        static Shader _shader;
        static bool _noShader;
        static Material _mat;
        static readonly Dictionary<IntPtr, Renderer> _renderers = new();
        static bool _wasOn = true;

        // Com o verniz ativo a tinta embaixo é dielétrica e fosca; sem o shader vale o truque antigo da Reflexos.
        internal static bool Active => Cfg.LightingClearCoat.Value && !_noShader;

        internal static void Apply(Renderer r)
        {
            if (!Cfg.LightingClearCoat.Value || _noShader || _renderers.ContainsKey(r.Pointer)) return;
            if (_mat == null)
            {
                _shader = Pacote.Shader("ClearCoat");
                if (_shader == null) { _noShader = true; return; }
                _mat = new Material(_shader) { name = "BetterGraphics_ClearCoat", hideFlags = HideFlags.DontUnloadUnusedAsset };
            }
            var mats = r.sharedMaterials;
            // O material extra redesenha o último submesh: só cobre a lataria inteira quando ela tem um só.
            if (mats.Length != 1 || SubMeshes(r) > 1) return;
            r.sharedMaterials = new[] { mats[0], _mat };
            _renderers[r.Pointer] = r;
        }

        static int SubMeshes(Renderer r)
        {
            if (r.TryCast<SkinnedMeshRenderer>() is { } skin) return skin.sharedMesh != null ? skin.sharedMesh.subMeshCount : 1;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null && mf.sharedMesh != null ? mf.sharedMesh.subMeshCount : 1;
        }

        internal static void Frame()
        {
            bool on = Cfg.LightingClearCoat.Value;
            // Religado no menu: a Varredura passa de novo pelos renderers para pôr a camada de volta.
            if (on && !_wasOn) Varredura.Reiniciar();
            _wasOn = on;
            if (_mat == null) return;
            if (!on) { if (_renderers.Count > 0) Restore(); return; }
            _mat.SetFloat("_BG_CoatSmoothness", Mathf.Clamp01(Cfg.LightingClearCoatSmoothness.Value));
            _mat.SetFloat("_BG_CoatStrength", Mathf.Clamp01(Cfg.LightingClearCoatStrength.Value));
        }

        internal static void Restore()
        {
            foreach (var r in _renderers.Values)
            {
                try
                {
                    if (r == null) continue;
                    var keep = new List<Material>();
                    foreach (var m in r.sharedMaterials) if (m != null && m != _mat) keep.Add(m);
                    r.sharedMaterials = keep.ToArray();
                }
                catch { }
            }
            _renderers.Clear();
        }

        internal static void SceneChanged() => _renderers.Clear();

        internal static string State() =>
            $"verniz: {(_noShader ? "sem shader" : Active ? "ativo" : "desligado")}, {_renderers.Count} renderers, lisura {Cfg.LightingClearCoatSmoothness.Value:0.00}, força {Cfg.LightingClearCoatStrength.Value:0.00}, base {Cfg.LightingPaintBaseSmoothness.Value:0.00}";
    }
}
