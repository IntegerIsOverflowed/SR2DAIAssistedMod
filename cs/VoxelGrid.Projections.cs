// SR2D - VoxelGrid from 2-D projections ("draw the front, the side and the top and get a model").
//
// The classic sprite-to-voxel trick: each view is a silhouette + colour map. A cell survives only
// when it is inside the silhouette of EVERY supplied view (the intersection of the extruded
// shapes), and takes its colour from the view that "sees" it (front, side or top - selectable,
// or nearest by depth). Opposite views (front + back, left + right, top + bottom) are supported
// too: the silhouette test uses both, and the colour comes from whichever face is closer; cells
// half-way between are blended with an ordered dither (VoxelBlend.Dither) or interpolated
// (VoxelBlend.Lerp) so two differently painted sides meet without a hard seam.
//
// Views may be any size: they are scaled to the grid (VoxelFit.Stretch / Proportional).
// Transparency: alpha 0 = outside (default threshold 1); with AlphaCutoff = 255 anything that is
// not fully opaque is outside; ColorKey treats one RGB as outside too.
using System;
using System.Collections.Generic;

namespace Sr2d64CSport
{
    /// <summary>Which side of the grid a projection sprite shows.</summary>
    internal enum VoxelView
    {
        /// <summary>Looking north from the south (-y): sprite x = grid x, sprite y (down) = grid -z. What you draw as the "front".</summary>
        Front,
        /// <summary>Looking south from the north (+y): sprite x = grid -x (mirrored), sprite y = -z.</summary>
        Back,
        /// <summary>Looking east from the west (-x): sprite x = grid y, sprite y = -z.</summary>
        Left,
        /// <summary>Looking west from the east (+x): sprite x = grid -y, sprite y = -z.</summary>
        Right,
        /// <summary>Looking down (+z): sprite x = grid x, sprite y (down) = grid -y (north up, like a map).</summary>
        Top,
        /// <summary>Looking up (-z): sprite x = grid x, sprite y = grid +y.</summary>
        Bottom,
    }

    /// <summary>How a view sprite whose size differs from the grid face is mapped onto it.</summary>
    internal enum VoxelFit
    {
        /// <summary>Stretch to the face (independent x / y factors).</summary>
        Stretch,
        /// <summary>Uniform scale (largest that fits), centred; the uncovered margin counts as outside.</summary>
        Proportional,
        /// <summary>1 sprite pixel = 1 voxel, centred; sprites larger than the face are cropped.</summary>
        Pixel,
    }

    /// <summary>How the colours of two opposite views meet in the middle of the model.</summary>
    internal enum VoxelBlend
    {
        /// <summary>Each cell takes the colour of the nearer view (hard seam half-way).</summary>
        Nearest,
        /// <summary>The two colours are mixed by an 8x8 ordered dither weighted by depth (a "screen door" transition, keeps original colours).</summary>
        Dither,
        /// <summary>Linear interpolation by depth (new intermediate colours).</summary>
        Lerp,
        /// <summary>Only the surface cells take their own view's colour; interior cells get <see cref="ProjectionOptions.InteriorColor"/> (or the front colour when it is null). Cheapest, right for shells.</summary>
        SurfaceOnly,
    }

    /// <summary>Options for <see cref="VoxelGrid.FromProjections(int, int, int, IEnumerable{(VoxelView, Sprite)}, ProjectionOptions?)"/>.</summary>
    internal sealed class ProjectionOptions
    {
        public VoxelFit Fit = VoxelFit.Stretch;
        /// <summary>Filter used when a view is resampled (Nearest keeps pixel art crisp; Bilinear / Area for photos).</summary>
        public SR2D.Filter Filter = SR2D.Filter.Nearest;
        /// <summary>Alpha below this = outside. 1 (default): only alpha 0 is transparent. 255: anything not fully opaque is outside.</summary>
        public int AlphaCutoff = 1;
        /// <summary>Optional colour key (0xRRGGBB, alpha ignored) that also counts as outside. -1 = none.</summary>
        public int ColorKey = -1;
        /// <summary>Colour-key tolerance (max channel difference).</summary>
        public int KeyTolerance = 0;
        /// <summary>Which view paints a cell when several see it: the one whose face is nearest to the cell (default) or a fixed priority order.</summary>
        public VoxelView[]? ColorPriority = null;
        /// <summary>How opposite views blend where their reach overlaps.</summary>
        public VoxelBlend Blend = VoxelBlend.Dither;
        /// <summary>Interior colour for <see cref="VoxelBlend.SurfaceOnly"/>; null = the nearest view's colour.</summary>
        public uint? InteriorColor = null;
        /// <summary>Material byte written into every solid cell (e.g. an object id). 0 = none.</summary>
        public byte Material = 0;
        /// <summary>
        /// Hollow the result: keep only cells within this many voxels of the surface (0 = solid). Big grids built from
        /// sprites are usually shells anyway - the inside is never seen and costs light-propagation time.
        /// </summary>
        public int Shell = 0;
        /// <summary>Emit strength given to cells whose source pixel is brighter than <see cref="EmissiveAbove"/> (0 = never).</summary>
        public byte EmissiveStrength = 0;
        /// <summary>Luma threshold (0..255) above which a pixel is treated as a light source when EmissiveStrength &gt; 0.</summary>
        public int EmissiveAbove = 250;
    }

    internal sealed unsafe partial class VoxelGrid
    {
        /// <summary>
        /// Build a grid from up to six view sprites (front / back / left / right / top / bottom). A cell is solid where
        /// every supplied view has an opaque pixel at its projection; its colour comes from the nearest view (or the
        /// priority order), opposite views blend by <see cref="ProjectionOptions.Blend"/>. Missing views constrain
        /// nothing (one front view alone extrudes the sprite through the whole depth).
        /// </summary>
        public static VoxelGrid FromProjections(int width, int height, int depth, IEnumerable<(VoxelView view, Sprite sprite)> views, ProjectionOptions? opt = null)
        {
            var g = new VoxelGrid(width, height, depth);
            g.ApplyProjections(views, opt);
            return g;
        }
        /// <summary>Front / side / top convenience overload (side = the left view, i.e. looking east). Any of the three may be null.</summary>
        public static VoxelGrid FromProjections(int width, int height, int depth, Sprite? front, Sprite? side, Sprite? top, ProjectionOptions? opt = null)
        {
            var l = new List<(VoxelView, Sprite)>();
            if (front != null) l.Add((VoxelView.Front, front));
            if (side != null) l.Add((VoxelView.Left, side));
            if (top != null) l.Add((VoxelView.Top, top));
            return FromProjections(width, height, depth, l, opt);
        }
        /// <summary>Grid sized after the sprites: width / height from the front view, depth from the side view (or the top view, or width).</summary>
        public static VoxelGrid FromProjections(Sprite? front, Sprite? side, Sprite? top, ProjectionOptions? opt = null)
        {
            int w = front?.Width ?? top?.Width ?? side?.Height ?? 1;
            int d = front?.Height ?? side?.Height ?? 1;
            int h = side?.Width ?? top?.Height ?? w;
            return FromProjections(w, h, d, front, side, top, opt);
        }

        /// <summary>Replace the grid contents with the intersection of the given views (see <see cref="FromProjections(int, int, int, IEnumerable{(VoxelView, Sprite)}, ProjectionOptions?)"/>).</summary>
        public VoxelGrid ApplyProjections(IEnumerable<(VoxelView view, Sprite sprite)> views, ProjectionOptions? opt = null)
        {
            opt ??= new ProjectionOptions();
            // resample every view onto its face (u = across, v = down in "view pixels" of face size)
            var maps = new List<ViewMap>();
            foreach (var (view, spr) in views)
            {
                if (spr == null) continue;
                var (fw, fh) = FaceSize(view);
                maps.Add(new ViewMap(view, Resample(spr, fw, fh, opt), fw, fh, opt));
            }
            if (maps.Count == 0) { Clear(); return this; }
            // ordering for colour choice
            var order = new List<ViewMap>();
            if (opt.ColorPriority != null) { foreach (var v in opt.ColorPriority) foreach (var m in maps) if (m.View == v && !order.Contains(m)) order.Add(m); }
            foreach (var m in maps) if (!order.Contains(m)) order.Add(m);
            bool byPriority = opt.ColorPriority != null && opt.ColorPriority.Length > 0;

            uint* cols = stackalloc uint[6]; float* dist = stackalloc float[6]; byte* em = stackalloc byte[6]; int* vw = stackalloc int[6];
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++)
            {
                Voxel* row = vox + (long)Index(0, y, z);
                for (int x = 0; x < w; x++)
                {
                    bool inside = true; int n = 0;
                    foreach (var m in order)
                    {
                        var (u, v, depthIn) = m.Map(x, y, z, this);
                        uint c = m.Pixel(u, v);
                        if (c == 0) { inside = false; break; }
                        cols[n] = c; dist[n] = depthIn; em[n] = m.IsBright(c) ? opt.EmissiveStrength : (byte)0; vw[n] = (int)m.View; n++;
                    }
                    if (!inside) { row[x] = Voxel.Empty; continue; }
                    // colour: nearest face (or first in priority); only OPPOSITE pairs blend (front/back, left/right, top/bottom) -
                    // adjacent views never mix, so each face keeps its own painting right up to the edge
                    int best = 0;
                    if (!byPriority) for (int i = 1; i < n; i++) if (dist[i] < dist[best]) best = i;
                    uint colour = cols[best]; byte emit = em[best];
                    if (n > 1 && opt.Blend != VoxelBlend.Nearest)
                    {
                        int second = -1, opp = vw[best] ^ 1;
                        for (int i = 0; i < n; i++) if (vw[i] == opp) { second = i; break; }
                        if (second >= 0 && cols[second] != colour)
                        {
                            float a = dist[best], b = dist[second], t = (a + b) > 0 ? a / (a + b) : 0f;   // 0 = right at best's face, 0.5 = half-way
                            switch (opt.Blend)
                            {
                                case VoxelBlend.Lerp: colour = LerpColour(colour, cols[second], t); break;
                                case VoxelBlend.Dither: if (t > Bayer8[(y & 7) * 8 + ((x + z) & 7)]) { colour = cols[second]; emit = em[second]; } break;
                                case VoxelBlend.SurfaceOnly: break;
                            }
                        }
                    }
                    row[x] = new Voxel(colour | 0xFF000000u, emit, opt.Material);
                }
            }
            Invalidate();
            if (opt.Blend == VoxelBlend.SurfaceOnly && opt.InteriorColor.HasValue)
            {
                using var surf = SelectSurface(); surf.Invert(); using var solid = SelectSolid(); surf.Combine(solid, SelectMode.Intersect);
                Paint(surf, opt.InteriorColor.Value);
            }
            if (opt.Shell > 0) Shell(opt.Shell);
            return this;
        }

        (int, int) FaceSize(VoxelView v) => v switch
        {
            VoxelView.Front or VoxelView.Back => (w, d),
            VoxelView.Left or VoxelView.Right => (h, d),
            _ => (w, h),
        };

        // scale a sprite onto a fw x fh map (0 = transparent) honouring Fit / AlphaCutoff / ColorKey
        static uint[] Resample(Sprite s, int fw, int fh, ProjectionOptions opt)
        {
            var map = new uint[fw * fh];
            int cut = Math.Clamp(opt.AlphaCutoff, 1, 255);
            using var tmp = new Sprite(fw, fh, SR2D.Op.Paint);
            tmp.ClearBuffer(0);
            if (s.Width == fw && s.Height == fh) tmp.Draw(s, 0, 0, SR2D.Op.Paint);
            else
            {
                int dw, dh, dx, dy;
                switch (opt.Fit)
                {
                    case VoxelFit.Proportional:
                        {
                            float k = MathF.Min(fw / (float)s.Width, fh / (float)s.Height);
                            dw = Math.Max(1, (int)MathF.Round(s.Width * k)); dh = Math.Max(1, (int)MathF.Round(s.Height * k));
                            dx = (fw - dw) / 2; dy = (fh - dh) / 2; break;
                        }
                    case VoxelFit.Pixel: dw = s.Width; dh = s.Height; dx = (fw - dw) / 2; dy = (fh - dh) / 2; break;
                    default: dw = fw; dh = fh; dx = 0; dy = 0; break;
                }
                if (dw == s.Width && dh == s.Height) tmp.Draw(s, dx, dy, SR2D.Op.Paint);
                else
                {
                    tmp.DrawScaled(s, dx, dy, dw, dh, SR2D.Op.Paint, opt.Filter);
                }
            }
            int* p = tmp.Ptr; int key = opt.ColorKey, tol = Math.Max(0, opt.KeyTolerance);
            for (int i = 0; i < map.Length; i++)
            {
                uint c = (uint)p[i];
                if ((c >> 24) < cut) continue;
                if (key >= 0 && KeyMatch(c, (uint)key, tol)) continue;
                map[i] = c | 0xFF000000u;
            }
            return map;
        }
        static bool KeyMatch(uint c, uint k, int tol)
        {
            int dr = Math.Abs((int)((c >> 16) & 255) - (int)((k >> 16) & 255)), dg = Math.Abs((int)((c >> 8) & 255) - (int)((k >> 8) & 255)), db = Math.Abs((int)(c & 255) - (int)(k & 255));
            return dr <= tol && dg <= tol && db <= tol;
        }
        static uint LerpColour(uint a, uint b, float t)
        {
            int ti = (int)(t * 256f + 0.5f); if (ti < 0) ti = 0; if (ti > 256) ti = 256;
            uint r = (((a >> 16) & 255) * (uint)(256 - ti) + ((b >> 16) & 255) * (uint)ti) >> 8;
            uint gg = (((a >> 8) & 255) * (uint)(256 - ti) + ((b >> 8) & 255) * (uint)ti) >> 8;
            uint bb = ((a & 255) * (uint)(256 - ti) + (b & 255) * (uint)ti) >> 8;
            return 0xFF000000u | (r << 16) | (gg << 8) | bb;
        }
        static readonly float[] Bayer8 = BuildBayer();
        static float[] BuildBayer()
        {
            var m = new float[64];
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                int v = 0, xc = x ^ y, yc = y;
                for (int bit = 0, mask = 4; bit < 3; bit++, mask >>= 1) { v <<= 2; v |= ((yc & mask) != 0 ? 1 : 0) | (((xc & mask) != 0 ? 1 : 0) << 1); }
                m[y * 8 + x] = (v + 0.5f) / 64f;
            }
            return m;
        }

        sealed class ViewMap
        {
            public readonly VoxelView View; readonly uint[] px; readonly int fw, fh; readonly int bright; readonly bool emissive;
            public ViewMap(VoxelView v, uint[] map, int w, int h, ProjectionOptions o) { View = v; px = map; fw = w; fh = h; bright = o.EmissiveAbove; emissive = o.EmissiveStrength > 0; }
            public uint Pixel(int u, int v) => (uint)u < (uint)fw && (uint)v < (uint)fh ? px[v * fw + u] : 0;
            public bool IsBright(uint c) => emissive && ((c >> 16 & 255) * 54 + (c >> 8 & 255) * 183 + (c & 255) * 19 >> 8) >= bright;
            /// <summary>Sprite pixel a cell projects to, and how deep the cell lies behind this view's face (0 = at the face).</summary>
            public (int u, int v, float depthIn) Map(int x, int y, int z, VoxelGrid g)
            {
                switch (View)
                {
                    case VoxelView.Front: return (x, g.d - 1 - z, y);
                    case VoxelView.Back: return (g.w - 1 - x, g.d - 1 - z, g.h - 1 - y);
                    case VoxelView.Left: return (y, g.d - 1 - z, x);
                    case VoxelView.Right: return (g.h - 1 - y, g.d - 1 - z, g.w - 1 - x);
                    case VoxelView.Top: return (x, g.h - 1 - y, g.d - 1 - z);
                    default: return (x, y, z);
                }
            }
        }

        /// <summary>
        /// The reverse: render the grid's silhouette + nearest colour as seen from a view, one pixel per voxel
        /// (what <see cref="FromProjections(Sprite, Sprite, Sprite, ProjectionOptions)"/> would want as input).
        /// Unlit, no shading - a colour map, not a picture; use DrawVoxels with a Side / TopDown camera for a picture.
        /// </summary>
        public Sprite ToProjection(VoxelView view)
        {
            var (fw, fh) = FaceSize(view);
            var s = new Sprite(fw, fh, SR2D.Op.Paint); s.ClearBuffer(0);
            int* p = s.Ptr;
            var m = new ViewMap(view, Array.Empty<uint>(), fw, fh, new ProjectionOptions());
            var depth = new float[fw * fh]; Array.Fill(depth, float.MaxValue);
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++)
            {
                Voxel* row = vox + (long)Index(0, y, z);
                for (int x = 0; x < w; x++)
                {
                    if (row[x].IsEmpty) continue;
                    var (u, v, di) = m.Map(x, y, z, this); int i = v * fw + u;
                    if (di < depth[i]) { depth[i] = di; p[i] = unchecked((int)(row[x].Argb | 0xFF000000u)); }
                }
            }
            return s;
        }
    }
}
