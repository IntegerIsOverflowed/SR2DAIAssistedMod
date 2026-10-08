using System;
using System.Drawing;

namespace Sr2d64CSport
{
    // Sprite part: four-corner warping (inverse-bilinear quad map). DrawQuadWarp takes the part of a source
    // sprite framed by one quadrilateral and re-projects it so the corners land on another quadrilateral of
    // this sprite. The map is the bilinear patch parametrized by the two quads, sampled by per-pixel
    // inversion (Heckbert's inverse bilinear), nearest-neighbour. The property that makes it the right
    // primitive for a cage editor: each quad edge is mapped LINEARLY between its (moved) corners, so two
    // quads that share two corners share the deformed edge too - moving a shared vertex drags every adjacent
    // quad with no crack along the seam. That is what apps/SpriteBox builds on.
    partial class Sprite
    {
        /// <summary>
        /// Four-corner warp: the part of <paramref name="Source"/> framed by <paramref name="SourceQuad"/> is
        /// drawn onto this sprite, re-projected so the source corners land exactly on <paramref name="DestQuad"/>.
        /// Both quads are four corners in CLOCKWISE order (top-left, top-right, bottom-right, bottom-left), in the
        /// sprites' own pixel coordinates where (0.5, 0.5) is the centre of pixel (0, 0). Sampling is
        /// inverse-bilinear with nearest-neighbour fetch - the same bilinear parametrization the mesh warp uses,
        /// so an IDENTITY quad (dest == source) reproduces the region byte for byte, and quads sharing corners
        /// stay seamless (a shared edge is mapped linearly by both of them). Pixels whose centre falls outside
        /// the destination quad, and self-intersecting (bowtie) quads, are left untouched - a degenerate quad
        /// warps nothing instead of throwing. <paramref name="Op"/>: <see cref="SR2D.Op.Paint"/> replaces the
        /// pixel (alpha included, the default); <see cref="SR2D.Op.AlphaBlend"/> composites the sampled pixel
        /// straight-alpha over the destination. Premultiplied sprites are converted in and out exactly like
        /// <see cref="FillPattern"/> does.
        /// </summary>
        public void DrawQuadWarp(Sprite Source, PointF[] SourceQuad, PointF[] DestQuad, SR2D.Op Op = SR2D.Op.Paint)
        {
            ArgumentNullException.ThrowIfNull(Source);
            ArgumentNullException.ThrowIfNull(SourceQuad);
            ArgumentNullException.ThrowIfNull(DestQuad);
            if (SourceQuad.Length < 4 || DestQuad.Length < 4)
                throw new ArgumentException("DrawQuadWarp needs four corners per quad (clockwise: TL, TR, BR, BL).");
            if (ReferenceEquals(Source, this))
                throw new ArgumentException("DrawQuadWarp: the source must be a different sprite - in-place warping would sample already-warped pixels.");
            if (meWidth == 0 || meHeight == 0 || Source.meWidth == 0 || Source.meHeight == 0) return;
            GdiSync();                                           // touching pBuf directly: flush pending GDI output first
            Source.GdiSync();                                    // ... on both ends, we read one and write the other

            // destination patch, double precision (the inverse solve degrades near degenerate quads).
            // p(u, v) = D0 + u*e0 + v*e1 + u*v*e2, corners clockwise: D0 = TL, D1 = TR, D2 = BR, D3 = BL.
            double d0x = DestQuad[0].X, d0y = DestQuad[0].Y;
            double e0x = DestQuad[1].X - d0x, e0y = DestQuad[1].Y - d0y;                       // top edge
            double e1x = DestQuad[3].X - d0x, e1y = DestQuad[3].Y - d0y;                       // left edge
            double e2x = DestQuad[2].X - DestQuad[3].X - e0x, e2y = DestQuad[2].Y - DestQuad[3].Y - e0y;
            // the same three deltas of the source quad - the destination (u, v) indexes straight into these
            double s0x = SourceQuad[0].X, s0y = SourceQuad[0].Y;
            double o0x = SourceQuad[1].X - s0x, o0y = SourceQuad[1].Y - s0y;
            double o1x = SourceQuad[3].X - s0x, o1y = SourceQuad[3].Y - s0y;
            double o2x = SourceQuad[2].X - SourceQuad[3].X - o0x, o2y = SourceQuad[2].Y - SourceQuad[3].Y - o0y;
            // cross terms of the quadratic in u:  cc2*u^2 + (cc1 - cross(q, e2))*u - cross(q, e1) = 0
            double cc1 = e0x * e1y - e0y * e1x;                    // cross(e0, e1)
            double cc2 = e0x * e2y - e0y * e2x;                    // cross(e0, e2); ~0 = the quad is a parallelogram (affine)

            // destination window: quad bbox, clipped to the LOCK rect (protects pixels like on every other verb) and the sprite
            double minX = Math.Min(Math.Min(DestQuad[0].X, DestQuad[1].X), Math.Min(DestQuad[2].X, DestQuad[3].X));
            double maxX = Math.Max(Math.Max(DestQuad[0].X, DestQuad[1].X), Math.Max(DestQuad[2].X, DestQuad[3].X));
            double minY = Math.Min(Math.Min(DestQuad[0].Y, DestQuad[1].Y), Math.Min(DestQuad[2].Y, DestQuad[3].Y));
            double maxY = Math.Max(Math.Max(DestQuad[0].Y, DestQuad[1].Y), Math.Max(DestQuad[2].Y, DestQuad[3].Y));
            var r = Rectangle.FromLTRB(meLeft, meTop, meRight, meBottom);
            r.Intersect(Rectangle.FromLTRB(0, 0, meWidth, meHeight));
            r.Intersect(Rectangle.FromLTRB((int)Math.Floor(minX), (int)Math.Floor(minY), (int)Math.Ceiling(maxX) + 1, (int)Math.Ceiling(maxY) + 1));
            if (r.IsEmpty) return;
            var db = Pixels; var sb = Source.Pixels;
            int sw = Source.meWidth;

            const double Eps = 1e-9;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                double qy = y + 0.5 - d0y;
                int row = y * meWidth;
                for (int x = r.Left; x < r.Right; x++)
                {
                    double qx = x + 0.5 - d0x;
                    // solve for u (eliminating v turns the map into a quadratic per pixel)
                    double a0 = qy * e1x - qx * e1y;               // -cross(q, e1)
                    double a1 = cc1 - (qx * e2y - qy * e2x);       // cross(e0, e1) - cross(q, e2)
                    double u;
                    if (Math.Abs(cc2) < 1e-12)
                    {
                        if (Math.Abs(a1) < 1e-12) continue;        // degenerate: no unique inverse here
                        u = -a0 / a1;
                        if (u < -Eps || u > 1 + Eps) continue;
                    }
                    else
                    {
                        double disc = a1 * a1 - 4 * cc2 * a0;
                        if (disc < 0) continue;                    // outside the quad (or inside a bowtie's unpaired lobe)
                        double sq = Math.Sqrt(disc);
                        double r1 = (-a1 - sq) / (2 * cc2), r2 = (-a1 + sq) / (2 * cc2);
                        bool ok1 = r1 >= -Eps && r1 <= 1 + Eps, ok2 = r2 >= -Eps && r2 <= 1 + Eps;
                        if (!ok1 && !ok2) continue;
                        u = ok1 && ok2 ? (Math.Abs(r1 - 0.5) <= Math.Abs(r2 - 0.5) ? r1 : r2) : ok1 ? r1 : r2;
                    }
                    // back-substitute v: q - u*e0 = v*(e1 + u*e2) - divide along the longer component
                    double bx = e1x + u * e2x, by = e1y + u * e2y;
                    double v;
                    if (Math.Abs(bx) >= Math.Abs(by)) { if (Math.Abs(bx) < 1e-12) continue; v = (qx - u * e0x) / bx; }
                    else { if (Math.Abs(by) < 1e-12) continue; v = (qy - u * e0y) / by; }
                    if (v < -Eps || v > 1 + Eps) continue;
                    if (u < 0) u = 0; else if (u > 1) u = 1;
                    if (v < 0) v = 0; else if (v > 1) v = 1;

                    // the same (u, v) on the source quad, nearest fetch, clamped into the source
                    double fx = s0x + u * o0x + v * o1x + u * v * o2x;
                    double fy = s0y + u * o0y + v * o1y + u * v * o2y;
                    int six = (int)Math.Floor(fx); if (six < 0) six = 0; else if (six >= sw) six = sw - 1;
                    int siy = (int)Math.Floor(fy); if (siy < 0) siy = 0; else if (siy >= Source.meHeight) siy = Source.meHeight - 1;
                    int sp = sb[siy * sw + six];
                    int sa = (sp >>> 24) & 255;
                    int di = row + x;

                    if (Op == SR2D.Op.AlphaBlend)
                    {   // straight-alpha source-over on all four bytes (native line_px case 3) via the shared mix
                        if (sa == 0) continue;                     // a fully transparent sample leaves the pixel alone
                        int sc = sp & 0x00ffffff;
                        if (Source.Premultiplied) sc = (((sp >>> 16 & 255) * 255 / sa) << 16) | (((sp >>> 8 & 255) * 255 / sa) << 8) | ((sp & 255) * 255 / sa);
                        int o = MixOp(db[di], (sa << 24) | sc, SR2D.LineOp.AlphaBlend, 1f);
                        if (Premultiplied)
                        { int oa = (o >>> 24) & 255; o = oa == 0 ? 0 : (oa << 24) | (((o >>> 16 & 255) * oa / 255) << 16) | (((o >>> 8 & 255) * oa / 255) << 8) | ((o & 255) * oa / 255); }
                        db[di] = o;
                    }
                    else
                    {   // Paint (and anything else): replace the pixel, alpha included
                        if (sa > 0 && Source.Premultiplied) sp = (sa << 24) | (((sp >>> 16 & 255) * 255 / sa) << 16) | (((sp >>> 8 & 255) * 255 / sa) << 8) | ((sp & 255) * 255 / sa);
                        if (sa > 0 && Premultiplied) sp = (sa << 24) | ((sp >>> 16 & 255) * sa / 255 << 16) | ((sp >>> 8 & 255) * sa / 255 << 8) | ((sp & 255) * sa / 255);
                        db[di] = sp;
                    }
                }
            }
        }

        /// <summary>
        /// Inverse bilinear: where inside a clockwise quad does <paramref name="P"/> sit, as (u, v) in 0..1.
        /// Null when the point lies outside the quad or the quad is degenerate there. This is the inverse of
        /// the map <see cref="DrawQuadWarp"/> samples with - and the point-in-quad test for cage editing.
        /// </summary>
        internal static (float U, float V)? QuadInvert(PointF[] Quad, PointF P)
        {
            ArgumentNullException.ThrowIfNull(Quad);
            if (Quad.Length < 4) throw new ArgumentException("QuadInvert needs four corners (clockwise: TL, TR, BR, BL).");
            double d0x = Quad[0].X, d0y = Quad[0].Y;
            double e0x = Quad[1].X - d0x, e0y = Quad[1].Y - d0y;
            double e1x = Quad[3].X - d0x, e1y = Quad[3].Y - d0y;
            double e2x = Quad[2].X - Quad[3].X - e0x, e2y = Quad[2].Y - Quad[3].Y - e0y;
            double cc1 = e0x * e1y - e0y * e1x, cc2 = e0x * e2y - e0y * e2x;
            const double Eps = 1e-7;
            double qx = P.X - d0x, qy = P.Y - d0y;
            double a0 = qy * e1x - qx * e1y, a1 = cc1 - (qx * e2y - qy * e2x);
            double u;
            if (Math.Abs(cc2) < 1e-12)
            {
                if (Math.Abs(a1) < 1e-12) return null;
                u = -a0 / a1;
            }
            else
            {
                double disc = a1 * a1 - 4 * cc2 * a0;
                if (disc < 0) return null;
                double sq = Math.Sqrt(disc);
                double r1 = (-a1 - sq) / (2 * cc2), r2 = (-a1 + sq) / (2 * cc2);
                bool ok1 = r1 >= -Eps && r1 <= 1 + Eps, ok2 = r2 >= -Eps && r2 <= 1 + Eps;
                if (!ok1 && !ok2) return null;
                u = ok1 && ok2 ? (Math.Abs(r1 - 0.5) <= Math.Abs(r2 - 0.5) ? r1 : r2) : ok1 ? r1 : r2;
            }
            double bx = e1x + u * e2x, by = e1y + u * e2y;
            double v;
            if (Math.Abs(bx) >= Math.Abs(by)) { if (Math.Abs(bx) < 1e-12) return null; v = (qx - u * e0x) / bx; }
            else { if (Math.Abs(by) < 1e-12) return null; v = (qy - u * e0y) / by; }
            if (u < -Eps || u > 1 + Eps || v < -Eps || v > 1 + Eps) return null;
            return ((float)Math.Clamp(u, 0f, 1f), (float)Math.Clamp(v, 0f, 1f));
        }
    }
}
