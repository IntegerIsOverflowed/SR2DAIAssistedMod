using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>
    /// Scroll bar in the SR2D control look: a flat track with a rounded thumb whose length shows <see cref="PageSize"/> /
    /// range, optional arrow buttons at the ends. <see cref="SpriteRangeControl.Value"/> runs Minimum .. Maximum - PageSize
    /// (the WinForms convention, so a view of PageSize pixels inside Maximum pixels maps 1:1). Drag the thumb, click the
    /// track to page, click the arrows (or use the wheel / keys) to step by <see cref="SpriteRangeControl.Step"/>.
    /// <see cref="SpriteBox"/> uses two of them when <see cref="SpriteBox.ScrollBars"/> is on; on its own it is an
    /// ordinary control. It does not take the focus when clicked (the view it scrolls keeps it).
    /// </summary>
    internal sealed class SpriteScrollBar : SpriteRangeControl
    {
        protected override AccessibleRole DefaultAccessibleRole => AccessibleRole.ScrollBar;
        Orientation _orient = Orientation.Vertical;
        double _page = 10;
        bool _arrows = true;
        int _grab; int _hitPart;             // 0 none, 1 thumb, 2 track before, 3 track after, 4 arrow up/left, 5 arrow down/right

        public SpriteScrollBar() { Size = new Size(16, 120); ShowValue = false; TabStop = false; Cursor = Cursors.Default; }

        [Category("Behavior"), DefaultValue(Orientation.Vertical), Description("Vertical or horizontal bar.")]
        public Orientation Orientation { get => _orient; set { _orient = value; Redraw(); } }

        [Category("Behavior"), DefaultValue(10.0), Description("Size of the visible part in value units: the thumb length is PageSize / (Maximum - Minimum) of the track, the largest Value is Maximum - PageSize, a track click moves by PageSize.")]
        public double PageSize { get => _page; set { _page = Math.Max(0, value); SetValue(Value, false); Redraw();   /* re-clamp to the new range */ } }

        [Category("Appearance"), DefaultValue(true), Description("Arrow buttons at both ends (one Step per click).")]
        public bool Arrows { get => _arrows; set { _arrows = value; Redraw(); } }

        /// <summary>Not shown on a scroll bar.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new bool ShowValue { get => base.ShowValue; set => base.ShowValue = value; }

        protected override double UpperLimit => Math.Max(Minimum, Maximum - _page);
        protected override bool FocusOnPress => false;
        /// <summary>True when the whole range fits in one page (nothing to scroll).</summary>
        [Browsable(false)] public bool IsIdle => Maximum - Minimum <= _page + 1e-9;

        bool Hz => _orient == Orientation.Horizontal;
        int Thick => Hz ? ClientSize.Height : ClientSize.Width;
        int Len => Hz ? ClientSize.Width : ClientSize.Height;
        int ArrowLen => _arrows ? Math.Min(Thick, Len / 3) : 0;

        // track from a to b along the axis, thumb from t0 to t1
        (float a, float b, float t0, float t1) Geometry()
        {
            float a = ArrowLen + 1, b = Len - ArrowLen - 1, track = Math.Max(1, b - a);
            double span = Math.Max(1e-9, Maximum - Minimum);
            float tl = (float)Math.Clamp(track * Math.Min(1, _page / span), Math.Min(track, Math.Max(8, Thick)), track);
            double f = UpperLimit > Minimum ? (Shown - Minimum) / (UpperLimit - Minimum) : 0;
            float t0 = a + (float)((track - tl) * Math.Clamp(f, 0, 1));
            return (a, b, t0, t0 + tl);
        }
        int HitPart(Point p)
        {
            var (a, b, t0, t1) = Geometry();
            float q = Hz ? p.X : p.Y;
            if (_arrows && q < a) return 4;
            if (_arrows && q > b) return 5;
            if (q >= t0 && q <= t1) return 1;
            return q < t0 ? 2 : 3;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!IsDragging && IsDragButton(e.Button))
            {
                _hitPart = HitPart(e.Location);
                var (_, _, t0, _) = Geometry();
                _grab = (int)((Hz ? e.X : e.Y) - t0);
                switch (_hitPart)
                {
                    case 4: SetValue(Value - Step, true); return;      // arrows / track: no drag
                    case 5: SetValue(Value + Step, true); return;
                    case 2: SetValue(Value - _page, true); return;
                    case 3: SetValue(Value + _page, true); return;
                }
            }
            base.OnMouseDown(e);
        }
        protected override double ValueFromPointer(Point p, Point start, double startValue, bool fine)
        {
            if (_hitPart != 1) return Value;
            var (a, b, t0, t1) = Geometry();
            float track = b - a, tl = t1 - t0, q = (Hz ? p.X : p.Y) - _grab;
            double f = track - tl > 0 ? (q - a) / (track - tl) : 0;
            return Minimum + Math.Clamp(f, 0, 1) * (UpperLimit - Minimum);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!IsDragging) { int h = HitPart(e.Location); if (h != _hitPart) { _hitPart = h; Redraw(); } }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!IsDragging) _hitPart = 0; }

        protected override void PaintControl(Sprite s)
        {
            var (a, b, t0, t1) = Geometry();
            int w = ClientSize.Width, h = ClientSize.Height, thick = Thick;
            float m = thick / 2f, r = Math.Max(1.5f, thick * 0.22f);
            int trackCol = Mix(BackColor, TrackColor, 0.35f), thumbCol = IsDragging ? Accent : Mix(TrackColor, ThumbColor, _hitPart == 1 && IsHot ? 0.7f : 0.45f);
            if (!Enabled || IsIdle) thumbCol = Mix(BackColor, TrackColor, 0.6f);
            // track
            if (Hz) FillRound(s, a, m - r, b - a, 2 * r, r, trackCol); else FillRound(s, m - r, a, 2 * r, b - a, r, trackCol);
            // thumb (inset a little)
            float tr = Math.Max(2f, thick * 0.32f);
            if (Hz) FillRound(s, t0, m - tr, t1 - t0, 2 * tr, tr, thumbCol); else FillRound(s, m - tr, t0, 2 * tr, t1 - t0, tr, thumbCol);
            // arrows: small triangles
            if (_arrows && ArrowLen > 3)
            {
                int al = ArrowLen; float sz = Math.Max(2f, thick * 0.22f);
                int c1 = Mix(BackColor, ForeColor, _hitPart == 4 && IsHot ? 0.9f : 0.5f), c2 = Mix(BackColor, ForeColor, _hitPart == 5 && IsHot ? 0.9f : 0.5f);
                if (!Enabled || IsIdle) c1 = c2 = Mix(BackColor, ForeColor, 0.25f);
                Span<PointF> tri = stackalloc PointF[3];
                if (Hz)
                {
                    float cx = al / 2f; tri[0] = new PointF(cx - sz * 0.6f, m); tri[1] = new PointF(cx + sz * 0.6f, m - sz); tri[2] = new PointF(cx + sz * 0.6f, m + sz); s.FillPolygon(tri, c1, SR2D.LineOp.Set, true);
                    cx = w - al / 2f; tri[0] = new PointF(cx + sz * 0.6f, m); tri[1] = new PointF(cx - sz * 0.6f, m - sz); tri[2] = new PointF(cx - sz * 0.6f, m + sz); s.FillPolygon(tri, c2, SR2D.LineOp.Set, true);
                }
                else
                {
                    float cy = al / 2f; tri[0] = new PointF(m, cy - sz * 0.6f); tri[1] = new PointF(m - sz, cy + sz * 0.6f); tri[2] = new PointF(m + sz, cy + sz * 0.6f); s.FillPolygon(tri, c1, SR2D.LineOp.Set, true);
                    cy = h - al / 2f; tri[0] = new PointF(m, cy + sz * 0.6f); tri[1] = new PointF(m - sz, cy - sz * 0.6f); tri[2] = new PointF(m + sz, cy - sz * 0.6f); s.FillPolygon(tri, c2, SR2D.LineOp.Set, true);
                }
            }
        }
    }
}
