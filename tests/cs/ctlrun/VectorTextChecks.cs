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

    static void SaveSprite(Sr2d64CSport.Sprite s, string name)
    {
        string outDir = Environment.GetEnvironmentVariable("CTLRUN_OUT") ?? Path.Combine(Path.GetTempPath(), "ctlrun");
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, name);
        File.WriteAllBytes(file, Sr2d64CSport.Png.Encode(s.Pixels, s.Width, s.Height));
        Console.WriteLine($"wrote {file} {s.Width}x{s.Height}");
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
    }

    static float Dist(PointF p, float cx, float cy) => MathF.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
}
