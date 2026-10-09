using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sr2d64CSport
{
    /// <summary>One elementary transform function of a keyframe: 0 = translate (a, b), 1 = scale (a, b), 2 = rotate (a = radians), 3 = skew (a, b = radians).
    /// Transform tracks are stored as lists of these (when they parse) so the interpolation lerps the PARAMETERS - lerping matrix
    /// elements cannot represent a 0 -> 360 deg spin (both ends are the same matrix).</summary>
    internal struct CssPrim
    {
        public byte Kind; public float A, B, C;
        public static CssPrim Translate(float x, float y) => new CssPrim { Kind = 0, A = x, B = y };
        public static CssPrim Scale(float x, float y) => new CssPrim { Kind = 1, A = x, B = y };
        public static CssPrim Rotate(float rad) => new CssPrim { Kind = 2, A = rad };
        public static CssPrim Skew(float x, float y) => new CssPrim { Kind = 3, A = x, B = y };
    }

    /// <summary>What one SMIL animation track drives: the d attribute, the transform attribute / a transform list, or motion along a path.</summary>
    internal enum SvgTrackKind { Path, Transform, Motion, Opacity, DashOffset }

    /// <summary>One CSS @keyframes step: a 0..1 offset, an optional per-step timing function and the declarations.</summary>
    internal sealed class CssKeyframe
    {
        public float Offset;
        public string? Ease;                                 // animation-timing-function of this step (drives the segment that STARTS here)
        public Dictionary<string, string> Decls = new Dictionary<string, string>();
    }

    /// <summary>One parsed CSS animation of one element: the @keyframes name plus the resolved timing (the shorthand
    /// "animation" with the animation-* longhands overriding it, inline style overriding the stylesheet).</summary>
    internal sealed class CssAnimation
    {
        public string Name = "";
        public double Dur = 0, Delay = 0;
        public string Ease = "linear";
        public double Repeat = 1; public bool Indefinite;
        public int Direction;                                // 0 = normal, 1 = reverse, 2 = alternate, 3 = alternate-reverse
        public bool FillForward, FillBackward;               // animation-fill-mode forwards / backwards / both

        static readonly (string k, string v)[] Eases = { ("linear", "0 0 1 1"), ("ease", "0.25 0.1 0.25 1"), ("ease-in", "0.42 0 1 1"), ("ease-out", "0 0 0.58 1"), ("ease-in-out", "0.42 0 0.58 1") };
        /// <summary>A keyword / cubic-bezier easing as keySpline numbers { x1, y1, x2, y2 }; null = unknown (kept linear).</summary>
        public static float[]? SplineOf(string? ease)
        {
            if (ease == null) return null;
            ease = ease.Trim();
            foreach (var (k, v) in Eases) if (ease == k) return Array.ConvertAll(v.Split(' '), f => float.Parse(f, CultureInfo.InvariantCulture));
            var m = Regex.Match(ease, @"cubic-bezier\(([^)]*)\)"); if (!m.Success) return null;
            var f2 = SvgReader.ParseNumbers(m.Groups[1].Value).ToArray(); return f2.Length >= 4 ? new[] { f2[0], f2[1], f2[2], f2[3] } : null;
        }

        /// <summary>The animations of one element from its merged animation declarations (shorthand first, then the longhands override).</summary>
        public static List<CssAnimation> Parse(Dictionary<string, string> props)
        {
            var list = new List<CssAnimation>();
            if (props.TryGetValue("animation", out var sh))
            {
                foreach (var part in SplitTop(sh, ','))
                {
                    var a = new CssAnimation();
                    a.ParseShorthand(part);
                    if (a.Name.Length > 0 && a.Name != "none") list.Add(a);
                }
            }
            else if (props.TryGetValue("animation-name", out var nm))
            {
                foreach (var name in SplitTop(nm, ','))
                {
                    var a = new CssAnimation { Name = name.Trim() };
                    if (a.Name.Length > 0 && a.Name != "none") { a.ApplyLonghands(props); list.Add(a); }
                }
            }
            if (props.TryGetValue("animation", out _))
                foreach (var a in list) a.ApplyLonghands(props);
            return list;
        }

        void ParseShorthand(string v)
        {
            bool haveDur = false;
            foreach (System.Text.RegularExpressions.Match t in Regex.Matches(v, @"[^\s(]+\([^)]*\)|\S+"))
            {
                string tok = t.Value.ToLowerInvariant();
                if (tok == "infinite") { Indefinite = true; continue; }
                if (tok == "normal") { Direction = 0; continue; }
                if (tok == "reverse") { Direction = 1; continue; }
                if (tok == "alternate") { Direction = 2; continue; }
                if (tok == "alternate-reverse") { Direction = 3; continue; }
                if (tok == "none" || tok == "forwards" || tok == "backwards" || tok == "both") { FillForward = tok == "forwards" || tok == "both"; FillBackward = tok == "backwards" || tok == "both"; continue; }
                if (Array.FindIndex(Eases, e => e.k == tok) >= 0 || tok.StartsWith("cubic-bezier(", StringComparison.Ordinal)) { Ease = tok; continue; }
                var clock = SvgReader.Clock(tok);
                if (clock.HasValue) { if (!haveDur) { Dur = clock.Value; haveDur = true; } else Delay = clock.Value; continue; }
                if (float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var it)) { Repeat = Math.Max(1, it); continue; }
                Name = t.Value;   // the bare identifier = the keyframes name (case-sensitive CSS: keep it as written)
            }
        }

        void ApplyLonghands(Dictionary<string, string> props)
        {
            if (props.TryGetValue("animation-name", out var nm)) { var n = SplitTop(nm, ',')[0].Trim(); if (n.Length > 0 && n != "none") Name = n; }
            if (props.TryGetValue("animation-duration", out var d) && SvgReader.Clock(d) is double cd) Dur = cd;
            if (props.TryGetValue("animation-delay", out var dl) && SvgReader.Clock(dl) is double cdl) Delay = cdl;
            if (props.TryGetValue("animation-timing-function", out var tf)) Ease = tf.Trim();
            if (props.TryGetValue("animation-iteration-count", out var ic))
            {
                var v = ic.Trim();
                if (v == "infinite") Indefinite = true;
                else if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) Repeat = Math.Max(1, n);
            }
            if (props.TryGetValue("animation-direction", out var dir)) Direction = dir.Trim() switch { "reverse" => 1, "alternate" => 2, "alternate-reverse" => 3, _ => 0 };
            if (props.TryGetValue("animation-fill-mode", out var fm)) { var v = fm.Trim(); FillForward = v == "forwards" || v == "both"; FillBackward = v == "backwards" || v == "both"; }
        }

        /// <summary>Splits on <paramref name="sep"/>, ignoring separators inside parentheses (cubic-bezier(0.4, 0, 0.6, 1)).</summary>
        static List<string> SplitTop(string v, char sep)
        {
            var parts = new List<string>(); int depth = 0; var cur = new System.Text.StringBuilder();
            foreach (var ch in v)
            {
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                if (ch == sep && depth == 0) { parts.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            parts.Add(cur.ToString());
            return parts;
        }
    }


    /// <summary>The parsed attributes of one SMIL &lt;animate&gt; / &lt;animateTransform&gt; / &lt;animateMotion&gt; / &lt;set&gt; element
    /// (the keyframe values stay as strings; each track parses them lazily on the first seek).</summary>
    internal sealed class SvgAnim
    {
        public SvgTrackKind Kind;
        public string? TransformType;                        // animateTransform type="translate | scale | rotate | skewX | skewY"; null = the values are transform lists
        public double Begin, Dur;                            // seconds (Dur 0 = &lt;set&gt;: hold from Begin)
        public double Repeat = 1;                            // repeatCount
        public bool Indefinite;                              // repeatCount="indefinite"
        public bool Freeze;                                  // fill="freeze" (else the base value comes back after the track ends)
        public int CalcMode;                                 // 0 = linear / paced, 1 = spline, 2 = discrete
        public int Direction;                                // CSS animation-direction: 0 normal, 1 reverse, 2 alternate, 3 alternate-reverse
        public bool FillBackward;                            // CSS animation-fill-mode backwards / both (hold the first keyframe during the delay)
        public string[] Values = Array.Empty<string>();      // keyframes (from/to becomes two entries; to-only steps from the base value)
        public float[] KeyTimes = Array.Empty<float>();
        public float[] KeyPoints = Array.Empty<float>();     // animateMotion keyPoints (fractions of the motion path length)
        public float[][] KeySplines = Array.Empty<float[]>();   // per segment { x1, y1, x2, y2 }
        public bool RotateAuto;                              // animateMotion rotate="auto"
        public float RotateAngle;                            // animateMotion rotate="&lt;deg&gt;" (when not auto)
        public bool Additive;                                // additive="sum" (animateTransform: added after the transform attribute)
    }

    /// <summary>A track resolved against what its target element produced: image shapes and/or &lt;mask&gt; content geometry.
    /// The Parsed* fields hold the lazily decoded keyframes and are shared between frames (read only).</summary>
    internal sealed class SvgTrack
    {
        public SvgAnim Anim = null!;
        public XElement El = null!;                          // the <animate*> element itself (motion path, ...)
        public XElement Target = null!;                      // the animated element (the parent of the &lt;animate*&gt;)
        public readonly List<VectorShape> Shapes = new List<VectorShape>();
        public readonly List<SvgMaskItem> MaskItems = new List<SvgMaskItem>();
        public List<VectorPath?>? Parsed;                    // Kind = Path
        public bool Compat = true;                           // the keyframe paths share their command structure (can morph)
        public List<Matrix3x2>? ParsedM;                     // Kind = Transform (prebuilt for CSS tracks)
        public List<List<CssPrim>?>? ParsedCss;              // Kind = Transform: the same keyframes as transform primitives (null entry = a raw matrix() - no parameter lerp)
        public List<float>? ParsedO;                         // Kind = Opacity (prebuilt for CSS tracks)
        public List<PointF>? MotionPts; public List<float>? MotionLen; public float MotionTotal;   // Kind = Motion (longest sub-path)
        public readonly List<float> BaseO = new List<float>();   // Kind = Opacity: the opacity each shape had at import (seek restores it)
        public readonly List<float> InhO = new List<float>();    // Kind = Opacity: the factor inherited from ancestor groups (the track value replaces the element's own)
        public readonly List<float> BaseD = new List<float>();   // Kind = DashOffset: the dash offset each shape had at import
        public float BaseOwn = 1f;                           // Kind = Opacity: the animated element's OWN static opacity (the track value replaces it, inherited factors stay)
        public char OriginBox;                               // CSS transform-box: 'f' = fill-box (the element's bounds), 'v' = the view box
        public string? Origin;                               // CSS transform-origin ("center", "50% 0", "10px 20px")
        public Vector2? OriginPt;                            // the resolved origin in the element's user space (Finish)
        public Matrix3x2? BaseM;                             // the element's static transform attribute (the implicit 0% / 100% CSS base)
    }

    /// <summary>One geometry source inside a &lt;mask&gt;: the clip paths of masked shapes are rebuilt from these whenever the animation seeks.</summary>
    internal sealed class SvgMaskItem
    {
        public XElement El = null!;                          // the content element (an animate may target it)
        public XElement TopEl = null!;                       // the &lt;mask&gt; element: the transform chain stops here (mask content is in the user space of the element referencing the mask)
        public VectorPath BasePath = new VectorPath();
        public bool EvenOdd;
        public VectorPath? LivePath;                         // d animation output at the current time (null = the base path)
        public Matrix3x2 LiveM;                              // the transform chain (with animated values) at the current time
    }

    /// <summary>A shape whose clip comes from a &lt;mask&gt;: re-derived from the mask geometry whenever the animation seeks.</summary>
    internal sealed class SvgMaskBind
    {
        public VectorShape Shape = null!;
        public XElement RefEl = null!;                       // the element the mask is on (the mask is in its user space)
        public List<SvgMaskItem> Items = new List<SvgMaskItem>();
        public VectorClip? Static;                           // a clip-path the same element also had (kept underneath the mask)
    }

    internal static partial class SvgReader
    {
        // ------------------------------------------------------------------ SMIL animation collection ----------------------------------
        /// <summary>Collects an &lt;animate&gt; / &lt;animateTransform&gt; / &lt;animateMotion&gt; / &lt;set&gt; element: one pending track on its parent.</summary>
        static void AnimElement(XElement e, Ctx c)
        {
            var target = e.Parent;
            if (target == null) return;
            string n = e.Name.LocalName;
            string attr = ((string?)e.Attribute("attributeName") ?? "").Trim();
            string? type = ((string?)e.Attribute("type"))?.Trim();
            SvgTrackKind? kind = n == "animateMotion" ? SvgTrackKind.Motion
                     : (n == "animateTransform" || (n == "set" && attr == "transform")) ? SvgTrackKind.Transform
                     : attr == "d" ? SvgTrackKind.Path : null;
            if (kind == null)
            {
                if (n != "set") c.Warn($"<animate> of '{attr}' is not supported (only d / transform / motion)");
                return;
            }
            double begin = 0;
            foreach (var part in ((string?)e.Attribute("begin") ?? "").Split(';'))
            {
                var b2 = Clock(part); if (b2.HasValue) { begin = b2.Value; break; }
            }
            double? dur = Clock((string?)e.Attribute("dur"));
            var anim = new SvgAnim { Kind = kind.Value, TransformType = type, Begin = begin, Dur = dur ?? 0 };
            string? rep = ((string?)e.Attribute("repeatCount"))?.Trim();
            if (rep == "indefinite") anim.Indefinite = true;
            else if (rep != null && float.TryParse(rep, NumberStyles.Float, ci, out var rn)) anim.Repeat = Math.Max(1, rn);
            anim.Freeze = ((string?)e.Attribute("fill"))?.Trim() == "freeze";
            anim.Additive = ((string?)e.Attribute("additive"))?.Trim() == "sum";
            string? mode = ((string?)e.Attribute("calcMode"))?.Trim();
            anim.CalcMode = mode == "discrete" ? 2 : mode == "spline" ? 1 : 0;
            if (kind == SvgTrackKind.Motion && mode == null) anim.CalcMode = 0;   // (paced is the SMIL default for motion - linear is what it means over one path)

            string? vals = (string?)e.Attribute("values");
            if (vals != null) anim.Values = vals.Split(';').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
            else if (kind != SvgTrackKind.Motion)
            {
                string? from = ((string?)e.Attribute("from"))?.Trim(), to = ((string?)e.Attribute("to"))?.Trim();
                if (from != null && to != null) anim.Values = new[] { from, to };
                else if (to != null) anim.Values = new[] { to };   // to-animation: step from the base value to 'to'
                else return;
            }
            anim.KeyTimes = KeyFrameList((string?)e.Attribute("keyTimes"));
            var splines = ((string?)e.Attribute("keySplines"))?.Split(';') ?? Array.Empty<string>();
            var ks = new List<float[]>();
            foreach (var s in splines)
            {
                var f = ParseNumbers(s).ToArray(); if (f.Length < 4) continue;
                ks.Add(new[] { f[0], f[1], f[2], f[3] });
            }
            anim.KeySplines = ks.ToArray();
            anim.KeyPoints = KeyFrameList((string?)e.Attribute("keyPoints"));
            string? rot = ((string?)e.Attribute("rotate"))?.Trim();
            if (rot == "auto") anim.RotateAuto = true;
            else if (rot != null && float.TryParse(rot, NumberStyles.Float, ci, out var ra)) anim.RotateAngle = ra;
            if (kind == SvgTrackKind.Motion && e.Elements().Any(ch => ch.Name.LocalName == "mpath")) c.Warn("animateMotion mpath is not followed (use the path attribute)");
            c.Pending.Add(new SvgTrack { Anim = anim, El = e, Target = target });
        }

        /// <summary>Splits a semicolon separated keyframe list ("0; 0.25; 1").</summary>
        static float[] KeyFrameList(string? v)
        {
            if (v == null) return Array.Empty<float>();
            return ParseNumbers(v).ToArray();
        }

        /// <summary>Parses a SMIL clock value ("2.5s", "150ms", "1min", "0:04" / "0:04.033"); null = not a clock (incl. "indefinite").</summary>
        internal static double? Clock(string? v)
        {
            if (v == null) return null;
            v = v.Trim(); if (v.Length == 0 || v == "indefinite") return null;
            if (v.Contains(':'))
            {
                double s = 0;
                foreach (var p in v.Split(':')) { if (!float.TryParse(p, NumberStyles.Float, ci, out var f)) return null; s = s * 60 + f; }
                return s;
            }
            int i = v.Length; while (i > 0 && (char.IsLetter(v[i - 1]))) i--;
            if (!float.TryParse(v.Substring(0, i).Replace(',', '.'), NumberStyles.Float, ci, out var n)) return null;
            return v.Substring(i).ToLowerInvariant() switch { "ms" => n / 1000.0, "min" => n * 60.0, "h" => n * 3600.0, _ => n };
        }

        // ------------------------------------------------------------------ masks ----------------------------------
        /// <summary>Builds the mask of an element (mask="url(#id)" attribute or style) as a clip over its shapes. mask-type="alpha"
        /// masks - and luminance masks whose filter forces white and keeps the alpha, which is what design tools write - are exact;
        /// real luminance masks are approximated by their alpha. The content geometry is kept as <see cref="SvgMaskItem"/>s so the
        /// animation can move it (the clip of every masked shape is re-derived on each seek).</summary>
        static (VectorClip? clip, List<SvgMaskItem>? items) MaskOf(XElement e, Ctx c, Style st)
        {
            var v = (string?)e.Attribute("mask") ?? StyleProp(e, "mask");
            if (v == null || v.Length == 0 || v == "none" || !v.StartsWith("url(", StringComparison.Ordinal)) return (null, null);
            int a = v.IndexOf('#'), b = v.IndexOf(')'); if (a < 0 || b <= a) return (null, null);
            string id = v.Substring(a + 1, b - a - 1).Trim();
            if (c.Masks.TryGetValue(id, out var cached)) return cached;
            c.Masks[id] = (null, null);
            if (!c.ById.TryGetValue(id, out var me) || me.Name.LocalName != "mask") return (null, null);
            var units = (string?)me.Attribute("maskUnits") ?? "objectBoundingBox";
            if (units != "userSpaceOnUse") c.Warn("maskUnits=" + units + " is approximated as userSpaceOnUse");
            var cunits = (string?)me.Attribute("maskContentUnits") ?? "userSpaceOnUse";
            if (cunits != "userSpaceOnUse") c.Warn("maskContentUnits=" + cunits + " is approximated as userSpaceOnUse");
            if (!AlphaMask(me, c)) c.Warn("luminance mask is approximated as an alpha mask");
            var items = new List<SvgMaskItem>();
            WalkMask(me, me, st, Matrix3x2.Identity, c, items);
            var clip = new VectorClip();
            foreach (var it in items)
            {
                var chain = StaticChain(it.El, it.TopEl, new Dictionary<XElement, Matrix3x2>());
                clip.Paths.Add((it.BasePath.Transformed(chain), it.EvenOdd));
            }
            if (clip.Paths.Count == 0) clip.Paths.Add((new VectorPath().Rect(0, 0, 0, 0), false));   // an empty mask hides the element (same as an empty clipPath)
            var result = (clip, items);
            c.Masks[id] = result;
            return result;
        }

        /// <summary>True when the mask is explicitly alpha (mask-type="alpha") or its filters reduce it to alpha
        /// (a feColorMatrix that forces white and passes the alpha through - the "0 0 0 0 1 / 0 0 0 0 1 / 0 0 0 0 1 / 0 0 0 1 0" matrix).</summary>
        static bool AlphaMask(XElement me, Ctx c)
        {
            var mt = (string?)me.Attribute("mask-type") ?? StyleProp(me, "mask-type");
            if (mt == "alpha") return true;
            foreach (var g in me.Descendants())
            {
                var fref = (string?)g.Attribute("filter") ?? StyleProp(g, "filter");
                if (fref == null || !fref.StartsWith("url(", StringComparison.Ordinal)) continue;
                int a = fref.IndexOf('#'), b = fref.IndexOf(')'); if (a < 0 || b <= a) continue;
                if (!c.ById.TryGetValue(fref.Substring(a + 1, b - a - 1).Trim(), out var fe) || fe.Name.LocalName != "filter") continue;
                bool white = false;
                foreach (var p in fe.Elements())
                {
                    if (p.Name.LocalName != "feColorMatrix" || ((string?)p.Attribute("type")) is string ty && ty != "matrix") { white = false; break; }
                    var vals = ParseNumbers((string?)p.Attribute("values") ?? "");
                    if (vals.Count < 20 || vals[4] != 1 || vals[9] != 1 || vals[14] != 1 || vals[19] != 0) { white = false; break; }
                    white = true;
                }
                if (white) return true;
            }
            return false;
        }

        /// <summary>Walks the content of a &lt;mask&gt;: geometry becomes <see cref="SvgMaskItem"/>s, animate children become pending tracks.</summary>
        static void WalkMask(XElement me, XElement parent, Style inherited, Matrix3x2 m, Ctx c, List<SvgMaskItem> items)
        {
            foreach (var e in parent.Elements())
            {
                string n = e.Name.LocalName;
                switch (n)
                {
                    case "animate": case "animateTransform": case "animateMotion": case "set": AnimElement(e, c); continue;
                    case "text": c.Warn("text inside mask is ignored"); continue;
                    case "animateSet": continue;
                }
                var cm = ParseTransform((string?)e.Attribute("transform")) * m;
                if (n == "g" || n == "a" || n == "switch") { var gst = ApplyStyle(e, inherited, c); if (!gst.Hidden) WalkMask(me, e, gst, cm, c, items); continue; }
                XElement el = e;
                if (n == "use")
                {
                    var href = Href(e);
                    if (href == null || !href.StartsWith('#') || !c.ById.TryGetValue(href.Substring(1), out var t)) continue;
                    el = t;
                    cm = ParseTransform((string?)t.Attribute("transform")) * Matrix3x2.CreateTranslation(Len(e, "x", c) ?? 0, Len(e, "y", c) ?? 0) * cm;
                }
                var st = ApplyStyle(el, inherited, c); if (st.Hidden) continue;
                var p = ShapePath(el, c, el.Name.LocalName); if (p == null) continue;
                var item = new SvgMaskItem { El = el, TopEl = me, BasePath = p, EvenOdd = st.ClipEvenOdd };
                items.Add(item); c.MaskItems.Add(item);
                if (!c.MaskItemByEl.ContainsKey(el)) c.MaskItemByEl[el] = item;
                foreach (var ch in el.Elements())   // the shape's own animation (children of leaves are not walked otherwise)
                {
                    var cn = ch.Name.LocalName;
                    if (cn == "animate" || cn == "animateTransform" || cn == "animateMotion" || cn == "set") AnimElement(ch, c);
                }
            }
        }

        /// <summary>Product of the transform attributes from <paramref name="el"/> up to (excluding) <paramref name="top"/> - the static version of VectorImage.Chain.</summary>
        internal static Matrix3x2 StaticChain(XElement el, XElement? top, Dictionary<XElement, Matrix3x2> cache)
        {
            var m = Matrix3x2.Identity;
            var cur = el;
            while (cur != null && !ReferenceEquals(cur, top) && cur.Name.LocalName != "svg")
            {
                if (!cache.TryGetValue(cur, out var own)) { own = ParseTransform((string?)cur.Attribute("transform")); cache[cur] = own; }
                m = m * own;   // row-vector chain, like Chain: the element's own static matrix applies to the point first
                cur = cur.Parent;
            }
            return m;
        }

        // ------------------------------------------------------------------ CSS animations ----------------------------------
        /// <summary>Turns CSS animations (animation / animation-* declarations plus @keyframes) into the same pending tracks the
        /// SMIL reader produces. transform and opacity keyframes are supported (what design tools and AI-generated game art
        /// animate); anything else is reported once. Timing: the shorthand + longhands, inline style overriding the stylesheet,
        /// keyword and cubic-bezier easings, iteration counts, delays, normal / reverse / alternate (-reverse) directions and
        /// fill modes; transform-box / transform-origin place rotate / scale (fill-box = the element's bounds, the default these files use).</summary>
        static void CollectCssAnimations(XElement root, Ctx c)
        {
            var unsupported = new HashSet<string>();
            foreach (var e in root.DescendantsAndSelf())
            {
                var props = new Dictionary<string, string>();
                string tag = e.Name.LocalName; string? id = (string?)e.Attribute("id");
                var classes = ((string?)e.Attribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var a in e.Attributes())   // presentation attributes (lowest priority, like ApplyStyle)
                    if (IsAnimProp(a.Name.LocalName)) props[a.Name.LocalName] = a.Value;
                foreach (var pass in new[] { 0, 1, 2 })
                    foreach (var (sel, decl) in c.Css)
                    {
                        bool hit = pass == 0 ? sel == tag || sel == "*" : pass == 1 ? sel.StartsWith('.') && Array.IndexOf(classes, sel.Substring(1)) >= 0 || (sel.Contains('.') && !sel.StartsWith('.') && sel.Split('.')[0] == tag && Array.IndexOf(classes, sel.Split('.')[1]) >= 0)
                                 : sel.StartsWith('#') && sel.Substring(1) == id;
                        if (hit) foreach (var kv in decl) if (IsAnimProp(kv.Key)) props[kv.Key] = kv.Value;
                    }
                var style = (string?)e.Attribute("style");
                if (style != null) foreach (var kv in ParseDecl(style)) if (IsAnimProp(kv.Key)) props[kv.Key] = kv.Value;   // inline style wins
                if (!props.ContainsKey("animation") && !props.ContainsKey("animation-name")) continue;
                foreach (var a in CssAnimation.Parse(props)) CssTracks(e, a, props, c, unsupported);
            }
            if (unsupported.Count > 0) c.Warn("css @keyframes animate " + string.Join(", ", unsupported.OrderBy(k => k)) + " (only transform / opacity are supported)");
        }
        static bool IsAnimProp(string k) => k == "animation" || k.StartsWith("animation-", StringComparison.Ordinal) || k == "transform-box" || k == "transform-origin" || k == "stroke-dashoffset" || k == "opacity";

        /// <summary>Builds the transform / opacity tracks of one CSS animation; nothing (null) when the keyframes are missing or carry nothing supported.</summary>
        static void CssTracks(XElement e, CssAnimation a, Dictionary<string, string> props, Ctx c, HashSet<string> unsupported)
        {
            if (!c.Keyframes.TryGetValue(a.Name, out var steps)) { c.Warn($"css animation '{a.Name}' has no @keyframes"); return; }
            foreach (var st in steps)
                foreach (var k in st.Decls.Keys)
                    if (k != "animation-timing-function" && k != "transform" && k != "opacity" && k != "stroke-dashoffset") unsupported.Add(k);
            var anim = new SvgAnim
            {
                Kind = SvgTrackKind.Transform,
                Begin = a.Delay, Dur = a.Dur,
                Repeat = a.Indefinite ? 1 : Math.Max(1, a.Repeat), Indefinite = a.Indefinite,
                Freeze = a.FillForward, FillBackward = a.FillBackward, Direction = a.Direction,
                CalcMode = 1,
            };
            // transform: per keyframe a matrix; the implicit 0% / 100% base is the element's transform attribute
            var tsteps = steps.Where(s => s.Decls.ContainsKey("transform")).ToList();
            if (tsteps.Count > 0)
            {
                Matrix3x2 Base() => ParseTransform((string?)e.Attribute("transform"));
                if (tsteps[0].Offset > 0) tsteps.Insert(0, new CssKeyframe { Offset = 0, Ease = tsteps[0].Ease, Decls = new Dictionary<string, string> { ["transform"] = "" } });
                if (tsteps[^1].Offset < 1) tsteps.Add(new CssKeyframe { Offset = 1, Ease = null, Decls = new Dictionary<string, string> { ["transform"] = "" } });
                var track = new SvgTrack
                {
                    Anim = anim, Target = e, El = e,
                    ParsedM = tsteps.Select(s => string.IsNullOrWhiteSpace(s.Decls["transform"]) ? Base() : CssTransformMatrix(s.Decls["transform"])).ToList(),
                    ParsedCss = tsteps.Select(s => string.IsNullOrWhiteSpace(s.Decls["transform"]) ? null : VectorImage.TransformPrims(s.Decls["transform"])).ToList(),
                    BaseM = Base(),
                    Origin = (props.TryGetValue("transform-origin", out var o) ? o : null),
                    OriginBox = props.TryGetValue("transform-box", out var b) ? b.Trim() switch { "fill-box" or "stroke-box" or "content-box" => 'f', "view-box" or "border-box" => 'v', _ => (char)0 } : (char)0,
                };
                track.Anim.KeyTimes = tsteps.Select(s => s.Offset).ToArray();
                track.Anim.KeySplines = tsteps.Take(tsteps.Count - 1).Select(s => CssAnimation.SplineOf(s.Ease ?? a.Ease) ?? Array.Empty<float>()).Where(s => s.Length > 0).ToArray();
                if (track.Anim.KeySplines.Length < tsteps.Count - 1) track.Anim.CalcMode = 0;   // mixed known/unknown easings: fall back to linear timing
                var pc = track.ParsedCss;
                if (pc != null)
                    for (int i = 0; i < pc.Count; i++)
                        if (pc[i] == null)
                        {   // a keyframe without transform = the element's base value: against the other stop's function list CSS
                            // interpolates the matching identity list (that is what makes `to { transform:rotate(360deg) }` sweep)
                            var other = i == 0 ? pc.Skip(1).FirstOrDefault(x => x != null) : pc.Take(i).LastOrDefault(x => x != null);
                            if (other != null) pc[i] = other.Select(p => p.Kind switch { 0 => CssPrim.Translate(0, 0), 1 => CssPrim.Scale(1, 1), 2 => CssPrim.Rotate(0), _ => CssPrim.Skew(0, 0) }).ToList();
                        }
                c.Pending.Add(track);
            }
            // opacity: per keyframe a float; the implicit 0% / 100% base is the element's opacity property (default 1)
            var osteps = steps.Where(s => s.Decls.ContainsKey("opacity")).ToList();
            if (osteps.Count > 0)
            {
                float Base() => Num01(props.TryGetValue("opacity", out var o) ? o : "1");
                if (osteps[0].Offset > 0) osteps.Insert(0, new CssKeyframe { Offset = 0, Ease = osteps[0].Ease, Decls = new Dictionary<string, string> { ["opacity"] = "" } });
                if (osteps[^1].Offset < 1) osteps.Add(new CssKeyframe { Offset = 1, Ease = null, Decls = new Dictionary<string, string> { ["opacity"] = "" } });
                var animO = new SvgAnim { Kind = SvgTrackKind.Opacity, Begin = anim.Begin, Dur = anim.Dur, Repeat = anim.Repeat, Indefinite = anim.Indefinite, Freeze = anim.Freeze, FillBackward = anim.FillBackward, Direction = anim.Direction, CalcMode = 1 };
                var track = new SvgTrack
                {
                    Anim = animO, Target = e, El = e,
                    ParsedO = osteps.Select(s => string.IsNullOrWhiteSpace(s.Decls["opacity"]) ? Base() : Num01(s.Decls["opacity"])).ToList(),
                    BaseOwn = Base(),
                };
                track.Anim.KeyTimes = osteps.Select(s => s.Offset).ToArray();
                track.Anim.KeySplines = osteps.Take(osteps.Count - 1).Select(s => CssAnimation.SplineOf(s.Ease ?? a.Ease) ?? Array.Empty<float>()).Where(s => s.Length > 0).ToArray();
                if (track.Anim.KeySplines.Length < osteps.Count - 1) track.Anim.CalcMode = 0;
                c.Pending.Add(track);
            }
            // stroke-dashoffset: the marching-dashes flow (sand / liquid streams); px values, interpolated linearly
            var dsteps = steps.Where(s => s.Decls.ContainsKey("stroke-dashoffset")).ToList();
            if (dsteps.Count > 0)
            {
                float Base() => ElementDashOffset(e, props);
                if (dsteps[0].Offset > 0) dsteps.Insert(0, new CssKeyframe { Offset = 0, Ease = dsteps[0].Ease, Decls = new Dictionary<string, string> { ["stroke-dashoffset"] = "" } });
                if (dsteps[^1].Offset < 1) dsteps.Add(new CssKeyframe { Offset = 1, Ease = null, Decls = new Dictionary<string, string> { ["stroke-dashoffset"] = "" } });
                var track = new SvgTrack
                {
                    Anim = new SvgAnim { Kind = SvgTrackKind.DashOffset, Begin = anim.Begin, Dur = anim.Dur, Repeat = anim.Repeat, Indefinite = anim.Indefinite, Freeze = anim.Freeze, FillBackward = anim.FillBackward, Direction = anim.Direction, CalcMode = 1 },
                    Target = e, El = e,
                    ParsedO = dsteps.Select(s => string.IsNullOrWhiteSpace(s.Decls["stroke-dashoffset"]) ? Base() : DashValue(s.Decls["stroke-dashoffset"], Base())).ToList(),
                };
                track.Anim.KeyTimes = dsteps.Select(s => s.Offset).ToArray();
                track.Anim.KeySplines = dsteps.Take(dsteps.Count - 1).Select(s => CssAnimation.SplineOf(s.Ease ?? a.Ease) ?? Array.Empty<float>()).Where(s => s.Length > 0).ToArray();
                if (track.Anim.KeySplines.Length < dsteps.Count - 1) track.Anim.CalcMode = 0;
                c.Pending.Add(track);
            }
        }

        /// <summary>The element's static stroke-dashoffset (attribute or merged style), the implicit base of a dash track.</summary>
        static float ElementDashOffset(XElement e, Dictionary<string, string> props)
        {
            if (props.TryGetValue("stroke-dashoffset", out var p) && ParseFloat(p) is float f) return f;
            var v = (string?)e.Attribute("stroke-dashoffset");
            return v != null && ParseFloat(v) is float f2 ? f2 : 0f;
        }
        static float DashValue(string v, float fallback) { var t = v.Trim(); return ParseFloat(t.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? t.Substring(0, t.Length - 2) : t) ?? fallback; }

        /// <summary>A CSS transform list ("translateY(-92px) scale(1.1) rotate(15deg)") as a matrix (px = user units, deg = degrees).</summary>
        internal static Matrix3x2 CssTransformMatrix(string v)
        {
            var m = Matrix3x2.Identity;
            foreach (Match mt in Regex.Matches(v, @"(matrix|translate|translateX|translateY|scale|scaleX|scaleY|rotate|skew|skewX|skewY)\s*\(([^)]*)\)"))
            {
                var a = ParseNumbers(mt.Groups[2].Value);
                Matrix3x2 k = Matrix3x2.Identity;
                switch (mt.Groups[1].Value)
                {
                    case "matrix": if (a.Count >= 6) k = new Matrix3x2(a[0], a[1], a[2], a[3], a[4], a[5]); break;
                    case "translate": k = Matrix3x2.CreateTranslation(a.Count > 0 ? a[0] : 0, a.Count > 1 ? a[1] : 0); break;
                    case "translateX": k = Matrix3x2.CreateTranslation(a.Count > 0 ? a[0] : 0, 0); break;
                    case "translateY": k = Matrix3x2.CreateTranslation(0, a.Count > 0 ? a[0] : 0); break;
                    case "scale": k = Matrix3x2.CreateScale(a.Count > 0 ? a[0] : 1, a.Count > 1 ? a[1] : (a.Count > 0 ? a[0] : 1)); break;
                    case "scaleX": k = Matrix3x2.CreateScale(a.Count > 0 ? a[0] : 1, 1); break;
                    case "scaleY": k = Matrix3x2.CreateScale(1, a.Count > 0 ? a[0] : 1); break;
                    case "rotate": k = Matrix3x2.CreateRotation((a.Count > 0 ? a[0] : 0) * MathF.PI / 180f); break;
                    case "skew": k = Matrix3x2.CreateSkew((a.Count > 0 ? a[0] : 0) * MathF.PI / 180f, (a.Count > 1 ? a[1] : 0) * MathF.PI / 180f); break;
                    case "skewX": k = Matrix3x2.CreateSkew((a.Count > 0 ? a[0] : 0) * MathF.PI / 180f, 0); break;
                    case "skewY": k = Matrix3x2.CreateSkew(0, (a.Count > 0 ? a[0] : 0) * MathF.PI / 180f); break;
                }
                m = k * m;   // later functions apply first (the same right-to-left order ParseTransform uses)
            }
            return m;
        }

        /// <summary>Resolves the transform-origin of a CSS track: the point in the element's user space rotate / scale turn around.</summary>
        internal static Vector2? ResolveOrigin(SvgTrack tr, Ctx c)
        {
            if (string.IsNullOrWhiteSpace(tr.Origin) && tr.OriginBox == 0) return null;
            RectangleF box;
            if (tr.OriginBox == 'v') box = c.Img.ViewBox;
            else
            {   // fill-box: the element's fill geometry bounds in its own user space (a group: the union of its content's bounds)
                var cache = new Dictionary<XElement, Matrix3x2>();
                bool any = false; float l = 0, t = 0, r = 0, b = 0;
                foreach (var s in tr.Shapes)
                {
                    if (s.Tag is not XElement el) continue;
                    var bb = s.Path.ControlBounds();
                    if (bb.Width <= 0 && bb.Height <= 0) continue;
                    bb = RectTo(bb, StaticChain(el, tr.Target, cache));
                    if (!any) { l = bb.X; t = bb.Y; r = bb.Right; b = bb.Bottom; any = true; }
                    else { l = Math.Min(l, bb.X); t = Math.Min(t, bb.Y); r = Math.Max(r, bb.Right); b = Math.Max(b, bb.Bottom); }
                }
                if (!any) return null;
                box = RectangleF.FromLTRB(l, t, r, b);
            }
            var parts = (tr.Origin ?? "center").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            float fx = 0.5f, fy = 0.5f; bool hx = false, vy = false;
            foreach (var p in parts)
            {
                var v = p.ToLowerInvariant();
                if (v == "left") { if (!hx) { fx = 0; hx = true; } }
                else if (v == "right") { if (!hx) { fx = 1; hx = true; } }
                else if (v == "top") { if (!vy) { fy = 0; vy = true; } }
                else if (v == "bottom") { if (!vy) { fy = 1; vy = true; } }
                else if (v == "center") { if (!hx) { fx = 0.5f; hx = true; } else if (!vy) { fy = 0.5f; vy = true; } }
                else if (v.EndsWith('%') && ParseFloat(v.TrimEnd('%')) is float pct) { if (!hx) { fx = Math.Clamp(pct / 100, 0, 1); hx = true; } else if (!vy) { fy = Math.Clamp(pct / 100, 0, 1); vy = true; } }
                else if (v.EndsWith("px", StringComparison.Ordinal) && ParseFloat(v.Substring(0, v.Length - 2)) is float px) { if (!hx) { fx = Math.Clamp(px / Math.Max(1e-3f, box.Width), 0, 1); hx = true; } else if (!vy) { fy = Math.Clamp(px / Math.Max(1e-3f, box.Height), 0, 1); vy = true; } }
            }
            return new Vector2(box.X + Math.Clamp(fx, 0f, 1f) * box.Width, box.Y + Math.Clamp(fy, 0f, 1f) * box.Height);
        }
        static RectangleF RectTo(RectangleF r, Matrix3x2 m)
        {
            var a = Vector2.Transform(new Vector2(r.Left, r.Top), m);
            var b = Vector2.Transform(new Vector2(r.Right, r.Top), m);
            var c = Vector2.Transform(new Vector2(r.Right, r.Bottom), m);
            var d = Vector2.Transform(new Vector2(r.Left, r.Bottom), m);
            float l = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), rr = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            float t = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)), bb = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
            return RectangleF.FromLTRB(l, t, rr, bb);
        }

        /// <summary>Resolves the pending tracks against the shapes / mask entries the walk produced, stores them on the image and computes the duration.</summary>
        static void Finish(VectorImage img, Ctx c)
        {
            var byEl = new Dictionary<XElement, List<VectorShape>>();
            foreach (var s in img.Shapes) if (s.Tag is XElement xe && !s.IsText) { if (!byEl.TryGetValue(xe, out var l)) byEl[xe] = l = new List<VectorShape>(); l.Add(s); }
            foreach (var t in c.Pending)
            {
                if (c.MaskItemByEl.TryGetValue(t.Target, out var item)) t.MaskItems.Add(item);
                else
                {
                    if (byEl.TryGetValue(t.Target, out var exact)) t.Shapes.AddRange(exact);
                    var sub = Subtree(t.Target);
                    if (t.Shapes.Count == 0 && t.MaskItems.Count == 0)
                        foreach (var it in c.MaskItems) if (sub.Contains(it.El)) t.MaskItems.Add(it);   // a group inside mask content
                    if (t.Shapes.Count == 0)
                        foreach (var s in img.Shapes) if (s.Tag is XElement xe && sub.Contains(xe)) t.Shapes.Add(s);   // a group in the picture
                }
                if (t.Shapes.Count == 0 && t.MaskItems.Count == 0) c.Warn($"<animate> on <{t.Target.Name.LocalName}> has nothing to drive");
                else img.Tracks.Add(t);
            }
            img.MaskItems.AddRange(c.MaskItems);
            img.MaskBinds.AddRange(c.MaskBinds);
            foreach (var r in c.UseRoots) img.UseRoots.Add(r);
            foreach (var kv in c.UseSite) img.UseSites[kv.Key] = kv.Value;
            double dur = 0;
            foreach (var t in img.Tracks)
            {
                var a = t.Anim;
                dur = Math.Max(dur, a.Begin + (a.Indefinite ? a.Dur : Math.Max(0, a.Repeat) * a.Dur));
            }
            img.Duration = Math.Round(dur * 1000) / 1000.0;
            // CSS tracks: capture what the seek multiplies / turns around (per shape base opacity; the transform origin)
            foreach (var t in img.Tracks)
            {
                if (t.Anim.Kind == SvgTrackKind.Opacity) foreach (var s in t.Shapes) { t.BaseO.Add(s.Opacity); t.InhO.Add(t.BaseOwn > 1e-4f ? s.Opacity / t.BaseOwn : 1f); }   // own opacity 0 (the hide-until-animated idiom): the track shows the inherited factor
                if (t.Anim.Kind == SvgTrackKind.DashOffset) foreach (var s in t.Shapes) t.BaseD.Add(s.DashOffset);
                if (t.Origin != null || t.OriginBox != 0) t.OriginPt = ResolveOrigin(t, c);
            }
            foreach (var s in img.Shapes) s.BaseTransform = s.Transform;   // the import-time static chain: seek rebuilds Transform as animated chain over it
        }

        static HashSet<XElement> Subtree(XElement root)
        {
            var set = new HashSet<XElement>();
            foreach (var d in root.DescendantsAndSelf()) set.Add(d);
            return set;
        }
    }

    internal sealed partial class VectorImage
    {
        /// <summary>Length of the animation loop in seconds (0 = a static picture). The maximum of the track windows (indefinite tracks count once).</summary>
        public double Duration;
        /// <summary>Frames per second for <see cref="FrameCount"/> / <see cref="GetFrame(int)"/>; the animation itself can be sampled at any time.</summary>
        public double FrameRate = 30;
        /// <summary>Frames of one animation loop at <see cref="FrameRate"/> (1 for a static picture).</summary>
        public int FrameCount => Duration <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Duration * FrameRate - 1e-6));
        public readonly List<SvgTrack> Tracks = new List<SvgTrack>();
        public readonly List<SvgMaskItem> MaskItems = new List<SvgMaskItem>();
        public readonly List<SvgMaskBind> MaskBinds = new List<SvgMaskBind>();
        /// <summary>Elements imported through &lt;use&gt;: their ancestors in the file do not apply (the use site's matrix does).</summary>
        public readonly HashSet<XElement> UseRoots = new HashSet<XElement>();
        /// <summary>For shapes that came from a &lt;use&gt;: the use element that placed them. SeekToTime applies the animated
        /// transforms of the use site's ancestors (a CSS-animated group wrapping a &lt;use&gt;) on top of the fragment chain.</summary>
        public readonly Dictionary<VectorShape, XElement> UseSites = new Dictionary<VectorShape, XElement>();
        Dictionary<XElement, Matrix3x2> ownM = new Dictionary<XElement, Matrix3x2>();      // per element: its static transform attribute
        Dictionary<XElement, List<SvgTrack>>? tracksByEl;

        /// <summary>An independent copy of the picture at time <paramref name="t"/> of the animation (seconds; the original keeps its base state).</summary>
        public VectorImage FrameAt(double t) { var f = CloneAnimated(); f.SeekToTime(t); return f; }
        /// <summary>Frame <paramref name="i"/> of <see cref="FrameCount"/> as an independent copy (the index wraps).</summary>
        public VectorImage GetFrame(int i)
        {
            int n = FrameCount, k = n <= 1 ? 0 : ((i % n) + n) % n;
            return FrameAt(k / FrameRate);
        }

        /// <summary>Applies every animation track at time <paramref name="t"/> (seconds) in place: paths, transforms and mask clips.
        /// Repeatable - the base paths / transforms of shapes are only changed relative to the previous seek, and every value is a pure function of t.</summary>
        public void SeekToTime(double t)
        {
            _seekT = t;
            if (Tracks.Count == 0 && MaskItems.Count == 0) return;
            tracksByEl = new Dictionary<XElement, List<SvgTrack>>();
            foreach (var tr in Tracks) { if (!tracksByEl.TryGetValue(tr.Target, out var l)) tracksByEl[tr.Target] = l = new List<SvgTrack>(); l.Add(tr); }
            // mask geometry first - the masked shapes derive their clips from it
            foreach (var it in MaskItems)
            {
                it.LivePath = null; it.LiveM = Matrix3x2.Identity;
                if (tracksByEl.TryGetValue(it.El, out var l))
                    foreach (var tr in l)
                    {
                        if (tr.Anim.Kind == SvgTrackKind.Path) { var p = TrackPath(tr, t); if (p != null) it.LivePath = p; }
                        else { var m = TrackMatrix(tr, t) ?? MotionMatrix(tr, t); if (m.HasValue) it.LiveM = m.Value; }
                    }
            }
            foreach (var tr in Tracks)
                if (tr.Anim.Kind == SvgTrackKind.Path)
                {
                    var p = TrackPath(tr, t);
                    if (p != null) foreach (var s in tr.Shapes) s.Path = p;
                }
            var statCache = new Dictionary<XElement, Matrix3x2>();
            foreach (var s in Shapes)
                if (s.Tag is XElement el && !s.IsText)
                {
                    // shapes without animated ancestors keep their static transform (their chain may not even be recomputable through use / nested svg);
                    // use content gets the animated transforms of the use site's ancestors on top (a CSS-animated group wrapping a <use>)
                    var site = UseSites.TryGetValue(s, out var s2) ? s2 : null;
                    bool anim = ChainAnimated(el, null) || (site != null && SiteAnim(site, t, out _));
                    if (anim)
                    {
                        // animated chain carried over the static import chain: total = chain(el) · inv(static chain) · base.
                        // For use content the base still carries the use site's statics; the site's animations apply last.
                        var total = Chain(el, null, t);
                        if (Matrix3x2.Invert(SvgReader.StaticChain(el, null, statCache), out var ic)) total = total * ic * s.BaseTransform;
                        if (site != null && SiteAnim(site, t, out var sm)) total = total * sm;
                        s.Transform = total;
                        if (s.ClipRoots != null)
                        {   // the clip follows its owner's ANIMATED chain only (the glass flips, the sand's own slide stays clipped)
                            var clip = new VectorClip();
                            foreach (var (root, owner) in s.ClipRoots)
                            {
                                var ao = AnimatedChain(owner as XElement, t);
                                if (site != null && SiteAnim(site, t, out var sm2)) ao = ao * sm2;
                                var moved = Matrix3x2.Invert(s.Transform, out var inv) ? root.Transformed(ao * inv) : root;
                                foreach (var pr in moved.Paths) clip.Paths.Add(pr);
                            }
                            s.Clip = clip;
                        }
                    }
                }
            // CSS opacity: reset every tracked shape to its import opacity, then multiply each active track's value
            // (two passes so nested animated opacities compose the same way whatever the track order is)
            foreach (var tr in Tracks)
                if (tr.Anim.Kind == SvgTrackKind.Opacity)
                    for (int i = 0; i < tr.Shapes.Count; i++) tr.Shapes[i].Opacity = tr.BaseO[i];
            foreach (var tr in Tracks)
                if (tr.Anim.Kind == SvgTrackKind.Opacity)
                {
                    var v = TrackOpacity(tr, t);
                    if (!v.HasValue) continue;
                    // the track value replaces the animated element's OWN static opacity (that is what the keyframes animate);
                    // the factor inherited from ancestor groups (InhO) stays multiplied in
                    for (int i = 0; i < tr.Shapes.Count; i++) tr.Shapes[i].Opacity = tr.InhO[i] * Math.Clamp(v.Value, 0f, 1f);
                }
            // CSS stroke-dashoffset: the flow resets to the import offset, the active value replaces it (px, may be negative)
            foreach (var tr in Tracks)
                if (tr.Anim.Kind == SvgTrackKind.DashOffset)
                    for (int i = 0; i < tr.Shapes.Count; i++) tr.Shapes[i].DashOffset = tr.BaseD[i];
            foreach (var tr in Tracks)
                if (tr.Anim.Kind == SvgTrackKind.DashOffset)
                {
                    var v = TrackOpacity(tr, t);
                    if (!v.HasValue) continue;
                    for (int i = 0; i < tr.Shapes.Count; i++) tr.Shapes[i].DashOffset = v.Value;
                }
            foreach (var b in MaskBinds)
            {
                var clip = new VectorClip();
                foreach (var it in b.Items) clip.Paths.Add(((it.LivePath ?? it.BasePath).Transformed(it.LiveM), it.EvenOdd));
                if (b.Static != null) { var merged = new VectorClip(); merged.Paths.AddRange(b.Static.Paths); merged.Paths.AddRange(clip.Paths); clip = merged; }
                var above = Chain(b.Shape.Tag as XElement, b.RefEl, t);   // shape-local -> mask user space
                b.Shape.Clip = Matrix3x2.Invert(above, out var inv) ? clip.Transformed(inv) : clip;
            }
        }

        /// <summary>A deep copy that shares everything immutable (paints, parsed keyframes) and clones everything the seek writes (shapes, mask geometry).</summary>
        internal VectorImage CloneAnimated()
        {
            var f = new VectorImage { ViewBox = ViewBox, Width = Width, Height = Height, Format = Format, Title = Title, Duration = Duration, FrameRate = FrameRate, ClipViewport = ClipViewport };
            f.Warnings.AddRange(Warnings);
            if (_visible.HasValue) f._visible = _visible;
            var map = new Dictionary<VectorShape, VectorShape>();
            foreach (var s in Shapes) { var cs = s.Clone(); map[s] = cs; f.Shapes.Add(cs); }
            var itemMap = new Dictionary<SvgMaskItem, SvgMaskItem>();
            foreach (var it in MaskItems)
            {
                var c2 = new SvgMaskItem { El = it.El, TopEl = it.TopEl, BasePath = it.BasePath, EvenOdd = it.EvenOdd, LivePath = it.LivePath, LiveM = it.LiveM };
                itemMap[it] = c2; f.MaskItems.Add(c2);
            }
            foreach (var tr in Tracks)
            {
                var t2 = new SvgTrack { Anim = tr.Anim, El = tr.El, Target = tr.Target, Parsed = tr.Parsed, Compat = tr.Compat, ParsedM = tr.ParsedM, ParsedCss = tr.ParsedCss, ParsedO = tr.ParsedO, MotionPts = tr.MotionPts, MotionLen = tr.MotionLen, MotionTotal = tr.MotionTotal, OriginBox = tr.OriginBox, Origin = tr.Origin, OriginPt = tr.OriginPt, BaseM = tr.BaseM, BaseOwn = tr.BaseOwn };
                t2.BaseO.AddRange(tr.BaseO);
                t2.InhO.AddRange(tr.InhO);
                t2.BaseD.AddRange(tr.BaseD);
                foreach (var s in tr.Shapes) if (map.TryGetValue(s, out var ms)) t2.Shapes.Add(ms);
                foreach (var it in tr.MaskItems) if (itemMap.TryGetValue(it, out var mi)) t2.MaskItems.Add(mi);
                f.Tracks.Add(t2);
            }
            foreach (var b in MaskBinds)
                if (map.TryGetValue(b.Shape, out var ms))
                    f.MaskBinds.Add(new SvgMaskBind { Shape = ms, RefEl = b.RefEl, Static = b.Static, Items = b.Items.Select(i2 => itemMap[i2]).ToList() });
            foreach (var kv in UseSites) if (map.TryGetValue(kv.Key, out var ms2)) f.UseSites[ms2] = kv.Value;
            foreach (var r in UseRoots) f.UseRoots.Add(r);
            return f;
        }

        // ------------------------------------------------------------------ evaluation ----------------------------------
        /// <summary>The transform of an element at t: the product of the transform attributes (with animated values substituted) from el up to the root,
        /// stopping at <paramref name="top"/> (exclusive), at &lt;use&gt; fragment roots and at &lt;svg&gt; elements.</summary>
        Matrix3x2 Chain(XElement? el, XElement? top, double t)
        {
            var m = Matrix3x2.Identity;
            var cur = el; bool first = true;
            while (cur != null)
            {
                var own = OwnOf(cur);
                if (tracksByEl != null && tracksByEl.TryGetValue(cur, out var l))
                    foreach (var tr in l)
                    {
                        if (tr.Anim.Kind == SvgTrackKind.Path) continue;
                        var v = tr.Anim.Kind == SvgTrackKind.Motion ? MotionMatrix(tr, t) : TrackMatrix(tr, t);
                        if (!v.HasValue) continue;
                        // additive sum / motion are appended to the transform LIST -> applied first (ParseTransform composes k * m, too)
                        own = tr.Anim.Kind == SvgTrackKind.Motion || tr.Anim.Additive ? v.Value * own : v.Value;
                    }
                m = m * own;   // row-vector chain: the element's own matrix applies to the point first, then its parent's (p' = p·M_el·M_par·…·M_root)
                if (ReferenceEquals(cur, top)) break;
                if (!first && UseRoots.Contains(cur)) break;
                if (cur.Name.LocalName == "svg") break;   // (&lt;use x y&gt; offsets and nested-svg viewports are part of the static import, not of the chain)
                cur = cur.Parent; first = false;
            }
            return m;
        }

        /// <summary>True when an animated element with a transform / motion track sits on the chain from el up to (excluding) top.</summary>
        bool ChainAnimated(XElement? el, XElement? top)
        {
            if (tracksByEl == null) return false;
            var cur = el; bool first = true;
            while (cur != null)
            {
                if (tracksByEl.TryGetValue(cur, out var l) && l.Any(tr => tr.Anim.Kind != SvgTrackKind.Path && tr.Anim.Kind != SvgTrackKind.Opacity)) return true;
                if (ReferenceEquals(cur, top) || (!first && UseRoots.Contains(cur)) || cur.Name.LocalName == "svg") return false;
                cur = cur.Parent; first = false;
            }
            return false;
        }

        /// <summary>The animated transforms of the ancestors of a use site (and of the use element itself), composed; the static parts
        /// are already baked into the shape's transform. <paramref name="anim"/> says whether anything animated was found.</summary>
        bool SiteAnim(XElement site, double t, out Matrix3x2 m)
        {
            m = Matrix3x2.Identity; bool any = false;
            if (tracksByEl == null) return any;
            var cur = site;
            while (cur != null)
            {
                var own = Matrix3x2.Identity; bool anyHere = false;
                if (tracksByEl.TryGetValue(cur, out var l))
                    foreach (var tr in l)
                    {
                        if (tr.Anim.Kind != SvgTrackKind.Transform && tr.Anim.Kind != SvgTrackKind.Motion) continue;
                        var v = tr.Anim.Kind == SvgTrackKind.Motion ? MotionMatrix(tr, t) : TrackMatrix(tr, t);
                        if (!v.HasValue) continue;
                        own = tr.Anim.Additive || tr.Anim.Kind == SvgTrackKind.Motion ? v.Value * own : v.Value;   // additive appends to the (baked) static list -> applies first
                        anyHere = true;
                    }
                if (anyHere) { m = m * own; any = true; }   // row-vector chain: the use element's own animation applies before its ancestors'
                cur = cur.Name.LocalName == "svg" ? null : cur.Parent;
            }
            return any;
        }

        /// <summary>The animated-only part of the transform chain from <paramref name="el"/> up (static attributes excluded):
        /// what must move a clip that lives in an ancestor's user space, on top of the clip's baked static placement.</summary>
        Matrix3x2 AnimatedChain(XElement? el, double t)
        {
            var m = Matrix3x2.Identity;
            if (tracksByEl == null) return m;
            var cur = el; bool first = true;
            while (cur != null)
            {
                var own = Matrix3x2.Identity; bool anyHere = false;
                if (tracksByEl.TryGetValue(cur, out var l))
                    foreach (var tr in l)
                    {
                        if (tr.Anim.Kind != SvgTrackKind.Transform && tr.Anim.Kind != SvgTrackKind.Motion) continue;
                        var v = tr.Anim.Kind == SvgTrackKind.Motion ? MotionMatrix(tr, t) : TrackMatrix(tr, t);
                        if (!v.HasValue) continue;
                        own = tr.Anim.Additive || tr.Anim.Kind == SvgTrackKind.Motion ? v.Value * own : v.Value;
                        anyHere = true;
                    }
                if (anyHere) m = m * own;
                if (!first && UseRoots.Contains(cur)) break;
                if (cur.Name.LocalName == "svg") break;
                cur = cur.Parent; first = false;
            }
            return m;
        }

        // ------------------------------------------------------------------ layer compositor support -------------------------
        internal double _seekT;

        /// <summary>Every track that drives a shape, in track-creation order (outer animated ancestors before inner ones).</summary>
        internal Dictionary<VectorShape, List<SvgTrack>> ShapeTracks()
        {
            var map = new Dictionary<VectorShape, List<SvgTrack>>();
            foreach (var tr in Tracks)
                foreach (var s in tr.Shapes)
                { if (!map.TryGetValue(s, out var l)) map[s] = l = new List<SvgTrack>(); l.Add(tr); }
            return map;
        }

        /// <summary>The animated part of the shape's chain at the last seek, as a delta over its static import chain
        /// (inv(BaseTransform) · Chain): the matrix the layer compositor blits its baked raster with.</summary>
        internal Matrix3x2 ChainDeltaOf(VectorShape s)
        {
            if (tracksByEl == null)
            {
                tracksByEl = new Dictionary<XElement, List<SvgTrack>>();
                foreach (var tr in Tracks) { if (!tracksByEl.TryGetValue(tr.Target, out var l)) tracksByEl[tr.Target] = l = new List<SvgTrack>(); l.Add(tr); }
            }
            if (s.Tag is XElement el && Matrix3x2.Invert(s.BaseTransform, out var ib)) return ib * Chain(el, null, _seekT);
            return Matrix3x2.Identity;
        }

        /// <summary>True when the track moves the element (transform / motion). Such content can live in a cached raster that is
        /// blitted per frame and only re-rasterised when the chain's linear part drifts (slow dollies / parallax). Tracks that
        /// change geometry or paint per frame (morph, opacity, dash) must stay live.</summary>
        internal static bool TrackIsMover(SvgTrack tr) => tr.Anim.Kind == SvgTrackKind.Transform || tr.Anim.Kind == SvgTrackKind.Motion;

        Matrix3x2 OwnOf(XElement el)
        {
            if (ownM.TryGetValue(el, out var m)) return m;
            m = SvgReader.ParseTransform((string?)el.Attribute("transform"));
            ownM[el] = m; return m;
        }

        /// <summary>Where t falls in the track's cycle: the 0..1 phase, or null when the track is not active at t (before begin / ended without freeze).
        /// CSS direction / fill-mode are honoured (reverse flips the phase, alternate flips every other cycle, backwards holds the first keyframe through the delay).</summary>
        static float? Phase(SvgAnim a, double t, out bool frozen)
        {
            frozen = false;
            bool flip = a.Direction == 1 || a.Direction == 3;   // the 1..0 direction (reverse, and the first cycle of alternate-reverse)
            double tt = t - a.Begin;
            if (tt < 0)
            {
                if (!a.FillBackward) return null;
                frozen = true; return flip ? 1f : 0f;
            }
            if (a.Dur <= 0) { frozen = true; return flip ? 1f : 0f; }   // <set>: hold from begin on
            double total = a.Indefinite ? double.PositiveInfinity : Math.Max(0, a.Repeat) * a.Dur;
            if (tt >= total)
            {
                if (!a.Freeze) return null;
                frozen = true; return flip ? 0f : 1f;   // freeze holds the LAST keyframe (phase 1; 0 when the direction is flipped)
            }
            double cyc = Math.Floor(tt / a.Dur);
            float u = (float)((tt % a.Dur) / a.Dur);
            switch (a.Direction)
            {
                case 1: u = 1f - u; break;
                case 2: if (cyc % 2 == 1) u = 1f - u; break;
                case 3: if (cyc % 2 == 0) u = 1f - u; break;
            }
            return u;
        }

        /// <summary>The interpolated path of a d track at t; null = inactive (the shapes keep their base path).</summary>
        VectorPath? TrackPath(SvgTrack tr, double t)
        {
            var a = tr.Anim;
            if (a.Values.Length == 0) return null;
            if (tr.Parsed == null)
            {
                tr.Parsed = new List<VectorPath?>();
                bool toOnly = a.Values.Length == 1 && tr.Target.Attribute("d") != null;   // to-only animation morphs from the base d
                if (toOnly) tr.Parsed.Add(TryParse((string)tr.Target.Attribute("d")!));
                foreach (var v in a.Values) tr.Parsed.Add(TryParse(v));
                tr.Compat = tr.Parsed.All(p => p != null) && tr.Parsed.Zip(tr.Parsed.Skip(1), (p, q) => SameCommands(p!, q!)).All(ok => ok) && tr.Parsed.Count >= 2;
            }
            var vals = tr.Parsed;
            var u = Phase(a, t, out _); if (u == null) return null;
            if (vals.Count == 1) return vals[0];
            if (a.CalcMode == 2 || !tr.Compat)
                return vals[DiscreteIndex(a, u.Value, vals.Count)];
            var kt = KeyTimesOf(a, vals.Count);
            int i = SegmentOf(kt, u.Value, out float f);
            if (a.CalcMode == 1 && i < a.KeySplines.Length) f = Ease(a.KeySplines[i], f);
            return LerpPaths(vals[i]!, vals[i + 1]!, f);

            static VectorPath? TryParse(string s) { try { return VectorPath.ParseSvg(s); } catch { return null; } }
        }

        /// <summary>The interpolated matrix of an animateTransform / CSS transform track at t; null = inactive.</summary>
        Matrix3x2? TrackMatrix(SvgTrack tr, double t)
        {
            var a = tr.Anim;
            if (tr.ParsedM == null)
            {
                if (a.Values.Length == 0) return null;
                tr.ParsedM = new List<Matrix3x2>();
                foreach (var v in a.Values)
                {
                    string s = a.TransformType != null && a.TransformType.Length > 0 ? a.TransformType + "(" + v + ")" : v;
                    tr.ParsedM.Add(SvgReader.ParseTransform(s));
                }
            }
            if (tr.ParsedCss == null && a.Values.Length > 0) tr.ParsedCss = a.Values.Select(v => TransformPrims(a.TransformType != null && a.TransformType.Length > 0 ? a.TransformType + "(" + v + ")" : v)).ToList();
            var ms = tr.ParsedM;
            var u = Phase(a, t, out _); if (u == null) return null;
            Matrix3x2 m;
            if (ms.Count == 1) m = ms[0];
            else if (a.CalcMode == 2) m = ms[DiscreteIndex(a, u.Value, ms.Count)];
            else
            {
                var kt = KeyTimesOf(a, ms.Count);
                int i = SegmentOf(kt, u.Value, out float f);
                if (a.CalcMode == 1 && i < a.KeySplines.Length) f = Ease(a.KeySplines[i], f);
                var prims = tr.ParsedCss;
                if (prims != null && prims.Count == ms.Count && prims[i] != null && prims[i + 1] != null && prims[i]!.Count == prims[i + 1]!.Count && prims[i]!.Zip(prims[i + 1]!, (p, q) => p.Kind == q.Kind).All(ok => ok))
                {   // matching function lists: lerp the parameters - a rotate 0 -> 360 sweeps a full turn (matrix-element lerp would hold still)
                    var k = new List<CssPrim>(prims[i]!.Count);
                    for (int p = 0; p < prims[i]!.Count; p++)
                    {
                        var pa = prims[i]![p]; var pb = prims[i + 1]![p];
                        k.Add(new CssPrim { Kind = pa.Kind, A = pa.A + (pb.A - pa.A) * f, B = pa.B + (pb.B - pa.B) * f, C = pa.C + (pb.C - pa.C) * f });
                    }
                    m = ComposePrims(k);
                }
                else m = Matrix3x2.Lerp(ms[i], ms[i + 1], f);   // (component lerp: exact for translate / scale, close for skew; rotating matrices lerp through a squish)
            }
            if (tr.OriginPt.HasValue)
            {   // CSS transform-origin / transform-box: rotate / scale turn around a point of the element's box
                // (row-vector convention: p' = (p - o)·M·(+o) - the opposite of the column-vector form)
                var o = tr.OriginPt.Value;
                m = Matrix3x2.CreateTranslation(-o.X, -o.Y) * m * Matrix3x2.CreateTranslation(o.X, o.Y);
            }
            return m;
        }

        /// <summary>A transform list as elementary functions (null when it contains a raw matrix() - that one only interpolates element-wise).</summary>
        internal static List<CssPrim>? TransformPrims(string v)
        {
            var list = new List<CssPrim>();
            foreach (Match mt in Regex.Matches(v, @"(matrix|translate|translateX|translateY|scale|scaleX|scaleY|rotate|skew|skewX|skewY)\s*\(([^)]*)\)"))
            {
                var a = ParseNumbersCompat(mt.Groups[2].Value);
                switch (mt.Groups[1].Value)
                {
                    case "translate": list.Add(CssPrim.Translate(a.Count > 0 ? a[0] : 0, a.Count > 1 ? a[1] : 0)); break;
                    case "translateX": list.Add(CssPrim.Translate(a.Count > 0 ? a[0] : 0, 0)); break;
                    case "translateY": list.Add(CssPrim.Translate(0, a.Count > 0 ? a[0] : 0)); break;
                    case "scale": list.Add(CssPrim.Scale(a.Count > 0 ? a[0] : 1, a.Count > 1 ? a[1] : (a.Count > 0 ? a[0] : 1))); break;
                    case "scaleX": list.Add(CssPrim.Scale(a.Count > 0 ? a[0] : 1, 1)); break;
                    case "scaleY": list.Add(CssPrim.Scale(1, a.Count > 0 ? a[0] : 1)); break;
                    case "rotate":
                        if (a.Count >= 3) { list.Add(CssPrim.Translate(a[1], a[2])); list.Add(CssPrim.Rotate((a.Count > 0 ? a[0] : 0) * MathF.PI / 180f)); list.Add(CssPrim.Translate(-a[1], -a[2])); }
                        else list.Add(CssPrim.Rotate((a.Count > 0 ? a[0] : 0) * MathF.PI / 180f));
                        break;
                    case "skew": list.Add(CssPrim.Skew((a.Count > 0 ? a[0] : 0) * MathF.PI / 180f, (a.Count > 1 ? a[1] : 0) * MathF.PI / 180f)); break;
                    case "skewX": list.Add(CssPrim.Skew((a.Count > 0 ? a[0] : 0) * MathF.PI / 180f, 0)); break;
                    case "skewY": list.Add(CssPrim.Skew(0, (a.Count > 0 ? a[0] : 0) * MathF.PI / 180f)); break;
                    default: return null;   // matrix(): no parameter form
                }
            }
            return list.Count > 0 ? list : null;
        }

        static List<float> ParseNumbersCompat(string s) => SvgReader.ParseNumbers(s);

        /// <summary>A transform-function list as a matrix, applied right-to-left (later functions first, like ParseTransform).</summary>
        static Matrix3x2 ComposePrims(List<CssPrim> prims)
        {
            var m = Matrix3x2.Identity;
            foreach (var p in prims)
                m = (p.Kind switch
                {
                    0 => Matrix3x2.CreateTranslation(p.A, p.B),
                    1 => Matrix3x2.CreateScale(p.A, p.B),
                    2 => Matrix3x2.CreateRotation(p.A),
                    _ => Matrix3x2.CreateSkew(p.A, p.B),
                }) * m;
            return m;
        }

        /// <summary>The interpolated opacity of a CSS track at t (multiplies the shape's import opacity); null = inactive.</summary>
        float? TrackOpacity(SvgTrack tr, double t)
        {
            var vs = tr.ParsedO;
            if (vs == null || vs.Count == 0) return null;
            var a = tr.Anim;
            var u = Phase(a, t, out _); if (u == null) return null;
            if (vs.Count == 1) return vs[0];
            if (a.CalcMode == 2) return vs[DiscreteIndex(a, u.Value, vs.Count)];
            var kt = KeyTimesOf(a, vs.Count);
            int i = SegmentOf(kt, u.Value, out float f);
            if (a.CalcMode == 1 && i < a.KeySplines.Length) f = Ease(a.KeySplines[i], f);
            return vs[i] + (vs[Math.Min(i + 1, vs.Count - 1)] - vs[i]) * f;
        }

        /// <summary>The animateMotion matrix at t: position on the motion path (+ auto rotation); null = inactive.</summary>
        Matrix3x2? MotionMatrix(SvgTrack tr, double t)
        {
            var a = tr.Anim;
            if (tr.MotionPts == null)
            {
                tr.MotionPts = new List<PointF>(); tr.MotionLen = new List<float>(); tr.MotionTotal = 0;
                string? pathD = (string?)tr.El.Attribute("path");
                if (!string.IsNullOrWhiteSpace(pathD))
                {
                    var pts = new List<PointF>();
                    var subs = VectorRender.Flatten(VectorPath.ParseSvg(pathD), Matrix3x2.Identity, 0.25f, pts);
                    (int start, int count, bool closed) best = (0, 0, false);
                    foreach (var s2 in subs) if (s2.count > best.count) best = s2;   // the longest sub-path is the motion path
                    float total = 0; var lens = new List<float>();
                    for (int k2 = 0; k2 < best.count; k2++)
                    {
                        tr.MotionPts.Add(pts[best.start + k2]);
                        if (k2 == 0) lens.Add(0);
                        else { var p0 = pts[best.start + k2 - 1]; var p1 = pts[best.start + k2]; total += MathF.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Y - p0.Y) * (p1.Y - p0.Y)); lens.Add(total); }
                    }
                    tr.MotionLen = lens; tr.MotionTotal = total;
                }
            }
            var u = Phase(a, t, out _); if (u == null || tr.MotionLen == null || tr.MotionLen.Count == 0) return null;
            float frac = u.Value;
            if (a.KeyPoints.Length > 0)
            {
                var kt = a.KeyTimes.Length == a.KeyPoints.Length ? a.KeyTimes : UniformKeyTimes(a.KeyPoints.Length);
                int i = SegmentOf(kt, u.Value, out float f);
                if (a.CalcMode == 1 && i < a.KeySplines.Length) f = Ease(a.KeySplines[i], f);
                if (a.CalcMode == 2) frac = a.KeyPoints[DiscreteIndex(a, u.Value, a.KeyPoints.Length)];
                else { var v0 = a.KeyPoints[i]; var v1 = a.KeyPoints[Math.Min(i + 1, a.KeyPoints.Length - 1)]; frac = v0 + (v1 - v0) * f; }
            }
            float dist = Math.Clamp(frac, 0f, 1f) * tr.MotionTotal;
            int j = 1; while (j < tr.MotionLen.Count - 1 && tr.MotionLen[j] < dist) j++;
            var pa = tr.MotionPts[j - 1]; var pb = tr.MotionPts[j];
            float segLen = tr.MotionLen[j] - tr.MotionLen[j - 1];
            float k = segLen > 1e-6f ? Math.Clamp((dist - tr.MotionLen[j - 1]) / segLen, 0f, 1f) : 0f;   // inside the flatten chord - snapping to pa stutters
            var m = Matrix3x2.CreateTranslation(pa.X + (pb.X - pa.X) * k, pa.Y + (pb.Y - pa.Y) * k);
            if (a.RotateAuto || a.RotateAngle != 0)
            {
                float ang = a.RotateAuto ? MathF.Atan2(pb.Y - pa.Y, pb.X - pa.X) : a.RotateAngle * MathF.PI / 180f;
                m = Matrix3x2.CreateRotation(ang) * m;
            }
            return m;
        }

        // keyframes / segments
        static float[] KeyTimesOf(SvgAnim a, int n)
        {
            if (a.KeyTimes.Length == n) return a.KeyTimes;
            if (a.KeyTimes.Length == n + 1 && a.Values.Length == 0) return a.KeyTimes;   // to-only: base + n entries
            return UniformKeyTimes(n);
        }
        static float[] UniformKeyTimes(int n)
        {
            var kt = new float[n];
            for (int i = 0; i < n; i++) kt[i] = n <= 1 ? 0 : (float)i / (n - 1);
            return kt;
        }
        static int SegmentOf(float[] kt, float u, out float f)
        {
            u = Math.Clamp(u, 0f, 1f);
            int i = 0;
            while (i < kt.Length - 2 && u > kt[i + 1]) i++;
            float span = kt[i + 1] - kt[i];
            f = span <= 1e-6f ? 0f : Math.Clamp((u - kt[i]) / span, 0f, 1f);
            return Math.Min(i, kt.Length - 2);
        }
        static int DiscreteIndex(SvgAnim a, float u, int n)
        {
            var kt = KeyTimesOf(a, n);
            u = Math.Clamp(u, 0f, 1f);
            int j = 0;
            while (j < n - 1 && u >= kt[j + 1]) j++;
            return j;
        }
        /// <summary>Cubic bezier ease: progress x -> progress y through the control points (x1,y1,x2,y2).</summary>
        static float Ease(float[] s, float x)
        {
            x = Math.Clamp(x, 0f, 1f);
            float x1 = s[0], y1 = s[1], x2 = s[2], y2 = s[3];
            float bx(float u) => 3 * u * (1 - u) * (1 - u) * x1 + 3 * u * u * (1 - u) * x2 + u * u * u;
            float by(float u) => 3 * u * (1 - u) * (1 - u) * y1 + 3 * u * u * (1 - u) * y2 + u * u * u;
            float lo = 0, hi = 1, uu = x;
            for (int i = 0; i < 24; i++)
            {
                float xv = bx(uu);
                if (MathF.Abs(xv - x) < 1e-5f) break;
                if (xv < x) lo = uu; else hi = uu;
                uu = (lo + hi) * 0.5f;
            }
            return by(uu);
        }
        /// <summary>True when two paths share their command structure (same commands, same sizes) - the precondition for morphing.</summary>
        static bool SameCommands(VectorPath a, VectorPath b)
        {
            if (a.Kinds.Count != b.Kinds.Count) return false;
            for (int i = 0; i < a.Kinds.Count; i++)
                if (a.Kinds[i] != b.Kinds[i]) return false;
            return a.Data.Count == b.Data.Count;
        }
        static VectorPath LerpPaths(VectorPath a, VectorPath b, float f)
        {
            var p = new VectorPath();
            for (int i = 0; i < a.Kinds.Count; i++) p.Kinds.Add(a.Kinds[i]);
            for (int i = 0; i < a.Data.Count; i++) p.Data.Add(a.Data[i] + (b.Data[i] - a.Data[i]) * f);
            return p;
        }
    }
}
