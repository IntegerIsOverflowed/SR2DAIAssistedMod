// The fixture set and the case matrix for the stroke truth harness.
//
// Paths are in canvas pixels on a 240 x 180 canvas, chosen so that each one isolates one part of the stroke
// definition: caps alone (a straight line), joins at one angle (chevrons), a nearly straight vertex (the case a
// join-culling shortcut can drop), a 180 degree reversal, an acute corner against the miter limit, a curve
// (many shallow vertices - where a dropped join shows as a scallop), a smooth closed shape, an axis-aligned
// square (exact miter arithmetic), a self-crossing path, mixed line/cubic sub-paths (the join at a jagged to
// smooth kind boundary, including a near-limit miter), and dashes (even, odd and dotted patterns).
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sr2d64CSport
{
    internal sealed class Case
    {
        public string Name;
        public VectorPath Path;
        public double Width;
        public Cap Cap;
        public Join Join;
        public double Miter = 4;
        public float[] Dash;
        public double DashOffset;
        public int Align;                                  // 0 centre, 1 outside, 2 inside (the demo's position selector)
        public override string ToString() =>
            $"{Name} w={Width:0.#} {Cap}/{Join}{(Join == Join.Miter ? ":" + Miter.ToString("0.#") : "")}" +
            (Dash != null ? " dash[" + string.Join(",", Array.ConvertAll(Dash, d => d.ToString("0.#"))) + "]" : "") +
            (Align != 0 ? " " + (Align == 1 ? "outside" : "inside") : "");
    }

    internal static class Fixtures
    {
        static VectorPath Open(params (double X, double Y)[] pts)
        {
            var p = new VectorPath();
            for (int i = 0; i < pts.Length; i++)
            { if (i == 0) p.MoveTo((float)pts[i].X, (float)pts[i].Y); else p.LineTo((float)pts[i].X, (float)pts[i].Y); }
            return p;
        }
        static VectorPath Closed(params (double X, double Y)[] pts)
        {
            var p = Open(pts); p.Close(); return p;
        }
        static (double, double) P(double x, double y) => (x, y);

        static VectorPath CircleCubic(double cx, double cy, double r)
        {   // the four-arc cubic approximation, k = 4/3 * tan(pi/8) * r
            double k = 0.5522847498307936 * r;
            var p = new VectorPath();
            p.MoveTo((float)(cx + r), (float)cy);
            p.CurveTo((float)(cx + r), (float)(cy + k), (float)(cx + k), (float)(cy + r), (float)cx, (float)(cy + r));
            p.CurveTo((float)(cx - k), (float)(cy + r), (float)(cx - r), (float)(cy + k), (float)(cx - r), (float)cy);
            p.CurveTo((float)(cx - r), (float)(cy - k), (float)(cx - k), (float)(cy - r), (float)cx, (float)(cy - r));
            p.CurveTo((float)(cx + k), (float)(cy - r), (float)(cx + r), (float)(cy - k), (float)(cx + r), (float)cy);
            p.Close();
            return p;
        }
        static VectorPath SCurve()
        {   // one open cubic with an inflection: the curvature changes sign mid-segment
            var p = new VectorPath();
            p.MoveTo(30, 130); p.CurveTo(70, 20, 170, 160, 210, 50);
            return p;
        }
        static VectorPath Fig8()
        {   // two cubics that cross themselves once
            var p = new VectorPath();
            p.MoveTo(40, 90); p.CurveTo(120, -20, 160, 200, 210, 90);
            p.CurveTo(170, 20, 110, 160, 30, 100);
            return p;
        }
        static VectorPath JaggedToSmooth()
        {   // one closed sub-path that switches kind: a jagged line run, a smooth cubic run, then a line again.
            // the joins at the two Line<->Cubic nodes used to take the coarse flattening chord's tangent instead
            // of the analytic Bezier tangent, so the stroke disagreed with the baked outline conversion there
            var p = new VectorPath();
            p.MoveTo(20, 120);
            p.LineTo(45, 114); p.LineTo(70, 120); p.LineTo(95, 114); p.LineTo(120, 120);
            p.CurveTo(170, 118, 215, 95, 215, 60);
            p.CurveTo(215, 35, 180, 28, 150, 30);
            p.LineTo(20, 30);
            p.Close();
            return p;
        }
        static VectorPath MixMiterEdge()
        {   // a sharp Line -> Cubic node whose analytic miter ratio (1/sin(phi/2) = 3.95) stays under the 4x
            // limit: the exact tangent mitres, while the coarse first chord pushed the ratio over it and the
            // live stroke silently beveled where its outline conversion mitred
            var p = new VectorPath();
            p.MoveTo(126.1212f, 100.3214f);
            p.LineTo(120, 90);
            p.CurveTo(124.4999f, 92.5980f, 129.6f, 95.6f, 131.2f, 100.4f);
            p.LineTo(136, 108);
            p.LineTo(114, 109.2f);
            p.Close();
            return p;
        }
        static VectorPath Star(double cx, double cy, double ro, double ri)
        {
            var p = new VectorPath();
            for (int i = 0; i < 10; i++)
            {
                double r = i % 2 == 0 ? ro : ri, a = Math.PI / 2 + i * Math.PI / 5;
                var x = cx + r * Math.Cos(a); var y = cy - r * Math.Sin(a);
                if (i == 0) p.MoveTo((float)x, (float)y); else p.LineTo((float)x, (float)y);
            }
            p.Close(); return p;
        }

        static readonly (double X, double Y)[] Chevron90 = { P(50, 40), P(120, 110), P(190, 40) };
        static readonly (double X, double Y)[] ChevronAcute = { P(112, 30), P(120, 150), P(128, 30) };
        static readonly (double X, double Y)[] Shallow = { P(20, 90), P(120, 90), P(220, 78) };      // a 5.7 degree turn
        static readonly (double X, double Y)[] Reversal = { P(40, 90), P(200, 90), P(100, 90) };      // backtracks 180 degrees
        static readonly (double X, double Y)[] HLine = { P(40, 90), P(200, 90) };
        static readonly (double X, double Y)[] Diag = { P(40, 140), P(200, 40) };
        static readonly (double X, double Y)[] Square = { P(70, 40), P(170, 40), P(170, 140), P(70, 140) };

        /// <summary>Every case the harness measures. The style matrix is applied per fixture, not globally: caps only
        /// differ on open ends, joins only at vertices, so each fixture is paired with the styles that can show a defect.</summary>
        public static List<Case> Build()
        {
            var list = new List<Case>();
            void Add(string name, VectorPath p, double w, Cap cap, Join join, double miter = 4, float[] dash = null, int align = 0)
                => list.Add(new Case { Name = name, Path = p, Width = w, Cap = cap, Join = join, Miter = miter, Dash = dash, Align = align });

            foreach (double w in new[] { 3.0, 7.0, 15.0 })
            {
                string ws = w.ToString("0.#");
                // caps: a straight line has no joins, so every difference here is a cap
                foreach (var cap in new[] { Cap.Butt, Cap.Round, Cap.Square })
                {
                    Add($"hline w{ws} {cap}", Open(HLine), w, cap, Join.Miter);
                    Add($"diag w{ws} {cap}", Open(Diag), w, cap, Join.Miter);
                }
                // joins at three angles, with butt caps so the ends cannot hide anything
                foreach (var join in new[] { Join.Miter, Join.Round, Join.Bevel })
                {
                    Add($"chev90 w{ws} {join}", Open(Chevron90), w, Cap.Butt, join);
                    Add($"chevAcute w{ws} {join}", Open(ChevronAcute), w, Cap.Butt, join);
                    Add($"shallow w{ws} {join}", Open(Shallow), w, Cap.Butt, join);
                    Add($"reversal w{ws} {join}", Open(Reversal), w, Cap.Butt, join);
                    Add($"square w{ws} {join}", Closed(Square), w, Cap.Butt, join);
                    Add($"star w{ws} {join}", Star(120, 92, 70, 30), w, Cap.Butt, join);
                    Add($"scurve w{ws} {join}", SCurve(), w, Cap.Butt, join);
                    Add($"circle w{ws} {join}", CircleCubic(120, 90, 55), w, Cap.Butt, join);
                    Add($"fig8 w{ws} {join}", Fig8(), w, Cap.Butt, join);
                    Add($"mixjoin w{ws} {join}", JaggedToSmooth(), w, Cap.Butt, join);
                }
                // a Line -> Cubic node just inside the miter limit: a coarse chord used to flip miter into bevel
                Add($"mixmiter w{ws} miter/3.9", MixMiterEdge(), w, Cap.Butt, Join.Miter);
                // the miter limit decides where a sharp corner stops being sharp
                foreach (double ml in new[] { 1.0, 2.0, 10.0 })
                    Add($"chevAcute w{ws} miter/{ml:0.#}", Open(ChevronAcute), w, Cap.Butt, Join.Miter, ml);
                // dashes: an even pattern, an odd one (the SVG rule repeats it) and a dotted one
                Add($"hline w{ws} dash 12/6", Open(HLine), w, Cap.Butt, Join.Miter, 4, new[] { 12f, 6f });
                Add($"hline w{ws} dash 12/6 round", Open(HLine), w, Cap.Round, Join.Round, 4, new[] { 12f, 6f });
                Add($"hline w{ws} dash odd 10", Open(HLine), w, Cap.Butt, Join.Miter, 4, new[] { 10f });
                Add($"hline w{ws} dotted", Open(HLine), w, Cap.Round, Join.Round, 4, new[] { 0f, 10f });
                Add($"circle w{ws} dash 14/7", CircleCubic(120, 90, 55), w, Cap.Butt, Join.Miter, 4, new[] { 14f, 7f });
                // alignment: the same band clipped to one side of a closed shape (the demo's position selector)
                if (w == 7)
                    foreach (var join in new[] { Join.Miter, Join.Round, Join.Bevel })
                        foreach (int align in new[] { 1, 2 })
                        {
                            Add($"square w{ws} {join} align{align}", Closed(Square), w, Cap.Butt, join, 4, null, align);
                            Add($"circle w{ws} {join} align{align}", CircleCubic(120, 90, 55), w, Cap.Butt, join, 4, null, align);
                            Add($"star w{ws} {join} align{align}", Star(120, 92, 70, 30), w, Cap.Butt, join, 4, null, align);
                        }
            }
            return list;
        }

        // ------------------------------------------------------------------ exact flattening for the truth side
        /// <summary>Flattens a path into sub-polylines at a tolerance far below a pixel, so the truth's own
        /// flattening error cannot be what the comparison sees. Independent of the engine's flattener.</summary>
        public static List<(List<(double X, double Y)> Pts, bool Closed)> Flatten(VectorPath p, double tol = 1e-3)
        {
            var subs = new List<(List<(double X, double Y)>, bool)>();
            var cur = new List<(double, double)>();
            double cx = 0, cy = 0, sx = 0, sy = 0;
            int d = 0; var K = p.Kinds; var D = p.Data;
            for (int i = 0; i < K.Count; i++)
            {
                switch (K[i])
                {
                    case VectorPath.K.Move:
                        if (cur.Count > 0) subs.Add((cur, false));
                        cur = new List<(double, double)>();
                        cx = sx = D[d]; cy = sy = D[d + 1]; d += 2; cur.Add((cx, cy)); break;
                    case VectorPath.K.Line:
                        cx = D[d]; cy = D[d + 1]; d += 2; cur.Add((cx, cy)); break;
                    case VectorPath.K.Cubic:
                        {
                            double x1 = D[d], y1 = D[d + 1], x2 = D[d + 2], y2 = D[d + 3], x3 = D[d + 4], y3 = D[d + 5]; d += 6;
                            Cubic(cx, cy, x1, y1, x2, y2, x3, y3, tol, cur);
                            cx = x3; cy = y3; break;
                        }
                    case VectorPath.K.Close:
                        // a closed ring keeps its vertices: the truth's stroker walks the polyline cyclically, so
                        // overwriting the first point with the last would delete the closing edge
                        if (cur.Count > 2) subs.Add((cur, true));
                        else if (cur.Count > 0) subs.Add((cur, false));
                        cur = new List<(double, double)>(); cx = sx; cy = sy; break;
                }
            }
            if (cur.Count > 0) subs.Add((cur, false));
            return subs;
        }

        static void Cubic(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double tol, List<(double, double)> o)
        {   // de Casteljau subdivision until every piece is flatter than tol (a quadratic-error bound)
            double ax = 3 * (x1 - x0), bx = 3 * (x2 - x1), cx2 = x3 - x2;
            double ay = 3 * (y1 - y0), by = 3 * (y2 - y1), cy2 = y3 - y2;
            double ddx = Math.Max(Math.Abs(x0 - 2 * x1 + x2), Math.Abs(x1 - 2 * x2 + x3));
            double ddy = Math.Max(Math.Abs(y0 - 2 * y1 + y2), Math.Abs(y1 - 2 * y2 + y3));
            double dd = Math.Sqrt(ddx * ddx + ddy * ddy);
            int n = (int)Math.Ceiling(Math.Sqrt(0.75 * dd / Math.Max(tol, 1e-9)));
            n = Math.Clamp(n, 1, 4096);
            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n, u = 1 - t;
                o.Add((u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3,
                       u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3));
            }
        }

        /// <summary>Cuts the flattened polylines into dashes (the SVG rule: an odd pattern repeats, the pattern
        /// restarts at every sub-path). Truth and comparison both use this so the dash geometry is not in question.</summary>
        public static List<(List<(double X, double Y)> Pts, bool Closed)> Dash(List<(List<(double X, double Y)>, bool)> subs, float[] pattern, double offset)
        {
            var pat = new List<double>();
            foreach (var v in pattern) pat.Add(Math.Max(0, v));
            if ((pat.Count & 1) != 0) { int n = pat.Count; for (int i = 0; i < n; i++) pat.Add(pat[i]); }   // odd repeats
            var outp = new List<(List<(double X, double Y)>, bool)>();
            double total = 0; foreach (var v in pat) total += v;
            if (total <= 0) return outp;
            foreach (var (pts, closed) in subs)
            {
                int n = pts.Count; if (n < 2) continue;
                var segs = new List<((double X, double Y) a, (double X, double Y) b, double len)>();
                int count = closed ? n : n - 1;
                for (int i = 0; i < count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % n];
                    double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    if (len > 0) segs.Add((a, b, len));
                }
                double off = offset % total; if (off < 0) off += total;
                int di = 0; double rem = pat[0]; bool on = true;
                double walk = off; int guard = 0;
                while (walk > 0 && guard++ < 4 * pat.Count)
                {
                    if (walk >= rem) { walk -= rem; di = (di + 1) % pat.Count; rem = pat[di]; on = !on; }
                    else { rem -= walk; walk = 0; }
                }
                List<(double, double)> cur = on ? new List<(double, double)> { segs[0].a } : null;
                foreach (var (a, b, len) in segs)
                {
                    double t = 0, dx = b.X - a.X, dy = b.Y - a.Y;
                    while (len - t > rem)
                    {
                        t += rem;
                        var cut = (a.X + dx * t / len, a.Y + dy * t / len);
                        if (on) { cur.Add(cut); if (cur.Count >= 2) outp.Add((cur, false)); cur = null; }
                        else cur = new List<(double, double)> { cut };
                        on = !on; di = (di + 1) % pat.Count; rem = pat[di];
                        if (rem == 0 && on) { cur = new List<(double, double)> { cut }; }
                    }
                    rem -= len - t; t = len;
                    if (on) cur.Add(b);
                }
                if (on && cur != null && cur.Count >= 2) outp.Add((cur, false));
            }
            return outp;
        }
    }
}
