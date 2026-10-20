// Seam check for the outline text: a direct anti-aliased draw is compared with an 8x supersampled reference,
// averaged back down to the same pixels. A seam (a thin gap where two pieces of the stroke meet) shows as a dip in the
// direct draw that the reference does not have.
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
            var bp = big.Pixels;
            double max = 0; int over = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int sum = 0;
                    for (int j = 0; j < S; j++)
                        for (int i = 0; i < S; i++) sum += bp[(y * S + j) * big.Width + x * S + i] & 0xff;
                    double refV = sum / (double)(S * S);
                    double d = Math.Abs((direct.Pixels[y * W + x] & 0xff) - refV) / 255.0;
                    if (d > max) max = d;
                    if (d > 0.25) over++;
                }
            Console.WriteLine($"  seams {pos} zoom {zoom:0.00} (cap 28): worst pixel {max:0.00} of 1.00, {over} px differ by more than 0.25");
            File.WriteAllBytes(Path.Combine(outDir, $"seam_{pos}_{zoom:0.00}.png"), Png.Encode(direct.Pixels, direct.Width, direct.Height));
        }
    }
}
