// The designer side of the chrome colours.
//
// Every SR2D colour is one 0xAARRGGBB integer, because that is what the engine's pixel calls take. Left to
// itself the Visual Studio property grid shows those as a signed decimal number (-13750212), which nobody can
// read or set on purpose. The two classes here are what the [TypeConverter] / [Editor] attributes on the
// colour properties of SpriteForm point at:
//
//   ArgbColorConverter  parses and writes one colour for the palette list (StripeColorList / StripeColors):
//                       "Red" for a colour that has a name, "[A=255, R=46, G=52, B=60]" for one that does not,
//                       and back AARRGGBB / RRGGBB (with or without a leading #), 0x..., decimal integers and
//                       colour names.
//   StripePaletteEditor the "..." dialog for the bar palette (a list of colours) entry by entry.
//
// The SpriteForm colour properties themselves are System.Drawing.Color now (2026-10-08): a Color-typed
// property gets the native designer experience for free - the swatch rectangle, the colour name (or the
// component values) and the drop-down arrow with the Custom / Web / System pages, exactly like BackColor.
// The custom editor that used to fake this for int properties is gone; the engine keeps working from the
// int argb the property stores at its setter.
//
// The colour dialog picks RGB; the alpha byte of the value is kept as it was (and an alpha of 0 is read as
// opaque, since a colour picker cannot express "invisible" and an invisible chrome colour is never what
// someone clicking a colour swatch meant). Type the hex, or pick Transparent from the list, for the alpha.
//
// These types must stay PUBLIC: the property grid instantiates them from the attribute in the designer's own
// process, and a designer-visible control property is only useful if its editor type is reachable too.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Globalization;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>Hex display / hex input for an 0xAARRGGBB colour property (see the file comment).</summary>
    public sealed class ArgbColorConverter : Int32Converter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
            => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object? value)
        {
            if (value is string s) return TryParse(s, out int argb) ? argb : base.ConvertFrom(context, culture, s);
            return value is null ? null : base.ConvertFrom(context, culture, value);
        }

        /// <summary>What the grid shows: the colour's name when it has one (like a native Color property), the
        /// A / R / G / B values when it does not. <see cref="TryParse"/> reads both forms back.</summary>
        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is int i) return ToDisplay(i);
            return base.ConvertTo(context, culture, value, destinationType);
        }

        // ---- the drop-down list of colours the grid shows for the value -------------------------------
        // Exactly the list a System.Drawing.Color property offers: every KnownColor, deduplicated by value, in
        // name order. Not exclusive, so the cell still takes typed hex, a decimal integer or the [A=.., R=..] text.
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) => Known.Argb;

        /// <summary>The colour's name (or its A / R / G / B values) - the text the grid shows.</summary>
        public static string ToDisplay(int argb) => Known.NameOf(argb) ?? FormatChannels(argb);

        static string FormatChannels(int argb) =>
            string.Format(CultureInfo.InvariantCulture, "[A={0}, R={1}, G={2}, B={3}]",
                          argb >> 24 & 0xFF, argb >> 16 & 0xFF, argb >> 8 & 0xFF, argb & 0xFF);

        /// <summary>"AARRGGBB" / "#RRGGBB" / "rgb" / "Red" / "[A=255, R=46, G=52, B=60]" / a decimal integer -
        /// anything the grid may hand back, plus everything the palette text field is documented to take.
        /// A bare digit string of eight or fewer characters is hex (the documented AARRGGBB / RRGGBB form,
        /// and what a value copied out of the engine's calls looks like); a longer one is decimal, since no hex
        /// colour is longer than eight and 4278190080 is the colour most people mean when they type one.</summary>
        internal static bool TryParse(string? text, out int argb)
        {
            argb = 0;
            var s = (text ?? "").Trim();
            if (s.Length == 0) return false;
            if (s.StartsWith("Color", StringComparison.OrdinalIgnoreCase)) s = s[5..].TrimStart();
            if (s[0] == '[' || s.IndexOf('=') >= 0) return TryParseChannels(s, out argb);
            if (s.StartsWith('#')) s = s[1..];        // the char overload is ordinal by definition
            if (s.StartsWith("0x", StringComparison.Ordinal) || s.StartsWith("0X", StringComparison.Ordinal)) s = s[2..];
            bool allDigits = true;
            for (int i = 0; i < s.Length; i++) if (!char.IsAsciiDigit(s[i])) { allDigits = false; break; }
            if (allDigits && s.Length > 8)            // a decimal ARGB / RGBA integer
            {
                if (!uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec)) return false;
                argb = unchecked((int)dec);
                return true;
            }
            if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            {
                argb = s.Length <= 6 ? v | unchecked((int)0xFF000000) : v;
                return true;
            }
            var named = Color.FromName(s);
            if (named.IsKnownColor) { argb = named.ToArgb(); return true; }
            return false;
        }

        /// <summary>The grid's own text form, read back: "[A=255, R=46, G=52, B=60]" in any order, alpha 255
        /// when it is not there.</summary>
        static bool TryParseChannels(string s, out int argb)
        {
            argb = 0;
            int a = 255, r = -1, g = -1, b = -1;
            foreach (var part in s.Trim('[', ']', ' ', '\t').Split(','))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) return false;
                if (!int.TryParse(part.AsSpan(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return false;
                switch (part.AsSpan(0, eq).Trim().ToString().ToUpperInvariant())
                {
                    case "A": a = Math.Clamp(v, 0, 255); break;
                    case "R": r = Math.Clamp(v, 0, 255); break;
                    case "G": g = Math.Clamp(v, 0, 255); break;
                    case "B": b = Math.Clamp(v, 0, 255); break;
                    default: return false;
                }
            }
            if (r < 0 || g < 0 || b < 0) return false;
            argb = unchecked((int)(((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
            return true;
        }

        /// <summary>The value the grid writes into code / the .resx for a string property (the bar palette).</summary>
        public static string ToHex(int argb) => argb.ToString("X8", CultureInfo.InvariantCulture);

        /// <summary>Named colours, built once: the grid asks for its standard values every time a property page
        /// is painted, and the list never changes.</summary>
        static class Known
        {
            /// <summary>value -> name, in the order the drop-down shows them (the first name of a value wins, so
            /// "Red" rather than "Red1").</summary>
            static readonly Dictionary<int, string> ByName;
            public static readonly StandardValuesCollection Argb;

            static Known()
            {
                var pairs = new List<(string Name, int Argb)>();
                foreach (KnownColor k in Enum.GetValues<KnownColor>())
                {
                    var c = Color.FromKnownColor(k);
                    if (c.Name.Length > 0) pairs.Add((c.Name, c.ToArgb()));
                }
                pairs.Sort(static (x, y) => string.CompareOrdinal(x.Name, y.Name));
                ByName = new Dictionary<int, string>();
                var values = new List<int>();
                foreach (var (name, argb) in pairs)
                {
                    if (ByName.ContainsKey(argb)) continue;
                    ByName[argb] = name;
                    values.Add(argb);
                }
                Argb = new StandardValuesCollection(values);
            }

            public static string? NameOf(int argb) => ByName.TryGetValue(argb, out string? n) ? n : null;
        }
    }

    /// <summary>
    /// The "..." button of <see cref="SpriteForm.StripeColorList"/>. The bar palette is a list, so the grid
    /// would otherwise offer one text box full of hex numbers - the dialog here is the colour picker applied
    /// to each entry: Add puts a new colour at the end, Edit (or double click) re-opens the standard colour
    /// dialog for the selected one, Remove takes it out. The value that goes back to the property is still
    /// the same AARRGGBB comma list the text box edited, so nothing about serialization changes.
    /// </summary>
    public sealed class StripePaletteEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context) => UITypeEditorEditStyle.Modal;

        public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider? provider, object? value)
        {
            var cols = new List<int>(SpriteForm.ParseColors(value as string));
            using var dlg = new PaletteForm(cols);
            if (dlg.ShowDialog() != DialogResult.OK) return value;
            return string.Join(",", cols.ConvertAll(c => c.ToString("X8", CultureInfo.InvariantCulture)));
        }

        /// <summary>The list itself: swatch + hex per row, Add / Edit / Remove under it. internal so the
        /// harness can open it against a real value and photograph it.</summary>
        internal sealed class PaletteForm : Form
        {
            readonly ListBox list;
            readonly List<int> cols;

            // The base Form disposes its Controls, but the analyzer wants the field it can see named.
            protected override void Dispose(bool disposing)
            {
                if (disposing) list?.Dispose();
                base.Dispose(disposing);
            }

            public PaletteForm(List<int> colors)
            {
                cols = colors;
                Text = "Stripe colours";
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterParent;
                // The sizes below are literals, so they are scaled by hand: a form built in code gets no
                // designer scaling, and at 175 % the unscaled window came out 280x340 device pixels around
                // DPI-scaled fonts - the button rows were then shorter than the buttons in them, and the
                // Fill list box painted over what stuck out (photographed: harness/paldlg.png).
                AutoScaleMode = AutoScaleMode.None;
                MinimumSize = new Size(Dpi(260), Dpi(200));
                Size = new Size(Dpi(280), Dpi(340));
                ShowInTaskbar = false;

                list = new ListBox
                {
                    Dock = DockStyle.Fill,
                    DrawMode = DrawMode.OwnerDrawFixed,
                    ItemHeight = Dpi(22),
                    SelectionMode = SelectionMode.One,
                    IntegralHeight = false,
                };
                list.DrawItem += OnDraw;
                list.DoubleClick += (_, _) => EditSelected();

                var add = new Button { Text = "Add", DialogResult = DialogResult.None, AutoSize = true, Padding = new Padding(Dpi(6), 0, Dpi(6), 0) };
                var edit = new Button { Text = "Edit", DialogResult = DialogResult.None, AutoSize = true, Padding = new Padding(Dpi(6), 0, Dpi(6), 0) };
                var remove = new Button { Text = "Remove", DialogResult = DialogResult.None, AutoSize = true, Padding = new Padding(Dpi(6), 0, Dpi(6), 0) };
                add.Click += (_, _) => { if (Pick(ChromeWhite, out int c)) { cols.Add(c); Reload(); } };
                edit.Click += (_, _) => EditSelected();
                remove.Click += (_, _) =>
                {
                    if (list.SelectedIndex < 0) return;
                    cols.RemoveAt(list.SelectedIndex); Reload();
                };

                // AutoSize, not a fixed Height: the row has to be as tall as the buttons it holds at this DPI.
                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    WrapContents = false, Padding = new Padding(Dpi(4), Dpi(4), Dpi(4), Dpi(2)),
                };
                buttons.Controls.AddRange(new Control[] { add, edit, remove });
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(Dpi(10), 0, Dpi(10), 0) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(Dpi(10), 0, Dpi(10), 0) };
                var bottom = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(Dpi(4)),
                };
                bottom.Controls.AddRange(new Control[] { ok, cancel });

                // The Fill control first: docked layout runs back to front, so it has to be the last one laid
                // out or it takes the whole client area and covers the button rows.
                Controls.Add(list);
                Controls.Add(buttons);
                Controls.Add(bottom);
                AcceptButton = ok; CancelButton = cancel;
                Reload();
            }

            /// <summary>Scales a design-time pixel value to this form's DPI.</summary>
            int Dpi(int v) => v * DeviceDpi / 96;

            void Reload()
            {
                int keep = list.SelectedIndex;
                list.BeginUpdate();
                list.Items.Clear();
                foreach (int _ in cols) list.Items.Add("");
                if (keep >= 0 && keep < list.Items.Count) list.SelectedIndex = keep;
                else if (list.Items.Count > 0) list.SelectedIndex = 0;
                list.EndUpdate();
            }

            void EditSelected()
            {
                int i = list.SelectedIndex;
                if (i < 0 || i >= cols.Count) return;
                if (Pick(cols[i], out int c)) { cols[i] = c; list.Invalidate(list.GetItemRectangle(i)); }
            }

            static bool Pick(int current, out int picked)
            {
                using var dlg = new ColorDialog
                {
                    FullOpen = true,
                    AnyColor = true,
                    CustomColors = Array.Empty<int>(),
                    Color = Color.FromArgb(unchecked((int)(0xFF000000u | (uint)current))),
                };
                if (dlg.ShowDialog() != DialogResult.OK) { picked = current; return false; }
                int alpha = current & unchecked((int)0xFF000000);
                if (alpha == 0) alpha = unchecked((int)0xFF000000);
                picked = unchecked((int)((uint)alpha | (uint)(dlg.Color.ToArgb() & 0x00FFFFFF)));
                return true;
            }

            void OnDraw(object? sender, DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= cols.Count) return;
                e.DrawBackground();
                int argb = cols[e.Index];
                var sw = new Rectangle(e.Bounds.X + Dpi(3), e.Bounds.Y + Dpi(3), Dpi(30), e.Bounds.Height - Dpi(6));
                using (var baseBrush = new SolidBrush(Color.White)) e.Graphics.FillRectangle(baseBrush, sw);
                using (var b = new SolidBrush(Color.FromArgb(argb))) e.Graphics.FillRectangle(b, sw);
                using (var pen = new Pen(Color.FromArgb(0x60, 0, 0, 0))) e.Graphics.DrawRectangle(pen, sw);
                TextRenderer.DrawText(e.Graphics, argb.ToString("X8", CultureInfo.InvariantCulture), e.Font,
                    new Rectangle(sw.Right + Dpi(8), e.Bounds.Y, e.Bounds.Right - sw.Right - Dpi(8), e.Bounds.Height),
                    SystemColors.WindowText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                e.DrawFocusRectangle();
            }

            const int ChromeWhite = unchecked((int)0xFFFFFFFF);
        }
    }
}
