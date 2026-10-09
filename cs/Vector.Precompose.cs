using System;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Threading;

namespace Sr2d64CSport
{
    // The third cache mode: static raster -> animated layer compositor -> precomposed bitmap loop.
    // A film is deliberately opt-in: it trades an up-front bake and bounded memory for no vector work during playback.
    // Frames are rendered by the direct renderer on a clone (not by the approximate layer compositor), so masks,
    // static clip windows around moving content, paint animations and rotated viewport clips keep their semantics.
    // The budget reduces spatial resolution, NEVER silently lowers the requested temporal resolution.
    internal sealed partial class VectorSprite
    {
        bool _precomposed;
        long _maxFilmPixels = 64L * 1024 * 1024;
        double _filmFrameRate;

        /// <summary>Opt-in bitmap-loop playback. The first draw bakes the loop synchronously; subsequent draws only blit.
        /// Turning this off releases the film immediately and returns to the layer compositor. Off by default.</summary>
        public bool Precomposed
        {
            get => _precomposed;
            set
            {
                if (_precomposed == value) return;
                _precomposed = value;
                if (value) DropLayers();                    // keep a film explicitly warmed by PrepareFilm before enabling
                else DropFilm();
            }
        }
        /// <summary>Maximum pixels retained across ALL film frames (4 bytes each). Default 64 M = 256 MiB.
        /// 0 uses <see cref="MaxPixels"/>. If the full-resolution loop does not fit, smaller bitmaps are baked at
        /// the same frame rate and enlarged during playback. Changing the budget releases the old film.</summary>
        public long MaxFilmPixels
        {
            get => _maxFilmPixels;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                if (_maxFilmPixels == value) return;
                _maxFilmPixels = value; DropFilm();
            }
        }
        /// <summary>Requested bake frame rate. 0 (default) uses <see cref="VectorImage.FrameRate"/>.
        /// Samples are evenly spaced over Duration (ceil(Duration * rate) frames); the loop endpoint is not duplicated.</summary>
        public double FilmFrameRate
        {
            get => _filmFrameRate;
            set
            {
                if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                if (_filmFrameRate == value) return;
                _filmFrameRate = value; DropFilm();
            }
        }
        /// <summary>Number of baked frames currently retained (0 before baking / after release).</summary>
        public int FilmFrames { get; private set; }
        /// <summary>Actual evenly-spaced samples per second: FilmFrames / Duration.</summary>
        public double FilmFps { get; private set; }
        /// <summary>Spatial bake scale relative to the requested draw transform: 1 = full resolution; less = budget-limited.</summary>
        public float FilmScale { get; private set; }
        /// <summary>Raw bitmap pixels retained across all frames. Never exceeds MaxFilmPixels (or MaxPixels when 0).</summary>
        public long FilmPixels { get; private set; }
        /// <summary>Size of each stored bitmap, including its transparent edge padding.</summary>
        public Size FilmSize => _frames == null ? Size.Empty : _filmCell.Size;
        /// <summary>Completed loop bakes so far (not reset by invalidation).</summary>
        public int FilmBakes { get; private set; }
        /// <summary>Bitmap-frame draws served so far (not reset by invalidation).</summary>
        public int FilmBlits { get; private set; }
        /// <summary>Time of the most recent preparation, including the bounds pass, in milliseconds.</summary>
        public double FilmBakeMs { get; private set; }
        /// <summary>Whether a complete film is allocated. No partial film is published during baking.</summary>
        public bool FilmActive => _frames != null;
        /// <summary>Film memory, resolution, timing and playback diagnostics (or the live-fallback reason).</summary>
        public string FilmStatus => _frames == null
            ? "film: " + (_filmReason.Length == 0 ? "not prepared" : _filmReason + " - live compositor")
            : $"film: {FilmFrames} frames @ {FilmFps:0.##} fps, {FilmSize.Width}x{FilmSize.Height} ({FilmScale:P0}), {FilmPixels * 4 / 1048576.0:0.#} MiB, bake {FilmBakeMs:0} ms, {FilmBlits} blits";

        Sprite[]? _frames;
        Rectangle _filmCell;
        Matrix3x2 _filmLinear;
        bool _filmPrepared, _filmClip;
        int _filmVersion = -1;
        RectangleF _filmViewBox;
        RectangleF? _filmVisible;
        double _filmDuration, _filmRate;
        long _filmBudget, _filmMaxCellPixels;
        VectorRenderOptions? _filmOptions;
        string _filmReason = "";
        const int FilmFrameLimit = 10000;       // prevent a hostile / day-long SVG from blocking preparation indefinitely

        void DropFilm()
        {
            if (_frames != null) foreach (var f in _frames) f.Dispose();
            _frames = null; _filmPrepared = false; _filmOptions = null; _filmReason = "";
            FilmFrames = 0; FilmFps = 0; FilmScale = 0; FilmPixels = 0; _filmCell = Rectangle.Empty;
        }

        static VectorRenderOptions FilmOptionsCopy(VectorRenderOptions o) => new VectorRenderOptions
        {
            AA = o.AA, Opacity = o.Opacity, Strokes = o.Strokes, Fills = o.Fills,
            ForceColor = o.ForceColor, IgnoreClips = o.IgnoreClips, MinStrokeWidth = o.MinStrokeWidth,
            Tolerance = o.Tolerance ?? Sprite.CurveTolerance, Premultiplied = true
        };
        bool SameFilmOptions() => _filmOptions != null
            && _filmOptions.AA == _opt.AA && _filmOptions.Opacity == _opt.Opacity
            && _filmOptions.Strokes == _opt.Strokes && _filmOptions.Fills == _opt.Fills
            && _filmOptions.ForceColor == _opt.ForceColor && _filmOptions.IgnoreClips == _opt.IgnoreClips
            && _filmOptions.MinStrokeWidth == _opt.MinStrokeWidth
            && _filmOptions.Tolerance == (_opt.Tolerance ?? Sprite.CurveTolerance);

        /// <summary>Explicitly prepares the bitmap loop for a draw matrix (translation is not baked).
        /// Optional warm-up at load time; Draw / DrawAt also prepare on demand. Returns false, with FilmStatus explaining
        /// why, when no useful film fits or the loop is invalid. Cancellation disposes every partial frame and rethrows.
        /// Preparation is synchronous; like other SR2D drawing calls, do not use this wrapper concurrently.</summary>
        public bool PrepareFilm(Matrix3x2 m, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lin = new Matrix3x2(m.M11, m.M12, m.M21, m.M22, 0, 0);
            double dur = Image.Duration;
            double rate = FilmFrameRate > 0 ? FilmFrameRate : Image.FrameRate;
            long budget = MaxFilmPixels > 0 ? MaxFilmPixels : MaxPixels;
            if (_filmPrepared && _filmVersion == Image.Version && Same(_filmLinear, lin)
                && _filmViewBox == Image.ViewBox && _filmVisible == Image.VisibleArea && _filmClip == Image.ClipViewport
                && _filmDuration == dur && _filmRate == rate && _filmBudget == budget
                && _filmMaxCellPixels == MaxPixels && SameFilmOptions()) return _frames != null;

            // Free old rasters before allocating the replacement: re-baking never needs two whole loops resident.
            DropFilm(); DropLayers();
            _filmPrepared = true; _filmVersion = Image.Version; _filmLinear = lin;
            _filmViewBox = Image.ViewBox; _filmVisible = Image.VisibleArea; _filmClip = Image.ClipViewport;
            _filmDuration = dur; _filmRate = rate; _filmBudget = budget; _filmMaxCellPixels = MaxPixels;
            _filmOptions = FilmOptionsCopy(_opt);
            var sw = Stopwatch.StartNew();
            Sprite[]? pending = null;
            try
            {
                if (Image.Tracks.Count == 0 || !double.IsFinite(dur) || dur <= 0 || !double.IsFinite(rate) || rate <= 0)
                { _filmReason = "no finite animation loop"; return false; }
                double count = Math.Ceiling(dur * rate - 1e-6);
                if (!double.IsFinite(count) || count > FilmFrameLimit)
                { _filmReason = "more than 10000 frames (choose a lower FilmFrameRate)"; return false; }
                int n = Math.Max(1, (int)count);
                long cellBudget = Math.Min(Math.Min(budget / n, MaxPixels), int.MaxValue / 4);
                if (cellBudget < 9) { _filmReason = "budget too small"; return false; }
                if (!float.IsFinite(lin.M11) || !float.IsFinite(lin.M12) || !float.IsFinite(lin.M21) || !float.IsFinite(lin.M22))
                { _filmReason = "non-finite draw matrix"; return false; }

                var src = Image.CloneAnimated();             // the caller's seek state / geometry is never changed by the bake
                RectangleF bounds;
                var window = Image.VisibleArea ?? (Image.ClipViewport ? Image.ViewBox : (RectangleF?)null);
                if (window.HasValue)
                    bounds = VectorRender.TransformRect(window.Value, lin);
                else
                {
                    // No window: union at the ACTUAL bake sample times, not at the caller's current seek. This catches
                    // content that appears later (opacity:0), path morphs and travel beyond the starting view box.
                    bounds = RectangleF.Empty;
                    for (int i = 0; i < n; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        src.SeekToTime(dur * i / n);
                        var b = VectorRender.TransformRect(src.Bounds(), lin);
                        if (b.Width > 0 && b.Height > 0) bounds = bounds.IsEmpty ? b : RectangleF.Union(bounds, b);
                    }
                }
                if (!FitFilmCell(bounds, cellBudget, out var cell, out float scale))
                { _filmReason = "empty or unrepresentable bounds"; return false; }
                var bakeOptions = FilmOptionsCopy(_opt);
                bakeOptions.MinStrokeWidth *= scale;
                bakeOptions.Tolerance *= scale;
                var bakeMatrix = lin * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(-cell.Left, -cell.Top);
                pending = new Sprite[n];
                for (int i = 0; i < n; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frame = new Sprite(cell.Width, cell.Height, SR2D.Op.AlphaOver);
                    pending[i] = frame;                       // ownership recorded BEFORE rendering, even if a draw throws
                    frame.ClearBuffer(0);
                    src.SeekToTime(dur * i / n);
                    src.Draw(frame, bakeMatrix, bakeOptions);
                }
                _frames = pending; pending = null;
                _filmCell = cell; FilmFrames = n; FilmFps = n / dur; FilmScale = scale;
                FilmPixels = (long)cell.Width * cell.Height * n;
                FilmBakes++; Rasterizations += n;
                return true;
            }
            catch (OutOfMemoryException)
            {
                _filmReason = "bitmap allocation failed";       // remember this key; do not retry the failed bake every tick
                return false;
            }
            catch
            {
                DropFilm();                                  // cancellation / other failure: the next explicit attempt may retry
                throw;
            }
            finally
            {
                if (pending != null) foreach (var frame in pending) frame?.Dispose();
                FilmBakeMs = sw.Elapsed.TotalMilliseconds;
            }
        }

        static bool FitFilmCell(RectangleF b, long budget, out Rectangle cell, out float scale)
        {
            cell = Rectangle.Empty; scale = 0;
            if (!float.IsFinite(b.Left) || !float.IsFinite(b.Top) || !float.IsFinite(b.Right) || !float.IsFinite(b.Bottom)
                || b.Width <= 0 || b.Height <= 0) return false;
            double q = Math.Min(1, Math.Sqrt(budget / (((double)b.Width + 2) * ((double)b.Height + 2))));
            for (int attempt = 0; attempt < 32; attempt++)
            {
                scale = (float)q;
                if (scale <= 0) return false;
                double left = Math.Floor((double)b.Left * scale) - 1, top = Math.Floor((double)b.Top * scale) - 1;
                double right = Math.Ceiling((double)b.Right * scale) + 1, bottom = Math.Ceiling((double)b.Bottom * scale) + 1;
                double width = right - left, height = bottom - top;
                if (left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue
                    || width > int.MaxValue || height > int.MaxValue) return false;
                long pixels = (long)width * (long)height;
                if (pixels <= budget)
                { cell = new Rectangle((int)left, (int)top, (int)width, (int)height); return true; }
                // Account for integer padding/rounding; the float scale used by the renderer is the one budgeted here.
                q *= Math.Sqrt((double)budget / pixels) * 0.99;
            }
            return false;
        }

        /// <summary>Time-based draw: in precomposed mode this selects a bitmap WITHOUT evaluating any vector tracks.
        /// In live mode (or fallback) it performs SeekToTime(time) and draws normally. Film time wraps over Duration.</summary>
        public void DrawAt(Sprite dst, double time, Matrix3x2 m, SR2D.Op op = SR2D.Op.AlphaOver, int blendFactor = 128)
        {
            if (Precomposed && Image.Tracks.Count > 0) DrawFilm(dst, m, time, op, blendFactor);
            else { Image.SeekToTime(time); Draw(dst, m, op, blendFactor); }
        }
        /// <summary>Time-based draw with the same placement, scale, degree angle and view-box pivot as Draw.</summary>
        public void DrawAt(Sprite dst, double time, float x, float y, float scaleX = 1f, float scaleY = 1f, float angleDeg = 0f,
            float pivotX = 0.5f, float pivotY = 0.5f, SR2D.Op op = SR2D.Op.AlphaOver, int blendFactor = 128)
            => DrawAt(dst, time, Image.PlaceMatrix(x, y, scaleX, scaleY, angleDeg, pivotX, pivotY), op, blendFactor);

        void DrawFilm(Sprite dst, Matrix3x2 m, double time, SR2D.Op op, int blendFactor)
        {
            if (!float.IsFinite(m.M31) || !float.IsFinite(m.M32)) return;
            if (!PrepareFilm(m))
            { Image.SeekToTime(time); DrawAnimated(dst, m, op, blendFactor); return; }
            double phase = double.IsFinite(time) ? time % _filmDuration : 0;
            if (phase < 0) phase += _filmDuration;
            int index = Math.Clamp((int)Math.Floor(phase * FilmFrames / _filmDuration + 1e-9), 0, FilmFrames - 1);
            var frame = _frames![index];
            float tx = SubPixel ? m.M31 : MathF.Floor(m.M31 + 0.5f), ty = SubPixel ? m.M32 : MathF.Floor(m.M32 + 0.5f);
            if (FilmScale == 1 && tx == MathF.Floor(tx) && ty == MathF.Floor(ty))
            {
                int dx = (int)tx + _filmCell.Left, dy = (int)ty + _filmCell.Top;
                if (op == SR2D.Op.Blend) dst.Blend(frame, dx, dy, blendFactor);
                else dst.Draw(frame, dx, dy, op == SR2D.Op.DefaultOp ? SR2D.Op.AlphaOver : op);
            }
            else
            {
                float inv = 1f / FilmScale;
                float left = tx + _filmCell.Left * inv, top = ty + _filmCell.Top * inv;
                float right = left + _filmCell.Width * inv, bottom = top + _filmCell.Height * inv;
                Span<PointF> quad = stackalloc PointF[4] { new PointF(left, top), new PointF(right, top), new PointF(right, bottom), new PointF(left, bottom) };
                Span<PointF> clip = stackalloc PointF[4];
                int clipCount = 0;
                var window = Image.VisibleArea ?? (Image.ClipViewport ? Image.ViewBox : (RectangleF?)null);
                if (window is RectangleF va)
                {
                    var placement = _filmLinear * Matrix3x2.CreateTranslation(tx, ty);
                    clip[0] = FilmPoint(va.Left, va.Top, placement); clip[1] = FilmPoint(va.Right, va.Top, placement);
                    clip[2] = FilmPoint(va.Right, va.Bottom, placement); clip[3] = FilmPoint(va.Left, va.Bottom, placement);
                    clipCount = 4;
                }
                dst.DrawQuad(frame, quad, clip[..clipCount], op, _opt.AA ? SR2D.Filter.Bilinear : SR2D.Filter.Nearest, blendFactor);
            }
            FilmBlits++; CacheHits++;
        }
        static PointF FilmPoint(float x, float y, Matrix3x2 m)
        { var p = Vector2.Transform(new Vector2(x, y), m); return new PointF(p.X, p.Y); }
        Rectangle FilmScreenRect(Matrix3x2 m)
        {
            float tx = SubPixel ? m.M31 : MathF.Floor(m.M31 + 0.5f), ty = SubPixel ? m.M32 : MathF.Floor(m.M32 + 0.5f);
            float inv = 1f / FilmScale;
            return Rectangle.FromLTRB((int)MathF.Floor(tx + _filmCell.Left * inv), (int)MathF.Floor(ty + _filmCell.Top * inv),
                (int)MathF.Ceiling(tx + _filmCell.Right * inv), (int)MathF.Ceiling(ty + _filmCell.Bottom * inv));
        }
    }
}
