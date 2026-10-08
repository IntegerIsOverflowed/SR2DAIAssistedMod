using System;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    /// <summary>
    /// 32-bit BGRA software sprite / surface backed by SR2D64.dll.
    ///
    /// Performance notes (vs. the previous version):
    ///  * pixel storage is 64-byte aligned unmanaged memory (no GC pinning, no
    ///    GCHandle, no heap fragmentation, cache-line/AVX friendly rows);
    ///  * every native call passes raw pointers (blittable) -> no marshalling;
    ///  * clipping is done once in a single small struct-returning routine;
    ///  * no temporary managed arrays / pins in the load & transform paths;
    ///  * ARGB()/BitMask() are pure managed inlines (no P/Invoke transition).
    /// The public API is unchanged.
    /// </summary>
    [SuppressMessage("Style", "IDE1006:Naming rule violation", Justification = "Public API kept as-is.")]
    public unsafe partial class Sprite : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int x;
            public int y;
        }

        [DllImport("gdi32", ExactSpelling = true)]
        private static extern int SetDIBitsToDevice(HandleRef hDC, int x, int y, int Dx, int Dy, int SrcX, int SrcY, int Scan, int NumScans, int* pBits, ref BITMAPINFO BitsInfo, int wUsage);
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out int* ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern bool BitBlt(HandleRef hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern bool GdiFlush();
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern bool StretchBlt(HandleRef hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int cx1, int cy1, uint rop);
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern int StretchDIBits(HandleRef hdc, int xDest, int yDest, int DestWidth, int DestHeight, int xSrc, int ySrc, int SrcWidth, int SrcHeight, int* lpBits, ref BITMAPINFO lpbmi, uint iUsage, uint rop);
        [DllImport("gdi32", ExactSpelling = true)]
        private static extern int SetStretchBltMode(HandleRef hdc, int mode);
        private const int COLORONCOLOR = 3, HALFTONE = 4;
        private const uint SRCCOPY = 0x00CC0020;

        // ------------------------------------------------------------------ state
        private int meWidth, meHeight;
        private SR2D.Op meOp;
        private bool mePremul;                                  // pixels are premultiplied (set by Premultiply / Premultiplied)
        private int meTop, meLeft, meRight, meBottom;
        private int* pBuf;                 // 64-byte aligned, meWidth*meHeight ints
        private BITMAPINFO bi32BitInfo;

        private bool meBorrowed;           // view onto another sprite's buffer (never frees it)

        // GDI-backed surface (optional, see the (W, H, gdiSurface: true) constructor)
        private bool meGdi;
        private IntPtr hDib, hMemDC, hOldBmp;

        private const int Alignment = 64;

        // ------------------------------------------------------------ properties
        public int Width { get { return meWidth; } }
        public int Height { get { return meHeight; } }

        /// <summary>
        /// Blend op used when a draw call passes <c>DefaultOp</c>. Never returns
        /// <c>DefaultOp</c> itself: an unset op means <c>Paint</c>. (The original silently
        /// drew nothing for a sprite whose Op was never assigned - e.g. one loaded from a
        /// file without a colour key.)
        /// </summary>
        public SR2D.Op Op
        {
            get { return meOp == SR2D.Op.DefaultOp ? SR2D.Op.Paint : meOp; }
            set { if (value != SR2D.Op.DefaultOp) { meOp = value; } }
        }

        /// <summary>
        /// True when the pixels are premultiplied (rgb already scaled by alpha): after
        /// <see cref="Premultiply"/>, for sprites created with <see cref="SR2D.Op.AlphaOver"/> (the
        /// convention of the AlphaOver op) or when set explicitly. Blend modes, DrawFx and the blur
        /// use it to un-premultiply the colour before the formula and to decide how the destination
        /// alpha is composed; set it yourself when you filled a sprite with premultiplied data by hand.
        /// </summary>
        public bool Premultiplied
        {
            get { return mePremul || meOp == SR2D.Op.AlphaOver; }
            set { mePremul = value; }
        }

        // op word for the native side: blend-mode codes carry the premultiplied flags of both sides
        internal int OpWord(SR2D.Op Op, Sprite Src) => SR2D.Encode((int)Op, Src.Premultiplied, Premultiplied);
        internal int OpWord(SR2D.LineOp Op) => SR2D.Encode((int)Op, false, Premultiplied);

        /// <summary>Address of the first pixel (row-major, top-down, BGRA).</summary>
        public Int64 PTR { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return (Int64)pBuf; } }

        /// <summary>Address of pixel (x, y). No bounds check (same as before).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Int64 DataPTR(int x, int y)
        {
            return (Int64)(pBuf + x + y * meWidth);
        }

        /// <summary>
        /// True when the pixel memory is a GDI DIB section (created with
        /// <c>new Sprite(w, h, gdiSurface: true)</c>). Such a surface presents with a
        /// single BitBlt and can be drawn on with GDI/GDI+ through <see cref="Hdc"/>.
        /// </summary>
        public bool IsGdiSurface => meGdi;

        /// <summary>
        /// Memory DC with the surface selected (GDI surfaces only, IntPtr.Zero otherwise).
        /// Use it for text/GDI drawing: <c>using var g = Graphics.FromHdc(spr.Hdc);</c>
        /// Call <see cref="GdiSync"/> before SR2D touches the pixels again if you
        /// queued GDI drawing (GDI may batch).
        /// </summary>
        public IntPtr Hdc => hMemDC;

        /// <summary>Flushes pending GDI drawing on a GDI surface so the pixel buffer is current.</summary>
        public void GdiSync() { if (meGdi) GdiFlush(); }

        // ------------------------------------------------------- direct pixel access
        //
        // Three ways to touch the pixels without going through Sprite methods, from
        // safest to rawest. None of them copies anything; all of them are as fast as
        // the class's own code.
        //
        //  Pixels / Row(y)      Span<int>  - safe code, bounds-checked, JIT hoists the
        //                                    check out of simple loops -> array speed.
        //  Ptr / DataPTR(x,y)   int*/Int64 - unsafe code, no checks at all.
        //  Lock()               a using-scope that hands out the Span and (for GDI
        //                       surfaces) flushes GDI before/after.
        //
        // Layout: row-major, top-down, pitch == Width (no padding), pixel = 0xAARRGGBB
        // as an int (byte order in memory B,G,R,A). Index = x + y * Width.

        /// <summary>
        /// Direct view over the whole pixel buffer (Width*Height ints, row-major,
        /// top-down, pixel = 0xAARRGGBB). Valid until Dispose. Safe code, no copy.
        /// </summary>
        public Span<int> Pixels { get { return new Span<int>(pBuf, meWidth * meHeight); } }

        /// <summary>One row of pixels (Width ints). Throws if y is out of range.</summary>
        public Span<int> Row(int y)
        {
            if ((uint)y >= (uint)meHeight) throw new ArgumentOutOfRangeException(nameof(y));
            return new Span<int>(pBuf + (long)y * meWidth, meWidth);
        }

        /// <summary>
        /// Rectangular sub-region as a 2-D accessor (see <see cref="PixelRect"/>); the
        /// rect is clamped to the surface. Safe code.
        /// </summary>
        public PixelRect Region(int x, int y, int w, int h)
        {
            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (x + w > meWidth) w = meWidth - x;
            if (y + h > meHeight) h = meHeight - y;
            if (w < 0) w = 0; if (h < 0) h = 0;
            return new PixelRect(pBuf + (long)y * meWidth + x, w, h, meWidth);
        }

        /// <summary>Raw pointer to the first pixel (unsafe code only). Same value as <see cref="PTR"/>.</summary>
        public int* Ptr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return pBuf; } }

        /// <summary>
        /// Scoped access: <c>using (var px = spr.Lock()) { px.Span[i] = ...; }</c>.
        /// For GDI surfaces the scope flushes pending GDI drawing on entry, so the
        /// buffer is current. Cheap (a struct); mostly a readability aid.
        /// </summary>
        public PixelLock Lock()
        {
            if (meGdi) GdiFlush();
            return new PixelLock(this);
        }

        /// <summary>Handle returned by <see cref="Lock"/>.</summary>
        public readonly ref struct PixelLock
        {
            private readonly Sprite _s;
            internal PixelLock(Sprite s) { _s = s; }
            public Span<int> Span => _s.Pixels;
            public int Width => _s.meWidth;
            public int Height => _s.meHeight;
            public int* Ptr => _s.pBuf;
            public Span<int> Row(int y) => _s.Row(y);
            public void Dispose() { }
        }

        /// <summary>
        /// 2-D view over a rectangle of a sprite with a row pitch, e.g.
        /// <c>var r = spr.Region(10, 10, 64, 64); r[x, y] = c;</c> or
        /// <c>foreach row: r.Row(y)</c>. Safe code; bounds-checked per access.
        /// </summary>
        public readonly ref struct PixelRect
        {
            private readonly int* _p;
            public readonly int Width, Height, Pitch;
            internal PixelRect(int* p, int w, int h, int pitch) { _p = p; Width = w; Height = h; Pitch = pitch; }
            public ref int this[int x, int y]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new IndexOutOfRangeException();
                    return ref _p[x + (long)y * Pitch];
                }
            }
            public Span<int> Row(int y)
            {
                if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
                return new Span<int>(_p + (long)y * Pitch, Width);
            }
            public int* Ptr => _p;
        }

        // ---------------------------------------------------------------- clipping
        /// <summary>Result of clipping a rectangle against the lock rect.</summary>
        private struct Clip
        {
            public int W, H;
            public int* D;   // dst pointer at clipped origin
            public int* S;   // src pointer at clipped origin
            public int* M;   // mask pointer at clipped origin
        }

        /// <summary>Clip a source rect (L2..R2, T2..B2, pitch W2, base pS) against the lock rect.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Clip2(int L2, int R2, int T2, int B2, int W2, int* pS, out Clip c)
        {
            int L = meLeft > L2 ? meLeft : L2;
            int R = meRight < R2 ? meRight : R2;
            int T = meTop > T2 ? meTop : T2;
            int B = meBottom < B2 ? meBottom : B2;
            c.W = R - L;
            c.H = B - T;
            c.D = pBuf + T * meWidth + L;
            c.S = pS + (T - T2) * W2 + (L - L2);
            c.M = null;
            return c.W > 0 && c.H > 0;
        }

        /// <summary>Clip against the lock rect, a source rect and a mask rect.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Clip3(int L2, int R2, int T2, int B2, int L3, int R3, int T3, int B3, int W2, int W3, int* pS, int* pM, out Clip c)
        {
            int L = meLeft > L2 ? meLeft : L2; if (L < L3) L = L3;
            int R = meRight < R2 ? meRight : R2; if (R > R3) R = R3;
            int T = meTop > T2 ? meTop : T2; if (T < T3) T = T3;
            int B = meBottom < B2 ? meBottom : B2; if (B > B3) B = B3;
            c.W = R - L;
            c.H = B - T;
            c.D = pBuf + T * meWidth + L;
            c.S = pS + (T - T2) * W2 + (L - L2);
            c.M = pM + (T - T3) * W3 + (L - L3);
            return c.W > 0 && c.H > 0;
        }

        /// <summary>Clip against the whole surface (ignores the lock rect).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ClipAbs(int L2, int R2, int T2, int B2, int W2, int* pS, out Clip c)
        {
            int L = L2 < 0 ? 0 : L2;
            int R = meWidth < R2 ? meWidth : R2;
            int T = T2 < 0 ? 0 : T2;
            int B = meHeight < B2 ? meHeight : B2;
            c.W = R - L;
            c.H = B - T;
            c.D = pBuf + T * meWidth + L;
            c.S = pS + (T - T2) * W2 + (L - L2);
            c.M = null;
            return c.W > 0 && c.H > 0;
        }

        // ----------------------------------------------------------------- clears
        public void ClearBuffer(int c)
        {
            if (meRight <= meLeft || meBottom <= meTop) return;
            SR2D.Native.ClearC(pBuf + meTop * meWidth + meLeft, meRight - meLeft, meBottom - meTop, meWidth, c);
        }

        public void MaskClearBuffer(Sprite SrcMask, int MaskX, int MaskY, int c, int Mask, bool NotMask = false)
        {
            if (!Clip2(MaskX, MaskX + SrcMask.meWidth, MaskY, MaskY + SrcMask.meHeight, SrcMask.meWidth, SrcMask.pBuf, out Clip k)) return;
            SR2D.Native.MaskClearC(k.D, k.S, k.W, k.H, Mask, meWidth, SrcMask.meWidth, c, NotMask ? 1 : 0);
        }

        /// <summary>Original signature: LEFT, RIGHT, TOP, BOTTOM (Right/Bottom exclusive).
        /// For x/y/width/height use <see cref="ClearRect(Rectangle, int)"/> or <see cref="ClearRectXY"/>.</summary>
        public void ClearRect(int PLeft, int PRight, int Top, int Bottom, int c)
        {
            if (PRight <= PLeft || Bottom <= Top) return;
            if (PLeft < meLeft) PLeft = meLeft;
            if (PRight > meRight) PRight = meRight;
            if (Top < meTop) Top = meTop;
            if (Bottom > meBottom) Bottom = meBottom;
            if (PRight <= PLeft || Bottom <= Top) return;
            SR2D.Native.ClearC(pBuf + Top * meWidth + PLeft, PRight - PLeft, Bottom - Top, meWidth, c);
        }

        // ------------------------------------------------------------------ lines
        /// <summary>
        /// Line from (lx1,ly1) to (lx2,ly2). <paramref name="DotStep"/> &gt; 0 lights one pixel
        /// every DotStep+1 steps; <paramref name="IsXor"/> XORs the colour in.
        /// Default (<paramref name="PreciseDots"/> = false): the original DRAW_DOTLINE rasteriser,
        /// bit-identical to the old DLL.
        /// <paramref name="PreciseDots"/> = true: the new rasteriser (DRAW_LINE2): dots are 1 px
        /// with a gap of DotStep px measured along the line (same density in every direction,
        /// the original counts major-axis steps so diagonals look sparser), A→B lights exactly
        /// the pixels of B→A, the pattern is anchored at the start point, clipping is done in the
        /// kernel, and a fan of many lines closes without moiré gaps. About 20 % slower than the
        /// original per line. Dashes, alpha ops, float endpoints: <see cref="DrawLine2"/>.
        /// </summary>
        public void DrawLine(int lx1, int ly1, int lx2, int ly2, int c, int DotStep = 0, bool IsXor = false, bool PreciseDots = false)
        {
            if (PreciseDots)
            {
                if (meRight <= meLeft || meBottom <= meTop) return;
                // 1-pixel dots with a gap of DotStep pixels measured ALONG the line, so a fan of
                // lines has the same dot density in every direction. The kernel enumerates the
                // dashes (it does not sample the pattern per pixel), so every dot lights exactly
                // one pixel on diagonals too. DotStep = 0 -> solid.
                int step = Math.Abs(DotStep);
                SR2D.Native.DrawLine2(pBuf, meWidth, meLeft, meTop, meRight, meBottom, lx1, ly1, lx2, ly2, c,
                                      IsXor ? 2 : 1, 0, step > 0 ? 1f : 0f, step, 0f);
                return;
            }
            Point p1, p2;
            p1.x = lx1; p1.y = ly1;
            p2.x = lx2; p2.y = ly2;
            int L = meLeft, R = meRight - 1, T = meTop, B = meBottom - 1;
            if (R < L || B < T) return;

            if (p1.y > p2.y) Swap(ref p1, ref p2);
            if (p1.y > B || p2.y < T) return;
            if (p1.y != p2.y)
            {
                float k = (float)(p2.x - p1.x) / (p2.y - p1.y);
                if (p1.y < T) { p1.x = (int)(p2.x + (T - p2.y) * k); p1.y = T; }
                if (p2.y > B) { p2.x = (int)(p1.x + (B - p1.y) * k); p2.y = B; }
            }

            if (p1.x > p2.x) Swap(ref p1, ref p2);
            if (p1.x > R || p2.x < L) return;
            if (p1.x != p2.x)
            {
                float k = (float)(p2.y - p1.y) / (p2.x - p1.x);
                if (p1.x < L) { p1.y = (int)(p2.y + (L - p2.x) * k); p1.x = L; }
                if (p2.x > R) { p2.y = (int)(p1.y + (R - p1.x) * k); p2.x = R; }
            }

            // Safety net (new): the two float clips above can leave an endpoint a few
            // pixels outside the rect for very steep/flat lines (slope error after the
            // first rounding). The original passed such points straight to the
            // rasteriser, which then wrote outside the buffer. Clamping is free and
            // does not change any correctly clipped line.
            if (p1.x < L) p1.x = L; else if (p1.x > R) p1.x = R;
            if (p2.x < L) p2.x = L; else if (p2.x > R) p2.x = R;
            if (p1.y < T) p1.y = T; else if (p1.y > B) p1.y = B;
            if (p2.y < T) p2.y = T; else if (p2.y > B) p2.y = B;

            SR2D.Native.DrawDotLine(pBuf, meWidth, &p1, &p2, Math.Abs(DotStep) + 1, c, IsXor ? 1 : 0);
        }

        /// <summary>
        /// New line rasteriser (needs the new DLL). Differences from <see cref="DrawLine"/>:
        /// float endpoints, clipping inside the kernel (never writes out of bounds),
        /// A→B lights exactly the same pixels as B→A, dashes are measured along the line
        /// (so spacing looks identical in every direction), and blend ops.
        /// <paramref name="DotLen"/>/<paramref name="GapLen"/> in pixels, 0 = solid;
        /// <paramref name="Phase"/> shifts the pattern (animate for marching ants).
        /// Dashes shorter than one step still light one pixel per dash (dots never vanish on
        /// diagonals). Negative <paramref name="DotLen"/> = classic "dot step" (one pixel every
        /// -DotLen+1 steps, <paramref name="GapLen"/> ignored).
        /// </summary>
        public void DrawLine2(float x0, float y0, float x1, float y1, int c,
                              SR2D.LineOp Op = SR2D.LineOp.Set, float DotLen = 0, float GapLen = 0, float Phase = 0, int BlendFactor = 128)
        {
            if (meRight <= meLeft || meBottom <= meTop) return;
            SR2D.Native.DrawLine2(pBuf, meWidth, meLeft, meTop, meRight, meBottom, x0, y0, x1, y1, c, OpWord(Op), BlendFactor, DotLen, GapLen, Phase);
        }

        /// <summary>Polyline through <paramref name="Points"/> with <see cref="DrawLine2"/>; dash phase continues across segments.</summary>
        public void DrawPolyline2(ReadOnlySpan<PointF> Points, int c, bool Closed = false,
                                  SR2D.LineOp Op = SR2D.LineOp.Set, float DotLen = 0, float GapLen = 0, float Phase = 0, int BlendFactor = 128)
        {
            int n = Points.Length; if (n < 2 || meRight <= meLeft || meBottom <= meTop) return;
            float ph = Phase;
            int segs = Closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                PointF a = Points[i], b = Points[(i + 1) % n];
                // every joint pixel is lit once: skip the end pixel unless this is the last open segment
                // (op | 0x100), so XOR / alpha ops don't double up at the vertices of dense polylines
                int w = OpWord(Op);
                int f = (Closed || i < segs - 1) ? w | 0x100 : w;
                SR2D.Native.DrawLine2(pBuf, meWidth, meLeft, meTop, meRight, meBottom, a.X, a.Y, b.X, b.Y, c, f, BlendFactor, DotLen, GapLen, ph);
                ph -= MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Swap(ref Point p1, ref Point p2)
        {
            Point p = p1; p1 = p2; p2 = p;
        }

        // --------------------------------------------------------------- rotation
        /// <summary>
        /// Process-wide default for <see cref="DrawRotate(Sprite,int,int,int,int,float,bool,bool?)"/>:
        /// false (default) = the original DRAW_ROT / DRAW_ROT_AA kernels, bit-identical to the
        /// old DLL; true = route through DRAW_WARP, which is 1.3-2x faster (nearest and bilinear)
        /// and produces the same image up to sub-pixel sampling differences (edge pixels and
        /// the bilinear weights are not bit-identical). Per-call override: the UseWarp argument.
        /// </summary>
        public static bool RotateWithWarp { get; set; } = false;

        /// <summary>
        /// Original rotate: <paramref name="Src"/> pixel (Sx, Sy) lands on canvas pixel (Dx, Dy),
        /// rotated by <paramref name="Angle"/> radians (positive = counter-clockwise on screen,
        /// the original convention). <paramref name="AA"/> = bilinear.
        /// <paramref name="UseWarp"/>: null = <see cref="RotateWithWarp"/>, false = original
        /// kernels (bit-exact), true = same arguments through DRAW_WARP (faster).
        /// </summary>
        public void DrawRotate(Sprite Src, int Sx, int Sy, int Dx, int Dy, float Angle, bool AA = false, bool? UseWarp = null)
        {
            if (meRight <= meLeft || meBottom <= meTop) return;
            int sw = Src.meWidth, sh = Src.meHeight;
            if (UseWarp ?? RotateWithWarp)
            {
                // Same geometry as DRAW_ROT: dest pixel centre (Dx+.5, Dy+.5) maps to source
                // (Sx, Sy) with the inverse rotation; Angle is CCW in the original convention,
                // DrawRotate2's is CW, so pass -Angle. DRAW_WARP samples at pixel centres, and
                // the +0.5 dest offset makes the two rasterisations agree (measured: 0.04 % of
                // pixels differ for nearest, all at the edges; bilinear differs by <=8/255
                // except ~6 % of pixels at feature edges).
                float c = (float)Math.Cos(-Angle), s = (float)Math.Sin(-Angle);
                // DRAW_ROT_AA treats texel (i, j) as sitting at source coordinate (i, j), the
                // warp's bilinear filter at (i+.5, j+.5): shift the pivot by half a texel.
                float hp = AA ? 0.5f : 0f, px = Sx + hp, py = Sy + hp;
                float* cx = stackalloc float[4] { -px, sw - px, sw - px, -px };
                float* cy = stackalloc float[4] { -py, -py, sh - py, sh - py };
                float* q = stackalloc float[8];
                float ox = Dx + 0.5f, oy = Dy + 0.5f;
                for (int i = 0; i < 4; i++)
                {
                    q[i * 2] = ox + cx[i] * c - cy[i] * s;
                    q[i * 2 + 1] = oy + cx[i] * s + cy[i] * c;
                }
                Warp(Src, q, null, 0, SR2D.Op.Paint, AA ? SR2D.Filter.Bilinear : SR2D.Filter.Nearest, 128);
                return;
            }
            int ddx = (Dx << 16) + 0x8000;
            int ddy = (Dy << 16) + 0x8000;
            int SinA = (int)(Math.Sin(Angle) * 0x10000);
            int CosA = (int)(Math.Cos(Angle) * 0x10000);

            // bounding box of the rotated sprite (16.16)
            int L = ddx - Sx * CosA - Sy * SinA, R = L, x;
            x = ddx - (Sx - sw) * CosA - Sy * SinA; if (L > x) L = x; if (R < x) R = x;
            x = ddx - Sx * CosA - (Sy - sh) * SinA; if (L > x) L = x; if (R < x) R = x;
            x = ddx - (Sx - sw) * CosA - (Sy - sh) * SinA; if (L > x) L = x; if (R < x) R = x;

            int T = ddy - Sy * CosA + Sx * SinA, B = T, y;
            y = ddy - (Sy - sh) * CosA + Sx * SinA; if (T > y) T = y; if (B < y) B = y;
            y = ddy - Sy * CosA + (Sx - sw) * SinA; if (T > y) T = y; if (B < y) B = y;
            y = ddy - (Sy - sh) * CosA + (Sx - sw) * SinA; if (T > y) T = y; if (B < y) B = y;

            L >>= 16; if (L < meLeft) L = meLeft;
            T >>= 16; if (T < meTop) T = meTop;
            R >>= 16; if (R >= meRight) R = meRight; else R++;
            B >>= 16; if (B >= meBottom) B = meBottom; else B++;
            if (L >= R || T >= B) return;

            if (AA) SR2D.Native.DrawRotAA(Src.pBuf, pBuf, L, T, R, B, Sx, Sy, Dx, Dy, sw, sh, meWidth, SinA, CosA);
            else SR2D.Native.DrawRot(Src.pBuf, pBuf, L, T, R, B, Sx, Sy, Dx, Dy, sw, sh, meWidth, SinA, CosA);
        }

        // ------------------------------------------------------------ bump mapping
        public void DrawDPBM(Sprite Src, int Sx, int Sy, int Lx, int Ly, int Lz, float Brite = 1, bool PointLite = false)
        {
            if (!Clip2(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, Src.meWidth, Src.pBuf, out Clip k)) return;

            if (PointLite)
            {
                Lx -= meLeft < Sx ? Sx : meLeft;
                Ly -= meTop < Sy ? Sy : meTop;
                if (Brite < -2) Brite = -2; else if (Brite > 2) Brite = 2;
                SR2D.Native.DrawDPBMPoint(k.S, k.D, k.W, k.H, Src.meWidth, meWidth, Lx, Ly, Lz, (int)(Brite * 0x100000));
            }
            else
            {
                SR2D.Native.DrawDPBM(k.S, k.D, k.W, k.H, ColFromLite(Lx, Ly, Lz, Brite), Src.meWidth, meWidth);
            }
        }

        public void MaskDrawDPBM(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, int Mask, int Lx, int Ly, int Lz, float Brite = 1, bool NotMask = false, bool PointLite = false)
        {
            if (!Clip3(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, MaskX, MaskX + SrcMask.meWidth, MaskY, MaskY + SrcMask.meHeight,
                       Src.meWidth, SrcMask.meWidth, Src.pBuf, SrcMask.pBuf, out Clip k)) return;

            if (PointLite)
            {
                if (meLeft < Sx) Lx -= MaskX < Sx ? Sx : MaskX;
                else Lx -= MaskX < meLeft ? meLeft : MaskX;
                if (meTop < Sy) Ly -= MaskY < Sy ? Sy : MaskY;
                else Ly -= MaskY < meTop ? meTop : MaskY;
                if (Brite < -2) Brite = -2; else if (Brite > 2) Brite = 2;
                SR2D.Native.MaskDrawDPBMPoint(k.S, k.D, k.M, Mask, k.W, k.H, Src.meWidth, meWidth, SrcMask.meWidth, Lx, Ly, Lz, (int)(Brite * 0x100000), NotMask ? 1 : 0);
            }
            else
            {
                SR2D.Native.MaskDrawDPBM(k.S, k.D, k.M, k.W, k.H, ColFromLite(Lx, Ly, Lz, Brite), Mask, Src.meWidth, meWidth, SrcMask.meWidth, NotMask ? 1 : 0);
            }
        }

        public void DrawEBM(Sprite SRCBump, Sprite SRCCol, int Sx, int Sy, bool DestSpace = false)
        {
            if (SRCCol.meWidth <= 0 || SRCCol.meHeight <= 0 || SRCCol.pBuf == null) return;   // the colour map is not clipped: an empty / disposed one has nothing to look up
            if (!Clip2(Sx, Sx + SRCBump.meWidth, Sy, Sy + SRCBump.meHeight, SRCBump.meWidth, SRCBump.pBuf, out Clip k)) return;

            if (DestSpace)
            {
                if (Sx < 0) Sx = 0;
                if (Sy < 0) Sy = 0;
                SR2D.Native.DrawEBMEx(k.S, k.D, SRCCol.pBuf, k.W, k.H, SRCBump.meWidth, meWidth, SRCCol.meWidth, SRCCol.meHeight, Sx, Sy, meHeight);
            }
            else
            {
                SR2D.Native.DrawEBM(k.S, k.D, SRCCol.pBuf, k.W, k.H, SRCBump.meWidth, meWidth, SRCCol.meWidth, SRCCol.meHeight);
            }
        }

        // ------------------------------------------------------------------ blits
        public void Draw(Sprite Src, int Sx, int Sy, SR2D.Op Op = SR2D.Op.DefaultOp)
        {
            if (!Clip2(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, Src.meWidth, Src.pBuf, out Clip k)) return;
            if (Op == SR2D.Op.DefaultOp) Op = Src.Op;
            int ws = Src.meWidth, wd = meWidth;
            if (SR2D.IsBlendMode(Op)) { SR2D.Native.BlendMode(k.S, k.D, k.W, k.H, ws, wd, OpWord(Op, Src)); return; }
            switch (Op)
            {
                case SR2D.Op.Paint: SR2D.Native.Paint(k.S, k.D, k.W, k.H, ws, wd); break;
                case SR2D.Op.AlphaTest: SR2D.Native.AlphaTest(k.S, k.D, k.W, k.H, ws, wd); break;
                case SR2D.Op.AlphaBlend: SR2D.Native.AlphaBlend(k.S, k.D, k.W, k.H, ws, wd); break;
                case SR2D.Op.Add2D: SR2D.Native.Add2D(k.S, k.D, k.W, k.H, ws, wd); break;
                case SR2D.Op.Add: SR2D.Native.Add(k.S, k.D, k.W, k.H, ws, wd); break;
                case SR2D.Op.Mul2X: SR2D.Native.Mul2X(k.S, k.D, k.W, k.H, ws, wd); break;
                case SR2D.Op.Mul: SR2D.Native.Mul(k.S, k.D, k.W, k.H, ws, wd); break;
                case SR2D.Op.Max: SR2D.Native.Max(k.S, k.D, k.W, k.H, ws, wd); break;
                case SR2D.Op.Min: SR2D.Native.Min(k.S, k.D, k.W, k.H, ws, wd); break;
                case SR2D.Op.Blend: SR2D.Native.Blend(k.S, k.D, k.W, k.H, ws, wd, 128); break;   // 50 % - use Blend() for a factor
                case SR2D.Op.AlphaOver: SR2D.Native.AlphaOver(k.S, k.D, k.W, k.H, ws, wd); break;
                default: SR2D.Native.Paint(k.S, k.D, k.W, k.H, ws, wd); break;
            }
        }

        public void MaskDraw(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, int Mask, bool NotMask = false, SR2D.Op Op = SR2D.Op.DefaultOp)
        {
            if (!Clip3(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, MaskX, MaskX + SrcMask.meWidth, MaskY, MaskY + SrcMask.meHeight,
                       Src.meWidth, SrcMask.meWidth, Src.pBuf, SrcMask.pBuf, out Clip k)) return;
            if (Op == SR2D.Op.DefaultOp) Op = Src.Op;
            int ws = Src.meWidth, wd = meWidth, wm = SrcMask.meWidth, nm = NotMask ? 1 : 0;
            if (SR2D.IsBlendMode(Op)) { SR2D.Native.MaskBlendMode(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm, OpWord(Op, Src)); return; }
            switch (Op)
            {
                case SR2D.Op.Paint: SR2D.Native.MaskPaint(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                case SR2D.Op.Mul2X: SR2D.Native.MaskMul2X(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                case SR2D.Op.Add2D: SR2D.Native.MaskAdd2D(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                case SR2D.Op.Add: SR2D.Native.MaskAdd(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                case SR2D.Op.Mul: SR2D.Native.MaskMul(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                case SR2D.Op.AlphaTest: SR2D.Native.MaskAlphaTest(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                case SR2D.Op.AlphaBlend: SR2D.Native.MaskAlphaBlend(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                case SR2D.Op.Max: SR2D.Native.MaskMax(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                case SR2D.Op.Min: SR2D.Native.MaskMin(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                case SR2D.Op.Blend: SR2D.Native.MaskBlend(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm, 128); break;
                case SR2D.Op.AlphaOver: SR2D.Native.MaskAlphaOver(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
                default: SR2D.Native.MaskPaint(k.S, k.D, k.M, k.W, k.H, Mask, ws, wd, wm, nm); break;
            }
        }

        /// <summary>Descriptive alias of <see cref="Blend(Sprite, int, int, int)"/>: a straight crossfade of <paramref name="Src"/> over
        /// this sprite by <paramref name="BlendFactor"/> (0..256). Exists so call sites never read like the unrelated
        /// <see cref="SR2D.Op.Blend"/> draw mode - "Crossfade" says what actually happens.</summary>
        public void Crossfade(Sprite Src, int Sx, int Sy, int BlendFactor) => Blend(Src, Sx, Sy, BlendFactor);
        public void Blend(Sprite Src, int Sx, int Sy, int BlendFactor)
        {
            if (!Clip2(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, Src.meWidth, Src.pBuf, out Clip k)) return;
            SR2D.Native.Blend(k.S, k.D, k.W, k.H, Src.meWidth, meWidth, BlendFactor);
        }

        public void MaskBlend(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, int BlendFactor, int Mask, bool NotMask = false)
        {
            if (!Clip3(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, MaskX, MaskX + SrcMask.meWidth, MaskY, MaskY + SrcMask.meHeight,
                       Src.meWidth, SrcMask.meWidth, Src.pBuf, SrcMask.pBuf, out Clip k)) return;
            SR2D.Native.MaskBlend(k.S, k.D, k.M, k.W, k.H, Mask, Src.meWidth, meWidth, SrcMask.meWidth, NotMask ? 1 : 0, BlendFactor);
        }

        public int MaskInterSector(Sprite Src, int Sx, int Sy, int Mask)
        {
            if (!ClipAbs(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, Src.meWidth, Src.pBuf, out Clip k)) return 0;
            return SR2D.Native.MaskIS(k.S, k.D, k.W, k.H, Src.meWidth, meWidth, Mask);
        }

        /// <summary>
        /// Per-channel multiply and add: <c>byte_out = clamp(((byte_src * mul_byte) &gt;&gt; 7) + add_byte * 2 - 256)</c>,
        /// applied to each of the four bytes of every pixel. <paramref name="Mul"/> and <paramref name="Add"/> are therefore
        /// PACKED VECTORS, one byte per channel (alpha, red, green, blue) - build them with
        /// <see cref="SR2D.ARGB(int,int,int,int)"/>, not as plain numbers. Passing <c>96</c> means "x0.75 on blue, x0 on
        /// green / red / ALPHA", which is normally not what was meant. Neutral: Mul = 128 per byte (x1.00), Add = 128 per
        /// byte (+0). An Add byte of 0 is -256, i.e. black / transparent. The alpha byte is transformed like the others, so
        /// a non-neutral alpha multiplier fades the sprite out (or in) as it does the colours.
        /// </summary>
        public void MulAddS2X(Sprite Src, int Sx, int Sy, int Mul, int Add)
        {
            if (!Clip2(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, Src.meWidth, Src.pBuf, out Clip k)) return;
            SR2D.Native.MulAdd(k.S, k.D, k.W, k.H, Src.meWidth, meWidth, Mul, Add);
        }

        public void MaskMulAddS2X(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, int Mul, int Add, int Mask, bool NotMask = false)
        {
            if (!Clip3(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, MaskX, MaskX + SrcMask.meWidth, MaskY, MaskY + SrcMask.meHeight,
                       Src.meWidth, SrcMask.meWidth, Src.pBuf, SrcMask.pBuf, out Clip k)) return;
            SR2D.Native.MaskMulAdd(k.S, k.D, k.M, k.W, k.H, Mask, Src.meWidth, meWidth, SrcMask.meWidth, NotMask ? 1 : 0, Mul, Add);
        }

        public void MoveByte(Sprite Src, int Sx, int Sy, SR2D.ColChannel chSrc, SR2D.ColChannel chDest)
        {
            if (!Clip2(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, Src.meWidth, Src.pBuf, out Clip k)) return;
            SR2D.Native.MoveByte(k.S, k.D, k.W, k.H, Src.meWidth, meWidth, (int)chSrc, (int)chDest);
        }

        public void MoveBit(Sprite Src, int Sx, int Sy, int mbSrc, int mbDest)
        {
            if (!Clip2(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, Src.meWidth, Src.pBuf, out Clip k)) return;
            SR2D.Native.MoveBit(k.S, k.D, k.W, k.H, Src.meWidth, meWidth, mbSrc, mbDest);
        }

        public void MaskMoveByte(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, SR2D.ColChannel chSrc, SR2D.ColChannel chDest, int Mask, bool NotMask = false)
        {
            if (!Clip3(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, MaskX, MaskX + SrcMask.meWidth, MaskY, MaskY + SrcMask.meHeight,
                       Src.meWidth, SrcMask.meWidth, Src.pBuf, SrcMask.pBuf, out Clip k)) return;
            SR2D.Native.MaskMoveByte(k.S, k.D, k.M, k.W, k.H, Mask, Src.meWidth, meWidth, SrcMask.meWidth, NotMask ? 1 : 0, (int)chSrc, (int)chDest);
        }

        public void MaskMoveBit(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, int mbSrc, int mbDest, int Mask, bool NotMask = false)
        {
            if (!Clip3(Sx, Sx + Src.meWidth, Sy, Sy + Src.meHeight, MaskX, MaskX + SrcMask.meWidth, MaskY, MaskY + SrcMask.meHeight,
                       Src.meWidth, SrcMask.meWidth, Src.pBuf, SrcMask.pBuf, out Clip k)) return;
            SR2D.Native.MaskMoveBit(k.S, k.D, k.M, k.W, k.H, Mask, Src.meWidth, meWidth, SrcMask.meWidth, NotMask ? 1 : 0, mbSrc, mbDest);
        }

        // ---------------------------------------------------------- free transform
        //
        // All of these go through one native kernel (DRAW_WARP). Cost model on a 512x512
        // source, AVX2, per call:
        //   DrawScaled   nearest  ~0.5 ms / Mpixel   (separable fast path)
        //   DrawScaled   bilinear ~1.5 ms / Mpixel
        //   DrawRotate2 / DrawQuad affine, nearest   ~1.4 ms / Mpixel
        //   DrawRotate2 / DrawQuad affine, bilinear  ~5 ms / Mpixel
        //   DrawQuad perspective, bilinear           ~7 ms / Mpixel  (per-pixel divide)
        // (Mpixel = destination pixels actually covered.)  A polygon clip adds only a
        // per-scanline cost, so it is essentially free.

        /// <summary>
        /// Draws <paramref name="Src"/> scaled to DestWidth x DestHeight at (DestX, DestY),
        /// respecting the lock rect. Negative sizes flip the sprite.
        /// </summary>
        public void DrawScaled(Sprite Src, int DestX, int DestY, int DestWidth, int DestHeight,
                               SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Nearest, int BlendFactor = 128)
        {
            float x0 = DestX, y0 = DestY, x1 = DestX + DestWidth, y1 = DestY + DestHeight;
            float* q = stackalloc float[8] { x0, y0, x1, y0, x1, y1, x0, y1 };
            Warp(Src, q, null, 0, Op, Filter, BlendFactor);
        }

        /// <summary>
        /// Draws <paramref name="Src"/> uniformly scaled by <paramref name="Scale"/>
        /// (1 = 1:1, 2 = double size, 0.5 = half; negative flips both axes) so that the
        /// source point (PivotX, PivotY) lands on (DestX, DestY). The pivot is in
        /// source pixel units; -1 (default) means the sprite centre. Sub-pixel positions
        /// are honoured (visible with Filter = Bilinear).
        /// <para>Overload note: with an integer 4th AND 5th argument the compiler picks the
        /// (DestWidth, DestHeight) overload — write <c>2f</c> or <c>Scale: 2</c> for a scale.</para>
        /// </summary>
        public void DrawScaled(Sprite Src, float DestX, float DestY, float Scale, float PivotX = -1, float PivotY = -1,
                               SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Nearest, int BlendFactor = 128)
            => DrawScaled(Src, DestX, DestY, Scale, Scale, PivotX, PivotY, Op, Filter, BlendFactor);

        /// <summary>
        /// Same as the uniform overload but with independent horizontal / vertical scale
        /// factors (e.g. ScaleX = -1, ScaleY = 1 mirrors the sprite around its pivot).
        /// </summary>
        public void DrawScaled(Sprite Src, float DestX, float DestY, float ScaleX, float ScaleY, float PivotX, float PivotY,
                               SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Nearest, int BlendFactor = 128)
        {
            if (Src.meWidth <= 0 || Src.meHeight <= 0) return;
            if (PivotX < 0) PivotX = Src.meWidth * 0.5f;
            if (PivotY < 0) PivotY = Src.meHeight * 0.5f;
            // destination-space extents of the sprite relative to the pivot
            float x0 = DestX - PivotX * ScaleX, y0 = DestY - PivotY * ScaleY;
            float x1 = x0 + Src.meWidth * ScaleX, y1 = y0 + Src.meHeight * ScaleY;
            float* q = stackalloc float[8] { x0, y0, x1, y0, x1, y1, x0, y1 };
            Warp(Src, q, null, 0, Op, Filter, BlendFactor);
        }

        /// <summary>
        /// Draws <paramref name="Src"/> scaled to DestWidth x DestHeight and rotated by
        /// <paramref name="Angle"/> (radians, clockwise on screen) around its pivot
        /// (PivotX, PivotY) in *source* pixel units; the pivot lands on (DestX, DestY).
        /// Pass PivotX/PivotY = -1 (default) to rotate around the sprite centre.
        /// (The "2" follows <see cref="DrawLine2"/>: the new-API counterpart of the original <see cref="DrawRotate"/>.
        /// For a rotation that keeps every source pixel see <see cref="DrawRotateShear"/>.)
        /// </summary>
        public void DrawRotate2(Sprite Src, int DestX, int DestY, float Angle,
                                int DestWidth = 0, int DestHeight = 0, float PivotX = -1, float PivotY = -1,
                                SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Nearest, int BlendFactor = 128)
        {
            if (DestWidth == 0) DestWidth = Src.meWidth;
            if (DestHeight == 0) DestHeight = Src.meHeight;
            if (PivotX < 0) PivotX = Src.meWidth * 0.5f;
            if (PivotY < 0) PivotY = Src.meHeight * 0.5f;
            float sx = (float)DestWidth / Src.meWidth, sy = (float)DestHeight / Src.meHeight;
            float px = PivotX * sx, py = PivotY * sy;              // pivot in destination units
            float c = (float)Math.Cos(Angle), s = (float)Math.Sin(Angle);
            float* q = stackalloc float[8];
            // corners relative to the pivot: TL, TR, BR, BL
            float* cx = stackalloc float[4] { -px, DestWidth - px, DestWidth - px, -px };
            float* cy = stackalloc float[4] { -py, -py, DestHeight - py, DestHeight - py };
            for (int i = 0; i < 4; i++)
            {
                q[i * 2] = DestX + cx[i] * c - cy[i] * s;
                q[i * 2 + 1] = DestY + cx[i] * s + cy[i] * c;
            }
            Warp(Src, q, null, 0, Op, Filter, BlendFactor);
        }

        /// <summary>Old name of <see cref="DrawRotate2"/> (kept so existing callers compile).</summary>
        [Obsolete("Renamed to DrawRotate2 (consistent with DrawLine2).")]
        public void DrawRotated(Sprite Src, int DestX, int DestY, float Angle,
                                int DestWidth = 0, int DestHeight = 0, float PivotX = -1, float PivotY = -1,
                                SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Nearest, int BlendFactor = 128)
            => DrawRotate2(Src, DestX, DestY, Angle, DestWidth, DestHeight, PivotX, PivotY, Op, Filter, BlendFactor);

        /// <summary>
        /// Maps the sprite onto an arbitrary quadrilateral. <paramref name="Quad"/> holds
        /// the four destination corners for the sprite's TL, TR, BR, BL corners, in that
        /// order. A parallelogram gives an affine warp; anything else a perspective warp.
        /// Optional <paramref name="ClipPolygon"/> (any simple polygon, even-odd rule)
        /// limits the drawn area.
        /// </summary>
        public void DrawQuad(Sprite Src, ReadOnlySpan<PointF> Quad, ReadOnlySpan<PointF> ClipPolygon = default,
                             SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Nearest, int BlendFactor = 128)
        {
            if (Quad.Length != 4) throw new ArgumentException("Quad must contain exactly 4 points.", nameof(Quad));
            float* q = stackalloc float[8];
            for (int i = 0; i < 4; i++) { q[i * 2] = Quad[i].X; q[i * 2 + 1] = Quad[i].Y; }
            fixed (PointF* pp = ClipPolygon)
            {
                // PointF is two consecutive floats -> reinterpret directly, no copy
                Warp(Src, q, ClipPolygon.Length >= 3 ? (float*)pp : null, ClipPolygon.Length, Op, Filter, BlendFactor);
            }
        }

        /// <summary>
        /// Fits the sprite into a polygon: the sprite is scaled to the polygon's bounding
        /// box and clipped to the polygon outline. For a 4-point polygon that should be
        /// *warped* onto the corners (not clipped), use <see cref="DrawQuad"/> instead.
        /// </summary>
        public void DrawInPolygon(Sprite Src, ReadOnlySpan<PointF> Polygon,
                                  SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Nearest, int BlendFactor = 128)
        {
            if (Polygon.Length < 3) return;
            float l = Polygon[0].X, r = l, t = Polygon[0].Y, b = t;
            for (int i = 1; i < Polygon.Length; i++)
            {
                if (Polygon[i].X < l) l = Polygon[i].X; if (Polygon[i].X > r) r = Polygon[i].X;
                if (Polygon[i].Y < t) t = Polygon[i].Y; if (Polygon[i].Y > b) b = Polygon[i].Y;
            }
            float* q = stackalloc float[8] { l, t, r, t, r, b, l, b };
            fixed (PointF* pp = Polygon)
            {
                Warp(Src, q, (float*)pp, Polygon.Length, Op, Filter, BlendFactor);
            }
        }

        private void Warp(Sprite Src, float* quad, float* poly, int npoly, SR2D.Op Op, SR2D.Filter Filter, int BlendFactor)
        {
            if (meRight <= meLeft || meBottom <= meTop || Src.meWidth <= 0 || Src.meHeight <= 0) return;
            if (Op == SR2D.Op.DefaultOp) Op = Src.Op;
            Filter = SR2D.Resolve(Filter, quad, Src.meWidth, Src.meHeight);
            SR2D.Native.DrawWarp(Src.pBuf, Src.meWidth, Src.meHeight, pBuf, meWidth,
                                 meLeft, meTop, meRight, meBottom, quad, poly, npoly, OpWord(Op, Src), (int)Filter, BlendFactor);
        }

        public void TileDraw(Sprite Src, int Dx, int Dy, int W, int H, int Sx = 0, int Sy = 0, SR2D.Op Op = SR2D.Op.DefaultOp)
        {
            if (Src.meWidth <= 0 || Src.meHeight <= 0) return;      // nothing to tile (also an empty / disposed source)
            // Temporarily shrink the lock rect to the tile area, then blit the grid.
            int meL = meLeft, meR = meRight, meT = meTop, meB = meBottom;
            try
            {
                if (Dx > meLeft) { if (Dx < meRight) meLeft = Dx; else return; }
                if (Dy > meTop) { if (Dy < meBottom) meTop = Dy; else return; }
                if (Dx + W <= meLeft) return; else if (Dx + W < meRight) meRight = Dx + W;
                if (Dy + H <= meTop) return; else if (Dy + H < meBottom) meBottom = Dy + H;

                int sw = Src.meWidth, sh = Src.meHeight;
                Sx -= (int)Math.Floor((double)(Sx + sw) / sw) * sw;
                Sy -= (int)Math.Floor((double)(Sy + sh) / sh) * sh;

                // first tile that can touch the visible area (skips fully clipped tiles)
                int x0 = Dx + Sx, y0 = Dy + Sy;
                if (x0 + sw <= meLeft) x0 += ((meLeft - x0) / sw) * sw;
                if (y0 + sh <= meTop) y0 += ((meTop - y0) / sh) * sh;

                for (int y = y0; y < meBottom; y += sh)
                    for (int x = x0; x < meRight; x += sw)
                        Draw(Src, x, y, Op);
            }
            finally
            {
                meLeft = meL; meRight = meR; meTop = meT; meBottom = meB;
            }
        }

        // ----------------------------------------------------------- allocation
        private void Init(int W = 0, int H = 0, SR2D.Op Op = SR2D.Op.DefaultOp)
        {
            if (Op != SR2D.Op.DefaultOp) meOp = Op;
            if (W <= 0 & H <= 0) return;
            if (W == meWidth & H == meHeight) return;
            if (W > 0) meWidth = W;
            if (H > 0) meHeight = H;
            SetLockRect(0, meWidth, 0, meHeight);

            FreeBuffer();
            bi32BitInfo.bmiHeader.biBitCount = 32;
            bi32BitInfo.bmiHeader.biPlanes = 1;
            bi32BitInfo.bmiHeader.biSize = 40;
            bi32BitInfo.bmiHeader.biWidth = meWidth;
            bi32BitInfo.bmiHeader.biHeight = -meHeight;   // top-down, same layout as our buffers
            bi32BitInfo.bmiHeader.biSizeImage = (meWidth * meHeight) << 2;

            nuint bytes = (nuint)((long)meWidth * meHeight * 4);
            if (meGdi)
            {
                // GDI owns the pixel memory: presenting is then a plain BitBlt (no
                // per-frame format conversion / copy inside SetDIBitsToDevice).
                try { hDib = CreateDIBSection(IntPtr.Zero, ref bi32BitInfo, 0 /*DIB_RGB_COLORS*/, out pBuf, IntPtr.Zero, 0); }
                catch (DllNotFoundException) { hDib = IntPtr.Zero; pBuf = null; }   // no GDI on this platform (e.g. a
                catch (EntryPointNotFoundException) { hDib = IntPtr.Zero; pBuf = null; }   // Linux test runner): plain buffer
                if (hDib == IntPtr.Zero || pBuf == null)
                {
                    meGdi = false;                        // fall back silently to a plain buffer
                }
                else
                {
                    hMemDC = CreateCompatibleDC(IntPtr.Zero);
                    hOldBmp = SelectObject(hMemDC, hDib);
                    // DIB sections are zero-initialised by the system (a 32-bit DIB row
                    // is always 4-byte aligned, so pitch == meWidth*4, exactly like ours).
                    return;
                }
            }
            pBuf = (int*)NativeMemory.AlignedAlloc(bytes == 0 ? (nuint)Alignment : bytes, Alignment);
            NativeMemory.Clear(pBuf, bytes);          // same "all zero" start state as new int[]
        }

        /// <summary>Turns every live view of this sprite's buffer into an empty 0x0 surface, so a view that
        /// outlived its owner becomes a no-op instead of drawing into freed memory.</summary>
        void PoisonViews()
        {
            if (meViews == null) return;
            lock (meViews)
            {
                foreach (var v in meViews)
                {
                    v.pBuf = null;                                 // the buffer dies with the owner
                    v.meWidth = v.meHeight = 0;                    // the verbs' meRight <= meLeft guards make the view a no-op
                    v.meLeft = v.meRight = v.meTop = v.meBottom = 0;
                    v.meOwner = null;
                    v.PoisonViews();                               // views of views die too
                }
                meViews.Clear();
            }
        }

        private void FreeBuffer()
        {
            if (!meBorrowed) PoisonViews();                        // first: nothing may keep a raw pointer to the memory being freed
            if (meBorrowed) { pBuf = null; return; }
            if (hDib != IntPtr.Zero)
            {
                if (hMemDC != IntPtr.Zero) { SelectObject(hMemDC, hOldBmp); DeleteDC(hMemDC); hMemDC = IntPtr.Zero; }
                DeleteObject(hDib); hDib = IntPtr.Zero;
                pBuf = null;                             // memory belonged to the DIB
                return;
            }
            if (pBuf != null)
            {
                NativeMemory.AlignedFree(pBuf);
                pBuf = null;
            }
        }

        // ---------------------------------------------------------------- loading
        private void LoadFromBitmap(Bitmap Bmp, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            if (W < 1) W = Bmp.Width;
            if (H < 1) H = Bmp.Height;

            Bitmap BmpDest = Bmp;
            bool owned = false;
            if (Bmp.PixelFormat != PixelFormat.Format32bppArgb)
            {
                BmpDest = new Bitmap(Bmp.Width, Bmp.Height, PixelFormat.Format32bppArgb);
                using (Graphics G = Graphics.FromImage(BmpDest))
                    G.DrawImage(Bmp, 0, 0, Bmp.Width, Bmp.Height);
                owned = true;
            }

            try
            {
                if (Trans == SR2D.Transform.None && W == Bmp.Width && H == Bmp.Height)
                {
                    Init(W, H);
                    CopyFromBitmap(BmpDest);
                }
                else
                {
                    using Sprite Spr = new Sprite(BmpDest);
                    LoadFromSprite(Spr, Trans, W, H, ColorKey);
                }
            }
            finally
            {
                if (owned) BmpDest.Dispose();
            }

            if (ColorKey >= 0)
            {
                AddColorKey(ColorKey);
                Op = SR2D.Op.AlphaTest;
            }
        }

        /// <summary>Copies a 32bppArgb bitmap of exactly meWidth x meHeight into the buffer.</summary>
        private void CopyFromBitmap(Bitmap bmp)
        {
            BitmapData BMD = bmp.LockBits(new Rectangle(0, 0, meWidth, meHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                long rowBytes = (long)meWidth * 4;
                if (BMD.Stride == rowBytes)
                {
                    Buffer.MemoryCopy((void*)BMD.Scan0, pBuf, rowBytes * meHeight, rowBytes * meHeight);
                }
                else
                {
                    byte* s = (byte*)BMD.Scan0;
                    byte* d = (byte*)pBuf;
                    for (int y = 0; y < meHeight; y++, s += BMD.Stride, d += rowBytes)
                        Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(BMD);
            }
        }

        private void LoadFromSprite(Sprite Src, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            bool fResize = false;
            if (W <= 0) W = Src.meWidth; else if (W != Src.meWidth) fResize = true;
            if (H <= 0) H = Src.meHeight; else if (H != Src.meHeight) fResize = true;

            if (fResize)
            {
                Init(W, H, Src.Op);
                if (Trans == SR2D.Transform.None)
                {
                    SR2D.Native.Resize(Src.pBuf, pBuf, Src.meWidth, Src.meHeight, meWidth, meHeight);
                }
                else if (W * H > Src.meWidth * Src.meHeight)
                {
                    // transform at source size, then resize (less work on the big image)
                    int n = Src.meWidth * Src.meHeight;
                    int* tmp = (int*)NativeMemory.AlignedAlloc((nuint)((long)n * 4), Alignment);
                    try
                    {
                        W = Src.meWidth; H = Src.meHeight;
                        Transform(Src.pBuf, tmp, W, H, Trans);
                        if (Trans > SR2D.Transform.FlipXY) { int D = W; W = H; H = D; }
                        SR2D.Native.Resize(tmp, pBuf, W, H, meWidth, meHeight);
                    }
                    finally { NativeMemory.AlignedFree(tmp); }
                }
                else
                {
                    // resize first, then transform (less work on the small image)
                    int n = W * H;
                    int* tmp = (int*)NativeMemory.AlignedAlloc((nuint)((long)n * 4), Alignment);
                    try
                    {
                        if (Trans > SR2D.Transform.FlipXY) { int D = W; W = H; H = D; }
                        SR2D.Native.Resize(Src.pBuf, tmp, Src.meWidth, Src.meHeight, W, H);
                        Transform(tmp, pBuf, W, H, Trans);
                    }
                    finally { NativeMemory.AlignedFree(tmp); }
                }
            }
            else
            {
                if (Trans > SR2D.Transform.FlipXY) { int D = W; W = H; H = D; }
                Init(W, H, Src.Op);
                Transform(Src.pBuf, pBuf, W, H, Trans);
            }

            if (ColorKey >= 0)
            {
                AddColorKey(ColorKey);
                Op = SR2D.Op.AlphaTest;
            }
        }

        public void LoadFromFile(string FileName, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            // managed decoders first (cs/ImageCodec.cs: PNG, WebP, JPEG, BMP, GIF, TGA - no GDI+, same pixels, works headless);
            // GDI+ only for what is left (TIFF, ICO, EMF/WMF, ...)
            if (ImageCodec.SniffFile(FileName) != ImageKind.Unknown) { LoadFromImage(File.ReadAllBytes(FileName), Trans, W, H, ColorKey); return; }
            using Bitmap Bmp = (Bitmap)Image.FromFile(FileName);
            LoadFromBitmap(Bmp, Trans, W, H, ColorKey);
        }

        // ------------------------------------------------------------------ any managed format (cs/ImageCodec.cs)
        /// <summary>Loads a PNG / WebP / JPEG / BMP / GIF / TGA picture from memory (format by signature); the same Transform / W / H / ColorKey options as the other loaders.</summary>
        public static Sprite FromImage(byte[] Data, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            var s = new Sprite(0, 0); s.LoadFromImage(Data, Trans, W, H, ColorKey); return s;
        }
        public static Sprite FromImage(Stream Data, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            using var ms = new MemoryStream(); Data.CopyTo(ms); return FromImage(ms.ToArray(), Trans, W, H, ColorKey);
        }
        /// <summary>Replaces the contents with a decoded picture of any managed format (straight alpha; Op becomes AlphaBlend when the picture has alpha, else Paint).</summary>
        public void LoadFromImage(byte[] Data, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            var (pw, ph, px, alpha) = ImageCodec.Decode(Data);
            AdoptPixels(pw, ph, px, alpha, Trans, W, H, ColorKey);
        }
        private void AdoptPixels(int pw, int ph, int[] px, bool alpha, SR2D.Transform Trans, int W, int H, int ColorKey)
        {
            if (Trans == SR2D.Transform.None && (W <= 0 || W == pw) && (H <= 0 || H == ph))
            {
                Init(pw, ph, alpha ? SR2D.Op.AlphaBlend : SR2D.Op.Paint);
                px.AsSpan().CopyTo(Pixels);
            }
            else
            {
                using var src = new Sprite(pw, ph, alpha ? SR2D.Op.AlphaBlend : SR2D.Op.Paint);
                px.AsSpan().CopyTo(src.Pixels);
                LoadFromSprite(src, Trans, W, H, -1);
            }
            if (ColorKey >= 0) { AddColorKey(ColorKey); Op = SR2D.Op.AlphaTest; }
        }

        // ------------------------------------------------------------------ WebP (cs/WebP.cs, managed decoder)
        /// <summary>Loads a WebP picture (lossy, lossless, alpha) from a file; the same Transform / W / H / ColorKey options as the other loaders.</summary>
        public static Sprite FromWebP(string FileName, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
            => FromWebP(File.ReadAllBytes(FileName), Trans, W, H, ColorKey);
        /// <summary>Loads a WebP picture from memory (embedded resource, download, ...).</summary>
        public static Sprite FromWebP(byte[] Data, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            var s = new Sprite(0, 0); s.LoadFromWebP(Data, Trans, W, H, ColorKey); return s;
        }
        public static Sprite FromWebP(Stream Data, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            using var ms = new MemoryStream(); Data.CopyTo(ms); return FromWebP(ms.ToArray(), Trans, W, H, ColorKey);
        }
        /// <summary>Replaces the contents with a decoded WebP picture (straight alpha; Op becomes AlphaBlend when the picture has alpha, else Paint).</summary>
        public void LoadFromWebP(byte[] Data, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            var (pw, ph, px) = WebP.Decode(Data);
            bool alpha = WebP.GetInfo(Data)?.HasAlpha ?? false;
            AdoptPixels(pw, ph, px, alpha, Trans, W, H, ColorKey);
        }

        public void SaveToFile(string FileName, ImageFormat Format, bool WithAlpha = false)
        {
            if (Format.Guid == ImageFormat.Png.Guid) { SavePng(FileName, WithAlpha ? PngColor.Rgba : PngColor.Rgb); return; }   // managed encoder, no GDI+
            PixelFormat pf = WithAlpha ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppRgb;
            using Bitmap Bmp = new Bitmap(meWidth, meHeight, meWidth << 2, pf, (nint)pBuf);
            Bmp.Save(FileName, Format);
        }

        // ------------------------------------------------------------------ PNG (cs/Png.cs, managed codec)
        /// <summary>Loads a PNG from a file (every colour type / bit depth / interlace); the same Transform / W / H / ColorKey options as the other loaders.</summary>
        public static Sprite FromPng(string FileName, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
            => FromPng(File.ReadAllBytes(FileName), Trans, W, H, ColorKey);
        /// <summary>Loads a PNG from memory.</summary>
        public static Sprite FromPng(byte[] Data, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            var s = new Sprite(0, 0); s.LoadFromPng(Data, Trans, W, H, ColorKey); return s;
        }
        public static Sprite FromPng(Stream Data, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            using var ms = new MemoryStream(); Data.CopyTo(ms); return FromPng(ms.ToArray(), Trans, W, H, ColorKey);
        }
        /// <summary>Replaces the contents with a decoded PNG (straight alpha; Op becomes AlphaBlend when the file has alpha / transparency, else Paint).</summary>
        public void LoadFromPng(byte[] Data, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            var (pw, ph, px) = Png.Decode(Data);
            var info = Png.GetInfo(Data);
            bool alpha = info != null && (info.HasAlphaChannel || info.ColorType == PngColorType.Palette);
            if (!alpha) foreach (int c in px) if ((c >>> 24) != 255) { alpha = true; break; }   // tRNS colour key
            AdoptPixels(pw, ph, px, alpha, Trans, W, H, ColorKey);
        }
        /// <summary>
        /// Encodes the sprite (or its lock rectangle, see <paramref name="LockRectOnly"/>) as a PNG in memory. <see cref="PngColor.Auto"/>
        /// picks the smallest lossless form (palette / grey / RGB, RGBA only when there is real transparency); pass
        /// <see cref="PngColor.Rgba"/> to always keep the alpha channel or <see cref="PngColor.Rgb"/> to drop it. A premultiplied
        /// sprite is un-premultiplied for the file (PNG stores straight alpha); nothing in memory changes.
        /// </summary>
        public byte[] ToPng(PngColor Color = PngColor.Auto, int Level = 2, bool LockRectOnly = false)
        {
            int l = LockRectOnly ? meLeft : 0, t = LockRectOnly ? meTop : 0, w = LockRectOnly ? meRight - meLeft : meWidth, h = LockRectOnly ? meBottom - meTop : meHeight;
            if (w <= 0 || h <= 0) return Array.Empty<byte>();
            if (!Premultiplied) return Png.Encode(new ReadOnlySpan<int>(pBuf + (long)t * meWidth + l, w + (h - 1) * meWidth), w, h, meWidth, Color, Level);
            using var tmp = new Sprite(w, h, SR2D.Op.AlphaOver);
            SR2D.Native.Paint(pBuf + (long)t * meWidth + l, tmp.pBuf, w, h, meWidth, w);
            tmp.Premultiplied = true;
            tmp.Unpremultiply();
            return Png.Encode(tmp.Pixels, w, h, w, Color, Level);
        }
        /// <summary>Writes the sprite as a PNG file (see <see cref="ToPng"/>).</summary>
        public void SavePng(string FileName, PngColor Color = PngColor.Auto, int Level = 2, bool LockRectOnly = false) => File.WriteAllBytes(FileName, ToPng(Color, Level, LockRectOnly));

        private static void Transform(int* pSrc, int* pDest, int W, int H, SR2D.Transform Trans)
        {
            switch (Trans)
            {
                case SR2D.Transform.FlipX: SR2D.Native.FlipX(pSrc, pDest, W, H); break;
                case SR2D.Transform.FlipY: SR2D.Native.FlipY(pSrc, pDest, W, H); break;
                case SR2D.Transform.FlipXY: SR2D.Native.FlipXY(pSrc, pDest, W, H); break;
                case SR2D.Transform.RotCCW: SR2D.Native.RotCCW(pSrc, pDest, W, H); break;
                case SR2D.Transform.RotCW: SR2D.Native.RotCW(pSrc, pDest, W, H); break;
                case SR2D.Transform.FlipXRotCCW: SR2D.Native.FlipXRotCCW(pSrc, pDest, W, H); break;
                case SR2D.Transform.FlipYRotCCW: SR2D.Native.FlipYRotCCW(pSrc, pDest, W, H); break;
                default:
                    long bytes = (long)W * H * 4;
                    Buffer.MemoryCopy(pSrc, pDest, bytes, bytes);
                    break;
            }
        }

        // -------------------------------------------------------------- lock rect
        /// <summary>Original signature: LEFT, RIGHT, TOP, BOTTOM; out-of-range values fall back to
        /// the surface edge, no arguments = whole surface. Rectangle overload in Sprite.Rect.cs.</summary>
        public void SetLockRect(int LRLeft = -1, int LRRight = -1, int LRTop = -1, int LRBottom = -1)
        {
            meLeft = (LRLeft >= 0 & LRLeft < meWidth) ? LRLeft : 0;
            meRight = (LRRight > LRLeft & LRRight <= meWidth) ? LRRight : meWidth;
            meTop = (LRTop >= 0 & LRTop < meHeight) ? LRTop : 0;
            meBottom = (LRBottom > LRTop & LRBottom <= meHeight) ? LRBottom : meHeight;
        }

        // ------------------------------------------------------------------ misc
        /// <summary>Copies the whole surface to a device context at (0,0).</summary>
        public void PaintToDevice(HandleRef hDC) => PaintToDevice(hDC, 0, 0, 0, 0, meWidth, meHeight);

        /// <summary>
        /// Copies the rectangle (SrcX,SrcY,W,H) of the surface to (DestX,DestY) on a
        /// device context. GDI surfaces use one BitBlt; plain surfaces use
        /// SetDIBitsToDevice. Handy for dirty-rectangle presents.
        /// </summary>
        /// <summary>
        /// Presents the whole surface scaled to DestW x DestH (integer zoom, letter-boxing,
        /// DPI scaling). <paramref name="Smooth"/> = HALFTONE (bilinear-ish) vs nearest.
        /// One GDI call either way.
        /// </summary>
        public void PaintToDeviceStretched(HandleRef hDC, int DestX, int DestY, int DestW, int DestH, bool Smooth = false)
        {
            if (pBuf == null || DestW <= 0 || DestH <= 0) return;
            _ = SetStretchBltMode(hDC, Smooth ? HALFTONE : COLORONCOLOR);
            if (meGdi) StretchBlt(hDC, DestX, DestY, DestW, DestH, hMemDC, 0, 0, meWidth, meHeight, SRCCOPY);
            else _ = StretchDIBits(hDC, DestX, DestY, DestW, DestH, 0, 0, meWidth, meHeight, pBuf, ref bi32BitInfo, 0, SRCCOPY);
        }

        /// <summary>
        /// Presents into a System.Drawing.Graphics (e.g. PaintEventArgs.Graphics): takes
        /// the HDC, blits the given source rectangle, releases the HDC. Convenience for
        /// Paint handlers; see <see cref="SpriteBox"/> for a complete control.
        /// </summary>
        public void PaintToGraphics(Graphics g, Rectangle Src, int DestX, int DestY)
        {
            IntPtr hdc = g.GetHdc();
            try { PaintToDevice(new HandleRef(g, hdc), DestX, DestY, Src.X, Src.Y, Src.Width, Src.Height); }
            finally { g.ReleaseHdc(hdc); }
        }
        public void PaintToGraphics(Graphics g) => PaintToGraphics(g, new Rectangle(0, 0, meWidth, meHeight), 0, 0);

        public void PaintToDevice(HandleRef hDC, int DestX, int DestY, int SrcX, int SrcY, int W, int H)
        {
            if (pBuf == null || W <= 0 || H <= 0) return;
            if (meGdi)
            {
                BitBlt(hDC, DestX, DestY, W, H, hMemDC, SrcX, SrcY, SRCCOPY);
                return;
            }
            // Describe the H rows starting at SrcY as their own top-down DIB (same
            // pitch), so no bottom-up scan-line arithmetic is needed.
            if (SrcY < 0 || SrcY + H > meHeight || SrcX < 0 || SrcX + W > meWidth) return;
            BITMAPINFO bi = bi32BitInfo;
            bi.bmiHeader.biHeight = -H;
            bi.bmiHeader.biSizeImage = (meWidth * H) << 2;
            _ = SetDIBitsToDevice(hDC, DestX, DestY, W, H, SrcX, 0, 0, H, pBuf + (long)SrcY * meWidth, ref bi, 0);
        }

        public void AddColorKey(int ColorKey)
        {
            SR2D.Native.AddColorKey(pBuf, meWidth, meHeight, ColorKey);
        }

        /// <summary>
        /// Converts this sprite from straight to premultiplied alpha in place
        /// (rgb *= alpha/255) and sets <see cref="Op"/> to <see cref="SR2D.Op.AlphaOver"/>.
        /// Do this once after loading a PNG that you want to composite with AlphaOver.
        /// Idempotent-ish: don't call it twice (colours would darken). Needs the new DLL.
        /// </summary>
        public void Premultiply()
        {
            SR2D.Native.PremulAlpha(pBuf, meWidth * meHeight);
            meOp = SR2D.Op.AlphaOver; mePremul = true;
        }

        /// <summary>Force alpha = 255 on every pixel.</summary>
        public void ClearAlpha()
        {
            SR2D.Native.ClearA(pBuf, meWidth * meHeight);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetPixel(int x, int y, int c)
        {
            if (x < meLeft || x >= meRight || y < meTop || y >= meBottom) return;
            pBuf[x + y * meWidth] = c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetPixel(int x, int y)
        {
            if ((uint)x >= (uint)meWidth || (uint)y >= (uint)meHeight) return 0;
            return pBuf[x + y * meWidth];
        }

        private static int ColFromLite(int x, int y, int z, float Brite)
        {
            if (Brite < 0) Brite = 0;
            if (Brite > 1) Brite = 1;
            float k = 127.4999f * Brite / (float)Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
            int R = (int)(x * k) + 128;
            int G = (int)(y * k) + 128;
            int B = (int)(z * k) + 128;
            return (R << 16) + (G << 8) + B;
        }

        public Bitmap ToBitmap
        {
            get
            {
                Bitmap BmpDest = new Bitmap(meWidth, meHeight, PixelFormat.Format32bppArgb);
                BitmapData BMD = BmpDest.LockBits(new Rectangle(0, 0, meWidth, meHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    long rowBytes = (long)meWidth * 4;
                    if (BMD.Stride == rowBytes)
                    {
                        Buffer.MemoryCopy(pBuf, (void*)BMD.Scan0, rowBytes * meHeight, rowBytes * meHeight);
                    }
                    else
                    {
                        byte* s = (byte*)pBuf;
                        byte* d = (byte*)BMD.Scan0;
                        for (int y = 0; y < meHeight; y++, s += rowBytes, d += BMD.Stride)
                            Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                    }
                }
                finally
                {
                    BmpDest.UnlockBits(BMD);
                }
                return BmpDest;
            }
        }

        // ------------------------------------------------------------ parallelism
        //
        // SR2D itself has no locks (like the original). Two threads drawing into the
        // SAME surface race: the z-order of overlapping sprites becomes random and
        // read-modify-write ops (AlphaBlend, Add, Mul ...) can lose one of the two
        // updates on pixels both threads touch at the same moment. Nothing crashes,
        // but frames are not reproducible.
        //
        // The deterministic way to use several cores is band rendering: split the
        // surface into horizontal bands, give every thread its own *view* (same
        // pixels, own lock rect) and let every thread issue the SAME draw calls.
        // Clipping makes each thread do only its band's share of the pixel work,
        // no pixel is ever written by two threads, and the result is bit-identical
        // to single-threaded rendering because every band applies the operations in
        // the same order.

        internal Sprite? meOwner;                              // set on views (CreateView): the sprite whose buffer they borrow
        internal System.Collections.Generic.List<Sprite>? meViews;                        // owner side: the live views of this sprite's buffer
        /// <summary>
        /// Creates a light-weight view that shares this sprite's pixels but has its own
        /// lock rect (clipped to the given rectangle). Disposing the view never frees
        /// the pixels. Views must not outlive the owner.
        /// <para>The lock rect clips WRITES only. Blitting a view as a SOURCE (Draw,
        /// DrawScaled, TileDraw, Warp, ...) reads the owner's full meWidth x meHeight
        /// buffer, so it is NOT a crop - TileDraw even strides by the owner's size.
        /// Use <see cref="Clone(Rectangle)"/> when you need a real sub-rectangle.</para>
        /// </summary>
        public Sprite CreateView(int Left, int Top, int Right, int Bottom)
        {
            var v = new Sprite();
            v.meBorrowed = true;
            v.pBuf = pBuf; v.meWidth = meWidth; v.meHeight = meHeight;
            v.meOp = meOp; v.mePremul = mePremul; v.bi32BitInfo = bi32BitInfo;
            v.SetLockRect(Left, Right, Top, Bottom);
            v.meOwner = this;
            (meViews ??= new System.Collections.Generic.List<Sprite>()).Add(v);           // the owner frees its views with itself (see FreeBuffer)
            return v;
        }

        /// <summary>
        /// Runs <paramref name="render"/> once per horizontal band, in parallel, each
        /// time with a view of this surface restricted to that band. The callback must
        /// issue the same drawing calls for every view it receives (it may read the
        /// view's lock rect if it wants to skip work). Output is identical to calling
        /// <c>render(this)</c> on one thread.
        ///
        /// Rules for the callback: only draw INTO the view it was given; source
        /// sprites are shared read-only, which is fine; any scratch/intermediate
        /// surface must be per-thread (allocate it inside the callback or keep one per
        /// band); don't rely on return values of MaskInterSector/GetPixel inside it.
        ///
        /// Band-exactness: every operation is bit-identical to single-threaded output
        /// except two original quirks whose result depends on the lock rect itself:
        /// DrawLine (endpoints are re-clipped per band, so a pixel at a band border may
        /// shift by one on steep lines) and DrawEBM(DestSpace: true) (the original
        /// measures the light-map offset from the clipped origin). Draw those after
        /// DrawParallel on the full surface if it matters.
        /// </summary>
        public void DrawParallel(Action<Sprite> render, int Threads = 0, int MinRowsPerBand = 32)
        {
            int rows = meBottom - meTop;
            if (Threads <= 0) Threads = Environment.ProcessorCount;
            int bands = Math.Min(Threads, Math.Max(1, rows / Math.Max(1, MinRowsPerBand)));
            if (bands <= 1 || pBuf == null) { render(this); return; }

            var views = new Sprite[bands];
            for (int i = 0; i < bands; i++)
            {
                int t = meTop + (int)((long)rows * i / bands);
                int b = meTop + (int)((long)rows * (i + 1) / bands);
                views[i] = CreateView(meLeft, t, meRight, b);
            }
            try
            {
                System.Threading.Tasks.Parallel.For(0, bands,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Threads },
                    i => render(views[i]));
            }
            finally
            {
                for (int i = 0; i < bands; i++) views[i].Dispose();
            }
        }

        // LockRect / Bounds / Rectangle overloads: see Sprite.Rect.cs

        #region Constructors
        private Sprite() { }

        public Sprite(Bitmap Bmp, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
            => LoadFromBitmap(Bmp, Trans, W, H, ColorKey);

        public Sprite(Sprite Src, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
            => LoadFromSprite(Src, Trans, W, H, ColorKey);

        public Sprite(string FileName, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
            => LoadFromFile(FileName, Trans, W, H, ColorKey);

        public Sprite(int W, int H, SR2D.Op Op = SR2D.Op.Paint)
        {
            if (Op == SR2D.Op.DefaultOp) Op = SR2D.Op.Paint;
            Init(W, H, Op);
        }

        /// <summary>
        /// Creates a surface whose pixels live in a GDI DIB section. Use this for the
        /// back buffer you present to a control: <see cref="PaintToDevice(HandleRef)"/>
        /// becomes a single BitBlt, and GDI/GDI+ can draw onto it via <see cref="Hdc"/>.
        /// All SR2D operations work exactly as on a normal sprite.
        /// </summary>
        public Sprite(int W, int H, bool gdiSurface, SR2D.Op Op = SR2D.Op.Paint)
        {
            if (Op == SR2D.Op.DefaultOp) Op = SR2D.Op.Paint;
            meGdi = gdiSurface;
            Init(W, H, Op);
        }
        #endregion

        public void Dispose()
        {
            if (meOwner != null) lock (meOwner) { meOwner.meViews?.Remove(this); meOwner = null; }   // a view unregistering itself
            FreeBuffer();
            // A disposed sprite becomes an empty 0x0 surface: every operation on it
            // (or with it as a source) clips to nothing instead of touching freed memory.
            meWidth = meHeight = 0;
            meLeft = meRight = meTop = meBottom = 0;
            GC.SuppressFinalize(this);
        }

        ~Sprite()
        {
            FreeBuffer();
        }
    }
}
