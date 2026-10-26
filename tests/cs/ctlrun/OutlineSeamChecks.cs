// Coverage check for the outline text. Three renders of the same picture are compared per pixel:
//   direct : the normal anti-aliased draw at device scale (what the user sees)
//   ref8   : anti-aliased draw at 8x, averaged back down (the old reference)
//   truth  : NON-anti-aliased draw at 16x, averaged over each device pixel. A non-AA fill tests the pixel centre only,
//            so 16x16 point samples per pixel give the true coverage with no edge filter in the loop.
// A seam is a dip in direct that truth does not have. The report says which of direct / ref8 is closer to truth.
using System;
using System.Collections.Generic;
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
        double worstF = 0, worstInt = 0; bool fail = false;
        VectorOffset.FastPath = System.Environment.GetEnvironmentVariable("CTL_NOFAST") == null;
        Console.WriteLine($"  bake fast path: {VectorOffset.FastPath}");
        foreach (var pos in new[] { VectorOutlinePosition.Inside, VectorOutlinePosition.Outside })
        foreach (var jn in new[] { VectorJoin.Bevel, VectorJoin.Round, VectorJoin.Miter })
        foreach (var zoom in new[] { 1f, 2.49f })
        {
            // the corner rule is what the exported band used to drop: a round join came back bevelled. The side contours are
            // closed, so the cap cannot show here; it is still asked for so the bake carries it through
            var pic = VectorText.Create("OBD", 28f, new VectorTextStyle { Bold = true, Color = Black, OutlinePosition = pos, Join = jn, Cap = VectorCap.Round });
            var baked = BakedCopy(pic);
            var svgBack = VectorImage.FromSvg(pic.ToSvg(true));
            const int pad = 4;
            int W = (int)MathF.Ceiling(pic.ViewBox.Width * zoom) + 2 * pad, H = (int)MathF.Ceiling(pic.ViewBox.Height * zoom) + 2 * pad;
            Matrix3x2 map = Matrix3x2.CreateScale(zoom) * Matrix3x2.CreateTranslation(pad - pic.ViewBox.X * zoom, pad - pic.ViewBox.Y * zoom);
            var a = new Sprite(W, H); a.ClearBuffer(White); pic.Draw(a, map);
            var b = new Sprite(W, H); b.ClearBuffer(White); baked.Draw(b, map);
            var c = new Sprite(W, H); c.ClearBuffer(White); svgBack.Draw(c, map);
            double maxB = 0, maxC = 0; int overB = 0;
            var whereB = new List<(int X, int Y, int Live, int Baked)>();
            for (int i = 0; i < a.Pixels.Length; i++)
            {
                double db = Math.Abs((a.Pixels[i] & 0xff) - (b.Pixels[i] & 0xff)) / 255.0;
                double dc = Math.Abs((a.Pixels[i] & 0xff) - (c.Pixels[i] & 0xff)) / 255.0;
                if (db > maxB) maxB = db;
                if (dc > maxC) maxC = dc;
                if (db > 0.25) { overB++; if (whereB.Count < 6) whereB.Add((i % a.Width, i / a.Width, a.Pixels[i] & 0xff, b.Pixels[i] & 0xff)); }
            }
            // the ink integral of the two, at the settings the user sees: the amount of paint each puts down is the shape
            // property that the edge conventions of the two passes cannot move
            double sumA = 0, sumB = 0;
            for (int i = 0; i < a.Pixels.Length; i++) { sumA += 255 - (a.Pixels[i] & 0xff); sumB += 255 - (b.Pixels[i] & 0xff); }
            double dInt = Math.Abs(sumA - sumB) / Math.Max(1.0, sumA);
            Console.WriteLine($"  bake {pos} {jn} zoom {zoom:0.00}: baked picture vs clipped worst {maxB:0.000}, {overB} px > 0.25; baked SVG read back vs clipped worst {maxC:0.000}");
            Console.WriteLine($"      ink integral live {sumA / 255:0.#} px vs baked {sumB / 255:0.#} px, difference {dInt * 100:0.00}%");
            if (dInt > 0.01) fail = true;
            var dif = new int[a.Pixels.Length];
            for (int i = 0; i < a.Pixels.Length; i++) { byte v = (byte)Math.Min(255, Math.Abs((a.Pixels[i] & 0xff) - (b.Pixels[i] & 0xff)) * 4); dif[i] = unchecked((int)0xFF000000) | (v << 16) | (v << 8) | v; }
            File.WriteAllBytes(Path.Combine(outDir, $"diff_{pos}_{jn}_{zoom:0.00}.png"), Png.Encode(dif, a.Width, a.Height));
            File.WriteAllBytes(Path.Combine(outDir, $"bake_{pos}_{jn}_{zoom:0.00}.png"), Png.Encode(b.Pixels, b.Width, b.Height));
            File.WriteAllBytes(Path.Combine(outDir, $"ref_{pos}_{jn}_{zoom:0.00}.png"), Png.Encode(a.Pixels, a.Width, a.Height));
            // the same pair with both passes flattened finely. The stroke pass tessellates curves at its device-pixel
            // tolerance by default, and a miter spike of a coarse polyline sticks out past the true offset curve, so the
            // live look carries a tip overshoot the shape conversion does not (and should not) reproduce. Matched
            // tolerances compare the two shapes; the default pair above is what the user actually sees.
            var fine = new VectorRenderOptions { AA = true, AaQuality = 16, Opacity = 1f, Strokes = true, Fills = true, Tolerance = 0.02f };
            var fa = new Sprite(W, H); fa.ClearBuffer(White); pic.Draw(fa, map, fine);
            var fb = new Sprite(W, H); fb.ClearBuffer(White); baked.Draw(fb, map, fine);
            double maxF = 0;
            for (int i = 0; i < fa.Pixels.Length; i++) maxF = Math.Max(maxF, Math.Abs((fa.Pixels[i] & 0xff) - (fb.Pixels[i] & 0xff)) / 255.0);
            Console.WriteLine($"      matched 0.02 px flattening, 16 sub-scanline AA: worst {maxF:0.000}");
            // the same pair with hard edges and matched flattening: a pixel can only differ when the two shapes disagree at
            // its centre, so this is the shape check at pixel resolution. A band one pixel wide is nearly all boundary, which
            // is why the count is read against the ink pixels and not the canvas.
            var hardOpt = new VectorRenderOptions { AA = false, Opacity = 1f, Strokes = true, Fills = true, Tolerance = 0.02f };
            var ha = new Sprite(W, H); ha.ClearBuffer(White); pic.Draw(ha, map, hardOpt);
            var hb = new Sprite(W, H); hb.ClearBuffer(White); baked.Draw(hb, map, hardOpt);
            int hard = 0, ink = 0;
            var whereH = new List<string>();
            for (int i = 0; i < ha.Pixels.Length; i++)
            {
                if ((ha.Pixels[i] & 0xff) < 0x80) ink++;
                if (ha.Pixels[i] == hb.Pixels[i]) continue;
                hard++;
                if (whereH.Count < 8) whereH.Add($"({i % ha.Width},{i / ha.Width}) live {(ha.Pixels[i] & 0xff)} baked {(hb.Pixels[i] & 0xff)}");
            }
            Console.WriteLine($"      hard edges, matched flattening: {hard} differing px of {ink} ink px ({hard * 100.0 / Math.Max(1, ink):0.00}%)");
            foreach (var w in whereH) Console.WriteLine($"      {w}");
            if (hard > Math.Max(8, ink / 20)) fail = true;
            if (maxF > 0.25) fail = true;
            worstF = Math.Max(worstF, maxF); worstInt = Math.Max(worstInt, dInt);
        }

        // the same export with a pattern on it. VectorOffset.Band takes no dash arguments, so an export that bakes through it
        // alone writes the outline solid: this row is the one that tells a dashed bake from a dropped dash. The pattern is set
        // through the container API an editor would use, on the shapes VectorText made.
        {
            const float Zoom = 2.49f;
            VectorImage Make(bool dashed)
            {
                var p = VectorText.Create("OBD", 28f, new VectorTextStyle { Bold = true, Color = Black, OutlinePosition = VectorOutlinePosition.Outside, Join = VectorJoin.Miter, Cap = VectorCap.Round });
                // 10 on / 16 off against a 4.5 wide outline of a 28 pt glyph, whose contours are only some 70 long: a round cap
                // reaches half a width (2.2) past each end of a dash, so an off length of that order is swallowed by the caps of
                // the dashes either side of it and the picture would show no gaps to compare
                if (dashed) foreach (var s in p.Shapes) s.SetDash(new float[] { 10f, 16f }, 0f, VectorDashSeam.Cut);
                return p;
            }
            var pic = Make(true);
            var svgBack = VectorImage.FromSvg(pic.ToSvg(true));
            var solidBack = VectorImage.FromSvg(Make(false).ToSvg(true));
            const int pad = 4;
            int W = (int)MathF.Ceiling(pic.ViewBox.Width * Zoom) + 2 * pad, H = (int)MathF.Ceiling(pic.ViewBox.Height * Zoom) + 2 * pad;
            Matrix3x2 map = Matrix3x2.CreateScale(Zoom) * Matrix3x2.CreateTranslation(pad - pic.ViewBox.X * Zoom, pad - pic.ViewBox.Y * Zoom);
            var hard = new VectorRenderOptions { AA = false, Opacity = 1f, Strokes = true, Fills = true, Tolerance = 0.02f };
            int Ink(VectorImage v)
            {
                var sp = new Sprite(W, H); sp.ClearBuffer(White); v.Draw(sp, map, hard);
                int n = 0; for (int i = 0; i < sp.Pixels.Length; i++) if ((sp.Pixels[i] & 0xff) < 0x80) n++;
                return n;
            }
            int live = Ink(pic), baked = Ink(svgBack), solid = Ink(solidBack);
            // the same picture baked through Band alone, which is what the export did before: printed, not gated, so the row
            // shows the number a dropped pattern produces instead of only asserting it is not there
            int dropped = Ink(BakedCopy(pic));
            double toLive = Math.Abs(baked - live) / Math.Max(1.0, live), toSolid = Math.Abs(baked - solid) / Math.Max(1.0, solid);
            double gaps = 1.0 - live / Math.Max(1.0, solid);
            Console.WriteLine($"  dashed bake: live {live} px, baked read back {baked} px ({toLive * 100:0.00}% from live), solid bake {solid} px ({toSolid * 100:0.00}% from that), stroke gaps {gaps * 100:0.00}%; band-only bake of the dashed picture {dropped} px");
            // the stroke has to show the pattern at all, or the row below it measures nothing
            if (gaps < 0.1) { Console.WriteLine("      FAIL the live stroke shows no gaps: the pattern is lost in its own caps"); fail = true; }
            // the baked outline has to be the dashed one: near the stroke it painted, and clearly short of the solid band
            if (toLive > 0.05) { Console.WriteLine("      FAIL the baked outline is not the stroke it came from"); fail = true; }
            if (toSolid < 0.05) { Console.WriteLine("      FAIL the exported outline came back solid: the bake dropped the pattern"); fail = true; }
            var lv = new Sprite(W, H); lv.ClearBuffer(White); pic.Draw(lv, map, hard);
            var bk = new Sprite(W, H); bk.ClearBuffer(White); svgBack.Draw(bk, map, hard);
            var dif = new int[W * H];
            for (int i = 0; i < dif.Length; i++) { byte v = (byte)Math.Min(255, Math.Abs((lv.Pixels[i] & 0xff) - (bk.Pixels[i] & 0xff)) * 4); dif[i] = unchecked((int)0xFF000000) | (v << 16) | (v << 8) | v; }
            File.WriteAllBytes(Path.Combine(outDir, "diff_dashed_bake.png"), Png.Encode(dif, W, H));
            File.WriteAllText(Path.Combine(outDir, "dashed_bake.svg"), pic.ToSvg(true), new System.Text.UTF8Encoding(false));
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
        Console.WriteLine($"  bake worst over all: pixel {worstF:0.000} (gate 0.25), ink integral {worstInt * 100:0.00}% (gate 1%) ({(fail ? "FAIL" : "ok")})");
        if (fail) { Console.WriteLine("FAIL baked outlines"); Environment.Exit(1); }
    }

    static VectorImage BakedCopy(VectorImage pic)
    {
        var img = new VectorImage { Title = pic.Title, ViewBox = pic.ViewBox, Width = pic.Width, Height = pic.Height };
        foreach (var s in pic.Shapes)
        {
            if (s.BandOf == null || s.Stroke == null) { img.Shapes.Add(s); continue; }
            var p = VectorOffset.Band(s.BandOf, s.StrokeWidth, s.BandSide, s.Cap, s.Join, s.MiterLimit);
            if (p.IsEmpty) continue;
            img.Shapes.Add(new VectorShape(p, s.Stroke) { Transform = s.Transform, Opacity = s.Opacity });
        }
        return img;
    }

    static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    static string PathD(VectorPath p, Func<float, float, (float, float)> f)
    {
        var sb = new System.Text.StringBuilder(); int di = 0; var d = p.Data;
        foreach (var k in p.Kinds)
        {
            switch (k)
            {
                case VectorPath.K.Move: { var (x, y) = f(d[di], d[di + 1]); sb.Append($"M{F(x)} {F(y)}"); di += 2; break; }
                case VectorPath.K.Line: { var (x, y) = f(d[di], d[di + 1]); sb.Append($"L{F(x)} {F(y)}"); di += 2; break; }
                case VectorPath.K.Cubic:
                    {
                        var (a, b) = f(d[di], d[di + 1]); var (c, e) = f(d[di + 2], d[di + 3]); var (g, h) = f(d[di + 4], d[di + 5]);
                        sb.Append($"C{F(a)} {F(b)} {F(c)} {F(e)} {F(g)} {F(h)}"); di += 6; break;
                    }
                case VectorPath.K.Close: sb.Append('Z'); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Test sheet: the "8" glyph contour (zero corner rounding), 30 mm tall. Black = original, blue = inflated 1 mm, red = deflated 1 mm. Hairlines.</summary>
    /// <summary>All paths of an imported picture as one path, with their transforms applied.</summary>
    static VectorPath Gather(VectorImage img)
    {
        var all = new VectorPath();
        foreach (var sh in img.Shapes)
        {
            if (sh.Path == null || sh.Path.IsEmpty) continue;
            var m = sh.Transform; var d = sh.Path.Data; int di = 0;
            System.Func<int, (float, float)> P = i => { var q = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(d[i], d[i + 1]), m); return (q.X, q.Y); };
            foreach (var k in sh.Path.Kinds)
                switch (k)
                {
                    case VectorPath.K.Move: { var (x, y) = P(di); all.MoveTo(x, y); di += 2; break; }
                    case VectorPath.K.Line: { var (x, y) = P(di); all.LineTo(x, y); di += 2; break; }
                    case VectorPath.K.Cubic:
                        {
                            var (a, b) = P(di); var (c, e) = P(di + 2); var (g, h) = P(di + 4);
                            all.CurveTo(a, b, c, e, g, h); di += 6; break;
                        }
                    case VectorPath.K.Close: all.Close(); break;
                }
        }
        return all;
    }

    /// <summary>Overlay: the source curve (black) with its visual outline (blue) on top.</summary>
    public static void WriteVisualTest(string file)
    {
        var contour = Gather(VectorImage.Load(System.Environment.GetEnvironmentVariable("CTL_OFFSET_SRC")));
        var vis = VectorOffset.VisualOutline(contour);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i + 1 < contour.Data.Count; i += 2)
        {
            minX = Math.Min(minX, contour.Data[i]); maxX = Math.Max(maxX, contour.Data[i]);
            minY = Math.Min(minY, contour.Data[i + 1]); maxY = Math.Max(maxY, contour.Data[i + 1]);
        }
        float scale = 30f / (maxY - minY), margin = 5f;
        Func<float, float, (float, float)> map = (x, y) => ((x - minX) * scale + margin, (y - minY) * scale + margin);
        float W = (maxX - minX) * scale + 2 * margin, H = 30f + 2 * margin;
        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(W)}mm\" height=\"{F(H)}mm\" viewBox=\"0 0 {F(W)} {F(H)}\">\n");
        sb.Append("  <title>visual outline (blue) over the curve (black)</title>\n");
        sb.Append($"  <path id=\"curve\" d=\"{PathD(contour, map)}\" fill=\"none\" stroke=\"#000000\" stroke-width=\"0.05\"/>\n");
        sb.Append($"  <path id=\"visual\" d=\"{PathD(vis, map)}\" fill=\"none\" stroke=\"#0000ff\" stroke-width=\"0.2\" stroke-opacity=\"0.6\"/>\n");
        var nd = new System.Text.StringBuilder();
        foreach (var (ax, ay, bx, by) in VectorOffset.NodeNormals(vis, contour, 2f / scale))
        {
            var pa = map((float)ax, (float)ay); var pb = map((float)bx, (float)by);
            nd.Append($"M{F(pa.Item1)} {F(pa.Item2)}L{F(pb.Item1)} {F(pb.Item2)}");
        }
        sb.Append($"  <path id=\"normals\" d=\"{nd}\" fill=\"none\" stroke=\"#ff00aa\" stroke-width=\"0.05\"/>\n");
        sb.Append("</svg>\n");
        File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"visual outline: {file}: curve {contour.SegmentCount} segments, outline {vis.SegmentCount} segments");
    }

    public static void WriteBandTest(string file)
    {
        var contour = Gather(VectorImage.Load(System.Environment.GetEnvironmentVariable("CTL_OFFSET_SRC")));
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i + 1 < contour.Data.Count; i += 2)
        {
            minX = Math.Min(minX, contour.Data[i]); maxX = Math.Max(maxX, contour.Data[i]);
            minY = Math.Min(minY, contour.Data[i + 1]); maxY = Math.Max(maxY, contour.Data[i + 1]);
        }
        float scale = 30f / (maxY - minY), margin = 5f;
        Func<float, float, (float, float)> map = (x, y) => ((x - minX) * scale + margin, (y - minY) * scale + margin);
        float W = (maxX - minX) * scale + 2 * margin, H = 30f + 2 * margin;
        double mm = 1.0 / scale;   // contour units per millimetre
        var vis = VectorOffset.VisualOutline(contour);
        var outside = VectorOffset.Band(contour, 2 * mm, 0.02 * mm, 0.02 * mm);
        var inside = VectorOffset.Band(contour, -2 * mm, 0.02 * mm, 0.02 * mm);
        var (devOut, nOut) = VectorOffset.CheckBand(contour, outside, 2 * mm);
        var (devIn, nIn) = VectorOffset.CheckBand(contour, inside, -2 * mm);
        Console.WriteLine($"band: outside 2mm {nOut} segments, max deviation {devOut * scale:0.0000} mm; inside 2mm {nIn} segments, max deviation {devIn * scale:0.0000} mm");
        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(W)}mm\" height=\"{F(H)}mm\" viewBox=\"0 0 {F(W)} {F(H)}\">\n");
        sb.Append("  <title>2 mm outside and inside outlines (approximate band) over the curve</title>\n");
        sb.Append($"  <path id=\"curve\" d=\"{PathD(contour, map)}\" fill=\"none\" stroke=\"#000000\" stroke-width=\"0.05\"/>\n");
        sb.Append($"  <path id=\"visual\" d=\"{PathD(vis, map)}\" fill=\"none\" stroke=\"#0000ff\" stroke-width=\"0.1\"/>\n");
        sb.Append($"  <path id=\"outside_2mm\" d=\"{PathD(outside, map)}\" fill=\"#e0245e\" fill-opacity=\"0.18\" fill-rule=\"evenodd\" stroke=\"#e0245e\" stroke-width=\"0.08\"/>\n");
        sb.Append($"  <path id=\"inside_2mm\" d=\"{PathD(inside, map)}\" fill=\"#00a050\" fill-opacity=\"0.18\" fill-rule=\"evenodd\" stroke=\"#00a050\" stroke-width=\"0.08\"/>\n");
        sb.Append("</svg>\n");
        File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"band test: {file}");
    }

    static string N(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Check sheet for comparing with Corel: rows of centre, outside and inside outlines, 1 mm and 2 mm, in real millimetres.</summary>
    public static void WriteRowsTest(string file)
    {
        string src = System.Environment.GetEnvironmentVariable("CTL_OFFSET_SRC");
        string head = File.ReadAllText(src);
        var wm = System.Text.RegularExpressions.Regex.Match(head, "width=\"([0-9.]+)mm\"");
        var vbm = System.Text.RegularExpressions.Regex.Match(head, "viewBox=\"[-0-9.]+ [-0-9.]+ ([0-9.]+) ([0-9.]+)\"");
        if (!wm.Success || !vbm.Success) throw new System.InvalidOperationException("source needs a width in mm and a viewBox");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double widthMm = double.Parse(wm.Groups[1].Value, inv);
        double vbW = double.Parse(vbm.Groups[1].Value, inv);
        var img = VectorImage.Load(src);
        var contour = Gather(img);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i + 1 < contour.Data.Count; i += 2)
        {
            minX = Math.Min(minX, contour.Data[i]); maxX = Math.Max(maxX, contour.Data[i]);
            minY = Math.Min(minY, contour.Data[i + 1]); maxY = Math.Max(maxY, contour.Data[i + 1]);
        }
        double bw = maxX - minX, bh = maxY - minY;
        // The loader works in CSS px (96 dpi): img.Width is the physical width in px, so mm per gathered unit = widthMm / img.Width.
        double k = widthMm / img.Width;
        Console.WriteLine($"DBG gathered bbox w {bw} h {bh}");
        double pixel = 0.02 / k, tol = 0.02 / k;
        double d1 = 1.0 / k, d2 = 2.0 / k;
        var out1 = VectorOffset.Band(contour, d1, pixel, tol);
        var in1 = VectorOffset.Band(contour, -d1, pixel, tol);
        var out2 = VectorOffset.Band(contour, d2, pixel, tol);
        var in2 = VectorOffset.Band(contour, -d2, pixel, tol);
        var (e1, s1) = VectorOffset.CheckBand(contour, out1, d1);
        var (f1, t1) = VectorOffset.CheckBand(contour, in1, -d1);
        var (e2, s2) = VectorOffset.CheckBand(contour, out2, d2);
        var (f2, t2) = VectorOffset.CheckBand(contour, in2, -d2);
        Console.WriteLine($"control hull {N(bw * k)} x {N(bh * k)} mm; {N(k)} mm per gathered unit (file width {widthMm} mm, loader width {img.Width} px)");
        Console.WriteLine($"outside 1 mm: {s1} segments, max deviation {N(e1 * k)} mm");
        Console.WriteLine($"inside 1 mm: {t1} segments, max deviation {N(f1 * k)} mm");
        Console.WriteLine($"outside 2 mm: {s2} segments, max deviation {N(e2 * k)} mm");
        Console.WriteLine($"inside 2 mm: {t2} segments, max deviation {N(f2 * k)} mm");

        double Wmm = bw * k, Hmm = bh * k, pad = 2.5, labelH = 7.0, margin = 3.0;
        double cellW = Wmm + 2 * pad + 2, cellH = Hmm + labelH + 2 * pad;
        double sheetW = 2 * margin + 3 * cellW, sheetH = 2 * margin + 3 * cellH;
        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(sheetW)}mm\" height=\"{N(sheetH)}mm\" viewBox=\"0 0 {N(sheetW)} {N(sheetH)}\">\n");
        sb.Append("  <title>Tangled curve check: centre, outside and inside outlines at 1 mm and 2 mm; black = original curve</title>\n");
        void Panel(int col, int row, string label, VectorPath band, string colour)
        {
            double x0 = margin + col * cellW + pad + 1, y0 = margin + row * cellH + labelH;
            System.Func<float, float, (float, float)> map = (x, y) => ((float)((x - minX) * k + x0), (float)((y - minY) * k + y0));
            sb.Append($"  <text x=\"{N(x0)}\" y=\"{N(y0 - 3)}\" font-family=\"Arial\" font-size=\"3\">{label}</text>\n");
            if (band != null)
                sb.Append($"  <path d=\"{PathD(band, map)}\" fill=\"{colour}\" fill-opacity=\"0.25\" fill-rule=\"evenodd\" stroke=\"{colour}\" stroke-width=\"0.05\"/>\n");
            sb.Append($"  <path d=\"{PathD(contour, map)}\" fill=\"none\" stroke=\"#000000\" stroke-width=\"0.05\"/>\n");
        }
        const string blue = "#1f6fd0", red = "#e0245e";
        Panel(0, 0, "centre", null, null);
        Panel(1, 0, "outside 1 mm", out1, blue);
        Panel(2, 0, "inside 1 mm", in1, red);
        Panel(0, 1, "centre", null, null);
        Panel(1, 1, "outside 2 mm", out2, blue);
        Panel(0, 2, "centre", null, null);
        Panel(1, 2, "inside 2 mm", in2, red);
        sb.Append("</svg>\n");
        File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"rows test: {file} ({N(sheetW)} x {N(sheetH)} mm)");
    }

    public static void WriteRenderTest(string file, string svgPath) {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const double mmPerUnit = 0.01, pixel = 0.04, tol = 0.04; const double w2 = 1;
        string[] keys = { "eight jagged", "eight smooth", "tangled", "scribble", "A_one_jagged_line", "WAVY_DASH", "circle", "just_straight_line", "OBD", "corners" };
        var cols = new (string Col, int Pos, double W)[] { ("CENTER", 0, 2.0), ("OUTSIDE", 1, 1.0), ("INSIDE", 2, 1.0) };
        var doc = System.Xml.Linq.XDocument.Load(svgPath);
        var shapes = new List<(string col, string key, VectorPath path)>();
        foreach (var e in doc.Descendants()) {
            var id = (string)e.Attribute("id");
            if (string.IsNullOrEmpty(id) || id.StartsWith("grid", StringComparison.Ordinal)) continue;
            int u = id.IndexOf('_'); if (u <= 0) continue;
            var geo = SvgGeoMm(e, inv, mmPerUnit); if (geo == null) continue;
            string col = id.Substring(0, u), key = System.Text.RegularExpressions.Regex.Replace(id.Substring(u + 1), "_?x0020_?", " ").Trim();
            if (key.Contains("scalop", StringComparison.Ordinal) || key == "smooth_corner") {
                key = "corners";
                int ex = shapes.FindIndex(s => s.col == col && s.key == key);
                if (ex >= 0) { shapes[ex] = (col, key, CatPath(shapes[ex].path, geo)); continue; }
            }
            shapes.Add((col, key, geo));
        }
        // baked outline (black, as the demo's ConvFill) and the renderer's stroke layer on top (green, opacity 1 here; masks in bg grey)
        var img = new VectorImage();
        const int bg = unchecked((int)0xFFCCCCCC), green = unchecked((int)0xFF40E050), black = unchecked((int)0xFF000000);
        foreach (var key in keys) foreach (var (col, pos, w) in cols) {
            int si = shapes.FindIndex(x => x.col == col && x.key == key);
            if (si < 0) continue;
            var s = shapes[si];
            bool closed = s.path.Kinds.Contains(VectorPath.K.Close);
            bool ring = pos != 0 && closed;
            double r = ring ? w : w / 2;
            bool wavy = s.key == "WAVY_DASH";
            VectorPath baked, mid = s.path;
            if (wavy) { mid = pos == 0 ? s.path : VectorOffset.Band(s.path, pos == 1 ? w / 2 : -w / 2, pixel, tol); baked = VectorOffset.Dashed(mid, 5 * w, 3 * w, w, pixel, tol); }
            else baked = VectorOffset.Outline(s.path, r, ring ? pos : 0, pixel, tol);
            img.Shapes.Add(new VectorShape { Path = baked, Fill = new VectorColor(black), EvenOdd = false });
            // the render layer (demo RenderOf): strokes, then the bg masks that keep one half of a ring
            var masks = new List<VectorPath>();
            VectorPath strokePath = wavy ? mid : s.path;
            float width = (float)(wavy ? w : (ring ? 2 * w : w));
            float[]? dash = wavy ? new[] { (float)(5 * w), (float)(3 * w) } : null;
            if (ring && !wavy) masks.Add(pos == 1 ? s.path : ExteriorMm(s.path, 3 * w + 2));
            img.Shapes.Add(new VectorShape { Path = strokePath, Fill = null, Stroke = new VectorColor(green), StrokeWidth = width, Cap = VectorCap.Round, Join = VectorJoin.Round, Dash = dash, Opacity = 0.5f });
            foreach (var m in masks) img.Shapes.Add(new VectorShape { Path = m, Fill = new VectorColor(bg), EvenOdd = pos != 1 });
        }
        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        foreach (var sh in img.Shapes) { var bb = sh.Path.ControlBounds(); bx0 = Math.Min(bx0, bb.Left); bx1 = Math.Max(bx1, bb.Right); by0 = Math.Min(by0, bb.Top); by1 = Math.Max(by1, bb.Bottom); }
        img.ViewBox = new System.Drawing.RectangleF((float)(bx0 - 5), (float)(by0 - 5), (float)(bx1 - bx0 + 10), (float)(by1 - by0 + 10));
        img.Width = (int)Math.Ceiling(bx1 - bx0 + 10); img.Height = (int)Math.Ceiling(by1 - by0 + 10);
        img.SaveSvg(file);
        Console.WriteLine($"render test: {file}");
    }

    static VectorPath ExteriorMm(VectorPath p, double margin) {
        var b = p.ControlBounds();
        double x0 = Math.Min(b.Left, b.Right) - margin, x1 = Math.Max(b.Left, b.Right) + margin;
        double y0 = Math.Min(b.Top, b.Bottom) - margin, y1 = Math.Max(b.Top, b.Bottom) + margin;
        var q = new VectorPath();
        q.MoveTo((float)x0, (float)y0); q.LineTo((float)x1, (float)y0); q.LineTo((float)x1, (float)y1); q.LineTo((float)x0, (float)y1); q.Close();
        var c = new VectorPath(); c.Kinds.AddRange(q.Kinds); c.Kinds.AddRange(p.Kinds);
        c.Data.AddRange(q.Data); c.Data.AddRange(p.Data);
        return c;
    }

    public static void WritePngPairs(string dir, string svgPath) {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const double mmPerUnit = 0.01, pixel = 0.04, tol = 0.04; const double w2 = 1;
        string[] keys = { "eight jagged", "eight smooth", "tangled", "scribble", "A_one_jagged_line", "WAVY_DASH", "circle", "just_straight_line", "OBD", "corners" };

        var doc = System.Xml.Linq.XDocument.Load(svgPath);
        var shapes = new List<(string col, string key, VectorPath path)>();
        foreach (var e in doc.Descendants()) {
            var id = (string)e.Attribute("id");
            if (string.IsNullOrEmpty(id) || id.StartsWith("grid", StringComparison.Ordinal)) continue;
            int u = id.IndexOf('_'); if (u <= 0) continue;
            var geo = SvgGeoMm(e, inv, mmPerUnit); if (geo == null) continue;
            string col = id.Substring(0, u), key = System.Text.RegularExpressions.Regex.Replace(id.Substring(u + 1), "_?x0020_?", " ").Trim();
            if (key.Contains("scalop", StringComparison.Ordinal) || key == "smooth_corner") {
                key = "corners";
                int ex = shapes.FindIndex(s => s.col == col && s.key == key);
                if (ex >= 0) { shapes[ex] = (col, key, CatPath(shapes[ex].path, geo)); continue; }
            }
            shapes.Add((col, key, geo));
        }
        // tangled curve: rendered stroke (green, opaque, masks in bg grey) and baked outline (black) per setting, square 1024 px SVG
        var sh = shapes.Find(x => x.col == "CENTER" && x.key == "tangled");
        var s = sh.path;
        var configs = new (string name, int pos, double w)[] { ("centre_2mm", 0, 2.0), ("outside_1mm", 1, 1.0), ("inside_1mm", 2, 1.0) };
        const int bg = unchecked((int)0xFFCCCCCC), green = unchecked((int)0xFF40E050), black = unchecked((int)0xFF000000);
        foreach (var (name, pos, w) in configs) {
            bool ring = pos != 0;
            double r = ring ? w : w / 2;
            var baked = VectorOffset.Outline(s, r, ring ? pos : 0, pixel, tol);
            float width = (float)(ring ? 2 * w : w);
            var stroke = s;
            // bounds over both, square view box with a 3 mm margin
            var bb = s.ControlBounds();
            var bbb = baked.ControlBounds();
            double x0 = Math.Min(bb.Left, bbb.Left), x1 = Math.Max(bb.Right, bbb.Right), y0 = Math.Min(bb.Top, bbb.Top), y1 = Math.Max(bb.Bottom, bbb.Bottom);
            double side = Math.Max(x1 - x0, y1 - y0) + 6, cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            var vb = new System.Drawing.RectangleF((float)(cx - side / 2), (float)(cy - side / 2), (float)side, (float)side);
            // rendered: the stroke layer alone
            var rimg = new VectorImage();
            rimg.Shapes.Add(new VectorShape { Path = stroke, Fill = null, Stroke = new VectorColor(green), StrokeWidth = width, Cap = VectorCap.Round, Join = VectorJoin.Round, Opacity = 1f });
            if (ring) rimg.Shapes.Add(new VectorShape { Path = pos == 1 ? s : ExteriorMm(s, 3 * w + 2), Fill = new VectorColor(bg), EvenOdd = pos != 1 });
            rimg.ViewBox = vb; rimg.Width = 1024; rimg.Height = 1024;
            RenderPng(rimg, System.IO.Path.Combine(dir, $"tangled_{name}_rendered.png"), bg);
            // baked: the outline object alone, black
            var bimg = new VectorImage();
            bimg.Shapes.Add(new VectorShape { Path = baked, Fill = new VectorColor(black), EvenOdd = false });
            bimg.ViewBox = vb; bimg.Width = 1024; bimg.Height = 1024;
            RenderPng(bimg, System.IO.Path.Combine(dir, $"tangled_{name}_baked.png"), bg);
            Console.WriteLine($"{name}: baked {baked.Kinds.Count} kinds, stroke width {width} mm");
        }
    }

    /// <summary>Draws a view box image through the engine renderer (not ImageMagick, which drops strokes) into a 1024x1024 PNG on a grey background.</summary>
    static void RenderPng(VectorImage img, string file, int background) {
        var canvas = new Sprite(1024, 1024);
        canvas.ClearBuffer(background);
        float k = 1024f / img.ViewBox.Width;
        var m = Matrix3x2.CreateTranslation(-img.ViewBox.X, -img.ViewBox.Y) * Matrix3x2.CreateScale(k);
        img.Draw(canvas, m, new VectorRenderOptions { AA = true });
        canvas.SavePng(file, PngColor.Rgb);
    }

    /// <summary>Table sheet of the demo (outline_test_shapes.svg): every shape in its column (CENTER 2 mm, OUTSIDE 1 mm, INSIDE 1 mm), at true mm scale.
    /// Prints nodes and the deviation from the exact distance for closed shapes. Yellow = base, black = outline, white = its wireframe.</summary>
    public static void WriteTableTest(string file, string svgPath) {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const double mmPerUnit = 0.01, pixel = 0.04, tol = 0.04;
        string[] keys = { "eight jagged", "eight smooth", "tangled", "scribble", "A_one_jagged_line", "WAVY_DASH", "circle", "just_straight_line", "OBD", "corners" };
        var cols = new (string Col, int Pos, double W)[] { ("CENTER", 0, 2.0), ("OUTSIDE", 1, 1.0), ("INSIDE", 2, 1.0) };
        var doc = System.Xml.Linq.XDocument.Load(svgPath);
        var shapes = new List<(string col, string key, VectorPath path)>();
        foreach (var e in doc.Descendants()) {
            var id = (string)e.Attribute("id");
            if (string.IsNullOrEmpty(id) || id.StartsWith("grid", StringComparison.Ordinal)) continue;
            int u = id.IndexOf('_'); if (u <= 0) continue;
            var geo = SvgGeoMm(e, inv, mmPerUnit); if (geo == null) continue;
            string col = id.Substring(0, u), key = System.Text.RegularExpressions.Regex.Replace(id.Substring(u + 1), "_?x0020_?", " ").Trim();
            if (key.Contains("scalop", StringComparison.Ordinal) || key == "smooth_corner") {
                key = "corners";
                int ex = shapes.FindIndex(s => s.col == col && s.key == key);
                if (ex >= 0) { shapes[ex] = (col, key, CatPath(shapes[ex].path, geo)); continue; }
            }
            shapes.Add((col, key, geo));
        }
        double X0 = double.MaxValue, Y0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue;
        var parts = new System.Text.StringBuilder();
        foreach (var key in keys) foreach (var (col, pos, w) in cols) {
            int si = shapes.FindIndex(x => x.col == col && x.key == key);
            if (si < 0) { Console.WriteLine($"missing {col} {key}"); continue; }
            var s = shapes[si];
            bool closed = s.path.Kinds.Contains(VectorPath.K.Close);
            bool ring = pos != 0 && closed;
            double r = ring ? w : w / 2;
            bool dashed = s.key == "WAVY_DASH";
            VectorPath band, chk = s.path; double chkD = ring && pos == 2 ? -r : r; bool skip = ring;
            if (dashed) {
                chk = pos == 0 ? s.path : VectorOffset.Band(s.path, pos == 1 ? w / 2 : -w / 2, pixel, tol);
                band = VectorOffset.Dashed(chk, 5 * w, 3 * w, w, pixel, tol);
                chkD = w / 2; skip = false;
                Console.WriteLine($"  dashes: {band.Kinds.FindAll(kk => kk == VectorPath.K.Close).Count} subpaths");
            } else band = VectorOffset.Outline(s.path, r, ring ? pos : 0, pixel, tol);
            int nodes = 0; foreach (var k in band.Kinds) if (k != VectorPath.K.Close) nodes++;
            string dev;
            var (md, _) = VectorOffset.CheckBand(chk, band, chkD, skipContour: skip); dev = $"{N(md)} mm";
            Console.WriteLine($"{col,-8} {key,-22} ring {(ring ? pos : 0)} width {N(ring ? w : 2 * r)} mm: nodes {nodes,5} dev {dev}");
            var (bx0, by0, bx1, by1) = BoundsMm(s.path);
            X0 = Math.Min(X0, bx0); Y0 = Math.Min(Y0, by0); X1 = Math.Max(X1, bx1); Y1 = Math.Max(Y1, by1);
            var (ox0, oy0, ox1, oy1) = BoundsMm(band);
            X0 = Math.Min(X0, ox0); Y0 = Math.Min(Y0, oy0); X1 = Math.Max(X1, ox1); Y1 = Math.Max(Y1, oy1);
            parts.Append($"  <path d=\"{PathD(s.path, (x, y) => (x, y))}\" fill=\"none\" stroke=\"#ffe600\" stroke-width=\"0.05\"/>\n");
            parts.Append($"  <path d=\"{PathD(band, (x, y) => (x, y))}\" fill=\"#000000\" fill-rule=\"evenodd\" stroke=\"#ffffff\" stroke-width=\"0.03\"/>\n");
            parts.Append($"  <text x=\"{N(bx0)}\" y=\"{N(by0 - 1.5)}\" font-family=\"Arial\" font-size=\"2.5\" fill=\"#ffffff\">{col} {key} ring {(ring ? pos : 0)} {N(ring ? w : 2 * r)} mm: {nodes} nodes</text>\n");
        }
        double m = 5;
        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(X1 - X0 + 2 * m)}mm\" height=\"{N(Y1 - Y0 + 2 * m)}mm\" viewBox=\"{N(X0 - m)} {N(Y0 - m)} {N(X1 - X0 + 2 * m)} {N(Y1 - Y0 + 2 * m)}\">\n");
        sb.Append($"  <rect x=\"{N(X0 - m)}\" y=\"{N(Y0 - m)}\" width=\"{N(X1 - X0 + 2 * m)}\" height=\"{N(Y1 - Y0 + 2 * m)}\" fill=\"#333333\"/>\n");
        sb.Append(parts);
        sb.Append("</svg>\n");
        File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"table test: {file}");
    }

    static VectorPath? SvgGeoMm(System.Xml.Linq.XElement e, System.Globalization.CultureInfo inv, double s) {
        double A(string n) => double.Parse((string)e.Attribute(n)!, inv) * s;
        var p = new VectorPath();
        switch (e.Name.LocalName) {
            case "path": { var q = VectorPath.ParseSvg((string)e.Attribute("d")!); return ScalePath(q, s); }
            case "polygon": case "polyline": {
                var nums = new List<double>();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches((string)e.Attribute("points")!, @"-?\d*\.?\d+(?:[eE][-+]?\d+)?"))
                    nums.Add(double.Parse(m.Value, inv) * s);
                for (int i = 0; i + 1 < nums.Count; i += 2) { if (i == 0) p.MoveTo((float)nums[i], (float)nums[i + 1]); else p.LineTo((float)nums[i], (float)nums[i + 1]); }
                if (e.Name.LocalName == "polygon") p.Close();
                return p; }
            case "line": p.MoveTo((float)A("x1"), (float)A("y1")); p.LineTo((float)A("x2"), (float)A("y2")); return p;
            case "circle": {
                double cx = A("cx"), cy = A("cy"), r = A("r"), k = 0.5523 * r;
                p.MoveTo((float)(cx + r), (float)cy);
                p.CurveTo((float)(cx + r), (float)(cy + k), (float)(cx + k), (float)(cy + r), (float)cx, (float)(cy + r));
                p.CurveTo((float)(cx - k), (float)(cy + r), (float)(cx - r), (float)(cy + k), (float)(cx - r), (float)cy);
                p.CurveTo((float)(cx - r), (float)(cy - k), (float)(cx - k), (float)(cy - r), (float)cx, (float)(cy - r));
                p.CurveTo((float)(cx + k), (float)(cy - r), (float)(cx + r), (float)(cy - k), (float)(cx + r), (float)cy);
                p.Close(); return p; }
        }
        return null;
    }

    static VectorPath ScalePath(VectorPath p, double s) {
        var q = new VectorPath(); q.Kinds.AddRange(p.Kinds);
        foreach (var v in p.Data) q.Data.Add((float)(v * s));
        return q;
    }

    static VectorPath CatPath(VectorPath a, VectorPath b) {
        var q = new VectorPath();
        q.Kinds.AddRange(a.Kinds); q.Kinds.AddRange(b.Kinds);
        q.Data.AddRange(a.Data); q.Data.AddRange(b.Data);
        return q;
    }

    static (double, double, double, double) BoundsMm(VectorPath p) {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (int i = 0; i + 1 < p.Data.Count; i += 2) {
            x0 = Math.Min(x0, p.Data[i]); x1 = Math.Max(x1, p.Data[i]);
            y0 = Math.Min(y0, p.Data[i + 1]); y1 = Math.Max(y1, p.Data[i + 1]);
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>The stroke outline of the source curve as one object (both sides, round joins), 4 mm wide, even-odd: for comparing with Corel's outline convert.</summary>
    public static void WriteStrokeTest(string file)
    {
        string src = System.Environment.GetEnvironmentVariable("CTL_OFFSET_SRC");
        var img = VectorImage.Load(src);
        var head = File.ReadAllText(src);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double widthMm = double.Parse(System.Text.RegularExpressions.Regex.Match(head, "width=\"([0-9.]+)mm\"").Groups[1].Value, inv);
        var contour = Gather(img);
        double k = widthMm / img.Width;                  // mm per gathered unit
        double r = 2.0 / k;                              // 2 mm each side
        var region = VectorOffset.Stroke(contour, r, 0.02 / k, 0.02 / k);
        var (dev, segs) = VectorOffset.CheckBand(contour, region, r);
        Console.WriteLine($"stroke 4 mm: {segs} segments, max deviation from 2 mm {N(dev * k)} mm");
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i + 1 < region.Data.Count; i += 2)
        {
            minX = Math.Min(minX, region.Data[i]); maxX = Math.Max(maxX, region.Data[i]);
            minY = Math.Min(minY, region.Data[i + 1]); maxY = Math.Max(maxY, region.Data[i + 1]);
        }
        double margin = 2, W = (maxX - minX) * k + 2 * margin, H = (maxY - minY) * k + 2 * margin;
        System.Func<float, float, (float, float)> map = (x, y) => ((float)((x - minX) * k + margin), (float)((y - minY) * k + margin));
        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(W)}mm\" height=\"{N(H)}mm\" viewBox=\"0 0 {N(W)} {N(H)}\">\n");
        sb.Append("  <title>Tangled curve: stroke outline 4 mm (2 mm each side), one object</title>\n");
        sb.Append($"  <path id=\"stroke_4mm\" d=\"{PathD(region, map)}\" fill=\"#000000\" fill-rule=\"evenodd\" stroke=\"none\"/>\n");
        sb.Append("</svg>\n");
        File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"stroke test: {file} ({N(W)} x {N(H)} mm)");
    }

    public static void WriteOffsetTest(string file)
    {
        if (System.Environment.GetEnvironmentVariable("CTL_VISUAL") == "1") { WriteVisualTest(file); return; }
        var style = new VectorTextStyle { Bold = true, Color = Black, OutlinePosition = VectorOutlinePosition.Inside, Radius = 0f };
        var img = VectorText.Create("8", 56f, style);
        var contour = new VectorPath();
        foreach (var sh in img.Shapes)
            if (sh.BandOf != null)
            {
                int di = 0; var d = sh.BandOf.Data;
                foreach (var k in sh.BandOf.Kinds)
                    switch (k)
                    {
                        case VectorPath.K.Move: contour.MoveTo(d[di], d[di + 1]); di += 2; break;
                        case VectorPath.K.Line: contour.LineTo(d[di], d[di + 1]); di += 2; break;
                        case VectorPath.K.Cubic: contour.CurveTo(d[di], d[di + 1], d[di + 2], d[di + 3], d[di + 4], d[di + 5]); di += 6; break;
                        case VectorPath.K.Close: contour.Close(); break;
                    }
            }
        var srcFile = System.Environment.GetEnvironmentVariable("CTL_OFFSET_SRC");
        if (!string.IsNullOrEmpty(srcFile)) contour = Gather(VectorImage.Load(srcFile));
        var lobes = VectorOffset.Lobes(contour);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i + 1 < contour.Data.Count; i += 2)
        {
            minX = Math.Min(minX, contour.Data[i]); maxX = Math.Max(maxX, contour.Data[i]);
            minY = Math.Min(minY, contour.Data[i + 1]); maxY = Math.Max(maxY, contour.Data[i + 1]);
        }
        float scale = 30f / (maxY - minY);          // mm per glyph unit
        float unitsPerMm = 1f / scale;
        var inflated = VectorOffset.Inflate(contour, unitsPerMm);
        var deflated = VectorOffset.Inflate(contour, -unitsPerMm);
        const float margin = 5f, gap = 8f;
        float pw = (maxX - minX) * scale + 2 * margin, H = 30f + 2 * margin;
        float W = 3 * pw + 2 * gap;
        var outside = VectorOffset.Inflate(contour, unitsPerMm);
        var inside = VectorOffset.Inflate(contour, -unitsPerMm);
        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(W)}mm\" height=\"{F(H)}mm\" viewBox=\"0 0 {F(W)} {F(H)}\">\n");
        sb.Append("  <title>8: center, outside 1 mm (blue), inside 1 mm (red); black = center curve</title>\n");
        string[] names = { "center", "outside", "inside" };
        string[] colors = { "#000000", "#0000ff", "#ff0000" };
        for (int k = 0; k < 3; k++)
        {
            float ox = k * (pw + gap);
            Func<float, float, (float, float)> map = (x, y) => ((x - minX) * scale + margin + ox, (y - minY) * scale + margin);
            sb.Append($"  <g id=\"{names[k]}\">\n");
            sb.Append($"    <path d=\"{PathD(lobes, map)}\" fill=\"none\" stroke=\"#000000\" stroke-width=\"0.05\"/>\n");
            if (k == 1) sb.Append($"    <path d=\"{PathD(outside, map)}\" fill=\"none\" stroke=\"{colors[1]}\" stroke-width=\"0.05\"/>\n");
            if (k == 2) sb.Append($"    <path d=\"{PathD(inside, map)}\" fill=\"none\" stroke=\"{colors[2]}\" stroke-width=\"0.05\"/>\n");
            sb.Append($"    <text x=\"{F(ox + margin)}\" y=\"{F(H - 1.5f)}\" font-size=\"2.2\" font-family=\"sans-serif\">{names[k]}</text>\n");
            sb.Append("  </g>\n");
        }
        sb.Append("</svg>\n");
        File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"offset test: {file} ({F(W)} x {F(H)} mm, 1 mm = {F(scale)} glyph units)");
    }
}
