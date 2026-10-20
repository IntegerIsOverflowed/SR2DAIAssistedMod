// VectorTextChecks.cs - stroke text (VectorText.cs): glyph metrics, bold / italic, Cyrillic, text along a path, fit.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

static class VectorTextChecks
{
    static void Chk(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) Environment.Exit(1); }

    // every point of a path (move / line / cubic end points)
    static List<PointF> Points(Sr2d64CSport.VectorPath p)
    {
        var r = new List<PointF>();
        int di = 0;
        foreach (var k in p.Kinds)
        {
            switch (k)
            {
                case Sr2d64CSport.VectorPath.K.Move:
                case Sr2d64CSport.VectorPath.K.Line: r.Add(new PointF(p.Data[di], p.Data[di + 1])); di += 2; break;
                case Sr2d64CSport.VectorPath.K.Cubic: r.Add(new PointF(p.Data[di + 4], p.Data[di + 5])); di += 6; break;
            }
        }
        return r;
    }

    static List<PointF> AllPoints(Sr2d64CSport.VectorImage img) => img.Shapes.SelectMany(s => Points(s.Path)).ToList();

    static void SaveText(string text, string name)
    {
        string outDir = Environment.GetEnvironmentVariable("CTLRUN_OUT") ?? Path.Combine(Path.GetTempPath(), "ctlrun");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, name), text, new System.Text.UTF8Encoding(false));
    }

    static void SaveSprite(Sr2d64CSport.Sprite s, string name)
    {
        string outDir = Environment.GetEnvironmentVariable("CTLRUN_OUT") ?? Path.Combine(Path.GetTempPath(), "ctlrun");
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, name);
        File.WriteAllBytes(file, Sr2d64CSport.Png.Encode(s.Pixels, s.Width, s.Height));
        Console.WriteLine($"wrote {file} {s.Width}x{s.Height}");
    }

    static (int w, int h) InkBox(Sr2d64CSport.Sprite sp)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < sp.Height; y++)
            for (int x = 0; x < sp.Width; x++)
            {
                int px = sp.Pixels[y * sp.Width + x];
                if (((px >> 16) & 255) < 128 && ((px >> 8) & 255) < 128 && (px & 255) < 128)
                { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            }
        return x1 < 0 ? (0, 0) : (x1 - x0 + 1, y1 - y0 + 1);
    }

    static int Segments(Sr2d64CSport.VectorImage img) => img.Shapes.Sum(s => s.Path.SegmentCount);

    // dots (. : i ё) are squares of the stroke width; text can be centred on a line; QGIS-style simplification
    public static void DotsAndSimplify()
    {
        // a period at size 70: the stroke is 6 px (regular) and 11.2 px (Bold), so the dot is that square
        foreach (var (bold, want) in new[] { (false, 6.0), (true, 11.2) })
        {
            var sp = new Sr2d64CSport.Sprite(120, 120);
            sp.ClearBuffer(unchecked((int)0xFFFFFFFF));
            Sr2d64CSport.VectorText.Create(".", 70f, new Sr2d64CSport.VectorTextStyle { Bold = bold }).Draw(sp, 20, 20);
            var (w, h) = InkBox(sp);
            Chk(Math.Abs(w - want) <= 1.5 && Math.Abs(h - want) <= 1.5 && Math.Abs(w - h) <= 1,
                $"a {(bold ? "Bold " : "")}period is a {w}x{h} px square (stroke {want:0.0} px)");
        }
        {
            var sp = new Sr2d64CSport.Sprite(120, 120);
            sp.ClearBuffer(unchecked((int)0xFFFFFFFF));
            Sr2d64CSport.VectorText.Create(":", 70f, new Sr2d64CSport.VectorTextStyle()).Draw(sp, 20, 20);
            var (w, _) = InkBox(sp);
            Chk(Math.Abs(w - 6.0) <= 1.5, $"a colon's dots are as wide as the stroke ({w} px)");
        }
        // Centred: the middle of the capitals sits on the line
        var line = new List<PointF> { new PointF(0, 100), new PointF(200, 100) };
        var centred = Sr2d64CSport.VectorText.OnPath("HHH", line, 20f, new Sr2d64CSport.VectorTextPathOptions { Centred = true });
        var cp = AllPoints(centred);
        float mid = (cp.Min(p => p.Y) + cp.Max(p => p.Y)) / 2f;
        Chk(Math.Abs(mid - 100f) < 0.5f, $"a centred label's middle sits on the line (middle y {mid:0.0}, line 100)");
        var above = Sr2d64CSport.VectorText.OnPath("HHH", line, 20f, new Sr2d64CSport.VectorTextPathOptions());
        var ap = AllPoints(above);
        float amid = (ap.Min(p => p.Y) + ap.Max(p => p.Y)) / 2f;
        Chk(amid < 100f - 5f, $"an 'Above' label sits over the line (middle y {amid:0.0})");

        // Linear (Douglas-Peucker): a noisy straight line keeps its ends; a corner stays
        var noisy = Enumerable.Range(0, 100).Select(i => new PointF(i, 0.2f * (float)Math.Sin(i))).ToList();
        var r1 = Sr2d64CSport.VectorSimplify.Line(noisy, false, new Sr2d64CSport.SimplifySettings { Tolerance = 1 });
        Chk(r1.Count == 2, $"Linear reduces a noisy straight line to its ends ({r1.Count} of 100 points)");
        var corner = new List<PointF>();
        for (int i = 0; i <= 10; i++) corner.Add(new PointF(i, 0));
        for (int i = 1; i <= 10; i++) corner.Add(new PointF(10, i));
        var r2 = Sr2d64CSport.VectorSimplify.Line(corner, false, new Sr2d64CSport.SimplifySettings { Tolerance = 0.5 });
        Chk(r2.Count == 3 && r2[1] == new PointF(10, 0), $"Linear keeps a corner ({r2.Count} points)");
        // a ring of a square with midpoints: the four corners are kept
        var ring = new List<PointF> { new PointF(0, 0), new PointF(5, 0), new PointF(10, 0), new PointF(10, 5), new PointF(10, 10), new PointF(5, 10), new PointF(0, 10), new PointF(0, 5) };
        var r5 = Sr2d64CSport.VectorSimplify.Line(ring, true, new Sr2d64CSport.SimplifySettings { Tolerance = 0.1 });
        Chk(r5.Count == 4, $"Linear on a closed ring keeps its four corners ({r5.Count} points)");
        // SnapToGrid with a 5-unit grid
        var snap = new List<PointF> { new PointF(0, 0), new PointF(1, 1), new PointF(12, 11), new PointF(14, 14) };
        var r3 = Sr2d64CSport.VectorSimplify.Line(snap, false, new Sr2d64CSport.SimplifySettings { Tolerance = 5, Algorithm = Sr2d64CSport.SimplifyAlgorithm.SnapToGrid });
        Chk(r3.Count == 3 && r3[1] == new PointF(10, 10) && r3[2] == new PointF(15, 15), $"Snap To Grid moves points to the grid and drops repeats ({r3.Count} points)");
        // Visvalingam: the point whose triangle is 0.5 square units is removed when the threshold is 1
        var vis = new List<PointF> { new PointF(0, 0), new PointF(5, 0.1f), new PointF(10, 0), new PointF(10, 10) };
        var r4 = Sr2d64CSport.VectorSimplify.Line(vis, false, new Sr2d64CSport.SimplifySettings { Tolerance = 1, Algorithm = Sr2d64CSport.SimplifyAlgorithm.Visvalingam });
        Chk(r4.Count == 3 && r4[1] == new PointF(10, 0), $"Visvalingam removes the smallest triangle first ({r4.Count} points)");
        // a feature that collapses keeps its geometry
        var tiny = new List<PointF> { new PointF(0, 0), new PointF(1, 0), new PointF(0, 1) };
        var r6 = Sr2d64CSport.VectorSimplify.Line(tiny, true, new Sr2d64CSport.SimplifySettings { Tolerance = 100, Algorithm = Sr2d64CSport.SimplifyAlgorithm.SnapToGrid });
        Chk(r6.Count == 3, "a feature that collapses under the threshold keeps its geometry");
        // a shape: 50 collinear points become one segment
        var path = new Sr2d64CSport.VectorPath();
        path.MoveTo(0, 0);
        for (int i = 1; i <= 50; i++) path.LineTo(i, 0);
        var shape = new Sr2d64CSport.VectorShape(path, null, null, 1f);
        Sr2d64CSport.VectorSimplify.Apply(shape, new Sr2d64CSport.SimplifySettings { Tolerance = 0.5 });
        Chk(shape.Path.SegmentCount == 2, $"a shape of 50 collinear points keeps {shape.Path.SegmentCount} segments");
    }

    public static void Run()
    {
        Console.WriteLine("VectorText (stroke font, no font files):");

        // glyph metrics: cap height is the size
        var h = Sr2d64CSport.VectorText.Create("H", 10f);
        var hp = AllPoints(h);
        float capH = hp.Max(p => p.Y) - hp.Min(p => p.Y);
        Chk(Math.Abs(capH - 10f) < 0.5f, $"capital H is 10 units tall at size 10 (got {capH:0.00})");

        // bold = a wider stroke, italic = a skew (top shifts right by 0.2 x height)
        var reg = Sr2d64CSport.VectorText.Create("I", 10f);
        var bold = Sr2d64CSport.VectorText.Create("I", 10f, new Sr2d64CSport.VectorTextStyle { Bold = true });
        Chk(bold.Shapes[0].StrokeWidth > reg.Shapes[0].StrokeWidth * 1.8f, $"bold stroke {bold.Shapes[0].StrokeWidth:0.00} > 1.8 x regular {reg.Shapes[0].StrokeWidth:0.00}");
        var ital = Sr2d64CSport.VectorText.Create("I", 10f, new Sr2d64CSport.VectorTextStyle { Italic = true });
        var ip = AllPoints(ital);
        float top = ip.Where(p => p.Y < -9.5f).Average(p => p.X), bottom = ip.Where(p => p.Y > -0.5f).Average(p => p.X);
        Chk(top - bottom > 1.5f, $"italic: the top leans right of the foot ({top - bottom:0.00} units over a 10-unit height)");

        // glyph coverage: Latin, digits, Cyrillic (incl. ё / Ж), no glyph for unsupported characters
        Chk(new[] { 'A', 'z', '7', '.', 'Ж', 'щ', 'ё', 'Ё', 'ъ', 'Я' }.All(Sr2d64CSport.VectorText.HasGlyph), "glyphs for Latin, digits and Cyrillic (incl. Ж, щ, ё, Ё, ъ, Я)");
        Chk(!Sr2d64CSport.VectorText.HasGlyph('€') && !Sr2d64CSport.VectorText.HasGlyph('中'), "no glyph for € or CJK (drawn as a gap)");
        float w1 = Sr2d64CSport.VectorText.Width("A", 10f), w2 = Sr2d64CSport.VectorText.Width("AB", 10f);
        Chk(w2 > w1 && w1 > 0, $"width grows with the text (A {w1:0.0}, AB {w2:0.0})");
        Chk(Sr2d64CSport.VectorText.Create("привет, Челябинск", 10f).Shapes.Count == 1, "a Cyrillic string makes one stroke shape");

        // text along a straight path: letters sit above the line (y smaller on screen), inside its length
        var line = new List<PointF> { new PointF(0, 100), new PointF(400, 100) };
        var onLine = Sr2d64CSport.VectorText.OnPath("HELLO", line, 10f);
        var lp = AllPoints(onLine);
        Chk(lp.Min(p => p.Y) >= 89f && lp.Max(p => p.Y) <= 101.5f, $"straight path: letters sit on the line (y {lp.Min(p => p.Y):0.0}..{lp.Max(p => p.Y):0.0})");
        Chk(lp.Min(p => p.X) >= -0.5f && lp.Max(p => p.X) <= 400f, "straight path: text stays within the path");

        // text along a semicircle over the top (left to right): letters stand on the arc, outward from its centre
        var arc = new List<PointF>();
        for (int i = 0; i <= 64; i++) { double a = Math.PI - Math.PI * i / 64.0; arc.Add(new PointF(200 + 100 * (float)Math.Cos(a), 200 - 100 * (float)Math.Sin(a))); }
        var onArc = Sr2d64CSport.VectorText.OnPath("CIRCLE", arc, 12f);
        var ap = AllPoints(onArc);
        float rmin = ap.Min(p => Dist(p, 200, 200)), rmax = ap.Max(p => Dist(p, 200, 200));
        Chk(rmin > 98.5f && rmax < 100 + 12 + 2, $"curved path: letters follow the arc, radius {rmin:0.0}..{rmax:0.0} (arc radius 100, height 12)");

        // fit to path: the text spans the whole path, from its start to its end
        var fit = Sr2d64CSport.VectorText.OnPath("AB", new List<PointF> { new PointF(0, 50), new PointF(200, 50) }, 10f,
            new Sr2d64CSport.VectorTextPathOptions { FitToPath = true });
        var fp = AllPoints(fit);
        Chk(fp.Min(p => p.X) < 2f && fp.Max(p => p.X) > 196f && fp.Max(p => p.X) < 201f, $"fit to path: spans 0..200 (got {fp.Min(p => p.X):0.0}..{fp.Max(p => p.X):0.0})");

        // a below-the-line variant sits under the line
        var under = Sr2d64CSport.VectorText.OnPath("ABC", line, 10f, new Sr2d64CSport.VectorTextPathOptions { Above = false });
        var up = AllPoints(under);
        Chk(up.Min(p => p.Y) >= 98.5f && up.Max(p => p.Y) <= 111f, "below the path: letters hang under the line");

        // below the path the glyphs stay upright: an H at size 70 on y = 100 spans y 100..170 (top on the line)
        var hangLine = new List<PointF> { new PointF(0, 100), new PointF(300, 100) };
        var hang = AllPoints(Sr2d64CSport.VectorText.OnPath("H", hangLine, 70f, new Sr2d64CSport.VectorTextPathOptions { Above = false }));
        Chk(MathF.Abs(hang.Min(p => p.Y) - 100f) < 0.01f && MathF.Abs(hang.Max(p => p.Y) - 170f) < 0.01f, $"below the path: glyphs stay upright (H spans y {hang.Min(p => p.Y):0.0}..{hang.Max(p => p.Y):0.0})");

        // rounding: radius 12 adds curve points to the angular glyph; radius 0 keeps the vertices only
        var angularO = AllPoints(Sr2d64CSport.VectorText.Create("O", 70f, new Sr2d64CSport.VectorTextStyle { Radius = 0f }));
        var roundO = AllPoints(Sr2d64CSport.VectorText.Create("O", 70f, new Sr2d64CSport.VectorTextStyle { Radius = 12f }));
        Chk(roundO.Count > angularO.Count + 20, $"radius 12 rounds the O's corners ({angularO.Count} -> {roundO.Count} points)");

        // tip limit: the apex of A is exactly at cap height with TipDrop 0, and lowered by at most TipDrop with 3
        float apexSharp = AllPoints(Sr2d64CSport.VectorText.Create("A", 70f, new Sr2d64CSport.VectorTextStyle { Radius = 12f, TipDrop = 0f })).Min(p => p.Y);
        float apexDrop = AllPoints(Sr2d64CSport.VectorText.Create("A", 70f, new Sr2d64CSport.VectorTextStyle { Radius = 12f, TipDrop = 3f })).Min(p => p.Y);
        Chk(MathF.Abs(apexSharp + 70f) < 0.01f, $"tip drop 0: A keeps its apex at cap height (y {apexSharp:0.00})");
        Chk(apexDrop > -70f + 0.5f && apexDrop <= -66.9f, $"tip drop 3: A's apex is lowered by 3 units at most (y {apexDrop:0.00})");

        // monospace: W is squeezed to 46 units, I is centred in a 60-unit advance
        float wMono = Sr2d64CSport.VectorText.Width("W", 70f, new Sr2d64CSport.VectorTextStyle { Monospace = true });
        float wProp = Sr2d64CSport.VectorText.Width("W", 70f);
        Chk(MathF.Abs(wMono - 46f) < 0.01f && MathF.Abs(wProp - 56f) < 0.01f, $"monospace squeezes W to 46 (proportional {wProp:0.0})");

        // defaults follow the font file: bevel joins, butt caps, outline on the path
        var dflt = new Sr2d64CSport.VectorTextStyle();
        Chk(dflt.Join == Sr2d64CSport.VectorJoin.Bevel && dflt.Cap == Sr2d64CSport.VectorCap.Butt && dflt.OutlinePosition == Sr2d64CSport.VectorOutlinePosition.Center,
            "defaults: bevel joins, butt caps, outline centred");
        Chk(Sr2d64CSport.VectorText.Create("A", 70f).Shapes[0].Join == Sr2d64CSport.VectorJoin.Bevel, "the text shape carries its join (bevel)");

        // joins: an angular A (radius 0) with a miter has a spike at the apex that a bevel does not
        // first and last row with dark ink (the picture is drawn at a fixed place, so the span is what matters)
        (int Top, int Bottom) InkRows(Sr2d64CSport.VectorImage img)
        {
            var sp = new Sr2d64CSport.Sprite(200, 200); sp.ClearBuffer(unchecked((int)0xFFFFFFFF));
            img.Draw(sp, 50, 20);            // Draw puts the picture's top-left corner here; the glyph is ~80 px tall
            bool Dark(int y) { for (int x = 0; x < sp.Width; x++) { int px = sp.Pixels[y * sp.Width + x]; if (((px >> 16) & 255) < 128 && ((px >> 8) & 255) < 128 && (px & 255) < 128) return true; } return false; }
            int top = -1, bottom = -1;
            for (int y = 0; y < sp.Height; y++) if (Dark(y)) { top = y; break; }
            for (int y = sp.Height - 1; y >= 0; y--) if (Dark(y)) { bottom = y; break; }
            return (top, bottom);
        }
        var aBevel = new Sr2d64CSport.VectorTextStyle { Radius = 0f, Join = Sr2d64CSport.VectorJoin.Bevel };
        var aMiter = new Sr2d64CSport.VectorTextStyle { Radius = 0f, Join = Sr2d64CSport.VectorJoin.Miter };
        var (topBevel, botBevel) = InkRows(Sr2d64CSport.VectorText.Create("A", 70f, aBevel));
        var (topMiter, botMiter) = InkRows(Sr2d64CSport.VectorText.Create("A", 70f, aMiter));
        int spanBevel = botBevel - topBevel, spanMiter = botMiter - topMiter;
        Chk(spanBevel > 0 && spanMiter >= spanBevel + 5, $"A radius 0: the miter apex is {spanMiter - spanBevel} px taller than the bevel apex ({spanMiter} vs {spanBevel} px)");

        // caps: a Square cap on the open L/I ends reaches half the stroke past the end
        var (topButt, botButt) = InkRows(Sr2d64CSport.VectorText.Create("l", 70f, new Sr2d64CSport.VectorTextStyle { Radius = 0f, Cap = Sr2d64CSport.VectorCap.Butt }));
        var (topSquare, botSquare) = InkRows(Sr2d64CSport.VectorText.Create("l", 70f, new Sr2d64CSport.VectorTextStyle { Radius = 0f, Cap = Sr2d64CSport.VectorCap.Square }));
        int spanButt = botButt - topButt, spanSquare = botSquare - topSquare;
        Chk(spanButt > 0 && spanSquare >= spanButt + 4, $"square cap on l: the stroke is {spanSquare - spanButt} px longer than with a butt cap (both ends)");

        // outline position on the closed O (radius 0; the contour spans x 5..45, stroke 6):
        // (1) the nodes never move: every position has the centre contour's nodes
        // (2) the ink band lies on its side of the contour, measured on a rendered row through the middle of the O:
        //     centre  2..8 and 42..48 (3 either side), inside 5..11 and 39..45, outside -1..5 and 45..51
        var centreNodes = AllPoints(Sr2d64CSport.VectorText.Create("O", 70f, new Sr2d64CSport.VectorTextStyle { Radius = 0f }));
        foreach (var (pos, l0, l1, r0, r1) in new[] {
            (Sr2d64CSport.VectorOutlinePosition.Center, 2f, 8f, 42f, 48f),
            (Sr2d64CSport.VectorOutlinePosition.Inside, 5f, 11f, 39f, 45f),
            (Sr2d64CSport.VectorOutlinePosition.Outside, -1f, 5f, 45f, 51f) })
        {
            var pic = Sr2d64CSport.VectorText.Create("O", 70f, new Sr2d64CSport.VectorTextStyle { Radius = 0f, OutlinePosition = pos });
            var nodes = AllPoints(pic);
            Chk(nodes.Count == centreNodes.Count && nodes.Zip(centreNodes, (a, b) => MathF.Abs(a.X - b.X) + MathF.Abs(a.Y - b.Y)).Max() < 0.001f,
                $"outline {pos}: the O's nodes are the centre contour's nodes (curve not moved; {nodes.Count} points)");

            var oc = new Sr2d64CSport.Sprite(120, 100);
            oc.ClearBuffer(unchecked((int)0xFFFFFFFF));
            pic.Draw(oc, System.Numerics.Matrix3x2.CreateTranslation(10 - pic.ViewBox.X, 10 - pic.ViewBox.Y));
            float rowY = (nodes.Min(p => p.Y) + nodes.Max(p => p.Y)) / 2 + 10 - pic.ViewBox.Y;
            int row = (int)MathF.Round(rowY), W = oc.Width;
            float Cov(int x) => (255 - (oc.Pixels[row * W + x] & 0xff)) / 255f;
            int mid = (int)MathF.Round((nodes.Min(p => p.X) + nodes.Max(p => p.X)) / 2 + 10 - pic.ViewBox.X);
            int a0 = -1, a1 = -1, b0 = -1, b1 = -1;
            for (int x = 0; x < mid; x++) if (Cov(x) > 0.5f) { if (a0 < 0) a0 = x; a1 = x + 1; }
            for (int x = mid; x < W; x++) if (Cov(x) > 0.5f) { if (b0 < 0) b0 = x; b1 = x + 1; }
            float Exp(float v) => v + 10 - pic.ViewBox.X;
            bool okL = a0 >= 0 && MathF.Abs(a0 - Exp(l0)) <= 1f && MathF.Abs(a1 - Exp(l1)) <= 1f;
            bool okR = b0 >= 0 && MathF.Abs(b0 - Exp(r0)) <= 1f && MathF.Abs(b1 - Exp(r1)) <= 1f;
            Chk(okL && okR, $"outline {pos}: ink band at row {row}: left {a0}..{a1}, right {b0}..{b1} (expect {Exp(l0):0}..{Exp(l1):0} and {Exp(r0):0}..{Exp(r1):0})");
        }

        // sheet: joins (rows 1-3), caps (rows 4-6), outline position (rows 7-9), for a look at the shapes
        var scene = new Sr2d64CSport.VectorImage { ViewBox = new RectangleF(0, 0, 800, 900), Width = 800, Height = 900 };
        var sheet = new Sr2d64CSport.Sprite(800, 900);
        sheet.ClearBuffer(unchecked((int)0xFFF7F4EC));
        int sInk = unchecked((int)0xFF1B2A41), sMute = unchecked((int)0xFF6B6F7A);
        string[] rowNames = { "join bevel", "join miter", "join round", "cap butt", "cap round", "cap square", "outline centre", "outline inside", "outline outside" };
        for (int row = 0; row < 9; row++)
        {
            int y = 90 + row * 90;
            var rowName = Sr2d64CSport.VectorText.Create(rowNames[row], 14f, new Sr2d64CSport.VectorTextStyle { Color = sMute });
            rowName.Draw(sheet, 20, y + 14);
            scene.Add(rowName, System.Numerics.Matrix3x2.CreateTranslation(20 - rowName.ViewBox.X, y + 14 - rowName.ViewBox.Y));
            Sr2d64CSport.VectorTextStyle st; string sample;
            if (row < 3) { st = new Sr2d64CSport.VectorTextStyle { Color = sInk, Radius = 0f, Join = new[] { Sr2d64CSport.VectorJoin.Bevel, Sr2d64CSport.VectorJoin.Miter, Sr2d64CSport.VectorJoin.Round }[row] }; sample = "AVNWK"; }
            else if (row < 6) { st = new Sr2d64CSport.VectorTextStyle { Color = sInk, Radius = 0f, Cap = new[] { Sr2d64CSport.VectorCap.Butt, Sr2d64CSport.VectorCap.Round, Sr2d64CSport.VectorCap.Square }[row - 3] }; sample = "IlLT"; }
            else { st = new Sr2d64CSport.VectorTextStyle { Color = sInk, Radius = 0f, Bold = true, OutlinePosition = new[] { Sr2d64CSport.VectorOutlinePosition.Center, Sr2d64CSport.VectorOutlinePosition.Inside, Sr2d64CSport.VectorOutlinePosition.Outside }[row - 6] }; sample = "OBD"; }
            var rowPic = Sr2d64CSport.VectorText.Create(sample, 56f, st);
            // placed by the picture's origin (the pen start), not its padded box: rows of different joins line up
            var rowM = System.Numerics.Matrix3x2.CreateTranslation(240, y);
            rowPic.Draw(sheet, rowM);
            scene.Add(rowPic, rowM);
            // CTLRUN_WIREFRAME=1: a hairline wireframe of the outline rows (red), over the filled glyphs
            if (row >= 6 && Environment.GetEnvironmentVariable("CTLRUN_WIREFRAME") == "1")
            {
                var wire = Sr2d64CSport.VectorText.Wireframe(rowPic, unchecked((int)0xFFE0245E));
                wire.Draw(sheet, rowM);
                scene.Add(wire, rowM);
            }
        }
        SaveSprite(sheet, "vectortext_outlines.png");
        SaveText(scene.ToSvg(), "vectortext_outlines.svg");

        // render: plain, bold, italic, and text on the arc (picture to look at)
        var canvas = new Sr2d64CSport.Sprite(800, 600);
        canvas.ClearBuffer(unchecked((int)0xFFFFFFFF));
        Sr2d64CSport.VectorText.Create("Hello Челябинск 2026", 36f).Draw(canvas, 20, 30);
        Sr2d64CSport.VectorText.Create("Bold Жёлтый", 36f, new Sr2d64CSport.VectorTextStyle { Bold = true }).Draw(canvas, 20, 100);
        Sr2d64CSport.VectorText.Create("Italic слева", 36f, new Sr2d64CSport.VectorTextStyle { Italic = true }).Draw(canvas, 20, 170);
        var big = new List<PointF>();
        for (int i = 0; i <= 64; i++) { double a = Math.PI - Math.PI * i / 64.0; big.Add(new PointF(400 + 260 * (float)Math.Cos(a), 560 - 260 * (float)Math.Sin(a))); }
        Sr2d64CSport.VectorText.OnPath("ул. Кирова - проспект Ленина", big, 22f,
            new Sr2d64CSport.VectorTextPathOptions { Style = new Sr2d64CSport.VectorTextStyle { Bold = true }, Align = Sr2d64CSport.VectorTextAlign.Center }).Draw(canvas, System.Numerics.Matrix3x2.Identity);   // path coordinates = canvas coordinates
        int dark = 0; foreach (var px in canvas.Pixels) if (((px >> 16) & 255) < 128 && ((px >> 8) & 255) < 128 && (px & 255) < 128) dark++;
        Chk(dark > 500, $"the picture has ink ({dark} dark pixels)");
        SaveSprite(canvas, "vectortext.png");

        // the demo's curve test, on its paper background (dark ink, not black on grey)
        var paper = new Sr2d64CSport.Sprite(800, 600);
        paper.ClearBuffer(unchecked((int)0xFFF7F4EC));
        var ink = unchecked((int)0xFF1B2A41); var accent = unchecked((int)0xFFB0432A);
        var upper = new List<PointF>();
        for (int i = 0; i <= 96; i++) { double a = Math.PI - Math.PI * i / 96.0; upper.Add(new PointF(400 + 220 * (float)Math.Cos(a), 300 - 220 * (float)Math.Sin(a))); }
        Sr2d64CSport.VectorText.OnPath("The quick brown fox jumps over the lazy dog", upper, 14f,
            new Sr2d64CSport.VectorTextPathOptions { Style = new Sr2d64CSport.VectorTextStyle { Bold = true, Color = ink }, Align = Sr2d64CSport.VectorTextAlign.Center }).Draw(paper, System.Numerics.Matrix3x2.Identity);
        var inner = new List<PointF>();
        for (int i = 0; i <= 96; i++) { double a = Math.PI - Math.PI * i / 96.0; inner.Add(new PointF(400 + 150 * (float)Math.Cos(a), 300 - 150 * (float)Math.Sin(a))); }
        Sr2d64CSport.VectorText.OnPath("Lorem ipsum dolor sit amet, consectetur", inner, 11.2f,
            new Sr2d64CSport.VectorTextPathOptions { Style = new Sr2d64CSport.VectorTextStyle { Bold = true, Color = accent }, Align = Sr2d64CSport.VectorTextAlign.Center, Above = false }).Draw(paper, System.Numerics.Matrix3x2.Identity);
        var fitLine = new List<PointF> { new PointF(200, 500), new PointF(600, 500) };
        Sr2d64CSport.VectorText.OnPath("Fit to the path", fitLine, 14f, new Sr2d64CSport.VectorTextPathOptions { Style = new Sr2d64CSport.VectorTextStyle { Color = ink }, FitToPath = true }).Draw(paper, System.Numerics.Matrix3x2.Identity);
        SaveSprite(paper, "vectortext_curve_demo.png");
    }

    static float Dist(PointF p, float cx, float cy) => MathF.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
}
