using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------
    // VectorImage: a resolution-independent picture made of filled / stroked
    // paths (what SVG, EPS and PDF describe). Load one from a file, compose
    // several, transform the whole thing (rotate / scale / squish / skew) and
    // draw it onto any Sprite - the rasterisation goes through the existing
    // anti-aliased polygon kernel (DRAW_POLY), so it costs about what the same
    // shapes drawn with FillPath / DrawPath would.
    //
    //   var logo = VectorImage.Load("logo.svg");        // .svg / .eps / .ps / .pdf / .ai
    //   logo.Draw(canvas, x, y, scale, scale, angleDeg); // pivot = centre of the view box
    //   var icon = logo.Rasterize(64, 64);               // premultiplied Sprite (AlphaOver)
    //
    // Files: Vector.cs (model, composition, bounds, SVG export), Vector.Render.cs
    // (flatten, stroke, gradients, clip -> Sprite), Vector.Svg.cs, Vector.Ps.cs
    // (PostScript interpreter = EPS), Vector.Pdf.cs.
    // ------------------------------------------------------------------------

    /// <summary>Stroke line cap.</summary>
    internal enum VectorCap : byte { Butt, Round, Square }
    /// <summary>Stroke line join.</summary>
    internal enum VectorJoin : byte { Miter, Round, Bevel }
    /// <summary>What a gradient does outside 0..1.</summary>
    internal enum VectorSpread : byte { Pad, Reflect, Repeat }

    /// <summary>How a shape is painted: <see cref="VectorColor"/> or <see cref="VectorGradient"/>; null = not painted.</summary>
    internal abstract class VectorPaint
    {
        public abstract VectorPaint Clone();
    }

    /// <summary>Solid ARGB colour.</summary>
    internal sealed class VectorColor : VectorPaint
    {
        public int Argb;
        public VectorColor(int argb) { Argb = argb; }
        public VectorColor(Color c) { Argb = c.ToArgb(); }
        public byte A => (byte)(Argb >> 24);
        public override VectorPaint Clone() => new VectorColor(Argb);
        public override string ToString() => $"#{Argb:X8}";
        public static readonly VectorColor Black = new VectorColor(unchecked((int)0xFF000000));
    }

    /// <summary>One gradient stop: offset 0..1 and colour.</summary>
    internal struct VectorStop
    {
        public float Offset; public int Argb;
        public VectorStop(float offset, int argb) { Offset = offset; Argb = argb; }
    }

    /// <summary>
    /// Linear or radial gradient. Coordinates are in gradient space, mapped by <see cref="Matrix"/> into the shape's
    /// space - or, with <see cref="ObjectBoundingBox"/>, given as fractions (0..1) of the shape's bounding box (SVG default).
    /// Radial: circle (Cx,Cy,R) with focal point (Fx,Fy) and optional focal radius Fr - also covers PDF's two-circle shadings.
    /// </summary>
    internal sealed class VectorGradient : VectorPaint
    {
        public bool Radial;
        public float X1, Y1, X2, Y2;                      // linear: start / end
        public float Cx, Cy, R, Fx, Fy, Fr;               // radial
        public Matrix3x2 Matrix = Matrix3x2.Identity;
        public bool ObjectBoundingBox;
        public VectorSpread Spread = VectorSpread.Pad;
        public bool ExtendStart = true, ExtendEnd = true; // PDF: paint beyond the ends (Pad) or leave transparent
        public List<VectorStop> Stops = new List<VectorStop>();
        public override VectorPaint Clone()
        {
            var g = (VectorGradient)MemberwiseClone(); g.Stops = new List<VectorStop>(Stops); return g;
        }
        public static VectorGradient Linear(float x1, float y1, float x2, float y2, params VectorStop[] stops)
        {
            var g = new VectorGradient { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 }; g.Stops.AddRange(stops); return g;
        }
        public static VectorGradient RadialAt(float cx, float cy, float r, params VectorStop[] stops)
        {
            var g = new VectorGradient { Radial = true, Cx = cx, Cy = cy, R = r, Fx = cx, Fy = cy }; g.Stops.AddRange(stops); return g;
        }
        /// <summary>Average colour of the stops (used where a gradient cannot be drawn, e.g. bounds-only previews).</summary>
        public int AverageArgb()
        {
            if (Stops.Count == 0) return 0;
            long a = 0, r = 0, gg = 0, b = 0;
            foreach (var s in Stops) { a += (s.Argb >> 24) & 255; r += (s.Argb >> 16) & 255; gg += (s.Argb >> 8) & 255; b += s.Argb & 255; }
            int n = Stops.Count; return (int)((a / n) << 24 | (r / n) << 16 | (gg / n) << 8 | (b / n));
        }
    }

    /// <summary>
    /// A path: sub-paths of straight and cubic Bézier segments in absolute coordinates. Quadratics and arcs are converted on
    /// entry (<see cref="QuadTo"/>, <see cref="ArcTo"/>), so renderers only see Move / Line / Cubic / Close.
    /// </summary>
    internal sealed class VectorPath
    {
        internal enum K : byte { Move, Line, Cubic, Close }
        internal readonly List<K> Kinds = new List<K>();
        internal readonly List<float> Data = new List<float>();
        float startX, startY, curX, curY; bool open;

        public bool IsEmpty => Kinds.Count == 0;
        public int SegmentCount => Kinds.Count;
        public PointF Current => new PointF(curX, curY);
        public PointF SubPathStart => new PointF(startX, startY);
        public bool HasOpenSubPath => open;

        public VectorPath Clear() { Kinds.Clear(); Data.Clear(); open = false; return this; }
        public VectorPath MoveTo(float x, float y) { Kinds.Add(K.Move); Data.Add(x); Data.Add(y); startX = curX = x; startY = curY = y; open = true; return this; }
        public VectorPath MoveTo(PointF p) => MoveTo(p.X, p.Y);
        public VectorPath LineTo(float x, float y) { if (!open) MoveTo(curX, curY); Kinds.Add(K.Line); Data.Add(x); Data.Add(y); curX = x; curY = y; return this; }
        public VectorPath LineTo(PointF p) => LineTo(p.X, p.Y);
        public VectorPath CurveTo(float c0x, float c0y, float c1x, float c1y, float x, float y)
        {
            if (!open) MoveTo(curX, curY);
            Kinds.Add(K.Cubic); Data.Add(c0x); Data.Add(c0y); Data.Add(c1x); Data.Add(c1y); Data.Add(x); Data.Add(y); curX = x; curY = y; return this;
        }
        public VectorPath CurveTo(PointF c0, PointF c1, PointF p) => CurveTo(c0.X, c0.Y, c1.X, c1.Y, p.X, p.Y);
        public VectorPath QuadTo(float cx, float cy, float x, float y)
        {
            float x0 = curX, y0 = curY;
            return CurveTo(x0 + 2f / 3 * (cx - x0), y0 + 2f / 3 * (cy - y0), x + 2f / 3 * (cx - x), y + 2f / 3 * (cy - y), x, y);
        }
        public VectorPath Close() { if (open && Kinds.Count > 0 && Kinds[^1] != K.Close) { Kinds.Add(K.Close); curX = startX; curY = startY; } open = false; return this; }
        /// <summary>SVG elliptical arc (endpoint parameterisation) converted to cubics.</summary>
        public VectorPath ArcTo(float rx, float ry, float rotDeg, bool largeArc, bool sweep, float x, float y)
        {
            float x0 = curX, y0 = curY;
            if (x0 == x && y0 == y) return this;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            if (rx < 1e-6f || ry < 1e-6f) return LineTo(x, y);
            double phi = rotDeg * Math.PI / 180, cp = Math.Cos(phi), sp = Math.Sin(phi);
            double dx = (x0 - x) / 2, dy = (y0 - y) / 2;
            double x1 = cp * dx + sp * dy, y1 = -sp * dx + cp * dy;
            double lam = x1 * x1 / (rx * (double)rx) + y1 * y1 / (ry * (double)ry);
            if (lam > 1) { double s = Math.Sqrt(lam); rx = (float)(rx * s); ry = (float)(ry * s); }
            double rx2 = rx * (double)rx, ry2 = ry * (double)ry;
            double num = rx2 * ry2 - rx2 * y1 * y1 - ry2 * x1 * x1, den = rx2 * y1 * y1 + ry2 * x1 * x1;
            double co = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den)); if (largeArc == sweep) co = -co;
            double cx1 = co * rx * y1 / ry, cy1 = -co * ry * x1 / rx;
            double cx = cp * cx1 - sp * cy1 + (x0 + x) / 2.0, cy = sp * cx1 + cp * cy1 + (y0 + y) / 2.0;
            double th1 = Math.Atan2((y1 - cy1) / ry, (x1 - cx1) / rx);
            double th2 = Math.Atan2((-y1 - cy1) / ry, (-x1 - cx1) / rx);
            double dth = th2 - th1;
            if (!sweep && dth > 0) dth -= 2 * Math.PI; else if (sweep && dth < 0) dth += 2 * Math.PI;
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 2) - 1e-6));
            double d = dth / n, t = 4.0 / 3 * Math.Tan(d / 4);
            double a0 = th1;
            for (int i = 0; i < n; i++)
            {
                double a1 = a0 + d;
                double c0 = Math.Cos(a0), s0 = Math.Sin(a0), c1 = Math.Cos(a1), s1 = Math.Sin(a1);
                // unit-circle control points, then scale / rotate / translate
                double p1x = c0 - t * s0, p1y = s0 + t * c0, p2x = c1 + t * s1, p2y = s1 - t * c1;
                double ex = i == n - 1 ? x : cp * rx * c1 - sp * ry * s1 + cx, ey = i == n - 1 ? y : sp * rx * c1 + cp * ry * s1 + cy;
                CurveTo((float)(cp * rx * p1x - sp * ry * p1y + cx), (float)(sp * rx * p1x + cp * ry * p1y + cy),
                        (float)(cp * rx * p2x - sp * ry * p2y + cx), (float)(sp * rx * p2x + cp * ry * p2y + cy), (float)ex, (float)ey);
                a0 = a1;
            }
            curX = x; curY = y;
            return this;
        }
        /// <summary>Circular arc around a centre (degrees, clockwise-positive in the y-down system), continuing the current sub-path with a line to its start.</summary>
        public VectorPath ArcAround(float cx, float cy, float r, float startDeg, float sweepDeg, bool lineToStart = true)
        {
            double a0 = startDeg * Math.PI / 180, sw = sweepDeg * Math.PI / 180;
            float sx = (float)(cx + r * Math.Cos(a0)), sy = (float)(cy + r * Math.Sin(a0));
            if (!open) MoveTo(sx, sy); else if (lineToStart) LineTo(sx, sy); else MoveTo(sx, sy);
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sw) / (Math.PI / 2) - 1e-6));
            double d = sw / n, t = 4.0 / 3 * Math.Tan(d / 4);
            for (int i = 0; i < n; i++)
            {
                double a1 = a0 + d, c0 = Math.Cos(a0), s0 = Math.Sin(a0), c1 = Math.Cos(a1), s1 = Math.Sin(a1);
                CurveTo((float)(cx + r * (c0 - t * s0)), (float)(cy + r * (s0 + t * c0)), (float)(cx + r * (c1 + t * s1)), (float)(cy + r * (s1 - t * c1)), (float)(cx + r * c1), (float)(cy + r * s1));
                a0 = a1;
            }
            return this;
        }
        public VectorPath Rect(float x, float y, float w, float h) => MoveTo(x, y).LineTo(x + w, y).LineTo(x + w, y + h).LineTo(x, y + h).Close();
        public VectorPath RoundRect(float x, float y, float w, float h, float rx, float ry)
        {
            rx = Math.Min(Math.Abs(rx), w / 2); ry = Math.Min(Math.Abs(ry), h / 2);
            if (rx <= 0 || ry <= 0) return Rect(x, y, w, h);
            const float k = 0.5522848f; float kx = rx * k, ky = ry * k;
            MoveTo(x + rx, y).LineTo(x + w - rx, y).CurveTo(x + w - rx + kx, y, x + w, y + ry - ky, x + w, y + ry)
            .LineTo(x + w, y + h - ry).CurveTo(x + w, y + h - ry + ky, x + w - rx + kx, y + h, x + w - rx, y + h)
            .LineTo(x + rx, y + h).CurveTo(x + rx - kx, y + h, x, y + h - ry + ky, x, y + h - ry)
            .LineTo(x, y + ry).CurveTo(x, y + ry - ky, x + rx - kx, y, x + rx, y).Close();
            return this;
        }
        public VectorPath Ellipse(float cx, float cy, float rx, float ry)
        {
            const float k = 0.5522848f; float kx = rx * k, ky = ry * k;
            MoveTo(cx + rx, cy).CurveTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry).CurveTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy)
            .CurveTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry).CurveTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy).Close();
            return this;
        }
        public VectorPath Circle(float cx, float cy, float r) => Ellipse(cx, cy, r, r);
        public VectorPath Polygon(ReadOnlySpan<PointF> pts, bool close = true)
        {
            if (pts.Length == 0) return this;
            MoveTo(pts[0]); for (int i = 1; i < pts.Length; i++) LineTo(pts[i]); if (close) Close(); return this;
        }
        /// <summary>Appends another path (optionally transformed).</summary>
        /// <summary>Shifts the coordinates stored in Data[start..end) (every entry is an x, y pair) - used by the SVG text importer to anchor a chunk after layout.</summary>
        internal void Translate(float dx, float dy, int dataStart, int dataEnd)
        {
            var d = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Data);
            for (int i = dataStart; i + 1 < dataEnd && i + 1 < d.Length; i += 2) { d[i] += dx; d[i + 1] += dy; }
        }
        public VectorPath Append(VectorPath other, Matrix3x2? m = null)
        {
            var t = other.Transformed(m ?? Matrix3x2.Identity);
            Kinds.AddRange(t.Kinds); Data.AddRange(t.Data); curX = t.curX; curY = t.curY; startX = t.startX; startY = t.startY; open = t.open; return this;
        }
        /// <summary>Copies a <see cref="Sprite.PathBuilder"/> (curves and arcs are flattened by the builder at <see cref="Sprite.CurveTolerance"/>).</summary>
        public static VectorPath From(Sprite.PathBuilder b)
        {
            var p = new VectorPath(); var f = new List<PointF>();
            foreach (var (start, count, closed) in b.Flatten(f))
            {
                if (count == 0) continue;
                p.MoveTo(f[start]); for (int i = 1; i < count; i++) p.LineTo(f[start + i]); if (closed) p.Close();
            }
            return p;
        }
        public VectorPath Clone() { var p = new VectorPath(); p.Kinds.AddRange(Kinds); p.Data.AddRange(Data); p.curX = curX; p.curY = curY; p.startX = startX; p.startY = startY; p.open = open; return p; }
        /// <summary>A copy with every point transformed.</summary>
        public VectorPath Transformed(Matrix3x2 m)
        {
            var p = Clone();
            if (m.IsIdentity) return p;
            var d = p.Data;
            for (int i = 0; i < d.Count; i += 2) { var v = Vector2.Transform(new Vector2(d[i], d[i + 1]), m); d[i] = v.X; d[i + 1] = v.Y; }
            var c = Vector2.Transform(new Vector2(curX, curY), m); p.curX = c.X; p.curY = c.Y;
            var s = Vector2.Transform(new Vector2(startX, startY), m); p.startX = s.X; p.startY = s.Y;
            return p;
        }
        /// <summary>Bounding box of the control points (contains the geometry; slightly loose on curves).</summary>
        public RectangleF ControlBounds()
        {
            if (Data.Count == 0) return RectangleF.Empty;
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            for (int i = 0; i < Data.Count; i += 2) { float x = Data[i], y = Data[i + 1]; if (x < l) l = x; if (x > r) r = x; if (y < t) t = y; if (y > b) b = y; }
            return RectangleF.FromLTRB(l, t, r, b);
        }
        /// <summary>True when the path is a single axis-aligned rectangle (4 corners, optional close); returns it.</summary>
        public bool IsAxisRect(out RectangleF rect)
        {
            rect = RectangleF.Empty;
            int n = Kinds.Count; if (n < 4 || n > 6 || Kinds[0] != K.Move) return false;
            int lines = 0; for (int i = 1; i < n; i++) { if (Kinds[i] == K.Line) lines++; else if (Kinds[i] != K.Close || i != n - 1) return false; }
            if (lines != 3 && lines != 4) return false;
            Span<float> x = stackalloc float[5], y = stackalloc float[5]; int c = 0;
            for (int i = 0, d = 0; i < n; i++) { if (Kinds[i] == K.Close) continue; x[c] = Data[d]; y[c] = Data[d + 1]; c++; d += 2; }
            if (c == 5) { if (Math.Abs(x[4] - x[0]) > 1e-4f || Math.Abs(y[4] - y[0]) > 1e-4f) return false; c = 4; }
            bool a = Eq(y[0], y[1]) && Eq(x[1], x[2]) && Eq(y[2], y[3]) && Eq(x[3], x[0]);
            bool bb = Eq(x[0], x[1]) && Eq(y[1], y[2]) && Eq(x[2], x[3]) && Eq(y[3], y[0]);
            if (!a && !bb) return false;
            float l = Math.Min(Math.Min(x[0], x[1]), Math.Min(x[2], x[3])), r = Math.Max(Math.Max(x[0], x[1]), Math.Max(x[2], x[3]));
            float t = Math.Min(Math.Min(y[0], y[1]), Math.Min(y[2], y[3])), b = Math.Max(Math.Max(y[0], y[1]), Math.Max(y[2], y[3]));
            rect = RectangleF.FromLTRB(l, t, r, b); return true;
            static bool Eq(float p, float q) => Math.Abs(p - q) < 1e-4f;
        }
        /// <summary>SVG path data ("M 10 10 L ..."), absolute commands.</summary>
        public string ToSvgData()
        {
            var sb = new StringBuilder(); int d = 0; var ci = CultureInfo.InvariantCulture;
            string F(float v) => v.ToString("0.###", ci);
            foreach (var k in Kinds)
            {
                switch (k)
                {
                    case K.Move: sb.Append('M').Append(F(Data[d])).Append(' ').Append(F(Data[d + 1])); d += 2; break;
                    case K.Line: sb.Append('L').Append(F(Data[d])).Append(' ').Append(F(Data[d + 1])); d += 2; break;
                    case K.Cubic: sb.Append('C'); for (int i = 0; i < 6; i++) { if (i > 0) sb.Append(' '); sb.Append(F(Data[d + i])); } d += 6; break;
                    case K.Close: sb.Append('Z'); break;
                }
            }
            return sb.ToString();
        }
        /// <summary>Parses SVG path data (all commands, relative and absolute, arcs and quadratics converted).</summary>
        public static VectorPath ParseSvg(string d) => SvgPathParser.Parse(d, new VectorPath());
        public override string ToString() => ToSvgData();
    }

    /// <summary>A clip region: the intersection of one or more paths (each with its fill rule) in the shape's coordinate space.</summary>
    internal sealed class VectorClip
    {
        public readonly List<(VectorPath Path, bool EvenOdd)> Paths = new List<(VectorPath, bool)>();
        public VectorClip() { }
        public VectorClip(VectorPath p, bool evenOdd = false) { Paths.Add((p, evenOdd)); }
        public VectorClip Intersect(VectorPath p, bool evenOdd = false) { var c = new VectorClip(); c.Paths.AddRange(Paths); c.Paths.Add((p, evenOdd)); return c; }
        public VectorClip Transformed(Matrix3x2 m) { var c = new VectorClip(); foreach (var (p, eo) in Paths) c.Paths.Add((p.Transformed(m), eo)); return c; }
        /// <summary>True when every clip path is an axis-aligned rectangle; returns their intersection.</summary>
        public bool IsRect(out RectangleF rect)
        {
            rect = new RectangleF(float.MinValue / 2, float.MinValue / 2, float.MaxValue, float.MaxValue);
            foreach (var (p, _) in Paths) { if (!p.IsAxisRect(out var r)) return false; rect = RectangleF.Intersect(rect, r); }
            return true;
        }
    }

    /// <summary>One drawable item: a path with fill and stroke properties, its own transform and an optional clip.</summary>
    /// <summary>What a text shape was made of: its Unicode text, the baseline (start -> end, image coordinates before the shape's
    /// Transform), the font size in image units and the advance box corners (Quad: baseline-left, baseline-right, top-right,
    /// top-left). <see cref="VectorShape.Tag"/> of shapes with <see cref="VectorShape.IsText"/>; ToString() returns the text.</summary>
    internal sealed class VectorTextRun
    {
        public string Text = ""; public PointF Start, End; public float Size; public string? FontName; public PointF[] Quad = Array.Empty<PointF>();
        public RectangleF Bounds { get { if (Quad.Length < 4) return RectangleF.Empty; float l = Quad[0].X, r = l, t = Quad[0].Y, b = t; foreach (var q in Quad) { l = Math.Min(l, q.X); r = Math.Max(r, q.X); t = Math.Min(t, q.Y); b = Math.Max(b, q.Y); } return RectangleF.FromLTRB(l, t, r, b); } }
        public override string ToString() => Text;
    }
    internal sealed class VectorShape
    {
        public VectorPath Path = new VectorPath();
        public VectorPaint? Fill = VectorColor.Black;
        public VectorPaint? Stroke;
        public bool EvenOdd;
        public float StrokeWidth = 1f;
        public VectorCap Cap = VectorCap.Butt;
        public VectorJoin Join = VectorJoin.Miter;
        public float MiterLimit = 4f;
        public float[]? Dash; public float DashOffset;
        /// <summary>Multiplies the alpha of fill and stroke (0..1).</summary>
        public float Opacity = 1f;
        /// <summary>Local -> image coordinates.</summary>
        public Matrix3x2 Transform = Matrix3x2.Identity;
        public VectorClip? Clip;
        /// <summary>Name of the shape: the SVG id (a group's id reaches children without one as "group/child#"), or whatever you assign. Used by the name-based edits in Vector.Edit.cs.</summary>
        public string? Id;
        /// <summary>Ids of the named groups the shape came from, outermost first, "/"-separated (SVG &lt;g id&gt; nesting); null when none. Name lookups match any segment.</summary>
        public string? Group;
        /// <summary>Hidden shapes stay in the container (with their name) but are not drawn, hit-tested or included in bounds; saved as display:none.</summary>
        public bool Hidden;
        /// <summary>Set by the importers: the shape is glyph outlines of a text run (its Unicode text is in <see cref="Tag"/>) / a placed raster image.</summary>
        public bool IsText, IsImage;
        public object? Tag { get; set; }

        public VectorShape() { }
        public VectorShape(VectorPath path, VectorPaint? fill, VectorPaint? stroke = null, float strokeWidth = 1f) { Path = path; Fill = fill; Stroke = stroke; StrokeWidth = strokeWidth; }
        public bool IsVisible => !Hidden && (Fill != null || Stroke != null) && Opacity > 0 && !Path.IsEmpty;
        public VectorShape Clone()
        {
            var s = (VectorShape)MemberwiseClone();
            s.Path = Path.Clone(); s.Fill = Fill?.Clone(); s.Stroke = Stroke?.Clone(); s.Dash = Dash == null ? null : (float[])Dash.Clone();
            return s;
        }
        /// <summary>Bakes <see cref="Transform"/> (and an extra matrix) into the coordinates: path, clip, gradient matrices, stroke width (by the mean scale).</summary>
        public VectorShape Bake(Matrix3x2? extra = null)
        {
            var m = extra.HasValue ? Transform * extra.Value : Transform;
            if (m.IsIdentity) return this;
            Path = Path.Transformed(m); Clip = Clip?.Transformed(m);
            float s = VectorRender.MeanScale(m); StrokeWidth *= s;
            if (Dash != null) for (int i = 0; i < Dash.Length; i++) Dash[i] *= s; DashOffset *= s;
            if (Fill is VectorGradient fg && !fg.ObjectBoundingBox) fg.Matrix *= m;
            if (Stroke is VectorGradient sg && !sg.ObjectBoundingBox) sg.Matrix *= m;
            if (Fill is VectorImagePaint fi) fi.Matrix *= m;
            if (Stroke is VectorImagePaint si) si.Matrix *= m;
            Transform = Matrix3x2.Identity;
            return this;
        }
    }

    /// <summary>Which importer <see cref="VectorImage.Load(string)"/> used.</summary>
    internal enum VectorFormat { Unknown, Svg, PostScript, Pdf }

    /// <summary>
    /// A container of vector shapes with an intrinsic size (<see cref="Width"/> x <see cref="Height"/> = the view box). See the
    /// file header for the idea; drawing is in Vector.Render.cs.
    /// </summary>
    internal sealed partial class VectorImage
    {
        public readonly List<VectorShape> Shapes = new List<VectorShape>();
        /// <summary>User-space rectangle the picture lives in (SVG viewBox, EPS BoundingBox, PDF page box moved to the origin).</summary>
        public RectangleF ViewBox;
        /// <summary>Intrinsic size in pixels / points (drawn at scale 1 the view box covers Width x Height).</summary>
        public float Width, Height;
        /// <summary>What the importer could not turn into shapes (text, raster images, unsupported operators) - one line each.</summary>
        public readonly List<string> Warnings = new List<string>();
        public VectorFormat Format;
        public string? Title;
        /// <summary>PDF only: number of pages in the document the image came from.</summary>
        public int PageCount = 1;
        /// <summary>Optional visible area (image units): only what lies inside is drawn / hit / bounded; the view box and the
        /// shapes are untouched (a render-level clip, not a geometry edit). Null = everything. Set with <see cref="SetVisibleArea(RectangleF,bool)"/>,
        /// bake it into the view box with <see cref="CropToVisibleArea"/>.</summary>
        public RectangleF? VisibleArea { get => _visible; set { _visible = value; Touch(); } }
        RectangleF? _visible;

        public VectorImage() { }
        public VectorImage(float width, float height) { Width = width; Height = height; ViewBox = new RectangleF(0, 0, width, height); }

        public int Count => Shapes.Count;
        public bool IsEmpty => Shapes.Count == 0;

        // ------------------------------------------------------------------ loading
        /// <summary>Loads .svg / .svgz, .eps / .ps / .ai (PostScript), .pdf; the content is sniffed when the extension does not tell.</summary>
        public static VectorImage Load(string file, int page = 0) => Load(File.ReadAllBytes(file), Path.GetExtension(file), page);
        public static VectorImage Load(byte[] data, string? hint = null, int page = 0)
        {
            hint = (hint ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B)
            {   // gzip (svgz)
                using var ms = new MemoryStream(data); using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress); using var outp = new MemoryStream();
                gz.CopyTo(outp); data = outp.ToArray();
            }
            var fmt = Sniff(data, hint);
            switch (fmt)
            {
                case VectorFormat.Pdf: return FromPdf(data, page);
                case VectorFormat.PostScript: return FromPostScript(data);
                case VectorFormat.Svg: return FromSvg(Encoding.UTF8.GetString(data));
                default: throw new NotSupportedException("Not an SVG, PostScript / EPS or PDF file.");
            }
        }
        public static VectorFormat Sniff(byte[] data, string? hint = null)
        {
            if (data.Length >= 4)
            {
                if (data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F') return VectorFormat.Pdf;
                if (data[0] == 0xC5 && data[1] == 0xD0 && data[2] == 0xD3 && data[3] == 0xC6) return VectorFormat.PostScript;   // DOS EPS binary header
                if (data[0] == '%' && data[1] == '!') return VectorFormat.PostScript;
            }
            int n = Math.Min(data.Length, 4096); string head = Encoding.ASCII.GetString(data, 0, n);
            if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase) || head.TrimStart().StartsWith("<?xml", StringComparison.Ordinal) || head.TrimStart().StartsWith('<')) return VectorFormat.Svg;
            int pdf = head.IndexOf("%PDF", StringComparison.Ordinal); if (pdf >= 0 && pdf < 1024) return VectorFormat.Pdf;
            if (head.Contains("%!PS", StringComparison.Ordinal)) return VectorFormat.PostScript;
            return hint switch { "svg" or "svgz" => VectorFormat.Svg, "pdf" => VectorFormat.Pdf, "eps" or "ps" or "ai" or "epsf" => VectorFormat.PostScript, _ => VectorFormat.Unknown };
        }
        public static VectorImage FromSvg(string xml) { var img = SvgReader.Read(xml); img.Format = VectorFormat.Svg; return img; }
        public static VectorImage FromPostScript(byte[] ps) { var img = PsReader.Read(ps); img.Format = VectorFormat.PostScript; return img; }
        public static VectorImage FromPostScript(string ps) => FromPostScript(Encoding.Latin1.GetBytes(ps));
        public static VectorImage FromPdf(byte[] pdf, int page = 0) { var img = PdfReader.Read(pdf, page); img.Format = VectorFormat.Pdf; return img; }
        public static VectorImage FromPdf(string file, int page = 0) => FromPdf(File.ReadAllBytes(file), page);
        /// <summary>Number of pages of a PDF without rendering one.</summary>
        public static int PdfPageCount(byte[] pdf) => PdfReader.PageCount(pdf);
        public static int PdfPageCount(string file) => PdfReader.PageCount(File.ReadAllBytes(file));

        // ------------------------------------------------------------------ composition
        public VectorImage Add(VectorShape s) { Shapes.Add(s); Touch(); return this; }
        /// <summary>Adds a path with a solid fill / stroke.</summary>
        public VectorShape Add(VectorPath path, int fillArgb, int strokeArgb = 0, float strokeWidth = 1f)
        {
            var s = new VectorShape(path, (fillArgb >> 24 & 255) != 0 ? new VectorColor(fillArgb) : null, (strokeArgb >> 24 & 255) != 0 ? new VectorColor(strokeArgb) : null, strokeWidth);
            Shapes.Add(s); Touch(); return s;
        }
        public VectorShape Add(Sprite.PathBuilder path, int fillArgb, int strokeArgb = 0, float strokeWidth = 1f) => Add(VectorPath.From(path), fillArgb, strokeArgb, strokeWidth);
        /// <summary>Adds all shapes of another image, placed with <paramref name="m"/> (its view-box origin -> m). Shapes are copied.</summary>
        public VectorImage Add(VectorImage other, Matrix3x2 m)
        {
            var shift = Matrix3x2.CreateTranslation(-other.ViewBox.X, -other.ViewBox.Y) * m;
            foreach (var s in other.Shapes) { var c = s.Clone(); c.Transform = s.Transform * shift; Shapes.Add(c); }
            foreach (var w in other.Warnings) if (!Warnings.Contains(w)) Warnings.Add(w);
            Touch(); return this;
        }
        /// <summary>Adds another image scaled into <paramref name="into"/> (aspect kept, centred) - the easy "compose" call.</summary>
        public VectorImage Add(VectorImage other, RectangleF into, bool keepAspect = true) => Add(other, other.FitMatrix(into, keepAspect) * Matrix3x2.CreateTranslation(other.ViewBox.X, other.ViewBox.Y));
        /// <summary>Adds a copy at (x, y), scaled and rotated (degrees) around the other image's view-box centre.</summary>
        public VectorImage Add(VectorImage other, float x, float y, float scale = 1f, float angleDeg = 0f)
        {
            float cx = other.ViewBox.Width / 2, cy = other.ViewBox.Height / 2;
            var m = Matrix3x2.CreateTranslation(-cx, -cy) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(angleDeg * MathF.PI / 180) * Matrix3x2.CreateTranslation(x, y);
            return Add(other, m);
        }
        /// <summary>Bakes <paramref name="m"/> into every shape; the view box follows.</summary>
        public VectorImage Transform(Matrix3x2 m)
        {
            foreach (var s in Shapes) s.Bake(m);
            ViewBox = VectorRender.TransformRect(ViewBox, m); Width = ViewBox.Width; Height = ViewBox.Height;
            if (_visible.HasValue) _visible = VectorRender.TransformRect(_visible.Value, m);
            Touch(); return this;
        }
        /// <summary>Bakes every shape transform into its path (Transform = identity everywhere).</summary>
        public VectorImage Flatten() { foreach (var s in Shapes) s.Bake(); Touch(); return this; }
        public VectorImage Clone()
        {
            var c = new VectorImage { ViewBox = ViewBox, Width = Width, Height = Height, Format = Format, Title = Title, PageCount = PageCount, _visible = _visible };
            foreach (var s in Shapes) c.Shapes.Add(s.Clone()); c.Warnings.AddRange(Warnings); return c;
        }
        /// <summary>Sets the view box to the geometry bounds (drops empty margins), keeping the scale.</summary>
        public VectorImage Crop(float margin = 0)
        {
            var b = Bounds(); if (b.IsEmpty) return this;
            b.Inflate(margin, margin); ViewBox = b; Width = b.Width; Height = b.Height; Touch(); return this;
        }
        /// <summary>Removes shapes that draw nothing (empty paths, no paint, opacity 0 - and hidden ones; use RemoveHidden for those alone).</summary>
        public VectorImage Prune() { Shapes.RemoveAll(s => !s.IsVisible); Touch(); return this; }

        // ------------------------------------------------------------------ geometry
        /// <summary>Bounding box of everything that would be painted (image coordinates; stroke width included).</summary>
        public RectangleF Bounds()
        {
            bool any = false; float l = 0, t = 0, r = 0, b = 0;
            foreach (var s in Shapes)
            {
                if (!s.IsVisible) continue;
                var rb = VectorRender.ShapeBounds(s, Matrix3x2.Identity); if (rb.IsEmpty) continue;
                if (!any) { l = rb.Left; t = rb.Top; r = rb.Right; b = rb.Bottom; any = true; }
                else { l = Math.Min(l, rb.Left); t = Math.Min(t, rb.Top); r = Math.Max(r, rb.Right); b = Math.Max(b, rb.Bottom); }
            }
            if (!any) return RectangleF.Empty;
            var box = RectangleF.FromLTRB(l, t, r, b);
            if (_visible.HasValue) { box = RectangleF.Intersect(box, _visible.Value); if (box.Width <= 0 || box.Height <= 0) return RectangleF.Empty; }
            return box;
        }
        /// <summary>Bounding box of the shapes called <paramref name="name"/> (hidden ones included when <paramref name="includeHidden"/>; the visible area does not limit it); Empty when none match.</summary>
        public RectangleF BoundsOf(string name, bool includeHidden = true)
        {
            bool any = false; float l = 0, t = 0, r = 0, b = 0;
            foreach (var s in Shapes)
            {
                if (!NameMatches(s, name) || (!includeHidden && s.Hidden) || s.Path.IsEmpty) continue;
                var rb = VectorRender.ShapeBounds(s, Matrix3x2.Identity); if (rb.IsEmpty) continue;
                if (!any) { l = rb.Left; t = rb.Top; r = rb.Right; b = rb.Bottom; any = true; }
                else { l = Math.Min(l, rb.Left); t = Math.Min(t, rb.Top); r = Math.Max(r, rb.Right); b = Math.Max(b, rb.Bottom); }
            }
            return any ? RectangleF.FromLTRB(l, t, r, b) : RectangleF.Empty;
        }

        // ------------------------------------------------------------------ visible area (render-level window)
        /// <summary>
        /// Shows only the part of the picture inside <paramref name="area"/> (image units, same space as <see cref="ViewBox"/>).
        /// Nothing is cut: shapes, names and the view box stay as they are, the renderer just does not paint outside the
        /// window (a lock rect when the picture is drawn axis-aligned, a coverage mask when rotated / sheared) and
        /// <see cref="Bounds"/> / <see cref="HitTest"/> / <see cref="VectorSprite"/> respect it. <paramref name="crop"/> = also
        /// call <see cref="CropToVisibleArea"/> so the container's Width / Height become the window's.
        /// </summary>
        public VectorImage SetVisibleArea(RectangleF area, bool crop = false)
        {
            if (area.Width <= 0 || area.Height <= 0) throw new ArgumentException("the visible area needs a positive size", nameof(area));
            _visible = area; Touch();
            return crop ? CropToVisibleArea() : this;
        }
        public VectorImage SetVisibleArea(float x, float y, float width, float height, bool crop = false) => SetVisibleArea(new RectangleF(x, y, width, height), crop);
        /// <summary>
        /// Visible area = the bounding box of the object(s) called <paramref name="name"/> (Id / group, wildcards; hidden
        /// shapes count too, so you can hide the frame object and window to where it was). <paramref name="margin"/> grows
        /// the box (image units, negative shrinks). Returns false when no shape matches (nothing changes).
        /// </summary>
        public bool SetVisibleArea(string name, bool crop = false, float margin = 0)
        {
            var b = BoundsOf(name, includeHidden: true);
            if (b.IsEmpty) return false;
            b.Inflate(margin, margin);
            if (b.Width <= 0 || b.Height <= 0) return false;
            SetVisibleArea(b, crop); return true;
        }
        /// <summary>Removes the visible-area window: the whole picture shows again (the view box is whatever it is now).</summary>
        public VectorImage ClearVisibleArea() { if (_visible.HasValue) { _visible = null; Touch(); } return this; }
        /// <summary>
        /// Makes the visible area the picture's size: the view box becomes the window (Width / Height follow), so
        /// Draw / DrawFit / Rasterize / SaveSvg all treat the window as the whole picture. The window itself stays in
        /// force (things outside the new view box still do not paint when you draw with a matrix that would show them);
        /// <see cref="ClearVisibleArea"/> afterwards keeps the new view box but lets everything paint again.
        /// Coordinates are not moved: shape coordinates and the view box origin stay in the original space.
        /// </summary>
        public VectorImage CropToVisibleArea()
        {
            if (!_visible.HasValue) return this;
            var v = _visible.Value; ViewBox = v; Width = v.Width; Height = v.Height; Touch(); return this;
        }
        /// <summary>Matrix that maps the view box into <paramref name="into"/> (aspect kept and centred, or stretched).</summary>
        public Matrix3x2 FitMatrix(RectangleF into, bool keepAspect = true)
        {
            float vw = Math.Max(1e-6f, ViewBox.Width), vh = Math.Max(1e-6f, ViewBox.Height);
            float sx = into.Width / vw, sy = into.Height / vh;
            if (keepAspect) sx = sy = Math.Min(sx, sy);
            float ox = into.X + (into.Width - vw * sx) / 2, oy = into.Y + (into.Height - vh * sy) / 2;
            return Matrix3x2.CreateTranslation(-ViewBox.X, -ViewBox.Y) * Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(ox, oy);
        }
        /// <summary>Matrix for the simple Draw call: view-box pivot (fractions, 0.5/0.5 = centre) placed at (x, y), scaled, rotated (degrees, clockwise on screen).</summary>
        public Matrix3x2 PlaceMatrix(float x, float y, float scaleX = 1f, float scaleY = 1f, float angleDeg = 0f, float pivotX = 0.5f, float pivotY = 0.5f)
        {
            float px = ViewBox.X + ViewBox.Width * pivotX, py = ViewBox.Y + ViewBox.Height * pivotY;
            var m = Matrix3x2.CreateTranslation(-px, -py) * Matrix3x2.CreateScale(scaleX, scaleY);
            if (angleDeg != 0) m *= Matrix3x2.CreateRotation(angleDeg * MathF.PI / 180);
            return m * Matrix3x2.CreateTranslation(x, y);
        }

        // ------------------------------------------------------------------ export
        /// <summary>Writes the picture as an SVG document (paths, solid and gradient paints, strokes, dashes, opacity, clips).</summary>
        public string ToSvg()
        {
            var ci = CultureInfo.InvariantCulture; var sb = new StringBuilder(); var defs = new StringBuilder(); int nid = 0;
            string F(float v) => v.ToString("0.####", ci);
            string Col(int argb) => $"#{argb & 0xFFFFFF:X6}";
            string Mat(Matrix3x2 m) => m.IsIdentity ? "" : $"matrix({F(m.M11)} {F(m.M12)} {F(m.M21)} {F(m.M22)} {F(m.M31)} {F(m.M32)})";
            string Paint(VectorPaint? p, string attr, StringBuilder o)
            {
                if (p == null) { o.Append(ci, $" {attr}=\"none\""); return ""; }
                if (p is VectorColor c) { o.Append(ci, $" {attr}=\"{Col(c.Argb)}\""); if (c.A != 255) o.Append(ci, $" {attr}-opacity=\"{(c.A / 255f).ToString("0.###", ci)}\""); return ""; }
                if (p is VectorImagePaint ip)
                {   // pattern with an embedded PNG (image space -> shape space through patternTransform)
                    string pid = $"i{++nid}";
                    defs.Append(ci, $"<pattern id=\"{pid}\" patternUnits=\"userSpaceOnUse\" width=\"{ip.Width}\" height=\"{ip.Height}\"{(ip.Matrix.IsIdentity ? "" : $" patternTransform=\"{Mat(ip.Matrix)}\"")}><image width=\"{ip.Width}\" height=\"{ip.Height}\" preserveAspectRatio=\"none\"{(ip.Smooth ? "" : " image-rendering=\"pixelated\"")} href=\"data:image/png;base64,{System.Convert.ToBase64String(ip.ToPng())}\"/></pattern>");
                    o.Append(ci, $" {attr}=\"url(#{pid})\""); return pid;
                }
                var g = (VectorGradient)p; string id = $"g{++nid}";
                defs.Append(g.Radial ? $"<radialGradient id=\"{id}\" cx=\"{F(g.Cx)}\" cy=\"{F(g.Cy)}\" r=\"{F(g.R)}\" fx=\"{F(g.Fx)}\" fy=\"{F(g.Fy)}\"" : $"<linearGradient id=\"{id}\" x1=\"{F(g.X1)}\" y1=\"{F(g.Y1)}\" x2=\"{F(g.X2)}\" y2=\"{F(g.Y2)}\"");
                if (g.Radial && g.Fr > 0) defs.Append(ci, $" fr=\"{F(g.Fr)}\"");
                defs.Append(ci, $" gradientUnits=\"{(g.ObjectBoundingBox ? "objectBoundingBox" : "userSpaceOnUse")}\"");
                if (!g.Matrix.IsIdentity) defs.Append(ci, $" gradientTransform=\"{Mat(g.Matrix)}\"");
                if (g.Spread != VectorSpread.Pad) defs.Append(ci, $" spreadMethod=\"{g.Spread.ToString().ToLowerInvariant()}\"");
                defs.Append('>');
                foreach (var s in g.Stops) defs.Append(ci, $"<stop offset=\"{F(s.Offset)}\" stop-color=\"{Col(s.Argb)}\" stop-opacity=\"{((s.Argb >> 24 & 255) / 255f).ToString("0.###", ci)}\"/>");
                defs.Append(g.Radial ? "</radialGradient>" : "</linearGradient>");
                o.Append(ci, $" {attr}=\"url(#{id})\""); return id;
            }
            sb.Append(ci, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(Width)}\" height=\"{F(Height)}\" viewBox=\"{F(ViewBox.X)} {F(ViewBox.Y)} {F(ViewBox.Width)} {F(ViewBox.Height)}\"");
            if (_visible is RectangleF va) sb.Append(ci, $" data-visible-area=\"{F(va.X)} {F(va.Y)} {F(va.Width)} {F(va.Height)}\"");   // SR2D's window (a private attribute other viewers ignore); the reader takes it back
            sb.Append('>');
            if (Title != null) sb.Append("<title>").Append(System.Security.SecurityElement.Escape(Title)).Append("</title>");
            var body = new StringBuilder();
            foreach (var s in Shapes)
            {
                if (!s.IsVisible && !s.Hidden) continue;    // hidden shapes are written (display="none") so a round trip keeps them
                string clipAttr = "";
                if (s.Clip != null && s.Clip.Paths.Count > 0)
                {
                    string cid = $"c{++nid}"; defs.Append(ci, $"<clipPath id=\"{cid}\">");
                    // nested clips are intersected by chaining clip-path references
                    for (int i = 0; i < s.Clip.Paths.Count; i++)
                    {
                        var (p, eo) = s.Clip.Paths[i];
                        if (i == 0) defs.Append(ci, $"<path d=\"{p.ToSvgData()}\" clip-rule=\"{(eo ? "evenodd" : "nonzero")}\"/>");
                        else { string inner = $"c{++nid}"; defs.Append(ci, $"</clipPath><clipPath id=\"{inner}\" clip-path=\"url(#{cid})\"><path d=\"{p.ToSvgData()}\" clip-rule=\"{(eo ? "evenodd" : "nonzero")}\"/>"); cid = inner; }
                    }
                    defs.Append("</clipPath>"); clipAttr = $" clip-path=\"url(#{cid})\"";
                }
                body.Append("<path d=\"").Append(s.Path.ToSvgData()).Append('"');
                Paint(s.Fill, "fill", body); Paint(s.Stroke, "stroke", body);
                if (s.EvenOdd) body.Append(" fill-rule=\"evenodd\"");
                if (s.Stroke != null)
                {
                    body.Append(ci, $" stroke-width=\"{F(s.StrokeWidth)}\"");
                    if (s.Cap != VectorCap.Butt) body.Append(ci, $" stroke-linecap=\"{s.Cap.ToString().ToLowerInvariant()}\"");
                    if (s.Join != VectorJoin.Miter) body.Append(ci, $" stroke-linejoin=\"{s.Join.ToString().ToLowerInvariant()}\"");
                    if (Math.Abs(s.MiterLimit - 4f) > 1e-3f) body.Append(ci, $" stroke-miterlimit=\"{F(s.MiterLimit)}\"");
                    if (s.Dash != null && s.Dash.Length > 0) { body.Append(" stroke-dasharray=\""); for (int i = 0; i < s.Dash.Length; i++) { if (i > 0) body.Append(' '); body.Append(F(s.Dash[i])); } body.Append('"'); if (s.DashOffset != 0) body.Append(ci, $" stroke-dashoffset=\"{F(s.DashOffset)}\""); }
                }
                if (s.Opacity < 1) body.Append(ci, $" opacity=\"{F(s.Opacity)}\"");
                if (s.Hidden) body.Append(" display=\"none\"");
                if (!s.Transform.IsIdentity) body.Append(ci, $" transform=\"{Mat(s.Transform)}\"");
                body.Append(clipAttr);
                if (s.Id != null) body.Append(ci, $" id=\"{System.Security.SecurityElement.Escape(s.Id)}\"");
                body.Append("/>");
            }
            if (defs.Length > 0) sb.Append("<defs>").Append(defs).Append("</defs>");
            sb.Append(body).Append("</svg>");
            return sb.ToString();
        }
        public void SaveSvg(string file) => File.WriteAllText(file, ToSvg(), new UTF8Encoding(false));

        public override string ToString() => $"VectorImage {Width:0.#}x{Height:0.#} ({Shapes.Count} shapes{(Warnings.Count > 0 ? $", {Warnings.Count} warnings" : "")})";
    }

    /// <summary>SVG path-data parser (shared by the SVG reader and <see cref="VectorPath.ParseSvg"/>).</summary>
    internal static class SvgPathParser
    {
        public static VectorPath Parse(string d, VectorPath p)
        {
            int i = 0, n = d.Length; char cmd = '\0'; float cx = 0, cy = 0, lcx = 0, lcy = 0; char lastCmd = '\0';
            bool Num(out float v)
            {
                v = 0; while (i < n && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
                if (i >= n) return false;
                int s = i; if (d[i] == '+' || d[i] == '-') i++;
                bool dig = false, dot = false;
                while (i < n) { char c = d[i]; if (char.IsDigit(c)) { dig = true; i++; } else if (c == '.' && !dot) { dot = true; i++; } else break; }
                if (i < n && (d[i] == 'e' || d[i] == 'E')) { int e = i; i++; if (i < n && (d[i] == '+' || d[i] == '-')) i++; if (i < n && char.IsDigit(d[i])) { while (i < n && char.IsDigit(d[i])) i++; } else i = e; }
                if (!dig) { i = s; return false; }
                v = float.Parse(d.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture); return true;
            }
            bool Flag(out bool f)
            {
                f = false; while (i < n && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
                if (i >= n || (d[i] != '0' && d[i] != '1')) return false; f = d[i++] == '1'; return true;
            }
            while (true)
            {
                while (i < n && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
                if (i >= n) break;
                char c = d[i];
                if (char.IsLetter(c)) { cmd = c; i++; }
                else if (cmd == '\0' || cmd == 'Z' || cmd == 'z') break;   // Z takes no operands: anything else here is garbage, not an implicit repeat (would loop forever)
                else if (cmd == 'M') cmd = 'L'; else if (cmd == 'm') cmd = 'l';
                bool rel = char.IsLower(cmd); char C = char.ToUpperInvariant(cmd);
                float x, y, x1, y1, x2, y2;
                switch (C)
                {
                    case 'M': if (!Num(out x) || !Num(out y)) return p; if (rel) { x += cx; y += cy; } p.MoveTo(x, y); cx = x; cy = y; break;
                    case 'L': if (!Num(out x) || !Num(out y)) return p; if (rel) { x += cx; y += cy; } p.LineTo(x, y); cx = x; cy = y; break;
                    case 'H': if (!Num(out x)) return p; if (rel) x += cx; p.LineTo(x, cy); cx = x; break;
                    case 'V': if (!Num(out y)) return p; if (rel) y += cy; p.LineTo(cx, y); cy = y; break;
                    case 'C':
                        if (!Num(out x1) || !Num(out y1) || !Num(out x2) || !Num(out y2) || !Num(out x) || !Num(out y)) return p;
                        if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                        p.CurveTo(x1, y1, x2, y2, x, y); lcx = x2; lcy = y2; cx = x; cy = y; break;
                    case 'S':
                        if (!Num(out x2) || !Num(out y2) || !Num(out x) || !Num(out y)) return p;
                        if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                        if (lastCmd == 'C' || lastCmd == 'S') { x1 = 2 * cx - lcx; y1 = 2 * cy - lcy; } else { x1 = cx; y1 = cy; }
                        p.CurveTo(x1, y1, x2, y2, x, y); lcx = x2; lcy = y2; cx = x; cy = y; break;
                    case 'Q':
                        if (!Num(out x1) || !Num(out y1) || !Num(out x) || !Num(out y)) return p;
                        if (rel) { x1 += cx; y1 += cy; x += cx; y += cy; }
                        p.QuadTo(x1, y1, x, y); lcx = x1; lcy = y1; cx = x; cy = y; break;
                    case 'T':
                        if (!Num(out x) || !Num(out y)) return p;
                        if (rel) { x += cx; y += cy; }
                        if (lastCmd == 'Q' || lastCmd == 'T') { x1 = 2 * cx - lcx; y1 = 2 * cy - lcy; } else { x1 = cx; y1 = cy; }
                        p.QuadTo(x1, y1, x, y); lcx = x1; lcy = y1; cx = x; cy = y; break;
                    case 'A':
                        {
                            if (!Num(out float rx) || !Num(out float ry) || !Num(out float rot) || !Flag(out bool la) || !Flag(out bool sw) || !Num(out x) || !Num(out y)) return p;
                            if (rel) { x += cx; y += cy; }
                            p.ArcTo(rx, ry, rot, la, sw, x, y); cx = x; cy = y; break;
                        }
                    case 'Z': p.Close(); var s0 = p.SubPathStart; cx = s0.X; cy = s0.Y; break;
                    default: return p;
                }
                lastCmd = C;
            }
            return p;
        }
    }
}
