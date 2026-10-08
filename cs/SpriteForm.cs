// SpriteForm - a borderless form whose chrome is drawn by SR2D itself (one cached Sprite per repaint).
//
//   var f = new SpriteForm { Text = "My window", Icon = myIcon };
//   f.Controls.Add(content);                       // docks into what the bar and the border leave
//   f.Show();
//
// The title bar, left to right:
//   * the decorative COLOUR BARS - skewed rectangles running the full height of the bar
//     (StripeMode / StripeBarCount / StripeBarWidth / StripeSkew / StripeStyle / StripeSeed / StripeColors).
//     By default the icon and the texts start at the left edge and are drawn OVER them (StripesBehindText);
//     set it false and the pattern keeps the left corner to itself and the texts follow it.
//   * the window icon - the form's own Icon (like a native form), or TitleIcon to override it, ShowIcon to hide it.
//     Where it lands on the bars it sits in a small rounded plate (IconBackdrop) so it stays readable.
//   * the caption (Text) and any number of extra texts after it (SetTitleTags), each with its own colour / font
//   * minimize / maximize (right click = full screen) / close on the right
// Every title text carries a drop shadow by default (TitleTextShadow / TitleTag.Shadow) so it stays readable
// over any bar colour.
//
// Drag the bar to move the window (dragging a MAXIMIZED window restores it under the cursor, like Windows
// does), DOUBLE-CLICK it to maximize / restore, right click it for the window menu, click the icon for the
// same menu (double-click closes, like a native title bar).
//
// The window carries a real Windows frame but hides it: NativeWindowFrame keeps WS_CAPTION + WS_THICKFRAME on
// the window (that is what Windows 11 looks at for the minimize / restore / close animations, the drop shadow
// and the snap layouts) and WM_NCCALCSIZE hands the whole non-client strip back to the client area, so the bar
// SR2D draws is still the only thing at the top. Measured: without those two bits the minimize is one frame,
// with them the same window fades away over several - like any ordinary window. NativeWindowFrame = false
// gives the older fully borderless window back.
// The resize grab is WM_NCHITTEST (see WndProc), so the band around the client area gets the real Windows
// resize cursors in all eight directions including the corners, and BorderColor / BorderThickness paint the
// border band there (alpha 0 or thickness 0 = no visible frame, the content runs to the window edge).
// Maximize is MaximizedBounds = the working area plus a WM_NCCALCSIZE clamp, so the frame's off-screen border
// never eats the bar and the taskbar never gets covered; MinimumSize keeps a floor under resizing so the bar
// can never be squeezed away.
// Full screen covers the monitor - or the working area with FullScreenExcludeTaskbar;
// FullScreenEnabled = false removes the feature (no right click, no menu command).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

#pragma warning disable CA2213 // the bar children are disposed with the form; the menu is disposed in Dispose(bool)

namespace Sr2d64CSport
{
    /// <summary>How <see cref="SpriteForm.StripeMode"/> decides the number of title bar colour bars.</summary>
    public enum StripeBarsMode
    {
        /// <summary>No pattern at all - the bar starts with the icon / the caption.</summary>
        None,
        /// <summary>One bar per character of the caption (<see cref="Form.Text"/>).</summary>
        CaptionLength,
        /// <summary>One bar per character of everything the bar writes: the caption plus all the extra texts.</summary>
        AllTitleTextLength,
        /// <summary>A fixed number of bars: <see cref="SpriteForm.StripeBarCount"/>.</summary>
        FixedCount,
    }

    /// <summary>How the title bar colour bars are painted.</summary>
    public enum StripeBarsStyle
    {
        /// <summary>Parallelograms running the full height of the bar.</summary>
        FullBars,
        /// <summary>Only the same bars cut to a short line at the top and at the bottom of the bar
        /// (<see cref="SpriteForm.StripeLineThickness"/> px each), the middle keeps the plain bar colour.</summary>
        TopAndBottomLines,
    }

    /// <summary>Where the <see cref="SpriteForm.StripeMode"/> colour bar run sits inside the width the bar has for it.</summary>
    public enum StripeBarsAlign
    {
        /// <summary>At the left end of the bar (plus <see cref="SpriteForm.StripeOffset"/>). The run is long enough
        /// to reach the last glyph of the text it mirrors, so it covers the block from the corner.</summary>
        Left,
        /// <summary>Centred in the width the bar has for it.</summary>
        Center,
        /// <summary>At the right end, just before the caption buttons.</summary>
        Right,
        /// <summary>Centred on the icon + text block instead of on the bar: the pattern sticks out by the same
        /// margin left of the icon and right of the last glyph. With <see cref="SpriteForm.StripesBehindText"/>
        /// off (the texts sit after the pattern) the two together are centred as one group.</summary>
        WithText,
    }

    /// <summary>Where the darkening plate sits behind the bar texts (see <see cref="SpriteForm.TitleDarkenMode"/>).
    /// The plate is a black rectangle sized to the text's pixel length, inflated around it, edges feathered.</summary>
    public enum TitleDarkenMode
    {
        /// <summary>No darkening.</summary>
        None,
        /// <summary>One plate behind the caption only.</summary>
        BehindTitle,
        /// <summary>One plate behind the caption and every extra title text.</summary>
        BehindAllText,
    }

    /// <summary>One extra text after the window title in the bar of a <see cref="SpriteForm"/> (own colour, font and shadow).</summary>
    public sealed class TitleTag
    {
        /// <summary>The text (empty tags are skipped).</summary>
        public string Text { get; set; } = "";
        /// <summary>Text colour (edits in the designer with the usual colour cell).</summary>
        public Color Color { get; set; } = Color.FromArgb(0xC8, 0xD0, 0xDC);
        /// <summary>Installed font family ("" = the app default / the pixel font).</summary>
        public string FontFamily { get; set; } = "";
        /// <summary>Font size in px (0 = the default).</summary>
        public float FontSize { get; set; }
        /// <summary>Drop-shadow the text (on by default, like every other title bar text).</summary>
        public bool Shadow { get; set; } = true;
        /// <summary>Shadow colour; fully transparent (the default) = the bar's <see cref="SpriteForm.TitleTextShadowColor"/>.</summary>
        public Color ShadowColor { get; set; } = Color.FromArgb(0, 0, 0, 0);

        public TitleTag() { }
        public TitleTag(string text, Color color) { Text = text; Color = color; }
        public TitleTag(string text, Color color, string fontFamily, float fontSize) { Text = text; Color = color; FontFamily = fontFamily; FontSize = fontSize; }
        public TitleTag(string text, Color color, string fontFamily, float fontSize, Color shadowColor) : this(text, color, fontFamily, fontSize) { ShadowColor = shadowColor; }
    }

    /// <summary>
    /// A borderless form whose chrome SR2D draws: colour bars in the left corner, the window icon, the
    /// caption and extra texts (each with a drop shadow by default), minimize / maximize / close, a
    /// configurable border band and the eight resize grabs.
    /// </summary>
    /// <remarks>
    /// PUBLIC and <c>DesignerCategory("Form")</c> on purpose: the attribute is inherited, so an internal
    /// "Code" base class makes Visual Studio open every derived form in the CODE editor instead of the
    /// WinForms designer, and a non-public base hides every chrome property from the property grid.
    /// </remarks>
    [DesignerCategory("Form")]
    public class SpriteForm : Form, IControlFaceSource
    {
        const int ChromeColor = unchecked((int)0xFF2E343C);
        const int WhiteColor = unchecked((int)0xFFFFFFFF);
        const int TextGap = 8;                              // px between two bar elements
        const int DefaultShadow = unchecked((int)0xFF101014);
        const int MinBarHeight = 18;                        // the smallest bar that still fits the 5x7 pixel font at scale 1
        const int MaxBarHeight = 64;

        readonly TitleBarStrip bar;
        bool barLive;              // false while the base Form constructor runs: it resizes the window, and the overridden OnResize / OnTextChanged must not touch the bar yet
        readonly SpriteButton bMin = new() { Text = "\u2013", Shape = ButtonShape.Square, Dock = DockStyle.Right, TextFontFamily = "Segoe UI", BackColor = Color.FromArgb(0x3A, 0x41, 0x4C) };
        readonly SpriteButton bMax = new() { Text = "\u25a1", Shape = ButtonShape.Square, Dock = DockStyle.Right, TextFontFamily = "Segoe UI Symbol", BackColor = Color.FromArgb(0x3A, 0x41, 0x4C) };
        readonly SpriteButton bClose = new() { Text = "\u00d7", Shape = ButtonShape.Square, Dock = DockStyle.Right, TextFontFamily = "Segoe UI", BackColor = Color.FromArgb(0x3A, 0x41, 0x4C) };
        readonly List<TitleTag> tags = new();
        SpriteMenu? menu;
        Icon? titleIcon;
        Sprite? iconSprite;                                  // the icon as pixels, rebuilt when the icon or its size changes
        IntPtr iconSpriteHandle; int iconSpriteSize = -1;
        Rectangle normalBounds;                              // where full screen came from (where to land back)
        bool fullscreen, fullEnabled = true, dragBar, dragRestores;
        Point dragPos;
        int iconClickZone = -1;                              // x range of the icon, so a click on it opens the window menu

        // ---- bar appearance (see the properties below) ------------------------------------------------
        int barColor = ChromeColor, borderColor = ChromeColor, borderThickness = 6, resizeBand = 6;
        int stripeSeed = -1, stripeSeedValue, stripeBarWidth, stripeBarCount = 8, stripeBarExtra, stripeSkew = -1, stripeLineThickness = 1;
        int stripeOffset, stripeBrightness = 100;              // 0 px shift, 100 % = the colours as picked
        StripeBarsAlign stripeAlign = StripeBarsAlign.Left;
        TitleDarkenMode darkenMode;                            // None: no darkening plate
        int darkenExtraWidth = 4, darkenExtraHeight = 2, darkenFeatherWidth = 4, darkenFeatherHeight = 2, darkenOpacity = 55;
        bool smoothChrome = true;                              // buffered painting + a bar buffer that survives a resize drag
        StripeBarsMode stripeMode = StripeBarsMode.CaptionLength;
        StripeBarsStyle stripeStyle = StripeBarsStyle.FullBars;
        bool stripesBehindText = true;        // the icon + the texts start at the left edge and sit ON the bars; false = they follow the pattern
        bool nativeFrame = true;              // WS_CAPTION + WS_THICKFRAME stay on, WM_NCCALCSIZE hands the whole frame back to the client area
        int[] stripePalette = Array.Empty<int>();
        int titleColor = unchecked((int)0xFFFFFFFF), shadowColor = DefaultShadow, shadowOffset = 1;
        bool textShadow = true;
        string titleFontFamily = ""; float titleFontSize;

        public SpriteForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            Size = new Size(960, 640);
            Text = " ";
            stripeSeedValue = Environment.TickCount;              // StripeSeed = -1: one random seed for the life of the form
            bar = new TitleBarStrip(this) { Height = MinBarHeight };
            barLive = true;
            Controls.Add(bar);
            bar.Controls.Add(bMin); bar.Controls.Add(bMax); bar.Controls.Add(bClose);
            ApplyPaintStyle();
            ApplyFrame();
            LayoutBar();
            bMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
            bMax.Click += (_, _) => ToggleMaximize();
            bMax.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ToggleFullScreen(); };   // right click on maximize = full screen
            bClose.Click += (_, _) => Close();
            bClose.MouseEnter += (_, _) => { bClose.AccentColor = Color.FromArgb(0xE8, 0x11, 0x23); bClose.Accented = true; };   // the Windows close red
            bClose.MouseLeave += (_, _) => bClose.Accented = false;
            var tips = new ToolTip();
            tips.SetToolTip(bMin, "Minimize");
            tips.SetToolTip(bMax, "Maximize / restore  (right click = full screen)");
            tips.SetToolTip(bClose, "Close");
        }

        // ---- API ---------------------------------------------------------------------------------
        /// <summary>Height of the title bar in px (the buttons follow it). 18 (the default, and the minimum) is the
        /// smallest bar that still fits the built-in pixel font at its smallest size (5x7 glyphs, scale 1).</summary>
        [Category("Appearance"), DefaultValue(MinBarHeight), Description("Height of the custom title bar; the buttons follow it. 18 = the smallest (5x7 pixel font at scale 1).")]
        public int TitleBarHeight { get => bar.Height; set { bar.Height = Math.Clamp(value, MinBarHeight, MaxBarHeight); LayoutBar(); } }
        /// <summary>Bar background colour - the colour the pattern, the icon and the texts sit on. This is the
        /// colour of the strip itself - the window's own background is <see cref="Control.BackColor"/>. Edits in
        /// the designer with the usual colour cell (swatch, name, the Custom / Web / System drop-down).</summary>
        [Category("Appearance"), Description("Title bar background colour - the strip, not the window (that is BackColor).")]
        public Color TitleBarColor
        {
            get => Color.FromArgb(barColor);
            set { barColor = value.ToArgb(); bar.Touch(); }
        }
        private void ResetTitleBarColor() => TitleBarColor = Color.FromArgb(ChromeColor);
        private bool ShouldSerializeTitleBarColor() => barColor != ChromeColor;
        /// <summary>Show the minimize button.</summary>
        [Category("Appearance"), DefaultValue(true), Description("Show the minimize button on the title bar.")]
        public bool ShowMinimizeButton { get => bMin.Visible; set { bMin.Visible = value; ApplyMinimumSize(); bar.Touch(); } }
        /// <summary>Show the maximize / restore button (double-click on the bar does the same, right click = full screen).</summary>
        [Category("Appearance"), DefaultValue(true), Description("Show the maximize / restore button on the title bar (right click on it = full screen).")]
        public bool ShowMaximizeButton { get => bMax.Visible; set { bMax.Visible = value; ApplyMinimumSize(); bar.Touch(); } }
        /// <summary>Show the close button.</summary>
        [Category("Appearance"), DefaultValue(true), Description("Show the close button on the title bar.")]
        public bool ShowCloseButton { get => bClose.Visible; set { bClose.Visible = value; ApplyMinimumSize(); bar.Touch(); } }
        /// <summary>Whether the window can go full screen at all (false: no right-click command, no menu command).</summary>
        [Category("Behavior"), DefaultValue(true), Description("Whether the window can go full screen at all (right click on the maximize button, or the title bar menu).")]
        public bool FullScreenEnabled { get => fullEnabled; set { fullEnabled = value; if (!value && fullscreen) ToggleFullScreen(); } }
        /// <summary>Full screen covers the working area (the taskbar stays visible) instead of the whole monitor.</summary>
        [Category("Behavior"), DefaultValue(false), Description("Full screen keeps the taskbar visible (uses the working area instead of the whole monitor).")]
        public bool FullScreenExcludeTaskbar { get; set; }
        /// <summary>Whether the window is currently full screen (as opposed to plainly maximized).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsFullScreen => fullscreen;

        /// <summary>Icon drawn in the bar. null (the default) = the form's own <see cref="Form.Icon"/>, exactly like a
        /// native title bar; set it to draw something else, use <see cref="Form.ShowIcon"/> to hide the slot.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Icon? TitleIcon { get => titleIcon; set { titleIcon = value; bar.Touch(); } }

        // ---- the colour bars ----------------------------------------------------------------------
        /// <summary>Show the decorative colour bars in the left corner of the bar.</summary>
        [Category("Appearance"), DefaultValue(true), Description("Show the decorative skewed colour bars in the left corner of the title bar.")]
        public bool ShowStripes { get => stripeMode != StripeBarsMode.None; set { stripeMode = value ? (stripeMode == StripeBarsMode.None ? StripeBarsMode.CaptionLength : stripeMode) : StripeBarsMode.None; bar.Touch(); } }
        /// <summary>What the bar run mirrors: nothing, the caption, every title text, or a fixed number of bars.</summary>
        [Category("Appearance"), DefaultValue(StripeBarsMode.CaptionLength), Description("What the colour bars mirror: no bars, the caption, all title text, or a fixed number of bars (StripeBarCount). With StripeBarWidth > 0 the number of bars in the two text modes becomes whatever covers that length.")]
        public StripeBarsMode StripeMode { get => stripeMode; set { stripeMode = value; bar.Touch(); } }
        /// <summary>Number of bars in <see cref="StripeBarsMode.FixedCount"/> - and, while <see cref="StripeBarWidth"/>
        /// is 0, the number the mirrored length is divided between there. Ignored in the two text modes when
        /// <see cref="StripeBarWidth"/> is positive: there the width decides how many bars are needed.</summary>
        [Category("Appearance"), DefaultValue(8), Description("Number of colour bars in the FixedCount mode (and, with StripeBarWidth 0, the number the mirrored length is divided by). In the caption / all-text modes a positive StripeBarWidth decides the number instead.")]
        public int StripeBarCount { get => stripeBarCount; set { stripeBarCount = Math.Clamp(value, 0, 4096); bar.Touch(); } }
        /// <summary>Width of one bar in pixels, taken literally in every mode.
        /// 0 (the default) divides the length the pattern mirrors between the bars, so the run ends exactly at the
        /// last glyph. A positive value in <see cref="StripeBarsMode.CaptionLength"/> or
        /// <see cref="StripeBarsMode.AllTitleTextLength"/> makes the number of bars automatic - as many of these
        /// bars as the icon + text block needs (<c>length / width</c>, rounded up) - which is the
        /// "auto amount / custom width" pairing. <see cref="StripeBarsMode.FixedCount"/> keeps both numbers yours:
        /// <see cref="StripeBarCount"/> bars of this width, so the run may come out longer or shorter than the text.</summary>
        [Category("Appearance"), DefaultValue(0), Description("Width of one colour bar in px, honoured exactly. 0 = the bars divide the length they mirror. Positive in the caption / all-text modes: the bars keep this width and their NUMBER becomes however many of them cover the icon + text. FixedCount uses both values as given.")]
        public int StripeBarWidth { get => stripeBarWidth; set { stripeBarWidth = Math.Clamp(value, 0, 128); bar.Touch(); } }
        /// <summary>Bars ADDED to (or taken off) the number the other properties derive, so a run that is pinned to
        /// the caption length, to the all-text length or to <see cref="StripeBarWidth"/> can still be nudged by hand:
        /// the count becomes <c>derived + StripeBarExtra</c> and the length it mirrors is divided between that many
        /// bars. 0 (the default) = exactly what the mode says. It changes the number of bars, not the length they
        /// cover - so with <see cref="StripeBarWidth"/> 0 an extra bar makes every bar one share thinner, and with a
        /// literal width it makes the run that much longer. Negative removes bars; the count never goes below 0.</summary>
        [Category("Appearance"), DefaultValue(0), Description("Bars added to the number the other stripe properties derive (caption length, all title text, StripeBarCount, or the length divided by StripeBarWidth). +1 puts one more bar on the run, -1 takes one off; 0 = exactly what the mode says.")]
        public int StripeBarExtra { get => stripeBarExtra; set { stripeBarExtra = Math.Clamp(value, -4096, 4096); bar.Touch(); } }
        /// <summary>How far the bar run is moved right from where <see cref="StripeAlignment"/> puts it (px;
        /// negative pulls it back, and the run never goes left of the bar).</summary>
        [Category("Appearance"), DefaultValue(0), Description("Shifts the colour bar run right (or left) from where StripeAlignment puts it, in pixels.")]
        public int StripeOffset { get => stripeOffset; set { stripeOffset = Math.Clamp(value, -2048, 2048); bar.Touch(); } }
        /// <summary>Where the bar run sits: at the left end of the bar, centred on the bar, at the right end before
        /// the caption buttons, or centred on the icon + text block (<see cref="StripeBarsAlign.WithText"/>).</summary>
        [Category("Appearance"), DefaultValue(StripeBarsAlign.Left), Description("Where the colour bar run sits: left, centred in the bar, right (before the caption buttons), or centred on the icon + text block (WithText).")]
        public StripeBarsAlign StripeAlignment { get => stripeAlign; set { stripeAlign = value; bar.Touch(); } }
        /// <summary>Brightness of the bar colours in per cent (100 = as picked / as rolled from the seed, 0 = black,
        /// 200 = twice as bright, clipped per channel). Applies to an explicit palette and to random colours alike.</summary>
        [Category("Appearance"), DefaultValue(100), Description("Brightness of the colour bars in per cent (100 = unchanged, 0 = black, 200 = twice as bright).")]
        public int StripeBrightness { get => stripeBrightness; set { stripeBrightness = Math.Clamp(value, 0, 300); bar.Touch(); } }
        /// <summary>How far the top of a bar sits right of its bottom, in pixels. -1 (default) = half the bar height.</summary>
        [Category("Appearance"), DefaultValue(-1), Description("How far the top of a bar is right of its bottom, in pixels; -1 = half the bar height.")]
        public int StripeSkew { get => stripeSkew; set { stripeSkew = Math.Clamp(value, -256, 256); bar.Touch(); } }
        /// <summary>Full parallelograms, or the same bars only as a short line at the top and the bottom of the bar.</summary>
        [Category("Appearance"), DefaultValue(StripeBarsStyle.FullBars), Description("Full height bars, or the bars only as a line at the top and at the bottom of the title bar.")]
        public StripeBarsStyle StripeStyle { get => stripeStyle; set { stripeStyle = value; bar.Touch(); } }
        /// <summary>Thickness of the top / bottom lines in <see cref="StripeBarsStyle.TopAndBottomLines"/>.</summary>
        [Category("Appearance"), DefaultValue(1), Description("Thickness in px of the top and bottom lines of the bars (the TopAndBottomLines style).")]
        public int StripeLineThickness { get => stripeLineThickness; set { stripeLineThickness = Math.Clamp(value, 1, 32); bar.Touch(); } }
        /// <summary>Seed of the random bar colours; -1 (the default) = one random seed picked when the form is created.
        /// Setting it (to -1 or to a number) re-rolls / re-fixes the colours and keeps them for every later repaint.</summary>
        [Category("Appearance"), DefaultValue(-1), Description("Seed of the random bar colours; -1 = random once, when the form is created.")]
        public int StripeSeed { get => stripeSeed; set { stripeSeed = value; stripeSeedValue = value < 0 ? Environment.TickCount : value; bar.Touch(); } }
        /// <summary>The bar colours as a list the designer can edit: AARRGGBB (or RRGGBB, #RRGGBB, a decimal
        /// ARGB integer, or a colour name like "Red") values separated by commas or spaces ("" = roll random
        /// colours from <see cref="StripeSeed"/> instead). The list cycles, so three colours on a nine bar run
        /// read 1,2,3,1,2,3,1,2,3. Everything is parsed to <c>int[]</c> HERE, in the setter - the bar paints from
        /// that array and never touches this string, so no repaint converts text. The getter writes back the same
        /// hex form the setter reads, so a designer round trip keeps the palette.</summary>
        [Category("Appearance"), DefaultValue(""), Description("Bar colours as AARRGGBB / RRGGBB / #RRGGBB values, decimal ARGB integers or names, separated by commas; empty = random colours from StripeSeed. Parsed once, on set - the paint reads the int array."),
         Editor(typeof(StripePaletteEditor), typeof(UITypeEditor))]
        public string StripeColorList
        {
            get => string.Join(",", Array.ConvertAll(stripePalette, c => c.ToString("X8", CultureInfo.InvariantCulture)));
            set => SetPalette(ParseColors(value));
        }
        /// <summary>Explicit bar colours (cycled); null / empty = random colours from <see cref="StripeSeed"/>.
        /// The same palette <see cref="StripeColorList"/> edits, as the integers the paint uses.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int[]? StripeColors { get => stripePalette.Length > 0 ? (int[])stripePalette.Clone() : null; set => SetPalette(value ?? Array.Empty<int>()); }

        void SetPalette(int[] cols)
        {
            if (cols.Length == stripePalette.Length)
            {
                for (int i = 0; i < cols.Length; i++) if (cols[i] != stripePalette[i]) { stripePalette = cols; bar.Touch(); return; }
                return;                                         // the same palette: no repaint to schedule
            }
            stripePalette = cols; bar.Touch();
        }

        /// <summary>Reads an "AARRGGBB, 4294901760, RRGGBB, #FF0000, Red" colour list: the same forms
        /// <see cref="ArgbColorConverter.TryParse"/> accepts for one colour (a value with 6 or fewer digits gets
        /// an opaque alpha), plus the names <see cref="Color.FromName(string)"/> knows. A token that is neither is
        /// skipped. Called by the setter only - <see cref="stripePalette"/> is the array the bar paints from.</summary>
        internal static int[] ParseColors(string? list)
        {
            if (string.IsNullOrWhiteSpace(list)) return Array.Empty<int>();
            var parts = list.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var cols = new int[parts.Length]; int n = 0;
            foreach (var raw in parts)
                if (ArgbColorConverter.TryParse(raw, out int v)) cols[n++] = v;      // hex, decimal or a name
            Array.Resize(ref cols, n);
            return cols;
        }
        /// <summary>Draw the icon and the texts OVER the bars, starting at the left edge of the bar (the default,
        /// like the reference look), or AFTER the pattern, which then keeps the left corner to itself.</summary>
        [Category("Appearance"), DefaultValue(true), Description("true (default): the icon and the texts are drawn over the colour bars. false: they start after the pattern.")]
        public bool StripesBehindText { get => stripesBehindText; set { stripesBehindText = value; bar.Touch(); } }
        /// <summary>The darkening plate behind the bar texts: a black rectangle sized to the text's pixel run
        /// (<see cref="TitleDarkenMode.BehindTitle"/> = the caption, <see cref="TitleDarkenMode.BehindAllText"/> =
        /// caption + every extra text), sized to the text's INK pixels and grown by the extra width / height, its
        /// edges feathered, drawn under the icon and the texts.</summary>
        [Category("Appearance"), DefaultValue(TitleDarkenMode.None), Description("The darkening plate behind the title texts: None, BehindTitle (the caption) or BehindAllText (caption + every extra text).")]
        public TitleDarkenMode TitleDarkenMode { get => darkenMode; set { darkenMode = value; bar.Touch(); } }
        /// <summary>How many px the plate is WIDER than the text's ink, on each side (left and right).</summary>
        [Category("Appearance"), DefaultValue(4), Description("How many px the darkening plate is wider than the text's ink, on each side (left and right).")]
        public int TitleDarkenExtraWidth { get => darkenExtraWidth; set { darkenExtraWidth = Math.Clamp(value, 0, 128); bar.Touch(); } }
        /// <summary>How many px the plate is TALLER than the text's ink, on each side (top and bottom).</summary>
        [Category("Appearance"), DefaultValue(2), Description("How many px the darkening plate is taller than the text's ink, on each side (top and bottom).")]
        public int TitleDarkenExtraHeight { get => darkenExtraHeight; set { darkenExtraHeight = Math.Clamp(value, 0, 128); bar.Touch(); } }
        /// <summary>How many px the plate's left and right edges fade out over ("feather width").</summary>
        [Category("Appearance"), DefaultValue(8), Description("How many px the darkening plate fades out over at its left and right edges (feather width).")]
        public int TitleDarkenFeatherWidth { get => darkenFeatherWidth; set { darkenFeatherWidth = Math.Clamp(value, 0, 256); bar.Touch(); } }
        /// <summary>How many px the plate's top and bottom edges fade out over ("feather height").</summary>
        [Category("Appearance"), DefaultValue(4), Description("How many px the darkening plate fades out over at its top and bottom edges (feather height).")]
        public int TitleDarkenFeatherHeight { get => darkenFeatherHeight; set { darkenFeatherHeight = Math.Clamp(value, 0, 128); bar.Touch(); } }
        /// <summary>How dark the plate is: 0 = invisible, 100 = solid black.</summary>
        [Category("Appearance"), DefaultValue(55), Description("Darkening opacity in % (0 = invisible, 100 = solid black).")]
        public int TitleDarkenOpacity { get => darkenOpacity; set { darkenOpacity = Math.Clamp(value, 0, 100); bar.Touch(); } }

        /// <summary>Draw the small rounded plate behind the title bar icon when it lands on the colour bars, so it
        /// stays readable over any bar colour (default true; only ever drawn when the icon does overlap the pattern).</summary>
        [Category("Appearance"), DefaultValue(true), Description("Draw a rounded plate behind the title bar icon where it sits over the colour bars.")]
        public bool IconBackdrop { get; set; } = true;

        // ---- the title texts ----------------------------------------------------------------------
        /// <summary>Colour of the caption. Edits in the designer with the usual colour cell.</summary>
        [Category("Appearance"), Description("Caption colour.")]
        public Color TitleTextColor
        {
            get => Color.FromArgb(titleColor);
            set { titleColor = value.ToArgb(); bar.Touch(); }
        }
        private void ResetTitleTextColor() => TitleTextColor = Color.FromArgb(WhiteColor);
        private bool ShouldSerializeTitleTextColor() => titleColor != WhiteColor;
        /// <summary>Caption font family ("" = the app default / the built-in pixel font). Any installed family
        /// here switches the caption from the pixel font to that real, anti-aliased font.</summary>
        [Category("Appearance"), DefaultValue(""), Description("Caption font family (empty = the app default or the built-in pixel font).")]
        public string TitleTextFontFamily { get => titleFontFamily; set { titleFontFamily = value ?? ""; bar.Touch(); } }
        /// <summary>Caption em size in px when a real font is used (0 = the app default).</summary>
        [Category("Appearance"), DefaultValue(0f), Description("Caption em size in px with a real font (0 = the app default).")]
        public float TitleTextFontSize { get => titleFontSize; set { titleFontSize = Math.Max(0, value); bar.Touch(); } }
        /// <summary>The caption font as one value, for the designer's font dialog: setting it writes
        /// <see cref="TitleTextFontFamily"/> (the family) and <see cref="TitleTextFontSize"/> (its size in px);
        /// the style (bold / italic) is carried by the family name, so "Segoe UI Semibold" works.
        /// The pixel font has no font file behind it, so an empty family reports nothing here.</summary>
        [Category("Appearance"), Description("Caption font (the designer's font dialog): the family and its size in px. Empty family = the built-in pixel font."),
         DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Font? TitleTextFont
        {
            get => titleFontFamily.Length == 0 ? null : SafeFont(titleFontFamily, titleFontSize);
            set
            {
                if (value == null) { TitleTextFontFamily = ""; TitleTextFontSize = 0; return; }
                TitleTextFontFamily = value.OriginalFontName ?? value.Name;
                TitleTextFontSize = FontToPixels(value);
            }
        }
        /// <summary>The font dialog's size converted to device pixels (what a real font's em size means here),
        /// so the caption matches the bar buffer at any DPI.</summary>
        float FontToPixels(Font f)
        {
            float dpi = DeviceDpi > 0 ? DeviceDpi : 96f;
            return f.Unit switch
            {
                GraphicsUnit.Pixel => f.Size,
                GraphicsUnit.Point => f.Size * dpi / 72f,
                GraphicsUnit.Inch => f.Size * dpi,
                GraphicsUnit.Millimeter => f.Size * dpi / 25.4f,
                GraphicsUnit.Document => f.Size * dpi / 300f,
                _ => f.Size * dpi / 72f,
            };
        }
        static Font? SafeFont(string family, float px)
        {
            try { return new Font(family, px > 0 ? px : 12f, FontStyle.Regular, GraphicsUnit.Pixel); } catch { return null; }
        }
        /// <summary>Drop-shadow every title bar text by default (on): the caption and, unless a tag says otherwise, the extra texts.</summary>
        [Category("Appearance"), DefaultValue(true), Description("Draw the title bar texts with a drop shadow (the default; a TitleTag can override it).")]
        public bool TitleTextShadow { get => textShadow; set { textShadow = value; bar.Touch(); } }
        /// <summary>Shadow colour of the title bar texts; fully transparent = no shadow. Edits in the designer
        /// with the usual colour cell.</summary>
        [Category("Appearance"), Description("Drop-shadow colour of the title bar texts; fully transparent = no shadow.")]
        public Color TitleTextShadowColor
        {
            get => Color.FromArgb(shadowColor);
            set { shadowColor = value.ToArgb(); bar.Touch(); }
        }
        private void ResetTitleTextShadowColor() => TitleTextShadowColor = Color.FromArgb(DefaultShadow);
        private bool ShouldSerializeTitleTextShadowColor() => shadowColor != DefaultShadow;
        /// <summary>Shadow distance in glyph pixels (1 = one pixel-font row/column, scaled with the text).</summary>
        [Category("Appearance"), DefaultValue(1), Description("Drop-shadow distance of the title bar texts, in glyph pixels.")]
        public int TitleTextShadowOffset { get => shadowOffset; set { shadowOffset = Math.Clamp(value, 0, 8); bar.Touch(); } }
        // ---- the border ---------------------------------------------------------------------------
        // The frame is not a control: the painted part is the form's own background showing through
        // Padding, and the resize grab is WM_NCHITTEST (see WndProc), so the corners get the real Windows
        // resize cursors and the native resize / snap loop for free.
        /// <summary>Border band colour. Its alpha at 0 (fully transparent) makes the border invisible: the content
        /// then runs to the window edge and the band only stays a resize grab. Edits in the designer with the
        /// usual colour cell.</summary>
        [Category("Appearance"), Description("Border band colour; fully transparent = an invisible border (still resizable).")]
        public Color BorderColor
        {
            get => Color.FromArgb(borderColor);
            set { borderColor = value.ToArgb(); ApplyFrame(); }
        }
        private void ResetBorderColor() => BorderColor = Color.FromArgb(ChromeColor);
        private bool ShouldSerializeBorderColor() => borderColor != ChromeColor;
        /// <summary>Width in px of the painted border band (0 = no visible frame). The resize grab band is <see cref="ResizeBandWidth"/>.</summary>
        [Category("Appearance"), DefaultValue(6), Description("Width in px of the painted border band (0 = no visible frame).")]
        public int BorderThickness { get => borderThickness; set { borderThickness = Math.Clamp(value, 0, 24); ApplyFrame(); } }
        /// <summary>Width in px of the band around the window that starts the native resize loop (0 = no edge resize). Never smaller than the painted border.</summary>
        [Category("Appearance"), DefaultValue(6), Description("Width in px of the resize grab band around the window (0 = no edge resize).")]
        public int ResizeBandWidth { get => resizeBand; set { resizeBand = Math.Clamp(value, 0, 24); ApplyFrame(); } }
        /// <summary>
        /// Keeps the real Windows frame on the window (WS_CAPTION + WS_THICKFRAME) and takes the whole non-client
        /// area back into the client area in WM_NCCALCSIZE, so nothing of it is visible and the bar is still drawn
        /// by SR2D. Windows 11 decides whether a window is worth its minimize / restore / close animations, its
        /// drop shadow and its snap layouts by looking at those two style bits, so this is what brings the
        /// animations back - measured: without WS_CAPTION / WS_THICKFRAME the minimize is a single-frame cut, with
        /// them the same window fades and shrinks over a few frames, exactly like an ordinary window.
        /// False is the older fully borderless window (no animation, no snap layouts). Needs the handle, so
        /// changing it live rebuilds the window.
        /// </summary>
        [Category("Appearance"), DefaultValue(true), Description("Keep the invisible native window frame so Windows 11 animates minimize / restore / close and offers the snap layouts.")]
        public bool NativeWindowFrame { get => nativeFrame; set { if (nativeFrame == value) return; nativeFrame = value; if (IsHandleCreated) RecreateHandle(); } }
        /// <summary>
        /// Paints the chrome without the visible artefacts of a resize drag: the window's own background goes
        /// through a buffer instead of being erased on screen, and the title bar keeps an oversized pixel buffer
        /// so dragging the edge does not rebuild (and re-render) it for every pixel of the move. On by default;
        /// false is the plainer older path (a rebuild per resize step, which reads as a flicker).
        /// </summary>
        [Category("Behavior"), DefaultValue(true), Description("Buffer the chrome painting and keep an oversized title bar buffer, so resizing does not flicker.")]
        public bool SmoothChrome { get => smoothChrome; set { if (smoothChrome == value) return; smoothChrome = value; ApplyPaintStyle(); bar.Touch(); } }

        /// <summary>Raised after the window entered or left full screen.</summary>
        public event EventHandler? FullScreenChanged;

        /// <summary>Replaces the extra texts after the caption (each with its own colour / font / shadow; empty texts are skipped).</summary>
        public void SetTitleTags(params TitleTag[] items)
        {
            tags.Clear();
            if (items != null) foreach (var t in items) if (t != null && t.Text.Length > 0) tags.Add(t);
            bar.Touch();
        }

        /// <summary>The extra texts after the caption (read-only view; replace them with <see cref="SetTitleTags"/>).</summary>
        [Browsable(false)] public IReadOnlyList<TitleTag> TitleTags => tags;

        /// <summary>Maximize / restore (the button, the menu command, a double-click on the bar). Maximized keeps the taskbar visible.</summary>
        public void ToggleMaximize()
        {
            if (fullscreen) { ToggleFullScreen(); return; }
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            else { MaximizedBounds = Screen.FromControl(this).WorkingArea; WindowState = FormWindowState.Maximized; }
        }

        /// <summary>
        /// Enter / leave full screen: the whole monitor (or just the working area, see
        /// <see cref="FullScreenExcludeTaskbar"/>). The title bar stays; corner rounding and the resize
        /// grips switch off while it lasts. Full screen is an explicitly placed window rather than a maximized
        /// one, because a window that carries a real frame (<see cref="NativeWindowFrame"/>) can never be
        /// maximized to more than the working area - the system clamps that rectangle itself.
        /// </summary>
        public void ToggleFullScreen()
        {
            if (!fullEnabled) return;
            if (fullscreen)
            {
                fullscreen = false;
                SetRounding(true);
                WindowState = FormWindowState.Normal;              // FIRST leave the maximized state...
                Bounds = normalBounds;                             // ...THEN the bounds are ours again
            }
            else
            {
                normalBounds = WindowState == FormWindowState.Maximized ? RestoreBounds : Bounds;
                var scr = Screen.FromControl(this);
                var target = FullScreenExcludeTaskbar ? scr.WorkingArea : scr.Bounds;
                fullscreen = true;
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                Bounds = target;
                SetRounding(false);                                // no rounded corners in full screen
            }
            ApplyFrame();                                          // full screen: no frame, no resize grab
            bar.Touch();
            FullScreenChanged?.Invoke(this, EventArgs.Empty);
        }

        void LayoutBar()
        {
            bMin.Size = bMax.Size = bClose.Size = new Size(bar.Height + 2, bar.Height);   // square, Windows-style
            // the glyph has to fit the bar: at the 18 px default a 12 px em would be clipped
            float glyph = Math.Clamp(bar.Height - 6, 8, 20);
            bMin.TextFontSize = glyph; bMax.TextFontSize = glyph; bClose.TextFontSize = glyph;
            ApplyMinimumSize();
            bar.Touch();
        }

        /// <summary>
        /// The floor a window with this chrome can usefully have: the three caption buttons, the icon, a short
        /// caption and a strip of content under the bar. Below it the docked bar and the buttons are squeezed to
        /// nothing, which is how a resize-to-zero used to leave a window with no title bar at all. A larger
        /// minimum the app sets itself is never shrunk back.
        /// </summary>
        void ApplyMinimumSize()
        {
            if (!barLive) return;
            int frame = BorderPainted ? borderThickness * 2 : 0;
            var want = new Size(ButtonsWidth() + 4 * TextGap + 80 + frame, bar.Height + 40 + frame);
            if (MinimumSize.Width < want.Width || MinimumSize.Height < want.Height)
                MinimumSize = new Size(Math.Max(MinimumSize.Width, want.Width), Math.Max(MinimumSize.Height, want.Height));
        }

        /// <summary>Pixels the caption buttons take off the right end of the bar.</summary>
        int ButtonsWidth() => (bMin.Visible ? bMin.Width : 0) + (bMax.Visible ? bMax.Width : 0) + (bClose.Visible ? bClose.Width : 0);

        // ---- what the bar draws -------------------------------------------------------------------
        // One Sprite for the whole strip: the background, the colour bars, the icon, the caption and the
        // extra texts. The bar is the only control that paints them, so no child can cover the pattern and
        // no dock / z-order bookkeeping is involved.
        //
        // Everything is MEASURED first and painted second: the auto bar width needs the pixel width of the
        // text the pattern mirrors, and dimming the bars under the text needs to know where that text lands.
        int EffectiveSkew(int h) => stripeSkew < 0 ? h / 2 : stripeSkew;

        /// <summary>One piece of bar text with its font, its measured box and where it starts.</summary>
        struct BarText
        {
            public ControlText Font; public string Text; public int Color; public bool Shadow; public int ShadowColor;
            public int Scale; public Size Box; public int X;
            public Rectangle Ink;                       // the set pixels of the text, relative to the draw origin
        }

        /// <summary>The whole layout of one title bar, computed before a single pixel is painted.</summary>
        struct BarPlan
        {
            public int Count, BarW, Skew;               // the pattern: how many bars, how wide, how skewed
            public double Step;                         // the exact per-bar advance (BarW rounded away loses up to half a pixel per bar)
            public int RunX, RunW, ClipW;               // where the run starts, its full footprint, and what is drawn
            public List<BarText> Texts;
            public Sprite? Icon; public int IconX, IconY;
            public int TextX0, TextX1;                  // the span the icon and the texts cover
        }

        /// <summary>Measures the bar: the texts, the icon, and the pattern run that mirrors them.
        /// <paramref name="layoutW"/> is the width the strip is laid out for (the control's width),
        /// <paramref name="paintW"/> the buffer it is painted into, which <see cref="SmoothChrome"/> keeps
        /// oversized - the layout of a left-anchored run does not depend on the width, so one render serves a
        /// whole resize drag.</summary>
        /// <summary>A/B for the stripe length, harness <c>--skewbudget=0</c>. 1 (shipped) = with an auto bar
        /// width the run's slant is paid for out of the length it mirrors, so the silhouette of the last bar -
        /// what the eye reads as "where the colour ends" - stops at the last glyph. 0 = the old answer: the
        /// BASE of the run mirrored the text, so every bar's top edge stuck out past the caption by the skew
        /// (9 px at the default 18 px bar), which is the "stripe larger than the caption" report. Measured by
        /// harness --geom, which reads the painted silhouette row by row off a screen grab.</summary>
        internal static int SkewBudgetProbe = 1;

        /// <summary>A/B for how <see cref="StripeBarWidth"/> is honoured, harness <c>--widthcap=1</c>.
        /// 0 (shipped): the width is literal in every mode, and in the two text-mirror modes the NUMBER of bars is
        /// then whatever covers the mirrored length (<c>length / width</c>, rounded up). That is the pairing the
        /// bar is meant to offer: custom width / auto amount, or, with the width at 0, auto width / the mode's own
        /// amount. 1 = round eight: the count stayed one bar per character there and the width was capped to each
        /// bar's share of the text, which is exactly what made the painted bars narrower than the width in the
        /// property. Measured with harness --geom (plan arithmetic + the painted run read off a screen grab).</summary>
        internal static int WidthCapProbe = 0;

        BarPlan PlanBar(int layoutW, int paintW, int h)
        {
            var p = new BarPlan { Texts = new List<BarText>(tags.Count + 1) };
            var cap = new BarText { Font = CaptionText(), Text = Text ?? "", Color = titleColor, Shadow = textShadow, ShadowColor = shadowColor };
            cap.Scale = TextScale(titleFontSize, cap.Font, h); cap.Box = cap.Font.Measure(cap.Text, cap.Scale); cap.Ink = TextInk(cap.Font, cap.Text, cap.Scale);
            p.Texts.Add(cap);
            foreach (var t in tags)
            {
                if (t.Text.Length == 0) continue;
                var bt = new BarText
                {
                    Font = TagText(t), Text = t.Text, Color = t.Color.ToArgb(), Shadow = t.Shadow && textShadow,
                    ShadowColor = t.ShadowColor.ToArgb() != 0 ? t.ShadowColor.ToArgb() : shadowColor,
                };
                bt.Scale = TextScale(t.FontSize, bt.Font, h); bt.Box = bt.Font.Measure(bt.Text, bt.Scale); bt.Ink = TextInk(bt.Font, bt.Text, bt.Scale);
                p.Texts.Add(bt);
            }
            p.Icon = EnsureIconSprite(h);

            // the pixel width the pattern mirrors: the caption, or every text the bar writes
            int capW = Math.Max(0, cap.Box.Width), allW = capW;
            for (int i = 1; i < p.Texts.Count; i++) allW += p.Texts[i].Box.Width + TextGap;
            int iconW = p.Icon != null ? p.Icon.Width + TextGap : 0;
            int textW = stripeMode == StripeBarsMode.CaptionLength ? capW : allW;
            // The whole block the bars are furniture FOR: the icon slot (only when one is drawn) plus the texts the
            // mode counts - exactly the span the layout below writes into TextX0..TextX1. Mirroring the text pixels
            // alone stopped the last bar a whole icon short of the last glyph.
            int blockW = iconW + textW;
            // Left pins the run to the bar's own corner, so it has to reach past the block's left margin too; the
            // other alignments place the run ON the block, so the block itself is the length.
            int target = stripeAlign == StripeBarsAlign.Left
                ? Math.Max(0, TextGap + blockW - Math.Max(0, stripeOffset))
                : blockW;
            p.Skew = EffectiveSkew(h);
            // The bars are parallelograms that lean right by the skew, so the run's SILHOUETTE ends skew px
            // after its base. Mirroring the text with the base - what this used to do - therefore left the top
            // of the last bar sticking out past the last glyph by exactly the skew. With an auto width the
            // slant comes out of the length budget, so the silhouette's right edge is the text's right edge.
            int budget = SkewBudgetProbe == 0 ? target : Math.Max(0, target - p.Skew);
            // The two pairings (see WidthCapProbe): a positive StripeBarWidth is the width and the NUMBER of bars
            // is whatever covers the length; a width of 0 divides the length between the count the mode gives.
            // FixedCount is the exception - there both numbers are the user's own, so nothing is derived.
            // StripeBarExtra is then added to whichever number won: the knob that tweaks a count the other
            // properties already fixed (caption length + 1 bar, a width-derived run stretched one bar past the text).
            int count = (stripeBarWidth > 0 && WidthCapProbe == 0 && stripeMode != StripeBarsMode.FixedCount
                ? (int)Math.Ceiling(budget / (double)stripeBarWidth)
                : BarCount()) + stripeBarExtra;
            // The exact share of the mirrored length one bar gets - the ceiling step only ever adds to it.
            double share = count > 0 && budget > 0 ? budget / (double)count : 0.0;
            bool capped = stripeMode != StripeBarsMode.FixedCount && WidthCapProbe != 0;
            double step = stripeBarWidth > 0 ? (capped ? Math.Min(stripeBarWidth, share) : stripeBarWidth) : share;
            p.Count = Math.Clamp(count, 0, 4096);
            p.Step = step;
            p.BarW = step > 0 ? Math.Max(1, (int)Math.Round(step)) : 0;
            p.RunW = p.Count > 0 && p.BarW > 0 ? (int)Math.Round(p.Count * p.Step) + p.Skew : 0;

            // The pattern never runs under the caption buttons: a narrow window clips it instead of pushing the caption away.
            int room = Math.Max(0, layoutW - ButtonsWidth() - TextGap);
            p.RunX = stripeAlign switch
            {
                StripeBarsAlign.Center => Math.Max(0, (room - p.RunW) / 2),
                StripeBarsAlign.Right => Math.Max(0, room - p.RunW),
                // the run straddles the icon + text block: the same margin past the icon and past the last glyph
                StripeBarsAlign.WithText when stripesBehindText => Math.Max(0, TextGap + blockW / 2 - p.RunW / 2),
                // the texts sit after the pattern, so the two are placed as one centred group
                StripeBarsAlign.WithText => Math.Max(0, (room - (p.RunW + TextGap + blockW)) / 2),
                _ => 0,
            } + stripeOffset;
            if (p.RunX < 0) { p.RunW += p.RunX; p.RunX = 0; }              // an offset that pushes it off the left edge
            p.ClipW = Math.Clamp(p.RunW, 0, Math.Max(0, paintW - p.RunX)); // what actually gets painted into the buffer

            int x = stripesBehindText ? TextGap : Math.Max(p.RunX + Math.Min(p.RunW, room) + TextGap, TextGap);
            p.TextX0 = x;
            if (p.Icon != null)
            {
                p.IconX = x; p.IconY = Math.Max(0, (h - p.Icon.Height) / 2);
                x += p.Icon.Width + TextGap;
            }
            for (int i = 0; i < p.Texts.Count; i++)
            {
                var bt = p.Texts[i];
                bt.X = x;
                x += bt.Box.Width + TextGap;
                p.Texts[i] = bt;
            }
            p.TextX1 = Math.Max(p.TextX0, x - TextGap);
            ProbeCount = p.Count; ProbeBarW = p.BarW; ProbeSkew = p.Skew; ProbeRunX = p.RunX; ProbeRunW = p.RunW;
            ProbeMirrorW = target; ProbeTextW = textW; ProbeTarget = target;
            ProbeCapEnd = p.Texts[0].X + Math.Max(0, p.Texts[0].Box.Width); ProbeTextX0 = p.TextX0; ProbeTextX1 = p.TextX1;
            return p;
        }

        void RenderBar(Sprite s, int paintW, int layoutW, int h)
        {
            var p = PlanBar(layoutW, paintW, h);
            s.ClearRectXY(0, 0, paintW, h, barColor);
            if (p.Count > 0 && p.ClipW > 0) DrawBars(s, p, h);
            DarkenUnderText(s, p, h, paintW);
            iconClickZone = -1;
            if (p.Icon != null)
            {
                if (IconBackdrop && p.IconX < p.RunX + p.RunW) IconChip(s, p.IconX, p.IconY, p.Icon.Width, p.Icon.Height, h);
                s.Draw(p.Icon, p.IconX, p.IconY, SR2D.Op.AlphaBlend);
                iconClickZone = p.IconX + p.Icon.Width;
            }
            foreach (var bt in p.Texts) DrawBarText(s, bt, h);
        }

        /// <summary>The rounded plate drawn behind the bar icon where it sits on the colour bars.</summary>
        static void IconChip(Sprite s, int ix, int iy, int iw, int ih, int h)
        {
            int pad = Math.Clamp(ih / 5, 2, 4);
            float x = ix - pad, y = Math.Max(0, iy - pad);
            float bw = iw + pad * 2, bh = Math.Min(h, iy + ih + pad) - y;
            float r = Math.Min(pad + 1, Math.Min(bw, bh) / 3);
            s.FillRoundRect(x, y, bw, bh, r, unchecked((int)0xB4000000), SR2D.LineOp.AlphaBlend);
            s.DrawRoundRect(x, y, bw, bh, r, unchecked((int)0x40FFFFFF), 1f, true, SR2D.LineOp.AlphaBlend);
        }

        /// <summary>
        /// The text scale step of one bar text. The pixel font has to be stepped up with the bar (its glyphs are
        /// 5x7), while a real font's size is already an absolute px em - stepping it as well would overflow the
        /// bar, so an explicit size draws at 1 and only the app default follows the bar height.
        /// </summary>
        static int TextScale(float explicitSize, ControlText ct, int h) => ct.IsPixel
            ? Math.Clamp((h - 8) / PixelFont.Default.GlyphHeight, 1, 3)
            : explicitSize > 0 ? 1 : Math.Clamp(h / 24, 1, 3);

        /// <summary>Draws the bar run at its planned place, clipped to <see cref="BarPlan.ClipW"/> pixels.</summary>
        void DrawBars(Sprite s, BarPlan p, int h)
        {
            int sk = p.Skew, line = Math.Max(1, stripeLineThickness);
            var cols = new int[p.Count];
            var rnd = new Random(stripeSeedValue);
            for (int i = 0; i < p.Count; i++)
            {
                int c = stripePalette.Length > 0 ? stripePalette[i % stripePalette.Length] | unchecked((int)0xFF000000)
                                                 : Hsl(rnd.NextDouble() * 360.0, 0.85, 0.55);
                cols[i] = ScaleBrightness(c, stripeBrightness);
            }
            int left = p.RunX, wide = p.ClipW;
            if (stripeStyle == StripeBarsStyle.FullBars)
            {
                s.SetLockRectXY(left, 0, wide, h);
                Bars(s, cols, p.Step, sk, h, left);
                s.SetLockRect();
                return;
            }
            s.SetLockRectXY(left, 0, wide, Math.Min(line, h));           // the same bars, only the top line
            Bars(s, cols, p.Step, sk, h, left);
            s.SetLockRectXY(left, Math.Max(0, h - line), wide, line);     // ... and the bottom one
            Bars(s, cols, p.Step, sk, h, left);
            s.SetLockRect();
        }

        static void Bars(Sprite s, int[] cols, double step, int sk, int h, int left)
        {
            Span<PointF> pts = stackalloc PointF[4];
            for (int i = 0; i < cols.Length; i++)
            {
                // every bar's own edges land on the cumulative position, so neighbours still share an exact
                // edge (no seam) and the last one finishes where the run was measured to end
                int bx = left + (int)Math.Round(i * step), bw = (int)Math.Round((i + 1) * step) - (int)Math.Round(i * step);
                // pixel-corner coordinates: the engine fills rows [top, bottom), so 0..h covers the bar exactly
                pts[0] = new PointF(bx + sk, 0); pts[1] = new PointF(bx + sk + bw, 0);
                pts[2] = new PointF(bx + bw, h); pts[3] = new PointF(bx, h);
                s.FillPolygon(pts, cols[i]);
            }
        }

        /// <summary>
        /// The darkening plate: a black rectangle BEHIND the covered texts, sized to their INK (the pixels the
        /// glyphs set, from <see cref="TextInk"/> - not the advance box, which starts where a leading space starts
        /// and ends a gap past the last glyph), grown by <see cref="TitleDarkenExtraWidth"/> / ExtraHeight on each
        /// side, its edges feathered: the left / right columns fade over <see cref="TitleDarkenFeatherWidth"/> px,
        /// the top / bottom rows over <see cref="TitleDarkenFeatherHeight"/> px, the corner pixels over the product
        /// of the two ramps, all at <see cref="TitleDarkenOpacity"/> %. Drawn after the pattern and before the icon
        /// and the texts - it sits BEHIND them (and behind a text's drop shadow, which may reach past it).
        /// </summary>
        void DarkenUnderText(Sprite s, BarPlan p, int h, int paintW)
        {
            if (darkenMode == TitleDarkenMode.None || darkenOpacity <= 0 || p.Texts.Count == 0) return;
            int last = darkenMode == TitleDarkenMode.BehindTitle ? 1 : p.Texts.Count;
            int x0 = int.MaxValue, x1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
            for (int i = 0; i < last; i++)
            {
                var bt = p.Texts[i];
                if (bt.Ink.IsEmpty) continue;                       // a space: no ink, nothing to cover
                int ty = Math.Max(0, (h - bt.Box.Height) / 2);      // the same centring DrawBarText uses
                x0 = Math.Min(x0, bt.X + bt.Ink.X); x1 = Math.Max(x1, bt.X + bt.Ink.X + bt.Ink.Width);
                y0 = Math.Min(y0, ty + bt.Ink.Y); y1 = Math.Max(y1, ty + bt.Ink.Y + bt.Ink.Height);
            }
            if (x1 <= x0 || y1 <= y0) return;
            x0 = Math.Max(0, x0 - darkenExtraWidth); x1 = Math.Min(paintW, x1 + darkenExtraWidth);
            y0 = Math.Max(0, y0 - darkenExtraHeight); y1 = Math.Min(h, y1 + darkenExtraHeight);
            if (x1 <= x0 || y1 <= y0) return;
            // the ramp multiplies ALPHA BYTES - a pre-shifted int would overflow the 32 bits and the edges
            // would come out as garbage stripes (exactly the bug the screen shots caught)
            int baseA = darkenOpacity * 255 / 100;
            int fw = Math.Min(darkenFeatherWidth, (x1 - x0) / 2), fh = Math.Min(darkenFeatherHeight, (y1 - y0) / 2);
            int mx = x0 + fw, mw = x1 - x0 - fw * 2, my = y0 + fh, mh = y1 - y0 - fh * 2;
            if (mw > 0 && mh > 0) s.FillRect(mx, my, mw, mh, unchecked((int)((uint)baseA << 24)), SR2D.LineOp.AlphaBlend);
            for (int i = 0; i < fw; i++)                        // left / right edges: one column per ramp step
            {
                int c = unchecked((int)((uint)(baseA * (i + 1) / fw) << 24));
                s.FillRect(x0 + i, my, 1, mh, c, SR2D.LineOp.AlphaBlend);
                s.FillRect(x1 - 1 - i, my, 1, mh, c, SR2D.LineOp.AlphaBlend);
            }
            for (int j = 0; j < fh; j++)                        // top / bottom edges: one row per ramp step
            {
                int c = unchecked((int)((uint)(baseA * (j + 1) / fh) << 24));
                s.FillRect(mx, y0 + j, mw, 1, c, SR2D.LineOp.AlphaBlend);
                s.FillRect(mx, y1 - 1 - j, mw, 1, c, SR2D.LineOp.AlphaBlend);
            }
            for (int i = 0; i < fw; i++)                        // corners: the product of both ramps
                for (int k = 0; k < fh; k++)
                {
                    int c = unchecked((int)((uint)(baseA * (i + 1) / fw * (k + 1) / fh) << 24));
                    s.FillRect(x0 + i, y0 + k, 1, 1, c, SR2D.LineOp.AlphaBlend);
                    s.FillRect(x1 - 1 - i, y0 + k, 1, 1, c, SR2D.LineOp.AlphaBlend);
                    s.FillRect(x0 + i, y1 - 1 - k, 1, 1, c, SR2D.LineOp.AlphaBlend);
                    s.FillRect(x1 - 1 - i, y1 - 1 - k, 1, 1, c, SR2D.LineOp.AlphaBlend);
                }
        }

        /// <summary>How many bars the pattern has: one per caption character, one per character of all the bar
        /// text, or <see cref="StripeBarCount"/>.</summary>
        int BarCount()
        {
            if (stripeMode == StripeBarsMode.None) return 0;
            int n = stripeMode switch
            {
                StripeBarsMode.CaptionLength => Text?.Length ?? 0,
                StripeBarsMode.AllTitleTextLength => (Text?.Length ?? 0) + AllTagTextLength(),
                _ => stripeBarCount,
            };
            return Math.Clamp(n, 0, 4096);
        }

        int AllTagTextLength() { int n = 0; foreach (var t in tags) n += t.Text.Length; return n; }

        /// <summary>Scales a colour's channels by a brightness percentage (100 = unchanged), keeping alpha.</summary>
        static int ScaleBrightness(int argb, int percent)
        {
            if (percent == 100) return argb;
            int a = argb & unchecked((int)0xFF000000);
            int R = Math.Clamp((argb >> 16 & 0xFF) * percent / 100, 0, 255);
            int G = Math.Clamp((argb >> 8 & 0xFF) * percent / 100, 0, 255);
            int B = Math.Clamp((argb & 0xFF) * percent / 100, 0, 255);
            return unchecked((int)((uint)a | (uint)(R << 16) | (uint)(G << 8) | (uint)B));
        }

        /// <summary>Lays <paramref name="percent"/> of black over a colour (the per-stripe dimming).</summary>

        /// <summary>Draws one planned bar text (with its drop shadow when it has one).</summary>
        void DrawBarText(Sprite s, BarText bt, int h)
        {
            if (string.IsNullOrEmpty(bt.Text)) return;
            int ty = Math.Max(0, (h - bt.Box.Height) / 2);
            if (bt.Shadow && (bt.ShadowColor >>> 24) != 0)
            {
                int d = Math.Max(1, shadowOffset * bt.Scale);
                bt.Font.Draw(s, bt.X + d, ty + d, bt.Text, bt.ShadowColor, 0, bt.Scale, 0, 0, SR2D.LineOp.AlphaBlend);
            }
            bt.Font.Draw(s, bt.X, ty, bt.Text, bt.Color, 0, bt.Scale, 0, 0, SR2D.LineOp.AlphaBlend);
        }

        /// <summary>
        /// Where one bar text's INK sits, relative to its draw origin: the set pixels, not the advance box
        /// (which runs one gap past the last glyph and as far left as a leading space reaches). The darkening
        /// plate is aligned to this. Pixel font: <see cref="Sprite.MeasureTextInk"/>; a real font: the same
        /// render-and-scan against the ControlText's own draw.
        /// </summary>
        static Rectangle TextInk(ControlText ct, string text, int scale)
        {
            if (string.IsNullOrEmpty(text)) return Rectangle.Empty;
            if (ct.IsPixel) return Sprite.MeasureTextInk(text, scale);
            var box = ct.Measure(text, scale);
            if (box.Width <= 0 || box.Height <= 0) return Rectangle.Empty;
            int pad = 2 + scale;
            using var s = new Sprite(box.Width + pad * 2, box.Height + pad * 2);
            ct.Draw(s, pad, pad, text, unchecked((int)0xFFFFFFFF), 0, scale, 0, 0, SR2D.LineOp.Set);
            var ink = Sprite.InkBounds(s);
            return ink.IsEmpty ? Rectangle.Empty : new Rectangle(ink.X - pad, ink.Y - pad, ink.Width, ink.Height);
        }

        ControlText CaptionText() => ResolveText(titleFontFamily, titleFontSize);
        static ControlText TagText(TitleTag t) => ResolveText(t.FontFamily, t.FontSize);

        static ControlText ResolveText(string family, float size)
        {
            float px = size > 0 ? size : SpriteControlBase.DefaultFontSize;
            if (family.Length > 0 && ControlFonts.Get(family) is SpriteFont f) return new ControlText(f, ControlFonts.Get(family, true), px);
            if (family.Length == 0)
            {
                if (SpriteControlBase.DefaultFont is SpriteFont df) return new ControlText(df, null, px);
                if (SpriteControlBase.DefaultFontFamily.Length > 0 && ControlFonts.Get(SpriteControlBase.DefaultFontFamily) is SpriteFont ef)
                    return new ControlText(ef, ControlFonts.Get(SpriteControlBase.DefaultFontFamily, true), px);
            }
            return ControlText.Pixel;
        }

        /// <summary>The bar icon as pixels (null = no icon slot), rebuilt when the icon or the bar height changes.</summary>
        Sprite? EnsureIconSprite(int h)
        {
            var ic = titleIcon ?? Icon;
            if (ic == null || !ShowIcon) return null;
            int size = Math.Clamp(h - 8, 12, 32);
            if (iconSprite != null && iconSpriteHandle == ic.Handle && iconSpriteSize == size) return iconSprite;
            iconSprite?.Dispose();
            try
            {
                using var small = new Icon(ic, size, size);
                using var bmp = small.ToBitmap();
                // Icon.ToBitmap hands back the frame that is stored in the .ico (a multi-size application icon
                // gives 32x32 even when asked for 16), so the target size is passed to the sprite explicitly.
                iconSprite = new Sprite(bmp, SR2D.Transform.None, size, size);
                iconSpriteHandle = ic.Handle; iconSpriteSize = size;
                return iconSprite;
            }
            catch { iconSprite = null; iconSpriteHandle = IntPtr.Zero; iconSpriteSize = -1; return null; }
        }

        static int Hsl(double hue, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (hue < 60) { r = c; g = x; } else if (hue < 120) { r = x; g = c; } else if (hue < 180) { g = c; b = x; }
            else if (hue < 240) { g = x; b = c; } else if (hue < 300) { r = x; b = c; } else { r = c; b = x; }
            int R = (int)((r + m) * 255), G = (int)((g + m) * 255), B = (int)((b + m) * 255);
            return unchecked((int)(0xFF000000u | (uint)(R << 16) | (uint)(G << 8) | (uint)B));
        }

        // ---- window chrome: rounding, the border band and the resize grips ------------------------
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        void SetRounding(bool on)
        {
            // DWMWA_WINDOW_CORNER_PREFERENCE: DWMWCP_ROUND rounds the window AND draws the drop shadow on
            // Windows 11 - this is what gives a borderless window the real-window feel (pre-Win11: no-op).
            int pref = on ? 2 : 1;                          // DWMWCP_ROUND / DWMWCP_DONOTROUND
            _ = DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
        }

        /// <summary>True when the border band is drawn (an opaque colour and a non-zero thickness).</summary>
        bool BorderPainted => borderThickness > 0 && (borderColor & unchecked((int)0xFF000000)) != 0;

        const int WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
        const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;

        /// <summary>
        /// <c>FormBorderStyle.None</c> makes WinForms take WS_SYSMENU and WS_MINIMIZEBOX off the window; Windows 11
        /// reads those bits for the taskbar buttons and the snap layouts. On top of them, <see cref="NativeWindowFrame"/>
        /// (the default) also puts WS_CAPTION + WS_THICKFRAME back, because those - not the menu bits - are what
        /// DWM looks at when it decides whether the window gets its minimize / restore / close animations and its
        /// real drop shadow. Measured on this machine: the same window minimized with only the menu bits is one
        /// frame, with the caption + thick frame it fades over several. The frame never shows, because
        /// WM_NCCALCSIZE gives the whole non-client strip back to the client area (see <see cref="WndProc"/>),
        /// so the bar drawn by SR2D is still the only thing at the top.
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                if (nativeFrame) cp.Style |= WS_CAPTION | WS_THICKFRAME;
                // Redraw the whole window when its size changes, not just the difference. Resizing gives the
                // window a fresh composition surface, and only the invalid part of it is painted: without these
                // class styles the rest stays empty and whatever is behind the window shows through it. This is
                // the second line of defence - the first one is answering WM_NCPAINT at all, see
                // <see cref="NcPaintProbe"/>. Measured over five real drag resizes (harness --burst): with
                // WM_NCPAINT swallowed these styles took the blanked frames from 21 of 136 to 2 of 130, and with
                // it answered they are not needed (0 of 131 without them). Kept because they cost nothing:
                // harness --msg over 20 grow + 20 shrink steps gives the same WM_PAINT, barPaint and elapsed
                // either way, and only WM_ERASEBKGND moves (60 -> 80 of the swallowed paints).
                if (ResizeRedrawProbe != 0) cp.ClassStyle |= CS_VREDRAW | CS_HREDRAW;
                return cp;
            }
        }

        /// <summary>A/B for that: 1 (shipped) = <c>CS_VREDRAW | CS_HREDRAW</c> on the window class; 0 = the class
        /// without them, which is what made a resize drag blank the window out. Harness <c>--rrprobe=0</c>.</summary>
        internal static int ResizeRedrawProbe = 1;

        const int CS_VREDRAW = 0x0001, CS_HREDRAW = 0x0002;

        /// <summary>Width of the resize grab ring: the painted border plus <see cref="ResizeBandWidth"/>, whichever is wider; 0 while maximized or full screen.</summary>
        int GrabBand => fullscreen || WindowState != FormWindowState.Normal ? 0 : Math.Max(borderThickness, resizeBand);

        /// <summary>
        /// Puts the frame on the window. The band is <c>Padding</c>, and the form paints it itself in
        /// <see cref="OnPaintBackground"/> - the window's <c>BackColor</c> is the app's own property and is
        /// never written to here (an earlier version borrowed it for the border colour, which silently threw
        /// away a BackColor set in the designer).
        /// </summary>
        void ApplyFrame()
        {
            if (!barLive) return;
            if (BackColorProbe == 0)
                BackColor = Color.FromArgb(unchecked((int)(0xFF000000u | (uint)borderColor)));   // the A/B: what the frame used to do to your BackColor
            var want = BorderPainted && GrabBand > 0 ? new Padding(borderThickness) : new Padding(0);
            if (Padding != want) Padding = want;
            ApplyMinimumSize();                                     // the frame is part of the window's minimum
            bar.Blip();                                             // maximized / full screen has no band: the strip may change
        }

        /// <summary>The form's own painting: its background, then the border band on top of it.</summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            FormBackgroundPaintCount++;
            base.OnPaintBackground(e);
            if (!BorderPainted) return;
            var pad = Padding;
            if (pad.Bottom == 0) return;                            // no band (maximized / full screen / invisible border)
            var g = e.Graphics;
            int w = ClientSize.Width, h = ClientSize.Height;
            using var br = new SolidBrush(Color.FromArgb(unchecked((int)(0xFF000000u | (uint)borderColor))));
            g.FillRectangle(br, 0, 0, w, pad.Top);                  // the graphics object is already clipped to the update region
            g.FillRectangle(br, 0, h - pad.Bottom, w, pad.Bottom);
            g.FillRectangle(br, 0, 0, pad.Left, h);
            g.FillRectangle(br, w - pad.Right, 0, pad.Right, h);
        }

        /// <summary>Turns the buffered painting of <see cref="SmoothChrome"/> on or off.</summary>
        void ApplyPaintStyle() => SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, smoothChrome);

        /// <summary>
        /// A/B for the designer BackColor bug. 1 (shipped) = the frame never touches <see cref="Control.BackColor"/>,
        /// so a colour set in the designer survives to runtime. 0 = what ApplyFrame did before: it painted the border
        /// band by borrowing the form's BackColor, which overwrote the designer value with the border colour after
        /// InitializeComponent had run - the form came out dark grey however you set it. Harness --backprobe=0.
        /// </summary>
        internal static int BackColorProbe = 1;

        /// <summary>The face colour an SR2D control dropped straight on this form takes: the form's own
        /// <see cref="Control.BackColor"/> once the app has chosen one, and nothing (null) while it is still the
        /// WinForms default - an untouched form is light grey, and handing that over would turn a control designed
        /// dark into a light one with light text. A control that was given an explicit BackColor keeps it either way.
        /// This is what the designer's own <c>BackColor = Color.FromArgb(32, 36, 40)</c> line used to defeat: the value
        /// it writes is the SR2D default, so it is still "not chosen by the app" (see SpriteControlBase.ShouldSerializeBackColor).
        /// The test is the one Control.ShouldSerializeBackColor makes - that colour is not accessible to call.</summary>
        internal Color? ControlFaceColor => BackColor != SystemColors.Control ? BackColor : (Color?)null;
        Color? IControlFaceSource.ControlFaceColor => ControlFaceColor;   // explicit: an implicit one would have to be public

        /// <summary>The form's background changed: every SR2D control that sits directly on it and has no colour of
        /// its own follows, and the containers among them hand their own face on down.</summary>
        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            var c = ControlFaceColor;
            foreach (Control k in Controls) if (k is SpriteControlBase sc) sc.AdoptFormBackColor(c);
        }

        /// <summary>A/B for the resize blink, harness <c>--nccalc=0</c>. 1 (shipped) = WM_NCCALCSIZE answers
        /// "the client area is the whole window". 0 = the message goes to DefWindowProc, so the system keeps its
        /// invisible caption strip: the bar is pushed down and the frame looks native, but the resize loop runs
        /// without our answer - which is how the blink was localised to the frame interception.</summary>
        internal static int NcCalcProbe = 1;

        /// <summary>A/B for the resize blink, harness <c>--ncpaint=0</c>. 1 (shipped) = WM_NCPAINT goes to the
        /// default handler like any other window. 0 = what this shipped with: swallow it, because
        /// WM_NCCALCSIZE left no non-client strip to paint - and with nothing answering WM_NCPAINT, the fresh
        /// composition surface a resize hands the window is never validated outside the invalid part, so the
        /// desktop behind shows through it for a frame at a time. Measured by harness --burst over five real
        /// drag resizes (harness/matrix.sh, all four cells): swallowed = 21 of 136 sizing grabs blanked,
        /// forwarded = 0 of 131 and 0 of 135; an independent recorder behind a green backdrop
        /// (harness/record.ps1 + e2e.sh) sees 20 of 674 grabs green for the old answer and 0 for this one.
        /// Forwarding costs
        /// nothing visually because the strip has zero height: see harness --blur, the caption-over-the-bar
        /// regression lives in WM_NCACTIVATE (<see cref="NcActivateProbe"/>), not here.</summary>
        internal static int NcPaintProbe = 1;

        /// <summary>Bar repaints since the last reset: <see cref="BarPaintCount"/> blits, of which
        /// <see cref="BarRebuildCount"/> had to re-render the whole pattern, plus <see cref="FormBackgroundPaintCount"/>
        /// form background fills. For resize A/B measurements.</summary>
        internal static int BarPaintCount, BarRebuildCount, FormBackgroundPaintCount;
        internal static void ResetChromeCounters() { BarPaintCount = 0; BarRebuildCount = 0; FormBackgroundPaintCount = 0; }

        /// <summary>The last <see cref="PlanBar"/> measurement, for the pixel checks: the pattern's bar count /
        /// bar width / footprint against the pixel width of the text it mirrors and the span the texts cover.
        /// Written on every plan, read by harness --geom.</summary>
        internal static int ProbeCount, ProbeBarW, ProbeSkew, ProbeRunX, ProbeRunW, ProbeMirrorW, ProbeTextX0, ProbeTextX1, ProbeTextW, ProbeTarget, ProbeCapEnd;

        const int WM_NCHITTEST = 0x84;
        const int WM_NCCALCSIZE = 0x83, WM_NCPAINT = 0x85, WM_NCACTIVATE = 0x86;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Two jobs. The resize grab ring: the band around the client area reports itself as the matching HT* code,
        /// so Windows shows the right cursor (including the four CORNER ones, which the old grip controls never
        /// got) and runs the native resize loop. And with <see cref="NativeWindowFrame"/>: making the frame it now
        /// carries invisible - WM_NCCALCSIZE answers that the whole window rectangle is the client area, so the
        /// strip Windows reserves for a caption becomes ours (clamped back inside the monitor while maximized,
        /// see <see cref="ClientInsideMonitor"/>), and WM_NCPAINT / WM_NCACTIVATE are taken over so DWM
        /// never paints or flashes a native caption over the bar. The style bits stay, which is the point: the
        /// window keeps its shadow, rounded corners and open / close / minimize animations.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (nativeFrame && HideFrame(ref m)) return;
            if (m.Msg == WM_NCHITTEST)
            {
                int band = GrabBand;
                if (band > 0)
                {
                    // lParam: signed 16-bit screen coordinates (the high word is negative on a left / top monitor).
                    int lp = m.LParam.ToInt32();
                    int hit = HitTest(new Point((short)(ushort)(lp & 0xFFFF), (short)(ushort)((lp >> 16) & 0xFFFF)), band);
                    if (hit != 0) { m.Result = (IntPtr)hit; return; }
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>The frame messages of <see cref="NativeWindowFrame"/>; true when handled here.</summary>
        bool HideFrame(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                    if (NcCalcProbe == 0) return false;         // the A/B: let the system keep its caption strip
                    ClientInsideMonitor(m.LParam);
                    m.Result = IntPtr.Zero;                                   // client area = the whole window
                    return true;
                case WM_NCPAINT:
                    // Forwarded, like any other window: DefWindowProc validates the whole window surface for this
                    // paint, which is what a resize needs. Swallowing it (NcPaintProbe == 0, this unit's old answer)
                    // left everything outside the invalid part of the fresh surface unpainted - the window blinked
                    // out of existence for the length of the drag. There is still no caption strip in it, because
                    // WM_NCCALCSIZE above gave that strip to the client area.
                    if (NcPaintProbe == 0)
                    {
                        m.Result = IntPtr.Zero;
                        return true;
                    }
                    return false;
                case WM_NCACTIVATE when NcActivateProbe == 0 && m.LParam == IntPtr.Zero:
                    // The answer this shipped with, kept verbatim for the A/B: forward the deactivation to
                    // DefWindowProc with lParam = -1. Documented as "do not redraw the non-client area",
                    // measured as a redraw of exactly the strip WM_NCCALCSIZE gave back to the client -
                    // harness --ncprobe=0 --blur shows the grey caption with its three buttons.
                    m.Result = DefWindowProc(m.HWnd, m.Msg, m.WParam, (IntPtr)(-1));
                    return true;
                case WM_NCACTIVATE:
                    // The activation change must NOT reach the default handler. DefWindowProc repaints the non-client
                    // strip even when asked not to (lParam = -1 is documented as "do not redraw", measured as a redraw),
                    // and since WM_NCCALCSIZE gave that strip back to the client area, what it paints there is a plain
                    // grey Windows caption with three buttons - over the top of OUR bar, the moment another window gets
                    // the focus. Answering TRUE keeps the chrome ours; activating and deactivating is WM_ACTIVATE's job
                    // and is untouched by this.
                    m.Result = NcActivateProbe == 0
                        ? DefWindowProc(m.HWnd, m.Msg, m.WParam, (IntPtr)(-1))                   // the A/B: the old answer, which lost the chrome
                        : (IntPtr)1;
                    ChromeDirty();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Test hook, not an API (internal, so the harness can A/B it while the engine is compiled into the
        /// app): 1 or 2 = the shipped answer, refuse WM_NCACTIVATE so the system never paints a caption over the
        /// bar; 0 = the behaviour before the fix, hand it to DefWindowProc with lParam -1, which measured as a
        /// grey native caption appearing on every deactivation (harness <c>--ncprobe=0</c> + <c>--blur</c>).
        /// </summary>
        internal static int NcActivateProbe = 2;

        /// <summary>Nothing may sit between the system's non-client repaint and ours - the bar and the band go
        /// invalid again right away, so the next paint puts the SR2D chrome back on top.</summary>
        void ChromeDirty()
        {
            if (!barLive || !IsHandleCreated) return;
            bar.Invalidate();
            Invalidate(new Rectangle(0, 0, ClientSize.Width, Math.Max(Padding.Top, borderThickness) + bar.Height));
        }

        /// <summary>
        /// Answers WM_NCCALCSIZE with "the client area is the whole window": rgrc[0] arrives holding the window
        /// rectangle and is left alone, so nothing is reserved for a caption and every pixel of the window belongs
        /// to the form - which is what WinForms already assumes for a borderless form, so no layout code changes.
        /// A MAXIMIZED window is the exception: with a real frame the system places it one invisible border off
        /// screen on all four sides (measured on a 2048x1452 work area: a 2072x1476 window at -12,-12), and
        /// because the client now equals the window, that border would cut the bar's icon and its buttons off -
        /// so the rectangle is clamped back inside the monitor's working area.
        /// </summary>
        void ClientInsideMonitor(IntPtr lParam)
        {
            if ((GetWindowLong(Handle, GWL_STYLE) & WS_MAXIMIZE) == 0) return;
            var mi = new WinMonitorInfo { cbSize = Marshal.SizeOf<WinMonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST), ref mi)) return;
            var work = fullscreen && !FullScreenExcludeTaskbar ? mi.rcMonitor : mi.rcWork;
            var p = Marshal.PtrToStructure<WinNccalcsizeParams>(lParam);
            p.rgrc0.Left = Math.Max(p.rgrc0.Left, work.Left);
            p.rgrc0.Top = Math.Max(p.rgrc0.Top, work.Top);
            p.rgrc0.Right = Math.Min(p.rgrc0.Right, work.Right);
            p.rgrc0.Bottom = Math.Min(p.rgrc0.Bottom, work.Bottom);
            Marshal.StructureToPtr(p, lParam, false);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WinRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct WinNccalcsizeParams { public WinRect rgrc0, rgrc1, rgrc2; public IntPtr lppos; }

        [StructLayout(LayoutKind.Sequential)]
        struct WinMonitorInfo { public int cbSize; public WinRect rcMonitor; public WinRect rcWork; public int dwFlags; }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref WinMonitorInfo lpmi);

        const int GWL_STYLE = -16, WS_MAXIMIZE = 0x01000000, MONITOR_DEFAULTTONEAREST = 2;

        int HitTest(Point screen, int band)
        {
            Point p = PointToClient(screen);
            int w = ClientSize.Width, h = ClientSize.Height;
            if (p.X < 0 || p.Y < 0 || p.X >= w || p.Y >= h) return 0;
            int corner = Math.Max(band, 12);                       // a wider corner grab than the plain edge, like a real window
            bool left = p.X < corner, right = p.X >= w - corner, top = p.Y < band, bottom = p.Y >= h - band;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (p.X < band) return HTLEFT;
            if (p.X >= w - band) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            return 0;
        }

        // The bar has to keep the BACK of the z-order: WinForms lays docked children out from the back
        // forward, so the bar claims its strip first and the app's content fills what is left.
        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (barLive && !ReferenceEquals(e.Control, bar) && Controls.GetChildIndex(bar) != Controls.Count - 1) bar.SendToBack();
        }

        // ---- dragging / clicking the bar (the bar drives it; see TitleBarStrip) --------------------
        internal void BarDragStart(Point screen)
        {
            if (WindowState != FormWindowState.Normal && WindowState != FormWindowState.Maximized) return;
            dragBar = true;
            dragRestores = WindowState == FormWindowState.Maximized;
            dragPos = screen;
        }

        internal void BarDragMove(Point screen)
        {
            if (!dragBar) return;
            if (screen == dragPos) return;
            if (dragRestores)
            {
                dragRestores = false;
                // keep the grab point where it was on the caption, relative to the restored width
                float frac = Math.Clamp((dragPos.X - Left) / (float)Math.Max(1, Width), 0.05f, 0.95f);
                WindowState = FormWindowState.Normal;
                Location = new Point(screen.X - (int)(RestoreBounds.Width * frac), screen.Y - bar.Height / 2);
            }
            else Location = new Point(Location.X + screen.X - dragPos.X, Location.Y + screen.Y - dragPos.Y);
            dragPos = screen;
        }

        internal void BarDragEnd() { dragBar = false; dragRestores = false; }

        internal void BarMenu(Point at)
        {
            menu ??= BuildMenu();
            menu.Show(bar, at);
        }

        SpriteMenu BuildMenu()
        {
            var m = new SpriteMenu();
            m.Add("Restore", () => { if (fullscreen) ToggleFullScreen(); else if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal; },
                  null, () => fullscreen || WindowState == FormWindowState.Maximized);
            m.Add("Minimize", () => WindowState = FormWindowState.Minimized);
            m.AddCheck("Maximized", () => !fullscreen && WindowState == FormWindowState.Maximized, on => ToggleMaximize(), () => !fullscreen);
            if (fullEnabled) m.AddCheck("Full screen", () => fullscreen, on => ToggleFullScreen());
            return m;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetRounding(true);                                                 // Windows 11 rounds the corners
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!barLive) return;                               // the base constructor is sizing the form
            if (WindowState == FormWindowState.Maximized && !fullscreen)
            {   // a maximized (or Aero-snapped) window never covers the taskbar
                var wa = Screen.FromControl(this).WorkingArea;
                if (MaximizedBounds != wa) MaximizedBounds = wa;
            }
            bMax.Text = !fullscreen && WindowState == FormWindowState.Maximized ? "\u29c9" : "\u25a1";   // restore glyph while maximized
            ApplyFrame();                                                 // a maximized / full screen window has no frame
            bar.Blip();                                                   // the size alone must not rebuild the strip
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (barLive) bar.Touch();            // the caption (and the bar count) follow the title
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(value);
            if (value && !fullscreen && WindowState != FormWindowState.Minimized)
                MaximizedBounds = Screen.FromControl(this).WorkingArea;        // a borderless maximize must not swallow the taskbar
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { menu?.Dispose(); menu = null; iconSprite?.Dispose(); iconSprite = null; titleIcon = null; }
            base.Dispose(disposing);
        }

        /// <summary>
        /// The title bar strip: the whole thing (background, colour bars, icon, caption, extra texts) is
        /// rendered once into a cached <see cref="Sprite"/> and blitted, and it owns the drag / double-click
        /// / menu gestures of the bar. The minimize / maximize / close buttons are its only children.
        /// </summary>
        sealed class TitleBarStrip : Control
        {
            readonly SpriteForm f;
            Sprite? buf;
            string stamp = "";
            int capW, layoutW;                              // the pixel buffer is oversized on purpose, see SmoothChrome
            const int MaxCap = 4096;

            public TitleBarStrip(SpriteForm form)
            {
                f = form;
                Dock = DockStyle.Top;
                // StandardClick + StandardDoubleClick have to be switched on: a plain Control keeps the latter
                // off, and without it WinForms never calls OnMouseDoubleClick (this is why the old bar never
                // maximized on a double-click).
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                       | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            }

            /// <summary>Redraw the bar on the next paint (any appearance change calls this).</summary>
            public void Touch() { stamp = ""; Invalidate(); }

            /// <summary>The strip's size changed: the buffer is blitted as it is, and only rebuilt if the
            /// signature says something inside it actually differs. This is what a resize drag costs then.</summary>
            public void Blip() { Invalidate(); }

            /// <summary>Everything the bar paints, as one string: a change anywhere rebuilds the buffer.
            /// (Cheap - the bar repaints on user action, not per frame - and it cannot be forgotten. The
            /// control's WIDTH is deliberately not in here: a left-anchored strip looks the same at any width
            /// up to the buffer's capacity, which is the whole point of the oversized buffer.)</summary>
            string Signature()
            {
                var sb = new StringBuilder(128);
                sb.Append(Height).Append('|').Append(f.barColor).Append('|').Append(f.stripeMode)
                  .Append(f.stripeStyle).Append(f.stripeBarWidth).Append(f.stripeBarCount).Append(f.stripeBarExtra).Append(f.stripeSkew).Append(f.stripeLineThickness)
                  .Append(f.stripeOffset).Append(f.stripeAlign).Append(f.stripeBrightness)
                  .Append(f.stripesBehindText).Append(f.IconBackdrop)
                  .Append(f.stripeSeedValue).Append('|').Append(f.fullscreen).Append(f.WindowState)
                  .Append('|').Append(f.titleColor).Append(f.textShadow).Append(f.shadowColor).Append(f.shadowOffset)
                  .Append(f.darkenMode).Append(f.darkenExtraWidth).Append(f.darkenExtraHeight).Append(f.darkenFeatherWidth).Append(f.darkenFeatherHeight).Append(f.darkenOpacity)
                  .Append('|').Append(f.titleFontFamily).Append(f.titleFontSize)
                  .Append('|').Append(f.bMin.Visible).Append(f.bMax.Visible).Append(f.bClose.Visible)
                  .Append('|').Append(SpriteControlBase.DefaultFont?.GetHashCode() ?? 0).Append(SpriteControlBase.DefaultFontFamily).Append(SpriteControlBase.DefaultFontSize);
                foreach (int c in f.stripePalette) sb.Append(',').Append(c);
                var ic = f.titleIcon ?? f.Icon;
                sb.Append('|').Append(ic?.Handle.ToInt64() ?? 0).Append(f.ShowIcon);
                sb.Append('|').Append(f.Text);
                foreach (var t in f.tags) sb.Append('\n').Append(t.Text).Append(t.Color).Append(t.FontFamily).Append(t.FontSize).Append(t.Shadow).Append(t.ShadowColor);
                return sb.ToString();
            }

            /// <summary>True when the layout of the strip depends on the control's width: a centred or right
            /// aligned run moves with it, and texts that start after the pattern do too.</summary>
            bool LayoutFollowsWidth => f.stripeAlign != StripeBarsAlign.Left || !f.stripesBehindText;

            // Instrumentation for the resize A/B (see SpriteForm.ResetChromeCounters).
            protected override void OnPaint(PaintEventArgs e)
            {
                BarPaintCount++;
                int w = Math.Max(1, Width), h = Math.Max(1, Height);
                string s = Signature();
                if (buf == null || s != stamp || buf.Height != h || w > capW || (LayoutFollowsWidth && w != layoutW))
                {
                    BarRebuildCount++;
                    capW = f.smoothChrome ? Math.Min(MaxCap, Math.Max(w, w + w / 2)) : w;
                    buf?.Dispose();
                    buf = new Sprite(capW, h);
                    stamp = s; layoutW = w;
                    f.RenderBar(buf, capW, w, h);
                }
                var r = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
                if (r.IsEmpty) return;
                var hdc = e.Graphics.GetHdc();
                try { buf.PaintToDevice(new HandleRef(this, hdc), r.X, r.Y, r); }
                finally { e.Graphics.ReleaseHdc(hdc); }
            }

            protected override void OnPaintBackground(PaintEventArgs e) { }

            // ---- the gestures of the bar: drag, double-click maximize, the window menu -------------
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                if (e.X < f.iconClickZone && f.iconClickZone > 0) { f.BarMenu(e.Location); return; }   // the icon opens the window menu, like a native title bar
                f.BarDragStart(Cursor.Position);
                Capture = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                f.BarDragMove(Cursor.Position);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                f.BarDragEnd();
                Capture = false;
                if (e.Button == MouseButtons.Right) f.BarMenu(e.Location);
            }

            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                base.OnMouseDoubleClick(e);
                if (e.Button != MouseButtons.Left) return;
                if (e.X < f.iconClickZone && f.iconClickZone > 0) { f.Close(); return; }               // double-click the icon = close, like Windows
                f.ToggleMaximize();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { buf?.Dispose(); buf = null; }
                base.Dispose(disposing);
            }
        }
    }
}
