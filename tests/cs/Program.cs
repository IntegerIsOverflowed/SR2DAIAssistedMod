using System; using System.IO; using System.Drawing; using Sr2d64CSport;
int W = 900, H = 700;
var s = new Sprite(W, H); s.ClearBuffer(unchecked((int)0xFF202428));
int white = unchecked((int)0xFFFFFFFF), yellow = unchecked((int)0xFFFFD040), cyan = unchecked((int)0xFF40E0FF), red = unchecked((int)0xFFFF5050), green = unchecked((int)0xFF60FF80), grey = unchecked((int)0xFF707070);

// 1. polyline vs smooth curve through the same points (open), uneven spacing
Span<PointF> pts = stackalloc PointF[7];
for (int i = 0; i < 7; i++) pts[i] = new PointF(40 + i * 60, 80 + (i % 2 == 0 ? -40 : 40) + (i == 3 ? 20 : 0));
pts[4] = new PointF(pts[4].X - 20, pts[4].Y);
s.DrawPolyline(pts, grey);
foreach (var p in pts) s.FillCircle(p.X, p.Y, 3, grey, SR2D.LineOp.Set, true);
s.DrawCurve(pts, yellow, 3, true);
s.DrawCurve(pts, cyan, 1, true, false, 0.5f);
s.DrawPolyline(pts, red, Smooth: true, Width: 1, AA: true, Tension: 1f);

// 2. closed blob filled + outline
Span<PointF> blob = stackalloc PointF[6];
for (int i = 0; i < 6; i++) { float a = i * MathF.PI / 3; float r = i % 2 == 0 ? 80 : 50; blob[i] = new PointF(560 + MathF.Cos(a) * r, 110 + MathF.Sin(a) * r); }
s.FillCurve(blob, unchecked((int)0x8040A0FF), SR2D.LineOp.AlphaBlend, true);
s.DrawCurve(blob, white, 2, true, Closed: true);
s.DrawPolygon(blob, grey);
s.FillPolygon(blob, unchecked((int)0x80FF8040), Smooth: true, Op: SR2D.LineOp.AlphaBlend, AA: true, Tension: 0.4f);

// 3. dashed smooth curve, hairline
Span<PointF> wave = stackalloc PointF[9];
for (int i = 0; i < 9; i++) wave[i] = new PointF(40 + i * 100, 230 + MathF.Sin(i * 1.3f) * 40);
s.DrawCurve2(wave, green, false, 0f, SR2D.LineOp.Set, 8, 4, 0);

// 4. beziers with control polygons
PointF p0 = new(40, 420), c0 = new(120, 300), c1 = new(260, 520), p1 = new(340, 400);
s.DrawPolyline(stackalloc PointF[] { p0, c0, c1, p1 }, grey);
s.DrawBezier(p0, c0, c1, p1, yellow, 4, true, RoundCaps: true);
s.DrawQuadBezier(new(380, 420), new(460, 300), new(540, 420), cyan, 2, true);

// 5. PathBuilder
var pb = new Sprite.PathBuilder();
pb.RoundRect(600, 300, 250, 120, 30);
pb.Circle(725, 360, 35);
s.FillPath(pb, unchecked((int)0xFF3060A0), SR2D.LineOp.Set, true, EvenOdd: true);
s.DrawPath(pb, white, 2, true);

var pb2 = new Sprite.PathBuilder();
pb2.MoveTo(40, 600).CurveTo(100, 480, 180, 700, 240, 600).SmoothTo(360, 700, 420, 600).SmoothQuadTo(520, 600)
   .ArcTo(60, 40, 0, false, true, 640, 600).LineTo(700, 640).SmoothThrough(stackalloc PointF[] { new(760, 560), new(820, 660), new(870, 600) });
s.DrawPath(pb2, green, 3, true, RoundCaps: true);
s.DrawPath2(pb2, red, SR2D.LineOp.Set, 6, 6, 0);

var pie = new Sprite.PathBuilder();
pie.MoveTo(725, 520).ArcAround(725, 520, 60, -90, 250).Close();
s.FillPath(pie, unchecked((int)0xFFE0A030), SR2D.LineOp.Set, true);
s.DrawPath(pie, white, 1.5f, true);

var sp = new Sprite.PathBuilder();
sp.SmoothPolygon(stackalloc PointF[] { new(560, 480), new(620, 460), new(650, 520), new(600, 560), new(540, 540) });
s.FillPath(sp, unchecked((int)0xFF60C060), SR2D.LineOp.Set, true);

var f = new System.Collections.Generic.List<PointF>();
Console.WriteLine("pb2 flattened points per sub-path: " + string.Join(", ", System.Linq.Enumerable.Select(pb2.Flatten(f), t => $"{t.count}")));

// raw BGRA dump (System.Drawing.Bitmap needs GDI+, absent on Linux); tests/cs/topng.py converts it
string outFile = args.Length > 0 ? args[0] : "curves_preview.raw";
var raw = new byte[W * H * 4];
for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int p = s.GetPixel(x, y); int o = (y * W + x) * 4; raw[o] = (byte)(p >> 16); raw[o + 1] = (byte)(p >> 8); raw[o + 2] = (byte)p; raw[o + 3] = 255; }
File.WriteAllBytes(outFile, raw); Console.WriteLine($"saved {outFile} ({W}x{H} RGBA)");

var sw = System.Diagnostics.Stopwatch.StartNew(); int n = 2000;
for (int i = 0; i < n; i++) s.DrawCurve(wave, green, 3, true);
Console.WriteLine($"DrawCurve 9 pts w3 AA (~830 px long): {sw.Elapsed.TotalMilliseconds * 1000 / n:F1} us");
sw.Restart(); for (int i = 0; i < n; i++) s.DrawCurve(wave, green, 1, false);
Console.WriteLine($"DrawCurve hairline: {sw.Elapsed.TotalMilliseconds * 1000 / n:F1} us");
sw.Restart(); for (int i = 0; i < n; i++) s.FillPath(pb, white, SR2D.LineOp.Set, true, true);
Console.WriteLine($"FillPath roundrect+circle AA: {sw.Elapsed.TotalMilliseconds * 1000 / n:F1} us");
