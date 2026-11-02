using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterGraphics
{
    // Umidade da chuva em toda a cena por pixel (shader Molhado): calçadas, prédios, veículos e o próprio
    // ônibus escurecem e brilham, e o plano forma poças com ondulações. O Chao continua cuidando dos
    // mapas de poças do asfalto do mapa; o escurecimento passa a ser só daqui.
    //  - Abrigo: um mapa visto de cima guarda a altura do que está mais alto em volta da câmera principal
    //    (shader Altura). Embaixo de telhado, túnel ou abrigo não molha.
    //  - Passe na câmera antes dos reflexos: copia o G-buffer e reescreve albedo, smoothness, normal e a
    //    luz ambiente já somada. Sonda e SSR depois refletem com os valores novos.
    // Seco, o passe e o mapa saem (custo zero).
    internal static class Molhado
    {

        static Material _mat;
        static Shader _altura;
        static Mesh _tela;
        static CommandBuffer _cb;
        static Camera _camCb;
        static bool _semShader;

        static int _debug;

        internal static bool Ativo => _cb != null;

        internal static void CenaMudou() { Tirar(); _mapaPronto = false; }

        internal static void Manter()
        {
            float umidade = Chao.Umidade;
            if (!Cfg.ChuvaGlobal.Value || umidade < 0.01f) { Tirar(); return; }
            if (_semShader) return;
            if (_mat == null)
            {
                var sh = Pacote.Shader("Molhado");
                _altura = Pacote.Shader("Altura");
                if (sh == null || _altura == null) { _semShader = true; return; }
                _mat = new Material(sh) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            }
            Pendurar(Pos.Cam);
        }

        static readonly int G0 = Shader.PropertyToID("_BG_G0"), G1 = Shader.PropertyToID("_BG_G1"), G2 = Shader.PropertyToID("_BG_G2"), L = Shader.PropertyToID("_BG_L");

        static void Pendurar(Camera cam)
        {
            if (cam == _camCb && _cb != null) return;
            Tirar();
            if (cam == null || cam.actualRenderingPath != RenderingPath.DeferredShading) return;
            _tela ??= Tela();
            _cb = new CommandBuffer { name = "BetterGraphics Molhado" };
            // Cópias com nome de propriedade: a Unity já as expõe ao shader como texturas globais.
            // Albedo e especular em float linear: com cópia sRGB de 8 bits o valor voltava codificado duas vezes
            // (tudo clareava) e o escuro ganhava faixas.
            _cb.GetTemporaryRT(G0, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _cb.GetTemporaryRT(G1, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _cb.GetTemporaryRT(G2, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGB2101010, RenderTextureReadWrite.Linear);
            _cb.GetTemporaryRT(L, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _cb.Blit(BuiltinRenderTextureType.GBuffer0, G0);
            _cb.Blit(BuiltinRenderTextureType.GBuffer1, G1);
            _cb.Blit(BuiltinRenderTextureType.GBuffer2, G2);
            _cb.Blit(BuiltinRenderTextureType.CameraTarget, L);
            RenderTargetIdentifier[] alvos = { BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.GBuffer1, BuiltinRenderTextureType.GBuffer2, BuiltinRenderTextureType.CameraTarget };
            _cb.SetRenderTarget(alvos, BuiltinRenderTextureType.CameraTarget);
            _cb.DrawMesh(_tela, Matrix4x4.identity, _mat, 0, 0);
            _cb.ReleaseTemporaryRT(G0); _cb.ReleaseTemporaryRT(G1); _cb.ReleaseTemporaryRT(G2); _cb.ReleaseTemporaryRT(L);
            cam.AddCommandBuffer(CameraEvent.BeforeReflections, _cb);
            _camCb = cam;
        }

        internal static Mesh Tela()
        {
            var m = new Mesh { hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(3f, -1f, 0f), new Vector3(-1f, 3f, 0f) };
            m.triangles = new[] { 0, 1, 2 };
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return m;
        }

        static void Tirar()
        {
            if (_cb != null && _camCb != null) try { _camCb.RemoveCommandBuffer(CameraEvent.BeforeReflections, _cb); } catch { }
            _cb = null; _camCb = null;
        }

        internal static void Restaurar() => Tirar();

        // Todo quadro (LateUpdate).
        internal static void Quadro()
        {
            if (_cb == null || _camCb == null || _mat == null) return;
            var cam = _camCb;
            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            _mat.SetMatrix("_BG_InvVP", vp.inverse);
            _mat.SetFloat("_BG_Umidade", Mathf.Sqrt(Chao.Umidade)); // escurece e brilha logo que começa a chover
            _mat.SetFloat("_BG_Chuva", Chuva.Intensidade());
            _mat.SetFloat("_BG_Debug", _debug);
            bool temOnibus = Interior.Grade(out var grade);
            _mat.SetFloat("_BG_TemOnibus", temOnibus ? 1f : 0f);
            if (temOnibus) _mat.SetMatrix("_BG_OnibusGrade", grade);
            Abrigo(cam);
        }

        // ---- mapa de abrigo ----
        // Desenhado sem câmera: todo render de câmera custava ~7 ms fixos (callbacks globais de câmera do
        // jogo e dos efeitos), mesmo sem nada para desenhar. Um CommandBuffer próprio desenha só os
        // candidatos da área, vistos de cima com projeção ortográfica.
        const float Lado = 160f;    // área coberta (m), centrada na câmera
        const int Resolucao = 1024;

        static RenderTexture _mapa;
        static CommandBuffer _cbMapa;
        static Material _matAltura;
        static Vector2 _centroMapa;
        static bool _mapaPronto;
        static float _proximoMapa, _proximaLista;

        // Caixa (xz) e submalhas guardadas na lista: ler r.bounds e sharedMaterials a cada desenho eram
        // chamadas ao IL2CPP por objeto (5 ms por mapa).
        struct Candidato { public Renderer r; public float x0, x1, z0, z1; public int sub; }
        static List<Candidato> _candidatos = new(), _novos = new();
        static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> _todos;
        static int _lidos;

        // Entra no mapa o que abriga da chuva: opacos, e dos transparentes só vidro e
        // policarbonato (têm _Color e não _TintColor). Partículas, rastros, brilhos de poste e o domo do
        // céu ficam de fora. A lista é refeita a cada 10 s, 150 renderers por quadro.
        static void Listar(int mascara)
        {
            if (_todos == null)
            {
                if (Time.unscaledTime < _proximaLista) return;
                _todos = Object.FindObjectsOfType<Renderer>();
                _lidos = 0; _novos.Clear();
            }
            int fim = System.Math.Min(_todos.Length, _lidos + 150);
            for (; _lidos < fim; _lidos++)
            {
                var r = _todos[_lidos];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                // Só o que a câmera principal vê (a seta do GPS, só do minimapa, flutua sobre o ônibus).
                if ((mascara & (1 << r.gameObject.layer)) == 0) continue;
                if (r.TryCast<ParticleSystemRenderer>() != null || r.TryCast<TrailRenderer>() != null || r.TryCast<LineRenderer>() != null) continue;
                var b = r.bounds;
                if (b.size.x > 1000f || b.size.z > 1000f) continue;
                var mats = r.sharedMaterials;
                var m = mats.Length > 0 ? mats[0] : null;
                if (m == null || m.shader == null) continue;
                if (m.renderQueue >= 2500 && (!m.HasProperty("_Color") || m.HasProperty("_TintColor") || m.shader.name.Contains("Particle", System.StringComparison.OrdinalIgnoreCase))) continue;
                // Folhagem (recorte por alfa) entraria como um quadrado sólido; e na chuva forte a água passa pela copa.
                if ((m.renderQueue >= 2450 && m.renderQueue < 2500) || m.IsKeywordEnabled("_ALPHATEST_ON") || m.shader.name.Contains("Cutout", System.StringComparison.OrdinalIgnoreCase) || m.shader.name.Contains("Tree", System.StringComparison.OrdinalIgnoreCase) || m.shader.name.Contains("Leaves", System.StringComparison.OrdinalIgnoreCase)) continue;
                Vector3 mn = b.min, mx = b.max;
                _novos.Add(new Candidato { r = r, x0 = mn.x, x1 = mx.x, z0 = mn.z, z1 = mx.z, sub = mats.Length });
            }
            if (_lidos < _todos.Length) return;
            (_candidatos, _novos) = (_novos, _candidatos);
            _todos = null;
            _proximaLista = Time.unscaledTime + 10f;
            _proximoMapa = 0f; // lista nova: redesenha
        }

        static void Abrigo(Camera cam)
        {
            if (_mapa == null)
            {
                _mapa = new RenderTexture(Resolucao, Resolucao, 24, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };
                _mapa.Create();
                _matAltura = new Material(_altura) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                _cbMapa = new CommandBuffer { name = "BetterGraphics Abrigo" };
                _mat.SetTexture("_BG_Altura", _mapa);
            }
            var swl = System.Diagnostics.Stopwatch.StartNew();
            Listar(cam.cullingMask);
            Lab.Tempo("MolhadoLista", swl.Elapsed.TotalMilliseconds);
            // O mundo parado não muda: redesenha quando a câmera se afasta 20 m do centro ou a cada 3 s
            // (o ônibus do jogador é tratado pela caixa dele no shader).
            var p = cam.transform.position;
            if (_mapaPronto && Time.unscaledTime < _proximoMapa && (new Vector2(p.x, p.z) - _centroMapa).sqrMagnitude < 400f) { Area(); return; }
            _proximoMapa = Time.unscaledTime + 3f;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            float texel = Lado / Resolucao;
            _centroMapa = new Vector2(Mathf.Round(p.x / texel) * texel, Mathf.Round(p.z / texel) * texel);
            // Olhando para baixo, +z no alto da imagem; 60 m acima da câmera e 160 m de profundidade.
            var olho = new Vector3(_centroMapa.x, p.y + 60f, _centroMapa.y);
            var vista = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(olho, Quaternion.Euler(90f, 0f, 0f), Vector3.one).inverse;
            // Projeção pura: o SetViewProjectionMatrices já converte para a GPU (convertida antes, o mapa saía espelhado).
            var proj = Matrix4x4.Ortho(-Lado / 2f, Lado / 2f, -Lado / 2f, Lado / 2f, 1f, 160f);
            float x0 = _centroMapa.x - Lado / 2f, x1 = _centroMapa.x + Lado / 2f, z0 = _centroMapa.y - Lado / 2f, z1 = _centroMapa.y + Lado / 2f;

            _cbMapa.Clear();
            _cbMapa.SetRenderTarget(_mapa);
            _cbMapa.ClearRenderTarget(true, true, new Color(-10000f, 0f, 0f, 0f));
            _cbMapa.SetViewProjectionMatrices(vista, proj);
            foreach (var c in _candidatos)
            {
                if (c.x1 < x0 || c.x0 > x1 || c.z1 < z0 || c.z0 > z1 || c.r == null) continue;
                for (int k = 0; k < c.sub; k++) _cbMapa.DrawRenderer(c.r, _matAltura, k, 0);
            }
            Graphics.ExecuteCommandBuffer(_cbMapa);
            _mapaPronto = true;
            Lab.Tempo("MolhadoMapa", sw.Elapsed.TotalMilliseconds);
            Area();
        }

        static void Area() => _mat.SetVector("_BG_AlturaArea", new Vector4(_centroMapa.x, _centroMapa.y, 1f / Lado, _mapaPronto ? 1f : 0f));

        // 1 = molhado (vermelho) e exposto (verde); 2 = albedo vermelho direto no G-buffer.
        internal static string Debug(int v) { _debug = v; return "molhado debug " + v; }

        // Para o Lab: salva o mapa de abrigo em imagem (cinza = altura relativa à câmera, preto = nada).
        internal static string SalvarMapa(string arq)
        {
            if (_mapa == null) return "sem mapa";
            var ant = RenderTexture.active; RenderTexture.active = _mapa;
            var t = new Texture2D(Resolucao, Resolucao, TextureFormat.RGBAFloat, false, true);
            t.ReadPixels(new Rect(0, 0, Resolucao, Resolucao), 0, 0); t.Apply();
            RenderTexture.active = ant;
            var px = t.GetPixels();
            float y0 = _camCb != null ? _camCb.transform.position.y : 0f;
            int vazios = 0; float min = float.MaxValue, max = float.MinValue;
            var saida = new Color[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                float h = px[i].r;
                if (h < -5000f) { vazios++; saida[i] = Color.black; continue; }
                min = Mathf.Min(min, h); max = Mathf.Max(max, h);
                float v = Mathf.Clamp01((h - y0 + 10f) / 20f);
                saida[i] = new Color(v, v, v, 1f);
            }
            t.SetPixels(saida); t.Apply();
            System.IO.File.WriteAllBytes(arq, ImageConversion.EncodeToPNG(t));
            Object.Destroy(t);
            return $"mapa salvo: {vazios * 100 / px.Length}% vazio, altura {min:0.0}..{max:0.0}, câmera y {y0:0.0}";
        }

        internal static string Estado() =>
            $"molhado: shader {(_mat != null ? "ok" : _semShader ? "falhou" : "-")} passe {(_cb != null ? "ligado" : "desligado")} umidade {Chao.Umidade:0.00} chuva {Chuva.Intensidade():0.00} mapa {(_mapaPronto ? "ok" : "-")}";
    }
}
