using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Sr2d64CSport
{
    /// <summary>
    /// EPS / PostScript importer: a small PostScript interpreter (stacks, dictionaries, procedures, control flow,
    /// arithmetic, arrays / strings, graphics state, CTM, paths, fill / stroke / clip, colour spaces incl. CMYK / HSB /
    /// separation tint approximations, save / restore, image / imagemask / colorimage rasters as picture shapes - see
    /// Vector.PsImage.cs) that records every fill and stroke as a <see cref="VectorShape"/>. Illustrator / CorelDraw /
    /// Inkscape / Ghostscript EPS files define their own operator shorthands in a prolog - since the prolog is executed,
    /// they work. Text (show ...) and shading dictionaries are reported in <see cref="VectorImage.Warnings"/>. Coordinates: the %%BoundingBox (or the page) becomes
    /// the view box; y is flipped so the result is y-down like everything else in SR2D.
    /// </summary>
    internal static class PsReader
    {
        public static VectorImage Read(byte[] data) => Read(data, false);
        /// <summary>Reads PostScript; <paramref name="aiFile"/> marks Illustrator-native content (AIPrivateData / .ai) so the artboard comments decide the page size.</summary>
        public static VectorImage Read(byte[] data, bool aiFile)
        {
            ImportLog.Begin(); List<string>? sink = null;
            try { var img = ReadCore(data, aiFile); sink = img.Warnings; return img; }
            finally { ImportLog.End(sink); }
        }
        static VectorImage ReadCore(byte[] data, bool aiFile)
        {
            int off = 0, len = data.Length;
            if (len >= 30 && data[0] == 0xC5 && data[1] == 0xD0 && data[2] == 0xD3 && data[3] == 0xC6)
            {   // DOS EPS binary: PostScript section offset / length at 4 / 8
                off = BitConverter.ToInt32(data, 4); len = BitConverter.ToInt32(data, 8);
                if (off < 0 || off + len > data.Length) { off = 0; len = data.Length; }
            }
            var interp = new PsInterp(data, off, off + len, aiFile);
            return interp.Run();
        }
    }

    // ---------------------------------------------------------------------- object model
    internal sealed class PsName { public readonly string N; public readonly bool Exec; public PsName(string n, bool exec) { N = n; Exec = exec; } public override string ToString() => (Exec ? "" : "/") + N; }
    internal sealed class PsArray { public List<object?> A; public bool Exec; public PsArray(List<object?> a, bool exec) { A = a; Exec = exec; } public override string ToString() => (Exec ? "{" : "[") + A.Count + (Exec ? "}" : "]"); }
    internal sealed class PsString { public byte[] B; public bool Exec; public PsString(byte[] b) { B = b; } public PsString(string s) { B = Encoding.Latin1.GetBytes(s); } public string S => Encoding.Latin1.GetString(B); public override string ToString() => "(" + S + ")"; }
    internal sealed class PsDict { public Dictionary<string, object?> D = new Dictionary<string, object?>(); public override string ToString() => "<<" + D.Count + ">>"; }
    internal sealed class PsMark { public static readonly PsMark I = new PsMark(); public override string ToString() => "mark"; }
    internal sealed class PsNull { public static readonly PsNull I = new PsNull(); public override string ToString() => "null"; }
    internal sealed class PsOp { public readonly string N; public readonly Action<PsInterp> F; public PsOp(string n, Action<PsInterp> f) { N = n; F = f; } public override string ToString() => "--" + N + "--"; }
    internal sealed class PsFile { public int Kind; public string? Filter; public byte[]? Data; public bool Exec; public byte[]? Eod; public PsFile? Inner; public PsFile(int kind, string? filter = null, byte[]? data = null) { Kind = kind; Filter = filter; Data = data; } }   // Inner: the file this filter reads from (filter chains)   // 0 = currentfile, 1 = filtered currentfile, 2 = string source (Data = its bytes)
    internal sealed class PsSave { public int GsDepth; }
    // control-flow signals of the interpreter (exit / stop / quit unwind the C# call stack); never thrown at callers
    internal sealed class PsExit : Exception { public PsExit() { } public PsExit(string message) : base(message) { } public PsExit(string message, Exception inner) : base(message, inner) { } }
    internal sealed class PsStop : Exception { public PsStop() { } public PsStop(string message) : base(message) { } public PsStop(string message, Exception inner) : base(message, inner) { } }
    internal sealed class PsQuit : Exception { public PsQuit() { } public PsQuit(string message) : base(message) { } public PsQuit(string message, Exception inner) : base(message, inner) { } }

    // ---------------------------------------------------------------------- scanner
    internal sealed class PsScanner
    {
        readonly byte[] d; public int Pos; readonly int end;
        public PsScanner(byte[] data, int start, int end) { d = data; Pos = start; this.end = end; }
        public bool Eof => Pos >= end;
        public byte[] Data => d; public int End => end;
        static bool Ws(int c) => c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\f' || c == 0;
        static bool Delim(int c) => c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
        public void SkipWs()
        {
            while (Pos < end)
            {
                int c = d[Pos];
                if (Ws(c)) Pos++;
                else if (c == '%')
                {
                    if (Pos + 3 < end && d[Pos + 1] == 'A' && d[Pos + 2] == 'I' && AiComment()) continue;
                    if (Pos + 12 < end && d[Pos + 1] == '_' && d[Pos + 2] == '/' && d[Pos + 3] == 'X' && OnXmlUid != null && StartsWith(Pos, "%_/XMLUID : (")) XmlUid();
                    SkipToLineEnd();
                }
                else break;
            }
        }
        public void SkipToLineEnd() { while (Pos < end && d[Pos] != '\n' && d[Pos] != '\r') Pos++; }
        /// <summary>Called on skipped rasters and other non-renderable Illustrator blocks (set by the interpreter to its warning sink).</summary>
        public Action<string>? OnSkip;
        /// <summary>Called with the id of an Illustrator "%_/XMLUID : (id) ; (AI10_ArtUID) ," art-dictionary comment (the object name Illustrator exports to SVG); it follows the art it belongs to.</summary>
        public Action<string>? OnXmlUid;
        void XmlUid()
        {
            int s = Pos + 13, e = s; var sb = new StringBuilder();
            while (e < end && d[e] != ')' && d[e] != '\n' && d[e] != '\r')
            {
                if (d[e] == '\\' && e + 1 < end) { e++; if (d[e] >= '0' && d[e] <= '7') { int v = 0, k = 0; while (k < 3 && e < end && d[e] >= '0' && d[e] <= '7') { v = v * 8 + (d[e] - '0'); e++; k++; } sb.Append((char)v); continue; } sb.Append((char)d[e]); e++; continue; }
                sb.Append((char)d[e]); e++;
            }
            if (e < end && d[e] == ')' && sb.Length > 0) OnXmlUid?.Invoke(sb.ToString());
        }
        // Illustrator structuring comments that bracket data the interpreter must not see: swatch / brush / symbol / pattern
        // definitions (non-printing), raster image bytes, text document dictionaries, palettes and version-gated content.
        static readonly (string begin, string end, string? warn)[] AiBlocks =
        {
            ("%AI5_Begin_NonPrinting", "%AI5_End_NonPrinting", null),
            ("%AI3_BeginPattern", "%AI3_EndPattern", null),
            ("%AI8_BeginBrushPattern", "%AI8_EndBrushPattern", null),
            ("%AI14_BeginSymbol", "%AI10_EndSymbol", null),
            ("%AI10_BeginSymbol", "%AI10_EndSymbol", null),
            ("%AI8_BeginPluginObject", "%AI8_EndPluginObject", null),
            ("%AI11_BeginTextDocument", "%AI11_EndTextDocument", null),
            ("%AI5_BeginPalette", "%AI5_EndPalette", null),
            ("%AI9_BeginArtStyles", "%AI9_EndArtStyles", null),
            ("%AI17_Begin_Content_if_version_gt", "%AI17_Alternate_Content", null),   // we act as an older reader: run the alternate content
        };
        /// <summary>At a '%AI' comment: skips bracketed blocks (see <see cref="AiBlocks"/>); true when something was skipped.</summary>
        bool AiComment()
        {
            foreach (var (begin, endm, warn) in AiBlocks)
            {
                if (!StartsWith(Pos, begin)) continue;
                if (warn != null) OnSkip?.Invoke(warn);
                SkipToLineEnd();
                int depth = 1;
                while (depth > 0 && Pos < end)
                {
                    int e = IndexOf(endm, Pos), b = begin == "%AI17_Begin_Content_if_version_gt" ? -1 : IndexOf(begin, Pos);
                    if (e < 0) { Pos = end; break; }
                    if (b >= 0 && b < e) { depth++; Pos = b + begin.Length; continue; }
                    depth--; Pos = e + endm.Length;
                }
                SkipToLineEnd();
                return true;
            }
            return false;
        }
        bool StartsWith(int at, string s) { if (at + s.Length > end) return false; for (int i = 0; i < s.Length; i++) if (d[at + i] != s[i]) return false; return true; }
        int IndexOf(string s, int from)
        {
            int n = end - s.Length; byte c0 = (byte)s[0];
            for (int i = Math.Max(0, from); i <= n; i++) { if (d[i] != c0) continue; if (StartsWith(i, s)) return i; }
            return -1;
        }
        /// <summary>Next token: number (double), PsName, PsString, PsArray (procedure), "[" "]" "<<" ">>" as PsName exec, or null at EOF.</summary>
        public object? Next()
        {
            SkipWs(); if (Pos >= end) return null;
            int c = d[Pos];
            switch (c)
            {
                case '(': return ReadString();
                case '<':
                    if (Pos + 1 < end && d[Pos + 1] == '<') { Pos += 2; return new PsName("<<", true); }
                    if (Pos + 1 < end && d[Pos + 1] == '~') return ReadA85();
                    return ReadHex();
                case '>': if (Pos + 1 < end && d[Pos + 1] == '>') { Pos += 2; return new PsName(">>", true); } Pos++; return new PsName(">", true);
                case '[': Pos++; return new PsName("[", true);
                case ']': Pos++; return new PsName("]", true);
                case '{': Pos++; return ReadProc();
                case '}': Pos++; return new PsName("}", true);
                case ')': Pos++; return Next();
                case '/':
                    {
                        Pos++; bool imm = false; if (Pos < end && d[Pos] == '/') { Pos++; imm = true; }
                        int s = Pos; while (Pos < end && !Ws(d[Pos]) && !Delim(d[Pos])) Pos++;
                        var n = Encoding.Latin1.GetString(d, s, Pos - s);
                        return imm ? new PsName("//" + n, false) : new PsName(n, false);
                    }
            }
            int st = Pos; while (Pos < end && !Ws(d[Pos]) && !Delim(d[Pos])) Pos++;
            if (Pos == st) { Pos++; return Next(); }
            string tok = Encoding.Latin1.GetString(d, st, Pos - st);
            if (TryNumber(tok, out double v)) return v;
            return new PsName(tok, true);
        }
        public static bool TryNumber(string t, out double v)
        {
            v = 0; if (t.Length == 0) return false; char c0 = t[0];
            if (!(char.IsDigit(c0) || c0 == '-' || c0 == '+' || c0 == '.')) return false;
            int hash = t.IndexOf('#');
            if (hash > 0 && int.TryParse(t.AsSpan(0, hash), NumberStyles.Integer, CultureInfo.InvariantCulture, out int radix) && radix >= 2 && radix <= 36)
            {
                try { v = Convert.ToInt64(t.Substring(hash + 1), radix); return true; } catch (FormatException) { return false; } catch (OverflowException) { return false; } catch (ArgumentException) { return false; }
            }
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
        PsArray ReadProc()
        {
            var l = new List<object?>();
            while (true)
            {
                var t = Next(); if (t == null) break;
                if (t is PsName n && n.Exec && n.N == "}") break;
                l.Add(t);
            }
            return new PsArray(l, true);
        }
        PsString ReadString()
        {
            Pos++; var sb = new List<byte>(); int depth = 1;
            while (Pos < end)
            {
                int c = d[Pos++];
                if (c == '\\')
                {
                    if (Pos >= end) break; int e = d[Pos++];
                    switch (e)
                    {
                        case 'n': sb.Add((byte)'\n'); break; case 'r': sb.Add((byte)'\r'); break; case 't': sb.Add((byte)'\t'); break; case 'b': sb.Add(8); break; case 'f': sb.Add(12); break;
                        case '\n': break; case '\r': if (Pos < end && d[Pos] == '\n') Pos++; break;
                        default:
                            if (e >= '0' && e <= '7') { int v = e - '0'; for (int k = 0; k < 2 && Pos < end && d[Pos] >= '0' && d[Pos] <= '7'; k++) v = v * 8 + (d[Pos++] - '0'); sb.Add((byte)v); }
                            else sb.Add((byte)e);
                            break;
                    }
                }
                else if (c == '(') { depth++; sb.Add((byte)c); }
                else if (c == ')') { if (--depth == 0) break; sb.Add((byte)c); }
                else sb.Add((byte)c);
            }
            return new PsString(sb.ToArray());
        }
        PsString ReadHex()
        {
            Pos++; var sb = new List<byte>(); int hi = -1;
            while (Pos < end) { int c = d[Pos++]; if (c == '>') break; int v = Hex(c); if (v < 0) continue; if (hi < 0) hi = v; else { sb.Add((byte)(hi * 16 + v)); hi = -1; } }
            if (hi >= 0) sb.Add((byte)(hi * 16));
            return new PsString(sb.ToArray());
        }
        PsString ReadA85()
        {
            Pos += 2; var o = new List<byte>(); Ascii85(d, ref Pos, end, o); return new PsString(o.ToArray());
        }
        public static int Hex(int c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
        /// <summary>Decodes ASCII85 from pos up to and including "~&gt;" (pos is left after it).</summary>
        public static void Ascii85(byte[] d, ref int pos, int end, List<byte> o)
        {
            uint tuple = 0; int n = 0;
            while (pos < end)
            {
                int c = d[pos++];
                if (c == '~') { if (pos < end && d[pos] == '>') pos++; break; }
                if (c == 'z' && n == 0) { o.Add(0); o.Add(0); o.Add(0); o.Add(0); continue; }
                if (c < '!' || c > 'u') continue;
                tuple = tuple * 85 + (uint)(c - '!'); n++;
                if (n == 5) { o.Add((byte)(tuple >> 24)); o.Add((byte)(tuple >> 16)); o.Add((byte)(tuple >> 8)); o.Add((byte)tuple); tuple = 0; n = 0; }
            }
            if (n > 0) { for (int i = n; i < 5; i++) tuple = tuple * 85 + 84; for (int i = 0; i < n - 1; i++) o.Add((byte)(tuple >> (24 - 8 * i))); }
        }
        /// <summary>Skips hex data up to and including the '&gt;' terminator.</summary>
        public void SkipHexToEod() { while (Pos < end && d[Pos] != '>') Pos++; if (Pos < end) Pos++; }
        public void SkipA85ToEod() { while (Pos + 1 < end && !(d[Pos] == '~' && d[Pos + 1] == '>')) Pos++; if (Pos + 1 < end) Pos += 2; }
        /// <summary>Reads up to n bytes of hex into buf; returns count.</summary>
        public int ReadHexBytes(byte[] buf, int n)
        {
            int k = 0, hi = -1;
            while (k < n && Pos < end)
            {
                int c = d[Pos]; int v = Hex(c);
                if (v < 0) { if (c == '>') break; Pos++; continue; }
                Pos++; if (hi < 0) hi = v; else { buf[k++] = (byte)(hi * 16 + v); hi = -1; }
            }
            return k;
        }
        /// <summary>Skips to (and past) a byte sequence; false when not found (position then = end).</summary>
        public bool SkipPast(byte[] pat)
        {
            if (pat.Length == 0) return true;
            for (int i = Pos; i + pat.Length <= end; i++)
            {
                if (d[i] != pat[0]) continue; int k = 1; while (k < pat.Length && d[i + k] == pat[k]) k++;
                if (k == pat.Length) { Pos = i + pat.Length; return true; }
            }
            Pos = end; return false;
        }
        public int ReadRawBytes(byte[] buf, int n) { int k = Math.Min(n, end - Pos); Array.Copy(d, Pos, buf, 0, k); Pos += k; return k; }
        public string? ReadLine()
        {
            if (Pos >= end) return null; int s = Pos; while (Pos < end && d[Pos] != '\n' && d[Pos] != '\r') Pos++;
            string l = Encoding.Latin1.GetString(d, s, Pos - s); if (Pos < end && d[Pos] == '\r') Pos++; if (Pos < end && d[Pos] == '\n') Pos++; return l;
        }
    }

    // ---------------------------------------------------------------------- graphics state
    internal sealed class PsGState
    {
        public Matrix3x2 Ctm;
        public VectorPath Path = new VectorPath();          // device (image) coordinates
        public bool HasCurrent; public float DevCx, DevCy;   // current point, kept in device space (inverse-mapped when user space is asked for)
        public float R, G, B;                                // PS has a single colour for fill and stroke
        public float SR, SG, SB; public bool AiEvenOdd;        // Illustrator: separate stroke colour (uppercase operators) and the XR fill rule
        public float LineWidth = 1, Miter = 10; public VectorCap Cap; public VectorJoin Join; public float[]? Dash; public float DashOffset;
        public VectorClip? Clip; public float Flat = 1;
        public string ColorSpace = "DeviceGray"; public int Ncomp = 1; public PsArray? ColorSpaceArr;
        public float Alpha = 1;                              // Illustrator's setalpha-style extensions (rare)
        public PsGState Clone() { var g = (PsGState)MemberwiseClone(); g.Path = Path.Clone(); g.Dash = Dash == null ? null : (float[])Dash.Clone(); return g; }
    }

    // ---------------------------------------------------------------------- interpreter
    internal sealed partial class PsInterp
    {
        readonly PsScanner sc; readonly VectorImage img = new VectorImage();
        readonly bool aiFile; bool aiPage;                   // Illustrator-native content; page = artboard (see ScanHeader)
        public readonly List<object?> St = new List<object?>(256);
        readonly List<PsDict> dicts = new List<PsDict>();
        readonly PsDict systemdict = new PsDict(), globaldict = new PsDict(), userdict = new PsDict(), statusdict = new PsDict();
        readonly List<PsGState> gsStack = new List<PsGState>();
        PsGState gs = new PsGState();
        readonly HashSet<string> warned = new HashSet<string>();
        readonly Random rnd = new Random(12345);
        int opCount; const int MaxOps = 20_000_000;
        RectangleF bbox; bool haveBbox;
        Matrix3x2 baseCtm;
        int execDepth;

        public PsInterp(byte[] data, int start, int end, bool aiFile = false)
        {
            this.aiFile = aiFile;
            sc = new PsScanner(data, start, end); sc.OnSkip = Warn; sc.OnXmlUid = AiXmlUid;
            ScanHeader(data, start, end);
            Setup(); SetupAi();
        }
        void Warn(string w) { if (warned.Add(w)) img.Warnings.Add(w); }

        void ScanHeader(byte[] d, int start, int end)
        {
            int n = Math.Min(end - start, 65536); string head = Encoding.Latin1.GetString(d, start, n);
            RectangleF? hi = null, bb = null, crop = null, tbox = null; float artW = 0, artH = 0; bool isAi = aiFile, isEps = false; bool first = true;
            foreach (var line in head.Split('\n', '\r'))
            {
                string l = line.Trim(); if (l.Length == 0) continue;
                if (first) { first = false; isEps = l.Contains("EPSF"); }
                if (l.StartsWith("%%HiResBoundingBox:", StringComparison.Ordinal)) hi ??= ParseBox(l.Substring(19));
                else if (l.StartsWith("%%BoundingBox:", StringComparison.Ordinal) && !l.Contains("(atend)")) bb ??= ParseBox(l.Substring(14));
                else if (l.StartsWith("%%Title:", StringComparison.Ordinal) && img.Title == null) img.Title = l.Substring(8).Trim().Trim('(', ')');
                else if (l.StartsWith("%AI3_Cropmarks:", StringComparison.Ordinal)) { crop ??= ParseBox(l.Substring(15)); isAi = true; }
                else if (l.StartsWith("%AI3_TemplateBox:", StringComparison.Ordinal)) { tbox ??= ParseBox(l.Substring(17)); isAi = true; }
                else if (l.StartsWith("%AI5_ArtSize:", StringComparison.Ordinal)) { var p = l.Substring(13).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); if (p.Length >= 2) { float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out artW); float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out artH); } isAi = true; }
                else if (l.StartsWith("%%IncludeResource: procset ", StringComparison.Ordinal) || l.StartsWith("%%DocumentNeededResources: procset ", StringComparison.Ordinal) || l.StartsWith("%%+ procset ", StringComparison.Ordinal))
                {   // procsets the file expects the printer to have (AI3 / AI88): provide inert stubs so "Name /initialize get exec" runs
                    var p = l.Substring(l.IndexOf("procset ", StringComparison.Ordinal) + 8).Split(' ', StringSplitOptions.RemoveEmptyEntries); if (p.Length > 0) aiProcsets.Add(p[0]);
                }
                else if (l.StartsWith("%AI", StringComparison.Ordinal)) isAi = true;
            }
            if (isAi && !isEps)
            {   // Illustrator document (not an EPS export): the page is the artboard, in ruler coordinates (y up), not %%BoundingBox
                // (which may be the art extent, or expressed from the page corner by some exporters). Rules follow Inkscape's extension-ai.
                RectangleF? art = null;
                if (crop is RectangleF c && c.Width != 0 && c.Height != 0) art = RectangleF.FromLTRB(Math.Min(c.Left, c.Right), Math.Min(c.Top, c.Bottom), Math.Max(c.Left, c.Right), Math.Max(c.Top, c.Bottom));
                else if (artW > 0 && artH > 0 && tbox is RectangleF t)
                {   // FromLTRB(l, b, r, t): Left = l, Top = b, Right = r, Bottom = t
                    float x0 = MathF.Floor(-(artW - t.Left - t.Right) / 2), y0 = MathF.Floor(-(artH + t.Top + t.Bottom) / 2);
                    art = RectangleF.FromLTRB(x0, -y0 - artH, x0 + artW, -y0);
                }
                else if (tbox is RectangleF t3)
                {   // AI3: no art size; the template box centre is the page centre
                    float h = -t3.Bottom - t3.Top, w = t3.Left + t3.Right;
                    art = h < 0 ? RectangleF.FromLTRB(0, 0, w, -h) : RectangleF.FromLTRB(0, -h, w, 0);
                }
                if (art is RectangleF a && a.Width > 0 && a.Height > 0) { bbox = a; haveBbox = true; aiPage = true; return; }
            }
            if (bb == null)
            {   // (atend): look at the tail
                int tn = Math.Min(end - start, 8192); string tail = Encoding.Latin1.GetString(d, end - tn, tn);
                int i = tail.LastIndexOf("%%BoundingBox:", StringComparison.Ordinal);
                if (i >= 0) { int e = tail.IndexOfAny(new[] { '\n', '\r' }, i); bb = ParseBox(tail.Substring(i + 14, (e < 0 ? tail.Length : e) - i - 14)); }
                i = tail.LastIndexOf("%%HiResBoundingBox:", StringComparison.Ordinal);
                if (i >= 0) { int e = tail.IndexOfAny(new[] { '\n', '\r' }, i); hi ??= ParseBox(tail.Substring(i + 19, (e < 0 ? tail.Length : e) - i - 19)); }
            }
            var box = hi ?? bb;
            if (box.HasValue && box.Value.Width > 0 && box.Value.Height > 0) { bbox = box.Value; haveBbox = true; }
            else bbox = new RectangleF(0, 0, 612, 792);
            static RectangleF? ParseBox(string s)
            {
                var p = s.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); if (p.Length < 4) return null;
                var v = new float[4]; for (int i = 0; i < 4; i++) if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
                return RectangleF.FromLTRB(v[0], v[1], v[2], v[3]);   // llx lly urx ury (y up)
            }
        }

        public VectorImage Run()
        {
            img.ViewBox = new RectangleF(0, 0, bbox.Width, bbox.Height); img.Width = bbox.Width; img.Height = bbox.Height;
            baseCtm = new Matrix3x2(1, 0, 0, -1, -bbox.Left, bbox.Bottom);   // PS y-up -> image y-down; (llx, ury) -> (0, 0)   [bbox.Bottom = ury in FromLTRB terms]
            gs.Ctm = baseCtm;
            try { ExecStream(); }
            catch (PsQuit) { }
            catch (PsStop) { Warn("PostScript error: 'stop' reached the top level (the picture may be incomplete)"); }
            catch (PsExit) { }
            catch (InvalidOperationException ex) { Warn("PostScript error: " + ex.Message + " (the picture may be incomplete)"); if (trace != null) { Console.Error.WriteLine("PS trace (last ops): " + string.Join(" ", trace)); Console.Error.WriteLine("stack: " + string.Join(" | ", St.ConvertAll(o => o?.ToString() ?? "null"))); } }
            catch (IndexOutOfRangeException) { Warn("PostScript error: bad index (the picture may be incomplete)"); }
            catch (InvalidCastException ex) { Warn("PostScript error: typecheck (" + ex.Message + ") (the picture may be incomplete)"); if (trace != null) { Console.Error.WriteLine("PS trace (last ops): " + string.Join(" ", trace)); Console.Error.WriteLine("stack: " + string.Join(" | ", St.ConvertAll(o => o?.ToString() ?? "null"))); } }
            catch (NullReferenceException) { Warn("PostScript error: null object (the picture may be incomplete)"); if (trace != null) Console.Error.WriteLine("PS trace (last ops): " + string.Join(" ", trace)); }
            if (!haveBbox) { var b = img.Bounds(); if (!b.IsEmpty) { img.ViewBox = b; img.Width = b.Width; img.Height = b.Height; } }
            else if (aiPage)
            {   // artwork placed entirely off the artboard (e.g. CorelDRAW exports with the drawing beside the page): show the art instead of an empty page
                var b = img.Bounds();
                if (!b.IsEmpty && !b.IntersectsWith(new RectangleF(0, 0, img.Width, img.Height))) { img.ViewBox = b; img.Width = b.Width; img.Height = b.Height; Warn("artwork lies outside the artboard: view box set to the artwork bounds"); }
            }
            return img;
        }
        void ExecStream()
        {
            while (true)
            {
                var t = sc.Next(); if (t == null) break;
                ExecToken(t);
            }
        }
        /// <summary>Executes a scanned token: procedures are pushed, executable names looked up and run, everything else pushed.</summary>
        void ExecToken(object t)
        {
            if (t is PsName n && n.Exec) ExecName(n.N);
            else if (t is PsName ln && ln.N.StartsWith("//", StringComparison.Ordinal)) { St.Add(Lookup(ln.N.Substring(2)) ?? PsNull.I); }
            else St.Add(t);
        }
        static readonly bool Trace = Environment.GetEnvironmentVariable("VECTRACE") == "1";
        readonly Queue<string>? trace = Trace ? new Queue<string>() : null;
        void ExecName(string name)
        {
            if (++opCount > MaxOps) throw new PsQuit();
            if (trace != null) { trace.Enqueue(name); if (trace.Count > 40) trace.Dequeue(); }
            var v = Lookup(name);
            if (v == null) { Warn($"unknown PostScript operator '{name}' ignored"); return; }
            Exec(v);
        }
        /// <summary>Executes an object (procedure bodies run element by element).</summary>
        public void Exec(object? v, bool direct = false)
        {
            switch (v)
            {
                case PsString ls when !ls.Exec && !direct: St.Add(v); break;   // a literal string bound to a name is pushed, not run
                case PsOp op: op.F(this); break;
                case PsArray a when a.Exec:
                    if (++execDepth > 2000) { execDepth--; throw new InvalidOperationException("execution stack overflow"); }
                    try
                    {
                        var items = a.A;
                        for (int i = 0; i < items.Count; i++)
                        {
                            var e = items[i];
                            if (e is PsName n) { if (n.Exec) ExecName(n.N); else if (n.N.StartsWith("//", StringComparison.Ordinal)) St.Add(Lookup(n.N.Substring(2)) ?? PsNull.I); else St.Add(e); }   // //name inside a procedure: immediately evaluated
                            else if (e is PsArray) St.Add(e);   // nested procedure: pushed
                            else if (e is PsOp o) o.F(this);
                            else St.Add(e);
                        }
                    }
                    finally { execDepth--; }
                    break;
                case PsName n2 when n2.Exec: ExecName(n2.N); break;
                case PsString s: { var sub = new PsInterpString(this, s.B); sub.Run(); break; }
                case PsFile pf when pf.Kind == 2 && pf.Data != null: { var sub = new PsInterpString(this, pf.Data); sub.Run(); break; }   // (string) filter cvx exec
                default: St.Add(v); break;
            }
        }
        sealed class PsInterpString
        {
            readonly PsInterp p; readonly PsScanner s;
            public PsInterpString(PsInterp p, byte[] b) { this.p = p; s = new PsScanner(b, 0, b.Length); }
            public void Run() { while (true) { var t = s.Next(); if (t == null) break; p.ExecToken(t); } }
        }
        object? Lookup(string name)
        {
            for (int i = dicts.Count - 1; i >= 0; i--) if (dicts[i].D.TryGetValue(name, out var v)) return v;
            return null;
        }
        PsDict? Where(string name) { for (int i = dicts.Count - 1; i >= 0; i--) if (dicts[i].D.ContainsKey(name)) return dicts[i]; return null; }

        // ---------------------------------------------------------------- stack helpers
        object? Pop() { if (St.Count == 0) throw new InvalidOperationException("stack underflow"); var v = St[^1]; St.RemoveAt(St.Count - 1); return v; }
        double Num() { var v = Pop(); if (v is double d) return d; if (v is bool) throw new InvalidOperationException("typecheck (bool for number)"); throw new InvalidOperationException($"typecheck ({v?.GetType().Name} for number)"); }
        int Int() => (int)Math.Round(Num());
        float F() => (float)Num();
        bool Bool() { var v = Pop(); if (v is bool b) return b; throw new InvalidOperationException("typecheck (bool)"); }
        PsArray Arr() { var v = Pop(); if (v is PsArray a) return a; throw new InvalidOperationException("typecheck (array)"); }
        PsDict Dict() { var v = Pop(); if (v is PsDict d) return d; throw new InvalidOperationException("typecheck (dict)"); }
        string Key(object? v) => v switch { PsName n => n.N, PsString s => s.S, double d => d.ToString(CultureInfo.InvariantCulture), bool b => b ? "true" : "false", null => "null", _ => v.GetType().Name + v.GetHashCode() };
        void Push(object? v) => St.Add(v);
        void Push(double v) => St.Add(v);
        void Push(bool v) => St.Add(v);
        void Push(string n) => St.Add(new PsName(n, false));
        static Matrix3x2 ToMatrix(PsArray a)
        {
            if (a.A.Count < 6) throw new InvalidOperationException("rangecheck (matrix)");
            float V(int i) => a.A[i] is double d ? (float)d : 0;
            return new Matrix3x2(V(0), V(1), V(2), V(3), V(4), V(5));
        }
        static PsArray FromMatrix(Matrix3x2 m, PsArray? into = null)
        {
            var a = into ?? new PsArray(new List<object?>(6) { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, false);
            while (a.A.Count < 6) a.A.Add(0.0);
            a.A[0] = (double)m.M11; a.A[1] = (double)m.M12; a.A[2] = (double)m.M21; a.A[3] = (double)m.M22; a.A[4] = (double)m.M31; a.A[5] = (double)m.M32; return a;
        }

        // ---------------------------------------------------------------- geometry helpers
        Vector2 ToDev(float x, float y) => Vector2.Transform(new Vector2(x, y), gs.Ctm);
        Vector2 ToUser(float dx, float dy) { if (!Matrix3x2.Invert(gs.Ctm, out var inv)) return new Vector2(dx, dy); return Vector2.Transform(new Vector2(dx, dy), inv); }
        void MoveTo(float x, float y) { var d = ToDev(x, y); gs.Path.MoveTo(d.X, d.Y); gs.HasCurrent = true; gs.DevCx = d.X; gs.DevCy = d.Y; }
        void LineTo(float x, float y) { if (!gs.HasCurrent) throw new InvalidOperationException("nocurrentpoint"); var d = ToDev(x, y); gs.Path.LineTo(d.X, d.Y); gs.DevCx = d.X; gs.DevCy = d.Y; }
        void CurveTo(float x1, float y1, float x2, float y2, float x3, float y3)
        {
            if (!gs.HasCurrent) throw new InvalidOperationException("nocurrentpoint");
            var a = ToDev(x1, y1); var b = ToDev(x2, y2); var c = ToDev(x3, y3); gs.Path.CurveTo(a.X, a.Y, b.X, b.Y, c.X, c.Y); gs.DevCx = c.X; gs.DevCy = c.Y;
        }
        Vector2 CurrentUser() { if (!gs.HasCurrent) throw new InvalidOperationException("nocurrentpoint"); return ToUser(gs.DevCx, gs.DevCy); }
        void Arc(float cx, float cy, float r, float a1, float a2, bool ccw)
        {
            double s = a1 * Math.PI / 180, e = a2 * Math.PI / 180;
            if (ccw) { while (e < s) e += 2 * Math.PI; } else { while (e > s) e -= 2 * Math.PI; }
            double sweep = e - s;
            float sx = (float)(cx + r * Math.Cos(s)), sy = (float)(cy + r * Math.Sin(s));
            if (gs.HasCurrent) LineTo(sx, sy); else MoveTo(sx, sy);
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
            double d = sweep / n, t = 4.0 / 3 * Math.Tan(d / 4), a0 = s;
            for (int i = 0; i < n; i++)
            {
                double a1r = a0 + d, c0 = Math.Cos(a0), s0 = Math.Sin(a0), c1 = Math.Cos(a1r), s1 = Math.Sin(a1r);
                CurveTo((float)(cx + r * (c0 - t * s0)), (float)(cy + r * (s0 + t * c0)), (float)(cx + r * (c1 + t * s1)), (float)(cy + r * (s1 - t * c1)), (float)(cx + r * c1), (float)(cy + r * s1));
                a0 = a1r;
            }
        }
        void ArcT(float x1, float y1, float x2, float y2, float r, bool pushTangents)
        {
            var p0 = CurrentUser();
            float d0x = p0.X - x1, d0y = p0.Y - y1, d1x = x2 - x1, d1y = y2 - y1;
            float l0 = MathF.Sqrt(d0x * d0x + d0y * d0y), l1 = MathF.Sqrt(d1x * d1x + d1y * d1y);
            if (l0 < 1e-9f || l1 < 1e-9f || r <= 0) { LineTo(x1, y1); if (pushTangents) { Push(x1); Push(y1); Push(x1); Push(y1); } return; }
            d0x /= l0; d0y /= l0; d1x /= l1; d1y /= l1;
            float cosT = d0x * d1x + d0y * d1y; float theta = MathF.Acos(Math.Clamp(cosT, -1, 1));
            if (theta < 1e-4f || MathF.PI - theta < 1e-4f) { LineTo(x1, y1); if (pushTangents) { Push(x1); Push(y1); Push(x1); Push(y1); } return; }
            float tan = r / MathF.Tan(theta / 2);
            float t1x = x1 + d0x * tan, t1y = y1 + d0y * tan, t2x = x1 + d1x * tan, t2y = y1 + d1y * tan;
            // centre: from t1 along the normal towards the inside
            float cross = d0x * d1y - d0y * d1x; float nx = -d0y, ny = d0x; if (cross < 0) { nx = -nx; ny = -ny; }
            float cx = t1x + nx * r, cy = t1y + ny * r;
            float a1 = MathF.Atan2(t1y - cy, t1x - cx) * 180 / MathF.PI, a2 = MathF.Atan2(t2y - cy, t2x - cx) * 180 / MathF.PI;
            LineTo(t1x, t1y); Arc(cx, cy, r, a1, a2, cross < 0);
            if (pushTangents) { Push(t1x); Push(t1y); Push(t2x); Push(t2y); }
        }

        // ---------------------------------------------------------------- painting
        int Argb(float r, float g, float b, float a = 1f) => (Math.Clamp((int)MathF.Round(a * gs.Alpha * 255), 0, 255) << 24) | Math.Clamp((int)MathF.Round(r * 255), 0, 255) << 16 | Math.Clamp((int)MathF.Round(g * 255), 0, 255) << 8 | Math.Clamp((int)MathF.Round(b * 255), 0, 255);
        void Fill(bool evenOdd)
        {
            if (!gs.Path.IsEmpty)
            {
                var p = gs.Path.Clone(); p.Close();
                img.Shapes.Add(new VectorShape { Path = p, Fill = new VectorColor(Argb(gs.R, gs.G, gs.B)), EvenOdd = evenOdd, Clip = gs.Clip });
            }
            NewPath();
        }
        void Stroke()
        {
            if (!gs.Path.IsEmpty)
            {
                var s = new VectorShape { Fill = null, Stroke = new VectorColor(Argb(gs.R, gs.G, gs.B)), Cap = gs.Cap, Join = gs.Join, MiterLimit = gs.Miter, Clip = gs.Clip };
                var m = gs.Ctm; float scale = VectorRender.MeanScale(m);
                bool uniform = MathF.Abs(m.M11 * m.M11 + m.M12 * m.M12 - (m.M21 * m.M21 + m.M22 * m.M22)) < 1e-3f * Math.Max(1e-6f, scale * scale) && MathF.Abs(m.M11 * m.M21 + m.M12 * m.M22) < 1e-3f * Math.Max(1e-6f, scale * scale);
                if (uniform || !Matrix3x2.Invert(m, out var inv))
                {   // device-space stroke with the scaled width
                    s.Path = gs.Path.Clone(); s.StrokeWidth = gs.LineWidth * scale;
                    if (gs.Dash != null) { s.Dash = new float[gs.Dash.Length]; for (int i = 0; i < gs.Dash.Length; i++) s.Dash[i] = gs.Dash[i] * scale; s.DashOffset = gs.DashOffset * scale; }
                }
                else
                {   // squished CTM: keep the path in user space so the renderer strokes it with the anisotropic pen
                    s.Path = gs.Path.Transformed(inv); s.Transform = m; s.StrokeWidth = gs.LineWidth; s.Dash = gs.Dash == null ? null : (float[])gs.Dash.Clone(); s.DashOffset = gs.DashOffset;
                    s.Clip = gs.Clip?.Transformed(inv);
                }
                if (s.StrokeWidth <= 0) s.StrokeWidth = 0;   // hairline: the renderer widens to MinStrokeWidth
                img.Shapes.Add(s);
            }
            NewPath();
        }
        void Clip(bool evenOdd)
        {
            if (gs.Path.IsEmpty) return;
            var p = gs.Path.Clone(); p.Close();
            gs.Clip = gs.Clip == null ? new VectorClip(p, evenOdd) : gs.Clip.Intersect(p, evenOdd);
            // (clip does not consume the path)
        }
        void NewPath() { gs.Path = new VectorPath(); gs.HasCurrent = false; }
        void SetColor(float r, float g, float b) { gs.R = r; gs.G = g; gs.B = b; }
        void SetCmyk(float c, float m, float y, float k) => SetColor((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
        void SetHsb(float h, float s, float v)
        {
            h -= MathF.Floor(h); float i = MathF.Floor(h * 6), f = h * 6 - i, p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            switch ((int)i % 6) { case 0: SetColor(v, t, p); break; case 1: SetColor(q, v, p); break; case 2: SetColor(p, v, t); break; case 3: SetColor(p, q, v); break; case 4: SetColor(t, p, v); break; default: SetColor(v, p, q); break; }
        }
        void SetColorSpace(object? cs)
        {
            string name; PsArray? arr = null;
            if (cs is PsName n) name = n.N; else if (cs is PsArray a && a.A.Count > 0 && a.A[0] is PsName an) { name = an.N; arr = a; } else { Warn("setcolorspace: unsupported colour space"); return; }
            gs.ColorSpace = name; gs.ColorSpaceArr = arr;
            switch (name)
            {
                case "DeviceGray": case "CalGray": gs.Ncomp = 1; SetColor(0, 0, 0); break;
                case "DeviceRGB": case "CalRGB": gs.Ncomp = 3; SetColor(0, 0, 0); break;
                case "DeviceCMYK": gs.Ncomp = 4; SetColor(0, 0, 0); break;
                case "Lab": gs.Ncomp = 3; break;
                case "ICCBased": gs.Ncomp = arr != null && arr.A.Count > 1 && arr.A[1] is PsDict d && d.D.TryGetValue("N", out var nn) && nn is double nd ? (int)nd : 3; break;
                case "Separation": gs.Ncomp = 1; SetColor(0, 0, 0); break;
                case "DeviceN": gs.Ncomp = arr != null && arr.A.Count > 1 && arr.A[1] is PsArray names ? names.A.Count : 1; break;
                case "Indexed": gs.Ncomp = 1; break;
                case "Pattern": gs.Ncomp = 0; break;
                default: gs.Ncomp = 1; Warn($"colour space {name} approximated"); break;
            }
        }
        /// <summary>setcolor: components according to the current colour space.</summary>
        void SetColorOp()
        {
            switch (gs.ColorSpace)
            {
                case "DeviceGray": case "CalGray": { float g = F(); SetColor(g, g, g); break; }
                case "DeviceRGB": case "CalRGB": { float b = F(), g = F(), r = F(); SetColor(r, g, b); break; }
                case "DeviceCMYK": { float k = F(), y = F(), m = F(), c = F(); SetCmyk(c, m, y, k); break; }
                case "ICCBased": if (gs.Ncomp == 4) goto case "DeviceCMYK"; if (gs.Ncomp == 1) goto case "DeviceGray"; goto case "DeviceRGB";
                case "Lab": { float bb = F(), aa = F(), L = F(); float l = Math.Clamp(L / 100, 0, 1); SetColor(l, l, l); break; }
                case "Separation": case "DeviceN":
                    {   // tint(s) through the tint transform when it is a procedure we can run; otherwise grey
                        int n = Math.Max(1, gs.Ncomp); var tints = new double[n]; for (int i = n - 1; i >= 0; i--) tints[i] = Num();
                        var arr = gs.ColorSpaceArr;
                        if (arr != null && arr.A.Count >= 4 && arr.A[3] is PsArray proc && proc.Exec)
                        {
                            int depth = St.Count; foreach (var t in tints) Push(t);
                            string alt = arr.A[2] is PsName an ? an.N : arr.A[2] is PsArray aa2 && aa2.A.Count > 0 && aa2.A[0] is PsName an2 ? an2.N : "DeviceGray";
                            try
                            {
                                Exec(proc);
                                int got = St.Count - depth;
                                if (alt == "DeviceCMYK" && got >= 4) { float k = F(), y = F(), m = F(), c = F(); SetCmyk(c, m, y, k); }
                                else if (alt == "DeviceRGB" && got >= 3) { float b = F(), g = F(), r = F(); SetColor(r, g, b); }
                                else if (got >= 1) { float g = F(); SetColor(g, g, g); }
                                while (St.Count > depth) Pop();
                            }
                            catch (InvalidOperationException) { while (St.Count > depth) Pop(); float g = 1 - (float)tints[0]; SetColor(g, g, g); }
                        }
                        else
                        {
                            float g = 1 - (float)tints[0];
                            if (arr != null && arr.A.Count > 1 && arr.A[1] is PsName sn && (sn.N == "All" || sn.N == "Black")) SetColor(g, g, g); else SetColor(g, g, g);
                        }
                        break;
                    }
                case "Indexed":
                    {
                        int idx = Int(); var arr = gs.ColorSpaceArr;
                        if (arr != null && arr.A.Count >= 4)
                        {
                            string bas = arr.A[1] is PsName bn ? bn.N : arr.A[1] is PsArray ba && ba.A.Count > 0 && ba.A[0] is PsName bn2 ? bn2.N : "DeviceRGB";
                            int nc = bas == "DeviceCMYK" ? 4 : bas == "DeviceGray" ? 1 : 3;
                            byte[]? tbl = arr.A[3] is PsString ts ? ts.B : null;
                            if (tbl != null && (idx + 1) * nc <= tbl.Length)
                            {
                                int o = idx * nc;
                                if (nc == 4) SetCmyk(tbl[o] / 255f, tbl[o + 1] / 255f, tbl[o + 2] / 255f, tbl[o + 3] / 255f);
                                else if (nc == 1) SetColor(tbl[o] / 255f, tbl[o] / 255f, tbl[o] / 255f);
                                else SetColor(tbl[o] / 255f, tbl[o + 1] / 255f, tbl[o + 2] / 255f);
                            }
                        }
                        break;
                    }
                case "Pattern": { Pop(); Warn("pattern colour approximated by 50 % grey"); SetColor(0.5f, 0.5f, 0.5f); break; }
                default: { float g = F(); SetColor(g, g, g); break; }
            }
        }

        void SkipFileData(PsFile f, long total)
        {
            if (f.Filter == "ASCIIHexDecode") sc.SkipHexToEod();
            else if (f.Filter == "ASCII85Decode") sc.SkipA85ToEod();
            else if (f.Filter == null) { var buf = new byte[Math.Min(total, 1 << 20)]; long left = total; while (left > 0) { int k = sc.ReadRawBytes(buf, (int)Math.Min(left, buf.Length)); if (k == 0) break; left -= k; } }
            else { Warn($"image data through {f.Filter} skipped heuristically"); sc.SkipA85ToEod(); }
        }
        object? DictGet(PsDict d, string k) => d.D.TryGetValue(k, out var v) ? v : null;

        // ---------------------------------------------------------------- operators
        const int BaseDicts = 4;
        void Def(string n, Action<PsInterp> f) => systemdict.D[n] = new PsOp(n, f);
        void Setup()
        {
            dicts.Add(aidict); dicts.Add(systemdict); dicts.Add(globaldict); dicts.Add(userdict);   // aidict: Illustrator operators, lowest priority (prologs redefine them)
            systemdict.D["systemdict"] = systemdict; systemdict.D["userdict"] = userdict; systemdict.D["globaldict"] = globaldict; systemdict.D["statusdict"] = statusdict; systemdict.D["shareddict"] = globaldict;
            systemdict.D["true"] = true; systemdict.D["false"] = false; systemdict.D["null"] = PsNull.I;
            systemdict.D["languagelevel"] = 3.0; systemdict.D["product"] = new PsString("SR2D"); systemdict.D["version"] = new PsString("3010"); systemdict.D["revision"] = 0.0; systemdict.D["serialnumber"] = 0.0;
            systemdict.D["FontDirectory"] = new PsDict(); systemdict.D["GlobalFontDirectory"] = new PsDict(); systemdict.D["SharedFontDirectory"] = systemdict.D["GlobalFontDirectory"]; systemdict.D["$error"] = new PsDict();
            var ed = new PsDict(); systemdict.D["errordict"] = ed;
            foreach (var en in new[] { "handleerror", "undefined", "typecheck", "rangecheck", "stackunderflow", "stackoverflow", "invalidaccess", "invalidfont", "ioerror", "limitcheck", "syntaxerror", "unmatchedmark", "VMerror", "dictfull", "dictstackoverflow", "dictstackunderflow", "execstackoverflow", "interrupt", "invalidexit", "invalidfileaccess", "invalidrestore", "nocurrentpoint", "timeout", "undefinedfilename", "undefinedresource", "undefinedresult", "unregistered", "configurationerror" })
                ed.D[en] = new PsOp(en, en == "handleerror" ? (Action<PsInterp>)(q => { }) : q => throw new PsStop());
            systemdict.D["mark"] = new PsOp("mark", p => p.Push(PsMark.I));

            // ---- stack
            Def("pop", p => p.Pop());
            Def("exch", p => { var b = p.Pop(); var a = p.Pop(); p.Push(b); p.Push(a); });
            Def("dup", p => { var a = p.Pop(); p.Push(a); p.Push(a); });
            Def("copy", p =>
            {
                var v = p.Pop();
                if (v is double d) { int n = (int)d; if (n < 0 || n > p.St.Count) throw new InvalidOperationException("rangecheck (copy)"); int s = p.St.Count - n; for (int i = 0; i < n; i++) p.Push(p.St[s + i]); }
                else if (v is PsArray dst) { var src = p.Arr(); for (int i = 0; i < src.A.Count && i < dst.A.Count; i++) dst.A[i] = src.A[i]; p.Push(new PsArray(dst.A.GetRange(0, Math.Min(src.A.Count, dst.A.Count)), dst.Exec)); }
                else if (v is PsDict dd) { var src = p.Dict(); foreach (var kv in src.D) dd.D[kv.Key] = kv.Value; p.Push(dd); }
                else if (v is PsString ds) { var src = (PsString)p.Pop()!; int n = Math.Min(src.B.Length, ds.B.Length); Array.Copy(src.B, ds.B, n); p.Push(new PsString(ds.B.AsSpan(0, n).ToArray())); }
                else throw new InvalidOperationException("typecheck (copy)");
            });
            Def("index", p => { int n = p.Int(); if (n < 0 || n >= p.St.Count) throw new InvalidOperationException("rangecheck (index)"); p.Push(p.St[p.St.Count - 1 - n]); });
            Def("roll", p =>
            {
                int j = p.Int(), n = p.Int(); if (n < 0 || n > p.St.Count) throw new InvalidOperationException("rangecheck (roll)"); if (n == 0) return;
                j %= n; if (j < 0) j += n; if (j == 0) return;
                int s = p.St.Count - n; var tmp = p.St.GetRange(s, n); for (int i = 0; i < n; i++) p.St[s + (i + j) % n] = tmp[i];
            });
            Def("clear", p => p.St.Clear());
            Def("count", p => p.Push((double)p.St.Count));
            Def("counttomark", p => { for (int i = p.St.Count - 1; i >= 0; i--) if (p.St[i] is PsMark) { p.Push((double)(p.St.Count - 1 - i)); return; } throw new InvalidOperationException("unmatchedmark"); });
            Def("cleartomark", p => { while (p.St.Count > 0) { var v = p.Pop(); if (v is PsMark) return; } });
            Def("[", p => p.Push(PsMark.I));
            Def("]", p => { var l = new List<object?>(); while (true) { var v = p.Pop(); if (v is PsMark) break; l.Add(v); } l.Reverse(); p.Push(new PsArray(l, false)); });
            Def("<<", p => p.Push(PsMark.I));
            Def(">>", p =>
            {
                var l = new List<object?>(); while (true) { var v = p.Pop(); if (v is PsMark) break; l.Add(v); } l.Reverse();
                var d = new PsDict(); for (int i = 0; i + 1 < l.Count; i += 2) d.D[p.Key(l[i])] = l[i + 1]; p.Push(d);
            });
            Def("}", p => { });
            // ---- arithmetic
            Def("add", p => { double b = p.Num(), a = p.Num(); p.Push(a + b); });
            Def("sub", p => { double b = p.Num(), a = p.Num(); p.Push(a - b); });
            Def("mul", p => { double b = p.Num(), a = p.Num(); p.Push(a * b); });
            Def("div", p => { double b = p.Num(), a = p.Num(); p.Push(b == 0 ? (a == 0 ? 0 : a > 0 ? 1e30 : -1e30) : a / b); });
            Def("idiv", p => { int b = p.Int(), a = p.Int(); p.Push(b == 0 ? 0 : (double)(a / b)); });
            Def("mod", p => { int b = p.Int(), a = p.Int(); p.Push(b == 0 ? 0 : (double)(a % b)); });
            Def("neg", p => p.Push(-p.Num()));
            Def("abs", p => p.Push(Math.Abs(p.Num())));
            Def("ceiling", p => p.Push(Math.Ceiling(p.Num())));
            Def("floor", p => p.Push(Math.Floor(p.Num())));
            Def("round", p => p.Push(Math.Floor(p.Num() + 0.5)));
            Def("truncate", p => p.Push(Math.Truncate(p.Num())));
            Def("sqrt", p => p.Push(Math.Sqrt(Math.Max(0, p.Num()))));
            Def("atan", p => { double den = p.Num(), num = p.Num(); double a = Math.Atan2(num, den) * 180 / Math.PI; if (a < 0) a += 360; p.Push(a); });
            Def("cos", p => p.Push(Math.Cos(p.Num() * Math.PI / 180)));
            Def("sin", p => p.Push(Math.Sin(p.Num() * Math.PI / 180)));
            Def("exp", p => { double e = p.Num(), b = p.Num(); p.Push(Math.Pow(b, e)); });
            Def("ln", p => p.Push(Math.Log(Math.Max(1e-300, p.Num()))));
            Def("log", p => p.Push(Math.Log10(Math.Max(1e-300, p.Num()))));
            Def("cvi", p => { var v = p.Pop(); if (v is PsString s && PsScanner.TryNumber(s.S.Trim(), out var d)) p.Push(Math.Truncate(d)); else if (v is double dd) p.Push(Math.Truncate(dd)); else throw new InvalidOperationException("typecheck (cvi)"); });
            Def("cvr", p => { var v = p.Pop(); if (v is PsString s && PsScanner.TryNumber(s.S.Trim(), out var d)) p.Push(d); else if (v is double dd) p.Push(dd); else throw new InvalidOperationException("typecheck (cvr)"); });
            Def("rand", p => p.Push((double)p.rnd.Next()));
            Def("srand", p => p.Pop());
            Def("rrand", p => p.Push(0.0));
            // ---- relational / boolean / bit
            Def("eq", p => { var b = p.Pop(); var a = p.Pop(); p.Push(Eq(a, b)); });
            Def("ne", p => { var b = p.Pop(); var a = p.Pop(); p.Push(!Eq(a, b)); });
            Def("gt", p => p.Push(Cmp(p) > 0)); Def("ge", p => p.Push(Cmp(p) >= 0)); Def("lt", p => p.Push(Cmp(p) < 0)); Def("le", p => p.Push(Cmp(p) <= 0));
            Def("and", p => { var b = p.Pop(); var a = p.Pop(); if (a is bool ba && b is bool bb) p.Push(ba && bb); else p.Push((double)((int)(double)a! & (int)(double)b!)); });
            Def("or", p => { var b = p.Pop(); var a = p.Pop(); if (a is bool ba && b is bool bb) p.Push(ba || bb); else p.Push((double)((int)(double)a! | (int)(double)b!)); });
            Def("xor", p => { var b = p.Pop(); var a = p.Pop(); if (a is bool ba && b is bool bb) p.Push(ba ^ bb); else p.Push((double)((int)(double)a! ^ (int)(double)b!)); });
            Def("not", p => { var a = p.Pop(); if (a is bool ba) p.Push(!ba); else p.Push((double)~(int)(double)a!); });
            Def("bitshift", p => { int s = p.Int(), a = p.Int(); p.Push((double)(s >= 0 ? a << s : a >> -s)); });
            // ---- control
            Def("exec", p => p.Exec(p.Pop(), direct: true));
            Def("if", p => { var proc = p.Pop(); bool c = p.Bool(); if (c) p.Exec(proc); });
            Def("ifelse", p => { var p2 = p.Pop(); var p1 = p.Pop(); bool c = p.Bool(); p.Exec(c ? p1 : p2); });
            Def("for", p =>
            {
                var proc = p.Pop(); double lim = p.Num(), inc = p.Num(), init = p.Num();
                if (inc == 0) return;
                try { for (double i = init; inc > 0 ? i <= lim : i >= lim; i += inc) { p.Push(i); p.Exec(proc); if (++p.opCount > MaxOps) throw new PsQuit(); } } catch (PsExit) { }
            });
            Def("repeat", p => { var proc = p.Pop(); int n = p.Int(); try { for (int i = 0; i < n; i++) { p.Exec(proc); if (++p.opCount > MaxOps) throw new PsQuit(); } } catch (PsExit) { } });
            Def("loop", p => { var proc = p.Pop(); try { while (true) { p.Exec(proc); if (++p.opCount > MaxOps) throw new PsQuit(); } } catch (PsExit) { } });
            Def("forall", p =>
            {
                var proc = p.Pop(); var coll = p.Pop();
                try
                {
                    if (coll is PsArray a) { var items = a.A.ToArray(); foreach (var it in items) { p.Push(it); p.Exec(proc); } }
                    else if (coll is PsDict d) { foreach (var kv in new List<KeyValuePair<string, object?>>(d.D)) { p.Push(kv.Key); p.Push(kv.Value); p.Exec(proc); } }
                    else if (coll is PsString s) { foreach (var b in s.B) { p.Push((double)b); p.Exec(proc); } }
                    else throw new InvalidOperationException("typecheck (forall)");
                }
                catch (PsExit) { }
            });
            Def("exit", p => throw new PsExit());
            Def("stop", p => throw new PsStop());
            Def("stopped", p => { var proc = p.Pop(); try { p.Exec(proc); p.Push(false); } catch (PsStop) { p.Push(true); } catch (InvalidOperationException) { p.Push(true); } });
            Def("quit", p => throw new PsQuit());
            Def("countexecstack", p => p.Push((double)p.execDepth));
            Def("execstack", p => { p.Pop(); p.Push(new PsArray(new List<object?>(), false)); });
            // ---- dictionaries
            Def("dict", p => { p.Pop(); p.Push(new PsDict()); });
            Def("begin", p => p.dicts.Add(p.Dict()));
            Def("end", p => { if (p.dicts.Count > BaseDicts) p.dicts.RemoveAt(p.dicts.Count - 1); });
            Def("def", p => { var v = p.Pop(); var k = p.Pop(); p.dicts[^1].D[p.Key(k)] = v; });
            Def("store", p => { var v = p.Pop(); var k = p.Key(p.Pop()); (p.Where(k) ?? p.dicts[^1]).D[k] = v; });
            Def("load", p => { var k = p.Key(p.Pop()); var v = p.Lookup(k); if (v == null) throw new InvalidOperationException("undefined: " + k); p.Push(v); });
            Def("known", p => { var k = p.Key(p.Pop()); var d = p.Dict(); p.Push(d.D.ContainsKey(k)); });
            Def("where", p => { var k = p.Key(p.Pop()); var d = p.Where(k); if (d != null) { p.Push(d); p.Push(true); } else p.Push(false); });
            Def("currentdict", p => p.Push(p.dicts[^1]));
            Def("countdictstack", p => p.Push((double)p.dicts.Count));
            Def("dictstack", p => { p.Pop(); p.Push(new PsArray(new List<object?>(p.dicts), false)); });
            Def("cleardictstack", p => { while (p.dicts.Count > BaseDicts) p.dicts.RemoveAt(p.dicts.Count - 1); });
            Def("undef", p => { var k = p.Key(p.Pop()); p.Dict().D.Remove(k); });
            Def("maxlength", p => { p.Pop(); p.Push(1e6); });
            Def("get", p =>
            {
                var k = p.Pop(); var c = p.Pop();
                if (c is PsArray a) { int i = (int)(double)k!; if (i < 0 || i >= a.A.Count) throw new InvalidOperationException("rangecheck (get)"); p.Push(a.A[i]); }
                else if (c is PsDict d) { var key = p.Key(k); if (!d.D.TryGetValue(key, out var v)) throw new InvalidOperationException("undefined: " + key); p.Push(v); }
                else if (c is PsString s) { int i = (int)(double)k!; if (i < 0 || i >= s.B.Length) throw new InvalidOperationException("rangecheck (get)"); p.Push((double)s.B[i]); }
                else throw new InvalidOperationException("typecheck (get)");
            });
            Def("put", p =>
            {
                var v = p.Pop(); var k = p.Pop(); var c = p.Pop();
                if (c is PsArray a) { int i = (int)(double)k!; while (a.A.Count <= i) a.A.Add(PsNull.I); a.A[i] = v; }
                else if (c is PsDict d) d.D[p.Key(k)] = v;
                else if (c is PsString s) { int i = (int)(double)k!; if (i >= 0 && i < s.B.Length) s.B[i] = (byte)(double)v!; }
                else throw new InvalidOperationException("typecheck (put)");
            });
            Def("length", p => { var c = p.Pop(); p.Push((double)(c is PsArray a ? a.A.Count : c is PsDict d ? d.D.Count : c is PsString s ? s.B.Length : c is PsName n ? n.N.Length : 0)); });
            // ---- arrays / strings
            Def("array", p => { int n = p.Int(); var l = new List<object?>(n); for (int i = 0; i < n; i++) l.Add(PsNull.I); p.Push(new PsArray(l, false)); });
            Def("packedarray", p => { int n = p.Int(); var l = new List<object?>(n); for (int i = 0; i < n; i++) l.Add(null); for (int i = n - 1; i >= 0; i--) l[i] = p.Pop(); p.Push(new PsArray(l, false)); });
            Def("setpacking", p => p.Pop()); Def("currentpacking", p => p.Push(false));
            Def("aload", p => { var a = p.Arr(); foreach (var it in a.A) p.Push(it); p.Push(a); });
            Def("astore", p => { var a = p.Arr(); for (int i = a.A.Count - 1; i >= 0; i--) a.A[i] = p.Pop(); p.Push(a); });
            Def("getinterval", p =>
            {
                int n = p.Int(), i = p.Int(); var c = p.Pop();
                if (c is PsArray a) { if (i < 0 || n < 0 || i + n > a.A.Count) throw new InvalidOperationException("rangecheck (getinterval)"); p.Push(new PsArray(a.A.GetRange(i, n), a.Exec)); }
                else if (c is PsString s) { if (i < 0 || n < 0 || i + n > s.B.Length) throw new InvalidOperationException("rangecheck (getinterval)"); p.Push(new PsString(s.B.AsSpan(i, n).ToArray())); }
                else throw new InvalidOperationException("typecheck (getinterval)");
            });
            Def("putinterval", p =>
            {
                var src = p.Pop(); int i = p.Int(); var c = p.Pop();
                if (c is PsArray a && src is PsArray sa) { for (int k = 0; k < sa.A.Count; k++) { while (a.A.Count <= i + k) a.A.Add(PsNull.I); a.A[i + k] = sa.A[k]; } }
                else if (c is PsString s && src is PsString ss) { for (int k = 0; k < ss.B.Length && i + k < s.B.Length; k++) s.B[i + k] = ss.B[k]; }
            });
            Def("string", p => { int n = p.Int(); p.Push(new PsString(new byte[Math.Max(0, n)])); });
            Def("cvs", p =>
            {
                var s = (PsString)p.Pop()!; var v = p.Pop();
                string t = v switch { double d => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("0.######", CultureInfo.InvariantCulture), bool b => b ? "true" : "false", PsName n => n.N, PsString ps => ps.S, PsOp o => o.N, _ => "--nostringval--" };
                var bytes = Encoding.Latin1.GetBytes(t); int n2 = Math.Min(bytes.Length, s.B.Length); Array.Copy(bytes, s.B, n2); p.Push(new PsString(bytes.AsSpan(0, n2).ToArray()));
            });
            Def("cvn", p => { var v = p.Pop(); p.Push(v is PsString s ? new PsName(s.S, false) : v); });
            Def("cvx", p => { var v = p.Pop(); if (v is PsFile pf) { pf.Exec = true; p.Push(pf); return; } if (v is PsString xs) { p.Push(new PsString(xs.B) { Exec = true }); return; } p.Push(v is PsName n ? new PsName(n.N, true) : v is PsArray a ? new PsArray(a.A, true) : v); });
            Def("cvlit", p => { var v = p.Pop(); p.Push(v is PsName n ? new PsName(n.N, false) : v is PsArray a ? new PsArray(a.A, false) : v); });
            Def("cvrs", p => { var s = (PsString)p.Pop()!; int radix = p.Int(); long v = (long)p.Num(); string t = radix == 10 ? v.ToString(CultureInfo.InvariantCulture) : Convert.ToString(v, radix == 16 ? 16 : radix == 8 ? 8 : radix == 2 ? 2 : 10).ToUpperInvariant(); var b = Encoding.Latin1.GetBytes(t); int n = Math.Min(b.Length, s.B.Length); Array.Copy(b, s.B, n); p.Push(new PsString(b.AsSpan(0, n).ToArray())); });
            Def("search", p =>
            {
                var seek = (PsString)p.Pop()!; var s = (PsString)p.Pop()!; int i = s.S.IndexOf(seek.S, StringComparison.Ordinal);
                if (i < 0) { p.Push(s); p.Push(false); return; }
                p.Push(new PsString(s.B.AsSpan(i + seek.B.Length).ToArray())); p.Push(new PsString(s.B.AsSpan(i, seek.B.Length).ToArray())); p.Push(new PsString(s.B.AsSpan(0, i).ToArray())); p.Push(true);
            });
            Def("anchorsearch", p =>
            {
                var seek = (PsString)p.Pop()!; var s = (PsString)p.Pop()!;
                if (s.S.StartsWith(seek.S, StringComparison.Ordinal)) { p.Push(new PsString(s.B.AsSpan(seek.B.Length).ToArray())); p.Push(new PsString(s.B.AsSpan(0, seek.B.Length).ToArray())); p.Push(true); } else { p.Push(s); p.Push(false); }
            });
            Def("token", p =>
            {
                var src = p.Pop();
                if (src is PsString s) { var scn = new PsScanner(s.B, 0, s.B.Length); var t = scn.Next(); if (t == null) { p.Push(false); return; } p.Push(new PsString(s.B.AsSpan(Math.Min(scn.Pos, s.B.Length)).ToArray())); p.Push(t); p.Push(true); }
                else if (src is PsFile) { var t = p.sc.Next(); if (t == null) p.Push(false); else { p.Push(t); p.Push(true); } }
                else throw new InvalidOperationException("typecheck (token)");
            });
            // ---- types / attributes
            Def("type", p => { var v = p.Pop(); p.Push(new PsName(v switch { double d => d == Math.Floor(d) ? "integertype" : "realtype", bool => "booleantype", PsString => "stringtype", PsName => "nametype", PsArray => "arraytype", PsDict => "dicttype", PsOp => "operatortype", PsMark => "marktype", PsFile => "filetype", PsSave => "savetype", _ => "nulltype" }, true)); });
            Def("xcheck", p => { var v = p.Pop(); p.Push(v is PsArray a ? a.Exec : v is PsName n ? n.Exec : v is PsOp); });
            Def("rcheck", p => { p.Pop(); p.Push(true); }); Def("wcheck", p => { p.Pop(); p.Push(true); });
            Def("readonly", p => { }); Def("executeonly", p => { }); Def("noaccess", p => { });
            Def("bind", p => { });
            // ---- VM / misc
            Def("save", p => { p.Push(new PsSave { GsDepth = p.gsStack.Count }); p.GSave(); });
            Def("restore", p => { var v = p.Pop(); if (v is PsSave s) { while (p.gsStack.Count > s.GsDepth) p.GRestore(); } });
            Def("vmstatus", p => { p.Push(0.0); p.Push(1e7); p.Push(1e8); });
            Def("vmreclaim", p => p.Pop()); Def("setvmthreshold", p => p.Pop()); Def("setglobal", p => p.Pop()); Def("setshared", p => p.Pop()); Def("currentglobal", p => p.Push(false)); Def("currentshared", p => p.Push(false));
            Def("gcheck", p => { p.Pop(); p.Push(false); });
            Def("usertime", p => p.Push((double)Environment.TickCount)); Def("realtime", p => p.Push((double)Environment.TickCount));
            Def("flush", p => { }); Def("print", p => p.Pop()); Def("=", p => p.Pop()); Def("==", p => p.Pop()); Def("stack", p => { }); Def("pstack", p => { }); Def("echo", p => p.Pop());
            Def("currentfile", p => p.Push(new PsFile(0)));
            Def("closefile", p => p.Pop()); Def("flushfile", p => { var f = p.Pop() as PsFile; if (f != null && f.Kind == 1) p.SkipFilterData(f); }); Def("resetfile", p => p.Pop()); Def("status", p => { p.Pop(); p.Push(false); });
            Def("filter", p =>
            {   // <source> [params] /Name filter -> file. Only decoding filters on currentfile matter for us (to skip image data)
                var name = p.Key(p.Pop());
                object? src = p.Pop();
                if (src is PsDict) src = p.Pop();                                             // optional parameter dictionary (LL3)
                byte[]? eod = null;
                if (name == "SubFileDecode" && src is PsString es) { eod = es.B; src = p.Pop(); if (src is double) src = p.Pop(); }   // <src> count (eod) /SubFileDecode
                while (src is double || src is PsArray) src = p.Pop();                        // other filters' numeric / array parameters
                if (src is PsFile f) p.Push(f.Kind == 2 ? new PsFile(2, name, f.Data) { Inner = f.Filter != null ? f : null } : new PsFile(1, name) { Eod = eod, Inner = f.Filter != null ? f : null }); else p.Push(new PsFile(2, name, (src as PsString)?.B));
            });
            Def("readhexstring", p => { var s = (PsString)p.Pop()!; var f = p.Pop(); int n = p.sc.ReadHexBytes(s.B, s.B.Length); p.Push(new PsString(s.B.AsSpan(0, n).ToArray())); p.Push(n == s.B.Length); });
            Def("readstring", p => { var s = (PsString)p.Pop()!; var f = p.Pop(); int n = p.sc.ReadRawBytes(s.B, s.B.Length); p.Push(new PsString(s.B.AsSpan(0, n).ToArray())); p.Push(n == s.B.Length); });
            Def("readline", p => { var s = (PsString)p.Pop()!; p.Pop(); var l = p.sc.ReadLine(); if (l == null) { p.Push(new PsString("")); p.Push(false); return; } var b = Encoding.Latin1.GetBytes(l); int n = Math.Min(b.Length, s.B.Length); Array.Copy(b, s.B, n); p.Push(new PsString(b.AsSpan(0, n).ToArray())); p.Push(true); });
            Def("read", p => { p.Pop(); p.Push(false); });
            Def("run", p => { p.Pop(); p.Warn("run (external file) ignored"); });
            Def("findresource", p =>
            {
                var cat = p.Key(p.Pop()); var key = p.Pop();
                if (p.resources.TryGetValue(cat, out var store) && store.TryGetValue(p.Key(key), out var inst)) { p.Push(inst); return; }
                if (cat == "Category")
                {   // a category implementation dictionary: the generic resource operators as procedures (CoolType probes ResourceStatus through it)
                    var d = new PsDict();
                    d.D["ResourceStatus"] = new PsOp("ResourceStatus", q => { q.Pop(); q.Push(false); });
                    d.D["FindResource"] = new PsOp("FindResource", q => { q.Pop(); q.Push(new PsDict()); });
                    d.D["DefineResource"] = new PsOp("DefineResource", q => { var inst = q.Pop(); q.Pop(); q.Push(inst); });
                    d.D["UndefineResource"] = new PsOp("UndefineResource", q => q.Pop());
                    d.D["ResourceForAll"] = new PsOp("ResourceForAll", q => { q.Pop(); q.Pop(); q.Pop(); });
                    d.D["Category"] = new PsName(p.Key(key), false);
                    p.Push(d);
                }
                else if (cat == "ColorSpace") p.Push(new PsArray(new List<object?> { new PsName("DeviceRGB", false) }, false));
                else p.Push(new PsDict());
            });
            Def("defineresource", p => { var cat = p.Key(p.Pop()); var inst = p.Pop(); var key = p.Key(p.Pop()); if (!p.resources.TryGetValue(cat, out var st)) p.resources[cat] = st = new Dictionary<string, object?>(); st[key] = inst; p.Push(inst); });
            Def("resourcestatus", p => { var cat = p.Key(p.Pop()); var key = p.Key(p.Pop()); if (p.resources.TryGetValue(cat, out var st) && st.ContainsKey(key)) { p.Push(0.0); p.Push(0.0); p.Push(true); } else p.Push(false); });
            Def("resourceforall", p => { p.Pop(); p.Pop(); p.Pop(); p.Pop(); });
            Def("undefineresource", p => { var cat = p.Key(p.Pop()); var key = p.Key(p.Pop()); if (p.resources.TryGetValue(cat, out var st)) st.Remove(key); });
            Def("setpagedevice", p => p.Pop()); Def("currentpagedevice", p => p.Push(new PsDict()));
            Def("setuserparams", p => p.Pop()); Def("currentuserparams", p => p.Push(SysParams(false))); Def("setsystemparams", p => p.Pop()); Def("currentsystemparams", p => p.Push(SysParams(true)));
            Def("showpage", p => { }); Def("copypage", p => { }); Def("erasepage", p => { });
            Def("setdevparams", p => { p.Pop(); p.Pop(); }); Def("currentdevparams", p => { p.Pop(); p.Push(new PsDict()); });
            Def("setobjectformat", p => p.Pop()); Def("currentobjectformat", p => p.Push(0.0));
            Def("setucacheparams", p => { while (p.St.Count > 0 && !(p.St[^1] is PsMark)) p.Pop(); if (p.St.Count > 0) p.Pop(); });
            Def("ucachestatus", p => { p.Push(PsMark.I); p.Push(0.0); p.Push(0.0); p.Push(0.0); p.Push(0.0); p.Push(0.0); });
            Def("setcachelimit", p => p.Pop()); Def("setcacheparams", p => { while (p.St.Count > 0 && !(p.St[^1] is PsMark)) p.Pop(); if (p.St.Count > 0) p.Pop(); });
            Def("cachestatus", p => { for (int i = 0; i < 7; i++) p.Push(0.0); });
            Def("currentcacheparams", p => { p.Push(PsMark.I); p.Push(0.0); p.Push(0.0); p.Push(0.0); });
            Def("noop", p => { });
            // ---- graphics state
            Def("gsave", p => p.GSave()); Def("grestore", p => p.GRestore()); Def("grestoreall", p => { while (p.gsStack.Count > 0) p.GRestore(); });
            Def("initgraphics", p => { p.gs = new PsGState { Ctm = p.baseCtm }; });
            // LanguageLevel 3: clip stack (clipsave / cliprestore save only the clip; used by Illustrator's T1_gsave when level3)
            Def("clipsave", p => p.clipStack.Add(p.gs.Clip)); Def("cliprestore", p => { if (p.clipStack.Count > 0) { p.gs.Clip = p.clipStack[^1]; p.clipStack.RemoveAt(p.clipStack.Count - 1); } });
            Def("gstate", p => p.Push(p.gs.Clone())); Def("currentgstate", p => { p.Pop(); p.Push(p.gs.Clone()); }); Def("setgstate", p => { var v = p.Pop(); if (v is PsGState g) p.gs = g.Clone(); });
            Def("setlinewidth", p => p.gs.LineWidth = p.F()); Def("currentlinewidth", p => p.Push((double)p.gs.LineWidth));
            Def("setlinecap", p => p.gs.Cap = (VectorCap)Math.Clamp(p.Int(), 0, 2)); Def("currentlinecap", p => p.Push((double)(int)p.gs.Cap));
            Def("setlinejoin", p => p.gs.Join = (VectorJoin)Math.Clamp(p.Int(), 0, 2)); Def("currentlinejoin", p => p.Push((double)(int)p.gs.Join));
            Def("setmiterlimit", p => p.gs.Miter = p.F()); Def("currentmiterlimit", p => p.Push((double)p.gs.Miter));
            Def("setdash", p => { float off = p.F(); var a = p.Arr(); var l = new List<float>(); foreach (var v in a.A) if (v is double d) l.Add((float)d); bool any = false; foreach (var v in l) if (v > 0) any = true; p.gs.Dash = any ? l.ToArray() : null; p.gs.DashOffset = off; });
            Def("currentdash", p => { var l = new List<object?>(); if (p.gs.Dash != null) foreach (var v in p.gs.Dash) l.Add((double)v); p.Push(new PsArray(l, false)); p.Push((double)p.gs.DashOffset); });
            Def("setflat", p => p.gs.Flat = p.F()); Def("currentflat", p => p.Push((double)p.gs.Flat));
            Def("setstrokeadjust", p => p.Pop()); Def("currentstrokeadjust", p => p.Push(false));
            Def("setoverprint", p => p.Pop()); Def("currentoverprint", p => p.Push(false)); Def("setoverprintmode", p => p.Pop()); Def("currentoverprintmode", p => p.Push(0.0));
            Def("setsmoothness", p => p.Pop()); Def("currentsmoothness", p => p.Push(0.02));
            Def("setgray", p => { float g = p.F(); p.gs.ColorSpace = "DeviceGray"; p.gs.Ncomp = 1; p.SetColor(g, g, g); });
            Def("currentgray", p => p.Push((double)(0.3f * p.gs.R + 0.59f * p.gs.G + 0.11f * p.gs.B)));
            Def("setrgbcolor", p => { float b = p.F(), g = p.F(), r = p.F(); p.gs.ColorSpace = "DeviceRGB"; p.gs.Ncomp = 3; p.SetColor(r, g, b); });
            Def("currentrgbcolor", p => { p.Push((double)p.gs.R); p.Push((double)p.gs.G); p.Push((double)p.gs.B); });
            Def("sethsbcolor", p => { float v = p.F(), s = p.F(), h = p.F(); p.gs.ColorSpace = "DeviceRGB"; p.gs.Ncomp = 3; p.SetHsb(h, s, v); });
            Def("currenthsbcolor", p => { p.Push(0.0); p.Push(0.0); p.Push((double)Math.Max(p.gs.R, Math.Max(p.gs.G, p.gs.B))); });
            Def("setcmykcolor", p => { float k = p.F(), y = p.F(), m = p.F(), c = p.F(); p.gs.ColorSpace = "DeviceCMYK"; p.gs.Ncomp = 4; p.SetCmyk(c, m, y, k); });
            Def("currentcmykcolor", p => { p.Push((double)(1 - p.gs.R)); p.Push((double)(1 - p.gs.G)); p.Push((double)(1 - p.gs.B)); p.Push(0.0); });
            Def("setcolorspace", p => p.SetColorSpace(p.Pop()));
            Def("currentcolorspace", p => p.Push(p.gs.ColorSpaceArr ?? (object)new PsArray(new List<object?> { new PsName(p.gs.ColorSpace, false) }, false)));
            Def("setcolor", p => p.SetColorOp());
            Def("currentcolor", p => { switch (p.gs.Ncomp) { case 4: p.Push((double)(1 - p.gs.R)); p.Push((double)(1 - p.gs.G)); p.Push((double)(1 - p.gs.B)); p.Push(0.0); break; case 3: p.Push((double)p.gs.R); p.Push((double)p.gs.G); p.Push((double)p.gs.B); break; default: p.Push((double)p.gs.R); break; } });
            Def("setpattern", p => { p.Pop(); p.Warn("pattern paint approximated by 50 % grey"); p.SetColor(0.5f, 0.5f, 0.5f); });
            Def("makepattern", p => { var m = p.Arr(); var d = p.Pop(); p.Push(d); });
            Def("sethalftone", p => p.Pop()); Def("currenthalftone", p => { var h = new PsDict(); h.D["HalftoneType"] = 1.0; h.D["Frequency"] = 60.0; h.D["Angle"] = 45.0; h.D["SpotFunction"] = new PsArray(new List<object?>(), true); p.Push(h); });
            Def("setscreen", p => { p.Pop(); p.Pop(); p.Pop(); }); Def("currentscreen", p => { p.Push(60.0); p.Push(45.0); p.Push(new PsArray(new List<object?>(), true)); });
            Def("setcolorscreen", p => { for (int i = 0; i < 12; i++) p.Pop(); }); Def("currentcolorscreen", p => { for (int i = 0; i < 4; i++) { p.Push(60.0); p.Push(45.0); p.Push(new PsArray(new List<object?>(), true)); } });
            Def("settransfer", p => p.Pop()); Def("currenttransfer", p => p.Push(new PsArray(new List<object?>(), true)));
            Def("setcolortransfer", p => { for (int i = 0; i < 4; i++) p.Pop(); }); Def("currentcolortransfer", p => { for (int i = 0; i < 4; i++) p.Push(new PsArray(new List<object?>(), true)); });
            Def("setblackgeneration", p => p.Pop()); Def("currentblackgeneration", p => p.Push(new PsArray(new List<object?>(), true)));
            Def("setundercolorremoval", p => p.Pop()); Def("currentundercolorremoval", p => p.Push(new PsArray(new List<object?>(), true)));
            Def("setcolorrendering", p => p.Pop()); Def("currentcolorrendering", p => p.Push(new PsDict()));
            Def("nulldevice", p => { });
            // ---- CTM
            Def("matrix", p => p.Push(FromMatrix(Matrix3x2.Identity)));
            Def("identmatrix", p => p.Push(FromMatrix(Matrix3x2.Identity, p.Arr())));
            Def("defaultmatrix", p => p.Push(FromMatrix(p.baseCtm, p.Arr())));
            Def("initmatrix", p => p.gs.Ctm = p.baseCtm);
            Def("currentmatrix", p => p.Push(FromMatrix(p.gs.Ctm, p.Arr())));
            Def("setmatrix", p => p.gs.Ctm = ToMatrix(p.Arr()));
            Def("concat", p => p.gs.Ctm = ToMatrix(p.Arr()) * p.gs.Ctm);
            Def("concatmatrix", p => { var r = p.Arr(); var b = ToMatrix(p.Arr()); var a = ToMatrix(p.Arr()); p.Push(FromMatrix(a * b, r)); });
            Def("invertmatrix", p => { var r = p.Arr(); var a = ToMatrix(p.Arr()); Matrix3x2.Invert(a, out var inv); p.Push(FromMatrix(inv, r)); });
            Def("translate", p => { if (p.St.Count > 0 && p.St[^1] is PsArray) { var r = p.Arr(); float y = p.F(), x = p.F(); p.Push(FromMatrix(Matrix3x2.CreateTranslation(x, y), r)); } else { float y = p.F(), x = p.F(); p.gs.Ctm = Matrix3x2.CreateTranslation(x, y) * p.gs.Ctm; } });
            Def("scale", p => { if (p.St.Count > 0 && p.St[^1] is PsArray) { var r = p.Arr(); float y = p.F(), x = p.F(); p.Push(FromMatrix(Matrix3x2.CreateScale(x, y), r)); } else { float y = p.F(), x = p.F(); p.gs.Ctm = Matrix3x2.CreateScale(x, y) * p.gs.Ctm; } });
            Def("rotate", p => { if (p.St.Count > 0 && p.St[^1] is PsArray) { var r = p.Arr(); float a = p.F(); p.Push(FromMatrix(Matrix3x2.CreateRotation(a * MathF.PI / 180), r)); } else { float a = p.F(); p.gs.Ctm = Matrix3x2.CreateRotation(a * MathF.PI / 180) * p.gs.Ctm; } });
            Def("transform", p => { Matrix3x2 m = p.gs.Ctm; if (p.St.Count > 0 && p.St[^1] is PsArray) m = ToMatrix(p.Arr()); float y = p.F(), x = p.F(); var v = Vector2.Transform(new Vector2(x, y), m); p.Push((double)v.X); p.Push((double)v.Y); });
            Def("itransform", p => { Matrix3x2 m = p.gs.Ctm; if (p.St.Count > 0 && p.St[^1] is PsArray) m = ToMatrix(p.Arr()); float y = p.F(), x = p.F(); Matrix3x2.Invert(m, out var inv); var v = Vector2.Transform(new Vector2(x, y), inv); p.Push((double)v.X); p.Push((double)v.Y); });
            Def("dtransform", p => { Matrix3x2 m = p.gs.Ctm; if (p.St.Count > 0 && p.St[^1] is PsArray) m = ToMatrix(p.Arr()); float y = p.F(), x = p.F(); var v = Vector2.TransformNormal(new Vector2(x, y), m); p.Push((double)v.X); p.Push((double)v.Y); });
            Def("idtransform", p => { Matrix3x2 m = p.gs.Ctm; if (p.St.Count > 0 && p.St[^1] is PsArray) m = ToMatrix(p.Arr()); float y = p.F(), x = p.F(); Matrix3x2.Invert(m, out var inv); var v = Vector2.TransformNormal(new Vector2(x, y), inv); p.Push((double)v.X); p.Push((double)v.Y); });
            // ---- paths
            Def("newpath", p => p.NewPath());
            Def("moveto", p => { float y = p.F(), x = p.F(); p.MoveTo(x, y); });
            Def("rmoveto", p => { float dy = p.F(), dx = p.F(); var c = p.CurrentUser(); p.MoveTo(c.X + dx, c.Y + dy); });
            Def("lineto", p => { float y = p.F(), x = p.F(); p.LineTo(x, y); });
            Def("rlineto", p => { float dy = p.F(), dx = p.F(); var c = p.CurrentUser(); p.LineTo(c.X + dx, c.Y + dy); });
            Def("curveto", p => { float y3 = p.F(), x3 = p.F(), y2 = p.F(), x2 = p.F(), y1 = p.F(), x1 = p.F(); p.CurveTo(x1, y1, x2, y2, x3, y3); });
            Def("rcurveto", p => { float y3 = p.F(), x3 = p.F(), y2 = p.F(), x2 = p.F(), y1 = p.F(), x1 = p.F(); var c = p.CurrentUser(); p.CurveTo(c.X + x1, c.Y + y1, c.X + x2, c.Y + y2, c.X + x3, c.Y + y3); });
            Def("arc", p => { float a2 = p.F(), a1 = p.F(), r = p.F(), y = p.F(), x = p.F(); p.Arc(x, y, r, a1, a2, true); });
            Def("arcn", p => { float a2 = p.F(), a1 = p.F(), r = p.F(), y = p.F(), x = p.F(); p.Arc(x, y, r, a1, a2, false); });
            Def("arct", p => { float r = p.F(), y2 = p.F(), x2 = p.F(), y1 = p.F(), x1 = p.F(); p.ArcT(x1, y1, x2, y2, r, false); });
            Def("arcto", p => { float r = p.F(), y2 = p.F(), x2 = p.F(), y1 = p.F(), x1 = p.F(); p.ArcT(x1, y1, x2, y2, r, true); });
            Def("closepath", p => { if (p.gs.HasCurrent) { p.gs.Path.Close(); var s = p.gs.Path.SubPathStart; p.gs.DevCx = s.X; p.gs.DevCy = s.Y; } });
            Def("currentpoint", p => { var c = p.CurrentUser(); p.Push((double)c.X); p.Push((double)c.Y); });
            Def("flattenpath", p => { }); Def("reversepath", p => { });
            Def("strokepath", p => { p.Warn("strokepath approximated by the path outline"); });
            Def("pathbbox", p =>
            {
                var b = p.gs.Path.ControlBounds(); if (p.gs.Path.IsEmpty) throw new InvalidOperationException("nocurrentpoint");
                var a = p.ToUser(b.Left, b.Top); var c = p.ToUser(b.Right, b.Bottom);
                p.Push((double)Math.Min(a.X, c.X)); p.Push((double)Math.Min(a.Y, c.Y)); p.Push((double)Math.Max(a.X, c.X)); p.Push((double)Math.Max(a.Y, c.Y));
            });
            Def("setbbox", p => { p.Pop(); p.Pop(); p.Pop(); p.Pop(); });
            Def("clippath", p => { p.NewPath(); var r = p.gs.Clip != null && p.gs.Clip.IsRect(out var cr) ? cr : new RectangleF(0, 0, p.img.Width, p.img.Height); p.gs.Path.Rect(r.X, r.Y, r.Width, r.Height); p.gs.HasCurrent = true; p.gs.DevCx = r.X; p.gs.DevCy = r.Y; });
            Def("upath", p => { p.Pop(); p.Push(new PsArray(new List<object?>(), true)); });
            Def("uappend", p => p.Pop()); Def("ucache", p => { });
            Def("ufill", p => { p.Pop(); }); Def("ueofill", p => { p.Pop(); }); Def("ustroke", p => { p.Pop(); }); Def("ustrokepath", p => p.Pop());
            Def("pathforall", p => { p.Pop(); p.Pop(); p.Pop(); p.Pop(); p.Warn("pathforall is not supported"); });
            // ---- painting
            Def("fill", p => p.Fill(false)); Def("eofill", p => p.Fill(true)); Def("stroke", p => p.Stroke());
            Def("rectfill", p => { p.RectOp(0); }); Def("rectstroke", p => { p.RectOp(1); }); Def("rectclip", p => { p.RectOp(2); });
            Def("clip", p => p.Clip(false)); Def("eoclip", p => p.Clip(true)); Def("initclip", p => p.gs.Clip = null);
            Def("shfill", p => { var d = p.Pop(); p.ShadingFill(d as PsDict); });
            Def("image", p => p.ImageOp(0)); Def("imagemask", p => p.ImageOp(1)); Def("colorimage", p => p.ImageOp(2));
            Def("execform", p => { var d = p.Pop(); if (d is PsDict fd && fd.D.TryGetValue("PaintProc", out var pp)) { var m = fd.D.TryGetValue("Matrix", out var mm) && mm is PsArray ma ? ToMatrix(ma) : Matrix3x2.Identity; p.GSave(); p.gs.Ctm = m * p.gs.Ctm; p.Push(fd); try { p.Exec(pp); } finally { p.GRestore(); } } });
            // ---- fonts / text (not rendered)
            Def("findfont", p => { p.Pop(); p.Push(FakeFont()); }); Def("scalefont", p => { p.Pop(); }); Def("makefont", p => { p.Pop(); }); Def("setfont", p => p.Pop()); Def("currentfont", p => p.Push(FakeFont())); Def("rootfont", p => p.Push(FakeFont()));
            Def("selectfont", p => { p.Pop(); p.Pop(); }); Def("definefont", p => { var f = p.Pop(); p.Pop(); p.Push(f); }); Def("undefinefont", p => p.Pop());
            Def("show", p => { p.Pop(); p.TextWarn(); }); Def("ashow", p => { p.Pop(); p.Pop(); p.Pop(); p.TextWarn(); }); Def("widthshow", p => { p.Pop(); p.Pop(); p.Pop(); p.Pop(); p.TextWarn(); });
            Def("awidthshow", p => { for (int i = 0; i < 6; i++) p.Pop(); p.TextWarn(); }); Def("kshow", p => { p.Pop(); p.Pop(); p.TextWarn(); });
            Def("xshow", p => { p.Pop(); p.Pop(); p.TextWarn(); }); Def("yshow", p => { p.Pop(); p.Pop(); p.TextWarn(); }); Def("xyshow", p => { p.Pop(); p.Pop(); p.TextWarn(); }); Def("cshow", p => { p.Pop(); p.Pop(); p.TextWarn(); });
            Def("glyphshow", p => { p.Pop(); p.TextWarn(); });
            Def("stringwidth", p => { p.Pop(); p.Push(0.0); p.Push(0.0); });
            Def("charpath", p => { p.Pop(); p.Pop(); p.TextWarn(); });
            Def("setcachedevice", p => { for (int i = 0; i < 6; i++) p.Pop(); }); Def("setcharwidth", p => { p.Pop(); p.Pop(); });
            Def("setcachedevice2", p => { for (int i = 0; i < 10; i++) p.Pop(); });
            Def("ISOLatin1Encoding", p => p.Push(new PsArray(new List<object?>(), false))); Def("StandardEncoding", p => p.Push(new PsArray(new List<object?>(), false)));
            Def("internaldict", p => { p.Pop(); p.Push(new PsDict()); });
            Def("deletefile", p => p.Pop()); Def("renamefile", p => { p.Pop(); p.Pop(); }); Def("file", p => { p.Pop(); p.Pop(); p.Push(new PsFile(2)); });
            Def("version", p => p.Push(new PsString("3010")));
            // languagelevel is a plain integer in systemdict (set above): "systemdict/languagelevel get 2 ge" must see a number, not an operator
            Def("product", p => p.Push(new PsString("SR2D")));
        }
        static PsDict FakeFont() { var d = new PsDict(); d.D["FontMatrix"] = FromMatrix(new Matrix3x2(0.001f, 0, 0, 0.001f, 0, 0)); d.D["FontType"] = 1.0; d.D["FontName"] = new PsName("Helvetica", false); d.D["Encoding"] = new PsArray(new List<object?>(), false); d.D["FontBBox"] = new PsArray(new List<object?> { 0.0, 0.0, 1000.0, 1000.0 }, false); return d; }
        /// <summary>Consumes the rest of a filtered currentfile: SubFileDecode up to its EOD string, ASCII85 / hex to their end markers.</summary>
        void SkipFilterData(PsFile f)
        {
            if (f.Filter == "SubFileDecode") { if (f.Eod != null && f.Eod.Length > 0) sc.SkipPast(f.Eod); }
            else if (f.Filter == "ASCII85Decode") sc.SkipA85ToEod();
            else if (f.Filter == "ASCIIHexDecode") sc.SkipPast(new[] { (byte)'>' });
        }
        void TextWarn() => Warn("text (show) is not imported - convert text to outlines before export");
        static bool Eq(object? a, object? b)
        {
            if (a is double x && b is double y) return x == y;
            if (a is bool p && b is bool q) return p == q;
            if (a is PsName na && b is PsName nb) return na.N == nb.N;
            if (a is PsString sa && b is PsString sb) return sa.S == sb.S;
            if (a is PsName n1 && b is PsString s1) return n1.N == s1.S;
            if (a is PsString s2 && b is PsName n2) return s2.S == n2.N;
            return ReferenceEquals(a, b) || (a is PsNull && b is PsNull) || (a is PsMark && b is PsMark);
        }
        /// <summary>A plausible system / user parameter dictionary (prologs read cache sizes and limits from it).</summary>
        static PsDict SysParams(bool system)
        {
            var d = new PsDict();
            if (system)
            {
                foreach (var (k, v) in new (string, double)[] { ("MaxPatternCache", 4000000), ("MaxFontCache", 4000000), ("MaxFormCache", 1000000), ("MaxScreenStorage", 500000), ("MaxUPathCache", 300000), ("MaxDisplayList", 4000000), ("MaxImageBuffer", 4000000), ("MaxOutlineCache", 65536), ("CurUPathCache", 0), ("CurFontCache", 0), ("CurFormCache", 0), ("CurPatternCache", 0), ("CurScreenStorage", 0), ("CurDisplayList", 0), ("CurOutlineCache", 0), ("CurSourceList", 0), ("MaxSourceList", 25), ("PageCount", 0), ("MaxInlineImage", 1000000), ("MaxDictStack", 1000000), ("MaxExecStack", 1000000), ("MaxOpStack", 1000000), ("MaxLocalVM", 100000000), ("MaxGlobalVM", 100000000) })
                    d.D[k] = v;
                d.D["ByteOrder"] = false; d.D["RealFormat"] = new PsString("IEEE"); d.D["LicenseID"] = new PsString(""); d.D["PrinterName"] = new PsString("SR2D"); d.D["Revision"] = 0.0;
            }
            else
            {
                foreach (var (k, v) in new (string, double)[] { ("MaxFontItem", 12500), ("MinFontCompress", 1250), ("MaxUPathItem", 5000), ("MaxFormItem", 100000), ("MaxPatternItem", 20000), ("MaxScreenItem", 48000), ("MaxOpStack", 5000), ("MaxDictStack", 530), ("MaxExecStack", 1500), ("MaxLocalVM", 100000000), ("VMReclaim", 0), ("VMThreshold", 200000), ("MaxSuperScreen", 1016), ("HalftoneMode", 0), ("AccurateScreens", 0), ("IdiomRecognition", 0), ("MaxDisplayAndSourceList", 4000000) })
                    d.D[k] = v;
                d.D["JobName"] = new PsString(""); d.D["JobTimeout"] = 0.0; d.D["WaitTimeout"] = 0.0;
            }
            return d;
        }
        static int Cmp(PsInterp p)
        {
            var b = p.Pop(); var a = p.Pop();
            if (a is double x && b is double y) return x.CompareTo(y);
            if (a is PsString sa && b is PsString sb) return string.CompareOrdinal(sa.S, sb.S);
            throw new InvalidOperationException("typecheck (compare)");
        }
        readonly List<VectorClip?> clipStack = new List<VectorClip?>();
        /// <summary>Named resource instances: category -> key -> object (defineresource / findresource; Illustrator keeps colour spaces and gradients here).</summary>
        readonly Dictionary<string, Dictionary<string, object?>> resources = new Dictionary<string, Dictionary<string, object?>>();
        void GSave() { gsStack.Add(gs.Clone()); }
        void GRestore() { if (gsStack.Count > 0) { var path = gs.Path; gs = gsStack[^1]; gsStack.RemoveAt(gsStack.Count - 1); } }
        void RectOp(int kind)
        {
            // x y w h  |  numarray  |  numstring (ignored)
            var saved = gs.Path; bool savedHas = gs.HasCurrent; float sdx = gs.DevCx, sdy = gs.DevCy;
            gs.Path = new VectorPath(); gs.HasCurrent = false;
            var top = Pop();
            if (top is PsArray a) { for (int i = 0; i + 3 < a.A.Count; i += 4) RectPath((float)(double)a.A[i]!, (float)(double)a.A[i + 1]!, (float)(double)a.A[i + 2]!, (float)(double)a.A[i + 3]!); }
            else if (top is double h) { float w = F(), y = F(), x = F(); RectPath(x, y, w, (float)h); }
            switch (kind)
            {
                case 0: Fill(false); break;
                case 1: Stroke(); break;
                case 2: Clip(false); break;
            }
            gs.Path = saved; gs.HasCurrent = savedHas; gs.DevCx = sdx; gs.DevCy = sdy;
        }
        void RectPath(float x, float y, float w, float h) { MoveTo(x, y); LineTo(x + w, y); LineTo(x + w, y + h); LineTo(x, y + h); gs.Path.Close(); }
        /// <summary>PostScript function dictionary (types 0 sampled with DataSource string, 2 exponential, 3 stitching, 4 PostScript calculator procedure; arrays of 1-out functions) -> t -> outputs.</summary>
        Func<double, double[]>? PsFunction(object? fo)
        {
            if (fo is object?[] arr)
            {
                var fns = new List<Func<double, double[]>>(); foreach (var e in arr) { var f = PsFunction(e); if (f != null) fns.Add(f); }
                if (fns.Count == 0) return null;
                return t => { var o = new double[fns.Count]; for (int i = 0; i < fns.Count; i++) { var r = fns[i](t); o[i] = r.Length > 0 ? r[0] : 0; } return o; };
            }
            var get = fo as Func<string, object?>; if (get == null) return null;
            double[]? Arr(string k) { if (!(get(k) is object?[] a)) return null; var r = new double[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = a[i] as double? ?? 0; return r; }
            int type = (int)(get("FunctionType") as double? ?? -1);
            double[] dom = Arr("Domain") ?? new double[] { 0, 1 }; double[]? range = Arr("Range");
            static double Cl(double v, double a, double b) => a <= b ? Math.Clamp(v, a, b) : Math.Clamp(v, b, a);   // damaged files: inverted Domain / Range must not throw
            double ClampD(double x) => dom.Length > 1 ? Cl(x, dom[0], dom[1]) : x;
            double[] ClampR(double[] y) { if (range == null) return y; for (int i = 0; i < y.Length && 2 * i + 1 < range.Length; i++) y[i] = Cl(y[i], range[2 * i], range[2 * i + 1]); return y; }
            switch (type)
            {
                case 2:
                {
                    double[] c0 = Arr("C0") ?? new double[] { 0 }, c1 = Arr("C1") ?? new double[] { 1 }; double n = get("N") as double? ?? 1;
                    return t => { t = ClampD(t); double tn = n == 1 ? t : Math.Pow(Math.Max(0, t), n); var o = new double[Math.Max(c0.Length, c1.Length)]; for (int i = 0; i < o.Length; i++) { double a = i < c0.Length ? c0[i] : 0, b = i < c1.Length ? c1[i] : 0; o[i] = a + tn * (b - a); } return ClampR(o); };
                }
                case 3:
                {
                    var funcs = new List<Func<double, double[]>?>(); if (get("Functions") is object?[] fa) foreach (var f in fa) funcs.Add(PsFunction(f));
                    double[] bounds = Arr("Bounds") ?? Array.Empty<double>(), enc = Arr("Encode") ?? Array.Empty<double>();
                    if (funcs.Count == 0) return null;
                    return t =>
                    {
                        t = ClampD(t); int k = 0; while (k < bounds.Length && t >= bounds[k]) k++; k = Math.Min(k, funcs.Count - 1);
                        double lo = k == 0 ? dom[0] : bounds[k - 1], hi = k == bounds.Length ? dom[1] : bounds[k];
                        double e0 = enc.Length > 2 * k + 1 ? enc[2 * k] : 0, e1 = enc.Length > 2 * k + 1 ? enc[2 * k + 1] : 1;
                        double tt = hi > lo ? e0 + (t - lo) / (hi - lo) * (e1 - e0) : e0;
                        var f = funcs[k]; return f == null ? new double[] { 0 } : ClampR(f(tt));
                    };
                }
                case 0:
                {
                    var data = get("DataSource") as byte[]; if (data == null) return null;
                    double[] size = Arr("Size") ?? new double[] { 2 }; int bps = (int)(get("BitsPerSample") as double? ?? 8);
                    int nOut = range != null ? range.Length / 2 : 1; int n0 = size.Length > 0 && size[0] >= 1 && size[0] <= 1 << 24 ? (int)size[0] : 0;
                    if (n0 == 0 || nOut <= 0 || dom.Length < 2 || !(bps is 1 or 2 or 4 or 8 or 12 or 16 or 24 or 32)) return null;   // damaged function dictionary
                    double[] enc = Arr("Encode") ?? new double[] { 0, n0 - 1 }, dec = Arr("Decode") ?? range ?? new double[] { 0, 1 };
                    if (enc.Length < 2) enc = new double[] { 0, n0 - 1 };
                    double max = Math.Pow(2, bps) - 1;
                    double Sample(long idx, int j)
                    {
                        long bit = (idx * nOut + j) * bps; long bi = bit >> 3; if (bi >= data.Length) return 0;
                        long v = 0; int need = bps, off = (int)(bit & 7);
                        while (need > 0 && bi < data.Length) { int avail = 8 - off, take = Math.Min(avail, need); int bits = (data[bi] >> (avail - take)) & ((1 << take) - 1); v = (v << take) | (uint)bits; need -= take; off += take; if (off == 8) { off = 0; bi++; } }
                        return v / max;
                    }
                    return t =>
                    {
                        t = ClampD(t);
                        double e = dom[1] > dom[0] ? enc[0] + (t - dom[0]) / (dom[1] - dom[0]) * (enc[1] - enc[0]) : enc[0];
                        e = Math.Clamp(e, 0, n0 - 1); long i0 = (long)Math.Floor(e), i1 = Math.Min(i0 + 1, n0 - 1); double fr = e - i0;
                        var o = new double[nOut];
                        for (int j = 0; j < nOut; j++) { double s0 = Sample(i0, j), s1 = Sample(i1, j), sv = s0 + (s1 - s0) * fr; double d0 = dec.Length > 2 * j + 1 ? dec[2 * j] : 0, d1 = dec.Length > 2 * j + 1 ? dec[2 * j + 1] : 1; o[j] = d0 + sv * (d1 - d0); }
                        return ClampR(o);
                    };
                }
                case 4:
                {
                    var body = get("__proc") as PsArray; if (body == null) return null; int nOut = range != null ? range.Length / 2 : 1;
                    return t => { var st = new List<double> { ClampD(t) }; try { PsCalc.Exec(body, st, 0); } catch (Exception e) { ImportLog.Swallowed(e, "PostScript type 4 function"); } var o = new double[nOut]; for (int i = 0; i < nOut; i++) { int si = st.Count - nOut + i; o[i] = si >= 0 && si < st.Count ? st[si] : 0; } return ClampR(o); };
                }
            }
            return null;
        }
        void ShadingFill(PsDict? sh)
        {
            if (sh == null) return;
            var g = PdfShading.FromDict(k => sh.D.TryGetValue(k, out var v) ? Conv(v) : null, Warn, PsFunction);
            if (g == null) { Warn("shading type not supported (skipped)"); return; }
            // paint the clip (or the whole page) with the gradient
            var p = new VectorPath();
            if (gs.Clip != null) { var (cp, eo) = gs.Clip.Paths[^1]; p = cp.Clone(); }
            else p.Rect(0, 0, img.Width, img.Height);
            g.Matrix = gs.Ctm;   // shading space = current user space
            var s = new VectorShape { Path = p, Fill = g, Clip = gs.Clip };
            img.Shapes.Add(s);
            static object? Conv(object? v) => v switch { PsArray a when a.Exec => a, PsArray a => ConvArr(a), PsName n => n.N, PsDict d => new Func<string, object?>(k => k == "__proc" ? (d.D.TryGetValue("__proc", out var pr) ? pr : null) : d.D.TryGetValue(k, out var x) ? Conv(x) : null), PsString s => s.B, _ => v };
            static object?[] ConvArr(PsArray a) { var r = new object?[a.A.Count]; for (int i = 0; i < r.Length; i++) r[i] = Conv(a.A[i]); return r; }
        }
    }

    /// <summary>Shading dictionary (PostScript shfill / PDF sh, types 2 and 3; type 1 and mesh types are reported) -> gradient. Shared by both importers.</summary>
    internal static class PdfShading
    {
        /// <summary><paramref name="get"/> returns dictionary entries as: double, string (name), object?[] (array), byte[] (string), Func&lt;string, object?&gt; (sub-dictionary), or a function object (Func&lt;double, double[]&gt;) for "Function".</summary>
        public static VectorGradient? FromDict(Func<string, object?> get, Action<string> warn, Func<object?, Func<double, double[]>?>? funcOf = null)
        {
            int type = (int)(get("ShadingType") as double? ?? 0);
            if (type != 2 && type != 3) { warn(type == 1 ? "function-based shading approximated by its average colour" : $"mesh shading (type {type}) is not supported"); return null; }
            var coords = get("Coords") as object?[]; if (coords == null) return null;
            double C(int i) => coords.Length > i && coords[i] is double d ? d : 0;
            var g = new VectorGradient { Radial = type == 3, Spread = VectorSpread.Pad };
            if (type == 2) { g.X1 = (float)C(0); g.Y1 = (float)C(1); g.X2 = (float)C(2); g.Y2 = (float)C(3); }
            else { g.Fx = (float)C(0); g.Fy = (float)C(1); g.Fr = (float)C(2); g.Cx = (float)C(3); g.Cy = (float)C(4); g.R = (float)C(5); }
            var dom = get("Domain") as object?[]; double t0 = dom != null && dom.Length > 1 && dom[0] is double a ? a : 0, t1 = dom != null && dom.Length > 1 && dom[1] is double b ? b : 1;
            var ext = get("Extend") as object?[]; g.ExtendStart = ext != null && ext.Length > 0 && ext[0] is bool e0 && e0; g.ExtendEnd = ext != null && ext.Length > 1 && ext[1] is bool e1 && e1;
            string cs = get("ColorSpace") as string ?? (get("ColorSpace") is object?[] csa && csa.Length > 0 ? csa[0] as string ?? "DeviceRGB" : "DeviceRGB");
            var fn = funcOf?.Invoke(get("Function"));
            if (fn == null)
            {   // no evaluable function: try the Background / a grey ramp
                g.Stops.Add(new VectorStop(0, unchecked((int)0xFF000000))); g.Stops.Add(new VectorStop(1, unchecked((int)0xFFFFFFFF)));
                if (get("Function") != null) warn("shading function could not be evaluated - grey ramp used");
                return g;
            }
            const int N = 32;
            for (int i = 0; i <= N; i++)
            {
                double t = t0 + (t1 - t0) * i / N; var c = fn(t);
                g.Stops.Add(new VectorStop((float)i / N, ToArgb(cs, c)));
            }
            return g;
        }
        public static int ToArgb(string cs, double[] c)
        {
            int B(double v) => Math.Clamp((int)Math.Round(v * 255), 0, 255);
            switch (cs)
            {
                case "DeviceCMYK": if (c.Length >= 4) return unchecked((int)0xFF000000) | B((1 - c[0]) * (1 - c[3])) << 16 | B((1 - c[1]) * (1 - c[3])) << 8 | B((1 - c[2]) * (1 - c[3])); break;
                case "DeviceGray": case "CalGray": if (c.Length >= 1) return unchecked((int)0xFF000000) | B(c[0]) << 16 | B(c[0]) << 8 | B(c[0]); break;
            }
            if (c.Length >= 3) return unchecked((int)0xFF000000) | B(c[0]) << 16 | B(c[1]) << 8 | B(c[2]);
            if (c.Length >= 1) return unchecked((int)0xFF000000) | B(c[0]) << 16 | B(c[0]) << 8 | B(c[0]);
            return unchecked((int)0xFF808080);
        }
    }
}
