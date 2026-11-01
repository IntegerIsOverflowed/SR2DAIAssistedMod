using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    // SR2D-drawn buttons, toggles, radio buttons, multi-button radio switch grids and progress bars - the same look as
    // SpriteKnob / SpriteSlider (dark body with an off-centre highlight and a light rim, accent colour for the active state,
    // pixel-font text), on the shared SpriteControlBase (palette, TextScale, hover, synchronous repaint).

    /// <summary>Base of the clickable controls: pressed state, click on release inside, Space / Enter from the keyboard.</summary>
    public abstract class SpriteClickable : SpriteControlBase
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

        /// <summary>Draws <paramref name="text"/> centred horizontally and vertically around (<paramref name="cx"/>, <paramref name="cy"/>),
        /// automatically wrapping onto multiple lines when wider than <paramref name="maxW"/> px so the text stays inside the cap surface.</summary>
        protected void DrawWrappedCentered(Sprite s, float cx, float cy, int maxW, int maxH, string text, int color, int wantScale)
        {
            if (string.IsNullOrEmpty(text) || maxW < 4 || maxH < 4) return;
            int sc = Math.Max(1, wantScale);
            string wrapped = text;
            while (sc > 1)
            {
                wrapped = T.Measure(text, sc).Width <= maxW ? text : WrapText(text, maxW, sc);
                var sz = T.Measure(wrapped, sc);
                if (sz.Width <= maxW && sz.Height <= maxH) break;
                sc--;
            }
            if (T.Measure(text, sc).Width > maxW)
                wrapped = WrapText(text, maxW, sc);
            var lines = wrapped.Split('\n');
            int lh = TextLineHeight(sc), gh = T.GlyphHeight(sc);
            int totalH = (lines.Length - 1) * lh + gh;
            int y0 = (int)MathF.Round(cy - totalH / 2f);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                if (T.Measure(line, sc).Width > maxW) line = FitText(line, maxW, sc);
                int lw = T.Measure(line, sc).Width;
                int x0 = (int)MathF.Round(cx - lw / 2f);
                T.Draw(s, x0, y0 + i * lh, line, color, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopLeft);
            }
        }
    }

    /// <summary>Shape of a <see cref="SpriteButton"/>.</summary>
    public enum ButtonShape
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
    [ToolboxBitmap(typeof(SpriteButton), "SpriteButton.bmp")]
    [DefaultEvent("Click"), DefaultProperty("Text")]
    public sealed class SpriteButton : SpriteClickable, IButtonControl
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.PushButton;
        ButtonShape _shape = ButtonShape.Rounded; bool _accented, _autoSize, _fitting; ControlLook _look = ControlLook.Classic;
        DialogResult _dialogResult = DialogResult.None; bool _isDefault;

        public SpriteButton() { Size = new Size(120, 36); }

        [Category("Layout"), DefaultValue(false), Browsable(true), EditorBrowsable(EditorBrowsableState.Always), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible), Description("Size the button to its caption (when false, Size is manual).")]
        public override bool AutoSize { get => _autoSize; set { if (_autoSize == value) return; _autoSize = value; base.AutoSize = value; Fit(); Redraw(); } }

        public override Size GetPreferredSize(Size proposed) => MeasurePreferred(Height > 0 ? Height : 36);
        Size MeasurePreferred(int h)
        {
            h = Math.Max(18, h);
            int sc = AutoTextScale(h * 2);
            if (_shape == ButtonShape.Round) return new Size(h, h);
            int tw = string.IsNullOrEmpty(Text) ? 0 : T.Measure(Text, sc).Width + 16;
            return new Size(Math.Max(h, tw + Padding.Horizontal), h);
        }
        void Fit()
        {
            if (!_autoSize || _fitting) return;
            var want = MeasurePreferred(Height > 0 ? Height : 36);
            if (Size != want) { _fitting = true; try { Size = want; } finally { _fitting = false; } }
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Redraw(); }
        protected override void OnTextScaleChanged() { Fit(); base.OnTextScaleChanged(); }
        protected override void OnTextFontChanged() { Fit(); Redraw(); }
        protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); Fit(); Redraw(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); Redraw(); }

        [Category("Behavior"), DefaultValue(DialogResult.None), Description("The dialog result produced in a modal form by clicking the button.")]
        public DialogResult DialogResult { get => _dialogResult; set => _dialogResult = value; }

        public void NotifyDefault(bool value) { if (_isDefault != value) { _isDefault = value; Redraw(); } }
        public void PerformClick() { if (CanSelect) Activate(); }
        protected override void Activate()
        {
            if (_dialogResult != DialogResult.None && FindForm() is Form f) f.DialogResult = _dialogResult;
            base.Activate();
        }

        [Category("Appearance"), DefaultValue(ButtonShape.Rounded), Description("Rounded rectangle, pill, round or square.")]
        public ButtonShape Shape { get => _shape; set { _shape = value; Fit(); Redraw(); } }

        [Category("Appearance"), DefaultValue(false), Description("Primary look: the face is filled with AccentColor.")]
        public bool Accented { get => _accented; set { _accented = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(ControlLook.Classic), Description("Classic (shaded face), Hairline (flat, keyline and corner ticks) or Segmented (flat dot-matrix face with an LED strip that lights while hot or pressed).")]
        public ControlLook Look { get => _look; set { _look = value; Redraw(); } }

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            bool down = IsPressed, hot = IsHot && Enabled;
            if (_look != ControlLook.Classic) { PaintLook(s, w, h, down, hot); return; }
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

        /// <summary>The flat looks: <see cref="ControlLook.Hairline"/> and <see cref="ControlLook.Segmented"/> (same shapes, sizes and caption as Classic).</summary>
        void PaintLook(Sprite s, int w, int h, bool down, bool hot)
        {
            bool hair = _look == ControlLook.Hairline;
            float inset = 1.5f, off = down ? 1f : 0f;
            int face = _accented ? (down ? Mix(AccentRaw, Black, 0.2f) : hot ? Mix(AccentRaw, White, 0.12f) : Accent)
                       : hair ? (down ? Mix(BackColor, Accent, 0.24f) : Mix(BackColor, ThumbColor, hot ? 0.12f : 0.07f))
                       : (down ? Mix(BackColor, ThumbColor, 0.12f) : Mix(BackColor, ThumbColor, hot ? 0.24f : 0.18f));
            int rim = down || _accented ? Accent : _look == ControlLook.Hairline ? Mix(Accent, BackColor, hot ? 0.25f : 0.5f) : Mix(BackColor, ThumbColor, hot ? 0.7f : 0.5f);
            int mark = _accented ? White : Accent;                  // keyline, corner / dial ticks, LED strip
            int dim = Mix(face, Black, 0.35f), dots = Mix(face, Black, 0.2f);   // unlit LED cells, dot-matrix dots
            bool lit = _accented || hot || down;                    // the LED strip (Segmented) lights up
            int textCol = _accented ? (Enabled ? White : Fore) : Fore;
            int ts = AutoTextScale(h * 2);
            if (_shape == ButtonShape.Round)
            {
                float r = Math.Min(w, h) / 2f - 1.5f, cx = w / 2f, cy = h / 2f;
                s.FillCircle(cx, cy, r, face, SR2D.LineOp.Set, true);
                if (hair)
                {
                    s.DrawCircle(cx, cy, r, rim, 1f, true);
                    s.DrawCircle(cx, cy, r - 3f, Argb(mark, 110), 1f, true, SR2D.LineOp.AlphaBlend);
                    LookDial(s, cx, cy, r - 9f, 12, 2, 4, 3, Argb(mark, 200));
                }
                else
                {
                    LookDots(s, w, h, r, true, dots);
                    for (int k = 0; k < 24; k++)
                    {
                        double a = (270 + 360.0 * k / 24) * Math.PI / 180;
                        s.FillCircle(cx + (float)Math.Cos(a) * (r - 3f), cy + (float)Math.Sin(a) * (r - 3f), 1.4f, lit ? mark : dim, SR2D.LineOp.Set, true);
                    }
                }
                if (!string.IsNullOrEmpty(Text))
                {
                    int avail = (int)(r * 1.5f), sc = FitScale(Text, avail, ts);
                    T.Draw(s, (int)MathF.Round(cx), (int)MathF.Round(cy), FitText(Text, avail, sc), textCol, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
                }
                return;
            }
            float rad = _shape == ButtonShape.Pill ? h / 2f : _shape == ButtonShape.Square ? 0f : h * 0.2f;
            float x = inset, y = inset + off, ww = w - 2 * inset, hh = h - 2 * inset - off;
            FillRound(s, x, y, ww, hh, rad, face);
            if (hair)
            {
                DrawRound(s, x, y, ww, hh, rad, rim, 1f);
                s.DrawRoundRect(x + 3f, y + 3f, ww - 6, hh - 6, Math.Max(0, rad - 3), Argb(mark, 110), 1f, true, SR2D.LineOp.AlphaBlend);
                // corner ticks sit inside the keyline; a pill's ends are round, so it keeps the keyline alone
                if (_shape != ButtonShape.Pill && ww >= 24 && hh >= 16)
                    LookCornerTicks(s, x + 5f, y + 5f, ww - 10, hh - 10, Math.Clamp((int)Math.Min(ww, hh) / 6, 2, 6), mark);
                // an instrument index bar along the top inside edge (not on pills: their ends are round)
                if (_shape != ButtonShape.Pill && ww >= 40 && hh >= 16)
                    s.FillRect((int)MathF.Round(x + 8f), (int)MathF.Round(y + 3f), (int)MathF.Round(ww - 16f), 2, mark);
            }
            else
            {
                // solid face (no dot matrix); LED strip: chunky cells along the bottom inside edge, lit while hot, pressed or accented
                float sy = y + hh - 6f, sx0 = x + Math.Max(rad, 6f), sx1 = x + ww - Math.Max(rad, 6f);
                int n = Math.Max(3, (int)((sx1 - sx0) / 9f));
                if (sx1 > sx0) LookCells(s, sx0, sx1, sy + 1.5f, 3f, true, n, 3f, _ => lit ? mark : dim);
            }
            CaptionIn(s, new Rectangle(0, (int)off, w, h), Text, textCol, ts);
        }

        /// <summary>
        /// The dot-matrix texture of the Segmented face: one dot every four pixels, kept inside the rounded outline
        /// (<paramref name="rad"/>; <paramref name="round"/> = a disc of radius <paramref name="rad"/> instead).
        /// </summary>
        static void LookDots(Sprite s, int w, int h, float rad, bool round, int col)
        {
            for (int y = 4; y < h - 4; y += 4)
                for (int x = 4; x < w - 4; x += 4)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    if (round)
                    {
                        float dx = px - w / 2f, dy = py - h / 2f;
                        if (dx * dx + dy * dy > (rad - 4) * (rad - 4)) continue;
                    }
                    else if (rad > 4)
                    {
                        float qx = Math.Clamp(px, rad, w - rad), qy = Math.Clamp(py, rad, h - rad);
                        float dx = px - qx, dy = py - qy;
                        if (dx * dx + dy * dy > (rad - 3) * (rad - 3)) continue;
                    }
                    s.FillRect(x, y, 1, 1, col);
                }
        }
    }

    /// <summary>Look of a <see cref="SpriteToggle"/>.</summary>
    public enum ToggleStyle
    {
        /// <summary>Modern switch: a pill track, the round handle slides right and the track turns accent when ON.</summary>
        Switch,
        /// <summary>Ellipse with a circle inside: the circle sits left (OFF) or right (ON) inside an outlined ellipse and fills with the accent.</summary>
        Ellipse,
        /// <summary>Rocker: a one-piece solid pivoting power switch - one side is raised while the opposite side is lowered ("I / O").</summary>
        Rocker,
        /// <summary>Regular check box: a rounded square with an accent tick.</summary>
        CheckBox,
    }

    /// <summary>
    /// Two-state control (a check box in four looks, see <see cref="ToggleStyle"/>) with an optional caption to the right.
    /// Click / Space flips <see cref="Checked"/> and raises <see cref="CheckedChanged"/>. With <see cref="AutoSize"/> on (the default,
    /// like a native CheckBox), the control's width fits its switch graphic plus <see cref="Control.Text"/> (shrinking to the switch
    /// graphic alone when <see cref="Control.Text"/> is empty).
    /// </summary>
    [ToolboxBitmap(typeof(SpriteToggle), "SpriteToggle.bmp")]
    [DefaultEvent("CheckedChanged"), DefaultProperty("Checked"), DefaultBindingProperty("Checked")]
    public sealed class SpriteToggle : SpriteClickable
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.CheckButton;
        ToggleStyle _style = ToggleStyle.Switch; bool _checked, _autoSize = true, _onlyButtonGlows = true, _fitting; string _onText = "I", _offText = "O";

        public SpriteToggle()
        {
            base.AutoSize = true;
            Size = new Size(SwitchWidth(28), 28);
            Fit();
        }

        [Category("Layout"), DefaultValue(true), Browsable(true), EditorBrowsable(EditorBrowsableState.Always), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible), Description("Size the control's width to its switch graphic and caption (when Text is empty, bounds shrink to the switch graphic only).")]
        public override bool AutoSize { get => _autoSize; set { if (_autoSize == value) return; _autoSize = value; base.AutoSize = value; Fit(); Redraw(); } }

        [Category("Action"), Description("Raised after Checked changed (user or code).")]
        public event EventHandler? CheckedChanged;

        [Category("Appearance"), DefaultValue(ToggleStyle.Switch), Description("Switch (sliding pill), Ellipse (circle inside an ellipse), Rocker (one-piece pivoting power switch) or CheckBox.")]
        public ToggleStyle Style { get => _style; set { _style = value; Fit(); Redraw(); } }

        [Category("Behavior"), DefaultValue(false), Description("The state.")]
        public bool Checked { get => _checked; set { if (_checked == value) return; _checked = value; CheckedChanged?.Invoke(this, EventArgs.Empty); AnimateTo(value ? 1f : 0f, _style == ToggleStyle.Rocker ? 140 : 180); } }

        [Category("Appearance"), DefaultValue("I"), Description("Rocker: label of the ON half (wraps and centres inside the cap).")]
        public string OnText { get => _onText; set { _onText = value ?? ""; Redraw(); } }
        [Category("Appearance"), DefaultValue("O"), Description("Rocker: label of the OFF half (wraps and centres inside the cap).")]
        public string OffText { get => _offText; set { _offText = value ?? ""; Redraw(); } }
        [Category("Appearance"), DefaultValue(true), Description("Rocker: when true (default), only the rocker button itself glows in AccentColor when Checked, leaving the housing and button contour unlit. Set false to also glow the contour.")]
        public bool OnlyButtonGlows { get => _onlyButtonGlows; set { if (_onlyButtonGlows == value) return; _onlyButtonGlows = value; Redraw(); } }

        protected override void Activate() { Checked = !Checked; base.Activate(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SnapAnim(_checked ? 1f : 0f); }

        /// <summary>Width the switch part takes (the caption starts after it).</summary>
        int SwitchWidth(int h) => _style switch { ToggleStyle.CheckBox => h, ToggleStyle.Rocker => (int)MathF.Round(h * 2.4f), _ => (int)MathF.Round(h * 1.9f) };

        public override Size GetPreferredSize(Size proposed) => MeasurePreferred(Height > 0 ? Height : 28);
        Size MeasurePreferred(int h)
        {
            h = Math.Max(16, h);
            int sc = AutoTextScale(h * 3);
            int minH = TextLineHeight(sc) + Padding.Vertical + 4;
            if (h < minH) h = minH;
            int sw = SwitchWidth(h);
            int w = string.IsNullOrEmpty(Text) ? sw + Padding.Horizontal : sw + 6 + T.Measure(Text, sc).Width + 3 + Padding.Horizontal;
            return new Size(Math.Max(16, w), h);
        }
        void Fit()
        {
            if (!_autoSize || _fitting) return;
            var want = MeasurePreferred(Height > 0 ? Height : 28);
            if (Size != want) { _fitting = true; try { Size = want; } finally { _fitting = false; } }
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Redraw(); }
        protected override void OnTextScaleChanged() { Fit(); base.OnTextScaleChanged(); }
        protected override void OnTextFontChanged() { Fit(); Redraw(); }
        protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); Fit(); Redraw(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); Redraw(); }

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            int sw = string.IsNullOrEmpty(Text) && _style != ToggleStyle.CheckBox ? w : Math.Min(w, SwitchWidth(h));
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
                    // 3D elliptical well + 3D circular button cap in the original Rocker style:
                    // recessed dark well with a top inner shadow band, and a raised circular cap with a dark 3D cylinder
                    // wall below it and a horizontal-clipped upper 45% bevel highlight on top (depresses into the well when pressed).
                    float lift = Math.Max(2.5f, (h - 3) * 0.16f);
                    float inset = Math.Max(1f, rimW);
                    float rx = sw / 2f - 1.5f, ry = Math.Max(3f, (h - lift * 0.7f) / 2f - 1.5f);
                    float cx = sw / 2f, cy = lift * 0.45f + (h - lift * 0.45f) / 2f;
                    int wellCol = Mix(BackColor, Black, 0.50f);
                    int wellShadow = Mix(BackColor, Black, 0.72f);
                    s.FillEllipse(cx, cy, rx, ry, wellCol, SR2D.LineOp.Set, true);
                    int shBot = Math.Clamp((int)MathF.Round(cy - ry + inset + Math.Max(2f, ry * 0.42f)), 1, h);
                    using (var sh = s.CreateView(new Rectangle(0, 0, sw, shBot)))
                        sh.FillEllipse(cx, cy, Math.Max(1f, rx - inset), Math.Max(1f, ry - inset), wellShadow, SR2D.LineOp.Set, true);
                    int ellRim = Lerp(rim, Mix(AccentRaw, BackColor, 0.30f), t);
                    s.DrawEllipse(cx, cy, rx, ry, ellRim, rimW * 1.2f, true);

                    float rxIn = Math.Max(2f, rx - inset - 0.5f), ryIn = Math.Max(2f, ry - inset - 0.5f);
                    float cr = ryIn * 0.84f;
                    float ratio = Math.Clamp(cr / ryIn, 0f, 0.98f);
                    float dx = Math.Max(cr * 0.4f, rxIn * MathF.Sqrt(1f - ratio * ratio) - cr * 0.35f);
                    float x0 = cx - dx, x1 = cx + dx, ccx = x0 + (x1 - x0) * t;
                    float up = down ? 0.45f : 1f;
                    float ccy = cy - lift * up;

                    int upFace = Mix(BackColor, ThumbColor, hot ? 0.44f : 0.38f);
                    int upHi = Mix(BackColor, ThumbColor, hot ? 0.64f : 0.58f);
                    int face = Lerp(upFace, Accent, t);
                    int hi = Lerp(upHi, Mix(AccentRaw, White, 0.28f), t);
                    int sideCol = Mix(face, Black, 0.50f);

                    // 3D cylindrical side wall below the raised circular cap (anchored at well level cy)
                    s.FillCircle(ccx, cy, cr, sideCol, SR2D.LineOp.Set, true);
                    // Main circular cap face shifted up by -lift * up
                    s.FillCircle(ccx, ccy, cr, face, SR2D.LineOp.Set, true);
                    // Upper 45% inset bevel highlight (matching the original Rocker cap highlight)
                    int hlBot = Math.Clamp((int)MathF.Round(ccy - cr + (2f * cr) * 0.45f), 1, h);
                    using (var hl = s.CreateView(new Rectangle(0, 0, sw, hlBot)))
                        hl.FillCircle(ccx, ccy, Math.Max(1f, cr - inset), hi, SR2D.LineOp.Set, true);
                    break;
                }
                case ToggleStyle.Rocker:
                {
                    // One-piece solid pivoting power rocker switch:
                    // A dark rectangular bezel housing holds ONE continuous solid rocker body that pivots around its
                    // central vertical crease. Pressing one side tilts that half down flat/flush into the housing while
                    // the opposite half angles sharply up proud above the bezel, exposing its sloped 3D front wedge wall
                    // and side cheek wall.
                    // t = 0: OFF (left 'O' side lowered into housing, right 'I' side raised/tilted up).
                    // t = 1: ON  (right 'I' side lowered into housing, left 'O' side raised/tilted up, body lit in Accent).
                    float lift = Math.Max(3.5f, (h - 4) * 0.22f);
                    float sink = Math.Max(1f, lift * 0.18f);
                    float bx = 1.5f, by = 1.5f + lift * 0.90f, bw = sw - 3f, bh = h - 3f - lift * 0.90f;
                    float rad = Math.Min(bw, bh) * 0.14f;
                    int bezelCol = Mix(BackColor, Black, 0.45f);
                    int cavityCol = Mix(BackColor, Black, 0.72f);
                    int bezelRim = _onlyButtonGlows ? rim : Lerp(rim, Mix(AccentRaw, ThumbColor, 0.35f), t);
                    FillRound(s, bx, by, bw, bh, rad, bezelCol);
                    DrawRound(s, bx, by, bw, bh, rad, bezelRim, rimW);
                    float pad = Math.Max(1.5f, rimW + 1f);
                    float wx = bx + pad, wy = by + pad, ww = bw - 2f * pad, wh = bh - 2f * pad;
                    FillRound(s, wx, wy, ww, wh, Math.Max(0f, rad - pad), cavityCol);

                    float tiltT = down ? (t + (0.5f - t) * 0.22f) : t;
                    float leftUp = tiltT, rightUp = 1f - tiltT;
                    float rx0 = wx + 1f, rx1 = wx + ww - 1f, rcx = (rx0 + rx1) * 0.5f;
                    float baseTop = wy + 0.5f, baseBot = wy + wh - 0.5f;
                    float pivLift = lift * 0.12f;
                    float bevelL = lift * 0.28f * leftUp, bevelR = lift * 0.28f * rightUp;
                    float topLx = rx0 + bevelL, botLx = rx0 + bevelL * 0.4f;
                    float topRx = rx1 - bevelR, botRx = rx1 - bevelR * 0.4f;

                    float topL = baseTop - lift * leftUp + sink * (1f - leftUp);
                    float botL = baseBot - lift * leftUp;
                    float topM = baseTop - pivLift;
                    float botM = baseBot - pivLift;
                    float topR = baseTop - lift * rightUp + sink * (1f - rightUp);
                    float botR = baseBot - lift * rightUp;

                    int offBody = Mix(BackColor, ThumbColor, hot ? 0.40f : 0.33f);
                    int bodyCol = Lerp(offBody, Accent, t);
                    int skirtCol = Mix(bodyCol, Black, 0.46f);
                    int cheekCol = Mix(bodyCol, Black, 0.34f);
                    int skirtEdge = Mix(bodyCol, Black, 0.25f);

                    // 1) Outer side cheek wall on the raised end (sloping down into the bezel corner)
                    if (leftUp > 0.03f)
                    {
                        Span<PointF> lCheek = stackalloc PointF[4];
                        lCheek[0] = new PointF(rx0, baseTop);
                        lCheek[1] = new PointF(topLx, topL);
                        lCheek[2] = new PointF(botLx, botL);
                        lCheek[3] = new PointF(rx0, baseBot);
                        s.FillPolygon(lCheek, cheekCol, SR2D.LineOp.Set, true);
                        s.DrawPolygon(lCheek, skirtEdge, 1f, true);
                    }
                    if (rightUp > 0.03f)
                    {
                        Span<PointF> rCheek = stackalloc PointF[4];
                        rCheek[0] = new PointF(topRx, topR);
                        rCheek[1] = new PointF(rx1, baseTop);
                        rCheek[2] = new PointF(rx1, baseBot);
                        rCheek[3] = new PointF(botRx, botR);
                        s.FillPolygon(rCheek, cheekCol, SR2D.LineOp.Set, true);
                        s.DrawPolygon(rCheek, skirtEdge, 1f, true);
                    }

                    // 2) 3D front skirt wedge visible underneath the raised wing(s)
                    Span<PointF> skirt = stackalloc PointF[6];
                    skirt[0] = new PointF(botLx, botL);
                    skirt[1] = new PointF(rcx, botM);
                    skirt[2] = new PointF(botRx, botR);
                    skirt[3] = new PointF(rx1, baseBot);
                    skirt[4] = new PointF(rcx, baseBot);
                    skirt[5] = new PointF(rx0, baseBot);
                    s.FillPolygon(skirt, skirtCol, SR2D.LineOp.Set, true);
                    s.DrawPolygon(skirt, skirtEdge, 1f, true);

                    // 3) Left and right sloped planes meeting at the central crease (rcx)
                    int leftFace = Mix(bodyCol, leftUp >= rightUp ? White : Black, leftUp >= rightUp ? 0.16f * leftUp : 0.20f * (1f - leftUp));
                    int rightFace = Mix(bodyCol, rightUp >= leftUp ? White : Black, rightUp >= leftUp ? 0.16f * rightUp : 0.20f * (1f - rightUp));

                    Span<PointF> leftQuad = stackalloc PointF[4];
                    leftQuad[0] = new PointF(topLx, topL);
                    leftQuad[1] = new PointF(rcx, topM);
                    leftQuad[2] = new PointF(rcx, botM);
                    leftQuad[3] = new PointF(botLx, botL);
                    s.FillPolygon(leftQuad, leftFace, SR2D.LineOp.Set, true);

                    Span<PointF> rightQuad = stackalloc PointF[4];
                    rightQuad[0] = new PointF(rcx, topM);
                    rightQuad[1] = new PointF(topRx, topR);
                    rightQuad[2] = new PointF(botRx, botR);
                    rightQuad[3] = new PointF(rcx, botM);
                    s.FillPolygon(rightQuad, rightFace, SR2D.LineOp.Set, true);

                    // Upper specular sheen across both sloped planes
                    float midLx = topLx + (botLx - topLx) * 0.42f, midRx = topRx + (botRx - topRx) * 0.42f;
                    float midL = topL + (botL - topL) * 0.42f, midM = topM + (botM - topM) * 0.42f, midR = topR + (botR - topR) * 0.42f;
                    Span<PointF> sheenL = stackalloc PointF[4];
                    sheenL[0] = new PointF(topLx + 1f, topL + 1f);
                    sheenL[1] = new PointF(rcx, topM + 1f);
                    sheenL[2] = new PointF(rcx, midM);
                    sheenL[3] = new PointF(midLx + 1f, midL);
                    s.FillPolygon(sheenL, Mix(leftFace, White, 0.12f + 0.10f * leftUp), SR2D.LineOp.Set, true);

                    Span<PointF> sheenR = stackalloc PointF[4];
                    sheenR[0] = new PointF(rcx, topM + 1f);
                    sheenR[1] = new PointF(topRx - 1f, topR + 1f);
                    sheenR[2] = new PointF(midRx - 1f, midR);
                    sheenR[3] = new PointF(rcx, midM);
                    s.FillPolygon(sheenR, Mix(rightFace, White, 0.12f + 0.10f * rightUp), SR2D.LineOp.Set, true);

                    // Continuous outer rim of the one-piece rocker cap + central pivot crease
                    int capEdgeCol = _onlyButtonGlows ? skirtEdge : Mix(bodyCol, White, 0.30f);
                    Span<PointF> capOutline = stackalloc PointF[6];
                    capOutline[0] = new PointF(topLx, topL);
                    capOutline[1] = new PointF(rcx, topM);
                    capOutline[2] = new PointF(topRx, topR);
                    capOutline[3] = new PointF(botRx, botR);
                    capOutline[4] = new PointF(rcx, botM);
                    capOutline[5] = new PointF(botLx, botL);
                    s.DrawPolygon(capOutline, capEdgeCol, 1.2f, true);
                    int creaseCol = _onlyButtonGlows ? Mix(bodyCol, Black, 0.18f) : Mix(bodyCol, White, 0.38f);
                    s.DrawWideLine(rcx, topM + 1f, rcx, botM - 1f, creaseCol, Math.Max(1f, rimW * 0.9f), true);

                    // Wrapped and centred labels on each half of the top cap
                    int ts = AutoTextScale(h * 2);
                    int halfCapW = Math.Max(4, (int)MathF.Round((rx1 - rx0) * 0.5f) - 6);
                    int halfCapH = Math.Max(4, (int)MathF.Round(wh - 2f));
                    int offLblCol = Lerp(unchecked((int)0xFFFFFFFF), Mix(BackColor, ForeColor, 0.85f), leftUp);
                    int onLblCol  = Lerp(unchecked((int)0xFFFFFFFF), Mix(BackColor, ForeColor, 0.85f), rightUp);
                    float leftCx  = (topLx + botLx + 2f * rcx) * 0.25f;
                    float rightCx = (topRx + botRx + 2f * rcx) * 0.25f;
                    float leftCy  = ((topL + botL) + (topM + botM)) * 0.25f;
                    float rightCy = ((topM + botM) + (topR + botR)) * 0.25f;
                    DrawWrappedCentered(s, leftCx, leftCy, halfCapW, halfCapH, _offText, offLblCol, ts);
                    DrawWrappedCentered(s, rightCx, rightCy, halfCapW, halfCapH, _onText, onLblCol, ts);
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
            if (!string.IsNullOrEmpty(Text) && w - sw > 6)
            {
                int ts = AutoTextScale(h * 3), avail = Math.Max(4, w - sw - 6);
                int sc = FitScale(Text, avail, ts);
                T.Draw(s, sw + 6, h / 2, FitText(Text, avail, sc), Fore, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            }
        }
    }

    /// <summary>
    /// Radio button in the knob's style: a small round body with a light rim, the accent dot when selected, caption to the
    /// right. Radios with the same parent and the same <see cref="GroupName"/> are exclusive (like WinForms, the parent is
    /// the group; the name lets several groups share a panel). With <see cref="AutoSize"/> on (the default), the control's
    /// width fits its circular button graphic plus <see cref="Control.Text"/> (shrinking to the graphic alone when empty).
    /// </summary>
    [ToolboxBitmap(typeof(SpriteRadio), "SpriteRadio.bmp")]
    [DefaultEvent("CheckedChanged"), DefaultProperty("Checked"), DefaultBindingProperty("Checked")]
    public sealed class SpriteRadio : SpriteClickable
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.RadioButton;
        bool _checked, _autoSize = true, _fitting; string _group = "";

        public SpriteRadio()
        {
            TabStop = false;
            base.AutoSize = true;
            Size = new Size(24, 24);
            Fit();
        }

        [Category("Layout"), DefaultValue(true), Browsable(true), EditorBrowsable(EditorBrowsableState.Always), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible), Description("Size the control's width to its radio graphic and caption (when Text is empty, bounds shrink to the radio graphic only).")]
        public override bool AutoSize { get => _autoSize; set { if (_autoSize == value) return; _autoSize = value; base.AutoSize = value; Fit(); Redraw(); } }

        public override Size GetPreferredSize(Size proposed) => MeasurePreferred(Height > 0 ? Height : 24);
        Size MeasurePreferred(int h)
        {
            h = Math.Max(16, h);
            int sc = AutoTextScale(h * 3);
            int minH = TextLineHeight(sc) + Padding.Vertical + 4;
            if (h < minH) h = minH;
            int w = string.IsNullOrEmpty(Text) ? h + Padding.Horizontal : h + 6 + T.Measure(Text, sc).Width + 3 + Padding.Horizontal;
            return new Size(Math.Max(16, w), h);
        }
        void Fit()
        {
            if (!_autoSize || _fitting) return;
            var want = MeasurePreferred(Height > 0 ? Height : 24);
            if (Size != want) { _fitting = true; try { Size = want; } finally { _fitting = false; } }
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Redraw(); }
        protected override void OnTextScaleChanged() { Fit(); base.OnTextScaleChanged(); }
        protected override void OnTextFontChanged() { Fit(); Redraw(); }
        protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); Fit(); Redraw(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); Redraw(); }

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
            if (!string.IsNullOrEmpty(Text) && w - h > 6)
            {
                int ts = AutoTextScale(h * 3), avail = Math.Max(4, w - h - 6);
                int sc = FitScale(Text, avail, ts);
                T.Draw(s, h + 6, h / 2, FitText(Text, avail, sc), Fore, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            }
        }
    }

    /// <summary>How unpressed (raised) and pressed (depressed) button caps are coloured in a <see cref="SpriteRadioGrid"/>.</summary>
    public enum ColoringStyle
    {
        /// <summary>Unpressed (raised) buttons are neutral grey (<see cref="SpriteControlBase.ThumbColor"/>); the pressed (depressed) button lights up in its per-button accent colour.</summary>
        UnpressedGray,
        /// <summary>Unpressed (raised) buttons use a darker shade of their per-button accent colour; the pressed (depressed) button lights up in its full per-button accent colour.</summary>
        UnpressedDarkAccent,
        /// <summary>Override mode: all unpressed (raised) buttons use <see cref="SpriteRadioGrid.UnpressedColor"/> and the pressed (depressed) button uses <see cref="SpriteRadioGrid.PressedColor"/>.</summary>
        Override,
    }

    /// <summary>
    /// Multi-button interlocking 3D radio switch grid (<see cref="Rows"/> x <see cref="Columns"/> sculpted push-button caps in recessed wells):
    /// only one button can be depressed (<see cref="SelectedIndex"/>) at a time, and pressing any button automatically unpresses the rest.
    /// Raised caps stand proud above the well rim with a 3D bottom wall and upper 45% bevel highlight (matching the original rocker look),
    /// while the depressed cap sinks into the well under the top rim shadow. Colouring is controlled by <see cref="ColoringStyle"/>.
    /// </summary>
    [ToolboxBitmap(typeof(SpriteRadioGrid), "SpriteRadio.bmp")]
    [DefaultEvent("SelectedIndexChanged"), DefaultProperty("SelectedIndex"), DefaultBindingProperty("SelectedIndex")]
    public sealed class SpriteRadioGrid : SpriteClickable
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Grouping;
        int _rows = 1, _cols = 2, _selected, _prevSelected, _hoverCell = -1, _pressCell = -1;
        ColoringStyle _coloringStyle = ColoringStyle.UnpressedGray;
        Color _unpressedColor = Color.FromArgb(0x48, 0x52, 0x60);
        Color _pressedColor = Color.FromArgb(0xFF, 0x90, 0x30);
        bool _fromPress;
        readonly List<string> _items = new();
        int[] _palette = Array.Empty<int>();

        public SpriteRadioGrid()
        {
            Size = new Size(160, 40);
            SnapAnim(1f);
        }

        [Category("Action"), Description("Raised when SelectedIndex changes (user click, keyboard or code).")]
        public event EventHandler? SelectedIndexChanged;

        [Category("Appearance"), DefaultValue(1), Description("Number of button rows in the grid (1..32).")]
        public int Rows
        {
            get => _rows;
            set { int v = Math.Clamp(value, 1, 32); if (_rows == v) return; _rows = v; ClampSelection(); Redraw(); }
        }

        [Category("Appearance"), DefaultValue(2), Description("Number of button columns in the grid (1..32).")]
        public int Columns
        {
            get => _cols;
            set { int v = Math.Clamp(value, 1, 32); if (_cols == v) return; _cols = v; ClampSelection(); Redraw(); }
        }

        /// <summary>Total number of buttons in the grid (<see cref="Rows"/> * <see cref="Columns"/>).</summary>
        [Browsable(false)] public int ButtonCount => _rows * _cols;

        [Category("Behavior"), DefaultValue(0), Description("Index of the currently depressed button (0 .. Rows * Columns - 1). Pressing one button automatically unpresses all others.")]
        public int SelectedIndex
        {
            get => _selected;
            set
            {
                int count = ButtonCount;
                int v = count > 0 ? Math.Clamp(value, 0, count - 1) : 0;
                if (_selected == v) return;
                _prevSelected = _selected;
                _selected = v;
                bool pressedStart = _fromPress;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                SnapAnim(pressedStart ? 0.32f : 0f);
                AnimateTo(1f, pressedStart ? 110 : 140);
            }
        }

        /// <summary>Row index (0 .. Rows - 1) of the currently depressed button.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedRow => _cols > 0 ? _selected / _cols : 0;

        /// <summary>Column index (0 .. Columns - 1) of the currently depressed button.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedColumn => _cols > 0 ? _selected % _cols : 0;

        [Category("Appearance"), DefaultValue(ColoringStyle.UnpressedGray), Description("How unpressed (raised) and pressed (depressed) caps are coloured: UnpressedGray (grey raised, per-button accent pressed), UnpressedDarkAccent (darker per-button accent raised, full accent pressed), or Override (UnpressedColor / PressedColor).")]
        public ColoringStyle ColoringStyle
        {
            get => _coloringStyle;
            set { if (_coloringStyle == value) return; _coloringStyle = value; Redraw(); }
        }

        [Category("Appearance"), DefaultValue(typeof(Color), "72, 82, 96"), Description("ColoringStyle.Override: colour of all unpressed (raised) button caps.")]
        public Color UnpressedColor
        {
            get => _unpressedColor;
            set { if (_unpressedColor == value) return; _unpressedColor = value; Redraw(); }
        }

        [Category("Appearance"), DefaultValue(typeof(Color), "255, 144, 48"), Description("ColoringStyle.Override: colour of the pressed (depressed) button cap.")]
        public Color PressedColor
        {
            get => _pressedColor;
            set { if (_pressedColor == value) return; _pressedColor = value; Redraw(); }
        }

        // Hidden compatibility shims so older code/tests referencing the boolean flags still compile cleanly
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool DepressedButtonColored
        {
            get => _coloringStyle != ColoringStyle.Override || _pressedColor != Color.Empty;
            set { Redraw(); }
        }
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool DepressedButtonGlows
        {
            get => true;
            set { Redraw(); }
        }
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool RaisedButtonsColored
        {
            get => _coloringStyle == ColoringStyle.UnpressedDarkAccent;
            set { _coloringStyle = value ? ColoringStyle.UnpressedDarkAccent : ColoringStyle.UnpressedGray; Redraw(); }
        }

        /// <summary>Per-button accent colours (cycled when shorter than <see cref="ButtonCount"/>; empty = use <see cref="SpriteControlBase.AccentColor"/> for every button).</summary>
        [Browsable(true), Category("Appearance"), Description("Per-button accent colours (cycled when shorter than the button count; empty = use AccentColor for every button).")]
        [SuppressMessage("Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "WinForms Designer array editor property.")]
        public Color[] AccentColors
        {
            get => Array.ConvertAll(_palette, Color.FromArgb);
            set => SetPalette(value == null ? Array.Empty<int>() : Array.ConvertAll(value, c => c.ToArgb()));
        }
        public bool ShouldSerializeAccentColors() => _palette.Length > 0;
        public void ResetAccentColors() => SetPalette(Array.Empty<int>());

        /// <summary>Per-button accent colours as comma-separated AARRGGBB / #RRGGBB hex values or colour names (edits with the StripePaletteEditor dialog in the designer).</summary>
        [Browsable(true), Category("Appearance"), DefaultValue(""),
         Description("Per-button accent colours as AARRGGBB / #RRGGBB values or colour names separated by commas (empty = use AccentColor)."),
         Editor("Sr2d64CSport.StripePaletteEditor", "System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
        public string AccentColorList
        {
            get => string.Join(",", Array.ConvertAll(_palette, c => c.ToString("X8", CultureInfo.InvariantCulture)));
            set => SetPalette(ParseColorList(value));
        }

        /// <summary>Button labels (editable in the WinForms Designer under Data -> Items, or in code). When empty, buttons display 1..N.</summary>
        [Browsable(true), Category("Data"), Description("Labels drawn on the button caps (row by row, left to right). When empty, buttons display 1..N.")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("System.Windows.Forms.Design.StringCollectionEditor, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
        [MergableProperty(false), Localizable(true)]
        [SuppressMessage("Design", "CA1002:DoNotExposeGenericLists", Justification = "Standard BCL List<string> required by out-of-process WinForms Designer.")]
        [SuppressMessage("Usage", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Setter required by out-of-process WinForms Designer.")]
        public List<string> Items
        {
            get => _items;
            set
            {
                if (!ReferenceEquals(_items, value))
                {
                    _items.Clear();
                    if (value != null) _items.AddRange(value);
                }
                Redraw();
            }
        }
        bool ShouldSerializeItems() => _items.Count > 0;
        void ResetItems() { _items.Clear(); Redraw(); }

        /// <summary>Button labels as a string array (editable one-per-line in the Visual Studio designer under Data).</summary>
        [Browsable(true), Category("Data"), Description("Labels drawn on the button caps as a string array (one per line in the designer).")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [SuppressMessage("Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "WinForms Designer StringArrayEditor property.")]
        public string[] ItemArray
        {
            get => _items.ToArray();
            set => SetItems(value ?? Array.Empty<string>(), _selected);
        }

        public void SetItems(IEnumerable<string> items, int selectedIndex = 0)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            int count = ButtonCount;
            _selected = _prevSelected = count > 0 ? Math.Clamp(selectedIndex, 0, count - 1) : 0;
            SnapAnim(1f);
            Redraw();
        }

        void SetPalette(int[] cols)
        {
            if (cols.Length == _palette.Length)
            {
                bool same = true;
                for (int i = 0; i < cols.Length; i++) if (cols[i] != _palette[i]) { same = false; break; }
                if (same) return;
            }
            _palette = cols;
            Redraw();
        }

        static int[] ParseColorList(string? list)
        {
            if (string.IsNullOrWhiteSpace(list)) return Array.Empty<int>();
            var parts = list.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var cols = new int[parts.Length]; int n = 0;
            foreach (var raw in parts)
            {
                var s = raw.Trim();
                if (s.StartsWith('#')) s = s[1..];
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
                if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
                    cols[n++] = s.Length <= 6 ? hex | unchecked((int)0xFF000000) : hex;
                else
                {
                    var named = Color.FromName(s);
                    if (named.IsKnownColor) cols[n++] = named.ToArgb();
                }
            }
            Array.Resize(ref cols, n);
            return cols;
        }

        void ClampSelection()
        {
            int count = ButtonCount;
            if (_selected >= count) _selected = Math.Max(0, count - 1);
            if (_prevSelected >= count) _prevSelected = _selected;
        }

        int HitCell(int x, int y)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (x < 0 || y < 0 || x >= w || y >= h || _rows <= 0 || _cols <= 0) return -1;
            int c = Math.Clamp((int)((long)x * _cols / Math.Max(1, w)), 0, _cols - 1);
            int r = Math.Clamp((int)((long)y * _rows / Math.Max(1, h)), 0, _rows - 1);
            return r * _cols + c;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SnapAnim(1f); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int cell = ClientRectangle.Contains(e.Location) ? HitCell(e.X, e.Y) : -1;
            if (cell != _hoverCell) { _hoverCell = cell; RedrawNow(); }
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverCell = -1;
            base.OnMouseLeave(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _pressCell = HitCell(e.X, e.Y);
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int target = e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location) ? HitCell(e.X, e.Y) : -1;
            int pressed = _pressCell; _pressCell = -1;
            if (target >= 0 && target == pressed)
            {
                _fromPress = true;
                try { SelectedIndex = target; }
                finally { _fromPress = false; }
            }
            base.OnMouseUp(e);
        }
        protected override bool IsInputKey(Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            return k is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int r = SelectedRow, c = SelectedColumn;
            switch (e.KeyCode)
            {
                case Keys.Left:  if (c > 0) SelectedIndex = r * _cols + (c - 1); else if (r > 0) SelectedIndex = (r - 1) * _cols + (_cols - 1); e.Handled = true; return;
                case Keys.Right: if (c + 1 < _cols) SelectedIndex = r * _cols + (c + 1); else if (r + 1 < _rows) SelectedIndex = (r + 1) * _cols; e.Handled = true; return;
                case Keys.Up:    if (r > 0) SelectedIndex = (r - 1) * _cols + c; e.Handled = true; return;
                case Keys.Down:  if (r + 1 < _rows) SelectedIndex = (r + 1) * _cols + c; e.Handled = true; return;
            }
            base.OnKeyDown(e);
        }

        int GetButtonAccent(int idx)
        {
            int raw = _palette.Length > 0 ? (_palette[idx % _palette.Length] | unchecked((int)0xFF000000)) : AccentRaw.ToArgb();
            return Enabled ? raw : Mix(raw, BackColor, 0.5f);
        }

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w < 6 || h < 6) return;
            bool hot = IsHot && Enabled;
            float anim = IsAnimating || Animated ? AnimT : 1f;
            float x = 1.5f, ww = w - 3f;
            float cellW = ww / _cols;
            float rowGap = _rows > 1 ? 3f : 0f;
            float rowSliceH = (h - 3f - (_rows - 1) * rowGap) / _rows;
            if (rowSliceH < 6f) { rowGap = 0f; rowSliceH = (h - 3f) / _rows; }
            float lift = Math.Max(2.5f, Math.Min(rowSliceH - 4f, 48f) * 0.20f);
            float wellH = rowSliceH - lift;
            if (wellH < 4f) { wellH = rowSliceH; lift = 0f; }
            float rad = Math.Min(cellW, wellH) * 0.18f;
            float rimW = Math.Max(1f, Math.Min(cellW, rowSliceH) * 0.05f);
            float inset = Math.Max(1f, rimW);

            int wellCol = Mix(BackColor, Black, 0.55f);
            int seamCol = Mix(BackColor, Black, 0.72f);
            int rimCol = Mix(BackColor, ThumbColor, hot ? 0.62f : 0.50f);
            int shadow = Mix(BackColor, Black, 0.65f);
            int ts = AutoTextScale((int)MathF.Round(rowSliceH * 2f));

            int unpOverride = Enabled ? (_unpressedColor.ToArgb() | unchecked((int)0xFF000000)) : Mix(_unpressedColor.ToArgb(), BackColor, 0.5f);
            int prsOverride = Enabled ? (_pressedColor.ToArgb() | unchecked((int)0xFF000000)) : Mix(_pressedColor.ToArgb(), BackColor, 0.5f);

            for (int r = 0; r < _rows; r++)
            {
                float rowTop = 1.5f + r * (rowSliceH + rowGap);
                float wy = rowTop + lift;

                // Draw the row's recessed well and its rim first (so raised caps stand proud above the top well rim!)
                FillRound(s, x, wy, ww, wellH, rad, wellCol);
                DrawRound(s, x, wy, ww, wellH, rad, rimCol, rimW);
                for (int c = 1; c < _cols; c++)
                    s.DrawWideLine(x + c * cellW, wy + 1f, x + c * cellW, wy + wellH - 1f, seamCol, Math.Max(1f, rimW), true);

                float capX = x + inset, capW = ww - 2f * inset, capH = wellH - 2f * inset, capRad = Math.Max(0f, rad - inset);
                int rowY = Math.Max(0, (int)MathF.Floor(rowTop));
                int rowH = Math.Min(h - rowY, (int)MathF.Ceiling(rowSliceH + 1f));
                if (rowH <= 0) continue;

                for (int c = 0; c < _cols; c++)
                {
                    int idx = r * _cols + c;
                    bool isSel = idx == _selected;
                    // up: 1 = fully raised (standing proud above the well rim), 0 = fully depressed (sunk into the well)
                    float up = isSel ? (1f - anim) : (idx == _prevSelected && _prevSelected != _selected ? anim : 1f);
                    if (_prevSelected == _selected) up = isSel ? 0f : 1f;

                    bool cellPressed = IsPressed && _pressCell == idx && (_hoverCell == idx || _hoverCell == -1);
                    float colorUp;
                    if (cellPressed && !isSel)
                    {
                        // Holding mouse down on a raised cap pushes it partway down while keeping its solid raised colour
                        up = Math.Min(up, 0.68f);
                        colorUp = 1f;
                    }
                    else
                    {
                        colorUp = up;
                    }

                    int btnAccent = GetButtonAccent(idx);
                    bool cellHot = hot && _hoverCell == idx;
                    int upFace, upHi, sideCol, downFace;
                    switch (_coloringStyle)
                    {
                        case ColoringStyle.UnpressedDarkAccent:
                            upFace = Mix(btnAccent, BackColor, cellHot ? 0.34f : 0.42f);
                            upHi = Mix(upFace, White, 0.25f);
                            sideCol = Mix(upFace, Black, 0.50f);
                            downFace = cellHot ? Mix(btnAccent, White, 0.12f) : btnAccent;
                            break;
                        case ColoringStyle.Override:
                            upFace = cellHot ? Mix(unpOverride, White, 0.12f) : unpOverride;
                            upHi = Mix(upFace, White, 0.25f);
                            sideCol = Mix(upFace, Black, 0.50f);
                            downFace = cellHot ? Mix(prsOverride, White, 0.12f) : prsOverride;
                            break;
                        default: // ColoringStyle.UnpressedGray
                            upFace = Mix(BackColor, ThumbColor, cellHot ? 0.44f : 0.38f);
                            upHi = Mix(BackColor, ThumbColor, cellHot ? 0.64f : 0.58f);
                            sideCol = Mix(upFace, Black, 0.48f);
                            downFace = cellHot ? Mix(btnAccent, White, 0.12f) : btnAccent;
                            break;
                    }

                    int face = Lerp(downFace, upFace, colorUp);
                    float dy = -lift * up;
                    float capY = wy + inset + dy;

                    int hx = Math.Max(0, (int)MathF.Round(x + c * cellW));
                    int hxNext = Math.Min(w, (int)MathF.Round(x + (c + 1) * cellW));
                    int hw = Math.Max(1, hxNext - hx);

                    using (var v = s.CreateView(new Rectangle(hx, rowY, hw, rowH)))
                    {
                        // 1) 3D vertical side wall below the raised cap, anchored at fixed wy + inset (inside the well!)
                        if (up > 0.02f)
                            FillRound(v, capX, wy + inset, capW, capH, capRad, sideCol);

                        // 2) Solid cap face at capY = wy + inset + dy
                        FillRound(v, capX, capY, capW, capH, capRad, face);

                        // 3) Sculpted bevel highlight on the upper 45% of a raised cap (moves with capY)
                        if (up > 0.02f)
                        {
                            int hlBot = Math.Clamp((int)MathF.Round(capY + capH * 0.45f), rowY + 1, rowY + rowH);
                            using var hl = v.CreateView(new Rectangle(hx, rowY, hw, hlBot - rowY));
                            FillRound(hl, capX + inset, capY + inset, capW - 2f * inset, capH - 2f * inset, Math.Max(0f, capRad - inset), Lerp(face, upHi, colorUp));
                        }

                        // 4) Top-edge shadow band under the well rim when sunk into the well (moves with capY, never cuts across a raised cap)
                        if (up < 0.45f && !(cellPressed && !isSel))
                        {
                            float shT = (0.45f - up) / 0.45f;
                            int shTop = Math.Clamp((int)MathF.Round(capY), rowY, rowY + rowH - 1);
                            int shH = Math.Clamp((int)MathF.Round(Math.Max(1f, capH * 0.22f)), 1, rowY + rowH - shTop);
                            using var sh = v.CreateView(new Rectangle(hx, shTop, hw, shH));
                            FillRound(sh, capX, capY, capW, capH, capRad, Lerp(face, Mix(face, shadow, 0.52f), shT));
                        }
                    }

                    // Crisp seam line between columns
                    if (c > 0)
                        s.DrawWideLine(x + c * cellW, Math.Max(rowTop, capY), x + c * cellW, wy + wellH - 1f, seamCol, Math.Max(1f, rimW * 0.85f), true);

                    // Wrapped and centred label inside the cap
                    string lbl = idx < _items.Count ? _items[idx] : (idx + 1).ToString(CultureInfo.InvariantCulture);
                    if (lbl.Length > 0)
                    {
                        int litLbl = Enabled ? unchecked((int)0xFFFFFFFF) : Fore;
                        int unlitLbl = Mix(BackColor, ForeColor, 0.88f);
                        int lc = Lerp(litLbl, unlitLbl, colorUp);
                        float textCx = x + (c + 0.5f) * cellW;
                        float textCy = capY + capH * 0.5f;
                        DrawWrappedCentered(s, textCx, textCy, Math.Max(4, hw - 4), Math.Max(4, (int)capH - 2), lbl, lc, ts);
                    }
                }
            }
        }
    }

    /// <summary>Shape of a <see cref="SpriteProgress"/>.</summary>
    public enum ProgressStyle
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
    [ToolboxBitmap(typeof(SpriteProgress), "SpriteProgress.bmp")]
    [DefaultEvent("Click"), DefaultProperty("Value"), DefaultBindingProperty("Value")]
    public sealed class SpriteProgress : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.ProgressBar;
        ProgressStyle _style = ProgressStyle.Horizontal; ControlLook _look = ControlLook.Classic;
        double _min, _max = 100, _value; bool _showPercent = true, _marquee; float _phase; int _segments;

        public SpriteProgress() { Size = new Size(220, 22); TabStop = false; Cursor = Cursors.Default; }

        [Category("Appearance"), DefaultValue(ProgressStyle.Horizontal), Description("Horizontal, vertical or ring.")]
        public ProgressStyle Style { get => _style; set { _style = value; Redraw(); } }
        [Category("Behavior"), DefaultValue(typeof(decimal), "0")] public decimal Minimum { get => (decimal)_min; set { _min = (double)value; if (_max < _min) _max = _min; Redraw(); } }
        [Category("Behavior"), DefaultValue(typeof(decimal), "100")] public decimal Maximum { get => (decimal)_max; set { _max = (double)value; if (_min > _max) _min = _max; Redraw(); } }
        [Category("Behavior"), DefaultValue(typeof(decimal), "0"), Description("Current position (clamped).")]
        public decimal Value { get => (decimal)_value; set { double v = Math.Clamp((double)value, _min, _max); if (v == _value) return; _value = v; if (IsHandleCreated) RedrawNow(); else Redraw(); } }
        [Category("Appearance"), DefaultValue(true), Description("Draw the percentage (or the caption when Text is set).")]
        public bool ShowPercent { get => _showPercent; set { _showPercent = value; Redraw(); } }
        [Category("Appearance"), DefaultValue(false), Description("Indeterminate: a segment travels along the track instead of the fill (advance with Tick / Phase).")]
        public bool Marquee { get => _marquee; set { _marquee = value; Redraw(); } }
        [Category("Appearance"), DefaultValue(0f), Description("Marquee position 0..1 (wraps).")]
        public float Phase { get => _phase; set { _phase = value - MathF.Floor(value); if (IsHandleCreated) RedrawNow(); else Redraw(); } }
        [Category("Appearance"), DefaultValue(0), Description("Number of LED-like segments (0 = continuous fill).")]
        public int Segments { get => _segments; set { _segments = Math.Max(0, value); Redraw(); } }
        [Category("Appearance"), DefaultValue(ControlLook.Classic), Description("Classic (shaded bar or ring), Hairline (framed bar with a keyline, thin ring with dial ticks) or Segmented (a row of lit cells; Segments sets how many).")]
        public ControlLook Look { get => _look; set { _look = value; Redraw(); } }

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
                if (_look != ControlLook.Classic) { PaintLookRing(s, w, h, f, cap); return; }
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
            if (_look != ControlLook.Classic) { PaintLookBar(s, w, h, hz, f, cap); return; }
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

        // a cell is lit inside the marquee window (a run of `window` of a turn that wraps), otherwise up to the value
        bool LitCell(double c, double f, double window) => _marquee ? ((c - _phase) % 1 + 1) % 1 <= window : c <= f;

        // the flat looks of the ring: Hairline = a thin track ring with dial ticks and a 2 px arc; Segmented = arc cells
        void PaintLookRing(Sprite s, int w, int h, double f, string cap)
        {
            bool hair = _look == ControlLook.Hairline;
            float r = Math.Min(w, h) / 2f - 1.5f, cx = w / 2f, cy = h / 2f;
            float tw = hair ? 2f : Math.Max(3f, r * 0.2f);
            float tr = hair ? r - 9f : r - tw / 2;                      // the stroke's centre; the hairline keeps room for the dial
            if (hair)
            {
                s.DrawCircle(cx, cy, tr, Track, 1f, true);
                LookDial(s, cx, cy, tr + 4f, 60, 2, 4, 5, Mix(BackColor, ThumbColor, 0.5f));
                if (_marquee) { LookArc(s, cx, cy, tr, tw, _phase, 0.25, Accent); if (_phase > 0.75) LookArc(s, cx, cy, tr, tw, 0, _phase - 0.75, Accent); }
                else LookArc(s, cx, cy, tr, tw, 0, f, Accent);
            }
            else
            {
                int n = _segments > 0 ? _segments : 24;
                for (int i = 0; i < n; i++)
                    LookArc(s, cx, cy, tr, tw, (double)i / n + 0.12 / n, 0.76 / n, LitCell((i + 0.5) / n, f, 0.25) ? Accent : Track);
            }
            int ts = AutoTextScale((int)(r * 2.2f));
            if (cap.Length > 0) { int sc = FitScale(cap, (int)(tr * 1.4f), ts); T.Draw(s, (int)cx, (int)cy, FitText(cap, (int)(tr * 1.4f), sc), Fore, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center); }
        }

        // the flat looks of the bar: Hairline = a keyline frame and a flat accent fill; Segmented = a row of cells (no frame)
        void PaintLookBar(Sprite s, int w, int h, bool hz, double f, string cap)
        {
            if (_look == ControlLook.Hairline)
            {
                int rim = Mix(BackColor, ThumbColor, 0.5f);
                LookKeyline(s, 0, 0, w, h, 0, rim, Argb(rim, 110));
                float ix = 4f, iy = 4f, iw = w - 8, ih = h - 8;         // inside the keyline
                void Fill(double from, double len)
                {
                    if (len <= 0) return;
                    if (hz) LookBar(s, ix + iw * (float)from, ix + iw * (float)(from + len), iy + ih / 2f, ih, true, Accent);
                    else LookBar(s, iy + ih * (float)(1 - from), iy + ih * (float)(1 - from - len), ix + iw / 2f, iw, false, Accent);
                }
                if (_marquee) { double len = 0.3, p = _phase * (1 + len) - len; Fill(Math.Max(0, p), Math.Min(len, Math.Min(p + len, 1) - Math.Max(0, p))); }
                else
                {
                    Fill(0, f);
                    if (f > 0.01 && f < 0.99)                        // the needle: a light 2 px mark at the end of the fill
                    {
                        int needle = Mix(Accent, White, 0.55f);
                        if (hz) s.FillRect((int)MathF.Round(ix + iw * (float)f) - 1, (int)iy, 2, (int)ih, needle);
                        else s.FillRect((int)ix, (int)MathF.Round(iy + ih * (float)(1 - f)) - 1, (int)iw, 2, needle);
                    }
                }
            }
            else
            {
                float across = hz ? h : w, extent = hz ? w : h;
                float th = Math.Min(across - 2, Math.Max(4f, across * 0.55f));
                int n = _segments > 0 ? _segments : Math.Max(4, (int)((extent - 2) / (th * 1.1f)));
                float qa = hz ? 1f : h - 1f, qb = hz ? w - 1f : 1f;
                LookCells(s, qa, qb, hz ? h / 2f : w / 2f, th, hz, n, 2f, c => LitCell(c, f, 0.3) ? Accent : Track);
            }
            if (cap.Length > 0)
            {
                int ts = AutoTextScale(hz ? h * 3 : w * 3), avail = hz ? w - 8 : h - 8;
                int sc = FitScale(cap, avail, ts);
                T.Draw(s, w / 2, h / 2, FitText(cap, avail, sc), Fore, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
            }
        }
    }
}
