using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // ImplicitUsings also brings System.Threading.Timer

namespace Sr2d64CSport
{
    /// <summary>
    /// Base of the SR2D-drawn VALUE controls (<see cref="SpriteKnob"/>, <see cref="SpriteSlider"/>); the buttons / toggles /
    /// progress bars live in SpriteControls.Buttons.cs on the same <see cref="SpriteControlBase"/>.
    ///
    /// Why not the native TrackBar: its thumb is moved by the common-controls code from WM_MOUSEMOVE deltas and
    /// the value is reported through reflected WM_HSCROLL messages, so under a busy message loop (a render loop on
    /// Application.Idle, a slow paint) the thumb lags and quick drags "lose" motion. Here the value is computed from
    /// the ABSOLUTE pointer position on every mouse message (so it can never fall behind - wherever the pointer is when
    /// the drag stops, that is the value), the control repaints synchronously with <see cref="SpriteBox.RedrawNow"/>
    /// (no WM_PAINT round trip) and the whole picture is one SR2D surface (no flicker, no GDI+).
    ///
    /// Interaction (both controls): left drag, mouse wheel (<see cref="Step"/>, Shift = 1/10 step), arrow / PageUp /
    /// PageDown / Home / End keys, double click = <see cref="ResetValue"/>.
    /// </summary>
    /// <summary>
    /// Common ground of every SR2D-drawn control (knob, slider, button, toggle, radio, progress): the palette
    /// (<see cref="AccentColor"/>, <see cref="TrackColor"/>, <see cref="ThumbColor"/>), <see cref="TextScale"/>, hover /
    /// pressed state, and the text helpers. Rendering goes through <see cref="PaintControl"/> on the SpriteBox surface.
    /// </summary>
    /// <summary>Implemented by the window an SR2D control is dropped on (<see cref="SpriteForm"/>, in the file the
    /// control tests do not compile): the face colour the app chose for it, or null. An interface rather than the
    /// type keeps this file independent of the chrome.</summary>
    internal interface IControlFaceSource { Color? ControlFaceColor { get; } }

    public abstract partial class SpriteControlBase : SpriteBox
    {
        int _textScale;
        // the palette defaults (Color, because the properties are Color for the designer; the paint code reads them as ints once per paint)
        internal static readonly Color DefaultBack = Color.FromArgb(0x20, 0x24, 0x28), DefaultAccent = Color.FromArgb(0x33, 0x99, 0xFF),
                                       DefaultTrack = Color.FromArgb(0x50, 0x58, 0x60), DefaultThumb = Color.FromArgb(0xE8, 0xEC, 0xF0);
        Color _accent = DefaultAccent, _track = DefaultTrack, _thumb = DefaultThumb;
        bool _hover, _animated = true, _backExplicit;

        // ------------------------------------------------------------------ animation
        // One position 0..1 per control ("off look" .. "on look"); AnimateTo eases it towards a target over a few frames on a
        // shared UI timer. Controls read AnimT while painting. Animated = false (or no handle yet) snaps.
        float _anim, _animFrom, _animTo = float.NaN; double _animStart, _animMs;
        static readonly System.Collections.Generic.List<SpriteControlBase> AnimRunning = new();
        static readonly System.Diagnostics.Stopwatch AnimClock = System.Diagnostics.Stopwatch.StartNew();
        static double _animNow, _animLastReal;
        static Timer? _animTimer;

        [Category("Behavior"), DefaultValue(true), Description("Animate state changes (the switch handle slides, the rocker tips, the tick draws, the radio dot grows). Off = every change snaps.")]
        public bool Animated { get => _animated; set { _animated = value; if (!value && IsAnimating) { _anim = _animTo; StopAnim(); Redraw(); } } }

        /// <summary>Current animation position 0..1 (0 = the "off" look, 1 = the "on" look).</summary>
        protected float AnimT => _anim;
        protected bool IsAnimating => !float.IsNaN(_animTo);

        /// <summary>Ease the animation position to <paramref name="target"/> (0..1) over <paramref name="ms"/> for a full swing (shorter for a shorter distance).</summary>
        protected void AnimateTo(float target, int ms = 170)
        {
            target = Math.Clamp(target, 0f, 1f);
            if (!_animated || !IsHandleCreated || Math.Abs(target - _anim) < 1e-4f) { _anim = target; StopAnim(); Redraw(); return; }
            _animFrom = _anim; _animTo = target; _animStart = _animNow; _animMs = Math.Max(30, ms * Math.Abs(target - _anim));
            if (!AnimRunning.Contains(this)) AnimRunning.Add(this);
            if (_animTimer == null) { _animTimer = new Timer { Interval = 15 }; _animTimer.Tick += (_, _) => { double now = AnimClock.Elapsed.TotalMilliseconds; StepAnimations(now - _animLastReal); _animLastReal = now; }; }
            if (!_animTimer.Enabled) { _animLastReal = AnimClock.Elapsed.TotalMilliseconds; _animTimer.Start(); }
        }
        /// <summary>Jump to a position without animating (initial state).</summary>
        protected void SnapAnim(float value) { _anim = Math.Clamp(value, 0f, 1f); StopAnim(); }
        void StopAnim() { _animTo = float.NaN; AnimRunning.Remove(this); }
        bool Advance()
        {
            if (!IsAnimating) return false;
            double p = (_animNow - _animStart) / _animMs;
            if (p >= 1) { _anim = _animTo; StopAnim(); return true; }
            float e = (float)(p * p * (3 - 2 * p));                       // smoothstep
            _anim = _animFrom + (_animTo - _animFrom) * e;
            return true;
        }
        /// <summary>Advance every running animation by <paramref name="dtMs"/> and repaint the controls (called by the shared timer; test harnesses may call it directly).</summary>
        internal static void StepAnimations(double dtMs)
        {
            _animNow += Math.Max(0, dtMs);
            var list = AnimRunning.ToArray();
            foreach (var c in list) if (c.Advance()) { if (c.IsHandleCreated) c.RedrawNow(); else c.Redraw(); }
            if (AnimRunning.Count == 0 && _animTimer != null && _animTimer.Enabled) _animTimer.Stop();
        }
        protected override void OnHandleDestroyed(EventArgs e) { StopAnim(); base.OnHandleDestroyed(e); }

        protected SpriteControlBase()
        {
            TabStop = true;
            AccessibleRole = DefaultAccessibleRole;     // screen readers: role + name (synced with the Text below) describe the owner-drawn control
            BackColor = DefaultBack;              // the setter leaves it "not explicit": a parent (a panel, a group box, a coloured form) may replace it
            ForeColor = Color.White;
            // the arrow by default: only controls whose value MOVES with the mouse (knob, slider, wheel) show the hands - see
            // SpriteRangeControl; buttons, toggles, radios, lists, tabs are clicked and keep the arrow like native controls
            Cursor = Cursors.Default;
        }

        // ------------------------------------------------------------------ ambient background
        // WinForms controls inherit BackColor from the parent until it is set; SpriteControls set theirs in the constructor
        // (so a knob on a light form stays dark), which would break that. This restores it: a control whose BackColor is
        // still the SR2D default takes the colour of its parent - the face of a SpriteControl container it is dropped into
        // (a sunken SpritePanel, a SpriteTabPage), or a SpriteForm's own BackColor - and follows it when that changes.
        // "Still the default" is the value, not the setter call: the designer writes BackColor = the default into the
        // generated code for every control you drop, which would otherwise pin the colour before you ever set the form's.
        /// <summary>Background colour (the face of most controls). Set it explicitly to stop a container from overriding it.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public override Color BackColor { get => base.BackColor; set { base.BackColor = value; _backExplicit = value != DefaultBack; } }
        /// <summary>True when BackColor is something other than the SR2D default (so no parent colour applies).</summary>
        [Browsable(false)] public bool BackColorIsExplicit => _backExplicit;
        /// <summary>The designer asks this before it writes a BackColor line into the form's code. Only a colour the
        /// app chose goes in: the SR2D default (which the designer used to pin for every control you dropped, so the
        /// form's own background never reached it) and a container's adopted face both stay un-written.</summary>
        public bool ShouldSerializeBackColor() => _backExplicit;
        public override void ResetBackColor() { _backExplicit = false; base.BackColor = DefaultBack; }
        internal void AdoptBackColor(Color c) { if (!_backExplicit && base.BackColor != c) base.BackColor = c; }
        /// <summary>The colour the window a control sits on hands out, or null while that window has not been given
        /// one: inheriting an untouched light grey form would turn every dark SR2D control on it into a light one
        /// with light text, so an untouched form hands out nothing and the control keeps its own default.</summary>
        internal void AdoptFormBackColor(Color? formColor) => AdoptBackColor(formColor ?? DefaultBack);
        /// <summary>Colour a child control should adopt (containers override this to hand out their face colour).</summary>
        protected virtual Color ChildBackColor => BackColor;
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            // Plain containers (TableLayoutPanel, FlowLayoutPanel, ...) have no face of their own to hand out, so the
            // walk keeps going up through them: a knob two panels deep inside a sunken SpritePanel still gets its colour.
            for (Control? c = Parent; c != null; c = c.Parent)
            {
                if (c is SpriteControlBase p) { AdoptBackColor(p.ChildBackColor); return; }
                if (c is IControlFaceSource f) { AdoptFormBackColor(f.ControlFaceColor); return; }
            }
        }
        /// <summary>Pushes <see cref="ChildBackColor"/> down to every SpriteControl below that has no explicit colour of its
        /// own. Plain containers are descended through; a nested SR2D container hands out its OWN face from there on.</summary>
        protected void PushBackColor() => PushBackColor(ChildBackColor, this);
        static void PushBackColor(Color face, Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is SpriteControlBase sc)
                {
                    sc.AdoptBackColor(face);
                    if (sc.Controls.Count > 0) PushBackColor(sc.ChildBackColor, sc);
                }
                else if (c.Controls.Count > 0) PushBackColor(face, c);
            }
        }

        /// <summary>Pixel size of the text font (1 = 5x7 px glyphs, 2 = 10x14 ...); 0 = 1 (the smallest size). Text that would not fit drops one size.</summary>
        [Category("Appearance"), DefaultValue(0), Description("Pixel size of the text font (1 = 5x7 px glyphs, 2 = 10x14 ...); 0 = 1 (the smallest size). Text that would not fit drops one size.")]
        public int TextScale { get => _textScale; set { _textScale = Math.Clamp(value, 0, 8); OnTextScaleChanged(); } }
        /// <summary>TextScale changed (containers re-layout, the default repaints).</summary>
        protected virtual void OnTextScaleChanged() => Redraw();

        [Category("Appearance"), Description("Colour of the active part: filled track, pointer, the ON state, the pressed button face.")]
        public Color AccentColor { get => _accent; set { _accent = value; Redraw(); } }
        bool ShouldSerializeAccentColor() => _accent != DefaultAccent;
        void ResetAccentColor() => AccentColor = DefaultAccent;

        [Category("Appearance"), Description("Colour of the passive part: empty track, OFF state, button face.")]
        public Color TrackColor { get => _track; set { _track = value; Redraw(); } }
        bool ShouldSerializeTrackColor() => _track != DefaultTrack;
        void ResetTrackColor() => TrackColor = DefaultTrack;

        [Category("Appearance"), Description("Colour of the thumb / knob body highlight / switch handle.")]
        public Color ThumbColor { get => _thumb; set { _thumb = value; Redraw(); } }
        bool ShouldSerializeThumbColor() => _thumb != DefaultThumb;
        void ResetThumbColor() => ThumbColor = DefaultThumb;

        /// <summary>Caption drawn on the control (WinForms Text; the designer serialises it).</summary>
        [Browsable(true), EditorBrowsable(EditorBrowsableState.Always), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible), Bindable(true)]
        [AllowNull] public override string Text { get => base.Text; set { base.Text = value ?? ""; Redraw(); } }

        /// <summary>The screen-reader role of this control, applied in the constructor; override where the default (a client area) is wrong.</summary>
        protected virtual AccessibleRole DefaultAccessibleRole => AccessibleRole.Client;

        /// <summary>Keeps <see cref="Control.AccessibleName"/> in sync with <see cref="Text"/> so screen readers announce the control by its caption.</summary>
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (Text.Length > 0) AccessibleName = Text; }

        /// <summary>True while the mouse is over the control.</summary>
        [Browsable(false)] public bool IsHot => _hover;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Redraw(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Redraw(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Redraw(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Redraw(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Redraw(); }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); Redraw(); }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); PushBackColor(); Redraw(); }

        // ------------------------------------------------------------------ rendering
        protected override bool HasRenderer => true;
        protected override void OnRender(Sprite s)
        {
            s.ClearBuffer(BackColor.ToArgb());
            PaintControl(s);
            if (Focused && ShowFocusCues && !OwnFocusRect) s.DrawRect(0.5f, 0.5f, s.Width - 1, s.Height - 1, Argb(_accent, 160), 1f, false, SR2D.LineOp.AlphaBlend);
        }
        /// <summary>Draws the control into its surface (already cleared to BackColor).</summary>
        protected abstract void PaintControl(Sprite s);
        /// <summary>True when <see cref="PaintControl"/> shows the focus itself (no rectangle around the whole control).</summary>
        protected virtual bool OwnFocusRect => false;

        // ------------------------------------------------------------------ colour maths
        // Everything the paint code computes is a packed ARGB int (what Sprite takes). System.Drawing.Color is only used
        // where WinForms needs it (BackColor / ForeColor / the designer-visible colour properties): converting through a
        // Color per blend costs a 24-byte struct and three property reads each way, the int path is plain shifts.
        protected const int White = unchecked((int)0xFFFFFFFF), Black = unchecked((int)0xFF000000);
        /// <summary>The colour with another alpha (0..255).</summary>
        protected static int Argb(int c, int alpha = 255) => (alpha << 24) | (c & 0xFFFFFF);
        protected static int Argb(Color c, int alpha = 255) => Argb(c.ToArgb(), alpha);
        /// <summary>Opaque linear blend of two packed colours: t = 0 gives a, t = 1 gives b.</summary>
        protected static int Mix(int a, int b, float t)
        {
            t = Math.Clamp(t, 0, 1);
            int ar = (a >> 16) & 255, ag = (a >> 8) & 255, ab = a & 255;
            int br = (b >> 16) & 255, bg = (b >> 8) & 255, bb = b & 255;
            return SR2D.ARGB(255, (byte)(ar + (br - ar) * t), (byte)(ag + (bg - ag) * t), (byte)(ab + (bb - ab) * t));
        }
        protected static int Mix(Color a, Color b, float t) => Mix(a.ToArgb(), b.ToArgb(), t);
        protected static int Mix(int a, Color b, float t) => Mix(a, b.ToArgb(), t);
        protected static int Mix(Color a, int b, float t) => Mix(a.ToArgb(), b, t);
        /// <summary>Same as <see cref="Mix(int, int, float)"/> (older name, kept).</summary>
        protected static int Lerp(int a, int b, float t) => Mix(a, b, t);
        protected int Fore => Enabled ? ForeColor.ToArgb() : Mix(ForeColor, BackColor, 0.5f);
        protected int Accent => Enabled ? _accent.ToArgb() : Mix(_accent, BackColor, 0.5f);
        protected int Track => _track.ToArgb();
        protected int Thumb => Enabled ? _thumb.ToArgb() : Mix(_thumb, BackColor, 0.5f);
        protected Color AccentRaw => _accent;
        /// <summary>Text scale for this control: the explicit <see cref="TextScale"/>, otherwise 1 - the smallest pixel-font size. (Older builds guessed a
        /// bigger scale from the control's own size, so text ballooned on large controls and containers that re-measure - e.g. a tab strip - changed their
        /// geometry at paint time. Set <see cref="TextScale"/> explicitly where bigger text is wanted; <paramref name="sizeHint"/> is kept for source compat.)</summary>
        protected int AutoTextScale(int sizeHint) { _ = sizeHint; return _textScale > 0 ? _textScale : 1; }
        /// <summary>Swallows WM_INPUTLANGCHANGE: WinForms wraps it in InputLanguageChangedEventArgs, whose LanguageTag needs a real CultureInfo - under
        /// InvariantGlobalization (or without ICU) that throws CultureNotFoundException the moment the user switches the keyboard layout while one of our
        /// controls has focus (e.g. typing Cyrillic on a Russian layout). The controls do not care about the input language: WM_CHAR already delivers the
        /// characters, so the message never reaches Control.WmInputLangChange.</summary>
        protected override void WndProc(ref Message m) { if (m.Msg != 0x0051) base.WndProc(ref m); }
        /// <summary>Largest scale &lt;= <paramref name="want"/> at which <paramref name="text"/> fits into <paramref name="width"/> px (never below 1). Uses the control's font (<see cref="T"/>).</summary>
        protected int FitScale(string text, int width, int want) => T.FitScale(text, width, want);
        /// <summary>Shortens <paramref name="text"/> with an ellipsis so it fits <paramref name="width"/> px at <paramref name="scale"/>.</summary>
        protected string FitText(string text, int width, int scale, int weight = 0) => T.FitText(text, width, scale, weight);
        /// <summary>Height of a text row with breathing space (pixel font: 9 * scale + 2).</summary>
        protected int LineHeight(int scale) => T.RowHeight(scale);
        /// <summary>Height of one text line as <see cref="ControlText.Draw"/> stacks them.</summary>
        protected int TextLineHeight(int scale) => T.LineHeight(scale);
        /// <summary>Word-wraps <paramref name="text"/> to <paramref name="width"/> px at <paramref name="scale"/> ('\n' kept; words longer than the width are cut).</summary>
        protected string WrapText(string text, int width, int scale) => T.Wrap(text, width, scale);
        /// <summary>Draws a check box (rounded square, tick drawn <paramref name="t"/> of the way) centred on (cx, cy), half size <paramref name="half"/>.</summary>
        protected static void PaintCheckBox(Sprite s, float cx, float cy, float half, float t, int onCol, int offCol, int faceCol)
        {
            float r = half * 0.3f;
            FillRound(s, cx - half, cy - half, half * 2, half * 2, r, faceCol);
            DrawRound(s, cx - half, cy - half, half * 2, half * 2, r, t > 0.5f ? onCol : offCol, MathF.Max(1f, half * 0.16f));
            if (t <= 0.02f) return;
            // the tick draws itself: the short stroke first, then the long one
            float w = MathF.Max(1.2f, half * 0.28f);
            var a = new PointF(cx - half * 0.55f, cy + half * 0.02f); var b = new PointF(cx - half * 0.15f, cy + half * 0.45f); var c = new PointF(cx + half * 0.6f, cy - half * 0.5f);
            float t1 = MathF.Min(1f, t / 0.4f), t2 = MathF.Max(0f, (t - 0.4f) / 0.6f);
            Span<PointF> pts = stackalloc PointF[3];
            pts[0] = a; pts[1] = new PointF(a.X + (b.X - a.X) * t1, a.Y + (b.Y - a.Y) * t1); pts[2] = new PointF(b.X + (c.X - b.X) * t2, b.Y + (c.Y - b.Y) * t2);
            s.DrawPolyline(pts.Slice(0, t2 > 0 ? 3 : 2), onCol, w, true);
        }
        /// <summary>A chevron (V) pointing down (dir 0), up (1), left (2) or right (3), centred on (cx, cy), arm length <paramref name="a"/>.</summary>
        protected static void PaintChevron(Sprite s, float cx, float cy, float a, int dir, int col, float width)
        {
            Span<PointF> p = stackalloc PointF[3];
            switch (dir)
            {
                case 0: p[0] = new PointF(cx - a, cy - a / 2); p[1] = new PointF(cx, cy + a / 2); p[2] = new PointF(cx + a, cy - a / 2); break;
                case 1: p[0] = new PointF(cx - a, cy + a / 2); p[1] = new PointF(cx, cy - a / 2); p[2] = new PointF(cx + a, cy + a / 2); break;
                case 2: p[0] = new PointF(cx + a / 2, cy - a); p[1] = new PointF(cx - a / 2, cy); p[2] = new PointF(cx + a / 2, cy + a); break;
                default: p[0] = new PointF(cx - a / 2, cy - a); p[1] = new PointF(cx + a / 2, cy); p[2] = new PointF(cx - a / 2, cy + a); break;
            }
            s.DrawPolyline(p, col, width, true);
        }

        // the knob's body look, shared with the round button / radio: dark disc, off-centre highlight, rim
        protected void PaintBody(Sprite s, float cx, float cy, float br, bool active, float rimW)
        {
            int body = Mix(BackColor, ThumbColor, active ? 0.24f : 0.18f), rim = Mix(BackColor, ThumbColor, active ? 0.75f : IsHot && Enabled ? 0.6f : 0.45f);
            s.FillCircle(cx, cy, br, body, SR2D.LineOp.Set, true);
            s.FillCircle(cx - br * 0.18f, cy - br * 0.18f, br * 0.55f, Mix(BackColor, ThumbColor, active ? 0.32f : 0.26f), SR2D.LineOp.Set, true);
            s.DrawCircle(cx, cy, br, rim, rimW, true);
        }
        // rounded rectangle helpers (PathBuilder based, anti-aliased)
        protected static void FillRound(Sprite s, float x, float y, float w, float h, float r, int c) => s.FillRoundRect(x, y, w, h, r, c);
        protected static void DrawRound(Sprite s, float x, float y, float w, float h, float r, int c, float width) => s.DrawRoundRect(x, y, w, h, r, c, width);

        // ------------------------------------------------------------------ pointer wrapping (endless drags)
        /// <summary>
        /// Teleports the pointer to the opposite edge of the screen when a drag runs off it, so a long drag never stops
        /// at the border (the numeric spinner, the wheel, the endless knob). <paramref name="client"/> is the pointer in
        /// client coordinates; returns the corrected client point (unchanged when nothing happened). Vertical only when
        /// <paramref name="vertical"/>, else horizontal. Two pixels inside the far edge so the next move is inside again.
        /// </summary>
        protected Point WrapPointerOnScreen(Point client, bool vertical)
        {
            if (!IsHandleCreated) return client;
            var sp = PointToScreen(client);
            var area = Screen.FromPoint(sp).Bounds;
            int q = vertical ? sp.Y : sp.X, lo = vertical ? area.Top : area.Left, hi = vertical ? area.Bottom : area.Right;
            if (hi - lo < 8 || (q > lo && q < hi - 1)) return client;
            int nq = q <= lo ? hi - 3 : lo + 2;
            var np = vertical ? new Point(sp.X, nq) : new Point(nq, sp.Y);
            Cursor.Position = np;
            return PointToClient(np);
        }

        /// <summary>
        /// Re-reads the font for this control and every SR2D control under it (after changing
        /// <see cref="DefaultFont"/> / <see cref="DefaultFontFamily"/> / <see cref="DefaultFontSize"/> at run time):
        /// auto-sized labels re-fit, containers re-layout their children, everything repaints. Call it on the form's
        /// top-level container (or on each strip) - the demo does after its font combo changes.
        /// </summary>
        public void RefreshFonts()
        {
            OnTextFontChanged();
            foreach (Control c in Controls) if (c is SpriteControlBase b) b.RefreshFonts(); else RefreshFontsUnder(c);
        }
        /// <summary>Same for a non-SR2D container (a TableLayoutPanel holding SR2D controls).</summary>
        public static void RefreshFontsUnder(Control root)
        {
            foreach (Control c in root.Controls) if (c is SpriteControlBase b) b.RefreshFonts(); else RefreshFontsUnder(c);
        }
    }

    /// <summary>Shared snapping helpers for the discrete-value mode of the range controls (Values / PowersOfTwo).</summary>
    internal static class Discrete
    {
        /// <summary>The most decimals any entry of a discrete list needs (0..3): the value text of a slider snapped
        /// to 0 / 0.5 / 1.5 / 3 must show "1.5", not the rounded "2" the integer default produced.</summary>
        public static int DecimalsOf(double[]? v)
        {
            int d = 0;
            if (v == null) return 0;
            foreach (double x in v)
                for (int k = 0; k <= 3; k++)
                    if (Math.Abs(x * Math.Pow(10, k) - Math.Round(x * Math.Pow(10, k))) < 1e-9) { d = Math.Max(d, k); break; }
            return d;
        }
        /// <summary>Sorted, de-duplicated copy of a value list (null when empty).</summary>
        public static double[]? Norm(double[]? v)
        {
            if (v is not { Length: > 0 }) return null;
            var c = (double[])v.Clone(); Array.Sort(c);
            int n = 0;
            for (int i = 0; i < c.Length; i++) if (n == 0 || c[i] != c[n - 1]) c[n++] = c[i];
            return n == c.Length ? c : c[..n];
        }
        /// <summary>The index of the entry equal to <paramref name="v"/> (the list is sorted: binary search), or the nearest one when there is no exact match.</summary>
        public static int IndexOf(double[] list, double v)
        {
            int i = Array.BinarySearch(list, v);
            if (i >= 0) return i;
            i = ~i;
            if (i <= 0) return 0;
            if (i >= list.Length) return list.Length - 1;
            return Math.Abs(list[i] - v) < Math.Abs(list[i - 1] - v) ? i : i - 1;
        }
        /// <summary>The list entry nearest to <paramref name="v"/>.</summary>
        public static double Nearest(double[] list, double v)
        {
            double best = list[0];
            foreach (double x in list) if (Math.Abs(x - v) < Math.Abs(best - v)) best = x;
            return best;
        }
        /// <summary>The neighbouring entry of <paramref name="v"/> in direction <paramref name="dir"/> (+1 / -1); v itself at the ends of the list (so a wheel never gets stuck between two entries).</summary>
        public static double Next(double[] list, double v, int dir)
        {
            double best = v;
            foreach (double x in list)
                if ((dir > 0 && x > v + 1e-9 && (best == v || x < best)) || (dir < 0 && x < v - 1e-9 && (best == v || x > best))) best = x;
            return best;
        }
        /// <summary>The powers of two inside min..max (e.g. 8 16 32 64 128 256 for 8..256).</summary>
        public static double[] Pow2(double min, double max)
        {
            var list = new System.Collections.Generic.List<double>();
            for (double p = 1; p <= max; p *= 2) if (p >= min) list.Add(p);
            return list.ToArray();
        }
    }

    public abstract class SpriteRangeControl : SpriteControlBase
    {
        double _min, _max = 100, _value, _step = 1, _reset;
        int _decimals;
        bool _showValue = true, _snap, _bipolar, _dragging;
        string _unit = "";
        double[]? _values; bool _pow2;

        protected SpriteRangeControl() { Cursor = SpriteCursors.HandOpen; }   // "drag me": the open hand while hovering, the fist while dragging

        // ------------------------------------------------------------------ value model
        /// <summary>Raised after <see cref="Value"/> changed (user or code).</summary>
        [Category("Action"), Description("Raised after Value changed.")]
        public event EventHandler? ValueChanged;

        [Category("Behavior"), DefaultValue(0.0), Description("Lower end of the range.")]
        public double Minimum { get => _min; set { if (_min == value) return; _min = value; if (_max < _min) _max = _min; SyncPow2(); SetValue(_value, false); Redraw(); } }

        [Category("Behavior"), DefaultValue(100.0), Description("Upper end of the range.")]
        public double Maximum { get => _max; set { if (_max == value) return; _max = value; if (_min > _max) _min = _max; SyncPow2(); SetValue(_value, false); Redraw(); } }

        [Category("Behavior"), DefaultValue(0.0), Description("Current value (clamped to Minimum..Maximum; snapped to Step when Snap is set).")]
        public double Value { get => _value; set => SetValue(value, false); }

        [Category("Behavior"), DefaultValue(1.0), Description("Increment for the mouse wheel and the arrow keys; also the snapping grid when Snap is set.")]
        public double Step { get => _step; set { _step = value <= 0 ? 1 : value; } }

        /// <summary>Discrete values the control snaps to: sorted and de-duplicated automatically, every input (drag, wheel, keys, typed, code) picks the nearest entry, and the wheel / arrow keys move one entry at a time. When set, Snap / Step are ignored.</summary>
        [Category("Behavior"), Description("Discrete values to snap to (e.g. 2 4 6 8 10 12, or any predetermined set). Every value picks the nearest entry; the wheel and the arrow keys step through the list.")]
        public double[]? Values { get => _values; set { _values = Discrete.Norm(value); SetValue(_value, false); Redraw(); } }
        [Browsable(false)] public bool ShouldSerializeValues() => _values != null;
        /// <summary>Snap to the powers of two inside Minimum..Maximum (2 4 8 16 ...); the list follows range changes. Equivalent to assigning <see cref="Values"/>.</summary>
        [Category("Behavior"), DefaultValue(false), Description("Snap to the powers of two inside Minimum..Maximum (2 4 8 16 ...) instead of a continuous range.")]
        public bool PowersOfTwo
        {
            get => _pow2;
            set { _pow2 = value; _values = value ? Discrete.Pow2(_min, _max) : null; SetValue(_value, false); Redraw(); }
        }
        /// <summary>Rebuilds the powers-of-two list after a range change.</summary>
        void SyncPow2() { if (_pow2) _values = Discrete.Pow2(_min, _max); }

        [Category("Behavior"), DefaultValue(false), Description("Round the value to multiples of Step (relative to Minimum).")]
        public bool Snap { get => _snap; set { _snap = value; SetValue(_value, false); } }

        [Category("Behavior"), DefaultValue(0.0), Description("Value restored by a double click.")]
        public double ResetValue { get => _reset; set => _reset = value; }

        [Category("Appearance"), DefaultValue(0), Description("Decimal places of the value text.")]
        public int Decimals { get => _decimals; set { _decimals = Math.Clamp(value, 0, 6); Redraw(); } }

        [Category("Appearance"), DefaultValue(""), Description("Text appended to the value (e.g. \" %\", \" deg\").")]
        public string Unit { get => _unit; set { _unit = value ?? ""; Redraw(); } }

        [Category("Appearance"), DefaultValue(true), Description("Show the numeric value.")]
        public bool ShowValue { get => _showValue; set { _showValue = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(false), Description("Fill the track from the middle of the range (or from 0 when the range spans it) instead of from Minimum - for symmetric ranges like -100..100.")]
        public bool Bipolar { get => _bipolar; set { _bipolar = value; Redraw(); } }

        /// <summary>True while the user drags with the mouse.</summary>
        [Browsable(false)]
        public bool IsDragging => _dragging;

        /// <summary>0..1 position of the value inside the range. With a discrete <see cref="Values"/> list the entries are spread EVENLY (position by index, not by value).</summary>
        protected double Fraction => DiscreteFractionOf(Shown);
        /// <summary>0..1 position where the bipolar fill starts (0 when zero is outside the range).</summary>
        protected double ZeroFraction => !_bipolar ? 0 : (_min < 0 && _max > 0 ? -_min / (_max - _min) : 0.5);
        protected double FromFraction(double t) => DiscreteValueAt(t);
        /// <summary>The 0..1 position of <paramref name="v"/> (even spacing between the entries of a <see cref="Values"/> list).</summary>
        protected double DiscreteFractionOf(double v)
        {
            if (_max <= _min) return 0;
            if (_values is { Length: > 1 })
            {
                int idx = Discrete.IndexOf(_values, v);
                return idx / (double)(_values.Length - 1);
            }
            return (v - _min) / (_max - _min);
        }
        /// <summary>The value at 0..1 position <paramref name="t"/> (the nearest entry of a <see cref="Values"/> list; the list spacing is even).</summary>
        protected double DiscreteValueAt(double t)
        {
            if (_values is { Length: > 1 })
            {
                int idx = Math.Clamp((int)Math.Round(Math.Clamp(t, 0, 1) * (_values.Length - 1)), 0, _values.Length - 1);
                return _values[idx];
            }
            return _min + Math.Clamp(t, 0, 1) * (_max - _min);
        }
        /// <summary>The notch values to draw (the discrete list; otherwise the Snap + Step grid, capped): null = no notches.</summary>
        protected double[]? NotchValues()
        {
            if (_values is { Length: > 0 }) return _values;
            if (_snap && _step > 0 && (_max - _min) / _step is var n && n >= 1 && n <= 64)
            {
                var list = new double[(int)n + 1];
                for (int i = 0; i < list.Length; i++) list[i] = _min + i * _step;
                return list;
            }
            return null;
        }
        /// <summary>Show a tick for every discrete value (the <see cref="Values"/> list, or the Snap + Step grid).</summary>
        [Category("Appearance"), DefaultValue(false), Description("Show a tick for every discrete value (the Values list, or the Snap + Step grid when no list is set).")]
        public bool ShowNotches { get => _showNotches; set { _showNotches = value; Redraw(); } }
        bool _showNotches;
        /// <summary>Print the value next to every notch (too dense marks are skipped automatically).</summary>
        [Category("Appearance"), DefaultValue(false), Description("Print the value next to every notch (the notches that would overlap the numbers are skipped).")]
        public bool NotchLabels { get => _notchLabels; set { _notchLabels = value; Redraw(); } }
        bool _notchLabels;

        /// <summary>The value text as drawn (Decimals + Unit) - the pending value while a deferred drag is on.</summary>
        public string ValueText => Shown.ToString("F" + EffectiveDecimals, System.Globalization.CultureInfo.InvariantCulture) + _unit;
        /// <summary>Decimals the value text shows: the explicit <see cref="Decimals"/>, or - for a discrete control -
        /// what the value list actually needs ("1.5" must not read as "2").</summary>
        protected int EffectiveDecimals => _values is { Length: > 0 } ? Discrete.DecimalsOf(_values) : _decimals;

        // ------------------------------------------------------------------ deferred commit
        bool _commitOnRelease; double _pending = double.NaN;

        bool _rightCommits;
        /// <summary>The button that started the current drag (Left / Right); None outside a drag.</summary>
        protected MouseButtons DragButton { get; private set; }

        [Category("Behavior"), DefaultValue(false), Description("A drag with the RIGHT button commits on release (like CommitOnRelease) while the left button keeps applying live - both behaviours on one control without a mode switch. Double right click is not a reset.")]
        public bool RightButtonCommits { get => _rightCommits; set => _rightCommits = value; }

        [Category("Behavior"), DefaultValue(false), Description("Apply the value (and raise ValueChanged) when the mouse button is released, not while dragging. While the button is down the control previews the pending value (the slider thumb / knob body lifts, a ghost marks the committed value, the value text turns accent) and drops back on release; Escape during the drag cancels. Wheel, keys, double click and code changes apply at once.")]
        public bool CommitOnRelease { get => _commitOnRelease; set { _commitOnRelease = value; if (!value && HasPendingValue && !_dragging) { _pending = double.NaN; Redraw(); } } }

        /// <summary>Raised on every drag step in <see cref="CommitOnRelease"/> mode when <see cref="PendingValue"/> changed (a live read-out; the value itself is still the old one).</summary>
        [Category("Action"), Description("Raised while dragging in CommitOnRelease mode when the pending (previewed) value changed.")]
        public event EventHandler? ValuePreview;

        /// <summary>True while a <see cref="CommitOnRelease"/> drag previews a value that is not applied yet.</summary>
        [Browsable(false)] public bool HasPendingValue => !double.IsNaN(_pending);
        /// <summary>The value the picture shows: the pending one during a deferred drag, otherwise <see cref="Value"/>.</summary>
        [Browsable(false)] public double PendingValue => Shown;
        /// <summary>The value to paint (pending during a deferred drag, else the value).</summary>
        protected double Shown => double.IsNaN(_pending) ? _value : _pending;
        /// <summary>0..1 position of <see cref="Value"/> (the committed one) inside the range.</summary>
        protected double CommittedFraction => DiscreteFractionOf(_value);
        /// <summary>True while <see cref="SetValue"/> applies the value of a finished deferred drag (the picture already followed the mouse).</summary>
        protected bool IsCommittingDrag { get; private set; }
        /// <summary>Wheel-type controls wrap the value round the range instead of clamping (Maximum folds onto Minimum).</summary>
        protected virtual bool Wraps => false;
        /// <summary>Largest value the control accepts (a scroll bar stops at Maximum - PageSize).</summary>
        protected virtual double UpperLimit => _max;
        /// <summary>Take the keyboard focus when pressed (a scroll bar does not - the view it scrolls keeps it).</summary>
        protected virtual bool FocusOnPress => true;

        /// <summary>Snap / clamp (or wrap) a raw value the way <see cref="SetValue"/> does.</summary>
        protected double Constrain(double v)
        {
            if (_values is { Length: > 0 }) v = Discrete.Nearest(_values, v);          // the discrete list wins over the Step grid
            else if (_snap && _step > 0) v = _min + Math.Round((v - _min) / _step) * _step;
            if (Wraps && _max > _min) { double span = _max - _min; v = _min + (v - _min) - Math.Floor((v - _min) / span) * span; if (v >= _max) v = _min; return v; }
            return Math.Clamp(v, _min, Math.Max(_min, UpperLimit));
        }

        protected void SetValue(double v, bool fromUser)
        {
            if (double.IsNaN(v)) return;
            v = Constrain(v);
            if (v == _value) return;
            double old = _value;
            _value = v;
            OnValueApplied(old);
            ValueChanged?.Invoke(this, EventArgs.Empty);
            RepaintRequested = false;                                  // this repaint covers it
            if (fromUser && IsHandleCreated) RedrawNow(); else Redraw();
        }
        void SetPending(double v)
        {
            if (double.IsNaN(v)) return;
            v = Constrain(v);
            if (v == _pending) return;
            _pending = v;
            OnPendingChanged();
            ValuePreview?.Invoke(this, EventArgs.Empty);
            RepaintRequested = false;
            if (IsHandleCreated) RedrawNow(); else Redraw();
        }
        /// <summary>Apply a drag result: at once, or as the pending value in <see cref="CommitOnRelease"/> mode.</summary>
        void ApplyDrag(double v) { if (HasPendingValue) SetPending(v); else SetValue(v, true); }

        /// <summary>Called right after <see cref="Value"/> changed, before ValueChanged (the knob turns its free pointer here).</summary>
        protected virtual void OnValueApplied(double oldValue) { }
        /// <summary>Called when the pending (previewed) value of a deferred drag changed.</summary>
        protected virtual void OnPendingChanged() { }
        /// <summary>A drag began (<paramref name="deferred"/> = CommitOnRelease: the control lifts).</summary>
        protected virtual void OnDragStarted(bool deferred) { if (deferred) AnimateTo(1f, 120); }
        /// <summary>A drag ended: <paramref name="committed"/> = the pending value was applied (false = cancelled: Escape or lost capture); <paramref name="shown"/> = the value the picture showed last.</summary>
        protected virtual void OnDragEnded(bool committed, double shown) { AnimateTo(0f, 160); }
        /// <summary>0..1 how far the control is "lifted" (deferred drag in progress; eases back after release).</summary>
        protected float Lift => AnimT;

        // ------------------------------------------------------------------ input
        /// <summary>The value the pointer at <paramref name="p"/> (client coordinates) stands for; <paramref name="start"/> = where the drag began.</summary>
        protected abstract double ValueFromPointer(Point p, Point start, double startValue, bool fine);

        Point _dragStart; double _dragStartValue;

        /// <summary>The button of the drag that just ended (valid inside <see cref="OnDragEnded"/>).</summary>
        protected MouseButtons LastDragButton { get; private set; }
        /// <summary>True when <paramref name="b"/> may start a drag: the left button always, the right one with <see cref="RightButtonCommits"/>.</summary>
        protected bool IsDragButton(MouseButtons b) => b == MouseButtons.Left || (b == MouseButtons.Right && _rightCommits);
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_dragging || !IsDragButton(e.Button)) return;
            if (FocusOnPress) Focus();
            bool deferred = _commitOnRelease || e.Button == MouseButtons.Right;
            DragButton = e.Button;
            Capture = true; _dragging = true; _dragStart = e.Location; _dragStartValue = _value;
            _restCursor = Cursor; Cursor = SpriteCursors.Get(SpriteCursors.Kind.HandGrab, this);
            if (deferred) _pending = _value;
            OnDragStarted(deferred);
            ApplyDrag(ValueFromPointer(e.Location, _dragStart, _dragStartValue, (ModifierKeys & Keys.Shift) != 0));
            RedrawNow();
        }
        /// <summary>Set by <see cref="ValueFromPointer"/> when the picture changed although the value may not have (the infinite pointer past an end).</summary>
        protected bool RepaintRequested;
        Cursor? _restCursor;                    // cursor before the drag (open hand) restored on release
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            RepaintRequested = false;
            ApplyDrag(ValueFromPointer(e.Location, _dragStart, _dragStartValue, (ModifierKeys & Keys.Shift) != 0));
            if (RepaintRequested) { RepaintRequested = false; if (IsHandleCreated) RedrawNow(); else Redraw(); }
            OnPointerMoved();
        }
        /// <summary>After every drag step (value applied or not).</summary>
        protected virtual void OnPointerMoved() { }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging || e.Button != DragButton) return;
            _dragging = false; LastDragButton = DragButton; DragButton = MouseButtons.None;
            double shown = Shown;
            if (HasPendingValue)
            {
                double p = _pending; _pending = double.NaN;
                IsCommittingDrag = true;
                try { SetValue(p, true); } finally { IsCommittingDrag = false; }
            }
            OnDragEnded(true, shown);
            Capture = false; if (_restCursor != null) { Cursor = _restCursor; _restCursor = null; }
            RedrawNow();
        }
        /// <summary>Stops a drag without applying the pending value (Escape / lost capture).</summary>
        protected void CancelDrag()
        {
            if (!_dragging) return;
            _dragging = false; LastDragButton = DragButton; DragButton = MouseButtons.None;
            double shown = Shown; _pending = double.NaN;
            OnDragEnded(false, shown);
            Capture = false; if (_restCursor != null) { Cursor = _restCursor; _restCursor = null; }
            Redraw();
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); CancelDrag(); }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left) SetValue(_reset, true);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_dragging) return;                                     // no mixing with a drag in progress
            if (_values is { Length: > 0 }) SetValue(Discrete.Next(_values, _value, Math.Sign(e.Delta)), true);
            else { double s = (ModifierKeys & Keys.Shift) != 0 ? _step / 10 : _step; SetValue(_value + Math.Sign(e.Delta) * s, true); }
            if (e is HandledMouseEventArgs h) h.Handled = true;    // do not scroll the parent
        }
        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
        {
            Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
            Keys.Escape => _dragging,
            _ => base.IsInputKey(keyData),
        };
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { if (_dragging) { CancelDrag(); e.Handled = true; } return; }
            if (_dragging) return;
            double s = e.Shift ? _step / 10 : _step, page = (_max - _min) / 10;
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: SetValue(_values is { Length: > 0 } ? Discrete.Next(_values, _value, -1) : _value - s, true); break;
                case Keys.Right: case Keys.Up: SetValue(_values is { Length: > 0 } ? Discrete.Next(_values, _value, 1) : _value + s, true); break;
                case Keys.PageDown: SetValue(_value - page, true); break;
                case Keys.PageUp: SetValue(_value + page, true); break;
                case Keys.Home: SetValue(_min, true); break;
                case Keys.End: SetValue(_max, true); break;
                default: return;
            }
            e.Handled = true;
        }

    }

    /// <summary>How <see cref="SpriteKnob"/> maps a drag to a value.</summary>
    public enum KnobDragMode
    {
        /// <summary>
        /// The pointer IS the value: the knob turns to face the mouse, pressing jumps there, the further from the centre the
        /// finer. With the 270-degree gauge the 90-degree gap snaps to the nearer end.
        /// </summary>
        Angular,
        /// <summary>
        /// Endless: the value is wound over several turns. Pressing jumps the pointer to the mouse (as in Angular), then keep
        /// circling: every full turn adds <see cref="SpriteKnob.ValuePerTurn"/> (default = range / <see cref="SpriteKnob.Turns"/>)
        /// until Minimum / Maximum clamp. What the handle does at the ends is <see cref="SpriteKnob.Pointer"/>'s business.
        /// </summary>
        Endless,
    }

    /// <summary>The value display drawn around a <see cref="SpriteKnob"/> - independent of how the knob is dragged.</summary>
    public enum KnobGauge
    {
        /// <summary>270-degree C-shaped arc (7 o'clock over the top to 5 o'clock) filled from the start (or from zero when Bipolar) to the value.</summary>
        Arc,
        /// <summary>Full circle from 12 o'clock clockwise, filled once from Minimum to Maximum.</summary>
        Circle,
        /// <summary>One thin ring per turn (Endless: <see cref="SpriteKnob.Turns"/> rings, outermost first) - a clock being wound.</summary>
        Rings,
        /// <summary>A true spiral with <see cref="SpriteKnob.Turns"/> coils from the outside in, filled along its length from Minimum to Maximum.</summary>
        Spiral,
        /// <summary>No gauge - just the body and the pointer.</summary>
        None,
    }

    /// <summary>How the pointer (handle) of a <see cref="SpriteKnob"/> behaves.</summary>
    public enum KnobPointer
    {
        /// <summary>The pointer shows the value: it stops at Minimum / Maximum (Angular: on the gauge; Endless: at the end of the last turn).</summary>
        Bounded,
        /// <summary>The pointer follows the mouse for ever (a jog wheel): the gauge still stops at the ends, only the value clamps.</summary>
        Infinite,
    }

    /// <summary>
    /// Rotary knob: body + pointer, a value gauge around it, caption above and value below. Drawn entirely with SR2D shape
    /// calls (anti-aliased) on a <see cref="SpriteBox"/> surface. Three independent choices: <see cref="DragMode"/> (how the
    /// mouse turns it), <see cref="Gauge"/> (how the value is shown) and <see cref="Pointer"/> (whether the pointer stops at the ends).
    /// </summary>
    [ToolboxBitmap(typeof(SpriteKnob), "SpriteKnob.bmp")]
    public sealed class SpriteKnob : SpriteRangeControl
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Slider;
        KnobDragMode _mode = KnobDragMode.Angular;
        KnobGauge _gauge = KnobGauge.Arc; bool _gaugeSet;
        KnobPointer _pointer = KnobPointer.Bounded;
        double _perTurn; int _turns = 3;
        const float StartDeg = 135f, SweepDeg = 270f;      // Arc gauge, y-down screen angles: 135 = bottom-left, 270 = top, 405 = bottom-right
        const float TopDeg = 270f;                          // circular gauges start at 12 o'clock

        public SpriteKnob() { Size = new Size(96, 112); }

        [Category("Behavior"), DefaultValue(KnobDragMode.Angular), Description("Angular: the knob faces the pointer, press = jump. Endless: press = jump, then circle to wind the value over several turns (Turns / ValuePerTurn).")]
        public KnobDragMode DragMode { get => _mode; set { _mode = value; Redraw(); } }

        [Category("Appearance"), Description("The value display: Arc (270-degree C), Circle (one full turn), Rings (one ring per turn), Spiral (Turns coils), None. Default: Arc for Angular, Rings for Endless.")]
        public KnobGauge Gauge { get => _gaugeSet ? _gauge : (_mode == KnobDragMode.Endless ? KnobGauge.Rings : KnobGauge.Arc); set { _gauge = value; _gaugeSet = true; Redraw(); } }
        bool ShouldSerializeGauge() => _gaugeSet;
        void ResetGauge() { _gaugeSet = false; Redraw(); }

        [Category("Behavior"), DefaultValue(KnobPointer.Bounded), Description("Bounded: the pointer stops at Minimum / Maximum. Infinite: a jog wheel - the pointer turns with the hand for ever and the value follows it relatively (no jump on press); at an end the value and the gauge stop, the pointer keeps turning, and turning back moves the value at once.")]
        public KnobPointer Pointer { get => _pointer; set { _pointer = value; Redraw(); } }

        [Category("Behavior"), DefaultValue(3), Description("Number of turns from Minimum to Maximum: Endless winding (when ValuePerTurn is 0) and the coils of the Spiral / Rings gauges (1 = a plain 360-degree knob).")]
        public int Turns { get => _turns; set { _turns = Math.Max(1, value); Redraw(); } }

        [Category("Behavior"), DefaultValue(0.0), Description("Endless mode: value change per full turn of the pointer (0 = (Maximum - Minimum) / Turns).")]
        public double ValuePerTurn { get => _perTurn; set { _perTurn = Math.Max(0, value); Redraw(); } }

        double PerTurn => _perTurn > 0 ? _perTurn : (Maximum - Minimum) / _turns;
        double FractionOf(double v) => DiscreteFractionOf(v);               // even spacing between the entries of a Values list
        double EndlessAngleOf(double v) => (v - Minimum) / PerTurn * 2 * Math.PI;
        /// <summary>Endless: total pointer angle (radians, 0 = 12 o'clock, clockwise) that represents the shown value.</summary>
        double EndlessAngle => EndlessAngleOf(Shown);
        /// <summary>Number of turns the gauge spans (Rings / Spiral): Turns, or what the range needs in Endless mode.</summary>
        int GaugeTurns => _mode == KnobDragMode.Endless ? Math.Max(1, (int)Math.Ceiling((Maximum - Minimum) / PerTurn - 1e-9)) : _turns;
        /// <summary>Turns wound so far on a multi-turn gauge (Endless: the real pointer turns, so gauge and pointer agree; Angular: the value spread over Turns).</summary>
        double GaugeWound => _mode == KnobDragMode.Endless ? EndlessAngle / (2 * Math.PI) : Fraction * GaugeTurns;

        /// <summary>Value change per full turn of the pointer (what the infinite pointer's relative motion is worth).</summary>
        double GainPerTurn => _mode == KnobDragMode.Endless ? PerTurn
            : (Gauge == KnobGauge.Arc || Gauge == KnobGauge.None) ? (Maximum - Minimum) * 360.0 / SweepDeg
            : (Maximum - Minimum);

        // endless / infinite drag: previous pointer angle (screen atan2) to unwrap the motion; free = the infinite pointer's
        // own angle (screen radians), an accumulator that follows the hand for ever - beyond the ends too, where the value
        // no longer follows. Value changes from elsewhere (wheel, keys, code) turn it by the same amount.
        double _lastAngle, _free = double.NaN;
        bool _press, _mouseDriven;
        protected override void OnPointerMoved() => _mouseDriven = false;
        protected override void OnValueApplied(double oldValue)
        {
            if (_mouseDriven || IsCommittingDrag) { _mouseDriven = false; return; }       // the drag already moved the pointer
            if (_pointer == KnobPointer.Infinite && !double.IsNaN(_free)) _free += (Value - oldValue) / GainPerTurn * 2 * Math.PI;
        }

        // layout: caption line (if Text) on top, value line (if ShowValue) at the bottom, knob fills the rest
        (float cx, float cy, float r, int capH, int valH, int ts) Geometry()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            int ts = AutoTextScale(Math.Min(w, h));
            int capH = string.IsNullOrEmpty(Text) ? 0 : LineHeight(ts);
            int valH = ShowValue ? LineHeight(ts) : 0;
            float r = Math.Max(4, Math.Min(w, h - capH - valH) / 2f - 2);
            return (w / 2f, capH + (h - capH - valH) / 2f, r, capH, valH, ts);
        }

        // the pointer angle (radians, screen convention: 0 = 3 o'clock, clockwise) for a value / the shown value
        double AngleOf(double v) => _mode == KnobDragMode.Angular
            ? (Gauge == KnobGauge.Arc || Gauge == KnobGauge.None ? (StartDeg + SweepDeg * FractionOf(v)) : (TopDeg + 360 * FractionOf(v))) * Math.PI / 180   // matches ValueFromPointer
            : TopDeg * Math.PI / 180 + EndlessAngleOf(v);
        double ValueAngle => AngleOf(Shown);
        double PointerAngle => _pointer == KnobPointer.Infinite && !double.IsNaN(_free) ? _free : ValueAngle;
        /// <summary>Angle of the pointer as drawn, degrees clockwise from 3 o'clock (the infinite pointer keeps turning past the ends).</summary>
        [Browsable(false)] public double PointerDegrees => PointerAngle * 180 / Math.PI;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!IsDragging && IsDragButton(e.Button) && (_mode == KnobDragMode.Endless || _pointer == KnobPointer.Infinite))
            {
                var (cx, cy, _, _, _, _) = Geometry();
                _lastAngle = Math.Atan2(e.Y - cy, e.X - cx);
                if (_pointer == KnobPointer.Infinite)
                {
                    // jog wheel: the press changes nothing, the pointer starts where it is (or where the value puts it)
                    if (double.IsNaN(_free)) _free = ValueAngle;
                    _press = true;
                }
                else if (_mode == KnobDragMode.Endless)
                {
                    // press = jump: the pointer of the CURRENT turn moves to the mouse (shortest way), the completed turns stay
                    double cur = EndlessAngle;                                       // 0 = top, clockwise
                    double want = ((_lastAngle * 180 / Math.PI - TopDeg) % 360 + 360) % 360 * Math.PI / 180;   // 0..2pi from the top
                    double frac = cur - Math.Floor(cur / (2 * Math.PI)) * 2 * Math.PI;
                    double d = want - frac; if (d > Math.PI) d -= 2 * Math.PI; else if (d < -Math.PI) d += 2 * Math.PI;
                    _pressJump = cur + d;                                            // handled by ValueFromPointer(start) below
                }
            }
            base.OnMouseDown(e);
        }
        double _pressJump = double.NaN;

        protected override double ValueFromPointer(Point p, Point start, double startValue, bool fine)
        {
            var (cx, cy, _, _, _, _) = Geometry();
            double a = Math.Atan2(p.Y - cy, p.X - cx);
            _mouseDriven = true;                  // the drag moves the free pointer itself - OnValueApplied must not turn it again
            if (_pointer == KnobPointer.Infinite || _mode == KnobDragMode.Endless)
            {
                // relative: unwrap the hand's motion, the pointer takes all of it, the value its share (clamped, so the
                // gauge stops at the ends while the pointer goes on; turning back moves the value immediately)
                if (_press) { _press = false; _lastAngle = a; return Shown; }
                double total;
                if (!double.IsNaN(_pressJump)) { total = _pressJump; _pressJump = double.NaN; _lastAngle = a; }   // the press
                else
                {
                    double d = a - _lastAngle;
                    if (d > Math.PI) d -= 2 * Math.PI; else if (d < -Math.PI) d += 2 * Math.PI;   // unwrap: shortest way round
                    _lastAngle = a;
                    if (_pointer == KnobPointer.Infinite && !double.IsNaN(_free)) _free += d * (fine ? 0.1 : 1.0);   // the pointer takes all of the motion - beyond the ends too
                    total = (Shown - Minimum) / GainPerTurn * (2 * Math.PI) + d * (fine ? 0.1 : 1.0);   // relative to the shown value, in GainPerTurn units
                }
                // clamp in value space; because the angle is re-derived from the clamped value every move, turning past
                // an end and back reacts immediately (no dead travel)
                return Math.Clamp(Minimum + total / (2 * Math.PI) * GainPerTurn, Minimum, Maximum);
            }
            double deg = a * 180 / Math.PI;                                       // -180..180, y down
            if (Gauge == KnobGauge.Arc || Gauge == KnobGauge.None)
            {
                double rel = ((deg - StartDeg) % 360 + 360) % 360;                // 0 at the start of the arc, clockwise
                double t = rel <= SweepDeg ? rel / SweepDeg : (rel < SweepDeg + (360 - SweepDeg) / 2 ? 1 : 0);   // the gap snaps to the nearer end
                return FromFraction(t);
            }
            else
            {
                // full-circle gauges in Angular mode: one turn from the top = the whole range; the seam at 12 o'clock is
                // crossed only by the mouse going round, so the value cannot jump between the ends by itself
                double rel = ((deg - TopDeg) % 360 + 360) % 360;
                return FromFraction(rel / 360);
            }
        }

        protected override void PaintControl(Sprite s)
        {
            var (cx, cy, r, capH, valH, ts) = Geometry();
            float trackW = Math.Max(2f, r * 0.16f);
            float br;                                                              // body radius
            int maxN = (int)(r * 2 * MathF.PI / 3f) + 2;
            switch (Gauge)
            {
                case KnobGauge.Arc:
                {
                    float tr = r - trackW / 2;
                    int segs = Math.Max(12, (int)(tr * SweepDeg / 180f * MathF.PI / 3f));
                    Span<PointF> pts = segs + 1 <= 1024 ? stackalloc PointF[segs + 1] : new PointF[segs + 1];   // huge knobs: heap, not stack
                    for (int i = 0; i <= segs; i++) pts[i] = ArcPt(cx, cy, tr, StartDeg + SweepDeg * i / segs);
                    s.DrawPolyline(pts, Track, trackW, true, false, SR2D.LineOp.Set, true);
                    double f0 = ZeroFraction, f1 = Fraction;
                    if (f1 < f0) (f0, f1) = (f1, f0);
                    if (f1 > f0)
                    {
                        int n = Math.Max(2, (int)Math.Ceiling(segs * (f1 - f0)));
                        Span<PointF> v = n + 1 <= 1024 ? stackalloc PointF[n + 1] : new PointF[n + 1];
                        for (int i = 0; i <= n; i++) v[i] = ArcPt(cx, cy, tr, (float)(StartDeg + SweepDeg * (f0 + (f1 - f0) * i / n)));
                        s.DrawPolyline(v, Accent, trackW, true, false, SR2D.LineOp.Set, true);
                    }
                    br = r - trackW * 1.7f;
                    break;
                }
                case KnobGauge.Circle:
                {
                    // one full turn from 12 o'clock = Minimum .. Maximum (whatever the drag mode does with turns)
                    float tr = r - trackW / 2;
                    s.DrawCircle(cx, cy, tr, Track, trackW, true);
                    double f0 = ZeroFraction, f1 = Fraction;
                    if (f1 < f0) (f0, f1) = (f1, f0);
                    if (f1 > f0)
                    {
                        Span<PointF> v = maxN + 1 <= 1024 ? stackalloc PointF[maxN + 1] : new PointF[maxN + 1];
                        int n = Math.Clamp((int)(tr * 2 * MathF.PI * (f1 - f0) / 3f), 2, maxN);
                        for (int i = 0; i <= n; i++) v[i] = ArcPt(cx, cy, tr, (float)(TopDeg + 360 * (f0 + (f1 - f0) * i / n)));
                        s.DrawPolyline(v.Slice(0, n + 1), Accent, trackW, true, false, SR2D.LineOp.Set, true);
                    }
                    br = r - trackW * 1.7f;
                    break;
                }
                case KnobGauge.Rings:
                {
                    // one thin ring per turn (outermost = first turn), completed turns fully lit, the current turn lit from the top
                    int turns = GaugeTurns;
                    float ringW = Math.Max(1.5f, Math.Min(trackW, (r * 0.45f) / turns));
                    double total = GaugeWound;                                     // turns wound
                    int done = (int)Math.Floor(total + 1e-9); double frac = total - done;
                    Span<PointF> v = maxN + 1 <= 1024 ? stackalloc PointF[maxN + 1] : new PointF[maxN + 1];
                    for (int k = 0; k < turns; k++)
                    {
                        float rr = r - ringW / 2 - k * (ringW + 1);
                        s.DrawCircle(cx, cy, rr, Track, ringW, true);
                        double lit = k < done ? 1 : k == done ? frac : 0;
                        if (lit <= 0) continue;
                        if (lit >= 0.999) { s.DrawCircle(cx, cy, rr, Accent, ringW, true); continue; }
                        int n = Math.Clamp((int)(rr * 2 * MathF.PI * lit / 3f), 2, maxN);
                        for (int i = 0; i <= n; i++) v[i] = ArcPt(cx, cy, rr, (float)(TopDeg + 360 * lit * i / n));
                        s.DrawPolyline(v.Slice(0, n + 1), Accent, ringW, true, false, SR2D.LineOp.Set, true);
                    }
                    br = r - turns * (ringW + 1) - ringW * 0.8f;
                    if (br < r * 0.35f) br = r * 0.35f;
                    break;
                }
                case KnobGauge.Spiral:
                {
                    // Archimedean spiral, Turns coils from the outer radius inwards, 12 o'clock start; the whole length is
                    // Minimum..Maximum. Constant-arc-length steps keep the polyline smooth on the inner coils.
                    int turns = GaugeTurns;
                    float coilW = Math.Max(1.5f, Math.Min(trackW, (r * 0.5f) / (turns + 0.5f)));
                    float pitch = coilW + Math.Max(1f, coilW * 0.6f);              // radial distance between coils
                    float r0 = r - coilW / 2, r1 = Math.Max(coilW, r0 - pitch * turns);
                    double totalAng = 2 * Math.PI * turns;
                    int n = Math.Clamp((int)(turns * (r0 + r1) * MathF.PI / 3f), 8, 2048);
                    var pts = new PointF[n + 1];
                    for (int i = 0; i <= n; i++) { double t = (double)i / n, sa = TopDeg * Math.PI / 180 + totalAng * t; float rr = (float)(r0 - (r0 - r1) * t); pts[i] = new PointF(cx + (float)Math.Cos(sa) * rr, cy + (float)Math.Sin(sa) * rr); }
                    s.DrawPolyline(pts, Track, coilW, true, false, SR2D.LineOp.Set, true);
                    double f = Math.Clamp(GaugeWound / turns, 0, 1); int m = (int)Math.Round(n * f);
                    if (m >= 1) s.DrawPolyline(new ReadOnlySpan<PointF>(pts, 0, m + 1), Accent, coilW, true, false, SR2D.LineOp.Set, true);
                    // a dot at the value's position on the spiral (the pointer's angle no longer tells where along the coils we are)
                    if (f > 0 && f < 1) s.FillCircle(pts[m].X, pts[m].Y, coilW * 0.9f, Mix(BackColor, ThumbColor, 0.95f), SR2D.LineOp.Set, true);
                    br = r1 - coilW * 1.2f;
                    if (br < r * 0.28f) br = r * 0.28f;
                    break;
                }
                default: br = r - 1; break;
            }
            // body: dark disc, light rim, a subtle highlight disc off-centre; pointer on top. A deferred drag (CommitOnRelease)
            // lifts the body towards the viewer: it grows a little, a shadow appears below-right, a thin ghost pointer stays at
            // the committed value; on release it drops back (Lift eases to 0).
            float lift = Lift;
            if (lift > 0.01f)
            {
                float sh = Math.Max(2f, r * 0.08f) * lift;
                s.FillCircle(cx + sh * 0.7f, cy + sh, br * (1 + 0.06f * lift) + 1, Argb(Black, (int)(120 * lift)), SR2D.LineOp.AlphaBlend, true);
                br *= 1 + 0.06f * lift;
            }
            PaintBody(s, cx, cy, br, IsDragging, Math.Max(1f, r * 0.04f));
            if (HasPendingValue && _pointer == KnobPointer.Bounded && Math.Abs(Shown - Value) > 1e-9)
            {   // ghost of the committed value
                float ga = (float)AngleOf(Value);
                s.DrawWideLine(cx + MathF.Cos(ga) * br * 0.35f, cy + MathF.Sin(ga) * br * 0.35f, cx + MathF.Cos(ga) * br * 0.85f, cy + MathF.Sin(ga) * br * 0.85f, Mix(BackColor, AccentRaw, 0.45f), Math.Max(1f, r * 0.05f), true, SR2D.LineOp.Set, true);
            }
            float ang = (float)PointerAngle;
            var p0 = new PointF(cx + MathF.Cos(ang) * br * 0.35f, cy + MathF.Sin(ang) * br * 0.35f);
            var p1 = new PointF(cx + MathF.Cos(ang) * br * 0.85f, cy + MathF.Sin(ang) * br * 0.85f);
            s.DrawWideLine(p0.X, p0.Y, p1.X, p1.Y, Accent, Math.Max(2f, r * 0.12f), true, SR2D.LineOp.Set, true);
            if (_pointer == KnobPointer.Infinite)
            {   // jog wheel: three faint dots around the body so the free spin is visible even at the ends
                for (int k = 0; k < 3; k++) { float a2 = ang + k * MathF.PI * 2 / 3; s.FillCircle(cx + MathF.Cos(a2) * br * 0.7f, cy + MathF.Sin(a2) * br * 0.7f, Math.Max(1f, br * 0.06f), Mix(BackColor, ThumbColor, 0.6f), SR2D.LineOp.Set, true); }
            }
            if (Gauge == KnobGauge.Circle || Gauge == KnobGauge.Rings || Gauge == KnobGauge.Spiral)
            {   // tick at 12 o'clock = start of every turn
                s.DrawWideLine(cx, cy - br - 1, cx, cy - br + Math.Max(3f, br * 0.15f), Mix(BackColor, ThumbColor, 0.8f), Math.Max(1f, r * 0.04f), true);
            }
            var notches = ShowNotches ? NotchValues() : null;
            if (notches is { Length: > 1 })
            {   // a tick around the body per discrete value (at the value's angle), optional numbers outside
                int tick = Mix(BackColor, AccentRaw, 0.35f);
                float inner = br + 2f, outer = br + 2f + Math.Max(3f, r * 0.1f);
                bool labels = NotchLabels;
                int ls = 1;                                                        // the smallest pixel font, printed INSIDE the body (nothing clips outside the control)
                float lr = br * 0.78f;
                float lastLabelAng = -999f;
                for (int i = 0; i < notches.Length; i++)
                {
                    float na = (float)AngleOf(notches[i]);
                    bool edge = i == 0 || i == notches.Length - 1;
                    s.DrawWideLine(cx + MathF.Cos(na) * inner, cy + MathF.Sin(na) * inner, cx + MathF.Cos(na) * (edge ? outer + 2 : outer), cy + MathF.Sin(na) * (edge ? outer + 2 : outer), tick, 1f, true);
                    if (labels && Math.Abs(na - lastLabelAng) > 0.3f)                  // ~17 degrees at scale 1: skip marks that would overlap
                    {
                        string txt = notches[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        T.Draw(s, (int)(cx + MathF.Cos(na) * lr), (int)(cy + MathF.Sin(na) * lr), txt, Mix(Fore, BackColor, 0.15f), 0, ls, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
                        lastLabelAng = na;
                    }
                }
            }
            // texts
            if (capH > 0) { int cs = FitScale(Text, ClientSize.Width - 2, ts); T.Draw(s, (int)cx, 1 + (capH - LineHeight(cs)) / 2, FitText(Text, ClientSize.Width - 2, cs), Fore, 0, cs, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopCenter); }
            if (valH > 0) { int vs = FitScale(ValueText, ClientSize.Width - 2, ts); T.Draw(s, (int)cx, ClientSize.Height - 1, ValueText, HasPendingValue ? Accent : Fore, 0, vs, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.BottomCenter); }
        }
        static PointF ArcPt(float cx, float cy, float r, float deg) { float a = deg * MathF.PI / 180f; return new PointF(cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r); }
    }

    /// <summary>
    /// Straight slider (horizontal or vertical): rounded track, accent fill, round thumb, optional ticks, caption and
    /// value text. Same interaction model as <see cref="SpriteKnob"/>: the thumb is always exactly under the pointer.
    /// </summary>
    [ToolboxBitmap(typeof(SpriteSlider), "SpriteSlider.bmp")]
    public class SpriteSlider : SpriteRangeControl
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Slider;
        Orientation _orient = Orientation.Horizontal;
        int _ticks;

        public SpriteSlider() { Size = new Size(220, 40); }

        [Category("Behavior"), DefaultValue(Orientation.Horizontal), Description("Horizontal (value grows to the right) or vertical (value grows upwards).")]
        public Orientation Orientation { get => _orient; set { _orient = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(0), Description("Number of tick intervals drawn along the track (0 = none).")]
        public int Ticks { get => _ticks; set { _ticks = Math.Max(0, value); Redraw(); } }

        // geometry: track from a to b along the main axis at cross position m; thumb radius tr
        (float a, float b, float m, float tr, int ts, Rectangle textArea) Geometry()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            bool hz = _orient == Orientation.Horizontal;
            int ts = AutoTextScale(hz ? h * 3 / 2 : w * 2);
            int textH = T.LineHeight(ts);
            if (hz)
            {
                bool cap = !string.IsNullOrEmpty(Text) || ShowValue;
                int top = cap ? textH + 3 : 0;
                float tr = Math.Clamp((h - top) * 0.36f, 4f, 14f);
                return (tr + 2, w - tr - 3, top + (h - top) / 2f, tr, ts, new Rectangle(0, 0, w, top));
            }
            else
            {
                int capH = string.IsNullOrEmpty(Text) ? 0 : textH + 2, valH = ShowValue ? textH + 2 : 0;
                float tr = Math.Clamp(w * 0.36f, 4f, 14f);
                return (h - valH - tr - 3, capH + tr + 2, w / 2f, tr, ts, new Rectangle(0, capH, w, h - capH - valH));   // a = bottom (min), b = top (max)
            }
        }

        protected override double ValueFromPointer(Point p, Point start, double startValue, bool fine)
        {
            var (a, b, _, _, _, _) = Geometry();
            float q = _orient == Orientation.Horizontal ? p.X : p.Y;
            double t = b != a ? (q - a) / (double)(b - a) : 0;
            if (fine) t = Fraction + (t - Fraction) * 0.1;     // Shift: the pointer offset counts 1/10 (from the current value)
            return FromFraction(t);
        }

        protected override void PaintControl(Sprite s)
        {
            var (a, b, m, tr, ts, textArea) = Geometry();
            bool hz = _orient == Orientation.Horizontal;
            float th = Math.Max(2f, tr * 0.5f);                 // track thickness
            // fill: zero .. committed value solid, committed .. pending (deferred drag) in a faded accent
            float f0 = (float)ZeroFraction, fc = (float)CommittedFraction, fp = (float)Fraction;
            float qc = a + (b - a) * fc, qp = a + (b - a) * fp, tq = qp;
            Bar(s, a, b, m, th, Track, hz);
            { float q0 = Math.Min(a + (b - a) * f0, qc), q1 = Math.Max(a + (b - a) * f0, qc); if (q1 - q0 > 0.5f) Bar(s, q0, q1, m, th, Accent, hz); }
            if (Math.Abs(qp - qc) > 0.5f) Bar(s, Math.Min(qc, qp), Math.Max(qc, qp), m, th, Mix(Accent, Track, 0.5f), hz);
            if (_ticks > 0)
            {
                int tick = Mix(BackColor, ForeColor, 0.45f);
                for (int i = 0; i <= _ticks; i++)
                {
                    float q = a + (b - a) * i / _ticks;
                    if (hz) s.DrawWideLine(q, m + tr + 1, q, m + tr + 4, tick, 1f, true);
                    else s.DrawWideLine(m + tr + 1, q, m + tr + 4, q, tick, 1f, true);
                }
            }
            var notches = ShowNotches ? NotchValues() : null;
            if (notches is { Length: > 0 })
            {
                int tick = Mix(BackColor, AccentRaw, 0.35f);
                float gap = (b - a) / Math.Max(1, notches.Length - 1);
                int ls = 1;                                                        // the smallest pixel font - labels must never dwarf the track
                bool labels = NotchLabels && gap >= 5 * 3;                         // at scale 1 a digit is ~5 px wide: skip marks that would overlap
                if (labels && hz && m + tr + 5 + LineHeight(ls) > ClientSize.Height) labels = false;    // never paint outside the box
                if (labels && !hz && m + tr + 5 + 3 * 4 > ClientSize.Width) labels = false;
                for (int i = 0; i < notches.Length; i++)
                {
                    float q = a + (b - a) * (float)DiscreteFractionOf(notches[i]);
                    bool edge = i == 0 || i == notches.Length - 1;
                    if (hz) s.DrawWideLine(q, m + tr + 1, q, m + tr + (edge ? 4 : 3), tick, 1f, true);
                    else s.DrawWideLine(m + tr + 1, q, m + tr + (edge ? 4 : 3), q, tick, 1f, true);
                    if (labels)
                    {
                        string txt = notches[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        if (hz) T.Draw(s, (int)q, (int)(m + tr + 4), txt, Mix(Fore, AccentRaw, 0.4f), 0, ls, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopCenter);
                        else T.Draw(s, (int)(m + tr + 4), (int)q, txt, Mix(Fore, AccentRaw, 0.4f), 0, ls, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
                    }
                }
            }
            // thumb: disc with rim (brighter while dragging), small accent dot. A deferred drag lifts it off the track
            // (shadow below-right, a little bigger) and leaves a ghost ring at the committed value; release drops it back.
            float lift = Lift, tx = hz ? tq : m, ty = hz ? m : tq;
            if (HasPendingValue && Math.Abs(qp - qc) > 0.5f)
                s.DrawCircle(hz ? qc : m, hz ? m : qc, tr * 0.8f, Mix(Track, ThumbColor, 0.5f), 1.5f, true);
            if (lift > 0.01f)
            {
                float up = Math.Max(2f, tr * 0.4f) * lift, tr2 = tr * (1 + 0.15f * lift);
                s.FillCircle(tx + up * 0.6f, ty + up * 0.9f, tr2, Argb(Black, (int)(110 * lift)), SR2D.LineOp.AlphaBlend, true);
                if (hz) ty -= up; else tx -= up;
                tr = tr2;
            }
            s.FillCircle(tx, ty, tr, Thumb, SR2D.LineOp.Set, true);
            s.DrawCircle(tx, ty, tr, IsDragging ? Accent : Mix(ThumbColor, BackColor, 0.4f), 1.5f, true);
            s.FillCircle(tx, ty, tr * 0.3f, Accent, SR2D.LineOp.Set, true);
            // texts
            if (hz)
            {
                // the value fits its half of the strip BY CONSTRUCTION (FitScale, like the knob draws it) - a long
                // value or a narrow control scales it down instead of colliding with the caption or the edge
                int vs = ShowValue ? Math.Max(1, FitScale(ValueText, Math.Max(30, ClientSize.Width / 3), ts)) : ts;
                int vw = ShowValue ? T.Measure(ValueText, vs).Width + 8 : 0;
                if (!string.IsNullOrEmpty(Text)) { int cs = FitScale(Text, ClientSize.Width - 4 - vw, ts); T.Draw(s, 2, textArea.Top + 1, FitText(Text, ClientSize.Width - 4 - vw, cs), Fore, 0, cs); }
                if (ShowValue) T.Draw(s, ClientSize.Width - 2, textArea.Top + 1, ValueText, HasPendingValue ? Accent : Fore, 0, vs, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopRight);
            }
            else
            {
                if (!string.IsNullOrEmpty(Text)) { int cs = FitScale(Text, ClientSize.Width - 2, ts); T.Draw(s, (int)m, 1, FitText(Text, ClientSize.Width - 2, cs), Fore, 0, cs, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopCenter); }
                if (ShowValue) T.Draw(s, (int)m, ClientSize.Height - 1, ValueText, HasPendingValue ? Accent : Fore, 0, FitScale(ValueText, ClientSize.Width - 2, ts), 0, 0, SR2D.LineOp.Set, 128, TextAnchor.BottomCenter);
            }
        }

        // rounded bar from q0 to q1 along the axis (round caps via DrawWideLine)
        static void Bar(Sprite s, float q0, float q1, float m, float th, int c, bool hz)
        {
            if (hz) s.DrawWideLine(q0, m, q1, m, c, th, true, SR2D.LineOp.Set, true);
            else s.DrawWideLine(m, q0, m, q1, c, th, true, SR2D.LineOp.Set, true);
        }
    }
}
