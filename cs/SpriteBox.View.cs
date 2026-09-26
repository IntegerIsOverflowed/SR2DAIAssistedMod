using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // ImplicitUsings also brings System.Threading.Timer

namespace Sr2d64CSport
{
    /// <summary>Mouse / keyboard navigation of a viewed <see cref="SpriteBox"/>.</summary>
    internal enum SpriteNavigation
    {
        /// <summary>No built-in navigation: the mouse is yours (the context menu, scroll bars and the Zoom / Pan properties still work).</summary>
        None,
        /// <summary>Space = hand (drag the image), middle button drags too, Ctrl + wheel zooms at the pointer; the plain left button is yours.</summary>
        Hand,
        /// <summary>Photoshop-like: as Hand, plus the plain cursor is a magnifier - drag left / right to zoom out / in around the pressed point, click = zoom in one step, Alt + click = out.</summary>
        Full,
    }

    // ------------------------------------------------------------------------------------------------------------------------
    // SpriteBox part 2: SizeMode (PictureBox-like placement of an image of ImageSize), zoom multiplier, pan with overscroll and
    // inertia, client <-> image mapping, scroll bars, context menu and Photoshop-like navigation. Everything here is inactive
    // while SizeMode == None (the classic control: surface == client area).
    // ------------------------------------------------------------------------------------------------------------------------
    internal partial class SpriteBox
    {
        readonly SpriteView _view = new();
        SpriteSizeMode _sizeMode = SpriteSizeMode.None;
        Size _imageSize = Size.Empty;
        Sprite? _screen, _mip; int _mipKx, _mipKy, _mipVersion = -1;
        int _contentVersion; bool _composedValid;
        bool _scrollBars, _inertia = true, _pixelGrid, _viewMenu = true; float _pixelGridMin = 4f; int _pixelGridColor = 0x60000000;
        int _barWidth = 14;
        SR2D.Filter _filter = SR2D.Filter.Auto;
        SpriteNavigation _nav = SpriteNavigation.Full;
        double _zoomStep = 1.25;
        SpriteScrollBar? _hbar, _vbar; bool _barSync;
        Timer? _slideTimer; readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew(); double _slideLast;
        SpriteMenu? _menu;
        Cursor? _userCursor; bool _cursorOwned;

        /// <summary>True when a size mode is active (the view machinery is in use).</summary>
        bool Viewed => _sizeMode != SpriteSizeMode.None;

        // ------------------------------------------------------------------ properties
        [Category("Behavior"), DefaultValue(SpriteSizeMode.None), Description("None: the surface is the client area (classic). Otherwise the surface has ImageSize and is placed like a PictureBox image: centred, stretched, zoomed to fit (borders), filled (clipped), fitted to the width or the height - times Zoom, moved by Pan.")]
        public SpriteSizeMode SizeMode
        {
            get => _sizeMode;
            set
            {
                if (_sizeMode == value) return;
                bool was = Viewed;
                _sizeMode = value;
                if (Viewed)
                {
                    if (_imageSize.IsEmpty) _imageSize = new Size(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
                    _view.Mode = value; _view.ImageSize = _imageSize;
                    if (!was)
                    {   // switching on: keep what was drawn (the old surface is copied into the image-sized one)
                        ReplaceSurface();
                        _dirty = true;                       // a Render handler subscribed later must still get its first call
                        if (_userCursor == null) { _userCursor = Cursor; }
                        ViewResized();
                    }
                }
                else if (was)
                {   // switching off: back to the classic surface (client-sized, GDI); the image content is copied top-left
                    ReplaceSurface();
                    _dirty = true;
                    _screen?.Dispose(); _screen = null; _mip?.Dispose(); _mip = null;
                    _hbar?.Hide(); _vbar?.Hide();
                    StopSlide(); StopGlide(false);
                    if (_cursorOwned && _userCursor != null) { base.Cursor = _userCursor; _cursorOwned = false; }
                    _menu?.Close();
                }
                ViewInvalidate(true);
            }
        }

        [Category("Behavior"), Description("Size of the image (the Surface) when a SizeMode is on. 0,0 = the client size at the moment the mode is switched on.")]
        public Size ImageSize
        {
            get => _imageSize;
            set
            {
                if (_imageSize == value) return;
                _imageSize = value;
                if (!Viewed) return;
                if (_imageSize.IsEmpty) _imageSize = new Size(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
                _view.ImageSize = _imageSize;
                ReplaceSurface();
                _dirty = true;
                _mip?.Dispose(); _mip = null;
                ViewResized();
            }
        }
        bool ShouldSerializeImageSize() => !_imageSize.IsEmpty;
        void ResetImageSize() => ImageSize = Size.Empty;

        [Category("Behavior"), DefaultValue(1.0), Description("Zoom multiplier on top of what the SizeMode gives (1 = the mode's own scale: 1:1 for CenterImage, 'fits' for Zoom ...). Clamped to MinZoom..MaxZoom.")]
        public double Zoom { get => _view.Zoom; set { StopGlide(false); _view.ZoomAtCentre(value); ViewInvalidate(true); } }
        [Category("Behavior"), DefaultValue(1.0 / 64), Description("Smallest Zoom.")]
        public double MinZoom { get => _view.MinZoom; set { _view.MinZoom = value; ViewInvalidate(true); } }
        [Category("Behavior"), DefaultValue(64.0), Description("Largest Zoom.")]
        public double MaxZoom { get => _view.MaxZoom; set { _view.MaxZoom = value; ViewInvalidate(true); } }
        [Category("Behavior"), DefaultValue(1.25), Description("Factor of one zoom step (Ctrl + wheel, Ctrl + plus / minus, menu). A magnifier click uses two steps.")]
        public double ZoomStep { get => _zoomStep; set => _zoomStep = Math.Max(1.01, value); }
        /// <summary>Effective scale of the image on screen in per cent (100 = one image pixel is one screen pixel).</summary>
        [Browsable(false)] public double ZoomPercent => Viewed ? _view.ScaleX * 100 : 100;

        [Category("Behavior"), DefaultValue(SpritePanMode.Free), Description("None: the image stays where the mode puts it. Scroll: it can be moved only as far as it is larger than the view. Free: Photoshop-like - it can be pushed almost out of the view (Overscroll).")]
        public SpritePanMode PanMode { get => _view.PanMode; set { _view.PanMode = value; ViewInvalidate(true); } }
        [Category("Behavior"), DefaultValue(0.9f), Description("Free pan: fraction of the image (per axis) that may be dragged out of the view; 0.9 = up to 90 % may leave, 10 % always stays visible.")]
        public float Overscroll { get => _view.Overscroll; set { _view.Overscroll = value; ViewInvalidate(true); } }
        /// <summary>Offset of the image from where the mode would put it (client pixels). Clamped to the pan limits.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public PointF Pan { get => _view.Pan; set { _view.Pan = value; ViewInvalidate(true); } }
        [Category("Behavior"), DefaultValue(true), Description("Photoshop-like flick: a dragged image keeps sliding after the button is released until friction stops it or it is grabbed again.")]
        public bool Inertia { get => _inertia; set { _inertia = value; if (!value) StopSlide(); } }
        [Category("Behavior"), DefaultValue(0.55f), Description("Friction of the slide: fraction of the speed lost per 100 ms.")]
        public float Friction { get => _view.Friction; set => _view.Friction = Math.Clamp(value, 0.01f, 0.99f); }
        [Category("Behavior"), DefaultValue(30f), Description("Inertia sensitivity: the slowest release speed (px per second) that still starts a slide. Lower = a gentler flick is enough.")]
        public float InertiaThreshold { get => _view.FlingThreshold * 1000; set => _view.FlingThreshold = Math.Clamp(value, 0f, 5000f) / 1000; }
        [Category("Behavior"), DefaultValue(1.3f), Description("Inertia gain: the release speed is multiplied by this. Above 1 the image slides further for the same flick, below 1 it is tamer.")]
        public float InertiaGain { get => _view.FlingGain; set => _view.FlingGain = Math.Clamp(value, 0.1f, 5f); }
        [Category("Behavior"), DefaultValue(true), Description("Zoom commands (magnifier click, wheel, keys, menu, Fit / 100 % / Reset) glide to the new zoom instead of jumping. The scrubby zoom drag is always immediate.")]
        public bool SmoothZoom { get => _smoothZoom; set { _smoothZoom = value; if (!value) StopGlide(true); } }
        [Category("Behavior"), DefaultValue(180), Description("Length of the zoom glide in ms (0 = jump).")]
        public int SmoothZoomTime { get => _glideMs; set => _glideMs = Math.Clamp(value, 0, 2000); }
        bool _smoothZoom = true; int _glideMs = 180;

        [Category("Appearance"), DefaultValue(false), Description("Show SR2D scroll bars (SpriteScrollBar) when the image is larger than the view (always in Free pan mode).")]
        public bool ScrollBars { get => _scrollBars; set { if (_scrollBars == value) return; _scrollBars = value; if (Viewed) ViewResized(); else ViewInvalidate(true); } }
        [Category("Appearance"), DefaultValue(14), Description("Thickness of the scroll bars.")]
        public int ScrollBarWidth { get => _barWidth; set { _barWidth = Math.Clamp(value, 6, 40); if (Viewed) ViewResized(); } }
        [Category("Appearance"), DefaultValue(SR2D.Filter.Auto), Description("Resampling filter of the view. Auto = Nearest when magnifying (pixels stay pixels), area-averaged when shrinking.")]
        public SR2D.Filter ViewFilter { get => _filter; set { _filter = value; ViewInvalidate(false); } }
        [Category("Appearance"), DefaultValue(false), Description("Draw a grid between image pixels once the picture is magnified to PixelGridMinScale or more (the view menu shows the threshold and the current zoom).")]
        public bool PixelGrid { get => _pixelGrid; set { if (_pixelGrid == value) return; _pixelGrid = value; ViewInvalidate(false); } }
        /// <summary>Effective scale (screen pixels per image pixel) from which <see cref="PixelGrid"/> is drawn. Default 4 (= 400 %). Below it the cells would be too small to separate.</summary>
        [Category("Appearance"), DefaultValue(4f), Description("Effective scale (screen pixels per image pixel) from which the pixel grid is drawn. Default 4 = 400 %.")]
        public float PixelGridMinScale { get => _pixelGridMin; set { value = Math.Max(2f, value); if (_pixelGridMin == value) return; _pixelGridMin = value; ViewInvalidate(false); } }
        /// <summary>Colour of the pixel grid lines (ARGB, alpha blended over the picture). Default 0x60000000.</summary>
        [Category("Appearance"), DefaultValue(typeof(Color), "96, 0, 0, 0"), Description("Colour of the pixel grid lines (alpha blended).")]
        public Color PixelGridColor { get => Color.FromArgb(_pixelGridColor); set { if (_pixelGridColor == value.ToArgb()) return; _pixelGridColor = value.ToArgb(); ViewInvalidate(false); } }
        /// <summary>True when the grid is switched on and the current zoom is high enough for it to be drawn.</summary>
        [Browsable(false)] public bool PixelGridVisible => _pixelGrid && Viewed && _view.ScaleX >= _pixelGridMin && _view.ScaleY >= _pixelGridMin;
        [Category("Behavior"), DefaultValue(SpriteNavigation.Full), Description("Built-in mouse / keyboard navigation while a SizeMode is on: None; Hand (Space = hand, middle button drags, Ctrl + wheel zooms); Full (as Hand, plus the magnifier on the plain left button: drag left / right = zoom, click = in, Alt + click = out).")]
        public SpriteNavigation Navigation { get => _nav; set { _nav = value; UpdateCursor(); } }
        [Category("Behavior"), DefaultValue(true), Description("Right click opens the built-in SR2D-drawn menu (size modes, zoom, reset, drag mode, scroll bars, inertia) while a SizeMode is on and no ContextMenuStrip is set.")]
        public bool ViewMenu { get => _viewMenu; set { _viewMenu = value; if (!value) _menu?.Close(); } }
        /// <summary>The built-in view menu (a <see cref="SpriteMenu"/>, built on first use) - add your own items to it.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public SpriteMenu ViewMenuItems => Menu;

        /// <summary>Raised after the zoom, the pan, the mode or the viewport changed.</summary>
        [Category("Action"), Description("Raised after the zoom, the pan, the size mode or the viewport changed.")]
        public event EventHandler? ViewChanged;

        /// <summary>The client rectangle the image is shown in (client area minus the scroll bars).</summary>
        [Browsable(false)] public Rectangle ViewportRectangle => Viewed ? new Rectangle(0, 0, Math.Max(1, ClientSize.Width - (_vbar?.Visible == true ? _barWidth : 0)), Math.Max(1, ClientSize.Height - (_hbar?.Visible == true ? _barWidth : 0))) : ClientRectangle;
        /// <summary>Where the image lands in client coordinates (may exceed the client area).</summary>
        [Browsable(false)] public RectangleF ImageRectangle => Viewed ? _view.Dest : new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
        /// <summary>True while the built-in navigation is dragging (hand or magnifier) - your own mouse handlers may want to stand back.</summary>
        [Browsable(false)] public bool IsNavigating => _drag != DragKind.None;
        /// <summary>The geometry model (for tests or custom UI).</summary>
        [Browsable(false)] public SpriteView View => _view;

        // ------------------------------------------------------------------ mapping
        // All of these take CLIENT coordinates of this control - e.Location from the box's own mouse events, or
        // box.PointToClient(Cursor.Position). Screen coordinates (Cursor.Position / MousePosition) or a parent's
        // e.Location land outside the image and give "outside" / null.
        // Without a SizeMode the surface is the client area (client == image); with FixedSurfaceSize it is stretched
        // over the client area and the mapping scales accordingly.
        (float x, float y) FixedScale => (_fixedSize.Width / (float)Math.Max(1, ClientSize.Width), _fixedSize.Height / (float)Math.Max(1, ClientSize.Height));
        /// <summary>Image (surface) coordinates - fractional pixels, extrapolated outside the image - of a client point of this control.</summary>
        public PointF ToImage(Point client) => ToImage(new PointF(client.X + 0.5f, client.Y + 0.5f));
        public PointF ToImage(PointF client)
        {
            if (Viewed) return _view.ToImage(client);
            if (_fixedSize.IsEmpty) return client;
            var (fx, fy) = FixedScale; return new PointF(client.X * fx, client.Y * fy);
        }
        /// <summary>Client coordinates of an image (surface) point.</summary>
        public PointF ToClient(PointF image)
        {
            if (Viewed) return _view.ToClient(image);
            if (_fixedSize.IsEmpty) return image;
            var (fx, fy) = FixedScale; return new PointF(image.X / fx, image.Y / fy);
        }
        /// <summary>
        /// The image pixel under a client point of this control plus whether it is inside the image (outside = extrapolated
        /// coordinates; its ToString says "outside x,y", which makes a wrong coordinate space obvious).
        /// </summary>
        public ImagePoint ImageAt(Point client)
        {
            if (Viewed) return _view.Hit(client);
            var p = ToImage(client);
            Size img = _fixedSize.IsEmpty ? ClientSize : _fixedSize;
            return new ImagePoint(p.X >= 0 && p.Y >= 0 && p.X < img.Width && p.Y < img.Height, p.X, p.Y);
        }
        public ImagePoint ImageAt(int clientX, int clientY) => ImageAt(new Point(clientX, clientY));
        /// <summary>The image pixel under a client point of this control, or null when the point is outside the image.</summary>
        public Point? ImagePixelAt(Point client) { var h = ImageAt(client); return h.Inside ? h.Pixel : null; }
        public Point? ImagePixelAt(int clientX, int clientY) => ImagePixelAt(new Point(clientX, clientY));
        /// <summary>
        /// The image pixel under a client point, also OUTSIDE the image: the coordinates keep going (negative, or beyond
        /// the image size), so a click beside the picture still gives you a usable image-space target. Same as
        /// <c>ImageAt(client).Pixel</c>; <see cref="ImagePixelAt(Point)"/> is the null-outside variant.
        /// </summary>
        public Point ImagePixelAtUnbounded(Point client) => ImageAt(client).Pixel;
        public Point ImagePixelAtUnbounded(int clientX, int clientY) => ImageAt(new Point(clientX, clientY)).Pixel;
        /// <summary>
        /// The geometry this control would use for another image size / size mode / zoom (its client size and pan mode,
        /// nothing painted, the control untouched) - "which pixel would (x, y) hit if the picture were 2048 x 2048 in Fill
        /// mode?". Pass nothing to get a snapshot of the current state (== <c>View.Clone()</c>).
        /// </summary>
        public SpriteView Geometry(Size? imageSize = null, SpriteSizeMode? sizeMode = null, double? zoom = null, PointF? pan = null)
        {
            var v = _view.Clone();
            if (!Viewed) { v.ViewportSize = ClientSize; v.ImageSize = ClientSize; v.Mode = SpriteSizeMode.CenterImage; v.Zoom = 1; v.Pan = PointF.Empty; }
            if (imageSize != null) v.ImageSize = imageSize.Value;
            if (sizeMode != null) v.Mode = sizeMode.Value == SpriteSizeMode.None ? SpriteSizeMode.CenterImage : sizeMode.Value;
            if (zoom != null) v.Zoom = zoom.Value;
            if (pan != null) v.Pan = pan.Value;
            return v;
        }
        /// <summary>The client rectangle an image pixel covers (a whole pixel of the magnified view).</summary>
        public RectangleF PixelRectangle(int x, int y)
        {
            if (Viewed) return _view.PixelRect(x, y);
            if (_fixedSize.IsEmpty) return new RectangleF(x, y, 1, 1);
            var (fx, fy) = FixedScale; return new RectangleF(x / fx, y / fy, 1 / fx, 1 / fy);
        }

        // ------------------------------------------------------------------ zoom / pan commands
        /// <summary>Zooms so the image point under <paramref name="clientAnchor"/> stays there.</summary>
        public void ZoomAt(double zoom, Point clientAnchor) { if (!Viewed) return; StopSlide(); GlideTo(zoom, new PointF(clientAnchor.X + 0.5f, clientAnchor.Y + 0.5f), null); }
        public void ZoomIn(Point? clientAnchor = null) => ZoomAt(TargetZoom * _zoomStep, clientAnchor ?? Centre);
        public void ZoomOut(Point? clientAnchor = null) => ZoomAt(TargetZoom / _zoomStep, clientAnchor ?? Centre);
        /// <summary>The zoom a running glide is heading for (or the current zoom): repeated steps chain instead of restarting from the half-way state.</summary>
        double TargetZoom => _gliding ? _glideZ1 : _view.Zoom;
        /// <summary>Zoom back to 1 (the mode's own scale), image centred on the same point.</summary>
        public void ResetZoom() => ZoomAt(1, Centre);
        /// <summary>One image pixel = one screen pixel, whatever the mode.</summary>
        public void ActualPixels() { if (Viewed && _view.BaseScaleX > 0) ZoomAt(1 / _view.BaseScaleX, Centre); }
        /// <summary>Zoom so the whole image is visible (borders on one axis), whatever the mode.</summary>
        public void FitToView() { if (!Viewed) return; StopSlide(); var vp = ViewportRectangle; double s = Math.Min((double)vp.Width / _imageSize.Width, (double)vp.Height / _imageSize.Height); GlideTo(s / _view.BaseScaleX, new PointF(vp.Width / 2f, vp.Height / 2f), PointF.Empty); }
        /// <summary>Puts the image back where the mode places it (pan 0).</summary>
        public void ResetPan() { if (!Viewed) return; StopSlide(); GlideTo(TargetZoom, new PointF(ViewportRectangle.Width / 2f, ViewportRectangle.Height / 2f), PointF.Empty); }
        /// <summary>Moves the image by a client-pixel delta (clamped to the pan limits).</summary>
        public void PanBy(float dx, float dy) { if (!Viewed) return; _view.PanBy(dx, dy); ViewInvalidate(true); }
        Point Centre { get { var v = ViewportRectangle; return new Point(v.Width / 2, v.Height / 2); } }

        // ------------------------------------------------------------------ invalidation
        /// <summary>Something about the view (or the picture) changed: recompose on the next paint; sync bars; raise ViewChanged.</summary>
        void ViewInvalidate(bool viewChanged)
        {
            _composedValid = false;
            if (!Viewed) { Invalidate(); return; }
            if (viewChanged) { SyncBars(); ViewChanged?.Invoke(this, EventArgs.Empty); }
            Invalidate();
        }
        /// <summary>The picture content changed (Redraw / Present / OnRender): the shrunk copy and the composed screen are stale.</summary>
        void ContentChanged() { _contentVersion++; _composedValid = false; }

        // ------------------------------------------------------------------ layout (viewport + scroll bars)
        bool _inResize;
        /// <summary>Which scroll bars the current client size / zoom needs. Deterministic for a given state, so a layout that applied it never asks for another one.</summary>
        (bool h, bool v) DecideBars() => _scrollBars ? _view.DecideScrollBars(ClientSize, _barWidth) : (false, false);
        Size ViewportFor(bool h, bool v) => SpriteView.ViewportFor(ClientSize, _barWidth, h, v);
        bool HBarShown => _hbar?.Visible == true;
        bool VBarShown => _vbar?.Visible == true;

        void ViewResized()
        {
            if (!Viewed || _inResize) return;
            _inResize = true;
            try
            {
                var (needH, needV) = DecideBars();
                _view.ViewportSize = ViewportFor(needH, needV);
                if (needH || needV)
                {
                    _hbar ??= MakeBar(Orientation.Horizontal); _vbar ??= MakeBar(Orientation.Vertical);
                    _hbar.Bounds = new Rectangle(0, ClientSize.Height - _barWidth, ClientSize.Width - (needV ? _barWidth : 0), _barWidth);
                    _vbar.Bounds = new Rectangle(ClientSize.Width - _barWidth, 0, _barWidth, ClientSize.Height - (needH ? _barWidth : 0));
                    if (_hbar.Visible != needH) _hbar.Visible = needH;
                    if (_vbar.Visible != needV) _vbar.Visible = needV;
                }
                else { if (HBarShown) _hbar!.Visible = false; if (VBarShown) _vbar!.Visible = false; }
                if (_screen != null && (_screen.Width != ClientSize.Width || _screen.Height != ClientSize.Height)) { _screen.Dispose(); _screen = null; }
            }
            finally { _inResize = false; }
            ViewInvalidate(true);
        }
        SpriteScrollBar MakeBar(Orientation o)
        {
            var b = new SpriteScrollBar { Orientation = o, Step = 32, BackColor = SpriteControlBase.DefaultBack, Visible = false };
            b.ValueChanged += (_, _) => { if (_barSync || !Viewed) return; StopSlide(); _view.SetScroll(o == Orientation.Horizontal, b.Value); _composedValid = false; ViewChanged?.Invoke(this, EventArgs.Empty); Invalidate(); };
            Controls.Add(b);
            return b;
        }
        void SyncBars()
        {
            if (_scrollBars && !_relayoutPending && !_inResize)
            {   // the range changes with the zoom, which can flip a bar on / off: re-layout once when the *decision* differs from what is shown
                bool curH = HBarShown, curV = VBarShown;
                var (h, v) = DecideBars();
                _view.ViewportSize = ViewportFor(curH, curV);           // keep the geometry consistent with the bars actually shown until the re-layout runs
                if (h != curH || v != curV) { _relayoutPending = true; BeginInvokeSafe(() => { _relayoutPending = false; ViewResized(); }); }
            }
            if (_hbar == null && _vbar == null) return;
            _barSync = true;
            try
            {
                if (HBarShown) { var i = _view.HorizontalScroll; _hbar!.Minimum = 0; _hbar.Maximum = i.Maximum; _hbar.PageSize = i.Page; _hbar.Value = i.Value; }
                if (VBarShown) { var i = _view.VerticalScroll; _vbar!.Minimum = 0; _vbar.Maximum = i.Maximum; _vbar.PageSize = i.Page; _vbar.Value = i.Value; }
            }
            finally { _barSync = false; }
        }
        bool _relayoutPending;
        // The deferred bar re-layout. Never run it synchronously: on a control without a handle (a strip that is built
        // while its host is still invisible - e.g. the demo's "SpriteBox SizeMode" test selected as the first test) the
        // old inline fallback turned ViewResized -> ViewInvalidate -> SyncBars -> ViewResized -> ... into infinite
        // recursion = StackOverflowException. With a handle BeginInvoke defers it to the message queue; without one the
        // pending flag stays set until OnHandleCreated -> ViewResized() (which is the re-layout and clears it).
        void BeginInvokeSafe(Action a) { if (IsHandleCreated) BeginInvoke(a); }

        // ------------------------------------------------------------------ compose (image -> screen buffer)
        /// <summary>
        /// Builds the screen picture: background, the image scaled into its destination rectangle, optional pixel grid.
        /// Only the viewport pixels are ever computed - the warp kernel clips to the lock rect before it touches a pixel, so a
        /// 64x zoom of a 4k image costs the same as a 1x view. Shrinking below 1/2 samples a cached box-averaged copy
        /// (rebuilt only when the picture or the shrink factor changes), so a panned overview of a huge image is cheap too.
        /// The result is cached until the view or the picture changes (an exposure repaint is a plain blit).
        /// </summary>
        Sprite Compose()
        {
            if (_screen == null || _screen.Width != ClientSize.Width || _screen.Height != ClientSize.Height) { _screen?.Dispose(); _screen = new Sprite(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), GdiSurface); _composedValid = false; }
            if (_composedValid) { if (_screen.IsGdiSurface) _screen.GdiSync(); return _screen; }
            var vp = ViewportRectangle;
            var dest = _view.Dest;
            int bg = BackColor.ToArgb();
            // background strips around the image (not the whole viewport: the image part is overwritten anyway).
            // The strips are one pixel wider than the destination rectangle on every side: the warp paints the pixels whose
            // CENTRE falls inside dest, so with a fractional dest edge (a centred image lands on .5 whenever viewport - image
            // is odd) the boundary row / column would belong to neither the strip nor the image and stay whatever was in the
            // buffer (black on a fresh one) - the "black line at the edge of the picture" while resizing.
            int dl = (int)MathF.Ceiling(dest.Left), dt = (int)MathF.Ceiling(dest.Top), dr = (int)MathF.Floor(dest.Right), db = (int)MathF.Floor(dest.Bottom);
            if (dt > vp.Top) _screen.ClearRect(vp.Left, vp.Right, vp.Top, Math.Min(dt, vp.Bottom), bg);
            if (db < vp.Bottom) _screen.ClearRect(vp.Left, vp.Right, Math.Max(db, vp.Top), vp.Bottom, bg);
            if (dl > vp.Left) _screen.ClearRect(vp.Left, Math.Min(dl, vp.Right), Math.Max(dt, vp.Top), Math.Min(db, vp.Bottom), bg);
            if (dr < vp.Right) _screen.ClearRect(Math.Max(dr, vp.Left), vp.Right, Math.Max(dt, vp.Top), Math.Min(db, vp.Bottom), bg);
            // the corner between two scroll bars
            if (HBarShown && VBarShown) _screen.ClearRect(vp.Right, ClientSize.Width, vp.Bottom, ClientSize.Height, SR2D.ARGB(255, 0x20, 0x24, 0x28));
            // the image
            float sx = _view.ScaleX, sy = _view.ScaleY;
            if (dest.IntersectsWith(vp) && _surface != null)
            {
                Sprite src = _surface; float ssx = sx, ssy = sy;
                if (sx < 0.5f || sy < 0.5f)
                {   // shrunk copy: integer box average, then bilinear from there (~1:1 .. 1:2 sampling)
                    int kx = Math.Max(1, (int)MathF.Floor(1 / sx)), ky = Math.Max(1, (int)MathF.Floor(1 / sy));
                    int mw = (src.Width + kx - 1) / kx, mh = (src.Height + ky - 1) / ky;
                    if (_mip == null || _mipKx != kx || _mipKy != ky || _mipVersion != _contentVersion || _mip.Width != mw || _mip.Height != mh)
                    {
                        if (_mip == null || _mip.Width != mw || _mip.Height != mh) { _mip?.Dispose(); _mip = new Sprite(mw, mh); }
                        _mip.DrawScaled(src, 0, 0, mw, mh, SR2D.Op.Paint, SR2D.Filter.Area);
                        _mipKx = kx; _mipKy = ky; _mipVersion = _contentVersion;
                    }
                    src = _mip; ssx = sx * kx; ssy = sy * ky;
                }
                var f = _filter;
                if (f == SR2D.Filter.Auto) f = (ssx >= 1f && ssy >= 1f) ? SR2D.Filter.Nearest : SR2D.Filter.Bilinear;
                else if (src == _mip && (f == SR2D.Filter.Area || f == SR2D.Filter.BilinearArea || f == SR2D.Filter.BicubicArea)) f = SR2D.Filter.Bilinear;   // already averaged
                _screen.SetLockRect(vp.Left, vp.Right, vp.Top, vp.Bottom);
                _screen.DrawScaled(src, dest.X, dest.Y, ssx, ssy, 0f, 0f, SR2D.Op.Paint, f);
                if (_pixelGrid && sx >= _pixelGridMin && sy >= _pixelGridMin)
                {
                    int gc = _pixelGridColor;
                    int x0 = Math.Max(0, (int)MathF.Floor((vp.Left - dest.X) / sx)), x1 = Math.Min(_imageSize.Width, (int)MathF.Ceiling((vp.Right - dest.X) / sx));
                    int y0 = Math.Max(0, (int)MathF.Floor((vp.Top - dest.Y) / sy)), y1 = Math.Min(_imageSize.Height, (int)MathF.Ceiling((vp.Bottom - dest.Y) / sy));
                    for (int x = x0; x <= x1; x++) { float px = dest.X + (x * sx); _screen.DrawWideLine(px, Math.Max(vp.Top, dest.Top), px, Math.Min(vp.Bottom, dest.Bottom), gc, 1f, false, SR2D.LineOp.AlphaBlend); }
                    for (int y = y0; y <= y1; y++) { float py = dest.Y + (y * sy); _screen.DrawWideLine(Math.Max(vp.Left, dest.Left), py, Math.Min(vp.Right, dest.Right), py, gc, 1f, false, SR2D.LineOp.AlphaBlend); }
                }
                _screen.SetLockRect();
            }
            _composedValid = true;
            return _screen;
        }

        // ------------------------------------------------------------------ navigation: mouse
        enum DragKind { None, Hand, Magnifier }
        DragKind _drag; Point _dragStart, _dragLast; double _dragZoom0; bool _space, _moved;
        readonly (double t, Point p)[] _trail = new (double, Point)[6]; int _trailN;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Viewed || _nav == SpriteNavigation.None) return;
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle) Focus();
            StopSlide(); StopGlide(false);
            bool hand = e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _space);
            if (hand && PanMode != SpritePanMode.None) { _drag = DragKind.Hand; Capture = true; _dragStart = _dragLast = e.Location; _moved = false; _trailN = 0; Trail(e.Location); UpdateCursor(); }
            else if (e.Button == MouseButtons.Left && _nav == SpriteNavigation.Full) { _drag = DragKind.Magnifier; Capture = true; _dragStart = _dragLast = e.Location; _dragZoom0 = _view.Zoom; _moved = false; UpdateCursor(); }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Viewed) return;
            switch (_drag)
            {
                case DragKind.Hand:
                {
                    int dx = e.X - _dragLast.X, dy = e.Y - _dragLast.Y; _dragLast = e.Location;
                    if (dx != 0 || dy != 0) { _moved = true; _view.PanBy(dx, dy); Trail(e.Location); _composedValid = false; SyncBars(); ViewChanged?.Invoke(this, EventArgs.Empty); if (IsHandleCreated) PresentView(); }
                    break;
                }
                case DragKind.Magnifier:
                {
                    int dx = e.X - _dragStart.X;
                    if (Math.Abs(dx) >= 3 || _moved)
                    {   // scrubby zoom: 120 px of travel = one octave, around the pressed point
                        _moved = true;
                        _view.ZoomAt(_dragZoom0 * Math.Pow(2, dx / 120.0), new PointF(_dragStart.X + 0.5f, _dragStart.Y + 0.5f));
                        _composedValid = false; SyncBars(); ViewChanged?.Invoke(this, EventArgs.Empty); if (IsHandleCreated) PresentView();
                    }
                    break;
                }
                default: UpdateCursor(); break;
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right && Viewed && _viewMenu && ContextMenuStrip == null && _drag == DragKind.None) { Menu.Show(this, e.Location); return; }
            if (!Viewed || _drag == DragKind.None) return;
            var kind = _drag; _drag = DragKind.None; Capture = false;
            if (kind == DragKind.Hand)
            {
                if (_inertia && _moved) Fling();
            }
            else if (kind == DragKind.Magnifier && !_moved)
            {   // a click: two steps in, Alt = out, around the click
                double f = _zoomStep * _zoomStep;
                ZoomAt((ModifierKeys & Keys.Alt) != 0 ? TargetZoom / f : TargetZoom * f, e.Location);
            }
            UpdateCursor();
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (_drag != DragKind.None && !Capture) { _drag = DragKind.None; UpdateCursor(); } }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Viewed || _nav == SpriteNavigation.None) return;
            bool handled = false;
            if ((ModifierKeys & Keys.Control) != 0) { ZoomAt(TargetZoom * Math.Pow(_zoomStep, e.Delta / 120.0), e.Location); handled = true; }
            else if (PanMode != SpritePanMode.None)
            {
                StopSlide();
                float d = e.Delta * 0.5f;
                if ((ModifierKeys & Keys.Shift) != 0) _view.PanBy(d, 0); else _view.PanBy(0, d);
                ViewInvalidate(true); handled = true;
            }
            if (handled && e is HandledMouseEventArgs h) h.Handled = true;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_drag == DragKind.None) UpdateCursor(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); UpdateCursor(); }

        /// <summary>Presents the composed view now (drag feedback without a WM_PAINT round trip).</summary>
        void PresentView()
        {
            if (!Visible) { Invalidate(); return; }
            IntPtr hdc = GetDC(Handle); if (hdc == IntPtr.Zero) return;
            try { BlitView(new HandleRef(this, hdc), ClientRectangle); }
            finally { _ = ReleaseDC(Handle, hdc); }
        }
        /// <summary>Paints the composed screen buffer: the viewport part of <paramref name="clip"/> and the corner between two scroll bars (the bars paint themselves).</summary>
        void BlitView(HandleRef h, Rectangle clip)
        {
            var scr = Compose();
            Rectangle r = Rectangle.Intersect(clip, ViewportRectangle);
            if (!r.IsEmpty) scr.PaintToDevice(h, r.X, r.Y, r.X, r.Y, r.Width, r.Height);
            if (HBarShown && VBarShown)
            {
                var vp = ViewportRectangle;
                Rectangle c = Rectangle.Intersect(clip, new Rectangle(vp.Right, vp.Bottom, ClientSize.Width - vp.Right, ClientSize.Height - vp.Bottom));
                if (!c.IsEmpty) scr.PaintToDevice(h, c.X, c.Y, c.X, c.Y, c.Width, c.Height);
            }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (Viewed) ViewResized(); }

        // ------------------------------------------------------------------ navigation: keyboard
        protected override bool IsInputKey(Keys keyData) => ((keyData & Keys.KeyCode) == Keys.Space && Viewed && _nav != SpriteNavigation.None) || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Viewed || _nav == SpriteNavigation.None) return;
            if (e.KeyCode == Keys.Space) { if (!_space) { _space = true; UpdateCursor(); } e.Handled = true; return; }
            if (e.KeyCode == Keys.Menu) { UpdateCursor(); return; }
            if (e.Control)
            {
                switch (e.KeyCode)
                {
                    case Keys.Oemplus: case Keys.Add: ZoomIn(); e.Handled = true; break;
                    case Keys.OemMinus: case Keys.Subtract: ZoomOut(); e.Handled = true; break;
                    case Keys.D0: case Keys.NumPad0: if (e.Alt) ActualPixels(); else if (e.Shift) FitToView(); else ResetZoom(); e.Handled = true; break;
                }
            }
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space && _space) { _space = false; if (_drag == DragKind.Hand && (MouseButtons & MouseButtons.Left) == 0) _drag = DragKind.None; UpdateCursor(); e.Handled = true; }
            if (e.KeyCode == Keys.Menu) UpdateCursor();
        }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _space = false; UpdateCursor(); }

        // ------------------------------------------------------------------ cursor
        /// <summary>The cursor shown when the navigation does not need its own (hand / magnifier). Setting Cursor while a SizeMode is on stores it here.</summary>
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override Cursor Cursor
        {
            get => base.Cursor;
            set { if (_cursorOwned) { _userCursor = value ?? Cursors.Default; UpdateCursor(); } else { base.Cursor = value; } }
        }
        void UpdateCursor()
        {
            if (!Viewed || _nav == SpriteNavigation.None)
            {
                if (_cursorOwned) { _cursorOwned = false; base.Cursor = _userCursor ?? Cursors.Default; }
                return;
            }
            if (!_cursorOwned) { _userCursor ??= base.Cursor; _cursorOwned = true; }
            Cursor c;
            if (_drag == DragKind.Hand) c = SpriteCursors.Get(SpriteCursors.Kind.HandGrab, this);
            else if (_drag == DragKind.Magnifier) c = SpriteCursors.Get((ModifierKeys & Keys.Alt) != 0 ? SpriteCursors.Kind.ZoomOut : SpriteCursors.Kind.ZoomIn, this);
            else if (_space && PanMode != SpritePanMode.None) c = SpriteCursors.Get(SpriteCursors.Kind.HandOpen, this);
            else if (_nav == SpriteNavigation.Full) c = SpriteCursors.Get((ModifierKeys & Keys.Alt) != 0 ? SpriteCursors.Kind.ZoomOut : SpriteCursors.Kind.ZoomIn, this);
            else c = _userCursor ?? Cursors.Default;
            if (base.Cursor != c) base.Cursor = c;
        }

        // ------------------------------------------------------------------ inertia
        void Trail(Point p)
        {
            double t = _clock.Elapsed.TotalMilliseconds;
            if (_trailN < _trail.Length) { _trail[_trailN++] = (t, p); }
            else { Array.Copy(_trail, 1, _trail, 0, _trail.Length - 1); _trail[^1] = (t, p); }
        }
        void Fling()
        {
            if (_trailN < 2) return;
            double now = _clock.Elapsed.TotalMilliseconds;
            var (lastT, lastP) = _trail[_trailN - 1];
            if (now - lastT > 120) return;                           // the hand stopped before letting go
            // oldest sample within 150 ms of the release
            int i = _trailN - 1; while (i > 0 && now - _trail[i - 1].t <= 150) i--;
            var (firstT, firstP) = _trail[i];
            double dt = lastT - firstT; if (dt < 8) return;
            _view.Fling(new PointF((float)((lastP.X - firstP.X) / dt), (float)((lastP.Y - firstP.Y) / dt)));
            if (!_view.IsSliding) return;
            _slideTimer ??= new Timer { Interval = 15 };
            _slideTimer.Tick -= SlideTick; _slideTimer.Tick += SlideTick;
            _slideLast = now; _slideTimer.Start();
        }
        void SlideTick(object? sender, EventArgs e)
        {
            double now = _clock.Elapsed.TotalMilliseconds, dt = now - _slideLast; _slideLast = now;
            bool moved = _view.Step(dt);
            if (_gliding) moved |= GlideStep(now);
            if (moved) { _composedValid = false; SyncBars(); ViewChanged?.Invoke(this, EventArgs.Empty); if (IsHandleCreated) PresentView(); }
            if (!_view.IsSliding && !_gliding) _slideTimer?.Stop();
        }
        void StopSlide() { _view.StopSliding(); if (!_gliding) _slideTimer?.Stop(); }

        // ------------------------------------------------------------------ smooth zoom (glide)
        bool _gliding; double _glideT0, _glideZ0, _glideZ1, _glideSm; PointF _glideAnchor, _glideDrift, _glidePanEnd;
        /// <summary>
        /// Moves the view to "ZoomAt(zoom, anchor)" (plus an optional final pan) - at once, or as a short glide when
        /// <see cref="SmoothZoom"/> is on. The image point under the anchor stays put during the glide; a pan target is
        /// blended in linearly on top, so Fit / Reset position glide too.
        /// </summary>
        void GlideTo(double zoom, PointF anchor, PointF? panTarget)
        {
            double z0 = _view.Zoom; PointF p0 = _view.Pan;
            _view.ZoomAt(zoom, anchor); double z1 = _view.Zoom; PointF pa1 = _view.Pan;          // end of the anchored zoom alone
            PointF p1 = pa1;
            if (panTarget.HasValue) { _view.Pan = panTarget.Value; _view.Layout(); p1 = _view.Pan; }
            bool same = Math.Abs(z1 - z0) < 1e-9 && Math.Abs(p1.X - p0.X) < 0.5f && Math.Abs(p1.Y - p0.Y) < 0.5f;
            if (same || !_smoothZoom || _glideMs <= 0 || !IsHandleCreated || !Visible) { _gliding = false; ViewInvalidate(true); return; }   // the view is at the end state already
            _view.Zoom = z0; _view.Pan = p0; _view.Layout();                                       // back to the start; the timer walks the way
            _gliding = true; _glideT0 = _clock.Elapsed.TotalMilliseconds; _glideZ0 = z0; _glideZ1 = z1; _glideAnchor = anchor; _glideSm = 0;
            _glideDrift = new PointF(p1.X - pa1.X, p1.Y - pa1.Y); _glidePanEnd = p1;
            _slideTimer ??= new Timer { Interval = 15 };
            _slideTimer.Tick -= SlideTick; _slideTimer.Tick += SlideTick;
            _slideLast = _glideT0; _slideTimer.Start();
        }
        /// <summary>Ends a glide: <paramref name="finish"/> jumps to its end state, otherwise the view stays where it is.</summary>
        void StopGlide(bool finish)
        {
            if (!_gliding) return;
            _gliding = false;
            if (finish) { _view.Zoom = _glideZ1; _view.Pan = _glidePanEnd; _view.Layout(); ViewInvalidate(true); }
            if (!_view.IsSliding) _slideTimer?.Stop();
        }
        bool GlideStep(double now)
        {
            double t = Math.Clamp((now - _glideT0) / _glideMs, 0, 1), sm = t * t * (3 - (2 * t));
            double z = Math.Exp(Math.Log(_glideZ0) + ((Math.Log(_glideZ1) - Math.Log(_glideZ0)) * sm));
            _view.ZoomAt(z, _glideAnchor);                       // keeps the image point under the anchor (as moved so far)
            float dsm = (float)(sm - _glideSm); _glideSm = sm;     // the pan drift is applied incrementally: the anchor rides along with it
            var pa = _view.Pan; _view.Pan = new PointF(pa.X + (_glideDrift.X * dsm), pa.Y + (_glideDrift.Y * dsm));
            if (t >= 1) { _view.Zoom = _glideZ1; _view.Pan = _glidePanEnd; _gliding = false; }
            _view.Layout();
            return true;
        }
        /// <summary>True while a zoom glide is running.</summary>
        [Browsable(false)] public bool IsGliding => _gliding;

        // ------------------------------------------------------------------ context menu (SR2D-drawn, see SpriteControls.Menu.cs)
        SpriteMenu Menu
        {
            get
            {
                if (_menu != null) return _menu;
                var m = _menu = new SpriteMenu { MinWidth = 190 };
                var modes = m.AddSub("Size mode");
                foreach (SpriteSizeMode sm in Enum.GetValues<SpriteSizeMode>()) { if (sm == SpriteSizeMode.None) continue; var mm = sm; modes.AddRadio(ModeText(mm), () => _sizeMode == mm, () => SizeMode = mm); }
                m.AddSeparator();
                m.Add("Zoom in", () => ZoomIn(), hint: "Ctrl +").HintProvider = () => $"{ZoomPercent:0.#} %  Ctrl +";
                m.Add("Zoom out", () => ZoomOut(), hint: "Ctrl -");
                m.Add("Actual pixels (100 %)", ActualPixels, hint: "Ctrl Alt 0");
                m.Add("Fit to view", FitToView, hint: "Ctrl Shift 0");
                m.Add("Reset zoom", ResetZoom, hint: "Ctrl 0");
                m.Add("Reset position", ResetPan);
                m.AddSeparator();
                var drag = m.AddSub("Drag");
                foreach (SpritePanMode pm in Enum.GetValues<SpritePanMode>()) { var p = pm; drag.AddRadio(PanText(p), () => PanMode == p, () => PanMode = p); }
                m.AddCheck("Scroll bars", () => _scrollBars, v => ScrollBars = v);
                m.AddCheck("Inertia", () => _inertia, v => Inertia = v);
                m.AddCheck("Smooth zoom", () => _smoothZoom, v => SmoothZoom = v);
                m.AddCheck("Pixel grid", () => _pixelGrid, v => PixelGrid = v).HintProvider = () => PixelGridVisible ? $"{ZoomPercent:0} %" : $"from {_pixelGridMin * 100:0} %  (now {ZoomPercent:0.#} %)";
                return m;
            }
        }
        /// <summary>Opens the built-in view menu at a client point (what the right button does).</summary>
        public void ShowViewMenu(Point client) { if (Viewed) Menu.Show(this, client); }
        static string PanText(SpritePanMode m) => m switch { SpritePanMode.None => "Locked", SpritePanMode.Scroll => "Scroll (classic)", _ => "Free (overscroll)" };
        static string ModeText(SpriteSizeMode m) => m switch { SpriteSizeMode.CenterImage => "Center (1:1)", SpriteSizeMode.StretchImage => "Stretch", SpriteSizeMode.Zoom => "Zoom to fit (borders)", SpriteSizeMode.Fill => "Fill (clipped)", SpriteSizeMode.FitWidth => "Fit width", SpriteSizeMode.FitHeight => "Fit height", _ => m.ToString() };

        void DisposeView()
        {
            _screen?.Dispose(); _screen = null; _mip?.Dispose(); _mip = null;
            _slideTimer?.Dispose(); _slideTimer = null;
            _menu?.Dispose(); _menu = null;
        }
    }
}
