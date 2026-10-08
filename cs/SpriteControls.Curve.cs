// SR2D - the curve editor: a small Photoshop-curves / 3ds Max "Color Map" style control - a 0..1 x 0..1 plot
// with draggable points, the curve between them drawn and evaluated by cs/Curve.cs (monotone cubic). Left
// drag moves / selects a point (a click nearer than Curve.MinGap to a point moves THAT one), a click on empty
// space adds one, right click selects the point under the cursor and opens the menu (the selected point's
// type: Auto / Flat / Linear, "Delete node" - the endpoints stay put, copy / paste the curve as C# code,
// clamp / compress, zoom, reset, help), double click a middle point switches its type
// (Auto -> Flat -> Linear). Right click never edits by itself: the point whose menu it opens would be gone
// before the menu could say anything.
// The mouse wheel zooms the VALUE axis anchored at the bottom (zero) line and the MIDDLE button pans
// up / down, so a curve whose Clamp01 is off can be shaped above 1 and below 0 (overshoot) - while it is
// on, the values stay inside 0..1 and the plot says so. Help lives in the menu (drawn over the plot).
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    public sealed class SpriteCurveEditor : SpriteControlBase
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.Graphic;
        Curve curve = new();
        int drag = -1, selected = -1;
        bool panning, showHelp;
        int helpScroll;
        Point panAt;
        double viewLo, viewHi = 1;                                             // the visible value range (wheel zooms, middle pans)
        double panLo, panHi;

        public SpriteCurveEditor() { Size = new Size(300, 170); curve.Changed += (_, _) => Redraw(); }   // an edit made on the Curve object itself (code, another control) repaints here
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _menu?.Close(); _menu?.Dispose(); _menu = null; } // CA2213: the popped-up menu is ours
            base.Dispose(disposing);
        }

        /// <summary>The edited curve. Assigning copies the points, the tangent modes and the clamping into the editor's own instance (the editor always edits the object it was built with, so a <see cref="Curve.Changed"/> subscription stays valid). Designer serialization is hidden - the curve is runtime data.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Curve Curve
        {
            get => curve;
            set
            {
                if (value == null || value == curve) return;
                curve.Clamp01 = value.Clamp01;                                  // BEFORE Set: clamping here would flatten an overshoot curve
                curve.Set(value.Points, value.Modes);
                selected = drag = -1; Redraw();
            }
        }
        bool guides = true;
        /// <summary>Draw the centre cross, the 0 / 1 lines and the identity diagonal (on by default).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden), System.ComponentModel.DefaultValue(true)]
        public bool Guides { get => guides; set { guides = value; Redraw(); } }

        // The caption band at the bottom is not plot: the endpoints of a curve sit AT the bottom edge (y = 0), so a
        // plot that ran under the text drew the caption straight through the two endpoint nodes and the 0.0 line.
        Rectangle Plot => new Rectangle(16, 8, Math.Max(40, ClientSize.Width - 24), Math.Max(40, ClientSize.Height - 16 - CapH));
        PointF ToPlot(PointF p)
        {
            var r = Plot;
            float x = r.Left + p.X * (r.Width - 1);
            float y = r.Bottom - 1 - (float)((p.Y - viewLo) / Math.Max(1e-6, viewHi - viewLo) * (r.Height - 1));
            return new PointF(x, y);
        }
        PointF FromPlot(Point px)
        {
            var r = Plot;
            float y = (float)Math.Clamp(viewLo + (r.Bottom - 1 - px.Y) / (double)Math.Max(1, r.Height - 1) * (viewHi - viewLo), -3.0, 3.0);
            return new PointF(Math.Clamp((px.X - r.Left) / (float)Math.Max(1, r.Width - 1), 0f, 1f), y);
        }

        /// <summary>The point under <paramref name="screen"/> within <paramref name="px"/> SCREEN pixels. A round grab area: the normalised <see cref="Curve.Hit"/> is a wide ellipse (easy to hit sideways, hard vertically) because the plot is much wider than tall.</summary>
        int HitScreen(Point screen, float px)
        {
            int best = -1; float bd = px * px;
            for (int i = 0; i < curve.Points.Count; i++)
            {
                var p = ToPlot(curve.Points[i]);
                float dx = p.X - screen.X, dy = p.Y - screen.Y, d = dx * dx + dy * dy;
                if (d <= bd) { bd = d; best = i; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (showHelp) { showHelp = false; Redraw(); return; }              // any click closes the help overlay
            if (_menu is { IsOpen: true }) { _menu.Close(); return; }          // one click on the control closes the menu (the click is swallowed)
            if (e.Button == MouseButtons.Middle) { panning = true; panAt = e.Location; panLo = viewLo; panHi = viewHi; Capture = true; return; }
            if (e.Button == MouseButtons.Right)
            {
                // Right click never edits: it SELECTS the node under the pointer (if any) and opens the menu, where
                // "Delete node" lives. Deleting on the press made the menu's node-type entries unreachable for an
                // existing point - the point was gone before its own menu could open - and one mis-click was an
                // unrecoverable edit. Curve.Remove still refuses the two endpoints.
                selected = HitScreen(e.Location, 9f); drag = -1;
                if (selected >= 0) Redraw();
                // Opened by the RIGHT button, so the next left click on the plot is a real gesture (move / add a point)
                // and not the click that merely lays the menu down.
                Menu().Show(this, e.Location, MouseButtons.Right);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            var f = FromPlot(e.Location);
            int i = HitScreen(e.Location, 8f);
            if (i < 0) i = curve.Add(f.X, f.Y);                                 // empty spot: a new Auto point
            else if (i > 0 && i < curve.Points.Count - 1 && e.Clicks == 2)      // double click a middle point: its next tangent type
            {
                var next = curve.ModeAt(i) switch { CurvePointMode.Auto => CurvePointMode.Flat, CurvePointMode.Flat => CurvePointMode.Linear, _ => CurvePointMode.Auto };
                curve.SetMode(i, next); selected = i; drag = -1; Redraw(); return;
            }
            drag = selected = i;
            if (drag >= 0) { Capture = true; curve.Move(drag, f.X, f.Y); Redraw(); }   // capture: the endpoints live ON the border, the drag must survive leaving the control
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (panning)
            {   // middle drag: pan the value axis (both edges follow the pointer)
                double units = (viewHi - viewLo) / Math.Max(1, Plot.Height - 1);
                double lo = Math.Clamp(panLo + (panAt.Y - e.Y) * units, -3, 3 - (panHi - panLo));
                viewLo = lo; viewHi = lo + (panHi - panLo);
                Redraw();
                return;
            }
            if (drag < 0 || (e.Button & MouseButtons.Left) == 0) return;
            var f = FromPlot(e.Location);
            curve.Move(drag, f.X, f.Y);
            Redraw();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            panning = false; drag = -1;
            Capture = false;
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            panning = false; drag = -1;                                        // capture lost externally (alt-tab, a modal): no phantom drag on return
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (showHelp)
            {   // the overlay owns the wheel while it is up: a plot too short for the whole help is read by scrolling it
                int room = Math.Max(0, HelpLines.Length * HelpStep - (Plot.Height - 12));
                helpScroll = Math.Clamp(helpScroll + (e.Delta > 0 ? -HelpStep : HelpStep), 0, room);
                if (e is HandledMouseEventArgs hs) hs.Handled = true;
                Redraw();
                return;
            }
            double span = Math.Clamp((viewHi - viewLo) * (e.Delta > 0 ? 1 / 1.25 : 1.25), 0.25, 4.0);
            viewHi = Math.Clamp(viewLo + span, viewLo + 0.25, 3);              // zoom anchored at the BOTTOM line (zero while un-panned)
            if (e is HandledMouseEventArgs h) h.Handled = true;
            Redraw();
        }
        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _menu?.Close();                                                    // clicking anywhere else on the form closes the menu
        }

        SpriteMenu? _menu;
        SpriteMenu Menu()
        {
            if (_menu != null) return _menu;
            _menu = new SpriteMenu { Title = "Curve", MinWidth = 240, TextScale = 1 };
            var point = _menu.AddSub("Point type");
            point.AddRadio("Auto (smooth)", () => selected >= 0 && curve.ModeAt(selected) == CurvePointMode.Auto, () => { if (selected >= 0) curve.SetMode(selected, CurvePointMode.Auto); Redraw(); }, () => selected >= 0);
            point.AddRadio("Flat (rest at the point)", () => selected >= 0 && curve.ModeAt(selected) == CurvePointMode.Flat, () => { if (selected >= 0) curve.SetMode(selected, CurvePointMode.Flat); Redraw(); }, () => selected >= 0);
            point.AddRadio("Linear (no easing here)", () => selected >= 0 && curve.ModeAt(selected) == CurvePointMode.Linear, () => { if (selected >= 0) curve.SetMode(selected, CurvePointMode.Linear); Redraw(); }, () => selected >= 0);
            // The deletion the right press used to do silently, now where its own menu can say so. The two endpoints
            // are not removable (Curve.Remove refuses them), so the entry is greyed while one of them is selected.
            _menu.Add("Delete node", () => { if (selected >= 0) { curve.Remove(selected); selected = drag = -1; Redraw(); } },
                "the point under the cursor (never the first or last one)",
                () => selected > 0 && selected < curve.Points.Count - 1);
            _menu.AddSeparator();
            _menu.Add("Copy as code", () => { System.Windows.Forms.Clipboard.SetText(curve.ToCode()); });
            _menu.Add("Paste points", () => { if (TryParsePoints(Clipboard.GetText(), out var pts)) { curve.Set(pts); selected = drag = -1; Redraw(); } });
            _menu.AddSeparator();
            _menu.AddCheck("Clamp values to 0..1", () => curve.Clamp01, v => { curve.Clamp01 = v; if (v) { curve.ClampTo01(); viewLo = 0; viewHi = 1; } Redraw(); });
            _menu.Add("Compress into 0..1 (keep the shape)", () => { curve.CompressTo01(); viewLo = 0; viewHi = 1; Redraw(); });
            _menu.Add("Zoom to 0..1", () => { viewLo = 0; viewHi = 1; Redraw(); });
            _menu.Add("Reset (diagonal)", () => { curve.Reset(); selected = drag = -1; viewLo = 0; viewHi = 1; Redraw(); });
            _menu.AddSeparator();
            _menu.Add("Help", () => { showHelp = true; helpScroll = 0; Redraw(); });
            return _menu;
        }

        /// <summary>Parses the number pairs of a pasted snippet (tolerates <c>new(0.5f, 0.2f)</c>, tuples, bare pairs).</summary>
        public static bool TryParsePoints(string? text, out PointF[] pts)
        {
            pts = Array.Empty<PointF>();
            if (string.IsNullOrWhiteSpace(text)) return false;
            var ms = System.Text.RegularExpressions.Regex.Matches(text, @"(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\s*f?\s*,\s*(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\s*f?");
            var list = new System.Collections.Generic.List<PointF>();
            foreach (System.Text.RegularExpressions.Match m in ms)
                if (float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
                    list.Add(new PointF(x, y));
            if (list.Count < 2) return false;
            pts = list.ToArray();
            return true;
        }

        protected override void PaintControl(Sprite s)
        {
            var r = Plot;
            s.FillRect(r.Left, r.Top, r.Width, r.Height, unchecked((int)0xFF14171C));
            s.DrawRect(r.Left, r.Top, r.Width, r.Height, Mix(BackColor, Fore, 0.45f));
            int guide = Mix(BackColor, Fore, 0.2f); int faint = Mix(BackColor, Fore, 0.12f);
            if (Guides)
            {
                s.DrawWideLine(r.Left + r.Width / 2f, r.Top, r.Left + r.Width / 2f, r.Bottom, guide, 1f, true);   // x = 0.5 (the value zoom does not move it)
                if (viewLo <= 0.5 && viewHi >= 0.5)                                                          // the value cross line follows the zoom / pan
                { float ym = ToPlot(new PointF(0, 0.5f)).Y; s.DrawWideLine(r.Left, ym, r.Right, ym, guide, 1f, true); }
                if (viewLo <= 1 && viewHi >= 1) { float y1 = ToPlot(new PointF(0, 1f)).Y; s.DrawWideLine(r.Left, y1, r.Right - 1, y1, faint, 1f, true); }               // the 1.0 line
                if (viewLo <= 0 && viewHi >= 0) { float y0 = ToPlot(new PointF(0, 0f)).Y; s.DrawWideLine(r.Left, y0, r.Right - 1, y0, faint, 1f, true); }               // the 0.0 line
                double da = Math.Max(0, viewLo), db = Math.Min(1, viewHi);                                     // the identity diagonal, only the visible part of it
                if (db > da)
                {
                    PointF p1 = ToPlot(new PointF((float)da, (float)da)), p2 = ToPlot(new PointF((float)db, (float)db));
                    s.DrawWideLine(p1.X, p1.Y, p2.X, p2.Y, Mix(BackColor, Fore, 0.08f), 1f, true);
                }
            }
            var ink = Accent;
            var pts = new PointF[49];
            for (int i = 0; i < pts.Length; i++)
            {
                float t = i / (pts.Length - 1f);
                pts[i] = ToPlot(new PointF(t, (float)curve.Evaluate(t)));
            }
            s.DrawPolyline(pts, ink, 1.6f, true, false, SR2D.LineOp.Set, true);
            for (int i = 0; i < curve.Points.Count; i++)
            {
                var p = ToPlot(curve.Points[i]);
                float pr = i == drag ? 4.5f : 3.5f;
                s.FillCircle(p.X, p.Y, pr, Thumb, SR2D.LineOp.Set, true);
                int rim = i == selected ? Accent : i == 0 || i == curve.Points.Count - 1 ? Mix(ThumbColor, BackColor, 0.35f) : Mix(ThumbColor, Accent, 0.4f);
                s.DrawCircle(p.X, p.Y, pr, rim, 1.4f, true);
                if (curve.ModeAt(i) == CurvePointMode.Flat)                            // a flat point shows a little horizontal rest mark
                    s.DrawWideLine(p.X - 5, p.Y, p.X + 5, p.Y, Accent, 1f, true);
            }
            // the 0 / 1 axis numbers (in the left margin, only while inside the zoomed view)
            int ls = 1;
            if (viewLo <= 1 && viewHi >= 1) T.Draw(s, 3, (int)ToPlot(new PointF(0, 1f)).Y, "1", Mix(Fore, BackColor, 0.35f), 0, ls, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            if (viewLo <= 0 && viewHi >= 0) T.Draw(s, 3, (int)ToPlot(new PointF(0, 0f)).Y, "0", Mix(Fore, BackColor, 0.35f), 0, ls, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            if (viewLo < 0) T.Draw(s, 3, (int)ToPlot(new PointF(0, (float)(viewLo + 0.01))).Y, viewLo.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), Mix(Fore, BackColor, 0.5f), 0, ls, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            if (viewHi > 1) T.Draw(s, 3, (int)ToPlot(new PointF(0, (float)(viewHi - 0.01))).Y, viewHi.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), Mix(Fore, BackColor, 0.5f), 0, ls, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.MiddleLeft);
            if (curve.Clamp01 && (viewHi > 1.001 || viewLo < -0.001))           // the zoom shows room the clamping will not let a point use
                T.Draw(s, r.Right - 4, r.Top + 2, "clamped at 0..1 - menu: untick Clamp", Mix(Fore, BackColor, 0.4f), 0, ls, 0, 0, SR2D.LineOp.Set, 210, TextAnchor.TopRight);
            if (CapH > 0) T.Draw(s, 2, ClientSize.Height - 1, FitText(Text, ClientSize.Width - 4, 1), Mix(Fore, BackColor, 0.2f), 0, 1, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.BottomLeft);
            if (showHelp) PaintHelp(s);
        }

        int CapH => string.IsNullOrEmpty(Text) ? 0 : LineHeight(1) + 3;

        int HelpStep => LineHeight(1) + 2;

        /// <summary>
        /// The overlay's lines. Eleven of them used to need 221 px (5x7 glyphs, one line per 13 px) while the plot the
        /// demos give the editor is 134 px tall, so the help ran over the frame and off the control; these nine fit a
        /// 320x150 editor, and a shorter box scrolls them with the wheel. What came off the list is in the menu, which
        /// spells out the point types and the paste entry.
        /// </summary>
        static readonly string[] HelpLines =
        {
            "Left drag    move / select",
            "Left click   add a point",
            "Dbl click    Auto/Flat/Linear",
            "Right click  select / menu",
            "Wheel        zoom value axis",
            "Middle drag  pan value axis",
            "Clamp on keeps 0..1: untick it",
            "in the menu for overshoot",
            "Click closes, wheel scrolls",
        };

        void PaintHelp(Sprite s)
        {
            var r = Plot;
            s.FillRect(r.Left + 2, r.Top + 2, r.Width - 4, r.Height - 4, unchecked((int)0xE0101418), SR2D.LineOp.AlphaOver);
            using var v = s.CreateView(r.Left + 2, r.Top + 2, r.Right - 2, r.Bottom - 2);   // clip: no line crosses the frame
            int y = r.Top + 6 - helpScroll;
            foreach (var line in HelpLines)
            {
                T.Draw(v, r.Left + 8, y, line, unchecked((int)0xFFD8DDE4), 0, 1, 0, 0, SR2D.LineOp.Set, 255, TextAnchor.TopLeft);
                y += HelpStep;
            }
        }
    }
}
