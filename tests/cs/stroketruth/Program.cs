// Stroke truth harness: renders each fixture with the engine's two strokers and with GDI+ / WPF, and measures all
// of them against an analytic truth built from the geometric definition of the stroke.
//
//   dotnet run --project tests/cs/stroketruth -c Release -- <outDir> [--only <substring>] [--images] [--outline] [--no-wpf]
//
// --outline adds the fourth column: the stroke converted to a filled shape (VectorOffset.Outline, what the demo's
// "Outline Convert To Object" and the SVG bake call), filled and compared with the same truth.
// Exit code: the number of cases whose worst column fails the gate (0 = all columns within the gate).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Sr2d64CSport
{
    internal static class Program
    {
        // The gate. Two references that agree with each other and with the analytic truth differ by a few percent of
        // soft coverage on a 3 px line purely because antialiasing puts the edge phase differently (A: 6.98 %, WPF
        // 6.55 %, both with zero hard-mismatching pixels), so a share threshold alone would flag every case as broken.
        // A real geometry defect shows as hard pixels AND distance: a pixel whose coverage differs by more than half,
        // measured against the other picture's edge. Fail = any hard pixel further than 1.5 px from the other edge,
        // or more than 0.5 % of the truth's ink pixels hard-mismatching at all.
        const double GateDev = 1.5, GateStrongFrac = 0.005;

        [STAThread]
        static int Main(string[] args)
        {
            string outDir = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : Path.Combine(Path.GetTempPath(), "stroketruth");
            Directory.CreateDirectory(outDir);
            string only = null;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--only") only = args[i + 1];
            bool images = args.Contains("--images"), outlineMode = args.Contains("--outline"), wpf = !args.Contains("--no-wpf");
            string dump = null, webDir = null, emitDir = null, rowDiff = null;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--dump") dump = args[i + 1];
                else if (args[i] == "--web") webDir = args[i + 1];
                else if (args[i] == "--emit") emitDir = args[i + 1];
                else if (args[i] == "--rowdiff") rowDiff = args[i + 1];
                else if (args[i] == "--sub") Truth.Sub = int.Parse(args[i + 1]);   // truth subgrid (must stay even)
            Refs.GdiMiterHalf = args.Contains("--gdi-miter-half");
            Refs.WpfMiterHalf = args.Contains("--wpf-miter-half");             // A/B: WPF's half-thickness miter definition
            VectorRender.LegacyJoinSideTest = args.Contains("--legacy-join");   // A/B: the pre-fix join side rule
            Refs.SpriteCentres = !args.Contains("--sprite-corner-frame");       // A/B: the Sprite API's pixel-centre frame
            VectorOffset.StrokeContours = !args.Contains("--piece-bars");        // A/B: the stroke outline as one contour vs loose bars
            OutlineFit.ArcSegs = !args.Contains("--no-arc-segs");                // A/B: circular spans as arcs vs split to their chords
            if (args.Contains("--arclog")) OutlineFit.ArcLog = s => Console.WriteLine(s);   // why a span was not taken as an arc
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--gdi-pixeloffset") Refs.GdiPixelOffset = int.Parse(args[i + 1]);
            var inv = CultureInfo.InvariantCulture;

            var cases = Fixtures.Build().Where(c => only == null || c.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (emitDir != null)
            {   // one page per case, so a headless browser can screenshot each at exactly 240 x 180
                Directory.CreateDirectory(emitDir);
                var manifest = new List<string>();
                foreach (var cse in cases)
                {
                    var svg = Refs.SvgPage(cse);
                    if (svg == null) continue;
                    string f = Safe(cse.Name) + ".html";
                    File.WriteAllText(Path.Combine(emitDir, f),
                        "<!doctype html><meta charset=utf-8><style>html,body{margin:0;padding:0;background:#fff;overflow:hidden}</style>" + svg);
                    manifest.Add($"{Safe(cse.Name)}\t{cse}");
                }
                File.WriteAllLines(Path.Combine(emitDir, "manifest.tsv"), manifest);
                Console.WriteLine($"{manifest.Count} svg pages in {emitDir}");
                Console.WriteLine("rem" + string.Join("\nrem", manifest.Select(m =>
                    $"msedge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --default-background-color=FFFFFFFF " +
                    $"--window-size={Refs.W},{Refs.H} --screenshot=\"{emitDir}\\{m.Split('\t')[0]}.png\" \"file:///{(Path.Combine(emitDir, m.Split('\t')[0] + ".html")).Replace('\\', '/')}\"")));
                return 0;
            }
            if (args.Contains("--wpfprobe"))
            {
                var p = new VectorPath(); p.MoveTo(40, 90); p.LineTo(200, 90);
                var probe = Refs.Wpf(p, 7, Cap.Butt, Join.Miter, 4, null, 0);
                Console.WriteLine(probe == null ? "wpf probe: null (" + Refs.WpfError + ")" : "wpf probe stroke: sum " + probe.Sum(b => (int)b) + " max " + probe.Max() + " err " + (Refs.WpfError ?? "none"));
                Console.WriteLine("wpf probe fill  : " + Refs.WpfFillProbe());
                Console.WriteLine("wpf variants    : " + Refs.WpfStrokeVariants());
                Console.WriteLine(Refs.WpfEnvProbe());
                return 0;
            }
            if (args.Contains("--probe-join")) { ProbeJoinSide(inv); return 0; }
            if (args.Contains("--bandprobe")) { BandProbe(cases.FirstOrDefault(), inv); return 0; }
            if (args.Contains("--bakedump"))
            {   // the geometry of the baked outline, so a fill defect can be told from a ring defect
                foreach (var cse in cases)
                {
                    double r = cse.Align == 0 ? cse.Width / 2 : cse.Width;
                    int jc = cse.Join == Join.Bevel ? 1 : cse.Join == Join.Round ? 2 : 0;
                    var b = VectorOffset.Outline(cse.Path, r, cse.Align, 0.05, 0.02, (VectorCap)cse.Cap, jc, cse.Miter);
                    Console.WriteLine($"  {cse.Name}\n    {b.ToSvgData()}");
                }
                return 0;
            }
            if (rowDiff != null) { RowDiff(rowDiff, inv); return 0; }
            var rows = new List<Row>();
            var nodeRows = new List<(string name, Refs.Nodes src, Refs.Nodes bake, Refs.Nodes widen)>();
            Console.WriteLine($"stroke truth harness: {cases.Count} cases, gate dev {GateDev} px / hard px {GateStrongFrac:P1} of ink, gdi pixeloffset={Refs.GdiPixelOffset} miterhalf={Refs.GdiMiterHalf}, out {outDir}");
            Console.WriteLine("case".PadRight(46) + "truth".PadRight(10) + "A engine-vector".PadRight(32) + "B Sprite-API".PadRight(32) + "GDI+".PadRight(32) + "WPF".PadRight(32) + "outline");
            Console.WriteLine("  i=binarised ink px  s=% of truth ink differing  d=worst hard-mismatch distance  e/m=excess/missing coverage  n=hard px count  * = outside the gate");
            foreach (var cse in cases)
            {
                var truth = Truth.Raster(BuildTruth(cse), Refs.W, Refs.H);
                int truthInk = 0;
                for (int i = 0; i < truth.Length; i++) if (truth[i] >= 128) truthInk++;
                var r = new Row { Cse = cse };
                r.Add("A", Refs.EngineA(cse), truth);
                r.Add("B", Refs.EngineB(cse), truth);
                // GDI+ and WPF pens are always centred, so neither is a reference for an inside / outside band
                if (cse.Align == 0)
                {
                    r.Add("gdi", Refs.GdiPlus(cse.Path, cse.Width, cse.Cap, cse.Join, cse.Miter, cse.Dash, cse.DashOffset), truth);
                    if (wpf) r.Add("wpf", Refs.Wpf(cse.Path, cse.Width, cse.Cap, cse.Join, cse.Miter, cse.Dash, cse.DashOffset), truth);
                if (webDir != null) r.Add("web", Refs.ReadPng(Path.Combine(webDir, Safe(cse.Name) + ".png")), truth);
                }
                if (outlineMode)
                {
                    var (baked, bakeNodes) = BakeOutline(cse);
                    r.Add("out", baked, truth, bakeNodes);
                    Refs.Nodes widenNodes = default;
                    if (cse.Align == 0 && (cse.Dash == null || cse.Dash.Length == 0))    // a native pen is always centred,
                    {                                                                   // and Widen has no dash input
                        byte[] widened = Refs.WidenOutline(cse.Path, cse.Width, cse.Cap, cse.Join, cse.Miter, out widenNodes);
                        r.Add("wid", widened, truth, widenNodes);
                    }
                    nodeRows.Add((cse.Name, Refs.Of(cse.Path), bakeNodes, widenNodes));
                }
                rows.Add(r);
                string na = cse.Align == 0 ? "" : "  [gdi/wpf: n/a, a native pen is always centred]";
                Console.WriteLine(cse.Name.PadRight(46) + $"[{truthInk}]".PadRight(10) + string.Concat(r.Cells.Select(cell => cell.Text(inv))) + (r.Failed.Count > 0 ? "   * " + string.Join(",", r.Failed) : "") + na);
                if (images)
                {
                    var inks = new List<byte[]> { truth };
                    foreach (var name in new[] { "A", "B", "gdi", "wpf", "out" })
                    {
                        var cell = r.Cells.FirstOrDefault(c => c.Name == name);
                        inks.Add(cell?.Ink ?? new byte[Refs.W * Refs.H]);
                    }
                    string safe = string.Concat(cse.Name.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_'));
                    Refs.WritePng(Path.Combine(outDir, safe + ".png"), inks.ToArray(), Refs.W, Refs.H, 2);
                    foreach (var cell in r.Cells)
                        Refs.WriteDiff(Path.Combine(outDir, safe + "_diff" + cell.Name + ".png"), cell.Ink, truth, Refs.W, Refs.H, 2);
                }
            }
            // the summary: which column is worst, and the cases that fail the gate
            Console.WriteLine();
            foreach (var col in rows.SelectMany(r => r.Cells).Select(c => c.Name).Distinct())
            {
                var cell = rows.SelectMany(r => r.Cells.Where(c => c.Name == col)).ToList();
                Console.WriteLine($"{col,-5} cases {cell.Count,4}  fail {cell.Count(c => !Pass(c.M)),3}  worst dev {cell.Max(c => c.M.MaxDev):0.0} px  worst hard px {cell.Max(c => c.M.Strong),5}  mean hard {cell.Average(c => (double)c.M.Strong):0.0}  worst share {cell.Max(c => c.M.Share):0.00}%  mean share {cell.Average(c => c.M.Share):0.000}%");
            }
            NodeReport(nodeRows, inv);
            var bad = rows.Where(r => r.Failed.Count > 0).OrderByDescending(r => r.Cells.Max(c => c.M.Strong)).ToList();
            Console.WriteLine($"\n{bad.Count} of {rows.Count} cases outside the gate" + (bad.Count == 0 ? "" : " (most hard pixels first):"));
            foreach (var r in bad.Take(40))
                Console.WriteLine($"  {r.Cse.Name,-46} {string.Join(" ", r.Failed),-12} worst {r.HardCell.M}");
            WriteReport(outDir, rows, inv);
            if (dump != null) DumpCoverage(dump, inv);
            return bad.Count;
        }

        static bool Pass(Refs.Metric m) => m.MaxDev <= GateDev && m.Strong <= Math.Max(4, GateStrongFrac * m.InkCount);

        static string Safe(string name) => string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_'));

        /// <summary>Prints an ASCII coverage map centred on the pixel where the buffers disagree most, so a failure
        /// shows up as a shape instead of a number (and so a suspiciously perfect match can be inspected).</summary>
        static void DumpCoverage(string needle, CultureInfo inv)
        {
            var cse = Fixtures.Build().FirstOrDefault(c => c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (cse == null) { Console.WriteLine("dump: no case matches " + needle); return; }
            var truth = Truth.Raster(BuildTruth(cse), Refs.W, Refs.H);
            var others = new List<(string, byte[])> { ("A", Refs.EngineA(cse)) };
            var b = Refs.EngineB(cse); if (b != null) others.Add(("B", b));
            // a native pen is always centred: it says nothing about an inside / outside band (same rule as the table)
            if (cse.Align == 0)
            {
                var g = Refs.GdiPlus(cse.Path, cse.Width, cse.Cap, cse.Join, cse.Miter, cse.Dash, cse.DashOffset);
                if (g != null) others.Add(("gdi", g));
                var w = Refs.Wpf(cse.Path, cse.Width, cse.Cap, cse.Join, cse.Miter, cse.Dash, cse.DashOffset);
                if (w != null) others.Add(("wpf", w));
            }
            var (bakedInk, _) = BakeOutline(cse);
            if (bakedInk != null) others.Add(("out", bakedInk));
            int worst = 0, best = -1;
            for (int i = 0; i < truth.Length; i++)
                foreach (var (_, buf) in others)
                {
                    int d = Math.Abs(buf[i] - truth[i]);
                    if (d > best) { best = d; worst = i; }
                }
            int cx = worst % Refs.W, cy = worst / Refs.W;
            const int rw = 26, rh = 15;
            Console.WriteLine($"\ndump {cse}  worst disagreement {best}/255 at ({cx},{cy})");
            Console.WriteLine("     legend: ' '=paper  .:-=+*#% = increasing ink coverage   truth on top, one line per render");
            for (int band = 0; band <= others.Count; band++)
            {
                var buf = band == 0 ? truth : others[band - 1].Item2;
                Console.WriteLine("  --- " + (band == 0 ? "TRUTH" : others[band - 1].Item1));
                for (int y = Math.Max(0, cy - rh / 2); y < Math.Min(Refs.H, cy + rh / 2); y++)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int x = Math.Max(0, cx - rw / 2); x < Math.Min(Refs.W, cx + rw / 2); x++)
                        sb.Append(" .:-=+*#%@"[Math.Min(9, buf[y * Refs.W + x] / 26)]);
                    Console.WriteLine($"{y,4} {sb}");
                }
            }
        }

        /// <summary>Join-side probe: the turn at a vertex has exactly one outer side, and a polyline join must be built
        /// there. This compares the rule in cs/Vector.Render.cs (a copy, so the engine stays untouched) with the geometry,
        /// over every base direction and turn angle, and then prints the contours the real stroker emits at a star tip.</summary>
        static void ProbeJoinSide(CultureInfo inv)
        {
            Console.WriteLine("join-side probe: for a corner with incoming direction d0 and outgoing d1 the outer normals are");
            Console.WriteLine("  n0 = side*(-d0y, d0x)*hw, n1 = side*(-d1y, d1x)*hw, and the join lives on the side where");
            Console.WriteLine("  n0+n1 points along the bulge (d0-d1). dot(n0+n1, d0-d1) = -2*side*cross, so side = cross>0 ? -1 : +1.");
            Console.WriteLine("The engine instead rotates n0 by -side*th/2 and tests that midpoint against the bulge;");
            Console.WriteLine("that midpoint is (180-th) degrees off the bulge on the wrong side, so for th > 90 both sides pass");
            Console.WriteLine("and the first candidate (-1) wins. Columns: side the engine picks, side that is correct, and where");
            Console.WriteLine("the join polygon's centre lands relative to the corner (outside = right, inside = wrong).\n");
            Console.WriteLine($"{"th",5} {"a0",6} {"turn",6} {"engine",8} {"correct",8} {"engine centre dot",18} {"verdict",8}");
            int flips = 0, rows = 0;
            foreach (double thDeg in new[] { 10.0, 30.0, 60.0, 89.0, 91.0, 110.0, 138.0, 150.0, 170.0 })
                foreach (double a0Deg in new[] { 0.0, 37.0, 90.0, 173.0 })
                    foreach (int turn in new[] { +1, -1 })   // +1 = left turn in math convention, -1 = right turn
                    {
                        double th = thDeg * Math.PI / 180, a0 = a0Deg * Math.PI / 180;
                        double d0x = Math.Cos(a0), d0y = Math.Sin(a0);
                        double a1 = a0 + turn * th;
                        double d1x = Math.Cos(a1), d1y = Math.Sin(a1);
                        double cross = d0x * d1y - d0y * d1x;
                        double bulx = d0x - d1x, buly = d0y - d1y;
                        double blen = Math.Sqrt(bulx * bulx + buly * buly);
                        if (blen < 1e-9) continue;
                        double engine = OldOuterSign(d0x, d0y, (float)th, bulx, buly);
                        double correct = cross > 0 ? -1 : 1;
                        // the miter kite centre (vertex + the mean of the two outer normals and the tip) projected on the bulge
                        // the wedge's outward direction is n0+n1; the join is on the outer side when it aligns with the bulge
                        const double hw = 3.5;
                        double n0x = -d0y * hw * engine, n0y = d0x * hw * engine;
                        double n1x = -d1y * hw * engine, n1y = d1x * hw * engine;
                        double dot = ((n0x + n1x) * bulx + (n0y + n1y) * buly) / blen;
                        rows++;
                        bool ok = Math.Abs(dot) < 1e-9 || dot > 0;
                        if (engine != correct) flips++;
                        Console.WriteLine($"{thDeg,5:0.#} {a0Deg,6:0.#} {(turn > 0 ? "left" : "right"),6} {engine,8:+0;-0} {correct,8:+0;-0} {dot,18:0.000} {(engine == correct ? "ok" : "WRONG SIDE"),8}");
                    }
            _ = inv;
            Console.WriteLine($"\n{flips} of {rows} corners take the inner side (the wedge lands inside the band and disappears).");
            Console.WriteLine("Now the real stroker: the contours it emits for the top point of the 5-point star (w=7, miter).");
            var cse = Fixtures.Build().First(c => c.Name == "star w7 Miter");
            var shape = new VectorShape(cse.Path, null, VectorColor.Black, (float)cse.Width)
            { Cap = (VectorCap)cse.Cap, Join = (VectorJoin)cse.Join, MiterLimit = (float)cse.Miter };
            var pts = new List<PointF>();
            var subs = VectorRender.Flatten(cse.Path, System.Numerics.Matrix3x2.Identity, 0.05f, pts);
            foreach (bool legacy in new[] { true, false })
            {
                VectorRender.LegacyJoinSideTest = legacy;
                var o = new List<PointF>(); var oc = new List<int>();
                VectorRender.Stroke(pts, subs, shape, (float)cse.Width, o, oc);
                // the star's top point is at (120, 22); anything the stroker puts there is the join wedge
                Console.WriteLine($"  {(legacy ? "old rule (LegacyJoinSideTest = true)" : "new rule")} : {oc.Count} contours total");
                Console.WriteLine($"           {"contour",8} {"area",9} {"x range",22} {"y range",22}  relation to the vertex at y=22");
                int idx = 0, atTip = 0;
                for (int ci = 0; ci < oc.Count; ci++)
                {
                    int cnt = oc[ci]; double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9, area = 0;
                    for (int i = 0; i < cnt; i++)
                    {
                        var p = o[idx + i];
                        x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
                        var q = o[idx + (i + 1) % cnt]; area += (double)p.X * q.Y - (double)q.X * p.Y;
                    }
                    area /= 2;
                    if (y0 < 22.01 && Math.Abs((x0 + x1) / 2 - 120) < 8)
                    {
                        atTip++;
                        string where = y1 <= 22.01 ? "outer: " + (22 - y0).ToString("0.##", inv) + " px above the vertex"
                                                   : "inner: " + (y1 - 22).ToString("0.##", inv) + " px below it, inside the band";
                        Console.WriteLine($"           #{ci,-7} {area,9:0.0} {($"[{x0.ToString("0.##", inv)}, {x1.ToString("0.##", inv)}]"),-22} {($"[{y0.ToString("0.##", inv)}, {y1.ToString("0.##", inv)}]"),-22} {where}");
                    }
                    idx += cnt;
                }
                if (atTip == 0) Console.WriteLine("           none");
            }
            VectorRender.LegacyJoinSideTest = false;
            // what the truth puts there, from the independent primitive builder
            var reg = new TruthRegion();
            foreach (var (tp, closed) in Fixtures.Flatten(cse.Path)) Truth.AddStroke(reg, tp, closed, cse.Width / 2, cse.Cap, cse.Join, cse.Miter);
            double top = double.MaxValue;
            foreach (var prim in reg.Prims) if (prim.X0 < 128 && prim.X1 > 112 && prim.Y0 < 30) top = Math.Min(top, prim.Y0);
            Console.WriteLine($"analytic truth topmost y near x=120 = {top.ToString("0.##", inv)}");
            Console.WriteLine($"expected miter tip: hw/cos(th/2) = {(3.5 / Math.Cos(138.0 * Math.PI / 180 / 2)).ToString("0.00", inv)} px above the vertex, i.e. y = {(22 - 3.5 / Math.Cos(138.0 * Math.PI / 180 / 2)).ToString("0.##", inv)}");

            static double OldOuterSign(double d0x, double d0y, float th, double bulx, double buly)
            {   // verbatim cs/Vector.Render.cs:255-266, the code path under test
                float half = th / 2;
                foreach (float side in new[] { -1f, 1f })
                {
                    float x = (float)-d0y * side, y = (float)d0x * side, a = -side * half, c = MathF.Cos(a), sn = MathF.Sin(a);
                    float mx = x * c - y * sn, my = x * sn + y * c;
                    if (mx * bulx + my * buly > 0) return side;
                }
                return -1f;
            }
        }

        /// <summary>Where two buffers disagree, as a profile instead of a picture: the per-row and per-column coverage
        /// sums of truth and of one render, side by side, with the rows and columns that differ marked. A constant
        /// absolute deficit at two different widths cannot be a shape error, so this finds what it is.</summary>
        static void RowDiff(string needle, CultureInfo inv)
        {
            var cse = Fixtures.Build().FirstOrDefault(c => c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (cse == null) { Console.WriteLine("rowdiff: no case matches " + needle); return; }
            var truth = Truth.Raster(BuildTruth(cse), Refs.W, Refs.H);
            var got = Refs.EngineA(cse);
            Console.WriteLine($"rowdiff {cse}  coverage sums in pixel units (255 = one full pixel)");
            var rowsT = new double[Refs.H]; var rowsG = new double[Refs.H];
            var colsT = new double[Refs.W]; var colsG = new double[Refs.W];
            for (int y = 0; y < Refs.H; y++) for (int x = 0; x < Refs.W; x++)
                { rowsT[y] += truth[y * Refs.W + x]; rowsG[y] += got[y * Refs.W + x]; colsT[x] += truth[y * Refs.W + x]; colsG[x] += got[y * Refs.W + x]; }
            Console.WriteLine("  rows differing by more than one pixel of coverage:");
            for (int y = 0; y < Refs.H; y++)
                if (Math.Abs(rowsT[y] - rowsG[y]) > 255)
                    Console.WriteLine($"    y={y,-4} truth {rowsT[y] / 255,8:0.0}  A {rowsG[y] / 255,8:0.0}  diff {(rowsT[y] - rowsG[y]) / 255,7:0.0}");
            Console.WriteLine("  columns differing by more than one pixel of coverage:");
            for (int x = 0; x < Refs.W; x++)
                if (Math.Abs(colsT[x] - colsG[x]) > 255)
                    Console.WriteLine($"    x={x,-4} truth {colsT[x] / 255,8:0.0}  A {colsG[x] / 255,8:0.0}  diff {(colsT[x] - colsG[x]) / 255,7:0.0}");
            Console.WriteLine($"  totals: truth {rowsT.Sum() / 255:0.0}  A {rowsG.Sum() / 255:0.0}  (sum of |per-pixel| deficits reported as missing by the gate)");
            _ = inv;
        }

        static void WriteReport(string outDir, List<Row> rows, CultureInfo inv)
        {
            File.WriteAllLines(Path.Combine(outDir, "report.tsv"),
                new[] { "case\tcolumn\ttruth-ink\tgot-ink\texcess\tmissing\tshare%\tmeanDev\tmaxDev\thard-px\tanchors\tcubics\tlines\tpass" }.Concat(
                    rows.SelectMany(r => r.Cells.Select(c => $"{r.Cse.Name}\t{c.Name}\t{c.M.Ink.ToString("0.0", inv)}\t{c.M.InkCount}\t{c.M.Excess.ToString("0.0", inv)}\t{c.M.Missing.ToString("0.0", inv)}\t{c.M.Share.ToString("0.000", inv)}\t{c.M.MeanDev.ToString("0.000", inv)}\t{c.M.MaxDev.ToString("0.00", inv)}\t{c.M.Strong}\t{c.Nodes.Anchors}\t{c.Nodes.Cubic}\t{c.Nodes.Line}\t{(Pass(c.M) ? 1 : 0)}"))));
        }

        /// <summary>The node half of the outline requirement: "amount of nodes that make sense". k = on-curve anchors,
        /// c = cubic segments, l = line segments, so a bake that chords every curve reads as c0 and a Corel-style
        /// calculated contour keeps the input's c. Printed per case and as a mean, against GDI+ Widen on the same input.</summary>
        static void NodeReport(List<(string name, Refs.Nodes src, Refs.Nodes bake, Refs.Nodes widen)> nodeRows, CultureInfo inv)
        {
            if (nodeRows.Count == 0) return;
            Console.WriteLine("\noutline nodes (k=on-curve anchors, c=cubic segments, l=line segments):");
            foreach (var (name, src, bake, widen) in nodeRows)
                if (!bake.IsEmpty || !widen.IsEmpty)
                    Console.WriteLine($"  {name,-44} in {src.ToString().PadRight(16)} baked {bake.ToString().PadRight(16)}" +
                        (widen.IsEmpty ? "widen n/a" : $"widen {widen}"));
            var b = nodeRows.Where(r => !r.bake.IsEmpty).ToList();
            if (b.Count > 0)
                Console.WriteLine($"  means over {b.Count} cases:  in k{b.Average(r => r.src.Anchors).ToString("0.0", inv)} c{b.Average(r => r.src.Cubic).ToString("0.0", inv)} l{b.Average(r => r.src.Line).ToString("0.0", inv)}" +
                    $"   baked k{b.Average(r => r.bake.Anchors).ToString("0.0", inv)} c{b.Average(r => r.bake.Cubic).ToString("0.0", inv)} l{b.Average(r => r.bake.Line).ToString("0.0", inv)}" +
                    $"   cubic kept {b.Count(r => r.bake.Cubic > 0)}/{b.Count}");
            var w = nodeRows.Where(r => !r.widen.IsEmpty).ToList();
            if (w.Count > 0)
                Console.WriteLine($"  GDI+ Widen over {w.Count} cases:   k{w.Average(r => r.widen.Anchors).ToString("0.0", inv)} c{w.Average(r => r.widen.Cubic).ToString("0.0", inv)} l{w.Average(r => r.widen.Line).ToString("0.0", inv)}" +
                    $"   cubic kept {w.Count(r => r.widen.Cubic > 0)}/{w.Count}");
        }

        /// <summary>OutlineArrange.Band taken one step at a time for one case: the region rings and their orientation, the
        /// offset ring, the reflex disc the round join needs, and the winding of every piece at a point in the gap. Written
        /// because the numbers alone could not say which of those four drops the arc.</summary>
        static void BandProbe(Case cse, CultureInfo inv)
        {
            if (cse == null) { Console.WriteLine("bandprobe: no case matched"); return; }
            double d = cse.Align == 0 ? cse.Width / 2 : cse.Width;
            var subs = Fixtures.Flatten(cse.Path);
            var input = subs.Where(s => s.Closed).Select(s => s.Pts.Select(q => new Pt2(q.X, q.Y)).ToList()).ToList();
            var region = OutlineArrange.Region(input);
            Console.WriteLine($"bandprobe {cse.Name}: input {input.Count} rings, region {region.Count} rings");
            for (int i = 0; i < region.Count; i++)
                Console.WriteLine($"  region[{i}] pts {region[i].Count} signed area {RingArea(region[i]).ToString("0.0", inv)}");
            var core = OutlineArrange.OffsetRing(region[0], -d, 0);      // the shrunk side, bevel (what join=2 asks OffsetRing for)
            Console.WriteLine($"  core = OffsetRing(region, -{d}, lim 0): pts {core.Count} signed area {RingArea(core).ToString("0.0", inv)}");
            // the discs at the vertices where the gap opens for this side, the way RoundJoinDiscs picks them
            var reg = region[0];
            int nd = reg.Count;
            var discs = new List<List<Pt2>>();
            for (int i = 0; i < nd; i++)
            {
                Pt2 prev = reg[(i - 1 + nd) % nd], cur = reg[i], next = reg[(i + 1) % nd];
                Pt2 e0 = cur - prev, e1 = next - cur;
                double l0 = e0.Length, l1 = e1.Length;
                if (l0 < 1e-12 || l1 < 1e-12) continue;
                Pt2 u0 = e0 * (1 / l0), u1 = e1 * (1 / l1);
                double cross = u0.X * u1.Y - u0.Y * u1.X;
                if (cross * (-d) <= 0) continue;
                var ring = new List<Pt2>();
                for (int k = 0; k < 32; k++)
                {
                    double a = 2 * Math.PI * k / 32;
                    ring.Add(new Pt2(cur.X + d * Math.Cos(a), cur.Y + d * Math.Sin(a)));
                }
                discs.Add(ring);
                Console.WriteLine($"  disc at ({cur.X.ToString("0.0", inv)},{cur.Y.ToString("0.0", inv)}) cross {cross.ToString("0.000", inv)}");
            }
            if (discs.Count == 0) { Console.WriteLine("  no disc vertex selected"); return; }
            var both = new List<List<Pt2>> { discs[0] };
            foreach (var r in region) both.Add(new List<Pt2>(r));
            var clipped = OutlineArrange.Union(both, 2);
            Console.WriteLine($"  disc INTERSECT region = Union([disc, region], 2): {clipped.Count} rings" +
                string.Join("", clipped.Select((r, i) => $"\n    [{i}] pts {r.Count} area {RingArea(r).ToString("0.0", inv)}")));
            // the last step of Band: the region, the reversed core and this piece, unioned at winding 1
            var nc = new List<Pt2>(core); nc.Reverse();
            var without = new List<List<Pt2>> { region[0], nc };
            var all = new List<List<Pt2>>(without);
            all.AddRange(clipped);
            Console.WriteLine("  region - core          : " +
                $"{OutlineArrange.Union(without, 1).Count} rings area {RingArea2(OutlineArrange.Union(without, 1)).ToString("0.0", inv)}");
            Console.WriteLine("  region - core + clip   : " +
                $"{OutlineArrange.Union(all, 1).Count} rings area {RingArea2(OutlineArrange.Union(all, 1)).ToString("0.0", inv)}");
            // the same clip taken against the region shrunk by a hair, so the piece shares no edge with the region ring
            var inset = OutlineArrange.OffsetRing(region[0], -Math.Abs(d) * 1e-3, 4.0);
            var both2 = new List<List<Pt2>> { discs[0], inset };
            var clip2 = OutlineArrange.Union(both2, 2);
            var all2 = new List<List<Pt2>>(without); all2.AddRange(clip2);
            Console.WriteLine("  clip vs inset region   : " +
                $"{clip2.Count} rings area {RingArea2(clip2).ToString("0.0", inv)}   union: " +
                $"{OutlineArrange.Union(all2, 1).Count} rings area {RingArea2(OutlineArrange.Union(all2, 1)).ToString("0.0", inv)}");
            // the winding of each piece at the sample points of the gap, as the final union would see them
            var neg = new List<Pt2>(core); neg.Reverse();
            var samples = new List<Pt2>();
            for (double x = 92; x <= 112; x += 2) for (double y = 58; y <= 72; y += 2) samples.Add(new Pt2(x, y));
            Console.WriteLine("    x    y  region  core  discClip  band(truth-ish)");
            foreach (var p in samples)
            {
                int wr = OutlineArrange.Winding(reg, p), wc = OutlineArrange.Winding(neg, p), wd = clipped.Count == 0 ? 0 : OutlineArrange.Winding(clipped[0], p);
                if (wr + wc + wd >= 1 && wr + wc < 1)
                    Console.WriteLine($"  {p.X,4:0.#} {p.Y,4:0.#}  {wr,5}  {wc,4}  {wd,7}   in the final union, NOT in region-minus-core");
            }
        }

        static double RingArea(IList<Pt2> r)
        {
            double a = 0;
            for (int k = 0; k < r.Count; k++) { Pt2 p = r[k], q = r[(k + 1) % r.Count]; a += p.X * q.Y - q.X * p.Y; }
            return a * 0.5;
        }

        static double RingArea2(IList<List<Pt2>> rings) => rings.Sum(RingArea);

        static TruthRegion BuildTruth(Case cse)
        {
            var subs = Fixtures.Flatten(cse.Path);
            var reg = new TruthRegion();
            var pieces = cse.Dash != null && cse.Dash.Length > 0 ? Fixtures.Dash(subs, cse.Dash, cse.DashOffset) : subs;
            double hw = cse.Align == 0 ? cse.Width / 2 : cse.Width;      // an aligned band of width w reaches w from the edge, then keeps one side
            foreach (var (pts, closed) in pieces) Truth.AddStroke(reg, pts, closed, hw, cse.Cap, cse.Join, cse.Miter);
            if (cse.Align != 0)
            {
                var rings = subs.Where(s => s.Closed).Select(s => s.Pts).ToList();
                reg.Side = cse.Align == 2 ? Truth.Interior(rings) : Truth.Exterior(rings);
            }
            return reg;
        }

        /// <summary>The stroke converted to a filled shape, rasterised the same way the demo's black layer is, together with
        /// the node count of the baked path.</summary>
        static (byte[] Ink, Refs.Nodes Nodes) BakeOutline(Case cse)
        {
            if (cse.Dash != null && cse.Dash.Length > 0) return (null, default);      // the dashed route is a different entry point
            double r = cse.Align == 0 ? cse.Width / 2 : cse.Width;
            int joinCode = cse.Join == Join.Bevel ? 1 : cse.Join == Join.Round ? 2 : 0;
            VectorPath baked;
            try
            {
                baked = VectorOffset.Outline(cse.Path, r, cse.Align, 0.05, 0.02, (VectorCap)cse.Cap, joinCode, cse.Miter);
            }
            catch (Exception e)
            {
                // named rather than silent: an empty outline column read as "no defects measured" while the bake threw
                Console.WriteLine($"  bake threw for {cse.Name}: {e.GetType().Name}: {e.Message}");
                return (null, default);
            }
            var nodes = Refs.Of(baked);
            using var canvas = new Sprite(Refs.W, Refs.H);
            canvas.ClearBuffer(unchecked((int)0xFFFFFFFF));
            var img = new VectorImage();
            img.Shapes.Add(new VectorShape(baked, VectorColor.Black, null, 0f));
            img.Draw(canvas, System.Numerics.Matrix3x2.Identity, new VectorRenderOptions { AA = true, Strokes = false, Fills = true });
            var px = canvas.Pixels;
            var ink = new byte[px.Length];
            for (int i = 0; i < px.Length; i++) ink[i] = (byte)Math.Clamp(255 - (px[i] & 0xFF), 0, 255);
            GC.KeepAlive(canvas);          // px is a Span over the sprite's unmanaged buffer: see Refs.InkOf
            return (ink, nodes);
        }

        sealed class Row
        {
            public Case Cse;
            public List<Cell> Cells = new List<Cell>();
            public void Add(string name, byte[] ink, byte[] truth, Refs.Nodes nodes = default)
            {
                if (ink == null) return;
                Cells.Add(new Cell { Name = name, Ink = ink, Nodes = nodes, M = Refs.Compare(ink, truth, Refs.W, Refs.H) });
            }
            public List<string> Failed => Cells.Where(c => !Program.Pass(c.M)).Select(c => c.Name).ToList();
            public Cell HardCell => Cells.OrderByDescending(c => c.M.Strong).ThenByDescending(c => c.M.MaxDev).First();
        }

        sealed class Cell
        {
            public string Name;
            public byte[] Ink;
            public Refs.Nodes Nodes;
            public Refs.Metric M;
            public string Text(CultureInfo inv) =>
                ($"{Name} i{M.InkCount.ToString("D4", inv)} s{M.Share.ToString("0.0", inv)}% d{M.MaxDev.ToString("0.0", inv)} n{M.Strong.ToString("D4", inv)}" +
                    (Nodes.IsEmpty ? "" : " " + Nodes)).PadRight(32);
        }
    }
}
