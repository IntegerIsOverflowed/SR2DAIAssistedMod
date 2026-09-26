// webpchk: cs/WebP.cs against libwebp reference decodes (tests/webp/*.ref.png) + mutation fuzz.
//   dotnet run -c Release                 -> compare every tests/webp/*.webp (exit code = failures)
//   dotnet run -c Release -- fuzz 5000    -> mutated inputs: only InvalidDataException may escape
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Sr2d64CSport;

static class Program
{
    static int Main(string[] args)
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../webp"));
        if (!Directory.Exists(dir)) dir = "/home/user/SR2D/tests/webp";
        var files = Directory.GetFiles(dir, "*.webp"); Array.Sort(files);
        if (args.Length >= 2 && args[0] == "fuzz") return Fuzz(files, int.Parse(args[1]));
        int fails = 0;
        foreach (var f in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(f);
                if (!WebP.IsWebP(bytes)) { Console.WriteLine($"FAIL {Path.GetFileName(f)}: IsWebP false"); fails++; continue; }
                var info = WebP.GetInfo(bytes) ?? throw new Exception("GetInfo null");
                var sw = Stopwatch.StartNew(); var (w, h, px) = WebP.Decode(bytes); sw.Stop();
                if (w != info.Width || h != info.Height) { Console.WriteLine($"FAIL {Path.GetFileName(f)}: size {w}x{h} vs info {info}"); fails++; continue; }
                var refPng = PngDecoder.Decode(File.ReadAllBytes(Path.ChangeExtension(f, ".ref.png"))) ?? throw new Exception("reference png unreadable");
                if (refPng.Width != w || refPng.Height != h) { Console.WriteLine($"FAIL {Path.GetFileName(f)}: reference is {refPng.Width}x{refPng.Height}"); fails++; continue; }
                int worst = 0, bad = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    int a = px[i], b = refPng.Argb[i]; if (a == b) continue; bad++;
                    for (int sh = 0; sh < 32; sh += 8) worst = Math.Max(worst, Math.Abs(((a >> sh) & 255) - ((b >> sh) & 255)));
                }
                // Sprite loaders on the same bytes
                using var s1 = Sprite.FromWebP(bytes); using var s2 = new Sprite(f);
                bool spriteOk = s1.Width == w && s1.Height == h && s2.Width == w && s1.Pixels.SequenceEqual(px) && s2.Pixels.SequenceEqual(px) && (s1.Op == (info.HasAlpha ? SR2D.Op.AlphaBlend : SR2D.Op.Paint));
                using var s3 = Sprite.FromWebP(bytes, SR2D.Transform.RotCW, 0, 0); bool rotOk = s3.Width == h && s3.Height == w;
                Console.WriteLine($"{(bad == 0 && spriteOk && rotOk ? "ok  " : "FAIL")} {Path.GetFileName(f),-28} {info,-26} {sw.Elapsed.TotalMilliseconds,6:F2} ms  {(bad == 0 ? "exact" : $"{bad} px differ, worst {worst}")}{(spriteOk ? "" : "  sprite loader mismatch")}{(rotOk ? "" : "  transform size wrong")}");
                if (bad != 0 || !spriteOk || !rotOk) fails++;
            }
            catch (Exception ex) { Console.WriteLine($"FAIL {Path.GetFileName(f)}: {ex.GetType().Name}: {ex.Message}"); fails++; }
        }
        // negative cases
        try { WebP.Decode(new byte[] { 1, 2, 3 }); Console.WriteLine("FAIL: garbage accepted"); fails++; } catch (InvalidDataException) { }
        if (WebP.GetInfo(new byte[20]) != null) { Console.WriteLine("FAIL: GetInfo on zeros"); fails++; }
        Console.WriteLine(fails == 0 ? $"webpchk: all {files.Length} files exact" : $"webpchk: {fails} failures");
        return fails;
    }

    static int Fuzz(string[] files, int iters)
    {
        var rng = new Random(7); var bad = new Dictionary<string, int>(); int ok = 0, inv = 0; long worst = 0;
        var srcs = new List<byte[]>(); foreach (var f in files) srcs.Add(File.ReadAllBytes(f));
        for (int it = 0; it < iters; it++)
        {
            var d = (byte[])srcs[it % srcs.Count].Clone();
            switch (rng.Next(4))
            {
                case 0: { int n = 1 + rng.Next(8); for (int k = 0; k < n; k++) d[rng.Next(d.Length)] = (byte)rng.Next(256); break; }
                case 1: { int n = 1 + rng.Next(4); for (int k = 0; k < n; k++) d[rng.Next(d.Length)] ^= (byte)(1 << rng.Next(8)); break; }
                case 2: Array.Resize(ref d, rng.Next(d.Length)); break;
                default: { int at = 12 + rng.Next(Math.Max(1, d.Length - 12)), n = rng.Next(1, 64); for (int k = 0; k < n && at + k < d.Length; k++) d[at + k] = (byte)rng.Next(256); break; }
            }
            var sw = Stopwatch.StartNew();
            try { WebP.Decode(d); ok++; }
            catch (InvalidDataException) { inv++; }
            catch (Exception ex) { string key = ex.GetType().Name + " " + (ex.StackTrace ?? "").Split('\n')[0].Trim(); bad[key] = bad.GetValueOrDefault(key) + 1; }
            worst = Math.Max(worst, sw.ElapsedMilliseconds);
        }
        Console.WriteLine($"webpchk fuzz {iters}: decoded {ok}, rejected {inv}, unexpected exceptions {bad.Count} kinds, slowest {worst} ms");
        foreach (var kv in bad) Console.WriteLine($"  {kv.Value}x {kv.Key}");
        return bad.Count;
    }
}
