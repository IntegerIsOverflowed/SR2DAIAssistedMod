// Randomised check of the anti-aliased polygon fill (DRAW_POLY, flag 1) against exact coverage.
// Random sets of closed contours (non-zero winding, so overlapping contours are a union) are filled with
// Sprite.FillPolygons(AA) on a white sprite. The exact coverage of each pixel is the fraction of an 8x8 grid of
// sample points inside the union (point in polygon, non-zero winding), using the pixel-centre convention:
// pixel (x, y) covers [x - 0.5, x + 0.5] in the coordinates given to FillPolygons.
// A correct AA fill differs from this by at most about one sub-scanline of a 4-row AA pass (~0.13 per pixel).
using System;
using System.Collections.Generic;
using System.Drawing;
using Sr2d64CSport;

static class RasterFuzz
{
    const int White = unchecked((int)0xFFFFFFFF), Black = unchecked((int)0xFF000000);

    public static void Run()
    {
        var rng = new Random(12345);
        int bad = 0; double worstAll = 0; string worstDesc = "";
        const int trials = 400, W = 40, H = 40;
        for (int t = 0; t < trials; t++)
        {
            int nc = rng.Next(1, 4);
            var pts = new List<PointF>(); var counts = new List<int>();
            for (int c = 0; c < nc; c++)
            {
                int n = rng.Next(3, 7);
                float cx = 4 + (float)rng.NextDouble() * 32, cy = 4 + (float)rng.NextDouble() * 32;
                for (int i = 0; i < n; i++)
                {
                    double a = rng.NextDouble() * Math.PI * 2; float r = 1 + (float)rng.NextDouble() * 9;
                    pts.Add(new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a)));
                }
                counts.Add(n);
            }
            var sp = new Sprite(W, H); sp.ClearBuffer(White);
            sp.FillPolygons(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pts), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(counts), Black, SR2D.LineOp.Set, true, false);
            var px = sp.Pixels;
            double worst = 0; int wx = 0, wy = 0; double wDirect = 0, wExact = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int inside = 0;
                    for (int j = 0; j < 8; j++)
                        for (int i = 0; i < 8; i++)
                        {
                            float sx = x - 0.5f + (i + 0.5f) / 8f, sy = y - 0.5f + (j + 0.5f) / 8f;
                            if (Winding(pts, counts, sx, sy) != 0) inside++;
                        }
                    double exact = inside / 64.0;
                    double direct = 1.0 - (px[y * W + x] & 0xff) / 255.0;
                    double e = Math.Abs(direct - exact);
                    if (e > worst) { worst = e; wx = x; wy = y; wDirect = direct; wExact = exact; }
                }
            if (worst > worstAll) { worstAll = worst; worstDesc = $"trial {t} pixel ({wx},{wy}) direct {wDirect:0.00} exact {wExact:0.00}"; }
            if (worst > 0.25) bad++;
        }
        Console.WriteLine($"  raster fuzz: {trials} random polygon sets, worst pixel error {worstAll:0.00} ({worstDesc}); {bad} sets with an error over 0.25");
        if (bad > 0) Environment.Exit(1);
    }

    static int Winding(List<PointF> pts, List<int> counts, float px, float py)
    {
        int wind = 0, start = 0;
        foreach (int n in counts)
        {
            for (int i = 0; i < n; i++)
            {
                var a = pts[start + i]; var b = pts[start + (i + 1) % n];
                if (a.Y <= py)
                {
                    if (b.Y > py && Cross(a, b, px, py) > 0) wind++;
                }
                else if (b.Y <= py && Cross(a, b, px, py) < 0) wind--;
            }
            start += n;
        }
        return wind;
    }

    static float Cross(PointF a, PointF b, float px, float py) => (b.X - a.X) * (py - a.Y) - (px - a.X) * (b.Y - a.Y);
}
