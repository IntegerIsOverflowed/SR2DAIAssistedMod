using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Sr2d64CSport;

// MBTiles without dependencies: the SQLite reader, the MVT decoder and the vector import.
// The expected digests come from Python's sqlite3 (see tests/data/mbtiles/README.md): SHA-256 over every tile in
// (z, x, y) order, each as "z/x/y/length\n" followed by the bytes after gunzip.
static class MbTilesChecks
{
    static void Chk(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) Environment.Exit(1); }

    static string? FindDataDir()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = new DirectoryInfo(start);
            while (d != null)
            {
                var p = Path.Combine(d.FullName, "tests", "data", "mbtiles");
                if (Directory.Exists(p)) return p;
                d = d.Parent;
            }
        }
        return null;
    }

    static string Digest(MbTiles mb)
    {
        var keys = mb.Keys().OrderBy(k => k.Z).ThenBy(k => k.X).ThenBy(k => k.Y).ToList();
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (z, x, y) in keys)
        {
            var raw = mb.GetTile(z, x, y) ?? throw new InvalidOperationException($"tile {z}/{x}/{y} listed but not readable");
            h.AppendData(Encoding.ASCII.GetBytes($"{z}/{x}/{y}/{raw.Length}\n"));
            h.AppendData(raw);
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    static void SaveSprite(Sprite s, string name)
    {
        string outDir = Environment.GetEnvironmentVariable("CTLRUN_OUT") ?? Path.Combine(Path.GetTempPath(), "ctlrun");
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, name);
        File.WriteAllBytes(file, Png.Encode(s.Pixels, s.Width, s.Height));
        Console.WriteLine($"wrote {file} {s.Width}x{s.Height}");
    }

    public static void Run()
    {
        var dir = FindDataDir();
        Chk(dir != null, "tests/data/mbtiles found");
        string a = Path.Combine(dir!, "chel-z0-9.mbtiles");          // UTF-16 text, 65536-byte pages
        string b = Path.Combine(dir!, "chel-z11-smallpages.mbtiles"); // UTF-8, 1024-byte pages, multi-level b-tree, overflow

        Chk(MbTiles.IsSqliteFile(a) && MbTiles.IsSqliteFile(b), "SQLite header recognised");
        Chk(!MbTiles.IsSqlite(Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")), "an SVG file is not SQLite");

        using (var mb = MbTiles.Open(a))
        {
            Chk(mb.Name == "Tilemaker to OpenMapTiles schema", "metadata name (UTF-16 file): " + mb.Name);
            Chk(mb.Format == "pbf" && mb.MinZoom == 0 && mb.MaxZoom == 14, $"format pbf, metadata zoom 0..14 (got {mb.Format} {mb.MinZoom}..{mb.MaxZoom}); the file holds tiles to z9 only");
            Chk(Math.Abs(mb.West - 60.85) < 1e-9 && Math.Abs(mb.North - 55.4) < 1e-9 && Math.Abs(mb.East - 61.75) < 1e-9 && Math.Abs(mb.South - 54.85) < 1e-9,
                $"bounds 60.85,54.85,61.75,55.4 (got {mb.West},{mb.South},{mb.East},{mb.North})");
            Chk(mb.TileCount == 16, "16 tiles (" + mb.TileCount + ")");
            Chk(mb.Json.Contains("vector_layers"), "json metadata present");
            string d = Digest(mb);
            Chk(d == "f0f39edbb5470e39a1a97aa6453a97513758c0a26d5026f06442a2837c4d0545", "every tile reads back byte-for-byte (digest " + d[..12] + ")");
            Chk(mb.GetTile(0, 5, 5) == null, "a tile that is not in the file reads as null");
            Chk(mb.GetTile(2, 9, 0) == null && mb.GetTile(-1, 0, 0) == null, "out-of-range coordinates read as null");
        }

        using (var mb = MbTiles.Open(b))
        {
            Chk(mb.TileCount == 20 && mb.MaxZoom == 14 && mb.MinZoom == 0, $"small-page file: 20 tiles, maxzoom from metadata (got {mb.TileCount}, {mb.MaxZoom})");
            string d = Digest(mb);
            Chk(d == "33bbb8e96c0ea6d1f538d028b885e5cac8240480e7ebd5c6a1e47bc381bb826e", "small pages: overflow and interior pages read back (digest " + d[..12] + ")");
        }

        // in-memory open gives the same answer
        using (var ms = new MemoryStream(File.ReadAllBytes(b)))
        using (var mb = MbTiles.Open(ms))
            Chk(mb.TileCount == 20 && Digest(mb) == "33bbb8e96c0ea6d1f538d028b885e5cac8240480e7ebd5c6a1e47bc381bb826e", "in-memory SQLite opens the same tiles");

        // gzip helper
        using (var outMs = new MemoryStream())
        {
            using (var gz = new GZipStream(outMs, CompressionLevel.Fastest, true)) gz.Write(new byte[] { 1, 2, 3, 4, 5 });
            Chk(MbTiles.Ungzip(outMs.ToArray()).SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }), "gzip payloads are gunzipped; plain payloads pass through");
        }
        Chk(MbTiles.Ungzip(new byte[] { 9, 8, 7 }).SequenceEqual(new byte[] { 9, 8, 7 }), "non-gzip payload unchanged");

        // MVT: every tile decodes; feature types are valid
        using (var mb = MbTiles.Open(a))
        {
            int layers = 0, feats = 0, bad = 0;
            foreach (var (z, x, y) in mb.Keys())
                foreach (var L in Mvt.Decode(mb.GetTile(z, x, y)!))
                {
                    layers++;
                    foreach (var f in L.Features)
                    {
                        feats++;
                        if (f.GeomType < 1 || f.GeomType > 3) bad++;
                    }
                }
            Chk(layers > 0 && feats > 0 && bad == 0, $"MVT decodes: {layers} layers, {feats} features, no bad geometry types");
        }

        // corrupt tiles never throw out of Mvt.Decode
        var rnd = new Random(7);
        int threw = 0;
        for (int i = 0; i < 300; i++)
        {
            var junk = new byte[rnd.Next(1, 400)];
            rnd.NextBytes(junk);
            try { Mvt.Decode(junk); } catch { threw++; }
        }
        Chk(threw == 0, "random bytes do not throw from Mvt.Decode");

        // vector import: whole fixture at its max zoom, and a file-based Load
        var img = VectorImage.FromMbTiles(b, new MbTilesView { Zoom = 11 });
        Chk(img.Format == VectorFormat.MbTiles && img.Shapes.Count > 0, $"vector image from MBTiles: {img.Shapes.Count} shapes");
        Chk(img.ViewBox.Width % 256 == 0 && img.ViewBox.Height % 256 == 0 && img.ViewBox.Width > 0, "view box is a whole number of tiles");
        Chk(img.Warnings.Any(w => w.Contains("names are drawn as vector text")), "the importer says names are drawn as vector text");
        var loaded = VectorImage.Load(b);
        Chk(loaded.Format == VectorFormat.MbTiles && loaded.Shapes.Count == img.Shapes.Count, "VectorImage.Load(path) gives the same shapes");
        var fromBytes = VectorImage.Load(File.ReadAllBytes(a));
        Chk(fromBytes.Format == VectorFormat.MbTiles && fromBytes.Shapes.Count > 0, "VectorImage.Load(bytes) reads the SQLite header");

        // render: the map must cover the view (not a few stray shapes) and the colours must be the style's
        var canvas = new Sprite(900, 700);
        canvas.ClearBuffer(MbTilesStyle.Background);
        img.DrawFit(canvas);
        int bg = MbTilesStyle.Background, changed = 0;
        foreach (var px in canvas.Pixels) if (px != bg) changed++;
        SaveSprite(canvas, "mbtiles_small_z11.png");
        Console.WriteLine($"  info  painted {changed * 100 / (canvas.Width * canvas.Height)}% of the view; view box {img.ViewBox.Width}x{img.ViewBox.Height}, visible {img.VisibleArea}");
        Chk(changed > 0, "the rendered map paints something");

        // a city-scale view from the full sample, if it is available (optional: large file)
        string? full = Environment.GetEnvironmentVariable("MBTILES_FILE");
        if (!string.IsNullOrEmpty(full) && File.Exists(full))
        {
            var sw = Stopwatch.StartNew();
            using (var mb = MbTiles.Open(full))
            {
                string d = Digest(mb);
                Chk(d == "61b1a25ba13dbd98ff5f85216cb828ebfa9c6fa71838d41a3b7f20e2c050424a", $"full file reads back (digest {d[..12]}, {mb.TileCount} tiles, {sw.ElapsedMilliseconds} ms)");
                sw.Restart();
                var city = VectorImage.FromMbTiles(mb, new MbTilesView { Zoom = 13 });
                Console.WriteLine($"  info  z13 city import: {city.Shapes.Count} shapes in {sw.ElapsedMilliseconds} ms");
                var sprite = new Sprite(1280, 800);
                sprite.ClearBuffer(MbTilesStyle.Background);
                sw.Restart();
                city.DrawFit(sprite);
                Console.WriteLine($"  info  z13 draw: {sw.ElapsedMilliseconds} ms");
                SaveSprite(sprite, "mbtiles_city_z13.png");
                // diagnostics: near-black pixels (all channels < 40) in three ways the demo draws the same picture
                static int Dark(Sprite s) { int n = 0; foreach (var px in s.Pixels) { int r = (px >> 16) & 255, g = (px >> 8) & 255, b = px & 255; if (r < 40 && g < 40 && b < 40) n++; } return n; }
                Console.WriteLine($"  info  dark pixels, direct DrawFit: {Dark(sprite)}");
                var cached = new Sprite(1280, 800); cached.ClearBuffer(MbTilesStyle.Background);
                city.Cached.DrawFit(cached, new System.Drawing.RectangleF(0, 0, 1280, 800));
                Console.WriteLine($"  info  dark pixels, VectorSprite cache: {Dark(cached)}");
                SaveSprite(cached, "diag_cached_z13.png");
                var zoomed = new Sprite(1280, 800); zoomed.ClearBuffer(MbTilesStyle.Background);
                var fit = city.FitMatrix(new System.Drawing.RectangleF(0, 0, 1280, 800), true);
                city.Draw(zoomed, System.Numerics.Matrix3x2.CreateScale(8f, 8f, new System.Numerics.Vector2(640, 400)) * fit);
                Console.WriteLine($"  info  dark pixels, 8x zoom: {Dark(zoomed)}");
                SaveSprite(zoomed, "diag_zoom8_z13.png");
                var clear = new Sprite(1280, 800); clear.ClearBuffer(0);          // a canvas that starts transparent, as the demo's may
                city.DrawFit(clear);
                int transparent = 0; foreach (var px in clear.Pixels) if ((px >> 24) == 0) transparent++;
                Console.WriteLine($"  info  transparent pixels left on a cleared-to-0 canvas: {transparent * 100.0 / (1280 * 800):0.0}%");
                SaveSprite(clear, "diag_clear0_z13.png");
                foreach (var op in new[] { SR2D.Op.Paint, SR2D.Op.AlphaBlend, SR2D.Op.AlphaOver })
                {
                    var t = new Sprite(640, 400, op); t.ClearBuffer(0);
                    city.DrawFit(t);
                    int tr = 0, opq = 0; foreach (var px in t.Pixels) { if ((px >> 24) == 0) tr++; else if ((px >> 24) == 255) opq++; }
                    Console.WriteLine($"  info  op {op}: transparent {tr * 100.0 / (640 * 400):0.0}%, opaque {opq * 100.0 / (640 * 400):0.0}%");
                    var hist = new Dictionary<int, int>(); foreach (var px in t.Pixels) { int al = px >> 24; hist[al] = hist.TryGetValue(al, out var h) ? h + 1 : 1; }
                    Console.WriteLine($"  info  op {op} alpha values: " + string.Join(" ", hist.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}x{kv.Value}")));
                    int rgbOnA0 = 0; foreach (var px in t.Pixels) if ((px >> 24) == 0 && (px & 0xFFFFFF) != 0) rgbOnA0++;
                    Console.WriteLine($"  info  op {op}: alpha 0 but colour written: {rgbOnA0} px");
                    SaveSprite(t, "diag_op_" + op + ".png");
                }
            }
            CullChecks(full);
        }
        else Console.WriteLine("  skip full-size sample (set MBTILES_FILE to chelyabinsk.mbtiles)");
    }

    // The grid index + DrawVisible: culled drawing must equal the full drawing, cull must drop shapes, labels must add shapes.
    static void CullChecks(string full)
    {
        using var mb = MbTiles.Open(full);
        var noLabels = VectorImage.FromMbTiles(mb, new MbTilesView { Zoom = 14, Labels = false });
        var img = VectorImage.FromMbTiles(mb, new MbTilesView { Zoom = 14, Labels = true });
        Chk(img.Shapes.Count > noLabels.Shapes.Count, $"labels add vector shapes ({img.Shapes.Count} with, {noLabels.Shapes.Count} without)");
        var sw = Stopwatch.StartNew();
        img.BuildIndex();
        Console.WriteLine($"  info  z14 grid index: {sw.Elapsed.TotalMilliseconds:0} ms for {img.Shapes.Count} shapes");

        // a view at 3x of the image centre part, 800x500 pixels
        var half = new System.Numerics.Vector2(400, 250);
        var c = new System.Numerics.Vector2(img.Width * 0.5f, img.Height * 0.5f);
        var m = System.Numerics.Matrix3x2.CreateTranslation(-c.X, -c.Y) * System.Numerics.Matrix3x2.CreateScale(3f) * System.Numerics.Matrix3x2.CreateTranslation(half.X, half.Y);

        var full1 = new Sprite(800, 500); full1.ClearBuffer(MbTilesStyle.Background);
        sw.Restart(); img.Draw(full1, m); long tFull = sw.ElapsedMilliseconds;
        var cull = new Sprite(800, 500); cull.ClearBuffer(MbTilesStyle.Background);
        sw.Restart(); img.DrawVisible(cull, m); long tCull = sw.ElapsedMilliseconds;
        int diff = 0; for (int i = 0; i < full1.Pixels.Length; i++) if (full1.Pixels[i] != cull.Pixels[i]) diff++;
        Chk(diff == 0, $"culled draw equals full draw ({diff} different pixels)");
        Console.WriteLine($"  info  view 3x: full draw {tFull} ms, culled draw {tCull} ms");

        var inView = img.ShapesIn(new System.Drawing.RectangleF(c.X - 130, c.Y - 85, 260, 170)).Count;
        Chk(inView < img.Shapes.Count / 2, $"a small view picks {inView} of {img.Shapes.Count} shapes");

        var lod = new Sprite(800, 500); lod.ClearBuffer(MbTilesStyle.Background);
        sw.Restart(); img.DrawVisible(lod, m, null, 1.5f); long tLod = sw.ElapsedMilliseconds;
        Console.WriteLine($"  info  view 3x with 1.5 px minimum size: {tLod} ms");
        SaveSprite(cull, "cull_view3x.png");
    }
}
