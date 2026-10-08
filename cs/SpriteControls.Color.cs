using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    // SpriteColorPicker + SpriteColorDialog: an SR2D-drawn colour selector in the two shapes applications need - a
    // control to place on a form and a modal dialog around it (the ColorDialog stand-in).
    //
    // The 2D selector comes in three gamuts (SelectorMode): the classic hue square (x = saturation, y = value), a
    // brightness strip pair (x = hue, y = value, saturation on the bar below) and a hue wheel (angle = hue,
    // radius = saturation, brightness on the bar below). The numeric column reads and edits six colour schemes
    // (ColorScheme): RGB, HSB, HLS, YIQ, Lab, CMYK - the first three entries follow the scheme (labels and ranges
    // change), the last entry is alpha except for CMYK (where it is K; alpha goes through the alpha bar). A hex
    // entry round-trips #RGB / #RRGGBB / #AARRGGBB. The HSV maths is published as pure static functions so the
    // headless checks can pin the round trip.
    //
    //     var picker = new SpriteColorPicker { Value = Color.Coral };
    //     picker.ValueChanged += (_, _) => preview.BackColor = picker.Value;
    //
    //     using var dlg = new SpriteColorDialog { Value = current };
    //     if (dlg.ShowDialog(this) == DialogResult.OK) current = dlg.Value;
    //
    public sealed class SpriteColorPicker : SpriteControlBase
    {
        float _h;                                   // 0..<360
        float _s = 1f, _v = 1f;                     // 0..1
        int _a = 255;                               // 0..255
        Color _value;
        bool _showAlpha = true, _sync;
        ColorScheme _scheme = ColorScheme.Rgb;
        SelectorMode _mode = SelectorMode.HueSquare;
        readonly SpriteNumeric _c0, _c1, _c2, _cA;  // three scheme channels + alpha (CMYK: C M Y K, alpha bar only)
        readonly SpriteTextBox _hex;                // the hex readout / entry
        readonly SpriteCombo _modeBox, _schemeBox;

        public SpriteColorPicker()
        {
            _value = Color.FromArgb(DefaultAccent.ToArgb());
            _c0 = new SpriteNumeric { Size = new Size(64, 20) };
            _c1 = new SpriteNumeric { Size = new Size(64, 20) };
            _c2 = new SpriteNumeric { Size = new Size(64, 20) };
            _cA = new SpriteNumeric { Minimum = 0, Maximum = 255, Size = new Size(64, 20) };
            foreach (var n in new[] { _c0, _c1, _c2, _cA })
            {
                n.ValueChanged += (_, _) => { if (!_sync) FromNumeric(); };
                Controls.Add(n);
            }
            _hex = new SpriteTextBox { Size = new Size(78, 22), TextAlign = HorizontalAlignment.Center };
            _hex.Committed += (_, _) => ParseHex(_hex.Text);
            Controls.Add(_hex);
            _modeBox = new SpriteCombo { Size = new Size(78, 22) };
            _modeBox.SetItems(new[] { "Hue Square", "Brightness", "Wheel" }, 0);
            _modeBox.SelectedIndexChanged += (_, _) => { var old = _mode; _mode = (SelectorMode)_modeBox.SelectedIndex; if (old != _mode) { LayoutKids(); Redraw(); } };
            Controls.Add(_modeBox);
            _schemeBox = new SpriteCombo { Size = new Size(78, 22) };
            _schemeBox.SetItems(new[] { "RGB", "HSB", "HSL", "YIQ", "Lab", "CMYK" }, 0);
            // through the property: it re-ranges the entries with _sync raised. Re-ranging clamps their values, and a
            // clamp that leaks out as a user edit would feed e.g. RGB 200 into the Lab scale and corrupt the colour.
            _schemeBox.SelectedIndexChanged += (_, _) => { if ((ColorScheme)_schemeBox.SelectedIndex != _scheme) Scheme = (ColorScheme)_schemeBox.SelectedIndex; };
            Controls.Add(_schemeBox);
            ApplySchemeRanges();
            SyncNumerics();
            SyncHex();
            Size = new Size(244, 252);          // after the children exist: OnResize -> LayoutKids lays them out
        }

        // ------------------------------------------------------------------ public surface

        /// <summary>The selected colour (alpha included when <see cref="ShowAlpha"/>; with the alpha bar hidden the alpha is kept but not editable).</summary>
        [Category("Appearance"), Description("The selected colour.")]
        public Color Value
        {
            get => _value;
            set { if (value.ToArgb() != _value.ToArgb()) SetRgba(value.R, value.G, value.B, value.A, true); }
        }
        private bool ShouldSerializeValue() => _value.ToArgb() != DefaultAccent.ToArgb();
        private void ResetValue() => Value = Color.FromArgb(DefaultAccent.ToArgb());

        /// <summary>Raised after the colour changed (a drag, a click, an entry - a drag raises it per step, coalesced by the paint cycle).</summary>
        [Category("Action"), Description("Raised after the colour changed.")]
        public event EventHandler? ValueChanged;

        /// <summary>Show the alpha bar (over a checkerboard) and the alpha entry. The alpha of <see cref="Value"/> is always kept.</summary>
        [Category("Behavior"), DefaultValue(true), Description("Show the alpha bar and the alpha entry.")]
        public bool ShowAlpha { get => _showAlpha; set { if (_showAlpha == value) return; _showAlpha = value; LayoutKids(); Redraw(); } }

        /// <summary>The 2D selector gamut: the classic hue square, the brightness pair (x = hue, y = value) or the hue wheel.</summary>
        [Category("Appearance"), DefaultValue(SelectorMode.HueSquare), Description("The 2D selector gamut: hue square, brightness pair or hue wheel.")]
        public SelectorMode Mode { get => _mode; set { if (_mode == value) return; _mode = value; _modeBox.SelectedIndex = (int)value; LayoutKids(); Redraw(); } }

        /// <summary>The colour scheme the numeric column reads and edits.</summary>
        [Category("Appearance"), DefaultValue(ColorScheme.Rgb), Description("The colour scheme of the numeric column: RGB, HSB, HLS, YIQ, Lab or CMYK.")]
        public ColorScheme Scheme
        {
            get => _scheme;
            set
            {
                if (_scheme == value) return;
                _scheme = value;
                _schemeBox.SelectedIndex = (int)value;
                _sync = true;                    // re-ranging the numerics makes them clamp / re-commit - none of that is a user edit
                ApplySchemeRanges();
                SyncNumerics();
                _sync = false;
                LayoutKids();
                Redraw();
            }
        }

        /// <summary>The colour as "#AARRGGBB" (the hex entry accepts #RGB, #RRGGBB and #AARRGGBB, with or without '#').</summary>
        [Category("Appearance"), Description("The colour as a hex string (#AARRGGBB). Setting it parses #RGB, #RRGGBB and #AARRGGBB.")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]   // computed from the colour - nothing to serialize
        public string Hex
        {
            get => $"#{_a:X2}{_value.R:X2}{_value.G:X2}{_value.B:X2}";
            set => ParseHex(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // the children are in the Controls tree already; releasing them here too is what the analyzer
                // watches (CA2213), and double-dispose is safe
                _c0.Dispose(); _c1.Dispose(); _c2.Dispose(); _cA.Dispose();
                _hex.Dispose(); _modeBox.Dispose(); _schemeBox.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ colour maths (pure, pinned by ctlrun)

        /// <summary>Packed ARGB (alpha ignored) to HSV: h in 0..&lt;360, s and v in 0..1. Achromatic colours report s = 0, h = 0.</summary>
        internal static (float H, float S, float V) RgbToHsv(int argb)
        {
            int r = (argb >>> 16) & 255, g = (argb >>> 8) & 255, b = argb & 255;
            float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            float d = mx - mn;
            float h;
            if (d <= 0) h = 0;
            else if (mx == r) h = 60f * (((g - b) / d) % 6);
            else if (mx == g) h = 60f * ((b - r) / d + 2);
            else h = 60f * ((r - g) / d + 4);
            if (h < 0) h += 360;
            return (h, mx <= 0 ? 0 : d / mx, mx / 255f);
        }

        /// <summary>HSV (h in 0..&lt;360, s and v in 0..1) to packed ARGB with the given alpha.</summary>
        internal static int HsvToRgb(float h, float s, float v, int alpha = 255)
        {
            h %= 360; if (h < 0) h += 360;
            s = Math.Clamp(s, 0f, 1f); v = Math.Clamp(v, 0f, 1f);
            float c = v * s, x = c * (1 - MathF.Abs(h / 60f % 2f - 1)), m = v - c;
            float r, g, b2;
            switch ((int)(h / 60) % 6)
            {
                case 0: r = c; g = x; b2 = 0; break;
                case 1: r = x; g = c; b2 = 0; break;
                case 2: r = 0; g = c; b2 = x; break;
                case 3: r = 0; g = x; b2 = c; break;
                case 4: r = x; g = 0; b2 = c; break;
                default: r = c; g = 0; b2 = x; break;
            }
            return SR2D.ARGB((byte)Math.Clamp(alpha, 0, 255), (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b2 + m) * 255));
        }

        /// <summary>Packed ARGB (alpha ignored) to HSL: h in 0..&lt;360, l and s in 0..1.</summary>
        internal static (float H, float L, float S) RgbToHsl(int argb)
        {
            int r = (argb >>> 16) & 255, g = (argb >>> 8) & 255, b = argb & 255;
            float mx = Math.Max(r, Math.Max(g, b)) / 255f, mn = Math.Min(r, Math.Min(g, b)) / 255f;
            float l = (mx + mn) / 2, d = mx - mn;
            if (d <= 0) return (0, l, 0);
            float s = l > 0.5f ? d / (2 - mx - mn) : d / (mx + mn);
            float h = mx == r / 255f ? 60f * (((g - b) / 255f) / d % 6) : mx == g / 255f ? 60f * ((b - r) / 255f / d + 2) : 60f * ((r - g) / 255f / d + 4);
            if (h < 0) h += 360;
            return (h, l, s);
        }

        /// <summary>HSL (h in 0..&lt;360, l and s in 0..1) to packed ARGB with the given alpha.</summary>
        internal static int HslToRgb(float h, float s, float l, int alpha = 255)
        {
            h %= 360; if (h < 0) h += 360;
            s = Math.Clamp(s, 0f, 1f); l = Math.Clamp(l, 0f, 1f);
            float c = (1 - MathF.Abs(2 * l - 1)) * s, x = c * (1 - MathF.Abs(h / 60f % 2f - 1)), m = l - c / 2;
            float r, g, b2;
            switch ((int)(h / 60) % 6)
            {
                case 0: r = c; g = x; b2 = 0; break;
                case 1: r = x; g = c; b2 = 0; break;
                case 2: r = 0; g = c; b2 = x; break;
                case 3: r = 0; g = x; b2 = c; break;
                case 4: r = x; g = 0; b2 = c; break;
                default: r = c; g = 0; b2 = x; break;
            }
            return SR2D.ARGB((byte)Math.Clamp(alpha, 0, 255), (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b2 + m) * 255));
        }

        /// <summary>Packed ARGB (alpha ignored) to YIQ (the NTSC basis). Display scaling is the caller's business;
        /// this returns the raw normalised values (Y 0..1, I and Q roughly -0.6..0.6).</summary>
        internal static (float Y, float I, float Q) RgbToYiq(int argb)
        {
            float r = ((argb >>> 16) & 255) / 255f, g = ((argb >>> 8) & 255) / 255f, b = (argb & 255) / 255f;
            return (0.299f * r + 0.587f * g + 0.114f * b,
                    0.5959f * r - 0.2746f * g - 0.3213f * b,
                    0.2115f * r - 0.5227f * g + 0.3112f * b);
        }

        /// <summary>Normalised YIQ to packed ARGB with the given alpha.</summary>
        internal static int YiqToRgb(float y, float i, float q, int alpha = 255)
        {
            float r = y + 0.9563f * i + 0.6210f * q, g = y - 0.2721f * i - 0.6474f * q, b = y - 1.1070f * i + 1.7046f * q;
            return SR2D.ARGB((byte)Math.Clamp(alpha, 0, 255), (byte)Math.Clamp(MathF.Round(r * 255), 0, 255), (byte)Math.Clamp(MathF.Round(g * 255), 0, 255), (byte)Math.Clamp(MathF.Round(b * 255), 0, 255));
        }

        /// <summary>Packed ARGB (alpha ignored) to CIE L*a*b* (D65): L in 0..100, a and b in about -128..127.</summary>
        internal static (float L, float A, float B) RgbToLab(int argb)
        {
            float fx(float c) { c /= 255f; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }
            float r = fx((argb >>> 16) & 255), g = fx((argb >>> 8) & 255), b = fx(argb & 255);
            float x = (r * 0.4124f + g * 0.3576f + b * 0.1805f) / 0.95047f;
            float y = r * 0.2126f + g * 0.7152f + b * 0.0722f;
            float z = (r * 0.0193f + g * 0.1192f + b * 0.9505f) / 1.08883f;
            float f(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
            float fx2 = f(x), fy = f(y), fz = f(z);
            return (116 * fy - 16, 500 * (fx2 - fy), 200 * (fy - fz));
        }

        /// <summary>CIE L*a*b* (D65) to packed ARGB with the given alpha.</summary>
        internal static int LabToRgb(float L, float A, float B, int alpha = 255)
        {
            float fy = (L + 16) / 116, fx = fy + A / 500, fz = fy - B / 200;
            float finv(float t) => t * t * t > 0.008856f ? t * t * t : (t - 16f / 116f) / 7.787f;
            float x = 0.95047f * finv(fx), y = finv(fy), z = 1.08883f * finv(fz);
            float lin(float c) => c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
            float r = lin(3.2406f * x - 1.5372f * y - 0.4986f * z);
            float g = lin(-0.9689f * x + 1.8758f * y + 0.0415f * z);
            float b = lin(0.0557f * x - 0.2040f * y + 1.0570f * z);
            return SR2D.ARGB((byte)Math.Clamp(alpha, 0, 255), (byte)Math.Clamp(MathF.Round(r * 255), 0, 255), (byte)Math.Clamp(MathF.Round(g * 255), 0, 255), (byte)Math.Clamp(MathF.Round(b * 255), 0, 255));
        }

        /// <summary>Packed ARGB (alpha ignored) to CMYK (each 0..1).</summary>
        internal static (float C, float M, float Y, float K) RgbToCmyk(int argb)
        {
            float r = ((argb >>> 16) & 255) / 255f, g = ((argb >>> 8) & 255) / 255f, b = (argb & 255) / 255f;
            float k = 1 - Math.Max(r, Math.Max(g, b));
            if (k >= 1) return (0, 0, 0, 1);
            return ((1 - r - k) / (1 - k), (1 - g - k) / (1 - k), (1 - b - k) / (1 - k), k);
        }

        /// <summary>CMYK (each 0..1) to packed ARGB with the given alpha.</summary>
        internal static int CmykToRgb(float c, float m, float y, float k, int alpha = 255)
        {
            c = Math.Clamp(c, 0f, 1f); m = Math.Clamp(m, 0f, 1f); y = Math.Clamp(y, 0f, 1f); k = Math.Clamp(k, 0f, 1f);
            return SR2D.ARGB((byte)Math.Clamp(alpha, 0, 255), (byte)Math.Round(255 * (1 - c) * (1 - k)), (byte)Math.Round(255 * (1 - m) * (1 - k)), (byte)Math.Round(255 * (1 - y) * (1 - k)));
        }

        // ------------------------------------------------------------------ scheme plumbing

        void ApplySchemeRanges()
        {   // the 4th entry is K in CMYK and alpha in every other scheme - the range has to follow, or a visit to CMYK
            // would leave alpha clamped at 0..100 afterwards. Set BEFORE the switch: re-ranging clamps the value, and
            // the caller keeps _sync raised so none of those clamps is read back as a user edit.
            SetRange(_cA, 0, _scheme == ColorScheme.Cmyk ? 100 : 255);
            switch (_scheme)
            {
                case ColorScheme.Rgb: SetRange(_c0, 0, 255); SetRange(_c1, 0, 255); SetRange(_c2, 0, 255); break;
                case ColorScheme.Hsb: SetRange(_c0, 0, 359); SetRange(_c1, 0, 100); SetRange(_c2, 0, 100); break;
                case ColorScheme.Hsl: SetRange(_c0, 0, 359); SetRange(_c1, 0, 100); SetRange(_c2, 0, 100); break;
                case ColorScheme.Yiq: SetRange(_c0, 0, 100); SetRange(_c1, -100, 100); SetRange(_c2, -100, 100); break;
                case ColorScheme.Lab: SetRange(_c0, 0, 100); SetRange(_c1, -128, 127); SetRange(_c2, -128, 127); break;
                case ColorScheme.Cmyk: SetRange(_c0, 0, 100); SetRange(_c1, 0, 100); SetRange(_c2, 0, 100); break;
            }
        }

        static void SetRange(SpriteNumeric n, double min, double max) { n.Minimum = min; n.Maximum = max; }

        /// <summary>The channel labels the paint code draws left of the entries (the 4th is alpha unless CMYK).</summary>
        static string[] SchemeLabels(ColorScheme s) => s switch
        {
            ColorScheme.Hsb => new[] { "H", "S", "B" },
            ColorScheme.Hsl => new[] { "H", "L", "S" },
            ColorScheme.Yiq => new[] { "Y", "I", "Q" },
            ColorScheme.Lab => new[] { "L", "a", "b" },
            ColorScheme.Cmyk => new[] { "C", "M", "Y" },
            _ => new[] { "R", "G", "B" },
        };

        (float c0, float c1, float c2) RgbToSchemeValues(int rgb) => _scheme switch
        {
            ColorScheme.Hsb => Scale100(RgbToHsv(rgb)),
            ColorScheme.Hsl => Scale100(RgbToHsl(rgb)),
            ColorScheme.Yiq => Mul100(RgbToYiq(rgb)),
            ColorScheme.Lab => RgbToLab(rgb),
            ColorScheme.Cmyk => Cmyk100(RgbToCmyk(rgb)),
            _ => (((rgb >>> 16) & 255), ((rgb >>> 8) & 255), (rgb & 255)),
        };
        static (float, float, float) Scale100((float H, float S, float V) t) => (t.H, t.S * 100, t.V * 100);
        static (float, float, float) Mul100((float a, float b, float c) t) => (t.a * 100, t.b * 100, t.c * 100);
        static (float, float, float) Cmyk100((float C, float M, float Y, float K) t) => (t.C * 100, t.M * 100, t.Y * 100);

        /// <summary>RGB from the three numeric entries of the current scheme. CMYK is not here: it has a fourth
        /// channel (K, in the entry the other schemes use for alpha), so <see cref="FromNumeric"/> handles it
        /// before reaching this.</summary>
        int SchemeValuesToRgb(float c0, float c1, float c2) => _scheme switch
        {
            ColorScheme.Hsb => HsvToRgb(c0, c1 / 100f, c2 / 100f),
            ColorScheme.Hsl => HslToRgb(c0, c2 / 100f, c1 / 100f),
            ColorScheme.Yiq => YiqToRgb(c0 / 100f, c1 / 100f, c2 / 100f),
            ColorScheme.Lab => LabToRgb(c0, c1, c2),
            _ => SR2D.ARGB((byte)Math.Clamp(_a, 0, 255), (byte)Math.Clamp((int)c0, 0, 255), (byte)Math.Clamp((int)c1, 0, 255), (byte)Math.Clamp((int)c2, 0, 255)),
        };

        void FromNumeric()
        {
            if (_scheme == ColorScheme.Cmyk) { ApplyModelColour(CmykToRgb((float)_c0.Value / 100f, (float)_c1.Value / 100f, (float)_c2.Value / 100f, (float)_cA.Value / 100f, _a)); return; }
            int rgb = SchemeValuesToRgb((float)_c0.Value, (float)_c1.Value, (float)_c2.Value);
            SetRgba((rgb >>> 16) & 255, (rgb >>> 8) & 255, rgb & 255, _a, true);
        }

        /// <summary>A colour that came from another model (CMYK): the HSV state has to follow it, or the gamut keeps
        /// painting the previous hue and the marker sits where the old colour was.</summary>
        void ApplyModelColour(int packed)
        {
            var hsv = RgbToHsv(packed);
            if (hsv.S > 0) _h = hsv.H;                       // same rule as SetRgba: an achromatic result keeps the old hue
            _s = hsv.S; _v = hsv.V;
            SetRgba2(packed);
        }

        void SetRgba(int r, int g, int b, int a, bool fromRgb)
        {
            r = Math.Clamp(r, 0, 255); g = Math.Clamp(g, 0, 255); b = Math.Clamp(b, 0, 255); a = Math.Clamp(a, 0, 255);
            int packed = SR2D.ARGB((byte)a, (byte)r, (byte)g, (byte)b);
            if (packed == _value.ToArgb()) return;
            if (fromRgb)
            {
                var hsv = RgbToHsv(packed);
                // keep the old hue when the new colour is achromatic (pure grey / black / white has no hue -
                // snapping it to 0 would jump the selector's marker sideways for no visible reason)
                if (hsv.S > 0) _h = hsv.H;
                _s = hsv.S; _v = hsv.V;
            }
            _a = a;
            _value = Color.FromArgb(packed);
            SyncChildren();
            Redraw();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        void SetHsv(float h, float s, float v)
        {
            _h = h % 360; if (_h < 0) _h += 360;
            _s = Math.Clamp(s, 0f, 1f); _v = Math.Clamp(v, 0f, 1f);
            SetRgba2(HsvToRgb(_h, _s, _v, _a));
        }

        void SetHue(float h)
        {
            _h = h % 360; if (_h < 0) _h += 360;
            SetRgba2(HsvToRgb(_h, _s, _v, _a));
        }

        void SetValueAxis(float v) => SetHsv(_h, _s, v);
        void SetSaturation(float s) => SetHsv(_h, s, _v);

        void SetAlpha(int a) => SetRgba2(SR2D.ARGB((byte)Math.Clamp(a, 0, 255), (byte)((_value.ToArgb() >>> 16) & 255), (byte)((_value.ToArgb() >>> 8) & 255), (byte)(_value.ToArgb() & 255)));

        void SetRgba2(int packed)
        {
            if (packed == _value.ToArgb()) return;
            _value = Color.FromArgb(packed);
            SyncChildren();
            Redraw();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        void SyncChildren()
        {
            _sync = true;
            var (c0, c1, c2) = RgbToSchemeValues(_value.ToArgb());
            _c0.Value = (double)(float)c0; _c1.Value = (double)(float)c1; _c2.Value = (double)(float)c2;
            if (_scheme != ColorScheme.Cmyk) _cA.Value = _a; else _cA.Value = (double)(float)(RgbToCmyk(_value.ToArgb()).K * 100);
            string hx = Hex;
            if (!_hex.Focused && _hex.Text != hx) _hex.Text = hx;   // not while the user is typing one; also not on no-change moves
            _sync = false;
        }
        void SyncNumerics() => SyncChildren();
        void SyncHex() => SyncChildren();

        void ParseHex(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var span = text.Trim().TrimStart('#');
            try
            {
                int a = _a, rgb;
                if (span.Length == 3)
                {
                    rgb = (Convert.ToInt32(span[..1].ToString(), 16) * 17 << 16) | (Convert.ToInt32(span.Substring(1, 1), 16) * 17 << 8) | Convert.ToInt32(span.Substring(2, 1), 16) * 17;
                }
                else if (span.Length == 6) rgb = Convert.ToInt32(span, 16);
                else if (span.Length == 8) { a = Convert.ToInt32(span[..2], 16); rgb = Convert.ToInt32(span.Substring(2, 6), 16); }
                else return;
                SetRgba((rgb >>> 16) & 255, (rgb >>> 8) & 255, rgb & 255, a, true);
            }
            catch (FormatException) { /* not a hex number: keep the previous colour */ }
        }

        // ------------------------------------------------------------------ eyedropper: take a colour from anywhere on the desktop

        /// <summary>Test seam: null = sample the desktop itself (GDI CopyFromScreen - works over EVERY window, not just this app's).</summary>
        internal static Func<Point, int>? ScreenSampler = null;

        /// <summary>true while the eyedropper is armed: the cursor is the pipette over the whole screen, one click picks, Esc / right click cancels.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Picking { get; private set; }

        #pragma warning disable CA2213 // borrowed reference, not owned: system cursors are never disposed, the pipette is class-owned
        Cursor _cursorBefore = Cursors.Default;
        #pragma warning restore CA2213
        bool _armedByPress;                         // armed while the button was already down (a press on the preview): release commits
        Point _armPos;
        int _hover;                                 // the colour under the pipette (the preview shows it live while picking)
        static Cursor? _eyeCursor;                  // the pipette cursor, built once on Windows (the nib sits on the hotspot)

        /// <summary>Arms the eyedropper: pipette cursor everywhere (mouse capture), the preview shows the hovered pixel live.</summary>
        public void BeginPick()
        {
            if (Picking || !Enabled) return;
            Picking = true;
            _armedByPress = false;
            _hover = 0;
            _cursorBefore = Cursor;
            Cursor = EyeCursor();
            Capture = true;                         // every mouse message is ours now - anywhere on the desktop
            Focus();
            UpdateHover(Cursor.Position);
        }

        /// <summary>Lays the eyedropper down without taking a colour. A host needs this: a modal dialog with KeyPreview
        /// sees Esc before the picker does, so it cancels the sweep here instead of closing.</summary>
        public void CancelPick() { if (Picking) EndPick(false); }

        void EndPick(bool apply)
        {
            if (!Picking) return;
            Picking = false;                        // before releasing the capture: OnMouseCaptureChanged must not see it as a loss
            Cursor = _cursorBefore;
            if (apply) SetRgba((_hover >>> 16) & 255, (_hover >>> 8) & 255, _hover & 255, _a, true);
            if (Capture) Capture = false;
            Redraw();
        }

        void UpdateHover(Point screen)
        {
            // A desktop grab is a system call and it can fail (a coordinate that fell off every monitor mid-sweep).
            // Laying the pipette down is the only clean answer: otherwise Picking stays true, the capture is held and
            // the cursor is left swapped for the rest of the session.
            try { _hover = SampleScreen(screen); }
            catch (Exception) { EndPick(false); return; }
            Redraw();
        }

        internal static int SampleScreen(Point p)
        {
            if (ScreenSampler != null) return ScreenSampler(p);
            using var bmp = new Bitmap(1, 1);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(p.X, p.Y, 0, 0, new Size(1, 1), System.Drawing.CopyPixelOperation.SourceCopy);
            return bmp.GetPixel(0, 0).ToArgb();
        }

        static Cursor EyeCursor()
        {
            if (_eyeCursor != null) return _eyeCursor;
            if (!OperatingSystem.IsWindows()) return _eyeCursor = Cursors.Cross;    // headless runs / non-Windows desktops
            using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);         // a pipette with the nib exactly at the centre - the hotspot of a GetHicon cursor
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var outline = new Pen(Color.Black, 5f))
                using (var core = new Pen(Color.White, 2.6f))
                using (var nib = new Pen(Color.Black, 2.4f))
                {
                    g.DrawLine(outline, 17.5f, 17.5f, 25f, 25f);        // the barrel (black rim)
                    g.DrawLine(core, 18.2f, 18.2f, 24.6f, 24.6f);       // the glass core
                    g.FillEllipse(Brushes.Black, 22f, 22f, 9f, 9f);     // the bulb
                    g.FillEllipse(Brushes.White, 23.8f, 23.8f, 5.4f, 5.4f);
                    g.DrawLine(nib, 16f, 16f, 18.6f, 18.6f);            // the nib, apex on the hotspot
                }
            }
            _eyeCursor = new Cursor(bmp.GetHicon());    // the handle stays with the cursor
            return _eyeCursor;
        }

        // ------------------------------------------------------------------ layout

        Rectangle _selRect, _barRect, _alphaRect, _previewRect;
        const int BarH = 14, Gap = 6, RightW = 86;

        void LayoutKids()
        {
            if (_c0 == null) return;            // the designer / the base ctor may resize before the children exist
            int w = ClientSize.Width, h = ClientSize.Height;
            int leftW = Math.Max(8, w - RightW - Gap);
            int bars = 1 + (_showAlpha ? 1 : 0);
            int availH = Math.Max(8, h - (BarH + Gap) * bars);
            int side = Math.Min(leftW, availH);                 // the gamut is a SQUARE - it used to stretch over the full left width
            _selRect = new Rectangle(0, 0, side, side);
            _barRect = new Rectangle(0, _selRect.Bottom + Gap, side, BarH);         // the strips match the square, not the old full width
            _alphaRect = new Rectangle(0, _barRect.Bottom + Gap, side, _showAlpha ? BarH : 0);
            int rx = side + Gap;                                // the entry column starts right of the square
            _previewRect = new Rectangle(rx, 0, Math.Max(8, w - rx), 28);
            int y = _previewRect.Bottom + 4;
            _hex.SetBounds(rx, y, Math.Max(30, w - rx), 20); y += 24;
            _modeBox.SetBounds(rx, y, Math.Max(30, w - rx), 20); y += 24;
            _schemeBox.SetBounds(rx, y, Math.Max(30, w - rx), 20); y += 24;
            var nums = new[] { _c0, _c1, _c2, _cA };
            for (int i = 0; i < nums.Length; i++)
            {   // CMYK: the 4th entry is K (alpha goes through the alpha bar). With ShowAlpha off the 4th entry edits
                // nothing, so it leaves the column instead of sitting there unresponsive.
                bool vis = i < 3 || _showAlpha || _scheme == ColorScheme.Cmyk;
                if (nums[i].Visible != vis) nums[i].Visible = vis;
                if (!vis) continue;
                nums[i].SetBounds(rx + 12, y, Math.Max(20, w - rx - 12), 20);
                y += 24;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutKids();
            Redraw();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Picking && e.KeyCode == Keys.Escape) { EndPick(false); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Picking && !Capture) EndPick(false);    // the system took the capture away (alt-tab, another window): cancel
        }

        // ------------------------------------------------------------------ mouse

        enum Drag { None, Selector, Bar, Alpha }
        Drag _drag;

        float HueAt(int x) => 360f * Math.Clamp((x - _selRect.X) / Math.Max(1f, _selRect.Width - 1), 0f, 1f);

        void ApplyDrag(Point p)
        {
            if (_drag == Drag.Selector)
            {
                if (_mode == SelectorMode.HueSquare)
                {
                    float s = Math.Clamp((p.X - _selRect.X) / Math.Max(1f, _selRect.Width - 1), 0f, 1f);
                    float v = 1 - Math.Clamp((p.Y - _selRect.Y) / Math.Max(1f, _selRect.Height - 1), 0f, 1f);
                    SetHsv(_h, s, v);
                }
                else if (_mode == SelectorMode.Brightness)
                {   // x = hue, y = value; saturation stays on the bar
                    float h = HueAt(p.X); if (h >= 360) h = 359.999f;
                    float v = 1 - Math.Clamp((p.Y - _selRect.Y) / Math.Max(1f, _selRect.Height - 1), 0f, 1f);
                    SetHsv(h, _s, v);
                }
                else
                {   // the wheel: angle = hue, radius = saturation
                    float cx = _selRect.X + _selRect.Width / 2f, cy = _selRect.Y + _selRect.Height / 2f;
                    float dx = p.X - cx, dy = p.Y - cy;
                    float r = _selRect.Width / 2f - 4f;             // same radius the paint uses - the marker stays under the cursor at the edge
                    float len = MathF.Sqrt(dx * dx + dy * dy);
                    float h = (MathF.Atan2(dy, dx) * 180 / MathF.PI + 360) % 360;
                    SetHsv(h, Math.Clamp(len / Math.Max(1f, r), 0f, 1f), _v);
                }
            }
            else if (_drag == Drag.Bar)
            {
                if (_mode == SelectorMode.HueSquare)
                {   // the bar under the square is the hue strip
                    float h = 360f * Math.Clamp((p.X - _barRect.X) / Math.Max(1f, _barRect.Width - 1), 0f, 1f);
                    if (h >= 360) h = 359.999f;
                    SetHue(h);
                }
                else if (_mode == SelectorMode.Brightness)
                {
                    SetSaturation(Math.Clamp((p.X - _barRect.X) / Math.Max(1f, _barRect.Width - 1), 0f, 1f));
                }
                else
                {   // the bar's own gradient runs dark (left) -> bright (right), so the drag reads it the same way
                    SetValueAxis(Math.Clamp((p.X - _barRect.X) / Math.Max(1f, _barRect.Width - 1), 0f, 1f));
                }
            }
            else if (_drag == Drag.Alpha)
            {
                int a = (int)MathF.Round(255 * Math.Clamp((p.X - _alphaRect.X) / Math.Max(1f, _alphaRect.Width - 1), 0f, 1f));
                if (a != _a) SetAlpha(a);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Picking)
            {   // the pick click: one press takes the colour under the pipette and puts the regular cursor back
                if (e.Button == MouseButtons.Left) EndPick(true);
                else if (e.Button == MouseButtons.Right) EndPick(false);
                return;
            }
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Enabled && _previewRect.Contains(e.Location))
            {   // the pipette badge on the preview arms it; the button is down now, so the RELEASE after a sweep commits
                BeginPick();
                _armedByPress = true;
                _armPos = e.Location;
                return;
            }
            if (e.Button != MouseButtons.Left || !Enabled) return;
            _drag = _alphaRect.Contains(e.Location) && ShowAlpha ? Drag.Alpha
                  : _barRect.Contains(e.Location) ? Drag.Bar
                  : HitSelector(e.Location) ? Drag.Selector : Drag.None;
            if (_drag != Drag.None) { Capture = true; ApplyDrag(e.Location); }
        }

        bool HitSelector(Point p)
        {
            if (_mode != SelectorMode.Wheel) return _selRect.Contains(p);
            float cx = _selRect.X + _selRect.Width / 2f, cy = _selRect.Y + _selRect.Height / 2f;
            float dx = p.X - cx, dy = p.Y - cy;
            return dx * dx + dy * dy <= (_selRect.Width / 2f - 2) * (_selRect.Width / 2f - 2);   // the painted wheel reaches half side - 2.5
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Picking) { UpdateHover(PointToScreen(e.Location)); return; }    // live preview of the pixel under the pipette
            base.OnMouseMove(e);
            if (_drag != Drag.None && e.Button == MouseButtons.Left) ApplyDrag(e.Location);
            else if (IsHot) Cursor = _selRect.Contains(e.Location) || _barRect.Contains(e.Location) || (_alphaRect.Contains(e.Location) && ShowAlpha) ? Cursors.Cross : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (Picking && e.Button == MouseButtons.Left && _armedByPress)
            {   // press the preview, sweep anywhere, release = take the colour under the pipette (the eyedropper gesture)
                bool swept = Math.Abs(e.X - _armPos.X) + Math.Abs(e.Y - _armPos.Y) > 6;
                if (swept) EndPick(true);
                else _armedByPress = false;            // a plain badge click stays armed for the one-click pick
                return;
            }
            if (Picking) return;                       // the pick happened on the press; releasing it must not drag
            base.OnMouseUp(e);
            if (_drag != Drag.None) { _drag = Drag.None; Capture = false; }
        }

        // ------------------------------------------------------------------ paint

        static void Checker(Sprite s, Rectangle r)
        {
            for (int cy = 0; cy < r.Height; cy += 7)
                for (int cx = 0; cx < r.Width; cx += 7)
                    s.FillRect(r.X + cx, r.Y + cy, Math.Min(7, r.Width - cx), Math.Min(7, r.Height - cy),
                        ((cx / 7 + cy / 7) & 1) == 0 ? unchecked((int)0xFFC8C8C8) : unchecked((int)0xFF7A7A7A));
        }

        static void Marker(Sprite s, float x, float y, int height)
        {
            s.FillRect((int)x - 2, y, 4, height, Argb(Color.Black, 190), SR2D.LineOp.AlphaBlend);
            s.FillRect((int)x - 1, y, 2, height, White, SR2D.LineOp.AlphaBlend);
        }

        protected override void PaintControl(Sprite s)
        {
            int rim = Mix(BackColor, ThumbColor, 0.45f);
            int hue = HsvToRgb(_h, 1f, 1f);

            if (_mode == SelectorMode.HueSquare)
            {   // x = saturation (white -> pure hue), y = value (transparent -> black)
                s.FillRect(_selRect.X, _selRect.Y, _selRect.Width, _selRect.Height, SpriteGradient.Linear(_selRect.X, 0, _selRect.Right, 0, false, White, hue));
                s.FillRect(_selRect.X, _selRect.Y, _selRect.Width, _selRect.Height, SpriteGradient.Linear(0, _selRect.Y, 0, _selRect.Bottom, false, 0, unchecked((int)0xFF000000)));
                Ring(s, _selRect.X + _s * (_selRect.Width - 1), _selRect.Y + (1 - _v) * (_selRect.Height - 1));
                // the hue strip
                int[] hues = { 0, 60, 120, 180, 240, 300, 360 };
                for (int i = 0; i < 6; i++)
                {
                    int x0 = _barRect.X + (int)MathF.Round(_barRect.Width * i / 6f);
                    int x1 = _barRect.X + (int)MathF.Round(_barRect.Width * (i + 1) / 6f);
                    s.FillRect(x0, _barRect.Y, x1 - x0, _barRect.Height, SpriteGradient.Linear(x0, 0, x1, 0, false, HsvToRgb(hues[i], 1, 1), HsvToRgb(hues[i + 1], 1, 1)));
                }
                Marker(s, _barRect.X + _h / 360f * (_barRect.Width - 1), _barRect.Y, _barRect.Height);
            }
            else if (_mode == SelectorMode.Brightness)
            {   // x = hue, y = value - the square lives at the CURRENT saturation (drag the bar below and it re-saturates)
                int[] hues = { 0, 60, 120, 180, 240, 300, 360 };
                for (int i = 0; i < 6; i++)
                {
                    int x0 = _selRect.X + (int)MathF.Round(_selRect.Width * i / 6f);
                    int x1 = _selRect.X + (int)MathF.Round(_selRect.Width * (i + 1) / 6f);
                    s.FillRect(x0, _selRect.Y, x1 - x0, _selRect.Height, SpriteGradient.Linear(x0, 0, x1, 0, false, HsvToRgb(hues[i], _s, 1), HsvToRgb(hues[i + 1], _s, 1)));
                }
                s.FillRect(_selRect.X, _selRect.Y, _selRect.Width, _selRect.Height, SpriteGradient.Linear(0, _selRect.Y, 0, _selRect.Bottom, false, 0, unchecked((int)0xFF000000)));
                Ring(s, _selRect.X + _h / 360f * (_selRect.Width - 1), _selRect.Y + (1 - _v) * (_selRect.Height - 1));
                s.FillRect(_barRect.X, _barRect.Y, _barRect.Width, _barRect.Height, SpriteGradient.Linear(_barRect.X, 0, _barRect.Right, 0, false, HsvToRgb(_h, 0, _v), HsvToRgb(_h, 1, _v)));
                Marker(s, _barRect.X + _s * (_barRect.Width - 1), _barRect.Y, _barRect.Height);
            }
            else
            {   // the wheel: angle = hue, radius = saturation (drawn as radial lines - white centre out to the pure hue)
                float cx = _selRect.X + _selRect.Width / 2f, cy = _selRect.Y + _selRect.Height / 2f;
                float r = _selRect.Width / 2f - 4f;                 // the 3 px rays reach r + 1.5 - they used to poke 1 px past the rim
                s.FillCircle(cx, cy, r + 1, BackColor.ToArgb());
                for (int a = 0; a < 720; a++)
                {   // the wheel lives at the current value: the brightness bar below re-lights / darkens the whole gamut
                    float ang = a / 2f, rad = ang * MathF.PI / 180;
                    s.DrawWideLine(cx, cy, cx + MathF.Cos(rad) * r, cy + MathF.Sin(rad) * r, HsvToRgb(ang, 1, _v), 3f, true, SR2D.LineOp.Set);
                }
                float mx = cx + MathF.Cos(_h * MathF.PI / 180) * _s * r, my = cy + MathF.Sin(_h * MathF.PI / 180) * _s * r;
                s.DrawCircle(mx, my, 5.5f, Black, 1.6f, false, SR2D.LineOp.AlphaBlend);
                s.DrawCircle(mx, my, 4f, White, 1.4f, false, SR2D.LineOp.AlphaBlend);
                // the brightness bar (black -> the current colour)
                Checker(s, new Rectangle(_barRect.X, _barRect.Y, _barRect.Width, _barRect.Height));
                s.FillRect(_barRect.X, _barRect.Y, _barRect.Width, _barRect.Height, SpriteGradient.Linear(_barRect.X, 0, _barRect.Right, 0, false, unchecked((int)0xFF000000), HsvToRgb(_h, _s, 1)));
                Marker(s, _barRect.X + _v * (_barRect.Width - 1), _barRect.Y, _barRect.Height);
            }
            s.DrawRect(_selRect.X + 0.5f, _selRect.Y + 0.5f, _selRect.Width - 1, _selRect.Height - 1, rim, 1f, false, SR2D.LineOp.AlphaBlend);
            s.DrawRect(_barRect.X + 0.5f, _barRect.Y + 0.5f, _barRect.Width - 1, _barRect.Height - 1, rim, 1f, false, SR2D.LineOp.AlphaBlend);

            if (ShowAlpha)
            {
                Checker(s, _alphaRect);
                s.FillRect(_alphaRect.X, _alphaRect.Y, _alphaRect.Width, _alphaRect.Height,
                    SpriteGradient.Linear(_alphaRect.X, 0, _alphaRect.Right, 0, false, unchecked((int)0xFF000000) | (_value.ToArgb() & 0xFFFFFF), _value.ToArgb() & 0xFFFFFF));
                s.DrawRect(_alphaRect.X + 0.5f, _alphaRect.Y + 0.5f, _alphaRect.Width - 1, _alphaRect.Height - 1, rim, 1f, false, SR2D.LineOp.AlphaBlend);
                Marker(s, _alphaRect.X + _a / 255f * (_alphaRect.Width - 1), _alphaRect.Y, _alphaRect.Height);
            }

            // --- the right column: the colour over a mini checker, then the children (hex, mode, scheme, entries)
            Checker(s, _previewRect);
            int face = Picking ? _hover | unchecked((int)0xFF000000) : _value.ToArgb();
            s.FillRect(_previewRect.X, _previewRect.Y, _previewRect.Width, _previewRect.Height, face, SR2D.LineOp.AlphaBlend);
            s.DrawRect(_previewRect.X + 0.5f, _previewRect.Y + 0.5f, _previewRect.Width - 1, _previewRect.Height - 1, Picking ? AccentRaw.ToArgb() : rim, 1f, false, SR2D.LineOp.AlphaBlend);
            DrawPickerGlyph(s);

            // the entry labels (scheme channel letters + A, or C M Y K), left of each numeric
            var labels = SchemeLabels(_scheme);
            int numY = _previewRect.Bottom + 4 + 24 * 3;                  // hex, mode, scheme occupy the first three rows
            for (int i = 0; i < 4; i++)
            {
                if (i == 3 && !_showAlpha && _scheme != ColorScheme.Cmyk) break;   // the row is not laid out, no letter for it
                string txt = i < 3 ? labels[i] : _scheme == ColorScheme.Cmyk ? "K" : "A";
                T.Draw(s, _previewRect.X + 5, numY + 10, txt, Mix(Fore, AccentRaw, 0.35f), 0, 1, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.Center);
                numY += 24;
            }
        }

        void DrawPickerGlyph(Sprite s)
        {   // the pipette badge on the preview's right end - the click target that arms the eyedropper
            float gx = _previewRect.Right - 18.5f, gy = _previewRect.Y + 5.5f;
            SR2D.LineOp op = SR2D.LineOp.Set;
            s.DrawWideLine(gx + 8.2f, gy + 8.2f, gx + 13.2f, gy + 13.2f, Black, 5f, true, op);   // the barrel
            s.DrawWideLine(gx + 8.8f, gy + 8.8f, gx + 13f, gy + 13f, White, 2.6f, true, op);     // the glass core
            s.FillPolygon(stackalloc System.Drawing.PointF[] { new(gx + 7f, gy + 7f), new(gx + 10.4f, gy + 9.2f), new(gx + 9.2f, gy + 10.4f) }, Black, op, true);   // the nib
            s.FillCircle(gx + 14.4f, gy + 14.4f, 4.2f, Black);                                   // the bulb
            s.FillCircle(gx + 14.4f, gy + 14.4f, 2.2f, White);
        }

        static void Ring(Sprite s, float x, float y)
        {
            s.DrawCircle(x, y, 5.5f, Black, 1.6f, false, SR2D.LineOp.AlphaBlend);
            s.DrawCircle(x, y, 4f, White, 1.4f, false, SR2D.LineOp.AlphaBlend);
        }

    }

    /// <summary>The 2D selector gamut of <see cref="SpriteColorPicker"/>.</summary>
    public enum SelectorMode
    {
        /// <summary>The classic square: x = saturation, y = value; the strip below is the hue.</summary>
        HueSquare,
        /// <summary>The brightness pair: x = hue, y = value; the strip below is the saturation.</summary>
        Brightness,
        /// <summary>The hue wheel: angle = hue, radius = saturation; the strip below is the value.</summary>
        Wheel,
    }

    /// <summary>The colour scheme the numeric column of <see cref="SpriteColorPicker"/> reads and edits.</summary>
    public enum ColorScheme
    {
        /// <summary>Red / green / blue, 0..255.</summary>
        Rgb,
        /// <summary>Hue / saturation / brightness (HSV), 0..359 / 0..100 / 0..100.</summary>
        Hsb,
        /// <summary>Hue / lightness / saturation (HSL), 0..359 / 0..100 / 0..100.</summary>
        Hsl,
        /// <summary>The NTSC YIQ basis, scaled for entry: Y 0..100, I and Q -100..100.</summary>
        Yiq,
        /// <summary>CIE L*a*b* (D65): L 0..100, a and b -128..127.</summary>
        Lab,
        /// <summary>Cyan / magenta / yellow / key, each 0..100 (alpha through the alpha bar).</summary>
        Cmyk,
    }

    /// <summary>
    /// A modal colour dialog around <see cref="SpriteColorPicker"/> - the stand-in for <c>ColorDialog</c> that keeps the
    /// application's own SR2D look (dark palette, SR2D-drawn everything). Show it with <c>ShowDialog(owner)</c>; read
    /// <see cref="Value"/> on <c>DialogResult.OK</c>:
    /// <code>
    /// using var dlg = new SpriteColorDialog { Value = current };
    /// if (dlg.ShowDialog(this) == DialogResult.OK) current = dlg.Value;
    /// </code>
    /// Enter = OK, Esc = Cancel. With CloseOnButton = false the dialog can also be embedded in a form
    /// (TopLevel = false): the buttons stay, set DialogResult and raise ButtonClick.
    /// </summary>
    public sealed class SpriteColorDialog : Form
    {
#pragma warning disable CA2213 // added to Controls; WinForms disposes child controls with the parent (fields disposed again in Dispose for the analyzer)
        readonly SpriteColorPicker _picker = new();
        readonly SpriteButton _ok, _cancel;
#pragma warning restore CA2213

        /// <summary>OK / Cancel close the dialog (the modal use). false keeps it open - they only set
        /// <see cref="Form.DialogResult"/> and raise <see cref="ButtonClick"/>; for a dialog embedded in a form
        /// (TopLevel = false) that stays while its colour is applied.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool CloseOnButton { get; set; } = true;

        /// <summary>Raised after a button click, before anything closes (read <see cref="Form.DialogResult"/>).
        /// Relevant with <see cref="CloseOnButton"/> = false; the modal flow closes regardless.</summary>
        public event EventHandler? ButtonClick;

        void ButtonDone()
        {
            ButtonClick?.Invoke(this, EventArgs.Empty);
            if (CloseOnButton) Close();
        }

        /// <summary>The colour the dialog shows; after <c>DialogResult.OK</c> it is the selected colour.</summary>
        [Category("Appearance"), Description("The colour the dialog shows / returns.")]
        public Color Value { get => _picker.Value; set => _picker.Value = value; }
        private bool ShouldSerializeValue() => Value.ToArgb() != SpriteControlBase.DefaultAccent.ToArgb();
        private void ResetValue() => Value = Color.FromArgb(SpriteControlBase.DefaultAccent.ToArgb());

        public SpriteColorDialog()
        {
            Text = "Color";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = SpriteControlBase.DefaultBack;          // the dialog IS an SR2D surface: same dark face as the controls
            ForeColor = Color.FromArgb(0xE8, 0xEC, 0xF0);
            ClientSize = new Size(276, 316);
            KeyPreview = true;
            _picker.SetBounds(8, 8, 260, 264);
            _ok = new SpriteButton { Text = "OK", Size = new Size(76, 28) };
            _cancel = new SpriteButton { Text = "Cancel", Size = new Size(76, 28) };
            _ok.Location = new Point(ClientSize.Width - 76 * 2 - 8 - 8, ClientSize.Height - 36);
            _cancel.Location = new Point(ClientSize.Width - 76 - 8, ClientSize.Height - 36);
            // SpriteButton is no IButtonControl, so AcceptButton / CancelButton are out; setting the form's
            // DialogResult closes a modal form by itself, and KeyDown below wires Enter and Esc.
            // CloseOnButton = false keeps the dialog open (embedding it in a form): the buttons then only set
            // DialogResult and raise ButtonClick
            _ok.Click += (_, _) => { DialogResult = DialogResult.OK; ButtonDone(); };
            _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; ButtonDone(); };
            Controls.AddRange(new Control[] { _picker, _ok, _cancel });
            KeyDown += (_, e) =>
            {
                if (_picker.Picking)
                {   // KeyPreview gives the FORM the key first: while the pipette is armed Esc must lay it down, not cancel the dialog
                    if (e.KeyCode == Keys.Escape) { _picker.CancelPick(); e.Handled = true; }
                    return;
                }
                if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; ButtonDone(); }
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; ButtonDone(); }
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // every IDisposable field this form declares is released here (the analyzers check exactly this path,
                // CA2213); the controls are in the Controls tree already, double-dispose is safe
                _picker.Dispose();
                _ok.Dispose();
                _cancel.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
