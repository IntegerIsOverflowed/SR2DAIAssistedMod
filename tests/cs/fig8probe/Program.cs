// fig8probe - prints the orientation of the region rings of the smooth figure-8 (CENTER_x0020_eight_x0020_smooth).
// Diagnoses the INSIDE ring of the outline (T107/T108): the inside band sums capsule pieces with the region rings,
// so the sign of each region ring (its winding) matters.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Sr2d64CSport;

static class Fig8Probe
{
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    static List<List<Pt2>> Flatten(string d, double unit)
    {
        var toks = new List<string>();
        foreach (Match t in Regex.Matches(d, @"[MLCZmlcz]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")) toks.Add(t.Value);
        var rings = new List<List<Pt2>>();
        List<Pt2> cur = null;
        Pt2 pos = new Pt2(0, 0), start = new Pt2(0, 0);
        char cmd = 'M';
        int i = 0;
        double N(int k) => double.Parse(toks[k], Ci) * unit;
        while (i < toks.Count)
        {
            string t = toks[i];
            if (t.Length == 1 && "MLCZmlcz".IndexOf(t[0]) >= 0) { cmd = t[0]; i++; if (cmd == 'z' || cmd == 'Z') { if (cur != null) { rings.Add(cur); cur = null; } pos = start; } continue; }
            switch (cmd)
            {
                case 'M': case 'm':
                    {
                        if (cur != null) rings.Add(cur);
                        Pt2 p = cmd == 'm' ? new Pt2(pos.X + N(i), pos.Y + N(i + 1)) : new Pt2(N(i), N(i + 1));
                        i += 2; cur = new List<Pt2> { p }; pos = start = p; cmd = cmd == 'm' ? 'l' : 'L';
                        break;
                    }
                case 'l': case 'L':
                    {
                        Pt2 p = cmd == 'l' ? new Pt2(pos.X + N(i), pos.Y + N(i + 1)) : new Pt2(N(i), N(i + 1));
                        i += 2; cur.Add(p); pos = p; break;
                    }
                case 'c': case 'C':
                    {
                        Pt2 c1 = cmd == 'c' ? new Pt2(pos.X + N(i), pos.Y + N(i + 1)) : new Pt2(N(i), N(i + 1));
                        Pt2 c2 = cmd == 'c' ? new Pt2(pos.X + N(i + 2), pos.Y + N(i + 3)) : new Pt2(N(i + 2), N(i + 3));
                        Pt2 e = cmd == 'c' ? new Pt2(pos.X + N(i + 4), pos.Y + N(i + 5)) : new Pt2(N(i + 4), N(i + 5));
                        i += 6;
                        for (int k = 1; k <= 64; k++)
                        {
                            double s = k / 64.0, u = 1 - s;
                            double x = u * u * u * pos.X + 3 * u * u * s * c1.X + 3 * u * s * s * c2.X + s * s * s * e.X;
                            double y = u * u * u * pos.Y + 3 * u * u * s * c1.Y + 3 * u * s * s * c2.Y + s * s * s * e.Y;
                            cur.Add(new Pt2(x, y));
                        }
                        pos = e; break;
                    }
                default: throw new Exception("unsupported " + cmd);
            }
        }
        if (cur != null) rings.Add(cur);
        return rings;
    }

    static double SignedArea(List<Pt2> r)
    {
        double a = 0;
        for (int j = 0; j < r.Count; j++) { var p = r[j]; var q = r[(j + 1) % r.Count]; a += p.X * q.Y - q.X * p.Y; }
        return a * 0.5;
    }

    static void Main(string[] args)
    {
        string svg = File.ReadAllText(args.Length > 0 ? args[0] : "/home/user/SR2D/demo/assets_builtin/outline_test_shapes.svg");
        var m = Regex.Match(svg, "id=\"CENTER_x0020_eight_x0020_smooth\"[^>]*\\sd=\"([^\"]*)\"");
        var raw = Flatten(m.Groups[1].Value, 0.01);
        Console.WriteLine("input rings: " + raw.Count);
        foreach (var r in raw) Console.WriteLine("  input ring pts " + r.Count + " signed area mm2 " + SignedArea(r).ToString("F2", Ci));
        var region = OutlineArrange.Region(raw);
        Console.WriteLine("region rings: " + region.Count);
        foreach (var r in region) Console.WriteLine("  region ring pts " + r.Count + " signed area mm2 " + SignedArea(r).ToString("F2", Ci));
        for (int k = 0; k < region.Count; k++)
        {
            var one = OutlineArrange.Band(new List<List<Pt2>> { region[k] }, 0, -5.8, 4.0, 1.0, null);
            var parts = new List<string>(); double t = 0;
            foreach (var r in one) { double a = SignedArea(r); t += a; parts.Add(a.ToString("F1", Ci) + "/" + r.Count); }
            Console.WriteLine("lobe " + k + " alone: net " + t.ToString("F1", Ci) + " [" + string.Join(", ", parts) + "]");
        }
        {
            var all = new List<List<Pt2>>();
            for (int k = 0; k < region.Count; k++) foreach (var r in OutlineArrange.Band(new List<List<Pt2>> { region[k] }, 0, -5.8, 4.0, 1.0, null)) all.Add(r);
            var u = OutlineArrange.Union(all, 1);
            double t = 0; var parts = new List<string>();
            foreach (var r in u) { double a = SignedArea(r); t += a; parts.Add(a.ToString("F1", Ci) + "/" + r.Count); }
            Console.WriteLine("raw union of both lobe bands (" + all.Count + " input rings): net " + t.ToString("F1", Ci) + " [" + string.Join(", ", parts) + "]");
        }
        // expected (raster, 0.04 mm, 14.59 px/mm): outside ring 1084 mm2, inside ring 915 mm2, region 1331.7 mm2
        foreach (var dd in new[] { new[] { 5.8, 0.0 }, new[] { 0.0, -5.8 } })
        {
            var b = OutlineArrange.Band(region, dd[0], dd[1], 4.0, 1.0, null);
            double tot = 0; var parts = new List<string>();
            foreach (var r in b) { double a = SignedArea(r); tot += a; parts.Add(a.ToString("F1", Ci) + "/" + r.Count); }
            Console.WriteLine("Band dOuter=" + dd[0] + " dInner=" + dd[1] + ": rings " + b.Count + " net area " + tot.ToString("F1", Ci) + "  [" + string.Join(", ", parts) + "]");
        }
    }
}
