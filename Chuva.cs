using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterGraphics
{
    // Água em movimento na chuva (partículas criadas pelo mod, com os materiais do próprio jogo):
    //  - Respingo dos pneus: névoa e gotas atrás de cada roda do ônibus do jogador, pela velocidade e
    //    por quanto o chão está molhado.
    //  - Poças: roda passando numa poça (lida no mapa de poças refeito pelo Chao, no UV exato sob o
    //    pneu) solta uma lâmina de água para os lados.
    //  - Sarjeta: varredura com raios em volta da câmera acha onde o asfalto encosta no meio-fio; ali
    //    corre água rente ao chão, no sentido da descida da rua.
    //  - Respingo das gotas no chão (do Enviro) mais visível.
    // Tudo depende da intensidade da chuva do clima atual: com tempo seco nada é emitido.
    internal static class Chuva
    {
        sealed class Roda { public Il2Cpp.VPWheelCollider w; public float raio; public float acum, acumGota, proxSplash; }
        static readonly List<Roda> _rodas = new();
        static Transform _busRodas; static Rigidbody _rb; static Vector3 _posAnt; static float _proximaBuscaRodas;

        static GameObject _go;
        static ParticleSystem _nevoa, _gotas, _lamina;
        static Material _matNevoa;
        static bool _matsProntos;

        static Vector3 _centroVarredura = new(1e9f, 0, 0);
        static float[,] _altura, _alturaPronta; static bool[,] _rua; static int _vi; static Vector3 _origemGrade, _origemPronta; static bool _varrendo;
        const int G = 40; const float Passo = 1.25f;

        internal static void CenaMudou()
        {
            _rodas.Clear(); _busRodas = null; _rb = null; _matsProntos = false;
            _sarjetas.Clear(); _centroVarredura = new Vector3(1e9f, 0, 0); _varrendo = false;
            if (_go != null) UnityEngine.Object.Destroy(_go);
            _fluxos.Clear(); _sarjetasPostas = -1; _go = null; _nevoa = _gotas = _lamina = _correnteza = _pingos = null; Array.Clear(_perto, 0, 4); Array.Clear(_longe, 0, 4); _alturaPronta = null; _modNevoa = null;
        }

        // Intensidade da chuva agora (0 a 1): o clima atual, não a umidade do chão (que demora a secar).
        static float Intensidade()
        {
            var sky = Luz.Sky;
            if (sky == null) return 0f;
            try
            {
                var p = sky.Weather.currentActiveWeatherPreset;
                return p != null ? Mathf.Clamp01(p.wetnessLevel) : 0f;
            }
            catch { return 0f; }
        }

        // Cada Emit passa pelo interop (~5-10 µs): teto por quadro, dividido entre os efeitos.
        static int _orcamento;
        static bool Gastar() { if (_orcamento <= 0) return false; _orcamento--; return true; }

        internal static void Quadro()
        {
            _orcamento = Cfg.ChuvaEmissoesQuadro.Value;
            if (!Cfg.ChuvaEfeitos.Value) { if (_go != null) CenaMudou(); return; }
            float chuva = Intensidade();
            float molhado = Chao.Umidade;
            if (chuva <= 0.01f && molhado <= 0.05f) return;
            if (!Materiais()) return;
            try { Criar(); }
            catch
            {
                // Modelo destruído no meio (o Enviro recria as partículas ao trocar de clima): recomeça.
                if (_go != null) UnityEngine.Object.Destroy(_go);
                _go = null; _matsProntos = false; _modNevoa = null;
                throw;
            }
            if (_nevoa == null || _gotas == null) return;
            Pneus(molhado);
            Grade();
            Sarjetas(chuva, molhado);
            RespingoDoEnviro(chuva);
            ChuvaDensa(chuva);
            Pingos(chuva);
        }

        // ---------------- materiais (cópias dos do jogo: névoa de pneu, gota esticada, respingo)

        static ParticleSystem _modNevoa;

        // Modelo: a fumaça de pneu do jogo (já desbota e cresce). A API de módulos (forma, cor/tamanho na
        // vida) foi removida do build pelo IL2CPP, então o mod clona esse sistema e só emite nele (Emit com
        // posição e velocidade). Gotas e correnteza usam sistemas novos, com textura de fio gerada.
        static bool Materiais()
        {
            if (_matsProntos) return true;
            foreach (var ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
            {
                if (ps.name.StartsWith("BetterGraphics_", StringComparison.Ordinal)) continue;
                var r = ps.GetComponent<ParticleSystemRenderer>();
                var m = r != null ? r.sharedMaterial : null;
                if (m != null && m.name.StartsWith("Tire Smoke", StringComparison.Ordinal)) { _modNevoa = ps; _matNevoa = new Material(m) { name = "BetterGraphics_NevoaPneu" }; break; }
            }
            if (_modNevoa == null) return false;
            _matNevoa.hideFlags = HideFlags.DontUnloadUnusedAsset;
            // A fumaça de pneu é branca e opaca: vira spray d'água cinza-claro translúcido.
            var spray = new Color(0.78f, 0.8f, 0.82f, 0.45f);
            if (_matNevoa.HasProperty("_TintColor")) _matNevoa.SetColor("_TintColor", spray); else _matNevoa.color = spray;
            _matsProntos = true;
            Log.Msg("Chuva: modelos de partícula prontos");
            return true;
        }

        static ParticleSystem Clonar(ParticleSystem modelo, string nome, Material mat, float gravidade, int max, bool esticar)
        {
            int passo = 0;
            try
            {
                var go = UnityEngine.Object.Instantiate(modelo.gameObject); passo = 1;
                go.name = nome; passo = 2;
                // Só a partícula: scripts do modelo (do Enviro/do jogo) e filhos saem.
                var comps = go.GetComponents<Component>(); passo = 3;
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    var b = c.TryCast<MonoBehaviour>();
                    if (b != null) b.enabled = false; // desligar (destruir levava a partícula junto)
                }
                passo = 4;
                for (int i = go.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(go.transform.GetChild(i).gameObject);
                passo = 5;
                go.transform.SetParent(_go.transform, false);
                go.transform.position = Vector3.zero; go.transform.rotation = Quaternion.identity; go.transform.localScale = Vector3.one;
                go.SetActive(true); passo = 6;
                var ps = go.GetComponent<ParticleSystem>(); passo = 7;
                if (ps == null) throw new Exception("sem ParticleSystem no clone");
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); passo = 71;
                Particulas.Configurar(ps, max, gravidade); passo = 8; // emissão manual, mundo, laço
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (esticar) { r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.04f; r.lengthScale = 1.5f; }
                passo = 9;
                ps.Play();
                return ps;
            }
            catch (Exception e) { throw new Exception($"Clonar {nome} passo {passo}: {e.GetType().Name}"); }
        }

        // Sistema novo, sem módulos herdados (os modelos do Enviro têm animação de folha de textura: o fio
        // virava o anel do respingo). Emissão manual; o renderer usa a textura de fio gerada abaixo.
        static ParticleSystem Limpo(string nome, Material mat, float gravidade, int max)
        {
            var go = new GameObject(nome);
            go.transform.SetParent(_go.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            Particulas.Configurar(ps, max, gravidade);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.16f; r.lengthScale = 3f;
            ps.Play();
            return ps;
        }

        // Fio d'água: linha clara com bordas suaves, mais densa no meio do comprimento.
        static Material MaterialFio()
        {
            var t = new Texture2D(16, 64, TextureFormat.RGBA32, false, true) { name = "BetterGraphics_Fio", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[16 * 64];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 16; x++)
                {
                    float dx = (x - 7.5f) / 7.5f, dy = (y - 31.5f) / 31.5f;
                    float a = Mathf.Exp(-dx * dx * 5f) * (1f - dy * dy * dy * dy);
                    px[y * 16 + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(a)));
                }
            t.SetPixels32(px); t.Apply(false, false);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var m = new Material(_matNevoa) { name = "BetterGraphics_FioAgua", mainTexture = t };
            var c = new Color(0.78f, 0.82f, 0.86f, 0.45f);
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c); else m.color = c;
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return m;
        }

        static Material _matFio;

        static void Criar()
        {
            if (_go != null) return;
            _go = new GameObject("BetterGraphics_Chuva");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _nevoa = Clonar(_modNevoa, "BetterGraphics_NevoaPneus", _matNevoa, -0.03f, 3000, false);
            _matFio ??= MaterialFio();
            _gotas = Limpo("BetterGraphics_GotasPneus", _matFio, 1f, 4000);
            _lamina = Limpo("BetterGraphics_LaminaPoca", _matFio, 1f, 3000);
        }


        // ---------------- pneus

        static void BuscarRodas()
        {
            var bus = Veiculo.Raiz();
            if (bus == _busRodas && _rodas.Count > 0) return;
            if (Time.unscaledTime < _proximaBuscaRodas) return;
            _proximaBuscaRodas = Time.unscaledTime + 2f;
            _rodas.Clear(); _busRodas = bus; _rb = null;
            if (bus == null) return;
            foreach (var w in bus.GetComponentsInChildren<Il2Cpp.VPWheelCollider>(true))
                _rodas.Add(new Roda { w = w, raio = w.radius > 0.1f ? w.radius : 0.5f });
            _rb = bus.GetComponentInChildren<Rigidbody>();
            _posAnt = bus.position;
            if (_rodas.Count > 0) Log.Msg($"Chuva: {_rodas.Count} rodas no ônibus");
        }

        static void Pneus(float molhado)
        {
            BuscarRodas();
            if (_rodas.Count == 0 || _busRodas == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var vel = _rb != null && !_rb.isKinematic ? _rb.velocity : (_busRodas.position - _posAnt) / dt;
            _posAnt = _busRodas.position;
            float v = vel.magnitude;
            if (v < 1.5f || molhado < 0.08f) return;
            var frente = Vector3.ProjectOnPlane(_busRodas.forward, Vector3.up).normalized;
            var lado = Vector3.Cross(Vector3.up, frente);
            float sentido = Vector3.Dot(vel, frente) >= 0f ? 1f : -1f;
            float forca = Cfg.ChuvaPneus.Value * Mathf.Clamp01(molhado * 1.3f);
            foreach (var r in _rodas)
            {
                if (r.w == null || r.w.WasCollected) continue;
                var c = r.w.transform.position;
                var contato = c - Vector3.up * (r.raio * 0.95f);
                float ladoRoda = Mathf.Sign(Vector3.Dot(c - _busRodas.position, lado));
                // Névoa: ~v² (o spray de verdade quase não existe a 20 km/h e vira cortina a 80 km/h).
                r.acum += dt * forca * Mathf.Min(90f, 0.1f * v * v);
                while (r.acum >= 1f)
                {
                    r.acum -= 1f;
                    var ep = new ParticleSystem.EmitParams
                    {
                        position = contato - frente * sentido * (r.raio * 0.9f) + lado * ladoRoda * UnityEngine.Random.Range(-0.1f, 0.25f) + Vector3.up * UnityEngine.Random.Range(0f, 0.3f),
                        velocity = vel * UnityEngine.Random.Range(0.25f, 0.55f) + Vector3.up * UnityEngine.Random.Range(0.4f, 1.6f) + lado * ladoRoda * UnityEngine.Random.Range(0f, 1.2f),
                        startSize = UnityEngine.Random.Range(0.18f, 0.4f) * Mathf.Lerp(0.8f, 1.8f, Mathf.InverseLerp(5f, 25f, v)),
                        startLifetime = UnityEngine.Random.Range(0.5f, 1.1f),
                        startColor = new Color(1f, 1f, 1f, Mathf.Lerp(0.06f, 0.18f, Mathf.InverseLerp(5f, 25f, v))),
                        rotation = UnityEngine.Random.Range(0f, 360f),
                    };
                    if (Gastar()) _nevoa.Emit(ep, 1);
                }
                // Gotas arrancadas da banda de rodagem: saem para trás e para cima e caem.
                r.acumGota += dt * forca * Mathf.Min(400f, 2.2f * v * v / 10f + 6f * v);
                while (r.acumGota >= 1f)
                {
                    r.acumGota -= 1f;
                    var ep = new ParticleSystem.EmitParams
                    {
                        position = contato - frente * sentido * (r.raio * 0.7f) + Vector3.up * UnityEngine.Random.Range(0.05f, r.raio * 0.8f),
                        velocity = vel * UnityEngine.Random.Range(0.55f, 0.9f) + Vector3.up * UnityEngine.Random.Range(0.5f, 3.5f) + lado * ladoRoda * UnityEngine.Random.Range(-0.3f, 1.5f),
                        startSize = UnityEngine.Random.Range(0.012f, 0.03f),
                        startLifetime = UnityEngine.Random.Range(0.35f, 0.8f),
                        startColor = new Color(1f, 1f, 1f, 0.6f),
                    };
                    if (Gastar()) _gotas.Emit(ep, 1);
                }
                Poca(r, c, vel, v, frente, lado, ladoRoda);
            }
        }

        // Roda numa poça: lâmina de água para os lados e para a frente, proporcional à velocidade.
        static void Poca(Roda r, Vector3 centro, Vector3 vel, float v, Vector3 frente, Vector3 lado, float ladoRoda)
        {
            if (v < 3f || Time.time < r.proxSplash) return;
            r.proxSplash = Time.time + 0.06f;
            var hits = Physics.RaycastAll(centro + Vector3.up * 0.2f, Vector3.down, r.raio + 1.2f, ~0, QueryTriggerInteraction.Ignore);
            float melhor = 1e9f; RaycastHit h = default; bool achou = false;
            foreach (var x in hits)
            {
                if (x.collider == null || x.collider.transform.root == _busRodas) continue;
                if (x.distance < melhor) { melhor = x.distance; h = x; achou = true; }
            }
            if (!achou) return;
            var rend = h.collider.GetComponent<Renderer>();
            float poca = Chao.Poca(rend, h.textureCoord);
            if (poca < 0.45f) return;
            float k = Cfg.ChuvaPocas.Value * poca * Mathf.InverseLerp(3f, 20f, v) * Chao.Umidade;
            int n = Mathf.RoundToInt(70f * k);
            for (int i = 0; i < n; i++)
            {
                var para = lado * ladoRoda * UnityEngine.Random.Range(1.5f, 4.5f) + frente * UnityEngine.Random.Range(-1f, 1.5f);
                var ep = new ParticleSystem.EmitParams
                {
                    position = h.point + Vector3.up * 0.05f + lado * ladoRoda * UnityEngine.Random.Range(0.1f, 0.3f),
                    velocity = vel * 0.6f + para * Mathf.Lerp(0.6f, 1.6f, k) + Vector3.up * UnityEngine.Random.Range(1f, 3.5f) * Mathf.Lerp(0.7f, 1.4f, k),
                    startSize = UnityEngine.Random.Range(0.02f, 0.05f),
                    startLifetime = UnityEngine.Random.Range(0.4f, 0.9f),
                    startColor = new Color(1f, 1f, 1f, 0.75f),
                };
                if (Gastar()) _lamina.Emit(ep, 1);
            }
            for (int i = 0; i < n / 6; i++)
            {
                var ep = new ParticleSystem.EmitParams
                {
                    position = h.point + Vector3.up * 0.3f + lado * ladoRoda * 0.5f,
                    velocity = vel * 0.5f + lado * ladoRoda * UnityEngine.Random.Range(1f, 3f) + Vector3.up * UnityEngine.Random.Range(0.5f, 1.5f),
                    startSize = UnityEngine.Random.Range(0.3f, 0.6f),
                    startLifetime = UnityEngine.Random.Range(0.5f, 0.9f),
                    startColor = new Color(1f, 1f, 1f, 0.2f),
                    rotation = UnityEngine.Random.Range(0f, 360f),
                };
                if (Gastar()) _nevoa.Emit(ep, 1);
            }
        }

        // ---------------- grade em volta da câmera

        // Altura da superfície de cima e se é asfalto, numa grade em volta da câmera, montada aos poucos
        // (raios por quadro) e refeita quando a câmera anda 12 m.
        static void Grade()
        {
            if (Cfg.ChuvaSarjeta.Value <= 0f && Cfg.ChuvaPingos.Value <= 0f) return;
            var cam = Pos.Cam;
            if (cam == null) return;
            var p = cam.transform.position;
            if (!_varrendo && (new Vector2(p.x - _centroVarredura.x, p.z - _centroVarredura.z)).magnitude > 12f)
            {
                _varrendo = true; _vi = 0;
                _altura = new float[G, G]; _rua = new bool[G, G];
                _origemGrade = new Vector3(p.x - G * Passo * 0.5f, p.y, p.z - G * Passo * 0.5f);
            }
            if (!_varrendo) return;
            int ate = Math.Min(G * G, _vi + 40);
            for (; _vi < ate; _vi++)
            {
                int i = _vi % G, j = _vi / G;
                var topo = new Vector3(_origemGrade.x + i * Passo, _origemGrade.y + 40f, _origemGrade.z + j * Passo);
                _altura[i, j] = Altura(topo, out _rua[i, j]);
            }
            if (_vi >= G * G) { _varrendo = false; _centroVarredura = p; _alturaPronta = _altura; _origemPronta = _origemGrade; MontarSarjetas(); }
        }

        static float Altura(Vector3 topo, out bool rua)
        {
            rua = false;
            Collider topoCol = null; Vector2 topoUv = default;
            var hits = Physics.RaycastAll(topo, Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore);
            float melhor = -1e9f;
            var bus = Veiculo.Raiz();
            foreach (var h in hits)
            {
                if (h.collider == null) continue;
                var raiz = h.collider.transform.root;
                if (bus != null && raiz == bus) continue;
                if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue; // veículos
                if (h.point.y > melhor) { melhor = h.point.y; topoCol = h.collider; topoUv = h.textureCoord; }
            }
            // Rua: a superfície de cima é um material de chão molhado conhecido (asfalto do mapa).
            if (topoCol != null)
                rua = Chao.Poca(topoCol.GetComponent<Renderer>(), topoUv) >= 0f;
            return melhor;
        }

        // ---------------- sarjetas (água correndo junto ao meio-fio)

        sealed class Sarjeta { public Vector3 p, fluxo, paraCalcada; public float acum; }
        static readonly List<Sarjeta> _sarjetas = new();

        static void MontarSarjetas()
        {
            _sarjetas.Clear(); _versaoSarjetas++;
            int[] di = { 1, -1, 0, 0 }, dj = { 0, 0, 1, -1 };
            for (int i = 1; i < G - 1; i++)
                for (int j = 1; j < G - 1; j++)
                {
                    if (!_rua[i, j]) continue;
                    float h = _altura[i, j];
                    for (int k = 0; k < 4; k++)
                    {
                        int ni = i + di[k], nj = j + dj[k];
                        float hv = _altura[ni, nj];
                        if (_rua[ni, nj] || hv - h < 0.06f || hv - h > 0.5f) continue;
                        if (_sarjetas.Count >= 120) break;
                        var dir = new Vector3(di[k], 0, dj[k]);
                        var ao = new Vector3(-dj[k], 0, di[k]); // ao longo do meio-fio
                        // Desce para o lado mais baixo da rua (pela grade); plano: um sentido fixo.
                        int ai = i + (int)ao.x, aj = j + (int)ao.z, bi = i - (int)ao.x, bj = j - (int)ao.z;
                        float ha = _rua[ai, aj] ? _altura[ai, aj] : h, hb = _rua[bi, bj] ? _altura[bi, bj] : h;
                        var fluxo = ha < hb - 0.01f ? ao : hb < ha - 0.01f ? -ao : (ao.x + ao.z > 0 ? ao : -ao);
                        var centro = new Vector3(_origemGrade.x + i * Passo, h + 0.02f, _origemGrade.z + j * Passo);
                        _sarjetas.Add(new Sarjeta { p = centro + dir * (Passo * 0.5f - 0.18f), fluxo = fluxo, paraCalcada = dir });
                    }
                }
        }

        // Um emissor nativo por trecho (conjunto reaproveitado: só muda de lugar quando a grade é refeita).
        // Cada um solta água numa faixa rente ao meio-fio, no sentido do fluxo, com riscos esticados.
        static readonly List<ParticleSystem> _fluxos = new();
        static Material _matCorrenteza;
        static int _sarjetasPostas = -1;

        static void Sarjetas(float chuva, float molhado)
        {
            if (Cfg.ChuvaSarjeta.Value <= 0f) { TaxaSarjetas(0f); return; }
            if (_matCorrenteza == null)
            {
                _matCorrenteza = new Material(_matFio) { name = "BetterGraphics_Correnteza" };
                var cor = new Color(0.86f, 0.89f, 0.92f, 0.55f);
                if (_matCorrenteza.HasProperty("_TintColor")) _matCorrenteza.SetColor("_TintColor", cor); else _matCorrenteza.color = cor;
                _matCorrenteza.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            if (_sarjetasPostas != _versaoSarjetas)
            {
                _sarjetasPostas = _versaoSarjetas;
                while (_fluxos.Count < _sarjetas.Count) _fluxos.Add(NovoFluxo());
                for (int i = 0; i < _fluxos.Count; i++)
                {
                    var ps = _fluxos[i];
                    if (i < _sarjetas.Count)
                    {
                        var s = _sarjetas[i];
                        // Centro da faixa: 15 cm para fora do meio-fio, rente ao asfalto; +Z = sentido do fluxo.
                        ps.transform.SetPositionAndRotation(s.p - s.paraCalcada * 0.15f + Vector3.up * 0.01f, Quaternion.LookRotation(s.fluxo, Vector3.up));
                        if (!ps.gameObject.activeSelf) ps.gameObject.SetActive(true);
                    }
                    else if (ps.gameObject.activeSelf) ps.gameObject.SetActive(false);
                }
                _taxaSarjeta = -1f;
            }
            // A água corre enquanto chove e um pouco depois (o chão ainda escoa).
            float k = Mathf.Max(chuva, Mathf.Clamp01((molhado - 0.4f) * 1.6f)) * Cfg.ChuvaSarjeta.Value;
            TaxaSarjetas(60f * k);
        }

        static float _taxaSarjeta = -1f;
        static int _versaoSarjetas;

        static void TaxaSarjetas(float taxa)
        {
            if (Mathf.Abs(taxa - _taxaSarjeta) < 0.5f) return;
            _taxaSarjeta = taxa;
            foreach (var ps in _fluxos) if (ps != null && ps.gameObject.activeSelf) Particulas.Taxa(ps, taxa);
        }

        static ParticleSystem NovoFluxo()
        {
            var go = new GameObject("BetterGraphics_Correnteza");
            go.transform.SetParent(_go.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            // Faixa de 30 cm de largura ao longo do trecho; a água anda 1 a 1,8 m/s e some depois de ~1,5 m.
            Particulas.Emissor(ps, 300, new Vector3(0.3f, 0.01f, Passo), 1f, 1.8f, 0.035f, 0.07f, 0.9f, 1.3f);
            Particulas.Taxa(ps, 0f);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = _matCorrenteza;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.25f; r.lengthScale = 1f;
            ps.Play();
            return ps;
        }

        static ParticleSystem _correnteza;

        // ---------------- gotas do Enviro batendo no chão

        static float _proxEnviro;
        static readonly Dictionary<IntPtr, Color> _splashOrig = new();

        static void RespingoDoEnviro(float chuva)
        {
            if (Time.unscaledTime < _proxEnviro) return;
            _proxEnviro = Time.unscaledTime + 3f;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>())
            {
                var m = r.sharedMaterial;
                if (m == null || !m.name.StartsWith("EnviroRainSplash", StringComparison.Ordinal) || !m.HasProperty("_TintColor")) continue;
                if (!_splashOrig.ContainsKey(m.Pointer)) _splashOrig[m.Pointer] = m.GetColor("_TintColor");
                var o = _splashOrig[m.Pointer];
                m.SetColor("_TintColor", new Color(o.r, o.g, o.b, Mathf.Max(o.a, Cfg.ChuvaRespingoChao.Value)));
            }
        }

        // ---------------- chuva densa (do mod)

        // A chuva do Enviro é rala: poucas gotas finas e quase transparentes. Duas camadas próprias, com
        // emissão nativa (sem custo de CPU por gota): perto, riscos finos e muitos; longe, riscos maiores e
        // mais espaçados, para a chuva ter profundidade até a névoa. Gotas a ~9 m/s (velocidade final de gota
        // grande), inclinadas pelo vento.
        // Não chove dentro do ônibus: cada camada são quatro blocos (esquerda, direita, frente, trás) alinhados
        // ao ônibus, cobrindo a área em volta menos a projeção dele no chão. Colisão de partícula não serve:
        // a barata não acompanha colisor em movimento e a exata com dezenas de milhares de gotas trava o jogo.
        // Com a câmera longe do ônibus os blocos viram uma área contínua em volta dela.
        static readonly ParticleSystem[] _perto = new ParticleSystem[4], _longe = new ParticleSystem[4];
        static ParticleSystem _pingos;
        static Material _matChuva, _matChuvaLonge, _matPingo;
        const float AlturaPerto = 12f, AlturaLonge = 16f, LadoPerto = 28f, LadoLonge = 110f;
        const float DensPerto = 28f, DensLonge = 0.75f; // gotas por segundo por m² de chão (temporal)

        static ParticleSystem Bloco(string nome, Material mat, int max, float altura, float tamMin, float tamMax, float esticar)
        {
            var go = new GameObject(nome);
            go.transform.SetParent(_go.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            float vida = (altura + 4f) / 9f;
            Particulas.Emissor(ps, max, new Vector3(1f, 1f, 0.2f), 8f, 10f, tamMin, tamMax, vida * 0.9f, vida * 1.1f);
            Particulas.Taxa(ps, 0f);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = esticar; r.lengthScale = 1f;
            r.maxParticleSize = 0.004f; // gota rente à lente não vira uma barra branca na tela
            ps.Play();
            return ps;
        }

        static Material MaterialChuva(float alfa)
        {
            var m = new Material(_matFio) { name = "BetterGraphics_Chuva" };
            var c = new Color(0.82f, 0.85f, 0.88f, alfa);
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c); else m.color = c;
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return m;
        }

        // Respingo de gota: ponto claro com coroa (anel) suave.
        static Material MaterialPingo()
        {
            const int N = 32;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false, true) { name = "BetterGraphics_Pingo", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - 15.5f) / 15.5f, dy = (y - 15.5f) / 15.5f, r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Exp(-r * r * 18f) * 0.9f + Mathf.Exp(-(r - 0.6f) * (r - 0.6f) * 60f) * 0.55f;
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(a)));
                }
            t.SetPixels32(px); t.Apply(false, false);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var m = new Material(_matFio) { name = "BetterGraphics_PingoMat", mainTexture = t };
            var c = new Color(0.85f, 0.88f, 0.9f, 0.55f);
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c); else m.color = c;
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return m;
        }

        // Quatro blocos numa área quadrada de lado L em volta de "centro" (eixos dir/frente), menos o retângulo
        // de meia-largura hx e meia-comprimento hz (o ônibus; zero quando não há buraco).
        static void Blocos(ParticleSystem[] b, Vector3 centro, Vector3 dir, Vector3 frente, float L, float hx, float hz, float altura, Vector3 deriva, Quaternion rot, float dens)
        {
            float h = L * 0.5f;
            hx = Mathf.Min(hx, h - 0.5f); hz = Mathf.Min(hz, h - 0.5f);
            // (centro x, centro z, largura x, comprimento z) de cada bloco, no espaço dir/frente
            var p = new Vector4[]
            {
                new(-(h + hx) * 0.5f, 0f, h - hx, L),        // esquerda (o comprimento todo)
                new((h + hx) * 0.5f, 0f, h - hx, L),         // direita
                new(0f, (h + hz) * 0.5f, 2f * hx, h - hz),   // frente (entre os laterais)
                new(0f, -(h + hz) * 0.5f, 2f * hx, h - hz),  // trás
            };
            for (int i = 0; i < 4; i++)
            {
                var ps = b[i];
                float area = Mathf.Max(0f, p[i].z) * Mathf.Max(0f, p[i].w);
                var pos = centro + dir * p[i].x + frente * p[i].y + Vector3.up * altura - deriva;
                ps.transform.SetPositionAndRotation(pos, rot);
                // O box da forma fica no espaço local (x = direita, y = frente quando a gota cai para baixo).
                Particulas.Caixa(ps, new Vector3(Mathf.Max(0.01f, p[i].z), Mathf.Max(0.01f, p[i].w), 0.2f));
                Particulas.Taxa(ps, area * dens);
            }
        }

        static float _proxBlocos;

        static void ChuvaDensa(float chuva)
        {
            if (Cfg.ChuvaDensidade.Value <= 0f)
            {
                if (_perto[0] != null) for (int i = 0; i < 4; i++) { Particulas.Taxa(_perto[i], 0f); Particulas.Taxa(_longe[i], 0f); }
                return;
            }
            var cam = Pos.Cam;
            if (cam == null) return;
            if (_perto[0] == null)
            {
                _matChuva ??= MaterialChuva(0.32f);
                _matChuvaLonge ??= MaterialChuva(0.16f);
                for (int i = 0; i < 4; i++)
                {
                    _perto[i] = Bloco("BetterGraphics_ChuvaPerto" + i, _matChuva, 16000, AlturaPerto, 0.006f, 0.011f, 0.035f);
                    _longe[i] = Bloco("BetterGraphics_ChuvaLonge" + i, _matChuvaLonge, 9000, AlturaLonge, 0.03f, 0.05f, 0.03f);
                }
            }
            // Vento do clima inclina a chuva (até ~25° com vento forte), com a direção girando e a força
            // em rajadas: soma de senos, suave e barata (nada de Random por quadro).
            float vento = 0.25f;
            try { vento = Mathf.Clamp01(Luz.Sky.Weather.currentActiveWeatherPreset.WindStrenght); } catch { }
            float t = Time.time;
            float rajada = 1f + 0.3f * (Mathf.Sin(t * 0.31f) * 0.6f + Mathf.Sin(t * 0.73f + 1.7f) * 0.4f);
            float giro = 40f * Mathf.Sin(t * 0.11f + 2f);
            var dirVento = Quaternion.AngleAxis(giro, Vector3.up) * new Vector3(0.8f, 0f, 0.6f);
            float inclinacao = vento * 0.45f * rajada;
            var queda = (Vector3.down + dirVento * inclinacao).normalized;

            var c = cam.transform.position;
            var bus = Veiculo.Raiz();
            Vector3 centro = c, dir = Vector3.right, frente = Vector3.forward; float hx = 0f, hz = 0f;
            if (bus != null && Veiculo.Distancia(c) < LadoPerto * 0.4f)
            {
                // Perto do ônibus: área alinhada a ele, com o buraco do tamanho da carroceria (+ folga para a
                // inclinação do vento entre o teto e o chão).
                var cx = Veiculo.Caixa;
                frente = Vector3.ProjectOnPlane(bus.forward, Vector3.up).normalized;
                dir = Vector3.Cross(Vector3.up, frente);
                centro = bus.TransformPoint(cx.center);
                centro.y = c.y;
                float folga = 0.3f + inclinacao * cx.size.y;
                hx = cx.extents.x * 0.62f + folga; // a caixa inclui os retrovisores; a carroceria é ~62% dela
                hz = cx.extents.z + folga;
            }
            var rot = Quaternion.LookRotation(queda, frente);
            float k = chuva * chuva * Cfg.ChuvaDensidade.Value; // garoa rala, temporal denso
            // A gota cai inclinada: o emissor fica deslocado contra o vento para ela chegar no lugar certo.
            Blocos(_perto, centro, dir, frente, LadoPerto, hx, hz, AlturaPerto, dirVento * (inclinacao * AlturaPerto), rot, DensPerto * k);
            Blocos(_longe, centro, dir, frente, LadoLonge, hx, hz, AlturaLonge, dirVento * (inclinacao * AlturaLonge), rot, DensLonge * k);
        }

        // Respingos das gotas no chão, em volta da câmera, na superfície de cima de cada ponto da grade
        // (telhados e marquises recebem; embaixo deles não pinga).
        static float _acumPingo;

        static void Pingos(float chuva)
        {
            if (Cfg.ChuvaPingos.Value <= 0f || _alturaPronta == null) return;
            var cam = Pos.Cam;
            if (cam == null) return;
            if (_pingos == null)
            {
                _matPingo ??= MaterialPingo();
                var go = new GameObject("BetterGraphics_Pingos");
                go.transform.SetParent(_go.transform, false);
                _pingos = go.AddComponent<ParticleSystem>();
                _pingos.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                Particulas.Configurar(_pingos, 3000, 1f);
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = _matPingo; r.renderMode = ParticleSystemRenderMode.Billboard;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                _pingos.Play();
            }
            _acumPingo += Time.deltaTime * chuva * chuva * Cfg.ChuvaPingos.Value * 1600f;
            var c = cam.transform.position;
            int tentativas = 0;
            while (_acumPingo >= 1f && tentativas++ < 200)
            {
                _acumPingo -= 1f;
                // Mais denso perto da câmera (é onde dá para ver cada respingo).
                var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float dist = Mathf.Lerp(0.8f, 18f, UnityEngine.Random.value * UnityEngine.Random.value);
                float x = c.x + Mathf.Cos(ang) * dist, z = c.z + Mathf.Sin(ang) * dist;
                int i = Mathf.RoundToInt((x - _origemPronta.x) / Passo), j = Mathf.RoundToInt((z - _origemPronta.z) / Passo);
                if (i < 0 || j < 0 || i >= G || j >= G) continue;
                float h = _alturaPronta[i, j];
                if (h < -1e8f) continue;
                if (!Gastar()) break;
                var ep = new ParticleSystem.EmitParams
                {
                    position = new Vector3(x, h + 0.03f, z),
                    velocity = new Vector3(UnityEngine.Random.Range(-0.3f, 0.3f), UnityEngine.Random.Range(0.6f, 1.4f), UnityEngine.Random.Range(-0.3f, 0.3f)),
                    startSize = UnityEngine.Random.Range(0.04f, 0.1f),
                    startLifetime = UnityEngine.Random.Range(0.1f, 0.22f),
                    startColor = new Color(1f, 1f, 1f, UnityEngine.Random.Range(0.4f, 0.9f)),
                    rotation = UnityEngine.Random.Range(0f, 360f),
                };
                _pingos.Emit(ep, 1);
            }
            if (_acumPingo > 50f) _acumPingo = 50f;
        }

        internal static bool SarjetaN(int n, out Vector3 p, out Vector3 fluxo, out Vector3 paraCalcada)
        {
            p = fluxo = paraCalcada = Vector3.zero;
            if (_sarjetas.Count == 0) return false;
            var s = _sarjetas[Math.Abs(n) % _sarjetas.Count];
            p = s.p; fluxo = s.fluxo; paraCalcada = s.paraCalcada;
            return true;
        }

        static int Contar(ParticleSystem[] b) { int n = 0; foreach (var x in b) if (x != null) n += x.particleCount; return n; }

        internal static string Estado() => $"chuva: intensidade {Intensidade():0.00}, rodas {_rodas.Count}, névoa {(_nevoa != null ? _nevoa.particleCount : 0)}, gotas {(_gotas != null ? _gotas.particleCount : 0)}, lâmina {(_lamina != null ? _lamina.particleCount : 0)}, sarjetas {_sarjetas.Count}/{_fluxos.Count}, chuva {Contar(_perto)}+{Contar(_longe)}, pingos {(_pingos != null ? _pingos.particleCount : 0)}";
    }
}
