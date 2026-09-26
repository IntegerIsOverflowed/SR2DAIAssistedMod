using System;
using System.Drawing;

namespace Sr2d64CSport
{
    /// <summary>Where the (x, y) of <see cref="Sprite.DrawText(int,int,string,int,int,int,int,int,bool,TextAnchor,PixelFont)"/> sits on the text box.</summary>
    internal enum TextAnchor
    {
        TopLeft, TopCenter, TopRight,
        MiddleLeft, Center, MiddleRight,
        BottomLeft, BottomCenter, BottomRight,
    }

    // Bitmap text drawn with SR2D's own fills (ClearRect for opaque text, FillRect + LineOp for blended /
    // XOR text). No GDI, no allocation per call; a label of 30 characters costs a few microseconds at
    // scale 1. Part of the Sprite partial class so it clips through the lock rect like every other primitive.
    internal unsafe partial class Sprite
    {
        /// <summary>
        /// Draws <paramref name="text"/> with a pixel font. Every glyph pixel becomes a <paramref name="scale"/> x <paramref name="scale"/>
        /// block, optionally "emboldened" by <paramref name="weight"/> extra pixels to the right (weight 1 = bold at scale 1; it
        /// also grows the advance so glyphs do not touch). <paramref name="bg"/> != 0 first fills the padded text box
        /// (padding = 2 * scale) with that colour. <paramref name="letterSpacing"/> is added between glyphs (pixels, may be
        /// negative). '\n' starts a new line (line height = (glyph height + 1) * scale + letterSpacing / 2).
        /// Returns the box that was drawn (before clipping), useful to stack labels.
        /// </summary>
        /// <param name="x">Anchor X (see <paramref name="anchor"/>).</param>
        /// <param name="y">Anchor Y.</param>
        /// <param name="text">Text (ASCII; other characters draw the font's fallback glyph).</param>
        /// <param name="color">Text colour (ARGB; the alpha byte is used by <see cref="SR2D.LineOp.AlphaBlend"/>).</param>
        /// <param name="bg">Background colour or 0 for none.</param>
        /// <param name="scale">Pixel size of a glyph pixel (1 = 5x7 px glyphs, 2 = 10x14 ...).</param>
        /// <param name="weight">Extra stroke width in pixels (0 = regular, 1 = bold, ...).</param>
        /// <param name="letterSpacing">Extra pixels between glyphs (default 0 = one font column * scale gap).</param>
        /// <param name="op">How the text pixels are combined (Set = fastest; Xor, AlphaBlend, Blend, Add ...).</param>
        /// <param name="blendFactor">Crossfade factor for <see cref="SR2D.LineOp.Blend"/>.</param>
        /// <param name="anchor">Which point of the text box lands on (x, y).</param>
        /// <param name="font">null = <see cref="PixelFont.Default"/>.</param>
        public Rectangle DrawText(int x, int y, string text, int color, int bg = 0, int scale = 1, int weight = 0, int letterSpacing = 0,
                                  SR2D.LineOp op = SR2D.LineOp.Set, int blendFactor = 128, TextAnchor anchor = TextAnchor.TopLeft, PixelFont? font = null)
        {
            if (string.IsNullOrEmpty(text)) return Rectangle.Empty;
            font ??= PixelFont.Default;
            if (scale < 1) scale = 1;
            if (weight < 0) weight = 0;
            var size = MeasureText(text, scale, weight, letterSpacing, font);
            int pad = bg != 0 ? 2 * scale : 0;
            // anchor -> top-left of the (padded) box
            int bw = size.Width + 2 * pad, bh = size.Height + 2 * pad;
            int ax = (int)anchor % 3, ay = (int)anchor / 3;
            int bx = x - (ax == 1 ? bw / 2 : ax == 2 ? bw : 0), by = y - (ay == 1 ? bh / 2 : ay == 2 ? bh : 0);
            var box = new Rectangle(bx, by, bw, bh);
            if (bg != 0) Fill(bx, by, bw, bh, bg, op, blendFactor);
            int gw = font.GlyphWidth, gh = font.GlyphHeight;
            int advance = (gw + 1) * scale + weight + letterSpacing;
            int lineH = (gh + 1) * scale + letterSpacing / 2;
            int cx = bx + pad, cy = by + pad;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\n') { cx = bx + pad; cy += lineH; continue; }
                if (ch == '\r') continue;
                if (!font.Has(ch))
                {   // a character the pixel font lacks: the fallback vector font (or the box when there is none)
                    int len = char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                    var fb = FallbackFontFor(text, i, len);
                    if (fb != null)
                    {
                        float em = PixelFont.FallbackEm * gh * scale;
                        var run = text.AsSpan(i, len);
                        int w = (int)MathF.Ceiling(fb.MeasureFallback(run, em));
                        for (int k = 0; k <= weight; k++) fb.DrawFallback(this, cx + k, cy + gh * scale, run, em, color, op, blendFactor);
                        cx += w + weight + scale + letterSpacing;
                        i += len - 1;
                        continue;
                    }
                    if (len == 2) i++;   // the box once per code point, not per surrogate
                }
                var rows = font.Rows(ch);
                for (int r = 0; r < gh; r++)
                {
                    int bits = rows[r];
                    if (bits == 0) continue;
                    // runs of set bits -> one rectangle each; weight widens every run to the right
                    for (int c = 0; c < gw;)
                    {
                        if ((bits & (1 << (gw - 1 - c))) == 0) { c++; continue; }
                        int c0 = c; while (c < gw && (bits & (1 << (gw - 1 - c))) != 0) c++;
                        Fill(cx + c0 * scale, cy + r * scale, (c - c0) * scale + weight, scale, color, op, blendFactor);
                    }
                }
                cx += advance;
            }
            return box;
        }

        /// <summary>The fallback font for the character (or surrogate pair) at <paramref name="i"/>, null = draw the box.</summary>
        static IFallbackFont? FallbackFontFor(string text, int i, int len)
        {
            if (!PixelFont.UseFallbackFont) return null;
            int cp = len == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            return cp < 0x20 || cp == 0x7F ? null : PixelFont.FallbackFor(cp);   // control characters keep the box
        }

        /// <summary>
        /// Pen x offsets of every character of one <see cref="DrawText(int,int,string,int,int,int,int,int,SR2D.LineOp,int,TextAnchor,PixelFont)"/> line:
        /// <paramref name="pens"/>[i] = pen x before character i, <paramref name="pens"/>[text.Length] = pen x after the last one (its trailing gap included).
        /// Characters the pixel font lacks are measured with the fallback font, exactly as they are drawn; a low surrogate gets the pen of its pair.
        /// This is what caret placement and hit-testing use - never assume every glyph advances by the same amount.
        /// </summary>
        public static void TextPens(ReadOnlySpan<char> text, Span<int> pens, int scale = 1, int weight = 0, int letterSpacing = 0, PixelFont? font = null)
        {
            font ??= PixelFont.Default;
            if (scale < 1) scale = 1;
            if (weight < 0) weight = 0;
            int gw = font.GlyphWidth, gh = font.GlyphHeight;
            int advance = (gw + 1) * scale + weight + letterSpacing;
            int x = 0;
            for (int i = 0; i < text.Length; i++)
            {
                pens[i] = x;
                char ch = text[i];
                if (ch == '\r' || ch == '\n') continue;
                if (!font.Has(ch))
                {
                    int len = char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                    int cp = len == 2 ? char.ConvertToUtf32(ch, text[i + 1]) : ch;
                    var fb = PixelFont.UseFallbackFont && cp >= 0x20 && cp != 0x7F ? PixelFont.FallbackFor(cp) : null;
                    if (fb != null)
                    {
                        x += (int)MathF.Ceiling(fb.MeasureFallback(text.Slice(i, len), PixelFont.FallbackEm * gh * scale)) + weight + scale + letterSpacing;
                        if (len == 2) pens[++i] = x;
                        continue;
                    }
                    if (len == 2) { pens[++i] = x; }
                }
                x += advance;
            }
            pens[text.Length] = x;
        }

        /// <summary>Convenience overload: text with a background box, anchored, regular weight.</summary>
        public Rectangle DrawText(int x, int y, string text, int color, int bg, TextAnchor anchor, int scale = 1)
            => DrawText(x, y, text, color, bg, scale, 0, 0, SR2D.LineOp.Set, 128, anchor, null);

        /// <summary>Size in pixels of the text as <see cref="DrawText(int,int,string,int,int,int,int,int,SR2D.LineOp,int,TextAnchor,PixelFont)"/> will draw it (without the background padding).</summary>
        public static Size MeasureText(string text, int scale = 1, int weight = 0, int letterSpacing = 0, PixelFont? font = null)
        {
            font ??= PixelFont.Default;
            if (scale < 1) scale = 1;
            if (weight < 0) weight = 0;
            int lineH = (font.GlyphHeight + 1) * scale + letterSpacing / 2;
            int lines = 1, maxW = 0, start = 0;
            Span<int> stackPens = stackalloc int[128];
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;
                var line = text.AsSpan(start, i - start);
                if (line.Length > 0)
                {
                    var pens = line.Length < stackPens.Length ? stackPens : new int[line.Length + 1];
                    TextPens(line, pens, scale, weight, letterSpacing, font);
                    int w = pens[line.Length] - scale - letterSpacing;      // the last glyph: its own width (+ weight), not the trailing gap
                    if (w > maxW) maxW = w;
                }
                if (i < text.Length) { lines++; start = i + 1; }
            }
            if (maxW <= 0) return Size.Empty;
            int h = (lines - 1) * lineH + font.GlyphHeight * scale;
            return new Size(maxW, h);
        }

        // opaque Set -> ClearRect (memset-class), everything else through the polygon filler with the op
        void Fill(int x, int y, int w, int h, int c, SR2D.LineOp op, int k)
        {
            if (w <= 0 || h <= 0) return;
            if (op == SR2D.LineOp.Set) { ClearRect(x, x + w, y, y + h, c); return; }
            FillRect(x, y, w, h, c, op, false, k);
        }
    }
}
