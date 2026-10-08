// Selection - an 8-bit coverage mask the size of a sprite (0 = not selected, 255 = selected,
// in between = soft edge), i.e. an editor selection: magic wand (flood), polygon / lasso,
// rectangle, ellipse, from alpha or a brightness threshold, combined with Add / Subtract /
// Intersect / Invert, feathered, grown / shrunk. A Sprite uses it in three ways:
//
//   canvas.FloodFill(x, y, colour, tolerance)          bucket fill (wand + fill in one call)
//   canvas.Fill(sel, colour, op)                       colour through the selection
//   canvas.Apply(sel, s => { ...draw anything... })    edit ONLY the selected pixels: the
//                                                      callback draws into a copy, the result is
//                                                      lerped back by the coverage
//   sel.Bounds / sel.Contains / sel.Coverage(x, y)     for parsing images (region size, bbox)
//
// Everything pixel-heavy runs in the DLL (FLOOD_MASK / FILL_MASK8 / LERP_MASK8, SIMD).
// The mask is plain bytes (Selection.Pixels), so custom tools are easy to add.
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    /// <summary>How the magic wand compares colours.</summary>
    public enum SelectMetric
    {
        /// <summary>Max channel difference (Chebyshev, like GIMP / Paint.NET) - the native fast path.</summary>
        Rgb,
        /// <summary>Weighted RGB distance (0.30 / 0.59 / 0.11) - compares like the eye (managed path).</summary>
        Perceptual,
    }

    /// <summary>How a new shape combines with the current selection.</summary>
    public enum SelectMode { Replace, Add, Subtract, Intersect, Xor }

    public sealed unsafe class Selection : IDisposable
    {
        byte* p; int w, h;                  // size (a Rotate90 of a non-square selection swaps them)
        int bl, bt, br, bb;                 // bounding box of non-zero coverage (br <= bl: empty); conservative
        bool disposed;
        int[]? edge; int edgeCount, edgeVersion = -1;   // cached edge pixels (x, y pairs) for DrawOutline
        Run[]? runs; int runCount, runVersion = -1, runThreshold = -1;   // cached horizontal runs (see Runs)

        /// <summary>A horizontal run of selected pixels: row <see cref="Y"/>, columns <see cref="X"/> .. X + Length - 1.</summary>
        public readonly struct Run
        {
            public readonly int Y, X, Length;
            public Run(int y, int x, int len) { Y = y; X = x; Length = len; }
            public int End => X + Length;      // exclusive
            public override string ToString() => $"y {Y}: x {X}..{End - 1} ({Length})";
        }

        /// <summary>Change counter (bumped by every operation that alters the mask). Call <see cref="Invalidate"/> after editing <see cref="Pixels"/> directly.</summary>
        public int Version { get; private set; }

        public Selection(int Width, int Height)
        {
            if (Width <= 0 || Height <= 0) throw new ArgumentOutOfRangeException(nameof(Width));
            w = Width; h = Height;
            p = (byte*)NativeMemory.AllocZeroed((nuint)((long)w * h));
            bl = w; bt = h; br = 0; bb = 0;
        }
        /// <summary>Selection the size of <paramref name="Of"/> (empty).</summary>
        public Selection(Sprite Of) : this(Of.Width, Of.Height) { }

        public int Width => w;
        public int Height => h;
        internal byte* Ptr => p;
        /// <summary>Raw coverage bytes, row-major (no copy).</summary>
        public Span<byte> Pixels => new Span<byte>(p, w * h);
        public byte Coverage(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? p[(long)y * w + x] : (byte)0;
        public bool Contains(int x, int y) => Coverage(x, y) != 0;
        public bool IsEmpty => br <= bl || bb <= bt;
        /// <summary>Bounding box of the selected pixels (exact after every operation; empty rectangle when nothing is selected).</summary>
        public Rectangle Bounds => IsEmpty ? Rectangle.Empty : Rectangle.FromLTRB(bl, bt, br, bb);
        /// <summary>Number of pixels with coverage > 0.</summary>
        public int Count { get { int n = 0; byte* q = p; for (long i = (long)w * h; i > 0; --i) n += *q++ != 0 ? 1 : 0; return n; } }
        /// <summary>Sum of coverage / 255 (area in pixels, soft edges counted fractionally).</summary>
        public double Area { get { long s = 0; byte* q = p; for (long i = (long)w * h; i > 0; --i) s += *q++; return s / 255.0; } }

        // ------------------------------------------------------------------ building
        public Selection Clear() { NativeMemory.Clear(p, (nuint)((long)w * h)); bl = w; bt = h; br = 0; bb = 0; Version++; return this; }
        public Selection SelectAll() { new Span<byte>(p, w * h).Fill(255); bl = 0; bt = 0; br = w; bb = h; Version++; return this; }
        public Selection Invert() { byte* q = p; for (long i = (long)w * h; i > 0; --i, ++q) *q = (byte)(255 - *q); bl = 0; bt = 0; br = w; bb = h; Shrink(); Version++; return this; }
        /// <summary>After writing to <see cref="Pixels"/> yourself: recomputes the bounds and bumps <see cref="Version"/>.</summary>
        public Selection Invalidate() { bl = 0; bt = 0; br = w; bb = h; Shrink(); Version++; return this; }

        /// <summary>
        /// Magic wand: selects the pixels of <paramref name="Src"/> connected to (x, y) whose colour
        /// is within <paramref name="Tolerance"/> (0..255, max channel difference) of the pixel
        /// under the seed. <paramref name="Contiguous"/> false = every matching pixel of the image.
        /// <paramref name="Diagonal"/> = 8-connected. <paramref name="Soft"/> = coverage fades over
        /// the outer half of the tolerance band (anti-aliased edge on gradients).
        /// <paramref name="IgnoreAlpha"/> compares RGB only. Restricted to Src's lock rect.
        /// Returns the number of pixels the wand found (before combining).
        /// </summary>
        public int Wand(Sprite Src, int x, int y, int Tolerance = 0, SelectMode Mode = SelectMode.Replace,
                        bool Contiguous = true, bool Diagonal = false, bool Soft = false, bool IgnoreAlpha = false,
                        SelectMetric Metric = SelectMetric.Rgb)
            => Metric == SelectMetric.Rgb
               ? WandCore(Src, x, y, 0, Tolerance, Mode, Contiguous, Diagonal, Soft, IgnoreAlpha, false)
               : WandCoreWeighted(Src, x, y, 0, Tolerance, Mode, Contiguous, Diagonal, Soft, IgnoreAlpha, false);

        /// <summary>Selects by colour (all pixels within Tolerance of <paramref name="Color"/>, ARGB), optionally only the region connected to (x, y).</summary>
        public int ByColor(Sprite Src, int Color, int Tolerance = 0, SelectMode Mode = SelectMode.Replace, bool IgnoreAlpha = false,
                           bool Contiguous = false, int x = 0, int y = 0, bool Diagonal = false, bool Soft = false,
                           SelectMetric Metric = SelectMetric.Rgb)
            => Metric == SelectMetric.Rgb
               ? WandCore(Src, x, y, Color, Tolerance, Mode, Contiguous, Diagonal, Soft, IgnoreAlpha, true)
               : WandCoreWeighted(Src, x, y, Color, Tolerance, Mode, Contiguous, Diagonal, Soft, IgnoreAlpha, true);

        /// <summary>
        /// Managed twin of the native wand with a PERCEPTUAL distance: sqrt(0.30 dR^2 + 0.59 dG^2 + 0.11 dB^2 (+ 0.30 dA^2))
        /// &lt;= Tolerance - greys and skin tones compare like the eye sees them, where the max-channel (Chebyshev) metric
        /// overweights blue. Same flags and SelectMode handling as <see cref="Wand"/> (Diagonal is accepted for
        /// signature compatibility; the weighted flood is 4-connected); scanline flood in managed code -
        /// fine for editor-scale images, not per-frame work.
        /// </summary>
        int WandCoreWeighted(Sprite Src, int x, int y, int refColor, int tol, SelectMode mode, bool contiguous, bool diagonal, bool soft, bool ignoreAlpha, bool useRef)
        {
            Check(Src);
            var r = Src.LockRect;
            if (!r.Contains(x, y)) return 0;
            int* px = (int*)Src.PixelPtr;
            int W = Src.Width, H = Src.Height;
            uint seed = useRef ? (uint)refColor : (uint)px[y * W + x];
            float sr = (seed >>> 16) & 255, sg = (seed >>> 8) & 255, sb = seed & 255, sa = (seed >>> 24) & 255;
            float wr = 0.30f, wg = 0.59f, wb = 0.11f, wa = 0.30f;
            float tol2 = tol * (float)tol;
            bool Match(int p)
            {
                uint c = (uint)p;
                float dr = ((c >>> 16) & 255) - sr, dg = ((c >>> 8) & 255) - sg, db = (c & 255) - sb;
                float d = wr * dr * dr + wg * dg * dg + wb * db * db;
                if (!ignoreAlpha) { float da = ((c >>> 24) & 255) - sa; d += wa * da * da; }
                return d <= tol2;
            }
            float CovOf(int p)
            {
                if (!soft) return 255;
                uint c = (uint)p;
                float dr = ((c >>> 16) & 255) - sr, dg = ((c >>> 8) & 255) - sg, db = (c & 255) - sb;
                float d2 = wr * dr * dr + wg * dg * dg + wb * db * db;
                if (!ignoreAlpha) { float da = ((c >>> 24) & 255) - sa; d2 += wa * da * da; }
                float d = MathF.Sqrt(d2);
                return Math.Clamp(255 * (1 - (d - tol / 2f) / MathF.Max(1f, tol / 2f)), 0, 255);
            }

            using var tmp = mode == SelectMode.Replace ? null : new Selection(W, H);   // the temporary mask is native memory: it must not leak when the wand returns
            var fill = tmp ?? this;
            if (mode == SelectMode.Replace) fill.Clear();   // Replace replaces (the native path does the same): the old mask must not guard the flood
            byte* m = fill.p;
            int found = 0;
            int bx0 = W, by0 = H, bx1 = 0, by1 = 0;
            void Bump(int x0, int y0, int x1, int y1) { if (x0 < bx0) bx0 = x0; if (y0 < by0) by0 = y0; if (x1 > bx1) bx1 = x1; if (y1 > by1) by1 = y1; }
            if (!contiguous)
            {
                for (int i = 0; i < W * H; i++)
                {
                    if (m[i] != 0 || !Match(px[i])) continue;
                    m[i] = (byte)CovOf(px[i]); found++;
                    Bump(i % W, i / W, i % W + 1, i / W + 1);
                }
            }
            else
            {
                var seen = new bool[W * H];
                var stack = new System.Collections.Generic.Stack<(int x0, int x1, int yv)>();
                void ScanRow(int yv, int from, int to)
                {
                    for (int i = from; i < to; i++)
                    {
                        int j = yv * W + i;
                        if (seen[j] || !Match(px[j])) continue;
                        int l = i;
                        while (l > r.Left && !seen[yv * W + l - 1] && Match(px[yv * W + l - 1])) l--;
                        int rr = i;
                        while (rr < r.Right - 1 && !seen[yv * W + rr + 1] && Match(px[yv * W + rr + 1])) rr++;
                        for (int q = l; q <= rr; q++) { seen[yv * W + q] = true; m[yv * W + q] = (byte)CovOf(px[yv * W + q]); }
                        found += rr - l + 1;
                        Bump(l, yv, rr + 1, yv + 1);
                        stack.Push((l, rr, yv));
                        i = rr;
                    }
                }
                ScanRow(y, x, x + 1);        // the run-extension inside ScanRow covers left AND right of the seed
                while (stack.Count > 0)
                {
                    var (x0, x1, yv) = stack.Pop();
                    if (yv - 1 >= r.Top) ScanRow(yv - 1, x0, x1 + 1);
                    if (yv + 1 < r.Bottom) ScanRow(yv + 1, x0, x1 + 1);
                }
            }
            if (bx1 <= bx0) return 0;                     // nothing matched
            fill.bl = bx0; fill.bt = by0; fill.br = bx1; fill.bb = by1; fill.Version++;
            if (mode != SelectMode.Replace) Combine(fill, mode);
            else { bl = fill.bl; bt = fill.bt; br = fill.br; bb = fill.bb; Version++; }
            return found;
        }

        /// <summary>Quick-mask EXPORT: the selection coverage becomes the sprite's ALPHA of <paramref name="colour"/> composited
        /// over <paramref name="background"/> - paint on it with any verb (opaque brush = select, erase = deselect, a soft brush =
        /// a soft edge), then hand it back with <see cref="FromSprite"/>. <paramref name="Dst"/> must be this selection's size.</summary>
        public void ToSprite(Sprite Dst, int colour = unchecked((int)0xFF3366FF), int background = unchecked((int)0x00000000))
        {
            Check(Dst);
            byte* mk = p; int* q = (int*)Dst.PixelPtr;
            int cr = (colour >>> 16) & 255, cg = (colour >>> 8) & 255, cb = colour & 255;
            int br = (background >>> 16) & 255, bg = (background >>> 8) & 255, bb = background & 255, ba = (background >>> 24) & 255;
            for (int i = 0; i < w * h; i++)
            {
                float a = mk[i] / 255f;
                int alpha = (int)MathF.Round(255 * a + ba * (1 - a));
                q[i] = unchecked((alpha << 24)
                     | ((int)MathF.Round(cr * a + br * (1 - a)) << 16)
                     | ((int)MathF.Round(cg * a + bg * (1 - a)) << 8)
                     | (int)MathF.Round(cb * a + bb * (1 - a)));
            }
        }

        /// <summary>Quick-mask IMPORT: the sprite's ALPHA becomes the selection coverage (paint with any verb, then read the mask
        /// back). <paramref name="Mask"/> must be this selection's size.</summary>
        public void FromSprite(Sprite Mask)
        {
            Check(Mask);
            byte* mk = p; int* q = (int*)Mask.PixelPtr;
            for (int i = 0; i < w * h; i++) mk[i] = (byte)((q[i] >>> 24) & 255);
            Invalidate();                                 // bounds + Version
        }

        int WandCore(Sprite Src, int x, int y, int refColor, int tol, SelectMode mode, bool contiguous, bool diagonal, bool soft, bool ignoreAlpha, bool useRef)
        {
            Check(Src);
            int flags = (contiguous ? 0 : 1) | (ignoreAlpha ? 2 : 0) | (diagonal ? 4 : 0) | (soft ? 8 : 0) | (useRef ? 16 : 0);
            var r = Src.LockRect;
            int* bbox = stackalloc int[4];
            if (mode == SelectMode.Replace)
            {
                Clear();
                int n = SR2D.Native.FloodMask(Src.PixelPtr, w, h, r.Left, r.Top, r.Right, r.Bottom, x, y, refColor, tol, flags, p, w, bbox);
                if (n > 0) { bl = bbox[0]; bt = bbox[1]; br = bbox[2]; bb = bbox[3]; }
                Version++;
                return n < 0 ? 0 : n;
            }
            using var tmp = new Selection(w, h);
            int m = SR2D.Native.FloodMask(Src.PixelPtr, w, h, r.Left, r.Top, r.Right, r.Bottom, x, y, refColor, tol, flags, tmp.p, w, bbox);
            if (m > 0) { tmp.bl = bbox[0]; tmp.bt = bbox[1]; tmp.br = bbox[2]; tmp.bb = bbox[3]; }
            Combine(tmp, mode);
            return m < 0 ? 0 : m;
        }

        /// <summary>Adds a rectangle (pixel-aligned, hard edge).</summary>
        public Selection Rect(int x, int y, int Width, int Height, SelectMode Mode = SelectMode.Replace)
        {
            using var tmp = new Selection(w, h);
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(w, x + Width), y1 = Math.Min(h, y + Height);
            for (int yy = y0; yy < y1; yy++) new Span<byte>(tmp.p + (long)yy * w + x0, Math.Max(0, x1 - x0)).Fill(255);
            if (x1 > x0 && y1 > y0) { tmp.bl = x0; tmp.bt = y0; tmp.br = x1; tmp.bb = y1; }
            return Combine(tmp, Mode);
        }
        public Selection Rect(Rectangle r, SelectMode Mode = SelectMode.Replace) => Rect(r.X, r.Y, r.Width, r.Height, Mode);
        /// <summary>The sprite's current lock rect as a selection (the whole sprite when it is the default).</summary>
        public Selection FromLockRect(Sprite Src, SelectMode Mode = SelectMode.Replace) { Check(Src); return Rect(Src.LockRect, Mode); }

        /// <summary>Adds a polygon (lasso). <paramref name="AA"/> gives a soft, anti-aliased edge; <paramref name="EvenOdd"/> = even-odd fill rule for self-intersecting outlines.</summary>
        public Selection Polygon(ReadOnlySpan<PointF> Points, SelectMode Mode = SelectMode.Replace, bool AA = true, bool EvenOdd = false)
        {
            if (Points.Length < 3) return this;
            using var s = new Sprite(w, h, SR2D.Op.Paint);
            s.FillPolygon(Points, unchecked((int)0xFFFFFFFF), SR2D.LineOp.Set, AA, EvenOdd);
            return FromChannel(s, 16, Mode);
        }
        /// <summary>Adds an ellipse.</summary>
        public Selection Ellipse(float cx, float cy, float rx, float ry, SelectMode Mode = SelectMode.Replace, bool AA = true)
        {
            using var s = new Sprite(w, h, SR2D.Op.Paint);
            s.FillEllipse(cx, cy, rx, ry, unchecked((int)0xFFFFFFFF), SR2D.LineOp.Set, AA);
            return FromChannel(s, 16, Mode);
        }
        /// <summary>Adds a path built with <see cref="Sprite.PathBuilder"/> (curves, arcs).</summary>
        public Selection Path(Sprite.PathBuilder Path, SelectMode Mode = SelectMode.Replace, bool AA = true, bool EvenOdd = false)
        {
            using var s = new Sprite(w, h, SR2D.Op.Paint);
            s.FillPath(Path, unchecked((int)0xFFFFFFFF), SR2D.LineOp.Set, AA, EvenOdd);
            return FromChannel(s, 16, Mode);
        }

        /// <summary>Selection from the alpha channel of <paramref name="Src"/> (coverage = alpha): the opaque shape of a sprite.</summary>
        public Selection FromAlpha(Sprite Src, SelectMode Mode = SelectMode.Replace) => FromChannel(Src, 24, Mode);
        /// <summary>Selection from a channel of <paramref name="Src"/> (Shift 0 = B, 8 = G, 16 = R, 24 = A) used directly as coverage.</summary>
        public Selection FromChannel(Sprite Src, int Shift, SelectMode Mode = SelectMode.Replace)
        {
            Check(Src);
            using var tmp = new Selection(w, h);
            var lr = Src.LockRect;                                   // like Wand: only the lock rect is selectable
            int l = w, t = h, r = 0, b = 0;
            for (int y = lr.Top; y < lr.Bottom; y++)
            {
                int* s = Src.PixelPtr + (long)y * w; byte* d = tmp.p + (long)y * w; bool any = false;
                for (int x = lr.Left; x < lr.Right; x++) { byte v = (byte)((uint)s[x] >> Shift); d[x] = v; if (v != 0) { any = true; if (x < l) l = x; if (x + 1 > r) r = x + 1; } }
                if (any) { if (y < t) t = y; b = y + 1; }
            }
            tmp.bl = l; tmp.bt = t; tmp.br = r; tmp.bb = b;
            return Combine(tmp, Mode);
        }
        /// <summary>Selects pixels whose luma (0..255) is within [Min, Max] (threshold / "select dark areas").</summary>
        public Selection ByLuma(Sprite Src, int Min, int Max, SelectMode Mode = SelectMode.Replace)
        {
            Check(Src);
            using var tmp = new Selection(w, h);
            var lr = Src.LockRect;
            int l = w, t = h, r = 0, b = 0;
            for (int y = lr.Top; y < lr.Bottom; y++)
            {
                int* s = Src.PixelPtr + (long)y * w; byte* d = tmp.p + (long)y * w; bool any = false;
                for (int x = lr.Left; x < lr.Right; x++)
                {
                    uint c = (uint)s[x]; int yv = (int)((((c >> 16) & 255) * 77 + ((c >> 8) & 255) * 150 + (c & 255) * 29) >> 8);
                    if (yv >= Min && yv <= Max) { d[x] = 255; any = true; if (x < l) l = x; if (x + 1 > r) r = x + 1; }
                }
                if (any) { if (y < t) t = y; b = y + 1; }
            }
            tmp.bl = l; tmp.bt = t; tmp.br = r; tmp.bb = b;
            return Combine(tmp, Mode);
        }

        // ------------------------------------------------------------------ combining / shaping
        /// <summary>Combines <paramref name="Other"/> (same size) into this selection.</summary>
        public Selection Combine(Selection Other, SelectMode Mode)
        {
            if (Other.w != w || Other.h != h) throw new ArgumentException("Selection sizes differ.", nameof(Other));
            long n = (long)w * h; byte* a = p; byte* b = Other.p;
            Version++;
            switch (Mode)
            {
                case SelectMode.Replace: Buffer.MemoryCopy(b, a, n, n); bl = Other.bl; bt = Other.bt; br = Other.br; bb = Other.bb; return this;
                case SelectMode.Add: for (long i = 0; i < n; i++) a[i] = Math.Max(a[i], b[i]); Union(Other); return this;
                case SelectMode.Subtract: for (long i = 0; i < n; i++) a[i] = (byte)Math.Max(0, a[i] - b[i]); Shrink(); return this;
                case SelectMode.Intersect: for (long i = 0; i < n; i++) a[i] = Math.Min(a[i], b[i]); Shrink(); return this;
                default: for (long i = 0; i < n; i++) a[i] = (byte)Math.Abs(a[i] - b[i]); Union(Other); Shrink(); return this;
            }
        }
        public Selection Add(Selection Other) => Combine(Other, SelectMode.Add);
        public Selection Subtract(Selection Other) => Combine(Other, SelectMode.Subtract);
        public Selection Intersect(Selection Other) => Combine(Other, SelectMode.Intersect);
        public Selection Xor(Selection Other) => Combine(Other, SelectMode.Xor);
        /// <summary>Union (new selection).</summary>
        public static Selection operator |(Selection a, Selection b) => a.Clone().Combine(b, SelectMode.Add);
        /// <summary>Intersection (new selection).</summary>
        public static Selection operator &(Selection a, Selection b) => a.Clone().Combine(b, SelectMode.Intersect);
        /// <summary>Difference a minus b (new selection).</summary>
        public static Selection operator -(Selection a, Selection b) => a.Clone().Combine(b, SelectMode.Subtract);
        /// <summary>Symmetric difference (new selection).</summary>
        public static Selection operator ^(Selection a, Selection b) => a.Clone().Combine(b, SelectMode.Xor);
        /// <summary>Inverse (new selection).</summary>
        public static Selection operator ~(Selection a) => a.Clone().Invert();

        void Union(Selection o) { if (o.IsEmpty) return; if (IsEmpty) { bl = o.bl; bt = o.bt; br = o.br; bb = o.bb; return; } bl = Math.Min(bl, o.bl); bt = Math.Min(bt, o.bt); br = Math.Max(br, o.br); bb = Math.Max(bb, o.bb); }
        /// <summary>Recomputes the bounding box inside the current one (after an operation that can only remove coverage).</summary>
        void Shrink()
        {
            int l = w, t = h, r = 0, b = 0;
            for (int y = Math.Max(0, bt); y < Math.Min(h, bb); y++)
            {
                byte* row = p + (long)y * w; int x0 = Math.Max(0, bl), x1 = Math.Min(w, br), first = -1, last = -1;
                for (int x = x0; x < x1; x++) if (row[x] != 0) { first = x; break; }
                if (first < 0) continue;
                for (int x = x1 - 1; x >= first; x--) if (row[x] != 0) { last = x; break; }
                if (first < l) l = first; if (last + 1 > r) r = last + 1; if (y < t) t = y; b = y + 1;
            }
            bl = l; bt = t; br = r; bb = b;
        }

        /// <summary>Soft edge: blurs the coverage by <paramref name="Radius"/> px (a Gaussian-like falloff on both sides of the edge).</summary>
        public Selection Feather(int Radius)
        {
            if (Radius <= 0 || IsEmpty) return this;
            using var s = ToSprite();
            s.Blur(Radius);
            FromChannel(s, 24, SelectMode.Replace);
            return this;
        }
        /// <summary>Grows (positive) or shrinks (negative) the selection by <paramref name="Pixels"/> (round structuring element).</summary>
        public Selection Grow(int Pixels)
        {
            if (Pixels == 0 || IsEmpty) return this;
            using var s = ToSprite();
            using var d = new Sprite(w, h, SR2D.Op.AlphaOver); d.ClearBuffer(0);
            var fx = Pixels > 0 ? new Effects().Dilate(Pixels) : new Effects().Erode(-Pixels);
            d.DrawFx(s, 0, 0, fx, SR2D.Op.Paint);
            FromChannel(d, 24, SelectMode.Replace);
            return this;
        }
        /// <summary>Keeps only the outline of the selection, <paramref name="Thickness"/> px wide (grow - shrink).</summary>
        public Selection Border(int Thickness)
        {
            if (Thickness <= 0 || IsEmpty) return this;
            using var inner = Clone(); inner.Grow(-Thickness);
            Grow(Thickness);
            return Combine(inner, SelectMode.Subtract);
        }

        // ------------------------------------------------------------------ as pixels
        /// <summary>
        /// The selection as horizontal runs (coverage &gt;= <paramref name="Threshold"/>), top to
        /// bottom, left to right. Cached until the selection changes. This is THE way to get at
        /// "the selected pixels": each run is a contiguous stretch of one sprite row, so
        /// <c>sprite.Row(run.Y).Slice(run.X, run.Length)</c> is a real <c>Span&lt;int&gt;</c> over
        /// them - no copy (see <see cref="Sprite.Runs(Selection,int)"/> for the ready-made loop).
        /// </summary>
        public ReadOnlySpan<Run> Runs(int Threshold = 1)
        {
            EnsureRuns(Threshold);
            return new ReadOnlySpan<Run>(runs, 0, runCount);
        }
        /// <summary>Number of runs (rows x segments) at the given threshold.</summary>
        public int RunCount(int Threshold = 1) { EnsureRuns(Threshold); return runCount; }
        /// <summary>Number of pixels with coverage &gt;= Threshold (default: same as <see cref="Count"/>).</summary>
        public int PixelCount(int Threshold = 1) { EnsureRuns(Threshold); int n = 0; for (int i = 0; i < runCount; i++) n += runs![i].Length; return n; }

        /// <summary>All selected pixel coordinates (coverage &gt;= Threshold), row-major. Allocates Count points - prefer <see cref="Runs"/> for big selections.</summary>
        public Point[] Points(int Threshold = 1)
        {
            var r = Runs(Threshold); int n = 0; foreach (var ru in r) n += ru.Length;
            var pts = new Point[n]; int k = 0;
            foreach (var ru in r) for (int x = ru.X; x < ru.End; x++) pts[k++] = new Point(x, ru.Y);
            return pts;
        }
        /// <summary>All selected pixel indices (y * Width + x), row-major - the cheapest flat form (one int per pixel).</summary>
        public int[] Indices(int Threshold = 1)
        {
            var r = Runs(Threshold); int n = 0; foreach (var ru in r) n += ru.Length;
            var idx = new int[n]; int k = 0;
            foreach (var ru in r) { int b = ru.Y * w + ru.X; for (int i = 0; i < ru.Length; i++) idx[k++] = b + i; }
            return idx;
        }

        void EnsureRuns(int threshold)
        {
            if (threshold < 1) threshold = 1; if (threshold > 255) threshold = 255;
            if (runVersion == Version && runThreshold == threshold && runs != null) return;
            Run[] r = runs ?? new Run[1024]; int n = 0;
            if (!IsEmpty)
            {
                for (int y = bt; y < bb; y++)
                {
                    byte* row = p + (long)y * w; int x = bl, end = br;
                    while (x < end)
                    {
                        // skip unselected: 8 bytes at a time when all are below threshold (fast exact test for threshold 1: word == 0)
                        if (threshold == 1) { while (x + 8 <= end && *(ulong*)(row + x) == 0) x += 8; }
                        while (x < end && row[x] < threshold) x++;
                        if (x >= end) break;
                        int x0 = x;
                        if (threshold == 1) { while (x + 8 <= end && *(ulong*)(row + x) == 0xFFFFFFFFFFFFFFFFul) x += 8; }
                        while (x < end && row[x] >= threshold) x++;
                        if (n == r.Length) Array.Resize(ref r, r.Length * 2);
                        r[n++] = new Run(y, x0, x - x0);
                    }
                }
            }
            runs = r; runCount = n; runVersion = Version; runThreshold = threshold;
        }

        // ------------------------------------------------------------------ display
        /// <summary>
        /// Shows the selection on <paramref name="Target"/>: a translucent tint over the selected
        /// pixels (<paramref name="Tint"/> ARGB, 0 = none) and "marching ants" along the edge -
        /// a dashed black/white outline whose dashes move with <paramref name="Phase"/> (pass a
        /// growing value, e.g. time * 20). <paramref name="OffsetX"/>/<paramref name="OffsetY"/>
        /// = where the selection's sprite sits on Target. Edge pixels are cached until the
        /// selection changes, so per-frame cost is the tint fill plus one SetPixel per edge pixel.
        /// </summary>
        public void Draw(Sprite Target, float Phase = 0, int Tint = 0x5000A0FF, bool Ants = true, int OffsetX = 0, int OffsetY = 0, int DashLen = 4,
                         int AntColorA = unchecked((int)0xFF000000), int AntColorB = unchecked((int)0xFFFFFFFF))
        {
            if (IsEmpty) return;
            if (Tint != 0 && (Tint >> 24) != 0)
            {
                if (OffsetX == 0 && OffsetY == 0 && Target.Width == w && Target.Height == h) Target.Fill(this, Tint, SR2D.LineOp.AlphaBlend);
                else
                {   // offset: fill through a temporary view of the overlap
                    var r = Rectangle.Intersect(new Rectangle(OffsetX + bl, OffsetY + bt, br - bl, bb - bt), Target.LockRect);
                    if (r.Width > 0 && r.Height > 0)
                        _ = SR2D.Native.FillMask8(Target.PixelPtr, Target.Width, r.Left, r.Top, r.Right, r.Bottom, p - ((long)OffsetY * w + OffsetX), w, Tint, (int)SR2D.LineOp.AlphaBlend, 128);   // mask base shifted so (x, y) in target space reads mask (x - ox, y - oy)
                }
            }
            if (Ants) DrawOutline(Target, Phase, OffsetX, OffsetY, DashLen, AntColorA, AntColorB);
        }

        /// <summary>Marching-ants outline only (see <see cref="Draw"/>). <paramref name="AntColorB"/> 0 = single-colour dashes with gaps.</summary>
        public void DrawOutline(Sprite Target, float Phase = 0, int OffsetX = 0, int OffsetY = 0, int DashLen = 4,
                                int AntColorA = unchecked((int)0xFF000000), int AntColorB = unchecked((int)0xFFFFFFFF))
        {
            if (IsEmpty) return;
            EnsureEdge();
            if (DashLen < 1) DashLen = 1;
            int ph = (int)Math.Floor(Phase);
            var lr = Target.LockRect; int L = lr.Left, T = lr.Top, R = lr.Right, B = lr.Bottom, tw = Target.Width; int* tp = Target.PixelPtr;
            int[] e = edge!;
            for (int i = 0; i < edgeCount; i += 2)
            {
                int x = e[i], y = e[i + 1];
                bool second = (((x + y + ph) / DashLen) & 1) != 0;   // diagonal dash pattern, moves along the edge with Phase
                if (second && AntColorB == 0) continue;
                x += OffsetX; y += OffsetY;
                if (x < L || x >= R || y < T || y >= B) continue;
                tp[(long)y * tw + x] = second ? AntColorB : AntColorA;
            }
        }

        /// <summary>Number of edge pixels (selected pixels with an unselected 4-neighbour or at the border); for debugging / ants cost.</summary>
        public int EdgeCount { get { if (IsEmpty) return 0; EnsureEdge(); return edgeCount / 2; } }

        /// <summary>Edge pixels as points (selected with coverage >= 128 and touching a pixel < 128 or the image border).</summary>
        public Point[] EdgePoints()
        {
            if (IsEmpty) return Array.Empty<Point>();
            EnsureEdge();
            var pts = new Point[edgeCount / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Point(edge![2 * i], edge[2 * i + 1]);
            return pts;
        }

        void EnsureEdge()
        {
            if (edgeVersion == Version && edge != null) return;
            int n = 0; int[] e = edge ?? new int[4096];
            for (int y = bt; y < bb; y++)
            {
                byte* row = p + (long)y * w, up = y > 0 ? row - w : null, dn = y + 1 < h ? row + w : null;
                for (int x = bl; x < br; x++)
                {
                    if (row[x] < 128) continue;
                    bool edgePx = x == 0 || x == w - 1 || y == 0 || y == h - 1 || row[x - 1] < 128 || row[x + 1] < 128 || up![x] < 128 || dn![x] < 128;
                    if (!edgePx) continue;
                    if (n + 2 > e.Length) Array.Resize(ref e, e.Length * 2);
                    e[n++] = x; e[n++] = y;
                }
            }
            edge = e; edgeCount = n; edgeVersion = Version;
        }

        /// <summary>Coverage as a premultiplied white sprite (alpha = coverage) - to draw the marching-ants area, tint it, or store it.</summary>
        public Sprite ToSprite()
        {
            var s = new Sprite(w, h, SR2D.Op.AlphaOver);
            int* d = s.PixelPtr; byte* q = p;
            for (long i = (long)w * h; i > 0; --i, ++d, ++q) { uint v = *q; *d = (int)(v * 0x01010101u); }
            return s;
        }
        // ---- in-place geometry (mirrors the Sprite editors: FlipX / FlipY / Rotate90 / Shift)
        /// <summary>Mirrors the selection left-right, in place.</summary>
        public Selection FlipX()
        {
            for (int y = 0; y < h; y++) { byte* row = p + (long)y * w; for (int a = 0, b = w - 1; a < b; a++, b--) { byte t = row[a]; row[a] = row[b]; row[b] = t; } }
            int nl = w - br, nr = w - bl; bl = nl; br = nr; Version++; return this;
        }
        /// <summary>Mirrors the selection top-bottom, in place.</summary>
        public Selection FlipY()
        {
            Span<byte> tmp = w <= 4096 ? stackalloc byte[w] : new byte[w];
            for (int a = 0, b = h - 1; a < b; a++, b--)
            {
                var ra = new Span<byte>(p + (long)a * w, w); var rb = new Span<byte>(p + (long)b * w, w);
                ra.CopyTo(tmp); rb.CopyTo(ra); tmp.CopyTo(rb);
            }
            int nt = h - bb, nb = h - bt; bt = nt; bb = nb; Version++; return this;
        }
        /// <summary>Turns the selection by quarter turns clockwise (1 = 90° CW, -1 = CCW, 2 = 180°), in place; width and height swap for odd turns.</summary>
        public Selection Rotate90(int QuarterTurns = 1)
        {
            QuarterTurns = ((QuarterTurns % 4) + 4) % 4;
            if (QuarterTurns == 0) return this;
            if (QuarterTurns == 2) return FlipX().FlipY();
            int nw = h, nh = w;
            byte* q = (byte*)NativeMemory.AllocZeroed((nuint)((long)nw * nh));
            for (int y = 0; y < h; y++)
            {
                byte* row = p + (long)y * w;
                if (QuarterTurns == 1) for (int x = 0; x < w; x++) q[(long)x * nw + (h - 1 - y)] = row[x];       // (x, y) -> (h-1-y, x)
                else for (int x = 0; x < w; x++) q[(long)(w - 1 - x) * nw + y] = row[x];                        // (x, y) -> (y, w-1-x)
            }
            NativeMemory.Free(p); p = q; w = nw; h = nh;
            return Invalidate();
        }
        /// <summary>Moves the selection by (dx, dy); with <paramref name="Wrap"/> what leaves comes back on the other side, otherwise it is dropped.</summary>
        public Selection Shift(int dx, int dy, bool Wrap = false)
        {
            if (dx == 0 && dy == 0) return this;
            byte* q = (byte*)NativeMemory.AllocZeroed((nuint)((long)w * h));
            for (int y = 0; y < h; y++)
            {
                int ty = y + dy; if (Wrap) ty = ((ty % h) + h) % h; else if ((uint)ty >= (uint)h) continue;
                byte* src = p + (long)y * w, dst = q + (long)ty * w;
                for (int x = 0; x < w; x++)
                {
                    int tx = x + dx; if (Wrap) tx = ((tx % w) + w) % w; else if ((uint)tx >= (uint)w) continue;
                    dst[tx] = src[x];
                }
            }
            NativeMemory.Free(p); p = q;
            return Invalidate();
        }

        public Selection Clone() { var c = new Selection(w, h); Buffer.MemoryCopy(p, c.p, (long)w * h, (long)w * h); c.bl = bl; c.bt = bt; c.br = br; c.bb = bb; return c; }

        void Check(Sprite s) { if (disposed) throw new ObjectDisposedException(nameof(Selection)); if (s.Width != w || s.Height != h) throw new ArgumentException("Sprite and selection sizes differ."); }
        internal Rectangle ClipTo(Rectangle lockRect) => Rectangle.Intersect(Bounds, lockRect);

        public void Dispose() { if (disposed) return; disposed = true; if (p != null) { NativeMemory.Free(p); p = null; } GC.SuppressFinalize(this); }
        ~Selection() { if (!disposed && p != null) NativeMemory.Free(p); }   // a forgotten Dispose must not leak the mask (VoxelSelection carries the same finalizer)
    }

    public unsafe partial class Sprite
    {
        /// <summary>
        /// Bucket fill: colours the region connected to (x, y) whose colour is within
        /// <paramref name="Tolerance"/> (0..255) of the pixel under (x, y). <paramref name="Contiguous"/>
        /// false = replace that colour everywhere in the lock rect. <paramref name="Op"/> is a line/shape op
        /// (Set, AlphaBlend, Blend(k), Add, Max, Min, AlphaOver, Xor); <paramref name="Soft"/> anti-aliases the
        /// edge on gradients. Returns the number of pixels filled. The comparison is done against the
        /// image as it was before the fill, so the result is independent of the op.
        /// </summary>
        public int FloodFill(int x, int y, int c, int Tolerance = 0, bool Contiguous = true, SR2D.LineOp Op = SR2D.LineOp.Set,
                             bool Diagonal = false, bool Soft = false, bool IgnoreAlpha = false, int BlendFactor = 128)
        {
            if (meRight <= meLeft || meBottom <= meTop) return 0;
            byte* mask = FloodScratch(meWidth * meHeight);
            int flags = (Contiguous ? 0 : 1) | (IgnoreAlpha ? 2 : 0) | (Diagonal ? 4 : 0) | (Soft ? 8 : 0);
            int* bbox = stackalloc int[4];
            int n = SR2D.Native.FloodMask(pBuf, meWidth, meHeight, meLeft, meTop, meRight, meBottom, x, y, 0, Tolerance, flags, mask, meWidth, bbox);
            if (n <= 0) return 0;
            _ = SR2D.Native.FillMask8(pBuf, meWidth, bbox[0], bbox[1], bbox[2], bbox[3], mask, meWidth, c, OpWord(Op), BlendFactor);
            return n;
        }
        public int FloodFill(System.Drawing.Point At, int c, int Tolerance = 0, bool Contiguous = true, SR2D.LineOp Op = SR2D.LineOp.Set)
            => FloodFill(At.X, At.Y, c, Tolerance, Contiguous, Op);

        /// <summary>Same as <see cref="FloodFill(int,int,int,int,bool,SR2D.LineOp,bool,bool,bool,int)"/> but fills every pixel within Tolerance of <paramref name="MatchColor"/> (ARGB) rather than the seed pixel.</summary>
        public int ReplaceColor(int MatchColor, int c, int Tolerance = 0, SR2D.LineOp Op = SR2D.LineOp.Set, bool IgnoreAlpha = false, int BlendFactor = 128)
        {
            if (meRight <= meLeft || meBottom <= meTop) return 0;
            byte* mask = FloodScratch(meWidth * meHeight);
            int* bbox = stackalloc int[4];
            int n = SR2D.Native.FloodMask(pBuf, meWidth, meHeight, meLeft, meTop, meRight, meBottom, 0, 0, MatchColor, Tolerance, 1 | 16 | (IgnoreAlpha ? 2 : 0), mask, meWidth, bbox);
            if (n <= 0) return 0;
            _ = SR2D.Native.FillMask8(pBuf, meWidth, bbox[0], bbox[1], bbox[2], bbox[3], mask, meWidth, c, OpWord(Op), BlendFactor);
            return n;
        }

        /// <summary>Colours the selected pixels (coverage scales the op like anti-aliasing). Returns pixels touched.</summary>
        public int Fill(Selection Sel, int c, SR2D.LineOp Op = SR2D.LineOp.Set, int BlendFactor = 128)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight) throw new ArgumentException("Selection size differs from the sprite.", nameof(Sel));
            var r = Sel.ClipTo(LockRect); if (r.Width <= 0 || r.Height <= 0) return 0;
            return SR2D.Native.FillMask8(pBuf, meWidth, r.Left, r.Top, r.Right, r.Bottom, Sel.Ptr, meWidth, c, OpWord(Op), BlendFactor);
        }

        /// <summary>
        /// Restricts any drawing to the selection: <paramref name="Draw"/> receives a copy of the
        /// selection's bounding box (same coordinates as this sprite - its lock rect is set to
        /// the box), draws whatever it likes, and the result is blended back with the coverage
        /// (fully selected pixels replaced, unselected untouched, soft edges mixed).
        /// Cost: one copy + one lerp of the bounding box, whatever the callback does.
        /// </summary>
        public void Apply(Selection Sel, Action<Sprite> Draw)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight) throw new ArgumentException("Selection size differs from the sprite.", nameof(Sel));
            var r = Sel.ClipTo(LockRect); if (r.Width <= 0 || r.Height <= 0) return;
            using var copy = new Sprite(meWidth, meHeight, Op);
            Buffer.MemoryCopy(pBuf, copy.pBuf, (long)meWidth * meHeight * 4, (long)meWidth * meHeight * 4);   // full copy keeps coordinates identical (callback may read outside the box)
            copy.SetLockRect(r);
            Draw(copy);
            long off = (long)r.Top * meWidth + r.Left;
            SR2D.Native.LerpMask8(copy.pBuf + off, pBuf + off, Sel.Ptr + off, r.Width, r.Height, meWidth, meWidth, meWidth, 0);
        }

        /// <summary>Copies the selected pixels of <paramref name="Src"/> (same size) onto this sprite by coverage (paste through a selection / masked copy).</summary>
        public void CopyThrough(Selection Sel, Sprite Src, bool Inverted = false)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight || Src.meWidth != meWidth || Src.meHeight != meHeight) throw new ArgumentException("Sizes differ.");
            var r = Inverted ? LockRect : Sel.ClipTo(LockRect); if (r.Width <= 0 || r.Height <= 0) return;
            long off = (long)r.Top * meWidth + r.Left;
            SR2D.Native.LerpMask8(Src.pBuf + off, pBuf + off, Sel.Ptr + off, r.Width, r.Height, meWidth, meWidth, meWidth, Inverted ? 1 : 0);
        }

        /// <summary>Extracts the selected pixels into a new premultiplied sprite (alpha = coverage * source alpha); everything else transparent. Use for "cut out the wand region".</summary>
        public Sprite Extract(Selection Sel)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight) throw new ArgumentException("Selection size differs from the sprite.", nameof(Sel));
            var s = new Sprite(meWidth, meHeight, SR2D.Op.AlphaOver); s.ClearBuffer(0);
            var r = Sel.ClipTo(LockRect); if (r.Width <= 0 || r.Height <= 0) return s;
            bool premul = Op == SR2D.Op.AlphaOver;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                int* src = pBuf + (long)y * meWidth, dst = s.pBuf + (long)y * meWidth; byte* m = Sel.Ptr + (long)y * meWidth;
                for (int x = r.Left; x < r.Right; x++)
                {
                    int cv = m[x]; if (cv == 0) continue;
                    uint c = (uint)src[x];
                    if (!premul) { uint a = c >> 24; c = (a << 24) | ((((c >> 16) & 255) * a / 255) << 16) | ((((c >> 8) & 255) * a / 255) << 8) | ((c & 255) * a / 255); }
                    if (cv < 255) { int wgt = cv + (cv >> 7); c = ((((c & 0x00ff00ffu) * (uint)wgt) >> 8) & 0x00ff00ffu) | ((((c >> 8) & 0x00ff00ffu) * (uint)wgt) & 0xff00ff00u); }
                    dst[x] = (int)c;
                }
            }
            return s;
        }

        // ------------------------------------------------------------------ selection -> spans of pixels
        /// <summary>One run of selected pixels inside a sprite row: where it is and a live span over its pixels.</summary>
        public readonly ref struct PixelRun
        {
            public readonly int X, Y;
            /// <summary>The pixels (0xAARRGGBB), writable, no copy. Valid while the sprite lives.</summary>
            public readonly Span<int> Pixels;
            public int Length => Pixels.Length;
            public PixelRun(int x, int y, Span<int> px) { X = x; Y = y; Pixels = px; }
        }
        /// <summary>Enumerates the selected pixels of this sprite as spans: <c>foreach (var run in canvas.Runs(sel)) { run.Pixels[i] ...; run.X; run.Y; }</c>.</summary>
        public readonly ref struct PixelRuns
        {
            readonly Sprite s; readonly ReadOnlySpan<Selection.Run> runs; readonly Rectangle clip;
            internal PixelRuns(Sprite sprite, ReadOnlySpan<Selection.Run> r, Rectangle c) { s = sprite; runs = r; clip = c; }
            /// <summary>Number of runs before lock-rect clipping (upper bound of what the loop yields).</summary>
            public int Count => runs.Length;
            public Enumerator GetEnumerator() => new Enumerator(s, runs, clip);
            public ref struct Enumerator
            {
                readonly Sprite s; readonly ReadOnlySpan<Selection.Run> runs; readonly Rectangle clip; int i, x0, x1;
                internal Enumerator(Sprite sprite, ReadOnlySpan<Selection.Run> r, Rectangle c) { s = sprite; runs = r; clip = c; i = -1; x0 = x1 = 0; }
                public bool MoveNext()
                {
                    while (++i < runs.Length) if (ClipRun(runs[i], clip, out x0, out x1)) return true;
                    return false;
                }
                public PixelRun Current { get { var r = runs[i]; return new PixelRun(x0, r.Y, new Span<int>(s.pBuf + (long)r.Y * s.meWidth + x0, x1 - x0)); } }
            }
        }
        /// <summary>Intersects a run with a rectangle; false when nothing is left.</summary>
        internal static bool ClipRun(in Selection.Run r, Rectangle c, out int x0, out int x1)
        {
            x0 = r.X < c.Left ? c.Left : r.X; x1 = r.End > c.Right ? c.Right : r.End;
            return r.Y >= c.Top && r.Y < c.Bottom && x1 > x0;
        }

        /// <summary>
        /// The selected pixels of this sprite as writable spans, one per horizontal run
        /// (coverage &gt;= <paramref name="Threshold"/>), zero copy:
        /// <code>
        /// foreach (var run in canvas.Runs(sel))
        ///     foreach (ref int px in run.Pixels) px |= unchecked((int)0xFF000000);   // e.g. force alpha
        /// </code>
        /// Run boundaries are cached in the selection until it changes. Like every other
        /// selection operation this is confined to the lock rect (selection ∩ lock rect).
        /// </summary>
        public PixelRuns Runs(Selection Sel, int Threshold = 1)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight) throw new ArgumentException("Selection size differs from the sprite.", nameof(Sel));
            return new PixelRuns(this, Sel.Runs(Threshold), LockRect);
        }

        /// <summary>Copies the selected pixels out into one flat array (row-major order of <see cref="Selection.Runs"/>) - for histograms, parsing, or processing followed by <see cref="SetPixels"/>.</summary>
        public int[] GetPixels(Selection Sel, int Threshold = 1)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight) throw new ArgumentException("Selection size differs from the sprite.", nameof(Sel));
            var runs = Sel.Runs(Threshold); var lr = LockRect; int n = 0;
            foreach (var r in runs) if (ClipRun(r, lr, out int a, out int b)) n += b - a;
            var outp = new int[n]; int k = 0;
            foreach (var r in runs) if (ClipRun(r, lr, out int a, out int b)) { new ReadOnlySpan<int>(pBuf + (long)r.Y * meWidth + a, b - a).CopyTo(outp.AsSpan(k)); k += b - a; }
            return outp;
        }
        /// <summary>Writes a flat array back into the selected pixels (same order as <see cref="GetPixels"/>). Returns pixels written (min of both counts).</summary>
        public int SetPixels(Selection Sel, ReadOnlySpan<int> Values, int Threshold = 1)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight) throw new ArgumentException("Selection size differs from the sprite.", nameof(Sel));
            int k = 0; var lr = LockRect;
            foreach (var r in Sel.Runs(Threshold))
            {
                if (!ClipRun(r, lr, out int a, out int b)) continue;
                int len = Math.Min(b - a, Values.Length - k); if (len <= 0) break;
                Values.Slice(k, len).CopyTo(new Span<int>(pBuf + (long)r.Y * meWidth + a, len)); k += len;
            }
            return k;
        }
        /// <summary>Average colour (ARGB, per channel) of the selected pixels; 0 when nothing is selected.</summary>
        public int AverageColor(Selection Sel, int Threshold = 1)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight) throw new ArgumentException("Selection size differs from the sprite.", nameof(Sel));
            long a = 0, r = 0, g = 0, b = 0, n = 0; var lr = LockRect;
            foreach (var run in Sel.Runs(Threshold))
            {
                if (!ClipRun(run, lr, out int x0, out int x1)) continue;
                int* px = pBuf + (long)run.Y * meWidth + x0;
                for (int i = 0; i < x1 - x0; i++) { uint c = (uint)px[i]; a += c >> 24; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; }
                n += x1 - x0;
            }
            if (n == 0) return 0;
            return SR2D.ARGB((byte)(a / n), (byte)(r / n), (byte)(g / n), (byte)(b / n));
        }

        /// <summary>Sets the lock rect to the selection's bounding box (the cheap rectangular approximation of "draw only inside the selection"; use <see cref="Apply"/> for the exact shape). Empty selection = whole sprite.</summary>
        public void SetLockRect(Selection Sel) => SetLockRect(Sel.Bounds);

        // per-thread mask scratch for FloodFill / ReplaceColor (one byte per pixel, reused). The block lives in a
        // finalizable holder so a worker thread that ends gives its scratch back (a bare [ThreadStatic] pointer would leak it).
        sealed class FloodScratchHolder
        {
            public byte* P; public int Len;
            ~FloodScratchHolder() { if (P != null) { NativeMemory.Free(P); P = null; } }
        }
        [ThreadStatic] static FloodScratchHolder? floodScratch;
        static byte* FloodScratch(int n)
        {
            var h = floodScratch ??= new FloodScratchHolder();
            if (h.Len < n)
            {
                if (h.P != null) NativeMemory.Free(h.P);
                h.P = (byte*)NativeMemory.Alloc((nuint)n); h.Len = n;
            }
            return h.P;
        }
    }
}
