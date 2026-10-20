// Checks for DRAW_LINE2_AA (the 1 px anti-aliased hairline) through the managed P/Invoke, and its timing against DRAW_LINE2.
using System;
using System.Diagnostics;
using System.Linq;
using Sr2d64CSport;

static unsafe class AaHairlineChecks
{
    static void Chk(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) Environment.Exit(1); }

    const int W = 64, H = 48;
    const int White = unchecked((int)0xFFFFFFFF);

    // a fresh black buffer, drawn into by f, returned for inspection
    static int[] Draw(Action<IntPtr> f)
    {
        var buf = new int[W * H];
        fixed (int* p = buf) f((IntPtr)p);
        return buf;
    }

    static int Gray(int[] b, int x, int y) => b[y * W + x] & 0xff;   // red channel of a grey ramp

    public static void Run()
    {
        // 1. a line centred on an integer row lights that row fully and nothing either side
        var h = Draw(p => SR2D.Native.DrawLine2Aa((int*)p, W, 0, 0, W, H, 2f, 10f, 20f, 10f, White, 1, 0));
        Chk(Enumerable.Range(3, 15).All(x => Gray(h, x, 10) == 255), "AA line on an integer row: the row is full white");
        Chk(Enumerable.Range(0, W).All(x => Gray(h, x, 9) == 0 && Gray(h, x, 11) == 0), "AA line on an integer row: the rows either side stay black");

        // 2. a line halfway between rows splits its coverage: 127 + 128 = 255
        var s = Draw(p => SR2D.Native.DrawLine2Aa((int*)p, W, 0, 0, W, H, 2f, 10.5f, 20f, 10.5f, White, 1, 0));
        Chk(Enumerable.Range(4, 12).All(x => Gray(s, x, 10) + Gray(s, x, 11) == 255), "AA line at .5: the two rows add to 255");
        Chk(Enumerable.Range(4, 12).All(x => Math.Abs(Gray(s, x, 10) - 127) <= 1 && Math.Abs(Gray(s, x, 11) - 128) <= 1), "AA line at .5: 127 above, 128 below");

        // 3. diagonal (slope 1/2): every column carries exactly 255 of coverage
        var d = Draw(p => SR2D.Native.DrawLine2Aa((int*)p, W, 0, 0, W, H, 1f, 3f, 41f, 23f, White, 1, 0));
        bool colsOk = true;
        for (int x = 3; x <= 40; x++)
        {
            int sum = 0; for (int y = 0; y < H; y++) sum += Gray(d, x, y);
            if (sum != 255) { colsOk = false; Console.WriteLine($"    column {x}: sum {sum}"); }
        }
        Chk(colsOk, "AA diagonal: each column sums to 255 of coverage");

        // 4. A->B and B->A paint the same pixels
        var ab = Draw(p => SR2D.Native.DrawLine2Aa((int*)p, W, 0, 0, W, H, 3.3f, 40.7f, 58.1f, 5.25f, White, 1, 0));
        var ba = Draw(p => SR2D.Native.DrawLine2Aa((int*)p, W, 0, 0, W, H, 58.1f, 5.25f, 3.3f, 40.7f, White, 1, 0));
        Chk(ab.SequenceEqual(ba), "AA line: A->B and B->A are identical");

        // 5. a steep line (walks along y) covers each row with 255 in total
        var st = Draw(p => SR2D.Native.DrawLine2Aa((int*)p, W, 0, 0, W, H, 10.25f, 2f, 14.75f, 40f, White, 1, 0));
        bool rowsOk = true;
        for (int y = 3; y <= 39; y++)
        {
            int sum = 0; for (int x = 0; x < W; x++) sum += Gray(st, x, y);
            if (sum != 255) { rowsOk = false; Console.WriteLine($"    row {y}: sum {sum}"); }
        }
        Chk(rowsOk, "AA steep line: each row sums to 255 of coverage");

        // 6. the clip rectangle is honoured: nothing outside it is written, and a line that starts outside still paints inside
        var c = Draw(p => SR2D.Native.DrawLine2Aa((int*)p, W, 10, 10, 30, 20, -50f, 15f, 100f, 15f, White, 1, 0));
        bool outside = false;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            if ((x < 10 || x >= 30 || y < 10 || y >= 20) && c[y * W + x] != 0) outside = true;
        Chk(!outside, "AA line: nothing is written outside the clip rectangle");
        Chk(Enumerable.Range(10, 20).All(x => Gray(c, x, 15) == 255), "AA line: the part inside the clip is painted");

        // 7. alpha-blend ops: half alpha over black gives about half intensity; the colour is never brighter than its alpha allows
        var ha = Draw(p => SR2D.Native.DrawLine2Aa((int*)p, W, 0, 0, W, H, 2f, 10f, 20f, 10f, unchecked((int)0x80FFFFFF), 3, 0));
        int v = Gray(ha, 10, 10);
        Chk(v >= 100 && v <= 156, $"AA line, alpha-blend op 3 with alpha 128 over black: {v}");

        // 7b. a coloured line over a coloured background: each channel is the exact integer lerp (set op, coverage 127 / 128)
        int bg = unchecked((int)0xFF1E3C5A), fg = unchecked((int)0xFFC86432);      // background (30,60,90), colour (200,100,50)
        var lr = Draw(p => { var pb = (int*)p; for (int i = 0; i < W * H; i++) pb[i] = bg; SR2D.Native.DrawLine2Aa(pb, W, 0, 0, W, H, 2f, 10.5f, 20f, 10.5f, fg, 1, 0); });
        int Lerp(int s8, int t8, int cv) => (s8 * cv + t8 * (255 - cv) + 127) / 255;
        bool lerpOk = true;
        foreach (var (row, cv) in new[] { (10, 127), (11, 128) })
        {
            int px = lr[row * W + 8];
            int[] exp = { Lerp(200, 30, cv), Lerp(100, 60, cv), Lerp(50, 90, cv), 255 };
            int got = (px >> 16) & 0xff, gotG = (px >> 8) & 0xff, gotB = px & 0xff, gotA = (int)((uint)px >> 24);
            if (got != exp[0] || gotG != exp[1] || gotB != exp[2] || gotA != exp[3]) { lerpOk = false; Console.WriteLine($"    row {row}: got {got},{gotG},{gotB},{gotA} expected {exp[0]},{exp[1]},{exp[2]},{exp[3]}"); }
        }
        Chk(lerpOk, "AA line over a coloured background: each channel is the exact coverage lerp");

        // 8. timing against the existing non-AA hairline (DRAW_LINE2, op 1 = set, width 1)
        const int N = 20000, Wt = 1000, Ht = 700;
        var rnd = new Random(7);
        var segs = Enumerable.Range(0, N).Select(_ => new[] { (float)(rnd.NextDouble() * Wt), (float)(rnd.NextDouble() * Ht), (float)(rnd.NextDouble() * Wt), (float)(rnd.NextDouble() * Ht) }).ToArray();
        var big = new int[Wt * Ht];
        double Time(bool aa)
        {
            var ts = new double[7];
            for (int r = 0; r < ts.Length; r++)
            {
                var sw = Stopwatch.StartNew();
                fixed (int* pb = big)
                    foreach (var g in segs)
                    {
                        if (aa) SR2D.Native.DrawLine2Aa(pb, Wt, 0, 0, Wt, Ht, g[0], g[1], g[2], g[3], White, 1, 0);
                        else SR2D.Native.DrawLine2(pb, Wt, 0, 0, Wt, Ht, g[0], g[1], g[2], g[3], White, 1, 0, 0, 0, 0);
                    }
                ts[r] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(ts);
            return ts[ts.Length / 2];
        }
        double plain = Time(false), aaMs = Time(true);
        Console.WriteLine($"  time {N} random segments into {Wt}x{Ht}, median of 7: DRAW_LINE2 {plain:0.0} ms, DRAW_LINE2_AA {aaMs:0.0} ms ({aaMs / plain:0.00}x)");
        Chk(aaMs > 0 && plain > 0, "timing measured");
    }
}
