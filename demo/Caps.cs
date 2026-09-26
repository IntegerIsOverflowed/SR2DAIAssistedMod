using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sr2d64CSport
{
    /// <summary>
    /// Probes the SR2D64.dll that the process actually loads: which exports exist,
    /// where it came from, which SIMD path is active. Lets the UI disable tests that
    /// the loaded build cannot serve (e.g. a legacy DLL without DRAW_WARP).
    /// </summary>
    internal static class Caps
    {
        public static readonly bool Loaded;
        public static readonly string Error = "";
        public static readonly string Path = "";
        public static readonly long FileSize;
        public static readonly DateTime FileTime;
        public static readonly bool HasWarp;        // DRAW_WARP  -> DrawScaled / DrawRotate2 / DrawQuad / DrawInPolygon
        public static readonly bool HasSimdInfo;    // SR2D_SIMD_LEVEL / SR2D_SET_SIMD_LEVEL
        public static readonly bool HasLine2;       // DRAW_LINE2 -> DrawLine2 / DrawPolyline2
        public static readonly bool HasAlphaOver;
        public static readonly bool HasPoly;        // DRAW_POLY -> shapes (Sprite.Shapes.cs)
        public static readonly bool HasBlur;
        public static readonly bool HasFx;          // DRAW_FX -> Effects / DrawFx (Effects.cs)        // DRAW_BLUR -> DrawBlurred / Blur (Sprite.Effects.cs)   // ALPHA_OVER / PREMUL_ALPHA -> Op.AlphaOver, Premultiply()
        public static readonly bool HasFlood;       // FLOOD_MASK / FILL_MASK8 / LERP_MASK8 -> FloodFill, Selection (Selection.cs)
        public static readonly bool HasBlendMode;   // BLEND_MODE / MASK_BLEND_MODE -> editor blend modes (Sprite.Blend.cs, SR2D.BlendMode)
        public static readonly bool HasVoxel;       // VOXEL_FACES / VOXEL_LIGHT / VOXEL_RENDER / VOXEL_FLOOD -> VoxelGrid, DrawVoxels, selections (VoxelGrid*.cs)
        public static readonly List<string> MissingOriginal = new List<string>();

        // the 54 exports of the original SR2D.dll
        static readonly string[] OriginalExports =
        {
            "MOVSD_","ARGB_","EXP2N","CLEAR_C","V_MUL_ADD","MASK_V_MUL_ADD","MASK_INTERSECT","RESIZE","FLIP_X","FLIP_Y","FLIP_XY",
            "ROT_CW","ROT_CCW","FLIP_X_ROT_CCW","FLIP_Y_ROT_CCW","DRAW_ROT","DRAW_ROT_AA","ADD_COLOR_KEY","BPP_32TO24","DRAW_DOTLINE",
            "CLR_ALPHA","MOD_2X","MOD_","ADD_","ADD_2D","PAINT","ALPHA_T","ALPHA_B","MOVE_BYTE","MOVE_BIT","MAX_","MIN_","BLEND",
            "MASK_PAINT","MASK_ADD","MASK_ADD_2D","MASK_MOD","MASK_MOD_2X","MASK_ALPHA_T","MASK_ALPHA_B","MASK_MAX","MASK_MIN",
            "MASK_BLEND","MASK_MOVE_BIT","MASK_MOVE_BYTE","DPBM_","DPBM_POINT","MASK_DPBM","MASK_DPBM_POINT","EBM_","MASK_EBM",
            "EBM_EX","MASK_EBM_EX","MASK_CLEAR_C"
        };

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern unsafe uint GetModuleFileNameW(IntPtr hModule, char* lpFilename, int nSize);

        static Caps()
        {
            try
            {
                if (!NativeLibrary.TryLoad(SR2D.DllName, typeof(Sprite).Assembly, DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.SafeDirectories, out IntPtr h))
                {
                    Error = SR2D.DllName + ".dll not found next to the executable.";
                    return;
                }
                Loaded = true;
                HasWarp = NativeLibrary.TryGetExport(h, "DRAW_WARP", out _);
                HasLine2 = NativeLibrary.TryGetExport(h, "DRAW_LINE2", out _);
                HasAlphaOver = NativeLibrary.TryGetExport(h, "ALPHA_OVER", out _) && NativeLibrary.TryGetExport(h, "PREMUL_ALPHA", out _);
                HasPoly = NativeLibrary.TryGetExport(h, "DRAW_POLY", out _);
                HasBlur = NativeLibrary.TryGetExport(h, "DRAW_BLUR", out _);
                HasFx = NativeLibrary.TryGetExport(h, "DRAW_FX", out _);
                HasFlood = NativeLibrary.TryGetExport(h, "FLOOD_MASK", out _) && NativeLibrary.TryGetExport(h, "FILL_MASK8", out _) && NativeLibrary.TryGetExport(h, "LERP_MASK8", out _);
                HasBlendMode = NativeLibrary.TryGetExport(h, "BLEND_MODE", out _) && NativeLibrary.TryGetExport(h, "MASK_BLEND_MODE", out _);
                HasVoxel = NativeLibrary.TryGetExport(h, "VOXEL_FACES", out _) && NativeLibrary.TryGetExport(h, "VOXEL_LIGHT", out _) && NativeLibrary.TryGetExport(h, "VOXEL_RENDER", out _) && NativeLibrary.TryGetExport(h, "VOXEL_FLOOD", out _);
                HasSimdInfo = NativeLibrary.TryGetExport(h, "SR2D_SIMD_LEVEL", out _) && NativeLibrary.TryGetExport(h, "SR2D_SET_SIMD_LEVEL", out _);
                foreach (string e in OriginalExports)
                    if (!NativeLibrary.TryGetExport(h, e, out _)) MissingOriginal.Add(e);

                var buf = new char[1024]; uint len;
                unsafe { fixed (char* pb = buf) len = GetModuleFileNameW(h, pb, buf.Length); }
                if (len > 0)
                {
                    Path = new string(buf, 0, (int)Math.Min(len, (uint)buf.Length));
                    var fi = new FileInfo(Path);
                    if (fi.Exists) { FileSize = fi.Length; FileTime = fi.LastWriteTime; }
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
        }

        public static string SimdName
        {
            get
            {
                if (!Loaded) return "n/a";
                if (!HasSimdInfo) return "legacy build (scalar)";
                try
                {
                    return SR2D.ActiveSimdLevel switch
                    {
                        SR2D.SimdLevel.Avx2 => "AVX2",
                        SR2D.SimdLevel.Sse2 => "SSE2",
                        _ => "?"
                    };
                }
                catch { return "?"; }
            }
        }

        public static string Summary
        {
            get
            {
                if (!Loaded) return "DLL: " + Error;
                string s = $"DLL: {Path}  ({FileSize / 1024} KB, {FileTime:yyyy-MM-dd HH:mm})   kernels: {SimdName}   new API: {(HasWarp && HasLine2 && HasAlphaOver && HasPoly && HasBlur ? "available" : HasWarp || HasLine2 || HasAlphaOver || HasPoly || HasBlur ? "partial" : "NOT available")}";
                if (MissingOriginal.Count > 0) s += "   MISSING exports: " + string.Join(",", MissingOriginal);
                return s;
            }
        }
    }
}
