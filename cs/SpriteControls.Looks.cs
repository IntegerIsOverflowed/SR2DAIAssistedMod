using System;
using System.Drawing;

namespace Sr2d64CSport
{
    /// <summary>
    /// Graphic look of <see cref="SpriteButton"/>, <see cref="SpriteSlider"/>, <see cref="SpriteProgress"/> and
    /// <see cref="SpriteWheel"/> (their <c>Look</c> property). <see cref="Classic"/> is the SR2D house look and stays
    /// the default, so nothing changes until a control asks for another one. The two flat looks share one palette
    /// (accent, track and rim colours of the control) and never change a control's size, text or behaviour.
    /// </summary>
    public enum ControlLook
    {
        /// <summary>The house look: shaded face or track, highlight and rim, plus the control's own style (ButtonShape, WheelStyle, ...).</summary>
        Classic,
        /// <summary>
        /// Flat and instrument-like: no gradient. A 1 px rim with an inner keyline, corner or dial ticks, a hairline track
        /// with a square thumb, a thin ring with dial ticks.
        /// </summary>
        Hairline,
        /// <summary>
        /// Flat and cut into cells: LED-style bars and rings, a dot-matrix button face with an LED strip, a faceted drum.
        /// Lit cells take the accent colour, unlit cells the track colour.
        /// </summary>
        Segmented,
    }

    /// <summary>
    /// Drawing helpers of the flat looks (<see cref="ControlLook.Hairline"/> and <see cref="ControlLook.Segmented"/>).
    /// They take plain pixel coordinates and whole-pixel rectangles, so a flat look stays crisp at every size.
    /// </summary>
    public abstract partial class SpriteControlBase
    {
        /// <summary>
        /// A flat bar from <paramref name="q0"/> to <paramref name="q1"/> along the axis, centred on <paramref name="m"/>
        /// across it and <paramref name="th"/> thick. Whole pixels only; a zero-length bar draws nothing.
        /// </summary>
        protected static void LookBar(Sprite s, float q0, float q1, float m, float th, bool hz, int col, SR2D.LineOp op = SR2D.LineOp.Set)
        {
            int a0 = (int)MathF.Round(MathF.Min(q0, q1)), a1 = (int)MathF.Round(MathF.Max(q0, q1));
            if (a1 <= a0) return;
            int t0 = (int)MathF.Round(m - th / 2), t1 = Math.Max(t0 + 1, (int)MathF.Round(m + th / 2));
            if (hz) s.FillRect(a0, t0, a1 - a0, t1 - t0, col, op);
            else s.FillRect(t0, a0, t1 - t0, a1 - a0, col, op);
        }

        /// <summary>
        /// <paramref name="n"/> cells between the pixels <paramref name="qa"/> (value 0) and <paramref name="qb"/> (value 1),
        /// separated by <paramref name="gap"/> pixels and centred on <paramref name="m"/> (<paramref name="th"/> thick).
        /// Cell i covers the value fractions i/n .. (i+1)/n; <paramref name="colour"/> gets the fraction of its centre.
        /// </summary>
        protected static void LookCells(Sprite s, float qa, float qb, float m, float th, bool hz, int n, float gap, Func<double, int> colour)
        {
            for (int i = 0; i < n; i++)
            {
                float p0 = qa + (qb - qa) * i / n, p1 = qa + (qb - qa) * (i + 1) / n;
                int a0 = (int)MathF.Round(MathF.Min(p0, p1) + gap / 2), a1 = (int)MathF.Round(MathF.Max(p0, p1) - gap / 2);
                if (a1 <= a0) continue;
                int t0 = (int)MathF.Round(m - th / 2), t1 = Math.Max(t0 + 1, (int)MathF.Round(m + th / 2));
                int c = colour((i + 0.5) / n);
                if (hz) s.FillRect(a0, t0, a1 - a0, t1 - t0, c);
                else s.FillRect(t0, a0, t1 - t0, a1 - a0, c);
            }
        }

        /// <summary>
        /// A ring arc: <paramref name="len"/> of a turn starting at <paramref name="from"/> (both as fractions of a turn,
        /// 0 = twelve o'clock, clockwise), radius <paramref name="r"/> to the centre of the stroke, <paramref name="w"/> wide.
        /// </summary>
        protected static void LookArc(Sprite s, float cx, float cy, float r, float w, double from, double len, int col)
        {
            if (len <= 0) return;
            if (len >= 0.999) { s.DrawCircle(cx, cy, r, col, w, true); return; }
            int n = Math.Clamp((int)(r * len * 2 * MathF.PI / 2f) + 2, 2, 720);
            var pts = new PointF[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double a = (270 + 360 * (from + len * i / n)) * Math.PI / 180;
                pts[i] = new PointF(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
            }
            s.DrawPolyline(pts.AsSpan(), col, w, true, false, SR2D.LineOp.Set, true);
        }

        /// <summary>
        /// Dial ticks: <paramref name="n"/> marks around a circle, starting at radius <paramref name="r0"/> and growing outwards;
        /// every <paramref name="major"/>th mark is <paramref name="longLen"/> long, the others <paramref name="shortLen"/>.
        /// </summary>
        protected static void LookDial(Sprite s, float cx, float cy, float r0, int n, int shortLen, int longLen, int major, int col)
        {
            for (int i = 0; i < n; i++)
            {
                double a = (270 + 360.0 * i / n) * Math.PI / 180;
                float len = i % major == 0 ? longLen : shortLen;
                float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                s.DrawWideLine(cx + c * r0, cy + sn * r0, cx + c * (r0 + len), cy + sn * (r0 + len), col, 1f, true);
            }
        }

        /// <summary>Corner ticks: a short L at each corner of the rectangle, drawn inside it, <paramref name="len"/> pixels per arm.</summary>
        protected static void LookCornerTicks(Sprite s, float x, float y, float w, float h, int len, int col)
        {
            int x0 = (int)MathF.Round(x), y0 = (int)MathF.Round(y);
            int x1 = (int)MathF.Round(x + w) - 1, y1 = (int)MathF.Round(y + h) - 1;
            s.FillRect(x0, y0, len, 1, col); s.FillRect(x0, y0, 1, len, col);
            s.FillRect(x1 - len + 1, y0, len, 1, col); s.FillRect(x1, y0, 1, len, col);
            s.FillRect(x0, y1, len, 1, col); s.FillRect(x0, y1 - len + 1, 1, len, col);
            s.FillRect(x1 - len + 1, y1, len, 1, col); s.FillRect(x1, y1 - len + 1, 1, len, col);
        }

        /// <summary>
        /// The hairline frame of a rectangle: a 1 px rim, and inside it a translucent keyline three pixels in.
        /// Corner radius <paramref name="rad"/> is kept on both (the keyline's is three pixels smaller).
        /// </summary>
        protected static void LookKeyline(Sprite s, float x, float y, float w, float h, float rad, int rim, int keyCol)
        {
            s.DrawRoundRect(x + 0.5f, y + 0.5f, w - 1, h - 1, rad, rim, 1f, true);
            if (w > 14 && h > 14)
                s.DrawRoundRect(x + 3.5f, y + 3.5f, w - 7, h - 7, MathF.Max(0, rad - 3), keyCol, 1f, true, SR2D.LineOp.AlphaBlend);
        }

    }
}
