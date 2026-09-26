using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------------------------------------------------------
    // Text for the SR2D controls: the 5x7 PixelFont (default, unchanged) or a SpriteFont (TrueType / OpenType) - the same
    // calls either way. A ControlText is a tiny value ("which font, which size") that every control builds from its
    // TextFont / TextFontFamily / TextFontSize properties, falling back to the app-wide SpriteControlBase.DefaultFont*.
    //
    //     SpriteControlBase.DefaultFontFamily = "Segoe UI";        // every control that has no font of its own
    //     knob.TextFontFamily = "Consolas"; knob.TextFontSize = 11; // one control (designer-settable strings)
    //     label.TextFont = SpriteFont.Load("brand.otf");            // a font object you loaded yourself
    //     knob.TextFontFamily = "";                                  // back to the pixel font
    //
    // Scale: the controls size their text in "TextScale" steps (1 = 5x7 glyphs, 2 = 10x14, ...; automatic from the
    // control size). With a SpriteFont a step multiplies TextFontSize (12 px * 1, * 2, ...), so the existing layout
    // arithmetic (rows, paddings, auto sizes) keeps working and a control switched to a real font looks the same size.
    // ------------------------------------------------------------------------------------------------------------------------

    /// <summary>The font a control draws with: <see cref="Font"/> null = the built-in <see cref="PixelFont"/>.</summary>
    internal readonly struct ControlText
    {
        /// <summary>Regular face, or null for the pixel font.</summary>
        public readonly SpriteFont? Font;
        /// <summary>Bold face for weight &gt; 0 (null: bold is emulated by a 1 px double draw).</summary>
        public readonly SpriteFont? Bold;
        /// <summary>Em size in pixels at scale 1.</summary>
        public readonly float Px;

        public ControlText(SpriteFont? font, SpriteFont? bold, float px) { Font = font; Bold = font == null ? null : bold; Px = px > 0 ? px : 12f; }
        /// <summary>The pixel font.</summary>
        public static ControlText Pixel => default;
        public bool IsPixel => Font == null;
        float SizeAt(int scale) => Px * Math.Max(1, scale);
        SpriteFont Face(int weight) => weight > 0 && Bold != null ? Bold : Font!;
        int Extra(int weight) => weight > 0 && Bold == null ? Math.Max(1, weight) : 0;   // emulated bold widens by the offset

        /// <summary>Height of one line's glyph box (pixel: 7 * scale; font: ascent + descent).</summary>
        public int GlyphHeight(int scale) => Font == null ? PixelFont.Default.GlyphHeight * Math.Max(1, scale) : (int)MathF.Round((Font.AscentEm + Font.DescentEm) * SizeAt(scale));
        /// <summary>Typical glyph advance (pixel: 6 * scale; font: the advance of 'n').</summary>
        public int GlyphAdvance(int scale) => Font == null ? (PixelFont.Default.GlyphWidth + 1) * Math.Max(1, scale) : Math.Max(1, (int)MathF.Round(Font.Measure("n", SizeAt(scale))));
        /// <summary>Distance between the tops of stacked lines (what <see cref="Draw"/> uses for '\n').</summary>
        public int LineHeight(int scale) => Font == null ? (PixelFont.Default.GlyphHeight + 1) * Math.Max(1, scale) : Math.Max(GlyphHeight(scale), (int)MathF.Round(Font.LineHeight(SizeAt(scale))));
        /// <summary>Height of a text row with breathing space (lists, menus, tabs).</summary>
        public int RowHeight(int scale) => Font == null ? (PixelFont.Default.GlyphHeight + 2) * Math.Max(1, scale) + 2 : LineHeight(scale) + 2 * Math.Max(1, scale) + 2;

        /// <summary>Same contract as <see cref="Sprite.MeasureText"/>: widest line x (lines - 1) * LineHeight + GlyphHeight.</summary>
        public Size Measure(string text, int scale = 1, int weight = 0, int letterSpacing = 0)
        {
            if (Font == null) return Sprite.MeasureText(text, scale, weight, letterSpacing);
            if (string.IsNullOrEmpty(text)) return Size.Empty;
            float size = SizeAt(scale); var f = Face(weight); float sp = f.LetterSpacing;
            if (letterSpacing != 0) f.LetterSpacing = letterSpacing / size;
            float w = 0; int lines = 0;
            try { foreach (var line in text.Split('\n')) { w = MathF.Max(w, f.Measure(line.TrimEnd('\r'), size)); lines++; } }
            finally { f.LetterSpacing = sp; }
            return new Size((int)MathF.Ceiling(w) + Extra(weight), (lines - 1) * LineHeight(scale) + GlyphHeight(scale));
        }
        /// <summary>Same contract as <see cref="Sprite.DrawText"/> (anchor, background box with 2 * scale padding, '\n', op).</summary>
        public Rectangle Draw(Sprite s, int x, int y, string text, int color, int bg = 0, int scale = 1, int weight = 0, int letterSpacing = 0,
                              SR2D.LineOp op = SR2D.LineOp.Set, int blendFactor = 128, TextAnchor anchor = TextAnchor.TopLeft)
        {
            if (Font == null) return s.DrawText(x, y, text, color, bg, scale, weight, letterSpacing, op, blendFactor, anchor);
            if (string.IsNullOrEmpty(text)) return Rectangle.Empty;
            if (scale < 1) scale = 1;
            var size = Measure(text, scale, weight, letterSpacing);
            int pad = bg != 0 ? 2 * scale : 0;
            int bw = size.Width + 2 * pad, bh = size.Height + 2 * pad;
            int ax = (int)anchor % 3, ay = (int)anchor / 3;
            int left = x - bw * ax / 2, top = y - bh * ay / 2;
            if (bg != 0) s.FillRect(left, top, bw, bh, bg, op, false, blendFactor);
            float px = SizeAt(scale); var f = Face(weight); int lh = LineHeight(scale), ex = Extra(weight); float sp = f.LetterSpacing;
            if (letterSpacing != 0) f.LetterSpacing = letterSpacing / px;
            try
            {
                int li = 0;
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length > 0)
                    {
                        float ly = top + pad + li * lh + f.Ascent(px);
                        s.DrawStringBaseline(left + pad, ly, line, f, px, color, op, blendFactor);
                        if (ex > 0) s.DrawStringBaseline(left + pad + ex, ly, line, f, px, color, op == SR2D.LineOp.Set ? SR2D.LineOp.Max : op, blendFactor);
                    }
                    li++;
                }
            }
            finally { f.LetterSpacing = sp; }
            return new Rectangle(left, top, bw, bh);
        }
        /// <summary>Pen x of the character at <paramref name="index"/> relative to the start of a single-line text (caret placement).</summary>
        public int CharX(string text, int index, int scale)
        {
            if (index <= 0 || text.Length == 0) return 0;
            index = Math.Min(index, text.Length);
            if (Font == null)
            {
                if (AllPixel(text.AsSpan(0, index))) return index * (PixelFont.Default.GlyphWidth + 1) * Math.Max(1, scale);   // the common case: one advance per character
                Span<int> pens = text.Length < 256 ? stackalloc int[text.Length + 1] : new int[text.Length + 1];
                Sprite.TextPens(text.AsSpan(), pens, scale);
                return pens[index];
            }
            return (int)MathF.Round(Font.Measure(text.AsSpan(0, index), SizeAt(scale)));
        }
        static bool AllPixel(ReadOnlySpan<char> t) { var f = PixelFont.Default; foreach (char c in t) if (c >= 0x80 && !f.Has(c)) return false; return true; }
        /// <summary>Nearest character boundary to <paramref name="x"/> (relative to the text start) - the inverse of <see cref="CharX"/>.</summary>
        public int IndexAt(string text, float x, int scale)
        {
            if (x <= 0 || text.Length == 0) return 0;
            if (Font == null)
            {
                int adv = (PixelFont.Default.GlyphWidth + 1) * Math.Max(1, scale);
                if (AllPixel(text.AsSpan())) return Math.Clamp((int)Math.Round(x / adv), 0, text.Length);
                Span<int> pens = text.Length < 256 ? stackalloc int[text.Length + 1] : new int[text.Length + 1];
                Sprite.TextPens(text.AsSpan(), pens, scale);
                for (int i = 1; i <= text.Length; i++) if (x < (pens[i - 1] + pens[i]) / 2f) return i - 1;
                return text.Length;
            }
            int prev = 0;
            for (int i = 1; i <= text.Length; i++)
            {
                int cx = CharX(text, i, scale);
                if (x < (prev + cx) / 2f) return i - 1;
                prev = cx;
            }
            return text.Length;
        }
        /// <summary>Largest scale &lt;= <paramref name="want"/> at which <paramref name="text"/> fits <paramref name="width"/> px (never below 1).</summary>
        public int FitScale(string text, int width, int want)
        {
            while (want > 1 && Measure(text, want).Width > width) want--;
            return want;
        }
        /// <summary>Shortens <paramref name="text"/> with an ellipsis so it fits <paramref name="width"/> px.</summary>
        public string FitText(string text, int width, int scale)
        {
            if (string.IsNullOrEmpty(text) || Measure(text, scale).Width <= width) return text;
            int n = text.Length;
            while (n > 1 && Measure(text.Substring(0, n) + "\u2026", scale).Width > width) n--;
            return text.Substring(0, n) + "\u2026";
        }
        /// <summary>Word-wraps to <paramref name="width"/> px ('\n' kept; words longer than the width are cut).</summary>
        public string Wrap(string text, int width, int scale)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return text;
            if (Font != null) return string.Join("\n", Font.Wrap(text, SizeAt(scale), width));
            int adv = (PixelFont.Default.GlyphWidth + 1) * Math.Max(1, scale), cols = Math.Max(1, (width + scale) / adv);
            var sb = new System.Text.StringBuilder(text.Length + 8);
            foreach (var para in text.Split('\n'))
            {
                if (sb.Length > 0) sb.Append('\n');
                int lineLen = 0;
                foreach (var word0 in para.Split(' '))
                {
                    var word = word0;
                    while (word.Length > cols) { if (lineLen > 0) { sb.Append('\n'); lineLen = 0; } sb.Append(word, 0, cols).Append('\n'); word = word.Substring(cols); }
                    if (lineLen > 0 && lineLen + 1 + word.Length > cols) { sb.Append('\n'); lineLen = 0; }
                    if (lineLen > 0) { sb.Append(' '); lineLen++; }
                    sb.Append(word); lineLen += word.Length;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Resolves family names to shared <see cref="SpriteFont"/>s for the controls (one instance per family / bold / italic, never disposed).</summary>
    internal static class ControlFonts
    {
        static readonly Dictionary<string, SpriteFont?> cache = new Dictionary<string, SpriteFont?>(StringComparer.OrdinalIgnoreCase);
        static readonly object gate = new object();
        /// <summary>The installed face, or null when the family is empty / not installed (the caller falls back to the pixel font).</summary>
        public static SpriteFont? Get(string? family, bool bold = false, bool italic = false)
        {
            if (string.IsNullOrWhiteSpace(family)) return null;
            string key = family.Trim() + (bold ? "|b" : "") + (italic ? "|i" : "");
            lock (gate)
            {
                if (cache.TryGetValue(key, out var f)) return f;
                try { f = SpriteFont.Installed(family.Trim(), bold, italic); } catch (Exception e) { ImportLog.Swallowed(e, "control font " + family); f = null; }
                // a bold request that resolved to the regular face is not a bold face: report null so the caller emulates
                if (f != null && bold && !f.StyleName.Contains("bold", StringComparison.OrdinalIgnoreCase) && !f.StyleName.Contains("black", StringComparison.OrdinalIgnoreCase) && !f.StyleName.Contains("heavy", StringComparison.OrdinalIgnoreCase)) f = null;
                cache[key] = f; return f;
            }
        }
        /// <summary>Registers a font under a family name so <c>TextFontFamily = name</c> finds it (fonts loaded from files / resources).</summary>
        public static void Register(string family, SpriteFont font, bool bold = false, bool italic = false)
        {
            lock (gate) cache[family.Trim() + (bold ? "|b" : "") + (italic ? "|i" : "")] = font;
        }
    }

    internal abstract partial class SpriteControlBase
    {
        static SpriteFont? _defFont; static string _defFamily = ""; static float _defSize = 12f;
        SpriteFont? _font; string _family = ""; float _fontSize;

        /// <summary>App-wide font for every SR2D control that has no font of its own; null (default) = the pixel font. Set at start-up (existing controls repaint on their next redraw).
        /// Deliberately hides the static <c>Control.DefaultFont</c> (a GDI <c>Font</c>): the SR2D controls draw their own text and never use it.</summary>
        public static new SpriteFont? DefaultFont { get => _defFont; set => _defFont = value; }
        /// <summary>App-wide font by installed family name ("" = pixel font). Used when <see cref="DefaultFont"/> is null.</summary>
        public static string DefaultFontFamily { get => _defFamily; set => _defFamily = value ?? ""; }
        /// <summary>App-wide em size in pixels at TextScale 1 (default 12).</summary>
        public static float DefaultFontSize { get => _defSize; set => _defSize = value > 0 ? value : 12f; }

        /// <summary>A font object for this control (overrides <see cref="TextFontFamily"/>); null = family / default / pixel font.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public SpriteFont? TextFont { get => _font; set { _font = value; OnTextFontChanged(); } }

        [Category("Appearance"), DefaultValue(""), Description("Installed font family for the control's text (e.g. Segoe UI); empty = the app default (SpriteControlBase.DefaultFontFamily) or the built-in pixel font.")]
        public string TextFontFamily { get => _family; set { _family = value ?? ""; OnTextFontChanged(); } }

        [Category("Appearance"), DefaultValue(0f), Description("Em size in pixels at TextScale 1 when a real font is used (0 = SpriteControlBase.DefaultFontSize, 12). TextScale multiplies it.")]
        public float TextFontSize { get => _fontSize; set { _fontSize = Math.Max(0f, value); OnTextFontChanged(); } }

        /// <summary>Called when the font selection changes; controls that size themselves to their text override it.</summary>
        protected virtual void OnTextFontChanged() => Redraw();

        /// <summary>The font this control draws with right now (pixel font unless something above says otherwise).</summary>
        protected ControlText T
        {
            get
            {
                float px = _fontSize > 0 ? _fontSize : _defSize;
                if (_font != null) return new ControlText(_font, null, px);
                if (_family.Length > 0) { var f = ControlFonts.Get(_family); if (f != null) return new ControlText(f, ControlFonts.Get(_family, true), px); }
                if (_defFont != null) return new ControlText(_defFont, null, px);
                if (_defFamily.Length > 0) { var f = ControlFonts.Get(_defFamily); if (f != null) return new ControlText(f, ControlFonts.Get(_defFamily, true), px); }
                return ControlText.Pixel;
            }
        }
        /// <summary>True when the control currently draws with a SpriteFont rather than the pixel font.</summary>
        [Browsable(false)] public bool UsesSpriteFont => !T.IsPixel;
    }
}
