using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------------------------------------------------------
    // Gradient fills for the shape API. A gradient is a VectorGradient (the same object the SVG / PDF / PostScript importers
    // produce): linear (x1,y1 -> x2,y2) or radial (centre, radius, optional focus), colour stops, spread (Pad / Repeat /
    // Reflect). Coordinates are in destination pixels unless the gradient is ObjectBoundingBox (fractions of the shape's
    // bounding box: 0..1 - the handy way, the same gradient object fits any shape).
    //
    //     var g = SpriteGradient.Linear(0, 0, 1, 0, 0xFF3399FF, 0xFFFF6030);        // left -> right (bounding-box fractions)
    //     canvas.FillRect(10, 10, 200, 60, g, AA: true);
    //     canvas.FillCircle(300, 100, 50, SpriteGradient.Radial(0.5f, 0.5f, 0.5f, 0xFFFFFFFF, 0xFF204080));
    //     canvas.FillPath(path, g);  canvas.FillPolygon(points, g);  canvas.StrokePath(path, g, 6f);
    //
    // The pixels go through the vector rasteriser's mask path (coverage + per-pixel gradient), so it composites like the
    // vector shapes (AlphaBlend over an ordinary sprite, source-over on an AlphaOver layer) and costs about the same as an
    // AA fill plus one gradient evaluation per covered pixel.
    // ------------------------------------------------------------------------------------------------------------------------

    /// <summary>Factory helpers for gradient paints (see <see cref="VectorGradient"/> for every field).</summary>
    internal static class SpriteGradient
    {
        /// <summary>Linear gradient between two points with evenly spaced colours. Coordinates are bounding-box fractions when <paramref name="boundingBox"/> (default), else pixels.</summary>
        public static VectorGradient Linear(float x1, float y1, float x2, float y2, bool boundingBox, params int[] argb)
        {
            var g = new VectorGradient { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, ObjectBoundingBox = boundingBox };
            Even(g, argb); return g;
        }
        public static VectorGradient Linear(float x1, float y1, float x2, float y2, params int[] argb) => Linear(x1, y1, x2, y2, true, argb);
        /// <summary>Radial gradient: centre (cx, cy), radius r, colours from the centre outwards. Fractions of the bounding box by default.</summary>
        public static VectorGradient Radial(float cx, float cy, float r, bool boundingBox, params int[] argb)
        {
            var g = new VectorGradient { Radial = true, Cx = cx, Cy = cy, R = r, Fx = cx, Fy = cy, Fr = 0, ObjectBoundingBox = boundingBox };
            Even(g, argb); return g;
        }
        public static VectorGradient Radial(float cx, float cy, float r, params int[] argb) => Radial(cx, cy, r, true, argb);
        /// <summary>Radial gradient with the focus (the point colour 0 sits at) away from the centre - a highlight on a sphere.</summary>
        public static VectorGradient RadialFocus(float cx, float cy, float r, float fx, float fy, bool boundingBox, params int[] argb)
        {
            var g = Radial(cx, cy, r, boundingBox, argb); g.Fx = fx; g.Fy = fy; return g;
        }
        /// <summary>Angle form: a linear gradient across the bounding box in a direction (degrees, 0 = left to right, 90 = top to bottom).</summary>
        public static VectorGradient Angle(float degrees, params int[] argb)
        {
            float a = degrees * MathF.PI / 180, c = MathF.Cos(a), s = MathF.Sin(a);
            // the segment through the centre long enough to cover the unit box in that direction
            float half = 0.5f * (MathF.Abs(c) + MathF.Abs(s));
            return Linear(0.5f - c * half, 0.5f - s * half, 0.5f + c * half, 0.5f + s * half, true, argb);
        }
        /// <summary>Sets colour stops at explicit offsets (0..1).</summary>
        public static VectorGradient Stops(this VectorGradient g, params (float offset, int argb)[] stops)
        {
            g.Stops.Clear(); foreach (var (o, c) in stops) g.Stops.Add(new VectorStop(o, c)); return g;
        }
        public static VectorGradient Spread(this VectorGradient g, VectorSpread spread) { g.Spread = spread; return g; }
        /// <summary>Extra transform of the gradient (e.g. rotate a bounding-box gradient about its centre).</summary>
        public static VectorGradient WithMatrix(this VectorGradient g, Matrix3x2 m) { g.Matrix = m; return g; }
        static void Even(VectorGradient g, int[] argb)
        {
            if (argb == null || argb.Length == 0) { g.Stops.Add(new VectorStop(0, unchecked((int)0xFF000000))); g.Stops.Add(new VectorStop(1, unchecked((int)0xFFFFFFFF))); return; }
            if (argb.Length == 1) { g.Stops.Add(new VectorStop(0, argb[0])); g.Stops.Add(new VectorStop(1, argb[0])); return; }
            for (int i = 0; i < argb.Length; i++) g.Stops.Add(new VectorStop(i / (float)(argb.Length - 1), argb[i]));
        }
    }

    internal sealed partial class Sprite
    {
        // ------------------------------------------------------------------ fills with a gradient paint
        /// <summary>Fills a polygon (pixel-centre coordinates like <see cref="FillPolygon(ReadOnlySpan{PointF}, int, SR2D.LineOp, bool, bool, int)"/>) with a gradient.</summary>
        public void FillPolygon(ReadOnlySpan<PointF> Points, VectorGradient Paint, bool AA = true, bool EvenOdd = false, float Opacity = 1f)
        {
            var p = new VectorPath(); if (Points.Length < 3) return;
            p.MoveTo(Points[0]); for (int i = 1; i < Points.Length; i++) p.LineTo(Points[i]); p.Close();
            FillPath(p, Paint, AA, EvenOdd, Opacity);
        }
        /// <summary>Fills a path (built with <see cref="PathBuilder"/>) with a gradient.</summary>
        public void FillPath(PathBuilder Path, VectorGradient Paint, bool AA = true, bool EvenOdd = false, float Opacity = 1f) => FillPath(VectorPath.From(Path), Paint, AA, EvenOdd, Opacity);
        /// <summary>Fills a <see cref="VectorPath"/> (pixel coordinates) with a gradient.</summary>
        public void FillPath(VectorPath Path, VectorGradient Paint, bool AA = true, bool EvenOdd = false, float Opacity = 1f)
        {
            var s = new VectorShape { Path = Path, Fill = Paint, EvenOdd = EvenOdd, Opacity = Opacity };
            VectorRender.DrawShape(s, this, Matrix3x2.Identity, AA);
        }
        /// <summary>Strokes a path with a gradient (width in pixels).</summary>
        public void StrokePath(VectorPath Path, VectorGradient Paint, float Width = 1f, bool AA = true, VectorCap Cap = VectorCap.Butt, VectorJoin Join = VectorJoin.Miter, float Opacity = 1f)
        {
            var s = new VectorShape { Path = Path, Fill = null, Stroke = Paint, StrokeWidth = Width, Cap = Cap, Join = Join, Opacity = Opacity };
            VectorRender.DrawShape(s, this, Matrix3x2.Identity, AA);
        }
        public void StrokePath(PathBuilder Path, VectorGradient Paint, float Width = 1f, bool AA = true, VectorCap Cap = VectorCap.Butt, VectorJoin Join = VectorJoin.Miter, float Opacity = 1f)
            => StrokePath(VectorPath.From(Path), Paint, Width, AA, Cap, Join, Opacity);
        /// <summary>Fills a <see cref="VectorPath"/> with a solid colour (non-zero winding unless <paramref name="EvenOdd"/>).</summary>
        public void FillPath(VectorPath Path, int c, SR2D.LineOp Op = SR2D.LineOp.Set, bool AA = true, bool EvenOdd = false, int BlendFactor = 128)
        {
            if (meRight <= meLeft || meBottom <= meTop) return;
            var pts = new List<PointF>(256);
            var subs = VectorRender.Flatten(Path, Matrix3x2.Identity, CurveTolerance, pts);
            if (pts.Count < 3) return;
            // sub-paths with < 3 points are dropped; area coordinates -> pixel-centre coordinates (FillPolygons adds 0.5)
            var work = new List<PointF>(pts.Count); var counts = new List<int>(subs.Count);
            foreach (var (start, count, _) in subs) { if (count < 3) continue; for (int i = 0; i < count; i++) work.Add(new PointF(pts[start + i].X - 0.5f, pts[start + i].Y - 0.5f)); counts.Add(count); }
            if (counts.Count == 0) return;
            FillPolygons(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(work), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(counts), c, Op, AA, EvenOdd, BlendFactor);
        }
        /// <summary>Strokes a <see cref="VectorPath"/> with a solid colour and a full <see cref="StrokeStyle"/> (caps / joins / dashes; Sprite.Stroke.cs).</summary>
        public void StrokePath(VectorPath Path, int c, StrokeStyle Style, bool AA = true, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            if (meRight <= meLeft || meBottom <= meTop) return;
            var pts = new List<PointF>(256);
            var subs = VectorRender.Flatten(Path, Matrix3x2.Identity, CurveTolerance, pts);
            StrokeFlattened(pts, subs, c, Style, AA, Op, BlendFactor);
        }
        /// <summary>Strokes a path with a gradient and a full <see cref="StrokeStyle"/>.</summary>
        public void StrokePath(VectorPath Path, VectorGradient Paint, StrokeStyle Style, bool AA = true, float Opacity = 1f)
        {
            var s = new VectorShape { Path = Path, Fill = null, Stroke = Paint, StrokeWidth = Style.Width, Cap = (VectorCap)Style.Cap, Join = (VectorJoin)Style.Join, MiterLimit = Style.MiterLimit, Dash = Style.Dash, DashOffset = Style.DashOffset, Opacity = Opacity };
            VectorRender.DrawShape(s, this, Matrix3x2.Identity, AA);
        }
        public void StrokePath(PathBuilder Path, VectorGradient Paint, StrokeStyle Style, bool AA = true, float Opacity = 1f) => StrokePath(VectorPath.From(Path), Paint, Style, AA, Opacity);
        /// <summary>Gradient-filled rectangle (x, y, width, height in pixels; the area covers whole pixels when the values are integers).</summary>
        public void FillRect(float x, float y, float w, float h, VectorGradient Paint, bool AA = true, float Opacity = 1f)
            => FillPath(new VectorPath().Rect(x, y, w, h), Paint, AA, false, Opacity);
        public void FillRect(RectangleF r, VectorGradient Paint, bool AA = true, float Opacity = 1f) => FillRect(r.X, r.Y, r.Width, r.Height, Paint, AA, Opacity);
        public void FillRoundRect(float x, float y, float w, float h, float radius, VectorGradient Paint, bool AA = true, float Opacity = 1f)
            => FillPath(new VectorPath().RoundRect(x, y, w, h, radius, radius), Paint, AA, false, Opacity);
        public void FillEllipse(float cx, float cy, float rx, float ry, VectorGradient Paint, bool AA = true, float Opacity = 1f)
            => FillPath(new VectorPath().Ellipse(cx, cy, rx, ry), Paint, AA, false, Opacity);
        public void FillCircle(float cx, float cy, float r, VectorGradient Paint, bool AA = true, float Opacity = 1f) => FillEllipse(cx, cy, r, r, Paint, AA, Opacity);
        /// <summary>Fills a selection mask's shape... no: fills the whole lock rect with the gradient (backgrounds). Bounding-box gradients span the lock rect.</summary>
        public void FillGradient(VectorGradient Paint, float Opacity = 1f)
        {
            var r = LockRect; FillRect(r.Left, r.Top, r.Width, r.Height, Paint, false, Opacity);
        }
    }
}
