// SR2D - the progress graphs of the tangents, drawn on a Sprite (the graphs of the Move / animation demos):
// TangentGraph.Draw shows one shape (progress 0..1 across, 0..1 up) with a marker, TangentGraph.DrawTrack
// shows a whole Track (time across, value up) with a grid, a line at every key time, the keys and a playhead.
// Pure drawing - no state.
using System;
using System.Drawing;

namespace Sr2d64CSport
{
    public static class TangentGraph
    {
        const int Back = unchecked((int)0xC0101418), Frame = 0x60FFFFFF, Grid = unchecked((int)0xFF2C3238), Line = unchecked((int)0xFF40C0FF),
                  Mark = unchecked((int)0xFFFFC040), Playhead = unchecked((int)0xB0FFFFFF), Label = unchecked((int)0xFF9AA4AE);

        /// <summary>
        /// Draws the progress graph of one shape in a w x h box at (x, y): <paramref name="shape"/> maps 0..1 to
        /// the progress (clamped to 0..1.5 on the graph), <paramref name="u"/> places the marker (0..1).
        /// </summary>
        public static void Draw(Sprite canvas, Func<double, double> shape, float u, int x, int y, int w = 78, int h = 58)
        {
            canvas.FillRect(x, y, w, h, Back, SR2D.LineOp.AlphaOver);
            canvas.DrawRect(x, y, w, h, Frame);
            var pts = new PointF[25];
            for (int i = 0; i < pts.Length; i++)
            {
                float t = i / (pts.Length - 1f);
                float p = Math.Clamp((float)shape(t), 0f, 1.5f);
                pts[i] = new PointF(x + 4 + t * (w - 8), y + h - 5 - p * (h - 10) / 1.5f);
            }
            canvas.DrawPolyline(pts, Line, 1.5f, true, false, SR2D.LineOp.AlphaOver, true);
            float cu = Math.Clamp(u, 0f, 1f), cp = Math.Clamp((float)shape(cu), 0f, 1.5f);
            canvas.FillCircle(x + 4 + cu * (w - 8), y + h - 5 - cp * (h - 10) / 1.5f, 2.5f, Mark, SR2D.LineOp.AlphaOver);
        }

        /// <summary>
        /// Draws a whole <see cref="Track"/> in <paramref name="area"/>: the curve over the keys' time span (the value
        /// range is fitted to the curve, overshoots included), a grid with a line at every key time and at five value
        /// levels, the keys as squares, and a playhead at <paramref name="time"/> with a dot on the curve. The value
        /// levels are labelled in the 34 px left of <paramref name="area"/>, the key times 14 px below it.
        /// </summary>
        public static void DrawTrack(Sprite canvas, Track track, Rectangle area, double time)
        {
            if (track.Count == 0 || area.Width < 16 || area.Height < 16) return;
            const int n = 120;
            double t0 = track.Keys[0].Time, t1 = track.Keys[^1].Time;
            if (t1 <= t0) t1 = t0 + 1;

            var vals = new double[n];
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                vals[i] = track.Evaluate(t0 + (t1 - t0) * i / (n - 1));
                lo = Math.Min(lo, vals[i]);
                hi = Math.Max(hi, vals[i]);
            }
            if (hi - lo < 1e-9) { lo -= 0.5; hi += 0.5; }
            double pad = (hi - lo) * 0.08;
            lo -= pad; hi += pad;

            float X(double t) => area.X + 6 + (float)((t - t0) / (t1 - t0)) * (area.Width - 12);
            float Y(double v) => area.Bottom - 6 - (float)((v - lo) / (hi - lo)) * (area.Height - 12);

            canvas.FillRect(area.X, area.Y, area.Width, area.Height, Back, SR2D.LineOp.AlphaOver);
            double step = NiceStep((hi - lo) / 4);                             // round value levels (1, 2 or 5 x 10^n)
            for (double v = Math.Ceiling(lo / step) * step; v <= hi; v += step)
                canvas.FillRect(area.X + 1, (int)Y(v), area.Width - 2, 1, Grid, SR2D.LineOp.AlphaOver);
            foreach (var k in track.Keys)                                      // a line at every key time
                canvas.FillRect((int)X(k.Time), area.Y + 1, 1, area.Height - 2, Grid, SR2D.LineOp.AlphaOver);
            canvas.DrawRect(area.X, area.Y, area.Width, area.Height, Frame);

            var pts = new PointF[n];
            for (int i = 0; i < n; i++) pts[i] = new PointF(X(t0 + (t1 - t0) * i / (n - 1)), Y(vals[i]));
            canvas.DrawPolyline(pts, Line, 2f, true, false, SR2D.LineOp.AlphaOver, true);
            foreach (var k in track.Keys)
                canvas.FillRect((int)X(k.Time) - 3, (int)Y(k.Value) - 3, 7, 7, Mark, SR2D.LineOp.AlphaOver);

            // labels: the five value levels in the gutter on the left, the key times under the area
            for (double v = Math.Ceiling(lo / step) * step; v <= hi; v += step)
            {
                string txt = Fmt(v, step);
                canvas.DrawText(Math.Max(0, area.X - 5 - Sprite.MeasureText(txt).Width), (int)Y(v) - 3, txt, Label, 0, 1);
            }
            foreach (var k in track.Keys)
            {
                string txt = k.Time.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
                canvas.DrawText((int)X(k.Time) - Sprite.MeasureText(txt).Width / 2, area.Bottom + 3, txt, Label, 0, 1);
            }

            double tc = Math.Clamp(time, t0, t1);
            float px = X(tc), py = Y(track.Evaluate(tc));
            canvas.FillRect((int)px, area.Y, 1, area.Height, Playhead, SR2D.LineOp.AlphaOver);
            canvas.FillCircle(px, py, 3.5f, Mark, SR2D.LineOp.AlphaOver);
        }

        /// <summary>The round step (1, 2 or 5 times a power of ten) nearest to <paramref name="raw"/>, at least raw.</summary>
        static double NiceStep(double raw)
        {
            double e = Math.Pow(10, Math.Floor(Math.Log10(raw))), f = raw / e;
            return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * e;
        }

        /// <summary>A value label with as many decimals as the step needs (none for steps of 1 or more).</summary>
        static string Fmt(double v, double step)
        {
            var ci = System.Globalization.CultureInfo.CurrentCulture;
            return step >= 1 ? v.ToString("0", ci) : step >= 0.1 ? v.ToString("0.0", ci) : v.ToString("0.00", ci);
        }
    }
}
