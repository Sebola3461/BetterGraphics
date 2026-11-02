using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using N3 = System.Numerics.Vector3;

namespace BetterGraphics
{
    // Luz ambiente do salão e da cabine vinda das janelas, por pixel (shader Interior).
    // Na pipeline Built-in a luz do céu chega igual em todo pixel, como se o ônibus não tivesse teto: o
    // interior fica chapado, sem o claro perto do vidro e o escuro no piso e sob os bancos. E o interior
    // dos ônibus costuma ser poucas malhas de 10 m, então não dá para resolver por objeto.
    //  - Janelas: triângulos das malhas de vidro projetados nas seis faces da caixa da carroceria.
    //  - Mapa 3D (grade de 20 cm, espaço do ônibus): em cada ponto, quanto se vê de vidro e de que lado
    //    (visibilidade com peso do cosseno, em ordem 1). Calculado uma vez por ônibus.
    //  - Passe na câmera antes dos reflexos: troca a luz ambiente de céu aberto pela que entra pelos vidros.
    internal static unsafe class Interior
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DSonda(float* sh);
        static DSonda _lerSonda; static bool _resolvido;

        const int Direcoes = 192;
        const float Celula = 0.15f, Passo = 0.2f;
        static readonly Vector3[] _dir = new Vector3[Direcoes];

        static Transform _bus;
        static Texture3D _vis;
        static Bounds _grade;        // salão no espaço do ônibus (o que o mapa cobre)
        static float _proximaConstrucao;
        static int _vidrosTri;
        static float _abertura;

        static Material _mat;
        static Mesh _tela;
        static CommandBuffer _cb;
        static Camera _camCb;
        static bool _semShader;

        internal static void CenaMudou() { Tirar(); _bus = null; _calculo = null; _proximaConstrucao = 0f; }

        static void Resolver()
        {
            if (_resolvido) return;
            _resolvido = true;
            var p = IL2CPP.il2cpp_resolve_icall("UnityEngine.RenderSettings::get_ambientProbe_Injected");
            if (p != IntPtr.Zero) _lerSonda = Marshal.GetDelegateForFunctionPointer<DSonda>(p);
            else Log.Aviso("Interior: função nativa da sonda ambiente não encontrada");
            // Direções espalhadas por igual na esfera (espiral de Fibonacci).
            float ouro = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < Direcoes; i++)
            {
                float y = 1f - (i + 0.5f) * 2f / Direcoes, r = Mathf.Sqrt(1f - y * y);
                _dir[i] = new Vector3(Mathf.Cos(ouro * i) * r, y, Mathf.Sin(ouro * i) * r);
            }
        }

        internal static void Manter()
        {
            if (!Cfg.LuzJanela.Value) { Tirar(); return; }
            Resolver();
            if (_lerSonda == null || _semShader) return;
            if (_mat == null)
            {
                var sh = Pacote.Shader("Interior");
                if (sh == null) { _semShader = true; return; }
                _mat = new Material(sh) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            }
            var bus = Veiculo.Raiz();
            if (bus == null) { Tirar(); return; }
            if (bus != _bus) { _bus = bus; _proximaConstrucao = Time.unscaledTime + 3f; } // espera a carroceria ser medida
            if (_proximaConstrucao > 0f && Time.unscaledTime >= _proximaConstrucao)
            {
                _proximaConstrucao = 0f;
                Construir(bus);
            }
            Receber();
            // Com a luz ambiente própria ligada ela é a única que escreve a ambiente (usa este mapa no salão): dois
            // passes mexendo no mesmo RT3, em ordem que muda, deixavam a lataria rosa e amarela.
            if (_vis != null && !AmbientLight.Ativo) Pendurar(Pos.Cam); else Tirar();
        }

        // ---- passe na câmera ----

        static void Pendurar(Camera cam)
        {
            if (cam == _camCb && _cb != null) return;
            Tirar();
            if (cam == null || cam.actualRenderingPath != RenderingPath.DeferredShading) return;
            if (_tela == null)
            {
                _tela = new Mesh { hideFlags = HideFlags.DontUnloadUnusedAsset };
                _tela.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(3f, -1f, 0f), new Vector3(-1f, 3f, 0f) };
                _tela.triangles = new[] { 0, 1, 2 };
                _tela.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            }
            _cb = new CommandBuffer { name = "BetterGraphics Interior" };
            _cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _cb.DrawMesh(_tela, Matrix4x4.identity, _mat, 0, 0);
            cam.AddCommandBuffer(CameraEvent.BeforeReflections, _cb);
            _camCb = cam;
        }

        static void Tirar()
        {
            if (_cb != null && _camCb != null) try { _camCb.RemoveCommandBuffer(CameraEvent.BeforeReflections, _cb); } catch { }
            _cb = null; _camCb = null;
        }

        internal static void Restaurar() { Tirar(); }

        // Todo quadro (LateUpdate): matrizes da câmera e do ônibus e a luz de fora.
        internal static void Quadro()
        {
            if (_cb == null || _camCb == null || _bus == null || _mat == null) return;
            var cam = _camCb;
            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            _mat.SetMatrix("_BG_InvVP", vp.inverse);
            Grade(out var grade);
            _mat.SetMatrix("_BG_MundoGrade", grade);
            _mat.SetMatrix("_BG_MundoBus", Matrix4x4.Rotate(Quaternion.Inverse(_bus.rotation)));
            _mat.SetVector("_BG_GradeTam", _grade.size);

            float* sh = stackalloc float[27];
            _lerSonda(sh);
            // A sonda da Unity já guarda os coeficientes no formato dos shaders (base e convolução aplicadas).
            for (int ch = 0; ch < 3; ch++)
            {
                float* c = sh + ch * 9;
                _mat.SetVector(ch == 0 ? "_BG_SHAr" : ch == 1 ? "_BG_SHAg" : "_BG_SHAb", new Vector4(c[3], c[1], c[2], c[0] - c[6]));
                _mat.SetVector(ch == 0 ? "_BG_SHBr" : ch == 1 ? "_BG_SHBg" : "_BG_SHBb", new Vector4(c[4], c[5], c[6] * 3f, c[7]));
            }
            _mat.SetVector("_BG_SHC", new Vector4(sh[8], sh[17], sh[26], 1f));
            // Média da luz de fora em todas as direções = termo constante. Paredes, teto e piso devolvem
            // uma parte dela, mais quanto mais vidro o salão tem.
            float reb = Cfg.JanelaRebate.Value * Mathf.Clamp01(_abertura * 4f);
            _mat.SetVector("_BG_Rebate", new Vector4(sh[0] * reb, sh[9] * reb, sh[18] * reb, 0f));
            _mat.SetFloat("_BG_Forca", Cfg.JanelaForca.Value);
            _mat.SetFloat("_BG_Debug", _debug ? 1f : 0f);
        }

        static bool _debug;

        // Mundo -> caixa do salão (0..1), para outros passes saberem o que é dentro do ônibus.
        internal static bool Grade(out Matrix4x4 m)
        {
            m = Matrix4x4.identity;
            if (_bus == null || _vis == null) return false;
            m = Matrix4x4.TRS(-Vector3.Scale(_grade.min, Inverso(_grade.size)), Quaternion.identity, Inverso(_grade.size)) * _bus.worldToLocalMatrix;
            return true;
        }
        // Mapa das janelas para a luz ambiente própria: visibilidade (rgb = direção, a = constante), mundo -> salão (0 a 1),
        // rotação mundo -> ônibus, tamanho do salão (m) e quanto o salão é aberto (vidro).
        internal static bool Janelas(out Texture vis, out Matrix4x4 grade, out Matrix4x4 rot, out Vector3 tamanho, out float abertura)
        {
            vis = _vis; tamanho = _grade.size; abertura = _abertura; rot = Matrix4x4.identity; grade = Matrix4x4.identity;
            if (!Cfg.LuzJanela.Value || !Grade(out grade)) return false;
            rot = Matrix4x4.Rotate(Quaternion.Inverse(_bus.rotation));
            return true;
        }

        internal static string Debug(bool v) { _debug = v; return "interior debug " + v; }

        static Vector3 Inverso(Vector3 v) => new(1f / v.x, 1f / v.y, 1f / v.z);

        // ---- mapa de visibilidade ----

        // Os triângulos de vidro são coletados na thread principal (só leitura de malhas) e o mapa é
        // calculado numa thread separada: são milhões de testes de raio, e qualquer tipo da Unity ali vira
        // chamada ao IL2CPP (calculado na thread principal, travava o jogo por 24 s). Lá só System.Numerics.
        static System.Threading.Tasks.Task<Mapa> _calculo;

        sealed class Mapa { public int gx, gy, gz, tri, vidroPct; public float[] cores; public float abertura; public long ms; }

        static void Construir(Transform bus)
        {
            var c = Veiculo.Caixa;
            var cmin = new N3(c.min.x, c.min.y, c.min.z);
            var cmax = new N3(c.max.x, c.max.y, c.max.z);
            var tris = new List<N3>();
            // Inclui os inativos: as janelas têm versão seca e molhada, trocadas pela chuva.
            foreach (var r in bus.GetComponentsInChildren<Renderer>(true))
                if (r != null && r.TryCast<ParticleSystemRenderer>() == null && EhVidro(r)) Triangulos(r, bus, tris);
            float tom = Cfg.VidroTom.Value;
            var dirs = new N3[Direcoes];
            for (int i = 0; i < Direcoes; i++) dirs[i] = new N3(_dir[i].x, _dir[i].y, _dir[i].z);
            // Salão: a caixa sem a casca e sem o que fica abaixo do piso (rodas, chassi).
            var gmin = cmin + new N3(0.08f, 0.3f, 0.08f);
            var gmax = cmax - new N3(0.08f, 0.05f, 0.08f);
            _grade = new Bounds(new Vector3((gmin.X + gmax.X) / 2f, (gmin.Y + gmax.Y) / 2f, (gmin.Z + gmax.Z) / 2f), new Vector3(gmax.X - gmin.X, gmax.Y - gmin.Y, gmax.Z - gmin.Z));
            _calculo = System.Threading.Tasks.Task.Run(() => Calcular(cmin, cmax, gmin, gmax, tris, dirs, tom));
        }

        // Thread principal, a cada segundo: entrega o mapa pronto à GPU.
        static void Receber()
        {
            if (_calculo == null || !_calculo.IsCompleted) return;
            var t = _calculo; _calculo = null;
            if (t.IsFaulted) { Log.Aviso("Interior: falha no mapa: " + t.Exception?.GetBaseException().Message); return; }
            var m = t.Result;
            var cores = new Color[m.gx * m.gy * m.gz];
            for (int i = 0; i < cores.Length; i++) cores[i] = new Color(m.cores[i * 4], m.cores[i * 4 + 1], m.cores[i * 4 + 2], m.cores[i * 4 + 3]);
            if (_vis != null) UnityEngine.Object.Destroy(_vis);
            _vis = new Texture3D(m.gx, m.gy, m.gz, TextureFormat.RGBAHalf, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontUnloadUnusedAsset };
            _vis.SetPixels(cores);
            _vis.Apply(false, true);
            _mat.SetTexture("_BG_Vis", _vis);
            _abertura = m.abertura; _vidrosTri = m.tri;
            Log.Msg($"Interior: mapa {m.gx}x{m.gy}x{m.gz}, {m.tri} triângulos de vidro, {m.vidroPct}% da caixa é vidro, abertura média {m.abertura:0.00} ({m.ms} ms em segundo plano)");
        }

        static bool EhVidro(Renderer r)
        {
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || m.shader == null) continue;
                string sh = m.shader.name;
                if (sh == "Glass Reflective" || sh.StartsWith("OneClick Glass", StringComparison.Ordinal)) return true;
                if (!sh.Contains("Transparent", StringComparison.OrdinalIgnoreCase)) continue;
                string n = (m.name + " " + r.name).ToLowerInvariant();
                if (n.Contains("vidro") || n.Contains("glass") || n.Contains("window") || n.Contains("janela")) return true;
            }
            return false;
        }

        static void Triangulos(Renderer r, Transform bus, List<N3> saida)
        {
            Mesh mesh = null;
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null) mesh = mf.sharedMesh;
            else if (r.TryCast<SkinnedMeshRenderer>() is SkinnedMeshRenderer sk) mesh = sk.sharedMesh;
            if (mesh == null) return;
            var m = bus.worldToLocalMatrix * r.transform.localToWorldMatrix;
            if (!mesh.isReadable)
            {
                // Malha sem leitura: a caixa dela vira dois triângulos no plano mais fino.
                var b = mesh.bounds;
                Vector3 mn = b.min, mx = b.max, ce = b.center, sz = b.size;
                N3[] q;
                if (sz.x <= sz.y && sz.x <= sz.z) q = new[] { Mult(m, ce.x, mn.y, mn.z), Mult(m, ce.x, mx.y, mn.z), Mult(m, ce.x, mx.y, mx.z), Mult(m, ce.x, mn.y, mx.z) };
                else if (sz.z <= sz.y) q = new[] { Mult(m, mn.x, mn.y, ce.z), Mult(m, mx.x, mn.y, ce.z), Mult(m, mx.x, mx.y, ce.z), Mult(m, mn.x, mx.y, ce.z) };
                else q = new[] { Mult(m, mn.x, ce.y, mn.z), Mult(m, mx.x, ce.y, mn.z), Mult(m, mx.x, ce.y, mx.z), Mult(m, mn.x, ce.y, mx.z) };
                saida.Add(q[0]); saida.Add(q[1]); saida.Add(q[2]);
                saida.Add(q[0]); saida.Add(q[2]); saida.Add(q[3]);
                return;
            }
            var v = mesh.vertices;
            var t = mesh.triangles;
            var lv = new N3[v.Length];
            for (int i = 0; i < v.Length; i++) { var p = v[i]; lv[i] = Mult(m, p.x, p.y, p.z); }
            for (int i = 0; i + 2 < t.Length; i += 3) { saida.Add(lv[t[i]]); saida.Add(lv[t[i + 1]]); saida.Add(lv[t[i + 2]]); }
        }

        static N3 Mult(Matrix4x4 m, float x, float y, float z) =>
            new(m.m00 * x + m.m01 * y + m.m02 * z + m.m03, m.m10 * x + m.m11 * y + m.m12 * z + m.m13, m.m20 * x + m.m21 * y + m.m22 * z + m.m23);

        // ---- daqui para baixo roda fora da thread principal: nada da Unity ----

        static Mapa Calcular(N3 cmin, N3 cmax, N3 gmin, N3 gmax, List<N3> tris, N3[] dirs, float tom)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tam = cmax - cmin;
            int nx = Math.Max(1, (int)MathF.Ceiling(tam.X / Celula)), ny = Math.Max(1, (int)MathF.Ceiling(tam.Y / Celula)), nz = Math.Max(1, (int)MathF.Ceiling(tam.Z / Celula));
            // Faces: 0 -x, 1 +x, 2 -y, 3 +y, 4 -z, 5 +z. Cada uma é uma grade nas duas outras coordenadas.
            var faces = new bool[6][];
            faces[0] = new bool[nz * ny]; faces[1] = new bool[nz * ny];
            faces[2] = new bool[nx * nz]; faces[3] = new bool[nx * nz];
            faces[4] = new bool[nx * ny]; faces[5] = new bool[nx * ny];
            int[] largura = { nz, nz, nx, nx, nx, nx };
            var centro = (cmin + cmax) * 0.5f;
            int nTri = 0;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                N3 a = tris[i], b = tris[i + 1], d = tris[i + 2];
                var n = N3.Cross(b - a, d - a);
                if (n.LengthSquared() < 1e-10f) continue;
                nTri++;
                var meio = (a + b + d) / 3f;
                float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
                int eixo = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
                int face = eixo * 2 + (Comp(meio, eixo) > Comp(centro, eixo) ? 1 : 0);
                Triangulo(UV(a, face, cmin), UV(b, face, cmin), UV(d, face, cmin), faces[face], largura[face]);
            }

            var gt = gmax - gmin;
            int gx = Math.Max(2, (int)MathF.Ceiling(gt.X / Passo)), gy = Math.Max(2, (int)MathF.Ceiling(gt.Y / Passo)), gz = Math.Max(2, (int)MathF.Ceiling(gt.Z / Passo));
            var cores = new float[gx * gy * gz * 4];
            double soma = 0;
            for (int z = 0; z < gz; z++)
                for (int y = 0; y < gy; y++)
                    for (int x = 0; x < gx; x++)
                    {
                        var p = gmin + gt * new N3((x + 0.5f) / gx, (y + 0.5f) / gy, (z + 0.5f) / gz);
                        float media = 0f; var vet = N3.Zero;
                        for (int k = 0; k < dirs.Length; k++)
                            if (Janela(p, dirs[k], cmin, cmax, faces, largura)) { media += 1f; vet += dirs[k]; }
                        // Visibilidade com peso do cosseno para a normal n ≈ média + vet·n (ordem 1).
                        media *= tom / dirs.Length; vet *= 2f * tom / dirs.Length;
                        int o = ((z * gy + y) * gx + x) * 4;
                        cores[o] = vet.X; cores[o + 1] = vet.Y; cores[o + 2] = vet.Z; cores[o + 3] = media;
                        soma += media;
                    }
            int vidro = 0, total = 0;
            foreach (var f in faces) { total += f.Length; foreach (var b in f) if (b) vidro++; }
            return new Mapa { gx = gx, gy = gy, gz = gz, tri = nTri, cores = cores, abertura = (float)(soma / (gx * gy * gz)), vidroPct = 100 * vidro / Math.Max(1, total), ms = sw.ElapsedMilliseconds };
        }

        static float Comp(N3 v, int e) => e == 0 ? v.X : e == 1 ? v.Y : v.Z;

        // Coordenadas da face em células: -x/+x usam (z, y); -y/+y usam (x, z); -z/+z usam (x, y).
        static System.Numerics.Vector2 UV(N3 p, int face, N3 cmin)
        {
            var q = (p - cmin) / Celula;
            return (face >> 1) switch
            {
                0 => new(q.Z, q.Y),
                1 => new(q.X, q.Z),
                _ => new(q.X, q.Y),
            };
        }

        static void Triangulo(System.Numerics.Vector2 a, System.Numerics.Vector2 b, System.Numerics.Vector2 d, bool[] f, int w)
        {
            int h = f.Length / w;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, d.X)))), x1 = Math.Min(w - 1, (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, d.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, d.Y)))), y1 = Math.Min(h - 1, (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, d.Y))));
            float area = Cruz(b - a, d - a);
            if (MathF.Abs(area) < 1e-6f) return;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new System.Numerics.Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Cruz(b - p, d - p) / area, w1 = Cruz(d - p, a - p) / area;
                    if (w0 >= -0.15f && w1 >= -0.15f && 1f - w0 - w1 >= -0.15f) f[y * w + x] = true;
                }
        }

        static float Cruz(System.Numerics.Vector2 a, System.Numerics.Vector2 b) => a.X * b.Y - a.Y * b.X;

        // O raio que sai de p na direção d atravessa a caixa por uma célula de vidro?
        static bool Janela(N3 p, N3 d, N3 cmin, N3 cmax, bool[][] faces, int[] largura)
        {
            float melhor = float.MaxValue; int face = -1;
            for (int e = 0; e < 3; e++)
            {
                float de = Comp(d, e);
                if (MathF.Abs(de) < 1e-6f) continue;
                float t = ((de > 0f ? Comp(cmax, e) : Comp(cmin, e)) - Comp(p, e)) / de;
                if (t >= 0f && t < melhor) { melhor = t; face = e * 2 + (de > 0f ? 1 : 0); }
            }
            if (face < 0) return false;
            var uv = UV(p + d * melhor, face, cmin);
            var f = faces[face]; int w = largura[face], h = f.Length / w;
            int x = Math.Clamp((int)uv.X, 0, w - 1), y = Math.Clamp((int)uv.Y, 0, h - 1);
            return f[y * w + x];
        }

        internal static string Estado() =>
            $"interior: shader {(_mat != null ? "ok" : _semShader ? "falhou" : "-")} mapa {(_vis != null ? $"{_vis.width}x{_vis.height}x{_vis.depth}" : "-")} câmera {(_camCb != null ? _camCb.name : "-")} abertura {_abertura:0.00} cor {QualitySettings.activeColorSpace}";
    }
}
