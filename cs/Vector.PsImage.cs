using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sr2d64CSport
{
    /// <summary>PostScript raster operators (image / imagemask / colorimage, Level 1 procedure sources and Level 2 dictionaries)
    /// -> <see cref="VectorImagePaint"/> shapes. Data sources: procedures returning strings, currentfile through ASCIIHex /
    /// ASCII85 / RunLength / Flate / LZW / DCT filters (chained), plain strings. The unit square of the image is mapped by the
    /// inverse of the image matrix and the CTM, exactly like PDF images.</summary>
    internal sealed partial class PsInterp
    {
        /// <summary>Reads the image sample data from a source: procedures are executed until they return an empty string or
        /// enough bytes; files decode through their filter chain; strings are taken as they are.</summary>
        byte[] ReadImageData(object? source, long total)
        {
            var o = new List<byte>((int)Math.Min(total, 1 << 24));
            if (source is PsArray proc && proc.Exec)
            {
                int guard = 0;
                while (o.Count < total && guard++ < 1_000_000)
                {
                    int depth = St.Count; Exec(proc);
                    if (St.Count <= depth) break;
                    var v = Pop(); if (!(v is PsString str) || str.B.Length == 0) break; o.AddRange(str.B);
                }
                return o.ToArray();
            }
            if (source is PsString s) return s.B;
            if (source is PsFile f) return ReadFileData(f, total);
            return Array.Empty<byte>();
        }
        byte[] ReadFileData(PsFile f, long total)
        {
            if (f.Inner != null) { var enc = ReadFileData(f.Inner, long.MaxValue / 2); return ApplyFilter(f.Filter, enc, total); }   // chained: decode the outer (source) filter first
            if (f.Kind == 2 && f.Data != null) return ApplyFilter(f.Filter, f.Data, total);
            // currentfile (possibly filtered): the encoded bytes follow the operator in the source
            byte[] raw;
            switch (f.Filter)
            {
                case "ASCIIHexDecode": { var buf = new byte[Math.Max(0, Math.Min(total, 1 << 26))]; int n = sc.ReadHexBytes(buf, buf.Length); sc.SkipHexToEod(); raw = buf.AsSpan(0, n).ToArray(); return raw; }
                case "ASCII85Decode": { var lst = new List<byte>(); int pos = sc.Pos; PsScanner.Ascii85(sc.Data, ref pos, sc.End, lst); sc.Pos = pos; return lst.ToArray(); }
                case null: { var buf = new byte[Math.Max(0, Math.Min(total, 1 << 26))]; int n = sc.ReadRawBytes(buf, buf.Length); return buf.AsSpan(0, n).ToArray(); }
                case "SubFileDecode": { int start = sc.Pos; if (f.Eod != null && f.Eod.Length > 0 && sc.SkipPast(f.Eod)) return sc.Data.AsSpan(start, sc.Pos - f.Eod.Length - start).ToArray(); return Array.Empty<byte>(); }
                default:
                    {   // binary filter directly on currentfile (rare: Flate / LZW / DCT / RunLength on binary EPS): we cannot know its length -> take the rest up to the next known marker
                        int start = sc.Pos; var rest = sc.Data.AsSpan(start, sc.End - start).ToArray();
                        try { var dec = ApplyFilter(f.Filter, rest, total); Warn($"image data through {f.Filter} on currentfile: end of data guessed"); sc.SkipA85ToEod(); return dec; } catch (Exception e) { ImportLog.Swallowed(e, "PostScript image filter"); sc.SkipA85ToEod(); return Array.Empty<byte>(); }
                    }
            }
        }
        /// <summary>Decodes one filter (the encoded bytes already isolated). Chained filters (e.g. ASCII85 -> Flate) arrive as nested PsFile(2) objects.</summary>
        static byte[] ApplyFilter(string? filter, byte[] data, long total)
        {
            switch (filter)
            {
                case null: return data;
                case "ASCIIHexDecode": { var o = new List<byte>(data.Length / 2); int hi = -1; foreach (var c in data) { if (c == '>') break; int v = PsScanner.Hex(c); if (v < 0) continue; if (hi < 0) hi = v; else { o.Add((byte)(hi * 16 + v)); hi = -1; } } if (hi >= 0) o.Add((byte)(hi * 16)); return o.ToArray(); }
                case "ASCII85Decode": { var o = new List<byte>(data.Length * 4 / 5); int pos = 0; PsScanner.Ascii85(data, ref pos, data.Length, o); return o.ToArray(); }
                case "FlateDecode": return PdfDoc.Inflate(data);
                case "LZWDecode": return PdfDoc.LzwDecode(data, 1);
                case "RunLengthDecode": { var o = new List<byte>(); int p = 0; while (p < data.Length) { int l = data[p++]; if (l == 128) break; if (l < 128) { for (int k = 0; k <= l && p < data.Length; k++) o.Add(data[p++]); } else if (p < data.Length) { byte b = data[p++]; for (int k = 0; k < 257 - l; k++) o.Add(b); } } return o.ToArray(); }
                case "DCTDecode": return data;   // decoded by the caller (needs the JPEG header)
                default: return data;
            }
        }
        static string? OuterFilter(object? src) => src is PsFile f ? f.Filter : null;   // the last filter applied = the innermost data format (DCT means JPEG bytes come out)

        /// <summary>image / imagemask / colorimage with every operand form. kind: 0 image, 1 imagemask, 2 colorimage.</summary>
        void ImageOp(int kind)
        {
            var top = Pop();
            int w, h, bpc, ncomp; Matrix3x2 im; object? src; bool multi = false; float[]? decode = null; bool interpolate = false; string cs = gs.ColorSpace; PsArray? csArr = gs.ColorSpaceArr;
            List<object?>? sources = null;
            if (top is PsDict d)
            {
                w = (int)(DictGet(d, "Width") as double? ?? 0); h = (int)(DictGet(d, "Height") as double? ?? 0); bpc = (int)(DictGet(d, "BitsPerComponent") as double? ?? 8);
                im = DictGet(d, "ImageMatrix") is PsArray ma ? ToMatrix(ma) : new Matrix3x2(w, 0, 0, -h, 0, h);
                src = DictGet(d, "DataSource"); multi = DictGet(d, "MultipleDataSources") is bool mb && mb;
                if (DictGet(d, "Decode") is PsArray da) { decode = new float[da.A.Count]; for (int i = 0; i < da.A.Count; i++) decode[i] = (float)(da.A[i] as double? ?? 0); }
                interpolate = DictGet(d, "Interpolate") is bool ib && ib;
                if (kind == 1) { ncomp = 1; bpc = 1; } else ncomp = gs.Ncomp <= 0 ? 1 : gs.Ncomp;
                if (multi && src is PsArray sa && !sa.Exec) sources = new List<object?>(sa.A);
                if (DictGet(d, "ImageType") is double it && (int)it != 1) Warn($"ImageType {(int)it} treated as type 1");
            }
            else if (kind == 2)
            {
                ncomp = (int)(double)top!; multi = Bool();
                var procs = new List<object?>(); int np = multi ? ncomp : 1; for (int i = 0; i < np; i++) procs.Add(Pop()); procs.Reverse();
                im = ToMatrix(Arr()); bpc = Int(); h = Int(); w = Int(); src = procs[0]; if (multi) sources = procs;
                cs = ncomp == 4 ? "DeviceCMYK" : ncomp == 3 ? "DeviceRGB" : "DeviceGray"; csArr = null;
            }
            else
            {
                src = top; im = ToMatrix(Arr()); var third = Pop(); h = Int(); w = Int();
                if (kind == 1) { bpc = 1; ncomp = 1; bool pol = third is bool pb && pb; decode = pol ? new[] { 1f, 0f } : new[] { 0f, 1f }; }
                else { bpc = (int)(third as double? ?? 8); ncomp = 1; cs = "DeviceGray"; csArr = null; }
            }
            if (w <= 0 || h <= 0) return;
            if (bpc != 1 && bpc != 2 && bpc != 4 && bpc != 8 && bpc != 12 && bpc != 16) bpc = 8;
            // ---- read the samples
            long rowBytes = ((long)w * bpc * ncomp + 7) / 8, total = rowBytes * h;
            byte[] data; string? codec = null;
            if (sources != null)
            {   // one source per component (planar): interleave
                long per = ((long)w * bpc + 7) / 8 * h; var planes = new List<byte[]>();
                foreach (var s in sources) planes.Add(ReadImageData(s, per));
                if (bpc == 8) { data = new byte[checked(w * h * ncomp)]; for (int c = 0; c < planes.Count && c < ncomp; c++) { var pl = planes[c]; for (int i = 0; i < w * h && i < pl.Length; i++) data[i * ncomp + c] = pl[i]; } }
                else { Warn("planar image data with bit depth != 8 is not supported"); return; }
            }
            else
            {
                codec = OuterFilter(src) == "DCTDecode" ? "DCTDecode" : null;
                if (src is PsFile pf && pf.Kind == 2 && pf.Data == null) { Warn("image data source is an unreadable file - image skipped"); return; }
                data = ReadImageData(src, codec != null ? long.MaxValue / 2 : total);
            }
            if ((long)w * h > 60_000_000) { Warn($"image {w}x{h} too large - skipped"); return; }
            // ---- to ARGB
            int[] px; bool hasAlpha = false; bool bilevel = kind == 1;
            try
            {
                if (codec == "DCTDecode")
                {
                    var jd = new JpegDecoder(data); var samples = jd.Decode(); int n = jd.Components; w = jd.Width; h = jd.Height; px = new int[w * h];
                    bool inv = jd.Adobe && n == 4;
                    for (int i = 0, k = 0; i < px.Length; i++, k += n)
                    {
                        if (n == 1) px[i] = unchecked((int)0xFF000000) | samples[k] << 16 | samples[k] << 8 | samples[k];
                        else if (n == 4) { int cc = samples[k], mm = samples[k + 1], yy = samples[k + 2], kk = samples[k + 3]; if (inv) { cc = 255 - cc; mm = 255 - mm; yy = 255 - yy; kk = 255 - kk; } px[i] = unchecked((int)0xFF000000) | ((255 - cc) * (255 - kk) / 255) << 16 | ((255 - mm) * (255 - kk) / 255) << 8 | ((255 - yy) * (255 - kk) / 255); }
                        else px[i] = unchecked((int)0xFF000000) | samples[k] << 16 | samples[k + 1] << 8 | samples[k + 2];
                    }
                }
                else
                {
                    if (data.Length < total) { var padded = new byte[total]; Array.Copy(data, padded, data.Length); data = padded; if (data.Length == 0) return; }
                    px = new int[w * h];
                    int maxv = bpc >= 8 ? 255 : (1 << bpc) - 1; var raw = new int[w * ncomp];
                    if (kind == 1)
                    {
                        bool inv = decode != null && decode.Length > 0 && decode[0] == 1; int on = Argb(gs.R, gs.G, gs.B);
                        for (int y = 0; y < h; y++) { long row = y * rowBytes; for (int x = 0; x < w; x++) { int bit = (data[row + (x >> 3)] >> (7 - (x & 7))) & 1; if (inv) bit ^= 1; px[y * w + x] = bit == 0 ? on : 0; } }
                        hasAlpha = true;
                    }
                    else
                    {
                        // Indexed / Separation etc. go through a LUT built with the interpreter's own colour conversion (setcolor)
                        int[]? lut = null; bool lutCs = ncomp == 1 && (cs == "Indexed" || cs == "Separation" || cs == "DeviceN" || cs == "DeviceGray" || cs == "CalGray");
                        float[] dmin = new float[ncomp], dscale = new float[ncomp];
                        for (int c = 0; c < ncomp; c++) { float lo = 0, hi = cs == "Indexed" ? maxv : 1; if (cs == "Lab") { lo = c == 0 ? 0 : -100; hi = c == 0 ? 100 : 100; } if (decode != null && decode.Length >= 2 * ncomp) { lo = decode[2 * c]; hi = decode[2 * c + 1]; } dmin[c] = lo; dscale[c] = (hi - lo) / maxv; }
                        if (lutCs)
                        {
                            lut = new int[maxv + 1]; var save = (gs.R, gs.G, gs.B);
                            for (int v = 0; v <= maxv; v++) { float val = dmin[0] + v * dscale[0]; try { Push((double)val); SetColorOp(); } catch (Exception e) { ImportLog.Swallowed(e, "PostScript indexed image lookup"); SetColor(val, val, val); } lut[v] = Argb(gs.R, gs.G, gs.B); }
                            (gs.R, gs.G, gs.B) = save;
                        }
                        for (int y = 0; y < h; y++)
                        {
                            UnpackRow(data, (int)(y * rowBytes), raw, w * ncomp, bpc); int orow = y * w;
                            if (lut != null) { for (int x = 0; x < w; x++) px[orow + x] = lut[Math.Min(maxv, raw[x])]; continue; }
                            for (int x = 0, i = 0; x < w; x++, i += ncomp)
                            {
                                float c0 = dmin[0] + raw[i] * dscale[0];
                                if (ncomp >= 4) { float m = dmin[1] + raw[i + 1] * dscale[1], yy = dmin[2] + raw[i + 2] * dscale[2], k = dmin[3] + raw[i + 3] * dscale[3]; px[orow + x] = unchecked((int)0xFF000000) | B8((1 - c0) * (1 - k)) << 16 | B8((1 - m) * (1 - k)) << 8 | B8((1 - yy) * (1 - k)); }
                                else if (ncomp == 3) { float g = dmin[1] + raw[i + 1] * dscale[1], bl = dmin[2] + raw[i + 2] * dscale[2]; if (cs == "Lab") { float l = Math.Clamp(c0 / 100, 0, 1); px[orow + x] = unchecked((int)0xFF000000) | B8(l) << 16 | B8(l) << 8 | B8(l); } else px[orow + x] = unchecked((int)0xFF000000) | B8(c0) << 16 | B8(g) << 8 | B8(bl); }
                                else px[orow + x] = unchecked((int)0xFF000000) | B8(c0) << 16 | B8(c0) << 8 | B8(c0);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Warn("image could not be decoded (" + ex.Message + ")"); return; }
            // ---- place: image space (w x h, y down) -> unit square through the inverse image matrix -> user space (CTM) -> device
            if (!Matrix3x2.Invert(im, out var imInv)) return;
            var paint = new VectorImagePaint(px, w, h) { HasAlpha = hasAlpha, Smooth = !bilevel || interpolate };
            var toDevice = imInv * gs.Ctm;
            paint.Matrix = toDevice;   // image pixel (x, y) -> user space via inverse image matrix, then CTM
            var p = new VectorPath();
            var a = Vector2.Transform(new Vector2(0, 0), toDevice); var b = Vector2.Transform(new Vector2(w, 0), toDevice); var c2 = Vector2.Transform(new Vector2(w, h), toDevice); var d2 = Vector2.Transform(new Vector2(0, h), toDevice);
            p.MoveTo(a.X, a.Y).LineTo(b.X, b.Y).LineTo(c2.X, c2.Y).LineTo(d2.X, d2.Y).Close();
            img.Shapes.Add(new VectorShape { Path = p, Fill = paint, Clip = gs.Clip, IsImage = true, Opacity = gs.Alpha });
        }
        /// <summary>Illustrator XI: [matrix] llx lly urx ury h w bits ImageType AlphaChannelCount reserved bin-ascii ImageMask XI, followed by the
        /// pixel rows (ASCII hex in lines starting with %, or raw binary) and XH. ImageType 1 bitmap, 2 grey, 3 RGB, 4 CMYK.</summary>
        void AiImage()
        {
            int imageMask = Int(), binAscii = Int(); Int(); int alphaCount = Int(), imageType = Int(), bits = Int(), w = Int(), h = Int();
            PopNumbers(4);   // llx lly urx ury
            var m = St.Count > 0 && St[^1] is PsArray ma ? ToMatrix((PsArray)Pop()!) : Matrix3x2.Identity;
            int ncomp = imageType switch { 1 => 1, 2 => 1, 3 => 3, 4 => 4, _ => 1 }; if (imageType == 1) bits = 1;
            long rowBytes = imageType == 1 ? (w + 7) / 8 : (long)w * (ncomp + alphaCount) * Math.Max(1, bits / 8);
            long total = rowBytes * Math.Max(0, h);
            byte[] data;
            if (binAscii == 1)
            {
                var o = new List<byte>((int)Math.Min(total, 1 << 24)); int hi = -1;
                sc.ReadLine();   // rest of the XI line
                while (!sc.Eof)
                {
                    int save = sc.Pos; var line = sc.ReadLine(); if (line == null) break;
                    if (line.Length == 0) continue;
                    if (line[0] != '%') { if (line.TrimStart().StartsWith("XH", StringComparison.Ordinal)) break; sc.Pos = save; break; }
                    for (int i = 1; i < line.Length; i++) { int v = PsScanner.Hex(line[i]); if (v < 0) continue; if (hi < 0) hi = v; else { o.Add((byte)(hi * 16 + v)); hi = -1; } }
                }
                data = o.ToArray();
            }
            else
            {
                sc.ReadLine(); var buf = new byte[Math.Min(total, 1 << 28)]; int n = sc.ReadRawBytes(buf, buf.Length); data = n == buf.Length ? buf : buf.AsSpan(0, n).ToArray();
            }
            if (w <= 0 || h <= 0 || (long)w * h > 60_000_000) return;
            if (data.Length < total) { var padded = new byte[total]; Array.Copy(data, padded, data.Length); data = padded; }
            var px = new int[w * h]; bool hasAlpha = alphaCount > 0 || imageMask != 0;
            int fill = Argb(gs.R, gs.G, gs.B);
            for (int y = 0; y < h; y++)
            {
                long row = y * rowBytes;
                for (int x = 0; x < w; x++)
                {
                    int c;
                    if (imageType == 1) { int bit = (data[row + (x >> 3)] >> (7 - (x & 7))) & 1; c = imageMask != 0 ? (bit != 0 ? fill : 0) : (bit != 0 ? unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF)); }
                    else
                    {
                        long o = row + (long)x * (ncomp + alphaCount) * Math.Max(1, bits / 8); int step = Math.Max(1, bits / 8);
                        int S(int k) => data[o + k * step];
                        int a = alphaCount > 0 ? S(ncomp) : 255;
                        if (imageType == 2) { int g = S(0); c = a << 24 | g << 16 | g << 8 | g; }
                        else if (imageType == 3) c = a << 24 | S(0) << 16 | S(1) << 8 | S(2);
                        else { int cc = S(0), mm = S(1), yy = S(2), kk = S(3); c = a << 24 | ((255 - cc) * (255 - kk) / 255) << 16 | ((255 - mm) * (255 - kk) / 255) << 8 | ((255 - yy) * (255 - kk) / 255); }
                        if (imageMask != 0 && imageType == 2) { c = (255 - S(0)) << 24 | (fill & 0xFFFFFF); }   // grey mask: darkness = coverage of the fill colour
                    }
                    px[y * w + x] = c;
                }
            }
            // image space: w x h with y up (first data row at the top) -> user space through the XI matrix -> device
            var flip = new Matrix3x2(1, 0, 0, -1, 0, h);
            var toDevice = flip * m * gs.Ctm;
            var paint = new VectorImagePaint(px, w, h) { HasAlpha = hasAlpha, Matrix = toDevice, Smooth = imageType != 1 };
            var p = new VectorPath();
            var a0 = Vector2.Transform(new Vector2(0, 0), toDevice); var b0 = Vector2.Transform(new Vector2(w, 0), toDevice); var c0 = Vector2.Transform(new Vector2(w, h), toDevice); var d0 = Vector2.Transform(new Vector2(0, h), toDevice);
            p.MoveTo(a0.X, a0.Y).LineTo(b0.X, b0.Y).LineTo(c0.X, c0.Y).LineTo(d0.X, d0.Y).Close();
            aiLastArt = AiArt.Shape; aiLastArtStart = img.Shapes.Count;
            img.Shapes.Add(new VectorShape { Path = p, Fill = paint, Clip = gs.Clip, IsImage = true, Opacity = gs.Alpha });
            aiLastArtEnd = img.Shapes.Count;
        }
        static int B8(float v) => Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        static void UnpackRow(byte[] data, int off, int[] dst, int count, int bpc)
        {
            switch (bpc)
            {
                case 8: for (int i = 0; i < count; i++) dst[i] = off + i < data.Length ? data[off + i] : 0; break;
                case 16: for (int i = 0; i < count; i++) dst[i] = off + 2 * i < data.Length ? data[off + 2 * i] : 0; break;
                case 12: for (int i = 0; i < count; i++) { long bit = (long)i * 12; int p = off + (int)(bit >> 3); int v = p + 1 < data.Length ? (data[p] << 8 | data[p + 1]) : 0; v = (bit & 7) == 0 ? v >> 4 : v & 0xFFF; dst[i] = v >> 4; } break;
                case 1: for (int i = 0; i < count; i++) { int p = off + (i >> 3); dst[i] = p < data.Length ? (data[p] >> (7 - (i & 7))) & 1 : 0; } break;
                case 2: for (int i = 0; i < count; i++) { int p = off + (i >> 2); dst[i] = p < data.Length ? (data[p] >> (6 - 2 * (i & 3))) & 3 : 0; } break;
                case 4: for (int i = 0; i < count; i++) { int p = off + (i >> 1); dst[i] = p < data.Length ? (data[p] >> ((i & 1) == 0 ? 4 : 0)) & 15 : 0; } break;
            }
        }
    }
}
