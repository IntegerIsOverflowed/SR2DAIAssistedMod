using System;
using System.Collections.Generic;

namespace Sr2d64CSport
{
    /// <summary>
    /// Records exceptions the importers swallow on purpose. The PDF / PostScript / AI / SVG readers and the font parsers
    /// treat a broken object as "skip it, keep the rest of the file" - that is what makes damaged and hostile files load
    /// at all. The price used to be silence: a genuine bug in a decoder looked exactly like a damaged file. Now every
    /// such catch calls <see cref="Swallowed"/>; while an import is running (<see cref="Begin"/> / <see cref="End"/>)
    /// the entries are collected and appended to <see cref="VectorImage.Warnings"/> (or <see cref="SpriteFont.Warnings"/>)
    /// as lines like <c>"skipped: IndexOutOfRangeException in Type1 charstring (gid 42)"</c>. Outside a scope the call
    /// is a no-op: nothing is allocated, nothing is formatted.
    /// <para>Scopes are per thread and nest (a PDF that loads a font that parses a CFF table: all lines go to the PDF).
    /// Duplicate lines are recorded once; a scope keeps at most <see cref="Limit"/> lines and then a final "... and N
    /// more" line, so a file with ten thousand broken glyphs does not produce ten thousand strings.</para>
    /// <para><see cref="Listener"/> lets a host see every swallowed exception immediately (debug output, breakpoints),
    /// including the ones outside any scope (e.g. a font loaded by a control at paint time).</para>
    /// </summary>
    public static class ImportLog
    {
        sealed class Scope { public readonly List<string> Lines = new List<string>(); public readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal); public int Dropped; public Scope? Outer; }
        [ThreadStatic] static Scope? current;

        /// <summary>Maximum distinct lines kept per import (default 64).</summary>
        public static int Limit { get; set; } = 64;
        /// <summary>Optional immediate observer: (exception, where) for every swallowed exception on any thread. Keep it fast; it runs inside the importer.</summary>
        public static Action<Exception, string>? Listener { get; set; }

        /// <summary>Starts collecting on this thread. Always pair with <see cref="End"/> (use try / finally).</summary>
        public static void Begin() { current = new Scope { Outer = current }; }
        /// <summary>Stops collecting and appends the recorded lines to <paramref name="warnings"/> (null = discard).</summary>
        public static void End(List<string>? warnings)
        {
            var s = current; if (s == null) return;
            current = s.Outer;
            if (warnings == null) return;
            foreach (var l in s.Lines) if (!warnings.Contains(l)) warnings.Add(l);
            if (s.Dropped > 0) warnings.Add($"skipped: ... and {s.Dropped} more (ImportLog.Limit = {Limit})");
        }
        /// <summary>True while an import scope is open on this thread (cheap; lets callers skip building a context string).</summary>
        public static bool Active => current != null || Listener != null;

        /// <summary>Records an exception a parser decided to survive. <paramref name="where"/> names the place (short, no line numbers): "Type1 charstring", "PDF object 42", ...
        /// <paramref name="fallback"/> receives the line when no scope is open (a font building a glyph at draw time writes into its own <c>Warnings</c>).</summary>
        public static void Swallowed(Exception e, string where, List<string>? fallback = null)
        {
            var listener = Listener; if (listener != null) { try { listener(e, where); } catch { /* an observer must not break the import */ } }
            var s = current;
            if (s == null)
            {
                if (fallback == null) return;
                string l = Line(e, where);
                lock (fallback) { if (fallback.Count < Math.Max(1, Limit) && !fallback.Contains(l)) fallback.Add(l); }
                return;
            }
            string line = Line(e, where);
            if (!s.Seen.Add(line)) return;
            if (s.Lines.Count >= Math.Max(1, Limit)) { s.Dropped++; return; }
            s.Lines.Add(line);
        }
        /// <summary>Same, with a lazily formatted context (only evaluated when somebody is listening).</summary>
        public static void Swallowed(Exception e, Func<string> where, List<string>? fallback = null) { if (Active || fallback != null) Swallowed(e, where(), fallback); }

        static string Line(Exception e, string where) =>
            "skipped: " + e.GetType().Name + " in " + where + (string.IsNullOrEmpty(e.Message) || e is IndexOutOfRangeException || e is NullReferenceException || e is ArgumentOutOfRangeException ? "" : " (" + Trim(e.Message) + ")");

        static string Trim(string m)
        {
            int nl = m.IndexOfAny(new[] { '\r', '\n' }); if (nl >= 0) m = m.Substring(0, nl);
            return m.Length > 80 ? m.Substring(0, 77) + "..." : m;
        }
    }
}
