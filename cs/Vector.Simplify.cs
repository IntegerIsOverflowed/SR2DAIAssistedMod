// Vector.Simplify.cs - QGIS-style geometry simplification for vector pictures.
// Three algorithms reduce the points of a line or ring, as QGIS does for a layer's "Simplify geometry" setting:
//   Linear      - Douglas-Peucker: a point is dropped when it lies within the threshold of the line through its neighbours.
//   SnapToGrid  - every point is moved to the nearest grid node (the grid is the threshold) and repeats are dropped.
//   Visvalingam - the point that makes the smallest triangle with its neighbours goes first, until every triangle is
//                 at least threshold^2 in area.
// A shape that would collapse below its minimum point count keeps its geometry: it is not dropped.
// Thresholds are in the picture's own units (for MBTiles: pixels at the zoom the picture was read at).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace Sr2d64CSport
{
    /// <summary>How points are removed by <see cref="VectorSimplify"/>.</summary>
    public enum SimplifyAlgorithm { Linear, SnapToGrid, Visvalingam }

    /// <summary>QGIS-style simplification: threshold, algorithm, and whether it runs while the features are read.</summary>
    public sealed class SimplifySettings
    {
        /// <summary>
        /// The threshold in picture units; 0 = off. Linear: the largest distance a dropped point may lie from the line.
        /// SnapToGrid: the grid cell size. Visvalingam: the square root of the smallest triangle area that is kept.
        /// </summary>
        public double Tolerance;
        public SimplifyAlgorithm Algorithm = SimplifyAlgorithm.Linear;
        /// <summary>
        /// True: each feature is simplified while its tile is read (the provider side, the cheapest place).
        /// False: the picture is simplified once it is built. The result is the same either way.
        /// </summary>
        public bool ProviderSide = true;
        /// <summary>
        /// The view scale (screen pixels per picture unit; 1 = 1:1, the picture's own size) at or below which the
        /// simplification applies. Zoomed out to 1:1 or further, the picture is simplified; zoomed in past it, the full
        /// detail is drawn. Default 1 (1:1).
        /// </summary>
        public double MaxScale = 1.0;
        public bool On => Tolerance > 0;
        /// <summary>True when the simplification applies at this view scale (the threshold is on and the view is at or below MaxScale).</summary>
        public bool AppliesAt(double viewScale) => On && viewScale <= MaxScale;
    }

    /// <summary>Point reduction for polylines and rings, and for the shapes of a <see cref="VectorImage"/>.</summary>
    internal static class VectorSimplify
    {
        /// <summary>Simplifies one polyline (or ring, when closed). The ring's closing point must not be repeated.</summary>
        public static List<PointF> Line(List<PointF> pts, bool closed, SimplifySettings s)
        {
            var p = new List<PointF>(pts.Count);
            foreach (var q in pts) if (p.Count == 0 || !Same(p[p.Count - 1], q)) p.Add(q);
            if (closed && p.Count > 1 && Same(p[0], p[p.Count - 1])) p.RemoveAt(p.Count - 1);
            int min = closed ? 3 : 2;
            if (!s.On || p.Count <= min) return p;
            List<PointF> r = s.Algorithm switch
            {
                SimplifyAlgorithm.SnapToGrid => Snap(p, closed, s.Tolerance),
                SimplifyAlgorithm.Visvalingam => Visvalingam(p, closed, s.Tolerance),
                _ => Douglas(p, closed, s.Tolerance),
            };
            return r.Count >= min ? r : p;      // collapsed: keep the geometry rather than drop the feature
        }

        /// <summary>Simplifies every shape of the picture in place (curves are flattened to short lines first).</summary>
        public static void Apply(VectorShape sh, SimplifySettings s)
        {
            if (!s.On || sh.Path == null || sh.Path.IsEmpty || sh.IsText || sh.IsImage) return;
            var pts = new List<PointF>();
            var subs = VectorRender.Flatten(sh.Path, Matrix3x2.Identity, 0.25f, pts);
            var path = sh.Path;
            path.Clear();
            foreach (var (start, count, closed) in subs)
            {
                var r = Line(pts.GetRange(start, count), closed, s);
                if (r.Count == 0) continue;
                path.MoveTo(r[0]);
                for (int i = 1; i < r.Count; i++) path.LineTo(r[i]);
                if (closed) path.Close();
            }
        }

        static bool Same(PointF a, PointF b) => Math.Abs(a.X - b.X) < 1e-4f && Math.Abs(a.Y - b.Y) < 1e-4f;

        static double Dist(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>Distance from p to the line through a and b (to a when a and b coincide).</summary>
        static double LineDist(PointF p, PointF a, PointF b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return Dist(p, a);
            return Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / len;
        }

        // ---------------------------------------------------------------- Linear (Douglas-Peucker)
        static List<PointF> Douglas(List<PointF> p, bool closed, double tol)
        {
            int n = p.Count;
            var keep = new bool[n + (closed ? 1 : 0)];
            var seq = p;
            if (closed)
            {
                // a ring has no two fixed ends: split it at the point farthest from the first, then run both halves
                seq = new List<PointF>(p) { p[0] };
                int far = 1; double best = -1;
                for (int i = 1; i < n; i++) { double d = Dist(p[i], p[0]); if (d > best) { best = d; far = i; } }
                keep[0] = keep[far] = keep[n] = true;
                Mark(seq, keep, 0, far, tol);
                Mark(seq, keep, far, n, tol);
            }
            else
            {
                keep[0] = keep[n - 1] = true;
                Mark(seq, keep, 0, n - 1, tol);
            }
            var r = new List<PointF>(n);
            for (int i = 0; i < n; i++) if (keep[i]) r.Add(p[i]);
            return r;
        }

        static void Mark(List<PointF> p, bool[] keep, int first, int last, double tol)
        {
            var stack = new Stack<(int a, int b)>();
            stack.Push((first, last));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                if (b - a < 2) continue;
                double worst = -1; int at = -1;
                for (int i = a + 1; i < b; i++)
                {
                    double d = LineDist(p[i], p[a], p[b]);
                    if (d > worst) { worst = d; at = i; }
                }
                if (worst > tol)
                {
                    keep[at] = true;
                    stack.Push((a, at));
                    stack.Push((at, b));
                }
            }
        }

        // ---------------------------------------------------------------- Snap to grid
        static List<PointF> Snap(List<PointF> p, bool closed, double grid)
        {
            var r = new List<PointF>(p.Count);
            foreach (var q in p)
            {
                var s = new PointF((float)(Math.Round(q.X / grid) * grid), (float)(Math.Round(q.Y / grid) * grid));
                if (r.Count == 0 || !Same(r[r.Count - 1], s)) r.Add(s);
            }
            if (closed && r.Count > 1 && Same(r[0], r[r.Count - 1])) r.RemoveAt(r.Count - 1);
            return r;
        }

        // ---------------------------------------------------------------- Visvalingam (least triangle area first)
        static List<PointF> Visvalingam(List<PointF> p, bool closed, double tol)
        {
            int n = p.Count, min = closed ? 3 : 2;
            var prev = new int[n];
            var next = new int[n];
            var alive = new bool[n];
            var area = new double[n];
            double thr = tol * tol;
            for (int i = 0; i < n; i++)
            {
                alive[i] = true;
                prev[i] = closed ? (i - 1 + n) % n : i - 1;
                next[i] = closed ? (i + 1) % n : (i + 1 < n ? i + 1 : -1);
            }
            // an end of an open line is never removed: its area is the largest possible
            double Area(int i)
            {
                int a = prev[i], b = next[i];
                if (a < 0 || b < 0) return double.MaxValue;
                double cross = (p[a].X - p[i].X) * (p[b].Y - p[i].Y) - (p[a].Y - p[i].Y) * (p[b].X - p[i].X);
                return Math.Abs(cross) * 0.5;
            }
            var set = new SortedSet<(double area, int idx)>();
            for (int i = 0; i < n; i++) { area[i] = Area(i); set.Add((area[i], i)); }
            int count = n;
            while (count > min && set.Count > 0)
            {
                var (a, i) = set.Min;
                if (a >= thr) break;                     // every remaining triangle is big enough
                set.Remove((a, i));
                alive[i] = false; count--;
                int pa = prev[i], nb = next[i];
                next[pa] = nb; prev[nb] = pa;
                foreach (int j in new[] { pa, nb })
                {
                    set.Remove((area[j], j));
                    area[j] = Area(j);
                    set.Add((area[j], j));
                }
            }
            var r = new List<PointF>(count);
            for (int i = 0; i < n; i++) if (alive[i]) r.Add(p[i]);
            return r;
        }
    }

    internal sealed partial class VectorImage
    {
        /// <summary>Simplifies every shape of the picture (see <see cref="SimplifySettings"/>). Off when the tolerance is 0.</summary>
        public void Simplify(SimplifySettings s)
        {
            if (!s.On) return;
            foreach (var sh in Shapes) VectorSimplify.Apply(sh, s);
            Touch();
        }
    }
}
