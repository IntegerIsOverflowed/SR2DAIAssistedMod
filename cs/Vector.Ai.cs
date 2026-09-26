// SR2D - Adobe Illustrator native operators for the PostScript interpreter.
//
// Illustrator files (.ai, and the PostScript hidden in PDF-based .ai files as /AIPrivateData) draw with short
// operators - m L C f S Xa XA Xy Lb ... - that older files define through a prolog (Adobe_Illustrator_AI5 etc.) and
// newer files (AI9+, everything CorelDRAW / Inkscape / Illustrator itself writes today) do not define at all: the
// reader is expected to know them. This file provides them in a lowest-priority dictionary, so a file that ships its own
// prolog (tiger.eps, AI8 exports) keeps using its own definitions while prolog-less files still render.
//
// Semantics follow the Adobe Illustrator 7 file format specification, the AI5 prolog shipped in AI8 files and Inkscape's
// extension-ai (which documents the CS4+ additions: XW known styles, Xy opacity, relative gradient geometry, Bm).
//
// Supported: paths (m l L c C v V y Y h H), painting (f F s S b B n N), clipping (W q Q), groups (u U), compound
// paths (*u *U: one paint for all subpaths), layers (Lb Ln LB, hidden layers are skipped), fill / stroke colours
// (g G k K Xa XA x X Xx XX Xk XK), opacity (Xy, group opacity through "6 () XW"), fill rule (XR), line attributes
// (w j J M d i), linear / radial gradients (Bd Bs BD definitions, Bb Bg Bh Bm BB instances), embedded raster images
// (XI, hex or binary; grey / RGB / CMYK / bitmap, alpha channel, image masks). Skipped with a warning: text objects,
// patterns, brushes, symbols, effects (art styles) - their bezier fallback, when the file carries one, is drawn.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Sr2d64CSport
{
    internal sealed partial class PsInterp
    {
        readonly PsDict aidict = new PsDict();                       // lowest-priority dictionary (see Setup)
        readonly HashSet<string> aiProcsets = new HashSet<string>(); // procsets the header says the printer supplies (AI3 / AI88): stubbed
        int aiCompound;                                              // *u nesting depth: paints are deferred to the matching *U
        int aiDeferredPaint;                                         // paint requested inside a compound path: bit 1 fill, bit 2 stroke
        bool aiClipPending;                                          // W seen: the next paint (or n) turns the path into a clip
        readonly List<int> aiGroupStart = new List<int>();           // shape index at each open u
        int aiLastGroupStart = -1, aiLastGroupEnd = -1;              // shapes of the most recently closed group (for "6 () XW")
        readonly List<bool> aiLayerHidden = new List<bool>();
        // object names: "(name) Ln" layer names become the shapes' Group path, "%_/XMLUID : (id)" art-dictionary comments
        // (what Illustrator writes as the SVG id) name the art object they follow - a shape, or the group / layer just closed
        readonly List<(string? name, int start)> aiLayers = new List<(string?, int)>();
        enum AiArt { None, Shape, Group, Layer }
        AiArt aiLastArt; int aiLastArtStart, aiLastArtEnd;
        void AiXmlUid(string id)
        {
            switch (aiLastArt)
            {
                case AiArt.Shape:
                    for (int i = aiLastArtStart; i < aiLastArtEnd && i < img.Shapes.Count; i++) if (string.IsNullOrEmpty(img.Shapes[i].Id)) img.Shapes[i].Id = id;
                    break;
                case AiArt.Group:
                    AiPrefixGroup(aiLastArtStart, aiLastArtEnd, id);
                    break;
                case AiArt.Layer:   // the layer already has its Ln name; an XMLUID right after Ln is the same layer
                    if (aiLayers.Count > 0 && aiLayers[^1].name == null) aiLayers[^1] = (id, aiLayers[^1].start);
                    break;
            }
            aiLastArt = AiArt.None;
        }
        void AiPrefixGroup(int start, int end, string name)
        {
            for (int i = start; i < end && i < img.Shapes.Count; i++) { var s = img.Shapes[i]; s.Group = string.IsNullOrEmpty(s.Group) ? name : name + "/" + s.Group; }
        }
        // gradients
        sealed class AiGradientDef { public bool Radial; public List<VectorStop> Stops = new List<VectorStop>(); }
        readonly Dictionary<string, AiGradientDef> aiGradients = new Dictionary<string, AiGradientDef>();
        sealed class AiBg { public bool HasFlag; public string Name = ""; public float XO, YO, Angle, Len, A = 1, B, C, D = 1, Tx, Ty, Aspect = 1; }
        AiBg? aiBg; Matrix3x2? aiBm; (float xh, float yh, float angle, float len)? aiBh; int aiGradIdx;   // pending instance (Bb ... BB)
        VectorGradient? aiFillGrad, aiStrokeGrad;                    // built at the first paint after Bg; cleared by colour operators

        bool AiHidden { get { foreach (var h in aiLayerHidden) if (h) return true; return false; } }

        void AiDef(string n, Action<PsInterp> f) => aidict.D[n] = new PsOp(n, f);
        void PopN(int n) { for (int i = 0; i < n && St.Count > 0; i++) Pop(); }
        void PopNumbers(int max) { for (int i = 0; i < max && St.Count > 0 && St[^1] is double; i++) Pop(); }
        float FOr(float dflt) { if (St.Count > 0 && St[^1] is double d) { Pop(); return (float)d; } if (St.Count > 0 && St[^1] is PsNull) { Pop(); } return dflt; }

        void SetupAi()
        {
            foreach (var ps in aiProcsets)
            {   // "Adobe_packedarray /initialize get exec" style calls on procsets the file does not embed
                if (Lookup(ps) != null) continue;
                var d = new PsDict(); var nop = new PsArray(new List<object?>(), true);
                d.D["initialize"] = nop; d.D["terminate"] = nop; aidict.D[ps] = d;
            }
            AiDef("--", p => p.Push(PsNull.I));   // Illustrator writes "--" for "no value" (e.g. Xy opacity)

            // ---- path construction (uppercase = point is not a smooth anchor; same geometry)
            AiDef("m", p => { float y = p.F(), x = p.F(); p.MoveTo(x, y); });
            AiDef("l", p => { float y = p.F(), x = p.F(); p.LineTo(x, y); }); aidict.D["L"] = aidict.D["l"];
            AiDef("c", p => { float y3 = p.F(), x3 = p.F(), y2 = p.F(), x2 = p.F(), y1 = p.F(), x1 = p.F(); p.CurveTo(x1, y1, x2, y2, x3, y3); }); aidict.D["C"] = aidict.D["c"];
            AiDef("v", p => { float y3 = p.F(), x3 = p.F(), y2 = p.F(), x2 = p.F(); var c = p.CurrentUser(); p.CurveTo(c.X, c.Y, x2, y2, x3, y3); }); aidict.D["V"] = aidict.D["v"];
            AiDef("y", p => { float y3 = p.F(), x3 = p.F(), y1 = p.F(), x1 = p.F(); p.CurveTo(x1, y1, x3, y3, x3, y3); }); aidict.D["Y"] = aidict.D["y"];
            AiDef("h", p => p.AiClose()); AiDef("H", p => { });
            // ---- painting: lowercase closes the path first
            AiDef("f", p => p.AiPaint(1, true)); AiDef("F", p => p.AiPaint(1, false));
            AiDef("s", p => p.AiPaint(2, true)); AiDef("S", p => p.AiPaint(2, false));
            AiDef("b", p => p.AiPaint(3, true)); AiDef("B", p => p.AiPaint(3, false));
            AiDef("n", p => p.AiPaint(0, false)); AiDef("N", p => p.AiPaint(0, false));
            AiDef("W", p => p.aiClipPending = true);
            AiDef("*", p => { if (p.St.Count > 0 && p.St[^1] is PsString) p.Pop(); p.NewPath(); });   // guide: never painted
            AiDef("q", p => p.GSave()); AiDef("Q", p => p.GRestore());
            AiDef("u", p => p.aiGroupStart.Add(p.img.Shapes.Count));
            AiDef("U", p => { if (p.aiGroupStart.Count == 0) return; p.aiLastGroupStart = p.aiGroupStart[^1]; p.aiGroupStart.RemoveAt(p.aiGroupStart.Count - 1); p.aiLastGroupEnd = p.img.Shapes.Count; p.aiLastArt = AiArt.Group; p.aiLastArtStart = p.aiLastGroupStart; p.aiLastArtEnd = p.aiLastGroupEnd; });
            AiDef("*u", p => p.aiCompound++);
            AiDef("*U", p => { if (p.aiCompound > 0 && --p.aiCompound == 0) { int k = p.aiDeferredPaint; p.aiDeferredPaint = 0; p.AiDoPaint(k); } });
            // ---- layers: "visible preview enabled printing dimmed multi color r g b [...] Lb", "(name) Ln", "LB"
            AiDef("Lb", p =>
            {
                var nums = new List<double>(); while (p.St.Count > 0 && p.St[^1] is double d) { nums.Add(d); p.Pop(); }
                nums.Reverse(); bool visible = nums.Count < 7 || nums[0] != 0;
                p.aiLayerHidden.Add(!visible); p.aiLayers.Add((null, p.img.Shapes.Count));
            });
            AiDef("Ln", p =>
            {
                string? name = p.St.Count > 0 && p.St[^1] is PsString ls ? ls.S.Trim() : null; p.PopN(1);
                if (p.aiLayers.Count > 0 && !string.IsNullOrEmpty(name)) p.aiLayers[^1] = (name, p.aiLayers[^1].start);
                p.aiLastArt = AiArt.Layer;
            });
            AiDef("LB", p =>
            {
                if (p.aiLayerHidden.Count > 0) p.aiLayerHidden.RemoveAt(p.aiLayerHidden.Count - 1);
                if (p.aiLayers.Count > 0) { var (name, start) = p.aiLayers[^1]; p.aiLayers.RemoveAt(p.aiLayers.Count - 1); if (!string.IsNullOrEmpty(name)) p.AiPrefixGroup(start, p.img.Shapes.Count, name!); }
                p.aiLastArt = AiArt.None;
            });
            // ---- attributes that only pop
            foreach (var n1 in new[] { "A", "Ap", "Ar", "As", "Ae", "AE", "Xw", "O", "R", "D", "Bn", "XN", "XD", "Pc", "Pn", "Xt" }) AiDef(n1, p => p.PopN(1));
            foreach (var n2 in new[] { "Xd", "XG", "Pb", "XT" }) AiDef(n2, p => p.PopN(2));
            foreach (var n0 in new[] { "Np", "PB", "E", "XH", "XF", "Mb", "Md", "MB", "Xf" }) AiDef(n0, p => { });
            AiDef("Xh", p => p.PopN(4)); AiDef("XP", p => p.PopN(4)); AiDef("Xs", p => p.PopN(6)); AiDef("XS", p => p.PopN(6));
            AiDef("Xz", p => { p.PopN(2); if (p.St.Count > 0 && p.St[^1] is PsString) p.Pop(); p.PopNumbers(7); });
            AiDef("XI", p => p.AiImage());
            AiDef("XR", p => p.gs.AiEvenOdd = p.Int() != 0);
            AiDef("i", p => { float f = p.F(); if (f > 0) p.gs.Flat = f; });
            AiDef("w", p => p.gs.LineWidth = p.F());
            AiDef("j", p => p.gs.Join = (VectorJoin)Math.Clamp(p.Int(), 0, 2));
            AiDef("J", p => p.gs.Cap = (VectorCap)Math.Clamp(p.Int(), 0, 2));
            AiDef("M", p => p.gs.Miter = p.F());
            AiDef("d", p => p.Exec(p.Lookup("setdash")));
            // ---- opacity: "mode opacity 0 0 0 Xy" (opacity may be --)
            AiDef("Xy", p =>
            {
                p.PopNumbers(3); float op = p.FOr(1); p.PopN(1);
                p.gs.Alpha = Math.Clamp(op, 0, 1);
            });
            // ---- known styles: "n (name) XW": 6 = apply the current blend (opacity) to the group just closed, 9 = reset it, 2/7 = current container
            AiDef("XW", p =>
            {
                if (p.St.Count > 0 && p.St[^1] is PsString) p.Pop(); int n = (int)p.FOr(0);
                if (n == 6 || n == 2 || n == 7)
                {
                    if (p.gs.Alpha < 1 && p.aiLastGroupStart >= 0)
                        for (int i = p.aiLastGroupStart; i < p.aiLastGroupEnd && i < p.img.Shapes.Count; i++) p.img.Shapes[i].Opacity *= p.gs.Alpha;
                }
            });
            // ---- colours: lowercase fill, uppercase stroke
            AiDef("g", p => { float g = p.F(); p.AiFill(g, g, g); }); AiDef("G", p => { float g = p.F(); p.AiStroke(g, g, g); });
            AiDef("k", p => { var (r, g, b) = p.PopCmyk(); p.AiFill(r, g, b); }); AiDef("K", p => { var (r, g, b) = p.PopCmyk(); p.AiStroke(r, g, b); });
            AiDef("Xa", p => { var (r, g, b) = p.PopXa(); p.AiFill(r, g, b); }); AiDef("XA", p => { var (r, g, b) = p.PopXa(); p.AiStroke(r, g, b); });
            AiDef("x", p => { var (r, g, b) = p.PopCustom(false); p.AiFill(r, g, b); }); AiDef("X", p => { var (r, g, b) = p.PopCustom(false); p.AiStroke(r, g, b); });
            AiDef("Xx", p => { var (r, g, b) = p.PopCustom(true); p.AiFill(r, g, b); }); AiDef("XX", p => { var (r, g, b) = p.PopCustom(true); p.AiStroke(r, g, b); });
            AiDef("Xk", p => { var (r, g, b) = p.PopXk(); p.AiFill(r, g, b); }); AiDef("XK", p => { var (r, g, b) = p.PopXk(); p.AiStroke(r, g, b); });
            AiDef("p", p => { p.Warn("Illustrator pattern fills are drawn as solid grey"); p.PopPattern(); p.AiFill(0.5f, 0.5f, 0.5f); });
            AiDef("P", p => { p.Warn("Illustrator pattern strokes are drawn as solid grey"); p.PopPattern(); p.AiStroke(0.5f, 0.5f, 0.5f); });
            // ---- gradients
            AiDef("Bd", p =>
            {
                p.PopN(1); int type = (int)p.FOr(0); string name = p.St.Count > 0 && p.Pop() is PsString s ? s.S : "";
                var def = new AiGradientDef { Radial = type == 1 }; p.ReadGradientBody(def); p.aiGradients[name] = def;
            });
            AiDef("BD", p => { });
            AiDef("Bs", p => p.St.Clear()); aidict.D["Br"] = aidict.D["Bs"];   // only reached in unusual layouts; the real stops are read by Bd
            AiDef("Bb", p => { p.aiGradIdx = (int)p.FOr(0); p.aiBg = null; p.aiBm = null; p.aiBh = null; });
            AiDef("Bg", p => p.ReadBg());
            AiDef("Bh", p => { float len = p.F(), ang = p.F(), yh = p.F(), xh = p.F(); p.aiBh = (xh, yh, ang, len); });
            AiDef("Bm", p => { float ty = p.F(), tx = p.F(), d = p.F(), c = p.F(), b = p.F(), a = p.F(); p.aiBm = new Matrix3x2(a, b, c, d, tx, ty); });
            AiDef("Bc", p => p.PopN(6)); AiDef("Xm", p => p.PopN(6));
            AiDef("BB", p => p.PopN(1));
            // ---- text objects: skipped up to the matching TO
            AiDef("To", p => { p.Warn("Illustrator text objects are skipped"); p.PopN(1); string? l; while ((l = p.sc.ReadLine()) != null) if (l.Trim() == "TO") break; });
            AiDef("z", p => { p.Warn("Illustrator text objects are skipped"); p.PopNumbers(4); p.PopN(1); });   // AI3 text: font size leading kerning align z ... T
            AiDef("e", p => p.PopN(1)); AiDef("t", p => p.PopN(1)); AiDef("T", p => { }); AiDef("a", p => p.PopN(1)); AiDef("Tx", p => p.PopN(1)); AiDef("TX", p => p.PopN(1));
        }

        // ------------------------------------------------------------------ colour helpers
        void AiFill(float r, float g, float b) { SetColor(r, g, b); aiFillGrad = null; if (aiGradIdx == 0) aiBg = null; }
        void AiStroke(float r, float g, float b) { gs.SR = r; gs.SG = g; gs.SB = b; aiStrokeGrad = null; if (aiGradIdx == 1) aiBg = null; }
        (float r, float g, float b) PopCmyk() { float k = F(), y = F(), m = F(), c = F(); return CmykToRgb(c, m, y, k); }
        static (float r, float g, float b) CmykToRgb(float c, float m, float y, float k) => ((1 - Math.Clamp(c, 0, 1)) * (1 - Math.Clamp(k, 0, 1)), (1 - Math.Clamp(m, 0, 1)) * (1 - Math.Clamp(k, 0, 1)), (1 - Math.Clamp(y, 0, 1)) * (1 - Math.Clamp(k, 0, 1)));
        /// <summary>"c m y k r g b Xa" or "r g b Xa": the RGB triple wins.</summary>
        (float r, float g, float b) PopXa()
        {
            float b = F(), g = F(), r = F();
            if (St.Count >= 4 && St[^1] is double && St[^2] is double && St[^3] is double && St[^4] is double) PopN(4);
            return (r, g, b);
        }
        /// <summary>x / X: "c m y k (name) tint"; Xx / XX: "comps (name) tint type" (type 1 = 3 RGB comps, 0 = 4 CMYK comps). tint operand 0 = full colour.</summary>
        (float r, float g, float b) PopCustom(bool generic)
        {
            int type = generic ? (int)FOr(0) : 0; float tint = FOr(0); if (St.Count > 0 && St[^1] is PsString) Pop();
            float r, g, b;
            if (type == 1) { b = F(); g = F(); r = F(); }
            else { (r, g, b) = PopCmyk(); }
            return Tint(r, g, b, tint);
        }
        /// <summary>Xk / XK: "c m y k [r g b] (name) tint type".</summary>
        (float r, float g, float b) PopXk()
        {
            int type = (int)FOr(0); float tint = FOr(0); if (St.Count > 0 && St[^1] is PsString) Pop();
            float r, g, b;
            if (type == 1) { b = F(); g = F(); r = F(); PopNumbers(4); }
            else (r, g, b) = PopCmyk();
            return Tint(r, g, b, tint);
        }
        static (float r, float g, float b) Tint(float r, float g, float b, float tintOperand)
        {   // the file stores 1 - tint: 0 = full colour, 1 = white
            float t = 1 - Math.Clamp(tintOperand, 0, 1);
            return (1 - t * (1 - r), 1 - t * (1 - g), 1 - t * (1 - b));
        }
        void PopPattern()
        {   // (name) px py sx sy angle rf r k ka [a b c d tx ty] p
            if (St.Count > 0 && St[^1] is PsArray) Pop(); PopNumbers(9); if (St.Count > 0 && St[^1] is PsString) Pop();
        }

        // ------------------------------------------------------------------ painting
        void AiClose() { if (gs.HasCurrent) { gs.Path.Close(); var s = gs.Path.SubPathStart; gs.DevCx = s.X; gs.DevCy = s.Y; } }
        void AiPaint(int kind, bool close)
        {
            if (close) AiClose();
            if (aiCompound > 0) { aiDeferredPaint |= kind; return; }   // compound path: paint everything once at *U
            AiDoPaint(kind);
        }
        void AiDoPaint(int kind)
        {
            var path = gs.Path;
            aiLastArt = AiArt.Shape; aiLastArtStart = img.Shapes.Count;
            if (!path.IsEmpty && !AiHidden)
            {
                bool eo = gs.AiEvenOdd;
                if (aiBg != null) BuildPendingGradient(path);
                if ((kind & 1) != 0)
                {
                    var p = path.Clone(); p.Close();
                    VectorPaint paint = aiFillGrad != null ? WithAlpha(aiFillGrad) : new VectorColor(Argb(gs.R, gs.G, gs.B));
                    img.Shapes.Add(new VectorShape { Path = p, Fill = paint, EvenOdd = eo, Clip = gs.Clip });
                }
                if ((kind & 2) != 0)
                {
                    float r = gs.R, g = gs.G, b = gs.B; gs.R = gs.SR; gs.G = gs.SG; gs.B = gs.SB;
                    gs.Path = path; Stroke(); gs.R = r; gs.G = g; gs.B = b;
                    if (aiStrokeGrad != null && img.Shapes.Count > 0 && img.Shapes[^1].Stroke != null) img.Shapes[^1].Stroke = WithAlpha(aiStrokeGrad);
                }
                if (aiClipPending) { gs.Path = path; Clip(eo); }
            }
            aiClipPending = false; aiLastArtEnd = img.Shapes.Count;
            NewPath();
        }
        VectorPaint WithAlpha(VectorGradient g)
        {
            if (gs.Alpha >= 1) return g;
            var c = (VectorGradient)g.Clone();
            for (int i = 0; i < c.Stops.Count; i++) { int a = (int)MathF.Round(((c.Stops[i].Argb >> 24) & 255) * gs.Alpha); c.Stops[i] = new VectorStop(c.Stops[i].Offset, (c.Stops[i].Argb & 0xFFFFFF) | (a << 24)); }
            return c;
        }

        // ------------------------------------------------------------------ gradients
        /// <summary>Reads the stops of a gradient definition: lines up to BD, "%_c m y k r g b style mid ramp Bs" (AI8+) or "... %_BS / %_Bs" fallbacks.</summary>
        void ReadGradientBody(AiGradientDef def)
        {
            var alt = new List<VectorStop>(); string? line;
            while ((line = sc.ReadLine()) != null)
            {
                string l = line.Trim();
                if (l == "BD" || l.StartsWith("%AI5_EndGradient", StringComparison.Ordinal)) break;
                if (l.StartsWith("%_", StringComparison.Ordinal) && l.EndsWith(" Bs", StringComparison.Ordinal)) { var s = ParseStop(l.Substring(2, l.Length - 5)); if (s.HasValue) def.Stops.Add(s.Value); }
                else if (l.EndsWith("%_BS", StringComparison.Ordinal) || l.EndsWith("%_Bs", StringComparison.Ordinal)) { var s = ParseStop(l.Substring(0, l.Length - 4)); if (s.HasValue) alt.Add(s.Value); }
            }
            if (def.Stops.Count == 0) def.Stops = alt;
            def.Stops.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }
        static List<string> Tokens(string s)
        {
            var l = new List<string>(); int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(') { int depth = 1, st = i++; while (i < s.Length && depth > 0) { if (s[i] == '\\') i++; else if (s[i] == '(') depth++; else if (s[i] == ')') depth--; i++; } l.Add(s.Substring(st, i - st)); continue; }
                int b = i; while (i < s.Length && !char.IsWhiteSpace(s[i])) i++; l.Add(s.Substring(b, i - b));
            }
            return l;
        }
        /// <summary>colorSpec colorStyle midpoint rampPoint -> stop. Styles: 0 gray, 1 cmyk, 2 cmyk+rgb, 3 custom cmyk, 4 custom rgb, 5 named (type 0 cmyk / 1 rgb), 6 = sub-style + opacity.</summary>
        static VectorStop? ParseStop(string text)
        {
            var t = Tokens(text); if (t.Count < 4) return null;
            float N(int fromEnd) => fromEnd <= t.Count && float.TryParse(t[t.Count - fromEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            int style = (int)N(3); float ramp = N(1) / 100f, alpha = 1; int off = 0;
            if (style == 6) { alpha = N(4); style = (int)N(5); off = 2; }
            float r, g, b; float tint = 0;
            switch (style)
            {
                case 0: r = g = b = N(4 + off); break;
                case 1: (r, g, b) = CmykToRgb(N(7 + off), N(6 + off), N(5 + off), N(4 + off)); break;
                case 2: r = N(6 + off); g = N(5 + off); b = N(4 + off); break;
                case 3: (r, g, b) = CmykToRgb(N(9 + off), N(8 + off), N(7 + off), N(6 + off)); tint = N(4 + off); break;
                case 4: r = N(9 + off); g = N(8 + off); b = N(7 + off); tint = N(5 + off); break;
                case 5:
                    if ((int)N(4 + off) == 1) { r = N(9 + off); g = N(8 + off); b = N(7 + off); }
                    else (r, g, b) = CmykToRgb(N(10 + off), N(9 + off), N(8 + off), N(7 + off));
                    tint = N(5 + off); break;
                default: return null;
            }
            if (tint > 0) (r, g, b) = Tint(r, g, b, tint);
            int argb = (Math.Clamp((int)MathF.Round(alpha * 255), 0, 255) << 24) | Math.Clamp((int)MathF.Round(r * 255), 0, 255) << 16 | Math.Clamp((int)MathF.Round(g * 255), 0, 255) << 8 | Math.Clamp((int)MathF.Round(b * 255), 0, 255);
            return new VectorStop(Math.Clamp(ramp, 0, 1), argb);
        }
        /// <summary>"[flag] (name) xOrigin yOrigin angle length a b c d tx ty [aspect] Bg".</summary>
        void ReadBg()
        {
            var nums = new List<float>(); while (St.Count > 0 && St[^1] is double d) { nums.Add((float)d); Pop(); }
            nums.Reverse();
            string name = St.Count > 0 && St[^1] is PsString ? ((PsString)Pop()!).S : "";
            bool flag = St.Count > 0 && St[^1] is double fd && (fd == 0 || fd == 1 || fd == 2); if (flag) Pop();
            var bg = new AiBg { Name = name, HasFlag = flag };
            if (nums.Count >= 10) { bg.XO = nums[0]; bg.YO = nums[1]; bg.Angle = nums[2]; bg.Len = nums[3]; bg.A = nums[4]; bg.B = nums[5]; bg.C = nums[6]; bg.D = nums[7]; bg.Tx = nums[8]; bg.Ty = nums[9]; if (nums.Count >= 11 && nums[10] > 0) bg.Aspect = nums[10]; }
            else if (nums.Count >= 4) { bg.XO = nums[0]; bg.YO = nums[1]; bg.Angle = nums[2]; bg.Len = nums[3]; }
            aiBg = bg;
        }
        /// <summary>Turns the pending Bg / Bm / Bh instance into a gradient for the given (device-space) path. Bm (Illustrator's own matrix) is exact; without it the geometry is rebuilt from Bg.</summary>
        void BuildPendingGradient(VectorPath devPath)
        {
            var bg = aiBg!; aiBg = null;
            if (!aiGradients.TryGetValue(bg.Name, out var def) || def.Stops.Count == 0) { Warn($"gradient '{bg.Name}' is not defined in this file: drawn with the current colour"); return; }
            var g = new VectorGradient { Radial = def.Radial, ObjectBoundingBox = false, Spread = VectorSpread.Pad };
            g.Stops.AddRange(def.Stops);
            Matrix3x2 unitToUser;                                   // gradient space: linear along x 0..1, radial = unit circle at the origin
            Matrix3x2.Invert(gs.Ctm, out var invCtm);
            var user = devPath.Transformed(invCtm);                 // user (ruler) space, y up
            var objT = new Matrix3x2(bg.A, bg.B, bg.C, bg.D, 0, 0);  // transform applied to the object after the gradient was set
            if (!Matrix3x2.Invert(objT, out var invObjT)) { invObjT = Matrix3x2.Identity; objT = Matrix3x2.Identity; }
            var bb = user.Transformed(invObjT).ControlBounds(); float w = Math.Max(bb.Width, 1e-6f), h = Math.Max(bb.Height, 1e-6f);
            float RadialScale(float x, float y) => MathF.Sqrt(w * w * (x - .5f) * (x - .5f) + h * h * (y - .5f) * (y - .5f)) * MathF.Sqrt(.5f);
            if (aiBm is Matrix3x2 bm && Math.Abs(bm.GetDeterminant()) > 1e-12f)
            {
                unitToUser = bm;
                if (def.Radial) { g.Cx = 0; g.Cy = 0; g.R = 1; g.Fx = 0; g.Fy = 0; }
                else { g.X1 = 0; g.Y1 = 0; g.X2 = 1; g.Y2 = 0; }
                if (def.Radial && aiBh is (float xh, float yh, _, _) && (xh != 0 || yh != 0) && Matrix3x2.Invert(bm, out var invBm))
                {   // highlight = user-space offset from the centre
                    var f = Vector2.Transform(new Vector2(bm.M31 + xh, bm.M32 + yh), invBm); g.Fx = f.X; g.Fy = f.Y;
                }
            }
            else if (bg.HasFlag)
            {   // CS4+ relative geometry (Inkscape extension-ai): the shape's box (with objT removed) scales the parameters
                float ang = bg.Angle * MathF.PI / 180;
                if (!def.Radial)
                {
                    var rot = Matrix3x2.CreateRotation(ang); Matrix3x2.Invert(rot, out var invRot);
                    var rb = user.Transformed(invObjT).Transformed(invRot).ControlBounds();
                    float maxLen = Math.Max(rb.Width, 1e-6f); float cx = rb.Left + rb.Width / 2, cy = rb.Top + rb.Height / 2;
                    g.X1 = cx - maxLen / 2 + bg.XO * maxLen; g.Y1 = cy; g.X2 = g.X1 + bg.Len * maxLen; g.Y2 = cy;
                    unitToUser = rot * objT;
                }
                else
                {
                    float r = bg.Len * RadialScale(bg.XO, bg.YO);
                    var o = new Vector2(bb.Left + bb.Width / 2 + bg.XO * w, bb.Top + bb.Height / 2 + bg.YO * h);   // origin in the un-transformed object space
                    var t = Matrix3x2.CreateScale(1, bg.Aspect) * Matrix3x2.CreateRotation(ang);
                    Matrix3x2.Invert(t, out var invT); var og = Vector2.Transform(o, invT);
                    g.Cx = og.X; g.Cy = og.Y; g.R = r; g.Fx = og.X; g.Fy = og.Y;
                    if (aiBh is (_, _, float fa, float fl) && fl != 0) { float fs = RadialScale(0, 0); g.Fx += fl * fs * MathF.Cos(fa * MathF.PI / 180); g.Fy += fl * fs * MathF.Sin(fa * MathF.PI / 180); }
                    unitToUser = t * objT;
                }
            }
            else
            {   // AI5 - CS3: absolute origin (user space), length in points, angle in degrees
                float ang = bg.Angle * MathF.PI / 180;
                if (!def.Radial) { g.X1 = bg.XO; g.Y1 = bg.YO; g.X2 = bg.XO + bg.Len * MathF.Cos(ang); g.Y2 = bg.YO + bg.Len * MathF.Sin(ang); }
                else { g.Cx = bg.XO; g.Cy = bg.YO; g.R = Math.Max(bg.Len, 1e-3f); g.Fx = bg.XO; g.Fy = bg.YO; if (aiBh is (float xh, float yh, _, _)) { g.Fx += xh; g.Fy += yh; } }
                unitToUser = new Matrix3x2(bg.A, bg.B, bg.C, bg.D, bg.Tx, bg.Ty);
                if (Math.Abs(unitToUser.GetDeterminant()) < 1e-12f) unitToUser = Matrix3x2.Identity;
            }
            g.Matrix = unitToUser * gs.Ctm;
            if (aiGradIdx == 1) aiStrokeGrad = g; else aiFillGrad = g;
        }
    }
}
