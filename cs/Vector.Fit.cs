// Vector.Fit.cs - cubic Bezier fit of a dense closed loop, no dependencies.
// Same algorithm as VectorOffset.FitLoop (Vector.Offset.cs), on Pt2 so that the outline pipeline stays self-contained:
// corners where the loop turns sharply (over a window of about 10 samples), then a tolerance-based Schneider
// least-squares cubic fit per run between corners, splitting at the worst point. One segment = one node. A run that
// lies within the tolerance of its chord is emitted as a straight (Cub false) so a polygon outline stays a polygon.
using System;
using System.Collections.Generic;

namespace Sr2d64CSport
{
    internal static class OutlineFit
    {
        /// <summary>One piece of a loop: a cubic from P0 to P1 with controls C1, C2, or a straight run (Cub false).</summary>
        internal struct Seg { public Pt2 P0, C1, C2, P1; public bool Cub; }

        static double Dot(Pt2 a, Pt2 b) => a.X * b.X + a.Y * b.Y;
        static double Cross(Pt2 a, Pt2 b) => a.X * b.Y - a.Y * b.X;
        static Pt2 Unit(Pt2 v) { double l = v.Length; return l < 1e-12 ? v : v * (1.0 / l); }

        /// <summary>Cubic fit of one closed loop. pixel = corner window scale (mm), tol = fit tolerance (mm).
        /// When reduceNodes is true, applies CorelDRAW-style node reduction (curve pair merging within reduceTol).</summary>
        public static List<Seg> FitLoop(IList<Pt2> pts, double pixel, double tol, bool reduceNodes = false, double reduceTol = 0.025)
        {
            var segs = new List<Seg>();
            int n = pts.Count;
            if (n < 3) return segs;
            double perim = 0;
            for (int i = 0; i < n; i++) perim += (pts[(i + 1) % n] - pts[i]).Length;
            int k = Math.Max(2, (int)Math.Round(10 * pixel / Math.Max(perim / n, 1e-12)));
            k = Math.Min(k, Math.Max(1, n / 3));
            double thr = 60 * Math.PI / 180;
            var turn = new double[n];
            var stepTurn = new double[n];
            var signedStep = new double[n];
            for (int i = 0; i < n; i++)
            {
                Pt2 u1 = pts[i] - pts[((i - k) % n + n) % n], u2 = pts[(i + k) % n] - pts[i];
                turn[i] = Math.Atan2(Math.Abs(Cross(u1, u2)), Dot(u1, u2));
                Pt2 a0 = pts[i] - pts[(i - 1 + n) % n], a1 = pts[(i + 1) % n] - pts[i];
                if (a0.Length > 1e-9 && a1.Length > 1e-9)
                {
                    signedStep[i] = Math.Atan2(Cross(a0, a1), Dot(a0, a1));
                    stepTurn[i] = Math.Abs(signedStep[i]);
                }
            }
            var corners = new List<int>();
            for (int i = 0; i < n; i++)
            {
                Pt2 a0 = pts[i] - pts[(i - 1 + n) % n], a1 = pts[(i + 1) % n] - pts[i];
                double l0 = a0.Length, l1 = a1.Length;
                if (l0 <= 1e-6 || l1 <= 1e-6) continue;
                double prevStep = stepTurn[(i - 1 + n) % n], nextStep = stepTurn[(i + 1) % n];
                double lMax = Math.Max(l0, l1), lMin = Math.Min(l0, l1);
                // 1. Any single-step bend > 16.5 deg is always a corner (smooth curves and >=24-gon discs turn <= 15 deg per step)
                bool sharpStep = stepTurn[i] > 16.5 * Math.PI / 180 ||
                    (stepTurn[i] > 10 * Math.PI / 180 && stepTurn[i] > 1.45 * prevStep && stepTurn[i] > 1.45 * nextStep);
                // Check if vertex i is a smooth continuation of an arc on either side (even if the other side was clipped by a sharp corner)
                bool arcPrev = (signedStep[i] * signedStep[(i - 1 + n) % n] > 0)
                    && (Math.Abs(stepTurn[i] - prevStep) <= 0.30 * Math.Max(stepTurn[i], prevStep) + 0.005);
                bool arcNext = (signedStep[i] * signedStep[(i + 1) % n] > 0)
                    && (Math.Abs(stepTurn[i] - nextStep) <= 0.30 * Math.Max(stepTurn[i], nextStep) + 0.005);
                bool clippedBySharp = (l0 < l1 && prevStep > 16.5 * Math.PI / 180 && arcNext)
                                   || (l1 < l0 && nextStep > 16.5 * Math.PI / 180 && arcPrev);
                bool uniformArc = !clippedBySharp
                    ? ((lMax <= 1.25 * lMin) && (arcPrev || arcNext) && (signedStep[i] * signedStep[(i - 1 + n) % n] >= 0) && (signedStep[i] * signedStep[(i + 1) % n] >= 0))
                    : true;
                // 2. Chord-length transition (a straight segment meeting a curve, cap, round join, or bevel)
                bool straightSide = (prevStep < 0.25 * Math.PI / 180 || nextStep < 0.25 * Math.PI / 180 || lMax > 0.55);
                bool transition = !clippedBySharp && straightSide && lMax > 1.60 * lMin
                    && ((lMax > 0.55 && stepTurn[i] > 0.35 * Math.PI / 180) || (lMax > 0.10 && stepTurn[i] > 4.5 * Math.PI / 180 && (prevStep < 0.25 * Math.PI / 180 || nextStep < 0.25 * Math.PI / 180)));
                // 3. Long straight segment (> 0.52 mm) meeting another segment at any angle > 0.35 deg, unless part of a uniform arc
                bool longLineCorner = lMax > 0.52 && stepTurn[i] > 0.35 * Math.PI / 180 && !uniformArc;
                // 4. Polygon / bevel corner whose sagitta exceeds smooth curve flatness and is not a uniform arc
                bool polyCorner = lMax * Math.Sin(stepTurn[i]) > 0.04 && stepTurn[i] > 2.0 * Math.PI / 180 && !uniformArc;
                if (sharpStep || transition || longLineCorner || polyCorner) { corners.Add(i); continue; }
                if (turn[i] <= thr || stepTurn[i] < 16.5 * Math.PI / 180 || stepTurn[i] < 0.45 * turn[i]) continue;
                bool peak = true;
                for (int o = -k; o <= k && peak; o++)
                    if (turn[(i + o + n) % n] > turn[i]) peak = false;
                if (peak && (corners.Count == 0 || i - corners[corners.Count - 1] > k)) corners.Add(i);
            }
            corners.Sort();
            List<int> starts;
            if (corners.Count == 0)
            {
                var full = new List<Pt2>(n + 1);
                for (int t = 0; t <= n; t++) full.Add(pts[t % n]);
                if (ArcSegs && WholeCircle(full, tol, out Arc fullArc))
                {
                    EmitArc(fullArc, full[0], full[n], segs);
                    return segs;
                }
                starts = n >= 16 ? new List<int> { 0, n / 4, n / 2, (3 * n) / 4 } : new List<int> { 0 };
            }
            else starts = corners;
            for (int r = 0; r < starts.Count; r++)
            {
                int s = starts[r], e = starts[(r + 1) % starts.Count];
                int cnt = (e - s + n) % n;
                if (cnt == 0) cnt = n;
                var run = new List<Pt2>(cnt + 1);
                for (int t = 0; t <= cnt; t++) run.Add(pts[(s + t) % n]);
                Pt2 t1 = StartTangent(pts, s, k, pixel), t2 = EndTangent(pts, e, k, pixel);
                FitRun(run, t1, t2, tol, segs, 0);
            }
            segs = ReduceLoopNodes(segs, Math.Max(tol * 2.5, 0.008));
            if (reduceNodes)
                segs = ReduceLoopNodes(segs, Math.Max(0.045, reduceTol));
            return segs;
        }

        /// <summary>CorelDRAW-style node reduction and micro-segment cleanup on a closed loop of fitted segments.</summary>
        public static List<Seg> ReduceLoopNodes(List<Seg> inSegs, double mergeTol)
        {
            var segs = new List<Seg>(inSegs);
            if (segs.Count <= 2) return segs;

            // 1. Collapse collinear micro-segments (< mergeTol) only when tangents match smoothly
            double cosSmooth = Math.Cos(10.0 * Math.PI / 180.0);
            bool changed = true;
            while (changed && segs.Count > 3)
            {
                changed = false;
                for (int i = 0; i < segs.Count; i++)
                {
                    double len = (segs[i].P1 - segs[i].P0).Length;
                    if (len < mergeTol)
                    {
                        int prev = (i + segs.Count - 1) % segs.Count;
                        int next = (i + 1) % segs.Count;
                        if (Dot(SegEndTan(segs[prev]), SegStartTan(segs[i])) < cosSmooth ||
                            Dot(SegEndTan(segs[i]), SegStartTan(segs[next])) < cosSmooth) continue;
                        Pt2 mid = (segs[i].P0 + segs[i].P1) * 0.5;
                        var sp = segs[prev]; sp.P1 = mid; segs[prev] = sp;
                        var sn = segs[next]; sn.P0 = mid; segs[next] = sn;
                        segs.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }

            static Pt2 SegStartTan(Seg s)
            {
                if (s.Cub)
                {
                    Pt2 d = s.C1 - s.P0;
                    if (d.Length < 1e-9) d = s.C2 - s.P0;
                    if (d.Length < 1e-9) d = s.P1 - s.P0;
                    return Unit(d);
                }
                return Unit(s.P1 - s.P0);
            }
            static Pt2 SegEndTan(Seg s)
            {
                if (s.Cub)
                {
                    Pt2 d = s.P1 - s.C2;
                    if (d.Length < 1e-9) d = s.P1 - s.C1;
                    if (d.Length < 1e-9) d = s.P1 - s.P0;
                    return Unit(d);
                }
                return Unit(s.P1 - s.P0);
            }
            static Pt2 EvalSeg(Seg b, double t)
            {
                if (!b.Cub) return b.P0 + (b.P1 - b.P0) * t;
                double s = 1.0 - t;
                return b.P0 * (s * s * s) + b.C1 * (3 * t * s * s) + b.C2 * (3 * t * t * s) + b.P1 * (t * t * t);
            }

            // 2. Greedily merge adjacent segment pairs around the loop when a single line or cubic fits within mergeTol
            double cosJoinLim = Math.Cos(22.0 * Math.PI / 180.0);
            for (int pass = 0; pass < 64 && segs.Count > 2; pass++)
            {
                double bestScore = double.MaxValue;
                int bestIdx = -1;
                Seg bestMerged = default;

                for (int i = 0; i < segs.Count; i++)
                {
                    int j = (i + 1) % segs.Count;
                    Seg sA = segs[i], sB = segs[j];
                    Pt2 tEndA = SegEndTan(sA), tStartB = SegStartTan(sB);
                    double cosJoin = Dot(tEndA, tStartB);
                    if (cosJoin < cosJoinLim) continue;
                    double lA = (sA.P1 - sA.P0).Length, lB = (sB.P1 - sB.P0).Length;
                    // Never merge a straight line (>= 0.55 mm) with a curve unless they meet collinearly (< 2 deg) and the curve bends gently (< 15 deg)
                    double lLine = !sA.Cub ? lA : (!sB.Cub ? lB : 0.0);
                    if (sA.Cub != sB.Cub && lLine >= 0.55)
                    {
                        if (cosJoin < Math.Cos(2.0 * Math.PI / 180.0)) continue;
                        Seg cubSeg = sA.Cub ? sA : sB;
                        if (Dot(SegStartTan(cubSeg), SegEndTan(cubSeg)) < Math.Cos(15.0 * Math.PI / 180.0)) continue;
                    }

                    var pts = new List<Pt2>(33);
                    for (int k = 0; k < 16; k++) pts.Add(EvalSeg(sA, k / 16.0));
                    for (int k = 0; k <= 16; k++) pts.Add(EvalSeg(sB, k / 16.0));

                    Pt2 ch = pts[pts.Count - 1] - pts[0];
                    double cl = ch.Length;
                    if (cl < 1e-6) continue;

                    if (!sA.Cub && !sB.Cub)
                    {
                        double worstL = 0;
                        for (int k = 1; k + 1 < pts.Count; k++)
                            worstL = Math.Max(worstL, Math.Abs(Cross(ch, pts[k] - pts[0])) / cl);
                        if (worstL <= mergeTol && worstL < bestScore)
                        {
                            bestScore = worstL;
                            bestIdx = i;
                            bestMerged = new Seg { P0 = pts[0], P1 = pts[pts.Count - 1], Cub = false };
                        }
                        continue;
                    }

                    Pt2 t1 = SegStartTan(sA), t2 = SegEndTan(sB) * -1.0;
                    if (Dot(t1, t2 * -1.0) < -0.985) continue;

                    double[] u = ChordParam(pts);
                    Pt2[] bz = GenBez(pts, u, t1, t2);
                    double err = MaxErr(pts, bz, u, out _);
                    Pt2[] bestBz = bz;
                    double bestE = err;
                    for (int it = 0; it < 16 && bestE > mergeTol * 0.25; it++)
                    {
                        u = Reparam(bz, pts, u);
                        bz = (it >= 1) ? GenBezSDM(pts, u, t1, t2, bz) : GenBez(pts, u, t1, t2);
                        err = MaxErr(pts, bz, u, out _);
                        if (err < bestE) { bestE = err; bestBz = bz; }
                    }
                    if (bestE <= mergeTol)
                    {
                        double cdev = CurveDev(bestBz, pts);
                        double totalErr = Math.Max(bestE, cdev);
                        if (totalErr <= mergeTol && totalErr < bestScore)
                        {
                            bestScore = totalErr;
                            bestIdx = i;
                            bestMerged = new Seg { P0 = bestBz[0], C1 = bestBz[1], C2 = bestBz[2], P1 = bestBz[3], Cub = true };
                        }
                    }
                }

                if (bestIdx < 0) break;
                int removeIdx = (bestIdx + 1) % segs.Count;
                segs[bestIdx] = bestMerged;
                segs.RemoveAt(removeIdx);
            }
            return segs;
        }

        static Pt2 StartTangent(IList<Pt2> pts, int s, int k, double pixel)
        {
            int n = pts.Count;
            Pt2 d0 = pts[(s + 1) % n] - pts[s];
            double l0 = d0.Length;
            Pt2 d1 = pts[(s + 2) % n] - pts[(s + 1) % n];
            double l1 = d1.Length;
            if (l0 > 1e-6 && l1 > 1e-6)
            {
                Pt2 u0 = d0 * (1.0 / l0), u1 = d1 * (1.0 / l1);
                if (Dot(u0, u1) > 0.9659)
                {
                    if (Circle3(pts[s], pts[(s + 1) % n], pts[(s + 2) % n], out Pt2 c, out _))
                    {
                        Pt2 rad = pts[s] - c;
                        Pt2 t = new Pt2(-rad.Y, rad.X);
                        if (Dot(t, u0) < 0) t = t * -1.0;
                        return Unit(t);
                    }
                    double w = l0 / (l0 + l1);
                    return Unit(u0 + (u0 - u1) * w);
                }
                return u0;
            }
            return l0 > 1e-9 ? Unit(d0) : TangentAt(pts, s, k, true);
        }
        static Pt2 EndTangent(IList<Pt2> pts, int e, int k, double pixel)
        {
            int n = pts.Count;
            Pt2 d0 = pts[(e - 1 + n) % n] - pts[e];
            double l0 = d0.Length;
            Pt2 d1 = pts[(e - 2 + n) % n] - pts[(e - 1 + n) % n];
            double l1 = d1.Length;
            if (l0 > 1e-6 && l1 > 1e-6)
            {
                Pt2 u0 = d0 * (1.0 / l0), u1 = d1 * (1.0 / l1);
                if (Dot(u0, u1) > 0.9659)
                {
                    if (Circle3(pts[e], pts[(e - 1 + n) % n], pts[(e - 2 + n) % n], out Pt2 c, out _))
                    {
                        Pt2 rad = pts[e] - c;
                        Pt2 t = new Pt2(-rad.Y, rad.X);
                        if (Dot(t, u0) < 0) t = t * -1.0;
                        return Unit(t);
                    }
                    double w = l0 / (l0 + l1);
                    return Unit(u0 + (u0 - u1) * w);
                }
                return u0;
            }
            return l0 > 1e-9 ? Unit(d0) : TangentAt(pts, e, k, false);
        }

        static Pt2 TangentAt(IList<Pt2> pts, int i, int k, bool forward)
        {
            int n = pts.Count;
            Pt2 q = forward ? pts[(i + k) % n] : pts[(i - k + n) % n];
            return Unit(q - pts[i]);
        }

        static void FitRun(List<Pt2> pts, Pt2 t1, Pt2 t2, double err, List<Seg> outSegs, int depth)
        {
            int n = pts.Count;
            if (n < 2) return;
            if (n == 2)
            {
                outSegs.Add(new Seg { P0 = pts[0], P1 = pts[1] });
                return;
            }
            Pt2 ch = pts[n - 1] - pts[0];
            double cl = ch.Length;
            if (cl > 0)
            {
                double worst = 0;
                for (int i = 1; i < n - 1; i++) worst = Math.Max(worst, Math.Abs(Cross(ch, pts[i] - pts[0])) / cl);
                if (worst <= err)
                {
                    outSegs.Add(new Seg { P0 = pts[0], P1 = pts[n - 1] });
                    return;
                }
            }
            if (ArcSegs && depth == 0)
            {
                var arcs = FindArcs(pts, err);
                if (arcs.Count > 0) { FitArcs(pts, arcs, t1, t2, err, outSegs, depth); return; }
            }
            double[] u = ChordParam(pts);
            Pt2[] bz = GenBez(pts, u, t1, t2);
            double maxErr = MaxErr(pts, bz, u, out int split);
            double reparamGate = Math.Max(err * 64.0, 0.35 * cl);
            if (maxErr > err && maxErr <= reparamGate)
            {
                Pt2[] bestBz = bz;
                double[] bestU = u;
                double bestErr = maxErr;
                int bestSplit = split;
                for (int it = 0; it < 10 && bestErr > err; it++)
                {
                    u = Reparam(bz, pts, u);
                    bz = (it >= 1) ? GenBezSDM(pts, u, t1, t2, bz) : GenBez(pts, u, t1, t2);
                    maxErr = MaxErr(pts, bz, u, out split);
                    if (maxErr < bestErr)
                    {
                        bestErr = maxErr;
                        bestBz = bz;
                        bestU = u;
                        bestSplit = split;
                    }
                }
                bz = bestBz;
                u = bestU;
                maxErr = bestErr;
                split = bestSplit;
            }
            if (maxErr <= err && n <= 200)
            {
                double cdev = CurveDev(bz, pts);
                if (cdev > err) { maxErr = cdev; split = n / 2; }
            }
            if (maxErr <= err)
            {
                outSegs.Add(new Seg { P0 = bz[0], C1 = bz[1], C2 = bz[2], P1 = bz[3], Cub = true });
                return;
            }
            if (n <= 3 || depth > 40)
            {
                for (int i = 0; i + 1 < n; i++)
                    outSegs.Add(new Seg { P0 = pts[i], P1 = pts[i + 1] });
                return;
            }
            if (n >= 6)
            {
                int minIdx = Math.Max(2, n / 8);
                int maxIdx = Math.Min(n - 3, n - 1 - n / 8);
                if (split < minIdx || split > maxIdx) split = Math.Clamp(split, minIdx, maxIdx);
            }
            else split = Math.Clamp(split, 1, n - 2);
            Pt2 dPrev = pts[split] - pts[split - 1], dNext = pts[split + 1] - pts[split];
            double lPrev = dPrev.Length, lNext = dNext.Length;
            Pt2 tcFwd = (lPrev > 1e-9 && lNext > 1e-9)
                ? Unit(dPrev * (lNext / (lPrev + lNext)) + dNext * (lPrev / (lPrev + lNext)))
                : Unit(pts[split + 1] - pts[split - 1]);
            FitRun(pts.GetRange(0, split + 1), t1, tcFwd * -1.0, err, outSegs, depth + 1);
            FitRun(pts.GetRange(split, n - split), tcFwd, t2, err, outSegs, depth + 1);
        }

        /// <summary>False: the pre-arc fit, which subdivides every circular span down to its chords. Kept reachable so the
        /// rig can A/B the two; production recognises arcs.</summary>
        public static bool ArcSegs = true;

        /// <summary>Set by the rig to a sink for one line per rejected circular span, so "this shape did not become arcs"
        /// is answered by a measurement instead of a guess. Null in production, and nothing is formatted when it is.</summary>
        public static Action<string>? ArcLog;

        /// <summary>A circular span of a run: points I..J lie on the circle (C, R) and turn by Sweep (signed, radians).</summary>
        struct Arc { public int I, J; public Pt2 C; public double R, Sweep; }

        // Below this a span is not worth an arc. Emitting one costs a cubic per quarter turn and forces the pieces on either
        // side of it to be fitted separately, so it only pays where the least-squares fit would itself have needed several
        // cubics - which is where the span's own chords sit further from a single cubic than the tolerance, i.e. a cap, a
        // round join of any real angle, a circular contour. A gentle bend of a smooth curve is one cubic either way, and
        // recognising it as an arc there costs nodes and buys nothing: the dense shapes' counts went up, not down, at 6 deg.
        const double MinArcSweep = 45 * Math.PI / 180;

        // How deep a chord may cut into the circle, as a share of the radius, before the span stops being a sampled arc and
        // starts being a polygon that happens to be concyclic. Scale free, so a cap sampled in 16 chords per half disc
        // (0.5 %) and a contour circle flattened to a couple of dozen chords (0.9 %) both pass, while a rectangle's sides
        // (29 %) and a regular decagon's (2.5 %) do not.
        const double ArcChordShare = 0.01;

        /// <summary>The circle a candidate span starting at pts[i] is grown against: the circumcircle of three adjacent
        /// samples. That is enough wherever the samples are sparse relative to the curvature - a cap and a round join are
        /// sampled at 11.25 deg a chord, so three of them are well conditioned. It is not enough for a dense contour ring,
        /// whose adjacent samples span a small fraction of a degree and whose circumcircle is then set by the samples' own
        /// rounding rather than by the ring; but a closed ring is <see cref="WholeCircle"/>'s case, and a dense *open* run
        /// of a real circle is rare enough that widening the window for it costs more than it buys: the widen also fires
        /// where a straight edge runs into a round join, and there it fits a circle to a mixture of the two and the star's
        /// joins came out eight and ten nodes heavier. So the seed stays three points.</summary>
        static bool SeedCircle(List<Pt2> pts, int i, out Pt2 c, out double r)
        {
            c = pts[i]; r = 0;
            Pt2 d0 = pts[i + 1] - pts[i], d1 = pts[i + 2] - pts[i + 1];
            double l0 = d0.Length, l1 = d1.Length;
            if (l0 < 1e-6 || l1 < 1e-6 || l0 > 1.6 * l1 || l1 > 1.6 * l0) return false;
            return Circle3(pts[i], pts[i + 1], pts[i + 2], out c, out r);
        }

        /// <summary>The least-squares circle through pts[a..b], by the algebraic fit taken about the window's centroid.
        /// Centring makes the quadratic term orthogonal to the linear ones, so what is left is a 2 x 2 solve rather than
        /// a 3 x 3 one over coordinates of arbitrary magnitude. False when the window is degenerate.</summary>
        static bool CircleFit(List<Pt2> pts, int a, int b, out Pt2 c, out double r)
        {
            c = pts[a]; r = 0;
            int m = b - a + 1;
            if (m < 4) return false;
            double mx = 0, my = 0;
            for (int i = a; i <= b; i++) { mx += pts[i].X; my += pts[i].Y; }
            mx /= m; my /= m;
            double suu = 0, suv = 0, svv = 0, sq = 0, squ = 0, sqv = 0;
            for (int i = a; i <= b; i++)
            {
                double u = pts[i].X - mx, v = pts[i].Y - my, q = u * u + v * v;
                suu += u * u; suv += u * v; svv += v * v;
                sq += q; squ += q * u; sqv += q * v;
            }
            double det = suu * svv - suv * suv;
            if (!(Math.Abs(det) > 1e-12 * suu * svv)) return false;           // the window is a straight line
            double B = (sqv * suv - squ * svv) / det, C = (squ * suv - sqv * suu) / det;
            double r2 = (B * B + C * C) / 4 + sq / m;                        // - D, and D = -sq / m
            if (!(r2 > 0)) return false;
            c = new Pt2(mx - B / 2, my - C / 2);
            r = Math.Sqrt(r2);
            return r > 1e-9 && r < 1e7;                                      // Circle3's own bounds on a real radius
        }

        // The one-sided ripple a circle carries when it is written as four cubics, as a fraction of its radius: the
        // canonical handle length 4 (sqrt 2 - 1) / 3 puts the curve outside the circle by up to this much, zero at the
        // knots and the quarter points, maximum a fifth of the way along each arc. Any closed run that lies within it of
        // a circle therefore cannot be told from one by anything downstream - the engine's own circles are that far off
        // a circle already - so the whole-run test below measures against it as well as against the caller's tolerance.
        // That matters because the caller's is an absolute length and this is a property of the shape: the bake fits to
        // 0.001 mm, and a 10 mm ring of a four-cubic circle sits 0.0015 mm off true, so on the tolerance alone a circle
        // is never recognised as one and comes out as seventeen arcs instead of four.
        const double CircleRipple = 2.7e-4;

        /// <summary>A run that is one complete circle. <see cref="FindArcs"/> grows each span against a circle fitted to a
        /// window, and a window is the wrong instrument for a closed ring: the source's own ripple makes its fitted radius
        /// come out low or high by a fraction of the tolerance, so the ring's last points fail the radial test and the span
        /// is taken truncated - a circle baked as three arcs of 123, 163 and 73 degrees, eleven cubics, where the shape is
        /// four. Over the whole run that bias cancels: the ripple's period is one of the source's cubics, a quarter turn,
        /// so a complete turn is four whole periods and the fit lands on the mean radius. Testing the run as a circle first
        /// therefore costs nothing where it fails - it is one pass over the points, and the greedy pass still runs.</summary>
        static bool WholeCircle(List<Pt2> pts, double err, out Arc arc)
        {
            arc = default;
            int n = pts.Count;
            if (n < 8) return false;
            if (!CircleFit(pts, 0, n - 1, out Pt2 c, out double r)) return false;
            double lim = Math.Max(err, CircleRipple * r);
            double worst = 0;
            for (int k = 0; k < n; k++) worst = Math.Max(worst, Math.Abs((pts[k] - c).Length - r));
            if (worst > lim)
            {
                if (ArcLog != null && n > 6)
                    ArcLog($"run of {n} pts: not one circle, a point sits {worst:0.####} off the fitted r={r:0.###} (limit {lim:0.####})");
                return false;
            }
            double sweep = 0;
            Pt2 vp = pts[0] - c;
            for (int k = 1; k < n; k++)
            {
                Pt2 vq = pts[k] - c;
                sweep += Math.Atan2(Cross(vp, vq), Dot(vp, vq));
                vp = vq;
            }
            if (Math.Abs(Math.Abs(sweep) - 2 * Math.PI) > 1e-6)
            {
                if (ArcLog != null && n > 6)
                    ArcLog($"run of {n} pts: concyclic to {worst:0.####} but turns {sweep * 180 / Math.PI:0.#}deg, not 360");
                return false;                                                     // the run does not close on itself
            }
            if (ArcLog != null) ArcLog($"run of {n} pts: one closed circle r={r:0.###}, worst point {worst:0.####} off it (limit {lim:0.####})");
            // clamped rather than accumulated: EmitArc takes k = Ceiling(|sweep| / (pi/2)), so 2*pi + 1e-9 makes it five
            // arcs of 72 degrees instead of four quarter turns, and the whole ring wobbles by the difference
            arc = new Arc { I = 0, J = n - 1, C = c, R = r, Sweep = sweep > 0 ? 2 * Math.PI : -2 * Math.PI };
            return true;
        }

        /// <summary>The circular spans of a run, in one greedy forward pass: a seed circle, and then the span grows
        /// while the next point lies within err of it, turns the same way and its chord stays near the circle.
        /// The sweep is accumulated from pts[i] itself, not from the third point: starting it at i + 2 lost the first two
        /// steps, so a half-disc cap sampled in 16 chords came out at 157.5 degrees and EmitArc, which steps the circle by
        /// sweep / k from pts[i]'s own angle, put every interior split and the last handle at the wrong angle - a quarter
        /// pixel of wobble along the whole cap, and hard pixels wherever the stroke and the conversion were compared.
        /// Both distance tests are on squared lengths and the turn is one atan2 of the two radius vectors rather than two
        /// of their absolute angles: a dense ring reaches the fitter with thousands of points and this pass runs over every
        /// sub-range of every split, so the constant factor here is the difference between a conversion that opens a page
        /// and one that stalls it.</summary>
        static List<Arc> FindArcs(List<Pt2> pts, double err)
        {
            var arcs = new List<Arc>();
            int n = pts.Count;
            if (WholeCircle(pts, err, out Arc full))
            {
                arcs.Add(full);
                return arcs;
            }
            double worstRad = 0, worstChord = 0;
            for (int i = 0; i + 2 < n;)
            {
                if (!SeedCircle(pts, i, out Pt2 c, out double r)) { i++; continue; }
                double slo = (1 - ArcChordShare) * r, shi = (1 + ArcChordShare) * r;
                double rlo = Math.Max(0, r - err), rhi = r + err;
                slo *= slo; shi *= shi; rlo *= rlo; rhi *= rhi;
                double seedLen = (pts[i + 1] - pts[i]).Length;
                int j = i, why = 0;
                Pt2 p = pts[i], vp = p - c;                       // the previous ring point, and its radius vector
                double sweep = 0;
                int sgn = 0;
                while (j + 1 < n)
                {
                    Pt2 q = pts[j + 1], vq = q - c;
                    double stepLen = (q - p).Length;
                    if (stepLen < 0.55 * seedLen || stepLen > 1.8 * seedLen) { why = 1; break; }
                    Pt2 m = (p + q) * 0.5 - c;
                    double m2 = Dot(m, m);
                    if (m2 < slo || m2 > shi)
                    {
                        if (ArcLog != null) { double sg = Math.Abs(Math.Sqrt(m2) - r) / r; if (sg > worstChord) worstChord = sg; }
                        why = 1; break;
                    }
                    double q2 = Dot(vq, vq);
                    if (q2 < rlo || q2 > rhi)
                    {
                        double rad = Math.Abs(Math.Sqrt(q2) - r);
                        if (rad > worstRad) worstRad = rad;
                        why = 2; break;
                    }
                    double da = Math.Atan2(Cross(vp, vq), Dot(vp, vq));
                    if (sgn == 0) sgn = da >= 0 ? 1 : -1;
                    else if (da * sgn <= 0) { why = 3; break; }                       // the turn reverses: the arc ended
                    if (Math.Abs(sweep + da) > 2 * Math.PI + 1e-9) { why = 4; break; } // a closed run repeats its first point at exactly one turn
                    sweep += da; vp = vq; p = q; j++;
                }
                bool anchored = (i == 0 || j == n - 1);
                double reqSweep = anchored ? MinArcSweep : 85 * Math.PI / 180;
                int minPts = anchored ? 3 : 4;
                if (j >= i + minPts && Math.Abs(sweep) >= reqSweep)
                {
                    if (ArcLog != null) ArcLog($"  span i={i} j={j} r={r:0.###} sweep={sweep * 180 / Math.PI:0.#}deg taken" +
                        (why == 0 ? ", ran out of run" : $", stopped short: {Why(why, err)}"));
                    arcs.Add(new Arc { I = i, J = j, C = c, R = r, Sweep = sweep }); i = j;
                }
                else
                {
                    if (ArcLog != null && j > i + 1) ArcLog(Reject(i, j, r, sweep, why, err));
                    i++;
                }
            }
            if (ArcLog != null && arcs.Count == 0 && n > 6)
                ArcLog($"  greedy pass found no span either; worst radial {worstRad:0.####} (err {err}), worst chord sagitta {worstChord:P2} (limit {ArcChordShare:P0})");
            return arcs;
        }

        static string Reject(int i, int j, double r, double sweep, int why, double err)
            => $"  span i={i} j={j} r={r:0.###} sweep={sweep * 180 / Math.PI:0.#}deg rejected: " + Why(why, err);

        static string Why(int why, double err) => why switch
        {
            1 => $"a chord cuts more than {ArcChordShare:P0} of the radius into the circle",
            2 => $"a point sits further than {err} off the circle",
            3 => "the turn reverses",
            4 => "the span passes one full turn",
            _ => $"the sweep is under {MinArcSweep * 180 / Math.PI:0}deg",
        };

        /// <summary>Emits a run as its arcs, with the pieces between them fitted as before. The tangent at an arc boundary is
        /// the circle's, not the chord across the boundary: a straight edge meets a cap tangentially, and a chord tangent
        /// there would kink the last cubic of the edge.</summary>
        static void FitArcs(List<Pt2> pts, List<Arc> arcs, Pt2 t1, Pt2 t2, double err, List<Seg> outSegs, int depth)
        {
            int n = pts.Count, from = 0;
            Pt2 tan = t1;                                                 // the direction of travel at pts[from]
            for (int a = 0; a <= arcs.Count; a++)
            {
                Arc arc = a < arcs.Count ? arcs[a] : default;
                int to = a < arcs.Count ? arc.I : n - 1;
                Pt2 end = a < arcs.Count ? ArcTan(arc, pts[arc.I]) * -1.0 : t2; // t2 points back into the run, as GenBez wants
                if (to > from) FitRun(pts.GetRange(from, to - from + 1), tan, end, err, outSegs, depth + 1);
                if (a == arcs.Count) return;
                EmitArc(arc, pts[arc.I], pts[arc.J], outSegs);
                from = arc.J;
                tan = ArcTan(arc, pts[arc.J]);
            }
        }

        /// <summary>The arc as cubics, one per quarter turn at most: the endpoints stay the ring's own points so the piece
        /// joins what comes before and after it exactly, the splits between them lie on the circle.</summary>
        static void EmitArc(Arc a, Pt2 p0, Pt2 p1, List<Seg> outSegs)
        {
            int k = Math.Max(1, (int)Math.Ceiling((Math.Abs(a.Sweep) - 1e-6) / (Math.PI / 2)));
            double d = a.Sweep / k, al = 4.0 / 3.0 * Math.Tan(Math.Abs(d) / 4) * a.R;
            double sgn = a.Sweep > 0 ? 1 : -1;
            double a0 = Math.Atan2(p0.Y - a.C.Y, p0.X - a.C.X);
            for (int m = 0; m < k; m++)
            {
                double sa = a0 + d * m, ea = sa + d;
                Pt2 A = m == 0 ? p0 : On(a.C, a.R, sa), B = m == k - 1 ? p1 : On(a.C, a.R, ea);
                Pt2 tA = TanAt(sa, sgn), tB = TanAt(ea, sgn);
                outSegs.Add(new Seg { P0 = A, C1 = A + tA * al, C2 = B - tB * al, P1 = B, Cub = true });
            }
        }

        static Pt2 On(Pt2 c, double r, double a) => new Pt2(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        static Pt2 TanAt(double a, double sgn) => new Pt2(-Math.Sin(a) * sgn, Math.Cos(a) * sgn);

        /// <summary>The unit tangent in the direction of travel at one point of an arc.</summary>
        static Pt2 ArcTan(Arc a, Pt2 p) => TanAt(Math.Atan2(p.Y - a.C.Y, p.X - a.C.X), a.Sweep > 0 ? 1 : -1);

        /// <summary>The circle through three points; false when they are collinear to the point where its radius is meaningless.</summary>
        static bool Circle3(Pt2 a, Pt2 b, Pt2 c, out Pt2 centre, out double r)
        {
            centre = a; r = 0;
            double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            if (Math.Abs(d) < 1e-12) return false;
            double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
            double ux = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
            double uy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
            centre = new Pt2(ux, uy);
            r = (a - centre).Length;
            return r > 1e-9 && r < 1e7;
        }

        /// <summary>The largest distance from the cubic (sampled at 32 points between its ends) to the polyline through pts.</summary>
        static double CurveDev(Pt2[] bz, List<Pt2> pts)        {
            double worst = 0;
            for (int i = 1; i < 32; i++)
            {
                Pt2 c = Q(bz, i / 32.0);
                double best = double.MaxValue;
                for (int j = 0; j + 1 < pts.Count; j++)
                {
                    Pt2 a = pts[j], b = pts[j + 1], ab = b - a;
                    double L2 = Dot(ab, ab);
                    double t = L2 > 1e-18 ? Math.Max(0, Math.Min(1, Dot(c - a, ab) / L2)) : 0;
                    Pt2 q = a + ab * t;
                    double sag = 4.0 * t * (1.0 - t) * 0.04 * Math.Sqrt(L2);
                    double d = Math.Max(0.0, (c - q).Length - sag);
                    if (d < best) best = d;
                }
                if (best > worst) worst = best;
            }
            return worst;
        }

        static double[] ChordParam(List<Pt2> pts)
        {
            int n = pts.Count;
            var u = new double[n];
            for (int i = 1; i < n; i++) u[i] = u[i - 1] + (pts[i] - pts[i - 1]).Length;
            double tot = u[n - 1];
            for (int i = 1; i < n; i++) u[i] = tot > 0 ? u[i] / tot : (double)i / (n - 1);
            return u;
        }

        static Pt2[] GenBez(List<Pt2> pts, double[] u, Pt2 t1, Pt2 t2)
        {
            int n = pts.Count;
            Pt2 p0 = pts[0], p3 = pts[n - 1];
            double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
            for (int i = 0; i < n; i++)
            {
                double t = u[i], s = 1 - t;
                double b0 = s * s * s, b1 = 3 * t * s * s, b2 = 3 * t * t * s, b3 = t * t * t;
                Pt2 a1 = t1 * b1, a2 = t2 * b2;
                Pt2 tmp = pts[i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
                c00 += Dot(a1, a1); c01 += Dot(a1, a2); c11 += Dot(a2, a2);
                x0 += Dot(a1, tmp); x1 += Dot(a2, tmp);
            }
            double det = c00 * c11 - c01 * c01;
            double segLen = (p3 - p0).Length;
            double al = 0, ar = 0;
            if (Math.Abs(det) > 1e-12) { al = (x0 * c11 - x1 * c01) / det; ar = (c00 * x1 - c01 * x0) / det; }
            double eps = 1e-6 * segLen;
            // a near-parallel tangent pair can solve to huge handles (a spike far off the ring): reject those too
            if (!(al > eps) || !(ar > eps) || al > 2 * segLen || ar > 2 * segLen) { al = segLen / 3; ar = segLen / 3; }
            return new[] { p0, p0 + t1 * al, p3 + t2 * ar, p3 };
        }

        static Pt2[] GenBezSDM(List<Pt2> pts, double[] u, Pt2 t1, Pt2 t2, Pt2[] prevBez)
        {
            int n = pts.Count;
            Pt2 p0 = pts[0], p3 = pts[n - 1];
            double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
            const double wTan = 0.05;
            for (int i = 0; i < n; i++)
            {
                double t = u[i], s = 1 - t;
                double b0 = s * s * s, b1 = 3 * t * s * s, b2 = 3 * t * t * s, b3 = t * t * t;
                Pt2 a1 = t1 * b1, a2 = t2 * b2;
                Pt2 tmp = pts[i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
                Pt2 d = ((prevBez[1] - prevBez[0]) * (s * s) + (prevBez[2] - prevBez[1]) * (2 * s * t) + (prevBez[3] - prevBez[2]) * (t * t)) * 3;
                double dl = d.Length;
                if (dl > 1e-9)
                {
                    Pt2 ut = d * (1.0 / dl);
                    Pt2 un = new Pt2(-ut.Y, ut.X);
                    double a1n = Dot(a1, un), a2n = Dot(a2, un), tmpn = Dot(tmp, un);
                    double a1t = Dot(a1, ut), a2t = Dot(a2, ut), tmpt = Dot(tmp, ut);
                    c00 += a1n * a1n + wTan * a1t * a1t;
                    c01 += a1n * a2n + wTan * a1t * a2t;
                    c11 += a2n * a2n + wTan * a2t * a2t;
                    x0 += a1n * tmpn + wTan * a1t * tmpt;
                    x1 += a2n * tmpn + wTan * a2t * tmpt;
                }
                else
                {
                    c00 += Dot(a1, a1); c01 += Dot(a1, a2); c11 += Dot(a2, a2);
                    x0 += Dot(a1, tmp); x1 += Dot(a2, tmp);
                }
            }
            double det = c00 * c11 - c01 * c01;
            double segLen = (p3 - p0).Length;
            double al = 0, ar = 0;
            if (Math.Abs(det) > 1e-12) { al = (x0 * c11 - x1 * c01) / det; ar = (c00 * x1 - c01 * x0) / det; }
            double eps = 1e-6 * segLen;
            if (!(al > eps) || !(ar > eps) || al > 2.5 * segLen || ar > 2.5 * segLen) return GenBez(pts, u, t1, t2);
            return new[] { p0, p0 + t1 * al, p3 + t2 * ar, p3 };
        }

        static Pt2 Q(Pt2[] b, double t)
        {
            double s = 1 - t;
            return b[0] * (s * s * s) + b[1] * (3 * t * s * s) + b[2] * (3 * t * t * s) + b[3] * (t * t * t);
        }

        static double MaxErr(List<Pt2> pts, Pt2[] bz, double[] u, out int split)
        {
            int n = pts.Count;
            double mx = 0;
            split = n / 2;
            for (int i = 1; i < n - 1; i++)
            {
                Pt2 d = Q(bz, u[i]) - pts[i];
                double e2 = Dot(d, d);
                if (e2 > mx) { mx = e2; split = i; }
            }
            return Math.Sqrt(mx);
        }

        static double[] Reparam(Pt2[] b, List<Pt2> pts, double[] u)
        {
            var r = new double[u.Length];
            for (int i = 0; i < u.Length; i++)
            {
                double t = u[i], s = 1 - t;
                Pt2 q = Q(b, t);
                Pt2 q1 = ((b[1] - b[0]) * (s * s) + (b[2] - b[1]) * (2 * s * t) + (b[3] - b[2]) * (t * t)) * 3;
                Pt2 d0 = b[2] - b[1] * 2 + b[0], d1 = b[3] - b[2] * 2 + b[1];
                Pt2 q2 = (d0 * s + d1 * t) * 6;
                Pt2 diff = q - pts[i];
                double num = Dot(diff, q1), den = Dot(q1, q1) + Dot(diff, q2);
                r[i] = den != 0 ? Math.Max(0, Math.Min(1, t - num / den)) : t;
            }
            r[0] = 0; r[u.Length - 1] = 1;
            return r;
        }
    }
}
