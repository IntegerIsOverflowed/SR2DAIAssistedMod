using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------------------------------------------------------
    // VectorImage edits: recolouring.
    //
    // These change the CONTENT of the container once (like Transform / Flatten / Crop), they are not render options: after
    // img.Recolor(a, b) every fill, stroke and gradient stop that was colour a IS colour b - the SVG you save afterwards has
    // b in it, VectorSprite re-rasterises once (Version is bumped) and then blits as before. Compare VectorRenderOptions
    // .ForceColor, which paints everything in one colour per draw call without touching the model.
    //
    //     img.Recolor(0xFF336699, 0xFFFF8800);                  // exact match, alpha kept from the target, fills AND strokes
    //     img.Recolor(a, b, target: PaintTarget.Stroke);         // outlines only (Fill = solid fills only, Both = default)
    //     img.SwapColors(red, blue);                             // both directions at once
    //     img.Recolor(red, blue, tolerance: 24, keepAlpha: true) // near matches; per-shape alpha / opacity untouched
    //     var used = img.Palette(target: PaintTarget.Fill);      // colours in use, most-used first (for a picker)
    //     img.Recolor(c => Grey(c));                             // any mapping function
    //
    // By object: shapes carry an Id (SVG id / whatever you set) and a Group (the ids of the SVG <g> groups they came
    // from, "outer/inner"). A name matches a shape's Id or any of its group ids, exactly or with * / ? wildcards
    // ("wheel*"), case-insensitive - so a group's id addresses every shape inside it. Children of a named group without
    // an id of their own are named "group/n" by the reader so each is still addressable.
    //     img.SetFill("logo", blue);  img.SetStroke("logo*", black, width: 2)  img.Hide("guides")  img.Remove("draft*")
    //     img.Hidden("guides", false) / shape.Hidden        // hidden shapes stay in the container (saved as display:none)
    //     var names = img.Names();                          // every distinct Id
    //
    // Merging / order:
    //     var both = VectorImage.Merge(bottom, top);             // top drawn above bottom (same view box as bottom, top fitted)
    //     bottom.Merge(top, shuffle: true);                      // in place; shuffle = the combined shape order is randomised
    //     img.Shuffle(seed)                                      // randomise the draw order of the existing shapes
    // ------------------------------------------------------------------------------------------------------------------------

    /// <summary>Which paints an edit touches: solid fills, outlines (strokes) or both.</summary>
    /// <summary>One row of <see cref="VectorImage.Objects"/>: shape index, its name (or the "[auto...]" it would get), kind, group path, bounds and hidden flag.</summary>
    internal readonly struct VectorObjectInfo
    {
        public readonly int Index; public readonly string Name, Kind; public readonly string? Group; public readonly RectangleF Bounds; public readonly bool Hidden;
        public VectorObjectInfo(int index, string name, string kind, string? group, RectangleF bounds, bool hidden) { Index = index; Name = name; Kind = kind; Group = group; Bounds = bounds; Hidden = hidden; }
        public override string ToString() => $"{Name} ({Kind}{(Hidden ? ", hidden" : "")}) {Bounds.Width:0.#}x{Bounds.Height:0.#} @ {Bounds.X:0.#},{Bounds.Y:0.#}";
    }
    internal enum PaintTarget { Both = 0, Fill = 1, Stroke = 2 }

    /// <summary>One entry of <see cref="VectorImage.Palette"/>: a colour and how many paints (fills / strokes / stops) use it.</summary>
    internal readonly struct VectorPaletteEntry
    {
        public readonly int Argb; public readonly int Uses;
        public VectorPaletteEntry(int argb, int uses) { Argb = argb; Uses = uses; }
        public Color Color => Color.FromArgb(Argb);
        public override string ToString() => $"#{Argb:X8} x{Uses}";
    }

    internal sealed partial class VectorImage
    {
        /// <summary>
        /// Replaces colour <paramref name="from"/> by <paramref name="to"/> in every fill, stroke and gradient stop.
        /// <paramref name="tolerance"/> = maximum per-channel RGB distance (0 = exact); <paramref name="keepAlpha"/> = keep the
        /// alpha the paint had (the RGB of <paramref name="to"/> is used, its alpha ignored) - the default, so a translucent
        /// shape stays translucent; false = the alpha of <paramref name="to"/> is written too. Returns the number of paints changed.
        /// </summary>
        public int Recolor(int from, int to, int tolerance = 0, bool keepAlpha = true, PaintTarget target = PaintTarget.Both)
            => Recolor(c => Near(c, from, tolerance, keepAlpha) ? (keepAlpha ? (c & unchecked((int)0xFF000000)) | (to & 0xFFFFFF) : to) : c, target);
        public int Recolor(Color from, Color to, int tolerance = 0, bool keepAlpha = true, PaintTarget target = PaintTarget.Both) => Recolor(from.ToArgb(), to.ToArgb(), tolerance, keepAlpha, target);

        /// <summary>Swaps two colours in one pass (a -> b and b -> a; without the pass ordering problem of two Recolor calls).</summary>
        public int SwapColors(int a, int b, int tolerance = 0, bool keepAlpha = true, PaintTarget target = PaintTarget.Both)
        {
            return Recolor(c =>
            {
                if (Near(c, a, tolerance, keepAlpha)) return keepAlpha ? (c & unchecked((int)0xFF000000)) | (b & 0xFFFFFF) : b;
                if (Near(c, b, tolerance, keepAlpha)) return keepAlpha ? (c & unchecked((int)0xFF000000)) | (a & 0xFFFFFF) : a;
                return c;
            }, target);
        }
        public int SwapColors(Color a, Color b, int tolerance = 0, bool keepAlpha = true, PaintTarget target = PaintTarget.Both) => SwapColors(a.ToArgb(), b.ToArgb(), tolerance, keepAlpha, target);

        /// <summary>
        /// Applies <paramref name="map"/> (old ARGB -> new ARGB) to every solid fill / stroke and every gradient stop of every
        /// shape; the model is edited in place and <see cref="Version"/> is bumped when anything changed. Returns the number
        /// of paints changed. Use this for anything beyond a plain swap (tint, greyscale, palette remap ...).
        /// </summary>
        public int Recolor(Func<int, int> map, PaintTarget target = PaintTarget.Both, Func<VectorShape, bool>? where = null)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            int changed = 0;
            foreach (var s in Shapes)
            {
                if (where != null && !where(s)) continue;
                // the default fill is a shared static instance - give the shape its own before editing it
                if (ReferenceEquals(s.Fill, VectorColor.Black)) s.Fill = new VectorColor(VectorColor.Black.Argb);
                if (ReferenceEquals(s.Stroke, VectorColor.Black)) s.Stroke = new VectorColor(VectorColor.Black.Argb);
                if (target != PaintTarget.Stroke) changed += Apply(s.Fill, map);
                if (target != PaintTarget.Fill) changed += Apply(s.Stroke, map);
            }
            if (changed > 0) Touch();
            return changed;
        }

        /// <summary>Replaces several colours at once (a palette remap): each key becomes its value; exact matches only.</summary>
        public int Recolor(IReadOnlyDictionary<int, int> mapping, bool keepAlpha = true, PaintTarget target = PaintTarget.Both)
        {
            if (mapping == null || mapping.Count == 0) return 0;
            return Recolor(c =>
            {
                if (mapping.TryGetValue(c, out int v)) return v;
                if (keepAlpha && mapping.TryGetValue(c | unchecked((int)0xFF000000), out v)) return (c & unchecked((int)0xFF000000)) | (v & 0xFFFFFF);
                return c;
            }, target);
        }

        // ------------------------------------------------------------------ by object name (VectorShape.Id)
        /// <summary>Shapes whose <see cref="VectorShape.Id"/> matches <paramref name="name"/> (exact, or with * / ? wildcards; a group id matches its "group/child" members). Case-insensitive.</summary>
        public IEnumerable<VectorShape> Named(string name)
        {
            if (string.IsNullOrEmpty(name)) yield break;
            foreach (var s in Shapes) if (NameMatches(s, name)) yield return s;
        }
        /// <summary>Index of the first shape called <paramref name="name"/>, or -1.</summary>
        public int IndexOf(string name) { for (int i = 0; i < Shapes.Count; i++) if (NameMatches(Shapes[i], name)) return i; return -1; }
        /// <summary>Every distinct name in draw order: group ids (once, when first met) and the ids of shapes that have their own (not the generated "group/n" and "autoKindNN" ones unless <paramref name="includeGenerated"/>).</summary>
        public List<string> Names(bool includeGenerated = false)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var list = new List<string>();
            foreach (var s in Shapes)
            {
                if (!string.IsNullOrEmpty(s.Group)) foreach (var g in s.Group.Split('/')) if (seen.Add(g)) list.Add(g);
                if (string.IsNullOrEmpty(s.Id)) continue;
                if (!includeGenerated && (s.Id.IndexOf('/') > 0 || s.Id.StartsWith("auto", StringComparison.Ordinal) && s.Id.Length > 4 && char.IsDigit(s.Id[^1]))) continue;
                if (seen.Add(s.Id)) list.Add(s.Id);
            }
            return list;
        }
        /// <summary>Gives every shape without an Id a name (<paramref name="prefix"/> + index) so it can be addressed; returns how many were named.</summary>
        public int NameUnnamed(string prefix = "shape")
        {
            int n = 0;
            for (int i = 0; i < Shapes.Count; i++) if (string.IsNullOrEmpty(Shapes[i].Id)) { Shapes[i].Id = prefix + i; n++; }
            return n;
        }
        /// <summary>
        /// Names every unnamed shape after what it is - <c>autoRect01</c>, <c>autoEllipse02</c>, <c>autoPath03</c> (closed),
        /// <c>autoOpenPath04</c> (a stroked line / polyline), <c>autoText05</c>, <c>autoBitmap06</c> - numbered in draw order
        /// with one counter per kind (two digits minimum, more when needed), so PDF / EPS / AI pictures whose shapes carry no
        /// ids become addressable like an SVG. Names already present stay (a second call only names what is still unnamed and
        /// continues each counter after the highest number in use). The "auto" prefix marks them as generated:
        /// <see cref="Names"/> lists them; <see cref="Names(bool)"/> with false hides them along with the "group/n" ones.
        /// Returns how many shapes were named.
        /// </summary>
        public int NameByKind(string prefix = "auto")
        {
            var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Shapes)
            {
                if (string.IsNullOrEmpty(s.Id) || !s.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                int i = s.Id.Length; while (i > prefix.Length && char.IsDigit(s.Id[i - 1])) i--;
                if (i == s.Id.Length || i == prefix.Length) continue;
                string kind = s.Id.Substring(prefix.Length, i - prefix.Length);
                if (int.TryParse(s.Id.AsSpan(i), out int num)) next[kind] = Math.Max(next.TryGetValue(kind, out int cur) ? cur : 0, num);
            }
            int n = 0;
            foreach (var s in Shapes)
            {
                if (!string.IsNullOrEmpty(s.Id)) continue;
                string kind = KindOf(s);
                int k = (next.TryGetValue(kind, out int cur) ? cur : 0) + 1; next[kind] = k;
                s.Id = prefix + kind + k.ToString("00", System.Globalization.CultureInfo.InvariantCulture); n++;
            }
            return n;
        }
        /// <summary>What a shape is, for naming and listing: "Text", "Bitmap", "Rect", "Ellipse", "Path" (closed) or "OpenPath".</summary>
        public static string KindOf(VectorShape s)
        {
            if (s.IsText) return "Text";
            if (s.IsImage) return "Bitmap";
            var p = s.Path;
            if (p.IsAxisRect(out _)) return "Rect";
            if (IsEllipse(p)) return "Ellipse";
            return p.HasOpenSubPath && s.Fill == null ? "OpenPath" : "Path";
        }
        /// <summary>Four cubics that close, symmetric about their centre: what every exporter writes for a circle / ellipse.</summary>
        static bool IsEllipse(VectorPath p)
        {
            int n = p.Kinds.Count; if (n < 5 || n > 6 || p.Kinds[0] != VectorPath.K.Move) return false;
            int cubics = 0; for (int i = 1; i < n; i++) { if (p.Kinds[i] == VectorPath.K.Cubic) cubics++; else if (p.Kinds[i] != VectorPath.K.Close) return false; }
            if (cubics != 4) return false;
            // anchor points: start + the end point of each cubic
            var d = p.Data; Span<float> ax = stackalloc float[5], ay = stackalloc float[5];
            ax[0] = d[0]; ay[0] = d[1];
            for (int c = 0; c < 4; c++) { int o = 2 + c * 6; ax[c + 1] = d[o + 4]; ay[c + 1] = d[o + 5]; }
            if (Math.Abs(ax[4] - ax[0]) > 1e-3f * (1 + Math.Abs(ax[0])) || Math.Abs(ay[4] - ay[0]) > 1e-3f * (1 + Math.Abs(ay[0]))) return false;
            float cx = (ax[0] + ax[2]) / 2, cy = (ay[0] + ay[2]) / 2, cx2 = (ax[1] + ax[3]) / 2, cy2 = (ay[1] + ay[3]) / 2;
            float size = Math.Max(1e-3f, Math.Max(Math.Abs(ax[2] - ax[0]) + Math.Abs(ay[2] - ay[0]), Math.Abs(ax[3] - ax[1]) + Math.Abs(ay[3] - ay[1])));
            if (Math.Abs(cx - cx2) > 0.02f * size || Math.Abs(cy - cy2) > 0.02f * size) return false;   // both diameters share a centre
            // the two diameters are (nearly) perpendicular and each cubic's controls lie near the tangent direction: good enough
            float dx1 = ax[2] - ax[0], dy1 = ay[2] - ay[0], dx2 = ax[3] - ax[1], dy2 = ay[3] - ay[1];
            float dot = dx1 * dx2 + dy1 * dy2, l1 = dx1 * dx1 + dy1 * dy1, l2 = dx2 * dx2 + dy2 * dy2;
            return dot * dot < 0.05f * l1 * l2;
        }
        /// <summary>
        /// Every shape as (index, name, kind, bounds, hidden) in draw order - the object list for a UI. Unnamed shapes get
        /// the name they WOULD get from <see cref="NameByKind"/> (shown in brackets, e.g. "[autoPath03]") unless
        /// <paramref name="nameThem"/>, which calls <see cref="NameByKind"/> first so the names are real.
        /// </summary>
        public List<VectorObjectInfo> Objects(bool nameThem = false)
        {
            if (nameThem) NameByKind();
            var list = new List<VectorObjectInfo>(Shapes.Count); var next = new Dictionary<string, int>();
            for (int i = 0; i < Shapes.Count; i++)
            {
                var s = Shapes[i]; string kind = KindOf(s); string name;
                if (!string.IsNullOrEmpty(s.Id)) name = s.Id;
                else { int k = (next.TryGetValue(kind, out int cur) ? cur : 0) + 1; next[kind] = k; name = "[auto" + kind + k.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "]"; }
                list.Add(new VectorObjectInfo(i, name, kind, s.Group, VectorRender.ShapeBounds(s, Matrix3x2.Identity), s.Hidden));
            }
            return list;
        }
        internal static bool NameMatches(VectorShape s, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            bool wild = name.IndexOf('*') >= 0 || name.IndexOf('?') >= 0;
            if (!string.IsNullOrEmpty(s.Id) && (wild ? Wild(s.Id, 0, name, 0) : string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase))) return true;
            if (string.IsNullOrEmpty(s.Group)) return false;
            if (!wild && string.Equals(s.Group, name, StringComparison.OrdinalIgnoreCase)) return true;   // full "outer/inner" path
            foreach (var g in s.Group.Split('/')) if (wild ? Wild(g, 0, name, 0) : string.Equals(g, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        static bool Wild(string s, int i, string p, int j)
        {
            while (j < p.Length)
            {
                if (p[j] == '*') { for (int k = i; k <= s.Length; k++) if (Wild(s, k, p, j + 1)) return true; return false; }
                if (i >= s.Length) return false;
                if (p[j] != '?' && char.ToUpperInvariant(p[j]) != char.ToUpperInvariant(s[i])) return false;
                i++; j++;
            }
            return i == s.Length;
        }

        /// <summary>Recolours only the shapes called <paramref name="name"/> (fill / stroke / both): from -> to as in <see cref="Recolor(int,int,int,bool,PaintTarget)"/>.</summary>
        public int Recolor(string name, int from, int to, int tolerance = 0, bool keepAlpha = true, PaintTarget target = PaintTarget.Both)
            => Recolor(c => Near(c, from, tolerance, keepAlpha) ? (keepAlpha ? (c & unchecked((int)0xFF000000)) | (to & 0xFFFFFF) : to) : c, target, s => NameMatches(s, name));

        /// <summary>Sets the solid fill of the shapes called <paramref name="name"/> (alpha 0 = no fill). Gradients are replaced by the colour. Returns shapes changed.</summary>
        public int SetFill(string name, int argb) => SetPaint(name, argb, PaintTarget.Fill, null);
        /// <summary>Sets the outline colour (and optionally width) of the shapes called <paramref name="name"/>; alpha 0 = no stroke.</summary>
        public int SetStroke(string name, int argb, float? width = null) => SetPaint(name, argb, PaintTarget.Stroke, width);
        /// <summary>Sets fill and / or stroke of the named shapes to one colour.</summary>
        public int SetColor(string name, int argb, PaintTarget target = PaintTarget.Both, float? strokeWidth = null) => SetPaint(name, argb, target, strokeWidth);
        int SetPaint(string name, int argb, PaintTarget target, float? width)
        {
            int n = 0;
            foreach (var s in Named(name))
            {
                bool none = (argb >> 24 & 255) == 0;
                if (target != PaintTarget.Stroke) s.Fill = none ? null : new VectorColor(argb);
                if (target != PaintTarget.Fill) { s.Stroke = none ? null : new VectorColor(argb); if (width.HasValue) s.StrokeWidth = width.Value; }
                n++;
            }
            if (n > 0) Touch();
            return n;
        }

        /// <summary>Hides (or shows again) the shapes called <paramref name="name"/>: they stay in the container, are not drawn and are saved with display:none.</summary>
        public int Hidden(string name, bool hidden = true) { int n = 0; foreach (var s in Named(name)) { if (s.Hidden != hidden) { s.Hidden = hidden; n++; } } if (n > 0) Touch(); return n; }
        public int Hide(string name) => Hidden(name, true);
        public int Show(string name) => Hidden(name, false);
        /// <summary>Removes the shapes called <paramref name="name"/> from the container for good. Returns how many.</summary>
        public int Remove(string name) { int n = Shapes.RemoveAll(s => NameMatches(s, name)); if (n > 0) Touch(); return n; }
        /// <summary>Removes every hidden shape (what Hide left behind).</summary>
        public int RemoveHidden() { int n = Shapes.RemoveAll(s => s.Hidden); if (n > 0) Touch(); return n; }

        // ------------------------------------------------------------------ merge / order
        /// <summary>
        /// Adds every shape of <paramref name="top"/> above this image's shapes. <paramref name="fit"/> = how the other picture
        /// is placed: null = same coordinates (its view-box origin moved onto ours - two pages of the same size line up),
        /// a rectangle = fitted into it (aspect kept). <paramref name="shuffle"/> = afterwards the draw order of ALL shapes is
        /// randomised (<paramref name="seed"/>; a "which is on top" free-for-all for collages / particle sheets). Shapes are
        /// copied; names that clash get a "#2" suffix so Named() keeps addressing one picture's parts.
        /// </summary>
        public VectorImage Merge(VectorImage top, RectangleF? fit = null, bool shuffle = false, int seed = 0)
        {
            if (top == null) throw new ArgumentNullException(nameof(top));
            var names = new HashSet<string>(Names(true), StringComparer.OrdinalIgnoreCase);
            int start = Shapes.Count;
            if (fit.HasValue) Add(top, fit.Value);
            else Add(top, Matrix3x2.CreateTranslation(ViewBox.X, ViewBox.Y));
            for (int i = start; i < Shapes.Count; i++)
            {   // names that already exist here get a "#2" suffix (ids and group ids alike)
                var s = Shapes[i];
                if (!string.IsNullOrEmpty(s.Id) && names.Contains(s.Id.Split('/')[0])) { string head = s.Id.Split('/')[0]; s.Id = head + "#2" + s.Id.Substring(head.Length); }
                if (!string.IsNullOrEmpty(s.Group)) { var segs = s.Group.Split('/'); for (int k = 0; k < segs.Length; k++) if (names.Contains(segs[k])) segs[k] += "#2"; s.Group = string.Join("/", segs); }
            }
            if (shuffle) Shuffle(seed);
            return this;
        }
        /// <summary>New image = <paramref name="bottom"/> with <paramref name="top"/> drawn above it (see the instance <see cref="Merge"/>).</summary>
        public static VectorImage Merge(VectorImage bottom, VectorImage top, RectangleF? fit = null, bool shuffle = false, int seed = 0) => bottom.Clone().Merge(top, fit, shuffle, seed);

        /// <summary>Randomises the draw order of the shapes (Fisher-Yates; <paramref name="seed"/> 0 = time-based).</summary>
        public VectorImage Shuffle(int seed = 0)
        {
            var r = seed == 0 ? new Random() : new Random(seed);
            for (int i = Shapes.Count - 1; i > 0; i--) { int j = r.Next(i + 1); (Shapes[i], Shapes[j]) = (Shapes[j], Shapes[i]); }
            Touch(); return this;
        }
        /// <summary>Reverses the draw order (what was on top is at the bottom).</summary>
        public VectorImage ReverseOrder() { Shapes.Reverse(); Touch(); return this; }
        /// <summary>Moves the shapes called <paramref name="name"/> to the top (drawn last) or the bottom of the stack, keeping their relative order.</summary>
        public int BringToFront(string name) => MoveNamed(name, true);
        public int SendToBack(string name) => MoveNamed(name, false);
        int MoveNamed(string name, bool front)
        {
            var hit = new List<VectorShape>(Named(name)); if (hit.Count == 0) return 0;
            Shapes.RemoveAll(s => hit.Contains(s));
            if (front) Shapes.AddRange(hit); else Shapes.InsertRange(0, hit);
            Touch(); return hit.Count;
        }

        // ------------------------------------------------------------------ in-place geometry (mirrors Sprite.Edit.cs)
        // Everything bakes a matrix into the shapes (Transform(m)); the view box is kept where an
        // editor would keep it: flips and rotations turn the picture inside the SAME view box
        // (about its centre) so the image keeps its place and, for quarter turns, swaps width / height.
        /// <summary>Rotates the picture by <paramref name="degrees"/> (clockwise, any angle) about the view-box centre (or the given point), in place. For quarter turns the view box turns with it (width / height swap); otherwise the view box grows to the rotated bounds unless <paramref name="keepViewBox"/>.</summary>
        public VectorImage Rotate(float degrees, float? pivotX = null, float? pivotY = null, bool keepViewBox = false)
        {
            degrees = ((degrees % 360f) + 360f) % 360f; if (degrees == 0f) return this;
            var vb = ViewBox; float cx = pivotX ?? vb.X + vb.Width / 2, cy = pivotY ?? vb.Y + vb.Height / 2;
            var m = Matrix3x2.CreateRotation(degrees * MathF.PI / 180f, new Vector2(cx, cy));
            foreach (var sh in Shapes) sh.Bake(m);
            if (_visible.HasValue) _visible = VectorRender.TransformRect(_visible.Value, m);
            bool quarter = degrees % 90f == 0f;
            if (!keepViewBox) { if (quarter && !pivotX.HasValue && !pivotY.HasValue && degrees != 180f) ViewBox = new RectangleF(cx - vb.Height / 2, cy - vb.Width / 2, vb.Height, vb.Width); else if (!quarter) ViewBox = VectorRender.TransformRect(vb, m); }
            Width = ViewBox.Width; Height = ViewBox.Height; Touch(); return this;
        }
        /// <summary>90° clockwise, in place (view box swaps width / height).</summary>
        public VectorImage RotateCW() => Rotate(90f);
        /// <summary>90° counter-clockwise, in place.</summary>
        public VectorImage RotateCCW() => Rotate(270f);
        /// <summary>Mirrors the picture left-right inside its view box, in place.</summary>
        public VectorImage FlipX() => Transform(Matrix3x2.CreateScale(-1f, 1f, new Vector2(ViewBox.X + ViewBox.Width / 2, ViewBox.Y + ViewBox.Height / 2)));
        /// <summary>Mirrors the picture top-bottom inside its view box, in place.</summary>
        public VectorImage FlipY() => Transform(Matrix3x2.CreateScale(1f, -1f, new Vector2(ViewBox.X + ViewBox.Width / 2, ViewBox.Y + ViewBox.Height / 2)));
        public VectorImage Mirror() => FlipX();
        /// <summary>Scales the picture (and its view box) by a factor about the view-box origin, in place; stroke widths scale along.</summary>
        public VectorImage Scale(float factor) => Scale(factor, factor);
        public VectorImage Scale(float sx, float sy) { if (!(sx > 0) || !(sy > 0)) return this; return Transform(Matrix3x2.CreateScale(sx, sy, new Vector2(ViewBox.X, ViewBox.Y))); }
        /// <summary>Rescales the picture so that its view box becomes width x height (0 keeps the aspect from the other side), in place.</summary>
        public VectorImage Resize(float width, float height)
        {
            if (ViewBox.Width <= 0 || ViewBox.Height <= 0) return this;
            if (width <= 0 && height <= 0) return this;
            if (width <= 0) width = height * ViewBox.Width / ViewBox.Height; if (height <= 0) height = width * ViewBox.Height / ViewBox.Width;
            return Scale(width / ViewBox.Width, height / ViewBox.Height);
        }
        /// <summary>Moves the picture by (dx, dy) inside its view box (the view box stays), in place.</summary>
        public VectorImage Shift(float dx, float dy)
        {
            if (dx == 0 && dy == 0) return this;
            var m = Matrix3x2.CreateTranslation(dx, dy);
            foreach (var sh in Shapes) sh.Bake(m);
            if (_visible.HasValue) { var v = _visible.Value; v.Offset(dx, dy); _visible = v; }
            Touch(); return this;
        }
        /// <summary>Changes the view box (the "canvas") without moving the geometry: an editor's canvas-size dialog. Crop(margin) = fit to content.</summary>
        public VectorImage Recanvas(RectangleF viewBox) { ViewBox = viewBox; Width = viewBox.Width; Height = viewBox.Height; Touch(); return this; }
        /// <summary>Adds (negative = removes) a margin on every side of the view box, in place.</summary>
        public VectorImage Expand(float left, float top, float right, float bottom) => Recanvas(RectangleF.FromLTRB(ViewBox.Left - left, ViewBox.Top - top, ViewBox.Right + right, ViewBox.Bottom + bottom));
        public VectorImage Expand(float all) => Expand(all, all, all, all);
        /// <summary>Same as <see cref="Crop(float)"/> (view box = content bounds): editor naming.</summary>
        public VectorImage Trim(float margin = 0) => Crop(margin);

        // ------------------------------------------------------------------ text and pictures (PDF / EPS / AI / SVG imports)
        /// <summary>Text runs of the picture (shapes flagged <see cref="VectorShape.IsText"/>) in drawing order: their Unicode text and bounds.</summary>
        public List<(string text, RectangleF bounds)> TextRuns()
        {
            var r = new List<(string, RectangleF)>();
            foreach (var s in Shapes)
            {
                if (!s.IsText) continue;
                if (s.Tag is VectorTextRun tr) { if (tr.Text.Length > 0) r.Add((tr.Text, VectorRender.TransformRect(tr.Bounds, s.Transform))); }
                else if (s.Tag is string t && t.Length > 0) r.Add((t, VectorRender.TransformRect(s.Path.ControlBounds(), s.Transform)));
            }
            return r;
        }
        /// <summary>The page text as plain text: runs on the same baseline joined with spaces, lines separated by new lines (reading order = drawing order per line, sorted top to bottom).</summary>
        public string Text()
        {
            var runs = TextRuns(); if (runs.Count == 0) return "";
            var lines = new List<(float y, float h, List<(float x, float r, string t)> parts)>();
            foreach (var (t, b) in runs)
            {
                float cy = b.Y + b.Height / 2; bool placed = false;
                foreach (var ln in lines) if (Math.Abs(ln.y - cy) < Math.Max(ln.h, b.Height) * 0.5f) { ln.parts.Add((b.X, b.Right, t)); placed = true; break; }
                if (!placed) lines.Add((cy, b.Height, new List<(float, float, string)> { (b.X, b.Right, t) }));
            }
            lines.Sort((a, c) => a.y.CompareTo(c.y));
            var sb = new System.Text.StringBuilder();
            foreach (var ln in lines)
            {
                ln.parts.Sort((a, c) => a.x.CompareTo(c.x));
                float right = float.NaN;
                for (int i = 0; i < ln.parts.Count; i++)
                {   // a space between runs only when there is a visible gap (glyph-by-glyph PDFs emit one run per letter)
                    var (x, r, t) = ln.parts[i];
                    if (i > 0 && !t.StartsWith(' ') && !ln.parts[i - 1].t.EndsWith(' ') && (float.IsNaN(right) || x - right > ln.h * 0.12f)) sb.Append(x - right > ln.h * 2.5f ? "  " : " ");
                    sb.Append(t); right = float.IsNaN(right) ? r : Math.Max(right, r);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
        /// <summary>Placed pictures (shapes flagged <see cref="VectorShape.IsImage"/>) with their pixel data and bounds in image coordinates.</summary>
        public List<(VectorImagePaint image, RectangleF bounds)> Images()
        {
            var r = new List<(VectorImagePaint, RectangleF)>();
            foreach (var s in Shapes) if (s.IsImage && s.Fill is VectorImagePaint ip) r.Add((ip, VectorRender.TransformRect(s.Path.ControlBounds(), s.Transform)));
            return r;
        }
        /// <summary>Removes every text run (keeps the graphics). Returns the number of shapes removed.</summary>
        public int RemoveText() { int n = Shapes.RemoveAll(s => s.IsText); if (n > 0) Touch(); return n; }
        /// <summary>Removes every placed picture. Returns the number of shapes removed.</summary>
        public int RemoveImages() { int n = Shapes.RemoveAll(s => s.IsImage); if (n > 0) Touch(); return n; }
        /// <summary>Copies a placed picture's pixels into a new sprite (straight ARGB, the image's own resolution).</summary>
        public static Sprite ToSprite(VectorImagePaint image) { var s = new Sprite(image.Width, image.Height); image.Argb.AsSpan().CopyTo(s.Pixels); return s; }

        /// <summary>
        /// The colours in use (solid fills, strokes, gradient stops), most-used first; <paramref name="ignoreAlpha"/> merges
        /// entries that differ in alpha only (reported as opaque). For colour pickers / "which colour is that" UIs.
        /// </summary>
        public List<VectorPaletteEntry> Palette(bool ignoreAlpha = false, PaintTarget target = PaintTarget.Both, bool includeHidden = true)
        {
            var count = new Dictionary<int, int>(); var order = new List<int>();
            void Add(int c) { if (ignoreAlpha) c |= unchecked((int)0xFF000000); if (count.TryGetValue(c, out int n)) count[c] = n + 1; else { count[c] = 1; order.Add(c); } }
            void Paint(VectorPaint? p) { if (p is VectorColor sc) Add(sc.Argb); else if (p is VectorGradient g) foreach (var st in g.Stops) Add(st.Argb); }
            foreach (var s in Shapes) { if (!includeHidden && s.Hidden) continue; if (target != PaintTarget.Stroke) Paint(s.Fill); if (target != PaintTarget.Fill) Paint(s.Stroke); }
            var list = new List<VectorPaletteEntry>(order.Count);
            foreach (var c in order) list.Add(new VectorPaletteEntry(c, count[c]));
            list.Sort((x, y) => y.Uses.CompareTo(x.Uses));       // stable enough: List.Sort is not stable, ties keep no order - fine for a palette
            return list;
        }

        /// <summary>The colour under an image-space point: the top-most shape's fill (average of a gradient), or null when nothing is hit.</summary>
        public int? ColorAt(PointF imagePoint)
        {
            int i = HitTest(imagePoint); if (i < 0) return null;
            var s = Shapes[i];
            return s.Fill is VectorColor c ? c.Argb : s.Fill is VectorGradient g ? g.AverageArgb() : s.Fill is VectorImagePaint ip ? ip.AverageArgb() : s.Stroke is VectorColor sc ? sc.Argb : s.Stroke is VectorGradient sg ? sg.AverageArgb() : (int?)null;
        }

        // ---- helpers
        static int Apply(VectorPaint? p, Func<int, int> map)
        {
            if (p is VectorColor c)
            {
                int v = map(c.Argb); if (v == c.Argb) return 0; c.Argb = v; return 1;
            }
            if (p is VectorGradient g)
            {
                int n = 0;
                for (int i = 0; i < g.Stops.Count; i++) { var st = g.Stops[i]; int v = map(st.Argb); if (v != st.Argb) { g.Stops[i] = new VectorStop(st.Offset, v); n++; } }
                return n;
            }
            return 0;
        }
        static bool Near(int c, int target, int tol, bool ignoreAlpha)
        {
            if (tol <= 0) return ignoreAlpha ? (c & 0xFFFFFF) == (target & 0xFFFFFF) : c == target;
            int dr = Math.Abs(((c >> 16) & 255) - ((target >> 16) & 255)), dg = Math.Abs(((c >> 8) & 255) - ((target >> 8) & 255)), db = Math.Abs((c & 255) - (target & 255));
            if (dr > tol || dg > tol || db > tol) return false;
            return ignoreAlpha || Math.Abs(((c >> 24) & 255) - ((target >> 24) & 255)) <= tol;
        }
    }
}
