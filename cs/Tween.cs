using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>
    /// Small tween engine: <c>Tween.To(box, () =&gt; x, v =&gt; x = v, 42, 250, Tween.Smooth)</c> animates a value and
    /// invalidates the target control on every step, so "how do I raise Render on movement" is a one-liner. All live
    /// tweens are driven by one shared 60 Hz timer (created lazily; nothing runs when no tween exists). Progress is
    /// computed from the wall clock on every pump, so slow frames just skip ahead - the animation always takes the
    /// requested time. Tests pump deterministically by overriding <see cref="Clock"/> and calling <see cref="Pump"/>();
    /// the timer only exists to do that automatically in a running app.
    /// </summary>
    internal sealed class Tween
    {
        Tween(Control target, Func<double> get, Action<double> set, double to, int ms, Func<double, double> ease)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _set = set ?? throw new ArgumentNullException(nameof(set));
            _from = get(); Target = to; Ms = Math.Max(1, ms); Ease = ease ?? Smooth; _start = Clock();
            _get = get;
        }
        readonly Control _target; readonly Func<double> _get; readonly Action<double> _set; readonly double _start, _from;
        /// <summary>Target value.</summary>
        public double Target { get; }
        /// <summary>Duration in milliseconds.</summary>
        public int Ms { get; }
        /// <summary>Easing (a function of t = 0..1 returning the warped t).</summary>
        public Func<double, double> Ease { get; }
        /// <summary>True when the tween ran to the end (not via <see cref="Cancel"/>).</summary>
        public bool Done { get; private set; }
        /// <summary>Raised once when the tween completes (not on cancel). Runs on the pump - keep it quick.</summary>
        public event Action? Completed;
        bool _cancelled;

        /// <summary>The time source (ms). Tests replace this for deterministic stepping.</summary>
        public static Func<double> Clock = () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        /// <summary>Starts a tween from the current value (read through <paramref name="get"/>) to <paramref name="to"/>.
        /// Every step calls <paramref name="set"/> and invalidates <paramref name="target"/> (a SpriteBox is fully
        /// <see cref="SpriteBox.Redraw"/>n, other controls just Invalidate). Cancels the target's earlier tweens of the
        /// same getter, so starting a new move never fights the old one.</summary>
        public static Tween To(Control target, Func<double> get, Action<double> set, double to, int ms, Func<double, double>? easing = null)
        {
            CancelFor(target, get);
            var t = new Tween(target, get, set, to, ms, easing ?? Smooth);
            Live.Add(t);
            EnsureTimer();
            Pump();                                   // first step right away: no one-frame lag before the value moves
            return t;
        }

        /// <summary>Stops every tween (all targets), or only the ones of <paramref name="target"/> when given. The values stay where they are.</summary>
        public static void CancelAll(Control? target = null)
        {
            foreach (var t in Live.ToArray())
                if (target == null || ReferenceEquals(t._target, target)) t._cancelled = true;
            Pump();
        }
        static void CancelFor(Control target, Func<double> get)
        {
            foreach (var t in Live.ToArray())
                if (ReferenceEquals(t._target, target) && t._get == get) t._cancelled = true;
        }

        /// <summary>Advances every live tween to <see cref="Clock"/>. Called by the shared timer; tests call it directly.</summary>
        public static void Pump()
        {
            if (Live.Count == 0) return;
            double now = Clock();
            foreach (var t in Live.ToArray())
            {
                if (t._cancelled) { t.Done = true; Live.Remove(t); continue; }
                double u = Math.Clamp((now - t._start) / t.Ms, 0.0, 1.0);
                t._set(t._from + (t.Target - t._from) * t.Ease(u));
                if (t._target is SpriteBox b) b.Redraw(); else t._target.Invalidate();
                if (u >= 1) { t.Done = true; Live.Remove(t); t.Completed?.Invoke(); }
            }
        }
        /// <summary>Number of live tweens (tests).</summary>
        public static int LiveCount => Live.Count;

        static readonly List<Tween> Live = new();
        static System.Windows.Forms.Timer? _timer;   // qualified: ImplicitUsings on makes bare "Timer" ambiguous with System.Threading.Timer
        static void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new System.Windows.Forms.Timer { Interval = 16 };     // ~60 Hz; the wall clock keeps the pace exact
            _timer.Tick += (_, _) => Pump();
            _timer.Start();
        }

        // ------------------------------------------------------------------ easings
        /// <summary>Linear.</summary>
        public static double Linear(double t) => t;
        /// <summary>Smoothstep (the controls' default feel): slow in, slow out.</summary>
        public static double Smooth(double t) => t * t * (3 - 2 * t);
        /// <summary>Accelerating (quadratic).</summary>
        public static double EaseIn(double t) => t * t;
        /// <summary>Decelerating (quadratic).</summary>
        public static double EaseOut(double t) => 1 - (1 - t) * (1 - t);
        /// <summary>Overshoots slightly and settles back.</summary>
        public static double Back(double t) { double s = 1.70158; t -= 1; return t * t * ((s + 1) * t + s) + 1; }
    }
}
