using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace Sr2d64CSport
{
    /// <summary>Options for <see cref="VectorImage.Draw(Sprite, Matrix3x2, VectorRenderOptions?)"/>.</summary>
    internal sealed class VectorRenderOptions
    {
        /// <summary>Anti-aliased edges (default true).</summary>
        public bool AA = true;
        /// <summary>Whole-image opacity 0..1.</summary>
        public float Opacity = 1f;
        /// <summary>Composite as premultiplied source-over (for AlphaOver layers). Null = decided by the destination's Op.</summary>
        public bool? Premultiplied { get; set; }
        /// <summary>Curve flattening tolerance in device pixels (null = <see cref="Sprite.CurveTolerance"/>).</summary>
        public float? Tolerance { get; set; }
        /// <summary>Draw strokes (default true) / fills (default true).</summary>
        public bool Strokes = true, Fills = true;
        /// <summary>Thinnest stroke on screen in pixels (hairlines / zero-width PDF lines are widened to this).</summary>
        public float MinStrokeWidth = 0.8f;
        /// <summary>Ignore per-shape clips (faster previews).</summary>
        public bool IgnoreClips { get; set; }
        /// <summary>Replace every paint by this colour (silhouette / shadow rendering); alpha is kept per shape.</summary>
        public int? ForceColor { get; set; }
        public static readonly VectorRenderOptions Default = new VectorRenderOptions();
    }

    internal sealed partial class VectorImage
    {
        /// <summary>Draws the image with the view-box pivot at (x, y), scaled, rotated (degrees, clockwise on screen). Scale 1 = view-box units are pixels.</summary>
        public void Draw(Sprite dst, float x, float y, float scaleX = 1f, float scaleY = 1f, float angleDeg = 0f, VectorRenderOptions? o = null, float pivotX = 0.5f, float pivotY = 0.5f)
            => Draw(dst, PlaceMatrix(x, y, scaleX, scaleY, angleDeg, pivotX, pivotY), o);
        /// <summary>Draws the image top-left at (x, y) at scale 1 (view-box units = pixels).</summary>
        public void Draw(Sprite dst, int x, int y, VectorRenderOptions? o = null) => Draw(dst, Matrix3x2.CreateTranslation(x - ViewBox.X, y - ViewBox.Y), o);
        /// <summary>Draws the image scaled into a rectangle (aspect kept and centred unless <paramref name="keepAspect"/> is false).</summary>
        public void DrawFit(Sprite dst, RectangleF into, bool keepAspect = true, VectorRenderOptions? o = null) => Draw(dst, FitMatrix(into, keepAspect), o);
        public void DrawFit(Sprite dst, VectorRenderOptions? o = null) => DrawFit(dst, new RectangleF(0, 0, dst.Width, dst.Height), true, o);
        /// <summary>Draws every shape through <paramref name="m"/> (image -> destination pixels) inside the destination's lock rect.</summary>
        public void Draw(Sprite dst, Matrix3x2 m, VectorRenderOptions? o = null) => VectorRender.Draw(this, dst, m, o ?? VectorRenderOptions.Default);

        /// <summary>Renders into a new premultiplied sprite (Op = AlphaOver, transparent background) of the given size, the view box fitted (aspect kept).</summary>
        public Sprite Rasterize(int width, int height, int background = 0, bool keepAspect = true, VectorRenderOptions? o = null)
        {
            var s = new Sprite(Math.Max(1, width), Math.Max(1, height), SR2D.Op.AlphaOver);
            s.ClearBuffer(background);
            DrawFit(s, new RectangleF(0, 0, width, height), keepAspect, o);
            return s;
        }
        /// <summary>Renders at <paramref name="scale"/> x the intrinsic size.</summary>
        public Sprite Rasterize(float scale = 1f, int background = 0, VectorRenderOptions? o = null)
            => Rasterize(Math.Max(1, (int)MathF.Ceiling(Width * scale)), Math.Max(1, (int)MathF.Ceiling(Height * scale)), background, true, o);

        /// <summary>Index of the top-most shape whose fill (or stroke body) contains the image-space point; -1 = none.</summary>
        public int HitTest(PointF imagePoint)
        {
            if (VisibleArea.HasValue && !VisibleArea.Value.Contains(imagePoint)) return -1;
            for (int i = Shapes.Count - 1; i >= 0; i--)
            {
                var s = Shapes[i]; if (!s.IsVisible) continue;
                if (VectorRender.HitShape(s, imagePoint)) return i;
            }
            return -1;
        }
    }

    /// <summary>The rasteriser behind <see cref="VectorImage.Draw(Sprite, Matrix3x2, VectorRenderOptions?)"/>: flattening, stroking, gradients, clips.</summary>
    internal static unsafe class VectorRender
    {
        // ------------------------------------------------------------------ scratch (per thread: DrawParallel bands may render concurrently)
        [ThreadStatic] static List<PointF>? tPts, tWork; [ThreadStatic] static List<int>? tCnt; [ThreadStatic] static List<PointF>? tOut; [ThreadStatic] static List<int>? tOutCnt;
        [ThreadStatic] static Sprite? tCov, tClip, tClip2;
        [ThreadStatic] static int[]? tLut;

        static Sprite Scratch(ref Sprite? s, int w, int h)
        {
            if (s == null || s.Width < w || s.Height < h) { s?.Dispose(); s = new Sprite(Math.Max(w, s?.Width ?? 64), Math.Max(h, s?.Height ?? 64), SR2D.Op.Paint); }
            return s;
        }

        public static float MeanScale(Matrix3x2 m) => MathF.Sqrt(MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21));
        public static float MaxScale(Matrix3x2 m) => MathF.Max(MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12), MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22));
        public static RectangleF TransformRect(RectangleF r, Matrix3x2 m)
        {
            var a = Vector2.Transform(new Vector2(r.Left, r.Top), m); var b = Vector2.Transform(new Vector2(r.Right, r.Top), m);
            var c = Vector2.Transform(new Vector2(r.Right, r.Bottom), m); var d = Vector2.Transform(new Vector2(r.Left, r.Bottom), m);
            float l = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), rr = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
            float t = MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)), bb = MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y));
            return RectangleF.FromLTRB(l, t, rr, bb);
        }

        // ------------------------------------------------------------------ flattening
        /// <summary>Flattens a path into polylines (device space when <paramref name="m"/> is given). Returns (start, count, closed) per sub-path.</summary>
        public static List<(int start, int count, bool closed)> Flatten(VectorPath p, Matrix3x2 m, float tol, List<PointF> outPts)
        {
            var subs = new List<(int, int, bool)>();
            int d = 0, start = outPts.Count; bool have = false; float cx = 0, cy = 0;
            var K = p.Kinds; var D = p.Data;
            void End(bool closed) { if (have) { int n = outPts.Count - start; if (n > 0) subs.Add((start, n, closed)); } have = false; }
            Vector2 T(float x, float y) => Vector2.Transform(new Vector2(x, y), m);
            for (int i = 0; i < K.Count; i++)
            {
                switch (K[i])
                {
                    case VectorPath.K.Move:
                        End(false); start = outPts.Count; cx = D[d]; cy = D[d + 1]; d += 2; var v = T(cx, cy); outPts.Add(new PointF(v.X, v.Y)); have = true; break;
                    case VectorPath.K.Line:
                        { float x = D[d], y = D[d + 1]; d += 2; if (!have) { start = outPts.Count; var v0 = T(cx, cy); outPts.Add(new PointF(v0.X, v0.Y)); have = true; } var v1 = T(x, y); outPts.Add(new PointF(v1.X, v1.Y)); cx = x; cy = y; break; }
                    case VectorPath.K.Cubic:
                        {
                            float x1 = D[d], y1 = D[d + 1], x2 = D[d + 2], y2 = D[d + 3], x3 = D[d + 4], y3 = D[d + 5]; d += 6;
                            if (!have) { start = outPts.Count; var v0 = T(cx, cy); outPts.Add(new PointF(v0.X, v0.Y)); have = true; }
                            var p0 = T(cx, cy); var p1 = T(x1, y1); var p2 = T(x2, y2); var p3 = T(x3, y3);
                            CubicTo(p0, p1, p2, p3, tol, outPts); cx = x3; cy = y3; break;
                        }
                    case VectorPath.K.Close:
                        if (have)
                        {   // drop a duplicated closing point
                            int n = outPts.Count - start; if (n > 1) { var f = outPts[start]; var l = outPts[^1]; if (MathF.Abs(f.X - l.X) < 1e-4f && MathF.Abs(f.Y - l.Y) < 1e-4f) outPts.RemoveAt(outPts.Count - 1); }
                            End(true);
                            // a following segment without Move starts at the sub-path start
                            int back = start; // (start is overwritten by the next Move/Line)
                            var s0 = FirstOf(subs, back, outPts); cx = s0.X; cy = s0.Y;
                        }
                        break;
                }
            }
            End(false);
            return subs;
            static PointF FirstOf(List<(int, int, bool)> subs, int start, List<PointF> pts) => pts[start];
        }
        /// <summary>Uniform subdivision with the segment count from the control polygon's second differences (error &lt;= tol).</summary>
        static void CubicTo(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tol, List<PointF> o)
        {
            float ddx = MathF.Max(MathF.Abs(p0.X - 2 * p1.X + p2.X), MathF.Abs(p1.X - 2 * p2.X + p3.X));
            float ddy = MathF.Max(MathF.Abs(p0.Y - 2 * p1.Y + p2.Y), MathF.Abs(p1.Y - 2 * p2.Y + p3.Y));
            float dd = MathF.Sqrt(ddx * ddx + ddy * ddy);
            int n = (int)MathF.Ceiling(MathF.Sqrt(0.75f * dd / MathF.Max(tol, 1e-4f)));
            n = Math.Clamp(n, 1, 256);
            for (int i = 1; i <= n; i++)
            {
                float t = (float)i / n, u = 1 - t, b0 = u * u * u, b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t;
                o.Add(new PointF(b0 * p0.X + b1 * p1.X + b2 * p2.X + b3 * p3.X, b0 * p0.Y + b1 * p1.Y + b2 * p2.Y + b3 * p3.Y));
            }
        }

        // ------------------------------------------------------------------ stroking (local space, then transformed)
        /// <summary>Builds the outline of a stroke as positively oriented contours (union under non-zero winding).</summary>
        public static void Stroke(List<PointF> pts, List<(int start, int count, bool closed)> subs, VectorShape s, float width, List<PointF> o, List<int> oCnt)
        {
            float hw = width / 2;
            var dash = s.Dash != null && s.Dash.Length > 0 && AnyPositive(s.Dash) ? s.Dash : null;
            List<PointF>? dpts = null; List<(int, int, bool)>? dsubs = null;
            if (dash != null) { dpts = new List<PointF>(); dsubs = new List<(int, int, bool)>(); if (ApplyDash(pts, subs, dash, s.DashOffset, dpts, dsubs)) { pts = dpts; subs = dsubs; } }
            foreach (var (start, count, closed) in subs)
            {
                if (count == 1 || (count == 2 && Same(pts[start], pts[start + 1])))
                {   // a dot: round cap -> disc, square cap -> square, butt -> nothing
                    var p = pts[start];
                    if (s.Cap == VectorCap.Round) Disc(p.X, p.Y, hw, o, oCnt);
                    else if (s.Cap == VectorCap.Square) { int b = o.Count; o.Add(new PointF(p.X - hw, p.Y - hw)); o.Add(new PointF(p.X + hw, p.Y - hw)); o.Add(new PointF(p.X + hw, p.Y + hw)); o.Add(new PointF(p.X - hw, p.Y + hw)); Emit(o, oCnt, b); }
                    continue;
                }
                // remove duplicate consecutive points
                var P = new List<PointF>(count); P.Add(pts[start]);
                for (int i = 1; i < count; i++) if (!Same(P[^1], pts[start + i])) P.Add(pts[start + i]);
                bool cl = closed; if (cl && P.Count > 2 && Same(P[0], P[^1])) P.RemoveAt(P.Count - 1);
                if (P.Count < 2) { if (s.Cap == VectorCap.Round) Disc(P[0].X, P[0].Y, hw, o, oCnt); continue; }
                if (P.Count == 2) cl = false;
                int n = P.Count, segs = cl ? n : n - 1;
                for (int i = 0; i < segs; i++)
                {
                    PointF a = P[i], b = P[(i + 1) % n];
                    float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy); if (len < 1e-9f) continue;
                    float ux = dx / len, uy = dy / len, nx = -uy * hw, ny = ux * hw;
                    float ea = 0, eb = 0;
                    if (!cl && s.Cap == VectorCap.Square) { if (i == 0) ea = hw; if (i == segs - 1) eb = hw; }
                    float ax = a.X - ux * ea, ay = a.Y - uy * ea, bx = b.X + ux * eb, by = b.Y + uy * eb;
                    int st = o.Count;
                    o.Add(new PointF(ax + nx, ay + ny)); o.Add(new PointF(bx + nx, by + ny)); o.Add(new PointF(bx - nx, by - ny)); o.Add(new PointF(ax - nx, ay - ny));
                    Emit(o, oCnt, st);
                }
                if (!cl && s.Cap == VectorCap.Round) { Disc(P[0].X, P[0].Y, hw, o, oCnt); Disc(P[n - 1].X, P[n - 1].Y, hw, o, oCnt); }
                // joins
                int first = cl ? 0 : 1, last = cl ? n - 1 : n - 2;
                for (int i = first; i <= last; i++)
                {
                    PointF pp = P[(i - 1 + n) % n], pc = P[i], pn = P[(i + 1) % n];
                    float d0x = pc.X - pp.X, d0y = pc.Y - pp.Y, d1x = pn.X - pc.X, d1y = pn.Y - pc.Y;
                    float l0 = MathF.Sqrt(d0x * d0x + d0y * d0y), l1 = MathF.Sqrt(d1x * d1x + d1y * d1y);
                    if (l0 < 1e-9f || l1 < 1e-9f) continue;
                    d0x /= l0; d0y /= l0; d1x /= l1; d1y /= l1;
                    float cross = d0x * d1y - d0y * d1x, dot = d0x * d1x + d0y * d1y;
                    float th = MathF.Atan2(MathF.Abs(cross), dot);
                    if (th < 1e-4f) continue;
                    if (hw * hw * (th - MathF.Sin(th)) * 0.5f < 0.002f && s.Join != VectorJoin.Miter) continue;   // invisible wedge
                    float sgn = cross < 0 ? 1f : -1f;                      // outer side
                    float n0x = -d0y * hw * sgn, n0y = d0x * hw * sgn, n1x = -d1y * hw * sgn, n1y = d1x * hw * sgn;
                    int st = o.Count;
                    o.Add(pc); o.Add(new PointF(pc.X + n0x, pc.Y + n0y));
                    var join = s.Join;
                    if (join == VectorJoin.Miter)
                    {   // miter length / stroke width = 1 / sin(phi/2), phi = angle between the segments = pi - th
                        float phi = MathF.PI - th; float ratio = 1 / MathF.Max(1e-6f, MathF.Sin(phi / 2));
                        if (ratio <= s.MiterLimit)
                        {   // miter tip = vertex + (n0 + n1) normalised * hw / cos(th/2)
                            float mx = n0x + n1x, my = n0y + n1y, ml = MathF.Sqrt(mx * mx + my * my);
                            if (ml > 1e-9f) { float L = hw / MathF.Max(1e-6f, MathF.Cos(th / 2)); o.Add(new PointF(pc.X + mx / ml * L, pc.Y + my / ml * L)); }
                        }
                    }
                    else if (join == VectorJoin.Round)
                    {
                        float step = 2 * MathF.Acos(MathF.Max(-1f, 1 - 0.1f / MathF.Max(hw, 0.1f)));
                        int k = Math.Clamp((int)MathF.Ceiling(th / MathF.Max(step, 1e-3f)), 1, 128);
                        float a = th / k * (cross < 0 ? -1f : 1f), ca = MathF.Cos(a), sa = MathF.Sin(a), x = n0x, y = n0y;
                        for (int j = 1; j < k; j++) { float t = x * ca - y * sa; y = x * sa + y * ca; x = t; o.Add(new PointF(pc.X + x, pc.Y + y)); }
                    }
                    o.Add(new PointF(pc.X + n1x, pc.Y + n1y));
                    Emit(o, oCnt, st);
                }
            }
        }
        static bool AnyPositive(float[] d) { foreach (var v in d) if (v > 0) return true; return false; }
        static bool Same(PointF a, PointF b) => MathF.Abs(a.X - b.X) < 1e-6f && MathF.Abs(a.Y - b.Y) < 1e-6f;
        static void Disc(float cx, float cy, float r, List<PointF> o, List<int> oCnt)
        {
            int n = r <= 1.5f ? 8 : Math.Clamp((int)MathF.Ceiling(MathF.PI / MathF.Max(MathF.Acos(MathF.Max(-1f, 1f - 0.1f / r)), 1e-3f)), 8, 256);
            int st = o.Count; float step = MathF.PI * 2 / n;
            for (int i = 0; i < n; i++) o.Add(new PointF(cx + r * MathF.Cos(i * step), cy + r * MathF.Sin(i * step)));
            Emit(o, oCnt, st);
        }
        /// <summary>Closes the contour started at <paramref name="start"/> with a positive orientation (all stroke pieces share one winding -> union).</summary>
        static void Emit(List<PointF> o, List<int> oCnt, int start)
        {
            int n = o.Count - start; if (n < 3) { o.RemoveRange(start, n); return; }
            double a = 0; for (int i = 0, j = n - 1; i < n; j = i++) a += (double)o[start + j].X * o[start + i].Y - (double)o[start + i].X * o[start + j].Y;
            if (a < 0) o.Reverse(start, n);
            oCnt.Add(n);
        }
        /// <summary>Max dash pieces per stroke; beyond that (a microscopic pattern on a long path, a damaged file) the stroke is drawn solid, which is what it would look like anyway.</summary>
        const double MaxDashPieces = 200_000;
        static bool ApplyDash(List<PointF> pts, List<(int start, int count, bool closed)> subs, float[] dash, float offset, List<PointF> o, List<(int, int, bool)> osubs)
        {
            float total = 0; foreach (var v in dash) { if (float.IsNaN(v) || float.IsInfinity(v)) return false; total += MathF.Max(0, v); }
            if (!(total > 0) || float.IsNaN(offset) || float.IsInfinity(offset)) return false;
            double pathLen = 0;
            foreach (var (start, count, closed) in subs)
            {
                int n = count + (closed ? 1 : 0);
                for (int i = 0; i < n - 1; i++) { PointF a = pts[start + i], b = pts[start + (i + 1) % count]; pathLen += Math.Sqrt((double)(b.X - a.X) * (b.X - a.X) + (double)(b.Y - a.Y) * (b.Y - a.Y)); }
            }
            if (double.IsNaN(pathLen) || pathLen / total * dash.Length > MaxDashPieces) return false;
            foreach (var (start, count, closed) in subs)
            {
                int n = count + (closed ? 1 : 0);
                // dash state at the start of the sub-path
                int di = 0; float rem = MathF.Max(0, dash[0]); bool on = true;
                float off = offset % total; if (off < 0) off += total;
                while (off > 0) { if (off >= rem) { off -= rem; di = (di + 1) % dash.Length; rem = MathF.Max(0, dash[di]); on = !on; } else { rem -= off; off = 0; } }
                int segStart = -1;
                if (on) { segStart = o.Count; o.Add(pts[start]); }
                for (int i = 0; i < n - 1; i++)
                {
                    PointF a = pts[start + i], b = pts[start + (i + 1) % count];
                    float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy), pos = 0;
                    while (len - pos > rem)
                    {
                        pos += rem; var p = new PointF(a.X + dx * pos / len, a.Y + dy * pos / len);
                        if (on) { o.Add(p); osubs.Add((segStart, o.Count - segStart, false)); segStart = -1; }
                        else { segStart = o.Count; o.Add(p); }
                        on = !on; di = (di + 1) % dash.Length; rem = MathF.Max(1e-6f, dash[di]);
                    }
                    rem -= len - pos;
                    if (on) o.Add(b);
                }
                if (on && segStart >= 0) { int c = o.Count - segStart; if (c >= 2) osubs.Add((segStart, c, false)); else o.RemoveRange(segStart, c); }
            }
            return true;
        }

        // ------------------------------------------------------------------ bounds / hit
        public static RectangleF ShapeBounds(VectorShape s, Matrix3x2 m)
        {
            var t = s.Transform * m;
            var pts = new List<PointF>(); Flatten(s.Path, t, 0.5f, pts);
            if (pts.Count == 0) return RectangleF.Empty;
            float l = float.MaxValue, tp = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (var p in pts) { if (p.X < l) l = p.X; if (p.X > r) r = p.X; if (p.Y < tp) tp = p.Y; if (p.Y > b) b = p.Y; }
            var rc = RectangleF.FromLTRB(l, tp, r, b);
            if (s.Stroke != null) { float w = s.StrokeWidth * MaxScale(t) / 2 * (s.Join == VectorJoin.Miter ? MathF.Max(1, s.MiterLimit) : 1); rc.Inflate(w, w); }
            if (s.Clip != null && !s.Clip.IsRect(out _)) { }   // (a non-rect clip can only shrink it; ignored for a conservative box)
            else if (s.Clip != null && s.Clip.IsRect(out var cr)) rc = RectangleF.Intersect(rc, TransformRect(cr, t));
            return rc;
        }
        public static bool HitShape(VectorShape s, PointF p)
        {
            var pts = new List<PointF>(); var subs = Flatten(s.Path, s.Transform, 0.25f, pts);
            if (s.Fill != null && Inside(pts, subs, p, s.EvenOdd)) return true;
            if (s.Stroke != null)
            {
                var o = new List<PointF>(); var oc = new List<int>();
                Stroke(pts, subs, s, MathF.Max(s.StrokeWidth * MeanScale(s.Transform), 1f), o, oc);
                var ss = new List<(int, int, bool)>(); int st = 0; foreach (var c in oc) { ss.Add((st, c, true)); st += c; }
                return Inside(o, ss, p, false);
            }
            return false;
        }
        static bool Inside(List<PointF> pts, List<(int start, int count, bool closed)> subs, PointF p, bool evenOdd)
        {
            int wn = 0; bool odd = false;
            foreach (var (start, count, _) in subs)
                for (int i = 0, j = count - 1; i < count; j = i++)
                {
                    PointF a = pts[start + j], b = pts[start + i];
                    if ((a.Y <= p.Y) != (b.Y <= p.Y))
                    {
                        float x = a.X + (p.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                        if (x > p.X) { odd = !odd; wn += b.Y > a.Y ? 1 : -1; }
                    }
                }
            return evenOdd ? odd : wn != 0;
        }

        // ------------------------------------------------------------------ drawing
        public static void Draw(VectorImage img, Sprite dst, Matrix3x2 m, VectorRenderOptions o)
        {
            var clipRect = dst.LockRect; if (clipRect.Width <= 0 || clipRect.Height <= 0) return;
            bool premul = o.Premultiplied ?? dst.Premultiplied;
            float tol = o.Tolerance ?? Sprite.CurveTolerance;
            var window = img.VisibleArea ?? (img.ClipViewport ? img.ViewBox : (RectangleF?)null);
            if (window is RectangleF va)
            {   // render-level window: axis-aligned draw = narrower lock rect (free), otherwise a rectangle clip is added to every shape (mask path)
                if (IsAxisAligned(m))
                {
                    var dr = TransformRect(va, m);
                    clipRect = Rectangle.Intersect(clipRect, Rectangle.FromLTRB((int)MathF.Floor(dr.Left + 0.5f), (int)MathF.Floor(dr.Top + 0.5f), (int)MathF.Ceiling(dr.Right - 0.5f), (int)MathF.Ceiling(dr.Bottom - 0.5f)));
                    if (clipRect.Width <= 0 || clipRect.Height <= 0) return;
                }
                else
                {
                    var win = new VectorPath().Rect(va.X, va.Y, va.Width, va.Height);
                    var dr = TransformRect(va, m);
                    clipRect = Rectangle.Intersect(clipRect, Rectangle.FromLTRB((int)MathF.Floor(dr.Left), (int)MathF.Floor(dr.Top), (int)MathF.Ceiling(dr.Right) + 1, (int)MathF.Ceiling(dr.Bottom) + 1));
                    if (clipRect.Width <= 0 || clipRect.Height <= 0) return;
                    foreach (var s in img.Shapes)
                    {
                        if (!s.IsVisible) continue;
                        var sb = ShapeBounds(s, Matrix3x2.Identity);
                        if (!sb.IsEmpty && va.Contains(sb)) { DrawOne(s, dst, m, o, clipRect, premul, tol); continue; }   // wholly inside: no mask needed
                        if (!sb.IsEmpty && !sb.IntersectsWith(va)) continue;                                              // wholly outside: skip
                        // the window in the shape's local space (Clip paths are local, like the path)
                        VectorPath local = win; if (!s.Transform.IsIdentity && Matrix3x2.Invert(s.Transform, out var inv)) local = win.Transformed(inv);
                        var tmp = tWin ??= new VectorShape();
                        CopyInto(s, tmp); tmp.Clip = s.Clip == null || o.IgnoreClips ? new VectorClip(local) : s.Clip.Intersect(local);
                        if (o.IgnoreClips) { var o2 = tWinOpt ??= new VectorRenderOptions(); CopyOpt(o, o2); o2.IgnoreClips = false; DrawOne(tmp, dst, m, o2, clipRect, premul, tol); }
                        else DrawOne(tmp, dst, m, o, clipRect, premul, tol);
                    }
                    return;
                }
            }
            foreach (var s in img.Shapes) DrawOne(s, dst, m, o, clipRect, premul, tol);
        }
        [ThreadStatic] static VectorShape? tWin; [ThreadStatic] static VectorRenderOptions? tWinOpt;
        static void CopyOpt(VectorRenderOptions a, VectorRenderOptions b) { b.AA = a.AA; b.Opacity = a.Opacity; b.Strokes = a.Strokes; b.Fills = a.Fills; b.MinStrokeWidth = a.MinStrokeWidth; b.ForceColor = a.ForceColor; b.Tolerance = a.Tolerance; b.Premultiplied = a.Premultiplied; }
        static void CopyInto(VectorShape from, VectorShape to)
        {
            to.Path = from.Path; to.Fill = from.Fill; to.Stroke = from.Stroke; to.EvenOdd = from.EvenOdd; to.StrokeWidth = from.StrokeWidth; to.Cap = from.Cap; to.Join = from.Join;
            to.MiterLimit = from.MiterLimit; to.Dash = from.Dash; to.DashOffset = from.DashOffset; to.Opacity = from.Opacity; to.Transform = from.Transform; to.Hidden = from.Hidden; to.IsText = from.IsText; to.IsImage = from.IsImage;
        }
        /// <summary>Draws a single shape (the gradient fills of Sprite.Gradient.cs go through here).</summary>
        public static void DrawShape(VectorShape s, Sprite dst, Matrix3x2 m, bool aa)
        {
            var clipRect = dst.LockRect; if (clipRect.Width <= 0 || clipRect.Height <= 0) return;
            var o = aa ? OptAA : OptNoAA;
            DrawOne(s, dst, m, o, clipRect, dst.Premultiplied, Sprite.CurveTolerance);
        }
        static readonly VectorRenderOptions OptAA = new VectorRenderOptions { AA = true }, OptNoAA = new VectorRenderOptions { AA = false };
        static void DrawOne(VectorShape s, Sprite dst, Matrix3x2 m, VectorRenderOptions o, Rectangle clipRect, bool premul, float tol)
        {
            var pts = tPts ??= new List<PointF>(1024); var cnt = tCnt ??= new List<int>(64); var op = tOut ??= new List<PointF>(2048); var opc = tOutCnt ??= new List<int>(256);
            {
                if (!s.IsVisible) return;
                float opacity = s.Opacity * o.Opacity; if (opacity <= 0) return;
                var t = s.Transform * m;
                float scale = MaxScale(t); if (scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale)) return;
                // clip: rectangular clips just narrow the lock rect; anything else goes through the mask path
                Rectangle lr = clipRect; VectorClip? softClip = null;
                if (s.Clip != null && !o.IgnoreClips && s.Clip.Paths.Count > 0)
                {
                    if (s.Clip.IsRect(out var cr) && IsAxisAligned(t))
                    {
                        var dr = TransformRect(cr, t);
                        lr = Rectangle.Intersect(lr, Rectangle.FromLTRB((int)MathF.Floor(dr.Left + 0.5f), (int)MathF.Floor(dr.Top + 0.5f), (int)MathF.Ceiling(dr.Right - 0.5f), (int)MathF.Ceiling(dr.Bottom - 0.5f)));
                        if (lr.Width <= 0 || lr.Height <= 0) return;
                    }
                    else softClip = s.Clip;
                }
                // geometry in device space (fills) - flattened once per shape
                pts.Clear(); cnt.Clear();
                var subs = Flatten(s.Path, t, tol, pts);
                if (pts.Count == 0) return;
                // quick reject by bounds
                float bl = float.MaxValue, bt = float.MaxValue, br = float.MinValue, bb = float.MinValue;
                foreach (var p in pts) { if (p.X < bl) bl = p.X; if (p.X > br) br = p.X; if (p.Y < bt) bt = p.Y; if (p.Y > bb) bb = p.Y; }
                float sw = 0;
                if (s.Stroke != null && o.Strokes) { sw = MathF.Max(s.StrokeWidth * MeanScale(t), o.MinStrokeWidth); float inf = sw * (s.Join == VectorJoin.Miter ? MathF.Max(1, s.MiterLimit) : 1); bl -= inf; bt -= inf; br += inf; bb += inf; }
                if (br < lr.Left - 1 || bl > lr.Right + 1 || bb < lr.Top - 1 || bt > lr.Bottom + 1) return;
                var shapeBox = RectangleF.FromLTRB(bl, bt, br, bb);

                if (s.Fill != null && o.Fills)
                {
                    foreach (var _ in subs) cnt.Add(0);
                    cnt.Clear(); foreach (var (st, c, _) in subs) cnt.Add(c);
                    PaintPolys(dst, lr, pts, cnt, s.Fill, s.EvenOdd, opacity, o, premul, softClip, t, shapeBox, s, false);
                }
                if (s.Stroke != null && o.Strokes)
                {
                    op.Clear(); opc.Clear();
                    if (IsUniform(t))
                    {   // uniform scale / rotation: stroke straight in device space
                        Stroke(pts, subs, s, sw, op, opc);
                    }
                    else
                    {   // squished: stroke in local space (width in local units), then transform the outline
                        var lp = new List<PointF>(); var ls = Flatten(s.Path, Matrix3x2.Identity, tol / scale, lp);
                        float lw = MathF.Max(s.StrokeWidth, o.MinStrokeWidth / MeanScale(t));
                        Stroke(lp, ls, s, lw, op, opc);
                        for (int i = 0; i < op.Count; i++) { var v = Vector2.Transform(new Vector2(op[i].X, op[i].Y), t); op[i] = new PointF(v.X, v.Y); }
                        if (t.M11 * t.M22 - t.M12 * t.M21 < 0) { int st = 0; foreach (var c in opc) { op.Reverse(st, c); st += c; } }   // mirrored: keep positive orientation
                    }
                    if (opc.Count > 0) PaintPolys(dst, lr, op, opc, s.Stroke, false, opacity, o, premul, softClip, t, shapeBox, s, true);
                }
            }
        }
        static bool IsAxisAligned(Matrix3x2 m) => (MathF.Abs(m.M12) < 1e-6f && MathF.Abs(m.M21) < 1e-6f) || (MathF.Abs(m.M11) < 1e-6f && MathF.Abs(m.M22) < 1e-6f);
        static bool IsUniform(Matrix3x2 m)
        {
            float a = m.M11 * m.M11 + m.M12 * m.M12, b = m.M21 * m.M21 + m.M22 * m.M22, d = m.M11 * m.M21 + m.M12 * m.M22;
            return MathF.Abs(a - b) <= 1e-3f * MathF.Max(a, b) && MathF.Abs(d) <= 1e-3f * MathF.Max(a, b);
        }

        /// <summary>Paints polygon contours with a paint. Solid + no soft clip = one DRAW_POLY; otherwise coverage mask + per-pixel paint.</summary>
        static void PaintPolys(Sprite dst, Rectangle lr, List<PointF> pts, List<int> cnt, VectorPaint paint, bool evenOdd, float opacity, VectorRenderOptions o, bool premul,
                               VectorClip? softClip, Matrix3x2 t, RectangleF box, VectorShape shape, bool isStroke)
        {
            int forced = o.ForceColor ?? 0;
            // work on a copy: the caller reuses pts for the stroke of the same shape
            var work = tWork ??= new List<PointF>(1024); work.Clear(); work.AddRange(pts);
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(work);
            // FillPolygons takes pixel-CENTRE coordinates (adds 0.5): shift our area coordinates
            for (int i = 0; i < span.Length; i++) span[i] = new PointF(span[i].X - 0.5f, span[i].Y - 0.5f);
            try
            {
                if (paint is VectorColor col && softClip == null)
                {
                    int argb = o.ForceColor.HasValue ? (forced & 0xFFFFFF) | (col.Argb & unchecked((int)0xFF000000)) : col.Argb;
                    int a = (int)MathF.Round(((argb >> 24) & 255) * opacity); if (a <= 0) return;
                    argb = (argb & 0xFFFFFF) | (a << 24);
                    var save = dst.LockRect; dst.SetLockRect(lr);
                    if (premul)   // LineOp.AlphaOver takes a STRAIGHT colour and writes premultiplied source-over (alpha accumulates)
                        dst.FillPolygons(span, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cnt), argb, SR2D.LineOp.AlphaOver, o.AA, evenOdd);
                    else dst.FillPolygons(span, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cnt), argb, a == 255 && !o.AA ? SR2D.LineOp.Set : SR2D.LineOp.AlphaBlend, o.AA, evenOdd);
                    dst.SetLockRect(save);
                    return;
                }
                // ---- mask path: coverage of the geometry (and the clip) in a scratch sprite, then paint per pixel
                Rectangle r0 = Rectangle.Intersect(lr, Rectangle.FromLTRB((int)MathF.Floor(box.Left), (int)MathF.Floor(box.Top), (int)MathF.Ceiling(box.Right) + 1, (int)MathF.Ceiling(box.Bottom) + 1));
                if (r0.Width <= 0 || r0.Height <= 0) return;
                var cov = Scratch(ref tCov, r0.Width, r0.Height);
                cov.SetLockRect(0, r0.Width, 0, r0.Height); cov.ClearRect(0, r0.Width, 0, r0.Height, 0);
                for (int i = 0; i < span.Length; i++) span[i] = new PointF(span[i].X - r0.X, span[i].Y - r0.Y);
                cov.FillPolygons(span, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cnt), unchecked((int)0xFFFFFFFF), SR2D.LineOp.Set, o.AA, evenOdd);
                Sprite? clip = null;
                if (softClip != null)
                {
                    clip = Scratch(ref tClip, r0.Width, r0.Height); clip.SetLockRect(0, r0.Width, 0, r0.Height);
                    bool first = true; var cp = new List<PointF>(); var cc = new List<int>();
                    foreach (var (path, eo) in softClip.Paths)
                    {
                        cp.Clear(); cc.Clear();
                        var subs = Flatten(path, t, o.Tolerance ?? Sprite.CurveTolerance, cp);
                        foreach (var (st, c, _) in subs) cc.Add(c);
                        var cs = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cp);
                        for (int i = 0; i < cs.Length; i++) cs[i] = new PointF(cs[i].X - 0.5f - r0.X, cs[i].Y - 0.5f - r0.Y);
                        var target = first ? clip : Scratch(ref tClip2, r0.Width, r0.Height);
                        target.SetLockRect(0, r0.Width, 0, r0.Height); target.ClearRect(0, r0.Width, 0, r0.Height, 0);
                        if (cc.Count > 0) target.FillPolygons(cs, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cc), unchecked((int)0xFFFFFFFF), SR2D.LineOp.Set, o.AA, eo);
                        if (!first) MulInto(clip, target, r0.Width, r0.Height);
                        first = false;
                    }
                }
                // paint
                if (paint is VectorColor c2)
                {
                    int argb = o.ForceColor.HasValue ? (forced & 0xFFFFFF) | (c2.Argb & unchecked((int)0xFF000000)) : c2.Argb;
                    Composite(dst, r0, cov, clip, null, argb, opacity, premul, default, default);
                }
                else if (paint is VectorImagePaint ip)
                {
                    if (o.ForceColor.HasValue) { Composite(dst, r0, cov, clip, null, (forced & 0xFFFFFF) | unchecked((int)0xFF000000), opacity, premul, default, default); return; }
                    var full = ip.Matrix * t; if (!Matrix3x2.Invert(full, out var inv)) return;
                    CompositeImage(dst, r0, cov, clip, ip, opacity, premul, full, inv);
                }
                else
                {
                    var g = (VectorGradient)paint;
                    var lut = BuildLut(g, opacity, o.ForceColor);
                    // device -> gradient space
                    Matrix3x2 gm = g.Matrix;
                    if (g.ObjectBoundingBox)
                    {   // gradient coordinates are fractions of the (local, untransformed) fill bounding box
                        var lb = shape.Path.ControlBounds();
                        if (isStroke) lb.Inflate(shape.StrokeWidth / 2, shape.StrokeWidth / 2);
                        gm = gm * Matrix3x2.CreateScale(MathF.Max(lb.Width, 1e-6f), MathF.Max(lb.Height, 1e-6f)) * Matrix3x2.CreateTranslation(lb.X, lb.Y);
                    }
                    var full = gm * t; if (!Matrix3x2.Invert(full, out var inv)) inv = Matrix3x2.Identity;
                    Composite(dst, r0, cov, clip, g, 0, opacity, premul, inv, lut);
                }
            }
            finally { }
        }
        static void MulInto(Sprite a, Sprite b, int w, int h)
        {
            var pa = a.Pixels; var pb = b.Pixels; int wa = a.Width, wb = b.Width;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) { int ia = y * wa + x; int ca = pa[ia] & 255, cb = pb[y * wb + x] & 255; int v = (ca * cb + 127) / 255; pa[ia] = v * 0x01010101; }
        }

        /// <summary>256-entry colour table of the gradient (straight ARGB, alpha scaled by opacity).</summary>
        static int[] BuildLut(VectorGradient g, float opacity, int? force)
        {
            var lut = tLut ??= new int[256];
            var st = g.Stops;
            if (st.Count == 0) { Array.Clear(lut); return lut; }
            var sorted = new List<VectorStop>(st); sorted.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            for (int i = 0; i < 256; i++)
            {
                float t = i / 255f; int c;
                if (t <= sorted[0].Offset) c = sorted[0].Argb;
                else if (t >= sorted[^1].Offset) c = sorted[^1].Argb;
                else
                {
                    int k = 0; while (k < sorted.Count - 1 && sorted[k + 1].Offset < t) k++;
                    var a = sorted[k]; var b = sorted[k + 1]; float f = b.Offset > a.Offset ? (t - a.Offset) / (b.Offset - a.Offset) : 0;
                    c = Lerp(a.Argb, b.Argb, f);
                }
                if (force.HasValue) c = (force.Value & 0xFFFFFF) | (c & unchecked((int)0xFF000000));
                int al = (int)MathF.Round(((c >> 24) & 255) * opacity);
                lut[i] = (c & 0xFFFFFF) | (al << 24);
            }
            return lut;
        }
        static int Lerp(int a, int b, float f)
        {
            int A = (int)(((a >> 24) & 255) + (((b >> 24) & 255) - ((a >> 24) & 255)) * f + 0.5f);
            int R = (int)(((a >> 16) & 255) + (((b >> 16) & 255) - ((a >> 16) & 255)) * f + 0.5f);
            int G = (int)(((a >> 8) & 255) + (((b >> 8) & 255) - ((a >> 8) & 255)) * f + 0.5f);
            int B = (int)((a & 255) + ((b & 255) - (a & 255)) * f + 0.5f);
            return A << 24 | R << 16 | G << 8 | B;
        }

        /// <summary>Per-pixel paint of a coverage mask (times an optional clip mask) with a colour or a gradient onto the destination.</summary>
        static void Composite(Sprite dst, Rectangle r, Sprite cov, Sprite? clip, VectorGradient? g, int argb, float opacity, bool premul, Matrix3x2 inv, int[]? lut)
        {
            var dp = dst.Pixels; var cp = cov.Pixels; var kp = clip != null ? clip.Pixels : Span<int>.Empty; int dw = dst.Width, cw = cov.Width, kw = clip?.Width ?? 0;
            int solidA = (int)MathF.Round(((argb >> 24) & 255) * opacity);
            float x1 = 0, y1 = 0, dx = 0, dy = 0, invLen2 = 0;
            float cx = 0, cy = 0, rr = 0, fx = 0, fy = 0, fr = 0; bool radial = g != null && g.Radial;
            if (g != null && !radial) { x1 = g.X1; y1 = g.Y1; dx = g.X2 - g.X1; dy = g.Y2 - g.Y1; float l2 = dx * dx + dy * dy; invLen2 = l2 > 1e-12f ? 1 / l2 : 0; }
            if (radial) { cx = g!.Cx; cy = g.Cy; rr = g.R; fx = g.Fx; fy = g.Fy; fr = g.Fr; }
            for (int y = 0; y < r.Height; y++)
            {
                int drow = (r.Y + y) * dw + r.X, crow = y * cw, krow = y * kw;
                for (int x = 0; x < r.Width; x++)
                {
                    int c = cp[crow + x] & 255; if (c == 0) continue;
                    if (clip != null) { c = (c * (kp[krow + x] & 255) + 127) / 255; if (c == 0) continue; }
                    int col;
                    if (g == null) col = argb;
                    else
                    {
                        // gradient parameter at the pixel centre
                        var p = Vector2.Transform(new Vector2(r.X + x + 0.5f, r.Y + y + 0.5f), inv);
                        float t; bool valid = true;
                        if (!radial) t = ((p.X - x1) * dx + (p.Y - y1) * dy) * invLen2;
                        else t = RadialT(p.X, p.Y, cx, cy, rr, fx, fy, fr, out valid);
                        if (!valid) continue;
                        if (t < 0 || t > 1)
                        {
                            switch (g.Spread)
                            {
                                case VectorSpread.Repeat: t -= MathF.Floor(t); break;
                                case VectorSpread.Reflect: { float m2 = t - 2 * MathF.Floor(t / 2); t = m2 > 1 ? 2 - m2 : m2; break; }
                                default: if ((t < 0 && !g.ExtendStart) || (t > 1 && !g.ExtendEnd)) continue; t = Math.Clamp(t, 0, 1); break;
                            }
                        }
                        col = lut![(int)(t * 255 + 0.5f)];
                    }
                    int a = g == null ? solidA : (col >> 24) & 255;
                    a = (a * c + 127) / 255; if (a == 0) continue;
                    int d = dp[drow + x];
                    int sr = (col >> 16) & 255, sg = (col >> 8) & 255, sb = col & 255;
                    int dr = (d >> 16) & 255, dg = (d >> 8) & 255, db = d & 255, da = (d >> 24) & 255;
                    int inv255 = 255 - a;
                    if (premul)
                    {   // premultiplied source-over
                        int pr = sr * a / 255, pg = sg * a / 255, pb = sb * a / 255;
                        dr = Math.Min(255, pr + (dr * inv255 + 127) / 255); dg = Math.Min(255, pg + (dg * inv255 + 127) / 255); db = Math.Min(255, pb + (db * inv255 + 127) / 255);
                        da = Math.Min(255, a + (da * inv255 + 127) / 255);
                    }
                    else
                    {
                        dr += (sr - dr) * a / 255; dg += (sg - dg) * a / 255; db += (sb - db) * a / 255;
                        da = Math.Min(255, a + (da * inv255 + 127) / 255);
                    }
                    dp[drow + x] = da << 24 | dr << 16 | dg << 8 | db;
                }
            }
        }
        /// <summary>Per-pixel paint of a coverage mask with a raster image: device -> image space through <paramref name="inv"/>;
        /// bilinear samples of a box-reduced level when the image is minified (so downscaled photos do not sparkle), nearest when
        /// <see cref="VectorImagePaint.Smooth"/> is off and the image is enlarged (crisp bilevel masks). Outside the image: transparent.</summary>
        static void CompositeImage(Sprite dst, Rectangle r, Sprite cov, Sprite? clip, VectorImagePaint ip, float opacity, bool premul, Matrix3x2 fwd, Matrix3x2 inv)
        {
            // minification factor: image pixels per device pixel along each axis
            float sxLen = MathF.Sqrt(fwd.M11 * fwd.M11 + fwd.M12 * fwd.M12), syLen = MathF.Sqrt(fwd.M21 * fwd.M21 + fwd.M22 * fwd.M22);
            int level = 1;
            if (sxLen > 0 && syLen > 0) { float minify = MathF.Min(1 / sxLen, 1 / syLen); if (minify >= 2) level = Math.Min(64, (int)minify); }
            var (px, iw, ih) = ip.Level(level);
            float lsx = (float)iw / ip.Width, lsy = (float)ih / ip.Height;   // image space -> level space
            bool smooth = ip.Smooth || level > 1 || sxLen < 0.75f;             // enlarged non-smooth images stay blocky
            var dp = dst.Pixels; var cp = cov.Pixels; var kp = clip != null ? clip.Pixels : Span<int>.Empty; int dw = dst.Width, cw = cov.Width, kw = clip?.Width ?? 0;
            int opa = (int)MathF.Round(opacity * 255);
            for (int y = 0; y < r.Height; y++)
            {
                int drow = (r.Y + y) * dw + r.X, crow = y * cw, krow = y * kw;
                // incremental image coordinates along the row
                var p0 = Vector2.Transform(new Vector2(r.X + 0.5f, r.Y + y + 0.5f), inv);
                float ux = p0.X * lsx, uy = p0.Y * lsy, dux = inv.M11 * lsx, duy = inv.M12 * lsy;
                for (int x = 0; x < r.Width; x++, ux += dux, uy += duy)
                {
                    int c = cp[crow + x] & 255; if (c == 0) continue;
                    if (clip != null) { c = (c * (kp[krow + x] & 255) + 127) / 255; if (c == 0) continue; }
                    int col;
                    if (smooth)
                    {
                        float fx = ux - 0.5f, fy = uy - 0.5f; int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy); float tx = fx - x0, ty = fy - y0;
                        if (x0 < -1 || y0 < -1 || x0 >= iw || y0 >= ih) continue;
                        int x1 = x0 + 1, y1 = y0 + 1; int xa = Math.Clamp(x0, 0, iw - 1), xb = Math.Clamp(x1, 0, iw - 1), ya = Math.Clamp(y0, 0, ih - 1), yb = Math.Clamp(y1, 0, ih - 1);
                        col = Bilerp(px[ya * iw + xa], px[ya * iw + xb], px[yb * iw + xa], px[yb * iw + xb], tx, ty);
                    }
                    else
                    {
                        int ix = (int)MathF.Floor(ux), iy = (int)MathF.Floor(uy);
                        if (ix < 0 || iy < 0 || ix >= iw || iy >= ih) continue;
                        col = px[iy * iw + ix];
                    }
                    int a = (col >> 24) & 255; if (a == 0) continue;
                    a = (a * c + 127) / 255; if (opa < 255) a = (a * opa + 127) / 255; if (a == 0) continue;
                    int d = dp[drow + x];
                    int sr = (col >> 16) & 255, sg = (col >> 8) & 255, sb = col & 255;
                    int dr = (d >> 16) & 255, dg = (d >> 8) & 255, db = d & 255, da = (d >> 24) & 255;
                    int inv255 = 255 - a;
                    if (premul)
                    {
                        int pr = sr * a / 255, pg = sg * a / 255, pb = sb * a / 255;
                        dr = Math.Min(255, pr + (dr * inv255 + 127) / 255); dg = Math.Min(255, pg + (dg * inv255 + 127) / 255); db = Math.Min(255, pb + (db * inv255 + 127) / 255);
                        da = Math.Min(255, a + (da * inv255 + 127) / 255);
                    }
                    else
                    {
                        if (a == 255) { dr = sr; dg = sg; db = sb; }
                        else { dr += (sr - dr) * a / 255; dg += (sg - dg) * a / 255; db += (sb - db) * a / 255; }
                        da = Math.Min(255, a + (da * inv255 + 127) / 255);
                    }
                    dp[drow + x] = da << 24 | dr << 16 | dg << 8 | db;
                }
            }
        }
        /// <summary>Bilinear blend of four straight-ARGB pixels (alpha-weighted colours so transparent neighbours do not darken edges).</summary>
        static int Bilerp(int c00, int c10, int c01, int c11, float tx, float ty)
        {
            int wx = (int)(tx * 256 + 0.5f), wy = (int)(ty * 256 + 0.5f);
            int w00 = (256 - wx) * (256 - wy), w10 = wx * (256 - wy), w01 = (256 - wx) * wy, w11 = wx * wy;
            int a00 = (c00 >> 24) & 255, a10 = (c10 >> 24) & 255, a01 = (c01 >> 24) & 255, a11 = (c11 >> 24) & 255;
            if (a00 == 255 && a10 == 255 && a01 == 255 && a11 == 255)
            {
                int r = (((c00 >> 16) & 255) * w00 + ((c10 >> 16) & 255) * w10 + ((c01 >> 16) & 255) * w01 + ((c11 >> 16) & 255) * w11 + 32768) >> 16;
                int g = (((c00 >> 8) & 255) * w00 + ((c10 >> 8) & 255) * w10 + ((c01 >> 8) & 255) * w01 + ((c11 >> 8) & 255) * w11 + 32768) >> 16;
                int b = ((c00 & 255) * w00 + (c10 & 255) * w10 + (c01 & 255) * w01 + (c11 & 255) * w11 + 32768) >> 16;
                return unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
            }
            long wa00 = (long)w00 * a00, wa10 = (long)w10 * a10, wa01 = (long)w01 * a01, wa11 = (long)w11 * a11; long wa = wa00 + wa10 + wa01 + wa11;
            if (wa == 0) return 0;
            int A = (int)((wa + 32768) >> 16);
            int R = (int)((((c00 >> 16) & 255) * wa00 + ((c10 >> 16) & 255) * wa10 + ((c01 >> 16) & 255) * wa01 + ((c11 >> 16) & 255) * wa11 + wa / 2) / wa);
            int G = (int)((((c00 >> 8) & 255) * wa00 + ((c10 >> 8) & 255) * wa10 + ((c01 >> 8) & 255) * wa01 + ((c11 >> 8) & 255) * wa11 + wa / 2) / wa);
            int Bv = (int)(((c00 & 255) * wa00 + (c10 & 255) * wa10 + (c01 & 255) * wa01 + (c11 & 255) * wa11 + wa / 2) / wa);
            return Math.Min(255, A) << 24 | R << 16 | G << 8 | Bv;
        }
        /// <summary>Two-point conical gradient parameter: largest t with |p - c(t)| = r(t), r(t) &gt;= 0 (c(t) = f + t (c - f), r(t) = fr + t (R - fr)).</summary>
        static float RadialT(float px, float py, float cx, float cy, float r, float fx, float fy, float fr, out bool valid)
        {
            valid = true;
            float cdx = cx - fx, cdy = cy - fy, dr = r - fr, pdx = px - fx, pdy = py - fy;
            float a = cdx * cdx + cdy * cdy - dr * dr;
            float b = pdx * cdx + pdy * cdy + fr * dr;
            float c = pdx * pdx + pdy * pdy - fr * fr;
            if (MathF.Abs(a) < 1e-6f)
            {
                if (MathF.Abs(b) < 1e-9f) { valid = false; return 0; }
                float t = c / (2 * b); if (fr + t * dr < 0) { valid = false; return 0; } return t;
            }
            float disc = b * b - a * c;
            if (disc < 0) { valid = false; return 0; }
            float sq = MathF.Sqrt(disc);
            float t1 = (b + sq) / a, t2 = (b - sq) / a;
            float tmax = MathF.Max(t1, t2), tmin = MathF.Min(t1, t2);
            if (fr + tmax * dr >= 0) return tmax;
            if (fr + tmin * dr >= 0) return tmin;
            valid = false; return 0;
        }
    }
}
