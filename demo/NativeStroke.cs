// Stroke check, native comparison: the same stroke geometry drawn with GDI+ (System.Drawing.Drawing2D), so the
// renderer's own stroke can be compared with the platform's. Windows only (System.Drawing); on other systems the
// call throws and the caller falls back to the engine's stroke alone.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    internal static class NativeStroke
    {
        /// <summary>One stroke as the engine receives it: the path (millimetres), its width, the optional dash pattern (in
        /// millimetres, on/off/on/...), and the clip (null = none). Clip paths follow the engine's VectorClip rules.</summary>
        public sealed class Item
        {
            public VectorPath Path = null!;
            public float Width;
            public float[]? Dash;
            public VectorClip? Clip;
        }

        /// <summary>Draws the items with GDI+ and returns the result composited over an opaque background, one ARGB int per
        /// pixel (the same layout as Sprite.Pixels). The mapping is the engine's: x' = (x - cx) * s + ox, likewise y.
        /// A GDI+ failure is rethrown with the step that failed (pen, dash, path, clip, draw) and the item it was in, because
        /// GDI+ only reports "Parameter is not valid".</summary>
        public static int[] Render(int w, int h, double s, double ox, double oy, double cx, double cy, IList<Item> items,
                                   VectorCap cap, VectorJoin join, bool antiAlias, int strokeArgb, int bgArgb)
        {
            if (w <= 0 || h <= 0) throw new ArgumentException($"canvas {w} x {h}");
            if (!(s > 0) || double.IsInfinity(s)) throw new ArgumentException($"scale {s}");
            if (!Finite(ox) || !Finite(oy) || !Finite(cx) || !Finite(cy)) throw new ArgumentException($"offset {ox},{oy} centre {cx},{cy}");

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.PageUnit = GraphicsUnit.World;
                g.SmoothingMode = antiAlias ? SmoothingMode.AntiAlias : SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.None;
                // a single transform: the same as the engine's matrix applied to the geometry
                g.Transform = new Matrix((float)s, 0f, 0f, (float)s, (float)(ox - cx * s), (float)(oy - cy * s));
                var color = Color.FromArgb(strokeArgb);
                for (int k = 0; k < items.Count; k++)
                {
                    var it = items[k];
                    if (it.Path.IsEmpty) continue;
                    string stage = "setup";
                    try
                    {
                        if (!(it.Width > 0) || !Finite(it.Width)) throw new ArgumentException($"width {it.Width}");
                        stage = "pen";
                        using var pen = new Pen(color, it.Width)
                        {
                            StartCap = LineCapOf(cap), EndCap = LineCapOf(cap), DashCap = DashCap.Flat,
                            LineJoin = JoinOf(join), MiterLimit = 4f,
                        };
                        if (it.Dash != null && it.Dash.Length >= 2)
                        {
                            // GDI+ dash lengths are multiples of the pen width; each must be positive and finite
                            stage = "dash";
                            var pattern = new float[it.Dash.Length];
                            for (int i = 0; i < pattern.Length; i++)
                            {
                                float v = it.Dash[i] / it.Width;
                                if (!(v > 0) || !Finite(v)) throw new ArgumentException($"dash {it.Dash[i]} / width {it.Width}");
                                pattern[i] = v;
                            }
                            pen.DashStyle = DashStyle.Custom;
                            pen.DashPattern = pattern;
                        }
                        stage = "path";
                        using var gp = ToGraphics(it.Path);
                        g.ResetClip();
                        if (it.Clip != null && it.Clip.Paths.Count > 0)
                        {
                            stage = "clip";
                            bool first = true;
                            foreach (var (cp, evenOdd) in it.Clip.Paths)
                            {
                                using var cgp = ToGraphics(cp, evenOdd ? FillMode.Alternate : FillMode.Winding);
                                if (first) g.SetClip(cgp, CombineMode.Replace);
                                else { using var reg = new Region(cgp); g.IntersectClip(reg); }
                                first = false;
                            }
                        }
                        stage = "draw";
                        g.DrawPath(pen, gp);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"item {k} at stage '{stage}' ({it.Path.SegmentCount} segments, width {it.Width:0.###} mm, " +
                            $"dash {(it.Dash == null ? "none" : string.Join("/", it.Dash))}, clip {(it.Clip == null ? "none" : it.Clip.Paths.Count + " path(s)")}): {ex.GetType().Name}: {ex.Message}", ex);
                    }
                }
            }
            // read back and composite over the opaque background
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var src = new int[w * h];
            try { Marshal.Copy(data.Scan0, src, 0, src.Length); }      // stride == w * 4 for 32 bpp ARGB
            finally { bmp.UnlockBits(data); }
            int bg = bgArgb & 0xFFFFFF;
            for (int i = 0; i < src.Length; i++)
            {
                int p = src[i];
                int a = (int)((uint)p >> 24);
                if (a == 255) { src[i] = unchecked((int)0xFF000000) | (p & 0xFFFFFF); continue; }
                if (a == 0) { src[i] = unchecked((int)0xFF000000) | bg; continue; }
                src[i] = unchecked((int)0xFF000000) | (Mix((p >> 16) & 0xFF, (bg >> 16) & 0xFF, a) << 16)
                                                    | (Mix((p >> 8) & 0xFF, (bg >> 8) & 0xFF, a) << 8)
                                                    | Mix(p & 0xFF, bg & 0xFF, a);
            }
            return src;
        }

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        static int Mix(int c, int b, int a) => (c * a + b * (255 - a) + 127) / 255;

        static System.Drawing.Drawing2D.LineCap LineCapOf(VectorCap c) => c switch
        {
            VectorCap.Round => System.Drawing.Drawing2D.LineCap.Round,
            VectorCap.Square => System.Drawing.Drawing2D.LineCap.Square,
            _ => System.Drawing.Drawing2D.LineCap.Flat,
        };

        static System.Drawing.Drawing2D.LineJoin JoinOf(VectorJoin j) => j switch
        {
            VectorJoin.Round => System.Drawing.Drawing2D.LineJoin.Round,
            VectorJoin.Bevel => System.Drawing.Drawing2D.LineJoin.Bevel,
            _ => System.Drawing.Drawing2D.LineJoin.Miter,
        };

        static GraphicsPath ToGraphics(VectorPath p, FillMode fill = FillMode.Winding)
        {
            var gp = new GraphicsPath(fill);
            PointF cur = PointF.Empty;
            int di = 0;
            foreach (var k in p.Kinds)
            {
                switch (k)
                {
                    case VectorPath.K.Move:
                        cur = new PointF(p.Data[di], p.Data[di + 1]); di += 2;
                        gp.StartFigure();
                        break;
                    case VectorPath.K.Line:
                        {
                            var e = new PointF(p.Data[di], p.Data[di + 1]); di += 2;
                            gp.AddLine(cur, e); cur = e;
                            break;
                        }
                    case VectorPath.K.Cubic:
                        {
                            var c1 = new PointF(p.Data[di], p.Data[di + 1]);
                            var c2 = new PointF(p.Data[di + 2], p.Data[di + 3]);
                            var e = new PointF(p.Data[di + 4], p.Data[di + 5]); di += 6;
                            gp.AddBezier(cur, c1, c2, e); cur = e;
                            break;
                        }
                    default:                                  // close: the figure joins back to its start
                        gp.CloseFigure();
                        break;
                }
            }
            return gp;
        }
    }
}
