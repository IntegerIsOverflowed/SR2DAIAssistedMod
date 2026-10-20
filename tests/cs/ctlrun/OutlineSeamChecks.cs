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

        // the demo's full SampleSvg drawn like the import test (fit x scale, Draw with the demo options)
        {
            const string fsvg = @"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 200'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='#ffcc33'/><stop offset='1' stop-color='#ff3366'/></linearGradient><radialGradient id='r'><stop offset='0' stop-color='#fff'/><stop offset='1' stop-color='#3366ff'/></radialGradient></defs><rect x='10' y='10' width='180' height='180' rx='24' fill='url(#g)' stroke='#402' stroke-width='4'/><circle cx='70' cy='70' r='40' fill='url(#r)' fill-opacity='0.9'/><path d='M40 150 C 70 100, 110 190, 160 130' fill='none' stroke='#fff' stroke-width='8' stroke-linecap='round' stroke-dasharray='20 10'/><polygon points='150,30 165,70 205,70 172,95 185,135 150,110 115,135 128,95 95,70 135,70' fill='#33aa55' stroke='#062' stroke-width='3' stroke-linejoin='round' transform='translate(-10 5) scale(0.8) rotate(10 150 80)'/><ellipse cx='100' cy='170' rx='60' ry='14' fill='#202020' fill-opacity='0.35'/><path d='M30 120 h40 v-20 h-40 z M40 110 h20 v0' fill='#ffffff' fill-rule='evenodd' opacity='0.7'/></svg>";
            var fimg = VectorImage.FromSvg(fsvg);
            foreach (var fs in fimg.Shapes) if (fs.Dash != null) Console.WriteLine($"  demo-dash: after load dash {string.Join(" ", fs.Dash)}, transform {fs.Transform.M11:0.###}/{fs.Transform.M22:0.###}, width {fs.StrokeWidth}");
            var fcan = new Sprite(600, 600); fcan.ClearBuffer(Black);
            var fo = new VectorRenderOptions { AA = true, Opacity = 1f, Strokes = true, Fills = true };
            fimg.Draw(fcan, 20, 20, 1.2f, 1.2f, 0f, fo, 0.5f, 0.5f);
            foreach (var fs in fimg.Shapes) if (fs.Dash != null) Console.WriteLine($"  demo-dash: after draw dash {string.Join(" ", fs.Dash)}");
            int flit = 0; for (int i = 0; i < fcan.Pixels.Length; i++) if ((fcan.Pixels[i] & 0xff) > 0x80) flit++;
            Console.WriteLine($"  demo-dash: white pixels {flit}");
            foreach (var aa in new[] { true, false }) foreach (var op in new[] { 1f, 0.5f })
            {
                var qcan = new Sprite(600, 600); qcan.ClearBuffer(Black);
                fimg.Draw(qcan, 20, 20, 1.5f, 1.5f, 0f, new VectorRenderOptions { AA = aa, Opacity = op, Strokes = true, Fills = true }, 0.5f, 0.5f);
                int qlit = 0; for (int i = 0; i < qcan.Pixels.Length; i++) if ((qcan.Pixels[i] & 0xff) > 0x80) qlit++;
                Console.WriteLine($"  demo-dash: AA {aa} opacity {op}: white pixels {qlit}");
            }
            {
                var solidImg = VectorImage.FromSvg(fsvg);
                foreach (var ss in solidImg.Shapes) ss.Dash = null;
                foreach (var sc in new[] { 1f, 1.5f, 3.4f })
                {
                    var dA = new Sprite(900, 900); dA.ClearBuffer(Black); var sA = new Sprite(900, 900); sA.ClearBuffer(Black);
                    var oo = new VectorRenderOptions { AA = false, Opacity = 1f, Strokes = true, Fills = false };
                    fimg.Draw(dA, 20, 20, sc, sc, 0f, oo, 0.5f, 0.5f);
                    solidImg.Draw(sA, 20, 20, sc, sc, 0f, oo, 0.5f, 0.5f);
                    int ld = 0, ls = 0; for (int i = 0; i < dA.Pixels.Length; i++) { if ((dA.Pixels[i] & 0xff) > 0x80) ld++; if ((sA.Pixels[i] & 0xff) > 0x80) ls++; }
                    Console.WriteLine($"  demo-dash: scale {sc}: dashed {ld} px, solid {ls} px");
                    if (sc == 3.4f) { bool gaps = ld < ls * 0.95; Console.WriteLine($"  {(gaps ? "ok  " : "FAIL")} dash gaps at demo scale 3.4 ({ld} dashed vs {ls} solid)"); if (!gaps) Environment.Exit(1); }
                    if (sc == 3.4f) { Directory.CreateDirectory("/tmp/ctlout"); File.WriteAllBytes("/tmp/ctlout/dash_demo_34.png", Png.Encode(dA.Pixels, dA.Width, dA.Height)); File.WriteAllBytes("/tmp/ctlout/solid_demo_34.png", Png.Encode(sA.Pixels, sA.Width, sA.Height)); }
                }
            }
            var vsc = fimg.Cached; vsc.AA = true; vsc.Opacity = 1f; vsc.Fills = true;
            var ccan = new Sprite(600, 600); ccan.ClearBuffer(Black);
            vsc.Draw(ccan, 20, 20, 1.2f, 1.2f, 0f, 0.5f, 0.5f);
            foreach (var fs in fimg.Shapes) if (fs.Dash != null) Console.WriteLine($"  demo-dash: after cached draw dash {string.Join(" ", fs.Dash)}");
            int clit = 0; for (int i = 0; i < ccan.Pixels.Length; i++) if ((ccan.Pixels[i] & 0xff) > 0x80) clit++;
            Console.WriteLine($"  demo-dash: cached white pixels {clit}");
        }
        // dashed stroke of the demo's sample SVG (stroke-dasharray 20 10, round caps): must show gaps
        {
            const string dsvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 200'><path d='M40 150 C 70 100, 110 190, 160 130' fill='none' stroke='#fff' stroke-width='8' stroke-linecap='round' stroke-dasharray='20 10'/></svg>";
            var dimg = VectorImage.FromSvg(dsvg);
            foreach (var ds in dimg.Shapes) Console.WriteLine($"  dash: stroke {ds.Stroke != null}, width {ds.StrokeWidth}, dash {(ds.Dash == null ? "null" : string.Join(" ", ds.Dash))}, cap {ds.Cap}");
            var dsp = new Sprite(200, 200); dsp.ClearBuffer(Black);
            foreach (var mt in new[] { Matrix3x2.Identity, Matrix3x2.CreateScale(2.5f), Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateTranslation(20, 10), Matrix3x2.CreateRotation(0.3f) * Matrix3x2.CreateScale(2f, 1.2f) })
            {
                var msp = new Sprite(600, 600); msp.ClearBuffer(Black);
                dimg.Draw(msp, mt * Matrix3x2.CreateTranslation(0, 0), new VectorRenderOptions { AA = true, Opacity = 1f, Strokes = true, Fills = true });
                int lit = 0; for (int i = 0; i < msp.Pixels.Length; i++) if ((msp.Pixels[i] & 0xff) > 0x80) lit++;
                Console.WriteLine($"  dash: matrix {mt.M11:0.00},{mt.M12:0.00},{mt.M21:0.00},{mt.M22:0.00}: white pixels {lit}");
            }
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
