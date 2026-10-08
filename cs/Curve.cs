// SR2D - an editable 0..1 curve (the "Color Map" / Levels-curve style control data):
// sorted points, monotone cubic interpolation (Fritsch-Carlson) - passes exactly through the
// points and never overshoots them, so the curve is safe as a speed profile (TangentMode-style
// shaping), a brightness ramp (Levels) or any other 0..1 transfer function.
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Sr2d64CSport
{
    /// <summary>The tangent type of one curve point (like the 3ds Max node types): Auto = automatic smooth slope, Flat = the curve rests at the point (slope 0), Linear = no easing at the point (segments arrive / leave straight).</summary>
    public enum CurvePointMode
    {
        /// <summary>Automatic smooth (the Fritsch-Carlson slope from the neighbours). The default.</summary>
        Auto,
        /// <summary>Flat: the slope at the point is 0 - the curve eases into and out of it (a plateau at the key).</summary>
        Flat,
        /// <summary>Linear: the segments meeting at this point run straight through it - no easing here (a "corner" with constant speed).</summary>
        Linear,
    }

    public sealed class Curve
    {
        readonly List<CurvePointMode> modes = new();
        readonly List<PointF> pts = new();
        /// <summary>Raised after the points changed (the editor raises it per drag step).</summary>
        public event EventHandler? Changed;
        /// <summary>True = values are kept inside 0..1 (the default). False = points may live above 1 / below 0 (overshoot); Evaluate then returns them unclamped.</summary>
        public bool Clamp01 { get; set; } = true;

        /// <summary>Creates the curve: by default the diagonal (0,0) - (1,1).</summary>
        public Curve() { pts.Add(new PointF(0, 0)); pts.Add(new PointF(1, 1)); modes.Add(CurvePointMode.Auto); modes.Add(CurvePointMode.Auto); }

        /// <summary>The points, sorted by x (endpoints included). Edit through the methods - they keep the invariants.</summary>
        public IReadOnlyList<PointF> Points => pts;
        /// <summary>Max points kept (a dense curve is unwieldy to edit at control size).</summary>
        public const int MaxPoints = 12;

        /// <summary>Resets to the diagonal (all point modes back to Auto).</summary>
        public void Reset()
        {
            pts.Clear(); modes.Clear(); pts.Add(new PointF(0, 0)); pts.Add(new PointF(1, 1));
            modes.Add(CurvePointMode.Auto); modes.Add(CurvePointMode.Auto);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        /// <summary>Sets the curve from a point list (sorted, endpoints at x = 0 / 1 enforced; y clamped only when <see cref="Clamp01"/>). Every point becomes Auto - see the overload for the tangent modes.</summary>
        public void Set(IEnumerable<PointF> source) => Set(source, null);
        /// <summary>Sets the curve from points and their tangent modes (pass <see cref="ModeAt"/> per point to keep a curve's shape when copying; null or a short list leaves the missing points Auto).</summary>
        public void Set(IEnumerable<PointF> source, IReadOnlyList<CurvePointMode>? sourceModes)
        {
            var sorted = new List<PointF>(source);
            sorted.Sort((a, b) => a.X.CompareTo(b.X));
            pts.Clear(); modes.Clear();
            int kept = -1;
            for (int idx = 0; idx < sorted.Count; idx++)
            {
                if (pts.Count >= MaxPoints - 1) break;                         // room for the final point at x = 1: never exceed MaxPoints (a longer list used to grow past it, with duplicate x = 1 keys)
                var p = sorted[idx];
                float x = Math.Clamp(p.X, 0f, 1f), y = Clamp01 ? Math.Clamp(p.Y, 0f, 1f) : p.Y;
                if (pts.Count == 0) x = 0;
                else if (x - pts[^1].X < MinGap) continue;                     // nearer than MinGap to the previously kept point: drop it (no floating near-duplicates, same rule as Add)
                pts.Add(new PointF(x, y)); modes.Add(ModeOf(sourceModes, idx)); kept = idx;
            }
            if (kept < sorted.Count - 1 && sorted.Count > 0)                   // the last source point did not make it (dropped by MinGap or cut by MaxPoints): it becomes the x = 1 endpoint
            {
                float y = Clamp01 ? Math.Clamp(sorted[^1].Y, 0f, 1f) : sorted[^1].Y;
                pts.Add(new PointF(1, y)); modes.Add(ModeOf(sourceModes, sorted.Count - 1));
            }
            if (pts.Count == 0) { pts.Add(new PointF(0, 0)); modes.Add(CurvePointMode.Auto); }
            if (pts.Count == 1) { pts.Add(new PointF(1, pts[0].Y)); modes.Add(CurvePointMode.Auto); }   // one key cannot define a curve: a single point becomes the flat line through it
            pts[0] = new PointF(0, pts[0].Y);
            pts[^1] = new PointF(1, pts[^1].Y);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        static CurvePointMode ModeOf(IReadOnlyList<CurvePointMode>? sourceModes, int idx)
            => sourceModes != null && idx >= 0 && idx < sourceModes.Count ? sourceModes[idx] : CurvePointMode.Auto;
        /// <summary>The tangent modes, parallel to <see cref="Points"/> - hand these to <see cref="Set(IEnumerable{PointF}, IReadOnlyList{CurvePointMode})"/> to copy a curve with its shape.</summary>
        public IReadOnlyList<CurvePointMode> Modes => modes;
        /// <summary>The smallest gap between two points' x (a tighter one would render as one floating node and break the tangents).</summary>
        public const float MinGap = 0.02f;
        /// <summary>Inserts a point (sorted; the endpoints keep their x). A click closer than <see cref="MinGap"/> to an existing point moves THAT point instead (no near-duplicates). Returns the index.</summary>
        public int Add(float x, float y)
        {
            x = Math.Clamp(x, 0f, 1f); y = Clamp01 ? Math.Clamp(y, 0f, 1f) : y;
            for (int k = 0; k < pts.Count; k++)
                if (Math.Abs(pts[k].X - x) < MinGap) { pts[k] = new PointF(pts[k].X, y); Changed?.Invoke(this, EventArgs.Empty); return k; }
            int i = 0;
            while (i < pts.Count && pts[i].X < x) i++;
            if (pts.Count >= MaxPoints) return -1;
            if (i == 0) x = 0;
            pts.Insert(i, new PointF(x, y));
            modes.Insert(i, CurvePointMode.Auto);
            Changed?.Invoke(this, EventArgs.Empty);
            return i;
        }
        /// <summary>Moves point <paramref name="i"/> (x clamped between the neighbours, never nearer than <see cref="MinGap"/>; y clamped only when <see cref="Clamp01"/>; the endpoints keep x = 0 / 1).</summary>
        public void Move(int i, float x, float y)
        {
            if (i < 0 || i >= pts.Count) return;
            y = Clamp01 ? Math.Clamp(y, 0f, 1f) : y;
            float lo = i == 0 ? 0 : pts[i - 1].X, hi = i == pts.Count - 1 ? 1 : pts[i + 1].X;
            if (i == 0) x = 0;
            else if (i == pts.Count - 1) x = 1;
            else
            {
                float lo2 = lo + MinGap, hi2 = Math.Min(1f, hi) - MinGap;
                x = lo2 > hi2 ? (lo + Math.Min(1f, hi)) / 2 : Math.Clamp(x, lo2, hi2);   // neighbours too close: sit between them
            }
            pts[i] = new PointF(x, y);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        /// <summary>Clamps every stored point back into 0..1 (destructive).</summary>
        public void ClampTo01()
        {
            for (int i = 0; i < pts.Count; i++) pts[i] = new PointF(pts[i].X, Math.Clamp(pts[i].Y, 0f, 1f));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        /// <summary>Rescales the value range of every point into 0..1 (keeps the shape, loses the overshoot).</summary>
        public void CompressTo01()
        {
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (var p in pts) { lo = Math.Min(lo, p.Y); hi = Math.Max(hi, p.Y); }
            if (lo >= hi) { ClampTo01(); return; }
            for (int i = 0; i < pts.Count; i++) pts[i] = new PointF(pts[i].X, (pts[i].Y - lo) / (hi - lo));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        /// <summary>Removes point <paramref name="i"/> (the two endpoints cannot be removed).</summary>
        public void Remove(int i)
        {
            if (i <= 0 || i >= pts.Count - 1) return;
            pts.RemoveAt(i);
            if (i < modes.Count) modes.RemoveAt(i);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The tangent type of point <paramref name="i"/>.</summary>
        public CurvePointMode ModeAt(int i) => i >= 0 && i < modes.Count ? modes[i] : CurvePointMode.Auto;
        /// <summary>Sets the tangent type of point <paramref name="i"/> (the point's shape in the curve editor's Point menu).</summary>
        public void SetMode(int i, CurvePointMode mode)
        {
            if (i < 0 || i >= pts.Count || modes[i] == mode) return;
            modes[i] = mode;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        /// <summary>The curve as a paste-able C# snippet: a <c>curve</c> local, an invariant-culture Set call, then one SetMode per non-Auto point and the Clamp01 note when it may overshoot.</summary>
        public string ToCode()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("var curve = new Curve(); curve.Set(new System.Drawing.PointF[] { ");
            for (int i = 0; i < pts.Count; i++)
            {
                sb.Append("new(").Append(pts[i].X.ToString("0.####", inv)).Append("f, ").Append(pts[i].Y.ToString("0.####", inv)).Append("f)");
                if (i < pts.Count - 1) sb.Append(", ");
            }
            sb.Append(" });");
            for (int i = 0; i < modes.Count; i++)                                // the tangent modes travel with the snippet, so pasting keeps the shape
                if (modes[i] != CurvePointMode.Auto)
                    sb.Append(" curve.SetMode(").Append(i.ToString(inv)).Append(", CurvePointMode.").Append(modes[i].ToString()).Append(");");
            if (!Clamp01) sb.Append(" curve.Clamp01 = false;   // may overshoot 0..1");
            return sb.ToString();
        }
        /// <summary>Index of the point nearest to (<paramref name="x"/>, <paramref name="y"/>) within <paramref name="radius"/>, or -1.</summary>
        public int Hit(PointF p, float radius)
        {
            int best = -1; float bd = radius * radius;
            for (int i = 0; i < pts.Count; i++)
            {
                float dx = pts[i].X - p.X, dy = pts[i].Y - p.Y, d = dx * dx + dy * dy;
                if (d <= bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>The curve value at u (0..1): monotone cubic through the points; clamped to 0..1 only when <see cref="Clamp01"/> (a curve with overshoot goes past 1 and comes back).</summary>
        public double Evaluate(double u)
        {
            u = Math.Clamp(u, 0, 1);
            int i = 1;
            while (i < pts.Count - 1 && pts[i].X < u) i++;
            var a = pts[i - 1]; var b = pts[i];
            float dx = b.X - a.X;
            if (dx <= 1e-6f) return b.Y;
            double t = Math.Clamp((u - a.X) / dx, 0, 1);
            double seg = (b.Y - a.Y) / dx;                                     // the segment's own slope
            double m0 = SlopeAt(i - 1, seg), m1 = SlopeAt(i, seg);
            double h00 = 2 * t * t * t - 3 * t * t + 1, h10 = t * t * t - 2 * t * t + t, h01 = -2 * t * t * t + 3 * t * t, h11 = t * t * t - t * t;
            double y = h00 * a.Y + h10 * m0 * dx + h01 * b.Y + h11 * m1 * dx;
            return Clamp01 ? Math.Clamp(y, 0, 1) : y;
        }
        /// <summary>The slope the Hermite uses at point <paramref name="k"/> when the segment's own slope is <paramref name="segSlope"/> - the point's <see cref="CurvePointMode"/> decides (Auto = Fritsch-Carlson, Flat = 0, Linear = the segment itself).</summary>
        double SlopeAt(int k, double segSlope) => ModeAt(k) switch
        {
            CurvePointMode.Flat => 0,
            CurvePointMode.Linear => segSlope,
            _ => Tangents()[k],
        };
        /// <summary>Fritsch-Carlson monotone tangents (value per unit x) per point.</summary>
        double[] Tangents()
        {
            int n = pts.Count;
            var d = new double[Math.Max(1, n - 1)];
            for (int k = 0; k < n - 1; k++) d[k] = (pts[k + 1].Y - pts[k].Y) / Math.Max(1e-6f, pts[k + 1].X - pts[k].X);
            var m = new double[n];
            if (n == 2) { m[0] = d[0]; m[1] = d[0]; return m; }
            m[0] = d[0]; m[n - 1] = d[n - 2];
            for (int k = 1; k < n - 1; k++) m[k] = d[k - 1] * d[k] <= 0 ? 0 : (d[k - 1] + d[k]) / 2;
            for (int k = 0; k < n - 1; k++)
            {   // the Fritsch-Carlson limiter keeps the cubic inside the hull (no overshoot)
                double lim = 3 * Math.Min(Math.Abs(d[k]), k + 1 < n - 1 ? Math.Abs(d[Math.Min(k + 1, n - 2)]) : Math.Abs(d[k])) + 0.0001;
                if (Math.Abs(m[k]) > lim) m[k] = Math.Sign(m[k]) * lim;
                if (Math.Abs(m[k + 1]) > lim && k + 1 < n) m[k + 1] = Math.Sign(m[k + 1]) * lim;
            }
            return m;
        }
    }
}
