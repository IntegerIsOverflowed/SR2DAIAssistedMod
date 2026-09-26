using System; using System.IO; using System.Collections.Generic; using System.Linq; using System.Diagnostics; using System.Threading; using System.Threading.Tasks; using Sr2d64CSport;
static class P {
  static int Main(string[] args) {
    if (args.Length > 0 && File.Exists(args[0])) {   // single file: full stack trace
      var d = File.ReadAllBytes(args[0]); var ext = Path.GetExtension(args[0]).ToLowerInvariant();
      try { var sw0 = Stopwatch.StartNew(); var img = VectorImage.Load(d, ext); Console.WriteLine($"loaded {sw0.ElapsedMilliseconds} ms, {img.Shapes.Count} shapes"); using var s = new Sprite(64, 64); img.Draw(s, 48f, 48f, 0.1f, 0.1f); Console.WriteLine($"drawn {sw0.ElapsedMilliseconds} ms"); Console.WriteLine("ok " + img.Warnings.Count + " warnings"); foreach (var w in img.Warnings) Console.WriteLine("  " + w); }
      catch (Exception ex) { Console.WriteLine(ex.ToString()); }
      return 0;
    }
    var files = new List<string>();
    foreach (var d in new[] { "/home/user/SR2D/tests/vec", "/home/user/uploads/aitest", "/home/user/uploads/corel", "/home/user/uploads/pdftext", "/home/user/uploads" })
      if (Directory.Exists(d)) foreach (var f in Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories)) { var e = Path.GetExtension(f).ToLowerInvariant(); if (e is ".svg" or ".eps" or ".ai" or ".pdf" or ".ps" or ".vox" or ".obj") files.Add(f); }
    int iters = args.Length > 0 ? int.Parse(args[0]) : 40; uint seed = args.Length > 1 ? uint.Parse(args[1]) : 7;
    var rng = new Random((int)seed);
    var kinds = new Dictionary<string, int>(); int hangs = 0, crashes = 0, total = 0; var slow = new List<string>();
    var skipped = new Dictionary<string, int>(); var cleanSkips = new List<string>();   // ImportLog: files whose Warnings report a swallowed exception
    var sw = Stopwatch.StartNew();
    foreach (var f in files) {
      var orig = File.ReadAllBytes(f); var ext = Path.GetExtension(f);
      bool vox = ext.Equals(".vox", StringComparison.OrdinalIgnoreCase) || ext.Equals(".obj", StringComparison.OrdinalIgnoreCase);
      for (int it = 0; it < iters; it++) {
        byte[] d = (byte[])orig.Clone(); string how;
        switch (it == 0 ? 0 : rng.Next(6)) {
          case 0: how = "orig"; break;
          case 1: { int n = 1 + rng.Next(8); for (int i = 0; i < n && d.Length > 0; i++) d[rng.Next(d.Length)] = (byte)rng.Next(256); how = $"flip{n}"; break; }
          case 2: { int len = rng.Next(d.Length + 1); Array.Resize(ref d, len); how = $"trunc{len}"; break; }
          case 3: { if (d.Length > 0) { int p = rng.Next(d.Length), n = Math.Min(d.Length - p, 1 + rng.Next(64)); for (int i = 0; i < n; i++) d[p + i] = (byte)"0123456789.- e[]{}<>/\\()%#"[rng.Next(26)]; } how = "junk"; break; }
          case 4: { if (d.Length > 16) { int p = rng.Next(d.Length - 8); var big = System.Text.Encoding.ASCII.GetBytes(new[] { "99999999999999", "-1e308", "NaN", "0", "1e-320", "2147483648", "-2147483649" }[rng.Next(7)]); Array.Copy(big, 0, d, p, Math.Min(big.Length, d.Length - p)); } how = "num"; break; }
          default: { if (d.Length > 2) { int a = rng.Next(d.Length), b = rng.Next(d.Length); if (a > b) (a, b) = (b, a); var seg = new byte[b - a]; Array.Copy(d, a, seg, 0, seg.Length); var nd = new byte[d.Length + seg.Length]; Array.Copy(d, 0, nd, 0, b); Array.Copy(seg, 0, nd, b, seg.Length); Array.Copy(d, b, nd, b + seg.Length, d.Length - b); d = nd; } how = "dup"; break; }
        }
        total++;
        var t = Task.Run(() => {
          try {
            if (vox) { if (ext.Equals(".vox", StringComparison.OrdinalIgnoreCase)) { using var g = VoxelGrid.LoadVox(d); } else { using var g = VoxelGrid.FromObj(System.Text.Encoding.Latin1.GetString(d), 16); } return "ok"; }
            var img = VectorImage.Load(d, ext);
            using var s = new Sprite(96, 96); img.Draw(s, 48f, 48f, 0.1f, 0.1f);
            int sk = 0; foreach (var w in img.Warnings) if (w.StartsWith("skipped: ", StringComparison.Ordinal)) sk++;
            if (sk > 0) { lock (skipped) { skipped[it == 0 ? "clean" : "fuzzed"] = skipped.GetValueOrDefault(it == 0 ? "clean" : "fuzzed") + 1; if (it == 0) cleanSkips.Add(Path.GetFileName(f) + ": " + img.Warnings.First(w => w.StartsWith("skipped: ", StringComparison.Ordinal))); } }
            return "ok";
          } catch (Exception e) { return e.GetType().Name; }
        });
        var t0 = sw.Elapsed;
        if (!t.Wait(20000)) { hangs++; Console.WriteLine($"HANG {Path.GetFileName(f)} it {it} ({how})"); File.WriteAllBytes($"/home/user/.cache/vecfuzz/hang_{Path.GetFileName(f)}_{it}{ext}", d); continue; }
        var dt = (sw.Elapsed - t0).TotalSeconds; if (dt > 5) slow.Add($"{Path.GetFileName(f)} it {it} ({how}) {dt:F1}s");
        var k = t.Result; kinds[k] = kinds.TryGetValue(k, out var c) ? c + 1 : 1;
        if (k != "ok" && k != "FormatException" && k != "InvalidDataException" && k != "NotSupportedException" && k != "EndOfStreamException" && k != "IOException") { crashes++; if (crashes <= 40) Console.WriteLine($"  {k}: {Path.GetFileName(f)} it {it} ({how})"); File.WriteAllBytes($"/home/user/.cache/vecfuzz/bad_{k}_{Path.GetFileName(f)}_{it}{ext}", d); }
      }
    }
    Console.WriteLine($"{files.Count} files x {iters}: {total} loads in {sw.Elapsed.TotalSeconds:F0}s; hangs {hangs}; unexpected exception types {crashes}");
    foreach (var kv in kinds) Console.WriteLine($"  {kv.Key}: {kv.Value}");
    foreach (var s in slow) Console.WriteLine("  slow: " + s);
    Console.WriteLine($"loads with 'skipped:' warnings (swallowed exceptions): clean files {skipped.GetValueOrDefault("clean")}, fuzzed {skipped.GetValueOrDefault("fuzzed")}");
    foreach (var s in cleanSkips) Console.WriteLine("  clean file with skipped: " + s);
    return hangs == 0 ? 0 : 1;
  }
}
