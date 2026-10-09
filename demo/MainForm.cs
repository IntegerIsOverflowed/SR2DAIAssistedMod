using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // ImplicitUsings also brings System.Threading.Timer

#pragma warning disable CA2213 // the control fields are added to Controls and disposed by WinForms with the form
namespace Sr2d64CSport
{
    /// <summary>
    /// Interactive demo / benchmark: pick a test, drag sliders, read FPS / ms per frame /
    /// ms per op. "Run suite" times every test headlessly and writes a CSV you can
    /// diff between DLL builds.
    /// </summary>
    internal sealed class MainForm : SpriteForm
    {
        // --- render surface ------------------------------------------------------
        sealed class Surface : Control
        {
            public Surface() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.Selectable, true); }
            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e) { Painter?.Invoke(e.Graphics); }
            public Action<Graphics>? Painter;
        }

        System.Windows.Forms.Panel? settingsHost;                // the test's extra settings control (e.g. the curve editor), shown in the Test tab
        readonly Dictionary<DemoTest, Dictionary<Param, int>> savedParams = new Dictionary<DemoTest, Dictionary<Param, int>>();
        static readonly Dictionary<Param, int> GenericDefaults = new Dictionary<Param, int>
        {
            [Param.Count] = 1, [Param.Angle] = 0, [Param.Scale] = 100, [Param.Blend] = 128, [Param.Brite] = 100, [Param.Z] = 120, [Param.DotStep] = 0,
            [Param.Op] = 0, [Param.Smooth] = 0, [Param.NotMask] = 0, [Param.Xor] = 0, [Param.Mouse] = 0, [Param.MaskBits] = 1, [Param.Time] = 1,
            [Param.Grid] = 64, [Param.Speed] = 6, [Param.GridOn] = 1, [Param.OffsetX] = 0, [Param.OffsetY] = 0,
        };
        static readonly Dictionary<Param, (int, int)> GenericRanges = new Dictionary<Param, (int, int)>
        {
            [Param.Count] = (1, 64), [Param.Angle] = (0, 360), [Param.Scale] = (5, 400), [Param.Blend] = (0, 255), [Param.Brite] = (-200, 200), [Param.Z] = (1, 600), [Param.DotStep] = (0, 8),
            [Param.Grid] = (8, 256), [Param.Speed] = (1, 100), [Param.OffsetX] = (-256, 256), [Param.OffsetY] = (-256, 256),
        };
        // animation clock: advances only while "Animate" is ticked; Ctx.Time is 0 when it is off (so the Angle slider is absolute)
        double animTime;
        // orbit camera (voxel tests): left drag = yaw / pitch, middle drag = pan, wheel = zoom (Scale slider)
        float camYaw = 0.8f, camPitch = 0.55f, camPanX, camPanY; bool orbiting, panning; int lastMx, lastMy;
        // PaintByMouse tests: the pen paints while the left button is down (ctx.PenDown / PenX / PenY)
        bool painting; int penX, penY;
        // resizable object (DrawScaled): edge being dragged: bit 1 = left, 2 = right, 4 = top, 8 = bottom
        int edgeDrag; float scaleY = 1f;
        float pivotX = -1, pivotY = -1;                 // rotation pivot in object units (source pixels, or the test's ObjectSize units; -1 = centre) for MouseRotates tests
        bool shadowDrag;                                // ShadowByMouse tests: dragging sets Z (distance) + Angle
        // rotate-by-mouse (DrawRotate with Animate off): dragging outside the object turns it towards the cursor
        bool rotating;
        static readonly string[] GenericOpNames = Enum.GetNames<SR2D.Op>().Skip(1).Take(9).ToArray();

        // state
        Ctx ctx = new Ctx();
        readonly TransformFrame frame = new TransformFrame();       // editable transform frame for DemoTest.Frame tests
        Assets? assets;
        Sprite? canvas, temp;
        List<DemoTest> tests = new List<DemoTest>();
        DemoTest? current;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<int, Sprite> bandTemps = new Dictionary<int, Sprite>();   // per-band scratch for DrawParallel

        void RunTest(DemoTest t)
        {
            if (!chkPar.Checked || canvas == null) { t.Run(ctx); return; }
            canvas.DrawParallel(view =>
            {
                var c2 = ctx.Clone();
                c2.Canvas = view;
                lock (bandTemps)
                {
                    if (!bandTemps.TryGetValue(view.LockRect.Top, out var tmp)) { tmp = new Sprite(temp!.Width, temp.Height); bandTemps[view.LockRect.Top] = tmp; }
                    c2.Temp = tmp;
                }
                t.Run(c2);
            });
        }
        double fpsAccumMs, renderAccumMs, presentAccumMs; int fpsFrames; double lastFpsUpdate;
        double emaRender = -1;
        string statsLine = "";                          // first line of the info panel: fps / timing, or the slow-test status
        /// <summary>
        /// Status line of the info panel (top-left of the viewport - there is no status label under the canvas any more).
        /// <paramref name="show"/> = redraw the panel and present right away (slow tests: the frame is not re-rendered,
        /// so the message would otherwise wait for the next one).
        /// </summary>
        void SetStatus(string text, bool show = false)
        {
            statsLine = text;
            if (show && canvas != null && current != null && !inSuite)
            {   // the panel is repainted on top of the last one: blank that area first so the old text does not show through
                canvas.SetLockRect();
                if (!lastPanel.IsEmpty) canvas.ClearRect(lastPanel.Left, lastPanel.Right, lastPanel.Top, lastPanel.Bottom, unchecked((int)0xFF101418));
                DrawInfoPanel(false);
                if (chkPresent.Checked) Present();
            }
        }
        int mouseX = 256, mouseY = 256;
        int backdropX, backdropY;                                               // grabbable checker offset (ctx.BackdropX/Y), dragged with the hand cursor
        // dragging: the "object" (sprite / light / mask) sits at mouseX/mouseY; a press inside
        // its hit rectangle starts a drag, the offset keeps it from jumping under the cursor.
        bool dragging; int dragDx, dragDy;

        // --- the window: every visible piece is an SR2D control (SpriteControls*.cs); the only plain Control is the
        // render surface the canvas is presented on. Layout = WinForms docking inside SpritePanels / SpriteStackPanels.
        readonly Surface surf = new Surface();
        readonly SpritePanel stripHost = new SpritePanel { Style = PanelStyle.Flat, Padding = Padding.Empty, Visible = false, Dock = DockStyle.Top, Height = 0 };   // hosts DemoTest.ControlStrip above the canvas
        readonly Dictionary<DemoTest, Control> strips = new Dictionary<DemoTest, Control>();
        readonly SpriteListBox lstTests = new SpriteListBox { ItemHeight = 18, Dock = DockStyle.Fill };
        readonly List<DemoTest> listItems = new List<DemoTest>();       // one per list row (group headers are DemoTests with Group == "")
        readonly SpriteTextBox txtFilter = new SpriteTextBox { Dock = DockStyle.Fill };      // search filter over the test list (name, group, description)
        readonly SpriteLabel lblFilterHint = new SpriteLabel { Style = LabelStyle.Muted, AutoSize = false, Width = 58, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight };
        readonly SpriteTextView lblDesc = new SpriteTextView { WordWrap = true, Dock = DockStyle.Bottom, Height = 170 };
        readonly SpriteTextView log = new SpriteTextView { Dock = DockStyle.Bottom, Height = 150, MaxLines = 4000 };
        // "Code" view: the selected test's C# (lifted from the embedded Tests.cs) shown in place of the canvas
        readonly SpritePanel codePanel = new SpritePanel { Style = PanelStyle.Flat, Padding = Padding.Empty, Visible = false, Dock = DockStyle.Fill };
        readonly SpriteTextView codeBox = new SpriteTextView { LineNumbers = true, Dock = DockStyle.Fill };
        string codeText = "";
        readonly SpriteButton btnCodeCopy = new SpriteButton { Size = new Size(120, 30) }, btnCodeBack = new SpriteButton { Size = new Size(190, 30), Accented = true };
        bool codeShown;
        bool running, inSuite;

        // parameters: SpriteSlider (ParamSlider keeps the integer Value / Minimum / Maximum the bench uses), SpriteCombo,
        // SpriteToggle in the check-box style, SpriteRadio / SpriteToggle for the bit boxes
        readonly ParamSlider tbCount = new ParamSlider(), tbAngle = new ParamSlider(), tbScale = new ParamSlider(), tbBlend = new ParamSlider(), tbBrite = new ParamSlider(), tbZ = new ParamSlider(), tbDot = new ParamSlider(), tbGrid = new ParamSlider(), tbSpeed = new ParamSlider(), tbOffX = new ParamSlider(), tbOffY = new ParamSlider();
        readonly SpriteCombo cbOp = new SpriteCombo(), cbCanvas = new SpriteCombo(), cbSprite = new SpriteCombo(), cbSimd = new SpriteCombo(), cbAssets = new SpriteCombo();
        readonly SpriteToggle chkSmooth = Tick(), chkNot = Tick(), chkXor = Tick(), chkFollow = Tick(), chkAnim = Tick(), chkClear = Tick(), chkVsync = Tick(), chkPresent = Tick(), chkGdi = Tick(), chkPar = Tick(), chkRealFont = Tick(), chkGridLines = Tick();
        static SpriteToggle Tick() => new SpriteToggle { Style = ToggleStyle.CheckBox, Size = new Size(280, 24) };
        // bit boxes (mask bits / lighting tier / projection views ...): rebuilt per test, check boxes or radio buttons
        readonly List<SpriteClickable> bitBoxes = new List<SpriteClickable>();
        readonly SpriteStackPanel bitRow = new SpriteStackPanel { Orientation = Orientation.Horizontal, Wrap = true, AutoSize = true, Gap = 4, Padding = Padding.Empty, Stretch = false };
        readonly SpriteStackPanel bitField = new SpriteStackPanel { AutoSize = true, Gap = 2, Padding = Padding.Empty };
        readonly SpriteLabel lblMask = new SpriteLabel { AutoSize = false, Height = 16 };
        static readonly string[] MaskNames = { "circle", "diamond", "stripes", "checker" };
        readonly SpriteButton btnSuite = new SpriteButton(), btnCopy = new SpriteButton(), btnSave = new SpriteButton(), btnReset = new SpriteButton(), btnFile = new SpriteButton(), btnMeasure = new SpriteButton(), btnCode = new SpriteButton();
        readonly SpriteToggle chkProgress = Tick();      // slow tests: show phases / partial renders while a frame is being made
        readonly SpriteNumeric numFps = new SpriteNumeric { Minimum = 1, Maximum = 1000, Value = 60, Decimals = 0, Size = new Size(70, 24) };
        readonly Timer resizeTimer = new Timer();
        readonly SpriteProgress prog = new SpriteProgress { Dock = DockStyle.Bottom, Height = 10, Visible = false, Minimum = 0, Maximum = 100 };
        // the right-hand pane: "Test" = only what the selected test reads, "Setup" = canvas / sprites / present / suite
        readonly SpriteTabControl rightTabs = new SpriteTabControl { Dock = DockStyle.Right, Width = 320 };
        SpriteStackPanel testStack = null!, setupStack = null!;
        readonly SpriteLabel lblNoParams = new SpriteLabel { Style = LabelStyle.Muted, AutoSize = false, Height = 40, WordWrap = true, Text = "This test has no parameters on the panel; it reacts to the mouse on the canvas only (or to nothing)." };
        // which control shows a Param (a slider, a toggle, or the field panel that holds a combo / the bit boxes)
        readonly Dictionary<Param, Control> paramMain = new Dictionary<Param, Control>();
        readonly Dictionary<Control, string> titles = new Dictionary<Control, string>();          // generic captions (restored when a test has none of its own)
        readonly Dictionary<Control, SpriteLabel> fieldTitles = new Dictionary<Control, SpriteLabel>();   // combo / bit row -> its title label
        Param shownParams = (Param)(-1);                 // what the Test tab shows right now
        Param staticUsed;                                // parameters the selected test's source mentions (CodeView) - the tab shows them before the first frame ran
        static readonly Color Back = SpriteControlBase.DefaultBack;

        // WinForms' own WM_INPUTLANGCHANGE handler builds a CultureInfo for the new layout and crashes under
        // InvariantGlobalization (the demo builds with it off, but a published trimmed build may not) - swallow it here too.
        protected override void WndProc(ref Message m) { if (m.Msg != 0x0051) base.WndProc(ref m); }

        [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm")] static extern uint timeEndPeriod(uint ms);
        [StructLayout(LayoutKind.Sequential)] struct NativeMessage { public IntPtr Handle; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public Point Location; }
        [DllImport("user32")] static extern int PeekMessage(out NativeMessage msg, IntPtr hWnd, uint filterMin, uint filterMax, uint flags);
        [DllImport("user32")] static extern IntPtr DispatchMessage(ref NativeMessage msg);
        [DllImport("user32")] static extern bool TranslateMessage(ref NativeMessage msg);   // WM_KEYDOWN -> WM_CHAR: without it every key the inline drain touches is silently lost
        const uint PM_REMOVE = 1;
        [DllImport("user32")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32")] static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        public MainForm()
        {
            Text = "SR2D demo";
            WindowState = FormWindowState.Maximized;        // starts maximised as a window (the taskbar stays); the sprite title bar is the base class's
            MinimumSize = new Size(1100, 700);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = Back; ForeColor = Color.Gainsboro;
            BuildUi();
            if (!Caps.Loaded)
            {
                MessageBox.Show(Caps.Error + "\n\nPut SR2D64.dll next to SR2DDemo.exe.", "SR2D Demo", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Load += (_, _) => Close();
                return;
            }
            ReloadTests();
            RebuildAssets();
            _ = timeBeginPeriod(1);
            Shown += (_, _) =>
            {
                RebuildCanvas();          // "fit to window" needs the final layout size
                if (ShotsDir == null) return;
                try { RunShots(ShotsDir); } catch (Exception ex) { Log("shots failed: " + ex); }
                Close();
            };
            // classic "render while the message queue is empty" game loop - no timer granularity limits. A moving mouse
            // used to starve it completely: WM_MOUSEMOVE / WM_SETCURSOR arrive at the mouse poll rate (up to 1000/s), the
            // queue never emptied and the fps fell to the message rate while the pointer was over the canvas. Now only
            // real input / paint / timer messages hand control back to the main pump; a pending flood of move messages is
            // coalesced and dispatched inline every 4 frames, so the fps stays at full speed with a few ms of latency.
            running = true;
            Application.Idle += (_, _) =>
            {
                while (running && !inSuite)
                {
                    if (PeekMessage(out _, IntPtr.Zero, 0, 0x01FF, 0) != 0 || PeekMessage(out _, IntPtr.Zero, 0x0201, 0x7FFFFFFF, 0) != 0) break;   // real work pending
                    for (int f = 0; f < 4 && running && !inSuite; f++)
                    {
                        Frame();
                        if (PeekMessage(out _, IntPtr.Zero, 0, 0x01FF, 0) != 0 || PeekMessage(out _, IntPtr.Zero, 0x0201, 0x7FFFFFFF, 0) != 0) break;
                    }
                    // only moves left: coalesce + dispatch here, keep rendering. TranslateMessage is NOT optional:
                    // the drain also sees the keydowns that arrived during the last Frame(), and without it no WM_CHAR
                    // is generated - the search box (and every text control) lost most keystrokes (a character slipped
                    // through only when it happened to be pumped by the main loop)
                    while (PeekMessage(out var nm, IntPtr.Zero, 0, 0, PM_REMOVE) != 0) { TranslateMessage(ref nm); DispatchMessage(ref nm); }
                }
            };;
        }

        // ------------------------------------------------------------------- UI
        void BuildUi()
        {
            SuspendLayout();
            // ---- header: which DLL / kernels - lives in the title bar now (SpriteForm.SetTitleTags);
            // the FULL path goes to the log, the bar shows the path relative to the exe
            Log("dll: " + Caps.Summary);
            try { TitleIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            UpdateTitleTags();

            // ---- left: filter + test list + description
            var left = new SpritePanel { Style = PanelStyle.Flat, Padding = new Padding(4, 2, 2, 4), Dock = DockStyle.Left, Width = 340 };
            var filterRow = new SpritePanel { Style = PanelStyle.Flat, Padding = Padding.Empty, Dock = DockStyle.Top, Height = 28 };
            // search filter: words are ANDed, matched case-insensitively against group + name + description; Esc clears,
            // Enter / Down jumps into the list; Ctrl+F from anywhere focuses the box
            txtFilter.Placeholder = "Search tests (Ctrl+F)  -  words: rotate shear, group: voxel ...";
            txtFilter.TextChanged += (_, _) => ApplyFilter();
            txtFilter.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) { txtFilter.Text = ""; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { lstTests.Focus(); e.SuppressKeyPress = true; }
            };
            var btnFilterClear = new SpriteButton { Text = "\u00d7", Shape = ButtonShape.Square, Size = new Size(24, 24), Dock = DockStyle.Right, Enabled = false };
            btnFilterClear.Click += (_, _) => { txtFilter.Text = ""; txtFilter.Focus(); };
            txtFilter.TextChanged += (_, _) => btnFilterClear.Enabled = txtFilter.Text.Length > 0;   // nothing to clear while empty
            filterRow.Controls.Add(txtFilter); filterRow.Controls.Add(lblFilterHint); filterRow.Controls.Add(btnFilterClear);
            lstTests.SelectedIndexChanged += (_, _) => SelectTest();
            lstTests.Margin = new Padding(0, 2, 0, 2);
            left.Controls.Add(lstTests); left.Controls.Add(filterRow); left.Controls.Add(lblDesc);   // the Fill control first: docking runs back to front

            // ---- centre: control strip / surface (or the code view) / progress bar
            var mid = new SpritePanel { Style = PanelStyle.Flat, Padding = new Padding(2, 2, 2, 2), Dock = DockStyle.Fill };
            surf.Dock = DockStyle.Fill; surf.BackColor = Color.Black; surf.Painter = _ => { };
            surf.MouseMove += (_, e) => SurfaceMove(e);
            surf.MouseDown += (_, e) => SurfaceDown(e);
            surf.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) { dragging = false; orbiting = false; edgeDrag = 0; rotating = false; shadowDrag = false; painting = false; frame.MouseUp(); } if (e.Button == MouseButtons.Middle) panning = false; };
            surf.MouseWheel += (_, e) => SurfaceWheel(e);
            surf.MouseEnter += (_, _) => surf.Focus();     // wheel events go to the focused control
            surf.MouseLeave += (_, _) => { if (!dragging && !orbiting && edgeDrag == 0) surf.Cursor = Cursors.Default; };
            // code view (same cell as the canvas; swapped in by the "Code" button / F2)
            var codeHead = new SpriteStackPanel { Orientation = Orientation.Horizontal, Dock = DockStyle.Top, Height = 38, Gap = 6, Padding = new Padding(4), Stretch = false };
            btnCodeBack.Text = "Back to the demo (F2)"; btnCodeBack.Click += (_, _) => ShowCode(false);
            btnCodeCopy.Text = "Copy code"; btnCodeCopy.Click += (_, _) => { if (codeText.Length > 0) Clipboard.SetText(codeText); };
            var codeInfo = new SpriteLabel { Style = LabelStyle.Muted, AutoSize = false, Size = new Size(700, 30), Text = "C# of the selected test as it runs (demo/Tests.cs); helpers it calls follow below. Select another test in the list to see its code." };
            codeHead.Controls.Add(btnCodeBack); codeHead.Controls.Add(btnCodeCopy); codeHead.Controls.Add(codeInfo);
            codePanel.Controls.Add(codeBox); codePanel.Controls.Add(codeHead);
            mid.Controls.Add(surf); mid.Controls.Add(codePanel); mid.Controls.Add(stripHost); mid.Controls.Add(prog);
            ColorDemo.CanvasSurface = surf;                           // the colour test embeds its dialog on the surface
            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.F2) { ShowCode(!codeShown); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.F) { txtFilter.Focus(); txtFilter.SelectAll(); e.Handled = true; e.SuppressKeyPress = true; }
            };

            // ---- right: the Test tab (only the parameters the selected test reads) and the Setup tab
            var pageTest = rightTabs.AddPage("Test"); var pageSetup = rightTabs.AddPage("Setup");
            pageTest.Padding = pageSetup.Padding = new Padding(4);
            testStack = new SpriteStackPanel { Dock = DockStyle.Fill, Gap = 6, Padding = new Padding(4, 4, 4, 4) };
            setupStack = new SpriteStackPanel { Dock = DockStyle.Fill, Gap = 6, Padding = new Padding(4, 4, 4, 4) };
            pageTest.Controls.Add(testStack); pageSetup.Controls.Add(setupStack);

            Combo(setupStack, "Canvas (drawn 1:1 from the top-left of the preview)", cbCanvas, new[] { "fit to window", "512 x 512", "1024 x 1024", "1280 x 720", "1920 x 1080", "2560 x 1440" }, 0, (_, _) => RebuildCanvas());
            Combo(setupStack, "Sprite size", cbSprite, new[] { "64", "128", "256", "512" }, 2, (_, _) => RebuildAssets());
            Combo(setupStack, "Test picture", cbAssets, new[] { "Lenna (photo + normal map)", "Bricks (legacy procedural)" }, 0, (_, _) => RebuildAssets());
            resizeTimer.Interval = 150;
            resizeTimer.Tick += (_, _) => { resizeTimer.Stop(); if (cbCanvas.SelectedIndex == 0) RebuildCanvas(); };
            surf.Resize += (_, _) => { resizeTimer.Stop(); resizeTimer.Start(); };
            Combo(setupStack, "SIMD kernels", cbSimd, new[] { "auto", "force SSE2", "force AVX2" }, 0, (_, _) => ApplySimd());
            cbSimd.Enabled = Caps.HasSimdInfo;
            Check(setupStack, chkClear, "Clear canvas each frame", true);
            Check(setupStack, chkPresent, "Present to screen (PaintToDevice)", true);
            Check(setupStack, chkGdi, "Canvas = GDI surface (BitBlt present)", true);
            chkGdi.CheckedChanged += (_, _) => RebuildCanvas();
            Check(setupStack, chkPar, "DrawParallel (one band per core)", false);
            // FrameInset = false puts the row's children on exactly the column of the stack's direct rows: the nested
            // row's own 2 px frame inset used to offset the "Limit fps to" checkbox 2 px to the right, and Padding
            // cannot cancel it (DisplayRectangle hard-codes the inset, it does not read Padding - the old
            // negative-Padding "fix" was a no-op, which is why the misalignment survived its fix)
            var fpsRow = new SpriteStackPanel { Orientation = Orientation.Horizontal, Height = 24, Gap = 6, Padding = new Padding(0), FrameInset = false, Stretch = false };   // 24 = the toggles' height (26 left a 2 px gap before the next row)
            chkVsync.Text = "Limit fps to"; chkVsync.Width = 110; chkVsync.Checked = false;
            fpsRow.Controls.Add(chkVsync); fpsRow.Controls.Add(numFps);
            setupStack.Controls.Add(fpsRow);
            Check(setupStack, chkRealFont, "Real font on the text panes (Segoe UI / Consolas)", true);
            chkRealFont.CheckedChanged += (_, _) => ApplyTextFonts();
            var suiteRow = new SpriteStackPanel { Orientation = Orientation.Horizontal, Wrap = true, AutoSize = true, Gap = 4, Padding = new Padding(0), FrameInset = false, Stretch = false };
            Btn(suiteRow, btnSuite, "Run suite (all tests)", (_, _) => RunSuite());
            Btn(suiteRow, btnCopy, "Copy log", (_, _) => { if (log.LineCount > 0) Clipboard.SetText(log.Text); });
            Btn(suiteRow, btnSave, "Save CSV", (_, _) => SaveCsv());
            setupStack.Controls.Add(suiteRow);
            setupStack.Controls.Add(new SpriteLabel { Style = LabelStyle.Muted, AutoSize = false, Height = 60, WordWrap = true, Text = "The Test tab shows only the parameters the selected test reads: the ones its source mentions, plus anything it touches while it runs." });

            testStack.Controls.Add(lblNoParams);
            Combo(testStack, "Blend Op", cbOp, GenericOpNames, 0, null, Param.Op);
            Slider(testStack, "Count (repetitions / frame)", tbCount, 1, 64, 1, Param.Count);
            Slider(testStack, "Angle (deg)", tbAngle, 0, 360, 0, Param.Angle);
            Slider(testStack, "Scale (x0.05 .. x4)", tbScale, 5, 400, 100, Param.Scale);
            Slider(testStack, "Blend factor", tbBlend, 0, 255, 128, Param.Blend);
            Slider(testStack, "Brightness (x0.01, -2..2)", tbBrite, -200, 200, 100, Param.Brite);
            Slider(testStack, "Light Z", tbZ, 1, 600, 120, Param.Z);
            Slider(testStack, "Line dot step", tbDot, 0, 8, 0, Param.DotStep);
            Slider(testStack, "Grid cell (px)", tbGrid, 8, 256, 64, Param.Grid);
            tbGrid.PowersOfTwo = true;                                          // 8 16 32 64 128 256 only - no leftover pixels
            Slider(testStack, "Animation speed (x8 px/s)", tbSpeed, 1, 100, 6, Param.Speed);
            Slider(testStack, "Offset X (px)", tbOffX, -256, 256, 0, Param.OffsetX);
            Slider(testStack, "Offset Y (px)", tbOffY, -256, 256, 0, Param.OffsetY);
            // bit boxes (mask bits by default; tests may rename them or turn them into radio buttons); wraps onto several rows
            lblMask.Text = "Mask bits:"; titles[bitField] = "Mask bits:"; fieldTitles[bitField] = lblMask;
            bitField.Controls.Add(lblMask); bitField.Controls.Add(bitRow);
            BuildBitBoxes(MaskNames, false);
            testStack.Controls.Add(bitField); paramMain[Param.MaskBits] = bitField;
            Check(testStack, chkNot, "NotMask (invert)", false, Param.NotMask);
            Check(testStack, chkGridLines, "Grid lines", true, Param.GridOn);
            Check(testStack, chkSmooth, "Smooth (AA / bilinear / point light)", false, Param.Smooth);
            Check(testStack, chkXor, "XOR lines", false, Param.Xor);
            Check(testStack, chkAnim, "Animate (off = time stands still at 0)", true, Param.Time);
            Check(testStack, chkFollow, "Follow the mouse (off: drag it, right click = jump)", false, Param.Mouse);
            var btnRow = new SpriteStackPanel { Orientation = Orientation.Horizontal, Wrap = true, AutoSize = true, Gap = 4, Padding = new Padding(-2, 0, -2, 0), Stretch = false };
            Btn(btnRow, btnReset, "Reset params", (_, _) => ResetParams());
            Btn(btnRow, btnCode, "Code (F2)", (_, _) => ShowCode(!codeShown));
            Btn(btnRow, btnFile, "Open file...", (_, _) => OpenTestFile()); btnFile.Visible = false;
            testStack.Controls.Add(btnRow);
            // slow tests only
            Btn(testStack, btnMeasure, "Measure fps (render N frames)", (_, _) => StartMeasure()); btnMeasure.Visible = false;
            Check(testStack, chkProgress, "Show progress (phases, partial renders)", true); chkProgress.Visible = false;

            // ---- bottom: log
            log.FollowTail = true;
            ApplyTextFonts();

            // the window chrome comes from SpriteForm (thin title bar: minimise / maximise / full screen / close,
            // right click = window menu); dock order (WinForms docks back to front): mid fills what the edges leave
            Controls.Add(mid); Controls.Add(rightTabs); Controls.Add(left); Controls.Add(log);
            ResumeLayout(true);
        }

        /// <summary>Real fonts on the reading panes (description, log, code) when the box in Setup is ticked; the pixel font otherwise.</summary>
        void ApplyTextFonts()
        {
            bool real = chkRealFont.Checked;
            lblDesc.TextFontFamily = real ? "Segoe UI" : ""; lblDesc.TextFontSize = 13;
            log.TextFontFamily = codeBox.TextFontFamily = real ? "Consolas" : ""; log.TextFontSize = codeBox.TextFontSize = 12;
        }

        static void Btn(Control parent, SpriteButton b, string text, EventHandler onClick)
        {
            b.Text = text; b.Size = new Size(Sprite.MeasureText(text).Width + 22, 26); b.Click += onClick;
            parent.Controls.Add(b);
        }
        /// <summary>A titled combo: a small stack (title label over the combo) so the pair shows / hides as one.</summary>
        void Combo(Control parent, string title, SpriteCombo cb, string[] items, int sel, EventHandler? onChange, Param p = Param.None)
        {
            // long titles wrap instead of being cut ("Canvas (drawn 1:1 from the top-left of the preview)"); the field
            // height is fixed (not AutoSize), so the rows below never depend on when the nested panel gets to lay out
            int lines = Math.Max(1, (Sprite.MeasureText(title).Width + 279) / 280);      // the pane is ~280 px of text width
            var field = new SpriteStackPanel { Gap = 2, Padding = Padding.Empty, Height = 16 + 11 * (lines - 1) + 2 + 24 + 4 };
            var lbl = new SpriteLabel { Text = title, AutoSize = false, Height = 16 + 11 * (lines - 1), WordWrap = lines > 1 };
            cb.SetItems(items, sel); cb.Height = 24;
            if (onChange != null) cb.SelectedIndexChanged += onChange;
            field.Controls.Add(lbl); field.Controls.Add(cb);
            parent.Controls.Add(field);
            titles[field] = title; fieldTitles[field] = lbl;
            if (p != Param.None) paramMain[p] = field;
        }
        void Slider(Control parent, string title, ParamSlider tb, int min, int max, int val, Param p = Param.None)
        {
            tb.Minimum = min; tb.Maximum = max; tb.Value = val; tb.Height = 42;
            tb.Text = title; tb.Ticks = 10; tb.ResetValue = val; tb.TextScale = 1;
            titles[tb] = title;
            tb.ValueChanged += (_, _) => { if (tb == tbScale && edgeDrag == 0) scaleY = tb.Value / 100f; };
            parent.Controls.Add(tb);
            if (p != Param.None) paramMain[p] = tb;
        }
        void Check(Control parent, SpriteToggle c, string text, bool val, Param p = Param.None)
        {
            c.Text = text; c.Checked = val; parent.Controls.Add(c);
            titles[c] = text;
            if (p != Param.None) paramMain[p] = c;
        }

        /// <summary>
        /// Shows, on the Test tab, exactly the controls of the parameters the current test reads: what its source mentions
        /// (<see cref="StaticUsed"/>) plus what Ctx recorded while it ran (a test may read more once a check box is on), or its
        /// own <see cref="DemoTest.Controls"/> list when it has one. Re-evaluated after each frame; cheap when nothing changed.
        /// </summary>
        void UpdateVisible(Param used)
        {
            if (settingsHost != null && current != null) settingsHost.Visible = current.SettingsControl != null && (current.SettingsVisible?.Invoke(ctx) ?? true);
            Param show = current != null && current.Controls.Count > 0 ? current.Controls.Keys.Aggregate(Param.None, (a, b) => a | b) : used | staticUsed;
            if (show == shownParams) return;
            shownParams = show;
            testStack.SuspendLayout();
            foreach (var kv in paramMain) kv.Value.Visible = (show & kv.Key) != 0;
            lblNoParams.Visible = (show & ~Param.Camera) == 0;
            testStack.ResumeLayout(true);
        }

        /// <summary>Parameters a test's source code mentions (c.Angle, c.Scale ... in its lambda and the helpers it calls) - what the Test tab shows before the first frame.</summary>
        static Param StaticUsed(DemoTest t)
        {
            Param p = Param.None;
            foreach (var name in CodeView.ParamRefs(CodeView.For(t)))
            {
                p |= name switch
                {
                    "X" or "Y" or "PivotX" or "PivotY" or "PivotXF" or "PivotYF" => Param.Mouse,
                    "Z" => Param.Z, "Angle" => Param.Angle, "Scale" or "ScaleY" => Param.Scale, "Blend" => Param.Blend, "Brite" => Param.Brite,
                    "Count" => Param.Count, "Smooth" => Param.Smooth, "NotMask" => Param.NotMask, "MaskBits" => Param.MaskBits, "Op" => Param.Op,
                    "DotStep" => Param.DotStep, "Xor" => Param.Xor, "Time" => Param.Time, "Yaw" or "Pitch" or "PanX" or "PanY" => Param.Camera,
                    _ => Param.None,
                };
            }
            if (t.Camera) p |= Param.Camera | Param.Scale;
            if (t.MouseRotates || t.Resizable || t.Frame || t.ShadowByMouse || t.PivotByClick || t.DragAnywhere) p |= Param.Mouse;
            if (t.ShadowByMouse) p |= Param.Z | Param.Angle;
            return p;
        }

        /// <summary>
        /// Relabel the shared controls for the selected test (DemoTest.Controls); tests without their own captions get the
        /// generic names back. Also swaps the Op entries / bit boxes and the slider ranges.
        /// </summary>
        void ApplyCaptions(DemoTest t)
        {
            bool custom = t.Controls.Count > 0;
            foreach (var kv in paramMain)
            {
                Param p = kv.Key; Control ctl = kv.Value;
                string caption = custom && t.Controls.TryGetValue(p, out var c) ? c : titles[ctl];
                if (ctl is ParamSlider tb)
                {
                    var (mn, mx) = t.Ranges.TryGetValue(p, out var rg) ? rg : GenericRanges[p];
                    if (tb.Minimum != mn || tb.Maximum != mx) tb.SetRange(mn, mx);
                    tb.Text = caption;
                }
            if (t.FractalCopies && canvas != null)                                  // the fractal tests: Copies is capped where the
            {                                                                       // next region would be too small to divide further
                int mn = GenericRanges[Param.Count].Item1;
                int mx = Math.Min(GenericRanges[Param.Count].Item2, FractalLayout.MaxRegions(canvas.Width, canvas.Height));
                if (tbCount.Minimum != mn || tbCount.Maximum != mx) tbCount.SetRange(mn, mx);
            }

                else if (ctl == bitField)
                {
                    string[] names = t.BitNames ?? MaskNames;
                    bool same = bitBoxes.Count == names.Length && bitBoxes.Select(b => b.Text).SequenceEqual(names) && (bitBoxes[0] is SpriteRadio) == t.BitsExclusive;
                    if (!same) BuildBitBoxes(names, t.BitsExclusive);
                    lblMask.Text = caption;
                }
                else if (fieldTitles.TryGetValue(ctl, out var lbl))
                {   // the Op combo
                    string[] names = t.OpNames ?? GenericOpNames;
                    if (!cbOp.Items.SequenceEqual(names)) { int sel = Math.Min(cbOp.SelectedIndex, names.Length - 1); cbOp.SetItems(names, Math.Max(0, sel)); }
                    lbl.Text = caption;
                }
                else if (ctl is SpriteToggle chk) chk.Text = caption;
            }
            shownParams = (Param)(-1);   // controls were relabelled: re-evaluate what the tab shows
        }

        void BuildBitBoxes(string[] names, bool exclusive)
        {
            bitRow.SuspendLayout();
            foreach (var b in bitBoxes) { bitRow.Controls.Remove(b); b.Dispose(); }
            bitBoxes.Clear();
            for (int i = 0; i < names.Length && i < 8; i++)
            {
                int w = Math.Min(280, Sprite.MeasureText(names[i]).Width + 30);
                SpriteClickable b = exclusive ? new SpriteRadio { Text = names[i], Checked = i == 0, Size = new Size(w, 22), GroupName = "bits" }
                                              : new SpriteToggle { Text = names[i], Checked = i == 0, Size = new Size(w, 22), Style = ToggleStyle.CheckBox };
                bitRow.Controls.Add(b); bitBoxes.Add(b);
            }
            bitRow.ResumeLayout(true);
        }
        static bool IsOn(SpriteClickable b) => b is SpriteToggle t ? t.Checked : ((SpriteRadio)b).Checked;
        int BitMask()
        {
            int m = 0;
            for (int i = 0; i < bitBoxes.Count; i++) if (IsOn(bitBoxes[i])) m |= 1 << i;
            return m;
        }
        void SetBitMask(int m)
        {
            for (int i = 0; i < bitBoxes.Count; i++)
            {
                bool on = (m >> i & 1) != 0;
                if (bitBoxes[i] is SpriteToggle c) c.Checked = on; else if (on) ((SpriteRadio)bitBoxes[i]).Checked = true;
            }
        }

        // ---- per-test parameter sets ------------------------------------------------------------
        Dictionary<Param, int> ReadParams() => new Dictionary<Param, int>
        {
            [Param.Count] = tbCount.Value, [Param.Angle] = tbAngle.Value, [Param.Scale] = tbScale.Value, [Param.Blend] = tbBlend.Value, [Param.Brite] = tbBrite.Value,
            [Param.Z] = tbZ.Value, [Param.DotStep] = tbDot.Value, [Param.Op] = cbOp.SelectedIndex, [Param.Smooth] = chkSmooth.Checked ? 1 : 0, [Param.NotMask] = chkNot.Checked ? 1 : 0,
            [Param.Xor] = chkXor.Checked ? 1 : 0, [Param.Mouse] = chkFollow.Checked ? 1 : 0, [Param.MaskBits] = BitMask(), [Param.Time] = chkAnim.Checked ? 1 : 0, [Param.Grid] = tbGrid.Value, [Param.Speed] = tbSpeed.Value, [Param.GridOn] = chkGridLines.Checked ? 1 : 0, [Param.OffsetX] = tbOffX.Value, [Param.OffsetY] = tbOffY.Value,
        };
        void WriteParams(DemoTest t, Dictionary<Param, int> v)
        {
            int Get(Param p) => v.TryGetValue(p, out var x) ? x : t.Defaults.TryGetValue(p, out x) ? x : GenericDefaults[p];
            void Tb(ParamSlider tb, Param p) => tb.Value = Math.Clamp(Get(p), tb.Minimum, tb.Maximum);
            Tb(tbCount, Param.Count); Tb(tbAngle, Param.Angle); Tb(tbScale, Param.Scale); Tb(tbBlend, Param.Blend); Tb(tbBrite, Param.Brite); Tb(tbZ, Param.Z); Tb(tbDot, Param.DotStep); Tb(tbGrid, Param.Grid); Tb(tbSpeed, Param.Speed); Tb(tbOffX, Param.OffsetX); Tb(tbOffY, Param.OffsetY);
            cbOp.SelectedIndex = Math.Clamp(Get(Param.Op), 0, cbOp.Items.Count - 1);
            chkSmooth.Checked = Get(Param.Smooth) != 0; chkNot.Checked = Get(Param.NotMask) != 0; chkXor.Checked = Get(Param.Xor) != 0;
            chkFollow.Checked = Get(Param.Mouse) != 0; chkAnim.Checked = Get(Param.Time) != 0; chkGridLines.Checked = Get(Param.GridOn) != 0;
            SetBitMask(Get(Param.MaskBits));
            scaleY = tbScale.Value / 100f;
        }

        // ---- mouse on the canvas ------------------------------------------------------------------
        // ---- object geometry -------------------------------------------------------------------------------------------
        // The draggable object is a w x h box in "object units" (source pixels for sprite tests, whatever the test's
        // ObjectSize delegate returns otherwise - the vector test returns its picture's size at Scale 1). Object -> canvas:
        //     canvas = position + R(angle) * ((p - pivot) * (scaleX, scaleY))
        // so the frame, the edge handles and the pivot all turn with the object.
        SizeF ObjectSize() => current?.ObjectSize != null ? current.ObjectSize(ctx) : new SizeF(assets?.Size ?? 64, assets?.Size ?? 64);
        (float sx, float sy) ObjectScale()
        {
            bool uses = current != null && ((current.Used | ctx.Used) & Param.Scale) != 0;
            return uses ? (tbScale.Value / 100f, scaleY) : (1f, 1f);
        }
        /// <summary>Screen angle of the object (radians, clockwise positive) including the animation spin.</summary>
        float ObjectAngle()
        {
            if (current == null || !current.MouseRotates) return 0f;
            float a = tbAngle.Value * MathF.PI / 180f + (chkAnim.Checked ? (float)animTime * current.AnimSpin : 0f);
            return current.AngleCCW ? -a : a;
        }
        PointF ObjectPivot() { var sz = ObjectSize(); return new PointF(pivotX < 0 ? sz.Width / 2 : pivotX, pivotY < 0 ? sz.Height / 2 : pivotY); }
        /// <summary>Canvas pixel -> object units.</summary>
        PointF ToObject(float px, float py)
        {
            var (sx, sy) = ObjectScale(); var pv = ObjectPivot(); float a = ObjectAngle(), c = MathF.Cos(a), sn = MathF.Sin(a);
            float dx = px - mouseX, dy = py - mouseY;
            float ux = dx * c + dy * sn, uy = -dx * sn + dy * c;
            return new PointF(pv.X + ux / (Math.Abs(sx) < 1e-6f ? 1e-6f : sx), pv.Y + uy / (Math.Abs(sy) < 1e-6f ? 1e-6f : sy));
        }
        /// <summary>Object units -> canvas pixel.</summary>
        PointF ToCanvas(float ox, float oy)
        {
            var (sx, sy) = ObjectScale(); var pv = ObjectPivot(); float a = ObjectAngle(), c = MathF.Cos(a), sn = MathF.Sin(a);
            float ux = (ox - pv.X) * sx, uy = (oy - pv.Y) * sy;
            return new PointF(mouseX + ux * c - uy * sn, mouseY + ux * sn + uy * c);
        }
        /// <summary>The four frame corners on the canvas (TL, TR, BR, BL).</summary>
        PointF[] ObjectCorners() { var sz = ObjectSize(); return new[] { ToCanvas(0, 0), ToCanvas(sz.Width, 0), ToCanvas(sz.Width, sz.Height), ToCanvas(0, sz.Height) }; }
        /// <summary>Axis-aligned bounds of the frame (for the old callers).</summary>
        Rectangle ObjectRect()
        {
            var c = ObjectCorners(); float l = c.Min(p => p.X), t = c.Min(p => p.Y), r = c.Max(p => p.X), b = c.Max(p => p.Y);
            return Rectangle.FromLTRB((int)MathF.Floor(l), (int)MathF.Floor(t), (int)MathF.Ceiling(r), (int)MathF.Ceiling(b));
        }
        /// <summary>Which edges of the object frame are under (px, py) (within 6 canvas px): 1 left, 2 right, 4 top, 8 bottom.</summary>
        int HitEdge(int px, int py)
        {
            if (current == null || !current.Resizable) return 0;
            var sz = ObjectSize(); var (sx, sy) = ObjectScale(); var o = ToObject(px, py);
            float gx = 6f / MathF.Max(1e-3f, MathF.Abs(sx)), gy = 6f / MathF.Max(1e-3f, MathF.Abs(sy));   // 6 px in object units
            bool inX = o.X >= -gx && o.X <= sz.Width + gx, inY = o.Y >= -gy && o.Y <= sz.Height + gy;
            int e = 0;
            if (inY && Math.Abs(o.X) <= gx) e |= 1; if (inY && Math.Abs(o.X - sz.Width) <= gx) e |= 2;
            if (inX && Math.Abs(o.Y) <= gy) e |= 4; if (inX && Math.Abs(o.Y - sz.Height) <= gy) e |= 8;
            return e;
        }
        /// <summary>Resize cursor for an edge set, turned with the object (the arrow follows the edge normal on screen).</summary>
        Cursor EdgeCursor(int e)
        {
            if (e == 0) return Cursors.Default;
            // direction the handle moves in object space, then rotated onto the screen
            float ox = (e & 1) != 0 ? -1 : (e & 2) != 0 ? 1 : 0, oy = (e & 4) != 0 ? -1 : (e & 8) != 0 ? 1 : 0;
            var (sx, sy) = ObjectScale(); float a = ObjectAngle(), c = MathF.Cos(a), sn = MathF.Sin(a);
            float ux = ox * MathF.Sign(sx == 0 ? 1 : sx), uy = oy * MathF.Sign(sy == 0 ? 1 : sy);
            float dx = ux * c - uy * sn, dy = ux * sn + uy * c;
            int oct = (int)MathF.Round(MathF.Atan2(dy, dx) / (MathF.PI / 4)) & 3;   // 0 = horizontal, 1 = \, 2 = vertical, 3 = /
            return oct switch { 0 => Cursors.SizeWE, 1 => Cursors.SizeNWSE, 2 => Cursors.SizeNS, _ => Cursors.SizeNESW };
        }
        bool RotatesByMouse => current != null && current.MouseRotates && (!chkAnim.Checked || current.AnimSpin == 0f);

        void SurfaceDown(MouseEventArgs e)
        {
            lastMx = e.X; lastMy = e.Y;
            if (current != null && current.Camera)
            {
                if (e.Button == MouseButtons.Left) { orbiting = true; surf.Cursor = SpriteCursors.Rotate; if (current.FreeOpIndex >= 0 && cbOp.SelectedIndex != current.FreeOpIndex) cbOp.SelectedIndex = current.FreeOpIndex; }
                else if (e.Button == MouseButtons.Middle) { panning = true; surf.Cursor = SpriteCursors.HandGrab; }
                else if (e.Button == MouseButtons.Right) { camPanX = camPanY = 0; }   // right click: recentre
                return;
            }
            if (current != null && current.PaintByMouse)
            {
                if (e.Button == MouseButtons.Left) { painting = true; penX = e.X; penY = e.Y; }
                return;
            }
            if (current != null && current.ShadowByMouse)
            {
                if (e.Button == MouseButtons.Left) { shadowDrag = true; ShadowTowards(e.X, e.Y); surf.Cursor = Cursors.Cross; }
                return;
            }
            if (current != null && current.Frame)
            {
                if (e.Button == MouseButtons.Right) { frame.Reset(); return; }
                if (e.Button == MouseButtons.Left && frame.MouseDown(e.X, e.Y, ModifierKeys)) { surf.Cursor = frame.CursorFor(frame.Hover, frame.HoverIndex); return; }
                mouseX = e.X; mouseY = e.Y;                                            // outside the frame: the hit-test point follows the click
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                int edge = HitEdge(e.X, e.Y);
                if (edge != 0) { edgeDrag = edge; surf.Cursor = EdgeCursor(edge); }
                else if (HitObject(e.X, e.Y))
                {
                    if (current?.ObjectClick != null) { var o = ToObject(e.X, e.Y); if (current.ObjectClick(ctx, o.X, o.Y)) return; }
                    dragging = true; dragDx = e.X - mouseX; dragDy = e.Y - mouseY; surf.Cursor = SpriteCursors.HandGrab;
                }
                else if (RotatesByMouse && HitRotateZone(e.X, e.Y)) { rotating = true; RotateStart(e.X, e.Y); surf.Cursor = SpriteCursors.Rotate; }
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (current != null && (current.MouseRotates || current.PivotByClick)) SetPivot(e.X, e.Y);     // right click: move the pivot (sprite stays)
                else { mouseX = e.X; mouseY = e.Y; }                                   // right click: teleport
            }
        }
        void SurfaceMove(MouseEventArgs e)
        {
            int dx = e.X - lastMx, dy = e.Y - lastMy; lastMx = e.X; lastMy = e.Y;
            if (current != null && current.Camera)
            {
                if (orbiting) { camYaw -= dx * 0.01f; camPitch = Math.Clamp(camPitch + dy * 0.01f, -1.55f, 1.55f); }   // drag right -> scene turns counter-clockwise
                else if (panning) { camPanX += dx; camPanY += dy; }
                if (!orbiting && !panning) surf.Cursor = Cursors.Default;
                return;
            }
            if (painting) { penX = e.X; penY = e.Y; return; }
            if (current != null && current.PaintByMouse) { surf.Cursor = SpriteCursors.Pen; return; }
            if (shadowDrag) { ShadowTowards(e.X, e.Y); return; }
            if (current != null && current.Frame)
            {
                frame.MouseMove(e.X, e.Y, ModifierKeys);
                if (!frame.Dragging) { mouseX = e.X; mouseY = e.Y; }                  // the free pointer is the hit-test point
                surf.Cursor = frame.CursorFor(frame.Hover, frame.HoverIndex);          // MouseMove updated Hover (or kept the dragged part)
                return;
            }
            if (edgeDrag != 0) { ResizeTo(e.X, e.Y); return; }
            if (rotating) { RotateTowards(e.X, e.Y); return; }
            if (dragging) { mouseX = e.X - dragDx; mouseY = e.Y - dragDy; if (current?.GrabBackdrop == true && canvas != null) { backdropX = mouseX - canvas.Width / 2; backdropY = mouseY - canvas.Height / 2; } }
            else if (chkFollow.Checked) { mouseX = e.X; mouseY = e.Y; }
            int edge = dragging ? 0 : HitEdge(e.X, e.Y);
            surf.Cursor = edge != 0 ? EdgeCursor(edge) : dragging ? SpriteCursors.HandGrab : HitObject(e.X, e.Y) ? SpriteCursors.HandOpen : RotatesByMouse && HitRotateZone(e.X, e.Y) ? SpriteCursors.Rotate : Cursors.Default;
        }
        void SurfaceWheel(MouseEventArgs e)
        {
            int steps = e.Delta / 120; if (steps == 0) return;
            if (current != null && current.Camera) { tbScale.Value = Math.Clamp((int)Math.Round(tbScale.Value * Math.Pow(1.15, steps)), tbScale.Minimum, tbScale.Maximum); return; }
            if (RotatesByMouse) { tbAngle.Value = ((tbAngle.Value + steps * 5) % 360 + 360) % 360; return; }
            if (current != null && ((current.Used | ctx.Used) & Param.Scale) != 0) tbScale.Value = Math.Clamp((int)Math.Round(tbScale.Value * Math.Pow(1.1, steps)), tbScale.Minimum, tbScale.Maximum);
        }
        /// <summary>ShadowByMouse: distance (Z slider) and direction (Angle slider) from the canvas centre (where the sprite sits) to the pointer.</summary>
        void ShadowTowards(int px, int py)
        {
            if (canvas == null) return;
            int cx = canvas.Width / 2, cy = canvas.Height / 2;
            int dx = px - cx, dy = py - cy;
            tbZ.Value = Math.Clamp((int)Math.Round(Math.Sqrt(dx * dx + dy * dy)), tbZ.Minimum, tbZ.Maximum);
            if (dx != 0 || dy != 0) { int deg = (int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI); tbAngle.Value = ((deg % 360) + 360) % 360; }
        }
        /// <summary>
        /// Right click in a MouseRotates test: the clicked canvas pixel becomes the pivot. The sprite is currently drawn
        /// rotated by Angle around the old pivot (which sits on the position), so the click is mapped back into source
        /// pixels through the inverse rotation; the position is moved to the click so the sprite stays where it is.
        /// Clicks outside the sprite reset the pivot to the centre (the position moves so the sprite still stays put).
        /// </summary>
        void SetPivot(int px, int py)
        {
            if (assets == null) return;
            var sz = ObjectSize(); var pv = ObjectPivot();
            var o = ToObject(px, py);                       // clicked canvas pixel in object units (through the current pivot)
            if (o.X < 0 || o.Y < 0 || o.X >= sz.Width || o.Y >= sz.Height)
            {   // outside: back to the centre, keep the object where it is (the position moves to where the centre is drawn)
                var cc = ToCanvas(sz.Width / 2, sz.Height / 2);
                mouseX = (int)MathF.Round(cc.X); mouseY = (int)MathF.Round(cc.Y);
                pivotX = pivotY = -1;
                return;
            }
            // sprite tests take whole source pixels; ObjectSize tests (vector) keep fractions
            bool whole = current?.ObjectSize == null;
            pivotX = whole ? (int)o.X : o.X; pivotY = whole ? (int)o.Y : o.Y;
            var np = ToCanvasWithPivot(pivotX, pivotY, pv);  // where the new pivot point is currently drawn
            mouseX = (int)MathF.Round(np.X); mouseY = (int)MathF.Round(np.Y);
        }
        /// <summary>Object point -> canvas using an explicit old pivot (before the position is moved).</summary>
        PointF ToCanvasWithPivot(float ox, float oy, PointF pv)
        {
            var (sx, sy) = ObjectScale(); float a = ObjectAngle(), c = MathF.Cos(a), sn = MathF.Sin(a);
            float ux = (ox - pv.X) * sx, uy = (oy - pv.Y) * sy;
            return new PointF(mouseX + ux * c - uy * sn, mouseY + ux * sn + uy * c);
        }
        /// <summary>
        /// Editor-style rotation: the angle changes by how far the pointer has TURNED around the position since the press
        /// (rotStartAngle / rotStartValue), not to "where the pointer points" - so grabbing a handle never makes the sprite
        /// jump, whatever the angle was, and the pivot / handle stays under the cursor. Shift = 15 degree steps.
        /// </summary>
        double rotStartAngle, rotStartValue, rotTurned;
        void RotateStart(int px, int py)
        {
            rotStartAngle = Math.Atan2(py - mouseY, px - mouseX); rotStartValue = tbAngle.Value; rotTurned = 0;
        }
        void RotateTowards(int px, int py)
        {
            double a = Math.Atan2(py - mouseY, px - mouseX);
            double d = a - rotStartAngle; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
            rotTurned += d; rotStartAngle = a;                                   // accumulate, so several full turns work
            double deg = rotTurned * 180 / Math.PI;
            if (current != null && current.AngleCCW) deg = -deg;                // the test's positive angle turns the other way
            double v = rotStartValue + deg;
            if (ModifierKeys.HasFlag(Keys.Shift)) v = Math.Round(v / 15) * 15;
            tbAngle.Value = (int)(((Math.Round(v) % 360) + 360) % 360);
        }
        /// <summary>
        /// Rotate zone "as in editors": a band just OUTSIDE the object frame (from the edge to ~28 canvas px beyond it,
        /// widest at the corners) - inside = move, on the edge = resize (Resizable tests), the ring around = rotate.
        /// Returns false when the pointer is far away from the object.
        /// </summary>
        bool HitRotateZone(int px, int py)
        {
            if (current == null || !current.MouseRotates) return false;
            var sz = ObjectSize(); var (sx, sy) = ObjectScale(); var o = ToObject(px, py);
            float ax = MathF.Max(1e-3f, MathF.Abs(sx)), ay = MathF.Max(1e-3f, MathF.Abs(sy));
            // distance from the frame in canvas px (0 inside)
            float dx = MathF.Max(MathF.Max(-o.X, o.X - sz.Width), 0) * ax, dy = MathF.Max(MathF.Max(-o.Y, o.Y - sz.Height), 0) * ay;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d <= 0) return false;                                       // inside: move (or resize handles)
            float band = Math.Max(20f, Math.Min(36f, Math.Min(sz.Width * ax, sz.Height * ay) * 0.25f));
            return d <= band;
        }
        void ResizeTo(int px, int py)
        {
            // the dragged edge follows the mouse: unrotated offset from the pivot / distance pivot -> edge in object units = new scale
            var sz = ObjectSize(); var pv = ObjectPivot(); float a = ObjectAngle(), c = MathF.Cos(a), sn = MathF.Sin(a);
            float dx = px - mouseX, dy = py - mouseY;
            float ux = dx * c + dy * sn, uy = -dx * sn + dy * c;
            float lo = tbScale.Minimum / 100f, hi = tbScale.Maximum / 100f;
            if ((edgeDrag & 3) != 0)
            {
                float d = (edgeDrag & 2) != 0 ? sz.Width - pv.X : -pv.X;
                if (MathF.Abs(d) > 1e-3f) tbScale.Value = Math.Clamp((int)Math.Round(Math.Clamp(ux / d, lo, hi) * 100f), tbScale.Minimum, tbScale.Maximum);
            }
            if ((edgeDrag & 12) != 0)
            {
                float d = (edgeDrag & 8) != 0 ? sz.Height - pv.Y : -pv.Y;
                if (MathF.Abs(d) > 1e-3f) scaleY = Math.Clamp(uy / d, lo, hi);
            }
            if ((edgeDrag & 12) == 0 && ModifierKeys.HasFlag(Keys.Shift)) scaleY = tbScale.Value / 100f;   // Shift: keep it square
        }

        /// <summary>Hit test for the draggable object: a sprite-sized square centred on the current position.</summary>
        bool HitObject(int px, int py)
        {
            if (assets == null || current == null) return false;
            if (current.GrabBackdrop) return true;                              // grabbable background: drag anywhere with the hand cursor
            if (((current.Used | ctx.Used) & Param.Mouse) == 0) return false;   // test does not use the position: nothing to drag
            if (current.DragAnywhere) return true;
            if (current.Resizable) { var sz = ObjectSize(); var o = ToObject(px, py); var (sx, sy) = ObjectScale(); float gx = 4f / MathF.Max(1e-3f, MathF.Abs(sx)), gy = 4f / MathF.Max(1e-3f, MathF.Abs(sy)); return o.X > gx && o.Y > gy && o.X < sz.Width - gx && o.Y < sz.Height - gy; }
            // plain objects: the (possibly scaled) bounding box, never smaller than 24 px around the position
            var rc = ObjectRect(); if (rc.Width < 48) rc.Inflate((48 - rc.Width) / 2, 0); if (rc.Height < 48) rc.Inflate(0, (48 - rc.Height) / 2);
            return rc.Contains(px, py);
        }

        /// <summary>Puts the object at the test's home position (DemoTest.HomeX / HomeY as canvas fractions; default = centre).</summary>
        void GoHome(DemoTest? t)
        {
            if (canvas == null) return;
            float fx = t != null && t.HomeX >= 0 ? t.HomeX : 0.5f, fy = t != null && t.HomeY >= 0 ? t.HomeY : 0.5f;
            mouseX = (int)(canvas.Width * fx); mouseY = (int)(canvas.Height * fy);
        }
        /// <summary>Back to the selected test's start values (its own defaults, else the generic ones) and the default camera.</summary>
        void ResetParams()
        {
            if (current != null) { savedParams.Remove(current); WriteParams(current, new Dictionary<Param, int>()); }
            chkClear.Checked = chkPresent.Checked = true;
            camYaw = 0.8f; camPitch = 0.55f; camPanX = camPanY = 0; animTime = 0; pivotX = pivotY = -1;
            if (canvas != null) GoHome(current);
        }

        void ReloadTests()
        {
            tests = Tests.All();
            FillTestList(null);
            lstTests.SelectedIndex = 1;
        }
        DemoTest? SelectedTest => lstTests.SelectedIndex >= 0 && lstTests.SelectedIndex < listItems.Count ? listItems[lstTests.SelectedIndex] : null;
        /// <summary>Fills the list with the tests that pass <paramref name="words"/> (null / empty = all), group headers included; keeps the selection when it survives.</summary>
        void FillTestList(string[]? words)
        {
            var keep = SelectedTest;
            listItems.Clear();
            var names = new List<string>(); var headers = new List<int>(); var colours = new Dictionary<int, int>();
            int grey = Color.Gray.ToArgb();
            string g = ""; int shown = 0;
            foreach (var t in tests)
            {
                if (words != null && words.Length > 0 && !Matches(t, words)) continue;
                if (t.Group != g) { g = t.Group; headers.Add(names.Count); names.Add(g); listItems.Add(new DemoTest { Group = "", Name = g, Run = null! }); }
                if (!t.Available) colours[names.Count] = grey;
                names.Add("   " + t.Name + (t.Available ? "" : "   (needs new DLL)")); listItems.Add(t); shown++;
            }
            lstTests.SetItems(names, headers, colours);      // clears the selection
            lblFilterHint.Text = words == null || words.Length == 0 ? "" : $"{shown} / {tests.Count}";
            int ki = keep != null && keep.Group != "" ? listItems.IndexOf(keep) : -1;
            if (ki >= 0) lstTests.SelectedIndex = ki;   // SelectTest runs again for the same test - it keeps its parameters
        }
        static bool Matches(DemoTest t, string[] words)
        {
            foreach (var w in words)
            {
                if (w.StartsWith("group:", StringComparison.OrdinalIgnoreCase)) { if (!t.Group.Contains(w[6..], StringComparison.OrdinalIgnoreCase)) return false; continue; }
                if (t.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Group.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Desc.Contains(w, StringComparison.OrdinalIgnoreCase)) continue;
                return false;
            }
            return true;
        }
        void ApplyFilter()
        {
            var words = txtFilter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            FillTestList(words);
            // nothing selected any more (the current test is filtered out): select the first hit so the canvas follows the search
            if (lstTests.SelectedIndex < 0 && listItems.Count > 1) lstTests.SelectedIndex = 1;
        }

        void SelectTest()
        {
            if (SelectedTest is not DemoTest t || t.Group.Length == 0) { return; }
            if (!t.Available)
            {
                lblDesc.Text = t.Name + "\r\n\r\nThis test needs " + (t.NeedsBlend && !Caps.HasBlendMode ? "BLEND_MODE" : t.NeedsVoxel && !Caps.HasVoxel ? "VOXEL_RENDER" : t.NeedsFlood && !Caps.HasFlood ? "FLOOD_MASK" : t.NeedsFx && !Caps.HasFx ? "DRAW_FX" : t.NeedsBlur && !Caps.HasBlur ? "DRAW_BLUR" : t.NeedsPoly && !Caps.HasPoly ? "DRAW_POLY" : t.NeedsLine2 && !Caps.HasLine2 ? "DRAW_LINE2" : t.NeedsAlphaOver && !Caps.HasAlphaOver ? "ALPHA_OVER" : "DRAW_WARP") + ", which the loaded DLL does not export. Swap in the new SR2D64.dll to enable it.";
                current = null;
                return;
            }
            if (current == t) return;                       // the list was refilled (filter) - nothing changed
            if (current != null) savedParams[current] = ReadParams();
            current = t;
            lblDesc.Text = t.Name + "\r\n\r\n" + t.Desc;
            staticUsed = StaticUsed(t);
            if (codeShown) LoadCode(t);
            ApplyCaptions(t);
            surf.Cursor = t.PaintByMouse ? SpriteCursors.Pen : Cursors.Default;
            if (settingsHost == null && t.SettingsControl != null)
            {
                settingsHost = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Bottom, AutoSize = true, AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent };
                testStack.Controls.Add(settingsHost);
                settingsHost.BringToFront();
            }
            if (settingsHost != null)
            {
                if (t.SettingsControl == null) { settingsHost.Visible = false; }
                else if (settingsHost.Controls.Count == 0 || settingsHost.Tag != t)
                {
                    settingsHost.SuspendLayout();
                    foreach (System.Windows.Forms.Control old in settingsHost.Controls) { settingsHost.Controls.Remove(old); old.Dispose(); }
                    var ctl = t.SettingsControl();
                    ctl.Dock = System.Windows.Forms.DockStyle.Top;
                    settingsHost.Controls.Add(ctl);
                    settingsHost.Height = ctl.Height + 4;
                    settingsHost.Tag = t;
                    settingsHost.ResumeLayout();
                }
            }
            if (t.SpriteSize > 0)                                              // the test wants a fixed "Sprite size" setup (the block demos: 512)
            {
                for (int i = 0; i < cbSprite.Items.Count; i++)
                    if (cbSprite.Items[i] == t.SpriteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)) { if (cbSprite.SelectedIndex != i) cbSprite.SelectedIndex = i; break; }
            }
            WriteParams(t, savedParams.TryGetValue(t, out var sp) ? sp : new Dictionary<Param, int>());
            orbiting = panning = rotating = shadowDrag = false; edgeDrag = 0; pivotX = pivotY = -1; backdropX = backdropY = 0;
            prog.Visible = false;
            btnFile.Visible = t.FileFilter != null;
            btnMeasure.Visible = chkProgress.Visible = t.SlowFrames > 0;
            ShowStrip(t);
            btnFile.Text = t.FilePath != null ? "Open file... (" + (t.FilePath.Contains('|') ? t.FilePath.Split('|').Length + " files" : Path.GetFileName(t.FilePath)) + ")" : "Open file...";
            StartSlowRun();
            emaRender = -1;
            fpsFrames = 0; fpsAccumMs = renderAccumMs = presentAccumMs = 0;
            dragging = false;
            // fresh start: no leftovers from the previous test (matters for tests that
            // accumulate or when "Clear canvas each frame" is off), and if the object was
            // dragged off-screen bring it back.
            if (canvas != null)
            {
                canvas.ClearBuffer(unchecked((int)0xFF101418));
                if (t.HomeX >= 0 || mouseX < 0 || mouseY < 0 || mouseX >= canvas.Width || mouseY >= canvas.Height) GoHome(t);
                Present();
            }
            ctx.ResetUsed(); ctx.Frame = frame; frame.Reset();
            UpdateVisible(t.Used);            // what we know from the last time it ran (None the first time) + what its source mentions
        }

        /// <summary>Swaps the canvas for the code view (rendering pauses while the code is shown).</summary>
        void ShowCode(bool on)
        {
            codeShown = on;
            if (on && current != null) LoadCode(current);
            codePanel.Visible = on; surf.Visible = !on;
            btnCode.Text = on ? "Demo (F2)" : "Code (F2)";
            if (!on) { resizeTimer.Stop(); resizeTimer.Start(); }
        }
        /// <summary>Puts the test's source into the code view, syntax-coloured by <see cref="CodeView.Colorize"/>.</summary>
        void LoadCode(DemoTest t)
        {
            codeText = CodeView.For(t);
            var (lines, runs) = CodeView.Colorize(codeText);
            var textRuns = new TextRun[]?[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                var r = runs[i]; if (r == null) continue;
                var tr = new TextRun[r.Length];
                for (int k = 0; k < r.Length; k++) tr[k] = new TextRun(r[k].start, r[k].len, r[k].color);   // Colorize already resolved Palette[cls] to the final ARGB
                textRuns[i] = tr;
            }
            codeBox.SetLines(lines, textRuns);
            codeBox.ScrollToLine(0);
        }
        /// <summary>Docks the test's control strip (DemoTest.ControlStrip) above the canvas, or hides the row.</summary>
        void ShowStrip(DemoTest t)
        {
            Control? strip = null;
            if (t.ControlStrip != null && !strips.TryGetValue(t, out strip))
            {
                try { strip = t.ControlStrip(); strips[t] = strip; }
                catch (Exception ex) { Log("control strip of \"" + t.Name + "\" failed to build:\r\n" + ex); }   // keep the demo alive; the log shows why
            }
            foreach (Control c in stripHost.Controls) c.Visible = c == strip;
            if (strip != null && strip.Parent != stripHost) stripHost.Controls.Add(strip);
            bool show = strip != null;
            if (ColorDemo.ColorOverlay != null)                       // the colour test keeps its dialog on the canvas
                ColorDemo.ColorOverlay.Visible = t.ControlStrip == ColorDemo.Build;
            int rowH = show ? t.StripHeight : 0;                        // the canvas shrinks so every control stays visible
            bool moved = rowH != stripHost.Height || stripHost.Visible != show;
            stripHost.Height = rowH;
            stripHost.Visible = show;
            // refit on BOTH transitions: hiding the row gives the canvas its old height back, and RebuildCanvas is the
            // only thing that hands that height to the canvas sprite. A test whose strip is the first in the list used to
            // leave every later test one strip-height too short (measured: the canvas kept 292 px of the row that went away).
            if (moved) { resizeTimer.Stop(); resizeTimer.Start(); }
        }

        void ApplySimd()
        {
            if (!Caps.HasSimdInfo) return;
            int lvl = cbSimd.SelectedIndex;   // 0 auto, 1 sse2, 2 avx2
            var got = SR2D.SetSimdLevel((SR2D.SimdLevel)lvl);
            UpdateTitleTags();
            Log("SIMD level now: " + Caps.SimdName);
            if (lvl == 2 && got != SR2D.SimdLevel.Avx2) MessageBox.Show("CPU/OS does not support AVX2 - staying on SSE2.");
        }

        /// <summary>The DLL line as title bar tags: the path relative to the exe (full path in the log) + the kernel set.</summary>
        void UpdateTitleTags()
        {
            string rel;
            try { rel = Path.GetRelativePath(AppContext.BaseDirectory, Caps.Path); } catch { rel = Caps.Path; }
            if (rel.Length == 0 || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) rel = Path.GetFileName(Caps.Path);
            string api = Caps.HasWarp && Caps.HasLine2 && Caps.HasAlphaOver && Caps.HasPoly && Caps.HasBlur ? "new API"
                       : Caps.HasWarp || Caps.HasLine2 || Caps.HasAlphaOver || Caps.HasPoly || Caps.HasBlur ? "partial API" : "old API";
            var col = Caps.HasWarp ? Color.FromArgb(0xFF, 0x70, 0xE0, 0x90) : Color.FromArgb(0xFF, 0xFF, 0xB0, 0x50);
            SetTitleTags(new TitleTag(rel, col), new TitleTag("kernels: " + Caps.SimdName + " \u00b7 " + api, Color.FromArgb(0xFF, 0x8A, 0x94, 0xA6)));
        }

        // ------------------------------------------------------------- assets
        void RebuildAssets()
        {
            int ss = int.Parse(cbSprite.Items[cbSprite.SelectedIndex], System.Globalization.CultureInfo.InvariantCulture);
            Cursor = Cursors.WaitCursor;
            var old = assets;
            assets = new Assets(ss, cbAssets.SelectedIndex == 1 ? AssetSet.Bricks : AssetSet.Lenna);
            old?.Dispose();
            temp?.Dispose();
            temp = new Sprite(ss * 2, ss * 2);
            ctx.A = assets; ctx.Temp = temp;             // the canvas may stay - the context must not keep the disposed set
            foreach (var bt in bandTemps.Values) bt.Dispose();
            bandTemps.Clear();
            Cursor = Cursors.Default;
            Log($"sprites: {ss}x{ss} ({assets.Set})");
            RebuildCanvas();
        }

        void RebuildCanvas()
        {
            if (assets == null || temp == null) return;
            int cw, ch;
            if (cbCanvas.SelectedIndex == 0) { cw = Math.Max(64, surf.ClientSize.Width); ch = Math.Max(64, surf.ClientSize.Height); }
            else { var cs = cbCanvas.Items[cbCanvas.SelectedIndex].Split('x'); cw = int.Parse(cs[0].Trim(), System.Globalization.CultureInfo.InvariantCulture); ch = int.Parse(cs[1].Trim(), System.Globalization.CultureInfo.InvariantCulture); }
            if (canvas != null && canvas.Width == cw && canvas.Height == ch && canvas.IsGdiSurface == chkGdi.Checked) return;
            canvas?.Dispose();
            foreach (var bt in bandTemps.Values) bt.Dispose();
            bandTemps.Clear();
            canvas = new Sprite(cw, ch, chkGdi.Checked);
            ctx = new Ctx { Canvas = canvas, Temp = temp, A = assets, Frame = frame };   // Frame is a process-wide field: a fresh context must not publish null to readers between the rebuild and the next selection
            mouseX = cw / 2; mouseY = ch / 2;
            surf.Invalidate();
            emaRender = -1;
            if (current?.FractalCopies == true)                                     // keep the Copies cap in step with the new viewport
            {
                int mx = Math.Min(GenericRanges[Param.Count].Item2, FractalLayout.MaxRegions(cw, ch));
                if (tbCount.Maximum != mx) tbCount.SetRange(GenericRanges[Param.Count].Item1, mx);
            }
            Log($"canvas: {cw}x{ch}{(canvas.IsGdiSurface ? " (GDI surface)" : "")}");
        }

        void FillCtx()
        {
            ctx.Frame = frame;                                 // every path that fills the context hands over the same frame (the suite runs tests without a selection)
            ctx.Time = chkAnim.Checked ? (float)animTime : 0f;
            ctx.Yaw = camYaw; ctx.Pitch = camPitch; ctx.PanX = camPanX; ctx.PanY = camPanY;
            ctx.ScaleY = scaleY;
            ctx.PivotXF = pivotX; ctx.PivotYF = pivotY;
            ctx.Preview = false;
            ctx.Progress = null;
            lock (ctx.Labels) ctx.Labels.Clear();
            lock (ctx.InfoLines) ctx.InfoLines.Clear();
            ctx.X = mouseX; ctx.Y = mouseY;
            ctx.BackdropX = backdropX; ctx.BackdropY = backdropY;
            ctx.Z = tbZ.Value;
            ctx.Angle = tbAngle.Value * MathF.PI / 180f;
            ctx.Scale = tbScale.Value / 100f;
            ctx.Blend = tbBlend.Value;
            ctx.Brite = tbBrite.Value / 100f;
            ctx.Count = tbCount.Value;
            ctx.Smooth = chkSmooth.Checked;
            ctx.NotMask = chkNot.Checked;
            ctx.Xor = chkXor.Checked;
            ctx.DotStep = tbDot.Value;
            ctx.Grid = tbGrid.Value;
            ctx.Speed = tbSpeed.Value;
            ctx.PenDown = painting; ctx.PenX = penX; ctx.PenY = penY;
            ctx.ShowGrid = chkGridLines.Checked;
            ctx.OffsetX = tbOffX.Value; ctx.OffsetY = tbOffY.Value;
            ctx.Op = (SR2D.Op)(cbOp.SelectedIndex + 1);
            int mb = BitMask();
            ctx.MaskBits = mb == 0 && current?.BitNames == null ? 1 : mb;
            ctx.Note = "";
        }

        // -------------------------------------------------------------- frame
        double lastFrameStart, nextFrameDue;
        void Frame()
        {
            // the output bench's fullscreen form (demo/OutputDemo.cs) takes the whole window over: the bench renders
            // nothing here while it is up, every frame of this loop goes to that form instead.
            if (OutputDemo.FullscreenOpen) { OutputDemo.TickFullscreen(); return; }
            if (canvas == null || current == null || codeShown) { System.Threading.Thread.Sleep(5); return; }
            for (int i = 0; i < Application.OpenForms.Count; i++)       // a modal dialog owns the message loop now; keep
                if (Application.OpenForms[i] is { } open && open.Modal) { System.Threading.Thread.Sleep(5); return; }   // rendering behind it starved its input (the OK / Cancel clicks "came late")
            double now = clock.Elapsed.TotalMilliseconds;
            if (chkVsync.Checked)
            {
                // Frame pacing. The old version slept 1 ms per iteration, but without
                // timeBeginPeriod(1) Windows' Sleep(1) is really ~15.6 ms, so a 16.6 ms
                // budget always cost two sleeps = 31 ms -> "60 fps" came out as 30-32.
                // Now: 1 ms timer resolution, sleep while >2 ms remain, spin the rest.
                double target = 1000.0 / (double)numFps.Value;
                double remaining = nextFrameDue - now;
                if (remaining > 2.0) { System.Threading.Thread.Sleep(1); return; }
                if (remaining > 0.0) { System.Threading.Thread.SpinWait(100); return; }
                nextFrameDue += target;
                if (nextFrameDue < now) nextFrameDue = now + target;   // we were late: don't try to catch up
            }
            double frameDelta = now - lastFrameStart;
            lastFrameStart = now;
            if (chkAnim.Checked && frameDelta < 1000) animTime += frameDelta / 1000.0;
            RenderFrame(now, frameDelta);
            // the "output paths" test drives its four comparison panes from here (after the canvas, so the assets
            // it was given are still alive); it needs a frame source of its own - the strip has no timer.
            if (current != null && current.ControlStrip == OutputDemo.Build) OutputDemo.Tick();
        }

        // ---- slow (non-real-time) tests: render DemoTest.SlowFrames frames after a change, then freeze and report
        // A slow test renders ONE full frame after each change (with phase progress) and then freezes; "Measure fps"
        // renders SlowFrames frames back to back and reports the per-frame time. Camera moves just move the camera
        // (preview while dragging, one full frame when it rests).
        int slowLeft, slowTotal; double slowTotalMs, slowRenderMs, slowFirstMs; string slowSig = ""; bool slowFrozen; double slowChangedAt = -1; bool slowPreviewing, measuring;
        const double SlowIdleMs = 500;   // a slow test starts its real frame this long after the last control change (previews meanwhile)
        string SlowSignature() => $"{mouseX},{mouseY},{tbZ.Value},{tbAngle.Value},{tbScale.Value},{tbBlend.Value},{tbBrite.Value},{tbCount.Value},{chkSmooth.Checked},{chkNot.Checked},{chkXor.Checked},{tbDot.Value},{cbOp.SelectedIndex},{BitMask()},{chkPar.Checked},{canvas?.Width}x{canvas?.Height},{camYaw:F3},{camPitch:F3},{camPanX},{camPanY},{current?.FilePath}";
        void StartSlowRun(int frames = 1)
        {
            if (current == null || current.SlowFrames <= 0) { slowFrozen = false; prog.Visible = false; return; }
            measuring = frames > 1;
            slowLeft = slowTotal = frames; slowTotalMs = slowRenderMs = 0; slowFrozen = false; slowSig = SlowSignature(); slowChangedAt = -1; slowPreviewing = false;
            prog.Visible = true; prog.Value = 0;
            SetStatus(measuring ? $"measuring: {frames} full frames back to back ..." : "rendering one full frame (not real-time) ...");
            if (measuring) Log($"{current.Name}: measuring {frames} frames ...");
        }
        void StartMeasure() { if (current != null && current.SlowFrames > 0 && !inSuite) StartSlowRun(Math.Max(2, current.SlowFrames)); }

        void RenderFrame(double now, double frameDelta)
        {
            if (canvas == null || current == null) return;
            if (current.SlowFrames > 0)
            {
                // a frozen slow test only re-runs when a relevant control changed; while controls keep changing a test
                // with a preview draws its cheap stand-in instead, and the real frames start once things have been idle
                string sig = SlowSignature();
                if (sig != slowSig)
                {
                    if (slowFrozen || measuring) StartSlowRun();          // a change during a measurement cancels it
                    slowSig = sig; slowChangedAt = now;
                    if (current.HasPreview) { slowPreviewing = true; slowLeft = slowTotal = 1; slowTotalMs = slowRenderMs = 0; }
                }
                else if (slowFrozen) { System.Threading.Thread.Sleep(10); return; }
                if (slowPreviewing)
                {
                    if (now - slowChangedAt < SlowIdleMs || orbiting || panning) { RenderPreviewFrame(); return; }
                    slowPreviewing = false; prog.Value = 0;
                }
                long a0 = Stopwatch.GetTimestamp();
                RenderSlowFrame();
                double ms = (Stopwatch.GetTimestamp() - a0) * 1000.0 / Stopwatch.Frequency;
                slowTotalMs += ms; slowLeft--;
                int done = slowTotal - slowLeft;
                prog.Value = Math.Clamp(done * 100 / Math.Max(1, slowTotal), 0, 100);
                if (done == 1) slowFirstMs = ms;
                if (slowLeft <= 0)
                {
                    slowFrozen = true; prog.Value = 100;
                    int nf = slowTotal;
                    string res;
                    if (measuring)
                    {
                        // the first frame may carry a build / faces / light pass: report the steady-state figure from the others
                        double per = (slowRenderMs - slowFirstMs) / (nf - 1);
                        res = $"MEASURED {nf} frames: {per:0.0} ms/frame = {1000.0 / Math.Max(0.001, per):0.00} fps (render only, frames 2..{nf}; first frame {slowFirstMs:0} ms; incl. present {slowTotalMs:0} ms total; progress {(chkProgress.Checked ? "shown" : "off")})   {ctx.Note}";
                        measuring = false;
                    }
                    else res = $"frame {slowRenderMs:0} ms (incl. present {slowTotalMs:0} ms) - 'Measure fps' for a steady figure   {ctx.Note}";
                    SetStatus(res, true); Log(res);
                }
                else SetStatus($"frame {done}/{slowTotal}: {ms:0} ms", true);
                return;
            }

            FillCtx();
            long t0 = Stopwatch.GetTimestamp();
            if (chkClear.Checked && !current.ClearsItself) canvas.ClearBuffer(unchecked((int)0xFF101418));
            try { RunTest(current); }
            catch (Exception ex) { Log("EXCEPTION in " + current.Name + ": " + ex.Message); current = null; return; }
            long t1 = Stopwatch.GetTimestamp();
            current.Used |= ctx.Used;
            UpdateVisible(current.Used);
            DrawOverlays();                               // after the timed region, before the one and only present

            if (chkPresent.Checked) Present();
            long t2 = Stopwatch.GetTimestamp();

            double renderMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            double presentMs = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
            emaRender = emaRender < 0 ? renderMs : emaRender * 0.9 + renderMs * 0.1;
            fpsFrames++; fpsAccumMs += frameDelta; renderAccumMs += renderMs; presentAccumMs += presentMs;
            if (now - lastFpsUpdate > 250)
            {
                double fps = fpsFrames * 1000.0 / Math.Max(1, fpsAccumMs);
                double perOp = renderAccumMs / fpsFrames / Math.Max(1, ctx.Count);
                statsLine = $"FPS {fps,6:0.0}   render {renderAccumMs / fpsFrames,7:0.000} ms   per op {perOp,7:0.000} ms   present {presentAccumMs / fpsFrames,6:0.00} ms";
                fpsFrames = 0; fpsAccumMs = renderAccumMs = presentAccumMs = 0; lastFpsUpdate = now;
            }
        }

        /// <summary>Cheap stand-in frame of a slow test while its controls are moving (Ctx.Preview = true); not timed, not logged.</summary>
        void RenderPreviewFrame()
        {
            if (canvas == null || current == null) return;
            FillCtx(); ctx.Preview = true;
            long t0 = Stopwatch.GetTimestamp();
            if (chkClear.Checked && !current.ClearsItself) canvas.ClearBuffer(unchecked((int)0xFF101418));
            try { RunTest(current); }
            catch (Exception ex) { Log("EXCEPTION in " + current.Name + ": " + ex.Message); current = null; return; }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            current.Used |= ctx.Used;
            UpdateVisible(current.Used);
            DrawOverlays();
            if (chkPresent.Checked) Present();
            SetStatus($"PREVIEW {ms:0.0} ms - full render starts when the controls rest", true);
        }

        // progress inside ONE slow frame (Ctx.Report from the test): phase text + bar + optional present of the half-done canvas
        int slowFrameNo; double slowPhaseStart; string slowPhase = "";
        void SlowProgress(string phase, double fraction, bool present)
        {
            if (current == null) return;
            double now = clock.Elapsed.TotalMilliseconds;
            if (phase != slowPhase) { if (slowPhase != "" && !measuring) Log($"  {slowPhase}: {now - slowPhaseStart:0} ms"); slowPhase = phase; slowPhaseStart = now; }
            int frames = Math.Max(1, slowTotal);
            // the bar covers the whole run: finished frames + the fraction of the current one
            prog.Value = Math.Clamp((int)(((slowFrameNo - 1) + Math.Clamp(fraction, 0, 1)) * 100 / frames), 0, 100);
            SetStatus($"frame {slowFrameNo}/{frames}: {phase}  ({fraction * 100:0} %)", present || chkProgress.Checked);
            if (present && chkPresent.Checked) Present();
            Application.DoEvents();
        }

        void RenderSlowFrame()
        {
            if (canvas == null || current == null) return;
            FillCtx();
            slowFrameNo = slowTotal - slowLeft + 1; slowPhase = "";
            ctx.Progress = chkProgress.Checked ? SlowProgress : null;   // off: the test runs as one opaque call (what a game would do)
            long t0 = Stopwatch.GetTimestamp();
            if (chkClear.Checked && !current.ClearsItself) canvas.ClearBuffer(unchecked((int)0xFF101418));
            try { RunTest(current); }
            catch (Exception ex) { Log("EXCEPTION in " + current.Name + ": " + ex.Message); current = null; return; }
            long t1 = Stopwatch.GetTimestamp();
            ctx.Progress = null;
            if (slowPhase != "" && !measuring) Log($"  {slowPhase}: {clock.Elapsed.TotalMilliseconds - slowPhaseStart:0} ms");
            slowRenderMs += (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            current.Used |= ctx.Used;
            UpdateVisible(current.Used);
            DrawOverlays();
            if (chkPresent.Checked) Present();
            Application.DoEvents();   // keep the window alive between multi-second frames
        }

        void OpenTestFile()
        {
            if (current == null || current.FileFilter == null) return;
            using var dlg = new OpenFileDialog { Filter = current.FileFilter, Title = "Open a file for: " + current.Name, CheckFileExists = true, Multiselect = current.FileMulti };
            if (current.FilePath != null) { try { dlg.InitialDirectory = Path.GetDirectoryName(current.FilePath.Split('|')[0]); } catch { } }
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            current.FilePath = string.Join("|", dlg.FileNames);
            btnFile.Text = "Open file... (" + (dlg.FileNames.Length > 1 ? dlg.FileNames.Length + " files" : Path.GetFileName(dlg.FileName)) + ")";
            Log("file: " + current.FilePath);
            canvas?.ClearBuffer(unchecked((int)0xFF101418));
            StartSlowRun();
        }

        void Present()
        {
            if (canvas == null) return;
            IntPtr hdc = GetDC(surf.Handle);
            try
            {
                var hr = new HandleRef(surf, hdc);
                canvas.PaintToDevice(hr);
            }
            finally { _ = ReleaseDC(surf.Handle, hdc); }
        }

        /// <summary>
        /// Helper graphics on top of the frame: text labels the test registered with Ctx.Label, the pivot / centre marker of
        /// the draggable object, the edge handles of a resizable one. Drawn INTO the canvas with SR2D's own ClearRect / lines
        /// (Sprite.DrawText, a few microseconds) after the timed region and before the single present, so there is no second
        /// paint on the window (no flicker) and nothing GDI+ in the frame. The next frame's clear removes them.
        /// </summary>
        void DrawOverlays()
        {
            if (canvas == null || current == null || inSuite) return;
            bool hasLabels; lock (ctx.Labels) hasLabels = ctx.Labels.Count > 0;
            bool pivot = !current.Camera && !current.ShadowByMouse && !current.DragAnywhere && !current.GrabBackdrop && !current.Frame && ((current.Used | ctx.Used) & Param.Mouse) != 0;
            canvas.SetLockRect();
            if (current.Frame) frame.Draw(canvas);
            DrawInfoPanel(pivot);
            if (!hasLabels && !pivot && !current.ShadowByMouse) return;
            if (current.ShadowByMouse)
            {   // arrow from the sprite centre to the shadow offset
                int cx = canvas.Width / 2, cy = canvas.Height / 2; float a = tbAngle.Value * MathF.PI / 180f;
                int ex = cx + (int)MathF.Round(MathF.Cos(a) * tbZ.Value), ey = cy + (int)MathF.Round(MathF.Sin(a) * tbZ.Value);
                canvas.DrawArrow(cx, cy, ex, ey, unchecked((int)0xFFFF5050), 2f, 10f, 8f, true);
                canvas.DrawText(ex + 8, ey + 8, $"distance {tbZ.Value} px, {tbAngle.Value} deg", unchecked((int)0xFFFFFFFF), unchecked((int)0xFF000000));
            }
            if (hasLabels)
            {
                (int x, int y, string text)[] items; lock (ctx.Labels) items = ctx.Labels.ToArray();
                foreach (var (x, y, text) in items) canvas.DrawText(x, y, text, unchecked((int)0xFFFFFFFF), unchecked((int)0xFF000000));
            }
            if (pivot)
            {
                const int w = unchecked((int)0xFFFFFFFF), r = unchecked((int)0xFFFF5050), k = unchecked((int)0xFF000000);
                if (current.MouseRotates || current.PivotByClick)
                {   // pivot marker (rotate / pivot tests only): white cross with a black outline so it reads on any background; short arms so the corner rotate handles stay visible
                    canvas.DrawLine(mouseX - 9, mouseY, mouseX + 9, mouseY, k, 4); canvas.DrawLine(mouseX, mouseY - 9, mouseX, mouseY + 9, k, 4);
                    canvas.DrawLine(mouseX - 8, mouseY, mouseX + 8, mouseY, w, 2); canvas.DrawLine(mouseX, mouseY - 8, mouseX, mouseY + 8, w, 2);
                    for (int i = 0; i < 8; i++) { float a0 = i * MathF.PI / 4, a1 = a0 + MathF.PI / 4; canvas.DrawLine((int)MathF.Round(mouseX + 3 * MathF.Cos(a0)), (int)MathF.Round(mouseY + 3 * MathF.Sin(a0)), (int)MathF.Round(mouseX + 3 * MathF.Cos(a1)), (int)MathF.Round(mouseY + 3 * MathF.Sin(a1)), r); }
                }
                else if (!current.Resizable)
                {   // plain draggable object: a faint frame around its bounding box shows what the hand grabs (no cross - nothing rotates about a point here)
                    var cs = ObjectCorners(); int fcol = dragging ? unchecked((int)0xA0FFFFFF) : unchecked((int)0x50FFFFFF);
                    for (int i = 0; i < 4; i++) { var p0 = cs[i]; var p1 = cs[(i + 1) & 3]; canvas.DrawWideLine(p0.X, p0.Y, p1.X, p1.Y, fcol, 1f, true, SR2D.LineOp.AlphaBlend); }
                }
                if (current.Resizable)
                {   // the frame turns with the object; handles at the corners and the edge centres
                    var cs = ObjectCorners(); var sz = ObjectSize();
                    for (int i = 0; i < 4; i++) { var p0 = cs[i]; var p1 = cs[(i + 1) & 3]; canvas.DrawLine((int)MathF.Round(p0.X), (int)MathF.Round(p0.Y), (int)MathF.Round(p1.X), (int)MathF.Round(p1.Y), w, 2); }
                    foreach (var p in new[] { cs[0], cs[1], cs[2], cs[3], ToCanvas(sz.Width / 2, 0), ToCanvas(sz.Width, sz.Height / 2), ToCanvas(sz.Width / 2, sz.Height), ToCanvas(0, sz.Height / 2) })
                    { int hx = (int)MathF.Round(p.X), hy = (int)MathF.Round(p.Y); canvas.ClearRect(hx - 3, hx + 3, hy - 3, hy + 3, w); }
                }
                if (RotatesByMouse)
                {   // editor-style frame: thin outline of the object + rotate handles a little outside each corner
                    var cs = ObjectCorners(); var sz = ObjectSize();
                    int fcol = rotating ? unchecked((int)0xFFFFD040) : unchecked((int)0x90FFFFFF);
                    if (!current.Resizable) for (int i = 0; i < 4; i++) { var p0 = cs[i]; var p1 = cs[(i + 1) & 3]; canvas.DrawWideLine(p0.X, p0.Y, p1.X, p1.Y, fcol, 1f, true, SR2D.LineOp.AlphaBlend); }
                    for (int i = 0; i < 4; i++)
                    {   // small curved arrow outside each corner, pointing away from the object's centre
                        var cc = ToCanvas(sz.Width / 2, sz.Height / 2); float vx = cs[i].X - cc.X, vy = cs[i].Y - cc.Y, vl = MathF.Max(1f, MathF.Sqrt(vx * vx + vy * vy));
                        float hx = cs[i].X + vx / vl * 12f, hy = cs[i].Y + vy / vl * 12f;
                        float ang0 = MathF.Atan2(vy, vx) * 180f / MathF.PI;
                        var arc = new Sprite.PathBuilder(); arc.ArcAround(hx, hy, 6f, ang0 - 60f, 120f);
                        canvas.DrawPath(arc, fcol, 2f, true, SR2D.LineOp.AlphaBlend, true);
                    }
                }
            }
        }

        /// <summary>
        /// Info panel in the top-left corner of the viewport: fps / timing line, the object's angle, scale, pivot and
        /// position when the test has one, then the test's own Ctx.Info lines. Drawn into the canvas like the other overlays.
        /// </summary>
        void DrawInfoPanel(bool hasObject)
        {
            if (canvas == null || current == null) return;
            var lines = new List<(string text, int col)>();
            const int white = unchecked((int)0xFFFFFFFF), soft = unchecked((int)0xFFC8DCFF), warm = unchecked((int)0xFFFFC878);
            if (statsLine.Length > 0) lines.Add((statsLine, white));
            if (ctx.Note.Length > 0) lines.Add((ctx.Note, white));
            if (hasObject)
            {
                var sb = new StringBuilder();
                if (current.MouseRotates)
                {
                    float shown = ObjectAngle() * 180f / MathF.PI; shown = ((shown % 360) + 360) % 360;
                    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"angle {tbAngle.Value} deg").Append(current.AnimSpin != 0 && chkAnim.Checked ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $" (+spin = {shown:0} deg on screen)") : "").Append("   ");
                }
                if (((current.Used | ctx.Used) & Param.Scale) != 0) { var (sx, sy) = ObjectScale(); sb.Append(MathF.Abs(sx - sy) < 1e-4f ? $"scale x{sx:0.00}   " : $"scale x{sx:0.00} / y{sy:0.00}   "); }
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"position {mouseX},{mouseY}");
                if (current.MouseRotates || current.PivotByClick) sb.Append("   pivot ").Append(pivotX < 0 ? "centre (right click = set)" : $"{pivotX:0.#},{pivotY:0.#}" + (current.ObjectSize == null ? " px" : " units"));
                lines.Add((sb.ToString(), soft));
                if (current.MouseRotates) lines.Add((rotating ? "rotating - Shift = 15 degree steps" : "drag inside = move, drag the ring around it = rotate, wheel = 5 degree steps", warm));
            }
            string[] extra; lock (ctx.InfoLines) extra = ctx.InfoLines.ToArray();
            foreach (var t in extra) lines.Add((t, soft));
            lastPanel = Rectangle.Empty;
            if (lines.Count == 0) return;
            int y = 8;
            foreach (var (text, col) in lines) { var r = canvas.DrawText(8, y, text, col, unchecked((int)0xC0000000), 1, 0, 0, SR2D.LineOp.AlphaBlend, 128, TextAnchor.TopLeft, null, unchecked((int)0xFF101014), 1); lastPanel = lastPanel.IsEmpty ? r : Rectangle.Union(lastPanel, r); y += 16; }   // pixel font + drop shadow: readable over any test's background
        }
        Rectangle lastPanel;                            // where the info panel was drawn last (for a standalone status repaint)

        // -------------------------------------------------------------- suite
        readonly List<string> csv = new List<string>();

        void RunSuite()
        {
            if (canvas == null || inSuite) return;
            inSuite = true;
            btnSuite.Enabled = false;
            try
            {
                bool present = chkPresent.Checked;
                var sw = new Stopwatch();
                int failed = 0;
                csv.Clear();
                csv.Add("group;test;count;ms_per_frame;ms_per_op;fps_equivalent;dll;kernels;parallel;gdi_surface");
                Log("=== suite start: " + Caps.Summary);
                Log($"{"test",-62} {"ms/frame",10} {"ms/op",10} {"fps",8}");
                string dllName = Path.GetFileName(Caps.Path);
                foreach (var t in tests)
                {
                    if (!t.Available) { Log($"{t.Name,-62} {"skipped (needs new DLL)",10}"); continue; }
                    if (t.SlowFrames > 0) { Log($"{t.Name,-62} {"skipped (slow test - run it by hand)",10}"); continue; }
                    try
                    {
                        FillCtx();
                        ctx.X = canvas.Width / 2; ctx.Y = canvas.Height / 2;      // deterministic position
                        ctx.Count = Math.Max(1, tbCount.Value);
                        // warm up
                        for (int i = 0; i < 3; i++) { if (!t.ClearsItself) canvas.ClearBuffer(0); RunTest(t); }
                        // measure: at least 300 ms or 20 frames, take median of per-frame times
                        var samples = new List<double>();
                        sw.Restart();
                        while (sw.ElapsedMilliseconds < 300 || samples.Count < 20)
                        {
                            long a = Stopwatch.GetTimestamp();
                            if (!t.ClearsItself) canvas.ClearBuffer(0);
                            ctx.Time = (float)(clock.Elapsed.TotalSeconds % 1000);
                            RunTest(t);
                            samples.Add((Stopwatch.GetTimestamp() - a) * 1000.0 / Stopwatch.Frequency);
                            if (samples.Count > 2000) break;
                        }
                        samples.Sort();
                        double med = samples[samples.Count / 2];
                        double perOp = med / ctx.Count;
                        Log($"{t.Name,-62} {med,10:0.000} {perOp,10:0.000} {1000.0 / med,8:0.0}");
                        csv.Add(string.Join(";", t.Group, t.Name, ctx.Count, med.ToString("0.0000", CultureInfo.InvariantCulture), perOp.ToString("0.0000", CultureInfo.InvariantCulture), (1000.0 / med).ToString("0.0", CultureInfo.InvariantCulture), dllName, Caps.SimdName, chkPar.Checked ? 1 : 0, chkGdi.Checked ? 1 : 0));
                    }
                    catch (Exception ex)
                    {
                        // one broken test must not hide the rest (and must never wedge the suite flag)
                        failed++;
                        Log($"{t.Name,-62} {"FAILED: " + ex.GetType().Name + " " + ex.Message,10}");
                    }
                    if (t.Check != null)
                    {   // the correctness half: render one deterministic frame and ask the test to assert on it
                        try
                        {
                            FillCtx();
                            ctx.X = canvas!.Width / 2; ctx.Y = canvas.Height / 2;
                            if (!t.ClearsItself) canvas.ClearBuffer(0);
                            RunTest(t);
                            var verdict = t.Check(ctx);
                            if (verdict != null) { failed++; Log($"{t.Name,-62} {"CHECK FAILED: " + verdict,10}"); }
                            else Log($"{t.Name,-62} {"check ok",10}");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Log($"{t.Name,-62} {"CHECK THREW: " + ex.GetType().Name + " " + ex.Message,10}");
                        }
                    }
                    if (present) Present();
                    Application.DoEvents();
                }
                Log(failed == 0 ? "=== suite done" : $"=== suite done: {failed} FAILED");
            }
            finally
            {
                btnSuite.Enabled = true;
                inSuite = false;
            }
        }

        void SaveCsv()
        {
            if (csv.Count < 2) { MessageBox.Show("Run the suite first."); return; }
            using var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"sr2d_{Caps.SimdName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
            if (dlg.ShowDialog(this) == DialogResult.OK) File.WriteAllLines(dlg.FileName, csv, Encoding.UTF8);
        }

        /// <summary>Set from the command line (<c>--shots &lt;dir&gt;</c>): writes one PNG per test and exits.</summary>
        internal string? ShotsDir;

        /// <summary>
        /// The instrument of the make-sense audit: every test is selected through the real UI path (captions, start
        /// parameters, strip, sprite size), rendered for one deterministic frame and saved as a single PNG with its
        /// control strip above the canvas, so the strip demos are photographed too. Plain WinForms children of a
        /// strip (a native TrackBar) have no SR2D picture and are not in the shot; the manifest says so.
        /// </summary>
        void RunShots(string dir)
        {
            Directory.CreateDirectory(dir);
            resizeTimer.Stop();          // a resize during the walk would rebuild the canvas under a running shot
            var man = new List<string> { "index\tgroup\ttest\tfile\tstatus\tstrip\tcolors\tingk_px\thash\tnote" };
            var seen = new Dictionary<long, string>();
            int n = 0, shots = 0;
            foreach (var t in tests)
            {
                n++;
                string file = "", status, strip = "none", note = t.Desc.Length > 60 ? $"desc {t.Desc.Length} chars" : "";
                long hash = 0; int colors = 0, ink = 0;
                if (!t.Available) status = "SKIP-DLL";
                else if (t.FileFilter != null && t.FilePath == null) status = "SKIP-NOFILE";
                else
                {
                    try
                    {
                        current = null;
                        lstTests.SelectedIndex = listItems.IndexOf(t);
                        // Selecting a test rebuilds the strip and shortens / lengthens the canvas below it, and that
                        // layout only settles when the queue is pumped. Without this the canvas kept the height the
                        // previous test's strip left it at, so every test after a strip test was shot at a different
                        // size between runs (measured: 1380x1144 vs 1380x1276 for the tests after the VoxelBox strip).
                        Application.DoEvents();
                        // The refit itself lives in the 150 ms resize timer, and this tight loop never gives it 150 ms, so
                        // a strip that appeared or went away left the canvas SPRITE one strip-height too short: every test
                        // after the first strip test lost 292 px of picture (measured: 1380x1276 -> 1380x984 once the
                        // output bench became test 1). Refit directly here; RebuildCanvas keeps the old sprite when the
                        // settled layout did not change the size, so this is cheap for the long runs of same-sized tests.
                        RebuildCanvas();
                        FillCtx();
                        ctx.X = canvas!.Width / 2; ctx.Y = canvas.Height / 2;
                        ctx.Time = 0.25f;                     // the same frame every run: shots must be comparable
                        ctx.Preview = false;
                        mouseX = ctx.X; mouseY = ctx.Y;       // the overlay puts the pivot / grab frame under the pointer
                        canvas.ClearBuffer(unchecked((int)0xFF101418));
                        RunTest(t);
                        // A test is its render plus its overlays: Ctx.Label names each column and the info panel carries
                        // the note and the readouts, so a shot without them shows less than the user sees. statsLine is
                        // the fps / ms line, which changes every run - blank it so the shots stay comparable.
                        var savedStats = statsLine; statsLine = "";
                        DrawOverlays();
                        statsLine = savedStats;
                        int sh = stripHost.Visible ? stripHost.Height : 0;
                        using var pic = new Sprite(canvas.Width, canvas.Height + sh, SR2D.Op.Paint);
                        pic.ClearBuffer(unchecked((int)0xFF101418));
                        if (sh > 0) { Compose(pic, stripHost, 0, 0); strip = "strip"; }
                        pic.Draw(canvas, 0, sh, SR2D.Op.Paint);
                        file = $"{n:000}_{Slug(t.Group, 28)}_{Slug(t.Name, 46)}.png";
                        pic.SavePng(Path.Combine(dir, file));
                        var st = Stat(pic, unchecked((int)0xFF101418));
                        colors = st.colors; ink = st.ink; hash = st.hash; shots++;
                        status = t.SlowFrames > 0 ? "OK-SLOW" : ink == 0 ? "BLANK" : "OK";
                        if (t.SettingsControl != null)
                        {   // the extra settings control (the curve editor of the tangent test, ...) lives in the right pane
                            try
                            {
                                var sc = t.SettingsControl();
                                sc.SetBounds(0, 0, Math.Max(1, sc.Width), Math.Max(1, sc.Height));
                                using var sp = new Sprite(sc.Width, sc.Height, SR2D.Op.Paint);
                                sp.ClearBuffer(unchecked((int)0xFF101418));
                                Compose(sp, sc, 0, 0);
                                sp.SavePng(Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "_settings.png"));
                                sc.Dispose();
                                note = (note.Length > 0 ? note + "; " : "") + "settings shot";
                            }
                            catch (Exception ex2) { note = (note.Length > 0 ? note + "; " : "") + "settings: " + ex2.GetType().Name; }
                        }
                    }
                    catch (Exception ex) { status = "THREW"; note = ex.GetType().Name + ": " + ex.Message; }
                }
                if (hash != 0 && seen.TryGetValue(hash, out var dup)) status = "DUP of " + dup;
                else if (hash != 0) seen[hash] = file;
                man.Add($"{n}\t{t.Group}\t{t.Name}\t{file}\t{status}\t{strip}\t{colors}\t{ink}\t{hash:x16}\t{note}");
                if (n % 10 == 0) Application.DoEvents();
            }
            File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), man, Encoding.UTF8);
            Log($"=== shots: {shots} / {tests.Count} tests rendered into {dir}");
        }

        /// <summary>Paints a control tree into one sprite: every SR2D control's own picture at its absolute offset, deepest
        /// (back-most in WinForms z-order) first so a container cannot cover its children.</summary>
        static void Compose(Sprite dst, Control host, int ox, int oy)
        {
            if (!host.Visible) return;
            ox += host.Left; oy += host.Top;
            if (host is SpriteBox sb) { try { dst.Draw(sb.RenderOnce(), ox, oy, SR2D.Op.AlphaOver); } catch { } }
            // a plain PictureBox has no picture of its own (the output bench blits into its DC, which a capture of the
            // control tree cannot see), so the bench tags its back buffer on the control for the shot
            else if (host is PictureBox pb && pb.Tag is OutputDemo.Pane pane && pane.Buffer != null)
            { try { dst.Draw(pane.Buffer, ox, oy, SR2D.Op.Paint); } catch { } }
            for (int i = host.Controls.Count - 1; i >= 0; i--) Compose(dst, host.Controls[i], ox, oy);
        }

        /// <summary>How much of a picture is real: distinct colours, pixels that are not the backdrop, and a hash to
        /// catch two tests that draw the same thing.</summary>
        static (int colors, int ink, long hash) Stat(Sprite s, int bg)
        {
            var set = new HashSet<int>(); int ink = 0; long h = 17;
            foreach (var v in s.Pixels) { h = (h ^ (uint)v) * 1099511628211L; if (v != bg) ink++; set.Add(v); }
            return (set.Count, ink, h);
        }

        static string Slug(string s, int max)
        {
            var b = new StringBuilder(Math.Min(s.Length, max));
            foreach (var ch in s)
            {
                if (b.Length >= max) break;
                b.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_');
            }
            return b.ToString();
        }

        void Log(string s)
        {
            log.AppendLine(s);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            running = false;
            _ = timeEndPeriod(1);
            assets?.Dispose(); canvas?.Dispose(); temp?.Dispose();
            Tests.ReleaseAnimatedVectorCache();
            foreach (var t in bandTemps.Values) t.Dispose();
            base.OnFormClosed(e);
        }
    }

    /// <summary>
    /// The bench's parameter slider: a <see cref="SpriteSlider"/> with the integer Minimum / Maximum / Value the bench
    /// code was written for (the TrackBar it replaces had those), caption + value drawn by the control itself, whole-step
    /// snapping and a SetRange that keeps the value inside the new range (a TrackBar threw when the order was wrong).
    /// </summary>
    internal sealed class ParamSlider : SpriteSlider
    {
        public ParamSlider() { Snap = true; Step = 1; Decimals = 0; ShowValue = true; Size = new Size(276, 42); }
        [System.ComponentModel.DefaultValue(0)] public new int Minimum { get => (int)Math.Round(base.Minimum); set => base.Minimum = value; }
        [System.ComponentModel.DefaultValue(100)] public new int Maximum { get => (int)Math.Round(base.Maximum); set => base.Maximum = value; }
        [System.ComponentModel.DefaultValue(0)] public new int Value { get => (int)Math.Round(base.Value); set => base.Value = value; }
        /// <summary>Changes both ends at once; the value is clamped into the new range (no exception for min &gt; old max).</summary>
        public void SetRange(int min, int max)
        {
            if (min > max) (min, max) = (max, min);
            base.Minimum = Math.Min(min, base.Minimum); base.Maximum = Math.Max(max, base.Maximum);
            base.Value = Math.Clamp(base.Value, min, max);
            base.Minimum = min; base.Maximum = max;
        }
    }
}
