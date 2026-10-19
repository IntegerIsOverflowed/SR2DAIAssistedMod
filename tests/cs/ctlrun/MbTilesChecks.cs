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

    static int PathSegments(Sr2d64CSport.VectorImage img) => img.Shapes.Sum(s => s.Path.SegmentCount);

    // hairline lines by default, street names on or off, and QGIS-style simplification of a whole picture
    public static void SimplifyAndHairlines()
    {
        var dir = FindDataDir();
        Chk(dir != null, "tests/data/mbtiles found (simplify checks)");
        using var mb = MbTiles.Open(Path.Combine(dir!, "chel-z11-smallpages.mbtiles"));
        var plain = Sr2d64CSport.VectorImage.FromMbTiles(mb, new Sr2d64CSport.MbTilesView { Zoom = 11, Labels = false, Threads = 1 });
        int outlines = plain.Shapes.Count(s => s.Stroke != null && !s.IsText);
        Chk(outlines > 0 && plain.Shapes.Where(s => s.Stroke != null && !s.IsText).All(s => s.Hairline),
            $"every outline of the map is a hairline by default ({outlines} outlines)");
        var simp = Sr2d64CSport.VectorImage.FromMbTiles(mb, new Sr2d64CSport.MbTilesView { Zoom = 11, Labels = false, Threads = 1,
            Simplify = new Sr2d64CSport.SimplifySettings { Tolerance = 2, Algorithm = Sr2d64CSport.SimplifyAlgorithm.Linear, ProviderSide = true } });
        int segPlain = PathSegments(plain), segSimp = PathSegments(simp);
        Chk(segSimp < segPlain, $"simplification at 2 px drops {segPlain - segSimp} of {segPlain} path segments");
        var late = Sr2d64CSport.VectorImage.FromMbTiles(mb, new Sr2d64CSport.MbTilesView { Zoom = 11, Labels = false, Threads = 1,
            Simplify = new Sr2d64CSport.SimplifySettings { Tolerance = 2, Algorithm = Sr2d64CSport.SimplifyAlgorithm.Linear, ProviderSide = false } });
        Chk(PathSegments(late) == segSimp, $"simplifying the built picture gives the same result as per feature ({PathSegments(late)} segments)");
        // street names need the city file (the small fixture has no road names at its level)
        string? full = Environment.GetEnvironmentVariable("MBTILES_FILE");
        if (!string.IsNullOrEmpty(full) && File.Exists(full))
        {
            using var big = MbTiles.Open(full);
            var withNames = Sr2d64CSport.VectorImage.FromMbTiles(big, new Sr2d64CSport.MbTilesView { Zoom = 13, Threads = 1 });
            var noNames = Sr2d64CSport.VectorImage.FromMbTiles(big, new Sr2d64CSport.MbTilesView { Zoom = 13, Threads = 1, StreetNames = false });
            int textWith = withNames.Shapes.Count(s => s.Layer == null), textNo = noNames.Shapes.Count(s => s.Layer == null);   // labels have no map layer
            Chk(textWith > 0 && textNo < textWith, $"Hide names of streets leaves fewer text shapes at z13 ({textWith} -> {textNo})");
        }
        else Console.WriteLine("  skip street-name check (set MBTILES_FILE to chelyabinsk.mbtiles)");
    }

    // the bitmap cache: a baked view copied at a whole-pixel pan equals the direct draw; a pan past the margin is not covered
    public static void BakeCache()
    {
        var dir = FindDataDir();
        Chk(dir != null, "tests/data/mbtiles found (bake checks)");
        using var mb = MbTiles.Open(Path.Combine(dir!, "chel-z11-smallpages.mbtiles"));
        var img = Sr2d64CSport.VectorImage.FromMbTiles(mb, new Sr2d64CSport.MbTilesView { Zoom = 11, Labels = false, Threads = 1 });
        const int W = 800, H = 500, bg = unchecked((int)0xFFF4F1E8);
        const string key = "bake-check";
        var o = Sr2d64CSport.VectorRenderOptions.Default;
        double cx = img.Width * 0.5, cy = img.Height * 0.5;
        var cache = new Sr2d64CSport.VectorViewCache();
        cache.Bake(img, key, W, H, cx, cy, 1.0, o, 1.5f, bg);
        Chk(cache.Covers(key, W, H, cx + 37, cy - 21, 1.0), "a 37,-21 pan stays inside the baked margin");
        Chk(!cache.Covers(key, W, H, cx + 1000, cy, 1.0), "a 1000 px pan is outside the margin");
        Chk(!cache.Covers(key + "x", W, H, cx, cy, 1.0), "another picture key is not covered");
        Chk(!cache.Covers(key, W, H, cx, cy, 2.0), "another zoom is not covered");
        foreach (var (dx, dy) in new[] { (0.0, 0.0), (37.0, -21.0), (-120.0, 64.0) })
        {
            Chk(cache.Covers(key, W, H, cx + dx, cy + dy, 1.0), $"pan {dx},{dy} is covered");
            var blit = new Sr2d64CSport.Sprite(W, H); blit.ClearBuffer(bg);
            cache.Blit(blit, cx + dx, cy + dy);
            var direct = new Sr2d64CSport.Sprite(W, H); direct.ClearBuffer(bg);
            img.DrawVisible(direct, Sr2d64CSport.VectorViewCache.ViewMatrix(cx + dx, cy + dy, 1f, W / 2f, H / 2f), o, 1.5f);
            int diff = 0; long sum = 0;
            for (int i = 0; i < direct.Pixels.Length; i++)
            {
                if (blit.Pixels[i] == direct.Pixels[i]) continue;
                diff++;
                for (int sh = 0; sh < 32; sh += 8) sum += Math.Abs(((blit.Pixels[i] >>> sh) & 255) - ((direct.Pixels[i] >>> sh) & 255));
            }
            double mean = sum / (double)direct.Pixels.Length / 4;
            Chk(mean < 0.05, $"blit at {dx},{dy} equals the direct draw (mean difference {mean:0.0000}, {diff} pixels differ)");
        }
        // a pan of a fraction of a pixel is rounded to whole pixels: shown, not claimed exact
        var frac = new Sr2d64CSport.Sprite(W, H); frac.ClearBuffer(bg);
        cache.Blit(frac, cx + 0.4, cy);
        Console.WriteLine("  info  a 0.4 px pan is rounded to whole pixels (the blit is not sub-pixel)");
    }

    /// <summary>The tile cache: a frame equals a direct draw at the same whole-pixel centre, pans and zoom included.</summary>
    public static void TileCache()
    {
        var dir = FindDataDir();
        Chk(dir != null, "tests/data/mbtiles found (tile checks)");
        using var mb = MbTiles.Open(Path.Combine(dir!, "chel-z11-smallpages.mbtiles"));
        var img = Sr2d64CSport.VectorImage.FromMbTiles(mb, new Sr2d64CSport.MbTilesView { Zoom = 11, Labels = false, Threads = 1 });
        img.BuildIndex();
        const int W = 800, H = 500, bg = unchecked((int)0xFFF4F1E8);
        const string key = "tile-check";
        var o = Sr2d64CSport.VectorRenderOptions.Default;
        double cx0 = img.Width * 0.5, cy0 = img.Height * 0.5;
        var tiles = new Sr2d64CSport.VectorTileCache();

        double MeanDiff(Sr2d64CSport.Sprite a, Sr2d64CSport.Sprite b, out int diff)
        {
            diff = 0; long sum = 0;
            for (int i = 0; i < a.Pixels.Length; i++)
            {
                if (a.Pixels[i] == b.Pixels[i]) continue;
                diff++;
                for (int sh = 0; sh < 32; sh += 8) sum += Math.Abs(((a.Pixels[i] >>> sh) & 255) - ((b.Pixels[i] >>> sh) & 255));
            }
            return sum / (double)a.Pixels.Length / 4;
        }

        // cold frame, then pans (whole and fractional, the fraction is rounded to whole device pixels)
        var first = new Sr2d64CSport.Sprite(W, H); first.ClearBuffer(bg);
        tiles.Draw(img, first, cx0, cy0, 1f, key, o, 1.5f, bg);
        foreach (var (dx, dy) in new[] { (0.0, 0.0), (37.0, -21.0), (-120.4, 64.6), (300.0, 10.0) })
        {
            double cx = cx0 + dx, cy = cy0 + dy;
            var tile = new Sr2d64CSport.Sprite(W, H); tile.ClearBuffer(bg);
            tiles.Draw(img, tile, cx, cy, 1f, key, o, 1.5f, bg);
            var direct = new Sr2d64CSport.Sprite(W, H); direct.ClearBuffer(bg);
            img.DrawVisible(direct, Sr2d64CSport.VectorViewCache.ViewMatrix(Math.Round(cx), Math.Round(cy), 1f, W / 2f, H / 2f), o, 1.5f);
            double mean = MeanDiff(tile, direct, out int diff);
            Chk(mean < 0.05, $"tile frame at pan {dx},{dy} equals the direct draw (mean difference {mean:0.0000}, {diff} pixels differ)");
        }
        Chk(tiles.LastCopied > 0, $"a pan copies tiles it already holds ({tiles.LastCopied} copied, {tiles.LastDrawn} drawn)");

        // a warm frame equals the cold frame of the same view
        var again = new Sr2d64CSport.Sprite(W, H); again.ClearBuffer(bg);
        tiles.Draw(img, again, cx0, cy0, 1f, key, o, 1.5f, bg);
        Chk(MeanDiff(again, first, out _) == 0, "a warm frame equals the cold frame (copies are exact)");

        // the background ring fills in: wait for it, then a pan needs no tile on screen
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (tiles.Busy && sw.Elapsed.TotalSeconds < 60) System.Threading.Thread.Sleep(5);
        Chk(!tiles.Busy, "the background ring finishes");
        var ringed = new Sr2d64CSport.Sprite(W, H); ringed.ClearBuffer(bg);
        tiles.Draw(img, ringed, cx0 + 260, cy0, 1f, key, o, 1.5f, bg);
        Chk(tiles.LastDrawn == 0, $"a pan into the ring draws no tile on screen ({tiles.LastDrawn} drawn, {tiles.LastPrefetched} from background)");

        // a zoom drops the tiles and draws the zoomed view
        const float zk = 2f;
        var z = new Sr2d64CSport.Sprite(W, H); z.ClearBuffer(bg);
        tiles.Draw(img, z, cx0, cy0, zk, key, o, 1.5f, bg);
        var zd = new Sr2d64CSport.Sprite(W, H); zd.ClearBuffer(bg);
        double qz = Math.Round(cx0 * zk) / zk, qy = Math.Round(cy0 * zk) / zk;
        img.DrawVisible(zd, Sr2d64CSport.VectorViewCache.ViewMatrix(qz, qy, zk, W / 2f, H / 2f), o, 1.5f);
        double zmean = MeanDiff(z, zd, out int zdiff);
        Chk(zmean < 0.05, $"tile frame at zoom x2 equals the direct draw (mean difference {zmean:0.0000}, {zdiff} pixels differ)");
        Chk(tiles.Count <= tiles.MaxTiles, $"the cache holds at most {tiles.MaxTiles} tiles ({tiles.Count} now)");
        tiles.Dispose();
    }

    /// <summary>FastLines: thin strokes are painted without AA (the output changes only at thin strokes; fills keep AA).</summary>
    public static void FastLinesCheck()
    {
        var dir = FindDataDir();
        Chk(dir != null, "tests/data/mbtiles found (fast lines)");
        using var mb = MbTiles.Open(Path.Combine(dir!, "chel-z11-smallpages.mbtiles"));
        var img = Sr2d64CSport.VectorImage.FromMbTiles(mb, new Sr2d64CSport.MbTilesView { Zoom = 11, Labels = false, Threads = 1 });
        img.BuildIndex();
        const int W = 800, H = 500, bg = unchecked((int)0xFFF4F1E8);
        double cx = img.Width * 0.5, cy = img.Height * 0.5;
        var full = new Sr2d64CSport.Sprite(W, H); full.ClearBuffer(bg);
        var fast = new Sr2d64CSport.Sprite(W, H); fast.ClearBuffer(bg);
        var m = Sr2d64CSport.VectorViewCache.ViewMatrix(cx, cy, 1f, W / 2f, H / 2f);
        img.DrawVisible(full, m, new Sr2d64CSport.VectorRenderOptions { AA = true, FastLines = false }, 1f);
        img.DrawVisible(fast, m, new Sr2d64CSport.VectorRenderOptions { AA = true, FastLines = true }, 1f);
        int diff = 0; long sum = 0;
        for (int i = 0; i < full.Pixels.Length; i++)
        {
            if (full.Pixels[i] == fast.Pixels[i]) continue;
            diff++;
            for (int sh = 0; sh < 32; sh += 8) sum += Math.Abs(((full.Pixels[i] >>> sh) & 255) - ((fast.Pixels[i] >>> sh) & 255));
        }
        double mean = sum / (double)full.Pixels.Length / 4;
        Chk(diff > 0, $"FastLines changes thin strokes ({diff} pixels differ)");
        Chk(mean < 2.0, $"FastLines changes only the edges of thin strokes (mean difference {mean:0.000})");
        var off = new Sr2d64CSport.VectorRenderOptions { AA = true };
        Chk(!off.FastLines, "FastLines is off by default");
        double Ms(Sr2d64CSport.VectorRenderOptions opt)
        {
            var times = new List<double>();
            for (int r = 0; r < 5; r++)
            {
                var sp = new Sr2d64CSport.Sprite(W, H); sp.ClearBuffer(bg);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                img.DrawVisible(sp, m, opt, 1f);
                times.Add(sw.Elapsed.TotalMilliseconds); sp.Dispose();
            }
            times.Sort(); return times[2];
        }
        Console.WriteLine($"  info  FastLines frame (median of 5): full {Ms(new Sr2d64CSport.VectorRenderOptions { AA = true }):0.0} ms, fast lines {Ms(new Sr2d64CSport.VectorRenderOptions { AA = true, FastLines = true }):0.0} ms");
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
            RenderModeChecks(full);
        }
        else Console.WriteLine("  skip full-size sample (set MBTILES_FILE to chelyabinsk.mbtiles)");
    }

    // The grid index + DrawVisible: culled drawing must equal the full drawing, cull must drop shapes, labels must add shapes.
    // hairline outlines, the map level of detail, and the parallel tile import
    static void RenderModeChecks(string full)
    {
        static int Dark(Sprite sp, int x, int y)
        {
            int px = sp.Pixels[y * sp.Width + x];
            return ((px >> 16) & 255) < 128 && ((px >> 8) & 255) < 128 && (px & 255) < 128 ? 1 : 0;
        }
        var black = new VectorColor(unchecked((int)0xFF000000));
        var white = unchecked((int)0xFFFFFFFF);

        // 1. hairline: a 1-unit line is one device pixel at every scale; a normal 1-unit line grows with the scale
        var lineP = new VectorPath(); lineP.MoveTo(-8, 0); lineP.LineTo(8, 0);
        var hair = new VectorImage(); hair.Shapes.Add(new VectorShape(lineP, null, black, 1f) { Hairline = true });
        var thin = new VectorImage(); thin.Shapes.Add(new VectorShape(lineP, null, black, 1f));
        float Cover(VectorImage img, float sc)
        {   // ink across one column of the line = its width in device pixels
            var sp = new Sprite(200, 200); sp.ClearBuffer(white);
            img.Draw(sp, System.Numerics.Matrix3x2.CreateScale(sc) * System.Numerics.Matrix3x2.CreateTranslation(100, 100));
            float sum = 0;
            for (int y = 0; y < sp.Height; y++) sum += (255 - ((sp.Pixels[y * sp.Width + 100] >> 16) & 255)) / 255f;
            return sum;
        }
        foreach (float sc in new[] { 0.5f, 1f, 4f, 10f })
        {
            float c = Cover(hair, sc);
            Chk(c > 0.6f && c < 1.5f, $"hairline at scale {sc}: {c:0.00} px across (one device pixel)");
        }
        float thick = Cover(thin, 4f);
        Chk(thick > 3.5f, $"a 1-unit stroke at scale 4 is {thick:0.0} px across (it scales; the hairline does not)");

        // 2. map level of detail: only a map (IsMap) leaves out small buildings; other pictures keep every shape
        VectorImage Scene(bool map)
        {
            var v = new VectorImage { IsMap = map };
            v.MapLayerMinPx["building"] = 2.5f;
            void Square(float x, float y, float w, string layer)
            {
                var p = new VectorPath(); p.MoveTo(x, y); p.LineTo(x + w, y); p.LineTo(x + w, y + w); p.LineTo(x, y + w); p.Close();
                v.Shapes.Add(new VectorShape(p, black, null, 0f) { Layer = layer });
            }
            Square(10, 10, 1, "building");       // a house 1 px wide at scale 1: left out of a map
            Square(50, 50, 40, "building");      // a big building: always drawn
            Square(150, 150, 1, "landuse");      // a small piece of another layer: the building rule does not touch it
            return v;
        }
        var mapPic = new Sprite(200, 200); mapPic.ClearBuffer(white); Scene(true).DrawVisible(mapPic, System.Numerics.Matrix3x2.Identity);
        var plainPic = new Sprite(200, 200); plainPic.ClearBuffer(white); Scene(false).DrawVisible(plainPic, System.Numerics.Matrix3x2.Identity);
        Chk(Dark(mapPic, 10, 10) == 0 && Dark(plainPic, 10, 10) == 1, "map: a 1 px building is left out; a picture that is not a map keeps it");
        Chk(Dark(mapPic, 60, 60) == 1 && Dark(mapPic, 150, 150) == 1, "map: a big building and other layers are drawn");

        // 3. parallel tile import: the same picture at any thread count, with the time for each
        if (full == null) return;
        var sw1 = Stopwatch.StartNew();
        var serial = VectorImage.FromMbTiles(full, new MbTilesView { Zoom = 14, Threads = 1 });
        long t1 = sw1.ElapsedMilliseconds;
        var sw2 = Stopwatch.StartNew();
        var parallel = VectorImage.FromMbTiles(full, new MbTilesView { Zoom = 14, Threads = 0 });
        long t2 = sw2.ElapsedMilliseconds;
        Console.WriteLine($"  time  z14 import, whole file: 1 thread {t1} ms, {Environment.ProcessorCount} threads {t2} ms ({serial.Shapes.Count} shapes)");
        Chk(serial.Shapes.Count == parallel.Shapes.Count && serial.Warnings.SequenceEqual(parallel.Warnings), "parallel import: same shapes and warnings, in the same order");
        int buildings = serial.Shapes.Count(sh => sh.Layer == "building");
        Chk(buildings > 0 && serial.Shapes.Where(sh => sh.Layer == "building").All(sh => sh.Hairline), $"building outlines are hairlines ({buildings} shapes)");
        var va = serial.VisibleArea!.Value;
        float k = Math.Min(800f / va.Width, 600f / va.Height);
        var m = System.Numerics.Matrix3x2.CreateScale(k) * System.Numerics.Matrix3x2.CreateTranslation(-va.X * k, -va.Y * k);
        var pa = new Sprite(800, 600); pa.ClearBuffer(white); serial.DrawVisible(pa, m, null, 1.5f);
        var pb = new Sprite(800, 600); pb.ClearBuffer(white); parallel.DrawVisible(pb, m, null, 1.5f);
        int diff = 0; for (int i = 0; i < pa.Pixels.Length; i++) if (pa.Pixels[i] != pb.Pixels[i]) diff++;
        Chk(diff == 0, $"parallel import draws the same pixels ({diff} different)");
        // pictures of the real map for a look: fitted, and zoomed 4x on the middle (buildings are hairlines at every zoom)
        SaveSprite(pa, "map_fit.png");
        float cx = va.X + va.Width / 2, cy = va.Y + va.Height / 2;
        var z4 = System.Numerics.Matrix3x2.CreateScale(k * 4) * System.Numerics.Matrix3x2.CreateTranslation(400 - cx * k * 4, 300 - cy * k * 4);
        var pz = new Sprite(800, 600); pz.ClearBuffer(white); serial.DrawVisible(pz, z4, null, 1.5f);
        SaveSprite(pz, "map_zoom4.png");
    }

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
