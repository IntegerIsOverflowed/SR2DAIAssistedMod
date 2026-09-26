using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------------------------------------------------------
    // SVG <text>: converted to glyph outlines at import time (one VectorShape per <text>, IsText = true, Tag = VectorTextRun),
    // so a text-bearing SVG draws, scales, hit-tests and exports like any other shape - no GDI+, no font at draw time.
    //
    // Supported: <text> with nested <tspan> (any depth), x / y / dx / dy (single values or lists - one per character), rotate,
    // text-anchor (start / middle / end), font-family (comma lists, generic families), font-size (px / pt / em / % / keywords),
    // font-weight, font-style, font (shorthand), letter-spacing, word-spacing, text-decoration underline / line-through,
    // xml:space="preserve", fill / stroke / opacity / transform / clip-path like every other element, <textPath> along a
    // referenced path (startOffset in units or %). Bidi / vertical writing / textLength / font-variant are not implemented.
    //
    // Fonts come from VectorFonts.Find (the PDF importer's substitution table: the installed family when present, else
    // Arial / Liberation / DejaVu by class); VectorFonts.Register("MyFont", bytes) or VectorFonts.Substitute give control.
    // The original text and its baseline are kept in VectorTextRun (shape.Tag; VectorImage.Text() joins them), and SVG export writes the outlines.
    // ------------------------------------------------------------------------------------------------------------------------
    internal static partial class SvgReader
    {
        sealed class TextPen
        {
            public float X, Y;                       // current text position (user units)
            public readonly VectorPath Path = new VectorPath();
            public readonly StringBuilder Text = new StringBuilder();
            public readonly List<(float x0, float x1, float y, float size, bool under, bool strike)> Decor = new List<(float, float, float, float, bool, bool)>();
            public float Start = float.NaN, StartY;   // first glyph origin (for the text run record)
            public float MaxSize;
            public bool PendingSpace;                 // collapsed white space waiting for the next glyph
            public bool AtStart = true;
            public readonly List<(int kind0, int data0, Style style)> Segs = new List<(int, int, Style)>();   // paint changes (tspan fill / stroke) -> separate shapes
            public void Segment(Style st)
            {
                if (Segs.Count > 0) { var last = Segs[Segs.Count - 1]; if (SamePaint(last.style, st)) return; if (last.kind0 == Path.Kinds.Count) { Segs[Segs.Count - 1] = (last.kind0, last.data0, st); return; } }
                Segs.Add((Path.Kinds.Count, Path.Data.Count, st));
            }
            static bool SamePaint(Style a, Style b) => ReferenceEquals(a.Fill, b.Fill) && a.FillRef == b.FillRef && ReferenceEquals(a.Stroke, b.Stroke) && a.StrokeRef == b.StrokeRef && a.FillOpacity == b.FillOpacity && a.StrokeOpacity == b.StrokeOpacity && a.StrokeWidth == b.StrokeWidth && a.Opacity == b.Opacity;
        }

        static void TextElement(XElement e, Ctx c, Style st, Matrix3x2 m, VectorClip? clip, string? myId)
        {
            var pen = new TextPen();
            float? x = FirstNum(e, "x", c), y = FirstNum(e, "y", c);
            pen.X = x ?? 0; pen.Y = y ?? 0;
            // text-anchor works on whole "chunks" (each absolute x starts a new chunk). Layout once to measure, then shift.
            var chunks = new List<(int pathStart, int textStart, float x0, int anchor)>();
            LayoutText(e, c, st, pen, chunks, x.HasValue, true);
            if (pen.Path.IsEmpty && pen.Text.Length == 0) return;
            // apply anchors: shift each chunk's path segment
            ApplyAnchors(pen, chunks);
            // decorations (underline / line-through) as extra rectangles at the end of the path (they take the last segment's paint)
            foreach (var (x0, x1, by, size, under, strike) in pen.Decor)
            {
                float th = MathF.Max(0.5f, size * 0.06f);
                if (under) pen.Path.Rect(x0, by + size * 0.1f, x1 - x0, th);
                if (strike) pen.Path.Rect(x0, by - size * 0.3f, x1 - x0, th);
            }
            string text = pen.Text.ToString();
            float sz = pen.MaxSize > 0 ? pen.MaxSize : st.FontSize;
            var p0 = new PointF(float.IsNaN(pen.Start) ? pen.X : pen.Start, float.IsNaN(pen.Start) ? pen.Y : pen.StartY); var p1 = new PointF(pen.X, pen.Y);
            var d0 = Vector2.Transform(new Vector2(p0.X, p0.Y), m); var d1 = Vector2.Transform(new Vector2(p1.X, p1.Y), m);
            var q2 = Vector2.Transform(new Vector2(p1.X, p1.Y - sz), m); var q3 = Vector2.Transform(new Vector2(p0.X, p0.Y - sz), m);
            var run = new VectorTextRun { Text = text, Start = new PointF(d0.X, d0.Y), End = new PointF(d1.X, d1.Y), Size = sz * MathF.Sqrt(MathF.Abs(m.GetDeterminant())), FontName = st.FontFamily, Quad = new[] { new PointF(d0.X, d0.Y), new PointF(d1.X, d1.Y), new PointF(q2.X, q2.Y), new PointF(q3.X, q3.Y) } };
            string? baseId = myId ?? (c.GroupId != null ? c.GroupId.Substring(c.GroupId.LastIndexOf('/') + 1) + "/" + (c.GroupChild++) : null);
            if (pen.Segs.Count == 0) pen.Segs.Add((0, 0, st));
            int made = 0;
            for (int i = 0; i < pen.Segs.Count; i++)
            {
                var (k0, dd0, sst) = pen.Segs[i];
                int k1 = i + 1 < pen.Segs.Count ? pen.Segs[i + 1].kind0 : pen.Path.Kinds.Count, dd1 = i + 1 < pen.Segs.Count ? pen.Segs[i + 1].data0 : pen.Path.Data.Count;
                if (k1 <= k0) continue;
                var part = pen.Segs.Count == 1 ? pen.Path : Slice(pen.Path, k0, k1, dd0, dd1);
                var shape = MakeShape(part, sst, c, m, clip);
                if (shape == null) continue;
                shape.IsText = true;
                if (made == 0) shape.Tag = run;                                   // the run record once (TextRuns / Text() count each <text> once)
                shape.Id = made == 0 ? baseId : (baseId == null ? null : baseId + "/" + made);
                shape.Group = c.GroupId; shape.Hidden = c.HideDepth > 0;
                c.Img.Shapes.Add(shape); made++;
            }
        }
        static VectorPath Slice(VectorPath src, int k0, int k1, int d0, int d1)
        {
            var p = new VectorPath();
            for (int i = k0; i < k1; i++) p.Kinds.Add(src.Kinds[i]);
            for (int i = d0; i < d1; i++) p.Data.Add(src.Data[i]);
            return p;
        }

        static void ApplyAnchors(TextPen pen, List<(int pathStart, int textStart, float x0, int anchor)> chunks)
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                var (ps, _, x0, anchor) = chunks[i];
                if (anchor == 0) continue;
                float x1 = i + 1 < chunks.Count ? chunks[i + 1].x0 : pen.X;   // the chunk's advance ends where the next starts (or the pen is)
                // width of the chunk = pen advance from x0 to the pen at the chunk end; ChunkEnd was recorded as the next chunk's start x before it moved
                float w = chunkWidths.TryGetValue(i, out var cw) ? cw : x1 - x0;
                float shift = anchor == 1 ? -w / 2 : -w;
                if (MathF.Abs(shift) < 1e-6f) continue;
                int pe = i + 1 < chunks.Count ? chunks[i + 1].pathStart : pen.Path.Data.Count;
                pen.Path.Translate(shift, 0, ps, pe);
                for (int k = 0; k < pen.Decor.Count; k++) { var d = pen.Decor[k]; if (d.x0 >= x0 - 1e-3f && (i + 1 >= chunks.Count || d.x0 < chunks[i + 1].x0 - 1e-3f)) pen.Decor[k] = (d.x0 + shift, d.x1 + shift, d.y, d.size, d.under, d.strike); }
                if (i == 0 && !float.IsNaN(pen.Start)) pen.Start += shift;
                if (i == chunks.Count - 1) pen.X += shift;
            }
            chunkWidths.Clear();
        }
        [ThreadStatic] static Dictionary<int, float>? tChunkWidths;
        static Dictionary<int, float> chunkWidths => tChunkWidths ??= new Dictionary<int, float>();

        /// <summary>Walks a text element's content (text nodes and tspans), appending glyph outlines to the pen.</summary>
        static void LayoutText(XElement e, Ctx c, Style st, TextPen pen, List<(int pathStart, int textStart, float x0, int anchor)> chunks, bool newChunk, bool isRoot)
        {
            bool preserve = string.Equals((string?)e.Attribute(XNamespace.Xml + "space"), "preserve", StringComparison.Ordinal);
            var xs = NumList(e, "x", c); var ys = NumList(e, "y", c); var dxs = NumList(e, "dx", c); var dys = NumList(e, "dy", c); var rots = NumList(e, "rotate", c);
            if (!isRoot && xs.Count > 0) { pen.X = xs[0]; newChunk = true; }
            if (!isRoot && ys.Count > 0) pen.Y = ys[0];
            if (dxs.Count > 0) pen.X += dxs[0];
            if (dys.Count > 0) pen.Y += dys[0];
            if (newChunk || chunks.Count == 0) { EndChunk(pen, chunks); chunks.Add((pen.Path.Data.Count, pen.Text.Length, pen.X, st.TextAnchor)); }
            pen.Segment(st);
            int charIndex = 0;    // per element, for the x / y / dx / dy / rotate lists
            var font = VectorFonts.Find(FirstFamily(st.FontFamily) + (st.FontBold ? " Bold" : "") + (st.FontItalic ? " Italic" : ""), (st.FontBold ? 1 << 18 : 0) | (st.FontItalic ? 64 : 0) | (IsSerif(st.FontFamily) ? 2 : 0));
            if (font == null) { c.Warn("text: no font found for '" + st.FontFamily + "' (VectorFonts.Directories / Register)"); return; }
            foreach (var node in e.Nodes())
            {
                if (node is XText tn)
                {
                    string raw = tn.Value;
                    if (!preserve) raw = raw.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
                    foreach (var rune in raw.EnumerateRunes())
                    {
                        int cp = rune.Value;
                        bool space = cp == ' ' || (!preserve && char.IsWhiteSpace((char)Math.Min(cp, 0xFFFF)));
                        if (space)
                        {
                            if (!preserve) { if (!pen.AtStart) pen.PendingSpace = true; continue; }
                        }
                        if (pen.PendingSpace) { pen.PendingSpace = false; Advance(pen, font, ' ', st, ref charIndex, xs, ys, dxs, dys, rots, chunks, isRoot, true); }
                        Advance(pen, font, cp, st, ref charIndex, xs, ys, dxs, dys, rots, chunks, isRoot, space);
                    }
                }
                else if (node is XElement child)
                {
                    string ln = child.Name.LocalName;
                    if (ln == "tspan" || ln == "a")
                    {
                        var cs = ApplyStyle(child, st, c);
                        if (cs.Hidden) continue;
                        // tspan lists continue the parent's per-character lists? No - each element has its own; pending spaces carry over.
                        LayoutText(child, c, cs, pen, chunks, false, false);
                        pen.Segment(st);
                    }
                    else if (ln == "textPath") { var cs = ApplyStyle(child, st, c); TextPathElement(child, c, cs, pen, chunks); pen.Segment(st); }
                    // other children (title, desc, ...) are skipped
                }
            }
        }
        static void EndChunk(TextPen pen, List<(int pathStart, int textStart, float x0, int anchor)> chunks)
        {
            if (chunks.Count == 0) return;
            var last = chunks[chunks.Count - 1];
            chunkWidths[chunks.Count - 1] = pen.X - last.x0;
        }

        static void Advance(TextPen pen, GlyphFont font, int cp, Style st, ref int ci, List<float> xs, List<float> ys, List<float> dxs, List<float> dys, List<float> rots,
                            List<(int pathStart, int textStart, float x0, int anchor)> chunks, bool isRoot, bool isSpace)
        {
            // per-character positioning lists (the first entry was consumed by the element itself)
            if (ci > 0)
            {
                if (ci < xs.Count) { EndChunk(pen, chunks); pen.X = xs[ci]; chunks.Add((pen.Path.Data.Count, pen.Text.Length, pen.X, st.TextAnchor)); }
                if (ci < ys.Count) pen.Y = ys[ci];
                if (ci < dxs.Count) pen.X += dxs[ci];
                if (ci < dys.Count) pen.Y += dys[ci];
            }
            float rot = rots.Count > 0 ? rots[Math.Min(ci, rots.Count - 1)] : 0;
            ci++;
            float size = st.FontSize;
            int gid = font.GidByUnicode(cp);
            if (gid < 0 && cp <= 0xFFFF) { var names = VectorFontData.GlyphListReverse; if (names.TryGetValue(cp, out var nm)) gid = font.GidByName(nm); }
            if (gid < 0) gid = 0;
            float adv = font.Advance(gid) * size;
            if (float.IsNaN(pen.Start)) { pen.Start = pen.X; pen.StartY = pen.Y; }
            if (!isSpace)
            {
                var g = font.Glyph(gid);
                if (g != null && !g.IsEmpty)
                {
                    var t = new Matrix3x2(size, 0, 0, -size, 0, 0);
                    if (rot != 0) t *= Matrix3x2.CreateRotation(rot * MathF.PI / 180f);
                    t *= Matrix3x2.CreateTranslation(pen.X, pen.Y);
                    pen.Path.Append(g, t);
                }
            }
            if (st.TextDecoration != null)
            {
                bool under = st.TextDecoration.Contains("underline"), strike = st.TextDecoration.Contains("line-through");
                if (under || strike) pen.Decor.Add((pen.X, pen.X + adv + st.LetterSpacing, pen.Y, size, under, strike));
            }
            pen.Text.Append(char.ConvertFromUtf32(cp));
            pen.X += adv + st.LetterSpacing + (isSpace ? st.WordSpacing : 0);
            if (size > pen.MaxSize) pen.MaxSize = size;
            pen.AtStart = false;
        }

        // ------------------------------------------------------------------ <textPath>
        static void TextPathElement(XElement e, Ctx c, Style st, TextPen pen, List<(int pathStart, int textStart, float x0, int anchor)> chunks)
        {
            string? href = Href(e);
            if (href == null || !href.StartsWith('#') || !c.ById.TryGetValue(href.Substring(1), out var target)) { c.Warn("textPath: path reference not found"); return; }
            VectorPath? guide = ShapePath(target, c, target.Name.LocalName);
            if (guide == null || guide.IsEmpty) return;
            var tm = ParseTransform((string?)target.Attribute("transform"));
            if (!tm.IsIdentity) guide = guide.Transformed(tm);
            // flatten the guide once
            var pts = new List<PointF>(); var subs = VectorRender.Flatten(guide, Matrix3x2.Identity, 0.25f, pts);
            if (subs.Count == 0) return;
            var (s0, n0, _) = subs[0];
            var seg = new List<PointF>(n0); for (int i = 0; i < n0; i++) seg.Add(pts[s0 + i]);
            var cum = new float[seg.Count]; for (int i = 1; i < seg.Count; i++) cum[i] = cum[i - 1] + Dist(seg[i - 1], seg[i]);
            float total = cum[seg.Count - 1]; if (total <= 0) return;
            float offset = 0; string? so = (string?)e.Attribute("startOffset");
            if (so != null) { so = so.Trim(); offset = so.EndsWith('%') ? (ParseFloat(so.TrimEnd('%')) ?? 0) / 100f * total : ParseLen(so, c) ?? 0; }
            var font = VectorFonts.Find(FirstFamily(st.FontFamily) + (st.FontBold ? " Bold" : "") + (st.FontItalic ? " Italic" : ""), (st.FontBold ? 1 << 18 : 0) | (st.FontItalic ? 64 : 0) | (IsSerif(st.FontFamily) ? 2 : 0));
            if (font == null) return;
            pen.Segment(st);
            // measure for the anchor
            string text = CollapseText(e); float size = st.FontSize;
            float width = 0; foreach (var r in text.EnumerateRunes()) { int g = Math.Max(0, font.GidByUnicode(r.Value)); width += font.Advance(g) * size + st.LetterSpacing; }
            if (st.TextAnchor == 1) offset -= width / 2; else if (st.TextAnchor == 2) offset -= width;
            float d = offset;
            foreach (var r in text.EnumerateRunes())
            {
                int gid = Math.Max(0, font.GidByUnicode(r.Value)); float adv = font.Advance(gid) * size;
                float mid = d + adv / 2;
                if (mid >= 0 && mid <= total && r.Value != ' ')
                {
                    var (p, tangent) = PointAt(seg, cum, mid);
                    var g = font.Glyph(gid);
                    if (g != null && !g.IsEmpty)
                    {
                        var t = new Matrix3x2(size, 0, 0, -size, -adv / 2, 0) * Matrix3x2.CreateRotation(MathF.Atan2(tangent.Y, tangent.X)) * Matrix3x2.CreateTranslation(p.X, p.Y);
                        pen.Path.Append(g, t);
                    }
                    if (float.IsNaN(pen.Start)) { pen.Start = p.X; pen.StartY = p.Y; }
                    pen.X = p.X; pen.Y = p.Y;
                }
                pen.Text.Append(char.ConvertFromUtf32(r.Value));
                d += adv + st.LetterSpacing + (r.Value == ' ' ? st.WordSpacing : 0);
                if (size > pen.MaxSize) pen.MaxSize = size;
            }
            pen.AtStart = false;
            // anchors were applied along the path: make sure the enclosing chunk does not shift these glyphs again
            EndChunk(pen, chunks); chunks.Add((pen.Path.Data.Count, pen.Text.Length, pen.X, 0));
        }
        static string CollapseText(XElement e)
        {
            var sb = new StringBuilder();
            foreach (var n in e.DescendantNodes()) if (n is XText t) sb.Append(t.Value);
            var s = sb.ToString().Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Trim();
        }
        static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        static (PointF p, PointF tangent) PointAt(List<PointF> seg, float[] cum, float d)
        {
            int i = 1; while (i < cum.Length - 1 && cum[i] < d) i++;
            float l = cum[i] - cum[i - 1]; float t = l > 0 ? (d - cum[i - 1]) / l : 0;
            var a = seg[i - 1]; var b = seg[i];
            return (new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t), new PointF(b.X - a.X, b.Y - a.Y));
        }

        // ------------------------------------------------------------------ font helpers
        static string FirstFamily(string list)
        {
            foreach (var part in list.Split(','))
            {
                var f = part.Trim().Trim('\'', '"');
                if (f.Length == 0) continue;
                switch (f.ToLowerInvariant())
                {
                    case "sans-serif": case "system-ui": case "ui-sans-serif": return "Helvetica";
                    case "serif": case "ui-serif": return "Times";
                    case "monospace": case "ui-monospace": return "Courier";
                    case "cursive": case "fantasy": return "Helvetica";
                }
                return f;
            }
            return "Helvetica";
        }
        static bool IsSerif(string list) { var l = list.ToLowerInvariant(); return (l.Contains("serif") && !l.Contains("sans")) || l.Contains("times") || l.Contains("georgia") || l.Contains("garamond") || l.Contains("book"); }
        static float? FontSizeOf(string v, float parent, Ctx c)
        {
            v = v.Trim();
            switch (v.ToLowerInvariant())
            {
                case "xx-small": return 9; case "x-small": return 10; case "small": return 13; case "medium": return 16; case "large": return 18; case "x-large": return 24; case "xx-large": return 32;
                case "smaller": return parent / 1.2f; case "larger": return parent * 1.2f;
            }
            if (v.EndsWith('%')) { var f = ParseFloat(v.TrimEnd('%')); return f.HasValue ? parent * f.Value / 100 : null; }
            if (v.EndsWith("em", StringComparison.Ordinal)) { var f = ParseFloat(v.Substring(0, v.Length - 2)); return f.HasValue ? parent * f.Value : null; }
            if (v.EndsWith("ex", StringComparison.Ordinal)) { var f = ParseFloat(v.Substring(0, v.Length - 2)); return f.HasValue ? parent * f.Value * 0.5f : null; }
            return ParseLen(v, c);
        }
        /// <summary>font: [style] [weight] size[/line-height] family</summary>
        static void ParseFontShorthand(string v, Style s, Ctx c)
        {
            var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            for (; i < parts.Length; i++)
            {
                string p = parts[i].ToLowerInvariant();
                if (p == "italic" || p == "oblique") s.FontItalic = true;
                else if (p == "bold" || p == "bolder" || (int.TryParse(p, out int fw) && fw >= 600 && fw <= 900 && i + 1 < parts.Length && !char.IsDigit(parts[i + 1][0]) == false)) s.FontBold = true;
                else if (p == "normal" || p == "small-caps" || p == "lighter" || (int.TryParse(p, out int fw2) && fw2 < 600)) { }
                else break;
            }
            if (i < parts.Length)
            {
                string sz = parts[i]; int slash = sz.IndexOf('/'); if (slash >= 0) sz = sz.Substring(0, slash);
                var fs = FontSizeOf(sz, s.FontSize, c); if (fs.HasValue) s.FontSize = fs.Value; i++;
            }
            if (i < parts.Length) s.FontFamily = string.Join(" ", parts, i, parts.Length - i);
        }
        static float? FirstNum(XElement e, string attr, Ctx c) { var l = NumList(e, attr, c); return l.Count > 0 ? l[0] : null; }
        static List<float> NumList(XElement e, string attr, Ctx c)
        {
            var v = (string?)e.Attribute(attr); var r = new List<float>();
            if (string.IsNullOrWhiteSpace(v)) return r;
            foreach (var tok in v.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)) { var f = ParseLen(tok, c); if (f.HasValue) r.Add(f.Value); }
            return r;
        }
    }
}
