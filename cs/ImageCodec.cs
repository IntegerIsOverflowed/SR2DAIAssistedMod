using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Sr2d64CSport
{
    /// <summary>File formats <see cref="ImageCodec"/> recognises by signature.</summary>
    internal enum ImageKind { Unknown, Png, WebP, Jpeg, Bmp, Gif, Tga }

    /// <summary>
    /// Managed image loading - one front door for every picture format SR2D can decode without GDI+:
    /// PNG (cs/Png.cs), WebP (cs/WebP.cs), JPEG (baseline + progressive, the decoder shared with the PDF / SVG importers),
    /// BMP (1 / 4 / 8 / 16 / 24 / 32 bit, RLE4 / RLE8, BITFIELDS, top-down and bottom-up, V4 / V5 headers with alpha),
    /// GIF (first frame, interlaced, transparency) and TGA (uncompressed / RLE, 8 / 15 / 16 / 24 / 32 bit, colour-mapped,
    /// either origin). Unlike GDI+, a 32-bit BI_RGB bitmap's 4th byte is taken as alpha when it is not all zero (files
    /// from GIMP / Pillow / Photoshop carry real alpha there); an all-zero 4th byte is padding and the picture is opaque. <see cref="Sprite.LoadFromFile"/> uses it first and only falls back to GDI+ for what is left
    /// (TIFF, ICO, EMF ...). Output is straight ARGB, top-down; <c>hasAlpha</c> says whether any pixel is not opaque so the
    /// sprite's Op can be defaulted the same way the old loaders did.
    /// </summary>
    internal static class ImageCodec
    {
        /// <summary>Format by the first bytes (at least 12 are needed for a sure answer; TGA has no signature and is recognised by its header fields).</summary>
        public static ImageKind Sniff(ReadOnlySpan<byte> d)
        {
            if (d.Length >= 8 && Png.IsPng(d)) return ImageKind.Png;
            if (d.Length >= 12 && WebP.IsWebP(d)) return ImageKind.WebP;
            if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ImageKind.Jpeg;
            if (d.Length >= 14 && d[0] == 'B' && d[1] == 'M') return ImageKind.Bmp;
            if (d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8' && (d[4] == '7' || d[4] == '9') && d[5] == 'a') return ImageKind.Gif;
            if (LooksLikeTga(d)) return ImageKind.Tga;
            return ImageKind.Unknown;
        }
        /// <summary>Format of a file (reads the header; Unknown when the file is unreadable). The extension is only used to accept a TGA.</summary>
        public static ImageKind SniffFile(string file)
        {
            try
            {
                using var fs = File.OpenRead(file); Span<byte> hdr = stackalloc byte[26]; int n = fs.Read(hdr);
                var k = Sniff(hdr.Slice(0, n));
                if (k == ImageKind.Tga && !file.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".targa", StringComparison.OrdinalIgnoreCase)) return ImageKind.Unknown;
                return k;
            }
            catch (IOException) { return ImageKind.Unknown; }
            catch (UnauthorizedAccessException) { return ImageKind.Unknown; }
        }
        public static bool CanDecode(ReadOnlySpan<byte> d) => Sniff(d) != ImageKind.Unknown;

        /// <summary>Decodes any supported format; throws <see cref="NotSupportedException"/> for an unknown one, <see cref="InvalidDataException"/> for a broken file.</summary>
        public static (int width, int height, int[] argb, bool hasAlpha) Decode(byte[] d)
        {
            switch (Sniff(d))
            {
                case ImageKind.Png:
                    {
                        var (w, h, px) = Png.Decode(d); var info = Png.GetInfo(d);
                        bool a = info != null && (info.HasAlphaChannel || info.ColorType == PngColorType.Palette);
                        if (!a) a = AnyAlpha(px);
                        return (w, h, px, a);
                    }
                case ImageKind.WebP: { var (w, h, px) = WebP.Decode(d); return (w, h, px, (WebP.GetInfo(d)?.HasAlpha ?? false) && AnyAlpha(px)); }
                case ImageKind.Jpeg: { var (w, h, px) = DecodeJpeg(d); return (w, h, px, false); }
                case ImageKind.Bmp: { var (w, h, px, a) = DecodeBmp(d); return (w, h, px, a); }
                case ImageKind.Gif: { var (w, h, px, a) = DecodeGif(d); return (w, h, px, a); }
                case ImageKind.Tga: { var (w, h, px, a) = DecodeTga(d); return (w, h, px, a); }
                default: throw new NotSupportedException("not a PNG / WebP / JPEG / BMP / GIF / TGA picture");
            }
        }
        static bool AnyAlpha(int[] px) { foreach (int c in px) if ((c >>> 24) != 255) return true; return false; }

        // ------------------------------------------------------------------------------------------------------ JPEG
        /// <summary>JPEG to ARGB (grey, YCbCr / RGB, CMYK / YCCK with the Adobe inversion); EXIF orientation is NOT applied (like GDI+).</summary>
        public static (int width, int height, int[] argb) DecodeJpeg(byte[] d)
        {
            var jd = new JpegDecoder(d); var s = jd.Decode(); int n = jd.Components, w = jd.Width, h = jd.Height;
            var px = new int[checked(w * h)];
            const int opaque = unchecked((int)0xFF000000);
            if (n == 1) for (int i = 0, k = 0; i < px.Length; i++, k++) { int g = s[k]; px[i] = opaque | g << 16 | g << 8 | g; }
            else if (n == 3) for (int i = 0, k = 0; i < px.Length; i++, k += 3) px[i] = opaque | s[k] << 16 | s[k + 1] << 8 | s[k + 2];
            else if (n == 4)
            {
                bool inv = jd.Adobe;
                for (int i = 0, k = 0; i < px.Length; i++, k += 4)
                {
                    int c = s[k], m = s[k + 1], y = s[k + 2], kk = s[k + 3];
                    if (inv) { c = 255 - c; m = 255 - m; y = 255 - y; kk = 255 - kk; }
                    px[i] = opaque | ((255 - c) * (255 - kk) / 255) << 16 | ((255 - m) * (255 - kk) / 255) << 8 | ((255 - y) * (255 - kk) / 255);
                }
            }
            else throw new NotSupportedException("JPEG with " + n + " components");
            return (w, h, px);
        }

        // ------------------------------------------------------------------------------------------------------ BMP
        /// <summary>Windows / OS2 bitmaps: BITMAPCOREHEADER, INFO, V2..V5; 1 / 4 / 8 bit palette, 16 / 24 / 32 bit, RLE4 / RLE8, BI_BITFIELDS (any masks); alpha only when a 32-bit file has a non-zero alpha mask or V4+ with non-zero alpha values.</summary>
        public static (int width, int height, int[] argb, bool hasAlpha) DecodeBmp(byte[] d)
        {
            if (d.Length < 26 || d[0] != 'B' || d[1] != 'M') throw new InvalidDataException("not a BMP");
            int pixOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(10));
            int hdr = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(14));
            int w, h, bpp, comp = 0, palCount = 0; bool core = hdr == 12;
            uint rm = 0, gm = 0, bm = 0, am = 0;
            if (core) { w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(18)); h = (short)BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(20)); bpp = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(24)); }
            else
            {
                if (hdr < 40 || d.Length < 14 + hdr) throw new InvalidDataException("BMP header");
                w = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18)); h = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22));
                bpp = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(28)); comp = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(30));
                palCount = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(46));
                if (comp == 3 || comp == 6)
                {   // BI_BITFIELDS / BI_ALPHABITFIELDS: masks follow the 40-byte header, or sit inside V4+/V5
                    int mo = hdr >= 52 ? 54 : 14 + hdr;
                    if (d.Length < mo + 12) throw new InvalidDataException("BMP masks");
                    rm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo)); gm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo + 4)); bm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo + 8));
                    if (hdr >= 56 && d.Length >= mo + 16) am = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo + 12));
                    else if (comp == 6 && hdr == 40 && d.Length >= mo + 16) am = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo + 12));
                }
            }
            bool topDown = h < 0; h = Math.Abs(h);
            if (w <= 0 || h <= 0 || (long)w * h > 1L << 31 - 3) throw new InvalidDataException("BMP size");
            if (bpp != 1 && bpp != 2 && bpp != 4 && bpp != 8 && bpp != 16 && bpp != 24 && bpp != 32) throw new NotSupportedException("BMP " + bpp + " bpp");
            if (comp != 0 && comp != 1 && comp != 2 && comp != 3 && comp != 6) throw new NotSupportedException("BMP compression " + comp);
            // palette
            int[]? pal = null;
            if (bpp <= 8)
            {
                int entries = palCount > 0 ? palCount : 1 << bpp; int esz = core ? 3 : 4; int po = 14 + hdr + ((comp == 3 && hdr == 40) ? 12 : 0);
                pal = new int[Math.Max(entries, 1 << bpp)];
                for (int i = 0; i < entries && po + i * esz + 2 < d.Length; i++) pal[i] = unchecked((int)0xFF000000) | d[po + i * esz + 2] << 16 | d[po + i * esz + 1] << 8 | d[po + i * esz];
            }
            var px = new int[checked(w * h)];
            bool hasAlpha = false;
            if (comp == 1 || comp == 2)
            {   // RLE8 / RLE4, bottom-up (top-down RLE is not allowed by the spec)
                int x = 0, y = 0, p = pixOff; int bits = comp == 1 ? 8 : 4;
                void Put(int idx) { if (x < w && y < h) { int yy = topDown ? y : h - 1 - y; px[yy * w + x] = pal![idx & (pal.Length - 1)]; } x++; }
                while (p + 1 < d.Length && y < h)
                {
                    int a = d[p++], b = d[p++];
                    if (a > 0) { for (int i = 0; i < a; i++) Put(bits == 8 ? b : ((i & 1) == 0 ? b >> 4 : b & 15)); }
                    else if (b == 0) { x = 0; y++; }
                    else if (b == 1) break;
                    else if (b == 2) { if (p + 1 < d.Length) { x += d[p++]; y += d[p++]; } }
                    else
                    {   // absolute run, padded to a word
                        if (bits == 8) { for (int i = 0; i < b && p < d.Length; i++) Put(d[p++]); if ((b & 1) != 0) p++; }
                        else { int bytes = (b + 1) / 2; for (int i = 0; i < b && p + i / 2 < d.Length; i++) Put((i & 1) == 0 ? d[p + i / 2] >> 4 : d[p + i / 2] & 15); p += bytes + (bytes & 1); }
                    }
                }
                return (w, h, px, false);
            }
            int stride = ((w * bpp + 31) / 32) * 4;
            if (pixOff < 0 || (long)pixOff + (long)stride * h > d.Length) throw new InvalidDataException("BMP pixel data truncated");
            // default masks for 16 / 32 bit without BITFIELDS
            if (bpp == 16 && rm == 0) { rm = 0x7C00; gm = 0x03E0; bm = 0x001F; }
            if (bpp == 32 && rm == 0) { rm = 0x00FF0000; gm = 0x0000FF00; bm = 0x000000FF; am = 0xFF000000; }   // BI_RGB 32-bit: the 4th byte is alpha when any of it is non-zero (see below), else padding
            Chan R = new Chan(rm), G = new Chan(gm), B = new Chan(bm), A = new Chan(am);
            bool anyA = false;
            for (int y = 0; y < h; y++)
            {
                int row = pixOff + y * stride, dy = topDown ? y : h - 1 - y; int o = dy * w;
                switch (bpp)
                {
                    case 1: case 2: case 4: case 8:
                        {
                            int per = 8 / bpp, mask = (1 << bpp) - 1;
                            for (int x = 0; x < w; x++) { int b = d[row + x / per]; int idx = (b >> (8 - bpp - (x % per) * bpp)) & mask; px[o + x] = pal![idx]; }
                            break;
                        }
                    case 16:
                        for (int x = 0; x < w; x++) { uint v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(row + x * 2)); int a = am != 0 ? A.Get(v) : 255; if (a != 255) anyA = true; px[o + x] = a << 24 | R.Get(v) << 16 | G.Get(v) << 8 | B.Get(v); }
                        break;
                    case 24:
                        for (int x = 0; x < w; x++) { int q = row + x * 3; px[o + x] = unchecked((int)0xFF000000) | d[q + 2] << 16 | d[q + 1] << 8 | d[q]; }
                        break;
                    case 32:
                        for (int x = 0; x < w; x++) { uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(row + x * 4)); int a = am != 0 ? A.Get(v) : 255; if (a != 255) anyA = true; px[o + x] = a << 24 | R.Get(v) << 16 | G.Get(v) << 8 | B.Get(v); }
                        break;
                }
            }
            if (am != 0 && bpp == 32)
            {   // many writers put 0 in an "alpha" byte of a plain 32-bit BMP (comp 0, hdr 40): treat all-zero alpha as opaque
                bool allZero = true; foreach (int c in px) if ((c >>> 24) != 0) { allZero = false; break; }
                if (allZero) { for (int i = 0; i < px.Length; i++) px[i] |= unchecked((int)0xFF000000); anyA = false; }
                hasAlpha = anyA;
            }
            else hasAlpha = anyA;
            return (w, h, px, hasAlpha);
        }
        readonly struct Chan
        {
            readonly int shift, bits; readonly uint mask;
            public Chan(uint m) { mask = m; shift = 0; bits = 0; if (m == 0) return; while (((m >> shift) & 1) == 0) shift++; uint t = m >> shift; while ((t & 1) != 0) { bits++; t >>= 1; } }
            public int Get(uint v)
            {
                if (mask == 0) return 0;
                uint x = (v & mask) >> shift;
                if (bits == 8) return (int)x;
                if (bits > 8) return (int)(x >> (bits - 8));
                // expand n bits to 8 by replication
                int r = (int)x << (8 - bits); return r | (r >> bits);
            }
        }

        // ------------------------------------------------------------------------------------------------------ GIF
        /// <summary>First frame of a GIF (87a / 89a): LZW, interlace, global / local palette, transparent index from the graphic control extension. Later frames are ignored.</summary>
        public static (int width, int height, int[] argb, bool hasAlpha) DecodeGif(byte[] d)
        {
            if (d.Length < 13 || d[0] != 'G' || d[1] != 'I' || d[2] != 'F') throw new InvalidDataException("not a GIF");
            int sw = d[6] | d[7] << 8, sh = d[8] | d[9] << 8; int flags = d[10]; int bgIndex = d[11];
            int p = 13; int[]? gpal = null;
            if ((flags & 0x80) != 0) { int n = 2 << (flags & 7); gpal = ReadGifPalette(d, ref p, n); }
            if (sw <= 0 || sh <= 0) throw new InvalidDataException("GIF size");
            var px = new int[checked(sw * sh)];   // transparent (0) until the frame paints
            int transparent = -1;
            while (p < d.Length)
            {
                int b = d[p++];
                if (b == 0x3B) break;                                            // trailer
                if (b == 0x21)
                {   // extension
                    if (p >= d.Length) break; int label = d[p++];
                    if (label == 0xF9 && p + 5 < d.Length && d[p] == 4) { int pf = d[p + 1]; transparent = (pf & 1) != 0 ? d[p + 4] : -1; }
                    while (p < d.Length) { int len = d[p++]; if (len == 0) break; p += len; }
                    continue;
                }
                if (b != 0x2C) throw new InvalidDataException("GIF block 0x" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                if (p + 9 > d.Length) throw new InvalidDataException("GIF image descriptor");
                int ix = d[p] | d[p + 1] << 8, iy = d[p + 2] | d[p + 3] << 8, iw = d[p + 4] | d[p + 5] << 8, ih = d[p + 6] | d[p + 7] << 8; int ifl = d[p + 8]; p += 9;
                int[]? pal = gpal;
                if ((ifl & 0x80) != 0) { int n = 2 << (ifl & 7); pal = ReadGifPalette(d, ref p, n); }
                if (pal == null) throw new InvalidDataException("GIF without a palette");
                bool interlaced = (ifl & 0x40) != 0;
                if (p >= d.Length) throw new InvalidDataException("GIF data");
                int minCode = d[p++];
                // gather the sub-blocks
                var data = new List<byte>(d.Length - p);
                while (p < d.Length) { int len = d[p++]; if (len == 0) break; int take = Math.Min(len, d.Length - p); for (int i = 0; i < take; i++) data.Add(d[p + i]); p += take; }
                var indices = GifLzw(data, minCode, iw * ih);
                // place
                int[] rowMap = new int[ih];
                if (interlaced) { int r = 0; foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) }) for (int y = start; y < ih; y += step) rowMap[r++] = y; }
                else for (int y = 0; y < ih; y++) rowMap[y] = y;
                bool anyA = false;
                for (int r = 0; r < ih; r++)
                {
                    int y = iy + rowMap[r]; if (y < 0 || y >= sh) continue;
                    for (int x = 0; x < iw; x++)
                    {
                        int xx = ix + x; if (xx < 0 || xx >= sw) continue;
                        int i = r * iw + x; if (i >= indices.Length) break;
                        int idx = indices[i];
                        if (idx == transparent) { anyA = true; continue; }
                        px[y * sw + xx] = idx < pal.Length ? pal[idx] : unchecked((int)0xFF000000);
                    }
                }
                // pixels never painted (frame smaller than the screen) stay transparent
                if (ix != 0 || iy != 0 || iw != sw || ih != sh) anyA = true;
                if (!anyA) { foreach (int c in px) if ((c >>> 24) != 255) { anyA = true; break; } }
                return (sw, sh, px, anyA);
            }
            throw new InvalidDataException("GIF has no image");
        }
        static int[] ReadGifPalette(byte[] d, ref int p, int n)
        {
            var pal = new int[n];
            for (int i = 0; i < n && p + 2 < d.Length; i++, p += 3) pal[i] = unchecked((int)0xFF000000) | d[p] << 16 | d[p + 1] << 8 | d[p + 2];
            return pal;
        }
        static byte[] GifLzw(List<byte> data, int minCode, int count)
        {
            var o = new byte[count]; int op = 0;
            int clear = 1 << minCode, eoi = clear + 1, codeSize = minCode + 1, next = eoi + 1;
            var prefix = new int[4096]; var suffix = new byte[4096]; var first = new byte[4096];
            for (int i = 0; i < clear; i++) { prefix[i] = -1; suffix[i] = first[i] = (byte)i; }
            int bitBuf = 0, bitCnt = 0, pos = 0, prev = -1;
            var stack = new byte[4097];
            while (op < count)
            {
                while (bitCnt < codeSize && pos < data.Count) { bitBuf |= data[pos++] << bitCnt; bitCnt += 8; }
                if (bitCnt < codeSize) break;
                int code = bitBuf & ((1 << codeSize) - 1); bitBuf >>= codeSize; bitCnt -= codeSize;
                if (code == clear) { codeSize = minCode + 1; next = eoi + 1; prev = -1; continue; }
                if (code == eoi) break;
                int sp = 0, cur = code;
                if (code >= next)
                {   // KwKwK
                    if (prev < 0) break;
                    stack[sp++] = first[prev]; cur = prev;
                }
                while (cur >= clear && sp < 4096) { stack[sp++] = suffix[cur]; cur = prefix[cur]; }
                stack[sp++] = (byte)cur;
                byte f = (byte)cur;
                while (sp > 0 && op < count) o[op++] = stack[--sp];
                if (prev >= 0 && next < 4096) { prefix[next] = prev; suffix[next] = f; first[next] = first[prev]; next++; if (next == (1 << codeSize) && codeSize < 12) codeSize++; }
                prev = code;
            }
            return o;
        }

        // ------------------------------------------------------------------------------------------------------ TGA
        static bool LooksLikeTga(ReadOnlySpan<byte> d)
        {
            if (d.Length < 18) return false;
            int cmType = d[1], type = d[2], bpp = d[16], desc = d[17];
            if (cmType > 1) return false;
            if (type != 1 && type != 2 && type != 3 && type != 9 && type != 10 && type != 11) return false;
            if (bpp != 8 && bpp != 15 && bpp != 16 && bpp != 24 && bpp != 32) return false;
            if ((desc & 0xC0) != 0) return false;
            int w = d[12] | d[13] << 8, h = d[14] | d[15] << 8; if (w == 0 || h == 0) return false;
            if ((type == 1 || type == 9) && (cmType != 1 || bpp != 8)) return false;
            if ((type == 3 || type == 11) && bpp != 8) return false;
            return true;
        }
        /// <summary>Targa: types 1 / 2 / 3 and their RLE forms 9 / 10 / 11; 8-bit grey or colour-mapped, 15 / 16 / 24 / 32-bit true colour; top-left or bottom-left origin. Alpha only for 32-bit (and 16-bit with attribute bits) when any pixel is not opaque.</summary>
        public static (int width, int height, int[] argb, bool hasAlpha) DecodeTga(byte[] d)
        {
            if (!LooksLikeTga(d)) throw new InvalidDataException("not a TGA");
            int idLen = d[0], cmType = d[1], type = d[2];
            int cmFirst = d[3] | d[4] << 8, cmLen = d[5] | d[6] << 8, cmBpp = d[7];
            int w = d[12] | d[13] << 8, h = d[14] | d[15] << 8, bpp = d[16], desc = d[17];
            bool topLeft = (desc & 0x20) != 0, rightToLeft = (desc & 0x10) != 0; int attrBits = desc & 15;
            int p = 18 + idLen;
            int[]? pal = null;
            if (cmType == 1)
            {
                int esz = (cmBpp + 7) / 8; pal = new int[cmFirst + cmLen];
                for (int i = 0; i < cmLen; i++) { if (p + esz > d.Length) throw new InvalidDataException("TGA colour map"); pal[cmFirst + i] = ReadTgaPixel(d, p, cmBpp, attrBits, out _); p += esz; }
            }
            var px = new int[checked(w * h)];
            int bytes = (bpp + 7) / 8; bool rle = type >= 9; bool anyA = false;
            int total = w * h, i0 = 0;
            int run = 0, raw = 0, cur = 0;
            for (; i0 < total; i0++)
            {
                int c;
                if (rle)
                {
                    if (run == 0 && raw == 0) { if (p >= d.Length) throw new InvalidDataException("TGA data"); int hdr = d[p++]; if ((hdr & 0x80) != 0) { run = (hdr & 127) + 1; cur = Read(); } else raw = (hdr & 127) + 1; }
                    if (run > 0) { c = cur; run--; } else { c = Read(); raw--; }
                }
                else c = Read();
                int x = i0 % w, y = i0 / w;
                if (rightToLeft) x = w - 1 - x; if (!topLeft) y = h - 1 - y;
                px[y * w + x] = c;
            }
            if (anyA && bpp == 32 && attrBits == 0)
            {   // no attribute bits declared: an all-zero 4th byte is padding, not transparency
                bool allZero = true; foreach (int c in px) if ((c >>> 24) != 0) { allZero = false; break; }
                if (allZero) { for (int i = 0; i < px.Length; i++) px[i] |= unchecked((int)0xFF000000); anyA = false; }
            }
            return (w, h, px, anyA);

            int Read()
            {
                if (p + bytes > d.Length) throw new InvalidDataException("TGA data truncated");
                int c;
                if (type == 1 || type == 9) { int idx = d[p]; c = pal != null && idx < pal.Length ? pal[idx] : unchecked((int)0xFF000000); }
                else if (type == 3 || type == 11) { int g = d[p]; c = unchecked((int)0xFF000000) | g << 16 | g << 8 | g; }
                else { c = ReadTgaPixel(d, p, bpp, attrBits, out bool a); if (a) anyA = true; }
                p += bytes; return c;
            }
        }
        static int ReadTgaPixel(byte[] d, int p, int bpp, int attrBits, out bool alpha)
        {
            alpha = false;
            switch (bpp)
            {
                case 15: case 16:
                    {
                        int v = d[p] | d[p + 1] << 8; int r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
                        r = r << 3 | r >> 2; g = g << 3 | g >> 2; b = b << 3 | b >> 2;
                        int a = 255; if (bpp == 16 && attrBits == 1) { a = (v & 0x8000) != 0 ? 255 : 0; if (a == 0) alpha = true; }
                        return a << 24 | r << 16 | g << 8 | b;
                    }
                case 24: return unchecked((int)0xFF000000) | d[p + 2] << 16 | d[p + 1] << 8 | d[p];
                case 32: { int a = d[p + 3]; if (a != 255) alpha = true; return a << 24 | d[p + 2] << 16 | d[p + 1] << 8 | d[p]; }
                default: { int g = d[p]; return unchecked((int)0xFF000000) | g << 16 | g << 8 | g; }
            }
        }
    }
}
