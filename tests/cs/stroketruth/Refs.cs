// Independent reference strokers on this machine: System.Drawing (GDI+) and WPF (MilCore).
// Both are complete, shipped stroking implementations that are not derived from SR2D, which is what makes them
// useful: when the engine and the analytic truth disagree, the references say who is wrong.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    internal static class Refs
    {
        public const int W = 240, H = 180;

        /// <summary>GDI+ defines the miter limit as (miter length / pen width) while SVG and PDF use
        /// (miter length / half width) for the same corner, i.e. a factor of two apart. The rig can be run with
        /// --gdi-miter-half to see which definition the shipped reference actually applies.</summary>
        public static bool GdiMiterHalf;

        /// <summary>Which GDI+ pixel-offset mode to use, for measuring which one puts GDI+ on the SVG grid convention.</summary>
        public static int GdiPixelOffset = 1;

        /// <summary>Renders a stroke with GDI+ and returns the ink coverage (255 = black ink).
        /// Returns null when GDI+ cannot express the request (a zero-length dash, which SVG draws as a dot).</summary>
        public static byte[]? GdiPlus(VectorPath path, double width, Cap cap, Join join, double miter, float[]? dash, double dashOffset)
        {
            // A zero-length dash is how SVG asks for a dot, and GDI+ rejects it outright, so this reference simply
            // cannot express the case; the caller shows the column as absent rather than as a pass.
            if (dash != null && dash.Length > 0 && Array.Exists(dash, v => v <= 0)) return null;
            try { return GdiPlusInner(path, width, cap, join, miter, dash, dashOffset); }
            catch (Exception) { return null; }       // the reference cannot express this case; reported as a missing column
        }

        /// <summary>GDI+'s default PixelOffsetMode.None treats a pixel's (0,0) as its centre, so every edge it draws sits
        /// half a pixel off the SVG convention; Half puts it on the same grid as the truth. Shared by the stroke and the
        /// widen references so the two are always measured in one frame.</summary>
        static PixelOffsetMode GdiOffset => GdiPixelOffset switch
        {
            0 => PixelOffsetMode.None,
            2 => PixelOffsetMode.HighQuality,
            3 => PixelOffsetMode.Default,
            _ => PixelOffsetMode.Half,
        };

        static byte[]? GdiPlusInner(VectorPath path, double width, Cap cap, Join join, double miter, float[]? dash, double dashOffset)
        {
            using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = GdiOffset;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.White);
                using var gp = ToGraphics(path);
                using var pen = new Pen(Color.Black, (float)width)
                {
                    StartCap = CapOf(cap), EndCap = CapOf(cap),
                    LineJoin = JoinOf(join),
                    MiterLimit = (float)Math.Max(0.1, GdiMiterHalf ? miter / 2 : miter),
                };
                if (dash != null && dash.Length > 0)
                {
                    // GDI+ dash lengths are multiples of the pen width, ours are pixels
                    var pat = new float[dash.Length];
                    for (int i = 0; i < dash.Length; i++) pat[i] = Math.Max(0, dash[i]) / (float)width;
                    if (pat.Length % 2 != 0) Array.Resize(ref pat, pat.Length * 2);
                    for (int i = pat.Length / 2; i < pat.Length; i++) pat[i] = pat[i - pat.Length / 2];
                    pen.DashStyle = DashStyle.Custom;
                    pen.DashPattern = pat;
                    // GDI+'s DashCap only offers Flat / Round / Triangle, so a dashed Square cap cannot be asked for
                    if (cap == Cap.Square) return null;
                    pen.DashCap = cap == Cap.Round ? DashCap.Round : DashCap.Flat;
                    pen.DashOffset = (float)(dashOffset / width);
                }
                g.DrawPath(pen, gp);
            }
            return Ink(bmp);
        }

        // the engine has its own LineCap / LineJoin in this namespace, so the GDI+ ones are written out in full
        static System.Drawing.Drawing2D.LineCap CapOf(Cap c) => c == Cap.Round ? System.Drawing.Drawing2D.LineCap.Round : c == Cap.Square ? System.Drawing.Drawing2D.LineCap.Square : System.Drawing.Drawing2D.LineCap.Flat;
        static System.Drawing.Drawing2D.LineJoin JoinOf(Join j) => j == Join.Round ? System.Drawing.Drawing2D.LineJoin.Round : j == Join.Bevel ? System.Drawing.Drawing2D.LineJoin.Bevel : System.Drawing.Drawing2D.LineJoin.Miter;

        /// <summary>Converts an SR2D path into a GraphicsPath, keeping each sub-path's closed flag.</summary>
        public static GraphicsPath ToGraphics(VectorPath p)
        {
            var gp = new GraphicsPath();
            var d = p.Data; int i = 0; float cx = 0, cy = 0, sx = 0, sy = 0;
            foreach (var k in p.Kinds)
            {
                switch (k)
                {
                    case VectorPath.K.Move:
                        gp.StartFigure(); cx = sx = d[i]; cy = sy = d[i + 1]; i += 2; break;
                    case VectorPath.K.Line:
                        gp.AddLine(cx, cy, d[i], d[i + 1]); cx = d[i]; cy = d[i + 1]; i += 2; break;
                    case VectorPath.K.Cubic:
                        gp.AddBezier(cx, cy, d[i], d[i + 1], d[i + 2], d[i + 3], d[i + 4], d[i + 5]);
                        cx = d[i + 4]; cy = d[i + 5]; i += 6; break;
                    case VectorPath.K.Close:
                        if (cx != sx || cy != sy) gp.AddLine(cx, cy, sx, sy);
                        gp.CloseFigure(); cx = sx; cy = sy; break;
                }
            }
            return gp;
        }

        /// <summary>Node statistics of a path, counted the same way for the engine's bake and for GDI+'s Widen:
        /// on-curve anchors plus how many segments are still curves. The outline requirement is "amount of nodes that
        /// make sense" with accuracy first, and a bake that replaced every bezier with 48 chords only shows up when the
        /// segment mix sits next to the coverage number.</summary>
        public readonly struct Nodes
        {
            public readonly int Anchors, Cubic, Line;
            public Nodes(int a, int c, int l) { Anchors = a; Cubic = c; Line = l; }
            public int Segs => Cubic + Line;
            public bool IsEmpty => Anchors == 0;
            public override string ToString() => $"k{Anchors} c{Cubic} l{Line}";
        }

        public static Nodes Of(VectorPath p)
        {
            int a = 0, c = 0, l = 0;
            foreach (var k in p.Kinds)
                switch (k)
                {
                    case VectorPath.K.Move: a++; break;
                    case VectorPath.K.Line: a++; l++; break;
                    case VectorPath.K.Cubic: a++; c++; break;
                }
            return new Nodes(a, c, l);
        }

        /// <summary>Counts a GDI+ PathData with the same definition as Of(VectorPath). The type is the low three bits of
        /// each marker (0 start, 1 line, 3 bezier - the high bits are figure flags), and a cubic marks its three
        /// following points, so it contributes three markers and one anchor.</summary>
        static Nodes OfGraphics(GraphicsPath gp)
        {
            int bez = 0, lin = 0, start = 0;
            foreach (byte b in gp.PathData.Types)
            {
                switch (b & 0x07)
                {
                    case 0x03: bez++; break;
                    case 0x01: lin++; break;
                    default: start++; break;
                }
            }
            int cubic = bez / 3;
            return new Nodes(start + lin + cubic, cubic, lin);
        }

        /// <summary>GDI+'s own stroke-to-shape, GraphicsPath.Widen - the .NET-native reference the user compares against.
        /// The widened path is filled with the winding rule and counted, so node inflation is measured against the engine's
        /// bake rather than argued about. Returns null when Widen rejects the figure.</summary>
        public static byte[]? WidenOutline(VectorPath path, double width, Cap cap, Join join, double miter, out Nodes nodes)
        {
            nodes = default;
            try
            {
                using var gp = ToGraphics(path);
                using var pen = new Pen(Color.Black, (float)width)
                {
                    StartCap = CapOf(cap), EndCap = CapOf(cap), LineJoin = JoinOf(join),
                    MiterLimit = (float)Math.Max(0.1, miter),
                };
                gp.Widen(pen);
                nodes = OfGraphics(gp);
                using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = GdiOffset;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.Clear(Color.White);
                    gp.FillMode = FillMode.Winding;
                    g.FillPath(Brushes.Black, gp);
                }
                return Ink(bmp);
            }
            catch (Exception) { return null; }
        }

        static byte[] Ink(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int n = rect.Width * rect.Height;
            var argb = new int[n];
            Marshal.Copy(data.Scan0, argb, 0, n);
            bmp.UnlockBits(data);
            var ink = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int a = (argb[i] >> 24) & 0xFF, v = argb[i] & 0xFF;      // black ink on white, alpha premultiplied
                if (a < 255) v = (int)Math.Round(255 - (255 - v) * (a / 255.0));
                ink[i] = (byte)Math.Clamp(255 - v, 0, 255);
            }
            return ink;
        }

        /// <summary>False: the miter limit as given (SVG semantics: miter length / stroke width). WPF's Pen.MiterLimit is
        /// documented against HALF the thickness, so the same number allows a spike twice as long; the rig can halve it to
        /// check that this is the whole difference (--wpf-miter-half).</summary>
        public static bool WpfMiterHalf;

        /// <summary>Renders a stroke with WPF's MilCore rasteriser. Returns null when WPF cannot render here.</summary>
        public static byte[]? Wpf(VectorPath path, double width, Cap cap, Join join, double miter, float[]? dash, double dashOffset)
        {
            try
            {
                var geo = new System.Windows.Media.PathGeometry();
                var figs = new List<System.Windows.Media.PathFigure>();
                System.Windows.Media.PathFigure? fig = null;
                double cx = 0, cy = 0, sx = 0, sy = 0; int i = 0; var d = path.Data;
                foreach (var k in path.Kinds)
                {
                    switch (k)
                    {
                        case VectorPath.K.Move:
                            fig = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(d[i], d[i + 1]), IsClosed = false, IsFilled = false };
                            figs.Add(fig); sx = cx = d[i]; sy = cy = d[i + 1]; i += 2; break;
                        case VectorPath.K.Line:
                            (fig ??= figs[figs.Count - 1]).Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(d[i], d[i + 1]), true));
                            cx = d[i]; cy = d[i + 1]; i += 2; break;
                        case VectorPath.K.Cubic:
                            (fig ??= figs[figs.Count - 1]).Segments.Add(new System.Windows.Media.BezierSegment(
                                new System.Windows.Point(d[i], d[i + 1]), new System.Windows.Point(d[i + 2], d[i + 3]), new System.Windows.Point(d[i + 4], d[i + 5]), true));
                            cx = d[i + 4]; cy = d[i + 5]; i += 6; break;
                        case VectorPath.K.Close:
                            if (fig != null) { fig.IsClosed = true; if (cx != sx || cy != sy) fig.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(sx, sy), true)); cx = sx; cy = sy; }
                            break;
                    }
                }
                foreach (var f in figs) geo.Figures.Add(f);
                geo.FillRule = System.Windows.Media.FillRule.EvenOdd;

                var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, width)
                {
                    StartLineCap = WpfCap(cap), EndLineCap = WpfCap(cap), LineJoin = WpfJoin(join),
                    MiterLimit = Math.Max(0.1, WpfMiterHalf ? miter / 2 : miter),
                };
                if (dash != null && dash.Length > 0)
                {
                    if (Array.Exists(dash, v => v <= 0)) return null;    // a zero-length dash is a dot, which WPF cannot express
                    var pat = new System.Windows.Media.DoubleCollection();
                    foreach (var v in dash) pat.Add(v / width);     // WPF dash units are pen widths too
                    pen.DashStyle = new System.Windows.Media.DashStyle(pat, dashOffset / width);
                    pen.DashCap = WpfCap(cap);
                }
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawGeometry(null, pen, geo);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(dv);
                var px = new byte[W * H * 4];
                rtb.CopyPixels(px, W * 4, 0);
                var ink = new byte[W * H];
                for (int j = 0; j < W * H; j++)
                {   // Pbgra32, premultiplied: the black pen's coverage is its alpha
                    int a = px[j * 4 + 3];
                    ink[j] = (byte)a;
                }
                return ink;
            }
            catch (Exception e) { WpfError = e.GetType().Name + ": " + e.Message; return null; }
        }

        public static string WpfError;

        /// <summary>Is WPF's offscreen rasteriser working here at all? A filled red rectangle, no pen involved.</summary>
        public static string WpfFillProbe()
        {
            try
            {
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(System.Windows.Media.Brushes.Black, null,
                        new System.Windows.Rect(40, 80, 160, 20));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(dv);
                var px = new byte[W * H * 4];
                rtb.CopyPixels(px, W * 4, 0);
                long sum = 0; int nz = 0;
                for (int i = 0; i < W * H; i++) { sum += px[i * 4 + 3]; if (px[i * 4 + 3] > 0) nz++; }
                return $"bounds {rtb.Width}x{rtb.Height} alpha sum {sum} nonzero {nz} (expect ~3200x255)";
            }
            catch (Exception e) { return "threw " + e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>Which of the WPF stroke inputs is the one that produces an empty bitmap?</summary>
        public static string WpfStrokeVariants()
        {
            var sb = new System.Text.StringBuilder();
            void Try(string label, Func<(System.Windows.Media.Geometry, System.Windows.Media.Pen)> make)
            {
                try
                {
                    var (geo, pen) = make();
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var dc = dv.RenderOpen()) dc.DrawGeometry(null, pen, geo);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    var px = new byte[W * H * 4];
                    rtb.CopyPixels(px, W * 4, 0);
                    int nz = 0; for (int i = 0; i < W * H; i++) if (px[i * 4 + 3] > 0) nz++;
                    sb.Append($"{label}: {nz} px, bounds {geo.Bounds.Width:0}x{geo.Bounds.Height:0}; ");
                }
                catch (Exception e) { sb.Append($"{label}: threw {e.GetType().Name}; "); }
            }
            var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, 7);
            Try("LineGeometry", () => (new System.Windows.Media.LineGeometry
            { StartPoint = new System.Windows.Point(40, 90), EndPoint = new System.Windows.Point(200, 90) }, pen));
            Try("rect stroke", () => (new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(40, 60, 160, 60)), pen));
            Try("fig IsFilled=true", () =>
            {
                var f = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(40, 90), IsFilled = true, IsClosed = false };
                f.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(200, 90), false));
                var g = new System.Windows.Media.PathGeometry(); g.Figures.Add(f); return (g, pen);
            });
            Try("fig IsFilled=false", () =>
            {
                var f = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(40, 90), IsFilled = false, IsClosed = false };
                f.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(200, 90), false));
                var g = new System.Windows.Media.PathGeometry(); g.Figures.Add(f); return (g, pen);
            });
            Try("fig isStroked=true", () =>
            {
                var f = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(40, 90), IsFilled = false, IsClosed = false };
                f.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(200, 90), true));
                var g = new System.Windows.Media.PathGeometry(); g.Figures.Add(f); return (g, pen);
            });
            Try("StreamGeometry", () =>
            {
                var sg = new System.Windows.Media.StreamGeometry();
                using (var c = sg.Open()) { c.BeginFigure(new System.Windows.Point(40, 90), false, false); c.LineTo(new System.Windows.Point(200, 90), true, false); }
                return (sg, pen);
            });
            return sb.ToString();
        }

        /// <summary>What the render tier and the dispatcher say about this process.</summary>
        public static string WpfEnvProbe()
        {
            try
            {
                int tier = System.Windows.Media.RenderCapability.Tier >> 16;
                var disp = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                return $"render tier {tier}, dispatcher thread {disp.Thread.ManagedThreadId} " +
                       $"sta {disp.Thread.GetApartmentState()} owns {disp.CheckAccess()}";
            }
            catch (Exception e) { return "env query threw " + e.GetType().Name + ": " + e.Message; }
        }

        static System.Windows.Media.PenLineCap WpfCap(Cap c) => c == Cap.Round ? System.Windows.Media.PenLineCap.Round : c == Cap.Square ? System.Windows.Media.PenLineCap.Square : System.Windows.Media.PenLineCap.Flat;
        static System.Windows.Media.PenLineJoin WpfJoin(Join j) => j == Join.Round ? System.Windows.Media.PenLineJoin.Round : j == Join.Bevel ? System.Windows.Media.PenLineJoin.Bevel : System.Windows.Media.PenLineJoin.Miter;

        // ------------------------------------------------------------------ the engine's own two strokers
        /// <summary>Pipeline A: the vector renderer's stroke, exactly as a VectorImage draws it.
        /// For an inside / outside band it uses the trick the demo uses, because the engine has no alignment of its
        /// own: a stroke twice as wide, clipped to one side of the path.</summary>
        public static byte[] EngineA(Case cse)
        {
            using var canvas = new Sprite(W, H);
            canvas.ClearBuffer(unchecked((int)0xFFFFFFFF));
            var shape = new VectorShape(cse.Path, null, VectorColor.Black, (float)(cse.Align == 0 ? cse.Width : 2 * cse.Width))
            {
                Cap = (VectorCap)cse.Cap,
                Join = (VectorJoin)cse.Join,
                MiterLimit = (float)cse.Miter,
                Dash = cse.Dash,
                DashOffset = (float)cse.DashOffset,
                SquareDotCap = true,
            };
            if (cse.Align != 0) shape.Clip = cse.Align == 2 ? new VectorClip(cse.Path) : ExteriorClip(cse.Path, (float)(2 * cse.Width + 2));
            var img = new VectorImage();
            img.Shapes.Add(shape);
            img.Draw(canvas, System.Numerics.Matrix3x2.Identity, new VectorRenderOptions { AA = true, Strokes = true, Fills = false });
            return InkOf(canvas);
        }

        /// <summary>Pipeline B: the Sprite stroke API (StrokePath), which is a separate implementation.</summary>
        /// <remarks>The Sprite rasteriser takes pixel-CENTRE coordinates (its DRAW_POLY adds 0.5 internally; VectorRender
        /// compensates before calling it, cs/Vector.Render.cs:519), so this API puts a band centred on y=90 over rows
        /// 87..93 while the SVG grid, where a coordinate is a pixel corner, wants 86.5..93.5. Feeding the fixtures straight
        /// in therefore offsets every band by half a pixel and hides the geometry differences this column is for: measured
        /// over the 123 centred cases, all 123 failed with a mean of 200 hard pixels and dev 1.0 px, and shifting the path
        /// by +0.5 instead of -0.5 doubled that (413). SpriteCentres compares in the API's own frame; --sprite-corner-frame
        /// turns it off to measure the raw behaviour.</remarks>
        public static bool SpriteCentres = true;
        public static byte[]? EngineB(Case cse)
        {
            if (cse.Align != 0) return null;      // no clip in this API, so an aligned band cannot be expressed
            float o = SpriteCentres ? -0.5f : 0f;
            var pb = new Sprite.PathBuilder();
            var d = cse.Path.Data; int i = 0;
            foreach (var k in cse.Path.Kinds)
                switch (k)
                {
                    case VectorPath.K.Move: pb.MoveTo(d[i] + o, d[i + 1] + o); i += 2; break;
                    case VectorPath.K.Line: pb.LineTo(d[i] + o, d[i + 1] + o); i += 2; break;
                    case VectorPath.K.Cubic: pb.CurveTo(d[i] + o, d[i + 1] + o, d[i + 2] + o, d[i + 3] + o, d[i + 4] + o, d[i + 5] + o); i += 6; break;
                    case VectorPath.K.Close: pb.Close(); break;
                }
            using var canvas = new Sprite(W, H);
            canvas.ClearBuffer(unchecked((int)0xFFFFFFFF));
            canvas.StrokePath(pb, unchecked((int)0xFF000000),
                new StrokeStyle((float)cse.Width, (LineCap)cse.Cap, (LineJoin)cse.Join, (float)cse.Miter, cse.Dash, (float)cse.DashOffset));
            return InkOf(canvas);
        }

        static VectorClip ExteriorClip(VectorPath contours, float margin)
        {
            var r = contours.ControlBounds(); r.Inflate(margin, margin);
            var p = new VectorPath().Rect(r.Left, r.Top, r.Width, r.Height);
            p.Append(contours);
            return new VectorClip(p, true);
        }

        static byte[] InkOf(Sprite s)
        {
            var px = s.Pixels;
            var ink = new byte[px.Length];
            for (int i = 0; i < px.Length; i++)
            {   // black ink over white: the green channel is 255 for paper and 0 for ink
                int v = px[i] & 0xFF;
                ink[i] = (byte)Math.Clamp(255 - v, 0, 255);
            }
            // Pixels is a Span over the sprite's own unmanaged buffer, and after the line above nothing reads s again,
            // so in Release s is dead and the allocating line above is free to trigger the collection whose finalizer
            // frees that buffer mid-loop - an AccessViolationException that lands on whichever case the GC happens to
            // pick, not on a case with anything wrong with it.
            GC.KeepAlive(s);
            return ink;
        }

        // ------------------------------------------------------------------ the browser as a third opinion
        /// <summary>Path data for an SVG, exactly the canvas-pixel coordinates the fixtures are defined in.</summary>
        public static string SvgD(VectorPath p)
        {
            var sb = new System.Text.StringBuilder();
            var d = p.Data; int i = 0; var fmt = CultureInfo.InvariantCulture;
            foreach (var k in p.Kinds)
                switch (k)
                {
                    case VectorPath.K.Move: sb.Append('M').Append(d[i].ToString("0.###", fmt)).Append(' ').Append(d[i + 1].ToString("0.###", fmt)).Append(' '); i += 2; break;
                    case VectorPath.K.Line: sb.Append('L').Append(d[i].ToString("0.###", fmt)).Append(' ').Append(d[i + 1].ToString("0.###", fmt)).Append(' '); i += 2; break;
                    case VectorPath.K.Cubic:
                        sb.Append('C');
                        for (int q = 0; q < 6; q++) sb.Append(d[i + q].ToString("0.###", fmt)).Append(q == 5 ? ' ' : ' ');
                        i += 6; break;
                    case VectorPath.K.Close: sb.Append('Z'); break;
                }
            return sb.ToString().Trim();
        }

        /// <summary>One standalone SVG page for a case, sized to the harness canvas so a screenshot is 1:1.
        /// Returns null for an inside / outside band: SVG 2 has no stroke alignment, so a browser is not a reference
        /// for it, for the same reason GDI+ and WPF are skipped there.</summary>
        public static string? SvgPage(Case cse)
        {
            if (cse.Align != 0) return null;
            string cap = cse.Cap == Cap.Round ? "round" : cse.Cap == Cap.Square ? "square" : "butt";
            string join = cse.Join == Join.Round ? "round" : cse.Join == Join.Bevel ? "bevel" : "miter";
            string dash = cse.Dash != null && cse.Dash.Length > 0
                ? " stroke-dasharray=\"" + string.Join(" ", cse.Dash.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))) +
                  "\" stroke-dashoffset=\"" + cse.DashOffset.ToString("0.###", CultureInfo.InvariantCulture) + "\""
                : "";
            return "<svg xmlns='http://www.w3.org/2000/svg' width='" + W + "' height='" + H + "' viewBox='0 0 " + W + " " + H + "'>" +
                   "<path d=\"" + SvgD(cse.Path) + "\" fill=\"none\" stroke=\"black\"" +
                   " stroke-width=\"" + cse.Width.ToString("0.###", CultureInfo.InvariantCulture) + "\"" +
                   " stroke-linecap=\"" + cap + "\" stroke-linejoin=\"" + join + "\"" +
                   " stroke-miterlimit=\"" + cse.Miter.ToString("0.###", CultureInfo.InvariantCulture) + "\"" + dash + "/></svg>";
        }

        /// <summary>Reads a browser screenshot back as a coverage buffer; null when it is not there or not 1:1.</summary>
        public static byte[]? ReadPng(string file)
        {
            if (!File.Exists(file)) return null;
            try
            {
                using var bmp = new Bitmap(file);
                if (bmp.Width != W || bmp.Height != H) return null;
                var ink = new byte[W * H];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        ink[y * W + x] = (byte)Math.Clamp(255 - Math.Round((c.R + c.G + c.B) / 3.0), 0, 255);
                    }
                return ink;
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ metrics
        public struct Metric
        {
            public double Excess, Missing;        // full-px of coverage the picture has too much / too little of
            public double MeanDev, MaxDev;        // px: how far a strongly mismatching pixel sits from the other edge
            public double Ink;                    // total truth coverage, the scale for the percentages
            public int Strong;                    // pixels whose coverage differs by more than half a pixel
            public int InkCount, GotCount;        // binarised ink counts, so an empty render is visible
            public double Share => Ink == 0 ? 0 : (Excess + Missing) * 100.0 / Ink;
            public override string ToString() =>
                $"excess {Excess,7:0.#} missing {Missing,7:0.#} share {Share,6:0.00}% dev {MeanDev,5:0.00}/{MaxDev,4:0.0} px over {Strong} px";
        }

        /// <summary>Compares a rendered coverage buffer with the truth buffer.
        ///
        /// The ink numbers are soft (coverage-weighted), not binarised: a 3 px wide stroke centred on an integer
        /// scanline covers its top and bottom pixel exactly half, so any "ink / no ink" threshold call at 128 makes
        /// those two rows arbitrary and would score a correct renderer as 25 % wrong. Summing |coverage difference|
        /// scores a half-covered pixel as half an error, which is what it is.
        ///
        /// The boundary deviation stays binarised, but only counts pixels whose coverage differs by more than half a
        /// pixel, so antialiasing ties are excluded and a real geometry error is not.</summary>
        public static Metric Compare(byte[] got, byte[] truth, int w, int h)
        {
            var m = new Metric();
            double excess = 0, missing = 0, ink = 0;
            for (int i = 0; i < truth.Length; i++)
            {
                double t = truth[i] / 255.0, g = got[i] / 255.0;
                ink += t;
                if (g > t) excess += g - t; else missing += t - g;
                m.InkCount += t >= 0.5 ? 1 : 0;
                m.GotCount += g >= 0.5 ? 1 : 0;
            }
            m.Excess = excess; m.Missing = missing; m.Ink = ink;
            var strong = new bool[w * h];
            for (int i = 0; i < truth.Length; i++) strong[i] = Math.Abs(got[i] - truth[i]) > 128;
            var tb = Boundary(truth, w, h);
            var gb = Boundary(got, w, h);
            var dist = DistanceField(tb, w, h);
            var distT = DistanceField(gb, w, h);
            double sum = 0; int n = 0;
            for (int i = 0; i < strong.Length; i++)
            {
                if (!strong[i]) continue;
                bool ti = truth[i] >= 128;
                double d = ti ? distT[i] : dist[i];            // distance from the missing/excess pixel to the other picture's edge
                sum += d; n++;
                if (d > m.MaxDev) m.MaxDev = d;
            }
            m.MeanDev = n == 0 ? 0 : sum / n;
            m.Strong = n;
            return m;
        }

        static bool[] Boundary(byte[] ink, int w, int h)
        {
            var b = new bool[ink.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x; bool on = ink[i] >= 128; bool edge = false;
                    if (x > 0 && (ink[i - 1] >= 128) != on) edge = true;
                    else if (x < w - 1 && (ink[i + 1] >= 128) != on) edge = true;
                    else if (y > 0 && (ink[i - w] >= 128) != on) edge = true;
                    else if (y < h - 1 && (ink[i + w] >= 128) != on) edge = true;
                    b[i] = edge;
                }
            return b;
        }

        /// <summary>Chamfer distance (px) from every pixel to the nearest seeded pixel: forward then backward pass.</summary>
        static double[] DistanceField(bool[] seed, int w, int h)
        {
            const double Big = 1e6;
            var d = new double[seed.Length];
            for (int i = 0; i < d.Length; i++) d[i] = seed[i] ? 0 : Big;
            void Relax(int x, int y)
            {
                int i = y * w + x; double best = d[i];
                for (int k = -1; k <= 1; k++)
                    for (int l = -1; l <= 1; l++)
                    {
                        if (k == 0 && l == 0) continue;
                        int nx = x + k, ny = y + l;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        double c = d[ny * w + nx] + (k != 0 && l != 0 ? 1.41421356 : 1.0);
                        if (c < best) best = c;
                    }
                d[i] = best;
            }
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) Relax(x, y);
            for (int y = h - 1; y >= 0; y--) for (int x = w - 1; x >= 0; x--) Relax(x, y);
            for (int i = 0; i < d.Length; i++) if (d[i] > 1e5) d[i] = 0;
            return d;
        }

        // ------------------------------------------------------------------ images
        /// <summary>Writes one PNG with the buffers side by side, ink drawn as black on white.</summary>
        public static void WritePng(string file, byte[][] inks, int w, int h, int scale)
        {
            int bw = w * scale * inks.Length + (inks.Length - 1) * scale, bh = h * scale;
            var buf = new int[bw * bh];
            for (int b = 0; b < inks.Length; b++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int v = 255 - inks[b][y * w + x];
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                                buf[((y * scale + sy) * bw) + (x + b * (w + 1)) * scale + sx] = unchecked((int)0xFF000000) | (v << 16) | (v << 8) | v;
                    }
            Save(file, buf, bw, bh);
        }

        /// <summary>Writes a red/green diff image: red = excess ink, green = missing ink, dark = both agree, white = neither.</summary>
        public static void WriteDiff(string file, byte[] got, byte[] truth, int w, int h, int scale)
        {
            var buf = new int[w * h];
            for (int i = 0; i < buf.Length; i++)
            {
                bool ti = truth[i] >= 128, gi = got[i] >= 128;
                buf[i] = gi && !ti ? unchecked((int)0xFF3030E0) : ti && !gi ? unchecked((int)0xFF40C020)
                       : ti ? unchecked((int)0xFF404040) : unchecked((int)0xFFFFFFFF);
            }
            var big = new int[w * scale * h * scale];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int sy = 0; sy < scale; sy++)
                        for (int sx = 0; sx < scale; sx++)
                            big[(y * scale + sy) * w * scale + x * scale + sx] = buf[y * w + x];
            Save(file, big, w * scale, h * scale);
        }

        static void Save(string file, int[] argb, int w, int h)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(argb, 0, data.Scan0, argb.Length);
            bmp.UnlockBits(data);
            bmp.Save(file, ImageFormat.Png);
        }
    }
}
