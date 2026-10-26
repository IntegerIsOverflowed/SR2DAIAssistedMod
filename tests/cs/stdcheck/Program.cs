// Standards check, engine side: renders the cases in cases.json (written by tests/std/stdcheck.py) with the engine's own
// VectorShape stroke / fill onto a white canvas, one PNG per case. Nothing here is a reference; the oracles are in Python.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Sr2d64CSport;

internal static class Program
{
    static Matrix3x2 ReadMatrix(System.Text.Json.JsonElement t) { var v = t.EnumerateArray().Select(F).ToArray(); return new Matrix3x2(v[0], v[1], v[2], v[3], v[4], v[5]); }
        static float F(JsonElement e) => (float)e.GetDouble();

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: stdcheck <cases.json> <out dir>"); return 2; }
        Directory.CreateDirectory(args[1]);
        using var doc = JsonDocument.Parse(File.ReadAllText(args[0]));
        int failed = 0;
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            try
            {
                int w = c.GetProperty("w").GetInt32(), h = c.GetProperty("h").GetInt32();
                var path = new VectorPath();
                foreach (var op in c.GetProperty("ops").EnumerateArray())
                {
                    var a = op.EnumerateArray().ToArray();
                    switch (a[0].GetString())
                    {
                        case "M": path.MoveTo(F(a[1]), F(a[2])); break;
                        case "L": path.LineTo(F(a[1]), F(a[2])); break;
                        case "C": path.CurveTo(F(a[1]), F(a[2]), F(a[3]), F(a[4]), F(a[5]), F(a[6])); break;
                        case "Z": path.Close(); break;
                        default: throw new InvalidDataException("op " + a[0].GetString());
                    }
                }
                bool fill = c.GetProperty("fill").GetBoolean(), stroke = c.GetProperty("stroke").GetBoolean();
                float[]? dash = c.GetProperty("dash").GetArrayLength() > 0 ? c.GetProperty("dash").EnumerateArray().Select(F).ToArray() : null;
                // outline mode (tests/std/outlinecmp.py): our own outline technique, as the demo calls it (Make in Tests.cs):
                // pos 0 = centre (half width each side), 1 = outside ring, 2 = inside ring; path in mm; tolerance 0.01 mm (default)
                VectorPath? outlined = null;
                if (c.TryGetProperty("outline", out var ol))
                {
                    int pos = ol.GetProperty("pos").GetInt32(); double ow = ol.GetProperty("w").GetDouble();
                    double otol = double.TryParse(Environment.GetEnvironmentVariable("STDCHECK_OUTTOL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ot) ? ot : 0.01;   // experiment: fit tolerance (mm)
                    int ocap = ol.TryGetProperty("cap", out var ocp) ? ocp.GetInt32() : 2;   // 0 butt, 1 round, 2 square (VectorCap)
                    int ojoin = ol.TryGetProperty("join", out var ojp) ? ojp.GetInt32() : 0;   // 0 miter, 1 bevel, 2 round (T113)
                    // "scale": the outline is computed in millimetres (the demo's unit), the path is in canvas px: scale the path to mm,
                    // outline it with the width in mm and the tolerance and grid in mm, then scale the result back to px
                    double oscale = ol.TryGetProperty("scale", out var osp) ? osp.GetDouble() : 1.0;
                    if (oscale != 1.0) ow = ow;   // width is already in mm when a scale is given (see make.py)
                    var opath = oscale == 1.0 ? path : path.Transformed(Matrix3x2.CreateScale((float)(1.0 / oscale)));
                    if (Environment.GetEnvironmentVariable("STDCHECK_SLOW") == "1") VectorOffset.FastPath = false;   // experiment: distance-field path instead of the mitred fast path
                    double opix = double.TryParse(Environment.GetEnvironmentVariable("STDCHECK_PIXEL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var opv) ? opv : otol;   // distance grid (demo: 0.04)
                    if (Environment.GetEnvironmentVariable("STDCHECK_NODES") == "1")   // experiment: node counts of the outline (stderr)
                    {
                        var probe = pos == 3 ? VectorOffset.Band(opath, ow, otol, otol, -1, (VectorCap)ocap) : VectorOffset.Outline(opath, pos == 0 ? ow / 2 : ow, pos == 0 ? 0 : pos, opix, otol, (VectorCap)ocap, ojoin);
                        Console.Error.WriteLine($"NODES {name} tol={otol} subpaths={probe.SubpathCount} nodes={probe.NodeCount}");
                    }
                    // pos 3 = experiment: the filled inflated shape (ring -1), the object the stage pipeline measures
                    outlined = pos == 3 ? VectorOffset.Band(opath, ow, otol, otol, -1, (VectorCap)ocap) : VectorOffset.Outline(opath, pos == 0 ? ow / 2 : ow, pos == 0 ? 0 : pos, opix, otol, (VectorCap)ocap, ojoin);
                    if (oscale != 1.0) outlined = outlined.Transformed(Matrix3x2.CreateScale((float)oscale));
                }
                var shape = new VectorShape(path, fill ? VectorColor.Black : null, stroke ? VectorColor.Black : null, F(c.GetProperty("width")))
                {
                    EvenOdd = c.GetProperty("evenOdd").GetBoolean(),
                    Cap = (VectorCap)c.GetProperty("cap").GetInt32(),
                    SquareDotCap = c.TryGetProperty("squareDot", out var sd) && sd.GetBoolean(),
                    Hairline = c.TryGetProperty("hairline", out var hl) && hl.GetBoolean(),
                    Join = (VectorJoin)c.GetProperty("join").GetInt32(),
                    MiterLimit = F(c.GetProperty("miter")),
                    Dash = dash,
                    DashOffset = F(c.GetProperty("dashOffset")),
                    Transform = c.TryGetProperty("transform", out var tr) ? ReadMatrix(tr) : Matrix3x2.Identity,
                };
                int aaq = Environment.GetEnvironmentVariable("STDCHECK_AAQ") is string aq && int.TryParse(aq, out int aqv) ? aqv : 0;   // experiment: per-render quality (0 = default)
                bool fast = Environment.GetEnvironmentVariable("STDCHECK_FAST") == "1";   // experiment: FastLines (no AA for strokes at most 1 px)
                var img = new VectorImage();
                if (outlined != null && Environment.GetEnvironmentVariable("STDCHECK_DUMP") is string dumpDir)   // experiment: outline path data for a preview
                    File.WriteAllText(Path.Combine(dumpDir, name + ".d"), outlined.ToSvgData());
                if (outlined != null) shape = new VectorShape(outlined, VectorColor.Black, null, 0f) { Transform = shape.Transform };
                img.Shapes.Add(shape);
                var canvas = new Sprite(w, h);
                canvas.ClearBuffer(unchecked((int)0xFFFFFFFF));
                img.Draw(canvas, Matrix3x2.Identity, new VectorRenderOptions { AA = true, AaQuality = aaq, FastLines = fast });
                canvas.SavePng(Path.Combine(args[1], name + ".png"));
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"{name}: {ex.GetType().Name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("STDCHECK_TRACE") == "1") Console.Error.WriteLine(ex.StackTrace);
            }
        }
        Console.WriteLine($"rendered {doc.RootElement.GetArrayLength() - failed} of {doc.RootElement.GetArrayLength()} cases");
        return failed == 0 ? 0 : 1;
    }
}
