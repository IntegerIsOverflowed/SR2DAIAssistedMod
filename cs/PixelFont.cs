using System;
using System.Collections.Generic;

namespace Sr2d64CSport
{
    /// <summary>
    /// A bitmap font for <see cref="Sprite.DrawText(int,int,string,int,int,int,int,int,bool,TextAnchor,PixelFont)"/>: fixed-cell
    /// glyphs of <see cref="GlyphWidth"/> x <see cref="GlyphHeight"/> bits. <see cref="Default"/> is a built-in 5x7 font
    /// covering printable ASCII 32..126, Latin-1 and Latin Extended-A (accents composed), Cyrillic, Greek and common symbols
    /// (see PixelFont.Unicode.cs; <see cref="Add"/> more). Characters it still lacks draw with <see cref="FallbackFont"/> when one is installed,
    /// otherwise as a box. You can supply your own font with the constructor - one string per glyph, '#' = set pixel, row after row -
    /// e.g. a 3x5 digit font or an 8x8 retro font. Glyph rows are stored as bit masks (bit GlyphWidth-1 = left column), so a glyph is at most 16 pixels wide.
    /// </summary>
    internal sealed partial class PixelFont
    {
        public int GlyphWidth { get; }
        public int GlyphHeight { get; }
        public int FirstChar { get; }
        readonly ushort[][] rows;   // [glyph][row]
        readonly ushort[] fallback;
        readonly Dictionary<char, ushort[]> extras = new Dictionary<char, ushort[]>();

        /// <summary>Row bit masks of a character (the fallback glyph when it is outside the font).</summary>
        public ushort[] Rows(char ch) => Find(ch) ?? fallback;

        /// <summary>Adds (or replaces) a glyph for any character outside the consecutive range, same picture format as the constructor.</summary>
        public PixelFont Add(char ch, string picture)
        {
            if (picture.Length != GlyphWidth * GlyphHeight) throw new ArgumentException($"expected {GlyphWidth * GlyphHeight} characters");
            var r = new ushort[GlyphHeight];
            for (int y = 0; y < GlyphHeight; y++) { int v = 0; for (int x = 0; x < GlyphWidth; x++) v = (v << 1) | (picture[y * GlyphWidth + x] != ' ' ? 1 : 0); r[y] = (ushort)v; }
            extras[ch] = r;
            return this;
        }
        /// <summary>Number of glyphs (consecutive from <see cref="FirstChar"/>).</summary>
        public int Count => rows.Length;

        /// <summary>
        /// Builds a font from glyph pictures: each string holds <paramref name="glyphWidth"/> * <paramref name="glyphHeight"/> characters
        /// ('#' or any non-space = set), rows top to bottom. Glyph i is character <paramref name="firstChar"/> + i.
        /// <paramref name="fallbackIndex"/> = glyph used for characters outside the font (-1 = the last one).
        /// </summary>
        public PixelFont(int glyphWidth, int glyphHeight, IReadOnlyList<string> glyphs, int firstChar = 32, int fallbackIndex = -1)
        {
            if (glyphWidth < 1 || glyphWidth > 16 || glyphHeight < 1) throw new ArgumentOutOfRangeException(nameof(glyphWidth), "1..16 columns");
            GlyphWidth = glyphWidth; GlyphHeight = glyphHeight; FirstChar = firstChar;
            rows = new ushort[glyphs.Count][];
            for (int g = 0; g < glyphs.Count; g++)
            {
                string s = glyphs[g];
                if (s.Length != glyphWidth * glyphHeight) throw new ArgumentException($"glyph {g}: expected {glyphWidth * glyphHeight} characters, got {s.Length}");
                var r = new ushort[glyphHeight];
                for (int y = 0; y < glyphHeight; y++)
                {
                    int v = 0;
                    for (int x = 0; x < glyphWidth; x++) v = (v << 1) | (s[y * glyphWidth + x] != ' ' ? 1 : 0);
                    r[y] = (ushort)v;
                }
                rows[g] = r;
            }
            fallback = rows.Length == 0 ? new ushort[glyphHeight] : rows[fallbackIndex < 0 || fallbackIndex >= rows.Length ? rows.Length - 1 : fallbackIndex];
        }

        /// <summary>The built-in 5x7 font (ASCII 32..126, the DEL slot is the "unknown character" box).</summary>
        public static PixelFont Default => _default ??= new PixelFont(5, 7, Ascii5x7, 32, 95)
            .Add('\u00b0', " ##  " + "#  # " + " ##  " + "     " + "     " + "     " + "     ")   // degree
            .Add('\u00b1', "  #  " + "  #  " + "#####" + "  #  " + "  #  " + "     " + "#####")   // plus-minus
            .Add('\u00d7', "     " + "#   #" + " # # " + "  #  " + " # # " + "#   #" + "     ")   // multiplication
            .Add('\u00b7', "     " + "     " + "     " + "  #  " + "     " + "     " + "     ")   // middle dot
            .Add('\u00b5', "     " + "     " + "#  # " + "#  # " + "#  # " + "#### " + "#    ")   // micro
            .Add('\u2192', "     " + "  #  " + "   # " + "#####" + "   # " + "  #  " + "     ")   // right arrow
            .Add('\u2190', "     " + "  #  " + " #   " + "#####" + " #   " + "  #  " + "     ")   // left arrow
            .Add('\u2191', "  #  " + " ### " + "# # #" + "  #  " + "  #  " + "  #  " + "  #  ")   // up arrow
            .Add('\u2193', "  #  " + "  #  " + "  #  " + "  #  " + "# # #" + " ### " + "  #  ")   // down arrow
            .Add('\u2026', "     " + "     " + "     " + "     " + "     " + "     " + "# # #")    // ellipsis
            .Extend();
        static PixelFont? _default;

        static readonly string[] Ascii5x7 =
        {
                "     " + "     " + "     " + "     " + "     " + "     " + "     ", // space
                "  #  " + "  #  " + "  #  " + "  #  " + "  #  " + "     " + "  #  ", // !
                " # # " + " # # " + "     " + "     " + "     " + "     " + "     ", // "
                " # # " + " # # " + "#####" + " # # " + "#####" + " # # " + " # # ", // #
                "  #  " + " ####" + "# #  " + " ### " + "  # #" + "#### " + "  #  ", // $
                "##   " + "##  #" + "   # " + "  #  " + " #   " + "#  ##" + "   ##", // %
                " ##  " + "#  # " + " ##  " + " ##  " + "#  # " + "#  # " + " ## #", // &
                "  #  " + "  #  " + "     " + "     " + "     " + "     " + "     ", // '
                "   # " + "  #  " + " #   " + " #   " + " #   " + "  #  " + "   # ", // (
                " #   " + "  #  " + "   # " + "   # " + "   # " + "  #  " + " #   ", // )
                "     " + "  #  " + "# # #" + " ### " + "# # #" + "  #  " + "     ", // *
                "     " + "  #  " + "  #  " + "#####" + "  #  " + "  #  " + "     ", // +
                "     " + "     " + "     " + "     " + "  #  " + "  #  " + " #   ", // ,
                "     " + "     " + "     " + "#####" + "     " + "     " + "     ", // -
                "     " + "     " + "     " + "     " + "     " + "  #  " + "  #  ", // .
                "     " + "    #" + "   # " + "  #  " + " #   " + "#    " + "     ", // /
                " ### " + "#   #" + "#  ##" + "# # #" + "##  #" + "#   #" + " ### ", // 0
                "  #  " + " ##  " + "  #  " + "  #  " + "  #  " + "  #  " + " ### ", // 1
                " ### " + "#   #" + "    #" + "   # " + "  #  " + " #   " + "#####", // 2
                "#####" + "   # " + "  #  " + "   # " + "    #" + "#   #" + " ### ", // 3
                "   # " + "  ## " + " # # " + "#  # " + "#####" + "   # " + "   # ", // 4
                "#####" + "#    " + "#### " + "    #" + "    #" + "#   #" + " ### ", // 5
                "  ## " + " #   " + "#    " + "#### " + "#   #" + "#   #" + " ### ", // 6
                "#####" + "    #" + "   # " + "  #  " + " #   " + " #   " + " #   ", // 7
                " ### " + "#   #" + "#   #" + " ### " + "#   #" + "#   #" + " ### ", // 8
                " ### " + "#   #" + "#   #" + " ####" + "    #" + "   # " + " ##  ", // 9
                "     " + "  #  " + "  #  " + "     " + "  #  " + "  #  " + "     ", // :
                "     " + "  #  " + "  #  " + "     " + "  #  " + "  #  " + " #   ", // ;
                "   # " + "  #  " + " #   " + "#    " + " #   " + "  #  " + "   # ", // <
                "     " + "     " + "#####" + "     " + "#####" + "     " + "     ", // =
                " #   " + "  #  " + "   # " + "    #" + "   # " + "  #  " + " #   ", // >
                " ### " + "#   #" + "    #" + "   # " + "  #  " + "     " + "  #  ", // ?
                " ### " + "#   #" + "# ###" + "# # #" + "# ###" + "#    " + " ### ", // @
                " ### " + "#   #" + "#   #" + "#####" + "#   #" + "#   #" + "#   #", // A
                "#### " + "#   #" + "#   #" + "#### " + "#   #" + "#   #" + "#### ", // B
                " ### " + "#   #" + "#    " + "#    " + "#    " + "#   #" + " ### ", // C
                "#### " + "#   #" + "#   #" + "#   #" + "#   #" + "#   #" + "#### ", // D
                "#####" + "#    " + "#    " + "#### " + "#    " + "#    " + "#####", // E
                "#####" + "#    " + "#    " + "#### " + "#    " + "#    " + "#    ", // F
                " ### " + "#   #" + "#    " + "# ###" + "#   #" + "#   #" + " ####", // G
                "#   #" + "#   #" + "#   #" + "#####" + "#   #" + "#   #" + "#   #", // H
                " ### " + "  #  " + "  #  " + "  #  " + "  #  " + "  #  " + " ### ", // I
                "  ###" + "   # " + "   # " + "   # " + "   # " + "#  # " + " ##  ", // J
                "#   #" + "#  # " + "# #  " + "##   " + "# #  " + "#  # " + "#   #", // K
                "#    " + "#    " + "#    " + "#    " + "#    " + "#    " + "#####", // L
                "#   #" + "## ##" + "# # #" + "# # #" + "#   #" + "#   #" + "#   #", // M
                "#   #" + "#   #" + "##  #" + "# # #" + "#  ##" + "#   #" + "#   #", // N
                " ### " + "#   #" + "#   #" + "#   #" + "#   #" + "#   #" + " ### ", // O
                "#### " + "#   #" + "#   #" + "#### " + "#    " + "#    " + "#    ", // P
                " ### " + "#   #" + "#   #" + "#   #" + "# # #" + "#  # " + " ## #", // Q
                "#### " + "#   #" + "#   #" + "#### " + "# #  " + "#  # " + "#   #", // R
                " ####" + "#    " + "#    " + " ### " + "    #" + "    #" + "#### ", // S
                "#####" + "  #  " + "  #  " + "  #  " + "  #  " + "  #  " + "  #  ", // T
                "#   #" + "#   #" + "#   #" + "#   #" + "#   #" + "#   #" + " ### ", // U
                "#   #" + "#   #" + "#   #" + "#   #" + "#   #" + " # # " + "  #  ", // V
                "#   #" + "#   #" + "#   #" + "# # #" + "# # #" + "# # #" + " # # ", // W
                "#   #" + "#   #" + " # # " + "  #  " + " # # " + "#   #" + "#   #", // X
                "#   #" + "#   #" + " # # " + "  #  " + "  #  " + "  #  " + "  #  ", // Y
                "#####" + "    #" + "   # " + "  #  " + " #   " + "#    " + "#####", // Z
                " ### " + " #   " + " #   " + " #   " + " #   " + " #   " + " ### ", // [
                "     " + "#    " + " #   " + "  #  " + "   # " + "    #" + "     ", // backslash
                " ### " + "   # " + "   # " + "   # " + "   # " + "   # " + " ### ", // ]
                "  #  " + " # # " + "#   #" + "     " + "     " + "     " + "     ", // ^
                "     " + "     " + "     " + "     " + "     " + "     " + "#####", // _
                " #   " + "  #  " + "     " + "     " + "     " + "     " + "     ", // `
                "     " + "     " + " ### " + "    #" + " ####" + "#   #" + " ####", // a
                "#    " + "#    " + "# ## " + "##  #" + "#   #" + "#   #" + "#### ", // b
                "     " + "     " + " ### " + "#    " + "#    " + "#   #" + " ### ", // c
                "    #" + "    #" + " ## #" + "#  ##" + "#   #" + "#   #" + " ####", // d
                "     " + "     " + " ### " + "#   #" + "#####" + "#    " + " ### ", // e
                "  ## " + " #  #" + " #   " + "###  " + " #   " + " #   " + " #   ", // f
                "     " + "     " + " ####" + "#   #" + " ####" + "    #" + " ### ", // g
                "#    " + "#    " + "# ## " + "##  #" + "#   #" + "#   #" + "#   #", // h
                "  #  " + "     " + " ##  " + "  #  " + "  #  " + "  #  " + " ### ", // i
                "   # " + "     " + "  ## " + "   # " + "   # " + "#  # " + " ##  ", // j
                "#    " + "#    " + "#  # " + "# #  " + "##   " + "# #  " + "#  # ", // k
                " ##  " + "  #  " + "  #  " + "  #  " + "  #  " + "  #  " + " ### ", // l
                "     " + "     " + "## # " + "# # #" + "# # #" + "#   #" + "#   #", // m
                "     " + "     " + "# ## " + "##  #" + "#   #" + "#   #" + "#   #", // n
                "     " + "     " + " ### " + "#   #" + "#   #" + "#   #" + " ### ", // o
                "     " + "     " + "#### " + "#   #" + "#### " + "#    " + "#    ", // p
                "     " + "     " + " ####" + "#   #" + " ####" + "    #" + "    #", // q
                "     " + "     " + "# ## " + "##  #" + "#    " + "#    " + "#    ", // r
                "     " + "     " + " ####" + "#    " + " ### " + "    #" + "#### ", // s
                " #   " + " #   " + "###  " + " #   " + " #   " + " #  #" + "  ## ", // t
                "     " + "     " + "#   #" + "#   #" + "#   #" + "#  ##" + " ## #", // u
                "     " + "     " + "#   #" + "#   #" + "#   #" + " # # " + "  #  ", // v
                "     " + "     " + "#   #" + "#   #" + "# # #" + "# # #" + " # # ", // w
                "     " + "     " + "#   #" + " # # " + "  #  " + " # # " + "#   #", // x
                "     " + "     " + "#   #" + "#   #" + " ####" + "    #" + " ### ", // y
                "     " + "     " + "#####" + "   # " + "  #  " + " #   " + "#####", // z
                "   # " + "  #  " + "  #  " + " #   " + "  #  " + "  #  " + "   # ", // {
                "  #  " + "  #  " + "  #  " + "  #  " + "  #  " + "  #  " + "  #  ", // |
                " #   " + "  #  " + "  #  " + "   # " + "  #  " + "  #  " + " #   ", // }
                "     " + "     " + " #   " + "# # #" + "   # " + "     " + "     ", // ~
                "#####" + "#   #" + "#   #" + "#   #" + "#   #" + "#   #" + "#####", // DEL -> box (unknown)
        };
    }
}
