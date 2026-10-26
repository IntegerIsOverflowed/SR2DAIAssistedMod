// Analytic truth for a stroked path: no stroker code, only the geometric definitions.
//
// A stroke region is a union of primitives, each of which answers "is this point inside" in closed form:
//   - the body of a segment  = a rectangle (convex polygon), extended at the ends by a square cap;
//   - a round cap            = the half disc beyond the end line;
//   - a round join           = the disc sector between the two outer normals, on the outer side of the turn;
//   - a bevel join           = the triangle vertex -> outer normal 0 -> outer normal 1;
//   - a miter join           = the quadrilateral vertex -> n0 -> miter tip -> n1, or a bevel when the miter
//                              length / width ratio exceeds the limit (the SVG/PDF rule, ratio = 1/sin(phi/2)).
// Coverage of a pixel is the fraction of an even subgrid inside the region, so the truth image is antialiased the
// same way a renderer's output is and can be compared pixel for pixel.
//
// The point of writing it this way is independence: the engine builds the same primitives as polygons and fills
// them with one non-zero-winding scanline pass, so a difference between truth and engine is a rasterisation or
// composition defect (a missed join, a notch, a seam), not a disagreement about what a join means.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sr2d64CSport
{
    /// <summary>A point of the truth region: a convex polygon, a disc, a disc sector or a half disc.
    /// Each one also reports a bounding box: the rasteriser walks primitives over their own box instead of testing
    /// every box against every pixel, which is the difference between milliseconds and minutes on a curve.</summary>
    internal abstract class Prim
    {
        public abstract bool Has(double x, double y);
        public double X0, Y0, X1, Y1;         // inclusive bounds in canvas units, set by the constructor

        internal sealed class Poly : Prim
        {
            readonly double[] xs, ys; readonly int n; readonly double[] ex, ey;   // edge normals pointing inwards
            public Poly(List<(double X, double Y)> v)
            {
                n = v.Count; xs = new double[n]; ys = new double[n];
                for (int i = 0; i < n; i++) { xs[i] = v[i].X; ys[i] = v[i].Y; }
                double mnx = double.MaxValue, mny = mnx, mxx = -mnx, mxy = -mny;
                for (int i = 0; i < n; i++)
                { mnx = Math.Min(mnx, xs[i]); mny = Math.Min(mny, ys[i]); mxx = Math.Max(mxx, xs[i]); mxy = Math.Max(mxy, ys[i]); }
                X0 = mnx; Y0 = mny; X1 = mxx; Y1 = mxy;
                double area = 0; for (int i = 0, j = n - 1; i < n; j = i++) area += xs[j] * ys[i] - xs[i] * ys[j];
                if (area < 0) { Array.Reverse(xs); Array.Reverse(ys); }
                // edge i points from vertex i to vertex i+1, so it has to be built after the winding is fixed
                ex = new double[n]; ey = new double[n];
                for (int i = 0; i < n; i++) { int j = (i + 1) % n; ex[i] = xs[j] - xs[i]; ey[i] = ys[j] - ys[i]; }
            }
            public override bool Has(double x, double y)
            {
                for (int i = 0; i < n; i++)
                {   // inside every left edge of the counter-clockwise polygon
                    double dx = x - xs[i], dy = y - ys[i];
                    if (-ey[i] * dx + ex[i] * dy < 0) return false;
                }
                return true;
            }
        }

        internal sealed class Disc : Prim
        {
            readonly double cx, cy, r2;
            public Disc(double cx, double cy, double r)
            { this.cx = cx; this.cy = cy; r2 = r * r; X0 = cx - r; Y0 = cy - r; X1 = cx + r; Y1 = cy + r; }
            public override bool Has(double x, double y) { double dx = x - cx, dy = y - cy; return dx * dx + dy * dy <= r2; }
        }

        /// <summary>Points within r of c whose direction from c lies in [a0, a0+sweep] (sweep may be negative).</summary>
        internal sealed class Sector : Prim
        {
            readonly double cx, cy, r2, a0, sweep;
            public Sector(double cx, double cy, double r, double a0, double sweep)
            {
                this.cx = cx; this.cy = cy; r2 = r * r; this.a0 = a0; this.sweep = sweep;
                X0 = cx; Y0 = cy; X1 = cx; Y1 = cy;                      // the apex is always part of the sector
                Corn(a0, r); Corn(a0 + sweep, r);                          // the two arc ends
                for (int k = 0; k < 4; k++)                                // and any cardinal direction the arc turns through
                    if (Contains(k * Math.PI / 2)) Corn(k * Math.PI / 2, r);
            }
            void Corn(double a, double r)
            {
                double x = cx + r * Math.Cos(a), y = cy + r * Math.Sin(a);
                X0 = Math.Min(X0, x); Y0 = Math.Min(Y0, y); X1 = Math.Max(X1, x); Y1 = Math.Max(Y1, y);
            }
            bool Contains(double a)
            {
                double d = a - a0;
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                return sweep >= 0 ? d >= -1e-12 && d <= sweep + 1e-12 : d <= 1e-12 && d >= sweep - 1e-12;
            }
            public override bool Has(double x, double y)
            {
                double dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r2) return false;
                if (dx == 0 && dy == 0) return true;
                double a = Math.Atan2(dy, dx) - a0;
                while (a > Math.PI) a -= 2 * Math.PI;
                while (a < -Math.PI) a += 2 * Math.PI;
                return sweep >= 0 ? a >= -1e-12 && a <= sweep + 1e-12 : a <= 1e-12 && a >= sweep - 1e-12;
            }
        }

        /// <summary>The half disc on the far side of the line through c perpendicular to dir (a round cap).</summary>
        internal sealed class HalfDisc : Prim
        {
            readonly double cx, cy, r2, dx, dy;
            public HalfDisc(double cx, double cy, double r, double dirX, double dirY)
            { this.cx = cx; this.cy = cy; r2 = r * r; double l = Math.Sqrt(dirX * dirX + dirY * dirY); if (l < 1e-12) { dx = 1; dy = 0; } else { dx = dirX / l; dy = dirY / l; } X0 = cx - r; Y0 = cy - r; X1 = cx + r; Y1 = cy + r; }
            public override bool Has(double x, double y)
            {
                double ex = x - cx, ey = y - cy;
                return ex * ex + ey * ey <= r2 && ex * dx + ey * dy >= 0;
            }
        }
    }

    /// <summary>The truth region of one stroke: primitives plus, for an inside / outside alignment, the side test.</summary>
    internal sealed class TruthRegion
    {
        public List<Prim> Prims = new List<Prim>();
        public Func<double, double, bool> Side;      // null = centred (no clip); otherwise the point must also satisfy this

        public bool Has(double x, double y)
        {
            for (int i = 0; i < Prims.Count; i++)
                if (Prims[i].Has(x, y) && (Side == null || Side(x, y))) return true;
            return false;
        }
    }

    internal static class Truth
    {
        const double Eps = 1e-9;

        /// <summary>Builds the stroke region of one flattened sub-path.</summary>
        public static void AddStroke(TruthRegion reg, List<(double X, double Y)> P, bool closed, double hw,
                                     Cap cap, Join join, double miterLimit)
        {
            int n = P.Count;
            if (n == 1)
            {   // a dot: round cap = disc, square cap = square, butt = nothing
                if (cap == Cap.Round) reg.Prims.Add(new Prim.Disc(P[0].X, P[0].Y, hw));
                else if (cap == Cap.Square) reg.Prims.Add(SquareAt(P[0].X, P[0].Y, hw, 0, 1));
                return;
            }
            if (n == 2 && Math.Abs(P[0].X - P[1].X) < Eps && Math.Abs(P[0].Y - P[1].Y) < Eps)
            {   // a zero-length piece: same rule as a dot, with the direction of the (degenerate) segment unknown
                if (cap == Cap.Round) reg.Prims.Add(new Prim.Disc(P[0].X, P[0].Y, hw));
                else if (cap == Cap.Square) reg.Prims.Add(SquareAt(P[0].X, P[0].Y, hw, 0, 1));
                return;
            }
            if (closed && n > 2 && Math.Abs(P[0].X - P[n - 1].X) < Eps && Math.Abs(P[0].Y - P[n - 1].Y) < Eps) n--;
            if (n == 2) closed = false;
            int segs = closed ? n : n - 1;

            for (int i = 0; i < segs; i++)
            {
                var a = P[i]; var b = P[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < Eps) continue;
                double ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
                double ea = 0, eb = 0;
                if (!closed && cap == Cap.Square) { if (i == 0) ea = hw; if (i == segs - 1) eb = hw; }
                double ax = a.X - ux * ea, ay = a.Y - uy * ea, bx = b.X + ux * eb, by = b.Y + uy * eb;
                reg.Prims.Add(new Prim.Poly(new List<(double, double)>
                {
                    (ax + nx * hw, ay + ny * hw), (bx + nx * hw, by + ny * hw),
                    (bx - nx * hw, by - ny * hw), (ax - nx * hw, ay - ny * hw),
                }));
            }
            if (!closed && cap == Cap.Round)
            {   // the half disc beyond each end line, facing away from the path
                var t0 = P[1]; var e0 = P[0]; reg.Prims.Add(new Prim.HalfDisc(e0.X, e0.Y, hw, e0.X - t0.X, e0.Y - t0.Y));
                var t1 = P[n - 2]; var e1 = P[n - 1]; reg.Prims.Add(new Prim.HalfDisc(e1.X, e1.Y, hw, e1.X - t1.X, e1.Y - t1.Y));
            }
            int first = closed ? 0 : 1, last = closed ? n - 1 : n - 2;
            for (int i = first; i <= last; i++)
            {
                var pp = P[(i - 1 + n) % n]; var pc = P[i]; var pn = P[(i + 1) % n];
                double d0x = pc.X - pp.X, d0y = pc.Y - pp.Y, d1x = pn.X - pc.X, d1y = pn.Y - pc.Y;
                double l0 = Math.Sqrt(d0x * d0x + d0y * d0y), l1 = Math.Sqrt(d1x * d1x + d1y * d1y);
                if (l0 < Eps || l1 < Eps) continue;
                d0x /= l0; d0y /= l0; d1x /= l1; d1y /= l1;
                double cross = d0x * d1y - d0y * d1x, dot = d0x * d1x + d0y * d1y;
                double th = Math.Atan2(Math.Abs(cross), dot);           // the turn angle, 0..pi
                if (th < 1e-6) continue;

                if (th > Math.PI - 1e-6)
                {   // a 180 degree reversal: the band doubles back, so both sides open and the join is a full disc
                    if (join == Join.Round) reg.Prims.Add(new Prim.Disc(pc.X, pc.Y, hw));
                    else
                    {
                        foreach (double s in new[] { -1.0, 1.0 })
                            reg.Prims.Add(JoinPoly(pc.X, pc.Y, d0x, d0y, d1x, d1y, hw, s, join, miterLimit, th));
                    }
                    continue;
                }
                double sgn = cross > 0 ? -1.0 : 1.0;                    // the outer side of the turn
                reg.Prims.Add(JoinPoly(pc.X, pc.Y, d0x, d0y, d1x, d1y, hw, sgn, join, miterLimit, th));
            }
        }

        static Prim SquareAt(double x, double y, double hw, double ux, double uy)
            => new Prim.Poly(new List<(double, double)> { (x - hw, y - hw), (x + hw, y - hw), (x + hw, y + hw), (x - hw, y + hw) });

        static Prim JoinPoly(double cx, double cy, double d0x, double d0y, double d1x, double d1y,
                             double hw, double s, Join join, double miterLimit, double th)
        {
            double n0x = -d0y * hw * s, n0y = d0x * hw * s, n1x = -d1y * hw * s, n1y = d1x * hw * s;
            if (join == Join.Round)
            {   // the sector from n0 to n1 going the short way round the outside
                double a0 = Math.Atan2(n0y, n0x), a1 = Math.Atan2(n1y, n1x);
                double sweep = a1 - a0; while (sweep > Math.PI) sweep -= 2 * Math.PI; while (sweep < -Math.PI) sweep += 2 * Math.PI;
                return new Prim.Sector(cx, cy, hw, a0, sweep);
            }
            if (join == Join.Miter)
            {   // ratio = miter length / stroke width = 1 / sin(phi / 2), phi = pi - th the interior angle
                double phi = Math.PI - th, ratio = 1 / Math.Max(1e-9, Math.Sin(phi / 2));
                if (ratio <= miterLimit)
                {
                    double mx = n0x + n1x, my = n0y + n1y, ml = Math.Sqrt(mx * mx + my * my);
                    if (ml > 1e-9)
                    {   // the tip is where the two outer offset lines meet: distance hw / cos(th / 2) along their bisector
                        double L = hw / Math.Max(1e-9, Math.Cos(th / 2));
                        return new Prim.Poly(new List<(double, double)> { (cx, cy), (cx + n0x, cy + n0y), (cx + mx / ml * L, cy + my / ml * L), (cx + n1x, cy + n1y) });
                    }
                }
            }
            return new Prim.Poly(new List<(double, double)> { (cx, cy), (cx + n0x, cy + n0y), (cx + n1x, cy + n1y) });
        }

        /// <summary>Non-zero winding of a flattened path (the side test for an inside / outside alignment).</summary>
        public static Func<double, double, bool> Interior(List<List<(double X, double Y)>> rings)
        {
            return (x, y) =>
            {
                int w = 0;
                foreach (var r in rings)
                {
                    int n = r.Count; if (n < 3) continue;
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        double yi = r[i].Y, yj = r[j].Y;
                        if (yi > y == yj > y) continue;
                        double xint = r[j].X + (y - yj) / (yi - yj) * (r[i].X - r[j].X);
                        if (x < xint) w += yi > yj ? 1 : -1;
                    }
                }
                return w != 0;
            };
        }

        public static Func<double, double, bool> Exterior(List<List<(double X, double Y)>> rings)
        {
            var inside = Interior(rings);
            return (x, y) => !inside(x, y);
        }

        /// <summary>Rasterises the region into a coverage buffer (0 = paper, 255 = ink) with a sub x sub subgrid per pixel.
        /// Each primitive only visits its own bounding box, and the sub-hits accumulate, so the result is the union's
        /// coverage (clamped at fully inked) at the cost of one pass over the ink rather than one over every prim.</summary>
        /// <remarks>The subgrid count must be EVEN. Samples sit at (2k+1)/(2*sub) inside the pixel, so an odd sub puts one
        /// sample exactly on the pixel's centre line - and a stroke of odd width centred on an integer coordinate has its
        /// edge exactly there (a band of width 3 on y=90 runs from 88.5 to 91.5). The inside tests are inclusive, so that
        /// sample counts as ink and every half-covered row comes out at 3/5 instead of 1/2: measured as a constant
        /// per-row excess in the truth on hline and reversal, with all four renderers agreeing against it. With sub=8 the
        /// samples are at odd/16 and no half-integer boundary can coincide with one.</remarks>
        public static int Sub = 8;
        public static byte[] Raster(TruthRegion reg, int w, int h, int sub = 0)
        {
            if (sub <= 0) sub = Sub;
            if (sub > 8) sub = 8;                       // one bit per sub-sample, so at most 8 x 8
            // the union, not the sum: a stroke that covers a pixel twice (a band doubled back on itself, a self-crossing
            // path) must still read as fully inked there, so each pixel keeps a mask of inked sub-samples and ors the prims in
            var mask = new ulong[w * h];
            double step = 1.0 / sub, half = step / 2;
            var side = reg.Side;
            foreach (var prim in reg.Prims)
            {
                if (!(prim.X1 >= 0 && prim.X0 < w && prim.Y1 >= 0 && prim.Y0 < h)) continue;
                int x0 = Math.Max(0, (int)Math.Floor(prim.X0 - half)), x1 = Math.Min(w - 1, (int)Math.Ceiling(prim.X1 + half));
                int y0 = Math.Max(0, (int)Math.Floor(prim.Y0 - half)), y1 = Math.Min(h - 1, (int)Math.Ceiling(prim.Y1 + half));
                for (int py = y0; py <= y1; py++)
                    for (int px = x0; px <= x1; px++)
                    {
                        ulong bits = 0;
                        for (int sy = 0; sy < sub; sy++)
                        {
                            double y = py + half + sy * step;
                            for (int sx = 0; sx < sub; sx++)
                            {
                                double x = px + half + sx * step;
                                if (prim.Has(x, y) && (side == null || side(x, y))) bits |= 1UL << (sy * sub + sx);
                            }
                        }
                        mask[py * w + px] |= bits;
                    }
            }
            var buf = new byte[w * h];
            int full = sub * sub;
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(BitOperations.PopCount(mask[i]) * 255 / full);
            return buf;
        }
    }

    internal enum Cap { Butt, Round, Square }
    internal enum Join { Miter, Round, Bevel }
}
