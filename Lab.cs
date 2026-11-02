using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MelonLoader.Utils;
using UnityEngine;

namespace BetterGraphics
{
    // Laboratório (jogo aberto com --graficos-lab): executa comandos de UserData/GraficosLab/cmd.txt,
    // uma linha por comando, e registra em resp.txt. Serve para comparar hora, clima, ângulo e ajustes
    // tirando prints, sem ninguém jogando.
    //   id N                     identifica o lote; ao terminar escreve "fim N"
    //   esperar S                pausa S segundos
    //   hora H                   hora do dia (14.5 = 14h30) e congela o relógio
    //   clima NOME | climas      muda o clima / lista os nomes
    //   orbita YAW PITCH DIST [ALTURA]  câmera em volta do ônibus (yaw 0 = frente dele), olhando o centro
    //   livre X Y Z YAW PITCH    câmera em posição absoluta
    //   interno X Y Z YAW PITCH  câmera no espaço do ônibus (x lado, y altura, z frente)
    //   cam f1|f2|f3|f4|soltar   câmeras do jogo (f1 = cabine) / devolve o controle
    //   ui 0|1                   esconde/mostra a interface
    //   fx 0|1                   liga/desliga o mod
    //   set NOME VALOR           muda uma preferência da seção [Realismo]
    //   print NOME               salva prints/NOME.png
    //   mat TEXTO [N]            propriedades de até N materiais cujo nome contém TEXTO
    //   rgi | rgiset NOME VALOR  parâmetros do Radiant GI na câmera (qualquer um, pelo nome)
    //   rsm | rsmset NOME VALOR  idem para o mapa de sombra refletivo no sol
    //   quadros S                tempos de quadro e pior tempo de cada módulo por S segundos
    //   diag | info | fps | sair
    internal static class Lab
    {
        internal static bool Ativo { get; private set; }
        static string _dir, _cmd, _resp, _prints;
        static readonly Queue<string> _fila = new();
        static float _ate, _proximaLeitura;
        static string _lote;
        static string _esperandoArquivo; static float _limiteArquivo;

        static bool _camLab; static Vector3 _camPos; static Quaternion _camRot;
        static float _andar; static readonly List<Rigidbody> _rbs = new();
        static bool _orbita, _interno, _mapa; static float _oYaw, _oPitch, _oDist, _oAlt;
        static Transform _refMapa;
        static GameObject _playerLab, _playerOrig;
        static readonly List<Canvas> _canvasOcultos = new();
        static float _fpsInicio = -1f; static int _fpsQuadros;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static void Iniciar()
        {
            Ativo = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--graficos-lab");
            if (!Ativo) return;
            _dir = Path.Combine(MelonEnvironment.UserDataDirectory, "GraficosLab");
            _prints = Path.Combine(_dir, "prints");
            Directory.CreateDirectory(_prints);
            _cmd = Path.Combine(_dir, "cmd.txt");
            _resp = Path.Combine(_dir, "resp.txt");
            if (File.Exists(_cmd)) File.Delete(_cmd);
            File.WriteAllText(_resp, $"lab iniciado {DateTime.Now:HH:mm:ss}\n");
            Log.Msg("Laboratório ativo: " + _dir);
            // Sem isto o Unity pausa quando a janela perde o foco, e os comandos param.
            Application.runInBackground = true;
        }

        static void Resp(string s)
        {
            try { File.AppendAllText(_resp, s + "\n"); } catch { }
        }

        internal static void Atualizar()
        {
            if (!Ativo) return;
            if (!Application.runInBackground) Application.runInBackground = true;
            QuadroStats();
            // Outros mods (PBSLoaderCommonInterface) religam a própria interface: mantém escondida enquanto pedido.
            if (_uiOculta && Time.frameCount % 15 == 0) Ui(false);
            if (_fpsInicio >= 0f) { _fpsQuadros++; if (Time.unscaledTime - _fpsInicio >= 3f) { Resp($"fps {_fpsQuadros / (Time.unscaledTime - _fpsInicio):0.0}"); _fpsInicio = -1f; } else return; }

            if (Time.unscaledTime >= _proximaLeitura)
            {
                _proximaLeitura = Time.unscaledTime + 0.25f;
                try
                {
                    if (File.Exists(_cmd))
                    {
                        foreach (var l in File.ReadAllLines(_cmd)) if (l.Trim().Length > 0) _fila.Enqueue(l.Trim());
                        File.Delete(_cmd);
                    }
                }
                catch { }
            }

            if (_esperandoArquivo != null)
            {
                if (!File.Exists(_esperandoArquivo) && Time.unscaledTime < _limiteArquivo) return;
                Resp(File.Exists(_esperandoArquivo) ? "ok print " + Path.GetFileName(_esperandoArquivo) : "erro print sem arquivo");
                _esperandoArquivo = null;
            }
            if (Time.unscaledTime < _ate) return;

            while (_fila.Count > 0 && Time.unscaledTime >= _ate && _esperandoArquivo == null && _fpsInicio < 0f)
            {
                string l = _fila.Dequeue();
                try { Executar(l); }
                catch (Exception e) { Resp($"erro {l}: {e.Message}"); }
                if (_fila.Count == 0 && _lote != null && _esperandoArquivo == null && _fpsInicio < 0f && Time.unscaledTime >= _ate)
                { Resp("fim " + _lote); _lote = null; }
            }
            if (_fila.Count == 0 && _lote != null && _esperandoArquivo == null && _fpsInicio < 0f && Time.unscaledTime >= _ate)
            { Resp("fim " + _lote); _lote = null; }
        }

        static float F(string s) => float.Parse(s, Inv);

        // Estatística de quadros: tempos de quadro e o pior tempo de cada módulo no período.
        static float _qAte = -1f; static readonly List<float> _quadros = new();
        static readonly Dictionary<string, (double max, int n, double soma)> _modulos = new();
        internal static void Tempo(string onde, double ms)
        {
            if (_qAte < 0f) return;
            _modulos.TryGetValue(onde, out var v);
            _modulos[onde] = (Math.Max(v.max, ms), v.n + 1, v.soma + ms);
        }
        static void QuadroStats()
        {
            if (_qAte < 0f) return;
            _quadros.Add(Time.unscaledDeltaTime * 1000f);
            if (Time.unscaledTime < _qAte) return;
            _qAte = -1f;
            var q = new List<float>(_quadros); q.Sort();
            float p50 = q[q.Count / 2], p99 = q[(int)(q.Count * 0.99f)], max = q[q.Count - 1];
            int picos = 0; foreach (var x in _quadros) if (x > p50 * 2.5f && x > 25f) picos++;
            var sb = new System.Text.StringBuilder($"quadros n {q.Count} p50 {p50:0.0} ms p99 {p99:0.0} max {max:0.0} picos {picos} | ");
            foreach (var kv in _modulos) sb.Append($"{kv.Key} max {kv.Value.max:0.0} méd {kv.Value.soma / Math.Max(1, kv.Value.n):0.00} ms; ");
            Resp(sb.ToString());
        }

        static void Executar(string l)
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sky = Luz.Sky;
            switch (p[0])
            {
                case "id": _lote = p[1]; return;
                case "esperar": _ate = Time.unscaledTime + F(p[1]); return;
                case "hora":
                    if (sky == null) { Resp("erro sem Enviro"); return; }
                    sky.GameTime.ProgressTime = Il2Cpp.EnviroTime.TimeProgressMode.None;
                    sky.SetInternalTimeOfDay(F(p[1]));
                    Resp("ok hora " + p[1]); return;
                case "clima":
                    if (sky == null) { Resp("erro sem Enviro"); return; }
                    sky.ChangeWeather(string.Join(" ", p, 1, p.Length - 1));
                    Resp("ok clima " + string.Join(" ", p, 1, p.Length - 1)); return;
                case "climas":
                    if (sky == null) { Resp("erro sem Enviro"); return; }
                    var nomes = new List<string>();
                    var lista = sky.Weather.weatherPresets;
                    for (int i = 0; i < lista.Count; i++) nomes.Add(lista[i].Name);
                    Resp("climas: " + string.Join(" | ", nomes)); return;
                case "orbita":
                    _orbita = true; _interno = false; _mapa = false; _camLab = true;
                    _oYaw = F(p[1]); _oPitch = F(p[2]); _oDist = F(p[3]); _oAlt = p.Length > 4 ? F(p[4]) : 0f;
                    Resp("ok " + l); return;
                case "interno": // posição no espaço do ônibus (x lado, y altura, z frente) e direção relativa a ele
                    _orbita = false; _interno = true; _mapa = false; _camLab = true;
                    _camPos = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                    _camRot = Quaternion.Euler(F(p[5]), F(p[4]), 0f);
                    Resp("ok " + l); return;
                case "mapa": // coordenadas do mapa GraficosLab (x leste, altura, y norte), relativas ao chão dele:
                             // o jogo desloca o mundo (origem flutuante) quando a câmera se afasta
                    _orbita = false; _interno = false; _mapa = true; _camLab = true;
                    _camPos = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                    _camRot = Quaternion.Euler(F(p[5]), F(p[4]), 0f);
                    Resp("ok " + l); return;
                case "livre":
                    _interno = false; _mapa = false;
                    _orbita = false; _camLab = true;
                    _camPos = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                    _camRot = Quaternion.Euler(F(p[5]), F(p[4]), 0f);
                    Resp("ok " + l); return;
                case "cam":
                    _camLab = false;
                    var orb = Il2Cpp.MouseOrbitCSHARP.instance;
                    if (orb != null)
                    {
                        orb.enabled = true;
                        switch (p[1])
                        {
                            case "f1": orb.CameraF1(); break;
                            case "f2": orb.CameraF2(); break;
                            case "f3": orb.CameraF3(); break;
                            case "f4": orb.CameraF4(); break;
                        }
                    }
                    Resp("ok " + l); return;
                case "ui": Ui(p[1] == "1"); Resp("ok " + l); return;
                case "fx": BetterGraphicsMod.Alternar(p[1] == "1"); Resp("ok " + l); return;
                case "set":
                    Resp(Cfg.Definir(p[1], p[2]) ? "ok " + l : "erro preferência desconhecida " + p[1]); return;
                case "print":
                    var arq = Path.Combine(_prints, p[1] + ".png");
                    if (File.Exists(arq)) File.Delete(arq);
                    ScreenCapture.CaptureScreenshot(arq);
                    _esperandoArquivo = arq; _limiteArquivo = Time.unscaledTime + 6f; return;
                case "diag": Diag.Gravar(); Resp("ok diag"); return;
                case "mat": Resp(Materiais.Inspecionar(p[1], p.Length > 2 ? int.Parse(p[2]) : 3)); return;
                case "olhar": Resp(Olhar(p.Length > 1 ? F(p[1]) : 0.5f, p.Length > 2 ? F(p[2]) : 0.5f)); return;
                case "censo": Resp(Censo()); return;
                case "interior": Resp(Interior.Estado()); return;
                case "molhado": Resp(Molhado.Estado()); return;
                case "gotas": Resp(WindshieldRain.Estado()); return;
                case "chuva": Resp(Chuva.Estado()); return;
                case "deferred": Resp(DeferredLighting.Estado()); return;
                case "ambiente": Resp(AmbientLight.Estado()); return;
                case "refldebug": Resp(DeferredLighting.ReflDebug(int.Parse(p[1]))); return;  // verde = oclusão especular nas sondas com caixa; vermelho = céu visível no reflexo do céu
                case "ambdebug": Resp(AmbientLight.Debug(int.Parse(p[1]))); return;  // 1 RT3 sem a ambiente da Unity, 2 céu visível, 3 ambiente própria
                case "luzes": Resp(Luzes(p.Length > 1 ? int.Parse(p[1]) : 15)); return;  // luzes N: as N luzes pontuais/spot mais perto da câmera
                case "gotasdebug": Resp(WindshieldRain.Debug(p[1] == "1")); return;
                case "pelicula": Resp(WindshieldRain.SaveFilm(Path.Combine(_prints, "pelicula.png"))); return; // película do para-brisa com as palhetas em vermelho
                case "limpador": PBSLoaderCommonUtils.Veiculos.Comandos.Limpadores(); Resp("ok limpador"); return;              // liga/desliga a chave do limpador
                case "limpadorrapido": PBSLoaderCommonUtils.Veiculos.Comandos.LimpadoresRapidos(); Resp("ok limpador rápido"); return;
                case "molhadodebug": Resp(Molhado.Debug(int.Parse(p[1]))); return;
                case "molhadomapa": Resp(Molhado.SalvarMapa(Path.Combine(_prints, "mapa_abrigo.png"))); return;
                case "interiordebug": Resp(Interior.Debug(p[1] == "1")); return;
                case "sombras": Resp(Sombras()); return; // sombras: projeção/recepção de sombra no ônibus do jogador e luzes ativas
                case "andar": // move o ônibus do jogador para a frente a V m/s (física congelada); "andar 0" para
                    _andar = F(p[1]);
                    var busA = Veiculo.Raiz();
                    if (busA != null)
                        foreach (var rb in busA.GetComponentsInChildren<Rigidbody>())
                        {
                            if (_andar > 0f) { if (!rb.isKinematic) { _rbs.Add(rb); rb.isKinematic = true; } }
                        }
                    if (_andar <= 0f) { foreach (var rb in _rbs) if (rb != null) rb.isKinematic = false; _rbs.Clear(); }
                    Resp("ok " + l); return;
                case "olharb": Resp(OlharB(F(p[1]), F(p[2]))); return;
                case "vol": Resp(Pos.Estado()); return;
                case "tex": // tex TEXTO: formato (sRGB ou linear) da textura de cor dos renderers cujo nome contém TEXTO
                    var vt = new HashSet<IntPtr>(); var sbt = new System.Text.StringBuilder("tex:\n");
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                        if (r.name.Contains(p[1], StringComparison.OrdinalIgnoreCase))
                            foreach (var m in r.sharedMaterials)
                            {
                                var t = m != null && m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                                if (t == null || !vt.Add(t.Pointer) || vt.Count > 12) continue;
                                sbt.AppendLine($"  {r.name} | {t.name} {t.width}x{t.height} {t.graphicsFormat} sRGB {UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat)}");
                            }
                    Resp(sbt.ToString()); return;
                case "dumptex": // dumptex MATERIAL PROP NOME: salva a textura em prints/NOME.png (RGB) e NOME_a.png (alfa)
                    Material md = null;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    {
                        foreach (var m in r.sharedMaterials) if (m != null && m.name.Contains(p[1], StringComparison.OrdinalIgnoreCase) && m.HasProperty(p[2]) && m.GetTexture(p[2]) != null) { md = m; break; }
                        if (md != null) break;
                    }
                    if (md == null) { Resp("dumptex: não achei"); return; }
                    var src = md.GetTexture(p[2]);
                    var rt = RenderTexture.GetTemporary(Math.Min(src.width, 1024), Math.Min(src.height, 1024), 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    Graphics.Blit(src, rt);
                    var ant = RenderTexture.active; RenderTexture.active = rt;
                    var tx = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
                    tx.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tx.Apply();
                    RenderTexture.active = ant; RenderTexture.ReleaseTemporary(rt);
                    var px = tx.GetPixels32();
                    var ta = new Texture2D(tx.width, tx.height, TextureFormat.RGBA32, false, true);
                    var pa = new Color32[px.Length];
                    var pr = new Color32[px.Length];
                    for (int i = 0; i < px.Length; i++) { var c = px[i]; pa[i] = new Color32(c.a, c.a, c.a, 255); pr[i] = new Color32(c.r, c.g, c.b, 255); }
                    ta.SetPixels32(pa); ta.Apply(); tx.SetPixels32(pr); tx.Apply();
                    File.WriteAllBytes(Path.Combine(_prints, p[3] + ".png"), ImageConversion.EncodeToPNG(tx));
                    File.WriteAllBytes(Path.Combine(_prints, p[3] + "_a.png"), ImageConversion.EncodeToPNG(ta));
                    Resp($"dumptex {md.name} {p[2]} {src.name} {src.width}x{src.height} escala {md.GetTextureScale("_MainTex")}"); return;
                case "part": // part: sistemas de partículas ativos (chuva do Enviro e outros)
                    var sbp = new System.Text.StringBuilder("part:\n");
                    foreach (var ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
                    {
                        var pr2 = ps.GetComponent<ParticleSystemRenderer>();
                        var mm = pr2 != null ? pr2.sharedMaterial : null;
                        sbp.AppendLine($"  {ps.transform.root.name}/{ps.name} jogando {ps.isPlaying} n {ps.particleCount}/{ps.main.maxParticles} vel {ps.main.startSpeedMultiplier:0.#} tam {ps.main.startSizeMultiplier:0.###} vida {ps.main.startLifetimeMultiplier:0.#} modo {(pr2 != null ? pr2.renderMode.ToString() : "-")} esticar {(pr2 != null ? pr2.velocityScale : 0f):0.##}/{(pr2 != null ? pr2.lengthScale : 0f):0.##} mat {(mm != null ? mm.name + " [" + (mm.shader != null ? mm.shader.name : "-") + "] cor " + (mm.HasProperty("_TintColor") ? mm.GetColor("_TintColor").ToString() : mm.HasProperty("_Color") ? mm.color.ToString() : "-") : "-")} pos {ps.transform.position}");
                    }
                    Resp(sbp.ToString()); return;
                case "sarjeta": // sarjeta N: câmera olhando o trecho de sarjeta N (de cima da rua, de lado)
                    if (!Chuva.SarjetaN(int.Parse(p[1]), out var sp, out var sf, out var sc)) { Resp("sarjeta: nenhuma"); return; }
                    var olhoS = sp - sc * 2.2f - sf * 1.5f + Vector3.up * 1.4f;
                    _orbita = false; _interno = false; _mapa = false; _camLab = true;
                    _camPos = olhoS; _camRot = Quaternion.LookRotation(sp + sf * 0.8f - olhoS, Vector3.up);
                    Resp("ok sarjeta " + p[1]); return;
                case "matset": // matset TEXTO PROP VALOR: float em todos os materiais de renderers cujo nome contém TEXTO
                    int nm = 0;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                        if (r.name.Contains(p[1], StringComparison.OrdinalIgnoreCase))
                            foreach (var m in r.sharedMaterials)
                                if (m != null && m.HasProperty(p[2])) { m.SetFloat(p[2], F(p[3])); nm++; }
                    Resp($"matset {nm} materiais"); return;
                case "env": Resp($"env {p[1]}: " + GI.Listar(Enviro(p[1]))); return;           // env fog|fogging|sky|volume|clima|render|luz|ceu
                case "envset": Resp($"envset {p[1]} " + GI.Definir(Enviro(p[1]), p[2], p[3])); return;
                case "rgi": Resp("rgi: " + GI.Listar(GI.Componente)); return;
                case "rgiset": Resp("rgiset " + GI.Definir(GI.Componente, p[1], p[2])); return;
                case "rgiperfil": Resp("rgiperfil: " + GI.Listar(GI.PerfilAtivo)); return;
                case "rgiperfilset": Resp("rgiperfilset " + GI.Definir(GI.PerfilAtivo, p[1], p[2])); return;
                case "rsm": Resp("rsm: " + GI.Listar(GI.MapaSombra) + $" | instalado {Il2CppRadiantGI.RadiantShadowMap.installed}"); return;
                case "rsmset": Resp("rsmset " + GI.Definir(GI.MapaSombra, p[1], p[2])); return;
                case "pplayer":
                    var ly = (Pos.Cam ?? UnityEngine.Camera.main).GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
                    ly.enabled = p[1] == "1"; Resp("ok pplayer " + ly.enabled); return;
                case "matraiz": Resp(MatRaiz(p[1], p[2], p.Length > 3 ? int.Parse(p[3]) : 10)); return;
                case "fps": _fpsInicio = Time.unscaledTime; _fpsQuadros = 0; return;
                case "quadros": _quadros.Clear(); _modulos.Clear(); _qAte = Time.unscaledTime + F(p[1]); _ate = _qAte + 0.1f; return;
                case "info": Resp(Info()); return;
                case "sair": Resp("saindo"); Application.Quit(); return;
                default: Resp("erro comando desconhecido: " + l); return;
            }
        }

        // Objetos do Enviro pelo apelido, para env/envset.
        static object Enviro(string qual)
        {
            var sky = Luz.Sky;
            if (sky == null) return null;
            return qual switch
            {
                "fog" => sky.fogSettings,
                "fogging" => sky.Fog,
                "sky" => sky,
                "volume" => sky.volumeLightSettings,
                "clima" => sky.Weather.currentActiveWeatherPreset,
                "render" => sky.EnviroSkyRender,
                "luz" => sky.lightSettings,
                "ceu" => sky.skySettings,
                "tempo" => sky.GameTime,
                _ => null,
            };
        }

        // Raio a partir de um ponto da tela (0..1): objeto e materiais atingidos.
        static string Olhar(float x, float y)
        {
            Camera(); // o jogo devolve a câmera para a posição dele a cada frame; reposiciona antes do raio
            var cam = Pos.Cam ?? UnityEngine.Camera.main;
            var ray = cam.ViewportPointToRay(new Vector3(x, y, 0f));
            // Pula gatilhos e colisores invisíveis (o mapa tem vários): primeiro com renderer visível.
            var hits = new List<RaycastHit>();
            foreach (var h in Physics.RaycastAll(ray, 500f)) hits.Add(h);
            hits.Sort((a, b) => a.distance.CompareTo(b.distance));
            RaycastHit hit = default; bool achou = false;
            foreach (var h in hits)
            {
                if (h.collider.isTrigger) continue;
                var rr = h.collider.GetComponent<Renderer>() ?? h.collider.GetComponentInParent<Renderer>();
                if (rr == null || !rr.enabled) continue;
                hit = h; achou = true; break;
            }
            if (!achou) return "olhar: nada";
            var t = hit.collider.transform;
            string nome = t.name;
            for (var q = t.parent; q != null; q = q.parent) nome = q.name + "/" + nome;
            var r = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
            var sb = new System.Text.StringBuilder($"olhar {hit.distance:0.0} m: {nome}\n");
            if (r != null)
                foreach (var m in r.sharedMaterials)
                    if (m != null) sb.Append("   ").Append(Materiais.Descrever(m, r));
            return sb.ToString();
        }

        // Renderers sob raízes que começam com RAIZ e usam material com TEXTO: nome, textura, metal.
        static string MatRaiz(string raiz, string texto, int n)
        {
            var sb = new System.Text.StringBuilder($"matraiz {raiz} {texto}:\n");
            var cont = new Dictionary<string, int>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!r.transform.root.name.StartsWith(raiz, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    if (!m.name.Contains(texto, StringComparison.OrdinalIgnoreCase) && (m.shader == null || !m.shader.name.Contains(texto, StringComparison.OrdinalIgnoreCase))) continue;
                    var tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                    var mg = m.HasProperty("_MetallicGlossMap") ? m.GetTexture("_MetallicGlossMap") : null;
                    string k = $"{(m.shader != null ? m.shader.name : "-")} | {(tex != null ? tex.name : "-")} | metalMap {(mg != null ? mg.name : "-")} | m{(m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f):0.##} g{(m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : 0f):0.##} | kw {string.Join(",", m.shaderKeywords)} | ex. {r.name}";
                    cont[k] = cont.TryGetValue(k, out int c) ? c + 1 : 1;
                }
            }
            var l = new List<KeyValuePair<string, int>>(cont);
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < l.Count && i < n; i++) sb.AppendLine($"  x{l[i].Value} {l[i].Key}");
            sb.Append($"  ({l.Count} combinações)");
            return sb.ToString();
        }

        // Sem colisor (telhados, vidros, toldos): renderers cuja caixa o raio atravessa, do mais perto.
        static string OlharB(float x, float y)
        {
            Camera(); // o jogo devolve a câmera para a posição dele a cada frame; reposiciona antes do raio
            var cam = Pos.Cam ?? UnityEngine.Camera.main;
            var ray = cam.ViewportPointToRay(new Vector3(x, y, 0f));
            var hits = new List<(float d, Renderer r)>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || !r.isVisible) continue;
                var b = r.bounds;
                if (b.size.magnitude > 80f) continue;
                float d = Raio(ray, b);
                if (d >= 0f) hits.Add((d, r));
            }
            hits.Sort((a, b) => a.d.CompareTo(b.d));
            var sb = new System.Text.StringBuilder($"olharb {x} {y}:\n");
            for (int i = 0; i < hits.Count && i < 6; i++)
            {
                var r = hits[i].r;
                var m = r.sharedMaterial;
                sb.AppendLine($"  {hits[i].d:0.0} m {r.transform.root.name}/{r.name} tam {r.bounds.size} | " + (m != null ? Materiais.Descrever(m, r) : "-"));
            }
            return sb.ToString();
        }

        // Distância de entrada do raio na caixa (método das placas); -1 se não cruza. O out de
        // Bounds.IntersectRay não volta pelo Il2CppInterop (sempre 0).
        static float Raio(Ray ray, Bounds b)
        {
            float tmin = 0f, tmax = 1e9f;
            for (int i = 0; i < 3; i++)
            {
                float o = ray.origin[i], dir = ray.direction[i], mn = b.min[i], mx = b.max[i];
                if (Mathf.Abs(dir) < 1e-6f) { if (o < mn || o > mx) return -1f; continue; }
                float t1 = (mn - o) / dir, t2 = (mx - o) / dir;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = Mathf.Max(tmin, t1); tmax = Mathf.Min(tmax, t2);
                if (tmin > tmax) return -1f;
            }
            return tmin;
        }

        // Renderers por raiz (nomes parecidos agrupados) com os materiais mais comuns.
        static string Sombras()
        {
            var sb = new System.Text.StringBuilder("sombras:\n");
            var bus = Veiculo.Raiz();
            if (bus == null) return "sem ônibus";
            var cont = new Dictionary<string, int>();
            foreach (var r in bus.GetComponentsInChildren<Renderer>(false))
            {
                var m = r.sharedMaterial;
                string k = $"{(m != null && m.shader != null ? m.shader.name : "-")} cast {r.shadowCastingMode} recv {r.receiveShadows} | ex. {r.name}";
                string kk = k.Substring(0, k.IndexOf(" | ex.", StringComparison.Ordinal));
                bool achou = false;
                foreach (var c in cont.Keys) if (c.StartsWith(kk + " |", StringComparison.Ordinal)) { cont[c]++; achou = true; break; }
                if (!achou) cont[k] = 1;
            }
            foreach (var kv in cont) sb.AppendLine($"  x{kv.Value} {kv.Key}");
            foreach (var luz in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (!luz.isActiveAndEnabled || luz.intensity <= 0f) continue;
                float d = Pos.Cam != null ? Vector3.Distance(luz.transform.position, Pos.Cam.transform.position) : 0f;
                if (luz.type != LightType.Directional && d > 30f) continue;
                sb.AppendLine($"  luz {luz.name} {luz.type} int {luz.intensity:0.##} alcance {luz.range:0.#} sombra {luz.shadows} d {d:0.#} raiz {luz.transform.root.name} mask {luz.cullingMask}");
            }
            sb.Append($"  sombras qualidade: dist {QualitySettings.shadowDistance} cascatas {QualitySettings.shadowCascades} proj {QualitySettings.shadowProjection} res {QualitySettings.shadowResolution} shadows {QualitySettings.shadows}");
            return sb.ToString();
        }

        static string Luzes(int n)
        {
            var cam = Pos.Cam;
            if (cam == null) return "sem câmera";
            var c = cam.transform.position;
            var lista = new List<(float d, Light l)>();
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                if (l.type == LightType.Point || l.type == LightType.Spot) lista.Add((Vector3.Distance(l.transform.position, c), l));
            lista.Sort((a, b) => a.d.CompareTo(b.d));
            var sb = new System.Text.StringBuilder($"{lista.Count} luzes pontuais/spot; ativas perto:\n");
            int k = 0;
            foreach (var (d, l) in lista)
            {
                if (!l.enabled || !l.gameObject.activeInHierarchy) continue;
                if (k++ >= n) break;
                var co = l.color;
                sb.AppendLine($"  {d,5:0.0} m {l.type,-5} alcance {l.range,5:0.0} intens {l.intensity,5:0.00} cor ({co.r:0.00},{co.g:0.00},{co.b:0.00}) ângulo {l.spotAngle:0} sombra {l.shadows} modo {l.renderMode} altura {l.transform.position.y - c.y:0.0} | {l.transform.root.name}/{l.name}");
            }
            return sb.ToString();
        }

        static string Censo()
        {
            var grupos = new Dictionary<string, Dictionary<string, int>>();
            var total = new Dictionary<string, int>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                string raiz = r.transform.root.name;
                int corte = raiz.IndexOfAny("0123456789(".ToCharArray());
                if (corte > 3) raiz = raiz.Substring(0, corte);
                if (!grupos.TryGetValue(raiz, out var g)) grupos[raiz] = g = new Dictionary<string, int>();
                total[raiz] = total.TryGetValue(raiz, out int nt) ? nt + 1 : 1;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    string k = (m.shader != null ? m.shader.name : "-") + ":" + m.name.Replace(" (Instance)", "");
                    g[k] = g.TryGetValue(k, out int c) ? c + 1 : 1;
                }
            }
            var raizes = new List<string>(grupos.Keys);
            raizes.Sort((a, b) => total[b].CompareTo(total[a]));
            var sb = new System.Text.StringBuilder("censo:\n");
            for (int i = 0; i < raizes.Count && i < 30; i++)
            {
                var ms = new List<KeyValuePair<string, int>>(grupos[raizes[i]]);
                ms.Sort((a, b) => b.Value.CompareTo(a.Value));
                var top = new List<string>();
                for (int j = 0; j < ms.Count && j < 4; j++) top.Add($"{ms[j].Key} x{ms[j].Value}");
                sb.AppendLine($"  {total[raizes[i]],5}  {raizes[i]} | {string.Join(" | ", top)}");
            }
            return sb.ToString();
        }

        static string Info()
        {
            var sky = Luz.Sky;
            var cam = Pos.Cam ?? UnityEngine.Camera.main;
            var bus = Veiculo.Raiz();
            string clima = "-";
            try { clima = sky?.Weather?.currentActiveWeatherPreset?.Name ?? "-"; } catch { }
            var sl = RenderSettings.sun; string sol = sl != null ? $"sol fwd {sl.transform.forward} int {sl.intensity:0.00} cor {sl.color} céu {Ceu.Visibilidade:0.00} amb {RenderSettings.ambientIntensity:0.00} refl {RenderSettings.reflectionIntensity:0.00} " : "";
            return sol + $"info hora {(sky != null ? sky.GameTime.Hours + ":" + sky.GameTime.Minutes : "-")} clima {clima} molhado {(sky != null ? sky.Weather.curWetness : 0f):0.00} | " +
                   $"cam {(cam != null ? cam.transform.position.ToString() + " " + cam.transform.eulerAngles : "-")} dentro {(cam != null && Veiculo.Dentro(cam.transform.position))} | " +
                   $"{Chao.Estado()} | {Chuva.Estado()} | varredura {Varredura.Vistos} vistos {Varredura.Pendentes} na fila | onibus {(bus != null ? bus.position.ToString() + " fwd " + bus.forward : "-")} caixa {Veiculo.Caixa.size} | fx {Cfg.Ativo.Value} cena {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}";
        }

        static bool _uiOculta;

        static void Ui(bool mostrar)
        {
            _uiOculta = !mostrar;
            if (mostrar)
            {
                foreach (var c in _canvasOcultos) if (c != null) c.enabled = true;
                _canvasOcultos.Clear();
                return;
            }
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                if (c.enabled && c.isRootCanvas) { c.enabled = false; _canvasOcultos.Add(c); }
        }

        // OnLateUpdate: posiciona a câmera depois dos scripts do jogo.
        internal static void Camera()
        {
            if (Ativo && _andar > 0f)
            {
                var bus = Veiculo.Raiz();
                if (bus != null) bus.position += Vector3.ProjectOnPlane(bus.forward, Vector3.up).normalized * _andar * Time.deltaTime;
            }
            if (!Ativo) return;
            var sky = Luz.Sky;
            if (!_camLab)
            {
                if (sky != null && _playerOrig != null && sky.Player == _playerLab) sky.Player = _playerOrig;
                return;
            }
            var cam = Pos.Cam ?? UnityEngine.Camera.main;
            if (cam == null) return;
            // A chuva (partículas do Enviro) segue o "jogador" do Enviro, não a câmera: com a câmera do
            // laboratório em outro lugar a chuva ficava fora do quadro. Um substituto acompanha a câmera.
            if (sky != null)
            {
                if (_playerLab == null) { _playerLab = new GameObject("BetterGraphics_LabEnviroPlayer"); UnityEngine.Object.DontDestroyOnLoad(_playerLab); }
                if (sky.Player != _playerLab) { _playerOrig = sky.Player; sky.Player = _playerLab; }
            }
            var orb = Il2Cpp.MouseOrbitCSHARP.instance;
            if (orb != null && orb.enabled) orb.enabled = false;
            if (_orbita)
            {
                var bus = Veiculo.Raiz();
                if (bus == null) return;
                var alvo = Veiculo.Centro() + Vector3.up * _oAlt;
                var fwd = Vector3.ProjectOnPlane(bus.forward, Vector3.up).normalized;
                var d = Quaternion.AngleAxis(_oYaw, Vector3.up) * fwd;
                float pr = _oPitch * Mathf.Deg2Rad;
                var pos = alvo + d * (Mathf.Cos(pr) * _oDist) + Vector3.up * (Mathf.Sin(pr) * _oDist);
                cam.transform.position = pos;
                cam.transform.rotation = Quaternion.LookRotation(alvo - pos, Vector3.up);
            }
            else if (_mapa)
            {
                if (_refMapa == null) _refMapa = GameObject.Find("_gencol_grama")?.transform;
                if (_refMapa == null) return;
                cam.transform.position = _refMapa.TransformPoint(_camPos);
                cam.transform.rotation = _camRot;
            }
            else if (_interno)
            {
                var bus = Veiculo.Raiz();
                if (bus == null) return;
                cam.transform.position = bus.TransformPoint(_camPos);
                cam.transform.rotation = bus.rotation * _camRot;
            }
            else
            {
                cam.transform.position = _camPos;
                cam.transform.rotation = _camRot;
            }
            if (_playerLab != null) _playerLab.transform.position = cam.transform.position;
        }
    }
}
