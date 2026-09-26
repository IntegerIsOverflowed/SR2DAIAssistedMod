// SpriteControls.TextView.cs - SpriteTextView: a multi-line, read-only text view in the SR2D control look (descriptions,
// logs, source code): word wrap or horizontal scrolling, SR2D scroll bars, wheel and keyboard scrolling, mouse selection
// with Ctrl+C / Ctrl+A, per-line colour runs (a Colorizer callback or explicit runs), line numbers, AppendLine with
// tail-follow and a line cap for logs. Pixel font by default; TextFontFamily / TextFont switch it to a real font.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>A coloured stretch of a <see cref="SpriteTextView"/> line: <see cref="Start"/> / <see cref="Length"/> in characters of that line, ARGB <see cref="Color"/> (0 = the control's ForeColor).</summary>
    internal readonly struct TextRun
    {
        public readonly int Start, Length, Color;
        public TextRun(int start, int length, int color) { Start = start; Length = length; Color = color; }
    }

    /// <summary>
    /// Multi-line read-only text: a description pane, a log, a code view. <see cref="Text"/> / <see cref="SetLines"/> replace
    /// the content, <see cref="AppendLine"/> adds to a log (the view follows the tail while it is scrolled to the bottom;
    /// <see cref="MaxLines"/> caps it). <see cref="WordWrap"/> wraps to the width, otherwise a horizontal bar appears.
    /// Colour: <see cref="Colorizer"/> is asked once per line (cached) or pass runs with <see cref="SetLines"/> /
    /// <see cref="AppendLine"/>. Selection with the mouse (double click = word, Ctrl+A = all), Ctrl+C copies;
    /// arrows / PgUp / PgDn / Home / End scroll. <see cref="LineNumbers"/> adds a gutter. The text is drawn with the
    /// pixel font (monospace) unless a TextFont / TextFontFamily is set.
    /// </summary>
    internal sealed class SpriteTextView : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Text;
        readonly List<string> _lines = new();
        readonly List<TextRun[]?> _runs = new();          // per line; null = not coloured yet (Colorizer) / plain
        readonly List<bool> _runsDone = new();             // Colorizer already asked for this line
        // visual rows: (line, first column, length) - one per line without wrap, several with it
        readonly List<(int line, int start, int len)> _rows = new();
        bool _rowsDirty = true; int _contentW; int _rowsWidth = -1;
        bool _wrap, _lineNumbers, _follow = true; int _maxLines, _tabSize = 4, _lineSpacing = 3;
        int _scrollY, _scrollX;                            // pixels
        (int line, int col) _anchor = (-1, -1), _caret = (-1, -1); bool _selecting;
        Func<string, TextRun[]?>? _colorizer;
#pragma warning disable CA2213 // the bars are added to Controls: WinForms disposes child controls with the parent
        readonly SpriteScrollBar _vbar, _hbar; bool _barSync;
#pragma warning restore CA2213

        public SpriteTextView()
        {
            Cursor = Cursors.IBeam;
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            _vbar = new SpriteScrollBar { Orientation = Orientation.Vertical, Visible = false };
            _hbar = new SpriteScrollBar { Orientation = Orientation.Horizontal, Visible = false };
            _vbar.ValueChanged += (_, _) => { if (_barSync) return; _scrollY = (int)Math.Round(_vbar.Value); Redraw(); };
            _hbar.ValueChanged += (_, _) => { if (_barSync) return; _scrollX = (int)Math.Round(_hbar.Value); Redraw(); };
            Controls.Add(_vbar); Controls.Add(_hbar);
            Size = new Size(240, 120);
        }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.Style |= 0x02000000; return cp; } }   // WS_CLIPCHILDREN

        // ------------------------------------------------------------------ content
        /// <summary>The whole text (lines joined with Environment.NewLine, like a TextBox; "\r\n", "\n" and "\r" are all accepted when set).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), AllowNull]
        public override string Text
        {
            get => string.Join(Environment.NewLine, _lines);
            set => SetLines((value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'), null);
        }
        /// <summary>Replaces the content; <paramref name="runs"/> (optional) = colour runs per line, same count as <paramref name="lines"/>.</summary>
        public void SetLines(IEnumerable<string> lines, IEnumerable<TextRun[]?>? runs)
        {
            ArgumentNullException.ThrowIfNull(lines);
            _lines.Clear(); _runs.Clear(); _runsDone.Clear();
            foreach (var l in lines) { _lines.Add(Expand(l)); _runs.Add(null); _runsDone.Add(false); }
            if (runs != null) { int i = 0; foreach (var r in runs) { if (i >= _runs.Count) break; _runs[i] = r; _runsDone[i] = r != null; i++; } }
            _scrollX = _scrollY = 0; _anchor = _caret = (-1, -1);
            _rowsDirty = true; Relayout();
            OnTextChanged(EventArgs.Empty);
        }
        /// <summary>Adds a line at the end (a log). <paramref name="color"/> 0 = ForeColor. The view follows the tail when it was at the bottom.</summary>
        public void AppendLine(string line, int color = 0)
        {
            bool atEnd = _scrollY >= MaxScrollY - 1;
            var parts = (line ?? "").Replace("\r\n", "\n").Split('\n');
            foreach (var p in parts) { _lines.Add(Expand(p)); _runs.Add(color != 0 ? new[] { new TextRun(0, p.Length, color) } : null); _runsDone.Add(color != 0); }
            if (_maxLines > 0 && _lines.Count > _maxLines)
            {
                int drop = _lines.Count - _maxLines;
                _lines.RemoveRange(0, drop); _runs.RemoveRange(0, drop); _runsDone.RemoveRange(0, drop);
                _anchor = _caret = (-1, -1); _rowsDirty = true;
            }
            else if (!_rowsDirty) { for (int i = _lines.Count - parts.Length; i < _lines.Count; i++) AddRows(i); }
            Relayout();
            if (atEnd && _follow) ScrollToEnd();
            OnTextChanged(EventArgs.Empty);
        }
        /// <summary>Removes everything.</summary>
        public void Clear() => SetLines(Array.Empty<string>(), null);
        /// <summary>The lines as they are shown (tabs expanded).</summary>
        [Browsable(false)] public IReadOnlyList<string> Lines => _lines;
        [Browsable(false)] public int LineCount => _lines.Count;
        string Expand(string s) => s.IndexOf('\t') < 0 ? s : ExpandTabs(s, _tabSize);
        static string ExpandTabs(string s, int tab)
        {
            var sb = new StringBuilder(s.Length + 16); int col = 0;
            foreach (char c in s) { if (c == '\t') { int n = tab - (col % tab); sb.Append(' ', n); col += n; } else { sb.Append(c); col++; } }
            return sb.ToString();
        }

        /// <summary>Colours the lines: called once per line when it is first shown (cached until the content changes). Return null for plain text.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Func<string, TextRun[]?>? Colorizer { get => _colorizer; set { _colorizer = value; ResetColors(); } }
        /// <summary>Forgets the cached colour runs so <see cref="Colorizer"/> is asked again.</summary>
        public void ResetColors() { for (int i = 0; i < _runsDone.Count; i++) { _runsDone[i] = false; _runs[i] = null; } Redraw(); }

        [Category("Behavior"), DefaultValue(false), Description("Wrap long lines to the width (else a horizontal scroll bar).")]
        public bool WordWrap { get => _wrap; set { if (_wrap == value) return; _wrap = value; _scrollX = 0; _rowsDirty = true; Relayout(); } }
        [Category("Appearance"), DefaultValue(false), Description("A line-number gutter on the left.")]
        public bool LineNumbers { get => _lineNumbers; set { _lineNumbers = value; _rowsDirty = true; Relayout(); } }
        [Category("Behavior"), DefaultValue(0), Description("Log cap: when more lines than this are appended the oldest go (0 = unlimited).")]
        public int MaxLines { get => _maxLines; set => _maxLines = Math.Max(0, value); }
        [Category("Behavior"), DefaultValue(true), Description("AppendLine keeps the end in view while the view is scrolled to the bottom.")]
        public bool FollowTail { get => _follow; set => _follow = value; }
        [Category("Behavior"), DefaultValue(4), Description("Tab stop width in characters (tabs are expanded when text is set).")]
        public int TabSize { get => _tabSize; set => _tabSize = Math.Clamp(value, 1, 16); }
        [Category("Appearance"), DefaultValue(3), Description("Extra pixels between rows (the pixel font has no leading of its own).")]
        public int LineSpacing { get => _lineSpacing; set { _lineSpacing = Math.Clamp(value, 0, 32); _rowsDirty = true; Relayout(); } }

        // ------------------------------------------------------------------ selection
        /// <summary>The selected text ("" when nothing is selected).</summary>
        [Browsable(false)]
        public string SelectedText
        {
            get
            {
                var (a, b) = Ordered(); if (a.line < 0 || a == b) return "";
                if (a.line == b.line) return _lines[a.line].Substring(a.col, b.col - a.col);
                var sb = new StringBuilder();
                sb.Append(_lines[a.line], a.col, _lines[a.line].Length - a.col).Append(Environment.NewLine);
                for (int i = a.line + 1; i < b.line; i++) sb.Append(_lines[i]).Append(Environment.NewLine);
                sb.Append(_lines[b.line], 0, b.col);
                return sb.ToString();
            }
        }
        [Browsable(false)] public bool HasSelection { get { var (a, b) = Ordered(); return a.line >= 0 && a != b; } }
        public void SelectAll() { if (_lines.Count == 0) return; _anchor = (0, 0); _caret = (_lines.Count - 1, _lines[^1].Length); Redraw(); }
        public void ClearSelection() { _anchor = _caret = (-1, -1); Redraw(); }
        /// <summary>Copies the selection (or, with nothing selected, nothing) to the clipboard.</summary>
        public void Copy() { var t = SelectedText; if (t.Length == 0) return; try { Clipboard.SetText(t); } catch (System.Runtime.InteropServices.ExternalException) { } }
        ((int line, int col), (int line, int col)) Ordered()
        {
            if (_anchor.line < 0 || _caret.line < 0) return ((-1, -1), (-1, -1));
            return _anchor.line < _caret.line || (_anchor.line == _caret.line && _anchor.col <= _caret.col) ? (_anchor, _caret) : (_caret, _anchor);
        }

        // ------------------------------------------------------------------ geometry
        int Sc => AutoTextScale(0);
        int RowH => TextLineHeight(Sc) + _lineSpacing;
        const int Pad = 4;
        int GutterW => _lineNumbers ? (Math.Max(2, _lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length) + 1) * T.GlyphAdvance(Sc) + 6 : 0;
        Rectangle Inner => new Rectangle(1, 1, Math.Max(1, ClientSize.Width - 2 - (_vbar.Visible ? _vbar.Width : 0)), Math.Max(1, ClientSize.Height - 2 - (_hbar.Visible ? _hbar.Height : 0)));
        Rectangle TextArea { get { var r = Inner; return new Rectangle(r.X + Pad + GutterW, r.Y + Pad, Math.Max(1, r.Width - 2 * Pad - GutterW), Math.Max(1, r.Height - 2 * Pad)); } }
        int ContentH => _rows.Count * RowH;
        /// <summary>Visual rows (lines after wrapping) - for tests.</summary>
        internal int RowCount { get { EnsureRows(); return _rows.Count; } }
        int MaxScrollY => Math.Max(0, ContentH - TextArea.Height);
        int MaxScrollX => _wrap ? 0 : Math.Max(0, _contentW - TextArea.Width);

        void EnsureRows()
        {
            int w = TextArea.Width;
            if (!_rowsDirty && (!_wrap || w == _rowsWidth)) return;
            _rows.Clear(); _contentW = 0; _rowsWidth = w;
            for (int i = 0; i < _lines.Count; i++) AddRows(i);
            _rowsDirty = false;
        }
        void AddRows(int i)
        {
            string l = _lines[i];
            if (!_wrap)
            {
                _rows.Add((i, 0, l.Length));
                int lw = l.Length == 0 ? 0 : T.Measure(l, Sc).Width; if (lw > _contentW) _contentW = lw;
                return;
            }
            if (l.Length == 0) { _rows.Add((i, 0, 0)); return; }
            // wrap by measuring: the same break rule as ControlText.Wrap, but we keep the columns
            int w = Math.Max(8, _rowsWidth), start = 0;
            while (start < l.Length)
            {
                int fit = FitCols(l, start, w);
                int end = start + fit;
                if (end < l.Length)
                {   // break after the last space inside the fitting stretch
                    int sp = l.LastIndexOf(' ', end - 1, end - start);
                    if (sp > start) end = sp + 1;
                }
                _rows.Add((i, start, end - start));
                start = end;
            }
        }
        /// <summary>Number of characters of <paramref name="l"/> from <paramref name="start"/> that fit <paramref name="w"/> px (at least 1).</summary>
        int FitCols(string l, int start, int w)
        {
            int rest = l.Length - start;
            if (T.IsPixel)
            {   // monospace fast path (fallback glyphs may be wider: verified below)
                int adv = T.GlyphAdvance(Sc), n = Math.Clamp((w + Sc) / adv, 1, rest);
                if (n == rest || T.CharX(l, start + n, Sc) - T.CharX(l, start, Sc) <= w + Sc) return n;
            }
            // binary search on the measured prefix
            int lo = 1, hi = rest; int x0 = T.CharX(l, start, Sc);
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (T.CharX(l, start + mid, Sc) - x0 <= w) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        void Relayout()
        {
            if (_vbar == null || _hbar == null) return;
            EnsureRows();
            int w = ClientSize.Width, h = ClientSize.Height;
            // two passes: a bar appearing shrinks the area the other one is judged by
            bool needV = ContentH > h - 2 - 2 * Pad, needH = !_wrap && _contentW > w - 2 - 2 * Pad - GutterW;
            if (needV && !needH) needH = !_wrap && _contentW > w - 2 - 2 * Pad - GutterW - _vbar.Width;
            if (needH && !needV) needV = ContentH > h - 2 - 2 * Pad - _hbar.Height;
            _barSync = true;
            try
            {
                _vbar.Visible = needV; _hbar.Visible = needH;
                if (needV) { _vbar.Bounds = new Rectangle(w - 15, 1, 14, h - 2 - (needH ? 14 : 0)); }
                if (needH) { _hbar.Bounds = new Rectangle(1, h - 15, w - 2 - (needV ? 14 : 0), 14); }
                if (_wrap) EnsureRows();                       // the text width changed with the bar
                var ta = TextArea;
                _scrollY = Math.Clamp(_scrollY, 0, MaxScrollY); _scrollX = Math.Clamp(_scrollX, 0, MaxScrollX);
                if (needV) { _vbar.Minimum = 0; _vbar.Maximum = ContentH; _vbar.PageSize = ta.Height; _vbar.Step = RowH; _vbar.Value = _scrollY; }
                if (needH) { _hbar.Minimum = 0; _hbar.Maximum = _contentW; _hbar.PageSize = ta.Width; _hbar.Step = T.GlyphAdvance(Sc) * 4; _hbar.Value = _scrollX; }
            }
            finally { _barSync = false; }
            Redraw();
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (_wrap) _rowsDirty = true; Relayout(); }
        protected override void OnTextFontChanged() { _rowsDirty = true; Relayout(); base.OnTextFontChanged(); }
        protected override void OnTextScaleChanged() { _rowsDirty = true; Relayout(); }

        void SetScroll(int y, int x)
        {
            _scrollY = Math.Clamp(y, 0, MaxScrollY); _scrollX = Math.Clamp(x, 0, MaxScrollX);
            _barSync = true; try { if (_vbar.Visible) _vbar.Value = _scrollY; if (_hbar.Visible) _hbar.Value = _scrollX; } finally { _barSync = false; }
            Redraw();
        }
        /// <summary>Scrolls so the last line is visible.</summary>
        public void ScrollToEnd() { EnsureRows(); SetScroll(MaxScrollY, _scrollX); }
        /// <summary>Scrolls so <paramref name="line"/> is at the top (or as far as the content allows).</summary>
        public void ScrollToLine(int line)
        {
            EnsureRows();
            int row = 0; while (row < _rows.Count && _rows[row].line < line) row++;
            SetScroll(row * RowH, _scrollX);
        }
        /// <summary>Index of the first line currently shown.</summary>
        [Browsable(false)] public int TopLine { get { EnsureRows(); int r = Math.Clamp(_scrollY / Math.Max(1, RowH), 0, Math.Max(0, _rows.Count - 1)); return _rows.Count == 0 ? 0 : _rows[r].line; } }

        // ------------------------------------------------------------------ hit testing
        (int line, int col) HitTest(Point p)
        {
            EnsureRows();
            if (_rows.Count == 0) return (-1, -1);
            var ta = TextArea;
            int row = Math.Clamp((p.Y - ta.Y + _scrollY) / Math.Max(1, RowH), 0, _rows.Count - 1);
            var (line, start, len) = _rows[row];
            string t = _lines[line];
            float x = p.X - ta.X + _scrollX;
            int col;
            if (_wrap)
            {   // IndexAt on the row's own text; x relative to the row start
                var seg = t.Substring(start, len);
                col = start + T.IndexAt(seg, x, Sc);
            }
            else col = T.IndexAt(t, x, Sc);
            return (line, Math.Clamp(col, 0, t.Length));
        }

        // ------------------------------------------------------------------ mouse
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            var h = HitTest(e.Location); if (h.line < 0) return;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (!shift || _anchor.line < 0) _anchor = h;
            _caret = h; _selecting = true; Capture = true; Redraw();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_selecting) return;
            var ta = TextArea;
            if (e.Y < ta.Y) SetScroll(_scrollY - RowH, _scrollX); else if (e.Y > ta.Bottom) SetScroll(_scrollY + RowH, _scrollX);
            var h = HitTest(e.Location); if (h.line >= 0 && h != _caret) { _caret = h; Redraw(); }
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { _selecting = false; Capture = false; } }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var h = HitTest(e.Location); if (h.line < 0) return;
            string t = _lines[h.line]; int a = h.col, b = h.col;
            static bool W(char c) => char.IsLetterOrDigit(c) || c == '_';
            if (a < t.Length && W(t[a]) || (a > 0 && W(t[a - 1])))
            {
                while (a > 0 && W(t[a - 1])) a--;
                while (b < t.Length && W(t[b])) b++;
            }
            else if (a < t.Length) b = a + 1;
            _anchor = (h.line, a); _caret = (h.line, b); Redraw();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int d = Math.Sign(e.Delta) * 3;
            if ((ModifierKeys & Keys.Shift) != 0 && !_wrap) SetScroll(_scrollY, _scrollX - d * T.GlyphAdvance(Sc) * 4);
            else SetScroll(_scrollY - d * RowH, _scrollX);
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }

        // ------------------------------------------------------------------ keyboard
        protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool ctrl = (e.Modifiers & Keys.Control) != 0; int page = Math.Max(RowH, TextArea.Height - RowH), step = T.GlyphAdvance(Sc) * 4;
            switch (e.KeyCode)
            {
                case Keys.Up: SetScroll(_scrollY - RowH, _scrollX); break;
                case Keys.Down: SetScroll(_scrollY + RowH, _scrollX); break;
                case Keys.Left: SetScroll(_scrollY, _scrollX - step); break;
                case Keys.Right: SetScroll(_scrollY, _scrollX + step); break;
                case Keys.PageUp: SetScroll(_scrollY - page, _scrollX); break;
                case Keys.PageDown: SetScroll(_scrollY + page, _scrollX); break;
                case Keys.Home: SetScroll(ctrl ? 0 : _scrollY, 0); break;
                case Keys.End: if (ctrl) ScrollToEnd(); else SetScroll(_scrollY, MaxScrollX); break;
                case Keys.A when ctrl: SelectAll(); break;
                case Keys.C when ctrl: case Keys.Insert when ctrl: Copy(); break;
                case Keys.Escape: if (HasSelection) ClearSelection(); else return; break;
                default: return;
            }
            e.Handled = true; e.SuppressKeyPress = true;
        }

        // ------------------------------------------------------------------ paint
        TextRun[]? RunsOf(int line)
        {
            if (!_runsDone[line]) { _runsDone[line] = true; if (_colorizer != null) _runs[line] = _colorizer(_lines[line]); }
            return _runs[line];
        }
        protected override void PaintControl(Sprite s)
        {
            EnsureRows();
            int w = ClientSize.Width, h = ClientSize.Height, sc = Sc, rh = RowH;
            bool focus = Focused;
            int face = Mix(BackColor, Black, 0.25f), rim = focus ? Accent : Mix(BackColor, ThumbColor, IsHot && Enabled ? 0.5f : 0.3f);
            FillRound(s, 0.5f, 0.5f, w - 1, h - 1, 3f, face);
            var inner = Inner; var ta = TextArea;
            int fore = Fore, selCol = Mix(AccentRaw, BackColor, focus ? 0.35f : 0.55f), dim = Mix(fore, face, 0.5f);
            var (sa, sb) = Ordered(); bool hasSel = sa.line >= 0 && sa != sb;
            int gutter = GutterW;
            if (gutter > 0)
            {
                s.FillRect(inner.X, inner.Y, gutter + Pad - 2, inner.Height, Mix(face, BackColor, 0.5f));
                s.FillRect(inner.X + gutter + Pad - 2, inner.Y, 1, inner.Height, Mix(face, ThumbColor, 0.12f));
            }
            using (var v = s.CreateView(new Rectangle(inner.X, ta.Y, inner.Width, ta.Height)))
            {
                int first = Math.Clamp(_scrollY / Math.Max(1, rh), 0, Math.Max(0, _rows.Count - 1));
                int last = Math.Min(_rows.Count - 1, (_scrollY + ta.Height) / Math.Max(1, rh) + 1);
                int lastNumbered = -1;
                for (int r = first; r <= last && r < _rows.Count; r++)
                {
                    var (line, start, len) = _rows[r];
                    int y = ta.Y + r * rh - _scrollY;
                    string full = _lines[line];
                    int x0 = ta.X - _scrollX;
                    if (gutter > 0 && line != lastNumbered)
                    {
                        lastNumbered = line;
                        T.Draw(s, ta.X - Pad - 4, y, (line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), dim, 0, sc, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopRight);
                    }
                    // selection band for this row
                    if (hasSel && line >= sa.line && line <= sb.line)
                    {
                        int c0 = line == sa.line ? Math.Max(sa.col, start) : start, c1 = line == sb.line ? Math.Min(sb.col, start + len) : start + len;
                        if (c1 > c0 || (line < sb.line && c1 >= c0))
                        {
                            int xa = x0 + RowX(full, start, c0, sc), xb = x0 + RowX(full, start, c1, sc);
                            if (line < sb.line && c1 == start + len) xb += T.GlyphAdvance(sc) / 2;   // the line break
                            v.FillRect(xa, y, Math.Max(2, xb - xa), rh, selCol);
                        }
                    }
                    if (len == 0) continue;
                    var runs = RunsOf(line);
                    if (runs == null || runs.Length == 0) { T.Draw(v, x0, y, start == 0 && len == full.Length ? full : full.Substring(start, len), fore, 0, sc); continue; }
                    // coloured pieces: the row's columns cut by the runs; uncovered stretches in ForeColor
                    int col = start, end = start + len;
                    foreach (var run in runs)
                    {
                        int rs = Math.Max(run.Start, col), re = Math.Min(run.Start + run.Length, end);
                        if (re <= rs) { if (run.Start >= end) break; continue; }
                        if (rs > col) T.Draw(v, x0 + RowX(full, start, col, sc), y, full.Substring(col, rs - col), fore, 0, sc);
                        T.Draw(v, x0 + RowX(full, start, rs, sc), y, full.Substring(rs, re - rs), run.Color == 0 ? fore : run.Color, 0, sc);
                        col = re;
                    }
                    if (col < end) T.Draw(v, x0 + RowX(full, start, col, sc), y, full.Substring(col, end - col), fore, 0, sc);
                }
            }
            if (_vbar.Visible && _hbar.Visible) s.FillRect(w - 15, h - 15, 14, 14, face);
            DrawRound(s, 0.5f, 0.5f, w - 1, h - 1, 3f, rim, 1f);
        }
        /// <summary>Pen x of column <paramref name="col"/> relative to the row that starts at <paramref name="start"/>.</summary>
        int RowX(string line, int start, int col, int sc) => start == 0 ? T.CharX(line, col, sc) : T.CharX(line, col, sc) - T.CharX(line, start, sc);
        protected override bool OwnFocusRect => true;
    }
}
