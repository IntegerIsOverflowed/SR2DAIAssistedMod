using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Sr2d64CSport
{
    /// <summary>
    /// PDF importer: one page's vector content -> shapes. Parses the file structure (xref tables and xref streams, object
    /// streams, Flate / LZW / ASCIIHex / ASCII85 / RunLength filters with PNG and TIFF predictors, incremental updates,
    /// standard encryption with an empty user password, and a brute-force object scan when the xref is damaged), then
    /// interprets the page content stream and its form XObjects: paths, fill rules, clipping, strokes with dashes / caps /
    /// joins, colour spaces (Gray / RGB / CMYK / ICC / Indexed / Separation / DeviceN / Lab approximations), ExtGState
    /// constant alpha, axial / radial shadings (sh and shading patterns, with sampled / exponential / stitching /
    /// PostScript-calculator functions), page rotation and crop box. Text becomes glyph outlines (Vector.PdfText.cs: embedded
    /// TrueType / CFF / OpenType / Type 1 programs, Type 0 CID fonts with CMaps, Type 3 procedures, encodings and
    /// Differences; non-embedded fonts use a system stand-in found by VectorFonts). Image XObjects and inline images become
    /// picture shapes (Flate / LZW / RunLength / DCT (JPEG) / CCITT with SMask, stencil and colour-key masks; Vector.Image.cs).
    /// Tiling patterns, soft masks, blend modes, mesh shadings, JPX and JBIG2 images are reported in <see cref="VectorImage.Warnings"/>.
    /// </summary>
    internal static class PdfReader
    {
        public static int PageCount(byte[] data) { var doc = new PdfDoc(data); return doc.Pages().Count; }
        public static VectorImage Read(byte[] data, int page)
        {
            ImportLog.Begin(); List<string>? sink = null;
            try { var img = ReadCore(data, page); sink = img.Warnings; return img; }
            finally { ImportLog.End(sink); }
        }
        static VectorImage ReadCore(byte[] data, int page)
        {
            var doc = new PdfDoc(data);
            var pages = doc.Pages();
            var img = new VectorImage { PageCount = pages.Count };
            foreach (var wrn in doc.Warnings) img.Warnings.Add(wrn);
            if (pages.Count == 0) { img.Warnings.Add("no pages found"); img.ViewBox = new RectangleF(0, 0, 612, 792); img.Width = 612; img.Height = 792; return img; }
            page = Math.Clamp(page, 0, pages.Count - 1);
            var pg = pages[page];
            var info = doc.Resolve(doc.Trailer.Get("Info")) as PdfDict; if (info != null && doc.Resolve(info.Get("Title")) is byte[] t) img.Title = PdfDoc.TextString(t);
            // boxes
            var box = doc.Inherited(pg, "CropBox") as object?[] ?? doc.Inherited(pg, "MediaBox") as object?[];
            RectangleF media = box != null && box.Length >= 4 ? Box(doc, box) : new RectangleF(0, 0, 612, 792);
            var mb = doc.Inherited(pg, "MediaBox") as object?[]; if (mb != null && mb.Length >= 4) media = RectangleF.Intersect(media, Box(doc, mb)) is var ir && ir.Width > 0 && ir.Height > 0 ? ir : media;
            int rotate = (int)(doc.Resolve(doc.Inherited(pg, "Rotate")) as double? ?? 0); rotate = ((rotate % 360) + 360) % 360; rotate = rotate / 90 * 90;
            float w = media.Width, h = media.Height; bool swap = rotate == 90 || rotate == 270;
            img.ViewBox = new RectangleF(0, 0, swap ? h : w, swap ? w : h); img.Width = img.ViewBox.Width; img.Height = img.ViewBox.Height;
            // base CTM: PDF user space (y up, origin at media box corner) -> image (y down), then rotation
            var flip = new Matrix3x2(1, 0, 0, -1, -media.Left, media.Bottom);   // (x, y) -> (x - l, ury - y) ... media.Bottom == ury in the y-up box
            Matrix3x2 rot = rotate switch
            {
                90 => new Matrix3x2(0, 1, -1, 0, h, 0),
                180 => new Matrix3x2(-1, 0, 0, -1, w, h),
                270 => new Matrix3x2(0, -1, 1, 0, 0, w),
                _ => Matrix3x2.Identity
            };
            var baseCtm = flip * rot;
            var content = doc.PageContent(pg);
            var res = doc.Resolve(doc.Inherited(pg, "Resources")) as PdfDict ?? new PdfDict();
            var interp = new PdfContent(doc, img);
            interp.RunWithBase(content, res, baseCtm);
            if (img.Shapes.Count == 0)
            {   // PDF-based Illustrator file with an empty (or preview-only) page: the artwork is the PostScript in /AIPrivateData
                var ai = AiPrivateData(doc, pg, out string? why);
                if (ai != null)
                {
                    var aiImg = PsReader.Read(ai, aiFile: true);
                    if (aiImg.Shapes.Count > 0)
                    {
                        aiImg.PageCount = img.PageCount; aiImg.Title ??= img.Title;
                        foreach (var w2 in img.Warnings) if (!aiImg.Warnings.Contains(w2)) aiImg.Warnings.Add(w2);
                        aiImg.Warnings.Insert(0, "PDF page content is empty: artwork read from the embedded Illustrator data (/AIPrivateData)");
                        return aiImg;
                    }
                }
                else if (why != null) img.Warnings.Add(why);
            }
            return img;
        }
        /// <summary>
        /// Collects the Illustrator private data blocks (/PieceInfo /Illustrator /Private /AIPrivateData1..N on the page or
        /// catalog, else any object carrying such keys) into the PostScript document they encode. Handles the per-block Flate
        /// filters of AI9 - CS, and the concatenated "%AI12_CompressedData" zlib stream of CS2 - CC legacy files. Zstandard
        /// (AI 2020+ "%AI24_ZStandard_Data") is not available in the BCL: null with a reason.
        /// </summary>
        static byte[]? AiPrivateData(PdfDoc doc, PdfDict page, out string? why)
        {
            why = null;
            var blocks = new SortedDictionary<int, byte[]>();
            void Collect(PdfDict? priv)
            {
                if (priv == null) return;
                foreach (var kv in priv.D)
                {
                    if (!kv.Key.StartsWith("AIPrivateData", StringComparison.Ordinal) || !int.TryParse(kv.Key.AsSpan(13), out int idx)) continue;
                    if (doc.Resolve(kv.Value) is PdfStream st && !blocks.ContainsKey(idx)) blocks[idx] = doc.Decode(st);
                }
            }
            PdfDict? Private(PdfDict? owner) => doc.Resolve((doc.Resolve((doc.Resolve(owner?.Get("PieceInfo")) as PdfDict)?.Get("Illustrator")) as PdfDict)?.Get("Private")) as PdfDict;
            Collect(Private(page));
            if (blocks.Count == 0) Collect(Private(doc.Resolve(doc.Trailer.Get("Root")) as PdfDict));
            if (blocks.Count == 0) foreach (var num in doc.ObjectNumbers()) { if (doc.Get(num) is PdfDict d && d.Has("AIPrivateData1")) { Collect(d); if (blocks.Count > 0) break; } }
            if (blocks.Count == 0) return null;
            using var ms = new MemoryStream();
            foreach (var b in blocks.Values) ms.Write(b, 0, b.Length);
            var all = ms.ToArray();
            int z = IndexOf(all, "%AI24_ZStandard_Data"u8);
            if (z >= 0) { why = "the Illustrator data is compressed with Zstandard (AI 2020+), which this reader cannot decode"; return null; }
            int c = IndexOf(all, "%AI12_CompressedData"u8);
            if (c >= 0)
            {
                int start = c + "%AI12_CompressedData".Length;
                var inflated = Inflate(all.AsSpan(start).ToArray());
                if (inflated.Length == 0) { why = "the compressed Illustrator data could not be inflated"; return null; }
                using var o = new MemoryStream(); o.Write(all, 0, c); o.Write(inflated, 0, inflated.Length); return o.ToArray();
            }
            return all;
        }
        static int IndexOf(byte[] hay, ReadOnlySpan<byte> pat) => hay.AsSpan().IndexOf(pat);
        static byte[] Inflate(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data); using var zs = new ZLibStream(ms, CompressionMode.Decompress); using var o = new MemoryStream();
                try { zs.CopyTo(o); } catch (InvalidDataException) { /* trailing garbage after the zlib stream: keep what was read */ }
                return o.ToArray();
            }
            catch (Exception e) { ImportLog.Swallowed(e, "PDF inflate"); return Array.Empty<byte>(); }
        }
        static RectangleF Box(PdfDoc doc, object?[] a)
        {
            float V(int i) => (float)(doc.Resolve(a[i]) as double? ?? 0);
            float x0 = V(0), y0 = V(1), x1 = V(2), y1 = V(3);
            return RectangleF.FromLTRB(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
        }
    }

    // ---------------------------------------------------------------------- objects
    internal sealed class PdfRef { public readonly int Num, Gen; public PdfRef(int n, int g) { Num = n; Gen = g; } public override string ToString() => $"{Num} {Gen} R"; }
    internal sealed class PdfDict
    {
        public readonly Dictionary<string, object?> D = new Dictionary<string, object?>();
        public object? Get(string k) => D.TryGetValue(k, out var v) ? v : null;
        public bool Has(string k) => D.ContainsKey(k);
    }
    internal sealed class PdfStream { public PdfDict Dict; public byte[] Raw; public int Num, Gen; public PdfStream(PdfDict d, byte[] raw) { Dict = d; Raw = raw; } }
    /// <summary>Operator token in a content stream.</summary>
    internal sealed class PdfOp { public readonly string N; public PdfOp(string n) { N = n; } public override string ToString() => n(); string n() => N; }
    internal sealed class PdfNull { public static readonly PdfNull I = new PdfNull(); }

    // ---------------------------------------------------------------------- lexer
    internal sealed class PdfLexer
    {
        public readonly byte[] D; public int Pos; public readonly int End;
        public PdfLexer(byte[] d, int pos = 0, int end = -1) { D = d; Pos = pos; End = end < 0 ? d.Length : end; }
        public static bool Ws(int c) => c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\f' || c == 0;
        public static bool Delim(int c) => c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
        public void SkipWs() { while (Pos < End) { int c = D[Pos]; if (Ws(c)) Pos++; else if (c == '%') { while (Pos < End && D[Pos] != '\n' && D[Pos] != '\r') Pos++; } else break; } }
        /// <summary>Token: double, string (name, without '/'), byte[] (string), PdfOp (keyword / delimiter "[", "]", "<<", ">>", "{", "}"), or null at end.</summary>
        public object? Token()
        {
            SkipWs(); if (Pos >= End) return null;
            int c = D[Pos];
            switch (c)
            {
                case '(': return LitString();
                case '<': if (Pos + 1 < End && D[Pos + 1] == '<') { Pos += 2; return new PdfOp("<<"); } return HexString();
                case '>': if (Pos + 1 < End && D[Pos + 1] == '>') { Pos += 2; return new PdfOp(">>"); } Pos++; return Token();
                case '[': Pos++; return new PdfOp("["); case ']': Pos++; return new PdfOp("]");
                case '{': Pos++; return new PdfOp("{"); case '}': Pos++; return new PdfOp("}");
                case ')': Pos++; return Token();
                case '/':
                    {
                        Pos++; var sb = new StringBuilder();
                        while (Pos < End && !Ws(D[Pos]) && !Delim(D[Pos]))
                        {
                            int ch = D[Pos++];
                            if (ch == '#' && Pos + 1 < End) { int h1 = PsScanner.Hex(D[Pos]), h2 = PsScanner.Hex(D[Pos + 1]); if (h1 >= 0 && h2 >= 0) { sb.Append((char)(h1 * 16 + h2)); Pos += 2; continue; } }
                            sb.Append((char)ch);
                        }
                        return sb.ToString();
                    }
            }
            int s = Pos; while (Pos < End && !Ws(D[Pos]) && !Delim(D[Pos])) Pos++;
            if (Pos == s) { Pos++; return Token(); }
            string tok = Encoding.Latin1.GetString(D, s, Pos - s);
            if ((char.IsDigit(tok[0]) || tok[0] == '-' || tok[0] == '+' || tok[0] == '.') && double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            if (tok[0] == '-' || tok[0] == '+' || tok[0] == '.' || char.IsDigit(tok[0])) { if (TryLooseNumber(tok, out v)) return v; }
            return new PdfOp(tok);
        }
        static bool TryLooseNumber(string t, out double v)
        {   // "--5" ".-2" "3.4.5" and friends seen in the wild
            var sb = new StringBuilder(); bool dot = false, any = false;
            foreach (var ch in t) { if (char.IsDigit(ch)) { sb.Append(ch); any = true; } else if (ch == '.' && !dot) { sb.Append(ch); dot = true; } else if (ch == '-' && sb.Length == 0) { if (sb.Length == 0) sb.Append('-'); } }
            if (!any) { v = 0; return false; }
            return double.TryParse(sb.ToString().Replace("--", "-"), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
        byte[] LitString()
        {
            Pos++; var o = new List<byte>(); int depth = 1;
            while (Pos < End)
            {
                int c = D[Pos++];
                if (c == '\\')
                {
                    if (Pos >= End) break; int e = D[Pos++];
                    switch (e)
                    {
                        case 'n': o.Add(10); break; case 'r': o.Add(13); break; case 't': o.Add(9); break; case 'b': o.Add(8); break; case 'f': o.Add(12); break;
                        case '\r': if (Pos < End && D[Pos] == '\n') Pos++; break; case '\n': break;
                        default: if (e >= '0' && e <= '7') { int v = e - '0'; for (int k = 0; k < 2 && Pos < End && D[Pos] >= '0' && D[Pos] <= '7'; k++) v = v * 8 + (D[Pos++] - '0'); o.Add((byte)v); } else o.Add((byte)e); break;
                    }
                }
                else if (c == '(') { depth++; o.Add((byte)c); }
                else if (c == ')') { if (--depth == 0) break; o.Add((byte)c); }
                else o.Add((byte)c);
            }
            return o.ToArray();
        }
        byte[] HexString()
        {
            Pos++; var o = new List<byte>(); int hi = -1;
            while (Pos < End) { int c = D[Pos++]; if (c == '>') break; int v = PsScanner.Hex(c); if (v < 0) continue; if (hi < 0) hi = v; else { o.Add((byte)(hi * 16 + v)); hi = -1; } }
            if (hi >= 0) o.Add((byte)(hi * 16));
            return o.ToArray();
        }
        /// <summary>Parses one object (arrays / dicts recursively; "R" references folded). <paramref name="allowStream"/>: after a dict, "stream" data is attached.</summary>
        public object? Object(PdfDoc? doc, bool allowStream = false)
        {
            var t = Token(); return ObjectFrom(t, doc, allowStream);
        }
        public object? ObjectFrom(object? t, PdfDoc? doc, bool allowStream)
        {
            if (t is PdfOp op)
            {
                switch (op.N)
                {
                    case "[":
                        {
                            var l = new List<object?>();
                            while (true) { var e = Token(); if (e == null) break; if (e is PdfOp eo && eo.N == "]") break; if (e is PdfOp ep && (ep.N == ">>" || ep.N == "}" || ep.N == "endobj" || ep.N == "endstream")) break; l.Add(ObjectFrom(e, doc, false)); }
                            FoldRefs(l); return l.ToArray();
                        }
                    case "<<":
                        {
                            var d = new PdfDict(); var l = new List<object?>();
                            while (true) { var e = Token(); if (e == null) break; if (e is PdfOp eo && eo.N == ">>") break; if (e is PdfOp ep && (ep.N == "endobj" || ep.N == "endstream" || ep.N == "stream")) { if (ep.N == "stream") Pos -= 6; break; } l.Add(ObjectFrom(e, doc, false)); }
                            FoldRefs(l);
                            for (int i = 0; i + 1 < l.Count; i += 2) { if (l[i] is string k) d.D[k] = l[i + 1]; else i--; }
                            if (allowStream)
                            {
                                int save = Pos; var nt = Token();
                                if (nt is PdfOp so && so.N == "stream")
                                {
                                    if (Pos < End && D[Pos] == '\r') Pos++; if (Pos < End && D[Pos] == '\n') Pos++;
                                    int start = Pos; int len = (int)((doc?.Resolve(d.Get("Length")) ?? d.Get("Length")) as double? ?? -1);
                                    bool ok = len >= 0 && start + len <= End;
                                    if (ok)
                                    {   // verify "endstream" follows (Length is wrong surprisingly often)
                                        int q = start + len; int lim = Math.Min(End, q + 4); while (q < lim && Ws(D[q])) q++;
                                        ok = q + 9 <= End && D[q] == 'e' && D[q + 1] == 'n' && D[q + 2] == 'd' && D[q + 3] == 's';
                                    }
                                    if (!ok) { len = FindEndstream(start) - start; if (len < 0) len = End - start; }
                                    var raw = new byte[len]; Array.Copy(D, start, raw, 0, len); Pos = start + len;
                                    var et = Token(); if (!(et is PdfOp eo2 && eo2.N == "endstream")) { /* tolerate */ }
                                    return new PdfStream(d, raw);
                                }
                                Pos = save;
                            }
                            return d;
                        }
                    case "true": return true; case "false": return false; case "null": return PdfNull.I;
                    case "{":
                        {   // PostScript calculator function body: keep as op list
                            var l = new List<object?>();
                            while (true) { var e = Token(); if (e == null) break; if (e is PdfOp eo && eo.N == "}") break; l.Add(ObjectFrom(e, doc, false)); }
                            return new PsArray(l, true);
                        }
                }
            }
            return t;
        }
        int FindEndstream(int from)
        {
            var pat = "endstream"u8; int n = End - pat.Length;
            for (int i = from; i <= n; i++)
            {
                if (D[i] != 'e') continue; bool ok = true; for (int k = 1; k < pat.Length; k++) if (D[i + k] != pat[k]) { ok = false; break; }
                if (ok) { int e = i; if (e > from && D[e - 1] == '\n') e--; if (e > from && D[e - 1] == '\r') e--; return e; }
            }
            return -1;
        }
        static void FoldRefs(List<object?> l)
        {
            for (int i = 0; i + 2 < l.Count; i++)
                if (l[i] is double a && l[i + 1] is double b && l[i + 2] is PdfOp r && r.N == "R" && a >= 0 && a == Math.Floor(a)) { l[i] = new PdfRef((int)a, (int)b); l.RemoveRange(i + 1, 2); }
        }
    }

    // ---------------------------------------------------------------------- document
    internal sealed class PdfDoc
    {
        readonly byte[] d;
        readonly Dictionary<int, long> xref = new Dictionary<int, long>();          // num -> offset
        readonly Dictionary<int, (int strm, int idx)> inStream = new Dictionary<int, (int, int)>();
        readonly Dictionary<int, object?> cache = new Dictionary<int, object?>();
        readonly Dictionary<int, List<(int num, object? obj)>> objStreams = new Dictionary<int, List<(int, object?)>>();
        public readonly PdfDict Trailer = new PdfDict();
        /// <summary>Fonts / decoded images shared by all content streams of the document (page, forms, patterns).</summary>
        public readonly Dictionary<PdfDict, PdfFont> Fonts = new Dictionary<PdfDict, PdfFont>();
        public readonly Dictionary<(PdfStream, int), VectorImagePaint?> Images = new Dictionary<(PdfStream, int), VectorImagePaint?>();
        public readonly List<string> Warnings = new List<string>();
        bool reconstructed; readonly HashSet<int> resolving = new HashSet<int>();
        PdfCrypt? crypt;

        public PdfDoc(byte[] data)
        {
            d = data;
            try { ParseXref(); } catch (Exception ex) { Warn("xref damaged (" + ex.GetType().Name + "): rebuilt by scanning"); Reconstruct(); }
            if (Trailer.Get("Root") == null) Reconstruct();
            SetupEncryption();
        }
        void Warn(string w) { if (!Warnings.Contains(w)) Warnings.Add(w); }

        // ---- xref
        void ParseXref()
        {
            int tail = Math.Max(0, d.Length - 2048); string t = Encoding.Latin1.GetString(d, tail, d.Length - tail);
            int i = t.LastIndexOf("startxref", StringComparison.Ordinal); if (i < 0) throw new FormatException("no startxref");
            var lx = new PdfLexer(d, tail + i + 9); long off = (long)(lx.Token() as double? ?? -1);
            var seen = new HashSet<long>();
            while (off > 0 && off < d.Length && seen.Add(off))
            {
                var tr = ParseXrefSection(off);
                if (tr == null) break;
                foreach (var kv in tr.D) if (!Trailer.Has(kv.Key)) Trailer.D[kv.Key] = kv.Value;
                if (tr.Get("XRefStm") is double xs && seen.Add((long)xs)) { var tr2 = ParseXrefSection((long)xs); }
                if (tr.Get("Prev") is double prev) off = (long)prev; else break;
            }
            if (xref.Count == 0 && inStream.Count == 0) throw new FormatException("empty xref");
        }
        PdfDict? ParseXrefSection(long off)
        {
            var lx = new PdfLexer(d, (int)off); lx.SkipWs();
            var t = lx.Token();
            if (t is PdfOp op && op.N == "xref")
            {   // classic table
                while (true)
                {
                    lx.SkipWs(); int save = lx.Pos; var a = lx.Token();
                    if (a is PdfOp to && to.N == "trailer") { return lx.Object(this) as PdfDict ?? new PdfDict(); }
                    if (!(a is double start)) { lx.Pos = save; return new PdfDict(); }
                    int count = (int)(lx.Token() as double? ?? 0);
                    lx.SkipWs();
                    for (int k = 0; k < count; k++)
                    {
                        lx.SkipWs(); if (lx.Pos + 18 > d.Length) return new PdfDict();
                        // entries are "nnnnnnnnnn ggggg n" but tolerate sloppy spacing
                        var o = lx.Token(); var g = lx.Token(); var ty = lx.Token();
                        if (!(o is double oo) || !(g is double) || !(ty is PdfOp tyo)) return new PdfDict();
                        int num = (int)start + k;
                        if (tyo.N == "n" && !xref.ContainsKey(num) && !inStream.ContainsKey(num)) xref[num] = (long)oo;
                    }
                }
            }
            // xref stream: "N G obj <<...>> stream"
            lx.Pos = (int)off; var num0 = lx.Token(); lx.Token(); var ob = lx.Token();
            if (!(ob is PdfOp obo && obo.N == "obj")) return null;
            var st = lx.Object(this, true) as PdfStream; if (st == null) return null;
            var data = Decode(st);
            var W = Resolve(st.Dict.Get("W")) as object?[] ?? new object?[] { 1.0, 1.0, 1.0 };
            int w0 = (int)(W[0] as double? ?? 0), w1 = (int)(W[1] as double? ?? 0), w2 = (int)(W.Length > 2 ? W[2] as double? ?? 0 : 0);
            int size = (int)(Resolve(st.Dict.Get("Size")) as double? ?? 0);
            var index = Resolve(st.Dict.Get("Index")) as object?[] ?? new object?[] { 0.0, (double)size };
            int p = 0, rowLen = w0 + w1 + w2;
            for (int s = 0; s + 1 < index.Length; s += 2)
            {
                int first = (int)(index[s] as double? ?? 0), cnt = (int)(index[s + 1] as double? ?? 0);
                for (int k = 0; k < cnt && p + rowLen <= data.Length; k++)
                {
                    long f1 = w0 == 0 ? 1 : Read(data, ref p, w0), f2 = Read(data, ref p, w1), f3 = Read(data, ref p, w2);
                    int num = first + k;
                    if (xref.ContainsKey(num) || inStream.ContainsKey(num)) continue;
                    if (f1 == 1) xref[num] = f2; else if (f1 == 2) inStream[num] = ((int)f2, (int)f3);
                }
            }
            return st.Dict;
            static long Read(byte[] b, ref int p, int n) { long v = 0; for (int i = 0; i < n; i++) v = (v << 8) | b[p++]; return v; }
        }
        /// <summary>Scans the whole file for "N G obj" and the last trailer / Root when the xref is unusable.</summary>
        void Reconstruct()
        {
            if (reconstructed) return; reconstructed = true;
            xref.Clear(); inStream.Clear(); cache.Clear(); objStreams.Clear();
            int n = d.Length;
            for (int i = 0; i + 3 < n; i++)
            {
                if (d[i] != 'o' || d[i + 1] != 'b' || d[i + 2] != 'j') continue;
                if (i + 3 < n && !(PdfLexer.Ws(d[i + 3]) || PdfLexer.Delim(d[i + 3]))) continue;
                // walk back: ws, gen digits, ws, num digits
                int j = i - 1; while (j >= 0 && PdfLexer.Ws(d[j])) j--; int ge = j; while (j >= 0 && char.IsDigit((char)d[j])) j--; int gs = j + 1; if (gs > ge) continue;
                while (j >= 0 && PdfLexer.Ws(d[j])) j--; if (j == ge) continue; int ne = j; while (j >= 0 && char.IsDigit((char)d[j])) j--; int ns = j + 1; if (ns > ne) continue;
                if (j >= 0 && !PdfLexer.Ws(d[j]) && !PdfLexer.Delim(d[j])) continue;
                if (int.TryParse(Encoding.ASCII.GetString(d, ns, ne - ns + 1), out int num)) xref[num] = ns;   // later definitions win
            }
            // trailer dict(s)
            string all = Encoding.Latin1.GetString(d);
            int ti = all.LastIndexOf("trailer", StringComparison.Ordinal);
            while (ti >= 0)
            {
                var lx = new PdfLexer(d, ti + 7); if (lx.Object(this) is PdfDict tr) { foreach (var kv in tr.D) if (!Trailer.Has(kv.Key)) Trailer.D[kv.Key] = kv.Value; }
                if (Trailer.Has("Root")) break;
                ti = ti > 0 ? all.LastIndexOf("trailer", ti - 1, StringComparison.Ordinal) : -1;
            }
            // object streams: register their contents; xref-stream trailers carry Root too
            foreach (var num in new List<int>(xref.Keys))
            {
                var o = Get(num);
                if (o is PdfStream s)
                {
                    var type = s.Dict.Get("Type") as string;
                    if (type == "ObjStm") { try { var list = ObjStream(num, s); for (int k = 0; k < list.Count; k++) if (!xref.ContainsKey(list[k].num)) inStream[list[k].num] = (num, k); } catch (Exception e) { ImportLog.Swallowed(e, "PDF object stream " + num); } }
                    else if (type == "XRef") { foreach (var kv in s.Dict.D) if (!Trailer.Has(kv.Key)) Trailer.D[kv.Key] = kv.Value; }
                }
            }
            if (!Trailer.Has("Root") || !(Resolve(Trailer.Get("Root")) is PdfDict))
            {   // find a Catalog
                Trailer.D.Remove("Root");
                foreach (var num in AllObjectNumbers()) if (Get(num) is PdfDict dd && dd.Get("Type") as string == "Catalog") { Trailer.D["Root"] = new PdfRef(num, 0); break; }
            }
        }
        IEnumerable<int> AllObjectNumbers() { var l = new List<int>(xref.Keys); l.AddRange(inStream.Keys); l.Sort(); return l; }
        public IEnumerable<int> ObjectNumbers() => AllObjectNumbers();

        // ---- objects
        public object? Resolve(object? o)
        {
            int guard = 0;
            while (o is PdfRef r && guard++ < 64) o = Get(r.Num);
            return o is PdfNull ? null : o;
        }
        public object? Get(int num)
        {
            if (cache.TryGetValue(num, out var c)) return c;
            if (!resolving.Add(num)) return null;
            try
            {
                object? o = null;
                try { o = Load(num); }
                catch (Exception e) { ImportLog.Swallowed(e, "PDF object " + num); o = null; }
                if (o == null && !reconstructed) { Reconstruct(); try { o = Load(num); } catch (Exception e) { ImportLog.Swallowed(e, "PDF object " + num + " after xref rebuild"); } }
                cache[num] = o; return o;
            }
            finally { resolving.Remove(num); }
        }
        object? Load(int num)
        {
            if (xref.TryGetValue(num, out long off))
            {
                if (off < 0 || off >= d.Length) return null;
                var lx = new PdfLexer(d, (int)off);
                var n = lx.Token(); var g = lx.Token(); var ob = lx.Token();
                if (!(n is double nd) || (int)nd != num || !(ob is PdfOp o && o.N == "obj"))
                {   // offset is off by a little (common): look around
                    int found = FindObjHeader(num, (int)off); if (found < 0) return null;
                    lx = new PdfLexer(d, found); lx.Token(); g = lx.Token(); lx.Token();
                }
                int gen = (int)(g as double? ?? 0);
                var obj = lx.Object(this, true);
                if (obj is PdfStream s) { s.Num = num; s.Gen = gen; }
                if (crypt != null) obj = crypt.Decrypt(obj, num, gen);
                return obj;
            }
            if (inStream.TryGetValue(num, out var loc))
            {
                if (!objStreams.TryGetValue(loc.strm, out var list))
                {
                    var s = Resolve(new PdfRef(loc.strm, 0)) as PdfStream; if (s == null) return null;
                    list = ObjStream(loc.strm, s);
                }
                if (loc.idx >= 0 && loc.idx < list.Count && list[loc.idx].num == num) return list[loc.idx].obj;
                foreach (var (n2, o2) in list) if (n2 == num) return o2;
            }
            return null;
        }
        int FindObjHeader(int num, int near)
        {
            string pat = num + " "; int lo = Math.Max(0, near - 200), hi = Math.Min(d.Length, near + 2048);
            for (int i = lo; i < hi; i++)
            {
                if (i > 0 && !(PdfLexer.Ws(d[i - 1]) || PdfLexer.Delim(d[i - 1]))) continue;
                var lx = new PdfLexer(d, i, hi); if (lx.Token() is double a && (int)a == num && lx.Token() is double && lx.Token() is PdfOp o && o.N == "obj") return i;
            }
            return -1;
        }
        List<(int num, object? obj)> ObjStream(int strmNum, PdfStream s)
        {
            var list = new List<(int, object?)>();
            objStreams[strmNum] = list;
            var data = Decode(s);
            int n = (int)(Resolve(s.Dict.Get("N")) as double? ?? 0), first = (int)(Resolve(s.Dict.Get("First")) as double? ?? 0);
            var hdr = new PdfLexer(data, 0, Math.Min(first, data.Length));
            var offs = new List<(int, int)>();
            for (int i = 0; i < n; i++) { var a = hdr.Token(); var b = hdr.Token(); if (a is double an && b is double bn) offs.Add(((int)an, (int)bn)); else break; }
            foreach (var (num, off) in offs)
            {
                int p = first + off; if (p < 0 || p >= data.Length) { list.Add((num, null)); continue; }
                var lx = new PdfLexer(data, p); list.Add((num, lx.Object(this)));
            }
            return list;
        }

        // ---- streams
        public byte[] Decode(PdfStream s)
        {
            byte[] data = s.Raw;
            var filters = new List<string>(); var parms = new List<PdfDict?>();
            var f = Resolve(s.Dict.Get("Filter"));
            var dp = Resolve(s.Dict.Get("DecodeParms") ?? s.Dict.Get("DP"));
            if (f is string fs) { filters.Add(fs); parms.Add(dp as PdfDict); }
            else if (f is object?[] fa) { for (int i = 0; i < fa.Length; i++) { if (Resolve(fa[i]) is string fn) { filters.Add(fn); parms.Add(dp is object?[] da && i < da.Length ? Resolve(da[i]) as PdfDict : dp as PdfDict); } } }
            for (int i = 0; i < filters.Count; i++)
            {
                switch (filters[i])
                {
                    case "FlateDecode": case "Fl": data = Inflate(data); data = Predictor(data, parms[i]); break;
                    case "LZWDecode": case "LZW": data = Lzw(data, (int)(Resolve(parms[i]?.Get("EarlyChange")) as double? ?? 1)); data = Predictor(data, parms[i]); break;
                    case "ASCIIHexDecode": case "AHx": { var o = new List<byte>(); int hi = -1; foreach (var b in data) { if (b == '>') break; int v = PsScanner.Hex(b); if (v < 0) continue; if (hi < 0) hi = v; else { o.Add((byte)(hi * 16 + v)); hi = -1; } } if (hi >= 0) o.Add((byte)(hi * 16)); data = o.ToArray(); break; }
                    case "ASCII85Decode": case "A85": { var o = new List<byte>(); int p = 0; if (data.Length > 1 && data[0] == '<' && data[1] == '~') p = 2; PsScanner.Ascii85(data, ref p, data.Length, o); data = o.ToArray(); break; }
                    case "RunLengthDecode": case "RL": { var o = new List<byte>(); int p = 0; while (p < data.Length) { int l = data[p++]; if (l == 128) break; if (l < 128) { for (int k = 0; k <= l && p < data.Length; k++) o.Add(data[p++]); } else { if (p < data.Length) { byte b = data[p++]; for (int k = 0; k < 257 - l; k++) o.Add(b); } } } data = o.ToArray(); break; }
                    case "DCTDecode": case "DCT": case "JPXDecode": case "CCITTFaxDecode": case "CCF": case "JBIG2Decode": return data;   // image codecs: see DecodeImageData
                    case "Crypt": break;
                    default: Warn($"stream filter {filters[i]} not supported"); return data;
                }
            }
            return data;
        }
        /// <summary>Image stream data: the generic filters applied, the final image codec (DCT / CCITT / JPX / JBIG2) left for the caller with its parameters.</summary>
        public byte[] DecodeImageData(PdfStream s, out string? codec, out PdfDict? codecParms)
        {
            codec = null; codecParms = null;
            var f = Resolve(s.Dict.Get("Filter")); var dp = Resolve(s.Dict.Get("DecodeParms") ?? s.Dict.Get("DP"));
            string? last = f as string ?? (f is object?[] fa && fa.Length > 0 ? Resolve(fa[^1]) as string : null);
            if (last == "DCTDecode" || last == "DCT" || last == "JPXDecode" || last == "CCITTFaxDecode" || last == "CCF" || last == "JBIG2Decode")
            {
                codec = last switch { "DCT" => "DCTDecode", "CCF" => "CCITTFaxDecode", _ => last };
                codecParms = f is string ? dp as PdfDict : dp is object?[] da && da.Length > 0 ? Resolve(da[^1]) as PdfDict : dp as PdfDict;
            }
            return Decode(s);
        }
        public static byte[] Inflate(byte[] data)
        {
            int start = 0; while (start < data.Length && PdfLexer.Ws(data[start])) start++;
            try
            {
                using var ms = new MemoryStream(data, start, data.Length - start); using var z = new ZLibStream(ms, CompressionMode.Decompress); using var o = new MemoryStream();
                try { z.CopyTo(o); } catch (InvalidDataException) { /* keep what we got (truncated streams) */ }
                if (o.Length > 0) return o.ToArray();
            }
            catch (Exception e) { ImportLog.Swallowed(e, "PDF FlateDecode (zlib), trying raw deflate"); }
            try
            {   // raw deflate without the zlib header, or a corrupt header
                using var ms = new MemoryStream(data, start + (data.Length - start >= 2 && (data[start] & 0x0F) == 8 ? 2 : 0), data.Length - start - (data.Length - start >= 2 && (data[start] & 0x0F) == 8 ? 2 : 0));
                using var z = new DeflateStream(ms, CompressionMode.Decompress); using var o = new MemoryStream();
                try { z.CopyTo(o); } catch (InvalidDataException) { }
                return o.ToArray();
            }
            catch (Exception e) { ImportLog.Swallowed(e, "PDF FlateDecode (raw deflate)"); return Array.Empty<byte>(); }
        }
        byte[] Predictor(byte[] data, PdfDict? p)
        {
            if (p == null) return data;
            int pred = (int)(Resolve(p.Get("Predictor")) as double? ?? 1); if (pred <= 1) return data;
            int colors = (int)(Resolve(p.Get("Colors")) as double? ?? 1), bpc = (int)(Resolve(p.Get("BitsPerComponent")) as double? ?? 8), columns = (int)(Resolve(p.Get("Columns")) as double? ?? 1);
            int bpp = Math.Max(1, colors * bpc / 8), rowLen = (columns * colors * bpc + 7) / 8;
            if (pred == 2)
            {   // TIFF predictor (8-bit only)
                if (bpc != 8) return data;
                for (int r = 0; r + rowLen <= data.Length; r += rowLen) for (int i = bpp; i < rowLen; i++) data[r + i] += data[r + i - bpp];
                return data;
            }
            // PNG predictors: each row is prefixed by a filter type byte
            int rows = data.Length / (rowLen + 1); var o = new byte[rows * rowLen]; var prev = new byte[rowLen];
            for (int r = 0; r < rows; r++)
            {
                int ft = data[r * (rowLen + 1)]; int src = r * (rowLen + 1) + 1, dst = r * rowLen;
                for (int i = 0; i < rowLen; i++)
                {
                    int raw = data[src + i], left = i >= bpp ? o[dst + i - bpp] : 0, up = prev[i], ul = i >= bpp ? prev[i - bpp] : 0, v;
                    switch (ft)
                    {
                        case 1: v = raw + left; break; case 2: v = raw + up; break; case 3: v = raw + ((left + up) >> 1); break;
                        case 4: { int pa = Math.Abs(up - ul), pb = Math.Abs(left - ul), pc = Math.Abs(left + up - 2 * ul); v = raw + (pa <= pb && pa <= pc ? left : pb <= pc ? up : ul); break; }
                        default: v = raw; break;
                    }
                    o[dst + i] = (byte)v;
                }
                Array.Copy(o, dst, prev, 0, rowLen);
            }
            return o;
        }
        public static byte[] LzwDecode(byte[] data, int early) => Lzw(data, early);
        static byte[] Lzw(byte[] data, int early)
        {
            var o = new List<byte>(); var table = new List<byte[]>(); void Reset() { table.Clear(); for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i }); table.Add(Array.Empty<byte>()); table.Add(Array.Empty<byte>()); }
            Reset(); int codeLen = 9; byte[]? prev = null; long bitBuf = 0; int bits = 0, p = 0;
            while (true)
            {
                while (bits < codeLen && p < data.Length) { bitBuf = (bitBuf << 8) | data[p++]; bits += 8; }
                if (bits < codeLen) break;
                int code = (int)((bitBuf >> (bits - codeLen)) & ((1 << codeLen) - 1)); bits -= codeLen;
                if (code == 256) { Reset(); codeLen = 9; prev = null; continue; }
                if (code == 257) break;
                byte[] entry;
                if (code < table.Count) { entry = table[code]; if (prev != null) { var ne = new byte[prev.Length + 1]; prev.CopyTo(ne, 0); ne[^1] = entry[0]; table.Add(ne); } }
                else if (prev != null) { entry = new byte[prev.Length + 1]; prev.CopyTo(entry, 0); entry[^1] = prev[0]; table.Add(entry); }
                else break;
                o.AddRange(entry); prev = entry;
                if (table.Count + early - 1 >= (1 << codeLen) && codeLen < 12) codeLen++;
            }
            return o.ToArray();
        }

        // ---- pages
        public List<PdfDict> Pages()
        {
            var list = new List<PdfDict>();
            var root = Resolve(Trailer.Get("Root")) as PdfDict;
            var pagesObj = root != null ? Resolve(root.Get("Pages")) as PdfDict : null;
            var seen = new HashSet<PdfDict>();
            if (pagesObj != null) Walk(pagesObj, list, seen, 0);
            if (list.Count == 0)
            {   // no tree: collect page objects by scanning
                Reconstruct();
                foreach (var num in AllObjectNumbers()) if (Resolve(new PdfRef(num, 0)) is PdfDict pd && pd.Get("Type") as string == "Page" && seen.Add(pd)) list.Add(pd);
            }
            return list;
        }
        readonly Dictionary<PdfDict, PdfDict?> parentOf = new Dictionary<PdfDict, PdfDict?>();
        void Walk(PdfDict node, List<PdfDict> list, HashSet<PdfDict> seen, int depth)
        {
            if (depth > 64 || !seen.Add(node)) return;
            var type = node.Get("Type") as string;
            var kids = Resolve(node.Get("Kids")) as object?[];
            if (type == "Page" || (kids == null && node.Has("Contents"))) { list.Add(node); return; }
            if (kids == null) return;
            foreach (var k in kids) if (Resolve(k) is PdfDict kd) { if (!parentOf.ContainsKey(kd)) parentOf[kd] = node; Walk(kd, list, seen, depth + 1); }
        }
        public object? Inherited(PdfDict page, string key)
        {
            var n = page; int guard = 0;
            while (n != null && guard++ < 64)
            {
                var v = n.Get(key); if (v != null) return Resolve(v);
                n = (parentOf.TryGetValue(n, out var p) ? p : null) ?? Resolve(n.Get("Parent")) as PdfDict;
            }
            return null;
        }
        public byte[] PageContent(PdfDict page)
        {
            var c = Resolve(page.Get("Contents"));
            if (c is PdfStream s) return Decode(s);
            if (c is object?[] arr)
            {
                using var ms = new MemoryStream();
                foreach (var e in arr) if (Resolve(e) is PdfStream es) { var b = Decode(es); ms.Write(b, 0, b.Length); ms.WriteByte((byte)'\n'); }
                return ms.ToArray();
            }
            return Array.Empty<byte>();
        }
        public static string TextString(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            return Encoding.Latin1.GetString(b);
        }

        // ---- encryption (standard security handler, empty user password)
        void SetupEncryption()
        {
            var enc = Resolve(Trailer.Get("Encrypt")) as PdfDict; if (enc == null) return;
            try
            {
                var ids = Resolve(Trailer.Get("ID")) as object?[]; byte[] id0 = ids != null && ids.Length > 0 && Resolve(ids[0]) is byte[] b0 ? b0 : Array.Empty<byte>();
                crypt = new PdfCrypt(this, enc, id0);
                cache.Clear(); objStreams.Clear();   // anything loaded before must be re-read decrypted
                if (!crypt.Ok) { Warn("encrypted PDF: the user password is not empty - content cannot be read"); crypt = null; }
            }
            catch (Exception ex) { Warn("encryption not supported: " + ex.Message); crypt = null; }
        }
        internal object? ResolveNoCrypt(object? o) => Resolve(o);
    }

    internal sealed class PdfCrypt
    {
        readonly byte[] key; readonly bool aes, identityStrings, identityStreams; public readonly bool Ok; readonly int v;
        static readonly byte[] Pad = { 0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08, 0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A };
        public PdfCrypt(PdfDoc doc, PdfDict enc, byte[] id0)
        {
            string filter = doc.Resolve(enc.Get("Filter")) as string ?? ""; if (filter != "Standard") throw new NotSupportedException("security handler " + filter);
            v = (int)(doc.Resolve(enc.Get("V")) as double? ?? 0); int r = (int)(doc.Resolve(enc.Get("R")) as double? ?? 2);
            int length = (int)(doc.Resolve(enc.Get("Length")) as double? ?? 40) / 8;
            byte[] O = doc.Resolve(enc.Get("O")) as byte[] ?? Array.Empty<byte>(), U = doc.Resolve(enc.Get("U")) as byte[] ?? Array.Empty<byte>();
            int P = (int)(long)(doc.Resolve(enc.Get("P")) as double? ?? -1);
            bool encryptMetadata = !(doc.Resolve(enc.Get("EncryptMetadata")) is bool em) || em;
            string stmf = "Identity", strf = "Identity";
            if (v >= 4)
            {
                stmf = doc.Resolve(enc.Get("StmF")) as string ?? "Identity"; strf = doc.Resolve(enc.Get("StrF")) as string ?? "Identity";
                var cf = doc.Resolve(enc.Get("CF")) as PdfDict; var std = cf != null ? doc.Resolve(cf.Get(stmf != "Identity" ? stmf : strf)) as PdfDict : null;
                string cfm = std != null ? doc.Resolve(std.Get("CFM")) as string ?? "None" : "None";
                aes = cfm == "AESV2" || cfm == "AESV3";
                if (std != null && doc.Resolve(std.Get("Length")) is double cl) length = cl > 40 ? (int)cl / 8 : (int)cl;
                identityStreams = stmf == "Identity"; identityStrings = strf == "Identity";
            }
            if (r >= 5)
            {   // AES-256: key = intermediate from the U string validation with the empty password
                byte[] vsalt = U.AsSpan(32, 8).ToArray(), ksalt = U.AsSpan(40, 8).ToArray();
                byte[] hash = r == 5 ? SHA256.HashData(vsalt) : Hash2B(Array.Empty<byte>(), vsalt, Array.Empty<byte>());
                Ok = SpanEq(hash, U.AsSpan(0, 32));
                byte[] ikey = r == 5 ? SHA256.HashData(ksalt) : Hash2B(Array.Empty<byte>(), ksalt, Array.Empty<byte>());
                byte[] UE = doc.Resolve(enc.Get("UE")) as byte[] ?? new byte[32];
                using var a = Aes.Create(); a.Key = ikey; a.IV = new byte[16]; a.Mode = CipherMode.CBC; a.Padding = PaddingMode.None;
                key = a.CreateDecryptor().TransformFinalBlock(UE, 0, 32); aes = true; return;
            }
            // algorithm 2 with the empty user password
            using var md5 = MD5.Create();
            var input = new List<byte>(); input.AddRange(Pad); input.AddRange(O.Length >= 32 ? O.AsSpan(0, 32).ToArray() : O);
            input.AddRange(BitConverter.GetBytes(P)); input.AddRange(id0);
            if (r >= 4 && !encryptMetadata) input.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            byte[] k = md5.ComputeHash(input.ToArray());
            int n = r == 2 ? 5 : Math.Clamp(length, 5, 16);
            if (r >= 3) for (int i = 0; i < 50; i++) k = md5.ComputeHash(k, 0, n);
            key = k.AsSpan(0, n).ToArray();
            // validate (algorithm 4 / 5)
            if (r == 2) Ok = SpanEq(Rc4(key, Pad), U.AsSpan(0, Math.Min(32, U.Length)));
            else
            {
                var h = md5.ComputeHash(Concat(Pad, id0)); var x = Rc4(key, h);
                for (int i = 1; i <= 19; i++) { var kk = new byte[key.Length]; for (int j = 0; j < kk.Length; j++) kk[j] = (byte)(key[j] ^ i); x = Rc4(kk, x); }
                Ok = U.Length >= 16 && SpanEq(x.AsSpan(0, 16).ToArray(), U.AsSpan(0, 16));
            }
            if (!Ok) Ok = true;   // some writers store a wrong U; decrypting with the derived key is still worth a try
        }
        static byte[] Hash2B(byte[] pwd, byte[] salt, byte[] udata)
        {   // ISO 32000-2 algorithm 2.B
            byte[] k = SHA256.HashData(Concat(pwd, salt, udata));
            for (int i = 0; ; i++)
            {
                var k1 = new List<byte>(); for (int j = 0; j < 64; j++) { k1.AddRange(pwd); k1.AddRange(k); k1.AddRange(udata); }
                using var a = Aes.Create(); a.Key = k.AsSpan(0, 16).ToArray(); a.IV = k.AsSpan(16, 16).ToArray(); a.Mode = CipherMode.CBC; a.Padding = PaddingMode.None;
                byte[] e = a.CreateEncryptor().TransformFinalBlock(k1.ToArray(), 0, k1.Count);
                int sum = 0; for (int j = 0; j < 16; j++) sum += e[j];
                int mod = sum % 3;
                k = mod == 0 ? SHA256.HashData(e) : mod == 1 ? SHA384.HashData(e) : SHA512.HashData(e);
                if (i >= 63 && e[^1] <= i - 32) break;
            }
            return k.AsSpan(0, 32).ToArray();
        }
        static byte[] Concat(params byte[][] parts) { var l = new List<byte>(); foreach (var p in parts) l.AddRange(p); return l.ToArray(); }
        static bool SpanEq(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.Length >= b.Length && a.Slice(0, b.Length).SequenceEqual(b) || b.Length >= a.Length && b.Slice(0, a.Length).SequenceEqual(a);
        static byte[] Rc4(byte[] key, byte[] data)
        {
            var s = new byte[256]; for (int i = 0; i < 256; i++) s[i] = (byte)i;
            for (int i = 0, j = 0; i < 256; i++) { j = (j + s[i] + key[i % key.Length]) & 255; (s[i], s[j]) = (s[j], s[i]); }
            var o = new byte[data.Length];
            for (int k = 0, i = 0, j = 0; k < data.Length; k++) { i = (i + 1) & 255; j = (j + s[i]) & 255; (s[i], s[j]) = (s[j], s[i]); o[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 255]); }
            return o;
        }
        byte[] ObjKey(int num, int gen)
        {
            if (v >= 5) return key;
            var input = new List<byte>(key); input.Add((byte)num); input.Add((byte)(num >> 8)); input.Add((byte)(num >> 16)); input.Add((byte)gen); input.Add((byte)(gen >> 8));
            if (aes) input.AddRange(new byte[] { 0x73, 0x41, 0x6C, 0x54 });
            var h = MD5.HashData(input.ToArray()); int n = Math.Min(key.Length + 5, 16); return h.AsSpan(0, n).ToArray();
        }
        byte[] DecryptBytes(byte[] data, int num, int gen)
        {
            var k = ObjKey(num, gen);
            if (!aes) return Rc4(k, data);
            if (data.Length < 16) return data;
            try
            {
                using var a = Aes.Create(); a.Key = k; a.IV = data.AsSpan(0, 16).ToArray(); a.Mode = CipherMode.CBC; a.Padding = PaddingMode.None;
                int len = (data.Length - 16) / 16 * 16; var o = a.CreateDecryptor().TransformFinalBlock(data, 16, len);
                int pad = o.Length > 0 ? o[^1] : 0; if (pad >= 1 && pad <= 16 && pad <= o.Length) Array.Resize(ref o, o.Length - pad);
                return o;
            }
            catch (Exception e) { ImportLog.Swallowed(e, "PDF stream filter"); return data; }
        }
        public object? Decrypt(object? o, int num, int gen)
        {
            switch (o)
            {
                case byte[] b: return identityStrings && v >= 4 ? b : DecryptBytes(b, num, gen);
                case object?[] a: for (int i = 0; i < a.Length; i++) a[i] = Decrypt(a[i], num, gen); return a;
                case PdfDict d: foreach (var k in new List<string>(d.D.Keys)) d.D[k] = Decrypt(d.D[k], num, gen); return d;
                case PdfStream s:
                    Decrypt(s.Dict, num, gen);
                    if (s.Dict.Get("Type") as string == "XRef") return s;
                    if (!(identityStreams && v >= 4)) s.Raw = DecryptBytes(s.Raw, num, gen);
                    return s;
                default: return o;
            }
        }
    }

    // ---------------------------------------------------------------------- content stream interpreter
    internal sealed partial class PdfContent
    {
        readonly PdfDoc doc; readonly VectorImage img; readonly HashSet<string> warned = new HashSet<string>();
        sealed class GS
        {
            public Matrix3x2 Ctm; public VectorClip? Clip;
            public VectorPaint? Fill = VectorColor.Black, Stroke = VectorColor.Black;
            public string FillCs = "DeviceGray", StrokeCs = "DeviceGray"; public object? FillCsObj, StrokeCsObj;
            public float LineWidth = 1, Miter = 10, Alpha = 1, StrokeAlpha = 1; public VectorCap Cap; public VectorJoin Join; public float[]? Dash; public float DashOffset;
            public bool SoftMask;
            // text state
            public float CharSp, WordSp, Hscale = 1, Leading, Rise, FontSize; public int RenderMode; public PdfFont? Font;
            public GS Clone() => (GS)MemberwiseClone();
        }
        GS gs = new GS(); readonly List<GS> stack = new List<GS>();
        VectorPath path = new VectorPath(); bool hasCur; float curX, curY, startX, startY;   // user space of the current CTM? no: we store device coords directly
        int pendingClip; // 0 none, 1 nonzero, 2 evenodd
        long opCount;
        readonly Dictionary<PdfDict, Func<double, double[]>?> fnCache = new Dictionary<PdfDict, Func<double, double[]>?>();

        public PdfContent(PdfDoc doc, VectorImage img) { this.doc = doc; this.img = img; }
        void Warn(string w) { if (warned.Add(w)) img.Warnings.Add(w); }

        // marked content: "/OC /MC0 BDC ... EMC" = an optional-content group (an Illustrator / InDesign / CAD layer). Its /Name
        // becomes the Group of every shape emitted inside (nested = "outer/inner"), so layers are addressable by name like SVG groups.
        readonly List<(string? name, int start)> mc = new List<(string?, int)>();
        void MarkedBegin(List<object?> o, PdfDict res)
        {
            string? name = null;
            if (o.Count >= 2 && o[0] is string tag && tag == "OC")
            {
                PdfDict? g = o[1] as PdfDict;
                if (g == null && o[1] is string key && doc.Resolve(res.Get("Properties")) is PdfDict props) g = doc.Resolve(props.Get(key)) as PdfDict;
                if (g != null)
                {
                    if (doc.Resolve(g.Get("Type")) as string == "OCMD") { var ocgs = doc.Resolve(g.Get("OCGs")); g = ocgs as PdfDict ?? (ocgs is object?[] arr && arr.Length > 0 ? doc.Resolve(arr[0]) as PdfDict : null); }
                    if (g != null && doc.Resolve(g.Get("Name")) is byte[] nb) { name = PdfDoc.TextString(nb).Trim(); if (name.Length == 0) name = null; }
                }
            }
            mc.Add((name, img.Shapes.Count));
        }
        void MarkedEnd()
        {
            if (mc.Count == 0) return;
            var (name, start) = mc[^1]; mc.RemoveAt(mc.Count - 1);
            if (name == null) return;
            for (int i = start; i < img.Shapes.Count; i++) { var s = img.Shapes[i]; s.Group = string.IsNullOrEmpty(s.Group) ? name : name + "/" + s.Group; }
        }

        Vector2 Dev(float x, float y) => Vector2.Transform(new Vector2(x, y), gs.Ctm);
        void MoveTo(float x, float y) { var d = Dev(x, y); path.MoveTo(d.X, d.Y); curX = startX = x; curY = startY = y; hasCur = true; }
        void LineTo(float x, float y) { if (!hasCur) { MoveTo(x, y); return; } var d = Dev(x, y); path.LineTo(d.X, d.Y); curX = x; curY = y; }
        void CurveTo(float x1, float y1, float x2, float y2, float x3, float y3) { if (!hasCur) MoveTo(x1, y1); var a = Dev(x1, y1); var b = Dev(x2, y2); var c = Dev(x3, y3); path.CurveTo(a.X, a.Y, b.X, b.Y, c.X, c.Y); curX = x3; curY = y3; }
        void Close() { if (hasCur) { path.Close(); curX = startX; curY = startY; } }

        public void Run(byte[] content, PdfDict res, Matrix3x2 ctm, int depth)
        {
            gs.Ctm = ctm;
            var lx = new PdfLexer(content); var ops = new List<object?>(16);
            while (true)
            {
                object? t;
                try { t = lx.Token(); } catch (Exception e) { ImportLog.Swallowed(e, "PDF content lexer"); break; }
                if (t == null) break;
                if (t is PdfOp op)
                {
                    switch (op.N)
                    {
                        case "[": case "<<": case "{": ops.Add(lx.ObjectFrom(t, doc, false)); continue;
                        case "true": ops.Add(true); continue; case "false": ops.Add(false); continue; case "null": ops.Add(null); continue;
                        case "BI": try { InlineImage(lx, res); } catch (Exception ex) { Warn("inline image skipped (" + ex.Message + ")"); } ops.Clear(); continue;
                    }
                    if (++opCount > 5_000_000) { Warn("content stream too long - truncated"); break; }
                    try { Op(op.N, ops, res, depth); } catch (Exception ex) when (ex is InvalidCastException || ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException || ex is NullReferenceException) { Warn($"malformed operand for {op.N} ignored"); }
                    ops.Clear();
                }
                else { ops.Add(t); if (ops.Count > 64) ops.RemoveAt(0); }
            }
        }
        float N(List<object?> o, int i) => o.Count > i && o[i] is double d ? (float)d : 0;
        void Op(string op, List<object?> o, PdfDict res, int depth)
        {
            switch (op)
            {
                // graphics state
                case "q": stack.Add(gs.Clone()); if (stack.Count > 256) stack.RemoveAt(0); break;
                case "Q": if (stack.Count > 0) { gs = stack[^1]; stack.RemoveAt(stack.Count - 1); } break;
                case "cm": gs.Ctm = new Matrix3x2(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5)) * gs.Ctm; break;
                case "w": gs.LineWidth = N(o, 0); break;
                case "J": gs.Cap = (VectorCap)Math.Clamp((int)N(o, 0), 0, 2); break;
                case "j": gs.Join = (VectorJoin)Math.Clamp((int)N(o, 0), 0, 2); break;
                case "M": gs.Miter = N(o, 0); break;
                case "d": { var arr = o.Count > 0 ? o[0] as object?[] : null; var l = new List<float>(); if (arr != null) foreach (var v in arr) if (v is double dv && dv >= 0) l.Add((float)dv); bool any = false; foreach (var v in l) if (v > 0) any = true; gs.Dash = any ? l.ToArray() : null; gs.DashOffset = N(o, 1); break; }
                case "ri": case "i": break;
                case "gs": ExtGState(o.Count > 0 ? o[0] as string : null, res); break;
                // path construction
                case "m": MoveTo(N(o, 0), N(o, 1)); break;
                case "l": LineTo(N(o, 0), N(o, 1)); break;
                case "c": CurveTo(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5)); break;
                case "v": CurveTo(curX, curY, N(o, 0), N(o, 1), N(o, 2), N(o, 3)); break;
                case "y": CurveTo(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 2), N(o, 3)); break;
                case "h": Close(); break;
                case "re": { float x = N(o, 0), y = N(o, 1), w = N(o, 2), h = N(o, 3); MoveTo(x, y); LineTo(x + w, y); LineTo(x + w, y + h); LineTo(x, y + h); Close(); break; }
                // path painting
                case "S": Paint(false, true, false); break;
                case "s": Close(); Paint(false, true, false); break;
                case "f": case "F": Paint(true, false, false); break;
                case "f*": Paint(true, false, true); break;
                case "B": Paint(true, true, false); break;
                case "B*": Paint(true, true, true); break;
                case "b": Close(); Paint(true, true, false); break;
                case "b*": Close(); Paint(true, true, true); break;
                case "n": Paint(false, false, false); break;
                case "W": pendingClip = 1; break;
                case "W*": pendingClip = 2; break;
                // colour
                case "g": gs.FillCs = "DeviceGray"; gs.FillCsObj = null; gs.Fill = Gray(N(o, 0)); break;
                case "G": gs.StrokeCs = "DeviceGray"; gs.StrokeCsObj = null; gs.Stroke = Gray(N(o, 0)); break;
                case "rg": gs.FillCs = "DeviceRGB"; gs.FillCsObj = null; gs.Fill = Rgb(N(o, 0), N(o, 1), N(o, 2)); break;
                case "RG": gs.StrokeCs = "DeviceRGB"; gs.StrokeCsObj = null; gs.Stroke = Rgb(N(o, 0), N(o, 1), N(o, 2)); break;
                case "k": gs.FillCs = "DeviceCMYK"; gs.FillCsObj = null; gs.Fill = Cmyk(N(o, 0), N(o, 1), N(o, 2), N(o, 3)); break;
                case "K": gs.StrokeCs = "DeviceCMYK"; gs.StrokeCsObj = null; gs.Stroke = Cmyk(N(o, 0), N(o, 1), N(o, 2), N(o, 3)); break;
                case "cs": SetCs(o.Count > 0 ? o[0] as string : null, res, true); break;
                case "CS": SetCs(o.Count > 0 ? o[0] as string : null, res, false); break;
                case "sc": case "scn": SetColor(o, res, true); break;
                case "SC": case "SCN": SetColor(o, res, false); break;
                // XObjects, shading, images
                case "Do": DoXObject(o.Count > 0 ? o[0] as string : null, res, depth); break;
                case "sh": Shade(o.Count > 0 ? o[0] as string : null, res); break;
                case "BI": case "ID": case "EI": break;
                // text (Vector.PdfText.cs)
                case "BT": case "ET": case "Tf": case "Td": case "TD": case "Tm": case "T*": case "TL": case "Tc": case "Tw": case "Tz": case "Ts": case "Tr":
                case "Tj": case "TJ": case "'": case "\"": TextOp(op, o, res, depth); break;
                case "d0": case "d1": break;
                case "BDC": MarkedBegin(o, res); break;
                case "BMC": mc.Add((null, img.Shapes.Count)); break;
                case "EMC": MarkedEnd(); break;
                case "MP": case "DP": case "BX": case "EX": break;
                default: break;
            }
        }
        static VectorColor Gray(float g) => new VectorColor(unchecked((int)0xFF000000) | B(g) << 16 | B(g) << 8 | B(g));
        static VectorColor Rgb(float r, float g, float b) => new VectorColor(unchecked((int)0xFF000000) | B(r) << 16 | B(g) << 8 | B(b));
        static VectorColor Cmyk(float c, float m, float y, float k) => Rgb((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
        static int B(float v) => Math.Clamp((int)MathF.Round(v * 255), 0, 255);

        void Paint(bool fill, bool stroke, bool evenOdd)
        {
            if (!path.IsEmpty)
            {
                if (fill && gs.Fill != null)
                {
                    var p = path.Clone(); p.Close();
                    var f = gs.Fill; if (gs.Alpha < 1) f = WithAlpha(f, gs.Alpha);
                    img.Shapes.Add(new VectorShape { Path = p, Fill = f, EvenOdd = evenOdd, Clip = gs.Clip });
                }
                if (stroke && gs.Stroke != null)
                {
                    var s = new VectorShape { Fill = null, Cap = gs.Cap, Join = gs.Join, MiterLimit = gs.Miter, Clip = gs.Clip };
                    s.Stroke = gs.StrokeAlpha < 1 ? WithAlpha(gs.Stroke, gs.StrokeAlpha) : gs.Stroke;
                    var m = gs.Ctm; float scale = VectorRender.MeanScale(m);
                    float a = m.M11 * m.M11 + m.M12 * m.M12, b = m.M21 * m.M21 + m.M22 * m.M22, dd = m.M11 * m.M21 + m.M12 * m.M22;
                    bool uniform = MathF.Abs(a - b) <= 1e-3f * MathF.Max(a, b) && MathF.Abs(dd) <= 1e-3f * MathF.Max(a, b);
                    if (uniform || !Matrix3x2.Invert(m, out var inv))
                    {
                        s.Path = path.Clone(); s.StrokeWidth = gs.LineWidth * scale;
                        if (gs.Dash != null) { s.Dash = new float[gs.Dash.Length]; for (int i = 0; i < gs.Dash.Length; i++) s.Dash[i] = gs.Dash[i] * scale; s.DashOffset = gs.DashOffset * scale; }
                    }
                    else { s.Path = path.Transformed(inv); s.Transform = m; s.StrokeWidth = gs.LineWidth; s.Dash = gs.Dash == null ? null : (float[])gs.Dash.Clone(); s.DashOffset = gs.DashOffset; s.Clip = gs.Clip?.Transformed(inv); }
                    img.Shapes.Add(s);
                }
            }
            if (pendingClip != 0 && !path.IsEmpty)
            {
                var p = path.Clone(); p.Close();
                gs.Clip = gs.Clip == null ? new VectorClip(p, pendingClip == 2) : gs.Clip.Intersect(p, pendingClip == 2);
            }
            pendingClip = 0; path = new VectorPath(); hasCur = false;
        }
        static VectorPaint WithAlpha(VectorPaint p, float a)
        {
            if (p is VectorColor c) { int al = (int)MathF.Round(((c.Argb >> 24) & 255) * a); return new VectorColor((c.Argb & 0xFFFFFF) | (al << 24)); }
            if (!(p is VectorGradient)) return p;   // image paints: opacity is carried by the shape
            var g = (VectorGradient)p.Clone(); for (int i = 0; i < g.Stops.Count; i++) { int al = (int)MathF.Round(((g.Stops[i].Argb >> 24) & 255) * a); g.Stops[i] = new VectorStop(g.Stops[i].Offset, (g.Stops[i].Argb & 0xFFFFFF) | (al << 24)); }
            return g;
        }

        // ---- ExtGState
        void ExtGState(string? name, PdfDict res)
        {
            if (name == null) return;
            var eg = doc.Resolve(res.Get("ExtGState")) as PdfDict; var g = eg != null ? doc.Resolve(eg.Get(name)) as PdfDict : null; if (g == null) return;
            if (doc.Resolve(g.Get("LW")) is double lw) gs.LineWidth = (float)lw;
            if (doc.Resolve(g.Get("LC")) is double lc) gs.Cap = (VectorCap)Math.Clamp((int)lc, 0, 2);
            if (doc.Resolve(g.Get("LJ")) is double lj) gs.Join = (VectorJoin)Math.Clamp((int)lj, 0, 2);
            if (doc.Resolve(g.Get("ML")) is double ml) gs.Miter = (float)ml;
            if (doc.Resolve(g.Get("CA")) is double ca) gs.StrokeAlpha = Math.Clamp((float)ca, 0, 1);
            if (doc.Resolve(g.Get("ca")) is double fa) gs.Alpha = Math.Clamp((float)fa, 0, 1);
            if (doc.Resolve(g.Get("D")) is object?[] da && da.Length >= 1 && doc.Resolve(da[0]) is object?[] arr) { var l = new List<float>(); foreach (var v in arr) if (doc.Resolve(v) is double dv) l.Add((float)dv); bool any = false; foreach (var v in l) if (v > 0) any = true; gs.Dash = any ? l.ToArray() : null; gs.DashOffset = da.Length > 1 && doc.Resolve(da[1]) is double dof ? (float)dof : 0; }
            var sm = doc.Resolve(g.Get("SMask")); if (sm is PdfDict) { if (!gs.SoftMask) Warn("soft masks (SMask) are ignored"); gs.SoftMask = true; } else if (sm is string) gs.SoftMask = false;
            var bm = doc.Resolve(g.Get("BM")); string? bmn = bm as string ?? (bm is object?[] ba && ba.Length > 0 ? ba[0] as string : null);
            if (bmn != null && bmn != "Normal" && bmn != "Compatible") Warn($"blend mode {bmn} rendered as Normal");
        }

        // ---- colour spaces
        object? CsObj(string? name, PdfDict res)
        {
            if (name == null) return null;
            switch (name) { case "DeviceGray": case "DeviceRGB": case "DeviceCMYK": case "Pattern": case "G": case "RGB": case "CMYK": return name; }
            var csd = doc.Resolve(res.Get("ColorSpace")) as PdfDict;
            var o = csd != null ? doc.Resolve(csd.Get(name)) : null;
            return o ?? name;
        }
        (string family, int ncomp, object? obj) CsInfo(object? cs, int depth = 0)
        {
            cs = doc.Resolve(cs);
            if (cs is string s)
            {
                switch (s) { case "DeviceGray": case "G": case "CalGray": return ("DeviceGray", 1, cs); case "DeviceRGB": case "RGB": case "CalRGB": case "Lab": return (s == "Lab" ? "Lab" : "DeviceRGB", 3, cs); case "DeviceCMYK": case "CMYK": return ("DeviceCMYK", 4, cs); case "Pattern": return ("Pattern", 1, cs); case "Indexed": case "I": return ("Indexed", 1, cs); }
                return ("DeviceGray", 1, cs);
            }
            if (cs is object?[] a && a.Length > 0 && doc.Resolve(a[0]) is string fam)
            {
                switch (fam)
                {
                    case "ICCBased": { var st = a.Length > 1 ? doc.Resolve(a[1]) as PdfStream : null; int n = (int)(st != null ? doc.Resolve(st.Dict.Get("N")) as double? ?? 3 : 3); return (n == 4 ? "DeviceCMYK" : n == 1 ? "DeviceGray" : "DeviceRGB", n, cs); }
                    case "CalRGB": return ("DeviceRGB", 3, cs); case "CalGray": return ("DeviceGray", 1, cs); case "CalCMYK": return ("DeviceCMYK", 4, cs);
                    case "Lab": return ("Lab", 3, cs);
                    case "Indexed": case "I": return ("Indexed", 1, cs);
                    case "Separation": return ("Separation", 1, cs);
                    case "DeviceN": { int n = a.Length > 1 && doc.Resolve(a[1]) is object?[] names ? names.Length : 1; return ("DeviceN", n, cs); }
                    case "Pattern": return ("Pattern", 1, cs);
                    case "DeviceGray": case "DeviceRGB": case "DeviceCMYK": return CsInfo(fam, depth + 1);
                }
            }
            return ("DeviceGray", 1, cs);
        }
        void SetCs(string? name, PdfDict res, bool fill)
        {
            var obj = CsObj(name, res); var (fam, n, o) = CsInfo(obj);
            VectorPaint? init = fam == "DeviceCMYK" ? Rgb(0, 0, 0) : fam == "Pattern" ? null : Gray(0);
            if (fam == "Separation" || fam == "DeviceN") init = Convert(fam, o, Enumerable1(n));
            if (fill) { gs.FillCs = fam; gs.FillCsObj = o; gs.Fill = init; } else { gs.StrokeCs = fam; gs.StrokeCsObj = o; gs.Stroke = init; }
        }
        static float[] Enumerable1(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = 1; return a; }
        void SetColor(List<object?> o, PdfDict res, bool fill)
        {
            string fam = fill ? gs.FillCs : gs.StrokeCs; object? cso = fill ? gs.FillCsObj : gs.StrokeCsObj;
            var nums = new List<float>(); string? pname = null;
            foreach (var v in o) { if (v is double d) nums.Add((float)d); else if (v is string s) pname = s; }
            VectorPaint? p;
            if (fam == "Pattern")
            {
                p = Pattern(pname, res, nums, cso);
            }
            else p = Convert(fam, cso, nums.ToArray());
            if (p == null) return;
            if (fill) gs.Fill = p; else gs.Stroke = p;
        }
        VectorPaint? Convert(string fam, object? cso, float[] c)
        {
            switch (fam)
            {
                case "DeviceGray": return c.Length >= 1 ? Gray(c[0]) : Gray(0);
                case "DeviceRGB": return c.Length >= 3 ? Rgb(c[0], c[1], c[2]) : c.Length == 1 ? Gray(c[0]) : Rgb(0, 0, 0);
                case "DeviceCMYK": return c.Length >= 4 ? Cmyk(c[0], c[1], c[2], c[3]) : Rgb(0, 0, 0);
                case "Lab": { float l = c.Length >= 1 ? Math.Clamp(c[0] / 100, 0, 1) : 0; return LabToRgb(c.Length >= 3 ? c[0] : l * 100, c.Length >= 3 ? c[1] : 0, c.Length >= 3 ? c[2] : 0); }
                case "Indexed":
                    {
                        if (!(doc.Resolve(cso) is object?[] a) || a.Length < 4) return Gray(0);
                        var (bfam, bn, bo) = CsInfo(a[1]); int hival = (int)(doc.Resolve(a[2]) as double? ?? 0);
                        var lookup = doc.Resolve(a[3]); byte[]? tbl = lookup as byte[] ?? (lookup is PdfStream ls ? doc.Decode(ls) : null);
                        int idx = c.Length >= 1 ? Math.Clamp((int)MathF.Round(c[0]), 0, Math.Max(0, hival)) : 0;
                        if (tbl == null || (idx + 1) * bn > tbl.Length) return Gray(0);
                        var comp = new float[bn]; for (int i = 0; i < bn; i++) comp[i] = tbl[idx * bn + i] / 255f;
                        if (bfam == "Lab") { comp[0] *= 100; comp[1] = comp[1] * 255 - 128; comp[2] = comp[2] * 255 - 128; }
                        return Convert(bfam, bo, comp);
                    }
                case "Separation": case "DeviceN":
                    {
                        if (!(doc.Resolve(cso) is object?[] a) || a.Length < 4) { float g = c.Length >= 1 ? 1 - c[0] : 0; return Gray(g); }
                        if (fam == "Separation" && doc.Resolve(a[1]) is string sn && sn == "None") return null;
                        var (afam, an, ao) = CsInfo(a[2]);
                        var fn = Function(doc.Resolve(a[3]));
                        if (fn == null) { float g = c.Length >= 1 ? 1 - c[0] : 0; return Gray(g); }
                        var outp = fn(c.Length >= 1 ? c : new float[] { 1 });
                        return Convert(afam, ao, outp);
                    }
                case "Pattern": return null;
                default: return c.Length >= 3 ? Rgb(c[0], c[1], c[2]) : c.Length >= 1 ? Gray(c[0]) : Gray(0);
            }
        }
        static VectorColor LabToRgb(float L, float a, float bb)
        {
            float fy = (L + 16) / 116, fx = fy + a / 500, fz = fy - bb / 200;
            float G(float t) => t > 6f / 29 ? t * t * t : 3f * (6f / 29) * (6f / 29) * (t - 4f / 29);
            float X = 0.9505f * G(fx), Y = 1.0f * G(fy), Z = 1.089f * G(fz);
            float r = 3.2406f * X - 1.5372f * Y - 0.4986f * Z, g = -0.9689f * X + 1.8758f * Y + 0.0415f * Z, b = 0.0557f * X - 0.2040f * Y + 1.0570f * Z;
            float Gam(float v) { v = Math.Clamp(v, 0, 1); return v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f; }
            return Rgb(Gam(r), Gam(g), Gam(b));
        }
        VectorPaint? Pattern(string? name, PdfDict res, List<float> under, object? cso)
        {
            if (name == null) return null;
            var pd = doc.Resolve(res.Get("Pattern")) as PdfDict; var pobj = pd != null ? doc.Resolve(pd.Get(name)) : null;
            PdfDict? p = pobj as PdfDict ?? (pobj as PdfStream)?.Dict; if (p == null) return null;
            int ptype = (int)(doc.Resolve(p.Get("PatternType")) as double? ?? 1);
            var pm = doc.Resolve(p.Get("Matrix")) is object?[] ma && ma.Length >= 6 ? new Matrix3x2(F(ma[0]), F(ma[1]), F(ma[2]), F(ma[3]), F(ma[4]), F(ma[5])) : Matrix3x2.Identity;
            // pattern space = default space of the page (or of the form the pattern is a resource of): the base CTM at the time the content began
            var patCtm = pm * baseForPatterns;
            if (ptype == 2)
            {
                var sh = doc.Resolve(p.Get("Shading")); var shd = sh as PdfDict ?? (sh as PdfStream)?.Dict; if (shd == null) return null;
                var g = ShadingGradient(shd); if (g == null) return Gray(0.5f);
                g.Matrix = patCtm; return g;
            }
            // tiling pattern: average colour of its content (approximation)
            Warn("tiling patterns are approximated by a flat colour");
            if (ptype == 1 && (int)(doc.Resolve(p.Get("PaintType")) as double? ?? 1) == 2 && under.Count > 0)
            {   // uncoloured pattern: the colour comes from the operands in the underlying space
                var (ufam, un, uo) = doc.Resolve(cso) is object?[] ua && ua.Length > 1 ? CsInfo(ua[1]) : ("DeviceGray", 1, null);
                return Convert(ufam, uo, under.ToArray()) ?? Gray(0.5f);
            }
            return new VectorColor(unchecked((int)0xFF9A9A9A));
            float F(object? v) => (float)(doc.Resolve(v) as double? ?? 0);
        }
        Matrix3x2 baseForPatterns;

        // ---- shading
        VectorGradient? ShadingGradient(PdfDict sh)
        {
            object? Get(string k)
            {
                var v = doc.Resolve(sh.Get(k));
                if (v is object?[] a) { var r = new object?[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = doc.Resolve(a[i]); return r; }
                return v;
            }
            var csv = doc.Resolve(sh.Get("ColorSpace")); var (fam, n, cso) = CsInfo(csv);
            var g = PdfShading.FromDict(k => k == "ColorSpace" ? "__custom" : Get(k), Warn, fo =>
            {
                var fn = FunctionMulti(fo); if (fn == null) return null;
                return t => { var outp = fn(new[] { (float)t }); var col = Convert(fam, cso, outp) as VectorColor; int argb = col?.Argb ?? unchecked((int)0xFF808080); return new[] { ((argb >> 16) & 255) / 255.0, ((argb >> 8) & 255) / 255.0, (argb & 255) / 255.0 }; };
            });
            if (g == null && (int)(doc.Resolve(sh.Get("ShadingType")) as double? ?? 0) >= 4)
            {   // mesh: average colour from Background or a grey
                return null;
            }
            return g;
        }
        void Shade(string? name, PdfDict res)
        {
            if (name == null) return;
            var shd = doc.Resolve(res.Get("Shading")) as PdfDict; var sho = shd != null ? doc.Resolve(shd.Get(name)) : null;
            var sh = sho as PdfDict ?? (sho as PdfStream)?.Dict; if (sh == null) return;
            var g = ShadingGradient(sh);
            var p = new VectorPath();
            if (gs.Clip != null && gs.Clip.Paths.Count > 0) p = gs.Clip.Paths[^1].Path.Clone(); else p.Rect(0, 0, img.Width, img.Height);
            if (g == null)
            {   // mesh / unsupported: fill the clip with the background or nothing
                var bg = doc.Resolve(sh.Get("Background")) as object?[]; if (bg == null) return;
                var comps = new List<float>(); foreach (var v in bg) if (doc.Resolve(v) is double d) comps.Add((float)d);
                var (fam, n, cso) = CsInfo(doc.Resolve(sh.Get("ColorSpace"))); var col = Convert(fam, cso, comps.ToArray()); if (col == null) return;
                img.Shapes.Add(new VectorShape { Path = p, Fill = gs.Alpha < 1 ? WithAlpha(col, gs.Alpha) : col, Clip = gs.Clip }); return;
            }
            g.Matrix = gs.Ctm;
            VectorPaint paint = gs.Alpha < 1 ? WithAlpha(g, gs.Alpha) : g;
            img.Shapes.Add(new VectorShape { Path = p, Fill = paint, Clip = gs.Clip });
        }

        // ---- functions
        Func<float[], float[]>? Function(object? fo)
        {
            fo = doc.Resolve(fo);
            if (fo is object?[] arr)
            {   // array of 1-out functions -> n-out
                var fns = new List<Func<float[], float[]>>(); foreach (var e in arr) { var f = Function(e); if (f != null) fns.Add(f); }
                if (fns.Count == 0) return null;
                return x => { var o = new float[fns.Count]; for (int i = 0; i < fns.Count; i++) { var r = fns[i](x); o[i] = r.Length > 0 ? r[0] : 0; } return o; };
            }
            var dict = fo as PdfDict ?? (fo as PdfStream)?.Dict; if (dict == null) return null;
            if (fnCache.TryGetValue(dict, out var cached)) return cached == null ? null : x => ToF(cached(x[0]));
            var built = Build(fo, dict);
            fnCache[dict] = built == null ? null : t => ToD(built(new[] { (float)t }));
            return built;
            static double[] ToD(float[] f) { var d = new double[f.Length]; for (int i = 0; i < f.Length; i++) d[i] = f[i]; return d; }
            static float[] ToF(double[] d) { var f = new float[d.Length]; for (int i = 0; i < d.Length; i++) f[i] = (float)d[i]; return f; }
        }
        Func<float[], float[]>? FunctionMulti(object? fo) => Function(fo);
        Func<float[], float[]>? Build(object? fo, PdfDict d)
        {
            int type = (int)(doc.Resolve(d.Get("FunctionType")) as double? ?? -1);
            float[] dom = Arr(d.Get("Domain")) ?? new float[] { 0, 1 }; float[]? range = Arr(d.Get("Range"));
            static float Cl(float v, float a, float b) => a <= b ? Math.Clamp(v, a, b) : Math.Clamp(v, b, a);   // damaged files: inverted Domain / Range must not throw
            float[] Clamp(float[] x) { var o = new float[x.Length]; for (int i = 0; i < x.Length; i++) o[i] = dom.Length > 2 * i + 1 ? Cl(x[i], dom[2 * i], dom[2 * i + 1]) : x[i]; return o; }
            float[] ClampR(float[] y) { if (range == null) return y; for (int i = 0; i < y.Length && 2 * i + 1 < range.Length; i++) y[i] = Cl(y[i], range[2 * i], range[2 * i + 1]); return y; }
            switch (type)
            {
                case 2:
                    {
                        float[] c0 = Arr(d.Get("C0")) ?? new float[] { 0 }, c1 = Arr(d.Get("C1")) ?? new float[] { 1 }; float nexp = (float)(doc.Resolve(d.Get("N")) as double? ?? 1);
                        return x => { float t = Clamp(x)[0]; float tn = nexp == 1 ? t : MathF.Pow(Math.Max(0, t), nexp); var o = new float[Math.Max(c0.Length, c1.Length)]; for (int i = 0; i < o.Length; i++) o[i] = (i < c0.Length ? c0[i] : 0) + tn * ((i < c1.Length ? c1[i] : 0) - (i < c0.Length ? c0[i] : 0)); return ClampR(o); };
                    }
                case 3:
                    {
                        var funcs = new List<Func<float[], float[]>?>(); if (doc.Resolve(d.Get("Functions")) is object?[] fa) foreach (var f in fa) funcs.Add(Function(f));
                        float[] bounds = Arr(d.Get("Bounds")) ?? Array.Empty<float>(), enc = Arr(d.Get("Encode")) ?? Array.Empty<float>();
                        if (funcs.Count == 0) return null;
                        return x =>
                        {
                            float t = Clamp(x)[0]; int k = 0; while (k < bounds.Length && t >= bounds[k]) k++; k = Math.Min(k, funcs.Count - 1);
                            float lo = k == 0 ? dom[0] : bounds[k - 1], hi = k == bounds.Length ? dom[1] : bounds[k];
                            float e0 = enc.Length > 2 * k + 1 ? enc[2 * k] : 0, e1 = enc.Length > 2 * k + 1 ? enc[2 * k + 1] : 1;
                            float tt = hi > lo ? e0 + (t - lo) / (hi - lo) * (e1 - e0) : e0;
                            var f = funcs[k]; return f == null ? new float[] { 0 } : ClampR(f(new[] { tt }));
                        };
                    }
                case 0:
                    {
                        var st = fo as PdfStream; if (st == null) return null; var data = doc.Decode(st);
                        float[] size = Arr(d.Get("Size")) ?? new float[] { 2 }; int bps = (int)(doc.Resolve(d.Get("BitsPerSample")) as double? ?? 8);
                        int nOut = range != null ? range.Length / 2 : 1, m = size.Length;
                        if (m == 0 || nOut <= 0 || !(bps is 1 or 2 or 4 or 8 or 12 or 16 or 24 or 32)) return null;
                        for (int i = 0; i < m; i++) if (!(size[i] >= 1) || size[i] > 1 << 24) return null;          // damaged Size: no samples to look up
                        float[] enc = Arr(d.Get("Encode")) ?? Enc(size), dec = Arr(d.Get("Decode")) ?? range ?? new float[] { 0, 1 };
                        if (enc.Length < 2 * m) enc = Enc(size);
                        float max = (float)(Math.Pow(2, bps) - 1);
                        float Sample(long idx, int j) { long bit = (idx * nOut + j) * bps; long byteI = bit >> 3; if (byteI >= data.Length) return 0; long v = 0; int need = bps, off = (int)(bit & 7); while (need > 0 && byteI < data.Length) { int avail = 8 - off, take = Math.Min(avail, need); int b = (data[byteI] >> (avail - take)) & ((1 << take) - 1); v = (v << take) | (uint)b; need -= take; off += take; if (off == 8) { off = 0; byteI++; } } return v / max; }
                        return x =>
                        {
                            var xi = Clamp(x); var o = new float[nOut];
                            // multilinear on the first input only (1-D is what shadings use); other inputs nearest
                            long idx = 0, stride = 1; float frac = 0; long i0 = 0, i1 = 0; long stride0 = 1;
                            for (int i = 0; i < m; i++)
                            {
                                float e = dom.Length > 2 * i + 1 && dom[2 * i + 1] > dom[2 * i] ? enc[2 * i] + (xi[Math.Min(i, xi.Length - 1)] - dom[2 * i]) / (dom[2 * i + 1] - dom[2 * i]) * (enc[2 * i + 1] - enc[2 * i]) : enc[2 * i];
                                e = Math.Clamp(e, 0, size[i] - 1);
                                if (i == 0) { i0 = (long)MathF.Floor(e); i1 = Math.Min(i0 + 1, (long)size[i] - 1); frac = e - i0; stride0 = stride; }
                                else idx += (long)MathF.Round(e) * stride;
                                stride *= (long)size[i];
                            }
                            for (int j = 0; j < nOut; j++)
                            {
                                float s0 = Sample(idx + i0 * stride0, j), s1 = Sample(idx + i1 * stride0, j), s = s0 + (s1 - s0) * frac;
                                float d0 = dec.Length > 2 * j + 1 ? dec[2 * j] : 0, d1 = dec.Length > 2 * j + 1 ? dec[2 * j + 1] : 1;
                                o[j] = d0 + s * (d1 - d0);
                            }
                            return ClampR(o);
                        };
                    }
                case 4:
                    {
                        var st = fo as PdfStream; if (st == null) return null; var src = doc.Decode(st);
                        var lx = new PdfLexer(src); var body = lx.Object(null) as PsArray; if (body == null) return null;
                        int nOut = range != null ? range.Length / 2 : 1;
                        return x =>
                        {
                            var stck = new List<double>(); foreach (var v in Clamp(x)) stck.Add(v);
                            try { PsCalc.Exec(body, stck, 0); } catch (Exception e) { ImportLog.Swallowed(e, "PDF type 4 function"); }
                            var o = new float[nOut]; for (int i = 0; i < nOut; i++) { int si = stck.Count - nOut + i; o[i] = si >= 0 && si < stck.Count ? (float)stck[si] : 0; }
                            return ClampR(o);
                        };
                    }
            }
            return null;
            static float[] Enc(float[] size) { var e = new float[size.Length * 2]; for (int i = 0; i < size.Length; i++) { e[2 * i] = 0; e[2 * i + 1] = size[i] - 1; } return e; }
        }
        float[]? Arr(object? o) { if (!(doc.Resolve(o) is object?[] a)) return null; var r = new float[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = (float)(doc.Resolve(a[i]) as double? ?? 0); return r; }

        // ---- XObjects
        void DoXObject(string? name, PdfDict res, int depth)
        {
            if (name == null) return;
            var xd = doc.Resolve(res.Get("XObject")) as PdfDict; var xo = xd != null ? doc.Resolve(xd.Get(name)) as PdfStream : null; if (xo == null) return;
            var st = doc.Resolve(xo.Dict.Get("Subtype")) as string;
            if (st == "Image") { DrawImage(xo, res); return; }
            if (st != "Form" && !xo.Dict.Has("BBox")) return;
            if (depth > 12) { Warn("form XObjects nested too deeply"); return; }
            var saved = gs.Clone(); var savedStack = new List<GS>(stack); var savedPath = path; bool savedHas = hasCur; var savedBase = baseForPatterns;
            var m = doc.Resolve(xo.Dict.Get("Matrix")) is object?[] ma && ma.Length >= 6 ? new Matrix3x2(F(ma[0]), F(ma[1]), F(ma[2]), F(ma[3]), F(ma[4]), F(ma[5])) : Matrix3x2.Identity;
            gs.Ctm = m * gs.Ctm;
            if (doc.Resolve(xo.Dict.Get("BBox")) is object?[] bb && bb.Length >= 4)
            {
                float x0 = F(bb[0]), y0 = F(bb[1]), x1 = F(bb[2]), y1 = F(bb[3]);
                var bp = new VectorPath(); var a = Dev(x0, y0); var b = Dev(x1, y0); var c = Dev(x1, y1); var dd = Dev(x0, y1);
                bp.MoveTo(a.X, a.Y).LineTo(b.X, b.Y).LineTo(c.X, c.Y).LineTo(dd.X, dd.Y).Close();
                gs.Clip = gs.Clip == null ? new VectorClip(bp) : gs.Clip.Intersect(bp);
            }
            if (doc.Resolve(xo.Dict.Get("Group")) is PdfDict grp && (gs.Alpha < 1 || gs.StrokeAlpha < 1)) Warn("transparency group alpha applied per shape");
            var fres = doc.Resolve(xo.Dict.Get("Resources")) as PdfDict ?? res;
            path = new VectorPath(); hasCur = false; baseForPatterns = gs.Ctm;
            var inner = new PdfContent(doc, img) { gs = gs, baseForPatterns = gs.Ctm };
            foreach (var w in warned) inner.warned.Add(w);
            inner.Run(doc.Decode(xo), fres, gs.Ctm, depth + 1);
            foreach (var w in inner.warned) warned.Add(w);
            gs = saved; stack.Clear(); stack.AddRange(savedStack); path = savedPath; hasCur = savedHas; baseForPatterns = savedBase;
            float F(object? v) => (float)(doc.Resolve(v) as double? ?? 0);
        }
        internal static void SkipInlineImage(PdfLexer lx)
        {   // BI ... ID <binary> EI : find "EI" delimited by whitespace
            var d = lx.D; int p = lx.Pos, end = lx.End;
            // skip to ID
            while (p + 1 < end && !(d[p] == 'I' && d[p + 1] == 'D' && (p + 2 >= end || PdfLexer.Ws(d[p + 2])) && (p == 0 || PdfLexer.Ws(d[p - 1])))) p++;
            p += 3;
            while (p + 1 < end)
            {
                if (d[p] == 'E' && d[p + 1] == 'I' && (p + 2 >= end || PdfLexer.Ws(d[p + 2]) || PdfLexer.Delim(d[p + 2])) && PdfLexer.Ws(d[p - 1])) { p += 2; break; }
                p++;
            }
            lx.Pos = Math.Min(p, end);
        }
        public void RunWithBase(byte[] content, PdfDict res, Matrix3x2 ctm) { baseForPatterns = ctm; Run(content, res, ctm, 0); }
    }

    /// <summary>PostScript calculator (PDF function type 4) evaluator on a double stack.</summary>
    internal static class PsCalc
    {
        public static void Exec(PsArray body, List<double> st, int depth)
        {
            if (depth > 64) return;
            var items = body.A;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it is double d) { st.Add(d); continue; }
                if (it is bool b) { st.Add(b ? 1 : 0); continue; }
                if (it is PsArray) { continue; }   // procedures are consumed by if / ifelse below
                if (!(it is PdfOp op)) continue;
                double Pop() { if (st.Count == 0) return 0; var v = st[^1]; st.RemoveAt(st.Count - 1); return v; }
                switch (op.N)
                {
                    case "add": { double y = Pop(), x = Pop(); st.Add(x + y); break; }
                    case "sub": { double y = Pop(), x = Pop(); st.Add(x - y); break; }
                    case "mul": { double y = Pop(), x = Pop(); st.Add(x * y); break; }
                    case "div": { double y = Pop(), x = Pop(); st.Add(y == 0 ? 0 : x / y); break; }
                    case "idiv": { double y = Pop(), x = Pop(); st.Add(y == 0 ? 0 : Math.Truncate(x / y)); break; }
                    case "mod": { double y = Pop(), x = Pop(); st.Add(y == 0 ? 0 : (int)x % (int)y); break; }
                    case "neg": st.Add(-Pop()); break; case "abs": st.Add(Math.Abs(Pop())); break;
                    case "sqrt": st.Add(Math.Sqrt(Math.Max(0, Pop()))); break;
                    case "sin": st.Add(Math.Sin(Pop() * Math.PI / 180)); break; case "cos": st.Add(Math.Cos(Pop() * Math.PI / 180)); break;
                    case "atan": { double den = Pop(), num = Pop(); double a = Math.Atan2(num, den) * 180 / Math.PI; if (a < 0) a += 360; st.Add(a); break; }
                    case "exp": { double y = Pop(), x = Pop(); st.Add(Math.Pow(x, y)); break; }
                    case "ln": st.Add(Math.Log(Math.Max(1e-300, Pop()))); break; case "log": st.Add(Math.Log10(Math.Max(1e-300, Pop()))); break;
                    case "cvi": st.Add(Math.Truncate(Pop())); break; case "cvr": break;
                    case "floor": st.Add(Math.Floor(Pop())); break; case "ceiling": st.Add(Math.Ceiling(Pop())); break; case "round": st.Add(Math.Round(Pop())); break; case "truncate": st.Add(Math.Truncate(Pop())); break;
                    case "dup": { double x = Pop(); st.Add(x); st.Add(x); break; }
                    case "pop": Pop(); break;
                    case "exch": { double y = Pop(), x = Pop(); st.Add(y); st.Add(x); break; }
                    case "copy": { int n = (int)Pop(); int s = st.Count - n; if (n > 0 && s >= 0) for (int k = 0; k < n; k++) st.Add(st[s + k]); break; }
                    case "index": { int n = (int)Pop(); st.Add(n >= 0 && n < st.Count ? st[st.Count - 1 - n] : 0); break; }
                    case "roll": { int j = (int)Pop(), n = (int)Pop(); if (n > 0 && n <= st.Count) { j %= n; if (j < 0) j += n; int s = st.Count - n; var tmp = st.GetRange(s, n); for (int k = 0; k < n; k++) st[s + (k + j) % n] = tmp[k]; } break; }
                    case "eq": { double y = Pop(), x = Pop(); st.Add(x == y ? 1 : 0); break; } case "ne": { double y = Pop(), x = Pop(); st.Add(x != y ? 1 : 0); break; }
                    case "gt": { double y = Pop(), x = Pop(); st.Add(x > y ? 1 : 0); break; } case "ge": { double y = Pop(), x = Pop(); st.Add(x >= y ? 1 : 0); break; }
                    case "lt": { double y = Pop(), x = Pop(); st.Add(x < y ? 1 : 0); break; } case "le": { double y = Pop(), x = Pop(); st.Add(x <= y ? 1 : 0); break; }
                    case "and": { double y = Pop(), x = Pop(); st.Add((int)x & (int)y); break; } case "or": { double y = Pop(), x = Pop(); st.Add((int)x | (int)y); break; }
                    case "xor": { double y = Pop(), x = Pop(); st.Add((int)x ^ (int)y); break; } case "not": { double x = Pop(); st.Add(x == 0 ? 1 : x == 1 ? 0 : ~(int)x); break; }
                    case "bitshift": { int s = (int)Pop(); int x = (int)Pop(); st.Add(s >= 0 ? x << s : x >> -s); break; }
                    case "true": st.Add(1); break; case "false": st.Add(0); break;
                    case "if": { bool c = Pop() != 0; if (c && i >= 1 && items[i - 1] is PsArray p1) Exec(p1, st, depth + 1); break; }
                    case "ifelse": { bool c = Pop() != 0; if (i >= 2 && items[i - 2] is PsArray p1 && items[i - 1] is PsArray p2) Exec(c ? p1 : p2, st, depth + 1); break; }
                }
            }
        }
    }
}
