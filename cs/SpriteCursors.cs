using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------------------------------------------------------
    // Cursors WinForms does not ship: an OPEN hand ("you can grab this"), a GRABBING hand (while the button is down and the
    // thing moves with the mouse), a rotate arrow, magnifiers. The hands are a traced vector drawing (the path data is in this
    // file - no resources, no files), the rest is drawn procedurally; all of it is rendered by SR2D (anti-aliased paths into a
    // 32-bit Sprite, black outline + white fill like the system cursors, so they read on any background) and turned into real
    // HCURSORs with a hot spot - one allocation per cursor, cached for the life of the process.
    //
    // When to use which (the convention every SR2D control follows): the hands mean "this MOVES with the mouse" - the open
    // hand over something that can be dragged / panned / turned, the closed hand while it is being dragged. Things that are
    // merely clicked (buttons, toggles, radios, check boxes, list rows, tabs) keep the arrow, like native controls do.
    //
    //     surf.Cursor = SpriteCursors.HandOpen;            // hovering something draggable
    //     surf.Cursor = SpriteCursors.HandGrab;            // while dragging
    //     surf.Cursor = SpriteCursors.Rotate;              // "drag here to turn"
    //     SpriteCursors.ZoomIn / ZoomOut                   // the Photoshop-like magnifier
    //
    // Size follows the DPI (32 px at 100 %, 48 at 150 %, 64 at 200 % - a 4K display at 200 % gets a hand as big as its arrow)
    // and the Windows "cursor size" accessibility slider; Get(kind, control) sizes for the monitor a control is on, Size = n
    // forces one size. SpriteCursors.Draw(kind, sprite, scale) is the raw renderer (also used by the headless test to check
    // the pictures); SpriteCursors.Make(...) turns any Sprite into a Cursor if you draw your own.
    // ------------------------------------------------------------------------------------------------------------------------

    /// <summary>Procedurally drawn cursors (open hand, grabbing hand, rotate, magnifiers) as real <see cref="Cursor"/> objects.</summary>
    internal static class SpriteCursors
    {
        public enum Kind { HandOpen, HandGrab, Rotate, ZoomIn, ZoomOut }

        static readonly Dictionary<(Kind, int), Cursor> cache = new Dictionary<(Kind, int), Cursor>();
        static readonly object gate = new object();

        /// <summary>Open hand: hovering something that can be dragged (at the current <see cref="Size"/>).</summary>
        public static Cursor HandOpen => Get(Kind.HandOpen);
        /// <summary>Closed hand: while dragging.</summary>
        public static Cursor HandGrab => Get(Kind.HandGrab);
        /// <summary>Circular arrow: drag turns the object.</summary>
        public static Cursor Rotate => Get(Kind.Rotate);
        public static Cursor ZoomIn => Get(Kind.ZoomIn);
        public static Cursor ZoomOut => Get(Kind.ZoomOut);

        /// <summary>The cursor for a kind at the current <see cref="Size"/> (created on first use; a system cursor when the OS refuses).</summary>
        public static Cursor Get(Kind kind) => Get(kind, Size);
        /// <summary>The cursor for a kind at an explicit pixel size (16..256; one HCURSOR per size is cached).</summary>
        public static Cursor Get(Kind kind, int size)
        {
            size = Math.Clamp(size, 16, 256);
            lock (gate)
            {
                if (cache.TryGetValue((kind, size), out var c)) return c;
                c = Build(kind, size) ?? Fallback(kind);
                cache[(kind, size)] = c; return c;
            }
        }
        /// <summary>The cursor for a kind sized for the monitor <paramref name="control"/> is on (per-monitor DPI).</summary>
        public static Cursor Get(Kind kind, Control control) => Get(kind, SizeFor(control));
        static Cursor Fallback(Kind k) => k switch { Kind.HandOpen => Cursors.Hand, Kind.HandGrab => Cursors.SizeAll, Kind.ZoomIn or Kind.ZoomOut => Cursors.Cross, _ => Cursors.Cross };

        // ---------------------------------------------------------------- size
        // Windows keeps SM_CXCURSOR at 32 whatever the DPI (the shell scales the arrow itself), so the size is computed from
        // the DPI: 32 px at 96 dpi, 48 at 150 %, 64 at 200 % (4K at 200 % gets a 64 px hand next to a 64 px arrow), times the
        // "cursor size" slider of the accessibility settings (1..15, registry). Override with Size for a fixed size.
        static int _forced;
        /// <summary>Cursor pixel size used by the properties: from the primary monitor DPI and the Windows cursor-size setting; set to force one (0 = automatic).</summary>
        public static int Size
        {
            get => _forced > 0 ? _forced : SizeForDpi(PrimaryDpi);
            set { _forced = Math.Max(0, value); }
        }
        /// <summary>Kept for compatibility: the automatic size (see <see cref="Size"/>).</summary>
        public static int SystemSize => SizeForDpi(PrimaryDpi);
        /// <summary>Cursor size for the monitor a control is on.</summary>
        public static int SizeFor(Control? control)
        {
            if (_forced > 0) return _forced;
            int dpi = 96;
            try { if (control != null && control.IsHandleCreated) dpi = (int)control.DeviceDpi; else dpi = PrimaryDpi; } catch { dpi = PrimaryDpi; }
            return SizeForDpi(dpi);
        }
        /// <summary>32 px at 96 dpi scaled by the DPI and by the accessibility cursor-size setting, rounded to a multiple of 8 (the sizes cursor themes ship).</summary>
        public static int SizeForDpi(int dpi)
        {
            float k = Math.Max(1f, dpi / 96f) * AccessibilityScale;
            int px = (int)MathF.Round(32 * k / 8f) * 8;
            return Math.Clamp(px, 32, 256);
        }
        static float AccessibilityScale
        {
            get
            {   // HKCU\Software\Microsoft\Accessibility\CursorSize: 1 = normal .. 15 = huge (the slider in Settings > Mouse pointer)
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
                    if (key?.GetValue("CursorSize") is int n && n > 1) return 1f + (n - 1) * 0.5f;    // 2 = 1.5x, 3 = 2x ... (matches the shell's steps)
                }
                catch { }
                return 1f;
            }
        }
        static int PrimaryDpi
        {
            get
            {
                try
                {
                    IntPtr dc = GetDC(IntPtr.Zero);
                    if (dc == IntPtr.Zero) return 96;
                    try { int d = GetDeviceCaps(dc, 88 /*LOGPIXELSX*/); return d > 0 ? d : 96; } finally { _ = ReleaseDC(IntPtr.Zero, dc); }
                }
                catch { return 96; }
            }
        }

        static Cursor? Build(Kind kind, int size)
        {
            try
            {
                float sc = size / 32f;
                using var s = new Sprite(size, size, SR2D.Op.AlphaOver);
                s.ClearBuffer(0);
                var hot = Draw(kind, s, sc);
                return Make(s, hot.X, hot.Y);
            }
            catch { return null; }
        }

        /// <summary>
        /// Draws a cursor picture into <paramref name="s"/> (premultiplied ARGB, any size; <paramref name="scale"/> 1 = the 32 px
        /// design) and returns the hot spot. Public so custom pictures can be composed from the same parts and for tests.
        /// </summary>
        public static Point Draw(Kind kind, Sprite s, float scale = 1f)
        {
            const int ink = unchecked((int)0xFF000000), paper = unchecked((int)0xFFFFFFFF);
            var op = SR2D.LineOp.AlphaOver;
            switch (kind)
            {
                case Kind.HandOpen:
                case Kind.HandGrab:
                {
                    // the hands come from a vector drawing (uploads/cursor/hand_cursor.svg, traced into the *Outline / *Fill
                    // constants below): a black outline shape with the white hand on top, like the system cursors. The
                    // design space is 32 px; anything else is a uniform scale.
                    bool open = kind == Kind.HandOpen;
                    var outline = ParsePath(open ? OpenOutline : GrabOutline, scale);
                    var fill = ParsePath(open ? OpenFill : GrabFill, scale);
                    s.FillPath(outline, ink, op, true);
                    s.FillPath(fill, paper, op, true);
                    // hot spot: the open hand points with the middle of the palm, the fist grabs at its centre
                    return open ? new Point((int)MathF.Round(16 * scale), (int)MathF.Round(17 * scale)) : new Point((int)MathF.Round(16 * scale), (int)MathF.Round(16 * scale));
                }
                case Kind.Rotate:
                {
                    // 3/4 circle arc with an arrow head at the top right, white body with black outline
                    var arc = new Sprite.PathBuilder(); float cx = 15f * scale, cy = 16f * scale, r = 8.5f * scale;
                    arc.ArcAround(cx, cy, r, 120f, 250f);
                    s.DrawPath(arc, ink, 5.5f * scale, true, op, true);
                    s.DrawPath(arc, paper, 3f * scale, true, op, true);
                    // arrow head at the arc end (angle 120 + 270 = 30 deg, pointing along the tangent, clockwise)
                    float ea = 10f * MathF.PI / 180f; float ex = cx + MathF.Cos(ea) * r, ey = cy + MathF.Sin(ea) * r;
                    float tx = -MathF.Sin(ea), ty = MathF.Cos(ea);                 // tangent (clockwise direction on screen)
                    float nx = MathF.Cos(ea), ny = MathF.Sin(ea);
                    float L = 6.5f * scale, Wd = 4.5f * scale;
                    var head = new Sprite.PathBuilder();
                    head.MoveTo(ex + tx * (L + 1.5f * scale), ey + ty * (L + 1.5f * scale)).LineTo(ex + nx * Wd + tx * 1.5f * scale, ey + ny * Wd + ty * 1.5f * scale).LineTo(ex - nx * Wd + tx * 1.5f * scale, ey - ny * Wd + ty * 1.5f * scale).Close();
                    s.DrawPath(head, ink, 2.4f * scale, true, op, true);
                    s.FillPath(head, paper, op, true);
                    return new Point((int)(15 * scale), (int)(16 * scale));
                }
                case Kind.ZoomIn:
                case Kind.ZoomOut:
                {
                    float lx = 11.5f * scale, ly = 11.5f * scale, lr = 7.5f * scale;
                    var handle = new Sprite.PathBuilder(); handle.MoveTo(lx + lr * 0.72f, ly + lr * 0.72f).LineTo(27f * scale, 27f * scale);
                    s.DrawPath(handle, ink, 5f * scale, true, op, true);
                    s.DrawPath(handle, paper, 2.4f * scale, true, op, true);
                    var lens = new Sprite.PathBuilder(); lens.Circle(lx, ly, lr);
                    s.DrawPath(lens, ink, 3.6f * scale, true, op, false);
                    s.FillPath(lens, unchecked((int)0x80FFFFFF), op, true);        // glassy fill (premultiplied grey = 50 % white)
                    s.DrawPath(lens, paper, 1.4f * scale, true, op, false);
                    var sign = new Sprite.PathBuilder();
                    sign.MoveTo(lx - lr * 0.55f, ly).LineTo(lx + lr * 0.55f, ly);
                    if (kind == Kind.ZoomIn) sign.MoveTo(lx, ly - lr * 0.55f).LineTo(lx, ly + lr * 0.55f);
                    s.DrawPath(sign, ink, 1.8f * scale, true, op, false);
                    return new Point((int)(lx), (int)(ly));
                }
            }
            return new Point(s.Width / 2, s.Height / 2);
        }

        // ---------------------------------------------------------------- the hand pictures
        // SVG path data (M / L / C / Z only, absolute) in the 32 px design space, x right, y down. Extracted from
        // uploads/cursor/hand_cursor.svg (CorelDRAW): each hand is the black outline shape + the white fill shape.
        const string OpenOutline =
            "M25.40 18.41C25.35 18.65 25.33 18.90 25.32 19.12L25.32 19.17L25.19 20.94L25.19 20.94C25.06 23.13 24.38 24.24 23.73 25.28C23.46 25.72 23.19 26.15 23.03" +
            " 26.65C22.96 26.88 22.94 27.18 22.92 27.48C22.87 28.35 22.82 29.20 21.92 30.04C20.88 31.00 18.52 30.98 17.11 30.97C17.00 30.97 17.33 30.97 16.72 30.97" +
            "C16.14 30.97 15.37 30.93 14.70 30.81C14.09 30.71 13.53 30.53 13.16 30.24C12.13 29.46 12.09 28.61 12.06 27.76C12.05 27.51 12.04 27.27 11.99 27.14C11.80" +
            " 26.66 11.03 25.97 10.42 25.41C10.39 25.39 10.36 25.37 10.04 25.07C10.00 25.03 9.80 24.86 9.47 24.56C8.89 24.05 8.15 23.39 7.94 23.14C7.75 22.92 7.60 " +
            "22.68 7.47 22.43C7.34 22.20 7.25 21.99 7.15 21.76C6.91 21.19 6.56 20.53 6.15 19.91C5.79 19.36 5.40 18.84 5.02 18.46L4.98 18.42L4.60 18.00L4.60 18.00L4" +
            ".44 17.84C4.36 17.75 4.28 17.68 4.18 17.58C4.08 17.48 3.99 17.38 3.91 17.27C3.87 17.23 3.83 17.18 3.77 17.12C3.74 17.10 3.71 17.07 3.68 17.04C3.24 16." +
            "61 2.72 16.10 2.72 15.20C2.72 14.51 3.04 14.00 3.52 13.65C3.97 13.32 4.57 13.18 5.10 13.18C5.51 13.18 5.93 13.28 6.32 13.42C6.78 13.60 7.20 13.85 7.48" +
            " 14.05C7.80 14.29 8.10 14.55 8.38 14.82C8.63 15.06 8.89 15.34 9.16 15.62C9.53 16.02 9.43 15.91 9.46 15.94C9.82 16.34 10.18 16.74 10.55 17.05C10.56 17." +
            "07 10.55 17.06 10.55 17.05L10.55 17.05C10.55 17.06 10.55 17.06 10.55 17.06C10.59 17.09 10.65 17.10 10.72 17.09C10.79 17.09 10.85 17.06 10.88 17.02C10." +
            "90 17.01 10.89 17.01 10.88 17.02L10.89 17.02C10.89 17.02 10.89 17.02 10.89 17.02C11.14 16.71 11.04 16.26 10.92 15.72C10.89 15.59 10.86 15.44 10.84 15." +
            "35C10.52 13.75 10.14 12.13 9.76 10.54C9.66 10.13 9.91 11.15 9.60 9.84L8.94 7.04L8.94 7.02C8.90 6.85 8.86 6.69 8.81 6.48L8.79 6.38C8.76 6.29 8.76 6.28 " +
            "8.76 6.27C8.69 6.01 8.59 5.65 8.59 5.39C8.59 4.59 8.91 3.95 9.38 3.52C9.68 3.25 10.04 3.06 10.42 2.97C10.81 2.89 11.21 2.90 11.59 3.03C12.16 3.21 12.6" +
            "6 3.62 12.95 4.29C13.04 4.49 13.21 5.05 13.41 5.70C13.70 6.66 14.09 8.04 14.18 8.38C14.21 8.47 14.24 8.56 14.27 8.66C14.30 8.76 14.34 8.87 14.38 9.02C" +
            "14.38 9.04 14.41 9.13 14.47 9.32C14.51 9.46 14.54 9.57 14.56 9.62C14.56 9.62 14.56 9.62 14.56 9.63C14.56 9.19 14.56 8.77 14.56 8.34C14.57 7.88 14.57 7" +
            ".42 14.59 6.97C14.61 6.62 14.60 6.28 14.60 5.94C14.59 5.46 14.58 4.98 14.61 4.50C14.63 4.27 14.64 4.12 14.64 3.97C14.65 3.70 14.66 3.43 14.68 3.25C14." +
            "71 2.88 14.78 2.59 14.93 2.30C15.08 2.00 15.28 1.78 15.56 1.54L15.61 1.50C15.66 1.47 15.72 1.43 15.78 1.39C16.33 1.06 16.94 1.00 17.49 1.14C18.01 1.27" +
            " 18.50 1.59 18.82 2.02C18.86 2.07 18.90 2.14 18.94 2.20L18.99 2.29C19.32 2.95 19.30 3.75 19.28 4.43C19.27 4.49 19.27 4.55 19.27 4.79L19.27 4.79C19.27 " +
            "5.06 19.27 5.33 19.27 5.60C19.26 6.61 19.26 7.63 19.30 8.66L19.33 8.55C19.79 6.49 20.23 4.54 20.41 4.23C20.48 4.10 20.59 3.95 20.71 3.81C20.83 3.67 20" +
            ".96 3.54 21.09 3.45C21.19 3.37 21.30 3.30 21.42 3.24C21.91 3.00 22.47 2.97 22.98 3.11C23.47 3.24 23.92 3.54 24.22 3.94C24.30 4.04 24.37 4.15 24.43 4.2" +
            "8C24.83 5.08 24.66 5.84 24.50 6.52C24.49 6.57 24.48 6.63 24.45 6.78C24.43 6.87 24.21 7.94 23.98 9.03C23.80 9.95 23.61 10.87 23.42 11.80C24.08 10.68 24" +
            ".73 9.56 24.90 9.25C25.13 8.83 25.31 8.50 25.67 8.20C26.07 7.87 26.54 7.70 27.23 7.70C27.63 7.70 27.98 7.81 28.28 7.99C28.64 8.21 28.89 8.52 29.06 8.8" +
            "8C29.21 9.20 29.28 9.55 29.28 9.90C29.28 10.34 29.16 10.80 28.92 11.19C28.90 11.23 28.85 11.33 28.80 11.44C28.76 11.52 28.72 11.60 28.63 11.76C28.50 1" +
            "1.99 28.36 12.28 28.23 12.56C28.20 12.62 28.17 12.68 28.02 12.98C27.80 13.42 27.59 13.83 27.38 14.25C27.17 14.65 26.96 15.06 26.75 15.47C26.61 15.76 2" +
            "6.46 16.03 26.32 16.30C26.05 16.83 25.77 17.35 25.53 17.91C25.48 18.03 25.44 18.21 25.40 18.41Z";
        const string OpenFill =
            "M24.23 19.09C24.24 18.56 24.36 17.88 24.52 17.49C24.89 16.62 25.36 15.82 25.78 14.98C26.19 14.14 26.61 13.35 27.04 12.49C27.26 12.06 27.45 11.63 27.68" +
            " 11.23C27.79 11.03 27.87 10.82 27.99 10.61C28.41 9.94 28.15 8.79 27.23 8.79C26.41 8.79 26.21 9.13 25.86 9.77C25.51 10.42 23.22 14.35 22.82 14.90C22.52" +
            " 15.31 21.80 15.09 21.80 14.54C21.80 14.43 21.88 14.10 21.91 13.94C22.32 11.59 22.89 8.95 23.37 6.57C23.48 6.00 23.72 5.31 23.45 4.76C23.16 4.18 22.30" +
            " 3.90 21.75 4.32C21.61 4.43 21.44 4.63 21.36 4.77C21.15 5.13 19.50 13.05 19.25 13.50C19.05 13.86 18.41 13.88 18.25 13.44C18.15 13.17 18.22 9.82 18.22 " +
            "9.28C18.22 9.09 18.21 8.90 18.21 8.70C18.16 7.40 18.17 6.09 18.18 4.79C18.18 4.19 18.27 3.30 18.01 2.77C17.67 2.22 16.84 1.94 16.27 2.38C15.92 2.67 15" +
            ".81 2.88 15.77 3.34C15.74 3.74 15.73 4.16 15.71 4.57C15.65 5.41 15.72 6.18 15.68 7.01C15.65 7.92 15.65 8.78 15.66 9.69C15.66 10.12 15.64 10.45 15.61 1" +
            "0.87C15.54 12.23 15.91 13.61 15.04 13.61C14.54 13.61 14.44 13.00 14.31 12.57C14.18 12.12 14.05 11.73 13.92 11.28C13.87 11.07 13.78 10.82 13.72 10.60C1" +
            "3.65 10.39 13.59 10.19 13.52 9.96C13.45 9.74 13.38 9.52 13.32 9.31C13.25 9.05 13.20 8.93 13.13 8.67C12.99 8.18 12.13 5.14 11.95 4.73C11.41 3.52 9.69 3" +
            ".91 9.69 5.39C9.69 5.55 9.81 5.94 9.85 6.12C9.91 6.37 9.96 6.54 10.01 6.79L10.66 9.59C11.10 11.42 11.55 13.29 11.92 15.14C12.11 16.11 12.40 16.87 11.7" +
            "4 17.70C11.30 18.26 10.40 18.37 9.84 17.88C9.31 17.43 8.83 16.87 8.36 16.36C7.89 15.85 7.38 15.34 6.83 14.93C6.46 14.66 5.72 14.27 5.10 14.27C4.51 14." +
            "27 3.81 14.55 3.81 15.20C3.81 15.68 4.23 16.04 4.53 16.34C4.70 16.51 4.78 16.64 4.95 16.80C5.10 16.95 5.22 17.07 5.37 17.22L5.79 17.69C6.71 18.61 7.65" +
            " 20.15 8.16 21.33C8.34 21.75 8.50 22.11 8.78 22.43C9.00 22.70 10.41 23.93 10.79 24.27C11.51 24.94 12.67 25.88 13.01 26.73C13.35 27.59 12.85 28.63 13.8" +
            "2 29.37C14.34 29.76 15.79 29.87 16.72 29.87C17.86 29.87 20.37 29.98 21.17 29.24C22.08 28.40 21.66 27.33 21.99 26.31C22.56 24.58 23.93 23.84 24.09 20.8" +
            "6L24.23 19.09Z";
        const string GrabOutline =
            "M9.73 17.80C10.28 17.97 11.04 18.20 11.26 17.70L11.26 17.70C11.43 17.26 11.54 16.50 11.55 15.74C11.57 15.03 11.51 14.35 11.36 13.95C11.19 13.51 10.99 " +
            "13.07 10.78 12.64C10.71 12.48 10.64 12.33 10.51 12.03C10.42 11.83 10.33 11.63 10.24 11.43C10.00 10.90 9.77 10.38 9.57 9.91C9.48 9.72 9.40 9.57 9.30 9." +
            "44C9.25 9.37 9.19 9.31 9.13 9.23L9.11 9.23C8.38 9.29 7.85 9.34 7.07 8.71C6.98 8.64 6.91 8.58 6.83 8.48C6.74 8.38 6.67 8.28 6.60 8.16C6.45 7.86 6.37 7." +
            "57 6.37 7.27C6.37 6.71 6.62 6.21 7.00 5.78C7.30 5.45 7.69 5.16 8.10 4.94C8.67 4.63 9.33 4.44 9.85 4.44C10.56 4.44 10.96 4.52 11.32 4.71C11.33 4.38 11." +
            "39 4.18 11.69 3.73C11.96 3.35 12.33 3.07 12.74 2.89C13.16 2.71 13.62 2.63 14.05 2.63C14.76 2.63 15.85 3.11 16.56 3.42C16.64 3.46 16.71 3.49 16.84 3.54" +
            "C17.77 3.94 18.13 4.62 18.37 5.33C18.48 5.17 18.61 5.01 18.77 4.88C19.55 4.21 20.40 4.15 21.22 4.37C21.88 4.55 22.46 4.91 22.96 5.24C23.55 5.64 23.88 " +
            "6.01 24.07 6.52C24.22 6.93 24.24 7.33 24.24 7.88C24.60 7.58 25.06 7.40 25.58 7.40C26.18 7.40 26.66 7.58 27.06 7.83C27.42 8.06 27.67 8.30 27.89 8.53L27" +
            ".89 8.53C28.34 8.98 28.71 9.37 28.93 9.91C29.19 10.55 29.18 11.22 28.76 12.06L28.71 12.14C28.34 12.88 27.19 15.14 27.14 15.49C27.09 15.83 27.08 16.40 " +
            "27.07 16.82L27.07 16.83L27.07 16.99C27.06 17.42 27.06 17.83 27.06 18.24C27.06 19.19 27.06 20.13 26.99 21.05C26.92 22.10 26.52 22.87 26.12 23.63C25.81 " +
            "24.22 25.50 24.81 25.48 25.47L25.48 25.51C25.46 26.19 25.43 26.89 25.16 27.63L25.14 27.67C24.91 28.21 24.50 28.55 24.01 28.77C23.64 28.93 23.24 29.01 " +
            "22.85 29.06C22.36 29.13 20.97 29.26 19.76 29.32C18.88 29.36 18.07 29.37 17.66 29.31C17.57 29.30 17.44 29.30 17.30 29.29C16.60 29.28 15.69 29.26 14.96 " +
            "28.71C14.43 28.31 14.26 27.90 14.09 27.48C14.01 27.28 13.93 27.07 13.63 26.94C13.28 26.79 12.96 26.68 12.64 26.57C11.72 26.26 10.86 25.96 9.85 25.08C9" +
            ".77 25.02 9.69 24.95 9.60 24.88C9.51 24.80 9.41 24.72 9.38 24.69C8.41 23.88 8.03 23.64 7.33 23.20C7.12 23.07 6.89 22.93 6.45 22.64C5.66 22.13 5.47 21." +
            "72 5.19 21.11L5.19 21.12C5.12 20.97 5.05 20.81 4.98 20.69C4.56 19.91 4.20 19.53 3.86 19.18C3.63 18.94 3.42 18.72 3.19 18.41C2.93 18.05 2.81 17.67 2.81" +
            " 17.30C2.81 16.96 2.91 16.63 3.08 16.34C3.23 16.09 3.44 15.87 3.67 15.70C4.03 15.43 4.49 15.26 4.94 15.26C5.43 15.26 5.80 15.29 6.22 15.41C6.65 15.53 " +
            "7.02 15.73 7.49 16.08C8.03 16.49 8.32 16.84 8.56 17.15C8.75 17.39 8.90 17.57 9.38 17.70C9.62 17.77 9.67 17.78 9.73 17.80Z";
        const string GrabFill =
            "M20.41 7.66C20.41 7.91 20.37 7.96 20.36 8.18L20.32 9.73C20.32 10.19 20.37 10.39 20.16 10.73C19.88 11.19 19.20 11.14 18.93 10.66C18.85 10.52 18.41 9.11" +
            " 18.33 8.85C18.09 8.07 17.85 7.31 17.60 6.53C17.35 5.80 17.23 4.91 16.40 4.55C15.83 4.30 14.62 3.72 14.05 3.72C13.51 3.72 12.89 3.92 12.60 4.35C12.40 " +
            "4.63 12.41 4.72 12.41 5.00C12.41 5.62 13.02 5.91 13.46 6.12C14.65 6.72 14.54 6.46 15.11 6.59C15.22 6.82 15.86 10.05 15.86 10.35C15.86 10.73 15.52 11.1" +
            "0 15.15 11.10C14.48 11.10 14.25 10.27 13.98 9.62C13.85 9.31 13.69 9.00 13.57 8.71C13.04 7.42 12.87 7.21 11.85 6.45C10.96 5.78 10.85 5.53 9.85 5.53C8.9" +
            "0 5.53 7.02 6.63 7.57 7.64C7.62 7.74 7.66 7.78 7.75 7.85C8.22 8.23 8.59 8.18 9.11 8.13C9.35 8.12 9.47 8.04 9.64 8.20C10.08 8.62 10.33 8.92 10.57 9.48C" +
            "10.88 10.18 11.19 10.88 11.51 11.58C11.80 12.23 12.13 12.90 12.38 13.56C12.79 14.64 12.70 17.01 12.29 18.09C11.64 19.72 10.05 19.02 9.09 18.76C7.74 18" +
            ".38 7.95 17.80 6.83 16.95C6.12 16.42 5.74 16.36 4.94 16.36C4.30 16.36 3.58 17.10 4.07 17.76C4.61 18.49 5.20 18.78 5.95 20.17C6.34 20.90 6.35 21.27 7.0" +
            "5 21.72C8.45 22.63 8.72 22.71 10.08 23.86C10.25 24.00 10.40 24.12 10.57 24.26C11.76 25.30 12.80 25.39 14.06 25.94C15.27 26.46 14.92 27.31 15.61 27.83C" +
            "16.23 28.30 17.34 28.15 17.82 28.23C18.68 28.36 21.92 28.09 22.71 27.98C23.31 27.90 23.93 27.74 24.14 27.24C24.35 26.67 24.37 26.04 24.39 25.43C24.44 " +
            "23.68 25.78 22.68 25.90 20.97C26.00 19.62 25.95 18.33 25.98 16.98C25.98 16.51 25.99 15.78 26.05 15.33C26.13 14.77 27.44 12.22 27.78 11.56C28.30 10.54 " +
            "27.83 10.02 27.12 9.30C26.74 8.93 26.31 8.49 25.58 8.49C24.71 8.49 24.21 9.57 24.82 10.19C24.87 10.24 25.00 10.33 25.08 10.36C25.42 10.49 25.47 10.25 " +
            "24.71 11.78C24.55 12.11 24.30 12.74 23.86 12.74C23.38 12.74 23.14 12.48 23.15 11.99L23.15 8.01C23.15 7.03 23.12 6.66 22.35 6.15C21.50 5.59 20.39 4.94 " +
            "19.48 5.71C18.80 6.30 19.08 7.54 20.41 7.66Z";

        /// <summary>Parses M / L / C / Z path data (absolute coordinates) into a PathBuilder, scaled by <paramref name="k"/>.</summary>
        public static Sprite.PathBuilder ParsePath(string d, float k)
        {
            var p = new Sprite.PathBuilder();
            int i = 0, n = d.Length;
            float Num()
            {
                while (i < n && (d[i] == ' ' || d[i] == ',')) i++;
                int st = i; if (i < n && (d[i] == '-' || d[i] == '+')) i++;
                while (i < n && (char.IsDigit(d[i]) || d[i] == '.')) i++;
                return float.Parse(d.AsSpan(st, i - st), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) * k;
            }
            char cmd = 'M';
            while (i < n)
            {
                char c = d[i];
                if (c == ' ' || c == ',') { i++; continue; }
                if (char.IsLetter(c)) { cmd = c; i++; if (cmd == 'Z') { p.Close(); continue; } }
                switch (cmd)
                {
                    case 'M': { float x = Num(), y = Num(); p.MoveTo(x, y); cmd = 'L'; break; }
                    case 'L': { float x = Num(), y = Num(); p.LineTo(x, y); break; }
                    case 'C': { float a = Num(), b = Num(), c1 = Num(), d1 = Num(), x = Num(), y = Num(); p.CurveTo(a, b, c1, d1, x, y); break; }
                    default: throw new FormatException("path command " + cmd);
                }
            }
            return p;
        }

        // ---------------------------------------------------------------- Sprite -> HCURSOR
        /// <summary>Turns a premultiplied ARGB sprite into a cursor with the given hot spot (the sprite may be disposed afterwards).</summary>
        public static Cursor? Make(Sprite s, int hotX, int hotY)
        {
            int w = s.Width, h = s.Height;
            // colour DIB (top-down 32 bpp, straight alpha - Windows wants unpremultiplied for alpha cursors)
            var bmi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
            IntPtr bits;
            IntPtr hColor = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
            if (hColor == IntPtr.Zero) return null;
            IntPtr hMask = IntPtr.Zero, hCur = IntPtr.Zero;
            try
            {
                var px = s.Pixels;
                unsafe
                {
                    int* dst = (int*)bits;
                    for (int i = 0; i < w * h; i++)
                    {
                        int v = px[i]; int a = (v >> 24) & 255;
                        if (a == 0) { dst[i] = 0; continue; }
                        if (a == 255) { dst[i] = v; continue; }
                        int r = Math.Min(255, ((v >> 16) & 255) * 255 / a), g = Math.Min(255, ((v >> 8) & 255) * 255 / a), b = Math.Min(255, (v & 255) * 255 / a);
                        dst[i] = (a << 24) | (r << 16) | (g << 8) | b;
                    }
                }
                // AND mask: 1 bpp, all zero (the alpha channel decides); rows padded to 16 bits
                int stride = ((w + 15) / 16) * 2;
                var maskBits = new byte[stride * h];
                hMask = CreateBitmap(w, h, 1, 1, maskBits);
                if (hMask == IntPtr.Zero) return null;
                var ii = new ICONINFO { fIcon = false, xHotspot = hotX, yHotspot = hotY, hbmMask = hMask, hbmColor = hColor };
                hCur = CreateIconIndirect(ref ii);
                return hCur == IntPtr.Zero ? null : new Cursor(hCur);
            }
            finally
            {
                if (hMask != IntPtr.Zero) DeleteObject(hMask);
                DeleteObject(hColor);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; public int colors; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("gdi32")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[] bits);
        [DllImport("gdi32")] static extern bool DeleteObject(IntPtr h);
        [DllImport("user32", SetLastError = true)] static extern IntPtr CreateIconIndirect(ref ICONINFO icon);
        [DllImport("user32")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32")] static extern int GetDeviceCaps(IntPtr dc, int index);
    }
}
