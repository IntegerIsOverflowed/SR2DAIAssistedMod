// SR2D demo - "output paths": how much does the new way of putting pixels on the window cost?
//
// The engine's original output route was: take the control's HDC with GetDC, blit the sprite into it,
// ReleaseDC. SpriteBox is the newer route: a control that owns a back buffer (Surface), can present
// through WM_PAINT or immediately (Present()), and can also stretch / zoom / resample that buffer.
// This bench draws ONE AND THE SAME scene four ways, side by side, each with its own stopwatch, so the
// difference is measured instead of argued about:
//
//   1  RAW BLIT     stock PictureBox + GetDC(handle) -> Sprite.PaintToDevice(HandleRef) -> ReleaseDC   (the original route)
//   2  BOX NOW      SpriteBox.RedrawNow(): the Render event draws into box.Surface, then Present() blits - no message queue
//   3  BOX PAINT    SpriteBox: box.Redraw() -> WM_PAINT -> the Render event draws -> blit              (the way a WinForms app drives it)
//   4  BOX ZOOM     SpriteBox in SizeMode Zoom at 200 %: the same surface, resampled on present        (the stretch / zoom route)
//
// "Fullscreen raw" opens a plain borderless native Form and runs ONE of these routes alone, unthrottled,
// with the real frame rate on screen; the bench's own rendering is paused while that form is up
// (MainForm.Frame() hands every frame over to it). Click / Escape closes it and the bench resumes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    internal static class OutputDemo
    {
        [DllImport("user32.dll", ExactSpelling = true)] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll", ExactSpelling = true)] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        /// <summary>Height the strip row wants.</summary>
        public const int StripHeight = 292;
        const int PaneGap = 3;

        internal enum Kind { Raw, BoxPresent, BoxPaint, BoxZoom }

        internal sealed class Pane
        {
            public Kind K;
            public string Title = "";
            public string Sub = "";
            public string Extra = "";                 // the 4th line (the fullscreen puts the fps there)
            public int Scale = 1;                     // text scale (the fullscreen reads it from across the room)
            public PictureBox? Pic;                     // Kind.Raw
            public SpriteBox? Box;                      // the SpriteBox kinds
            public Sprite? Buffer;                      // Kind.Raw's own back buffer (the boxes own theirs)
            public double DrawMs = -1, BlitMs = -1, PaintMs = -1;   // EMAs per frame; -1 = no sample yet
            public double LastDrawMs;                   // this frame's draw, unsmoothed - so (total - draw) is the blit
            public int DrawFrames;                      // how often the scene actually reached a surface: a route that never ran has nothing to time
            public string Error = "";
        }

        static readonly List<Pane> panes = new List<Pane>();

        // ---- scene settings (the strip's knobs; shared with the fullscreen form) ----
        static int copies = 12;
        static bool gdiBuffers = true;
        static float sceneTime;
        static Assets? assets;                          // the demo's current assets, offered by the test lambda every frame
        static Assets? offered;
        static Sprite? col, alpha;                      // the scene's sprites, resampled once per asset set

        static long lastTickStamp = Stopwatch.GetTimestamp();
        static long lastTickMs, lastLambdaDriveMs;      // when Tick() last ran / when the canvas lambda last drove it
        static double loopFps = -1;
        static long lastSummaryMs;
        static Label summary = null!;

        static FullForm? full;
        /// <summary>True while the fullscreen bench owns the screen - MainForm.Frame() renders nothing then.</summary>
        public static bool FullscreenOpen => full != null;

        // ------------------------------------------------------------------ the test's canvas
        static int uiThread;                            // the thread that built the strip (the panes' owner)
        /// <summary>Called from the test's render lambda: takes the assets / time for this frame and drives the panes
        /// when the bench's own frame loop is not running (the headless "--shots" capture path renders a test by calling
        /// its lambda directly, without ever going through Frame()).</summary>
        public static void OnCanvasFrame(Ctx c)
        {
            offered = c.A; sceneTime = (float)c.Time;
            if (uiThread != 0 && Environment.CurrentManagedThreadId != uiThread) return;      // a parallel-band thread: the UI thread drives the panes
            long now = Environment.TickCount64;
            if (now - lastTickMs < 60) return;                          // the frame loop is driving the panes already
            if (now - lastLambdaDriveMs < 60) return;                   // several canvas frames in a row: one drive is enough
            lastLambdaDriveMs = now;
            Tick();
            // A headless capture (--shots) calls this lambda once per test and the panes have no frame source of their
            // own, so one frame would be photographed mid-warm-up with a meaningless average. Catch up here; a live run
            // is already ticking every frame and leaves the loop at once.
            for (int i = 0; i < 8 && Warmest() < 12; i++) Tick();
        }

        /// <summary>How few measured frames any pane has - the pane that has sampled least decides whether the strip is warm.</summary>
        static int Warmest()
        {
            int m = int.MaxValue;
            foreach (var p in panes) m = Math.Min(m, p.DrawFrames);
            return m == int.MaxValue ? 0 : m;
        }

        /// <summary>Two short info lines with the current verdict, so the numbers are in the canvas and in the shots.</summary>
        public static void Report(Ctx c)
        {
            c.Info(Headline());
            c.Info("Fullscreen runs one route alone, unthrottled: press the button on the strip (it pauses the bench).");
        }

        /// <summary>The test's canvas frame: the same scene on the bench's own surface (which is presented the raw way,
        /// GetDC + PaintToDevice, by MainForm) plus the verdict in the info panel.</summary>
        public static void Canvas(Ctx c)
        {
            OnCanvasFrame(c);
            Scene(c.Canvas, null, copies * 3);
            Report(c);
        }

        static string Headline()
        {
            var raw = Find(Kind.Raw);
            double Tot(Pane p) => Math.Max(0, p.DrawMs) + Math.Max(0, p.BlitMs);
            if (raw == null || Tot(raw) <= 0) return "measuring ...";
            double r = Tot(raw);
            var pres = Find(Kind.BoxPresent); var paint = Find(Kind.BoxPaint); var zoom = Find(Kind.BoxZoom);
            return $"one frame: raw {r:0.000} ms   box.RedrawNow {Ratio(pres, r)}   box.WM_PAINT {Ratio(paint, r)}   box zoom2 {Ratio(zoom, r)}   loop {loopFps:0} fps";
        }
        static string Ratio(Pane? p, double raw) => p == null || p.DrawFrames == 0 ? "n/a" : $"{Tot2(p) / raw:0.00} x";
        static double Tot2(Pane p) => Math.Max(0, p.DrawMs) + Math.Max(0, p.BlitMs);
        static Pane? Find(Kind k) { foreach (var p in panes) if (p.K == k) return p; return null; }

        // ------------------------------------------------------------------ the strip
        public static Control Build()
        {
            uiThread = Environment.CurrentManagedThreadId;
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = Color.FromArgb(0x20, 0x24, 0x28), Padding = new Padding(4, 2, 4, 2),
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var knobs = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Margin = new Padding(0), BackColor = Color.Transparent };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
            for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            root.Controls.Add(knobs, 0, 0); root.Controls.Add(grid, 0, 1);

            var lblCopies = Cap("copies");
            var tb = new TrackBar { Minimum = 1, Maximum = 64, Value = copies, Width = 200, AutoSize = false, Height = 26, Margin = new Padding(2, 2, 2, 2), TickStyle = TickStyle.None };
            var lblN = Cap(copies.ToString(System.Globalization.CultureInfo.InvariantCulture));
            tb.ValueChanged += (_, _) => { copies = tb.Value; lblN.Text = tb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); };
            var gdi = new CheckBox { Text = "GDI DIB buffers (BitBlt) - off = SetDIBitsToDevice", Checked = gdiBuffers, AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(12, 5, 4, 2) };
            gdi.CheckedChanged += (_, _) => SetGdi(gdi.Checked);
            var fs = new Button { Text = "Fullscreen raw output (pauses the bench)", AutoSize = true, Margin = new Padding(12, 2, 4, 2), FlatStyle = FlatStyle.System };
            fs.Click += (_, _) => OpenFullscreen();
            summary = new Label { Text = "", AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(16, 5, 2, 2) };
            knobs.Controls.Add(lblCopies); knobs.Controls.Add(tb); knobs.Controls.Add(lblN);
            knobs.Controls.Add(gdi); knobs.Controls.Add(fs); knobs.Controls.Add(summary);

            panes.Clear();
            panes.Add(MakePane(grid, Kind.Raw, "1  RAW PictureBox", "GetDC + PaintToDevice + ReleaseDC"));
            panes.Add(MakePane(grid, Kind.BoxPresent, "2  SpriteBox.RedrawNow", "render into Surface + Present()"));
            panes.Add(MakePane(grid, Kind.BoxPaint, "3  SpriteBox WM_PAINT", "Redraw() -> paint message -> blit"));
            panes.Add(MakePane(grid, Kind.BoxZoom, "4  SpriteBox zoom 200%", "same surface, resampled on present"));
            return root;
        }

        static Label Cap(string t) => new Label { Text = t, AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(6, 6, 2, 2) };

        static void SetGdi(bool on)
        {
            gdiBuffers = on;
            foreach (var p in panes)
            {
                if (p.Buffer != null) { p.Buffer.Dispose(); p.Buffer = null; }
                if (p.Box != null) p.Box.GdiSurface = on;
            }
            full?.OnBuffersChanged();
        }

        static Pane MakePane(TableLayoutPanel host, Kind k, string title, string sub)
        {
            var p = new Pane { K = k, Title = title, Sub = sub };
            int col = host.Controls.Count;
            if (k == Kind.Raw)
            {
                // a stock PictureBox: nothing of its own paints the picture - the bench blits into its DC every frame.
                // BackColor is the erase colour WinForms uses when it gets a WM_PAINT (after a resize / uncover);
                // the next frame's blit covers it again, exactly like the original engine did.
                var pic = new PictureBox { Dock = DockStyle.Fill, Margin = new Padding(PaneGap), BackColor = Color.Black, TabStop = false };
                p.Pic = pic; pic.Tag = p;
                host.Controls.Add(pic, col, 0);
            }
            else
            {
                var box = new SpriteBox
                {
                    Dock = DockStyle.Fill, Margin = new Padding(PaneGap), GdiSurface = gdiBuffers,
                    SizeMode = k == Kind.BoxZoom ? SpriteSizeMode.Zoom : SpriteSizeMode.None,
                    SmoothZoom = false, Inertia = false, ViewMenu = false, PanMode = SpritePanMode.None,
                };
                if (k == Kind.BoxZoom)
                {
                    box.ImageSize = new Size(Math.Max(8, box.ClientSize.Width), Math.Max(8, box.ClientSize.Height));
                    box.Zoom = 2.0; box.ViewFilter = SR2D.Filter.Bilinear;
                    // the client can change (the strip reflows with the window): keep the image at the pane size so
                    // this pane draws exactly as many pixels as the other three
                    box.ClientSizeChanged += (_, _) => { var s = box.ClientSize; if (s.Width >= 8 && s.Height >= 8 && box.ImageSize != s) box.ImageSize = s; };
                }
                p.Box = box;
                box.Render += (_, e) => DrawInto(p, e.Surface);
                host.Controls.Add(box, col, 0);
            }
            return p;
        }

        /// <summary>The Render handler of every SpriteBox pane: the scene plus its readout, timed.</summary>
        static void DrawInto(Pane p, Sprite surface)
        {
            long a = Timestamp();
            bool drew = Scene(surface, p);
            if (!drew) return;                          // nothing was drawn - not a frame worth timing
            p.LastDrawMs = Ms(a, Timestamp());
            p.DrawFrames++;
            p.DrawMs = Ema(p.DrawMs, p.LastDrawMs);
        }

        /// <summary>One frame of every pane. Called from MainForm.Frame() (after the canvas rendered) and from the
        /// test lambda when the frame loop is not running.</summary>
        public static void Tick()
        {
            long stamp = Timestamp();
            long now = Environment.TickCount64;
            if (lastTickMs != 0) loopFps = Ema(loopFps, 1000.0 / Math.Max(0.0001, Ms(lastTickStamp, stamp)));
            lastTickStamp = stamp; lastTickMs = now;
            if (offered == null) return;
            if (!ReferenceEquals(assets, offered))
            {
                assets = offered; BuildSprites(offered);
                // the first frame of a new asset set also resamples the scene's two sprites: that is setup cost, not a
                // route's cost, and an EMA that starts there keeps reporting it for dozens of frames (measured: the raw
                // pane showed 0.49 ms against the boxes' 0.24 ms for the SAME scene until the averages were restarted)
                foreach (var p in panes) { p.DrawMs = p.BlitMs = p.PaintMs = -1; p.DrawFrames = 0; }
            }
            foreach (var p in panes) Run(p);
            if (summary != null && now - lastSummaryMs >= 250) { lastSummaryMs = now; summary.Text = Headline(); }
        }

        static void Run(Pane p)
        {
            try
            {
                switch (p.K)
                {
                    case Kind.Raw:
                    {
                        var view = p.Pic!;
                        var sz = view.ClientSize;
                        if (sz.Width < 8 || sz.Height < 8) return;
                        if (p.Buffer != null && (p.Buffer.Width != sz.Width || p.Buffer.Height != sz.Height || p.Buffer.IsGdiSurface != gdiBuffers)) { p.Buffer.Dispose(); p.Buffer = null; }
                        if (p.Buffer == null) p.Buffer = new Sprite(sz.Width, sz.Height, gdiBuffers);
                        var buf = p.Buffer;
                        long a = Timestamp();
                        if (!Scene(buf, p)) break;      // the pane has no size yet - no frame, nothing to time
                        long b = Timestamp();
                        if (view.IsHandleCreated)
                        {
                            IntPtr hdc = GetDC(view.Handle);
                            if (hdc != IntPtr.Zero) { try { buf.PaintToDevice(new HandleRef(view, hdc)); } finally { _ = ReleaseDC(view.Handle, hdc); } }
                        }
                        long c = Timestamp();
                        p.LastDrawMs = Ms(a, b);
                        p.DrawFrames++;
                        p.DrawMs = Ema(p.DrawMs, p.LastDrawMs); p.BlitMs = Ema(p.BlitMs, Ms(b, c));
                        break;
                    }
                    case Kind.BoxPresent:
                    case Kind.BoxZoom:
                    {
                        var box = p.Box!;
                        int before = p.DrawFrames;
                        long a = Timestamp();
                        box.RedrawNow();                    // the Render handler runs here (it times the draw), then Present() blits - no queue
                        double total = Ms(a, Timestamp());
                        if (p.DrawFrames == before) break;   // the renderer did not run - the sample would be a lie
                        p.PaintMs = Ema(p.PaintMs, total);
                        p.BlitMs = Ema(p.BlitMs, Math.Max(0, total - p.LastDrawMs));
                        break;
                    }
                    case Kind.BoxPaint:
                    {
                        var box = p.Box!;
                        int before = p.DrawFrames;
                        box.Redraw();
                        long b = Timestamp();
                        if (box.IsHandleCreated && box.Visible) box.Update();   // WM_PAINT now, synchronously: the Render event and the blit run inside
                        double total = Ms(b, Timestamp());
                        if (p.DrawFrames == before) break;                       // no paint happened - nothing to time
                        p.PaintMs = Ema(p.PaintMs, total);
                        p.BlitMs = Ema(p.BlitMs, Math.Max(0, total - p.LastDrawMs));
                        break;
                    }
                }
            }
            catch (Exception ex) { p.Error = ex.GetType().Name; }
        }

        // ------------------------------------------------------------------ the scene (identical work in every pane)
        static void BuildSprites(Assets a)
        {
            col?.Dispose(); alpha?.Dispose(); col = alpha = null;
            // the scene's sprites are a fixed 64 px so a pane's cost does not depend on the demo's "Sprite size" setup
            col = Resized(a.Color, 64); alpha = Resized(a.Alpha, 64);
        }
        static Sprite Resized(Sprite src, int edge)
            => src.Width == edge && src.Height == edge ? Clone(src) : new Sprite(src, SR2D.Transform.None, edge, edge);
        static Sprite Clone(Sprite src) { var s = new Sprite(src.Width, src.Height); src.Pixels.CopyTo(s.Pixels); return s; }

        /// <summary>Draws one bench frame. FALSE = nothing was drawn (the surface has no size yet, no assets): a frame
        /// like this must never be timed, or a pane that has not been laid out reports a 0 ms route.</summary>
        static bool Scene(Sprite t, Pane? p, int n = -1)
        {
            int w = t.Width, h = t.Height;
            if (w < 8 || h < 8) return false;
            if (col == null) { if (offered == null) return false; BuildSprites(offered); }
            if (col == null) return false;
            if (n < 0) n = copies;
            t.ClearBuffer(unchecked((int)0xFF101418));
            for (int y = 0; y < h; y += 2)                                  // a vertical gradient: a resample shows up on it
            {
                int v = 0x16 + (y * 0x2E) / h;
                t.FillRect(0, y, w, 2, unchecked((int)(0xFF000000u | ((uint)v << 16) | ((uint)(v >> 1) << 8) | 0x24)));
            }
            float cx = w * 0.5f, cy = h * 0.5f + 6f, rx = w * 0.34f, ry = h * 0.30f;
            var s = col!; var al = alpha!;
            for (int i = 0; i < n; i++)
            {
                double a = sceneTime * (0.55 + 0.09 * (i % 5)) + i * (Math.PI * 2 / Math.Max(1, n));
                int x = (int)(cx + (float)Math.Cos(a) * rx) - s.Width / 2;
                int y = (int)(cy + (float)Math.Sin(a * 1.7) * ry) - s.Height / 2;
                if ((i & 3) == 3) t.Draw(al, x, y, SR2D.Op.AlphaBlend);
                else t.Draw(s, x, y, SR2D.Op.Paint);
            }
            t.DrawRect(0, 0, w - 1, h - 1, unchecked((int)0xFF485868));
            if (p != null) Readout(t, p);
            return true;
        }

        static void Readout(Sprite t, Pane p)
        {
            const int fg = unchecked((int)0xFFE8F4FF), dim = unchecked((int)0xFFA8C0D8), bg = unchecked((int)0xFF0A0E12);
            int sc = p.Scale, lh = 16 * sc;
            t.DrawText(4, 3, p.Title, fg, bg, sc);
            t.DrawText(4, 3 + lh, p.Sub, dim, bg, sc);
            int y = 3 + 2 * lh;
            if (p.Error.Length > 0) { t.DrawText(4, y, p.Error, unchecked((int)0xFFFF8080), bg, sc); return; }
            if (p.DrawFrames == 0) { t.DrawText(4, y, "measuring ...", dim, bg, sc); return; }
            // the frame count is printed while the average is still short: a headless single-frame capture of this strip
            // would otherwise show a figure the reader cannot tell apart from a settled measurement
            string warm = p.DrawFrames < 30 ? $" [{p.DrawFrames} frames]" : "";
            t.DrawText(4, y, $"draw {p.DrawMs:0.000} + blit {Math.Max(0, p.BlitMs):0.000} ms{warm}", fg, bg, sc);
            double tot = Math.Max(0, p.DrawMs) + Math.Max(0, p.BlitMs);
            var extra = p.Extra.Length > 0 ? p.Extra : $"{1000.0 / Math.Max(0.001, tot):0} fps if this route ran alone";
            t.DrawText(4, y + lh, extra, dim, bg, sc);
        }

        static double Ema(double old, double nw) => old < 0 ? nw : old * 0.85 + nw * 0.15;
        static long Timestamp() => Stopwatch.GetTimestamp();
        static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;

        // ------------------------------------------------------------------ fullscreen (option 2)
        static void OpenFullscreen()
        {
            if (full != null) { full.Activate(); return; }
            if (offered == null) return;                       // the test has not rendered a frame yet: no assets to show
            assets = offered; BuildSprites(offered);
            var f = new FullForm();
            full = f;
            f.FormClosed += (_, _) => { full = null; };
            f.Show(); f.Activate();
        }
        internal static void TickFullscreen() => full?.Tick();

        /// <summary>A plain, borderless, screen-sized WinForms Form (no SR2D chrome, no SpriteForm) that runs ONE output
        /// route alone, as fast as the message pump allows, and shows the frame rate. Click or Esc closes it.</summary>
        sealed class FullForm : Form
        {
            readonly SpriteBox box = new SpriteBox { Dock = DockStyle.Fill, Visible = false };
            readonly Pane hud = new Pane { Scale = 2 };
            Sprite? buf;
            int route;                                       // 0 raw blit, 1 box.RedrawNow, 2 box WM_PAINT
            int frames; long windowStartMs; double fps = -1;

            public FullForm()
            {
                Text = "SR2D output bench - fullscreen";
                FormBorderStyle = FormBorderStyle.None;
                AutoScaleMode = AutoScaleMode.None;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.Black;
                KeyPreview = true;
                var scr = Screen.PrimaryScreen;
                Bounds = scr != null ? scr.Bounds
                     : Screen.AllScreens.Length > 0 ? Screen.AllScreens[0].Bounds
                     : new Rectangle(0, 0, 1280, 720);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
                box.GdiSurface = gdiBuffers;
                box.Render += (_, e) => { long a = Timestamp(); Scene(e.Surface, hud); hud.LastDrawMs = Ms(a, Timestamp()); hud.DrawFrames++; hud.DrawMs = Ema(hud.DrawMs, hud.LastDrawMs); };
                box.MouseDown += (_, _) => Close();
                Controls.Add(box);
                windowStartMs = Environment.TickCount64;
                SetRoute(0);
            }

            public void OnBuffersChanged() { buf?.Dispose(); buf = null; box.GdiSurface = gdiBuffers; hud.DrawMs = hud.BlitMs = hud.PaintMs = -1; }

            string RouteName => route switch { 1 => "SpriteBox.RedrawNow(): render into Surface + Present()", 2 => "SpriteBox Redraw() -> WM_PAINT", _ => "PictureBox way: GetDC + PaintToDevice + ReleaseDC" };

            public void Tick() { try { TickCore(); } catch (Exception ex) { hud.Error = ex.GetType().Name + ": " + ex.Message; } }

            void TickCore()
            {
                if (offered == null) return;
                if (!ReferenceEquals(assets, offered))
                {
                    assets = offered; BuildSprites(offered);
                    hud.DrawMs = hud.BlitMs = hud.PaintMs = -1; hud.DrawFrames = 0;      // the resample of the new set is not the route's cost
                }
                if (route == 0)
                {
                    var sz = ClientSize;
                    if (sz.Width < 8 || sz.Height < 8) return;
                    if (buf != null && (buf.Width != sz.Width || buf.Height != sz.Height || buf.IsGdiSurface != gdiBuffers)) { buf.Dispose(); buf = null; }
                    if (buf == null) buf = new Sprite(sz.Width, sz.Height, gdiBuffers);
                    var b0 = buf;
                    long a = Timestamp();
                    if (!Scene(b0, hud)) return;         // no frame drawn - the fps window must not count it
                    long mid = Timestamp();
                    IntPtr hdc = GetDC(Handle);
                    if (hdc != IntPtr.Zero) { try { b0.PaintToDevice(new HandleRef(this, hdc)); } finally { _ = ReleaseDC(Handle, hdc); } }
                    hud.LastDrawMs = Ms(a, mid);
                    hud.DrawFrames++;
                    hud.DrawMs = Ema(hud.DrawMs, hud.LastDrawMs); hud.BlitMs = Ema(hud.BlitMs, Ms(mid, Timestamp()));
                }
                else if (route == 1)
                {
                    long a = Timestamp();
                    box.RedrawNow();                                   // the Render handler runs here and times the draw, then Present() blits - no queue
                    double total = Ms(a, Timestamp());
                    hud.PaintMs = Ema(hud.PaintMs, total);
                    hud.BlitMs = Ema(hud.BlitMs, Math.Max(0, total - hud.LastDrawMs));
                }
                else
                {
                    box.Redraw();
                    long b = Timestamp();
                    box.Update();                                  // WM_PAINT now: the Render event and the blit run inside
                    double total = Ms(b, Timestamp());
                    hud.PaintMs = Ema(hud.PaintMs, total);
                    hud.BlitMs = Ema(hud.BlitMs, Math.Max(0, total - hud.LastDrawMs));
                }
                frames++;
                long now = Environment.TickCount64;
                if (now - windowStartMs >= 250)
                {
                    fps = frames * 1000.0 / Math.Max(1, now - windowStartMs);
                    frames = 0; windowStartMs = now;
                    hud.Extra = $"FPS {fps:0.0}   Left/Right = copies ({copies}), Tab = next route, G = buffers ({(gdiBuffers ? "DIB" : "plain")}), click / Esc = close";
                }
            }

            void SetRoute(int r)
            {
                route = Math.Clamp(r, 0, 2);
                box.Visible = route != 0;
                hud.Title = $"FULLSCREEN  route {route + 1} / 3   {RouteName}";
                hud.Sub = "the bench's own rendering is paused while this is up; uncapped, so one core is busy by design";
                hud.Extra = "measuring ...";
                hud.DrawMs = hud.BlitMs = hud.PaintMs = -1;
                hud.LastDrawMs = 0;
                hud.Error = "";
                Invalidate();
            }

            /// <summary>Repaints the last frame after a resize or an uncover (the raw route has no WM_PAINT of its own).</summary>
            protected override void OnPaint(PaintEventArgs e)
            {
                if (route == 0 && buf != null) buf.PaintToGraphics(e.Graphics);
                else base.OnPaint(e);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { buf?.Dispose(); buf = null; box.Dispose(); }
                base.Dispose(disposing);
            }
            protected override void OnMouseDown(MouseEventArgs e) => Close();
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                switch (keyData)
                {
                    case Keys.Escape: case Keys.Q: Close(); return true;
                    case Keys.Tab: SetRoute((route + 1) % 3); return true;
                    case Keys.D1: SetRoute(0); return true;
                    case Keys.D2: SetRoute(1); return true;
                    case Keys.D3: SetRoute(2); return true;
                    case Keys.Left: copies = Math.Max(1, copies - 1); return true;
                    case Keys.Right: copies = Math.Min(64, copies + 1); return true;
                    case Keys.G: SetGdi(!gdiBuffers); return true;
                    default: return base.ProcessCmdKey(ref msg, keyData);
                }
            }
        }
    }
}
