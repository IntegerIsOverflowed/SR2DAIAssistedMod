using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------
    // Smooth curves for Sprite.
    //
    // Two levels, same rendering back end (the flattened curve goes through
    // DrawPolyline / FillPolygon / DrawPolyline2, so width, AA, every LineOp and
    // dash patterns work exactly as for straight shapes):
    //
    //  1. "Just make it smooth" - no extra points:
    //       DrawCurve(points, ...)            spline THROUGH the given points (Catmull-Rom)
    //       FillCurve(points, ...)            closed version, filled
    //       DrawPolyline(points, ..., Smooth: true) / FillPolygon(..., Smooth: true)
    //     One knob: Tension 0..1 (0 = default, loose and round; 1 = straight lines).
    //
//  2. Full control - explicit control points, either directly:
    //       DrawBezier(p0, c0, c1, p1, ...)   cubic;  DrawQuadBezier(p0, c, p1, ...)
    //     or through a PathBuilder (the easy way to manage many control points):
    //       var p = new Sprite.PathBuilder();
    //       p.MoveTo(10, 10).CurveTo(50, 0, 90, 80, 120, 60).SmoothTo(200, 20, 240, 90)
    //        .ArcTo(...).LineTo(...).Close();
    //       canvas.DrawPath(p, col, Width: 3, AA: true);   canvas.FillPath(p, col, AA: true);
    //
    // Flattening is adaptive: each curve is split until the chord deviates less than
    // Sprite.CurveTolerance pixels (default 0.2), so a small curve costs a handful of
    // segments and a screen-sized one a few hundred - never a fixed count.
    // ------------------------------------------------------------------------
    internal unsafe partial class Sprite
    {
        /// <summary>Maximum distance (pixels) between a curve and its polyline approximation. 0.2 is
        /// invisible even with AA; 1.0 is noticeably faceted on large curves but ~3x fewer segments.</summary>
        public static float CurveTolerance = 0.2f;

        // ---------------------------------------------------------------- scratch
        [ThreadStatic] private static List<PointF>? tFlat;
        private static List<PointF> Flat() { var l = tFlat ??= new List<PointF>(256); l.Clear(); return l; }

        // ====================================================================
        // Level 1: smooth through the given points (no control points needed)
        // ====================================================================

        /// <summary>
        /// Smooth curve THROUGH <paramref name="Points"/> (centripetal Catmull-Rom: no loops or
        /// overshoot at uneven spacing). <paramref name="Tension"/> 0 = round, 1 = the plain
        /// polyline; <paramref name="Closed"/> makes a smooth loop. Width / AA / Op as in
        /// <see cref="DrawPolyline"/>.
        /// </summary>
        public void DrawCurve(ReadOnlySpan<PointF> Points, int c, float Width = 1f, bool AA = false, bool Closed = false,
                              float Tension = 0f, SR2D.LineOp Op = SR2D.LineOp.Set, bool RoundCaps = false, int BlendFactor = 128)
        {
            if (Points.Length < 2) return;
            var f = Flat();
            FlattenSpline(Points, Closed, Tension, f);
            DrawPolyline(AsSpan(f), c, Width, AA, Closed, Op, RoundCaps, BlendFactor);
        }

        /// <summary>Closed smooth shape through the points, filled.</summary>
        public void FillCurve(ReadOnlySpan<PointF> Points, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = false,
                              float Tension = 0f, bool EvenOdd = false, int BlendFactor = 128)
        {
            if (Points.Length < 3) return;
            var f = Flat();
            FlattenSpline(Points, true, Tension, f);
            FillPolygon(AsSpan(f), c, Op, AA, EvenOdd, BlendFactor);
        }

        /// <summary>Dashed / hairline smooth curve through the points (<see cref="DrawPolyline2"/> back end:
        /// DotLen / GapLen in pixels, Phase for animation, all LineOps, always 1 px).</summary>
        public void DrawCurve2(ReadOnlySpan<PointF> Points, int c, bool Closed = false, float Tension = 0f,
                               SR2D.LineOp Op = SR2D.LineOp.Set, float DotLen = 0, float GapLen = 0, float Phase = 0, int BlendFactor = 128)
        {
            if (Points.Length < 2) return;
            var f = Flat();
            FlattenSpline(Points, Closed, Tension, f);
            DrawPolyline2(AsSpan(f), c, Closed, Op, DotLen, GapLen, Phase, BlendFactor);
        }

        /// <summary><see cref="DrawPolyline"/> with a <paramref name="Smooth"/> switch: true = the same call
        /// draws a spline through the points instead of straight segments.</summary>
        public void DrawPolyline(ReadOnlySpan<PointF> Points, int c, bool Smooth, float Width = 1f, bool AA = false, bool Closed = false,
                                 SR2D.LineOp Op = SR2D.LineOp.Set, bool RoundCaps = false, int BlendFactor = 128, float Tension = 0f)
        {
            if (Smooth) DrawCurve(Points, c, Width, AA, Closed, Tension, Op, RoundCaps, BlendFactor);
            else DrawPolyline(Points, c, Width, AA, Closed, Op, RoundCaps, BlendFactor);
        }

        /// <summary><see cref="FillPolygon"/> with a <paramref name="Smooth"/> switch (rounded blob through the vertices).</summary>
        public void FillPolygon(ReadOnlySpan<PointF> Points, int c, bool Smooth, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = false,
                                bool EvenOdd = false, int BlendFactor = 128, float Tension = 0f)
        {
            if (Smooth) FillCurve(Points, c, Op, AA, Tension, EvenOdd, BlendFactor);
            else FillPolygon(Points, c, Op, AA, EvenOdd, BlendFactor);
        }

        // ====================================================================
        // Level 2: explicit control points
        // ====================================================================

        /// <summary>Cubic Bézier from p0 to p1 with control points c0, c1.</summary>
        public void DrawBezier(PointF p0, PointF c0, PointF c1, PointF p1, int c, float Width = 1f, bool AA = false,
                               SR2D.LineOp Op = SR2D.LineOp.Set, bool RoundCaps = false, int BlendFactor = 128)
        {
            var f = Flat();
            f.Add(p0);
            FlattenCubic(p0.X, p0.Y, c0.X, c0.Y, c1.X, c1.Y, p1.X, p1.Y, f);
            DrawPolyline(AsSpan(f), c, Width, AA, false, Op, RoundCaps, BlendFactor);
        }

        /// <summary>Quadratic Bézier from p0 to p1 with one control point.</summary>
        public void DrawQuadBezier(PointF p0, PointF ctrl, PointF p1, int c, float Width = 1f, bool AA = false,
                                   SR2D.LineOp Op = SR2D.LineOp.Set, bool RoundCaps = false, int BlendFactor = 128)
        {
            var f = Flat();
            f.Add(p0);
            FlattenQuad(p0.X, p0.Y, ctrl.X, ctrl.Y, p1.X, p1.Y, f);
            DrawPolyline(AsSpan(f), c, Width, AA, false, Op, RoundCaps, BlendFactor);
        }

        /// <summary>
        /// Strokes a <see cref="PathBuilder"/>. Every sub-path (MoveTo … MoveTo/Close) is one
        /// polyline; a closed sub-path gets a proper join at the seam.
        /// </summary>
        public void DrawPath(PathBuilder Path, int c, float Width = 1f, bool AA = false,
                             SR2D.LineOp Op = SR2D.LineOp.Set, bool RoundCaps = false, int BlendFactor = 128)
        {
            var f = Flat();
            foreach (var (start, count, closed) in Path.Flatten(f))
                if (count >= 2) DrawPolyline(AsSpan(f, start, count), c, Width, AA, closed, Op, RoundCaps, BlendFactor);
        }

        /// <summary>Dashed 1-px stroke of a path (<see cref="DrawPolyline2"/> back end, phase continuous per sub-path).</summary>
        public void DrawPath2(PathBuilder Path, int c, SR2D.LineOp Op = SR2D.LineOp.Set, float DotLen = 0, float GapLen = 0, float Phase = 0, int BlendFactor = 128)
        {
            var f = Flat();
            foreach (var (start, count, closed) in Path.Flatten(f))
                if (count >= 2) DrawPolyline2(AsSpan(f, start, count), c, closed, Op, DotLen, GapLen, Phase, BlendFactor);
        }

        /// <summary>
        /// Fills a path. All sub-paths are filled together in ONE call (non-zero winding by
        /// default: a sub-path drawn in the opposite direction makes a hole; <paramref name="EvenOdd"/>
        /// makes every overlap alternate). Open sub-paths are closed implicitly.
        /// </summary>
        public void FillPath(PathBuilder Path, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = false, bool EvenOdd = false, int BlendFactor = 128)
        {
            var f = Flat();
            int nsub = Math.Max(1, Path.SubPathCount);
            Span<int> counts = nsub <= 1024 ? stackalloc int[nsub] : new int[nsub];
            int nc = 0, total = 0;
            foreach (var (start, count, _) in Path.Flatten(f))
            {
                if (count < 3) { f.RemoveRange(start, count); continue; }   // degenerate sub-path: drop it (keeps points contiguous)
                counts[nc++] = count; total += count;
            }
            if (nc == 0) return;
            FillPolygons(AsSpan(f, 0, total), counts.Slice(0, nc), c, Op, AA, EvenOdd, BlendFactor);
        }

        // ====================================================================
        // PathBuilder: a small, allocation-light path with an SVG-like API
        // ====================================================================

        /// <summary>
        /// Builds a path from lines, Bézier curves, arcs and smooth (spline) sections. All
        /// methods return <c>this</c> so calls chain. Coordinates are pixel centres like every
        /// other shape call. Reusable: call <see cref="Clear"/> and build again (no allocations
        /// after the first use).
        /// </summary>
        public sealed class PathBuilder
        {
            // Command stream: each command is a kind + up to 6 floats.
            enum K : byte { Move, Line, Quad, Cubic, Arc, Smooth, Close }
            readonly List<byte> kinds = new List<byte>();
            readonly List<float> data = new List<float>();
            float startX, startY, curX, curY, lastCx, lastCy; bool haveLastC; K lastK = K.Move;
            int subPaths;

            public int SubPathCount => subPaths;
            public bool IsEmpty => kinds.Count == 0;
            public PointF Current => new PointF(curX, curY);

            public PathBuilder Clear() { kinds.Clear(); data.Clear(); subPaths = 0; haveLastC = false; lastK = K.Move; return this; }

            /// <summary>Starts a new sub-path at (x, y).</summary>
            public PathBuilder MoveTo(float x, float y)
            {
                Push(K.Move, x, y); startX = curX = x; startY = curY = y; subPaths++; haveLastC = false; return this;
            }
            public PathBuilder MoveTo(PointF p) => MoveTo(p.X, p.Y);

            /// <summary>Straight segment to (x, y).</summary>
            public PathBuilder LineTo(float x, float y) { Ensure(); Push(K.Line, x, y); curX = x; curY = y; haveLastC = false; return this; }
            public PathBuilder LineTo(PointF p) => LineTo(p.X, p.Y);

            /// <summary>Quadratic Bézier to (x, y) with control point (cx, cy).</summary>
            public PathBuilder QuadTo(float cx, float cy, float x, float y)
            {
                Ensure(); Push(K.Quad, cx, cy, x, y); lastCx = cx; lastCy = cy; haveLastC = true; curX = x; curY = y; return this;
            }

            /// <summary>Cubic Bézier to (x, y) with control points (c0x, c0y) and (c1x, c1y).</summary>
            public PathBuilder CurveTo(float c0x, float c0y, float c1x, float c1y, float x, float y)
            {
                Ensure(); Push(K.Cubic, c0x, c0y, c1x, c1y, x, y); lastCx = c1x; lastCy = c1y; haveLastC = true; curX = x; curY = y; return this;
            }
            public PathBuilder CurveTo(PointF c0, PointF c1, PointF p) => CurveTo(c0.X, c0.Y, c1.X, c1.Y, p.X, p.Y);

            /// <summary>
            /// Cubic Bézier whose first control point is the reflection of the previous curve's
            /// last control point (SVG "S"): the curve continues with a continuous tangent, so
            /// you only give the second control point and the end point. After a straight
            /// segment / MoveTo the first control point is the current point.
            /// </summary>
            public PathBuilder SmoothTo(float c1x, float c1y, float x, float y)
            {
                Ensure();
                float c0x = haveLastC ? 2 * curX - lastCx : curX, c0y = haveLastC ? 2 * curY - lastCy : curY;
                return CurveTo(c0x, c0y, c1x, c1y, x, y);
            }

            /// <summary>Quadratic version of <see cref="SmoothTo(float,float,float,float)"/> (SVG "T").</summary>
            public PathBuilder SmoothQuadTo(float x, float y)
            {
                Ensure();
                bool q = haveLastC && lastK == K.Quad;
                float cx = q ? 2 * curX - lastCx : curX, cy = q ? 2 * curY - lastCy : curY;
                return QuadTo(cx, cy, x, y);
            }

            /// <summary>
            /// Circular/elliptical arc from the current point to (x, y): radii rx, ry, ellipse
            /// rotation <paramref name="rotationDeg"/>, <paramref name="largeArc"/> and <paramref name="sweep"/>
            /// pick one of the four candidates exactly like SVG's "A" command. Radii too small
            /// for the two points are scaled up (SVG rule).
            /// </summary>
            public PathBuilder ArcTo(float rx, float ry, float rotationDeg, bool largeArc, bool sweep, float x, float y)
            {
                Ensure(); Push(K.Arc, rx, ry, rotationDeg, (largeArc ? 1 : 0) + (sweep ? 2 : 0), x, y); curX = x; curY = y; haveLastC = false; return this;
            }

            /// <summary>Arc around a centre: from the current angle position (the current point is
            /// moved there with a line if needed) sweeping <paramref name="sweepDeg"/> degrees.</summary>
            public PathBuilder ArcAround(float cx, float cy, float r, float startDeg, float sweepDeg)
            {
                float a0 = startDeg * MathF.PI / 180, a1 = (startDeg + sweepDeg) * MathF.PI / 180;
                float sx = cx + MathF.Cos(a0) * r, sy = cy + MathF.Sin(a0) * r;
                if (kinds.Count == 0) MoveTo(sx, sy); else if (sx != curX || sy != curY) LineTo(sx, sy);
                float ex = cx + MathF.Cos(a1) * r, ey = cy + MathF.Sin(a1) * r;
                float abs = MathF.Abs(sweepDeg);
                if (abs >= 360f) { float mx = cx + MathF.Cos(a0 + MathF.PI) * r, my = cy + MathF.Sin(a0 + MathF.PI) * r; ArcTo(r, r, 0, false, sweepDeg > 0, mx, my); return ArcTo(r, r, 0, false, sweepDeg > 0, sx, sy); }
                return ArcTo(r, r, 0, abs > 180f, sweepDeg > 0, ex, ey);
            }

            /// <summary>
            /// Smooth spline section through the given points (Catmull-Rom, see <see cref="DrawCurve"/>),
            /// starting at the current point. The tangent at the start blends from the previous
            /// segment. <paramref name="tension"/> 0 = round … 1 = straight.
            /// </summary>
            public PathBuilder SmoothThrough(ReadOnlySpan<PointF> pts, float tension = 0f)
            {
                Ensure();
                if (pts.Length == 0) return this;
                Push(K.Smooth, pts.Length, tension);
                foreach (var p in pts) { data.Add(p.X); data.Add(p.Y); }
                curX = pts[pts.Length - 1].X; curY = pts[pts.Length - 1].Y; haveLastC = false; return this;
            }

            /// <summary>Closes the current sub-path (straight line back to its MoveTo point, joined smoothly when stroked).</summary>
            public PathBuilder Close() { if (kinds.Count > 0 && lastK != K.Close) { Push(K.Close); curX = startX; curY = startY; haveLastC = false; } return this; }

            // -------- convenience whole shapes (each is its own sub-path)
            public PathBuilder Rect(float x, float y, float w, float h) => MoveTo(x, y).LineTo(x + w, y).LineTo(x + w, y + h).LineTo(x, y + h).Close();
            public PathBuilder Ellipse(float cx, float cy, float rx, float ry)
                => MoveTo(cx + rx, cy).ArcTo(rx, ry, 0, false, true, cx - rx, cy).ArcTo(rx, ry, 0, false, true, cx + rx, cy).Close();
            public PathBuilder Circle(float cx, float cy, float r) => Ellipse(cx, cy, r, r);
            public PathBuilder RoundRect(float x, float y, float w, float h, float r)
            {
                r = MathF.Min(r, MathF.Min(w, h) * 0.5f); if (r <= 0) return Rect(x, y, w, h);
                return MoveTo(x + r, y).LineTo(x + w - r, y).ArcTo(r, r, 0, false, true, x + w, y + r)
                      .LineTo(x + w, y + h - r).ArcTo(r, r, 0, false, true, x + w - r, y + h)
                      .LineTo(x + r, y + h).ArcTo(r, r, 0, false, true, x, y + h - r)
                      .LineTo(x, y + r).ArcTo(r, r, 0, false, true, x + r, y).Close();
            }
            /// <summary>Whole closed spline through the points as one sub-path (blob).</summary>
            public PathBuilder SmoothPolygon(ReadOnlySpan<PointF> pts, float tension = 0f)
            {
                if (pts.Length < 2) return this;
                MoveTo(pts[0]); Push(K.Smooth, -pts.Length, tension);   // negative count = closed loop
                foreach (var p in pts) { data.Add(p.X); data.Add(p.Y); }
                return Close();
            }

            // -------- internals
            void Ensure() { if (kinds.Count == 0 || lastK == K.Close) { float x = curX, y = curY; kinds.Add((byte)K.Move); data.Add(x); data.Add(y); startX = x; startY = y; subPaths++; lastK = K.Move; } }
            void Push(K k, params float[] v) { kinds.Add((byte)k); data.AddRange(v); lastK = k; }
            void Push(K k) { kinds.Add((byte)k); lastK = k; }

            /// <summary>Flattens into <paramref name="outPts"/>; yields (start index, count, closed) per sub-path.</summary>
            internal IEnumerable<(int start, int count, bool closed)> Flatten(List<PointF> outPts)
            {
                int di = 0, start = outPts.Count; bool open = false, closed = false;
                float cx = 0, cy = 0, sx = 0, sy = 0;
                var tmp = new List<PointF>(0);
                for (int ki = 0; ki < kinds.Count; ki++)
                {
                    var k = (K)kinds[ki];
                    switch (k)
                    {
                        case K.Move:
                            if (open) { int cnt = outPts.Count - start; if (closed && cnt > 1 && outPts[start] == outPts[outPts.Count - 1]) { outPts.RemoveAt(outPts.Count - 1); cnt--; } yield return (start, cnt, closed); }
                            start = outPts.Count; closed = false; open = true;
                            cx = sx = data[di++]; cy = sy = data[di++]; outPts.Add(new PointF(cx, cy));
                            break;
                        case K.Line:
                            cx = data[di++]; cy = data[di++]; outPts.Add(new PointF(cx, cy)); break;
                        case K.Quad:
                            { float ax = data[di++], ay = data[di++], x = data[di++], y = data[di++]; FlattenQuad(cx, cy, ax, ay, x, y, outPts); cx = x; cy = y; break; }
                        case K.Cubic:
                            { float ax = data[di++], ay = data[di++], bx = data[di++], by = data[di++], x = data[di++], y = data[di++]; FlattenCubic(cx, cy, ax, ay, bx, by, x, y, outPts); cx = x; cy = y; break; }
                        case K.Arc:
                            { float rx = data[di++], ry = data[di++], rot = data[di++]; int fl = (int)data[di++]; float x = data[di++], y = data[di++]; FlattenArc(cx, cy, rx, ry, rot, (fl & 1) != 0, (fl & 2) != 0, x, y, outPts); cx = x; cy = y; break; }
                        case K.Smooth:
                            {
                                int n = (int)data[di++]; float tension = data[di++]; bool loop = n < 0; if (loop) n = -n;
                                // build control list: current point + given points; previous point (if any) improves the start tangent
                                int need = n + 2;
                                var ctrl = tmp; ctrl.Clear(); if (ctrl.Capacity < need) ctrl.Capacity = need;
                                if (!loop)
                                {
                                    ctrl.Add(new PointF(cx, cy));
                                    for (int i = 0; i < n; i++) { ctrl.Add(new PointF(data[di], data[di + 1])); di += 2; }
                                    int before = outPts.Count;
                                    FlattenSpline(AsSpan(ctrl), false, tension, outPts);
                                    outPts.RemoveAt(before);                    // spline starts with the current point, already present
                                }
                                else
                                {
                                    for (int i = 0; i < n; i++) { ctrl.Add(new PointF(data[di], data[di + 1])); di += 2; }
                                    int before = outPts.Count;
                                    FlattenSpline(AsSpan(ctrl), true, tension, outPts);
                                    outPts.RemoveAt(before);
                                }
                                var last = outPts[outPts.Count - 1]; cx = last.X; cy = last.Y;
                                break;
                            }
                        case K.Close:
                            closed = true; cx = sx; cy = sy; break;
                    }
                }
                if (open) { int cnt = outPts.Count - start; if (closed && cnt > 1 && outPts[start] == outPts[outPts.Count - 1]) { outPts.RemoveAt(outPts.Count - 1); cnt--; } yield return (start, cnt, closed); }
            }
        }

        // ====================================================================
        // Flattening
        // ====================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ReadOnlySpan<PointF> AsSpan(List<PointF> l) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(l);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ReadOnlySpan<PointF> AsSpan(List<PointF> l, int start, int count) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(l).Slice(start, count);

        // Number of segments for a Bézier from the control polygon's "bend" (Wang's formula):
        // n >= sqrt( (3/4 * max second difference) / tol )  for cubics.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CubicSegments(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
        {
            float ddx = MathF.Max(MathF.Abs(x0 - 2 * x1 + x2), MathF.Abs(x1 - 2 * x2 + x3));
            float ddy = MathF.Max(MathF.Abs(y0 - 2 * y1 + y2), MathF.Abs(y1 - 2 * y2 + y3));
            float dd = MathF.Sqrt(ddx * ddx + ddy * ddy);
            int n = (int)MathF.Ceiling(MathF.Sqrt(0.75f * dd / MathF.Max(CurveTolerance, 0.01f)));
            return Math.Clamp(n, 1, 512);
        }

        /// <summary>Appends the cubic (without its first point) as line segments.</summary>
        private static void FlattenCubic(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3, List<PointF> o)
        {
            int n = CubicSegments(x0, y0, x1, y1, x2, y2, x3, y3);
            // forward differencing
            float h = 1f / n, h2 = h * h, h3 = h2 * h;
            float ax = -x0 + 3 * x1 - 3 * x2 + x3, bx = 3 * x0 - 6 * x1 + 3 * x2, cx = -3 * x0 + 3 * x1;
            float ay = -y0 + 3 * y1 - 3 * y2 + y3, by = 3 * y0 - 6 * y1 + 3 * y2, cy = -3 * y0 + 3 * y1;
            float px = x0, py = y0;
            float d1x = ax * h3 + bx * h2 + cx * h, d2x = 6 * ax * h3 + 2 * bx * h2, d3x = 6 * ax * h3;
            float d1y = ay * h3 + by * h2 + cy * h, d2y = 6 * ay * h3 + 2 * by * h2, d3y = 6 * ay * h3;
            for (int i = 1; i < n; i++)
            {
                px += d1x; d1x += d2x; d2x += d3x;
                py += d1y; d1y += d2y; d2y += d3y;
                o.Add(new PointF(px, py));
            }
            o.Add(new PointF(x3, y3));      // exact end point (no drift)
        }

        private static void FlattenQuad(float x0, float y0, float x1, float y1, float x2, float y2, List<PointF> o)
        {
            float ddx = MathF.Abs(x0 - 2 * x1 + x2), ddy = MathF.Abs(y0 - 2 * y1 + y2);
            float dd = MathF.Sqrt(ddx * ddx + ddy * ddy);
            int n = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(0.5f * dd / MathF.Max(CurveTolerance, 0.01f))), 1, 512);
            for (int i = 1; i < n; i++)
            {
                float t = (float)i / n, u = 1 - t;
                o.Add(new PointF(u * u * x0 + 2 * u * t * x1 + t * t * x2, u * u * y0 + 2 * u * t * y1 + t * t * y2));
            }
            o.Add(new PointF(x2, y2));
        }

        /// <summary>SVG end-point arc → centre parameterisation → line segments (appended without the start point).</summary>
        private static void FlattenArc(float x0, float y0, float rx, float ry, float rotDeg, bool large, bool sweep, float x1, float y1, List<PointF> o)
        {
            rx = MathF.Abs(rx); ry = MathF.Abs(ry);
            if (rx < 1e-6f || ry < 1e-6f || (x0 == x1 && y0 == y1)) { o.Add(new PointF(x1, y1)); return; }
            float phi = rotDeg * MathF.PI / 180, cp = MathF.Cos(phi), sp = MathF.Sin(phi);
            float dx = (x0 - x1) * 0.5f, dy = (y0 - y1) * 0.5f;
            float x1p = cp * dx + sp * dy, y1p = -sp * dx + cp * dy;
            float lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
            if (lam > 1) { float s = MathF.Sqrt(lam); rx *= s; ry *= s; }
            float num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            float den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            float coef = (large == sweep ? -1 : 1) * MathF.Sqrt(MathF.Max(0, num / den));
            float cxp = coef * (rx * y1p / ry), cyp = coef * (-ry * x1p / rx);
            float cx = cp * cxp - sp * cyp + (x0 + x1) * 0.5f, cy = sp * cxp + cp * cyp + (y0 + y1) * 0.5f;
            float ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry, vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;
            float a0 = MathF.Atan2(uy, ux);
            float da = MathF.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            if (!sweep && da > 0) da -= 2 * MathF.PI; else if (sweep && da < 0) da += 2 * MathF.PI;
            // segment count from the chord error of the larger radius
            float r = MathF.Max(rx, ry);
            float step = 2 * MathF.Acos(MathF.Max(-1f, 1 - MathF.Max(CurveTolerance, 0.01f) / MathF.Max(r, 0.01f)));
            int n = Math.Clamp((int)MathF.Ceiling(MathF.Abs(da) / MathF.Max(step, 1e-3f)), 1, 1024);
            for (int i = 1; i < n; i++)
            {
                float a = a0 + da * i / n, ca = MathF.Cos(a), sa = MathF.Sin(a);
                o.Add(new PointF(cx + rx * ca * cp - ry * sa * sp, cy + rx * ca * sp + ry * sa * cp));
            }
            o.Add(new PointF(x1, y1));
        }

        /// <summary>
        /// Centripetal Catmull-Rom through <paramref name="P"/> (alpha = 0.5: no cusps / self
        /// intersections for uneven spacing), converted segment-wise to cubic Béziers and
        /// flattened. Open: the end tangents are those of the end segments. Closed: wraps.
        /// Tension 0..1 scales the tangents down (1 = straight polyline).
        /// </summary>
        private static void FlattenSpline(ReadOnlySpan<PointF> P, bool closed, float tension, List<PointF> o)
        {
            int n = P.Length;
            if (n == 0) return;
            if (n == 1) { o.Add(P[0]); return; }
            float k = Math.Clamp(1f - tension, 0f, 1f);
            if (n == 2 && !closed) { o.Add(P[0]); o.Add(P[1]); return; }
            o.Add(P[0]);
            int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                PointF p1 = P[i], p2 = P[(i + 1) % n];
                PointF p0 = closed ? P[(i - 1 + n) % n] : (i > 0 ? P[i - 1] : Reflect(p2, p1));
                PointF p3 = closed ? P[(i + 2) % n] : (i + 2 < n ? P[i + 2] : Reflect(p1, p2));
                // centripetal parameterisation: knot intervals ~ sqrt(distance)
                float d01 = MathF.Sqrt(Dist(p0, p1)), d12 = MathF.Sqrt(Dist(p1, p2)), d23 = MathF.Sqrt(Dist(p2, p3));
                if (d12 < 1e-4f) continue;
                if (d01 < 1e-4f) d01 = d12; if (d23 < 1e-4f) d23 = d12;
                // tangents (Barry–Goldman form), scaled to Bézier handles
                float t1x = (p1.X - p0.X) / d01 - (p2.X - p0.X) / (d01 + d12) + (p2.X - p1.X) / d12;
                float t1y = (p1.Y - p0.Y) / d01 - (p2.Y - p0.Y) / (d01 + d12) + (p2.Y - p1.Y) / d12;
                float t2x = (p2.X - p1.X) / d12 - (p3.X - p1.X) / (d12 + d23) + (p3.X - p2.X) / d23;
                float t2y = (p2.Y - p1.Y) / d12 - (p3.Y - p1.Y) / (d12 + d23) + (p3.Y - p2.Y) / d23;
                float s = d12 * k / 3f;
                float c0x = p1.X + t1x * s, c0y = p1.Y + t1y * s, c1x = p2.X - t2x * s, c1y = p2.Y - t2y * s;
                FlattenCubic(p1.X, p1.Y, c0x, c0y, c1x, c1y, p2.X, p2.Y, o);
            }
            if (closed && o.Count > 1) o.RemoveAt(o.Count - 1);   // last point == first: DrawPolyline(Closed) adds the seam
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Dist(PointF a, PointF b) { float dx = b.X - a.X, dy = b.Y - a.Y; return MathF.Sqrt(dx * dx + dy * dy); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PointF Reflect(PointF a, PointF b) => new PointF(2 * b.X - a.X, 2 * b.Y - a.Y);   // b + (b - a): phantom end point
    }
}
