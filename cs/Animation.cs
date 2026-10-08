// SR2D - keyframe tracks with in / out tangents (the 3ds Max tangent model applied to interpolation):
// a Track is a list of keyframes; every segment between two keys is shaped by a tangent mode - Linear,
// Step, Smooth (ease in-out), Fast (fast start), Slow (slow start), Spline (custom handle slopes on the
// keys) and Auto (Catmull-Rom slopes computed from the neighbouring keys). Evaluate(t) turns time into a
// value - a position, an angle, a sprite frame index - so any lerp gets the speed profile of the tangent.
using System;

namespace Sr2d64CSport
{
    /// <summary>The tangent of a keyframe / the shaping of one interpolation segment, like the 3ds Max key tangents.</summary>
    public enum TangentMode
    {
        /// <summary>Easy in and out (smoothstep): rests at the keys, fastest in the middle. The default.</summary>
        Smooth,
        /// <summary>Constant speed between the keys.</summary>
        Linear,
        /// <summary>Hold the left key's value for the whole segment, then jump (a frame change, a cut).</summary>
        Step,
        /// <summary>Fast start, ease into the next key (ease-out).</summary>
        Fast,
        /// <summary>Slow start, accelerate towards the next key (ease-in).</summary>
        Slow,
        /// <summary>Cubic Hermite through the segment with the custom handle slopes of the keys (<see cref="Keyframe.HandleOut"/> of the left key, <see cref="Keyframe.HandleIn"/> of the right key; 0 = flat at both ends, larger = faster through the key).</summary>
        Spline,
        /// <summary>Automatic smooth: Catmull-Rom slopes from the neighbouring keys, so a curve passes through the key without overshooting it (what 3ds Max computes when a tangent is set to Auto). With only two keys in sight it falls back to the one-sided slope (the segment itself, = Smooth).</summary>
        Auto,
    }

    /// <summary>One key of a <see cref="Track"/>: a value at a time with the tangents of the segments before (In) and after (Out) it.</summary>
    public sealed class Keyframe
    {
        /// <summary>The time of the key (any unit - seconds, frames, percent).</summary>
        public double Time { get; set; }
        /// <summary>The value of the key (any unit - px, degrees, a frame index).</summary>
        public double Value { get; set; }
        /// <summary>The tangent of the segment that ENDS at this key (governs when the previous key's Out is <see cref="TangentMode.Auto"/> - the default, so fresh keys are automatic on both sides).</summary>
        public TangentMode In { get; set; } = TangentMode.Auto;
        /// <summary>The tangent of the segment that STARTS at this key (the default <see cref="TangentMode.Auto"/> = Catmull-Rom slopes from the neighbours: the curve rests at extremes and never overshoots).</summary>
        public TangentMode Out { get; set; } = TangentMode.Auto;
        /// <summary>Spline handle slope of the incoming segment (value per unit of the segment; 0 = flat).</summary>
        public double HandleIn { get; set; }
        /// <summary>Spline handle slope of the outgoing segment (value per unit of the segment; 0 = flat).</summary>
        public double HandleOut { get; set; }
        public Keyframe() { }
        public Keyframe(double time, double value) { Time = time; Value = value; }
        public Keyframe(double time, double value, TangentMode outTangent) { Time = time; Value = value; Out = outTangent; }
        public override string ToString() => $"{Time:0.###} = {Value:0.###} ({Out})";
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

        /// <summary>Adds a key (a new Keyframe; inserted in time order; the default tangent is Auto).</summary>
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
            var a = keys[i - 1]; var b = keys[i];
            double u = (t - a.Time) / (b.Time - a.Time);
            var mode = Resolve(a, b);
            if (mode == TangentMode.Auto)                                     // Catmull-Rom slopes from the neighbours, scaled to the segment
                return a.Value + (b.Value - a.Value) * Hermite01(u, slopes[i - 1] * (b.Time - a.Time), slopes[i] * (b.Time - a.Time));
            return a.Value + (b.Value - a.Value) * EaseCore(mode, u, a.HandleOut, b.HandleIn);
        }

        static TangentMode Resolve(Keyframe a, Keyframe b) => a.Out != TangentMode.Auto ? a.Out : b.In != TangentMode.Auto ? b.In : TangentMode.Auto;

        /// <summary>
        /// The shaped progress 0..1 of one segment at <paramref name="u"/> - the tangent of <paramref name="a"/>.Out
        /// (an Auto left tangent defers to <paramref name="b"/>.In; Auto on both = automatic slopes, which for a
        /// standalone key pair is the one-sided segment slope). Exposed so a single pair of values can be shaped
        /// without building a Track.
        /// </summary>
        public static double EaseSegment(Keyframe a, Keyframe b, double u)
        {
            u = Math.Clamp(u, 0, 1);
            var mode = Resolve(a, b);
            if (mode == TangentMode.Auto)
            {
                double d = b.Value - a.Value;                                 // one-sided auto slope, normalised to the segment
                return Hermite01(u, d, d);
            }
            return EaseCore(mode, u, a.HandleOut, b.HandleIn);
        }

        /// <summary>The single-segment helper for the demos: shapes 0..1 with one tangent (Spline / Auto = flat handles = Smooth).</summary>
        public static double EaseAt(TangentMode mode, double u)
        {
            u = Math.Clamp(u, 0, 1);
            return EaseCore(mode, u, 0, 0);
        }

        static double EaseCore(TangentMode mode, double u, double d0, double d1) => mode switch
        {
            TangentMode.Linear => u,
            TangentMode.Step => u >= 1 ? 1 : 0,                // holds the left value, jumps at the segment end (u = 1 delivers the key)
            TangentMode.Fast => 1 - (1 - u) * (1 - u),
            TangentMode.Slow => u * u,
            TangentMode.Spline => Hermite01(u, d0, d1),
            TangentMode.Auto => Hermite01(u, 0, 0),                           // standalone = flat handles = Smooth
            _ => u * u * (3 - 2 * u),                                         // Smooth
        };

        /// <summary>Cubic Hermite between 0 and 1 (p(0) = 0, p(1) = 1) with the given end slopes in value per unit u.</summary>
        static double Hermite01(double u, double d0, double d1)
        {
            double u2 = u * u, u3 = u2 * u;
            return -2 * u3 + 3 * u2 + (u3 - 2 * u2 + u) * d0 + (u3 - u2) * d1;
        }
    }
}
