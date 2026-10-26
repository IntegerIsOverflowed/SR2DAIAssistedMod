// Vector.Offset.cs - outline bands by offsetting the contour's own curves (parallel curves), not by sampling the stroke.
// Lines stay lines, cubics stay cubics (refitted within a tolerance), an outside corner gets a bevel (a chamfer),
// and an inside corner is trimmed at the intersection of its two offset curves, so every original node stays a node.
using System;
using System.Collections.Generic;

namespace Sr2d64CSport
{
    internal static class VectorOffset
    {
        struct V
        {
            public double X, Y;
            public V(double x, double y) { X = x; Y = y; }
            public static V operator +(V a, V b) => new V(a.X + b.X, a.Y + b.Y);
            public static V operator -(V a, V b) => new V(a.X - b.X, a.Y - b.Y);
            public static V operator *(V a, double k) => new V(a.X * k, a.Y * k);
            public double Len => Math.Sqrt(X * X + Y * Y);
            public static double Cross(V a, V b) => a.X * b.Y - a.Y * b.X;
            public static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y;
        }

        /// <summary>One piece of a loop: a line (Cub false) or a cubic.</summary>
        struct Seg { public V P0, C1, C2, P1; public bool Cub; }


        // ------------------------------------------------------------------ reading

        /// <summary>The closed loops of a path (open subpaths are not outlines and are skipped).</summary>
        static List<List<Seg>> ReadClosed(VectorPath p)
        {
            var loops = new List<List<Seg>>();
            var cur = new List<Seg>();
            V pos = default, start = default;
            int di = 0; bool open = false;
            void Flush(bool closed)
            {
                if (closed && cur.Count > 0) loops.Add(cur);
                cur = new List<Seg>();
            }
            foreach (var k in p.Kinds)
            {
                switch (k)
                {
                    case VectorPath.K.Move:
                        if (open) Flush(false);
                        pos = start = new V(p.Data[di], p.Data[di + 1]); di += 2; open = true; break;
                    case VectorPath.K.Line:
                        {
                            var q = new V(p.Data[di], p.Data[di + 1]); di += 2;
                            if ((q - pos).Len > 1e-9) cur.Add(new Seg { P0 = pos, P1 = q });
                            pos = q; break;
                        }
                    case VectorPath.K.Cubic:
                        {
                            var c1 = new V(p.Data[di], p.Data[di + 1]); var c2 = new V(p.Data[di + 2], p.Data[di + 3]);
                            var q = new V(p.Data[di + 4], p.Data[di + 5]); di += 6;
                            cur.Add(new Seg { P0 = pos, C1 = c1, C2 = c2, P1 = q, Cub = true });
                            pos = q; break;
                        }
                    case VectorPath.K.Close:
                        if ((start - pos).Len > 1e-9) cur.Add(new Seg { P0 = pos, P1 = start });
                        pos = start; Flush(true); open = false; break;
                }
            }
            if (open) Flush(false);
            return loops;
        }

        // ------------------------------------------------------------------ curve maths

        static V Bez(in Seg s, double t)
        {
            if (!s.Cub) return s.P0 + (s.P1 - s.P0) * t;
            double u = 1 - t;
            return s.P0 * (u * u * u) + s.C1 * (3 * u * u * t) + s.C2 * (3 * u * t * t) + s.P1 * (t * t * t);
        }

        static V Deriv(in Seg s, double t)
        {
            if (!s.Cub) return s.P1 - s.P0;
            double u = 1 - t;
            return (s.C1 - s.P0) * (3 * u * u) + (s.C2 - s.C1) * (6 * u * t) + (s.P1 - s.C2) * (3 * t * t);
        }

        /// <summary>Unit tangent at t; where the derivative vanishes (a cusp end), the nearest non-zero chord.</summary>
        static V Tan(in Seg s, double t)
        {
            var d = Deriv(s, t);
            if (d.Len < 1e-12)
            {
                d = s.Cub ? (t < 0.5 ? s.C1 - s.P0 : s.P1 - s.C2) : s.P1 - s.P0;
                if (d.Len < 1e-12) d = s.P1 - s.P0;
                if (d.Len < 1e-12) d = new V(1, 0);
            }
            return d * (1 / d.Len);
        }

        /// <summary>Point of the offset at distance d (positive = left of the direction of travel).</summary>
        static V Off(in Seg s, double t, double d)
        {
            var T = Tan(s, t);
            return Bez(s, t) + new V(-T.Y, T.X) * d;
        }

        // ------------------------------------------------------------------ offsetting one segment

        /// <summary>
        /// The offset of one segment by d, as ONE segment (no splitting, no sampling). A line offsets to a line.
        /// A cubic offsets to a cubic: end points by the normals, end tangents along the curve, and handle lengths
        /// from the offset speed |B'|(1 - d*k) (k = signed curvature), which is exact at both ends.
        /// </summary>
        static Seg OffsetSeg(in Seg s, double d)
        {
            if (!s.Cub)
            {
                var T = Tan(s, 0.5); var N = new V(-T.Y, T.X) * d;
                return new Seg { P0 = s.P0 + N, P1 = s.P1 + N };
            }
            var O0 = Off(s, 0, d); var O1 = Off(s, 1, d);
            double sp0 = Math.Max(0, Deriv(s, 0).Len * (1 - d * Curv(s, 0)));
            double sp1 = Math.Max(0, Deriv(s, 1).Len * (1 - d * Curv(s, 1)));
            var T0 = Tan(s, 0); var T1 = Tan(s, 1);
            return new Seg { P0 = O0, C1 = O0 + T0 * (sp0 / 3), C2 = O1 - T1 * (sp1 / 3), P1 = O1, Cub = true };
        }

        /// <summary>Signed curvature (positive = turning left, the side Off() offsets to).</summary>
        static double Curv(in Seg s, double t)
        {
            var d1 = Deriv(s, t); double L = d1.Len;
            if (L < 1e-12) return 0;
            return V.Cross(d1, Deriv2(s, t)) / (L * L * L);
        }

        static V Deriv2(in Seg c, double t)
        {
            if (!c.Cub) return new V(0, 0);
            return (c.C2 - c.C1 * 2 + c.P0) * (6 * (1 - t)) + (c.P1 - c.C2 * 2 + c.C1) * (6 * t);
        }

        // ------------------------------------------------------------------ intersections and trimming

        /// <summary>Intersections of two pieces (as parameters on each), found by recursive bounding-box subdivision.</summary>
        static void Intersect(in Seg a, double a0, double a1, in Seg b, double b0, double b1, List<(double, double)> hits, int depth)
        {
            var A = Sub(a, a0, a1); var B = Sub(b, b0, b1);
            if (!Overlap(A, B)) return;
            double sa = Size(A), sb = Size(B);
            if ((sa < 1e-7 && sb < 1e-7) || depth > 60)
            {
                hits.Add(((a0 + a1) / 2, (b0 + b1) / 2)); return;
            }
            if (sa >= sb)
            {
                double m = (a0 + a1) / 2;
                Intersect(a, a0, m, b, b0, b1, hits, depth + 1); Intersect(a, m, a1, b, b0, b1, hits, depth + 1);
            }
            else
            {
                double m = (b0 + b1) / 2;
                Intersect(a, a0, a1, b, b0, m, hits, depth + 1); Intersect(a, a0, a1, b, m, b1, hits, depth + 1);
            }
        }

        static Seg Sub(in Seg s, double t0, double t1)
        {
            if (!s.Cub)
                return new Seg { P0 = Bez(s, t0), P1 = Bez(s, t1) };
            return Split(Split(s, t1, true), t0 / Math.Max(t1, 1e-12), false);
        }

        static bool Overlap(in Seg a, in Seg b)
        {
            var (al, ar, at, ab) = Box(a); var (bl, br, bt, bb) = Box(b);
            return al <= br + 1e-9 && bl <= ar + 1e-9 && at <= bb + 1e-9 && bt <= ab + 1e-9;
        }

        static (double l, double r, double t, double b) Box(in Seg s)
        {
            double l = Math.Min(s.P0.X, s.P1.X), r = Math.Max(s.P0.X, s.P1.X), t = Math.Min(s.P0.Y, s.P1.Y), b = Math.Max(s.P0.Y, s.P1.Y);
            if (s.Cub)
            {
                l = Math.Min(l, Math.Min(s.C1.X, s.C2.X)); r = Math.Max(r, Math.Max(s.C1.X, s.C2.X));
                t = Math.Min(t, Math.Min(s.C1.Y, s.C2.Y)); b = Math.Max(b, Math.Max(s.C1.Y, s.C2.Y));
            }
            return (l, r, t, b);
        }

        static double Size(in Seg s) { var (l, r, t, b) = Box(s); return Math.Max(r - l, b - t); }

        /// <summary>The part of a piece before (keepLeft) or after parameter t (de Casteljau).</summary>
        static Seg Split(in Seg s, double t, bool keepLeft)
        {
            if (!s.Cub)
            {
                var m = Bez(s, t);
                return keepLeft ? new Seg { P0 = s.P0, P1 = m } : new Seg { P0 = m, P1 = s.P1 };
            }
            V a = Lerp(s.P0, s.C1, t), b = Lerp(s.C1, s.C2, t), c = Lerp(s.C2, s.P1, t);
            V d = Lerp(a, b, t), e = Lerp(b, c, t), f = Lerp(d, e, t);
            return keepLeft ? new Seg { P0 = s.P0, C1 = a, C2 = d, P1 = f, Cub = true }
                            : new Seg { P0 = f, C1 = e, C2 = c, P1 = s.P1, Cub = true };
        }

        static V Lerp(V a, V b, double t) => a + (b - a) * t;

        // ------------------------------------------------------------------ one offset loop

        /// <summary>
        /// The loop offset by d (positive = to the left of travel), one piece per original piece, plus joins:
        /// an outside corner gets a bevel (straight chord between the two offset ends); an inside corner is trimmed
        /// where the two offset pieces cross, but only if that crossing is near the original node (within 4 |d|),
        /// otherwise both offset ends are joined through the original node (no long chords).
        /// </summary>
        static List<Seg> OffsetLoop(List<Seg> segs, double d)
        {
            var flat = new List<Seg>(segs.Count);
            var lastOf = new int[segs.Count];
            for (int i = 0; i < segs.Count; i++)
            {
                flat.Add(OffsetSeg(segs[i], d));
                lastOf[i] = i;
            }
            int M = flat.Count;
            var joinPts = new List<V>?[M];   // points between flat[k].P1 and flat[k+1].P0 (empty = direct)
            double reach = 4 * Math.Abs(d);
            if (segs.Count >= 2)
            {
                for (int i = 0; i < segs.Count; i++)
                {
                    int k = lastOf[i], j = (k + 1) % M;
                    var node = segs[i].P1;
                    var tin = Tan(segs[i], 1); var tout = Tan(segs[(i + 1) % segs.Count], 0);
                    double turn = V.Cross(tin, tout);
                    if ((flat[k].P1 - flat[j].P0).Len < 1e-9) continue;   // already joined
                    bool inner = Math.Abs(turn) > 1e-9 && turn * d > 0;
                    if (inner)
                    {
                        var hits = new List<(double, double)>();
                        Intersect(flat[k], 0, 1, flat[j], 0, 1, hits, 0);
                        (double ua, double ub)? best = null; double bd = double.MaxValue;
                        foreach (var h in hits)
                        {
                            double dd = (Bez(flat[k], h.Item1) - node).Len;
                            if (dd < bd) { bd = dd; best = h; }
                        }
                        if (best is (double ua2, double ub2) && bd <= reach)
                        {
                            flat[k] = Split(flat[k], ua2, true);
                            flat[j] = Split(flat[j], ub2, false);
                            continue;
                        }
                        // the offset ends diverge (gap): extend both tangents until they meet, one merged node
                        var mrg = Merge(flat[k], flat[j], reach);
                        joinPts[k] = mrg is V mv ? new List<V> { mv } : new List<V> { node };
                        continue;
                    }
                    // outside corner: one node where the two tangents meet (no chamfer nodes); a chamfer line only if they do not meet
                    var mo = Merge(flat[k], flat[j], reach);
                    joinPts[k] = mo is V mo2 ? new List<V> { mo2 } : new List<V>();
                }
            }
            var result = new List<Seg>(M * 2);
            for (int k = 0; k < M; k++)
            {
                result.Add(flat[k]);
                int j = (k + 1) % M;
                var from = flat[k].P1;
                if (joinPts[k] is List<V> pts)
                {
                    foreach (var q in pts)
                    {
                        if ((q - from).Len > 1e-9) result.Add(new Seg { P0 = from, P1 = q });
                        from = q;
                    }
                }
                if ((flat[j].P0 - from).Len > 1e-9) result.Add(new Seg { P0 = from, P1 = flat[j].P0 });
            }
            return result;
        }

        // ------------------------------------------------------------------ the band

        /// <summary>
        /// The outline band of the closed contours of <paramref name="contour"/>, by offsetting them by <paramref name="distance"/>:
        /// outside = the contour and its copy pushed out; inside = the contour and its copy pushed in. The band is filled even-odd.
        /// </summary>
        /// <summary>
        /// The silhouette of the closed contour pushed out (distance &gt; 0) or in (distance &lt; 0): the boundary of the
        /// filled shape grown or shrunk by |distance|. Each copy is cut where it crosses itself or another copy, and only
        /// the pieces that lie on the right side of the shape and at exactly |distance| from its curves are kept (the
        /// swallow-tails and the overlaps of a figure 8's lobes drop out); the rest is stitched into closed loops.
        /// </summary>
        /// <summary>
        /// The visual outline of a closed curve (what a "smart fill" of its even-odd faces covers): the curve is split at
        /// every self-crossing with its own nodes kept, each piece is kept when the filled side differs on its two sides,
        /// and the kept pieces are stitched into closed loops. No offsetting, no sampling of the output.
        /// </summary>
        public static VectorPath VisualOutline(VectorPath contour)
        {
            var loops = ReadClosed(contour);
            var segs = new List<Seg>(); var owner = new List<int>(); var first = new List<int>(); var last = new List<int>();
            for (int li = 0; li < loops.Count; li++)
            {
                first.Add(segs.Count);
                foreach (var sg in loops[li]) { segs.Add(sg); owner.Add(li); }
                last.Add(segs.Count - 1);
            }
            int n = segs.Count;
            var cuts = new List<double>[n];
            for (int i = 0; i < n; i++) cuts[i] = new List<double>();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    bool junction = owner[i] == owner[j] && (j == i + 1 || (i == first[owner[i]] && j == last[owner[i]]));
                    if (junction) continue;
                    var hits = new List<(double, double)>();
                    Intersect(segs[i], 0, 1, segs[j], 0, 1, hits, 0);
                    foreach (var h in hits)
                        if (h.Item1 > 1e-6 && h.Item1 < 1 - 1e-6 && h.Item2 > 1e-6 && h.Item2 < 1 - 1e-6)
                        { cuts[i].Add(h.Item1); cuts[j].Add(h.Item2); }
                }
            // edges: the curve split at all its crossings; meta = (original segment, t0, t1)
            var edges = new List<Seg>(); var meta = new List<(int gi, double t0, double t1)>();
            for (int i = 0; i < n; i++)
            {
                cuts[i].Sort();
                double prev = 0;
                foreach (double t in cuts[i])
                {
                    if (t - prev < 1e-7) continue;
                    edges.Add(Sub(segs[i], prev, t)); meta.Add((i, prev, t)); prev = t;
                }
                edges.Add(Sub(segs[i], prev, 1)); meta.Add((i, prev, 1));
            }
            // keep the edges where the filled side differs on the two sides (filled = nonzero, or enclosed pocket)
            var Filled = FilledTester(loops, out double eps);
            var bidx = new List<int>();
            for (int e = 0; e < edges.Count; e++)
            {
                V m = Bez(edges[e], 0.5), T = Tan(edges[e], 0.5), N = new V(-T.Y, T.X) * eps;
                if (Filled(m + N) != Filled(m - N)) bidx.Add(e);
            }
            // orient each boundary piece with the filled side on its left
            var dirs = new List<(int e, bool rev)>();
            foreach (int e in bidx)
            {
                V m = Bez(edges[e], 0.5), T = Tan(edges[e], 0.5), N = new V(-T.Y, T.X) * eps;
                dirs.Add((e, !Filled(m + N)));
            }
            V StartOf(int e, bool rev) => rev ? edges[e].P1 : edges[e].P0;
            V EndOf(int e, bool rev) => rev ? edges[e].P0 : edges[e].P1;
            V DirIn(int e, bool rev) => rev ? Tan(edges[e], 0.0) * -1.0 : Tan(edges[e], 1.0);
            V DirOut(int e, bool rev) => rev ? Tan(edges[e], 1.0) * -1.0 : Tan(edges[e], 0.0);
            double tolJoin = eps * 0.1;
            // stitch: at each junction take the most-left turn (keeps each face's boundary together, so pinches
            // come out as separate closed subpaths); a run closes when it returns to its own start point
            var used = new bool[dirs.Count];
            var result = new VectorPath();
            for (int s0 = 0; s0 < dirs.Count; s0++)
            {
                if (used[s0]) continue;
                var run = new List<(int e, bool rev)> { dirs[s0] };
                used[s0] = true;
                V runStart = StartOf(dirs[s0].e, dirs[s0].rev);
                while (true)
                {
                    var (le, lr) = run[run.Count - 1];
                    V end = EndOf(le, lr);
                    if ((end - runStart).Len < tolJoin) break;
                    V din = Unit(DirIn(le, lr));
                    int best = -1; double bestAng = double.MinValue;
                    for (int q = 0; q < dirs.Count; q++)
                    {
                        if (used[q]) continue;
                        var (qe, qr) = dirs[q];
                        if ((StartOf(qe, qr) - end).Len > tolJoin) continue;
                        V dout = Unit(DirOut(qe, qr));
                        double ang = Math.Atan2(din.X * dout.Y - din.Y * dout.X, din.X * dout.X + din.Y * dout.Y);
                        if (ang > bestAng) { bestAng = ang; best = q; }
                    }
                    if (best < 0) break;
                    used[best] = true;
                    run.Add(dirs[best]);
                }
                var loop = new List<Seg>();
                bool open = false; int cg = 0; double ct0 = 0, ct1 = 0;
                foreach (var (e, r) in run)
                {
                    var (gi, t0, t1) = meta[e];
                    if (!r && open && gi == cg && Math.Abs(t0 - ct1) < 1e-9) { ct1 = t1; continue; }
                    if (open) loop.Add(Sub(segs[cg], ct0, ct1));
                    open = false;
                    if (!r) { open = true; cg = gi; ct0 = t0; ct1 = t1; }
                    else loop.Add(ReverseAtoms(new List<Seg> { edges[e] })[0]);
                }
                if (open) loop.Add(Sub(segs[cg], ct0, ct1));
                V endP = EndOf(run[run.Count - 1].e, run[run.Count - 1].rev);
                if ((endP - loop[0].P0).Len > tolJoin) loop.Add(new Seg { P0 = endP, P1 = loop[0].P0 });
                foreach (var sub in SplitAtRepeats(loop, tolJoin)) Emit(result, sub);
            }
            return result;
        }

        /// <summary>Filled test for a point: nonzero winding, or unfilled but enclosed (enclosed pockets count as filled).</summary>
        static Func<V, bool> FilledTester(List<List<Seg>> loops, out double eps)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var L in loops) foreach (var sg in L) { var (l, r, t, b) = Box(sg); minX = Math.Min(minX, l); maxX = Math.Max(maxX, r); minY = Math.Min(minY, t); maxY = Math.Max(maxY, b); }
            eps = 1e-3 * Math.Max(maxX - minX, maxY - minY);
            const int G = 1000;
            double span = Math.Max(maxX - minX, maxY - minY) + 4, cell = span / G;
            double gx0 = (minX + maxX) / 2 - span / 2, gy0 = (minY + maxY) / 2 - span / 2;
            var exterior = ExteriorGrid(loops, gx0, gy0, cell, G);
            return p =>
            {
                if (WindingAt(loops, p) != 0) return true;
                int i = Math.Clamp((int)Math.Floor((p.X - gx0) / cell), 0, G - 1), j = Math.Clamp((int)Math.Floor((p.Y - gy0) / cell), 0, G - 1);
                return !exterior[i, j];
            };
        }

        /// <summary>
        /// Splits a closed loop at every point it passes through twice (a pinch) into separate closed subpaths.
        /// Pieces shorter than tol are degenerate (the outline closing on its own pinch) and are dropped.
        /// </summary>
        static List<List<Seg>> SplitAtRepeats(List<Seg> loop, double tol)
        {
            var clean = new List<Seg>();
            foreach (var sg in loop) if ((sg.P1 - sg.P0).Len > tol) clean.Add(sg);
            if (clean.Count == 0) return new List<List<Seg>>();
            loop = clean;
            for (int i = 0; i < loop.Count; i++)
                for (int j = i + 1; j < loop.Count; j++)
                    if ((loop[i].P0 - loop[j].P0).Len < tol)
                    {
                        var a = loop.GetRange(i, j - i);
                        var b = new List<Seg>(loop.GetRange(j, loop.Count - j));
                        b.AddRange(loop.GetRange(0, i));
                        var res = new List<List<Seg>>();
                        res.AddRange(SplitAtRepeats(a, tol)); res.AddRange(SplitAtRepeats(b, tol));
                        return res;
                    }
            return new List<List<Seg>> { loop };
        }

        static V Unit(V v) => v.Len < 1e-12 ? v : v * (1.0 / v.Len);

        /// <summary>
        /// Normals at every node of an outline: at each node one segment towards the inside (filled side)
        /// and one towards the outside (unfilled side), each of length len. Returned as (x0, y0, x1, y1).
        /// </summary>
        public static List<(double, double, double, double)> NodeNormals(VectorPath outline, VectorPath contour, double len)
        {
            var Filled = FilledTester(ReadClosed(contour), out _);
            var res = new List<(double, double, double, double)>();
            foreach (var sub in ReadClosed(outline))
            {
                int n = sub.Count;
                for (int k = 0; k < n; k++)
                {
                    V p = sub[k].P0;
                    V tin = Unit(Tan(sub[(k - 1 + n) % n], 1.0)), tout = Unit(Tan(sub[k], 0.0));
                    V t = tin + tout;
                    if (t.Len < 1e-9) t = tout;
                    t = Unit(t);
                    V nrm = new V(-t.Y, t.X);
                    V inward = Filled(p + nrm * (len * 0.01)) ? nrm : nrm * -1.0;
                    V a = p + inward * len, b = p - inward * len;
                    res.Add((p.X, p.Y, a.X, a.Y));
                    res.Add((p.X, p.Y, b.X, b.Y));
                }
            }
            return res;
        }

        /// <summary>Grid cells with zero winding that are connected to the grid border (the unbounded unfilled region).</summary>
        static bool[,] ExteriorGrid(List<List<Seg>> loops, double x0, double y0, double cell, int G)
        {
            var zero = new bool[G, G];
            var ext = new bool[G, G];
            for (int i = 0; i < G; i++)
                for (int j = 0; j < G; j++)
                    zero[i, j] = WindingAt(loops, new V(x0 + (i + 0.5) * cell, y0 + (j + 0.5) * cell), 32) == 0;
            var q = new Queue<(int, int)>();
            for (int i = 0; i < G; i++)
                foreach (var (a, b) in new[] { (i, 0), (i, G - 1), (0, i), (G - 1, i) })
                    if (zero[a, b] && !ext[a, b]) { ext[a, b] = true; q.Enqueue((a, b)); }
            int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };
            while (q.Count > 0)
            {
                var (a, b) = q.Dequeue();
                for (int k = 0; k < 4; k++)
                {
                    int na = a + dx[k], nb = b + dy[k];
                    if (na >= 0 && nb >= 0 && na < G && nb < G && zero[na, nb] && !ext[na, nb]) { ext[na, nb] = true; q.Enqueue((na, nb)); }
                }
            }
            return ext;
        }

        /// <summary>Nonzero winding number of a point against all loops (512 steps per segment).</summary>
        static int WindingAt(List<List<Seg>> loops, V q, int steps = 512)
        {
            int w = 0;
            foreach (var L in loops)
                foreach (var s in L)
                {
                    V prev = s.P0;
                    for (int i = 1; i <= steps; i++)
                    {
                        V cur = Bez(s, i / (double)steps);
                        double side = (cur.X - prev.X) * (q.Y - prev.Y) - (q.X - prev.X) * (cur.Y - prev.Y);
                        if (prev.Y <= q.Y) { if (cur.Y > q.Y && side > 0) w++; }
                        else if (cur.Y <= q.Y && side < 0) w--;
                        prev = cur;
                    }
                }
            return w;
        }

        /// <summary>Even-odd parity of a point against all loops (512 steps per segment).</summary>
        static bool ParityAt(List<List<Seg>> loops, V q)
        {
            int crossings = 0;
            foreach (var L in loops)
                foreach (var s in L)
                {
                    V prev = s.P0;
                    for (int i = 1; i <= 512; i++)
                    {
                        V cur = Bez(s, i / 512.0);
                        if ((prev.Y > q.Y) != (cur.Y > q.Y))
                        {
                            double x = prev.X + (q.Y - prev.Y) * (cur.X - prev.X) / (cur.Y - prev.Y);
                            if (x > q.X) crossings++;
                        }
                        prev = cur;
                    }
                }
            return (crossings & 1) == 1;
        }

        /// <summary>The contour split at its self-crossings into simple loops (a figure 8: its two lobes).</summary>
        public static VectorPath Lobes(VectorPath contour)
        {
            var r = new VectorPath();
            foreach (var L0 in ReadClosed(contour))
                foreach (var L in SimpleLoops(L0, 0)) Emit(r, L);
            return r;
        }

        public static VectorPath Inflate(VectorPath contour, float distance)
        {
            var result = new VectorPath();
            foreach (var c in InflateLoops(contour, distance)) Emit(result, c);
            return result;
        }

        static List<List<Seg>> InflateLoops(VectorPath contour, float distance)
        {
            var originals = new List<List<Seg>>();
            var copies = new List<List<Seg>>();
            foreach (var L0 in ReadClosed(contour))
                foreach (var L in SimpleLoops(L0, 0))
                {
                    bool regionLeft = SignedArea(L) > 0;
                    originals.Add(L);
                    copies.Add(OffsetLoop(L, regionLeft ? -distance : distance));
                }
            if (copies.Count == 0 || distance == 0) return new List<List<Seg>>();
            return Silhouette(copies, originals, Math.Abs(distance), distance < 0);
        }

        /// <summary>
        /// Keep the pieces of the copies that are on the wanted side of the shape (inside if wantInside, else outside)
        /// and at distance d from every original curve, and stitch them into closed loops (a piece may run backwards).
        /// </summary>
        static List<List<Seg>> Silhouette(List<List<Seg>> copies, List<List<Seg>> originals, double d, bool wantInside)
        {
            const double tol = 0.01;
            var pieces = new List<(List<Seg> atoms, V start, V end)>();
            for (int a = 0; a < copies.Count; a++)
            {
                var A = copies[a];
                var cuts = new List<double>[A.Count];
                for (int i = 0; i < A.Count; i++) cuts[i] = new List<double>();
                for (int b = 0; b < copies.Count; b++)
                    for (int i = 0; i < A.Count; i++)
                        for (int jj = 0; jj < copies[b].Count; jj++)
                        {
                            if (b == a && jj <= i) continue;                          // each self pair once
                            if (b == a && (jj == i + 1 || (i == 0 && jj == A.Count - 1))) continue;   // adjacent: a junction, not a crossing
                            var hits = new List<(double, double)>();
                            Intersect(A[i], 0, 1, copies[b][jj], 0, 1, hits, 0);
                            foreach (var h in hits)
                            {
                                if (h.Item1 > 1e-6 && h.Item1 < 1 - 1e-6) cuts[i].Add(h.Item1);
                                if (b == a && h.Item2 > 1e-6 && h.Item2 < 1 - 1e-6) cuts[jj].Add(h.Item2);
                            }
                        }
                var atoms = new List<Seg>(); var cutBefore = new List<bool>();
                for (int i = 0; i < A.Count; i++)
                {
                    cuts[i].Sort();
                    double prev = 0; bool first = true;
                    foreach (double t in cuts[i])
                    {
                        if (t - prev < 1e-7) continue;
                        atoms.Add(Sub(A[i], prev, t)); cutBefore.Add(!first); first = false; prev = t;
                    }
                    atoms.Add(Sub(A[i], prev, 1)); cutBefore.Add(!first);
                }
                var starts = new List<int>();
                for (int k = 0; k < atoms.Count; k++) if (cutBefore[k]) starts.Add(k);
                if (starts.Count == 0)
                {
                    if (Keep(atoms, originals, d, wantInside)) pieces.Add((atoms, atoms[0].P0, atoms[^1].P1));
                    continue;
                }
                for (int q = 0; q < starts.Count; q++)
                {
                    int from = starts[q], to = starts[(q + 1) % starts.Count];
                    var piece = new List<Seg>();
                    for (int k = from; ; k = (k + 1) % atoms.Count) { piece.Add(atoms[k]); if (k == (to + atoms.Count - 1) % atoms.Count) break; }
                    if (Keep(piece, originals, d, wantInside)) pieces.Add((piece, piece[0].P0, piece[^1].P1));
                }
            }
            // stitch the kept pieces into loops
            var used = new bool[pieces.Count];
            var result = new List<List<Seg>>();
            for (int s0 = 0; s0 < pieces.Count; s0++)
            {
                if (used[s0]) continue;
                var loop = new List<Seg>(pieces[s0].atoms);
                used[s0] = true;
                V end = pieces[s0].end;
                while (true)
                {
                    int nxt = -1; double best = double.MaxValue; bool rev = false;
                    for (int q = 0; q < pieces.Count; q++)
                    {
                        if (used[q]) continue;
                        double d1 = (pieces[q].start - end).Len, d2 = (pieces[q].end - end).Len;
                        if (d1 < best) { best = d1; nxt = q; rev = false; }
                        if (d2 < best) { best = d2; nxt = q; rev = true; }
                    }
                    if (nxt < 0 || best > tol) break;
                    used[nxt] = true;
                    if (rev) { loop.AddRange(ReverseAtoms(pieces[nxt].atoms)); end = pieces[nxt].start; }
                    else { loop.AddRange(pieces[nxt].atoms); end = pieces[nxt].end; }
                }
                if ((end - loop[0].P0).Len > 1e-9) loop.Add(new Seg { P0 = end, P1 = loop[0].P0 });
                result.Add(loop);
            }
            return result;
        }

        /// <summary>A piece is kept if its middle is on the wanted side of the shape and at least d from every original curve.</summary>
        static bool Keep(List<Seg> piece, List<List<Seg>> originals, double d, bool wantInside)
        {
            V m = Bez(piece[piece.Count / 2], 0.5);
            bool inside = false;
            foreach (var o in originals) if (InsideEvenOdd(new List<List<Seg>> { o }, m)) inside = !inside;
            if (inside != wantInside) return false;
            return DistToLoops(m, originals) >= d * 0.98;
        }

        /// <summary>Distance from a point to the nearest original curve: 256 samples per segment, refined by a local ternary search.</summary>
        static double DistToLoops(V p, List<List<Seg>> loops)
        {
            double best = double.MaxValue;
            foreach (var L in loops)
                foreach (var sg in L)
                {
                    double bt = 0, bd = double.MaxValue;
                    for (int i = 0; i <= 256; i++)
                    {
                        double dd = (Bez(sg, i / 256.0) - p).Len;
                        if (dd < bd) { bd = dd; bt = i / 256.0; }
                    }
                    double lo = Math.Max(0, bt - 1.0 / 256), hi = Math.Min(1, bt + 1.0 / 256);
                    for (int it = 0; it < 30; it++)
                    {
                        double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                        if ((Bez(sg, m1) - p).Len < (Bez(sg, m2) - p).Len) hi = m2; else lo = m1;
                    }
                    bd = Math.Min(bd, (Bez(sg, (lo + hi) / 2) - p).Len);
                    best = Math.Min(best, bd);
                }
            return best;
        }

        /// <summary>
        /// Weld loops that touch at a vertex into one closed loop (the boundary passes that vertex twice), so the
        /// result is one shape, as a boolean union gives in Corel / 3ds Max.
        /// </summary>
        static List<List<Seg>> Weld(List<List<Seg>> loops)
        {
            while (loops.Count > 1 && TryWeldOnce(loops)) { }   // loops that do not touch stay separate
            return loops;
        }

        static bool TryWeldOnce(List<List<Seg>> loops)
        {
            const double tol = 0.01;
            for (int a = 0; a < loops.Count; a++)
                for (int b = a + 1; b < loops.Count; b++)
                    for (int ia = 0; ia < loops[a].Count; ia++)
                        for (int ib = 0; ib < loops[b].Count; ib++)
                            if ((loops[a][ia].P0 - loops[b][ib].P0).Len < tol)
                            {
                                var combined = Rotate(loops[a], ia);
                                combined.AddRange(Rotate(loops[b], ib));
                                loops[a] = combined;
                                loops.RemoveAt(b);
                                return true;
                            }
            return false;
        }

        static List<Seg> Rotate(List<Seg> L, int k)
        {
            var r = new List<Seg>(L.Count);
            for (int i = 0; i < L.Count; i++) r.Add(L[(k + i) % L.Count]);
            return r;
        }

        /// <summary>The same run of segments traversed backwards.</summary>
        static List<Seg> ReverseAtoms(List<Seg> atoms)
        {
            var r = new List<Seg>(atoms.Count);
            for (int i = atoms.Count - 1; i >= 0; i--)
            {
                var s = atoms[i];
                r.Add(new Seg { P0 = s.P1, P1 = s.P0, C1 = s.C2, C2 = s.C1, Cub = s.Cub });
            }
            return r;
        }

        /// <summary>Where the end tangent of a (extended forward) meets the start tangent of b (extended backward); null if they do not converge.</summary>
        static V? Merge(in Seg a, in Seg b, double reach)
        {
            V P = a.P1, Q = b.P0, Ta = Tan(a, 1), Tb = Tan(b, 0);
            double cr = V.Cross(Ta, Tb);
            if (Math.Abs(cr) < 1e-9) return null;
            double s = V.Cross(Q - P, Tb) / cr, u = V.Cross(Q - P, Ta) / cr;   // P + s Ta = Q + u Tb
            if (s < 0 || u > 0) return null;                                    // they overlap (trim case), or do not meet ahead
            V M = P + Ta * s;
            return (M - P).Len <= reach ? M : null;
        }

        static double SignedArea(List<Seg> L)
        {
            double area = 0; V prev = L[0].P0;
            foreach (var s in L)
                for (int i = 1; i <= 16; i++) { V c = Bez(s, i / 16.0); area += prev.X * c.Y - c.X * prev.Y; prev = c; }
            return area / 2;   // > 0: the interior is on the left (the side Off() pushes to with d > 0)
        }

        /// <summary>
        /// Split a loop at its proper self-crossings into simple loops (a figure 8 gives its two lobes).
        /// Tangential contacts (a stroke retraced on itself) are not crossings and are left alone.
        /// </summary>
        static List<List<Seg>> SimpleLoops(List<Seg> loop, int depth)
        {
            var res = new List<List<Seg>>();
            if (depth < 40 && FindCrossing(loop, out int i, out double ti, out int j, out double tj))
            {
                int n = loop.Count;
                var A = new List<Seg> { Sub(loop[i], ti, 1) };
                for (int k = i + 1; k < j; k++) A.Add(loop[k]);
                A.Add(Sub(loop[j], 0, tj));
                var B = new List<Seg> { Sub(loop[j], tj, 1) };
                for (int k = j + 1; k < n; k++) B.Add(loop[k]);
                for (int k = 0; k < i; k++) B.Add(loop[k]);
                B.Add(Sub(loop[i], 0, ti));
                res.AddRange(SimpleLoops(A, depth + 1));
                res.AddRange(SimpleLoops(B, depth + 1));
                return res;
            }
            res.Add(loop);
            return res;
        }

        static bool FindCrossing(List<Seg> L, out int bi, out double bt, out int bj, out double bu)
        {
            int n = L.Count;
            for (int i = 0; i < n; i++)
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;
                    var hits = new List<(double, double)>();
                    Intersect(L[i], 0, 1, L[j], 0, 1, hits, 0);
                    foreach (var h in hits)
                    {
                        double ti = h.Item1, tj = h.Item2;
                        if (ti <= 1e-4 || ti >= 1 - 1e-4 || tj <= 1e-4 || tj >= 1 - 1e-4) continue;
                        if (Math.Abs(V.Cross(Tan(L[i], ti), Tan(L[j], tj))) < 0.2) continue;   // tangential, not a crossing
                        bi = i; bt = ti; bj = j; bu = tj; return true;
                    }
                }
            bi = bj = 0; bt = bu = 0; return false;
        }

        /// <summary>
        /// The band a stroke of this width paints: centred it reaches width/2 each way; inside or outside it covers width/2 on
        /// that side only (that is what the text picture shows - it strokes the side contour twice as wide and clips it at the
        /// contour, so half the pen width is what survives the clip). Same caps, same corner rule, curves stay curves; closed
        /// subpaths, fill even-odd.
        /// </summary>
        public static VectorPath Band(VectorPath contour, float width, VectorOutlinePosition side, VectorCap cap, VectorJoin join, float miterLimit = 4f)
        {
            if (width <= 0) return new VectorPath();
            int ring = side == VectorOutlinePosition.Center ? 0 : side == VectorOutlinePosition.Inside ? 2 : 1;
            int jc = join == VectorJoin.Bevel ? 1 : join == VectorJoin.Round ? 2 : 0;   // the arrangement's 0 miter, 1 bevel, 2 round
            // the grid fallback (only when the fast path declines) samples 8 cells across the band
            return Orient(Band(contour, width / 2.0, width / 8.0, BakeTol, ring, cap, jc, miterLimit));
        }

        /// <summary>Tolerance of the fitted outline, as a fraction of the band distance (0.04 at 1 mm).</summary>
        const double BandTol = 0.04;
        /// <summary>Tolerance of the baked band (bool Band), as a fraction of the band distance (0.001). The fit error at 0.04
        /// biased the curved sides of a small glyph by about 0.1 px per side; at 0.001 the OBD bake matches the clipped
        /// stroke with no pixel over 0.25 inside, and 142 segments (HEAD: 2456). A field, not a constant, so the rig and the
        /// page can A/B the node count this buys against the accuracy it costs instead of arguing about it.</summary>
        public static double BakeTol = BandTol * 0.025;

        /// <summary>Filled region grown by d (d &gt; 0) or shrunk by -d, or null when the contour has an open subpath.</summary>
        /// <summary>False: always use the old offset code (A/B checks).</summary>
        public static bool FastPath = true;


        static VectorPath? FastFilled(VectorPath contour, double d, double tol, double s = 1, double miter = 4)
        {
            if (!FastPath || d == 0 || !TryFlatten(contour, out var rings, out _, s)) return null;
            return FromRings(OutlineArrange.Filled(OutlineArrange.Region(rings), d / s, miter), tol, s);
        }

        /// <summary>Band between the outer offset (dOuter) and the inner offset (dInner), or null when the contour has an open subpath.</summary>
        static VectorPath? FastBand(VectorPath contour, double dOuter, double dInner, double tol, double s = 1, double miter = 4, VectorCap cap = VectorCap.Square, int join = 0)
        {
            if (!FastPath || (dOuter == 0 && dInner == 0)) return null;
            // keepOpen: this entry is the stroke. An open subpath is then the centre line of a band of half-width
            // (dOuter - dInner) / 2, drawn with the caps and joins asked for, which is what the stroker renders; the
            // distance-field tracer it replaces gave every corner a round join and every end a round cap whatever was asked.
            if (!TryFlatten(contour, out var rings, out var lines, s, true)) return null;
            var region = OutlineArrange.Region(rings);
            // zero-area subpaths (a line drawn inside a shape) get a band of their own, as thick as the ring (dOuter - dInner),
            // centred on the line: 2 mm for the centre, 1 mm for outside and inside. Its ends follow cap and may leave the shape.
            var bars = StrokePieces(lines, (dOuter - dInner) / 2 / s, cap, join, miter);
            return FromRings(OutlineArrange.Band(region, dOuter / s, dInner / s, miter, 1.0, bars, join), tol, s);
        }

        /// <summary>False: the piece-per-segment bars with separate join pieces. Kept reachable so the rig can A/B the two
        /// routes; production uses the contour route.</summary>
        public static bool StrokeContours = true;

        static List<List<Pt2>> StrokePieces(List<List<Pt2>> lines, double h, VectorCap cap, int join, double miterLimit)
        {
            if (!StrokeContours) return BarPieces(lines, h, cap, join, miterLimit);
            var pieces = new List<List<Pt2>>();
            if (h <= 0) return pieces;
            foreach (var ln in lines)
            {
                var ring = StrokeRing(ln, h, cap, join, miterLimit);
                if (ring.Count < 3) continue;
                double area = 0;
                for (int k = 0; k < ring.Count; k++) { var p = ring[k]; var q = ring[(k + 1) % ring.Count]; area += p.X * q.Y - q.X * p.Y; }
                if (Math.Abs(area) < 1e-12) continue;
                if (area < 0) ring.Reverse();
                pieces.Add(ring);
            }
            return pieces;
        }

        /// <summary>
        /// The stroke of one open polyline as a single closed contour: the offset on one side, the end cap, the offset back
        /// on the other side, the start cap. One curve is what makes the corners exact - the piece-per-segment route put two
        /// bands with a shared edge into the arrangement, and a collinear overlap is the case it silently drops (its own
        /// comment lists T-junctions and collinear overlaps as still open), so every join but the inseen miter came back
        /// with half the band missing. A path that crosses or doubles back on itself becomes a self-crossing or
        /// self-touching curve here, which the winding rule resolves without any collision between pieces.
        /// A polyline that only runs along one line is first reduced to the interval it sweeps, because its two sides would
        /// otherwise lie exactly on each other; the cap then applies at an extreme only if an end of the path reaches it,
        /// which is what the union of the primitives gives: a turnaround in the middle squares the band off.
        /// </summary>
        static List<Pt2> StrokeRing(IList<Pt2> pts, double h, VectorCap cap, int join, double miterLimit)
        {
            var poly = new List<Pt2>();
            foreach (var q in pts)
                if (poly.Count == 0 || (q - poly[poly.Count - 1]).Length > 1e-9) poly.Add(q);
            if (poly.Count < 2) return new List<Pt2>();
            var caps = new[] { cap, cap };
            if (Collinear(poly, out Pt2 baseP, out Pt2 axis))
            {
                double lo = double.MaxValue, hi = double.MinValue;
                var t = new double[poly.Count];
                for (int i = 0; i < poly.Count; i++)
                {
                    t[i] = (poly[i] - baseP).X * axis.X + (poly[i] - baseP).Y * axis.Y;
                    lo = Math.Min(lo, t[i]); hi = Math.Max(hi, t[i]);
                }
                bool loCapped = Math.Abs(t[0] - lo) < 1e-9 || Math.Abs(t[poly.Count - 1] - lo) < 1e-9;
                bool hiCapped = Math.Abs(t[0] - hi) < 1e-9 || Math.Abs(t[poly.Count - 1] - hi) < 1e-9;
                // An extreme that only a turnaround reaches has no cap there, but it does have the join: doubling back
                // on the line is a 180-degree turn, whose miter is infinite (so miter and bevel both end flat) and whose
                // round join is the half disc - the same point a Round cap of the same radius would draw.
                caps[0] = loCapped ? cap : TurnCap(join);
                caps[1] = hiCapped ? cap : TurnCap(join);
                poly = new List<Pt2> { baseP + axis * lo, baseP + axis * hi };
            }
            int n = poly.Count;
            var u = new Pt2[n - 1];
            var nm = new Pt2[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                Pt2 d = poly[i + 1] - poly[i];
                double l = d.Length;
                if (l < 1e-12) return new List<Pt2>();
                u[i] = d * (1 / l);
                nm[i] = new Pt2(-u[i].Y, u[i].X);
            }
            var plus = new List<Pt2>();     // the +n side, in the direction of travel
            var minus = new List<Pt2>();    // the -n side, in the direction of travel
            OffsetWalk(poly, u, nm, h, join, miterLimit, 1, plus);
            OffsetWalk(poly, u, nm, h, join, miterLimit, -1, minus);
            var ring = new List<Pt2>(plus);
            CapArc(ring, poly[n - 1], nm[n - 2], u[n - 2], h, caps[1]);      // around the end: +n to -n through the outgoing direction
            for (int i = minus.Count - 1; i >= 0; i--) ring.Add(minus[i]);
            CapArc(ring, poly[0], nm[0] * -1, u[0] * -1, h, caps[0]);        // around the start: -n to +n through the outgoing direction
            return ring;
        }

        /// <summary>One side of the band: the offset of every vertex with the join its corner asks for. s = +1 walks the +n
        /// side (n = rot90-ccw of the direction of travel), s = -1 the other.</summary>
        static void OffsetWalk(IList<Pt2> poly, Pt2[] u, Pt2[] nm, double h, int join, double miterLimit, int s, List<Pt2> walk)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                if (i == 0) { walk.Add(poly[0] + nm[0] * (s * h)); continue; }
                if (i == n - 1) { walk.Add(poly[n - 1] + nm[n - 2] * (s * h)); continue; }
                Pt2 n0 = nm[i - 1] * s, n1 = nm[i] * s;
                double cross = u[i - 1].X * u[i].Y - u[i - 1].Y * u[i].X;
                double cos = u[i - 1].X * u[i].X + u[i - 1].Y * u[i].Y;
                double den = 1 + cos;
                if (den < 1e-9)
                {
                    // A 180-degree turnaround: the band ends here. Which side counts as outer is float noise (cross is
                    // zero to the last ulp), and the inner-side pinch to the centre point leaves a diagonal chord in the
                    // ring that the union then keeps as boundary - the notch on the jagged A's right leg. Close both
                    // walks with the end itself instead: the flat edge (miter, bevel) or the half disc (round join).
                    if (join == 2)
                    {
                        double a0 = Math.Atan2(n0.Y, n0.X);
                        for (int k = 0; k <= ArcHalf; k++)
                        {
                            double t = a0 - s * Math.PI * k / ArcHalf;
                            walk.Add(new Pt2(poly[i].X + h * Math.Cos(t), poly[i].Y + h * Math.Sin(t)));
                        }
                    }
                    else
                    {
                        walk.Add(poly[i] + n0 * h);
                        walk.Add(poly[i] + n1 * h);
                    }
                    continue;
                }
                bool outer = s * cross < 0;      // with n = rot90-ccw(u) the outside of a positive turn is the -n side
                if (!outer)
                {
                    // the inner side: the two offset lines always cross, and that crossing is the boundary
                    walk.Add(poly[i] + (n0 + n1) * (h / den));
                    continue;
                }
                if (join == 2)
                {
                    double a0 = Math.Atan2(n0.Y, n0.X), a1 = Math.Atan2(n1.Y, n1.X);
                    double dd = a1 - a0;
                    while (dd > Math.PI) dd -= 2 * Math.PI;
                    while (dd < -Math.PI) dd += 2 * Math.PI;
                    int steps = Math.Max(1, (int)Math.Round(ArcHalf * Math.Abs(dd) / Math.PI));
                    for (int k = 0; k <= steps; k++)
                    {
                        double t = a0 + dd * k / steps;
                        walk.Add(new Pt2(poly[i].X + h * Math.Cos(t), poly[i].Y + h * Math.Sin(t)));
                    }
                }
                else if (join == 0 && den > 1e-9 && Math.Sqrt(2 / den) <= Math.Max(1.0, miterLimit))
                    walk.Add(poly[i] + (n0 + n1) * (h / den));
                else
                {
                    walk.Add(poly[i] + n0 * h);
                    walk.Add(poly[i] + n1 * h);
                }
            }
        }

        const int ArcHalf = 16;   // chords per half disc, the same sampling the round cap of the piece route uses

        /// <summary>
        /// The half of an open polyline's stroke on one side of it, bounded by the polyline itself: exactly what a stroke
        /// clipped to that side paints, which is the outline conversion's outside / inside position of a dashed contour.
        /// Built without the arrangement on purpose - cutting the full bar against the fill dropped every cap extension,
        /// whose outer edge lies on the fill boundary, and a coincident edge is the arrangement's blind spot.
        /// sgn = +1 keeps the +n side (n = rot90-ccw of the direction of travel), -1 the other.
        /// </summary>
        static List<Pt2> StrokeRingSide(IList<Pt2> pts, double h, VectorCap cap, int join, double miterLimit, int sgn)
        {
            var poly = new List<Pt2>();
            foreach (var q in pts)
                if (poly.Count == 0 || (q - poly[poly.Count - 1]).Length > 1e-9) poly.Add(q);
            if (poly.Count < 2) return new List<Pt2>();
            // Which end of the sweep is an end of the path (its cap) and which is only an extreme a 180-degree turnaround
            // reaches (the corner's shape). A dash slice of a straight run has both of its ends capped.
            bool loTurn = false, hiTurn = false;
            bool coll = Collinear(poly, out Pt2 baseP, out Pt2 axis);
            if (coll)
            {
                // the same sweep as StrokeRing: reduce to the interval the line covers; an extreme only a turnaround
                // reaches has no cap of its own there, only the join's end
                double lo = double.MaxValue, hi = double.MinValue;
                var t = new double[poly.Count];
                for (int i = 0; i < poly.Count; i++)
                {
                    t[i] = (poly[i] - baseP).X * axis.X + (poly[i] - baseP).Y * axis.Y;
                    lo = Math.Min(lo, t[i]); hi = Math.Max(hi, t[i]);
                }
                loTurn = !(Math.Abs(t[0] - lo) < 1e-9 || Math.Abs(t[poly.Count - 1] - lo) < 1e-9);
                hiTurn = !(Math.Abs(t[0] - hi) < 1e-9 || Math.Abs(t[poly.Count - 1] - hi) < 1e-9);
                poly = new List<Pt2> { baseP + axis * lo, baseP + axis * hi };
            }
            int n = poly.Count;
            var u = new Pt2[n - 1];
            var nm = new Pt2[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                Pt2 d = poly[i + 1] - poly[i];
                double l = d.Length;
                if (l < 1e-12) return new List<Pt2>();
                u[i] = d * (1 / l);
                nm[i] = new Pt2(-u[i].Y, u[i].X);
            }
            var walk = new List<Pt2>();
            OffsetWalk(poly, u, nm, h, join, miterLimit, sgn, walk);
            var ring = new List<Pt2>(walk);
            HalfCap(ring, poly[n - 1], nm[n - 2] * sgn, u[n - 2], h, hiTurn ? TurnCap(join) : cap, hiTurn);
            for (int i = n - 1; i >= 0; i--) AddNew(ring, poly[i]);
            var sc = new List<Pt2>();
            HalfCap(sc, poly[0], nm[0] * sgn, u[0] * -1, h, loTurn ? TurnCap(join) : cap, loTurn);
            for (int i = sc.Count - 1; i >= 0; i--) AddNew(ring, sc[i]);   // reversed: the cap reads outward from the centre line
            Unfold(ring);
            return ring;
        }

        /// <summary>
        /// Cuts the self-intersection loops out of a closed ring. Where the contour bends towards the kept side with a radius
        /// below h, the offset doubles back on itself: the walk traces a small loop whose winding is opposite the ring's, so
        /// a nonzero fill cancels it and the bar comes back with a wedge missing. The boundary of the union of the primitives
        /// is the envelope - the offset with the loop spliced off at its crossing - which is what this leaves. The loop can
        /// reach into the start cap (the first offset points fall behind the cap face), so it runs on the closed ring.
        /// </summary>
        static void Unfold(List<Pt2> ring)
        {
            for (int pass = 0; pass < 16; pass++)
            {
                int n = ring.Count;
                bool cut = false;
                for (int i = 0; i < n && !cut; i++)
                    for (int j = i + 2; j < n; j++)
                    {
                        if (i == 0 && j == n - 1) continue;
                        if (!SegSeg(ring[i], ring[(i + 1) % n], ring[j], ring[(j + 1) % n], out Pt2 x)) continue;
                        // the crossing splits the ring in two closed pieces; the fold loop is the small reversed one, the
                        // envelope the big one - and the loop can sit on either side of the split (at the start cap it does)
                        var a = new List<Pt2> { x };
                        for (int k = i + 1; k <= j; k++) a.Add(ring[k]);
                        var b = new List<Pt2> { x };
                        for (int k = j + 1; k <= n + i; k++) b.Add(ring[k % n]);
                        double aa = Area(a), bb = Area(b);
                        ring.Clear();
                        ring.AddRange(Math.Abs(aa) >= Math.Abs(bb) ? a : b);
                        cut = true;
                        break;
                    }
                if (!cut) return;
            }
        }

        static double Area(IList<Pt2> r)
        {
            double s = 0;
            for (int i = 0; i < r.Count; i++) { Pt2 p = r[i], q = r[(i + 1) % r.Count]; s += p.X * q.Y - q.X * p.Y; }
            return s / 2;
        }

        /// <summary>A proper crossing of the open segments (a,b) and (c,d); touching and parallel count as no crossing.</summary>
        static bool SegSeg(Pt2 a, Pt2 b, Pt2 c, Pt2 d, out Pt2 p)
        {
            p = a;
            Pt2 r = b - a, s = d - c;
            double den = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(den) < 1e-12) return false;
            double t = ((c.X - a.X) * s.Y - (c.Y - a.Y) * s.X) / den;
            double u = ((c.X - a.X) * r.Y - (c.Y - a.Y) * r.X) / den;
            if (t <= 1e-9 || t >= 1 - 1e-9 || u <= 1e-9 || u >= 1 - 1e-9) return false;
            p = a + r * t;
            return true;
        }

        static void AddNew(List<Pt2> ring, Pt2 p)
        {
            if (ring.Count == 0 || (p - ring[ring.Count - 1]).Length > 1e-9) ring.Add(p);
        }

        /// <summary>The half of the end at <paramref name="q"/> on the n side, from the offset edge back to the centre line, of a
        /// band of depth h. <paramref name="turn"/> says the end is not an end of the path but an extreme a 180-degree turnaround
        /// reaches, where what closes the band is the corner's own shape: the round corner's half disc of the full depth at the
        /// contour, or a flat end. Otherwise it is a cap, and a cap belongs to the band and not to a pen of twice its depth: the
        /// square's corner and centre pushed out by h/2, the round half disc of radius h/2 on the band's centre line (the same cap
        /// a centred band of depth h has, moved out to where the band lies), or just the centre point for a butt cap, whose
        /// closing edge is the straight line to it.</summary>
        static void HalfCap(List<Pt2> ring, Pt2 q, Pt2 n, Pt2 uOut, double h, VectorCap cap, bool turn)
        {
            if (turn)
            {
                if (cap == VectorCap.Round)
                {
                    double ta0 = Math.Atan2(n.Y, n.X);
                    double tdd = NormAngle(Math.Atan2(uOut.Y, uOut.X) - ta0);
                    for (int k = 1; k <= ArcHalf; k++)
                    {
                        double t = ta0 + tdd * k / ArcHalf;
                        ring.Add(new Pt2(q.X + h * Math.Cos(t), q.Y + h * Math.Sin(t)));
                    }
                }
                ring.Add(q);
                return;
            }
            if (cap == VectorCap.Square)
            {
                ring.Add(q + n * h + uOut * (h * 0.5));
                ring.Add(q + uOut * (h * 0.5));
                ring.Add(q);
                return;
            }
            if (cap == VectorCap.Round)
            {
                double a0 = Math.Atan2(n.Y, n.X);
                double dd = 2 * NormAngle(Math.Atan2(uOut.Y, uOut.X) - a0);   // n and uOut are a quarter turn apart, so this is the half turn through uOut
                Pt2 c = q + n * (h * 0.5);
                for (int k = 1; k < ArcHalf; k++)   // k = ArcHalf lands on q, which the line below adds exactly
                {
                    double t = a0 + dd * k / ArcHalf;
                    ring.Add(new Pt2(c.X + h * 0.5 * Math.Cos(t), c.Y + h * 0.5 * Math.Sin(t)));
                }
            }
            ring.Add(q);
        }

        static double NormAngle(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        /// <summary>What a 180-degree turnaround in the middle of a collinear path draws at that extreme: the round join's
        /// half disc, or a flat end. A square never extends a join - no stroker does - so only the round case adds geometry.</summary>
        static VectorCap TurnCap(int join) => join == 2 ? VectorCap.Round : VectorCap.Butt;

        /// <summary>The points of the cap at <paramref name="q"/>, between the two sides of the band and around the outgoing
        /// direction. Butt adds nothing (the straight closing edge is the cap), square adds the two corners pushed out by h,
        /// round adds the half disc between them.</summary>
        static void CapArc(List<Pt2> ring, Pt2 q, Pt2 nOut, Pt2 uOut, double h, VectorCap cap)
        {
            if (cap == VectorCap.Square)
            {
                ring.Add(q + nOut * h + uOut * h);
                ring.Add(q - nOut * h + uOut * h);
                return;
            }
            if (cap != VectorCap.Round) return;
            double a0 = Math.Atan2(nOut.Y, nOut.X);
            for (int k = 1; k < ArcHalf; k++)
            {
                double t = a0 - Math.PI * k / ArcHalf;      // +n to -n through +u: n = rot90-ccw(u) puts u a quarter turn clockwise from n
                ring.Add(new Pt2(q.X + h * Math.Cos(t), q.Y + h * Math.Sin(t)));
            }
        }

        /// <summary>True when every point of the polyline lies on one line; the axis is then the direction of the first segment.</summary>
        static bool Collinear(IList<Pt2> poly, out Pt2 baseP, out Pt2 axis)
        {
            baseP = poly[0];
            axis = new Pt2(1, 0);
            Pt2 d = poly[1] - poly[0];
            double l = d.Length;
            if (l < 1e-12) return false;
            axis = d * (1 / l);
            double tol = 1e-9 * Math.Max(1.0, l * poly.Count);
            for (int i = 1; i < poly.Count; i++)
            {
                Pt2 v = poly[i] - baseP;
                if (Math.Abs(v.X * axis.Y - v.Y * axis.X) > tol) return false;
            }
            return true;
        }

        /// <summary>
        /// Bands of half-width h around each segment of the open (or zero-area) subpaths, plus the join piece at every
        /// internal vertex, counter-clockwise. The line ends are shaped by cap: butt (no extension), square (extended by h),
        /// round (half disc of radius h). Ends are not clipped to the shape: with square or round caps they may leave it.
        /// The corner piece is the same geometry the stroker draws: the miter quad within the limit, the bevel triangle
        /// beyond it or when asked, the arc fan for a round join. Without these pieces the outline of a polyline that turns
        /// was the union of loose segment quads, i.e. every corner was cut off, which is why the traced route was used.
        /// </summary>
        static List<List<Pt2>> BarPieces(List<List<Pt2>> lines, double h, VectorCap cap, int join = 0, double miterLimit = 4.0)
        {
            const int ArcSteps = 16;   // steps per half disc of a round cap
            var pieces = new List<List<Pt2>>();
            if (h <= 0) return pieces;
            // closed counter-clockwise, then kept: the arrangement unions by winding, so every piece has to face out
            void Add(List<Pt2> ring)
            {
                double area = 0;
                for (int k = 0; k < ring.Count; k++) { var p = ring[k]; var q = ring[(k + 1) % ring.Count]; area += p.X * q.Y - q.X * p.Y; }
                if (Math.Abs(area) < 1e-12) return;
                if (area < 0) ring.Reverse();
                pieces.Add(ring);
            }
            foreach (var ln in lines)
            {
                int segs = ln.Count - 1;   // an open polyline: its segments between consecutive points
                for (int i = 0; i < segs; i++)
                {
                    var a = ln[i]; var b = ln[i + 1];
                    double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-9) continue;
                    double ux = dx / len, uy = dy / len;   // along the segment
                    double nx = -uy, ny = ux;              // across the segment
                    bool first = i == 0, last = i == segs - 1;   // caps only at the two ends of the whole line
                    double ea = first && cap == VectorCap.Square ? h : 0, eb = last && cap == VectorCap.Square ? h : 0;
                    bool roundA = first && cap == VectorCap.Round, roundB = last && cap == VectorCap.Round;

                    // point on the half disc of radius h about c, at angle phi, measured from the segment direction (u) toward the normal (n)
                    Pt2 Arc(Pt2 c, double phi) => new Pt2(c.X + h * (Math.Cos(phi) * ux + Math.Sin(phi) * nx), c.Y + h * (Math.Cos(phi) * uy + Math.Sin(phi) * ny));

                    var ring = new List<Pt2> { new Pt2(a.X - ux * ea + nx * h, a.Y - uy * ea + ny * h) };   // +n side at a
                    if (roundB) for (int k = 1; k <= ArcSteps; k++) ring.Add(Arc(b, Math.PI / 2 - Math.PI * k / ArcSteps));   // around b: +n -> -n through +u
                    else
                    {
                        ring.Add(new Pt2(b.X + ux * eb + nx * h, b.Y + uy * eb + ny * h));
                        ring.Add(new Pt2(b.X + ux * eb - nx * h, b.Y + uy * eb - ny * h));
                    }
                    if (roundA)
                    {
                        ring.Add(Arc(a, -Math.PI / 2));   // a - n
                        for (int k = 1; k < ArcSteps; k++) ring.Add(Arc(a, -Math.PI / 2 - Math.PI * k / ArcSteps));   // around a: -n -> +n through -u
                    }
                    else ring.Add(new Pt2(a.X - ux * ea - nx * h, a.Y - uy * ea - ny * h));
                    Add(ring);
                }
                // JoinPiece is null where the two segment bands already cover the corner (a straight run, a reversal with a
                // non-round join), and Add dereferences its argument, so the null is skipped rather than passed on
                for (int i = 1; i < segs; i++)
                {
                    var corner = JoinPiece(ln[i - 1], ln[i], ln[i + 1], h, join, miterLimit, ArcSteps);
                    if (corner != null) Add(corner);
                }
            }
            return pieces;
        }

        /// <summary>The piece that fills the gap a turn leaves on the outside of a polyline of half-width h: the miter quad
        /// within the limit, the bevel triangle beyond it (or when asked), the arc fan for a round join. Null when nothing
        /// is missing (a straight run, or a reversal, where the two segment bands already cover the corner).</summary>
        static List<Pt2>? JoinPiece(Pt2 prev, Pt2 cur, Pt2 next, double h, int join, double miterLimit, int arcSteps)
        {
            Pt2 d0 = cur - prev, d1 = next - cur;
            double l0 = d0.Length, l1 = d1.Length;
            if (l0 < 1e-12 || l1 < 1e-12) return null;
            Pt2 u0 = d0 * (1 / l0), u1 = d1 * (1 / l1);
            Pt2 n0 = new Pt2(-u0.Y, u0.X), n1 = new Pt2(-u1.Y, u1.X);
            double cross = u0.X * u1.Y - u0.Y * u1.X;
            double cos = u0.X * u1.X + u0.Y * u1.Y;
            double den = 1 + cos;
            if (cross * cross < 1e-12)                                   // straight, or doubled back on itself
                return join == 2 ? Disc(cur, h, arcSteps * 2) : null;    // a round join still draws the full disc at a reversal
            // the outside is the side the two offset edges diverge toward: with n = rot90-ccw(u) it is -n when the turn is
            // positive (same rule the stroker uses, derived from (rot90(d0)+rot90(d1)) . (d0-d1) = -2 * cross)
            double s = cross > 0 ? -1 : 1;
            var ring = new List<Pt2> { cur };
            if (join == 2)
            {
                double a0 = Math.Atan2(s * n0.Y, s * n0.X), a1 = Math.Atan2(s * n1.Y, s * n1.X);
                double dd = a1 - a0;
                while (dd > Math.PI) dd -= 2 * Math.PI;
                while (dd < -Math.PI) dd += 2 * Math.PI;
                int steps = Math.Max(2, (int)Math.Round(arcSteps * Math.Abs(dd) / Math.PI));
                for (int k = 0; k <= steps; k++)
                {
                    double t = a0 + dd * k / steps;
                    ring.Add(new Pt2(cur.X + h * Math.Cos(t), cur.Y + h * Math.Sin(t)));
                }
            }
            else
            {
                // sqrt(2 / den) is the miter length over the band width, identical to the stroker's 1 / sin(interior / 2)
                bool mitre = join == 0 && Math.Sqrt(2 / den) <= Math.Max(1.0, miterLimit);
                // inset by 0.1 % of h: a spike sitting exactly on the two segment edges is a collinear overlap, and the
                // arrangement drops such pieces (the same reason MiterSpikes scales its quad in)
                double hs = mitre ? h * (1 - 1e-3) : h;
                ring.Add(new Pt2(cur.X + s * n0.X * hs, cur.Y + s * n0.Y * hs));
                if (mitre) ring.Add(new Pt2(cur.X + s * (n0.X + n1.X) * (hs / den), cur.Y + s * (n0.Y + n1.Y) * (hs / den)));
                ring.Add(new Pt2(cur.X + s * n1.X * hs, cur.Y + s * n1.Y * hs));
            }
            return ring;
        }

        /// <summary>The regular polygon of radius h about c (a full disc at a reversed round join).</summary>
        static List<Pt2> Disc(Pt2 c, double h, int steps)
        {
            var ring = new List<Pt2>(steps + 1);
            for (int k = 0; k < steps; k++)
            {
                double t = 2 * Math.PI * k / steps;
                ring.Add(new Pt2(c.X + h * Math.Cos(t), c.Y + h * Math.Sin(t)));
            }
            return ring;
        }

        /// <summary>
        /// The subpaths of a path as dense polylines (mm; cubics sampled at about 0.01 mm): the closed ones with area in
        /// rings, the open and the zero-area ones in lines. Without keepOpen an open subpath that encloses area fails the
        /// flatten, because a region offset cannot be built from it; with keepOpen it is returned as a line, which is the
        /// centre line of the band the stroker draws.
        /// </summary>
        static bool TryFlatten(VectorPath p, out List<List<Pt2>> rings, out List<List<Pt2>> lines, double s = 1, bool keepOpen = false)
        {
            double inv = 1.0 / s;
            rings = new List<List<Pt2>>();
            lines = new List<List<Pt2>>();
            var cur = new List<Pt2>();
            bool open = false;
            int di = 0;
            double px = 0, py = 0, sx = 0, sy = 0;
            foreach (var k in p.Kinds)
            {
                switch (k)
                {
                    case VectorPath.K.Move:
                        if (open && !TakeOpenLine(cur, lines, keepOpen)) return false;
                        cur = new List<Pt2>();
                        px = sx = p.Data[di] * inv; py = sy = p.Data[di + 1] * inv; di += 2;
                        cur.Add(new Pt2(px, py));
                        open = true;
                        break;
                    case VectorPath.K.Line:
                        px = p.Data[di] * inv; py = p.Data[di + 1] * inv; di += 2;
                        cur.Add(new Pt2(px, py));
                        break;
                    case VectorPath.K.Cubic:
                        {
                            double c1x = p.Data[di] * inv, c1y = p.Data[di + 1] * inv, c2x = p.Data[di + 2] * inv, c2y = p.Data[di + 3] * inv;
                            double ex = p.Data[di + 4] * inv, ey = p.Data[di + 5] * inv;
                            di += 6;
                            double ctrl = Math.Sqrt((c1x - px) * (c1x - px) + (c1y - py) * (c1y - py))
                                        + Math.Sqrt((c2x - c1x) * (c2x - c1x) + (c2y - c1y) * (c2y - c1y))
                                        + Math.Sqrt((ex - c2x) * (ex - c2x) + (ey - c2y) * (ey - c2y));
                            int n = Math.Max(8, (int)(ctrl / 0.01));
                            for (int i = 1; i <= n; i++)
                            {
                                double t = i / (double)n, m = 1 - t;
                                cur.Add(new Pt2(m * m * m * px + 3 * m * m * t * c1x + 3 * m * t * t * c2x + t * t * t * ex,
                                                m * m * m * py + 3 * m * m * t * c1y + 3 * m * t * t * c2y + t * t * t * ey));
                            }
                            px = ex; py = ey;
                            break;
                        }
                    case VectorPath.K.Close:
                        if (open && cur.Count >= 2)
                        {
                            double ar = 0;
                            for (int m = 0; m < cur.Count; m++) { var q = cur[(m + 1) % cur.Count]; ar += cur[m].X * q.Y - q.X * cur[m].Y; }
                            if (Math.Abs(ar) < 1e-9) lines.Add(cur);       // zero area: a line, not a region
                            else if (cur.Count >= 3) rings.Add(cur);
                        }
                        open = false;
                        px = sx; py = sy;
                        break;
                }
            }
            if (open && !TakeOpenLine(cur, lines, keepOpen)) return false;
            return true;
        }

        /// <summary>An open subpath that encloses no area (a drawn line) is taken as a line; any other open subpath is not,
        /// unless keepOpen: then it is the centre line of the band asked for.</summary>
        static bool TakeOpenLine(List<Pt2> cur, List<List<Pt2>> lines, bool keepOpen = false)
        {
            if (cur.Count < 2) return false;
            if (keepOpen) { lines.Add(cur); return true; }
            double ar = 0;
            for (int m = 0; m < cur.Count; m++) { var q = cur[(m + 1) % cur.Count]; ar += cur[m].X * q.Y - q.X * cur[m].Y; }
            if (Math.Abs(ar) >= 1e-9) return false;
            lines.Add(cur);
            return true;
        }

        /// <summary>Fits each ring (cubics where the ring curves, line segments where it is straight; see Vector.Fit.cs) and writes closed subpaths.</summary>
        static VectorPath FromRings(List<List<Pt2>> rings, double tol, double s = 1)
        {
            var result = new VectorPath();
            foreach (var ring in rings)
            {
                var segs = OutlineFit.FitLoop(ring, 0.05, tol);
                if (segs.Count == 0) continue;
                result.MoveTo((float)(segs[0].P0.X * s), (float)(segs[0].P0.Y * s));
                foreach (var g in segs)
                    if (g.Cub) result.CurveTo((float)(g.C1.X * s), (float)(g.C1.Y * s), (float)(g.C2.X * s), (float)(g.C2.Y * s), (float)(g.P1.X * s), (float)(g.P1.Y * s));
                    else result.LineTo((float)(g.P1.X * s), (float)(g.P1.Y * s));
                result.Close();
            }
            return result;
        }

        static void Emit(VectorPath p, List<Seg> loop)
        {
            if (loop.Count == 0) return;
            p.MoveTo((float)loop[0].P0.X, (float)loop[0].P0.Y);
            foreach (var s in loop)
            {
                if (s.Cub) p.CurveTo((float)s.C1.X, (float)s.C1.Y, (float)s.C2.X, (float)s.C2.Y, (float)s.P1.X, (float)s.P1.Y);
                else p.LineTo((float)s.P1.X, (float)s.P1.Y);
            }
            p.Close();
        }

        static V Midpoint(Seg s) => Bez(s, 0.5);
        static V Normal(Seg s) { var T = Tan(s, 0.5); return new V(-T.Y, T.X); }

        /// <summary>Even-odd: the point is inside an odd number of the loops (ray to +x against 64-step polylines).</summary>
        static bool InsideEvenOdd(List<List<Seg>> loops, V q)
        {
            int crossings = 0;
            foreach (var L in loops)
                foreach (var s in L)
                {
                    V prev = s.P0;
                    for (int i = 1; i <= 64; i++)
                    {
                        V cur = Bez(s, i / 64.0);
                        if ((prev.Y > q.Y) != (cur.Y > q.Y))
                        {
                            double x = prev.X + (q.Y - prev.Y) * (cur.X - prev.X) / (cur.Y - prev.Y);
                            if (x > q.X) crossings++;
                        }
                        prev = cur;
                    }
                }
            return (crossings & 1) == 1;
        }
            // ------------------------------------------------------------------ approximate band (distance field, no dependency)

        /// <summary>Stroke outline as an object: every point within r of the closed contours (both sides, round joins, holes where the stroke encloses a pocket). One path, even-odd fill.</summary>
        /// <summary>Gives each loop of a region the direction its nesting asks for (outer loops and islands one way, holes the other), so nonzero and even-odd fill agree.</summary>
        static VectorPath Orient(VectorPath region)
        {
            var loops = new List<(V Start, List<(bool Cubic, V C1, V C2, V End)> Segs)>();
            var d = region.Data; int di = 0;
            V start = default, cur = default;
            List<(bool Cubic, V C1, V C2, V End)>? segs = null;
            void Finish()
            {
                if (segs == null) return;
                if (segs.Count > 0 && Math.Abs(cur.X - start.X) + Math.Abs(cur.Y - start.Y) > 1e-6) segs.Add((false, start, start, start));
                if (segs.Count > 0) loops.Add((start, segs));
                segs = null;
            }
            foreach (var k in region.Kinds)
            {
                switch (k)
                {
                    case VectorPath.K.Move: Finish(); start = cur = new V(d[di], d[di + 1]); segs = new(); di += 2; break;
                    case VectorPath.K.Line: cur = new V(d[di], d[di + 1]); segs?.Add((false, cur, cur, cur)); di += 2; break;
                    case VectorPath.K.Cubic:
                        {
                            var c1 = new V(d[di], d[di + 1]); var c2 = new V(d[di + 2], d[di + 3]); cur = new V(d[di + 4], d[di + 5]);
                            segs?.Add((true, c1, c2, cur)); di += 6; break;
                        }
                    case VectorPath.K.Close: Finish(); cur = start; break;
                }
            }
            Finish();
            var polys = new List<List<V>>();
            foreach (var (s, sg) in loops)
            {
                var P = new List<V> { s }; V prev = s;
                foreach (var g in sg)
                {
                    if (g.Cubic) for (int q = 1; q <= 8; q++) P.Add(Bz(prev, g.C1, g.C2, g.End, q / 8.0));
                    else P.Add(g.End);
                    prev = g.End;
                }
                polys.Add(P);
            }
            var result = new VectorPath();
            for (int i = 0; i < loops.Count; i++)
            {
                var s = loops[i].Start; var segs2 = loops[i].Segs;
                V q = polys[i][Math.Min(1, polys[i].Count - 1)];
                int depth = 0;
                for (int j = 0; j < loops.Count; j++) if (j != i && Inside(q, polys[j])) depth++;
                bool wantPositive = depth % 2 == 0, isPositive = PolyArea(polys[i]) > 0;
                if (isPositive != wantPositive) segs2 = Reverse(s, segs2);
                result.MoveTo((float)s.X, (float)s.Y);
                for (int n = 0; n < segs2.Count; n++)
                {
                    var g = segs2[n];
                    if (g.Cubic) result.CurveTo((float)g.C1.X, (float)g.C1.Y, (float)g.C2.X, (float)g.C2.Y, (float)g.End.X, (float)g.End.Y);
                    else if (n < segs2.Count - 1 || Math.Abs(g.End.X - s.X) + Math.Abs(g.End.Y - s.Y) > 1e-6) result.LineTo((float)g.End.X, (float)g.End.Y);
                }
                result.Close();
            }
            return result;
        }

        static V Bz(V p0, V p1, V p2, V p3, double t)
        {
            double u = 1 - t;
            return p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
        }

        static double PolyArea(List<V> P)
        {
            double a = 0;
            for (int i = 0; i < P.Count; i++) { var p = P[i]; var q = P[(i + 1) % P.Count]; a += p.X * q.Y - q.X * p.Y; }
            return a / 2;
        }

        static bool Inside(V q, List<V> P)
        {
            bool inside = false;
            for (int i = 0, j = P.Count - 1; i < P.Count; j = i++)
            {
                var a = P[i]; var b = P[j];
                if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        static List<(bool Cubic, V C1, V C2, V End)> Reverse(V start, List<(bool Cubic, V C1, V C2, V End)> segs)
        {
            var r = new List<(bool Cubic, V C1, V C2, V End)>();
            for (int i = segs.Count - 1; i >= 0; i--)
            {
                V to = i == 0 ? start : segs[i - 1].End;
                r.Add((segs[i].Cubic, segs[i].C2, segs[i].C1, to));
            }
            return r;
        }

        public static VectorPath Stroke(VectorPath contour, double r, double pixel, double tol) => Orient(Band(contour, r, pixel, tol, 0));

        /// <summary>Outline as an object. ring 0 = centre (every point within r of the path, open or closed); 1 = outside ring (r wide, outside a closed shape); 2 = inside ring (r wide, inside a closed shape).
        /// miterLimit is the stroke's own: a corner within it gets the spike, beyond it the bevel, so the baked shape reproduces the drawn stroke at any limit.</summary>
        public static VectorPath Outline(VectorPath contour, double r, int ring, double pixel, double tol, VectorCap cap = VectorCap.Square, int join = 0, double miterLimit = 4.0) => Orient(Band(contour, r, pixel, tol, ring, cap, join, miterLimit));

        /// <summary>Dashed outline as one compound path (Corel): the closed path is cut into dashes of length on and gaps of
        /// length off, and each dash is outlined with width w. One subpath per dash. Corel scales the pattern with the width
        /// (on = 5 w, off = 3 w). cap and join are the ends and corners of each dash, as the stroker draws them; the default
        /// is round, which is what a dashed pen paints. The compound is nonzero-correct (adjacent dashes overlap where the
        /// band is thicker than the gap) and is oriented by VectorOffset.Orient, so fill it with the nonzero rule.</summary>
        public static VectorPath Dashed(VectorPath closed, double on, double off, double w, double pixel, double tol,
            VectorCap cap = VectorCap.Round, VectorJoin join = VectorJoin.Round, double miterLimit = 4.0)
        {
            int jc = join == VectorJoin.Bevel ? 1 : join == VectorJoin.Round ? 2 : 0;   // the arrangement's 0 miter, 1 bevel, 2 round
            var result = new VectorPath();
            foreach (var L in ReadClosed(closed))
            {
                var pts = new List<V>();
                foreach (var sg in L)
                    for (int k = 0; k < 48; k++) pts.Add(Bez(sg, k / 48.0));
                if (pts.Count < 2) continue;
                pts.Add(pts[0]);
                bool dash = true;
                double left = on;
                var cur = new List<V> { pts[0] };
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    V a = pts[i], b = pts[i + 1];
                    double len = (b - a).Len, pos = 0;
                    while (len - pos > left)
                    {
                        pos += left;
                        V p = a + (b - a) * (pos / len);
                        if (dash) { cur.Add(p); AppendDash(result, cur, w, pixel, tol, cap, jc, miterLimit); cur = new List<V>(); }
                        else cur = new List<V> { p };
                        dash = !dash;
                        left = dash ? on : off;
                    }
                    left -= len - pos;
                    if (dash) cur.Add(b);
                }
                if (dash) AppendDash(result, cur, w, pixel, tol, cap, jc, miterLimit);
            }
            return result;
        }

        static void AppendDash(VectorPath result, List<V> pts, double w, double pixel, double tol, VectorCap cap, int join, double miterLimit)
        {
            double len = 0;
            for (int i = 0; i + 1 < pts.Count; i++) len += (pts[i + 1] - pts[i]).Len;
            if (len < pixel) return;
            var line = new VectorPath();
            line.MoveTo((float)pts[0].X, (float)pts[0].Y);
            for (int i = 1; i < pts.Count; i++) line.LineTo((float)pts[i].X, (float)pts[i].Y);
            var o = Outline(line, w / 2, 0, pixel, tol, cap, join, miterLimit);
            result.Kinds.AddRange(o.Kinds); result.Data.AddRange(o.Data);
        }

        /// <summary>Dashed outline cut where the renderer cuts it: along the contour itself, not along an offset midline.
        /// Each dash is the slice of the contour between two pattern points, banded to the stroke the dash paints: the whole
        /// centred band for a centre outline, and for the outside / inside positions the half of that band on its side, which
        /// is what the renderer's clip to the exterior / the fill leaves of the stroke of twice the width it draws there.
        /// A dash pattern counted along an offset midline drifts against the stroke on every curved contour (the midline is
        /// longer or shorter than the contour), which is what made the wavy blob's converted dashes sit beside the drawn ones.
        /// <paramref name="pattern"/> alternates on / off in the path's own units, <paramref name="offset"/> is the phase at the
        /// path start and <paramref name="seam"/> is the same rule the stroker uses where a closed lap meets that phase, so a
        /// converted dashed outline and the stroke it came from agree at the seam in every mode.</summary>
        public static VectorPath DashedBand(VectorPath closed, double[] pattern, double offset, VectorDashSeam seam, double w, double pixel, double tol,
            VectorOutlinePosition side, VectorCap cap, VectorJoin join, double miterLimit = 4.0)
        {
            int jc = join == VectorJoin.Bevel ? 1 : join == VectorJoin.Round ? 2 : 0;   // the arrangement's 0 miter, 1 bevel, 2 round
            var loops = new List<(List<V> Pts, int Sgn)>();
            foreach (var L in ReadClosed(closed))
            {
                var pts = new List<V>();
                foreach (var sg in L)
                    for (int k = 0; k < 48; k++) pts.Add(Bez(sg, k / 48.0));
                if (pts.Count < 2) continue;
                pts.Add(pts[0]);
                double a = 0;
                for (int i = 0; i + 1 < pts.Count; i++) a += pts[i].X * pts[i + 1].Y - pts[i + 1].X * pts[i].Y;
                loops.Add((pts, a >= 0 ? 1 : -1));   // a nonzero fill lies left of a ccw ring and right of a cw one
            }
            var ringsOut = new List<List<Pt2>>();
            static double Area(List<List<Pt2>> rs)
            {
                double a = 0;
                foreach (var r in rs)
                    for (int k = 0; k < r.Count; k++) { var p = r[k]; var q = r[(k + 1) % r.Count]; a += (p.X * q.Y - q.X * p.Y) * 0.5; }
                return a;
            }
            foreach (var (pts, sgnLoop) in loops)
            {
                var cum = new double[pts.Count];
                for (int i = 0; i + 1 < pts.Count; i++) cum[i + 1] = cum[i] + (pts[i + 1] - pts[i]).Len;
                double per = cum[pts.Count - 1];
                if (per <= 0) continue;
                var spans = DashSpans(pattern, offset, seam, per, pixel);
                foreach (var (a, b) in spans)
                {
                    var slice = new List<Pt2>();
                    void At(double d)
                    {
                        double e = d >= per ? d - per : d;                       // a wrapped dash runs past the seam
                        int i = Array.BinarySearch(cum, e);
                        if (i < 0) i = ~i - 1;
                        i = Math.Clamp(i, 0, pts.Count - 2);
                        double f = (e - cum[i]) / Math.Max(1e-12, cum[i + 1] - cum[i]);
                        slice.Add(new Pt2(pts[i].X + (pts[i + 1].X - pts[i].X) * f, pts[i].Y + (pts[i + 1].Y - pts[i].Y) * f));
                    }
                    At(a);
                    for (int i = 0; i + 1 < pts.Count; i++)
                        if (cum[i] > a && cum[i] < b) slice.Add(new Pt2(pts[i].X, pts[i].Y));
                    if (b > per)
                    {
                        slice.Add(new Pt2(pts[0].X, pts[0].Y));
                        for (int i = 1; i + 1 < pts.Count; i++)
                            if (cum[i] < b - per) slice.Add(new Pt2(pts[i].X, pts[i].Y));
                    }
                    At(b);
                    List<List<Pt2>> pieces;
                    if (side == VectorOutlinePosition.Center)
                        pieces = StrokePieces(new List<List<Pt2>> { slice }, w / 2, cap, jc, miterLimit);
                    else
                    {
                        var half = StrokeRingSide(slice, w, cap, jc, miterLimit, sgnLoop * (side == VectorOutlinePosition.Inside ? 1 : -1));
                        pieces = half.Count >= 3 ? new List<List<Pt2>> { half } : new List<List<Pt2>>();
                        OutlineArrange.Probe($"dash span {a:0.##}..{b:0.##} slice {slice.Count} pts half {pieces.Count} rings area {Area(pieces):0.##}");
                        if (OutlineArrange.BandProbe != null)
                        {
                            OutlineArrange.Probe($"ringdump slice {DumpPts(slice)}");
                            foreach (var h2 in pieces) OutlineArrange.Probe($"ringdump bar {DumpPts(h2)}");
                        }
                    }
                    ringsOut.AddRange(pieces);
                }
            }
            // no Orient: adjacent dashes of one contour keep the same winding, so where their cap extensions meet on a tight
            // concave run the nonzero fill unions them, exactly as the stroker paints the overlap. Orient orients by nesting,
            // and a cap overlap is not nesting - it read one dash as a hole of its neighbour and cancelled the shared wedge.
            var result = FromRings(ringsOut, tol, 1);
            // open pieces: an open contour has no inside or outside, so the renderer dashes it centred at the outline width
            // whatever position was asked for, and AppendDash is exactly that - one centred band per dash slice. Without this a
            // dashed conversion of a shape with open pieces would silently drop them.
            foreach (var P in ReadOpen(closed))
            {
                if (P.Count < 2) continue;
                var cum = new double[P.Count];
                for (int i = 0; i + 1 < P.Count; i++) cum[i + 1] = cum[i] + (P[i + 1] - P[i]).Len;
                double len = cum[P.Count - 1];
                if (len <= 0) continue;
                foreach (var (a, b) in DashSpans(pattern, offset, VectorDashSeam.Cut, len, pixel))
                {
                    var slice = new List<V>();
                    void At(double d)
                    {
                        int i = Array.BinarySearch(cum, d);
                        if (i < 0) i = ~i - 1;
                        i = Math.Clamp(i, 0, P.Count - 2);
                        double f = (d - cum[i]) / Math.Max(1e-12, cum[i + 1] - cum[i]);
                        slice.Add(new V(P[i].X + (P[i + 1].X - P[i].X) * f, P[i].Y + (P[i + 1].Y - P[i].Y) * f));
                    }
                    At(a);
                    for (int i = 0; i < P.Count; i++) if (cum[i] > a && cum[i] < b) slice.Add(P[i]);
                    At(b);
                    if (slice.Count >= 2) AppendDash(result, slice, w, pixel, tol, cap, jc, miterLimit);
                }
            }
            return result;
        }

        /// <summary>The painted arc-length intervals of one closed lap of length <paramref name="per"/>, laid out exactly as the
        /// stroker lays them out: alternating on / off from <paramref name="offset"/> into the pattern, an odd-length pattern read
        /// as if written twice, spans shorter than a pixel dropped, and <paramref name="seam"/> deciding what the lap's start does
        /// to the dash that straddles it. A wrapped span is returned as (a, b) with b above <paramref name="per"/>, so the caller
        /// samples it past the seam instead of capping two halves there.</summary>
        static List<(double A, double B)> DashSpans(double[] pattern, double offset, VectorDashSeam seam, double per, double pixel)
        {
            var spans = new List<(double A, double B)>();
            double period = 0; bool bad = false;
            foreach (var v in pattern) { if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) bad = true; period += Math.Max(0, v); }
            if (bad || !(period > 0)) return spans;
            if ((pattern.Length & 1) != 0) period *= 2;
            double ph = offset % period; if (ph < 0) ph += period;
            int di = 0; double rem = Math.Max(0, pattern[0]); bool on = true;
            while (ph > 0) { if (ph >= rem) { ph -= rem; di = (di + 1) % pattern.Length; rem = Math.Max(0, pattern[di]); on = !on; } else { rem -= ph; ph = 0; } }
            double s = 0, from = 0; bool open = on;
            while (s < per)
            {
                double step = Math.Min(rem, per - s);
                s += step; rem -= step;
                if (s >= per) break;                                   // the lap ends inside this element
                if (open) spans.Add((from, s)); else from = s;         // a pattern boundary at s
                open = !open; di = (di + 1) % pattern.Length; rem = Math.Max(0, pattern[di]);
            }
            if (open) spans.Add((from, per));
            if (seam == VectorDashSeam.Wrap && spans.Count > 1 && spans[0].A == 0 && spans[^1].B == per)
            {   // the lap both starts and ends inside a dash: on the loop those two are one dash, so join them across the seam
                spans[^1] = (spans[^1].A, per + spans[0].B);
                spans.RemoveAt(0);
            }
            var keep = new List<(double A, double B)>();
            foreach (var (a, b) in spans) if (b - a >= pixel) keep.Add((a, b));
            return keep;
        }

        /// <summary>Invariant-culture vertex list for the ringdump probe lines, so a dump parses the same in every locale.</summary>
        static string DumpPts(IList<Pt2> r)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            foreach (var p in r) sb.Append(' ').Append(p.X.ToString("0.####", ci)).Append(' ').Append(p.Y.ToString("0.####", ci));
            return sb.ToString();
        }

        /// <summary>Open subpaths (no Close), sampled: cubics at 48 steps. Closed subpaths are read by ReadClosed.</summary>
        static List<List<V>> ReadOpen(VectorPath p)
        {
            var res = new List<List<V>>();
            List<V>? cur = null; V pos = default; int di = 0;
            foreach (var k in p.Kinds)
            {
                switch (k)
                {
                    case VectorPath.K.Move:
                        if (cur != null && cur.Count > 1) res.Add(cur);
                        pos = new V(p.Data[di], p.Data[di + 1]); cur = new List<V> { pos }; di += 2; break;
                    case VectorPath.K.Line:
                        pos = new V(p.Data[di], p.Data[di + 1]); cur?.Add(pos); di += 2; break;
                    case VectorPath.K.Cubic:
                        {
                            var c1 = new V(p.Data[di], p.Data[di + 1]); var c2 = new V(p.Data[di + 2], p.Data[di + 3]); var e = new V(p.Data[di + 4], p.Data[di + 5]);
                            if (cur != null) for (int q = 1; q <= 48; q++) cur.Add(Bz(pos, c1, c2, e, q / 48.0));
                            pos = e; di += 6; break;
                        }
                    case VectorPath.K.Close: cur = null; break;
                }
            }
            if (cur != null && cur.Count > 1) res.Add(cur);
            return res;
        }

        /// <summary>
        /// Approximate band outline at signed distance d (contour units). d &gt; 0: the filled region grown by d (outside outline).
        /// d &lt; 0: the filled region shrunk by -d (inside outline). The distance field is traced with marching squares on a grid
        /// of the given pixel size, then refitted with cubic Beziers within tol. Real corners become nodes; smooth runs get the
        /// fewest cubics that fit within tol. Closed subpaths; fill with evenodd.
        /// </summary>
        public static VectorPath Band(VectorPath contour, double d, double pixel, double tol, int ring = -1, VectorCap cap = VectorCap.Square, int join = 0, double miterLimit = 4.0)
        {
            if (ring < 0) { var fastFill = FastFilled(contour, d, tol); if (fastFill != null) return fastFill; }
            else if (d > 0)
            {
                // centre: d on both sides; outside ring: d outward; inside ring: d inward
                var fastRing = FastBand(contour, ring == 2 ? 0 : d, ring == 1 ? 0 : -d, tol, 1, miterLimit, cap, join);
                if (fastRing != null) return fastRing;
            }
            var loops = ReadClosed(contour);
            var poly = new List<List<V>>();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var L in loops)
            {
                var P = new List<V>();
                foreach (var sg in L)
                    for (int k = 0; k < 48; k++)
                    {
                        V q = Bez(sg, k / 48.0);
                        P.Add(q);
                        minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
                        minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
                    }
                poly.Add(P);
            }
            var openPolys = ReadOpen(contour);
            foreach (var P in openPolys)
                foreach (var q in P) { minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y); }
            double margin = Math.Abs(d) + 3 * pixel;
            double ox = minX - margin, oy = minY - margin;
            int W = (int)Math.Ceiling((maxX - minX + 2 * margin) / pixel) + 1;
            int H = (int)Math.Ceiling((maxY - minY + 2 * margin) / pixel) + 1;

            // 1. nonzero coverage of the curve on the grid (scanline), then fill the pockets it encloses
            var mask = new bool[W, H];
            var xs = new List<(double x, int dir)>();
            for (int j = 0; j < H; j++)
            {
                double y = oy + j * pixel;
                xs.Clear();
                foreach (var P in poly)
                    for (int k = 0; k < P.Count; k++)
                    {
                        V a = P[k], b = P[(k + 1) % P.Count];
                        if (a.Y <= y && b.Y > y) xs.Add((a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y), 1));
                        else if (b.Y <= y && a.Y > y) xs.Add((a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y), -1));
                    }
                xs.Sort((u1, u2) => u1.x.CompareTo(u2.x));
                int wind = 0, e = 0;
                for (int i = 0; i < W; i++)
                {
                    double x = ox + i * pixel;
                    while (e < xs.Count && xs[e].x <= x) { wind += xs[e].dir; e++; }
                    mask[i, j] = wind != 0;
                }
            }
            var exterior = new bool[W, H];
            var queue = new Queue<int>();
            for (int i = 0; i < W; i++)
                for (int j = 0; j < H; j++)
                    if ((i == 0 || j == 0 || i == W - 1 || j == H - 1) && !mask[i, j]) { exterior[i, j] = true; queue.Enqueue(i * H + j); }
            int[] di = { 1, -1, 0, 0 }, dj = { 0, 0, 1, -1 };
            while (queue.Count > 0)
            {
                int c = queue.Dequeue(), ci = c / H, cj = c % H;
                for (int k = 0; k < 4; k++)
                {
                    int ni = ci + di[k], nj = cj + dj[k];
                    if (ni < 0 || nj < 0 || ni >= W || nj >= H || mask[ni, nj] || exterior[ni, nj]) continue;
                    exterior[ni, nj] = true; queue.Enqueue(ni * H + nj);
                }
            }
            var filled = new bool[W, H];
            for (int i = 0; i < W; i++)
                for (int j = 0; j < H; j++)
                    filled[i, j] = !exterior[i, j];

            // 2. field G. Band: signed distance to the region boundary (grown when d > 0, shrunk when d < 0).
            //    Stroke: unsigned distance to the curve itself, so the stroke is the single level set G = d (no boolean step, no gaps).
            double[,] G;
            if (ring >= 0)
            {
                var seedMap = new bool[W, H];
                foreach (var P in poly)
                    for (int k = 0; k < P.Count; k++)
                    {
                        V a = P[k], b = P[(k + 1) % P.Count];
                        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                        int n = Math.Max(1, (int)Math.Ceiling(len / (0.5 * pixel)));
                        for (int s = 0; s <= n; s++)
                        {
                            double x = a.X + (b.X - a.X) * s / n, y = a.Y + (b.Y - a.Y) * s / n;
                            int i = (int)Math.Round((x - ox) / pixel), j = (int)Math.Round((y - oy) / pixel);
                            if (i >= 0 && j >= 0 && i < W && j < H) seedMap[i, j] = true;
                        }
                    }
                foreach (var P in openPolys)
                    for (int k = 0; k + 1 < P.Count; k++)
                    {
                        V a = P[k], b = P[k + 1];
                        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                        int n = Math.Max(1, (int)Math.Ceiling(len / (0.5 * pixel)));
                        for (int s = 0; s <= n; s++)
                        {
                            double x = a.X + (b.X - a.X) * s / n, y = a.Y + (b.Y - a.Y) * s / n;
                            int i = (int)Math.Round((x - ox) / pixel), j = (int)Math.Round((y - oy) / pixel);
                            if (i >= 0 && j >= 0 && i < W && j < H) seedMap[i, j] = true;
                        }
                    }
                var dist = DistField(seedMap, W, H, pixel);
                G = new double[W, H];
                for (int i = 0; i < W; i++)
                    for (int j = 0; j < H; j++)
                        // ring 1 keeps the outside (exterior) side, ring 2 the inside (filled) side; the other side is pushed far away
                        G[i, j] = ring == 0 ? dist[i, j] : (ring == 1 ? exterior[i, j] : filled[i, j]) ? dist[i, j] : 1e9;
            }
            else
            {
                var dOut = DistField(filled, W, H, pixel);
                var dIn = DistField(exterior, W, H, pixel);
                G = new double[W, H];
                for (int i = 0; i < W; i++)
                    for (int j = 0; j < H; j++)
                        G[i, j] = d > 0 ? dOut[i, j] : -dIn[i, j];
            }

            // 3. marching squares at level d
            double level = d;
            V Pt(long key)
            {
                long idx = key >> 1;
                int i = (int)(idx / H), j = (int)(idx % H);
                if ((key & 1) == 0)
                {
                    double t = (level - G[i, j]) / (G[i + 1, j] - G[i, j]);
                    return new V(ox + (i + t) * pixel, oy + j * pixel);
                }
                else
                {
                    double t = (level - G[i, j]) / (G[i, j + 1] - G[i, j]);
                    return new V(ox + i * pixel, oy + (j + t) * pixel);
                }
            }
            var adj = new Dictionary<long, List<long>>();
            void Link(long a, long b)
            {
                if (!adj.TryGetValue(a, out var la)) { la = new List<long>(); adj[a] = la; }
                if (!adj.TryGetValue(b, out var lb)) { lb = new List<long>(); adj[b] = lb; }
                la.Add(b); lb.Add(a);
            }
            for (int i = 0; i + 1 < W; i++)
                for (int j = 0; j + 1 < H; j++)
                {
                    // corners 0=(i,j) 1=(i+1,j) 2=(i+1,j+1) 3=(i,j+1); edge k joins corner k and k+1
                    bool[] inside = { G[i, j] <= level, G[i + 1, j] <= level, G[i + 1, j + 1] <= level, G[i, j + 1] <= level };
                    long[] ed = { HK(i, j, H), HK(i + 1, j, H) + 1, HK(i, j + 1, H), HK(i, j, H) + 1 };
                    var cr = new List<int>();
                    for (int k = 0; k < 4; k++)
                        if (inside[k] != inside[(k + 1) % 4]) cr.Add(k);
                    if (cr.Count == 2) Link(ed[cr[0]], ed[cr[1]]);
                    else if (cr.Count == 4)
                    {
                        bool centre = (G[i, j] + G[i + 1, j] + G[i + 1, j + 1] + G[i, j + 1]) / 4 <= level;
                        for (int k = 0; k < 4; k++)
                            if (inside[k] != centre) Link(ed[(k + 3) % 4], ed[k]);
                    }
                }

            // 4. walk the closed chains, fit each with cubics
            var result = new VectorPath();
            var visited = new HashSet<long>();
            foreach (long start in adj.Keys)
            {
                if (visited.Contains(start)) continue;
                var pts = new List<V>();
                long prev = -1, cur = start;
                while (true)
                {
                    visited.Add(cur);
                    pts.Add(Pt(cur));
                    var nb = adj[cur];
                    long nxt = (prev == -1 || nb[0] != prev) ? nb[0] : nb[1];
                    if (nxt == start || visited.Contains(nxt)) break;
                    prev = cur; cur = nxt;
                }
                if (pts.Count >= 3) Emit(result, FitLoop(pts, pixel, tol));
            }
            return result;
        }

        static long HK(int i, int j, int H) => ((long)i * H + j) * 2;

        /// <summary>Cubic fit of one closed chain: corners where the chain turns sharply, then tolerance-based fitting per run.</summary>
        static List<Seg> FitLoop(List<V> pts, double pixel, double tol)
        {
            int n = pts.Count;
            double perim = 0;
            for (int i = 0; i < n; i++) perim += (pts[(i + 1) % n] - pts[i]).Len;
            int k = Math.Max(2, (int)Math.Round(10 * pixel / Math.Max(perim / n, 1e-12)));
            k = Math.Min(k, Math.Max(1, n / 3));                       // tiny chains: the window must stay inside the loop
            double thr = 35 * Math.PI / 180;
            var turn = new double[n];
            for (int i = 0; i < n; i++)
            {
                V u1 = pts[i] - pts[((i - k) % n + n) % n], u2 = pts[(i + k) % n] - pts[i];
                turn[i] = Math.Atan2(Math.Abs(V.Cross(u1, u2)), V.Dot(u1, u2));
            }
            var corners = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (turn[i] <= thr) continue;
                bool peak = true;
                for (int o = -k; o <= k && peak; o++)
                    if (turn[(i + o + n) % n] > turn[i]) peak = false;
                if (peak && (corners.Count == 0 || i - corners[corners.Count - 1] > k)) corners.Add(i);
            }
            var starts = corners.Count == 0 ? new List<int> { 0 } : corners;
            var segs = new List<Seg>();
            for (int r = 0; r < starts.Count; r++)
            {
                int s = starts[r], e = starts[(r + 1) % starts.Count];
                int cnt = (e - s + n) % n;
                if (cnt == 0) cnt = n;
                var run = new List<V>(cnt + 1);
                for (int t = 0; t <= cnt; t++) run.Add(pts[(s + t) % n]);
                FitRun(run, TangentAt(pts, s, k, true), TangentAt(pts, e, k, false), tol, segs, 0);
            }
            return segs;
        }

        static V TangentAt(List<V> pts, int i, int k, bool forward)
        {
            int n = pts.Count;
            V q = forward ? pts[(i + k) % n] : pts[(i - k + n) % n];
            return Unit(q - pts[i]);
        }

        /// <summary>Schneider-style least-squares cubic fit with tangents t1 and t2, splitting at the worst point.</summary>
        static void FitRun(List<V> pts, V t1, V t2, double err, List<Seg> outSegs, int depth)
        {
            int n = pts.Count;
            if (n < 2) return;
            if (n == 2)
            {
                double dist = (pts[1] - pts[0]).Len / 3.0;
                outSegs.Add(new Seg { P0 = pts[0], C1 = pts[0] + t1 * dist, C2 = pts[1] + t2 * dist, P1 = pts[1], Cub = true });
                return;
            }
            double[] u = ChordParam(pts);
            V[] bz = GenBez(pts, u, t1, t2);
            double maxErr = MaxErr(pts, bz, u, out int split);
            if (maxErr > err && maxErr <= err * 4)
            {
                for (int it = 0; it < 20 && maxErr > err; it++)
                {
                    u = Reparam(bz, pts, u);
                    bz = GenBez(pts, u, t1, t2);
                    maxErr = MaxErr(pts, bz, u, out split);
                }
            }
            if (maxErr <= err || depth > 40 || split < 1 || split > n - 2)
            {
                outSegs.Add(new Seg { P0 = bz[0], C1 = bz[1], C2 = bz[2], P1 = bz[3], Cub = true });
                return;
            }
            V tc = Unit(pts[split - 1] - pts[split + 1]);
            FitRun(pts.GetRange(0, split + 1), t1, tc, err, outSegs, depth + 1);
            FitRun(pts.GetRange(split, n - split), tc * -1.0, t2, err, outSegs, depth + 1);
        }

        static double[] ChordParam(List<V> pts)
        {
            int n = pts.Count;
            var u = new double[n];
            for (int i = 1; i < n; i++) u[i] = u[i - 1] + (pts[i] - pts[i - 1]).Len;
            double tot = u[n - 1];
            for (int i = 1; i < n; i++) u[i] = tot > 0 ? u[i] / tot : (double)i / (n - 1);
            return u;
        }

        static V[] GenBez(List<V> pts, double[] u, V t1, V t2)
        {
            int n = pts.Count;
            V p0 = pts[0], p3 = pts[n - 1];
            double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
            for (int i = 0; i < n; i++)
            {
                double t = u[i], s = 1 - t;
                double b0 = s * s * s, b1 = 3 * t * s * s, b2 = 3 * t * t * s, b3 = t * t * t;
                V a1 = t1 * b1, a2 = t2 * b2;
                V tmp = pts[i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
                c00 += V.Dot(a1, a1); c01 += V.Dot(a1, a2); c11 += V.Dot(a2, a2);
                x0 += V.Dot(a1, tmp); x1 += V.Dot(a2, tmp);
            }
            double det = c00 * c11 - c01 * c01;
            double segLen = (p3 - p0).Len;
            double al = 0, ar = 0;
            if (Math.Abs(det) > 1e-12) { al = (x0 * c11 - x1 * c01) / det; ar = (c00 * x1 - c01 * x0) / det; }
            double eps = 1e-6 * segLen;
            if (!(al > eps) || !(ar > eps)) { al = segLen / 3; ar = segLen / 3; }
            return new[] { p0, p0 + t1 * al, p3 + t2 * ar, p3 };
        }

        static V Q(V[] b, double t)
        {
            double s = 1 - t;
            return b[0] * (s * s * s) + b[1] * (3 * t * s * s) + b[2] * (3 * t * t * s) + b[3] * (t * t * t);
        }

        static double MaxErr(List<V> pts, V[] bz, double[] u, out int split)
        {
            int n = pts.Count;
            double mx = 0;
            split = n / 2;
            for (int i = 1; i < n - 1; i++)
            {
                V d = Q(bz, u[i]) - pts[i];
                double e2 = V.Dot(d, d);
                if (e2 > mx) { mx = e2; split = i; }
            }
            return Math.Sqrt(mx);
        }

        static double[] Reparam(V[] b, List<V> pts, double[] u)
        {
            var r = new double[u.Length];
            for (int i = 0; i < u.Length; i++)
            {
                double t = u[i], s = 1 - t;
                V q = Q(b, t);
                V q1 = ((b[1] - b[0]) * (s * s) + (b[2] - b[1]) * (2 * s * t) + (b[3] - b[2]) * (t * t)) * 3;
                V d0 = b[2] - b[1] * 2 + b[0], d1 = b[3] - b[2] * 2 + b[1];
                V q2 = (d0 * s + d1 * t) * 6;
                V diff = q - pts[i];
                double num = V.Dot(diff, q1), den = V.Dot(q1, q1) + V.Dot(diff, q2);
                r[i] = den != 0 ? Math.Clamp(t - num / den, 0, 1) : t;
            }
            r[0] = 0; r[u.Length - 1] = 1;
            return r;
        }

        /// <summary>Distance in contour units from every grid point to the nearest seed point (exact, separable squared EDT).</summary>
        static double[,] DistField(bool[,] seed, int W, int H, double pixel)
        {
            const double INF = 1e20;
            var g = new double[W, H];
            for (int i = 0; i < W; i++)
                for (int j = 0; j < H; j++)
                    g[i, j] = seed[i, j] ? 0 : INF;
            int N = Math.Max(W, H);
            var buf = new double[N];
            var res = new double[N];
            var v = new int[N];
            var z = new double[N + 1];
            for (int i = 0; i < W; i++)
            {
                for (int j = 0; j < H; j++) buf[j] = g[i, j];
                Dt1(buf, H, res, v, z);
                for (int j = 0; j < H; j++) g[i, j] = res[j];
            }
            for (int j = 0; j < H; j++)
            {
                for (int i = 0; i < W; i++) buf[i] = g[i, j];
                Dt1(buf, W, res, v, z);
                for (int i = 0; i < W; i++) g[i, j] = Math.Sqrt(res[i]) * pixel;
            }
            return g;
        }

        static void Dt1(double[] f, int n, double[] dst, int[] v, double[] z)
        {
            int k = 0;
            v[0] = 0; z[0] = -1e30; z[1] = 1e30;
            for (int q = 1; q < n; q++)
            {
                double s;
                while (true)
                {
                    int vk = v[k];
                    s = ((f[q] + (double)q * q) - (f[vk] + (double)vk * vk)) / (2.0 * q - 2.0 * vk);
                    if (s <= z[k]) k--; else break;
                }
                k++;
                v[k] = q; z[k] = s; z[k + 1] = 1e30;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                double dq = q - v[k];
                dst[q] = dq * dq + f[v[k]];
            }
        }

        /// <summary>Largest deviation of a band's cubics from the contour, measured against |d|, plus the segment count.</summary>
        /// <summary>Largest |distance of the band boundary to the contour - |d||. Closed loops close back on themselves; open subpaths are edges with no closing edge.
        /// skipContour: a ring band also contains the contour itself (distance 0); those points are not the offset, so they are skipped.</summary>
        public static (double maxDev, int segments) CheckBand(VectorPath contour, VectorPath band, double d, bool skipContour = true)
        {
            var edges = new List<(V A, V B)>();
            foreach (var L in ReadClosed(contour))
            {
                var pts = new List<V>();
                foreach (var sg in L)
                    for (int k = 0; k < 48; k++) pts.Add(Bez(sg, k / 48.0));
                for (int i = 0; i < pts.Count; i++) edges.Add((pts[i], pts[(i + 1) % pts.Count]));
            }
            foreach (var P in ReadOpen(contour))
                for (int i = 0; i + 1 < P.Count; i++) edges.Add((P[i], P[i + 1]));
            double mx = 0;
            int segs = 0;
            foreach (var L in ReadClosed(band))
                foreach (var sg in L)
                {
                    segs++;
                    for (int k = 0; k <= 16; k++)
                    {
                        V p = Bez(sg, k / 16.0);
                        double best = double.MaxValue;
                        foreach (var e in edges) best = Math.Min(best, DistSeg(p, e.A, e.B));
                        if (skipContour && best < 0.5 * Math.Abs(d)) continue;
                        mx = Math.Max(mx, Math.Abs(best - Math.Abs(d)));
                    }
                }
            return (mx, segs);
        }

        static double DistSeg(V p, V a, V b)
        {
            V ab = b - a;
            double l2 = V.Dot(ab, ab);
            double t = l2 > 0 ? Math.Clamp(V.Dot(p - a, ab) / l2, 0, 1) : 0;
            return (p - (a + ab * t)).Len;
        }

}
}
