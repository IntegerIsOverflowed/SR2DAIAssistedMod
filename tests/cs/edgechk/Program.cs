using System; using System.Drawing; using Sr2d64CSport;
static class P {
  static int fails = 0;
  static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
  static void Try(string what, Action a) { if (Environment.GetEnvironmentVariable("V") != null) Console.WriteLine("  > " + what); try { a(); } catch (Exception e) { fails++; Console.WriteLine($"THROW {what}: {e.GetType().Name}: {e.Message}"); } }
  static unsafe int Main() {
    int white = unchecked((int)0xFFFFFFFF), red = unchecked((int)0xFFFF0000);
    var canvas = new Sprite(64, 48); canvas.ClearBuffer(unchecked((int)0xFF000000));
    var src = new Sprite(16, 16); src.ClearBuffer(red);
    // 1. disposed / empty sprites as source and destination
    var dead = new Sprite(8, 8); dead.Dispose();
    Try("draw dead src", () => canvas.Draw(dead, 0, 0));
    Try("draw onto dead", () => dead.Draw(src, 0, 0));
    Try("scaled dead", () => canvas.DrawScaled(dead, 0, 0, 30, 30));
    Try("rotate dead", () => canvas.DrawRotate(dead, 0, 0, 10, 10, 0.3f));
    Try("rotate2 dead", () => canvas.DrawRotate2(dead, 10, 10, 0.3f));
    Try("blur dead", () => canvas.DrawBlurred(dead, 0, 0, 3));
    Try("blur onto dead", () => dead.Blur(3));
    Try("toblurred dead", () => { using var b = dead.ToBlurred(3, out _); });
    Try("line dead", () => dead.DrawLine(0, 0, 5, 5, white));
    Try("line2 dead", () => dead.DrawLine2(0, 0, 5, 5, white));
    Try("fillrect dead", () => dead.FillRect(0, 0, 5, 5, white));
    Try("circle dead", () => dead.FillCircle(2, 2, 2, white));
    Try("text dead", () => dead.DrawText(0, 0, "hi", white));
    Try("tile dead", () => canvas.TileDraw(dead, 0, 0, 64, 48));
    Try("tile onto dead", () => dead.TileDraw(src, 0, 0, 64, 48));
    Try("pixels dead", () => Check(dead.Pixels.Length == 0, "dead pixels len"));
    Try("region dead", () => { var r = dead.Region(0, 0, 4, 4); Check(r.Width == 0 && r.Height == 0, "dead region"); });
    Try("premul dead", () => dead.Premultiply());
    Try("clearalpha dead", () => dead.ClearAlpha());
    Try("colorkey dead", () => dead.AddColorKey(0));
    Try("mask dead", () => canvas.MaskDraw(src, dead, 0, 0, 0, 0, 1));
    Try("dpbm dead", () => canvas.DrawDPBM(dead, 0, 0, 1, 1, 1));
    Try("ebm dead", () => canvas.DrawEBM(src, dead, 0, 0));
    Try("ebm dead2", () => canvas.DrawEBM(dead, src, 0, 0));
    Try("copy ctor dead", () => { using var c = new Sprite(dead); Check(c.Width == 0, "copy of dead has size " + c.Width); });
    Try("resize ctor dead", () => { using var c = new Sprite(dead, SR2D.Transform.None, 10, 10); });
    Try("new 0x0", () => { using var z = new Sprite(0, 0); z.ClearBuffer(1); z.Draw(src, 0, 0); canvas.Draw(z, 0, 0); });
    Try("new -5x3", () => { using var z = new Sprite(-5, 3); canvas.Draw(z, 0, 0); });
    Try("dispose twice", () => { dead.Dispose(); });
    // 2. views
    Try("view", () => { using var v = canvas.CreateView(10, 10, 40, 30); v.ClearBuffer(white); Check(canvas.GetPixel(9, 10) != white && canvas.GetPixel(10, 10) == white && canvas.GetPixel(39, 29) == white && canvas.GetPixel(40, 29) != white, "view clip"); });
    Try("view out of range", () => { using var v = canvas.CreateView(-10, -10, 1000, 1000); Check(v.LockRect == canvas.Bounds, "view clamps: " + v.LockRect); });
    Try("view inverted", () => { using var v = canvas.CreateView(40, 30, 10, 10); Console.WriteLine("  inverted view rect -> " + v.LockRect); });
    // 3. lines with far endpoints / extreme values
    canvas.ClearBuffer(0);
    Try("line far", () => { canvas.DrawLine(-100000, -100000, 100000, 100000, white); canvas.DrawLine(int.MinValue / 4, 5, int.MaxValue / 4, 5, white, 3); canvas.DrawLine(3, int.MinValue / 4, 3, int.MaxValue / 4, white, 0, true); });
    Try("line max", () => { canvas.DrawLine(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue, white); });
    Try("line2 nan", () => { canvas.DrawLine2(float.NaN, 0, 10, 10, white); canvas.DrawLine2(float.PositiveInfinity, 0, 10, float.NegativeInfinity, white, SR2D.LineOp.AlphaBlend, 3, 3); });
    Try("rotate extreme", () => { canvas.DrawRotate(src, 8, 8, 1 << 20, 1 << 20, 1f); canvas.DrawRotate(src, 8, 8, -(1 << 20), 3, 1f, true); canvas.DrawRotate(src, 1 << 15, 1 << 15, 3, 3, 2f); });
    Try("rotate huge pivot", () => { canvas.DrawRotate(src, int.MaxValue / 2, 0, 3, 3, 2f); });
    Try("scaled 0 size", () => { canvas.DrawScaled(src, 0, 0, 0, 0); canvas.DrawScaled(src, 0, 0, -20, 20); canvas.DrawScaled(src, 5f, 5f, 0f); });
    Try("scaled huge", () => { canvas.DrawScaled(src, -1000000, -1000000, 3000000, 3000000, SR2D.Op.Paint, SR2D.Filter.Bicubic); });
    Try("quad wrong", () => { Span<PointF> q = stackalloc PointF[3]; try { canvas.DrawQuad(src, q); Check(false, "DrawQuad accepted 3 points"); } catch (ArgumentException) { } });
    Try("quad degenerate", () => { Span<PointF> q = stackalloc PointF[4]; canvas.DrawQuad(src, q, default, SR2D.Op.AlphaBlend, SR2D.Filter.Bilinear); });
    Try("polygon 2 pts", () => { Span<PointF> q = stackalloc PointF[2]; canvas.DrawInPolygon(src, q); canvas.FillPolygon(q, white); canvas.DrawPolygon(q, white); canvas.DrawPolyline(q, white, 3f, true); });
    Try("polygon empty", () => { canvas.FillPolygon(ReadOnlySpan<PointF>.Empty, white); canvas.DrawPolyline(ReadOnlySpan<PointF>.Empty, white); canvas.DrawCurve(ReadOnlySpan<PointF>.Empty, white); });
    Try("circle neg", () => { canvas.FillCircle(5, 5, -3, white); canvas.DrawCircle(5, 5, 0, white, 0, true); canvas.DrawCircle(5, 5, 1e9f, white, 2, true); canvas.FillEllipse(0, 0, float.NaN, 3, white); });
    Try("rect neg", () => { canvas.FillRect(10, 10, -5, -5, white); canvas.DrawRect(10, 10, 0, 0, white); canvas.ClearRect(30, 10, 30, 10, white); canvas.ClearRectXY(-10, -10, 5, 5, white); });
    Try("rounded", () => { canvas.FillRoundRect(0, 0, 10, 10, 20, SpriteGradient.Linear(0,0,1,1,white,red)); canvas.FillRoundRect(0, 0, 10, 10, -1, SpriteGradient.Linear(0,0,1,1,white,red)); canvas.FillRoundRect(0, 0, -10, 10, 3, SpriteGradient.Linear(0,0,1,1,white,red)); });
    Try("text", () => { canvas.DrawText(-100, -100, "hello", white); canvas.DrawText(0, 0, "", white); canvas.DrawText(0, 0, "x", white, 100); canvas.DrawText(60, 40, "clipped text long", white, 0); });
    Try("tile", () => { canvas.TileDraw(src, -100, -100, 500, 500, -37, 91); canvas.TileDraw(src, 10, 10, 0, 0); canvas.TileDraw(src, 10, 10, -5, 30); canvas.TileDraw(src, 200, 200, 10, 10); });
    Try("blur", () => { canvas.DrawBlurred(src, -5, -5, 0); canvas.DrawBlurred(src, -5, -5, -3); canvas.DrawBlurred(src, 0, 0, 100000); canvas.Blur(0); canvas.Blur(1000); using var b = canvas.ToBlurred(-1, out int m); Check(m == 0, "margin " + m); });
    Try("lockrect", () => { canvas.SetLockRect(100, 200, 100, 200); Check(canvas.LockRect == canvas.Bounds, "lockrect oob resets: " + canvas.LockRect); canvas.SetLockRect(new Rectangle(-5, -5, 20, 20)); Console.WriteLine("  lockrect(-5,-5,20,20) -> " + canvas.LockRect); canvas.SetLockRect(); });
    Try("region", () => { var r = canvas.Region(-5, -5, 10, 10); Check(r.Width == 5 && r.Height == 5, $"region clamp {r.Width}x{r.Height}"); var r2 = canvas.Region(60, 40, 100, 100); Check(r2.Width == 4 && r2.Height == 8, $"region clamp2 {r2.Width}x{r2.Height}"); var r3 = canvas.Region(100, 100, 5, 5); Check(r3.Width == 0 && r3.Height == 0, $"region outside {r3.Width}x{r3.Height}"); var r4 = canvas.Region(-50, 0, 10, 10); Check(r4.Width == 0, "region fully left " + r4.Width); });
    Try("region row", () => { var r = canvas.Region(-50, 0, 10, 10); Check(r.Row(0).Length == 0, "row on empty region"); });
    Try("getpixel", () => { Check(canvas.GetPixel(-1, 0) == 0 && canvas.GetPixel(64, 0) == 0 && canvas.GetPixel(0, 48) == 0, "getpixel oob"); canvas.SetPixel(-1, -1, 5); canvas.SetPixel(64, 48, 5); });
    Try("moveByte", () => { canvas.MoveByte(src, -8, -8, SR2D.ColChannel.ChRed, SR2D.ColChannel.ChAlpha); canvas.MoveBit(src, 60, 44, 31, 0); canvas.MoveBit(src, 0, 0, 40, -1); });
    Try("blend factor", () => { canvas.Blend(src, 0, 0, -50); canvas.Blend(src, 0, 0, 5000); canvas.Draw(src, 0, 0, (SR2D.Op)99); canvas.Draw(src, 0, 0, (SR2D.Op)(-1)); });
    Try("muladd", () => { canvas.MulAddS2X(src, 0, 0, int.MaxValue, int.MinValue); });
    Try("dpbm", () => { canvas.DrawDPBM(src, 0, 0, 0, 0, 0); canvas.DrawDPBM(src, 0, 0, 0, 0, 0, 5, true); canvas.DrawDPBM(src, 0, 0, int.MaxValue, int.MinValue, 1, -9, true); });
    Try("ebm", () => { canvas.DrawEBM(src, src, -8, -8); canvas.DrawEBM(src, src, -8, -8, true); using var one = new Sprite(1, 1); canvas.DrawEBM(src, one, 0, 0); canvas.DrawEBM(one, src, 0, 0, true); });
    Try("colorkey", () => { src.AddColorKey(red); Check(src.Op == SR2D.Op.Paint, "AddColorKey does not set op (documented)"); src.ClearAlpha(); });
    Try("intersect", () => { canvas.MaskInterSector(src, -100, -100, 1); canvas.MaskInterSector(src, 60, 40, 1); });
    Try("maskdraw", () => { canvas.MaskDraw(src, src, 0, 0, 100, 100, 1); canvas.MaskDraw(src, src, -8, -8, -8, -8, 1, true, SR2D.Op.AlphaOver); canvas.MaskClearBuffer(src, -4, -4, white, 1); });
    // 4. big / odd
    Try("1x1", () => { using var one = new Sprite(1, 1); one.DrawLine(0, 0, 0, 0, white); one.FillCircle(0, 0, 0.5f, white, SR2D.LineOp.Set, true); one.DrawRotate(src, 0, 0, 0, 0, 1); one.DrawScaled(src, 0, 0, 1, 1, SR2D.Op.Paint, SR2D.Filter.BicubicArea); one.Blur(5); canvas.DrawScaled(one, 0, 0, 64, 48, SR2D.Op.Paint, SR2D.Filter.Bicubic); canvas.DrawBlurred(one, 5, 5, 10); });
    Try("large", () => { using var big = new Sprite(4097, 3); big.ClearBuffer(white); big.DrawLine(0, 0, 4096, 2, red, 0, false, true); canvas.DrawScaled(big, 0, 0, 64, 48, SR2D.Op.Paint, SR2D.Filter.BicubicArea); });
    Try("wide resize", () => { using var big = new Sprite(2000, 2000); using var small = new Sprite(big, SR2D.Transform.RotCW, 3, 3000); Check(small.Width == 3 && small.Height == 3000, $"rotcw resize {small.Width}x{small.Height}"); });
    Try("resize transform", () => { using var s2 = new Sprite(src, SR2D.Transform.RotCCW, 8, 32); Check(s2.Width == 8 && s2.Height == 32, $"rot resize dims {s2.Width}x{s2.Height}"); using var s3 = new Sprite(src, SR2D.Transform.RotCW, 40, 20); Check(s3.Width == 40 && s3.Height == 20, $"rot resize dims2 {s3.Width}x{s3.Height}"); });
    // 5. parallel
    Try("parallel", () => { using var big = new Sprite(300, 300); big.DrawParallel(v => { v.ClearBuffer(white); v.FillCircle(150, 150, 100, red, SR2D.LineOp.Set, true); }); using var single = new Sprite(300, 300); single.ClearBuffer(white); single.FillCircle(150, 150, 100, red, SR2D.LineOp.Set, true); Check(big.Pixels.SequenceEqual(single.Pixels), "parallel == single"); });
    Try("parallel small", () => { using var s = new Sprite(10, 10); s.DrawParallel(v => v.ClearBuffer(white), 8); });
    // 6. selection
    Try("selection", () => { using var sel = new Selection(canvas); sel.Rect(new Rectangle(-10, -10, 20, 20)); sel.Ellipse(70, 60, 20, 20, SelectMode.Add); sel.Ellipse(5, 5, -3, 0, SelectMode.Add); sel.Ellipse(5, 5, float.NaN, 5, SelectMode.Add); sel.Invert(); sel.Feather(100); sel.Grow(-1000); sel.Grow(1000); var b = sel.Bounds; Console.WriteLine("  sel bounds " + b); sel.Wand(canvas, 100, 100, 10); sel.Wand(canvas, 0, 0, -5); sel.Wand(canvas, 0, 0, int.MaxValue); canvas.Fill(sel, white); sel.DrawOutline(canvas, 0, -100, -100); canvas.Apply(sel, v => v.ClearBuffer(red)); canvas.FloodFill(-1, -1, white); canvas.FloodFill(0, 0, white, int.MaxValue); canvas.ReplaceColor(0, white, int.MaxValue); });
    Try("selection mismatch", () => { using var sel = new Selection(8, 8); try { canvas.Fill(sel, white); Console.WriteLine("  Fill with a wrong-size selection did not throw"); } catch (ArgumentException) { } try { canvas.Apply(sel, v => v.ClearBuffer(red)); Console.WriteLine("  Apply with a wrong-size selection did not throw"); } catch (ArgumentException) { } });
    Try("selection big", () => { using var sel = new Selection(1, 1); sel.Rect(new Rectangle(0, 0, 1, 1)); sel.Feather(3); sel.Grow(1); sel.DrawOutline(canvas); });
    // 7. voxel
    Try("voxel", () => { using var g = new VoxelGrid(1, 1, 1); g.Set(0, 0, 0, new Voxel(red)); g.Set(5, 5, 5, new Voxel(red)); using var out2 = new Sprite(32, 32); var cam = new VoxelCamera(); g.Draw(out2, cam, 16, 16); g.Draw(out2, cam, float.NaN, 16); g.Draw(out2, cam, 1e12f, 16); using var sel = g.SelectSolid(); sel.Grow(3); using var g2 = g.Extract(sel); Check(g2.Width >= 1, "extract"); using var g3 = g.Resized(3, 3, 3); using var g4 = g.RotatedZ(-7); try { using var g5 = g.Resized(0, 0, 0); Check(false, "Resized(0) accepted"); } catch (ArgumentOutOfRangeException) { } });
    Try("voxel empty", () => { using var g = new VoxelGrid(4, 4, 4); using var out2 = new Sprite(32, 32); g.Draw(out2, new VoxelCamera(), 16, 16); var b = g.SolidBounds(); Console.WriteLine("  empty solid bounds " + b); using var c = g.Cropped(); Console.WriteLine("  cropped empty -> " + c.Width + "x" + c.Height + "x" + c.Depth); });
    Try("voxel bad size", () => { try { using var g = new VoxelGrid(0, 4, 4); Check(false, "0-size grid accepted"); } catch (ArgumentOutOfRangeException) { } });
    // 8. layered
    Try("layered", () => { using var ls = new LayeredSprite(64, 48); var l = ls.Add(src); l.X = -100; l.Y = -100; l.Scale = 0; l.Angle = float.NaN; ls.Compose(); l.Scale = 1e9f; ls.Compose(); ls.Remove(l); ls.Compose(); });
    // 9. gradient / effects
    Try("gradient", () => { canvas.FillRect(0, 0, 64, 48, SpriteGradient.Linear(0, 0, 0, 0, false, white, red)); canvas.FillRect(0, 0, 64, 48, SpriteGradient.Radial(10, 10, 0, false, white, red)); canvas.FillRect(0, 0, 64, 48, SpriteGradient.Radial(10, 10, -5, false, white, red)); canvas.FillRect(0, 0, 64, 48, SpriteGradient.Linear(0, 0, 1, 1, white)); canvas.FillRect(0, 0, 64, 48, SpriteGradient.Linear(0, 0, 1, 1)); });
    Try("effects", () => { var fx = new Effects().Blur(-5).Blur(100000).Brightness(1e9f).Contrast(float.NaN).Wave(0, 0, 0).Ripple(0, 0, 0, 0, 0); canvas.DrawFx(src, 0, 0, fx); for (int i = 0; i < 40; i++) fx.Blur(1); canvas.DrawFx(src, 0, 0, fx); });
    // 10. audit regressions
    Try("round rect", () => { canvas.FillRoundRect(2, 2, 30, 20, 6, red); canvas.DrawRoundRect(2, 2, 30, 20, 6, white, 2f); canvas.FillRoundRect(2, 2, 30, 20, 0, red); canvas.FillRoundRect(2, 2, 30, 20, 1e9f, red); canvas.DrawRoundRect(2, 2, 0, 20, 3, red); canvas.FillRoundRect(new System.Drawing.RectangleF(-10, -10, 100, 100), 40, red, SR2D.LineOp.Set, false); });
    Try("svg Z garbage", () => { var img = VectorImage.Load(System.Text.Encoding.UTF8.GetBytes("<svg><path d='M0 0 L10 0 L10 10 z 5 5 7 7 L0 0'/></svg>"), "svg"); img.Draw(canvas, 0f, 0f, 1f, 1f); });
    Try("svg not xml", () => { try { VectorImage.Load(System.Text.Encoding.UTF8.GetBytes("<svg><path d='M0 0'/>"), "svg"); Check(false, "broken xml accepted"); } catch (FormatException) { } });
    Try("micro dash", () => { var img = VectorImage.Load(System.Text.Encoding.UTF8.GetBytes("<svg><path d='M0 0 L100000 0 L100000 100000' stroke='red' stroke-dasharray='0.0001 0.0001' fill='none'/></svg>"), "svg"); var sw = System.Diagnostics.Stopwatch.StartNew(); img.Draw(canvas, 0f, 0f, 0.001f, 0.001f); Check(sw.ElapsedMilliseconds < 5000, "micro dash slow"); });
    Try("voxel after dispose", () => { var g = new VoxelGrid(2, 2, 2); g.Set(0, 0, 0, new Voxel(red)); g.Dispose(); Check(!g.Contains(0, 0, 0), "Contains after dispose"); Check(!g.IsSolid(0, 0, 0), "IsSolid after dispose"); Check(g.Get(0, 0, 0).IsEmpty, "Get after dispose"); });
    // 11. blend modes
    Try("blend modes", () => {
      using var bg = new Sprite(32, 32); using var fg = new Sprite(32, 32);
      int grey = unchecked((int)0xFF808080), half = unchecked((int)0x80FF0000);
      bg.ClearBuffer(grey); fg.ClearBuffer(unchecked((int)0xFF404040));
      bg.DrawBlend(fg, 0, 0, SR2D.BlendMode.Multiply);
      int px = bg.GetPixel(5, 5) & 0xffffff; Check(px == 0x202020, $"multiply 80*40 -> {px:x6} (expect 202020)");
      bg.ClearBuffer(grey); bg.DrawBlend(fg, 0, 0, SR2D.BlendMode.Screen, 0.5f);
      px = bg.GetPixel(5, 5) & 0xffffff; Check(px == 0x909090, $"screen 50% -> {px:x6} (expect 909090)");
      bg.ClearBuffer(grey); bg.Draw(fg, 0, 0, SR2D.Op.Difference);                  // through the Op enum
      px = bg.GetPixel(5, 5) & 0xffffff; Check(px == 0x404040, $"difference via Op -> {px:x6}");
      bg.ClearBuffer(grey); bg.DrawScaled(fg, 0, 0, 32, 32, SR2D.Op.LinearDodge, SR2D.Filter.Bilinear);
      px = bg.GetPixel(5, 5) & 0xffffff; Check(px == 0xC0C0C0, $"linear dodge via DrawScaled bilinear -> {px:x6}");
      bg.ClearBuffer(grey); bg.FillRect(0, 0, 32, 32, half, SR2D.BlendMode.Multiply.ToLineOp());       // shapes with a mode
      px = bg.GetPixel(5, 5) & 0xffffff; Check(px == 0x804040, $"fill multiply half red -> {px:x6} (expect 804040)");
      bg.ClearBuffer(grey); bg.FillBlend(half, SR2D.BlendMode.Normal, 0.5f);
      px = bg.GetPixel(5, 5) & 0xffffff; Check(px == 0xA06060, $"fill normal 25% red -> {px:x6} (expect A06060)");
      bg.ClearBuffer(grey); bg.DrawText(2, 2, "Hi", half, 0, 1, 0, 0, SR2D.LineOp.Overlay);
      bg.ClearBuffer(grey); bg.DrawLine2(0, 5, 31, 5, half, SR2D.LineOp.Hue); bg.DrawCircle(16, 16, 8, half, 3f, true, SR2D.LineOp.Color);
      // premultiplied transparent destination: colour survives, alpha accumulates
      using var layer = new Sprite(32, 32, SR2D.Op.AlphaOver); layer.ClearBuffer(0);
      layer.DrawBlend(fg, 0, 0, SR2D.BlendMode.Multiply, 0.5f);
      int p2 = layer.GetPixel(5, 5); Check((p2 >> 24 & 255) == 128 && (p2 & 0xff) == 0x20, $"premul dst: {p2:x8} (expect 80202020)");
      Check(layer.Premultiplied && !bg.Premultiplied, "Premultiplied flags");
      // layers: BlendMode + Opacity
      using var st = new LayeredSprite(32, 32);
      st.Add(bg, null, 0, 0, SR2D.Op.Paint);
      var L = st.Add(fg, null, 0, 0, SR2D.Op.Multiply); L.Opacity = 0.5f;
      Check(L.BlendMode == SR2D.BlendMode.Multiply, "layer mode readback");
      st.Compose();
      // hostile
      bg.DrawBlend(dead, 0, 0, SR2D.BlendMode.Hue); dead.DrawBlend(fg, 0, 0, SR2D.BlendMode.Hue, float.NaN);
      bg.DrawBlend(fg, 100, 100, SR2D.BlendMode.Dissolve, -3f); bg.DrawBlend(fg, -100, -100, (SR2D.BlendMode)999, 2f);
      bg.Draw(fg, 0, 0, (SR2D.Op)200); bg.FillRect(0, 0, 5, 5, half, (SR2D.LineOp)77);
    });
    // 12. in-place editors (Sprite.Edit.cs, Selection, VoxelGrid, VectorImage)
    Try("edit flips", () => {
      using var s = new Sprite(5, 3); for (int y = 0; y < 3; y++) for (int x = 0; x < 5; x++) s.SetPixel(x, y, y * 10 + x);
      var a = s.Pixels.ToArray();
      s.FlipX(); Check(s.GetPixel(0, 0) == 4 && s.GetPixel(4, 2) == 20, "flipx"); s.FlipX(); Check(s.Pixels.SequenceEqual(a), "flipx round trip");
      s.FlipY(); Check(s.GetPixel(0, 0) == 20, "flipy"); s.FlipY(); Check(s.Pixels.SequenceEqual(a), "flipy round trip");
      s.Rotate180(); Check(s.GetPixel(0, 0) == 24 && s.GetPixel(4, 2) == 0, "rot180"); s.Rotate180(); Check(s.Pixels.SequenceEqual(a), "rot180 round trip");
      s.RotateCW(); Check(s.Width == 3 && s.Height == 5 && s.GetPixel(2, 0) == 0 && s.GetPixel(0, 0) == 20, $"rotcw {s.Width}x{s.Height} {s.GetPixel(2,0)} {s.GetPixel(0,0)}");
      s.RotateCCW(); Check(s.Width == 5 && s.Height == 3 && s.Pixels.SequenceEqual(a), "rotcw+ccw round trip");
      s.Rotate90(4); s.Rotate90(-1).Rotate90(5); Check(s.Pixels.SequenceEqual(a), "rotate90 multiples");
      s.Rotate(90f).Rotate(270f); Check(s.Pixels.SequenceEqual(a), "Rotate(90) lossless path");
      // region flip through the lock rect
      s.SetLockRect(1, 4, 0, 3); s.FlipX(); s.SetLockRect();
      Check(s.GetPixel(0, 0) == 0 && s.GetPixel(1, 0) == 3 && s.GetPixel(3, 0) == 1 && s.GetPixel(4, 0) == 4, "region flipx");
      s.Transform(SR2D.Transform.RotCW); Check(s.Width == 3, "Transform enum");
      // shift / scroll
      using var t = new Sprite(4, 1); for (int x = 0; x < 4; x++) t.SetPixel(x, 0, x + 1);
      t.Scroll(1, 0); Check(t.GetPixel(0, 0) == 4 && t.GetPixel(1, 0) == 1, "scroll wrap"); t.Shift(-1, 0, false, 9); Check(t.GetPixel(0, 0) == 1 && t.GetPixel(3, 0) == 9, "shift fill");
      using var t2 = new Sprite(2, 3); for (int y = 0; y < 3; y++) t2.SetPixel(0, y, y + 1); t2.Scroll(0, 1); Check(t2.GetPixel(0, 0) == 3 && t2.GetPixel(0, 1) == 1, "scroll y wrap"); t2.Shift(0, -2); Check(t2.GetPixel(0, 0) == 2 && t2.GetPixel(0, 1) == 0, "shift y");
    });
    Try("edit canvas", () => {
      using var s = new Sprite(6, 4); s.ClearBuffer(0); s.SetPixel(2, 1, red); s.SetPixel(3, 2, red);
      Check(s.ContentBounds() == new Rectangle(2, 1, 2, 2), "content bounds " + s.ContentBounds());
      s.Trim(); Check(s.Width == 2 && s.Height == 2 && s.GetPixel(0, 0) == red && s.GetPixel(1, 1) == red, $"trim {s.Width}x{s.Height}");
      s.Expand(1, 7); Check(s.Width == 4 && s.Height == 4 && s.GetPixel(0, 0) == 7 && s.GetPixel(1, 1) == red, "expand");
      s.Crop(1, 1, 10, 10); Check(s.Width == 3 && s.Height == 3 && s.GetPixel(0, 0) == red, "crop clamp");
      s.Recanvas(-1, -1, 1, 1); Check(s.Width == 1 && s.GetPixel(0, 0) == red, "recanvas crop");
      try { s.Crop(50, 50, 5, 5); Check(false, "crop outside accepted"); } catch (ArgumentOutOfRangeException) { }
      using var e = new Sprite(3, 3); e.ClearBuffer(0); e.Trim(); Check(e.Width == 3, "trim of empty keeps size");
      using var op = new Sprite(4, 4); op.ClearBuffer(white); op.SetPixel(1, 2, red); op.Trim(0, 0, white); Check(op.Width == 1 && op.Height == 1, "trim by background");
      // resize / scale keep op + premul
      using var r = new Sprite(8, 8, SR2D.Op.AlphaOver); r.ClearBuffer(unchecked((int)0x80404040)); r.Resize(4, 0); Check(r.Width == 4 && r.Height == 4 && r.Op == SR2D.Op.AlphaOver && r.Premultiplied, "resize aspect/op");
      Check(r.GetPixel(1, 1) == unchecked((int)0x80404040), $"resize premul value {r.GetPixel(1,1):x8}");
      using var st = new Sprite(8, 8); st.ClearBuffer(unchecked((int)0x80FF0000)); st.Scale(0.5f); Check(st.Width == 4, "scale"); int v = st.GetPixel(1, 1); Check(Math.Abs((v >> 16 & 255) - 255) <= 1 && (v >> 24 & 255) == 128, $"scale straight keeps colour {v:x8}");
      // Rotate any angle: grow, background, straight-alpha edge
      using var g = new Sprite(20, 10); g.ClearBuffer(unchecked((int)0xFF00FF00)); g.Rotate(45f, SR2D.Filter.Bilinear, Grow: true); Check(g.Width >= 21 && g.Height >= 21 && g.Width == g.Height, $"rotate grow {g.Width}x{g.Height}"); Check((g.GetPixel(0, 0) >> 24) == 0, "grow corners transparent");
      using var g2 = new Sprite(10, 10); g2.ClearBuffer(unchecked((int)0xFF00FF00)); g2.Rotate(30f, SR2D.Filter.Bilinear, false, unchecked((int)0xFF000000)); Check(g2.Width == 10 && g2.GetPixel(5, 5) == unchecked((int)0xFF00FF00) && g2.GetPixel(0, 0) == unchecked((int)0xFF000000), $"rotate in place {g2.GetPixel(5,5):x8} {g2.GetPixel(0,0):x8}");
      g2.Rotate(float.NaN); g2.Rotate(720f); g2.Rotate(0f, Grow: true); Check(g2.Width == 10, "rotate no-ops");
      // view rules
      using var big = new Sprite(8, 8); using var vw = big.CreateView(2, 2, 6, 6); vw.FlipX(); vw.Rotate180(); vw.Rotate(180f);
      try { vw.Resize(2, 2); Check(false, "view resized"); } catch (InvalidOperationException) { }
      try { vw.RotateCW(); Check(false, "view rotcw accepted"); } catch (InvalidOperationException) { }
      try { vw.Rotate90(2); Check(false, "view rotate90(2) accepted"); } catch (InvalidOperationException) { }
      try { vw.Crop(0, 0, 2, 2); Check(false, "view crop accepted"); } catch (InvalidOperationException) { }
      vw.Rotate(30f); vw.Rotate(90f); vw.Apply(new Effects().Invert()); vw.Fade(0.5f);   // in-rect edits are fine on a view
      Check(big.Width == 8 && big.Height == 8, "owner intact");
      // clone
      using var c = big.Clone(1, 1, 3, 3); Check(c.Width == 3 && c.Height == 3, "clone rect"); using var c2 = big.Clone(-5, -5, 2, 2); Check(c2.Width == 0, "clone outside empty");
      dead.FlipX().Rotate90().Rotate(33f).Trim().Shift(1, 1).Invert(); Check(dead.Width == 0, "dead edits no-op");
      try { dead.Resize(4, 4); } catch (Exception ex) { Check(false, "dead resize threw " + ex.GetType().Name); }
    });
    Try("edit colour", () => {
      using var s = new Sprite(4, 4); s.ClearBuffer(unchecked((int)0xFF204060));
      s.Invert(); Check((s.GetPixel(1, 1) & 0xffffff) == 0xDFBF9F, $"invert {s.GetPixel(1,1):x8}"); s.Invert();
      s.Grayscale(); int g = s.GetPixel(1, 1); Check((g >> 16 & 255) == (g & 255) && (g >> 24 & 255) == 255, $"grayscale {g:x8}");
      s.ClearBuffer(unchecked((int)0xFF204060)); s.Fade(0.5f); Check((s.GetPixel(1, 1) >> 24 & 255) == 128 && (s.GetPixel(1, 1) & 0xffffff) == 0x204060, $"fade straight {s.GetPixel(1,1):x8}");
      using var p = new Sprite(4, 4, SR2D.Op.AlphaOver); p.ClearBuffer(unchecked((int)0xFF204060)); p.Fade(0.5f); Check(p.GetPixel(1, 1) == unchecked((int)0x80102030), $"fade premul {p.GetPixel(1,1):x8}");
      p.Unpremultiply(); Check(!p.Premultiplied && p.Op == SR2D.Op.AlphaBlend && (p.GetPixel(1, 1) & 0xffffff) == 0x204060 && (p.GetPixel(1, 1) >> 24 & 255) == 128, $"unpremultiply {p.GetPixel(1,1):x8}");
      Check(s.ReplaceColor(unchecked((int)0x80204060), red) > 0 && s.GetPixel(0, 0) == red, "replace colour (existing Selection.cs method)");
      s.AdjustColor(Brightness: 0.5f).RotateHue(90f).Tint(0x00FF00, 0.5f); s.Apply(new Effects().Blur(2));
      try { s.Apply(null!); Check(false, "apply null"); } catch (ArgumentNullException) { }
    });
    Try("edit selection", () => {
      using var sel = new Selection(4, 3); sel.Pixels[1] = 255;                  // (1, 0)
      sel.Invalidate(); sel.FlipX(); Check(sel.Contains(2, 0) && !sel.Contains(1, 0) && sel.Bounds == new Rectangle(2, 0, 1, 1), "sel flipx " + sel.Bounds);
      sel.FlipY(); Check(sel.Contains(2, 2) && sel.Bounds == new Rectangle(2, 2, 1, 1), "sel flipy " + sel.Bounds);
      sel.Rotate90(); Check(sel.Width == 3 && sel.Height == 4 && sel.Contains(0, 2), $"sel rot90 {sel.Width}x{sel.Height} {sel.Bounds}");
      sel.Rotate90(-1); Check(sel.Width == 4 && sel.Contains(2, 2), "sel rot back");
      sel.Shift(1, 0, true); Check(sel.Contains(3, 2), "sel shift"); sel.Shift(1, 0); Check(sel.IsEmpty, "sel shift out drops"); sel.Shift(1, 1, true).Rotate90(2);
    });
    Try("edit voxel", () => {
      using var g = new VoxelGrid(3, 2, 1); g.Set(0, 0, 0, new Voxel(red)); g.Set(2, 1, 0, new Voxel(white));
      var sel = new VoxelSelection(g); sel[0, 0, 0] = true; g.Objects.Add(new VoxelObject("corner", sel));
      g.RotateZ(1); Check(g.Width == 2 && g.Height == 3, $"rotz size {g.Width}x{g.Height}");
      using var rz = new VoxelGrid(3, 2, 1); rz.Set(0, 0, 0, new Voxel(red)); rz.Set(2, 1, 0, new Voxel(white)); using var rr = rz.RotatedZ(1);
      Check(g.Voxels().SequenceEqual(rr.Voxels()), "RotateZ in place == RotatedZ");
      using var os = g.SelectObject(g.FindObject("corner")!); Check(os.Count == 1 && g.Get(os.Bounds.x0, os.Bounds.y0, os.Bounds.z0).Color == red, "object followed the rotation");
      g.RotateZ(3); Check(g.Width == 3 && g.Get(0, 0, 0).Color == red && g.Get(2, 1, 0).Color == white, "rotz round trip");
      g.RotateX(1); Check(g.Height == 1 && g.Depth == 2, "rotx swaps h/d"); g.RotateX(-1); g.RotateY(2).RotateY(2); Check(g.Get(0, 0, 0).Color == red, "rot x/y round trips");
      g.FlipX(); Check(g.Get(2, 0, 0).Color == red, "flipx"); g.FlipX();
      g.Expand(1); Check(g.Width == 5 && g.Get(1, 1, 1).Color == red, "expand"); g.Trim(); Check(g.Width == 3 && g.Height == 2 && g.Depth == 1 && g.Get(0, 0, 0).Color == red, $"trim {g.Width}x{g.Height}x{g.Depth}");
      g.Crop(0, 0, 0, 1, 1, 1); Check(g.Count == 1 && g.Get(0, 0, 0).Color == red, "crop"); g.Resize(2, 2, 2); Check(g.Count == 8 && g.Get(1, 1, 1).Color == red, "resize");
      g.Scale(2f); Check(g.Width == 4, "scale"); using var f = g.SelectSolid(); Check(f.Count == 64, "scale filled");
      try { g.Crop(9, 9, 9, 10, 10, 10); Check(false, "voxel crop outside"); } catch (ArgumentOutOfRangeException) { }
      var cam = new VoxelCamera(); using var o = new Sprite(16, 16); g.Draw(o, cam, 8, 8);   // derived data rebuilt after the size changes
    });
    Try("edit vector", () => {
      var img = VectorImage.Load(System.Text.Encoding.UTF8.GetBytes("<svg width='20' height='10' viewBox='0 0 20 10'><rect x='0' y='0' width='5' height='10' fill='red'/></svg>"), "svg");
      var b0 = img.Bounds(); img.FlipX(); var b1 = img.Bounds(); Check(Math.Abs(b1.Left - 15) < 0.01f && img.ViewBox.Width == 20, $"vec flipx {b1}");
      img.FlipX(); img.RotateCW(); Check(Math.Abs(img.ViewBox.Width - 10) < 0.01f && Math.Abs(img.ViewBox.Height - 20) < 0.01f, $"vec rotcw viewbox {img.ViewBox}");
      var b2 = img.Bounds(); Check(img.ViewBox.Contains(b2), $"vec rotated content inside view box {b2} in {img.ViewBox}");
      img.RotateCCW(); Check(Math.Abs(img.ViewBox.Width - 20) < 0.01f && Math.Abs(img.Bounds().Left - b0.Left) < 0.01f, "vec rot round trip");
      img.Rotate(45f); Check(img.ViewBox.Width > 20, "vec rotate grows"); float vw0 = img.ViewBox.Width; img.Scale(2f); Check(Math.Abs(img.ViewBox.Width - vw0 * 2) < 0.01f, "vec scale");
      img.Resize(100, 0); Check(Math.Abs(img.ViewBox.Width - 100) < 0.01f, "vec resize"); img.Shift(5, 5).Expand(2).Trim(); using var o = new Sprite(64, 64); img.Draw(o, 0f, 0f, 1f, 1f);
    });
    Console.WriteLine(fails == 0 ? "edgechk: all passed" : $"edgechk: {fails} failures");
    return fails == 0 ? 0 : 1;
  }
}
