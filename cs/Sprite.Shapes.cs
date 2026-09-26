using System;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------
    // Vector shapes for Sprite: polylines / polygons with width, rectangles,
    // ellipses, arrows and corner brackets — all with optional anti-aliasing and
    // every LineOp (Set / Xor / AlphaBlend / Blend / Add / Max / Min / AlphaOver).
    //
    // Why a partial class (and not an extension class / a subclass):
    //   * every method here touches pBuf / meWidth / the lock rect directly, exactly
    //     like the methods in Sprite.cs — no property calls, no bounds re-checks, no
    //     wrapper object. An extension class would need public accessors for the
    //     buffer and the clip rect; a subclass would force everyone to create the
    //     subclass. The compiler merges partial files into ONE type: there is zero
    //     run-time difference, it is purely a source-organisation choice.
    //   * Everything here ends in ONE native call per shape (DRAW_POLY for filled /
    //     wide / anti-aliased shapes, DRAW_LINE2 for 1-px non-AA outlines), so the
    //     managed part is only geometry (a few dozen floats), the rasterising is SIMD.
    //
    // Coordinate conventions (same as DrawLine / DrawLine2):
    //   * integer coordinates are pixel centres; outlines are centred on the path.
    //   * FillRect(x, y, w, h) covers exactly the w*h pixels starting at (x, y);
    //     DrawRect / DrawBracket draw their border INSIDE that rectangle, so a
    //     1-px DrawRect around FillRect(x,y,w,h) is the outermost ring of it.
    //   * a shape built from several parts (thick polyline, arrow, bracket) is
    //     rasterised as ONE union: with alpha ops every pixel is blended exactly
    //     once, no seams and no double-blended joints.
    // ------------------------------------------------------------------------
    internal unsafe partial class Sprite
    {
        /// <summary>Anti-aliasing quality for the shape methods: 4 (default, 4 sub-scanlines +
        /// exact horizontal coverage) or 16 (slower, for very flat edges / large gradients).</summary>
        public static int AaQuality = 4;

        // ------------------------------------------------------------ scratch
        // Per-thread geometry buffers (DrawParallel renders on several threads).
        [ThreadStatic] private static float[]? tXY;
        [ThreadStatic] private static int[]? tCnt;

        private struct Geo
        {
            public float[] XY; public int[] Cnt; public int N, C;   // N floats used, C contours

            public static Geo Get(int maxPts, int maxContours)
            {
                Geo g;
                if (tXY == null || tXY.Length < maxPts * 2) tXY = new float[Math.Max(maxPts * 2, 512)];
                if (tCnt == null || tCnt.Length < maxContours) tCnt = new int[Math.Max(maxContours, 32)];
                g.XY = tXY; g.Cnt = tCnt; g.N = 0; g.C = 0;
                return g;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Pt(float x, float y) { XY[N++] = x + 0.5f; XY[N++] = y + 0.5f; }   // centre -> area coords
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void PtArea(float x, float y) { XY[N++] = x; XY[N++] = y; }            // already area coords
            /// <summary>Close the contour started at <paramref name="start"/>, forcing a positive
            /// signed area so that all generated contours share one orientation: under non-zero
            /// winding equal orientations UNION, opposite ones would cancel (holes at joins).</summary>
            public void Close(int start)
            {
                int n = (N - start) / 2; Cnt[C++] = n;
                float a = 0; var xy = XY;
                for (int i = 0, j = n - 1; i < n; j = i++) a += xy[start + j * 2] * xy[start + i * 2 + 1] - xy[start + i * 2] * xy[start + j * 2 + 1];
                if (a < 0)
                    for (int i = 0, j = n - 1; i < j; i++, j--)
                    {
                        (xy[start + i * 2], xy[start + j * 2]) = (xy[start + j * 2], xy[start + i * 2]);
                        (xy[start + i * 2 + 1], xy[start + j * 2 + 1]) = (xy[start + j * 2 + 1], xy[start + i * 2 + 1]);
                    }
            }
            /// <summary>Close without orientation fix-up (deliberately reversed contours = holes).</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CloseRaw(int start) { Cnt[C++] = (N - start) / 2; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int PolyFlags(bool aa) => aa ? (AaQuality >= 16 ? 5 : 1) : 0;

        private void Poly(ref Geo g, int c, SR2D.LineOp op, bool aa, int k)
        {
            if (g.C == 0 || meRight <= meLeft || meBottom <= meTop) return;
            fixed (float* xy = g.XY) fixed (int* cnt = g.Cnt)
                SR2D.Native.DrawPoly(pBuf, meWidth, meLeft, meTop, meRight, meBottom, xy, cnt, g.C, c, OpWord(op), k, PolyFlags(aa));
        }

        // ------------------------------------------------------------ polygons
        /// <summary>
        /// Filled polygon. <paramref name="AA"/> = anti-aliased edges; <paramref name="EvenOdd"/> selects
        /// the even-odd fill rule (self-intersecting stars get holes) instead of non-zero winding.
        /// <paramref name="BlendFactor"/> is only used by <see cref="SR2D.LineOp.Blend"/>.
        /// </summary>
        public void FillPolygon(ReadOnlySpan<PointF> Points, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = false, bool EvenOdd = false, int BlendFactor = 128)
        {
            int n = Points.Length; if (n < 3 || meRight <= meLeft || meBottom <= meTop) return;
            var g = Geo.Get(n, 1);
            for (int i = 0; i < n; i++) g.Pt(Points[i].X, Points[i].Y);
            g.CloseRaw(0);
            fixed (float* xy = g.XY) fixed (int* cnt = g.Cnt)
                SR2D.Native.DrawPoly(pBuf, meWidth, meLeft, meTop, meRight, meBottom, xy, cnt, 1, c, OpWord(Op), BlendFactor, PolyFlags(AA) | (EvenOdd ? 2 : 0));
        }

        /// <summary>Several contours filled in one call (non-zero winding → union; a reversed inner
        /// contour makes a hole). <paramref name="Counts"/> holds the vertex count of each contour.</summary>
        public void FillPolygons(ReadOnlySpan<PointF> Points, ReadOnlySpan<int> Counts, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = false, bool EvenOdd = false, int BlendFactor = 128)
        {
            int n = Points.Length; if (n < 3 || Counts.Length == 0 || meRight <= meLeft || meBottom <= meTop) return;
            var g = Geo.Get(n, Counts.Length);
            for (int i = 0; i < n; i++) g.Pt(Points[i].X, Points[i].Y);
            for (int i = 0; i < Counts.Length; i++) g.Cnt[g.C++] = Counts[i];
            fixed (float* xy = g.XY) fixed (int* cnt = g.Cnt)
                SR2D.Native.DrawPoly(pBuf, meWidth, meLeft, meTop, meRight, meBottom, xy, cnt, g.C, c, OpWord(Op), BlendFactor, PolyFlags(AA) | (EvenOdd ? 2 : 0));
        }

        /// <summary>Polygon outline (closed polyline) with the given width.</summary>
        public void DrawPolygon(ReadOnlySpan<PointF> Points, int c, float Width = 1f, bool AA = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
            => DrawPolyline(Points, c, Width, AA, true, Op, false, BlendFactor);

        /// <summary>
        /// Polyline with width and optional anti-aliasing.
        /// Width ≤ 1 without AA → 1-px hairline (DRAW_LINE2, each vertex pixel lit once, so XOR
        /// works). Otherwise the stroke is built as one polygon union (segments + round joins)
        /// and filled by DRAW_POLY. Caps are square (flush with the end points, like the hairline);
        /// <paramref name="RoundCaps"/> puts half discs on open ends.
        /// </summary>
        public void DrawPolyline(ReadOnlySpan<PointF> Points, int c, float Width = 1f, bool AA = false, bool Closed = false,
                                 SR2D.LineOp Op = SR2D.LineOp.Set, bool RoundCaps = false, int BlendFactor = 128)
        {
            int n = Points.Length;
            if (n < 2 || meRight <= meLeft || meBottom <= meTop) return;
            if (n == 2) Closed = false;
            if (Width <= 1f && !AA)
            {
                int segs = Closed ? n : n - 1;
                int op = OpWord(Op);
                for (int i = 0; i < segs; i++)
                {
                    PointF a = Points[i], b = Points[(i + 1) % n];
                    // skip the end pixel of every segment except the last open one (the next segment draws it)
                    int f = (Closed || i < segs - 1) ? op | 0x100 : op;
                    SR2D.Native.DrawLine2(pBuf, meWidth, meLeft, meTop, meRight, meBottom, a.X, a.Y, b.X, b.Y, c, f, BlendFactor, 0, 0, 0);
                }
                return;
            }
            var g = Geo.Get(n * 4 + 2 * 300 + 64, n * 2 + 4);     // quads + 2 cap discs; wedges grow on demand
            StrokePath(ref g, Points, Width, Closed, RoundCaps);
            Poly(ref g, c, Op, AA, BlendFactor);
        }

        /// <summary>Single line with width / AA (see <see cref="DrawPolyline"/>). Named differently from
        /// <see cref="DrawLine"/> so an integer literal in the 6th argument can never silently switch
        /// between "dot step" and "width".</summary>
        public void DrawWideLine(float x0, float y0, float x1, float y1, int c, float Width = 1f, bool AA = false, SR2D.LineOp Op = SR2D.LineOp.Set, bool RoundCaps = false, int BlendFactor = 128)
        {
            Span<PointF> p = stackalloc PointF[2];
            p[0] = new PointF(x0, y0); p[1] = new PointF(x1, y1);
            DrawPolyline(p, c, Width, AA, false, Op, RoundCaps, BlendFactor);
        }

        // Stroke geometry: one quad per segment; round joins (pie wedges, only where visible)
        // for widths > 2; square caps (segment extended by w/2) or round caps. All appended as
        // separate contours -> union.
        private static void StrokePath(ref Geo g, ReadOnlySpan<PointF> P, float w, bool closed, bool roundCaps)
        {
            int n = P.Length;
            float hw = w * 0.5f;
            bool joins = w > 2f;
            int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                PointF a = P[i], b = P[(i + 1) % n];
                float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6f) { if (segs == 1) { dx = 1; dy = 0; len = 1; } else continue; }
                float ux = dx / len, uy = dy / len;              // unit direction
                float nx = -uy * hw, ny = ux * hw;                // half-width normal
                // extend ends: square caps at open ends; at joins only when no disc is added
                float ea = 0, eb = 0;
                bool aOpen = !closed && i == 0, bOpen = !closed && i == segs - 1;
                if (aOpen) ea = roundCaps ? 0 : hw; else if (!joins) ea = hw;
                if (bOpen) eb = roundCaps ? 0 : hw; else if (!joins) eb = hw;
                float ax = a.X - ux * ea, ay = a.Y - uy * ea, bx = b.X + ux * eb, by = b.Y + uy * eb;
                int s = g.N;
                g.Pt(ax + nx, ay + ny); g.Pt(bx + nx, by + ny); g.Pt(bx - nx, by - ny); g.Pt(ax - nx, ay - ny);
                g.Close(s);
            }
            if (roundCaps && !closed) { Disc(ref g, P[0].X, P[0].Y, hw, hw); Disc(ref g, P[n - 1].X, P[n - 1].Y, hw, hw); }
            if (joins)
            {
                // Round joins, but only where they are visible: two adjacent quads leave a wedge
                // of area hw²(θ - sin θ)/2 uncovered on the outer side of a turn by θ. For the
                // tiny turns of a flattened curve that is far below a thousandth of a pixel, so
                // nothing is added; for real corners a pie slice covering just the outer wedge
                // is appended (a handful of vertices instead of a whole disc per vertex).
                int first = closed ? 0 : 1, last = closed ? n - 1 : n - 2;
                for (int i = first; i <= last; i++)
                {
                    PointF pp = P[(i - 1 + n) % n], pc = P[i], pn = P[(i + 1) % n];
                    float d0x = pc.X - pp.X, d0y = pc.Y - pp.Y, d1x = pn.X - pc.X, d1y = pn.Y - pc.Y;
                    float l0 = MathF.Sqrt(d0x * d0x + d0y * d0y), l1 = MathF.Sqrt(d1x * d1x + d1y * d1y);
                    if (l0 < 1e-6f || l1 < 1e-6f) continue;
                    d0x /= l0; d0y /= l0; d1x /= l1; d1y /= l1;
                    float cross = d0x * d1y - d0y * d1x, dot = d0x * d1x + d0y * d1y;
                    float th = MathF.Atan2(MathF.Abs(cross), dot);            // turn angle 0..π
                    if (hw * hw * (th - MathF.Sin(th)) * 0.5f < 0.02f) continue;
                    JoinWedge(ref g, pc.X, pc.Y, hw, d0x, d0y, d1x, d1y, cross, th);
                }
            }
        }

        // Pie slice from the end normal of the incoming segment to the start normal of the
        // outgoing one, on the outer side of the turn (centre = vertex).
        private static void JoinWedge(ref Geo g, float cx, float cy, float hw, float d0x, float d0y, float d1x, float d1y, float cross, float th)
        {
            // outer side: left of the direction if turning right (cross < 0), else right
            float sgn = cross < 0 ? 1f : -1f;
            float n0x = -d0y * hw * sgn, n0y = d0x * hw * sgn;      // normal at the vertex, incoming
            float n1x = -d1y * hw * sgn, n1y = d1x * hw * sgn;      // normal at the vertex, outgoing
            // arc points between n0 and n1: chord error <= 0.12 px
            float step = 2 * MathF.Acos(MathF.Max(-1f, 1 - 0.12f / MathF.Max(hw, 0.12f)));
            int segs = Math.Clamp((int)MathF.Ceiling(th / MathF.Max(step, 1e-3f)), 1, 128);
            if (g.XY.Length < g.N + (segs + 3) * 2) { Array.Resize(ref g.XY, Math.Max(g.XY.Length * 2, g.N + (segs + 3) * 2)); tXY = g.XY; }
            if (g.Cnt.Length < g.C + 1) { Array.Resize(ref g.Cnt, g.Cnt.Length * 2); tCnt = g.Cnt; }
            int s = g.N;
            g.Pt(cx, cy);
            g.Pt(cx + n0x, cy + n0y);
            float a = th / segs * (cross < 0 ? -1f : 1f);          // rotate n0 towards n1
            float ca = MathF.Cos(a), sa = MathF.Sin(a), x = n0x, y = n0y;
            for (int i = 1; i < segs; i++)
            {
                float t = x * ca - y * sa; y = x * sa + y * ca; x = t;
                g.Pt(cx + x, cy + y);
            }
            g.Pt(cx + n1x, cy + n1y);
            g.Close(s);
        }

        // regular polygon approximating a circle/ellipse; vertex count from the chord error
        private static int DiscSegments(float r)
        {
            if (r <= 1.5f) return 6;
            // chord error e = r (1 - cos(pi/n)) <= 0.12 px
            float a = MathF.Acos(MathF.Max(-1f, 1f - 0.12f / r));
            int n = (int)MathF.Ceiling(MathF.PI / MathF.Max(a, 1e-3f));
            return Math.Clamp(n, 8, 256);
        }
        private static void Disc(ref Geo g, float cx, float cy, float rx, float ry)
        {
            int n = DiscSegments(MathF.Max(rx, ry));
            if (g.XY.Length < g.N + n * 2 + 8) { Array.Resize(ref g.XY, Math.Max(g.XY.Length * 2, g.N + n * 2 + 8)); tXY = g.XY; }
            if (g.Cnt.Length < g.C + 1) { Array.Resize(ref g.Cnt, g.Cnt.Length * 2); tCnt = g.Cnt; }
            int s = g.N;
            float step = MathF.PI * 2 / n, ca = MathF.Cos(step), sa = MathF.Sin(step), x = 1, y = 0;
            for (int i = 0; i < n; i++)
            {
                g.Pt(cx + x * rx, cy + y * ry);
                float t = x * ca - y * sa; y = x * sa + y * ca; x = t;
            }
            g.Close(s);
        }
        private static void DiscRev(ref Geo g, float cx, float cy, float rx, float ry)
        {   // reversed orientation -> hole under non-zero winding
            int n = DiscSegments(MathF.Max(rx, ry));
            if (g.XY.Length < g.N + n * 2 + 8) { Array.Resize(ref g.XY, Math.Max(g.XY.Length * 2, g.N + n * 2 + 8)); tXY = g.XY; }
            if (g.Cnt.Length < g.C + 1) { Array.Resize(ref g.Cnt, g.Cnt.Length * 2); tCnt = g.Cnt; }
            int s = g.N;
            float step = MathF.PI * 2 / n, ca = MathF.Cos(step), sa = -MathF.Sin(step), x = 1, y = 0;
            for (int i = 0; i < n; i++)
            {
                g.Pt(cx + x * rx, cy + y * ry);
                float t = x * ca - y * sa; y = x * sa + y * ca; x = t;
            }
            g.CloseRaw(s);
        }

        // ------------------------------------------------------------ rectangles
        /// <summary>Filled rectangle covering pixels [x, x+w) × [y, y+h) (float allowed with AA).</summary>
        public void FillRect(float x, float y, float w, float h, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = false, int BlendFactor = 128)
        {
            if (w <= 0 || h <= 0) return;
            if (!AA && Op == SR2D.LineOp.Set)
            {   // fastest path: plain fill through the existing clipper (pixel-centre rule, same result)
                int L = (int)MathF.Ceiling(x - 0.5f), T = (int)MathF.Ceiling(y - 0.5f);
                int R = (int)MathF.Ceiling(x + w - 0.5f), B = (int)MathF.Ceiling(y + h - 0.5f);
                ClearRect(L, R, T, B, c);
                return;
            }
            var g = Geo.Get(4, 1);
            g.PtArea(x, y); g.PtArea(x + w, y); g.PtArea(x + w, y + h); g.PtArea(x, y + h); g.Close(0);
            Poly(ref g, c, Op, AA, BlendFactor);
        }

        /// <summary>Rectangle border of <paramref name="Width"/> px drawn inside [x, x+w) × [y, y+h).</summary>
        public void DrawRect(float x, float y, float w, float h, int c, float Width = 1f, bool AA = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            if (w <= 0 || h <= 0) return;
            if (Width * 2 >= w || Width * 2 >= h) { FillRect(x, y, w, h, c, Op, AA, BlendFactor); return; }
            if (Width <= 1f && !AA)
            {   // hairline: 4 segments through the outer pixel centres, each corner pixel once
                float x1 = x + w - 1, y1 = y + h - 1;
                Span<PointF> p = stackalloc PointF[4];
                p[0] = new PointF(x, y); p[1] = new PointF(x1, y); p[2] = new PointF(x1, y1); p[3] = new PointF(x, y1);
                DrawPolyline(p, c, 1f, false, true, Op, false, BlendFactor);
                return;
            }
            var g = Geo.Get(8, 2);
            g.PtArea(x, y); g.PtArea(x + w, y); g.PtArea(x + w, y + h); g.PtArea(x, y + h); g.Close(0);
            int s = g.N; float t = Width;
            g.PtArea(x + t, y + t); g.PtArea(x + t, y + h - t); g.PtArea(x + w - t, y + h - t); g.PtArea(x + w - t, y + t); g.CloseRaw(s);   // reversed -> hole
            Poly(ref g, c, Op, AA, BlendFactor);
        }

        /// <summary>Rectangle from a <see cref="Rectangle"/> / <see cref="RectangleF"/> (a Rectangle converts implicitly).</summary>
        public void FillRect(RectangleF r, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = false, int BlendFactor = 128) => FillRect(r.X, r.Y, r.Width, r.Height, c, Op, AA, BlendFactor);
        public void DrawRect(RectangleF r, int c, float Width = 1f, bool AA = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128) => DrawRect(r.X, r.Y, r.Width, r.Height, c, Width, AA, Op, BlendFactor);

        // ------------------------------------------------------------ rounded rectangles
        /// <summary>Filled rectangle [x, x+w) × [y, y+h) with corners rounded by <paramref name="Radius"/> (clamped to half the shorter side; 0 = plain <see cref="FillRect(float, float, float, float, int, SR2D.LineOp, bool, int)"/>).</summary>
        public void FillRoundRect(float x, float y, float w, float h, float Radius, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = true, int BlendFactor = 128)
        {
            if (w <= 0 || h <= 0) return;
            if (Radius <= 0) { FillRect(x, y, w, h, c, Op, AA, BlendFactor); return; }
            FillPath(new PathBuilder().RoundRect(x, y, w, h, Radius), c, Op, AA, false, BlendFactor);
        }
        /// <summary>Rounded rectangle border of <paramref name="Width"/> px centred on the outline of [x, x+w) × [y, y+h).</summary>
        public void DrawRoundRect(float x, float y, float w, float h, float Radius, int c, float Width = 1f, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            if (w <= 0 || h <= 0) return;
            if (Radius <= 0) { DrawRect(x, y, w, h, c, Width, AA, Op, BlendFactor); return; }
            DrawPath(new PathBuilder().RoundRect(x, y, w, h, Radius), c, Width, AA, Op: Op, BlendFactor: BlendFactor);
        }
        public void FillRoundRect(RectangleF r, float Radius, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = true, int BlendFactor = 128) => FillRoundRect(r.X, r.Y, r.Width, r.Height, Radius, c, Op, AA, BlendFactor);
        public void DrawRoundRect(RectangleF r, float Radius, int c, float Width = 1f, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128) => DrawRoundRect(r.X, r.Y, r.Width, r.Height, Radius, c, Width, AA, Op, BlendFactor);

        // ------------------------------------------------------------ ellipses
        /// <summary>Filled ellipse with centre (cx, cy) and radii rx, ry (pixel-centre coordinates).</summary>
        public void FillEllipse(float cx, float cy, float rx, float ry, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = false, int BlendFactor = 128)
        {
            if (rx <= 0 || ry <= 0 || meRight <= meLeft || meBottom <= meTop) return;
            var g = Geo.Get(260, 1);
            Disc(ref g, cx, cy, rx, ry);
            Poly(ref g, c, Op, AA, BlendFactor);
        }
        public void FillCircle(float cx, float cy, float r, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = false, int BlendFactor = 128) => FillEllipse(cx, cy, r, r, c, Op, AA, BlendFactor);

        /// <summary>Ellipse outline centred on the radius (half the width inside, half outside).</summary>
        public void DrawEllipse(float cx, float cy, float rx, float ry, int c, float Width = 1f, bool AA = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            if (rx <= 0 || ry <= 0 || meRight <= meLeft || meBottom <= meTop) return;
            float hw = Width * 0.5f;
            if (rx - hw <= 0.5f || ry - hw <= 0.5f) { FillEllipse(cx, cy, rx + hw, ry + hw, c, Op, AA, BlendFactor); return; }
            if (Width <= 1f && !AA)
            {   // hairline: polyline through the circle points
                int n = DiscSegments(MathF.Max(rx, ry));
                Span<PointF> p = n <= 256 ? stackalloc PointF[n] : new PointF[n];
                float step = MathF.PI * 2 / n;
                for (int i = 0; i < n; i++) { float a = i * step; p[i] = new PointF(cx + MathF.Cos(a) * rx, cy + MathF.Sin(a) * ry); }
                DrawPolyline(p, c, 1f, false, true, Op, false, BlendFactor);
                return;
            }
            var g = Geo.Get(520, 2);
            Disc(ref g, cx, cy, rx + hw, ry + hw);
            DiscRev(ref g, cx, cy, rx - hw, ry - hw);
            Poly(ref g, c, Op, AA, BlendFactor);
        }
        public void DrawCircle(float cx, float cy, float r, int c, float Width = 1f, bool AA = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128) => DrawEllipse(cx, cy, r, r, c, Width, AA, Op, BlendFactor);

        // ------------------------------------------------------------ arrows
        /// <summary>
        /// Arrow from (x0,y0) to the tip (x1,y1). Shaft of <paramref name="Width"/> px; head of
        /// <paramref name="HeadLen"/> × <paramref name="HeadWidth"/> px (defaults scale with the width).
        /// <paramref name="FilledHead"/> = solid triangle, otherwise an open "V" of the same stroke width.
        /// Shaft + head are one union → a translucent arrow has no seam.
        /// </summary>
        public void DrawArrow(float x0, float y0, float x1, float y1, int c, float Width = 1f, float HeadLen = 0, float HeadWidth = 0,
                              bool FilledHead = true, bool AA = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            if (meRight <= meLeft || meBottom <= meTop) return;
            float dx = x1 - x0, dy = y1 - y0, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6f) return;
            float ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
            if (HeadLen <= 0) HeadLen = MathF.Max(8f, Width * 4f);
            if (HeadWidth <= 0) HeadWidth = MathF.Max(6f, Width * 3f);
            if (HeadLen > len) { HeadLen = len; }
            float bxp = x1 - ux * HeadLen, byp = y1 - uy * HeadLen;              // head base centre
            float hwid = HeadWidth * 0.5f;
            float lx = bxp + nx * hwid, ly = byp + ny * hwid, rx = bxp - nx * hwid, ry = byp - ny * hwid;

            if (!FilledHead)
            {   // open V: polyline L -> tip -> R, plus the shaft to the tip
                Span<PointF> v = stackalloc PointF[3];
                v[0] = new PointF(lx, ly); v[1] = new PointF(x1, y1); v[2] = new PointF(rx, ry);
                if (Width <= 1f && !AA)
                {
                    DrawPolyline(v, c, 1f, false, false, Op, false, BlendFactor);
                    SR2D.Native.DrawLine2(pBuf, meWidth, meLeft, meTop, meRight, meBottom, x0, y0, x1, y1, c, OpWord(Op) | 0x100, BlendFactor, 0, 0, 0);
                    return;
                }
                var g2 = Geo.Get(4 * 3 + 5 * 34, 8);
                StrokePath(ref g2, v, Width, false, false);
                Span<PointF> sh = stackalloc PointF[2];
                sh[0] = new PointF(x0, y0); sh[1] = new PointF(x1 - ux * Width * 0.5f, y1 - uy * Width * 0.5f);
                StrokePath(ref g2, sh, Width, false, false);
                Poly(ref g2, c, Op, AA, BlendFactor);
                return;
            }

            // filled head: triangle (L, tip, R); shaft ends at the head base (slightly inside so the union has no gap)
            float shaftLen = len - HeadLen + 0.5f;
            if (Width <= 1f && !AA)
            {
                if (shaftLen > 0) SR2D.Native.DrawLine2(pBuf, meWidth, meLeft, meTop, meRight, meBottom, x0, y0, x0 + ux * shaftLen, y0 + uy * shaftLen, c, OpWord(Op), BlendFactor, 0, 0, 0);
                var g1 = Geo.Get(3, 1);
                g1.Pt(lx, ly); g1.Pt(x1, y1); g1.Pt(rx, ry); g1.Close(0);
                Poly(ref g1, c, Op, false, BlendFactor);
                return;
            }
            var g = Geo.Get(8, 2);
            float hw = Width * 0.5f;
            if (shaftLen > 0)
            {
                float ex = x0 + ux * shaftLen, ey = y0 + uy * shaftLen, sx = x0 - ux * hw, sy = y0 - uy * hw;   // square cap at start
                int s = g.N;
                g.Pt(sx + nx * hw, sy + ny * hw); g.Pt(ex + nx * hw, ey + ny * hw); g.Pt(ex - nx * hw, ey - ny * hw); g.Pt(sx - nx * hw, sy - ny * hw);
                g.Close(s);
            }
            int s2 = g.N;
            g.Pt(lx, ly); g.Pt(x1, y1); g.Pt(rx, ry); g.Close(s2);
            Poly(ref g, c, Op, AA, BlendFactor);
        }

        // ------------------------------------------------------------ brackets
        /// <summary>
        /// Corner marks ("brackets") of a rectangle [x, x+w) × [y, y+h): an L of
        /// <paramref name="ArmLen"/> px per selected corner, <paramref name="Width"/> px thick,
        /// drawn inside the rectangle like <see cref="DrawRect"/>. Typical selection / focus /
        /// viewfinder decoration. One native call for all four corners.
        /// </summary>
        public void DrawBracket(float x, float y, float w, float h, int c, float ArmLen, float Width = 1f, SR2D.Corners Corners = SR2D.Corners.All,
                                bool AA = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            if (w <= 0 || h <= 0 || ArmLen <= 0 || Corners == 0 || meRight <= meLeft || meBottom <= meTop) return;
            float ax = MathF.Min(ArmLen, w), ay = MathF.Min(ArmLen, h);
            if (ax * 2 >= w && ay * 2 >= h && (Corners & SR2D.Corners.All) == SR2D.Corners.All) { DrawRect(x, y, w, h, c, Width, AA, Op, BlendFactor); return; }
            float t = MathF.Min(Width, MathF.Min(w, h) * 0.5f);
            float x1 = x + w, y1 = y + h;

            if (t <= 1f && !AA)
            {   // hairline L per corner through the outer pixel centres
                float px0 = x, py0 = y, px1 = x1 - 1, py1 = y1 - 1;
                if ((Corners & SR2D.Corners.TopLeft) != 0)     HairL(px0 + ax - 1, py0, px0, py0, px0, py0 + ay - 1, c, Op, BlendFactor);
                if ((Corners & SR2D.Corners.TopRight) != 0)    HairL(px1 - ax + 1, py0, px1, py0, px1, py0 + ay - 1, c, Op, BlendFactor);
                if ((Corners & SR2D.Corners.BottomLeft) != 0)  HairL(px0 + ax - 1, py1, px0, py1, px0, py1 - ay + 1, c, Op, BlendFactor);
                if ((Corners & SR2D.Corners.BottomRight) != 0) HairL(px1 - ax + 1, py1, px1, py1, px1, py1 - ay + 1, c, Op, BlendFactor);
                return;
            }
            // each L as a single 6-vertex contour (no overlap, so it is safe for XOR/alpha)
            var g = Geo.Get(24, 4);
            int s;
            if ((Corners & SR2D.Corners.TopLeft) != 0)
            { s = g.N; g.PtArea(x, y); g.PtArea(x + ax, y); g.PtArea(x + ax, y + t); g.PtArea(x + t, y + t); g.PtArea(x + t, y + ay); g.PtArea(x, y + ay); g.Close(s); }
            if ((Corners & SR2D.Corners.TopRight) != 0)
            { s = g.N; g.PtArea(x1 - ax, y); g.PtArea(x1, y); g.PtArea(x1, y + ay); g.PtArea(x1 - t, y + ay); g.PtArea(x1 - t, y + t); g.PtArea(x1 - ax, y + t); g.Close(s); }
            if ((Corners & SR2D.Corners.BottomLeft) != 0)
            { s = g.N; g.PtArea(x, y1 - ay); g.PtArea(x + t, y1 - ay); g.PtArea(x + t, y1 - t); g.PtArea(x + ax, y1 - t); g.PtArea(x + ax, y1); g.PtArea(x, y1); g.Close(s); }
            if ((Corners & SR2D.Corners.BottomRight) != 0)
            { s = g.N; g.PtArea(x1 - t, y1 - ay); g.PtArea(x1, y1 - ay); g.PtArea(x1, y1); g.PtArea(x1 - ax, y1); g.PtArea(x1 - ax, y1 - t); g.PtArea(x1 - t, y1 - t); g.Close(s); }
            Poly(ref g, c, Op, AA, BlendFactor);
        }
        private void HairL(float a0, float b0, float a1, float b1, float a2, float b2, int c, SR2D.LineOp op, int k)
        {
            Span<PointF> q = stackalloc PointF[3];
            q[0] = new PointF(a0, b0); q[1] = new PointF(a1, b1); q[2] = new PointF(a2, b2);
            DrawPolyline(q, c, 1f, false, false, op, false, k);
        }
        public void DrawBracket(RectangleF r, int c, float ArmLen, float Width = 1f, SR2D.Corners Corners = SR2D.Corners.All, bool AA = false, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
            => DrawBracket(r.X, r.Y, r.Width, r.Height, c, ArmLen, Width, Corners, AA, Op, BlendFactor);
    }
}
