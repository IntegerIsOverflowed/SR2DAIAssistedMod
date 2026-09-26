using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Text;

namespace Sr2d64CSport
{
    /// <summary>
    /// A CMap (code bytes -> CID, or code -> Unicode for ToUnicode): codespace ranges by byte length, single mappings and
    /// ranges. Identity-H / V is the built-in 2-byte identity. Embedded CMap streams are parsed; other predefined CMaps
    /// (the CJK collections) are not available and fall back to 2-byte identity.
    /// </summary>
    internal sealed class PdfCMap
    {
        struct Range { public int NBytes, Lo, Hi, Dst; }
        readonly List<(int nbytes, int lo, int hi)> codespace = new List<(int, int, int)>();
        readonly Dictionary<int, int> single = new Dictionary<int, int>();
        readonly List<Range> ranges = new List<Range>();
        readonly Dictionary<int, string> uni = new Dictionary<int, string>();
        readonly List<(int lo, int hi, int nbytes, string dst)> uniRanges = new List<(int, int, int, string)>();
        public bool Identity, Vertical;
        public static PdfCMap IdentityH() { var c = new PdfCMap { Identity = true }; c.codespace.Add((2, 0, 0xFFFF)); return c; }

        public static PdfCMap Parse(byte[] data)
        {
            var c = new PdfCMap(); var lx = new PdfLexer(data); var st = new List<object?>();
            while (true)
            {
                object? t; try { t = lx.Token(); } catch (Exception e) { ImportLog.Swallowed(e, "PDF CMap lexer"); break; }
                if (t == null) break;
                if (t is PdfOp op)
                {
                    switch (op.N)
                    {
                        case "[": st.Add(lx.ObjectFrom(t, null, false)); continue;
                        case "begincodespacerange":
                            while (true) { var a = lx.Token(); if (!(a is byte[] lo)) break; var b = lx.Token() as byte[]; if (b == null) break; c.codespace.Add((lo.Length, Int(lo), Int(b))); }
                            break;
                        case "begincidrange":
                            while (true) { var a = lx.Token(); if (!(a is byte[] lo)) break; var b = lx.Token() as byte[]; var d = lx.Token(); if (b == null || !(d is double dd)) break; c.ranges.Add(new Range { NBytes = lo.Length, Lo = Int(lo), Hi = Int(b), Dst = (int)dd }); c.Ensure(lo.Length); }
                            break;
                        case "begincidchar":
                            while (true) { var a = lx.Token(); if (!(a is byte[] code)) break; var d = lx.Token(); if (!(d is double dd)) break; c.single[Int(code)] = (int)dd; c.Ensure(code.Length); }
                            break;
                        case "beginbfchar":
                            while (true) { var a = lx.Token(); if (!(a is byte[] code)) break; var d = lx.Token(); string? s = d is byte[] db ? Utf16(db) : d is string nm ? GlyphStr(nm) : null; if (s == null) break; c.uni[Int(code)] = s; c.Ensure(code.Length); }
                            break;
                        case "beginbfrange":
                            while (true)
                            {
                                var a = lx.Token(); if (!(a is byte[] lo)) break; var b = lx.Token() as byte[]; if (b == null) break; var d = lx.Token();
                                if (d is PdfOp dop && dop.N == "[") d = lx.ObjectFrom(d, null, false);
                                int l = Int(lo), h = Int(b); c.Ensure(lo.Length);
                                if (d is byte[] db) c.uniRanges.Add((l, h, lo.Length, Utf16(db)));
                                else if (d is object?[] arr) { for (int i = 0; i < arr.Length && l + i <= h; i++) if (arr[i] is byte[] e) c.uni[l + i] = Utf16(e); }
                                else break;
                            }
                            break;
                        case "usecmap": if (st.Count > 0 && st[^1] is string un && un.StartsWith("Identity", StringComparison.Ordinal)) { c.Identity = true; c.codespace.Add((2, 0, 0xFFFF)); } break;
                        case "endcmap": break;
                    }
                    st.Clear();
                }
                else { st.Add(t); if (st.Count > 32) st.RemoveAt(0); }
            }
            if (c.codespace.Count == 0) c.codespace.Add((c.ranges.Count > 0 || c.single.Count > 0 ? 2 : 1, 0, 0xFFFF));
            return c;
        }
        void Ensure(int nbytes) { foreach (var cs in codespace) if (cs.nbytes == nbytes) return; codespace.Add((nbytes, 0, nbytes >= 4 ? int.MaxValue : (1 << (8 * nbytes)) - 1)); }
        static int Int(byte[] b) { int v = 0; for (int i = 0; i < b.Length && i < 4; i++) v = v << 8 | b[i]; return v; }
        static string Utf16(byte[] b) { if (b.Length == 1) return ((char)b[0]).ToString(); var sb = new StringBuilder(); for (int i = 0; i + 1 < b.Length; i += 2) sb.Append((char)(b[i] << 8 | b[i + 1])); return sb.ToString(); }
        static string? GlyphStr(string name) { int u = VectorFontData.GlyphToUnicode(name); return u >= 0 ? char.ConvertFromUtf32(u) : null; }
        public bool HasUnicode => uni.Count > 0 || uniRanges.Count > 0;
        /// <summary>Splits a string into codes: (code, byte length).</summary>
        public void Decode(byte[] s, List<(int code, int nbytes)> outCodes)
        {
            int i = 0;
            while (i < s.Length)
            {
                int used = 0, code = 0;
                // try byte lengths 1..4: the first codespace whose range contains the partial code decides
                for (int n = 1; n <= 4 && i + n <= s.Length; n++)
                {
                    code = code << 8 | s[i + n - 1];
                    foreach (var cs in codespace) if (cs.nbytes == n && code >= cs.lo && code <= cs.hi) { used = n; break; }
                    if (used > 0) break;
                }
                if (used == 0)
                {   // no codespace matched: use the shortest codespace length (spec: partial match rules simplified)
                    int n = 4; foreach (var cs in codespace) n = Math.Min(n, cs.nbytes); if (i + n > s.Length) n = s.Length - i;
                    code = 0; for (int k = 0; k < n; k++) code = code << 8 | s[i + k]; used = Math.Max(1, n);
                }
                outCodes.Add((code, used)); i += used;
            }
        }
        public int Cid(int code)
        {
            if (single.TryGetValue(code, out int c)) return c;
            foreach (var r in ranges) if (code >= r.Lo && code <= r.Hi) return r.Dst + (code - r.Lo);
            return Identity ? code : 0;
        }
        public string? Unicode(int code)
        {
            if (uni.TryGetValue(code, out var s)) return s;
            foreach (var r in uniRanges) if (code >= r.lo && code <= r.hi) { if (r.dst.Length == 0) return null; var chars = r.dst.ToCharArray(); chars[^1] = (char)(chars[^1] + (code - r.lo)); return new string(chars); }
            return null;
        }
    }

    /// <summary>A font as the content interpreter sees it: glyph source, code -> glyph mapping, widths, Type 3 procedures.</summary>
    internal sealed class PdfFont
    {
        public string Subtype = "Type1"; public string? BaseFont; public int Flags; public bool Type0, Type3, Embedded, Symbolic, Substituted;
        public GlyphFont? Prog;
        public float[]? Widths; public int FirstChar; public float MissingWidth;         // simple fonts, text space (/1000 applied; Type3: glyph space)
        public Dictionary<int, float>? CidWidths; public float DefaultWidth = 1f;      // Type0
        public string?[] Names = new string?[256]; public bool HasDifferences, HasBaseEncoding; public string? BaseEncodingName;
        public PdfCMap? CMap, ToUnicode; public byte[]? Cid2Gid; public bool CidIsGid = true;
        public PdfDict? CharProcs, T3Resources; public Matrix3x2 FontMatrix = new Matrix3x2(0.001f, 0, 0, 0.001f, 0, 0);
        readonly Dictionary<int, int> gidCache = new Dictionary<int, int>();

        /// <summary>Glyph index for a character code (simple fonts) or CID (Type0). 0 / -1 = nothing to draw.</summary>
        public int Gid(int code)
        {
            if (gidCache.TryGetValue(code, out int g)) return g;
            g = Lookup(code); gidCache[code] = g; return g;
        }
        int Lookup(int code)
        {
            var p = Prog; if (p == null) return -1;
            if (Type0)
            {
                int cid = code;
                if (Cid2Gid != null) { int i = cid * 2; return i + 1 < Cid2Gid.Length ? Cid2Gid[i] << 8 | Cid2Gid[i + 1] : 0; }
                if (Substituted)
                {   // stand-in font: only Unicode gets us to a glyph
                    var u = ToUnicode?.Unicode(code); if (u != null && u.Length > 0) { int gg = p.GidByUnicode(char.ConvertToUtf32(u, 0)); if (gg > 0) return gg; }
                    return -1;
                }
                if (p.IsCidKeyed) return p.GidByCid(cid);
                return cid;
            }
            string? name = Names[code];
            var tt = p as TrueTypeFont; bool nameBased = p is Type1Font || p is CffFont || (tt != null && tt.Cff != null && !tt.HasGlyf);
            if (Substituted)
            {
                string? n2 = name ?? (Symbolic && BaseFont != null && BaseFont.Contains("Symbol") ? VectorFontData.SymbolEncoding[code] : VectorFontData.StandardEncoding[code]);
                var u = ToUnicode?.Unicode(code); int uc = u != null && u.Length > 0 ? char.ConvertToUtf32(u, 0) : -1;
                if (uc < 0 && !string.IsNullOrEmpty(n2)) uc = VectorFontData.GlyphToUnicode(n2);
                if (uc < 0 && name == null) uc = code;
                if (uc >= 0) { int gg = p.GidByUnicode(uc); if (gg > 0) return gg; }
                if (!string.IsNullOrEmpty(n2)) { int gg = p.GidByName(n2); if (gg > 0) return gg; }
                return -1;
            }
            if (nameBased)
            {
                if (name == null && !HasBaseEncoding) { int gg = p.GidByCode(code); if (gg > 0) return gg; }
                name ??= VectorFontData.StandardEncoding[code];
                if (!string.IsNullOrEmpty(name))
                {
                    int gg = p.GidByName(name); if (gg >= 0) return gg;
                    int u = VectorFontData.GlyphToUnicode(name);
                    if (u >= 0)
                    {
                        if (VectorFontData.GlyphListReverse.TryGetValue(u, out var alt) && alt != name) { gg = p.GidByName(alt); if (gg >= 0) return gg; }
                        gg = p.GidByName($"uni{u:X4}"); if (gg >= 0) return gg;
                        if (tt != null) { gg = tt.GidByUnicode(u); if (gg > 0) return gg; }
                    }
                    int num = NumberedName(name); if (num >= 0 && num < Math.Max(1, p.GlyphCount)) return num;
                }
                { int gg = p.GidByCode(code); if (gg > 0) return gg; }
                return 0;
            }
            // TrueType glyf outlines
            if (tt == null) return 0;
            if (Symbolic && !HasDifferences) { int gg = tt.GidByCode(code); if (gg > 0) return gg; }
            string? nm = name ?? VectorFontData.StandardEncoding[code];
            if (!string.IsNullOrEmpty(nm))
            {
                int u = VectorFontData.GlyphToUnicode(nm);
                if (u >= 0) { int gg = tt.GidByUnicode(u); if (gg > 0) return gg; }
                { int gg = tt.GidByName(nm); if (gg > 0) return gg; }
                int num = NumberedName(nm); if (num >= 0 && tt.HasCmap == false) return num;
                if (u >= 0 && tt.HasMacCmap) { int mc = MacCode(u); if (mc >= 0) { int gg = tt.GidByMacCode(mc); if (gg > 0) return gg; } }
            }
            { int gg = tt.GidByCode(code); if (gg > 0) return gg; }
            if (!tt.HasCmap) return code;
            { int gg = tt.GidByMacCode(code); if (gg > 0) return gg; }
            if (name == null) return code;
            return 0;
        }
        static int NumberedName(string n)
        {   // g12, glyph12, cid12, c12, G12, index12
            int i = 0; while (i < n.Length && char.IsLetter(n[i])) i++;
            if (i == 0 || i == n.Length) return -1;
            string pre = n.Substring(0, i).ToLowerInvariant(); if (pre != "g" && pre != "glyph" && pre != "cid" && pre != "c" && pre != "index") return -1;
            return int.TryParse(n.AsSpan(i), out int v) ? v : -1;
        }
        static int MacCode(int u) { var mac = VectorFontData.MacRomanEncoding; if (u < 128) return u; for (int c = 128; c < 256; c++) if (mac[c].Length > 0 && VectorFontData.GlyphToUnicode(mac[c]) == u) return c; return -1; }

        /// <summary>Horizontal advance in text space for a code (simple) / CID (Type0), before font size.</summary>
        public float Width(int code, int cid, int gid)
        {
            if (Type0) { if (CidWidths != null && CidWidths.TryGetValue(cid, out float w)) return w; return DefaultWidth; }
            if (Widths != null && code >= FirstChar && code - FirstChar < Widths.Length) { float w = Widths[code - FirstChar]; if (w != 0 || !Substituted || Type3) return w; }
            if (Widths != null && MissingWidth > 0) return MissingWidth;
            if (Type3) return 0;
            if (Prog != null && gid >= 0) return Prog.Advance(gid);
            return MissingWidth;
        }
        /// <summary>Unicode text for a code (ToUnicode, glyph name or the code itself) - for the shape tag.</summary>
        public string Text(int code)
        {
            var u = ToUnicode?.Unicode(code); if (u != null) return u;
            if (!Type0) { string? n = Names[code] ?? VectorFontData.StandardEncoding[code]; int uc = !string.IsNullOrEmpty(n) ? VectorFontData.GlyphToUnicode(n) : -1; if (uc >= 0) return char.ConvertFromUtf32(uc); if (code >= 32 && code < 127) return ((char)code).ToString(); }
            return "";
        }
    }

    internal sealed partial class PdfContent
    {
        // ---- text object state (BT .. ET)
        Matrix3x2 tm = Matrix3x2.Identity, tlm = Matrix3x2.Identity;
        VectorPath? textClip; bool textClipUsed;

        void TextOp(string op, List<object?> o, PdfDict res, int depth)
        {
            switch (op)
            {
                case "BT": tm = tlm = Matrix3x2.Identity; textClip = null; textClipUsed = false; break;
                case "ET":
                    if (textClipUsed)
                    {
                        var p = textClip ?? new VectorPath().Rect(-1e6f, -1e6f, 1e-3f, 1e-3f);
                        gs.Clip = gs.Clip == null ? new VectorClip(p) : gs.Clip.Intersect(p);
                        textClip = null; textClipUsed = false;
                    }
                    break;
                case "Tc": gs.CharSp = N(o, 0); break;
                case "Tw": gs.WordSp = N(o, 0); break;
                case "Tz": gs.Hscale = N(o, 0) / 100f; break;
                case "TL": gs.Leading = N(o, 0); break;
                case "Ts": gs.Rise = N(o, 0); break;
                case "Tr": gs.RenderMode = Math.Clamp((int)N(o, 0), 0, 7); break;
                case "Tf": gs.FontSize = N(o, 1); gs.Font = LoadFont(o.Count > 0 ? o[0] as string : null, res); break;
                case "Td": tlm = Matrix3x2.CreateTranslation(N(o, 0), N(o, 1)) * tlm; tm = tlm; break;
                case "TD": gs.Leading = -N(o, 1); tlm = Matrix3x2.CreateTranslation(N(o, 0), N(o, 1)) * tlm; tm = tlm; break;
                case "Tm": tlm = new Matrix3x2(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5)); tm = tlm; break;
                case "T*": NextLine(); break;
                case "Tj": if (o.Count > 0 && o[^1] is byte[] s) Show(s, res, depth); break;
                case "'": NextLine(); if (o.Count > 0 && o[^1] is byte[] s1) Show(s1, res, depth); break;
                case "\"": gs.WordSp = N(o, 0); gs.CharSp = N(o, 1); NextLine(); if (o.Count > 2 && o[2] is byte[] s2) Show(s2, res, depth); break;
                case "TJ":
                    if (o.Count > 0 && o[^1] is object?[] arr)
                        foreach (var e in arr)
                        {
                            if (e is byte[] es) Show(es, res, depth);
                            else if (e is double adj) { float tx = (float)(-adj / 1000 * gs.FontSize * gs.Hscale); tm = Matrix3x2.CreateTranslation(tx, 0) * tm; }
                        }
                    break;
            }
        }
        void NextLine() { tlm = Matrix3x2.CreateTranslation(0, -gs.Leading) * tlm; tm = tlm; }

        // ---- fonts
        PdfFont? LoadFont(string? name, PdfDict res)
        {
            PdfDict? fd = null;
            if (name != null) { var fonts = doc.Resolve(res.Get("Font")) as PdfDict; fd = fonts != null ? doc.Resolve(fonts.Get(name)) as PdfDict : null; }
            if (fd == null) { Warn($"font resource {name} missing - Helvetica substituted"); fd = new PdfDict(); fd.D["BaseFont"] = "Helvetica"; fd.D["Subtype"] = "Type1"; name ??= "?"; }
            if (doc.Fonts.TryGetValue(fd, out var cached)) return cached;
            PdfFont f;
            try { f = BuildFont(fd); } catch (Exception ex) { Warn("font could not be read: " + ex.Message); f = new PdfFont(); }
            doc.Fonts[fd] = f; return f;
        }
        PdfFont BuildFont(PdfDict fd)
        {
            var f = new PdfFont { Subtype = doc.Resolve(fd.Get("Subtype")) as string ?? "Type1", BaseFont = doc.Resolve(fd.Get("BaseFont")) as string };
            PdfDict font = fd;
            if (f.Subtype == "Type0")
            {
                f.Type0 = true;
                var desc = doc.Resolve(fd.Get("DescendantFonts")) as object?[]; var cidFont = desc != null && desc.Length > 0 ? doc.Resolve(desc[0]) as PdfDict : null;
                var enc = doc.Resolve(fd.Get("Encoding"));
                if (enc is PdfStream es) f.CMap = PdfCMap.Parse(doc.Decode(es));
                else { string en = enc as string ?? "Identity-H"; f.CMap = PdfCMap.IdentityH(); f.CMap.Vertical = en.EndsWith("-V", StringComparison.Ordinal); if (!en.StartsWith("Identity", StringComparison.Ordinal)) Warn($"predefined CMap {en} is not available - codes treated as 2-byte CIDs"); }
                if (cidFont != null)
                {
                    font = cidFont;
                    f.DefaultWidth = (float)(doc.Resolve(cidFont.Get("DW")) as double? ?? 1000) / 1000f;
                    if (doc.Resolve(cidFont.Get("W")) is object?[] W)
                    {
                        f.CidWidths = new Dictionary<int, float>();
                        for (int i = 0; i < W.Length;)
                        {
                            if (!(doc.Resolve(W[i]) is double a)) { i++; continue; }
                            if (i + 1 < W.Length && doc.Resolve(W[i + 1]) is object?[] list) { for (int k = 0; k < list.Length; k++) if (doc.Resolve(list[k]) is double wv) f.CidWidths[(int)a + k] = (float)wv / 1000f; i += 2; }
                            else if (i + 2 < W.Length && doc.Resolve(W[i + 1]) is double b && doc.Resolve(W[i + 2]) is double wv2) { int lo = (int)a, hi = Math.Min((int)b, lo + 65535); for (int c = lo; c <= hi; c++) f.CidWidths[c] = (float)wv2 / 1000f; i += 3; }
                            else i++;
                        }
                    }
                    var c2g = doc.Resolve(cidFont.Get("CIDToGIDMap"));
                    if (c2g is PdfStream cs) f.Cid2Gid = doc.Decode(cs);
                }
            }
            else if (f.Subtype == "Type3")
            {
                f.Type3 = true;
                if (doc.Resolve(fd.Get("FontMatrix")) is object?[] fm && fm.Length >= 6) f.FontMatrix = new Matrix3x2(Fl(fm[0]), Fl(fm[1]), Fl(fm[2]), Fl(fm[3]), Fl(fm[4]), Fl(fm[5]));
                f.CharProcs = doc.Resolve(fd.Get("CharProcs")) as PdfDict; f.T3Resources = doc.Resolve(fd.Get("Resources")) as PdfDict;
            }
            var fdesc = doc.Resolve(font.Get("FontDescriptor")) as PdfDict;
            if (fdesc != null)
            {
                f.Flags = (int)(doc.Resolve(fdesc.Get("Flags")) as double? ?? 0); f.MissingWidth = (float)(doc.Resolve(fdesc.Get("MissingWidth")) as double? ?? 0) / 1000f;
                foreach (var key in new[] { "FontFile2", "FontFile3", "FontFile" })
                {
                    if (doc.Resolve(fdesc.Get(key)) is PdfStream ff)
                    {
                        byte[] data; try { data = doc.Decode(ff); } catch (Exception e) { ImportLog.Swallowed(e, "PDF embedded font file"); continue; }
                        string? sub = doc.Resolve(ff.Dict.Get("Subtype")) as string;
                        f.Prog = GlyphFont.Parse(data, sub ?? key);
                        if (f.Prog != null && f.Prog.GlyphCount > 0) { f.Embedded = true; break; }
                        f.Prog = null;
                    }
                }
            }
            f.Symbolic = (f.Flags & 4) != 0 && (f.Flags & 32) == 0;
            if (!f.Type3)
            {
                // simple-font widths
                if (!f.Type0)
                {
                    f.FirstChar = (int)(doc.Resolve(fd.Get("FirstChar")) as double? ?? 0);
                    if (doc.Resolve(fd.Get("Widths")) is object?[] ws && ws.Length > 0) { f.Widths = new float[ws.Length]; for (int i = 0; i < ws.Length; i++) f.Widths[i] = Fl(ws[i]) / 1000f; }
                }
                if (f.Prog == null)
                {
                    f.Prog = VectorFonts.Find(f.BaseFont, f.Flags); f.Substituted = f.Prog != null;
                    if (f.Prog == null) Warn($"font {f.BaseFont ?? f.Subtype} is not embedded and no substitute font was found (text skipped)");
                    else if (f.Type0 && !f.Symbolic) { }
                }
                // CIDToGIDMap only applies to embedded CIDFontType2
                if (f.Substituted) f.Cid2Gid = null;
            }
            else
            {
                f.FirstChar = (int)(doc.Resolve(fd.Get("FirstChar")) as double? ?? 0);
                if (doc.Resolve(fd.Get("Widths")) is object?[] ws) { f.Widths = new float[ws.Length]; for (int i = 0; i < ws.Length; i++) f.Widths[i] = Fl(ws[i]); }
            }
            // encoding (simple fonts)
            if (!f.Type0)
            {
                var enc = doc.Resolve(fd.Get("Encoding"));
                string[]? baseEnc = null;
                bool stdSymbolFont = f.BaseFont != null && (f.BaseFont.Contains("Symbol") || f.BaseFont.Contains("Dingbat"));
                if (!f.Embedded && !f.Type3) baseEnc = stdSymbolFont ? (f.BaseFont!.Contains("Symbol") ? VectorFontData.SymbolEncoding : VectorFontData.ZapfDingbatsEncoding) : VectorFontData.StandardEncoding;
                else if (!f.Symbolic && !f.Type3) baseEnc = VectorFontData.StandardEncoding;
                if (enc is string en) { var e = VectorFontData.EncodingByName(en); if (e != null) { baseEnc = e; f.HasBaseEncoding = true; f.BaseEncodingName = en; } }
                else if (enc is PdfDict ed)
                {
                    if (doc.Resolve(ed.Get("BaseEncoding")) is string ben) { var e = VectorFontData.EncodingByName(ben); if (e != null) { baseEnc = e; f.HasBaseEncoding = true; f.BaseEncodingName = ben; } }
                    else if (baseEnc == null && !f.Symbolic) baseEnc = VectorFontData.StandardEncoding;
                }
                if (f.Substituted && baseEnc == null) baseEnc = VectorFontData.StandardEncoding;
                if (baseEnc != null) for (int i = 0; i < 256; i++) f.Names[i] = baseEnc[i].Length > 0 ? baseEnc[i] : null;
                if (enc is PdfDict ed2 && doc.Resolve(ed2.Get("Differences")) is object?[] diff)
                {
                    int code = 0;
                    foreach (var item in diff) { var v = doc.Resolve(item); if (v is double dc) code = (int)dc; else if (v is string gn) { if (code >= 0 && code < 256) f.Names[code] = gn; code++; f.HasDifferences = true; } }
                }
            }
            if (doc.Resolve(fd.Get("ToUnicode")) is PdfStream tu) { try { f.ToUnicode = PdfCMap.Parse(doc.Decode(tu)); } catch (Exception e) { ImportLog.Swallowed(e, "PDF ToUnicode CMap"); } }
            return f;
        }
        float Fl(object? v) => (float)(doc.Resolve(v) as double? ?? 0);

        // ---- showing text
        static readonly List<(int code, int nbytes)> codeBuf = new List<(int, int)>(64);
        void Show(byte[] s, PdfDict res, int depth)
        {
            var f = gs.Font;
            if (f == null) { f = gs.Font = LoadFont(null, res); if (f == null) return; }
            float fs = gs.FontSize, th = gs.Hscale; int mode = gs.RenderMode;
            bool invisible = mode == 3 || mode == 7 && false; bool doFill = mode == 0 || mode == 2 || mode == 4 || mode == 6, doStroke = mode == 1 || mode == 2 || mode == 5 || mode == 6, doClip = mode >= 4;
            if (mode == 7) { doFill = doStroke = false; }
            if (doClip) textClipUsed = true;
            if (mode == 3 && !doClip) { AdvanceOnly(s, f); return; }
            if (f.Prog == null && !f.Type3) { AdvanceOnly(s, f); return; }
            var codes = codeBuf; codes.Clear();
            if (f.Type0 && f.CMap != null) f.CMap.Decode(s, codes); else foreach (var b in s) codes.Add((b, 1));
            VectorPath? combined = null; var text = new StringBuilder();
            var trmBase = new Matrix3x2(fs * th, 0, 0, fs, 0, gs.Rise);
            var startTm = tm; var startDev = Vector2.Transform(new Vector2(0, gs.Rise), tm * gs.Ctm); float sizeDev = fs * VectorRender.MeanScale(tm * gs.Ctm);
            foreach (var (code, nbytes) in codes)
            {
                int cid = f.Type0 && f.CMap != null ? f.CMap.Cid(code) : code;
                int gid = f.Type3 ? -1 : f.Gid(f.Type0 ? cid : code);
                float w0 = f.Width(code, cid, gid);
                var trm = trmBase * tm * gs.Ctm;
                if (f.Type3)
                {
                    w0 = Vector2.TransformNormal(new Vector2(w0, 0), f.FontMatrix).X;
                    if (!invisible) Type3Glyph(f, code, trm, res, depth);
                }
                else if (gid > 0 || (gid == 0 && f.Prog is TrueTypeFont && f.Substituted == false && false))
                {
                    var gp = f.Prog!.Glyph(gid);
                    if (gp != null) { combined ??= new VectorPath(); combined.Append(gp, trm); }
                }
                text.Append(f.Text(code));
                float adv = (w0 * fs + gs.CharSp + (nbytes == 1 && code == 32 ? gs.WordSp : 0)) * th;
                if (f.CMap != null && f.CMap.Vertical) tm = Matrix3x2.CreateTranslation(0, -(w0 * fs + gs.CharSp)) * tm;
                else tm = Matrix3x2.CreateTranslation(adv, 0) * tm;
            }
            if (combined == null) return;
            if (doClip) { textClip ??= new VectorPath(); textClip.Append(combined); }
            if (!doFill && !doStroke) return;
            // advance box of the run (baseline start -> end, ascent 0.8 em, descent 0.25 em) in device space - independent of the glyph shapes
            var endDev = Vector2.Transform(new Vector2(0, gs.Rise), tm * gs.Ctm);
            var boxPath = new VectorPath(); var full = trmBase * startTm * gs.Ctm; float advUnits = 0; { if (Matrix3x2.Invert(full, out var inv)) advUnits = Vector2.Transform(endDev, inv).X; }
            var p0 = Vector2.Transform(new Vector2(0, -0.25f), full); var p1 = Vector2.Transform(new Vector2(advUnits, -0.25f), full); var p2 = Vector2.Transform(new Vector2(advUnits, 0.8f), full); var p3 = Vector2.Transform(new Vector2(0, 0.8f), full);
            var run = new VectorTextRun { Text = text.ToString(), Start = new PointF(startDev.X, startDev.Y), End = new PointF(endDev.X, endDev.Y), Size = sizeDev, FontName = f.BaseFont ?? f.Subtype, Quad = new[] { new PointF(p0.X, p0.Y), new PointF(p1.X, p1.Y), new PointF(p2.X, p2.Y), new PointF(p3.X, p3.Y) } };
            var shape = new VectorShape { Path = combined, Fill = null, Stroke = null, Clip = gs.Clip, IsText = true, Tag = run };
            if (doFill && gs.Fill != null) shape.Fill = gs.Alpha < 1 ? WithAlpha(gs.Fill, gs.Alpha) : gs.Fill;
            if (doStroke && gs.Stroke != null)
            {
                shape.Stroke = gs.StrokeAlpha < 1 ? WithAlpha(gs.Stroke, gs.StrokeAlpha) : gs.Stroke;
                shape.StrokeWidth = gs.LineWidth * VectorRender.MeanScale(gs.Ctm); shape.Cap = gs.Cap; shape.Join = gs.Join; shape.MiterLimit = gs.Miter;
                if (gs.Dash != null) { float sc = VectorRender.MeanScale(gs.Ctm); shape.Dash = new float[gs.Dash.Length]; for (int i = 0; i < gs.Dash.Length; i++) shape.Dash[i] = gs.Dash[i] * sc; shape.DashOffset = gs.DashOffset * sc; }
            }
            if (shape.Fill == null && shape.Stroke == null) return;
            img.Shapes.Add(shape);
        }
        void AdvanceOnly(byte[] s, PdfFont f)
        {
            var codes = codeBuf; codes.Clear();
            if (f.Type0 && f.CMap != null) f.CMap.Decode(s, codes); else foreach (var b in s) codes.Add((b, 1));
            foreach (var (code, nbytes) in codes)
            {
                int cid = f.Type0 && f.CMap != null ? f.CMap.Cid(code) : code; float w0 = f.Width(code, cid, -1);
                if (f.Type3) w0 = Vector2.TransformNormal(new Vector2(w0, 0), f.FontMatrix).X;
                float adv = (w0 * gs.FontSize + gs.CharSp + (nbytes == 1 && code == 32 ? gs.WordSp : 0)) * gs.Hscale;
                tm = Matrix3x2.CreateTranslation(adv, 0) * tm;
            }
        }
        void Type3Glyph(PdfFont f, int code, Matrix3x2 trm, PdfDict res, int depth)
        {
            if (f.CharProcs == null || depth > 8) return;
            string? name = f.Names[code]; if (name == null) return;
            if (!(doc.Resolve(f.CharProcs.Get(name)) is PdfStream proc)) return;
            var saved = gs.Clone(); var savedStack = new List<GS>(stack); var savedPath = path; bool savedHas = hasCur; var savedBase = baseForPatterns;
            var savedTm = tm; var savedTlm = tlm; var savedClip = textClip; bool savedClipUsed = textClipUsed;
            var inner = new PdfContent(doc, img) { gs = gs.Clone(), baseForPatterns = baseForPatterns };
            inner.gs.Ctm = f.FontMatrix * trm; inner.gs.Font = null; inner.gs.RenderMode = 0;
            foreach (var w in warned) inner.warned.Add(w);
            try { inner.Run(doc.Decode(proc), f.T3Resources ?? res, inner.gs.Ctm, depth + 1); } catch (Exception e) { ImportLog.Swallowed(e, "PDF Type 3 glyph procedure"); }
            foreach (var w in inner.warned) warned.Add(w);
            gs = saved; stack.Clear(); stack.AddRange(savedStack); path = savedPath; hasCur = savedHas; baseForPatterns = savedBase;
            tm = savedTm; tlm = savedTlm; textClip = savedClip; textClipUsed = savedClipUsed;
        }

        // ================================================================================ images
        void DrawImage(PdfStream xo, PdfDict res)
        {
            var dict = xo.Dict;
            bool isMask = doc.Resolve(dict.Get("ImageMask") ?? dict.Get("IM")) is bool im && im;
            VectorImagePaint? paint;
            int colorKey = 0;
            if (isMask)
            {
                var fc = gs.Fill; if (fc == null) return;
                int argb = fc is VectorColor vc ? vc.Argb : fc is VectorGradient vg ? vg.AverageArgb() : fc is VectorImagePaint ip ? ip.AverageArgb() : unchecked((int)0xFF000000);
                colorKey = argb;
            }
            var key = (xo, colorKey);
            if (!doc.Images.TryGetValue(key, out paint))
            {
                try { paint = DecodeImage(xo, res, isMask ? colorKey : (int?)null); }
                catch (Exception ex) { Warn("image could not be decoded (" + ex.Message + ")"); paint = null; }
                if (doc.Images.Count > 64) doc.Images.Clear();
                doc.Images[key] = paint;
            }
            PlaceImage(paint, isMask);
        }
        void PlaceImage(VectorImagePaint? paint, bool isMask)
        {
            var p = new VectorPath(); var a = Dev(0, 0); var b = Dev(1, 0); var c = Dev(1, 1); var d = Dev(0, 1);
            p.MoveTo(a.X, a.Y).LineTo(b.X, b.Y).LineTo(c.X, c.Y).LineTo(d.X, d.Y).Close();
            VectorPaint fill;
            if (paint == null) fill = new VectorColor(unchecked((int)0x60808080));   // placeholder for undecodable images
            else { paint = (VectorImagePaint)paint.Clone(); paint.Matrix = Matrix3x2.CreateScale(1f / paint.Width, -1f / paint.Height) * Matrix3x2.CreateTranslation(0, 1) * gs.Ctm; fill = paint; }
            var shape = new VectorShape { Path = p, Fill = fill, Clip = gs.Clip, Opacity = gs.Alpha, IsImage = true };
            img.Shapes.Add(shape);
        }
        void InlineImage(PdfLexer lx, PdfDict res)
        {
            // dictionary up to ID
            var dict = new PdfDict(); var items = new List<object?>();
            while (true)
            {
                var t = lx.Token(); if (t == null) return;
                if (t is PdfOp op) { if (op.N == "ID") break; if (op.N == "[" || op.N == "<<") { items.Add(lx.ObjectFrom(t, doc, false)); continue; } if (op.N == "true") items.Add(true); else if (op.N == "false") items.Add(false); else if (op.N == "null") items.Add(null); continue; }
                items.Add(t);
            }
            for (int i = 0; i + 1 < items.Count; i += 2) if (items[i] is string k) dict.D[Expand(k)] = items[i + 1]; else i--;
            // binary data: one whitespace after ID
            var d = lx.D; int p = lx.Pos, end = lx.End; if (p < end && PdfLexer.Ws(d[p])) p++;
            int start = p, len = -1;
            var filt = doc.Resolve(dict.Get("Filter"));
            bool unfiltered = filt == null || (filt is object?[] fa && fa.Length == 0);
            if (unfiltered)
            {
                int w = (int)(doc.Resolve(dict.Get("Width")) as double? ?? 0), h = (int)(doc.Resolve(dict.Get("Height")) as double? ?? 0), bpc = (int)(doc.Resolve(dict.Get("BitsPerComponent")) as double? ?? 8);
                bool mask = doc.Resolve(dict.Get("ImageMask")) is bool m && m; int n = mask ? 1 : CsInfo(CsObj(doc.Resolve(dict.Get("ColorSpace")) as string ?? "DeviceGray", res)).ncomp; if (mask) bpc = 1;
                len = (w * n * bpc + 7) / 8 * h; if (start + len > end) len = -1;
            }
            if (len < 0)
            {   // search for whitespace EI whitespace/EOF
                int q = start;
                while (q + 1 < end) { if (d[q] == 'E' && d[q + 1] == 'I' && (q + 2 >= end || PdfLexer.Ws(d[q + 2]) || PdfLexer.Delim(d[q + 2])) && q > start && PdfLexer.Ws(d[q - 1])) { len = q - 1 - start; break; } q++; }
                if (len < 0) len = end - start;
            }
            var raw = new byte[Math.Max(0, len)]; Array.Copy(d, start, raw, 0, raw.Length);
            // position after EI
            int e = start + len; while (e + 1 < end && !(d[e] == 'E' && d[e + 1] == 'I' && (e + 2 >= end || PdfLexer.Ws(d[e + 2]) || PdfLexer.Delim(d[e + 2])))) e++;
            lx.Pos = Math.Min(end, e + 2);
            // named colour space through the resources
            if (dict.Get("ColorSpace") is string csn) { var o = CsObj(csn, res); if (o != null) dict.D["ColorSpace"] = o; }
            var st = new PdfStream(dict, raw);
            bool isMask = doc.Resolve(dict.Get("ImageMask")) is bool im && im;
            int colorKey = 0; if (isMask) { var fc = gs.Fill; if (fc == null) return; colorKey = fc is VectorColor vc ? vc.Argb : fc is VectorGradient vg ? vg.AverageArgb() : unchecked((int)0xFF000000); }
            VectorImagePaint? paint;
            try { paint = DecodeImage(st, res, isMask ? colorKey : (int?)null); } catch (Exception ex) { Warn("inline image could not be decoded (" + ex.Message + ")"); paint = null; }
            PlaceImage(paint, isMask);
            static string Expand(string k) => k switch { "W" => "Width", "H" => "Height", "BPC" => "BitsPerComponent", "CS" => "ColorSpace", "F" => "Filter", "DP" => "DecodeParms", "D" => "Decode", "IM" => "ImageMask", "I" => "Interpolate", "L" => "Length", _ => k };
        }

        /// <summary>Decodes an image XObject / inline image to straight ARGB. <paramref name="stencilArgb"/>: ImageMask colour (alpha from the bits).</summary>
        VectorImagePaint? DecodeImage(PdfStream xo, PdfDict res, int? stencilArgb, bool asSoftMask = false)
        {
            var dict = xo.Dict;
            int w = (int)(doc.Resolve(dict.Get("Width")) as double? ?? 0), h = (int)(doc.Resolve(dict.Get("Height")) as double? ?? 0);
            if (w <= 0 || h <= 0) return null;
            if ((long)w * h > 60_000_000) { Warn($"image {w}x{h} too large - skipped"); return null; }
            int bpc = (int)(doc.Resolve(dict.Get("BitsPerComponent")) as double? ?? (stencilArgb.HasValue ? 1 : 8));
            bool isMask = stencilArgb.HasValue; if (isMask) bpc = 1;
            var data = doc.DecodeImageData(xo, out string? codec, out PdfDict? parms);
            string fam = "DeviceGray"; int n = 1; object? cso = null;
            if (!isMask)
            {
                var csObj = dict.Get("ColorSpace"); if (csObj is string csName) csObj = CsObj(csName, res);
                if (csObj != null) (fam, n, cso) = CsInfo(csObj);
                if (asSoftMask) { fam = "DeviceGray"; n = 1; }
            }
            int jpegComps = 0;
            switch (codec)
            {
                case null: break;
                case "DCTDecode": case "DCT":
                    {
                        var jd = new JpegDecoder(data); data = jd.Decode(); jpegComps = jd.Components; bpc = 8;
                        if (jd.Width != w || jd.Height != h) { w = jd.Width; h = jd.Height; }
                        if (jpegComps != n && fam != "Indexed") { n = jpegComps; fam = n == 1 ? "DeviceGray" : n == 4 ? "DeviceCMYK" : "DeviceRGB"; cso = null; }
                        if (jd.Adobe && jpegComps == 4 && jd.Transform == 2) { }   // YCCK already turned into CMYK
                        break;
                    }
                case "CCITTFaxDecode": case "CCF":
                    {
                        int K = (int)(doc.Resolve(parms?.Get("K")) as double? ?? 0), cols = (int)(doc.Resolve(parms?.Get("Columns")) as double? ?? 1728), rows = (int)(doc.Resolve(parms?.Get("Rows")) as double? ?? h);
                        bool black1 = doc.Resolve(parms?.Get("BlackIs1")) is bool b1 && b1, align = doc.Resolve(parms?.Get("EncodedByteAlign")) is bool ba && ba;
                        data = new CcittDecoder(data, K, cols, align, black1).Decode(rows); bpc = 1; if (!isMask) { fam = "DeviceGray"; n = 1; cso = null; }
                        if (cols != w) w = cols;
                        break;
                    }
                case "JPXDecode": Warn("JPEG 2000 images (JPXDecode) are not supported - grey placeholder"); return null;
                case "JBIG2Decode": Warn("JBIG2 images are not supported - grey placeholder"); return null;
                default: Warn($"image filter {codec} not supported - grey placeholder"); return null;
            }
            if (bpc != 1 && bpc != 2 && bpc != 4 && bpc != 8 && bpc != 16) bpc = 8;
            int maxv = bpc == 16 ? 255 : (1 << bpc) - 1;
            int stride = (w * n * bpc + 7) / 8;
            if ((long)stride * h > data.Length) { int have = (int)(data.Length / Math.Max(1, stride)); if (have <= 0) return null; if (have < h) { var padded = new byte[(long)stride * h > int.MaxValue ? int.MaxValue : stride * h]; Array.Copy(data, padded, Math.Min(data.Length, padded.Length)); data = padded; } }
            // decode array
            float[]? decode = null; if (doc.Resolve(dict.Get("Decode")) is object?[] da && da.Length >= 2 * n) { decode = new float[2 * n]; for (int i = 0; i < 2 * n; i++) decode[i] = Fl(da[i]); }
            var px = new int[w * h]; bool hasAlpha = false;
            var raw = new int[w * n];
            if (isMask)
            {
                bool inv = decode != null && decode[0] == 1; int rgb = stencilArgb!.Value & 0xFFFFFF; int on = unchecked((int)0xFF000000) | rgb;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++) { int bit = (data[row + (x >> 3)] >> (7 - (x & 7))) & 1; if (inv) bit ^= 1; px[y * w + x] = bit == 0 ? on : 0; }
                }
                return new VectorImagePaint(px, w, h) { HasAlpha = true };
            }
            // colour conversion
            float[] dmin = new float[n], dscale = new float[n];
            for (int c = 0; c < n; c++)
            {
                float lo = 0, hi = 1;
                if (fam == "Indexed") { lo = 0; hi = maxv; } else if (fam == "Lab") { hi = c == 0 ? 100 : 127; lo = c == 0 ? 0 : -128; if (cso != null && doc.Resolve(cso) is object?[] la && la.Length > 1 && doc.Resolve(la[1]) is PdfDict ld && doc.Resolve(ld.Get("Range")) is object?[] rg && rg.Length >= 4 && c > 0) { lo = Fl(rg[(c - 1) * 2]); hi = Fl(rg[(c - 1) * 2 + 1]); } }
                if (decode != null) { lo = decode[2 * c]; hi = decode[2 * c + 1]; }
                dmin[c] = lo; dscale[c] = (hi - lo) / maxv;
            }
            bool defaultDecode = decode == null;
            int[]? lut = null;
            if (n == 1 && bpc <= 8 && (fam == "DeviceGray" || fam == "Indexed" || fam == "Separation" || fam == "DeviceN" || fam == "Lab"))
            {
                lut = new int[maxv + 1];
                for (int v = 0; v <= maxv; v++) { var comp = new float[] { dmin[0] + v * dscale[0] }; var pc = Convert(fam, cso, comp) as VectorColor; lut[v] = pc?.Argb ?? unchecked((int)0xFF000000); }
            }
            Dictionary<long, int>? memo = lut == null && !(fam == "DeviceRGB" || fam == "DeviceCMYK") ? new Dictionary<long, int>() : null;
            float[] compBuf = new float[n];
            for (int y = 0; y < h; y++)
            {
                Unpack(data, y * stride, raw, w * n, bpc);
                int orow = y * w;
                if (lut != null) { for (int x = 0; x < w; x++) px[orow + x] = lut[Math.Min(maxv, raw[x])]; continue; }
                if (fam == "DeviceRGB" && n == 3)
                {
                    if (bpc == 8 && defaultDecode) for (int x = 0, i = 0; x < w; x++, i += 3) px[orow + x] = unchecked((int)0xFF000000) | raw[i] << 16 | raw[i + 1] << 8 | raw[i + 2];
                    else for (int x = 0, i = 0; x < w; x++, i += 3) px[orow + x] = unchecked((int)0xFF000000) | B(dmin[0] + raw[i] * dscale[0]) << 16 | B(dmin[1] + raw[i + 1] * dscale[1]) << 8 | B(dmin[2] + raw[i + 2] * dscale[2]);
                    continue;
                }
                if (fam == "DeviceCMYK" && n == 4)
                {
                    for (int x = 0, i = 0; x < w; x++, i += 4)
                    {
                        float c = dmin[0] + raw[i] * dscale[0], m = dmin[1] + raw[i + 1] * dscale[1], yy = dmin[2] + raw[i + 2] * dscale[2], k = dmin[3] + raw[i + 3] * dscale[3];
                        px[orow + x] = unchecked((int)0xFF000000) | B((1 - c) * (1 - k)) << 16 | B((1 - m) * (1 - k)) << 8 | B((1 - yy) * (1 - k));
                    }
                    continue;
                }
                for (int x = 0, i = 0; x < w; x++, i += n)
                {
                    long keyv = 0; for (int c = 0; c < n && c < 8; c++) keyv = keyv << 8 | (uint)(raw[i + c] & 255);
                    if (memo!.TryGetValue(keyv, out int col)) { px[orow + x] = col; continue; }
                    for (int c = 0; c < n; c++) compBuf[c] = dmin[c] + raw[i + c] * dscale[c];
                    var pc = Convert(fam, cso, compBuf) as VectorColor; col = pc?.Argb ?? unchecked((int)0xFF000000);
                    if (memo.Count < 65536) memo[keyv] = col;
                    px[orow + x] = col;
                }
            }
            if (asSoftMask) return new VectorImagePaint(px, w, h);
            // transparency: SMask (alpha image), Mask (stencil or colour key)
            var sm = doc.Resolve(dict.Get("SMask")) as PdfStream;
            if (sm != null)
            {
                VectorImagePaint? smp = null; try { smp = DecodeImage(sm, res, null, asSoftMask: true); } catch (Exception e) { ImportLog.Swallowed(e, "PDF image soft mask"); }
                if (smp != null) { ApplyAlpha(px, w, h, smp, false); hasAlpha = true; }
            }
            else
            {
                var mk = doc.Resolve(dict.Get("Mask"));
                if (mk is PdfStream ms)
                {
                    VectorImagePaint? mp = null; try { mp = DecodeImage(ms, res, unchecked((int)0xFFFFFFFF)); } catch (Exception e) { ImportLog.Swallowed(e, "PDF image mask"); }
                    if (mp != null) { ApplyAlpha(px, w, h, mp, true); hasAlpha = true; }
                }
                else if (mk is object?[] ranges && ranges.Length >= 2 * n && jpegComps == 0)
                {
                    var r = new int[2 * n]; for (int i = 0; i < 2 * n; i++) r[i] = (int)Fl(ranges[i]);
                    for (int y = 0; y < h; y++)
                    {
                        Unpack(data, y * stride, raw, w * n, bpc);
                        for (int x = 0, i = 0; x < w; x++, i += n) { bool inRange = true; for (int c = 0; c < n; c++) { int v = raw[i + c]; if (v < r[2 * c] || v > r[2 * c + 1]) { inRange = false; break; } } if (inRange) { px[y * w + x] &= 0xFFFFFF; hasAlpha = true; } }
                    }
                }
            }
            return new VectorImagePaint(px, w, h) { HasAlpha = hasAlpha };
        }
        static void Unpack(byte[] data, int off, int[] dst, int count, int bpc)
        {
            switch (bpc)
            {
                case 8: for (int i = 0; i < count; i++) dst[i] = off + i < data.Length ? data[off + i] : 0; break;
                case 16: for (int i = 0; i < count; i++) dst[i] = off + 2 * i < data.Length ? data[off + 2 * i] : 0; break;
                case 1: for (int i = 0; i < count; i++) { int p = off + (i >> 3); dst[i] = p < data.Length ? (data[p] >> (7 - (i & 7))) & 1 : 0; } break;
                case 2: for (int i = 0; i < count; i++) { int p = off + (i >> 2); dst[i] = p < data.Length ? (data[p] >> (6 - 2 * (i & 3))) & 3 : 0; } break;
                case 4: for (int i = 0; i < count; i++) { int p = off + (i >> 1); dst[i] = p < data.Length ? (data[p] >> ((i & 1) == 0 ? 4 : 0)) & 15 : 0; } break;
            }
        }
        /// <summary>Multiplies the alpha of <paramref name="px"/> by a mask image (nearest resampling): soft mask = grey level, stencil = its alpha.</summary>
        static void ApplyAlpha(int[] px, int w, int h, VectorImagePaint m, bool stencil)
        {
            var mp = m.Argb; int mw = m.Width, mh = m.Height;
            for (int y = 0; y < h; y++)
            {
                int my = mh == h ? y : (int)((long)y * mh / h); int mrow = my * mw;
                for (int x = 0; x < w; x++)
                {
                    int mx = mw == w ? x : (int)((long)x * mw / w); int mv = mp[mrow + mx];
                    int a = stencil ? (mv >> 24) & 255 : mv & 255;
                    int i = y * w + x; int pa = (px[i] >> 24) & 255; a = (a * pa + 127) / 255;
                    px[i] = (px[i] & 0xFFFFFF) | a << 24;
                }
            }
        }
    }
}
