using UnityEngine;
using UnityEngine.Rendering;

namespace BetterGraphics
{
    // Névoa por perspectiva aérea (shader AerialFog, passe 1) na câmera do jogo, antes dos efeitos de imagem opacos: as
    // nuvens e a luz volumétrica do Enviro ficam por cima. A tabela (passe 0) é feita pela PhysicalSky junto com o céu.
    internal static class AerialFog
    {
        static Material _mat;
        static Mesh _screen;
        static CommandBuffer _cb;
        static Camera _cbCam;
        const CameraEvent Event = CameraEvent.BeforeImageEffectsOpaque;

        internal static Material Material => _mat;

        internal static bool Prepare()
        {
            if (_mat != null) return true;
            var sh = Pacote.Shader("AerialFog");
            if (sh == null) return false;
            _mat = new Material(sh) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            return true;
        }

        internal static void Frame(bool on)
        {
            var cam = Pos.Cam;
            if (!on || _mat == null || cam == null || cam.actualRenderingPath != RenderingPath.DeferredShading) { Remove(); return; }
            Attach(cam);
            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            _mat.SetMatrix("_BG_FogInvViewProj", vp.inverse);
        }

        static void Attach(Camera cam)
        {
            if (cam == _cbCam && _cb != null) return;
            Remove();
            if (_screen == null)
            {
                _screen = new Mesh { hideFlags = HideFlags.DontUnloadUnusedAsset };
                _screen.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(3f, -1f, 0f), new Vector3(-1f, 3f, 0f) };
                _screen.triangles = new[] { 0, 1, 2 };
                _screen.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            }
            _cb = new CommandBuffer { name = "BetterGraphics AerialFog" };
            _cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _cb.DrawMesh(_screen, Matrix4x4.identity, _mat, 0, 1);
            cam.AddCommandBuffer(Event, _cb);
            _cbCam = cam;
        }

        internal static void Remove()
        {
            if (_cb != null && _cbCam != null) try { _cbCam.RemoveCommandBuffer(Event, _cb); } catch { }
            _cb = null; _cbCam = null;
        }
    }
}
