using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.ApplicationDirectory | System.Runtime.InteropServices.DllImportSearchPath.System32)]
    // the resolver's fallback (nothing found) probes default paths - keep it to the application directory
    // and System32, never the current directory or PATH (a planted DLL there would run with the app's
    // rights). System32 MUST stay in the set: gdi32 / user32 / kernel32 are KnownDLLs and load regardless,
    // but winmm is NOT - with ApplicationDirectory alone the demo died on its timeBeginPeriod P/Invoke
    // (DllNotFoundException winmm) the moment it left System32 out.

namespace Sr2d64CSport
{
    /// <summary>
    /// Constants, enums and the raw P/Invoke surface of SR2D64.dll.
    /// All entry points use blittable arguments only (no marshalling stubs,
    /// no SetLastError, no security checks) so a call is a plain native call.
    /// </summary>
    public static class SR2D
    {
        public const string DllName = "SR2D64";

        // ------------------------------------------------------------------ locating SR2D64.dll
        // Modern WinForms designers load a managed assembly in DesignToolsServer / a shadow-copy folder.
        // Assembly.Location and AppContext.BaseDirectory can therefore BOTH be outside the app's bin folder.
        // The shared SR2D.Native.targets records the actual build output plus the selected None/Content DLL
        // asset. Probe those trusted assembly hints after the running assembly/app directories; never CWD/PATH.
        // The project/native repository layout is not a runtime dependency. SR2D.DllPath remains an explicit override.
        static string? dllPath, nativeLoadError; static bool resolverSet; static bool? available;
        /// <summary>Full path of the DLL to load (set before the first native call), or the path the resolver picked after it (null = default probing).</summary>
        public static string? DllPath { get => dllPath; set { dllPath = value; available = null; nativeLoadError = null; EnsureResolver(); } }
        /// <summary>Reason the last availability probe failed (missing DLL/dependency, bitness, ABI or export); null on success.</summary>
        public static string? NativeLoadError => nativeLoadError;
        /// <summary>True when the ABI-compatible engine loads. Missing/bad native libraries return false so designers
        /// can paint placeholders instead of disabling a control; NativeLoadError provides the diagnostic.</summary>
        public static bool IsAvailable
        {
            get
            {
                if (available.HasValue) return available.Value;
                bool ok;
                try { EnsureResolver(); _ = Native.SR2D_SIMD_LEVEL(); ok = true; }
                catch (DllNotFoundException ex) { ok = false; nativeLoadError = ex.Message; }
                catch (BadImageFormatException ex) { ok = false; nativeLoadError = ex.Message; }   // wrong bitness
                catch (EntryPointNotFoundException ex) { ok = false; nativeLoadError = ex.Message; }
                catch (PlatformNotSupportedException ex) { ok = false; nativeLoadError = ex.Message; } // wrong ABI: designer still stays editable
                if (ok) nativeLoadError = null;
                available = ok;
                return ok;
            }
        }
        /// <summary>The ABI version the managed layer was built for (see SR2D_ABI in native/sr2d_api.h). Bump both together.</summary>
        internal const int AbiVersion = 1;
        /// <summary>The mirrored struct sizes the native side compiles static_asserts for (sr2d_api.h). A drift means
        /// the native reads garbage pointers - refuse to run instead. Looked up by name: the headless runners
        /// compile subsets of cs/, so a mirror type may legitimately be absent from a given build.</summary>
        internal static void CheckAbiLayouts()
        {
            Check("Sr2d64CSport.Effects.Stage", 80);        // native SR2D_FxStage
            Check("Sr2d64CSport.Voxel", 8);           // native SR2D_Voxel
            Check("Sr2d64CSport.VoxelGrid+Scene", 232);         // native SR2D_VoxelScene
            static void Check(string typeName, int bytes)
            {
                var t = Type.GetType(typeName, throwOnError: false);
                if (t == null) return;                      // not part of this build
                if (System.Runtime.InteropServices.Marshal.SizeOf(t) != bytes)
                    throw new TypeLoadException($"SR2D ABI: {typeName} must be {bytes} bytes - the C# mirror and the native struct (see sr2d_api.h static_asserts) drifted apart.");
            }
        }
        /// <summary>Loads a candidate library and demands the ABI handshake export; a library that does not match is freed and refused.</summary>
        static bool TryLoadAbiChecked(string path, ref string? fail, out IntPtr h)
        {
            // Loading by a full path must also allow dependencies BESIDE that DLL (the designer's exe folder
            // is unrelated). Keep search confined to that file's directory, the application and System32.
            bool loaded = OperatingSystem.IsWindows()
                ? NativeLibrary.TryLoad(System.IO.Path.GetFullPath(path), typeof(SR2D).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32, out h)
                : NativeLibrary.TryLoad(path, out h);
            if (!loaded) return false;
            int v = 0; bool has = true;
            try { v = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<AbiVersionFn>(NativeLibrary.GetExport(h, "SR2D_ABI_VERSION"))(); }
            catch (EntryPointNotFoundException) { has = false; }
            if (has && v == AbiVersion) return true;
            fail = $"The SR2D64 native library at '{path}' does not match the managed code: " +
                   (has ? $"it reports ABI {v}, the managed layer expects {AbiVersion}." : "it has no SR2D_ABI_VERSION export (too old, or not the SR2D64 engine at all).") +
                   " Rebuild the native DLL from native/ so both come from the same source state.";
            NativeLibrary.Free(h);
            return false;
        }
        delegate int AbiVersionFn();                                       // the marshaller needs a named (non-generic) delegate type
        internal static void EnsureResolver()
        {
            if (resolverSet) return;
            CheckAbiLayouts();
            resolverSet = true;
            try { NativeLibrary.SetDllImportResolver(typeof(SR2D).Assembly, Resolve); }
            catch (InvalidOperationException) { /* a resolver is already registered for this assembly (host app did it): keep it */ }
        }
        static IntPtr Resolve(string name, System.Reflection.Assembly asm, DllImportSearchPath? path)
        {
            if (name != DllName) return IntPtr.Zero;
            string? fail = null;
            if (dllPath != null && TryLoadAbiChecked(dllPath, ref fail, out var h0)) return h0;
            string? env = Environment.GetEnvironmentVariable("SR2D_DLL");
            if (!string.IsNullOrEmpty(env) && TrustworthyPath(env) && TryLoadAbiChecked(env, ref fail, out var h1)) { dllPath = env; return h1; }
            foreach (string f in CandidateFiles(asm))
                if (System.IO.File.Exists(f) && TryLoadAbiChecked(f, ref fail, out var h)) { dllPath = f; return h; }
            if (fail != null) throw new PlatformNotSupportedException(fail);   // found, but not ours: a clear error instead of garbage pixels
            return IntPtr.Zero;                                       // nothing found: fall back to the default probing
        }
        static readonly string[] FileNames = OperatingSystem.IsWindows() ? new[] { DllName + ".dll" }
                                           : new[] { "lib" + DllName + ".so", DllName + ".so", "lib" + DllName + ".dylib", DllName + ".dll" }; // headless runners / ELF fixture named like the Windows asset
        static System.Collections.Generic.IEnumerable<string> CandidateFiles(System.Reflection.Assembly asm)
        {
            var seen = new System.Collections.Generic.HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (string dir in CandidateDirs(asm))
                foreach (string name in FileNames)
                {
                    string file = System.IO.Path.Combine(dir, name);
                    if (seen.Add(file)) yield return file;
                }
            // DllPath is a FILE, not just a directory hint: linked/custom-named DLLs and explicit source files
            // must work too. On Linux this also permits a test ELF library named SR2D64.dll in a portable fixture.
            foreach (var attr in asm.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                if (attr is System.Reflection.AssemblyMetadataAttribute md && md.Key == "SR2D.DllPath"
                    && !string.IsNullOrEmpty(md.Value) && System.IO.Path.IsPathRooted(md.Value) && seen.Add(md.Value))
                    yield return md.Value;
        }
        static System.Collections.Generic.IEnumerable<string> CandidateDirs(System.Reflection.Assembly asm)
        {
            var seen = new System.Collections.Generic.HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            string? loc = null; try { loc = asm.Location; } catch { }
            if (!string.IsNullOrEmpty(loc)) { string? d = System.IO.Path.GetDirectoryName(loc); if (d != null && seen.Add(d)) yield return d; }
            string? b = AppContext.BaseDirectory; if (!string.IsNullOrEmpty(b) && seen.Add(b.TrimEnd('\\', '/'))) yield return b;
            foreach (var attr in asm.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                if (attr is not System.Reflection.AssemblyMetadataAttribute md || string.IsNullOrEmpty(md.Value)) continue;
                string? d = md.Key switch
                {
                    "SR2D.OutputDir" or "SR2D.ProjectDir" => md.Value,
                    "SR2D.DllPath" => System.IO.Path.GetDirectoryName(md.Value),
                    _ => null
                };
                if (d != null && System.IO.Path.IsPathRooted(d) && seen.Add(d)) yield return d;
            }
            // Walk ONLY a known bin/obj tree, stopping at its project folder (no arbitrary writable ancestors).
            foreach (string? start in new[] { loc == null ? null : System.IO.Path.GetDirectoryName(loc), b })
            {
                string? d = start;
                bool underBuild = d != null && UnderBuildOutput(d);
                while (underBuild && d != null)
                {
                    d = System.IO.Path.GetDirectoryName(d.TrimEnd('\\', '/'));
                    if (d == null) break;
                    if (seen.Add(d)) yield return d;
                    underBuild = UnderBuildOutput(d);
                }
            }
        }
        /// <summary>True while <paramref name="path"/> still sits inside a bin / obj output tree (the walk may continue above it).</summary>
        static bool UnderBuildOutput(string path)
        {
            foreach (var piece in PathSegments(path)) if (piece is "bin" or "obj") return true;
            return false;
        }
        /// <summary>A path the environment variable may point at: absolute, no .. segments, an existing file. Anything else is ignored - anything that can set an env var must not redirect the engine.</summary>
        static bool TrustworthyPath(string path)
        {
            if (!System.IO.Path.IsPathRooted(path) || !System.IO.File.Exists(path)) return false;
            foreach (var piece in PathSegments(System.IO.Path.GetFullPath(path))) if (piece == "..") return false;
            return true;
        }
        static System.Collections.Generic.IEnumerable<string> PathSegments(string path)
        {
            foreach (var piece in path.Split('/', '\\')) if (piece.Length > 0) yield return piece;
        }

        public enum Op : int
        {
            DefaultOp,
            Paint,
            AlphaTest,
            AlphaBlend,
            Add2D,
            Add,
            Mul,
            Mul2X,
            Max,
            Min,
            Blend,
            /// <summary>
            /// Porter-Duff source-over for PREMULTIPLIED sprites (new DLL). Unlike
            /// <see cref="AlphaBlend"/> it also accumulates the destination alpha, so
            /// compositing onto a transparent (zeroed) surface gives correct colours and
            /// a correct coverage alpha. Call <see cref="Sprite.Premultiply"/> once on a
            /// straight-alpha image (PNG) before using it with this op.
            /// </summary>
            AlphaOver = 11,

            // ---- editor blend modes (new DLL). Same names and codes as SR2D.BlendMode; every
            // draw call that takes an Op accepts them (blits, masked blits, DrawScaled / DrawRotate2 /
            // DrawQuad, DrawFx*, DrawBlurred, layers). The mode acts on the colour; the source alpha
            // (times an optional opacity, see SR2D.WithOpacity) is the coverage, i.e. "Normal" is a
            // plain alpha blend. Premultiplied sprites (Op AlphaOver / Sprite.Premultiplied) are
            // handled automatically on both sides.
            /// <summary>Source over (alpha blend) - the same coverage rule every other mode uses.</summary>
            Normal = 32,
            /// <summary>Random pixels of the source replace the destination, density = coverage.</summary>
            Dissolve = 33,
            Darken = 34,
            /// <summary>Photoshop Multiply: like <see cref="Mul"/> but weighted by the source alpha.</summary>
            Multiply = 35,
            ColorBurn = 36,
            LinearBurn = 37,
            DarkerColor = 38,
            Lighten = 39,
            Screen = 40,
            ColorDodge = 41,
            /// <summary>Linear dodge = add, weighted by the source alpha (see also <see cref="Add"/>).</summary>
            LinearDodge = 42,
            LighterColor = 43,
            Overlay = 44,
            SoftLight = 45,
            HardLight = 46,
            VividLight = 47,
            LinearLight = 48,
            PinLight = 49,
            HardMix = 50,
            Difference = 51,
            Exclusion = 52,
            Subtract = 53,
            Divide = 54,
            Hue = 55,
            Saturation = 56,
            Color = 57,
            Luminosity = 58
        }

        /// <summary>
        /// The editor blend modes (Photoshop / GIMP / PDF names and formulas). Use one where an
        /// <see cref="Op"/> or <see cref="LineOp"/> is expected through <see cref="ToOp"/> /
        /// <see cref="ToLineOp"/> (or the equally named members of those enums), or with
        /// <see cref="Sprite.DrawBlend"/>. The mode is applied to the colour only; the source alpha
        /// times the opacity is the coverage, so every mode is automatically "mixed with alpha blend"
        /// like a layer in an editor: <c>out = dst + (Mode(src, dst) - dst) * coverage</c>. On a
        /// premultiplied destination (Op AlphaOver) the alpha accumulates like AlphaOver does and the
        /// destination is un-premultiplied for the formula, so blending onto transparent layers works.
        /// The non-separable modes (Hue .. Luminosity) use the PDF definitions with lum = .3 .59 .11.
        /// </summary>
        public enum BlendMode : int
        {
            Normal = 32, Dissolve, Darken, Multiply, ColorBurn, LinearBurn, DarkerColor,
            Lighten, Screen, ColorDodge, LinearDodge, LighterColor,
            Overlay, SoftLight, HardLight, VividLight, LinearLight, PinLight, HardMix,
            Difference, Exclusion, Subtract, Divide,
            Hue, Saturation, Color, Luminosity
        }

        /// <summary>First / last op code that is a blend mode; codes are shared by Op, LineOp and BlendMode.</summary>
        public const int BlendModeFirst = 32, BlendModeLast = 58;
        /// <summary>Bits of an op word beyond the code (internal encoding, see sr2d_api.h): destination / source premultiplied, opacity in bits 16..24.</summary>
        public const int OpDstPremul = 0x200, OpSrcPremul = 0x400, OpOpacityShift = 16;

        /// <summary>True when <paramref name="op"/> carries a blend mode (in any of the three enums, with or without opacity bits).</summary>
        public static bool IsBlendMode(Op op) => IsBlendMode((int)op);
        public static bool IsBlendMode(LineOp op) => IsBlendMode((int)op);
        public static bool IsBlendMode(int op) { int c = op & 0xff; return c >= BlendModeFirst && c <= BlendModeLast; }
        /// <summary>The blend mode of an op word (Normal for anything that is not a blend mode).</summary>
        public static BlendMode ModeOf(Op op) => IsBlendMode(op) ? (BlendMode)((int)op & 0xff) : BlendMode.Normal;

        /// <summary><paramref name="Mode"/> as an <see cref="Op"/>, optionally with an opacity 0..1 (multiplies the source alpha).</summary>
        public static Op ToOp(this BlendMode Mode, float Opacity = 1f) => (Op)Encode((int)Mode, Opacity);
        /// <summary><paramref name="Mode"/> as a <see cref="LineOp"/> for the shape / text / selection calls, optionally with an opacity 0..1.</summary>
        public static LineOp ToLineOp(this BlendMode Mode, float Opacity = 1f) => (LineOp)Encode((int)Mode, Opacity);
        /// <summary>A blend-mode op with an opacity 0..1 (ignored for the classic ops, which have no opacity).</summary>
        public static Op WithOpacity(Op op, float Opacity) => IsBlendMode(op) ? (Op)Encode((int)op, Opacity) : op;
        public static LineOp WithOpacity(LineOp op, float Opacity) => IsBlendMode(op) ? (LineOp)Encode((int)op, Opacity) : op;
        /// <summary>The opacity 0..1 encoded in a blend-mode op (1 when none).</summary>
        public static float OpacityOf(Op op) { int o = ((int)op >> OpOpacityShift) & 0x1ff; return o == 0 ? 1f : (o - 1) / 256f; }

        internal static int Encode(int op, float Opacity)
        {
            if (Opacity >= 1f && (((op >> OpOpacityShift) & 0x1ff) == 0)) return op;
            int o = Opacity >= 1f ? 256 : Opacity <= 0f || float.IsNaN(Opacity) ? 0 : (int)(Opacity * 256f + 0.5f);
            return (op & ~(0x1ff << OpOpacityShift)) | ((o + 1) << OpOpacityShift);
        }
        /// <summary>Adds the premultiplied-source / destination bits to a blend-mode op word (no-op for classic ops).</summary>
        internal static int Encode(int op, bool srcPremul, bool dstPremul)
            => IsBlendMode(op) ? op | (srcPremul ? OpSrcPremul : 0) | (dstPremul ? OpDstPremul : 0) : op;

        public enum ColChannel : int
        {
            ChBlue = 0,
            ChGreen = 1,
            ChRed = 2,
            ChAlpha = 3
        }

#pragma warning disable CA1069 // FlipXRotCW == FlipYRotCCW and FlipYRotCW == FlipXRotCCW by geometry: both names are kept from the original API
        public enum Transform : int
        {
            None = 0,
            FlipX = 1,
            FlipY = 2,
            FlipXY = 3,
            RotCCW = 4,
            RotCW = 5,
            FlipXRotCCW = 6,
            FlipYRotCCW = 7,
            FlipXRotCW = 7,
            FlipYRotCW = 6
        }
#pragma warning restore CA1069

        /// <summary>Sampling used by the free-transform methods.</summary>
        /// <summary>
        /// Sampling filter for the warp methods (DrawScaled / DrawRotate2 / DrawQuad ...).
        /// With any filter other than Nearest, AlphaBlend / AlphaTest sample the sprite
        /// premultiplied internally, so the colour of fully transparent pixels never bleeds
        /// into edges - no need to "clean" transparent RGB in your PNGs.
        /// </summary>
        public enum Filter : int
        {
            /// <summary>Nearest neighbour (fastest, pixel-art look).</summary>
            Nearest = 0,
            /// <summary>Bilinear interpolation (smooth, ~3x the cost of Nearest).</summary>
            Bilinear = 1,
            /// <summary>
            /// Area (box) prefilter for DOWNSCALING: the source is averaged by the integer shrink
            /// factor of the transform before nearest sampling, so every source pixel contributes
            /// and 1-px details cannot drop out when shrinking by more than 2x (plain
            /// Nearest/Bilinear only look at 1 / 4 source pixels per destination pixel). No-op
            /// (identical to Nearest) when the shrink factor rounds to 1. Needs the new DLL.
            /// </summary>
            Area = 2,
            /// <summary>Area prefilter + bilinear sampling of the prefiltered image: the best
            /// quality for downscaling (smooth at any non-integer factor). Identical to Bilinear
            /// when not shrinking by 2x or more.</summary>
            BilinearArea = 3,
            /// <summary>
            /// Bicubic (Catmull-Rom, 4x4 taps) for UPSCALING and rotation: sharper than Bilinear
            /// (no "tent" blur, no diamond artefacts on diagonals) with a slight, natural
            /// overshoot at hard edges. ~2x the cost of Bilinear. Needs the new DLL.
            /// </summary>
            Bicubic = 4,
            /// <summary>Bicubic + area prefilter: good at every scale factor, up or down. The
            /// sensible "I don't want to think about it" choice when quality matters.</summary>
            BicubicArea = 6,
            /// <summary>
            /// Pick the best filter for the transform at hand, from the destination quad: an
            /// unrotated 1:1 blit is done with Nearest (exact copy, cheapest); an enlargement or
            /// a rotation / perspective with Bicubic (sharp, no tent blur); a reduction by 2x or
            /// more with BicubicArea (area prefilter, so no detail drops out); anything in between
            /// with Bilinear. Only meaningful for the transform of a draw call; as the
            /// <c>Sampling</c> of a distortion stage it means Bilinear. Resolved in C# before the
            /// native call (see <see cref="SR2D.Resolve"/>).
            /// </summary>
            Auto = -1
        }

        /// <summary>
        /// Resolves <see cref="Filter.Auto"/> for a destination quad (TL, TR, BR, BL) of a
        /// <paramref name="sw"/> x <paramref name="sh"/> source. Other filters are returned as is.
        /// The measure is the length of the quad's edges in destination pixels per source pixel
        /// (the same the DLL's area prefilter uses): both axes in [0.999, 1.001] and the quad
        /// axis-aligned -> Nearest; the smaller axis scale &lt;= 0.5 -> BicubicArea; the larger
        /// axis scale &gt;= 1.001 or a rotation -> Bicubic; otherwise Bilinear.
        /// </summary>
        public static unsafe Filter Resolve(Filter f, float* quad, int sw, int sh)
        {
            if (f != Filter.Auto) return f;
            if (sw <= 0 || sh <= 0) return Filter.Bilinear;
            float ux = quad[2] - quad[0], uy = quad[3] - quad[1];          // TL -> TR
            float vx = quad[6] - quad[0], vy = quad[7] - quad[1];          // TL -> BL
            float wx = quad[4] - quad[6], wy = quad[5] - quad[7];          // BL -> BR (perspective: differs from u)
            float su = MathF.Sqrt(ux * ux + uy * uy) / sw, sv = MathF.Sqrt(vx * vx + vy * vy) / sh, sw2 = MathF.Sqrt(wx * wx + wy * wy) / sw;
            if (!(su > 0) || !(sv > 0) || !(sw2 > 0)) return Filter.Bilinear;   // degenerate / NaN
            float smin = MathF.Min(MathF.Min(su, sv), sw2), smax = MathF.Max(MathF.Max(su, sv), sw2);
            bool axisAligned = MathF.Abs(uy) < 1e-3f && MathF.Abs(vx) < 1e-3f && MathF.Abs(wy) < 1e-3f;
            if (axisAligned && smin > 0.999f && smax < 1.001f) return Filter.Nearest;
            if (smin <= 0.5f) return Filter.BicubicArea;
            if (!axisAligned || smax >= 1.001f) return Filter.Bicubic;
            return Filter.Bilinear;
        }
        /// <summary>Resolves <see cref="Filter.Auto"/> for a <paramref name="sw"/> x <paramref name="sh"/> source drawn <paramref name="dw"/> x <paramref name="dh"/> destination pixels large, rotated by <paramref name="Angle"/> radians (0 = axis-aligned).</summary>
        public static unsafe Filter Resolve(Filter f, int sw, int sh, float dw, float dh, float Angle = 0)
        {
            if (f != Filter.Auto) return f;
            float c = MathF.Cos(Angle), s = MathF.Sin(Angle);
            float* q = stackalloc float[8] { 0, 0, dw * c, dw * s, dw * c - dh * s, dw * s + dh * c, -dh * s, dh * c };
            return Resolve(f, q, sw, sh);
        }
        /// <summary>Resolves <see cref="Filter.Auto"/> for a destination quad given as 4 points (TL, TR, BR, BL).</summary>
        public static unsafe Filter Resolve(Filter f, System.ReadOnlySpan<System.Drawing.PointF> Quad, int sw, int sh)
        {
            if (f != Filter.Auto) return f;
            if (Quad.Length != 4) return Filter.Bilinear;
            float* q = stackalloc float[8];
            for (int i = 0; i < 4; i++) { q[i * 2] = Quad[i].X; q[i * 2 + 1] = Quad[i].Y; }
            return Resolve(f, q, sw, sh);
        }

        /// <summary>Pixel operation for <see cref="Sprite.DrawLine2"/> and the shape methods (Sprite.Shapes.cs).</summary>
        public enum LineOp : int
        {
            /// <summary>Overwrite with the colour.</summary>
            Set = 1,
            /// <summary>XOR the colour in (draw twice to erase).</summary>
            Xor = 2,
            /// <summary>Alpha-blend using the colour's alpha byte.</summary>
            AlphaBlend = 3,
            /// <summary>Crossfade by the BlendFactor argument (0..256).</summary>
            Blend = 4,
            /// <summary>Saturating add.</summary>
            Add = 5,
            Max = 6,
            Min = 7,
            /// <summary>Source-over with the colour's alpha: like AlphaBlend but the destination alpha
            /// accumulates, so it is right for drawing onto transparent layers.</summary>
            AlphaOver = 8,

            // ---- editor blend modes (new DLL), same codes as SR2D.Op / SR2D.BlendMode; the colour's
            // alpha byte (times an optional opacity, SR2D.WithOpacity) is the coverage
            Normal = 32, Dissolve = 33, Darken = 34, Multiply = 35, ColorBurn = 36, LinearBurn = 37, DarkerColor = 38,
            Lighten = 39, Screen = 40, ColorDodge = 41, LinearDodge = 42, LighterColor = 43,
            Overlay = 44, SoftLight = 45, HardLight = 46, VividLight = 47, LinearLight = 48, PinLight = 49, HardMix = 50,
            Difference = 51, Exclusion = 52, Subtract = 53, Divide = 54,
            Hue = 55, Saturation = 56, Color = 57, Luminosity = 58
        }

        /// <summary>Which corners <see cref="Sprite.DrawBracket"/> marks.</summary>
        [Flags]
        public enum Corners : int { TopLeft = 1, TopRight = 2, BottomLeft = 4, BottomRight = 8, All = 15 }

        /// <summary>SIMD level the native library selected: 1 = SSE2, 2 = AVX2.</summary>
        public enum SimdLevel : int { Sse2 = 1, Avx2 = 2 }

        // Pure arithmetic helpers: doing these in managed code is far cheaper
        // than a P/Invoke transition. The native exports still exist for ABI
        // compatibility with other callers.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ARGB(byte A, byte R, byte G, byte B) => (A << 24) | (R << 16) | (G << 8) | B;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BitMask(int i) => 1 << i;

        public static SimdLevel ActiveSimdLevel => (SimdLevel)Native.SR2D_SIMD_LEVEL();

        /// <summary>Force a SIMD level (0 = auto detect). Mainly for benchmarking.</summary>
        public static SimdLevel SetSimdLevel(SimdLevel level) => (SimdLevel)Native.SR2D_SET_SIMD_LEVEL((int)level);

        /// <summary>Raw native surface. Pointers are passed as IntPtr (blittable).</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006")]
        internal static unsafe class Native
        {
            static Native() => EnsureResolver();      // runs before the first P/Invoke of this class
            [DllImport(DllName, ExactSpelling = true)] public static extern int SR2D_SIMD_LEVEL();
            [DllImport(DllName, ExactSpelling = true)] public static extern int SR2D_SET_SIMD_LEVEL(int level);

            [DllImport(DllName, EntryPoint = "MOVSD_", ExactSpelling = true)] public static extern void MovsD(int* pSrc, int* pDest, int Length);
            [DllImport(DllName, EntryPoint = "CLEAR_C", ExactSpelling = true)] public static extern void ClearC(int* pDest, int W, int H, int WD, int c);
            [DllImport(DllName, EntryPoint = "V_MUL_ADD", ExactSpelling = true)] public static extern void MulAdd(int* pSrc, int* pDest, int W, int H, int WS, int WD, int VMul, int VAdd);
            [DllImport(DllName, EntryPoint = "MASK_V_MUL_ADD", ExactSpelling = true)] public static extern void MaskMulAdd(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask, int VMul, int VAdd);
            [DllImport(DllName, EntryPoint = "MASK_CLEAR_C", ExactSpelling = true)] public static extern void MaskClearC(int* pDest, int* pMask, int W, int H, int MaskEx, int WD, int WM, int c, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_INTERSECT", ExactSpelling = true)] public static extern int MaskIS(int* pSrc, int* pDest, int W, int H, int WS, int WD, int MaskEx);
            [DllImport(DllName, EntryPoint = "RESIZE", ExactSpelling = true)] public static extern void Resize(int* pSrc, int* pDest, int WS, int HS, int WD, int HD);
            [DllImport(DllName, EntryPoint = "FLIP_X", ExactSpelling = true)] public static extern void FlipX(int* pSrc, int* pDest, int W, int H);
            [DllImport(DllName, EntryPoint = "FLIP_Y", ExactSpelling = true)] public static extern void FlipY(int* pSrc, int* pDest, int W, int H);
            [DllImport(DllName, EntryPoint = "FLIP_XY", ExactSpelling = true)] public static extern void FlipXY(int* pSrc, int* pDest, int W, int H);
            [DllImport(DllName, EntryPoint = "ROT_CW", ExactSpelling = true)] public static extern void RotCW(int* pSrc, int* pDest, int WS, int HS);
            [DllImport(DllName, EntryPoint = "ROT_CCW", ExactSpelling = true)] public static extern void RotCCW(int* pSrc, int* pDest, int WS, int HS);
            [DllImport(DllName, EntryPoint = "FLIP_X_ROT_CCW", ExactSpelling = true)] public static extern void FlipXRotCCW(int* pSrc, int* pDest, int WS, int HS);
            [DllImport(DllName, EntryPoint = "FLIP_Y_ROT_CCW", ExactSpelling = true)] public static extern void FlipYRotCCW(int* pSrc, int* pDest, int WS, int HS);
            [DllImport(DllName, EntryPoint = "DRAW_ROT", ExactSpelling = true)] public static extern void DrawRot(int* pSrc, int* pDest, int L, int T, int R, int B, int Sx, int Sy, int Dx, int Dy, int sw, int sh, int dw, int SinA, int CosA);
            [DllImport(DllName, EntryPoint = "DRAW_ROT_AA", ExactSpelling = true)] public static extern void DrawRotAA(int* pSrc, int* pDest, int L, int T, int R, int B, int Sx, int Sy, int Dx, int Dy, int sw, int sh, int dw, int SinA, int CosA);
            [DllImport(DllName, EntryPoint = "ADD_COLOR_KEY", ExactSpelling = true)] public static extern void AddColorKey(int* pSrc, int W, int H, int cKey);
            [DllImport(DllName, EntryPoint = "BPP_32TO24", ExactSpelling = true)] public static extern void Bpp32to24(int* pSrc, byte* pDest, int Width, int Height, int Stride);
            [DllImport(DllName, EntryPoint = "DRAW_DOTLINE", ExactSpelling = true)] public static extern void DrawDotLine(int* pDest, int W, Sprite.Point* p1, Sprite.Point* p2, int DotStep, int c, int IsXor);
            [DllImport(DllName, EntryPoint = "CLR_ALPHA", ExactSpelling = true)] public static extern void ClearA(int* pDest, int Size);

            [DllImport(DllName, EntryPoint = "PAINT", ExactSpelling = true)] public static extern void Paint(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "MOD_2X", ExactSpelling = true)] public static extern void Mul2X(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "ADD_2D", ExactSpelling = true)] public static extern void Add2D(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "ADD_", ExactSpelling = true)] public static extern void Add(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "MOD_", ExactSpelling = true)] public static extern void Mul(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "ALPHA_T", ExactSpelling = true)] public static extern void AlphaTest(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "ALPHA_B", ExactSpelling = true)] public static extern void AlphaBlend(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "MOVE_BYTE", ExactSpelling = true)] public static extern void MoveByte(int* pSrc, int* pDest, int W, int H, int WS, int WD, int MoveSrc, int MoveDest);
            [DllImport(DllName, EntryPoint = "MOVE_BIT", ExactSpelling = true)] public static extern void MoveBit(int* pSrc, int* pDest, int W, int H, int WS, int WD, int MoveSrc, int MoveDest);
            [DllImport(DllName, EntryPoint = "MAX_", ExactSpelling = true)] public static extern void Max(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "MIN_", ExactSpelling = true)] public static extern void Min(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "BLEND", ExactSpelling = true)] public static extern void Blend(int* pSrc, int* pDest, int W, int H, int WS, int WD, int k);

            [DllImport(DllName, EntryPoint = "MASK_PAINT", ExactSpelling = true)] public static extern void MaskPaint(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_MOD_2X", ExactSpelling = true)] public static extern void MaskMul2X(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_ADD_2D", ExactSpelling = true)] public static extern void MaskAdd2D(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_ADD", ExactSpelling = true)] public static extern void MaskAdd(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_MOD", ExactSpelling = true)] public static extern void MaskMul(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_ALPHA_T", ExactSpelling = true)] public static extern void MaskAlphaTest(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_ALPHA_B", ExactSpelling = true)] public static extern void MaskAlphaBlend(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_MOVE_BYTE", ExactSpelling = true)] public static extern void MaskMoveByte(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask, int MoveSrc, int MoveDest);
            [DllImport(DllName, EntryPoint = "MASK_MOVE_BIT", ExactSpelling = true)] public static extern void MaskMoveBit(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask, int MoveSrc, int MoveDest);
            [DllImport(DllName, EntryPoint = "MASK_MAX", ExactSpelling = true)] public static extern void MaskMax(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_MIN", ExactSpelling = true)] public static extern void MaskMin(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_BLEND", ExactSpelling = true)] public static extern void MaskBlend(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask, int k);

            [DllImport(DllName, EntryPoint = "EBM_", ExactSpelling = true)] public static extern void DrawEBM(int* pBMap, int* pDest, int* pLMap, int W, int H, int WB, int WD, int WL, int HL);
            [DllImport(DllName, EntryPoint = "EBM_EX", ExactSpelling = true)] public static extern void DrawEBMEx(int* pBMap, int* pDest, int* pLMap, int W, int H, int WB, int WD, int WL, int HL, int xx, int yy, int HD);
            [DllImport(DllName, EntryPoint = "DPBM_", ExactSpelling = true)] public static extern void DrawDPBM(int* pBMap, int* pDest, int W, int H, int c, int WS, int WD);
            [DllImport(DllName, EntryPoint = "DPBM_POINT", ExactSpelling = true)] public static extern void DrawDPBMPoint(int* pSrc, int* pDest, int W, int H, int WS, int WD, int Lx, int Ly, int Lz, int Br);
            [DllImport(DllName, EntryPoint = "MASK_DPBM_POINT", ExactSpelling = true)] public static extern void MaskDrawDPBMPoint(int* pSrc, int* pDest, int* pMask, int MaskEx, int W, int H, int WS, int WD, int WM, int Lx, int Ly, int Lz, int Br, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_DPBM", ExactSpelling = true)] public static extern void MaskDrawDPBM(int* pSrc, int* pDest, int* pMask, int W, int H, int c, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "MASK_EBM", ExactSpelling = true)] public static extern void MaskDrawEBM(int* pSrc, int* pDest, int* pMask, int* pLMap, int MaskEx, int W, int H, int WS, int WD, int WM, int WL, int HL, int NotMask);
            [DllImport(DllName, EntryPoint = "ALPHA_OVER", ExactSpelling = true)] public static extern void AlphaOver(int* pSrc, int* pDest, int W, int H, int WS, int WD);
            [DllImport(DllName, EntryPoint = "MASK_ALPHA_OVER", ExactSpelling = true)] public static extern void MaskAlphaOver(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);
            [DllImport(DllName, EntryPoint = "PREMUL_ALPHA", ExactSpelling = true)] public static extern void PremulAlpha(int* p, int n);
            [DllImport(DllName, EntryPoint = "BLEND_MODE", ExactSpelling = true)] public static extern void BlendMode(int* pSrc, int* pDest, int W, int H, int WS, int WD, int op);
            [DllImport(DllName, EntryPoint = "MASK_BLEND_MODE", ExactSpelling = true)] public static extern void MaskBlendMode(int* pSrc, int* pDest, int* pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask, int op);
            [DllImport(DllName, EntryPoint = "DRAW_POLY", ExactSpelling = true)] public static extern void DrawPoly(int* pDest, int dw, int clipL, int clipT, int clipR, int clipB, float* xy, int* counts, int ncont, int col, int op, int k, int flags);
            [DllImport(DllName, EntryPoint = "DRAW_LINE2", ExactSpelling = true)] public static extern void DrawLine2(int* pDest, int dw, int clipL, int clipT, int clipR, int clipB, float x0, float y0, float x1, float y1, int col, int op, int k, float dotLen, float gapLen, float phase);
            [DllImport(DllName, EntryPoint = "DRAW_BLUR", ExactSpelling = true)] public static extern int DrawBlur(int* pSrc, int sw, int sh, int* pDest, int dw, int clipL, int clipT, int clipR, int clipB, int dx, int dy, int r, int op, int k, int flags, int strength);
            [DllImport(DllName, EntryPoint = "DRAW_WARP", ExactSpelling = true)] public static extern void DrawWarp(int* pSrc, int sw, int sh, int* pDest, int dw, int clipL, int clipT, int clipR, int clipB, float* quad, float* poly, int npoly, int op, int flags, int k);
            [DllImport(DllName, EntryPoint = "DRAW_FX", ExactSpelling = true)] public static extern int DrawFx(int* pSrc, int sw, int sh, int* pDest, int dw, int clipL, int clipT, int clipR, int clipB, float* quad, float* poly, int npoly, int op, int flags, int k, Effects.Stage* stages, int nstages);
            [DllImport(DllName, EntryPoint = "FLOOD_MASK", ExactSpelling = true)] public static extern int FloodMask(int* pSrc, int sw, int sh, int clipL, int clipT, int clipR, int clipB, int x, int y, int refColor, int tol, int flags, byte* mask, int mw, int* bbox);
            [DllImport(DllName, EntryPoint = "FILL_MASK8", ExactSpelling = true)] public static extern int FillMask8(int* pDest, int dw, int clipL, int clipT, int clipR, int clipB, byte* mask, int mw, int col, int op, int k);
            [DllImport(DllName, EntryPoint = "LERP_MASK8", ExactSpelling = true)] public static extern void LerpMask8(int* pSrc, int* pDest, byte* mask, int w, int h, int ws, int wd, int wm, int invert);
            [DllImport(DllName, EntryPoint = "VOXEL_FACES", ExactSpelling = true)] public static extern int VoxelFaces(void* vox, int gw, int gh, int gd, byte* faces);
            [DllImport(DllName, EntryPoint = "VOXEL_LIGHT", ExactSpelling = true)] public static extern int VoxelLight(void* vox, byte* faces, int gw, int gh, int gd, uint* light, int skyLevel, int flags);
            [DllImport(DllName, EntryPoint = "VOXEL_RENDER", ExactSpelling = true)] public static extern int VoxelRender(void* scene, int* pDest, int dw, int clipL, int clipT, int clipR, int clipB, int* pick);
            [DllImport(DllName, EntryPoint = "VOXEL_FLOOD", ExactSpelling = true)] public static extern int VoxelFlood(void* vox, int gw, int gh, int gd, int x, int y, int z, uint refColor, int refMat, int tol, int flags, byte* mask);

            [DllImport(DllName, EntryPoint = "MASK_EBM_EX", ExactSpelling = true)] public static extern void MaskDrawEBMEx(int* pSrc, int* pDest, int* pMask, int* pLMap, int MaskEx, int W, int H, int WS, int WD, int WM, int WL, int HL, int NotMask, int xx, int yy, int HD);
        }
    }
}
