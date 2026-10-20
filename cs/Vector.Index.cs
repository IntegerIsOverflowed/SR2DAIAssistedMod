// Vector.Index.cs - a grid index over the shapes of a VectorImage, so a view draws only what is inside it.
// Used by big imports (a whole map city is tens of thousands of shapes): DrawVisible(...) finds the shapes that
// touch the destination through the grid, keeps their z-order, and skips shapes that would be smaller than a
// minimum size on screen (level of detail). Shapes are not copied; the subset is a view of the same list.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Sr2d64CSport
{
    internal sealed partial class VectorImage
    {
        RectangleF[] _indexBounds = Array.Empty<RectangleF>();
        List<int>[] _indexCells = Array.Empty<List<int>>();
        int _indexCols, _indexRows, _indexedCount = -1;
        float _indexX0, _indexY0, _indexCell = 256f;
        float[] _indexSize = Array.Empty<float>();      // larger side of each shape's own path (no stroke padding)

        /// <summary>True for a map loaded from tiles (MBTiles): only then does <see cref="MapLayerMinPx"/> apply.</summary>
        public bool IsMap;
        /// <summary>
        /// Map level of detail, for maps only (<see cref="IsMap"/>): a shape whose <see cref="VectorShape.Layer"/> is a key here
        /// is left out of <see cref="DrawVisible"/> while its larger side is smaller than the value, in device pixels.
        /// The importer sets "building" to 2.5 px: houses vanish when the map is zoomed out and show again when it is closer.
        /// </summary>
        public readonly Dictionary<string, float> MapLayerMinPx = new Dictionary<string, float>();

        /// <summary>True when the grid matches the shape list (so DrawVisible may be called from several threads).</summary>
        internal bool IndexCurrent => _indexedCount == Shapes.Count;

        /// <summary>Builds (or rebuilds) the grid over the shapes' bounds (image units). Call after the shape list changes.</summary>
        public void BuildIndex(float cell = 256f)
        {
            int n = Shapes.Count;
            _indexBounds = new RectangleF[n];
            _indexSize = new float[n];
            _indexCell = Math.Max(16f, cell);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var s = Shapes[i];
                if (s.Path.IsEmpty) { _indexBounds[i] = RectangleF.Empty; continue; }
                var b = s.Path.ControlBounds();
                _indexSize[i] = Math.Max(b.Width, b.Height);
                float pad = (s.Stroke != null ? s.StrokeWidth * 0.5f : 0f) + 1f;      // a stroke reaches past its path
                b = RectangleF.Inflate(b, pad, pad);
                _indexBounds[i] = b;
                minX = Math.Min(minX, b.Left); minY = Math.Min(minY, b.Top);
                maxX = Math.Max(maxX, b.Right); maxY = Math.Max(maxY, b.Bottom);
            }
            if (minX > maxX) { minX = minY = maxX = maxY = 0; }
            _indexX0 = minX; _indexY0 = minY;
            _indexCols = Math.Max(1, (int)Math.Ceiling((maxX - minX) / _indexCell) + 1);
            _indexRows = Math.Max(1, (int)Math.Ceiling((maxY - minY) / _indexCell) + 1);
            _indexCells = new List<int>[_indexCols * _indexRows];
            for (int i = 0; i < n; i++)
            {
                var b = _indexBounds[i];
                if (b.IsEmpty) continue;
                int c0 = Math.Clamp((int)Math.Floor((b.Left - _indexX0) / _indexCell), 0, _indexCols - 1);
                int c1 = Math.Clamp((int)Math.Floor((b.Right - _indexX0) / _indexCell), 0, _indexCols - 1);
                int r0 = Math.Clamp((int)Math.Floor((b.Top - _indexY0) / _indexCell), 0, _indexRows - 1);
                int r1 = Math.Clamp((int)Math.Floor((b.Bottom - _indexY0) / _indexCell), 0, _indexRows - 1);
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                        (_indexCells[r * _indexCols + c] ??= new List<int>()).Add(i);
            }
            _indexedCount = n;
        }

        /// <summary>Shape indices (ascending = drawing order) whose bounds touch the rectangle (image units).</summary>
        public List<int> ShapesIn(RectangleF r)
        {
            if (_indexedCount != Shapes.Count) BuildIndex(_indexCell);
            // no shared state here: several threads may query one picture (VectorTileCache draws tiles in parallel)
            var result = new List<int>();
            int c0 = Math.Clamp((int)Math.Floor((r.Left - _indexX0) / _indexCell), 0, _indexCols - 1);
            int c1 = Math.Clamp((int)Math.Floor((r.Right - _indexX0) / _indexCell), 0, _indexCols - 1);
            int r0 = Math.Clamp((int)Math.Floor((r.Top - _indexY0) / _indexCell), 0, _indexRows - 1);
            int r1 = Math.Clamp((int)Math.Floor((r.Bottom - _indexY0) / _indexCell), 0, _indexRows - 1);
            for (int row = r0; row <= r1; row++)
                for (int col = c0; col <= c1; col++)
                {
                    var cell = _indexCells[row * _indexCols + col];
                    if (cell == null) continue;
                    foreach (int i in cell)
                        if (_indexBounds[i].IntersectsWith(r)) result.Add(i);   // a shape in several cells: removed below
                }
            if (result.Count > 1)
            {
                result.Sort();
                int w = 1;
                for (int k = 1; k < result.Count; k++) if (result[k] != result[w - 1]) result[w++] = result[k];
                result.RemoveRange(w, result.Count - w);
            }
            return result;
        }

        /// <summary>
        /// Draws only the shapes that touch the destination (image -> destination matrix <paramref name="m"/>), and skips
        /// shapes whose bounds are smaller than <paramref name="minScreenPx"/> on screen. The output is the same as
        /// <see cref="Draw(Sprite, Matrix3x2, VectorRenderOptions?)"/> for the visible part.
        /// </summary>
        public void DrawVisible(Sprite dst, Matrix3x2 m, VectorRenderOptions? o = null, float minScreenPx = 0f)
        {
            if (!Matrix3x2.Invert(m, out var inv)) { Draw(dst, m, o); return; }
            var c = new[] { new Vector2(0, 0), new Vector2(dst.Width, 0), new Vector2(0, dst.Height), new Vector2(dst.Width, dst.Height) };
            for (int k = 0; k < c.Length; k++) c[k] = Vector2.Transform(c[k], inv);
            float x0 = c.Min(v => v.X), y0 = c.Min(v => v.Y), x1 = c.Max(v => v.X), y1 = c.Max(v => v.Y);
            var visible = new RectangleF(x0, y0, x1 - x0, y1 - y0);
            float scale = MathF.Sqrt(MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21));

            // 2 device pixels around the view: a hairline reaches one device pixel past its path, at any zoom
            float margin = scale > 1e-6f ? 2f / scale : 0f;
            var ids = ShapesIn(RectangleF.Inflate(visible, margin, margin));
            var view = new VectorImage { Format = Format, Title = Title, ViewBox = ViewBox, Width = Width, Height = Height, VisibleArea = VisibleArea, ClipViewport = ClipViewport };
            foreach (int i in ids)
            {
                var sh = Shapes[i];
                float need = minScreenPx;
                if (IsMap && sh.Layer != null && MapLayerMinPx.TryGetValue(sh.Layer, out float layerMin)) need = Math.Max(need, layerMin);
                if (need > 0f && _indexSize[i] * scale < need) continue;
                view.Shapes.Add(sh);
            }
            view.Draw(dst, m, o);
        }
    }

    /// <summary>
    /// The bitmap cache of a picture: the picture is drawn once into a bitmap twice the canvas size, centred on a view.
    /// Pans that keep the canvas inside that bitmap are a copy of it (whole pixels); a zoom change, a pan past the
    /// margin or a different picture key means the vectors are drawn again. The caller decides when to bake (the view
    /// is still) and passes a key with everything that changes the picture (file, level, options, minimum size).
    /// </summary>
    internal sealed class VectorViewCache : IDisposable
    {
        Sprite? _bmp;
        string _key = "";
        double _cx, _cy, _k;             // the view the bitmap was drawn for: picture units at the centre, scale
        int _w, _h;                      // the canvas size the margin was made for
        int _bw, _bh;                    // the bitmap size (twice the canvas)

        /// <summary>Forgets the picture (the memory is kept for the next bake).</summary>
        public void Clear() => _key = "";

        /// <summary>The view matrix: picture units at (cx, cy) map to the centre (ox, oy) of the target, at scale k.</summary>
        internal static Matrix3x2 ViewMatrix(double cx, double cy, float k, float ox, float oy)
            => Matrix3x2.CreateTranslation((float)-cx, (float)-cy) * Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(ox, oy);

        /// <summary>True when the bitmap covers a w x h canvas at this view: same key, same scale, and the view inside the margin.</summary>
        public bool Covers(string key, int w, int h, double cx, double cy, double k)
            => _bmp != null && key == _key && w == _w && h == _h && Math.Abs(_k - k) < 1e-9
               && Math.Abs((cx - _cx) * k) <= (_bw - w) / 2.0 - 2 && Math.Abs((cy - _cy) * k) <= (_bh - h) / 2.0 - 2;

        /// <summary>Draws the picture into the bitmap for this view (background first, same options as the direct draw).</summary>
        public void Bake(VectorImage img, string key, int w, int h, double cx, double cy, double k, VectorRenderOptions o, float minPx, int background)
        {
            int bw = 2 * w, bh = 2 * h;
            if (_bmp == null || _bmp.Width != bw || _bmp.Height != bh) { _bmp?.Dispose(); _bmp = new Sprite(bw, bh); }
            _bmp.ClearBuffer(background);
            img.DrawVisible(_bmp, ViewMatrix(cx, cy, (float)k, bw * 0.5f, bh * 0.5f), o, minPx);
            _key = key; _cx = cx; _cy = cy; _k = k; _w = w; _h = h; _bw = bw; _bh = bh;
        }

        /// <summary>Copies the bitmap onto the canvas for the view (cx, cy). Only valid when <see cref="Covers"/> is true.</summary>
        public void Blit(Sprite canvas, double cx, double cy)
        {
            // canvas pixel (x, y) is bitmap pixel (x + dx, y + dy): picture units -> bitmap pixels, minus the canvas centre
            double dx = (_bw - _w) / 2.0 + (cx - _cx) * _k, dy = (_bh - _h) / 2.0 + (cy - _cy) * _k;
            canvas.Draw(_bmp!, -(int)Math.Round(dx), -(int)Math.Round(dy));
        }

        public void Dispose() { _bmp?.Dispose(); _bmp = null; _key = ""; }
    }

    /// <summary>
    /// Tiles of a picture: the canvas is covered by fixed tiles (TileSize x TileSize device pixels, on a lattice fixed to
    /// the picture at the current scale), each drawn once with <see cref="VectorImage.DrawVisible"/> and kept in an LRU.
    /// A pan copies the tiles it already has. The tiles that are missing on screen are drawn at once (in parallel); the
    /// ring of tiles just outside the view is drawn on a background task, so a pan finds its next tiles ready.
    /// A change of key or scale drops every tile (zoom frames draw the tiles again). The view centre is rounded to whole
    /// device pixels, so a tile and its copy line up exactly.
    /// </summary>
    internal sealed class VectorTileCache : IDisposable
    {
        public const int TileSize = 256;
        /// <summary>Most tiles kept (a tile is TileSize x TileSize x 4 bytes = 256 KB): 256 tiles = 64 MB.</summary>
        public int MaxTiles = 256;
        /// <summary>Draw the ring of tiles around the view on a background task (default true).</summary>
        public bool Prefetch = true;
        /// <summary>Width of the background ring, in tiles, around the visible range (default 2).</summary>
        public int PrefetchRing = 2;
        /// <summary>Tiles drawn on screen / copied / finished in the background since the previous Draw (statistics).</summary>
        public int LastDrawn, LastCopied, LastPrefetched;

        sealed class Tile { public int I, J, Frame; public Sprite Pix = null!; public LinkedListNode<Tile> Link = null!; }
        readonly object _lock = new object();
        readonly Dictionary<(int, int), Tile> _tiles = new Dictionary<(int, int), Tile>();
        readonly LinkedList<Tile> _lru = new LinkedList<Tile>();          // most recent first
        readonly HashSet<(int, int)> _pending = new HashSet<(int, int)>();  // tiles a background task is drawing
        readonly Stack<Sprite> _free = new Stack<Sprite>();
        string _key = "";
        float _k = float.NaN;
        int _frame, _gen, _prefetched;
        bool _disposed;

        /// <summary>Forgets every tile (tiles still being drawn in the background are dropped when they finish).</summary>
        public void Clear() { lock (_lock) ClearLocked(); }

        void ClearLocked()
        {
            foreach (var t in _tiles.Values) _free.Push(t.Pix);
            _tiles.Clear(); _lru.Clear(); _pending.Clear();
            _gen++;
        }

        /// <summary>True while background tiles are still being drawn.</summary>
        public bool Busy { get { lock (_lock) return _pending.Count > 0; } }

        /// <summary>Number of tiles held.</summary>
        public int Count { get { lock (_lock) return _tiles.Count; } }

        /// <summary>
        /// Draws the picture onto <paramref name="canvas"/> for the view centred on (cx, cy) (picture units) at scale k.
        /// key: everything that changes the pixels (file, level, options, minimum size, antialiasing); background: the tile fill.
        /// </summary>
        public void Draw(VectorImage img, Sprite canvas, double cx, double cy, float k, string key, VectorRenderOptions o, float minPx, int background)
        {
            if (_disposed) return;
            lock (_lock)
            {
                if (key != _key || k != _k) { ClearLocked(); _key = key; _k = k; }
            }
            if (!img.IndexCurrent) img.BuildIndex();                       // before any thread reads the index
            int W = canvas.Width, H = canvas.Height;
            // whole device pixels: the lattice and the canvas share one grid
            long N = (long)Math.Round(cx * k), M = (long)Math.Round(cy * k);
            int ox = W / 2, oy = H / 2;
            long gx0 = N - ox, gy0 = M - oy;                                // device position of the canvas' left / top pixel
            int i0 = (int)Math.Floor(gx0 / (double)TileSize), i1 = (int)Math.Floor((gx0 + W - 1) / (double)TileSize);
            int j0 = (int)Math.Floor(gy0 / (double)TileSize), j1 = (int)Math.Floor((gy0 + H - 1) / (double)TileSize);

            var shown = new List<Tile>((i1 - i0 + 1) * (j1 - j0 + 1));
            var missing = new List<(int i, int j)>();
            int gen, frame, prefetched;
            lock (_lock)
            {
                _frame++;
                frame = _frame; gen = _gen;
                prefetched = _prefetched; _prefetched = 0;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        if (_tiles.TryGetValue((i, j), out var t))
                        {
                            _lru.Remove(t.Link); _lru.AddFirst(t.Link); t.Frame = frame; shown.Add(t);
                        }
                        else missing.Add((i, j));
                    }
            }
            LastCopied = shown.Count; LastDrawn = missing.Count; LastPrefetched = prefetched;

            // the tiles on screen that are missing: drawn now, in parallel
            var fresh = new Tile[missing.Count];
            var sprites = new Sprite[missing.Count];
            lock (_lock) for (int q = 0; q < missing.Count; q++) sprites[q] = FreeSpriteLocked();
            for (int q = 0; q < missing.Count; q++) fresh[q] = new Tile { I = missing[q].i, J = missing[q].j, Frame = frame, Pix = sprites[q] };
            if (missing.Count > 0) RenderTiles(img, fresh, k, o, minPx, background);
            lock (_lock)
            {
                foreach (var t in fresh)
                {
                    if (_gen != gen || _tiles.ContainsKey((t.I, t.J))) { _free.Push(t.Pix); continue; }
                    _pending.Remove((t.I, t.J));
                    _tiles[(t.I, t.J)] = t;
                    t.Link = _lru.AddFirst(t);
                    shown.Add(t);
                }
            }

            // the ring of tiles just outside the view: drawn in the background for the next frames
            if (Prefetch) Ring(img, i0, j0, i1, j1, k, o, minPx, background, gen);

            // copy the tiles onto the canvas
            foreach (var t in shown)
                canvas.Draw(t.Pix, (int)(t.I * (long)TileSize - N + ox), (int)(t.J * (long)TileSize - M + oy), SR2D.Op.Paint);

            lock (_lock) EvictLocked(frame);
        }

        Sprite FreeSpriteLocked() => _free.Count > 0 ? _free.Pop() : new Sprite(TileSize, TileSize);

        void EvictLocked(int frame)
        {
            var node = _lru.Last;
            while (_tiles.Count > MaxTiles && node != null)
            {
                var prev = node.Previous;
                if (node.Value.Frame != frame)
                {
                    _tiles.Remove((node.Value.I, node.Value.J));
                    _lru.Remove(node);
                    _free.Push(node.Value.Pix);
                }
                node = prev;
            }
        }

        /// <summary>Draws the tiles: each into its own sprite, in parallel. Pure: reads the picture, writes only the sprites.</summary>
        static void RenderTiles(VectorImage img, Tile[] tiles, float k, VectorRenderOptions o, float minPx, int background)
        {
            Parallel.For(0, tiles.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, q =>
            {
                var t = tiles[q];
                t.Pix.ClearBuffer(background);
                // picture -> tile pixels: the tile's top-left is device (I * TileSize, J * TileSize) on the picture lattice
                var m = Matrix3x2.CreateTranslation((float)(-t.I * (double)TileSize / k), (float)(-t.J * (double)TileSize / k)) * Matrix3x2.CreateScale(k);
                img.DrawVisible(t.Pix, m, o, minPx);
            });
        }

        /// <summary>Starts a background task for the tiles around the visible range that are neither held nor being drawn.</summary>
        void Ring(VectorImage img, int i0, int j0, int i1, int j1, float k, VectorRenderOptions o, float minPx, int background, int gen)
        {
            var want = new List<(int i, int j)>();
            lock (_lock)
            {
                if (_gen != gen) return;
                int r = PrefetchRing;
                for (int j = j0 - r; j <= j1 + r; j++)
                    for (int i = i0 - r; i <= i1 + r; i++)
                    {
                        if (i >= i0 && i <= i1 && j >= j0 && j <= j1) continue;     // on screen: drawn in Draw
                        if (_tiles.ContainsKey((i, j)) || _pending.Contains((i, j))) continue;
                        want.Add((i, j));
                        _pending.Add((i, j));
                    }
                if (want.Count == 0) return;
            }
            Task.Run(() =>
            {
                var fresh = new Tile[want.Count];
                lock (_lock) for (int q = 0; q < want.Count; q++) fresh[q] = new Tile { I = want[q].i, J = want[q].j, Frame = 0, Pix = FreeSpriteLocked() };
                try { RenderTiles(img, fresh, k, o, minPx, background); }
                catch
                {
                    lock (_lock) foreach (var w in want) _pending.Remove(w);     // a failed tile is asked for again later
                    throw;
                }
                lock (_lock)
                {
                    foreach (var t in fresh)
                    {
                        if (_gen != gen || _disposed) { _free.Push(t.Pix); continue; }    // the scale, the key or the picture changed meanwhile
                        _pending.Remove((t.I, t.J));
                        if (_tiles.ContainsKey((t.I, t.J))) { _free.Push(t.Pix); continue; }
                        _tiles[(t.I, t.J)] = t;
                        t.Link = _lru.AddFirst(t);
                        _prefetched++;
                    }
                }
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                ClearLocked();
                while (_free.Count > 0) _free.Pop().Dispose();
            }
        }
    }
}
