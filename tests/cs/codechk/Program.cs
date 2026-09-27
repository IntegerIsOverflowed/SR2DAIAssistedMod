using System; using System.IO; using System.Text.RegularExpressions; using Sr2d64CSport;
static class Program {
  static int Main(string[] args) {
    var src = CodeView.Source("Tests.cs")!;
    int ok = 0, fail = 0, noRefs = 0; string outDir = args.Length > 0 ? args[0] : "/home/user/.cache/codechk"; Directory.CreateDirectory(outDir);
    foreach (Match m in Regex.Matches(src, @"\bT\(G\w+,\s*""((?:[^""\\]|\\.)*)""")) {
      string name = m.Groups[1].Value.Replace("\\\"", "\"");
      if (name.Contains("\" + ")) continue;
      var t = new DemoTest { Name = name };
      // every strip builder the demo attaches, so For()'s ExtractMethod paths are all exercised (the LoadCode
      // crash shipped because only two of the nine were wired here)
      if (name.StartsWith("SpriteKnob") || name.StartsWith("SpriteButton")) t.ControlStrip = ControlsDemo.Build;
      if (name.StartsWith("SpriteBox SizeMode")) t.ControlStrip = ViewDemo.Build;
      if (name.StartsWith("MoveByte")) t.ControlStrip = SwapDemo.Build;
      if (name.StartsWith("SpriteLabel")) t.ControlStrip = FormDemo.Build;
      if (name.StartsWith("VoxelBox")) t.ControlStrip = VoxelDemo.Build;
      if (name.StartsWith("SpriteFont")) t.ControlStrip = FontDemo.Build;
      if (name.StartsWith("Vector import")) t.ControlStrip = RecolorDemo.Build;
      if (name.StartsWith("Voxel lights")) t.ControlStrip = LightsDemo.Build;
      string code = CodeView.For(t);
      bool good = code.Contains("(Ctx c) =>") && (t.ControlStrip == null || code.Contains("public static Control Build"));   // Build() / BuildButtons()
      if (good) ok++; else { fail++; Console.WriteLine("FAIL: " + name); }
      var (lines, runs) = CodeView.Colorize(code);
      if (lines.Length != code.Replace("\r\n", "\n").Split('\n').Length) { fail++; Console.WriteLine("FAIL colorize line count: " + name); }
      for (int li = 0; li < runs.Length; li++) if (runs[li] is { } rr) foreach (var (st, ln, col) in rr)
        if (st < 0 || ln <= 0 || st + ln > lines[li].Length) { fail++; Console.WriteLine($"FAIL run out of line {li}: " + name); break; }
        else if (Array.IndexOf(CodeView.Palette, col) < 0) { fail++; Console.WriteLine($"FAIL run color is not a final Palette ARGB (LoadCode must pass it through, not re-index Palette[{col}]): " + name); break; }
      var refs = CodeView.ParamRefs(code); if (refs.Count == 0) noRefs++;
      if (Environment.GetEnvironmentVariable("CODECHK_PARAMS") == "1") Console.WriteLine($"  {name,-60} {string.Join(" ", refs)}");
      if (args.Length > 1 && name.StartsWith(args[1])) { Console.WriteLine(code); File.WriteAllText(Path.Combine(outDir, "sample.txt"), code); }
    }
    Console.WriteLine($"extracted {ok}, failed {fail}, tests whose code reads no Ctx parameter {noRefs}");
    return fail == 0 ? 0 : 1;
  }
}
