using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Sr2d64CSport;

namespace Outbench
{
    // Benchmark answering: how much does the SpriteBox output path cost vs the legacy raw HDC blit, and
    // is the damage the present (blit), the message round trip (WM_PAINT), or the resample (SizeMode)?
    // It renders the SAME deterministic scene through four present paths, timing composition (render) and
    // the present separately so the present cost is isolated. See README of the task for the four variants.
    internal static class Program
    {
        [DllImport("user32", ExactSpelling = true)] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32", ExactSpelling = true)] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private const int BG = unchecked((int)0xFF101418);   // surface clear colour; the sanity check counts pixels != BG

        private sealed class Result
        {
            public string Variant = "";
            public int Frames;
            public double RenderMs;
            public double PresentMs;
            public double TotalMs;
            public long Checksum;      // rolling hash of sampled surface pixels (proves the frame had content)
            public long NonBg;         // count of sampled pixels that differ from BG
            public bool Blank;         // true when NonBg == 0 -> a silently-broken measurement
        }

        // The single reused tile: opaque, deterministic gradient so the copies are visibly non-background.
        private static Sprite MakeTile(int size)
        {
            var t = new Sprite(size, size);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int r = (x * 255) / (size - 1);
                    int g = (y * 255) / (size - 1);
                    int b = ((x ^ y) * 255) / (size - 1);
                    t.SetPixel(x, y, unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b));
                }
            return t;
        }

        // Heavy, deterministic scene: clear, then `count` tile copies in a wrapping grid, plus a status line.
        // The grid wraps to the surface's own size so the same code renders full-res and half-res variants.
        private static void RenderScene(Sprite surf, Sprite tile, int count, int frame)
        {
            surf.ClearBuffer(BG);
            int cw = tile.Width, ch = tile.Height;
            int cols = Math.Max(1, surf.Width / cw);
            int rows = Math.Max(1, surf.Height / ch);
            for (int i = 0; i < count; i++)
            {
                int cell = i % (cols * rows);
                int gx = (cell % cols) * cw;
                int gy = (cell / cols) % rows * ch;
                surf.Draw(tile, gx, gy, SR2D.Op.Paint);
            }
            surf.DrawText(2, 2, $"outbench frame {frame} copies {count} {surf.Width}x{surf.Height}",
                          unchecked((int)0xFFFFFFFF), unchecked((int)0xC0000000), 2);
        }

        // Not a stopwatch claim of accuracy: sample the surface and count non-background pixels. If a present
        // path silently draws nothing we get NonBg == 0 and mark the run blank instead of printing a tidy table.
        private static void FrameCheck(Sprite surf, out long checksum, out long nonBg)
        {
            checksum = 1469598103934665603L; nonBg = 0;
            int step = 7;
            for (int y = 0; y < surf.Height; y += step)
                for (int x = 0; x < surf.Width; x += step)
                {
                    int p = surf.GetPixel(x, y);
                    if (p != BG) nonBg++;
                    checksum = (checksum ^ (uint)p) * 1099511628211L;
                }
        }

        // The legacy surface: a plain Control that owns a GDI-backed Sprite. Present is exactly the demo's
        // GetDC -> PaintToDevice(HandleRef) -> ReleaseDC (cs/Sprite.cs:1241, one BitBlt for a GDI surface).
        private sealed class RawSurface : Control
        {
            public Sprite Canvas = null!;
            public void EnsureCanvas()
            {
                int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
                if (Canvas == null || Canvas.Width != w || Canvas.Height != h)
                {
                    Canvas?.Dispose();
                    Canvas = new Sprite(w, h, true);   // GDI DIB section -> PaintToDevice is a single BitBlt
                }
            }
            public void PresentRaw()
            {
                IntPtr hdc = GetDC(Handle);
                try { Canvas.PaintToDevice(new HandleRef(this, hdc)); }
                finally { _ = ReleaseDC(Handle, hdc); }
            }
        }

        private static int Frames = 240, Warm = 15, W = 640, H = 480, Count = 200;
        private static bool ShowWindow = true;

        private static void Main(string[] args)
        {
            foreach (var a in args)
            {
                if (a.StartsWith("--frames=")) int.TryParse(a[9..], out Frames);
                else if (a.StartsWith("--w=")) int.TryParse(a[4..], out W);
                else if (a.StartsWith("--h=")) int.TryParse(a[4..], out H);
                else if (a.StartsWith("--count=")) int.TryParse(a[8..], out Count);
                else if (a.StartsWith("--copies=")) int.TryParse(a[9..], out Count);   // alias
                else if (a == "--noshow") ShowWindow = false;
            }
            if (Frames < 1) Frames = 1;
            if (Count < 1) Count = 1;

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();

            if (!SpriteBox.NativeAvailable)
            {
                Console.WriteLine("outbench: SR2D64 native library unavailable - every Sprite call would throw.");
                Console.WriteLine("outbench: 1 FAILED");
                Environment.Exit(1);
                return;
            }

            using var tile = MakeTile(32);

            var form = new Form
            {
                AutoScaleMode = AutoScaleMode.None,   // keep client size == the device pixels we set, for reproducibility
                ClientSize = new Size(W + 20, H + 20),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(40, 40),
                Text = "outbench",
            };

            var raw = new RawSurface { Bounds = new Rectangle(10, 10, W, H) };
            var boxPresent = new SpriteBox { Bounds = new Rectangle(10, 10, W, H), SizeMode = SpriteSizeMode.None };
            var boxPaint = new SpriteBox { Bounds = new Rectangle(10, 10, W, H), SizeMode = SpriteSizeMode.None };
            // Zoom variant: SizeMode != None makes Viewed true, so Present()/OnPaint route through
            // BlitView -> Compose -> Sprite.DrawScaled (cs/SpriteBox.View.cs:379). ImageSize is HALF the client,
            // so the 2x upscale is a genuine resample, not a 1:1 copy. ViewFilter forces the Bilinear kernel.
            var boxZoom = new SpriteBox
            {
                Bounds = new Rectangle(10, 10, W, H),
                SizeMode = SpriteSizeMode.StretchImage,
                ImageSize = new Size(Math.Max(1, W / 2), Math.Max(1, H / 2)),
                ViewFilter = SR2D.Filter.Bilinear,
            };

            form.Controls.Add(raw);
            form.Controls.Add(boxPresent);
            form.Controls.Add(boxPaint);
            form.Controls.Add(boxZoom);

            // Render handler for BOX-PAINT: composes into e.Surface inside WM_PAINT and self-times, so the
            // round-trip+blit present cost is derived as wall - render.
            boxPaint.Render += (_, e) =>
            {
                var sw = Stopwatch.StartNew();
                RenderScene(e.Surface, tile, Count, s_paintDone);
                s_paintRenderMs += sw.Elapsed.TotalMilliseconds;
                s_paintDone++;
            };

            if (ShowWindow)
            {
                form.Show();
            }
            else
            {
                // Hidden mode: still a real top-level window with a valid HDC, but parked off the visible
                // desktop so nothing lands on a monitor and DWM composition is not included. We must NOT
                // minimise it: SpriteBox.Present() bails when !Visible, and WM_PAINT would never run for
                // a minimised window. Off-screen keeps Visible==true so all four present paths do real work.
                form.Show();
                form.Location = new Point(-32000, -32000);
            }
            Pump();   // let handle creation + first paints settle

            var results = new List<Result>();
            results.Add(RunRaw(raw, tile, form));
            results.Add(RunBoxPresent(boxPresent, tile, form));
            results.Add(RunBoxPaint(boxPaint, tile, form));
            results.Add(RunBoxZoom(boxZoom, tile, form));

            // Restore so the form leaves no stray window if the harness keeps the session alive.
            if (!ShowWindow) form.Location = new Point(40, 40);
            form.Close();

            PrintReport(results, ShowWindow);
        }

        private static void Pump() { for (int i = 0; i < 5; i++) Application.DoEvents(); }

        private static void SetOnly(Control target)
        {
            foreach (Control c in target.Parent!.Controls) c.Visible = ReferenceEquals(c, target);
            target.Visible = true;
            _ = target.Handle;   // force handle creation before measuring
            Pump();
        }

        // One measured pass: warm-up excluded, returns the better-of-two chosen by total.
        private static (double render, double present, double total) MeasurePass(int frames, Action<int> render, Action<int> present)
        {
            var sw = new Stopwatch();
            double renderMs = 0, presentMs = 0;
            var swTotal = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                sw.Restart(); render(f); renderMs += sw.Elapsed.TotalMilliseconds;
                sw.Restart(); present(f); presentMs += sw.Elapsed.TotalMilliseconds;
            }
            swTotal.Stop();
            return (renderMs, presentMs, swTotal.Elapsed.TotalMilliseconds);
        }

        private static Result RunRaw(RawSurface raw, Sprite tile, Form form)
        {
            SetOnly(raw);
            raw.EnsureCanvas();
            void render(int f) => RenderScene(raw.Canvas, tile, Count, f);
            void present(int f) => raw.PresentRaw();
            for (int f = 0; f < Warm; f++) { render(f); present(f); }
            var best = MeasurePass(Frames, render, present);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var pass = MeasurePass(Frames, render, present);
                if (pass.total < best.total) best = pass;
            }
            FrameCheck(raw.Canvas, out long cks, out long nb);
            return Pack("RAW", best, cks, nb);
        }

        private static Result RunBoxPresent(SpriteBox box, Sprite tile, Form form)
        {
            SetOnly(box);
            var surf = box.Surface;   // EnsureSurface -> client-sized GDI surface (SizeMode None)
            void render(int f) => RenderScene(surf, tile, Count, f);
            void present(int f) => box.Present();
            for (int f = 0; f < Warm; f++) { render(f); present(f); }
            var best = MeasurePass(Frames, render, present);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var pass = MeasurePass(Frames, render, present);
                if (pass.total < best.total) best = pass;
            }
            FrameCheck(surf, out long cks, out long nb);
            return Pack("BOX-PRESENT", best, cks, nb);
        }

        private static double s_paintRenderMs;
        private static int s_paintDone;

        // Present happens in WM_PAINT (Redraw -> Invalidate -> pump -> OnPaint runs the Render handler and blits).
        // render is timed inside the handler; present = wall - render (round trip + GetHdc/ReleaseHdc + blit).
        private static Result RunBoxPaint(SpriteBox box, Sprite tile, Form form)
        {
            SetOnly(box);
            var surf = box.Surface;

            void Frame()
            {
                box.Redraw();
                int before = s_paintDone;
                var guard = Stopwatch.StartNew();
                // Pump until the WM_PAINT ran the handler (s_paintDone incremented) - do NOT call Update(), which
                // would paint synchronously and hide the very message round trip this variant is measuring.
                while (s_paintDone <= before && guard.Elapsed.TotalSeconds < 5.0) Application.DoEvents();
            }

            void RunPass()
            {
                s_paintRenderMs = 0; s_paintDone = 0;
                var swTotal = Stopwatch.StartNew();
                for (int f = 0; f < Frames; f++) Frame();
                swTotal.Stop();
                double wall = swTotal.Elapsed.TotalMilliseconds;
                paintStore = (s_paintRenderMs, wall - s_paintRenderMs, wall);
            }

            for (int f = 0; f < Warm; f++) Frame();
            RunPass();
            var first = paintStore;
            RunPass();
            var second = paintStore;
            var best = second.Item3 < first.Item3 ? second : first;

            FrameCheck(surf, out long cks, out long nb);
            return Pack("BOX-PAINT", (best.Item1, best.Item2, best.Item3), cks, nb);
        }
        private static (double, double, double) paintStore;

        private static Result RunBoxZoom(SpriteBox box, Sprite tile, Form form)
        {
            SetOnly(box);
            var surf = box.Surface;   // ImageSize (half) surface; Present() -> BlitView -> Compose -> DrawScaled
            void render(int f) => RenderScene(surf, tile, Count, f);
            void present(int f) => box.Present();
            for (int f = 0; f < Warm; f++) { render(f); present(f); }
            var best = MeasurePass(Frames, render, present);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var pass = MeasurePass(Frames, render, present);
                if (pass.total < best.total) best = pass;
            }
            FrameCheck(surf, out long cks, out long nb);
            return Pack("BOX-ZOOM", best, cks, nb);
        }

        private static Result Pack(string variant, (double render, double present, double total) t, long cks, long nb)
            => new Result
            {
                Variant = variant,
                Frames = Frames,
                RenderMs = t.render,
                PresentMs = t.present,
                TotalMs = t.total,
                Checksum = cks,
                NonBg = nb,
                Blank = nb == 0,
            };

        private static void PrintReport(List<Result> rs, bool shown)
        {
            Console.WriteLine("outbench - SpriteBox output path vs legacy raw HDC blit");
            Console.WriteLine($"surface {W}x{H} | scene copies {Count} (32x32 tile) | frames/variant {Frames} | warm-up {Warm} (excluded)");
            Console.WriteLine($"each variant measured twice; the better (lower total) pass is reported.");
            Console.WriteLine($"timings are Stopwatch totals over {Frames} frames; they vary ~25% run to run, treat as ratios not absolutes.");
            Console.WriteLine(shown
                ? "window SHOWN: on-screen HDC, real composition target."
                : "window hidden: DWM composition not included in the numbers (window parked off the visible desktop, still a real HDC; NOT minimised so Present/WM_PAINT still run).");
            Console.WriteLine("BOX-ZOOM routing: SizeMode=StretchImage (makes Viewed=true), ImageSize=" +
                              $"{Math.Max(1, W / 2)}x{Math.Max(1, H / 2)} (half res, so 2x upscale is a real resample), ViewFilter=Bilinear;");
            Console.WriteLine("  present goes box.Present() -> BlitView -> Compose -> Sprite.DrawScaled (cs/SpriteBox.View.cs:379). NOT FixedSurfaceSize/SmoothStretch.");
            Console.WriteLine();
            Console.WriteLine("variant       | frames | render ms | present ms | total ms | fps    | non-bg px | checksum   | frame");
            Console.WriteLine("--------------+--------+-----------+------------+----------+--------+-----------+------------+------");
            foreach (var r in rs)
            {
                double fps = r.TotalMs > 0 ? r.Frames / (r.TotalMs / 1000.0) : 0;
                Console.WriteLine($"{r.Variant,-13} | {r.Frames,6} | {r.RenderMs,9:F1} | {r.PresentMs,10:F1} | {r.TotalMs,8:F1} | {fps,6:F1} | {r.NonBg,9} | {r.Checksum:X10} | {(r.Blank ? "BLANK!" : "ok")}");
            }
            Console.WriteLine();

            var raw = rs.Find(x => x.Variant == "RAW");
            double rawPresent = raw!.PresentMs;
            foreach (var r in rs)
            {
                if (r.Variant == "RAW") continue;
                if (rawPresent > 0 && r.PresentMs > 0)
                    Console.WriteLine($"{r.Variant} present is {r.PresentMs / rawPresent:F1}x the raw blit");
            }

            // Decomposition: present cost of the SpriteBox 1:1 path vs raw, the round trip, and the resample.
            var present = rs.Find(x => x.Variant == "BOX-PRESENT")!;
            var paint = rs.Find(x => x.Variant == "BOX-PAINT")!;
            var zoom = rs.Find(x => x.Variant == "BOX-ZOOM")!;
            double roundTrip = paint.PresentMs - present.PresentMs;   // WM_PAINT path minus direct Present path
            double resample = zoom.PresentMs - present.PresentMs;     // BlitView/Compose/DrawScaled minus 1:1 blit
            Console.WriteLine();
            Console.WriteLine($"decomposition (present ms vs BOX-PRESENT 1:1 blit): message round trip ~ {roundTrip:F1} ms, size-mode resample ~ {resample:F1} ms");

            // Verdict computed purely from the numbers, never hardcoded.
            Console.WriteLine($"verdict: {ComputeVerdict(rawPresent, present.PresentMs, roundTrip, resample, rs)}");

            bool anyBlank = rs.Exists(x => x.Blank);
            Console.WriteLine();
            if (anyBlank)
            {
                int failed = rs.FindAll(x => x.Blank).Count;
                Console.WriteLine($"outbench: {failed} FAILED (blank frame - present path drew nothing, timings invalid)");
                Environment.Exit(1);
            }
            else
            {
                Console.WriteLine("outbench: all variants measured cleanly, no blank frames.");
                Environment.Exit(0);
            }
        }

        private static string ComputeVerdict(double rawPresent, double boxPresent, double roundTrip, double resample, List<Result> rs)
        {
            // Present-path (1:1) overhead of SpriteBox over the raw blit.
            double presentOverhead = boxPresent - rawPresent;
            double paintTotal = rs.Find(x => x.Variant == "BOX-PAINT")!.TotalMs;
            double rawTotal = rs.Find(x => x.Variant == "RAW")!.TotalMs;
            string path = rawTotal > 0 ? $"the SpriteBox path is {paintTotal / rawTotal:F1}x the raw total end to end; " : "";
            // Which single stage dominates, among raw-vs-box present gap, round trip, resample?
            double pO = Math.Abs(presentOverhead), rt = Math.Abs(roundTrip), re = Math.Abs(resample);
            if (re >= pO && re >= rt)
                return $"{path}the damage is mostly the resample (SizeMode/DrawScaled), ~{resample:F1} ms of present time.";
            if (rt >= pO && rt >= re)
                return $"{path}the damage is mostly the WM_PAINT message round trip, ~{roundTrip:F1} ms beyond a direct Present().";
            return $"{path}the damage is mostly the present itself (SpriteBox adds ~{presentOverhead:F1} ms over the raw blit); round trip and resample are smaller.";
        }
    }
}
