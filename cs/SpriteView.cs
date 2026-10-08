using System;
using System.Drawing;

namespace Sr2d64CSport
{
    /// <summary>How <see cref="SpriteBox"/> places its image (the <see cref="SpriteBox.Surface"/> of <see cref="SpriteBox.ImageSize"/>) in the client area.</summary>
    public enum SpriteSizeMode
    {
        /// <summary>Classic behaviour: the surface IS the client area (1:1, follows the size). Zoom / pan / navigation are off.</summary>
        None,
        /// <summary>The image is drawn 1:1 (times <see cref="SpriteBox.Zoom"/>) centred in the client area.</summary>
        CenterImage,
        /// <summary>The image is stretched to the client area, aspect ratio not kept (times Zoom, centred).</summary>
        StretchImage,
        /// <summary>The image is scaled to fit inside the client area keeping the aspect ratio (borders on one axis) - PictureBox's "Zoom".</summary>
        Zoom,
        /// <summary>The image is scaled to cover the client area keeping the aspect ratio (clipped on one axis).</summary>
        Fill,
        /// <summary>The image is scaled so its width fills the client width (the height may be clipped or leave borders).</summary>
        FitWidth,
        /// <summary>The image is scaled so its height fills the client height.</summary>
        FitHeight,
    }

    /// <summary>What dragging the image (hand) may do.</summary>
    public enum SpritePanMode
    {
        /// <summary>The image cannot be moved.</summary>
        None,
        /// <summary>Classic scrolling: the image can be moved only as far as it is larger than the client area (a small image stays put).</summary>
        Scroll,
        /// <summary>Photoshop-like: the image can be pushed almost out of the client area - <see cref="SpriteView.Overscroll"/> says how much of it may leave.</summary>
        Free,
    }

    /// <summary>A client point mapped into image pixels (<see cref="SpriteView.Hit"/>).</summary>
    public readonly struct ImagePoint
    {
        /// <summary>False when the point lies outside the image (X / Y are then extrapolated - beyond 0 or the size).</summary>
        public readonly bool Inside;
        /// <summary>Image coordinates (pixel units, fractional: 0.5,0.5 is the centre of pixel 0,0).</summary>
        public readonly float X, Y;
        public ImagePoint(bool inside, float x, float y) { Inside = inside; X = x; Y = y; }
        public bool OutOfBounds => !Inside;
        /// <summary>The pixel (floor of X / Y).</summary>
        public Point Pixel => new Point((int)MathF.Floor(X), (int)MathF.Floor(Y));
        public PointF Location => new PointF(X, Y);
        public override string ToString() => $"{(Inside ? "" : "outside ")}{X:0.##},{Y:0.##}";
    }

    /// <summary>
    /// The geometry of an image shown inside a viewport: size mode, zoom multiplier, pan offset with clamping (classic or
    /// Photoshop-like overscroll), client &lt;-&gt; image mapping, scroll-bar ranges and a small inertia model. No WinForms in
    /// here - <see cref="SpriteBox"/> owns one and every number it shows comes from it; it can be unit-tested headlessly.
    /// Call <see cref="Layout"/> (or read <see cref="Dest"/>, which does) after changing anything.
    /// </summary>
    public sealed class SpriteView
    {
        Size _image = new Size(1, 1), _viewport = new Size(1, 1);
        SpriteSizeMode _mode = SpriteSizeMode.CenterImage;
        SpritePanMode _panMode = SpritePanMode.Free;
        double _zoom = 1, _minZoom = 1.0 / 64, _maxZoom = 64;
        float _overscroll = 0.9f;
        PointF _pan;
        bool _dirty = true;
        RectangleF _dest; float _sx = 1, _sy = 1; PointF _panMin, _panMax;

        /// <summary>Size of the image (the surface) in pixels.</summary>
        public Size ImageSize { get => _image; set { value = new Size(Math.Max(1, value.Width), Math.Max(1, value.Height)); if (_image == value) return; _image = value; _dirty = true; } }
        /// <summary>Size of the area the image is shown in.</summary>
        public Size ViewportSize { get => _viewport; set { value = new Size(Math.Max(1, value.Width), Math.Max(1, value.Height)); if (_viewport == value) return; _viewport = value; _dirty = true; } }
        public SpriteSizeMode Mode { get => _mode; set { if (_mode == value) return; _mode = value; _dirty = true; } }
        public SpritePanMode PanMode { get => _panMode; set { if (_panMode == value) return; _panMode = value; _dirty = true; } }
        /// <summary>Multiplier on top of the mode's scale (1 = what the mode gives). Clamped to MinZoom..MaxZoom.</summary>
        public double Zoom { get => _zoom; set { value = Math.Clamp(value, _minZoom, _maxZoom); if (_zoom == value) return; _zoom = value; _dirty = true; } }
        public double MinZoom { get => _minZoom; set { _minZoom = Math.Max(1e-4, value); Zoom = _zoom; _dirty = true; } }
        public double MaxZoom { get => _maxZoom; set { _maxZoom = Math.Max(_minZoom, value); Zoom = _zoom; _dirty = true; } }
        /// <summary>Free pan: fraction of the image (per axis) that may be pushed out of the viewport (0.9 = 90 % may leave, 10 % always stays visible).</summary>
        public float Overscroll { get => _overscroll; set { value = Math.Clamp(value, 0f, 1f); if (_overscroll == value) return; _overscroll = value; _dirty = true; } }
        /// <summary>Offset of the image from where the mode would put it (client pixels). Clamped by Layout.</summary>
        public PointF Pan { get => _pan; set { if (_pan == value) return; _pan = value; _dirty = true; } }

        // ------------------------------------------------------------------ results
        /// <summary>Where the image lands in the viewport (client pixels, may exceed the viewport).</summary>
        public RectangleF Dest { get { Layout(); return _dest; } }
        /// <summary>Effective scale (client pixels per image pixel) on each axis.</summary>
        public float ScaleX { get { Layout(); return _sx; } }
        public float ScaleY { get { Layout(); return _sy; } }
        /// <summary>Scale the mode alone gives (Zoom = 1), x axis.</summary>
        public float BaseScaleX => ModeScale().sx;
        public float BaseScaleY => ModeScale().sy;
        /// <summary>Allowed pan range (after Layout).</summary>
        public PointF PanMin { get { Layout(); return _panMin; } }
        public PointF PanMax { get { Layout(); return _panMax; } }
        /// <summary>True when the image can be moved at all along an axis.</summary>
        public bool CanPanX { get { Layout(); return _panMax.X > _panMin.X; } }
        public bool CanPanY { get { Layout(); return _panMax.Y > _panMin.Y; } }

        (float sx, float sy) ModeScale()
        {
            float vw = _viewport.Width, vh = _viewport.Height, iw = _image.Width, ih = _image.Height;
            switch (_mode)
            {
                case SpriteSizeMode.StretchImage: return (vw / iw, vh / ih);
                case SpriteSizeMode.Zoom: { float s = Math.Min(vw / iw, vh / ih); return (s, s); }
                case SpriteSizeMode.Fill: { float s = Math.Max(vw / iw, vh / ih); return (s, s); }
                case SpriteSizeMode.FitWidth: { float s = vw / iw; return (s, s); }
                case SpriteSizeMode.FitHeight: { float s = vh / ih; return (s, s); }
                default: return (1, 1);
            }
        }

        /// <summary>Recomputes scale, pan limits (clamping <see cref="Pan"/>) and the destination rectangle.</summary>
        public void Layout()
        {
            if (!_dirty) return;
            _dirty = false;
            var (bx, by) = ModeScale();
            _sx = (float)(bx * _zoom); _sy = (float)(by * _zoom);
            float dw = _image.Width * _sx, dh = _image.Height * _sy, vw = _viewport.Width, vh = _viewport.Height;
            // natural origin: centred (None is handled by the box itself - here it behaves like CenterImage)
            float ox = (vw - dw) / 2, oy = (vh - dh) / 2;
            Range(dw, vw, out float minX, out float maxX); Range(dh, vh, out float minY, out float maxY);
            _panMin = new PointF(minX - ox, minY - oy); _panMax = new PointF(maxX - ox, maxY - oy);
            _pan = new PointF(Math.Clamp(_pan.X, _panMin.X, _panMax.X), Math.Clamp(_pan.Y, _panMin.Y, _panMax.Y));
            _dest = new RectangleF(ox + _pan.X, oy + _pan.Y, dw, dh);
        }
        // allowed range of the image origin along one axis (d = image extent, v = viewport extent)
        void Range(float d, float v, out float min, out float max)
        {
            float centre = (v - d) / 2;
            switch (_panMode)
            {
                case SpritePanMode.None: min = max = centre; return;
                case SpritePanMode.Scroll:
                    if (d <= v) { min = max = centre; return; }
                    min = v - d; max = 0; return;
                default:
                {
                    // classic range, widened so that up to Overscroll of the image may leave the viewport
                    float keep = Math.Max(1f, d * (1 - _overscroll));          // pixels that must stay inside
                    float cMin = d <= v ? centre : v - d, cMax = d <= v ? centre : 0;
                    min = Math.Min(cMin, keep - d); max = Math.Max(cMax, v - keep);
                    return;
                }
            }
        }

        /// <summary>
        /// Decides which scroll bars a client area of <paramref name="client"/> needs for the current image / zoom / pan mode
        /// (a bar of <paramref name="barWidth"/> takes room, which may make the other axis need one too). Deterministic for a
        /// given state and leaves <see cref="ViewportSize"/> at the size that decision gives - so a layout that applied it
        /// never asks for another one (no visibility ping-pong).
        /// </summary>
        public (bool h, bool v) DecideScrollBars(Size client, int barWidth)
        {
            bool needH = false, needV = false, stable = false;
            for (int pass = 0; pass < 4 && !stable; pass++)
            {
                ViewportSize = ViewportFor(client, barWidth, needH, needV);
                bool h = HorizontalScroll.Scrollable, v = VerticalScroll.Scrollable;
                stable = h == needH && v == needV; needH = h; needV = v;
            }
            if (!stable) needH = needV = true;
            ViewportSize = ViewportFor(client, barWidth, needH, needV);
            return (needH, needV);
        }
        public static Size ViewportFor(Size client, int barWidth, bool h, bool v) => new Size(Math.Max(1, client.Width - (v ? barWidth : 0)), Math.Max(1, client.Height - (h ? barWidth : 0)));

        // ------------------------------------------------------------------ mapping
        /// <summary>Image coordinates of a client point (fractional; extrapolated outside the image).</summary>
        public PointF ToImage(PointF client) { Layout(); return new PointF((client.X - _dest.X) / _sx, (client.Y - _dest.Y) / _sy); }
        public PointF ToImage(Point client) => ToImage(new PointF(client.X + 0.5f, client.Y + 0.5f));
        /// <summary>Client coordinates of an image point.</summary>
        public PointF ToClient(PointF image) { Layout(); return new PointF(_dest.X + image.X * _sx, _dest.Y + image.Y * _sy); }
        /// <summary>Client rectangle covered by an image pixel.</summary>
        public RectangleF PixelRect(int x, int y) { Layout(); return new RectangleF(_dest.X + x * _sx, _dest.Y + y * _sy, _sx, _sy); }
        /// <summary>Maps a client point (the centre of that client pixel) to the image and says whether it hits it.</summary>
        public ImagePoint Hit(Point client)
        {
            var p = ToImage(client);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X < _image.Width && p.Y < _image.Height;
            return new ImagePoint(inside, p.X, p.Y);
        }

        /// <summary>
        /// A stand-alone mapper for "offline" questions: which image pixel would a client point hit if a control of
        /// <paramref name="viewport"/> pixels showed an image of <paramref name="image"/> pixels with this size mode / zoom / pan -
        /// without a SpriteBox, without painting anything. Same maths as the control (this IS the control's geometry class).
        /// <code>
        /// var v = SpriteView.For(new Size(800, 600), new Size(1024, 1024), SpriteSizeMode.Zoom);
        /// var p = v.Hit(new Point(790, 300));            // p.Inside false, p.X / p.Y extrapolated beyond the image
        /// var q = v.ToImage(new PointF(10, 10));         // fractional image coordinates
        /// </code>
        /// For an existing control use <c>box.View.Clone()</c> (a snapshot of its current geometry) or <c>box.Geometry(...)</c>.
        /// </summary>
        public static SpriteView For(Size viewport, Size image, SpriteSizeMode mode, double zoom = 1, PointF pan = default, SpritePanMode panMode = SpritePanMode.Free, float overscroll = 0.9f)
        {
            var v = new SpriteView { ViewportSize = viewport, ImageSize = image, Mode = mode, PanMode = panMode, Overscroll = overscroll };
            v.Zoom = zoom; v.Pan = pan;
            return v;
        }
        /// <summary>An independent copy of this geometry (same image / viewport / mode / zoom / pan); changing it does not touch the original.</summary>
        public SpriteView Clone()
        {
            var v = new SpriteView { ViewportSize = _viewport, ImageSize = _image, Mode = _mode, PanMode = _panMode, Overscroll = _overscroll, MinZoom = _minZoom, MaxZoom = _maxZoom };
            v.Zoom = _zoom; v.Pan = _pan;
            return v;
        }
        /// <summary>
        /// The image pixel under a client point WITHOUT the inside test: outside the image the coordinates simply continue
        /// (negative or beyond the size), which lets you aim at something off-picture. Floor of <see cref="ToImage(Point)"/>.
        /// </summary>
        public Point PixelAt(Point client) { var p = ToImage(client); return new Point((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)); }

        // ------------------------------------------------------------------ zoom / scroll helpers
        /// <summary>Sets the zoom so that the image point under <paramref name="clientAnchor"/> stays under it.</summary>
        public void ZoomAt(double zoom, PointF clientAnchor)
        {
            Layout();
            var img = ToImage(clientAnchor);
            var (bx, by) = ModeScale();
            Zoom = zoom;
            float sx = (float)(bx * _zoom), sy = (float)(by * _zoom);
            float dw = _image.Width * sx, dh = _image.Height * sy;
            float ox = (_viewport.Width - dw) / 2, oy = (_viewport.Height - dh) / 2;
            // wanted origin: anchor - img * s ; pan = origin - natural origin
            _pan = new PointF(clientAnchor.X - img.X * sx - ox, clientAnchor.Y - img.Y * sy - oy);
            _dirty = true; Layout();
        }
        /// <summary>Sets the zoom keeping the viewport centre fixed.</summary>
        public void ZoomAtCentre(double zoom) => ZoomAt(zoom, new PointF(_viewport.Width / 2f, _viewport.Height / 2f));
        /// <summary>Moves the image by a client-pixel delta (clamped). Returns the delta actually applied.</summary>
        public PointF PanBy(float dx, float dy)
        {
            Layout();
            var before = _pan;
            Pan = new PointF(_pan.X + dx, _pan.Y + dy); Layout();
            return new PointF(_pan.X - before.X, _pan.Y - before.Y);
        }

        /// <summary>Scroll-bar model for one axis: Value 0..(Max - Page); Value 0 = image pushed to its top/left limit position.</summary>
        public readonly struct ScrollInfo
        {
            public readonly double Maximum, Page, Value;
            public ScrollInfo(double max, double page, double value) { Maximum = max; Page = page; Value = value; }
            /// <summary>False when there is nothing to scroll.</summary>
            public bool Scrollable => Maximum > Page + 0.5;
        }
        public ScrollInfo HorizontalScroll { get { Layout(); return new ScrollInfo(_panMax.X - _panMin.X + _viewport.Width, _viewport.Width, _panMax.X - _pan.X); } }
        public ScrollInfo VerticalScroll { get { Layout(); return new ScrollInfo(_panMax.Y - _panMin.Y + _viewport.Height, _viewport.Height, _panMax.Y - _pan.Y); } }
        /// <summary>Applies a scroll-bar value (see <see cref="ScrollInfo"/>) to one axis.</summary>
        public void SetScroll(bool horizontal, double value)
        {
            Layout();
            if (horizontal) Pan = new PointF((float)(_panMax.X - value), _pan.Y); else Pan = new PointF(_pan.X, (float)(_panMax.Y - value));
            Layout();
        }

        // ------------------------------------------------------------------ inertia (kinetic pan)
        PointF _velocity;                                    // client px per ms
        /// <summary>Friction of the kinetic pan: fraction of the speed lost per 100 ms (0.7 = fairly quick stop).</summary>
        public float Friction { get; set; } = 0.55f;
        /// <summary>Slowest release speed (client px per ms) that still starts a slide; slower releases just stop. Default 0.03 = 30 px/s.</summary>
        public float FlingThreshold { get; set; } = 0.03f;
        /// <summary>Multiplier on the release speed: above 1 the image slides further (and a gentler flick is enough), below 1 it is tamer. Default 1.3.</summary>
        public float FlingGain { get; set; } = 1.3f;
        /// <summary>Fastest slide (client px per ms) a flick can start. Default 5.</summary>
        public float MaxFlingSpeed { get; set; } = 5f;
        /// <summary>True while a flung image is still sliding.</summary>
        public bool IsSliding => Math.Abs(_velocity.X) > 0.01f || Math.Abs(_velocity.Y) > 0.01f;
        /// <summary>Starts sliding with the given release speed (client px per ms) times <see cref="FlingGain"/>; below <see cref="FlingThreshold"/> nothing happens.</summary>
        public void Fling(PointF velocityPxPerMs)
        {
            float v = MathF.Sqrt(velocityPxPerMs.X * velocityPxPerMs.X + velocityPxPerMs.Y * velocityPxPerMs.Y);
            if (v < FlingThreshold || v <= 0) { _velocity = PointF.Empty; return; }
            float g = Math.Max(0.01f, FlingGain); float vv = v * g;
            if (vv > MaxFlingSpeed) g = MaxFlingSpeed / v;                   // never faster than MaxFlingSpeed (a wild flick)
            _velocity = new PointF(velocityPxPerMs.X * g, velocityPxPerMs.Y * g);
        }
        public void StopSliding() => _velocity = PointF.Empty;
        /// <summary>Advances the slide by <paramref name="dtMs"/>; returns true when the image moved. Hitting a pan limit stops that axis.</summary>
        public bool Step(double dtMs)
        {
            if (!IsSliding) return false;
            dtMs = Math.Clamp(dtMs, 0, 100);
            float keep = MathF.Pow(1 - Friction, (float)(dtMs / 100));
            float dx = (float)(_velocity.X * dtMs), dy = (float)(_velocity.Y * dtMs);
            var applied = PanBy(dx, dy);
            _velocity = new PointF(_velocity.X * keep, _velocity.Y * keep);
            if (Math.Abs(applied.X - dx) > 0.01f) _velocity.X = 0;
            if (Math.Abs(applied.Y - dy) > 0.01f) _velocity.Y = 0;
            if (!IsSliding) _velocity = PointF.Empty;
            return Math.Abs(applied.X) > 0.001f || Math.Abs(applied.Y) > 0.001f;
        }
    }
}
