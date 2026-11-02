using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterGraphics
{
    // Chuva caindo e respingos, calculados inteiros na GPU (RainFall e RainSplash): a CPU só passa vento, tempo,
    // densidade e o mapa de abrigo do Molhado. Substitui a chuva do Enviro (rala, atravessava o teto do ônibus), que
    // fica escondida enquanto esta estiver ativa.
    internal static class RainFall
    {
        const int MaxNear = 65536, MaxFar = 32768;
        // Caixas cúbicas: o shader esmaece em esfera (raio = metade do lado), então só a esfera interna aparece.
        static readonly Vector3 NearBox = new(30f, 30f, 30f), FarBox = new(90f, 90f, 90f);
        // Respingos: uma vaga por célula de uma grade fixa no mundo, numa área de SplashCells x SplashCells células em
        // volta da câmera; cada vaga tem anel + gotículas.
        const int SplashCells = 48, SplashParts = 6, Splashes = SplashCells * SplashCells * SplashParts;
        const float SplashCell = 0.5f;

        static Material _mat, _splashMat;
        static Mesh _mesh, _splashMesh;
        static bool _noShader;
        static Vector3 _prevCam;
        static Vector3 _camVel;
        static bool _hasPrev;

        internal static bool Active { get; private set; }

        internal static void CenaMudou() { Active = false; _hasPrev = false; }

        internal static void Quadro()
        {
            float rain = Chuva.Intensidade() * Mathf.Max(Cfg.RainIntensity.Value, 0f);
            var cam = Pos.Cam;
            if (!Cfg.RainFallEnabled.Value || rain < 0.01f || cam == null || _noShader) { Active = false; return; }
            if (_mat == null)
            {
                var sh = Pacote.Shader("RainFall");
                var ss = Pacote.Shader("RainSplash");
                if (sh == null || ss == null) { _noShader = true; return; }
                _mat = new Material(sh) { name = "BetterGraphics_RainFall", hideFlags = HideFlags.DontUnloadUnusedAsset };
                _splashMat = new Material(ss) { name = "BetterGraphics_RainSplash", hideFlags = HideFlags.DontUnloadUnusedAsset };
                _mesh = Quads(MaxNear + MaxFar);
                _splashMesh = Quads(Splashes);
            }
            rain = Mathf.Clamp01(rain);

            // Velocidade da câmera (o risco é esticado pela velocidade relativa); o salto da origem flutuante é ignorado.
            float dt = Mathf.Max(Time.deltaTime, 1e-3f);
            var c = cam.transform.position;
            if (_hasPrev)
            {
                var v = (c - _prevCam) / dt;
                if (v.magnitude < 60f) _camVel = Vector3.Lerp(_camVel, v, 1f - Mathf.Exp(-dt * 8f));
            }
            _prevCam = c; _hasPrev = true;

            float time = (float)(Time.timeAsDouble % 1000.0);
            var wind = Wind(time, out float turbulence);
            float density = Mathf.Max(Cfg.RainParticleDensity.Value, 0f) / 1000f; // gotas por m³
            int near = Mathf.Min(MaxNear, Mathf.RoundToInt(density * NearBox.x * NearBox.y * NearBox.z));
            int far = Mathf.Min(MaxFar, Mathf.RoundToInt(density * 0.02f * FarBox.x * FarBox.y * FarBox.z));
            float fog = 0f;
            try { fog = Luz.Sky.Weather.currentActiveWeatherPreset.fogDensity; } catch { }

            Texture map = null; var area = Vector4.zero;
            bool shelter = Cfg.RainCollision.Value && Molhado.Abrigo(cam, out map, out area);
            bool bus = BusColumn(out var grade);
            Lights(c);
            foreach (var m in new[] { _mat, _splashMat })
            {
                m.SetVectorArray("_BG_RF_LightPos", _lightPos);
                m.SetVectorArray("_BG_RF_LightCol", _lightCol);
                m.SetVectorArray("_BG_RF_LightDir", _lightDir);
                m.SetVector("_BG_RF_Near", new Vector4(NearBox.x, NearBox.y, NearBox.z, near));
                m.SetVector("_BG_RF_Far", new Vector4(FarBox.x, FarBox.y, FarBox.z, near + far));
                m.SetVector("_BG_RF_Wind", new Vector4(wind.x, wind.y, turbulence, time));
                // Garoa: poucas gotas; temporal: todas.
                m.SetVector("_BG_RF_Fall", new Vector4(Mathf.Max(Cfg.RainTerminalVelocity.Value, 1f), Mathf.Max(Cfg.RainMotionBlurStretch.Value, 0.1f), Mathf.Pow(rain, 1.5f), fog));
                m.SetVector("_BG_RF_CamVel", _camVel);
                m.SetVector("_BG_RF_Splash", new Vector4(SplashCells, SplashCell, 0.5f, rain));
                if (shelter) m.SetTexture("_BG_RF_Shelter", map);
                m.SetVector("_BG_RF_ShelterArea", shelter ? area : Vector4.zero);
                m.SetFloat("_BG_RF_HasBus", bus ? 1f : 0f);
                if (bus) m.SetMatrix("_BG_RF_Bus", grade);
            }
            int drawn = near + far;
            if (drawn > 0) Graphics.DrawMesh(_mesh, Matrix4x4.identity, _mat, 0, cam, 0, null, false, false);
            Graphics.DrawMesh(_splashMesh, Matrix4x4.identity, _splashMat, 0, cam, 0, null, false, false);
            Active = true;
        }

        // ---- luzes próximas (postes, faróis) que iluminam a chuva ----

        const int LightCount = 8;
        static readonly Vector4[] _lightPos = new Vector4[LightCount], _lightCol = new Vector4[LightCount], _lightDir = new Vector4[LightCount];
        static readonly List<Light> _allLights = new();
        static readonly Light[] _picked = new Light[LightCount];
        static float _nextLightScan, _nextLightPick;

        static void Lights(Vector3 cam)
        {
            if (Time.unscaledTime >= _nextLightScan)
            {
                _nextLightScan = Time.unscaledTime + 5f;
                _allLights.Clear();
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                    if (l.type == LightType.Point || l.type == LightType.Spot) _allLights.Add(l);
            }
            // As mais próximas, descontado o alcance (um poste forte a 30 m pesa mais que uma luz fraca a 10 m).
            if (Time.unscaledTime >= _nextLightPick)
            {
                _nextLightPick = Time.unscaledTime + 0.2f;
                Array.Clear(_picked, 0, LightCount);
                var score = new float[LightCount];
                for (int k = 0; k < LightCount; k++) score[k] = float.MaxValue;
                foreach (var l in _allLights)
                {
                    if (l == null || l.WasCollected || !l.enabled || !l.gameObject.activeInHierarchy || l.intensity <= 0.01f) continue;
                    float d = Vector3.Distance(l.transform.position, cam) - l.range;
                    if (d > 30f) continue;
                    int worst = 0;
                    for (int k = 1; k < LightCount; k++) if (score[k] > score[worst]) worst = k;
                    if (d < score[worst]) { score[worst] = d; _picked[worst] = l; }
                }
            }
            for (int k = 0; k < LightCount; k++)
            {
                var l = _picked[k];
                if (l == null || l.WasCollected || !l.enabled) { _lightPos[k] = Vector4.zero; continue; }
                var t = l.transform;
                var p = t.position;
                _lightPos[k] = new Vector4(p.x, p.y, p.z, Mathf.Max(l.range, 0.1f));
                var col = l.color * l.intensity;
                bool spot = l.type == LightType.Spot;
                float half = l.spotAngle * 0.5f * Mathf.Deg2Rad;
                _lightCol[k] = new Vector4(col.r, col.g, col.b, spot ? Mathf.Cos(half) : -2f);
                var f = t.forward;
                _lightDir[k] = new Vector4(f.x, f.y, f.z, spot ? Mathf.Cos(half * 0.7f) : 1f);
            }
        }

        // Mundo -> caixa da carroceria do ônibus do jogador (0 a 1). A caixa medida inclui os retrovisores; a
        // carroceria é ~62% da largura dela.
        static bool BusColumn(out Matrix4x4 m)
        {
            m = Matrix4x4.identity;
            var bus = Veiculo.Raiz();
            if (bus == null) return false;
            var b = Veiculo.Caixa;
            var size = new Vector3(b.extents.x * 1.24f + 0.1f, b.size.y, b.size.z + 0.1f);
            var min = new Vector3(b.center.x - size.x * 0.5f, b.min.y, b.center.z - size.z * 0.5f);
            var inv = new Vector3(1f / size.x, 1f / size.y, 1f / size.z);
            m = Matrix4x4.TRS(-Vector3.Scale(min, inv), Quaternion.identity, inv) * bus.worldToLocalMatrix;
            return true;
        }

        // Vento base do schema (x, z) pela força do vento do clima, com rajadas (soma de senos, suave) e a direção
        // girando devagar. Devolve também a amplitude da turbulência (m) que o shader aplica a cada gota.
        static Vector2 Wind(float t, out float turbulence)
        {
            float strength = 0.25f;
            try { strength = Mathf.Clamp01(Luz.Sky.Weather.currentActiveWeatherPreset.WindStrenght); } catch { }
            var b = Cfg.RainWindVelocity.Value;
            var baseWind = b != null && b.Length >= 3 ? new Vector2(b[0], b[2]) : new Vector2(2.5f, 4.1f);
            float gust = 1f + 0.35f * (Mathf.Sin(t * 0.31f) * 0.6f + Mathf.Sin(t * 0.73f + 1.7f) * 0.4f);
            float turn = 35f * Mathf.Sin(t * 0.11f + 2f);
            var w = (Vector2)(Quaternion.Euler(0f, 0f, turn) * baseWind) * ((0.4f + strength) * gust);
            turbulence = 0.25f + 0.9f * strength;
            return w;
        }

        // Um quadrado por gota: uv = canto, uv2.x = índice.
        static Mesh Quads(int n)
        {
            var v = new Vector3[n * 4]; var uv = new Vector2[n * 4]; var uv2 = new Vector2[n * 4]; var t = new int[n * 6];
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < 4; k++) { uv[i * 4 + k] = new Vector2(k & 1, k >> 1); uv2[i * 4 + k] = new Vector2(i, 0f); }
                t[i * 6] = i * 4; t[i * 6 + 1] = i * 4 + 1; t[i * 6 + 2] = i * 4 + 2;
                t[i * 6 + 3] = i * 4 + 2; t[i * 6 + 4] = i * 4 + 1; t[i * 6 + 5] = i * 4 + 3;
            }
            var m = new Mesh { hideFlags = HideFlags.DontUnloadUnusedAsset, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.vertices = v; m.uv = uv; m.uv2 = uv2; m.triangles = t;
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return m;
        }

        internal static string Estado()
        {
            if (!Active) return $"chuva GPU: desligada (intensidade {Chuva.Intensidade():0.00}, shader {(_noShader ? "falhou" : _mat != null ? "ok" : "-")})";
            float density = Cfg.RainParticleDensity.Value / 1000f;
            int near = Mathf.Min(MaxNear, Mathf.RoundToInt(density * NearBox.x * NearBox.y * NearBox.z));
            return $"chuva GPU: {near} gotas perto, velocidade da câmera {_camVel.magnitude:0.0} m/s, abrigo {(Molhado.Abrigo(Pos.Cam, out _, out var a) ? "ok" : "-")} {a}";
        }
    }
}
