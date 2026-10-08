using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;

namespace Sr2d64CSport
{
    /// <summary>
    /// A scalable font for <see cref="Sprite.DrawString"/> (named SpriteFont so it never collides with System.Drawing.Font in WinForms code): a TrueType / OpenType (glyf or CFF) / bare CFF / Type 1 program
    /// (parsed by cs/Vector.Fonts.cs, the same reader the PDF importer uses) plus a cache of rasterised glyphs per size.
    /// Nothing from System.Drawing.Text or GDI is involved: glyph outlines are filled with SR2D's own anti-aliased polygon
    /// rasteriser into 8-bit coverage bitmaps (once per glyph, size and 1/4-pixel horizontal phase) and composited with
    /// FILL_MASK8, so drawn text supports every LineOp (alpha blend, add, xor, ...) and premultiplied targets.
    ///
    /// Load once, keep it: <c>var f = SpriteFont.Load(@"C:\Windows\Fonts\segoeui.ttf");</c> or <c>SpriteFont.Installed("Segoe UI")</c>.
    /// Sizes are in pixels (em height). A SpriteFont is safe to share between sprites; it is not thread-safe for concurrent draws
    /// (use one SpriteFont per thread with DrawParallel, or lock).
    /// </summary>
    public sealed partial class SpriteFont : IDisposable, IFallbackFont
    {
        readonly GlyphFont prog;
        readonly Dictionary<long, Glyph?> glyphs = new Dictionary<long, Glyph?>();
        readonly Dictionary<int, int> gidByChar = new Dictionary<int, int>();
        Sprite? scratch; int scratchW, scratchH; long cacheBytes;
        bool disposed;

        /// <summary>Largest total size of cached glyph bitmaps before the cache is emptied (default 16 MB per SpriteFont).</summary>
        public long CacheLimit = 16L << 20;
        /// <summary>Number of horizontal sub-pixel positions per glyph bitmap (1 = whole pixels, 4 = default, smoother spacing at small sizes; costs 4x the cache).</summary>
        public int SubPixelPositions { get => subPos; set { value = Math.Clamp(value, 1, 16); if (subPos == value) return; subPos = value; ClearCache(); } }
        int subPos = 4;
        /// <summary>Applies the font's pair kerning ("kern" table) when laying out text (default true).</summary>
        public bool Kerning = true;
        /// <summary>Extra space added between glyphs, in pixels (can be negative).</summary>
        public float LetterSpacing { get; set; }
        /// <summary>Glyph index drawn for characters the font has no glyph for (0 = the font's .notdef box; -1 = skip them).</summary>
        public int MissingGlyph { get => missing; set { if (missing == value) return; missing = value; gidByChar.Clear(); } }
        int missing;
        /// <summary>Simulated bold: outlines are expanded by this fraction of the em (0.03 looks like a semi-bold; 0 = off). Changing it drops the glyph cache.</summary>
        public float FakeBold { get => fakeBold; set { if (fakeBold == value) return; fakeBold = value; ClearCache(); } }
        float fakeBold;
        /// <summary>Simulated italic: horizontal shear (0.2 = typical oblique; 0 = off). Changing it drops the glyph cache.</summary>
        public float FakeItalic { get => fakeItalic; set { if (fakeItalic == value) return; fakeItalic = value; ClearCache(); } }
        float fakeItalic;

        SpriteFont(GlyphFont p, string name) { prog = p; Name = name; p.Warnings ??= Warnings; }

        /// <summary>The file / description the font came from.</summary>
        public string Name { get; }
        /// <summary>Family name from the font's name table ("" when the program has none, e.g. bare CFF).</summary>
        public string FamilyName => prog.FamilyName.Length > 0 ? prog.FamilyName : Path.GetFileNameWithoutExtension(Name);
        public string StyleName => prog.StyleName;
        /// <summary>The parsed program (glyph outlines in em units, cmap lookups, metrics) for anything the Font API does not expose.</summary>
        public GlyphFont Program => prog;
        public int GlyphCount => prog.GlyphCount;
        /// <summary>Typographic ascent / descent / line gap in em units (multiply by the size in pixels).</summary>
        public float AscentEm => prog.Ascent;
        public float DescentEm => prog.Descent;
        public float LineGapEm => prog.LineGap;
        /// <summary>Recommended baseline-to-baseline distance in pixels for <paramref name="size"/>.</summary>
        public float LineHeight(float size) => (prog.Ascent + prog.Descent + prog.LineGap) * size;
        public float Ascent(float size) => prog.Ascent * size;
        public float Descent(float size) => prog.Descent * size;

        // ------------------------------------------------------------------ loading
        /// <summary>Loads a .ttf / .otf / .ttc (face <paramref name="index"/>) / .pfb / .pfa / bare CFF file.</summary>
        public static SpriteFont Load(string fileName, int index = 0) => Load(File.ReadAllBytes(fileName), fileName, index);
        /// <summary>Loads a font program from memory (embedded resource, download, ...).</summary>
        public static SpriteFont Load(byte[] data, string? name = null, int index = 0)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data == null || data.Length < 4) throw new ArgumentException("empty font data", nameof(data));
            ImportLog.Begin(); List<string>? sink = null;
            try
            {
                GlyphFont? p = null;
                uint tag = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
                if (tag == 0x74746366 && index > 0) p = new TrueTypeFont(data, index);
                p ??= GlyphFont.Parse(data) ?? throw new InvalidDataException("not a TrueType / OpenType / CFF / Type 1 font");
                if (p.GlyphCount == 0) throw new InvalidDataException("the font program has no glyphs");
                var f = new SpriteFont(p, name ?? "(memory)"); sink = f.Warnings; return f;
            }
            finally { ImportLog.End(sink); }
        }
        /// <summary>Problems the parser survived while loading this font (broken tables / glyphs that were skipped); empty for a clean file. See <see cref="ImportLog"/>.</summary>
        public readonly List<string> Warnings = new List<string>();
        public static SpriteFont Load(Stream s, string? name = null, int index = 0) { ArgumentNullException.ThrowIfNull(s); using var ms = new MemoryStream(); s.CopyTo(ms); return Load(ms.ToArray(), name, index); }
        /// <summary>Tries to load; null instead of an exception.</summary>
        public static SpriteFont? TryLoad(string fileName, int index = 0) { try { return File.Exists(fileName) ? Load(fileName, index) : null; } catch (Exception e) { ImportLog.Swallowed(e, "SpriteFont.TryLoad " + fileName); return null; } }

        /// <summary>
        /// Finds an installed font by family name in the system font directories (Windows: %WINDIR%\Fonts and the per-user
        /// folder; Linux / macOS: the usual font trees), matching the name table; <paramref name="bold"/> / <paramref name="italic"/>
        /// pick the style. Null when nothing matches. The directory scan is cached for the process.
        /// </summary>
        public static SpriteFont? Installed(string family, bool bold = false, bool italic = false)
        {
            var path = FindSystemFont(family, bold, italic);
            return path == null ? null : TryLoad(path.Value.file, path.Value.index);
        }
        /// <summary>Names of the font families found in the system font directories (for a font picker).</summary>
        public static IReadOnlyList<string> SystemFamilies() { ScanSystem(); var l = new List<string>(sysIndex!.Keys); l.Sort(StringComparer.OrdinalIgnoreCase); return l; }
        /// <summary>Directories searched by <see cref="Installed"/>; edit before the first call to add your own.</summary>
        public static readonly List<string> SystemFontDirectories = DefaultFontDirs();

        static Dictionary<string, List<(string file, int index, bool bold, bool italic, string style)>>? sysIndex;
        static readonly object sysLock = new object();
        static List<string> DefaultFontDirs()
        {
            var l = new List<string>();
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var win = Environment.GetEnvironmentVariable("WINDIR"); if (!string.IsNullOrEmpty(win)) l.Add(Path.Combine(win, "Fonts"));
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); if (!string.IsNullOrEmpty(local)) l.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
                }
                else
                {
                    l.Add("/usr/share/fonts"); l.Add("/usr/local/share/fonts"); l.Add("/Library/Fonts"); l.Add("/System/Library/Fonts");
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(home)) { l.Add(Path.Combine(home, ".fonts")); l.Add(Path.Combine(home, ".local", "share", "fonts")); l.Add(Path.Combine(home, "Library", "Fonts")); }
                }
            }
            catch (Exception e) { ImportLog.Swallowed(e, "system font folders"); }
            return l;
        }
        static void ScanSystem()
        {
            lock (sysLock)
            {
                if (sysIndex != null) return;
                var idx = new Dictionary<string, List<(string, int, bool, bool, string)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in SystemFontDirectories)
                {
                    if (!Directory.Exists(dir)) continue;
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories); } catch (Exception e) { ImportLog.Swallowed(e, "font directory scan " + dir); continue; }
                    foreach (var f in files)
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".ttf" && ext != ".otf" && ext != ".ttc" && ext != ".otc") continue;
                        try
                        {
                            var data = File.ReadAllBytes(f);
                            int faces = 1;
                            if (data.Length > 12 && data[0] == 't' && data[1] == 't' && data[2] == 'c' && data[3] == 'f') faces = Math.Min(64, data[8] << 24 | data[9] << 16 | data[10] << 8 | data[11]);
                            for (int i = 0; i < faces; i++)
                            {
                                var tt = new TrueTypeFont(data, i);
                                string fam = tt.FamilyName, st = tt.StyleName; if (fam.Length == 0) continue;
                                string sl = st.ToLowerInvariant();
                                bool b = sl.Contains("bold") || sl.Contains("black") || sl.Contains("heavy") || sl.Contains("semibold") || sl.Contains("demibold");
                                bool it = sl.Contains("italic") || sl.Contains("oblique");
                                if (!idx.TryGetValue(fam, out var list)) idx[fam] = list = new List<(string, int, bool, bool, string)>();
                                list.Add((f, i, b, it, st));
                            }
                        }
                        catch (Exception e) { ImportLog.Swallowed(e, "indexing font file " + f); }
                    }
                }
                sysIndex = idx;
            }
        }
        static (string file, int index)? FindSystemFont(string family, bool bold, bool italic)
        {
            ScanSystem();
            if (!sysIndex!.TryGetValue(family, out var faces) || faces.Count == 0) return null;
            (string, int)? best = null; int bestScore = -1;
            foreach (var (file, index, b, it, style) in faces)
            {
                int score = (b == bold ? 2 : 0) + (it == italic ? 2 : 0);
                string sl = style.ToLowerInvariant();
                if (!bold && !b && (sl == "regular" || sl == "normal" || sl == "book" || sl == "roman")) score++;
                if (bold && b && !(sl.Contains("black") || sl.Contains("heavy") || sl.Contains("semi") || sl.Contains("demi"))) score++;   // plain bold over black / semibold
                if (score > bestScore) { bestScore = score; best = (file, index); }
            }
            return best;
        }

        // ------------------------------------------------------------------ glyph lookup
        /// <summary>Glyph index of a Unicode code point (cmap; falls back to glyph names for Type 1 / CFF); <see cref="MissingGlyph"/> when absent.</summary>
        public int GlyphIndex(int codePoint)
        {
            if (gidByChar.TryGetValue(codePoint, out int g)) return g;
            g = prog.GidByUnicode(codePoint);
            if (g < 0 && !prog.HasCmap)
            {   // Type 1 / bare CFF: by glyph name, then by the program's built-in encoding
                var names = VectorFontData.GlyphListReverse;
                if (names.TryGetValue(codePoint, out var nm)) g = prog.GidByName(nm);
                if (g < 0 && codePoint < 256) g = prog.GidByCode(codePoint);
                if (g < 0 && codePoint <= 0xFFFF) g = prog.GidByName($"uni{codePoint:X4}");
            }
            if (g < 0 && codePoint < 256 && prog is TrueTypeFont tt && tt.HasSymbolCmap) g = prog.GidByCode(codePoint);
            if (g < 0) g = MissingGlyph;
            gidByChar[codePoint] = g; return g;
        }
        public bool HasGlyph(int codePoint) => prog.GidByUnicode(codePoint) > 0 || (!prog.HasCmap && GlyphIndex(codePoint) > 0);
        /// <summary>Advance width of a glyph in pixels at <paramref name="size"/> (before letter spacing / kerning).</summary>
        public float Advance(int gid, float size) => prog.Advance(gid) * size;
        /// <summary>Glyph outline in em units (y up), null for empty glyphs (space) - for your own effects (outlines, paths along curves, ...).</summary>
        public VectorPath? Outline(int gid) => prog.Glyph(gid);
        /// <summary>Glyph outline in pixels (y down, origin on the baseline) at <paramref name="size"/>.</summary>
        public VectorPath? Outline(int gid, float size)
        {
            var p = prog.Glyph(gid); if (p == null) return null;
            var m = new Matrix3x2(size, 0, 0, -size, 0, 0);
            if (FakeItalic != 0) m = new Matrix3x2(1, 0, FakeItalic, 1, 0, 0) * m;
            return p.Transformed(m);
        }

        // ------------------------------------------------------------------ layout
        /// <summary>One positioned glyph of a laid-out string.</summary>
        public readonly struct Placed
        {
            public readonly int Gid; public readonly float X; public readonly int CharIndex;
            public Placed(int gid, float x, int charIndex) { Gid = gid; X = x; CharIndex = charIndex; }
        }
        /// <summary>
        /// Lays out one line: glyph indices and pen x positions (pixels, relative to the start), applying kerning and letter
        /// spacing. Returns the total advance. Control characters other than tab are skipped; a tab advances to the next
        /// multiple of <paramref name="tabWidth"/> (pixels; 0 = 4 spaces).
        /// </summary>
        public float Layout(ReadOnlySpan<char> text, float size, List<Placed> outGlyphs, float tabWidth = 0)
        {
            float x = 0; int prev = -1;
            if (tabWidth <= 0) tabWidth = Advance(GlyphIndex(' '), size) * 4;
            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(text[i], text[i + 1]); i++; }
                if (cp == '\t') { x = (MathF.Floor(x / tabWidth) + 1) * tabWidth; prev = -1; continue; }
                if (cp < 32 || cp == 0x7F || (cp >= 0x200B && cp <= 0x200F) || cp == 0xFEFF) continue;
                int g = GlyphIndex(cp); if (g < 0) continue;
                if (Kerning && prev >= 0) x += prog.Kerning(prev, g) * size;
                outGlyphs.Add(new Placed(g, x, i));
                x += Advance(g, size) + LetterSpacing;
                prev = g;
            }
            if (outGlyphs.Count > 0) x -= LetterSpacing;
            return MathF.Max(0, x);
        }
        /// <summary>Width in pixels of one line of text at <paramref name="size"/>.</summary>
        public float Measure(ReadOnlySpan<char> text, float size) { var l = tmpLayout ??= new List<Placed>(64); l.Clear(); return Layout(text, size, l); }
        [ThreadStatic] static List<Placed>? tmpLayout;
        /// <summary>Size of a (possibly multi-line, '\n'-separated) text block: widest line x line count * <see cref="LineHeight"/>.</summary>
        public SizeF MeasureBlock(string text, float size, float lineSpacing = 1f)
        {
            float w = 0; int lines = 0;
            foreach (var line in text.Split('\n')) { w = MathF.Max(w, Measure(line.TrimEnd('\r'), size)); lines++; }
            return new SizeF(w, lines * LineHeight(size) * lineSpacing);
        }
        /// <summary>
        /// Breaks text into lines no wider than <paramref name="maxWidth"/> pixels (at spaces; a single word longer than
        /// the width is broken mid-word). Existing '\n' are kept.
        /// </summary>
        public List<string> Wrap(string text, float size, float maxWidth)
        {
            var result = new List<string>();
            foreach (var para in text.Split('\n'))
            {
                var line = para.TrimEnd('\r');
                if (Measure(line, size) <= maxWidth) { result.Add(line); continue; }
                var words = line.Split(' '); var cur = new System.Text.StringBuilder();
                foreach (var word in words)
                {
                    string trial = cur.Length == 0 ? word : cur + " " + word;
                    if (Measure(trial, size) <= maxWidth) { cur.Clear(); cur.Append(trial); continue; }
                    if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                    // the word alone
                    if (Measure(word, size) <= maxWidth) { cur.Append(word); continue; }
                    int start = 0;
                    while (start < word.Length)
                    {
                        int len = 1; while (start + len < word.Length && Measure(word.AsSpan(start, len + 1), size) <= maxWidth) len++;
                        if (start + len < word.Length) result.Add(word.Substring(start, len)); else cur.Append(word, start, len);
                        start += len;
                    }
                }
                result.Add(cur.ToString());
            }
            return result;
        }

        // ------------------------------------------------------------------ glyph bitmaps
        /// <summary>A rasterised glyph: 8-bit coverage, origin offset relative to the pen position (baseline, left).</summary>
        internal sealed class Glyph
        {
            public byte[] Cov = Array.Empty<byte>(); public int W, H, Left, Top;   // Top = rows above the baseline (bitmap y = baseline - Top)
            public bool Empty => W == 0 || H == 0;
        }
        static long Key(int gid, float size, int phase) => (long)gid << 40 | (long)(BitConverter.SingleToInt32Bits(size) & 0xFFFFFFFFL) << 8 | (uint)phase;

        /// <summary>The cached bitmap of a glyph at a size and sub-pixel phase (0..SubPixelPositions-1); rasterised on first use.</summary>
        internal Glyph? GetGlyph(int gid, float size, int phase)
        {
            long key = Key(gid, size, phase);
            if (glyphs.TryGetValue(key, out var g)) return g;
            g = Rasterise(gid, size, phase);
            if (cacheBytes > CacheLimit) { glyphs.Clear(); cacheBytes = 0; }
            glyphs[key] = g; if (g != null) cacheBytes += g.Cov.Length + 32;
            return g;
        }
        unsafe Glyph? Rasterise(int gid, float size, int phase)
        {
            var path = prog.Glyph(gid); if (path == null) return null;
            float sub = SubPixelPositions <= 1 ? 0 : (float)phase / SubPixelPositions;
            // em -> pixels, y down, plus the sub-pixel shift; fake italic as a shear about the baseline
            var m = new Matrix3x2(size, 0, 0, -size, sub, 0);
            if (FakeItalic != 0) m = new Matrix3x2(1, 0, FakeItalic, 1, 0, 0) * m;
            var pts = new List<PointF>(256);
            var subs = VectorRender.Flatten(path, m, MathF.Min(0.2f, MathF.Max(0.02f, 0.25f * 12 / MathF.Max(size, 1))), pts);
            if (pts.Count < 3) return null;
            float bl = float.MaxValue, bt = float.MaxValue, br = float.MinValue, bb = float.MinValue;
            foreach (var p in pts) { if (p.X < bl) bl = p.X; if (p.X > br) br = p.X; if (p.Y < bt) bt = p.Y; if (p.Y > bb) bb = p.Y; }
            float grow = FakeBold > 0 ? FakeBold * size : 0;
            int x0 = (int)MathF.Floor(bl - grow), y0 = (int)MathF.Floor(bt - grow), x1 = (int)MathF.Ceiling(br + grow) + 1, y1 = (int)MathF.Ceiling(bb + grow) + 1;
            int w = x1 - x0, h = y1 - y0; if (w <= 0 || h <= 0 || (long)w * h > 64L << 20) return null;
            if (scratch == null || scratchW < w || scratchH < h)
            {
                scratch?.Dispose(); scratchW = Math.Max(w, Math.Max(scratchW, 64)); scratchH = Math.Max(h, Math.Max(scratchH, 64));
                scratch = new Sprite(scratchW, scratchH, SR2D.Op.Paint);
            }
            scratch.SetLockRect(0, w, 0, h); scratch.ClearRect(0, w, 0, h, 0);
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pts);
            var cnt = new int[subs.Count]; for (int i = 0; i < subs.Count; i++) cnt[i] = subs[i].count;
            // FillPolygons takes pixel-centre coordinates (adds 0.5); the outline is in area coordinates
            for (int i = 0; i < span.Length; i++) span[i] = new PointF(span[i].X - x0 - 0.5f, span[i].Y - y0 - 0.5f);
            scratch.FillPolygons(span, cnt, unchecked((int)0xFFFFFFFF), SR2D.LineOp.Set, true, false);
            if (grow > 0)
            {   // fake bold: also stroke the outline with the growth width (union with the fill)
                var st = new StrokeStyle(grow * 2, LineCap.Round, LineJoin.Round);
                var o = new List<PointF>(); var oc = new List<int>();
                foreach (var (start, count, closed) in subs) Sprite.StrokeOutline(span.Slice(start, count), true, st, o, oc);
                if (oc.Count > 0) scratch.FillPolygons(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(o), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(oc), unchecked((int)0xFFFFFFFF), SR2D.LineOp.Max, true, false);
            }
            var g = new Glyph { W = w, H = h, Left = x0, Top = -y0, Cov = new byte[w * h] };
            int* src = scratch.PixelPtr; int sw = scratch.Width;
            fixed (byte* dst = g.Cov)
                for (int y = 0; y < h; y++) { int* s = src + y * sw; byte* d = dst + y * w; for (int x = 0; x < w; x++) d[x] = (byte)(s[x] >>> 24); }
            return g;
        }
        /// <summary>Drops every cached glyph bitmap (after changing FakeBold / FakeItalic / SubPixelPositions, or to free memory).</summary>
        public void ClearCache() { glyphs.Clear(); cacheBytes = 0; }
        public long CacheBytes => cacheBytes;

        public void Dispose() { if (disposed) return; disposed = true; scratch?.Dispose(); scratch = null; glyphs.Clear(); }
        public override string ToString() => $"{FamilyName} {StyleName} ({GlyphCount} glyphs)";
    }
}
