using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    /// <summary>Line end style for <see cref="Sprite.StrokePolyline"/> and friends.</summary>
    internal enum LineCap : byte
    {
        /// <summary>The stroke stops exactly at the end point.</summary>
        Butt,
        /// <summary>A half disc of the stroke width is added at the end.</summary>
        Round,
        /// <summary>The stroke continues half its width past the end point.</summary>
        Square,
    }

    /// <summary>Corner style for <see cref="Sprite.StrokePolyline"/> and friends.</summary>
    internal enum LineJoin : byte
    {
        /// <summary>Sharp corner; falls back to Bevel when the spike would exceed the miter limit.</summary>
        Miter,
        /// <summary>Rounded corner.</summary>
        Round,
        /// <summary>The corner is cut off flat.</summary>
        Bevel,
    }

    /// <summary>
    /// Stroke style: width, caps, joins, miter limit and dash pattern in one reusable object (like a Pen).
    /// Dash lengths are in pixels and alternate on / off; a dash pattern with round caps gives dotted lines when
    /// the "on" length is 0.
    /// </summary>
    internal sealed class StrokeStyle
    {
        public float Width = 1f;
        public LineCap Cap = LineCap.Butt;
        public LineJoin Join = LineJoin.Miter;
        /// <summary>Miter length / stroke width above which a miter join is bevelled (SVG / PDF default 4 = corners sharper than ~29 degrees get cut).</summary>
        public float MiterLimit = 4f;
        /// <summary>Dash pattern (on, off, on, off, ...) in pixels; null or empty = solid.</summary>
        public float[]? Dash;
        /// <summary>Distance into the dash pattern at which the stroke starts (animate it to make "marching ants").</summary>
        public float DashOffset;

        public StrokeStyle() { }
        public StrokeStyle(float width, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter, float miterLimit = 4f, float[]? dash = null, float dashOffset = 0)
        { Width = width; Cap = cap; Join = join; MiterLimit = miterLimit; Dash = dash; DashOffset = dashOffset; }
        public StrokeStyle Clone() => new StrokeStyle(Width, Cap, Join, MiterLimit, Dash == null ? null : (float[])Dash.Clone(), DashOffset);

        /// <summary>Round caps + round joins ("pen" look, the safe choice for hand drawing).</summary>
        public static StrokeStyle Round(float width, float[]? dash = null) => new StrokeStyle(width, LineCap.Round, LineJoin.Round, 4f, dash);
        /// <summary>Butt caps + miter joins (the SVG / PDF default).</summary>
        public static StrokeStyle Sharp(float width, float miterLimit = 4f) => new StrokeStyle(width, LineCap.Butt, LineJoin.Miter, miterLimit);
        /// <summary>Square caps + bevel joins.</summary>
        public static StrokeStyle Square(float width) => new StrokeStyle(width, LineCap.Square, LineJoin.Bevel);
        /// <summary>Dotted: dots of the stroke width spaced <paramref name="gap"/> apart (round caps on zero-length dashes).</summary>
        public static StrokeStyle Dotted(float width, float gap = -1) => new StrokeStyle(width, LineCap.Round, LineJoin.Round, 4f, new[] { 0f, gap < 0 ? width * 2 : gap + width });
    }

    // ------------------------------------------------------------------------
    // Proper stroking of polylines / paths: caps (butt / round / square), joins (miter with limit /
    // round / bevel) and dash patterns. The whole stroke - every segment quad, join wedge and cap -
    // is emitted as ONE set of positively oriented contours and rasterised in a single DRAW_POLY
    // call (non-zero winding = union), so with alpha ops each pixel is blended exactly once, there
    // are no seams between pieces and no double-blended overlaps at corners.
    //
    // DrawPolyline / DrawWideLine / DrawPath (Sprite.Shapes.cs / Sprite.Curves.cs) keep their
    // original behaviour (round joins implied for width > 2, butt / square / round caps by flag);
    // the Stroke* methods here are the full-featured versions.
    // ------------------------------------------------------------------------
    internal unsafe partial class Sprite
    {
        [ThreadStatic] private static List<PointF>? tStrokePts, tStrokeOut, tDashPts;
        [ThreadStatic] private static List<(int start, int count, bool closed)>? tStrokeSubs, tDashSubs;
        [ThreadStatic] private static List<int>? tStrokeCnt;

        /// <summary>
        /// Strokes a polyline with caps / joins / dashes. Width, colour and op as in <see cref="DrawPolyline(ReadOnlySpan{PointF}, int, float, bool, bool, SR2D.LineOp, bool, int)"/>;
        /// <paramref name="Closed"/> joins the last point back to the first (no caps then).
        /// </summary>
        public void StrokePolyline(ReadOnlySpan<PointF> Points, int c, StrokeStyle Style, bool AA = true, bool Closed = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            if (Points.Length == 0 || meRight <= meLeft || meBottom <= meTop) return;
            var pts = tStrokePts ??= new List<PointF>(256); pts.Clear();
            var subs = tStrokeSubs ??= new List<(int, int, bool)>(8); subs.Clear();
            foreach (var p in Points) pts.Add(p);
            subs.Add((0, Points.Length, Closed && Points.Length > 2));
            StrokeFlattened(pts, subs, c, Style, AA, Op, BlendFactor);
        }
        /// <summary>Strokes a polyline; shorthand for a plain width with the given cap / join.</summary>
        public void StrokePolyline(ReadOnlySpan<PointF> Points, int c, float Width, LineCap Cap = LineCap.Butt, LineJoin Join = LineJoin.Miter, bool AA = true, bool Closed = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
            => StrokePolyline(Points, c, new StrokeStyle(Width, Cap, Join), AA, Closed, Op, BlendFactor);
        /// <summary>Closed polygon outline with proper joins.</summary>
        public void StrokePolygon(ReadOnlySpan<PointF> Points, int c, StrokeStyle Style, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
            => StrokePolyline(Points, c, Style, AA, true, Op, BlendFactor);
        /// <summary>A single line with caps (and a dash pattern if the style has one).</summary>
        public void StrokeLine(float x0, float y0, float x1, float y1, int c, StrokeStyle Style, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            Span<PointF> p = stackalloc PointF[2]; p[0] = new PointF(x0, y0); p[1] = new PointF(x1, y1);
            StrokePolyline(p, c, Style, AA, false, Op, BlendFactor);
        }
        public void StrokeLine(float x0, float y0, float x1, float y1, int c, float Width, LineCap Cap = LineCap.Butt, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
            => StrokeLine(x0, y0, x1, y1, c, new StrokeStyle(Width, Cap), AA, Op, BlendFactor);
        /// <summary>
        /// Strokes a <see cref="PathBuilder"/> (curves flattened at <see cref="CurveTolerance"/>): every sub-path gets caps at
        /// open ends, joins at every corner including the seam of closed sub-paths, and the dash pattern runs along each sub-path.
        /// </summary>
        public void StrokePath(PathBuilder Path, int c, StrokeStyle Style, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            if (meRight <= meLeft || meBottom <= meTop) return;
            var pts = tStrokePts ??= new List<PointF>(256); pts.Clear();
            var subs = tStrokeSubs ??= new List<(int, int, bool)>(8); subs.Clear();
            foreach (var sp in Path.Flatten(pts)) if (sp.count >= 1) subs.Add(sp);
            StrokeFlattened(pts, subs, c, Style, AA, Op, BlendFactor);
        }
        public void StrokePath(PathBuilder Path, int c, float Width, LineCap Cap = LineCap.Butt, LineJoin Join = LineJoin.Miter, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
            => StrokePath(Path, c, new StrokeStyle(Width, Cap, Join), AA, Op, BlendFactor);
        /// <summary>Rectangle outline centred on the rectangle edge with proper corners (miter = sharp, round = rounded outer corners).</summary>
        public void StrokeRect(float x, float y, float w, float h, int c, StrokeStyle Style, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            Span<PointF> p = stackalloc PointF[4]; p[0] = new PointF(x, y); p[1] = new PointF(x + w, y); p[2] = new PointF(x + w, y + h); p[3] = new PointF(x, y + h);
            StrokePolyline(p, c, Style, AA, true, Op, BlendFactor);
        }
        public void StrokeRect(RectangleF r, int c, StrokeStyle Style, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128) => StrokeRect(r.X, r.Y, r.Width, r.Height, c, Style, AA, Op, BlendFactor);

        /// <summary>
        /// Builds the outline polygon(s) of a stroke without drawing them - the contours are positively oriented and meant to
        /// be filled together under non-zero winding (<see cref="FillPolygons"/>), or handed to a gradient fill / a mask.
        /// Returns the number of contours; <paramref name="outPoints"/> / <paramref name="outCounts"/> are appended to.
        /// </summary>
        public static int StrokeOutline(ReadOnlySpan<PointF> Points, bool Closed, StrokeStyle Style, List<PointF> outPoints, List<int> outCounts)
        {
            var pts = new List<PointF>(Points.Length); foreach (var p in Points) pts.Add(p);
            var subs = new List<(int, int, bool)> { (0, Points.Length, Closed && Points.Length > 2) };
            int before = outCounts.Count;
            BuildStroke(pts, subs, Style, outPoints, outCounts);
            return outCounts.Count - before;
        }
        public static int StrokeOutline(PathBuilder Path, StrokeStyle Style, List<PointF> outPoints, List<int> outCounts)
        {
            var pts = new List<PointF>(256); var subs = new List<(int, int, bool)>();
            foreach (var sp in Path.Flatten(pts)) if (sp.count >= 1) subs.Add(sp);
            int before = outCounts.Count;
            BuildStroke(pts, subs, Style, outPoints, outCounts);
            return outCounts.Count - before;
        }

        // ------------------------------------------------------------ implementation
        internal void StrokeFlattened(List<PointF> pts, List<(int start, int count, bool closed)> subs, int c, StrokeStyle style, bool aa, SR2D.LineOp op, int k)
        {
            if (subs.Count == 0) return;
            var o = tStrokeOut ??= new List<PointF>(1024); o.Clear();
            var oc = tStrokeCnt ??= new List<int>(64); oc.Clear();
            BuildStroke(pts, subs, style, o, oc);
            if (oc.Count == 0) return;
            var span = CollectionsMarshal.AsSpan(o);
            var g = Geo.Get(span.Length, oc.Count);
            for (int i = 0; i < span.Length; i++) g.Pt(span[i].X, span[i].Y);
            for (int i = 0; i < oc.Count; i++) g.Cnt[g.C++] = oc[i];
            Poly(ref g, c, op, aa, k);
        }

        /// <summary>The stroker proper. Emits positively oriented contours (union under non-zero winding).</summary>
        private static void BuildStroke(List<PointF> pts, List<(int start, int count, bool closed)> subs, StrokeStyle s, List<PointF> o, List<int> oCnt)
        {
            float width = MathF.Max(0.05f, s.Width), hw = width / 2;
            if (s.Dash != null && s.Dash.Length > 0 && SkAnyPositive(s.Dash))
            {
                var dp = tDashPts ??= new List<PointF>(512); dp.Clear();
                var ds = tDashSubs ??= new List<(int, int, bool)>(64); ds.Clear();
                if (SkApplyDash(pts, subs, s.Dash, s.DashOffset, dp, ds)) { pts = dp; subs = ds; }
            }
            var P = new List<PointF>(64);
            foreach (var (start, count, closedIn) in subs)
            {
                if (count <= 0) continue;
                // drop duplicate consecutive points
                P.Clear(); P.Add(pts[start]);
                for (int i = 1; i < count; i++) if (!SkSame(P[^1], pts[start + i])) P.Add(pts[start + i]);
                bool closed = closedIn;
                if (closed && P.Count > 2 && SkSame(P[0], P[^1])) P.RemoveAt(P.Count - 1);
                if (P.Count == 1)
                {   // a dot: round cap = disc, square cap = square, butt = nothing (zero-length dashes with round caps = dotted lines)
                    var p = P[0];
                    if (s.Cap == LineCap.Round) SkDisc(p.X, p.Y, hw, o, oCnt);
                    else if (s.Cap == LineCap.Square) { int b = o.Count; o.Add(new PointF(p.X - hw, p.Y - hw)); o.Add(new PointF(p.X + hw, p.Y - hw)); o.Add(new PointF(p.X + hw, p.Y + hw)); o.Add(new PointF(p.X - hw, p.Y + hw)); SkEmit(o, oCnt, b); }
                    continue;
                }
                if (P.Count == 2) closed = false;
                int n = P.Count, segs = closed ? n : n - 1;
                // segment bodies
                for (int i = 0; i < segs; i++)
                {
                    PointF a = P[i], b = P[(i + 1) % n];
                    float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy); if (len < 1e-9f) continue;
                    float ux = dx / len, uy = dy / len, nx = -uy * hw, ny = ux * hw;
                    float ea = 0, eb = 0;
                    if (!closed && s.Cap == LineCap.Square) { if (i == 0) ea = hw; if (i == segs - 1) eb = hw; }
                    float ax = a.X - ux * ea, ay = a.Y - uy * ea, bx = b.X + ux * eb, by = b.Y + uy * eb;
                    int st = o.Count;
                    o.Add(new PointF(ax + nx, ay + ny)); o.Add(new PointF(bx + nx, by + ny)); o.Add(new PointF(bx - nx, by - ny)); o.Add(new PointF(ax - nx, ay - ny));
                    SkEmit(o, oCnt, st);
                }
                // round caps
                if (!closed && s.Cap == LineCap.Round) { HalfSkDisc(P[0], P[1], hw, o, oCnt); HalfSkDisc(P[n - 1], P[n - 2], hw, o, oCnt); }
                // joins
                int first = closed ? 0 : 1, last = closed ? n - 1 : n - 2;
                for (int i = first; i <= last; i++)
                {
                    PointF pp = P[(i - 1 + n) % n], pc = P[i], pn = P[(i + 1) % n];
                    float d0x = pc.X - pp.X, d0y = pc.Y - pp.Y, d1x = pn.X - pc.X, d1y = pn.Y - pc.Y;
                    float l0 = MathF.Sqrt(d0x * d0x + d0y * d0y), l1 = MathF.Sqrt(d1x * d1x + d1y * d1y);
                    if (l0 < 1e-9f || l1 < 1e-9f) continue;
                    d0x /= l0; d0y /= l0; d1x /= l1; d1y /= l1;
                    float cross = d0x * d1y - d0y * d1x, dot = d0x * d1x + d0y * d1y;
                    float th = MathF.Atan2(MathF.Abs(cross), dot);              // turn angle 0..pi
                    if (th < 1e-4f) continue;
                    // the uncovered outer wedge has area hw^2 (th - sin th) / 2 and a miter adds hw (1 / cos(th/2) - 1) of
                    // length: skip joins that would not change a pixel (the tiny turns of a flattened curve)
                    if (hw * hw * (th - MathF.Sin(th)) * 0.5f < 0.002f && (s.Join != LineJoin.Miter || hw * (1 / MathF.Cos(th / 2) - 1) < 0.01f)) continue;
                    float sgn = cross < 0 ? 1f : -1f;                            // outer side of the turn
                    float n0x = -d0y * hw * sgn, n0y = d0x * hw * sgn, n1x = -d1y * hw * sgn, n1y = d1x * hw * sgn;
                    int st = o.Count;
                    o.Add(pc); o.Add(new PointF(pc.X + n0x, pc.Y + n0y));
                    if (s.Join == LineJoin.Miter)
                    {   // miter length / width = 1 / sin(phi / 2), phi = pi - th (the interior angle)
                        float phi = MathF.PI - th, ratio = 1 / MathF.Max(1e-6f, MathF.Sin(phi / 2));
                        if (ratio <= MathF.Max(1f, s.MiterLimit))
                        {
                            float mx = n0x + n1x, my = n0y + n1y, ml = MathF.Sqrt(mx * mx + my * my);
                            if (ml > 1e-9f) { float L = hw / MathF.Max(1e-6f, MathF.Cos(th / 2)); o.Add(new PointF(pc.X + mx / ml * L, pc.Y + my / ml * L)); }
                        }
                    }
                    else if (s.Join == LineJoin.Round)
                    {
                        float step = 2 * MathF.Acos(MathF.Max(-1f, 1 - 0.1f / MathF.Max(hw, 0.1f)));
                        int kk = Math.Clamp((int)MathF.Ceiling(th / MathF.Max(step, 1e-3f)), 1, 128);
                        float a = th / kk * (cross < 0 ? -1f : 1f), ca = MathF.Cos(a), sa = MathF.Sin(a), x = n0x, y = n0y;
                        for (int j = 1; j < kk; j++) { float t = x * ca - y * sa; y = x * sa + y * ca; x = t; o.Add(new PointF(pc.X + x, pc.Y + y)); }
                    }
                    o.Add(new PointF(pc.X + n1x, pc.Y + n1y));
                    SkEmit(o, oCnt, st);
                }
            }
        }
        static bool SkAnyPositive(float[] d) { foreach (var v in d) if (v > 0) return true; return false; }
        static bool SkSame(PointF a, PointF b) => MathF.Abs(a.X - b.X) < 1e-6f && MathF.Abs(a.Y - b.Y) < 1e-6f;
        static int SkArcSteps(float r, float angle) => r <= 1.5f ? Math.Max(2, (int)MathF.Ceiling(angle / (MathF.PI / 4))) : Math.Clamp((int)MathF.Ceiling(angle / MathF.Max(MathF.Acos(MathF.Max(-1f, 1f - 0.1f / r)) * 2, 1e-3f)), 2, 256);
        static void SkDisc(float cx, float cy, float r, List<PointF> o, List<int> oCnt)
        {
            int n = Math.Max(8, SkArcSteps(r, MathF.PI * 2));
            int st = o.Count; float step = MathF.PI * 2 / n;
            for (int i = 0; i < n; i++) o.Add(new PointF(cx + r * MathF.Cos(i * step), cy + r * MathF.Sin(i * step)));
            SkEmit(o, oCnt, st);
        }
        /// <summary>Half disc at <paramref name="end"/>, facing away from <paramref name="towards"/> (a round cap): only the outside half, so the join with the segment quad is seamless.</summary>
        static void HalfSkDisc(PointF end, PointF towards, float r, List<PointF> o, List<int> oCnt)
        {
            float dx = end.X - towards.X, dy = end.Y - towards.Y, len = MathF.Sqrt(dx * dx + dy * dy); if (len < 1e-9f) { SkDisc(end.X, end.Y, r, o, oCnt); return; }
            dx /= len; dy /= len;
            float baseAng = MathF.Atan2(dy, dx) - MathF.PI / 2;
            int n = Math.Max(4, SkArcSteps(r, MathF.PI));
            int st = o.Count;
            for (int i = 0; i <= n; i++) { float a = baseAng + MathF.PI * i / n; o.Add(new PointF(end.X + r * MathF.Cos(a), end.Y + r * MathF.Sin(a))); }
            // the chord closes through the end point's diameter, which lies inside the segment quad: overlap, not a gap
            SkEmit(o, oCnt, st);
        }
        /// <summary>Closes the contour started at <paramref name="start"/> with a positive orientation (all stroke pieces share one winding -> union).</summary>
        static void SkEmit(List<PointF> o, List<int> oCnt, int start)
        {
            int n = o.Count - start; if (n < 3) { o.RemoveRange(start, n); return; }
            double a = 0; for (int i = 0, j = n - 1; i < n; j = i++) a += (double)o[start + j].X * o[start + i].Y - (double)o[start + i].X * o[start + j].Y;
            if (a < 0) o.Reverse(start, n);
            oCnt.Add(n);
        }

        const double SkMaxDashPieces = 200_000;
        /// <summary>Cuts the polylines into dashes. Returns false (leave the input alone) for degenerate patterns or absurd piece counts.</summary>
        static bool SkApplyDash(List<PointF> pts, List<(int start, int count, bool closed)> subs, float[] dash, float offset, List<PointF> o, List<(int, int, bool)> osubs)
        {
            float total = 0; foreach (var v in dash) { if (float.IsNaN(v) || float.IsInfinity(v)) return false; total += MathF.Max(0, v); }
            if (!(total > 0) || float.IsNaN(offset) || float.IsInfinity(offset)) return false;
            if ((dash.Length & 1) != 0) { var d2 = new float[dash.Length * 2]; dash.CopyTo(d2, 0); dash.CopyTo(d2, dash.Length); dash = d2; total *= 2; }   // odd count repeats (SVG rule)
            double pathLen = 0;
            foreach (var (start, count, closed) in subs)
            {
                int n = count + (closed ? 1 : 0);
                for (int i = 0; i < n - 1; i++) { PointF a = pts[start + i], b = pts[start + (i + 1) % count]; pathLen += Math.Sqrt((double)(b.X - a.X) * (b.X - a.X) + (double)(b.Y - a.Y) * (b.Y - a.Y)); }
            }
            if (double.IsNaN(pathLen) || pathLen / total * dash.Length > SkMaxDashPieces) return false;
            foreach (var (start, count, closed) in subs)
            {
                int n = count + (closed ? 1 : 0);
                int di = 0; float rem = MathF.Max(0, dash[0]); bool on = true;
                float off = offset % total; if (off < 0) off += total;
                int guard = 0;
                while (off > 0 && guard++ < 4 * dash.Length) { if (off >= rem) { off -= rem; di = (di + 1) % dash.Length; rem = MathF.Max(0, dash[di]); on = !on; } else { rem -= off; off = 0; } }
                int segStart = -1;
                if (on) { segStart = o.Count; o.Add(pts[start]); }
                else if (rem <= 0) { on = true; di = (di + 1) % dash.Length; rem = MathF.Max(0, dash[di]); segStart = o.Count; o.Add(pts[start]); }
                for (int i = 0; i < n - 1; i++)
                {
                    PointF a = pts[start + i], b = pts[start + (i + 1) % count];
                    float dx = b.X - a.X, dy = b.Y - a.Y, segLen = MathF.Sqrt(dx * dx + dy * dy), t = 0;
                    if (segLen <= 0) continue;
                    while (segLen - t > rem)
                    {
                        t += rem; var p = new PointF(a.X + dx * t / segLen, a.Y + dy * t / segLen);
                        if (on) { o.Add(p); int c = o.Count - segStart; if (c >= 1) osubs.Add((segStart, c, false)); segStart = -1; }
                        else { segStart = o.Count; o.Add(p); }
                        on = !on; di = (di + 1) % dash.Length; rem = MathF.Max(0, dash[di]);
                        if (rem == 0 && on) { /* zero-length dash: a dot (round / square cap draws it) */ }
                    }
                    rem -= segLen - t;
                    if (on) o.Add(b);
                }
                if (on && segStart >= 0) { int c = o.Count - segStart; if (c >= 1) osubs.Add((segStart, c, false)); }
            }
            return true;
        }
    }
}
