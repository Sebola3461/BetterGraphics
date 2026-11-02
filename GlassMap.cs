using System;
using System.Collections.Generic;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace BetterGraphics
{
    // Superfície do para-brisa numa grade, no espaço do ônibus projetado ao longo de z: para cada célula,
    // o z da face de fora, a normal (apontando para a frente) e se ali há vidro.
    internal sealed class GlassMap
    {
        internal float X0, Y0, Width, Height;
        internal int W, H;
        internal float[] Z, NX, NY;
        internal bool[] Covered;

        internal int Cell(float u, float v) =>
            Math.Clamp((int)(v / Height * H), 0, H - 1) * W + Math.Clamp((int)(u / Width * W), 0, W - 1);

        // u, v relativos ao canto da zona (m).
        internal bool Sample(float u, float v, out float z, out float nz)
        {
            z = 0f; nz = 1f;
            if (u < 0f || v < 0f || u >= Width || v >= Height) return false;
            int c = Cell(u, v);
            if (!Covered[c]) return false;
            z = Z[c];
            nz = MathF.Sqrt(MathF.Max(1f - NX[c] * NX[c] - NY[c] * NY[c], 0.04f));
            return true;
        }

        // Rasteriza os triângulos do para-brisa (espaço do ônibus). Com duas faces (vidro com espessura) fica a de fora.
        internal static GlassMap Build(List<(N3 a, N3 b, N3 c)> tris, int w, int h)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var (a, b, c) in tris)
            {
                x0 = MathF.Min(x0, MathF.Min(a.X, MathF.Min(b.X, c.X))); x1 = MathF.Max(x1, MathF.Max(a.X, MathF.Max(b.X, c.X)));
                y0 = MathF.Min(y0, MathF.Min(a.Y, MathF.Min(b.Y, c.Y))); y1 = MathF.Max(y1, MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
            }
            var m = new GlassMap { X0 = x0, Y0 = y0, Width = MathF.Max(x1 - x0, 0.1f), Height = MathF.Max(y1 - y0, 0.1f), W = w, H = h };
            int n = w * h;
            m.Z = new float[n]; m.NX = new float[n]; m.NY = new float[n]; m.Covered = new bool[n];
            float cw = m.Width / w, ch = m.Height / h;
            foreach (var (a, b, c) in tris)
            {
                var nn = N3.Normalize(N3.Cross(b - a, c - a));
                if (nn.Z < 0f) nn = -nn;
                float ax = (a.X - x0) / cw, ay = (a.Y - y0) / ch, bx = (b.X - x0) / cw, by = (b.Y - y0) / ch, cx = (c.X - x0) / cw, cy = (c.Y - y0) / ch;
                float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (MathF.Abs(den) < 1e-9f) continue;
                int ix0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)))), ix1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
                int iy0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)))), iy1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
                for (int y = iy0; y <= iy1; y++)
                    for (int x = ix0; x <= ix1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den;
                        float l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                        float l3 = 1f - l1 - l2;
                        if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                        float z = a.Z * l1 + b.Z * l2 + c.Z * l3;
                        int i = y * w + x;
                        if (m.Covered[i] && z <= m.Z[i]) continue;
                        m.Covered[i] = true; m.Z[i] = z; m.NX[i] = nn.X; m.NY[i] = nn.Y;
                    }
            }
            return m;
        }

        // Valores para a GPU: z, nx, ny e cobertura. Fora do vidro o z e a normal são copiados dos vizinhos, para a
        // filtragem bilinear não puxar a gota da borda para z = 0.
        internal float[] Texture()
        {
            int n = W * H;
            var z = (float[])Z.Clone(); var nx = (float[])NX.Clone(); var ny = (float[])NY.Clone();
            var filled = (bool[])Covered.Clone();
            for (int pass = 0; pass < 8; pass++)
            {
                var next = (bool[])filled.Clone();
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x;
                        if (filled[i]) continue;
                        float sz = 0f, sx = 0f, sy = 0f; int k = 0;
                        for (int d = 0; d < 4; d++)
                        {
                            int xx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), yy = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                            if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                            int j = yy * W + xx;
                            if (!filled[j]) continue;
                            sz += z[j]; sx += nx[j]; sy += ny[j]; k++;
                        }
                        if (k == 0) continue;
                        z[i] = sz / k; nx[i] = sx / k; ny[i] = sy / k; next[i] = true;
                    }
                filled = next;
            }
            var t = new float[n * 4];
            for (int i = 0; i < n; i++) { t[i * 4] = z[i]; t[i * 4 + 1] = nx[i]; t[i * 4 + 2] = ny[i]; t[i * 4 + 3] = Covered[i] ? 1f : 0f; }
            return t;
        }

        // Gravidade no plano do vidro, em coordenadas da zona: (0, -1) no vidro em pé.
        internal N2[] Gravity()
        {
            var g = new N2[W * H];
            for (int i = 0; i < g.Length; i++) g[i] = new N2(NX[i] * NY[i], -(1f - NY[i] * NY[i]));
            return g;
        }
    }
}
