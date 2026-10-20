// Coverage check for the outline text. Three renders of the same picture are compared per pixel:
//   direct : the normal anti-aliased draw at device scale (what the user sees)
//   ref8   : anti-aliased draw at 8x, averaged back down (the old reference)
//   truth  : NON-anti-aliased draw at 16x, averaged over each device pixel. A non-AA fill tests the pixel centre only,
//            so 16x16 point samples per pixel give the true coverage with no edge filter in the loop.
// A seam is a dip in direct that truth does not have. The report says which of direct / ref8 is closer to truth.
using System;
using System.IO;
using System.Numerics;
using Sr2d64CSport;

static class OutlineSeamChecks
{
    const int White = unchecked((int)0xFFFFFFFF), Black = unchecked((int)0xFF000000);

    public static void Run()
    {
        string outDir = Environment.GetEnvironmentVariable("CTLRUN_OUT") ?? Path.Combine(Path.GetTempPath(), "ctlrun");
        Directory.CreateDirectory(outDir);
        foreach (var zoom in new[] { 1f, 1.06f, 2.49f })
        foreach (var pos in new[] { VectorOutlinePosition.Center, VectorOutlinePosition.Inside, VectorOutlinePosition.Outside })
        {
            var pic = VectorText.Create("OBD", 28f, new VectorTextStyle { Bold = true, Color = Black, OutlinePosition = pos });
            const int pad = 4;
            int W = (int)MathF.Ceiling(pic.ViewBox.Width * zoom) + 2 * pad, H = (int)MathF.Ceiling(pic.ViewBox.Height * zoom) + 2 * pad;
            Matrix3x2 Map(float k) => Matrix3x2.CreateScale(zoom * k) * Matrix3x2.CreateTranslation(k * (pad - pic.ViewBox.X * zoom), k * (pad - pic.ViewBox.Y * zoom));

            var direct = new Sprite(W, H); direct.ClearBuffer(White);
            pic.Draw(direct, Map(1f));

            const int S = 8;
            var big = new Sprite(W * S, H * S); big.ClearBuffer(White);
            pic.Draw(big, Map(S));

            const int T = 16;
            var truthBig = new Sprite(W * T, H * T); truthBig.ClearBuffer(White);
            pic.Draw(truthBig, Map(T), new VectorRenderOptions { AA = false });

            var bp = big.Pixels; var tp = truthBig.Pixels;
            double maxD = 0, maxR = 0; int overD = 0, overR = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int sum = 0, tsum = 0;
                    for (int j = 0; j < S; j++)
                        for (int i = 0; i < S; i++) sum += bp[(y * S + j) * big.Width + x * S + i] & 0xff;
                    for (int j = 0; j < T; j++)
                        for (int i = 0; i < T; i++) tsum += tp[(y * T + j) * truthBig.Width + x * T + i] & 0xff;
                    double refV = sum / (double)(S * S), truth = tsum / (double)(T * T);
                    double dd = Math.Abs((direct.Pixels[y * W + x] & 0xff) - truth) / 255.0;
                    double dr = Math.Abs(refV - truth) / 255.0;
                    if (dd > maxD) maxD = dd;
                    if (dr > maxR) maxR = dr;
                    if (dd > 0.25) overD++;
                    if (dr > 0.25) overR++;
                }
            Console.WriteLine($"  seams {pos} zoom {zoom:0.00}: direct vs truth worst {maxD:0.00}, {overD} px > 0.25; ref8 vs truth worst {maxR:0.00}, {overR} px > 0.25");
            File.WriteAllBytes(Path.Combine(outDir, $"seam_{pos}_{zoom:0.00}.png"), Png.Encode(direct.Pixels, direct.Width, direct.Height));
        }

        // VectorRenderOptions.Wireframe: the hairline overlay has the wireframe colour only when the option is on
        int wire = unchecked((int)0xFFE0245E);
        var wpic = VectorText.Create("OBD", 40f, new VectorTextStyle { Bold = true, Color = Black });
        var off = new Sprite(200, 80); off.ClearBuffer(White);
        var on = new Sprite(200, 80); on.ClearBuffer(White);
        var mw = Matrix3x2.CreateTranslation(10 - wpic.ViewBox.X, 10 - wpic.ViewBox.Y);
        wpic.Draw(off, mw, new VectorRenderOptions { Wireframe = false });
        wpic.Draw(on, mw, new VectorRenderOptions { Wireframe = true });
        int countOff = 0, countOn = 0;
        for (int i = 0; i < off.Pixels.Length; i++) { if ((off.Pixels[i] & 0xFFFFFF) == (wire & 0xFFFFFF)) countOff++; if ((on.Pixels[i] & 0xFFFFFF) == (wire & 0xFFFFFF)) countOn++; }
        Console.WriteLine($"  wireframe: {countOn} overlay pixels with the flag, {countOff} without");
        if (countOn == 0 || countOff != 0) { Console.WriteLine("FAIL wireframe overlay"); Environment.Exit(1); }
    }

    // Baked outlines (SVG export without clips): the stroke pieces cut by in-house clipping must render like the clipped picture.
    public static void BakeRun()
    {
        string outDir = Environment.GetEnvironmentVariable("CTLRUN_OUT") ?? Path.Combine(Path.GetTempPath(), "ctlrun");
        Directory.CreateDirectory(outDir);
        double worstAll = 0; bool fail = false;
        foreach (var pos in new[] { VectorOutlinePosition.Inside, VectorOutlinePosition.Outside })
        foreach (var zoom in new[] { 1f, 2.49f })
        {
            var pic = VectorText.Create("OBD", 28f, new VectorTextStyle { Bold = true, Color = Black, OutlinePosition = pos });
            var baked = BakedCopy(pic);
            var svgBack = VectorImage.FromSvg(pic.ToSvg(true));
            const int pad = 4;
            int W = (int)MathF.Ceiling(pic.ViewBox.Width * zoom) + 2 * pad, H = (int)MathF.Ceiling(pic.ViewBox.Height * zoom) + 2 * pad;
            Matrix3x2 map = Matrix3x2.CreateScale(zoom) * Matrix3x2.CreateTranslation(pad - pic.ViewBox.X * zoom, pad - pic.ViewBox.Y * zoom);
            var a = new Sprite(W, H); a.ClearBuffer(White); pic.Draw(a, map);
            var b = new Sprite(W, H); b.ClearBuffer(White); baked.Draw(b, map);
            var c = new Sprite(W, H); c.ClearBuffer(White); svgBack.Draw(c, map);
            double maxB = 0, maxC = 0; int overB = 0;
            for (int i = 0; i < a.Pixels.Length; i++)
            {
                double db = Math.Abs((a.Pixels[i] & 0xff) - (b.Pixels[i] & 0xff)) / 255.0;
                double dc = Math.Abs((a.Pixels[i] & 0xff) - (c.Pixels[i] & 0xff)) / 255.0;
                if (db > maxB) maxB = db;
                if (dc > maxC) maxC = dc;
                if (db > 0.25) overB++;
            }
            worstAll = Math.Max(worstAll, Math.Max(maxB, maxC));
            Console.WriteLine($"  bake {pos} zoom {zoom:0.00}: baked picture vs clipped worst {maxB:0.000}, {overB} px > 0.25; baked SVG read back vs clipped worst {maxC:0.000}");
            if (maxB > 0.25 || maxC > 0.25) fail = true;
            File.WriteAllBytes(Path.Combine(outDir, $"bake_{pos}_{zoom:0.00}.png"), Png.Encode(b.Pixels, b.Width, b.Height));
        }

        // wireframe overlay: FastLines must change it (1 px hairlines: no AA = hard edges only); WireframeColor must set its colour
        {
            var wpic = VectorText.Create("OBD", 40f, new VectorTextStyle { Bold = true, Color = Black });
            int red = unchecked((int)0xFFE0245E);
            int partialFast = 0, partialAa = 0, redFast = 0;
            var m = Matrix3x2.CreateTranslation(10 - wpic.ViewBox.X, 10 - wpic.ViewBox.Y);
            var plain = new Sprite(200, 80); plain.ClearBuffer(White); wpic.Draw(plain, m, new VectorRenderOptions { Wireframe = false });
            foreach (bool fast in new[] { true, false })
            {
                var sp = new Sprite(200, 80); sp.ClearBuffer(White);
                wpic.Draw(sp, m, new VectorRenderOptions { Wireframe = true, FastLines = fast, WireframeColor = red });
                int partial = 0, exact = 0;
                for (int k = 0; k < sp.Pixels.Length; k++)
                {
                    if (sp.Pixels[k] == plain.Pixels[k]) continue;                       // not the overlay
                    if ((sp.Pixels[k] & 0xFFFFFF) == (red & 0xFFFFFF)) exact++; else partial++;
                }
                if (fast) { partialFast = partial; redFast = exact; } else partialAa = partial;
            }
            Console.WriteLine($"  wireframe FastLines: overlay pixels not in the wireframe colour {partialFast} (fast) vs {partialAa} (AA); in the colour {redFast} (fast)");
            if (partialFast != 0 || partialAa == 0 || redFast == 0) { Console.WriteLine("FAIL wireframe FastLines"); Environment.Exit(1); }
        }

        // node structure of the baked bands (what a vector editor shows): hairline wireframe at 8x, no overlaps expected
        {
            var pic = VectorText.Create("OBD", 28f, new VectorTextStyle { Bold = true, Color = Black, OutlinePosition = VectorOutlinePosition.Inside });
            var baked = BakedCopy(pic);
            int nodes = 0; foreach (var s2 in baked.Shapes) nodes += s2.Path.SegmentCount;
            Matrix3x2 m8 = Matrix3x2.CreateScale(8f) * Matrix3x2.CreateTranslation(-pic.ViewBox.X * 8f + 8, -pic.ViewBox.Y * 8f + 8);
            var wsp = new Sprite((int)MathF.Ceiling(pic.ViewBox.Width * 8f) + 16, (int)MathF.Ceiling(pic.ViewBox.Height * 8f) + 16);
            wsp.ClearBuffer(White);
            VectorText.Wireframe(baked, unchecked((int)0xFFE0245E)).Draw(wsp, m8);
            File.WriteAllBytes(Path.Combine(outDir, "bake_nodes_inside.png"), Png.Encode(wsp.Pixels, wsp.Width, wsp.Height));
            Console.WriteLine($"  bake nodes (OBD inside): {nodes} segments in {baked.Shapes.Count} shapes");
        }

        // sample sheet for the user's SVG editor: the three positions, bold, outlines baked (no clipPath anywhere)
        var sheet = new VectorImage { Title = "Outline positions (baked)" };
        float y = 0; float maxW = 0;
        foreach (var pos in new[] { VectorOutlinePosition.Center, VectorOutlinePosition.Inside, VectorOutlinePosition.Outside })
        {
            var row = VectorText.Create("OBD 08", 56f, new VectorTextStyle { Bold = true, Color = Black, OutlinePosition = pos });
            float dx = 20 - row.ViewBox.X, dy = y + 20 - row.ViewBox.Y;
            foreach (var s in row.Shapes) { s.Transform = s.Transform * Matrix3x2.CreateTranslation(dx, dy); sheet.Shapes.Add(s); }
            y += row.ViewBox.Height + 30; maxW = MathF.Max(maxW, row.ViewBox.Width + 40);
        }
        sheet.ViewBox = new System.Drawing.RectangleF(0, 0, maxW, y + 20); sheet.Width = maxW; sheet.Height = y + 20;
        File.WriteAllText(Path.Combine(outDir, "vectortext_outlines_baked.svg"), sheet.ToSvg(true), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"  bake worst over all: {worstAll:0.000} ({(fail ? "FAIL" : "ok")})");
        if (fail) { Console.WriteLine("FAIL baked outlines"); Environment.Exit(1); }
    }

    static VectorImage BakedCopy(VectorImage pic)
    {
        var img = new VectorImage { Title = pic.Title, ViewBox = pic.ViewBox, Width = pic.Width, Height = pic.Height };
        foreach (var s in pic.Shapes)
        {
            if (s.BandOf == null || s.Stroke == null) { img.Shapes.Add(s); continue; }
            var p = new VectorPath();
            foreach (var poly in VectorBands.Bake(s.BandOf, s, s.StrokeWidth, s.BandSide == VectorOutlinePosition.Inside))
            {
                p.MoveTo(poly[0]);
                for (int i = 1; i < poly.Count; i++) p.LineTo(poly[i]);
                p.Close();
            }
            if (p.IsEmpty) continue;
            img.Shapes.Add(new VectorShape(p, s.Stroke) { Transform = s.Transform, Opacity = s.Opacity });
        }
        return img;
    }
}
