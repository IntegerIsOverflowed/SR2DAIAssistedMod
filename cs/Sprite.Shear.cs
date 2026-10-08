using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    // Rotation by three shears (A. Paeth, "A Fast Algorithm for General Raster Rotation", 1986).
    //
    //   R(a) = Shear_x(-tan(a/2)) * Shear_y(sin(a)) * Shear_x(-tan(a/2))
    //
    // A shear only moves whole rows (or columns) sideways by an offset that depends on the row, so every
    // source pixel is copied exactly once per pass and lands exactly once: no pixel is dropped and none
    // is duplicated - the "holes" and doubled pixels of a nearest-neighbour forward rotation cannot occur,
    // and the content stays crisp (no resampling blur) because nothing is interpolated. The price is
    // that the outline is a staircase of row/column steps (like the classic Amiga / demo-scene rotozoom)
    // and that angles near +-90 degrees make tan(a/2) large: angles are therefore reduced to -45..45
    // degrees by first turning the sprite by a multiple of 90 degrees with the exact RotCW/RotCCW
    // transforms (also lossless), and the three shears then handle the remainder.
    //
    // The passes run in managed code on row copies (memmove of ints): a 256x256 sprite rotates in
    // ~0.2 ms. The rotated image is built in a scratch sprite (padded, transparent outside the shape)
    // and composited with the requested Op; pixels the shape does not cover are skipped, so an Op like
    // Paint leaves the canvas around the rotated sprite untouched.
    public unsafe partial class Sprite
    {
        // scratch slots: 0 = pre-transposed source, 1 = final image, 2 = transposed intermediate, 3 = coverage mask
        [ThreadStatic] static Sprite?[]? tShearView, tShearStore;

        /// <summary>
        /// Lossless "three-shear" rotation: draws <paramref name="Src"/> rotated by <paramref name="Angle"/> radians
        /// (clockwise on screen, like <see cref="DrawRotate2"/>) so that source pixel (PivotX, PivotY) lands on canvas
        /// pixel (DestX, DestY). Every source pixel appears exactly once in the result (no dropped or doubled pixels,
        /// no interpolation, no blur); the outline is a pixel staircase. Pass PivotX/PivotY = -1 for the centre.
        /// <paramref name="Op"/> = how the rotated pixels are combined (DefaultOp = the source's op; Paint for an opaque
        /// sprite is the fastest); <paramref name="BlendFactor"/> only for <see cref="SR2D.Op.Blend"/>.
        /// For AlphaTest sources the colour key / alpha is honoured; for opaque sprites (Paint) only the
        /// rotated shape is written - the padding around it is skipped, not painted black.
        /// </summary>
        public void DrawRotateShear(Sprite Src, int DestX, int DestY, float Angle, int PivotX = -1, int PivotY = -1,
                                    SR2D.Op Op = SR2D.Op.DefaultOp, int BlendFactor = 128)
        {
            if (Src == null || Src.meWidth <= 0 || Src.meHeight <= 0 || meRight <= meLeft || meBottom <= meTop) return;
            if (Op == SR2D.Op.DefaultOp) Op = Src.Op;
            if (PivotX < 0) PivotX = Src.meWidth / 2;
            if (PivotY < 0) PivotY = Src.meHeight / 2;

            // ---- 1. reduce the angle to -45..45 degrees with exact quarter turns
            double a = Math.IEEERemainder(Angle, 2 * Math.PI);          // -pi..pi
            int quarter = (int)Math.Round(a / (Math.PI / 2));            // -2..2
            double rem = a - quarter * (Math.PI / 2);                    // -pi/4..pi/4
            quarter = ((quarter % 4) + 4) % 4;                           // 0..3 clockwise quarter turns

            Sprite baseSpr = Src; int px = PivotX, py = PivotY;
            if (quarter != 0)
            {
                // exact 90/180/270 degree turn (clockwise on screen) into scratch A; move the pivot along
                // NB: the original DLL's ROT_CW maps (x,y) -> (y, w-1-x), which is a COUNTER-clockwise quarter turn on a
                // y-down screen (named for y-up maths coordinates; kept bit-exact). Screen-clockwise therefore = RotCCW.
                SR2D.Transform tr = quarter == 1 ? SR2D.Transform.RotCCW : quarter == 2 ? SR2D.Transform.FlipXY : SR2D.Transform.RotCW;
                int w = Src.meWidth, h = Src.meHeight;
                int tw = quarter == 2 ? w : h, th = quarter == 2 ? h : w;
                Sprite t = Scratch(0, tw, th);
                Transform(Src.pBuf, t.pBuf, w, h, tr);
                switch (quarter)
                {
                    case 1: px = h - 1 - PivotY; py = PivotX; break;              // (x,y) -> (h-1-y, x)
                    case 2: px = w - 1 - PivotX; py = h - 1 - PivotY; break;
                    default: px = PivotY; py = w - 1 - PivotX; break;             // (x,y) -> (y, w-1-x)
                }
                baseSpr = t;
            }

            int sw = baseSpr.meWidth, sh = baseSpr.meHeight;
            // ---- 2. the three shears. Screen y grows downwards; a clockwise rotation on screen by `rem`
            // is the standard matrix [c -s; s c] in (x right, y down) coordinates, and Paeth's
            // factorisation with alpha = -tan(rem/2), beta = sin(rem) holds unchanged in that frame.
            double alpha = -Math.Tan(rem / 2), beta = Math.Sin(rem);
            // Exact extents instead of symmetric worst-case padding: every shift is monotonic in the row / column
            // index, so its extremes sit at the two ends of the occupied span. Pivot row / column never move
            // (round(0) = 0). The buffer is therefore the tight bounding box of the three passes: at 45 degrees
            // 574x512 for a 256x256 source instead of 1234x922 - the routine is memory bound, so this is ~3x faster.
            int s1a = (int)Math.Round(alpha * (0 - py)), s1b = (int)Math.Round(alpha * (sh - 1 - py));
            int s1min = Math.Min(s1a, s1b), s1max = Math.Max(s1a, s1b);
            int W1 = sw + s1max - s1min;                                     // occupied width after pass 1 (the "band")
            int pivX1 = px - s1min;                                          // pivot column inside the band
            int s2a = (int)Math.Round(beta * (0 - pivX1)), s2b = (int)Math.Round(beta * (W1 - 1 - pivX1));
            int s2min = Math.Min(s2a, s2b), s2max = Math.Max(s2a, s2b);
            int H3 = sh + s2max - s2min;                                     // final height
            int oy = -s2min, pivY1 = py + oy;                                // source row y sits at oy + y; pivot row
            int s3a = (int)Math.Round(alpha * (0 - pivY1)), s3b = (int)Math.Round(alpha * (H3 - 1 - pivY1));
            int s3min = Math.Min(s3a, s3b), s3max = Math.Max(s3a, s3b);
            int W3 = W1 + s3max - s3min;                                     // final width
            int ox = -s3min, pivX3 = pivX1 + ox;                             // band column 0 sits at ox; pivot column

            Sprite bufA = Scratch(1, W3, H3);                                // final image
            bufA.ClearBuffer(0);
            // pass 1: shear x - source row y moves by alpha * (y - py); lands in the band at oy + y
            for (int y = 0; y < sh; y++)
            {
                int shift = (int)Math.Round(alpha * (y - py));
                int* srow = baseSpr.pBuf + (long)y * sw;
                int* drow = bufA.pBuf + (long)(oy + y) * W3 + ox - s1min + shift;
                Buffer.MemoryCopy(srow, drow, (long)sw * 4, (long)sw * 4);
            }
            // pass 2: shear y - column x moves by beta * (x - pivX3). Column-wise access would be a cache miss per
            // pixel; instead the buffer is transposed with the native FLIP_X_ROT_CCW kernel (dest[x*h+y] = src[y*w+x],
            // a pure transpose, its own inverse), the shear runs as a row shift over the band's columns only and the
            // result is transposed back.
            Sprite bufT = Scratch(2, H3, W3);
            Transform(bufA.pBuf, bufT.pBuf, W3, H3, SR2D.Transform.FlipXRotCCW);
            ShearRows(bufT.pBuf, H3, beta, pivX3, ox, ox + W1);
            Transform(bufT.pBuf, bufA.pBuf, H3, W3, SR2D.Transform.FlipXRotCCW);
            // pass 3: shear x again - row y moves by alpha * (y - pivY1)
            ShearRows(bufA.pBuf, W3, alpha, pivY1, 0, H3);

            // ---- 3. composite: the pivot is at (pivX3, pivY1) in bufA and must land on (DestX, DestY)
            int dx = DestX - pivX3, dy = DestY - pivY1;
            if (Op == SR2D.Op.AlphaTest || Op == SR2D.Op.AlphaBlend || Op == SR2D.Op.AlphaOver || SR2D.IsBlendMode(Op))
            {
                // the padding is 0 = fully transparent: these ops skip it by themselves
                Draw(bufA, dx, dy, Op);
                return;
            }
            // other ops: restrict to the rotated shape with a mask (bit 0 = "covered") so the padding is not applied
            Sprite mask = Scratch(3, W3, H3);
            // coverage = every non-zero pixel (opaque sources carry alpha 0xFF, so only a pixel that is
            // exactly 0 - transparent black - in the source is treated as "not there", which is what
            // the alpha ops would do with it as well)
            {
                int n = W3 * H3, i = 0; int* ap = bufA.pBuf, mp = mask.pBuf;
                int lanes = Vector<int>.Count;
                for (; i + lanes <= n; i += lanes)
                    Vector.AndNot(Vector<int>.One, Vector.Equals(Unsafe.ReadUnaligned<Vector<int>>(ap + i), Vector<int>.Zero)).StoreUnsafe(ref *(mp + i));
                for (; i < n; i++) mp[i] = ap[i] != 0 ? 1 : 0;
            }
            if (Op == SR2D.Op.Blend) MaskBlend(bufA, mask, dx, dy, dx, dy, BlendFactor, 1, false);
            else MaskDraw(bufA, mask, dx, dy, dx, dy, 1, false, Op);
        }

        /// <summary>Row shear in place for rows y0..y1-1: row y moves right by round(k * (y - pivot)); pixels shifted
        /// out are lost (the caller sizes the buffer so that never happens for occupied pixels). One memmove + a clear
        /// of the vacated pixels per row.</summary>
        static void ShearRows(int* buf, int w, double k, int pivot, int y0, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                int shift = (int)Math.Round(k * (y - pivot));
                if (shift == 0) continue;
                int* p = buf + (long)y * w;
                if (shift >= w || -shift >= w) { new Span<int>(p, w).Clear(); continue; }
                if (shift > 0)
                {
                    long n = (long)(w - shift) * 4;
                    Buffer.MemoryCopy(p, p + shift, n, n);                    // memmove semantics (overlap-safe)
                    new Span<int>(p, shift).Clear();
                }
                else
                {
                    long n = (long)(w + shift) * 4;
                    Buffer.MemoryCopy(p - shift, p, n, n);
                    new Span<int>(p + w + shift, -shift).Clear();
                }
            }
        }

        // Scratch buffers: the shear padding depends on the angle, so an ANIMATED rotation asks for a different size
        // every frame. Reallocating (and zero-filling) four buffers per frame cost more than the rotation itself
        // (3.2 ms vs 0.8 ms for a 256 px sprite). Each slot therefore keeps a grow-only backing store and hands out
        // a borrowed view of the requested size over it - no allocation once the store is large enough (it is sized
        // with 25 % headroom, so a spinning sprite settles after a frame or two). Callers clear what they need.
        static Sprite Scratch(int slot, int w, int h)
        {
            tShearView ??= new Sprite?[4]; tShearStore ??= new Sprite?[4];
            Sprite? store = tShearStore[slot];
            long need = (long)w * h;
            if (store == null || (long)store.meWidth * store.meHeight < need)
            {
                store?.Dispose();
                long cap = Math.Max(need + need / 4, 4096);
                store = tShearStore[slot] = new Sprite((int)Math.Min(cap, int.MaxValue), 1);
            }
            Sprite view = tShearView[slot] ??= new Sprite { meBorrowed = true };
            view.pBuf = store.pBuf; view.meWidth = w; view.meHeight = h; view.meOp = store.meOp; view.bi32BitInfo = store.bi32BitInfo;
            view.SetLockRect();
            return view;
        }
    }
}
