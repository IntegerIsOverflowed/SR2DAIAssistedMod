// SR2D - public C ABI description (X-macro).
//
// Every exported function is declared exactly once, here. The list is used to:
//   * generate the exported __stdcall wrappers      (sr2d.cpp)
//   * generate the runtime-dispatch function table  (sr2d_ops.h)
//   * fill the table from each SIMD flavour          (sr2d_kernels_*.cpp)
//   * declare reference/new functions in the tests   (tests/)
//
// Signatures and semantics are identical to the original SR2D.dll.
#pragma once
#include <stdint.h>

struct SR2D_Point { int x; int y; };

typedef unsigned char sr2d_byte;
typedef unsigned int  sr2d_uint;

// One stage of a DRAW_FX effect chain (80 bytes, x64 layout; mirrored by SR2D.FxStage in C#).
struct SR2D_FxStage
{
    int32_t        kind;      // SR2D_FX_BLUR / _DISTORT_MAP / _DISTORT / _COLOR
    int32_t        flags;     // kind-specific SR2D_FXF_* bits
    int32_t        i[4];
    float          f[8];
    const int32_t* map;       // SR2D_FX_DISTORT_MAP: height texture (BGRA), tiled
    int32_t        mw, mh;    // its size
    int32_t        pad[2];
};
static_assert(sizeof(SR2D_FxStage) == 80,    "SR2D ABI: SR2D_FxStage drifted (must be 80 bytes; mirror: Effects.Stage in C#)");
static_assert(alignof(SR2D_FxStage) == 8,    "SR2D ABI: SR2D_FxStage alignment drifted (must be 8)");

// One voxel (8 bytes, mirrored by Voxel in C#). A == 0 -> empty cell, anything else -> opaque.
// emit 0..15 = light emission strength (colour = the voxel colour), mat/user = free data.
struct SR2D_Voxel { uint32_t argb; uint8_t emit; uint8_t mat; uint16_t user; };
static_assert(sizeof(SR2D_Voxel) == 8,       "SR2D ABI: SR2D_Voxel drifted (must be 8 bytes; mirror: VoxelGrid.Voxel in C#)");

// VOXEL_RENDER input (232 bytes; v2 was 224, v1 208 - zero the appended fields for the older behaviour;
// mirrored by VoxelGrid.Scene in C#). See sr2d_voxel.inl.
struct SR2D_VoxelScene
{
    const SR2D_Voxel* vox;     // gw*gh*gd voxels, index = (z*gh + y)*gw + x   (x east, y north, z up)
    const uint8_t*    faces;   // VOXEL_FACES output for the same grid (required)
    const uint32_t*   light;   // VOXEL_LIGHT output or NULL (lighting 2/3 fall back to 1); bytes = level * 8 (see VOXEL_LIGHT)
    int32_t gw, gh, gd;
    int32_t lighting;          // 0 none, 1 face shade, 2 + propagated light, 3 + smooth light / AO
    int32_t mode;              // 0 points (1 px per voxel), 1 cubes (faces rasterised)
    int32_t flags;             // SR2D_VOX_* bits
    float   m[6];              // sx = m0 x + m1 y + m2 z + ox, sy = m3 x + m4 y + m5 z + oy (pixels)
    float   ox, oy;
    float   view[3];           // camera look direction in grid space (any length); decides visible faces + paint order
    float   fadeMin;           // depth fade (SR2D_VOX_FADE_*): brightness at the far / bottom end (0..1), 1 = off
    float   shade[6];          // brightness of the +X -X +Y -Y +Z -Z faces (1 = unchanged)
    float   ao[4];             // ambient-occlusion factor for 0..3 occluders (lighting 3)
    float   lut[16];           // light level 0..15 -> brightness (lighting 2/3)
    int32_t skyColor;          // 0xRRGGBB tint of sky light
    uint32_t outside;          // light value assumed outside the grid (sky<<24 | r<<16 | g<<8 | b, bytes in 1/8 levels like light[])
    // ---- appended (v2: 224 bytes; zero them for the v1 behaviour) ----
    int32_t zFrom, zTo;        // slab range [zFrom, zTo) to draw; zTo <= 0 -> whole grid. Painter's order is per
                               // slab, so a caller may draw the grid in several calls (far slabs first: ascending z
                               // when view z < 0, descending when > 0) and report progress in between.
    float   fadeGamma;         // depth fade curve (<= 0 -> 1 = linear)
    int32_t fadeColor;         // 0xRRGGBB fog colour for SR2D_VOX_FADE_COLOR
    // ---- appended (v3: 232 bytes; zero for the v2 behaviour) ----
    const float* lampLut;      // light level 0..15 -> brightness for the LAMP channels (NULL = lut); lets lamps and sky
                               // be scaled separately ("lamp energy" vs "sky energy")
};
static_assert(sizeof(SR2D_VoxelScene) == 232, "SR2D ABI: SR2D_VoxelScene drifted (must be 232 bytes; mirror: VoxelGrid.Scene in C#)");
#define SR2D_VOX_KEEP_ALPHA  1  // write the voxel alpha instead of 255
#define SR2D_VOX_FADE_Z      2  // depth fade along grid z: brightness 1 at the top slab (gd-1) -> fadeMin at z = 0
#define SR2D_VOX_FADE_VIEW   4  // depth fade along the view direction: 1 at the nearest grid corner -> fadeMin at the farthest
                                // (both may be set; the factors multiply. Applied after the lighting tier, before writing.)
#define SR2D_VOX_FADE_COLOR  8  // fade towards fadeColor (fog) instead of towards black

// ABI handshake: SR2D_ABI_VERSION() (exported below the op list) returns SR2D_ABI. Bump it whenever the
// SR2D_OPS list, an op-word encoding or a mirrored struct layout changes - cs/SR2D.cs refuses to run
// against a native library that reports a different number (a stale SR2D64.dll otherwise loads and
// silently draws garbage).
#define SR2D_ABI 1
#define SR2D_OPS(X) \
/* ---- misc ------------------------------------------------------------ */ \
X(void, MOVSD_,        (int* src, int* dst, int dwcnt), (src, dst, dwcnt)) \
X(void, BPP_32TO24,    (sr2d_uint* src, sr2d_byte* dest, int w, int h, int stride), (src, dest, w, h, stride)) \
X(void, CLEAR_C,       (int* dst, int w, int h, int wd, int c), (dst, w, h, wd, c)) \
X(void, MASK_CLEAR_C,  (int* dest, int* mask, int w, int h, int maskex, int wd, int wm, int c, int notm), (dest, mask, w, h, maskex, wd, wm, c, notm)) \
X(void, V_MUL_ADD,     (int* src, int* dst, int w, int h, int ws, int wd, int vmul, int vadd), (src, dst, w, h, ws, wd, vmul, vadd)) \
X(void, MASK_V_MUL_ADD,(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int vmul, int vadd), (src, dst, mask, w, h, maskex, ws, wd, wm, notm, vmul, vadd)) \
X(int,  MASK_INTERSECT,(int* src, int* dst, int w, int h, int ws, int wd, int maskex), (src, dst, w, h, ws, wd, maskex)) \
X(void, CLR_ALPHA,     (int* dest, int size), (dest, size)) \
X(void, DRAW_DOTLINE,  (int* dest, int w, SR2D_Point* p1, SR2D_Point* p2, int dotstep, int col, int isxor), (dest, w, p1, p2, dotstep, col, isxor)) \
/* ---- filters --------------------------------------------------------- */ \
X(void, PAINT,         (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_PAINT,    (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, MOD_,          (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_MOD,      (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, MOD_2X,        (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_MOD_2X,   (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, ADD_,          (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_ADD,      (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, ADD_2D,        (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_ADD_2D,   (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, ALPHA_T,       (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_ALPHA_T,  (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, ALPHA_B,       (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_ALPHA_B,  (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, MAX_,          (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_MAX,      (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, MIN_,          (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_MIN,      (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, MOVE_BYTE,     (int* src, int* dst, int w, int h, int ws, int wd, int movesrc, int movedst), (src, dst, w, h, ws, wd, movesrc, movedst)) \
X(void, MASK_MOVE_BYTE,(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int movesrc, int movedst), (src, dst, mask, w, h, maskex, ws, wd, wm, notm, movesrc, movedst)) \
X(void, MOVE_BIT,      (int* src, int* dst, int w, int h, int ws, int wd, int movesrc, int movedst), (src, dst, w, h, ws, wd, movesrc, movedst)) \
X(void, MASK_MOVE_BIT, (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int movesrc, int movedst), (src, dst, mask, w, h, maskex, ws, wd, wm, notm, movesrc, movedst)) \
X(void, BLEND,         (int* src, int* dst, int w, int h, int ws, int wd, int k), (src, dst, w, h, ws, wd, k)) \
X(void, MASK_BLEND,    (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int k), (src, dst, mask, w, h, maskex, ws, wd, wm, notm, k)) \
/* ---- transforms ------------------------------------------------------ */ \
X(void, ADD_COLOR_KEY, (int* src, int w, int h, int cKey), (src, w, h, cKey)) \
X(void, DRAW_ROT_AA,   (int* src, int* dest, int L, int T, int R, int B, int sx, int sy, int dx, int dy, int sw, int sh, int dw, int SinA, int CosA), (src, dest, L, T, R, B, sx, sy, dx, dy, sw, sh, dw, SinA, CosA)) \
X(void, DRAW_ROT,      (int* src, int* dest, int L, int T, int R, int B, int sx, int sy, int dx, int dy, int sw, int sh, int dw, int SinA, int CosA), (src, dest, L, T, R, B, sx, sy, dx, dy, sw, sh, dw, SinA, CosA)) \
X(void, FLIP_Y_ROT_CCW,(int* src, int* dest, int w, int h), (src, dest, w, h)) \
X(void, FLIP_X_ROT_CCW,(int* src, int* dest, int w, int h), (src, dest, w, h)) \
X(void, ROT_CW,        (int* src, int* dest, int w, int h), (src, dest, w, h)) \
X(void, ROT_CCW,       (int* src, int* dest, int w, int h), (src, dest, w, h)) \
X(void, FLIP_XY,       (int* src, int* dest, int w, int h), (src, dest, w, h)) \
X(void, FLIP_Y,        (int* src, int* dest, int w, int h), (src, dest, w, h)) \
X(void, FLIP_X,        (int* src, int* dest, int w, int h), (src, dest, w, h)) \
X(void, RESIZE,        (sr2d_uint* src, sr2d_uint* dest, sr2d_uint ws, sr2d_uint hs, sr2d_uint wd, sr2d_uint hd), (src, dest, ws, hs, wd, hd)) \
/* ---- bump mapping ---------------------------------------------------- */ \
X(void, MASK_DPBM_POINT,(int* src, int* dst, int* mask, int maskex, int w, int h, int ws, int wd, int wm, int lx, int ly, int lz, int br, int notm), (src, dst, mask, maskex, w, h, ws, wd, wm, lx, ly, lz, br, notm)) \
X(void, DPBM_POINT,    (int* src, int* dst, int w, int h, int ws, int wd, int lx, int ly, int lz, int br), (src, dst, w, h, ws, wd, lx, ly, lz, br)) \
X(void, MASK_EBM,      (sr2d_byte* src, int* dst, int* mask, int* cmap, int maskex, int w, int h, int ws, int wd, int wm, int wc, int hc, int notm), (src, dst, mask, cmap, maskex, w, h, ws, wd, wm, wc, hc, notm)) \
X(void, MASK_EBM_EX,   (sr2d_byte* src, int* dst, int* mask, int* cmap, int maskex, int w, int h, int ws, int wd, int wm, int wc, int hc, int notm, int xx, int yy, int hd), (src, dst, mask, cmap, maskex, w, h, ws, wd, wm, wc, hc, notm, xx, yy, hd)) \
X(void, EBM_,          (int* src, int* dst, int* cmap, int w, int h, int ws, int wd, int wc, int hc), (src, dst, cmap, w, h, ws, wd, wc, hc)) \
X(void, EBM_EX,        (int* src, int* dst, int* cmap, int w, int h, int ws, int wd, int wc, int hc, int xx, int yy, int hd), (src, dst, cmap, w, h, ws, wd, wc, hc, xx, yy, hd)) \
X(void, DPBM_,         (int* src, int* dst, int w, int h, int c, int ws, int wd), (src, dst, w, h, c, ws, wd)) \
X(void, MASK_DPBM,     (int* src, int* dst, int* mask, int w, int h, int c, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, c, maskex, ws, wd, wm, notm)) \
/* ---- premultiplied source-over (new) --------------------------------- */ \
X(void, ALPHA_OVER,    (int* src, int* dst, int w, int h, int ws, int wd), (src, dst, w, h, ws, wd)) \
X(void, MASK_ALPHA_OVER,(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), (src, dst, mask, w, h, maskex, ws, wd, wm, notm)) \
X(void, PREMUL_ALPHA,  (int* src, int n), (src, n)) \
/* ---- blend modes (new) ----------------------------------------------- */ \
X(void, BLEND_MODE,    (int* src, int* dst, int w, int h, int ws, int wd, int op), (src, dst, w, h, ws, wd, op)) \
X(void, MASK_BLEND_MODE,(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int op), (src, dst, mask, w, h, maskex, ws, wd, wm, notm, op)) \
/* ---- line (new) ------------------------------------------------------ */ \
X(void, DRAW_POLY,     (int* dst, int dw, int clipL, int clipT, int clipR, int clipB, const float* xy, const int* counts, int ncont, int col, int op, int k, int flags), (dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, col, op, k, flags)) \
X(void, DRAW_LINE2,    (int* dst, int dw, int clipL, int clipT, int clipR, int clipB, float x0, float y0, float x1, float y1, int col, int op, int k, float dotlen, float gaplen, float phase), (dst, dw, clipL, clipT, clipR, clipB, x0, y0, x1, y1, col, op, k, dotlen, gaplen, phase)) \
/* ---- free transform (new) ------------------------------------------- */ \
X(void, DRAW_WARP,     (int* src, int sw, int sh, int* dst, int dw, int clipL, int clipT, int clipR, int clipB, const float* quad, const float* poly, int npoly, int op, int flags, int k), (src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, op, flags, k)) \
/* ---- blur (new) ------------------------------------------------------ */ \
X(int,  DRAW_BLUR,     (int* src, int sw, int sh, int* dst, int dw, int clipL, int clipT, int clipR, int clipB, int dx, int dy, int r, int op, int k, int flags, int strength), (src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, dx, dy, r, op, k, flags, strength)) \
/* ---- effect chain (new) --------------------------------------------- */ \
X(int,  DRAW_FX,       (int* src, int sw, int sh, int* dst, int dw, int clipL, int clipT, int clipR, int clipB, const float* quad, const float* poly, int npoly, int op, int flags, int k, const SR2D_FxStage* stages, int nstages), (src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, op, flags, k, stages, nstages)) \
/* ---- flood fill / selection masks (new) ------------------------------ */ \
X(int,  FLOOD_MASK,    (const int* src, int sw, int sh, int clipL, int clipT, int clipR, int clipB, int x, int y, int ref, int tol, int flags, uint8_t* mask, int mw, int* bbox), (src, sw, sh, clipL, clipT, clipR, clipB, x, y, ref, tol, flags, mask, mw, bbox)) \
X(int,  FILL_MASK8,    (int* dst, int dw, int clipL, int clipT, int clipR, int clipB, const uint8_t* mask, int mw, int col, int op, int k), (dst, dw, clipL, clipT, clipR, clipB, mask, mw, col, op, k)) \
X(void, LERP_MASK8,    (const int* src, int* dst, const uint8_t* mask, int w, int h, int ws, int wd, int wm, int invert), (src, dst, mask, w, h, ws, wd, wm, invert)) \
/* ---- voxels (new) ---------------------------------------------------- */ \
X(int,  VOXEL_FACES,   (const void* vox, int gw, int gh, int gd, uint8_t* faces), (vox, gw, gh, gd, faces)) \
X(int,  VOXEL_LIGHT,   (const void* vox, const uint8_t* faces, int gw, int gh, int gd, uint32_t* light, int skyLevel, int flags), (vox, faces, gw, gh, gd, light, skyLevel, flags)) \
X(int,  VOXEL_RENDER,  (const SR2D_VoxelScene* scene, int* dst, int dw, int clipL, int clipT, int clipR, int clipB, int* pick), (scene, dst, dw, clipL, clipT, clipR, clipB, pick)) \
X(int,  VOXEL_FLOOD,   (const void* vox, int gw, int gh, int gd, int x, int y, int z, uint32_t ref, int refMat, int tol, int flags, uint8_t* mask), (vox, gw, gh, gd, x, y, z, ref, refMat, tol, flags, mask))

// DRAW_WARP
//   quad  : 4 destination corners (x0,y0 ... x3,y3, float, pixel units) that receive the
//           sprite corners TL, TR, BR, BL. Any convex quad -> projective warp (perspective);
//           parallelograms take the cheaper affine path automatically.
//   poly  : optional clip polygon (npoly points, even-odd rule) or NULL.
//   op    : SR2D.Op value (1 Paint .. 10 Blend; Blend uses k). 0 -> Paint.
//   flags : bit0 = bilinear filtering (default nearest);
//           bit1 = area prefilter for minification: the source is box-averaged by the
//                  integer shrink factor of the transform (per axis, rounded) before
//                  sampling, so every source pixel contributes and 1-px details cannot
//                  drop out when scaling down by more than 2x. No effect when the
//                  factor rounds to 1 (bit-identical to plain nearest/bilinear then).
//                  The box average is alpha-weighted for AlphaTest/AlphaBlend.
//           bit2 = bicubic sampling (Catmull-Rom, 4x4 taps; overrides bit0): smoother
//                  magnification than bilinear, keeps edges crisper. Combine with bit1 for
//                  minification.
//   Alpha edges: with any filter (bilinear / bicubic / area) and op 2 or 3 the sprite is
//   sampled PREMULTIPLIED (internal copy) and composited source-over, so the colour of
//   fully transparent pixels never bleeds into the edge (no dark halo); AlphaTest gets the
//   un-premultiplied colour back. Nearest sampling is untouched (no mixing, nothing to fix).
//   Destination writes are limited to [clipL,clipR) x [clipT,clipB).
// Blend modes (new): op codes 32..58 accepted by every 'op' argument (blits, MASK_ blits,
//   DRAW_WARP, DRAW_POLY, DRAW_LINE2, FILL_MASK8, DRAW_BLUR, DRAW_FX) and by BLEND_MODE /
//   MASK_BLEND_MODE. Formulas are the PDF / Photoshop ones on 8-bit straight colour.
//   The op word carries the compositing context in its upper bits:
//     bits 16..24  opacity 0..256 plus one (0 = not given = 256)
//     SR2D_OP_DST_PREMUL  the destination is premultiplied (an AlphaOver sprite): the result
//                         is the PDF union composite (alpha accumulates, transparent backdrop
//                         shows the source). Without it the destination alpha is untouched
//                         (AlphaBlend-like).
//     SR2D_OP_SRC_PREMUL  the source is premultiplied (un-premultiplied before blending).
//   Effective coverage per pixel = source alpha * opacity; NORMAL is then source-over.
#define SR2D_BM_FIRST         32
#define SR2D_BM_NORMAL        32
#define SR2D_BM_DISSOLVE      33
#define SR2D_BM_DARKEN        34
#define SR2D_BM_MULTIPLY      35
#define SR2D_BM_COLOR_BURN    36
#define SR2D_BM_LINEAR_BURN   37
#define SR2D_BM_DARKER_COLOR  38
#define SR2D_BM_LIGHTEN       39
#define SR2D_BM_SCREEN        40
#define SR2D_BM_COLOR_DODGE   41
#define SR2D_BM_LINEAR_DODGE  42
#define SR2D_BM_LIGHTER_COLOR 43
#define SR2D_BM_OVERLAY       44
#define SR2D_BM_SOFT_LIGHT    45
#define SR2D_BM_HARD_LIGHT    46
#define SR2D_BM_VIVID_LIGHT   47
#define SR2D_BM_LINEAR_LIGHT  48
#define SR2D_BM_PIN_LIGHT     49
#define SR2D_BM_HARD_MIX      50
#define SR2D_BM_DIFFERENCE    51
#define SR2D_BM_EXCLUSION     52
#define SR2D_BM_SUBTRACT      53
#define SR2D_BM_DIVIDE        54
#define SR2D_BM_HUE           55
#define SR2D_BM_SATURATION    56
#define SR2D_BM_COLOR         57
#define SR2D_BM_LUMINOSITY    58
#define SR2D_BM_LAST          58
#define SR2D_OP_DST_PREMUL    0x200
#define SR2D_OP_SRC_PREMUL    0x400
#define SR2D_OP_OPACITY_SHIFT 16

#define SR2D_WARP_BILINEAR 1
#define SR2D_WARP_AREA     2
#define SR2D_WARP_BICUBIC  4

// DRAW_BLUR (new): draws the whole sprite blurred (3 box passes of radius r ~ Gaussian,
//   sigma ~ r) at (dx, dy) with SOFT edges: the blur extends 3*r beyond the sprite into
//   transparent black, premultiplied, so it fades out instead of being cut off.
//   Cost is independent of r (running sums). op = SR2D.Op (default AlphaBlend; 12 = copy
//   the straight-alpha result, 1 Paint copies the premultiplied one), k for Blend;
//   flags: 1 source already premultiplied, 2 treat source as opaque, 4 two box passes
//   instead of three, 16 a single box pass (plain box blur); strength 0..256 scales the result. src may alias dst (in-place
//   blur: the source is consumed before the first write). Returns pixels written.
#define SR2D_BLUR_PREMUL 1
#define SR2D_BLUR_OPAQUE 2
#define SR2D_BLUR_FAST   4
#define SR2D_BLUR_BOX    16
#define SR2D_BLUR_OP_COPY 12   // op code: replace with the straight-alpha blurred result

// DRAW_FX (new): draws a sprite through a chain of effects with ONE call and ONE
//   composite: the sprite is converted to a premultiplied work image (padded by the
//   margin the chain needs: blur radius, displacement amplitude), the stages run in order
//   on that image (cached scratch, no allocation in steady state), and the result is
//   composited exactly like DRAW_WARP (quad, optional clip polygon, op, k, filter flags).
//   flags: bits 0..2 as DRAW_WARP (bilinear / area / bicubic);
//          SR2D_FX_POST   : transform FIRST (into a destination-sized work image), then run
//                           the stages at screen resolution. Default: stages at sprite
//                           resolution, then the transform (cheaper for magnified sprites,
//                           and the effect scales/rotates with the sprite).
//          SR2D_FX_PREMUL : the source is already premultiplied (AlphaOver sprites);
//          SR2D_FX_OPAQUE : ignore the source alpha (treat every pixel as opaque).
//   Compositing is premultiplied: AlphaTest/AlphaBlend/AlphaOver behave as expected
//   (soft blur edges, no halos); ops that ignore alpha (Paint, Add, Max, ...) receive the
//   alpha-weighted colour, as DRAW_BLUR does. op 12 = copy the straight-alpha result.
//   A plain 1:1 blit (axis-aligned quad at integer position) skips the warp entirely.
//   Returns 1 if anything was drawn.
//
//   Stages (SR2D_FxStage):
//     SR2D_FX_BLUR         i[0] radius (0..512), flags SR2D_FXF_BLUR_FAST = 2 box passes,
//                          SR2D_FXF_BLUR_BOX = 1 pass (a plain box blur, the cheapest).
//                          i[1] = downscale factor k (0 / 1 = none, 2 / 4 / 8, -1 = automatic
//                          from the radius: 2 from r 12, 4 from 24, 8 from 48): the picture
//                          is box-averaged by k, blurred with radius r/k
//                          and bilinearly enlarged again - for large radii visually the same
//                          at a fraction of the cost (the blur itself becomes k^2 x cheaper).
//     SR2D_FX_DISTORT_MAP  displacement from a height texture (map, mw, mh; tiled):
//                          height = luminance (SR2D_FXF_MAP_ALPHA: alpha), the displacement
//                          is its gradient, normalised so that f[1] = maximum shift in pixels.
//                          f[0] scale (texture magnification, 1 = one texel per pixel),
//                          f[1] strength (px, sign flips the direction), f[2], f[3] texture
//                          offset in pixels (scroll it to animate).
//     SR2D_FX_DISTORT      procedural: i[0] = SR2D_FX_WAVE / RIPPLE / NOISE / TURBULENCE,
//                          f[0] scale (wavelength / feature size in px), f[1] strength
//                          (max shift in px), f[2] phase (animate: radians for wave and
//                          ripple, "time" for noise: +1 = a new pattern), f[3] wave direction
//                          (radians) or ripple centre x, f[4] ripple centre y, f[5] ripple
//                          falloff radius (0 = none). Wave flags: SR2D_FXF_WAVE_LONG shifts
//                          along the travel direction instead of across it, SR2D_FXF_WAVE_CROSS
//                          adds a second wave at 90 degrees (2-D shimmer).
//                          Both distortions sample bilinearly; SR2D_FXF_SAMPLE_NEAREST /
//                          _BICUBIC override. Coordinates are sprite pixels (or screen pixels
//                          with SR2D_FX_POST), so patterns stay attached to the sprite.
//     SR2D_FX_COLOR        f[0] brightness (-1..1, 0 none), f[1] contrast (1 none), f[2]
//                          saturation (1 none, 0 grey), f[3] gamma (1 none), f[4] hue rotation
//                          (degrees), f[5] opacity (0..1, 1 none), f[6] tint amount (0..1),
//                          i[0] tint colour 0xRRGGBB; flags SR2D_FXF_INVERT. Applied in that
//                          order (gamma first) in a single pass; colour is clamped so the
//                          image stays a valid premultiplied one. Opacity multiplies alpha
//                          at draw time - the sprite itself is never modified.
//     SR2D_FX_MORPH        dilate (grow) by i[0] px: every pixel becomes the per-channel
//                          maximum of a disc of that radius (premultiplied, so the alpha
//                          grows and the colour follows it) - the basis of outlines;
//                          SR2D_FXF_MORPH_ERODE shrinks instead (minimum), _SQUARE uses a
//                          square instead of a disc. Radius 0..256.
//     SR2D_FX_SHADOW       drop shadow / glow in one stage: a copy of the current picture is
//                          grown by i[1] px (0 = none), blurred by i[0] px, coloured with
//                          i[2] = 0xRRGGBB (its alpha shape stays), scaled by f[0] = opacity
//                          (0..1), moved by (f[1], f[2]) px, and put UNDER the picture
//                          (SR2D_FXF_SHADOW_GLOW: ADDED on top - glow; _SHADOW_ONLY: the
//                          picture itself is dropped, e.g. to draw the shadow in a separate
//                          pass). _SHADOW_FAST = 2 box passes, _SHADOW_BOX = 1. i[3] =
//                          blur downscale factor as for SR2D_FX_BLUR. Its margin is
//                          blur support + grow + |offset|, so it is never cut off.
//     SR2D_FX_DIFFUSE      Photoshop-style "diffuse": every pixel is replaced by a randomly
//                          chosen pixel within i[0] px (Chebyshev radius 1..64), i[1] times
//                          over (passes 1..32, each with its own random pattern), i[2] = seed
//                          (change it per frame to animate the grain). flags
//                          SR2D_FXF_DIFFUSE_DARKEN / _LIGHTEN keep the per-channel darker /
//                          lighter of the original and the picked pixel ("Darken Only" /
//                          "Lighten Only" in Photoshop). Margin = radius * passes.
//     SR2D_FX_MOTION       motion blur: the picture is averaged over i[0] samples (2..64, the
//                          first sits ON the pixel) along a trail. Linear trail (i[1] = 0):
//                          f[0] = direction in degrees (0 = +x, 90 = down), f[1] = trail length
//                          in px - the last sample sits that far from the pixel. Custom path
//                          (i[1] = point count 2..32): map = i[1] float pairs (x, y) of relative
//                          trail offsets in px, walked uniformly and scaled by f[1] (strength;
//                          1 = the points as given). Sampling bilinear, SR2D_FXF_SAMPLE_NEAREST
//                          / _BICUBIC override (as the distortions). Margin = the trail extent
//                          + the tap support.
//     Any stage            flags SR2D_FXF_DISABLED = skipped (and adds no margin).
#define SR2D_FX_POST     8
#define SR2D_FX_PREMUL   16
#define SR2D_FX_OPAQUE   32
#define SR2D_FX_BLUR         1
#define SR2D_FX_DISTORT_MAP  2
#define SR2D_FX_DISTORT      3
#define SR2D_FX_COLOR        4
#define SR2D_FX_MORPH        5
#define SR2D_FX_SHADOW       6
#define SR2D_FX_DIFFUSE      7
#define SR2D_FX_MOTION       8
#define SR2D_FX_WAVE        0
#define SR2D_FX_RIPPLE      1
#define SR2D_FX_NOISE       2
#define SR2D_FX_TURBULENCE  3
#define SR2D_FXF_BLUR_FAST      1
#define SR2D_FXF_BLUR_BOX       2       /* SR2D_FX_BLUR: one box pass (overrides _FAST) */
#define SR2D_FXF_DIFFUSE_DARKEN 1       /* SR2D_FX_DIFFUSE: keep min(original, picked) per channel */
#define SR2D_FXF_DIFFUSE_LIGHTEN 2      /* SR2D_FX_DIFFUSE: keep max(original, picked) per channel */
#define SR2D_FXF_MAP_ALPHA      1
#define SR2D_FXF_WAVE_LONG      1
#define SR2D_FXF_WAVE_CROSS     2
#define SR2D_FXF_INVERT         1
#define SR2D_FXF_SAMPLE_NEAREST 0x100
#define SR2D_FXF_SAMPLE_BICUBIC 0x200
#define SR2D_FXF_DISABLED       0x400   /* any kind: skip this stage (chain built once, toggled at run time) */
#define SR2D_FXF_MORPH_ERODE    1       /* SR2D_FX_MORPH: shrink instead of grow */
#define SR2D_FXF_MORPH_SQUARE   2       /* SR2D_FX_MORPH: square structuring element instead of a disc */
#define SR2D_FXF_SHADOW_GLOW    1       /* SR2D_FX_SHADOW: additive glow over the picture instead of a shadow under it */
#define SR2D_FXF_SHADOW_ONLY    2       /* SR2D_FX_SHADOW: keep only the shadow / glow (drop the picture) */
#define SR2D_FXF_SHADOW_FAST    4       /* SR2D_FX_SHADOW: two box passes for the blur */
#define SR2D_FXF_SHADOW_BOX     8       /* SR2D_FX_SHADOW: one box pass for the blur */

// ALPHA_OVER (new): Porter-Duff "source over" for PREMULTIPLIED sources, all four
//   bytes:  d = s + d * (1 - a_s).  Unlike ALPHA_B it accumulates alpha, so layers
//   built on a transparent (all-zero) surface stay transparent where nothing was
//   drawn and get correct colour where something was. Same cost as ALPHA_B.
//   PREMUL_ALPHA converts a straight-alpha image in place (rgb *= a/255).
//   op value 11 in DRAW_WARP.
//
// DRAW_LINE2 (new, replaces the DRAW_DOTLINE quirks; DRAW_DOTLINE itself is unchanged)
//   Bresenham-quality line from (x0,y0) to (x1,y1) in float pixel coordinates, clipped
//   to [clipL,clipR) x [clipT,clipB) inside the kernel (no out-of-bounds writes for any
//   input). Exactly one pixel per major-axis step, both endpoints included.
//   op    : 0/1 = set colour, 2 = XOR, 3 = AlphaBlend (col alpha), 4 = Blend by k (0..256),
//           5 = Add (saturating), 6 = Max, 7 = Min.
//   dotlen/gaplen : dash pattern measured along the LINE (Euclidean pixels), so the
//           spacing is the same for every direction. Dashes are enumerated, not sampled:
//           a dash shorter than one step still lights exactly one pixel.
//           dotlen == 0 -> solid.  dotlen < 0 -> "dot step" mode: one pixel every
//           (-dotlen + 1) major steps, counter anchored at the start point (the
//           DRAW_DOTLINE cadence without its clipping artefacts); gaplen ignored.
//   phase : pattern offset in pixels (animate for "marching ants").
//   op 8  : AlphaOver (source-over with a straight-alpha colour; alpha accumulates).
//   op | 0x100 : do not draw the pixel of the END point (chained segments / XOR).
//
// DRAW_POLY (new): filled polygon(s) with optional anti-aliasing.
//   xy     : ncont contours back to back, counts[i] points each (x,y float pairs),
//            implicitly closed. Non-zero winding by default -> contours of ONE call
//            form a union (thick strokes = segment quads + join discs, rings =
//            outer + reversed inner contour, ...) and every pixel is blended once.
//   op/k   : as DRAW_LINE2 (1 set, 2 xor, 3 alphablend, 4 blend k, 5 add, 6 max,
//            7 min, 8 alpha-over).
//   flags  : 1 = anti-alias (4 sub-scanlines, exact horizontal coverage),
//            2 = even-odd fill rule, 4 = high-quality AA (16 sub-scanlines).
//   Non-AA uses the pixel-centre rule (integer rectangles are exact).

// SIMD levels reported by SR2D_SIMD_LEVEL()
enum { SR2D_SIMD_SSE2 = 1, SR2D_SIMD_AVX2 = 2 };

// FLOOD_MASK (magic wand / flood-fill region finder)
//   Writes an 8-bit coverage mask (0 / 255, or a soft edge with SR2D_FLOOD_SOFT) of the
//   pixels connected to (x, y) whose max channel difference to the seed colour is <= tol
//   into mask (stride mw bytes). Only the clip rect of the mask is written. Returns the
//   number of selected pixels (-1 = out of memory); bbox (may be NULL) gets L,T,R,B.
//   flags: 1 GLOBAL (all matching pixels, no connectivity), 2 IGNORE_ALPHA, 4 DIAGONAL
//   (8-connected), 8 SOFT, 16 REF_COLOR (compare with 'ref' instead of the seed pixel).
// FILL_MASK8: colour through a coverage mask with the DRAW_LINE2 / DRAW_POLY op codes
//   (1 Set, 2 Xor, 3 AlphaBlend, 4 Blend(k), 5 Add, 6 Max, 7 Min, 8 AlphaOver); the mask
//   value scales the op like AA coverage. Returns pixels touched.
// LERP_MASK8: dst = lerp(dst, src, mask / 255) over a w x h rect (commit through a selection).

// VOXEL_FACES: faces[i] = 0x40 for a solid cell | bits 0..5 for its exposed sides (+X -X +Y -Y
//   +Z -Z: the neighbour's alpha is lower than the cell's own - empty or outside counts as 0, so
//   opaque-on-opaque and equal-alpha (the inside of a uniform glass block) stay hidden).
//   Returns the number of exposed voxels.
// VOXEL_LIGHT: Minecraft-style light propagation. light[i] = sky<<24 | r<<16 | g<<8 | b, each
//   byte = level * 8 (fixed point 1/8 levels, 0..120 = level 0..15). Open-sky columns get
//   skyLevel from the top down to the first solid, emitters seed their colour scaled by emit,
//   everything spreads through empty cells losing 'dec' per step: flags bits 8..15 (0 -> 8 =
//   one level per cell, the Minecraft rule: a full lamp reaches 15 cells; 4 -> 30 cells, 2 ->
//   60, 1 -> 120 - the cost grows with the lit volume). flags bit 0: sky enters from the sides.
//   Solid cells keep their own emission. Returns lit cells, -1 = out of memory.
// VOXEL_RENDER: draws the grid into dst with the projection / lighting of 'scene'; opaque voxels
//   write Paint (alpha 255), translucent ones (alpha 1..254) composite over what is behind them
//   (straight-alpha over with their own alpha, alpha channel included - KEEP_ALPHA stamps instead). pick (stride dw, may be NULL) receives the voxel index of
//   every written pixel. Returns voxels drawn. See sr2d_voxel.inl for the algorithm.
// VOXEL_FLOOD: 3-D flood fill region finder (the voxel analogue of FLOOD_MASK). Writes 1 into
//   mask (one byte per cell, cleared first) for every cell connected to the seed (x, y, z) that
//   matches it: empty matches empty; solid matches solid with max channel difference <= tol
//   (or the same material byte with SR2D_VOXF_MATERIAL). flags: 1 GLOBAL (every matching cell,
//   no connectivity), 4 DIAGONAL (26-connected), 16 REF (compare with ref / refMat instead of
//   the seed voxel; the flood still starts at x, y, z), 32 OUTSIDE (seed = every matching cell on
//   the grid boundary; with REF and an empty ref (alpha 0) this selects the "outside air", so
//   !mask & empty = enclosed cavities). x, y, z must lie inside the grid unless REF is combined
//   with GLOBAL or OUTSIDE (then they are ignored); otherwise the call returns 0.
//   Returns the number of cells selected, -1 = out of memory.
#define SR2D_VOXF_GLOBAL    1
#define SR2D_VOXF_DIAGONAL  4
#define SR2D_VOXF_REF      16
#define SR2D_VOXF_OUTSIDE  32
#define SR2D_VOXF_MATERIAL 64
