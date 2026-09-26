using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;

namespace Sr2d64CSport
{
    /// <summary>
    /// A raster picture used as a paint: the shape's path (normally the image rectangle) is filled by sampling
    /// <see cref="Argb"/> through <see cref="Matrix"/> (image pixel space, y down -> the shape's local space). Used for
    /// PDF image XObjects / inline images, SVG &lt;image&gt; elements and anything you build yourself (<see cref="FromSprite"/>).
    /// Straight (non-premultiplied) ARGB. Sampling is bilinear, box-prefiltered when the image is drawn smaller than it is
    /// (<see cref="Level"/> caches the reductions); <see cref="Smooth"/> = false keeps hard pixels when enlarged (bilevel masks).
    /// </summary>
    internal sealed class VectorImagePaint : VectorPaint
    {
        public int[] Argb; public int Width, Height;
        public Matrix3x2 Matrix = Matrix3x2.Identity;
        public bool Smooth = true;
        /// <summary>True when any pixel is not fully opaque (set by the decoders; used to skip alpha work).</summary>
        public bool HasAlpha;
        Dictionary<int, (int[] px, int w, int h)>? levels;
        public VectorImagePaint(int[] argb, int width, int height) { Argb = argb; Width = width; Height = height; }
        public override VectorPaint Clone() { var c = new VectorImagePaint(Argb, Width, Height) { Matrix = Matrix, Smooth = Smooth, HasAlpha = HasAlpha }; return c; }
        public override string ToString() => $"image {Width}x{Height}";
        /// <summary>Wraps a sprite's pixels (copied) - straight ARGB expected.</summary>
        public static VectorImagePaint FromSprite(Sprite s) { var px = new int[s.Width * s.Height]; s.Pixels.CopyTo(px); return new VectorImagePaint(px, s.Width, s.Height) { HasAlpha = true }; }
        /// <summary>Average colour (for palettes / previews).</summary>
        public int AverageArgb()
        {
            long a = 0, r = 0, g = 0, b = 0; int n = Argb.Length; if (n == 0) return 0; int step = Math.Max(1, n / 4096), cnt = 0;
            for (int i = 0; i < n; i += step) { int c = Argb[i]; a += (c >> 24) & 255; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; cnt++; }
            return (int)((a / cnt) << 24 | (r / cnt) << 16 | (g / cnt) << 8 | (b / cnt));
        }
        /// <summary>Box-reduced copy by an integer factor (1 = the image itself), cached.</summary>
        public (int[] px, int w, int h) Level(int k)
        {
            if (k <= 1) return (Argb, Width, Height);
            levels ??= new Dictionary<int, (int[], int, int)>();
            if (levels.TryGetValue(k, out var l)) return l;
            int w = Math.Max(1, Width / k), h = Math.Max(1, Height / k); var o = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = y * k, y1 = y == h - 1 ? Height : Math.Min(Height, y0 + k);
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * k, x1 = x == w - 1 ? Width : Math.Min(Width, x0 + k);
                    long a = 0, r = 0, g = 0, b = 0; int n = 0;
                    for (int yy = y0; yy < y1; yy++) { int row = yy * Width; for (int xx = x0; xx < x1; xx++) { int c = Argb[row + xx]; int ca = (c >> 24) & 255; a += ca; r += ((c >> 16) & 255) * ca; g += ((c >> 8) & 255) * ca; b += (c & 255) * ca; n++; } }
                    if (a == 0) { o[y * w + x] = 0; continue; }
                    o[y * w + x] = (int)((a / n) << 24 | (r / a) << 16 | (g / a) << 8 | (b / a));
                }
            }
            if (levels.Count > 4) levels.Clear();
            levels[k] = (o, w, h); return (o, w, h);
        }
        /// <summary>Encodes the picture as a PNG (RGBA, 8 bit) - for the SVG writer and for saving images out of a document.</summary>
        public byte[] ToPng()
        {
            using var ms = new MemoryStream();
            void Chunk(string type, byte[] data)
            {
                var len = BitConverter.GetBytes(data.Length); if (BitConverter.IsLittleEndian) Array.Reverse(len); ms.Write(len, 0, 4);
                var t = System.Text.Encoding.ASCII.GetBytes(type); ms.Write(t, 0, 4); ms.Write(data, 0, data.Length);
                uint crc = Crc32(t, 0, 4, 0xFFFFFFFF); crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFF; var cb = BitConverter.GetBytes(crc); if (BitConverter.IsLittleEndian) Array.Reverse(cb); ms.Write(cb, 0, 4);
            }
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var hdr = new byte[13]; void BE(int v, int o) { hdr[o] = (byte)(v >> 24); hdr[o + 1] = (byte)(v >> 16); hdr[o + 2] = (byte)(v >> 8); hdr[o + 3] = (byte)v; }
            BE(Width, 0); BE(Height, 4); hdr[8] = 8; hdr[9] = 6; Chunk("IHDR", hdr);
            var raw = new byte[(Width * 4 + 1) * Height];
            for (int y = 0; y < Height; y++) { int o = y * (Width * 4 + 1) + 1; for (int x = 0; x < Width; x++) { int c = Argb[y * Width + x]; raw[o++] = (byte)(c >> 16); raw[o++] = (byte)(c >> 8); raw[o++] = (byte)c; raw[o++] = (byte)(c >> 24); } }
            using (var zs = new MemoryStream())
            {
                using (var z = new ZLibStream(zs, CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
                Chunk("IDAT", zs.ToArray());
            }
            Chunk("IEND", Array.Empty<byte>());
            return ms.ToArray();
        }
        static uint[]? crcTable;
        static uint Crc32(byte[] d, int off, int len, uint crc)
        {
            if (crcTable == null) { var t = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; } crcTable = t; }
            for (int i = 0; i < len; i++) crc = crcTable[(crc ^ d[off + i]) & 255] ^ (crc >> 8);
            return crc;
        }
    }

    // =========================================================================================== PNG (for SVG <image>)
    internal static class PngDecoder
    {
        public static bool IsPng(byte[] d) => d.Length > 8 && d[0] == 137 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G';
        public static VectorImagePaint? Decode(byte[] d)
        {
            if (!IsPng(d)) return null;
            int p = 8, w = 0, h = 0, bpc = 8, ct = 0, interlace = 0; byte[]? pal = null, trns = null; using var idat = new MemoryStream();
            while (p + 8 <= d.Length)
            {
                int len = d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]; string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4); p += 8; if (len < 0 || p + len > d.Length) break;
                switch (type)
                {
                    case "IHDR": w = d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]; h = d[p + 4] << 24 | d[p + 5] << 16 | d[p + 6] << 8 | d[p + 7]; bpc = d[p + 8]; ct = d[p + 9]; interlace = d[p + 12]; break;
                    case "PLTE": pal = d.AsSpan(p, len).ToArray(); break;
                    case "tRNS": trns = d.AsSpan(p, len).ToArray(); break;
                    case "IDAT": idat.Write(d, p, len); break;
                }
                p += len + 4;
                if (type == "IEND") break;
            }
            if (w <= 0 || h <= 0 || interlace != 0 || (long)w * h > 80_000_000) return null;
            byte[] raw;
            using (var zin = new MemoryStream(idat.ToArray())) using (var z = new ZLibStream(zin, CompressionMode.Decompress)) using (var o = new MemoryStream()) { z.CopyTo(o); raw = o.ToArray(); }
            int channels = ct switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 1 };
            int bpp = Math.Max(1, channels * bpc / 8), stride = (w * channels * bpc + 7) / 8;
            var img = PdfImageDecoder.Unfilter(raw, stride, h, bpp); if (img == null) return null;
            var px = new int[w * h]; bool alpha = false;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int Sample(int idx) { int bit = (x * channels + idx) * bpc; if (bpc == 8) return img[row + bit / 8]; if (bpc == 16) return img[row + bit / 8]; int b = img[row + bit / 8]; return (b >> (8 - bpc - bit % 8)) & ((1 << bpc) - 1); }
                    int Scale(int v) => bpc == 8 || bpc == 16 ? v : v * 255 / ((1 << bpc) - 1);
                    int c;
                    switch (ct)
                    {
                        case 0: { int g = Scale(Sample(0)); int a = 255; if (trns != null && trns.Length >= 2 && Sample(0) == ((trns[0] << 8 | trns[1]) >> (bpc == 16 ? 8 : 0)) && bpc <= 8) a = 0; c = a << 24 | g << 16 | g << 8 | g; break; }
                        case 2: { int r = Sample(0), g = Sample(1), b = Sample(2); int a = 255; if (trns != null && trns.Length >= 6 && bpc == 8 && r == trns[1] && g == trns[3] && b == trns[5]) a = 0; c = a << 24 | r << 16 | g << 8 | b; break; }
                        case 3: { int i = Sample(0); int r = pal != null && i * 3 + 2 < pal.Length ? pal[i * 3] : 0, g = pal != null && i * 3 + 2 < pal.Length ? pal[i * 3 + 1] : 0, b = pal != null && i * 3 + 2 < pal.Length ? pal[i * 3 + 2] : 0; int a = trns != null && i < trns.Length ? trns[i] : 255; c = a << 24 | r << 16 | g << 8 | b; break; }
                        case 4: { int g = Scale(Sample(0)), a = Scale(Sample(1)); c = a << 24 | g << 16 | g << 8 | g; break; }
                        default: { int r = Sample(0), g = Sample(1), b = Sample(2), a = Sample(3); c = a << 24 | r << 16 | g << 8 | b; break; }
                    }
                    if ((c >> 24 & 255) != 255) alpha = true;
                    px[y * w + x] = c;
                }
            }
            return new VectorImagePaint(px, w, h) { HasAlpha = alpha };
        }
    }

    // =========================================================================================== PDF image decoding
    /// <summary>Turns PDF image dictionaries (XObjects and inline images) into <see cref="VectorImagePaint"/>s.</summary>
    internal static class PdfImageDecoder
    {
        /// <summary>PNG row filters (Sub / Up / Average / Paeth) in place-ish; null when the data is too short.</summary>
        public static byte[]? Unfilter(byte[] raw, int stride, int rows, int bpp)
        {
            if (raw.Length < (long)(stride + 1) * rows) rows = (int)(raw.Length / (stride + 1)); if (rows <= 0) return null;
            var o = new byte[stride * rows]; var prev = new byte[stride];
            for (int y = 0; y < rows; y++)
            {
                int ft = raw[y * (stride + 1)]; int s = y * (stride + 1) + 1, d = y * stride;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? o[d + x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0, v = raw[s + x];
                    switch (ft) { case 1: v += a; break; case 2: v += b; break; case 3: v += (a + b) >> 1; break; case 4: { int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; break; } }
                    o[d + x] = (byte)v;
                }
                Array.Copy(o, d, prev, 0, stride);
            }
            return o;
        }
    }

    // JPEG (DCTDecode): JpegDecoder moved to cs/Jpeg.cs (shared with ImageCodec / Sprite.LoadFromFile)

    // =========================================================================================== CCITT Group 3 / 4
    /// <summary>CCITTFaxDecode: K &lt; 0 = pure 2-D (G4), K = 0 = 1-D MH, K &gt; 0 = mixed (G3 2-D). Output: packed 1-bit rows, 0 = black unless BlackIs1.</summary>
    internal sealed class CcittDecoder
    {
        readonly byte[] d; int pos, bitPos; readonly int columns, k; readonly bool byteAlign, blackIs1;
        public CcittDecoder(byte[] data, int K, int columns, bool encodedByteAlign, bool blackIs1) { d = data; k = K; this.columns = Math.Max(1, columns); byteAlign = encodedByteAlign; this.blackIs1 = blackIs1; }
        int Peek(int n)
        {
            int v = 0; long bp = (long)pos * 8 + bitPos;
            for (int i = 0; i < n; i++) { long b = bp + i; int byteI = (int)(b >> 3); int bit = byteI < d.Length ? (d[byteI] >> (7 - (int)(b & 7))) & 1 : 0; v = v << 1 | bit; }
            return v;
        }
        void Eat(int n) { bitPos += n; pos += bitPos >> 3; bitPos &= 7; }
        bool Eod => pos >= d.Length;
        void Align() { if (bitPos != 0) { bitPos = 0; pos++; } }
        int ReadRun(bool white)
        {
            var tbl = white ? VectorFontData.CcittWhite : VectorFontData.CcittBlack; int total = 0;
            while (true)
            {
                int run = -1;
                for (int bits = 2; bits <= 14; bits++) { int code = Peek(bits); if (tbl.TryGetValue(bits << 16 | code, out int r)) { Eat(bits); run = r; break; } }
                if (run < 0) return total > 0 ? total : -1;
                if (run == -2) return -2;   // EOL inside a run
                total += run;
                if (run < 64) return total;          // terminating code
                // make-up code: a terminating code follows (same colour)
            }
        }
        int ReadMode()
        {   // returns: 0 pass, 1 horizontal, 2 V0, 3 VR1, 4 VL1, 5 VR2, 6 VL2, 7 VR3, 8 VL3, -1 error/EOL
            if (Peek(1) == 1) { Eat(1); return 2; }
            if (Peek(3) == 0b011) { Eat(3); return 3; }
            if (Peek(3) == 0b010) { Eat(3); return 4; }
            if (Peek(3) == 0b001) { Eat(3); return 1; }
            if (Peek(4) == 0b0001) { Eat(4); return 0; }
            if (Peek(6) == 0b000011) { Eat(6); return 5; }
            if (Peek(6) == 0b000010) { Eat(6); return 6; }
            if (Peek(7) == 0b0000011) { Eat(7); return 7; }
            if (Peek(7) == 0b0000010) { Eat(7); return 8; }
            return -1;
        }
        bool SkipEols()
        {   // consumes EOL codes (000000000001) and fill; returns true when at least one EOL was seen
            bool any = false;
            while (!Eod)
            {
                // fill bits: any number of zeros before the EOL
                int look = 0; while (look < 64 && Peek(look + 1) == 0 && !Eod) { look++; if (pos * 8 + bitPos + look >= d.Length * 8) break; }
                if (look >= 11 && Peek(look + 1) == 1) { Eat(look + 1); any = true; if (k > 0) { /* mode bit follows: read by caller */ } continue; }
                break;
            }
            return any;
        }
        public byte[] Decode(int rows)
        {
            int stride = (columns + 7) / 8; var outRows = new List<byte[]>();
            var refLine = new List<int> { columns, columns }; var cur = new List<int>(64);
            int maxRows = rows > 0 ? rows : int.MaxValue;
            while (outRows.Count < maxRows && !Eod)
            {
                if (byteAlign && k >= 0) Align();
                bool eol = SkipEols();
                if (Eod) break;
                bool twoD;
                if (k < 0) twoD = true; else if (k == 0) twoD = false; else { twoD = Peek(1) == 0; Eat(1); }
                if (byteAlign && k < 0) Align();
                if (byteAlign && k > 0 && !eol) { }
                cur.Clear(); int a0 = -1; bool white = true;
                bool ok = true;
                if (twoD)
                {
                    while (a0 < columns)
                    {
                        // b1: first changing element on the reference line to the right of a0 with opposite colour of a0's colour... (standard: colour opposite to colour of a0 = same as "white" flag's opposite transitions)
                        int b1 = FindB1(refLine, a0, white), b2 = b1 < refLine.Count - 1 ? NextChange(refLine, b1) : columns;
                        int b1v = b1 < 0 ? columns : Math.Min(columns, refLine[b1]); int b2v = Math.Min(columns, b2);
                        int mode = ReadMode();
                        if (mode < 0) { ok = false; break; }
                        switch (mode)
                        {
                            case 0: a0 = b2v; break;   // pass: fill from a0 to b2 with current colour (no transition recorded)
                            case 1:
                                {
                                    int r1 = ReadRun(white), r2 = ReadRun(!white); if (r1 < 0 || r2 < 0) { ok = false; break; }
                                    int start = a0 < 0 ? 0 : a0; int a1 = Math.Min(columns, start + r1), a2 = Math.Min(columns, a1 + r2);
                                    cur.Add(a1); cur.Add(a2); a0 = a2; break;
                                }
                            default:
                                {
                                    int delta = mode switch { 2 => 0, 3 => 1, 4 => -1, 5 => 2, 6 => -2, 7 => 3, _ => -3 };
                                    int a1 = Math.Clamp(b1v + delta, 0, columns); cur.Add(a1); a0 = a1; white = !white; break;
                                }
                        }
                        if (!ok) break;
                        if (a0 >= columns) break;
                    }
                }
                else
                {
                    int x = 0;
                    while (x < columns)
                    {
                        int r = ReadRun(white); if (r < 0) { ok = false; break; }
                        x = Math.Min(columns, x + r); cur.Add(x); white = !white;
                    }
                }
                if (cur.Count == 0 && !ok) break;
                // emit the row from the changing elements
                var row = new byte[stride]; bool w = true; int xpos = 0;
                foreach (var ch in cur)
                {
                    int to = Math.Clamp(ch, 0, columns);
                    if (!w) for (int xx = xpos; xx < to; xx++) row[xx >> 3] |= (byte)(0x80 >> (xx & 7));
                    xpos = Math.Max(xpos, to); w = !w;
                }
                if (!w) for (int xx = xpos; xx < columns; xx++) row[xx >> 3] |= (byte)(0x80 >> (xx & 7));
                outRows.Add(row);
                if (!ok) break;
                // reference line = current changes (+ sentinels)
                refLine.Clear(); refLine.AddRange(cur); refLine.Add(columns); refLine.Add(columns);
                if (byteAlign && k > 0) Align();
            }
            // pack: rows of "1 = black" bits; PDF default (BlackIs1 false) wants 0 = black
            int nrows = rows > 0 ? rows : outRows.Count; var o = new byte[stride * nrows];
            for (int y = 0; y < nrows; y++)
            {
                if (y < outRows.Count) Array.Copy(outRows[y], 0, o, y * stride, stride);
                if (!blackIs1) for (int i = 0; i < stride; i++) o[y * stride + i] = (byte)~o[y * stride + i];
            }
            return o;
        }
        /// <summary>Index into the reference line of b1: the first changing element &gt; a0 whose colour is opposite to the current colour (changes alternate starting with a white->black change at even indices).</summary>
        static int FindB1(List<int> refLine, int a0, bool white)
        {
            // changing elements in refLine alternate colours: index even = change to black (white->black), odd = change to white.
            // b1 must have the opposite colour of a0's colour, i.e. a change TO the opposite colour: white current -> even index.
            int i = 0; int n = refLine.Count;
            while (i < n && refLine[i] <= a0) i++;
            if (((i & 1) == 0) != white) i++;
            return i < n ? i : n - 1;
        }
        static int NextChange(List<int> refLine, int i) => i + 1 < refLine.Count ? refLine[i + 1] : refLine[^1];
    }
}
