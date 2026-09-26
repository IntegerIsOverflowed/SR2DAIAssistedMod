// SpriteControls.Input.cs - text and list input in the SR2D control look: SpriteTextBox (single-line editor with caret,
// selection, clipboard), SpriteNumeric (a text box with spin buttons and a numeric Value), SpriteCombo (a drop-down list
// backed by SpriteMenu) and SpriteListBox (a scrollable list with single / multi selection and optional check boxes).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // ImplicitUsings also brings System.Threading.Timer

namespace Sr2d64CSport
{
    /// <summary>
    /// Single-line text editor drawn with SR2D: pixel-font text, blinking caret, mouse / Shift selection, double click selects
    /// a word, Ctrl+A / C / X / V, Home / End, Ctrl+Left / Right by word, <see cref="Placeholder"/> when empty,
    /// <see cref="PasswordChar"/>, <see cref="ReadOnly"/>, <see cref="MaxLength"/>. Enter raises <see cref="Committed"/>
    /// (and Escape restores the text from the last commit) so a form can react to "done" like a NumericUpDown.
    /// </summary>
    internal class SpriteTextBox : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Text;
        int _caret, _anchor, _scroll, _maxLen; bool _ro, _caretOn = true, _dragging; char _pw; string _placeholder = "", _committed = "";
        HorizontalAlignment _align = HorizontalAlignment.Left;
        Timer? _blink;

        public SpriteTextBox()
        {
            Cursor = Cursors.IBeam;
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            Size = new Size(140, 24);
        }

        [Category("Action"), Description("Enter was pressed (or the control lost the focus) with a text different from the last committed one.")]
        public event EventHandler? Committed;

        [Browsable(true), EditorBrowsable(EditorBrowsableState.Always), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible), Bindable(true)]
        [AllowNull]
        public override string Text
        {
            get => base.Text;
            set { value ??= ""; if (_maxLen > 0 && value.Length > _maxLen) value = value.Substring(0, _maxLen); base.Text = value; _committed = value; _caret = _anchor = Math.Min(_caret, value.Length); EnsureVisible(); Redraw(); }
        }

        [Category("Appearance"), DefaultValue(""), Description("Dimmed text shown while the box is empty.")]
        public string Placeholder { get => _placeholder; set { _placeholder = value ?? ""; Redraw(); } }

        [Category("Behavior"), DefaultValue(false)]
        public bool ReadOnly { get => _ro; set { _ro = value; Redraw(); } }

        [Category("Behavior"), DefaultValue(0), Description("Maximum text length (0 = unlimited).")]
        public int MaxLength { get => _maxLen; set { _maxLen = Math.Max(0, value); } }

        [Category("Behavior"), DefaultValue('\0'), Description("Show this character instead of the text (a password field).")]
        public char PasswordChar { get => _pw; set { _pw = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(HorizontalAlignment.Left)]
        public HorizontalAlignment TextAlign { get => _align; set { _align = value; Redraw(); } }

        [Browsable(false)] public int SelectionStart => Math.Min(_caret, _anchor);
        [Browsable(false)] public int SelectionLength => Math.Abs(_caret - _anchor);
        [Browsable(false)] public string SelectedText => Text.Substring(SelectionStart, SelectionLength);
        [Browsable(false)] public int CaretIndex => _caret;

        public void Select(int start, int length) { start = Math.Clamp(start, 0, Text.Length); _anchor = start; _caret = Math.Clamp(start + length, 0, Text.Length); EnsureVisible(); Redraw(); }
        public void SelectAll() => Select(0, Text.Length);
        /// <summary>Replaces the selection (or inserts at the caret) as typing would.</summary>
        public void InsertText(string s)
        {
            s ??= "";
            if (_ro || (s.Length == 0 && SelectionLength == 0)) return;     // "" with a selection = delete the selection (Backspace / Delete / Cut)
            s = s.Replace("\r", "").Replace("\n", " ");
            string t = Text; int a = SelectionStart, n = SelectionLength;
            if (_maxLen > 0) { int room = _maxLen - (t.Length - n); if (room <= 0) return; if (s.Length > room) s = s.Substring(0, room); }
            t = t.Remove(a, n).Insert(a, s);
            SetTextInternal(t, a + s.Length);
        }
        void SetTextInternal(string t, int caret)
        {
            base.Text = t; _caret = _anchor = Math.Clamp(caret, 0, t.Length);
            EnsureVisible(); ShowCaret(); Redraw();
        }
        /// <summary>Raises Committed when the text differs from the last commit (Enter / focus loss).</summary>
        public void Commit()
        {
            if (Text == _committed) return;
            _committed = Text; OnCommitted();
        }
        protected virtual void OnCommitted() => Committed?.Invoke(this, EventArgs.Empty);
        /// <summary>Text width of the right-hand extra (spin buttons of a numeric box); 0 here.</summary>
        protected virtual int RightExtra => 0;
        /// <summary>Height taken at the bottom (the numeric box's spinner under the field); 0 here.</summary>
        protected virtual int BottomExtra => 0;
        /// <summary>True when <paramref name="p"/> is on a part that is not text (the spinner): the I-beam gives way to the arrow and clicks do not place the caret.</summary>
        protected virtual bool IsExtraAt(Point p) => RightExtra > 0 && p.X >= ClientSize.Width - RightExtra;
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) { _caret = IndexAt(e.X, Sc); EnsureVisible(); ShowCaret(); Redraw(); return; }
            var want = IsExtraAt(e.Location) ? Cursors.Default : Cursors.IBeam;
            if (Cursor != want) Cursor = want;
        }

        // ------------------------------------------------------------------ geometry
        /// <summary>Height of the text field (the whole control, minus the spinner row when it sits underneath).</summary>
        protected int FieldH => Math.Max(8, ClientSize.Height - BottomExtra);
        protected int Sc => AutoTextScale(FieldH * 4);
        protected Rectangle TextArea { get { int sc = Sc, pad = 4 * sc + 2; return new Rectangle(pad, 0, Math.Max(1, ClientSize.Width - pad * 2 - RightExtra), FieldH); } }
        string Shown => _pw != '\0' ? new string(_pw, Text.Length) : Text;
        int TextW(int sc) { var t = Shown; return t.Length == 0 ? 0 : T.Measure(t, sc).Width; }
        int XOf(int index, int sc)
        {
            var ta = TextArea; int tw = TextW(sc), start = ta.X;
            if (tw < ta.Width) start = _align == HorizontalAlignment.Center ? ta.X + (ta.Width - tw) / 2 : _align == HorizontalAlignment.Right ? ta.Right - tw : ta.X;
            return start - _scroll + T.CharX(Shown, index, sc);
        }
        int IndexAt(int x, int sc) => T.IndexAt(Shown, x - XOf(0, sc), sc);
        void EnsureVisible()
        {
            int sc = Sc; var ta = TextArea; int tw = TextW(sc);
            if (tw <= ta.Width) { _scroll = 0; return; }
            _scroll = Math.Clamp(_scroll, 0, tw - ta.Width);
            int cx = XOf(_caret, sc);
            if (cx < ta.X) _scroll -= ta.X - cx; else if (cx > ta.Right - sc) _scroll += cx - (ta.Right - sc);
        }

        // ------------------------------------------------------------------ caret
        void ShowCaret() { _caretOn = true; if (_blink != null && _blink.Enabled) { _blink.Stop(); _blink.Start(); } }
        void StartBlink() { _blink ??= new Timer { Interval = 500 }; _blink.Tick -= BlinkTick; _blink.Tick += BlinkTick; _caretOn = true; _blink.Start(); }
        void StopBlink() { _blink?.Stop(); _caretOn = true; }
        void BlinkTick(object? s, EventArgs e) { _caretOn = !_caretOn; Redraw(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); StartBlink(); Redraw(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); StopBlink(); Commit(); Redraw(); }
        protected override void OnHandleDestroyed(EventArgs e) { _blink?.Stop(); base.OnHandleDestroyed(e); }
        protected override void Dispose(bool disposing) { if (disposing) _blink?.Dispose(); base.Dispose(disposing); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); EnsureVisible(); Redraw(); }

        // ------------------------------------------------------------------ mouse
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            if (IsExtraAt(e.Location)) return;   // the numeric box's spinner
            int i = IndexAt(e.X, Sc);
            _caret = i; if ((ModifierKeys & Keys.Shift) == 0) _anchor = i;
            _dragging = true; Capture = true; ShowCaret(); Redraw();
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (_dragging) { _dragging = false; Capture = false; } }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); _dragging = false; }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = IndexAt(e.X, Sc); string t = Text;
            int a = i, b = i;
            while (a > 0 && !char.IsWhiteSpace(t[a - 1])) a--;
            while (b < t.Length && !char.IsWhiteSpace(t[b])) b++;
            _anchor = a; _caret = b; Redraw();
        }

        // ------------------------------------------------------------------ keyboard
        protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Enter or Keys.Escape || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool shift = e.Shift, ctrl = (e.Modifiers & Keys.Control) != 0;
            string t = Text;
            void MoveTo(int i) { _caret = Math.Clamp(i, 0, t.Length); if (!shift) _anchor = _caret; EnsureVisible(); ShowCaret(); Redraw(); }
            int WordLeft(int i) { while (i > 0 && char.IsWhiteSpace(t[i - 1])) i--; while (i > 0 && !char.IsWhiteSpace(t[i - 1])) i--; return i; }
            int WordRight(int i) { while (i < t.Length && !char.IsWhiteSpace(t[i])) i++; while (i < t.Length && char.IsWhiteSpace(t[i])) i++; return i; }
            switch (e.KeyCode)
            {
                case Keys.Left: if (!shift && SelectionLength > 0) MoveTo(SelectionStart); else MoveTo(ctrl ? WordLeft(_caret) : _caret - 1); break;
                case Keys.Right: if (!shift && SelectionLength > 0) MoveTo(SelectionStart + SelectionLength); else MoveTo(ctrl ? WordRight(_caret) : _caret + 1); break;
                case Keys.Home: MoveTo(0); break;
                case Keys.End: MoveTo(t.Length); break;
                case Keys.Back:
                    if (_ro) break;
                    if (SelectionLength > 0) InsertText("");
                    else if (_caret > 0) { int a = ctrl ? WordLeft(_caret) : _caret - 1; SetTextInternal(t.Remove(a, _caret - a), a); }
                    break;
                case Keys.Delete:
                    if (_ro) break;
                    if (SelectionLength > 0) InsertText("");
                    else if (_caret < t.Length) { int b = ctrl ? WordRight(_caret) : _caret + 1; SetTextInternal(t.Remove(_caret, b - _caret), _caret); }
                    break;
                case Keys.A when ctrl: SelectAll(); break;
                case Keys.C when ctrl: Copy(); break;
                case Keys.X when ctrl: Copy(); if (!_ro) InsertText(""); break;
                case Keys.V when ctrl: Paste(); break;
                case Keys.Enter: Commit(); OnEnterPressed(); break;
                case Keys.Escape: if (Text != _committed) { base.Text = _committed; _caret = _anchor = Math.Min(_caret, _committed.Length); EnsureVisible(); Redraw(); } break;
                default: return;
            }
            e.Handled = true; e.SuppressKeyPress = true;
        }
        /// <summary>Enter: the numeric box steps nothing here; a form may hook Committed instead.</summary>
        protected virtual void OnEnterPressed() { }
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled || _ro || char.IsControl(e.KeyChar)) return;
            if (!AcceptChar(e.KeyChar)) { e.Handled = true; return; }
            InsertText(e.KeyChar.ToString()); e.Handled = true;
        }
        /// <summary>Filter for typed characters (the numeric box only takes digits, sign, decimal separator).</summary>
        protected virtual bool AcceptChar(char c) => true;
        void Copy() { if (SelectionLength == 0 || _pw != '\0') return; try { Clipboard.SetText(SelectedText); } catch { } }
        void Paste() { if (_ro) return; try { if (Clipboard.ContainsText()) InsertText(Clipboard.GetText()); } catch { } }

        // ------------------------------------------------------------------ paint
        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = FieldH, sc = Sc;
            bool focus = Focused;
            int face = Mix(BackColor, Black, _ro ? 0.1f : 0.25f), rim = focus ? Accent : Mix(BackColor, ThumbColor, IsHot && Enabled ? 0.5f : 0.3f);
            FillRound(s, 0.5f, 0.5f, w - 1, h - 1, 3f, face);
            var ta = TextArea;
            string shown = Shown;
            int textY = (h - T.GlyphHeight(sc)) / 2;
            using (var v = s.CreateView(new Rectangle(ta.X - sc, 0, ta.Width + 2 * sc, h)))
            {
                if (shown.Length == 0 && _placeholder.Length > 0 && !focus)
                    T.Draw(v, ta.X, textY, FitText(_placeholder, ta.Width, sc), Mix(ForeColor, BackColor, 0.55f), 0, sc);
                int x0 = XOf(0, sc);
                if (SelectionLength > 0)
                {
                    int sx = XOf(SelectionStart, sc), ex = XOf(SelectionStart + SelectionLength, sc) - sc;
                    v.FillRect(sx - 1, textY - sc - 1, ex - sx + 2, T.GlyphHeight(sc) + 2 * sc + 2, focus ? Mix(AccentRaw, BackColor, 0.35f) : Mix(BackColor, ThumbColor, 0.2f));
                }
                if (shown.Length > 0) T.Draw(v, x0, textY, shown, Fore, 0, sc);
                if (focus && _caretOn && !_ro)
                {
                    int cx = XOf(_caret, sc);
                    v.FillRect(cx - 1, textY - sc, Math.Max(1, sc), T.GlyphHeight(sc) + 2 * sc, Accent);
                }
            }
            PaintExtra(s);
            DrawRound(s, 0.5f, 0.5f, w - 1, h - 1, 3f, rim, 1f);
        }
        /// <summary>Hook for the numeric box's buttons.</summary>
        protected virtual void PaintExtra(Sprite s) { }
        protected override bool OwnFocusRect => true;   // the rim turns accent instead
    }

    /// <summary>
    /// A number field: <see cref="SpriteTextBox"/> that only takes digits, with up / down buttons on the right, arrow keys and
    /// the wheel stepping by <see cref="Step"/> (Shift = a tenth), <see cref="Minimum"/> / <see cref="Maximum"/> clamping and
    /// <see cref="Decimals"/> formatting. <see cref="ValueChanged"/> fires when the committed number changes (Enter, focus
    /// loss, a button, a key or the wheel); typing alone does not change <see cref="Value"/> yet.
    /// </summary>
    internal sealed class SpriteNumeric : SpriteTextBox
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Slider;
        double _value, _min, _max = 100, _step = 1; int _dec; string _unit = ""; int _pressedBtn; Timer? _repeat; int _repeatDir;
        SpinnerPlacement _spinner = SpinnerPlacement.Right; bool _wrapMouse = true, _dragSpin = true; int _pixelsPerStep = 4;
        // spinner drag: press on a button, move = value follows the pointer (up / right = larger); a press without movement is a click
        bool _spinDragging, _spinMoved; Point _spinLast; double _spinAcc, _spinStart; int _hotBtn;

        public SpriteNumeric() { Size = new Size(96, 24); TextAlign = HorizontalAlignment.Right; SyncText(); }

        [Category("Appearance"), DefaultValue(SpinnerPlacement.Right), Description("Where the up / down buttons are: Right = stacked beside the field (NumericUpDown), Below = a row under the field (minus | plus, wide buttons for touch / pen), None = no buttons (keys, wheel and typing only).")]
        public SpinnerPlacement Spinner { get => _spinner; set { _spinner = value; Redraw(); } }

        [Category("Behavior"), DefaultValue(true), Description("While dragging on the spinner a pointer that reaches the top / bottom (Right spinner) or the left / right edge (Below spinner) of the screen is moved to the opposite edge, so a long drag never stops at the border.")]
        public bool WrapMouse { get => _wrapMouse; set => _wrapMouse = value; }

        [Category("Behavior"), DefaultValue(true), Description("Press on a spinner button and drag to change the value continuously (up / right = larger, PixelsPerStep of travel per Step, Shift = a tenth); the press itself steps once like a click and auto-repeats while held still.")]
        public bool DragToChange { get => _dragSpin; set => _dragSpin = value; }

        [Category("Behavior"), DefaultValue(4), Description("Mouse travel in pixels per Step while dragging the spinner.")]
        public int PixelsPerStep { get => _pixelsPerStep; set => _pixelsPerStep = Math.Max(1, value); }

        [Category("Action"), Description("Raised after Value changed (user or code).")]
        public event EventHandler? ValueChanged;

        [Category("Behavior"), DefaultValue(0.0)]
        public double Value { get => _value; set => SetValue(value, false); }
        [Category("Behavior"), DefaultValue(0.0)]
        public double Minimum { get => _min; set { _min = value; if (_max < _min) _max = _min; SetValue(_value, false); } }
        [Category("Behavior"), DefaultValue(100.0)]
        public double Maximum { get => _max; set { _max = value; if (_min > _max) _min = _max; SetValue(_value, false); } }
        [Category("Behavior"), DefaultValue(1.0), Description("Increment of the buttons / arrow keys / wheel (Shift = a tenth).")]
        public double Step { get => _step; set { _step = value <= 0 ? 1 : value; } }
        [Category("Appearance"), DefaultValue(0)]
        public int Decimals { get => _dec; set { _dec = Math.Clamp(value, 0, 6); SyncText(); } }
        [Category("Appearance"), DefaultValue(""), Description("Suffix shown after the number (not editable).")]
        public string Unit { get => _unit; set { _unit = value ?? ""; SyncText(); } }

        void SetValue(double v, bool fromUser)
        {
            if (double.IsNaN(v)) v = _min;
            v = Math.Clamp(v, _min, _max);
            v = Math.Round(v, _dec);
            bool changed = v != _value; _value = v;
            SyncText();
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }
        void SyncText() { string s = Format(_value); if (Text != s) Text = s; }
        string Format(double v) => v.ToString("F" + _dec, System.Globalization.CultureInfo.CurrentCulture);
        public void Nudge(int steps, bool fine = false) => SetValue(_value + steps * (fine ? _step / 10 : _step), true);

        protected override void OnCommitted()
        {
            // parse what was typed (the unit / spaces stripped); garbage keeps the old value
            string t = Text.Trim(); if (_unit.Length > 0 && t.EndsWith(_unit, StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - _unit.Length).Trim();
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double v) ||
                double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) SetValue(v, true);
            else SyncText();
            base.OnCommitted();
        }
        protected override void OnEnterPressed() { SelectAll(); }
        protected override bool AcceptChar(char c) => char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == ',' || c == 'e' || c == 'E';

        bool Below => _spinner == SpinnerPlacement.Below;
        protected override int RightExtra => (_spinner == SpinnerPlacement.Right ? ButtonW : 0) + UnitW;
        protected override int BottomExtra => Below ? Math.Clamp(ClientSize.Height * 2 / 5, 12, 28) : 0;
        int ButtonW => Math.Max(14, FieldH * 3 / 4);
        int UnitW => _unit.Length == 0 ? 0 : T.Measure(_unit, Sc).Width + 3 * Sc;
        Rectangle UpRect => Below ? new Rectangle(ClientSize.Width / 2 + 1, FieldH + 2, ClientSize.Width - ClientSize.Width / 2 - 2, ClientSize.Height - FieldH - 3)
                                  : new Rectangle(ClientSize.Width - ButtonW - 1, 1, ButtonW, FieldH / 2 - 1);
        Rectangle DownRect => Below ? new Rectangle(1, FieldH + 2, ClientSize.Width / 2 - 1, ClientSize.Height - FieldH - 3)
                                    : new Rectangle(ClientSize.Width - ButtonW - 1, FieldH / 2, ButtonW, FieldH - FieldH / 2 - 1);
        int ButtonAt(Point p) => _spinner == SpinnerPlacement.None ? 0 : UpRect.Contains(p) ? 1 : DownRect.Contains(p) ? -1 : 0;
        protected override bool IsExtraAt(Point p) => _spinner switch
        {
            SpinnerPlacement.Right => p.X >= ClientSize.Width - RightExtra,
            SpinnerPlacement.Below => p.Y >= FieldH,
            _ => false,
        };

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled && !ReadOnly)
            {
                int dir = ButtonAt(e.Location);
                if (dir != 0)
                {
                    Focus(); Commit();
                    _pressedBtn = dir; _repeatDir = dir; Capture = true;
                    _spinStart = _value;
                    Nudge(dir, (ModifierKeys & Keys.Shift) != 0);                                  // the press steps at once, like NumericUpDown
                    if (_dragSpin) { _spinDragging = true; _spinMoved = false; _spinLast = e.Location; _spinAcc = _value; }   // moving from here on drags
                    _repeat ??= new Timer(); _repeat.Interval = 400; _repeat.Tick -= RepeatTick; _repeat.Tick += RepeatTick; _repeat.Start();
                    RedrawNow(); return;
                }
            }
            base.OnMouseDown(e);
        }
        void RepeatTick(object? s, EventArgs e)
        {
            if (_spinDragging && _spinMoved) return;                 // a drag is in progress: the pointer sets the value, no auto-repeat
            _repeat!.Interval = 60; Nudge(_repeatDir, (ModifierKeys & Keys.Shift) != 0); RedrawNow();   // 400 ms delay, then one step per 60 ms
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_spinDragging)
            {
                bool vertical = !Below;
                int d = vertical ? _spinLast.Y - e.Y : e.X - _spinLast.X;      // up / right = larger
                if (!_spinMoved && Math.Abs(d) < 3) return;                     // dead zone: a wobbly click is still a click
                if (!_spinMoved) { _spinMoved = true; _repeat?.Stop(); Cursor = SpriteCursors.Get(SpriteCursors.Kind.HandGrab, this); }
                _spinLast = e.Location;
                _spinAcc += (double)d / _pixelsPerStep * ((ModifierKeys & Keys.Shift) != 0 ? _step / 10 : _step);
                _spinAcc = Math.Clamp(_spinAcc, _min, _max);
                SetValue(_spinAcc, true);
                if (_wrapMouse) { var np = WrapPointerOnScreen(e.Location, vertical); if (np != e.Location) _spinLast = np; }
                RedrawNow(); return;
            }
            base.OnMouseMove(e);
            int hot = Enabled && !ReadOnly ? ButtonAt(e.Location) : 0;
            if (hot != _hotBtn) { _hotBtn = hot; Redraw(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hotBtn != 0) { _hotBtn = 0; Redraw(); } }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_pressedBtn != 0)
            {
                EndSpin();
                _pressedBtn = 0; Capture = false; Redraw(); return;
            }
            base.OnMouseUp(e);
        }
        void EndSpin() { _repeat?.Stop(); if (_spinDragging) { _spinDragging = false; if (_spinMoved) Cursor = Cursors.Default; } _spinMoved = false; }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (_pressedBtn != 0) { EndSpin(); _pressedBtn = 0; Redraw(); } }

        protected override void Dispose(bool disposing) { if (disposing) _repeat?.Dispose(); base.Dispose(disposing); }
        protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _spinDragging && _spinMoved) { EndSpin(); SetValue(_spinStart, true); _pressedBtn = 0; Capture = false; Redraw(); e.Handled = true; e.SuppressKeyPress = true; return; }
            switch (e.KeyCode)
            {
                case Keys.Up: Commit(); Nudge(1, e.Shift); break;
                case Keys.Down: Commit(); Nudge(-1, e.Shift); break;
                case Keys.PageUp: Commit(); Nudge(10); break;
                case Keys.PageDown: Commit(); Nudge(-10); break;
                default: base.OnKeyDown(e); return;
            }
            SelectAll(); e.Handled = true; e.SuppressKeyPress = true;
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Enabled || ReadOnly) return;
            Commit(); Nudge(Math.Sign(e.Delta), (ModifierKeys & Keys.Shift) != 0);
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }
        protected override void PaintExtra(Sprite s)
        {
            int h = FieldH, sc = Sc;
            int rightBtn = _spinner == SpinnerPlacement.Right ? ButtonW : 0;
            if (_unit.Length > 0) T.Draw(s, ClientSize.Width - rightBtn - 2 - 2 * sc, h / 2, _unit, Mix(ForeColor, BackColor, 0.45f), 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleRight);
            if (_spinner == SpinnerPlacement.None) return;
            var up = UpRect; var dn = DownRect;
            int line = Mix(BackColor, ThumbColor, 0.25f), col = Enabled && !ReadOnly ? Fore : Mix(ForeColor, BackColor, 0.6f);
            int pressed = Mix(AccentRaw, BackColor, 0.5f), hot = Mix(BackColor, ThumbColor, 0.12f);
            if (!Below)
            {
                s.FillRect(up.X, 1, 1, h - 2, line);
                s.FillRect(up.X, up.Bottom, up.Width, 1, line);
                if (_pressedBtn == 1 || _hotBtn == 1) s.FillRect(up.X + 1, up.Y, up.Width - 1, up.Height, _pressedBtn == 1 ? pressed : hot);
                if (_pressedBtn == -1 || _hotBtn == -1) s.FillRect(dn.X + 1, dn.Y + 1, dn.Width - 1, dn.Height - 1, _pressedBtn == -1 ? pressed : hot);
                float a = Math.Max(2f, Math.Min(up.Width, up.Height) * 0.28f);
                PaintChevron(s, up.X + up.Width / 2f, up.Y + up.Height / 2f + a * 0.2f, a, 1, col, 1.2f);
                PaintChevron(s, dn.X + dn.Width / 2f, dn.Y + dn.Height / 2f - a * 0.2f, a, 0, col, 1.2f);
            }
            else
            {   // a button row under the field: [ - ][ + ] in its own rounded frame, one pixel below the field
                int w = ClientSize.Width, rowY = h + 1, rowH = ClientSize.Height - rowY;
                int face = Mix(BackColor, ThumbColor, 0.06f);
                FillRound(s, 0.5f, rowY + 0.5f, w - 1, rowH - 1, 3f, face);
                if (_pressedBtn == -1 || _hotBtn == -1) FillRound(s, dn.X + 0.5f, dn.Y + 0.5f, dn.Width - 1, dn.Height - 1, 2f, _pressedBtn == -1 ? pressed : hot);
                if (_pressedBtn == 1 || _hotBtn == 1) FillRound(s, up.X + 0.5f, up.Y + 0.5f, up.Width - 1, up.Height - 1, 2f, _pressedBtn == 1 ? pressed : hot);
                s.FillRect(w / 2, rowY + 2, 1, rowH - 4, line);
                float a = Math.Max(2f, Math.Min(rowH, 20) * 0.22f), t = MathF.Max(1.2f, a * 0.35f);
                float mx = dn.X + dn.Width / 2f, my = dn.Y + dn.Height / 2f, px = up.X + up.Width / 2f, py = up.Y + up.Height / 2f;
                s.FillRect(mx - a, my - t / 2, 2 * a, t, col);                                        // minus
                s.FillRect(px - a, py - t / 2, 2 * a, t, col); s.FillRect(px - t / 2, py - a, t, 2 * a, col);   // plus
                DrawRound(s, 0.5f, rowY + 0.5f, w - 1, rowH - 1, 3f, Mix(BackColor, ThumbColor, IsHot && Enabled ? 0.5f : 0.3f), 1f);
            }
        }
    }

    /// <summary>Where a <see cref="SpriteNumeric"/> puts its up / down buttons.</summary>
    internal enum SpinnerPlacement { Right, Below, None }

    /// <summary>
    /// A drop-down list: the field shows the selected item and a chevron, clicking (or Space / Enter / Alt+Down) opens an
    /// SR2D <see cref="SpriteMenu"/> with the <see cref="Items"/>; Up / Down and the wheel change the selection directly.
    /// </summary>
    internal sealed class SpriteCombo : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.ComboBox;
        readonly List<string> _items = new(); int _sel = -1; SpriteMenu? _menu; bool _open; string _placeholder = "";

        public SpriteCombo()
        {
            Cursor = Cursors.Default;
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            Size = new Size(140, 24);
        }
        [Category("Action"), Description("Raised after SelectedIndex changed.")]
        public event EventHandler? SelectedIndexChanged;

        /// <summary>The entries (edit, then call <see cref="RefreshItems"/> or set SelectedIndex; the menu is rebuilt on open).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<string> Items => _items;
        public void SetItems(IEnumerable<string> items, int select = 0) { _items.Clear(); _items.AddRange(items); _sel = -1; SelectedIndex = _items.Count == 0 ? -1 : Math.Clamp(select, 0, _items.Count - 1); Redraw(); }
        public void RefreshItems() { if (_sel >= _items.Count) SelectedIndex = _items.Count - 1; Redraw(); }

        [Category("Behavior"), DefaultValue(-1)]
        public int SelectedIndex
        {
            get => _sel;
            set { value = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1); if (value == _sel) return; _sel = value; Redraw(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
        }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SelectedItem { get => _sel >= 0 && _sel < _items.Count ? _items[_sel] : null; set => SelectedIndex = value == null ? -1 : _items.IndexOf(value); }

        [Category("Appearance"), DefaultValue(""), Description("Dimmed text shown while nothing is selected.")]
        public string Placeholder { get => _placeholder; set { _placeholder = value ?? ""; Redraw(); } }

        [Browsable(false)] public bool IsOpen => _open;

        int Sc => AutoTextScale(ClientSize.Height * 4);
        int ButtonW => Math.Max(16, ClientSize.Height * 3 / 4);

        /// <summary>Opens the list below the field.</summary>
        public void Open()
        {
            if (!Enabled || _items.Count == 0 || _open) return;
            _menu ??= new SpriteMenu();
            _menu.Clear(); _menu.TextScale = Sc; _menu.MinWidth = ClientSize.Width;
            _menu.BackColor = Color.FromArgb(Mix(BackColor, Black, 0.1f)); _menu.ForeColor = ForeColor; _menu.AccentColor = AccentRaw; _menu.BorderColor = Color.FromArgb(Mix(BackColor, ThumbColor, 0.45f));
            for (int i = 0; i < _items.Count; i++) { int k = i; _menu.AddRadio(_items[i], () => _sel == k, () => { SelectedIndex = k; _menu.Close(); }); }
            _menu.Closed -= MenuClosed; _menu.Closed += MenuClosed;
            _open = true; Redraw();
            _menu.Show(this, new Point(0, ClientSize.Height));
        }
        void MenuClosed(SpriteMenu m) { _open = false; Redraw(); }
        public void Close() => _menu?.Close();
        protected override void Dispose(bool disposing) { if (disposing) _menu?.Dispose(); base.Dispose(disposing); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            if (_open) Close(); else Open();
        }
        protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Enter || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_items.Count == 0) return;
            switch (e.KeyCode)
            {
                case Keys.Down when (e.Modifiers & Keys.Alt) != 0: Open(); break;
                case Keys.Up: SelectedIndex = Math.Max(0, _sel - 1); break;
                case Keys.Down: SelectedIndex = Math.Min(_items.Count - 1, _sel + 1); break;
                case Keys.Home: SelectedIndex = 0; break;
                case Keys.End: SelectedIndex = _items.Count - 1; break;
                case Keys.Space: case Keys.Enter: if (_open) Close(); else Open(); break;
                default:
                    {   // first letter cycles through matching items
                        char c = (char)e.KeyValue; if (!char.IsLetterOrDigit(c)) return;
                        int n = _items.Count;
                        for (int s = 1; s <= n; s++) { int i = (_sel + s + n) % n; if (_items[i].Length > 0 && char.ToUpperInvariant(_items[i][0]) == char.ToUpperInvariant(c)) { SelectedIndex = i; break; } }
                        break;
                    }
            }
            e.Handled = true;
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Enabled || _items.Count == 0 || _open) return;
            SelectedIndex = Math.Clamp(_sel - Math.Sign(e.Delta), 0, _items.Count - 1);
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Redraw(); }

        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height, sc = Sc, bw = ButtonW;
            bool hot = IsHot && Enabled, focus = Focused;
            int face = Mix(BackColor, ThumbColor, _open ? 0.12f : hot ? 0.24f : 0.18f), rim = focus || _open ? Accent : Mix(BackColor, ThumbColor, hot ? 0.6f : 0.45f);
            FillRound(s, 0.5f, 0.5f, w - 1, h - 1, 3f, face);
            if (!_open) { using var top = s.CreateView(new Rectangle(0, 0, w, (int)(h * 0.45f))); FillRound(top, 1.5f, 1.5f, w - 3, h - 3, 2f, Mix(BackColor, ThumbColor, 0.26f)); }
            int pad = 4 * sc + 2, avail = w - bw - pad - 2;
            string t = SelectedItem ?? _placeholder; int col = SelectedItem != null ? Fore : Mix(ForeColor, BackColor, 0.55f);
            if (t.Length > 0) { int fit = FitScale(t, avail, sc); T.Draw(s, pad, h / 2, FitText(t, avail, fit), col, 0, fit, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft); }
            s.FillRect(w - bw - 1, 3, 1, h - 6, Mix(BackColor, ThumbColor, 0.3f));
            float a = Math.Max(3f, bw * 0.24f);
            PaintChevron(s, w - bw / 2f - 0.5f, h / 2f, a, _open ? 1 : 0, Enabled ? (_open ? Accent : Fore) : Fore, 1.5f);
            DrawRound(s, 0.5f, 0.5f, w - 1, h - 1, 3f, rim, 1f);
        }
        protected override bool OwnFocusRect => true;
    }

    /// <summary>How items of a <see cref="SpriteListBox"/> are selected.</summary>
    internal enum ListSelection
    {
        /// <summary>Clicking only raises ItemActivated; nothing stays selected.</summary>
        None,
        /// <summary>One item at a time.</summary>
        One,
        /// <summary>Several: click toggles, Shift+click ranges (Ctrl+A all).</summary>
        Multi,
    }

    /// <summary>
    /// A list of strings with an SR2D scroll bar (<see cref="SpriteScrollBar"/>), hover highlight, keyboard navigation
    /// (arrows, PgUp / PgDn, Home / End, first letter), the wheel, single / multi selection (<see cref="SelectionMode"/>)
    /// and optional check boxes (<see cref="CheckBoxes"/>, independent of the selection). Double click (or Enter) raises
    /// <see cref="ItemActivated"/>.
    /// </summary>
    internal sealed class SpriteListBox : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.List;
        readonly List<string> _items = new(); readonly HashSet<int> _selected = new(), _checked = new(), _headers = new();
        readonly Dictionary<int, int> _colors = new();
        int _focusIdx = -1, _hot = -1, _anchor = -1, _itemH; ListSelection _mode = ListSelection.One; bool _checks, _showLines;
#pragma warning disable CA2213 // _bar is added to Controls: WinForms disposes child controls with the parent
        readonly SpriteScrollBar _bar; bool _barSync; int _top;   // first visible pixel row of the list
#pragma warning restore CA2213

        public SpriteListBox()
        {
            Cursor = Cursors.Default;
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            _bar = new SpriteScrollBar { Orientation = Orientation.Vertical, Visible = false, Step = 1 };
            _bar.ValueChanged += (_, _) => { if (_barSync) return; _top = (int)Math.Round(_bar.Value); Redraw(); };
            Controls.Add(_bar);
            Size = new Size(160, 120);      // after the bar exists: setting Size raises OnResize -> Relayout
        }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.Style |= 0x02000000; return cp; } }

        [Category("Action"), Description("Raised after the selection changed.")]
        public event EventHandler? SelectedIndexChanged;
        [Category("Action"), Description("Raised on double click / Enter on an item (ItemIndex = the item).")]
        public event EventHandler<int>? ItemActivated;
        [Category("Action"), Description("Raised after a check box changed (the item index is passed).")]
        public event EventHandler<int>? ItemChecked;

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<string> Items => _items;
        /// <summary>
        /// Replaces the items and clears selection and checks. <paramref name="headers"/> = indices drawn as group headers (bold band,
        /// not selectable, skipped by the keyboard); <paramref name="colors"/> = ARGB text colour per index (e.g. greyed entries).
        /// </summary>
        public void SetItems(IEnumerable<string> items, IEnumerable<int>? headers, IReadOnlyDictionary<int, int>? colors = null)
        {
            _headers.Clear(); _colors.Clear();
            if (headers != null) foreach (int h in headers) _headers.Add(h);
            if (colors != null) foreach (var kv in colors) _colors[kv.Key] = kv.Value;
            SetItems(items);
        }
        /// <summary>True when index <paramref name="i"/> is a group header (see <see cref="SetItems(IEnumerable{string}, IEnumerable{int}, IReadOnlyDictionary{int, int})"/>).</summary>
        public bool IsHeader(int i) => _headers.Contains(i);
        /// <summary>Replaces the items and clears selection and checks.</summary>
        public void SetItems(IEnumerable<string> items) { _items.Clear(); _items.AddRange(items); _selected.Clear(); _checked.Clear(); _focusIdx = _items.Count > 0 ? 0 : -1; _anchor = -1; _top = 0; Relayout(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
        /// <summary>Call after editing <see cref="Items"/> in place.</summary>
        public void RefreshItems() { _selected.RemoveWhere(i => i >= _items.Count); _checked.RemoveWhere(i => i >= _items.Count); if (_focusIdx >= _items.Count) _focusIdx = _items.Count - 1; Relayout(); }

        [Category("Behavior"), DefaultValue(ListSelection.One)]
        public ListSelection SelectionMode { get => _mode; set { _mode = value; if (value == ListSelection.None) _selected.Clear(); else if (value == ListSelection.One && _selected.Count > 1) { int k = SelectedIndex; _selected.Clear(); _selected.Add(k); } Redraw(); } }

        [Category("Behavior"), DefaultValue(false), Description("A check box in front of every item (CheckedIndices), independent of the selection.")]
        public bool CheckBoxes { get => _checks; set { _checks = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(false), Description("A faint line between the items.")]
        public bool ShowLines { get => _showLines; set { _showLines = value; Redraw(); } }

        [Category("Appearance"), DefaultValue(0), Description("Row height in pixels (0 = from the text scale).")]
        public int ItemHeight { get => _itemH; set { _itemH = Math.Max(0, value); Relayout(); } }

        [Category("Behavior"), DefaultValue(-1), Description("The (first) selected index; -1 = none.")]
        public int SelectedIndex
        {
            get { int best = -1; foreach (int i in _selected) if (best < 0 || i < best) best = i; return best; }
            set { _selected.Clear(); if (value >= 0 && value < _items.Count && _mode != ListSelection.None) { _selected.Add(value); _focusIdx = value; _anchor = value; EnsureVisible(value); } Redraw(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
        }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SelectedItem { get => SelectedIndex >= 0 ? _items[SelectedIndex] : null; set => SelectedIndex = value == null ? -1 : _items.IndexOf(value); }
        /// <summary>All selected indices in ascending order.</summary>
        [Browsable(false)] public int[] SelectedIndices { get { var a = new int[_selected.Count]; _selected.CopyTo(a); Array.Sort(a); return a; } }
        /// <summary>All checked indices in ascending order.</summary>
        [Browsable(false)] public int[] CheckedIndices { get { var a = new int[_checked.Count]; _checked.CopyTo(a); Array.Sort(a); return a; } }
        public bool IsSelected(int i) => _selected.Contains(i);
        public bool GetChecked(int i) => _checked.Contains(i);
        public void SetChecked(int i, bool on) { if (i < 0 || i >= _items.Count) return; bool was = _checked.Contains(i); if (was == on) return; if (on) _checked.Add(i); else _checked.Remove(i); Redraw(); ItemChecked?.Invoke(this, i); }
        public void SelectAll() { if (_mode != ListSelection.Multi) return; _selected.Clear(); for (int i = 0; i < _items.Count; i++) _selected.Add(i); Redraw(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
        public void ClearSelection() { if (_selected.Count == 0) return; _selected.Clear(); Redraw(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }

        // ------------------------------------------------------------------ geometry
        int Sc => AutoTextScale(Math.Max(ClientSize.Width, ClientSize.Height) / 2);
        int RowH => _itemH > 0 ? _itemH : TextLineHeight(Sc) + 6;
        int ListH => _items.Count * RowH;
        Rectangle Inner => new Rectangle(1, 1, Math.Max(1, ClientSize.Width - 2 - (_bar.Visible ? _bar.Width : 0)), Math.Max(1, ClientSize.Height - 2));
        int MaxTop => Math.Max(0, ListH - Inner.Height);
        void Relayout()
        {
            if (_bar == null) return;
            int h = ClientSize.Height - 2, w = ClientSize.Width;
            bool need = ListH > h;
            _top = Math.Clamp(_top, 0, Math.Max(0, ListH - h));
            _barSync = true;
            try
            {
                _bar.Visible = need;
                if (need) { _bar.Bounds = new Rectangle(w - 15, 1, 14, h); _bar.Minimum = 0; _bar.Maximum = ListH; _bar.PageSize = h; _bar.Step = RowH; _bar.Value = _top; }
            }
            finally { _barSync = false; }
            Redraw();
        }
        void EnsureVisible(int i)
        {
            if (i < 0 || i >= _items.Count) return;
            int y0 = i * RowH, y1 = y0 + RowH, h = Inner.Height;
            if (y0 < _top) _top = y0; else if (y1 > _top + h) _top = y1 - h;
            _top = Math.Clamp(_top, 0, MaxTop);
            _barSync = true; try { _bar.Value = _top; } finally { _barSync = false; }
        }
        int ItemAt(Point p) { var r = Inner; if (!r.Contains(p)) return -1; int i = (p.Y - r.Y + _top) / RowH; return i >= 0 && i < _items.Count ? i : -1; }
        Rectangle CheckRect(int i) { int sc = Sc, half = 4 * sc + 1, rh = RowH; return new Rectangle(Inner.X + 4 + 1, Inner.Y + i * rh - _top + (rh - 2 * half) / 2, 2 * half, 2 * half); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        // ------------------------------------------------------------------ selection logic
        void ClickSelect(int i, bool shift, bool ctrl)
        {
            if (i >= 0 && _headers.Contains(i)) return;                      // headers are not items
            if (i < 0 || _mode == ListSelection.None) { _focusIdx = i; Redraw(); return; }
            _focusIdx = i;
            if (_mode == ListSelection.One) { if (_selected.Count == 1 && _selected.Contains(i)) { Redraw(); return; } _selected.Clear(); _selected.Add(i); _anchor = i; }
            else if (shift && _anchor >= 0) { if (!ctrl) _selected.Clear(); for (int k = Math.Min(_anchor, i); k <= Math.Max(_anchor, i); k++) _selected.Add(k); }
            else if (ctrl) { if (!_selected.Remove(i)) _selected.Add(i); _anchor = i; }
            else { _selected.Clear(); _selected.Add(i); _anchor = i; }
            Redraw(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
        void MoveFocus(int i, bool shift, bool ctrl)
        {
            if (_items.Count == 0) return;
            i = Math.Clamp(i, 0, _items.Count - 1);
            if (_headers.Contains(i))
            {   // step over a header in the direction of travel; nothing beyond it = stay
                int dir = i >= _focusIdx ? 1 : -1, j = i;
                while (j >= 0 && j < _items.Count && _headers.Contains(j)) j += dir;
                if (j < 0 || j >= _items.Count) { j = i; while (j >= 0 && j < _items.Count && _headers.Contains(j)) j -= dir; }
                if (j < 0 || j >= _items.Count || _headers.Contains(j)) return;
                i = j;
            }
            EnsureVisible(i);
            if (ctrl && _mode == ListSelection.Multi) { _focusIdx = i; Redraw(); }   // Ctrl+arrows move the focus only; Space toggles
            else ClickSelect(i, shift, false);
        }

        // ------------------------------------------------------------------ mouse
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = ItemAt(e.Location); if (h != _hot) { _hot = h; Redraw(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hot >= 0) { _hot = -1; Redraw(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            int i = ItemAt(e.Location);
            if (i >= 0 && _checks && CheckRect(i).Contains(e.Location)) { SetChecked(i, !_checked.Contains(i)); _focusIdx = i; return; }
            ClickSelect(i, (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Control) != 0);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = ItemAt(e.Location);
            if (i >= 0 && !(_checks && CheckRect(i).Contains(e.Location))) ItemActivated?.Invoke(this, i);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MaxTop == 0) return;
            _top = Math.Clamp(_top - Math.Sign(e.Delta) * RowH * 3, 0, MaxTop);
            _barSync = true; try { _bar.Value = _top; } finally { _barSync = false; }
            _hot = ItemAt(e.Location); Redraw();
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }
        protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Enter || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool shift = e.Shift, ctrl = (e.Modifiers & Keys.Control) != 0;
            int page = Math.Max(1, Inner.Height / RowH - 1);
            switch (e.KeyCode)
            {
                case Keys.Up: MoveFocus(_focusIdx - 1, shift, ctrl); break;
                case Keys.Down: MoveFocus(_focusIdx + 1, shift, ctrl); break;
                case Keys.PageUp: MoveFocus(_focusIdx - page, shift, ctrl); break;
                case Keys.PageDown: MoveFocus(_focusIdx + page, shift, ctrl); break;
                case Keys.Home: MoveFocus(0, shift, ctrl); break;
                case Keys.End: MoveFocus(_items.Count - 1, shift, ctrl); break;
                case Keys.Space:
                    if (_focusIdx >= 0) { if (_checks) SetChecked(_focusIdx, !_checked.Contains(_focusIdx)); else if (_mode == ListSelection.Multi) ClickSelect(_focusIdx, false, true); }
                    break;
                case Keys.Enter: if (_focusIdx >= 0) ItemActivated?.Invoke(this, _focusIdx); break;
                case Keys.A when ctrl: SelectAll(); break;
                default:
                    {
                        char c = (char)e.KeyValue; if (!char.IsLetterOrDigit(c) || ctrl) return;
                        int n = _items.Count; if (n == 0) return;
                        for (int s = 1; s <= n; s++) { int i = (_focusIdx + s + n) % n; if (_items[i].Length > 0 && char.ToUpperInvariant(_items[i][0]) == char.ToUpperInvariant(c)) { MoveFocus(i, false, false); break; } }
                        break;
                    }
            }
            e.Handled = true;
        }

        // ------------------------------------------------------------------ paint
        protected override void PaintControl(Sprite s)
        {
            int w = ClientSize.Width, h = ClientSize.Height, sc = Sc, rh = RowH;
            bool focus = Focused;
            int face = Mix(BackColor, Black, 0.25f), rim = focus ? Accent : Mix(BackColor, ThumbColor, IsHot && Enabled ? 0.5f : 0.3f);
            FillRound(s, 0.5f, 0.5f, w - 1, h - 1, 3f, face);
            var inner = Inner;
            using (var v = s.CreateView(inner))
            {
                int first = _top / rh, last = Math.Min(_items.Count - 1, (_top + inner.Height) / rh);
                int textX = inner.X + 4 * sc + 2 + (_checks ? 8 * sc + 2 + 4 * sc : 0), avail = inner.Right - textX - 2 * sc;
                int selCol = Mix(AccentRaw, BackColor, focus ? 0.3f : 0.55f), hotCol = Mix(BackColor, ThumbColor, 0.14f), line = Mix(BackColor, ThumbColor, 0.12f);
                for (int i = first; i <= last; i++)
                {
                    int y = inner.Y + i * rh - _top;
                    bool sel = _selected.Contains(i), hot = i == _hot && Enabled && !_headers.Contains(i);
                    if (_headers.Contains(i))
                    {   // group header: a band in the rim colour, bold caption, no check box
                        v.FillRect(inner.X + 1, y, inner.Width - 2, rh, Mix(BackColor, ThumbColor, 0.10f));
                        string ht = _items[i]; int hfit = FitScale(ht, avail, sc);
                        T.Draw(v, inner.X + 4 * sc + 2, y + rh / 2, FitText(ht, avail, hfit), Mix(Fore, AccentRaw.ToArgb(), 0.35f), 0, hfit, 1, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
                        continue;
                    }
                    if (sel) v.FillRect(inner.X + 1, y, inner.Width - 2, rh, selCol);
                    else if (hot) v.FillRect(inner.X + 1, y, inner.Width - 2, rh, hotCol);
                    if (_showLines && i > 0) v.FillRect(inner.X + 4, y, inner.Width - 8, 1, line);
                    if (_checks)
                    {
                        var cr = CheckRect(i); float half = cr.Width / 2f;
                        PaintCheckBox(v, cr.X + half, cr.Y + half, half, _checked.Contains(i) ? 1f : 0f, Accent, Mix(BackColor, ThumbColor, 0.45f), Mix(BackColor, Color.Black, 0.35f));
                    }
                    string t = _items[i];
                    int fit = FitScale(t, avail, sc);
                    int tcol = _colors.TryGetValue(i, out var cc0) ? (Enabled ? cc0 : Mix(cc0, BackColor, 0.5f)) : sel ? (Enabled ? unchecked((int)0xFFFFFFFF) : Fore) : Fore;
                    T.Draw(v, textX, y + rh / 2, FitText(t, avail, fit), tcol, 0, fit, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
                    if (focus && i == _focusIdx && ShowFocusCues) v.DrawRect(inner.X + 1.5f, y + 0.5f, inner.Width - 3, rh - 1, Argb(AccentRaw, 160), 1f, false, SR2D.LineOp.AlphaBlend);
                }
            }
            DrawRound(s, 0.5f, 0.5f, w - 1, h - 1, 3f, rim, 1f);
        }
        protected override bool OwnFocusRect => true;
    }
}
