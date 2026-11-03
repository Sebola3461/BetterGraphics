using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace BetterGraphics
{
    // Chuva nos vidros do ônibus do jogador.
    //  - Na chuva o jogo liga a versão molhada das janelas com "OneClick Glass/Steamy Refract" (manchas paradas).
    //    Esse material vira o RainGlass: a película de água borra a vista e as laterais ganham gotas procedurais.
    //  - No para-brisa as gotas são simuladas (RainDropSim) junto com a película e as palhetas reais (WiperTracker).
    //    Cada gota é um quadradinho sobre o vidro, desenhado na câmera pelo RainDrops, que calcula a lente da água
    //    por pixel: com isso a gota tem a resolução da tela, de perto ou de longe.
    internal static unsafe class WindshieldRain
    {
        const string GameShader = "OneClick Glass/Steamy Refract";
        const string CloneName = "RainGlass (BetterGraphics)"; // sem o prefixo BetterGraphics_: o Vidros.Restaurar não a tira
        const int Capacity = 24576, DataWidth = 256, DataHeight = Capacity / DataWidth;
        // O para-brisa fica a ~1,5 m do olho (~0,9 mm por pixel): no tamanho físico uma gota de 1 mm seria um ponto
        // de 2 pixels. Na referência fotográfica as gotas ocupam 12 a 25 pixels em 1080p.
        const float VisualScale = 3f;

        static Shader _glassShader;
        static Material _dropMat;
        static bool _noShader;
        static readonly Dictionary<IntPtr, Material> _cloneOf = new();      // material do jogo -> RainGlass
        static readonly Dictionary<IntPtr, Material> _originalOf = new();   // RainGlass -> material do jogo
        static readonly Dictionary<IntPtr, Renderer> _renderers = new();
        static Transform _bus;
        static int _layer;
        static GlassMap _map;
        static RainDropSim _sim;
        static Texture2D _dropTex, _filmTex, _glassTex;
        static float[] _dropBuf;
        // Arrays do lado IL2CPP reaproveitados: passar byte[] gerenciado ao LoadRawTextureData convertia elemento por
        // elemento, alguns milissegundos por quadro.
        static Il2CppStructArray<byte> _dropBytes, _filmBytes;
        static Mesh _quads;
        static System.Threading.Tasks.Task _step;
        static readonly List<RainDropSim.Blade> _blades = new();
        static Vector3 _prevPos;
        static float _speed, _pendingDt, _nextScan;

        internal static void CenaMudou() { Wait(); _renderers.Clear(); _bus = null; _sim = null; _map = null; WiperTracker.Clear(); }

        static void Wait() { try { _step?.Wait(); } catch (Exception e) { Log.Aviso("Chuva no vidro: " + e.InnerException?.Message); } _step = null; }

        internal static void Manter()
        {
            if (!Cfg.RainGlassEnabled.Value) { if (_renderers.Count > 0 || _sim != null) Restaurar(); return; }
            if (_noShader) return;
            if (_glassShader == null)
            {
                _glassShader = Pacote.Shader("RainGlass");
                var drops = Pacote.Shader("RainDrops");
                if (_glassShader == null || drops == null) { _noShader = true; return; }
                _dropMat = new Material(drops) { name = "BetterGraphics_RainDrops", hideFlags = HideFlags.DontUnloadUnusedAsset };
            }
            var bus = Veiculo.Raiz();
            if (bus != _bus)
            {
                Restaurar();
                _bus = bus; _nextScan = 0f;
                if (bus != null) _prevPos = bus.position;
            }
            if (bus == null) return;
            // Percorrer o ônibus inteiro custa milissegundos: só na troca de ônibus e a cada 10 s (janelas novas).
            if (_sim != null && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 10f;
            if (Swap(bus) || _sim == null) { Wait(); Build(bus); }
        }

        static bool IsGameGlass(Material m) => m != null && m.shader != null && m.shader.name == GameShader;

        // Inclui as janelas inativas: a versão molhada só liga quando começa a chover.
        static bool Swap(Transform bus)
        {
            bool added = false;
            foreach (var r in bus.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool has = false;
                foreach (var m in mats) if (IsGameGlass(m)) { has = true; break; }
                if (!has) continue;
                var swapped = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++) swapped[i] = IsGameGlass(mats[i]) ? Clone(mats[i]) : mats[i];
                r.sharedMaterials = swapped;
                if (!_renderers.ContainsKey(r.Pointer)) { _renderers[r.Pointer] = r; added = true; }
            }
            return added;
        }

        static Material Clone(Material game)
        {
            if (_cloneOf.TryGetValue(game.Pointer, out var c) && c != null) return c;
            c = new Material(_glassShader) { name = CloneName, renderQueue = game.renderQueue, hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (game.HasProperty("_Color")) c.SetColor("_Color", game.GetColor("_Color"));
            _cloneOf[game.Pointer] = c;
            _originalOf[c.Pointer] = game;
            return c;
        }

        // ---- medida do para-brisa e criação da simulação ----

        static void Build(Transform bus)
        {
            _sim = null; _map = null;
            var tris = new List<(N3, N3, N3)>();
            float middle = Veiculo.Caixa.center.z;
            var toBus = WiperTracker.ToNumerics(bus.worldToLocalMatrix);
            foreach (var r in _renderers.Values)
            {
                if (r == null) continue;
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || !mesh.isReadable) continue;
                var m = WiperTracker.ToNumerics(r.transform.localToWorldMatrix) * toBus;
                var v = mesh.vertices; var t = mesh.triangles;
                var p = new N3[v.Length];
                for (int i = 0; i < v.Length; i++) p[i] = N3.Transform(new N3(v[i].x, v[i].y, v[i].z), m);
                bool front = false;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    N3 a = p[t[i]], b = p[t[i + 1]], c = p[t[i + 2]];
                    var n = N3.Cross(b - a, c - a);
                    if (n.LengthSquared() < 1e-12f || MathF.Abs(N3.Normalize(n).Z) < 0.5f || (a.Z + b.Z + c.Z) / 3f < middle) continue;
                    tris.Add((a, b, c)); front = true;
                }
                if (front) _layer = r.gameObject.layer;
            }
            if (tris.Count == 0) { Log.Aviso("Chuva no vidro: para-brisa não achado (só a película nas janelas)"); Shader.SetGlobalFloat("_BG_RainHasZone", 0f); return; }

            var res = Cfg.RainGridResolution.Value;
            int gw = Math.Clamp(res != null && res.Length > 0 ? res[0] : 256, 32, 1024), gh = Math.Clamp(res != null && res.Length > 1 ? res[1] : 256, 32, 1024);
            _map = GlassMap.Build(tris, gw, gh);
            _sim = new RainDropSim(_map.Width, _map.Height, gw, gh, Capacity, VisualScale);
            _sim.SetGlass(_map.Covered, _map.Gravity());

            _glassTex = NewTexture(gw, gh, TextureFormat.RGBAFloat, FilterMode.Bilinear);
            var gt = _map.Texture();
            var gb = new byte[gt.Length * 4];
            Buffer.BlockCopy(gt, 0, gb, 0, gb.Length);
            _glassTex.LoadRawTextureData(gb); _glassTex.Apply(false, false);
            _filmTex = NewTexture(gw, gh, TextureFormat.RFloat, FilterMode.Bilinear);
            _filmBytes = new Il2CppStructArray<byte>(gw * gh * 4);
            _dropTex ??= NewTexture(DataWidth, DataHeight, TextureFormat.RGBAFloat, FilterMode.Point);
            _dropBuf ??= new float[Capacity * 4];
            _dropBytes ??= new Il2CppStructArray<byte>(Capacity * 16);
            _quads ??= Quads();

            Shader.SetGlobalTexture("_BG_RainGlassMap", _glassTex);
            Shader.SetGlobalTexture("_BG_RainFilm", _filmTex);
            Shader.SetGlobalVector("_BG_RainZone", new Vector4(_map.X0, _map.Y0, _map.Width, _map.Height));
            Shader.SetGlobalFloat("_BG_RainMiddleZ", middle);
            Shader.SetGlobalFloat("_BG_RainHasZone", 1f);
            _dropMat.SetTexture("_BG_Drops", _dropTex);
            WiperTracker.Find(bus);
            int covered = 0; foreach (var c in _map.Covered) if (c) covered++;
            Log.Msg($"Chuva no vidro: para-brisa {_map.Width:0.00} x {_map.Height:0.00} m, grade {gw}x{gh} ({covered * 100 / (gw * gh)}% vidro), {_renderers.Count} vidros");
        }

        static Texture2D NewTexture(int w, int h, TextureFormat f, FilterMode filter) =>
            new(w, h, f, false, true) { filterMode = filter, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };

        // Um quadrado por gota: uv = canto, uv2 = posição da gota na textura de dados.
        static Mesh Quads()
        {
            var v = new Vector3[Capacity * 4]; var uv = new Vector2[Capacity * 4]; var uv2 = new Vector2[Capacity * 4]; var t = new int[Capacity * 6];
            for (int i = 0; i < Capacity; i++)
            {
                var idx = new Vector2(i % DataWidth, i / DataWidth);
                for (int k = 0; k < 4; k++) { uv[i * 4 + k] = new Vector2(k & 1, k >> 1); uv2[i * 4 + k] = idx; }
                t[i * 6] = i * 4; t[i * 6 + 1] = i * 4 + 1; t[i * 6 + 2] = i * 4 + 2;
                t[i * 6 + 3] = i * 4 + 2; t[i * 6 + 4] = i * 4 + 1; t[i * 6 + 5] = i * 4 + 3;
            }
            var m = new Mesh { hideFlags = HideFlags.DontUnloadUnusedAsset, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.vertices = v; m.uv = uv; m.uv2 = uv2; m.triangles = t;
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return m;
        }

        // ---- quadro: simula, envia e desenha ----

        static float _secoDesde = -1f;

        internal static void Quadro()
        {
            if (_bus == null || _renderers.Count == 0) return;
            float dt = Mathf.Clamp(Time.deltaTime, 1e-3f, 0.05f);
            var p = _bus.position;
            float v = Vector3.Dot(p - _prevPos, _bus.forward) / Mathf.Max(Time.deltaTime, 1e-3f);
            _prevPos = p;
            if (Mathf.Abs(v) < 60f) _speed = Mathf.Lerp(_speed, Mathf.Max(v, 0f), 1f - Mathf.Exp(-dt * 2f)); // ignora o salto da origem flutuante
            float rain = Chuva.Intensidade();
            var cam = Pos.Cam;

            foreach (var (game, clone) in Pairs()) if (game.HasProperty("_Color")) { var c = game.GetColor("_Color"); if (clone.GetColor("_Color") != c) clone.SetColor("_Color", c); }
            Shader.SetGlobalFloat("_BG_Rain", rain);
            Shader.SetGlobalFloat("_BG_DropScale", 1f);
            Shader.SetGlobalFloat("_BG_RainSpeed", _speed);
            Shader.SetGlobalMatrix("_BG_WorldToBus", _bus.worldToLocalMatrix);
            Shader.SetGlobalFloat("_BG_RainCamInside", cam != null && Veiculo.Dentro(cam.transform.position) ? 1f : 0f);
            Shader.SetGlobalFloat("_BG_Ior", Mathf.Clamp(Cfg.RainIor.Value, 1f, 2f));
            Shader.SetGlobalFloat("_BG_RainRoughness", Mathf.Clamp01(Cfg.RainRoughnessWet.Value));
            Shader.SetGlobalFloat("_BG_ContactAngle", Mathf.Clamp(Cfg.RainContactAngle.Value, 20f, 89f) * Mathf.Deg2Rad);
            if (_sim == null) return;

            // Vidro seco há 1 min (sem chuva e sem gota): nada a simular nem a desenhar (custava ~0,5 ms por quadro no seco).
            bool semAgua = rain <= 0.001f && _sim.Count == 0;
            if (!semAgua) _secoDesde = -1f;
            else if (_secoDesde < 0f) _secoDesde = Time.unscaledTime;
            if (semAgua && Time.unscaledTime - _secoDesde > 60f)
            {
                if (_step != null && _step.IsCompleted) Wait();
                return;
            }

            // A simulação roda numa thread, um passo adiantada. Se o passo anterior ainda não acabou, este quadro
            // desenha o último resultado e o tempo fica para o próximo passo (o quadro do jogo não espera).
            _pendingDt = Mathf.Min(_pendingDt + dt, 0.1f);
            bool ready = _step == null || _step.IsCompleted;
            if (ready) { Wait(); Upload(); }
            if (cam != null && Veiculo.Distancia(cam.transform.position) < 30f)
                Graphics.DrawMesh(_quads, _bus.localToWorldMatrix, _dropMat, _layer, cam, 0, null, false, false);

            _blades.Clear();
            var vs = new WiperTracker.VirtualSettings
            {
                Active = Cfg.WiperActive.Value, Switch = WiperSwitch(),
                SweepSpeed = Cfg.WiperSweepSpeed.Value, MinAngle = Cfg.WiperMinAngle.Value, MaxAngle = Cfg.WiperMaxAngle.Value,
            };
            if (!ready) return;
            float stepDt = _pendingDt; _pendingDt = 0f;
            if (Cfg.WiperActive.Value) WiperTracker.Update(_bus, _map, Mathf.Max(Cfg.WiperBladeWidth.Value, 0.005f) * 0.5f, vs, stepDt, _blades);
            float angle = Mathf.Clamp(Cfg.RainContactAngle.Value, 20f, 150f) * Mathf.Deg2Rad;
            var settings = new RainDropSim.Settings
            {
                Rain = rain, Speed = _speed,
                GravityWeight = Cfg.RainGravityWeight.Value, WindWeight = Cfg.RainWindWeight.Value,
                // Ângulo de contato alto (hidrofóbico) segura menos; baixo (vidro sujo, com película) segura mais.
                Adhesion = Mathf.Clamp((1f + Mathf.Cos(angle)) / (1f + Mathf.Cos(75f * Mathf.Deg2Rad)), 0.3f, 2f),
                Evaporation = Mathf.Max(Cfg.RainEvaporation.Value, 0f), Accumulation = Mathf.Max(Cfg.WiperAccumulation.Value, 0f),
            };
            var sim = _sim; var blades = _blades.ToArray();
            _step = System.Threading.Tasks.Task.Run(() => sim.Step(settings, stepDt, blades));
        }

        static int WiperSwitch()
        {
            try { return PBSLoaderCommonUtils.Veiculos.Veiculo.Cabine.Limpadores; } catch { return 0; }
        }

        static IEnumerable<(Material game, Material clone)> Pairs()
        {
            foreach (var kv in _originalOf)
            {
                var game = kv.Value;
                if (game == null || !_cloneOf.TryGetValue(game.Pointer, out var clone) || clone == null) continue;
                yield return (game, clone);
            }
        }

        static void Upload()
        {
            _sim.Encode(_dropBuf);
            Copy(_dropBuf, _dropBytes);
            _dropTex.LoadRawTextureData(_dropBytes); _dropTex.Apply(false, false);
            Copy(_sim.Film, _filmBytes);
            _filmTex.LoadRawTextureData(_filmBytes); _filmTex.Apply(false, false);
        }

        // Os dados de um array IL2CPP começam depois do cabeçalho (classe, monitor, limites, tamanho).
        static void Copy(float[] src, Il2CppStructArray<byte> dst)
        {
            long bytes = Math.Min((long)src.Length * 4, dst.Length);
            fixed (float* s = src) Buffer.MemoryCopy(s, (byte*)dst.Pointer + 4 * IntPtr.Size, dst.Length, bytes);
        }

        internal static void Restaurar()
        {
            Wait();
            foreach (var r in _renderers.Values)
            {
                try
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] != null && _originalOf.TryGetValue(mats[i].Pointer, out var game) && game != null) mats[i] = game;
                    r.sharedMaterials = mats;
                }
                catch { }
            }
            _renderers.Clear();
            _sim = null; _map = null;
            WiperTracker.Clear();
            Shader.SetGlobalFloat("_BG_RainHasZone", 0f);
        }

        internal static string Debug(bool on) { Shader.SetGlobalFloat("_BG_RainDebug", on ? 1f : 0f); return "gotas debug " + on; }

        // Para o Lab: salva a película do para-brisa (branco = cheia) com as palhetas por cima em vermelho.
        internal static string SaveFilm(string file)
        {
            if (_sim == null) return "sem simulação";
            Wait();
            int w = _sim.GridW, h = _sim.GridH;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                byte f = (byte)(Mathf.Clamp01(_sim.Film[i]) * 255f);
                px[i] = _map.Covered[i] ? new Color32(f, f, f, 255) : new Color32(0, 0, 60, 255);
            }
            foreach (var b in _blades)
                for (float s = 0f; s <= 1f; s += 0.002f)
                {
                    var q = N2.Lerp(b.A1, b.B1, s);
                    int c = _map.Cell(q.X, q.Y);
                    px[c] = new Color32(255, 0, 0, 255);
                }
            t.SetPixels32(px); t.Apply();
            System.IO.File.WriteAllBytes(file, ImageConversion.EncodeToPNG(t));
            UnityEngine.Object.Destroy(t);
            float sum = 0f; int n = 0;
            for (int i = 0; i < _sim.Film.Length; i++) if (_map.Covered[i]) { sum += _sim.Film[i]; n++; }
            return $"película média {(n > 0 ? sum / n : 0f):0.00}, {_blades.Count} palhetas varrendo";
        }

        internal static string Estado()
        {
            if (_sim == null) return $"gotas: shader {(_glassShader != null ? "ok" : _noShader ? "falhou" : "-")}, {_renderers.Count} vidros, sem simulação";
            var (mean, max, moving, carried) = _sim.Stats();
            return $"gotas: {_renderers.Count} vidros, {_sim.Count} gotas (raio médio {mean * 1000f:0.00} mm, máx {max * 1000f:0.00}, {moving} escorrendo, {carried} na palheta), " +
                   $"velocidade {_speed:0.0} m/s, chuva {Chuva.Intensidade():0.00}, chave do limpador {WiperSwitch()} | limpador {WiperTracker.Status()}";
        }
    }
}
