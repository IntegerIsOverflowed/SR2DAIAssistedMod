using System;
using System.Drawing;

namespace Sr2d64CSport
{
    /// <summary>
    /// Graphic look of <see cref="SpriteButton"/>, <see cref="SpriteSlider"/>, <see cref="SpriteProgress"/> and
    /// <see cref="SpriteWheel"/> (their <c>Look</c> property). <see cref="Classic"/> is the SR2D house look and stays
    /// the default, so nothing changes until a control asks for another one.
    /// <list type="bullet">
    /// <item><description><see cref="Hairline"/> (<b>Liquid Glass / Aqua Neon</b>): glossy curved glass capsules, pressurized glass tubes with glowing plasma cores, convex glass-orb thumbs, and smoked-glass lens drums.</description></item>
    /// <item><description><see cref="Segmented"/> (<b>Heavy Industrial / Riveted Steel</b>): brushed gunmetal steel plates with 3D corner rivets, diagonal hazard/turbine chevron bars, knurled steel block thumbs, and pivoting analog needle gauges.</description></item>
    /// </list>
    /// </summary>
    public enum ControlLook
    {
        /// <summary>The house look: shaded face or track, highlight and rim, plus the control's own style (ButtonShape, WheelStyle, ...).</summary>
        Classic,
        /// <summary>
        /// Liquid Glass / Aqua Neon look: glossy curved glass capsules with upper reflection domes and subsurface glow,
        /// glass-tube tracks with glowing liquid cores and spherical glass-orb thumbs, and smoked-glass lens wheels.
        /// </summary>
        Hairline,
        /// <summary>
        /// Heavy Industrial / Riveted Steel look: brushed gunmetal steel faceplates with 3D corner rivets, slotted tracks
        /// with 45-degree angled hazard stripes, knurled steel block thumbs, and bolted analog needle dial gauges.
        /// </summary>
        Segmented,
    }

    /// <summary>
    /// Shared drawing helpers for the Liquid Glass (<see cref="ControlLook.Hairline"/>) and Heavy Industrial (<see cref="ControlLook.Segmented"/>) looks.
    /// </summary>
    public abstract partial class SpriteControlBase
    {
        /// <summary>Draws a 3D metallic rivet / bolt head at (<paramref name="rx"/>, <paramref name="ry"/>).</summary>
        protected void LookRivet(Sprite s, float rx, float ry, float r = 2.0f)
        {
            int socket = Mix(BackColor, Black, 0.82f);
            int head   = Mix(BackColor, ThumbColor, 0.52f);
            int glint  = Mix(ThumbColor, White, 0.65f);
            s.FillCircle(rx, ry, r + 0.5f, socket, SR2D.LineOp.Set, true);
            s.FillCircle(rx, ry, r, head, SR2D.LineOp.Set, true);
            s.FillRect((int)MathF.Round(rx - r * 0.45f), (int)MathF.Round(ry - r * 0.45f), 1, 1, glint);
        }

        /// <summary>Draws a brushed gunmetal steel plate with horizontal grain lines, bevel borders, and 4 corner rivets.</summary>
        protected void LookIndustrialPlate(Sprite s, float x, float y, float w, float h, int baseCol, int rimCol, bool rivets = true)
        {
            int ix = (int)MathF.Round(x), iy = (int)MathF.Round(y), iw = Math.Max(2, (int)MathF.Round(w)), ih = Math.Max(2, (int)MathF.Round(h));
            s.FillRect(ix, iy, iw, ih, Mix(BackColor, Black, 0.72f));
            int cEven = baseCol, cOdd = Mix(baseCol, Black, 0.14f);
            for (int row = iy + 1; row < iy + ih - 1; row++)
                s.FillRect(ix + 1, row, iw - 2, 1, (row & 1) == 0 ? cEven : cOdd);
            // Top metallic highlight lip + bottom dark shadow lip + outer rim
            s.FillRect(ix + 1, iy + 1, iw - 2, 1, Mix(baseCol, White, 0.28f));
            s.FillRect(ix + 1, iy + ih - 2, iw - 2, 1, Mix(baseCol, Black, 0.48f));
            s.DrawRect(ix + 0.5f, iy + 0.5f, iw - 1, ih - 1, rimCol, 1f, false);
            if (rivets && iw >= 20 && ih >= 14)
            {
                float pad = Math.Clamp(Math.Min(iw, ih) * 0.20f, 3.5f, 5.5f);
                float rr = Math.Clamp(Math.Min(iw, ih) * 0.08f, 1.4f, 2.1f);
                LookRivet(s, x + pad, y + pad, rr);
                LookRivet(s, x + w - pad, y + pad, rr);
                LookRivet(s, x + pad, y + h - pad, rr);
                LookRivet(s, x + w - pad, y + h - pad, rr);
            }
        }

        /// <summary>Fills a rectangular slot with 45-degree diagonal industrial hazard/turbine chevron stripes.</summary>
        protected void LookHazardBar(Sprite s, float x, float y, float w, float h, bool hz, int mainCol, int stripeCol)
        {
            int ix = (int)MathF.Round(x), iy = (int)MathF.Round(y), iw = (int)MathF.Round(w), ih = (int)MathF.Round(h);
            if (iw <= 0 || ih <= 0) return;
            s.FillRect(ix, iy, iw, ih, mainCol);
            using var clip = s.CreateView(new Rectangle(ix, iy, iw, ih));
            int step = Math.Max(6, (hz ? ih : iw) * 3 / 4);
            int band = Math.Max(2, step / 2);
            Span<PointF> poly = stackalloc PointF[4];
            if (hz)
            {
                for (int sx = ix - ih; sx < ix + iw + ih; sx += step)
                {
                    poly[0] = new PointF(sx, iy + ih);
                    poly[1] = new PointF(sx + ih * 0.65f, iy);
                    poly[2] = new PointF(sx + ih * 0.65f + band, iy);
                    poly[3] = new PointF(sx + band, iy + ih);
                    clip.FillPolygon(poly, stripeCol, SR2D.LineOp.Set, true);
                }
                s.FillRect(ix, iy, iw, 1, Mix(mainCol, White, 0.30f));
            }
            else
            {
                for (int sy = iy - iw; sy < iy + ih + iw; sy += step)
                {
                    poly[0] = new PointF(ix, sy);
                    poly[1] = new PointF(ix + iw, sy + iw * 0.65f);
                    poly[2] = new PointF(ix + iw, sy + iw * 0.65f + band);
                    poly[3] = new PointF(ix, sy + band);
                    clip.FillPolygon(poly, stripeCol, SR2D.LineOp.Set, true);
                }
                s.FillRect(ix, iy, 1, ih, Mix(mainCol, White, 0.30f));
            }
        }

        /// <summary>Draws a glossy Liquid Glass capsule with a deep vertical gradient, upper reflection dome, and bottom subsurface glow.</summary>
        protected void LookGlassCapsule(Sprite s, float x, float y, float w, float h, float rad, int tintCol, float glowStrength, int rimCol)
        {
            if (w < 2f || h < 2f) return;
            int topCol = Mix(BackColor, tintCol, 0.28f + 0.25f * glowStrength);
            int midCol = Mix(BackColor, Black, 0.52f - 0.15f * glowStrength);
            int botCol = Mix(midCol, tintCol, 0.45f + 0.40f * glowStrength);
            s.FillRoundRect(x, y, w, h, rad, SpriteGradient.Linear(0f, 0f, 0f, 1f, topCol, midCol, botCol), true);

            // Upper curved glass reflection dome
            float domeX = x + Math.Max(2f, rad * 0.35f), domeY = y + 1.5f;
            float domeW = Math.Max(2f, w - 2f * Math.Max(2f, rad * 0.35f)), domeH = Math.Max(2f, h * 0.42f);
            float domeRad = Math.Max(1f, Math.Min(rad * 0.7f, domeH * 0.5f));
            int glossTop = Argb(Mix(tintCol, White, 0.75f), (int)(120 + 55 * glowStrength));
            int glossBot = Argb(Mix(tintCol, White, 0.50f), 15);
            s.FillRoundRect(domeX, domeY, domeW, domeH, domeRad, SpriteGradient.Linear(0f, 0f, 0f, 1f, glossTop, glossBot), true);

            // Outer glass rim
            DrawRound(s, x, y, w, h, rad, rimCol, 1.1f);
        }

        /// <summary>Draws a 3D translucent Glass Sphere / Orb with radial focus shading and an upper specular crescent.</summary>
        protected void LookGlassOrb(Sprite s, float cx, float cy, float r, int tintCol, int rimCol)
        {
            if (r < 2f) return;
            int coreHi = Mix(tintCol, White, 0.65f);
            int midCol = tintCol;
            int edgeCol = Mix(tintCol, Black, 0.42f);
            s.FillCircle(cx, cy, r, SpriteGradient.RadialFocus(0.5f, 0.5f, 0.5f, 0.35f, 0.28f, true, coreHi, midCol, edgeCol), true);
            // Upper glass reflection lens crescent
            float rx = r * 0.62f, ry = r * 0.36f;
            s.FillEllipse(cx, cy - r * 0.42f, rx, ry, Argb(White, 155), SR2D.LineOp.AlphaBlend, true);
            s.DrawCircle(cx, cy, r, rimCol, 1.1f, true);
        }

        // Keep legacy helper signatures so any external caller still compiles cleanly
        protected static void LookBar(Sprite s, float q0, float q1, float m, float th, bool hz, int col, SR2D.LineOp op = SR2D.LineOp.Set)
        {
            int a0 = (int)MathF.Round(MathF.Min(q0, q1)), a1 = (int)MathF.Round(MathF.Max(q0, q1));
            if (a1 <= a0) return;
            int t0 = (int)MathF.Round(m - th / 2), t1 = Math.Max(t0 + 1, (int)MathF.Round(m + th / 2));
            if (hz) s.FillRect(a0, t0, a1 - a0, t1 - t0, col, op);
            else s.FillRect(t0, a0, t1 - t0, a1 - a0, col, op);
        }

        protected static void LookCells(Sprite s, float qa, float qb, float m, float th, bool hz, int n, float gap, Func<double, int> colour)
        {
            int t0 = (int)MathF.Round(m - th / 2), t1 = Math.Max(t0 + 1, (int)MathF.Round(m + th / 2));
            for (int i = 0; i < n; i++)
            {
                float p0 = qa + (qb - qa) * i / n, p1 = qa + (qb - qa) * (i + 1) / n;
                int a0 = (int)MathF.Round(MathF.Min(p0, p1) + gap / 2), a1 = (int)MathF.Round(MathF.Max(p0, p1) - gap / 2);
                if (a1 <= a0) continue;
                int c = colour((i + 0.5) / n);
                if (hz) s.FillRect(a0, t0, a1 - a0, t1 - t0, c);
                else s.FillRect(t0, a0, t1 - t0, a1 - a0, c);
            }
        }

        protected static void LookArc(Sprite s, float cx, float cy, float r, float w, double from, double len, int col)
        {
            if (len <= 0) return;
            if (len >= 0.999) { s.DrawCircle(cx, cy, r, col, w, true); return; }
            int n = Math.Clamp((int)(r * len * 2 * MathF.PI / 2f) + 2, 2, 720);
            var pts = new PointF[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double a = (270 + 360 * (from + len * i / n)) * Math.PI / 180;
                pts[i] = new PointF(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
            }
            s.DrawPolyline(pts.AsSpan(), col, w, true, false, SR2D.LineOp.Set, true);
        }

        protected static void LookDial(Sprite s, float cx, float cy, float r0, int n, int shortLen, int longLen, int major, int col)
        {
            for (int i = 0; i < n; i++)
            {
                double a = (270 + 360.0 * i / n) * Math.PI / 180;
                float len = i % major == 0 ? longLen : shortLen;
                float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                s.DrawWideLine(cx + c * r0, cy + sn * r0, cx + c * (r0 + len), cy + sn * (r0 + len), col, 1f, true);
            }
        }
    }
}
