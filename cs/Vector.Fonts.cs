using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Sr2d64CSport
{
    /// <summary>
    /// Font programs as outline sources for the vector importers: TrueType / OpenType (glyf or CFF outlines, cmap, post
    /// names, hmtx), bare CFF / Type1C / CIDFontType0C (Type 2 charstrings, charsets, encodings, CID-keyed FDArray) and
    /// Type 1 (PFA / PFB / PDF FontFile: eexec, Type 1 charstrings, flex, seac). Glyphs come back as <see cref="VectorPath"/>s
    /// in em units (1.0 = the em, y up), so a text renderer only multiplies by the font size. Outlines are cached per glyph.
    /// </summary>
    public abstract class GlyphFont
    {
        readonly Dictionary<int, VectorPath?> cache = new Dictionary<int, VectorPath?>();
        /// <summary>Where glyph-build failures go when no import is running (SpriteFont points this at its own Warnings list); null = drop.</summary>
        internal List<string>? Warnings;
        /// <summary>Short format name for diagnostics ("TrueType", "CFF", "Type 1").</summary>
        internal abstract string Kind { get; }
        /// <summary>" of <family>" for diagnostics, empty when the name is unknown or unreadable.</summary>
        internal string Where() { string f; try { f = FamilyName; } catch (Exception) { f = ""; } return f.Length > 0 ? " of " + f : ""; }
        /// <summary>Number of glyphs in the program (0 when unknown).</summary>
        public int GlyphCount { get; protected set; }
        /// <summary>Glyph outline in em units, y up; null for an empty / missing glyph.</summary>
        [ThreadStatic] static int buildDepth;   // a font object may be shared, so the budget lives per thread
        public VectorPath? Glyph(int gid)
        {
            if (cache.TryGetValue(gid, out var p)) return p;
            if (buildDepth > 8)
            {   // seac (CFF / Type 1) re-enters Glyph from inside Build: without a budget a crafted glyph that
                // references itself recursed to a StackOverflowException, which kills the process
                ImportLog.Swallowed(new InvalidOperationException("glyph build nested more than 8 deep (a seac chain references itself)"), () => Kind + " glyph " + gid + Where(), Warnings);
                return null;
            }
            cache[gid] = null;                     // in-progress mark: a cycle (A seac -> A) resolves to null right here
            buildDepth++;
            try
            {
                p = Build(gid);
            }
            catch (Exception e) { ImportLog.Swallowed(e, () => Kind + " glyph " + gid + Where(), Warnings); p = null; }
            finally { buildDepth--; }
            if (p != null && p.IsEmpty) p = null;
            cache[gid] = p; return p;
        }
        protected abstract VectorPath? Build(int gid);
        /// <summary>Horizontal advance in em units (from the program's metrics; 0.5 when unknown).</summary>
        public virtual float Advance(int gid) => 0.5f;
        /// <summary>Glyph index by PostScript glyph name, -1 when the program has no such name.</summary>
        public virtual int GidByName(string name) => -1;
        /// <summary>Glyph index by Unicode code point (TrueType cmap), -1 when unmapped.</summary>
        public virtual int GidByUnicode(int u) => -1;
        /// <summary>Glyph index through the program's own (built-in) encoding, -1 when it has none / no entry.</summary>
        public virtual int GidByCode(int code) => -1;
        /// <summary>CID-keyed programs: glyph index of a CID (identity otherwise).</summary>
        public virtual int GidByCid(int cid) => cid;
        public virtual bool IsCidKeyed => false;
        /// <summary>True when the program has any cmap / encoding of its own.</summary>
        public virtual bool HasCmap => false;
        /// <summary>Typographic ascent above the baseline in em units (positive; 0.8 when the program has no metrics).</summary>
        public virtual float Ascent => 0.8f;
        /// <summary>Typographic descent below the baseline in em units (positive; 0.2 when unknown).</summary>
        public virtual float Descent => 0.2f;
        /// <summary>Extra line spacing in em units recommended by the font (0 when unknown).</summary>
        public virtual float LineGap => 0f;
        /// <summary>Kerning adjustment (em units, usually negative) between two glyphs; 0 when the font has no pair kerning.</summary>
        public virtual float Kerning(int leftGid, int rightGid) => 0f;
        /// <summary>Family name from the font's own name table ("" when unknown).</summary>
        public virtual string FamilyName => "";
        /// <summary>Style name ("Regular", "Bold Italic", ...) from the name table ("" when unknown).</summary>
        public virtual string StyleName => "";

        /// <summary>Sniffs the data and parses it with the right reader; null when nothing usable is found.</summary>
        public static GlyphFont? Parse(byte[] data, string? hint = null)
        {
            if (data == null || data.Length < 4) return null;
            try
            {
                uint tag = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
                if (tag == 0x00010000 || tag == 0x74727565 /*true*/ || tag == 0x4F54544F /*OTTO*/ || tag == 0x74746366 /*ttcf*/) return new TrueTypeFont(data);
                if (data[0] == '%' && data[1] == '!') return new Type1Font(data);
                if (data[0] == 0x80) return new Type1Font(data);   // PFB
                if (data[0] == 1 && data[1] == 0 && data[2] <= 4) return new CffFont(data, 0, data.Length);
                if (hint == "Type1" || hint == "FontFile") return new Type1Font(data);
                if (hint == "FontFile3" || hint == "Type1C" || hint == "CIDFontType0C") return new CffFont(data, 0, data.Length);
                if (hint == "FontFile2" || hint == "TrueType" || hint == "OpenType") return new TrueTypeFont(data);
                // last guesses
                if (IndexOf(data, "eexec"u8) >= 0) return new Type1Font(data);
                return new CffFont(data, 0, data.Length);
            }
            catch (Exception e) { ImportLog.Swallowed(e, "font program header"); return null; }
        }
        internal static int IndexOf(byte[] hay, ReadOnlySpan<byte> pat) => hay.AsSpan().IndexOf(pat);
    }

    // ============================================================================================ TrueType / OpenType
    internal sealed class TrueTypeFont : GlyphFont
    {
        internal override string Kind => cff != null ? "OpenType/CFF" : "TrueType";
        readonly byte[] d; readonly Dictionary<string, (int off, int len)> tables = new Dictionary<string, (int, int)>();
        readonly float unitsPerEm = 1000; readonly int[]? loca; readonly int glyfOff, glyfLen;
        readonly ushort[]? advances; readonly CffFont? cff;
        Dictionary<int, int>? cmapUni, cmapMac, cmapSym; Dictionary<string, int>? postNames; bool postTried;
        float ascent = 0.8f, descent = 0.2f, lineGap; Dictionary<int, float>? kern; bool kernTried; string? family, style;
        public override float Ascent => ascent;
        public override float Descent => descent;
        public override float LineGap => lineGap;
        public override string FamilyName => family ??= ReadName(1) ?? ReadName(16) ?? "";
        public override string StyleName => style ??= ReadName(2) ?? ReadName(17) ?? "";
        public override float Kerning(int leftGid, int rightGid)
        {
            if (!kernTried) { kernTried = true; try { ParseKern(); } catch (Exception e) { ImportLog.Swallowed(e, () => "TrueType kern table" + Where()); kern = null; } }
            return kern != null && kern.TryGetValue(leftGid << 16 | (rightGid & 0xFFFF), out float v) ? v : 0f;
        }
        /// <summary>The "kern" table (format 0 sub-tables, horizontal). GPOS pair adjustment is not read.</summary>
        void ParseKern()
        {
            if (!tables.TryGetValue("kern", out var kt) || kt.len < 4) return;
            int p = kt.off; int ver = U16(p); int n;
            if (ver == 0) { n = U16(p + 2); p += 4; }
            else { n = (int)U32(p + 4); p += 8; }   // Apple format: version 1.0 (32 bit), nTables 32 bit
            var m = new Dictionary<int, float>();
            for (int t = 0; t < n && p + 6 <= kt.off + kt.len; t++)
            {
                int length, coverage, format, hdr;
                if (ver == 0) { length = U16(p + 2); coverage = U16(p + 4); format = coverage >> 8; hdr = 6; if ((coverage & 1) == 0 || (coverage & 4) != 0) { p += Math.Max(length, hdr); continue; } }
                else { length = (int)U32(p); coverage = U16(p + 4); format = coverage & 0xFF; hdr = 8; if ((coverage & 0x8000) != 0) { p += Math.Max(length, hdr); continue; } }
                if (format == 0)
                {
                    int q = p + hdr; int np = U16(q); q += 8;
                    for (int i = 0; i < np && q + 6 <= d.Length; i++, q += 6)
                    {
                        int l = U16(q), r = U16(q + 2); short v = S16(q + 4);
                        int key = l << 16 | r; if (!m.ContainsKey(key)) m[key] = v / unitsPerEm;
                    }
                }
                p += Math.Max(length, hdr);
            }
            if (m.Count > 0) kern = m;
        }
        string? ReadName(int id)
        {
            if (!tables.TryGetValue("name", out var nt) || nt.len < 6) return null;
            int count = U16(nt.off + 2), strOff = nt.off + U16(nt.off + 4); string? best = null; int bestScore = -1;
            for (int i = 0; i < count; i++)
            {
                int r = nt.off + 6 + i * 12; if (r + 12 > d.Length) break;
                int pid = U16(r), eid = U16(r + 2), lang = U16(r + 4), nid = U16(r + 6), len = U16(r + 8), off = U16(r + 10);
                if (nid != id || strOff + off + len > d.Length) continue;
                int score = pid == 3 && (eid == 1 || eid == 10) ? (lang == 0x409 ? 3 : 2) : pid == 0 ? 2 : pid == 1 && eid == 0 ? 1 : 0;
                if (score <= bestScore) continue;
                string v = pid == 1 ? Encoding.Latin1.GetString(d, strOff + off, len) : Encoding.BigEndianUnicode.GetString(d, strOff + off, len);
                best = v; bestScore = score;
            }
            return best;
        }
        public bool HasGlyf => loca != null && glyfLen > 0;
        public CffFont? Cff => cff;
        public override bool HasCmap => cmapUni != null || cmapMac != null || cmapSym != null;
        public bool HasSymbolCmap => cmapSym != null;
        public bool HasMacCmap => cmapMac != null;
        public override bool IsCidKeyed => cff != null && cff.IsCidKeyed;
        public override int GidByCid(int cid) => cff != null ? cff.GidByCid(cid) : cid;

        public TrueTypeFont(byte[] data) : this(data, 0) { }
        /// <summary><paramref name="index"/> selects a face of a TrueType collection (.ttc); ignored for single fonts.</summary>
        public TrueTypeFont(byte[] data, int index)
        {
            d = data; int off = 0;
            if (U32(0) == 0x74746366) { int nf = (int)U32(8); off = (int)U32(12 + 4 * Math.Clamp(index, 0, Math.Max(0, nf - 1))); }   // collection
            int numTables = U16(off + 4);
            for (int i = 0; i < numTables; i++)
            {
                int r = off + 12 + i * 16; if (r + 16 > d.Length) break;
                string tag = Encoding.Latin1.GetString(d, r, 4); int to = (int)U32(r + 8), tl = (int)U32(r + 12);
                if (to >= 0 && to < d.Length) tables[tag] = (to, Math.Min(tl, d.Length - to));
            }
            if (tables.TryGetValue("head", out var head) && head.len >= 54) { unitsPerEm = U16(head.off + 18); if (unitsPerEm <= 0) unitsPerEm = 1000; }
            int numGlyphs = tables.TryGetValue("maxp", out var maxp) && maxp.len >= 6 ? U16(maxp.off + 4) : 0;
            if (tables.TryGetValue("CFF ", out var cf) && cf.len > 4) { try { cff = new CffFont(d, cf.off, cf.len); } catch (Exception e) { ImportLog.Swallowed(e, () => "OpenType CFF table" + Where()); cff = null; } }
            if (tables.TryGetValue("loca", out var lo) && tables.TryGetValue("glyf", out var gl))
            {
                bool longFmt = head.len >= 54 && (short)U16(head.off + 50) != 0;
                int n = longFmt ? lo.len / 4 : lo.len / 2; if (numGlyphs > 0 && n > numGlyphs + 1) n = numGlyphs + 1;
                loca = new int[n]; for (int i = 0; i < n; i++) loca[i] = longFmt ? (int)U32(lo.off + i * 4) : U16(lo.off + i * 2) * 2;
                glyfOff = gl.off; glyfLen = gl.len; if (numGlyphs == 0) numGlyphs = n - 1;
            }
            GlyphCount = numGlyphs > 0 ? numGlyphs : cff?.GlyphCount ?? 0;
            if (tables.TryGetValue("hhea", out var hh) && tables.TryGetValue("hmtx", out var hm) && hh.len >= 36)
            {
                int nm = U16(hh.off + 34); nm = Math.Min(nm, hm.len / 4);
                advances = new ushort[nm]; for (int i = 0; i < nm; i++) advances[i] = U16(hm.off + i * 4);
            }
            if (tables.TryGetValue("cmap", out var cm)) ParseCmap(cm.off, cm.len);
            // vertical metrics: OS/2 typo metrics when the USE_TYPO_METRICS bit is set or hhea is empty, else hhea (what Windows / browsers use)
            bool have = false;
            if (tables.TryGetValue("OS/2", out var os2) && os2.len >= 72)
            {
                int fsSel = U16(os2.off + 62); short ta = S16(os2.off + 68), td = S16(os2.off + 70); int tg = os2.len >= 74 ? S16(os2.off + 72) : 0;
                if ((fsSel & 0x80) != 0 && ta != 0) { ascent = ta / unitsPerEm; descent = Math.Abs(td) / unitsPerEm; lineGap = Math.Max(0, tg) / unitsPerEm; have = true; }
            }
            if (!have && tables.TryGetValue("hhea", out var hh2) && hh2.len >= 10)
            {
                short ha = S16(hh2.off + 4), hd = S16(hh2.off + 6); int hg = S16(hh2.off + 8);
                if (ha != 0) { ascent = ha / unitsPerEm; descent = Math.Abs(hd) / unitsPerEm; lineGap = Math.Max(0, hg) / unitsPerEm; have = true; }
            }
            if (!have && tables.TryGetValue("OS/2", out var os3) && os3.len >= 72)
            {
                short ta = S16(os3.off + 68), td = S16(os3.off + 70);
                if (ta != 0) { ascent = ta / unitsPerEm; descent = Math.Abs(td) / unitsPerEm; }
            }
        }
        ushort U16(int p) => p + 1 < d.Length ? (ushort)(d[p] << 8 | d[p + 1]) : (ushort)0;
        short S16(int p) => (short)U16(p);
        uint U32(int p) => p + 3 < d.Length ? (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]) : 0u;

        void ParseCmap(int off, int len)
        {
            int n = U16(off + 2);
            for (int i = 0; i < n; i++)
            {
                int r = off + 4 + i * 8; int pid = U16(r), eid = U16(r + 2); int so = (int)U32(r + 4); int sub = off + so; if (sub < 0 || sub >= d.Length) continue;
                Dictionary<int, int>? map = null;
                try { map = ParseSubtable(sub); } catch (Exception e) { ImportLog.Swallowed(e, () => "TrueType cmap subtable " + pid + "/" + eid + Where()); }
                if (map == null || map.Count == 0) continue;
                if (pid == 3 && eid == 0) cmapSym ??= map;
                else if (pid == 3 && (eid == 1 || eid == 10)) { if (cmapUni == null || eid == 10 && map.Count > cmapUni.Count) cmapUni = map; }
                else if (pid == 0) cmapUni ??= map;
                else if (pid == 1 && eid == 0) cmapMac ??= map;
            }
        }
        Dictionary<int, int>? ParseSubtable(int p)
        {
            int fmt = U16(p); var m = new Dictionary<int, int>();
            switch (fmt)
            {
                case 0: for (int c = 0; c < 256; c++) { int g = d[p + 6 + c]; if (g != 0) m[c] = g; } return m;
                case 4:
                    {
                        int segX2 = U16(p + 6), ends = p + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                        for (int s = 0; s < segX2 / 2; s++)
                        {
                            int end = U16(ends + s * 2), start = U16(starts + s * 2), delta = U16(deltas + s * 2), ro = U16(ranges + s * 2);
                            if (start > end) continue; if (end - start > 65535) break;
                            for (int c = start; c <= end && c != 0xFFFF; c++)
                            {
                                int g;
                                if (ro == 0) g = (c + delta) & 0xFFFF;
                                else { int gp = ranges + s * 2 + ro + (c - start) * 2; if (gp + 1 >= d.Length) continue; g = U16(gp); if (g != 0) g = (g + delta) & 0xFFFF; }
                                if (g != 0) m[c] = g;
                            }
                        }
                        return m;
                    }
                case 6: { int first = U16(p + 6), cnt = U16(p + 8); for (int i = 0; i < cnt; i++) { int g = U16(p + 10 + i * 2); if (g != 0) m[first + i] = g; } return m; }
                case 12:
                    {
                        int ngroups = (int)U32(p + 12); int q = p + 16;
                        for (int i = 0; i < ngroups && q + 12 <= d.Length; i++, q += 12)
                        {
                            int sc = (int)U32(q), ec = (int)U32(q + 4), sg = (int)U32(q + 8); if (ec - sc > 65535 || sc < 0) continue;
                            for (int c = sc; c <= ec; c++) m[c] = sg + (c - sc);
                        }
                        return m;
                    }
            }
            return null;
        }
        public override int GidByUnicode(int u) { if (cmapUni != null && cmapUni.TryGetValue(u, out int g)) return g; if (cmapSym != null) { if (cmapSym.TryGetValue(u, out g)) return g; if (u < 256 && cmapSym.TryGetValue(0xF000 | u, out g)) return g; } return -1; }
        /// <summary>Symbolic TrueType lookup: (3,0) at code, F0xx / F1xx / F2xx, then (1,0) at code.</summary>
        public override int GidByCode(int code)
        {
            int g;
            if (cmapSym != null) { if (cmapSym.TryGetValue(code, out g) || cmapSym.TryGetValue(0xF000 | code, out g) || cmapSym.TryGetValue(0xF100 | code, out g) || cmapSym.TryGetValue(0xF200 | code, out g)) return g; }
            if (cmapMac != null && cmapMac.TryGetValue(code, out g)) return g;
            if (cmapSym == null && cmapMac == null && cmapUni != null && cmapUni.TryGetValue(code, out g)) return g;
            return -1;
        }
        public int GidByMacCode(int code) => cmapMac != null && cmapMac.TryGetValue(code, out int g) ? g : -1;
        public override int GidByName(string name)
        {
            if (cff != null) { int g = cff.GidByName(name); if (g >= 0) return g; }
            if (!postTried) { postTried = true; try { ParsePost(); } catch (Exception e) { ImportLog.Swallowed(e, () => "TrueType post table" + Where()); postNames = null; } }
            return postNames != null && postNames.TryGetValue(name, out int gg) ? gg : -1;
        }
        void ParsePost()
        {
            if (!tables.TryGetValue("post", out var po) || po.len < 34) return;
            uint ver = U32(po.off); var mac = VectorFontData.MacGlyphNames;
            if (ver == 0x00010000) { postNames = new Dictionary<string, int>(); for (int i = 0; i < mac.Length; i++) postNames[mac[i]] = i; return; }
            if (ver != 0x00020000) return;
            int n = U16(po.off + 32); var idx = new int[n]; int p = po.off + 34;
            for (int i = 0; i < n; i++) idx[i] = U16(p + i * 2);
            p += n * 2; var names = new List<string>(); int end = po.off + po.len;
            while (p < end && p < d.Length) { int l = d[p]; if (p + 1 + l > d.Length) break; names.Add(Encoding.Latin1.GetString(d, p + 1, l)); p += 1 + l; }
            postNames = new Dictionary<string, int>(n);
            for (int i = 0; i < n; i++) { string? nm = idx[i] < 258 ? mac[idx[i]] : idx[i] - 258 < names.Count ? names[idx[i] - 258] : null; if (nm != null) postNames.TryAdd(nm, i); }
        }
        public override float Advance(int gid)
        {
            if (advances != null && advances.Length > 0) { int i = Math.Min(gid, advances.Length - 1); return advances[i] / unitsPerEm; }
            return cff != null ? cff.Advance(gid) : 0.5f;
        }
        protected override VectorPath? Build(int gid)
        {
            if (cff != null && !HasGlyf) return cff.Glyph(gid);
            if (loca == null) return null;
            var p = new VectorPath(); float s = 1 / unitsPerEm;
            AppendGlyf(gid, p, new Matrix3x2(s, 0, 0, s, 0, 0), 0);
            return p;
        }
        void AppendGlyf(int gid, VectorPath path, Matrix3x2 m, int depth)
        {
            if (loca == null || gid < 0 || gid + 1 >= loca.Length || depth > 8) return;
            int off = glyfOff + loca[gid], end = glyfOff + loca[gid + 1]; if (loca[gid + 1] <= loca[gid] || end > glyfOff + glyfLen || end > d.Length) return;
            int nc = S16(off);
            if (nc >= 0) { SimpleGlyph(off, nc, path, m); return; }
            // composite
            int p = off + 10;
            while (true)
            {
                int flags = U16(p), gi = U16(p + 2); p += 4;
                float dx, dy;
                if ((flags & 1) != 0) { dx = S16(p); dy = S16(p + 2); p += 4; } else { dx = (sbyte)d[p]; dy = (sbyte)d[p + 1]; p += 2; }
                float a = 1, b = 0, c = 0, dd = 1;
                if ((flags & 8) != 0) { a = dd = F2D(p); p += 2; }
                else if ((flags & 0x40) != 0) { a = F2D(p); dd = F2D(p + 2); p += 4; }
                else if ((flags & 0x80) != 0) { a = F2D(p); b = F2D(p + 2); c = F2D(p + 4); dd = F2D(p + 6); p += 8; }
                if ((flags & 2) == 0) { dx = 0; dy = 0; }   // point matching: not supported, place at origin
                var sub = new Matrix3x2(a, b, c, dd, dx, dy) * m;
                AppendGlyf(gi, path, sub, depth + 1);
                if ((flags & 0x20) == 0) break;
            }
        }
        float F2D(int p) => S16(p) / 16384f;
        void SimpleGlyph(int off, int nc, VectorPath path, Matrix3x2 m)
        {
            int p = off + 10; var ends = new int[nc]; for (int i = 0; i < nc; i++) { ends[i] = U16(p); p += 2; }
            int npts = nc > 0 ? ends[nc - 1] + 1 : 0; if (npts <= 0 || npts > 10000) return;
            int il = U16(p); p += 2 + il;
            var flags = new byte[npts];
            for (int i = 0; i < npts;) { if (p >= d.Length) return; byte f = d[p++]; flags[i++] = f; if ((f & 8) != 0) { int r = d[p++]; for (int k = 0; k < r && i < npts; k++) flags[i++] = f; } }
            var xs = new float[npts]; var ys = new float[npts]; float v = 0;
            for (int i = 0; i < npts; i++) { byte f = flags[i]; if ((f & 2) != 0) { int dx = d[p++]; v += (f & 16) != 0 ? dx : -dx; } else if ((f & 16) == 0) { v += S16(p); p += 2; } xs[i] = v; }
            v = 0;
            for (int i = 0; i < npts; i++) { byte f = flags[i]; if ((f & 4) != 0) { int dy = d[p++]; v += (f & 32) != 0 ? dy : -dy; } else if ((f & 32) == 0) { v += S16(p); p += 2; } ys[i] = v; }
            int start = 0;
            for (int c = 0; c < nc; c++)
            {
                int e = ends[c]; if (e < start) { start = e + 1; continue; }
                int n = e - start + 1;
                Vector2 P(int i) => Vector2.Transform(new Vector2(xs[start + (i % n + n) % n], ys[start + (i % n + n) % n]), m);
                bool On(int i) => (flags[start + (i % n + n) % n] & 1) != 0;
                // find a starting on-curve point (or synthesize the midpoint of two off-curve points)
                int s0 = -1; for (int i = 0; i < n; i++) if (On(i)) { s0 = i; break; }
                Vector2 startPt; if (s0 >= 0) startPt = P(s0); else { startPt = (P(0) + P(1)) * 0.5f; s0 = 0; }
                path.MoveTo(startPt.X, startPt.Y);
                Vector2 cur = startPt; Vector2? ctrl = null; int first = s0;
                for (int k = 1; k <= n; k++)
                {
                    int i = first + k; bool on = On(i); var pt = P(i);
                    if (on)
                    {
                        if (ctrl.HasValue) { path.QuadTo(ctrl.Value.X, ctrl.Value.Y, pt.X, pt.Y); ctrl = null; } else path.LineTo(pt.X, pt.Y);
                        cur = pt;
                    }
                    else
                    {
                        if (ctrl.HasValue) { var mid = (ctrl.Value + pt) * 0.5f; path.QuadTo(ctrl.Value.X, ctrl.Value.Y, mid.X, mid.Y); cur = mid; }
                        ctrl = pt;
                    }
                }
                if (ctrl.HasValue) path.QuadTo(ctrl.Value.X, ctrl.Value.Y, startPt.X, startPt.Y);
                path.Close();
                start = e + 1;
            }
        }
    }

    // ============================================================================================ CFF / Type 2
    internal sealed class CffFont : GlyphFont
    {
        internal override string Kind => "CFF";
        readonly byte[] d; readonly int baseOff, endOff;
        int[] charStrings = Array.Empty<int>();   // offsets pairs: start,end interleaved
        int[] gsubrs = Array.Empty<int>(), subrs = Array.Empty<int>();
        string[] strings = Array.Empty<string>();
        int[] charsetSids = Array.Empty<int>();    // gid -> SID (or CID)
        Dictionary<string, int>? nameToGid; Dictionary<int, int>? cidToGid; Dictionary<int, int>? codeToGid;
        readonly bool cid; int[]? fdSelect; int[][]? fdSubrs; float[]? fdDefWidth, fdNomWidth;
        float defaultWidth, nominalWidth; public Matrix3x2 FontMatrix = new Matrix3x2(0.001f, 0, 0, 0.001f, 0, 0);
        public override bool IsCidKeyed => cid;
        public override bool HasCmap => codeToGid != null;

        public CffFont(byte[] data, int off, int len)
        {
            d = data; baseOff = off; endOff = Math.Min(data.Length, off + len);
            int hdrSize = d[off + 2]; int p = off + hdrSize;
            var nameIdx = Index(ref p); var topIdx = Index(ref p); var strIdx = Index(ref p); var gsub = Index(ref p);
            gsubrs = gsub;
            strings = new string[strIdx.Length / 2]; for (int i = 0; i < strings.Length; i++) strings[i] = Encoding.Latin1.GetString(d, strIdx[i * 2], strIdx[i * 2 + 1] - strIdx[i * 2]);
            if (topIdx.Length < 2) throw new InvalidDataException("CFF: no top dict");
            var top = Dict(topIdx[0], topIdx[1]);
            if (top.TryGetValue(0xC07, out var fm) && fm.Count >= 6) FontMatrix = new Matrix3x2((float)fm[0], (float)fm[1], (float)fm[2], (float)fm[3], (float)fm[4], (float)fm[5]);
            cid = top.ContainsKey(0xC1E);
            if (top.TryGetValue(17, out var cs) && cs.Count > 0) { int q = off + (int)cs[0]; charStrings = Index(ref q); }
            GlyphCount = charStrings.Length / 2;
            if (top.TryGetValue(18, out var pv) && pv.Count >= 2) LoadPrivate(off + (int)pv[1], (int)pv[0], out subrs, out defaultWidth, out nominalWidth);
            // charset
            int charsetOff = top.TryGetValue(15, out var cso) && cso.Count > 0 ? (int)cso[0] : 0;
            charsetSids = new int[GlyphCount];
            if (charsetOff > 2) ReadCharset(off + charsetOff);
            else for (int g = 0; g < GlyphCount; g++) charsetSids[g] = g;   // ISOAdobe: sid = gid (Expert charsets treated alike)
            if (cid)
            {
                cidToGid = new Dictionary<int, int>(GlyphCount); for (int g = 0; g < GlyphCount; g++) cidToGid.TryAdd(charsetSids[g], g);
                if (top.TryGetValue(0xC24, out var fda) && fda.Count > 0)
                {
                    int q = off + (int)fda[0]; var fdIdx = Index(ref q); int nfd = fdIdx.Length / 2;
                    fdSubrs = new int[nfd][]; fdDefWidth = new float[nfd]; fdNomWidth = new float[nfd];
                    for (int i = 0; i < nfd; i++)
                    {
                        var fd = Dict(fdIdx[i * 2], fdIdx[i * 2 + 1]);
                        if (fd.TryGetValue(18, out var fpv) && fpv.Count >= 2) LoadPrivate(off + (int)fpv[1], (int)fpv[0], out fdSubrs[i], out fdDefWidth[i], out fdNomWidth[i]);
                        else fdSubrs[i] = Array.Empty<int>();
                    }
                }
                if (top.TryGetValue(0xC25, out var fds) && fds.Count > 0) ReadFdSelect(off + (int)fds[0]);
            }
            else
            {
                int encOff = top.TryGetValue(16, out var eo) && eo.Count > 0 ? (int)eo[0] : 0;
                ReadEncoding(encOff);
            }
        }
        // ---- structures
        int[] Index(ref int p)
        {
            if (p + 2 > endOff) { p += 2; return Array.Empty<int>(); }
            int count = d[p] << 8 | d[p + 1]; p += 2; if (count == 0) return Array.Empty<int>();
            int offSize = d[p++]; int offArr = p; int dataStart = p + (count + 1) * offSize - 1;
            var r = new int[count * 2];
            int Off(int i) { int q = offArr + i * offSize, v = 0; for (int k = 0; k < offSize; k++) v = v << 8 | d[q + k]; return v; }
            for (int i = 0; i < count; i++) { r[i * 2] = dataStart + Off(i); r[i * 2 + 1] = dataStart + Off(i + 1); }
            p = dataStart + Off(count);
            return r;
        }
        Dictionary<int, List<double>> Dict(int s, int e)
        {
            var r = new Dictionary<int, List<double>>(); var ops = new List<double>();
            int p = s;
            while (p < e && p < endOff)
            {
                int b0 = d[p];
                if (b0 <= 21) { int op = b0; p++; if (b0 == 12) { op = 0xC00 | d[p]; p++; } r[op] = new List<double>(ops); ops.Clear(); }
                else if (b0 == 28) { ops.Add((short)(d[p + 1] << 8 | d[p + 2])); p += 3; }
                else if (b0 == 29) { ops.Add(d[p + 1] << 24 | d[p + 2] << 16 | d[p + 3] << 8 | d[p + 4]); p += 5; }
                else if (b0 == 30) { p++; ops.Add(Real(ref p)); }
                else if (b0 >= 32 && b0 <= 246) { ops.Add(b0 - 139); p++; }
                else if (b0 >= 247 && b0 <= 250) { ops.Add((b0 - 247) * 256 + d[p + 1] + 108); p += 2; }
                else if (b0 >= 251 && b0 <= 254) { ops.Add(-(b0 - 251) * 256 - d[p + 1] - 108); p += 2; }
                else p++;
            }
            return r;
        }
        double Real(ref int p)
        {
            var sb = new StringBuilder(); bool done = false;
            while (p < endOff && !done)
            {
                int b = d[p++];
                for (int k = 0; k < 2; k++)
                {
                    int nib = k == 0 ? b >> 4 : b & 15;
                    if (nib <= 9) sb.Append((char)('0' + nib)); else if (nib == 10) sb.Append('.'); else if (nib == 11) sb.Append('E'); else if (nib == 12) sb.Append("E-"); else if (nib == 14) sb.Append('-'); else if (nib == 15) { done = true; break; }
                }
            }
            return double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        void LoadPrivate(int off, int size, out int[] lsubrs, out float defW, out float nomW)
        {
            lsubrs = Array.Empty<int>(); defW = 0; nomW = 0;
            if (off < 0 || off >= endOff) return;
            var pd = Dict(off, Math.Min(endOff, off + size));
            if (pd.TryGetValue(19, out var s) && s.Count > 0) { int q = off + (int)s[0]; lsubrs = Index(ref q); }
            if (pd.TryGetValue(20, out var dw) && dw.Count > 0) defW = (float)dw[0];
            if (pd.TryGetValue(21, out var nw) && nw.Count > 0) nomW = (float)nw[0];
        }
        void ReadCharset(int p)
        {
            int fmt = d[p++]; charsetSids[0] = 0; int g = 1;
            if (fmt == 0) { while (g < GlyphCount && p + 1 < endOff) { charsetSids[g++] = d[p] << 8 | d[p + 1]; p += 2; } }
            else if (fmt == 1 || fmt == 2)
            {
                while (g < GlyphCount && p + 2 < endOff)
                {
                    int first = d[p] << 8 | d[p + 1]; p += 2; int nLeft = fmt == 1 ? d[p++] : (d[p] << 8 | d[p + 1]); if (fmt == 2) p += 2;
                    for (int i = 0; i <= nLeft && g < GlyphCount; i++) charsetSids[g++] = first + i;
                }
            }
        }
        void ReadFdSelect(int p)
        {
            fdSelect = new int[GlyphCount]; int fmt = d[p++];
            if (fmt == 0) { for (int g = 0; g < GlyphCount && p < endOff; g++) fdSelect[g] = d[p++]; }
            else if (fmt == 3)
            {
                int nr = d[p] << 8 | d[p + 1]; p += 2; int first = d[p] << 8 | d[p + 1]; p += 2;
                for (int i = 0; i < nr && p + 2 < endOff; i++) { int fd = d[p++]; int next = d[p] << 8 | d[p + 1]; p += 2; for (int g = first; g < next && g < GlyphCount; g++) if (g >= 0) fdSelect[g] = fd; first = next; }
            }
        }
        void ReadEncoding(int encOff)
        {
            codeToGid = new Dictionary<int, int>(256);
            if (encOff == 0 || encOff == 1)
            {   // standard / expert encoding: code -> name -> gid
                var enc = encOff == 0 ? VectorFontData.StandardEncoding : VectorFontData.MacExpertEncoding;
                for (int c = 0; c < 256; c++) { if (enc[c].Length == 0) continue; int g = GidByName(enc[c]); if (g > 0) codeToGid[c] = g; }
                return;
            }
            int p = baseOff + encOff; int fmt = d[p++];
            if ((fmt & 0x7F) == 0) { int n = d[p++]; for (int i = 1; i <= n && p < endOff; i++) codeToGid[d[p++]] = i; }
            else if ((fmt & 0x7F) == 1)
            {
                int nr = d[p++]; int g = 1;
                for (int i = 0; i < nr && p + 1 < endOff; i++) { int first = d[p], nLeft = d[p + 1]; p += 2; for (int k = 0; k <= nLeft; k++) { if (first + k < 256) codeToGid[first + k] = g; g++; } }
            }
            if ((fmt & 0x80) != 0)
            {
                int ns = d[p++];
                for (int i = 0; i < ns && p + 2 < endOff; i++) { int code = d[p], sid = d[p + 1] << 8 | d[p + 2]; p += 3; int g = GidBySid(sid); if (g > 0) codeToGid[code] = g; }
            }
        }
        int GidBySid(int sid) { for (int g = 0; g < charsetSids.Length; g++) if (charsetSids[g] == sid) return g; return -1; }
        string Sid(int sid) { var std = VectorFontData.CffStandardStrings; return sid < std.Length ? std[sid] : sid - std.Length < strings.Length ? strings[sid - std.Length] : ".s" + sid; }
        public override int GidByName(string name)
        {
            if (cid) return -1;
            if (nameToGid == null) { nameToGid = new Dictionary<string, int>(GlyphCount); for (int g = 0; g < GlyphCount; g++) nameToGid.TryAdd(Sid(charsetSids[g]), g); }
            return nameToGid.TryGetValue(name, out int gid) ? gid : -1;
        }
        public override int GidByCode(int code) => codeToGid != null && codeToGid.TryGetValue(code, out int g) ? g : -1;
        public override int GidByCid(int c) => cidToGid != null ? (cidToGid.TryGetValue(c, out int g) ? g : 0) : c;
        public override float Advance(int gid) { var w = widths ??= new Dictionary<int, float>(); if (!w.TryGetValue(gid, out float a)) { Glyph(gid); if (!w.TryGetValue(gid, out a)) a = 0.5f; } return a; }
        Dictionary<int, float>? widths;

        // ---- Type 2 charstring interpreter
        static int Bias(int n) => n < 1240 ? 107 : n < 33900 ? 1131 : 32768;
        sealed class T2
        {
            public VectorPath Path = new VectorPath(); public float X, Y; public double[] St = new double[48]; public int N; public int Stems; public bool WidthParsed; public float Width;
            public bool Ended, Open;
            public void MoveTo(float x, float y) { if (Open) Path.Close(); Path.MoveTo(x, y); Open = true; }
            public void LineTo(float x, float y) { if (!Open) { Path.MoveTo(X, Y); Open = true; } Path.LineTo(x, y); }
            public void Curve(float x1, float y1, float x2, float y2, float x3, float y3) { if (!Open) { Path.MoveTo(X, Y); Open = true; } Path.CurveTo(x1, y1, x2, y2, x3, y3); }
        }
        protected override VectorPath? Build(int gid)
        {
            if (gid < 0 || gid >= GlyphCount) return null;
            var st = new T2(); int fd = fdSelect != null && gid < fdSelect.Length ? fdSelect[gid] : -1;
            var ls = fd >= 0 && fdSubrs != null && fd < fdSubrs.Length ? fdSubrs[fd] : subrs;
            float nomW = fd >= 0 && fdNomWidth != null && fd < fdNomWidth.Length ? fdNomWidth[fd] : nominalWidth;
            float defW = fd >= 0 && fdDefWidth != null && fd < fdDefWidth.Length ? fdDefWidth[fd] : defaultWidth;
            st.Width = defW;
            Run(charStrings[gid * 2], charStrings[gid * 2 + 1], st, ls, 0, nomW);
            if (st.Open) st.Path.Close();
            (widths ??= new Dictionary<int, float>())[gid] = Vector2.TransformNormal(new Vector2(st.Width, 0), FontMatrix).X;
            return st.Path.Transformed(FontMatrix);
        }
        void Run(int p, int e, T2 s, int[] ls, int depth, float nomW)
        {
            if (depth > 10) return;
            var st = s.St;
            while (p < e && p < endOff)
            {
                int b0 = d[p++];
                if (b0 >= 32 || b0 == 28)
                {
                    double v;
                    if (b0 == 28) { v = (short)(d[p] << 8 | d[p + 1]); p += 2; }
                    else if (b0 <= 246) v = b0 - 139;
                    else if (b0 <= 250) { v = (b0 - 247) * 256 + d[p] + 108; p++; }
                    else if (b0 <= 254) { v = -(b0 - 251) * 256 - d[p] - 108; p++; }
                    else { v = (int)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]) / 65536.0; p += 4; }
                    if (s.N < st.Length) st[s.N++] = v;
                    continue;
                }
                int n = s.N;
                switch (b0)
                {
                    case 1: case 3: case 18: case 23:   // stems
                        if ((n & 1) != 0 && !s.WidthParsed) s.Width = nomW + (float)st[0];
                        s.WidthParsed = true; s.Stems += n / 2; s.N = 0; break;
                    case 19: case 20:                    // hintmask
                        if ((n & 1) != 0 && !s.WidthParsed) s.Width = nomW + (float)st[0];
                        s.WidthParsed = true; s.Stems += n / 2; s.N = 0; p += (s.Stems + 7) / 8; break;
                    case 21: // rmoveto
                        { int i = 0; if (n > 2 && !s.WidthParsed) { s.Width = nomW + (float)st[0]; i = 1; } s.WidthParsed = true; s.X += (float)st[i]; s.Y += (float)st[i + 1]; s.MoveTo(s.X, s.Y); s.N = 0; break; }
                    case 22: // hmoveto
                        { int i = 0; if (n > 1 && !s.WidthParsed) { s.Width = nomW + (float)st[0]; i = 1; } s.WidthParsed = true; s.X += (float)st[i]; s.MoveTo(s.X, s.Y); s.N = 0; break; }
                    case 4:  // vmoveto
                        { int i = 0; if (n > 1 && !s.WidthParsed) { s.Width = nomW + (float)st[0]; i = 1; } s.WidthParsed = true; s.Y += (float)st[i]; s.MoveTo(s.X, s.Y); s.N = 0; break; }
                    case 5: for (int i = 0; i + 1 < n; i += 2) { s.X += (float)st[i]; s.Y += (float)st[i + 1]; s.LineTo(s.X, s.Y); } s.N = 0; break;
                    case 6: case 7: // hlineto / vlineto alternate
                        { bool horiz = b0 == 6; for (int i = 0; i < n; i++) { if (horiz) s.X += (float)st[i]; else s.Y += (float)st[i]; s.LineTo(s.X, s.Y); horiz = !horiz; } s.N = 0; break; }
                    case 8: for (int i = 0; i + 5 < n; i += 6) RR(s, st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]); s.N = 0; break;
                    case 24: { int i = 0; for (; i + 5 < n - 2; i += 6) RR(s, st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]); if (i + 1 < n) { s.X += (float)st[i]; s.Y += (float)st[i + 1]; s.LineTo(s.X, s.Y); } s.N = 0; break; }
                    case 25: { int i = 0; for (; i + 1 < n - 6; i += 2) { s.X += (float)st[i]; s.Y += (float)st[i + 1]; s.LineTo(s.X, s.Y); } if (i + 5 < n) RR(s, st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]); s.N = 0; break; }
                    case 26: case 27: // vvcurveto / hhcurveto
                        {
                            int i = 0; double d1 = 0; if ((n & 1) != 0) { d1 = st[0]; i = 1; }
                            for (; i + 3 < n; i += 4)
                            {
                                if (b0 == 26) RR(s, d1, st[i], st[i + 1], st[i + 2], 0, st[i + 3]);
                                else RR(s, st[i], d1, st[i + 1], st[i + 2], st[i + 3], 0);
                                d1 = 0;
                            }
                            s.N = 0; break;
                        }
                    case 30: case 31: // vhcurveto / hvcurveto
                        {
                            bool horiz = b0 == 31; int i = 0;
                            while (i + 3 < n)
                            {
                                bool last = i + 8 > n; double dlast = last && i + 4 < n ? st[i + 4] : 0;
                                if (horiz) RR(s, st[i], 0, st[i + 1], st[i + 2], dlast, st[i + 3]);
                                else RR(s, 0, st[i], st[i + 1], st[i + 2], st[i + 3], dlast);
                                horiz = !horiz; i += 4;
                            }
                            s.N = 0; break;
                        }
                    case 10: // callsubr
                        { if (n < 1) break; int idx = (int)st[--s.N] + Bias(ls.Length / 2); if (idx >= 0 && idx < ls.Length / 2) Run(ls[idx * 2], ls[idx * 2 + 1], s, ls, depth + 1, nomW); if (s.Ended) return; break; }
                    case 29: // callgsubr
                        { if (n < 1) break; int idx = (int)st[--s.N] + Bias(gsubrs.Length / 2); if (idx >= 0 && idx < gsubrs.Length / 2) Run(gsubrs[idx * 2], gsubrs[idx * 2 + 1], s, ls, depth + 1, nomW); if (s.Ended) return; break; }
                    case 11: return;   // return
                    case 14: // endchar
                        {
                            int i = 0; if ((n == 1 || n == 5) && !s.WidthParsed) { s.Width = nomW + (float)st[0]; i = 1; } s.WidthParsed = true;
                            if (n - i >= 4) Seac(s, (float)st[i], (float)st[i + 1], (int)st[i + 2], (int)st[i + 3]);
                            if (s.Open) { s.Path.Close(); s.Open = false; }
                            s.N = 0; s.Ended = true; return;
                        }
                    case 12:
                        {
                            int b1 = d[p++];
                            switch (b1)
                            {
                                case 35: // flex
                                    if (n >= 13) { RR(s, st[0], st[1], st[2], st[3], st[4], st[5]); RR(s, st[6], st[7], st[8], st[9], st[10], st[11]); }
                                    s.N = 0; break;
                                case 34: // hflex
                                    if (n >= 7) { float y0 = s.Y; RR(s, st[0], 0, st[1], st[2], st[3], 0); RR(s, st[4], 0, st[5], y0 - s.Y, st[6], 0); }
                                    s.N = 0; break;
                                case 36: // hflex1
                                    if (n >= 9) { float y0 = s.Y; RR(s, st[0], st[1], st[2], st[3], st[4], 0); RR(s, st[5], 0, st[6], st[7], st[8], y0 - (s.Y + st[7])); }
                                    s.N = 0; break;
                                case 37: // flex1
                                    if (n >= 11)
                                    {
                                        float sx = s.X, sy = s.Y; double dx = 0, dy = 0; for (int k = 0; k < 10; k += 2) { dx += st[k]; dy += st[k + 1]; }
                                        RR(s, st[0], st[1], st[2], st[3], st[4], st[5]);
                                        // last point: d11 applies to the larger delta; the other coordinate returns to the start
                                        float c1x = s.X + (float)st[6], c1y = s.Y + (float)st[7], c2x = c1x + (float)st[8], c2y = c1y + (float)st[9];
                                        float ex, ey; if (Math.Abs(dx) > Math.Abs(dy)) { ex = c2x + (float)st[10]; ey = sy; } else { ex = sx; ey = c2y + (float)st[10]; }
                                        s.Curve(c1x, c1y, c2x, c2y, ex, ey); s.X = ex; s.Y = ey;
                                    }
                                    s.N = 0; break;
                                case 3: case 4: case 5: case 9: case 10: case 11: case 12: case 14: case 15: case 18: case 21: case 22: case 23: case 24: case 26: case 27: case 28: case 29: case 30:
                                    s.N = 0; break;   // arithmetic / logic operators: unsupported, drop the stack
                                default: s.N = 0; break;
                            }
                            break;
                        }
                    default: s.N = 0; break;
                }
            }
        }
        static void RR(T2 s, double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            float c1x = s.X + (float)dx1, c1y = s.Y + (float)dy1, c2x = c1x + (float)dx2, c2y = c1y + (float)dy2; float ex = c2x + (float)dx3, ey = c2y + (float)dy3;
            s.Curve(c1x, c1y, c2x, c2y, ex, ey); s.X = ex; s.Y = ey;
        }
        void Seac(T2 s, float adx, float ady, int bchar, int achar)
        {
            var std = VectorFontData.StandardEncoding; if (bchar < 0 || bchar > 255 || achar < 0 || achar > 255) return;
            int bg = GidByName(std[bchar]), ag = GidByName(std[achar]); if (bg < 0 || ag < 0) return;
            Matrix3x2.Invert(FontMatrix, out var inv);
            var bp = Glyph(bg); var ap = Glyph(ag);
            if (s.Open) { s.Path.Close(); s.Open = false; }
            if (bp != null) s.Path.Append(bp, inv);
            if (ap != null) s.Path.Append(ap, inv * Matrix3x2.CreateTranslation(adx, ady));
        }
    }

    // ============================================================================================ Type 1
    internal sealed class Type1Font : GlyphFont
    {
        internal override string Kind => "Type 1";
        readonly Dictionary<string, byte[]> chars = new Dictionary<string, byte[]>();
        readonly List<byte[]> subrs = new List<byte[]>();
        readonly List<string> names = new List<string>();           // gid order = CharStrings order
        readonly Dictionary<string, int> gidByName = new Dictionary<string, int>();
        readonly string[]? encoding;                                 // built-in encoding (code -> name)
        readonly Dictionary<int, float> widths = new Dictionary<int, float>();
        public Matrix3x2 FontMatrix = new Matrix3x2(0.001f, 0, 0, 0.001f, 0, 0);
        public override bool HasCmap => encoding != null;

        public Type1Font(byte[] data)
        {
            names.Add(".notdef"); gidByName[".notdef"] = 0; chars[".notdef"] = Array.Empty<byte>();   // gid 0 = .notdef, as in every other format
            if (data.Length > 6 && data[0] == 0x80)
            {   // PFB segments
                using var ms = new MemoryStream(); int p = 0;
                while (p + 6 <= data.Length && data[p] == 0x80 && data[p + 1] != 3) { int len = data[p + 2] | data[p + 3] << 8 | data[p + 4] << 16 | data[p + 5] << 24; p += 6; ms.Write(data, p, Math.Min(len, data.Length - p)); p += len; }
                data = ms.ToArray();
            }
            int ee = IndexOf(data, "eexec"u8);
            string clear = Encoding.Latin1.GetString(data, 0, ee < 0 ? data.Length : ee);
            var fm = System.Text.RegularExpressions.Regex.Match(clear, @"/FontMatrix\s*\[([^\]]*)\]");
            if (fm.Success) { var v = fm.Groups[1].Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); if (v.Length >= 6) FontMatrix = new Matrix3x2(F(v[0]), F(v[1]), F(v[2]), F(v[3]), F(v[4]), F(v[5])); }
            if (clear.Contains("/Encoding StandardEncoding")) encoding = VectorFontData.StandardEncoding;
            else if (clear.Contains("/Encoding"))
            {
                var enc = new string[256];
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(clear, @"dup\s+(\d+)\s*/(\S+)\s+put")) { int c = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); if (c >= 0 && c < 256) enc[c] = m.Groups[2].Value; }
                for (int i = 0; i < 256; i++) enc[i] ??= "";
                encoding = enc;
            }
            if (ee < 0) return;
            int q = ee + 5; while (q < data.Length && (data[q] == '\r' || data[q] == '\n' || data[q] == ' ' || data[q] == '\t')) q++;
            byte[] enc2 = data.AsSpan(q).ToArray();
            bool hex = true; for (int i = 0; i < 4 && i < enc2.Length; i++) if (PsScanner.Hex(enc2[i]) < 0) { hex = false; break; }
            if (hex) { var o = new List<byte>(enc2.Length / 2); int hi = -1; foreach (var b in enc2) { int v = PsScanner.Hex(b); if (v < 0) continue; if (hi < 0) hi = v; else { o.Add((byte)(hi * 16 + v)); hi = -1; } } enc2 = o.ToArray(); }
            var priv = Decrypt(enc2, 55665, 4);
            ParsePrivate(priv);
        }
        static float F(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        static byte[] Decrypt(byte[] d, ushort r, int skip)
        {
            const ushort c1 = 52845, c2 = 22719; var o = new byte[Math.Max(0, d.Length - skip)];
            for (int i = 0; i < d.Length; i++) { byte c = d[i]; byte pl = (byte)(c ^ (r >> 8)); r = (ushort)((c + r) * c1 + c2); if (i >= skip) o[i - skip] = pl; }
            return o;
        }
        void ParsePrivate(byte[] b)
        {
            int lenIV = 4; int li = IndexOf(b, "/lenIV"u8); if (li >= 0) { int p = li + 6; lenIV = ReadInt(b, ref p); }
            int sp = IndexOf(b, "/Subrs"u8);
            if (sp >= 0)
            {
                int p = sp + 6; int count = ReadInt(b, ref p);
                for (int i = 0; i < count; i++)
                {
                    int dp = IndexOfFrom(b, "dup "u8, p); if (dp < 0) break; p = dp + 4;
                    int idx = ReadInt(b, ref p); int len = ReadInt(b, ref p);
                    SkipToken(b, ref p); p++;   // RD / -| then one space
                    if (p + len > b.Length || len < 0) break;
                    var cs = Decrypt(b.AsSpan(p, len).ToArray(), 4330, lenIV);
                    while (subrs.Count <= idx) subrs.Add(Array.Empty<byte>());
                    if (idx >= 0) subrs[idx] = cs;
                    p += len;
                }
            }
            int cp = IndexOf(b, "/CharStrings"u8);
            if (cp < 0) return;
            int pp = cp + 12; ReadInt(b, ref pp);
            int begin = IndexOfFrom(b, "begin"u8, pp); if (begin >= 0) pp = begin + 5;
            while (pp < b.Length)
            {
                while (pp < b.Length && b[pp] != '/') { if (b[pp] == 'e' && pp + 3 <= b.Length && b[pp + 1] == 'n' && b[pp + 2] == 'd' && (pp == 0 || PdfLexer.Ws(b[pp - 1]))) return; pp++; }
                if (pp >= b.Length) break;
                pp++; int ns = pp; while (pp < b.Length && !PdfLexer.Ws(b[pp]) && b[pp] != '{' && b[pp] != '(' && b[pp] != '/') pp++;
                string name = Encoding.Latin1.GetString(b, ns, pp - ns);
                int save = pp; int len = ReadInt(b, ref pp); if (len < 0 || pp >= b.Length) { pp = save; continue; }
                SkipToken(b, ref pp); pp++;
                if (pp + len > b.Length) break;
                var cs = Decrypt(b.AsSpan(pp, len).ToArray(), 4330, lenIV);
                if (!gidByName.ContainsKey(name)) { gidByName[name] = names.Count; names.Add(name); chars[name] = cs; GlyphCount = names.Count; }
                else if (name == ".notdef") chars[name] = cs;
                pp += len;
            }
            GlyphCount = names.Count;
        }
        static int IndexOfFrom(byte[] b, ReadOnlySpan<byte> pat, int from) { if (from >= b.Length) return -1; int i = b.AsSpan(from).IndexOf(pat); return i < 0 ? -1 : i + from; }
        static int ReadInt(byte[] b, ref int p)
        {
            while (p < b.Length && !(b[p] >= '0' && b[p] <= '9') && b[p] != '-') { if (b[p] == '/' || b[p] == '{') return -1; p++; }
            int s = p; if (p < b.Length && b[p] == '-') p++; while (p < b.Length && b[p] >= '0' && b[p] <= '9') p++;
            return int.TryParse(Encoding.Latin1.GetString(b, s, p - s), out int v) ? v : -1;
        }
        static void SkipToken(byte[] b, ref int p) { while (p < b.Length && PdfLexer.Ws(b[p])) p++; while (p < b.Length && !PdfLexer.Ws(b[p])) p++; }

        public override int GidByName(string name) => gidByName.TryGetValue(name, out int g) ? g : -1;
        public override int GidByCode(int code) => encoding != null && code >= 0 && code < 256 && encoding[code].Length > 0 ? GidByName(encoding[code]) : -1;
        public string? BuiltinName(int code) => encoding != null && code >= 0 && code < 256 && encoding[code].Length > 0 ? encoding[code] : null;
        public override float Advance(int gid) { if (!widths.TryGetValue(gid, out float w)) { Glyph(gid); if (!widths.TryGetValue(gid, out w)) w = 0.5f; } return w; }

        sealed class T1
        {
            public VectorPath Path = new VectorPath(); public float X, Y, Sbx, Sby, Width; public double[] St = new double[48]; public int N; public bool Open;
            public double[] Ps = new double[48]; public int NPs;                  // PostScript operand stack for othersubr results
            public int Flex; public List<float> FlexPts = new List<float>();
            public void MoveTo(float x, float y) { if (Open) Path.Close(); Path.MoveTo(x, y); Open = true; }
            public void LineTo(float x, float y) { if (!Open) { Path.MoveTo(X, Y); Open = true; } Path.LineTo(x, y); }
            public void Curve(float x1, float y1, float x2, float y2, float x3, float y3) { if (!Open) { Path.MoveTo(X, Y); Open = true; } Path.CurveTo(x1, y1, x2, y2, x3, y3); }
        }
        protected override VectorPath? Build(int gid)
        {
            if (gid < 0 || gid >= names.Count) return null;
            var s = new T1(); Run(chars[names[gid]], s, 0);
            if (s.Open) s.Path.Close();
            widths[gid] = Vector2.TransformNormal(new Vector2(s.Width, 0), FontMatrix).X;
            return s.Path.Transformed(FontMatrix);
        }
        bool Run(byte[] cs, T1 s, int depth)
        {
            if (depth > 15) return false;
            var st = s.St; int p = 0;
            while (p < cs.Length)
            {
                int b0 = cs[p++];
                if (b0 >= 32)
                {
                    double v;
                    if (b0 <= 246) v = b0 - 139;
                    else if (b0 <= 250) { v = (b0 - 247) * 256 + cs[p] + 108; p++; }
                    else if (b0 <= 254) { v = -(b0 - 251) * 256 - cs[p] - 108; p++; }
                    else { v = (int)(cs[p] << 24 | cs[p + 1] << 16 | cs[p + 2] << 8 | cs[p + 3]); p += 4; }
                    if (s.N < st.Length) st[s.N++] = v;
                    continue;
                }
                int n = s.N;
                switch (b0)
                {
                    case 13: if (n >= 2) { s.Sbx = (float)st[0]; s.Width = (float)st[1]; s.X = s.Sbx; s.Y = 0; } s.N = 0; break;   // hsbw
                    case 9: if (s.Open) { s.Path.Close(); s.Open = false; } s.N = 0; break;   // closepath
                    case 1: case 3: s.N = 0; break;
                    case 21: if (n >= 2) Move(s, (float)st[n - 2], (float)st[n - 1]); s.N = 0; break;
                    case 22: if (n >= 1) Move(s, (float)st[n - 1], 0); s.N = 0; break;
                    case 4: if (n >= 1) Move(s, 0, (float)st[n - 1]); s.N = 0; break;
                    case 5: if (n >= 2) { s.X += (float)st[0]; s.Y += (float)st[1]; s.LineTo(s.X, s.Y); } s.N = 0; break;
                    case 6: if (n >= 1) { s.X += (float)st[0]; s.LineTo(s.X, s.Y); } s.N = 0; break;
                    case 7: if (n >= 1) { s.Y += (float)st[0]; s.LineTo(s.X, s.Y); } s.N = 0; break;
                    case 8: if (n >= 6) RR(s, st[0], st[1], st[2], st[3], st[4], st[5]); s.N = 0; break;
                    case 30: if (n >= 4) RR(s, 0, st[0], st[1], st[2], st[3], 0); s.N = 0; break;   // vhcurveto
                    case 31: if (n >= 4) RR(s, st[0], 0, st[1], st[2], 0, st[3]); s.N = 0; break;   // hvcurveto
                    case 10: // callsubr
                        {
                            if (n < 1) break; int idx = (int)st[--s.N];
                            // flex / hint replacement done through the standard subrs 0-3 are handled by callothersubr below
                            if (idx >= 0 && idx < subrs.Count && subrs[idx].Length > 0) { if (!Run(subrs[idx], s, depth + 1)) return false; }
                            break;
                        }
                    case 11: return true;
                    case 14: if (s.Open) { s.Path.Close(); s.Open = false; } return false;   // endchar
                    case 12:
                        {
                            int b1 = cs[p++];
                            switch (b1)
                            {
                                case 0: s.N = 0; break;                       // dotsection
                                case 1: case 2: s.N = 0; break;               // vstem3 hstem3
                                case 6: // seac
                                    if (n >= 5) { Seac(s, (float)st[0], (float)st[1], (float)st[2], (int)st[3], (int)st[4]); s.N = 0; return false; }
                                    s.N = 0; break;
                                case 7: if (n >= 4) { s.Sbx = (float)st[0]; s.Sby = (float)st[1]; s.Width = (float)st[2]; s.X = s.Sbx; s.Y = s.Sby; } s.N = 0; break;   // sbw
                                case 12: { if (n >= 2) { double a = st[n - 2], b = st[n - 1]; st[n - 2] = b == 0 ? 0 : a / b; s.N = n - 1; } break; }   // div
                                case 16: // callothersubr
                                    {
                                        if (n < 2) { s.N = 0; break; }
                                        int on = (int)st[n - 1], na = (int)st[n - 2]; int first = n - 2 - na; if (first < 0) { first = 0; na = n - 2; }
                                        switch (on)
                                        {
                                            case 1: s.Flex = 1; s.FlexPts.Clear(); break;
                                            case 0:
                                                {   // flex end: 7 collected points (reference + 6 controls), current point from the last
                                                    s.Flex = 0; var f = s.FlexPts;
                                                    if (f.Count >= 14) { s.Curve(f[2], f[3], f[4], f[5], f[6], f[7]); s.Curve(f[8], f[9], f[10], f[11], f[12], f[13]); s.X = f[12]; s.Y = f[13]; }
                                                    s.NPs = 0; s.Ps[s.NPs++] = s.Y; s.Ps[s.NPs++] = s.X;   // the two following "pop"s get end x, y
                                                    break;
                                                }
                                            case 3: s.NPs = 0; s.Ps[s.NPs++] = 3; break;
                                            default: s.NPs = 0; for (int i = na - 1; i >= 0; i--) s.Ps[s.NPs++] = st[first + i]; break;   // unknown: pops return the arguments
                                        }
                                        s.N = first; break;
                                    }
                                case 17: if (s.NPs > 0) st[s.N++] = s.Ps[--s.NPs]; else st[s.N++] = 0; break;   // pop
                                case 33: if (n >= 2) { s.X = (float)st[0]; s.Y = (float)st[1]; } s.N = 0; break;   // setcurrentpoint
                                default: s.N = 0; break;
                            }
                            break;
                        }
                    default: s.N = 0; break;
                }
            }
            return true;
        }
        static void Move(T1 s, float dx, float dy)
        {
            s.X += dx; s.Y += dy;
            if (s.Flex > 0) { s.FlexPts.Add(s.X); s.FlexPts.Add(s.Y); return; }
            s.MoveTo(s.X, s.Y);
        }
        static void RR(T1 s, double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            float c1x = s.X + (float)dx1, c1y = s.Y + (float)dy1, c2x = c1x + (float)dx2, c2y = c1y + (float)dy2, ex = c2x + (float)dx3, ey = c2y + (float)dy3;
            s.Curve(c1x, c1y, c2x, c2y, ex, ey); s.X = ex; s.Y = ey;
        }
        void Seac(T1 s, float asb, float adx, float ady, int bchar, int achar)
        {
            var std = VectorFontData.StandardEncoding; if (bchar < 0 || bchar > 255 || achar < 0 || achar > 255) return;
            int bg = GidByName(std[bchar]), ag = GidByName(std[achar]); if (bg < 0 || ag < 0) return;
            if (s.Open) { s.Path.Close(); s.Open = false; }
            Matrix3x2.Invert(FontMatrix, out var inv);
            var bp = Glyph(bg); var ap = Glyph(ag);
            if (bp != null) s.Path.Append(bp, inv);
            if (ap != null) s.Path.Append(ap, inv * Matrix3x2.CreateTranslation(s.Sbx - asb + adx, ady));
        }
    }

    // ============================================================================================ system / fallback fonts
    /// <summary>
    /// Substitute fonts for text whose program is not embedded (the standard 14 and other non-embedded fonts): TrueType files
    /// found in <see cref="Directories"/> (the Windows Fonts folder, /usr/share/fonts, ~/.fonts). Arial / Times / Courier
    /// (Liberation or DejaVu on Linux) stand in for Helvetica / Times / Courier; other families are looked up by file name
    /// first. Add directories, <see cref="Register"/> your own font data by family name, or set <see cref="Substitute"/> to
    /// pick files yourself. Metrics still come from the PDF (/Widths), so the layout stays right even with a stand-in.
    /// </summary>
    internal static class VectorFonts
    {
        public static readonly List<string> Directories = new List<string>();
        /// <summary>Custom resolver: (family, bold, italic, class "sans" / "serif" / "mono" / "symbol") -> font file path or null.</summary>
        public static Func<string, bool, bool, string, string?>? Substitute { get; set; }
        static readonly Dictionary<string, byte[]> registered = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string>? index; static readonly Dictionary<string, GlyphFont?> loaded = new Dictionary<string, GlyphFont?>(StringComparer.OrdinalIgnoreCase);
        static readonly object sync = new object();

        static VectorFonts()
        {
            try { string f = Environment.GetFolderPath(Environment.SpecialFolder.Fonts); if (!string.IsNullOrEmpty(f)) Directories.Add(f); } catch (Exception e) { ImportLog.Swallowed(e, "system font folder"); }
            Directories.Add("/usr/share/fonts"); Directories.Add("/usr/local/share/fonts");
            try { string h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); if (!string.IsNullOrEmpty(h)) { Directories.Add(Path.Combine(h, ".fonts")); Directories.Add(Path.Combine(h, ".local", "share", "fonts")); Directories.Add(Path.Combine(h, "Library", "Fonts")); } } catch (Exception e) { ImportLog.Swallowed(e, "user font folders"); }
            Directories.Add("/Library/Fonts"); Directories.Add("/System/Library/Fonts");
        }
        /// <summary>Registers font data (TrueType / OpenType / CFF / Type1) for a family name, e.g. Register("Helvetica", File.ReadAllBytes("arial.ttf")).</summary>
        public static void Register(string family, byte[] data) { lock (sync) { registered[Normalize(family)] = data; loaded.Remove("reg:" + Normalize(family)); } }
        static string Normalize(string s) { var sb = new StringBuilder(); foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch)); return sb.ToString(); }

        static Dictionary<string, string> Index()
        {
            if (index != null) return index;
            var ix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directories)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant(); if (ext != ".ttf" && ext != ".otf" && ext != ".ttc" && ext != ".pfb") continue;
                        ix.TryAdd(Path.GetFileNameWithoutExtension(f), f);
                    }
                }
                catch (Exception e) { ImportLog.Swallowed(e, "font directory scan " + dir); }
            }
            return index = ix;
        }
        /// <summary>Forget the directory scan (call after adding directories at run time).</summary>
        public static void Rescan() { lock (sync) { index = null; loaded.Clear(); } }

        /// <summary>A stand-in for a PDF / PostScript base font name; flags = PDF font descriptor flags (serif 2, symbolic 4, italic 64, force bold 1&lt;&lt;18).</summary>
        public static GlyphFont? Find(string? baseFont, int flags = 0)
        {
            string name = baseFont ?? "Helvetica"; int plus = name.IndexOf('+'); if (plus == 6 && name.Length > 7) name = name.Substring(7);
            string lower = name.ToLowerInvariant();
            bool bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold") || (flags & (1 << 18)) != 0;
            bool italic = lower.Contains("italic") || lower.Contains("oblique") || (flags & 64) != 0;
            string family = name; int cut = family.IndexOfAny(new[] { ',', '-' }); if (cut > 0) family = family.Substring(0, cut);
            family = family.Replace("PSMT", "").Replace("MT", "").Trim();
            string cls = lower.Contains("courier") || lower.Contains("mono") ? "mono" : lower.Contains("symbol") ? "symbol" : lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("georgia") || lower.Contains("garamond") || lower.Contains("book") || lower.Contains("roman") || lower.Contains("cambria") || lower.Contains("palatino") || lower.Contains("century") ? "serif" : (flags & 2) != 0 && !(lower.Contains("arial") || lower.Contains("helvetica") || lower.Contains("verdana") || lower.Contains("calibri") || lower.Contains("tahoma") || lower.Contains("segoe")) ? "serif" : "sans";
            string key = $"{Normalize(family)}|{(bold ? 1 : 0)}{(italic ? 1 : 0)}|{cls}";
            lock (sync)
            {
                if (loaded.TryGetValue(key, out var f)) return f;
                f = Load(family, bold, italic, cls);
                loaded[key] = f; return f;
            }
        }
        static GlyphFont? Load(string family, bool bold, bool italic, string cls)
        {
            string nf = Normalize(family);
            if (registered.TryGetValue(nf, out var data)) return GlyphFont.Parse(data);
            if (Substitute != null) { string? p = Substitute(family, bold, italic, cls); if (p != null && File.Exists(p)) return GlyphFont.Parse(File.ReadAllBytes(p)); }
            var ix = Index();
            var cands = new List<string>();
            string sfx = bold && italic ? "bi" : bold ? "bd" : italic ? "i" : "";
            string sfx2 = bold && italic ? "z" : bold ? "b" : italic ? "i" : "";
            string pretty = family.Replace(" ", "");
            // 1. the family itself (Windows naming: arial, arialbd, ariali, arialbi / verdana, verdanab, verdanai, verdanaz)
            cands.Add(pretty + sfx); cands.Add(pretty + sfx2); cands.Add(pretty + "-" + (bold && italic ? "BoldItalic" : bold ? "Bold" : italic ? "Italic" : "Regular")); cands.Add(pretty);
            // 2. class defaults
            string[] win, lin, dv;
            switch (cls)
            {
                case "mono": win = new[] { "cour", "courbd", "couri", "courbi" }; lin = new[] { "LiberationMono-Regular", "LiberationMono-Bold", "LiberationMono-Italic", "LiberationMono-BoldItalic" }; dv = new[] { "DejaVuSansMono", "DejaVuSansMono-Bold", "DejaVuSansMono-Oblique", "DejaVuSansMono-BoldOblique" }; break;
                case "serif": win = new[] { "times", "timesbd", "timesi", "timesbi" }; lin = new[] { "LiberationSerif-Regular", "LiberationSerif-Bold", "LiberationSerif-Italic", "LiberationSerif-BoldItalic" }; dv = new[] { "DejaVuSerif", "DejaVuSerif-Bold", "DejaVuSerif-Italic", "DejaVuSerif-BoldItalic" }; break;
                case "symbol": win = new[] { "symbol", "symbol", "symbol", "symbol" }; lin = new[] { "StandardSymbolsPS", "StandardSymbolsPS", "StandardSymbolsPS", "StandardSymbolsPS" }; dv = new[] { "DejaVuSans", "DejaVuSans-Bold", "DejaVuSans-Oblique", "DejaVuSans-BoldOblique" }; break;
                default: win = new[] { "arial", "arialbd", "ariali", "arialbi" }; lin = new[] { "LiberationSans-Regular", "LiberationSans-Bold", "LiberationSans-Italic", "LiberationSans-BoldItalic" }; dv = new[] { "DejaVuSans", "DejaVuSans-Bold", "DejaVuSans-Oblique", "DejaVuSans-BoldOblique" }; break;
            }
            int v = bold && italic ? 3 : bold ? 1 : italic ? 2 : 0;
            cands.Add(win[v]); cands.Add(lin[v]); cands.Add(dv[v]); cands.Add(win[0]); cands.Add(lin[0]); cands.Add(dv[0]);
            foreach (var c in cands)
                if (ix.TryGetValue(c, out var path)) { try { var f = GlyphFont.Parse(File.ReadAllBytes(path)); if (f != null && f.GlyphCount > 0) return f; } catch (Exception e) { ImportLog.Swallowed(e, "reading font file " + path); } }
            return null;
        }
    }
}
