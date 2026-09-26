using System; using System.IO; using System.Diagnostics; using Sr2d64CSport;
int W = 800, H = 500;
var canvas = new Sprite(W, H);
// checkerboard background so soft edges / alpha are visible
for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) canvas.SetPixel(x, y, ((x >> 4) + (y >> 4)) % 2 == 0 ? unchecked((int)0xFF303438) : unchecked((int)0xFF404448));

// a straight-alpha sprite: soft disc + hard square + text-like bars
var spr = new Sprite(96, 96, SR2D.Op.AlphaBlend);
spr.FillCircle(48, 48, 40, unchecked((int)0xFFFFC040), SR2D.LineOp.Set, true);
spr.FillRect(10, 10, 30, 30, unchecked((int)0xFF40A0FF), SR2D.LineOp.Set);
spr.FillRect(20, 60, 60, 8, unchecked((int)0xFFFFFFFF), SR2D.LineOp.Set);
// premultiplied copy
var pre = new Sprite(96, 96); pre.Draw(spr, 0, 0, SR2D.Op.Paint); pre.Premultiply();

int[] radii = { 0, 2, 6, 14, 30 };
for (int i = 0; i < radii.Length; i++)
{
    int x = 20 + i * 150;
    canvas.DrawBlurred(spr, x, 20, radii[i]);                               // AlphaBlend (Src.Op)
    canvas.DrawBlurred(pre, x, 160, radii[i]);                              // AlphaOver
    canvas.DrawBlurred(spr, x, 300, radii[i], SR2D.Op.Add, 200, Fast: true);// additive glow, fast
}
// drop shadow: black copy carrying spr's alpha
var sh = new Sprite(96, 96, SR2D.Op.AlphaBlend); sh.MoveByte(spr, 0, 0, SR2D.ColChannel.ChAlpha, SR2D.ColChannel.ChAlpha);
canvas.DrawBlurred(sh, 30, 410, 6, SR2D.Op.AlphaBlend, 200); canvas.Draw(spr, 20, 400, SR2D.Op.AlphaBlend);
// lock rect clipping
canvas.SetLockRect(200, 300, 400, 500);
canvas.DrawBlurred(spr, 180, 400, 10);
canvas.SetLockRect();
// ToBlurred + in-place Blur
var tb = spr.ToBlurred(8, out int margin); canvas.Draw(tb, 340 - margin, 400 - margin);
var cp = new Sprite(96, 96, SR2D.Op.AlphaBlend); cp.Draw(spr, 0, 0, SR2D.Op.Paint); cp.Blur(8); canvas.Draw(cp, 480, 400);
Console.WriteLine($"ToBlurred: {tb.Width}x{tb.Height}, margin {margin}, op {tb.Op}");

// sanity: soft edge really extends beyond the sprite rect
int outside = canvas.GetPixel(20 + 4 * 150 - 20, 20 + 48), bg = ((( 20 + 4 * 150 - 20) >> 4) + ((20 + 48) >> 4)) % 2 == 0 ? unchecked((int)0xFF303438) : unchecked((int)0xFF404448);
Console.WriteLine($"pixel 20px left of the r=30 sprite: {outside:X8} (background {bg:X8}) -> {(outside != bg ? "blur extends beyond the rect: OK" : "NOT blurred!")}");

// timing (release-ish, whatever JIT gives us)
var big = new Sprite(1920, 1080); var s256 = new Sprite(256, 256, SR2D.Op.AlphaBlend); s256.FillCircle(128, 128, 100, unchecked((int)0xFFFF8040), SR2D.LineOp.Set, true);
foreach (int r in new[] { 0, 4, 16, 64 })
{
    big.DrawBlurred(s256, 500, 300, r);
    var sw = Stopwatch.StartNew(); int n = 20; for (int i = 0; i < n; i++) big.DrawBlurred(s256, 500, 300, r);
    Console.WriteLine($"DrawBlurred 256x256 r={r,2}: {sw.Elapsed.TotalMilliseconds / n:F3} ms");
}
big.Blur(8); { var sw = Stopwatch.StartNew(); big.Blur(8); Console.WriteLine($"Blur in place 1920x1080 r=8: {sw.Elapsed.TotalMilliseconds:F2} ms"); }
big.Blur(8, true); { var sw = Stopwatch.StartNew(); big.Blur(8, true); Console.WriteLine($"Blur in place 1920x1080 r=8 Fast: {sw.Elapsed.TotalMilliseconds:F2} ms"); }

if (args.Length > 0) { using var f = File.Create(args[0]); var bytes = new byte[W * H * 4]; System.Runtime.InteropServices.MemoryMarshal.AsBytes(canvas.Pixels).CopyTo(bytes); f.Write(bytes); Console.WriteLine("wrote " + args[0]); }
