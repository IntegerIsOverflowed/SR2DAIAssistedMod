using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Sr2d64CSport
{
    /// <summary>Which built-in picture the colour / normal / keyed sprites use.</summary>
    internal enum AssetSet
    {
        /// <summary>Lenna photo + her normal map (embedded WebPs, demo/assets_builtin/), resampled to the sprite size.</summary>
        Lenna,
        /// <summary>The original procedural bricks + dome height field (the legacy look; the normal map is derived from the same field).</summary>
        Bricks,
    }

    /// <summary>
    /// Test sprites: the Lenna photo (embedded) by default, or the procedural bricks (<see cref="AssetSet.Bricks"/>).
    /// If a file with the matching name exists in an "assets" folder next to the executable it wins over both
    /// (color.png, normal.png, light.png, mask.png, tile.png).
    /// </summary>
    internal sealed class Assets : IDisposable
    {
        public int Size { get; }
        public AssetSet Set { get; }

        /// <summary>Opaque colour sprite (bricks + gradient + text-like details).</summary>
        public Sprite Color { get; }
        /// <summary>Colour sprite with real alpha (soft disc / glow) - for AlphaBlend.</summary>
        public Sprite Alpha { get; }
        /// <summary>Colour-keyed sprite (magenta key already applied -> Op = AlphaTest).</summary>
        public Sprite Keyed { get; }
        /// <summary>Tangent-space normal map (R=x, G=y, B=z, 128 = flat) matching Color.</summary>
        public Sprite Normal { get; }
        /// <summary>Light map for EBM (wc x hc lookup), a bright spot with a ring.</summary>
        public Sprite Light { get; }
        /// <summary>Environment image for EBM: sky gradient, horizon, ground, a sun - reads as chrome when looked up by the normal.</summary>
        public Sprite Env { get; }
        /// <summary>Mask sprite: several shapes in bits 0..3 of each pixel.</summary>
        public Sprite Mask { get; }
        /// <summary>Small seamless tile for TileDraw.</summary>
        public Sprite Tile { get; }

        public const int MaskCircle = 1, MaskDiamond = 2, MaskStripes = 4, MaskChecker = 8;

        public Assets(int size, AssetSet set = AssetSet.Lenna)
        {
            Size = size; Set = set;
            string dir = Path.Combine(AppContext.BaseDirectory, "assets");
            Func<int, int, int[]> color = set == AssetSet.Lenna ? (w, h) => Embedded("lenna.webp", w, h) ?? GenColor(w, h) : GenColor;
            Func<int, int, int[]> normal = set == AssetSet.Lenna ? (w, h) => Embedded("lenna_normal.webp", w, h) ?? GenNormal(w, h) : GenNormal;
            Color = LoadOr(Path.Combine(dir, "color.png"), size, size, color);
            Normal = LoadOr(Path.Combine(dir, "normal.png"), size, size, normal);
            Light = LoadOr(Path.Combine(dir, "light.png"), 256, 256, GenLight);
            Env = LoadOr(Path.Combine(dir, "env.png"), 256, 256, GenEnv);
            Mask = LoadOr(Path.Combine(dir, "mask.png"), size, size, GenMask);
            Tile = LoadOr(Path.Combine(dir, "tile.png"), 64, 64, GenTile);
            Alpha = Gen(size, size, GenAlpha);
            Keyed = Gen(size, size, (w, h) => GenKeyed(w, h, Color.Pixels.ToArray()));
            Keyed.AddColorKey(0xFF00FF);
            Keyed.Op = SR2D.Op.AlphaTest;
        }

        /// <summary>Embedded picture (demo/assets_builtin/*.webp or *.png, EmbeddedResource) decoded and area-resampled to w x h; null when missing.</summary>
        static int[]? Embedded(string name, int w, int h)
        {
            try
            {
                var asm = typeof(Assets).Assembly;
                string? res = null;
                foreach (var n in asm.GetManifestResourceNames()) if (n.EndsWith(name, StringComparison.OrdinalIgnoreCase)) { res = n; break; }
                if (res == null) return null;
                using var st = asm.GetManifestResourceStream(res); if (st == null) return null;
                using var ms = new MemoryStream(); st.CopyTo(ms); var bytes = ms.ToArray();
                int pw, ph; int[] px;
                if (WebP.IsWebP(bytes)) (pw, ph, px) = WebP.Decode(bytes);                          // cs/WebP.cs, no GDI+ (works headless too)
                else { var pd = PngDecoder.Decode(bytes) ?? throw new InvalidDataException(name); (pw, ph, px) = (pd.Width, pd.Height, pd.Argb); }
                if (pw == w && ph == h) return px;
                using var src = new Sprite(pw, ph); px.CopyTo(src.Pixels);
                using var spr = new Sprite(src, SR2D.Transform.None, w, h);   // RESIZE ctor: area average when shrinking
                return spr.Pixels.ToArray();
            }
            catch { return null; }
        }

        // an override in <exe>/assets/: name.webp is tried first, then name.png (the Sprite(string) constructor sniffs WebP itself)
        static Sprite LoadOr(string file, int w, int h, Func<int, int, int[]> gen)
        {
            foreach (var f in new[] { Path.ChangeExtension(file, ".webp"), file })
            {
                if (!File.Exists(f)) continue;
                try { return new Sprite(f, SR2D.Transform.None, w, h); }
                catch { /* fall through to procedural */ }
            }
            return Gen(w, h, gen);
        }

        static Sprite Gen(int w, int h, Func<int, int, int[]> gen)
        {
            var spr = new Sprite(w, h);
            gen(w, h).AsSpan().CopyTo(spr.Pixels);
            return spr;
        }

        // ------------------------------------------------------------- generators
        static int Argb(int a, int r, int g, int b) => (a << 24) | (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
        static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

        // deterministic value noise
        static float Hash(int x, int y) { uint h = (uint)(x * 374761393 + y * 668265263); h = (h ^ (h >> 13)) * 1274126177u; return ((h ^ (h >> 16)) & 0xffff) / 65535f; }
        static float Noise(float x, float y)
        {
            int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }
        static float Fbm(float x, float y) => Noise(x, y) * 0.5f + Noise(x * 2, y * 2) * 0.25f + Noise(x * 4, y * 4) * 0.125f + Noise(x * 8, y * 8) * 0.0625f;

        /// <summary>Height field shared by the colour and the normal map so they match.</summary>
        static float Height(int x, int y, int w, int h)
        {
            float u = (float)x / w, v = (float)y / h;
            // brick pattern
            float bw = 1f / 6, bh = 1f / 12;
            float row = MathF.Floor(v / bh);
            float uu = u + (((int)row & 1) == 1 ? bw * 0.5f : 0);
            float fx = (uu % bw) / bw, fy = (v % bh) / bh;
            float edge = MathF.Min(MathF.Min(fx, 1 - fx) / 0.08f, MathF.Min(fy, 1 - fy) / 0.12f);
            float brick = MathF.Min(1, edge);
            // big bump in the middle + noise
            float dx = u - 0.5f, dy = v - 0.5f;
            float dome = MathF.Max(0, 1 - (dx * dx + dy * dy) * 10);
            return brick * 0.55f + dome * 0.35f + Fbm(u * 12, v * 12) * 0.25f;
        }

        static int[] GenColor(int w, int h)
        {
            var p = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float hgt = Height(x, y, w, h);
                    float u = (float)x / w, v = (float)y / h;
                    int r = (int)(120 + 90 * hgt + 40 * u), g = (int)(70 + 80 * hgt + 30 * v), b = (int)(60 + 60 * hgt);
                    // a few coloured discs so rotations / flips are obvious
                    if (Disc(u, v, 0.2f, 0.2f, 0.08f)) { r = 240; g = 40; b = 40; }
                    if (Disc(u, v, 0.8f, 0.2f, 0.08f)) { r = 40; g = 220; b = 60; }
                    if (Disc(u, v, 0.2f, 0.8f, 0.08f)) { r = 60; g = 80; b = 240; }
                    p[y * w + x] = Argb(255, r, g, b);
                }
            return p;
        }
        static bool Disc(float u, float v, float cx, float cy, float r) { float dx = u - cx, dy = v - cy; return dx * dx + dy * dy < r * r; }

        static int[] GenNormal(int w, int h)
        {
            var p = new int[w * h];
            const float strength = 6f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float hl = Height(x - 1, y, w, h), hr = Height(x + 1, y, w, h), hu = Height(x, y - 1, w, h), hd = Height(x, y + 1, w, h);
                    float nx = (hl - hr) * strength, ny = (hu - hd) * strength, nz = 1f;
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    nx /= len; ny /= len; nz /= len;
                    p[y * w + x] = Argb(255, (int)(128 + nx * 127), (int)(128 + ny * 127), (int)(128 + nz * 127));
                }
            return p;
        }

        static int[] GenLight(int w, int h)
        {
            var p = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dx = (x - w * 0.5f) / (w * 0.5f), dy = (y - h * 0.5f) / (h * 0.5f);
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float spot = MathF.Max(0, 1 - d * 1.4f);
                    spot = spot * spot;
                    float ring = MathF.Exp(-MathF.Pow((d - 0.75f) * 12, 2));
                    int r = (int)(255 * spot + 90 * ring), g = (int)(230 * spot + 160 * ring), b = (int)(180 * spot + 255 * ring);
                    p[y * w + x] = Argb(255, r + 8, g + 10, b + 20);
                }
            return p;
        }

        static int[] GenEnv(int w, int h)
        {
            // Looked up with u = normal.x, v = normal.y (128 = flat -> image centre). Sky at the
            // top, bright horizon band through the centre, ground below, a sun upper-left.
            var p = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = (float)y / (h - 1), u = (float)x / (w - 1);
                    float r, g, b;
                    if (v < 0.5f)
                    {   // sky: deep blue at the top -> pale at the horizon
                        float t = v / 0.5f; t = t * t;
                        r = 20 + 200 * t; g = 60 + 180 * t; b = 160 + 95 * t;
                    }
                    else
                    {   // ground: warm near the horizon -> dark below
                        float t = (v - 0.5f) / 0.5f; t = MathF.Sqrt(t);
                        r = 230 - 190 * t; g = 170 - 140 * t; b = 110 - 90 * t;
                    }
                    float hz = MathF.Exp(-MathF.Pow((v - 0.5f) * 40, 2));          // thin bright horizon line
                    r += 60 * hz; g += 60 * hz; b += 60 * hz;
                    float du = u - 0.3f, dv = v - 0.22f, d = MathF.Sqrt(du * du + dv * dv);
                    float sun = MathF.Exp(-MathF.Pow(d * 10, 2)) + 0.5f * MathF.Exp(-MathF.Pow(d * 3.5f, 2));
                    r += 255 * sun; g += 230 * sun; b += 150 * sun;
                    p[y * w + x] = Argb(255, (int)MathF.Min(255, r), (int)MathF.Min(255, g), (int)MathF.Min(255, b));
                }
            return p;
        }

        static int[] GenMask(int w, int h)
        {
            var p = new int[w * h];
            float cx = w * 0.5f, cy = h * 0.5f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int m = 0;
                    float dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy < (w * 0.42f) * (w * 0.42f)) m |= MaskCircle;
                    if (MathF.Abs(dx) + MathF.Abs(dy) < w * 0.45f) m |= MaskDiamond;
                    if (((x + y) / 24) % 2 == 0) m |= MaskStripes;
                    if (((x / 32) + (y / 32)) % 2 == 0) m |= MaskChecker;
                    p[y * w + x] = m | 0x40 << 24; // some alpha so the mask is visible when drawn
                }
            return p;
        }

        static int[] GenTile(int w, int h)
        {
            var p = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float n = Fbm(x * 0.15f, y * 0.15f);
                    int edge = (x < 2 || y < 2 || x >= w - 2 || y >= h - 2) ? 40 : 0;
                    p[y * w + x] = Argb(255, (int)(50 + 60 * n) + edge, (int)(90 + 80 * n) + edge, (int)(60 + 50 * n) + edge);
                }
            return p;
        }

        static int[] GenAlpha(int w, int h)
        {
            var p = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (float)x / w - 0.5f, v = (float)y / h - 0.5f;
                    float d = MathF.Sqrt(u * u + v * v) * 2;
                    float a = MathF.Max(0, 1 - d);
                    a = a * a * (3 - 2 * a);
                    int alpha = (int)(255 * a);
                    // premultiplied-looking glow, colour varies with angle
                    float ang = MathF.Atan2(v, u);
                    int r = (int)(128 + 127 * MathF.Cos(ang)), g = (int)(128 + 127 * MathF.Cos(ang + 2.1f)), b = (int)(128 + 127 * MathF.Cos(ang + 4.2f));
                    p[y * w + x] = Argb(alpha, r, g, b);
                }
            return p;
        }

        static int[] GenKeyed(int w, int h, int[]? colour = null)
        {
            var p = colour != null && colour.Length == w * h ? colour : GenColor(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (float)x / w - 0.5f, v = (float)y / h - 0.5f;
                    // keep a star shape, key out the rest
                    float ang = MathF.Atan2(v, u);
                    float rad = 0.28f + 0.18f * MathF.Cos(ang * 5);
                    if (u * u + v * v > rad * rad) p[y * w + x] = unchecked((int)0xFFFF00FF);
                }
            return p;
        }

        public void Dispose()
        {
            Color.Dispose(); Alpha.Dispose(); Keyed.Dispose(); Normal.Dispose(); Light.Dispose(); Env.Dispose(); Mask.Dispose(); Tile.Dispose();
        }
    }
}
