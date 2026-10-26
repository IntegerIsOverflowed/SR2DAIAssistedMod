// Test flag: the outline operations of VectorOffset, rebuilt on System.Drawing.Drawing2D (GraphicsPath.Widen).
// When NativeOutline.On is set, the demo's outline conversion and wavy-dash midline use this instead of VectorOffset.
// Windows only (System.Drawing); no third-party code. Output is polylines (GDI+ flattens), so node counts are not fitted.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Sr2d64CSport
{
    internal static class NativeOutline
    {
        /// <summary>The demo flag: false = VectorOffset (our pipeline), true = GraphicsPath.Widen.</summary>
        public static bool On;

        /// <summary>Cap of open lines: 0 = flat, 1 = square, 2 = round (the Test tab selector).</summary>
        public static int CapIndex = 1;
        /// <summary>Corner join: 0 = miter (limit 4), 1 = bevel, 2 = round (the Test tab selector).</summary>
        public static int JoinIndex = 0;

        const float S = 100f;              // path units per mm (GDI+ works in floats)
        const float Flat = 0.5f;           // flatness in path units (0.005 mm)

        /// <summary>
        /// The band between the outer offset dOuter and the inner offset dInner, as VectorOffset.Band:
        /// centre (d, -d) is the stroke of width 2d, outside (d, 0) and inside (0, -d) are the rings.
        /// The pieces are regions, combined by Compose: each contour's stroke, the contour itself, and each open line
        /// (bar) as its own group, so no ring is chosen and no orientation is assumed.
        /// </summary>
        public static VectorPath Band(VectorPath p, double dOuter, double dInner)
        {
            Split(p, out var contours, out var lines);
            int k = contours.Count;
            double barWidth = dOuter - dInner;                         // bars and the centre stroke
            double strokeWidth = dOuter > 0 && dInner < 0 ? barWidth   // centre: stroke of width dOuter - dInner
                                : dOuter > 0 ? 2 * dOuter              // outside: the stroke reaches dOuter each side
                                : -2 * dInner;                         // inside: the stroke reaches -dInner each side

            var groups = new List<IList<List<Pt2>>>();
            foreach (var c in contours) groups.Add(Stroke(c, true, strokeWidth));       // groups 0..k-1: stroke of each contour
            groups.Add(contours.ConvertAll(c => ToMm(c)));                                 // group k: the contours' regions
            foreach (var l in lines) groups.Add(Stroke(l, false, barWidth));             // groups k+1..: bars

            Func<bool[], bool> keep;
            if (dOuter > 0 && dInner < 0)
                keep = m => AnyOf(m, 0, k) || AnyOf(m, k + 1, m.Length);                     // centre
            else if (dOuter > 0 && dInner == 0)
                keep = m => (AnyOf(m, 0, k) && !m[k]) || AnyOf(m, k + 1, m.Length);          // outside
            else if (dOuter == 0 && dInner < 0)
                keep = m => (AnyOf(m, 0, k) && m[k]) || AnyOf(m, k + 1, m.Length);           // inside
            else
                keep = m => m[k] || AnyOf(m, k + 1, m.Length);                               // no offset: the shape itself
            return ToVector(OutlineArrange.Compose(groups, keep));
        }

        /// <summary>The filled region grown by d (d &gt; 0) or shrunk by -d, as VectorOffset.Band(path, d) with no inner ring.</summary>
        public static VectorPath Grow(VectorPath p, double d)
        {
            if (d == 0) return p;
            Split(p, out var contours, out var lines);
            int k = contours.Count;
            var groups = new List<IList<List<Pt2>>>();
            foreach (var c in contours) groups.Add(Stroke(c, true, 2 * Math.Abs(d)));
            groups.Add(contours.ConvertAll(c => ToMm(c)));
            if (d > 0)
            {
                foreach (var l in lines) groups.Add(Stroke(l, false, 2 * d));           // open lines grow as a stroke
                return ToVector(OutlineArrange.Compose(groups, m => AnyOf(m, 0, k) || m[k] || AnyOf(m, k + 1, m.Length)));
            }
            return ToVector(OutlineArrange.Compose(groups, m => m[k] && !AnyOf(m, 0, k)));  // shrink: contour minus its stroke
        }

        static bool AnyOf(bool[] m, int from, int to)
        {
            for (int i = from; i < to; i++) if (m[i]) return true;
            return false;
        }

        /// <summary>Splits a path into its closed contours with area and the open or zero-area lines, flattened (path units).</summary>
        static void Split(VectorPath p, out List<List<PointF>> contours, out List<List<PointF>> lines)
        {
            contours = new List<List<PointF>>();
            lines = new List<List<PointF>>();
            foreach (var (pts, closed) in Polylines(ToGraphics(p)))
            {
                if (pts.Count < 2) continue;
                if (!closed || Math.Abs(Area(pts)) < 1e-9) lines.Add(pts);
                else contours.Add(pts);
            }
        }

        /// <summary>GraphicsPath.Widen of one polyline (path units in and out), as millimetre rings.</summary>
        static List<List<Pt2>> Stroke(List<PointF> pts, bool closed, double widthMm)
        {
            var rings = new List<List<Pt2>>();
            foreach (var (r, _) in Widen(pts, closed, (float)(widthMm * S)))
                if (r.Count >= 3) rings.Add(ToMm(r));
            return rings;
        }

        static List<Pt2> ToMm(IList<PointF> pts)
        {
            var r = new List<Pt2>(pts.Count);
            foreach (var q in pts) r.Add(new Pt2(q.X / S, q.Y / S));
            return r;
        }

        static VectorPath ToVector(IList<List<Pt2>> rings)
        {
            var v = new VectorPath();
            foreach (var ring in rings)
            {
                if (ring.Count < 3) continue;
                v.MoveTo((float)ring[0].X, (float)ring[0].Y);
                for (int i = 1; i < ring.Count; i++) v.LineTo((float)ring[i].X, (float)ring[i].Y);
                v.Close();
            }
            return v;
        }

        /// <summary>GraphicsPath.Widen of one polyline (closed, or open with square caps), in path units.</summary>
        static List<(List<PointF> pts, bool closed)> Widen(List<PointF> pts, bool closed, float width)
        {
            var gp = new GraphicsPath(FillMode.Winding);
            if (closed) gp.AddPolygon(pts.ToArray()); else gp.AddLines(pts.ToArray());
            using (var pen = new Pen(Color.Black, width))
            {
                pen.LineJoin = JoinIndex == 1 ? System.Drawing.Drawing2D.LineJoin.Bevel : JoinIndex == 2 ? System.Drawing.Drawing2D.LineJoin.Round : System.Drawing.Drawing2D.LineJoin.Miter;
                pen.MiterLimit = 4f;
                var cap = CapIndex == 0 ? System.Drawing.Drawing2D.LineCap.Flat : CapIndex == 2 ? System.Drawing.Drawing2D.LineCap.Round : System.Drawing.Drawing2D.LineCap.Square;
                pen.StartCap = cap;
                pen.EndCap = cap;
                gp.Widen(pen, new Matrix(), Flat);
            }
            return Polylines(gp);
        }

        // ---- conversions

        static GraphicsPath ToGraphics(VectorPath p)
        {
            var gp = new GraphicsPath(FillMode.Winding);
            PointF cur = PointF.Empty, start = PointF.Empty;
            int di = 0;
            foreach (var k in p.Kinds)
            {
                if (k == VectorPath.K.Move)
                {
                    cur = start = new PointF(p.Data[di] * S, p.Data[di + 1] * S); di += 2;
                    gp.StartFigure();
                }
                else if (k == VectorPath.K.Line)
                {
                    var e = new PointF(p.Data[di] * S, p.Data[di + 1] * S); di += 2;
                    gp.AddLine(cur, e); cur = e;
                }
                else if (k == VectorPath.K.Cubic)
                {
                    var c1 = new PointF(p.Data[di] * S, p.Data[di + 1] * S);
                    var c2 = new PointF(p.Data[di + 2] * S, p.Data[di + 3] * S);
                    var e = new PointF(p.Data[di + 4] * S, p.Data[di + 5] * S); di += 6;
                    gp.AddBezier(cur, c1, c2, e); cur = e;
                }
                else // close
                {
                    gp.CloseFigure(); cur = start;
                }
            }
            gp.Flatten(new Matrix(), Flat);
            return gp;
        }

        /// <summary>The figures of a flattened path as polylines (closed when the figure is closed).</summary>
        static List<(List<PointF> pts, bool closed)> Polylines(GraphicsPath gp)
        {
            var list = new List<(List<PointF>, bool)>();
            PointF[] points = gp.PathPoints;
            byte[] types = gp.PathTypes;
            List<PointF>? cur = null;
            for (int i = 0; i < points.Length; i++)
            {
                byte t = types[i];
                bool start = (t & 0x07) == 0, close = (t & 0x80) != 0;
                if (start)
                {
                    if (cur != null) list.Add((cur, false));
                    cur = new List<PointF> { points[i] };
                }
                else if (cur != null) cur.Add(points[i]);
                if (close && cur != null)
                {
                    // a closed figure may repeat its first point at the end
                    if (cur.Count > 1 && cur[0] == cur[cur.Count - 1]) cur.RemoveAt(cur.Count - 1);
                    list.Add((cur, true));
                    cur = null;
                }
            }
            if (cur != null) list.Add((cur, false));
            return list;
        }

        static double Area(IList<PointF> pts)
        {
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % pts.Count];
                a += (double)p.X * q.Y - (double)q.X * p.Y;
            }
            return a / 2;
        }
    }
}
