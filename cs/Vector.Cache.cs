using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------------------------------------------------------
    // VectorSprite: a VectorImage that behaves like a bitmap between transform changes.
    //
    // Rasterising a vector picture costs flattening + polygon fills for every shape, every frame - a few hundred shapes at
    // 60 fps is where a plain img.Draw(...) starts to hurt. VectorSprite keeps the last raster: the image is rendered ONCE
    // into a premultiplied sprite at the current scale / rotation / skew (the "linear part" of the matrix) and every frame
    // that only moves it (translation), or asks for the same transform again, is a single AlphaOver blit. The raster is
    // rebuilt only when
    //   - the scale / rotation changes beyond Tolerance (default 1/512 of a unit - invisible),
    //   - the image content changes (VectorImage.Version is bumped by Add / Transform / Flatten / Crop / Prune; call
    //     img.Touch() after editing shapes directly),
    //   - the render options change (AA, opacity, strokes / fills, forced colour) - set them through the properties here.
    //
    //     var vs = new VectorSprite(VectorImage.Load("logo.svg"));
    //     vs.Draw(canvas, x, y, scale, scale, angleDeg);        // frame 1: rasterise + blit; frames 2..n: blit only
    //
    // Sub-pixel positions: the raster is aligned to whole pixels, so a slowly moving picture steps by pixels like any sprite
    // (set SubPixel = true to re-rasterise for fractional offsets - smoother, back to full cost while moving).
    // Memory: one ARGB sprite of the rotated bounding box (a 1000 x 1000 picture = 4 MB). MaxPixels caps it: above the cap
    // the picture is drawn directly (no cache) so a huge zoom never allocates a giant bitmap.
    // ------------------------------------------------------------------------------------------------------------------------

    /// <summary>A <see cref="VectorImage"/> with a cached raster: costs a bitmap blit per frame until the scale / rotation or the content changes.</summary>
    internal sealed class VectorSprite : IDisposable
    {
        public VectorImage Image { get; private set; }
        Sprite? _raster; Matrix3x2 _linear; Vector2 _origin; int _version = -1; bool _valid;
        VectorRenderOptions _opt = new VectorRenderOptions();
        /// <summary>How far the linear part of the matrix may drift before a re-raster (default 1/512).</summary>
        public float Tolerance = 1f / 512;
        /// <summary>Re-rasterise for fractional positions (smooth sub-pixel motion at full cost) instead of snapping the cached raster to whole pixels.</summary>
        public bool SubPixel = false;
        /// <summary>Largest raster kept (pixels); bigger transforms draw directly. Default 16 M (64 MB).</summary>
        public long MaxPixels = 16L * 1024 * 1024;
        /// <summary>Statistics: rasterisations so far and blits served from the cache.</summary>
        public int Rasterizations, CacheHits;
        /// <summary>Diagnostics for animated pictures: one line per z segment (LAYER = cached raster + blit stats, DYN = live).</summary>
        public string LayerInfo
        {
            get
            {
                if (_segs == null) return "(no plan)";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < _segs.Count; i++)
                {
                    var sg = _segs[i];
                    sb.Append(i).Append(':').Append(sg.Dyn ? "DYN" : "LAYER").Append(" n=").Append(sg.Sub!.Shapes.Count);
                    if (sg.Vs != null) sb.Append(" rast=").Append(sg.Vs.Rasterizations).Append(" hit=").Append(sg.Vs.CacheHits).Append(" px=").Append(sg.Vs.Raster == null ? "-" : (sg.Vs.Raster.Width + "x" + sg.Vs.Raster.Height));
                    sb.Append(" | ");
                }
                return sb.ToString();
            }
        }
        /// <summary>One-line compositor statistics for the note line / diagnostics.</summary>
        public string LayerSummary
        {
            get
            {
                if (_segs == null || Image.Tracks.Count == 0) return "compositor: n/a (static)";
                int layers = 0, dyn = 0, live = 0, rasts = 0, hits = 0;
                foreach (var sg in _segs)
                {
                    if (sg.Dyn) { dyn++; live += sg.Sub!.Shapes.Count; }
                    else { layers++; if (sg.Vs != null) { rasts += sg.Vs.Rasterizations; hits += sg.Vs.CacheHits; } }
                }
                return $"compositor: {layers} layers, {dyn} live runs ({live} shapes), {rasts} rasters, {hits} blits";
            }
        }
        /// <summary>Raster currently cached (null before the first draw / after a content change).</summary>
        public Sprite? Raster => _valid ? _raster : null;
        /// <summary>Filter for the blit when the cached raster is drawn scaled (Nearest = exact copy at the cached transform).</summary>
        public bool Premultiplied => true;

        public VectorSprite(VectorImage image) { Image = image; }
        /// <summary>Replaces the image (the cache is dropped).</summary>
        public void SetImage(VectorImage image) { Image = image; Invalidate(); }
        /// <summary>Forces a re-raster on the next draw.</summary>
        public void Invalidate() { _valid = false; DropLayers(); }

        // ---- render options (each setter invalidates the cache)
        public bool AA { get => _opt.AA; set { if (_opt.AA != value) { _opt.AA = value; Invalidate(); } } }
        public float Opacity { get => _opt.Opacity; set { if (_opt.Opacity != value) { _opt.Opacity = value; Invalidate(); } } }
        public bool Strokes { get => _opt.Strokes; set { if (_opt.Strokes != value) { _opt.Strokes = value; Invalidate(); } } }
        public bool Fills { get => _opt.Fills; set { if (_opt.Fills != value) { _opt.Fills = value; Invalidate(); } } }
        public int? ForceColor { get => _opt.ForceColor; set { if (_opt.ForceColor != value) { _opt.ForceColor = value; Invalidate(); } } }
        public bool IgnoreClips { get => _opt.IgnoreClips; set { if (_opt.IgnoreClips != value) { _opt.IgnoreClips = value; Invalidate(); } } }
        public float? CurveTolerance { get => _opt.Tolerance; set { if (_opt.Tolerance != value) { _opt.Tolerance = value; Invalidate(); } } }
        /// <summary>The options object used for rasterisation (copy your own settings in, then <see cref="Invalidate"/>).</summary>
        public VectorRenderOptions Options { get => _opt; set { _opt = value ?? new VectorRenderOptions(); Invalidate(); } }

        /// <summary>Same placement as <see cref="VectorImage.Draw(Sprite, float, float, float, float, float, VectorRenderOptions?, float, float)"/>: view-box pivot at (x, y), scale, angle (degrees).</summary>
        public void Draw(Sprite dst, float x, float y, float scaleX = 1f, float scaleY = 1f, float angleDeg = 0f, float pivotX = 0.5f, float pivotY = 0.5f, SR2D.Op op = SR2D.Op.AlphaOver, int blendFactor = 128)
            => Draw(dst, Image.PlaceMatrix(x, y, scaleX, scaleY, angleDeg, pivotX, pivotY), op, blendFactor);
        /// <summary>Fits the image into a rectangle (the raster is reused while the rectangle keeps its size).</summary>
        public void DrawFit(Sprite dst, RectangleF into, bool keepAspect = true, SR2D.Op op = SR2D.Op.AlphaOver) => Draw(dst, Image.FitMatrix(into, keepAspect), op);
        /// <summary>Draws with an arbitrary image -> destination matrix. Translation is free; a new linear part re-rasterises.</summary>
        public void Draw(Sprite dst, Matrix3x2 m, SR2D.Op op = SR2D.Op.AlphaOver, int blendFactor = 128)
        {
            if (Image.Tracks.Count > 0) { DrawAnimated(dst, m, op, blendFactor); return; }
            var lin = new Matrix3x2(m.M11, m.M12, m.M21, m.M22, 0, 0);
            var box = VectorRender.TransformRect(Image.Bounds(), lin);
            if (box.IsEmpty) return;
            long px = (long)MathF.Ceiling(box.Width + 2) * (long)MathF.Ceiling(box.Height + 2);
            if (px > MaxPixels || px <= 0)
            {   // too big to cache: direct render
                _valid = false; Image.Draw(dst, m, _opt); return;
            }
            // the destination point of the image origin; the raster is aligned so that its pixel grid matches whole destination pixels
            float tx = m.M31, ty = m.M32;
            float fx = tx - MathF.Floor(tx), fy = ty - MathF.Floor(ty);       // fractional part of the translation
            bool same = _valid && _raster != null && _version == Image.Version && Same(_linear, lin) && (!SubPixel || (MathF.Abs(_origin.X - fx) < 1e-3f && MathF.Abs(_origin.Y - fy) < 1e-3f));
            if (!same)
            {
                // raster covers box shifted by the fractional offset when SubPixel, else by 0
                float ox = SubPixel ? fx : 0, oy = SubPixel ? fy : 0;
                int left = (int)MathF.Floor(box.Left + ox) - 1, top = (int)MathF.Floor(box.Top + oy) - 1;
                int w = (int)MathF.Ceiling(box.Right + ox) + 2 - left, h = (int)MathF.Ceiling(box.Bottom + oy) + 2 - top;
                if (_raster == null || _raster.Width != w || _raster.Height != h) { _raster?.Dispose(); _raster = new Sprite(w, h, SR2D.Op.AlphaOver); }
                _raster.SetLockRect(); _raster.ClearBuffer(0);
                Image.Draw(_raster, lin * Matrix3x2.CreateTranslation(ox - left, oy - top), _opt);
                _linear = lin; _origin = new Vector2(ox, oy); _rasterLeft = left; _rasterTop = top; _version = Image.Version; _valid = true; Rasterizations++;
            }
            else CacheHits++;
            int dx = (int)MathF.Floor(tx + (SubPixel ? 0 : 0.5f)) + _rasterLeft, dy = (int)MathF.Floor(ty + (SubPixel ? 0 : 0.5f)) + _rasterTop;
            if (op == SR2D.Op.AlphaOver || op == SR2D.Op.DefaultOp) dst.Draw(_raster!, dx, dy, SR2D.Op.AlphaOver);
            else if (op == SR2D.Op.Blend) dst.Blend(_raster!, dx, dy, blendFactor);
            else dst.Draw(_raster!, dx, dy, op);
        }
        int _rasterLeft, _rasterTop;
        bool Same(Matrix3x2 a, Matrix3x2 b) => MathF.Abs(a.M11 - b.M11) <= Tolerance && MathF.Abs(a.M12 - b.M12) <= Tolerance && MathF.Abs(a.M21 - b.M21) <= Tolerance && MathF.Abs(a.M22 - b.M22) <= Tolerance;

        // ---- animated pictures: the layer compositor ------------------------------------------------------------------
        // An animated picture is split, in z order, into segments. Shapes whose whole animation chain only translates
        // (camera dollies, parallax planes, plain static content) become rasters rendered once per zoom level and
        // blitted with the chain's current translation; everything else (rotation, scale, morphs, opacity, dashes,
        // mask re-derivation, use sites) is drawn live. A scene that is mostly dolly + parallax therefore costs a few
        // blits plus its genuinely animated shapes per frame instead of re-rasterising every shape every frame.
        sealed class Seg { public VectorImage? Sub; public VectorSprite? Vs; public VectorShape? Rep; public bool Dyn; }
        List<Seg>? _segs; int _planVersion = -1;
        readonly System.Collections.Generic.List<(VectorShape, Matrix3x2, VectorClip?)> _swap = new System.Collections.Generic.List<(VectorShape, Matrix3x2, VectorClip?)>();

        void DropLayers()
        {
            if (_segs == null) return;
            foreach (var sg in _segs) sg.Vs?.Dispose();
            _segs = null; _planVersion = -1;
        }

        void EnsurePlan()
        {
            if (_segs != null && _planVersion == Image.Version) return;
            DropLayers();
            _planVersion = Image.Version;
            _segs = new List<Seg>();
            var byShape = Image.ShapeTracks();
            var bound = new HashSet<VectorShape>();
            foreach (var b in Image.MaskBinds) bound.Add(b.Shape);
            var shapes = Image.Shapes;
            // layer key of a shape: the moving tracks driving it (creation order); null = draw live. The animated delta
            // over the static chain must further match across the run (checked below) or the run falls back to live.
            string? KeyOf(VectorShape s)
            {
                if (bound.Contains(s) || Image.UseSites.ContainsKey(s)) return null;
                if (!byShape.TryGetValue(s, out var trs)) return "";
                var key = "";
                for (int k = 0; k < trs.Count; k++)
                {
                    if (!VectorImage.TrackIsMover(trs[k])) return null;   // morph / opacity / dash: per-frame paint
                    key += Image.Tracks.IndexOf(trs[k]).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
                }
                return key;
            }
            bool SameDelta(VectorShape a, VectorShape b)
            {
                var da = Image.ChainDeltaOf(a); var db = Image.ChainDeltaOf(b);
                return MathF.Abs(da.M11 - db.M11) < 1e-3f && MathF.Abs(da.M12 - db.M12) < 1e-3f && MathF.Abs(da.M21 - db.M21) < 1e-3f && MathF.Abs(da.M22 - db.M22) < 1e-3f && MathF.Abs(da.M31 - db.M31) < 1e-2f && MathF.Abs(da.M32 - db.M32) < 1e-2f;
            }
            int i = 0;
            while (i < shapes.Count)
            {
                var k = KeyOf(shapes[i]);
                int j = i + 1;
                if (k == null) { while (j < shapes.Count && KeyOf(shapes[j]) == null) j++; }
                else { while (j < shapes.Count && KeyOf(shapes[j]) == k && SameDelta(shapes[i], shapes[j])) j++; }
                var sub = new VectorImage { ViewBox = Image.ViewBox, Width = Image.Width, Height = Image.Height, Format = Image.Format };
                for (int x = i; x < j; x++) sub.Shapes.Add(shapes[x]);
                _segs.Add(new Seg { Sub = sub, Dyn = k == null, Rep = k == null ? null : shapes[i], Vs = k == null ? null : new VectorSprite(sub) { Options = _opt, MaxPixels = MaxPixels, Tolerance = Tolerance, SubPixel = SubPixel } });
                i = j;
            }
        }

        void DrawAnimated(Sprite dst, Matrix3x2 m, SR2D.Op op, int blendFactor)
        {
            EnsurePlan();
            var savedLock = dst.LockRect;
            bool clipped = false;
            if (Image.ClipViewport)
            {   // keep everything inside the container, like the window path of the direct renderer
                var dr = VectorRender.TransformRect(Image.ViewBox, m);
                var lr = Rectangle.Intersect(savedLock, Rectangle.FromLTRB((int)MathF.Floor(dr.Left), (int)MathF.Floor(dr.Top), (int)MathF.Ceiling(dr.Right), (int)MathF.Ceiling(dr.Bottom)));
                if (lr.Width <= 0 || lr.Height <= 0) return;
                dst.SetLockRect(lr);
                clipped = true;
            }
            try
            {
                foreach (var sg in _segs!)
                {
                    if (sg.Dyn || sg.Vs == null || sg.Sub == null) { sg.Sub!.Draw(dst, m, _opt); continue; }
                    // the raster bakes the STATIC placement (and the static form of the clips); the animated delta
                    // arrives with the blit, so content and clip windows move and breathe together; the sub-sprite
                    // re-rasterises by itself when the delta's linear part drifts more than Tolerance (slow dollies
                    // re-raster a couple of times a second, fast local motions every frame)
                    var d = sg.Rep == null ? Matrix3x2.Identity : Image.ChainDeltaOf(sg.Rep);
                    _swap.Clear();
                    foreach (var s in sg.Sub.Shapes)
                    {
                        _swap.Add((s, s.Transform, s.ClipRoots != null ? s.Clip : null));
                        s.Transform = s.BaseTransform;
                        if (s.ClipRoots != null)
                        {
                            var c = new VectorClip();
                            foreach (var (root, owner) in s.ClipRoots)
                                if (Matrix3x2.Invert(s.BaseTransform, out var ib))
                                    foreach (var pr in root.Transformed(ib).Paths) c.Paths.Add(pr);
                            s.Clip = c;
                        }
                    }
                    try { sg.Vs.Draw(dst, d * m, op, blendFactor); }
                    finally { foreach (var (s, tm, cl) in _swap) { s.Transform = tm; if (cl != null) s.Clip = cl; } }
                }
            }
            finally { if (clipped) dst.SetLockRect(savedLock); }
        }
        /// <summary>Destination rectangle the cached raster occupies for a matrix (whole pixels), or the transformed bounds when nothing is cached.</summary>
        public Rectangle ScreenRect(Matrix3x2 m)
        {
            if (_valid && _raster != null) return new Rectangle((int)MathF.Floor(m.M31 + 0.5f) + _rasterLeft, (int)MathF.Floor(m.M32 + 0.5f) + _rasterTop, _raster.Width, _raster.Height);
            var b = VectorRender.TransformRect(Image.Bounds(), m); return Rectangle.Round(b);
        }
        public void Dispose() { _raster?.Dispose(); _raster = null; _valid = false; DropLayers(); }
    }

    internal sealed partial class VectorImage
    {
        /// <summary>Content version: bumped by the editing calls; <see cref="VectorSprite"/> re-rasterises when it changes. Call <see cref="Touch"/> after editing shapes directly.</summary>
        public int Version { get; private set; }
        /// <summary>Marks the content as changed (caches re-render).</summary>
        public void Touch() => Version++;
        /// <summary>The picture's cached raster wrapper (created on first use): img.Cached.Draw(canvas, x, y, scale, scale, angle).</summary>
        public VectorSprite Cached => _cached ??= new VectorSprite(this);
        VectorSprite? _cached;
    }
}
