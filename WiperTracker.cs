using System;
using System.Collections.Generic;
using UnityEngine;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;
using NM = System.Numerics.Matrix4x4;

namespace BetterGraphics
{
    // Palhetas do limpador do ônibus do jogador, como segmentos no plano do para-brisa.
    // O jogo anima de dois jeitos: girando o objeto (MEPControlesBus.limpadorEsquerdo/Direito) ou ligando uma
    // malha por posição (limpadoresAWIP/AWIP2). Os dois são lidos igual: a cada quadro, os vértices das peças
    // visíveis que encostam no vidro dão a linha da palheta.
    // Sem palheta modelada, um limpador virtual (ângulos e velocidade do schema) segue a chave do jogo.
    internal static class WiperTracker
    {
        sealed class Part { public Renderer R; public N3[] Verts; }

        sealed class Group
        {
            public string Name;
            public readonly List<Part> Parts = new();
            public bool Has;
            public N2 A, B;
            public float MotionA, MotionB;
        }

        static readonly List<Group> _groups = new();
        static readonly List<N2> _points = new();
        static float _virtualPhase;
        static bool _virtualHas;
        static readonly N2[] _virtualPrev = new N2[4];

        internal static bool Virtual { get; private set; }
        internal static int Contacts { get; private set; }

        internal static void Clear() { _groups.Clear(); _virtualHas = false; Virtual = false; Contacts = 0; }

        internal static void Find(Transform bus)
        {
            Clear();
            var seen = new HashSet<IntPtr>();
            var c = PBSLoaderCommonUtils.Avancado.Controles;
            if (c != null && c.transform.root == bus)
            {
                try { AddObject("limpadorEsquerdo", c.limpadorEsquerdo, seen); } catch { }
                try { AddObject("limpadorDireito", c.limpadorDireito, seen); } catch { }
                try { AddArray("AWIP", c.limpadoresAWIP, seen); } catch { }
                try { AddArray("AWIP2", c.limpadoresAWIP2, seen); } catch { }
            }
            // Ônibus de mod que nomeiam a peça e não a ligam ao controle: um grupo por nome.
            foreach (var r in bus.GetComponentsInChildren<Renderer>(true))
            {
                if (seen.Contains(r.Pointer)) continue;
                var n = r.name.ToLowerInvariant();
                // "_bt..." são os botões do painel ("_btwipers2_on_").
                if ((!n.Contains("wiper") && !n.Contains("limpador")) || n.StartsWith("_bt", StringComparison.Ordinal)) continue;
                seen.Add(r.Pointer);
                var g = _groups.Find(x => x.Name == n);
                if (g == null) _groups.Add(g = new Group { Name = n });
                AddPart(g, r);
            }
            _groups.RemoveAll(g => g.Parts.Count == 0);
            var sb = new System.Text.StringBuilder();
            foreach (var g in _groups) sb.Append($" {g.Name}({g.Parts.Count})");
            Log.Msg(_groups.Count > 0 ? "Limpadores:" + sb : "Limpadores: nenhuma palheta modelada (usa o limpador virtual)");
        }

        static void AddObject(string name, GameObject go, HashSet<IntPtr> seen)
        {
            if (go == null) return;
            var g = new Group { Name = name };
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                if (seen.Add(r.Pointer)) AddPart(g, r);
            _groups.Add(g);
        }

        static void AddArray(string name, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MeshRenderer> arr, HashSet<IntPtr> seen)
        {
            if (arr == null) return;
            var g = new Group { Name = name };
            for (int i = 0; i < arr.Length; i++)
            {
                var r = arr[i];
                if (r != null && seen.Add(r.Pointer)) AddPart(g, r);
            }
            _groups.Add(g);
        }

        static void AddPart(Group g, Renderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) return;
            var v = mesh.vertices;
            int step = Math.Max(1, v.Length / 300);
            var list = new List<N3>();
            for (int i = 0; i < v.Length; i += step) list.Add(new N3(v[i].x, v[i].y, v[i].z));
            g.Parts.Add(new Part { R = r, Verts = list.ToArray() });
        }

        // Unity usa vetor-coluna; System.Numerics, vetor-linha: a matriz vai transposta.
        internal static NM ToNumerics(Matrix4x4 m) => new(
            m.m00, m.m10, m.m20, m.m30,
            m.m01, m.m11, m.m21, m.m31,
            m.m02, m.m12, m.m22, m.m32,
            m.m03, m.m13, m.m23, m.m33);

        internal struct VirtualSettings { public bool Active; public int Switch; public float SweepSpeed, MinAngle, MaxAngle; }

        internal static void Update(Transform bus, GlassMap map, float halfWidth, in VirtualSettings vs, float dt, List<RainDropSim.Blade> output)
        {
            var toBus = ToNumerics(bus.worldToLocalMatrix);
            bool any = false;
            Contacts = 0;
            foreach (var g in _groups)
            {
                _points.Clear();
                foreach (var p in g.Parts)
                {
                    if (p.R == null || p.R.WasCollected || !p.R.enabled || !p.R.gameObject.activeInHierarchy) continue;
                    var m = ToNumerics(p.R.localToWorldMatrix) * toBus;
                    foreach (var v in p.Verts)
                    {
                        var q = N3.Transform(v, m);
                        float u = q.X - map.X0, w = q.Y - map.Y0;
                        if (!map.Sample(u, w, out float z, out float nz)) continue;
                        // A palheta encosta no vidro do lado de fora; o braço fica alguns centímetros à frente.
                        float dist = (q.Z - z) * nz;
                        if (dist < -0.015f || dist > 0.06f) continue;
                        _points.Add(new N2(u, w));
                    }
                }
                Contacts += _points.Count;
                if (!Segment(_points, out var e1, out var e2)) { g.Has = false; continue; }
                any = true;
                if (g.Has)
                {
                    if (N2.Distance(e1, g.A) + N2.Distance(e2, g.B) > N2.Distance(e1, g.B) + N2.Distance(e2, g.A)) (e1, e2) = (e2, e1);
                    g.MotionA += (N2.Distance(e1, g.A) - g.MotionA) * 0.1f;
                    g.MotionB += (N2.Distance(e2, g.B) - g.MotionB) * 0.1f;
                    // A ponta presa ao braço é a que menos anda.
                    if (g.MotionA > g.MotionB * 1.2f + 1e-4f)
                    {
                        (e1, e2) = (e2, e1); (g.A, g.B) = (g.B, g.A); (g.MotionA, g.MotionB) = (g.MotionB, g.MotionA);
                    }
                    output.Add(new RainDropSim.Blade { A0 = g.A, B0 = g.B, A1 = e1, B1 = e2, HalfWidth = halfWidth });
                }
                g.Has = true; g.A = e1; g.B = e2;
            }
            Virtual = !any && vs.Active;
            if (Virtual) VirtualBlades(map, halfWidth, vs, dt, output);
        }

        // Linha principal dos pontos (análise de componentes) e as duas pontas.
        static bool Segment(List<N2> pts, out N2 e1, out N2 e2)
        {
            e1 = e2 = default;
            if (pts.Count < 4) return false;
            var mean = N2.Zero;
            foreach (var p in pts) mean += p;
            mean /= pts.Count;
            float xx = 0f, xy = 0f, yy = 0f;
            foreach (var p in pts) { var d = p - mean; xx += d.X * d.X; xy += d.X * d.Y; yy += d.Y * d.Y; }
            float ang = 0.5f * MathF.Atan2(2f * xy, xx - yy);
            var axis = new N2(MathF.Cos(ang), MathF.Sin(ang));
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in pts) { float t = N2.Dot(p - mean, axis); lo = MathF.Min(lo, t); hi = MathF.Max(hi, t); }
            if (hi - lo < 0.1f) return false;
            e1 = mean + axis * lo; e2 = mean + axis * hi;
            return true;
        }

        // Duas palhetas paralelas com o pivô embaixo, ângulo a partir da vertical. Desligada a chave, termina o
        // ciclo e para em min_angle.
        static void VirtualBlades(GlassMap map, float halfWidth, in VirtualSettings vs, float dt, List<RainDropSim.Blade> output)
        {
            float before = _virtualPhase;
            bool parked = MathF.Abs(MathF.IEEERemainder(_virtualPhase, MathF.PI * 2f)) < 0.05f;
            if (vs.Switch > 0 || !parked)
                _virtualPhase += dt * vs.SweepSpeed * MathF.PI * (vs.Switch == 2 ? 1.6f : 1f);
            if (vs.Switch == 0 && MathF.Floor(before / (MathF.PI * 2f)) != MathF.Floor(_virtualPhase / (MathF.PI * 2f)))
                _virtualPhase = MathF.Round(_virtualPhase / (MathF.PI * 2f)) * MathF.PI * 2f;
            float k = 0.5f - 0.5f * MathF.Cos(_virtualPhase);
            float ang = (vs.MinAngle + (vs.MaxAngle - vs.MinAngle) * k) * (MathF.PI / 180f);
            float len = MathF.Min(map.Height * 0.9f, map.Width * 0.42f);
            var dir = new N2(MathF.Sin(ang), MathF.Cos(ang));
            for (int i = 0; i < 2; i++)
            {
                var pivot = new N2(map.Width * (i == 0 ? 0.27f : 0.73f), map.Height * 0.03f);
                N2 a = pivot + dir * (len * 0.12f), b = pivot + dir * len;
                if (_virtualHas)
                    output.Add(new RainDropSim.Blade { A0 = _virtualPrev[i * 2], B0 = _virtualPrev[i * 2 + 1], A1 = a, B1 = b, HalfWidth = halfWidth });
                _virtualPrev[i * 2] = a; _virtualPrev[i * 2 + 1] = b;
            }
            _virtualHas = true;
        }

        internal static string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Virtual ? "virtual" : "modelo").Append($", {Contacts} pontos de contato |");
            foreach (var g in _groups) sb.Append($" {g.Name}: {(g.Has ? $"({g.A.X:0.00},{g.A.Y:0.00})-({g.B.X:0.00},{g.B.Y:0.00})" : "sem contato")}");
            return sb.ToString();
        }
    }
}
