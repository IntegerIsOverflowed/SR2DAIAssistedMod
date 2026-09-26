using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Sr2d64CSport
{
    /// <summary>
    /// "Show code" for the demo: the C# of the selected test, lifted straight out of Tests.cs (embedded in the exe as a
    /// resource, so it is always the code that is really running) - the test's render lambda, the Tests helpers it calls
    /// (Grid, SrcFor, ...) and, for the control demos, the Build() method that makes the control strip. Shown in a
    /// SpriteTextView coloured by <see cref="Colorize"/> (a small C# highlighter, VS dark colours). DemoTest.Code overrides the
    /// extraction with hand-written text.
    /// </summary>
    internal static class CodeView
    {
        // ------------------------------------------------------------------ sources
        static readonly Dictionary<string, string?> sources = new Dictionary<string, string?>();
        /// <summary>Text of a demo source file: embedded resource first (LogicalName "src/<name>"), else the file next to the project (development runs).</summary>
        public static string? Source(string name)
        {
            if (sources.TryGetValue(name, out var s)) return s;
            string? text = null;
            try
            {
                var asm = typeof(CodeView).Assembly;
                foreach (var r in asm.GetManifestResourceNames())
                    if (r.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) || r.EndsWith("." + name, StringComparison.OrdinalIgnoreCase) || r == name)
                    { using var st = asm.GetManifestResourceStream(r); if (st != null) { using var rd = new StreamReader(st); text = rd.ReadToEnd(); } break; }
                if (text == null)
                {   // walk up from the exe: bin/x64/Release/net8.0-windows -> demo/
                    string? dir = AppContext.BaseDirectory;
                    for (int i = 0; i < 6 && dir != null && text == null; i++, dir = Path.GetDirectoryName(dir))
                    {
                        string p = Path.Combine(dir, name); if (File.Exists(p)) text = File.ReadAllText(p);
                        p = Path.Combine(dir, "demo", name); if (text == null && File.Exists(p)) text = File.ReadAllText(p);
                    }
                }
            }
            catch { text = null; }
            text = text?.Replace("\r\n", "\n");
            sources[name] = text; return text;
        }

        // ------------------------------------------------------------------ extraction
        /// <summary>The code shown for a test.</summary>
        public static string For(DemoTest t)
        {
            if (t.Code != null) return t.Code;
            var sb = new StringBuilder();
            sb.Append("// ").Append(t.Name).Append('\n');
            sb.Append("// The render lambda of this test as registered in demo/Tests.cs - it runs once per frame.\n");
            sb.Append("// c is the Ctx the demo fills from the panel: c.Canvas = the target Sprite (c.W x c.H), c.Temp = a scratch Sprite,\n");
            sb.Append("// c.A = Assets (c.A.Color / Alpha / Keyed / Mask / Tile / Normal / Env sprites, c.S px square), c.X / c.Y = the mouse,\n");
            sb.Append("// c.Count, c.Scale, c.ScaleY, c.Angle (rad), c.Blend (0..255), c.Brite, c.Z, c.Op, c.Smooth, c.NotMask, c.Xor, c.DotStep,\n");
            sb.Append("// c.MaskBits, c.Time (s, 0 with Animate off), c.PivotX / PivotY (right click), c.Note = status text.\n\n");
            var src = Source("Tests.cs");
            if (src == null) { sb.Append("// Tests.cs is not available (not embedded in this build and not found next to the exe)."); return sb.ToString(); }
            string? body = ExtractLambda(src, t.Name);
            if (body == null) { sb.Append("// (could not locate this test's lambda in Tests.cs)\n"); }
            else
            {
                sb.Append("(Ctx c) =>\n{\n").Append(Indent(body, 4)).Append("\n}\n");
                // helpers of class Tests the body calls (one level deep, a few at most)
                var helpers = Helpers(src);
                var done = new HashSet<string>(); var queue = new Queue<string>(); string scan = body;
                foreach (var name in Referenced(scan, helpers)) queue.Enqueue(name);
                while (queue.Count > 0 && done.Count < 8)
                {
                    var name = queue.Dequeue(); if (!done.Add(name)) continue;
                    var code = helpers[name];
                    sb.Append("\n// ---- helper (demo/Tests.cs) ----\n").Append(Dedent(code)).Append('\n');
                    if (done.Count < 4) foreach (var n2 in Referenced(code, helpers)) if (!done.Contains(n2)) queue.Enqueue(n2);
                }
            }
            if (t.ControlStrip != null)
            {
                var m = t.ControlStrip.Method; string type = m.DeclaringType?.Name ?? "", meth = m.Name;
                var csrc = Source("ControlsDemo.cs");
                string? code = csrc != null ? ExtractMethod(csrc, type, meth) : null;
                sb.Append("\n// ---- control strip: ").Append(type).Append('.').Append(meth).Append("() (demo/ControlsDemo.cs) - builds the SR2D controls above the canvas ----\n");
                sb.Append(code != null ? Dedent(code) : "// (source not available)").Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Finds the registration whose name literal matches (or is the longest prefix of) <paramref name="name"/> and returns its lambda body.</summary>
        static string? ExtractLambda(string src, string name)
        {
            int best = -1, bestLen = -1;
            foreach (Match m in Regex.Matches(src, @"\bT\((G\w+),\s*""", RegexOptions.Multiline))
            {
                int q = m.Index + m.Length - 1;                       // the opening quote
                int e = SkipString(src, q); if (e < 0) continue;
                string lit = Unescape(src.Substring(q + 1, e - q - 2));
                if (lit == name) { best = m.Index; bestLen = int.MaxValue; break; }
                if (lit.Length > 0 && name.StartsWith(lit, StringComparison.Ordinal) && lit.Length > bestLen) { best = m.Index; bestLen = lit.Length; }
            }
            if (best < 0) return null;
            // scan the T( argument list for "c =>" at depth 1
            int i = src.IndexOf('(', best) + 1; int depth = 1;
            while (i < src.Length && depth > 0)
            {
                char ch = src[i];
                if (ch == '"' || (ch == '$' && i + 1 < src.Length && (src[i + 1] == '"' || src[i + 1] == '@')) || (ch == '@' && i + 1 < src.Length && src[i + 1] == '"')) { i = SkipString(src, i); if (i < 0) return null; continue; }
                if (ch == '\'') { i = SkipChar(src, i); continue; }
                if (ch == '/' && i + 1 < src.Length && src[i + 1] == '/') { i = src.IndexOf('\n', i); if (i < 0) return null; continue; }
                if (ch == '/' && i + 1 < src.Length && src[i + 1] == '*') { i = src.IndexOf("*/", i, StringComparison.Ordinal); if (i < 0) return null; i += 2; continue; }
                if (ch == '(' || ch == '{' || ch == '[') depth++;
                else if (ch == ')' || ch == '}' || ch == ']') { depth--; if (depth == 0) return null; }
                else if (depth == 1 && ch == 'c' && (i == 0 || !IsIdent(src[i - 1])))
                {
                    int j = i + 1; while (j < src.Length && src[j] == ' ') j++;
                    if (j + 1 < src.Length && src[j] == '=' && src[j + 1] == '>')
                    {
                        j += 2; while (j < src.Length && char.IsWhiteSpace(src[j])) j++;
                        if (src[j] == '{') { int end = MatchBrace(src, j); return end < 0 ? null : Trim(src.Substring(j + 1, end - j - 1)); }
                        // expression lambda: up to the ',' or ')' that closes it at this depth
                        int k = j, d = 0;
                        while (k < src.Length)
                        {
                            char c2 = src[k];
                            if (c2 == '"' || (c2 == '$' && k + 1 < src.Length && (src[k + 1] == '"' || src[k + 1] == '@')) || (c2 == '@' && k + 1 < src.Length && src[k + 1] == '"')) { k = SkipString(src, k); if (k < 0) return null; continue; }
                            if (c2 == '\'') { k = SkipChar(src, k); continue; }
                            if (c2 == '(' || c2 == '{' || c2 == '[') d++;
                            else if (c2 == ')' || c2 == '}' || c2 == ']') { if (d == 0) break; d--; }
                            else if (c2 == ',' && d == 0) break;
                            k++;
                        }
                        return Trim(src.Substring(j, k - j)) + ";";
                    }
                }
                i++;
            }
            return null;
        }

        /// <summary>Static helper members of class Tests (indent 8): name -> full source.</summary>
        static Dictionary<string, string>? helperCache;
        static Dictionary<string, string> Helpers(string src)
        {
            if (helperCache != null) return helperCache;
            var d = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(src, @"^        (?:\[ThreadStatic\] )?(?:internal |public |private )?static (?!class)(?:\([^\n)]*\)\s*)?[^\n;=(]*?\b(\w+)\s*(?:<[^\n>]*>)?\(", RegexOptions.Multiline))
            {
                string name = m.Groups[1].Value; if (d.ContainsKey(name)) continue;
                int end = MemberEnd(src, m.Index); if (end < 0) continue;
                d[name] = src.Substring(m.Index, end - m.Index);
            }
            helperCache = d; return d;
        }
        static IEnumerable<string> Referenced(string code, Dictionary<string, string> helpers)
        {
            foreach (var kv in helpers) if (Regex.IsMatch(code, @"(?<![\w.])" + Regex.Escape(kv.Key) + @"\s*\(")) yield return kv.Key;
        }
        /// <summary>Source of method <paramref name="method"/> inside class <paramref name="type"/>.</summary>
        static string? ExtractMethod(string src, string type, string method)
        {
            var cm = Regex.Match(src, @"class\s+" + Regex.Escape(type) + @"\b");
            if (!cm.Success) return null;
            int classEnd = MatchBrace(src, src.IndexOf('{', cm.Index)); if (classEnd < 0) classEnd = src.Length;
            var mm = Regex.Match(src.Substring(cm.Index, classEnd - cm.Index), @"^\s*(?:public |internal |private )?static [^\n;=(]*?\b" + Regex.Escape(method) + @"\s*\(", RegexOptions.Multiline);
            if (!mm.Success) return null;
            int start = cm.Index + mm.Index; while (start < src.Length && (src[start] == ' ' || src[start] == '\n')) start++;
            int end = MemberEnd(src, start); return end < 0 ? null : src.Substring(start, end - start);
        }
        /// <summary>End (exclusive) of a member starting at <paramref name="start"/>: after the matching '}' of a block body, or after the ';' of an expression body.</summary>
        static int MemberEnd(string src, int start)
        {
            int i = start, paren = 0;
            while (i < src.Length)
            {
                char ch = src[i];
                if (ch == '"' || (ch == '$' && i + 1 < src.Length && (src[i + 1] == '"' || src[i + 1] == '@')) || (ch == '@' && i + 1 < src.Length && src[i + 1] == '"')) { i = SkipString(src, i); if (i < 0) return -1; continue; }
                if (ch == '\'') { i = SkipChar(src, i); continue; }
                if (ch == '/' && i + 1 < src.Length && src[i + 1] == '/') { i = src.IndexOf('\n', i); if (i < 0) return -1; continue; }
                if (ch == '(') paren++; else if (ch == ')') paren--;
                else if (ch == '{' && paren == 0) { int e = MatchBrace(src, i); if (e < 0) return -1; e++; int k = e; while (k < src.Length && src[k] == ' ') k++; return k < src.Length && src[k] == ';' ? k + 1 : e; }   // '=> x switch { ... };' keeps its ';'
                else if (ch == ';' && paren == 0) return i + 1;
                i++;
            }
            return -1;
        }
        static int MatchBrace(string src, int open)
        {
            int i = open + 1, depth = 1;
            while (i < src.Length)
            {
                char ch = src[i];
                if (ch == '"' || (ch == '$' && i + 1 < src.Length && (src[i + 1] == '"' || src[i + 1] == '@')) || (ch == '@' && i + 1 < src.Length && src[i + 1] == '"')) { i = SkipString(src, i); if (i < 0) return -1; continue; }
                if (ch == '\'') { i = SkipChar(src, i); continue; }
                if (ch == '/' && i + 1 < src.Length && src[i + 1] == '/') { i = src.IndexOf('\n', i); if (i < 0) return -1; continue; }
                if (ch == '/' && i + 1 < src.Length && src[i + 1] == '*') { i = src.IndexOf("*/", i, StringComparison.Ordinal); if (i < 0) return -1; i += 2; continue; }
                if (ch == '{') depth++; else if (ch == '}') { depth--; if (depth == 0) return i; }
                i++;
            }
            return -1;
        }
        /// <summary>Index after a string literal starting at <paramref name="i"/> ("...", @"...", $"...", $@"..." with nested interpolations), -1 if unterminated.</summary>
        static int SkipString(string src, int i)
        {
            bool interp = false, verbatim = false;
            while (i < src.Length && src[i] != '"') { if (src[i] == '$') interp = true; else if (src[i] == '@') verbatim = true; else return -1; i++; }
            if (i >= src.Length) return -1;
            i++;
            while (i < src.Length)
            {
                char ch = src[i];
                if (ch == '"') { if (verbatim && i + 1 < src.Length && src[i + 1] == '"') { i += 2; continue; } return i + 1; }
                if (ch == '\\' && !verbatim) { i += 2; continue; }
                if (interp && ch == '{')
                {
                    if (i + 1 < src.Length && src[i + 1] == '{') { i += 2; continue; }
                    int depth = 1; i++;
                    while (i < src.Length && depth > 0)
                    {
                        char c2 = src[i];
                        if (c2 == '"' || (c2 == '$' && i + 1 < src.Length && (src[i + 1] == '"' || src[i + 1] == '@')) || (c2 == '@' && i + 1 < src.Length && src[i + 1] == '"')) { i = SkipString(src, i); if (i < 0) return -1; continue; }
                        if (c2 == '\'') { i = SkipChar(src, i); continue; }
                        if (c2 == '{' || c2 == '(') depth++; else if (c2 == '}' || c2 == ')') depth--;
                        i++;
                    }
                    continue;
                }
                i++;
            }
            return -1;
        }
        static int SkipChar(string src, int i)
        {
            i++; if (i < src.Length && src[i] == '\\') i += 2; else i++;
            while (i < src.Length && src[i] != '\'' && src[i] != '\n') i++;
            return i + 1;
        }
        static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
        static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        static string Trim(string s)
        {
            var lines = s.Replace("\r", "").Split('\n').ToList();
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            return Dedent(string.Join("\n", lines));
        }
        static string Dedent(string s)
        {
            var lines = s.Replace("\r", "").Split('\n');
            int min = int.MaxValue;
            foreach (var l in lines) { if (l.Trim().Length == 0) continue; int n = 0; while (n < l.Length && l[n] == ' ') n++; min = Math.Min(min, n); }
            if (min == int.MaxValue || min == 0) return s.Replace("\r", "");
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].Length >= min ? lines[i].Substring(min) : lines[i].TrimStart();
            return string.Join("\n", lines);
        }
        static string Indent(string s, int n) { string pad = new string(' ', n); return pad + s.Replace("\n", "\n" + pad); }

        // ------------------------------------------------------------------ highlighting
        static readonly HashSet<string> Keywords = new HashSet<string>(("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern " +
            "false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short " +
            "sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while yield get set value nameof when where record init with async await").Split(' '));
        /// <summary>RTF for a RichTextBox: dark background palette (text, keyword, string, comment, number, type, method).</summary>
        /// <summary>Token classes of <see cref="Colorize"/>: 0 plain, 1 keyword, 2 string / char, 3 comment, 4 number, 5 type name, 6 call.</summary>
        public static readonly int[] Palette =
        {
            unchecked((int)0xFFD4D4D4), unchecked((int)0xFF569CD6), unchecked((int)0xFFCE9178), unchecked((int)0xFF6A9955),
            unchecked((int)0xFFB5CEA8), unchecked((int)0xFF4EC9B0), unchecked((int)0xFFDCDCAA),
        };
        /// <summary>
        /// Names of the Ctx members a test's code reads ("c.Angle", "c.Scale" ... -> "Angle", "Scale"), comment lines
        /// skipped (the extracted code starts with a comment header that lists every parameter). The demo maps them to
        /// Param flags to decide which controls the Test tab shows before the test ran for the first time.
        /// </summary>
        public static HashSet<string> ParamRefs(string code)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in code.Split('\n'))
            {
                if (raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in ParamRef.Matches(raw)) set.Add(m.Groups[1].Value);
            }
            return set;
        }
        static readonly Regex ParamRef = new Regex(@"\bc\.(X|Y|Z|Angle|Scale|ScaleY|Blend|Brite|Count|Smooth|NotMask|MaskBits|Op|DotStep|Xor|Time|Yaw|Pitch|PanX|PanY|PivotXF?|PivotYF?)\b", RegexOptions.Compiled);

        /// <summary>
        /// Splits <paramref name="code"/> into lines and colours them (VS dark palette): one array of (start, length, argb) runs per
        /// line, null for a plain line. Comments and verbatim strings may span lines - the state carries over.
        /// </summary>
        public static (string[] lines, (int start, int len, int color)[]?[] runs) Colorize(string code)
        {
            code = code.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = code.Split('\n');
            var runs = new (int, int, int)[]?[lines.Length];
            var cur = new List<(int, int, int)>();
            int line = 0, lineStart = 0, i = 0, n = code.Length;
            void Emit(int from, int to, int cls)
            {   // a token may cross line ends: cut it at every '\n'
                while (from < to)
                {
                    int nl = code.IndexOf('\n', from, to - from); int end = nl < 0 ? to : nl;
                    if (end > from && cls != 0) cur.Add((from - lineStart, end - from, Palette[cls]));
                    if (nl < 0) return;
                    runs[line] = cur.Count > 0 ? cur.ToArray() : null; cur.Clear(); line++; lineStart = nl + 1; from = nl + 1;
                }
            }
            while (i < n)
            {
                char ch = code[i];
                if (ch == '\n') { Emit(i, i + 1, 0); i++; continue; }
                if (ch == '/' && i + 1 < n && code[i + 1] == '/') { int e = code.IndexOf('\n', i); if (e < 0) e = n; Emit(i, e, 3); i = e; continue; }
                if (ch == '/' && i + 1 < n && code[i + 1] == '*') { int e = code.IndexOf("*/", i, StringComparison.Ordinal); e = e < 0 ? n : e + 2; Emit(i, e, 3); i = e; continue; }
                if (ch == '"' || ((ch == '$' || ch == '@') && i + 1 < n && (code[i + 1] == '"' || code[i + 1] == '@' || code[i + 1] == '$')))
                {
                    int e = SkipString(code, i); if (e < 0) e = n;
                    Emit(i, e, 2); i = e; continue;
                }
                if (ch == '\'') { int e = SkipChar(code, i); if (e > n) e = n; Emit(i, e, 2); i = e; continue; }
                if (char.IsDigit(ch) || (ch == '.' && i + 1 < n && char.IsDigit(code[i + 1]) && (i == 0 || !IsIdent(code[i - 1]))))
                {
                    int e = i + 1; while (e < n && (char.IsLetterOrDigit(code[e]) || code[e] == '.' || code[e] == '_')) e++;
                    Emit(i, e, 4); i = e; continue;
                }
                if (IsIdent(ch) && !char.IsDigit(ch))
                {
                    int e = i + 1; while (e < n && IsIdent(code[e])) e++;
                    string word = code.Substring(i, e - i);
                    int k = e; while (k < n && code[k] == ' ') k++;
                    bool call = k < n && (code[k] == '(' || code[k] == '<' && word.Length > 1 && char.IsUpper(word[0]) == false);
                    bool prevDot = i > 0 && code[i - 1] == '.';
                    int cls = Keywords.Contains(word) && !prevDot ? 1 : char.IsUpper(word[0]) && !(call && prevDot) ? 5 : call ? 6 : 0;
                    Emit(i, e, cls); i = e; continue;
                }
                i++;
            }
            if (line < runs.Length) runs[line] = cur.Count > 0 ? cur.ToArray() : null;
            return (lines, runs);
        }
    }
}
