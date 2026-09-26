using System.Drawing;
using System.Runtime.CompilerServices;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------
    // Rectangle (x, y, width, height) overloads for the methods that take
    // left/right/top/bottom in the original API. The original signatures are
    // untouched; these forward to them (inlined, so there is no extra cost).
    //
    // System.Drawing.Rectangle was chosen over (Point, Size) or a custom struct:
    // it IS x/y/width/height, WinForms already gives you one everywhere
    // (ClientRectangle, e.ClipRectangle, Bounds) and it has Intersect/Inflate/
    // Contains for free. Rectangle.Right/Bottom are exclusive, exactly like the
    // original Right/Bottom arguments, so no off-by-one anywhere.
    // ------------------------------------------------------------------------
    internal unsafe partial class Sprite
    {
        /// <summary>Whole surface as a rectangle: (0, 0, Width, Height).</summary>
        public Rectangle Bounds => new Rectangle(0, 0, meWidth, meHeight);

        /// <summary>Current lock rect (clipping rectangle) as x/y/width/height.</summary>
        public Rectangle LockRect => Rectangle.FromLTRB(meLeft, meTop, meRight, meBottom);

        /// <summary>Fills the pixels of <paramref name="r"/> (clipped to the lock rect). Same as
        /// <c>ClearRect(r.Left, r.Right, r.Top, r.Bottom, c)</c>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearRect(Rectangle r, int c) => ClearRect(r.Left, r.Right, r.Top, r.Bottom, c);

        /// <summary>Fills the w×h pixels starting at (x, y). Alias of <see cref="ClearRect(Rectangle, int)"/>
        /// with explicit ints (named differently because the original <c>ClearRect</c> already
        /// takes four ints in L/R/T/B order).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearRectXY(int x, int y, int w, int h, int c) => ClearRect(x, x + w, y, y + h, c);

        /// <summary>Restricts all drawing to <paramref name="r"/> (clamped to the surface).
        /// An empty rectangle resets to the whole surface, like <c>SetLockRect()</c>.</summary>
        public void SetLockRect(Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) { SetLockRect(); return; }
            SetLockRect(r.Left, r.Right, r.Top, r.Bottom);
        }
        /// <summary>Restricts all drawing to the w×h pixels at (x, y).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetLockRectXY(int x, int y, int w, int h) => SetLockRect(new Rectangle(x, y, w, h));

        /// <summary>View sharing this sprite's pixels, clipped to <paramref name="r"/> (see <see cref="CreateView(int,int,int,int)"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Sprite CreateView(Rectangle r) => CreateView(r.Left, r.Top, r.Right, r.Bottom);

        /// <summary>Copies the source rectangle of the surface to (DestX, DestY) on a device context.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PaintToDevice(System.Runtime.InteropServices.HandleRef hDC, int DestX, int DestY, Rectangle Src)
            => PaintToDevice(hDC, DestX, DestY, Src.X, Src.Y, Src.Width, Src.Height);

        /// <summary>Tiles <paramref name="Src"/> over <paramref name="Dest"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TileDraw(Sprite Src, Rectangle Dest, int Sx = 0, int Sy = 0, SR2D.Op Op = SR2D.Op.DefaultOp)
            => TileDraw(Src, Dest.X, Dest.Y, Dest.Width, Dest.Height, Sx, Sy, Op);

        /// <summary>Pixel accessor for <paramref name="r"/> (clamped), see <see cref="Region(int,int,int,int)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PixelRect Region(Rectangle r) => Region(r.X, r.Y, r.Width, r.Height);

        /// <summary>Draws <paramref name="Src"/> with its top-left corner at <paramref name="At"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Draw(Sprite Src, System.Drawing.Point At, SR2D.Op Op = SR2D.Op.DefaultOp) => Draw(Src, At.X, At.Y, Op);
    }
}
