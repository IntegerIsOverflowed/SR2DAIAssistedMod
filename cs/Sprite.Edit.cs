using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    // In-place edits of a sprite's own pixels: rotate / flip / resize / crop / pad / shift / trim,
    // colour adjustments and effect chains applied to the sprite itself. Until now these needed
    // "new Sprite(old, Transform)" or a scratch sprite plus a Draw*.
    //
    // Rules
    //  * Size-preserving edits (Flip*, Rotate180, Rotate(degrees) without Grow, Shift, the colour
    //    ops, Apply) work inside the LOCK RECT (default = the whole sprite), so "flip this region"
    //    is a SetLockRect + Flip.
    //  * Size-changing edits (Rotate90/270 of a non-square sprite, Rotate(Grow: true), Resize,
    //    Scale, Crop, Expand, Trim) always take the whole sprite and reset the lock rect. A view
    //    (CreateView) cannot change size (InvalidOperationException); a GDI surface can (the DIB
    //    is recreated, Hdc stays valid for the new one).
    //  * All return this, so edits chain: s.FlipX().Rotate(15).Trim().
    //  * Op and Premultiplied are kept. Rotations / rescales of a straight-alpha sprite sample it
    //    premultiplied and write it back straight (no dark fringes at soft edges).
    public unsafe partial class Sprite
    {
        // ------------------------------------------------------------------ buffer replacement
        // Replaces the pixels with a W x H buffer (64-byte aligned, ours to free). Plain sprites
        // adopt the buffer; GDI surfaces recreate the DIB and copy; views must keep their size.
        void Adopt(int* buf, int W, int H)
        {
            if (W == meWidth && H == meHeight && (meBorrowed || meGdi))
            {   // same size: copy into the existing memory (a view's owner / the DIB keep their buffer)
                long bytes = (long)W * H * 4;
                Buffer.MemoryCopy(buf, pBuf, bytes, bytes);
                NativeMemory.AlignedFree(buf);
                if (meGdi) GdiFlush();
                return;
            }
            if (meBorrowed) { NativeMemory.AlignedFree(buf); throw new InvalidOperationException("a view (CreateView) cannot change its size; edit the owner or a copy"); }
            if (meGdi)
            {
                Init(W, H);                                   // new DIB section of the new size
                long bytes = (long)W * H * 4;
                Buffer.MemoryCopy(buf, pBuf, bytes, bytes);
                NativeMemory.AlignedFree(buf);
                return;
            }
            FreeBuffer();
            pBuf = buf; meWidth = W; meHeight = H;
            bi32BitInfo.bmiHeader.biWidth = W;
            bi32BitInfo.bmiHeader.biHeight = -H;
            bi32BitInfo.bmiHeader.biSizeImage = (W * H) << 2;
            SetLockRect(0, W, 0, H);
        }
        // Steals the buffer of a scratch sprite (which becomes empty) - no copy.
        void TakeOver(Sprite t)
        {
            int* b = t.pBuf; int W = t.meWidth, H = t.meHeight;
            t.pBuf = null; t.meWidth = t.meHeight = 0; t.meLeft = t.meRight = t.meTop = t.meBottom = 0;
            t.Dispose();
            Adopt(b, W, H);
            SetLockRect(0, meWidth, 0, meHeight);
        }
        static int* Alloc(int W, int H, int fill)
        {
            long n = (long)W * H;
            int* p = (int*)NativeMemory.AlignedAlloc((nuint)Math.Max(64, n * 4), Alignment);
            if (fill == 0) NativeMemory.Clear(p, (nuint)(n * 4)); else new Span<int>(p, (int)n).Fill(fill);
            return p;
        }
        // copy of the lock rect as a new sprite (Op / Premultiplied kept)
        Sprite RectCopy(int l, int t, int w, int h)
        {
            var s = new Sprite(w, h, meOp); s.mePremul = mePremul;
            for (int y = 0; y < h; y++) Buffer.MemoryCopy(pBuf + (long)(t + y) * meWidth + l, s.pBuf + (long)y * w, (long)w * 4, (long)w * 4);
            return s;
        }
        // op that writes a resampled copy back in the sprite's own alpha convention
        SR2D.Op SelfOp => Premultiplied ? SR2D.Op.Paint : (SR2D.Op)12;   // 12 = premultiplied work -> straight (see Blur)
        bool Empty => meWidth <= 0 || meHeight <= 0 || pBuf == null;
        // size-changing / whole-sprite edits are refused on a view (it shares the owner's buffer and only owns a clip rect)
        void Whole() { if (meBorrowed) throw new InvalidOperationException("a view (CreateView) is a clip rect on another sprite: whole-sprite edits (rotate 90, resize, crop, expand, trim) must be done on the owner or on a copy (Clone)"); }
        bool RectEmpty => meRight <= meLeft || meBottom <= meTop;

        // ------------------------------------------------------------------ copies
        /// <summary>A copy of the whole sprite (pixels, Op, Premultiplied; not the lock rect).</summary>
        public Sprite Clone() => Empty ? new Sprite(0, 0) : RectCopy(0, 0, meWidth, meHeight);
        /// <summary>A copy of a rectangle of the sprite (clamped to the surface; empty rectangle = empty sprite).</summary>
        public Sprite Clone(Rectangle r)
        {
            r.Intersect(Bounds);
            return r.IsEmpty || Empty ? new Sprite(0, 0) : RectCopy(r.X, r.Y, r.Width, r.Height);
        }
        public Sprite Clone(int x, int y, int w, int h) => Clone(new Rectangle(x, y, w, h));

        // ------------------------------------------------------------------ lossless flips / quarter turns
        /// <summary>Mirrors the lock rect left-right, in place.</summary>
        public Sprite FlipX()
        {
            if (Empty || RectEmpty) return this;
            int w = meRight - meLeft;
            int* tmp = stackalloc int[w <= 1024 ? w : 1];
            int* row = w <= 1024 ? tmp : (int*)NativeMemory.AlignedAlloc((nuint)((long)w * 4), Alignment);
            try
            {
                for (int y = meTop; y < meBottom; y++)
                {
                    int* p = pBuf + (long)y * meWidth + meLeft;
                    SR2D.Native.FlipX(p, row, w, 1);
                    Buffer.MemoryCopy(row, p, (long)w * 4, (long)w * 4);
                }
            }
            finally { if (row != tmp) NativeMemory.AlignedFree(row); }
            if (meGdi) GdiFlush();
            return this;
        }
        /// <summary>Mirrors the lock rect top-bottom, in place.</summary>
        public Sprite FlipY()
        {
            if (Empty || RectEmpty) return this;
            int w = meRight - meLeft; long bytes = (long)w * 4;
            int* tmp = stackalloc int[w <= 1024 ? w : 1];
            int* row = w <= 1024 ? tmp : (int*)NativeMemory.AlignedAlloc((nuint)bytes, Alignment);
            try
            {
                for (int a = meTop, b = meBottom - 1; a < b; a++, b--)
                {
                    int* pa = pBuf + (long)a * meWidth + meLeft, pb = pBuf + (long)b * meWidth + meLeft;
                    Buffer.MemoryCopy(pa, row, bytes, bytes); Buffer.MemoryCopy(pb, pa, bytes, bytes); Buffer.MemoryCopy(row, pb, bytes, bytes);
                }
            }
            finally { if (row != tmp) NativeMemory.AlignedFree(row); }
            if (meGdi) GdiFlush();
            return this;
        }
        /// <summary>Mirrors the lock rect: <paramref name="Horizontal"/> = left-right, <paramref name="Vertical"/> = top-bottom (both = a 180° turn).</summary>
        public Sprite Flip(bool Horizontal, bool Vertical = false) { if (Horizontal) FlipX(); if (Vertical) FlipY(); return this; }
        /// <summary>Same as <see cref="FlipX"/> (editor naming).</summary>
        public Sprite Mirror() => FlipX();
        /// <summary>Turns the lock rect by 180°, in place (lossless).</summary>
        public Sprite Rotate180() => FlipX().FlipY();

        /// <summary>
        /// Turns the WHOLE sprite by quarter turns clockwise ON SCREEN (1 = 90° CW, -1 / 3 = 90° CCW, 2 = 180°;
        /// note the original Transform.RotCW enum is the y-up name and turns the other way), lossless, in place; width and height swap for odd turns. Views cannot do odd turns of a
        /// non-square sprite.
        /// </summary>
        public Sprite Rotate90(int QuarterTurns = 1)
        {
            QuarterTurns = ((QuarterTurns % 4) + 4) % 4;
            if (QuarterTurns == 0 || Empty) return this;
            Whole();
            if (QuarterTurns == 2) { SetLockRect(); return Rotate180(); }
            // the original DLL's ROT_CW is named for y-up coordinates and turns counter-clockwise on a
            // y-down screen (kept bit-exact for the constructor); screen-clockwise is therefore RotCCW
            SR2D.Transform t = QuarterTurns == 1 ? SR2D.Transform.RotCCW : SR2D.Transform.RotCW;
            int* buf = Alloc(meHeight, meWidth, 0);
            Transform(pBuf, buf, meWidth, meHeight, t);
            Adopt(buf, meHeight, meWidth);
            SetLockRect(0, meWidth, 0, meHeight);
            return this;
        }
        /// <summary>90° clockwise, lossless, in place (width and height swap).</summary>
        public Sprite RotateCW() => Rotate90(1);
        /// <summary>90° counter-clockwise, lossless, in place (width and height swap).</summary>
        public Sprite RotateCCW() => Rotate90(3);
        /// <summary>Applies one of the original lossless transforms (flips / quarter turns / their combinations) to the whole sprite, in place.</summary>
        public Sprite Transform(SR2D.Transform Trans)
        {
            if (Trans == SR2D.Transform.None || Empty) return this;
            Whole();
            SetLockRect();
            switch (Trans)
            {
                case SR2D.Transform.FlipX: return FlipX();
                case SR2D.Transform.FlipY: return FlipY();
                case SR2D.Transform.FlipXY: return Rotate180();
            }
            int* buf = Alloc(meHeight, meWidth, 0);
            Transform(pBuf, buf, meWidth, meHeight, Trans);
            Adopt(buf, meHeight, meWidth);
            SetLockRect(0, meWidth, 0, meHeight);
            return this;
        }

        // ------------------------------------------------------------------ resampling edits
        // draws a copy of the lock rect through the warp back onto the rect (background first)
        Sprite Rewarp(Sprite src, ReadOnlySpan<PointF> quad, SR2D.Filter Filter, int Background)
        {
            ClearBuffer(Background);
            DrawFxQuad(src, quad, EmptyFx, default, SelfOp, Filter);
            src.Dispose();
            if (meGdi) GdiFlush();
            return this;
        }
        [ThreadStatic] static Effects? emptyFx;
        static Effects EmptyFx => emptyFx ??= new Effects();

        /// <summary>
        /// Rotates the contents of the lock rect by <paramref name="Degrees"/> (clockwise, any angle)
        /// about the rect's centre (or <paramref name="PivotX"/> / <paramref name="PivotY"/>, sprite
        /// pixels), in place. What turns out of the rect is cut off, uncovered areas get
        /// <paramref name="Background"/>. With <paramref name="Grow"/> the WHOLE sprite is resized to
        /// the rotated bounding box instead (nothing is cut; the sprite grows, centre stays the centre).
        /// Multiples of 90° are lossless (they take the flip / quarter-turn path when the rect is the
        /// whole sprite or square). Otherwise <paramref name="Filter"/> resamples (Bicubic default).
        /// </summary>
        public Sprite Rotate(float Degrees, SR2D.Filter Filter = SR2D.Filter.Bicubic, bool Grow = false, int Background = 0, float PivotX = -1, float PivotY = -1)
        {
            if (Empty || RectEmpty || float.IsNaN(Degrees) || float.IsInfinity(Degrees)) return this;
            Degrees = ((Degrees % 360f) + 360f) % 360f;
            if (Degrees == 0f) return this;
            bool whole = meLeft == 0 && meTop == 0 && meRight == meWidth && meBottom == meHeight;
            if (PivotX < 0 && PivotY < 0 && Degrees % 90f == 0f)
            {
                int q = (int)(Degrees / 90f);
                if (q == 2) return Rotate180();
                if ((whole && !meBorrowed) || meRight - meLeft == meBottom - meTop)
                {
                    if (whole && !meBorrowed) return Rotate90(q);
                    // square region: turn a copy losslessly and put it back
                    using var c = RectCopy(meLeft, meTop, meRight - meLeft, meBottom - meTop);
                    c.Rotate90(q);
                    Draw(c, meLeft, meTop, SR2D.Op.Paint);
                    return this;
                }
            }
            if (Grow) { Whole(); SetLockRect(); }
            int l = meLeft, t = meTop, w = meRight - meLeft, h = meBottom - meTop;
            float rad = Degrees * MathF.PI / 180f, c0 = MathF.Cos(rad), s0 = MathF.Sin(rad);
            float px = PivotX < 0 ? w * 0.5f : PivotX - l, py = PivotY < 0 ? h * 0.5f : PivotY - t;   // pivot in rect coordinates
            Span<PointF> q4 = stackalloc PointF[4];
            for (int i = 0; i < 4; i++)
            {   // corners turned about the pivot, still in rect coordinates
                float cx = ((i == 1 || i == 2) ? w : 0) - px, cy = (i >= 2 ? h : 0) - py;
                q4[i] = new PointF(px + cx * c0 - cy * s0, py + cx * s0 + cy * c0);
            }
            int nw = w, nh = h; float ox = l, oy = t;
            if (Grow)
            {   // the new canvas is the bounding box of the turned corners
                float x0 = q4[0].X, x1 = x0, y0 = q4[0].Y, y1 = y0;
                for (int i = 1; i < 4; i++) { x0 = MathF.Min(x0, q4[i].X); x1 = MathF.Max(x1, q4[i].X); y0 = MathF.Min(y0, q4[i].Y); y1 = MathF.Max(y1, q4[i].Y); }
                nw = Math.Max(1, (int)MathF.Ceiling(x1 - x0 - 0.001f)); nh = Math.Max(1, (int)MathF.Ceiling(y1 - y0 - 0.001f));
                ox = -x0 + (nw - (x1 - x0)) * 0.5f; oy = -y0 + (nh - (y1 - y0)) * 0.5f;   // centred in the new canvas
            }
            for (int i = 0; i < 4; i++) q4[i] = new PointF(q4[i].X + ox, q4[i].Y + oy);
            Sprite src = RectCopy(l, t, w, h);
            if (Grow && (nw != meWidth || nh != meHeight))
            {
                var tgt = new Sprite(nw, nh, meOp); tgt.mePremul = mePremul;
                tgt.Rewarp(src, q4, Filter, Background);
                TakeOver(tgt);
                return this;
            }
            return Rewarp(src, q4, Filter, Background);
        }

        /// <summary>
        /// Rescales the WHOLE sprite to <paramref name="W"/> x <paramref name="H"/> in place (0 keeps
        /// the aspect ratio from the other side). <paramref name="Filter"/> Auto = bicubic up,
        /// area-averaged down, Nearest for pixel art.
        /// </summary>
        public Sprite Resize(int W, int H, SR2D.Filter Filter = SR2D.Filter.Auto)
        {
            if (Empty) return this;
            if (W <= 0 && H <= 0) return this;
            if (W <= 0) W = Math.Max(1, (int)MathF.Round((float)H * meWidth / meHeight));
            if (H <= 0) H = Math.Max(1, (int)MathF.Round((float)W * meHeight / meWidth));
            if (W == meWidth && H == meHeight) return this;
            Whole();
            var tgt = new Sprite(W, H, meOp); tgt.mePremul = mePremul;
            Span<PointF> q = stackalloc PointF[4] { new PointF(0, 0), new PointF(W, 0), new PointF(W, H), new PointF(0, H) };
            tgt.Rewarp(Clone(), q, Filter, 0);
            TakeOver(tgt);
            return this;
        }
        /// <summary>Rescales the whole sprite by a factor (0.5 = half size), in place.</summary>
        public Sprite Scale(float Factor, SR2D.Filter Filter = SR2D.Filter.Auto) => Scale(Factor, Factor, Filter);
        public Sprite Scale(float FactorX, float FactorY, SR2D.Filter Filter = SR2D.Filter.Auto)
        {
            if (Empty || !(FactorX > 0) || !(FactorY > 0)) return this;
            return Resize(Math.Max(1, (int)MathF.Round(meWidth * FactorX)), Math.Max(1, (int)MathF.Round(meHeight * FactorY)), Filter);
        }

        // ------------------------------------------------------------------ canvas size
        /// <summary>
        /// Changes the canvas: the new sprite is <paramref name="Width"/> x <paramref name="Height"/>
        /// and the old pixels land at (<paramref name="X"/>, <paramref name="Y"/>) in it (may be
        /// negative = crop); uncovered area = <paramref name="Fill"/>. Crop / Expand / Trim are shortcuts.
        /// </summary>
        public Sprite Recanvas(int X, int Y, int Width, int Height, int Fill = 0)
        {
            if (Width <= 0 || Height <= 0) throw new ArgumentOutOfRangeException(nameof(Width), "the new size must be positive");
            if (Empty) { Init(Width, Height); ClearBuffer(Fill); return this; }
            if (X == 0 && Y == 0 && Width == meWidth && Height == meHeight) return this;
            Whole();
            int* buf = Alloc(Width, Height, Fill);
            int sx0 = Math.Max(0, -X), sy0 = Math.Max(0, -Y), dx0 = Math.Max(0, X), dy0 = Math.Max(0, Y);
            int cw = Math.Min(meWidth - sx0, Width - dx0), ch = Math.Min(meHeight - sy0, Height - dy0);
            for (int y = 0; y < ch; y++)
                Buffer.MemoryCopy(pBuf + (long)(sy0 + y) * meWidth + sx0, buf + (long)(dy0 + y) * Width + dx0, (long)cw * 4, (long)cw * 4);
            Adopt(buf, Width, Height);
            SetLockRect(0, meWidth, 0, meHeight);
            return this;
        }
        /// <summary>Keeps only the rectangle (clamped to the sprite), in place.</summary>
        public Sprite Crop(int x, int y, int w, int h)
        {
            var r = Rectangle.Intersect(new Rectangle(x, y, w, h), Bounds);
            if (r.IsEmpty) throw new ArgumentOutOfRangeException(nameof(x), "the crop rectangle does not touch the sprite");
            return Recanvas(-r.X, -r.Y, r.Width, r.Height);
        }
        public Sprite Crop(Rectangle r) => Crop(r.X, r.Y, r.Width, r.Height);
        /// <summary>Crops to the current lock rect (SetLockRect first), then resets the lock rect.</summary>
        public Sprite CropToLockRect() => Crop(meLeft, meTop, meRight - meLeft, meBottom - meTop);
        /// <summary>Adds (or with negative values removes) a border on each side, in place; new area = <paramref name="Fill"/>.</summary>
        public Sprite Expand(int Left, int Top, int Right, int Bottom, int Fill = 0)
            => Recanvas(Left, Top, meWidth + Left + Right, meHeight + Top + Bottom, Fill);
        /// <summary>Adds the same border on every side.</summary>
        public Sprite Expand(int All, int Fill = 0) => Expand(All, All, All, All, Fill);

        /// <summary>
        /// Bounding box of the "content": pixels whose alpha is above <paramref name="AlphaThreshold"/>
        /// (default: any non-zero alpha); or, when <paramref name="Background"/> is given, pixels that
        /// differ from that colour (for opaque sprites). Empty rectangle when nothing qualifies.
        /// </summary>
        public Rectangle ContentBounds(int AlphaThreshold = 0, int? Background = null)
        {
            if (Empty) return Rectangle.Empty;
            int l = meWidth, t = meHeight, r = -1, b = -1;
            for (int y = 0; y < meHeight; y++)
            {
                int* row = pBuf + (long)y * meWidth; int first = -1, last = -1;
                if (Background.HasValue)
                {
                    int bg = Background.Value;
                    for (int x = 0; x < meWidth; x++) if (row[x] != bg) { first = x; break; }
                    if (first < 0) continue;
                    for (int x = meWidth - 1; x >= first; x--) if (row[x] != bg) { last = x; break; }
                }
                else
                {
                    uint th = (uint)Math.Clamp(AlphaThreshold, 0, 255);
                    for (int x = 0; x < meWidth; x++) if ((uint)row[x] >> 24 > th) { first = x; break; }
                    if (first < 0) continue;
                    for (int x = meWidth - 1; x >= first; x--) if ((uint)row[x] >> 24 > th) { last = x; break; }
                }
                if (first < l) l = first; if (last > r) r = last; if (y < t) t = y; b = y;
            }
            return r < 0 ? Rectangle.Empty : Rectangle.FromLTRB(l, t, r + 1, b + 1);
        }
        /// <summary>
        /// Crops away the transparent (or <paramref name="Background"/>-coloured) border, keeping
        /// <paramref name="Margin"/> pixels around the content, in place. A sprite with no content
        /// is left unchanged. Returns this.
        /// </summary>
        public Sprite Trim(int Margin = 0, int AlphaThreshold = 0, int? Background = null)
        {
            var r = ContentBounds(AlphaThreshold, Background);
            if (r.IsEmpty) return this;
            r.Inflate(Margin, Margin);
            r.Intersect(Bounds);
            return r == Bounds ? this : Crop(r);
        }

        // ------------------------------------------------------------------ shifting
        /// <summary>
        /// Moves the contents of the lock rect by (dx, dy) pixels. <paramref name="Wrap"/> = what
        /// leaves on one side comes back on the other (a scrolling tile); otherwise the uncovered
        /// strip is <paramref name="Fill"/>.
        /// </summary>
        public Sprite Shift(int dx, int dy, bool Wrap = false, int Fill = 0)
        {
            if (Empty || RectEmpty) return this;
            int w = meRight - meLeft, h = meBottom - meTop;
            if (Wrap) { dx = ((dx % w) + w) % w; dy = ((dy % h) + h) % h; if (dx == 0 && dy == 0) return this; }
            else if (dx == 0 && dy == 0) return this;
            else if (Math.Abs(dx) >= w || Math.Abs(dy) >= h) { ClearBuffer(Fill); return this; }
            long rb = (long)w * 4;
            int* tmp = (int*)NativeMemory.AlignedAlloc((nuint)Math.Max(64, rb), Alignment);
            try
            {
                // rows: move by dy (a memmove per row, in the right order)
                if (dy != 0)
                {
                    if (Wrap)
                    {   // rotate the rows: three reversals would need a row swap each; simpler = one temp block per column chunk -> use a full temp copy of the rect
                        int* blk = (int*)NativeMemory.AlignedAlloc((nuint)(rb * h), Alignment);
                        try
                        {
                            for (int y = 0; y < h; y++) Buffer.MemoryCopy(pBuf + (long)(meTop + y) * meWidth + meLeft, blk + (long)y * w, rb, rb);
                            for (int y = 0; y < h; y++) Buffer.MemoryCopy(blk + (long)y * w, pBuf + (long)(meTop + (y + dy) % h) * meWidth + meLeft, rb, rb);
                        }
                        finally { NativeMemory.AlignedFree(blk); }
                    }
                    else if (dy > 0) { for (int y = h - 1; y >= dy; y--) Buffer.MemoryCopy(pBuf + (long)(meTop + y - dy) * meWidth + meLeft, pBuf + (long)(meTop + y) * meWidth + meLeft, rb, rb); for (int y = 0; y < dy; y++) new Span<int>(pBuf + (long)(meTop + y) * meWidth + meLeft, w).Fill(Fill); }
                    else { for (int y = 0; y < h + dy; y++) Buffer.MemoryCopy(pBuf + (long)(meTop + y - dy) * meWidth + meLeft, pBuf + (long)(meTop + y) * meWidth + meLeft, rb, rb); for (int y = h + dy; y < h; y++) new Span<int>(pBuf + (long)(meTop + y) * meWidth + meLeft, w).Fill(Fill); }
                }
                if (dx != 0)
                {
                    for (int y = meTop; y < meBottom; y++)
                    {
                        int* row = pBuf + (long)y * meWidth + meLeft;
                        if (Wrap)
                        {
                            Buffer.MemoryCopy(row, tmp, rb, rb);
                            Buffer.MemoryCopy(tmp, row + dx, (long)(w - dx) * 4, (long)(w - dx) * 4);
                            Buffer.MemoryCopy(tmp + (w - dx), row, (long)dx * 4, (long)dx * 4);
                        }
                        else if (dx > 0) { Buffer.MemoryCopy(row, row + dx, (long)(w - dx) * 4, (long)(w - dx) * 4); new Span<int>(row, dx).Fill(Fill); }
                        else { Buffer.MemoryCopy(row - dx, row, (long)(w + dx) * 4, (long)(w + dx) * 4); new Span<int>(row + w + dx, -dx).Fill(Fill); }
                    }
                }
            }
            finally { NativeMemory.AlignedFree(tmp); }
            if (meGdi) GdiFlush();
            return this;
        }
        /// <summary>Scrolls the lock rect with wrap-around (see <see cref="Shift"/>).</summary>
        public Sprite Scroll(int dx, int dy) => Shift(dx, dy, true);

        // ------------------------------------------------------------------ colour, in place
        /// <summary>
        /// Runs an effect chain over the lock rect and writes the result back (blur, colour, distortion,
        /// shadow ... anything <see cref="Effects"/> can do). Effects that need a margin (blur, shadow,
        /// glow) are clipped to the rect - <see cref="Expand"/> first when the halo must fit.
        /// </summary>
        public Sprite Apply(Effects Fx)
        {
            if (Fx == null) throw new ArgumentNullException(nameof(Fx));
            if (Empty || RectEmpty) return this;
            // DRAW_FX reads the whole source before its first write, so src == dst is safe (like Blur)
            DrawFx(this, 0, 0, Fx, SelfOp);
            if (meGdi) GdiFlush();
            return this;
        }
        /// <summary>Colour adjustment in place (see <see cref="Effects.Color"/> for the ranges: Brightness -1..1, Contrast / Saturation / Gamma 1 = unchanged, Hue in degrees).</summary>
        public Sprite AdjustColor(float Brightness = 0, float Contrast = 1, float Saturation = 1, float Gamma = 1, float Hue = 0)
            => Apply(new Effects().Color(Brightness, Contrast, Saturation, Gamma, Hue));
        /// <summary>Negative of the colour (alpha untouched), in place.</summary>
        public Sprite Invert() => Apply(new Effects().Invert());
        /// <summary>Greyscale (saturation 0), in place.</summary>
        public Sprite Grayscale() => Apply(new Effects().Grayscale());
        /// <summary>Mixes the colour toward <paramref name="Color"/> by <paramref name="Amount"/> (0..1; 1 = a silhouette), in place.</summary>
        public Sprite Tint(int Color, float Amount) => Apply(new Effects().Tint(Color, Amount));
        /// <summary>Hue rotation in degrees, in place.</summary>
        public Sprite RotateHue(float Degrees) => Apply(new Effects().Hue(Degrees));
        /// <summary>Multiplies every alpha by <paramref name="Factor"/> (0..1) - the sprite becomes translucent; premultiplied sprites scale the colour along.</summary>
        public Sprite Fade(float Factor)
        {
            if (Empty || RectEmpty) return this;
            if (float.IsNaN(Factor)) return this;
            int f = (int)Math.Clamp(Factor * 256f + 0.5f, 0f, 256f);
            if (f == 256) return this;
            bool pm = Premultiplied;
            for (int y = meTop; y < meBottom; y++)
            {
                int* row = pBuf + (long)y * meWidth;
                for (int x = meLeft; x < meRight; x++)
                {
                    uint c = (uint)row[x];
                    uint a = ((c >> 24) * (uint)f + 128) >> 8;
                    if (pm)
                    {
                        uint r = (((c >> 16) & 255) * (uint)f + 128) >> 8, g = (((c >> 8) & 255) * (uint)f + 128) >> 8, b = ((c & 255) * (uint)f + 128) >> 8;
                        row[x] = (int)((a << 24) | (r << 16) | (g << 8) | b);
                    }
                    else row[x] = (int)((a << 24) | (c & 0x00ffffffu));
                }
            }
            if (meGdi) GdiFlush();
            return this;
        }
        /// <summary>
        /// Converts premultiplied pixels back to straight alpha in place (the inverse of
        /// <see cref="Premultiply"/>); Op becomes AlphaBlend, <see cref="Premultiplied"/> false. No-op
        /// on a sprite that is not premultiplied.
        /// </summary>
        public Sprite Unpremultiply()
        {
            if (!Premultiplied || Empty) { mePremul = false; return this; }
            DrawFx(this, 0, 0, EmptyFx, (SR2D.Op)12);          // lock rect only; loads premultiplied (FlagPremul), writes straight
            mePremul = false;
            if (meOp == SR2D.Op.AlphaOver) meOp = SR2D.Op.AlphaBlend;
            if (meGdi) GdiFlush();
            return this;
        }
    }
}
