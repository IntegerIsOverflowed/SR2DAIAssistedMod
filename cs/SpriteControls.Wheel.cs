using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>
    /// Thumb wheel / drum picker: a cylinder seen from the side with a ridge per <see cref="SpriteRangeControl.Step"/> and the
    /// neighbouring values printed on it; the value under the centre line is the current one. Drag along the drum and the
    /// surface follows the pointer (1 step per <see cref="PixelsPerStep"/> px, Shift = 1/10), the mouse wheel turns it one
    /// step, arrows / Page / Home / End as on every range control. <see cref="WrapMouse"/> teleports the pointer to the
    /// opposite edge of the screen when it leaves along the drag axis, so a long turn never stops at the border.
    /// <see cref="WrapAround"/> makes the range circular (359 -> 0 -> 1, for angles / hours).
    /// <para>Looks (<see cref="Style"/>): <see cref="WheelStyle.Detailed"/> (default) is a machined volume wheel - a
    /// knurled rubber band with a grip groove every step, a metal hub on each side, a lens highlight and a read-out window
    /// with a hairline; <see cref="WheelStyle.Flat"/> is the original plain shaded drum.</para>
    /// <para>Typing a value (<see cref="Edit"/>): an SR2D number field (<see cref="SpriteNumeric"/> without buttons) appears
    /// <see cref="WheelEdit.Beside"/> the drum permanently, or over the drum on <see cref="WheelEdit.DoubleClick"/> /
    /// a <see cref="WheelEdit.Click"/> without movement; Enter or clicking elsewhere applies, Escape hides it. With the
    /// pop-up modes the double-click reset of the base class moves to Ctrl + double click.</para>
    /// </summary>
    internal sealed class SpriteWheel : SpriteRangeControl
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Slider;
        Orientation _orient = Orientation.Vertical;
        bool _wrapMouse = true, _wrapAround, _labels = true, _reversed;
        int _pitch, _pixelsPerStep, _editWidth;
        WheelEdit _edit; WheelStyle _style = WheelStyle.Detailed;
#pragma warning disable CA2213 // _field is added to Controls: WinForms disposes child controls with the parent
        SpriteNumeric? _field;
#pragma warning restore CA2213
        bool _fieldSync, _popupOpen;

        public SpriteWheel() { Size = new Size(64, 128); }

        [Category("Behavior"), DefaultValue(Orientation.Vertical), Description("Vertical: the drum turns about a horizontal axis, values run top to bottom (drag up = larger). Horizontal: values run left to right (drag left = larger).")]
        public Orientation Orientation { get => _orient; set { _orient = value; LayoutField(); Redraw(); } }

        [Category("Behavior"), DefaultValue(true), Description("While dragging, a pointer that reaches the edge of the screen along the drag axis is moved to the opposite edge, so the wheel can be turned for ever in one motion.")]
        public bool WrapMouse { get => _wrapMouse; set => _wrapMouse = value; }

        [Category("Behavior"), DefaultValue(false), Description("Circular range: past Maximum comes Minimum again (Maximum itself is the same position as Minimum - use 0..360 for degrees, 0..24 for hours).")]
        public bool WrapAround { get => _wrapAround; set { _wrapAround = value; SetValue(Value, false); Redraw();   /* re-clamp / re-wrap */ } }
        protected override bool Wraps => _wrapAround;

        [Category("Appearance"), DefaultValue(true), Description("Print the values on the drum (one label per step, or per few steps when they would overlap).")]
        public bool Labels { get => _labels; set { _labels = value; Redraw(); } }

        [Category("Behavior"), DefaultValue(false), Description("Flip the direction the values run along the drum (and with it the drag sense).")]
        public bool Reversed { get => _reversed; set { _reversed = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(0), Description("Distance in pixels between two steps at the centre of the drum (0 = automatic from the text size).")]
        public int Pitch { get => _pitch; set { _pitch = Math.Max(0, value); Redraw(); } }

        [Category("Behavior"), DefaultValue(0), Description("Mouse travel in pixels for one Step (0 = the drum pitch, so the surface sticks to the pointer). Smaller = faster.")]
        public int PixelsPerStep { get => _pixelsPerStep; set => _pixelsPerStep = Math.Max(0, value); }

        [Category("Appearance"), DefaultValue(WheelStyle.Detailed), Description("Detailed = knurled rubber band, metal hubs, lens highlight, read-out window with hairline (a volume wheel). Flat = the plain shaded drum.")]
        public WheelStyle Style { get => _style; set { _style = value; Redraw(); } }

        [Category("Behavior"), DefaultValue(WheelEdit.None), Description("How a value is typed: None; Beside = a number field next to the drum; DoubleClick / Click = a field pops up over the drum (Enter / click away applies, Escape hides; a Click is a press without movement). With DoubleClick / Click the reset to ResetValue moves to Ctrl + double click.")]
        public WheelEdit Edit
        {
            get => _edit;
            set
            {
                if (_edit == value) return;
                _edit = value; _popupOpen = false;
                if (value == WheelEdit.Beside) { EnsureField(); _field!.Visible = true; RefreshField(); }
                else if (_field != null) _field.Visible = false;
                LayoutField(); Redraw();
            }
        }

        /// <summary>Old name of <see cref="Edit"/> = <see cref="WheelEdit.Beside"/> (kept for existing code / designer files).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Editable { get => _edit == WheelEdit.Beside; set => Edit = value ? WheelEdit.Beside : (_edit == WheelEdit.Beside ? WheelEdit.None : _edit); }

        [Category("Appearance"), DefaultValue(0), Description("Width of the Beside field (0 = automatic: about half of a vertical wheel, a third of a horizontal one).")]
        public int EditWidth { get => _editWidth; set { _editWidth = Math.Max(0, value); LayoutField(); Redraw(); } }

        /// <summary>The number field (null until <see cref="Edit"/> was set); an SR2D <see cref="SpriteNumeric"/> without buttons.</summary>
        [Browsable(false)] public SpriteNumeric? EditField => _field;
        /// <summary>Old name of <see cref="EditField"/> (it was a WinForms TextBox before 2026-09-24; the SR2D field has the same Text / SelectAll surface).</summary>
        [Browsable(false)] public SpriteNumeric? EditBoxControl => _field;
        /// <summary>True while the pop-up field (Click / DoubleClick modes) is showing.</summary>
        [Browsable(false)] public bool IsEditing => _field != null && _field.Visible && _edit != WheelEdit.Beside;

        // ------------------------------------------------------------------ geometry
        bool Hz => _orient == Orientation.Horizontal;
        int TextH(int ts) => T.GlyphHeight(ts);
        string Fmt => "F" + Decimals;
        /// <summary>Extent of a label along the axis: the widest of Minimum / Maximum (horizontal) or the text height (vertical).</summary>
        int LabelLen(int ts) => Hz ? Math.Max(T.Measure(Minimum.ToString(Fmt, System.Globalization.CultureInfo.InvariantCulture), ts).Width, T.Measure(Maximum.ToString(Fmt, System.Globalization.CultureInfo.InvariantCulture), ts).Width) : TextH(ts);
        int PitchPx(int ts) => _pitch > 0 ? _pitch : (Hz ? LabelLen(ts) + 10 * ts : TextH(ts) + 4 * ts + 4);
        int EditW => _edit == WheelEdit.Beside ? (_editWidth > 0 ? _editWidth : Math.Max(36, Hz ? ClientSize.Width * 3 / 10 : ClientSize.Width * 9 / 20)) : 0;
        /// <summary>Thickness of a metal hub across the drum ends (Detailed style), 0 for Flat.</summary>
        int HubPx(int len) => _style == WheelStyle.Detailed ? Math.Clamp(len / 14, 3, 10) : 0;

        /// <summary>Drum rectangle, caption height and text scale.</summary>
        (Rectangle drum, int capH, int ts) Geometry()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            int ts = AutoTextScale(Hz ? h * 2 : w * 3 / 2);
            int capH = string.IsNullOrEmpty(Text) ? 0 : LineHeight(ts);
            int ew = EditW, gap = ew > 0 ? 4 : 0;
            var drum = new Rectangle(1 + ew + gap, capH + 1, Math.Max(4, w - 2 - ew - gap), Math.Max(4, h - capH - 2));
            return (drum, capH, ts);
        }

        // ------------------------------------------------------------------ drag (relative, along the axis; the drum surface follows the pointer)
        Point _last, _pressAt; double _acc; bool _moved;
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (IsEditing) { CloseField(true); }
            if (IsDragButton(e.Button)) { _last = _pressAt = e.Location; _acc = Shown; _moved = false; }
            base.OnMouseDown(e);
        }
        protected override double ValueFromPointer(Point p, Point start, double startValue, bool fine)
        {
            int d = Hz ? p.X - _last.X : p.Y - _last.Y;
            _last = p;
            if (d == 0) return _acc;
            if (Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) > 2) _moved = true;
            var (drum, _, ts) = Geometry();
            double px = _pixelsPerStep > 0 ? _pixelsPerStep : PitchPx(ts);
            // the surface follows the pointer: dragging towards the small end brings the larger values (printed on the other
            // side) under the centre line - drag up / left = larger (Reversed flips it, together with the print direction)
            double dir = _reversed ? 1 : -1;
            _acc += dir * d / px * Step * (fine ? 0.1 : 1.0);
            if (!_wrapAround) _acc = Math.Clamp(_acc, Minimum, Maximum);      // no dead travel past an end
            if (_wrapMouse) { var np = WrapPointerOnScreen(p, !Hz); if (np != p) _last = np; }
            return _acc;
        }
        protected override void OnValueApplied(double oldValue)
        {
            if (!IsDragging || IsCommittingDrag) _acc = Value;
            RefreshField();
        }
        protected override void OnPendingChanged() => RefreshField();
        protected override void OnDragEnded(bool committed, double shown)
        {
            base.OnDragEnded(committed, shown);
            if (!committed) RefreshField();
            // Click mode: a press and release without movement opens the field
            else if (_edit == WheelEdit.Click && !_moved && LastDragButton == MouseButtons.Left) OpenField();
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && (_edit == WheelEdit.DoubleClick || _edit == WheelEdit.Click))
            {
                if ((ModifierKeys & Keys.Control) != 0) { CloseField(false); SetValue(ResetValue, true); }
                else OpenField();
                return;
            }
            base.OnMouseDoubleClick(e);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (IsEditing) CloseField(true);
            base.OnMouseWheel(e);
        }

        // ------------------------------------------------------------------ the number field (SR2D, shared by Beside and the pop-up modes)
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.Style |= 0x02000000; return cp; }    // WS_CLIPCHILDREN: the drum paints around the field
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutField(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (_field != null) _field.Enabled = Enabled; }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); RefreshField(); }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); RefreshField(); }
        protected override void OnTextFontChanged() { base.OnTextFontChanged(); if (_field != null) { _field.TextFont = TextFont; _field.TextFontFamily = TextFontFamily; _field.TextFontSize = TextFontSize; } }
        void EnsureField()
        {
            if (_field != null) return;
            _field = new SpriteNumeric { Spinner = SpinnerPlacement.None, TextAlign = HorizontalAlignment.Center, TabStop = false, Visible = false };
            _field.AccentColor = AccentColor; _field.TrackColor = TrackColor; _field.ThumbColor = ThumbColor; _field.ForeColor = ForeColor;
            _field.ValueChanged += (_, _) => { if (_fieldSync) return; _fieldSync = true; try { SetValue(_field.Value, true); } finally { _fieldSync = false; } RefreshField(); };
            _field.Committed += (_, _) => { if (IsEditing && !_fieldSync) CloseField(false); };   // Enter in the pop-up: apply and hide
            _field.LostFocus += (_, _) => { if (IsEditing) CloseField(false); };
            _field.KeyDown += (_, k) => { if (k.KeyCode == Keys.Escape && IsEditing) { CloseField(false); k.Handled = true; } };
            Controls.Add(_field);
            RefreshField();
        }
        void LayoutField()
        {
            if (_field == null || !_field.Visible) return;
            var (drum, _, ts) = Geometry();
            int fh = Math.Max(18, TextH(ts) + 8 * ts + 4);
            if (_edit == WheelEdit.Beside) { int ew = EditW; _field.Bounds = new Rectangle(1, drum.Top + Math.Max(0, (drum.Height - fh) / 2), ew, fh); return; }
            // pop-up: over the read-out window, as wide as the drum allows
            int fw = Math.Clamp(Hz ? Math.Max(48, T.Measure(ValueText, ts).Width + 16 * ts) : drum.Width - 4, 32, Math.Max(32, drum.Width - 2));
            _field.Bounds = new Rectangle(drum.Left + (drum.Width - fw) / 2, drum.Top + (drum.Height - fh) / 2, fw, fh);
        }
        /// <summary>Puts the shown value into the field (no re-entry).</summary>
        void RefreshField()
        {
            if (_field == null || _fieldSync) return;
            _fieldSync = true;
            try
            {
                _field.Minimum = Minimum; _field.Maximum = Maximum; _field.Decimals = Decimals; _field.Step = Step; _field.Unit = Unit;
                if (_field.Value != Shown) _field.Value = Shown;
                _field.ForeColor = HasPendingValue ? AccentRaw : ForeColor;
                _field.BackColor = BackColor;
            }
            finally { _fieldSync = false; }
        }
        void OpenField()
        {
            EnsureField(); RefreshField();
            _popupOpen = true; _field!.Visible = true; LayoutField(); _field.BringToFront();
            _field.Focus(); _field.SelectAll();
            Redraw();
        }
        void CloseField(bool apply)
        {
            if (_field == null || !_popupOpen) return;
            _popupOpen = false;
            if (apply) _field.Commit();
            _field.Visible = false;
            if (!Focused && IsHandleCreated) Focus();
            Redraw();
        }
        /// <summary>Parses the field and applies it (bad text = revert).</summary>
        public void CommitEdit() { if (_field != null) { _field.Commit(); RefreshField(); } }
        /// <summary>Steps the value (Up / Down / wheel in the field).</summary>
        public void Nudge(int steps, bool fine = false) => SetValue(Value + steps * (fine ? Step / 10 : Step), true);

        // ------------------------------------------------------------------ paint
        protected override void PaintControl(Sprite s)
        {
            var (drum, capH, ts) = Geometry();
            bool hz = Hz, detailed = _style == WheelStyle.Detailed;
            int len = hz ? drum.Width : drum.Height, thick = hz ? drum.Height : drum.Width;   // along the axis / across
            float R = len / 2f, c0 = (hz ? drum.Left : drum.Top) + R;                             // centre along the axis
            int pitch = PitchPx(ts);
            double angPerStep = pitch / R;
            float lift = Lift;
            int hub = HubPx(thick);                                                               // metal hub on each side (across)
            var band = drum; if (hub > 0) { if (hz) band.Inflate(0, -hub); else band.Inflate(-hub, 0); }   // the rubber part
            int dark = Mix(BackColor, Black, detailed ? 0.55f : 0.35f), light = Mix(BackColor, ThumbColor, (detailed ? 0.22f : 0.30f) + 0.10f * lift), ridge = Mix(BackColor, Color.Black, 0.55f);

            // 1. the cylinder: one strip per pixel along the axis, brightness by cos of the surface angle (a soft specular near the middle)
            int lo = hz ? drum.Left : drum.Top;
            for (int i = 0; i < len; i++)
            {
                float t = (i + 0.5f - R) / R; float cos = MathF.Sqrt(MathF.Max(0f, 1 - t * t));
                float k = detailed ? MathF.Pow(cos, 1.1f) * 0.7f + MathF.Pow(cos, 24f) * 0.35f : MathF.Pow(cos, 0.8f) * 0.85f + MathF.Pow(cos, 12f) * 0.25f;
                int col = Lerp(dark, light, k);
                if (hz) s.FillRect(lo + i, band.Top, 1, band.Height, col); else s.FillRect(band.Left, lo + i, band.Width, 1, col);
            }
            if (detailed)
            {   // the hubs: brushed metal (lighter, with a fine cross grain), a dark seam against the rubber, a bevel at the outer edge
                int metalHi = Mix(BackColor, ThumbColor, 0.55f), metalLo = Mix(BackColor, ThumbColor, 0.18f), seam = Mix(BackColor, Black, 0.6f);
                for (int i = 0; i < len; i++)
                {
                    float t = (i + 0.5f - R) / R; float cos = MathF.Sqrt(MathF.Max(0f, 1 - t * t));
                    float k = MathF.Pow(cos, 0.9f) * (0.75f + 0.25f * ((i * 7) % 3 == 0 ? 1f : 0.85f));   // grain
                    int col = Lerp(metalLo, metalHi, k);
                    if (hz) { s.FillRect(lo + i, drum.Top, 1, hub, col); s.FillRect(lo + i, drum.Bottom - hub, 1, hub, col); }
                    else { s.FillRect(drum.Left, lo + i, hub, 1, col); s.FillRect(drum.Right - hub, lo + i, hub, 1, col); }
                }
                if (hz) { s.FillRect(drum.Left, band.Top, len, 1, seam); s.FillRect(drum.Left, band.Bottom - 1, len, 1, seam); s.FillRect(drum.Left, drum.Top + 1, len, 1, Argb(metalHi, 120), SR2D.LineOp.AlphaBlend); }
                else { s.FillRect(band.Left, drum.Top, 1, len, seam); s.FillRect(band.Right - 1, drum.Top, 1, len, seam); s.FillRect(drum.Left + 1, drum.Top, 1, len, Argb(metalHi, 120), SR2D.LineOp.AlphaBlend); }
            }
            // 2. ridges and labels: one per step index around the shown value; wrap-around shows the range continuing
            double stepIdx = (Shown - Minimum) / Step;                                           // fractional index of the shown value
            int textH = TextH(ts);
            string fmt = Fmt;
            int labelLen = LabelLen(ts);
            int labelEvery = Math.Max(1, (int)Math.Ceiling((labelLen + 2.0) / (pitch * 0.65)));   // uniform thinning that still fits where the projection squeezes the pitch (cos >= 0.65)
            int window = Math.Max(pitch, (hz ? T.Measure(ValueText, ts).Width : textH) + 4);   // the read-out window
            int span = (int)Math.Ceiling(Math.PI / 2 / angPerStep) + 1;
            int i0 = (int)Math.Floor(stepIdx) - span, i1 = (int)Math.Ceiling(stepIdx) + span;
            double maxIdx = (Maximum - Minimum) / Step;
            int dir = _reversed ? -1 : 1;                                                     // +1: larger values further down / right
            int grooveHi = Mix(BackColor, ThumbColor, 0.45f);
            float across0 = hz ? band.Top + 1 : band.Left + 1, across1 = hz ? band.Bottom - 1 : band.Right - 1;
            for (int i = i0; i <= i1; i++)
            {
                double off = (i - stepIdx) * angPerStep;                                          // surface angle from the centre line
                if (Math.Abs(off) >= Math.PI / 2 - 1e-3) continue;
                int idx = i; double v = Minimum + i * Step;
                if (_wrapAround) { double n = Math.Floor(i / maxIdx); idx = (int)Math.Round(i - n * maxIdx); v = Minimum + (i - n * maxIdx) * Step; if (idx >= (int)Math.Round(maxIdx)) idx = 0; }
                else if (i < 0 || i > maxIdx + 1e-9) continue;
                float cos = (float)Math.Cos(off), q = c0 + (float)Math.Sin(off) * R * dir;
                if (detailed)
                {   // a grip groove: dark cut with a light lip on the side that faces the viewer; sub-grooves between labelled steps
                    float gw = MathF.Max(1f, MathF.Min(3f, pitch * 0.18f) * cos);
                    if (hz) { s.FillRect(q - gw / 2, across0, gw, across1 - across0, Argb(ridge, (int)(90 + 150 * cos)), SR2D.LineOp.AlphaBlend); s.FillRect(q + gw / 2, across0, 1, across1 - across0, Argb(grooveHi, (int)(40 + 110 * cos)), SR2D.LineOp.AlphaBlend); }
                    else { s.FillRect(across0, q - gw / 2, across1 - across0, gw, Argb(ridge, (int)(90 + 150 * cos)), SR2D.LineOp.AlphaBlend); s.FillRect(across0, q + gw / 2, across1 - across0, 1, Argb(grooveHi, (int)(40 + 110 * cos)), SR2D.LineOp.AlphaBlend); }
                    if (pitch >= 14)
                    {   // knurl: two fine lines between grooves
                        for (int k = 1; k <= 2; k++)
                        {
                            double so = off + angPerStep * k / 3.0; if (Math.Abs(so) >= Math.PI / 2 - 1e-3) continue;
                            float sq = c0 + (float)Math.Sin(so) * R * dir, sc = (float)Math.Cos(so);
                            if (hz) s.FillRect(sq, across0 + 1, 1, across1 - across0 - 2, Argb(ridge, (int)(30 + 70 * sc)), SR2D.LineOp.AlphaBlend);
                            else s.FillRect(across0 + 1, sq, across1 - across0 - 2, 1, Argb(ridge, (int)(30 + 70 * sc)), SR2D.LineOp.AlphaBlend);
                        }
                    }
                }
                else
                {
                    if (hz) s.DrawWideLine(q, drum.Top + 1, q, drum.Bottom - 1, Argb(ridge, (int)(60 + 150 * cos)), 1f, true, SR2D.LineOp.AlphaBlend);
                    else s.DrawWideLine(drum.Left + 1, q, drum.Right - 1, q, Argb(ridge, (int)(60 + 150 * cos)), 1f, true, SR2D.LineOp.AlphaBlend);
                }
                if (_labels && cos > 0.62f && idx % labelEvery == 0)
                {
                    if (ShowValue && Math.Abs(Math.Sin(off)) * R < window / 2f + labelLen / 2f + 3) continue;   // the read-out sits there
                    string lbl = v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
                    int lts = FitScale(lbl, (hz ? band.Height : band.Width) - 4, ts);
                    int col = Mix(Lerp(dark, light, cos), ForeColor, 0.25f + 0.65f * cos);
                    if (hz) T.Draw(s, (int)MathF.Round(q), band.Top + band.Height / 2, lbl, col, 0, lts, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
                    else T.Draw(s, band.Left + band.Width / 2, (int)MathF.Round(q), lbl, col, 0, lts, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
                }
            }
            // 3. end stops of a bounded range: a dark cap beyond the last value
            if (!_wrapAround)
            {
                double offMin = (0 - stepIdx) * angPerStep, offMax = (maxIdx - stepIdx) * angPerStep;
                if (offMin > -Math.PI / 2) Cap(s, drum, c0 + (float)Math.Sin(offMin) * R * dir, dir < 0, hz, dark);
                if (offMax < Math.PI / 2) Cap(s, drum, c0 + (float)Math.Sin(offMax) * R * dir, dir > 0, hz, dark);
            }
            // 4. lens highlight (Detailed): a soft light streak across the drum, a touch off centre, as on a glossy wheel
            if (detailed)
            {
                float hl = MathF.Max(2f, len * 0.09f), hq = c0 - R * 0.28f;
                int white = unchecked((int)0xFFFFFFFF);
                for (int i = 0; i < (int)hl; i++)
                {
                    float f = 1f - MathF.Abs((i + 0.5f) / hl * 2f - 1f); int a = (int)(38 * f * f);
                    if (hz) s.FillRect(hq - hl / 2 + i, band.Top + 1, 1, band.Height - 2, Argb(white, a), SR2D.LineOp.AlphaBlend);
                    else s.FillRect(band.Left + 1, hq - hl / 2 + i, band.Width - 2, 1, Argb(white, a), SR2D.LineOp.AlphaBlend);
                }
            }
            // 5. centre window: accent band + read-out; Detailed adds a hairline through the middle and a darker glass tint
            int acc = Accent;
            float w0 = c0 - window / 2f;
            if (hz)
            {
                s.FillRect(w0, drum.Top, window, drum.Height, Argb(acc, (detailed ? 36 : 50) + (int)(40 * lift)), SR2D.LineOp.AlphaBlend);
                s.FillRect(w0, drum.Top, 1, drum.Height, acc); s.FillRect(c0 + window / 2f - 1, drum.Top, 1, drum.Height, acc);
                if (detailed && !ShowValue) s.FillRect(c0 - 0.5f, drum.Top, 1, drum.Height, Argb(acc, 150), SR2D.LineOp.AlphaBlend);
            }
            else
            {
                s.FillRect(drum.Left, w0, drum.Width, window, Argb(acc, (detailed ? 36 : 50) + (int)(40 * lift)), SR2D.LineOp.AlphaBlend);
                s.FillRect(drum.Left, w0, drum.Width, 1, acc); s.FillRect(drum.Left, c0 + window / 2f - 1, drum.Width, 1, acc);
                if (detailed && !ShowValue) s.FillRect(drum.Left, c0 - 0.5f, drum.Width, 1, Argb(acc, 150), SR2D.LineOp.AlphaBlend);
            }
            if (ShowValue && !IsEditing)
            {
                int avail = (hz ? band.Height : band.Width) - 4;
                int vts = FitScale(ValueText, avail, ts);
                int col = HasPendingValue ? Accent : Fore;
                if (detailed)
                {   // a dark plate behind the digits so they read on the light band
                    var m = T.Measure(FitText(ValueText, avail, vts), vts);
                    int plate = Argb(Mix(BackColor, Black, 0.5f), 170);
                    if (hz) s.FillRoundRect(c0 - m.Width / 2f - 3, band.Top + band.Height / 2f - m.Height / 2f - 2, m.Width + 6, m.Height + 4, 2f, plate, SR2D.LineOp.AlphaBlend);
                    else s.FillRoundRect(band.Left + band.Width / 2f - m.Width / 2f - 3, c0 - m.Height / 2f - 2, m.Width + 6, m.Height + 4, 2f, plate, SR2D.LineOp.AlphaBlend);
                }
                if (hz) T.Draw(s, (int)c0, band.Top + band.Height / 2, FitText(ValueText, avail, vts), col, 0, vts, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
                else T.Draw(s, band.Left + band.Width / 2, (int)c0, FitText(ValueText, avail, vts), col, 0, vts, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
            }
            // 6. frame (brighter while dragging / lifted) and caption
            int rim = IsDragging ? Accent : Mix(BackColor, ThumbColor, IsHot && Enabled ? 0.55f : 0.4f);
            if (detailed) DrawRound(s, drum.Left - 0.5f, drum.Top - 0.5f, drum.Width + 1, drum.Height + 1, 3f, rim, 1f);
            else s.DrawRect(drum.Left - 0.5f, drum.Top - 0.5f, drum.Width + 1, drum.Height + 1, rim, 1f, false);
            if (capH > 0) { int cs = FitScale(Text, ClientSize.Width - 2, ts); T.Draw(s, 1, 1 + (capH - LineHeight(cs)) / 2, FitText(Text, ClientSize.Width - 2, cs), Fore, 0, cs); }
        }
        // dark cap from the end-stop position to the drum edge (beyond Minimum / Maximum)
        static void Cap(Sprite s, Rectangle drum, float q, bool towardsHighEnd, bool hz, int dark)
        {
            int c = (dark & 0xFFFFFF) | (170 << 24);
            if (hz) { if (towardsHighEnd) s.FillRect(q, drum.Top, drum.Right - q, drum.Height, c, SR2D.LineOp.AlphaBlend); else s.FillRect(drum.Left, drum.Top, q - drum.Left, drum.Height, c, SR2D.LineOp.AlphaBlend); }
            else { if (towardsHighEnd) s.FillRect(drum.Left, q, drum.Width, drum.Bottom - q, c, SR2D.LineOp.AlphaBlend); else s.FillRect(drum.Left, drum.Top, drum.Width, q - drum.Top, c, SR2D.LineOp.AlphaBlend); }
        }
    }

    /// <summary>Look of a <see cref="SpriteWheel"/>.</summary>
    internal enum WheelStyle { Detailed, Flat }
    /// <summary>How a <see cref="SpriteWheel"/> lets the user type a value.</summary>
    internal enum WheelEdit { None, Beside, DoubleClick, Click }
}
