using System.Linq; using System.Collections.Generic;
// vecrun: vecrun.dll <out dir> <files...>  -> <out>/<name>.rgba (+ prints size, shape count, warnings, timings)
using System; using System.Diagnostics; using System.Drawing; using System.IO; using Sr2d64CSport;
static class Program {
  static void Save(Sprite s, string path){ var px = s.Pixels; var buf = new byte[px.Length*4]; for (int i=0;i<px.Length;i++){int c=px[i]; buf[i*4]=(byte)(c>>16); buf[i*4+1]=(byte)(c>>8); buf[i*4+2]=(byte)c; buf[i*4+3]=(byte)(c>>24);} File.WriteAllBytes(path, buf); }
  // Recolor / SwapColors / Palette / ColorAt: content edit, Version bump, cache re-raster, SVG round trip
  static int RecolorTest(string outDir, string file) {
    int bad = 0; void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) bad++; }
    var img = VectorImage.Load(file);
    var pal = img.Palette(true);
    Console.WriteLine($"{Path.GetFileName(file)}: {img.Shapes.Count} shapes, palette {pal.Count} colours; top: " + string.Join(" ", pal.GetRange(0, Math.Min(6, pal.Count))));
    int a = pal[0].Argb, b = unchecked((int)0xFF00FF88);
    int v0 = img.Version;
    var vs = img.Cached; var cv = new Sprite(300, 300); vs.DrawFit(cv, new RectangleF(0, 0, 300, 300)); int ras0 = vs.Rasterizations;
    int before = 0; foreach (var px in cv.Pixels) if ((px & 0xFFFFFF) == (b & 0xFFFFFF)) before++;
    int n = img.Recolor(a, b);
    Check(n == pal[0].Uses, $"Recolor changed {n} paints (palette said {pal[0].Uses} uses)");
    Check(img.Version == v0 + 1, "Version bumped once");
    var pal2 = img.Palette(true);
    Check(!pal2.Exists(e => (e.Argb & 0xFFFFFF) == (a & 0xFFFFFF)), "old colour gone from the palette");
    Check(pal2.Exists(e => (e.Argb & 0xFFFFFF) == (b & 0xFFFFFF) && e.Uses >= n), "new colour in the palette");
    vs.DrawFit(cv, new RectangleF(0, 0, 300, 300));
    Check(vs.Rasterizations == ras0 + 1, "VectorSprite re-rasterised exactly once");
    int after = 0; foreach (var px in cv.Pixels) if ((px & 0xFFFFFF) == (b & 0xFFFFFF)) after++;
    Check(after > before, $"new colour visible on the raster ({before} -> {after} px)");
    vs.DrawFit(cv, new RectangleF(0, 0, 300, 300));
    Check(vs.Rasterizations == ras0 + 1, "second draw is a blit (no re-raster)");
    // swap back and forth
    int s1 = img.SwapColors(b, a); int s2 = img.SwapColors(b, a);
    Check(s1 == n && s2 == n, $"SwapColors touched {s1} / {s2} paints both times");
    Check(img.Palette(true).Exists(e => (e.Argb & 0xFFFFFF) == (b & 0xFFFFFF)), "after two swaps the picture is back to the recoloured state");
    // keepAlpha: a translucent shape stays translucent
    var t = new VectorImage(10, 10); t.Add(new VectorPath().Rect(0, 0, 10, 10), unchecked((int)0x80FF0000));
    t.Recolor(unchecked((int)0xFFFF0000), unchecked((int)0xFF0000FF));
    Check(((VectorColor)t.Shapes[0].Fill!).Argb == unchecked((int)0x800000FF), "keepAlpha: alpha 0x80 kept, RGB replaced (matched ignoring alpha)");
    t.Recolor(unchecked((int)0x800000FF), unchecked((int)0xFF00FF00), keepAlpha: false);
    Check(((VectorColor)t.Shapes[0].Fill!).Argb == unchecked((int)0xFF00FF00), "keepAlpha false: alpha written");
    // tolerance
    var t2 = new VectorImage(10, 10); t2.Add(new VectorPath().Rect(0, 0, 10, 10), unchecked((int)0xFF101010)); t2.Add(new VectorPath().Rect(0, 0, 5, 5), unchecked((int)0xFF404040));
    Check(t2.Recolor(unchecked((int)0xFF000000), unchecked((int)0xFFFFFFFF), tolerance: 20) == 1, "tolerance 20 catches #101010 but not #404040");
    // default shared Black instance must not leak edits between images
    var d1 = new VectorImage(4, 4); d1.Shapes.Add(new VectorShape { Path = new VectorPath().Rect(0, 0, 4, 4) });
    var d2 = new VectorImage(4, 4); d2.Shapes.Add(new VectorShape { Path = new VectorPath().Rect(0, 0, 4, 4) });
    d1.Recolor(unchecked((int)0xFF000000), unchecked((int)0xFFFF0000));
    Check(((VectorColor)d2.Shapes[0].Fill!).Argb == unchecked((int)0xFF000000) && VectorColor.Black.Argb == unchecked((int)0xFF000000), "shared default Black untouched by an edit of another image");
    // ColorAt + SVG round trip
    var mid = new PointF(img.ViewBox.X + img.Width / 2, img.ViewBox.Y + img.Height / 2);
    Console.WriteLine($"ColorAt(centre) = {(img.ColorAt(mid) is int cc ? cc.ToString("X8") : "null")}");
    string svg = Path.Combine(outDir, "recolored.svg"); img.SaveSvg(svg);
    var back = VectorImage.Load(svg);
    Check(back.Palette(true).Exists(e => (e.Argb & 0xFFFFFF) == (b & 0xFFFFFF)), "SaveSvg -> Load keeps the new colour");
    Save(cv, Path.Combine(outDir, "recolored.rgba"));

    // ---- target: fill / stroke / both
    var ft = new VectorImage(20, 20);
    ft.Add(new VectorPath().Rect(0, 0, 10, 10), unchecked((int)0xFFFF0000)); ft.Shapes[0].Stroke = new VectorColor(unchecked((int)0xFFFF0000)); ft.Shapes[0].Id = "boxA";
    ft.Add(new VectorPath().Rect(10, 10, 10, 10), unchecked((int)0xFFFF0000)); ft.Shapes[1].Id = "boxB";
    int red = unchecked((int)0xFFFF0000), blu = unchecked((int)0xFF0000FF), grn = unchecked((int)0xFF00FF00);
    Check(ft.Palette(target: PaintTarget.Stroke).Count == 1 && ft.Palette(target: PaintTarget.Stroke)[0].Uses == 1, "Palette(Stroke) sees the one outline");
    Check(ft.Recolor(red, blu, target: PaintTarget.Stroke) == 1 && ((VectorColor)ft.Shapes[0].Stroke!).Argb == blu && ((VectorColor)ft.Shapes[0].Fill!).Argb == red, "Recolor(target: Stroke) changes only the outline");
    Check(ft.Recolor(red, grn, target: PaintTarget.Fill) == 2 && ((VectorColor)ft.Shapes[0].Stroke!).Argb == blu && ((VectorColor)ft.Shapes[1].Fill!).Argb == grn, "Recolor(target: Fill) changes only fills");
    Check(ft.SwapColors(grn, blu, target: PaintTarget.Both) == 3, "SwapColors(Both) touches all three paints");
    // ---- names: recolor / set / hide / remove / wildcards / groups
    Check(ft.Recolor("boxB", blu, red) == 1 && ((VectorColor)ft.Shapes[1].Fill!).Argb == red && ((VectorColor)ft.Shapes[0].Fill!).Argb == blu, "Recolor(name) only touches the named shape");
    Check(ft.SetStroke("box*", grn, 3) == 2 && ft.Shapes[1].Stroke != null && ft.Shapes[1].StrokeWidth == 3, "SetStroke with * wildcard (2 shapes, width set)");
    Check(ft.SetFill("boxA", 0) == 1 && ft.Shapes[0].Fill == null, "SetFill alpha 0 = no fill");
    Check(ft.Hide("boxA") == 1 && ft.Shapes[0].Hidden && !ft.Shapes[0].IsVisible && ft.Shapes.Count == 2, "Hide: flag set, not visible, still in the container");
    var hs = new Sprite(20, 20); ft.Draw(hs, 0, 0); bool anyTopLeft = false; for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) if ((hs.GetPixel(x, y) >> 24 & 255) != 0) anyTopLeft = true;
    Check(!anyTopLeft, "hidden shape is not drawn");
    string hsvg = Path.Combine(outDir, "hidden.svg"); ft.SaveSvg(hsvg); var hb = VectorImage.Load(hsvg);
    Check(hb.Shapes.Count == 2 && hb.Shapes[0].Hidden && hb.Shapes[0].Id == "boxA" && !hb.Shapes[1].Hidden, "SaveSvg writes display=none; Load brings the hidden shape back by name");
    Check(hb.Show("boxA") == 1 && hb.Shapes[0].IsVisible, "Show restores it");
    Check(ft.Names().Count == 2 && ft.IndexOf("boxB") == 1, "Names / IndexOf");
    Check(ft.Remove("boxA") == 1 && ft.Shapes.Count == 1 && ft.Shapes[0].Id == "boxB", "Remove drops the shape");
    Check(ft.Remove("nothing") == 0 && ft.Hide("nothing") == 0, "unknown names change nothing");
    // group ids reach children; hidden groups with an id import Hidden
    var gsvg = VectorImage.Load(System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'><g id='wheel'><rect x='0' y='0' width='5' height='5'/><rect id='hub' x='5' y='5' width='5' height='5'/></g><g id='guides' display='none'><rect x='0' y='0' width='20' height='20'/></g><rect x='10' y='10' width='5' height='5' style='display:none'/></svg>"), ".svg");
    Check(gsvg.Shapes.Count == 3, $"group import: 3 shapes (hidden unnamed rect dropped as before), got {gsvg.Shapes.Count}");
    Check(gsvg.Shapes[0].Id == "wheel/0" && gsvg.Shapes[1].Id == "hub", "children of a named group are 'group/n', own ids win");
    int wheelN = 0; foreach (var sh in gsvg.Named("wheel")) wheelN++;
    Check(wheelN == 2, $"Named(group) addresses every child ({wheelN})");
    Check(gsvg.Shapes[2].Hidden && gsvg.Shapes[2].Id == "guides/0" && gsvg.Show("guides") == 1, "display:none group with an id is imported hidden and can be shown");
    Check(gsvg.Names().Count == 3 && gsvg.Names()[0] == "wheel" && gsvg.Names()[2] == "guides", "Names lists group heads once");
    Check(gsvg.BringToFront("wheel") == 2 && gsvg.Shapes[2].Id == "hub" && gsvg.SendToBack("hub") == 1 && gsvg.Shapes[0].Id == "hub", "BringToFront / SendToBack");
    // ---- merge + shuffle
    var bottom = new VectorImage(100, 100); bottom.Add(new VectorPath().Rect(0, 0, 100, 100), red); bottom.Shapes[0].Id = "bg";
    var top = new VectorImage(10, 10); top.Add(new VectorPath().Rect(0, 0, 10, 10), blu); top.Shapes[0].Id = "bg";
    var merged = VectorImage.Merge(bottom, top, fit: new RectangleF(50, 50, 50, 50));
    Check(merged.Shapes.Count == 2 && bottom.Shapes.Count == 1 && merged.Shapes[1].Id == "bg#2", "static Merge: new image, source untouched, clashing name suffixed");
    var ms = new Sprite(100, 100); merged.Draw(ms, 0, 0);
    Check((ms.GetPixel(75, 75) & 0xFFFFFF) == (blu & 0xFFFFFF) && (ms.GetPixel(25, 25) & 0xFFFFFF) == (red & 0xFFFFFF), "top picture fitted into the rectangle, drawn above");
    int mv = bottom.Version; bottom.Merge(top);
    Check(bottom.Shapes.Count == 2 && bottom.Version > mv, "instance Merge appends + bumps Version");
    var shuf = new VectorImage(10, 10); for (int i = 0; i < 30; i++) { shuf.Add(new VectorPath().Rect(i, 0, 1, 1), red); shuf.Shapes[i].Id = "s" + i; }
    var order1 = new List<string>(); shuf.Shuffle(7); foreach (var sh in shuf.Shapes) order1.Add(sh.Id!);
    var shuf2 = new VectorImage(10, 10); for (int i = 0; i < 30; i++) { shuf2.Add(new VectorPath().Rect(i, 0, 1, 1), red); shuf2.Shapes[i].Id = "s" + i; }
    var order2 = new List<string>(); shuf2.Shuffle(7); foreach (var sh in shuf2.Shapes) order2.Add(sh.Id!);
    bool same = true, moved = false; for (int i = 0; i < 30; i++) { if (order1[i] != order2[i]) same = false; if (order1[i] != "s" + i) moved = true; }
    order1.Sort(); bool all = true; for (int i = 0; i < 30; i++) if (!order1.Contains("s" + i)) all = false;
    Check(same && moved && all && shuf.Shapes.Count == 30, "Shuffle(seed): deterministic, permutes, loses nothing");
    var tigerMerge = img.Clone(); int tc = tigerMerge.Shapes.Count; tigerMerge.Merge(img, shuffle: true, seed: 3);
    Check(tigerMerge.Shapes.Count == tc * 2, $"Merge(shuffle) of the file onto itself: {tigerMerge.Shapes.Count} shapes");

    Console.WriteLine(bad == 0 ? "recolor: all checks passed" : $"recolor: {bad} FAILED");
    return bad;
  }
  // VECOBJECTS=1: object names (Objects / NameByKind / importer names) + visible area (SetVisibleArea / CropToVisibleArea)
  static int ObjectsTest(string outDir, string[] files) {
    int bad = 0; void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) bad++; }
    foreach (var f in files) {
      var img = VectorImage.Load(f);
      var objs = img.Objects();
      int named = 0; foreach (var o in objs) if (!o.Name.StartsWith("[")) named++;
      var kinds = new Dictionary<string, int>(); foreach (var o in objs) kinds[o.Kind] = (kinds.TryGetValue(o.Kind, out int k) ? k : 0) + 1;
      var groups = new HashSet<string>(); foreach (var s in img.Shapes) if (s.Group != null) groups.Add(s.Group);
      Console.WriteLine($"{Path.GetFileName(f)}: {objs.Count} objects, {named} named by the file, kinds {string.Join(" ", kinds.Select(kv => kv.Key + "=" + kv.Value))}; groups: {string.Join(", ", groups.Take(6))}{(groups.Count > 6 ? " ..." : "")}");
      foreach (var o in objs.Take(5)) Console.WriteLine("    " + o);
      int n = img.NameByKind(); var names = img.Names(true);
      Check(n == objs.Count - named && img.Shapes.TrueForAll(s => !string.IsNullOrEmpty(s.Id)), $"NameByKind named {n} (all shapes have an Id now)");
      Check(img.NameByKind() == 0, "second NameByKind names nothing");
      var dup = names.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
      Check(dup.Count == 0 || dup.All(d => img.Shapes.Count(s => string.Equals(s.Id, d, StringComparison.OrdinalIgnoreCase)) <= 1), "auto names are unique");
    }
    // visible area on a synthetic picture: 3 squares, window on the middle one
    var v = new VectorImage(300, 100);
    v.Add(new VectorPath().Rect(0, 0, 100, 100), unchecked((int)0xFFFF0000)); v.Shapes[0].Id = "left";
    v.Add(new VectorPath().Rect(100, 0, 100, 100), unchecked((int)0xFF00FF00)); v.Shapes[1].Id = "mid";
    v.Add(new VectorPath().Rect(200, 0, 100, 100), unchecked((int)0xFF0000FF)); v.Shapes[2].Id = "right";
    v.Add(new VectorPath().Circle(150, 50, 60), unchecked((int)0x80FFFFFF)); v.Shapes[3].Id = "disc";   // spills over the window on both sides
    var c = new Sprite(300, 100); c.ClearBuffer(unchecked((int)0xFF000000));
    v.Draw(c, 0, 0); Check((c.Pixels[50 * 300 + 20] & 0xFFFFFF) == 0xFF0000 && (c.Pixels[50 * 300 + 280] & 0xFFFFFF) == 0x0000FF, "no window: left red, right blue");
    Check(v.SetVisibleArea("mid"), "SetVisibleArea(\"mid\") found the object");
    Check(v.VisibleArea == new RectangleF(100, 0, 100, 100) && v.Width == 300, "window = mid's box, view box unchanged");
    c.ClearBuffer(unchecked((int)0xFF000000)); v.Draw(c, 0, 0);
    Check((c.Pixels[50 * 300 + 20] & 0xFFFFFF) == 0 && (c.Pixels[50 * 300 + 280] & 0xFFFFFF) == 0 && (c.Pixels[50 * 300 + 150] & 0xFFFFFF) != 0, "axis-aligned draw: outside the window stays black, inside painted");
    Check(v.HitTest(new PointF(20, 50)) < 0 && v.HitTest(new PointF(150, 50)) == 3, "HitTest respects the window");
    Check(v.Bounds() == new RectangleF(100, 0, 100, 100), $"Bounds() clipped to the window: {v.Bounds()}");
    // rotated draw: mask path
    var c2 = new Sprite(400, 400); c2.ClearBuffer(unchecked((int)0xFF000000));
    v.Draw(c2, 200, 200, 1f, 1f, 45f);
    int lit = 0; foreach (var p in c2.Pixels) if ((p & 0xFFFFFF) != 0) lit++;
    Check(lit > 100 * 100 * 0.9 && lit < 100 * 100 * 1.15, $"rotated draw paints ~the window only ({lit} px, window = 10000)");
    // hide the frame object and crop
    v.Hide("mid"); v.CropToVisibleArea();
    Check(v.Width == 100 && v.Height == 100 && v.ViewBox == new RectangleF(100, 0, 100, 100), "CropToVisibleArea: size = window");
    var r = v.Rasterize(1f); Check(r.Width == 100 && r.Height == 100 && (r.Pixels[50 * 100 + 50] & 0xFFFFFF) != 0 && (r.Pixels[2 * 100 + 2] & 0xFFFFFF) == 0, "Rasterize of the cropped picture: disc in the middle, mid hidden -> corner empty");
    // cache honours it
    var vs = new VectorSprite(v); var c3 = new Sprite(300, 300); c3.ClearBuffer(0); vs.Draw(c3, 150, 150, 1f, 1f, 0f);
    Check(vs.Raster != null && vs.Raster.Width <= 104 && vs.Raster.Height <= 104, $"VectorSprite raster = window size ({vs.Raster?.Width}x{vs.Raster?.Height})");
    // svg round trip
    string svg = Path.Combine(outDir, "window.svg"); v.SaveSvg(svg); var back = VectorImage.Load(svg);
    Check(back.VisibleArea == v.VisibleArea && back.Width == 100, "SaveSvg / Load keeps the visible area");
    v.ClearVisibleArea(); Check(v.VisibleArea == null && v.Width == 100, "ClearVisibleArea keeps the cropped view box");
    // by name with crop flag, one call
    var w = new VectorImage(300, 100); w.Add(new VectorPath().Rect(0, 0, 100, 100), unchecked((int)0xFFFF0000)); w.Add(new VectorPath().Rect(120, 20, 60, 60), unchecked((int)0xFF00FF00)); w.Shapes[1].Id = "frame";
    Check(w.SetVisibleArea("frame", crop: true, margin: 10) && w.Width == 80 && w.ViewBox.X == 110, "SetVisibleArea(name, crop: true, margin: 10)");
    Check(!w.SetVisibleArea("nothing"), "unknown name -> false");
    // visual: the first file, whole (left) / windowed to its centre third, rotated 20 deg (middle) / cropped + cached (right) -> window.rgba 900x300
    if (files.Length > 0) {
      var pic = VectorImage.Load(files[0]); var cv = new Sprite(900, 300); cv.ClearBuffer(unchecked((int)0xFF303438));
      pic.DrawFit(cv, new RectangleF(10, 10, 280, 280));
      var vb = pic.ViewBox; pic.SetVisibleArea(vb.X + vb.Width / 3, vb.Y + vb.Height / 3, vb.Width / 3, vb.Height / 3);
      float sc = 280f / Math.Max(vb.Width, vb.Height);
      pic.Draw(cv, 450, 150, sc, sc, 20f);
      pic.CropToVisibleArea(); pic.Cached.DrawFit(cv, new RectangleF(610, 10, 280, 280));
      Save(cv, Path.Combine(outDir, "window.rgba")); Console.WriteLine("wrote window.rgba 900x300");
    }
    Console.WriteLine(bad == 0 ? "objects: all checks passed" : $"objects: {bad} FAILED");
    return bad;
  }
  static int Main(string[] args) {
    string outDir = args[0]; Directory.CreateDirectory(outDir);
    if (Environment.GetEnvironmentVariable("VECCACHE") != null) { CacheTest(outDir, args.Length > 1 ? args[1] : null); return 0; }
    if (Environment.GetEnvironmentVariable("VECRECOLOR") != null) return RecolorTest(outDir, args[1]);
    if (Environment.GetEnvironmentVariable("VECOBJECTS") != null) return ObjectsTest(outDir, args.Skip(1).ToArray());
    if (Environment.GetEnvironmentVariable("VECPAGE") is string pg) {   // VECPAGE=<page|all> VECW=<width>: render PDF pages one to one (white background) -> <name>_p<N>.rgba
      int width = int.TryParse(Environment.GetEnvironmentVariable("VECW"), out int vw) ? vw : 800; int fails2 = 0;
      for (int i = 1; i < args.Length; i++) {
        string f = args[i]; int pages = 1; try { pages = VectorImage.PdfPageCount(f); } catch { }
        int from = pg == "all" ? 0 : int.Parse(pg), to = pg == "all" ? pages - 1 : int.Parse(pg);
        for (int p = from; p <= to && p < pages; p++) {
          try {
            var sw = Stopwatch.StartNew(); var img = VectorImage.Load(File.ReadAllBytes(f), Path.GetExtension(f), p); long tLoad = sw.ElapsedMilliseconds;
            int h = Math.Max(1, (int)Math.Round(width * img.Height / Math.Max(1f, img.Width)));
            var canvas = new Sprite(width, h); canvas.ClearBuffer(unchecked((int)0xFFFFFFFF));
            sw.Restart(); img.DrawFit(canvas, new System.Drawing.Rectangle(0, 0, width, h)); long tDraw = sw.ElapsedMilliseconds;
            int text = 0, images = 0; foreach (var sh in img.Shapes) { if (sh.IsText) text++; if (sh.IsImage) images++; }
            Console.WriteLine($"{Path.GetFileName(f)} page {p + 1}/{pages}: {img.Width:0.#}x{img.Height:0.#} shapes={img.Shapes.Count} (text runs {text}, images {images}) load={tLoad} ms draw={tDraw} ms");
            foreach (var w in img.Warnings) Console.WriteLine("   warn: " + w);
            if (Environment.GetEnvironmentVariable("VECTEXT") != null) Console.WriteLine(img.Text());
            if (Environment.GetEnvironmentVariable("VECIMG") != null) { int k = 0; foreach (var (ip, bb) in img.Images()) { File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + $"_p{p + 1}_img{k++}.png"), ip.ToPng()); Console.WriteLine($"   image {ip.Width}x{ip.Height} at {bb} alpha={ip.HasAlpha}"); } }
            if (Environment.GetEnvironmentVariable("VECDUMP") != null) foreach (var sh in img.Shapes) { if (sh.IsText) Console.WriteLine($"   text '{sh.Tag}' bounds={sh.Path.ControlBounds()}"); if (sh.IsImage) Console.WriteLine($"   image {sh.Fill} bounds={sh.Path.ControlBounds()}"); }
            Save(canvas, Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + $"_p{p + 1}.rgba")); Console.WriteLine($"   -> {width}x{h}");
          } catch (Exception ex) { fails2++; Console.WriteLine($"{f} page {p + 1}: FAIL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        }
      }
      return fails2;
    }
    int fails = 0;
    for (int i = 1; i < args.Length; i++) {
      string f = args[i]; string name = Path.GetFileNameWithoutExtension(f) + "_" + Path.GetExtension(f).TrimStart('.');
      try {
        var sw = Stopwatch.StartNew();
        var img = VectorImage.Load(f);
        long tLoad = sw.ElapsedMilliseconds;
        var b = img.Bounds();
        Console.WriteLine($"{Path.GetFileName(f)}: {img.Format} {img.Width:0.#}x{img.Height:0.#} viewBox={img.ViewBox} shapes={img.Shapes.Count} bounds={b} pages={img.PageCount} load={tLoad} ms");
        foreach (var w in img.Warnings) Console.WriteLine("   warn: " + w);
        if (Environment.GetEnvironmentVariable("VECDUMP") != null) foreach (var sh in img.Shapes) Console.WriteLine($"   shape fill={(sh.Fill is VectorColor fc ? fc.Argb.ToString("X8") : sh.Fill?.GetType().Name ?? "-")} stroke={(sh.Stroke is VectorColor sc ? sc.Argb.ToString("X8") : sh.Stroke?.GetType().Name ?? "-")} w={sh.StrokeWidth} clip={(sh.Clip != null)} bounds={sh.Path.ControlBounds()}");
        // 1) fit into 600x450 canvas with chequer background (AlphaBlend path), 2) rotated + squished, 3) Rasterize to premultiplied
        var canvas = new Sprite(600, 450);
        for (int y = 0; y < 450; y++) for (int x = 0; x < 600; x++) canvas.Pixels[y*600+x] = ((x>>4)+(y>>4)&1)==0 ? unchecked((int)0xFF3A3F46) : unchecked((int)0xFF2B2F35);
        sw.Restart(); img.DrawFit(canvas, new System.Drawing.Rectangle(10, 10, 380, 430)); long tDraw = sw.ElapsedMilliseconds;
        sw.Restart(); img.Draw(canvas, 490, 120, 0.35f, 0.25f, 30f); long tRot = sw.ElapsedMilliseconds;
        sw.Restart(); var small = img.Rasterize(180, 180, 0, true); long tRas = sw.ElapsedMilliseconds;
        small.Op = SR2D.Op.AlphaOver; canvas.Draw(small, 410, 260);
        Console.WriteLine($"   fit={tDraw} ms rot={tRot} ms raster={tRas} ms hit(200,200)={img.HitTest(new System.Drawing.PointF(img.ViewBox.X + img.Width*0.5f, img.ViewBox.Y + img.Height*0.5f))}");
        if (Environment.GetEnvironmentVariable("VECPROF") != null)
        {
          var m = img.FitMatrix(new System.Drawing.RectangleF(10, 10, 380, 430));
          foreach (var (label, opt) in new[] { ("fills only", new VectorRenderOptions { Strokes = false }), ("strokes only", new VectorRenderOptions { Fills = false }), ("no AA", new VectorRenderOptions { AA = false }), ("all", new VectorRenderOptions()) })
          { img.Draw(canvas, m, opt); sw.Restart(); for (int k = 0; k < 5; k++) img.Draw(canvas, m, opt); Console.WriteLine($"   {label}: {sw.Elapsed.TotalMilliseconds / 5:F1} ms"); }
        }
        Save(canvas, Path.Combine(outDir, name + ".rgba"));
        if (args.Length > 2 && Path.GetExtension(f) == ".svg") { var back = img.ToSvg(); File.WriteAllText(Path.Combine(outDir, name + "_roundtrip.svg"), back); }
      } catch (Exception ex) { fails++; Console.WriteLine($"{f}: FAIL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }
    return fails;
  }

  // VECCACHE=1 vecrun.dll <outDir> [file]: VectorSprite cache timing + gradient fills -> cache.rgba (900x600)
  static void CacheTest(string outDir, string? file) {
    var img = file != null ? VectorImage.Load(file) : VectorImage.Load("/home/user/SR2D/tests/vec/tiger.svg");
    var c = new Sprite(900, 600); c.ClearBuffer(unchecked((int)0xFF303438));
    var vs = new VectorSprite(img);
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < 20; i++) img.Draw(c, 220, 300, 0.4f, 0.4f, 15f);
    double direct = sw.Elapsed.TotalMilliseconds / 20; sw.Restart();
    vs.Draw(c, 220, 300, 0.4f, 0.4f, 15f); double first = sw.Elapsed.TotalMilliseconds; sw.Restart();
    for (int i = 0; i < 20; i++) vs.Draw(c, 220 + i, 300, 0.4f, 0.4f, 15f);
    double cached = sw.Elapsed.TotalMilliseconds / 20;
    Console.WriteLine($"{img.Shapes.Count} shapes: direct {direct:0.00} ms/frame, VectorSprite first {first:0.00} ms, then {cached:0.000} ms/frame (raster {vs.Raster?.Width}x{vs.Raster?.Height}); rasterizations {vs.Rasterizations} hits {vs.CacheHits}");
    vs.Draw(c, 220, 300, 0.4f, 0.4f, 16f); Console.WriteLine($"  after angle change: rasterizations {vs.Rasterizations}");
    img.Touch(); vs.Draw(c, 220, 300, 0.4f, 0.4f, 16f); Console.WriteLine($"  after Touch: rasterizations {vs.Rasterizations}");
    // gradient fills
    var g1 = SpriteGradient.Linear(0, 0, 1, 0, unchecked((int)0xFF3399FF), unchecked((int)0xFFFF6030));
    c.FillRect(480, 20, 200, 60, g1);
    c.FillRoundRect(700, 20, 180, 60, 16, SpriteGradient.Angle(90, unchecked((int)0xFFFFFFFF), unchecked((int)0xFF204080)));
    c.FillCircle(560, 180, 70, SpriteGradient.RadialFocus(0.5f, 0.5f, 0.5f, 0.35f, 0.3f, true, unchecked((int)0xFFFFFFFF), unchecked((int)0xFFE04040), unchecked((int)0xFF400000)));
    var pb = new Sprite.PathBuilder(); pb.MoveTo(700, 120).LineTo(880, 140).LineTo(840, 250).LineTo(720, 230).Close();
    c.FillPath(pb, SpriteGradient.Linear(0, 0, 1, 1, unchecked((int)0xFF80FF80), unchecked((int)0xFF004000)).Spread(VectorSpread.Reflect).Stops((0f, unchecked((int)0xFF80FF80)), (0.5f, unchecked((int)0xFFFFFF00)), (1f, unchecked((int)0xFF004000))));
    var pb2 = new Sprite.PathBuilder(); pb2.MoveTo(480, 300).CurveTo(560, 220, 640, 380, 720, 300).CurveTo(780, 240, 840, 360, 880, 300);
    c.StrokePath(pb2, SpriteGradient.Linear(0, 0, 1, 0, unchecked((int)0xFFFF00FF), unchecked((int)0xFF00FFFF)), 14f, true, VectorCap.Round, VectorJoin.Round);
    var star = new PointF[10]; for (int i = 0; i < 10; i++) { float a = i * MathF.PI / 5 - MathF.PI / 2, r = (i & 1) == 0 ? 90 : 40; star[i] = new PointF(560 + MathF.Cos(a) * r, 470 + MathF.Sin(a) * r); }
    c.FillPolygon((ReadOnlySpan<PointF>)star, SpriteGradient.Radial(0.5f, 0.5f, 0.6f, unchecked((int)0xFFFFE080), unchecked((int)0xFFFF8000), unchecked((int)0xFF802000)), AA: true, EvenOdd: true);
    c.FillEllipse(780, 470, 90, 50, SpriteGradient.Linear(0, 0, 1, 0, unchecked((int)0xFF000000), unchecked((int)0xFFFFFFFF)).Spread(VectorSpread.Repeat).WithMatrix(System.Numerics.Matrix3x2.CreateScale(0.25f, 1f)));
    // pixel-space gradient (boundingBox: false) across two shapes
    var gp = SpriteGradient.Linear(480, 540, 880, 540, false, unchecked((int)0xFFFF0000), unchecked((int)0xFF00FF00), unchecked((int)0xFF0000FF));
    c.FillRect(480, 530, 190, 50, gp); c.FillRect(690, 530, 190, 50, gp);
    var px = c.Pixels; var buf = new byte[px.Length * 4]; for (int i = 0; i < px.Length; i++) { int v = px[i]; buf[i * 4] = (byte)(v >> 16); buf[i * 4 + 1] = (byte)(v >> 8); buf[i * 4 + 2] = (byte)v; buf[i * 4 + 3] = 255; }
    File.WriteAllBytes(Path.Combine(outDir, "cache.rgba"), buf); Console.WriteLine("wrote cache.rgba 900x600");
  }
}
