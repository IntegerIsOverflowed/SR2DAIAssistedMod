// benchrun: run bench test bodies headlessly, dump the canvas as raw RGBA (convert with tests/cs/topng.py)
using System; using System.Diagnostics; using System.IO; using System.Linq; using Sr2d64CSport;
static class Program {
  static void Save(Sprite s, string name){ var px = s.Pixels; var buf = new byte[px.Length*4]; for (int i=0;i<px.Length;i++){int c=px[i]; buf[i*4]=(byte)(c>>16); buf[i*4+1]=(byte)(c>>8); buf[i*4+2]=(byte)c; buf[i*4+3]=255;} File.WriteAllBytes(name, buf); Console.WriteLine($"wrote {name} {s.Width}x{s.Height}"); }
  static int Main(string[] args) {
    string mode = args.Length > 0 ? args[0] : "big";
    var all = Tests.All();
    if (mode == "shear") {
      var c0 = new Ctx { Canvas = new Sprite(1100, 600), Temp = new Sprite(256,256), A = new Assets(256) };
      c0.X = 550; c0.Y = 300; c0.Count = 1; c0.Blend = 128; c0.MaskBits = 1; c0.Op = SR2D.Op.Paint;
      foreach (var (name, anim) in new[] { ("DrawRotateShear", false), ("DrawRotateShear", true), ("DrawRotate (", true), ("DrawRotate2 (", true) }) {
        var t = all.First(x => x.Name.StartsWith(name));
        for (int w = 0; w < 5; w++) { c0.Time = anim ? w * 0.05f : 0; c0.Angle = 0.3f; t.Run(c0); }
        var sw = Stopwatch.StartNew(); int n = 120;
        for (int i = 0; i < n; i++) { c0.Time = anim ? i * 0.033f : 0; c0.Angle = 0.3f; t.Run(c0); }
        Console.WriteLine($"{name,-18} animated={anim,-5} {sw.Elapsed.TotalMilliseconds / n,7:0.000} ms/frame");
      }
      // lossless check: pixel count of the rotated shape must equal the source's, at many angles and pivots
      {
        var src = new Sprite(97, 61); var sp = src.Pixels; for (int i = 0; i < sp.Length; i++) sp[i] = unchecked((int)0xFF000000) | (i * 2654435761u > 0 ? (int)(i * 2654435761u >> 8) & 0xFFFFFF : 1);
        int bad = 0; var cv = new Sprite(400, 400);
        for (int ai = 0; ai < 720; ai++) {
          float ang = ai * 0.5f * MathF.PI / 180f;
          int pvx = (ai % 3) switch { 0 => -1, 1 => 0, _ => 96 }, pvy = (ai % 5) switch { 0 => -1, 1 => 0, 2 => 60, _ => 17 };
          cv.ClearBuffer(0); cv.DrawRotateShear(src, 200, 200, ang, pvx, pvy, SR2D.Op.Paint);
          int cnt = 0; foreach (var px in cv.Pixels) if (px != 0) cnt++;
          int pxv = pvx < 0 ? 48 : pvx, pyv = pvy < 0 ? 30 : pvy;
          if (cnt != 97 * 61 || cv.Pixels[200 * 400 + 200] != sp[pyv * 97 + pxv]) { bad++; if (bad < 6) Console.WriteLine($"angle {ai*0.5} pivot {pvx},{pvy}: count {cnt} (want {97*61}) pivotpix {cv.Pixels[200*400+200]:X8} want {sp[pyv*97+pxv]:X8}"); }
        }
        Console.WriteLine($"lossless check: {bad} failures of 720");
      }
      return 0;
    }
    if (mode == "list") { foreach (var t in all) Console.WriteLine(t.Name); return 0; }
    if (mode == "big") {
      foreach (int n in new[]{256, 512}) {
        var big = all.First(x => x.Name.StartsWith("BIG voxel"));
        var bc = new Ctx { Canvas = new Sprite(1100, 600), Temp = new Sprite(128,128), A = new Assets(128) };
        bc.Count = n/128; bc.Scale = 1.2f; bc.MaskBits = 4; bc.Op = (SR2D.Op)1; bc.Yaw = 0.6f; bc.Pitch = 0.5f; bc.Brite = 1f; bc.DotStep = 0; bc.Blend = 40; bc.NotMask = args.Length > 1 && args[1] == "night"; bc.Z = 3; bc.Brite = 1.5f;
        var sw = Stopwatch.StartNew();
        bc.Progress = (ph, f, pr) => { if (!ph.StartsWith("render")) Console.WriteLine($"   [{sw.Elapsed.TotalMilliseconds,6:0} ms] {ph}"); };
        big.Run(bc); Console.WriteLine($"[{sw.Elapsed.TotalMilliseconds,6:0} ms] BIG {n}: " + bc.Note);
        Save(bc.Canvas, $"/home/user/.cache/benchrun/big{n}.rgba");
      }
      return 0;
    }
    // generic: run the named test once with default-ish params
    var test = all.FirstOrDefault(x => x.Name.StartsWith(mode, StringComparison.OrdinalIgnoreCase));
    if (test == null) { Console.WriteLine("no such test"); return 1; }
    string outName = "/home/user/.cache/benchrun/out.rgba";
    var c = new Ctx { Canvas = new Sprite(1100, 600), Temp = new Sprite(256,256), A = new Assets(256) };
    c.X = 550; c.Y = 300; c.Count = 1; c.Scale = 1f; c.Blend = 128; c.Brite = 1f; c.Z = 120; c.MaskBits = 1; c.Time = 1;
    for (int i = 1; i < args.Length; i++) { var kv = args[i].Split('='); switch (kv[0]) { case "x": c.X = int.Parse(kv[1]); break; case "y": c.Y = int.Parse(kv[1]); break; case "scale": c.Scale = float.Parse(kv[1]); break; case "blend": c.Blend = int.Parse(kv[1]); break; case "count": c.Count = int.Parse(kv[1]); break; case "bits": c.MaskBits = int.Parse(kv[1]); break; case "dot": c.DotStep = int.Parse(kv[1]); break; case "not": c.NotMask = true; break; case "op": c.Op = (SR2D.Op)int.Parse(kv[1]); break; case "yaw": c.Yaw = float.Parse(kv[1]); break; case "pitch": c.Pitch = float.Parse(kv[1]); break; case "reach": LightsDemo.Reach = int.Parse(kv[1]); break; case "sky": LightsDemo.Sky = int.Parse(kv[1]); break; case "energy": LightsDemo.LampEnergy = double.Parse(kv[1]); break; case "lamp": { var q = kv[1].Split(','); int li = int.Parse(q[0]); LightsDemo.Lamps[li].Hue = double.Parse(q[1]); LightsDemo.Lamps[li].Strength = int.Parse(q[2]); LightsDemo.Lamps[li].On = q.Length < 4 || q[3] != "off"; break; } case "out": outName = kv[1]; break; } }
    c.Canvas.ClearBuffer(unchecked((int)0xFF101418));
    test.Run(c); Console.WriteLine(c.Note);
    // the demo's overlays are not available headlessly; draw the Labels / Info lines the same way so the layout can be judged
    foreach (var (lx, ly, lt) in c.Labels) c.Canvas.DrawText(lx, ly, lt, unchecked((int)0xFFFFFFFF), unchecked((int)0xFF000000));
    { int iy = 8; foreach (var t in c.InfoLines) { c.Canvas.DrawText(8, iy, t, unchecked((int)0xFFC8DCFF), unchecked((int)0xC0000000), 1, 0, 0, SR2D.LineOp.AlphaBlend); iy += 16; } }
    Save(c.Canvas, outName);
    return 0;
  }
}
