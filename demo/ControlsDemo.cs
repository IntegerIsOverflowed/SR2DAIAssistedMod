using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // ImplicitUsings also brings System.Threading.Timer

namespace Sr2d64CSport
{
    /// <summary>
    /// The "SR2D controls" bench test: a strip of <see cref="SpriteKnob"/> / <see cref="SpriteSlider"/> controls (plus one
    /// native TrackBar bound to the same value, for a side-by-side feel of the drag responsiveness) above the canvas.
    /// The canvas part of the test (in Tests.cs) reads the static values and draws a sprite rotated / scaled / blended by them.
    /// </summary>
    internal static class ControlsDemo
    {
        // values the canvas test reads (set by the controls, UI thread only)
        public static double AngleDeg, Offset, ScalePct = 100, Blend = 128, Brite = 100;
        public static int Events;                       // ValueChanged count (shows how many updates a quick drag delivered)
        public static string LastSource = "", OffsetMode = "Endless";
        public static double Hue = 200, Level = 1;              // wheels: hue tint (0..360, wraps) and blur level (1..8)

        /// <summary>Height the strip wants (the bench sizes its row to this; the canvas below shrinks accordingly).</summary>
        public const int StripHeight = 318;

        public static Control Build()
        {
            // two rows: knobs (with their option boxes) on top, sliders below - everything visible without scrolling at 1100 px
            var strip = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.FromArgb(0x20, 0x24, 0x28), Padding = new Padding(4, 2, 4, 2) };
            strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));
            strip.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var rowA = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Margin = new Padding(0), BackColor = Color.Transparent };
            var rowB = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Margin = new Padding(0), BackColor = Color.Transparent };
            strip.Controls.Add(rowA, 0, 0); strip.Controls.Add(rowB, 0, 1);

            // 1. angular knob: the pointer IS the value (270-degree arc)
            var angle = new SpriteKnob { Text = "Angle", Minimum = 0, Maximum = 360, Value = 0, Step = 1, Unit = "\u00b0", Size = new Size(104, 132), Margin = new Padding(4) };
            angle.ValueChanged += (s, _) => { AngleDeg = angle.Value; Bump("Angle knob (Angular, Arc)"); };

            // 2. the configurable knob: drag mode x gauge x handle x turns - every combination is one control
            var offset = new SpriteKnob { Text = "Offset", Minimum = -100, Maximum = 100, Value = 0, Step = 1, DragMode = KnobDragMode.Endless, Gauge = KnobGauge.Spiral, Turns = 4, Unit = " px", AccentColor = Color.FromArgb(0xFF, 0x90, 0x30), Size = new Size(136, 168), Margin = new Padding(4) };
            offset.ValueChanged += (s, _) => { Offset = offset.Value; Bump($"Offset knob ({offset.DragMode}, {offset.Gauge}, {offset.Pointer} pointer, {offset.Turns} turns)"); };
            var opts = new TableLayoutPanel { Width = 246, Height = 168, ColumnCount = 3, RowCount = 6, Margin = new Padding(0, 4, 4, 4), BackColor = Color.Transparent };
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82)); opts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82)); opts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            Label Head(string t) => new Label { Text = t, ForeColor = Color.Gainsboro, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
            opts.Controls.Add(Head("Drag"), 0, 0); opts.Controls.Add(Head("Gauge"), 1, 0); opts.Controls.Add(Head("Pointer"), 2, 0);
            void Radios<T>(int col, T current, T[] values, Action<T> set) where T : struct, Enum
            {
                int row = 1;
                foreach (var v in values)
                {
                    var rb = new RadioButton { Text = v.ToString(), ForeColor = Color.Gainsboro, AutoSize = false, Width = 80, Height = 17, Checked = v.Equals(current), Margin = new Padding(0) };
                    var vv = v;
                    rb.CheckedChanged += (s, _) => { if (rb.Checked) { set(vv); Describe(offset); } };
                    opts.Controls.Add(rb, col, row++);
                }
            }
            Radios(0, KnobDragMode.Endless, new[] { KnobDragMode.Angular, KnobDragMode.Endless }, m => offset.DragMode = m);
            Radios(1, KnobGauge.Spiral, new[] { KnobGauge.Arc, KnobGauge.Circle, KnobGauge.Rings, KnobGauge.Spiral, KnobGauge.None }, g => offset.Gauge = g);
            Radios(2, KnobPointer.Bounded, new[] { KnobPointer.Bounded, KnobPointer.Infinite }, h => offset.Pointer = h);
            var turnsRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 0), BackColor = Color.Transparent };
            turnsRow.Controls.Add(new Label { Text = "Turns (winding / coils / rings):", ForeColor = Color.Gainsboro, AutoSize = true, Margin = new Padding(0, 4, 4, 0) });
            var turns = new NumericUpDown { Minimum = 1, Maximum = 10, Value = 4, Width = 44 };
            turns.ValueChanged += (s, _) => { offset.Turns = (int)turns.Value; Describe(offset); };
            turnsRow.Controls.Add(turns);
            opts.Controls.Add(turnsRow, 0, 6); opts.SetColumnSpan(turnsRow, 3); opts.RowCount = 7;
            Describe(offset);

            // 3. a bigger-font knob: same control, TextScale 2 -> caption / value at 10x14 px; Circle gauge, Angular
            var blend = new SpriteKnob { Text = "Blend", Minimum = 0, Maximum = 255, Value = 128, Step = 5, ResetValue = 128, TextScale = 2, Gauge = KnobGauge.Circle, AccentColor = Color.FromArgb(0x60, 0xE0, 0x80), Size = new Size(124, 160), Margin = new Padding(4) };
            blend.ValueChanged += (s, _) => { Blend = blend.Value; Bump("Blend knob (Angular, Circle gauge, TextScale 2)"); };

            // 4. small knobs: 52 px with value, 40 px bare endless jog wheel - the same control scaled down (TextScale auto -> 1)
            var small = new TableLayoutPanel { Width = 58, Height = 168, ColumnCount = 1, RowCount = 3, Margin = new Padding(4), BackColor = Color.Transparent };
            small.RowStyles.Add(new RowStyle(SizeType.Absolute, 68)); small.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); small.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            var tiny1 = new SpriteKnob { Minimum = 0, Maximum = 1, Value = 0.5, Step = 0.05, Decimals = 2, Size = new Size(52, 66), Margin = new Padding(0) };
            var tiny2 = new SpriteKnob { Minimum = 0, Maximum = 1, Value = 0.5, Step = 0.05, ShowValue = false, DragMode = KnobDragMode.Endless, Pointer = KnobPointer.Infinite, Gauge = KnobGauge.Circle, Turns = 2, Size = new Size(40, 40), Margin = new Padding(6, 6, 0, 0) };
            var tiny3 = new SpriteKnob { Minimum = 0, Maximum = 1, Value = 0.3, Step = 0.05, ShowValue = false, Gauge = KnobGauge.Spiral, Turns = 3, Size = new Size(48, 48), Margin = new Padding(2, 2, 0, 0) };
            tiny1.ValueChanged += (s, _) => Bump("small knob 52 px (Arc)");
            tiny2.ValueChanged += (s, _) => Bump("tiny jog wheel 40 px (Endless, Infinite pointer, Circle gauge, 2 turns)");
            tiny3.ValueChanged += (s, _) => Bump("tiny spiral knob 48 px (Angular, Spiral 3 coils)");
            small.Controls.Add(tiny1, 0, 0); small.Controls.Add(tiny2, 0, 1); small.Controls.Add(tiny3, 0, 2);
            var smallLbl = new Label { Text = "same\nclass,\n52 / 40\n/ 48 px", ForeColor = Color.Gainsboro, AutoSize = true, Margin = new Padding(0, 8, 4, 0) };

            rowA.Controls.AddRange(new Control[] { angle, offset, opts, blend, small, smallLbl });

            // 5. slider + native TrackBar bound to the same value
            var column = new TableLayoutPanel { Width = 300, Height = 128, ColumnCount = 1, RowCount = 3, Margin = new Padding(4), BackColor = Color.Transparent };
            column.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            column.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            column.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var scale = new SpriteSlider { Text = "Scale (SpriteSlider)", Minimum = 10, Maximum = 400, Value = 100, Step = 5, Unit = " %", Ticks = 8, ResetValue = 100, TextScale = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
            scale.ValueChanged += (s, _) => { ScalePct = scale.Value; Bump("Scale slider"); };
            var native = new TrackBar { Minimum = 10, Maximum = 400, Value = 100, TickFrequency = 50, Dock = DockStyle.Fill, Margin = new Padding(0), AutoSize = false, Height = 36 };
            var nativeLbl = new Label { Text = "same value on a native TrackBar (compare a quick drag):", Font = new Font(SystemFonts.DefaultFont.FontFamily, 7.5f), ForeColor = Color.Gainsboro, BackColor = Color.Transparent, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(0) };
            bool sync = false;
            native.ValueChanged += (s, _) => { if (sync) return; sync = true; scale.Value = native.Value; sync = false; Bump("native TrackBar"); };
            scale.ValueChanged += (s, _) => { if (sync) return; sync = true; native.Value = (int)Math.Clamp(scale.Value, native.Minimum, native.Maximum); sync = false; };
            column.Controls.Add(scale, 0, 0); column.Controls.Add(nativeLbl, 0, 1); column.Controls.Add(native, 0, 2);

            // 6. vertical slider
            var brite = new SpriteSlider { Text = "Brite", Orientation = Orientation.Vertical, Minimum = 0, Maximum = 200, Value = 100, Step = 5, Unit = "%", Ticks = 4, ResetValue = 100, AccentColor = Color.FromArgb(0xE0, 0x60, 0xC0), Size = new Size(56, 128), Margin = new Padding(4) };
            brite.ValueChanged += (s, _) => { Brite = brite.Value; Bump("Brite slider (vertical)"); };

            // 7. a bipolar slider (fill from zero) and a disabled one, for the looks
            var bip = new SpriteSlider { Text = "Bipolar (fill from 0)", Minimum = -50, Maximum = 50, Value = 20, Step = 1, Bipolar = true, Ticks = 10, Size = new Size(210, 50), Margin = new Padding(4) };
            var dis = new SpriteSlider { Text = "Disabled", Minimum = 0, Maximum = 10, Value = 4, Enabled = false, Size = new Size(160, 50), Margin = new Padding(4) };
            bip.ValueChanged += (s, _) => Bump("bipolar slider");
            var misc = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            misc.Controls.Add(bip); misc.Controls.Add(dis);

            // 8. wheels: a vertical drum (posterise level, SR2D numeric field beside it), a horizontal wrap-around one (hue,
            //    0..360, double-click pops the field up) and a third in the old flat look whose field opens on a plain click
            var level = new SpriteWheel { Text = "Blur", Minimum = 1, Maximum = 8, Value = Level, Step = 1, Edit = WheelEdit.Beside, Size = new Size(112, 128), Margin = new Padding(4), AccentColor = Color.FromArgb(0x60, 0xE0, 0x80) };
            level.ValueChanged += (s, _) => { Level = level.Value; Bump("Blur wheel (vertical, Edit = Beside)"); };
            var hue = new SpriteWheel { Text = "Hue (wraps 0..360, mouse wraps, dbl-click edits)", Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 360, Value = Hue, Step = 5, WrapAround = true, Edit = WheelEdit.DoubleClick, Unit = "\u00b0", Size = new Size(300, 50), Margin = new Padding(4, 4, 4, 0), AccentColor = Color.FromArgb(0xFF, 0x90, 0x30) };
            hue.ValueChanged += (s, _) => { Hue = hue.Value; Bump("Hue wheel (horizontal, WrapAround, WrapMouse, Edit = DoubleClick)"); };
            var hue2 = new SpriteWheel { Text = "same, Style = Flat, click edits, WrapMouse off", Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 360, Value = Hue, Step = 5, WrapAround = true, WrapMouse = false, Style = WheelStyle.Flat, Edit = WheelEdit.Click, Unit = "\u00b0", Size = new Size(300, 50), Margin = new Padding(4, 2, 4, 0), AccentColor = Color.FromArgb(0xFF, 0x90, 0x30) };
            bool hsync = false;
            hue.ValueChanged += (s, _) => { if (hsync) return; hsync = true; hue2.Value = hue.Value; hsync = false; };
            hue2.ValueChanged += (s, _) => { if (hsync) return; hsync = true; hue.Value = hue2.Value; hsync = false; Hue = hue2.Value; Bump("Hue wheel 2 (Flat, Edit = Click)"); };
            var wheels = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            wheels.Controls.Add(hue); wheels.Controls.Add(hue2);

            // 9. CommitOnRelease for every knob / slider / wheel on the strip: the value (and the canvas) follows on release only
            var deferred = new System.Collections.Generic.List<SpriteRangeControl> { angle, offset, blend, tiny1, tiny2, tiny3, scale, brite, bip, level, hue, hue2 };
            var tDefer = new SpriteToggle { Text = "CommitOnRelease (all)", Style = ToggleStyle.CheckBox, Size = new Size(160, 24), Margin = new Padding(4, 4, 4, 0), AccentColor = Color.FromArgb(0xE0, 0x60, 0xC0) };
            tDefer.CheckedChanged += (s, _) => { foreach (var d in deferred) d.CommitOnRelease = tDefer.Checked; Bump("CommitOnRelease " + tDefer.Checked); };
            var tRight = new SpriteToggle { Text = "RightButtonCommits (all)", Style = ToggleStyle.CheckBox, Size = new Size(170, 24), Margin = new Padding(4, 2, 4, 0), AccentColor = Color.FromArgb(0xE0, 0x60, 0xC0) };
            tRight.CheckedChanged += (s, _) => { foreach (var d in deferred) d.RightButtonCommits = tRight.Checked; Bump("RightButtonCommits " + tRight.Checked); };
            var deferLbl = new Label { Text = "CommitOnRelease: the thumb / knob lifts\nwhile you drag, a ghost marks the old\nvalue, release drops it and applies;\nEsc cancels. Wheel / keys apply at once.\nRightButtonCommits: left drag slides live,\nright drag commits on release only.", ForeColor = Color.Gainsboro, AutoSize = true, Margin = new Padding(4, 2, 0, 0), Font = new Font(SystemFonts.DefaultFont.FontFamily, 7.5f) };
            var deferCol = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            deferCol.Controls.Add(tDefer); deferCol.Controls.Add(tRight); deferCol.Controls.Add(deferLbl);

            rowB.Controls.AddRange(new Control[] { column, brite, misc, level, wheels, deferCol });
            return strip;
        }

        static void Describe(SpriteKnob k)
        {
            string drag = k.DragMode == KnobDragMode.Endless ? $"Endless: press = jump, then keep circling; 1 turn = {200.0 / k.Turns:0.#} px, {k.Turns} turns for the whole range, clamps at the ends" : "Angular: the knob faces the pointer, press = jump";
            string gauge = k.Gauge switch { KnobGauge.Arc => "270-degree C arc", KnobGauge.Circle => "one full circle = the whole range", KnobGauge.Rings => "one ring per turn", KnobGauge.Spiral => $"spiral with {k.Turns} coils, lit along its length", _ => "no gauge" };
            string handle = k.Pointer == KnobPointer.Infinite ? "the pointer spins for ever, only the value stops" : "the pointer stops at the ends";
            OffsetMode = $"{drag} | gauge: {gauge} | pointer: {handle}";
        }

        static void Bump(string src) { Events++; LastSource = src; }

        // ------------------------------------------------------------------ buttons / toggles / radios / progress strip
        // values the second canvas test reads
        public static double Progress;                  // 0..100, the fake job
        public static bool Running, Spin = true, Bilinear = true, Backdrop, Grid;
        public static int OpChoice, SizeChoice = 1;     // radios: 0 Paint 1 AlphaBlend 2 Add 3 Blend; 0 small 1 normal 2 big

        public const int ButtonsStripHeight = 330;

        public static Control BuildButtons()
        {
            var strip = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Color.FromArgb(0x20, 0x24, 0x28), Padding = new Padding(4, 2, 4, 2) };
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label Head(string t) => new Label { Text = t, ForeColor = Color.Gainsboro, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 2, 0, 4) };

            // --- column 1: progress bars (H continuous, H segments, ring, vertical, marquee) driven by the fake job
            var pH = new SpriteProgress { Text = "", Size = new Size(280, 22), Margin = new Padding(0, 0, 0, 6) };
            var pSeg = new SpriteProgress { Segments = 16, ShowPercent = false, Size = new Size(280, 14), Margin = new Padding(0, 0, 0, 6), AccentColor = Color.FromArgb(0x60, 0xE0, 0x80) };
            var pMq = new SpriteProgress { Marquee = true, Text = "idle", Size = new Size(280, 16), Margin = new Padding(0, 0, 0, 6), AccentColor = Color.FromArgb(0xFF, 0x90, 0x30) };
            var pRing = new SpriteProgress { Style = ProgressStyle.Ring, Size = new Size(96, 96), Margin = new Padding(0, 0, 8, 0) };
            var pRingSeg = new SpriteProgress { Style = ProgressStyle.Ring, Segments = 12, ShowPercent = false, Size = new Size(96, 96), Margin = new Padding(0, 0, 8, 0), AccentColor = Color.FromArgb(0x60, 0xE0, 0x80) };
            var pRingMq = new SpriteProgress { Style = ProgressStyle.Ring, Marquee = true, Text = "", Size = new Size(48, 48), Margin = new Padding(0, 24, 8, 0), AccentColor = Color.FromArgb(0xFF, 0x90, 0x30) };
            var pV = new SpriteProgress { Style = ProgressStyle.Vertical, ShowPercent = false, Size = new Size(18, 96), Margin = new Padding(0, 0, 4, 0) };
            var pVSeg = new SpriteProgress { Style = ProgressStyle.Vertical, Segments = 10, ShowPercent = false, Size = new Size(18, 96), Margin = new Padding(0), AccentColor = Color.FromArgb(0xE0, 0x60, 0xC0) };
            var bars = new SpriteProgress[] { pH, pSeg, pRing, pRingSeg, pV, pVSeg };
            var rings = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            rings.Controls.AddRange(new Control[] { pRing, pRingSeg, pRingMq, pV, pVSeg });
            var col1 = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            col1.Controls.AddRange(new Control[] { Head("SpriteProgress: horizontal / segments / marquee / ring / vertical"), pH, pSeg, pMq, rings });

            // --- column 2: buttons controlling the job
            var start = new SpriteButton { Text = "Start", Accented = true, Size = new Size(110, 36), Margin = new Padding(0, 0, 6, 6) };
            var pause = new SpriteButton { Text = "Pause", Size = new Size(110, 36), Margin = new Padding(0, 0, 0, 6), Enabled = false };
            var reset = new SpriteButton { Text = "Reset", Shape = ButtonShape.Pill, Size = new Size(110, 36), Margin = new Padding(0, 0, 6, 6) };
            var step = new SpriteButton { Text = "Step +10%", Shape = ButtonShape.Square, Size = new Size(110, 36), Margin = new Padding(0, 0, 0, 6) };
            var round = new SpriteButton { Text = "GO", Shape = ButtonShape.Round, Accented = true, Size = new Size(64, 64), Margin = new Padding(0, 0, 10, 0) };
            var roundSmall = new SpriteButton { Text = "+", Shape = ButtonShape.Round, Size = new Size(40, 40), Margin = new Padding(0, 12, 6, 0), TextScale = 2 };
            var disabled = new SpriteButton { Text = "Disabled", Size = new Size(96, 40), Margin = new Padding(0, 12, 0, 0), Enabled = false };
            var timer = new Timer { Interval = 20 };
            void Show()
            {
                foreach (var b in bars) b.Value = Progress;
                pMq.Text = Running ? "working\u2026" : Progress >= 100 ? "done" : Progress > 0 ? "paused" : "idle";
                if (!Running) { pMq.Redraw(); pRingMq.Redraw(); }
                start.Text = Progress >= 100 ? "Again" : Running ? "Running" : Progress > 0 ? "Resume" : "Start";
                start.Enabled = !Running; pause.Enabled = Running;
            }
            void SetRunning(bool on) { Running = on; if (on) timer.Start(); else timer.Stop(); Show(); }
            timer.Tick += (_, _) =>
            {
                Progress = Math.Min(100, Progress + 100.0 / 8000 * timer.Interval);   // 8 s job
                pMq.Tick(0.02f); pRingMq.Tick(0.02f);
                if (Progress >= 100) SetRunning(false); else Show();
            };
            start.Click += (_, _) => { if (Progress >= 100) Progress = 0; SetRunning(true); Bump("Start button"); };
            pause.Click += (_, _) => { SetRunning(false); Bump("Pause button"); };
            reset.Click += (_, _) => { Progress = 0; SetRunning(false); Bump("Reset button (pill)"); };
            step.Click += (_, _) => { Progress = Math.Min(100, Progress + 10); if (Progress >= 100) SetRunning(false); else Show(); Bump("Step button (square)"); };
            round.Click += (_, _) => { Progress = 0; SetRunning(true); Bump("round GO button"); };
            roundSmall.Click += (_, _) => { Progress = Math.Min(100, Progress + 1); Show(); Bump("small round button"); };
            var btnGrid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0), BackColor = Color.Transparent };
            btnGrid.Controls.Add(start, 0, 0); btnGrid.Controls.Add(pause, 1, 0); btnGrid.Controls.Add(reset, 0, 1); btnGrid.Controls.Add(step, 1, 1);
            var roundRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            roundRow.Controls.AddRange(new Control[] { round, roundSmall, disabled });
            var col2 = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            col2.Controls.AddRange(new Control[] { Head("SpriteButton: rounded / pill / square / round"), btnGrid, roundRow });

            // --- column 3: the four toggle styles
            var tSpin = new SpriteToggle { Text = "Spin the sprite", Style = ToggleStyle.Switch, Checked = true, Size = new Size(270, 30), Margin = new Padding(0, 0, 0, 4) };
            var tBil = new SpriteToggle { Text = "Bilinear filter", Style = ToggleStyle.Ellipse, Checked = true, Size = new Size(270, 30), Margin = new Padding(0, 0, 0, 4), AccentColor = Color.FromArgb(0x60, 0xE0, 0x80) };
            var tBack = new SpriteToggle { Text = "Light backdrop", Style = ToggleStyle.Rocker, Size = new Size(270, 32), Margin = new Padding(0, 0, 0, 4), AccentColor = Color.FromArgb(0xFF, 0x90, 0x30) };
            var tGrid = new SpriteToggle { Text = "Grid lines", Style = ToggleStyle.CheckBox, Size = new Size(270, 28), Margin = new Padding(0, 0, 0, 4) };
            var tDis = new SpriteToggle { Text = "Disabled switch", Style = ToggleStyle.Switch, Checked = true, Enabled = false, Size = new Size(270, 28), Margin = new Padding(0, 0, 0, 4) };
            var tBig = new SpriteToggle { Style = ToggleStyle.Rocker, OnText = "ON", OffText = "OFF", Checked = true, Size = new Size(120, 44), Margin = new Padding(0, 4, 0, 0) };
            tSpin.CheckedChanged += (_, _) => { Spin = tSpin.Checked; Bump("Switch toggle"); };
            tBil.CheckedChanged += (_, _) => { Bilinear = tBil.Checked; Bump("Ellipse toggle"); };
            tBack.CheckedChanged += (_, _) => { Backdrop = tBack.Checked; Bump("Rocker toggle"); };
            tGrid.CheckedChanged += (_, _) => { Grid = tGrid.Checked; Bump("CheckBox toggle"); };
            tBig.CheckedChanged += (_, _) => { tSpin.Checked = tBil.Checked = tBack.Checked = tGrid.Checked = tBig.Checked; Bump("big rocker (sets all four)"); };
            // Animated flag for everything on this strip (toggles, radios) - off = every change snaps
            var tAnim = new SpriteToggle { Text = "Animated (all toggles / radios)", Style = ToggleStyle.CheckBox, Checked = true, Size = new Size(270, 24), Margin = new Padding(0, 6, 0, 0), AccentColor = Color.FromArgb(0xE0, 0x60, 0xC0) };
            var animated = new System.Collections.Generic.List<SpriteControlBase> { tSpin, tBil, tBack, tGrid, tDis, tBig };
            tAnim.CheckedChanged += (_, _) => { foreach (var a in animated) a.Animated = tAnim.Checked; Bump("Animated flag " + tAnim.Checked); };
            var col3 = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            col3.Controls.AddRange(new Control[] { Head("SpriteToggle: Switch / Ellipse / Rocker / CheckBox"), tSpin, tBil, tBack, tGrid, tDis, tBig, tAnim });

            // --- column 4: two radio groups on one panel (GroupName keeps them apart)
            var radios = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Margin = new Padding(0), BackColor = Color.Transparent };
            radios.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); radios.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            radios.Controls.Add(Head("Op (group 'op')"), 0, 0); radios.Controls.Add(Head("Size (group 'size')"), 1, 0);
            string[] ops = { "Paint", "AlphaBlend", "Add", "Blend" }, sizes = { "Small (0.5x)", "Normal", "Big (1.4x)" };
            for (int i = 0; i < ops.Length; i++)
            {
                int ii = i;
                var r = new SpriteRadio { Text = ops[i], GroupName = "op", Checked = i == OpChoice, Size = new Size(140, 24), Margin = new Padding(0, 0, 0, 2) };
                r.CheckedChanged += (_, _) => { if (r.Checked) { OpChoice = ii; Bump("Op radio " + ops[ii]); } }; animated.Add(r);
                radios.Controls.Add(r, 0, i + 1);
            }
            for (int i = 0; i < sizes.Length; i++)
            {
                int ii = i;
                var r = new SpriteRadio { Text = sizes[i], GroupName = "size", Checked = i == SizeChoice, Size = new Size(140, 24), Margin = new Padding(0, 0, 0, 2), AccentColor = Color.FromArgb(0x60, 0xE0, 0x80) };
                r.CheckedChanged += (_, _) => { if (r.Checked) { SizeChoice = ii; Bump("Size radio " + sizes[ii]); } }; animated.Add(r);
                radios.Controls.Add(r, 1, i + 1);
            }
            var rBig = new SpriteRadio { Text = "big radio, own group", GroupName = "solo", Checked = true, Size = new Size(230, 40), Margin = new Padding(0, 6, 0, 0) }; animated.Add(rBig);
            radios.Controls.Add(rBig, 0, 5); radios.SetColumnSpan(rBig, 2);

            strip.Controls.Add(col1, 0, 0); strip.Controls.Add(col2, 1, 0); strip.Controls.Add(col3, 2, 0); strip.Controls.Add(radios, 3, 0);
            strip.Disposed += (_, _) => timer.Dispose();
            Show();
            return strip;
        }
    }

    /// <summary>
    /// Strip of the "standard controls" test: the form furniture (cs/SpriteControls.Static.cs + Input.cs) arranged as a small
    /// settings form - a tab control with a group box, labels, text / numeric boxes, a combo, a list box, LEDs. The statics
    /// drive the canvas (caption, copies, tint, layout).
    /// </summary>
    internal static class FormDemo
    {
        public static string Caption = "SR2D";
        public static int Copies = 3, Spacing = 24, Layout = 0;          // layout: 0 row, 1 column, 2 diagonal, 3 ring
        public static double Tint = 200; public static bool TintOn, Frame = true, Shadow;
        public static readonly List<string> Picked = new();               // list box selection (drawn as tags)
        public static string Status = "idle"; public static int Events; public static string LastSource = "";
        static void Bump(string src) { Events++; LastSource = src; }
        public const int StripHeight = 300;

        static IEnumerable<SpriteControlBase> AllSpriteControls(Control root)
        {
            foreach (Control c in root.Controls) { if (c is SpriteControlBase b) yield return b; foreach (var d in AllSpriteControls(c)) yield return d; }
        }
        public static Control Build()
        {
            var strip = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.FromArgb(0x20, 0x24, 0x28), Padding = new Padding(4, 2, 4, 2) };
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // --- column 1: a tab control; page 1 = a group box with label / text box / numeric / combo rows, page 2 = a sunken panel with LEDs
            var tabs = new SpriteTabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
            var pText = tabs.AddPage("Caption"); var pLayout = tabs.AddPage("Layout"); var pAbout = tabs.AddPage("About");

            var grp = new SpriteGroupBox { Text = "Sprite caption", Dock = DockStyle.Fill, ShowCheck = true, Checked = true };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, BackColor = Color.Transparent, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var lText = new SpriteLabel { Text = "Text", Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 0) };
            var text = new SpriteTextBox { Text = Caption, Placeholder = "type a caption\u2026", MaxLength = 24, Dock = DockStyle.Fill, Height = 24, Margin = new Padding(0, 2, 4, 2) };
            var lCopies = new SpriteLabel { Text = "Copies", Anchor = AnchorStyles.Left };
            var copies = new SpriteNumeric { Minimum = 1, Maximum = 12, Value = Copies, Width = 90, Height = 24, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 4, 2) };
            var lSpacing = new SpriteLabel { Text = "Spacing", Anchor = AnchorStyles.Left };
            var spacing = new SpriteNumeric { Minimum = 0, Maximum = 200, Step = 4, Value = Spacing, Unit = "px", Width = 110, Height = 24, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 4, 2) };
            var lLayout = new SpriteLabel { Text = "Layout", Anchor = AnchorStyles.Left };
            var layout = new SpriteCombo { Width = 150, Height = 24, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 4, 2) };
            layout.SetItems(new[] { "Row", "Column", "Diagonal", "Ring" }, Layout);
            var lBelow = new SpriteLabel { Text = "Spinner\nbelow", Anchor = AnchorStyles.Left, Style = LabelStyle.Muted };
            var below = new SpriteNumeric { Minimum = 0, Maximum = 100, Step = 1, Value = 50, Spinner = SpinnerPlacement.Below, Width = 90, Height = 44, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 4, 2) };
            var lPw = new SpriteLabel { Text = "Password", Anchor = AnchorStyles.Left, Style = LabelStyle.Muted };
            var pw = new SpriteTextBox { PasswordChar = '*', Text = "secret", Width = 110, Height = 24, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 4, 2) };
            grid.Controls.Add(lText, 0, 0); grid.Controls.Add(text, 1, 0);
            grid.Controls.Add(lCopies, 0, 1); grid.Controls.Add(copies, 1, 1);
            grid.Controls.Add(lSpacing, 0, 2); grid.Controls.Add(spacing, 1, 2);
            grid.Controls.Add(lLayout, 0, 3); grid.Controls.Add(layout, 1, 3);
            grid.Controls.Add(lBelow, 0, 4); grid.Controls.Add(below, 1, 4);
            grid.Controls.Add(lPw, 0, 5); grid.Controls.Add(pw, 1, 5);
            grp.Controls.Add(grid);
            pText.Controls.Add(grp);
            text.Committed += (_, _) => { Caption = text.Text; Bump("text box (Enter / focus loss)"); };
            copies.ValueChanged += (_, _) => { Copies = (int)copies.Value; Bump("numeric Copies"); };
            spacing.ValueChanged += (_, _) => { Spacing = (int)spacing.Value; Bump("numeric Spacing"); };
            below.ValueChanged += (_, _) => Bump("numeric (Spinner = Below, drag the buttons) = " + below.Value);
            layout.SelectedIndexChanged += (_, _) => { Layout = layout.SelectedIndex; Bump("combo Layout = " + layout.SelectedItem); };
            grp.CheckedChanged += (_, _) => { Frame = grp.Checked; Bump("group box check " + grp.Checked); };

            var panel = new SpritePanel { Style = PanelStyle.Sunken, Dock = DockStyle.Fill };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            var ledTint = new SpriteLed { Text = "Tint the copies (click)", Clickable = true, On = TintOn, Width = 220 };
            var ledShadow = new SpriteLed { Text = "Drop shadow (click)", Clickable = true, On = Shadow, Shape = LedShape.Square, LedColor = Color.FromArgb(0xFF, 0x90, 0x30), Width = 220 };
            var ledBusy = new SpriteLed { Text = "Blinking bar LED (always on)", On = true, Blink = 600, Shape = LedShape.Bar, LedColor = Color.FromArgb(0xE0, 0x60, 0xC0), Width = 220 };
            var sep = new SpriteSeparator { Text = "tint", Width = 220 };
            var tintSlider = new SpriteSlider { Text = "Hue", Minimum = 0, Maximum = 360, Value = Tint, Size = new Size(220, 44), ShowValue = true, Decimals = 0, Unit = "\u00b0" };
            ledTint.OnChanged += (_, _) => { TintOn = ledTint.On; Bump("LED tint " + ledTint.On); };
            ledShadow.OnChanged += (_, _) => { Shadow = ledShadow.On; Bump("LED shadow " + ledShadow.On); };
            tintSlider.ValueChanged += (_, _) => { Tint = tintSlider.Value; Bump("Hue slider"); };
            flow.Controls.AddRange(new Control[] { ledTint, ledShadow, ledBusy, sep, tintSlider });
            panel.Controls.Add(flow);
            pLayout.Controls.Add(panel);

            var about = new SpriteLabel { Style = LabelStyle.Plain, AutoSize = false, WordWrap = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft,
                Text = "Every control on this strip is drawn by SR2D in the pixel font (or a real font - pick one below, it switches the whole strip through SpriteControlBase.DefaultFontFamily): SpriteTabControl, SpriteGroupBox (the check box in the caption disables the whole group), SpriteLabel (this text wraps), SpriteTextBox, SpriteNumeric, SpriteCombo, SpriteListBox, SpriteLed, SpriteSeparator, SpritePanel. They are ordinary WinForms controls: dock, anchor and the designer work as usual." };
            // font switch: the whole strip (every SpriteControl in the app) via SpriteControlBase.DefaultFontFamily; "" = pixel font
            var fontRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 30, ColumnCount = 2, BackColor = Color.Transparent, Margin = new Padding(0) };
            fontRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96)); fontRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var lFont = new SpriteLabel { Text = "Text font", Anchor = AnchorStyles.Left };
            var fontPick = new SpriteCombo { Width = 220, Height = 24, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 4, 2) };
            var fams = new List<string> { "Pixel font (default)" };
            foreach (var pref in new[] { "Segoe UI", "Arial", "Verdana", "Consolas", "Georgia", "DejaVu Sans", "DejaVu Serif", "DejaVu Sans Mono", "Liberation Sans", "Noto Sans" }) if (ControlFonts.Get(pref) != null) fams.Add(pref);
            fontPick.SetItems(fams.ToArray(), Math.Max(0, fams.IndexOf(SpriteControlBase.DefaultFontFamily)));
            fontPick.SelectedIndexChanged += (s, _) =>
            {
                SpriteControlBase.DefaultFontFamily = fontPick.SelectedIndex <= 0 ? "" : fams[fontPick.SelectedIndex];
                SpriteControlBase.RefreshFontsUnder(strip);   // every control re-measures, containers re-layout, everything repaints
                Bump("Text font = " + (fontPick.SelectedIndex <= 0 ? "pixel font" : fams[fontPick.SelectedIndex]));
            };
            fontRow.Controls.Add(lFont, 0, 0); fontRow.Controls.Add(fontPick, 1, 0);
            pAbout.Controls.Add(about); pAbout.Controls.Add(fontRow);

            // --- column 2: labels in every style + a list box with check boxes
            var col2 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Margin = new Padding(0, 0, 8, 0) };
            col2.RowStyles.Add(new RowStyle(SizeType.AutoSize)); col2.RowStyles.Add(new RowStyle(SizeType.AutoSize)); col2.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var heading = new SpriteLabel { Text = "SpriteLabel styles", Style = LabelStyle.Heading, AutoSize = false, Width = 310, Height = 18, Margin = new Padding(0, 0, 0, 2) };
            var labels = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
            var readout = new SpriteLabel { Text = "0 events", Style = LabelStyle.Readout, Margin = new Padding(0, 0, 6, 4) };
            var badge = new SpriteLabel { Text = "NEW", Style = LabelStyle.Badge, Margin = new Padding(0, 0, 6, 4) };
            var muted = new SpriteLabel { Text = "muted hint text", Style = LabelStyle.Muted, Margin = new Padding(0, 0, 6, 4) };
            var bold = new SpriteLabel { Text = "bold plain", Bold = true, Margin = new Padding(0, 0, 6, 4) };
            var big = new SpriteLabel { Text = "TextScale 2", TextScale = 2, Margin = new Padding(0, 0, 6, 4), ForeColor = Color.FromArgb(0x60, 0xE0, 0x80) };
            labels.Controls.AddRange(new Control[] { readout, badge, muted, bold, big });
            var list = new SpriteListBox { Dock = DockStyle.Fill, CheckBoxes = true, SelectionMode = ListSelection.Multi, ShowLines = true, Margin = new Padding(0, 2, 0, 0) };
            list.SetItems(new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima" });
            void SyncPicked() { Picked.Clear(); foreach (int i in list.CheckedIndices) Picked.Add(list.Items[i]); }
            list.ItemChecked += (_, i) => { SyncPicked(); Bump("list check " + list.Items[i]); };
            list.SelectedIndexChanged += (_, _) => { var sel = list.SelectedIndices; Status = sel.Length == 0 ? "nothing selected" : sel.Length + " selected: " + string.Join(", ", sel.Select(i => list.Items[i])); Bump("list selection"); };
            list.ItemActivated += (_, i) => { list.SetChecked(i, !list.GetChecked(i)); Bump("list double click " + list.Items[i]); };
            col2.Controls.Add(heading, 0, 0); col2.Controls.Add(labels, 0, 1); col2.Controls.Add(list, 0, 2);

            // --- column 3: a raised card with a status read-out and a few buttons that exercise the API from code
            var card = new SpritePanel { Style = PanelStyle.Raised, Dock = DockStyle.Fill, Padding = new Padding(10) };
            var cardFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            var cardHead = new SpriteLabel { Text = "From code", Style = LabelStyle.Heading, AutoSize = false, Width = 250, Height = 18 };
            var status = new SpriteLabel { Text = Status, Style = LabelStyle.Readout, AutoSize = false, Width = 250, Height = 24, Ellipsis = true, Margin = new Padding(0, 4, 0, 6) };
            var bSelect = new SpriteButton { Text = "Select all (list)", Size = new Size(250, 30), Margin = new Padding(0, 0, 0, 4) };
            var bPage = new SpriteButton { Text = "Next tab", Size = new Size(250, 30), Margin = new Padding(0, 0, 0, 4) };
            var bDisable = new SpriteToggle { Text = "Enable group", Style = ToggleStyle.Switch, Checked = true, Size = new Size(250, 28), Margin = new Padding(0, 0, 0, 4) };
            var bTab = new SpriteToggle { Text = "Tabs at the bottom", Style = ToggleStyle.CheckBox, Size = new Size(250, 24), Margin = new Padding(0, 0, 0, 4) };
            bSelect.Click += (_, _) => { list.SelectAll(); Bump("Select all button"); };
            bPage.Click += (_, _) => { tabs.SelectedIndex = (tabs.SelectedIndex + 1) % tabs.TabPages.Count; Bump("Next tab button"); };
            bDisable.CheckedChanged += (_, _) => { grp.Checked = bDisable.Checked; };
            grp.CheckedChanged += (_, _) => { if (bDisable.Checked != grp.Checked) bDisable.Checked = grp.Checked; };
            bTab.CheckedChanged += (_, _) => { tabs.Side = bTab.Checked ? TabSide.Bottom : TabSide.Top; Bump("tab side"); };
            cardFlow.Controls.AddRange(new Control[] { cardHead, status, bSelect, bPage, bDisable, bTab });
            card.Controls.Add(cardFlow);

            var timer = new Timer { Interval = 250 };
            timer.Tick += (_, _) => { readout.Text = Events + " events"; if (status.Text != Status) status.Text = Status; };
            timer.Start();
            strip.Disposed += (_, _) => timer.Dispose();
            strip.Controls.Add(tabs, 0, 0); strip.Controls.Add(col2, 1, 0); strip.Controls.Add(card, 2, 0);
            return strip;
        }
    }

    /// <summary>
    /// Strip of the "Voxel lights" bench test: per lamp a Hue knob, a Strength knob and an on/off switch, plus sliders for
    /// the light reach, the sky level and the lamp energy. The test reads the statics and rebuilds / re-lights the scene
    /// when <see cref="Key"/> changes (Update is timed and shown).
    /// </summary>
    internal static class LightsDemo
    {
        public sealed class Lamp { public double Hue; public int Strength = 15; public bool On = true; public int Color => HueToArgb(Hue); }
        public static readonly Lamp[] Lamps = { new Lamp { Hue = 40 }, new Lamp { Hue = 200 }, new Lamp { Hue = 320 } };
        public static int Reach = 15, Sky = 2;
        public static double LampEnergy = 1.5;
        public const int StripHeight = 190;

        public static string Key() => $"{Reach}|{Sky}|" + string.Join("|", Array.ConvertAll(Lamps, l => $"{l.Hue:0}:{l.Strength}:{l.On}"));
        public static string Describe() => string.Join("  ", Array.ConvertAll(Lamps, l => l.On ? $"hue {l.Hue:0} emit {l.Strength}" : "off"));

        /// <summary>Saturated colour from a hue 0..360 (the emitter colour; white at hue 360 - a plain lamp).</summary>
        public static int HueToArgb(double hue)
        {
            if (hue >= 359.5) return unchecked((int)0xFFFFF4E0);
            double h = hue / 60.0; int i = (int)Math.Floor(h) % 6; double f = h - Math.Floor(h);
            double q = 1 - f, t = f; double r, g, b;
            switch (i) { case 0: r = 1; g = t; b = 0; break; case 1: r = q; g = 1; b = 0; break; case 2: r = 0; g = 1; b = t; break; case 3: r = 0; g = q; b = 1; break; case 4: r = t; g = 0; b = 1; break; default: r = 1; g = 0; b = q; break; }
            // lift the two weak channels a little so the lit surfaces keep some colour instead of going pure primary
            r = 0.2 + 0.8 * r; g = 0.2 + 0.8 * g; b = 0.2 + 0.8 * b;
            return SR2D.ARGB(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        public static Control Build()
        {
            var strip = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, BackColor = Color.FromArgb(0x20, 0x24, 0x28), Padding = new Padding(4, 2, 4, 2) };
            string[] names = { "Room 1 (left)", "Room 2 (middle)", "Room 3 (right)" };
            for (int i = 0; i < 3; i++)
            {
                var lamp = Lamps[i];
                var box = new TableLayoutPanel { Width = 236, Height = 176, ColumnCount = 2, RowCount = 3, Margin = new Padding(4), BackColor = Color.FromArgb(0x26, 0x2B, 0x30) };
                box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116)); box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
                box.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); box.RowStyles.Add(new RowStyle(SizeType.Absolute, 124)); box.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
                var head = new Label { Text = names[i], ForeColor = Color.Gainsboro, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), AutoSize = true, Margin = new Padding(4, 3, 0, 0) };
                box.Controls.Add(head, 0, 0); box.SetColumnSpan(head, 2);
                var hue = new SpriteKnob { Text = "Hue", Minimum = 0, Maximum = 360, Value = lamp.Hue, Step = 5, Unit = "\u00b0", Gauge = KnobGauge.Circle, Size = new Size(108, 122), Margin = new Padding(4, 0, 4, 0), AccentColor = Color.FromArgb(lamp.Color) };
                var str = new SpriteKnob { Text = "Strength", Minimum = 0, Maximum = 15, Value = lamp.Strength, Step = 1, Unit = "", Size = new Size(108, 122), Margin = new Padding(4, 0, 4, 0), AccentColor = Color.FromArgb(0xFF, 0xD0, 0x70) };
                var on = new SpriteToggle { Text = "Lamp on", Style = ToggleStyle.Switch, Checked = lamp.On, Size = new Size(200, 26), Margin = new Padding(4, 2, 4, 0), AccentColor = Color.FromArgb(0x60, 0xE0, 0x80) };
                hue.ValueChanged += (_, _) => { lamp.Hue = hue.Value; hue.AccentColor = Color.FromArgb(lamp.Color); };
                str.ValueChanged += (_, _) => lamp.Strength = (int)str.Value;
                on.CheckedChanged += (_, _) => { lamp.On = on.Checked; hue.Enabled = str.Enabled = on.Checked; };
                box.Controls.Add(hue, 0, 1); box.Controls.Add(str, 1, 1); box.Controls.Add(on, 0, 2); box.SetColumnSpan(on, 2);
                strip.Controls.Add(box);
            }
            var global = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Width = 300, Height = 176, WrapContents = false, Margin = new Padding(4), BackColor = Color.Transparent };
            var reach = new SpriteSlider { Text = "Light reach (cells for a level-15 light)", Minimum = 15, Maximum = 120, Value = Reach, Step = 1, Ticks = 7, ResetValue = 15, Size = new Size(290, 50), Margin = new Padding(0, 0, 0, 4) };
            var sky = new SpriteSlider { Text = "Sky (ambient) level 0..15", Minimum = 0, Maximum = 15, Value = Sky, Step = 1, Ticks = 15, ResetValue = 2, Size = new Size(290, 50), Margin = new Padding(0, 0, 0, 4), AccentColor = Color.FromArgb(0x90, 0xC0, 0xFF) };
            var energy = new SpriteSlider { Text = "Lamp energy (on-screen multiplier, lamps only)", Minimum = 0.1, Maximum = 4, Value = LampEnergy, Step = 0.1, Decimals = 1, Unit = "x", Ticks = 8, ResetValue = 1.5, Size = new Size(290, 50), Margin = new Padding(0), AccentColor = Color.FromArgb(0xFF, 0x90, 0x30) };
            reach.ValueChanged += (_, _) => Reach = (int)reach.Value;
            sky.ValueChanged += (_, _) => Sky = (int)sky.Value;
            energy.ValueChanged += (_, _) => LampEnergy = energy.Value;
            global.Controls.AddRange(new Control[] { reach, sky, energy });
            strip.Controls.Add(global);
            return strip;
        }
    }
}

namespace Sr2d64CSport
{
    /// <summary>
    /// Strip of the "SpriteBox SizeMode" bench test: a SpriteBox showing a 1600 x 1200 procedural picture with a size mode,
    /// zoom, free pan with inertia, scroll bars and the Photoshop-like navigation, next to controls for every option. The
    /// canvas below only prints the view state (the statics here).
    /// </summary>
    internal static class ViewDemo
    {
        public const int StripHeight = 600;
        public static string State = "", Hover = "";
        public static int Frames;

        public static Control Build()
        {
            var strip = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.FromArgb(0x20, 0x24, 0x28), Padding = new Padding(4, 2, 4, 2) };
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));

            var box = new SpriteBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(0x30, 0x34, 0x3A), Margin = new Padding(0, 0, 6, 0), ImageSize = new Size(1600, 1200), SizeMode = SpriteSizeMode.Zoom, ScrollBars = true };
            box.Render += (_, e) => { Picture(e.Surface); Frames++; };
            box.ViewChanged += (_, _) => Describe(box);
            box.MouseMove += (_, e) => { var h = box.ImageAt(e.Location); int px = -1; if (h.Inside) px = box.Surface.Pixels[h.Pixel.Y * box.Surface.Width + h.Pixel.X]; Hover = $"client {e.X},{e.Y} -> image {h.X:0.#},{h.Y:0.#} {(h.Inside ? $"pixel {h.Pixel.X},{h.Pixel.Y} = #{px & 0xFFFFFF:X6}" : "(outside, extrapolated)")}"; };
            box.MouseLeave += (_, _) => Hover = "";

            var col = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0), BackColor = Color.Transparent };
            Label Head(string t) => new Label { Text = t, ForeColor = Color.Gainsboro, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
            col.Controls.Add(Head("SizeMode"));
            var modes = new FlowLayoutPanel { AutoSize = true, Width = 320, Margin = new Padding(0), BackColor = Color.Transparent };
            foreach (SpriteSizeMode m in Enum.GetValues<SpriteSizeMode>())
            {
                var mm = m;
                var r = new SpriteRadio { Text = m.ToString(), GroupName = "mode", Checked = m == box.SizeMode, Size = new Size(104, 22), Margin = new Padding(0, 0, 2, 2) };
                r.CheckedChanged += (_, _) => { if (r.Checked) box.SizeMode = mm; };
                modes.Controls.Add(r);
                box.ViewChanged += (_, _) => { if (box.SizeMode == mm && !r.Checked) r.Checked = true; };
            }
            col.Controls.Add(modes);
            col.Controls.Add(Head("Zoom (multiplier on the mode; Ctrl + wheel / magnifier drag)"));
            var zoom = new SpriteSlider { Text = "log2 zoom", Minimum = -6, Maximum = 6, Value = 0, Step = 0.25, Decimals = 2, Ticks = 12, ResetValue = 0, Size = new Size(316, 46), Margin = new Padding(0) };
            bool zsync = false;
            zoom.ValueChanged += (_, _) => { if (zsync) return; zsync = true; box.Zoom = Math.Pow(2, zoom.Value); zsync = false; };
            box.ViewChanged += (_, _) => { if (zsync) return; zsync = true; zoom.Value = Math.Log2(box.Zoom); zsync = false; };
            col.Controls.Add(zoom);
            var btns = new FlowLayoutPanel { AutoSize = true, Width = 320, Margin = new Padding(0, 2, 0, 0), BackColor = Color.Transparent };
            void Btn(string t, Action a) { var b = new SpriteButton { Text = t, Size = new Size(76, 26), Margin = new Padding(0, 0, 3, 3) }; b.Click += (_, _) => a(); btns.Controls.Add(b); }
            Btn("Reset zoom", box.ResetZoom); Btn("100 %", box.ActualPixels); Btn("Fit", box.FitToView); Btn("Reset pos", box.ResetPan);
            col.Controls.Add(btns);
            col.Controls.Add(Head("Drag (Space or middle button = hand)"));
            var pans = new FlowLayoutPanel { AutoSize = true, Width = 320, Margin = new Padding(0), BackColor = Color.Transparent };
            foreach (SpritePanMode pm in Enum.GetValues<SpritePanMode>())
            {
                var p = pm;
                var r = new SpriteRadio { Text = pm.ToString(), GroupName = "pan", Checked = pm == box.PanMode, Size = new Size(80, 22), Margin = new Padding(0, 0, 2, 2), AccentColor = Color.FromArgb(0x60, 0xE0, 0x80) };
                r.CheckedChanged += (_, _) => { if (r.Checked) box.PanMode = p; };
                pans.Controls.Add(r);
                box.ViewChanged += (_, _) => { if (box.PanMode == p && !r.Checked) r.Checked = true; };
            }
            col.Controls.Add(pans);
            var over = new SpriteSlider { Text = "Overscroll (part of the image that may leave)", Minimum = 0, Maximum = 100, Value = 90, Step = 5, Unit = " %", Ticks = 10, ResetValue = 90, Size = new Size(316, 46), Margin = new Padding(0), AccentColor = Color.FromArgb(0xFF, 0x90, 0x30) };
            over.ValueChanged += (_, _) => box.Overscroll = (float)(over.Value / 100);
            col.Controls.Add(over);
            var fr = new SpriteSlider { Text = "Inertia friction (speed lost per 100 ms)", Minimum = 5, Maximum = 95, Value = 55, Step = 5, Unit = " %", Ticks = 9, ResetValue = 55, Size = new Size(316, 46), Margin = new Padding(0), AccentColor = Color.FromArgb(0xE0, 0x60, 0xC0) };
            fr.ValueChanged += (_, _) => box.Friction = (float)(fr.Value / 100);
            col.Controls.Add(fr);
            var sens = new SpriteSlider { Text = "Inertia sensitivity: slowest flick that slides", Minimum = 0, Maximum = 300, Value = 30, Step = 10, Unit = " px/s", Ticks = 6, ResetValue = 30, Size = new Size(316, 46), Margin = new Padding(0), AccentColor = Color.FromArgb(0xE0, 0x60, 0xC0) };
            sens.ValueChanged += (_, _) => box.InertiaThreshold = (float)sens.Value;
            col.Controls.Add(sens);
            var gain = new SpriteSlider { Text = "Inertia gain (release speed x)", Minimum = 0.5, Maximum = 3, Value = 1.3, Step = 0.1, Decimals = 1, Ticks = 5, ResetValue = 1.3, Size = new Size(316, 46), Margin = new Padding(0), AccentColor = Color.FromArgb(0xE0, 0x60, 0xC0) };
            gain.ValueChanged += (_, _) => box.InertiaGain = (float)gain.Value;
            col.Controls.Add(gain);
            var flags = new FlowLayoutPanel { AutoSize = true, Width = 320, Margin = new Padding(0, 4, 0, 0), BackColor = Color.Transparent };
            void Flag(string t, bool init, Action<bool> set) { var c = new SpriteToggle { Text = t, Style = ToggleStyle.CheckBox, Checked = init, Size = new Size(156, 22), Margin = new Padding(0, 0, 2, 2) }; c.CheckedChanged += (_, _) => set(c.Checked); flags.Controls.Add(c); }
            Flag("Scroll bars", true, v => box.ScrollBars = v); Flag("Inertia (flick)", true, v => box.Inertia = v);
            Flag("Smooth zoom (glide)", true, v => box.SmoothZoom = v); Flag("Pixel grid (from 400 %)", false, v => box.PixelGrid = v);
            Flag("Bilinear magnify", false, v => box.ViewFilter = v ? SR2D.Filter.Bilinear : SR2D.Filter.Auto);
            col.Controls.Add(flags);
            col.Controls.Add(Head("Navigation"));
            var navs = new FlowLayoutPanel { AutoSize = true, Width = 320, Margin = new Padding(0), BackColor = Color.Transparent };
            foreach (SpriteNavigation nv in Enum.GetValues<SpriteNavigation>())
            {
                var n = nv;
                var r = new SpriteRadio { Text = nv.ToString(), GroupName = "nav", Checked = nv == box.Navigation, Size = new Size(80, 22), Margin = new Padding(0, 0, 2, 2), AccentColor = Color.FromArgb(0xFF, 0xD0, 0x70) };
                r.CheckedChanged += (_, _) => { if (r.Checked) box.Navigation = n; };
                navs.Controls.Add(r);
            }
            col.Controls.Add(navs);
            col.Controls.Add(new Label { Text = "Full: plain cursor = magnifier (drag left/right = zoom about the pressed point, click = in, Alt+click = out); Space = hand; wheel scrolls, Shift = sideways, Ctrl = zoom; right click = menu; Ctrl 0 / Ctrl Alt 0 / Ctrl Shift 0 = reset / 100 % / fit.", ForeColor = Color.Gainsboro, AutoSize = false, Width = 316, Height = 70, Font = new Font(SystemFonts.DefaultFont.FontFamily, 7.5f), Margin = new Padding(0, 2, 0, 0) });

            strip.Controls.Add(box, 0, 0); strip.Controls.Add(col, 1, 0);
            Describe(box);
            return strip;
        }

        static void Describe(SpriteBox b)
        {
            var d = b.ImageRectangle; var vp = b.ViewportRectangle;
            State = $"{b.SizeMode}  zoom x{b.Zoom:0.###} = {b.ZoomPercent:0.#} %  image {b.ImageSize.Width}x{b.ImageSize.Height} at {d.X:0},{d.Y:0} {d.Width:0}x{d.Height:0}  viewport {vp.Width}x{vp.Height}  pan {b.Pan.X:0},{b.Pan.Y:0}  {b.PanMode}{(b.View.IsSliding ? "  sliding" : "")}";
        }

        /// <summary>The picture: gradient, a 100 px grid with coordinates, discs, a pixel-art corner (for the grid / nearest look) and fine text.</summary>
        static void Picture(Sprite s)
        {
            int w = s.Width, h = s.Height;
            var px = s.Pixels;
            for (int y = 0; y < h; y++) { int g = 40 + y * 60 / h; for (int x = 0; x < w; x++) px[y * w + x] = unchecked((int)0xFF000000) | ((30 + x * 90 / w) << 16) | (g << 8) | (70 + ((x + y) * 50 / (w + h))); }
            int grid = unchecked((int)0xFF506070), txt = unchecked((int)0xFFE0E8F0);
            for (int x = 0; x < w; x += 100) s.DrawLine(x, 0, x, h - 1, grid);
            for (int y = 0; y < h; y += 100) s.DrawLine(0, y, w - 1, y, grid);
            for (int y = 0; y < h; y += 100) for (int x = 0; x < w; x += 100) s.DrawText(x + 3, y + 3, $"{x},{y}", txt, 0, 1);
            s.FillCircle(w * 0.5f, h * 0.5f, Math.Min(w, h) * 0.3f, unchecked((int)0xFFFF9030), SR2D.LineOp.Set, true);
            s.FillCircle(w * 0.5f, h * 0.5f, Math.Min(w, h) * 0.22f, unchecked((int)0xFF3399FF), SR2D.LineOp.Set, true);
            s.DrawCircle(w * 0.5f, h * 0.5f, Math.Min(w, h) * 0.3f, txt, 3f, true);
            s.DrawText(w / 2, h / 2, "SpriteBox.SizeMode", txt, 0, 4, 1, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
            s.DrawText(w / 2, h / 2 + 50, "zoom in on the corner sprite -> single pixels (grid at 8x)", txt, 0, 2, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
            // pixel-art corner: a 16 x 16 smiley at 1:1 in the top-left cell
            int[] face = { 0x3C, 0x42, 0x81, 0xA5, 0x81, 0xA5, 0x99, 0x42, 0x3C };
            for (int r = 0; r < face.Length; r++) for (int c = 0; c < 8; c++) if ((face[r] >> (7 - c) & 1) != 0) px[(20 + r) * w + 20 + c] = unchecked((int)0xFFFFFF00);
            for (int i = 0; i < 16; i++) px[(40 + i) * w + 20 + i] = unchecked((int)0xFFFF4040);
            s.DrawText(2, h - 2, "bottom-left", txt, 0, 1, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.BottomLeft);
            s.DrawText(w - 2, h - 2, "bottom-right", txt, 0, 1, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.BottomRight);
            s.DrawText(w - 2, 2, "top-right", txt, 0, 1, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopRight);
        }
    }
}

namespace Sr2d64CSport
{
    /// <summary>The "VoxelBox" bench test strip: a <see cref="VoxelBox"/> and a column that picks the scene / edit mode. Everything else is in the box's own right-click menu.</summary>
    internal static class VoxelDemo
    {
        public const int StripHeight = 600;
        public static string State = "", Hit = "";
        static VoxelGrid? loaded;
        static ComboBox? objBox;
        static string Refreshed(VoxelBox box) { RefreshObjects(box); return ""; }
        static void RefreshObjects(VoxelBox box)
        {
            if (objBox == null) return;
            string? keep = objBox.SelectedItem?.ToString();
            objBox.BeginUpdate(); objBox.Items.Clear();
            if (box.Grid != null) foreach (var o in box.Grid.Objects) objBox.Items.Add(o.Name);
            objBox.EndUpdate();
            if (keep != null) objBox.SelectedItem = keep;
            if (objBox.SelectedIndex < 0 && objBox.Items.Count > 0) objBox.SelectedIndex = 0;
        }

        public static Control Build()
        {
            var strip = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.FromArgb(0x20, 0x24, 0x28), Padding = new Padding(4, 2, 4, 2) };
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));

            var box = new VoxelBox { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 6, 0), ShowInfo = true, Fade = VoxelFade.Depth, FadeMin = 0.55f };
            box.Grid = Tests.EnsureVoxelHouse(); box.Zoom = 12; box.Lighting = VoxelLighting.Smooth;
            box.FrameRendered += (_, _) => State = $"{box.View}  zoom x{box.Zoom:0.##}  {box.Lighting}  fade {box.Fade}  {(box.Night ? "night  " : "")}{box.LastDrawnVoxels} voxels drawn in {box.LastFrameMs:0.0} ms  parallel {box.Parallel}  drag mode {box.DragMode}";
            box.TrackHover = true;
            box.VoxelHover += (_, e) => Hit = e.Hit ? $"hover: voxel {e.X},{e.Y},{e.Z} face {e.Face} (index {e.Index})" : "hover: nothing under the mouse";
            box.MouseLeave += (_, _) => Hit = "";
            bool edit = false;
            box.VoxelClick += (_, e) =>
            {
                if (!edit || !e.Hit || box.Grid == null) return;
                var g = box.Grid;
                if ((Control.ModifierKeys & Keys.Shift) != 0) { var (x, y, z) = g.Coords(e.Index); g.Set(x, y, z, Voxel.Empty); }
                else { var (x, y, z) = g.Neighbour(e.Index, e.Face); if (g.Contains(x, y, z)) g.Set(x, y, z, new Voxel(0xFFFF8030, (byte)((Control.ModifierKeys & Keys.Control) != 0 ? 14 : 0), 9)); }
                g.Update(); box.Redraw();
            };

            var col = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0), BackColor = Color.Transparent };
            Label Head(string t) => new Label { Text = t, ForeColor = Color.Gainsboro, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
            col.Controls.Add(Head("Scene (box.Grid = ...)"));
            var scenes = new FlowLayoutPanel { AutoSize = true, Width = 290, Margin = new Padding(0), BackColor = Color.Transparent };
            void Scene(string name, Func<VoxelGrid> make, bool init = false)
            {
                var r = new SpriteRadio { Text = name, GroupName = "scene", Checked = init, Size = new Size(142, 22), Margin = new Padding(0, 0, 2, 2) };
                r.CheckedChanged += (_, _) => { if (!r.Checked) return; var g = make(); box.Grid = g; box.FitToView(); };
                scenes.Controls.Add(r);
            }
            Scene("House 24 cubed", Tests.EnsureVoxelHouse, true);
            Scene("Terrain 64", () => Tests.EnsureVoxels(64, 1, out _));
            Scene("Terrain 128", () => Tests.EnsureVoxels(128, 1, out _));
            Scene("Terrain 256 (slow build)", () => Tests.EnsureVoxels(256, 1, out _));
            col.Controls.Add(scenes);
            var btns = new FlowLayoutPanel { AutoSize = true, Width = 290, Margin = new Padding(0, 2, 0, 0), BackColor = Color.Transparent };
            void Btn(string t, Action a) { var b = new SpriteButton { Text = t, Size = new Size(92, 26), Margin = new Padding(0, 0, 3, 3) }; b.Click += (_, _) => a(); btns.Controls.Add(b); }
            Btn("Open .vox / .obj", () =>
            {
                using var dlg = new OpenFileDialog { Filter = "Voxel models (*.vox;*.obj)|*.vox;*.obj|All files|*.*" };
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try { var g = dlg.FileName.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ? VoxelGrid.FromObj(dlg.FileName, 96) : VoxelGrid.LoadVox(dlg.FileName); g.Update(); loaded?.Dispose(); loaded = g; box.Grid = g; box.FitToView(); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Load failed"); }
            });
            Btn("Fit (F)", box.FitToView); Btn("Reset (Home)", box.ResetCamera);
            Btn("Merge file...", () =>
            {   // second model merged on top of the current one (VoxelGrid.Stacked: new grid, both keep their objects)
                if (box.Grid == null) return;
                using var dlg = new OpenFileDialog { Filter = "Voxel models (*.vox;*.obj)|*.vox;*.obj|All files|*.*", Title = "Model to stack on top" };
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    using var top = dlg.FileName.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ? VoxelGrid.FromObj(dlg.FileName, 64) : VoxelGrid.LoadVox(dlg.FileName);
                    var g = VoxelGrid.Stacked(box.Grid, top, centre: true, onSolid: true, bottomName: box.Grid.Objects.Count == 0 ? "bottom" : null, topName: System.IO.Path.GetFileNameWithoutExtension(dlg.FileName));
                    g.Update(); loaded?.Dispose(); loaded = g; box.Grid = g; box.FitToView(); RefreshObjects(box);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Merge failed"); }
            });
            col.Controls.Add(btns);
            // objects of the grid (VoxelGrid.Objects: from .obj `o` groups, .vox models, or defined by code)
            col.Controls.Add(Head("Objects (grid.Objects: .obj groups / .vox models)"));
            objBox = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(0x30, 0x34, 0x38), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 0, 2) };
            col.Controls.Add(objBox);
            var orow = new FlowLayoutPanel { AutoSize = true, Width = 290, Margin = new Padding(0), BackColor = Color.Transparent };
            void OBtn(string t, Func<VoxelGrid, string, string> op)
            {
                var b = new SpriteButton { Text = t, Size = new Size(68, 24), Margin = new Padding(0, 0, 3, 3), TextScale = 1 };
                b.Click += (_, _) => { if (box.Grid == null || objBox?.SelectedItem == null) return; string n = objBox.SelectedItem.ToString()!; Hit = op(box.Grid, n); box.Grid.Update(); box.Redraw(); };
                orow.Controls.Add(b);
            }
            OBtn("Hide", (g, n) => $"Hide(\"{n}\"): {g.Hide(n)} cells lifted out");
            OBtn("Show", (g, n) => $"Show(\"{n}\"): {g.Show(n)} cells put back");
            OBtn("Remove", (g, n) => { int k = g.Remove(n); RefreshObjects(box); return $"Remove(\"{n}\"): {k} cells erased"; });
            OBtn("Recolour", (g, n) => { var r = new Random(); uint c = 0xFF000000u | (uint)r.Next(0x1000000); return $"Recolor(\"{n}\", #{c:X8}): {g.Recolor(n, c)} cells"; });
            OBtn("Lamp", (g, n) => $"SetData(\"{n}\", emit 12): {g.SetData(n, emit: 12)} cells");
            OBtn("Solo", (g, n) => { int k = 0; foreach (var o in g.Objects.ToArray()) if (!string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase) && !o.Hidden) k += g.Hide(o.Name); return $"solo \"{n}\": {k} other cells hidden"; });
            OBtn("Show all", (g, n) => { int k = 0; foreach (var o in g.Objects.ToArray()) if (o.Hidden) k += Math.Max(0, g.Show(o.Name)); return $"{k} cells put back"; });
            OBtn("Split tags", (g, n) => $"DefineObjectsByMaterial: {g.DefineObjectsByMaterial(keepExisting: true)} new object(s)" + Refreshed(box));
            col.Controls.Add(orow);
            box.GridChanged += (_, _) => RefreshObjects(box);
            box.VoxelHover += (_, e) => { if (e.Hit && box.Grid != null) { var (x, y, z) = box.Grid.Coords(e.Index); var o = box.Grid.ObjectAt(x, y, z); if (o != null) Hit += $"  object '{o.Name}'"; } };
            RefreshObjects(box);
            col.Controls.Add(Head("Left button"));
            var flags = new FlowLayoutPanel { AutoSize = true, Width = 290, Margin = new Padding(0), BackColor = Color.Transparent };
            var ed = new SpriteToggle { Text = "Edit: click = add voxel on the hit face, Shift = remove, Ctrl = lamp", Style = ToggleStyle.CheckBox, Size = new Size(288, 22), Margin = new Padding(0, 0, 2, 2), AccentColor = Color.FromArgb(0xFF, 0x90, 0x30) };
            ed.CheckedChanged += (_, _) => { edit = ed.Checked; box.DragMode = edit ? VoxelDragMode.None : VoxelDragMode.Orbit; };
            flags.Controls.Add(ed);
            void Flag(string t, bool init, Action<bool> set) { var c = new SpriteToggle { Text = t, Style = ToggleStyle.CheckBox, Checked = init, Size = new Size(142, 22), Margin = new Padding(0, 0, 2, 2) }; c.CheckedChanged += (_, _) => set(c.Checked); flags.Controls.Add(c); }
            Flag("Spin", false, v => box.Animate = v); Flag("Night (N)", false, v => box.Night = v);
            Flag("Preview on drag", true, v => box.PreviewWhileDragging = v); Flag("Parallel", true, v => box.Parallel = v);
            col.Controls.Add(flags);
            col.Controls.Add(new Label { Text = "Right click = settings menu (camera presets, voxel mode, lighting tiers + sliders, depth fade, zoom, overlays). Left drag = orbit (presets: pan), middle or Space + left = pan, wheel = zoom about the pointer, arrows = orbit / turn preset, + - = zoom, Home = reset, F = fit, I = info, N = night. In Edit mode the left click goes to VoxelClick; Space + left orbits.", ForeColor = Color.Gainsboro, AutoSize = false, Width = 288, Height = 110, Font = new Font(SystemFonts.DefaultFont.FontFamily, 7.5f), Margin = new Padding(0, 6, 0, 0) });

            strip.Controls.Add(box, 0, 0); strip.Controls.Add(col, 1, 0);
            return strip;
        }
    }
}

namespace Sr2d64CSport
{
    /// <summary>
    /// Control strip of the vector import test: recolouring the loaded <see cref="VectorImage"/> IN PLACE with
    /// <see cref="VectorImage.Recolor(int,int,int,bool)"/> / <see cref="VectorImage.SwapColors(int,int,int,bool)"/> - an edit of the container
    /// (done once, the SVG you save afterwards has the new colour), not a render option. Colour A / B come from the picture's
    /// palette (most-used first), from a click on the canvas (pick mode) or from the system colour dialog.
    /// </summary>
    internal static class RecolorDemo
    {
        public const int StripHeight = 126;
        /// <summary>The image the strip edits (set by the test each frame; null = nothing loaded yet).</summary>
        public static VectorImage? Image;
        public static int ColorA = unchecked((int)0xFF000000), ColorB = unchecked((int)0xFFFF8800);
        public static int Tolerance;
        public static bool KeepAlpha = true;
        /// <summary>What the colour edits touch: fills, outlines or both.</summary>
        public static PaintTarget Target = PaintTarget.Both;
        /// <summary>Another picture merged on top of the shown one (kept so "Merge shuffled" can be repeated; Shift = choose another file).</summary>
        public static VectorImage? Overlay;
        static ComboBox? objBox; static Label? objInfo;
        /// <summary>0 = none, 1 = next canvas click sets A, 2 = sets B (the test reads it in its mouse hook).</summary>
        public static int PickMode;
        public static string Status = "";
        public static int Edits;                                   // recolour operations so far (the test shows it)
        /// <summary>Undo stack: images before each edit (clones; bounded).</summary>
        static readonly System.Collections.Generic.Stack<VectorImage> undo = new System.Collections.Generic.Stack<VectorImage>();

        static SpriteButton? swA, swB; static FlowLayoutPanel? pal;

        public static Control Build()
        {
            var strip = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(0x20, 0x24, 0x28), Padding = new Padding(4, 2, 4, 0), WrapContents = false };
            Label Head(string t) => new Label { Text = t, ForeColor = Color.Gainsboro, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };

            // colour A / B: swatch buttons (click = colour dialog, right click = pick from the canvas)
            var ab = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 8, 0), BackColor = Color.Transparent };
            ab.Controls.Add(Head("Colour A -> B   (click = dialog, right click = pick on the canvas)"));
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            swA = Swatch("A", () => ColorA, v => ColorA = v, 1); swB = Swatch("B", () => ColorB, v => ColorB = v, 2);
            var arrow = new Label { Text = "->", ForeColor = Color.Gainsboro, AutoSize = true, Margin = new Padding(4, 10, 4, 0) };
            var flip = new SpriteButton { Text = "A<>B", Size = new Size(54, 30), Margin = new Padding(6, 3, 0, 0) };
            flip.Click += (_, _) => { (ColorA, ColorB) = (ColorB, ColorA); Refresh(); };
            row.Controls.Add(swA); row.Controls.Add(arrow); row.Controls.Add(swB); row.Controls.Add(flip);
            ab.Controls.Add(row);
            var opts = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 0), BackColor = Color.Transparent };
            var tol = new SpriteSlider { Text = "Tolerance (per channel)", Minimum = 0, Maximum = 64, Value = 0, Step = 1, Ticks = 8, Size = new Size(190, 36), Margin = new Padding(0, 0, 6, 0), TextScale = 1 };
            tol.ValueChanged += (_, _) => Tolerance = (int)tol.Value;
            var keep = new SpriteToggle { Text = "Keep alpha", Style = ToggleStyle.CheckBox, Checked = true, Size = new Size(110, 22), Margin = new Padding(0, 8, 0, 0) };
            keep.CheckedChanged += (_, _) => KeepAlpha = keep.Checked;
            opts.Controls.Add(tol); opts.Controls.Add(keep);
            ab.Controls.Add(opts);
            // target: fill / stroke / both (PaintTarget)
            var trow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 0), BackColor = Color.Transparent };
            trow.Controls.Add(new Label { Text = "Target:", ForeColor = Color.Gainsboro, AutoSize = true, Margin = new Padding(0, 4, 4, 0) });
            void TargetRadio(string t, PaintTarget v, int w)
            {
                var r = new SpriteRadio { Text = t, GroupName = "recolorTarget", Checked = Target == v, Size = new Size(w, 22), Margin = new Padding(0, 0, 4, 0) };
                r.CheckedChanged += (_, _) => { if (r.Checked) { Target = v; lastPalVersion = -1; Refresh(); } };
                trow.Controls.Add(r);
            }
            TargetRadio("Both", PaintTarget.Both, 60); TargetRadio("Fill only", PaintTarget.Fill, 80); TargetRadio("Outline only", PaintTarget.Stroke, 100);
            ab.Controls.Add(trow);
            strip.Controls.Add(ab);

            // actions
            var act = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 8, 0), BackColor = Color.Transparent };
            act.Controls.Add(Head("Edit the container (once; VectorSprite re-rasterises, SaveSvg keeps it)"));
            var brow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            void Btn(string t, Action a, bool accent = false, int w = 96) { var b = new SpriteButton { Text = t, Accented = accent, Size = new Size(w, 30), Margin = new Padding(0, 0, 4, 4) }; b.Click += (_, _) => a(); brow.Controls.Add(b); }
            Btn("Recolor A->B", () => Edit(img => img.Recolor(ColorA, ColorB, Tolerance, KeepAlpha, Target), "Recolor"), true, 110);
            Btn("Swap A<>B", () => Edit(img => img.SwapColors(ColorA, ColorB, Tolerance, KeepAlpha, Target), "SwapColors"), false, 100);
            Btn("Greyscale", () => Edit(img => img.Recolor(c => { int l = (((c >> 16) & 255) * 77 + ((c >> 8) & 255) * 151 + (c & 255) * 28) >> 8; return (c & unchecked((int)0xFF000000)) | (l << 16) | (l << 8) | l; }, Target), "Recolor(greyscale)"));
            Btn("Invert", () => Edit(img => img.Recolor(c => c ^ 0xFFFFFF, Target), "Recolor(invert)"), false, 80);
            Btn("Undo", () => { if (undo.Count > 0 && Image != null) { var prev = undo.Pop(); Image.Shapes.Clear(); Image.Shapes.AddRange(prev.Shapes); Image.ViewBox = prev.ViewBox; Image.Width = prev.Width; Image.Height = prev.Height; Image.VisibleArea = prev.VisibleArea; Image.Touch(); Status = $"undone ({undo.Count} more)"; Refresh(); } }, false, 70);
            act.Controls.Add(brow);
            // order / merge
            var orow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            void OBtn(string t, Action a, int w = 96) { var b = new SpriteButton { Text = t, Size = new Size(w, 30), Margin = new Padding(0, 0, 4, 4) }; b.Click += (_, _) => a(); orow.Controls.Add(b); }
            OBtn("Shuffle order", () => Edit(img => { img.Shuffle(); return img.Shapes.Count; }, "Shuffle"), 108);
            OBtn("Reverse order", () => Edit(img => { img.ReverseOrder(); return img.Shapes.Count; }, "ReverseOrder"), 108);
            OBtn("Merge file...", () => MergeFile(false), 100);
            OBtn("Merge shuffled", () => MergeFile(true), 116);
            OBtn("Remove hidden", () => Edit(img => img.RemoveHidden(), "RemoveHidden"), 110);
            act.Controls.Add(orow);
            act.Controls.Add(new Label { Text = "Palette = the picture's own colours of the chosen target (most used first): left click = A, right click = B", ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 2, 0, 0) });
            strip.Controls.Add(act);

            // objects (shape names)
            var obj = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 8, 0), BackColor = Color.Transparent };
            obj.Controls.Add(Head("Object (SVG id / AI XMLUID / layer name; * wildcards; 'Name unnamed' = autoKindNN)"));
            objBox = new ComboBox { Width = 170, DropDownStyle = ComboBoxStyle.DropDown, BackColor = Color.FromArgb(0x30, 0x34, 0x38), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 0, 3) };
            objBox.SelectedIndexChanged += (_, _) => ObjInfo();
            objBox.TextChanged += (_, _) => ObjInfo();
            obj.Controls.Add(objBox);
            var obrow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            void ObjBtn(string t, Func<VectorImage, string, int> op, string what, int w = 64)
            {
                var b = new SpriteButton { Text = t, Size = new Size(w, 26), Margin = new Padding(0, 0, 3, 3), TextScale = 1 };
                b.Click += (_, _) => { string n = objBox?.Text.Trim() ?? ""; if (n.Length == 0) { Status = "type or choose an object name"; return; } Edit(img => op(img, n), $"{what}(\"{n}\")"); ObjInfo(); };
                obrow.Controls.Add(b);
            }
            ObjBtn("Hide", (img, n) => img.Hide(n), "Hide", 54);
            ObjBtn("Show", (img, n) => img.Show(n), "Show", 54);
            ObjBtn("Remove", (img, n) => img.Remove(n), "Remove", 66);
            ObjBtn("Colour = B", (img, n) => img.SetColor(n, ColorB, Target), "SetColor", 82);
            ObjBtn("A->B here", (img, n) => img.Recolor(n, ColorA, ColorB, Tolerance, KeepAlpha, Target), "Recolor", 80);
            ObjBtn("To front", (img, n) => img.BringToFront(n), "BringToFront", 68);
            ObjBtn("To back", (img, n) => img.SendToBack(n), "SendToBack", 66);
            obj.Controls.Add(obrow);
            // names for the unnamed + the visible-area window
            var wrow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            void WBtn(string t, Action a, int w, string tip) { var b = new SpriteButton { Text = t, Size = new Size(w, 26), Margin = new Padding(0, 0, 3, 3), TextScale = 1 }; b.Click += (_, _) => a(); new ToolTip().SetToolTip(b, tip); wrow.Controls.Add(b); }
            WBtn("Name unnamed", () => Edit(img => img.NameByKind(), "NameByKind"), 96, "NameByKind(): every shape without a name gets autoRect01 / autoEllipse02 / autoPath03 / autoOpenPath04 / autoText05 / autoBitmap06 (per-kind counters, draw order) - PDF / EPS / AI objects become addressable like SVG ids");
            WBtn("Window = obj", () => { string n = objBox?.Text.Trim() ?? ""; if (n.Length == 0) { Status = "type or choose an object name"; return; } Edit(img => img.SetVisibleArea(n) ? 1 : 0, $"SetVisibleArea(\"{n}\")"); }, 92, "SetVisibleArea(name): show only the bounding box of this object (render-level clip; view box unchanged). Hide the object afterwards to use it as a frame.");
            WBtn("Crop", () => Edit(img => { img.CropToVisibleArea(); return 1; }, "CropToVisibleArea"), 50, "CropToVisibleArea(): the view box (Width x Height) becomes the window");
            WBtn("Clear window", () => Edit(img => { img.ClearVisibleArea(); return 1; }, "ClearVisibleArea"), 92, "ClearVisibleArea(): the whole picture again (the view box stays as it is now)");
            obj.Controls.Add(wrow);
            objInfo = new Label { Text = "", ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0) };
            obj.Controls.Add(objInfo);
            strip.Controls.Add(obj);

            // palette of the image
            pal = new FlowLayoutPanel { AutoSize = true, Width = 300, Margin = new Padding(0, 14, 0, 0), BackColor = Color.Transparent, MaximumSize = new Size(420, 80) };
            strip.Controls.Add(pal);
            Refresh();
            return strip;
        }

        static SpriteButton Swatch(string name, Func<int> get, Action<int> set, int pick)
        {
            var b = new SpriteButton { Text = name, Shape = ButtonShape.Square, Size = new Size(64, 36), Margin = new Padding(0, 0, 0, 0), TextScale = 2 };
            b.Click += (_, _) =>
            {
                using var dlg = new ColorDialog { Color = Color.FromArgb(get() | unchecked((int)0xFF000000)), FullOpen = true };
                if (dlg.ShowDialog(b.FindForm()) == DialogResult.OK) { set(dlg.Color.ToArgb()); PickMode = 0; Refresh(); }
            };
            b.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) { PickMode = PickMode == pick ? 0 : pick; Status = PickMode != 0 ? $"click the picture to pick colour {name}" : ""; Refresh(); } };
            return b;
        }
        static void Paint(SpriteButton b, int argb, bool picking)
        {
            var c = Color.FromArgb(argb | unchecked((int)0xFF000000));
            b.AccentColor = c; b.Accented = true;
            b.ForeColor = (c.R * 77 + c.G * 151 + c.B * 28) >> 8 > 140 ? Color.Black : Color.White;
            b.Text = picking ? "pick" : $"#{argb & 0xFFFFFF:X6}";
        }

        static void Edit(Func<VectorImage, int> op, string what)
        {
            if (Image == null) { Status = "no picture"; Refresh(); return; }
            if (undo.Count > 20) { var arr = undo.ToArray(); undo.Clear(); for (int i = Math.Min(arr.Length, 20) - 1; i >= 0; i--) undo.Push(arr[i]); }
            undo.Push(Image.Clone());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = op(Image);
            Edits++;
            Status = $"{what}: {n} paint(s) changed in {sw.Elapsed.TotalMilliseconds:0.00} ms (Version {Image.Version})";
            Refresh();
        }

        static void MergeFile(bool shuffle)
        {
            if (Image == null) { Status = "no picture"; Refresh(); return; }
            if (Overlay == null || !shuffle || (Control.ModifierKeys & Keys.Shift) != 0)
            {
                using var dlg = new OpenFileDialog { Filter = "Vector files|*.svg;*.svgz;*.eps;*.ps;*.ai;*.pdf|All files|*.*", Title = "Picture to merge on top" };
                if (dlg.ShowDialog(objBox?.FindForm()) != DialogResult.OK) return;
                try { Overlay = VectorImage.Load(dlg.FileName); Overlay.NameUnnamed(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) + "_"); }
                catch (Exception ex) { Status = "load failed: " + ex.Message; Refresh(); return; }
            }
            var ov = Overlay;
            Edit(img => { int before = img.Shapes.Count; img.Merge(ov, fit: img.ViewBox, shuffle: shuffle); return img.Shapes.Count - before; }, shuffle ? "Merge(shuffle)" : "Merge");
        }
        static void ObjInfo()
        {
            if (objInfo == null || objBox == null) return;
            if (Image == null) { objInfo.Text = ""; return; }
            string n = objBox.Text.Trim();
            string win = Image.VisibleArea is RectangleF va ? $"  window {va.Width:0}x{va.Height:0} @ {va.X:0},{va.Y:0}" : "";
            if (n.Length == 0)
            {
                var kinds = new Dictionary<string, int>(); int unnamed = 0;
                foreach (var o in Image.Objects()) { kinds[o.Kind] = (kinds.TryGetValue(o.Kind, out int k) ? k : 0) + 1; if (o.Name.StartsWith('[')) unnamed++; }
                objInfo.Text = $"{Image.Names().Count} named, {unnamed} unnamed of {Image.Shapes.Count}: {string.Join(" ", kinds.Select(kv => kv.Value + " " + kv.Key))}{win}"; return;
            }
            int total = 0, hidden = 0; foreach (var s in Image.Named(n)) { total++; if (s.Hidden) hidden++; }
            var bb = Image.BoundsOf(n);
            objInfo.Text = total == 0 ? "no shape matches" + win : $"{total} shape(s) match, {hidden} hidden, box {bb.Width:0}x{bb.Height:0} @ {bb.X:0},{bb.Y:0}{win}";
        }

        /// <summary>The canvas test calls this with the picture point under a click while <see cref="PickMode"/> is set.</summary>
        public static void PickAt(PointF imagePoint)
        {
            if (Image == null || PickMode == 0) return;
            int? c = Image.ColorAt(imagePoint);
            if (c == null) { Status = "nothing under the cursor"; return; }
            if (PickMode == 1) ColorA = c.Value; else ColorB = c.Value;
            Status = $"colour {(PickMode == 1 ? "A" : "B")} = #{c.Value:X8}"; PickMode = 0; Refresh();
        }
        /// <summary>The test tells the strip which image is shown (a new file / page = new palette, cleared undo).</summary>
        public static void SetImage(VectorImage img)
        {
            if (ReferenceEquals(Image, img)) return;
            Image = img; undo.Clear(); lastPalVersion = -1;
            if (pal != null && pal.IsHandleCreated) pal.BeginInvoke(new Action(Refresh)); else Refresh();
        }

        static int lastPalVersion = -1;
        static void Refresh()
        {
            if (swA != null) Paint(swA, ColorA, PickMode == 1);
            if (swB != null) Paint(swB, ColorB, PickMode == 2);
            if (pal == null || Image == null) return;
            if (Image.Version == lastPalVersion && pal.Controls.Count > 0) return;
            lastPalVersion = Image.Version;
            if (objBox != null)
            {
                string keep = objBox.Text; objBox.BeginUpdate(); objBox.Items.Clear();
                foreach (var n in Image.Names()) { objBox.Items.Add(n); if (objBox.Items.Count >= 400) break; }
                foreach (var o in Image.Objects()) { if (o.Name.StartsWith('[')) continue; if (Image.Names().Contains(o.Name)) continue; if (!o.Name.StartsWith("auto", StringComparison.Ordinal)) continue; objBox.Items.Add(o.Name); if (objBox.Items.Count >= 600) break; }   // generated auto names (after "Name unnamed")
                objBox.EndUpdate(); objBox.Text = keep; ObjInfo();
            }
            pal.SuspendLayout();
            foreach (Control c in pal.Controls) c.Dispose();
            pal.Controls.Clear();
            var entries = Image.Palette(ignoreAlpha: true, target: Target);
            int shown = 0;
            foreach (var e in entries)
            {
                if (shown++ >= 36) break;
                var col = Color.FromArgb(e.Argb | unchecked((int)0xFF000000));
                var sw = new SpriteButton { Shape = ButtonShape.Square, Accented = true, AccentColor = col, Size = new Size(22, 22), Margin = new Padding(0, 0, 1, 1), Text = "" };
                var argb = e.Argb;
                new ToolTip().SetToolTip(sw, $"#{argb & 0xFFFFFF:X6}  used by {e.Uses} paint(s)  -  left = A, right = B");
                sw.Click += (_, _) => { ColorA = argb; PickMode = 0; Refresh(); };
                sw.MouseUp += (_, me) => { if (me.Button == MouseButtons.Right) { ColorB = argb; PickMode = 0; Refresh(); } };
                pal.Controls.Add(sw);
            }
            pal.ResumeLayout();
        }
    }
}

namespace Sr2d64CSport
{
    /// <summary>
    /// Strip of the "SpriteFont" test: pick any installed family (bold / italic faces), type the paragraph yourself,
    /// set the fake bold in fine steps and switch the TextCache on / off to compare DrawString every frame with a
    /// cached bitmap blit.
    /// </summary>
    internal static class FontDemo
    {
        public const int StripHeight = 118;
        public static string Text = "The quick brown fox jumps over the lazy dog. Съешь же ещё этих мягких французских булок, да выпей чаю. 0123456789 – fi fl ffi AV To Ty.";
        /// <summary>Fake bold in em (0 = the face as it is; 0.02 = a little heavier, 0.05 = bold-ish).</summary>
        public static double Bold;
        public static bool UseCache = true, Bold2, Italic;
        public static string Family = "";
        static SpriteFont? font; static string loadedKey = "\0";
        /// <summary>The picked face (loaded once per family / style change); falls back to the demo's default face.</summary>
        public static SpriteFont? Get()
        {
            string key = Family + "|" + Bold2 + "|" + Italic;
            if (key != loadedKey)
            {
                loadedKey = key; var old = font; font = null;
                if (Family.Length > 0) { try { font = SpriteFont.Installed(Family, Bold2, Italic); } catch { font = null; } }
                if (old != null && old != DemoFont.Get()) old.Dispose();
            }
            return font ?? DemoFont.Get();
        }

        public static Control Build()
        {
            var strip = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Color.FromArgb(0x20, 0x24, 0x28), Padding = new Padding(4, 2, 4, 0) };
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330)); strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300)); strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); strip.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // family picker (every installed family; the current one preselected) + the bold / italic face switches
            var famCol = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            famCol.Controls.Add(new SpriteLabel { Text = "Font family (installed)", Style = LabelStyle.Muted, Margin = new Padding(0, 0, 0, 2) });
            var fams = new List<string>(); try { fams.AddRange(SpriteFont.SystemFamilies()); } catch { }
            var def = DemoFont.Get(); int sel = def == null ? 0 : Math.Max(0, fams.FindIndex(f => string.Equals(f, def.FamilyName, StringComparison.OrdinalIgnoreCase)));
            var pick = new SpriteCombo { Width = 320, Height = 24, Margin = new Padding(0, 0, 0, 2) };
            if (fams.Count > 0) { pick.SetItems(fams, sel); Family = fams[sel]; }
            pick.SelectedIndexChanged += (_, _) => { if (pick.SelectedIndex >= 0) Family = fams[pick.SelectedIndex]; };
            famCol.Controls.Add(pick);
            var faces = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            var tb = new SpriteToggle { Text = "Bold face", Style = ToggleStyle.CheckBox, Size = new Size(96, 22), Margin = new Padding(0, 0, 6, 0) }; tb.CheckedChanged += (_, _) => Bold2 = tb.Checked;
            var ti = new SpriteToggle { Text = "Italic face", Style = ToggleStyle.CheckBox, Size = new Size(96, 22), Margin = new Padding(0, 0, 6, 0) }; ti.CheckedChanged += (_, _) => Italic = ti.Checked;
            var tc = new SpriteToggle { Text = "TextCache", Style = ToggleStyle.CheckBox, Checked = UseCache, Size = new Size(96, 22), Margin = new Padding(0), AccentColor = Color.FromArgb(0x60, 0xE0, 0x80) }; tc.CheckedChanged += (_, _) => UseCache = tc.Checked;
            faces.Controls.Add(tb); faces.Controls.Add(ti); faces.Controls.Add(tc);
            famCol.Controls.Add(faces);
            strip.Controls.Add(famCol, 0, 0); strip.SetRowSpan(famCol, 2);

            // fake bold in em: a slider with fine steps (the bench's Blend slider was far too coarse for this)
            var bold = new SpriteSlider { Text = "Fake bold (em)", Minimum = 0, Maximum = 0.12, Step = 0.005, Decimals = 3, Value = Bold, ResetValue = 0, Ticks = 6, Size = new Size(290, 46), Margin = new Padding(0) };
            bold.ValueChanged += (_, _) => Bold = bold.Value;
            strip.Controls.Add(bold, 1, 0);
            var hint = new Label { Text = "0 = the face as designed. Bold face / Italic face pick real\nstyles when the family has them; fake bold / italic are\nsynthetic. TextCache: the paragraph is rasterised once and\nblitted (SpriteFont.Render + TextCache) instead of laid out\nper frame - compare the fps.", ForeColor = Color.Gainsboro, AutoSize = true, Margin = new Padding(0, 2, 0, 0), Font = new Font(SystemFonts.DefaultFont.FontFamily, 7.5f) };
            strip.Controls.Add(hint, 1, 1);

            // the paragraph: type your own (Enter / focus loss applies)
            var txtCol = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(6, 0, 0, 0), BackColor = Color.Transparent };
            txtCol.Controls.Add(new SpriteLabel { Text = "Your text (Enter applies; \\n = line break)", Style = LabelStyle.Muted, Margin = new Padding(0, 0, 0, 2) });
            var text = new SpriteTextBox { Text = Text.Replace("\n", "\\n", StringComparison.Ordinal), Placeholder = "type something…", Height = 24, Width = 600, Margin = new Padding(0, 0, 0, 2), MaxLength = 400 };
            text.Committed += (_, _) => Text = text.Text.Replace("\\n", "\n", StringComparison.Ordinal);
            txtCol.Controls.Add(text);
            var presets = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
            void Preset(string title, string t) { var b = new SpriteButton { Text = title, Size = new Size(88, 24), Margin = new Padding(0, 0, 4, 0) }; b.Click += (_, _) => { Text = t; text.Text = t.Replace("\n", "\\n", StringComparison.Ordinal); }; presets.Controls.Add(b); }
            Preset("Pangram", "The quick brown fox jumps over the lazy dog. Съешь же ещё этих мягких французских булок, да выпей чаю. 0123456789 – fi fl ffi AV To Ty.");
            Preset("Kerning", "AVAILABLE WATER Toy Type LTA. To Ta Te Yo Vo Wa fi fl ffi ffl – — “quotes” ‘single’ 1/2 3/4 ¼ ½ µ ß ø æ œ");
            Preset("Digits", "0123456789 00 11 88 99  3.14159 2.71828  12:34:56  +7 (351) 000-00-00  €12.50 $9.99 £3.20 ¥100");
            Preset("Lines", "Line one\nLine two is longer than the first\nThree");
            txtCol.Controls.Add(presets);
            strip.Controls.Add(txtCol, 2, 0); strip.SetRowSpan(txtCol, 2);
            return strip;
        }
    }
}
