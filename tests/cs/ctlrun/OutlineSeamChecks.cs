// Coverage check for the outline text. Three renders of the same picture are compared per pixel:
//   direct : the normal anti-aliased draw at device scale (what the user sees)
//   ref8   : anti-aliased draw at 8x, averaged back down (the old reference)
//   truth  : NON-anti-aliased draw at 16x, averaged over each device pixel. A non-AA fill tests the pixel centre only,
//            so 16x16 point samples per pixel give the true coverage with no edge filter in the loop.
// A seam is a dip in direct that truth does not have. The report says which of direct / ref8 is closer to truth.
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
    }
}
