// Vector.Index.cs - a grid index over the shapes of a VectorImage, so a view draws only what is inside it.
// Used by big imports (a whole map city is tens of thousands of shapes): DrawVisible(...) finds the shapes that
// touch the destination through the grid, keeps their z-order, and skips shapes that would be smaller than a
// minimum size on screen (level of detail). Shapes are not copied; the subset is a view of the same list.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace Sr2d64CSport
{
    internal sealed partial class VectorImage
    {
        RectangleF[] _indexBounds = Array.Empty<RectangleF>();
        List<int>[] _indexCells = Array.Empty<List<int>>();
        int _indexCols, _indexRows, _indexedCount = -1;
        float _indexX0, _indexY0, _indexCell = 256f;
        int[] _indexStamp = Array.Empty<int>();
        int _indexGen;
        VectorImage? _visibleView;

        /// <summary>Builds (or rebuilds) the grid over the shapes' bounds (image units). Call after the shape list changes.</summary>
        public void BuildIndex(float cell = 256f)
        {
            int n = Shapes.Count;
            _indexBounds = new RectangleF[n];
            _indexCell = Math.Max(16f, cell);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var s = Shapes[i];
                if (s.Path.IsEmpty) { _indexBounds[i] = RectangleF.Empty; continue; }
                var b = s.Path.ControlBounds();
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
            _indexStamp = new int[n];
            _indexGen = 0;
            _indexedCount = n;
        }

        /// <summary>Shape indices (ascending = drawing order) whose bounds touch the rectangle (image units).</summary>
        public List<int> ShapesIn(RectangleF r)
        {
            if (_indexedCount != Shapes.Count) BuildIndex(_indexCell);
            var result = new List<int>();
            int c0 = Math.Clamp((int)Math.Floor((r.Left - _indexX0) / _indexCell), 0, _indexCols - 1);
            int c1 = Math.Clamp((int)Math.Floor((r.Right - _indexX0) / _indexCell), 0, _indexCols - 1);
            int r0 = Math.Clamp((int)Math.Floor((r.Top - _indexY0) / _indexCell), 0, _indexRows - 1);
            int r1 = Math.Clamp((int)Math.Floor((r.Bottom - _indexY0) / _indexCell), 0, _indexRows - 1);
            int gen = ++_indexGen;
            for (int row = r0; row <= r1; row++)
                for (int col = c0; col <= c1; col++)
                {
                    var cell = _indexCells[row * _indexCols + col];
                    if (cell == null) continue;
                    foreach (int i in cell)
                    {
                        if (_indexStamp[i] == gen) continue;
                        _indexStamp[i] = gen;
                        if (_indexBounds[i].IntersectsWith(r)) result.Add(i);
                    }
                }
            result.Sort();
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

            var ids = ShapesIn(visible);
            _visibleView ??= new VectorImage { Format = Format, Title = Title, ViewBox = ViewBox, Width = Width, Height = Height, VisibleArea = VisibleArea, ClipViewport = ClipViewport };
            _visibleView.Shapes.Clear();
            foreach (int i in ids)
            {
                if (minScreenPx > 0f)
                {
                    var b = _indexBounds[i];
                    if (Math.Max(b.Width, b.Height) * scale < minScreenPx) continue;
                }
                _visibleView.Shapes.Add(Shapes[i]);
            }
            _visibleView.Draw(dst, m, o);
            _visibleView.Shapes.Clear();                 // do not keep the shapes alive through the view
        }
    }
}
