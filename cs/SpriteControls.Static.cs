// SpriteControls.Static.cs - the "furniture" of a form in the SR2D control look: SpriteLabel, SpriteSeparator, SpriteLed,
// and the containers SpritePanel, SpriteGroupBox, SpriteTabControl / SpriteTabPage.
//
// Containers are ordinary WinForms containers (drop any control into them, the designer treats them as parents); they
// paint their frame with SR2D and hand their face colour down to SpriteControls inside that have no explicit BackColor
// (see SpriteControlBase.AdoptBackColor), so a knob on a sunken panel is drawn on the panel's colour without any setup.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // ImplicitUsings also brings System.Threading.Timer

namespace Sr2d64CSport
{
    /// <summary>Look of a <see cref="SpriteLabel"/>.</summary>
    internal enum LabelStyle
    {
        /// <summary>Plain text in ForeColor.</summary>
        Plain,
        /// <summary>Bold text with a rule running to the right edge - a section title.</summary>
        Heading,
        /// <summary>Dimmed text (hints, units, secondary information).</summary>
        Muted,
        /// <summary>A sunken read-out field (a value display that is not editable).</summary>
        Readout,
        /// <summary>An accent-coloured pill with white text (a status tag / counter).</summary>
        Badge,
    }

    /// <summary>
    /// Static text in the pixel font: any <see cref="ContentAlignment"/>, optional word wrap, AutoSize like a WinForms Label
    /// (it grows with the text; turn it off for a fixed box), five styles (<see cref="LabelStyle"/>). It is not selectable.
    /// </summary>
    [ToolboxBitmap(typeof(SpriteLabel), "SpriteLabel.bmp")]
    internal sealed class SpriteLabel : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.StaticText;
        LabelStyle _style; ContentAlignment _align = ContentAlignment.MiddleLeft;
        bool _wrap, _autoSize = true, _ellipsis = true, _bold; int _lineGap;

        public SpriteLabel()
        {
            TabStop = false; Cursor = Cursors.Default;
            SetStyle(ControlStyles.Selectable, false);
            Size = new Size(80, 20);
        }

        [Category("Appearance"), DefaultValue(LabelStyle.Plain), Description("Plain / Heading (bold + rule) / Muted (dim) / Readout (sunken field) / Badge (accent pill).")]
        public LabelStyle Style { get => _style; set { _style = value; Fit(); Redraw(); } }

        [Category("Appearance"), DefaultValue(ContentAlignment.MiddleLeft), Description("Where the text sits in the box (ignored by AutoSize, which fits the box to the text).")]
        public ContentAlignment TextAlign { get => _align; set { _align = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(false), Description("Wrap the text at the box width (needs AutoSize = false).")]
        public bool WordWrap { get => _wrap; set { _wrap = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(true), Description("Shorten text that does not fit with an ellipsis instead of clipping it.")]
        public bool Ellipsis { get => _ellipsis; set { _ellipsis = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(false), Description("Bold text (Heading is always bold).")]
        public bool Bold { get => _bold; set { _bold = value; Fit(); Redraw(); } }

        [Category("Appearance"), DefaultValue(0), Description("Extra pixels between lines of a multi-line text.")]
        public int LineGap { get => _lineGap; set { _lineGap = Math.Max(0, value); Fit(); Redraw(); } }

        [Category("Layout"), DefaultValue(true), Browsable(true), EditorBrowsable(EditorBrowsableState.Always), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible), Description("Size the control to its text (like a WinForms Label).")]
        public override bool AutoSize { get => _autoSize; set { _autoSize = value; base.AutoSize = value; Fit(); } }

        int Sc => TextScale > 0 ? TextScale : 1;      // labels never guess a size from their box: 1 unless TextScale says otherwise
        int Weight => _bold || _style == LabelStyle.Heading ? Math.Max(1, Sc / 2) : 0;
        int InsetX(int sc) => _style is LabelStyle.Readout or LabelStyle.Badge ? 4 * sc + 2 : 0;
        int InsetY(int sc) => _style is LabelStyle.Readout or LabelStyle.Badge ? 2 * sc + 1 : 0;

        public override Size GetPreferredSize(Size proposed) => Measure(Sc);
        Size Measure(int sc)
        {
            var t = Text ?? "";
            var m = t.Length == 0 ? new Size(0, TextLineHeight(sc)) : T.Measure(t, sc, Weight, 0);
            if (_lineGap > 0) m.Height += _lineGap * Math.Max(0, t.Split('\n').Length - 1);
            int w = m.Width + 2 * InsetX(sc) + Padding.Horizontal + (_style == LabelStyle.Heading ? 24 : 0);
            return new Size(Math.Max(w, 4), m.Height + 2 * InsetY(sc) + Padding.Vertical + 2);
        }
        void Fit() { if (_autoSize) Size = Measure(Sc); }
        protected override void OnTextFontChanged() { Fit(); Redraw(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Redraw(); }
        protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); Fit(); Redraw(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Redraw(); }

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height, sc = Sc, weight = Weight;
            var box = new Rectangle(Padding.Left, Padding.Top, w - Padding.Horizontal, h - Padding.Vertical);
            int col = _style switch { LabelStyle.Muted => Mix(ForeColor, BackColor, 0.5f), LabelStyle.Badge => Enabled ? unchecked((int)0xFFFFFFFF) : Fore, LabelStyle.Heading => Enabled ? Thumb : Fore, _ => Fore };
            if (_style == LabelStyle.Readout)
            {
                FillRound(s, box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1, 3f, Mix(BackColor, Black, 0.25f));
                DrawRound(s, box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1, 3f, Mix(BackColor, ThumbColor, 0.3f), 1f);
            }
            else if (_style == LabelStyle.Badge)
                FillRound(s, box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1, box.Height / 2f, Accent);
            box.Inflate(-InsetX(sc), -InsetY(sc));
            string t = Text ?? "";
            if (t.Length == 0) { if (_style == LabelStyle.Heading) s.FillRect(box.X, box.Y + box.Height / 2, box.Width, 1, Track); return; }
            if (_wrap && !_autoSize) t = WrapText(t, box.Width, sc);
            var m = T.Measure(t, sc, weight, 0);
            int lines = 1; foreach (char ch in t) if (ch == '\n') lines++;
            int lineH = TextLineHeight(sc) + _lineGap, totalH = m.Height + _lineGap * (lines - 1);
            int av = (int)_align, ay = av >= 256 ? 2 : av >= 16 ? 1 : 0, row = av >> (ay * 4);   // ContentAlignment: 1 / 2 / 4 = left / centre / right, shifted by 4 bits per row
            int ax = row == 1 ? 0 : row == 2 ? 1 : 2;
            if (_ellipsis && !_wrap && lines == 1 && m.Width > box.Width) { t = FitText(t, box.Width, sc); m = T.Measure(t, sc, weight, 0); }
            int y = box.Y + (ay == 0 ? 0 : ay == 1 ? (box.Height - totalH) / 2 : box.Height - totalH);
            // each line placed on its own so centre / right alignment works per line
            int li = 0;
            foreach (var line in t.Split('\n'))
            {
                int lw = T.Measure(line, sc, weight, 0).Width;
                int x = box.X + (ax == 0 ? 0 : ax == 1 ? (box.Width - lw) / 2 : box.Width - lw);
                if (line.Length > 0) T.Draw(s, x, y + li * lineH, line, col, 0, sc, weight, 0);
                if (_style == LabelStyle.Heading && li == 0)
                {   // the rule: from the text end to the right edge, at the x-height
                    int rx = x + lw + 6 * sc, ry = y + TextLineHeight(sc) / 2 - 1;
                    if (ax == 2) { rx = box.X; int rw = x - 6 * sc - rx; if (rw > 2) s.FillRect(rx, ry, rw, 1, Track); }
                    else if (box.Right - rx > 2) s.FillRect(rx, ry, box.Right - rx, 1, Track);
                }
                li++;
            }
        }
    }

    /// <summary>A thin rule (horizontal or vertical) with an optional centred caption: "── or ──".</summary>
    [ToolboxBitmap(typeof(SpriteSeparator), "SpriteSeparator.bmp")]
    internal sealed class SpriteSeparator : SpriteControlBase
    {
        Orientation _orient = Orientation.Horizontal;
        public SpriteSeparator()
        {
            TabStop = false; Cursor = Cursors.Default;
            SetStyle(ControlStyles.Selectable, false);
            Size = new Size(160, 12);
        }
        [Category("Appearance"), DefaultValue(Orientation.Horizontal)]
        public Orientation Orientation { get => _orient; set { _orient = value; Redraw(); } }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Redraw(); }
        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height, line = Track, dim = Mix(ForeColor, BackColor, 0.5f);
            if (_orient == Orientation.Vertical) { s.FillRect(w / 2, 2, 1, h - 4, line); return; }
            string t = Text ?? "";
            if (t.Length == 0) { s.FillRect(2, h / 2, w - 4, 1, line); return; }
            int sc = AutoTextScale(h * 4);
            var m = T.Measure(t, sc);
            int tx = (w - m.Width) / 2, gap = 6 * sc, y = h / 2;
            if (tx - gap > 4) s.FillRect(2, y, tx - gap - 2, 1, line);
            if (w - 2 - (tx + m.Width + gap) > 2) s.FillRect(tx + m.Width + gap, y, w - 2 - (tx + m.Width + gap), 1, line);
            T.Draw(s, tx, (h - m.Height) / 2, t, dim, 0, sc);
        }
    }

    /// <summary>Shape of a <see cref="SpriteLed"/>.</summary>
    internal enum LedShape { Round, Square, Bar }

    /// <summary>
    /// An indicator light with a caption: <see cref="On"/> lights it in <see cref="LedColor"/> (a lit LED glows: bright core,
    /// soft halo), off is the same colour dimmed; <see cref="Blink"/> flashes it on a timer. Clicking does nothing unless
    /// <see cref="Clickable"/> - then it toggles and raises <see cref="OnChanged"/>, which makes it a very small toggle.
    /// </summary>
    [ToolboxBitmap(typeof(SpriteLed), "SpriteLed.bmp")]
    internal sealed class SpriteLed : SpriteControlBase
    {
        bool _on, _clickable, _phase = true; int _blink; Color _led = DefaultLed; LedShape _shape; Timer? _timer;
        public SpriteLed()
        {
            TabStop = false; Cursor = Cursors.Default;
            SetStyle(ControlStyles.Selectable, false);
            Size = new Size(90, 20);
        }
        [Category("Action"), Description("Raised after On changed (user click or code).")]
        public event EventHandler? OnChanged;

        [Category("Appearance"), DefaultValue(false), Description("Lit or dark.")]
        public bool On { get => _on; set { if (_on == value) return; _on = value; _phase = true; SyncTimer(); OnChanged?.Invoke(this, EventArgs.Empty); Redraw(); } }

        [Category("Appearance"), Description("Colour when lit (green by default; off = the same colour dimmed).")]
        public Color LedColor { get => _led; set { _led = value; Redraw(); } }
        static readonly Color DefaultLed = Color.FromArgb(0x60, 0xE0, 0x80);
        bool ShouldSerializeLedColor() => _led != DefaultLed;
        void ResetLedColor() => LedColor = DefaultLed;

        [Category("Appearance"), DefaultValue(LedShape.Round)]
        public LedShape Shape { get => _shape; set { _shape = value; Redraw(); } }

        [Category("Behavior"), DefaultValue(0), Description("Blink period in ms while On (0 = steady).")]
        public int Blink { get => _blink; set { _blink = Math.Max(0, value); SyncTimer(); Redraw(); } }

        [Category("Behavior"), DefaultValue(false), Description("A click toggles On.")]
        public bool Clickable { get => _clickable; set { _clickable = value; TabStop = value; SetStyle(ControlStyles.Selectable, value); } }

        void SyncTimer()
        {
            bool want = _on && _blink > 0 && IsHandleCreated;
            if (want) { _timer ??= new Timer(); _timer.Interval = Math.Max(30, _blink / 2); _timer.Tick -= Tick; _timer.Tick += Tick; if (!_timer.Enabled) _timer.Start(); }
            else if (_timer != null && _timer.Enabled) { _timer.Stop(); _phase = true; Redraw(); }
        }
        void Tick(object? sender, EventArgs e) { _phase = !_phase; Redraw(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SyncTimer(); }
        protected override void OnHandleDestroyed(EventArgs e) { _timer?.Stop(); base.OnHandleDestroyed(e); }
        protected override void Dispose(bool disposing) { if (disposing) _timer?.Dispose(); base.Dispose(disposing); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (_clickable && e.Button == MouseButtons.Left && Enabled) { Focus(); On = !On; } }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (_clickable && e.KeyCode == Keys.Space) { On = !On; e.Handled = true; } }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Redraw(); }

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            bool lit = _on && _phase && Enabled;
            float t = lit ? 1f : 0f;                     // an LED is instant (no animation): on or off
            int dark = Mix(_led, BackColor, 0.72f), bright = Lerp(dark, _led.ToArgb(), t), core = Mix(bright, White, 0.45f * t);
            float d = Math.Min(h - 4, 16), r = d / 2, cx = 2 + r + 1, cy = h / 2f;
            if (_shape == LedShape.Bar) { d = Math.Min(h - 6, 10); r = d / 2; cx = 4 + 6; }
            if (t > 0.05f)
            {   // halo: two soft alpha rings
                int halo = (bright & 0xFFFFFF);
                if (_shape == LedShape.Round) { s.FillCircle(cx, cy, r + 3, halo | ((int)(45 * t) << 24), SR2D.LineOp.AlphaBlend, true); s.FillCircle(cx, cy, r + 1.5f, halo | ((int)(80 * t) << 24), SR2D.LineOp.AlphaBlend, true); }
                else if (_shape == LedShape.Square) { FillRound(s, cx - r - 3, cy - r - 3, d + 6, d + 6, 3, Lerp(BackColor.ToArgb(), bright, 0.18f * t)); }
                else FillRound(s, cx - 8 - 3, cy - r - 3, 16 + 6, d + 6, 3, Lerp(BackColor.ToArgb(), bright, 0.18f * t));
            }
            switch (_shape)
            {
                case LedShape.Round:
                    s.FillCircle(cx, cy, r, bright, SR2D.LineOp.Set, true);
                    s.FillCircle(cx - r * 0.25f, cy - r * 0.25f, r * 0.4f, core, SR2D.LineOp.Set, true);
                    s.DrawCircle(cx, cy, r, Mix(BackColor, ThumbColor, 0.45f), 1f, true); break;
                case LedShape.Square:
                    FillRound(s, cx - r, cy - r, d, d, 2, bright); FillRound(s, cx - r + 2, cy - r + 2, d * 0.4f, d * 0.4f, 1, core);
                    DrawRound(s, cx - r, cy - r, d, d, 2, Mix(BackColor, ThumbColor, 0.45f), 1f); break;
                default:
                    FillRound(s, cx - 8, cy - r, 16, d, 2, bright); FillRound(s, cx - 6, cy - r + 2, 5, d * 0.35f, 1, core);
                    DrawRound(s, cx - 8, cy - r, 16, d, 2, Mix(BackColor, ThumbColor, 0.45f), 1f); break;
            }
            if (!string.IsNullOrEmpty(Text))
            {
                int x0 = (int)(_shape == LedShape.Bar ? cx + 8 : cx + r) + 6, sc = AutoTextScale(h * 4);
                int fit = FitScale(Text, w - x0 - 2, sc);
                T.Draw(s, x0, (int)cy, FitText(Text, w - x0 - 2, fit), Fore, 0, fit, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            }
        }
    }

    /// <summary>Frame of a <see cref="SpritePanel"/>.</summary>
    internal enum PanelStyle
    {
        /// <summary>No frame: just the background (a coloured area / a layout host).</summary>
        Flat,
        /// <summary>Darker inset face with a thin rim - a well for controls.</summary>
        Sunken,
        /// <summary>Lighter face with the button rim - a card.</summary>
        Raised,
        /// <summary>The parent's colour with a rim only.</summary>
        Outline,
    }

    /// <summary>
    /// A container with an SR2D frame. Drop controls into it (in code: <c>panel.Controls.Add(x)</c>; the designer handles it
    /// as a parent). <see cref="Control.Padding"/> keeps children off the rim (default 8). SpriteControls inside inherit the
    /// face colour unless their BackColor was set explicitly.
    /// </summary>
    [Designer("System.Windows.Forms.Design.ParentControlDesigner, System.Design", typeof(IDesigner))]
    [ToolboxBitmap(typeof(SpritePanel), "SpritePanel.bmp")]
    internal class SpritePanel : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Client;
        PanelStyle _style = PanelStyle.Sunken; int _radius = 6;
        public SpritePanel()
        {
            TabStop = false; Cursor = Cursors.Default;
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.ContainerControl, true);
            Padding = new Padding(8);
            Size = new Size(200, 120);
        }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.Style |= 0x02000000; return cp; }   // WS_CLIPCHILDREN: the frame never paints over the children
        }

        [Category("Appearance"), DefaultValue(PanelStyle.Sunken)]
        public PanelStyle Style { get => _style; set { _style = value; PushBackColor(); Redraw(); } }

        [Category("Appearance"), DefaultValue(6), Description("Corner radius of the frame in pixels.")]
        public int CornerRadius { get => _radius; set { _radius = Math.Max(0, value); Redraw(); } }

        /// <summary>The face colour (what children see as their background).</summary>
        [Browsable(false)] public Color FaceColor => Color.FromArgb(Face);
        protected int Face => _style switch
        {
            PanelStyle.Sunken => Mix(BackColor, Black, 0.22f),
            PanelStyle.Raised => Mix(BackColor, ThumbColor, 0.10f),
            _ => BackColor.ToArgb(),
        };
        protected override Color ChildBackColor => FaceColor;
        protected int Rim => Mix(BackColor, ThumbColor, _style == PanelStyle.Sunken ? 0.28f : 0.45f);

        /// <summary>Extra room the frame takes at the top (the group box caption).</summary>
        protected virtual int TopInset => 0;
        public override Rectangle DisplayRectangle
        {
            get { var r = ClientRectangle; r.Inflate(-2, -2); r.Y += TopInset; r.Height -= TopInset; r.X += Padding.Left; r.Width -= Padding.Horizontal; r.Y += Padding.Top; r.Height -= Padding.Vertical; return r; }
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Redraw(); }
        /// <summary>The caption height (group box) follows the font: docked children must be laid out again.</summary>
        protected override void OnTextFontChanged() { if (IsHandleCreated) PerformLayout(); base.OnTextFontChanged(); }

        /// <summary>Paints the frame; derived classes add to it.</summary>
        protected override void PaintControl(Sprite s) => PaintFrame(s, 0);
        protected void PaintFrame(Sprite s, int top)
        {
            int w = ClientSize.Width, h = ClientSize.Height - top;
            if (_style == PanelStyle.Flat) { if (top > 0) s.FillRect(0, top, w, h, Face); return; }
            float r = Math.Min(_radius, Math.Min(w, h) / 2f);
            if (_style != PanelStyle.Outline) FillRound(s, 0.5f, top + 0.5f, w - 1, h - 1, r, Face);
            if (_style == PanelStyle.Sunken)
            {   // inner shadow along the top and the left edge
                using var v = s.CreateView(new Rectangle(0, top, w, Math.Min(h, 3)));
                DrawRound(v, 1.5f, top + 1.5f, w - 3, h - 3, Math.Max(0, r - 1), Mix(BackColor, Black, 0.4f), 1.5f);
            }
            DrawRound(s, 0.5f, top + 0.5f, w - 1, h - 1, r, Rim, 1f);
        }
    }

    /// <summary>
    /// A framed group with a caption in the rim (a WinForms GroupBox in the SR2D look). Optional check box in the caption
    /// (<see cref="ShowCheck"/>): unchecking disables every control inside - the usual "enable this section" idiom.
    /// </summary>
    [Designer("System.Windows.Forms.Design.ParentControlDesigner, System.Design", typeof(IDesigner))]
    [ToolboxBitmap(typeof(SpriteGroupBox), "SpriteGroupBox.bmp")]
    internal sealed class SpriteGroupBox : SpritePanel
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Grouping;
        bool _showCheck, _checked = true, _hotCheck; HorizontalAlignment _capAlign = HorizontalAlignment.Left;
        public SpriteGroupBox() { Style = PanelStyle.Outline; Size = new Size(200, 120); }

        [Category("Action"), Description("Raised after Checked changed.")]
        public event EventHandler? CheckedChanged;

        [Category("Appearance"), DefaultValue(HorizontalAlignment.Left), Description("Where the caption sits on the top edge.")]
        public HorizontalAlignment CaptionAlign { get => _capAlign; set { _capAlign = value; Redraw(); } }

        [Category("Behavior"), DefaultValue(false), Description("A check box in front of the caption; unchecked disables the controls inside.")]
        public bool ShowCheck { get => _showCheck; set { _showCheck = value; TabStop = value; SetStyle(ControlStyles.Selectable, value); ApplyChecked(); Redraw(); } }

        [Category("Behavior"), DefaultValue(true), Description("State of the caption check box (with ShowCheck); false disables the children.")]
        public bool Checked
        {
            get => _checked;
            set { if (_checked == value) return; _checked = value; ApplyChecked(); CheckedChanged?.Invoke(this, EventArgs.Empty); AnimateTo(value ? 1f : 0f, 160); }
        }
        void ApplyChecked() { bool en = !_showCheck || _checked; foreach (Control c in Controls) c.Enabled = en; }
        protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); if (_showCheck && !_checked && e.Control != null) e.Control.Enabled = false; }

        int Sc => AutoTextScale(Math.Min(ClientSize.Width, ClientSize.Height) * 2 / 3);
        int CaptionH => TextLineHeight(Sc) + 2;
        protected override int TopInset => CaptionH / 2 + 2;
        Rectangle CheckRect { get { int sc = Sc, half = 4 * sc + 1; int x = CaptionX(sc); return new Rectangle(x, (CaptionH - 2 * half) / 2 + 1, 2 * half, 2 * half); } }
        int CaptionX(int sc)
        {
            int w = ClientSize.Width, tw = TextWidth(sc);
            return _capAlign switch { HorizontalAlignment.Center => Math.Max(8, (w - tw) / 2), HorizontalAlignment.Right => Math.Max(8, w - 12 - tw), _ => Math.Max(8, CornerRadius + 6) };
        }
        int TextWidth(int sc) => (string.IsNullOrEmpty(Text) ? 0 : T.Measure(Text, sc).Width) + (_showCheck ? 8 * sc + 2 + 5 * sc : 0);

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_showCheck) { bool h = CheckRect.Contains(e.Location) || new Rectangle(CaptionX(Sc), 0, TextWidth(Sc), CaptionH).Contains(e.Location); if (h != _hotCheck) { _hotCheck = h; Redraw(); } } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hotCheck) { _hotCheck = false; Redraw(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_showCheck && e.Button == MouseButtons.Left && Enabled && _hotCheck) { Focus(); Checked = !Checked; }
        }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (_showCheck && e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; } }

        protected override void PaintControl(Sprite s)
        {
            int sc = Sc, capH = CaptionH, top = capH / 2;
            PaintFrame(s, top);
            int w = ClientSize.Width;
            string t = Text ?? "";
            int tw = TextWidth(sc), x = CaptionX(sc);
            // the gap in the rim behind the caption
            if (tw > 0) s.FillRect(x - 3 * sc, top - 1, tw + 6 * sc, 3, BackColor.ToArgb());
            int tx = x;
            if (_showCheck)
            {
                var cr = CheckRect; float half = cr.Width / 2f;
                int on = Enabled ? (_hotCheck ? Mix(AccentRaw, White, 0.15f) : Accent) : Accent, off = Mix(BackColor, ThumbColor, _hotCheck ? 0.6f : 0.45f);
                PaintCheckBox(s, cr.X + half, cr.Y + half, half, IsAnimating ? AnimT : (_checked ? 1f : 0f), on, off, Mix(BackColor, Color.Black, 0.22f));
                tx += cr.Width + 5 * sc;
            }
            if (t.Length > 0)
            {
                int avail = w - tx - 8, fit = FitScale(t, avail, sc);
                T.Draw(s, tx, capH / 2, FitText(t, avail, fit), _showCheck && !_checked ? Mix(ForeColor, BackColor, 0.5f) : Fore, 0, fit, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            }
        }
    }

    /// <summary>One page of a <see cref="SpriteTabControl"/>: a flat panel with the tab's caption in <see cref="Control.Text"/>.</summary>
    [Designer("System.Windows.Forms.Design.ParentControlDesigner, System.Design", typeof(IDesigner))]
    internal sealed class SpriteTabPage : SpritePanel
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.PageTab;
        public SpriteTabPage() { Style = PanelStyle.Flat; Padding = new Padding(8); Visible = false; }
        public SpriteTabPage(string text) : this() { Text = text; }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (Parent is SpriteTabControl tc) tc.Redraw(); }
        // page colour: a shade lighter than the tab control so the selected tab visibly connects to it
        internal int PageFace => Mix(BackColor, ThumbColor, 0.06f);
        protected override void PaintControl(Sprite s) { s.ClearBuffer(PageFace); }
        protected override Color ChildBackColor => Color.FromArgb(PageFace);
    }

    /// <summary>Where the tab strip of a <see cref="SpriteTabControl"/> is.</summary>
    internal enum TabSide { Top, Bottom }

    /// <summary>
    /// Tabbed pages. Pages are <see cref="SpriteTabPage"/> children: <see cref="AddPage(string)"/> (or <c>Controls.Add(page)</c>)
    /// adds one, the strip shows their Text, <see cref="SelectedIndex"/> switches. Left / Right keys change the page when the
    /// strip has the focus; the wheel over the strip does too. The rest of the client area is the page (Dock = Fill inside it).
    /// </summary>
    [ToolboxBitmap(typeof(SpriteTabControl), "SpriteTabControl.bmp")]
    internal sealed class SpriteTabControl : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.PageTabList;
        readonly System.Collections.Generic.List<SpriteTabPage> _pages = new();
        int _sel = -1, _hot = -1, _tabW; TabSide _side; bool _stretch;

        public SpriteTabControl()
        {
            Cursor = Cursors.Default;
            SetStyle(ControlStyles.ContainerControl, true);
            Size = new Size(260, 160);
        }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.Style |= 0x02000000; return cp; } }
        protected override bool OwnFocusRect => true;

        [Category("Action"), Description("Raised after SelectedIndex changed.")]
        public event EventHandler? SelectedIndexChanged;

        [Category("Behavior"), DefaultValue(-1), Description("Index of the shown page (-1 = none).")]
        public int SelectedIndex
        {
            get => _sel;
            set
            {
                value = _pages.Count == 0 ? -1 : Math.Clamp(value, -1, _pages.Count - 1);
                if (value == _sel) return;
                _sel = value;
                for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == _sel;
                LayoutPages(); Redraw();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public SpriteTabPage? SelectedPage { get => _sel >= 0 && _sel < _pages.Count ? _pages[_sel] : null; set => SelectedIndex = value == null ? -1 : _pages.IndexOf(value); }

        /// <summary>The pages in strip order.</summary>
        [Browsable(false)] public System.Collections.Generic.IReadOnlyList<SpriteTabPage> TabPages => _pages;

        [Category("Appearance"), DefaultValue(TabSide.Top)]
        public TabSide Side { get => _side; set { _side = value; LayoutPages(); Redraw(); } }

        [Category("Appearance"), DefaultValue(0), Description("Fixed tab width in pixels; 0 = each tab as wide as its caption.")]
        public int TabWidth { get => _tabW; set { _tabW = Math.Max(0, value); Redraw(); } }

        [Category("Appearance"), DefaultValue(false), Description("Stretch the tabs over the full width.")]
        public bool Stretch { get => _stretch; set { _stretch = value; Redraw(); } }

        /// <summary>Adds a page with that caption and returns it.</summary>
        public SpriteTabPage AddPage(string text) { var p = new SpriteTabPage(text); Controls.Add(p); return p; }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (e.Control is SpriteTabPage p && !_pages.Contains(p)) { _pages.Add(p); p.Visible = false; if (_sel < 0) SelectedIndex = 0; else { LayoutPages(); Redraw(); } }
        }
        protected override void OnControlRemoved(ControlEventArgs e)
        {
            base.OnControlRemoved(e);
            if (e.Control is SpriteTabPage p) { int i = _pages.IndexOf(p); if (i < 0) return; _pages.RemoveAt(i); if (_sel >= _pages.Count) { _sel = _pages.Count - 1; SelectedIndexChanged?.Invoke(this, EventArgs.Empty); } if (_sel >= 0) _pages[_sel].Visible = true; LayoutPages(); Redraw(); }
        }

        int Sc => AutoTextScale(ClientSize.Height / 2);
        int StripH => TextLineHeight(Sc) + 10;
        /// <summary>Client area of the pages.</summary>
        public override Rectangle DisplayRectangle { get { int sh = StripH; return _side == TabSide.Top ? new Rectangle(0, sh, ClientSize.Width, Math.Max(0, ClientSize.Height - sh)) : new Rectangle(0, 0, ClientSize.Width, Math.Max(0, ClientSize.Height - sh)); } }
        void LayoutPages() { var r = DisplayRectangle; foreach (var p in _pages) if (p.Bounds != r) p.Bounds = r; }
        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutPages(); Redraw(); }
        // the strip height follows the font: a taller font would otherwise leave the pages where they were (the page and
        // its children painted over the strip until the next tab switch re-laid them out)
        protected override void OnTextFontChanged() { LayoutPages(); base.OnTextFontChanged(); }
        protected override void OnTextScaleChanged() { LayoutPages(); base.OnTextScaleChanged(); }

        // tab rectangles along the strip
        Rectangle[] Tabs()
        {
            int sc = Sc, n = _pages.Count, w = ClientSize.Width, sh = StripH, y = _side == TabSide.Top ? 0 : ClientSize.Height - sh;
            var r = new Rectangle[n];
            int x = 4;
            if (_stretch && n > 0) { int each = (w - 8) / n; for (int i = 0; i < n; i++) { r[i] = new Rectangle(x, y, each, sh); x += each; } return r; }
            for (int i = 0; i < n; i++)
            {
                int tw = _tabW > 0 ? _tabW : T.Measure(_pages[i].Text ?? "", sc).Width + 14 * sc;
                r[i] = new Rectangle(x, y, tw, sh); x += tw + 2;
            }
            return r;
        }
        int TabAt(Point p) { var t = Tabs(); for (int i = 0; i < t.Length; i++) if (t[i].Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = TabAt(e.Location); if (h != _hot) { _hot = h; Redraw(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hot >= 0) { _hot = -1; Redraw(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = TabAt(e.Location); Focus();
            if (i >= 0) SelectedIndex = i;
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_pages.Count == 0) return;
            SelectedIndex = Math.Clamp(_sel - Math.Sign(e.Delta), 0, _pages.Count - 1);
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }
        protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_pages.Count == 0) return;
            switch (e.KeyCode)
            {
                case Keys.Left: SelectedIndex = Math.Max(0, _sel - 1); break;
                case Keys.Right: SelectedIndex = Math.Min(_pages.Count - 1, _sel + 1); break;
                case Keys.Home: SelectedIndex = 0; break;
                case Keys.End: SelectedIndex = _pages.Count - 1; break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height, sc = Sc, sh = StripH;
            bool top = _side == TabSide.Top;
            var tabs = Tabs();
            int pageFace = _pages.Count > 0 ? _pages[Math.Max(0, _sel)].PageFace : Mix(BackColor, ThumbColor, 0.06f);
            int rim = Mix(BackColor, ThumbColor, 0.45f), idle = Mix(BackColor, Black, 0.18f), hot = Mix(BackColor, ThumbColor, 0.12f);
            // the page area background (the page itself paints over it; this keeps the frame line continuous)
            var page = DisplayRectangle;
            s.FillRect(page.X, page.Y, page.Width, page.Height, pageFace);
            int lineY = top ? sh - 1 : h - sh;
            float r = 4f;
            using var strip = s.CreateView(top ? new Rectangle(0, 0, w, sh) : new Rectangle(0, h - sh, w, sh));   // tab bodies never spill into the page area
            for (int i = 0; i < tabs.Length; i++)
            {
                var t = tabs[i]; bool sel = i == _sel, isHot = i == _hot && !sel;
                int face = sel ? pageFace : isHot ? hot : idle;
                // a rounded rectangle whose bottom (or top) half is hidden under the page: only the outer corners round
                if (top) { FillRound(strip, t.X + 0.5f, t.Y + 2.5f, t.Width - 1, t.Height + r, r, face); DrawRound(strip, t.X + 0.5f, t.Y + 2.5f, t.Width - 1, t.Height + r, r, sel ? rim : Mix(BackColor, ThumbColor, 0.25f), 1f); }
                else { FillRound(strip, t.X + 0.5f, t.Y - r - 2.5f, t.Width - 1, t.Height + r, r, face); DrawRound(strip, t.X + 0.5f, t.Y - r - 2.5f, t.Width - 1, t.Height + r, r, sel ? rim : Mix(BackColor, ThumbColor, 0.25f), 1f); }
                if (sel) { if (top) s.FillRect(t.X + 1, t.Y + 2, t.Width - 2, 2, Accent); else s.FillRect(t.X + 1, t.Y + t.Height - 4, t.Width - 2, 2, Accent); }
                string cap = _pages[i].Text ?? "";
                int fit = FitScale(cap, t.Width - 6 * sc, sc);
                T.Draw(s, t.X + t.Width / 2, t.Y + (top ? 2 : -1) + t.Height / 2, FitText(cap, t.Width - 6 * sc, fit), sel || !Enabled ? Fore : Mix(ForeColor, BackColor, isHot ? 0.15f : 0.35f), 0, fit, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
            }
            // the frame line between strip and page, open under the selected tab so it joins the page
            s.FillRect(0, lineY, w, 1, rim);
            if (_sel >= 0 && _sel < tabs.Length) s.FillRect(tabs[_sel].X + 1, lineY, tabs[_sel].Width - 2, 1, pageFace);
            if (Focused && ShowFocusCues && _sel >= 0 && _sel < tabs.Length) { var t = tabs[_sel]; s.DrawRect(t.X + 2.5f, t.Y + (top ? 4.5f : 1.5f), t.Width - 5, t.Height - 6, Argb(AccentRaw, 160), 1f, false, SR2D.LineOp.AlphaBlend); }
        }
    }

    /// <summary>
    /// A panel that lays its visible children out in a row or a column (a FlowLayoutPanel in the SR2D look): children are
    /// placed in <see cref="Control.Controls"/> order with <see cref="Gap"/> pixels between them; hidden ones take no room, so
    /// showing / hiding controls re-flows the rest. <see cref="Stretch"/> gives every child the full cross size (column: full
    /// width). A horizontal panel can <see cref="Wrap"/> onto further rows; <see cref="Control.AutoSize"/> makes the panel
    /// as tall (column / wrapped row) or as wide (row) as its content. A vertical panel taller than its content gets an SR2D
    /// scroll bar and follows the wheel.
    /// </summary>
    [Designer("System.Windows.Forms.Design.ParentControlDesigner, System.Design", typeof(IDesigner))]
    [ToolboxBitmap(typeof(SpriteStackPanel), "SpriteStackPanel.bmp")]
    internal sealed class SpriteStackPanel : SpritePanel
    {
        Orientation _orient = Orientation.Vertical; int _gap = 6; bool _stretch = true, _wrap, _autoSize, _inLayout;
#pragma warning disable CA2213 // _bar is added to Controls: WinForms disposes child controls with the parent
        readonly SpriteScrollBar _bar; bool _barSync; int _scroll;
#pragma warning restore CA2213

        public SpriteStackPanel()
        {
            Style = PanelStyle.Flat; Padding = new Padding(4);
            _bar = new SpriteScrollBar { Orientation = Orientation.Vertical, Visible = false };
            _bar.ValueChanged += (_, _) => { if (_barSync) return; _scroll = (int)Math.Round(_bar.Value); PerformLayout(); };
            Controls.Add(_bar);
        }

        [Category("Layout"), DefaultValue(Orientation.Vertical), Description("Column (Vertical) or row (Horizontal).")]
        public Orientation Orientation { get => _orient; set { _orient = value; PerformLayout(); } }
        [Category("Layout"), DefaultValue(6), Description("Pixels between children.")]
        public int Gap { get => _gap; set { _gap = Math.Max(0, value); PerformLayout(); } }
        [Category("Layout"), DefaultValue(true), Description("Children take the full width (column) / height (row).")]
        public bool Stretch { get => _stretch; set { _stretch = value; PerformLayout(); } }
        [Category("Layout"), DefaultValue(false), Description("Horizontal only: continue on the next row when the width is used up.")]
        public bool Wrap { get => _wrap; set { _wrap = value; PerformLayout(); } }
        [Category("Layout"), DefaultValue(false), Description("Size the panel to its content (height for a column or a wrapped row, width for a row).")]
        public override bool AutoSize { get => _autoSize; set { _autoSize = value; PerformLayout(); } }
        /// <summary>Height (column) or width (row) the content needs, padding included.</summary>
        [Browsable(false)] public int ContentSize { get; private set; }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            // a pass that runs while the panel is chain-hidden is meaningless: WinForms' Visible walks the whole
            // ancestor chain, so every child reports "invisible" and the pass would skip them - and nothing re-ran
            // when the page was finally shown (the demo Setup tab once stayed stacked under its first control)
            if (_inLayout || _bar == null || !Visible) return;
            _inLayout = true;
            try
            {
                // up to three passes: a nested AutoSize panel (a field with a label over a control) settles its own
                // Height while it is laid out, so the first pass may position the rows that follow it with the stale
                // default height - the second pass re-reads the settled heights and closes the gap / overlap
                for (int pass = 0; ; pass++) { if (!StackPass() || pass == 2) break; }
            }
            finally { _inLayout = false; }
        }
        // the compensation for the hidden-time passes: the first trigger after the panel really becomes visible
        // (its page was shown, an ancestor appeared) does the one layout that matters
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) PerformLayout();
        }
        protected override void OnParentVisibleChanged(EventArgs e)
        {
            base.OnParentVisibleChanged(e);
            if (Visible) PerformLayout();      // a tab page was just shown - its panel must lay out now, not stay stale
        }
        bool StackPass()
        {
            var r = DisplayRectangle;
            int x = r.X, y = r.Y, rowH = 0, used;
            var kids = new List<Control>(Controls.Count);
            foreach (Control c in Controls) if (c != _bar && c.Visible) kids.Add(c);
            int before = 0; foreach (var c in kids) before = before * 31 + c.Height;
            if (_orient == Orientation.Vertical)
            {
                int total = 0; foreach (var c in kids) total += c.Height + _gap; if (kids.Count > 0) total -= _gap;
                used = total + Padding.Vertical + 4; ContentSize = used;
                if (_autoSize && Height != used) { Height = used; r = DisplayRectangle; }
                bool need = !_autoSize && total > r.Height;
                _barSync = true;
                try
                {
                    _bar.Visible = need;
                    if (need) { _bar.Bounds = new Rectangle(ClientSize.Width - 16, 2, 14, ClientSize.Height - 4); _bar.Minimum = 0; _bar.Maximum = total; _bar.PageSize = r.Height; _bar.Step = 24; _scroll = Math.Clamp(_scroll, 0, Math.Max(0, total - r.Height)); _bar.Value = _scroll; }
                    else _scroll = 0;
                }
                finally { _barSync = false; }
                int w = r.Width - (need ? 18 : 0);
                y -= _scroll;
                int prevBottom = y - _gap;   // hard no-overlap rule: a row's top is never above the previous row's bottom + Gap,
                foreach (var c in kids)      // whatever any child reports mid-layout (a stale/zero height must not stack rows on top of each other)
                {
                    int h = Math.Max(c.Height, 1);
                    if (y < prevBottom + _gap) y = prevBottom + _gap;
                    if (_stretch) c.SetBounds(x, y, w, h); else c.Location = new Point(x, y);
                    prevBottom = y + h;
                    y += h + _gap;
                }
                int afterV = 0; foreach (var c in kids) afterV = afterV * 31 + c.Height;
                return afterV != before;
            }
            _bar.Visible = false;
            int right = r.Right, maxX = x, prevRight = x - _gap;
            foreach (var c in kids)
            {
                int cw = Math.Max(c.Width, 1);   // same no-overlap rule horizontally
                if (_wrap && x > r.X && x + cw > right) { x = r.X; y += rowH + _gap; rowH = 0; prevRight = x - _gap; }
                if (x < prevRight + _gap) x = prevRight + _gap;
                if (_stretch && !_wrap) c.SetBounds(x, y, cw, r.Height); else c.Location = new Point(x, y);
                prevRight = x + cw;
                x += cw + _gap; rowH = Math.Max(rowH, c.Height); maxX = Math.Max(maxX, x - _gap);
            }
            if (_wrap) { used = y + rowH - r.Y + Padding.Vertical + 4; ContentSize = used; if (_autoSize && Height != used) Height = used; }
            else { used = maxX - r.X + Padding.Horizontal + 4; ContentSize = used; if (_autoSize && Width != used) Width = used; }
            // did a child settle its own height while we laid out (nested AutoSize panel, a label that grew)? -> one more pass
            int after = 0; foreach (var c in kids) after = after * 31 + c.Height;
            return after != before;
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_bar.Visible) return;
            _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * 48, 0, (int)Math.Max(0, _bar.Maximum - _bar.PageSize));
            _barSync = true; try { _bar.Value = _scroll; } finally { _barSync = false; }
            PerformLayout();
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }
        protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); PerformLayout(); }
        protected override void OnControlRemoved(ControlEventArgs e) { base.OnControlRemoved(e); PerformLayout(); }
    }
}
