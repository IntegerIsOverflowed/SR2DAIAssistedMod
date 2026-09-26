using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>
    /// A WinForms control whose whole client area is an SR2D <see cref="Sprite"/>.
    ///
    /// Why a dedicated control instead of a Paint handler on a Panel:
    ///  * WinForms erases the background (WM_ERASEBKGND) and, with DoubleBuffered,
    ///    allocates its own back buffer and copies through it. Both are wasted work
    ///    and the first one causes flicker. This control opts out of both - the Sprite
    ///    IS the back buffer.
    ///  * OnPaint only blits e.ClipRectangle, so partial invalidations (dirty rects,
    ///    a tooltip passing over) cost only that rectangle.
    ///  * The surface is a GDI DIB section (BitBlt present) and is recreated on
    ///    resize automatically.
    ///
    /// Usage - render on demand:
    ///     box.Render += (s, e) => { e.Surface.ClearBuffer(0); e.Surface.Draw(...); };   // or the designer's Events tab
    ///     box.Redraw();                          // whenever the scene changed (mouse move, model update ...)
    ///
    /// NOTE: plain Invalidate() only re-blits the cached surface (that is what keeps
    /// expose/overlap repaints cheap) - it does NOT rerun Render. Redraw() marks the
    /// scene dirty first, so Render runs on the next paint. Many Redraw() calls before
    /// the next WM_PAINT coalesce into ONE Render.
    ///
    /// Usage - game loop: draw into box.Surface yourself, then call box.Present()
    /// (immediate blit, no message round-trip, Render not involved).
    /// </summary>
    /// <summary>Argument of <see cref="SpriteBox.Render"/>: the surface to draw into (already the right size).</summary>
    internal sealed class RenderEventArgs : EventArgs
    {
        public RenderEventArgs(Sprite surface) { Surface = surface; }
        /// <summary>The back buffer of the control: draw the whole scene into it.</summary>
        public Sprite Surface { get; }
        /// <summary>Width / height of the surface (shorthand for Surface.Width / .Height).</summary>
        public int Width => Surface.Width;
        public int Height => Surface.Height;
    }

    [DefaultEvent(nameof(Render))]
    internal partial class SpriteBox : Control
    {
        private Sprite? _surface;
        private bool _dirty = true;

        private Size _fixedSize = Size.Empty;
        private bool _gdi = true;
        private bool _smooth;

        /// <summary>
        /// Raised from OnPaint when the scene must be (re)drawn into the surface. Listed in the designer's Events tab
        /// (like Paint); double-click it there to get <c>void canvas_Render(object sender, RenderEventArgs e)</c> and
        /// draw into <c>e.Surface</c>. Subscribing in code works the same: <c>box.Render += (s, e) => { e.Surface.ClearBuffer(0); ... };</c>
        /// </summary>
        [Category("Appearance"), Description("Draw the scene into e.Surface (the back buffer). Runs on the next paint after Redraw(); many Redraw() calls coalesce into one Render.")]
        public event EventHandler<RenderEventArgs>? Render;

        /// <summary>The back buffer: the client area (SizeMode None), <see cref="FixedSurfaceSize"/>, or <see cref="ImageSize"/> when a <see cref="SizeMode"/> is on. Never null after the handle exists.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Sprite Surface { get { EnsureSurface(); return _surface!; } }

        /// <summary>Use a GDI DIB section (BitBlt present). Default true.</summary>
        [Category("Behavior"), DefaultValue(true), Description("Back the surface with a GDI DIB section (BitBlt present).")]
        public bool GdiSurface
        {
            get => _gdi;
            set { if (_gdi == value) return; _gdi = value; _surface?.Dispose(); _surface = null; _dirty = true; Invalidate(); }
        }

        /// <summary>
        /// If set, the surface keeps this fixed size and is stretched to the client area
        /// on present (pixel-art zoom, or render at low res and upscale). Size.Empty =
        /// surface follows the client size 1:1.
        /// </summary>
        [Category("Behavior"), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible),
         Description("Fixed surface size stretched to the client area; 0,0 = follow the client size 1:1.")]
        public Size FixedSurfaceSize
        {
            get => _fixedSize;
            set { if (_fixedSize == value) return; _fixedSize = value; _surface?.Dispose(); _surface = null; _dirty = true; Invalidate(); }
        }
        // designer serialisation contract for a non-simple property type
        private bool ShouldSerializeFixedSurfaceSize() => !_fixedSize.IsEmpty;
        private void ResetFixedSurfaceSize() => FixedSurfaceSize = Size.Empty;

        /// <summary>Smooth (HALFTONE) stretching when FixedSurfaceSize is used.</summary>
        [Category("Behavior"), DefaultValue(false), Description("HALFTONE stretching when FixedSurfaceSize is set.")]
        public bool SmoothStretch
        {
            get => _smooth;
            set { if (_smooth == value) return; _smooth = value; Invalidate(); }
        }

        /// <summary>Not used: the surface is opaque. Hidden from the designer.</summary>
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public override Image? BackgroundImage { get => base.BackgroundImage; set => base.BackgroundImage = value; }
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public override ImageLayout BackgroundImageLayout { get => base.BackgroundImageLayout; set => base.BackgroundImageLayout = value; }

        public SpriteBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint     // no separate WM_ERASEBKGND
                   | ControlStyles.UserPaint
                   | ControlStyles.Opaque                   // we cover every pixel -> no background fill
                   | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.DoubleBuffer, false); // Sprite is the buffer
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { /* intentionally empty */ }

        /// <summary>
        /// Draws the scene into the surface. The base implementation raises <see cref="Render"/>; controls derived
        /// from SpriteBox (<see cref="SpriteKnob"/>, <see cref="SpriteSlider"/>) override it instead of subscribing
        /// to their own event. Override <see cref="HasRenderer"/> too when you override this.
        /// </summary>
        protected virtual void OnRender(Sprite surface) => Render?.Invoke(this, new RenderEventArgs(surface));
        /// <summary>True when something will draw the scene (the Render event has subscribers, or a subclass overrides OnRender).</summary>
        protected virtual bool HasRenderer => Render != null;

        /// <summary>
        /// False when SR2D64.dll cannot be loaded (typically inside the Visual Studio form designer, whose host process
        /// does not run from the project's output folder, or when the DLL is missing/of the wrong bitness). The control
        /// then paints a plain placeholder instead of throwing, so the designer keeps working and the form still loads.
        /// </summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public static bool NativeAvailable => SR2D.IsAvailable;

        /// <summary>Paints the design-time / no-DLL placeholder: the background colour and the control's type name.</summary>
        protected virtual void OnPaintUnavailable(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(Color.FromArgb(0x80, ForeColor))) g.DrawRectangle(p, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            string text = GetType().Name + (DesignMode ? "" : "\n(SR2D64.dll not found)");
            using var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            using var tb = new SolidBrush(ForeColor);
            g.DrawString(text, Font, tb, ClientRectangle, f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!NativeAvailable) { OnPaintUnavailable(e); base.OnPaint(e); return; }
            EnsureSurface();
            if (_dirty && HasRenderer) { OnRender(_surface!); _dirty = false; ContentChanged(); }
            else if (_surface!.IsGdiSurface) _surface.GdiSync();
            IntPtr hdc = e.Graphics.GetHdc();
            try
            {
                var h = new HandleRef(this, hdc);
                if (Viewed) BlitView(h, Rectangle.Intersect(e.ClipRectangle, ClientRectangle));
                else if (FixedSurfaceSize.IsEmpty)
                {
                    Rectangle r = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
                    if (!r.IsEmpty) _surface!.PaintToDevice(h, r.X, r.Y, r.X, r.Y, r.Width, r.Height);
                }
                else
                {
                    _surface!.PaintToDeviceStretched(h, 0, 0, ClientSize.Width, ClientSize.Height, SmoothStretch);
                }
            }
            finally { e.Graphics.ReleaseHdc(hdc); }
            base.OnPaint(e);   // lets Paint event subscribers draw overlays with GDI+ on top
        }

        /// <summary>
        /// Requests a full redraw: marks the scene dirty so <see cref="Render"/> runs on the
        /// next paint, then invalidates. Call it whenever the scene data changed (mouse move,
        /// timer tick, model update). Cheap to call often: several calls before the next
        /// paint message result in a single Render. (Plain Invalidate() only re-blits the
        /// cached surface.)
        /// </summary>
        /// <summary>Renders on demand, without a WM_PAINT: creates the surface, runs the renderer when dirty and returns it.
        /// What the headless tests (tests/cs/ctlrun) use instead of OnPaint; also handy for "render once, save the picture".</summary>
        public Sprite RenderOnce() { EnsureSurface(); if (_dirty && HasRenderer) { OnRender(_surface!); _dirty = false; ContentChanged(); } return _surface!; }
        
        public void Redraw() { _dirty = true; ContentChanged(); Invalidate(); }

        /// <summary>Same as <see cref="Redraw"/> (older name, kept).</summary>
        public void RefreshScene() => Redraw();

        /// <summary>
        /// Redraws synchronously: runs <see cref="Render"/> now and presents the result
        /// immediately (no message round-trip). Use when you need the picture on screen
        /// before returning, e.g. inside a blocking loop; otherwise prefer <see cref="Redraw"/>.
        /// </summary>
        public void RedrawNow()
        {
            if (!NativeAvailable) { Invalidate(); return; }         // placeholder only (designer / no DLL)
            EnsureSurface();
            if (HasRenderer) { OnRender(_surface!); _dirty = false; }
            Present();
        }

        /// <summary>
        /// Immediate present of the current surface contents, bypassing the message queue.
        /// For game loops: draw into <see cref="Surface"/>, then Present().
        /// </summary>
        public void Present()
        {
            if (_surface == null) return;
            ContentChanged();                                    // whoever drew into Surface: the view's caches are stale
            if (!IsHandleCreated || !Visible)
            {
                // Window not on screen yet (e.g. called from the form constructor): keep the
                // surface content and let the first WM_PAINT show it instead of dropping it.
                _dirty = false;
                Invalidate();
                return;
            }
            IntPtr hdc;
            try { hdc = GetDC(Handle); }
            catch (DllNotFoundException) { _dirty = false; Invalidate(); return; }   // no GDI/user32 on this platform (a Linux
            catch (EntryPointNotFoundException) { _dirty = false; Invalidate(); return; }   // headless runner): paint via WM_PAINT
            if (hdc == IntPtr.Zero) return;
            try
            {
                var h = new HandleRef(this, hdc);
                if (Viewed) BlitView(h, ClientRectangle);
                else if (FixedSurfaceSize.IsEmpty) _surface.PaintToDevice(h);
                else _surface.PaintToDeviceStretched(h, 0, 0, ClientSize.Width, ClientSize.Height, SmoothStretch);
            }
            finally { _ = ReleaseDC(Handle, hdc); }
        }

        /// <summary>Allocates a surface for the current size and copies the old content into it (no-op without the DLL).</summary>
        private void ReplaceSurface()
        {
            if (!NativeAvailable) return;
            var old = _surface; _surface = null;
            EnsureSurface();
            if (old != null) { _surface!.Draw(old, 0, 0, SR2D.Op.Paint); old.Dispose(); }
        }

        private void EnsureSurface()
        {
            if (_surface != null) return;
            if (!NativeAvailable) throw new DllNotFoundException("SR2D64.dll could not be loaded (see SR2D.DllPath / the SR2D_DLL environment variable).");
            Size sz = Viewed ? ImageSize : FixedSurfaceSize.IsEmpty ? ClientSize : FixedSurfaceSize;
            _surface = new Sprite(Math.Max(1, sz.Width), Math.Max(1, sz.Height), GdiSurface);
            _dirty = true;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Viewed) { ViewResized(); Invalidate(); return; }
            if (FixedSurfaceSize.IsEmpty && _surface != null &&
                (_surface.Width != ClientSize.Width || _surface.Height != ClientSize.Height))
            {
                // Keep what was drawn: copy the old content into the new surface. Lets
                // "draw once in the constructor, present later" survive the layout pass.
                ReplaceSurface();
                _dirty = HasRenderer;         // if there is a renderer, let it redraw at the new size
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _surface?.Dispose(); _surface = null; DisposeView(); }
            base.Dispose(disposing);
        }

        [DllImport("user32", ExactSpelling = true)] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32", ExactSpelling = true)] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    }
}
