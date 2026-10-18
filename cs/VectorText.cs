// VectorText.cs - text made of open strokes (lines and arcs), no font files.
// Glyphs are single-stroke designs in em units (cap height 10, x-height 6.5, descender -3), drawn as stroked paths,
// so the text stays sharp at any scale and can be bent along a path (map labels along roads and rivers).
// Bold = a wider stroke. Italic = a skew (x += 0.2 * y). Latin, digits, basic punctuation and full Cyrillic.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace Sr2d64CSport
{
    /// <summary>How text is drawn. Size is the cap height in image units (glyph em = Size / 10).</summary>
    public sealed class VectorTextStyle
    {
        public bool Bold;
        public bool Italic;
        /// <summary>ARGB colour of the strokes.</summary>
        public int Color = unchecked((int)0xFF000000);
        /// <summary>Stroke width as a fraction of Size (regular 0.08; bold doubles it). 0 = the default.</summary>
        public float Weight;
        /// <summary>Space between glyphs as a fraction of Size.</summary>
        public float Tracking = 0.1f;

        internal float StrokeFraction => Weight > 0 ? Weight : (Bold ? 0.16f : 0.08f);
    }

    /// <summary>Text along a path.</summary>
    public sealed class VectorTextPathOptions
    {
        public VectorTextStyle Style = new VectorTextStyle();
        /// <summary>Where the text starts, as a distance along the path from its start (image units).</summary>
        public float Offset;
        /// <summary>Start, Center or End: where the text sits along the path, after Offset.</summary>
        public VectorTextAlign Align = VectorTextAlign.Start;
        /// <summary>True: text on the left of the path's direction (screen up); false: below it.</summary>
        public bool Above = true;
        /// <summary>True: the text is stretched or squeezed along the path so it spans it exactly (Offset is then 0).</summary>
        public bool FitToPath;
    }

    public enum VectorTextAlign { Start, Center, End }

    /// <summary>Stroke-font text as a <see cref="VectorImage"/>, and text bent along a polyline.</summary>
    internal static class VectorText
    {
        /// <summary>True when the glyph exists. Characters without a glyph are drawn as a gap.</summary>
        public static bool HasGlyph(char c) => Glyphs.ContainsKey(c);

        /// <summary>Width of the text in image units at the given size (advance of every glyph, no trailing gap).</summary>
        public static float Width(string text, float size = 10f, VectorTextStyle? style = null)
        {
            style ??= new VectorTextStyle();
            float s = size / 10f, w = 0;
            foreach (char c in text) w += Advance(c, style) * s;
            return w > 0 ? w - style.Tracking * size : 0;
        }

        /// <summary>
        /// Text as a picture: the left end of the baseline at (0,0), upright on screen (y grows down), cap height = size.
        /// </summary>
        public static VectorImage Create(string text, float size = 10f, VectorTextStyle? style = null)
        {
            style ??= new VectorTextStyle();
            float s = size / 10f;
            var path = new VectorPath();
            float cursor = 0;
            foreach (char c in text)
            {
                foreach (var line in Strokes(c))
                {
                    if (line.Count < 2) continue;
                    path.MoveTo(Map(line[0], cursor, s, style.Italic));
                    for (int i = 1; i < line.Count; i++) path.LineTo(Map(line[i], cursor, s, style.Italic));
                }
                cursor += Advance(c, style) * s;
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
            float s = size / 10f;
            float total = Width(text, size, style);
            float f = 1f;                                                    // distance per text unit
            float start = options.Offset;
            if (options.FitToPath && total > 0) { f = (pl.Length - 0) / total; start = 0; }
            else
            {
                start = options.Align switch
                {
                    VectorTextAlign.Center => (pl.Length - total) / 2 + options.Offset,
                    VectorTextAlign.End => pl.Length - total - options.Offset,
                    _ => options.Offset,
                };
            }
            float side = options.Above ? 1f : -1f;
            var path = new VectorPath();
            float cursor = 0;
            foreach (char c in text)
            {
                foreach (var line in Strokes(c))
                {
                    if (line.Count < 2) continue;
                    PointF Mapped(PointF p)
                    {
                        float gx = p.X + (style.Italic ? p.Y * 0.2f : 0f), gy = p.Y;
                        float d = start + (cursor + gx * s) * f;      // cursor is already in image units
                        var (at, tx, ty) = pl.At(d);
                        float h = gy * s * side;                             // up = (ty, -tx) in y-down screen space
                        return new PointF(at.X + h * ty, at.Y - h * tx);
                    }
                    path.MoveTo(Mapped(line[0]));
                    for (int i = 1; i < line.Count; i++) path.LineTo(Mapped(line[i]));
                }
                cursor += Advance(c, style) * s;
            }
            return Picture(path, size, style, text);
        }

        // ------------------------------------------------------------------ internals

        static PointF Map(PointF p, float cursor, float s, bool italic)
        {
            float x = p.X + (italic ? p.Y * 0.2f : 0f);
            return new PointF(cursor + x * s, -p.Y * s);                     // flip: glyphs are y-up, the picture is y-down
        }

        static VectorImage Picture(VectorPath path, float size, VectorTextStyle style, string text)
        {
            var img = new VectorImage { Title = text };
            float sw = Math.Max(0.5f, size * style.StrokeFraction);
            var shape = new VectorShape(path, null, new VectorColor(style.Color), sw);
            img.Shapes.Add(shape);
            var b = path.IsEmpty ? new RectangleF(0, 0, 1, 1) : path.ControlBounds();
            float pad = sw * 0.5f;
            img.ViewBox = new RectangleF(b.Left - pad, b.Top - pad, Math.Max(1f, b.Width + 2 * pad), Math.Max(1f, b.Height + 2 * pad));
            img.Width = img.ViewBox.Width;
            img.Height = img.ViewBox.Height;
            return img;
        }

        static float Advance(char c, VectorTextStyle style)
        {
            if (c == ' ') return 3.0f + style.Tracking * 10f;
            return (Glyphs.TryGetValue(c, out var g) ? g.Advance : 0f) + style.Tracking * 10f;
        }

        static IEnumerable<List<PointF>> Strokes(char c)
        {
            return Glyphs.TryGetValue(c, out var g) ? g.Lines : Enumerable.Empty<List<PointF>>();
        }

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

            static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }

        static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>
        /// Parses a glyph: "M x y" starts a stroke, "L x y" adds a line, "A cx cy rx ry a0 a1" adds an elliptical arc
        /// from angle a0 to a1 in degrees (y up, negative sweeps clockwise). A line is inserted if the arc does not start where the stroke ends.
        /// </summary>
        static List<List<PointF>> Parse(string spec)
        {
            var lines = new List<List<PointF>>();
            var cur = new List<PointF>();
            // "M0 0" and "M 0 0" both work: a command letter is always its own token
            var t = System.Text.RegularExpressions.Regex.Replace(spec, "([MLA])", " $1 ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < t.Length)
            {
                string op = t[i++];
                if (op == "M")
                {
                    cur = new List<PointF> { new PointF(F(t[i]), F(t[i + 1])) };
                    lines.Add(cur); i += 2;
                }
                else if (op == "L")
                {
                    if (cur.Count == 0) { cur.Add(new PointF(0, 0)); lines.Add(cur); }
                    cur.Add(new PointF(F(t[i]), F(t[i + 1]))); i += 2;
                }
                else if (op == "A")
                {
                    float cx = F(t[i]), cy = F(t[i + 1]), rx = F(t[i + 2]), ry = F(t[i + 3]), a0 = F(t[i + 4]), a1 = F(t[i + 5]);
                    i += 6;
                    if (cur.Count == 0) { cur.Add(new PointF(0, 0)); lines.Add(cur); }
                    int n = Math.Max(4, (int)(Math.Abs(a1 - a0) / 8f));
                    for (int k = 0; k <= n; k++)
                    {
                        float a = (a0 + (a1 - a0) * k / n) * MathF.PI / 180f;
                        var p = new PointF(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a));
                        var last = cur[^1];
                        if (Math.Abs(last.X - p.X) > 1e-3f || Math.Abs(last.Y - p.Y) > 1e-3f) cur.Add(p);
                    }
                }
                else throw new FormatException("Glyph op '" + op + "' is not M, L or A.");
            }
            return lines;
        }

        sealed class Glyph
        {
            public readonly float Advance;
            public readonly List<List<PointF>> Lines;
            public Glyph(float advance, string spec) { Advance = advance; Lines = Parse(spec); }
        }

        static readonly Dictionary<char, Glyph> Glyphs = BuildGlyphs();

        static Dictionary<char, Glyph> BuildGlyphs()
        {
            var g = new Dictionary<char, Glyph>();
            void Add(char c, float adv, string spec) => g[c] = new Glyph(adv, spec);
            void Alias(char c, char like) => g[c] = g[like];

            // ---- capitals (cap height 10)
            Add('A', 6, "M0 0 L3 10 L6 0 M1.2 4 L4.8 4");
            Add('B', 6.4f, "M0 0 L0 10 L3.2 10 A3.2 7.5 3.2 2.5 90 -90 A3.2 2.5 3.2 2.5 90 -90 L0 0 M0 5 L3.2 5");
            Add('C', 6.6f, "A3.2 5 3.2 5 45 315");
            Add('D', 7, "M0 0 L0 10 L3 10 A3 5 3 5 90 -90 L0 0");
            Add('E', 5, "M5 0 L0 0 L0 10 L5 10 M0 5 L4 5");
            Add('F', 5, "M0 0 L0 10 L5 10 M0 5 L4 5");
            Add('G', 7, "A3.2 5 3.2 5 45 360 L3.5 5");
            Add('H', 6, "M0 0 L0 10 M6 0 L6 10 M0 5 L6 5");
            Add('I', 2, "M0 0 L2 0 M1 0 L1 10 M0 10 L2 10");
            Add('J', 4.5f, "M4.5 10 L4.5 2.5 A2.25 2.5 2.25 2.5 0 -180");
            Add('K', 5.5f, "M0 0 L0 10 M5.5 10 L0 4 M2 6 L5.5 0");
            Add('L', 5, "M0 10 L0 0 L5 0");
            Add('M', 7, "M0 0 L0 10 L3.5 4 L7 10 L7 0");
            Add('N', 6, "M0 0 L0 10 L6 0 L6 10");
            Add('O', 6.5f, "A3.25 5 3.25 5 0 360");
            Add('P', 5.6f, "M0 0 L0 10 L3.2 10 A3.2 7.5 3.2 2.5 90 -90 L0 5");
            Add('Q', 6.5f, "A3.25 5 3.25 5 0 360 M4 2.5 L6.5 0");
            Add('R', 6, "M0 0 L0 10 L3.2 10 A3.2 7.5 3.2 2.5 90 -90 L0 5 M3 5 L6 0");
            Add('S', 5.6f, "A2.75 7.5 2.75 2.5 30 270 A2.75 2.5 2.75 2.5 90 -150");
            Add('T', 6, "M0 10 L6 10 M3 10 L3 0");
            Add('U', 6, "M0 10 L0 3.5 A3 3.5 3 3.5 180 360 L6 10");
            Add('V', 6, "M0 10 L3 0 L6 10");
            Add('W', 8, "M0 10 L2 0 L4 7 L6 0 L8 10");
            Add('X', 6, "M0 0 L6 10 M6 0 L0 10");
            Add('Y', 6, "M0 10 L3 5 L6 10 M3 5 L3 0");
            Add('Z', 6, "M0 10 L6 10 L0 0 L6 0");

            // ---- digits
            Add('0', 5.5f, "A2.75 5 2.75 5 0 360");
            Add('1', 4, "M0 8 L2.5 10 L2.5 0 M0 0 L5 0");
            Add('2', 5.5f, "A2.75 7.5 2.75 2.5 160 -30 L0 0 L5.5 0");
            Add('3', 5.5f, "A2.75 7.5 2.75 2.5 160 -90 A2.75 2.5 2.75 2.5 90 -160");
            Add('4', 6, "M4.5 0 L4.5 10 L0 3 L6 3");
            Add('5', 5.5f, "M5.5 10 L0.5 10 L0 5.5 L2.5 6 A2.9 2.5 2.6 2.5 90 -150");
            Add('6', 5.5f, "A2.75 3.25 2.75 3.25 0 360 M0 3.25 L0 5 A2.75 6.75 2.75 3.25 180 60");
            Add('7', 5.5f, "M0 10 L5.5 10 L2 0");
            Add('8', 5.5f, "A2.75 2.6 2.75 2.6 0 360 A2.75 7.4 2.5 2.6 0 360");
            Add('9', 5.5f, "A2.75 6.75 2.75 3.25 0 360 M5.5 6.75 L5.5 4 A2.75 3.25 2.75 3.25 0 -90");

            // ---- lower case (x-height 6.5, descender -3)
            Add('a', 5, "A2.5 3.25 2.5 3.25 0 360 M5 0 L5 6.5");
            Add('b', 5, "M0 10 L0 0 A2.5 3.25 2.5 3.25 180 -180");
            Add('c', 4.5f, "A2.25 3.25 2.25 3.25 45 315");
            Add('d', 5, "M5 10 L5 0 A2.5 3.25 2.5 3.25 0 360");
            Add('e', 5, "M0.5 3.25 L4.5 3.25 A2.5 3.25 2.5 3.25 0 330");
            Add('f', 3.5f, "M1 0 L1 7.5 A1.5 1.5 1.5 1.5 180 60 M0 6.5 L3 6.5");
            Add('g', 5, "A2.5 3.25 2.5 3.25 0 360 M5 6.5 L5 -2 A2.5 -2 2.5 1.5 0 -150");
            Add('h', 5, "M0 10 L0 0 M0 3.5 A2.5 3.25 2.5 3.25 180 0 L5 0");
            Add('i', 2, "M1 0 L1 6.5 M1 8.5 L1 9");
            Add('j', 2.5f, "M1.5 6.5 L1.5 -1.5 A0.5 -1.5 0.5 0.4 0 -180 M2 8.5 L2 9");
            Add('k', 4.5f, "M0 10 L0 0 M4.5 6.5 L0 2.5 M1.8 3.8 L4.5 0");
            Add('l', 2, "M1 0 L1 10");
            Add('m', 6, "M0 0 L0 6.5 M0 4.5 L2 6.5 L2 0 M2 4.5 L4 6.5 L4 0 M4 4.5 L6 6.5 L6 0");
            Add('n', 5, "M0 0 L0 6.5 M0 4.5 L2 6.5 L5 6.5 L5 0");
            Add('o', 5, "A2.5 3.25 2.5 3.25 0 360");
            Add('p', 5, "M0 -3 L0 6.5 A2.5 3.25 2.5 3.25 180 -180");
            Add('q', 5, "M5 -3 L5 6.5 A2.5 3.25 2.5 3.25 0 360");
            Add('r', 3.5f, "M0 0 L0 6.5 M0 4 A2 4 2 2.5 150 60");
            Add('s', 4.5f, "A2.25 4.9 2.25 1.6 40 270 A2.25 1.6 2.25 1.6 90 -140");
            Add('t', 3.5f, "M1 9 L1 1.5 L3.5 1.5 M0 6.5 L3 6.5");
            Add('u', 5, "M0 6.5 L0 2.5 A2.5 2.5 2.5 2.5 180 360 M5 6.5 L5 0");
            Add('v', 5, "M0 6.5 L2.5 0 L5 6.5");
            Add('w', 7, "M0 6.5 L1.75 0 L3.5 4.5 L5.25 0 L7 6.5");
            Add('x', 5, "M0 0 L5 6.5 M5 0 L0 6.5");
            Add('y', 5, "M0 6.5 L2.5 0 M5 6.5 L1.5 -3");
            Add('z', 5, "M0 6.5 L5 6.5 L0 0 L5 0");

            // ---- punctuation and signs
            Add(' ', 3, "");
            Add('.', 1.6f, "A0.8 0.8 0.8 0.8 0 360");
            Add(',', 1.6f, "M1.2 1.5 L0.6 -1");
            Add(':', 1.6f, "A0.8 2.5 0.8 0.8 0 360 A0.8 7.5 0.8 0.8 0 360");
            Add(';', 1.6f, "A0.8 2.5 0.8 0.8 0 360 M1.2 1.5 L0.6 -1");
            Add('!', 2, "M1 10 L1 3.5 M1 0.5 L1 0.8");
            Add('?', 5, "A2.5 7.5 2.5 2.5 160 -30 L2.5 5 L2.5 4.5 M2.5 0.5 L2.5 1.2");
            Add('-', 4, "M0.5 5 L3.5 5");
            Add('_', 5, "M0 -1.5 L5 -1.5");
            Add('+', 6, "M1 5 L5 5 M3 3 L3 7");
            Add('=', 6, "M1 3.8 L5 3.8 M1 6.2 L5 6.2");
            Add('/', 4, "M0 0 L4 10");
            Add('\\', 4, "M0 10 L4 0");
            Add('|', 2, "M1 -3 L1 10");
            Add('(', 3, "A3.2 5 2 5 60 300");
            Add(')', 3, "A0.5 5 2 5 -120 120");
            Add('[', 3, "M3 -2 L1 -2 L1 11 L3 11");
            Add(']', 3, "M0 -2 L2 -2 L2 11 L0 11");
            Add('"', 3, "M1 10 L1 7 M3 10 L3 7");
            Add('\'', 1.5f, "M1 10 L1 7");
            Add('*', 4, "M2 4 L2 9 M0 7 L4 6.2 M0.6 4.6 L3.4 8.4 M3.4 4.6 L0.6 8.4");
            Add('%', 7, "A1.5 8.5 1.2 1.5 0 360 A5.5 1.5 1.2 1.5 0 360 M6 10 L1 0");
            Add('<', 5, "M5 8.5 L0.5 5 L5 1.5");
            Add('>', 5, "M0.5 8.5 L5 5 L0.5 1.5");
            Add('#', 6, "M2 0 L1.5 10 M4.5 0 L4 10 M0.5 3 L5.5 3 M0 7 L5 7");
            Add('&', 6.5f, "M6 0 L2 6 A2.2 2.2 2.2 2.2 60 360 L1 0 L6 4.5");
            Add('@', 8, "A4 5 4 5 45 360 M4 4 A1.8 2 1.8 2 360 90");

            // ---- Latin look-alikes in Cyrillic (same shapes)
            Alias('А', 'A'); Alias('В', 'B'); Alias('Е', 'E'); Alias('К', 'K'); Alias('М', 'M'); Alias('Н', 'H');
            Alias('О', 'O'); Alias('Р', 'P'); Alias('С', 'C'); Alias('Т', 'T'); Alias('Х', 'X');
            Alias('а', 'a'); Alias('е', 'e'); Alias('о', 'o'); Alias('р', 'p'); Alias('с', 'c'); Alias('у', 'y'); Alias('х', 'x');

            // ---- Cyrillic capitals (drawn)
            Add('Б', 6, "M6 0 L0 0 L0 10 L6 10 M0 5 L4 5");
            Add('Г', 5, "M0 0 L0 10 L5 10");
            Add('Д', 6.5f, "M0 0 L0 9 L1 10 L5.5 10 L6.5 9 L6.5 0 M-0.5 0 L7 0");
            Add('Ж', 8, "M0 10 L4 5 L8 10 M0 0 L4 5 L8 0");
            Add('З', 5.5f, "A2.75 7.5 2.75 2.5 160 -90 A2.75 2.5 2.75 2.5 90 -160");
            Add('И', 6, "M0 0 L0 10 L6 0 L6 10");
            Add('Й', 6, "M0 0 L0 10 L6 0 L6 10 M2 11.5 L4 11.5");
            Add('Л', 6, "M0 0 L1.2 10 L4.8 10 L6 0 M1.2 4 L4.8 4");
            Add('П', 6, "M0 0 L0 10 L6 10 L6 0");
            Add('Ф', 7, "A3.5 5 3.5 4.9 0 360 M3.5 -0.5 L3.5 10.5");
            Add('Ц', 6.5f, "M0 10 L0 0 L5.5 0 M5.5 10 L5.5 -2.5 L6.8 -2.5");
            Add('Ч', 6, "M0 10 L0 6 L6 6 M6 10 L6 0");
            Add('Ш', 8, "M0 0 L0 10 M4 0 L4 10 M8 0 L8 10 M0 0 L8 0");
            Add('Щ', 9, "M0 0 L0 10 M4 0 L4 10 M8 0 L8 10 M0 0 L8 0 M8 0 L8.5 -2.5 L9.5 -2.5");
            Add('Ъ', 6.5f, "M0 10 L2.5 10 M1 10 L1 0 M1 5 L3 5 A3 2.5 1.5 2.5 90 -90 M1 0 L4 0");
            Add('Ы', 7, "M0 0 L0 10 M0 5 L3 5 M3 0 L3 10 M3 0 L6 0 L6 10");
            Add('Ь', 5.5f, "M0 10 L0 0 L3 0 A3.2 2.5 3.2 2.5 -90 90 L0 5 L3 5");
            Add('Э', 5.5f, "M5.5 10 L1.5 10 L0 5 L1.5 0 L5.5 0 M1.5 5 L4 5");
            Add('Ю', 8, "M0 0 L0 10 M0 5 L2.5 5 M2.5 0 L2.5 10 M3 5 A5.5 5 2.5 5 180 540");
            Add('Я', 6, "M0 0 L0 10 L3 10 A3 7.5 3 2.5 90 -90 L0 5 M3 5 L6 0");
            Add('Ё', 5, "M5 0 L0 0 L0 10 L5 10 M0 5 L4 5 A1.5 11.2 0.6 0.6 0 360 A3.5 11.2 0.6 0.6 0 360");

            // ---- Cyrillic lower case (drawn)
            Add('б', 5, "A2.5 3.25 2.5 3.25 0 360 M0 3.25 L0 9.5 L5 9.5");
            Add('в', 5, "M0 0 L0 6.5 L2.5 6.5 A2.5 1.6 2.5 1.6 90 -90 L0 3.25 M0 3.25 L2.5 3.25");
            Add('г', 4.5f, "M0 0 L0 6.5 M0 6.5 L4.5 6.5");
            Add('д', 5.5f, "M0 0 L0.8 6.5 L4.8 6.5 L5.5 0 M-0.5 0 L6 0");
            Add('ж', 7.5f, "M0 6.5 L3.75 3.25 L7.5 6.5 M0 0 L3.75 3.25 L7.5 0");
            Add('з', 4.5f, "A2.25 4.9 2.25 1.6 160 -90 A2.25 1.6 2.25 1.6 90 -160");
            Add('и', 5, "M0 0 L0 6.5 L5 0 L5 6.5");
            Add('й', 5, "M0 0 L0 6.5 L5 0 L5 6.5 M1.5 8.2 L3.5 8.2");
            Add('к', 4.8f, "M0 0 L0 6.5 M4.8 6.5 L0 2.5 M1.8 3.6 L4.8 0");
            Add('л', 5, "M0 0 L1 6 L4 6 L5 0");
            Add('м', 6.5f, "M0 0 L0 6.5 L3.25 0 L6.5 6.5 L6.5 0");
            Add('н', 5, "M0 0 L0 6.5 L5 6.5 L5 0");
            Add('п', 5, "M0 0 L0 6.5 L5 6.5 L5 0");
            Add('т', 5, "M0 6.5 L5 6.5 M2.5 6.5 L2.5 0");
            Add('ф', 5, "A2.5 3.25 2.5 3.25 0 360 M2.5 -2.5 L2.5 9");
            Add('ц', 5.5f, "M0 6.5 L0 0 L4.5 0 M4.5 6.5 L4.5 -2 L5.6 -2");
            Add('ч', 5, "M0 6.5 L0 3.5 L5 3.5 M5 6.5 L5 0");
            Add('ш', 5.5f, "M0 0 L0 6.5 M2.5 0 L2.5 6.5 M5 0 L5 6.5 M0 0 L5 0");
            Add('щ', 6.5f, "M0 0 L0 6.5 M2.5 0 L2.5 6.5 M5 0 L5 6.5 M0 0 L5 0 M5 0 L5.5 -2 L6.5 -2");
            Add('ъ', 5.5f, "M0 6.5 L2 6.5 M1 6.5 L1 3 L3.5 3 A2 1.6 1.2 1.6 90 -90 M1 0 L3.5 0");
            Add('ы', 5.5f, "M0 0 L0 6.5 M0 3.25 L2.5 3.25 M2.5 0 L2.5 6.5 M2.5 0 L4.5 0 L4.5 6.5");
            Add('ь', 4, "M0 0 L0 6.5 M0 3.25 L3 3.25 A2.5 1.6 2.5 1.6 90 -90");
            Add('э', 5, "M0.5 3.25 L4.5 3.25 A2.5 3.25 2.5 3.25 0 330");
            Add('ю', 7, "M0 0 L0 6.5 M0 3.25 L2 3.25 M2 0 L2 6.5 M2.5 3.25 A4.5 3.25 2 3.25 180 540");
            Add('я', 5.5f, "M5 0 L5 6.5 L1.5 6.5 A1.8 3.25 1.8 3.25 90 -90 L5 3.25 M3.5 3.25 L0 0");
            Add('ё', 5, "M0.5 3.25 L4.5 3.25 A2.5 3.25 2.5 3.25 0 330 A1.5 8.4 0.6 0.6 0 360 A3.5 8.4 0.6 0.6 0 360");

            return g;
        }
    }
}
