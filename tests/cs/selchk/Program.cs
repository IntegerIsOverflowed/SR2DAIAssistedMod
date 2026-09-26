// Selection / FloodFill check: FloodFill == wand + Fill, tolerance, global, ops, Apply restricts drawing,
// polygon / ellipse / rect / alpha selections, boolean combine, Bounds, Feather / Grow / Border,
// Extract, ReplaceColor, timings, preview PNG. Run under `timeout`.
using System; using System.Diagnostics; using System.Drawing;
namespace Sr2d64CSport {
static class P {
  static int fails;
  static void Check(string what, bool ok){ Console.WriteLine((ok?"  ok   ":"  FAIL ")+what); if(!ok) fails++; }
  static int Cmp(Sprite a, Sprite b){ int n=0; for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++) if(a.GetPixel(x,y)!=b.GetPixel(x,y)) n++; return n; }
  static Sprite Scene(){
    var s = new Sprite(320, 240, SR2D.Op.Paint); s.ClearBuffer(unchecked((int)0xFFE8E8E8));
    s.FillRect(20, 20, 120, 90, unchecked((int)0xFF3060C0));                 // blue box
    s.FillCircle(230, 80, 50, unchecked((int)0xFFC04040), SR2D.LineOp.Set, true); // AA red disc
    s.DrawLine2(0, 150, 320, 150, unchecked((int)0xFF000000));               // 1px black line splits the background
    for (int y=170;y<230;y++) for(int x=20;x<300;x++) s.SetPixel(x,y, SR2D.ARGB(255,(byte)(x*255/300),(byte)(x*255/300),(byte)(x*255/300))); // gradient bar
    s.FillRect(60, 40, 40, 30, unchecked((int)0xFFE8E8E8));                  // hole in the blue box (same colour as background, not connected)
    return s;
  }
  static unsafe void Main(){
    var s = Scene();
    // 1. flood fill of the background above the line must not leak below it, nor into the hole
    var a = new Sprite(s); int n = a.FloodFill(5, 5, unchecked((int)0xFF00FF00));
    Check("FloodFill: pixels below the 1-px line untouched", a.GetPixel(5, 200) == s.GetPixel(5, 200) && a.GetPixel(160, 155) == s.GetPixel(160, 155));
    Check("FloodFill: enclosed hole of the same colour untouched (contiguous)", a.GetPixel(70, 50) == unchecked((int)0xFFE8E8E8));
    Check("FloodFill: seed region filled, count plausible", a.GetPixel(5, 5) == unchecked((int)0xFF00FF00) && n > 20000 && n < 320*150);
    // 2. FloodFill == Wand + Fill
    var b = new Sprite(s); using (var sel = new Selection(b)) { int m = sel.Wand(b, 5, 5); b.Fill(sel, unchecked((int)0xFF00FF00)); Check($"FloodFill == Wand + Fill ({m} px)", Cmp(a, b) == 0 && m == n); Check("Bounds of the wand region", sel.Bounds == Rectangle.FromLTRB(0, 0, 320, 150)); }
    // 3. tolerance on the gradient bar: tol 0 selects one column, tol 30 a band
    using (var sel = new Selection(s)) { int t0 = sel.Wand(s, 150, 200, 0); int t30 = sel.Wand(s, 150, 200, 30); Check($"tolerance widens the region ({t0} -> {t30})", t0 < t30 && t30 < 300*60); var bb = sel.Bounds; Check("band bounds are the bar height", bb.Top == 170 && bb.Bottom == 230 && bb.Width > 20); }
    // 4. global (non-contiguous) selects the hole too
    var c = new Sprite(s); c.FloodFill(5, 5, unchecked((int)0xFF00FF00), 0, Contiguous: false); Check("Contiguous=false reaches the enclosed hole and below the line", c.GetPixel(70, 50) == unchecked((int)0xFF00FF00) && c.GetPixel(5, 200) == unchecked((int)0xFF00FF00));
    // 5. ops: AlphaBlend 50 % vs reference lerp
    var d = new Sprite(s); d.FloodFill(30, 30, unchecked((int)0x80FF0000), 0, true, SR2D.LineOp.AlphaBlend); uint px = (uint)d.GetPixel(30, 30);
    Check("AlphaBlend fill blends (blue box -> purple), alpha kept", ((px >> 16) & 255) > 120 && ((px >> 16) & 255) < 160 && (px & 255) > 80 && (px >> 24) == 255);
    // 6. Soft edge on the AA disc: wand with tolerance + Soft gives intermediate coverage on the rim
    using (var sel = new Selection(s)) { sel.Wand(s, 230, 80, 60, Soft: true); int mids = 0; for (int y=25;y<135;y++) for (int x=175;x<285;x++){ int v = sel.Coverage(x,y); if (v>0 && v<255) mids++; } Check($"Soft wand: {mids} intermediate coverage pixels on the AA rim", mids > 50); }
    // 7. Apply restricts drawing to the selection
    var e = new Sprite(s); using (var sel = new Selection(e)) { sel.Wand(e, 30, 30); e.Apply(sel, v => v.ClearBuffer(unchecked((int)0xFFFFFF00))); Check("Apply: inside selection changed, outside untouched", e.GetPixel(30,30) == unchecked((int)0xFFFFFF00) && e.GetPixel(70,50) == s.GetPixel(70,50) && e.GetPixel(5,5) == s.GetPixel(5,5)); }
    // 8. shapes + boolean ops + bounds
    using (var sel = new Selection(320, 240)) {
      sel.Rect(10, 10, 50, 40); Check("Rect bounds / count", sel.Bounds == new Rectangle(10,10,50,40) && sel.Count == 2000);
      sel.Rect(40, 30, 50, 40, SelectMode.Add); Check("Add: union bounds", sel.Bounds == Rectangle.FromLTRB(10,10,90,70) && sel.Count == 2000+2000-20*20);
      sel.Rect(40, 30, 50, 40, SelectMode.Subtract); Check("Subtract restores, bounds shrink back", sel.Count == 2000-400 && sel.Bounds == new Rectangle(10,10,50,40));
      sel.Rect(0, 0, 30, 30, SelectMode.Intersect); Check("Intersect", sel.Count == 400 && sel.Bounds == new Rectangle(10,10,20,20));
      sel.Invert(); Check("Invert count", sel.Count == 320*240-400 && sel.Bounds == new Rectangle(0,0,320,240));
      Span<PointF> tri = stackalloc PointF[3]{ new PointF(100,100), new PointF(200,100), new PointF(150,180) };
      sel.Polygon(tri, SelectMode.Replace, AA: true); double area = sel.Area; Console.WriteLine("    polygon bounds " + sel.Bounds); Check($"Polygon AA area ~ triangle ({area:F0} vs 4000)", Math.Abs(area-4000) < 60 && sel.Bounds.Top >= 99 && sel.Bounds.Bottom <= 181);
      sel.Ellipse(160, 120, 40, 20, SelectMode.Replace); Check($"Ellipse area ~ pi*a*b ({sel.Area:F0} vs {Math.PI*800:F0})", Math.Abs(sel.Area - Math.PI*800) < 40);
      sel.Rect(100, 100, 50, 50); var cl = sel.Clone(); sel.Grow(5); Check("Grow +5 enlarges bounds by 5", sel.Bounds == new Rectangle(95,95,60,60)); sel.Grow(-5); Check("Grow -5 back (approx)", Math.Abs(sel.Count-2500) < 80);
      cl.Border(3); Check($"Border(3): ring only ({cl.Count} px)", cl.Coverage(125,125) == 0 && cl.Coverage(100,100) > 0 && cl.Count > 500 && cl.Count < 1400);
      cl.Rect(100,100,50,50); cl.Feather(6); Console.WriteLine($"    feather: centre {cl.Coverage(125,125)} corner {cl.Coverage(100,100)} edge-mid {cl.Coverage(100,125)} outside(90,90) {cl.Coverage(90,90)} outside(70,125) {cl.Coverage(70,125)} bounds {cl.Bounds}"); Check("Feather: soft edge, centre solid", cl.Coverage(125,125) == 255 && cl.Coverage(100,125) > 0 && cl.Coverage(100,125) < 255 && cl.Coverage(70,125) == 0);
      cl.Dispose();
    }
    // 8b. operators + display
    using (var A = new Selection(320,240)) using (var B = new Selection(320,240)) {
      A.Rect(10,10,40,40); B.Rect(30,30,40,40);
      using var u = A | B; using var i = A & B; using var df = A - B; using var x = A ^ B; using var inv = ~A;
      Check("operator | (union)", u.Count == 1600+1600-400 && u.Bounds == Rectangle.FromLTRB(10,10,70,70));
      Check("operator & (intersect)", i.Count == 400 && i.Bounds == new Rectangle(30,30,20,20));
      Check("operator - (difference)", df.Count == 1200 && df.Bounds == new Rectangle(10,10,40,40));
      Check("operator ^ (xor)", x.Count == 2400 && x.Bounds == Rectangle.FromLTRB(10,10,70,70));
      Check("operator ~ (invert)", inv.Count == 320*240-1600 && !inv.Contains(20,20) && inv.Contains(5,5));
      Check("operands untouched", A.Count == 1600 && B.Count == 1600);
      int v0 = A.Version; A.Add(B); Check("in-place Add bumps Version", A.Count == 2800 && A.Version == v0+1);
      var ec = A.EdgeCount; Check($"EdgeCount of the union shape ({ec})", ec == 234);   // 240 perimeter - 6 convex + 2 concave corners - 2 inner-corner pixels (4-neighbour test)
      var canvas = new Sprite(320,240,SR2D.Op.Paint); canvas.ClearBuffer(unchecked((int)0xFF808080));
      A.Draw(canvas, 0, Tint: unchecked((int)0x80FF0000));
      uint inside = (uint)canvas.GetPixel(20,20), outside = (uint)canvas.GetPixel(100,100), corner = (uint)canvas.GetPixel(10,10);
      Check("Draw: tint inside, nothing outside, ant pixel on the edge", ((inside>>16)&255) > 150 && outside == 0xFF808080 && (corner == 0xFF000000 || corner == 0xFFFFFFFF));
      int ants = 0; foreach (var pt in A.EdgePoints()) { uint cc = (uint)canvas.GetPixel(pt.X, pt.Y); if (cc == 0xFF000000 || cc == 0xFFFFFFFF) ants++; }
      Check("every edge pixel carries an ant colour", ants == ec);
      var c2 = new Sprite(320,240,SR2D.Op.Paint); c2.ClearBuffer(unchecked((int)0xFF808080)); A.Draw(c2, 3, Tint: unchecked((int)0x80FF0000));
      Check("Phase moves the dashes", Cmp(canvas, c2) > 0);
      var c3 = new Sprite(400,300,SR2D.Op.Paint); c3.ClearBuffer(unchecked((int)0xFF808080)); A.Draw(c3, 0, Tint: unchecked((int)0x80FF0000), OffsetX: 50, OffsetY: 30);
      Check("Draw with offset", ((uint)c3.GetPixel(70,50)>>16 & 255) > 150 && (uint)c3.GetPixel(20,20) == 0xFF808080);
    }
    // 8c. selection -> spans of pixels
    using (var sel = new Selection(s)) {
      sel.Wand(s, 230, 80, 60, Soft: true);                                     // AA disc, soft rim
      var runs = sel.Runs(); int fromRuns = 0; foreach (var r in runs) fromRuns += r.Length;
      Check($"Runs cover exactly the selected pixels ({runs.Length} runs, {fromRuns} px)", fromRuns == sel.Count && sel.PixelCount() == sel.Count);
      bool ordered = true, inside = true; int py = -1, pxe = -1;
      foreach (var r in runs) { if (r.Y < py || (r.Y == py && r.X <= pxe)) ordered = false; py = r.Y; pxe = r.End; for (int x = r.X; x < r.End; x++) if (!sel.Contains(x, r.Y)) inside = false; if (r.X > 0 && sel.Contains(r.X - 1, r.Y)) inside = false; if (r.End < 320 && sel.Contains(r.End, r.Y)) inside = false; }
      Check("runs ordered, maximal, and only over selected pixels", ordered && inside);
      int solid = sel.PixelCount(255), soft = sel.Count - solid; Check($"threshold: {solid} solid + {soft} soft rim px", solid > 0 && soft > 0 && solid + soft == sel.Count);
      var t = new Sprite(s); int touched = 0;
      foreach (var run in t.Runs(sel)) { foreach (ref int pxl in run.Pixels) pxl = unchecked((int)0xFF00FF00); touched += run.Length; }
      int green = 0, wrong = 0; for (int y=0;y<240;y++) for (int x=0;x<320;x++) { bool isg = t.GetPixel(x,y) == unchecked((int)0xFF00FF00); if (isg) green++; if (isg != sel.Contains(x,y)) wrong++; }
      Check($"write-through spans: {touched} px painted, none outside", touched == sel.Count && green == sel.Count && wrong == 0);
      int[] pix = s.GetPixels(sel); Check("GetPixels length", pix.Length == sel.Count);
      for (int i = 0; i < pix.Length; i++) pix[i] = unchecked((int)0xFF0000FF); var t2 = new Sprite(s); int wr = t2.SetPixels(sel, pix);
      int blue = 0; foreach (var run in t2.Runs(sel)) foreach (int v in run.Pixels) if (v == unchecked((int)0xFF0000FF)) blue++;
      Check("SetPixels round trip", wr == pix.Length && blue == pix.Length && t2.GetPixel(5,5) == s.GetPixel(5,5));
      var idx = sel.Indices(); var pts = sel.Points(); Check("Indices / Points agree", idx.Length == pts.Length && idx[0] == pts[0].Y * 320 + pts[0].X && idx[^1] == pts[^1].Y * 320 + pts[^1].X);
      uint avg = (uint)s.AverageColor(sel); Check($"AverageColor of the red disc = {avg:X8}", ((avg >> 16) & 255) > 150 && (avg & 255) < 120);
      // timing: full-HD wand region, iterate spans and OR alpha
      var bg = new Sprite(1920,1080,SR2D.Op.Paint); bg.ClearBuffer(unchecked((int)0xFF808080)); for(int i=0;i<40;i++) bg.FillCircle(100+i*45, 300+(i%5)*120, 30, unchecked((int)0xFF000000));
      using var bs = new Selection(bg); bs.Wand(bg, 5, 5, 10);
      var sw2 = Stopwatch.StartNew(); int rc2 = bs.RunCount(); double tRuns = sw2.Elapsed.TotalMilliseconds;
      sw2.Restart(); long sum = 0; foreach (var run in bg.Runs(bs)) foreach (int v in run.Pixels) sum += v & 255; double tIter = sw2.Elapsed.TotalMilliseconds;
      sw2.Restart(); var flat = bg.GetPixels(bs); double tGet = sw2.Elapsed.TotalMilliseconds;
      Console.WriteLine($"  timing 1080p selection ({bs.Count} px, {rc2} runs): build runs {tRuns:F2} ms | iterate spans + sum {tIter:F2} ms | GetPixels copy {tGet:F2} ms");
    }
    // 8d. lock rect contract: every selection operation is confined to selection ∩ lock rect
    {
      var img = new Sprite(s); img.SetLockRect(0, 160, 0, 240);            // left half only
      using var sel = new Selection(img);
      int n1 = sel.Wand(img, 5, 5); Check("Wand stops at the lock rect", sel.Bounds.Right <= 160 && !sel.Contains(170, 5) && n1 > 0);
      img.SetLockRect(); sel.Wand(img, 5, 5); img.SetLockRect(0, 160, 0, 240);    // full region selected, then lock again
      Check("selection itself may extend past the lock rect", sel.Contains(170, 5));
      Sprite Half(Sprite src){ var c = new Sprite(src); c.SetLockRect(0, 160, 0, 240); return c; }   // copy ctor resets the lock rect (original behaviour)
      var t = Half(img); t.Fill(sel, unchecked((int)0xFF00FF00)); Check("Fill: only inside the lock rect", t.GetPixel(5,5) == unchecked((int)0xFF00FF00) && t.GetPixel(170,5) == s.GetPixel(170,5));
      t = Half(img); t.Apply(sel, v => v.ClearBuffer(unchecked((int)0xFFFF00FF))); Check("Apply: only inside the lock rect", t.GetPixel(5,5) == unchecked((int)0xFFFF00FF) && t.GetPixel(170,5) == s.GetPixel(170,5));
      t = Half(img); int painted = 0; foreach (var run in t.Runs(sel)) { painted += run.Length; foreach (ref int pv in run.Pixels) pv = 0x11223344; }
      Check("Runs: clipped to the lock rect", painted < sel.Count && painted > 0 && t.GetPixel(5,5) == 0x11223344 && t.GetPixel(170,5) == s.GetPixel(170,5));
      var got = img.GetPixels(sel); Check("GetPixels/SetPixels: same clipped count", got.Length == painted && img.SetPixels(sel, got) == painted);   // write the same values back: img unchanged
      var ex = img.Extract(sel); Check("Extract: clipped", ex.GetPixel(5,5) != 0 && ex.GetPixel(170,5) == 0);
      var t2 = Half(img); var srcc = new Sprite(320,240,SR2D.Op.Paint); srcc.ClearBuffer(0x55667788); t2.CopyThrough(sel, srcc); Check("CopyThrough: clipped", t2.GetPixel(5,5) == 0x55667788 && t2.GetPixel(170,5) == s.GetPixel(170,5));
      using var fa = new Selection(img); fa.FromAlpha(img); Check("FromAlpha honours the lock rect", fa.Bounds.Right <= 160 && fa.Count == 160*240);
      using var lk = new Selection(img); lk.FromLockRect(img); Check("FromLockRect", lk.Bounds == new Rectangle(0,0,160,240));
      var t3 = new Sprite(s); using var circ = new Selection(t3); circ.Ellipse(100, 100, 30, 20); t3.SetLockRect(circ); Check("SetLockRect(selection) = its bounds", t3.LockRect == circ.Bounds);
      t3.SetLockRect(); t3.SetLockRect(200, 300, 0, 240); using var far = new Selection(t3); far.Rect(0,0,50,50); Check("disjoint lock rect: Fill touches nothing, returns 0", t3.Fill(far, 0) == 0);
      t3.SetLockRect(); t3.SetLockRect(0, 160, 0, 240); int r1 = t3.FloodFill(200, 5, 0); Check("FloodFill with seed outside the lock rect fills nothing", r1 == 0 && t3.GetPixel(200,5) == s.GetPixel(200,5));
      // Draw / ants respect the target's lock rect too
      var t4 = new Sprite(320,240,SR2D.Op.Paint); t4.ClearBuffer(unchecked((int)0xFF808080)); t4.SetLockRect(0, 100, 0, 240); using var box = new Selection(t4); box.Rect(50, 50, 100, 100); box.Draw(t4, 0);
      Check("Draw: tint and ants clipped to the target's lock rect", (uint)t4.GetPixel(60,60) != 0xFF808080 && (uint)t4.GetPixel(120,60) == 0xFF808080 && (uint)t4.GetPixel(149,60) == 0xFF808080);
    }
    // 9. FromAlpha + Extract + CopyThrough
    var spr = new Sprite(320,240,SR2D.Op.AlphaBlend); spr.ClearBuffer(0); spr.FillCircle(100,100,40, unchecked((int)0xFF00AAFF), SR2D.LineOp.Set, true);
    using (var sel = new Selection(spr)) { sel.FromAlpha(spr); Check($"FromAlpha area ~ disc ({sel.Area:F0})", Math.Abs(sel.Area - Math.PI*1600) < 30);
      var ex = s.Extract(sel); Check("Extract: premultiplied cut-out, outside transparent", ex.GetPixel(100,100) == s.GetPixel(100,100) && ex.GetPixel(5,5) == 0 && ex.Op == SR2D.Op.AlphaOver);
      var dst = new Sprite(s); var src2 = new Sprite(320,240,SR2D.Op.Paint); src2.ClearBuffer(unchecked((int)0xFF123456)); dst.CopyThrough(sel, src2); Check("CopyThrough", dst.GetPixel(100,100) == unchecked((int)0xFF123456) && dst.GetPixel(5,5) == s.GetPixel(5,5)); }
    // 10. ReplaceColor
    var f = new Sprite(s); int rc = f.ReplaceColor(unchecked((int)0xFF3060C0), unchecked((int)0xFF000000)); Check($"ReplaceColor ({rc} px)", rc == 120*90-40*30 && f.GetPixel(30,30) == unchecked((int)0xFF000000));
    // 11. lock rect respected
    var g = new Sprite(s); g.SetLockRect(0, 160, 0, 120); g.FloodFill(5,5, unchecked((int)0xFF00FF00)); g.SetLockRect(); Check("FloodFill respects the lock rect", g.GetPixel(5,5) == unchecked((int)0xFF00FF00) && g.GetPixel(170, 5) == s.GetPixel(170,5) && g.GetPixel(5,130) == s.GetPixel(5,130));
    // 12. timing 1080p
    var big = new Sprite(1920,1080,SR2D.Op.Paint); big.ClearBuffer(unchecked((int)0xFF808080)); for(int i=0;i<40;i++) big.FillCircle(100+i*45, 300+(i%5)*120, 30, unchecked((int)0xFF000000));
    var sw = Stopwatch.StartNew(); const int reps=10; int cnt=0; for(int r=0;r<reps;r++) cnt = big.FloodFill(5,5, (r&1)!=0 ? 0x40FF0000 : unchecked((int)0xFF808080), 10, true, SR2D.LineOp.AlphaBlend); double tf = sw.Elapsed.TotalMilliseconds/reps;
    using (var sel = new Selection(big)) { sw.Restart(); for(int r=0;r<reps;r++) sel.Wand(big, 5, 5, 10); double tw = sw.Elapsed.TotalMilliseconds/reps; sw.Restart(); for(int r=0;r<reps;r++) big.Apply(sel, v => v.ClearBuffer(0x11223344)); double ta = sw.Elapsed.TotalMilliseconds/reps;
      Console.WriteLine($"  timing 1920x1080 ({SR2D.ActiveSimdLevel}): FloodFill ({cnt} px, AlphaBlend) {tf:F2} ms | Wand {tw:F2} ms | Apply(ClearBuffer) {ta:F2} ms"); }
    // preview
    var prev = new Sprite(640, 240, SR2D.Op.Paint); prev.Draw(s, 0, 0, SR2D.Op.Paint);
    var edit = new Sprite(s); edit.FloodFill(5,5, unchecked((int)0xFFFFD080), 0, true); edit.FloodFill(150,200, unchecked((int)0xFF40C040), 40, true, SR2D.LineOp.Set, Soft: true);
    using (var sel = new Selection(edit)) { sel.Wand(edit, 230, 80, 60, Soft: true); edit.Apply(sel, v => v.DrawFx(s, 0, 0, new Effects().Color(Hue: 120))); sel.Draw(edit, 2, Tint: 0x3000A0FF); Span<PointF> tri = stackalloc PointF[3]{ new PointF(40,120), new PointF(140,140), new PointF(60,145) }; sel.Polygon(tri); edit.Fill(sel, unchecked((int)0xA0FF00FF), SR2D.LineOp.AlphaBlend); sel.DrawOutline(edit, 0); }
    prev.Draw(edit, 320, 0, SR2D.Op.Paint);
    var raw = new byte[prev.Width*prev.Height*4]; int k=0; for(int y=0;y<prev.Height;y++)for(int x=0;x<prev.Width;x++){ int cc=prev.GetPixel(x,y); raw[k++]=(byte)(cc>>16); raw[k++]=(byte)(cc>>8); raw[k++]=(byte)cc; raw[k++]=255; }
    System.IO.File.WriteAllBytes("sel_preview.raw", raw);
    Console.WriteLine(fails==0 ? "selection ok" : "FAILURES " + fails); Environment.Exit(fails==0?0:1);
  } } }
