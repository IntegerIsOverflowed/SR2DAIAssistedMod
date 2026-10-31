// Vector.Arrange.cs - planar arrangement, region boundary, mitred offset and union of closed rings.
// No dependencies. Every crossing between rings (or within a ring) becomes a node, the arrangement is
// traced into faces with half-edges (interior on the left), and a face is selected by its total winding
// number. Regions are returned as boundary rings: counter-clockwise outer rings, clockwise holes.
using System;
using System.Collections.Generic;

namespace Sr2d64CSport
{
    internal struct Pt2
    {
        public double X, Y;
        public Pt2(double x, double y) { X = x; Y = y; }
        public static Pt2 operator +(Pt2 a, Pt2 b) => new Pt2(a.X + b.X, a.Y + b.Y);
        public static Pt2 operator -(Pt2 a, Pt2 b) => new Pt2(a.X - b.X, a.Y - b.Y);
        public static Pt2 operator *(Pt2 a, double s) => new Pt2(a.X * s, a.Y * s);
        public double Length => Math.Sqrt(X * X + Y * Y);
    }

    internal static class OutlineArrange
    {
        /// <summary>One bounded face of an arrangement: boundary ring (counter-clockwise), area and total winding.</summary>
        internal sealed class Face
        {
            public List<Pt2> Ring = new List<Pt2>();
            public double Area;
            public int Winding;
            public bool Kept;
            public double MinX, MinY, MaxX, MaxY;
        }

        /// <summary>Arrangement of one or more closed rings, as half-edges and bounded faces.</summary>
        internal sealed class Planar
        {
            public List<Pt2> Pos = new List<Pt2>();          // vertices: ring vertices first, then crossing nodes
            public int[] Origin = Array.Empty<int>(), Target = Array.Empty<int>();   // per half-edge
            public int[] Rank = Array.Empty<int>();             // index of a half-edge in its origin's outgoing list
            public int[][] Out = Array.Empty<int[]>();            // outgoing half-edges per vertex, counter-clockwise by angle
            public int[] FaceOf = Array.Empty<int>();           // per half-edge: bounded face index, or -1 (unbounded)
            public List<Face> Faces = new List<Face>();

            // face-cycle successor: the outgoing half-edge clockwise-adjacent to the twin of h
            public int Next(int h)
            {
                int[] l = Out[Target[h]];
                return l[(Rank[h ^ 1] - 1 + l.Length) % l.Length];
            }

            /// <summary>Boundary rings of the union of the faces selected by keep (interior on the left).</summary>
            public List<List<Pt2>> BoundaryRings(Func<Face, bool> keep)
            {
                int H = Origin.Length;
                var kept = new bool[Faces.Count];
                for (int f = 0; f < Faces.Count; f++) kept[f] = keep(Faces[f]);
                bool K(int h) { int f = FaceOf[h]; return f >= 0 && kept[f]; }

                var used = new bool[H];
                var result = new List<List<Pt2>>();
                for (int h0 = 0; h0 < H; h0++)
                {
                    if (used[h0] || !K(h0) || K(h0 ^ 1)) continue;   // kept on the left, not on the right
                    var ring = new List<Pt2>();
                    int h = h0, guard = 0;
                    do
                    {
                        used[h] = true;
                        ring.Add(Pos[Origin[h]]);
                        int c = Next(h);
                        int spin = 0;
                        while (K(c ^ 1) && spin++ < H) c = Next(c ^ 1);   // skip edges interior to the union
                        h = c;
                    } while (h != h0 && ++guard < H);
                    if (ring.Count >= 3) SplitPinchedRing(ring, result);
                }
                return result;
            }

            static void SplitPinchedRing(List<Pt2> ring, List<List<Pt2>> outRings)
            {
                int n = ring.Count;
                if (n < 3) return;
                const double tol2 = 1e-7 * 1e-7;
                for (int i = 0; i < n; i++)
                {
                    Pt2 pi = ring[i];
                    for (int j = i + 1; j < n; j++)
                    {
                        double dx = pi.X - ring[j].X, dy = pi.Y - ring[j].Y;
                        if (dx * dx + dy * dy <= tol2)
                        {
                            var a = ring.GetRange(i, j - i);
                            var b = new List<Pt2>(n - (j - i));
                            b.AddRange(ring.GetRange(j, n - j));
                            b.AddRange(ring.GetRange(0, i));
                            SplitPinchedRing(a, outRings);
                            SplitPinchedRing(b, outRings);
                            return;
                        }
                    }
                }
                double area2 = 0;
                for (int k = 0; k < n; k++)
                {
                    Pt2 p = ring[k], q = ring[(k + 1) % n];
                    area2 += p.X * q.Y - q.X * p.Y;
                }
                if (Math.Abs(area2) > 1e-10) outRings.Add(ring);
            }
        }

        /// <summary>Arrangement of closed rings: every crossing is a node, faces get total winding over all rings.</summary>
        public static Planar Arrange(IList<List<Pt2>> rings, double cell = 0.5)
        {
            var g = new Planar();
            // Set only by the rig: one line per step, so "this union is slow" is answered by a measurement of which
            // step owns it. Null in production, and no clock is started when it is.
            var probe = BandProbe;
            var clock = probe == null ? null : System.Diagnostics.Stopwatch.StartNew();
            double mark = 0;
            void Lap(string tag)
            {
                if (probe == null || clock == null) return;
                double t = clock.Elapsed.TotalMilliseconds;
                probe($"  arrange {tag,-24} {t - mark,8:0} ms");
                mark = t;
            }
            int R = rings.Count;
            var vBase = new int[R];
            int N = 0;
            for (int r = 0; r < R; r++) { vBase[r] = N; N += rings[r].Count; }

            var pos = new List<Pt2>(N + 64);
            var segRing = new int[N];
            var segLocal = new int[N];
            var segEnd = new int[N];
            for (int r = 0; r < R; r++)
            {
                int n = rings[r].Count;
                for (int i = 0; i < n; i++)
                {
                    int s = vBase[r] + i;
                    pos.Add(rings[r][i]);
                    segRing[s] = r;
                    segLocal[s] = i;
                    segEnd[s] = vBase[r] + (i + 1) % n;
                }
            }
            g.Pos = pos;
            if (N < 3)
            {
                g.Origin = Array.Empty<int>(); g.Target = Array.Empty<int>(); g.Rank = Array.Empty<int>();
                g.Out = Array.Empty<int[]>(); g.FaceOf = Array.Empty<int>();
                return g;
            }

            // 1. crossings between segments, on a uniform grid of segment boxes
            double minX = double.MaxValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int s = 0; s < N; s++)
            {
                minX = Math.Min(minX, pos[s].X); minY = Math.Min(minY, pos[s].Y);
                maxY = Math.Max(maxY, Math.Max(pos[s].Y, pos[segEnd[s]].Y));
            }
            var grid = new Dictionary<long, List<int>>();
            for (int s = 0; s < N; s++)
            {
                Pt2 a = pos[s], b = pos[segEnd[s]];
                int x0 = (int)Math.Floor((Math.Min(a.X, b.X) - minX) / cell), x1 = (int)Math.Floor((Math.Max(a.X, b.X) - minX) / cell);
                int y0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - minY) / cell), y1 = (int)Math.Floor((Math.Max(a.Y, b.Y) - minY) / cell);
                for (int gx = x0; gx <= x1; gx++)
                    for (int gy = y0; gy <= y1; gy++)
                    {
                        long key = ((long)gx << 32) ^ (uint)gy;
                        if (!grid.TryGetValue(key, out var list)) { list = new List<int>(); grid[key] = list; }
                        list.Add(s);
                    }
            }

            var crossT = new List<double>[N];
            var crossId = new List<int>[N];
            Lap($"grid ({N} segs, {grid.Count} cells)");
            // A pair is tested once, in the cell that holds the lower-left corner of both segment boxes. That cell is shared by
            // both, so the test needs no set of tested pairs (the set grew into hundreds of millions of entries on large offsets).
            int CellX0(int s) => (int)Math.Floor((Math.Min(pos[s].X, pos[segEnd[s]].X) - minX) / cell);
            int CellY0(int s) => (int)Math.Floor((Math.Min(pos[s].Y, pos[segEnd[s]].Y) - minY) / cell);
            foreach (var kv in grid)
            {
                var cellList = kv.Value;
                int cx = (int)(kv.Key >> 32), cy = (int)(kv.Key & 0xFFFFFFFFL);
                int m = cellList.Count;
                for (int p = 0; p < m; p++)
                    for (int q = p + 1; q < m; q++)
                    {
                        int i = cellList[p], j = cellList[q];
                        if (i > j) { int t = i; i = j; j = t; }
                        if (Math.Max(CellX0(i), CellX0(j)) != cx || Math.Max(CellY0(i), CellY0(j)) != cy) continue;
                        if (segRing[i] == segRing[j])
                        {
                            int n = rings[segRing[i]].Count;
                            int li = segLocal[i], lj = segLocal[j];
                            if (lj == li + 1 || (li == 0 && lj == n - 1)) continue;   // adjacent: share a vertex
                        }
                        if (!Cross(pos[i], pos[segEnd[i]], pos[j], pos[segEnd[j]], out double ti, out double uj)) continue;
                        Pt2 X = pos[i] + (pos[segEnd[i]] - pos[i]) * ti;
                        int id = pos.Count;
                        pos.Add(X);
                        if (crossT[i] == null) { crossT[i] = new List<double>(); crossId[i] = new List<int>(); }
                        if (crossT[j] == null) { crossT[j] = new List<double>(); crossId[j] = new List<int>(); }
                        crossT[i].Add(ti); crossId[i].Add(id);
                        crossT[j].Add(uj); crossId[j].Add(id);
                    }
            }

            // 1b. T-junctions: a ring vertex lying on the interior of another segment (a spike tip on another offset edge, or the
            // end of a collinear overlap) splits that segment at the vertex. Without this the crossing search never sees the touch,
            // and the faces on either side are wrong.
            Lap($"crossings (+{pos.Count - N} nodes)");
            const double onTol = 1e-7;
            for (int v = 0; v < N; v++)
            {
                Pt2 P = pos[v];
                int gx = (int)Math.Floor((P.X - minX) / cell), gy = (int)Math.Floor((P.Y - minY) / cell);
                long key = ((long)gx << 32) ^ (uint)gy;
                if (!grid.TryGetValue(key, out var onCell)) continue;
                foreach (int s in onCell)
                {
                    int e = segEnd[s];
                    if (v == s || v == e) continue;
                    Pt2 a = pos[s], r = pos[e] - a;
                    double len2 = r.X * r.X + r.Y * r.Y;
                    if (len2 < 1e-24) continue;
                    Pt2 ap = P - a;
                    double t = (ap.X * r.X + ap.Y * r.Y) / len2;
                    if (t <= 1e-9 || t >= 1 - 1e-9) continue;
                    if ((ap - r * t).Length > onTol) continue;
                    if (crossT[s] == null) { crossT[s] = new List<double>(); crossId[s] = new List<int>(); }
                    crossT[s].Add(t); crossId[s].Add(v);
                }
            }

            // 2. edges: canonicalize coincident vertices (within 1e-7 mm) and deduplicate collinear overlapping edges
            Lap("T-junctions");
            int vCount = pos.Count;
            var vCanon = new int[vCount];
            var vMap = new Dictionary<(long, long), int>(vCount);
            for (int i = 0; i < vCount; i++)
            {
                long qx = (long)Math.Round(pos[i].X * 1e7);
                long qy = (long)Math.Round(pos[i].Y * 1e7);
                var key = (qx, qy);
                if (!vMap.TryGetValue(key, out int canon)) { canon = i; vMap[key] = i; }
                vCanon[i] = canon;
            }
            var eA = new List<int>();
            var eB = new List<int>();
            var seenEdges = new HashSet<long>();
            void AddEdge(int u, int v)
            {
                u = vCanon[u]; v = vCanon[v];
                if (u == v) return;
                int a = Math.Min(u, v), b = Math.Max(u, v);
                long key = ((long)a << 32) | (uint)b;
                if (seenEdges.Add(key)) { eA.Add(u); eB.Add(v); }
            }
            for (int s = 0; s < N; s++)
            {
                int k0 = crossT[s] == null ? 0 : crossT[s].Count;
                if (k0 == 0) { AddEdge(s, segEnd[s]); continue; }
                var order = new int[k0];
                for (int k = 0; k < k0; k++) order[k] = k;
                var ts = crossT[s];
                Array.Sort(order, (x, y) => ts[x].CompareTo(ts[y]));
                int prev = s;
                foreach (int k in order)
                {
                    int cur = crossId[s][k];
                    AddEdge(prev, cur);
                    prev = cur;
                }
                AddEdge(prev, segEnd[s]);
            }

            // 3. half-edges, outgoing lists sorted by angle
            int E = eA.Count, H = 2 * E;
            Lap($"edges ({E}, {vCount} verts)");
            g.Origin = new int[H];
            g.Target = new int[H];
            g.Rank = new int[H];
            g.FaceOf = new int[H];
            var outCount = new int[vCount];
            for (int e = 0; e < E; e++)
            {
                g.Origin[2 * e] = eA[e]; g.Target[2 * e] = eB[e];
                g.Origin[2 * e + 1] = eB[e]; g.Target[2 * e + 1] = eA[e];
                outCount[eA[e]]++; outCount[eB[e]]++;
            }
            g.Out = new int[vCount][];
            for (int v = 0; v < vCount; v++) g.Out[v] = new int[outCount[v]];
            var fill = new int[vCount];
            for (int h = 0; h < H; h++) { int v = g.Origin[h]; g.Out[v][fill[v]++] = h; }
            for (int v = 0; v < vCount; v++)
            {
                if (g.Out[v].Length > 1)
                {
                    Pt2 o = pos[v];
                    int[] outs = g.Out[v];
                    Array.Sort(outs, (x, y) =>
                    {
                        Pt2 dx = pos[g.Target[x]] - o, dy = pos[g.Target[y]] - o;
                        return Math.Atan2(dx.Y, dx.X).CompareTo(Math.Atan2(dy.Y, dy.X));
                    });
                }
                for (int k = 0; k < g.Out[v].Length; k++) g.Rank[g.Out[v][k]] = k;
            }

            // 4. faces: trace each face cycle, keep the bounded ones (positive area)
            Lap($"half-edge order ({H})");
            for (int h = 0; h < H; h++) g.FaceOf[h] = -2;
            var cycle = new List<int>();
            var unbounded = new List<List<int>>();   // cycles with negative area: outer sides of components
            for (int h0 = 0; h0 < H; h0++)
            {
                if (g.FaceOf[h0] != -2) continue;
                cycle.Clear();
                int h = h0;
                do { cycle.Add(h); h = g.Next(h); } while (h != h0 && cycle.Count <= H);
                var ring2 = new List<Pt2>(cycle.Count);
                foreach (int c in cycle) ring2.Add(pos[g.Origin[c]]);
                double area = 0;
                for (int k = 0; k < ring2.Count; k++)
                {
                    Pt2 a = ring2[k], b = ring2[(k + 1) % ring2.Count];
                    area += a.X * b.Y - b.X * a.Y;
                }
                area *= 0.5;
                if (area > 1e-12 && ring2.Count >= 3)
                {
                    int fi = g.Faces.Count;
                    var face = new Face { Ring = ring2, Area = area, MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue };
                    foreach (var v in ring2)
                    {
                        face.MinX = Math.Min(face.MinX, v.X); face.MaxX = Math.Max(face.MaxX, v.X);
                        face.MinY = Math.Min(face.MinY, v.Y); face.MaxY = Math.Max(face.MaxY, v.Y);
                    }
                    g.Faces.Add(face);
                    foreach (int c in cycle) g.FaceOf[c] = fi;
                }
                else
                {
                    foreach (int c in cycle) g.FaceOf[c] = -1;
                    if (area < -1e-12) unbounded.Add(new List<int>(cycle));
                }
            }

            // 4b. a component lying in a bounded face (a hole of that face): its outer side belongs to the smallest
            // bounded face around it, so the face's boundary includes the hole. Cycles enclosed by nothing stay unbounded.
            // The faces are bucketed into rows by their box and each bucket is ordered by area, so a component asks only
            // the faces sharing its row and stops at the first one that holds it. Testing every component against every
            // face was O(outer x faces): 2.7 s of the 17.7 s the tangled shape's outward offset cost.
            Lap($"faces ({g.Faces.Count} bounded, {unbounded.Count} outer)");
            // One row geometry serves this step and step 5. The row height is the arrangement's own cell unless the box
            // is so tall that the bucket array would be the problem, in which case the rows get coarser: a bucketing by
            // y range is exact at any height, and coarser rows only make the buckets longer.
            double rowH = Math.Max(cell, (maxY - minY) / 65536.0);
            int nRow = (int)Math.Floor((maxY - minY) / rowH) + 1;
            var faceRow = new List<int>[nRow];
            for (int fi = 0; fi < g.Faces.Count; fi++)
            {
                var face = g.Faces[fi];
                int y0 = (int)Math.Floor((face.MinY - minY) / rowH), y1 = (int)Math.Floor((face.MaxY - minY) / rowH);
                if (y0 < 0) y0 = 0;
                if (y1 > nRow - 1) y1 = nRow - 1;
                for (int gy = y0; gy <= y1; gy++) (faceRow[gy] ??= new List<int>()).Add(fi);
            }
            foreach (var bucket in faceRow)
                if (bucket != null && bucket.Count > 1)
                    bucket.Sort((x, y) =>
                    {
                        int c = g.Faces[x].Area.CompareTo(g.Faces[y].Area);
                        return c != 0 ? c : x.CompareTo(y);       // the tie the old full scan broke by face index
                    });
            foreach (var neg in unbounded)
            {
                double negArea2 = 0, bestLen = -1;
                int c0 = neg[0];
                for (int k = 0; k < neg.Count; k++)
                {
                    int ck = neg[k];
                    Pt2 pa = pos[g.Origin[ck]], pb = pos[g.Target[ck]];
                    negArea2 += pa.X * pb.Y - pb.X * pa.Y;
                    double l = (pb - pa).Length;
                    if (l > bestLen) { bestLen = l; c0 = ck; }
                }
                double negAbsArea = Math.Abs(negArea2) * 0.5;
                Pt2 a = pos[g.Origin[c0]], b = pos[g.Target[c0]];
                Pt2 dd = b - a;
                double dl = dd.Length;
                if (dl < 1e-12) continue;
                Pt2 left = new Pt2(-dd.Y / dl, dd.X / dl);     // the component's outside: the face that encloses it
                Pt2 q = (a + b) * 0.5 + left * 1e-6;
                int gy = (int)Math.Floor((q.Y - minY) / rowH);
                if (gy < 0 || gy > nRow - 1 || faceRow[gy] == null) continue;
                int best = -1;
                foreach (int fi in faceRow[gy])               // ascending area: the first face holding q is the smallest that does
                {
                    var face = g.Faces[fi];
                    if (face.Area <= negAbsArea) continue;
                    if (q.X < face.MinX || q.X > face.MaxX || q.Y < face.MinY || q.Y > face.MaxY) continue;
                    if (Winding(face.Ring, q) != 0) { best = fi; break; }
                }
                if (best >= 0) foreach (int c in neg) g.FaceOf[c] = best;
            }

            // 5. total winding of each face, from a point just inside its longest edge. Taken as one signed count of the
            // crossings of the ray from that point towards +X over every ring's segments, bucketed into the rows above,
            // instead of one Winding call per ring: the crossing rule is a sum of per-segment terms, so the rings never
            // need separating and the ones that cannot reach the ray are never walked. Per face per ring was
            // O(faces x segments) - 14.6 s of the 17.7 s the tangled shape's outward offset cost.
            Lap("hole attach");
            var segRow = new List<int>[nRow];
            var segMaxX = new double[N];
            for (int s = 0; s < N; s++)
            {
                Pt2 a = pos[s], b = pos[segEnd[s]];
                segMaxX[s] = Math.Max(a.X, b.X);
                int y0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - minY) / rowH), y1 = (int)Math.Floor((Math.Max(a.Y, b.Y) - minY) / rowH);
                if (y0 < 0) y0 = 0;
                if (y1 > nRow - 1) y1 = nRow - 1;
                for (int gy = y0; gy <= y1; gy++) (segRow[gy] ??= new List<int>()).Add(s);
            }
            foreach (var f in g.Faces)
            {
                int m = f.Ring.Count, best = 0;
                double bestLen = -1;
                for (int k = 0; k < m; k++)
                {
                    double len = (f.Ring[(k + 1) % m] - f.Ring[k]).Length;
                    if (len > bestLen) { bestLen = len; best = k; }
                }
                Pt2 a = f.Ring[best], b = f.Ring[(best + 1) % m];
                Pt2 d = b - a;
                double dl = d.Length;
                Pt2 left = new Pt2(-d.Y / dl, d.X / dl);
                Pt2 p = (a + b) * 0.5 + left * 1e-6;
                int w = RayWinding(segRow, pos, segEnd, segMaxX, p, minY, rowH, nRow);
                f.Winding = w;
                f.Kept = w != 0;
            }
            Lap($"face winding ({g.Faces.Count} faces over {N} segs)");
            return g;
        }

        /// <summary>The total winding of every arranged ring around p, as one signed count of the crossings of the ray
        /// from p towards +X. The per-segment test is Winding's own, and a segment sits in the bucket of every row its
        /// y range covers, so the row holding p.Y meets each segment exactly once: the total is the same integer as
        /// adding the rings up one by one, without walking the ones that cannot reach the ray.</summary>
        static int RayWinding(List<int>[] rows, List<Pt2> pos, int[] segEnd, double[] segMaxX, Pt2 p, double minY, double rowH, int nRow)
        {
            int gy = (int)Math.Floor((p.Y - minY) / rowH);
            if (gy < 0 || gy > nRow - 1) return 0;                 // no segment reaches this height, so none can cross
            var row = rows[gy];
            if (row == null) return 0;
            int wn = 0;
            for (int k = 0; k < row.Count; k++)
            {
                int s = row[k];
                if (segMaxX[s] <= p.X) continue;                   // wholly left of the ray: a counted crossing is always right of p
                Pt2 a = pos[s], b = pos[segEnd[s]];
                double side = (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);
                if (a.Y <= p.Y)
                {
                    if (b.Y > p.Y && side > 0) wn++;
                }
                else if (b.Y <= p.Y && side < 0) wn--;
            }
            return wn;
        }

        /// <summary>Bounded faces of a single closed ring; Kept is true for nonzero winding.</summary>
        public static List<Face> Faces(IList<Pt2> ring, double cell = 0.5)
        {
            var rings = new List<List<Pt2>> { ring as List<Pt2> ?? new List<Pt2>(ring) };
            return Arrange(rings, cell).Faces;
        }

        /// <summary>Boundary rings of the region enclosed by closed rings (nonzero winding), cleaned of pinch spikes.</summary>
        public static List<List<Pt2>> Region(IList<List<Pt2>> rings)
        {
            var raw = Arrange(rings).BoundaryRings(f => f.Winding != 0);
            var clean = new List<List<Pt2>>(raw.Count);
            foreach (var r in raw)
            {
                var c = CleanRing(r, 0.02);
                if (c.Count >= 3) clean.Add(c);
            }
            return clean;
        }

        /// <summary>
        /// Removes the degenerate geometry where a curve nearly touches itself: vertices closer than tol are merged,
        /// and reversal tips (turns over about 172 degrees whose tip lies on the chord of its neighbours, i.e. zero-width
        /// spikes) are removed. Repeats until stable.
        /// </summary>
        public static List<Pt2> CleanRing(IList<Pt2> src, double tol)
        {
            var p = new List<Pt2>(src);
            for (int pass = 0; pass < 50; pass++)
            {
                bool changed = false;
                var q = new List<Pt2>(p.Count);
                foreach (var v in p)
                    if (q.Count == 0 || (v - q[q.Count - 1]).Length >= tol) q.Add(v);
                while (q.Count > 1 && (q[0] - q[q.Count - 1]).Length < tol) q.RemoveAt(q.Count - 1);
                if (q.Count != p.Count) changed = true;
                p = q;
                int n = p.Count;
                if (n < 4) break;
                var keep = new List<Pt2>(n);
                for (int k = 0; k < n; k++)
                {
                    Pt2 a = p[(k - 1 + n) % n], b = p[k], c = p[(k + 1) % n];
                    Pt2 u = b - a, v = c - b;
                    double lu = u.Length, lv = v.Length;
                    double cosTurn = (lu > 0 && lv > 0) ? (u.X * v.X + u.Y * v.Y) / (lu * lv) : 0;
                    if (lu > 0 && lv > 0 && cosTurn < -0.99 && TipWidth(a, b, c) <= tol)
                    {
                        changed = true; continue;
                    }
                    if (lu > 0 && lv > 0 && cosTurn > 0.999999 && TipWidth(a, b, c) <= 1e-5)
                    {
                        changed = true; continue;
                    }
                    keep.Add(b);
                }
                p = keep;
                if (!changed) break;
            }
            return p;
        }

        /// <summary>
        /// How far the tip <paramref name="b"/> stands off the chord joining its neighbours, i.e. the width of the wedge
        /// the reversal closes. The turn angle alone cannot tell a degenerate spike from a real acute corner: the inner
        /// crossing of a hairpin stroke turns by 172 degrees and hides 300 px² of the shape behind it, and deleting that
        /// vertex on the angle alone filled the whole hairpin. A zero-width round trip has the tip on the chord (or the
        /// neighbours on each other), so this is 0 there and the spike removal still fires.
        /// </summary>
        static double TipWidth(Pt2 a, Pt2 b, Pt2 c)
        {
            Pt2 w = c - a;
            double lw = w.Length;
            if (lw < 1e-12) return 0;
            Pt2 u = b - a;
            return Math.Abs(u.X * w.Y - u.Y * w.X) / lw;
        }

        /// <summary>Drops rings whose absolute area is below minAbsArea (slivers left by the union).</summary>
        public static List<List<Pt2>> DropSmall(IList<List<Pt2>> rings, double minAbsArea)
        {
            var result = new List<List<Pt2>>();
            foreach (var r in rings)
            {
                double a = 0;
                for (int k = 0; k < r.Count; k++)
                {
                    Pt2 p = r[k], q = r[(k + 1) % r.Count];
                    a += p.X * q.Y - q.X * p.Y;
                }
                if (Math.Abs(a * 0.5) >= minAbsArea) result.Add(r);
            }
            return result;
        }

        /// <summary>Union of closed rings: boundary rings of the points whose total winding is at least minWinding.</summary>
        public static List<List<Pt2>> Union(IList<List<Pt2>> rings, int minWinding)
        {
            return Arrange(rings).BoundaryRings(f => f.Winding >= minWinding);
        }

        /// <summary>
        /// Mitred offset of a closed ring. d &gt; 0 moves each edge to the right of its direction (outside a
        /// counter-clockwise region), d &lt; 0 to the left. A join is the miter point when its length is within
        /// miterLimit*|d|, otherwise a bevel (two points). Self-intersections are left for Union.
        /// </summary>
        /// <summary>Smooth curve sample threshold (1 / cos(turn / 2) &lt;= 1.010, i.e. turn &lt;= ~16 deg).
        /// Smooth curve samples use the single bisector offset point instead of two bevel points or separate spike/disc polygons.</summary>
        internal const double SmoothBisectorLimit = 1.010;

        public static List<Pt2> OffsetRing(IList<Pt2> src, double d, double miterLimit)
        {
            var p = new List<Pt2>(src.Count);
            foreach (var q in src)
                if (p.Count == 0 || (q - p[p.Count - 1]).Length > 1e-9) p.Add(q);
            while (p.Count > 1 && (p[0] - p[p.Count - 1]).Length <= 1e-9) p.RemoveAt(p.Count - 1);
            int n = p.Count;
            var outp = new List<Pt2>(n + 8);
            if (n < 3 || d == 0) return outp;
            double effLimit = Math.Max(miterLimit, SmoothBisectorLimit);
            double absD = Math.Abs(d);
            for (int i = 0; i < n; i++)
            {
                Pt2 prev = p[(i - 1 + n) % n], cur = p[i], next = p[(i + 1) % n];
                Pt2 d0 = cur - prev, d1 = next - cur;
                double l0 = d0.Length, l1 = d1.Length;
                if (l0 < 1e-12 || l1 < 1e-12) continue;
                Pt2 u0 = d0 * (1 / l0), u1 = d1 * (1 / l1);
                Pt2 n0 = new Pt2(u0.Y, -u0.X), n1 = new Pt2(u1.Y, -u1.X);   // unit right normals
                double cos = u0.X * u1.X + u0.Y * u1.Y;
                double cross = u0.X * u1.Y - u0.Y * u1.X;
                double den = 1 + cos;
                bool innerJoin = cross * d < 0;
                if (innerJoin)
                {
                    // Clamp inner bisector when |d| * tan(turn/2) exceeds the adjacent edge lengths:
                    // otherwise a sharp inner corner or tight bend at large stroke widths shoots a bow-tie spike
                    // past the ends of the segments and forms phantom loops outside the shape.
                    double sBack = den > 1e-9 ? absD * Math.Abs(cross) / den : 1e30;
                    if (den > 1e-9 && sBack <= 0.95 * Math.Min(l0, l1))
                        outp.Add(cur + (n0 + n1) * (d / den));
                    else
                    {
                        outp.Add(cur + n0 * d);
                        outp.Add(cur + n1 * d);
                    }
                }
                else if (den > 1e-9 && Math.Sqrt(2 / den) <= effLimit)
                {
                    outp.Add(cur + (n0 + n1) * (d / den));
                }
                else
                {
                    outp.Add(cur + n0 * d);
                    outp.Add(cur + n1 * d);
                }
            }
            return outp;
        }

        /// <summary>The miter spikes of a ring grown by d (d &gt; 0): at each convex corner within the miter limit, the quad
        /// corner, two offset points and the miter point. Added to the bevel ring, they give the mitred band.</summary>
        public static List<List<Pt2>> MiterSpikes(IList<Pt2> src, double d, double miterLimit)
        {
            var p = new List<Pt2>(src.Count);
            foreach (var q in src)
                if (p.Count == 0 || (q - p[p.Count - 1]).Length > 1e-9) p.Add(q);
            while (p.Count > 1 && (p[0] - p[p.Count - 1]).Length <= 1e-9) p.RemoveAt(p.Count - 1);
            int n = p.Count;
            var spikes = new List<List<Pt2>>();
            if (n < 3 || d <= 0) return spikes;
            for (int i = 0; i < n; i++)
            {
                Pt2 prev = p[(i - 1 + n) % n], cur = p[i], next = p[(i + 1) % n];
                Pt2 d0 = cur - prev, d1 = next - cur;
                double l0 = d0.Length, l1 = d1.Length;
                if (l0 < 1e-12 || l1 < 1e-12) continue;
                Pt2 u0 = d0 * (1 / l0), u1 = d1 * (1 / l1);
                Pt2 n0 = new Pt2(u0.Y, -u0.X), n1 = new Pt2(u1.Y, -u1.X);
                double cos = u0.X * u1.X + u0.Y * u1.Y;
                double cross = u0.X * u1.Y - u0.Y * u1.X;
                double den = 1 + cos;
                if (den <= 1e-9 || cross * d <= 0) continue;          // straight, or the inner side of a turn (the bevel ring covers it)
                double ratio = Math.Sqrt(2 / den);
                if (ratio <= SmoothBisectorLimit || ratio > miterLimit) continue;
                double ds = d * (1 - 1e-4);
                Pt2 a = cur + n0 * ds, b = cur + n1 * ds, m = cur + (n0 + n1) * (ds / den);
                var quad = new List<Pt2> { cur, a, m, b };
                double area = 0;
                for (int k = 0; k < 4; k++) { var u = quad[k]; var v = quad[(k + 1) % 4]; area += u.X * v.Y - v.X * u.Y; }
                if (Math.Abs(area) < 1e-12) continue;
                if (area < 0) quad.Reverse();
                spikes.Add(quad);
            }
            return spikes;
        }

        /// <summary>
        /// A region grown by d (d &gt; 0) or shrunk by -d (d &lt; 0): the mitred offset rings, unioned by winding.
        /// Returns boundary rings (counter-clockwise outer, clockwise holes).
        /// </summary>
        public static List<List<Pt2>> Filled(IList<List<Pt2>> region, double d, double miterLimit = 4.0)
        {
            if (d == 0) return new List<List<Pt2>>(region);
            var pieces = new List<List<Pt2>>();
            foreach (var r in region)
            {
                var o = OffsetRing(r, d, miterLimit);
                if (o.Count >= 3)
                {
                    if (region.Count > 1)
                        pieces.AddRange(Union(new List<List<Pt2>> { o }, 1));
                    else
                        pieces.Add(o);
                }
            }
            return Tidy(Union(pieces, 1), Math.Abs(d));
        }

        /// <summary>
        /// The band between two offsets of a region: the region grown by dOuter (0 = the region itself) minus the
        /// region offset by dInner (negative = shrunk; 0 = the region itself). Pieces are unioned by winding:
        /// the outer offset counts +1, the reversed inner offset counts -1, and the band is where the total is at least 1.
        /// Used for the outside ring (dOuter = w, dInner = 0), the inside ring (0, -w) and the centre (w/2, -w/2).
        /// </summary>
        /// <summary>The mitred-offset band (the centre ring): grown and shrunk offsets of the region, each side unioned first, then the bars.</summary>
        const double CapsuleSag = 0.003;   // mm: the chords between boundary samples stay this close to the curve
        const double CapsuleMaxStep = 0.5; // mm: longest chord
        const double CapsuleDiscErr = 0.003; // mm: the disc polygon's error, d * (1 / cos(pi / n) - 1)

        /// <summary>
        /// The regular polygon of radius r (the disc), with its vertices on the circle of radius r / cos(pi / n), so it never falls
        /// short of the disc (its error is CapsuleDiscErr at most).
        /// </summary>
        static List<Pt2> DiscPoly(Pt2 c, double r)
        {
            int n = (int)Math.Ceiling(Math.PI / Math.Acos(1.0 / (1.0 + CapsuleDiscErr / r)));
            if (n < 24) n = 24;
            // 0.1 % more than the exact circumradius: the edge midpoints then lie just outside the circle of radius r, so the bevel's
            // end points (which lie on that circle) are never exactly on a polygon edge (coincident geometry breaks the planar union)
            double rc = r / Math.Cos(Math.PI / n) * 1.001;
            var disc = new List<Pt2>(n);
            // half a step turned: a vertex must not sit exactly on a bevel end point (the bevel's ends lie on the circle of radius r),
            // since coincident vertices break the planar union
            for (int j = 0; j < n; j++)
            {
                double th = 2 * Math.PI * (j + 0.5) / n;
                disc.Add(new Pt2(c.X + rc * Math.Cos(th), c.Y + rc * Math.Sin(th)));
            }
            return disc;
        }

        /// <summary>
        /// Round joins: a disc of radius r at each vertex of the ring where the offset of distance r opens a gap, i.e. where
        /// cross * r &gt; 0 (the same test OffsetRing uses), and the turn is large enough for the bevel to differ from the arc.
        /// Small turns are left to the bevel: its error there is r (1 - cos(turn / 2)), about 0.001 r for a turn of 0.05 rad.
        /// </summary>
        static List<List<Pt2>> RoundJoinDiscs(IList<Pt2> src, double r)
        {
            var discs = new List<List<Pt2>>();
            var p = new List<Pt2>(src.Count);
            foreach (var q in src)
                if (p.Count == 0 || (q - p[p.Count - 1]).Length > 1e-9) p.Add(q);
            while (p.Count > 1 && (p[0] - p[p.Count - 1]).Length <= 1e-9) p.RemoveAt(p.Count - 1);
            int n = p.Count;
            if (n < 3) return discs;
            const double minTurn = 0.28;
            for (int i = 0; i < n; i++)
            {
                Pt2 prev = p[(i - 1 + n) % n], cur = p[i], next = p[(i + 1) % n];
                Pt2 d0 = cur - prev, d1 = next - cur;
                double l0 = d0.Length, l1 = d1.Length;
                if (l0 < 1e-12 || l1 < 1e-12) continue;
                Pt2 u0 = d0 * (1 / l0), u1 = d1 * (1 / l1);
                double cross = u0.X * u1.Y - u0.Y * u1.X, dot = u0.X * u1.X + u0.Y * u1.Y;
                double turn = Math.Atan2(Math.Abs(cross), dot);
                if (turn < minTurn) continue;
                // a reversal (the path doubles back, as at the A's right vertex) has no side to read from cross: its round join is a
                // disc on both sides (the stroke's round join is a disc at every vertex, so the extra disc is never wrong)
                bool reversal = Math.Abs(cross) < 1e-6 && dot < 0;
                if (reversal || cross * r > 0) discs.Add(DiscPoly(cur, Math.Abs(r)));
            }
            return discs;
        }

        /// <summary>
        /// The round-join discs of a whole region, with duplicates removed. Two pieces of a region that meet at a vertex - the
        /// crossing of a figure-eight - ask for the same disc, and DiscPoly builds it from the centre and radius alone, so the pair
        /// is byte-identical. A coincident pair is the degenerate input the planar arrangement drops: the eight's grown side then
        /// comes back as two still-crossing rings, and composing those against the region loses the band (2.4 of 187 mm²).
        /// </summary>
        static List<List<Pt2>> RegionDiscs(IList<List<Pt2>> region, double r)
        {
            var all = new List<List<Pt2>>();
            var seen = new List<Pt2>();
            foreach (var ring in region)
                foreach (var d in RoundJoinDiscs(ring, r))
                {
                    double cx = 0, cy = 0;
                    foreach (var q in d) { cx += q.X; cy += q.Y; }
                    var c = new Pt2(cx / d.Count, cy / d.Count);
                    double tol = Math.Abs(r) * 1e-6;
                    bool dup = false;
                    foreach (var q in seen) if ((q - c).Length < tol) { dup = true; break; }
                    if (dup) continue;
                    seen.Add(c);
                    all.Add(d);
                }
            return all;
        }

        /// <summary>
        /// The band of a region's boundary: points within dOuter of the boundary outside the region, and within |dInner| of it inside
        /// (dInner &lt; 0). Built from the mitred offsets (the miter limit is miterLimit; bevel = limit 0). The grown and shrunk sides
        /// are each unioned as one set before they are composed, so overlapping parts count once (figure-eight, T107/T109).
        /// join: 0 miter, 1 bevel, 2 round. A round join adds a disc at each vertex where the gap opens: outside, convex vertices,
        /// unioned with the grown side; inside, reflex vertices, intersected with the region. Extra pieces (zero-width bars from
        /// BarPieces) are added after the composition, so they stay in the result.
        /// </summary>
        public static List<List<Pt2>> Band(IList<List<Pt2>> region, double dOuter, double dInner, double miterLimit = 4.0, double dropScale = 1.0, IList<List<Pt2>>? extra = null, int join = 0)
        {
            double lim = join == 0 ? miterLimit : 0;   // miter: the limit; bevel and round: every gap is cut flat (round adds discs)
            Probe($"in: {region.Count} rings dOuter={dOuter:0.###} dInner={dInner:0.###} join={join} area={SignedArea(region):0.##}");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double mark = 0;
            void Lap(string tag) { if (BandProbe == null) return; double t = clock.Elapsed.TotalMilliseconds; BandProbe($"perf {tag,-26} {t - mark,9:0} ms"); mark = t; }

            // grown side: offsets of the separate pieces of the region (the region itself when dOuter = 0), plus the round discs.
            // When region has multiple rings (e.g. self-crossing lobes of tangled), resolve each ring's grown offset via Union(..., 1)
            // first so a negative-winding concave fold loop of one lobe never cancels a positive face of an adjacent lobe.
            var grown = new List<List<Pt2>>();
            int nDiscs = 0;   // probe only: the join pieces added beside the offset rings
            foreach (var r in region)
            {
                if (dOuter == 0) { grown.Add(new List<Pt2>(r)); continue; }
                var o = OffsetRing(r, dOuter, lim);
                if (o.Count >= 3)
                {
                    if (region.Count > 1)
                        grown.AddRange(Union(new List<List<Pt2>> { o }, 1));
                    else
                        grown.Add(o);
                }
            }
            if (join == 2 && dOuter > 0) { var dd = RegionDiscs(region, dOuter); nDiscs = dd.Count; grown.AddRange(dd); }
            Lap($"grown offsets ({grown.Count} pcs)");
            var positive = (dOuter > 0 || grown.Count > 1) ? Union(grown, 1) : grown;
            Lap($"grown union -> {positive.Count} rings");
            Probe($"grown: {grown.Count} pieces ({nDiscs} discs) -> union {positive.Count} rings area {SignedArea(positive):0.##}");

            // shrunk side (the region itself when dInner = 0): resolve each ring's inward offset via Union({q}, 1) first
            // so self-crossing swallowtail loops at large widths (where a lobe pinches narrower than 2*|dInner|) are stripped
            // per ring and never survive or cancel adjacent lobes.
            var shrunk = new List<List<Pt2>>();
            foreach (var r in region)
            {
                if (dInner == 0) { shrunk.Add(new List<Pt2>(r)); continue; }
                var q = OffsetRing(r, dInner, lim);
                if (q.Count >= 3)
                {
                    if (region.Count > 1)
                        shrunk.AddRange(Union(new List<List<Pt2>> { q }, 1));
                    else
                        shrunk.Add(q);
                }
            }
            var negative = (dInner < 0 || shrunk.Count > 1) ? Union(shrunk, 1) : shrunk;
            Lap($"shrunk offsets+union -> {negative.Count}");
            Probe($"shrunk: {shrunk.Count} pieces -> union {negative.Count} rings area {SignedArea(negative):0.##}");

            var pieces = new List<List<Pt2>>(positive.Count + negative.Count);
            pieces.AddRange(positive);
            foreach (var q in negative) { var rq = new List<Pt2>(q); rq.Reverse(); pieces.Add(rq); }

            // round joins on the inside: the disc at each reflex vertex, where the shrunk side left a gap, kept inside the region
            if (join == 2 && dInner < 0)
            {
                // Cut against the region shrunk by a hair, not the region itself: a disc clipped by a ring edge keeps a straight
                // edge exactly on that ring, and the final union drops a piece whose edge coincides with another piece's edge (the
                // star's five valleys lose about 500 px2 to it). The same reason MiterSpikes scales in by 1e-3; here the gap is
                // 1e-3 of the band, so the piece stays inside the region and the result is unchanged to within it.
                double eps = Math.Abs(dInner) * 1e-3;
                var clip = new List<List<Pt2>>(region.Count);
                foreach (var rr in region)
                {
                    double a = 0;
                    for (int k = 0; k < rr.Count; k++) { var p = rr[k]; var q = rr[(k + 1) % rr.Count]; a += p.X * q.Y - q.X * p.Y; }
                    // move each ring away from the material: a counter-clockwise outer ring shrinks inward (left), a clockwise hole grows inward
                    var inset = OffsetRing(rr, a >= 0 ? -eps : eps, 4.0);
                    clip.Add(inset.Count >= 3 ? inset : new List<Pt2>(rr));
                }
                foreach (var disc in RegionDiscs(region, dInner))
                {
                    var both = new List<List<Pt2>> { disc };
                    both.AddRange(clip);
                    pieces.AddRange(Union(both, 2));
                }
            }

            // extra pieces (counter-clockwise, winding +1) are added AFTER the subtraction: zero-width bars from BarPieces lie inside the
            // region, and they must stay in the result (both rings) rather than be cancelled by it
            if (extra != null) foreach (var e in extra) if (e.Count >= 3) pieces.Add(new List<Pt2>(e));
            if (extra != null && BandProbe != null) for (int i = 0; i < extra.Count; i++) ProbeVerts($"extra {i}", extra[i]);
            Lap($"join discs + extra ({pieces.Count} pcs)");
            var composed = Union(pieces, 1);
            Lap($"compose union -> {composed.Count} rings");
            if (BandProbe != null) for (int i = 0; i < composed.Count; i++) ProbeVerts($"compose {i}", composed[i]);
            var tidyed = Tidy(composed, Math.Max(Math.Abs(dOuter), Math.Abs(dInner)) * dropScale);
            Lap($"tidy -> {tidyed.Count} rings");
            if (BandProbe != null) for (int i = 0; i < tidyed.Count; i++) ProbeVerts($"tidy {i}", tidyed[i]);
            Probe($"pieces: {pieces.Count} -> compose {composed.Count} rings area {SignedArea(composed):0.##} -> tidy {tidyed.Count} rings area {SignedArea(tidyed):0.##}");
            return tidyed;
        }

        /// <summary>Vertex-level dump for the --bandprobe harness: one line per ring, every vertex. Only reached when the
        /// hook is set, so production pays one null check per band. Invariant culture: the dump is read as numbers by
        /// whoever is debugging, and a comma decimal separator would make them two numbers.</summary>
        static void ProbeVerts(string tag, IList<Pt2> r)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(string.Create(inv, $"{tag}: {r.Count} verts"));
            foreach (var p in r) sb.Append(inv, $" ({p.X:0.####},{p.Y:0.####})");
            Probe(sb.ToString());
        }

        /// <summary>Diagnostic hook for the demo's --bandprobe: when set, Band reports the ring count and net signed area of
        /// each of its stages, so a collapsed result can be traced to the stage that lost it. Null everywhere else.</summary>
        internal static Action<string>? BandProbe;
        internal static void Probe(string s) => BandProbe?.Invoke(s);

        static double SignedArea(IList<List<Pt2>> rings)
        {
            double total = 0;
            foreach (var r in rings)
                for (int k = 0; k < r.Count; k++) { var p = r[k]; var q = r[(k + 1) % r.Count]; total += (p.X * q.Y - q.X * p.Y) * 0.5; }
            return total;
        }

        /// <summary>Boundary samples: the first point, then each next point that keeps the chord from it within CapsuleSag of every dense point between.</summary>
        static List<Pt2> ChordSamples(IList<Pt2> r)
        {
            var kept = new List<Pt2>();
            int N = r.Count;
            int i = 0;
            kept.Add(r[0]);
            while (i < N - 1)
            {
                int j = i + 1;
                double run = 0;
                while (j + 1 < N + 0)
                {
                    Pt2 a = r[i], b = r[j + 1];
                    double step = (r[j] - r[j - 1]).Length;
                    if (run + step > CapsuleMaxStep) break;
                    double ch = (b - a).Length;
                    bool ok = true;
                    if (ch > 1e-12)
                        for (int k = i + 1; k <= j && ok; k++)
                        {
                            Pt2 p = r[k] - a, u = b - a;
                            double dist = Math.Abs(p.X * u.Y - p.Y * u.X) / ch;
                            if (dist > CapsuleSag) ok = false;
                        }
                    if (!ok) break;
                    run += step;
                    j++;
                }
                kept.Add(r[j % N]);
                i = j;
                if (j >= N) break;
            }
            // the last kept point may coincide with the first: drop the duplicate close
            if (kept.Count > 1 && (kept[kept.Count - 1] - kept[0]).Length < 1e-9) kept.RemoveAt(kept.Count - 1);
            return kept;
        }

        // cleanup of a union result: pinch spikes removed, then rings under a quarter of the disc of radius D dropped
        static List<List<Pt2>> Tidy(IList<List<Pt2>> rings, double D)
        {
            var cleaned = new List<List<Pt2>>(rings.Count);
            foreach (var r in rings)
            {
                var c = CleanRing(r, 0.02);
                if (c.Count >= 3) cleaned.Add(c);
            }
            // Slivers are small OUTER rings (counter-clockwise). Holes (clockwise) are always kept, however small: dropping a hole
            // fills it, and a small hole is real geometry (e.g. the eroded core of a thick band inside a wide lobe).
            // the sliver threshold is a quarter disc of radius up to 0.5 mm: a wide band must not erase the shape it bands
            double dm = Math.Min(D, 0.5);
            double min = 0.25 * Math.PI * dm * dm;
            var kept = new List<List<Pt2>>(cleaned.Count);
            foreach (var r in cleaned)
            {
                double a = 0;
                for (int k = 0; k < r.Count; k++) { Pt2 p = r[k], q = r[(k + 1) % r.Count]; a += p.X * q.Y - q.X * p.Y; }
                if (a < 0 || a * 0.5 >= min) kept.Add(r);
            }
            return kept;
        }

        /// <summary>
        /// Boolean over groups of closed rings. A group is one region: its rings' nonzero winding, summed, so a
        /// group's own overlaps and orientations never make holes in another group. keep receives, for each face of
        /// the arrangement, whether each group covers it, and returns whether the face is in the result.
        /// Returns the boundary rings of the kept faces, cleaned as Region does.
        /// </summary>
        public static List<List<Pt2>> Compose(IList<IList<List<Pt2>>> groups, Func<bool[], bool> keep)
        {
            var rings = new List<List<Pt2>>();
            var owner = new List<int>();
            var boxes = new List<(double x0, double y0, double x1, double y1)>();
            for (int g = 0; g < groups.Count; g++)
                foreach (var r in groups[g])
                {
                    if (r.Count < 3) continue;
                    double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                    foreach (var q in r)
                    {
                        x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y);
                        x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y);
                    }
                    rings.Add(r); owner.Add(g); boxes.Add((x0, y0, x1, y1));
                }

            var arr = Arrange(rings);
            var kept = new HashSet<Face>();
            var member = new bool[groups.Count];
            var wind = new int[groups.Count];
            foreach (var f in arr.Faces)
            {
                Pt2 p = SamplePoint(f);
                Array.Clear(wind, 0, wind.Length);
                for (int i = 0; i < rings.Count; i++)
                {
                    var b = boxes[i];
                    if (p.X < b.x0 || p.X > b.x1 || p.Y < b.y0 || p.Y > b.y1) continue;   // winding is 0 outside the box
                    wind[owner[i]] += Winding(rings[i], p);
                }
                for (int g = 0; g < member.Length; g++) member[g] = wind[g] != 0;
                if (keep(member)) kept.Add(f);
            }

            var raw = arr.BoundaryRings(f => kept.Contains(f));
            var clean = new List<List<Pt2>>(raw.Count);
            foreach (var r in raw)
            {
                var c = CleanRing(r, 0.02);
                if (c.Count >= 3) clean.Add(c);
            }
            return clean;
        }

        // a point just inside the longest edge of a face (its interior is on the left of each edge)
        static Pt2 SamplePoint(Face f)
        {
            int m = f.Ring.Count, best = 0;
            double bestLen = -1;
            for (int k = 0; k < m; k++)
            {
                double len = (f.Ring[(k + 1) % m] - f.Ring[k]).Length;
                if (len > bestLen) { bestLen = len; best = k; }
            }
            Pt2 a = f.Ring[best], b = f.Ring[(best + 1) % m];
            Pt2 d = b - a;
            double dl = d.Length;
            Pt2 left = new Pt2(-d.Y / dl, d.X / dl);
            return (a + b) * 0.5 + left * 1e-6;
        }

        /// <summary>Nonzero winding number of a closed ring around p (crossing rule).</summary>
        public static int Winding(IList<Pt2> ring, Pt2 p)
        {
            int wn = 0, n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                Pt2 a = ring[i], b = ring[(i + 1) % n];
                double side = (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);
                if (a.Y <= p.Y)
                {
                    if (b.Y > p.Y && side > 0) wn++;
                }
                else if (b.Y <= p.Y && side < 0) wn--;
            }
            return wn;
        }

        // proper crossing of p0-p1 and q0-q1, parameters strictly inside both
        static bool Cross(Pt2 p0, Pt2 p1, Pt2 q0, Pt2 q1, out double t, out double u)
        {
            t = u = 0;
            Pt2 r = p1 - p0, s = q1 - q0;
            double denom = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(denom) < 1e-14) return false;
            Pt2 qp = q0 - p0;
            t = (qp.X * s.Y - qp.Y * s.X) / denom;
            u = (qp.X * r.Y - qp.Y * r.X) / denom;
            const double eps = 1e-9;
            if (!(t > eps && t < 1 - eps && u > eps && u < 1 - eps)) return false;
            // Segments meeting at a shared endpoint only intersect at that node. For near-tangent
            // edges the division above is numerically unstable: the same geometry can evaluate just
            // inside the eps window after a page translation, spawning a phantom crossing node that
            // corrupts the face cycles (observed: both holes of a self-touching figure-8 vanish once
            // the shape sits ~180 mm from the origin). The T-junction pass already owns endpoint
            // contacts, using the same 1e-9 mm geometric tolerance. Squared distances keep the guard
            // out of the sqrt path of the per-pair crossing loop.
            const double onTol2 = 1e-9 * 1e-9;
            double d00x = p0.X - q0.X, d00y = p0.Y - q0.Y;
            double d01x = p0.X - q1.X, d01y = p0.Y - q1.Y;
            double d10x = p1.X - q0.X, d10y = p1.Y - q0.Y;
            double d11x = p1.X - q1.X, d11y = p1.Y - q1.Y;
            if (d00x * d00x + d00y * d00y <= onTol2 || d01x * d01x + d01y * d01y <= onTol2 ||
                d10x * d10x + d10y * d10y <= onTol2 || d11x * d11x + d11y * d11y <= onTol2) return false;
            return true;
        }
    }
}
