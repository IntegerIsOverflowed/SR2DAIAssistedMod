using System;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------
    // Image effects (new DLL): blur.
    //
    // DrawBlurred draws a sprite through a Gaussian-like blur. The blur is NOT
    // clipped by the sprite's rectangle: the soft edge extends 3*Radius pixels
    // (2*Radius in Fast mode) beyond it on every side, fading to transparent, and
    // is then composited with the requested op inside the lock rect only.
    //
    // Implementation notes (see native DRAW_BLUR):
    //   * three box blurs of radius R approximate a Gaussian with sigma ~ R
    //     (the classic Wells / Kovesi trick; the result is visually identical);
    //   * each box is a running sum, so the cost does NOT depend on the radius,
    //     only on the padded area (Width + 6R) x (Height + 6R);
    //   * everything is done in premultiplied 16-bit fixed point, so transparent
    //     pixels do not bleed their (usually black) colour into the edges.
    // ------------------------------------------------------------------------
    internal unsafe partial class Sprite
    {
        /// <summary>
        /// Draws <paramref name="Src"/> blurred, with its top-left corner at (x, y). The blur
        /// spreads <c>3*Radius</c> pixels (<c>2*Radius</c> when <paramref name="Fast"/>) beyond
        /// the sprite on every side (soft, not clipped by the sprite rect); only the lock rect
        /// of this sprite limits it. <paramref name="Radius"/> 0 draws the sprite unblurred.
        /// <para><paramref name="Op"/>: <c>DefaultOp</c> = Src.Op. All ops are supported; for
        /// premultiplied sprites use <see cref="SR2D.Op.AlphaOver"/> (correct on transparent
        /// targets), for straight-alpha ones <see cref="SR2D.Op.AlphaBlend"/>. Ops that ignore
        /// alpha (Paint, Add, Add2D, Max, Min, Mul, Mul2X, Blend) receive the alpha-weighted
        /// colour, i.e. the blur fades to black around the sprite - the natural neutral for
        /// additive glows (<c>Op.Add</c>) and for Max.</para>
        /// <para><paramref name="Strength"/>: 0..256 opacity multiplier applied to the blurred
        /// alpha (256 = as is). <paramref name="Opaque"/>: ignore the source alpha (treat every
        /// pixel as alpha 255) - use it for sprites without an alpha channel, whose alpha byte
        /// is 0. <paramref name="Fast"/>: two box passes instead of three (~30 % cheaper,
        /// slightly boxier profile).</para>
        /// Returns the number of destination pixels touched (0 = fully clipped). Needs the new DLL.
        /// </summary>
        public int DrawBlurred(Sprite Src, int x, int y, int Radius, SR2D.Op Op = SR2D.Op.DefaultOp,
                               int Strength = 256, bool Opaque = false, bool Fast = false, int BlendFactor = 128)
            => DrawBlurred(Src, x, y, Radius, Fast ? BlurQuality.Fast : BlurQuality.Gaussian, Op, Strength, Opaque, BlendFactor);

        /// <summary>As <see cref="DrawBlurred(Sprite, int, int, int, SR2D.Op, int, bool, bool, int)"/> with an explicit <see cref="BlurQuality"/> (Box = one pass, the cheapest).</summary>
        public int DrawBlurred(Sprite Src, int x, int y, int Radius, BlurQuality Quality, SR2D.Op Op = SR2D.Op.DefaultOp,
                               int Strength = 256, bool Opaque = false, int BlendFactor = 128)
        {
            if (meRight <= meLeft || meBottom <= meTop || Src.meWidth <= 0 || Src.meHeight <= 0) return 0;
            SR2D.Op op = Op == SR2D.Op.DefaultOp ? Src.Op : Op;
            int flags = (op == SR2D.Op.AlphaOver || Src.Premultiplied ? 1 : 0) | (Opaque ? 2 : 0) | QualityFlags(Quality);
            return SR2D.Native.DrawBlur(Src.pBuf, Src.meWidth, Src.meHeight, pBuf, meWidth,
                                        meLeft, meTop, meRight, meBottom, x, y, Radius, OpWord(op, Src), BlendFactor, flags, Strength);
        }
        static int QualityFlags(BlurQuality q) => q == BlurQuality.Box ? 16 : q == BlurQuality.Fast ? 4 : 0;
        static int BlurPasses(BlurQuality q) => q == BlurQuality.Box ? 1 : q == BlurQuality.Fast ? 2 : 3;

        /// <summary>See <see cref="DrawBlurred(Sprite, int, int, int, SR2D.Op, int, bool, bool, int)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DrawBlurred(Sprite Src, System.Drawing.Point at, int Radius, SR2D.Op Op = SR2D.Op.DefaultOp,
                               int Strength = 256, bool Opaque = false, bool Fast = false, int BlendFactor = 128)
            => DrawBlurred(Src, at.X, at.Y, Radius, Op, Strength, Opaque, Fast, BlendFactor);

        /// <summary>
        /// Draws <paramref name="Src"/> blurred and centred on (cx, cy) - handy for glows and
        /// shadows: <c>DrawBlurredAt(sprite, cx, cy, r, Op.Add)</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DrawBlurredAt(Sprite Src, int cx, int cy, int Radius, SR2D.Op Op = SR2D.Op.DefaultOp,
                                 int Strength = 256, bool Opaque = false, bool Fast = false, int BlendFactor = 128)
            => DrawBlurred(Src, cx - Src.meWidth / 2, cy - Src.meHeight / 2, Radius, Op, Strength, Opaque, Fast, BlendFactor);

        /// <summary>
        /// Blurs this sprite in place. The picture cannot grow, so the blur is clipped by the
        /// sprite's own rect (content near the edges fades toward transparent as if the
        /// surroundings were transparent black). Straight-alpha sprites are converted to
        /// premultiplied and back; premultiplied ones (Op == AlphaOver) stay premultiplied.
        /// Only the lock rect is modified; the source is read from the whole sprite.
        /// </summary>
        public void Blur(int Radius, bool Fast = false) => Blur(Radius, Fast ? BlurQuality.Fast : BlurQuality.Gaussian);
        /// <summary>In-place blur with an explicit <see cref="BlurQuality"/> (Box = one pass, the cheapest).</summary>
        public void Blur(int Radius, BlurQuality Quality)
        {
            if (Radius <= 0 || meRight <= meLeft || meBottom <= meTop) return;
            bool premul = Op == SR2D.Op.AlphaOver;
            int flags = (premul ? 1 : 0) | QualityFlags(Quality);
            // in place: DRAW_BLUR consumes the whole source before its first write. Paint copies the
            // premultiplied result (alpha included), op 12 the straight-alpha one.
            _ = SR2D.Native.DrawBlur(pBuf, meWidth, meHeight, pBuf, meWidth, meLeft, meTop, meRight, meBottom, 0, 0, Radius,
                                 premul ? (int)SR2D.Op.Paint : 12, 128, flags, 256);
        }

        /// <summary>
        /// Returns a new sprite holding this sprite blurred, enlarged by the blur margin
        /// (<c>3*Radius</c>, or <c>2*Radius</c> if <paramref name="Fast"/>) on every side so
        /// nothing is cut off. <paramref name="Margin"/> receives that margin, i.e. the offset
        /// to subtract from a draw position to keep the picture in place. The result is
        /// premultiplied with Op = AlphaOver if this sprite is, straight-alpha otherwise.
        /// Use it to blur once and draw many times.
        /// </summary>
        public Sprite ToBlurred(int Radius, out int Margin, bool Fast = false) => ToBlurred(Radius, Fast ? BlurQuality.Fast : BlurQuality.Gaussian, out Margin);
        /// <summary>As <see cref="ToBlurred(int, out int, bool)"/> with an explicit <see cref="BlurQuality"/>; the margin is <c>Radius * passes</c>.</summary>
        public Sprite ToBlurred(int Radius, BlurQuality Quality, out int Margin)
        {
            if (Radius < 0) Radius = 0; if (Radius > 512) Radius = 512;
            Margin = Radius * BlurPasses(Quality);
            bool premul = Op == SR2D.Op.AlphaOver;
            Sprite r = new Sprite(meWidth + 2 * Margin, meHeight + 2 * Margin);   // zero-initialised
            int flags = (premul ? 1 : 0) | QualityFlags(Quality);
            _ = SR2D.Native.DrawBlur(pBuf, meWidth, meHeight, r.pBuf, r.meWidth, 0, 0, r.meWidth, r.meHeight, Margin, Margin,
                                 Radius, premul ? (int)SR2D.Op.Paint : 12, 128, flags, 256);
            r.meOp = premul ? SR2D.Op.AlphaOver : (meOp == SR2D.Op.DefaultOp ? SR2D.Op.AlphaBlend : meOp);
            return r;
        }
    }
}
