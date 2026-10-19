// VectorText.cs - text drawn from open strokes, no font files.
// Glyph data: the "VectorAngular" font (glyph outlines are straight polylines, in font units, y up, baseline 0,
// cap height 70, x-height 50, descender -20). The data is the font's own table, exported from its page
// (Latin, digits, punctuation, Cyrillic). Drawing rounds each interior corner with a quadratic curve
// (Radius), can keep acute tips sharp (TipDrop), and can use one advance per glyph (Monospace).
// Strokes take a join (Bevel by default, as in the font file), a cap, and an outline position (centre, inside or outside).
// Bold = a wider stroke, Italic = a skew (x += 0.2 * y). Curves are flattened to short segments, so the text
// can be bent along a path (map labels along roads and rivers) with the same code as straight text.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Sr2d64CSport
{
    /// <summary>How text is drawn. Size is the cap height in image units.</summary>
    public sealed class VectorTextStyle
    {
        public bool Bold;
        public bool Italic;
        /// <summary>ARGB colour of the strokes.</summary>
        public int Color = unchecked((int)0xFF000000);
        /// <summary>Stroke width as a fraction of Size (regular 6/70 = 0.086; bold 0.16). 0 = the default.</summary>
        public float Weight;
        /// <summary>Space between glyphs as a fraction of Size.</summary>
        public float Tracking = 0.1f;
        /// <summary>Corner rounding radius in font units (cap height = 70). 0 = angular corners. Default 6 (the font's own).</summary>
        public float Radius = 6f;
        /// <summary>Tip limit in font units: an acute corner at the top or bottom of a stroke is lowered by at most this.
        /// 0 keeps such tips sharp (the font's default).</summary>
        public float TipDrop;
        /// <summary>One advance (60 units) for every glyph, wide glyphs squeezed to 46 units, glyphs centred.</summary>
        public bool Monospace;
        /// <summary>Corner join of the strokes. Bevel is the font's default; Miter and Round are also available.
        /// Angular corners (Radius 0) show it most.</summary>
        public VectorJoin Join = VectorJoin.Bevel;
        /// <summary>Cap at the ends of open strokes. Butt is the font's default; Round and Square are also available.</summary>
        public VectorCap Cap = VectorCap.Butt;
        /// <summary>Where a closed outline's stroke sits: Center (on the outline, the default), Inside or Outside.
        /// Open strokes (the bars of A, the stem of T) are always centred.</summary>
        public VectorOutlinePosition OutlinePosition = VectorOutlinePosition.Center;

        internal float StrokeFraction => Weight > 0 ? Weight : (Bold ? 0.16f : 6f / 70f);
    }

    /// <summary>Where the stroke of a closed outline sits relative to the outline.</summary>
    public enum VectorOutlinePosition { Center, Inside, Outside }

    /// <summary>Text along a path.</summary>
    public sealed class VectorTextPathOptions
    {
        public VectorTextStyle Style = new VectorTextStyle();
        /// <summary>Where the text starts, as a distance along the path from its start (image units).</summary>
        public float Offset;
        /// <summary>Start, Center or End: where the text sits along the path, after Offset.</summary>
        public VectorTextAlign Align = VectorTextAlign.Start;
        /// <summary>True: text above the path (screen up from the line); false: hanging below it. The glyphs stay upright in both cases.</summary>
        public bool Above = true;
        /// <summary>True: the text is stretched or squeezed along the path so it spans it exactly (Offset is then 0).</summary>
        public bool FitToPath;
    }

    public enum VectorTextAlign { Start, Center, End }

    /// <summary>Stroke-font text as a <see cref="VectorImage"/>, and text bent along a polyline.</summary>
    internal static class VectorText
    {
        const float CapHeight = 70f;          // font units
        const float SideBearing = 5f;         // font units, each side of a glyph
        const float SpaceAdvance = 30f;
        const float MonoAdvance = 60f;
        const float MonoMaxWidth = 46f;
        const int CurveSteps = 4;             // segments per rounded corner

        /// <summary>True when the glyph exists. Characters without a glyph are drawn as a gap.</summary>
        public static bool HasGlyph(char c) => Glyphs.ContainsKey(c);

        /// <summary>Width of the visible text (ink, from the left edge of the first glyph to the right edge of the last) in image units.</summary>
        public static float Width(string text, float size = 10f, VectorTextStyle? style = null)
        {
            style ??= new VectorTextStyle();
            var (lo, hi) = Ink(text, style);
            return (hi - lo) * size / CapHeight;
        }

        /// <summary>Left and right edge of the ink in font units, the pen starting at 0.</summary>
        static (float Lo, float Hi) Ink(string text, VectorTextStyle style)
        {
            float lo = float.MaxValue, hi = float.MinValue, pen = 0;
            foreach (char c in text)
            {
                float ox = pen + Lsb(c, style);
                foreach (var st in GlyphStrokes(c, style))
                    foreach (var p in st.P)
                    {
                        float x = ox + p.X + (style.Italic ? p.Y * 0.2f : 0f);
                        lo = Math.Min(lo, x); hi = Math.Max(hi, x);
                    }
                pen += Advance(c, style);
            }
            return lo <= hi ? (lo, hi) : (0f, 0f);
        }

        /// <summary>
        /// Text as a picture: the left end of the baseline at (0,0), upright on screen (y grows down), cap height = size.
        /// </summary>
        public static VectorImage Create(string text, float size = 10f, VectorTextStyle? style = null)
        {
            style ??= new VectorTextStyle();
            float s = size / CapHeight;
            var path = new VectorPath();
            float pen = 0;
            foreach (char c in text)
            {
                float ox = pen + Lsb(c, style);
                foreach (var st in GlyphStrokes(c, style))
                {
                    if (st.P.Count < 2) continue;
                    path.MoveTo(Map(st.P[0], ox, s, style.Italic));
                    for (int i = 1; i < st.P.Count; i++) path.LineTo(Map(st.P[i], ox, s, style.Italic));
                    if (st.Closed) path.Close();
                }
                pen += Advance(c, style);
            }
            return Picture(path, size, style, text);
        }

        /// <summary>
        /// Text bent along a polyline (image coordinates, y down). Each glyph point is placed by its distance along
        /// the path and its height above the path, so the curves of the path carry the letters with them.
        /// Glyphs past either end continue straight on from the end.
        /// The picture is in path coordinates: draw it with <c>Draw(sprite, Matrix3x2.Identity)</c>, not with x / y.
        /// </summary>
        public static VectorImage OnPath(string text, IReadOnlyList<PointF> points, float size = 10f, VectorTextPathOptions? options = null)
        {
            options ??= new VectorTextPathOptions();
            var style = options.Style ?? new VectorTextStyle();
            if (points == null || points.Count < 2) throw new ArgumentException("A text path needs at least two points.", nameof(points));
            var pl = new PolyLine(points);
            float s = size / CapHeight;
            var (inkLo, inkHi) = Ink(text, style);
            float total = Width(text, size, style);
            float f = 1f;                                                    // distance per image unit of text
            float start = options.Offset;
            if (options.FitToPath && total > 0) { f = pl.Length / total; start = 0; }
            else
            {
                start = options.Align switch
                {
                    VectorTextAlign.Center => (pl.Length - total) / 2 + options.Offset,
                    VectorTextAlign.End => pl.Length - total - options.Offset,
                    _ => options.Offset,
                };
            }
            var path = new VectorPath();
            float pen = 0;
            foreach (char c in text)
            {
                float ox = pen + Lsb(c, style);
                foreach (var st in GlyphStrokes(c, style))
                {
                    if (st.P.Count < 2) continue;
                    PointF Mapped(PointF p)
                    {
                        float gx = p.X + (style.Italic ? p.Y * 0.2f : 0f), gy = p.Y;
                        float d = start + (ox + gx - inkLo) * s * f;  // distance along the path (image units); the ink starts at start
                        var (at, tx, ty) = pl.At(d);
                        // up = (ty, -tx) in y-down screen space. Glyphs stay upright either way;
                        // below the path the glyph's top sits on the line and the body hangs under it.
                        float h = (options.Above ? gy : gy - CapHeight) * s;
                        return new PointF(at.X + h * ty, at.Y - h * tx);
                    }
                    path.MoveTo(Mapped(st.P[0]));
                    for (int i = 1; i < st.P.Count; i++) path.LineTo(Mapped(st.P[i]));
                    if (st.Closed) path.Close();
                }
                pen += Advance(c, style);
            }
            return Picture(path, size, style, text);
        }

        // ------------------------------------------------------------------ internals

        static PointF Map(PointF p, float ox, float s, bool italic)
        {
            float x = ox + p.X + (italic ? p.Y * 0.2f : 0f);
            return new PointF(x * s, -p.Y * s);                              // flip: glyphs are y-up, the picture is y-down
        }

        static VectorImage Picture(VectorPath path, float size, VectorTextStyle style, string text)
        {
            var img = new VectorImage { Title = text };
            float sw = Math.Max(0.5f, size * style.StrokeFraction);
            var shape = new VectorShape(path, null, new VectorColor(style.Color), sw) { Join = style.Join, Cap = style.Cap, MiterLimit = 4f };
            img.Shapes.Add(shape);
            var b = path.IsEmpty ? new RectangleF(0, 0, 1, 1) : path.ControlBounds();
            float pad = sw * 0.5f * (style.Join == VectorJoin.Miter ? 4f : 1f);   // a miter can reach 4 half-widths out
            img.ViewBox = new RectangleF(b.Left - pad, b.Top - pad, Math.Max(1f, b.Width + 2 * pad), Math.Max(1f, b.Height + 2 * pad));
            img.Width = img.ViewBox.Width;
            img.Height = img.ViewBox.Height;
            return img;
        }

        /// <summary>Advance of a character in font units, tracking included.</summary>
        static float Advance(char c, VectorTextStyle style)
        {
            float a;
            if (c == ' ') a = SpaceAdvance;
            else if (!Glyphs.TryGetValue(c, out var g)) a = 0f;
            else a = style.Monospace ? MonoAdvance : g.Width + 2 * SideBearing;
            return a + style.Tracking * CapHeight;
        }

        /// <summary>Left side bearing (font units): the glyph's origin relative to its pen position.</summary>
        static float Lsb(char c, VectorTextStyle style)
        {
            if (!style.Monospace || !Glyphs.TryGetValue(c, out var g)) return SideBearing;
            return (MonoAdvance - Math.Min(g.Width, MonoMaxWidth)) / 2;
        }

        sealed class Stroke
        {
            public List<PointF> P = new List<PointF>();
            public bool Closed;
        }

        /// <summary>The glyph's strokes in font units (y up), squeezed for Monospace and rounded at the corners.</summary>
        static List<Stroke> GlyphStrokes(char c, VectorTextStyle style)
        {
            var list = new List<Stroke>();
            if (!Glyphs.TryGetValue(c, out var g)) return list;
            float kx = style.Monospace && g.Width > MonoMaxWidth ? MonoMaxWidth / g.Width : 1f;
            foreach (var raw in g.Strokes)
            {
                var pts = raw.P.Select(p => new PointF(p.X * kx, p.Y)).ToList();
                var rounded = Round(pts, raw.Closed, style.Radius, style.TipDrop);
                if (raw.Closed && style.OutlinePosition != VectorOutlinePosition.Center)
                    rounded = OffsetOutline(rounded, style.OutlinePosition, style.StrokeFraction * CapHeight / 2f);
                list.Add(new Stroke { P = rounded, Closed = raw.Closed });
            }
            return list;
        }

        /// <summary>
        /// Moves a closed outline by half the stroke width, inward (Inside) or outward (Outside), so the stroke lies
        /// inside or outside the glyph. Each corner is mitred on the offset line; where the mitre would be longer
        /// than 4 half-widths, the corner is bevelled there instead.
        /// </summary>
        static List<PointF> OffsetOutline(List<PointF> p, VectorOutlinePosition where, float hw)
        {
            // drop repeated points (the rounding can repeat the end of a curve)
            var q = new List<PointF>(p.Count);
            foreach (var pt in p)
                if (q.Count == 0 || MathF.Abs(q[^1].X - pt.X) > 1e-4f || MathF.Abs(q[^1].Y - pt.Y) > 1e-4f) q.Add(pt);
            if (q.Count > 1 && MathF.Abs(q[^1].X - q[0].X) < 1e-4f && MathF.Abs(q[^1].Y - q[0].Y) < 1e-4f) q.RemoveAt(q.Count - 1);
            int n = q.Count;
            if (n < 3) return q;

            // glyph space is y up: a positive area runs counter-clockwise, so the inside is on the left of each edge
            double area = 0;
            for (int i = 0; i < n; i++) { var a = q[i]; var b = q[(i + 1) % n]; area += (double)a.X * b.Y - (double)b.X * a.Y; }
            float side = (area > 0 ? 1f : -1f) * (where == VectorOutlinePosition.Inside ? 1f : -1f);

            var o = new List<PointF>(n * 2);
            for (int i = 0; i < n; i++)
            {
                var a = q[(i - 1 + n) % n]; var v = q[i]; var b = q[(i + 1) % n];
                float d0x = v.X - a.X, d0y = v.Y - a.Y, d1x = b.X - v.X, d1y = b.Y - v.Y;
                float l0 = MathF.Sqrt(d0x * d0x + d0y * d0y), l1 = MathF.Sqrt(d1x * d1x + d1y * d1y);
                d0x /= l0; d0y /= l0; d1x /= l1; d1y /= l1;
                // offset normals (length hw) of the two edges at this corner
                float n0x = -d0y * hw * side, n0y = d0x * hw * side, n1x = -d1y * hw * side, n1y = d1x * hw * side;
                float c = d0x * d1x + d0y * d1y;                       // cos of the turn
                if (1 + c > 1e-3f)
                {   // intersection of the two offset edges: v + (n0 + n1) / (1 + cos)
                    float mx = (n0x + n1x) / (1 + c), my = (n0y + n1y) / (1 + c);
                    if (MathF.Sqrt(mx * mx + my * my) <= 4f * hw) { o.Add(new PointF(v.X + mx, v.Y + my)); continue; }
                }
                o.Add(new PointF(v.X + n0x, v.Y + n0y));               // too sharp: bevel the offset corner
                o.Add(new PointF(v.X + n1x, v.Y + n1y));
            }
            return o;
        }

        /// <summary>
        /// Rounds the corners of a polyline: each interior corner (every corner when closed) becomes a quadratic curve
        /// whose control point is the corner; the cut distance is at most <paramref name="r"/> and at most half of each
        /// adjoining segment. An acute corner at the top or bottom of the stroke is cut no deeper than
        /// 2 * tip / sqrt((1 + cos) / 2), so the tip is not lowered by more than <paramref name="tip"/>.
        /// Open ends are not rounded. Curves are returned as short segments.
        /// </summary>
        static List<PointF> Round(List<PointF> p, bool closed, float r, float tip)
        {
            int n = p.Count;
            var result = new List<PointF>(n * (CurveSteps + 1));
            if (n < 3 || r <= 0f)
            {
                result.AddRange(p);
                return result;
            }
            float ymax = p.Max(q => q.Y), ymin = p.Min(q => q.Y);

            (PointF a, PointF b, PointF v, float t) Corner(int i)
            {
                var v = p[i];
                var A = p[(i - 1 + n) % n];
                var B = p[(i + 1) % n];
                float l1 = Dist(A, v), l2 = Dist(B, v);
                float t = l1 > 0 && l2 > 0 ? Math.Min(r, Math.Min(l1 / 2, l2 / 2)) : 0f;
                if (t > 0)
                {
                    float cos = ((A.X - v.X) * (B.X - v.X) + (A.Y - v.Y) * (B.Y - v.Y)) / (l1 * l2);
                    if (cos > 0 && (v.Y == ymax || v.Y == ymin))
                        t = Math.Min(t, 2 * tip / MathF.Sqrt((1 + cos) / 2));
                }
                PointF a2 = l1 > 0 && t > 0 ? new PointF(v.X + (A.X - v.X) * t / l1, v.Y + (A.Y - v.Y) * t / l1) : v;
                PointF b2 = l2 > 0 && t > 0 ? new PointF(v.X + (B.X - v.X) * t / l2, v.Y + (B.Y - v.Y) * t / l2) : v;
                return (a2, b2, v, t);
            }

            void Emit(int i)
            {
                var (a, b, v, t) = Corner(i);
                result.Add(a);
                if (t <= 0f) return;
                for (int k = 1; k <= CurveSteps; k++)
                {
                    float u = k / (float)CurveSteps, w = 1 - u;
                    result.Add(new PointF(w * w * a.X + 2 * w * u * v.X + u * u * b.X, w * w * a.Y + 2 * w * u * v.Y + u * u * b.Y));
                }
            }

            if (closed)
            {
                for (int i = 0; i < n; i++) Emit(i);
            }
            else
            {
                result.Add(p[0]);
                for (int i = 1; i < n - 1; i++) Emit(i);
                result.Add(p[n - 1]);
            }
            return result;
        }

        static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // ------------------------------------------------------------------ glyph table

        sealed class RawStroke
        {
            public List<PointF> P = new List<PointF>();
            public bool Closed;
        }

        sealed class GlyphDef
        {
            public float Width;
            public List<RawStroke> Strokes = new List<RawStroke>();
        }

        static readonly Dictionary<char, GlyphDef> Glyphs = Load();

        /// <summary>Reads the table: one glyph per line, "char TAB width TAB strokes"; strokes are split by '|', points are
        /// "x,y" separated by spaces, a trailing Z closes the stroke.</summary>
        static Dictionary<char, GlyphDef> Load()
        {
            var dict = new Dictionary<char, GlyphDef>();
            foreach (var line in Data.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length != 3 || parts[0].Length != 1) continue;
                var g = new GlyphDef { Width = float.Parse(parts[1], CultureInfo.InvariantCulture) };
                foreach (var spec in parts[2].Split('|'))
                {
                    var rs = new RawStroke { Closed = spec.EndsWith("Z", StringComparison.Ordinal) };
                    var body = rs.Closed ? spec.Substring(0, spec.Length - 1) : spec;
                    foreach (var pt in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var xy = pt.Split(',');
                        rs.P.Add(new PointF(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture)));
                    }
                    if (rs.P.Count > 0) g.Strokes.Add(rs);
                }
                dict[parts[0][0]] = g;
            }
            return dict;
        }

        /// <summary>Glyph table of the VectorAngular font (exported from its page, aliases already expanded).</summary>
        const string Data = @"
0	36	12,0 24,0 36,12 36,58 24,70 12,70 0,58 0,12Z
1	18	2,54 18,70 18,0
2	36	2,54 8,66 20,70 30,64 34,52 28,38 0,0 36,0
3	36	2,58 10,70 24,70 33,60 33,48 24,38 12,37 24,36 36,26 36,10 26,0 10,0 0,12
4	38	30,0 30,70 0,22 38,22
5	36	32,70 4,70 2,38 14,42 26,42 36,32 36,10 26,0 10,0 0,12
6	36	30,66 22,70 12,70 0,56 0,12 8,0 26,0 36,12 36,30 26,42 10,42 0,30
7	36	0,70 36,70 12,0
8	36	10,70 26,70 33,63 33,49 27,42 9,28 0,20 0,8 8,0 28,0 36,8 36,20 27,28 9,42 3,49 3,63Z
9	36	6,4 14,0 24,0 36,14 36,58 28,70 10,70 0,58 0,40 10,28 26,28 36,40
A	44	0,0 22,70 44,0|8,24 36,24
B	36	0,0 28,0 36,8 36,30 30,38 34,46 34,62 26,70 0,70Z|0,38 30,38
C	36	36,56 28,70 8,70 0,60 0,10 8,0 28,0 36,14
D	36	0,0 0,70 20,70 36,54 36,16 20,0Z
E	36	36,70 0,70 0,0 36,0|0,35 28,35
F	34	0,0 0,70 34,70|0,35 26,35
G	36	36,56 28,70 8,70 0,60 0,10 8,0 28,0 36,10 36,32 20,32
H	36	0,0 0,70|36,0 36,70|0,35 36,35
I	0	0,0 0,70
J	28	28,70 28,14 20,0 8,0 0,12
K	36	0,0 0,70|34,70 0,24|10,37 36,0
L	32	0,70 0,0 32,0
M	44	0,0 0,70 22,14 44,70 44,0
N	36	0,0 0,70 36,0 36,70
O	40	12,0 28,0 40,12 40,58 28,70 12,70 0,58 0,12Z
P	36	0,0 0,70 24,70 36,60 36,46 24,36 0,36
Q	40	12,0 28,0 40,12 40,58 28,70 12,70 0,58 0,12Z|26,16 40,-4
R	36	0,0 0,70 24,70 36,60 36,46 24,36 0,36|18,36 36,0
S	36	34,56 28,70 10,70 2,60 2,48 12,37 26,33 36,22 36,10 28,0 8,0 0,12
T	40	0,70 40,70|20,70 20,0
U	36	0,70 0,16 12,0 24,0 36,16 36,70
V	40	0,70 20,0 40,70
W	56	0,70 13,0 28,52 43,0 56,70
X	36	0,0 36,70|36,0 0,70
Y	40	0,70 20,36 40,70|20,36 20,0
Z	34	0,70 34,70 0,0 34,0
a	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z|30,50 30,0
b	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z|0,70 0,0
c	30	30,38 22,50 8,50 0,40 0,10 8,0 22,0 30,12
d	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z|30,70 30,0
e	30	0,25 30,25 30,40 22,50 8,50 0,40 0,10 8,0 22,0 30,10
f	24	24,66 18,70 12,66 12,0|0,50 24,50
g	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z|30,50 30,-8 22,-20 8,-20 0,-12
h	30	0,70 0,0|0,34 6,50 24,50 30,40 30,0
i	0	0,0 0,50|0,62 0,65
j	10	10,50 10,-8 4,-20 0,-20|10,62 10,65
k	28	0,70 0,0|26,50 0,18|10,30 28,0
l	0	0,70 0,0
m	52	0,0 0,50|0,34 6,50 20,50 26,40 26,0|26,40 32,50 46,50 52,40 52,0
n	30	0,0 0,50|0,34 6,50 24,50 30,40 30,0
o	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z
p	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z|0,50 0,-20
q	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z|30,50 30,-20
r	20	0,0 0,50|0,34 6,50 20,50
s	28	26,40 20,50 8,50 2,42 2,34 8,27 22,24 28,16 28,8 20,0 8,0 0,8
t	22	10,66 10,8 16,0 22,0|0,50 22,50
u	30	0,50 0,10 8,0 22,0 30,10|30,50 30,0
v	32	0,50 16,0 32,50
w	48	0,50 10,0 24,38 38,0 48,50
x	30	0,50 30,0|30,50 0,0
y	32	0,50 17,3|32,50 10,-20
z	28	0,50 28,50 0,0 28,0
.	0	0,0 0,3
,	4	4,2 0,-10
:	0	0,0 0,3|0,38 0,41
;	4	4,38 4,41|4,2 0,-10
!	0	0,70 0,22|0,0 0,3
?	36	0,56 6,68 18,70 30,68 36,56 36,48 18,38 18,24|18,0 18,3
-	24	0,26 24,26
_	36	0,-8 36,-8
+	30	0,30 30,30|15,15 15,45
=	30	0,38 30,38|0,22 30,22
/	26	0,-4 26,74
(	16	16,76 4,56 4,6 16,-8
)	14	0,76 12,56 12,6 0,-8
""	12	0,70 0,54|12,70 12,54
'	0	0,70 0,54
А	44	0,0 22,70 44,0|8,24 36,24
В	36	0,0 28,0 36,8 36,30 30,38 34,46 34,62 26,70 0,70Z|0,38 30,38
Е	36	36,70 0,70 0,0 36,0|0,35 28,35
К	36	0,0 0,70|34,70 0,24|10,37 36,0
М	44	0,0 0,70 22,14 44,70 44,0
Н	36	0,0 0,70|36,0 36,70|0,35 36,35
О	40	12,0 28,0 40,12 40,58 28,70 12,70 0,58 0,12Z
Р	36	0,0 0,70 24,70 36,60 36,46 24,36 0,36
С	36	36,56 28,70 8,70 0,60 0,10 8,0 28,0 36,14
Т	40	0,70 40,70|20,70 20,0
Х	36	0,0 36,70|36,0 0,70
І	0	0,0 0,70
а	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z|30,50 30,0
е	30	0,25 30,25 30,40 22,50 8,50 0,40 0,10 8,0 22,0 30,10
о	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z
к	28	0,70 0,0|26,50 0,18|10,30 28,0
р	30	10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z|0,50 0,-20
с	30	30,38 22,50 8,50 0,40 0,10 8,0 22,0 30,12
х	30	0,50 30,0|30,50 0,0
у	32	0,50 17,3|32,50 10,-20
і	0	0,0 0,50|0,62 0,65
З	36	2,58 10,70 24,70 33,60 33,48 24,38 12,37 24,36 36,26 36,10 26,0 10,0 0,12
Б	36	34,70 0,70 0,0 20,0 36,10 36,26 20,36 0,36
Г	32	0,0 0,70 32,70
Д	44	10,0 16,70 40,70 40,0|2,-12 2,0 44,0 44,-12
Ж	38	0,70 19,35 0,0|38,70 19,35 38,0|19,70 19,0
И	36	0,70 0,0 36,70 36,0
Й	36	0,70 0,0 36,70 36,0|8,86 18,80 28,86
Л	36	0,0 15,70 36,70 36,0
П	34	0,0 0,70 34,70 34,0
У	38	0,70 21,22|38,70 10,-10
Ф	36	18,70 18,0|12,13 24,13 36,25 36,45 24,57 12,57 0,45 0,25Z
Ц	36	0,70 0,0 36,0|36,70 36,-12
Ч	34	0,70 0,50 8,34 20,32 34,32|34,70 34,0
Ш	50	0,70 0,0 50,0 50,70|25,70 25,0
Щ	50	0,70 0,0 50,0|50,70 50,-12|25,70 25,0
Ъ	36	0,70 14,70 14,0 24,0 36,8 36,26 24,34 14,34
Ь	35	0,70 0,0 22,0 35,9 35,25 22,34 0,34
Ы	46	0,70 0,0 22,0 35,9 35,25 22,34 0,34|46,70 46,0
Э	40	4,56 10,70 28,70 40,60 40,10 28,0 10,0 4,14|10,35 40,35
Ю	48	0,70 0,0|0,35 12,35|24,0 36,0 48,12 48,58 36,70 24,70 12,58 12,12Z
Я	36	36,0 36,70 14,70 2,60 2,46 14,36 36,36|18,36 0,0
Ё	36	36,70 0,70 0,0 36,0|0,35 28,35|10,84 10,87|26,84 26,87
Є	34	34,56 28,70 10,70 0,60 0,10 10,0 28,0 34,14|8,35 32,35
Ї	16	8,0 8,70|2,84 2,87|14,84 14,87
Ґ	30	0,0 0,70 30,70 30,80
б	30	0,25 4,50 16,66 30,70|10,0 20,0 30,10 30,40 20,50 10,50 0,40 0,10Z
в	28	0,0 18,0 28,6 28,18 18,25 24,32 24,43 16,50 0,50Z|0,25 18,25
г	24	0,0 0,50 24,50
д	32	8,0 13,50 28,50 28,0|0,-10 0,0 32,0 32,-10
ж	32	0,50 16,25 0,0|32,50 16,25 32,0|16,50 16,0
з	28	2,40 8,50 18,50 26,43 26,34 18,26 10,25 22,24 28,16 28,8 20,0 8,0 0,8
и	30	0,50 0,0 30,50 30,0
й	30	0,50 0,0 30,50 30,0|7,66 15,62 23,66
л	30	0,0 12,50 30,50 30,0
м	32	0,0 0,50 16,12 32,50 32,0
н	30	0,50 0,0|0,25 30,25|30,50 30,0
п	30	0,0 0,50 30,50 30,0
т	28	0,50 28,50|14,50 14,0
ф	32	16,70 16,-20|10,3 22,3 32,13 32,37 22,47 10,47 0,37 0,13Z
ц	30	0,50 0,0 30,0|30,50 30,-10
ч	28	0,50 0,38 6,24 16,22 28,22|28,50 28,0
ш	44	0,50 0,0 44,0 44,50|22,50 22,0
щ	44	0,50 0,0 44,0|44,50 44,-10|22,50 22,0
ъ	30	0,50 12,50 12,0 20,0 30,6 30,19 20,25 12,25
ь	29	0,50 0,0 18,0 29,6 29,19 18,25 0,25
ы	40	0,50 0,0 18,0 29,6 29,19 18,25 0,25|40,50 40,0
э	32	2,38 8,50 22,50 32,40 32,10 22,0 8,0 2,12|8,25 32,25
ю	36	0,50 0,0|0,25 10,25|20,0 26,0 36,10 36,40 26,50 20,50 10,40 10,10Z
я	26	26,0 26,50 12,50 2,44 2,31 12,25 26,25|12,25 0,0
ё	30	0,25 30,25 30,40 22,50 8,50 0,40 0,10 8,0 22,0 30,10|10,64 10,66|22,64 22,66
є	30	30,38 22,50 8,50 0,40 0,10 8,0 22,0 30,12|8,25 28,25
ї	16	8,0 8,50|2,64 2,66|14,64 14,66
ґ	22	0,0 0,50 22,50 22,60
";

        /// <summary>Polyline with arc length; At(d) gives the point and unit tangent at distance d (extrapolated past the ends).</summary>
        sealed class PolyLine
        {
            readonly List<PointF> pts = new List<PointF>();
            readonly List<float> cum = new List<float>();
            public float Length { get; }

            public PolyLine(IReadOnlyList<PointF> src)
            {
                cum.Add(0);
                foreach (var p in src)
                {
                    if (pts.Count > 0 && Math.Abs(pts[^1].X - p.X) < 1e-4f && Math.Abs(pts[^1].Y - p.Y) < 1e-4f) continue;
                    pts.Add(p);
                    if (pts.Count > 1) cum.Add(cum[^1] + Dist(pts[^2], pts[^1]));
                }
                if (pts.Count < 2) pts = new List<PointF> { src[0], src[^1] }.Distinct().ToList();
                Length = cum[^1];
            }

            public (PointF At, float Tx, float Ty) At(float d)
            {
                if (pts.Count < 2) return (pts[0], 1, 0);
                if (d <= 0) { var (ux, uy) = Unit(pts[0], pts[1]); return Extend(pts[0], ux, uy, d); }         // travel direction = first segment
                if (d >= Length) { var (ux, uy) = Unit(pts[^2], pts[^1]); return Extend(pts[^1], ux, uy, d - Length); }   // = last segment
                int i = 1;
                while (i < cum.Count - 1 && cum[i] < d) i++;
                float seg = cum[i] - cum[i - 1], t = seg > 0 ? (d - cum[i - 1]) / seg : 0;
                float x = pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t, y = pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t;
                float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y, len = MathF.Sqrt(dx * dx + dy * dy);
                return (new PointF(x, y), len > 0 ? dx / len : 1, len > 0 ? dy / len : 0);
            }

            // beyond an end: continue straight on along the end segment (over < 0 = before the start)
            static (PointF, float, float) Extend(PointF anchor, float ux, float uy, float over) =>
                (new PointF(anchor.X + ux * over, anchor.Y + uy * over), ux, uy);

            // unit direction from a to b (falls back to +x for a zero-length segment)
            static (float, float) Unit(PointF a, PointF b)
            {
                float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy);
                return len > 0 ? (dx / len, dy / len) : (1f, 0f);
            }
        }
    }
}
