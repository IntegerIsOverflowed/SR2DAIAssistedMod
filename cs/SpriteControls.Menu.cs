using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // ImplicitUsings also brings System.Threading.Timer

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------------------------------------------------------
    // SpriteMenu: an SR2D-drawn popup menu (the replacement for ContextMenuStrip on SR2D controls). Same palette as the
    // other controls (dark body, accent for the hot row, pixel font). Items: command, check box, radio (grouped), separator,
    // header, sub-menu and an inline slider row. Keyboard: Up / Down, Right / Enter opens a sub-menu or activates, Left /
    // Escape closes one level, Home / End, first letter jumps. Mouse: hover highlights and opens sub-menus after a short
    // delay, a click activates; a click anywhere outside closes the whole chain (the click is NOT swallowed: it reaches
    // the control underneath, like modern apps). Check / radio / slider rows keep the menu open so several settings can
    // be changed in one visit (Sticky); commands close it.
    //
    //     var m = new SpriteMenu();
    //     m.Add("Reset view", () => view.Reset());
    //     m.AddCheck("Scroll bars", () => box.ScrollBars, v => box.ScrollBars = v);
    //     var cam = m.AddSub("Camera"); foreach (...) cam.AddRadio(name, () => current == i, () => current = i);
    //     m.AddSlider("Zoom", 0.1, 8, () => zoom, v => zoom = v, decimals: 2);
    //     m.Show(control, e.Location);
    //
    // The popup lives in a borderless tool window (no activation stealing: ShowWithoutActivation + WS_EX_NOACTIVATE), so
    // the owner keeps focus and keyboard input is forwarded through the message filter.
    // ------------------------------------------------------------------------------------------------------------------------

    public enum MenuItemKind { Command, Check, Radio, Separator, Header, SubMenu, Slider }

    /// <summary>One row of a <see cref="SpriteMenu"/>. Build them with the Add* helpers on the menu.</summary>
    public sealed class SpriteMenuItem
    {
        public MenuItemKind Kind;
        public string Text = "";
        /// <summary>Right-aligned hint (a shortcut or the current value).</summary>
        public string? Hint;
        public Func<string>? HintProvider;
        public Action? Click;
        public Func<bool>? IsChecked;
        public Action<bool>? SetChecked;
        public Func<bool>? IsEnabled;
        public SpriteMenu? Sub;
        public object? Tag { get; set; }
        // slider rows
        public double Min, Max = 1, Step; public int Decimals; public string Unit = "";
        public Func<double>? GetValue; public Action<double>? SetValue;
        public bool Enabled => IsEnabled?.Invoke() ?? true;
        public bool Selectable => Kind != MenuItemKind.Separator && Kind != MenuItemKind.Header && Enabled;
        public override string ToString() => Kind + " " + Text;
    }

    /// <summary>An SR2D-drawn popup menu. See the file header for usage.</summary>
    public sealed class SpriteMenu : IDisposable
    {
        public readonly List<SpriteMenuItem> Items = new List<SpriteMenuItem>();
        /// <summary>Minimum popup width in pixels (default 160).</summary>
        public int MinWidth = 160;
        /// <summary>Text scale (1 = 5x7 pixels per glyph, 2 = double). Default 1; menus on large controls may use 2.</summary>
        public int TextScale = 1;
        /// <summary>Font for the menu text: a font object, or an installed family name; both empty = the app default (SpriteControlBase.DefaultFont*) or the pixel font.</summary>
        public SpriteFont? Font; public string? FontFamily; public float FontSize;
        /// <summary>Raised before the menu is shown (refresh item texts / enabled states here).</summary>
        public event Action<SpriteMenu>? Opening;
        /// <summary>Raised after the whole menu chain closed.</summary>
        public event Action<SpriteMenu>? Closed;
        /// <summary>Optional title row drawn at the top.</summary>
        public string? Title;
        public Color BackColor = Color.FromArgb(0x24, 0x28, 0x2E), ForeColor = Color.White, AccentColor = Color.FromArgb(0x33, 0x99, 0xFF), BorderColor = Color.FromArgb(0x50, 0x58, 0x60);

        SpriteMenuPanel? _panel;
        internal SpriteMenu? Parent; internal SpriteMenuItem? ParentItem;

        // ------------------------------------------------------------------ building
        public SpriteMenuItem Add(SpriteMenuItem it) { Items.Add(it); return it; }
        public SpriteMenuItem Add(string text, Action click, string? hint = null, Func<bool>? enabled = null) => Add(new SpriteMenuItem { Kind = MenuItemKind.Command, Text = text, Click = click, Hint = hint, IsEnabled = enabled });
        public SpriteMenuItem AddCheck(string text, Func<bool> isChecked, Action<bool> set, Func<bool>? enabled = null) => Add(new SpriteMenuItem { Kind = MenuItemKind.Check, Text = text, IsChecked = isChecked, SetChecked = set, IsEnabled = enabled });
        public SpriteMenuItem AddRadio(string text, Func<bool> isChecked, Action select, Func<bool>? enabled = null) => Add(new SpriteMenuItem { Kind = MenuItemKind.Radio, Text = text, IsChecked = isChecked, Click = select, IsEnabled = enabled });
        public SpriteMenuItem AddSeparator() => Add(new SpriteMenuItem { Kind = MenuItemKind.Separator });
        public SpriteMenuItem AddHeader(string text) => Add(new SpriteMenuItem { Kind = MenuItemKind.Header, Text = text });
        public SpriteMenu AddSub(string text, Func<bool>? enabled = null) { var m = new SpriteMenu { Parent = this, TextScale = TextScale, Font = Font, FontFamily = FontFamily, FontSize = FontSize, BackColor = BackColor, ForeColor = ForeColor, AccentColor = AccentColor, BorderColor = BorderColor }; var it = Add(new SpriteMenuItem { Kind = MenuItemKind.SubMenu, Text = text, Sub = m, IsEnabled = enabled }); m.ParentItem = it; return m; }
        public SpriteMenuItem AddSlider(string text, double min, double max, Func<double> get, Action<double> set, double step = 0, int decimals = 0, string unit = "")
            => Add(new SpriteMenuItem { Kind = MenuItemKind.Slider, Text = text, Min = min, Max = max, Step = step, Decimals = decimals, Unit = unit, GetValue = get, SetValue = set });
        /// <summary>Radio group from an enum-like list: <paramref name="names"/>[i] selects index i.</summary>
        public void AddRadioGroup(IList<string> names, Func<int> current, Action<int> select) { for (int i = 0; i < names.Count; i++) { int k = i; AddRadio(names[i], () => current() == k, () => select(k)); } }
        public void Clear() { Items.Clear(); }

        // ------------------------------------------------------------------ showing
        /// <summary>True while this menu (or one of its sub-menus) is on screen.</summary>
        public bool IsOpen => _panel != null && _panel.Visible;
        /// <summary>Pops the menu up at a client point of <paramref name="owner"/>. Pass the button that opened it so an
        /// outside click can tell a re-open from a fresh gesture - see <see cref="ShowAt"/>.</summary>
        public void Show(Control owner, Point clientPoint, MouseButtons openButton = MouseButtons.Left) => ShowAt(owner, owner.PointToScreen(clientPoint), openButton: openButton);
        /// <summary>Pops the menu up at a screen point. The root menu handles outside clicks and keyboard input for the whole chain.
        /// <paramref name="openButton"/> only matters for the root: a menu opened by the LEFT button re-opens on the next left click
        /// over its owner, so that click is swallowed and merely closes the menu; a right-click context menu is not, because the
        /// next left click there is a real gesture (a drag on the control under it) and must reach it.</summary>
        public void ShowAt(Control owner, Point screenPoint, bool asSubmenu = false, MouseButtons openButton = MouseButtons.Left)
        {
            Opening?.Invoke(this);
            _panel ??= new SpriteMenuPanel(this);
            if (!asSubmenu) OpenedBy = openButton;
            _panel.Popup(owner, screenPoint, asSubmenu);
        }
        /// <summary>The button that opened this root menu (<see cref="ShowAt"/>).</summary>
        internal MouseButtons OpenedBy { get; private set; } = MouseButtons.Left;
        /// <summary>Closes this menu and its open sub-menus (and, when <paramref name="chain"/>, the parents too).</summary>
        public void Close(bool chain = true)
        {
            var root = this; if (chain) while (root.Parent != null) root = root.Parent;
            root.CloseDown();
        }
        internal void CloseDown()
        {
            foreach (var it in Items) it.Sub?.CloseDown();
            if (_panel != null && _panel.Visible) { _panel.Hide(); if (Parent == null) { SpriteMenuPanel.RootClosed(this); Closed?.Invoke(this); } }
        }
        internal SpriteMenuPanel? Panel => _panel;
        internal void Refresh() { if (_panel != null && _panel.Visible) _panel.LayoutRows(); }
        public void Dispose() { CloseDown(); foreach (var it in Items) it.Sub?.Dispose(); _panel?.Dispose(); _panel = null; }   // an open menu leaves OpenRoots / the message filter first
    }

    /// <summary>The window of one open menu level: an SR2D-painted SpriteBox in a borderless, non-activating tool window.</summary>
    public sealed class SpriteMenuPanel : SpriteBox
    {
        readonly SpriteMenu _menu; MenuHost? _host; Control? _owner;
        readonly List<Rectangle> _rows = new List<Rectangle>(); int _hot = -1, _pressedSlider = -1; int _padX = 10, _rowH, _sepH, _sliderH, _ts;
        Timer? _subTimer; SpriteMenu? _openSub; Size _want;
        static readonly List<SpriteMenu> OpenRoots = new List<SpriteMenu>();
        static OutsideClickFilter? _filter;

        public SpriteMenuPanel(SpriteMenu m)
        {
            _menu = m; Visible = false; TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }
        internal SpriteMenu Menu => _menu;
        internal Control? Owner => _owner;

        // ------------------------------------------------------------------ host window
        sealed class MenuHost : Form
        {
            public MenuHost() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; }
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/; return cp; } }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0021 /*WM_MOUSEACTIVATE*/) { m.Result = (IntPtr)3 /*MA_NOACTIVATE*/; return; }
                base.WndProc(ref m);
            }
        }

        public void Popup(Control owner, Point screen, bool asSubmenu)
        {
            _owner = owner;
            if (_host == null) { _host = new MenuHost(); _host.Controls.Add(this); Dock = DockStyle.Fill; }
            // owned by the owner's top-level window: stays above it (no TopMost needed) and goes with it when another window
            // is activated - an owned window never floats over other applications
            var top = owner.FindForm();
            if (top != null && _host.Owner != top) { try { _host.Owner = top; } catch { } }
            if (!asSubmenu && top != null) { top.Deactivate -= OwnerDeactivated; top.Deactivate += OwnerDeactivated; top.LocationChanged -= OwnerMoved; top.LocationChanged += OwnerMoved; top.SizeChanged -= OwnerMoved; top.SizeChanged += OwnerMoved; _top = top; }
            LayoutRows();
            var area = Screen.FromPoint(screen).WorkingArea;
            int x = screen.X, y = screen.Y, w = _want.Width, h = _want.Height;
            if (asSubmenu)
            {   // to the right of the parent row; flip to the left when there is no room
                if (x + w > area.Right && _menu.ParentItem != null && _menu.Parent?.Panel != null) x = _menu.Parent.Panel._host!.Left - w + 2;
            }
            if (x + w > area.Right) x = Math.Max(area.Left, area.Right - w);
            if (y + h > area.Bottom) y = Math.Max(area.Top, area.Bottom - h);
            _host.Bounds = new Rectangle(x, y, w, h);
            _hot = -1;
            Visible = true;
            if (!_host.Visible) _host.Show(); else _host.Invalidate();
            Redraw();
            if (!asSubmenu)
            {
                if (!OpenRoots.Contains(_menu)) OpenRoots.Add(_menu);
                EnsureFilter();
                Capture = true;   // the menu owns the mouse until it closes (like the native menus): every later click
                                  // lands HERE, none can be lost to an activation / hit-test race ("first click after
                                  // moving over the list did nothing"); outside clicks close it (and still pass through
                                  // - the message filter closes the menu and releases the capture before dispatch)
            }
        }
#pragma warning disable CA2213 // the owner form (event subscriptions only) - not owned, never disposed here
        Form? _top;
#pragma warning restore CA2213
        void OwnerDeactivated(object? s, EventArgs e) { if (_menu.Parent == null) _menu.Close(); }
        void OwnerMoved(object? s, EventArgs e) { if (_menu.Parent == null) _menu.Close(); }
        public new void Hide()
        {
            if (_top != null) { _top.Deactivate -= OwnerDeactivated; _top.LocationChanged -= OwnerMoved; _top.SizeChanged -= OwnerMoved; _top = null; }
            CloseSub();
            _subTimer?.Stop();
            if (Capture) Capture = false;
            if (_host != null && _host.Visible) _host.Hide();
            Visible = false; _hot = -1; _pressedSlider = -1;
        }
        internal static void RootClosed(SpriteMenu m) { OpenRoots.Remove(m); if (OpenRoots.Count == 0 && _filter != null) { Application.RemoveMessageFilter(_filter); _filter = null; } }
        static void EnsureFilter() { if (_filter == null) { _filter = new OutsideClickFilter(); Application.AddMessageFilter(_filter); } }

        /// <summary>Closes open menus on a click outside any of their windows; routes keyboard input to the deepest open level.</summary>
        sealed class OutsideClickFilter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                switch (m.Msg)
                {
                    case 0x0201: case 0x0204: case 0x0207: case 0x00A1: case 0x00A4: case 0x00A7:   // L/R/M button down (client + non-client)
                    {
                        var p = Cursor.Position;
                        foreach (var root in OpenRoots.ToArray())
                        {
                            if (root.ChainContains(p)) continue;                    // over the menu itself: normal dispatch (it holds the capture)
                            var opener = root.Panel?.Owner;                         // the control whose click opened the menu
                            bool onOpener = opener != null && opener.Visible && opener.RectangleToScreen(opener.ClientRectangle).Contains(p);
                            root.CloseDown();                                       // any click outside the chain closes it
                            // A left click on the opener of a left-click menu ONLY closes it: swallowing it stops the field from
                            // toggling open again on the very same click ("one click closes"). A right-click context menu does
                            // not get that treatment - its next left click is a real gesture on the control underneath.
                            if (m.Msg == 0x0201 && onOpener && root.OpenedBy != MouseButtons.Right) return true;
                        }
                        return false;   // never swallow anything else: the click goes to whatever is under the pointer
                    }
                    case 0x0100: case 0x0104:   // WM_KEYDOWN / WM_SYSKEYDOWN
                    {
                        if (OpenRoots.Count == 0) return false;
                        var root = OpenRoots[^1]; var deep = root.Deepest();
                        return deep != null && deep.Key((Keys)(int)m.WParam);
                    }
                    case 0x020A:   // wheel: over a slider row adjusts it
                    {
                        if (OpenRoots.Count == 0) return false;
                        var p = Cursor.Position; foreach (var root in OpenRoots) { var pn = root.PanelAt(p); if (pn != null) { pn.Wheel(pn.PointToClient(p), (short)((long)m.WParam >> 16)); return true; } }
                        return false;
                    }
                }
                return false;
            }
        }

        // ------------------------------------------------------------------ layout
        ControlText T => _menu.Font != null ? new ControlText(_menu.Font, null, _menu.FontSize > 0 ? _menu.FontSize : SpriteControlBase.DefaultFontSize)
                       : !string.IsNullOrEmpty(_menu.FontFamily) && ControlFonts.Get(_menu.FontFamily) is SpriteFont mf ? new ControlText(mf, ControlFonts.Get(_menu.FontFamily, true), _menu.FontSize > 0 ? _menu.FontSize : SpriteControlBase.DefaultFontSize)
                       : SpriteControlBase.DefaultFont != null ? new ControlText(SpriteControlBase.DefaultFont, null, _menu.FontSize > 0 ? _menu.FontSize : SpriteControlBase.DefaultFontSize)
                       : !string.IsNullOrEmpty(SpriteControlBase.DefaultFontFamily) && ControlFonts.Get(SpriteControlBase.DefaultFontFamily) is SpriteFont df ? new ControlText(df, ControlFonts.Get(SpriteControlBase.DefaultFontFamily, true), _menu.FontSize > 0 ? _menu.FontSize : SpriteControlBase.DefaultFontSize)
                       : ControlText.Pixel;
        internal void LayoutRows()
        {
            _ts = Math.Max(1, _menu.TextScale);
            _rowH = T.GlyphHeight(_ts) + 2 * _ts + 10; _sepH = 7; _sliderH = _rowH + 12 * _ts; _padX = 10 * _ts;
            int w = _menu.MinWidth, y = 4;
            _rows.Clear();
            if (_menu.Title != null) { w = Math.Max(w, T.Measure(_menu.Title, _ts).Width + _padX * 2); y += _rowH; }
            foreach (var it in _menu.Items)
            {
                int h = it.Kind == MenuItemKind.Separator ? _sepH : it.Kind == MenuItemKind.Slider ? _sliderH : _rowH;
                _rows.Add(new Rectangle(0, y, 0, h)); y += h;
                int tw = T.Measure(it.Text, _ts).Width + _padX * 2 + 18 * _ts;
                string? hint = HintOf(it); if (hint != null) tw += T.Measure(hint, _ts).Width + 16 * _ts;
                if (it.Kind == MenuItemKind.SubMenu) tw += 14 * _ts;
                w = Math.Max(w, tw);
            }
            y += 4;
            _want = new Size(w, y);
            for (int i = 0; i < _rows.Count; i++) _rows[i] = new Rectangle(0, _rows[i].Y, w, _rows[i].Height);
            if (_host != null) _host.ClientSize = _want; else Size = _want;
        }
        static string? HintOf(SpriteMenuItem it) => it.HintProvider != null ? it.HintProvider() : it.Hint;

        // ------------------------------------------------------------------ painting
        protected override bool HasRenderer => true;
        protected override void OnRender(Sprite s)
        {
            int bg = _menu.BackColor.ToArgb(), fore = _menu.ForeColor.ToArgb(), accent = _menu.AccentColor.ToArgb();
            int dim = Mix(_menu.ForeColor, _menu.BackColor, 0.55f), line = Mix(_menu.BorderColor, _menu.BackColor, 0.5f);
            s.ClearBuffer(bg);
            int y = 4;
            if (_menu.Title != null)
            {
                T.Draw(s, _padX, y + 5, _menu.Title, dim, 0, _ts);
                s.FillRect(_padX, y + _rowH - 3, s.Width - _padX * 2, 1, line); y += _rowH;
            }
            Span<PointF> tri = stackalloc PointF[3];
            for (int i = 0; i < _menu.Items.Count; i++)
            {
                var it = _menu.Items[i]; var r = _rows[i];
                bool hot = i == _hot && it.Selectable;
                int tc = it.Enabled ? fore : Mix(_menu.ForeColor, _menu.BackColor, 0.4f);
                if (hot) { s.FillRect(2, r.Y, s.Width - 4, r.Height, Mix(_menu.AccentColor, _menu.BackColor, 0.35f)); }
                int cy = r.Y + (it.Kind == MenuItemKind.Slider ? 5 : (r.Height - T.GlyphHeight(_ts)) / 2);
                switch (it.Kind)
                {
                    case MenuItemKind.Separator: s.FillRect(_padX, r.Y + r.Height / 2, s.Width - _padX * 2, 1, line); continue;
                    case MenuItemKind.Header: T.Draw(s, _padX, cy, it.Text, dim, 0, _ts); continue;
                    case MenuItemKind.Check:
                    {
                        bool on = it.IsChecked?.Invoke() ?? false; float bx = _padX + 4 * _ts, by = r.Y + r.Height / 2f, bs = 5f * _ts;
                        s.DrawRect(bx - bs, by - bs, bs * 2, bs * 2, on ? accent : dim, 1f, true);
                        if (on) { Span<PointF> tick = stackalloc PointF[] { new PointF(bx - bs * 0.55f, by), new PointF(bx - bs * 0.1f, by + bs * 0.5f), new PointF(bx + bs * 0.6f, by - bs * 0.55f) }; s.DrawPolyline(tick, accent, 1.6f * _ts, true); }
                        break;
                    }
                    case MenuItemKind.Radio:
                    {
                        bool on = it.IsChecked?.Invoke() ?? false; float bx = _padX + 4 * _ts, by = r.Y + r.Height / 2f, rr = 5f * _ts;
                        s.DrawCircle(bx, by, rr, on ? accent : dim, 1f, true);
                        if (on) s.FillCircle(bx, by, rr * 0.5f, accent, SR2D.LineOp.Set, true);
                        break;
                    }
                    case MenuItemKind.Slider:
                    {
                        double v = it.GetValue?.Invoke() ?? it.Min; double f = it.Max > it.Min ? Math.Clamp((v - it.Min) / (it.Max - it.Min), 0, 1) : 0;
                        int tx = _padX, tw = s.Width - _padX * 2, ty = r.Y + r.Height - 8 * _ts - 2;
                        s.FillRect(tx, ty - 1, tw, 3 * _ts, Mix(_menu.BorderColor, _menu.BackColor, 0.3f));
                        s.FillRect(tx, ty - 1, (int)(tw * f), 3 * _ts, accent);
                        s.FillCircle(tx + (float)(tw * f), ty + 0.5f, 4.5f * _ts, i == _pressedSlider || hot ? accent : fore, SR2D.LineOp.Set, true);
                        string val = v.ToString("F" + it.Decimals, System.Globalization.CultureInfo.CurrentCulture) + it.Unit;
                        T.Draw(s, s.Width - _padX, cy, val, accent, 0, _ts, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopRight);
                        T.Draw(s, _padX, cy, it.Text, tc, 0, _ts);
                        continue;
                    }
                }
                int textX = _padX + (it.Kind == MenuItemKind.Check || it.Kind == MenuItemKind.Radio || HasToggles ? 14 * _ts : 0);
                T.Draw(s, textX, cy, it.Text, tc, 0, _ts);
                string? hint = HintOf(it);
                if (hint != null) T.Draw(s, s.Width - _padX - (it.Kind == MenuItemKind.SubMenu ? 10 * _ts : 0), cy, hint, hot ? fore : dim, 0, _ts, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopRight);
                if (it.Kind == MenuItemKind.SubMenu)
                {
                    float ax = s.Width - _padX - 2 * _ts, ay = r.Y + r.Height / 2f, a = 3.5f * _ts;
                    tri[0] = new PointF(ax - a, ay - a); tri[1] = new PointF(ax + a * 0.4f, ay); tri[2] = new PointF(ax - a, ay + a);
                    s.FillPolygon(tri, tc, SR2D.LineOp.Set, true);
                }
            }
            s.DrawRect(0.5f, 0.5f, s.Width - 1, s.Height - 1, _menu.BorderColor.ToArgb(), 1f, false);
        }
        bool HasToggles { get { foreach (var it in _menu.Items) if (it.Kind == MenuItemKind.Check || it.Kind == MenuItemKind.Radio) return true; return false; } }
        static int Mix(Color a, Color b, float t) => SR2D.ARGB(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        // ------------------------------------------------------------------ mouse
        int RowAt(Point p) { for (int i = 0; i < _rows.Count; i++) if (_rows[i].Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressedSlider >= 0) { SliderTo(_pressedSlider, e.X); return; }
            int i = RowAt(e.Location);
            if (i != _hot) { SetHot(i, true); }
        }
        void SetHot(int i, bool fromMouse)
        {
            _hot = i; Redraw();
            _subTimer?.Stop();
            var it = i >= 0 ? _menu.Items[i] : null;
            if (it != null && it.Kind == MenuItemKind.SubMenu && it.Enabled && it.Sub != _openSub)
            {
                if (fromMouse) { _subTimer ??= new Timer { Interval = 220 }; _subTimer.Tick -= SubTick; _subTimer.Tick += SubTick; _subTimer.Start(); }
                else OpenSub(i);
            }
            else if (_openSub != null && (it == null || it.Sub != _openSub) && fromMouse) { _subTimer ??= new Timer { Interval = 220 }; _subTimer.Tick -= SubTick; _subTimer.Tick += SubTick; _subTimer.Start(); }
        }
        void SubTick(object? s, EventArgs e)
        {
            _subTimer!.Stop();
            var it = _hot >= 0 ? _menu.Items[_hot] : null;
            if (it != null && it.Kind == MenuItemKind.SubMenu && it.Enabled) OpenSub(_hot);
            else if (_openSub != null && !(_openSub.Panel?.IsHot ?? false)) CloseSub();
        }
        bool IsHot => _hot >= 0 || (_openSub?.Panel?.IsHot ?? false);
        void OpenSub(int i)
        {
            var it = _menu.Items[i]; if (it.Sub == null) return;
            if (_openSub != null && _openSub != it.Sub) CloseSub();
            _openSub = it.Sub;
            var r = _rows[i];
            if (Capture) Capture = false;   // the sub-menu level navigates through the filter - a held capture would route its clicks here
            it.Sub.ShowAt(_owner!, _host!.PointToScreen(new Point(Width - 4, r.Y - 4)), asSubmenu: true);
        }
        void CloseSub() { if (_openSub != null) { var s = _openSub; _openSub = null; s.CloseDown(); } if (_menu.IsOpen && Visible && !Capture) Capture = true; }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_pressedSlider >= 0) return;
            // keep the row of an open sub-menu lit
            if (_openSub != null) { int keep = _menu.Items.FindIndex(x => x.Sub == _openSub); if (_hot != keep) { _hot = keep; Redraw(); } }
            else if (_hot >= 0) { _hot = -1; Redraw(); }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Visible) return;                       // the capture can still deliver events right after the menu closed
            int i = RowAt(e.Location);
            if (i < 0) { _menu.Close(); return; }       // a click outside the rows closes the menu (native behaviour); for clicks
                                                        // on other windows the filter releases the capture first, so they pass through
            var it = _menu.Items[i];
            if (it.Kind == MenuItemKind.Slider && it.Enabled) { _pressedSlider = i; Capture = true; SliderTo(i, e.X); return; }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Visible) return;
            if (Capture) Capture = false;
            if (_pressedSlider >= 0) { _pressedSlider = -1; Redraw(); return; }
            if (e.Button != MouseButtons.Left) return;
            int i = RowAt(e.Location);
            if (i < 0) i = RowAt(PointToClient(Cursor.Position));   // an up with a stale / zeroed position still activates the row under the pointer
            if (i < 0) return;
            Activate(i);
        }
        void SliderTo(int i, int x)
        {
            var it = _menu.Items[i]; int tx = _padX, tw = Width - _padX * 2;
            double f = Math.Clamp((x - tx) / (double)Math.Max(1, tw), 0, 1), v = it.Min + f * (it.Max - it.Min);
            if (it.Step > 0) v = Math.Round((v - it.Min) / it.Step) * it.Step + it.Min;
            v = Math.Clamp(v, it.Min, it.Max);
            it.SetValue?.Invoke(v); Redraw();
        }
        internal void Wheel(Point p, int delta)
        {
            int i = RowAt(p); if (i < 0) return; var it = _menu.Items[i];
            if (it.Kind != MenuItemKind.Slider) return;
            double step = it.Step > 0 ? it.Step : (it.Max - it.Min) / 50;
            double v = Math.Clamp((it.GetValue?.Invoke() ?? it.Min) + Math.Sign(delta) * step, it.Min, it.Max);
            it.SetValue?.Invoke(v); Redraw();
        }
        void Activate(int i)
        {
            var it = _menu.Items[i]; if (!it.Selectable) return;
            switch (it.Kind)
            {
                case MenuItemKind.Command: _menu.Close(); it.Click?.Invoke(); break;
                case MenuItemKind.Check: it.SetChecked?.Invoke(!(it.IsChecked?.Invoke() ?? false)); RefreshChain(); break;
                case MenuItemKind.Radio: it.Click?.Invoke(); RefreshChain(); break;
                case MenuItemKind.SubMenu: OpenSub(i); break;
            }
        }
        void RefreshChain() { var m = _menu; while (m.Parent != null) m = m.Parent; m.RefreshAll(); }

        // ------------------------------------------------------------------ keyboard (routed by the filter to the deepest open level)
        internal bool Key(Keys k)
        {
            if ((k & Keys.Alt) != 0 || (k & Keys.Control) != 0) return false;   // Alt+F4 / Ctrl+C / shortcuts belong to the form, a menu never takes them
            k &= Keys.KeyCode;
            switch (k)
            {
                case Keys.Escape: case Keys.Left:
                    if (_menu.Parent != null) { _menu.CloseDown(); _menu.Parent.Panel?.SubClosedByKey(); } else _menu.Close();
                    return true;
                case Keys.Down: MoveHot(1); return true;
                case Keys.Up: MoveHot(-1); return true;
                case Keys.Home: { for (int i = 0; i < _menu.Items.Count; i++) if (_menu.Items[i].Selectable) { SetHot(i, false); break; } return true; }
                case Keys.End: { for (int i = _menu.Items.Count - 1; i >= 0; i--) if (_menu.Items[i].Selectable) { SetHot(i, false); break; } return true; }
                case Keys.Right:
                    if (_hot >= 0 && _menu.Items[_hot].Kind == MenuItemKind.SubMenu) { OpenSub(_hot); _openSub?.Panel?.MoveHot(1); return true; }
                    if (_hot >= 0 && _menu.Items[_hot].Kind == MenuItemKind.Slider) { Nudge(_hot, 1); return true; }
                    return true;
                case Keys.Enter: case Keys.Space: if (_hot >= 0) { Activate(_hot); if (_menu.Items[_hot].Kind == MenuItemKind.SubMenu) _openSub?.Panel?.MoveHot(1); } return true;
                case Keys.Add: case Keys.Oemplus: if (_hot >= 0 && _menu.Items[_hot].Kind == MenuItemKind.Slider) { Nudge(_hot, 1); return true; } break;
                case Keys.Subtract: case Keys.OemMinus: if (_hot >= 0 && _menu.Items[_hot].Kind == MenuItemKind.Slider) { Nudge(_hot, -1); return true; } break;
            }
            // first-letter navigation (plain keys only - the modifiers were sent back to the form above)
            char c = (char)k; if (char.IsLetterOrDigit(c))
            {
                int n = _menu.Items.Count;
                for (int s = 1; s <= n; s++) { int i = ((_hot < 0 ? -1 : _hot) + s + n) % n; var it = _menu.Items[i]; if (it.Selectable && it.Text.Length > 0 && char.ToUpperInvariant(it.Text[0]) == char.ToUpperInvariant(c)) { SetHot(i, false); return true; } }
            }
            return false;  // not a key the menu uses: let it reach the focused control / the form (Alt, F-keys, Tab, clipboard...)
        }
        void Nudge(int i, int dir) { var it = _menu.Items[i]; double step = it.Step > 0 ? it.Step : (it.Max - it.Min) / 50; it.SetValue?.Invoke(Math.Clamp((it.GetValue?.Invoke() ?? it.Min) + dir * step, it.Min, it.Max)); Redraw(); }
        internal void MoveHot(int dir)
        {
            int n = _menu.Items.Count; if (n == 0) return;
            int i = _hot;
            for (int s = 0; s < n; s++) { i = ((i < 0 ? (dir > 0 ? -1 : 0) : i) + dir + n) % n; if (_menu.Items[i].Selectable) { SetHot(i, false); return; } }
        }
        internal void SubClosedByKey() { _openSub = null; Redraw(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_top != null) { _top.Deactivate -= OwnerDeactivated; _top.LocationChanged -= OwnerMoved; _top.SizeChanged -= OwnerMoved; _top = null; }
                RootClosed(_menu);                      // never leave a dead panel in the open list (the filter would touch it on every click)
                _subTimer?.Dispose(); _host?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class SpriteMenuExtensions
    {
        /// <summary>True when the screen point is over this menu or any open sub-menu.</summary>
        internal static bool ChainContains(this SpriteMenu m, Point screen) => m.PanelAt(screen) != null;
        internal static SpriteMenuPanel? PanelAt(this SpriteMenu m, Point screen)
        {
            var p = m.Panel;
            if (p != null && p.Visible && p.Parent is Form f && f.Visible && f.Bounds.Contains(screen)) return p;
            foreach (var it in m.Items) { if (it.Sub != null) { var r = it.Sub.PanelAt(screen); if (r != null) return r; } }
            return null;
        }
        /// <summary>The deepest open level (keyboard target).</summary>
        internal static SpriteMenuPanel? Deepest(this SpriteMenu m)
        {
            foreach (var it in m.Items) if (it.Sub != null && it.Sub.IsOpen) { var d = it.Sub.Deepest(); if (d != null) return d; }
            return m.IsOpen ? m.Panel : null;
        }
        internal static void RefreshAll(this SpriteMenu m) { m.Refresh(); m.Panel?.Redraw(); foreach (var it in m.Items) it.Sub?.RefreshAll(); }
    }
}
