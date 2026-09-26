using System;
using System.Collections.Generic;
using System.IO;

namespace Sr2d64CSport
{
    // =========================================================================================== JPEG (DCTDecode)
    /// <summary>Baseline and progressive JPEG decoder (Huffman, 8-bit, 1 / 3 / 4 components, restart markers, Adobe APP14 transform).
    /// Output: component samples interleaved per pixel (1 = gray, 3 = RGB after YCbCr conversion, 4 = CMYK; YCCK converted to CMYK).</summary>
    internal sealed class JpegDecoder
    {
        sealed class Comp { public int Id, H, V, Tq; public int BlocksW, BlocksH; public short[] Coef = Array.Empty<short>(); public byte[] Out = Array.Empty<byte>(); public int OutW, OutH; public Huff? Dc, Ac; public int Pred; }
        sealed class Huff { public byte[] Look = new byte[1 << 9]; public byte[] LookLen = new byte[1 << 9]; public int[] MaxCode = new int[18], ValPtr = new int[17], MinCode = new int[17]; public byte[] Vals = Array.Empty<byte>(); }
        readonly byte[] d; int p;
        readonly ushort[][] qt = new ushort[4][]; readonly Huff?[] dcT = new Huff?[4], acT = new Huff?[4];
        readonly List<Comp> comps = new List<Comp>();
        public int Width, Height; int mcuW, mcuH, mcusX, mcusY, hmax, vmax; bool progressive; int restart; public bool Adobe; public int Transform = -1; bool jfif;
        int bitBuf, bitCnt; int eobrun; bool hitMarker;
        public int Components => comps.Count;

        public JpegDecoder(byte[] data) { d = data; }
        ushort U16() { int v = d[p] << 8 | d[p + 1]; p += 2; return (ushort)v; }

        public byte[] Decode()
        {
            p = 0; if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new InvalidDataException("not a JPEG");
            p = 2; bool frame = false;
            while (p < d.Length)
            {
                if (d[p] != 0xFF) { p++; continue; }
                int m = d[p + 1]; p += 2; if (m == 0xFF || m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01) { if (m == 0xFF) p--; continue; }
                if (m == 0xD9) break;
                if (p + 2 > d.Length) break;
                int len = U16(); int segEnd = p + len - 2; if (segEnd > d.Length) segEnd = d.Length;
                switch (m)
                {
                    case 0xE0: if (len >= 7 && d[p] == 'J' && d[p + 1] == 'F' && d[p + 2] == 'I' && d[p + 3] == 'F') jfif = true; break;
                    case 0xEE: if (len >= 13 && d[p] == 'A' && d[p + 1] == 'd' && d[p + 2] == 'o' && d[p + 3] == 'b' && d[p + 4] == 'e') { Adobe = true; Transform = d[p + 11]; } break;
                    case 0xDB:
                        {
                            int q = p;
                            while (q < segEnd) { int pq = d[q] >> 4, id = d[q] & 3; q++; var t = new ushort[64]; for (int i = 0; i < 64; i++) { if (pq == 0) t[Zig[i]] = d[q++]; else { t[Zig[i]] = (ushort)(d[q] << 8 | d[q + 1]); q += 2; } } qt[id] = t; }
                            break;
                        }
                    case 0xC4:
                        {
                            int q = p;
                            while (q < segEnd)
                            {
                                int tc = d[q] >> 4, th = d[q] & 3; q++; var counts = new byte[17]; int total = 0; for (int i = 1; i <= 16; i++) { counts[i] = d[q++]; total += counts[i]; }
                                var vals = new byte[total]; Array.Copy(d, q, vals, 0, Math.Min(total, d.Length - q)); q += total;
                                var h = BuildHuff(counts, vals); if (tc == 0) dcT[th] = h; else acT[th] = h;
                            }
                            break;
                        }
                    case 0xDD: restart = U16(); break;
                    case 0xC0: case 0xC1: case 0xC2:
                        {
                            if (frame) break; frame = true; progressive = m == 0xC2;
                            int prec = d[p]; Height = d[p + 1] << 8 | d[p + 2]; Width = d[p + 3] << 8 | d[p + 4]; int nc = d[p + 5]; int q = p + 6;
                            if (prec != 8) throw new NotSupportedException("JPEG precision " + prec);
                            for (int i = 0; i < nc; i++) { comps.Add(new Comp { Id = d[q], H = Math.Max(1, d[q + 1] >> 4), V = Math.Max(1, d[q + 1] & 15), Tq = d[q + 2] & 3 }); q += 3; }
                            if (Width <= 0 || Height <= 0 || nc == 0) throw new InvalidDataException("JPEG size");
                            Prepare();
                            break;
                        }
                    case 0xC3: case 0xC5: case 0xC6: case 0xC7: case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                        throw new NotSupportedException("JPEG process (lossless / arithmetic) not supported");
                    case 0xDA:
                        {
                            int ns = d[p]; int q = p + 1; var sc = new List<Comp>();
                            for (int i = 0; i < ns; i++) { int id = d[q], tbl = d[q + 1]; q += 2; var c = comps.Find(cc => cc.Id == id) ?? (i < comps.Count ? comps[i] : null); if (c == null) continue; c.Dc = dcT[tbl >> 4]; c.Ac = acT[tbl & 15]; sc.Add(c); }
                            int ss = d[q], se = d[q + 1], ah = d[q + 2] >> 4, al = d[q + 2] & 15; q += 3; p = q;
                            DecodeScan(sc, ss, se, ah, al);
                            continue;   // p already at the next marker
                        }
                    case 0xDC: break;   // DNL
                }
                p = segEnd;
            }
            if (!frame) throw new InvalidDataException("JPEG without frame");
            return Output();
        }
        static readonly int[] Zig = { 0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63 };
        static Huff BuildHuff(byte[] counts, byte[] vals)
        {
            var h = new Huff { Vals = vals }; int code = 0, k = 0;
            for (int l = 1; l <= 16; l++)
            {
                h.ValPtr[l] = k; h.MinCode[l] = code;
                code += counts[l]; k += counts[l];
                h.MaxCode[l] = counts[l] > 0 ? code - 1 : -1;
                code <<= 1;
            }
            h.MaxCode[17] = int.MaxValue;
            // fast lookup for codes up to 9 bits
            code = 0; k = 0;
            for (int l = 1; l <= 9; l++)
            {
                for (int i = 0; i < counts[l]; i++, k++, code++)
                {
                    int shift = 9 - l; int start = code << shift;
                    for (int j = 0; j < (1 << shift); j++) { if (k < vals.Length) { h.Look[start + j] = vals[k]; h.LookLen[start + j] = (byte)l; } }
                }
                code <<= 1;
            }
            return h;
        }
        void Prepare()
        {
            hmax = 1; vmax = 1; foreach (var c in comps) { hmax = Math.Max(hmax, c.H); vmax = Math.Max(vmax, c.V); }
            mcuW = 8 * hmax; mcuH = 8 * vmax; mcusX = (Width + mcuW - 1) / mcuW; mcusY = (Height + mcuH - 1) / mcuH;
            foreach (var c in comps)
            {
                c.BlocksW = mcusX * c.H; c.BlocksH = mcusY * c.V;
                long n = (long)c.BlocksW * c.BlocksH * 64; if (n > 400_000_000) throw new NotSupportedException("JPEG too large");
                c.Coef = new short[n];
            }
        }
        // ---- bit reader
        int Bit()
        {
            if (bitCnt == 0)
            {
                if (p >= d.Length || hitMarker) { bitBuf = 0; bitCnt = 8; }
                else
                {
                    int b = d[p++];
                    if (b == 0xFF) { int b2 = p < d.Length ? d[p] : 0; if (b2 == 0) p++; else if (b2 >= 0xD0 && b2 <= 0xD7) { /* unexpected RST inside data: treat as zeros */ hitMarker = true; b = 0; p--; } else { hitMarker = true; b = 0; p--; } }
                    bitBuf = b; bitCnt = 8;
                }
            }
            bitCnt--; return (bitBuf >> bitCnt) & 1;
        }
        int Bits(int n) { int v = 0; for (int i = 0; i < n; i++) v = v << 1 | Bit(); return v; }
        int Receive(int s) { if (s == 0) return 0; int v = Bits(s); return v < (1 << (s - 1)) ? v - (1 << s) + 1 : v; }
        int DecodeHuff(Huff? h)
        {
            if (h == null) return 0;
            int code = 0;
            for (int l = 1; l <= 16; l++)
            {
                code = code << 1 | Bit();
                if (h.MaxCode[l] >= 0 && code <= h.MaxCode[l] && code >= h.MinCode[l]) { int idx = h.ValPtr[l] + code - h.MinCode[l]; return idx < h.Vals.Length ? h.Vals[idx] : 0; }
            }
            return 0;
        }
        void ResetBits() { bitBuf = 0; bitCnt = 0; hitMarker = false; }
        void DecodeScan(List<Comp> sc, int ss, int se, int ah, int al)
        {
            ResetBits(); eobrun = 0; foreach (var c in sc) c.Pred = 0;
            bool single = sc.Count == 1;
            int total; int cw = 0, ch = 0;
            if (single) { var c = sc[0]; cw = (Width * c.H / hmax + 7) / 8; ch = (Height * c.V / vmax + 7) / 8; total = cw * ch; }
            else total = mcusX * mcusY;
            int todo = restart > 0 ? restart : int.MaxValue; int done = 0;
            for (int u = 0; u < total; u++)
            {
                if (single)
                {
                    var c = sc[0]; int by = u / cw, bx = u % cw;
                    Block(c, by, bx, ss, se, ah, al);
                }
                else
                {
                    int my = u / mcusX, mx = u % mcusX;
                    foreach (var c in sc)
                        for (int v = 0; v < c.V; v++) for (int hh = 0; hh < c.H; hh++) Block(c, my * c.V + v, mx * c.H + hh, ss, se, ah, al);
                }
                done++;
                if (done == todo || hitMarker)
                {
                    // expect RSTn marker
                    bitCnt = 0; hitMarker = false;
                    while (p + 1 < d.Length && !(d[p] == 0xFF && d[p + 1] != 0 && d[p + 1] != 0xFF)) p++;
                    if (p + 1 < d.Length && d[p] == 0xFF && d[p + 1] >= 0xD0 && d[p + 1] <= 0xD7) { p += 2; done = 0; eobrun = 0; foreach (var c in sc) c.Pred = 0; ResetBits(); }
                    else break;   // some other marker: scan ends here
                }
            }
            // move to the next marker
            bitCnt = 0;
            while (p + 1 < d.Length && !(d[p] == 0xFF && d[p + 1] != 0 && !(d[p + 1] >= 0xD0 && d[p + 1] <= 0xD7))) p++;
        }
        void Block(Comp c, int by, int bx, int ss, int se, int ah, int al)
        {
            if (by >= c.BlocksH || bx >= c.BlocksW) { Skip(c, ss, se, ah, al); return; }
            int off = (by * c.BlocksW + bx) * 64; var z = c.Coef;
            if (!progressive)
            {
                int t = DecodeHuff(c.Dc); int diff = t == 0 ? 0 : Receive(t); c.Pred += diff; z[off] = (short)c.Pred;
                int k = 1;
                while (k < 64)
                {
                    int rs = DecodeHuff(c.Ac); int s = rs & 15, r = rs >> 4;
                    if (s == 0) { if (r < 15) break; k += 16; continue; }
                    k += r; if (k > 63) break; z[off + Zig[k]] = (short)Receive(s); k++;
                }
                return;
            }
            if (ss == 0)
            {   // DC scan
                if (ah == 0) { int t = DecodeHuff(c.Dc); int diff = t == 0 ? 0 : Receive(t); c.Pred += diff; z[off] = (short)(c.Pred << al); }
                else { if (Bit() != 0) z[off] |= (short)(1 << al); }
                return;
            }
            if (ah == 0)
            {   // AC first
                if (eobrun > 0) { eobrun--; return; }
                int k = ss;
                while (k <= se)
                {
                    int rs = DecodeHuff(c.Ac); int s = rs & 15, r = rs >> 4;
                    if (s == 0)
                    {
                        if (r < 15) { eobrun = (1 << r) - 1; if (r > 0) eobrun += Bits(r); break; }
                        k += 16; continue;
                    }
                    k += r; if (k > 63) break; z[off + Zig[k]] = (short)(Receive(s) * (1 << al)); k++;
                }
                return;
            }
            // AC refinement
            {
                int k = ss; int p1 = 1 << al, m1 = -1 << al;
                if (eobrun <= 0)
                {
                    for (; k <= se;)
                    {
                        int rs = DecodeHuff(c.Ac); int s = rs & 15, r = rs >> 4; int val = 0;
                        if (s == 0)
                        {
                            if (r < 15) { eobrun = (1 << r); if (r > 0) eobrun += Bits(r); break; }
                        }
                        else { val = Bit() != 0 ? p1 : m1; }
                        while (k <= se)
                        {
                            int zi = off + Zig[k];
                            if (z[zi] != 0) { if (Bit() != 0) { if ((z[zi] & p1) == 0) z[zi] += (short)(z[zi] >= 0 ? p1 : m1); } }
                            else { if (r == 0) { if (val != 0) z[zi] = (short)val; k++; break; } r--; }
                            k++;
                        }
                    }
                }
                if (eobrun > 0)
                {
                    for (; k <= se; k++) { int zi = off + Zig[k]; if (z[zi] != 0) { if (Bit() != 0) { if ((z[zi] & p1) == 0) z[zi] += (short)(z[zi] >= 0 ? p1 : m1); } } }
                    eobrun--;
                }
            }
        }
        void Skip(Comp c, int ss, int se, int ah, int al)
        {   // decode into a scratch block (blocks outside the component's area in non-interleaved scans never happen, but be safe)
            var tmp = new short[64]; var save = c.Coef; int sbw = c.BlocksW, sbh = c.BlocksH;
            c.Coef = tmp; c.BlocksW = 1; c.BlocksH = 1; Block(c, 0, 0, ss, se, ah, al); c.Coef = save; c.BlocksW = sbw; c.BlocksH = sbh;
        }
        // ---- IDCT + colour
        static readonly float[] CosT = BuildCos();
        static float[] BuildCos() { var t = new float[64]; for (int x = 0; x < 8; x++) for (int u = 0; u < 8; u++) t[x * 8 + u] = (u == 0 ? 0.353553391f : 0.5f) * MathF.Cos((2 * x + 1) * u * MathF.PI / 16); return t; }
        void Idct(Comp c)
        {
            var q = qt[c.Tq] ?? qt[0] ?? new ushort[64]; if (q.Length < 64) { var qq = new ushort[64]; for (int i = 0; i < 64; i++) qq[i] = 1; q = qq; }
            int bw = c.BlocksW, bh = c.BlocksH; c.OutW = bw * 8; c.OutH = bh * 8; c.Out = new byte[c.OutW * c.OutH];
            var tmp = new float[64]; var deq = new float[64];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int off = (by * bw + bx) * 64; bool dcOnly = true;
                    for (int i = 0; i < 64; i++) { deq[i] = c.Coef[off + i] * q[i]; if (i > 0 && deq[i] != 0) dcOnly = false; }
                    int ob = by * 8 * c.OutW + bx * 8;
                    if (dcOnly)
                    {
                        int v = Math.Clamp((int)MathF.Round(deq[0] / 8 + 128), 0, 255);
                        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) c.Out[ob + y * c.OutW + x] = (byte)v;
                        continue;
                    }
                    // rows: tmp[y*8+x] = sum_u deq[y*8+u] * cos[x][u]   (y here is the vertical frequency index v)
                    for (int v = 0; v < 8; v++)
                    {
                        int r = v * 8; bool zero = true; for (int u = 1; u < 8; u++) if (deq[r + u] != 0) { zero = false; break; }
                        if (zero) { float dc = deq[r] * 0.353553391f; for (int x = 0; x < 8; x++) tmp[r + x] = dc; continue; }
                        for (int x = 0; x < 8; x++) { float s = 0; int cx = x * 8; for (int u = 0; u < 8; u++) s += deq[r + u] * CosT[cx + u]; tmp[r + x] = s; }
                    }
                    // columns
                    for (int x = 0; x < 8; x++)
                        for (int y = 0; y < 8; y++)
                        {
                            float s = 0; int cy = y * 8; for (int v = 0; v < 8; v++) s += tmp[v * 8 + x] * CosT[cy + v];
                            c.Out[ob + y * c.OutW + x] = (byte)Math.Clamp((int)MathF.Round(s + 128), 0, 255);
                        }
                }
            c.Coef = Array.Empty<short>();
        }
        /// <summary>One source row interpolated horizontally (values scaled by 256).</summary>
        static void HRow(Comp c, int row, int[] x0, int[] x1, int[] wx, int[] dst, int width)
        {
            int ro = row * c.OutW; var src = c.Out;
            for (int x = 0; x < width; x++) { int a = src[ro + x0[x]], b = src[ro + x1[x]]; dst[x] = a * (256 - wx[x]) + b * wx[x]; }
        }
        byte[] Output()
        {
            foreach (var c in comps) Idct(c);
            int n = comps.Count; var o = new byte[checked(Width * Height * n)];   // hostile header: OverflowException instead of a wrong-sized buffer
            // per component: full-resolution components are copied; subsampled ones are upsampled with the "fancy"
            // triangle filter libjpeg / GDI+ use (centre-aligned linear interpolation: 3/4 near + 1/4 far for 2:1),
            // separable in x and y, edges clamped. Replication (the old way) shows blocky colour fringes on sharp edges.
            for (int ci = 0; ci < n; ci++)
            {
                var c = comps[ci]; int fx = hmax / Math.Max(1, c.H), fy = vmax / Math.Max(1, c.V);
                if (fx <= 1 && fy <= 1)
                {
                    for (int y = 0; y < Height; y++) { int row = Math.Min(c.OutH - 1, y) * c.OutW; int ob = y * Width * n + ci; for (int x = 0; x < Width; x++) o[ob + x * n] = c.Out[row + Math.Min(c.OutW - 1, x)]; }
                    continue;
                }
                int srcW = Math.Min(c.OutW, (Width + fx - 1) / fx), srcH = Math.Min(c.OutH, (Height + fy - 1) / fy);
                // x taps: for output x the source coordinate is (x + 0.5) / fx - 0.5 -> i0, i1 and the weight of i1 in 1/16ths... use 1/256 fixed point
                var x0 = new int[Width]; var x1 = new int[Width]; var wx = new int[Width];
                for (int x = 0; x < Width; x++)
                {
                    float sxf = (x + 0.5f) / fx - 0.5f; int i0 = (int)MathF.Floor(sxf); float t = sxf - i0;
                    x0[x] = Math.Clamp(i0, 0, srcW - 1); x1[x] = Math.Clamp(i0 + 1, 0, srcW - 1); wx[x] = (int)(t * 256 + 0.5f);
                }
                var rowBuf0 = new int[Width]; var rowBuf1 = new int[Width]; int rowIdx0 = -1, rowIdx1 = -1;
                for (int y = 0; y < Height; y++)
                {
                    float syf = (y + 0.5f) / fy - 0.5f; int j0 = (int)MathF.Floor(syf); float ty = syf - j0;
                    int r0 = Math.Clamp(j0, 0, srcH - 1), r1 = Math.Clamp(j0 + 1, 0, srcH - 1); int wy = (int)(ty * 256 + 0.5f);
                    if (rowIdx0 != r0) { HRow(c, r0, x0, x1, wx, rowBuf0, Width); rowIdx0 = r0; }
                    if (rowIdx1 != r1) { if (r1 == r0) { Array.Copy(rowBuf0, rowBuf1, Width); } else HRow(c, r1, x0, x1, wx, rowBuf1, Width); rowIdx1 = r1; }
                    int ob = y * Width * n + ci;
                    for (int x = 0; x < Width; x++) o[ob + x * n] = (byte)((rowBuf0[x] * (256 - wy) + rowBuf1[x] * wy + 32768) >> 16);
                }
            }
            bool ycc = n == 3 ? (Transform != 0 || (!Adobe && !(comps[0].Id == 'R' && comps[1].Id == 'G' && comps[2].Id == 'B'))) : n == 4 && Transform == 2;
            if (n == 3 && !Adobe && !jfif && comps[0].Id == 'R' && comps[1].Id == 'G' && comps[2].Id == 'B') ycc = false;
            if (ycc)
            {
                for (int i = 0; i < o.Length; i += n)
                {
                    float Y = o[i], cb = o[i + 1] - 128f, cr = o[i + 2] - 128f;
                    int r = (int)(Y + 1.402f * cr + 0.5f), g = (int)(Y - 0.344136f * cb - 0.714136f * cr + 0.5f), b = (int)(Y + 1.772f * cb + 0.5f);
                    r = Math.Clamp(r, 0, 255); g = Math.Clamp(g, 0, 255); b = Math.Clamp(b, 0, 255);
                    if (n == 4) { o[i] = (byte)(255 - r); o[i + 1] = (byte)(255 - g); o[i + 2] = (byte)(255 - b); }   // YCCK -> CMYK (K unchanged)
                    else { o[i] = (byte)r; o[i + 1] = (byte)g; o[i + 2] = (byte)b; }
                }
            }
            return o;
        }
    }
}
