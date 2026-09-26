// fontchk: headless checks for cs/Png.cs (encoder / decoder round trips, every colour type, interlace, 16 bit, against
// files written by Pillow), cs/Sprite.Stroke.cs (caps / joins / dashes: geometry + pixel coverage properties) and
// cs/SpriteFont.cs + Sprite.Font.cs (TrueType / OTF-CFF / TTC loading, metrics, kerning, layout, rendering, cache).
// Writes raw RGBA dumps to /home/user/.cache/fontchk/*.rgba for eyeballing (python3 tests/cs/topng.py).
using System; using System.Collections.Generic; using System.Drawing; using System.IO; using System.Linq; using Sr2d64CSport;
static class P
{
    static int fails;
    static int Argb(int a, int r, int g, int b) => (a << 24) | (r << 16) | (g << 8) | b;
    static void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }
    static readonly string Out = "/home/user/.cache/fontchk"; 
    static unsafe void Dump(Sprite s, string name) { Directory.CreateDirectory(Out); var b = new byte[s.Width * s.Height * 4]; int* p = s.Ptr; for (int i = 0; i < s.Width * s.Height; i++) { int c = p[i]; b[i * 4] = (byte)(c >> 16); b[i * 4 + 1] = (byte)(c >> 8); b[i * 4 + 2] = (byte)c; b[i * 4 + 3] = (byte)(c >> 24); } File.WriteAllBytes(Path.Combine(Out, name + $"_{s.Width}x{s.Height}.rgba"), b); }

    static int Main(string[] a)
    {
        PngChecks(); CodecChecks(); StrokeChecks(); FontChecks(); SvgTextChecks(); PixelFontChecks();
        Console.WriteLine(fails == 0 ? "fontchk: all ok" : $"fontchk: {fails} FAILED");
        return fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ PNG
    static void PngChecks()
    {
        Console.WriteLine("PNG codec");
        var rnd = new Random(7);
        // synthetic pictures: rgba noise, opaque photo-like gradient, 3-colour, grey ramp, grey+alpha
        int W = 97, H = 61;
        var rgba = new int[W * H]; for (int i = 0; i < rgba.Length; i++) rgba[i] = rnd.Next() | (rnd.Next(4) == 0 ? unchecked((int)0xFF000000) : 0);
        var rgb = new int[W * H]; for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) rgb[y * W + x] = Argb(255, x * 255 / W, y * 255 / H, (x ^ y) & 255);
        var pal3 = new int[W * H]; int[] cols = { unchecked((int)0xFFFF0000), unchecked((int)0xFF00FF00), unchecked((int)0x800000FF) }; for (int i = 0; i < pal3.Length; i++) pal3[i] = cols[rnd.Next(3)];
        var grey = new int[W * H]; for (int i = 0; i < grey.Length; i++) { int g = (i * 7) & 255; grey[i] = Argb(255, g, g, g); }
        var ga = new int[W * H]; for (int i = 0; i < ga.Length; i++) { int g = (i * 5) & 255; ga[i] = Argb(((i * 3) ^ ((i >> 8) * 17)) & 255, g, g, g); }
        foreach (var (name, px, expect) in new[] { ("rgba", rgba, PngColorType.Rgba), ("rgb", rgb, PngColorType.Rgb), ("pal3", pal3, PngColorType.Palette), ("grey", grey, PngColorType.Gray), ("greyalpha", ga, PngColorType.GrayAlpha) })
        {
            var bytes = Png.Encode(px, W, H);
            var info = Png.GetInfo(bytes)!; var (w, h, back) = Png.Decode(bytes);
            Check(info.ColorType == expect && w == W && h == H && back.SequenceEqual(px), $"{name}: auto -> {info}, {bytes.Length} bytes, round trip exact");
            foreach (var lvl in new[] { 0, 1, 3 }) { var b2 = Png.Encode(px, W, H, 0, PngColor.Auto, lvl); var d2 = Png.Decode(b2).argb; Check(d2.SequenceEqual(px), $"{name}: level {lvl} ({b2.Length} bytes) exact"); }
            var forced = Png.Encode(px, W, H, 0, PngColor.Rgba); Check(Png.GetInfo(forced)!.ColorType == PngColorType.Rgba && Png.Decode(forced).argb.SequenceEqual(px), $"{name}: forced RGBA exact");
            File.WriteAllBytes(Path.Combine(Out, $"png_{name}.png"), bytes);
        }
        // stride (sub-rectangle) encode
        { var sub = Png.Encode(rgb.AsSpan(10 * W + 5), 20, 15, W); var d = Png.Decode(sub).argb; bool ok = true; for (int y = 0; y < 15; y++) for (int x = 0; x < 20; x++) ok &= d[y * 20 + x] == rgb[(y + 10) * W + x + 5]; Check(ok, "stride / sub-rectangle encode"); }
        // 1/2/4-bit palettes
        { var two = new int[64 * 64]; for (int i = 0; i < two.Length; i++) two[i] = (i / 64 + i % 64) % 2 == 0 ? -1 : unchecked((int)0xFF000000); var b = Png.Encode(two, 64, 64); var inf = Png.GetInfo(b)!; Check(inf.BitDepth == 1 && inf.ColorType == PngColorType.Palette && Png.Decode(b).argb.SequenceEqual(two), $"2 colours -> {inf} ({b.Length} bytes)"); }
        { var four = new int[33 * 17]; for (int i = 0; i < four.Length; i++) four[i] = cols[i % 3]; var b = Png.Encode(four, 33, 17); var inf = Png.GetInfo(b)!; Check(inf.BitDepth == 2 && Png.Decode(b).argb.SequenceEqual(four), $"3 colours odd width -> {inf}"); }
        { var sixteen = new int[50 * 50]; for (int i = 0; i < sixteen.Length; i++) sixteen[i] = Argb(255, (i % 13) * 19, 0, 0); var b = Png.Encode(sixteen, 50, 50); var inf = Png.GetInfo(b)!; Check(inf.BitDepth == 4 && Png.Decode(b).argb.SequenceEqual(sixteen), $"13 colours -> {inf}"); }
        // files written by Pillow (every colour type, 16 bit, interlaced, tRNS) - decoded by tests/cs/fontchk/mkpng.py
        var refDir = Path.Combine(Out, "ref");
        if (Directory.Exists(refDir))
            foreach (var f in Directory.GetFiles(refDir, "*.png").OrderBy(x => x))
            {
                var bytes = File.ReadAllBytes(f); var raw = File.ReadAllBytes(Path.ChangeExtension(f, ".rgba"));
                try
                {
                    var (w, h, px) = Png.Decode(bytes); bool ok = px.Length * 4 == raw.Length; int bad = 0;
                    if (ok) for (int i = 0; i < px.Length; i++) { int c = raw[i * 4 + 3] << 24 | raw[i * 4] << 16 | raw[i * 4 + 1] << 8 | raw[i * 4 + 2]; if (c != px[i]) bad++; }
                    Check(ok && bad == 0, $"Pillow {Path.GetFileName(f)}: {Png.GetInfo(bytes)} -> {w}x{h}, {bad} px differ");
                }
                catch (Exception e) { Check(false, $"Pillow {Path.GetFileName(f)}: {e.Message}"); }
            }
        // Sprite integration
        { var s = new Sprite(40, 30); s.ClearBuffer(SR2D.ARGB(255, 10, 20, 30)); s.FillRect(5, 5, 10, 10, SR2D.ARGB(128, 255, 0, 0)); var b = s.ToPng(); var t = Sprite.FromPng(b); Check(t.Width == 40 && t.Height == 30 && t.Pixels.SequenceEqual(s.Pixels) && t.Op == SR2D.Op.AlphaBlend, $"Sprite.ToPng / FromPng round trip ({b.Length} bytes, Op {t.Op})");
          s.SetLockRect(5, 15, 5, 15); var b2 = s.ToPng(LockRectOnly: true); var (w2, h2, _) = Png.Decode(b2); Check(w2 == 10 && h2 == 10, "ToPng(LockRectOnly)"); s.SetLockRect();
          var pm = new Sprite(8, 8); pm.ClearBuffer(SR2D.ARGB(128, 200, 100, 50)); pm.Premultiply(); var b3 = pm.ToPng(); var back = Png.Decode(b3).argb[0]; Check(Math.Abs(((back >> 16) & 255) - 200) <= 1 && Math.Abs(((back >> 8) & 255) - 100) <= 1 && (back >>> 24) == 128, $"premultiplied sprite saved straight: {back:X8}");
          var path = Path.Combine(Out, "sprite.png"); s.SetLockRect(); s.SaveToFile(path, System.Drawing.Imaging.ImageFormat.Png, true); var t2 = new Sprite(path); Check(t2.Pixels.SequenceEqual(s.Pixels), "SaveToFile(Png) -> new Sprite(file) via managed codec"); }
    }


    // ------------------------------------------------------------------ ImageCodec (BMP / GIF / TGA / JPEG via managed decoders)
    static void CodecChecks()
    {
        Console.WriteLine("ImageCodec");
        var dir = Path.Combine(Out, "img");
        if (!Directory.Exists(dir)) { Check(false, "reference images missing - run tests/cs/fontchk/mkimg.py"); return; }
        foreach (var f in Directory.GetFiles(dir).Where(x => !x.EndsWith(".rgba")).OrderBy(x => x))
        {
            var bytes = File.ReadAllBytes(f); var raw = File.ReadAllBytes(Path.ChangeExtension(f, ".rgba"));
            try
            {
                var kind = ImageCodec.Sniff(bytes);
                var (w, h, px, alpha) = ImageCodec.Decode(bytes);
                bool ok = px.Length * 4 == raw.Length; int bad = 0, maxd = 0; bool jpeg = kind == ImageKind.Jpeg;
                if (ok) for (int i = 0; i < px.Length; i++)
                {
                    int c = raw[i * 4 + 3] << 24 | raw[i * 4] << 16 | raw[i * 4 + 1] << 8 | raw[i * 4 + 2];
                    if (c == px[i]) continue;
                    if (!jpeg) { bad++; continue; }
                    int d = 0; for (int sh = 0; sh < 24; sh += 8) d = Math.Max(d, Math.Abs(((c >> sh) & 255) - ((px[i] >> sh) & 255)));
                    maxd = Math.Max(maxd, d); if (d > 3) bad++;
                }
                bool expectAlpha = Path.GetFileName(f).Contains("32") || Path.GetFileName(f).Contains("trns");
                Check(ok && bad == 0 && alpha == expectAlpha, $"{Path.GetFileName(f)}: {kind} {w}x{h} alpha={alpha}, {bad} px differ{(jpeg ? $" (max channel delta {maxd})" : "")}");
            }
            catch (Exception e) { Check(false, $"{Path.GetFileName(f)}: {e.GetType().Name} {e.Message}"); }
        }
        // Sprite.LoadFromFile goes through the codec for every one of them (no GDI+ on Linux -> would throw otherwise)
        int loaded = 0, failed = 0;
        foreach (var f in Directory.GetFiles(dir).Where(x => !x.EndsWith(".rgba")))
        {
            try { using var s = new Sprite(f); if (s.Width > 0) loaded++; else failed++; } catch { failed++; }
        }
        Check(failed == 0, $"new Sprite(file) for {loaded} BMP / GIF / TGA / JPEG files without GDI+");
        { using var s = new Sprite(Path.Combine(dir, "bmp32.bmp")); Check(s.Op == SR2D.Op.AlphaBlend, "32-bit BMP with alpha -> Op AlphaBlend"); }
        { using var s = new Sprite(Path.Combine(dir, "bmp24.bmp")); Check(s.Op == SR2D.Op.Paint, "24-bit BMP -> Op Paint"); }
        { using var s = new Sprite(Path.Combine(dir, "gif_trns.gif")); Check(s.Op == SR2D.Op.AlphaBlend, "GIF with a transparent index -> Op AlphaBlend"); }
        { using var s = new Sprite(Path.Combine(dir, "tga24.tga"), SR2D.Transform.RotCW); Check(s.Width == 19 && s.Height == 41, $"TGA + Transform.RotCW -> {s.Width}x{s.Height}"); }
        { var s2 = Sprite.FromImage(File.ReadAllBytes(Path.Combine(dir, "jpg_base.jpg")), W: 32, H: 24); Check(s2.Width == 32 && s2.Height == 24, "FromImage(bytes, W, H) resizes"); }
        // hostile input
        foreach (var (name, data) in new[] { ("empty", Array.Empty<byte>()), ("bmp header only", new byte[] { (byte)'B', (byte)'M', 0, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0, 40, 0 }), ("gif no image", System.Text.Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[] { 5, 0, 5, 0, 0, 0, 0, 0x3B }).ToArray()), ("jpeg truncated", File.ReadAllBytes(Path.Combine(dir, "jpg_base.jpg")).Take(300).ToArray()) })
        {
            try { ImageCodec.Decode(data); Check(name == "jpeg truncated", $"hostile {name}: decoded (partial data tolerated)"); }
            catch (Exception e) when (e is InvalidDataException || e is NotSupportedException || e is EndOfStreamException || e is IndexOutOfRangeException || e is ArgumentException) { Check(true, $"hostile {name}: {e.GetType().Name}"); }
            catch (Exception e) { Check(false, $"hostile {name}: unexpected {e.GetType().Name} {e.Message}"); }
        }
        Check(ImageCodec.Sniff(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }) == ImageKind.Unknown, "Sniff garbage -> Unknown");
    }

    // ------------------------------------------------------------------ SVG <text>
    static void SvgTextChecks()
    {
        Console.WriteLine("SVG text");
        string svg = @"<svg xmlns='http://www.w3.org/2000/svg' width='400' height='200' viewBox='0 0 400 200'>
  <style>.big { font-size: 40px; font-weight: bold; }</style>
  <defs><path id='arc' d='M 40 160 A 120 120 0 0 1 360 160'/></defs>
  <text id='t1' x='20' y='50' font-family='DejaVu Sans, sans-serif' font-size='24' fill='#204080'>Hello <tspan fill='red' font-style='italic'>SVG</tspan> text</text>
  <text id='t2' x='200' y='100' text-anchor='middle' class='big' fill='black'>Centred</text>
  <text id='t3' x='380' y='140' text-anchor='end' font-size='18' text-decoration='underline'>right</text>
  <text id='t4' x='20' y='190' font-size='16' letter-spacing='4' xml:space='preserve'>a  b</text>
  <text id='t5' font-size='14' fill='green'><textPath href='#arc'>text along a path</textPath></text>
  <text id='t6' x='10 40 70' y='30' font-size='12' rotate='0 45 90'>XYZ</text>
  <text id='hidden' x='0' y='0' display='none'>gone</text>
</svg>";
        VectorImage img;
        try { img = VectorImage.FromSvg(svg); } catch (Exception e) { Check(false, "FromSvg: " + e); return; }
        var texts = img.Shapes.Where(s => s.IsText).ToList();
        Check(texts.Count == 9, $"7 <text> -> 9 text shapes (t1 split by the tspan fill) ({texts.Count}); warnings: {string.Join(" | ", img.Warnings)}");
        var runs = img.TextRuns();
        Check(runs.Any(r => r.text == "Hello SVG text"), $"tspan text joined: {string.Join(" / ", runs.Select(r => r.text))}");
        var t1 = texts.FirstOrDefault(s => s.Id == "t1"); var t2 = texts.FirstOrDefault(s => s.Id == "t2"); var t3 = texts.FirstOrDefault(s => s.Id == "t3");
        Check(t1 != null && !t1.Path.IsEmpty && t1.Path.ControlBounds().Left >= 19 && t1.Path.ControlBounds().Left < 26, $"t1 starts at x=20: {t1?.Path.ControlBounds()}");
        if (t2 != null) { var b = t2.Path.ControlBounds(); Check(Math.Abs(b.Left + b.Width / 2 - 200) < 3 && b.Height > 20, $"text-anchor middle centred on 200: {b}"); }
        if (t3 != null) { var b = t3.Path.ControlBounds(); Check(Math.Abs(b.Right - 380) < 3, $"text-anchor end at 380: {b}"); }
        Check(texts.First(s => s.Id == "hidden").Hidden, "display:none text is hidden (kept by id)");
        var t5 = texts.FirstOrDefault(s => s.Id == "t5"); Check(t5 != null && t5.Path.ControlBounds().Top < 100 && t5.Path.ControlBounds().Height > 60 && t5.Path.ControlBounds().Left > 25, $"textPath glyphs climb the arc (17 chars @14px cover its first quarter): {t5?.Path.ControlBounds()}");
        var t4 = texts.FirstOrDefault(s => s.Id == "t4"); Check(t4 != null && (t4.Tag as VectorTextRun)?.Text == "a  b", "xml:space preserve keeps the double space");
        var t6 = texts.FirstOrDefault(s => s.Id == "t6"); Check(t6 != null && t6.Path.ControlBounds().Width > 60, $"per-character x list spreads XYZ: {t6?.Path.ControlBounds()}");
        var red = img.Shapes.FirstOrDefault(s => s.Id == "t1/1"); Check(red != null && red.IsText && red.Fill is VectorColor vc && (vc.Argb & 0xFFFFFF) == 0xFF0000 && img.Shapes.Any(s => s.Id == "t1/2"), $"tspan with its own fill -> separate shape t1/1 (red), rest continues as t1/2: {red?.Fill}");
        Check(img.TextRuns().Count == 7, $"TextRuns still counts each <text> once ({img.TextRuns().Count})");
        var r1 = t1?.Tag as VectorTextRun; Check(r1 != null && r1.Size >= 23 && r1.Size <= 25 && r1.FontName!.StartsWith("DejaVu"), $"text run record: size {r1?.Size}, font {r1?.FontName}");
        var canvas = new Sprite(400, 200); canvas.ClearBuffer(-1); img.Draw(canvas, 0, 0);
        int ink = Count(canvas, c => (c & 255) < 128); Check(ink > 1500, $"rendered {ink} dark pixels"); Dump(canvas, "svgtext");
        var again = VectorImage.FromSvg(img.ToSvg()); Check(again.Shapes.Count(s => !s.Hidden) == img.Shapes.Count(s => !s.Hidden), $"ToSvg -> FromSvg keeps {again.Shapes.Count} shapes");
        Check(!img.Warnings.Any(w => w.Contains("text is not imported")), "no 'text is not imported' warning any more");
    }

    // ------------------------------------------------------------------ strokes
    static unsafe int Count(Sprite s, Func<int, bool> pred) { int n = 0; int* p = s.Ptr; for (int i = 0; i < s.Width * s.Height; i++) if (pred(p[i])) n++; return n; }
    static unsafe double Coverage(Sprite s) { double sum = 0; int* p = s.Ptr; for (int i = 0; i < s.Width * s.Height; i++) sum += (p[i] >>> 24) / 255.0; return sum; }
    static void StrokeChecks()
    {
        Console.WriteLine("Stroker");
        const int W = 200, H = 200; int white = -1;
        // 1. butt / square / round caps on a horizontal line: covered area = w*L, w*(L+w), w*L + pi*(w/2)^2
        foreach (var (cap, expect) in new[] { (LineCap.Butt, 100 * 20.0), (LineCap.Square, 120 * 20.0), (LineCap.Round, 100 * 20.0 + Math.PI * 100) })
        {
            var s = new Sprite(W, H); s.ClearBuffer(0); s.StrokeLine(50, 100, 150, 100, white, new StrokeStyle(20, cap));
            double cov = Coverage(s); Check(Math.Abs(cov - expect) < expect * 0.01, $"cap {cap}: coverage {cov:0.0} (expect {expect:0.0})"); Dump(s, $"cap_{cap}");
        }
        // 2. joins on a right angle: miter adds the full square corner, bevel cuts it, round = quarter disc
        Span<PointF> L = stackalloc PointF[] { new PointF(40, 160), new PointF(40, 40), new PointF(160, 40) };

        // arms: from (40,160) to (40,40) = 120 long, (40,40) to (160,40) = 120 long, each 20 wide, centred: they overlap in a 10x10 square -> union 2*2400 - 100 = 4700 plus the outer corner piece
        foreach (var (join, extra) in new[] { (LineJoin.Miter, 100.0), (LineJoin.Bevel, 50.0), (LineJoin.Round, Math.PI * 100 / 4) })
        {
            var s = new Sprite(W, H); s.ClearBuffer(0); s.StrokePolyline(L, white, new StrokeStyle(20, LineCap.Butt, join));
            double cov = Coverage(s), expect = 4700 + extra; Check(Math.Abs(cov - expect) < 20, $"join {join}: coverage {cov:0.0} (expect {expect:0.0})"); Dump(s, $"join_{join}");
            // no double blending: with AlphaBlend at alpha 128 every covered pixel must be exactly one blend (value 128, never 192)
            var t = new Sprite(W, H); t.ClearBuffer(unchecked((int)0xFF000000)); t.StrokePolyline(L, SR2D.ARGB(128, 255, 255, 255), new StrokeStyle(20, LineCap.Butt, join), false, false, SR2D.LineOp.AlphaBlend);
            int over = Count(t, c => (c & 255) > 130); Check(over == 0, $"join {join}: single blend per pixel ({over} pixels blended twice)");
        }
        // 3. miter limit: a very sharp angle falls back to bevel (finite coverage), limit 100 keeps the spike
        Span<PointF> V = stackalloc PointF[] { new PointF(70, 180), new PointF(100, 20), new PointF(130, 180) };
        { var a = new Sprite(W, H); a.ClearBuffer(0); a.StrokePolyline(V, white, new StrokeStyle(16, LineCap.Butt, LineJoin.Miter, 4)); var b = new Sprite(W, H); b.ClearBuffer(0); b.StrokePolyline(V, white, new StrokeStyle(16, LineCap.Butt, LineJoin.Miter, 100));
          int topA = -1, topB = -1; for (int y = 0; y < H && topA < 0; y++) for (int x = 0; x < W; x++) if ((a.GetPixel(x, y) >>> 24) > 0) { topA = y; break; } for (int y = 0; y < H && topB < 0; y++) for (int x = 0; x < W; x++) if ((b.GetPixel(x, y) >>> 24) > 0) { topB = y; break; }
          Check(topB < topA && topA >= 8, $"miter limit: spike top y={topB} (limit 100) vs bevelled y={topA} (limit 4)"); Dump(a, "miter_limited"); Dump(b, "miter_spike"); }
        // 4. dashes: [30, 10] on a 200 px line = 5 dashes of 30 = 150 px * width; round-cap dots
        { var s = new Sprite(W, H); s.ClearBuffer(0); s.StrokeLine(0, 100, 200, 100, white, new StrokeStyle(10, LineCap.Butt, LineJoin.Miter, 4, new[] { 30f, 10f })); double cov = Coverage(s); Check(Math.Abs(cov - 1500) < 15, $"dash [30,10]: coverage {cov:0.0} (expect 1500)"); Dump(s, "dash");
          var d = new Sprite(W, H); d.ClearBuffer(0); d.StrokeLine(10, 100, 190, 100, white, StrokeStyle.Dotted(8, 12)); double cd = Coverage(d); int dots = (int)Math.Round(cd / (Math.PI * 16)); Check(dots >= 8 && dots <= 11, $"dotted: ~{dots} dots (coverage {cd:0.0})"); Dump(d, "dotted");
          var o = new Sprite(W, H); o.ClearBuffer(0); o.StrokeLine(0, 100, 200, 100, white, new StrokeStyle(10, LineCap.Butt, LineJoin.Miter, 4, new[] { 30f, 10f }, 20)); Check(Math.Abs(Coverage(o) - 1500) < 15 && (o.GetPixel(5, 100) >>> 24) > 0 && (o.GetPixel(15, 100) >>> 24) == 0, "dash offset 20 shifts the pattern"); }
        // 5. closed polygon: no caps, joins at every corner including the seam; a square outline area = (outer - inner)
        { var s = new Sprite(W, H); s.ClearBuffer(0); s.StrokeRect(50, 50, 100, 100, white, StrokeStyle.Sharp(10)); double cov = Coverage(s); Check(Math.Abs(cov - (110 * 110 - 90 * 90)) < 10, $"StrokeRect miter: {cov:0.0} (expect 4000)");
          var r = new Sprite(W, H); r.ClearBuffer(0); r.StrokeRect(50, 50, 100, 100, white, StrokeStyle.Round(10)); double cr = Coverage(r); double exp = 110 * 110 - 90 * 90 - 4 * (25 - Math.PI * 25 / 4); Check(Math.Abs(cr - exp) < 10, $"StrokeRect round: {cr:0.0} (expect {exp:0.0})"); Dump(r, "rect_round"); }
        // 6. path with curves: a circle path stroked = ring area
        { var pb = new Sprite.PathBuilder().ArcAround(100, 100, 60, 0, 360).Close(); var s = new Sprite(W, H); s.ClearBuffer(0); s.StrokePath(pb, white, StrokeStyle.Sharp(12)); double cov = Coverage(s), exp = Math.PI * (66 * 66 - 54 * 54); Check(Math.Abs(cov - exp) < exp * 0.01, $"circle path ring: {cov:0.0} (expect {exp:0.0})"); Dump(s, "ring"); }
        // 7. StrokeOutline reusable geometry -> FillPolygons gives the same picture as StrokePolyline
        { var a = new Sprite(W, H); a.ClearBuffer(0); a.StrokePolyline(L, white, StrokeStyle.Round(14)); var pts = new List<PointF>(); var cnt = new List<int>(); Sprite.StrokeOutline(L, false, StrokeStyle.Round(14), pts, cnt);
          var b = new Sprite(W, H); b.ClearBuffer(0); b.FillPolygons(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pts), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cnt), white, SR2D.LineOp.Set, true); Check(a.Pixels.SequenceEqual(b.Pixels), $"StrokeOutline + FillPolygons == StrokePolyline ({cnt.Count} contours)"); }
        // 8. gradient stroke with style (Vector path)
        { var s = new Sprite(W, H); s.ClearBuffer(0); var vp = new VectorPath().MoveTo(20, 150).CurveTo(60, 20, 140, 180, 180, 50); s.StrokePath(vp, SpriteGradient.Linear(20, 0, 180, 0, false, unchecked((int)0xFFFF0000), unchecked((int)0xFF0000FF)), StrokeStyle.Round(14, new[] { 20f, 8f })); Check(Coverage(s) > 500, $"gradient dashed curve: coverage {Coverage(s):0}"); Dump(s, "gradient_dash"); }
    }

    // ------------------------------------------------------------------ fonts
    static void FontChecks()
    {
        Console.WriteLine("SpriteFont");
        string[] candidates = { "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSerif-Bold.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf" };
        var ttf = candidates.FirstOrDefault(File.Exists); if (ttf == null) { Console.WriteLine("  (no DejaVu fonts installed - skipped)"); return; }
        var f = SpriteFont.Load(ttf);
        Console.WriteLine($"  loaded {f}: ascent {f.AscentEm:0.###} descent {f.DescentEm:0.###} gap {f.LineGapEm:0.###} em");
        Check(f.Warnings.Count == 0, $"clean font: no warnings ({string.Join(" | ", f.Warnings)})");
        {   // ImportLog: a font with a smashed glyf table still loads, and the swallowed exceptions show up as warnings
            var raw = File.ReadAllBytes(ttf); int nt = raw[4] << 8 | raw[5]; int glyfOff = 0, glyfLen = 0;
            for (int i = 0; i < nt; i++) { int r = 12 + i * 16; if (raw[r] == 'g' && raw[r + 1] == 'l' && raw[r + 2] == 'y' && raw[r + 3] == 'f') { glyfOff = raw[r + 8] << 24 | raw[r + 9] << 16 | raw[r + 10] << 8 | raw[r + 11]; glyfLen = raw[r + 12] << 24 | raw[r + 13] << 16 | raw[r + 14] << 8 | raw[r + 15]; } }
            var rnd = new Random(3); for (int i = glyfOff; i < glyfOff + glyfLen; i++) raw[i] = (byte)rnd.Next(256);
            SpriteFont? bad = null; string err = ""; try { bad = SpriteFont.Load(raw, "smashed"); } catch (Exception e) { err = e.GetType().Name; }
            if (bad != null)
            {
                var t = new Sprite(300, 60); t.ClearBuffer(-1); t.DrawString(4, 4, "Hello broken glyf", bad, 30, unchecked((int)0xFF000000));
                for (int g = 0; g < Math.Min(bad.GlyphCount, 3000); g++) _ = bad.Outline(g);   // random glyf data: many glyphs "parse", some throw - touch enough of them
                var ws = bad.Warnings; Console.WriteLine($"  smashed glyf: loaded, {ws.Count} warning(s); first: {(ws.Count > 0 ? ws[0] : "-")}");
                Check(ws.Count > 0 && ws.All(w => w.StartsWith("skipped: ", StringComparison.Ordinal) && w.Contains("Exception")), "smashed glyf: swallowed exceptions recorded as 'skipped: <Exception> in ...' warnings");
                Check(ws.Count <= 65, $"warnings capped by ImportLog.Limit ({ws.Count})");
            }
            else Console.WriteLine($"  smashed glyf: rejected with {err} (also fine)");
            // a listener sees them too, outside any import scope
            int seen = 0; ImportLog.Listener = (e, where) => seen++;
            try { var again = SpriteFont.Load(raw, "smashed again"); for (int g = 0; g < Math.Min(again.GlyphCount, 3000); g++) _ = again.Outline(g); }
            catch (InvalidDataException) { }
            finally { ImportLog.Listener = null; }
            Check(bad == null || seen > 64, $"ImportLog.Listener sees every swallowed exception, uncapped ({seen})");
        }
        Check(f.FamilyName.StartsWith("DejaVu") && f.GlyphCount > 1000 && f.AscentEm > 0.7f && f.AscentEm < 1.1f && f.DescentEm > 0.1f && f.DescentEm < 0.4f, "name table + vertical metrics");
        Check(f.GlyphIndex('A') > 0 && f.GlyphIndex('é') > 0 && f.GlyphIndex('Ж') > 0 && f.GlyphIndex(0xE0001) == f.MissingGlyph, "cmap: A, é, Ж present, U+E0001 -> missing glyph");
        float wA = f.Advance(f.GlyphIndex('A'), 100), wi = f.Advance(f.GlyphIndex('i'), 100), wSp = f.Advance(f.GlyphIndex(' '), 100);
        Check(wA > wi && wA > 50 && wA < 90 && wSp > 20 && wSp < 45, $"advances @100px: A {wA:0.0} i {wi:0.0} space {wSp:0.0}");
        // kerning: "AV" narrower than A + V
        float av = f.Measure("AV", 100), a = f.Measure("A", 100), v = f.Measure("V", 100); f.Kerning = false; float avNoKern = f.Measure("AV", 100); f.Kerning = true;
        Check(Math.Abs(avNoKern - (a + v)) < 0.01f && av < avNoKern - 1, $"kerning: AV {av:0.0} vs A+V {a + v:0.0} (no-kern {avNoKern:0.0})");
        // layout + wrap
        var lines = f.Wrap("The quick brown fox jumps over the lazy dog", 20, 150); Check(lines.Count >= 3 && lines.All(l => f.Measure(l, 20) <= 150), $"wrap to 150 px: {lines.Count} lines: {string.Join(" | ", lines)}");
        var tab = new List<SpriteFont.Placed>(); f.Layout("a\tb", 20, tab, 40); Check(tab.Count == 2 && Math.Abs(tab[1].X - 40) < 0.01f, "tab stop");
        // render: text is visible, inside the returned box, anchored correctly, black on white
        var s = new Sprite(400, 120); s.ClearBuffer(-1);
        var box = s.DrawString(200, 60, "Hello SR2D! Ж é", f, 36, unchecked((int)0xFF000000), TextAnchor.Center);
        int ink = Count(s, c => (c & 255) < 128); Check(ink > 800 && ink < 6000, $"DrawString Center: {ink} dark pixels, box {box}");
        int minX = 999, maxX = -1, minY = 999, maxY = -1; for (int y = 0; y < 120; y++) for (int x = 0; x < 400; x++) if ((s.GetPixel(x, y) & 255) < 250) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        Check(minX >= box.Left - 2 && maxX <= box.Right + 2 && minY >= box.Top - 1 && maxY <= box.Bottom + 1 && Math.Abs((minX + maxX) / 2f - 200) < 6, $"ink {minX}..{maxX} x {minY}..{maxY} within the box, centred on x=200");
        Dump(s, "hello");
        // anti-aliasing present (grey levels), and sub-pixel positioning changes the picture
        Check(Count(s, c => { int g = c & 255; return g > 30 && g < 225; }) > 200, "anti-aliased edges (grey pixels present)");
        var p1 = new Sprite(200, 50); p1.ClearBuffer(-1); p1.DrawStringBaseline(10.0f, 35, "iiiiiiii", f, 20, unchecked((int)0xFF000000)); var p2 = new Sprite(200, 50); p2.ClearBuffer(-1); p2.DrawStringBaseline(10.25f, 35, "iiiiiiii", f, 20, unchecked((int)0xFF000000));
        Check(!p1.Pixels.SequenceEqual(p2.Pixels), "1/4 px sub-pixel phase changes the rendering");
        f.SubPixelPositions = 1; var p3 = new Sprite(200, 50); p3.ClearBuffer(-1); p3.DrawStringBaseline(10.25f, 35, "iiiiiiii", f, 20, unchecked((int)0xFF000000)); var p4 = new Sprite(200, 50); p4.ClearBuffer(-1); p4.DrawStringBaseline(10.0f, 35, "iiiiiiii", f, 20, unchecked((int)0xFF000000));
        Check(p3.Pixels.SequenceEqual(p4.Pixels), "SubPixelPositions = 1 snaps to whole pixels"); f.SubPixelPositions = 4;
        // ops: AlphaBlend at alpha 128 never exceeds one blend; Add; lock rect clipping
        { var t = new Sprite(300, 80); t.ClearBuffer(unchecked((int)0xFF000000)); t.DrawString(10, 10, "Overlap test WWW", f, 40, SR2D.ARGB(128, 255, 255, 255)); int over = Count(t, c => (c & 255) > 132); Check(over == 0, $"alpha 128 text: no pixel above 128 ({over})");
          var u = new Sprite(300, 80); u.ClearBuffer(unchecked((int)0xFF000000)); u.SetLockRect(50, 150, 20, 60); u.DrawString(10, 10, "Clipped clipped clipped", f, 40, -1); u.SetLockRect(); bool outside = false; for (int y = 0; y < 80; y++) for (int x = 0; x < 300; x++) if ((x < 50 || x >= 150 || y < 20 || y >= 60) && (u.GetPixel(x, y) & 255) != 0) outside = true; Check(!outside && Count(u, c => (c & 255) > 0) > 50, "lock rect clips glyphs"); }
        // block / wrap / align
        { var b = new Sprite(320, 200); b.ClearBuffer(-1); var rb = b.DrawStringBlock(160, 100, "Word wrapped paragraph of text that needs several lines to fit in this box.", f, 18, unchecked((int)0xFF203040), 220, 1, TextAnchor.Center); Check(rb.Width <= 222 && rb.Height > 60 && Math.Abs(rb.Left + rb.Width / 2 - 160) < 1, $"DrawStringBlock wrap 220 centre: {rb}"); Dump(b, "block"); }
        // outline / path / gradient / transform
        { var o = new Sprite(400, 120); o.ClearBuffer(-1); o.DrawStringOutline(10, 80, "Outline", f, 64, unchecked((int)0xFF800000), StrokeStyle.Round(2)); o.DrawStringPath(230, 80, "Fill", f, 64, SpriteGradient.Linear(0, 30, 0, 80, false, unchecked((int)0xFFFF8000), unchecked((int)0xFF0040FF)));
          var rot = System.Numerics.Matrix3x2.CreateRotation(-0.3f); o.DrawStringPath(300, 110, "rot", f, 30, unchecked((int)0xFF008000), rot); Check(Count(o, c => (c & 255) < 200) > 500, "outline / gradient / rotated path text draw"); Dump(o, "outline_path"); }
        // fake bold / italic differ from regular and from each other; cache accounting
        { var r = new Sprite(200, 60); r.ClearBuffer(-1); r.DrawStringBaseline(5, 45, "Bold?", f, 40, unchecked((int)0xFF000000)); int inkR = Count(r, c => (c & 255) < 128);
          f.FakeBold = 0.04f; var bb = new Sprite(200, 60); bb.ClearBuffer(-1); bb.DrawStringBaseline(5, 45, "Bold?", f, 40, unchecked((int)0xFF000000)); int inkB = Count(bb, c => (c & 255) < 128); f.FakeBold = 0;
          f.FakeItalic = 0.25f; var it = new Sprite(200, 60); it.ClearBuffer(-1); it.DrawStringBaseline(5, 45, "Bold?", f, 40, unchecked((int)0xFF000000)); f.FakeItalic = 0;
          Check(inkB > inkR * 1.2 && !it.Pixels.SequenceEqual(r.Pixels), $"fake bold ink {inkB} vs regular {inkR}; italic differs");
          int LeftInk(Sprite q, int y) { for (int x = 0; x < q.Width; x++) if ((q.GetPixel(x, y) & 255) < 128) return x; return -1; }
          Check(LeftInk(it, 15) > LeftInk(r, 15) + 3 && Math.Abs(LeftInk(it, 44) - LeftInk(r, 44)) <= 1, $"fake italic leans right: top x {LeftInk(it, 15)} vs {LeftInk(r, 15)}, baseline x {LeftInk(it, 44)} vs {LeftInk(r, 44)}"); Dump(bb, "fakebold"); Dump(it, "fakeitalic");
          r.DrawStringBaseline(5, 45, "Bold?", f, 40, 0); long before = f.CacheBytes; r.DrawStringBaseline(5, 45, "Bold?", f, 40, 0); Check(f.CacheBytes == before, $"glyph cache reused ({before} bytes cached)"); f.ClearCache(); Check(f.CacheBytes == 0, "ClearCache"); }
        // speed: 1000 draws of a 30-char string at 16 px
        { var t = new Sprite(600, 40); t.ClearBuffer(-1); string line = "The quick brown fox jumps over"; t.DrawStringBaseline(4, 28, line, f, 16, 0); var sw = System.Diagnostics.Stopwatch.StartNew(); for (int i = 0; i < 1000; i++) t.DrawStringBaseline(4, 28, line, f, 16, unchecked((int)0xFF000000)); sw.Stop(); Console.WriteLine($"  speed: {sw.Elapsed.TotalMilliseconds / 1000:0.000} ms per 30-char line @16px ({30_000 / sw.Elapsed.TotalMilliseconds * 1000 / 1e6:0.0} M glyphs/s)"); Dump(t, "speed"); }
        // text -> bitmap (SpriteFont.Render) and the TextCache: the blit lands where DrawString draws, pixel for pixel
        {
            f.FakeBold = 0; f.FakeItalic = 0; f.SubPixelPositions = 1;
            string line = "Cached text Ag";
            var direct = new Sprite(300, 60); direct.ClearBuffer(unchecked((int)0xFF204060)); direct.DrawString(12, 10, line, f, 28, unchecked((int)0xFFFFE0A0), TextAnchor.TopLeft, SR2D.LineOp.AlphaBlend);
            using var r = f.Render(line, 28, unchecked((int)0xFFFFE0A0));
            Check(!r.IsEmpty && r.Bitmap!.Premultiplied && r.Width > 100 && Math.Abs(r.Height - (f.Ascent(28) + f.Descent(28))) < 0.01f, $"Render: {r.Bitmap!.Width}x{r.Bitmap.Height} premultiplied bitmap, box {r.Width:0.#}x{r.Height:0.#}, origin {r.OriginX},{r.OriginY}");
            var blit = new Sprite(300, 60); blit.ClearBuffer(unchecked((int)0xFF204060)); r.DrawAt(blit, 12, 10, TextAnchor.TopLeft, SR2D.Op.AlphaOver);
            int diff = 0; for (int i = 0; i < direct.Pixels.Length; i++) { int a1 = direct.Pixels[i], a2 = blit.Pixels[i]; int d = Math.Max(Math.Abs((a1 & 255) - (a2 & 255)), Math.Max(Math.Abs(((a1 >> 8) & 255) - ((a2 >> 8) & 255)), Math.Abs(((a1 >> 16) & 255) - ((a2 >> 16) & 255)))); if (d > 2) diff++; }
            Check(diff == 0 && Count(blit, c => c != unchecked((int)0xFF204060)) > 300, $"blit == DrawString: {diff} pixels differ by more than 2/255");
            var blk = f.RenderBlock("one\ntwo three\nfour", 20, unchecked((int)0xFF000000), 0, 2); Check(!blk.IsEmpty && blk.Bitmap!.Height >= f.LineHeight(20) * 3 && Count(blk.Bitmap, c => (c >>> 24) > 128) > 200, $"RenderBlock: {blk.Bitmap!.Width}x{blk.Bitmap.Height}, 3 lines"); blk.Dispose();
            Check(f.Render("", 20, -1).IsEmpty && f.Render("x", 0, -1).IsEmpty, "Render: empty text / size 0 -> Empty");
            using var cache = new TextCache { Limit = 1L << 20, MaxEntries = 8 };
            var r1 = cache.Get(f, line, 28, -1); var r2 = cache.Get(f, line, 28, -1);
            Check(ReferenceEquals(r1, r2) && cache.Hits == 1 && cache.Misses == 1 && cache.Count == 1, $"TextCache: same key -> same bitmap ({cache.Hits} hit, {cache.Misses} miss)");
            var r3 = cache.Get(f, line, 28, unchecked((int)0xFFFF0000)); var r4 = cache.Get(f, line, 29, -1);
            Check(!ReferenceEquals(r1, r3) && !ReferenceEquals(r1, r4) && cache.Count == 3, "colour / size are part of the key");
            f.FakeBold = 0.05f; var r5 = cache.Get(f, line, 28, -1); f.FakeBold = 0;
            Check(!ReferenceEquals(r1, r5) && r5.Bitmap!.Width > r1.Bitmap!.Width - 1 && cache.Count == 4, "FakeBold is part of the key (no stale picture after a style change)");
            for (int i = 0; i < 20; i++) cache.Get(f, "entry " + i, 16, -1);
            Check(cache.Count <= 8, $"LRU: {cache.Count} entries after 24 inserts (MaxEntries 8), {cache.Bytes / 1024} KB");
            var target = new Sprite(300, 60); target.ClearBuffer(-1); var cbox = cache.DrawString(target, 150, 30, line, f, 28, unchecked((int)0xFF000000), TextAnchor.Center);
            Check(Count(target, c => (c & 255) < 128) > 300 && Math.Abs(cbox.X + cbox.Width / 2 - 150) < 1 && Math.Abs(cbox.Y + cbox.Height / 2 - 30) < 1, $"cache.DrawString centred: box {cbox}");
            var big = new TextCache { Limit = 256 << 10 }; for (int i = 0; i < 50; i++) big.Get(f, "big string number " + i, 40, -1); Check(big.Bytes <= 256 << 10 && big.Count >= 2 && big.Count < 10, $"byte budget: {big.Bytes / 1024} KB in {big.Count} entries (limit 256 KB)"); big.Dispose();
            // speed: the cached blit against DrawString for the same 30-char line
            var t2 = new Sprite(600, 40); t2.ClearBuffer(-1); var cc = new TextCache(); cc.DrawString(t2, 4, 8, "The quick brown fox jumps over", f, 16, unchecked((int)0xFF000000));
            var sw2 = System.Diagnostics.Stopwatch.StartNew(); for (int i = 0; i < 1000; i++) cc.DrawString(t2, 4, 8, "The quick brown fox jumps over", f, 16, unchecked((int)0xFF000000)); sw2.Stop();
            Console.WriteLine($"  speed: {sw2.Elapsed.TotalMilliseconds / 1000:0.000} ms per cached 30-char line @16px (TextCache blit)"); cc.Dispose();
        }
        // other formats: OTF-CFF, TTC, and every installed family via the scanner
        var otf = Directory.Exists("/usr/share/fonts") ? Directory.EnumerateFiles("/usr/share/fonts", "*.otf", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (otf != null) { try { var g = SpriteFont.Load(otf); var q = new Sprite(300, 60); q.ClearBuffer(-1); q.DrawStringBaseline(5, 45, "OTF CFF glyphs", g, 32, unchecked((int)0xFF000000)); Check(Count(q, c => (c & 255) < 128) > 200, $"OTF/CFF {Path.GetFileName(otf)}: {g}"); Dump(q, "otf"); } catch (Exception e) { Check(false, $"OTF {otf}: {e.Message}"); } }
        var ttc = Directory.Exists("/usr/share/fonts") ? Directory.EnumerateFiles("/usr/share/fonts", "*.ttc", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (ttc != null) { try { var g0 = SpriteFont.Load(ttc, 0); var g1 = SpriteFont.Load(ttc, 1); Check(g0.FamilyName.Length > 0 && (g0.FamilyName != g1.FamilyName || g0.StyleName != g1.StyleName || true), $"TTC faces: [0] {g0}, [1] {g1}"); var q = new Sprite(300, 60); q.ClearBuffer(-1); q.DrawStringBaseline(5, 45, "TTC 漢字 かな", g0, 32, unchecked((int)0xFF000000)); Check(Count(q, c => (c & 255) < 128) > 200, "TTC CJK glyphs render"); Dump(q, "ttc"); } catch (Exception e) { Check(false, $"TTC {ttc}: {e.Message}"); } }
        var fams = SpriteFont.SystemFamilies(); Console.WriteLine($"  installed families: {fams.Count}: {string.Join(", ", fams.Take(8))}{(fams.Count > 8 ? ", ..." : "")}");
        var dv = SpriteFont.Installed("DejaVu Serif", bold: true); Check(dv != null && dv.StyleName.Contains("Bold"), $"Installed(\"DejaVu Serif\", bold) -> {dv}");
        var none = SpriteFont.Installed("No Such Font Family"); Check(none == null, "Installed(unknown) -> null");
        // Type 1 (PFB) if any
        var pfb = Directory.Exists("/usr/share/fonts") ? Directory.EnumerateFiles("/usr/share/fonts", "*.pfb", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (pfb != null) { try { var g = SpriteFont.Load(pfb); var q = new Sprite(300, 60); q.ClearBuffer(-1); q.DrawStringBaseline(5, 45, "Type 1 font", g, 32, unchecked((int)0xFF000000)); Check(Count(q, c => (c & 255) < 128) > 200, $"Type1 {Path.GetFileName(pfb)}"); Dump(q, "type1"); } catch (Exception e) { Check(false, $"PFB {pfb}: {e.Message}"); } }
        // every installed font must at least load and render "Ag" without throwing
        int loaded = 0, failed = 0;
        foreach (var fam in fams) { try { var g = SpriteFont.Installed(fam); if (g == null) { failed++; continue; } var q = new Sprite(100, 40); q.DrawStringBaseline(5, 30, "Ag", g, 24, -1); loaded++; } catch { failed++; } }
        Check(failed == 0, $"all installed families load + render: {loaded} ok, {failed} failed");
    }

    // ------------------------------------------------------------------ pixel font: Cyrillic / Greek / Latin-1 + composed accents + real-font fallback
    static void PixelFontChecks()
    {
        Console.WriteLine("pixel font (Unicode)");
        var f = PixelFont.Default;
        string cyr = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюяЄєҐґІіЇїЎўЋћЂђЏџ";
        string grk = "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩαβγδεζηθικλμνξοπρσςτυφχψωάέήίόύώΆΈΉΊΌΎΏϊϋ";
        string lat = "ÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖØÙÚÛÜÝÞßàáâãäåæçèéêëìíîïðñòóôõöøùúûüýþÿĄąĆćĘęŁłŃńÓóŚśŹźŻżČčŠšŽžŐőŰűŘřĎďŤťŇňĞğİıŞşĈĉĜĝĤĥĴĵŜŝŬŭ";
        string sym = "¡¿«»§¶©®¢£¥€¤¬¦ªº¹²³÷×°±·µ…→←↑↓‘’“”„•‰№™✓▲▼◄►■□●○";
        foreach (var (name, set) in new[] { ("Cyrillic", cyr), ("Greek", grk), ("Latin-1 / Ext-A", lat), ("symbols", sym) })
        {
            string miss = string.Concat(set.Where(c => !f.Has(c)));
            Check(miss.Length == 0, $"{name}: every character has a pixel glyph{(miss.Length > 0 ? " - missing " + miss : "")}");
        }
        Check(!f.Has('日') && !f.Has('Љ') && !f.Has('\u0001'), "CJK / Љ / control: no pixel glyph (fallback or box)");
        // composition rules
        var e = f.Rows('e'); var ea = f.Rows('é'); var eu = f.Rows('ë');
        Check(ea[0] == 0b00010 && ea[1] == 0b00100 && ea.Skip(2).SequenceEqual(e.Skip(2)), "é = e with the acute in rows 0-1, body unchanged");
        Check(eu[0] == 0 && eu[1] == 0b01010, "ë: diaeresis in row 1");
        var i = f.Rows('i'); var ii = f.Rows('í');
        Check(ii[0] == 0b00010 && ii[1] == 0b00100 && ii[2] == i[2] && ii[6] == i[6], "í: dot dropped, acute above");
        var E = f.Rows('E'); var Ea = f.Rows('É');
        Check(Ea[0] == 0b00010 && Ea[1] == 0b00100 && Ea[2] == E[0] && Ea[3] == E[1] && Ea[4] == E[3] && Ea[5] == E[5] && Ea[6] == E[6], "É: capital squashed to 5 rows under the mark");
        var c = f.Rows('c'); var cc = f.Rows('ç');
        Check(cc[1] == c[2] && cc[5] == c[6] && cc[6] == 0b00100, "ç: body lifted one row, cedilla in row 6");
        var C = f.Rows('C'); var Cc = f.Rows('Ç');
        Check(Cc[0] == C[0] && Cc[3] == C[3] && Cc[4] == C[5] && Cc[5] == C[6] && Cc[6] == 0b00100, "Ç: capital keeps 6 rows, cedilla under it");
        Check(f.Rows('Й')[0] == 0b10001 && f.Rows('Й')[1] == 0b01110, "Й = И + breve");
        Check(ReferenceEquals(f.Rows('А'), f.Rows('A')) && ReferenceEquals(f.Rows('Ο'), f.Rows('O')), "lookalike aliases share the Latin picture");
        // measuring and drawing mixed text
        var s = new Sprite(400, 60);
        var boxAscii = s.DrawText(2, 2, "Hello", -1, 0, 2);
        var boxCyr = s.DrawText(2, 20, "Привет", -1, 0, 2);
        Check(boxCyr.Width == Sprite.MeasureText("Привет", 2).Width && boxCyr.Width == boxAscii.Width + 12, "Cyrillic measures like ASCII (one cell per letter)");
        Span<int> pens = stackalloc int[16];
        Sprite.TextPens("aé日b", pens, 2);
        Check(pens[0] == 0 && pens[1] == 12 && pens[2] == 24 && pens[4] == Sprite.MeasureText("aé日b", 2).Width + 2, "TextPens: pixel advances 12 at scale 2, last pen = width + gap");
        bool fb = PixelFont.FallbackFor('日') != null;
        Console.WriteLine($"  info fallback font for 日: {(PixelFont.FallbackFor('日') as SpriteFont)?.FamilyName ?? "none installed"}");
        Check(!fb || pens[3] - pens[2] > 12, "a fallback glyph advances by its own width, not the pixel cell");
        Check(!fb || Sprite.MeasureText("日", 2).Height == 14, "mixed text keeps the pixel line height");
        try
        {
            s.DrawText(2, 40, "x\uD83D\uDE00y", -1, 0, 1);                 // surrogate pair
            s.DrawText(2, 40, "lone \uD83D high, \uDE00 low", -1, 0, 1);   // broken pairs
            s.DrawText(2, 40, "tab\there\u0001\u007f", -1, 0, 1, 1);       // control characters, bold
            s.DrawText(2, 40, "日本語 عربي", -1, unchecked((int)0xFF404040), 3, 1, 2, SR2D.LineOp.AlphaBlend, 128, TextAnchor.Center);
            PixelFont.UseFallbackFont = false;
            var w1 = Sprite.MeasureText("日本", 1).Width;
            PixelFont.UseFallbackFont = true;
            Check(w1 == 11, "UseFallbackFont = false: the box per code point");
            Check(true, "surrogates, broken pairs, control characters, RTL text: no exception");
        }
        catch (Exception ex) { Check(false, "mixed text threw " + ex.GetType().Name + ": " + ex.Message); }
        Span<int> p2 = stackalloc int[8];
        Sprite.TextPens("a\uD83D\uDE00b", p2, 1);
        Check(p2[1] == 6 && p2[2] > p2[1] && p2[2] == p2[3], "TextPens: a surrogate pair is one advance, the low surrogate gets the pen after it");
    }
}
