using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace Sr2d64CSport
{
    /// <summary>
    /// Pure C# PNG codec (no System.Drawing, no native code) so that loading and saving pictures is symmetric with the
    /// managed WebP decoder and works headless.
    ///
    /// Encoder: 8-bit RGBA / RGB / grey / grey+alpha / 1-8 bit palette, picked automatically from the pixels
    /// (<see cref="PngColor.Auto"/>), adaptive per-row filter selection (the libpng "minimum sum of absolute differences"
    /// heuristic), Deflate via <see cref="ZLibStream"/>. Output is bit-exact round-trippable and readable by every viewer.
    /// Decoder: every colour type and bit depth of the specification (1 / 2 / 4 / 8 / 16 bit, palette + tRNS, grey + tRNS,
    /// RGB + tRNS, interlaced Adam7), gAMA / iCCP / etc. are ignored (colours are taken as stored, like GDI+ does by default).
    /// 16-bit samples keep their high byte. Result is straight (non-premultiplied) ARGB, top-down, as everywhere in SR2D.
    /// </summary>
    internal static class Png
    {
        static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };   // was a => new property: a fresh array per access (awesome-copilot CSharpExpert / WinFormsExpert "property patterns")

        public static bool IsPng(ReadOnlySpan<byte> d) => d.Length >= 8 && d.Slice(0, 8).SequenceEqual(Signature);

        /// <summary>Width / height / bit depth / colour type / interlace flag from the header, or null when not a PNG.</summary>
        public static PngInfo? GetInfo(ReadOnlySpan<byte> d)
        {
            if (!IsPng(d) || d.Length < 33) return null;
            if (BinaryPrimitives.ReadInt32BigEndian(d.Slice(8)) != 13 || !d.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
            int w = BinaryPrimitives.ReadInt32BigEndian(d.Slice(16)), h = BinaryPrimitives.ReadInt32BigEndian(d.Slice(20));
            if (w <= 0 || h <= 0) return null;
            return new PngInfo(w, h, d[24], (PngColorType)d[25], d[28] != 0);
        }

        // ============================================================================================ encoder
        /// <summary>
        /// Encodes straight ARGB pixels (top-down, <paramref name="stride"/> ints per row) as a PNG.
        /// <paramref name="color"/> Auto: RGBA when any pixel is translucent, palette when there are at most 256 distinct
        /// colours (and that is smaller), grey when every pixel is neutral, RGB otherwise. Alpha is dropped for opaque
        /// output (<see cref="PngColor.Rgb"/> / <see cref="PngColor.Gray"/>) - pass <see cref="PngColor.Rgba"/> to keep
        /// a fully opaque alpha channel. <paramref name="level"/> maps to Deflate: 0 = store (fastest, big), 1 = fast,
        /// 2 = default (libpng-like size), 3 = smallest.
        /// </summary>
        public static byte[] Encode(ReadOnlySpan<int> argb, int width, int height, int stride = 0, PngColor color = PngColor.Auto, int level = 2)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (stride <= 0) stride = width;
            if ((long)stride * (height - 1) + width > argb.Length) throw new ArgumentException("pixel buffer too small", nameof(argb));

            // ---- analyse
            bool translucent = false, neutral = true; int[]? palette = null; int npal = 0;
            var palIndex = new System.Collections.Generic.Dictionary<int, int>(256);
            bool tryPal = color == PngColor.Auto || color == PngColor.Palette;
            for (int y = 0; y < height && (tryPal || !translucent || neutral); y++)
            {
                var row = argb.Slice(y * stride, width);
                for (int x = 0; x < width; x++)
                {
                    int c = row[x]; int a = c >>> 24;
                    if (a != 255) translucent = true;
                    if (neutral && (((c >> 16) & 255) != ((c >> 8) & 255) || ((c >> 8) & 255) != (c & 255))) neutral = false;
                    if (tryPal)
                    {
                        if (!palIndex.ContainsKey(c))
                        {
                            if (npal == 256) { tryPal = false; palIndex.Clear(); }
                            else palIndex[c] = npal++;
                        }
                    }
                }
            }
            if (tryPal) { palette = new int[npal]; foreach (var kv in palIndex) palette[kv.Value] = kv.Key; }

            PngColorType ct; int bitDepth = 8, channels;
            switch (color)
            {
                case PngColor.Rgba: ct = PngColorType.Rgba; break;
                case PngColor.Rgb: ct = PngColorType.Rgb; break;
                case PngColor.Gray: ct = PngColorType.Gray; break;
                case PngColor.GrayAlpha: ct = PngColorType.GrayAlpha; break;
                case PngColor.Palette:
                    if (palette == null) throw new ArgumentException("more than 256 distinct colours - palette output is not possible", nameof(color));
                    ct = PngColorType.Palette; break;
                default:
                    if (palette != null && npal <= 256 && !(neutral && !translucent && npal > 16))   // palette unless grey is as small and simpler
                    {
                        // palette rows cost <= 1 byte/px; RGB 3, RGBA 4, grey 1. Grey wins for neutral opaque pictures with many levels.
                        ct = PngColorType.Palette;
                    }
                    else if (neutral) ct = translucent ? PngColorType.GrayAlpha : PngColorType.Gray;
                    else ct = translucent ? PngColorType.Rgba : PngColorType.Rgb;
                    break;
            }
            if (ct == PngColorType.Palette) bitDepth = npal <= 2 ? 1 : npal <= 4 ? 2 : npal <= 16 ? 4 : 8;
            channels = ct switch { PngColorType.Gray => 1, PngColorType.GrayAlpha => 2, PngColorType.Rgb => 3, PngColorType.Rgba => 4, _ => 1 };

            // ---- raw scanlines (filter byte + samples)
            int rowBytes = (width * channels * bitDepth + 7) / 8, bpp = Math.Max(1, channels * bitDepth / 8);
            var raw = new byte[(rowBytes + 1) * height];
            var cur = new byte[rowBytes]; var prev = new byte[rowBytes];
            var f = new byte[5][]; for (int i = 0; i < 5; i++) f[i] = new byte[rowBytes];
            for (int y = 0; y < height; y++)
            {
                var row = argb.Slice(y * stride, width);
                PackRow(row, cur, ct, bitDepth, palIndex);
                int best = ChooseFilter(cur, prev, bpp, f, y == 0);
                int o = y * (rowBytes + 1); raw[o] = (byte)best;
                Buffer.BlockCopy(best == 0 ? cur : f[best], 0, raw, o + 1, rowBytes);
                (cur, prev) = (prev, cur);
            }

            // ---- chunks
            using var ms = new MemoryStream(raw.Length / 3 + 256);
            ms.Write(Signature);
            Span<byte> hdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(hdr, width); BinaryPrimitives.WriteInt32BigEndian(hdr.Slice(4), height);
            hdr[8] = (byte)bitDepth; hdr[9] = (byte)ct; hdr[10] = 0; hdr[11] = 0; hdr[12] = 0;
            WriteChunk(ms, "IHDR"u8, hdr);
            if (ct == PngColorType.Palette)
            {
                var plte = new byte[npal * 3]; var trns = new byte[npal]; int lastA = -1;
                for (int i = 0; i < npal; i++) { int c = palette![i]; plte[i * 3] = (byte)(c >> 16); plte[i * 3 + 1] = (byte)(c >> 8); plte[i * 3 + 2] = (byte)c; trns[i] = (byte)(c >>> 24); if (trns[i] != 255) lastA = i; }
                WriteChunk(ms, "PLTE"u8, plte);
                if (lastA >= 0) WriteChunk(ms, "tRNS"u8, trns.AsSpan(0, lastA + 1));
            }
            using (var z = new MemoryStream(raw.Length / 3 + 64))
            {
                var lvl = level <= 0 ? CompressionLevel.NoCompression : level == 1 ? CompressionLevel.Fastest : level == 2 ? CompressionLevel.Optimal : CompressionLevel.SmallestSize;
                using (var zs = new ZLibStream(z, lvl, true)) zs.Write(raw, 0, raw.Length);
                WriteChunk(ms, "IDAT"u8, z.GetBuffer().AsSpan(0, (int)z.Length));
            }
            WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
            return ms.ToArray();
        }

        static void PackRow(ReadOnlySpan<int> row, byte[] o, PngColorType ct, int bitDepth, System.Collections.Generic.Dictionary<int, int> pal)
        {
            int w = row.Length, k = 0;
            switch (ct)
            {
                case PngColorType.Rgba: for (int x = 0; x < w; x++) { int c = row[x]; o[k++] = (byte)(c >> 16); o[k++] = (byte)(c >> 8); o[k++] = (byte)c; o[k++] = (byte)(c >>> 24); } break;
                case PngColorType.Rgb: for (int x = 0; x < w; x++) { int c = row[x]; o[k++] = (byte)(c >> 16); o[k++] = (byte)(c >> 8); o[k++] = (byte)c; } break;
                case PngColorType.Gray: for (int x = 0; x < w; x++) o[k++] = (byte)row[x]; break;
                case PngColorType.GrayAlpha: for (int x = 0; x < w; x++) { int c = row[x]; o[k++] = (byte)c; o[k++] = (byte)(c >>> 24); } break;
                default:
                    if (bitDepth == 8) { for (int x = 0; x < w; x++) o[k++] = (byte)pal[row[x]]; }
                    else
                    {
                        Array.Clear(o, 0, o.Length); int perByte = 8 / bitDepth;
                        for (int x = 0; x < w; x++) { int idx = pal[row[x]]; int shift = 8 - bitDepth * (x % perByte + 1); o[x / perByte] |= (byte)(idx << shift); }
                    }
                    break;
            }
        }

        /// <summary>libpng heuristic: apply all five filters, keep the one whose bytes (as signed) sum to the smallest magnitude.</summary>
        static int ChooseFilter(byte[] cur, byte[] prev, int bpp, byte[][] f, bool firstRow)
        {
            int n = cur.Length;
            long bestSum = 0; int best = 0;
            for (int i = 0; i < n; i++) bestSum += Mag(cur[i]);
            // Sub
            { var o = f[1]; long s = 0; for (int i = 0; i < n; i++) { byte v = (byte)(cur[i] - (i >= bpp ? cur[i - bpp] : 0)); o[i] = v; s += Mag(v); } if (s < bestSum) { bestSum = s; best = 1; } }
            if (!firstRow)
            {
                // Up
                { var o = f[2]; long s = 0; for (int i = 0; i < n; i++) { byte v = (byte)(cur[i] - prev[i]); o[i] = v; s += Mag(v); } if (s < bestSum) { bestSum = s; best = 2; } }
                // Average
                { var o = f[3]; long s = 0; for (int i = 0; i < n; i++) { int a = i >= bpp ? cur[i - bpp] : 0; byte v = (byte)(cur[i] - ((a + prev[i]) >> 1)); o[i] = v; s += Mag(v); } if (s < bestSum) { bestSum = s; best = 3; } }
                // Paeth
                { var o = f[4]; long s = 0; for (int i = 0; i < n; i++) { int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0; byte v = (byte)(cur[i] - Paeth(a, b, c)); o[i] = v; s += Mag(v); } if (s < bestSum) { best = 4; } }
            }
            return best;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] static int Mag(byte v) => v < 128 ? v : 256 - v;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> len = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(len, data.Length); s.Write(len);
            s.Write(type); s.Write(data);
            uint crc = Crc32(type, 0xFFFFFFFF); crc = Crc32(data, crc) ^ 0xFFFFFFFF;
            BinaryPrimitives.WriteUInt32BigEndian(len, crc); s.Write(len);
        }
        static uint[]? crcTable;
        static uint Crc32(ReadOnlySpan<byte> d, uint crc)
        {
            var t = crcTable;
            if (t == null)
            {
                t = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; }
                crcTable = t;
            }
            foreach (byte b in d) crc = t[(crc ^ b) & 255] ^ (crc >> 8);
            return crc;
        }

        // ============================================================================================ decoder
        /// <summary>Decodes a PNG to straight ARGB (top-down). Throws <see cref="InvalidDataException"/> on malformed data.</summary>
        public static (int width, int height, int[] argb) Decode(ReadOnlySpan<byte> d)
        {
            var info = GetInfo(d) ?? throw new InvalidDataException("not a PNG");
            int w = info.Width, h = info.Height, depth = info.BitDepth; var ct = info.ColorType; bool interlaced = info.Interlaced;
            if ((long)w * h > 400_000_000L) throw new InvalidDataException("PNG too large");
            int channels = ct switch { PngColorType.Gray => 1, PngColorType.Rgb => 3, PngColorType.Palette => 1, PngColorType.GrayAlpha => 2, PngColorType.Rgba => 4, _ => throw new InvalidDataException("bad colour type") };
            bool depthOk = ct switch { PngColorType.Gray => depth is 1 or 2 or 4 or 8 or 16, PngColorType.Palette => depth is 1 or 2 or 4 or 8, _ => depth is 8 or 16 };
            if (!depthOk) throw new InvalidDataException("bad bit depth");

            byte[]? pal = null, trns = null; var idat = new MemoryStream();
            int p = 8;
            while (p + 8 <= d.Length)
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(d.Slice(p)); var type = d.Slice(p + 4, 4); p += 8;
                if (len < 0 || p + len > d.Length) throw new InvalidDataException("truncated chunk");
                if (type.SequenceEqual("IDAT"u8)) idat.Write(d.Slice(p, len));
                else if (type.SequenceEqual("PLTE"u8)) pal = d.Slice(p, len).ToArray();
                else if (type.SequenceEqual("tRNS"u8)) trns = d.Slice(p, len).ToArray();
                else if (type.SequenceEqual("IEND"u8)) break;
                p += len + 4;   // + crc (not verified: a damaged file shows up as garbage rows, same as libpng in non-strict mode)
            }
            if (ct == PngColorType.Palette && pal == null) throw new InvalidDataException("palette missing");

            // inflate (all of it: the filtered rows of every pass are back to back)
            byte[] raw;
            {
                idat.Position = 0;
                long expect = interlaced ? InterlacedSize(w, h, channels * depth) : ((long)(w * channels * depth + 7) / 8 + 1) * h;
                if (expect > int.MaxValue) throw new InvalidDataException("PNG too large");
                raw = new byte[expect];
                using var z = new ZLibStream(idat, CompressionMode.Decompress);
                int got = 0; while (got < raw.Length) { int n = z.Read(raw, got, raw.Length - got); if (n <= 0) break; got += n; }
                if (got < raw.Length) throw new InvalidDataException("PNG data truncated");
            }

            var px = new int[w * h];
            int bitsPerPixel = channels * depth, bpp = Math.Max(1, bitsPerPixel / 8);
            // tRNS colour keys
            int keyG = -1, keyR = -1, keyGg = -1, keyB = -1;
            if (trns != null)
            {
                if (ct == PngColorType.Gray && trns.Length >= 2) keyG = trns[0] << 8 | trns[1];
                else if (ct == PngColorType.Rgb && trns.Length >= 6) { keyR = trns[0] << 8 | trns[1]; keyGg = trns[2] << 8 | trns[3]; keyB = trns[4] << 8 | trns[5]; }
            }
            int off = 0;
            if (!interlaced) DecodePass(raw, ref off, w, h, 0, 0, 1, 1, px, w, ct, depth, channels, bpp, pal, trns, keyG, keyR, keyGg, keyB);
            else
            {
                ReadOnlySpan<int> sx = stackalloc int[] { 0, 4, 0, 2, 0, 1, 0 }, sy = stackalloc int[] { 0, 0, 4, 0, 2, 0, 1 }, dx = stackalloc int[] { 8, 8, 4, 4, 2, 2, 1 }, dy = stackalloc int[] { 8, 8, 8, 4, 4, 2, 2 };
                for (int pass = 0; pass < 7; pass++)
                {
                    int pw = (w - sx[pass] + dx[pass] - 1) / dx[pass], ph = (h - sy[pass] + dy[pass] - 1) / dy[pass];
                    if (pw <= 0 || ph <= 0) continue;
                    DecodePass(raw, ref off, pw, ph, sx[pass], sy[pass], dx[pass], dy[pass], px, w, ct, depth, channels, bpp, pal, trns, keyG, keyR, keyGg, keyB);
                }
            }
            return (w, h, px);
        }
        static long InterlacedSize(int w, int h, int bitsPerPixel)
        {
            ReadOnlySpan<int> sx = stackalloc int[] { 0, 4, 0, 2, 0, 1, 0 }, sy = stackalloc int[] { 0, 0, 4, 0, 2, 0, 1 }, dx = stackalloc int[] { 8, 8, 4, 4, 2, 2, 1 }, dy = stackalloc int[] { 8, 8, 8, 4, 4, 2, 2 };
            long total = 0;
            for (int pass = 0; pass < 7; pass++)
            {
                int pw = (w - sx[pass] + dx[pass] - 1) / dx[pass], ph = (h - sy[pass] + dy[pass] - 1) / dy[pass];
                if (pw <= 0 || ph <= 0) continue;
                total += ((long)(pw * bitsPerPixel + 7) / 8 + 1) * ph;
            }
            return total;
        }

        /// <summary>Unfilters and converts the rows of one (sub)image whose pixels land at (x0 + i*dx, y0 + j*dy).</summary>
        static void DecodePass(byte[] raw, ref int off, int pw, int ph, int x0, int y0, int dx, int dy, int[] px, int W, PngColorType ct, int depth, int channels, int bpp,
                               byte[]? pal, byte[]? trns, int keyG, int keyR, int keyGg, int keyB)
        {
            int rowBytes = (pw * channels * depth + 7) / 8;
            var prev = new byte[rowBytes]; var cur = new byte[rowBytes];
            for (int j = 0; j < ph; j++)
            {
                int filter = raw[off++];
                Buffer.BlockCopy(raw, off, cur, 0, rowBytes); off += rowBytes;
                switch (filter)
                {
                    case 0: break;
                    case 1: for (int i = bpp; i < rowBytes; i++) cur[i] += cur[i - bpp]; break;
                    case 2: for (int i = 0; i < rowBytes; i++) cur[i] += prev[i]; break;
                    case 3: for (int i = 0; i < rowBytes; i++) cur[i] += (byte)(((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1); break;
                    case 4: for (int i = 0; i < rowBytes; i++) cur[i] += (byte)Paeth(i >= bpp ? cur[i - bpp] : 0, prev[i], i >= bpp ? prev[i - bpp] : 0); break;
                    default: throw new InvalidDataException("bad filter type");
                }
                int y = y0 + j * dy; int rowBase = y * W;
                switch (ct)
                {
                    case PngColorType.Rgba:
                        if (depth == 8) for (int i = 0, k = 0; i < pw; i++, k += 4) px[rowBase + x0 + i * dx] = cur[k + 3] << 24 | cur[k] << 16 | cur[k + 1] << 8 | cur[k + 2];
                        else for (int i = 0, k = 0; i < pw; i++, k += 8) px[rowBase + x0 + i * dx] = cur[k + 6] << 24 | cur[k] << 16 | cur[k + 2] << 8 | cur[k + 4];
                        break;
                    case PngColorType.Rgb:
                        if (depth == 8)
                            for (int i = 0, k = 0; i < pw; i++, k += 3)
                            {
                                int r = cur[k], g = cur[k + 1], b = cur[k + 2];
                                int a = keyR >= 0 && r == keyR && g == keyGg && b == keyB ? 0 : 255;
                                px[rowBase + x0 + i * dx] = a << 24 | r << 16 | g << 8 | b;
                            }
                        else
                            for (int i = 0, k = 0; i < pw; i++, k += 6)
                            {
                                int r16 = cur[k] << 8 | cur[k + 1], g16 = cur[k + 2] << 8 | cur[k + 3], b16 = cur[k + 4] << 8 | cur[k + 5];
                                int a = keyR >= 0 && r16 == keyR && g16 == keyGg && b16 == keyB ? 0 : 255;
                                px[rowBase + x0 + i * dx] = a << 24 | cur[k] << 16 | cur[k + 2] << 8 | cur[k + 4];
                            }
                        break;
                    case PngColorType.GrayAlpha:
                        if (depth == 8) for (int i = 0, k = 0; i < pw; i++, k += 2) { int g = cur[k]; px[rowBase + x0 + i * dx] = cur[k + 1] << 24 | g << 16 | g << 8 | g; }
                        else for (int i = 0, k = 0; i < pw; i++, k += 4) { int g = cur[k]; px[rowBase + x0 + i * dx] = cur[k + 2] << 24 | g << 16 | g << 8 | g; }
                        break;
                    case PngColorType.Gray:
                        if (depth == 16)
                            for (int i = 0, k = 0; i < pw; i++, k += 2) { int v = cur[k] << 8 | cur[k + 1], g = cur[k]; int a = v == keyG ? 0 : 255; px[rowBase + x0 + i * dx] = a << 24 | g << 16 | g << 8 | g; }
                        else
                        {
                            int max = (1 << depth) - 1;
                            for (int i = 0; i < pw; i++)
                            {
                                int v = Sample(cur, i, depth); int g = depth == 8 ? v : v * 255 / max; int a = v == keyG ? 0 : 255;
                                px[rowBase + x0 + i * dx] = a << 24 | g << 16 | g << 8 | g;
                            }
                        }
                        break;
                    default:   // palette
                        for (int i = 0; i < pw; i++)
                        {
                            int idx = Sample(cur, i, depth);
                            int r = 0, g = 0, b = 0, a = 255;
                            if (idx * 3 + 2 < pal!.Length) { r = pal[idx * 3]; g = pal[idx * 3 + 1]; b = pal[idx * 3 + 2]; }
                            if (trns != null && idx < trns.Length) a = trns[idx];
                            px[rowBase + x0 + i * dx] = a << 24 | r << 16 | g << 8 | b;
                        }
                        break;
                }
                (cur, prev) = (prev, cur);
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Sample(byte[] row, int i, int depth)
        {
            if (depth == 8) return row[i];
            int bit = i * depth; int b = row[bit >> 3];
            return (b >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
        }
    }

    /// <summary>PNG colour types (IHDR byte 9).</summary>
    internal enum PngColorType : byte { Gray = 0, Rgb = 2, Palette = 3, GrayAlpha = 4, Rgba = 6 }

    /// <summary>Output colour model for <see cref="Png.Encode"/>.</summary>
    internal enum PngColor
    {
        /// <summary>Smallest lossless representation of the pixels (palette / grey / RGB / RGBA, alpha only when used).</summary>
        Auto,
        /// <summary>8-bit RGBA, always (keeps an opaque alpha channel too).</summary>
        Rgba,
        /// <summary>8-bit RGB, alpha dropped.</summary>
        Rgb,
        /// <summary>8-bit grey (blue channel), alpha dropped.</summary>
        Gray,
        /// <summary>8-bit grey + alpha.</summary>
        GrayAlpha,
        /// <summary>Indexed colour, 1 / 2 / 4 / 8 bits per pixel; throws when the picture has more than 256 distinct ARGB values.</summary>
        Palette,
    }

    internal sealed class PngInfo
    {
        public PngInfo(int width, int height, int bitDepth, PngColorType colorType, bool interlaced) { Width = width; Height = height; BitDepth = bitDepth; ColorType = colorType; Interlaced = interlaced; }
        public int Width { get; }
        public int Height { get; }
        public int BitDepth { get; }
        public PngColorType ColorType { get; }
        public bool Interlaced { get; }
        public bool HasAlphaChannel => ColorType == PngColorType.Rgba || ColorType == PngColorType.GrayAlpha;
        public override string ToString() => $"{Width}x{Height} {ColorType} {BitDepth}-bit{(Interlaced ? " interlaced" : "")}";
    }
}
