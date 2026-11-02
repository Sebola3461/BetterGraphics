using System;
using System.Collections.Generic;
using System.Numerics;

namespace BetterGraphics
{
    // Água do para-brisa, em metros, no plano do vidro: x para a direita, y para cima, origem no canto
    // inferior esquerdo da zona. Não usa UnityEngine: roda numa thread separada e nos testes.
    //  - Gotas: posição, raio e direção. Fundem-se somando volume; as pesadas vencem a aderência e escorrem.
    //  - Película: grade de baixa resolução com a água fina entre as gotas (chuva fina e rastro das que escorrem).
    //  - Palhetas: cada uma chega como o segmento do quadro anterior e o do atual. O que fica na faixa varrida
    //    entre os dois vai junto com a palheta, escorregando para a ponta livre; a película recolhida vira
    //    gotas grandes na frente dela. Quando a palheta volta, essa água fica na borda do arco e escorre.
    internal sealed class RainDropSim
    {
        internal struct Drop
        {
            public Vector2 Pos, Dir;
            public float Radius, Stretch, Travel, Seed;
            public bool Carried;
        }

        // A = ponta presa ao braço (a que menos anda), B = ponta livre.
        internal struct Blade { public Vector2 A0, B0, A1, B1; public float HalfWidth; }

        internal struct Settings
        {
            public float Rain;          // 0 a 1
            public float Speed;         // m/s do ônibus para a frente
            public float GravityWeight, WindWeight;
            public float Adhesion;      // 1 = vidro limpo (ângulo de contato de 75°)
            public float Evaporation;   // fração da película perdida por segundo
            public float Accumulation;  // multiplica a água que a palheta recolhe
        }

        const float PhysicalMaxRadius = 0.003f;
        const float FilmDepth = 0.00008f;          // película cheia = 80 µm de água
        const float PhysicalCarriedRadius = 0.0012f;

        // Escala visual: o para-brisa é visto de longe (~0,9 mm por pixel) e a gota no tamanho real some. Raios,
        // fusão e palheta trabalham no tamanho desenhado; o peso e a aderência, no tamanho físico (raio / Scale),
        // para escorrer como a gota de verdade.
        internal readonly float Scale;
        internal readonly float MaxRadius;
        readonly float _mergeCell, _carriedRadius;

        internal readonly float Width, Height;
        internal readonly int GridW, GridH, Capacity;
        internal readonly float[] Film;
        internal readonly Drop[] Drops;
        internal int Count;

        readonly bool[] _glass;
        readonly Vector2[] _gravity;
        readonly float _cellW, _cellH;
        readonly int _mergeW, _mergeH;
        readonly int[] _mergeHead, _mergeNext;
        readonly Random _rnd;
        readonly List<float> _pending = new();
        float _spawnDebt;

        internal int LastWiped { get; private set; }

        internal RainDropSim(float width, float height, int gridW, int gridH, int capacity, float scale = 1f, int seed = 1234)
        {
            Width = width; Height = height; GridW = gridW; GridH = gridH; Capacity = capacity;
            Scale = MathF.Max(scale, 0.1f);
            MaxRadius = PhysicalMaxRadius * Scale; _mergeCell = 0.008f * Scale; _carriedRadius = PhysicalCarriedRadius * Scale;
            Film = new float[gridW * gridH];
            Drops = new Drop[capacity];
            _glass = new bool[gridW * gridH];
            _gravity = new Vector2[gridW * gridH];
            Array.Fill(_glass, true);
            Array.Fill(_gravity, new Vector2(0f, -1f));
            _cellW = width / gridW; _cellH = height / gridH;
            _mergeW = (int)MathF.Ceiling(width / _mergeCell) + 1; _mergeH = (int)MathF.Ceiling(height / _mergeCell) + 1;
            _mergeHead = new int[_mergeW * _mergeH]; _mergeNext = new int[capacity];
            _rnd = new Random(seed);
        }

        // Onde há vidro e a direção da gravidade no plano dele, por célula da grade. No vidro em pé a gravidade
        // é (0, -1); inclinado, menor e com um pouco de x.
        internal void SetGlass(bool[] glass, Vector2[] gravity)
        {
            Array.Copy(glass, _glass, _glass.Length);
            Array.Copy(gravity, _gravity, _gravity.Length);
        }

        internal int Cell(Vector2 p)
        {
            int x = Math.Clamp((int)(p.X / _cellW), 0, GridW - 1), y = Math.Clamp((int)(p.Y / _cellH), 0, GridH - 1);
            return y * GridW + x;
        }

        internal bool OnGlass(Vector2 p) => p.X >= 0f && p.Y >= 0f && p.X < Width && p.Y < Height && _glass[Cell(p)];

        internal void Step(in Settings s, float dt, IReadOnlyList<Blade> blades)
        {
            Spawn(s, dt);
            Move(s, dt);
            Wipe(s, blades);
            Evaporate(s, dt);
            Merge();
            Compact();
        }

        float Rnd() => (float)_rnd.NextDouble();

        // ---- chuva nova ----

        void Spawn(in Settings s, float dt)
        {
            // Mais gotas no para-brisa andando: ele vai de encontro à chuva. Gotas desenhadas maiores, menos delas.
            _spawnDebt += s.Rain * Width * Height * 1800f / Scale * (1f + s.Speed * 0.08f) * dt;
            int limit = 4000;
            while (_spawnDebt >= 1f && limit-- > 0)
            {
                _spawnDebt -= 1f;
                var p = new Vector2(Rnd() * Width, Rnd() * Height);
                if (!OnGlass(p)) continue;
                float u = Rnd();
                float r = (0.00035f + 0.0012f * u * u + (Rnd() < 0.05f ? 0.0008f : 0f)) * Scale;
                // Cheio, a gota nova toma o lugar de uma gotícula qualquer: assim o arco que a palheta limpou volta a
                // receber chuva em vez de só engordar as gotas que sobraram fora dele.
                Add(p, r);
            }
            if (_spawnDebt > 1f) _spawnDebt = 1f;
            float add = s.Rain * dt * 0.12f;
            if (add > 0f)
                for (int i = 0; i < Film.Length; i++)
                    if (_glass[i]) Film[i] = MathF.Min(Film[i] + add * (0.6f + 0.8f * Noise(i)), 1f);
        }

        // Valor fixo por célula: a película não se espalha por igual.
        static float Noise(int i)
        {
            uint h = (uint)i * 2654435761u; h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12;
            return (h & 1023) / 1023f;
        }

        internal void Add(Vector2 p, float r, bool carried = false, Vector2 dir = default)
        {
            int i;
            if (Count < Capacity) i = Count++;
            else
            {
                i = _rnd.Next(Capacity);
                for (int k = 0; k < 8 && Drops[i].Radius > 0.0006f * Scale; k++) i = _rnd.Next(Capacity);
            }
            Drops[i] = new Drop { Pos = p, Radius = r, Dir = dir == default ? new Vector2(0f, -1f) : dir, Seed = Rnd(), Carried = carried };
        }

        // ---- movimento ----

        // Atrito do vidro em células de ~3 mm (sujeira, riscos): faz a gota parar, desviar e seguir um caminho torto.
        static float Friction(Vector2 p)
        {
            int ix = (int)MathF.Floor(p.X * 330f), iy = (int)MathF.Floor(p.Y * 330f);
            uint h = (uint)(ix * 73856093) ^ (uint)(iy * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return 0.55f + (h & 1023) / 1023f * 0.9f;
        }

        void Move(in Settings s, float dt)
        {
            // Pesos do schema normalizados no padrão (0,5 e 0,8): com eles a física é a calibrada.
            float gravityScale = s.GravityWeight / 0.5f, windScale = s.WindWeight / 0.8f;
            float wind = s.Speed * s.Speed;
            for (int i = 0; i < Count; i++)
            {
                ref var d = ref Drops[i];
                if (d.Radius <= 0f) continue;
                if (!OnGlass(d.Pos)) { d.Radius = 0f; continue; }
                if (d.Carried) continue; // a palheta é que move
                float rmm = d.Radius * 1000f / Scale;
                // Peso ~r² contra a aderência (fixa por ponto); o vento relativo cresce com o quadrado da velocidade,
                // sobe pelo para-brisa e abre para os lados.
                Vector2 force = _gravity[Cell(d.Pos)] * (rmm * rmm * gravityScale);
                // Na frente chata do ônibus o ar bate quase de frente e escoa pelas bordas: no meio o vento que corre
                // pelo vidro é fraco, nas laterais e no alto mais forte. Gotas pequenas ficam até ~60 km/h.
                float fromCenter = MathF.Abs(d.Pos.X / Width * 2f - 1f);
                float flow = 0.3f + 0.7f * MathF.Max(fromCenter, d.Pos.Y / Height);
                float outward = d.Pos.X < Width * 0.5f ? -1f : 1f;
                force += new Vector2(outward * fromCenter * 0.8f, 1f) * (wind * 0.012f * flow * rmm * windScale);
                float adhesion = 2.8f * Friction(d.Pos) * s.Adhesion;
                // Acima de 2,2 mm a gota não se sustenta: escorre de qualquer jeito, soltando contas.
                if (rmm > 2.2f) force += _gravity[Cell(d.Pos)] * adhesion;
                float push = force.Length();
                if (push > adhesion)
                {
                    var dir = Vector2.Normalize(force);
                    // Serpenteia: desvio lateral que muda devagar com a posição e com o atrito dos lados.
                    float side = MathF.Sin(d.Pos.Y * 140f + d.Seed * 6.3f) * 0.35f
                        + (Friction(d.Pos + new Vector2(0.004f, 0f)) - Friction(d.Pos - new Vector2(0.004f, 0f))) * 0.8f;
                    dir = Vector2.Normalize(dir + new Vector2(-dir.Y, dir.X) * side);
                    float speed = MathF.Min((push - adhesion) * 0.08f, 0.8f) * MathF.Sqrt(Scale); // escorrendo 5 a 20 cm/s, no vento até 0,8
                    float step = speed * dt;
                    d.Pos += dir * step;
                    d.Dir = dir;
                    d.Stretch = MathF.Min(speed * 2.5f, 0.7f);
                    d.Travel += step;
                    if (OnGlass(d.Pos)) Film[Cell(d.Pos)] = MathF.Max(Film[Cell(d.Pos)], 0.8f);
                    // Conta de água deixada para trás a cada pouco.
                    float interval = d.Radius * 2.2f + 0.0008f * Scale;
                    while (d.Travel > interval && d.Radius > 0.00045f * Scale)
                    {
                        d.Travel -= interval;
                        float rb = d.Radius * (0.22f + Rnd() * 0.18f);
                        d.Radius = MathF.Cbrt(MathF.Max(d.Radius * d.Radius * d.Radius - rb * rb * rb, 0f));
                        var behind = d.Pos - dir * d.Radius * (1.2f + d.Stretch);
                        if (OnGlass(behind)) Add(behind, rb);
                    }
                }
                else d.Stretch *= 0.9f;
                if (d.Pos.X < 0f || d.Pos.X > Width || d.Pos.Y < 0f || d.Pos.Y > Height) d.Radius = 0f;
            }
        }

        // ---- palhetas ----

        void Wipe(in Settings s, IReadOnlyList<Blade> blades)
        {
            for (int i = 0; i < Count; i++) Drops[i].Carried = false;
            LastWiped = 0;
            if (blades == null) return;
            while (_pending.Count < blades.Count) _pending.Add(0f);
            for (int b = 0; b < blades.Count; b++) WipeBlade(s, blades[b], b);
        }

        void WipeBlade(in Settings s, in Blade bl, int index)
        {
            var move = (bl.A1 + bl.B1 - bl.A0 - bl.B0) * 0.5f;
            var seg = bl.B1 - bl.A1;
            float len = seg.Length();
            if (len < 0.02f || move.LengthSquared() < 1e-10f) return; // parada: não varre
            var along = seg / len;
            var normal = new Vector2(-along.Y, along.X);
            if (Vector2.Dot(normal, move) < 0f) normal = -normal;
            float hw = bl.HalfWidth;

            Vector2 min = Vector2.Min(Vector2.Min(bl.A0, bl.B0), Vector2.Min(bl.A1, bl.B1)) - new Vector2(hw + MaxRadius);
            Vector2 max = Vector2.Max(Vector2.Max(bl.A0, bl.B0), Vector2.Max(bl.A1, bl.B1)) + new Vector2(hw + MaxRadius);

            // Gotas: vão na frente da palheta e escorregam para a ponta livre.
            float drift = 0.35f * s.Accumulation * MathF.Max(move.Length(), 0.002f);
            for (int i = 0; i < Count; i++)
            {
                ref var d = ref Drops[i];
                if (d.Radius <= 0f || d.Pos.X < min.X || d.Pos.Y < min.Y || d.Pos.X > max.X || d.Pos.Y > max.Y) continue;
                if (!Swept(bl, d.Pos, hw + d.Radius)) continue;
                float t = Math.Clamp(Vector2.Dot(d.Pos - bl.A1, along) + drift, 0f, len);
                d.Pos = bl.A1 + along * t + normal * (hw + d.Radius);
                d.Dir = normal; d.Stretch = 0f; d.Carried = true;
                LastWiped++;
            }

            // Película: recolhida e somada ao que a palheta carrega.
            int x0 = Math.Max(0, (int)(min.X / _cellW)), x1 = Math.Min(GridW - 1, (int)(max.X / _cellW));
            int y0 = Math.Max(0, (int)(min.Y / _cellH)), y1 = Math.Min(GridH - 1, (int)(max.Y / _cellH));
            float collected = 0f;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int c = y * GridW + x;
                    if (Film[c] <= 0f) continue;
                    var p = new Vector2((x + 0.5f) * _cellW, (y + 0.5f) * _cellH);
                    if (!Swept(bl, p, hw)) continue;
                    collected += Film[c];
                    Film[c] = 0f;
                }
            _pending[index] += collected * _cellW * _cellH * FilmDepth * s.Accumulation;
            float unit = 4.18879f * PhysicalCarriedRadius * PhysicalCarriedRadius * PhysicalCarriedRadius; // volume de uma gota de 1,2 mm
            int spawned = 0;
            while (_pending[index] >= unit && spawned++ < 64)
            {
                _pending[index] -= unit;
                // Mais na metade de fora: a água corre pela palheta até a ponta.
                float t = len * (0.5f + 0.5f * MathF.Sqrt(Rnd()));
                var p = bl.A1 + along * t + normal * (hw + _carriedRadius);
                if (OnGlass(p)) Add(p, _carriedRadius, true, normal);
            }
        }

        // Dentro da faixa varrida neste quadro (entre o segmento anterior e o atual) ou a menos de "margin" do atual.
        static bool Swept(in Blade bl, Vector2 p, float margin) =>
            InTriangle(p, bl.A0, bl.B0, bl.B1) || InTriangle(p, bl.A0, bl.B1, bl.A1) || DistanceToSegment(p, bl.A1, bl.B1) < margin;

        static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos) && MathF.Abs(Cross(b - a, c - a)) > 1e-9f;
        }

        internal static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-12f), 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }

        // ---- secagem, fusão ----

        void Evaporate(in Settings s, float dt)
        {
            float k = 1f - Math.Clamp(s.Evaporation * dt, 0f, 1f);
            for (int i = 0; i < Film.Length; i++) Film[i] *= k;
            // Sem chuva as gotas secam devagar (as pequenas somem primeiro).
            if (s.Rain >= 0.05f) return;
            float shrink = s.Evaporation * 0.0005f * Scale * dt;
            for (int i = 0; i < Count; i++)
            {
                Drops[i].Radius -= shrink;
                if (Drops[i].Radius < 0.0001f * Scale) Drops[i].Radius = 0f;
            }
        }

        int MergeIndex(Vector2 p) =>
            Math.Clamp((int)(p.Y / _mergeCell), 0, _mergeH - 1) * _mergeW + Math.Clamp((int)(p.X / _mergeCell), 0, _mergeW - 1);

        // Gotas que se encostam viram uma só, com o volume somado, no centro de massa.
        void Merge()
        {
            Array.Fill(_mergeHead, -1);
            for (int i = 0; i < Count; i++)
            {
                if (Drops[i].Radius <= 0f) continue;
                int c = MergeIndex(Drops[i].Pos);
                _mergeNext[i] = _mergeHead[c]; _mergeHead[c] = i;
            }
            for (int i = 0; i < Count; i++)
            {
                if (Drops[i].Radius <= 0f) continue;
                int cx = Math.Clamp((int)(Drops[i].Pos.X / _mergeCell), 0, _mergeW - 1), cy = Math.Clamp((int)(Drops[i].Pos.Y / _mergeCell), 0, _mergeH - 1);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= _mergeW || y >= _mergeH) continue;
                        for (int j = _mergeHead[y * _mergeW + x]; j >= 0; j = _mergeNext[j])
                        {
                            if (j == i || Drops[j].Radius <= 0f || Drops[i].Radius <= 0f) continue;
                            ref var a = ref Drops[i];
                            ref var b = ref Drops[j];
                            if (Vector2.Distance(a.Pos, b.Pos) > (a.Radius + b.Radius) * 0.85f) continue;
                            float va = a.Radius * a.Radius * a.Radius, vb = b.Radius * b.Radius * b.Radius;
                            var center = (a.Pos * va + b.Pos * vb) / (va + vb);
                            float r = MathF.Min(MathF.Cbrt(va + vb), MaxRadius); // o excesso escorre e vai embora
                            bool carried = a.Carried || b.Carried;
                            if (va >= vb) { a.Pos = center; a.Radius = r; a.Carried = carried; b.Radius = 0f; }
                            else { b.Pos = center; b.Radius = r; b.Carried = carried; a.Radius = 0f; }
                        }
                    }
            }
        }

        void Compact()
        {
            int k = 0;
            for (int i = 0; i < Count; i++) if (Drops[i].Radius > 0f) Drops[k++] = Drops[i];
            Count = k;
        }

        // ---- saída para a GPU ----

        // 4 floats por gota: x, y, raio, e direção em graus inteiros + alongamento (0 a 0,7) na fração.
        internal void Encode(float[] buffer)
        {
            Array.Clear(buffer, 0, Math.Min(buffer.Length, Capacity * 4));
            for (int i = 0; i < Count; i++)
            {
                ref var d = ref Drops[i];
                int o = i * 4;
                float deg = MathF.Atan2(d.Dir.Y, d.Dir.X) * (180f / MathF.PI);
                if (deg < 0f) deg += 360f;
                buffer[o] = d.Pos.X; buffer[o + 1] = d.Pos.Y; buffer[o + 2] = d.Radius;
                buffer[o + 3] = MathF.Floor(deg) % 360f + Math.Clamp(d.Stretch / 0.7f, 0f, 0.99f);
            }
        }

        internal (float mean, float max, int moving, int carried) Stats()
        {
            if (Count == 0) return (0f, 0f, 0, 0);
            float sum = 0f, max = 0f; int moving = 0, carried = 0;
            for (int i = 0; i < Count; i++)
            {
                sum += Drops[i].Radius; max = MathF.Max(max, Drops[i].Radius);
                if (Drops[i].Stretch > 0.05f) moving++;
                if (Drops[i].Carried) carried++;
            }
            return (sum / Count, max, moving, carried);
        }
    }
}
