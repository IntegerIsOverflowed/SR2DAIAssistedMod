using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sr2d64CSport
{
    /// <summary>
    /// SVG 1.1 / 2 importer: paths and basic shapes, groups, transforms, use / symbol, presentation attributes, inline
    /// style and simple stylesheets (type / .class / #id selectors), linear and radial gradients (with href chains,
    /// gradientUnits / gradientTransform / spreadMethod), clipPath (paths, intersection by nesting), opacity,
    /// fill / stroke opacity, dashes, caps, joins, display / visibility. Text, images, filters, masks and patterns are
    /// reported in <see cref="VectorImage.Warnings"/> (text is skipped, embedded PNG / JPEG images are imported, patterns fall back to their average
    /// colour when they contain solid fills, else mid grey).
    /// </summary>
    internal static partial class SvgReader
    {
        static readonly XNamespace ns = "http://www.w3.org/2000/svg";
        static readonly XNamespace xlink = "http://www.w3.org/1999/xlink";
        static readonly CultureInfo ci = CultureInfo.InvariantCulture;

        sealed class Style
        {
            public VectorPaint? Fill = VectorColor.Black; public bool FillSet;
            public VectorPaint? Stroke; public bool StrokeSet;
            public float FillOpacity = 1, StrokeOpacity = 1, Opacity = 1;
            public float StrokeWidth = 1; public bool EvenOdd, ClipEvenOdd;
            public VectorCap Cap = VectorCap.Butt; public VectorJoin Join = VectorJoin.Miter; public float Miter = 4;
            public float[]? Dash; public float DashOffset;
            public bool Hidden;
            public string? FillRef, StrokeRef;                 // url(#id) to resolve later against the shape's bounds
            public int CurrentColor = unchecked((int)0xFF000000);
            public string? ImageRendering;
            // text
            public string FontFamily = "sans-serif"; public float FontSize = 16; public bool FontBold, FontItalic; public int TextAnchor;   // 0 start 1 middle 2 end
            public float LetterSpacing, WordSpacing; public string? TextDecoration;
            public Style Clone() { var s = (Style)MemberwiseClone(); s.Opacity = 1; s.Hidden = false; return s; }   // opacity / visibility of groups are applied at group level
        }

        sealed class Ctx
        {
            public VectorImage Img = null!;
            public Dictionary<string, XElement> ById = new Dictionary<string, XElement>();
            public Dictionary<string, VectorGradient?> Gradients = new Dictionary<string, VectorGradient?>();
            public Dictionary<string, VectorClip?> Clips = new Dictionary<string, VectorClip?>();
            public List<(string sel, Dictionary<string, string> decl)> Css = new List<(string, Dictionary<string, string>)>();
            public HashSet<string> Warned = new HashSet<string>();
            public int UseDepth;
            public int HideDepth;                      // > 0 while inside a display:none group that has an id (its shapes are imported Hidden)
            public string? GroupId; public int GroupChild;   // nearest enclosing group id: children without an id are named "group/n"
            public float Dpi = 96;
            public void Warn(string w) { if (Warned.Add(w)) Img.Warnings.Add(w); }
        }

        public static VectorImage Read(string xml)
        {
            ImportLog.Begin(); List<string>? sink = null;
            try { var img = ReadCore(xml); sink = img.Warnings; return img; }
            finally { ImportLog.End(sink); }
        }
        static VectorImage ReadCore(string xml)
        {
            XDocument doc;
            try { doc = XDocument.Parse(StripDoctype(xml), LoadOptions.None); }
            catch (System.Xml.XmlException e) { throw new FormatException("not well-formed SVG: " + e.Message, e); }   // one exception type for every format
            var root = doc.Root ?? throw new FormatException("empty SVG");
            if (root.Name.LocalName != "svg") root = root.Descendants().FirstOrDefaultLocal("svg") ?? throw new FormatException("no <svg> element");
            var img = new VectorImage(); var c = new Ctx { Img = img };
            foreach (var e in root.DescendantsAndSelf()) { var id = (string?)e.Attribute("id"); if (id != null && !c.ById.ContainsKey(id)) c.ById[id] = e; }
            foreach (var st in root.Descendants().WhereLocal("style")) ParseCss(st.Value, c.Css);
            var title = root.Elements().FirstOrDefaultLocal("title"); if (title != null) img.Title = title.Value.Trim();

            // size + view box
            float? w = Len(root, "width", c), h = Len(root, "height", c);
            var vb = ParseViewBox((string?)root.Attribute("viewBox"));
            if (vb.HasValue) img.ViewBox = vb.Value;
            else img.ViewBox = new RectangleF(0, 0, w ?? 300, h ?? 150);
            var dva = ParseViewBox((string?)root.Attribute("data-visible-area")); if (dva.HasValue && dva.Value.Width > 0 && dva.Value.Height > 0) img.VisibleArea = dva.Value;
            img.Width = w ?? img.ViewBox.Width; img.Height = h ?? img.ViewBox.Height;
            if (w.HasValue && h.HasValue && vb.HasValue && (Math.Abs(w.Value - vb.Value.Width) > 1e-3 || Math.Abs(h.Value - vb.Value.Height) > 1e-3))
            {   // intrinsic size differs from the view box: the view box is what the shapes live in; scale it to the intrinsic size
                var m = ViewportMatrix(vb.Value, new RectangleF(0, 0, w.Value, h.Value), (string?)root.Attribute("preserveAspectRatio"));
                var st = new Style(); Children(root, c, st, m); img.ViewBox = new RectangleF(0, 0, w.Value, h.Value);
            }
            else Children(root, c, new Style(), Matrix3x2.Identity);
            return img;
        }

        static string StripDoctype(string xml)
        {   // entity declarations in DOCTYPEs are not needed; drop the whole DOCTYPE so the XML reader does not choke on external DTDs
            int i = xml.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return xml;
            int depth = 0, j = i;
            for (; j < xml.Length; j++) { if (xml[j] == '[') depth++; else if (xml[j] == ']') depth--; else if (xml[j] == '>' && depth <= 0) break; }
            return xml.Substring(0, i) + (j < xml.Length ? xml.Substring(j + 1) : "");
        }

        // ------------------------------------------------------------------ tree walk
        static void Children(XElement parent, Ctx c, Style inherited, Matrix3x2 m)
        {
            foreach (var e in parent.Elements()) Element(e, c, inherited, m);
        }
        static void Element(XElement e, Ctx c, Style inherited, Matrix3x2 parentM)
        {
            string n = e.Name.LocalName;
            switch (n)
            {
                case "defs": case "linearGradient": case "radialGradient": case "clipPath": case "symbol": case "marker": case "pattern": case "mask": case "filter": case "style": case "title": case "desc": case "metadata": case "script":
                    return;
            }
            var st = ApplyStyle(e, inherited, c);
            string? myId = (string?)e.Attribute("id");
            // display:none / visibility:hidden: dropped as before - unless the element has an id, then it is imported as a
            // Hidden shape so it keeps its name (VectorImage.Show(name) brings it back; Prune / RemoveHidden drop it)
            if (st.Hidden && string.IsNullOrEmpty(myId)) return;
            if (st.Hidden) { c.HideDepth++; try { Visible(e, c, st, parentM, n, myId); } finally { c.HideDepth--; } return; }
            Visible(e, c, st, parentM, n, myId);
        }
        static void Visible(XElement e, Ctx c, Style st, Matrix3x2 parentM, string n, string? myId)
        {
            var m = ParseTransform((string?)e.Attribute("transform")) * parentM;
            var clip = ClipOf(e, c, st);
            float groupOpacity = st.Opacity;
            switch (n)
            {
                case "g": case "a": case "switch":
                    {
                        string? outerId = c.GroupId; int outerChild = c.GroupChild;
                        if (!string.IsNullOrEmpty(myId)) { c.GroupId = outerId == null ? myId : outerId + "/" + myId; c.GroupChild = 0; }
                        try
                        {
                            if (n == "switch") { var first = e.Elements().FirstOrDefault(); if (first != null) WithGroup(c, groupOpacity, clip, m, () => Element(first, c, st, m)); return; }
                            WithGroup(c, groupOpacity, clip, m, () => Children(e, c, st, m));
                        }
                        finally { c.GroupId = outerId; c.GroupChild = outerChild; }
                        return;
                    }
                case "svg":
                    {   // nested svg: new viewport
                        float x = Len(e, "x", c) ?? 0, y = Len(e, "y", c) ?? 0; float? w = Len(e, "width", c), h = Len(e, "height", c);
                        var vb = ParseViewBox((string?)e.Attribute("viewBox"));
                        var mm = m;
                        if (vb.HasValue) mm = ViewportMatrix(vb.Value, new RectangleF(x, y, w ?? vb.Value.Width, h ?? vb.Value.Height), (string?)e.Attribute("preserveAspectRatio")) * m;
                        else mm = Matrix3x2.CreateTranslation(x, y) * m;
                        WithGroup(c, groupOpacity, clip, m, () => Children(e, c, st, mm)); return;
                    }
                case "use":
                    {
                        string? href = Href(e); if (href == null || !href.StartsWith('#') || !c.ById.TryGetValue(href.Substring(1), out var target)) return;
                        if (c.UseDepth > 16) { c.Warn("use: reference chain too deep"); return; }
                        float x = Len(e, "x", c) ?? 0, y = Len(e, "y", c) ?? 0;
                        var mm = Matrix3x2.CreateTranslation(x, y) * m;
                        c.UseDepth++;
                        try
                        {
                            if (target.Name.LocalName == "symbol" || target.Name.LocalName == "svg")
                            {
                                float? w = Len(e, "width", c) ?? Len(target, "width", c), h = Len(e, "height", c) ?? Len(target, "height", c);
                                var vb = ParseViewBox((string?)target.Attribute("viewBox"));
                                var inner = vb.HasValue ? ViewportMatrix(vb.Value, new RectangleF(0, 0, w ?? vb.Value.Width, h ?? vb.Value.Height), (string?)target.Attribute("preserveAspectRatio")) * mm : mm;
                                var ts = ApplyStyle(target, st, c);
                                WithGroup(c, groupOpacity, clip, m, () => Children(target, c, ts, inner));
                            }
                            else WithGroup(c, groupOpacity, clip, m, () => Element(target, c, st, mm));
                        }
                        finally { c.UseDepth--; }
                        return;
                    }
                case "text": TextElement(e, c, st, m, clip, myId); return;
                case "tspan": case "textPath": return;   // only meaningful inside <text> (handled there)
                case "image":
                    {   // embedded data: URIs (PNG / JPEG) become image paints; external files are not fetched
                        string? href = Href(e); if (href == null) return;
                        var paint = ImagePaintFromHref(href, c); if (paint == null) return;
                        float x = Len(e, "x", c) ?? 0, y = Len(e, "y", c) ?? 0; float w = Len(e, "width", c) ?? paint.Width, h = Len(e, "height", c) ?? paint.Height;
                        if (w <= 0 || h <= 0) return;
                        var box = ViewportMatrix(new RectangleF(0, 0, paint.Width, paint.Height), new RectangleF(x, y, w, h), (string?)e.Attribute("preserveAspectRatio") ?? "xMidYMid meet");
                        var corner = box; paint.Matrix = corner;
                        if (string.Equals((string?)e.Attribute("image-rendering") ?? st.ImageRendering, "pixelated", StringComparison.OrdinalIgnoreCase) || string.Equals(st.ImageRendering, "optimizeSpeed", StringComparison.OrdinalIgnoreCase)) paint.Smooth = false;
                        var ip = new VectorPath().Rect(x, y, w, h);
                        var ish = new VectorShape { Path = ip, Transform = m, Fill = paint, Opacity = st.Opacity * st.FillOpacity, IsImage = true, Clip = clip, Id = myId, Group = c.GroupId, Hidden = c.HideDepth > 0 };
                        c.Img.Shapes.Add(ish); return;
                    }
                case "foreignObject": c.Warn("foreignObject is skipped"); return;
            }
            var path = ShapePath(e, c, n);
            if (path == null) return;
            var shape = MakeShape(path, st, c, m, clip);
            if (shape == null) return;
            shape.Id = myId ?? (c.GroupId != null ? c.GroupId.Substring(c.GroupId.LastIndexOf('/') + 1) + "/" + (c.GroupChild++) : null);
            shape.Group = c.GroupId;
            shape.Hidden = c.HideDepth > 0;
            c.Img.Shapes.Add(shape);
        }
        /// <summary>Runs a group's children, then applies group opacity and clip to the shapes it produced.</summary>
        static void WithGroup(Ctx c, float opacity, VectorClip? clip, Matrix3x2 m, Action body)
        {
            int start = c.Img.Shapes.Count;
            body();
            if (opacity >= 1 && clip == null) return;
            var clipDev = clip?.Transformed(m);   // clip paths are in the group's user space; shapes are stored with their own transform, so express the clip in image space
            for (int i = start; i < c.Img.Shapes.Count; i++)
            {
                var s = c.Img.Shapes[i];
                if (opacity < 1) s.Opacity *= opacity;      // (approximation: per-shape instead of group compositing; overlapping children double up)
                if (clipDev != null)
                {   // the shape's clip is in the shape's local space: bring the group clip there
                    if (Matrix3x2.Invert(s.Transform, out var inv)) { var local = clipDev.Transformed(inv); s.Clip = s.Clip == null ? local : Merge(s.Clip, local); }
                }
            }
        }
        static VectorClip Merge(VectorClip a, VectorClip b) { var c = new VectorClip(); c.Paths.AddRange(a.Paths); c.Paths.AddRange(b.Paths); return c; }

        static VectorShape? MakeShape(VectorPath path, Style st, Ctx c, Matrix3x2 m, VectorClip? clip)
        {
            var s = new VectorShape { Path = path, Transform = m, EvenOdd = st.EvenOdd, StrokeWidth = st.StrokeWidth, Cap = st.Cap, Join = st.Join, MiterLimit = st.Miter, Dash = st.Dash, DashOffset = st.DashOffset, Opacity = st.Opacity };
            s.Fill = ResolvePaint(st.Fill, st.FillRef, st.FillOpacity, c);
            s.Stroke = st.StrokeWidth > 0 ? ResolvePaint(st.Stroke, st.StrokeRef, st.StrokeOpacity, c) : null;
            if (clip != null) s.Clip = clip;   // clip-path on the element itself: same user space as the element (before its own transform? no - after: clipPathUnits=userSpaceOnUse refers to the element's user space, which includes its transform)
            return s.Fill == null && s.Stroke == null ? null : s;
        }
        static VectorPaint? ResolvePaint(VectorPaint? p, string? url, float opacity, Ctx c)
        {
            if (url != null)
            {
                var g = Gradient(url, c);
                if (g != null) { if (opacity < 1) { g = (VectorGradient)g.Clone(); for (int i = 0; i < g.Stops.Count; i++) g.Stops[i] = new VectorStop(g.Stops[i].Offset, ScaleA(g.Stops[i].Argb, opacity)); } return g; }
                if (c.ById.TryGetValue(url, out var el) && el.Name.LocalName == "pattern")
                {
                    // a pattern that is just one <image> in user space (what VectorImage.ToSvg writes for pictures): use the picture as the paint
                    var kids = new List<XElement>(el.Elements()); 
                    if (kids.Count == 1 && kids[0].Name.LocalName == "image" && ((string?)el.Attribute("patternUnits") ?? "objectBoundingBox") == "userSpaceOnUse" && Href(kids[0]) is string ih)
                    {
                        var ip = ImagePaintFromHref(ih, c);
                        if (ip != null)
                        {
                            float pw = Len(el, "width", c) ?? ip.Width, ph = Len(el, "height", c) ?? ip.Height, iw = Len(kids[0], "width", c) ?? pw, ihh = Len(kids[0], "height", c) ?? ph;
                            ip.Matrix = Matrix3x2.CreateScale(iw / ip.Width, ihh / ip.Height) * Matrix3x2.CreateTranslation(Len(kids[0], "x", c) ?? 0, Len(kids[0], "y", c) ?? 0) * Matrix3x2.CreateTranslation(Len(el, "x", c) ?? 0, Len(el, "y", c) ?? 0) * ParseTransform((string?)el.Attribute("patternTransform"));
                            if (string.Equals((string?)kids[0].Attribute("image-rendering"), "pixelated", StringComparison.OrdinalIgnoreCase)) ip.Smooth = false;
                            return ip;   // (no tiling: the picture covers the shape once, transparent outside)
                        }
                    }
                    c.Warn("pattern fills are replaced by their average colour");
                    int col = PatternAverage(el, c); return new VectorColor(ScaleA(col, opacity));
                }
                if (p == null) return null;   // url(#missing) with no fallback -> none
            }
            if (p is VectorColor vc) return opacity < 1 ? new VectorColor(ScaleA(vc.Argb, opacity)) : vc;
            return p;
        }
        static int ScaleA(int argb, float f) { int a = (int)MathF.Round(((argb >> 24) & 255) * f); return (argb & 0xFFFFFF) | (Math.Clamp(a, 0, 255) << 24); }
        static int PatternAverage(XElement pat, Ctx c)
        {
            long r = 0, g = 0, b = 0, n = 0;
            foreach (var e in pat.Descendants())
            {
                var f = (string?)e.Attribute("fill") ?? StyleProp(e, "fill"); if (f == null) continue;
                var col = ParseColor(f, unchecked((int)0xFF000000)); if (col == null) continue;
                r += (col.Value >> 16) & 255; g += (col.Value >> 8) & 255; b += col.Value & 255; n++;
            }
            if (n == 0) return unchecked((int)0xFF808080);
            return unchecked((int)0xFF000000) | (int)(r / n) << 16 | (int)(g / n) << 8 | (int)(b / n);
        }

        // ------------------------------------------------------------------ basic shapes
        static VectorPath? ShapePath(XElement e, Ctx c, string n)
        {
            var p = new VectorPath();
            switch (n)
            {
                case "path": { var d = (string?)e.Attribute("d"); if (string.IsNullOrWhiteSpace(d)) return null; return SvgPathParser.Parse(d, p); }
                case "rect":
                    {
                        float x = Len(e, "x", c) ?? 0, y = Len(e, "y", c) ?? 0, w = Len(e, "width", c) ?? 0, h = Len(e, "height", c) ?? 0;
                        if (w <= 0 || h <= 0) return null;
                        float? rx = Len(e, "rx", c), ry = Len(e, "ry", c);
                        float RX = rx ?? ry ?? 0, RY = ry ?? rx ?? 0;
                        return RX > 0 && RY > 0 ? p.RoundRect(x, y, w, h, RX, RY) : p.Rect(x, y, w, h);
                    }
                case "circle": { float r = Len(e, "r", c) ?? 0; if (r <= 0) return null; return p.Circle(Len(e, "cx", c) ?? 0, Len(e, "cy", c) ?? 0, r); }
                case "ellipse": { float rx = Len(e, "rx", c) ?? 0, ry = Len(e, "ry", c) ?? 0; if (rx <= 0 || ry <= 0) return null; return p.Ellipse(Len(e, "cx", c) ?? 0, Len(e, "cy", c) ?? 0, rx, ry); }
                case "line": return p.MoveTo(Len(e, "x1", c) ?? 0, Len(e, "y1", c) ?? 0).LineTo(Len(e, "x2", c) ?? 0, Len(e, "y2", c) ?? 0);
                case "polyline": case "polygon":
                    {
                        var pts = ParseNumbers((string?)e.Attribute("points") ?? ""); if (pts.Count < 4) return null;
                        p.MoveTo(pts[0], pts[1]); for (int i = 2; i + 1 < pts.Count; i += 2) p.LineTo(pts[i], pts[i + 1]);
                        if (n == "polygon") p.Close(); return p;
                    }
                default:
                    if (e.Name.Namespace == ns || e.Name.Namespace == XNamespace.None) c.Warn($"<{n}> is not supported");
                    return null;
            }
        }

        // ------------------------------------------------------------------ styles
        static Style ApplyStyle(XElement e, Style inh, Ctx c)
        {
            var s = inh.Clone();
            var props = new Dictionary<string, string>();
            // presentation attributes
            foreach (var a in e.Attributes()) if (a.Name.Namespace == XNamespace.None && IsStyleProp(a.Name.LocalName)) props[a.Name.LocalName] = a.Value;
            // stylesheet rules (type, .class, #id; specificity by order type < class < id)
            string tag = e.Name.LocalName; string? id = (string?)e.Attribute("id"); var classes = ((string?)e.Attribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pass in new[] { 0, 1, 2 })
                foreach (var (sel, decl) in c.Css)
                {
                    bool hit = pass == 0 ? sel == tag || sel == "*" : pass == 1 ? sel.StartsWith('.') && Array.IndexOf(classes, sel.Substring(1)) >= 0 || (sel.Contains('.') && !sel.StartsWith('.') && sel.Split('.')[0] == tag && Array.IndexOf(classes, sel.Split('.')[1]) >= 0)
                             : sel.StartsWith('#') && sel.Substring(1) == id;
                    if (hit) foreach (var kv in decl) props[kv.Key] = kv.Value;
                }
            // inline style
            var style = (string?)e.Attribute("style");
            if (style != null) foreach (var kv in ParseDecl(style)) props[kv.Key] = kv.Value;

            foreach (var kv in props)
            {
                string v = kv.Value.Trim();
                if (v == "inherit") continue;
                switch (kv.Key)
                {
                    case "color": { var col = ParseColor(v, s.CurrentColor); if (col.HasValue) s.CurrentColor = col.Value; break; }
                }
            }
            foreach (var kv in props)
            {
                string v = kv.Value.Trim();
                if (v == "inherit" || v.Length == 0) continue;
                switch (kv.Key)
                {
                    case "fill": SetPaint(v, s, true); break;
                    case "stroke": SetPaint(v, s, false); break;
                    case "fill-opacity": s.FillOpacity = Num01(v); break;
                    case "stroke-opacity": s.StrokeOpacity = Num01(v); break;
                    case "opacity": s.Opacity = Num01(v); break;
                    case "image-rendering": s.ImageRendering = v; break;
                    case "font-family": s.FontFamily = v; break;
                    case "font-size": { float? fs = FontSizeOf(v, s.FontSize, c); if (fs.HasValue) s.FontSize = fs.Value; break; }
                    case "font-weight": s.FontBold = v == "bold" || v == "bolder" || (int.TryParse(v, out int fw) && fw >= 600); break;
                    case "font-style": s.FontItalic = v == "italic" || v == "oblique"; break;
                    case "font": ParseFontShorthand(v, s, c); break;
                    case "text-anchor": s.TextAnchor = v == "middle" ? 1 : v == "end" ? 2 : 0; break;
                    case "letter-spacing": s.LetterSpacing = v == "normal" ? 0 : ParseLen(v, c) ?? 0; break;
                    case "word-spacing": s.WordSpacing = v == "normal" ? 0 : ParseLen(v, c) ?? 0; break;
                    case "text-decoration": s.TextDecoration = v == "none" ? null : v; break;
                    case "stroke-width": s.StrokeWidth = ParseLen(v, c) ?? s.StrokeWidth; break;
                    case "fill-rule": s.EvenOdd = v == "evenodd"; break;
                    case "clip-rule": s.ClipEvenOdd = v == "evenodd"; break;
                    case "stroke-linecap": s.Cap = v == "round" ? VectorCap.Round : v == "square" ? VectorCap.Square : VectorCap.Butt; break;
                    case "stroke-linejoin": s.Join = v == "round" ? VectorJoin.Round : v == "bevel" ? VectorJoin.Bevel : VectorJoin.Miter; break;
                    case "stroke-miterlimit": s.Miter = ParseFloat(v) ?? s.Miter; break;
                    case "stroke-dasharray":
                        if (v == "none") s.Dash = null;
                        else { var l = new List<float>(); foreach (var tkn in v.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)) { var f = ParseLen(tkn, c); if (f.HasValue) l.Add(f.Value); } if (l.Count % 2 == 1) l.AddRange(l.ToArray()); s.Dash = l.Count > 0 ? l.ToArray() : null; }
                        break;
                    case "stroke-dashoffset": s.DashOffset = ParseLen(v, c) ?? 0; break;
                    case "display": if (v == "none") s.Hidden = true; break;
                    case "visibility": if (v == "hidden" || v == "collapse") s.Hidden = true; break;
                }
            }
            return s;
        }
        static void SetPaint(string v, Style s, bool fill)
        {
            VectorPaint? p; string? url = null;
            if (v.StartsWith("url(", StringComparison.Ordinal))
            {
                int a = v.IndexOf('#'), b = v.IndexOf(')'); if (a >= 0 && b > a) url = v.Substring(a + 1, b - a - 1).Trim().Trim('"', '\'');
                string rest = b >= 0 ? v.Substring(b + 1).Trim() : "";
                p = rest.Length > 0 && rest != "none" ? Col(rest, s) : (rest == "none" ? null : VectorColor.Black);   // fallback colour
            }
            else if (v == "none") p = null;
            else p = Col(v, s);
            if (fill) { s.Fill = p; s.FillRef = url; s.FillSet = true; } else { s.Stroke = p; s.StrokeRef = url; s.StrokeSet = true; }
            static VectorPaint? Col(string t, Style s) { if (t == "currentColor") return new VectorColor(s.CurrentColor); var c = ParseColor(t, s.CurrentColor); return c.HasValue ? new VectorColor(c.Value) : null; }
        }
        static bool IsStyleProp(string n) => n is "font-family" or "font-size" or "font-weight" or "font-style" or "font" or "text-anchor" or "letter-spacing" or "word-spacing" or "text-decoration" or "fill" or "stroke" or "fill-opacity" or "stroke-opacity" or "opacity" or "stroke-width" or "fill-rule" or "clip-rule" or "stroke-linecap" or "stroke-linejoin" or "stroke-miterlimit" or "stroke-dasharray" or "stroke-dashoffset" or "display" or "visibility" or "color" or "stop-color" or "stop-opacity" or "image-rendering";
        static string? StyleProp(XElement e, string name)
        {
            var st = (string?)e.Attribute("style"); if (st == null) return null;
            foreach (var kv in ParseDecl(st)) if (kv.Key == name) return kv.Value; return null;
        }
        static List<KeyValuePair<string, string>> ParseDecl(string s)
        {
            var l = new List<KeyValuePair<string, string>>();
            foreach (var part in s.Split(';')) { int i = part.IndexOf(':'); if (i > 0) l.Add(new KeyValuePair<string, string>(part.Substring(0, i).Trim().ToLowerInvariant(), part.Substring(i + 1).Trim())); }
            return l;
        }
        static void ParseCss(string css, List<(string, Dictionary<string, string>)> into)
        {
            css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
            css = Regex.Replace(css, @"<!\[CDATA\[|\]\]>", "");
            foreach (Match m in Regex.Matches(css, @"([^{}]+)\{([^}]*)\}"))
            {
                var decl = new Dictionary<string, string>(); foreach (var kv in ParseDecl(m.Groups[2].Value)) decl[kv.Key] = kv.Value;
                foreach (var sel in m.Groups[1].Value.Split(',')) { string s = sel.Trim(); if (s.Length > 0 && !s.StartsWith('@')) into.Add((s, decl)); }
            }
        }
        static float Num01(string v) { var f = ParseFloat(v.TrimEnd('%')); if (!f.HasValue) return 1; return Math.Clamp(v.EndsWith('%') ? f.Value / 100 : f.Value, 0, 1); }

        // ------------------------------------------------------------------ paint servers
        static VectorGradient? Gradient(string id, Ctx c)
        {
            if (c.Gradients.TryGetValue(id, out var g)) return g;
            c.Gradients[id] = null;   // cycle guard
            if (!c.ById.TryGetValue(id, out var e)) return null;
            g = BuildGradient(e, c, 0);
            c.Gradients[id] = g; return g;
        }
        static VectorGradient? BuildGradient(XElement e, Ctx c, int depth)
        {
            string n = e.Name.LocalName; if (n != "linearGradient" && n != "radialGradient") return null;
            VectorGradient? baseG = null;
            string? href = Href(e);
            if (href != null && href.StartsWith('#') && depth < 8 && c.ById.TryGetValue(href.Substring(1), out var be)) baseG = BuildGradient(be, c, depth + 1);
            var g = new VectorGradient { Radial = n == "radialGradient" };
            if (baseG != null)
            {   // inherit everything not given here
                g.Stops.AddRange(baseG.Stops); g.Matrix = baseG.Matrix; g.ObjectBoundingBox = baseG.ObjectBoundingBox; g.Spread = baseG.Spread;
                g.X1 = baseG.X1; g.Y1 = baseG.Y1; g.X2 = baseG.X2; g.Y2 = baseG.Y2; g.Cx = baseG.Cx; g.Cy = baseG.Cy; g.R = baseG.R; g.Fx = baseG.Fx; g.Fy = baseG.Fy; g.Fr = baseG.Fr;
                if (baseG.Radial != g.Radial) { if (g.Radial) { g.Cx = g.Cy = 0.5f; g.R = 0.5f; g.Fx = g.Fy = 0.5f; } else { g.X1 = g.Y1 = g.Y2 = 0; g.X2 = 1; } }
            }
            else
            {
                g.ObjectBoundingBox = true;
                if (g.Radial) { g.Cx = g.Cy = 0.5f; g.R = 0.5f; g.Fx = g.Fy = 0.5f; } else { g.X1 = g.Y1 = g.Y2 = 0; g.X2 = 1; }
            }
            var units = (string?)e.Attribute("gradientUnits"); if (units != null) g.ObjectBoundingBox = units != "userSpaceOnUse";
            var tr = (string?)e.Attribute("gradientTransform"); if (tr != null) g.Matrix = ParseTransform(tr);
            var sp = (string?)e.Attribute("spreadMethod"); if (sp != null) g.Spread = sp == "reflect" ? VectorSpread.Reflect : sp == "repeat" ? VectorSpread.Repeat : VectorSpread.Pad;
            float Coord(string name, float cur) { var v = (string?)e.Attribute(name); if (v == null) return cur; var f = ParseFloat(v.TrimEnd('%')); if (!f.HasValue) return cur; return v.EndsWith('%') ? f.Value / 100 : (g.ObjectBoundingBox ? f.Value : (ParseLen(v, c) ?? f.Value)); }
            if (g.Radial)
            {
                g.Cx = Coord("cx", g.Cx); g.Cy = Coord("cy", g.Cy); g.R = Coord("r", g.R);
                bool hasFx = e.Attribute("fx") != null, hasFy = e.Attribute("fy") != null;
                g.Fx = hasFx ? Coord("fx", g.Cx) : (baseG != null && e.Attribute("cx") == null ? g.Fx : g.Cx);
                g.Fy = hasFy ? Coord("fy", g.Cy) : (baseG != null && e.Attribute("cy") == null ? g.Fy : g.Cy);
                g.Fr = Coord("fr", g.Fr);
            }
            else { g.X1 = Coord("x1", g.X1); g.Y1 = Coord("y1", g.Y1); g.X2 = Coord("x2", g.X2); g.Y2 = Coord("y2", g.Y2); }
            var stops = new List<VectorStop>();
            foreach (var s in e.Elements().WhereLocal("stop"))
            {
                float off = 0; var ov = (string?)s.Attribute("offset"); if (ov != null) { var f = ParseFloat(ov.TrimEnd('%')); if (f.HasValue) off = ov.EndsWith('%') ? f.Value / 100 : f.Value; }
                off = Math.Clamp(off, 0, 1); if (stops.Count > 0 && off < stops[^1].Offset) off = stops[^1].Offset;
                string? sc = (string?)s.Attribute("stop-color"), so = (string?)s.Attribute("stop-opacity");
                var st = (string?)s.Attribute("style"); if (st != null) foreach (var kv in ParseDecl(st)) { if (kv.Key == "stop-color") sc = kv.Value; else if (kv.Key == "stop-opacity") so = kv.Value; }
                // class rules for stops
                var cls = ((string?)s.Attribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var (sel, decl) in c.Css) if (sel.StartsWith('.') && Array.IndexOf(cls, sel.Substring(1)) >= 0) { if (decl.TryGetValue("stop-color", out var v1)) sc = v1; if (decl.TryGetValue("stop-opacity", out var v2)) so = v2; }
                int col = sc == null ? unchecked((int)0xFF000000) : (sc == "currentColor" ? unchecked((int)0xFF000000) : ParseColor(sc, unchecked((int)0xFF000000)) ?? unchecked((int)0xFF000000));
                if (so != null) col = ScaleA(col, Num01(so));
                stops.Add(new VectorStop(off, col));
            }
            if (stops.Count > 0) g.Stops = stops;
            if (g.Stops.Count == 0) return null;   // "none" per spec
            return g;
        }
        static VectorClip? ClipOf(XElement e, Ctx c, Style st)
        {
            var v = (string?)e.Attribute("clip-path") ?? StyleProp(e, "clip-path"); if (v == null || !v.StartsWith("url(", StringComparison.Ordinal)) return null;
            int a = v.IndexOf('#'), b = v.IndexOf(')'); if (a < 0 || b <= a) return null;
            string id = v.Substring(a + 1, b - a - 1).Trim();
            if (c.Clips.TryGetValue(id, out var clip)) return clip;
            c.Clips[id] = null;
            if (!c.ById.TryGetValue(id, out var ce) || ce.Name.LocalName != "clipPath") return null;
            var units = (string?)ce.Attribute("clipPathUnits");
            if (units == "objectBoundingBox") { c.Warn("clipPathUnits=objectBoundingBox is approximated as userSpaceOnUse"); }
            var m = ParseTransform((string?)ce.Attribute("transform"));
            // union of the children (each child is one region; SVG unions them) - stored as one path with all sub-paths under non-zero... children may differ in rule; use non-zero union
            var union = new VectorPath(); bool eo = false;
            foreach (var ch in ce.Elements())
            {
                XElement el = ch;
                var cm = ParseTransform((string?)ch.Attribute("transform")) * m;
                if (ch.Name.LocalName == "use") { var h = Href(ch); if (h != null && h.StartsWith('#') && c.ById.TryGetValue(h.Substring(1), out var t)) { el = t; cm = Matrix3x2.CreateTranslation(Len(ch, "x", c) ?? 0, Len(ch, "y", c) ?? 0) * cm; cm = ParseTransform((string?)t.Attribute("transform")) * cm; } else continue; }
                if (el.Name.LocalName == "text") { c.Warn("text inside clipPath is ignored"); continue; }
                var cs = ApplyStyle(el, st, c); if (cs.Hidden) continue;
                var p = ShapePath(el, c, el.Name.LocalName); if (p == null) continue;
                union.Append(p, cm); eo |= cs.ClipEvenOdd;
            }
            var result = union.IsEmpty ? new VectorClip(new VectorPath().Rect(0, 0, 0, 0)) : new VectorClip(union, eo && ce.Elements().Count() == 1);
            // nested clip on the clipPath element itself
            var outer = ClipOf(ce, c, st); if (outer != null) result = Merge(result, outer);
            c.Clips[id] = result; return result;
        }

        // ------------------------------------------------------------------ parsing helpers
        static VectorImagePaint? ImagePaintFromHref(string href, Ctx c)
        {
            href = href.Trim();
            if (!href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { c.Warn("external <image> files are not loaded (embed them as data: URIs)"); return null; }
            int comma = href.IndexOf(','); if (comma < 0) return null;
            string meta = href.Substring(5, comma - 5); byte[] bytes;
            try { bytes = meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? Convert.FromBase64String(System.Text.RegularExpressions.Regex.Replace(href.Substring(comma + 1), @"\s+", "")) : Encoding.Latin1.GetBytes(Uri.UnescapeDataString(href.Substring(comma + 1))); }
            catch { c.Warn("<image> data URI could not be decoded"); return null; }
            try
            {
                if (PngDecoder.IsPng(bytes)) return PngDecoder.Decode(bytes);
                if (WebP.IsWebP(bytes)) { var (ww, wh, wpx) = WebP.Decode(bytes); bool anyA = false; foreach (int wc in wpx) if ((wc >> 24 & 255) != 255) { anyA = true; break; } return new VectorImagePaint(wpx, ww, wh) { HasAlpha = anyA }; }
                if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                {
                    var jd = new JpegDecoder(bytes); var samples = jd.Decode(); int n = jd.Components, w = jd.Width, h = jd.Height; var px = new int[w * h];
                    for (int i = 0, k = 0; i < px.Length; i++, k += n)
                    {
                        if (n == 1) px[i] = unchecked((int)0xFF000000) | samples[k] << 16 | samples[k] << 8 | samples[k];
                        else if (n == 4) { int cc = samples[k], mm = samples[k + 1], yy = samples[k + 2], kk = samples[k + 3]; if (jd.Adobe) { cc = 255 - cc; mm = 255 - mm; yy = 255 - yy; kk = 255 - kk; } px[i] = unchecked((int)0xFF000000) | ((255 - cc) * (255 - kk) / 255) << 16 | ((255 - mm) * (255 - kk) / 255) << 8 | ((255 - yy) * (255 - kk) / 255); }
                        else px[i] = unchecked((int)0xFF000000) | samples[k] << 16 | samples[k + 1] << 8 | samples[k + 2];
                    }
                    return new VectorImagePaint(px, w, h);
                }
                if (bytes.Length > 5 && (bytes[0] == '<' || meta.Contains("svg")))
                {   // nested SVG picture: import its shapes instead (vector stays vector)
                    c.Warn("<image> with an embedded SVG is not rasterised (use <use> or inline it)"); return null;
                }
            }
            catch (Exception ex) { c.Warn("<image> could not be decoded: " + ex.Message); return null; }
            c.Warn("<image> format not supported (PNG and JPEG data URIs are)"); return null;
        }
        static string? Href(XElement e) => (string?)e.Attribute("href") ?? (string?)e.Attribute(xlink + "href");
        static RectangleF? ParseViewBox(string? v)
        {
            if (v == null) return null; var n = ParseNumbers(v); if (n.Count < 4 || n[2] <= 0 || n[3] <= 0) return null; return new RectangleF(n[0], n[1], n[2], n[3]);
        }
        static Matrix3x2 ViewportMatrix(RectangleF vb, RectangleF port, string? par)
        {
            float sx = port.Width / vb.Width, sy = port.Height / vb.Height;
            string a = (par ?? "xMidYMid meet").Trim(); bool none = a.StartsWith("none", StringComparison.Ordinal);
            if (!none)
            {
                bool slice = a.Contains("slice"); float s = slice ? Math.Max(sx, sy) : Math.Min(sx, sy); sx = sy = s;
            }
            float tx = port.X - vb.X * sx, ty = port.Y - vb.Y * sy;
            if (!none)
            {
                float ex = port.Width - vb.Width * sx, ey = port.Height - vb.Height * sy;
                if (a.Contains("xMid")) tx += ex / 2; else if (a.Contains("xMax")) tx += ex;
                if (a.Contains("YMid")) ty += ey / 2; else if (a.Contains("YMax")) ty += ey;
            }
            return new Matrix3x2(sx, 0, 0, sy, tx, ty);
        }
        public static Matrix3x2 ParseTransform(string? t)
        {
            var m = Matrix3x2.Identity; if (string.IsNullOrWhiteSpace(t)) return m;
            foreach (Match mt in Regex.Matches(t, @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)"))
            {
                var a = ParseNumbers(mt.Groups[2].Value); Matrix3x2 k = Matrix3x2.Identity;
                switch (mt.Groups[1].Value)
                {
                    case "matrix": if (a.Count >= 6) k = new Matrix3x2(a[0], a[1], a[2], a[3], a[4], a[5]); break;
                    case "translate": k = Matrix3x2.CreateTranslation(a.Count > 0 ? a[0] : 0, a.Count > 1 ? a[1] : 0); break;
                    case "scale": k = Matrix3x2.CreateScale(a.Count > 0 ? a[0] : 1, a.Count > 1 ? a[1] : (a.Count > 0 ? a[0] : 1)); break;
                    case "rotate": k = a.Count >= 3 ? Matrix3x2.CreateRotation(a[0] * MathF.PI / 180, new Vector2(a[1], a[2])) : Matrix3x2.CreateRotation((a.Count > 0 ? a[0] : 0) * MathF.PI / 180); break;
                    case "skewX": k = new Matrix3x2(1, 0, MathF.Tan((a.Count > 0 ? a[0] : 0) * MathF.PI / 180), 1, 0, 0); break;
                    case "skewY": k = new Matrix3x2(1, MathF.Tan((a.Count > 0 ? a[0] : 0) * MathF.PI / 180), 0, 1, 0, 0); break;
                }
                m = k * m;   // transforms apply right-to-left in SVG: "translate(..) rotate(..)" = rotate first -> row-vector order: k * m
            }
            return m;
        }
        static List<float> ParseNumbers(string s)
        {
            var l = new List<float>();
            foreach (Match m in Regex.Matches(s, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")) l.Add(float.Parse(m.Value, NumberStyles.Float, ci));
            return l;
        }
        static float? ParseFloat(string s) => float.TryParse(s.Trim(), NumberStyles.Float, ci, out var f) ? f : null;
        static float? Len(XElement e, string attr, Ctx c) { var v = (string?)e.Attribute(attr); return v == null ? null : ParseLen(v, c); }
        static float? ParseLen(string v, Ctx c)
        {
            v = v.Trim(); if (v.Length == 0) return null;
            int i = v.Length; while (i > 0 && (char.IsLetter(v[i - 1]) || v[i - 1] == '%')) i--;
            var f = ParseFloat(v.Substring(0, i)); if (!f.HasValue) return null;
            string u = v.Substring(i).ToLowerInvariant();
            return u switch { "px" or "" => f, "pt" => f * c.Dpi / 72, "pc" => f * c.Dpi / 6, "mm" => f * c.Dpi / 25.4f, "cm" => f * c.Dpi / 2.54f, "in" => f * c.Dpi, "em" => f * 16, "ex" => f * 8, "%" => f, _ => f };
        }
        public static int? ParseColor(string s, int currentColor)
        {
            s = s.Trim();
            if (s.Length == 0 || s == "none" || s == "transparent") return s == "transparent" ? 0 : null;
            if (s == "currentColor") return currentColor;
            if (s[0] == '#')
            {
                string h = s.Substring(1);
                if (h.Length == 3 || h.Length == 4) { var sb = new StringBuilder(); foreach (var ch in h) { sb.Append(ch); sb.Append(ch); } h = sb.ToString(); }
                if (h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, ci, out var rgb)) return unchecked((int)0xFF000000) | rgb;
                if (h.Length == 8 && uint.TryParse(h, NumberStyles.HexNumber, ci, out var rgba)) return (int)((rgba >> 8) | (rgba & 255) << 24);   // #RRGGBBAA
                return null;
            }
            var m = Regex.Match(s, @"^(rgba?|hsla?)\s*\(([^)]*)\)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var parts = m.Groups[2].Value.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) return null;
                float Comp(string p, float max) { bool pc = p.EndsWith('%'); var f = ParseFloat(p.TrimEnd('%')) ?? 0; return pc ? f / 100 * max : f; }
                float a = parts.Length > 3 ? (parts[3].EndsWith('%') ? Comp(parts[3], 1) : ParseFloat(parts[3]) ?? 1) : 1;
                int A = Math.Clamp((int)MathF.Round(a * 255), 0, 255);
                if (m.Groups[1].Value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
                {
                    int R = Math.Clamp((int)MathF.Round(Comp(parts[0], 255)), 0, 255), G = Math.Clamp((int)MathF.Round(Comp(parts[1], 255)), 0, 255), B = Math.Clamp((int)MathF.Round(Comp(parts[2], 255)), 0, 255);
                    return A << 24 | R << 16 | G << 8 | B;
                }
                float hh = (ParseFloat(parts[0].TrimEnd('d', 'e', 'g')) ?? 0) / 360f, ss = Comp(parts[1], 1), ll = Comp(parts[2], 1);
                var (r, g, b) = HslToRgb(hh, ss, ll); return A << 24 | r << 16 | g << 8 | b;
            }
            if (Named.TryGetValue(s.ToLowerInvariant(), out var named)) return unchecked((int)0xFF000000) | named;
            return null;
        }
        static (int, int, int) HslToRgb(float h, float s, float l)
        {
            float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            float F(float t) { t -= MathF.Floor(t); if (t < 1f / 6) return p + (q - p) * 6 * t; if (t < 0.5f) return q; if (t < 2f / 3) return p + (q - p) * (2f / 3 - t) * 6; return p; }
            return ((int)MathF.Round(F(h + 1f / 3) * 255), (int)MathF.Round(F(h) * 255), (int)MathF.Round(F(h - 1f / 3) * 255));
        }
        static readonly Dictionary<string, int> Named = BuildNamed();
        static Dictionary<string, int> BuildNamed()
        {
            const string t = "aliceblue f0f8ff antiquewhite faebd7 aqua 00ffff aquamarine 7fffd4 azure f0ffff beige f5f5dc bisque ffe4c4 black 000000 blanchedalmond ffebcd blue 0000ff blueviolet 8a2be2 brown a52a2a burlywood deb887 cadetblue 5f9ea0 chartreuse 7fff00 chocolate d2691e coral ff7f50 cornflowerblue 6495ed cornsilk fff8dc crimson dc143c cyan 00ffff darkblue 00008b darkcyan 008b8b darkgoldenrod b8860b darkgray a9a9a9 darkgreen 006400 darkgrey a9a9a9 darkkhaki bdb76b darkmagenta 8b008b darkolivegreen 556b2f darkorange ff8c00 darkorchid 9932cc darkred 8b0000 darksalmon e9967a darkseagreen 8fbc8f darkslateblue 483d8b darkslategray 2f4f4f darkslategrey 2f4f4f darkturquoise 00ced1 darkviolet 9400d3 deeppink ff1493 deepskyblue 00bfff dimgray 696969 dimgrey 696969 dodgerblue 1e90ff firebrick b22222 floralwhite fffaf0 forestgreen 228b22 fuchsia ff00ff gainsboro dcdcdc ghostwhite f8f8ff gold ffd700 goldenrod daa520 gray 808080 green 008000 greenyellow adff2f grey 808080 honeydew f0fff0 hotpink ff69b4 indianred cd5c5c indigo 4b0082 ivory fffff0 khaki f0e68c lavender e6e6fa lavenderblush fff0f5 lawngreen 7cfc00 lemonchiffon fffacd lightblue add8e6 lightcoral f08080 lightcyan e0ffff lightgoldenrodyellow fafad2 lightgray d3d3d3 lightgreen 90ee90 lightgrey d3d3d3 lightpink ffb6c1 lightsalmon ffa07a lightseagreen 20b2aa lightskyblue 87cefa lightslategray 778899 lightslategrey 778899 lightsteelblue b0c4de lightyellow ffffe0 lime 00ff00 limegreen 32cd32 linen faf0e6 magenta ff00ff maroon 800000 mediumaquamarine 66cdaa mediumblue 0000cd mediumorchid ba55d3 mediumpurple 9370db mediumseagreen 3cb371 mediumslateblue 7b68ee mediumspringgreen 00fa9a mediumturquoise 48d1cc mediumvioletred c71585 midnightblue 191970 mintcream f5fffa mistyrose ffe4e1 moccasin ffe4b5 navajowhite ffdead navy 000080 oldlace fdf5e6 olive 808000 olivedrab 6b8e23 orange ffa500 orangered ff4500 orchid da70d6 palegoldenrod eee8aa palegreen 98fb98 paleturquoise afeeee palevioletred db7093 papayawhip ffefd5 peachpuff ffdab9 peru cd853f pink ffc0cb plum dda0dd powderblue b0e0e6 purple 800080 rebeccapurple 663399 red ff0000 rosybrown bc8f8f royalblue 4169e1 saddlebrown 8b4513 salmon fa8072 sandybrown f4a460 seagreen 2e8b57 seashell fff5ee sienna a0522d silver c0c0c0 skyblue 87ceeb slateblue 6a5acd slategray 708090 slategrey 708090 snow fffafa springgreen 00ff7f steelblue 4682b4 tan d2b48c teal 008080 thistle d8bfd8 tomato ff6347 turquoise 40e0d0 violet ee82ee wheat f5deb3 white ffffff whitesmoke f5f5f5 yellow ffff00 yellowgreen 9acd32";
            var d = new Dictionary<string, int>(); var p = t.Split(' ');
            for (int i = 0; i + 1 < p.Length; i += 2) d[p[i]] = int.Parse(p[i + 1], NumberStyles.HexNumber, ci);
            return d;
        }

        // XElement helpers that ignore namespaces (many SVGs in the wild have none or a wrong one)
        internal static XElement? FirstOrDefaultLocal(this IEnumerable<XElement> e, string local) { foreach (var x in e) if (x.Name.LocalName == local) return x; return null; }
        internal static IEnumerable<XElement> WhereLocal(this IEnumerable<XElement> e, string local) { foreach (var x in e) if (x.Name.LocalName == local) yield return x; }
        static int Count(this IEnumerable<XElement> e) { int n = 0; foreach (var _ in e) n++; return n; }
        static XElement? FirstOrDefault(this IEnumerable<XElement> e) { foreach (var x in e) return x; return null; }
    }
}
