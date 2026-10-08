using System;
using System.Drawing;

namespace Sr2d64CSport
{
    // Editor blend modes (Photoshop / GIMP / PDF): convenience entry points. The modes themselves
    // are ordinary op codes (SR2D.BlendMode == SR2D.Op.Multiply etc.), so every existing draw call
    // accepts them as well; these overloads just add the opacity argument and the geometry
    // shortcuts. Everything is done in the DLL (BLEND_MODE / MASK_BLEND_MODE / DRAW_WARP / DRAW_FX
    // with a blend-mode op word).
    public unsafe partial class Sprite
    {
        /// <summary>
        /// Draws <paramref name="Src"/> at (x, y) with an editor blend mode, like a layer set to that
        /// mode: <c>dst = dst + (Mode(src, dst) - dst) * srcAlpha * Opacity</c>. Premultiplied sprites
        /// (<see cref="Premultiplied"/>) on either side are handled; on a premultiplied destination
        /// the alpha accumulates (AlphaOver rule), otherwise it is left alone (AlphaBlend rule).
        /// <see cref="SR2D.BlendMode.Normal"/> at opacity 1 is exactly AlphaBlend / AlphaOver.
        /// </summary>
        public void DrawBlend(Sprite Src, int x, int y, SR2D.BlendMode Mode, float Opacity = 1f)
        {
            if (!Clip2(x, x + Src.meWidth, y, y + Src.meHeight, Src.meWidth, Src.pBuf, out Clip k)) return;
            SR2D.Native.BlendMode(k.S, k.D, k.W, k.H, Src.meWidth, meWidth, OpWord(Mode.ToOp(Opacity), Src));
        }

        /// <summary>Blend-mode blit restricted by a 1-bit mask sprite, see <see cref="MaskDraw"/> for the mask arguments.</summary>
        public void DrawBlend(Sprite Src, Sprite SrcMask, int x, int y, int MaskX, int MaskY, int Mask, SR2D.BlendMode Mode, float Opacity = 1f, bool NotMask = false)
        {
            if (!Clip3(x, x + Src.meWidth, y, y + Src.meHeight, MaskX, MaskX + SrcMask.meWidth, MaskY, MaskY + SrcMask.meHeight,
                       Src.meWidth, SrcMask.meWidth, Src.pBuf, SrcMask.pBuf, out Clip k)) return;
            SR2D.Native.MaskBlendMode(k.S, k.D, k.M, k.W, k.H, Mask, Src.meWidth, meWidth, SrcMask.meWidth, NotMask ? 1 : 0, OpWord(Mode.ToOp(Opacity), Src));
        }

        /// <summary>Blend-mode blit restricted by a <see cref="Selection"/> (soft edges honoured): the selection scales the coverage.</summary>
        public void DrawBlend(Sprite Src, int x, int y, Selection Sel, SR2D.BlendMode Mode, float Opacity = 1f)
        {
            if (Sel == null) { DrawBlend(Src, x, y, Mode, Opacity); return; }
            Apply(Sel, s => s.DrawBlend(Src, x, y, Mode, Opacity));
        }

        /// <summary>Scaled draw with a blend mode (destination size in pixels), see <see cref="DrawScaled(Sprite, int, int, int, int, SR2D.Op, SR2D.Filter, int)"/>.</summary>
        public void DrawBlendScaled(Sprite Src, int x, int y, int DestWidth, int DestHeight, SR2D.BlendMode Mode, float Opacity = 1f, SR2D.Filter Filter = SR2D.Filter.Auto)
            => DrawScaled(Src, x, y, DestWidth, DestHeight, Mode.ToOp(Opacity), Filter);

        /// <summary>Rotated (and optionally scaled) draw with a blend mode, see <see cref="DrawRotate2"/>.</summary>
        public void DrawBlendRotated(Sprite Src, int x, int y, float Angle, SR2D.BlendMode Mode, float Opacity = 1f,
                                     int DestWidth = 0, int DestHeight = 0, float PivotX = -1, float PivotY = -1, SR2D.Filter Filter = SR2D.Filter.Auto)
            => DrawRotate2(Src, x, y, Angle, DestWidth, DestHeight, PivotX, PivotY, Mode.ToOp(Opacity), Filter);

        /// <summary>Arbitrary quad (TL, TR, BR, BL) with a blend mode, see <see cref="DrawQuad"/>.</summary>
        public void DrawBlendQuad(Sprite Src, ReadOnlySpan<PointF> Quad, SR2D.BlendMode Mode, float Opacity = 1f, SR2D.Filter Filter = SR2D.Filter.Auto)
            => DrawQuad(Src, Quad, default, Mode.ToOp(Opacity), Filter);

        /// <summary>Effect chain + blend mode at (x, y), see <see cref="DrawFx(Sprite, int, int, Effects, SR2D.Op, bool, int)"/>.</summary>
        public bool DrawBlendFx(Sprite Src, int x, int y, Effects Fx, SR2D.BlendMode Mode, float Opacity = 1f)
            => DrawFx(Src, x, y, Fx, Mode.ToOp(Opacity));

        /// <summary>Fills the whole lock rectangle with a colour through a blend mode (a "fill layer"): the colour's alpha times <paramref name="Opacity"/> is the coverage.</summary>
        public void FillBlend(int c, SR2D.BlendMode Mode, float Opacity = 1f)
            => FillRect(meLeft, meTop, meRight - meLeft, meBottom - meTop, c, Mode.ToLineOp(Opacity), false);

        /// <summary>Fills a rectangle with a colour through a blend mode, see <see cref="FillRect(float, float, float, float, int, SR2D.LineOp, bool, int)"/>.</summary>
        public void FillBlend(float x, float y, float w, float h, int c, SR2D.BlendMode Mode, float Opacity = 1f, bool AA = false)
            => FillRect(x, y, w, h, c, Mode.ToLineOp(Opacity), AA);
    }
}
