using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // --shots <dir>: render every test into <dir> as one PNG each (+ manifest.tsv) and quit - the audit tool
            string? shots = null;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--shots" || args[i] == "-shots") shots = args[i + 1];
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--framecheck" || args[i] == "-framecheck") return FrameCheck(args[i + 1]);
            // --arclog <file>: one line per circular span the outline fitter took or refused, so "this shape did not come
            // out as arcs, and why" is a measurement of the page's own pipeline instead of a guess about it. Written beside
            // the report rather than into it, because the report is diffed against a checked-in baseline.
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--arclog" || args[i] == "-arclog") ArcLogTo(args[i + 1]);
            // --dashprobe <file>: the dash cutter's own lines - what the stroker cut and what the conversion spanned - so a
            // disagreement between the two can be read as two lists of arc lengths instead of inferred from the ink.
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--dashprobe" || args[i] == "-dashprobe") DashProbeTo(args[i + 1]);
            // --baketol <mm>: the tolerance the bake fits its outline to. The check reports the node count and the pixel
            // error together, so what the tolerance buys is a measurement rather than a claim about either one alone.
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--baketol" || args[i] == "-baketol")
                    VectorOffset.BakeTol = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
            // to the console only: the report is diffed against a checked-in baseline, and a line in it would make every
            // comparison start with a difference that says nothing about the geometry
            Console.WriteLine($"VectorOffset.BakeTol = {VectorOffset.BakeTol.ToString(CultureInfo.InvariantCulture)} mm, OutlineFit.ArcSegs = {OutlineFit.ArcSegs}");
            DashFlags(args);
            Console.WriteLine($"dash: {Tests.OutlineConvertDemo.DashRunNote()}");
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--outlinecheck" || args[i] == "-outlinecheck") return OutlineCheck(args[i + 1]);
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--fileprobe" || args[i] == "-fileprobe") return FileProbe(args[i + 1]);
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--outlineperf" || args[i] == "-outlineperf") return OutlinePerf(args[i + 1], Array.IndexOf(args, "--no-arc-segs") < 0);
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--bandprobe" || args[i] == "-bandprobe") { Console.Error.WriteLine("dispatched"); return BandProbe(args[i + 1]); }
            for (int i = 0; i < args.Length; i++) if (args[i] == "--uiprobe" || args[i] == "-uiprobe") return UiProbe();
            if (!Environment.Is64BitProcess)
            {
                MessageBox.Show("SR2DDemo must run as a 64-bit process (SR2D64.dll is x64).", "SR2D Demo");
                return 2;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            // Unhandled exceptions are logged to crash.log next to the exe and shown with the details pre-copied to the
            // clipboard, instead of the default Continue/Quit dialog. (A StackOverflowException is process-fatal and
            // cannot be caught - everything else, on any thread, lands here.)
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ShowCrash(e.Exception, terminating: false);          // UI thread
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject, System.Globalization.CultureInfo.InvariantCulture)), terminating: true);   // background threads: log only
            Application.Run(new MainForm { ShotsDir = shots });
            return 0;
        }

        /// <summary>Points the outline fitter's span log at a file. The writer is left open and flushed per line: the process
        /// ends when the check does, and a buffered writer would lose the tail, which is where the verdict sits.</summary>
        static void ArcLogTo(string path)
        {
            var w = new StreamWriter(path, false) { AutoFlush = true };
            OutlineFit.ArcLog = s => { lock (w) w.WriteLine(s); };
        }

        /// <summary>Points the arrangement's probe at a file, keeping only the dash lines: the rest are per-ring and per-stage
        /// and would bury the arc lengths a dash comparison is about.</summary>
        static void DashProbeTo(string path)
        {
            var w = new StreamWriter(path, false) { AutoFlush = true };
            OutlineArrange.BandProbe = s => { if (s.StartsWith("dash ", StringComparison.Ordinal)) lock (w) w.WriteLine(s); };
        }

        /// <summary>The dash setting the two outline pages edit, from the command line, so a seam rule or a unit convention is
        /// measured headlessly instead of clicked through: <c>--dash blob|all|off</c>, <c>--dashpat "5 3"</c>,
        /// <c>--dashunits width|mm</c>, <c>--dashoff &lt;v&gt;</c>, <c>--dashseam cut|wrap</c>. A word that does not parse is
        /// reported and left alone: silently dashing nothing would read as a seam rule having no effect.</summary>
        static void DashFlags(string[] args)
        {
            string? Arg(string name) { for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1]; return null; }
            void Bad(string flag, string? v, string want) => Console.WriteLine($"ignored {flag} \"{v}\": expected {want}");
            var scope = Arg("--dash");
            if (scope != null)
            {
                var s = scope.ToLowerInvariant();
                if (s == "blob" || s == "wavy") Tests.OutlineConvertDemo.DashScope = 0;
                else if (s == "all") Tests.OutlineConvertDemo.DashScope = 1;
                else if (s == "off" || s == "none") Tests.OutlineConvertDemo.DashScope = 2;
                else Bad("--dash", scope, "blob | all | off");
            }
            var pat = Arg("--dashpat");
            if (pat != null) Tests.OutlineConvertDemo.DashText = pat;
            var units = Arg("--dashunits");
            if (units != null)
            {
                var u = units.ToLowerInvariant();
                if (u == "width" || u == "w") Tests.OutlineConvertDemo.DashInWidths = true;
                else if (u == "mm" || u == "user") Tests.OutlineConvertDemo.DashInWidths = false;
                else Bad("--dashunits", units, "width | mm");
            }
            var off = Arg("--dashoff");
            if (off != null)
            {
                if (float.TryParse(off, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && !float.IsInfinity(v) && v >= 0) Tests.OutlineConvertDemo.DashOffset = v;
                else Bad("--dashoff", off, "a number >= 0");
            }
            var seam = Arg("--dashseam");
            if (seam != null)
            {
                var s = seam.ToLowerInvariant();
                if (s == "cut") Tests.OutlineConvertDemo.DashSeamChoice = VectorDashSeam.Cut;
                else if (s == "wrap") Tests.OutlineConvertDemo.DashSeamChoice = VectorDashSeam.Wrap;
                else Bad("--dashseam", seam, "cut | wrap");
            }
        }

        /// <summary>Builds the two outline pages' settings panels headless and prints every child's bounds at three widths:
        /// the panels are sprite-drawn WinForms controls whose layout is arithmetic settled on Resize, so a group that
        /// overflows the panel or lands on top of a neighbour is a number in this log, not a surprise on someone's screen.
        /// Exit 0 = nothing overflows or overlaps.</summary>
        static int UiProbe()
        {
            // the interpolated values here are names and integer geometry, so the explicit invariant culture the analyser
            // asks for cannot change a byte of the report this probe is compared against
            var sb = new System.Text.StringBuilder();
            int bad = 0;
            (string Name, Func<System.Windows.Forms.Control> Build)[] pages =
            {
                ("outline convert", Tests.OutlineConvertDemo.BuildSettings),
                ("stroke check", Tests.StrokeCheckDemo.BuildSettings),
            };
            foreach (var (name, build) in pages)
            {
                var root = build();
                foreach (int w in new[] { 320, 700, 1400 })
                {
                    root.Width = w;
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{name} at width {w}: root {root.Width}x{root.Height}");
                    for (int i = 0; i < root.Controls.Count; i++)
                    {
                        var c = root.Controls[i];
                        string text = c.Text.Length > 40 ? c.Text.Substring(0, 40) + "..." : c.Text;
                        sb.AppendLine(CultureInfo.InvariantCulture, $"  {i} {c.GetType().Name,-15} at {c.Left,4},{c.Top,4} {c.Width,5}x{c.Height,4} {(c.Visible ? "shown " : "hidden")} \"{text.Replace("\r", " ").Replace("\n", " ")}\"");
                        if (c.Visible && (c.Bottom > root.Height || c.Right > root.Width)) { bad++; sb.AppendLine(CultureInfo.InvariantCulture, $"    OUTSIDE: bottom {c.Bottom} right {c.Right} vs root {root.Width}x{root.Height}"); }
                    }
                    // hidden siblings are the page's either/or views (the table and the one-shape group share a slot on
                    // purpose), so only two shown controls can actually be on top of each other
                    for (int i = 0; i < root.Controls.Count; i++)
                        for (int j = i + 1; j < root.Controls.Count; j++)
                        {
                            if (!root.Controls[i].Visible || !root.Controls[j].Visible) continue;
                            var a = root.Controls[i].Bounds; a.Intersect(root.Controls[j].Bounds);
                            if (a.Width > 0 && a.Height > 0) { bad++; sb.AppendLine(CultureInfo.InvariantCulture, $"    OVERLAP: child {i} and {j} share {a.Width}x{a.Height}"); }
                        }
                }
                root.Dispose();
            }
            Console.Write(sb.ToString());
            Console.WriteLine(bad == 0 ? "UI PROBE PASS: no child outside its panel, no two children overlapping" : $"UI PROBE FAIL: {bad} layout problems");
            return bad == 0 ? 0 : 1;
        }

        /// <summary>
        /// --outlinecheck &lt;file&gt;: the Outline Convert To Object page's own check, run headless, with the per-case numbers
        /// it aggregates. The page claims its baked outline is what the engine's stroke paints; that claim used to be only
        /// lookable-at, so nothing could contradict it. Writes the table and the verdict to &lt;file&gt;, exit 0 = pass.
        /// </summary>
        static int OutlineCheck(string path)
        {
            var sb = new System.Text.StringBuilder();
            string? fail = null;
            // the pairs are written next to the report so a failing number can be looked at, not only argued about
            string dump = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                Path.GetFileNameWithoutExtension(path) + "_cases");
            Directory.CreateDirectory(dump);
            try { fail = Tests.OutlineConvertDemo.SelfCheck(line => sb.AppendLine(line), dump); }
            catch (Exception ex) { fail = "check threw " + ex.GetType().Name + ": " + ex.Message; sb.AppendLine(ex.ToString()); }
            sb.AppendLine(CultureInfo.InvariantCulture, $"case images: {dump}");
            sb.AppendLine(fail == null ? "PASS: the conversion and the stroke are the same picture (pixel 0.25, ink integral 1%)"
                                       : "FAIL: " + fail);
            try { File.WriteAllText(path, sb.ToString()); } catch (IOException) { }
            Console.Out.Write(sb.ToString());
            return fail == null ? 0 : 1;
        }

        /// <summary>
        /// --fileprobe &lt;file&gt;: the two outline pages' "Load vector file" and "Export to SVG" buttons, run headless. The
        /// check writes its own probe file, loads it through the call the Load button makes, exports both pages' pictures
        /// through the calls their Export buttons make, reads the files back and compares each with what the page draws.
        /// Writes the report and the verdict to &lt;file&gt;, the probe, the exports and one picture per case to
        /// &lt;file&gt;_files, exit 0 = pass.
        /// </summary>
        static int FileProbe(string path)
        {
            var sb = new System.Text.StringBuilder();
            string? fail = null;
            string dump = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                Path.GetFileNameWithoutExtension(path) + "_files");
            Directory.CreateDirectory(dump);
            try { fail = Tests.OutlineConvertDemo.FileSelfCheck(line => sb.AppendLine(line), dump); }
            catch (Exception ex) { fail = "check threw " + ex.GetType().Name + ": " + ex.Message; sb.AppendLine(ex.ToString()); }
            sb.AppendLine(CultureInfo.InvariantCulture, $"probe, exports and case images: {dump}");
            sb.AppendLine(fail == null ? "PASS: a file loaded through the button becomes a shape both pages measure, and what each Export writes is the picture that page draws"
                                       : "FAIL: " + fail);
            try { File.WriteAllText(path, sb.ToString()); } catch (IOException) { }
            Console.Out.Write(sb.ToString());
            return fail == null ? 0 : 1;
        }

        /// <summary>
        /// --outlineperf &lt;file&gt; [--no-arc-segs]: where the Outline Convert To Object page's open time goes. The page
        /// builds every shape in every column before it shows anything, so its open cost is the sum of 30 conversions
        /// plus the view assembly; this times each stage separately, twice (the first build pays the JIT), then breaks
        /// the slowest conversions down lap by lap inside OutlineArrange.Band and OutlineArrange.Arrange, and writes the
        /// table to &lt;file&gt;. --no-arc-segs runs the same page with the fit's arc scan off, which is how the scan's own
        /// share of the open time is priced. Exit 0 always: it measures, it does not gate.
        /// </summary>
        static int OutlinePerf(string path, bool arcSegs)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                OutlineFit.ArcSegs = arcSegs;
                // the timings are written with a ":0" format and carry no decimal separator, so the explicit invariant
                // culture the analyser asks for cannot change what any line of this report says
                sb.AppendLine(CultureInfo.InvariantCulture, $"OutlineFit.ArcSegs = {arcSegs}");
                Tests.OutlineConvertDemo.ResetViews();
                Tests.OutlineConvertDemo.Perf = null;
                var cold = System.Diagnostics.Stopwatch.StartNew();
                Tests.OutlineConvertDemo.TableViewOnce();
                cold.Stop();
                sb.AppendLine(CultureInfo.InvariantCulture, $"first build (pays the JIT): {cold.Elapsed.TotalMilliseconds:0} ms");
                sb.AppendLine();
                Tests.OutlineConvertDemo.ResetViews();
                Tests.OutlineConvertDemo.Perf = line => sb.AppendLine(line);
                var warm = System.Diagnostics.Stopwatch.StartNew();
                Tests.OutlineConvertDemo.TableViewOnce();
                warm.Stop();
                Tests.OutlineConvertDemo.Perf = null;
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"second build (what reopening the page costs): {warm.Elapsed.TotalMilliseconds:0} ms");

                // the totals above say which conversion is slow; these laps say which step inside its offset is.
                // StageProbe routes OutlineArrange.Band's own probe, which also dumps every ring's vertices, so only
                // the timing lines are kept - a ring dump for tangled is megabytes of report for no measurement.
                sb.AppendLine();
                sb.AppendLine("worst cases, one lap at a time (page settings: Square cap, Miter join):");
                void Stage(string key, int pos, double w)
                {
                    var wall = System.Diagnostics.Stopwatch.StartNew();
                    Tests.OutlineConvertDemo.StageProbe(key, pos, w, VectorCap.Square, VectorJoin.Miter,
                        // ordinal: the probe's own tags are ASCII and the report is line-filtered, not culture-collated
                        line => { if (line.StartsWith("perf ", StringComparison.Ordinal) || line.StartsWith("  arrange ", StringComparison.Ordinal) || line.StartsWith("===", StringComparison.Ordinal) || line.StartsWith("in:", StringComparison.Ordinal)) sb.AppendLine(line); });
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  wall                    {wall.Elapsed.TotalMilliseconds,9:0} ms");
                }
                Stage("tangled", 0, 2.0);
                Stage("tangled", 2, 1.0);
                Stage("eight smooth", 0, 2.0);
            }
            catch (Exception ex) { sb.AppendLine("perf threw " + ex.GetType().Name + ": " + ex.Message); sb.AppendLine(ex.ToString()); }
            try { File.WriteAllText(path, sb.ToString()); } catch (IOException) { }
            Console.Out.Write(sb.ToString());
            return 0;
        }

        /// <summary>
        /// --bandprobe &lt;file&gt;: the stage-by-stage report of one bake (the ring count and net area OutlineArrange.Band sees
        /// after each of its unions), for the cases --outlinecheck reports as nearly empty. Reads only, changes nothing.
        /// </summary>
        static int BandProbe(string path)
        {
            var sb = new System.Text.StringBuilder();
            // each line goes to the file as it is produced: a stage that never reports is the stage that is stuck
            void Log(string s) { sb.AppendLine(s); Console.Out.WriteLine(s); Console.Out.Flush(); }
            try { File.WriteAllText(path, ""); } catch (IOException) { }
            void Sink(string s) { Log(s); try { File.AppendAllText(path, s + "\r\n"); } catch (IOException) { } }
            try
            {
                // the eight is the self-crossing smooth curve whose outside round-join bake comes back with 37 of 2876 pixels
                Tests.OutlineConvertDemo.StageProbe("eight smooth", 1, 1.0, VectorCap.Butt, VectorJoin.Round, Sink);
                Tests.OutlineConvertDemo.StageProbe("eight smooth", 1, 1.0, VectorCap.Butt, VectorJoin.Bevel, Sink);
                Tests.OutlineConvertDemo.StageProbe("eight smooth", 0, 2.0, VectorCap.Butt, VectorJoin.Round, Sink);
                // the jagged A: an open polyline that retraces half of its right leg, so its stroke ring self-touches and
                // carries collinear self-overlapping edges - the arrangement's degenerate input
                Tests.OutlineConvertDemo.StageProbe("A_one_jagged_line", 0, 2.0, VectorCap.Square, VectorJoin.Miter, Sink);
                // the wavy blob's dashes: the pattern is cut along the contour and each dash banded to the stroke it paints,
                // so the ring positions report the clip ring and every dash's cut against it
                foreach (var pos in new[] { 0, 1, 2 })
                    Tests.OutlineConvertDemo.StageProbe("WAVY_DASH", pos, pos == 0 ? 2.0 : 1.0, VectorCap.Square, VectorJoin.Miter, Sink);
                Tests.OutlineConvertDemo.StageProbe("WAVY_DASH", 2, 1.0, VectorCap.Butt, VectorJoin.Round, Sink);
                // the one-shape fit: the frame must stay put while the outline position switches, or the switch reads as
                // the shape changing size (a circle's outside / inside ring both fitted look like two different circles)
                Tests.OutlineConvertDemo.OneShape = true;
                Tests.OutlineConvertDemo.ShapeChoice = Array.IndexOf(Tests.OutlineConvertDemo.Keys, "circle");
                foreach (var pos in new[] { 0, 1, 2 })
                {
                    Tests.OutlineConvertDemo.PosChoice = pos;
                    Sink($"frame circle pos={pos}: {Tests.OutlineConvertDemo.FrameProbe()}");
                }
                Tests.OutlineConvertDemo.OneShape = false;
            }
            catch (Exception ex) { Sink("probe threw " + ex.GetType().Name + ": " + ex.Message); Sink(ex.ToString()); }
            Console.Out.Write(sb.ToString());
            return 0;
        }

        /// <summary>
        /// --framecheck &lt;file&gt;: the truth table of SpriteForm.CoversWorkAsMaximized, the predicate that decides
        /// whether a WM_NCCALCSIZE rectangle is the maximized frame (and so whether the client gets clamped back
        /// inside the working area) without looking at the style bits, followed by the same question across three
        /// screens in a row - what the clamp answers when the working area it was given is not the frame's own.
        /// Written to &lt;file&gt;, exit code 0 = every case as expected. No window is created.
        /// </summary>
        static int FrameCheck(string path)
        {
            var work = R(0, 0, 1920, 1020);             // a 1920x1080 monitor with a 60 px taskbar at the bottom
            var workTop = R(0, 60, 1920, 1080);         // the same monitor with the taskbar along the top
            int give = 9;                                // the invisible border at 125 %: SM_CXSIZEFRAME + SM_CXPADDEDBORDER
            var cases = new (string What, SpriteForm.WinRect Rc, SpriteForm.WinRect Work, bool Want)[]
            {
                ("maximised frame, one border off on all four sides", R(-9, -9, 1929, 1029), work, true),
                ("maximised frame, 8 px border", R(-8, -8, 1928, 1028), work, true),
                ("maximised with the taskbar at the top", R(-9, 51, 1929, 1080), workTop, true),
                ("a normal window at 100,100 800x600", R(100, 100, 900, 700), work, false),
                ("a normal window dragged past the edge by 50 px", R(-50, -50, 1970, 1070), work, false),
                ("a normal window one px over the border allowance", R(-10, -9, 1929, 1029), work, false),
                ("a normal window sized to exactly the working area", R(0, 0, 1920, 1020), work, true),   // clamping it is a no-op
                ("the minimized rectangle at -32000", R(-32000, -32000, -31600, -31500), work, false),
                ("an empty rectangle", R(0, 0, 0, 0), work, false),
                // the frame this machine really gets at 125 % (1920x1020 work area, window at -9,-9) against both
                // the raw metric sum and the DPI-scaled allowance MaximizedBorderAllowance(120): the metrics are not
                // scaled, so an allowance taken straight from them is one pixel short and the clamp would be skipped
                ("the real frame at 125 % with the unscaled metric as the allowance", R(-9, -9, 1929, 1029), work, false),
                ("the real frame at 125 % with the allowance scaled to 120 dpi", R(-9, -9, 1929, 1029), work, true),
            };
            int bad = 0;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"live: SM_CXSIZEFRAME+SM_CXPADDEDBORDER = {SpriteForm.MaxBorder()}, allowance = " +
                $"{SpriteForm.MaximizedBorderAllowance(96)} at 96 dpi, {SpriteForm.MaximizedBorderAllowance(120)} at 120, " +
                $"{SpriteForm.MaximizedBorderAllowance(144)} at 144, {SpriteForm.MaximizedBorderAllowance(192)} at 192");
            foreach (var c in cases)
            {
                int gv = c.What.Contains("unscaled metric") ? 8 : c.What.Contains("scaled to 120 dpi") ? SpriteForm.MaximizedBorderAllowance(120) : give;
                bool got = SpriteForm.CoversWorkAsMaximized(c.Rc, c.Work, gv);
                if (got != c.Want) bad++;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{(got == c.Want ? "ok   " : "FAIL ")}{c.What,-62} {Fmt(c.Rc)} vs {Fmt(c.Work)} give={gv} -> {got}, want {c.Want}");
            }

            // Three screens in a row, the middle one primary with a 60 px taskbar, the outer two without. The maximized
            // frame of each screen is then measured against two working areas: the one it belongs to, and the left one -
            // which is what the window's own position answers while the window is minimized at -32000,-32000, because
            // the nearest monitor from there is the top-left screen of the desktop whatever screen the window is on.
            // That is the reported split: the left screen is measured right by accident, the other two are not.
            var leftWork = R(-1920, 0, 0, 1080);
            var midWork = R(0, 0, 1920, 1020);
            var rightWork = R(1920, 0, 3840, 1080);
            var leftFrame = R(-1929, -9, 9, 1089);          // leftWork inflated by the 9 px invisible border
            var midFrame = R(-9, -9, 1929, 1029);
            var rightFrame = R(1911, -9, 3849, 1089);
            var minRect = R(-32000, -32000, -31763, -31961);    // a 237x39 window parked at the minimized position
            var screens = new (string What, SpriteForm.WinRect Frame, SpriteForm.WinRect Work, bool WantCover, string WantClamp)[]
            {
                ("middle frame against its own working area", midFrame, midWork, true, "work"),
                ("middle frame against the LEFT working area", midFrame, leftWork, false, "sliver"),
                ("right frame against its own working area", rightFrame, rightWork, true, "work"),
                ("right frame against the LEFT working area", rightFrame, leftWork, false, "collapsed"),
                ("left frame against its own working area", leftFrame, leftWork, true, "work"),
                ("left frame against the MIDDLE working area", leftFrame, midWork, false, "sliver"),
                ("the minimized rectangle against the LEFT working area", minRect, leftWork, false, "collapsed"),
            };
            sb.AppendLine();
            sb.AppendLine("three screens in a row (primary middle, taskbar 60 px): is the clamp asked for a frame that belongs to this working area, and what would it answer?");
            foreach (var c in screens)
            {
                bool cover = SpriteForm.CoversWorkAsMaximized(c.Frame, c.Work, give);
                var clamped = SpriteForm.ClampTo(c.Frame, c.Work);
                string got = SpriteForm.IsCollapsed(clamped) ? "collapsed"
                    : clamped.Right - clamped.Left <= give || clamped.Bottom - clamped.Top <= give ? "sliver"
                    : clamped.Left == c.Work.Left && clamped.Top == c.Work.Top && clamped.Right == c.Work.Right && clamped.Bottom == c.Work.Bottom ? "work" : "partial";
                if (cover != c.WantCover || got != c.WantClamp) bad++;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{(cover == c.WantCover && got == c.WantClamp ? "ok   " : "FAIL ")}{c.What,-52} covers={cover} (want {c.WantCover}), an unconditional clamp answers {Fmt(clamped)} = {got} (want {c.WantClamp})");
            }
            try { File.WriteAllText(path, sb.ToString()); } catch { }
            return bad == 0 ? 0 : 1;
        }

        static SpriteForm.WinRect R(int l, int t, int r, int b) { var x = new SpriteForm.WinRect { Left = l, Top = t, Right = r, Bottom = b }; return x; }
        static string Fmt(SpriteForm.WinRect r) => $"{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}";

        /// <summary>Appends the exception to crash.log and shows it; the report is already on the clipboard.</summary>
        static void ShowCrash(Exception ex, bool terminating)
        {
            string log = Path.Combine(AppContext.BaseDirectory, "crash.log");
            try { File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(terminating ? "TERMINATING " : "")}{ex}\r\n\r\n"); } catch { /* best effort */ }
            string full = $"{ex.GetType().Name}: {ex.Message}{(terminating ? "\r\n(the process cannot continue)" : "")}\r\n\r\n{ex}";
            var lines = full.Split('\n');
            if (lines.Length > 40) full = string.Join("\n", lines, 0, 40) + $"\n... ({lines.Length - 40} more lines - in crash.log)";
            if (File.Exists(log)) full += $"\r\n\r\nlogged to:\r\n{log}";
            try { Clipboard.SetText(full); } catch { }
            MessageBox.Show(full, "SR2D Demo - unhandled exception", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
