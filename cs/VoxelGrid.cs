// VoxelGrid - a 3-D grid of voxels rendered through SR2D (VOXEL_FACES / VOXEL_LIGHT / VOXEL_RENDER).
//
//   var g = new VoxelGrid(64, 64, 32);              // x east, y north, z up
//   g[x, y, z] = new Voxel(0xFF80C040);             // A = 0 -> empty, anything else -> solid
//   g[x, y, z] = new Voxel(0xFFFFD080, emit: 12);   // light source (colour = voxel colour)
//   canvas.DrawVoxels(g, VoxelCamera.Isometric(scale: 4), cx, cy, VoxelLighting.Smooth);
//
// * Voxels() is the raw Span<Voxel> (like Sprite pixels): bulk edits go straight into it,
//   then call Invalidate() (the indexer setter does it for you).
// * Faces / light are derived data, rebuilt lazily on the next draw after an edit. Light
//   propagation is the expensive part (about 25 ns per cell); call Update() yourself when you
//   want to choose the moment, or draw with VoxelLighting.Faces which never needs it.
// * Cameras: pixel-art presets (Isometric 2:1, ThreeQuarter, TopDown, Side, each with a
//   quarter-turn Turn 0..3) or Free(yaw, pitch). Scale 1 with a preset draws one pixel per voxel
//   (Points mode, hole-free); larger scales rasterise real cubes with per-face shading. Set Mode
//   explicitly to force either.
// * Everything is opaque; the renderer paints exposed camera-facing faces far-to-near, so
//   interior voxels and hidden sides cost nothing. Grids of any size work (the real-time range
//   is roughly up to 256^3 - see the README table); bigger ones just take longer.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Sr2d64CSport
{
    /// <summary>One voxel: colour (A = 0 -> empty), light emission 0..15, and two free data fields.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    internal struct Voxel : IEquatable<Voxel>
    {
        /// <summary>ARGB colour; alpha 0 means the cell is empty, anything else solid.</summary>
        public uint Argb;
        /// <summary>Light emission strength 0..15 (0 = none). The emitted colour is <see cref="Argb"/>.</summary>
        public byte Emit;
        /// <summary>Free byte, e.g. a material id. Not interpreted by the renderer.</summary>
        public byte Material;
        /// <summary>Free 16-bit value. Not interpreted by the renderer.</summary>
        public ushort User;

        public Voxel(uint argb, byte emit = 0, byte material = 0, ushort user = 0) { Argb = argb; Emit = emit; Material = material; User = user; }
        public Voxel(int argb, byte emit = 0, byte material = 0, ushort user = 0) : this(unchecked((uint)argb), emit, material, user) { }
        public Voxel(System.Drawing.Color c, byte emit = 0, byte material = 0, ushort user = 0) : this(unchecked((uint)c.ToArgb()), emit, material, user) { }

        public static readonly Voxel Empty = default;
        public bool IsEmpty => (Argb >> 24) == 0;
        public bool IsSolid => (Argb >> 24) != 0;
        public int Color { get => unchecked((int)Argb); set => Argb = unchecked((uint)value); }

        public bool Equals(Voxel o) => Argb == o.Argb && Emit == o.Emit && Material == o.Material && User == o.User;
        public override bool Equals(object? o) => o is Voxel v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(Argb, Emit, Material, User);
        public static bool operator ==(Voxel a, Voxel b) => a.Equals(b);
        public static bool operator !=(Voxel a, Voxel b) => !a.Equals(b);
        public override string ToString() => IsEmpty ? "empty" : $"#{Argb:X8} emit {Emit} mat {Material} user {User}";
    }

    /// <summary>Lighting tiers, cheapest first. Each tier includes the previous one.</summary>
    internal enum VoxelLighting
    {
        /// <summary>Stored colours as they are.</summary>
        None = 0,
        /// <summary>Per-face brightness from <see cref="VoxelCamera.Shade"/> (the classic "top light, dark sides" look). No light data needed.</summary>
        Faces = 1,
        /// <summary>+ Minecraft-style propagated light: sky light from above and coloured emitters (0..15) spreading through empty cells.</summary>
        Propagated = 2,
        /// <summary>+ smooth lighting: light interpolated across each face from its corners, plus ambient occlusion in creases.</summary>
        Smooth = 3,
    }

    /// <summary>How the voxels are rasterised.</summary>
    internal enum VoxelMode
    {
        /// <summary>Points at scale ~1 (one pixel per voxel), cubes otherwise.</summary>
        Auto = -1,
        /// <summary>One pixel per exposed voxel, whatever the scale. The pixel art fast path.</summary>
        Points = 0,
        /// <summary>Every exposed face is drawn as a parallelogram; watertight, no cracks.</summary>
        Cubes = 1,
    }

    /// <summary>
    /// Orthographic projection of a <see cref="VoxelGrid"/>: a 2 x 3 matrix (grid units -> pixels) plus the
    /// view direction. Use the presets (<see cref="Isometric"/>, <see cref="ThreeQuarter"/>, <see cref="TopDown"/>,
    /// <see cref="Side"/>) for crisp pixel-art results, or <see cref="Free"/> for any yaw / pitch.
    /// Grid axes: x east, y north, z up. Screen y grows downwards.
    /// </summary>
    internal sealed class VoxelCamera
    {
        // sx = M[0] x + M[1] y + M[2] z,  sy = M[3] x + M[4] y + M[5] z  (before the offset)
        internal readonly float[] M = new float[6];
        internal readonly float[] View = new float[3];
        float scale = 1f;

        /// <summary>Pixels per voxel along the horizontal grid axes.</summary>
        public float Scale => scale;
        /// <summary>Screen-space anchor: which grid point lands on the (x, y) given to DrawVoxels. Default: grid centre.</summary>
        public VoxelAnchor Anchor = VoxelAnchor.Center;
        /// <summary><see cref="VoxelMode.Auto"/> picks Points for presets at scale 1 and Cubes otherwise.</summary>
        public VoxelMode Mode = VoxelMode.Auto;
        /// <summary>Brightness of the +X, -X, +Y, -Y, +Z, -Z faces for <see cref="VoxelLighting.Faces"/> and up. Set with <see cref="SetLight"/> or directly.</summary>
        public readonly float[] Shade = { 0.75f, 0.55f, 0.65f, 0.5f, 1f, 0.35f };
        /// <summary>Ambient-occlusion factor for a face corner with 0, 1, 2, 3 occluders (<see cref="VoxelLighting.Smooth"/>).</summary>
        public readonly float[] AO = { 1f, 0.8f, 0.62f, 0.45f };
        /// <summary>Light level 0..15 -> brightness (<see cref="VoxelLighting.Propagated"/> and up). Default: Minecraft's 0.8^(15-l) curve with a floor. Applies to sky light and (times <see cref="LampEnergy"/> / <see cref="SkyEnergy"/>) to lamps.</summary>
        public readonly float[] LightCurve = new float[16];
        /// <summary>Multiplier on the light curve for the LAMP channels only (emitters), default 1. Above 1 lamps over-drive their surroundings (clamped per channel); the sky is untouched.</summary>
        public float LampEnergy = 1f;
        /// <summary>Multiplier on the light curve for the SKY (ambient) channel only, default 1.</summary>
        public float SkyEnergy = 1f;
        /// <summary>Tint of sky light (0xRRGGBB). Default white.</summary>
        public int SkyColor = 0xFFFFFF;
        /// <summary>Sky light assumed just outside the grid (0..15), -1 = the grid's <see cref="VoxelGrid.SkyLight"/> (default).</summary>
        public int OutsideSky = -1;
        /// <summary>Write the voxel alpha instead of 255 (grids used as alpha-tagged sprites).</summary>
        public bool KeepAlpha;
        /// <summary>
        /// Depth fade (the cheapest "lighting" there is; works with every tier including None): <see cref="VoxelFade.Height"/>
        /// darkens with grid z (top slab full colour, bottom slab <see cref="FadeMin"/>), <see cref="VoxelFade.Depth"/> darkens
        /// with distance along the view direction (nearest corner full, farthest <see cref="FadeMin"/>), both may be combined.
        /// Multiplies the colour AFTER the lighting tier. Off by default.
        /// </summary>
        public VoxelFade Fade = VoxelFade.None;
        /// <summary>Brightness at the far / bottom end of the fade, 0..1 (default 0.25).</summary>
        public float FadeMin = 0.25f;
        /// <summary>Fade curve: 1 = linear, &lt; 1 keeps the front bright longer, &gt; 1 darkens sooner (default 1).</summary>
        public float FadeGamma = 1f;
        /// <summary>Fog colour (0xRRGGBB) faded towards instead of black; used when <see cref="VoxelFade.Fog"/> is set.</summary>
        public int FadeColor = 0x000000;

        bool preset;
        int turn;

        public VoxelCamera()
        {
            for (int i = 0; i < 16; i++) LightCurve[i] = 0.05f + 0.95f * MathF.Pow(0.8f, 15 - i);
        }

        /// <summary>True for a pixel-art preset (Isometric, ThreeQuarter, TopDown, Side).</summary>
        public bool IsPreset => preset;
        /// <summary>Quarter turns of a preset (0..3), 0 = default orientation.</summary>
        public int Turn => turn;

        /// <summary>Copy of this camera (matrices and tables).</summary>
        public VoxelCamera Clone()
        {
            var c = new VoxelCamera();
            Array.Copy(M, c.M, 6); Array.Copy(View, c.View, 3); c.scale = scale; c.Anchor = Anchor; c.Mode = Mode;
            Array.Copy(Shade, c.Shade, 6); Array.Copy(AO, c.AO, 4); Array.Copy(LightCurve, c.LightCurve, 16);
            c.SkyColor = SkyColor; c.OutsideSky = OutsideSky; c.KeepAlpha = KeepAlpha; c.preset = preset; c.turn = turn;
            c.Fade = Fade; c.FadeMin = FadeMin; c.FadeGamma = FadeGamma; c.FadeColor = FadeColor; c.LampEnergy = LampEnergy; c.SkyEnergy = SkyEnergy;
            return c;
        }

        // ---- presets -------------------------------------------------------------------------
        // The horizontal axes map to integer pixel steps so every voxel edge lands on a pixel edge.
        // All presets look from the south-west (turn 0): +x runs up-right, +y up-left, +z up.
        // Quarter turns rotate the grid counter-clockwise (seen from above) under the camera.

        /// <summary>
        /// Pixel-art isometric (the 2:1 "diamond" look, strictly a dimetric projection): at scale 1 grid x steps
        /// (1, -0.5) pixels, y (-1, -0.5), z (0, -1), which is exactly one pixel per voxel with no holes (Points mode);
        /// at scale 2 you get the classic 2 x 1 tiles as crisp cubes. Turn 0 looks from the south-west (+x up-right,
        /// +y up-left); <paramref name="turn"/> rotates the grid by quarter turns.
        /// </summary>
        public static VoxelCamera Isometric(float scale = 1f, int turn = 0) => Axes(scale, turn, 1f, -0.5f, -1f, -0.5f, 1f);

        /// <summary>
        /// "3/4 view" of top-down RPGs: looking north and down, x steps (1, 0), y (0, -0.5), z (0, -1): front faces and
        /// squashed tops, no diagonals. Turn 1..3 looks from the west / north / east.
        /// </summary>
        public static VoxelCamera ThreeQuarter(float scale = 1f, int turn = 0) => Axes(scale, turn, 1f, 0f, 0f, -0.5f, 1f);

        /// <summary>Straight down: 1 pixel per voxel in x and y (north up on screen), top faces only. Turn rotates by 90 degrees.</summary>
        public static VoxelCamera TopDown(float scale = 1f, int turn = 0) => Axes(scale, turn, 1f, 0f, 0f, -1f, 0f);

        /// <summary>Side view from the south (turn 0; 1 = from the west, 2 north, 3 east): 1 pixel per voxel, z is up on screen.</summary>
        public static VoxelCamera Side(float scale = 1f, int turn = 0) => Axes(scale, turn, 1f, 0f, 0f, 0f, 1f);

        // grid x -> screen (ax0, ax1), grid y -> (ay0, ay1), z -> (0, -pz); the view direction follows from the rows
        static VoxelCamera Axes(float scale, int turn, float ax0, float ax1, float ay0, float ay1, float pz)
        {
            var c = new VoxelCamera { preset = true, turn = turn & 3, scale = scale };
            float[] ax = { ax0, ax1 }, ay = { ay0, ay1 };
            for (int i = 0; i < (turn & 3); i++) { var nx = ay; var ny = new[] { -ax[0], -ax[1] }; ax = nx; ay = ny; }   // x -> y, y -> -x
            c.M[0] = scale * ax[0]; c.M[1] = scale * ay[0]; c.M[2] = 0;
            c.M[3] = scale * ax[1]; c.M[4] = scale * ay[1]; c.M[5] = -scale * pz;
            c.SetViewFromRows();
            return c;
        }

        // forward = row0 x row1 (screen x, screen y-down and "into the screen" form a right-handed frame, as does x east / y north / z up)
        void SetViewFromRows()
        {
            float vx = M[1] * M[5] - M[2] * M[4], vy = M[2] * M[3] - M[0] * M[5], vz = M[0] * M[4] - M[1] * M[3];
            float l = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
            if (l < 1e-12f) { View[0] = 0; View[1] = 0; View[2] = -1; return; }
            View[0] = vx / l; View[1] = vy / l; View[2] = vz / l;
        }

        // ---- free camera -------------------------------------------------------------------------

        /// <summary>
        /// Free orthographic camera. <paramref name="yaw"/> is the compass direction the camera looks along
        /// (radians, 0 = looking east/+x, pi/2 = north/+y), <paramref name="pitch"/> the downward tilt
        /// (0 = horizontal, pi/2 = straight down). <paramref name="scale"/> = pixels per voxel,
        /// <paramref name="aspect"/> squashes screen y (0.5 for a 2:1 look).
        /// </summary>
        public static VoxelCamera Free(float yaw, float pitch, float scale = 4f, float aspect = 1f, float roll = 0f)
        {
            var c = new VoxelCamera { preset = false, scale = scale };
            float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw), cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
            // view (forward) direction
            float vx = cp * cy, vy = cp * sy, vz = -sp;
            // right = forward x up(0,0,1), up = right x forward
            float rx = vy, ry = -vx, rz = 0;
            float rl = MathF.Sqrt(rx * rx + ry * ry); if (rl < 1e-6f) { rx = -sy; ry = cy; rl = 1f; }   // looking straight down/up: keep yaw as screen orientation
            rx /= rl; ry /= rl;
            float ux = ry * vz - rz * vy, uy = rz * vx - rx * vz, uz = rx * vy - ry * vx;
            float ul = MathF.Sqrt(ux * ux + uy * uy + uz * uz); ux /= ul; uy /= ul; uz /= ul;
            if (roll != 0f)
            {
                float cr = MathF.Cos(roll), sr = MathF.Sin(roll);
                float nrx = rx * cr + ux * sr, nry = ry * cr + uy * sr, nrz = rz * cr + uz * sr;
                ux = -rx * sr + ux * cr; uy = -ry * sr + uy * cr; uz = -rz * sr + uz * cr;
                rx = nrx; ry = nry; rz = nrz;
            }
            c.M[0] = scale * rx; c.M[1] = scale * ry; c.M[2] = scale * rz;
            c.M[3] = -scale * aspect * ux; c.M[4] = -scale * aspect * uy; c.M[5] = -scale * aspect * uz;
            c.View[0] = vx; c.View[1] = vy; c.View[2] = vz;
            return c;
        }

        /// <summary>Any custom projection: screen x = (ax, ay, az) . (x, y, z), screen y = (bx, by, bz) . (x, y, z). The view direction is row0 x row1.</summary>
        public static VoxelCamera Custom(float ax, float ay, float az, float bx, float by, float bz, float scale = 1f)
        {
            var c = new VoxelCamera { preset = false, scale = scale };
            c.M[0] = ax * scale; c.M[1] = ay * scale; c.M[2] = az * scale; c.M[3] = bx * scale; c.M[4] = by * scale; c.M[5] = bz * scale;
            c.SetViewFromRows();
            return c;
        }

        /// <summary>Same projection with a different scale.</summary>
        public VoxelCamera WithScale(float newScale)
        {
            var c = Clone();
            float k = scale != 0 ? newScale / scale : newScale;
            for (int i = 0; i < 6; i++) c.M[i] *= k;
            c.scale = newScale;
            return c;
        }

        /// <summary>
        /// Derive the six face brightnesses from a light direction (pointing TOWARDS the light, e.g. (−1, −1, 2)
        /// = light from the south-west, high up): brightness = ambient + (1 - ambient) * max(0, n . l).
        /// </summary>
        public VoxelCamera SetLight(float lx, float ly, float lz, float ambient = 0.35f)
        {
            float l = MathF.Sqrt(lx * lx + ly * ly + lz * lz); if (l < 1e-9f) return this;
            lx /= l; ly /= l; lz /= l;
            float[] n = { lx, -lx, ly, -ly, lz, -lz };
            for (int i = 0; i < 6; i++) Shade[i] = ambient + (1f - ambient) * MathF.Max(0f, n[i]);
            return this;
        }

        /// <summary>Same as <see cref="SetLight(float, float, float, float)"/> with a vector.</summary>
        public VoxelCamera SetLight(System.Numerics.Vector3 towardsLight, float ambient = 0.35f) => SetLight(towardsLight.X, towardsLight.Y, towardsLight.Z, ambient);

        /// <summary>Uniform light-level curve: brightness = min + (1 - min) * (level / 15)^gamma.</summary>
        public VoxelCamera SetLightCurve(float min = 0.05f, float gamma = 1.5f)
        {
            for (int i = 0; i < 16; i++) LightCurve[i] = min + (1f - min) * MathF.Pow(i / 15f, gamma);
            return this;
        }

        /// <summary>Project a grid point to screen pixels relative to the anchor origin (use with DrawVoxels' x, y).</summary>
        public System.Numerics.Vector2 Project(float x, float y, float z) => new System.Numerics.Vector2(M[0] * x + M[1] * y + M[2] * z, M[3] * x + M[4] * y + M[5] * z);

        /// <summary>Screen bounding box (relative to the projected grid origin) of a w x h x d grid.</summary>
        public System.Drawing.RectangleF Bounds(int w, int h, int d)
        {
            float x0 = 0, x1 = 0, y0 = 0, y1 = 0;
            for (int c = 1; c < 8; c++)
            {
                var p = Project((c & 1) != 0 ? w : 0, (c & 2) != 0 ? h : 0, (c & 4) != 0 ? d : 0);
                x0 = MathF.Min(x0, p.X); x1 = MathF.Max(x1, p.X); y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y);
            }
            return System.Drawing.RectangleF.FromLTRB(x0, y0, x1, y1);
        }

        internal VoxelMode EffectiveMode => Mode != VoxelMode.Auto ? Mode : (preset && MathF.Abs(scale - 1f) < 1e-6f ? VoxelMode.Points : VoxelMode.Cubes);
    }

    /// <summary>Which grid point DrawVoxels puts at the given screen position.</summary>
    internal enum VoxelAnchor { Center, Origin, BottomCenter, TopLeftOfBounds }

    /// <summary>Depth-fade flags of <see cref="VoxelCamera.Fade"/> (SR2D_VOX_FADE_* in the native API).</summary>
    [Flags]
    internal enum VoxelFade
    {
        None = 0,
        /// <summary>Darker towards the bottom of the grid (z = 0), full colour at the top slab.</summary>
        Height = 2,
        /// <summary>Darker with distance from the camera (orthographic depth along the view direction): the near corner of the grid is full colour, the far corner FadeMin.</summary>
        Depth = 4,
        /// <summary>Fade towards <see cref="VoxelCamera.FadeColor"/> (fog) instead of towards black.</summary>
        Fog = 8,
    }

    /// <summary>
    /// A dense w x h x d block of <see cref="Voxel"/>s (x east, y north, z up) with cached visibility and lighting,
    /// drawn with <see cref="Sprite.DrawVoxels"/>. Index = (z * H + y) * W + x, like rows of pixels stacked.
    /// </summary>
    internal sealed unsafe partial class VoxelGrid : IDisposable
    {
        // native SR2D_VoxelScene (232 bytes)
        [StructLayout(LayoutKind.Sequential)]
        internal struct Scene
        {
            public Voxel* Vox; public byte* Faces; public uint* Light;
            public int Gw, Gh, Gd, Lighting, Mode, Flags;
            public fixed float M[6]; public float Ox, Oy;
            public fixed float View[3]; public float FadeMin;
            public fixed float Shade[6]; public fixed float AO[4]; public fixed float Lut[16];
            public int SkyColor; public uint Outside;
            public int ZFrom, ZTo; public float FadeGamma; public int FadeColor;
            public float* LampLut;
        }
        /// <summary>Light levels are stored in 1/8 steps by the kernel (byte = level * 8).</summary>
        internal const int LightOne = 8;

        internal int w, h, d;                 // an in-place RotateX / RotateY / RotateZ swaps two of them
        internal long cells;                  // Recanvas / Resize change it
        void SetCells(long n) { cells = n; }
        internal Voxel* vox; byte* faces; uint* light;
        bool facesValid, lightValid;
        int lightSky = 15; bool lightSides = true; int lightReach = 15;
        int exposed;
        bool disposed;

        /// <summary>Largest grid (cells) the native kernels address: 2^31 - 65 (32-bit cell indices).</summary>
        public const long MaxCells = int.MaxValue - 64;
        /// <summary>Bytes a grid of this size needs: voxels + faces, plus light when <paramref name="withLight"/>.</summary>
        public static long MemoryEstimate(int width, int height, int depth, bool withLight) { long n = (long)width * height * depth; return n * 9 + (withLight ? n * 4 : 0) + 192; }

        public VoxelGrid(int width, int height, int depth)
        {
            if (width <= 0 || height <= 0 || depth <= 0) throw new ArgumentOutOfRangeException(nameof(width), "grid dimensions must be positive");
            cells = (long)width * height * depth;
            // the native kernels index cells with a 32-bit int: up to 2^31 - 1 cells (a 1290^3 cube; 1024^3 = 2^30 is fine).
            // Memory is the practical limit: 8 bytes per voxel + 1 face byte, + 4 bytes light (+ 4 bytes scratch while it propagates)
            // when Propagated / Smooth lighting is used - 1024^3 needs 9 GB for Faces lighting, 17 GB with propagated light.
            if (cells > MaxCells) throw new ArgumentOutOfRangeException(nameof(width), $"grid too large ({cells} cells, max {MaxCells})");
            w = width; h = height; d = depth;
            vox = (Voxel*)NativeMemory.AllocZeroed((nuint)(cells * 8 + 64));
            faces = (byte*)NativeMemory.AllocZeroed((nuint)(cells + 64));
        }

        public int Width => w;
        public int Height => h;
        public int Depth => d;
        /// <summary>Number of cells (W * H * D).</summary>
        public int Count => (int)cells;
        /// <summary>Grid version, bumped by every edit (Invalidate). Lets callers cache derived data.</summary>
        public int Version { get; private set; }

        /// <summary>All voxels, index = (z * Height + y) * Width + x. Edit freely, then <see cref="Invalidate"/>.</summary>
        public Span<Voxel> Voxels() => new Span<Voxel>(vox, (int)cells);
        /// <summary>The voxels of one z layer (Height rows of Width).</summary>
        public Span<Voxel> Layer(int z) => new Span<Voxel>(vox + (long)z * w * h, w * h);
        /// <summary>One row (constant y, z) of Width voxels.</summary>
        public Span<Voxel> Row(int y, int z) => new Span<Voxel>(vox + ((long)z * h + y) * w, w);
        /// <summary>Raw pointer (like Sprite.PTR). Valid until Dispose.</summary>
        public Voxel* Ptr => vox;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Index(int x, int y, int z) => (z * h + y) * w + x;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(int x, int y, int z) => vox != null && (uint)x < (uint)w && (uint)y < (uint)h && (uint)z < (uint)d;   // false after Dispose: no read through a freed buffer

        /// <summary>Get / set a voxel (bounds-checked; the setter invalidates the caches).</summary>
        public Voxel this[int x, int y, int z]
        {
            get { if (!Contains(x, y, z)) throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}, {z}) is outside the {Width}x{Height}x{Depth} grid"); return vox[Index(x, y, z)]; }
            set { if (!Contains(x, y, z)) throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}, {z}) is outside the {Width}x{Height}x{Depth} grid"); vox[Index(x, y, z)] = value; Invalidate(); }
        }

        /// <summary>Voxel at (x, y, z) or Empty when outside the grid.</summary>
        public Voxel Get(int x, int y, int z) => Contains(x, y, z) ? vox[Index(x, y, z)] : Voxel.Empty;
        /// <summary>Set a voxel; ignored outside the grid. Returns this.</summary>
        public VoxelGrid Set(int x, int y, int z, Voxel v) { if (Contains(x, y, z)) { vox[Index(x, y, z)] = v; Invalidate(); } return this; }
        public VoxelGrid Set(int x, int y, int z, int argb, byte emit = 0, byte material = 0) => Set(x, y, z, new Voxel(argb, emit, material));
        public bool IsSolid(int x, int y, int z) => Contains(x, y, z) && vox[Index(x, y, z)].IsSolid;

        /// <summary>Mark the derived data (faces, light) stale. Called by the setters; call it after bulk edits through <see cref="Voxels"/>.</summary>
        public void Invalidate() { facesValid = false; lightValid = false; Version++; }

        public VoxelGrid Clear() { NativeMemory.Clear(vox, (nuint)(cells * 8)); Invalidate(); return this; }
        public VoxelGrid Fill(Voxel v) { Voxels().Fill(v); Invalidate(); return this; }

        /// <summary>Axis-aligned box [x0, x1) x [y0, y1) x [z0, z1), clipped to the grid.</summary>
        public VoxelGrid FillBox(int x0, int y0, int z0, int x1, int y1, int z1, Voxel v)
        {
            x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); z0 = Math.Max(z0, 0); x1 = Math.Min(x1, w); y1 = Math.Min(y1, h); z1 = Math.Min(z1, d);
            if (x0 >= x1 || y0 >= y1 || z0 >= z1) return this;
            for (int z = z0; z < z1; z++) for (int y = y0; y < y1; y++) new Span<Voxel>(vox + Index(x0, y, z), x1 - x0).Fill(v);
            Invalidate(); return this;
        }

        /// <summary>Solid sphere of radius r around (cx, cy, cz) - see FillEllipsoid (VoxelGrid.Edit.cs) for the coordinate convention.</summary>
        public VoxelGrid FillSphere(float cx, float cy, float cz, float r, Voxel v) => FillEllipsoid(cx, cy, cz, r, r, r, v, null);

        /// <summary>
        /// Build the grid from a height map: column (x, y) is filled from z = 0 up to height(x, y) (exclusive, clamped to Depth)
        /// with colour(x, y, z). Anything above is cleared.
        /// </summary>
        public VoxelGrid FromHeightMap(Func<int, int, int> height, Func<int, int, int, Voxel> colour)
        {
            // rows in parallel (independent cells); the per-cell delegate is the cost, ~5 ns per cell on one core
            Voxel* v = vox; int W = w, D = d, H = h;
            Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    int top = Math.Clamp(height(x, y), 0, D);
                    for (int z = 0; z < top; z++) v[((long)z * H + y) * W + x] = colour(x, y, z);
                    for (int z = top; z < D; z++) v[((long)z * H + y) * W + x] = Voxel.Empty;
                }
            });
            Invalidate(); return this;
        }

        /// <summary>
        /// Height-map fill by LAYERS: the column (x, y) is a stack of bands given by <paramref name="column"/>, which writes
        /// up to <paramref name="maxBands"/> (top z exclusive, voxel) pairs bottom-up into <c>bands</c> and returns how many;
        /// the rest of the column up to Depth is cleared. One delegate call per column instead of one per cell - about
        /// 10x faster than <see cref="FromHeightMap(Func{int,int,int}, Func{int,int,int,Voxel})"/> for terrain (water /
        /// rock / dirt / grass layers), the bulk being plain 8-byte stores. Rows run in parallel.
        /// <example>
        /// g.FromColumns(4, (x, y, bands) => { bands[0] = (rockTop, rock); bands[1] = (rockTop + 3, dirt); bands[2] = (rockTop + 4, grass); return 3; });
        /// </example>
        /// </summary>
        public VoxelGrid FromColumns(int maxBands, ColumnFiller column) => FromColumns(maxBands, column, 0, h);
        /// <summary>Same for the rows [<paramref name="yFrom"/>, <paramref name="yTo"/>) only - fill a big grid in bands and report progress in between.</summary>
        public VoxelGrid FromColumns(int maxBands, ColumnFiller column, int yFrom, int yTo)
        {
            if (maxBands <= 0) throw new ArgumentOutOfRangeException(nameof(maxBands));
            yFrom = Math.Max(0, yFrom); yTo = Math.Min(h, yTo);
            if (yFrom >= yTo) return this;
            Voxel* v = vox; int W = w, D = d, H = h; long slab = (long)W * H;
            Parallel.For(yFrom, yTo, () => new (int top, Voxel voxel)[maxBands], (y, _, bands) =>
            {
                for (int x = 0; x < W; x++)
                {
                    int n = Math.Clamp(column(x, y, bands), 0, maxBands);
                    Voxel* p = v + (long)y * W + x;              // z = 0 of this column; stride = slab
                    int z = 0;
                    for (int b = 0; b < n; b++)
                    {
                        int top = Math.Min(bands[b].top, D); Voxel c = bands[b].voxel;
                        for (; z < top; z++) p[z * slab] = c;
                    }
                    for (; z < D; z++) p[z * slab] = Voxel.Empty;
                }
                return bands;
            }, _ => { });
            Invalidate(); return this;
        }
        /// <summary>Per-column callback of <see cref="FromColumns"/>: fill <paramref name="bands"/> bottom-up (top z exclusive, ascending) and return the count.</summary>
        public delegate int ColumnFiller(int x, int y, (int top, Voxel voxel)[] bands);

        /// <summary>Copy the voxels of another grid at an offset (clipped). Empty source cells are skipped unless <paramref name="copyEmpty"/>.</summary>
        public VoxelGrid Paste(VoxelGrid src, int ox, int oy, int oz, bool copyEmpty = false)
        {
            for (int z = 0; z < src.d; z++)
            {
                int tz = z + oz; if ((uint)tz >= (uint)d) continue;
                for (int y = 0; y < src.h; y++)
                {
                    int ty = y + oy; if ((uint)ty >= (uint)h) continue;
                    int x0 = Math.Max(0, -ox), x1 = Math.Min(src.w, w - ox);
                    if (x0 >= x1) continue;
                    Voxel* s = src.vox + src.Index(x0, y, z); Voxel* t = vox + Index(x0 + ox, ty, tz);
                    if (copyEmpty) Buffer.MemoryCopy(s, t, (x1 - x0) * 8L, (x1 - x0) * 8L);
                    else for (int x = 0; x < x1 - x0; x++) if (s[x].IsSolid) t[x] = s[x];
                }
            }
            Invalidate(); return this;
        }

        /// <summary>Deep copy.</summary>
        public VoxelGrid Clone()
        {
            var g = new VoxelGrid(w, h, d);
            Buffer.MemoryCopy(vox, g.vox, cells * 8, cells * 8);
            g.lightSky = lightSky; g.lightSides = lightSides; g.lightReach = lightReach;
            ObjectsCloneTo(g);
            return g;
        }

        // ---- derived data -------------------------------------------------------------------------

        /// <summary>Sky light level 0..15 poured into open columns from above (default 15). Changing it invalidates the light.</summary>
        public int SkyLight { get => lightSky; set { value = Math.Clamp(value, 0, 15); if (value != lightSky) { lightSky = value; lightValid = false; } } }
        /// <summary>Also let sky light in from the four side borders (default true): a grid cut out of a bigger world is not a dark box.</summary>
        public bool SkyFromSides { get => lightSides; set { if (value != lightSides) { lightSides = value; lightValid = false; } } }
        /// <summary>
        /// How far a full-strength light (level 15) travels through empty cells before it is gone, in cells: 15 (default, the
        /// Minecraft rule: one level per cell) .. 120. A lamp with Emit e reaches e / 15 of it; the light falls off linearly
        /// over that distance and the propagation cost grows with the lit volume (256^3 open grid: 36 ms at 15, ~50 ms at 120).
        /// Internally the loss per cell is 120 / LightReach in 1/8 levels, so the exact reaches are 120 / n: 15, 17, 20, 24,
        /// 30, 40, 60, 120 - other values round to the nearest. Changing it invalidates the light.
        /// </summary>
        public int LightReach
        {
            get => lightReach;
            set { value = Math.Clamp(value, 15, 120); if (value != lightReach) { lightReach = value; lightValid = false; } }
        }
        /// <summary>Loss per cell handed to the kernel (1/8 levels): 8 = one level per cell.</summary>
        int LightDec => Math.Clamp((int)Math.Round(15.0 * LightOne / lightReach), 1, 15 * LightOne);
        /// <summary>The reach actually used (after rounding to a whole loss per cell).</summary>
        public int EffectiveLightReach => 15 * LightOne / LightDec;

        /// <summary>True when faces and light are current.</summary>
        public bool IsUpToDate => facesValid && lightValid && light != null;
        /// <summary>Number of voxels with at least one exposed side (after <see cref="UpdateFaces"/>).</summary>
        public int ExposedCount { get { UpdateFaces(); return exposed; } }

        /// <summary>Recompute the exposed-face bytes if stale (cheap: ~1 ns per cell).</summary>
        public VoxelGrid UpdateFaces()
        {
            if (facesValid) return this;
            exposed = SR2D.Native.VoxelFaces(vox, w, h, d, faces);
            facesValid = true;
            return this;
        }

        /// <summary>Recompute faces and propagated light if stale (~7 ns per cell: 256^3 in ~0.1 s). Called automatically by draws with Propagated / Smooth lighting.</summary>
        public VoxelGrid Update()
        {
            UpdateFaces();
            if (lightValid && light != null) return this;
            if (light == null) light = (uint*)NativeMemory.Alloc((nuint)(cells * 4 + 64));
            int n = SR2D.Native.VoxelLight(vox, faces, w, h, d, light, lightSky, (lightSides ? 1 : 0) | (LightDec << 8));
            if (n < 0) throw new OutOfMemoryException("VOXEL_LIGHT: out of scratch memory");
            lightValid = true;
            return this;
        }

        /// <summary>Exposed-side bits of a cell after <see cref="UpdateFaces"/>: 0x40 solid, 1 +X, 2 -X, 4 +Y, 8 -Y, 16 +Z, 32 -Z.</summary>
        public byte FaceBits(int x, int y, int z) { UpdateFaces(); return Contains(x, y, z) ? faces[Index(x, y, z)] : (byte)0; }
        /// <summary>Packed light of a cell after <see cref="Update"/>: sky &lt;&lt; 24 | r &lt;&lt; 16 | g &lt;&lt; 8 | b, each 0..15 (whole levels; see <see cref="LightAtFine"/> for the 1/8 steps).</summary>
        public uint LightAt(int x, int y, int z) { uint L = LightAtFine(x, y, z); return (L >> 27) << 24 | ((L >> 19) & 15) << 16 | ((L >> 11) & 15) << 8 | ((L >> 3) & 15); }
        /// <summary>Packed light in the kernel's fixed point: every byte = level * 8 (0..120).</summary>
        public uint LightAtFine(int x, int y, int z) { Update(); return Contains(x, y, z) ? light[Index(x, y, z)] & 0x7F7F7F7Fu : 0; }
        /// <summary>Sky light 0..15 at a cell.</summary>
        public int SkyLightAt(int x, int y, int z) => (int)(LightAt(x, y, z) >> 24);

        // ---- rendering ------------------------------------------------------------------------------

        /// <summary>
        /// Draw into <paramref name="dst"/> (respecting its lock rect) with grid anchor at (x, y).
        /// Returns the number of voxels drawn. <paramref name="pick"/> (optional, dst.Width * dst.Height ints, prefilled with -1)
        /// receives (voxel index &lt;&lt; 3 | face) per written pixel for picking; see <see cref="Pick"/>.
        /// </summary>
        public int Draw(Sprite dst, VoxelCamera cam, float x, float y, VoxelLighting lighting = VoxelLighting.Faces, int* pick = null)
            => Draw(dst, cam, x, y, lighting, pick, 0, 0);

        /// <summary>
        /// Draw only the slabs [<paramref name="zFrom"/>, <paramref name="zTo"/>) of the painter's-order walk (0 = farthest slab
        /// from the camera along z, Depth = nearest; NOT grid z - the kernel flips it for the camera). Successive calls with
        /// consecutive ranges over the same target produce exactly the whole picture, so a big grid can be drawn in steps with
        /// progress / event handling in between (see <see cref="Sprite.DrawVoxelsProgressive"/>). zTo &lt;= 0 = everything.
        /// </summary>
        public int Draw(Sprite dst, VoxelCamera cam, float x, float y, VoxelLighting lighting, int* pick, int zFrom, int zTo)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoxelGrid));
            if (dst == null || cam == null) return 0;
            int lt = Math.Clamp((int)lighting, 0, 3);
            if (lt >= 2) Update(); else UpdateFaces();
            Scene s = default;
            s.Vox = vox; s.Faces = faces; s.Light = lt >= 2 ? light : null;
            s.Gw = w; s.Gh = h; s.Gd = d; s.Lighting = lt; s.Mode = (int)cam.EffectiveMode;
            s.Flags = (cam.KeepAlpha ? 1 : 0) | ((int)cam.Fade & 14);
            s.FadeMin = Math.Clamp(cam.FadeMin, 0f, 1f); s.FadeGamma = cam.FadeGamma > 0 ? cam.FadeGamma : 1f; s.FadeColor = cam.FadeColor & 0xFFFFFF;
            s.ZFrom = zFrom; s.ZTo = zTo;
            for (int i = 0; i < 6; i++) { s.M[i] = cam.M[i]; s.Shade[i] = cam.Shade[i]; }
            for (int i = 0; i < 3; i++) s.View[i] = cam.View[i];
            for (int i = 0; i < 4; i++) s.AO[i] = cam.AO[i];
            float* lamp = stackalloc float[16];
            for (int i = 0; i < 16; i++) { s.Lut[i] = cam.LightCurve[i] * cam.SkyEnergy; lamp[i] = cam.LightCurve[i] * cam.LampEnergy; }
            s.LampLut = lamp;
            s.SkyColor = cam.SkyColor & 0xFFFFFF; s.Outside = (uint)(cam.OutsideSky < 0 ? lightSky : Math.Clamp(cam.OutsideSky, 0, 15)) * LightOne << 24;
            var a = AnchorOffset(cam);
            s.Ox = x - a.X; s.Oy = y - a.Y;
            var r = dst.LockRect;
            return SR2D.Native.VoxelRender(&s, dst.Ptr, dst.Width, r.Left, r.Top, r.Right, r.Bottom, pick);
        }

        /// <summary>Projected position of the anchor point (grid units -> pixels, before the screen offset).</summary>
        public System.Numerics.Vector2 AnchorOffset(VoxelCamera cam)
        {
            switch (cam.Anchor)
            {
                case VoxelAnchor.Origin: return default;
                case VoxelAnchor.BottomCenter: return cam.Project(w * 0.5f, h * 0.5f, 0);
                case VoxelAnchor.TopLeftOfBounds: { var b = cam.Bounds(w, h, d); return new System.Numerics.Vector2(b.Left, b.Top); }
                default: return cam.Project(w * 0.5f, h * 0.5f, d * 0.5f);
            }
        }

        /// <summary>Screen rectangle (pixels) the grid covers when drawn with anchor at (x, y).</summary>
        public System.Drawing.Rectangle ScreenBounds(VoxelCamera cam, float x, float y)
        {
            var b = cam.Bounds(w, h, d); var a = AnchorOffset(cam);
            float pad = cam.EffectiveMode == VoxelMode.Points ? 1f : 0f;
            return System.Drawing.Rectangle.FromLTRB((int)MathF.Floor(b.Left - a.X + x - pad), (int)MathF.Floor(b.Top - a.Y + y - pad), (int)MathF.Ceiling(b.Right - a.X + x + pad) + 1, (int)MathF.Ceiling(b.Bottom - a.Y + y + pad) + 1);
        }

        /// <summary>
        /// Draw with a pick buffer and return what is under pixel (px, py): the voxel index (-1 = nothing) and the face
        /// hit (0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z; -1 on grids of 2^28 cells or more, where the pick value has no room for it). Use <see cref="Neighbour"/> to place a block against that face.
        /// This renders once into a scratch sprite; for many queries per frame keep your own pick buffer.
        /// </summary>
        public (int index, int face) Pick(Sprite dstLike, VoxelCamera cam, float x, float y, int px, int py)
        {
            if (dstLike == null || (uint)px >= (uint)dstLike.Width || (uint)py >= (uint)dstLike.Height) return (-1, -1);
            int n = dstLike.Width * dstLike.Height;
            int* buf = (int*)NativeMemory.Alloc((nuint)(n * 4L));
            try
            {
                new Span<int>(buf, n).Fill(-1);
                using (var tmp = new Sprite(dstLike.Width, dstLike.Height, SR2D.Op.Paint))
                {
                    tmp.SetLockRect(dstLike.LockRect);
                    Draw(tmp, cam, x, y, VoxelLighting.None, buf);
                }
                int p = buf[py * dstLike.Width + px];
                if (p < 0) return (-1, -1);
                return cells < (1 << 28) ? (p >> 3, p & 7) : (p, -1);   // grids of 2^28 cells and more: the pick buffer holds the bare index, no face
            }
            finally { NativeMemory.Free(buf); }
        }

        /// <summary>(x, y, z) of a cell index.</summary>
        public (int x, int y, int z) Coords(int index) { int x = index % w; int t = index / w; return (x, t % h, t / h); }
        /// <summary>Cell in front of a face (the place a new block goes when the user clicks that face).</summary>
        public (int x, int y, int z) Neighbour(int index, int face)
        {
            var (x, y, z) = Coords(index);
            switch (face) { case 0: return (x + 1, y, z); case 1: return (x - 1, y, z); case 2: return (x, y + 1, z); case 3: return (x, y - 1, z); case 4: return (x, y, z + 1); default: return (x, y, z - 1); }
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            ObjectsDispose();
            if (vox != null) { NativeMemory.Free(vox); vox = null; }
            if (faces != null) { NativeMemory.Free(faces); faces = null; }
            if (light != null) { NativeMemory.Free(light); light = null; }
            GC.SuppressFinalize(this);
        }
        ~VoxelGrid() { if (!disposed) { if (vox != null) NativeMemory.Free(vox); if (faces != null) NativeMemory.Free(faces); if (light != null) NativeMemory.Free(light); } }
    }

    internal unsafe partial class Sprite
    {
        /// <summary>
        /// Draw a voxel grid with the given camera, its anchor (grid centre by default) at (x, y). Respects the lock rect;
        /// writes opaque pixels (Paint). Returns the number of voxels drawn.
        /// </summary>
        public int DrawVoxels(VoxelGrid grid, VoxelCamera cam, float x, float y, VoxelLighting lighting = VoxelLighting.Faces)
            => grid.Draw(this, cam, x, y, lighting);

        /// <summary>
        /// Same, split into horizontal bands rendered in parallel (see <see cref="DrawParallel"/>). Worth it above ~100k drawn voxels;
        /// each band only walks the rows that project into it.
        /// </summary>
        public int DrawVoxelsParallel(VoxelGrid grid, VoxelCamera cam, float x, float y, VoxelLighting lighting = VoxelLighting.Faces, int Threads = 0)
        {
            if ((int)lighting >= 2) grid.Update(); else grid.UpdateFaces();     // once, outside the workers
            int total = 0;
            DrawParallel(v => System.Threading.Interlocked.Add(ref total, grid.Draw(v, cam, x, y, lighting)), Threads, 16);
            return total;
        }

        /// <summary>
        /// Same picture as <see cref="DrawVoxels"/>, drawn in <paramref name="steps"/> slab chunks (far to near) with
        /// <paramref name="progress"/>(done, total, voxelsSoFar) called after each: report a progress bar, pump the message
        /// queue, present the half-finished frame. Return false from the callback to stop early (the picture is then
        /// complete only for the far part). Slightly slower than one call (per-chunk setup, a few microseconds each).
        /// <paramref name="parallel"/> renders every chunk with <see cref="DrawParallel"/> bands.
        /// </summary>
        public int DrawVoxelsProgressive(VoxelGrid grid, VoxelCamera cam, float x, float y, VoxelLighting lighting, int steps, Func<int, int, int, bool> progress, bool parallel = false, int Threads = 0)
        {
            if ((int)lighting >= 2) grid.Update(); else grid.UpdateFaces();
            int depth = grid.Depth; steps = Math.Clamp(steps, 1, Math.Max(1, depth));
            int total = 0;
            for (int i = 0; i < steps; i++)
            {
                int z0 = (int)((long)depth * i / steps), z1 = (int)((long)depth * (i + 1) / steps);
                if (z1 <= z0) continue;
                if (parallel) DrawParallel(v => System.Threading.Interlocked.Add(ref total, grid.Draw(v, cam, x, y, lighting, null, z0, z1)), Threads, 16);
                else total += grid.Draw(this, cam, x, y, lighting, null, z0, z1);
                if (progress != null && !progress(i + 1, steps, total)) break;
            }
            return total;
        }
    }
}
