using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    // SR2D-drawn buttons, toggles, radio buttons and progress bars - the same look as SpriteKnob / SpriteSlider
    // (dark body with an off-centre highlight and a light rim, accent colour for the active state, pixel-font text),
    // on the shared SpriteControlBase (palette, TextScale, hover, synchronous repaint).

    /// <summary>Base of the clickable controls: pressed state, click on release inside, Space / Enter from the keyboard.</summary>
    internal abstract class SpriteClickable : SpriteControlBase
    {
        bool _pressed;
        /// <summary>True while the left button (or Space) is held on the control.</summary>
        [Browsable(false)] public bool IsPressed => _pressed;

        protected SpriteClickable()
        {
            // This control decides itself when a click happened (release inside, or Space / Enter) and raises Click from
            // Activate. Without this WinForms would ALSO raise Click on WM_LBUTTONUP (StandardClick) - twice per press -
            // and turn every second quick press into a double click that never reaches Click (StandardDoubleClick).
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus(); Capture = true; _pressed = true; RedrawNow();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Capture) return;
            bool inside = ClientRectangle.Contains(e.Location);
            if (inside != _pressed) { _pressed = inside; RedrawNow(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            // Read the state BEFORE releasing the capture: ReleaseCapture sends WM_CAPTURECHANGED synchronously and
            // OnMouseCaptureChanged clears _pressed (that is what makes a lost capture cancel the press).
            bool fire = _pressed; _pressed = false;
            if (Capture) Capture = false;
            if (fire) Activate();
            RedrawNow();
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (_pressed && !Capture) { _pressed = false; Redraw(); } }
        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) == Keys.Enter || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { if (!_pressed) { _pressed = true; RedrawNow(); } e.Handled = true; }
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && _pressed) { _pressed = false; Activate(); RedrawNow(); e.Handled = true; }
        }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (_pressed && !Capture) { _pressed = false; Redraw(); } }
        /// <summary>The click: raise Click (button) / flip the state (toggle, radio).</summary>
        protected virtual void Activate() => OnClick(EventArgs.Empty);


        // caption helper: text centred in a rectangle, scale dropped until it fits
        protected void CaptionIn(Sprite s, Rectangle r, string text, int color, int want)
        {
            if (string.IsNullOrEmpty(text) || r.Width < 6) return;
            int sc = FitScale(text, r.Width - 4, want);
            T.Draw(s, r.X + r.Width / 2, r.Y + r.Height / 2, FitText(text, r.Width - 4, sc), color, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
        }
    }

    /// <summary>Shape of a <see cref="SpriteButton"/>.</summary>
    internal enum ButtonShape
    {
        /// <summary>Rounded rectangle (corner radius 20 % of the height).</summary>
        Rounded,
        /// <summary>Pill: fully rounded ends.</summary>
        Pill,
        /// <summary>A round knob-like body (use a square Size); the caption goes inside.</summary>
        Round,
        /// <summary>Plain rectangle.</summary>
        Square,
    }

    /// <summary>
    /// Push button in the knob's style: dark face with a soft highlight and a light rim, the caption in the pixel font.
    /// Pressing sinks the face and lights the rim in the accent colour; <see cref="Accented"/> makes it a primary button
    /// (accent-coloured face). Raises <see cref="Control.Click"/> on release inside (or Space / Enter).
    /// </summary>
    internal sealed class SpriteButton : SpriteClickable
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.PushButton;
        ButtonShape _shape = ButtonShape.Rounded; bool _accented;

        public SpriteButton() { Size = new Size(120, 36); }

        [Category("Appearance"), DefaultValue(ButtonShape.Rounded), Description("Rounded rectangle, pill, round or square.")]
        public ButtonShape Shape { get => _shape; set { _shape = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(false), Description("Primary look: the face is filled with AccentColor.")]
        public bool Accented { get => _accented; set { _accented = value; Redraw(); } }

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            bool down = IsPressed, hot = IsHot && Enabled;
            int face = _accented ? (down ? Mix(AccentRaw, Black, 0.25f) : hot ? Mix(AccentRaw, White, 0.12f) : Accent)
                                 : Mix(BackColor, ThumbColor, down ? 0.12f : hot ? 0.24f : 0.18f);
            int hi = _accented ? Mix(face, White, 0.14f) : Mix(BackColor, ThumbColor, down ? 0.16f : 0.26f);
            int rim = down ? Accent : Mix(BackColor, ThumbColor, hot ? 0.6f : 0.45f);
            float rimW = Math.Max(1f, Math.Min(w, h) * 0.035f);
            int ts = AutoTextScale(h * 2);
            int textCol = _accented ? (Enabled ? unchecked((int)0xFFFFFFFF) : Fore) : Fore;
            if (_shape == ButtonShape.Round)
            {
                float r = Math.Min(w, h) / 2f - 1.5f, cx = w / 2f, cy = h / 2f;
                s.FillCircle(cx, cy, r, face, SR2D.LineOp.Set, true);
                if (!down) s.FillCircle(cx - r * 0.18f, cy - r * 0.18f, r * 0.55f, hi, SR2D.LineOp.Set, true);
                s.DrawCircle(cx, cy, r, rim, rimW, true);
                if (!string.IsNullOrEmpty(Text))
                {   // centred on the exact disc centre (an integer rectangle would be off by a pixel on odd sizes)
                    int avail = (int)(r * 1.5f), sc = FitScale(Text, avail, ts);
                    T.Draw(s, (int)MathF.Round(cx), (int)MathF.Round(cy), FitText(Text, avail, sc), textCol, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
                }
                return;
            }
            float rad = _shape == ButtonShape.Pill ? h / 2f : _shape == ButtonShape.Square ? 0f : h * 0.2f;
            float inset = 1.5f, off = down ? 1f : 0f;
            FillRound(s, inset, inset + off, w - 2 * inset, h - 2 * inset - off, rad, face);
            if (!down)
            {   // highlight: the upper 45 % of the face, slightly lighter
                using var top = s.CreateView(new Rectangle(0, 0, w, (int)(h * 0.45f)));
                FillRound(top, inset + rimW, inset + rimW, w - 2 * inset - 2 * rimW, h - 2 * inset - 2 * rimW, Math.Max(0, rad - rimW), hi);
            }
            DrawRound(s, inset, inset + off, w - 2 * inset, h - 2 * inset - off, rad, rim, rimW);
            CaptionIn(s, new Rectangle(0, (int)off, w, h), Text, textCol, ts);
        }
    }

    /// <summary>Look of a <see cref="SpriteToggle"/>.</summary>
    internal enum ToggleStyle
    {
        /// <summary>Modern switch: a pill track, the round handle slides right and the track turns accent when ON.</summary>
        Switch,
        /// <summary>Ellipse with a circle inside: the circle sits left (OFF) or right (ON) inside an outlined ellipse and fills with the accent.</summary>
        Ellipse,
        /// <summary>Rocker: a two-part button that tips - the pressed half sinks and lights up ("I / O").</summary>
        Rocker,
        /// <summary>Regular check box: a rounded square with an accent tick.</summary>
        CheckBox,
    }

    /// <summary>
    /// Two-state control (a check box in four looks, see <see cref="ToggleStyle"/>) with an optional caption to the right.
    /// Click / Space flips <see cref="Checked"/> and raises <see cref="CheckedChanged"/>. The switch part keeps the height
    /// of the control; the caption uses the rest of the width.
    /// </summary>
    internal sealed class SpriteToggle : SpriteClickable
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.CheckButton;
        ToggleStyle _style = ToggleStyle.Switch; bool _checked; string _onText = "I", _offText = "O";

        public SpriteToggle() { Size = new Size(140, 28); }

        [Category("Action"), Description("Raised after Checked changed (user or code).")]
        public event EventHandler? CheckedChanged;

        [Category("Appearance"), DefaultValue(ToggleStyle.Switch), Description("Switch (sliding pill), Ellipse (circle inside an ellipse), Rocker (two-part tipping button) or CheckBox.")]
        public ToggleStyle Style { get => _style; set { _style = value; Redraw(); } }

        [Category("Behavior"), DefaultValue(false), Description("The state.")]
        public bool Checked { get => _checked; set { if (_checked == value) return; _checked = value; CheckedChanged?.Invoke(this, EventArgs.Empty); AnimateTo(value ? 1f : 0f, _style == ToggleStyle.Rocker ? 140 : 180); } }

        [Category("Appearance"), DefaultValue("I"), Description("Rocker: label of the ON half.")]
        public string OnText { get => _onText; set { _onText = value ?? ""; Redraw(); } }
        [Category("Appearance"), DefaultValue("O"), Description("Rocker: label of the OFF half.")]
        public string OffText { get => _offText; set { _offText = value ?? ""; Redraw(); } }

        protected override void Activate() { Checked = !Checked; base.Activate(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SnapAnim(_checked ? 1f : 0f); }

        /// <summary>Width the switch part takes (the caption starts after it).</summary>
        int SwitchWidth(int h) => _style switch { ToggleStyle.CheckBox => h, ToggleStyle.Rocker => (int)(h * 2.2f), _ => (int)(h * 1.9f) };

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            int sw = Math.Min(w, SwitchWidth(h));
            bool on = _checked, down = IsPressed, hot = IsHot && Enabled;
            float t = IsAnimating || Animated ? AnimT : (on ? 1f : 0f);            // 0 = off look, 1 = on look
            int off = Mix(BackColor, ThumbColor, hot ? 0.34f : 0.28f), rim = Mix(BackColor, ThumbColor, hot ? 0.65f : 0.5f);
            int onCol = down ? Mix(AccentRaw, Black, 0.2f) : Accent;
            int onRim = Mix(AccentRaw, White, 0.3f);
            float rimW = Math.Max(1f, h * 0.05f);
            switch (_style)
            {
                case ToggleStyle.Switch:
                {
                    float th = h * 0.7f, ty = (h - th) / 2, tx = 1.5f, tw = sw - 3;
                    FillRound(s, tx, ty, tw, th, th / 2, Lerp(off, onCol, t));
                    DrawRound(s, tx, ty, tw, th, th / 2, Lerp(rim, onRim, t), rimW);
                    float hr = th / 2 - rimW - 1, x0 = tx + th / 2, x1 = tx + tw - th / 2, hx = x0 + (x1 - x0) * t, hy = h / 2f;
                    if (down) hr *= 0.9f;
                    // the handle stretches a little while it travels (0 at the ends, +25 % in the middle)
                    float stretch = hr * 0.5f * (1f - MathF.Abs(2f * t - 1f));
                    FillRound(s, hx - hr - stretch / 2, hy - hr, 2 * hr + stretch, 2 * hr, hr, Thumb);
                    s.FillCircle(hx - hr * 0.2f, hy - hr * 0.2f, hr * 0.5f, Mix(ThumbColor, White, 0.5f), SR2D.LineOp.Set, true);
                    break;
                }
                case ToggleStyle.Ellipse:
                {
                    float ry = h / 2f - 1.5f, rx = sw / 2f - 1.5f, cx = sw / 2f, cy = h / 2f;
                    s.FillEllipse(cx, cy, rx, ry, Mix(BackColor, ThumbColor, 0.1f), SR2D.LineOp.Set, true);
                    s.DrawEllipse(cx, cy, rx, ry, Lerp(rim, Accent, t), rimW * 1.2f, true);
                    float cr = ry - rimW * 2.5f, x0 = cx - rx + ry, x1 = cx + rx - ry, ccx = x0 + (x1 - x0) * t;
                    if (down) cr *= 0.9f;
                    s.FillCircle(ccx, cy, cr, Lerp(off, onCol, t), SR2D.LineOp.Set, true);
                    s.FillCircle(ccx - cr * 0.2f, cy - cr * 0.2f, cr * 0.45f, Lerp(Mix(BackColor, ThumbColor, 0.4f), onRim, t), SR2D.LineOp.Set, true);
                    break;
                }
                case ToggleStyle.Rocker:
                {
                    // A rocker seen slightly from above: two caps on a pivot in the middle of a dark well. The pressed cap is
                    // DOWN (sunk into the well: dark face, a shadow band along its top edge, no side wall) and the other cap is
                    // UP (face lifted, a light highlight on top, a dark side wall visible below it). t tips it: 0 = O down /
                    // I up, 1 = I down / O up. The ON cap takes the accent colour as it sinks; the sunk label lights up.
                    float lift = Math.Max(2f, (h - 3) * 0.16f);
                    float x = 1.5f, y = 1.5f + lift, ww = sw - 3, wellH = h - 3 - lift, rad = wellH * 0.18f, half = ww / 2;
                    int wellCol = Mix(BackColor, ThumbColor, 0.05f), wallCol = Mix(BackColor, ThumbColor, 0.02f);
                    int upFace = Mix(BackColor, ThumbColor, 0.36f), upHi = Mix(BackColor, ThumbColor, 0.55f);
                    int downOff = Mix(BackColor, ThumbColor, 0.1f), shadow = Mix(BackColor, Black, 0.5f);
                    FillRound(s, x, y, ww, wellH, rad, wellCol);                              // the well
                    DrawRound(s, x, y, ww, wellH, rad, Lerp(rim, Mix(AccentRaw, BackColor, 0.3f), t), rimW);   // its rim - under the caps (a raised cap stands proud of the well)
                    s.DrawWideLine(x + half, y + 1, x + half, y + wellH - 1, wallCol, Math.Max(1f, rimW), true);   // the pivot seam
                    int ts = AutoTextScale(h * 2);
                    float inset = Math.Max(1f, rimW);                                        // caps sit inside the well rim
                    for (int k = 0; k < 2; k++)
                    {
                        bool isOn = k == 1;
                        float up = isOn ? 1f - t : t;                                        // 1 = this cap fully raised
                        float hx = x + k * half;
                        float capX = x + inset, capW = ww - 2 * inset, capH = wellH - 2 * inset, capRad = Math.Max(0, rad - inset);
                        float dy = -lift * up;                                               // raised: shifted up by the lift
                        int face = isOn ? Lerp(onCol, upFace, up) : Lerp(downOff, upFace, up);
                        using (var v = s.CreateView(new Rectangle((int)MathF.Round(hx), 0, (int)MathF.Round(half), h)))
                        {
                            if (up > 0.02f) FillRound(v, capX, y + inset + dy + lift, capW, capH, capRad, wallCol);   // side wall below the raised cap
                            FillRound(v, capX, y + inset + dy, capW, capH, capRad, face);
                            if (up > 0.02f)                                                  // highlight on the upper part of a raised cap
                                using (var hl = v.CreateView(new Rectangle((int)MathF.Round(hx), 0, (int)MathF.Round(half), (int)(y + inset + dy + capH * 0.45f))))
                                    FillRound(hl, capX + inset, y + inset + dy + inset, capW - 2 * inset, capH - 2 * inset, Math.Max(0, capRad - inset), Lerp(face, upHi, up));
                            if (up < 0.98f)                                                  // shadow band under the well rim on a sunk cap
                                using (var sh = v.CreateView(new Rectangle((int)MathF.Round(hx), 0, (int)MathF.Round(half), (int)(y + inset + Math.Max(1f, capH * 0.2f)))))
                                    FillRound(sh, capX, y + inset + dy, capW, capH, capRad, Lerp(Mix(face, shadow, 0.55f), face, up));
                        }
                        string lbl = isOn ? _onText : _offText;
                        int litLbl = isOn ? unchecked((int)0xFFFFFFFF) : Mix(BackColor, ForeColor, 0.55f);
                        int lc = Lerp(litLbl, Mix(BackColor, ForeColor, 0.85f), up);
                        if (lbl.Length > 0) { int sc = FitScale(lbl, (int)half - 4, ts); T.Draw(s, (int)MathF.Round(hx + half / 2), (int)MathF.Round(y + wellH / 2 + dy + (1f - up) * 1f), lbl, lc, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center); }
                    }
                    break;
                }
                default:
                {
                    float bs = h - 4, bx = 2, by = 2, rad = bs * 0.2f;
                    FillRound(s, bx, by, bs, bs, rad, Lerp(Mix(BackColor, ThumbColor, down ? 0.1f : 0.16f), onCol, t));
                    DrawRound(s, bx, by, bs, bs, rad, Lerp(rim, onRim, t), rimW);
                    if (t > 0.01f)
                    {   // the tick draws itself: first the short stroke, then the long one
                        float lw = Math.Max(1.5f, bs * 0.12f);
                        int white = unchecked((int)0xFFFFFFFF);
                        float ax = bx + bs * 0.24f, ay = by + bs * 0.52f, mx = bx + bs * 0.42f, my = by + bs * 0.72f, ex = bx + bs * 0.78f, ey = by + bs * 0.3f;
                        float p1 = Math.Clamp(t / 0.4f, 0f, 1f), p2 = Math.Clamp((t - 0.4f) / 0.6f, 0f, 1f);
                        s.DrawWideLine(ax, ay, ax + (mx - ax) * p1, ay + (my - ay) * p1, white, lw, true, SR2D.LineOp.Set, true);
                        if (p2 > 0) s.DrawWideLine(mx, my, mx + (ex - mx) * p2, my + (ey - my) * p2, white, lw, true, SR2D.LineOp.Set, true);
                    }
                    break;
                }
            }
            // caption to the right, vertically centred
            if (!string.IsNullOrEmpty(Text) && w - sw > 12)
            {
                int ts = AutoTextScale(h * 3);
                int sc = FitScale(Text, w - sw - 8, ts);
                T.Draw(s, sw + 6, h / 2, FitText(Text, w - sw - 8, sc), Fore, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            }
        }
    }

    /// <summary>
    /// Radio button in the knob's style: a small round body with a light rim, the accent dot when selected, caption to the
    /// right. Radios with the same parent and the same <see cref="GroupName"/> are exclusive (like WinForms, the parent is
    /// the group; the name lets several groups share a panel).
    /// </summary>
    internal sealed class SpriteRadio : SpriteClickable
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.RadioButton;
        bool _checked; string _group = "";

        public SpriteRadio() { Size = new Size(140, 24); TabStop = false; }

        [Category("Action"), Description("Raised after Checked changed (user or code).")]
        public event EventHandler? CheckedChanged;

        [Category("Behavior"), DefaultValue(""), Description("Radios with the same parent and group name are mutually exclusive ('' = all radios of the parent).")]
        public string GroupName { get => _group; set { _group = value ?? ""; } }

        [Category("Behavior"), DefaultValue(false), Description("Selected state. Setting true clears the other radios of the group.")]
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value; TabStop = value;
                if (value && Parent != null)
                    foreach (Control c in Parent.Controls)
                        if (c != this && c is SpriteRadio r && r._group == _group && r._checked) { r._checked = false; r.TabStop = false; r.CheckedChanged?.Invoke(r, EventArgs.Empty); r.AnimateTo(0f, 160); }
                CheckedChanged?.Invoke(this, EventArgs.Empty);
                AnimateTo(value ? 1f : 0f, 200);
            }
        }

        protected override void Activate() { if (!Checked) Checked = true; base.Activate(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SnapAnim(_checked ? 1f : 0f); }

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            float r = h / 2f - 2, cx = h / 2f, cy = h / 2f;
            float t = IsAnimating || Animated ? AnimT : (_checked ? 1f : 0f);
            PaintBody(s, cx, cy, r, IsPressed || _checked, Math.Max(1f, r * 0.12f));
            if (t > 0.01f)
            {   // the dot grows from the centre (with a slight overshoot) and the rim takes the accent
                float grow = t < 0.7f ? t / 0.7f * 1.12f : 1.12f - 0.12f * (t - 0.7f) / 0.3f;
                float dr = r * 0.45f * grow;
                s.DrawCircle(cx, cy, r, Lerp(Mix(BackColor, ThumbColor, 0.75f), Accent, t), Math.Max(1f, r * 0.12f), true);
                s.FillCircle(cx, cy, dr, Accent, SR2D.LineOp.Set, true);
                s.FillCircle(cx - dr * 0.27f, cy - dr * 0.27f, dr * 0.44f, Mix(AccentRaw, White, 0.4f), SR2D.LineOp.Set, true);
            }
            if (!string.IsNullOrEmpty(Text) && w - h > 12)
            {
                int ts = AutoTextScale(h * 3);
                int sc = FitScale(Text, w - h - 8, ts);
                T.Draw(s, h + 6, h / 2, FitText(Text, w - h - 8, sc), Fore, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            }
        }
    }

    /// <summary>Shape of a <see cref="SpriteProgress"/>.</summary>
    internal enum ProgressStyle
    {
        /// <summary>Horizontal bar filling left to right.</summary>
        Horizontal,
        /// <summary>Vertical bar filling bottom to top.</summary>
        Vertical,
        /// <summary>Ring filling clockwise from 12 o'clock (the knob's gauge without the knob); the text sits in the middle.</summary>
        Ring,
    }

    /// <summary>
    /// Progress indicator in the knob's style (track colour + accent fill), horizontal / vertical / ring. Shows
    /// <see cref="Value"/> in Minimum..Maximum, the caption and optionally the percentage; <see cref="Marquee"/> animates
    /// an indeterminate segment (call <see cref="Tick"/> from a timer or set <see cref="Phase"/>). Segments can be
    /// drawn with <see cref="Segments"/> (LED-style bar).
    /// </summary>
    internal sealed class SpriteProgress : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.ProgressBar;
        ProgressStyle _style = ProgressStyle.Horizontal;
        double _min, _max = 100, _value; bool _showPercent = true, _marquee; float _phase; int _segments;

        public SpriteProgress() { Size = new Size(220, 22); TabStop = false; Cursor = Cursors.Default; }

        [Category("Appearance"), DefaultValue(ProgressStyle.Horizontal), Description("Horizontal, vertical or ring.")]
        public ProgressStyle Style { get => _style; set { _style = value; Redraw(); } }
        [Category("Behavior"), DefaultValue(0.0)] public double Minimum { get => _min; set { _min = value; if (_max < _min) _max = _min; Redraw(); } }
        [Category("Behavior"), DefaultValue(100.0)] public double Maximum { get => _max; set { _max = value; if (_min > _max) _min = _max; Redraw(); } }
        [Category("Behavior"), DefaultValue(0.0), Description("Current position (clamped).")]
        public double Value { get => _value; set { value = Math.Clamp(value, _min, _max); if (value == _value) return; _value = value; if (IsHandleCreated) RedrawNow(); else Redraw(); } }
        [Category("Appearance"), DefaultValue(true), Description("Draw the percentage (or the caption when Text is set).")]
        public bool ShowPercent { get => _showPercent; set { _showPercent = value; Redraw(); } }
        [Category("Appearance"), DefaultValue(false), Description("Indeterminate: a segment travels along the track instead of the fill (advance with Tick / Phase).")]
        public bool Marquee { get => _marquee; set { _marquee = value; Redraw(); } }
        [Category("Appearance"), DefaultValue(0f), Description("Marquee position 0..1 (wraps).")]
        public float Phase { get => _phase; set { _phase = value - MathF.Floor(value); if (IsHandleCreated) RedrawNow(); else Redraw(); } }
        [Category("Appearance"), DefaultValue(0), Description("Number of LED-like segments (0 = continuous fill).")]
        public int Segments { get => _segments; set { _segments = Math.Max(0, value); Redraw(); } }

        /// <summary>Advance the marquee by <paramref name="dt"/> of a full sweep (e.g. 0.02 from a 20 ms timer).</summary>
        public void Tick(float dt = 0.02f) => Phase = _phase + dt;

        double Fraction => _max > _min ? (_value - _min) / (_max - _min) : 0;
        string Caption => !string.IsNullOrEmpty(Text) ? Text : _showPercent && !_marquee ? $"{Fraction * 100:0}%" : "";

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            double f = Fraction; string cap = Caption;
            if (_style == ProgressStyle.Ring)
            {
                float r = Math.Min(w, h) / 2f - 1.5f, cx = w / 2f, cy = h / 2f, tw = Math.Max(2f, r * 0.16f), tr = r - tw / 2;
                s.DrawCircle(cx, cy, tr, Track, tw, true);
                int maxN = (int)(tr * 2 * MathF.PI / 3f) + 2;
                var v = new PointF[maxN + 1];
                void Arc(double from, double len, int col)
                {
                    if (len <= 0) return;
                    if (len >= 0.999) { s.DrawCircle(cx, cy, tr, col, tw, true); return; }
                    int n = Math.Clamp((int)(tr * 2 * MathF.PI * len / 3f), 2, maxN);
                    for (int i = 0; i <= n; i++) { float a = (float)((270 + 360 * (from + len * i / n)) * Math.PI / 180); v[i] = new PointF(cx + MathF.Cos(a) * tr, cy + MathF.Sin(a) * tr); }
                    s.DrawPolyline(v.AsSpan(0, n + 1), col, tw, true, false, SR2D.LineOp.Set, true);
                }
                if (_marquee) { Arc(_phase, 0.25, Accent); if (_phase > 0.75) Arc(0, _phase - 0.75, Accent); }
                else if (_segments > 0) { for (int i = 0; i < _segments; i++) if ((i + 0.5) / _segments <= f) Arc((double)i / _segments + 0.05 / _segments, 0.9 / _segments, Accent); }
                else Arc(0, f, Accent);
                int ts = AutoTextScale((int)(r * 2.2f));
                if (cap.Length > 0) { int sc = FitScale(cap, (int)(tr * 1.4f), ts); T.Draw(s, (int)cx, (int)cy, FitText(cap, (int)(tr * 1.4f), sc), Fore, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center); }
                return;
            }
            bool hz = _style == ProgressStyle.Horizontal;
            float x = 1.5f, y = 1.5f, ww = w - 3, hh = h - 3, rad = Math.Min(ww, hh) * 0.3f;
            FillRound(s, x, y, ww, hh, rad, Track);
            float inset = Math.Max(1f, Math.Min(ww, hh) * 0.12f);
            float ix = x + inset, iy = y + inset, iw = ww - 2 * inset, ih = hh - 2 * inset, irad = Math.Max(0, rad - inset);
            void Fill(double from, double len)
            {
                if (len <= 0) return;
                if (hz) FillRound(s, ix + (float)(iw * from), iy, (float)(iw * len), ih, irad, Accent);
                else FillRound(s, ix, iy + (float)(ih * (1 - from - len)), iw, (float)(ih * len), irad, Accent);
            }
            if (_marquee) { double len = 0.3, p = _phase * (1 + len) - len; Fill(Math.Max(0, p), Math.Min(len, Math.Min(p + len, 1) - Math.Max(0, p))); }
            else if (_segments > 0) { double gap = 0.15 / _segments, seg = 1.0 / _segments; for (int i = 0; i < _segments; i++) if ((i + 0.5) / _segments <= f) Fill(i * seg + gap / 2, seg - gap); }
            else Fill(0, f);
            // highlight strip along the top / left of the fill and the track, like the button face
            using (var top = s.CreateView(new Rectangle(0, 0, w, hz ? (int)(h * 0.45f) : h)))
                DrawRound(top, x + 0.5f, y + 0.5f, ww - 1, hh - 1, rad, Mix(BackColor, ThumbColor, 0.35f), 1f);
            if (cap.Length > 0)
            {
                int ts = AutoTextScale(hz ? h * 3 : w * 3);
                int avail = hz ? w - 8 : h - 8;
                int sc = FitScale(cap, avail, ts);
                // text over the bar: light on the track, dark where the accent fill would clash - use an outline colour
                if (hz) T.Draw(s, w / 2, h / 2, FitText(cap, avail, sc), Fore, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
                else T.Draw(s, w / 2, h / 2, FitText(cap, avail, sc), Fore, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
            }
        }
    }
}
