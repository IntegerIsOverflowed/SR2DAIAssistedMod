using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    // ------------------------------------------------------------------------
    // Effect chains (new DLL, DRAW_FX).
    //
    //   var fx = new Effects().Blur(6).Noise(scale: 24, strength: 5, phase: t).Brightness(0.2f).Opacity(0.5f);
    //   canvas.DrawFx(sprite, x, y, fx);                              // 1:1
    //   canvas.DrawFxScaled(sprite, x, y, 2f, fx, Filter: Filter.Bilinear);
    //   canvas.DrawFxRotated(sprite, x, y, angle, fx);
    //
    // One native call: the sprite is copied premultiplied into a padded work image, the
    // stages run in order on it (in the DLL's cached scratch memory - no per-frame
    // allocation), and the result is composited once with the sprite's Op (or the one you
    // pass) through the same transform code as DrawScaled / DrawRotate2 / DrawQuad.
    // The sprite itself is never modified.
    //
    // Stage order matters and is the order you add them: Blur then Distort smears first
    // and wobbles the smeared picture; Distort then Blur wobbles the sharp one and softens.
    //
    //   fx.MotionBlur(20, 12)                      motion blur: direction 20 deg, 12 px trail
    //   fx.MotionBlurPath(arc, 2f)                 the trail follows a polyline (scaled x2)
    //   fx.Shadow(4, 4, 6)                         drop shadow (black 60 %, blur 6, offset 4,4)
    //   fx.Glow(8, 0xFFFFD700)                     additive glow (colours are int ARGB, as everywhere in SR2D;
    //   fx.Outline(2, 0xFFFFFFFF)                  System.Drawing.Color overloads exist for convenience)
    //   fx.Dilate(3) / fx.Erode(2)                 grow / shrink the shape
    //   fx.Blur(6); int b = fx.LastIndex; ... fx.Enable(b, false);   toggle a stage at run time
    //
    // PRE vs POST (Effects.Post / the 'Post' argument): by default the stages run at the
    // sprite's resolution and the transform comes last, so a distortion pattern is glued
    // to the sprite (rotates and scales with it) and magnified sprites are cheap. With
    // Post the sprite is transformed first and the stages run at screen resolution: blur
    // radius and wave length are then in screen pixels and the pattern stays fixed on the
    // screen while the sprite moves under it (heat haze, water surface).
    // ------------------------------------------------------------------------

    /// <summary>Procedural distortion patterns for <see cref="Effects.Distort"/>.</summary>
    /// <summary>How a blur trades quality for speed (see <see cref="Effects.Blur(int, BlurQuality, int)"/>).</summary>
    public enum BlurQuality : int
    {
        /// <summary>Three box passes (Gaussian-like profile). The default; identical to the previous <c>Blur(r)</c>.</summary>
        Gaussian = 0,
        /// <summary>Two box passes (triangle profile; ~30 % cheaper). The previous <c>Fast</c>.</summary>
        Fast = 1,
        /// <summary>One box pass - a plain box blur (~2.3x cheaper than Gaussian). Visibly "boxy" on hard edges, fine for soft haze / depth of field.</summary>
        Box = 2,
    }

    /// <summary>How <see cref="Effects.Diffuse"/> combines the picked pixel with the original.</summary>
    public enum DiffuseMode : int
    {
        /// <summary>The picked neighbour replaces the pixel (Photoshop "Normal").</summary>
        Normal = 0,
        /// <summary>Per channel the darker of original and picked ("Darken Only").</summary>
        DarkenOnly = 1,
        /// <summary>Per channel the lighter of original and picked ("Lighten Only").</summary>
        LightenOnly = 2,
    }

    public enum Distortion : int
    {
        /// <summary>Sine wave: rows (or the given direction) shift back and forth. Flag / heat shimmer.</summary>
        Wave = 0,
        /// <summary>Concentric rings from a centre point. Water drop.</summary>
        Ripple = 1,
        /// <summary>Smooth 3-D value noise (phase = time): soft wobble.</summary>
        Noise = 2,
        /// <summary>Three octaves of noise: more detail, ~2x the cost of Noise.</summary>
        Turbulence = 3
    }

    /// <summary>
    /// A reusable, ordered list of effect stages. Build it once (or mutate the parameters
    /// every frame - it is a plain list of structs, no native resources) and pass it to
    /// <c>Sprite.DrawFx*</c>. Every builder method returns <c>this</c> for chaining.
    /// </summary>
    public sealed class Effects
    {
        // native SR2D_FxStage, 80 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct Stage
        {
            public int Kind, Flags;
            public fixed int I[4];
            public fixed float F[8];
            public int* Map;
            public int Mw, Mh;
            public int Pad0, Pad1;
        }

        internal const int KindBlur = 1, KindDistortMap = 2, KindDistort = 3, KindColor = 4, KindMorph = 5, KindShadow = 6, KindDiffuse = 7, KindMotion = 8;
        internal const int FlagPost = 8, FlagPremul = 16, FlagOpaque = 32;
        const int FBlurFast = 1, FBlurBox = 2, FMapAlpha = 1, FWaveLong = 1, FWaveCross = 2, FInvert = 1, FSampleNearest = 0x100, FSampleBicubic = 0x200;
        const int FDisabled = 0x400, FMorphErode = 1, FMorphSquare = 2, FShadowGlow = 1, FShadowOnly = 2, FShadowFast = 4, FShadowBox = 8;
        const int FDiffuseDarken = 1, FDiffuseLighten = 2;
        /// <summary>Downscale value meaning "pick from the radius" (2 from r 12, 4 from r 24, 8 from r 48).</summary>
        public const int AutoDownscale = -1;

        internal readonly List<Stage> Stages = new List<Stage>();
        readonly List<Sprite?> maps = new List<Sprite?>();   // keeps DISTORT_MAP sprites alive / pinned by reference
        readonly List<IntPtr> bufs = new();                  // MotionBlurPath point arrays (native copies, freed in Clear / ~Effects)

        ~Effects() => FreeBufs();
        void FreeBufs()
        {
            for (int i = 0; i < bufs.Count; i++) { if (bufs[i] != IntPtr.Zero) Marshal.FreeHGlobal(bufs[i]); }
            bufs.Clear();
        }

        /// <summary>
        /// Run the stages after the transform, at screen resolution (see the class remarks).
        /// Ignored for plain 1:1 draws. Default false.
        /// </summary>
        public bool Post { get => post; set { if (post != value) { post = value; Version++; } } }
        bool post;

        /// <summary>
        /// Change counter: incremented by every call that alters the chain (add / remove /
        /// enable / disable / Clear / Post). Lets a cache (e.g. <see cref="LayeredSprite"/>)
        /// notice that a chain it drew with earlier has changed, without comparing stages.
        /// Editing the pixels of a DistortMap sprite is NOT tracked.
        /// </summary>
        public int Version { get; private set; }

        public int Count => Stages.Count;
        public Effects Clear() { if (Stages.Count > 0 || bufs.Count > 0) { Stages.Clear(); maps.Clear(); FreeBufs(); Version++; } return this; }

        /// <summary>Appends a copy of every stage of <paramref name="Other"/> (its enabled/disabled state included).</summary>
        public Effects Append(Effects Other)
        {
            if (Other == null || Other.Stages.Count == 0) return this;
            Stages.AddRange(Other.Stages); maps.AddRange(Other.maps);
            for (int i = 0; i < Other.Stages.Count; i++)           // path buffers are OWNED per chain: the append gets its own copy
            {
                IntPtr b = Other.bufs[i];
                Stage os = Other.Stages[i];
                if (b != IntPtr.Zero && os.Kind == KindMotion)
                {
                    int bytes; IntPtr nb;
                    unsafe { bytes = os.I[1] * 8; nb = Marshal.AllocHGlobal(bytes); Buffer.MemoryCopy((void*)b, (void*)nb, bytes, bytes); }
                    bufs.Add(nb);                                   // the append gets its own copy of the path points
                }
                else bufs.Add(IntPtr.Zero);
            }
            Version++;
            return this;
        }
        /// <summary>Independent copy of the chain (stages, DistortMap references, Post).</summary>
        public Effects Clone() { var e = new Effects(); e.Append(this); e.post = post; return e; }

        // ------------------------------------------------------------------ blur
        /// <summary>Gaussian-like blur of radius <paramref name="Radius"/> px (soft edges, cost independent of the radius). <paramref name="Fast"/>: two box passes instead of three.</summary>
        public Effects Blur(int Radius, bool Fast = false) => Blur(Radius, Fast ? BlurQuality.Fast : BlurQuality.Gaussian, 1);

        /// <summary>
        /// Blur with an explicit speed / quality trade-off. <paramref name="Quality"/>: number of
        /// box passes (<see cref="BlurQuality.Gaussian"/> 3, <see cref="BlurQuality.Fast"/> 2,
        /// <see cref="BlurQuality.Box"/> 1 - the cheapest). <paramref name="Downscale"/> = 2, 4 or 8:
        /// the picture is box-averaged by that factor, blurred with <c>Radius / Downscale</c> and
        /// bilinearly enlarged again - for large radii visually the same (mean error ~1 level at
        /// r 32 / 4) while the blur itself gets <c>Downscale²</c> times cheaper (r 24: 1.3 ms → 0.37 ms
        /// at 256², box + 4: 0.2 ms). 1 = none; <see cref="AutoDownscale"/> picks 2 from r 12,
        /// 4 from r 24, 8 from r 48 (k ≤ r / 6, so the block softening stays hidden inside the blur).
        /// </summary>
        public unsafe Effects Blur(int Radius, BlurQuality Quality, int Downscale = 1)
        {
            if (Radius <= 0) return this;
            Stage s = default; s.Kind = KindBlur; s.I[0] = Math.Min(Radius, 512);
            s.I[1] = Downscale < 0 ? -1 : Math.Clamp(Downscale, 1, 8);
            s.Flags = Quality == BlurQuality.Box ? FBlurBox : Quality == BlurQuality.Fast ? FBlurFast : 0;
            return Add(s, null);
        }
        /// <summary>Plain one-pass box blur - the fastest blur there is (~0.28 ms at 256² for any radius). Same as <c>Blur(Radius, BlurQuality.Box, Downscale)</c>.</summary>
        public Effects BoxBlur(int Radius, int Downscale = 1) => Blur(Radius, BlurQuality.Box, Downscale);

        // ------------------------------------------------------------------ diffuse
        /// <summary>
        /// Photoshop-style "Diffuse": every pixel is replaced by a randomly chosen pixel within
        /// <paramref name="Radius"/> px (1..64, square neighbourhood), <paramref name="Passes"/>
        /// times over (1..32; each pass scatters the previous result again, so 3 passes at r 1
        /// look like Photoshop's filter applied 3 times). A pick that would leave the sprite folds back onto
        /// the outermost real pixel (reflect-101 - no wrap-around, and the transparent surround is never
        /// picked), so edges scatter like interior ones: a border pixel keeps its value only through the
        /// ordinary 1/(2r+1)² self-pick. Flat areas stay flat. <paramref name="Seed"/> selects the random pattern:
        /// it is deterministic per seed (the same frame drawn twice is identical, and the pattern is anchored to the sprite /
        /// screen pixels, so DrawParallel bands agree); increment it per frame to animate the
        /// grain. <paramref name="Mode"/>: <see cref="DiffuseMode.Normal"/>, <c>DarkenOnly</c>
        /// (keep the per-channel darker of the original and the picked pixel) or <c>LightenOnly</c>.
        /// Cost ~0.09 ms per pass at 256² (AVX2). Margin = Radius * Passes.
        /// </summary>
        public unsafe Effects Diffuse(int Radius = 1, int Passes = 1, int Seed = 0, DiffuseMode Mode = DiffuseMode.Normal)
        {
            if (Radius <= 0 || Passes <= 0) return this;
            Stage s = default; s.Kind = KindDiffuse;
            s.I[0] = Math.Clamp(Radius, 1, 64); s.I[1] = Math.Clamp(Passes, 1, 32); s.I[2] = Seed;
            s.Flags = Mode == DiffuseMode.DarkenOnly ? FDiffuseDarken : Mode == DiffuseMode.LightenOnly ? FDiffuseLighten : 0;
            return Add(s, null);
        }
        /// <summary>
        /// Changes the seed of every Diffuse stage in the chain without rebuilding it (cheap; bumps
        /// <see cref="Version"/> so a <see cref="LayeredSprite"/> redraws the layer). Call once per
        /// frame to animate the grain.
        /// </summary>
        public unsafe Effects DiffuseSeed(int Seed)
        {
            bool any = false;
            for (int i = 0; i < Stages.Count; i++)
            {
                Stage s = Stages[i];
                if (s.Kind != KindDiffuse || s.I[2] == Seed) continue;
                s.I[2] = Seed; Stages[i] = s; any = true;
            }
            if (any) Version++;
            return this;
        }

        // ------------------------------------------------------------------ distortion
        /// <summary>
        /// Procedural distortion. <paramref name="Scale"/>: wavelength / feature size in pixels;
        /// <paramref name="Strength"/>: maximum displacement in pixels (negative flips it);
        /// <paramref name="Phase"/>: animate by increasing it (radians for Wave/Ripple - one
        /// full cycle per 2*PI; for Noise/Turbulence it is "time", +1 = a completely new
        /// pattern, so step it by ~0.02 per frame). <paramref name="Direction"/>: wave travel
        /// direction in radians (0 = along x). Ripple: <paramref name="CenterX"/>/<paramref name="CenterY"/>
        /// in sprite (or screen, Post) pixels, <paramref name="Falloff"/> radius after which it is calm (0 = none).
        /// </summary>
        public Effects Distort(Distortion Type, float Scale, float Strength, float Phase = 0,
                               float Direction = 0, float CenterX = 0, float CenterY = 0, float Falloff = 0,
                               SR2D.Filter Sampling = SR2D.Filter.Bilinear)
            => DistortEx(Type, Scale, Strength, Phase, Direction, CenterX, CenterY, Falloff, 0, Sampling);

        /// <summary>Sine wave across the sprite (rows shift left/right for Direction 0). See <see cref="Distort"/>.</summary>
        public Effects Wave(float Wavelength, float Strength, float Phase = 0, float Direction = 0, bool Longitudinal = false, bool Cross = false, SR2D.Filter Sampling = SR2D.Filter.Bilinear)
            => DistortEx(Distortion.Wave, Wavelength, Strength, Phase, Direction, 0, 0, 0, (Longitudinal ? FWaveLong : 0) | (Cross ? FWaveCross : 0), Sampling);

        /// <summary>Concentric ripples around (CenterX, CenterY). See <see cref="Distort"/>.</summary>
        public Effects Ripple(float Wavelength, float Strength, float Phase, float CenterX, float CenterY, float Falloff = 0, SR2D.Filter Sampling = SR2D.Filter.Bilinear)
            => DistortEx(Distortion.Ripple, Wavelength, Strength, Phase, 0, CenterX, CenterY, Falloff, 0, Sampling);

        /// <summary>Smooth noise wobble; animate with <paramref name="Phase"/> (+1 = new pattern). See <see cref="Distort"/>.</summary>
        public Effects Noise(float Scale, float Strength, float Phase = 0, SR2D.Filter Sampling = SR2D.Filter.Bilinear)
            => DistortEx(Distortion.Noise, Scale, Strength, Phase, 0, 0, 0, 0, 0, Sampling);

        /// <summary>Three-octave noise wobble. See <see cref="Distort"/>.</summary>
        public Effects Turbulence(float Scale, float Strength, float Phase = 0, SR2D.Filter Sampling = SR2D.Filter.Bilinear)
            => DistortEx(Distortion.Turbulence, Scale, Strength, Phase, 0, 0, 0, 0, 0, Sampling);

        unsafe Effects DistortEx(Distortion type, float scale, float strength, float phase, float dir, float cx, float cy, float falloff, int flags, SR2D.Filter sampling)
        {
            if (strength == 0) return this;
            Stage s = default; s.Kind = KindDistort; s.I[0] = (int)type;
            s.F[0] = scale; s.F[1] = strength; s.F[2] = phase; s.F[3] = type == Distortion.Ripple ? cx : dir; s.F[4] = cy; s.F[5] = falloff;
            s.Flags = flags | SampleFlag(sampling);
            return Add(s, null);
        }

        /// <summary>
        /// Distortion by a height map (bump map): <paramref name="Map"/> is any sprite whose
        /// luminance (or alpha with <paramref name="UseAlpha"/>) is read as height; pixels are
        /// pushed along the slope. The map is tiled; <paramref name="Scale"/> magnifies it
        /// (1 = one texel per pixel, 2 = twice as large), <paramref name="Strength"/> is the
        /// maximum displacement in pixels (negative pulls instead of pushes). Scroll the map
        /// with <paramref name="OffsetX"/>/<paramref name="OffsetY"/> (pixels) to animate.
        /// The map sprite must stay alive and unchanged in size while this chain is used.
        /// </summary>
        public unsafe Effects DistortMap(Sprite Map, float Scale, float Strength, float OffsetX = 0, float OffsetY = 0, bool UseAlpha = false, SR2D.Filter Sampling = SR2D.Filter.Bilinear)
        {
            if (Map == null) throw new ArgumentNullException(nameof(Map));
            if (Strength == 0 || Map.Width <= 0 || Map.Height <= 0) return this;
            Stage s = default; s.Kind = KindDistortMap;
            s.F[0] = Scale; s.F[1] = Strength; s.F[2] = OffsetX; s.F[3] = OffsetY;
            s.Flags = (UseAlpha ? FMapAlpha : 0) | SampleFlag(Sampling);
            return Add(s, Map);
        }

        static int SampleFlag(SR2D.Filter f) => f == SR2D.Filter.Auto ? 0 : (((int)f & 4) != 0) ? FSampleBicubic : (((int)f & 1) != 0) ? 0 : FSampleNearest;

        // ------------------------------------------------------------------ colour
        /// <summary>
        /// Colour adjustment in one pass. Neutral values: Brightness 0, Contrast 1, Saturation 1,
        /// Gamma 1, Hue 0, Opacity 1, Tint 0. <paramref name="Brightness"/> -1..1 adds to all
        /// channels; <paramref name="Contrast"/> scales around mid grey (0 = flat grey, 2 = harsh);
        /// <paramref name="Saturation"/> 0 = greyscale, &gt;1 = vivid; <paramref name="Gamma"/>
        /// &gt;1 lifts the mid-tones, &lt;1 darkens them; <paramref name="Hue"/> rotates the hue
        /// in degrees; <paramref name="Opacity"/> 0..1 multiplies the alpha at draw time;
        /// <paramref name="Tint"/> 0..1 mixes the colour toward <paramref name="TintColor"/>
        /// (0xRRGGBB); <paramref name="Invert"/> negates the colour.
        /// Consecutive colour stages are merged into one.
        /// </summary>
        public unsafe Effects Color(float Brightness = 0, float Contrast = 1, float Saturation = 1, float Gamma = 1,
                                    float Hue = 0, float Opacity = 1, float Tint = 0, int TintColor = 0, bool Invert = false)
        {
            Stage s = default; s.Kind = KindColor;
            s.F[0] = Brightness; s.F[1] = Contrast; s.F[2] = Saturation; s.F[3] = Gamma; s.F[4] = Hue; s.F[5] = Opacity; s.F[6] = Tint;
            s.I[0] = TintColor & 0xffffff; s.Flags = Invert ? FInvert : 0;
            return Add(s, null);
        }
        /// <summary>Brightness only: -1..1 (0 = unchanged).</summary>
        public Effects Brightness(float Amount) => Color(Brightness: Amount);
        /// <summary>Contrast only: 1 = unchanged, 0 = flat grey, 2 = doubled.</summary>
        public Effects Contrast(float Factor) => Color(Contrast: Factor);
        /// <summary>Saturation only: 1 = unchanged, 0 = greyscale.</summary>
        public Effects Saturation(float Factor) => Color(Saturation: Factor);
        /// <summary>Gamma only: 1 = unchanged, &gt;1 brighter mid-tones.</summary>
        public Effects Gamma(float Value) => Color(Gamma: Value);
        /// <summary>Hue rotation in degrees.</summary>
        public Effects Hue(float Degrees) => Color(Hue: Degrees);
        /// <summary>Draws the sprite with this transparency (0 = invisible, 1 = as is) without touching its alpha channel.</summary>
        public Effects Opacity(float Value) => Color(Opacity: Value);
        /// <summary>Mixes the colour toward <paramref name="Color"/> (0xAARRGGBB / 0xRRGGBB, the alpha byte is ignored) by <paramref name="Amount"/> (0..1); 1 = a silhouette in that colour.</summary>
        public Effects Tint(int Color, float Amount) => this.Color(Tint: Amount, TintColor: Color & 0xffffff);
        /// <summary>Convenience overload taking a <see cref="System.Drawing.Color"/>.</summary>
        public Effects Tint(System.Drawing.Color Color, float Amount) => Tint(Color.ToArgb(), Amount);
        public Effects Grayscale() => Color(Saturation: 0);
        public Effects Invert() => Color(Invert: true);

        // ------------------------------------------------------------------ motion blur
        /// <summary>
        /// Motion blur: the picture averaged over <paramref name="Samples"/> taps along a straight trail
        /// (<paramref name="Degree"/> in degrees, 0 = right, 90 = down; <paramref name="Strength"/> = trail
        /// length in px - the first tap sits on the pixel, the last that far away). Samples 0 = automatic
        /// from the strength (2..32). Sampling is bilinear (<paramref name="Sampling"/> override). The trail
        /// is part of the draw's margin, nothing is cut off.
        /// </summary>
        public unsafe Effects MotionBlur(float Degree, float Strength, int Samples = 0, SR2D.Filter Sampling = SR2D.Filter.Bilinear)
        {
            if (!(Strength > 0) || Samples == 1) return this;
            int n = Samples <= 0 ? Math.Clamp((int)MathF.Ceiling(Strength) + 1, 2, 32) : Math.Clamp(Samples, 2, 64);
            Stage s = default; s.Kind = KindMotion;
            s.I[0] = n; s.F[0] = Degree; s.F[1] = Strength;
            s.I[2] = BitConverter.SingleToInt32Bits(Strength);   // trail extent for the managed Margin mirror
            s.Flags = SampleFlag(Sampling);
            return Add(s, null);
        }

        /// <summary>
        /// Motion blur along a custom path: <paramref name="Path"/> is a polyline of relative trail offsets
        /// in pixels (2..32 points), walked uniformly and scaled by <paramref name="Strength"/> (1 = the
        /// points as given). The points are COPIED at call time - the array can be reused afterwards.
        /// <paramref name="Samples"/> 0 = automatic from the path extent (2..32).
        /// </summary>
        public unsafe Effects MotionBlurPath(System.Drawing.PointF[] Path, float Strength = 1f, int Samples = 0, SR2D.Filter Sampling = SR2D.Filter.Bilinear)
        {
            if (Path == null || Path.Length < 2 || Path.Length > 32 || !(Strength > 0)) return this;
            float extent = 0;
            for (int i = 0; i < Path.Length; i++) extent = MathF.Max(extent, MathF.Sqrt(Path[i].X * Path[i].X + Path[i].Y * Path[i].Y) * Strength);
            IntPtr p = Marshal.AllocHGlobal(Path.Length * 8);
            float* f = (float*)p;
            for (int i = 0; i < Path.Length; i++) { f[i * 2] = Path[i].X; f[i * 2 + 1] = Path[i].Y; }
            int n = Samples <= 0 ? Math.Clamp((int)MathF.Ceiling(extent) + 1, 2, 32) : Math.Clamp(Samples, 2, 64);
            Stage s = default; s.Kind = KindMotion;
            s.I[0] = n; s.I[1] = Path.Length; s.F[1] = Strength;
            s.I[2] = BitConverter.SingleToInt32Bits(extent);
            s.Flags = SampleFlag(Sampling);
            return Add(s, null, p);
        }
        /// <summary>Path as x / y pairs (an even count of floats, 2..32 points), see the PointF overload.</summary>
        public unsafe Effects MotionBlurPath(float[] PointsXY, float Strength = 1f, int Samples = 0, SR2D.Filter Sampling = SR2D.Filter.Bilinear)
        {
            if (PointsXY == null || PointsXY.Length < 4 || PointsXY.Length > 64 || (PointsXY.Length & 1) != 0) return this;
            var pts = new System.Drawing.PointF[PointsXY.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new System.Drawing.PointF(PointsXY[i * 2], PointsXY[i * 2 + 1]);
            return MotionBlurPath(pts, Strength, Samples, Sampling);
        }

        // ------------------------------------------------------------------ outline / morphology
        /// <summary>
        /// Grows the sprite's shape by <paramref name="Radius"/> px (dilate): every pixel takes the
        /// maximum of its neighbourhood, so the alpha edge moves outward and the colour follows.
        /// Combined with a following <see cref="Color"/> stage and the sprite drawn on top this
        /// gives a coloured outline - or use <see cref="Outline"/> which does exactly that.
        /// <paramref name="Square"/>: square neighbourhood instead of a disc. Cost grows with the
        /// radius (~0.3 ms at r 2, ~0.9 ms at r 6 on a 256² sprite, AVX2).
        /// </summary>
        public unsafe Effects Dilate(int Radius, bool Square = false)
        {
            if (Radius <= 0) return this;
            Stage s = default; s.Kind = KindMorph; s.I[0] = Math.Min(Radius, 256); s.Flags = Square ? FMorphSquare : 0;
            return Add(s, null);
        }
        /// <summary>Shrinks the sprite's shape by <paramref name="Radius"/> px (erode) - the opposite of <see cref="Dilate"/>; thin parts disappear.</summary>
        public unsafe Effects Erode(int Radius, bool Square = false)
        {
            if (Radius <= 0) return this;
            Stage s = default; s.Kind = KindMorph; s.I[0] = Math.Min(Radius, 256); s.Flags = FMorphErode | (Square ? FMorphSquare : 0);
            return Add(s, null);
        }
        /// <summary>
        /// Solid outline of <paramref name="Thickness"/> px in <paramref name="Color"/> around the
        /// sprite, with the sprite itself on top. (Dilate + tint as the "shadow" of a
        /// <see cref="Shadow"/> stage with no blur and no offset - one stage.)
        /// </summary>
        public Effects Outline(int Thickness, int Color, float Opacity = 1)
            => ShadowEx(0, Thickness, Color, Opacity, 0, 0, 0);
        /// <summary>Convenience overload taking a <see cref="System.Drawing.Color"/>.</summary>
        public Effects Outline(int Thickness, System.Drawing.Color Color, float Opacity = 1) => Outline(Thickness, Color.ToArgb(), Opacity);

        // ------------------------------------------------------------------ shadow / glow
        /// <summary>
        /// Drop shadow under the sprite in one stage: the sprite's alpha shape, optionally grown by
        /// <paramref name="Spread"/> px, blurred by <paramref name="Blur"/> px, filled with
        /// <paramref name="Color"/> (0xAARRGGBB as everywhere in SR2D; its alpha byte multiplies
        /// <paramref name="Opacity"/>, so 0xFF000000 and 0x000000 both mean solid black) at
        /// <paramref name="Opacity"/>, offset by (<paramref name="OffsetX"/>, <paramref name="OffsetY"/>)
        /// px and placed UNDER the sprite. Everything after this stage (e.g. a distortion) applies
        /// to sprite and shadow together. <paramref name="Fast"/>: 2-pass blur.
        /// </summary>
        public Effects Shadow(float OffsetX, float OffsetY, int Blur, int Color, float Opacity = 0.6f, int Spread = 0, bool Fast = false, int Downscale = 1)
            => ShadowEx(Blur, Spread, Color, Opacity, OffsetX, OffsetY, Fast ? FShadowFast : 0, Downscale);
        /// <summary>Drop shadow with an explicit blur <paramref name="Quality"/> / <paramref name="Downscale"/> (see <see cref="Blur(int, BlurQuality, int)"/>) - large soft shadows for a fraction of the cost.</summary>
        public Effects Shadow(float OffsetX, float OffsetY, int Blur, int Color, BlurQuality Quality, int Downscale = AutoDownscale, float Opacity = 0.6f, int Spread = 0)
            => ShadowEx(Blur, Spread, Color, Opacity, OffsetX, OffsetY, Quality == BlurQuality.Box ? FShadowBox : Quality == BlurQuality.Fast ? FShadowFast : 0, Downscale);
        /// <summary>Drop shadow with the default colour (black, 60 %).</summary>
        public Effects Shadow(float OffsetX, float OffsetY, int Blur) => Shadow(OffsetX, OffsetY, Blur, unchecked((int)0xFF000000));
        /// <summary>Convenience overload taking a <see cref="System.Drawing.Color"/>.</summary>
        public Effects Shadow(float OffsetX, float OffsetY, int Blur, System.Drawing.Color Color, float Opacity = 0.6f, int Spread = 0, bool Fast = false, int Downscale = 1)
            => Shadow(OffsetX, OffsetY, Blur, Color.ToArgb(), Opacity, Spread, Fast, Downscale);
        /// <summary>
        /// Drop shadow given the Photoshop way: light <paramref name="AngleDeg"/> (degrees, 0 = the shadow falls to the
        /// right, 90 = downwards on screen, i.e. the shadow is cast TOWARDS this direction) and <paramref name="Distance"/>
        /// px. Same as <see cref="Shadow(float, float, int, int, float, int, bool, int)"/> with
        /// OffsetX = cos(angle)·Distance, OffsetY = sin(angle)·Distance.
        /// </summary>
        public Effects ShadowAt(float AngleDeg, float Distance, int Blur, int Color = unchecked((int)0xFF000000), float Opacity = 0.6f, int Spread = 0, bool Fast = false, int Downscale = 1)
            => Shadow((float)Math.Cos(AngleDeg * Math.PI / 180) * Distance, (float)Math.Sin(AngleDeg * Math.PI / 180) * Distance, Blur, Color, Opacity, Spread, Fast, Downscale);
        /// <summary>Only the shadow, angle/distance form of <see cref="ShadowOnly(float, float, int, int, float, int, bool, int)"/>.</summary>
        public Effects ShadowOnlyAt(float AngleDeg, float Distance, int Blur, int Color = unchecked((int)0xFF000000), float Opacity = 0.6f, int Spread = 0, bool Fast = false, int Downscale = 1)
            => ShadowOnly((float)Math.Cos(AngleDeg * Math.PI / 180) * Distance, (float)Math.Sin(AngleDeg * Math.PI / 180) * Distance, Blur, Color, Opacity, Spread, Fast, Downscale);
        /// <summary>
        /// Glow: the blurred (and optionally grown) shape in <paramref name="Color"/> (0xAARRGGBB)
        /// ADDED on top of the sprite, so the light spills out over the edge. Neon, highlights,
        /// "selected" marker.
        /// </summary>
        public Effects Glow(int Blur, int Color, float Intensity = 1, int Spread = 0, bool Fast = false, int Downscale = 1)
            => ShadowEx(Blur, Spread, Color, Intensity, 0, 0, FShadowGlow | (Fast ? FShadowFast : 0), Downscale);
        /// <summary>Glow with an explicit blur <paramref name="Quality"/> / <paramref name="Downscale"/> (see <see cref="Blur(int, BlurQuality, int)"/>).</summary>
        public Effects Glow(int Blur, int Color, BlurQuality Quality, int Downscale = AutoDownscale, float Intensity = 1, int Spread = 0)
            => ShadowEx(Blur, Spread, Color, Intensity, 0, 0, FShadowGlow | (Quality == BlurQuality.Box ? FShadowBox : Quality == BlurQuality.Fast ? FShadowFast : 0), Downscale);
        /// <summary>Convenience overload taking a <see cref="System.Drawing.Color"/>.</summary>
        public Effects Glow(int Blur, System.Drawing.Color Color, float Intensity = 1, int Spread = 0, bool Fast = false, int Downscale = 1)
            => Glow(Blur, Color.ToArgb(), Intensity, Spread, Fast, Downscale);
        /// <summary>Only the shadow (nothing of the sprite itself) - e.g. to draw all shadows of a scene first, then the sprites.</summary>
        public Effects ShadowOnly(float OffsetX, float OffsetY, int Blur, int Color, float Opacity = 0.6f, int Spread = 0, bool Fast = false, int Downscale = 1)
            => ShadowEx(Blur, Spread, Color, Opacity, OffsetX, OffsetY, FShadowOnly | (Fast ? FShadowFast : 0), Downscale);
        /// <summary>Convenience overload taking a <see cref="System.Drawing.Color"/>.</summary>
        public Effects ShadowOnly(float OffsetX, float OffsetY, int Blur, System.Drawing.Color Color, float Opacity = 0.6f, int Spread = 0, bool Fast = false, int Downscale = 1)
            => ShadowOnly(OffsetX, OffsetY, Blur, Color.ToArgb(), Opacity, Spread, Fast, Downscale);

        unsafe Effects ShadowEx(int blur, int spread, int argb, float opacity, float ox, float oy, int flags, int downscale = 1)
        {
            // alpha byte: 0 is treated as "not given" (0xRRGGBB literals), anything else scales the opacity
            int a = (argb >> 24) & 255; if (a == 0) a = 255;
            Stage s = default; s.Kind = KindShadow;
            s.I[0] = Math.Clamp(blur, 0, 512); s.I[1] = Math.Clamp(spread, 0, 256); s.I[2] = argb & 0xffffff;
            s.I[3] = downscale < 0 ? -1 : Math.Clamp(downscale, 1, 8);
            s.F[0] = opacity * (a / 255f); s.F[1] = ox; s.F[2] = oy; s.Flags = flags;
            return Add(s, null);
        }

        // ------------------------------------------------------------------ per-stage on / off
        /// <summary>
        /// Enables or disables stage <paramref name="Index"/> (0-based, in the order added) without
        /// removing it: a disabled stage is skipped by the DLL and adds no margin. Build the chain
        /// once, toggle at run time.
        /// </summary>
        public unsafe Effects Enable(int Index, bool On = true)
        {
            Stage s = Stages[Index];
            int f = On ? s.Flags & ~FDisabled : s.Flags | FDisabled;
            if (f == s.Flags) return this;
            s.Flags = f;
            Stages[Index] = s;
            Version++;
            return this;
        }
        public Effects Disable(int Index) => Enable(Index, false);
        /// <summary>Whether stage <paramref name="Index"/> is currently enabled.</summary>
        public bool IsEnabled(int Index) => (Stages[Index].Flags & FDisabled) == 0;
        /// <summary>Index of the stage added last (for <see cref="Enable"/>): <c>fx.Blur(4); int blur = fx.LastIndex;</c></summary>
        public int LastIndex => Stages.Count - 1;
        /// <summary>Removes the last stage, with its native scratch buffer: <see cref="bufs"/> must stay parallel to
        /// <see cref="Stages"/>, or a later <see cref="Clear"/> would see a non-empty buffer list and bump the version
        /// of an already-empty chain (and the buffer would leak until the final free).</summary>
        public Effects RemoveLast()
        {
            if (Stages.Count == 0) return this;
            Stages.RemoveAt(Stages.Count - 1);
            maps.RemoveAt(maps.Count - 1);
            if (bufs.Count > 0)
            {
                int last = bufs.Count - 1;
                if (bufs[last] != IntPtr.Zero) Marshal.FreeHGlobal(bufs[last]);
                bufs.RemoveAt(last);
            }
            Version++;
            return this;
        }

        // ------------------------------------------------------------------ internals
        unsafe Effects Add(Stage s, Sprite? map, IntPtr buf = default)
        {
            Stages.Add(s); maps.Add(map); bufs.Add(buf); Version++;
            return this;
        }

        /// <summary>Total padding the chain adds around the sprite (px), for layout purposes.</summary>
        public unsafe int Margin
        {
            get
            {
                int m = 0;
                foreach (var s in Stages)
                {
                    // mirrors fx_margin() in sr2d_fx.inl
                    if ((s.Flags & FDisabled) != 0) continue;
                    if (s.Kind == KindBlur)
                    {
                        int r = Math.Clamp(s.I[0], 0, 512), k = BlurDown(r, s.I[1]);
                        m += r * ((s.Flags & FBlurBox) != 0 ? 1 : (s.Flags & FBlurFast) != 0 ? 2 : 3) + (k > 1 ? k : 0);
                    }
                    else if (s.Kind == KindDiffuse) m += Math.Clamp(s.I[0], 1, 64) * Math.Clamp(s.I[1], 1, 32);
                    else if (s.Kind == KindMorph) m += Math.Clamp(s.I[0], 0, 256);
                    else if (s.Kind == KindShadow)
                    {
                        int ox = ShadowShift(s.F[1]), oy = ShadowShift(s.F[2]);
                        int r = Math.Clamp(s.I[0], 0, 512), k = BlurDown(r, s.I[3]);
                        m += r * ((s.Flags & FShadowBox) != 0 ? 1 : (s.Flags & FShadowFast) != 0 ? 2 : 3) + (k > 1 ? k : 0) + Math.Clamp(s.I[1], 0, 256) + Math.Max(Math.Abs(ox), Math.Abs(oy));
                    }
                    else if (s.Kind == KindDistort || s.Kind == KindDistortMap)
                    {
                        float a = Math.Abs(s.F[1]);
                        if (s.Kind == KindDistort && s.I[0] == (int)Distortion.Wave && (s.Flags & FWaveCross) != 0) a *= 1.5f;
                        if (!(a <= 2048f)) a = 2048f;                       // also NaN / infinity
                        m += (int)Math.Ceiling(a) + ((s.Flags & FSampleBicubic) != 0 ? 2 : 1);
                    }
                    else if (s.Kind == KindMotion)
                    {
                        float a = BitConverter.Int32BitsToSingle(s.I[2]);   // the trail extent stored by the builder
                        if (!(a <= 2048f)) a = 2048f;
                        m += (int)Math.Ceiling(a) + ((s.Flags & FSampleBicubic) != 0 ? 2 : 1);
                    }
                }
                return m;
            }
        }


        static int BlurDown(int r, int k)   // mirrors fx_blur_down()
        {
            if (k < 0) k = r >= 48 ? 8 : r >= 24 ? 4 : r >= 12 ? 2 : 1;
            return k <= 1 ? 1 : k <= 2 ? 2 : k <= 4 ? 4 : 8;
        }
        static int ShadowShift(float v)
        {
            if (!(v > -1024f)) v = v < 0 ? -1024f : 0f;
            if (v > 1024f) v = 1024f;
            return (int)(v < 0 ? v - 0.5f : v + 0.5f);
        }

        /// <summary>
        /// Fills <paramref name="dst"/> (at least Count entries) with the native stages, resolving
        /// map pointers, and returns the number written. Called by Sprite.DrawFx while the
        /// destination buffer is in use; map sprites are read from their current buffers.
        /// </summary>
        internal unsafe int Bake(Stage* dst)
        {
            for (int i = 0; i < Stages.Count; i++)
            {
                Stage s = Stages[i];
                Sprite? m = maps[i];
                if (m != null)
                {
                    if (m.Width <= 0 || m.Height <= 0) { s.Kind = 0; }
                    else { s.Map = m.PixelPtr; s.Mw = m.Width; s.Mh = m.Height; }
                }
                else if (bufs[i] != IntPtr.Zero) s.Map = (int*)bufs[i];   // MotionBlurPath's point array
                dst[i] = s;
            }
            return Stages.Count;
        }
    }

    public unsafe partial class Sprite
    {
        /// <summary>Raw pixel pointer (for Effects map stages).</summary>
        internal int* PixelPtr => pBuf;

        /// <summary>
        /// Draws <paramref name="Src"/> through the effect chain <paramref name="Fx"/> with its
        /// top-left corner at (x, y) (blur / displacement may extend beyond the sprite rect by
        /// <see cref="Effects.Margin"/>). <paramref name="Op"/> default = Src.Op; AlphaBlend /
        /// AlphaTest / AlphaOver composite the premultiplied result correctly, ops that ignore
        /// alpha (Paint, Add, Max ...) get the alpha-weighted colour (fades to black outside).
        /// <paramref name="Opaque"/>: treat the source alpha as 255. Returns false if nothing was drawn.
        /// </summary>
        public bool DrawFx(Sprite Src, int x, int y, Effects Fx, SR2D.Op Op = SR2D.Op.DefaultOp, bool Opaque = false, int BlendFactor = 128)
        {
            float* q = stackalloc float[8] { x, y, x + Src.meWidth, y, x + Src.meWidth, y + Src.meHeight, x, y + Src.meHeight };
            return FxWarp(Src, q, null, 0, Fx, Op, SR2D.Filter.Nearest, Opaque, BlendFactor);
        }
        /// <summary>See <see cref="DrawFx(Sprite, int, int, Effects, SR2D.Op, bool, int)"/>.</summary>
        public bool DrawFx(Sprite Src, System.Drawing.Point At, Effects Fx, SR2D.Op Op = SR2D.Op.DefaultOp, bool Opaque = false, int BlendFactor = 128)
            => DrawFx(Src, At.X, At.Y, Fx, Op, Opaque, BlendFactor);

        /// <summary>Effect chain + scaling to DestWidth x DestHeight at (DestX, DestY); negative sizes flip.</summary>
        public bool DrawFxScaled(Sprite Src, int DestX, int DestY, int DestWidth, int DestHeight, Effects Fx,
                                 SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Bilinear, bool Opaque = false, int BlendFactor = 128)
        {
            float x0 = DestX, y0 = DestY, x1 = DestX + DestWidth, y1 = DestY + DestHeight;
            float* q = stackalloc float[8] { x0, y0, x1, y0, x1, y1, x0, y1 };
            return FxWarp(Src, q, null, 0, Fx, Op, Filter, Opaque, BlendFactor);
        }

        /// <summary>Effect chain + uniform scale around the pivot (source px; -1 = centre) landing on (DestX, DestY).</summary>
        public bool DrawFxScaled(Sprite Src, float DestX, float DestY, float Scale, Effects Fx, float PivotX = -1, float PivotY = -1,
                                 SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Bilinear, bool Opaque = false, int BlendFactor = 128)
        {
            if (Src.meWidth <= 0 || Src.meHeight <= 0) return false;
            if (PivotX < 0) PivotX = Src.meWidth * 0.5f;
            if (PivotY < 0) PivotY = Src.meHeight * 0.5f;
            float x0 = DestX - PivotX * Scale, y0 = DestY - PivotY * Scale;
            float x1 = x0 + Src.meWidth * Scale, y1 = y0 + Src.meHeight * Scale;
            float* q = stackalloc float[8] { x0, y0, x1, y0, x1, y1, x0, y1 };
            return FxWarp(Src, q, null, 0, Fx, Op, Filter, Opaque, BlendFactor);
        }

        /// <summary>Effect chain + rotation (radians, clockwise) and optional scaling around the pivot, like <see cref="DrawRotate2"/>.</summary>
        public bool DrawFxRotated(Sprite Src, int DestX, int DestY, float Angle, Effects Fx,
                                  int DestWidth = 0, int DestHeight = 0, float PivotX = -1, float PivotY = -1,
                                  SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Bilinear, bool Opaque = false, int BlendFactor = 128)
        {
            if (DestWidth == 0) DestWidth = Src.meWidth;
            if (DestHeight == 0) DestHeight = Src.meHeight;
            if (PivotX < 0) PivotX = Src.meWidth * 0.5f;
            if (PivotY < 0) PivotY = Src.meHeight * 0.5f;
            float sx = (float)DestWidth / Src.meWidth, sy = (float)DestHeight / Src.meHeight;
            float px = PivotX * sx, py = PivotY * sy;
            float c = (float)Math.Cos(Angle), s = (float)Math.Sin(Angle);
            float* q = stackalloc float[8];
            float* cx = stackalloc float[4] { -px, DestWidth - px, DestWidth - px, -px };
            float* cy = stackalloc float[4] { -py, -py, DestHeight - py, DestHeight - py };
            for (int i = 0; i < 4; i++)
            {
                q[i * 2] = DestX + cx[i] * c - cy[i] * s;
                q[i * 2 + 1] = DestY + cx[i] * s + cy[i] * c;
            }
            return FxWarp(Src, q, null, 0, Fx, Op, Filter, Opaque, BlendFactor);
        }

        /// <summary>Effect chain + arbitrary quad (TL, TR, BR, BL) with optional clip polygon, like <see cref="DrawQuad"/>.</summary>
        public bool DrawFxQuad(Sprite Src, ReadOnlySpan<PointF> Quad, Effects Fx, ReadOnlySpan<PointF> ClipPolygon = default,
                               SR2D.Op Op = SR2D.Op.DefaultOp, SR2D.Filter Filter = SR2D.Filter.Bilinear, bool Opaque = false, int BlendFactor = 128)
        {
            if (Quad.Length != 4) throw new ArgumentException("Quad must contain exactly 4 points.", nameof(Quad));
            float* q = stackalloc float[8];
            for (int i = 0; i < 4; i++) { q[i * 2] = Quad[i].X; q[i * 2 + 1] = Quad[i].Y; }
            fixed (PointF* pp = ClipPolygon)
            {
                return FxWarp(Src, q, ClipPolygon.Length >= 3 ? (float*)pp : null, ClipPolygon.Length, Fx, Op, Filter, Opaque, BlendFactor);
            }
        }

        private bool FxWarp(Sprite Src, float* quad, float* poly, int npoly, Effects Fx, SR2D.Op Op, SR2D.Filter Filter, bool Opaque, int BlendFactor)
        {
            if (Fx == null) throw new ArgumentNullException(nameof(Fx));
            if (meRight <= meLeft || meBottom <= meTop || Src.meWidth <= 0 || Src.meHeight <= 0) return false;
            if (Op == SR2D.Op.DefaultOp) Op = Src.Op;
            Filter = SR2D.Resolve(Filter, quad, Src.meWidth, Src.meHeight);
            int flags = ((int)Filter & 7) | (Fx.Post ? Effects.FlagPost : 0) | (Opaque ? Effects.FlagOpaque : 0)
                      | (Src.Premultiplied ? Effects.FlagPremul : 0);                // AlphaOver sprites are premultiplied by convention
            int opw = OpWord(Op, Src);
            int n = Fx.Count;
            if (n <= 16)
            {
                Effects.Stage* st = stackalloc Effects.Stage[16];
                n = Fx.Bake(st);
                return SR2D.Native.DrawFx(Src.pBuf, Src.meWidth, Src.meHeight, pBuf, meWidth, meLeft, meTop, meRight, meBottom,
                                          quad, poly, npoly, opw, flags, BlendFactor, st, n) != 0;
            }
            Effects.Stage[] arr = new Effects.Stage[n];
            fixed (Effects.Stage* st = arr)
            {
                n = Fx.Bake(st);
                return SR2D.Native.DrawFx(Src.pBuf, Src.meWidth, Src.meHeight, pBuf, meWidth, meLeft, meTop, meRight, meBottom,
                                          quad, poly, npoly, opw, flags, BlendFactor, st, n) != 0;
            }
        }

        /// <summary>
        /// Draws <paramref name="Src"/> at (x, y) with the given opacity (0..1) without changing
        /// its alpha channel. Shortcut for <c>DrawFx(Src, x, y, new Effects().Opacity(o))</c>.
        /// </summary>
        public bool DrawTransparent(Sprite Src, int x, int y, float Opacity, SR2D.Op Op = SR2D.Op.DefaultOp)
            => DrawFx(Src, x, y, OpacityFx(Opacity), Op);

        [ThreadStatic] static Effects? opacityFx;
        static Effects OpacityFx(float o)
        {
            var fx = opacityFx ??= new Effects();
            fx.Clear();
            return fx.Opacity(o);
        }

        // ------------------------------------------------------------------ motion blur for real time (multi-tap)
        // Per thread, like VectorRender's scratch: the bench renders parallel bands and both surfaces are
        // written by every call.
        [ThreadStatic] static Sprite? tTrailAcc, tTrailTap;

        /// <summary>
        /// Motion blur for real-time rendering: the sprite drawn <paramref name="Taps"/> times along
        /// <paramref name="Path"/> with the copies AVERAGED instead of piled up - each tap is added in
        /// premultiplied space at 1/<paramref name="Taps"/> into a reused surface that covers the sprite plus
        /// the trail, and that single average is composited here. <see cref="Effects.MotionBlur"/> instead
        /// resamples every pixel of that same sprite-plus-trail image once per tap, so its cost grows with
        /// the trail length; this one stays at Taps passes over the sprite whatever the trail is.
        /// <paramref name="Path"/> is the polyline of relative trail offsets in pixels that
        /// <see cref="Effects.MotionBlurPath"/> takes (the first point is the sprite's own position), sampled
        /// here at Taps places uniformly along the points and rounded to whole pixels.
        /// Deterministic - the same inputs give the same picture, so it works for a still object, for
        /// screenshots and at any frame rate, unlike <see cref="MotionEcho"/>. It is an approximation: a tap
        /// is a WHOLE copy of the sprite, so a long trail with few taps shows separate ghosts rather than a
        /// smooth sweep, and a sprite that changes shape along the trail is averaged, not swept. Taps is
        /// clamped to 2..32; fewer than 2 path points is a plain draw. Both surfaces are reused per thread,
        /// so nothing is allocated once they are big enough for the sprite and the trail.
        /// </summary>
        public void DrawMotionTaps(Sprite Src, int x, int y, ReadOnlySpan<PointF> Path, int Taps = 8)
        {
            if (Src.pBuf == null || Src.meWidth <= 0 || Src.meHeight <= 0) return;
            int n = Math.Clamp(Taps, 2, 32);
            if (Path.Length < 2) { Draw(Src, x, y); return; }
            Span<int> ox = stackalloc int[n], oy = stackalloc int[n];
            int minX = 0, maxX = 0, minY = 0, maxY = 0;
            for (int k = 0; k < n; k++)
            {   // uniform along the points: the path is already a resampled trail, so index order = time order
                float s = (float)k / (n - 1) * (Path.Length - 1);
                int i = Math.Min((int)s, Path.Length - 2);
                float f = s - i;                         // relative to the clamped segment - at s = the last index i is one short, so the fraction is 1 and the FINAL point is reached (a fraction taken off the unclamped floor repeats the last-but-one point and never draws the head of the trail)
                ox[k] = (int)MathF.Round(Path[i].X + (Path[i + 1].X - Path[i].X) * f);
                oy[k] = (int)MathF.Round(Path[i].Y + (Path[i + 1].Y - Path[i].Y) * f);
                minX = Math.Min(minX, ox[k]); maxX = Math.Max(maxX, ox[k]);
                minY = Math.Min(minY, oy[k]); maxY = Math.Max(maxY, oy[k]);
            }
            int aw = Src.meWidth + maxX - minX, ah = Src.meHeight + maxY - minY;
            Sprite? acc = tTrailAcc;
            if (acc == null || acc.Width < aw || acc.Height < ah)
            {   // AlphaOver = the surface is premultiplied by convention, which is what the taps add into
                acc?.Dispose();
                acc = tTrailAcc = new Sprite(Math.Max(aw, acc?.Width ?? 0), Math.Max(ah, acc?.Height ?? 0), SR2D.Op.AlphaOver);
            }
            Sprite? tap = tTrailTap;
            if (tap == null || tap.Width < Src.meWidth || tap.Height < Src.meHeight)
            {
                tap?.Dispose();
                tap = tTrailTap = new Sprite(Math.Max(Src.meWidth, tap?.Width ?? 0), Math.Max(Src.meHeight, tap?.Height ?? 0));
            }
            // One scaled copy (premultiplied, then 1/n) is blitted n times - scaling the sprite once per
            // frame beats an opacity pass on every tap, and the sum of n taps is exactly the trail mean
            // (premultiplied colour and alpha both add, and n x 1/n never saturates).
            tap.SetLockRectXY(0, 0, Src.meWidth, Src.meHeight);
            tap.Premultiplied = false;
            tap.ClearBuffer(0);
            tap.Draw(Src, 0, 0, SR2D.Op.Paint);
            if (Src.Premultiplied) tap.Premultiplied = true; else tap.Premultiply();
            tap.Fade(1f / n);
            acc.SetLockRectXY(0, 0, aw, ah);
            acc.ClearBuffer(0);
            for (int k = 0; k < n; k++) acc.Draw(tap, ox[k] - minX, oy[k] - minY, SR2D.Op.Add);
            Draw(acc, x + minX, y + minY, SR2D.Op.AlphaOver);
        }
    }

    /// <summary>
    /// Real-time friendly motion blur ("echo"): instead of re-sampling a trail every frame, a persistent
    /// accumulator keeps the previous frames - each <see cref="Step"/> fades the accumulator by
    /// <see cref="Persistence"/> and draws the new frame over it. Two whole-surface passes whatever the
    /// trail length, so it stays cheap while <see cref="Effects.MotionBlur"/>'s resampling grows with
    /// the trail. Feed EVERY rendered frame through <see cref="Step"/> and present the returned sprite:
    /// moving objects leave a decaying trail (feedback motion blur, the classic racing-game ghost).
    /// Not frame-rate independent - Persistence applies per step, tie it to your fixed update rate.
    /// The accumulator is a straight-alpha sprite; draw it onto the target with AlphaBlend / AlphaOver.
    /// </summary>
    internal sealed class MotionEcho : IDisposable
    {
        /// <summary>The accumulated frames (what you draw to the screen).</summary>
        public readonly Sprite Accumulator;
        float persistence;
        /// <summary>How much of the accumulator survives each step (0..0.99; 0 = no trail, higher = longer ghost).</summary>
        public float Persistence { get => persistence; set => persistence = Math.Clamp(value, 0f, 0.99f); }

        /// <summary>An empty accumulator of the given size (straight alpha, transparent).</summary>
        public MotionEcho(int width, int height) { Accumulator = new Sprite(width, height); Persistence = 0.9f; }

        /// <summary>Fades the accumulator and blends <paramref name="Frame"/> over it. Returns <see cref="Accumulator"/>.</summary>
        public Sprite Step(Sprite Frame)
        {
            if (persistence > 0) Accumulator.Fade(persistence); else Accumulator.ClearBuffer(0);
            Accumulator.Draw(Frame, 0, 0, SR2D.Op.AlphaOver);
            return Accumulator;
        }

        /// <summary>Throws the trail away (the next Step starts from an empty accumulator).</summary>
        public void Reset() => Accumulator.ClearBuffer(0);

        public void Dispose() { Accumulator.Dispose(); GC.SuppressFinalize(this); }
    }
}
