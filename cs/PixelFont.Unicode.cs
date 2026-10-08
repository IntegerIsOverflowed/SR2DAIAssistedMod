using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Sr2d64CSport
{
    // The built-in 5x7 font beyond ASCII (PixelFont.Default only - a font you supply keeps its own character set):
    //
    //   * hand-drawn glyphs: Cyrillic (Russian, Ukrainian, Belarusian, Serbian except Љ Њ), Greek, the Latin-1 symbols
    //     (¡ ¿ « » § ¶ © ® ¢ £ ¥ € ...), ß ð þ æ ø œ ł đ, typographic quotes / dashes / bullet, a few UI arrows and shapes;
    //   * aliases: letters that look the same as a Latin one (Cyrillic А В Е К М Н О Р С Т Х, Greek Α Β Ε Ζ Η Ι Κ Μ Ν Ο Ρ Τ Υ Χ ...);
    //   * composed letters: base + diacritic (é = e + acute, Й = И + breve, Ё = Е + diaeresis, ą = a + ogonek ...), built on
    //     first use from a decomposition table - Latin-1, Latin Extended-A, the accented Cyrillic and Greek tonos letters.
    //     A capital (7 rows) is squashed to 5 rows to make room for the mark, a lowercase letter (x-height, rows 2..6) gets
    //     the mark in the two free rows above, i / j lose their dot; cedilla / ogonek go under a letter lifted by one row.
    //
    // Everything still missing (CJK, Arabic, Hebrew, emoji, rare symbols) is drawn with PixelFont.FallbackFont when one
    // is installed - see Sprite.Text.cs. PixelFont.Has(ch) tells the two apart.
    /// <summary>A vector font the pixel font can borrow glyphs from (<see cref="SpriteFont"/> implements it; see <see cref="PixelFont.FallbackFonts"/>).</summary>
    public interface IFallbackFont
    {
        /// <summary>True when the font has a glyph for the code point.</summary>
        bool HasGlyph(int codePoint);
        /// <summary>Advance width of <paramref name="text"/> at em size <paramref name="em"/> (pixels).</summary>
        float MeasureFallback(ReadOnlySpan<char> text, float em);
        /// <summary>Draws <paramref name="text"/> with the pen at (<paramref name="x"/>, <paramref name="baselineY"/>).</summary>
        void DrawFallback(Sprite target, float x, float baselineY, ReadOnlySpan<char> text, float em, int color, SR2D.LineOp op, int blendFactor);
    }

    public sealed partial class PixelFont
    {
        /// <summary>True when the font has a picture for <paramref name="ch"/> (own glyph, built-in extension or a composable accent); false = the box / the fallback font.</summary>
        public bool Has(char ch) => Find(ch) != null;

        ushort[]? Find(char ch)
        {
            int i = ch - FirstChar;
            if ((uint)i < (uint)rows.Length) return rows[i];
            if (extras.TryGetValue(ch, out var r)) return r;
            if (!extended) return null;
            if (composed.TryGetValue(ch, out r)) return r;       // also remembers misses (null) - DrawText asks for CJK etc. repeatedly
            return composed.GetOrAdd(ch, Compose(ch));
        }
        bool extended;   // the built-in 5x7 font: aliases + hand glyphs registered, composition allowed
        readonly ConcurrentDictionary<char, ushort[]?> composed = new ConcurrentDictionary<char, ushort[]?>();

        // ---------------------------------------------------------------- composition
        static Dictionary<char, (char b, char m)>? decomp;

        ushort[]? Compose(char ch)
        {
            if (GlyphWidth != 5 || GlyphHeight != 7) return null;
            decomp ??= BuildDecomp();
            if (!decomp.TryGetValue(ch, out var d)) return null;
            var b = Find(d.b); if (b == null) return null;
            var m = Mark(d.m); if (m == null) return null;
            var r = new ushort[7];
            int top = 0; while (top < 7 && b[top] == 0) top++;
            int bottom = 6; while (bottom >= 0 && b[bottom] == 0) bottom--;
            if (top > 6) return null;
            if (m.Length == 2)
            {   // above: make two free rows over the body
                if (top >= 2) { Array.Copy(b, r, 7); }
                else if (b[1] == 0 && top == 0 && b[2] != 0) { Array.Copy(b, r, 7); r[0] = 0; top = 2; }      // i, j: drop the dot
                else { r[2] = b[0]; r[3] = b[1]; r[4] = b[3]; r[5] = b[5]; r[6] = b[6]; top = 2; }          // capital: 7 -> 5 rows
                r[top - 2] = m[0]; r[top - 1] = m[1];
            }
            else
            {   // below: one free row under the body
                if (bottom <= 5) { Array.Copy(b, r, 7); }
                else if (top >= 2) { for (int y = 1; y <= 5; y++) r[y] = b[y + 1]; bottom = 5; }             // lift the x-height letter
                else { r[0] = b[0]; r[1] = b[1]; r[2] = b[2]; r[3] = b[3]; r[4] = b[5]; r[5] = b[6]; bottom = 5; }   // capital: 7 -> 6 rows
                r[bottom + 1] = m[0];
            }
            return r;
        }
        static ushort[]? Mark(char m) => m switch
        {
            '\u00b4' => P("...#.", "..#.."),   // acute
            '`' => P(".#...", "..#.."),        // grave
            '^' => P("..#..", ".#.#."),        // circumflex
            '\u00a8' => P(".....", ".#.#."),   // diaeresis
            '~' => P(".##.#", "#.##."),        // tilde
            '\u00b0' => P(".###.", ".#.#."),   // ring
            '\u02d8' => P("#...#", ".###."),   // breve
            '\u02c7' => P(".#.#.", "..#.."),   // caron
            '\u00af' => P(".....", ".###."),   // macron
            '\u02d9' => P(".....", "..#.."),   // dot above
            '\u02dd' => P("..#.#", ".#.#."),   // double acute
            '\u00b8' => P("..#.."),            // cedilla (below)
            '\u02db' => P("...#."),            // ogonek (below)
            _ => null,
        };
        static ushort[] P(params string[] pic)
        {
            var r = new ushort[pic.Length];
            for (int y = 0; y < pic.Length; y++) { int v = 0; foreach (char c in pic[y]) v = (v << 1) | (c == '#' ? 1 : 0); r[y] = (ushort)v; }
            return r;
        }
        // "composed base mark" triplets
        const string Decomp =
            "ÀA`ÁA´ÂA^ÃA~ÄA¨ÅA°ÇC¸ÈE`ÉE´ÊE^ËE¨ÌI`ÍI´ÎI^ÏI¨ÑN~ÒO`ÓO´ÔO^ÕO~ÖO¨ÙU`ÚU´ÛU^ÜU¨ÝY´" +
            "àa`áa´âa^ãa~äa¨åa°çc¸èe`ée´êe^ëe¨ìi`íi´îi^ïi¨ñn~òo`óo´ôo^õo~öo¨ùu`úu´ûu^üu¨ýy´ÿy¨" +
            "ĀA¯āa¯ĂA˘ăa˘ĄA˛ąa˛ĆC´ćc´ĈC^ĉc^ĊC˙ċc˙ČCˇčcˇĎDˇďdˇĒE¯ēe¯ĔE˘ĕe˘ĖE˙ėe˙ĘE˛ęe˛ĚEˇěeˇ" +
            "ĜG^ĝg^ĞG˘ğg˘ĠG˙ġg˙ĢG¸ģg¸ĤH^ĥh^ĨI~ĩi~ĪI¯īi¯ĬI˘ĭi˘ĮI˛įi˛İI˙ĴJ^ĵj^ĶK¸ķk¸ĹL´ĺl´ĻL¸ļl¸ĽLˇľlˇ" +
            "ŃN´ńn´ŅN¸ņn¸ŇNˇňnˇŌO¯ōo¯ŎO˘ŏo˘ŐO˝őo˝ŔR´ŕr´ŖR¸ŗr¸ŘRˇřrˇŚS´śs´ŜS^ŝs^ŞS¸şs¸ŠSˇšsˇ" +
            "ŢT¸ţt¸ŤTˇťtˇŨU~ũu~ŪU¯ūu¯ŬU˘ŭu˘ŮU°ůu°ŰU˝űu˝ŲU˛ųu˛ŴW^ŵw^ŶY^ŷy^ŸY¨ŹZ´źz´ŻZ˙żz˙ŽZˇžzˇ" +
            "ȘS¸șs¸ȚT¸țt¸ǍAˇǎaˇǏIˇǐiˇǑOˇǒoˇǓUˇǔuˇ" +
            "ЙИ˘йи˘ЁЕ¨ёе¨ЎУ˘ўу˘ЇІ¨їі¨ЃГ´ѓг´ЌК´ќк´" +
            "ΆΑ´ΈΕ´ΉΗ´ΊΙ´ΌΟ´ΎΥ´ΏΩ´άα´έε´ήη´ίι´όο´ύυ´ώω´ϊι¨ϋυ¨ΪΙ¨ΫΥ¨";
        static Dictionary<char, (char, char)> BuildDecomp()
        {
            var d = new Dictionary<char, (char, char)>(Decomp.Length / 3);
            for (int i = 0; i + 2 < Decomp.Length; i += 3) d[Decomp[i]] = (Decomp[i + 1], Decomp[i + 2]);
            return d;
        }

        // ---------------------------------------------------------------- real-font fallback
        /// <summary>
        /// Draw characters the pixel font lacks (CJK, Arabic, emoji, rare symbols ...) with an installed vector font instead of the box.
        /// Default true; the first miss loads the first family of <see cref="FallbackFamilies"/> that is installed and has the glyph.
        /// </summary>
        public static bool UseFallbackFont { get; set; } = true;
        /// <summary>
        /// Em size of a fallback glyph relative to the pixel cell height (glyph height * scale); 1.3 puts CJK / caps roughly at the cell height.
        /// </summary>
        public static float FallbackEm { get; set; } = 1.3f;
        /// <summary>Fonts you loaded yourself (<see cref="SpriteFont"/>s), tried first (in order) for a character the pixel font lacks. Call <see cref="ResetFallbackFonts"/> after editing.</summary>
        public static readonly List<IFallbackFont> FallbackFonts = new List<IFallbackFont>();
        /// <summary>
        /// Turns a family name of <see cref="FallbackFamilies"/> into a font (null = not installed). SpriteFont.cs registers
        /// <c>SpriteFont.Installed</c> when it is compiled in (module initialiser in Sprite.Font.cs); without it, or when you set this to null,
        /// only <see cref="FallbackFonts"/> are used. PixelFont.cs / Sprite.Text.cs themselves do not depend on the vector font code.
        /// </summary>
        public static Func<string, IFallbackFont?>? FallbackLoader { get; set; }
        /// <summary>System families tried (in order, loaded on first need) after <see cref="FallbackFonts"/>. Edit before the first draw, or call <see cref="ResetFallbackFonts"/>.</summary>
        public static readonly List<string> FallbackFamilies = new List<string>
        {
            "Segoe UI", "Segoe UI Symbol", "Microsoft YaHei", "Malgun Gothic", "Yu Gothic UI", "Nirmala UI", "Arial",   // Windows
            "DejaVu Sans", "Noto Sans", "Noto Sans CJK SC", "Noto Sans Symbols", "Liberation Sans", "FreeSans",      // Linux
            "Helvetica Neue", "PingFang SC", "Apple Symbols",                                                     // macOS
        };
        /// <summary>The first fallback font that has <paramref name="codePoint"/> (null = none installed / none has it - the box is drawn).</summary>
        public static IFallbackFont? FallbackFor(int codePoint)
        {
            if (fbByCp.TryGetValue(codePoint, out var f)) return f;
            f = null;
            lock (fbLock)
            {
                foreach (var uf in FallbackFonts) if (uf != null && uf.HasGlyph(codePoint)) { f = uf; break; }
                var loader = FallbackLoader;
                for (int i = 0; f == null && loader != null && i < FallbackFamilies.Count; i++)
                {
                    while (fbLoaded.Count <= i) fbLoaded.Add(null);
                    while (fbTried.Count <= i) fbTried.Add(false);
                    if (!fbTried[i]) { fbTried[i] = true; fbLoaded[i] = loader(FallbackFamilies[i]); }
                    var sf = fbLoaded[i];
                    if (sf != null && sf.HasGlyph(codePoint)) f = sf;
                }
            }
            fbByCp[codePoint] = f;
            return f;
        }
        /// <summary>Forgets the loaded fallback fonts and the per-character choice (after editing <see cref="FallbackFonts"/> / <see cref="FallbackFamilies"/>).</summary>
        public static void ResetFallbackFonts() { lock (fbLock) { fbLoaded.Clear(); fbTried.Clear(); fbByCp.Clear(); } }
        static readonly object fbLock = new object();
        static readonly List<IFallbackFont?> fbLoaded = new List<IFallbackFont?>();
        static readonly List<bool> fbTried = new List<bool>();
        static readonly ConcurrentDictionary<int, IFallbackFont?> fbByCp = new ConcurrentDictionary<int, IFallbackFont?>();

        // ---------------------------------------------------------------- the built-in extension of the 5x7 font
        /// <summary>Registers aliases and the hand-drawn Cyrillic / Greek / symbol glyphs (called once for <see cref="Default"/>).</summary>
        PixelFont Extend()
        {
            extended = true;
            foreach (var (glyph, pic) in Hand) extras[glyph] = P(pic.Split('/'));
            for (int i = 0; i + 1 < Aliases.Length; i += 2) if (!extras.ContainsKey(Aliases[i])) { var r = Find(Aliases[i + 1]); if (r != null) extras[Aliases[i]] = r; }
            return this;
        }
        // "alias, original" pairs
        const string Aliases =
            "АAВBЕEКKМMНHОOРPСCТTХXІIЈJЅSаaеeоoрpсcуyхxіiјjѕs" +                       // Cyrillic lookalikes
            "ΑAΒBΕEΖZΗHΙIΚKΜMΝNΟOΡPΤTΥYΧXΓГΠПΦФκкοoχxφф" +                             // Greek lookalikes
            "\u00a0 \u00ad-\u2010-\u2011-\u2012-\u2013-\u2014-\u2015-\u2212-\u2032'\u2033\"\u2044/\u2215/\u00d0Đ\u2716×\u2715×\u2219·\u00b5μ";
        static readonly (char, string)[] Hand =
        {
            // Cyrillic capitals
            ('Б', "####./#..../#..../####./#...#/#...#/####."),
            ('Г', "#####/#..../#..../#..../#..../#..../#...."),
            ('Д', ".###./.#.#./.#.#./.#.#./.#.#./#####/#...#"),
            ('Ж', "#.#.#/#.#.#/.###./..#../.###./#.#.#/#.#.#"),
            ('З', ".###./#...#/....#/..##./....#/#...#/.###."),
            ('И', "#...#/#...#/#..##/#.#.#/##..#/#...#/#...#"),
            ('Л', ".####/.#..#/.#..#/.#..#/.#..#/.#..#/#...#"),
            ('П', "#####/#...#/#...#/#...#/#...#/#...#/#...#"),
            ('У', "#...#/#...#/#...#/.####/....#/#...#/.###."),
            ('Ф', "..#../.###./#.#.#/#.#.#/#.#.#/.###./..#.."),
            ('Ц', "#..#./#..#./#..#./#..#./#..#./#####/....#"),
            ('Ч', "#...#/#...#/#...#/.####/....#/....#/....#"),
            ('Ш', "#.#.#/#.#.#/#.#.#/#.#.#/#.#.#/#.#.#/#####"),
            ('Щ', "#.#.#/#.#.#/#.#.#/#.#.#/#.#.#/#####/....#"),
            ('Ъ', "##.../.#.../.#.../.###./.#..#/.#..#/.###."),
            ('Ы', "#...#/#...#/#...#/##..#/#.#.#/#.#.#/##..#"),
            ('Ь', "#..../#..../#..../####./#...#/#...#/####."),
            ('Э', ".###./#...#/....#/..###/....#/#...#/.###."),
            ('Ю', "#..#./#.#.#/#.#.#/###.#/#.#.#/#.#.#/#..#."),
            ('Я', ".####/#...#/#...#/.####/..#.#/.#..#/#...#"),
            ('Є', ".###./#...#/#..../###../#..../#...#/.###."),
            ('Ґ', "....#/#####/#..../#..../#..../#..../#...."),
            ('Ћ', "#####/..#../..##./..#.#/..#.#/..#.#/..#.#"),
            ('Ђ', "#####/..#../..##./..#.#/..#.#/....#/..##."),
            ('Џ', "#...#/#...#/#...#/#...#/#...#/#####/..#.."),
            // Cyrillic lowercase (x-height rows 2..6, б / ф use 6 rows)
            ('б', "...../..###/.#.../####./#...#/#...#/.###."),
            ('в', "...../...../####./#...#/####./#...#/####."),
            ('г', "...../...../#####/#..../#..../#..../#...."),
            ('д', "...../...../.###./.#.#./.#.#./#####/#...#"),
            ('ж', "...../...../#.#.#/#.#.#/.###./#.#.#/#.#.#"),
            ('з', "...../...../####./....#/.###./....#/####."),
            ('и', "...../...../#...#/#..##/#.#.#/##..#/#...#"),
            ('к', "...../...../#..#./#.#../##.../#.#../#..#."),
            ('л', "...../...../.####/.#..#/.#..#/.#..#/#...#"),
            ('м', "...../...../#...#/##.##/#.#.#/#...#/#...#"),
            ('н', "...../...../#...#/#...#/#####/#...#/#...#"),
            ('п', "...../...../#####/#...#/#...#/#...#/#...#"),
            ('т', "...../...../#####/..#../..#../..#../..#.."),
            ('ф', "...../..#../.###./#.#.#/#.#.#/.###./..#.."),
            ('ц', "...../...../#..#./#..#./#..#./#####/....#"),
            ('ч', "...../...../#...#/#...#/.####/....#/....#"),
            ('ш', "...../...../#.#.#/#.#.#/#.#.#/#.#.#/#####"),
            ('щ', "...../...../#.#.#/#.#.#/#.#.#/#####/....#"),
            ('ъ', "...../...../##.../.#.../.###./.#..#/.###."),
            ('ы', "...../...../#...#/#...#/##..#/#.#.#/##..#"),
            ('ь', "...../...../#..../#..../####./#...#/####."),
            ('э', "...../...../.###./....#/..###/....#/.###."),
            ('ю', "...../...../#..#./#.#.#/###.#/#.#.#/#..#."),
            ('я', "...../...../.####/#...#/.####/.#..#/#...#"),
            ('є', "...../...../.###./#..../###../#..../.###."),
            ('ґ', "...../...../....#/#####/#..../#..../#...."),
            ('ћ', "#..../###../#..../####./#...#/#...#/#...#"),
            ('ђ', "#..../###../#..../####./#...#/....#/..##."),
            ('џ', "...../...../#...#/#...#/#...#/#####/..#.."),
            // Greek capitals (the rest are aliases)
            ('Δ', "..#../.#.#./.#.#./#...#/#...#/#...#/#####"),
            ('Θ', ".###./#...#/#...#/#####/#...#/#...#/.###."),
            ('Λ', "..#../.#.#./.#.#./#...#/#...#/#...#/#...#"),
            ('Ξ', "#####/...../...../.###./...../...../#####"),
            ('Σ', "#####/#..../.#.../..#../.#.../#..../#####"),
            ('Ψ', "#.#.#/#.#.#/#.#.#/.###./..#../..#../..#.."),
            ('Ω', ".###./#...#/#...#/#...#/.#.#./.#.#./##.##"),
            // Greek lowercase
            ('α', "...../...../.##.#/#..#./#..#./#..#./.##.#"),
            ('β', ".##../#..#./#.##./#...#/#...#/####./#...."),
            ('γ', "...../...../#...#/#...#/.###./..#../..#.."),
            ('δ', ".###./#..../.#.../.###./#...#/#...#/.###."),
            ('ε', "...../...../.###./#..../.##../#..../.###."),
            ('ζ', "#####/...#./..#../.#.../#..../.###./....#"),
            ('η', "...../...../####./#...#/#...#/#...#/....#"),
            ('θ', "...../.##../#..#./####./#..#./#..#./.##.."),
            ('ι', "...../...../..#../..#../..#../..#.#/...#."),
            ('λ', "#..../.#.../..#../.##../.#.#./#...#/#...#"),
            ('ν', "...../...../#...#/#...#/#...#/.#.#./..#.."),
            ('ξ', ".####/.#.../..##./.#.../#..../.###./...#."),
            ('π', "...../...../#####/.#.#./.#.#./.#.#./.#.##"),
            ('ρ', "...../...../.###./#...#/#...#/####./#...."),
            ('σ', "...../...../.####/#.#../#..#./#..#./.##.."),
            ('ς', "...../...../.####/#..../#..../.###./..##."),
            ('τ', "...../...../#####/..#../..#../..#.#/...#."),
            ('υ', "...../...../#...#/#...#/#...#/#...#/.###."),
            ('ψ', "...../...../#.#.#/#.#.#/#.#.#/.###./..#.."),
            ('ω', "...../...../#...#/#...#/#.#.#/#.#.#/.#.#."),
            ('μ', "...../...../#..#./#..#./#..#./####./#...."),
            // Latin-1 symbols and letters
            ('¡', "..#../...../..#../..#../..#../..#../..#.."),
            ('¿', "..#../...../..#../.#.../#..../#...#/.###."),
            ('«', "...../..#.#/.#.#./#.#../.#.#./..#.#/....."),
            ('»', "...../#.#../.#.#./..#.#/.#.#./#.#../....."),
            ('‹', "...../...#./..#../.#.../..#../...#./....."),
            ('›', "...../.#.../..#../...#./..#../.#.../....."),
            ('§', ".###./#..../.##../#..#./..##./....#/.###."),
            ('¶', ".####/##.#./##.#./.#.#./..#.#/..#.#/..#.#"),
            ('©', ".###./#...#/#.##./#.#../#.##./#...#/.###."),
            ('®', ".###./#...#/###.#/##..#/#.#.#/#...#/.###."),
            ('¢', "..#../.####/#.#../#.#../.####/..#../....."),
            ('£', "..##./.#..#/.#.../###../.#.../.#.../#####"),
            ('¥', "#...#/.#.#./..#../#####/..#../#####/..#.."),
            ('€', "..###/.#.../####./.#.../####./.#.../..###"),
            ('¤', "...../#...#/.###./.#.#./.###./#...#/....."),
            ('¬', "...../...../#####/....#/....#/...../....."),
            ('¦', "..#../..#../..#../...../..#../..#../..#.."),
            ('ª', ".###./....#/.####/#...#/.####/...../....."),
            ('º', ".###./#...#/#...#/#...#/.###./...../....."),
            ('¹', "..#../.##../..#../..#../.###./...../....."),
            ('²', ".##../#..#./..#../.#.../####./...../....."),
            ('³', "###../...#./.##../...#./###../...../....."),
            ('÷', "...../..#../...../#####/...../..#../....."),
            ('ß', ".###./#...#/#.##./#...#/#...#/#.##./#...."),
            ('ð', "##.../.##../.###./..#.#/.###./#...#/.###."),
            ('þ', "#..../#..../####./#...#/#...#/####./#...."),
            ('Đ', "####./#...#/#...#/###.#/#...#/#...#/####."),
            ('Þ', "#..../####./#...#/#...#/####./#..../#...."),
            ('æ', "...../...../.###./...##/.####/#.#../.####"),
            ('Æ', ".####/.#.#./.#.#./####./#.#../#.#../#.###"),
            ('ø', "...../...../.###./#..##/#.#.#/##..#/.###."),
            ('Ø', ".###./#..##/#.#.#/#.#.#/#.#.#/##..#/.###."),
            ('œ', "...../...../.#.#./#.#.#/#.###/#.#../.####"),
            ('Œ', ".####/#.#../#.#../#.###/#.#../#.#../.####"),
            ('ł', ".##../..#../..##./.##../#.#../..#../.###."),
            ('Ł', ".#.../.#..#/.#.#./.##../##.../.#.../.####"),
            ('đ', "....#/..###/....#/.####/#...#/#...#/.####"),
            ('ı', "...../...../.##../..#../..#../..#../.###."),
            // typographic
            ('‘', "..#../.#.../...../...../...../...../....."),
            ('’', ".#.../..#../...../...../...../...../....."),
            ('‚', "...../...../...../...../...../.#.../#...."),
            ('“', ".#.#./#.#../...../...../...../...../....."),
            ('”', ".#.#./..#.#/...../...../...../...../....."),
            ('„', "...../...../...../...../...../.#.#./#.#.."),
            ('•', "...../...../.###./.###./.###./...../....."),
            ('‰', "##..#/##.#./...#./..#../.#.#./#.#.#/#.#.#"),
            ('№', "#..#./##.#./#.##./#..#./...##/..#.#/...##"),
            ('™', "####./.#.../.#.../.#.../...../...../....."),
            ('✓', "...../....#/...#./#.#../.#.../...../....."),
            ('✗', "...../#...#/.#.#./..#../.#.#./#...#/....."),
            // UI shapes
            ('▲', "...../..#../..#../.###./.###./#####/....."),
            ('▼', "...../#####/.###./.###./..#../..#../....."),
            ('►', "#..../##.../###../####./###../##.../#...."),
            ('◄', "....#/...##/..###/.####/..###/...##/....#"),
            ('■', "...../#####/#####/#####/#####/#####/....."),
            ('□', "...../#####/#...#/#...#/#...#/#####/....."),
            ('●', "...../.###./#####/#####/#####/.###./....."),
            ('○', "...../.###./#...#/#...#/#...#/.###./....."),
            ('◆', "..#../.###./#####/.###./..#../...../....."),
            ('★', "..#../..#../#####/.###./.#.#./#...#/....."),
            ('☐', "#####/#...#/#...#/#...#/#...#/#...#/#####"),
            ('☑', "#####/#...#/#..##/##.##/##..#/#...#/#####"),
        };
    }
}
