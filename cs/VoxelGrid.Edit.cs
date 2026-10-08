// VoxelGrid editing: 3-D selections, flood fill, solids (box / sphere / ellipsoid / cylinder / cone /
// line / tube), boolean and colour operations, invert, hollow / shell, noise-driven procedural fills.
//
//   var sel = g.SelectFlood(x, y, z, tolerance: 16);          // 3-D magic wand
//   g.Fill(sel, new Voxel(0xFF4080FF));                        // paint through it
//   g.Invert(sel, new Voxel(0xFF808080));                      // empty <-> solid (new solids need a colour)
//   g.Shell(1);                                                // keep only the outer 1-voxel crust
//   g.FillNoise(new VoxelNoise { Scale = 12, Threshold = 0.1f }, z => rock);   // caves / rock from value noise
//
// Every operation clips to the grid, invalidates the render caches and returns the grid (or the
// selection) for chaining. VoxelSelection is one byte per cell (0/1) with the same boolean ops as
// the 2-D Selection class (Replace / Add / Subtract / Intersect / Xor), Grow / Shrink, Invert,
// bounds and count; it is the "mask" for every editing call that takes one.
using System;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    /// <summary>A set of cells of a <see cref="VoxelGrid"/> (one byte per cell, 0 / 1).</summary>
    public sealed unsafe class VoxelSelection : IDisposable
    {
        internal readonly int w, h, d; internal readonly int cells;
        internal byte* p; bool disposed;
        int bx0 = int.MaxValue, by0 = int.MaxValue, bz0 = int.MaxValue, bx1 = -1, by1 = -1, bz1 = -1, count = -1;

        public VoxelSelection(int width, int height, int depth)
        {
            if (width <= 0 || height <= 0 || depth <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            long n = (long)width * height * depth; if (n > VoxelGrid.MaxCells) throw new ArgumentOutOfRangeException(nameof(width), "too large");
            w = width; h = height; d = depth; cells = (int)n;
            p = (byte*)NativeMemory.AllocZeroed((nuint)(cells + 64));
        }
        public VoxelSelection(VoxelGrid g) : this(g.Width, g.Height, g.Depth) { }

        public int Width => w; public int Height => h; public int Depth => d;
        public byte* Ptr => p;
        public Span<byte> Cells() => new Span<byte>(p, cells);
        public int Index(int x, int y, int z) => (z * h + y) * w + x;
        public bool Contains(int x, int y, int z) => (uint)x < (uint)w && (uint)y < (uint)h && (uint)z < (uint)d;
        public bool this[int x, int y, int z]
        {
            get => Contains(x, y, z) && p[Index(x, y, z)] != 0;
            set { if (Contains(x, y, z)) { p[Index(x, y, z)] = value ? (byte)1 : (byte)0; Dirty(); } }
        }
        internal void Dirty() { count = -1; }
        public bool SameSize(VoxelGrid g) => g.Width == w && g.Height == h && g.Depth == d;
        public bool SameSize(VoxelSelection s) => s.w == w && s.h == h && s.d == d;

        /// <summary>Number of selected cells.</summary>
        public int Count { get { if (count < 0) Recount(); return count; } }
        public bool IsEmpty => Count == 0;
        /// <summary>Inclusive min / exclusive max corner of the selected cells (empty rectangle when nothing is selected).</summary>
        public (int x0, int y0, int z0, int x1, int y1, int z1) Bounds { get { if (count < 0) Recount(); return count == 0 ? (0, 0, 0, 0, 0, 0) : (bx0, by0, bz0, bx1 + 1, by1 + 1, bz1 + 1); } }
        void Recount()
        {
            count = 0; bx0 = by0 = bz0 = int.MaxValue; bx1 = by1 = bz1 = -1;
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++)
            {
                byte* row = p + (long)Index(0, y, z);
                for (int x = 0; x < w; x++) if (row[x] != 0)
                {
                    count++;
                    if (x < bx0) bx0 = x; if (x > bx1) bx1 = x; if (y < by0) by0 = y; if (y > by1) by1 = y; if (z < bz0) bz0 = z; if (z > bz1) bz1 = z;
                }
            }
        }

        public VoxelSelection Clear() { NativeMemory.Clear(p, (nuint)cells); Dirty(); return this; }
        public VoxelSelection SelectAll() { Cells().Fill(1); Dirty(); return this; }
        public VoxelSelection Invert() { byte* q = p; for (int i = 0; i < cells; i++) q[i] ^= 1; Dirty(); return this; }
        public VoxelSelection Clone() { var s = new VoxelSelection(w, h, d); Buffer.MemoryCopy(p, s.p, cells, cells); return s; }

        /// <summary>Combine with another selection of the same size.</summary>
        public VoxelSelection Combine(VoxelSelection o, SelectMode mode)
        {
            if (!SameSize(o)) throw new ArgumentException("selection sizes differ");
            byte* a = p, b = o.p;
            switch (mode)
            {
                case SelectMode.Replace: Buffer.MemoryCopy(b, a, cells, cells); break;
                case SelectMode.Add: for (int i = 0; i < cells; i++) a[i] |= b[i]; break;
                case SelectMode.Subtract: for (int i = 0; i < cells; i++) a[i] &= (byte)(b[i] ^ 1); break;
                case SelectMode.Intersect: for (int i = 0; i < cells; i++) a[i] &= b[i]; break;
                default: for (int i = 0; i < cells; i++) a[i] ^= b[i]; break;
            }
            Dirty(); return this;
        }

        /// <summary>Set the cells for which <paramref name="test"/> is true (with the given mode against the current selection).</summary>
        public VoxelSelection Where(Func<int, int, int, bool> test, SelectMode mode = SelectMode.Replace)
        {
            using var t = new VoxelSelection(w, h, d);
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (test(x, y, z)) t.p[Index(x, y, z)] = 1;
            return Combine(t, mode);
        }

        /// <summary>Axis-aligned box [x0, x1) x [y0, y1) x [z0, z1).</summary>
        public VoxelSelection Box(int x0, int y0, int z0, int x1, int y1, int z1, SelectMode mode = SelectMode.Replace)
        {
            using var t = new VoxelSelection(w, h, d);
            x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); z0 = Math.Max(z0, 0); x1 = Math.Min(x1, w); y1 = Math.Min(y1, h); z1 = Math.Min(z1, d);
            for (int z = z0; z < z1; z++) for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) t.p[Index(x, y, z)] = 1;
            return Combine(t, mode);
        }

        /// <summary>Grow by one cell in the 6 (or 26 with <paramref name="diagonal"/>) directions, <paramref name="n"/> times.</summary>
        public VoxelSelection Grow(int n = 1, bool diagonal = false) { for (int i = 0; i < n; i++) Morph(true, diagonal); return this; }
        /// <summary>Shrink by one cell (a selected cell stays only if all its neighbours are selected).</summary>
        public VoxelSelection Shrink(int n = 1, bool diagonal = false) { for (int i = 0; i < n; i++) Morph(false, diagonal); return this; }
        /// <summary>Keep only the selected cells that touch an unselected one (or the grid edge): the crust of the selection.</summary>
        public VoxelSelection Border(bool diagonal = false) { using var inner = Clone().Shrink(1, diagonal); return Combine(inner, SelectMode.Subtract); }
        void Morph(bool grow, bool diagonal)
        {
            using var src = Clone();
            byte* s = src.p, o = p;
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = Index(x, y, z);
                if (s[i] != 0 == grow) continue;        // growing: only unselected cells can change; shrinking: only selected ones
                bool hit = false;
                if (!diagonal)
                {
                    hit = Nb(s, x + 1, y, z, grow) || Nb(s, x - 1, y, z, grow) || Nb(s, x, y + 1, z, grow) || Nb(s, x, y - 1, z, grow) || Nb(s, x, y, z + 1, grow) || Nb(s, x, y, z - 1, grow);
                }
                else
                {
                    for (int dz = -1; dz <= 1 && !hit; dz++) for (int dy = -1; dy <= 1 && !hit; dy++) for (int dx = -1; dx <= 1 && !hit; dx++)
                        if ((dx | dy | dz) != 0 && Nb(s, x + dx, y + dy, z + dz, grow)) hit = true;
                }
                if (hit) o[i] = grow ? (byte)1 : (byte)0;
            }
            Dirty();
        }
        // grow: neighbour selected?  shrink: neighbour unselected or outside?
        bool Nb(byte* s, int x, int y, int z, bool grow) => Contains(x, y, z) ? (s[Index(x, y, z)] != 0) == grow : !grow;

        public void Dispose() { if (disposed) return; disposed = true; if (p != null) { NativeMemory.Free(p); p = null; } GC.SuppressFinalize(this); }
        ~VoxelSelection() { if (!disposed && p != null) NativeMemory.Free(p); }
    }

    /// <summary>Parameters of the procedural fills (<see cref="VoxelGrid.FillNoise"/>, <see cref="VoxelGrid.Noise"/>).</summary>
    public sealed class VoxelNoise
    {
        /// <summary>Feature size in voxels (period of the base octave).</summary>
        public float Scale = 16f;
        /// <summary>Octaves of fractal detail (1 = smooth blobs).</summary>
        public int Octaves = 3;
        /// <summary>Amplitude falloff per octave.</summary>
        public float Persistence = 0.5f;
        /// <summary>Frequency growth per octave.</summary>
        public float Lacunarity = 2f;
        public int Seed = 1;
        /// <summary>Cells with noise &gt; Threshold are filled (noise is roughly -1..1; 0 = half full).</summary>
        public float Threshold = 0f;
        /// <summary>Use |noise| (ridged / turbulence look: veins and cracks).</summary>
        public bool Turbulence = false;
        /// <summary>Add this * (z / Depth - 0.5) to the noise before thresholding: negative = denser at the bottom (terrain), 0 = uniform.</summary>
        public float Gradient = 0f;

        // Perlin-style gradient noise, integer hashed, deterministic across platforms
        static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
        static uint Hash(int x, int y, int z, int seed)
        {
            uint hsh = (uint)(x * 374761393) + (uint)(y * 668265263) + (uint)(z * 2147483647) + (uint)(seed * 1274126177);
            hsh = (hsh ^ (hsh >> 13)) * 1274126177u; return hsh ^ (hsh >> 16);
        }
        static float Grad(uint hsh, float x, float y, float z)
        {
            switch (hsh & 15)
            {
                case 0: return x + y; case 1: return -x + y; case 2: return x - y; case 3: return -x - y;
                case 4: return x + z; case 5: return -x + z; case 6: return x - z; case 7: return -x - z;
                case 8: return y + z; case 9: return -y + z; case 10: return y - z; case 11: return -y - z;
                case 12: return y + x; case 13: return -y + z; case 14: return y - x; default: return -y - z;
            }
        }
        /// <summary>One octave of gradient noise, about -1..1.</summary>
        public static float Perlin(float x, float y, float z, int seed)
        {
            int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y), zi = (int)MathF.Floor(z);
            float xf = x - xi, yf = y - yi, zf = z - zi, u = Fade(xf), v = Fade(yf), ww = Fade(zf);
            float n000 = Grad(Hash(xi, yi, zi, seed), xf, yf, zf), n100 = Grad(Hash(xi + 1, yi, zi, seed), xf - 1, yf, zf);
            float n010 = Grad(Hash(xi, yi + 1, zi, seed), xf, yf - 1, zf), n110 = Grad(Hash(xi + 1, yi + 1, zi, seed), xf - 1, yf - 1, zf);
            float n001 = Grad(Hash(xi, yi, zi + 1, seed), xf, yf, zf - 1), n101 = Grad(Hash(xi + 1, yi, zi + 1, seed), xf - 1, yf, zf - 1);
            float n011 = Grad(Hash(xi, yi + 1, zi + 1, seed), xf, yf - 1, zf - 1), n111 = Grad(Hash(xi + 1, yi + 1, zi + 1, seed), xf - 1, yf - 1, zf - 1);
            float x00 = n000 + u * (n100 - n000), x10 = n010 + u * (n110 - n010), x01 = n001 + u * (n101 - n001), x11 = n011 + u * (n111 - n011);
            float y0 = x00 + v * (x10 - x00), y1 = x01 + v * (x11 - x01);
            return (y0 + ww * (y1 - y0)) * 0.87f;   // 3-D Perlin extremes ~ +-1.15 -> ~ -1..1
        }
        /// <summary>Fractal noise at grid coordinates with these settings (Scale / Octaves / Persistence / Lacunarity / Seed / Turbulence).</summary>
        public float Sample(float x, float y, float z)
        {
            float f = 1f / MathF.Max(0.01f, Scale), amp = 1f, sum = 0f, norm = 0f;
            for (int o = 0; o < Math.Max(1, Octaves); o++)
            {
                float n = Perlin(x * f + 0.31f, y * f + 0.17f, z * f + 0.53f, Seed + o * 7919);
                if (Turbulence) n = MathF.Abs(n);
                sum += n * amp; norm += amp; amp *= Persistence; f *= Lacunarity;
            }
            return sum / norm;
        }
    }

    public sealed unsafe partial class VoxelGrid
    {
        // ---- selections -----------------------------------------------------------------------------

        /// <summary>Empty selection sized to this grid.</summary>
        public VoxelSelection NewSelection() => new VoxelSelection(this);

        /// <summary>
        /// 3-D magic wand: the cells connected to (x, y, z) that match it - empty matches empty, solid matches solid within
        /// <paramref name="tolerance"/> (max channel difference) or with the same <see cref="Voxel.Material"/> when <paramref name="byMaterial"/>.
        /// </summary>
        public VoxelSelection SelectFlood(int x, int y, int z, int tolerance = 0, bool diagonal = false, bool byMaterial = false, VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
            => FloodInto(into, mode, x, y, z, 0, 0, tolerance, (diagonal ? 4 : 0) | (byMaterial ? 64 : 0));

        /// <summary>Every cell of the grid matching <paramref name="like"/> (colour within tolerance, or same material), connected or not.</summary>
        public VoxelSelection SelectByColor(Voxel like, int tolerance = 0, bool byMaterial = false, VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
            => FloodInto(into, mode, 0, 0, 0, like.Argb, like.Material, tolerance, 1 | 16 | (byMaterial ? 64 : 0));

        /// <summary>All solid cells.</summary>
        public VoxelSelection SelectSolid(VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
        {
            var s = into ?? NewSelection(); using var t = new VoxelSelection(this);
            Voxel* v = vox; byte* m = t.p; for (int i = 0; i < cells; i++) m[i] = (byte)(v[i].Argb >> 24 != 0 ? 1 : 0);
            return s.Combine(t, mode);
        }
        /// <summary>All empty cells.</summary>
        public VoxelSelection SelectEmpty(VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
        {
            var s = into ?? NewSelection(); using var t = new VoxelSelection(this);
            Voxel* v = vox; byte* m = t.p; for (int i = 0; i < cells; i++) m[i] = (byte)(v[i].Argb >> 24 == 0 ? 1 : 0);
            return s.Combine(t, mode);
        }

        /// <summary>The empty cells reachable from the grid boundary ("outside air"). Its complement among the empty cells = enclosed cavities.</summary>
        public VoxelSelection SelectOutside(bool diagonal = false, VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
            => FloodInto(into, mode, 0, 0, 0, 0, 0, 0, 32 | 16 | (diagonal ? 4 : 0));   // OUTSIDE | REF with an empty reference

        /// <summary>Enclosed empty space: empty cells not connected to the boundary.</summary>
        public VoxelSelection SelectCavities(VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
        {
            using var outside = SelectOutside(); using var empty = SelectEmpty();
            empty.Combine(outside, SelectMode.Subtract);
            return (into ?? NewSelection()).Combine(empty, mode);
        }

        /// <summary>Solid cells with at least one exposed side (the visible surface).</summary>
        public VoxelSelection SelectSurface(VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
        {
            UpdateFaces(); var s = into ?? NewSelection(); using var t = new VoxelSelection(this);
            for (int i = 0; i < cells; i++) t.p[i] = (byte)((faces[i] & 0x40) != 0 && (faces[i] & 63) != 0 ? 1 : 0);
            return s.Combine(t, mode);
        }

        VoxelSelection FloodInto(VoxelSelection? into, SelectMode mode, int x, int y, int z, uint refc, int refm, int tol, int flags)
        {
            var s = into ?? NewSelection();
            if (!s.SameSize(this)) throw new ArgumentException("selection size differs from the grid");
            if (mode == SelectMode.Replace)
            {
                int n = SR2D.Native.VoxelFlood(vox, w, h, d, x, y, z, refc, refm, tol, flags, s.p);
                if (n < 0) throw new OutOfMemoryException("VOXEL_FLOOD");
                s.Dirty(); return s;
            }
            using var t = new VoxelSelection(this);
            if (SR2D.Native.VoxelFlood(vox, w, h, d, x, y, z, refc, refm, tol, flags, t.p) < 0) throw new OutOfMemoryException("VOXEL_FLOOD");
            return s.Combine(t, mode);
        }

        // ---- painting through selections -------------------------------------------------------------

        /// <summary>Set every selected cell to <paramref name="v"/> (Voxel.Empty erases).</summary>
        public VoxelGrid Fill(VoxelSelection sel, Voxel v)
        {
            CheckSel(sel); byte* m = sel.p; Voxel* p = vox;
            for (int i = 0; i < cells; i++) if (m[i] != 0) p[i] = v;
            Invalidate(); return this;
        }
        /// <summary>Recolour the selected SOLID cells (keeps emit / material / user).</summary>
        public VoxelGrid Paint(VoxelSelection sel, uint argb)
        {
            CheckSel(sel); byte* m = sel.p; Voxel* p = vox; argb |= 0xFF000000u;
            for (int i = 0; i < cells; i++) if (m[i] != 0 && p[i].IsSolid) p[i].Argb = argb;
            Invalidate(); return this;
        }
        /// <summary>Apply <paramref name="f"/> to every selected cell (or every cell when <paramref name="sel"/> is null).</summary>
        public VoxelGrid Map(Func<int, int, int, Voxel, Voxel> f, VoxelSelection? sel = null)
        {
            if (sel != null) CheckSel(sel);
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = Index(x, y, z); if (sel != null && sel.p[i] == 0) continue;
                vox[i] = f(x, y, z, vox[i]);
            }
            Invalidate(); return this;
        }
        /// <summary>Set emit / material / user on the selected solid cells (pass null to keep a field).</summary>
        public VoxelGrid SetData(VoxelSelection sel, byte? emit = null, byte? material = null, ushort? user = null)
        {
            CheckSel(sel); byte* m = sel.p; Voxel* p = vox;
            for (int i = 0; i < cells; i++) if (m[i] != 0 && p[i].IsSolid) { if (emit.HasValue) p[i].Emit = emit.Value; if (material.HasValue) p[i].Material = material.Value; if (user.HasValue) p[i].User = user.Value; }
            Invalidate(); return this;
        }
        /// <summary>Erase the selected cells.</summary>
        public VoxelGrid Erase(VoxelSelection sel) => Fill(sel, Voxel.Empty);
        /// <summary>Copy the selected cells out into a new grid of the selection's bounds (unselected cells stay empty).</summary>
        public VoxelGrid Extract(VoxelSelection sel)
        {
            CheckSel(sel); var b = sel.Bounds;
            var g = new VoxelGrid(Math.Max(1, b.x1 - b.x0), Math.Max(1, b.y1 - b.y0), Math.Max(1, b.z1 - b.z0));
            for (int z = b.z0; z < b.z1; z++) for (int y = b.y0; y < b.y1; y++) for (int x = b.x0; x < b.x1; x++)
                if (sel.p[Index(x, y, z)] != 0) g.vox[g.Index(x - b.x0, y - b.y0, z - b.z0)] = vox[Index(x, y, z)];
            g.Invalidate(); return g;
        }
        void CheckSel(VoxelSelection sel) { if (!sel.SameSize(this)) throw new ArgumentException("selection size differs from the grid"); }

        /// <summary>
        /// Invert solidity: empty cells become <paramref name="newSolid"/> (they have no colour of their own - this is the colour they get),
        /// solid cells become empty. Restricted to <paramref name="sel"/> when given. The old colours of the removed cells are lost.
        /// </summary>
        public VoxelGrid Invert(Voxel newSolid, VoxelSelection? sel = null)
        {
            if (sel != null) CheckSel(sel);
            if (newSolid.IsEmpty) newSolid.Argb |= 0xFF000000u;
            Voxel* p = vox;
            for (int i = 0; i < cells; i++) { if (sel != null && sel.p[i] == 0) continue; p[i] = p[i].IsSolid ? Voxel.Empty : newSolid; }
            Invalidate(); return this;
        }

        /// <summary>Flood fill from a cell: replace the connected matching region (see <see cref="SelectFlood"/>) with <paramref name="v"/>.</summary>
        public VoxelGrid FloodFill(int x, int y, int z, Voxel v, int tolerance = 0, bool diagonal = false)
        {
            if (!Contains(x, y, z)) return this;
            using var s = SelectFlood(x, y, z, tolerance, diagonal); return Fill(s, v);
        }
        /// <summary>Fill enclosed cavities (empty space not connected to the outside) - turns a closed shell into a solid.</summary>
        public VoxelGrid FillCavities(Voxel v) { using var s = SelectCavities(); return Fill(s, v); }
        /// <summary>Keep only the outer <paramref name="thickness"/> cells of every solid body (from the outside air inwards); the inside becomes empty.</summary>
        public VoxelGrid Shell(int thickness = 1)
        {
            using var outside = SelectOutside();
            outside.Grow(Math.Max(1, thickness));           // outside air + the crust
            using var inner = SelectSolid(); inner.Combine(outside, SelectMode.Subtract);   // solid cells not within the crust
            return Erase(inner);
        }
        /// <summary>Hollow the solid bodies but keep enclosed cavities as they are (thickness measured from any empty cell).</summary>
        public VoxelGrid Hollow(int thickness = 1)
        {
            using var empty = SelectEmpty(); empty.Grow(Math.Max(1, thickness));
            using var inner = SelectSolid(); inner.Combine(empty, SelectMode.Subtract);
            return Erase(inner);
        }
        /// <summary>Remove solid cells that have no solid neighbour at all (dust).</summary>
        public VoxelGrid RemoveIsolated()
        {
            UpdateFaces();
            for (int i = 0; i < cells; i++) if ((faces[i] & 0x7F) == 0x7F) vox[i] = Voxel.Empty;
            Invalidate(); return this;
        }

        // ---- solids ---------------------------------------------------------------------------------

        /// <summary>
        /// Solid ellipsoid with radii (rx, ry, rz) around a centre. Coordinates are continuous: cell (i, j, k) spans
        /// [i, i+1) on each axis and its centre is at i + 0.5, so a sphere centred on a cell centre is (cx + 0.5, ...).
        /// A cell is filled when its centre lies inside; radii below 0.5 still fill the cell holding the centre.
        /// </summary>
        public VoxelGrid FillEllipsoid(float cx, float cy, float cz, float rx, float ry, float rz, Voxel v, VoxelSelection? sel = null)
        {
            int x0 = Math.Max(0, (int)MathF.Floor(cx - rx - 1)), x1 = Math.Min(w - 1, (int)MathF.Ceiling(cx + rx + 1));
            int y0 = Math.Max(0, (int)MathF.Floor(cy - ry - 1)), y1 = Math.Min(h - 1, (int)MathF.Ceiling(cy + ry + 1));
            int z0 = Math.Max(0, (int)MathF.Floor(cz - rz - 1)), z1 = Math.Min(d - 1, (int)MathF.Ceiling(cz + rz + 1));
            float ix = 1f / MathF.Max(rx, 0.5f), iy = 1f / MathF.Max(ry, 0.5f), iz = 1f / MathF.Max(rz, 0.5f);
            for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - cx) * ix, dy = (y + 0.5f - cy) * iy, dz = (z + 0.5f - cz) * iz;
                if (dx * dx + dy * dy + dz * dz <= 1f) Put(x, y, z, v, sel);
            }
            Invalidate(); return this;
        }
        /// <summary>Hollow sphere of the given wall thickness.</summary>
        public VoxelGrid ShellSphere(float cx, float cy, float cz, float r, float thickness, Voxel v, VoxelSelection? sel = null)
        {
            FillSphere(cx, cy, cz, r, v, sel);
            if (r - thickness > 0) FillSphere(cx, cy, cz, r - thickness, Voxel.Empty, sel);
            return this;
        }
        /// <summary>Solid sphere restricted to a selection.</summary>
        public VoxelGrid FillSphere(float cx, float cy, float cz, float r, Voxel v, VoxelSelection? sel) => FillEllipsoid(cx, cy, cz, r, r, r, v, sel);

        /// <summary>
        /// Cylinder / cone / capsule between two points with radius r0 at the start and r1 at the end (equal = cylinder, one zero = cone).
        /// <paramref name="round"/> adds hemispherical caps (a capsule). Same continuous coordinates as FillEllipsoid
        /// (cell centres at i + 0.5); cells whose centre is within the radius are filled, radius below 0.5 gives a 1-cell line.
        /// </summary>
        public VoxelGrid FillCylinder(float x0, float y0, float z0, float x1, float y1, float z1, float r0, float r1, Voxel v, bool round = false, VoxelSelection? sel = null)
        {
            float ax = x1 - x0, ay = y1 - y0, az = z1 - z0, len2 = ax * ax + ay * ay + az * az;
            float rmax = MathF.Max(r0, r1) + 1f;
            int bx0 = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, x1) - rmax)), bx1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(x0, x1) + rmax));
            int by0 = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, y1) - rmax)), by1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(y0, y1) + rmax));
            int bz0 = Math.Max(0, (int)MathF.Floor(MathF.Min(z0, z1) - rmax)), bz1 = Math.Min(d - 1, (int)MathF.Ceiling(MathF.Max(z0, z1) + rmax));
            for (int z = bz0; z <= bz1; z++) for (int y = by0; y <= by1; y++) for (int x = bx0; x <= bx1; x++)
            {
                float px = x + 0.5f - x0, py = y + 0.5f - y0, pz = z + 0.5f - z0;
                float t = len2 > 0 ? (px * ax + py * ay + pz * az) / len2 : 0f;
                float tc = Math.Clamp(t, 0f, 1f);
                if (!round && (t < 0f || t > 1f)) continue;
                float qx = px - ax * tc, qy = py - ay * tc, qz = pz - az * tc;
                float r = MathF.Max(r0 + (r1 - r0) * tc, 0.5f);
                if (qx * qx + qy * qy + qz * qz <= r * r) Put(x, y, z, v, sel);
            }
            Invalidate(); return this;
        }
        /// <summary>Vertical cylinder (axis along z) filling the cell layers zBottom .. zTop - 1 - the common case.</summary>
        public VoxelGrid FillCylinderZ(float cx, float cy, int zBottom, int zTop, float r, Voxel v, VoxelSelection? sel = null)
            => FillCylinder(cx, cy, zBottom, cx, cy, zTop - 0.001f, r, r, v, false, sel);
        /// <summary>Cone standing on layer zBottom with base radius r, apex at zTop (layers zBottom .. zTop - 1).</summary>
        public VoxelGrid FillCone(float cx, float cy, int zBottom, int zTop, float r, Voxel v, VoxelSelection? sel = null)
            => FillCylinder(cx, cy, zBottom, cx, cy, zTop - 0.001f, r, 0f, v, false, sel);
        /// <summary>Line of voxels (3-D Bresenham) with an optional thickness (radius).</summary>
        public VoxelGrid DrawLine(int x0, int y0, int z0, int x1, int y1, int z1, Voxel v, float radius = 0f, VoxelSelection? sel = null)
        {
            if (radius > 0f) return FillCylinder(x0 + 0.5f, y0 + 0.5f, z0 + 0.5f, x1 + 0.5f, y1 + 0.5f, z1 + 0.5f, radius, radius, v, true, sel);
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0), dz = Math.Abs(z1 - z0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, sz = z0 < z1 ? 1 : -1;
            int dm = Math.Max(dx, Math.Max(dy, dz)), x = x0, y = y0, z = z0, ex = dm / 2, ey = dm / 2, ez = dm / 2;
            for (int i = 0; i <= dm; i++)
            {
                Put(x, y, z, v, sel);
                ex -= dx; if (ex < 0) { ex += dm; x += sx; }
                ey -= dy; if (ey < 0) { ey += dm; y += sy; }
                ez -= dz; if (ez < 0) { ez += dm; z += sz; }
            }
            Invalidate(); return this;
        }
        /// <summary>Box outline (walls only) of the given wall thickness.</summary>
        public VoxelGrid ShellBox(int x0, int y0, int z0, int x1, int y1, int z1, int thickness, Voxel v, VoxelSelection? sel = null)
        {
            FillBox(x0, y0, z0, x1, y1, z1, v, sel);
            int t = Math.Max(1, thickness);
            if (x1 - x0 > 2 * t && y1 - y0 > 2 * t && z1 - z0 > 2 * t) FillBox(x0 + t, y0 + t, z0 + t, x1 - t, y1 - t, z1 - t, Voxel.Empty, sel);
            return this;
        }
        /// <summary>Box restricted to a selection.</summary>
        public VoxelGrid FillBox(int x0, int y0, int z0, int x1, int y1, int z1, Voxel v, VoxelSelection? sel)
        {
            if (sel == null) return FillBox(x0, y0, z0, x1, y1, z1, v);
            x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); z0 = Math.Max(z0, 0); x1 = Math.Min(x1, w); y1 = Math.Min(y1, h); z1 = Math.Min(z1, d);
            for (int z = z0; z < z1; z++) for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) Put(x, y, z, v, sel);
            Invalidate(); return this;
        }
        /// <summary>Solid torus in the plane z = cz (major radius R, tube radius r).</summary>
        public VoxelGrid FillTorus(float cx, float cy, float cz, float R, float r, Voxel v, VoxelSelection? sel = null)
        {
            int x0 = Math.Max(0, (int)(cx - R - r - 1)), x1 = Math.Min(w - 1, (int)(cx + R + r + 1)), y0 = Math.Max(0, (int)(cy - R - r - 1)), y1 = Math.Min(h - 1, (int)(cy + R + r + 1));
            int z0 = Math.Max(0, (int)(cz - r - 1)), z1 = Math.Min(d - 1, (int)(cz + r + 1)); float rr = MathF.Max(r, 0.5f) * MathF.Max(r, 0.5f);
            for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy, dz = z + 0.5f - cz, q = MathF.Sqrt(dx * dx + dy * dy) - R;
                if (q * q + dz * dz <= rr) Put(x, y, z, v, sel);
            }
            Invalidate(); return this;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        void Put(int x, int y, int z, Voxel v, VoxelSelection? sel)
        {
            if (!Contains(x, y, z)) return;
            int i = Index(x, y, z);
            if (sel != null && sel.p[i] == 0) return;
            vox[i] = v;
        }

        // ---- transforms ------------------------------------------------------------------------------

        /// <summary>Move the contents by whole cells (what leaves the grid is lost, what enters is empty).</summary>
        public VoxelGrid Translate(int dx, int dy, int dz) { using var c = Clone(); Clear(); Paste(c, dx, dy, dz, copyEmpty: true); ObjectsTranslate(dx, dy, dz); return this; }
        /// <summary>Mirror along an axis (0 x, 1 y, 2 z).</summary>
        public VoxelGrid Mirror(int axis)
        {
            using var c = Clone();
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                vox[Index(x, y, z)] = c.vox[c.Index(axis == 0 ? w - 1 - x : x, axis == 1 ? h - 1 - y : y, axis == 2 ? d - 1 - z : z)];
            ObjectsMirror(axis);
            Invalidate(); return this;
        }
        // ---- in-place turns: the grid is rewritten cell by cell through a coordinate map; the objects follow
        // srcOf(x, y, z) = the OLD cell that lands at NEW (x, y, z); (nw, nh, nd) = the new size (same cell count)
        VoxelGrid Remap(int nw, int nh, int nd, Func<int, int, int, (int x, int y, int z)> srcOf)
        {
            if ((long)nw * nh * nd != cells) throw new ArgumentException("remap changes the cell count");
            var nv = (Voxel*)NativeMemory.AllocZeroed((nuint)(cells * 8 + 64));
            int ow = w, oh = h;
            for (int z = 0; z < nd; z++) for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
            {
                var (sx, sy, sz) = srcOf(x, y, z);
                nv[((long)z * nh + y) * nw + x] = vox[((long)sz * oh + sy) * ow + sx];
            }
            foreach (var o in Objects)
            {
                if (o.Cells != null && o.Cells.SameSize(this))
                {
                    var t = new VoxelSelection(nw, nh, nd);
                    for (int z = 0; z < nd; z++) for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++) { var (sx, sy, sz) = srcOf(x, y, z); t.p[t.Index(x, y, z)] = o.Cells.p[o.Cells.Index(sx, sy, sz)]; }
                    t.Dirty(); o.Cells.Dispose(); o.Cells = t;
                }
                if (o.stash != null && o.stashW == w && o.stashH == h && o.stashD == d)
                {   // stashed (hidden) cells: find where each old index lands by inverting the map once over the new grid
                    var where = new System.Collections.Generic.Dictionary<int, int>(o.stash.Count);
                    for (int k = 0; k < o.stash.Count; k++) where[o.stash[k].index] = k;
                    var moved = new (int index, Voxel v)[o.stash.Count];
                    for (int z = 0; z < nd; z++) for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
                    {
                        var (sx, sy, sz) = srcOf(x, y, z);
                        if (where.TryGetValue((sz * oh + sy) * ow + sx, out int k)) moved[k] = ((z * nh + y) * nw + x, o.stash[k].v);
                    }
                    o.stash.Clear(); o.stash.AddRange(moved); o.stashW = nw; o.stashH = nh; o.stashD = nd;
                }
            }
            NativeMemory.Free(vox); vox = nv;
            w = nw; h = nh; d = nd;
            if (light != null) { NativeMemory.Free(light); light = null; }
            Invalidate(); return this;
        }
        /// <summary>Quarter turns about the z axis (vertical), IN PLACE: positive = counter-clockwise seen from +z (the same sense as <see cref="RotatedZ"/>); Width and Height swap for odd turns.</summary>
        public VoxelGrid RotateZ(int quarterTurns = 1)
        {
            quarterTurns &= 3; if (quarterTurns == 0) return this;
            int ow = w, oh = h;
            return quarterTurns switch
            {   // new (x, y) = (-y, x) per CCW turn  ->  old = inverse
                1 => Remap(oh, ow, d, (x, y, z) => (y, oh - 1 - x, z)),
                2 => Remap(ow, oh, d, (x, y, z) => (ow - 1 - x, oh - 1 - y, z)),
                _ => Remap(oh, ow, d, (x, y, z) => (ow - 1 - y, x, z)),
            };
        }
        /// <summary>Quarter turns about the x axis, IN PLACE (positive = y toward z); Height and Depth swap for odd turns.</summary>
        public VoxelGrid RotateX(int quarterTurns = 1)
        {
            quarterTurns &= 3; if (quarterTurns == 0) return this;
            int oh = h, od = d;
            return quarterTurns switch
            {
                1 => Remap(w, od, oh, (x, y, z) => (x, z, od - 1 - y)),
                2 => Remap(w, oh, od, (x, y, z) => (x, oh - 1 - y, od - 1 - z)),
                _ => Remap(w, od, oh, (x, y, z) => (x, oh - 1 - z, y)),
            };
        }
        /// <summary>Quarter turns about the y axis, IN PLACE (positive = z toward x); Width and Depth swap for odd turns.</summary>
        public VoxelGrid RotateY(int quarterTurns = 1)
        {
            quarterTurns &= 3; if (quarterTurns == 0) return this;
            int ow = w, od = d;
            return quarterTurns switch
            {
                1 => Remap(od, h, ow, (x, y, z) => (z, y, ow - 1 - x)),
                2 => Remap(ow, h, od, (x, y, z) => (ow - 1 - x, y, od - 1 - z)),
                _ => Remap(od, h, ow, (x, y, z) => (ow - 1 - z, y, x)),
            };
        }
        /// <summary>Mirror left-right (x), in place. Same as Mirror(0).</summary>
        public VoxelGrid FlipX() => Mirror(0);
        /// <summary>Mirror front-back (y), in place. Same as Mirror(1).</summary>
        public VoxelGrid FlipY() => Mirror(1);
        /// <summary>Mirror top-bottom (z), in place. Same as Mirror(2).</summary>
        public VoxelGrid FlipZ() => Mirror(2);
        /// <summary>Crops IN PLACE to the solid bounds (plus <paramref name="margin"/> cells); an empty grid becomes 1x1x1.</summary>
        public VoxelGrid Trim(int margin = 0)
        {
            using var s = SelectSolid();
            if (s.IsEmpty) return this;
            var b = s.Bounds;
            int x0 = Math.Max(0, b.x0 - margin), y0 = Math.Max(0, b.y0 - margin), z0 = Math.Max(0, b.z0 - margin);
            int x1 = Math.Min(w, b.x1 + margin), y1 = Math.Min(h, b.y1 + margin), z1 = Math.Min(d, b.z1 + margin);
            return Recanvas(-x0, -y0, -z0, x1 - x0, y1 - y0, z1 - z0);
        }
        /// <summary>
        /// Changes the grid size IN PLACE: the new grid is nw x nh x nd and the old cell (0,0,0) lands at
        /// (dx, dy, dz) (negative = crop). Uncovered cells are empty. Material objects follow; selection-backed
        /// objects and hidden stashes are moved along (cells that fall outside are dropped).
        /// </summary>
        public VoxelGrid Recanvas(int dx, int dy, int dz, int nw, int nh, int nd)
        {
            if (nw <= 0 || nh <= 0 || nd <= 0) throw new ArgumentOutOfRangeException(nameof(nw), "grid dimensions must be positive");
            long n = (long)nw * nh * nd; if (n > MaxCells) throw new ArgumentOutOfRangeException(nameof(nw), "grid too large");
            if (dx == 0 && dy == 0 && dz == 0 && nw == w && nh == h && nd == d) return this;
            var nv = (Voxel*)NativeMemory.AllocZeroed((nuint)(n * 8 + 64));
            int sx0 = Math.Max(0, -dx), sy0 = Math.Max(0, -dy), sz0 = Math.Max(0, -dz);
            int cw = Math.Min(w - sx0, nw - Math.Max(0, dx)), ch = Math.Min(h - sy0, nh - Math.Max(0, dy)), cd = Math.Min(d - sz0, nd - Math.Max(0, dz));
            for (int z = 0; z < cd; z++) for (int y = 0; y < ch; y++)
                Buffer.MemoryCopy(vox + (((long)(sz0 + z) * h + sy0 + y) * w + sx0), nv + (((long)(Math.Max(0, dz) + z) * nh + Math.Max(0, dy) + y) * nw + Math.Max(0, dx)), (long)cw * 8, (long)cw * 8);
            foreach (var o in Objects)
            {
                if (o.Cells != null && o.Cells.SameSize(this)) { var t = Translated(o.Cells, dx, dy, dz, nw, nh, nd); o.Cells.Dispose(); o.Cells = t; }
                if (o.stash != null && o.stashW == w && o.stashH == h && o.stashD == d)
                {
                    for (int k = o.stash.Count - 1; k >= 0; k--)
                    {
                        var (x, y, z) = Coords(o.stash[k].index); x += dx; y += dy; z += dz;
                        if ((uint)x < (uint)nw && (uint)y < (uint)nh && (uint)z < (uint)nd) o.stash[k] = ((z * nh + y) * nw + x, o.stash[k].v); else o.stash.RemoveAt(k);
                    }
                    o.stashW = nw; o.stashH = nh; o.stashD = nd;
                }
            }
            NativeMemory.Free(vox); vox = nv;
            NativeMemory.Free(faces); faces = (byte*)NativeMemory.AllocZeroed((nuint)(n + 64));
            if (light != null) { NativeMemory.Free(light); light = null; }
            w = nw; h = nh; d = nd; SetCells(n);
            Invalidate(); return this;
        }
        /// <summary>Grows (or with negative values shrinks) the grid by the given cells on each side, in place.</summary>
        public VoxelGrid Expand(int x0, int y0, int z0, int x1, int y1, int z1) => Recanvas(x0, y0, z0, w + x0 + x1, h + y0 + y1, d + z0 + z1);
        public VoxelGrid Expand(int all) => Expand(all, all, all, all, all, all);
        /// <summary>Keeps only the box [x0, x1) x [y0, y1) x [z0, z1) (clipped to the grid), in place.</summary>
        public VoxelGrid Crop(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); z0 = Math.Max(0, z0); x1 = Math.Min(w, x1); y1 = Math.Min(h, y1); z1 = Math.Min(d, z1);
            if (x1 <= x0 || y1 <= y0 || z1 <= z0) throw new ArgumentOutOfRangeException(nameof(x0), "the crop box does not touch the grid");
            return Recanvas(-x0, -y0, -z0, x1 - x0, y1 - y0, z1 - z0);
        }
        /// <summary>Resamples IN PLACE to another size (nearest cell), see <see cref="Resized"/>.</summary>
        public VoxelGrid Resize(int nw, int nh, int nd)
        {
            if (nw == w && nh == h && nd == d) return this;
            using var g = Resized(nw, nh, nd);
            var nv = (Voxel*)NativeMemory.AllocZeroed((nuint)(g.cells * 8 + 64));
            Buffer.MemoryCopy(g.vox, nv, g.cells * 8, g.cells * 8);
            foreach (var o in Objects) { if (o.Cells != null) { o.Cells.Dispose(); o.Cells = null; } o.stash = null; }   // selection-backed objects do not survive a resample
            Objects.RemoveAll(o => !o.IsMaterial);
            NativeMemory.Free(vox); vox = nv;
            NativeMemory.Free(faces); faces = (byte*)NativeMemory.AllocZeroed((nuint)(g.cells + 64));
            if (light != null) { NativeMemory.Free(light); light = null; }
            w = nw; h = nh; d = nd; SetCells(g.cells);
            Invalidate(); return this;
        }
        /// <summary>Resamples in place by a factor (2 = every cell becomes 2x2x2).</summary>
        public VoxelGrid Scale(float factor) => Resize(Math.Max(1, (int)MathF.Round(w * factor)), Math.Max(1, (int)MathF.Round(h * factor)), Math.Max(1, (int)MathF.Round(d * factor)));

        /// <summary>Rotate a quarter turn (counter-clockwise seen from +z) about the vertical axis. Returns a NEW grid (w and h swap).</summary>
        public VoxelGrid RotatedZ(int quarterTurns = 1)
        {
            quarterTurns &= 3; if (quarterTurns == 0) return Clone();
            var src = this; VoxelGrid? tmp = null;
            for (int q = 0; q < quarterTurns; q++)
            {
                var g = new VoxelGrid(src.h, src.w, src.d);
                for (int z = 0; z < src.d; z++) for (int y = 0; y < src.h; y++) for (int x = 0; x < src.w; x++)
                    g.vox[g.Index(src.h - 1 - y, x, z)] = src.vox[src.Index(x, y, z)];    // (x, y) -> (-y, x)
                tmp?.Dispose(); tmp = g; src = g;
            }
            foreach (var o in Objects) if (o.IsMaterial) tmp!.Objects.Add(new VoxelObject(o.Name, o.Material) { Tag = o.Tag });   // selection-backed objects do not carry over
            tmp!.Invalidate(); return tmp;
        }
        /// <summary>Copy resampled into a new grid of another size (nearest cell).</summary>
        public VoxelGrid Resized(int nw, int nh, int nd)
        {
            var g = new VoxelGrid(nw, nh, nd);
            for (int z = 0; z < nd; z++) for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
                g.vox[g.Index(x, y, z)] = vox[Index((int)((x + 0.5f) * w / nw), (int)((y + 0.5f) * h / nh), (int)((z + 0.5f) * d / nd))];
            foreach (var o in Objects) if (o.IsMaterial) g.Objects.Add(new VoxelObject(o.Name, o.Material) { Tag = o.Tag });
            g.Invalidate(); return g;
        }
        /// <summary>Smallest box containing all solid cells (exclusive max), or all zeros when empty.</summary>
        public (int x0, int y0, int z0, int x1, int y1, int z1) SolidBounds() { using var s = SelectSolid(); return s.Bounds; }
        /// <summary>Copy of the grid cropped to its solid bounds.</summary>
        public VoxelGrid Cropped() { using var s = SelectSolid(); return s.IsEmpty ? new VoxelGrid(1, 1, 1) : Extract(s); }

        // ---- procedural -------------------------------------------------------------------------------

        /// <summary>Fractal noise value (about -1..1) at a cell, with the settings in <paramref name="n"/> (Gradient included).</summary>
        public float Noise(VoxelNoise n, int x, int y, int z) => n.Sample(x, y, z) + n.Gradient * ((z + 0.5f) / d - 0.5f);

        /// <summary>
        /// Fill the cells where noise &gt; <paramref name="n"/>.Threshold with <paramref name="colour"/>(x, y, z, noise); other cells are left alone
        /// (or erased when <paramref name="eraseBelow"/>). Restricted to <paramref name="sel"/> when given. Cave systems, rocks, clouds, asteroids.
        /// </summary>
        public VoxelGrid FillNoise(VoxelNoise n, Func<int, int, int, float, Voxel> colour, bool eraseBelow = false, VoxelSelection? sel = null)
        {
            if (sel != null) CheckSel(sel);
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = Index(x, y, z); if (sel != null && sel.p[i] == 0) continue;
                float v = Noise(n, x, y, z);
                if (v > n.Threshold) vox[i] = colour(x, y, z, v); else if (eraseBelow) vox[i] = Voxel.Empty;
            }
            Invalidate(); return this;
        }
        /// <summary>Same with one colour.</summary>
        public VoxelGrid FillNoise(VoxelNoise n, Voxel v, bool eraseBelow = false, VoxelSelection? sel = null) => FillNoise(n, (x, y, z, f) => v, eraseBelow, sel);
        /// <summary>Carve: erase the cells where noise &gt; threshold (caves through existing rock).</summary>
        public VoxelGrid CarveNoise(VoxelNoise n, VoxelSelection? sel = null) => FillNoise(n, (x, y, z, f) => Voxel.Empty, false, sel);
        /// <summary>Select the cells where noise &gt; threshold.</summary>
        public VoxelSelection SelectNoise(VoxelNoise n, VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
            => (into ?? NewSelection()).Where((x, y, z) => Noise(n, x, y, z) > n.Threshold, mode);
        /// <summary>
        /// Heightmap terrain from 2-D fractal noise: column height = base + amplitude * noise(x, y); colour by depth below the surface.
        /// </summary>
        public VoxelGrid FillTerrain(VoxelNoise n, float baseHeight, float amplitude, Func<int, int, int, int, Voxel> colourByDepth)
        {
            return FromHeightMap((x, y) => (int)MathF.Round(baseHeight + amplitude * n.Sample(x, y, 0.5f)), (x, y, z) => colourByDepth(x, y, z, HeightAt(n, baseHeight, amplitude, x, y) - 1 - z));
        }
        static int HeightAt(VoxelNoise n, float b, float a, int x, int y) => (int)MathF.Round(b + a * n.Sample(x, y, 0.5f));
        /// <summary>Sprinkle: set a random fraction (0..1) of the cells matching <paramref name="where"/> (default: empty cells on top of a solid) to v.</summary>
        public VoxelGrid Scatter(float density, Voxel v, int seed = 1, Func<int, int, int, bool>? where = null)
        {
            var r = new Random(seed);
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                bool ok = where != null ? where(x, y, z) : (!IsSolid(x, y, z) && z > 0 && IsSolid(x, y, z - 1));
                if (ok && r.NextDouble() < density) vox[Index(x, y, z)] = v;
            }
            Invalidate(); return this;
        }
        /// <summary>Statistics: solid cells, emitters.</summary>
        public (int solid, int emitters) Stats() { int s = 0, e = 0; for (int i = 0; i < cells; i++) if (vox[i].IsSolid) { s++; if (vox[i].Emit > 0) e++; } return (s, e); }
    }
}
