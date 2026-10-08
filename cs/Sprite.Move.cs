// SR2D - moving and offsetting the contents of a selection within one sprite:
// Move   = the Photoshop Move tool (cut the selected pixels, paste them Dx/Dy away, the source turns transparent),
// Offset = Photoshop Filter > Other > Offset restricted to a selection (the content redistributes inside the selection,
//          wrapping at the bounding box like Photoshop, or - for non-rectangular selections - along the selected spans).
// Both are mask-weighted: a feathered selection moves / offsets with soft edges, every formula scales all four
// premultiplied BGRA channels linearly by the coverage byte, exactly like Fill / Extract.
using System;
using System.Drawing;

namespace Sr2d64CSport
{
    /// <summary>How <see cref="Sprite.Offset(Sr2d64CSport.Selection, int, int, Sr2d64CSport.SelOffsetWrap)"/> routes the content that leaves the selection.</summary>
    public enum SelOffsetWrap
    {
        /// <summary>Photoshop behaviour: the content wraps around the selection's bounding box (a torus over that rectangle); a pixel that routes onto an unselected pixel is masked away.</summary>
        BoundingBox,
        /// <summary>Non-rectangular selections: the content wraps within the selected spans instead - horizontally inside the selected run of its row, vertically inside the selected run of its column - so nothing ever jumps across an unselected gap. A rectangle behaves exactly like <see cref="SelOffsetWrap.BoundingBox"/>.</summary>
        Spans,
    }

    /// <summary>The maximal runs of covered pixels along every row (horizontal) or every column (vertical) of a box, with per-line lookup: <see cref="Route"/> wraps a coordinate within the run of its line.</summary>
    internal sealed class SpanTable
    {
        readonly int[] start, len, off; readonly int outer;
        /// <summary>Builds the table for <paramref name="Sel"/> over <paramref name="b"/>: lines are rows (horizontal) / columns (vertical), along the scan direction.</summary>
        public SpanTable(Selection Sel, Rectangle b, bool horizontal)
        {
            outer = horizontal ? b.Height : b.Width;
            int along = horizontal ? b.Width : b.Height;
            var st = new System.Collections.Generic.List<int>(); var ln = new System.Collections.Generic.List<int>();
            off = new int[outer + 1];
            for (int i = 0; i < outer; i++)
            {
                off[i] = st.Count;
                int run = 0;
                int o = horizontal ? b.Left : b.Top;                          // run starts are stored ABSOLUTE (Route's t is absolute)
                for (int j = 0; j < along; j++)
                {
                    bool on = horizontal ? Sel.Coverage(o + j, b.Top + i) != 0 : Sel.Coverage(b.Left + i, o + j) != 0;
                    if (on) run++;
                    else if (run > 0) { st.Add(o + j - run); ln.Add(run); run = 0; }
                }
                if (run > 0) { st.Add(o + along - run); ln.Add(run); }
            }
            off[outer] = st.Count;
            start = st.ToArray(); len = ln.ToArray();
        }
        /// <summary>The coordinate <paramref name="t"/> (absolute, on line <paramref name="line"/>) shifted by <paramref name="delta"/> and wrapped within the covered run of that line.</summary>
        public int Route(int line, int t, int delta)
        {
            int s = off[line], e = off[line + 1];
            for (int i = s; i < e; i++)
                if (t >= start[i] && t < start[i] + len[i])
                    return start[i] + ((t - start[i] - delta) % len[i] + len[i]) % len[i];
            return t;                                                                  // uncovered coordinate: route to itself (no destination weight anyway)
        }
    }

    public unsafe partial class Sprite
    {
        /// <summary>
        /// The Photoshop Move tool inside the same sprite: the selected pixels (the coverage IS the mask, so a feathered
        /// selection moves with soft edges) are pasted <paramref name="Dx"/> / <paramref name="Dy"/> pixels away and their
        /// source is faded out by the same coverage - what was under them becomes transparent. The marquee itself does not
        /// move. Content pushed outside the sprite (or the lock rect) is clipped away, like dragging over the canvas edge.
        /// Overlapping source and destination are handled (the source is snapshotted first). Fully opaque pixels survive the
        /// trip unchanged; the vacated area is premultiplied-transparent. Returns the number of selected pixels.
        /// </summary>
        public int Move(Selection Sel, int Dx, int Dy)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight) throw new ArgumentException("Selection size differs from the sprite.", nameof(Sel));
            if ((Dx == 0 && Dy == 0) || Empty || RectEmpty) return 0;
            var r = Sel.ClipTo(LockRect); if (r.Width <= 0 || r.Height <= 0) return 0;
            int bw = r.Width, bh = r.Height;
            int[] cc = new int[(long)bw * bh]; byte[] ww = new byte[(long)bw * bh];   // snapshot: weighted content + coverage
            long k = 0; int n = 0;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                int* src = pBuf + (long)y * meWidth; byte* m = Sel.Ptr + (long)y * meWidth;
                for (int x = r.Left; x < r.Right; x++, k++)
                {
                    int wgt = m[x]; ww[k] = (byte)wgt;
                    cc[k] = wgt == 0 ? 0 : ScalePix(src[x], wgt);
                    if (wgt != 0) n++;
                }
            }
            k = 0;
            for (int y = r.Top; y < r.Bottom; y++)                                    // 1) fade the source out by the coverage
            {
                int* src = pBuf + (long)y * meWidth; byte* m = Sel.Ptr + (long)y * meWidth;
                for (int x = r.Left; x < r.Right; x++, k++)
                    if (ww[k] != 0) src[x] = ScalePix(src[x], 255 - ww[k]);
            }
            k = 0;
            for (int y = r.Top; y < r.Bottom; y++)                                    // 2) paste at the offset, clipped
            {
                int ty = y + Dy; if (ty < meTop || ty >= meBottom) { k += bw; continue; }
                int* dst = pBuf + (long)ty * meWidth;
                for (int x = r.Left; x < r.Right; x++, k++)
                {
                    int tx = x + Dx; if (tx < meLeft || tx >= meRight) continue;
                    int wgt = ww[k]; if (wgt == 0) continue;
                    dst[tx] = OverPix(dst[tx], cc[k], 255 - wgt);
                }
            }
            if (meGdi) GdiFlush();
            return n;
        }

        /// <summary>
        /// Photoshop Filter &gt; Other &gt; Offset restricted to a selection: the selected content shifts by
        /// <paramref name="Dx"/> / <paramref name="Dy"/> and what leaves on one side comes back on the other -
        /// nothing is cleared, the pixels just redistribute inside the selection (the total content is kept).
        /// <paramref name="Wrap"/> routes the content: <see cref="SelOffsetWrap.BoundingBox"/> wraps around the
        /// selection's bounding box (Photoshop's behaviour; for a rectangle that is the strip itself),
        /// <see cref="SelOffsetWrap.Spans"/> wraps within the selected spans of each row / column so a non-rectangular
        /// selection never carries pixels across its unselected gaps. A pixel that routes onto an unselected pixel is
        /// masked away. Feathered selections carry their coverage with the content: the destination receives
        /// <c>source * weight + old * (1 - weight)</c>. The marquee does not move. Returns the pixels written.
        /// </summary>
        public int Offset(Selection Sel, int Dx, int Dy, SelOffsetWrap Wrap = SelOffsetWrap.BoundingBox)
        {
            if (Sel.Width != meWidth || Sel.Height != meHeight) throw new ArgumentException("Selection size differs from the sprite.", nameof(Sel));
            if ((Dx == 0 && Dy == 0) || Empty || RectEmpty) return 0;
            var b = Sel.Bounds; if (b.Width <= 0 || b.Height <= 0) return 0;
            int bw = b.Width, bh = b.Height;
            int[] cc = new int[(long)bw * bh]; byte[] ww = new byte[(long)bw * bh];   // snapshot of the whole bounding box
            long k = 0;
            for (int y = b.Top; y < b.Bottom; y++)
            {
                int* src = pBuf + (long)y * meWidth; byte* m = Sel.Ptr + (long)y * meWidth;
                for (int x = b.Left; x < b.Right; x++, k++)
                {
                    int wgt = m[x]; ww[k] = (byte)wgt;
                    cc[k] = wgt == 0 ? 0 : ScalePix(src[x], wgt);
                }
            }
            SpanTable? rows = Wrap == SelOffsetWrap.Spans && Dx != 0 ? new SpanTable(Sel, b, horizontal: true) : null;
            SpanTable? cols = Wrap == SelOffsetWrap.Spans && Dy != 0 ? new SpanTable(Sel, b, horizontal: false) : null;
            int written = 0;
            k = 0;
            for (int y = b.Top; y < b.Bottom; y++)
            {
                int* dst = pBuf + (long)y * meWidth; byte* md = Sel.Ptr + (long)y * meWidth;
                for (int x = b.Left; x < b.Right; x++, k++)
                {
                    if (md[x] == 0) continue;                                         // only selected pixels receive
                    int sx = rows != null ? rows.Route(y - b.Top, x, Dx) : b.Left + ((x - Dx - b.Left) % bw + bw) % bw;
                    int sy = cols != null ? cols.Route(x - b.Left, y, Dy) : b.Top + ((y - Dy - b.Top) % bh + bh) % bh;
                    long sk = (long)(sy - b.Top) * bw + (sx - b.Left);
                    int ws = ww[sk]; if (ws == 0) continue;                           // nothing to deliver
                    dst[x] = OverPix(dst[x], cc[sk], 255 - ws);
                    written++;
                }
            }
            if (meGdi) GdiFlush();
            return written;
        }

        /// <summary>All four premultiplied channels of <paramref name="c"/> scaled by <paramref name="w"/>/255 (rounded). Per-lane math: the packed (R << 16 | B) * w trick overflows a signed int.</summary>
        static int ScalePix(int c, int w)
        {
            int Lane(int sh) => ((((c >> sh) & 255) * w + 127) / 255) << sh;
            return Lane(24) | Lane(16) | Lane(8) | Lane(0);
        }
        /// <summary>dst = (dst0 * inv + weighted * 255 + 127) / 255 per channel - receiving <paramref name="weighted"/> content that already carries its coverage.</summary>
        static int OverPix(int dst0, int weighted, int inv)
        {
            int Lane(int sh) => Math.Min(255, (((((dst0 >> sh) & 255) * inv + ((weighted >> sh) & 255) * 255 + 127) / 255))) << sh;
            return Lane(24) | Lane(16) | Lane(8) | Lane(0);
        }
    }
}
