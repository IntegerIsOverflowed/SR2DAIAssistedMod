// outlinecheck - stage 1 (planar arrangement) and stage 2 (mitred offset + union) on the cleaned 14-node tangled curve.
// Stage 1 expected (prototype, shapely): 5 bounded faces, 3 kept, area 823.01 mm2.
// Stage 2 targets (Corel, before Bezier fit): outside 1 mm = 1 path; inside 1 mm = 3 subpaths.
// Ground truth: distance to the region boundary on a 0.1 mm grid (round offset), ignoring a +-0.03 mm band.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Sr2d64CSport;

static class OutlineCheck
{
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    static string F(double v) => v.ToString("F3", Ci);

    // Flattens an absolute/relative M, L, C, Z path into a dense ring (mm). Cubic spacing as in the prototype.
    static List<Pt2> Flatten(string d, double sx, double sy, double spacing)
    {
        var toks = new List<string>();
        foreach (Match t in Regex.Matches(d, @"[MLCZmlcz]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")) toks.Add(t.Value);
        var pts = new List<Pt2>();
        Pt2 cur = new Pt2(), start = new Pt2();
        char cmd = 'M';
        int i = 0;
        Pt2 Read()
        {
            var p = new Pt2(double.Parse(toks[i], Ci) * sx, double.Parse(toks[i + 1], Ci) * sy);
            i += 2;
            return p;
        }
        while (i < toks.Count)
        {
            string t = toks[i];
            if (t.Length == 1 && "MLCZmlcz".IndexOf(t[0]) >= 0)
            {
                cmd = t[0];
                i++;
                if (cmd == 'Z' || cmd == 'z') cur = start;
                continue;
            }
            bool rel = char.IsLower(cmd);
            char up = char.ToUpperInvariant(cmd);
            if (up == 'M')
            {
                Pt2 p = Read();
                cur = rel ? cur + p : p;
                start = cur;
                pts.Add(cur);
                cmd = rel ? 'l' : 'L';
            }
            else if (up == 'L')
            {
                Pt2 p = Read();
                cur = rel ? cur + p : p;
                pts.Add(cur);
            }
            else if (up == 'C')
            {
                Pt2 c1 = Read(), c2 = Read(), p3 = Read();
                if (rel) { c1 = cur + c1; c2 = cur + c2; p3 = cur + p3; }
                double ctrl = (c1 - cur).Length + (c2 - c1).Length + (p3 - c2).Length;
                int n = Math.Max(8, (int)(ctrl / spacing));
                for (int k = 1; k <= n; k++)
                {
                    double s = k / (double)n, m = 1 - s;
                    pts.Add(new Pt2(m * m * m * cur.X + 3 * m * m * s * c1.X + 3 * m * s * s * c2.X + s * s * s * p3.X,
                                    m * m * m * cur.Y + 3 * m * m * s * c1.Y + 3 * m * s * s * c2.Y + s * s * s * p3.Y));
                }
                cur = p3;
            }
            else i++;
        }
        if (pts.Count > 1 && Math.Abs(pts[0].X - pts[pts.Count - 1].X) < 1e-9 && Math.Abs(pts[0].Y - pts[pts.Count - 1].Y) < 1e-9)
            pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    static void Bounds(IList<List<Pt2>> rings, out double x0, out double y0, out double x1, out double y1)
    {
        x0 = y0 = double.MaxValue; x1 = y1 = double.MinValue;
        foreach (var r in rings)
            foreach (var p in r)
            {
                x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
                y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
            }
    }

    static double SignedArea(IList<List<Pt2>> rings)
    {
        double a = 0;
        foreach (var r in rings)
            for (int k = 0; k < r.Count; k++)
            {
                Pt2 p = r[k], q = r[(k + 1) % r.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
        return a * 0.5;
    }

    static int Vertices(IList<List<Pt2>> rings)
    {
        int n = 0;
        foreach (var r in rings) n += r.Count;
        return n;
    }

    // vertices whose direction changes by more than 15 degrees
    static int Corners(IList<List<Pt2>> rings)
    {
        int c = 0;
        foreach (var r in rings)
        {
            int n = r.Count;
            for (int k = 0; k < n; k++)
            {
                Pt2 a = r[(k - 1 + n) % n], b = r[k], e = r[(k + 1) % n];
                double ux = b.X - a.X, uy = b.Y - a.Y, vx = e.X - b.X, vy = e.Y - b.Y;
                double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
                if (lu < 1e-12 || lv < 1e-12) continue;
                double cos = (ux * vx + uy * vy) / (lu * lv);
                if (cos < Math.Cos(15 * Math.PI / 180)) c++;
            }
        }
        return c;
    }

    // segments of rings, for distance queries and winding
    sealed class Segs
    {
        public readonly double[] Ax, Ay, Bx, By;
        public readonly int N;
        public Segs(IList<List<Pt2>> rings)
        {
            int n = 0;
            foreach (var r in rings) n += r.Count;
            N = n;
            Ax = new double[n]; Ay = new double[n]; Bx = new double[n]; By = new double[n];
            int k = 0;
            foreach (var r in rings)
            {
                int m = r.Count;
                for (int i = 0; i < m; i++)
                {
                    Pt2 a = r[i], b = r[(i + 1) % m];
                    Ax[k] = a.X; Ay[k] = a.Y; Bx[k] = b.X; By[k] = b.Y;
                    k++;
                }
            }
        }
    }

    // winding by horizontal row bins (0.1 mm): only segments spanning the point's row are visited
    sealed class RowIndex
    {
        readonly Segs s;
        readonly double y0, bin;
        readonly int[][] rows;
        public RowIndex(Segs s, double y0, double y1, double bin)
        {
            this.s = s; this.y0 = y0; this.bin = bin;
            int nr = (int)Math.Ceiling((y1 - y0) / bin) + 1;
            var lists = new List<int>[nr];
            for (int r = 0; r < nr; r++) lists[r] = new List<int>();
            for (int i = 0; i < s.N; i++)
            {
                int r0 = Clamp((int)Math.Floor((Math.Min(s.Ay[i], s.By[i]) - y0) / bin), nr);
                int r1 = Clamp((int)Math.Floor((Math.Max(s.Ay[i], s.By[i]) - y0) / bin), nr);
                for (int r = r0; r <= r1; r++) lists[r].Add(i);
            }
            rows = new int[nr][];
            for (int r = 0; r < nr; r++) rows[r] = lists[r].ToArray();
        }
        static int Clamp(int v, int nr) => v < 0 ? 0 : (v >= nr ? nr - 1 : v);
        public int Winding(Pt2 p)
        {
            int r = (int)Math.Floor((p.Y - y0) / bin);
            if (r < 0 || r >= rows.Length) return 0;
            int wn = 0;
            foreach (int i in rows[r])
            {
                double side = (s.Bx[i] - s.Ax[i]) * (p.Y - s.Ay[i]) - (p.X - s.Ax[i]) * (s.By[i] - s.Ay[i]);
                if (s.Ay[i] <= p.Y)
                {
                    if (s.By[i] > p.Y && side > 0) wn++;
                }
                else if (s.By[i] <= p.Y && side < 0) wn--;
            }
            return wn;
        }
    }

    // distance to segments on a 0.5 mm cell grid
    sealed class SegGrid
    {
        readonly Segs s;
        readonly double x0, y0, cell;
        readonly Dictionary<long, List<int>> map = new Dictionary<long, List<int>>();
        public SegGrid(Segs s, double cell)
        {
            this.s = s; this.cell = cell;
            x0 = double.MaxValue; y0 = double.MaxValue;
            for (int i = 0; i < s.N; i++)
            {
                x0 = Math.Min(x0, Math.Min(s.Ax[i], s.Bx[i]));
                y0 = Math.Min(y0, Math.Min(s.Ay[i], s.By[i]));
            }
            for (int i = 0; i < s.N; i++)
            {
                int gx0 = (int)Math.Floor((Math.Min(s.Ax[i], s.Bx[i]) - x0) / cell), gx1 = (int)Math.Floor((Math.Max(s.Ax[i], s.Bx[i]) - x0) / cell);
                int gy0 = (int)Math.Floor((Math.Min(s.Ay[i], s.By[i]) - y0) / cell), gy1 = (int)Math.Floor((Math.Max(s.Ay[i], s.By[i]) - y0) / cell);
                for (int gx = gx0; gx <= gx1; gx++)
                    for (int gy = gy0; gy <= gy1; gy++)
                    {
                        long key = Key(gx, gy);
                        if (!map.TryGetValue(key, out var list)) { list = new List<int>(); map[key] = list; }
                        list.Add(i);
                    }
            }
        }
        static long Key(int gx, int gy) => ((long)gx << 32) ^ (uint)gy;
        public double Dist(Pt2 p, double rq)
        {
            int gx0 = (int)Math.Floor((p.X - rq - x0) / cell), gx1 = (int)Math.Floor((p.X + rq - x0) / cell);
            int gy0 = (int)Math.Floor((p.Y - rq - y0) / cell), gy1 = (int)Math.Floor((p.Y + rq - y0) / cell);
            double best = double.MaxValue;
            for (int gx = gx0; gx <= gx1; gx++)
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    if (!map.TryGetValue(Key(gx, gy), out var list)) continue;
                    foreach (int i in list)
                    {
                        double dx = s.Bx[i] - s.Ax[i], dy = s.By[i] - s.Ay[i];
                        double len2 = dx * dx + dy * dy;
                        double t = len2 > 0 ? ((p.X - s.Ax[i]) * dx + (p.Y - s.Ay[i]) * dy) / len2 : 0;
                        t = Math.Max(0, Math.Min(1, t));
                        double ex = s.Ax[i] + t * dx - p.X, ey = s.Ay[i] + t * dy - p.Y;
                        best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
                    }
                }
            return best;
        }
    }

    static void WriteSvg(string path, List<List<Pt2>> region, List<List<Pt2>> outRings)
    {
        double x0, y0, x1, y1;
        Bounds(new List<List<Pt2>>(region) { outRings.Count > 0 ? outRings[0] : region[0] }, out x0, out y0, out x1, out y1);
        foreach (var r in outRings)
            foreach (var p in r) { x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y); }
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"");
        sb.Append(F(x0 - 1) + " " + F(-(y1 + 1)) + " " + F(x1 - x0 + 2) + " " + F(y1 - y0 + 2));
        sb.Append("\" width=\"800\" height=\"" + F(800.0 * (y1 - y0 + 2) / (x1 - x0 + 2)) + "\">\n<rect x=\"" + F(x0 - 1) + "\" y=\"" + F(y0 - 1) + "\" width=\"" + F(x1 - x0 + 2) + "\" height=\"" + F(y1 - y0 + 2) + "\" fill=\"#cccccc\" transform=\"scale(1,-1)\"/>\n");
        sb.Append("<g transform=\"scale(1,-1)\">\n");
        sb.Append("<path fill=\"#000000\" fill-rule=\"nonzero\" d=\"");
        foreach (var r in outRings) AppendRing(sb, r);
        sb.Append("\"/>\n");
        sb.Append("<path fill=\"none\" stroke=\"#d00000\" stroke-width=\"0.03\" d=\"");
        foreach (var r in region) AppendRing(sb, r);
        sb.Append("\"/>\n</g>\n</svg>\n");
        File.WriteAllText(path, sb.ToString());
    }

    static void AppendRing(StringBuilder sb, List<Pt2> r)
    {
        for (int k = 0; k < r.Count; k++)
            sb.Append((k == 0 ? "M" : "L") + F(r[k].X) + " " + F(r[k].Y) + " ");
        sb.Append("Z ");
    }

    static void Verify(string name, double d, List<List<Pt2>> region, RowIndex regRows, SegGrid regGrid,
                       List<List<Pt2>> outRings, string svgPath)
    {
        double ad = Math.Abs(d), rq = ad + 0.1, pad = ad + 0.3, step = 0.1;
        double rx0, ry0, rx1, ry1;
        Bounds(region, out rx0, out ry0, out rx1, out ry1);
        Console.WriteLine();
        Console.WriteLine("== " + name + " ==");
        Console.WriteLine("result: {0} rings, {1} vertices, {2} corners (>15 deg), area {3} mm2",
            outRings.Count, Vertices(outRings), Corners(outRings), F(Math.Abs(SignedArea(outRings))));
        foreach (var r in outRings) Console.WriteLine("   ring: {0} vertices, area {1} mm2", r.Count, F(SignedArea(new List<List<Pt2>> { r })));

        if (outRings.Count == 0) return;
        var outSeg = new Segs(outRings);
        double ox0, oy0, ox1, oy1;
        Bounds(outRings, out ox0, out oy0, out ox1, out oy1);
        var outRows = new RowIndex(outSeg, oy0 - 1, oy1 + 1, 0.1);

        long truthCount = 0, mismatch = 0, missing = 0, extra = 0, band = 0;
        double mx0 = double.MaxValue, my0 = double.MaxValue, mx1 = double.MinValue, my1 = double.MinValue;
        var sample = new List<string>();
        for (double x = rx0 - pad; x <= rx1 + pad; x += step)
            for (double y = ry0 - pad; y <= ry1 + pad; y += step)
            {
                var p = new Pt2(x, y);
                bool inR = regRows.Winding(p) != 0;
                double dist = regGrid.Dist(p, rq);
                if (dist > rq) dist = rq + 1;
                bool truth = d > 0 ? (inR || dist <= ad) : (inR && dist >= ad);
                bool res = outRows.Winding(p) > 0;
                if (truth) truthCount++;
                if (Math.Abs(dist - ad) < 0.03) { band++; continue; }
                if (truth == res) continue;
                mismatch++;
                if (truth) missing++; else extra++;
                mx0 = Math.Min(mx0, x); my0 = Math.Min(my0, y); mx1 = Math.Max(mx1, x); my1 = Math.Max(my1, y);
                if (sample.Count < 8) sample.Add("(" + F(x) + ", " + F(y) + ") " + (truth ? "missing" : "extra"));
            }
        Console.WriteLine("truth area (grid) {0} mm2", F(truthCount * step * step));
        Console.WriteLine("mismatches {0} (missing {1}, extra {2}) outside the 0.03 mm band; band points {3}", mismatch, missing, extra, band);
        if (mismatch > 0)
        {
            Console.WriteLine("mismatch bbox: x {0}..{1}, y {2}..{3}", F(mx0), F(mx1), F(my0), F(my1));
            foreach (var s in sample) Console.WriteLine("   " + s);
        }
        WriteSvg(svgPath, region, outRings);
        Console.WriteLine("svg: " + svgPath);
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "circle")
        {
            var c = new List<Pt2>();
            for (int i = 0; i < 720; i++) { double a = 2 * Math.PI * i / 720; c.Add(new Pt2(2 * Math.Cos(a), 2 * Math.Sin(a))); }
            var reg = OutlineArrange.Region(new List<List<Pt2>> { c });
            Console.WriteLine("circle region: {0} rings, area {1}", reg.Count, F(SignedArea(reg)));
            var pieces = new List<List<Pt2>>();
            foreach (var r in reg) { var o = OutlineArrange.OffsetRing(r, 1.0, 4.0); pieces.Add(o); }
            foreach (var r in reg) { var q = new List<Pt2>(r); q.Reverse(); pieces.Add(q); }
            var g = OutlineArrange.Arrange(pieces);
            foreach (var f in g.Faces) Console.WriteLine("face area {0} winding {1} vertices {2}", F(f.Area), f.Winding, f.Ring.Count);
            Console.WriteLine("ring sizes {0} {1}; first pts offset {2},{3} reversed {4},{5}", pieces[0].Count, pieces[1].Count, F(pieces[0][0].X), F(pieces[0][0].Y), F(pieces[1][0].X), F(pieces[1][0].Y));
            var outRing = OutlineArrange.Band(reg, 1.0, 0);
            Console.WriteLine("outside ring: {0} rings, signed areas:", outRing.Count);
            foreach (var r in outRing) Console.WriteLine("   {0} vertices, area {1}", r.Count, F(SignedArea(new List<List<Pt2>> { r })));
            var inRing = OutlineArrange.Band(reg, 0, -1.0);
            Console.WriteLine("inside ring: {0} rings", inRing.Count);
            foreach (var r in inRing) Console.WriteLine("   {0} vertices, area {1}", r.Count, F(SignedArea(new List<List<Pt2>> { r })));
            return 0;
        }
        if (args.Length > 0 && args[0] == "polyfile")
        {
            // polyfile in out dOuter dInner [miter]: rings in a text file (one ring per block, "x y" lines, mm); writes the band rings
            var rings = new List<List<Pt2>>(); var cur = new List<Pt2>();
            foreach (var line in File.ReadAllLines(args[1]))
            {
                if (line.Trim().Length == 0) { if (cur.Count > 0) rings.Add(cur); cur = new List<Pt2>(); continue; }
                var xy = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                cur.Add(new Pt2(double.Parse(xy[0], Ci), double.Parse(xy[1], Ci)));
            }
            if (cur.Count > 0) rings.Add(cur);
            double dO = double.Parse(args[3], Ci), dI = double.Parse(args[4], Ci);
            double miter = args.Length > 5 ? double.Parse(args[5], Ci) : 4.0;
            var reg = OutlineArrange.Region(rings);
            var band = OutlineArrange.Band(reg, dO, dI, miter);
            var sb = new StringBuilder();
            sb.AppendLine("# region " + reg.Count + " rings");
            foreach (var r in reg) { foreach (var q in r) sb.AppendLine(F(q.X) + " " + F(q.Y)); sb.AppendLine(); }
            sb.AppendLine("# band " + band.Count + " rings");
            foreach (var r in band) { foreach (var q in r) sb.AppendLine(F(q.X) + " " + F(q.Y)); sb.AppendLine(); }
            File.WriteAllText(args[2], sb.ToString());
            Console.WriteLine("region rings " + reg.Count + ", band rings " + band.Count);
            foreach (var r in band) Console.WriteLine("  band ring " + r.Count + " pts, signed area " + F(SignedArea(new List<List<Pt2>> { r })));
            return 0;
        }
        if (args.Length > 0 && args[0] == "rect")
        {
            var rect = new List<Pt2> { new Pt2(0, 0), new Pt2(10, 0), new Pt2(10, 4), new Pt2(0, 4) };
            var rg = OutlineArrange.Region(new List<List<Pt2>> { rect });
            foreach (var r in OutlineArrange.Band(rg, 0, -1.0))
                Console.WriteLine("inner band ring: " + string.Join(" ", r.ConvertAll(q => "(" + F(q.X) + "," + F(q.Y) + ")")));
            foreach (var r in OutlineArrange.Band(rg, 1.0, 0))
                Console.WriteLine("outer band ring: " + string.Join(" ", r.ConvertAll(q => "(" + F(q.X) + "," + F(q.Y) + ")")));
            return 0;
        }
        if (args.Length > 0 && args[0] == "jagged")
        {
            // eight jagged: the figure-eight polygon from outline_test_shapes.svg (CENTER/OUTSIDE/INSIDE share it), in mm
            string[] raw = { "1139.15,7909.22", "4394.85,7909.22", "4394.85,5955.8", "1139.15,4653.52", "1139.15,2700.1", "4394.85,2700.1", "4394.85,4653.52", "1139.15,5955.8" };
            var jr = new List<Pt2>();
            foreach (var t in raw) { var xy = t.Split(','); jr.Add(new Pt2(double.Parse(xy[0], Ci) * 0.01, double.Parse(xy[1], Ci) * 0.01)); }
            var reg = OutlineArrange.Region(new List<List<Pt2>> { jr });
            Console.WriteLine("jagged region: {0} rings, area {1}", reg.Count, F(SignedArea(reg)));
            var jfaces = OutlineArrange.Faces(jr);
            foreach (var f in jfaces) Console.WriteLine("  face area {0} kept {1}", F(f.Area), f.Kept);
            var outR = OutlineArrange.Band(reg, 1.0, 0);
            var inR = OutlineArrange.Band(reg, 0, -1.0);
            var cenR = OutlineArrange.Band(reg, 1.0, -1.0);
            Console.WriteLine("outside: {0} rings, area {1}", outR.Count, F(NonZeroArea(outR)));
            Console.WriteLine("inside : {0} rings, area {1}", inR.Count, F(NonZeroArea(inR)));
            Console.WriteLine("centre : {0} rings, area {1}", cenR.Count, F(NonZeroArea(cenR)));
            // brute-force truth on a 0.02 mm grid
            double x0 = -1.5, x1 = 46, y0 = -1.5, y1 = 80;
            double tO = 0, tI = 0, tC = 0, mO = 0, mI = 0, mC = 0, h = 0.02;
            for (double x = x0; x < x1; x += h)
                for (double y = y0; y < y1; y += h)
                {
                    var p = new Pt2(x, y);
                    bool inside = NonZeroWind(reg, p);
                    double dB = double.MaxValue;
                    foreach (var r in reg) for (int i = 0; i < r.Count; i++) dB = Math.Min(dB, SegDist(p, r[i], r[(i + 1) % r.Count]));
                    bool wantO = !inside && dB <= 1.0, wantI = inside && dB <= 1.0, wantC = dB <= 1.0;
                    if (wantO) tO += h * h; if (wantI) tI += h * h; if (wantC) tC += h * h;
                    bool gO = NonZeroWind(outR, p), gI = NonZeroWind(inR, p), gC = NonZeroWind(cenR, p);
                    if (gO != wantO) mO += h * h;
                    if (gI != wantI) mI += h * h;
                    if (gC != wantC) mC += h * h;
                }
            Console.WriteLine("truth area: outside {0} inside {1} centre {2}", F(tO), F(tI), F(tC));
            Console.WriteLine("mismatch area: outside {0} inside {1} centre {2}", F(mO), F(mI), F(mC));
            WriteSvg("/home/user/renders/jagged_outside.svg", reg, outR);
            WriteSvg("/home/user/renders/jagged_inside.svg", reg, inR);
            WriteSvg("/home/user/renders/jagged_centre.svg", reg, cenR);
            FitReport("jagged outside", outR, 0.04, "/home/user/renders/jagged_outside_fit.svg");
            FitReport("jagged centre", cenR, 0.04, "/home/user/renders/jagged_centre_fit.svg");
            foreach (var r in outR) Console.WriteLine("  out ring {0} pts area {1}", r.Count, F(SignedArea(new List<List<Pt2>> { r })));
            if (outR.Count > 0)
            {
                var segsDbg = OutlineFit.FitLoop(outR[0], 0.04, 0.04);
                foreach (var sg in segsDbg) Console.WriteLine("  seg P0({0},{1}) C1({2},{3}) C2({4},{5}) P1({6},{7})", F(sg.P0.X), F(sg.P0.Y), F(sg.C1.X), F(sg.C1.Y), F(sg.C2.X), F(sg.C2.Y), F(sg.P1.X), F(sg.P1.Y));
            }
            foreach (var r in outR) Console.WriteLine("  pts: " + string.Join(" ", r.ConvertAll(q => "(" + F(q.X) + "," + F(q.Y) + ")")));
            return 0;
        }
        string svg = args.Length > 0 ? args[0] : "/home/user/proto/tangled_14nodes.svg";
        var text = File.ReadAllText(svg);
        var m = Regex.Match(text, "<path[^>]*?\\sd=\"([^\"]*)\"");
        if (!m.Success) { Console.WriteLine("no path"); return 1; }
        var ring = Flatten(m.Groups[1].Value, 37.9856 / 413.25, 38.7507 / 421.57, 0.01);

        // stage 1
        var faces = OutlineArrange.Faces(ring);
        double keptArea = 0;
        int kept = 0;
        foreach (var f in faces)
        {
            if (f.Kept) { kept++; keptArea += f.Area; }
        }
        Console.WriteLine("stage 1: ring {0} vertices; bounded faces {1}, kept {2}, kept area {3} mm2 (prototype: 5, 3, 823.01)",
            ring.Count, faces.Count, kept, F(keptArea));

        // stage 2
        var region = OutlineArrange.Region(new List<List<Pt2>> { ring });
        double bx0, by0, bx1, by1;
        Bounds(region, out bx0, out by0, out bx1, out by1);
        Console.WriteLine("region boundary: {0} rings, {1} vertices, area {2} mm2", region.Count, Vertices(region), F(SignedArea(region)));

        var regSeg = new Segs(region);
        var regRows = new RowIndex(regSeg, by0 - 1, by1 + 1, 0.1);
        var regGrid = new SegGrid(regSeg, 0.5);
        Directory.CreateDirectory("/home/user/renders");

        var outRingsOut = Tidy(OutlineArrange.Union(OffsetAll(region, 1.0), 1));
        Verify("outside 1 mm (miter, limit 4)", 1.0, region, regRows, regGrid, outRingsOut, "/home/user/renders/cs_stage2_outside_1mm.svg");

        var outRingsIn = Tidy(OutlineArrange.Union(OffsetAll(region, -1.0), 1));
        Verify("inside 1 mm (miter, limit 4)", -1.0, region, regRows, regGrid, outRingsIn, "/home/user/renders/cs_stage2_inside_1mm.svg");
        Diagnose("inside result", outRingsIn);

        // stage 3: Bezier fit, node counts and fit error
        double tol = args.Length > 1 ? double.Parse(args[1], Ci) : 0.04;
        Console.WriteLine();
        Console.WriteLine("== stage 3 Bezier fit, tolerance {0} mm ==", F(tol));
        FitReport("outside 1 mm (Corel: 37 nodes, 1 path)", outRingsOut, tol, "/home/user/renders/cs_stage3_outside_1mm_bezier.svg");
        FitReport("inside 1 mm (Corel: 45 nodes, 3 subpaths)", outRingsIn, tol, "/home/user/renders/cs_stage3_inside_1mm_bezier.svg");
        return 0;
    }

    static double SegDist(Pt2 p, Pt2 a, Pt2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
        double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
        return Math.Sqrt(ex * ex + ey * ey);
    }
    static bool NonZeroWind(List<List<Pt2>> rings, Pt2 p)
    {
        int w = 0;
        foreach (var r in rings) w += OutlineArrange.Winding(r, p);
        return w != 0;
    }
    // area of the nonzero-winding set of rings, by signed-area sum (rings are disjoint pieces with holes as CW)
    static double NonZeroArea(List<List<Pt2>> rings)
    {
        double a = 0;
        foreach (var r in rings) a += SignedArea(new List<List<Pt2>> { r });
        return a;
    }

    static void Diagnose(string name, List<List<Pt2>> rings)
    {
        Console.WriteLine("-- sharpest turns: " + name);
        var list = new List<string>();
        for (int ri = 0; ri < rings.Count; ri++)
        {
            var r = rings[ri];
            int n = r.Count;
            for (int k = 0; k < n; k++)
            {
                Pt2 a = r[(k - 1 + n) % n], b = r[k], e = r[(k + 1) % n];
                double ux = b.X - a.X, uy = b.Y - a.Y, vx = e.X - b.X, vy = e.Y - b.Y;
                double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
                if (lu < 1e-12 || lv < 1e-12) continue;
                double cos = (ux * vx + uy * vy) / (lu * lv);
                double ang = Math.Acos(Math.Max(-1, Math.Min(1, cos))) * 180 / Math.PI;
                if (ang > 30) list.Add(F(ang).PadLeft(8) + " deg at (" + F(b.X) + ", " + F(b.Y) + ") ring " + ri + " len " + F(lu) + "/" + F(lv));
            }
        }
        list.Sort((x, y) => string.CompareOrdinal(y, x));
        Console.WriteLine("   turns over 30 deg: " + list.Count);
        for (int i = 0; i < Math.Min(12, list.Count); i++) Console.WriteLine("   " + list[i]);
    }

    // clean pinch spikes, then drop rings under 1 mm2 (slivers)
    static List<List<Pt2>> Tidy(List<List<Pt2>> rings)
    {
        var cleaned = new List<List<Pt2>>();
        foreach (var r in rings)
        {
            var c = OutlineArrange.CleanRing(r, 0.02);
            if (c.Count >= 3) cleaned.Add(c);
        }
        var kept = OutlineArrange.DropSmall(cleaned, 1.0);
        Console.WriteLine("tidy: {0} rings -> {1} after drop of rings under 1 mm2", rings.Count, kept.Count);
        return kept;
    }

    static void FitReport(string name, List<List<Pt2>> rings, double tol, string svgPath)
    {
        int nodes = 0;
        double errRing = 0, errFit = 0;
        var fitted = new List<Pt2>();
        var segsPerRing = new List<string>();
        var fitAll = new List<List<OutlineFit.Seg>>();
        foreach (var r in rings)
        {
            var segs = OutlineFit.FitLoop(r, 0.05, tol);
            fitAll.Add(segs);
            nodes += segs.Count;
            segsPerRing.Add(segs.Count.ToString(Ci));
            foreach (var s in segs)
                for (int k = 0; k < 200; k++)
                {
                    double t = k / 200.0, m = 1 - t;
                    // a straight run has no controls: sample the chord
                    Pt2 c1 = s.Cub ? s.C1 : s.P0 + (s.P1 - s.P0) * (1.0 / 3);
                    Pt2 c2 = s.Cub ? s.C2 : s.P0 + (s.P1 - s.P0) * (2.0 / 3);
                    fitted.Add(new Pt2(m * m * m * s.P0.X + 3 * m * m * t * c1.X + 3 * m * t * t * c2.X + t * t * t * s.P1.X,
                                       m * m * m * s.P0.Y + 3 * m * m * t * c1.Y + 3 * m * t * t * c2.Y + t * t * t * s.P1.Y));
                }
        }
        // one-sided errors: ring vertices to the fitted polyline, and fitted samples to the ring polyline (edges, not vertices)
        var ringSegs = new List<(Pt2, Pt2)>();
        foreach (var r in rings)
            for (int k = 0; k < r.Count; k++) ringSegs.Add((r[k], r[(k + 1) % r.Count]));
        var fitSegs = new List<(Pt2, Pt2)>();
        foreach (var r in rings) { }
        var fitPts = fitted;
        foreach (var r in rings)
            foreach (var p in r)
                errRing = Math.Max(errRing, NearestDist(p, fitPts));
        Pt2 worst = default;
        foreach (var f in fitPts)
        {
            double e = NearestSegDist(f, ringSegs);
            if (e > errFit) { errFit = e; worst = f; }
        }
        Console.WriteLine("   worst fit point ({0}, {1})", F(worst.X), F(worst.Y));
        Console.WriteLine("{0}: {1} nodes in {2} path(s) [{3}]", name, nodes, rings.Count, string.Join(", ", segsPerRing));
        WriteBezSvg(svgPath, fitAll);
        Console.WriteLine("   max error ring->fit {0} mm, fit->ring {1} mm", F(errRing), F(errFit));
    }

    static void WriteBezSvg(string path, List<List<OutlineFit.Seg>> loops)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var loop in loops)
            foreach (var s in loop)
                foreach (var p in new[] { s.P0, s.P1 })
                {
                    x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
                }
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"" + F(x0 - 1) + " " + F(-(y1 + 1)) + " " + F(x1 - x0 + 2) + " " + F(y1 - y0 + 2) + "\" width=\"800\" height=\"" + F(800.0 * (y1 - y0 + 2) / (x1 - x0 + 2)) + "\">\n");
        sb.Append("<rect x=\"" + F(x0 - 1) + "\" y=\"" + F(y0 - 1) + "\" width=\"" + F(x1 - x0 + 2) + "\" height=\"" + F(y1 - y0 + 2) + "\" fill=\"#cccccc\" transform=\"scale(1,-1)\"/>\n");
        sb.Append("<path fill=\"#000000\" fill-rule=\"nonzero\" transform=\"scale(1,-1)\" d=\"");
        foreach (var loop in loops)
        {
            if (loop.Count == 0) continue;
            sb.Append("M" + F(loop[0].P0.X) + " " + F(loop[0].P0.Y) + " ");
            foreach (var s in loop)
                sb.Append(s.Cub
                    ? "C" + F(s.C1.X) + " " + F(s.C1.Y) + " " + F(s.C2.X) + " " + F(s.C2.Y) + " " + F(s.P1.X) + " " + F(s.P1.Y) + " "
                    : "L" + F(s.P1.X) + " " + F(s.P1.Y) + " ");
            sb.Append("Z ");
        }
        sb.Append("\"/>\n</svg>\n");
        File.WriteAllText(path, sb.ToString());
    }

    static double NearestSegDist(Pt2 p, List<(Pt2, Pt2)> segs)
    {
        double best = double.MaxValue;
        foreach (var (a, b) in segs)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2 : 0;
            t = Math.Max(0, Math.Min(1, t));
            double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
            double d2 = ex * ex + ey * ey;
            if (d2 < best) best = d2;
        }
        return Math.Sqrt(best);
    }

    static double NearestDist(Pt2 p, List<Pt2> pts)
    {
        double best = double.MaxValue;
        foreach (var q in pts)
        {
            double dx = q.X - p.X, dy = q.Y - p.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 < best) best = d2;
        }
        return Math.Sqrt(best);
    }

    static List<List<Pt2>> OffsetAll(List<List<Pt2>> region, double d)
    {
        var offs = new List<List<Pt2>>();
        foreach (var r in region) offs.Add(OutlineArrange.OffsetRing(r, d, 4.0));
        return offs;
    }
}
