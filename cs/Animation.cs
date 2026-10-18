// SR2D - keyframe tracks with 3ds Max style key tangents: a Track is a list of keyframes, and every segment
// between two keys is a cubic Bezier. Its two handles come from the tangent of each key SIDE - Out of the left
// key (how the curve leaves it) and In of the right key (how it arrives) - so a key can be Smooth on one side
// and Linear on the other, exactly like the In / Out tangents of a key in 3ds Max. Evaluate(t) turns time into
// a value - a position, an angle, a sprite frame index - so any lerp gets the speed profile of the tangents.
// The progress graphs (TangentGraph) are in Animation.Graph.cs.
using System;
using System.Drawing;

namespace Sr2d64CSport
{
    /// <summary>
    /// The tangent of ONE side of a keyframe: <see cref="Keyframe.Out"/> shapes the segment that leaves the key,
    /// <see cref="Keyframe.In"/> the segment that arrives at it. Each side becomes a Bezier handle:
    /// <list type="bullet">
    /// <item><see cref="Smooth"/> / <see cref="Flat"/>: a level handle (zero slope at the key, the ease in-out).</item>
    /// <item><see cref="Linear"/>: a handle on the straight line to the other key (constant speed).</item>
    /// <item><see cref="Auto"/>: a handle on the Catmull-Rom slope of the neighbouring keys (no overshoot at the keys).</item>
    /// <item><see cref="Fast"/>: a steep handle - the speed is high just beside the key.</item>
    /// <item><see cref="Slow"/>: a long level handle - the speed stays low beside the key.</item>
    /// <item><see cref="Spline"/>: a handle with the slope in <see cref="Keyframe.HandleOut"/> / <see cref="Keyframe.HandleIn"/>.</item>
    /// <item><see cref="Bezier"/>: the handle you set in <see cref="Keyframe.BezierOut"/> / <see cref="Keyframe.BezierIn"/> (3ds Max Custom).</item>
    /// <item><see cref="Step"/>: holds the left value until the next key (on either side it holds the whole segment).</item>
    /// </list>
    /// Values 0..6 keep their meaning from the first release (the Move demo's Op box lists them); 7 and 8 were added after.
    /// </summary>
    public enum TangentMode
    {
        /// <summary>Easy in and out (smoothstep): rests at the keys, fastest in the middle. The default for a standalone pair.</summary>
        Smooth,
        /// <summary>Constant speed between the keys.</summary>
        Linear,
        /// <summary>Hold the left key's value for the whole segment, then jump at the next key (a frame change, a cut).</summary>
        Step,
        /// <summary>Steep handle: fast start / fast arrival next to the key (ease-out when the other side is Smooth).</summary>
        Fast,
        /// <summary>Long level handle: slow start / slow arrival next to the key (ease-in when the other side is Fast).</summary>
        Slow,
        /// <summary>Hermite handles with the custom slopes of the keys (<see cref="Keyframe.HandleOut"/>, <see cref="Keyframe.HandleIn"/>; 0 = level, larger = faster through the key).</summary>
        Spline,
        /// <summary>Automatic: Catmull-Rom slopes from the neighbouring keys, so a curve passes through each key without overshooting it (what 3ds Max computes for Auto). A standalone pair falls back to its own segment slope.</summary>
        Auto,
        /// <summary>Custom Bezier handles you set on the key (<see cref="Keyframe.BezierOut"/> / <see cref="Keyframe.BezierIn"/>; a zero handle = the default level handle a third of the segment long). Handles may overshoot.</summary>
        Bezier,
        /// <summary>Level handle, like <see cref="Smooth"/>: the key is a turning point. Kept for the 3ds Max vocabulary.</summary>
        Flat,
        /// <summary>A custom curve (<see cref="Keyframe.Shape"/>, a <see cref="Curve"/> with any number of nodes, read as a spline through them) shapes the segment that leaves the key. Its ends are normalised to 0 and 1.</summary>
        Curve,
    }

    /// <summary>One key of a <see cref="Track"/>: a value at a time, with the tangent of the segment that leaves it (<see cref="Out"/>) and of the segment that arrives at it (<see cref="In"/>).</summary>
    public sealed class Keyframe
    {
        /// <summary>The time of the key (any unit - seconds, frames, percent).</summary>
        public double Time { get; set; }
        /// <summary>The value of the key (any unit - px, degrees, a frame index).</summary>
        public double Value { get; set; }
        /// <summary>The tangent of the segment that ARRIVES at this key (the default <see cref="TangentMode.Auto"/>).</summary>
        public TangentMode In { get; set; } = TangentMode.Auto;
        /// <summary>The tangent of the segment that LEAVES this key (the default <see cref="TangentMode.Auto"/>).</summary>
        public TangentMode Out { get; set; } = TangentMode.Auto;
        /// <summary>Spline slope of the incoming segment, in progress per unit of the segment (0 = level).</summary>
        public double HandleIn { get; set; }
        /// <summary>Spline slope of the outgoing segment, in progress per unit of the segment (0 = level).</summary>
        public double HandleOut { get; set; }
        /// <summary>The custom curve of the segment leaving this key when <see cref="Out"/> is <see cref="TangentMode.Curve"/> (shared instances are fine: it is read on every Evaluate).</summary>
        public Curve? Shape { get; set; }
        /// <summary><see cref="TangentMode.Bezier"/> handle leaving this key: time and value offsets from the key, in the Track's units (0,0 = the default level handle).</summary>
        public (double Time, double Value) BezierOut { get; set; }
        /// <summary><see cref="TangentMode.Bezier"/> handle arriving at this key: offsets from the key, normally negative time (0,0 = the default level handle).</summary>
        public (double Time, double Value) BezierIn { get; set; }
        public Keyframe() { }
        public Keyframe(double time, double value) { Time = time; Value = value; }
        public Keyframe(double time, double value, TangentMode outTangent) { Time = time; Value = value; Out = outTangent; }
        public override string ToString() => $"{Time:0.###} = {Value:0.###} (in {In}, out {Out})";
    }

    /// <summary>
    /// A sorted list of <see cref="Keyframe"/>s: <see cref="Evaluate"/> maps any time to the interpolated value.
    /// Feed it seconds and read a position, or frames and read a sprite index - the tangents shape the speed
    /// between the keys either way.
    /// </summary>
    public sealed class Track
    {
        readonly System.Collections.Generic.List<Keyframe> keys = new();
        double[] slopes = Array.Empty<double>();                              // the Catmull-Rom slope of each key (value per time)
        bool dirty = true;

        /// <summary>The number of keys.</summary>
        public int Count => keys.Count;
        /// <summary>The last key time (0 for an empty track).</summary>
        public double Duration => keys.Count == 0 ? 0 : keys[^1].Time;
        /// <summary>The keys, sorted by time (edit <see cref="Keyframe.Time"/> in place, then call <see cref="Sort"/>).</summary>
        public System.Collections.Generic.IReadOnlyList<Keyframe> Keys => keys;

        /// <summary>Adds a key (a new Keyframe; inserted in time order; its Out is <paramref name="outTangent"/>, its In the default Auto).</summary>
        public Keyframe Add(double time, double value, TangentMode outTangent = TangentMode.Auto)
        {
            var k = new Keyframe(time, value, outTangent);
            Add(k);
            return k;
        }
        /// <summary>Adds a ready key (inserted in time order).</summary>
        public void Add(Keyframe k)
        {
            int i = keys.Count;
            while (i > 0 && keys[i - 1].Time > k.Time) i--;
            keys.Insert(i, k);
            dirty = true;
        }
        /// <summary>Re-sorts the keys (after editing <see cref="Keyframe.Time"/> in place) and refreshes the auto slopes.</summary>
        public void Sort()
        {
            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            dirty = true;
        }
        /// <summary>Removes every key.</summary>
        public void Clear() { keys.Clear(); dirty = true; }

        /// <summary>The Catmull-Rom slope of every key from its neighbours (one-sided at the ends), in value per time.</summary>
        void EnsureSlopes()
        {
            if (!dirty && slopes.Length == keys.Count) return;
            if (slopes.Length != keys.Count) slopes = new double[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                var prev = keys[Math.Max(0, i - 1)];
                var next = keys[Math.Min(keys.Count - 1, i + 1)];
                slopes[i] = next.Time > prev.Time ? (next.Value - prev.Value) / (next.Time - prev.Time) : 0;
            }
            dirty = false;
        }

        /// <summary>The value at <paramref name="t"/>: before the first / after the last key it holds that key's value.</summary>
        public double Evaluate(double t)
        {
            if (keys.Count == 0) return 0;
            if (t <= keys[0].Time || keys.Count == 1) return keys[0].Value;
            if (t >= keys[^1].Time) return keys[^1].Value;
            EnsureSlopes();
            int i = 1;
            while (keys[i].Time < t) i++;
            return Segment(keys[i - 1], keys[i], t, slopes[i - 1], slopes[i]);
        }

        /// <summary>
        /// The shaped progress 0..1 of one segment at <paramref name="u"/>, from <paramref name="a"/>.Out and
        /// <paramref name="b"/>.In. A standalone pair has no neighbours, so Auto uses the segment's own slope
        /// (Auto on both sides = a straight line). Returns u itself when the two values are equal.
        /// </summary>
        public static double EaseSegment(Keyframe a, Keyframe b, double u)
        {
            u = Math.Clamp(u, 0, 1);
            double dt = b.Time - a.Time, dv = b.Value - a.Value;
            if (IsHold(a, b)) return u >= 1 ? 1 : 0;                         // the jump comes at the key
            if (dt <= 0 || Math.Abs(dv) < 1e-12) return u;
            double slope = dv / dt;
            double v = Segment(a, b, a.Time + u * dt, slope, slope);
            return (v - a.Value) / dv;
        }

        /// <summary>
        /// The single-segment helper for the demos: shapes 0..1 with one tangent on both sides of a 0..1 pair.
        /// Fast and Slow use the two-sided shape (Fast = fast start into a Smooth arrival, Slow = Smooth start
        /// into a fast arrival - the 1-(1-u)^2 and u^2 curves); Auto, Spline, Bezier and Flat are Smooth here.
        /// </summary>
        public static double EaseAt(TangentMode mode, double u)
        {
            var (o, i) = mode switch
            {
                TangentMode.Fast => (TangentMode.Fast, TangentMode.Smooth),
                TangentMode.Slow => (TangentMode.Smooth, TangentMode.Fast),
                TangentMode.Smooth or TangentMode.Linear or TangentMode.Step => (mode, mode),
                _ => (TangentMode.Smooth, TangentMode.Smooth),
            };
            var a = new Keyframe(0, 0, o);
            var b = new Keyframe(1, 1) { In = i };
            return EaseSegment(a, b, u);
        }

        static bool IsHold(Keyframe a, Keyframe b) => a.Out == TangentMode.Step || b.In == TangentMode.Step;

        /// <summary>The value at time <paramref name="t"/> inside the segment a..b: a cubic Bezier solved for its time.</summary>
        /// <param name="slopeA">The Catmull-Rom slope of <paramref name="a"/> (value per time), used by Auto.</param>
        /// <param name="slopeB">The Catmull-Rom slope of <paramref name="b"/> (value per time), used by Auto.</param>
        static double Segment(Keyframe a, Keyframe b, double t, double slopeA, double slopeB)
        {
            double dt = b.Time - a.Time, dv = b.Value - a.Value;
            if (dt <= 0) return b.Value;
            if (IsHold(a, b)) return t >= b.Time ? b.Value : a.Value;
            if (a.Out == TangentMode.Curve && a.Shape != null)                 // the custom curve of the segment, normalised
                return a.Value + dv * CurveProgress(a.Shape, (t - a.Time) / dt);

            var o = OutHandle(a, dt, dv, slopeA);                              // offset from a
            var i = InHandle(b, dt, dv, slopeB);                               // offset from b (negative time)
            double x1 = Math.Clamp(o.Time, 0, dt), x2 = Math.Clamp(dt + i.Time, 0, dt);
            if (x1 > x2) x1 = x2 = (x1 + x2) / 2;                              // crossing handles: keep the time monotonic
            double y1 = o.Value, y2 = dv + i.Value;

            double target = t - a.Time, lo = 0, hi = 1;                        // bisection on the curve parameter s
            for (int k = 0; k < 50; k++)
            {
                double m = (lo + hi) / 2;
                if (Cubic(m, 0, x1, x2, dt) < target) lo = m; else hi = m;
            }
            return a.Value + Cubic((lo + hi) / 2, 0, y1, y2, dv);
        }

        /// <summary>The handle leaving key <paramref name="a"/> (time and value offsets; the segment is dt long and rises dv).</summary>
        static (double Time, double Value) OutHandle(Keyframe a, double dt, double dv, double slope) => a.Out switch
        {
            TangentMode.Linear => (dt / 3, dv / 3),
            TangentMode.Auto => (dt / 3, slope * dt / 3),
            TangentMode.Fast => (dt / 3, dv * 2 / 3),
            TangentMode.Slow => (dt * 2 / 3, 0),
            TangentMode.Spline => (dt / 3, dv * a.HandleOut / 3),
            TangentMode.Bezier => Custom(a.BezierOut, dt / 3),
            _ => (dt / 3, 0),                                                  // Smooth, Flat
        };

        /// <summary>The handle arriving at key <paramref name="b"/> (time and value offsets from b; negative time).</summary>
        static (double Time, double Value) InHandle(Keyframe b, double dt, double dv, double slope) => b.In switch
        {
            TangentMode.Linear => (-dt / 3, -dv / 3),
            TangentMode.Auto => (-dt / 3, -slope * dt / 3),
            TangentMode.Fast => (-dt / 3, -dv * 2 / 3),
            TangentMode.Slow => (-dt * 2 / 3, 0),
            TangentMode.Spline => (-dt / 3, -dv * b.HandleIn / 3),
            TangentMode.Bezier => Custom(b.BezierIn, -dt / 3),
            _ => (-dt / 3, 0),                                                 // Smooth, Flat
        };

        static (double Time, double Value) Custom((double Time, double Value) h, double defaultTime) =>
            h.Time == 0 && h.Value == 0 ? (defaultTime, 0) : h;

        /// <summary>The custom curve's progress 0..1 at u, with its own ends counted as 0 and 1 (a flat curve is linear).</summary>
        static double CurveProgress(Curve c, double u)
        {
            double lo = c.Evaluate(0), hi = c.Evaluate(1);
            if (hi - lo < 1e-6) return Math.Clamp(u, 0, 1);
            return (c.Evaluate(Math.Clamp(u, 0, 1)) - lo) / (hi - lo);
        }

        static double Cubic(double s, double p0, double p1, double p2, double p3)
        {
            double m = 1 - s;
            return m * m * m * p0 + 3 * m * m * s * p1 + 3 * m * s * s * p2 + s * s * s * p3;
        }
    }

    /// <summary>
    /// Moves a point from A to B along the straight line between them. The tangents set the speed along that line:
    /// the start tangent leaves A, the end tangent arrives at B (the same modes as a <see cref="Track"/> key).
    /// </summary>
    public static class Interpolate
    {
        /// <summary>
        /// The point at <paramref name="weight"/> (0 = A, 1 = B). <paramref name="startTangent"/> is the tangent that
        /// leaves A, <paramref name="endTangent"/> the one that arrives at B. Smooth = ease in and out, Linear =
        /// constant speed, Fast = quick start, Slow = quick arrival, Step = jump to B at the end.
        /// </summary>
        public static PointF SplineLerp(PointF a, PointF b, TangentMode startTangent, TangentMode endTangent, double weight)
        {
            var from = new Keyframe(0, 0, startTangent);
            var to = new Keyframe(1, 1) { In = endTangent };
            return At(a, b, Track.EaseSegment(from, to, weight));
        }

        /// <summary>
        /// The point at <paramref name="weight"/> (0 = A, 1 = B) with the speed set by a custom <paramref name="curve"/>
        /// (the curve editor's curve: x = time, y = progress; its ends are normalised so A and B are hit exactly).
        /// </summary>
        public static PointF SplineLerp(PointF a, PointF b, Curve curve, double weight)
        {
            var from = new Keyframe(0, 0, TangentMode.Curve) { Shape = curve };
            var to = new Keyframe(1, 1);
            return At(a, b, Track.EaseSegment(from, to, weight));
        }

        static PointF At(PointF a, PointF b, double progress) =>
            new PointF((float)(a.X + (b.X - a.X) * progress), (float)(a.Y + (b.Y - a.Y) * progress));
    }
}
