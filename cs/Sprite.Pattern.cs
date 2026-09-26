using System;
using System.Drawing;

namespace Sr2d64CSport
{
    partial class Sprite
    {
        /// <summary>
        /// Image-brush fill: fills <paramref name="Path"/> with <paramref name="Pattern"/> (an image brush, like
        /// SVG &lt;pattern&gt; / a PDF pattern). The pattern's (0, 0) sits at (OriginX, OriginY) in destination
        /// pixels; <paramref name="Scale"/> and <paramref name="Angle"/> (degrees, clockwise) place it. With
        /// <paramref name="Tile"/> the pattern repeats to cover the path, otherwise the path is filled only where
        /// the (single, possibly rotated) pattern image lands. Anti-aliased path coverage multiplies the pattern's
        /// alpha, so edges stay smooth. Managed per-pixel loop over the path's bounding box - meant for editor-scale
        /// paths (vector import, drawing tools), not per-frame full-screen fills.
        /// </summary>
        /// <param name="Path">The polygon / polyline to fill (closed automatically like DrawPolyline closed).</param>
        /// <param name="Pattern">The image to paint with. Its own alpha is respected.</param>
        /// <param name="OriginX/Y">Where the pattern's top-left corner sits in destination pixels (the transform's anchor for scale / angle).</param>
        public void FillPattern(ReadOnlySpan<PointF> Path, Sprite Pattern, float OriginX = 0, float OriginY = 0,
                                float Scale = 1, float Angle = 0, bool Tile = true,
                                SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128, bool AA = true)
        {
            ArgumentNullException.ThrowIfNull(Pattern);
            if (Path.Length < 3 || Pattern.Width == 0 || Pattern.Height == 0) return;
            var r = BoundsOf(Path);
            r.Intersect(new Rectangle(0, 0, meWidth, meHeight));
            if (r.IsEmpty) return;

            // coverage: fill the path once into a scratch, AA gives us the soft edge in its alpha
            using var cov = new Sprite(r.Width, r.Height);
            var local = new PointF[Path.Length];
            for (int i = 0; i < Path.Length; i++) local[i] = new PointF(Path[i].X - r.X, Path[i].Y - r.Y);
            var poly = new Sprite.PathBuilder().MoveTo(local[0]);
            for (int i = 1; i < local.Length; i++) poly.LineTo(local[i]);     // FillPath auto-closes the sub-path
            cov.FillPath(poly, unchecked((int)0xFFFFFFFF), SR2D.LineOp.Set, AA);
            var cb = cov.Pixels;

            float cos = (float)Math.Cos(-Angle * Math.PI / 180.0), sin = (float)Math.Sin(-Angle * Math.PI / 180.0);
            float invScale = Scale != 0 ? 1f / Scale : 1f;
            int pw = Pattern.Width, ph = Pattern.Height;
            var pb = Pattern.Pixels;
            var db = Pixels;
            float bf = Math.Clamp(BlendFactor, 0, 256) / 256f;

            unchecked
            {
            for (int y = 0; y < r.Height; y++)
            {
                for (int x = 0; x < r.Width; x++)
                {
                    int c = cb[y * r.Width + x];
                    int covA = (c >>> 24) & 255;                       // path coverage (AA)
                    if (covA == 0) continue;
                    float px = x + r.X - OriginX, py = y + r.Y - OriginY;
                    float ux = (px * cos - py * sin) * invScale, uy = (px * sin + py * cos) * invScale;
                    int sx, sy;
                    if (Tile) { sx = Mod((int)Math.Floor(ux), pw); sy = Mod((int)Math.Floor(uy), ph); }
                    else { sx = (int)Math.Floor(ux); sy = (int)Math.Floor(uy); if ((uint)sx >= (uint)pw || (uint)sy >= (uint)ph) continue; }
                    int sp = pb[sy * pw + sx];
                    int a = ((sp >>> 24) & 255) * covA / 255;
                    if (a == 0) continue;
                    int src = (a << 24) | (sp & 0x00ffffff);
                    int di = (y + r.Y) * meWidth + (x + r.X);
                    db[di] = MixOp(db[di], src, Op, bf);
                }
            }
            }
        }

        Rectangle BoundsOf(ReadOnlySpan<PointF> pts)
        {
            float l = pts[0].X, t = pts[0].Y, rr = pts[0].X, b = pts[0].Y;
            for (int i = 1; i < pts.Length; i++) { l = MathF.Min(l, pts[i].X); t = MathF.Min(t, pts[i].Y); rr = MathF.Max(rr, pts[i].X); b = MathF.Max(b, pts[i].Y); }
            return Rectangle.FromLTRB((int)MathF.Floor(l), (int)MathF.Floor(t), Math.Min(meWidth, (int)MathF.Ceiling(rr) + 1), Math.Min(meHeight, (int)MathF.Ceiling(b) + 1));
        }
        static int Mod(int v, int m) { int r = v % m; return r < 0 ? r + m : r; }
        /// <summary>One-pixel compositing of the ops a pattern fill promises (the rest falls back to AlphaBlend).</summary>
        int MixOp(int dst, int src, SR2D.LineOp op, float bf)
        {
            int sa = (src >>> 24) & 255;
            if (op == SR2D.LineOp.Set) return src;
            float f = sa / 255f;
            int dr = (dst >>> 16) & 255, dg = (dst >>> 8) & 255, dbv = dst & 255;
            int sr = (src >>> 16) & 255, sg = (src >>> 8) & 255, sb = src & 255;
            if (op == SR2D.LineOp.Blend)
            {
                float t = f * bf;
                return (unchecked((int)0xFF000000))
                     | ((int)Math.Round(sr * t + dr * (1 - t)) << 16)
                     | ((int)Math.Round(sg * t + dg * (1 - t)) << 8)
                     | (int)Math.Round(sb * t + dbv * (1 - t));
            }
            // AlphaBlend (OVER) + AlphaOver; anything else degrades to OVER
            int ar = (int)Math.Round(sr * f + dr * (1 - f)), ag = (int)Math.Round(sg * f + dg * (1 - f)), ab = (int)Math.Round(sb * f + dbv * (1 - f));
            int aa = Math.Max(sa, 255 - (int)Math.Round((255 - sa) * f));   // approximated destination alpha
            return (aa << 24) | (ar << 16) | (ag << 8) | ab;
        }
    }
}
