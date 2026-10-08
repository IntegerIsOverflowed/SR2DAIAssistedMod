using System;
using System.Collections.Generic;
using System.Drawing;

namespace Sr2d64CSport
{
    // Scalable-font text (cs/SpriteFont.cs). DrawString / MeasureString / DrawStringBlock are the real-font
    // counterparts of the 5x7 PixelFont DrawText / MeasureText in Sprite.Text.cs; both stay.
    //
    // Rendering: each glyph is an 8-bit coverage bitmap from the SpriteFont's cache; it is composited with
    // FILL_MASK8 (coverage scales the LineOp like anti-aliasing coverage), so text obeys the lock
    // rectangle, every LineOp and premultiplied targets, and costs one SIMD mask fill per glyph.
    public unsafe partial class Sprite
    {
        [ThreadStatic] private static List<SpriteFont.Placed>? tPlaced;

        /// <summary>
        /// Draws one line of text with a scalable font. (<paramref name="x"/>, <paramref name="y"/>) is the anchor point
        /// (see <paramref name="anchor"/>: TopLeft = top-left of the line box, BottomLeft = the baseline start is
        /// <see cref="SpriteFont.Descent"/> above it, ...). Use <see cref="DrawStringBaseline"/> to position by the baseline.
        /// <paramref name="size"/> is the em height in pixels. Returns the line box actually covered.
        /// </summary>
        public RectangleF DrawString(float x, float y, string text, SpriteFont font, float size, int color, TextAnchor anchor = TextAnchor.TopLeft,
                                     SR2D.LineOp op = SR2D.LineOp.AlphaBlend, int blendFactor = 128)
        {
            if (string.IsNullOrEmpty(text) || font == null || size <= 0) return RectangleF.Empty;
            var placed = tPlaced ??= new List<SpriteFont.Placed>(64); placed.Clear();
            float width = font.Layout(text, size, placed);
            float asc = font.Ascent(size), desc = font.Descent(size), height = asc + desc;
            int ax = (int)anchor % 3, ay = (int)anchor / 3;
            float left = x - width * ax / 2f, top = y - height * ay / 2f;
            DrawPlaced(placed, font, size, left, top + asc, color, op, blendFactor);
            return new RectangleF(left, top, width, height);
        }
        /// <summary>Draws one line with the pen starting at (<paramref name="x"/>, <paramref name="baselineY"/>). Returns the advance in pixels.</summary>
        public float DrawStringBaseline(float x, float baselineY, string text, SpriteFont font, float size, int color, SR2D.LineOp op = SR2D.LineOp.AlphaBlend, int blendFactor = 128)
        {
            if (string.IsNullOrEmpty(text) || font == null || size <= 0) return 0;
            var placed = tPlaced ??= new List<SpriteFont.Placed>(64); placed.Clear();
            float width = font.Layout(text, size, placed);
            DrawPlaced(placed, font, size, x, baselineY, color, op, blendFactor);
            return width;
        }
        /// <summary>
        /// Draws a text block: '\n' separates lines, and when <paramref name="maxWidth"/> is positive lines are word-wrapped
        /// to it. <paramref name="align"/> 0 = left, 1 = centre, 2 = right within the block (the block itself is placed by
        /// <paramref name="anchor"/>). Returns the block rectangle.
        /// </summary>
        public RectangleF DrawStringBlock(float x, float y, string text, SpriteFont font, float size, int color, float maxWidth = 0, int align = 0,
                                          TextAnchor anchor = TextAnchor.TopLeft, float lineSpacing = 1f, SR2D.LineOp op = SR2D.LineOp.AlphaBlend, int blendFactor = 128)
        {
            if (string.IsNullOrEmpty(text) || font == null || size <= 0) return RectangleF.Empty;
            List<string> lines = maxWidth > 0 ? font.Wrap(text, size, maxWidth) : new List<string>(text.Split('\n'));
            float lh = font.LineHeight(size) * lineSpacing, asc = font.Ascent(size);
            float bw = 0; foreach (var l in lines) bw = MathF.Max(bw, font.Measure(l.TrimEnd('\r'), size));
            if (maxWidth > 0) bw = MathF.Max(bw, 0) ; float bh = lines.Count * lh;
            int ax = (int)anchor % 3, ay = (int)anchor / 3;
            float left = x - bw * ax / 2f, top = y - bh * ay / 2f;
            var placed = tPlaced ??= new List<SpriteFont.Placed>(64);
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i].TrimEnd('\r'); if (line.Length == 0) continue;
                placed.Clear(); float w = font.Layout(line, size, placed);
                float lx = left + (bw - w) * align / 2f;
                DrawPlaced(placed, font, size, lx, top + i * lh + asc, color, op, blendFactor);
            }
            return new RectangleF(left, top, bw, bh);
        }
        /// <summary>Width and height (ascent + descent) of one line of text in pixels.</summary>
        public static SizeF MeasureString(string text, SpriteFont font, float size) => new SizeF(font.Measure(text, size), font.Ascent(size) + font.Descent(size));

        /// <summary>
        /// Draws text as filled outlines instead of cached bitmaps - for large sizes (> ~200 px, where bitmaps get big), for
        /// arbitrary transforms (<paramref name="transform"/> maps the pixel-space text, origin at the pen start on the
        /// baseline) or for a gradient <paramref name="paint"/>. Slower per glyph, no cache.
        /// </summary>
        public void DrawStringPath(float x, float baselineY, string text, SpriteFont font, float size, int color, System.Numerics.Matrix3x2? transform = null,
                                   SR2D.LineOp op = SR2D.LineOp.AlphaBlend, bool aa = true, int blendFactor = 128)
        {
            var path = TextPath(text, font, size, x, baselineY, transform);
            if (path != null) FillPath(path, color, op, aa, false, blendFactor);
        }
        public void DrawStringPath(float x, float baselineY, string text, SpriteFont font, float size, VectorGradient paint, System.Numerics.Matrix3x2? transform = null, bool aa = true, float opacity = 1f)
        {
            var path = TextPath(text, font, size, x, baselineY, transform);
            if (path != null) FillPath(path, paint, aa, false, opacity);
        }
        /// <summary>Strokes the glyph outlines (outlined / hollow text).</summary>
        public void DrawStringOutline(float x, float baselineY, string text, SpriteFont font, float size, int color, StrokeStyle style, System.Numerics.Matrix3x2? transform = null,
                                      SR2D.LineOp op = SR2D.LineOp.AlphaBlend, bool aa = true, int blendFactor = 128)
        {
            var path = TextPath(text, font, size, x, baselineY, transform);
            if (path != null) StrokePath(path, color, style, aa, op, blendFactor);
        }
        /// <summary>The outlines of a line of text as one <see cref="VectorPath"/> in pixels (origin = pen start on the baseline), or null when empty.</summary>
        public static VectorPath? TextPath(string text, SpriteFont font, float size, float x = 0, float baselineY = 0, System.Numerics.Matrix3x2? transform = null)
        {
            if (string.IsNullOrEmpty(text) || font == null || size <= 0) return null;
            var placed = new List<SpriteFont.Placed>(text.Length); font.Layout(text, size, placed);
            var all = new VectorPath();
            var t = transform ?? System.Numerics.Matrix3x2.Identity;
            foreach (var p in placed)
            {
                var g = font.Outline(p.Gid, size); if (g == null) continue;
                all.Append(g, System.Numerics.Matrix3x2.CreateTranslation(p.X, 0) * t * System.Numerics.Matrix3x2.CreateTranslation(x, baselineY));
            }
            return all.IsEmpty ? null : all;
        }

        // ------------------------------------------------------------------ implementation
        internal void DrawPlaced(List<SpriteFont.Placed> placed, SpriteFont font, float size, float penX, float baseY, int color, SR2D.LineOp op, int k)
        {
            if (meRight <= meLeft || meBottom <= meTop) return;
            int by = (int)MathF.Round(baseY);
            int nsub = Math.Max(1, font.SubPixelPositions);
            if (nsub <= 1) penX = MathF.Round(penX);                        // whole-pixel mode: the pen starts on a pixel, glyphs round to the nearest one
            int opw = OpWord(op);
            foreach (var p in placed)
            {
                float gx = penX + p.X;
                int ix, phase;
                if (nsub <= 1) { ix = (int)MathF.Round(gx); phase = 0; }
                else { ix = (int)MathF.Floor(gx); phase = Math.Min(nsub - 1, (int)((gx - ix) * nsub)); }
                var g = font.GetGlyph(p.Gid, size, phase); if (g == null || g.Empty) continue;
                int dl = ix + g.Left, dt = by - g.Top;                       // destination top-left of the bitmap
                int cl = Math.Max(dl, meLeft), ct = Math.Max(dt, meTop), cr = Math.Min(dl + g.W, meRight), cb = Math.Min(dt + g.H, meBottom);
                if (cl >= cr || ct >= cb) continue;
                fixed (byte* cov = g.Cov)
                    _ = SR2D.Native.FillMask8(pBuf, meWidth, cl, ct, cr, cb, cov - ((long)dt * g.W + dl), g.W, color, opw, k);   // mask base shifted so mask[y*W+x] is glyph pixel (x-dl, y-dt)
            }
        }
    }

    // SpriteFont as the pixel font's fallback (PixelFont.FallbackFonts / FallbackFamilies): registered automatically when this
    // file is compiled in, so Sprite.DrawText draws CJK / emoji / anything the 5x7 font lacks with an installed vector font.
    public sealed partial class SpriteFont
    {
        float IFallbackFont.MeasureFallback(ReadOnlySpan<char> text, float em) => Measure(text, em);
        void IFallbackFont.DrawFallback(Sprite target, float x, float baselineY, ReadOnlySpan<char> text, float em, int color, SR2D.LineOp op, int blendFactor)
        {
            var l = fbPlaced ??= new List<Placed>(4); l.Clear();
            Layout(text, em, l);
            target.DrawPlaced(l, this, em, x, baselineY, color, op, blendFactor);
        }
        [ThreadStatic] static List<Placed>? fbPlaced;
    }
    // CA2255: the cs/ files are compiled into the application itself (not into a shared library), which is exactly the
    // case the rule allows; the initializer only wires the font loader into PixelFont when SpriteFont.cs is part of the build.
#pragma warning disable CA2255
    internal static class SpriteFontFallbackRegistration
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Register() { PixelFont.FallbackLoader ??= family => SpriteFont.Installed(family); }
    }
#pragma warning restore CA2255
}
