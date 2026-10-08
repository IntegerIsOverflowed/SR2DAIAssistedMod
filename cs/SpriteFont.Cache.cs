using System;
using System.Collections.Generic;
using System.Drawing;

namespace Sr2d64CSport
{
    // Text -> bitmap: SpriteFont.Render rasterises a line or a block once into a premultiplied sprite (Op AlphaOver,
    // transparent background) that is then blitted like any other sprite (one SIMD blit instead of one mask fill per
    // glyph, no layout, no kerning look-ups); TextCache keeps such bitmaps in an LRU keyed by everything that shapes
    // the picture (font, text, size, colour, wrap width, alignment, bold / italic / sub-pixel settings) under a byte
    // budget. Use it for text that is drawn every frame (HUDs, labels, read-outs): DrawString is right for text that
    // changes every frame, the cache for text that repeats. The pixel font (DrawText) is untouched.
    public sealed partial class SpriteFont
    {
        /// <summary>
        /// Renders one line of text into a new premultiplied sprite (<see cref="SR2D.Op.AlphaOver"/>, transparent
        /// background) sized to the line box (width = advance, height = ascent + descent) plus <paramref name="pad"/>
        /// pixels on every side (for glyphs that overhang the box: italics, swashes, the fake bold). The returned
        /// <see cref="RenderedText"/> carries the pen origin so the bitmap can be placed exactly where
        /// <see cref="Sprite.DrawString"/> would have put the text. Blit it with <see cref="SR2D.Op.AlphaOver"/> - the
        /// bitmap is premultiplied, so AlphaOver is right onto opaque and transparent targets alike (AlphaBlend would
        /// multiply the colour by the alpha a second time and darken the edges).
        /// </summary>
        public RenderedText Render(string text, float size, int color, int pad = 1)
        {
            if (string.IsNullOrEmpty(text) || size <= 0) return RenderedText.Empty;
            float width = Measure(text, size), asc = Ascent(size), desc = Descent(size);
            int extra = (int)MathF.Ceiling(fakeBold * size + MathF.Abs(fakeItalic) * (asc + desc)) + pad;
            int w = Math.Max(1, (int)MathF.Ceiling(width) + 2 * extra), h = Math.Max(1, (int)MathF.Ceiling(asc + desc) + 2 * pad);
            var s = new Sprite(w, h, SR2D.Op.AlphaOver);
            s.ClearBuffer(0);
            s.DrawStringBaseline(extra, pad + asc, text, this, size, color, SR2D.LineOp.AlphaOver);
            return new RenderedText(s, extra, pad, width, asc + desc, asc);
        }

        /// <summary>
        /// Renders a text block (see <see cref="Sprite.DrawStringBlock"/>: '\n' lines, word wrap to <paramref name="maxWidth"/>
        /// when positive, <paramref name="align"/> 0 left / 1 centre / 2 right) into a new premultiplied sprite.
        /// </summary>
        public RenderedText RenderBlock(string text, float size, int color, float maxWidth = 0, int align = 0, float lineSpacing = 1f, int pad = 1)
        {
            if (string.IsNullOrEmpty(text) || size <= 0) return RenderedText.Empty;
            List<string> lines = maxWidth > 0 ? Wrap(text, size, maxWidth) : new List<string>(text.Split('\n'));
            float lh = LineHeight(size) * lineSpacing, asc = Ascent(size), desc = Descent(size);
            float bw = 0; foreach (var l in lines) bw = MathF.Max(bw, Measure(l.TrimEnd('\r'), size));
            float bh = Math.Max(1, lines.Count) * lh;
            int extra = (int)MathF.Ceiling(fakeBold * size + MathF.Abs(fakeItalic) * (asc + desc)) + pad;
            int w = Math.Max(1, (int)MathF.Ceiling(bw) + 2 * extra), h = Math.Max(1, (int)MathF.Ceiling(bh + desc) + 2 * pad);
            var s = new Sprite(w, h, SR2D.Op.AlphaOver);
            s.ClearBuffer(0);
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i].TrimEnd('\r'); if (line.Length == 0) continue;
                float lw = Measure(line, size);
                s.DrawStringBaseline(extra + (bw - lw) * align / 2f, pad + i * lh + asc, line, this, size, color, SR2D.LineOp.AlphaOver);
            }
            return new RenderedText(s, extra, pad, bw, bh, asc);
        }

        /// <summary>Everything that changes the picture of a text rendered with this font (cache key part).</summary>
        internal long StyleKey => (long)BitConverter.SingleToInt32Bits(fakeBold) << 32 ^ (long)(uint)BitConverter.SingleToInt32Bits(fakeItalic) ^ ((long)subPos << 58) ^ (Kerning ? 1L << 57 : 0) ^ ((long)BitConverter.SingleToInt32Bits(LetterSpacing) << 20);
    }

    /// <summary>
    /// A text bitmap from <see cref="SpriteFont.Render"/>: the premultiplied <see cref="Bitmap"/> and where the text sits in
    /// it (<see cref="OriginX"/> / <see cref="OriginY"/> = the top-left of the line box inside the bitmap, so
    /// <c>dst.Draw(r.Bitmap, x - r.OriginX, y - r.OriginY, Op.AlphaOver)</c> puts the line box's top-left at (x, y) - the
    /// same place <c>dst.DrawString(x, y, ...)</c> with TopLeft anchor draws it). <see cref="DrawAt"/> does the anchor maths.
    /// </summary>
    public sealed class RenderedText : IDisposable
    {
        public static readonly RenderedText Empty = new RenderedText(null, 0, 0, 0, 0, 0);
        /// <summary>The premultiplied sprite (Op AlphaOver); null for empty text.</summary>
        public Sprite? Bitmap { get; }
        /// <summary>Top-left of the text box inside the bitmap.</summary>
        public int OriginX { get; }
        public int OriginY { get; }
        /// <summary>Line / block box in pixels (the advance width and ascent + descent, or the block size), as DrawString measures it.</summary>
        public float Width { get; }
        public float Height { get; }
        /// <summary>Baseline offset from the box top (first line).</summary>
        public float Ascent { get; }
        public bool IsEmpty => Bitmap == null;
        public long Bytes => Bitmap == null ? 0 : (long)Bitmap.Width * Bitmap.Height * 4 + 64;
        internal RenderedText(Sprite? bmp, int ox, int oy, float w, float h, float asc) { Bitmap = bmp; OriginX = ox; OriginY = oy; Width = w; Height = h; Ascent = asc; }
        /// <summary>Blits the text so that its box is anchored at (<paramref name="x"/>, <paramref name="y"/>) like <see cref="Sprite.DrawString"/> does. Returns the box.</summary>
        public RectangleF DrawAt(Sprite dst, float x, float y, TextAnchor anchor = TextAnchor.TopLeft, SR2D.Op op = SR2D.Op.AlphaOver)
        {
            if (Bitmap == null) return RectangleF.Empty;
            int ax = (int)anchor % 3, ay = (int)anchor / 3;
            float left = x - Width * ax / 2f, top = y - Height * ay / 2f;
            dst.Draw(Bitmap, (int)MathF.Round(left) - OriginX, (int)MathF.Round(top) - OriginY, op);
            return new RectangleF(left, top, Width, Height);
        }
        /// <summary>Blits with the pen start at (<paramref name="x"/>, <paramref name="baselineY"/>).</summary>
        public void DrawBaseline(Sprite dst, float x, float baselineY, SR2D.Op op = SR2D.Op.AlphaOver)
        {
            if (Bitmap == null) return;
            dst.Draw(Bitmap, (int)MathF.Round(x) - OriginX, (int)MathF.Round(baselineY - Ascent) - OriginY, op);
        }
        public void Dispose() { if (this != Empty) Bitmap?.Dispose(); }
    }

    /// <summary>
    /// LRU cache of rendered text bitmaps (<see cref="SpriteFont.Render"/>) under a byte budget. <see cref="Get"/> returns
    /// the cached bitmap for a (font, text, size, colour, layout) combination or renders and stores it; the least recently
    /// used entries are dropped when <see cref="Limit"/> is exceeded. Changing a font's FakeBold / FakeItalic /
    /// SubPixelPositions / Kerning / LetterSpacing changes the key, so stale pictures are never returned (the old entries
    /// age out). Not thread-safe: one cache per rendering thread, or lock around it. Disposing the cache disposes the
    /// bitmaps - do not keep a returned <see cref="RenderedText"/> beyond the frame (it may be evicted).
    /// </summary>
    internal sealed class TextCache : IDisposable
    {
        readonly struct Key : IEquatable<Key>
        {
            public readonly SpriteFont Font; public readonly string Text; public readonly float Size, MaxWidth, LineSpacing; public readonly int Color, Align, Pad; public readonly long Style; public readonly bool Block;
            public Key(SpriteFont f, string t, float s, int c, bool block, float mw, int al, float ls, int pad) { Font = f; Text = t; Size = s; Color = c; Block = block; MaxWidth = mw; Align = al; LineSpacing = ls; Pad = pad; Style = f.StyleKey; }
            public bool Equals(Key o) => ReferenceEquals(Font, o.Font) && Size == o.Size && Color == o.Color && Block == o.Block && MaxWidth == o.MaxWidth && Align == o.Align && LineSpacing == o.LineSpacing && Pad == o.Pad && Style == o.Style && string.Equals(Text, o.Text, StringComparison.Ordinal);
            public override bool Equals(object? obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(Font, Text.GetHashCode(StringComparison.Ordinal) /* per-process, fine for a cache */, Size, Color, Block ? MaxWidth + 1 : -MaxWidth, Align, Style, Pad);
        }
        sealed class Entry { public RenderedText Text = RenderedText.Empty; public LinkedListNode<Key> Node = null!; }
        readonly Dictionary<Key, Entry> map = new Dictionary<Key, Entry>();
        readonly LinkedList<Key> lru = new LinkedList<Key>();          // most recent at the front
        long bytes; bool disposed;

        /// <summary>Byte budget for the bitmaps (default 32 MB); the oldest entries go first.</summary>
        public long Limit { get; set; } = 32L << 20;
        /// <summary>Cap on the number of entries (default 4096) - many tiny strings would otherwise never reach the byte limit.</summary>
        public int MaxEntries { get; set; } = 4096;
        public long Bytes => bytes;
        public int Count => map.Count;
        /// <summary>Statistics since the last <see cref="ResetStats"/>.</summary>
        public long Hits { get; private set; }
        public long Misses { get; private set; }
        public void ResetStats() { Hits = Misses = 0; }

        /// <summary>The bitmap of one line (rendered on the first call).</summary>
        public RenderedText Get(SpriteFont font, string text, float size, int color, int pad = 1)
            => Fetch(new Key(font, text, size, color, false, 0, 0, 1f, pad));
        /// <summary>The bitmap of a block (see <see cref="SpriteFont.RenderBlock"/>).</summary>
        public RenderedText GetBlock(SpriteFont font, string text, float size, int color, float maxWidth = 0, int align = 0, float lineSpacing = 1f, int pad = 1)
            => Fetch(new Key(font, text, size, color, true, maxWidth, align, lineSpacing, pad));

        /// <summary>Cached DrawString: one line anchored at (x, y).</summary>
        public RectangleF DrawString(Sprite dst, float x, float y, string text, SpriteFont font, float size, int color, TextAnchor anchor = TextAnchor.TopLeft, SR2D.Op op = SR2D.Op.AlphaOver)
            => string.IsNullOrEmpty(text) || font == null || size <= 0 ? RectangleF.Empty : Get(font, text, size, color).DrawAt(dst, x, y, anchor, op);
        /// <summary>Cached DrawStringBlock.</summary>
        public RectangleF DrawStringBlock(Sprite dst, float x, float y, string text, SpriteFont font, float size, int color, float maxWidth = 0, int align = 0, TextAnchor anchor = TextAnchor.TopLeft, float lineSpacing = 1f, SR2D.Op op = SR2D.Op.AlphaOver)
            => string.IsNullOrEmpty(text) || font == null || size <= 0 ? RectangleF.Empty : GetBlock(font, text, size, color, maxWidth, align, lineSpacing).DrawAt(dst, x, y, anchor, op);

        RenderedText Fetch(Key k)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TextCache));
            if (map.TryGetValue(k, out var e))
            {
                Hits++;
                if (e.Node != lru.First) { lru.Remove(e.Node); lru.AddFirst(e.Node); }
                return e.Text;
            }
            Misses++;
            var r = k.Block ? k.Font.RenderBlock(k.Text, k.Size, k.Color, k.MaxWidth, k.Align, k.LineSpacing, k.Pad) : k.Font.Render(k.Text, k.Size, k.Color, k.Pad);
            e = new Entry { Text = r, Node = new LinkedListNode<Key>(k) };
            map[k] = e; lru.AddFirst(e.Node); bytes += r.Bytes;
            Trim(bytes > Limit || map.Count > MaxEntries);
            return r;
        }
        void Trim(bool needed)
        {
            if (!needed) return;
            while (lru.Count > 1 && (bytes > Limit || map.Count > MaxEntries))
            {
                var last = lru.Last!; lru.RemoveLast();
                if (map.Remove(last.Value, out var e)) { bytes -= e.Text.Bytes; e.Text.Dispose(); }
            }
        }
        /// <summary>Drops every entry (e.g. after replacing the font or on a theme change).</summary>
        public void Clear()
        {
            foreach (var e in map.Values) e.Text.Dispose();
            map.Clear(); lru.Clear(); bytes = 0;
        }
        /// <summary>Drops the entries rendered with <paramref name="font"/>.</summary>
        public void Remove(SpriteFont font)
        {
            var node = lru.First;
            while (node != null)
            {
                var next = node.Next;
                if (ReferenceEquals(node.Value.Font, font) && map.Remove(node.Value, out var e)) { bytes -= e.Text.Bytes; e.Text.Dispose(); lru.Remove(node); }
                node = next;
            }
        }
        public void Dispose() { if (disposed) return; disposed = true; Clear(); }
    }
}
