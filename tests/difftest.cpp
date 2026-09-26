// Differential test: original SR2D sources (REF_*) vs new kernels (SSE2 & AVX2).
// Every exported function is exercised with random geometry/pixels and the
// destination buffers must be bit-identical.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#include <vector>
#include <chrono>

#include "../native/sr2d_ops.h"

typedef unsigned char byte;
typedef unsigned int uint;
typedef SR2D_Point point;

extern "C" {
#define X(ret, name, params, args) ret REF_##name params;
// declare reference versions with the same signatures (SR2D_Point == point layout)
X(void, MOVSD_,        (int* src, int* dst, int dwcnt), )
X(void, BPP_32TO24,    (uint* src, byte* dest, int w, int h, int stride), )
X(void, CLEAR_C,       (int* dst, int w, int h, int wd, int c), )
X(void, MASK_CLEAR_C,  (int* dest, int* mask, int w, int h, int maskex, int wd, int wm, int c, int notm), )
X(void, V_MUL_ADD,     (int* src, int* dst, int w, int h, int ws, int wd, int vmul, int vadd), )
X(void, MASK_V_MUL_ADD,(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int vmul, int vadd), )
X(int,  MASK_INTERSECT,(int* src, int* dst, int w, int h, int ws, int wd, int maskex), )
X(void, CLR_ALPHA,     (int* dest, int size), )
X(void, DRAW_DOTLINE,  (int* dest, int w, point* p1, point* p2, int dotstep, int col, int isxor), )
X(void, PAINT,         (int* src, int* dst, int w, int h, int ws, int wd), )
X(void, MASK_PAINT,    (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), )
X(void, MOD_,          (int* src, int* dst, int w, int h, int ws, int wd), )
X(void, MASK_MOD,      (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), )
X(void, MOD_2X,        (int* src, int* dst, int w, int h, int ws, int wd), )
X(void, MASK_MOD_2X,   (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), )
X(void, ADD_,          (int* src, int* dst, int w, int h, int ws, int wd), )
X(void, MASK_ADD,      (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), )
X(void, ADD_2D,        (int* src, int* dst, int w, int h, int ws, int wd), )
X(void, MASK_ADD_2D,   (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), )
X(void, ALPHA_T,       (int* src, int* dst, int w, int h, int ws, int wd), )
X(void, MASK_ALPHA_T,  (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), )
X(void, ALPHA_B,       (int* src, int* dst, int w, int h, int ws, int wd), )
X(void, MASK_ALPHA_B,  (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), )
X(void, MAX_,          (int* src, int* dst, int w, int h, int ws, int wd), )
X(void, MASK_MAX,      (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), )
X(void, MIN_,          (int* src, int* dst, int w, int h, int ws, int wd), )
X(void, MASK_MIN,      (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm), )
X(void, MOVE_BYTE,     (int* src, int* dst, int w, int h, int ws, int wd, int movesrc, int movedst), )
X(void, MASK_MOVE_BYTE,(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int movesrc, int movedst), )
X(void, MOVE_BIT,      (int* src, int* dst, int w, int h, int ws, int wd, int movesrc, int movedst), )
X(void, MASK_MOVE_BIT, (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int movesrc, int movedst), )
X(void, BLEND,         (int* src, int* dst, int w, int h, int ws, int wd, int k), )
X(void, MASK_BLEND,    (int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int k), )
X(void, ADD_COLOR_KEY, (int* src, int w, int h, int cKey), )
X(void, DRAW_ROT_AA,   (int* src, int* dest, int L, int T, int R, int B, int sx, int sy, int dx, int dy, int sw, int sh, int dw, int SinA, int CosA), )
X(void, DRAW_ROT,      (int* src, int* dest, int L, int T, int R, int B, int sx, int sy, int dx, int dy, int sw, int sh, int dw, int SinA, int CosA), )
X(void, FLIP_Y_ROT_CCW,(int* src, int* dest, int w, int h), )
X(void, FLIP_X_ROT_CCW,(int* src, int* dest, int w, int h), )
X(void, ROT_CW,        (int* src, int* dest, int w, int h), )
X(void, ROT_CCW,       (int* src, int* dest, int w, int h), )
X(void, FLIP_XY,       (int* src, int* dest, int w, int h), )
X(void, FLIP_Y,        (int* src, int* dest, int w, int h), )
X(void, FLIP_X,        (int* src, int* dest, int w, int h), )
X(void, RESIZE,        (uint* src, uint* dest, uint ws, uint hs, uint wd, uint hd), )
X(void, MASK_DPBM_POINT,(int* src, int* dst, int* mask, int maskex, int w, int h, int ws, int wd, int wm, int lx, int ly, int lz, int br, int notm), )
X(void, DPBM_POINT,    (int* src, int* dst, int w, int h, int ws, int wd, int lx, int ly, int lz, int br), )
X(void, MASK_EBM,      (byte* src, int* dst, int* mask, int* cmap, int maskex, int w, int h, int ws, int wd, int wm, int wc, int hc, int notm), )
X(void, MASK_EBM_EX,   (byte* src, int* dst, int* mask, int* cmap, int maskex, int w, int h, int ws, int wd, int wm, int wc, int hc, int notm, int xx, int yy, int hd), )
X(void, EBM_,          (int* src, int* dst, int* cmap, int w, int h, int ws, int wd, int wc, int hc), )
X(void, EBM_EX,        (int* src, int* dst, int* cmap, int w, int h, int ws, int wd, int wc, int hc, int xx, int yy, int hd), )
X(void, DPBM_,         (int* src, int* dst, int w, int h, int c, int ws, int wd), )
X(void, MASK_DPBM,     (int* src, int* dst, int* mask, int w, int h, int c, int maskex, int ws, int wd, int wm, int notm), )
#undef X
}

// ---------------------------------------------------------------- rng / bufs
static uint64_t rs = 0x9E3779B97F4A7C15ull;
static uint32_t rnd() { rs ^= rs << 13; rs ^= rs >> 7; rs ^= rs << 17; return (uint32_t)(rs >> 11); }
static int rr(int lo, int hi) { return lo + (int)(rnd() % (uint32_t)(hi - lo + 1)); }

struct Buf
{
    std::vector<int> a;
    Buf(size_t n, int mode) : a(n)
    {
        for (size_t i = 0; i < n; ++i)
        {
            uint32_t v = rnd();
            switch (mode)
            {
            case 1: v &= 0x00ffffff; if (rnd() & 1) v |= 0xff000000; break;     // alpha 0 or 255
            case 2: v = (rnd() % 3 == 0) ? 0 : v; break;                          // sparse masks
            case 3: v = (rnd() & 7) == 0 ? 0x00123456 : v; break;                 // color-key hits
            default: break;
            }
            a[i] = (int)v;
        }
    }
};

static int g_fail = 0, g_ok = 0;
static const char* g_cur = "";
static void check(const char* what, const void* p, const void* q, size_t bytes)
{
    if (memcmp(p, q, bytes) != 0)
    {
        ++g_fail;
        const byte* a = (const byte*)p; const byte* b = (const byte*)q;
        size_t i = 0; while (i < bytes && a[i] == b[i]) ++i;
        printf("MISMATCH %-16s [%s] at byte %zu of %zu (%02x vs %02x)\n", g_cur, what, i, bytes, a[i], b[i]);
    }
    else ++g_ok;
}

// run: lambda gets (ops or null->reference). dst copies compared afterwards
template<class F>
static void run3(const char* name, const sr2d_ops& s2, const sr2d_ops& a2, F f, std::vector<int>& dst)
{
    g_cur = name;
    std::vector<int> d0 = dst, d1 = dst, d2 = dst;
    f((const sr2d_ops*)0, d0);
    f(&s2, d1);
    f(&a2, d2);
    check("sse2", d0.data(), d1.data(), d0.size() * 4);
    check("avx2", d0.data(), d2.data(), d0.size() * 4);
}

#define R(name) (o ? o->name : REF_##name)

int main(int argc, char** argv)
{
    int iters = argc > 1 ? atoi(argv[1]) : 300;
    sr2d_ops S, A;
    sr2d_fill_ops_sse2(S);
    sr2d_fill_ops_avx2(A);

    for (int it = 0; it < iters; ++it)
    {
        // geometry: dst pitch >= w, src pitch >= w, mask pitch >= w
        const bool big = (it % 10) == 0;               // occasionally use large surfaces
        int w  = rr(1, big ? 300 : 70), h = rr(1, big ? 200 : 40);
        int wd = w + rr(0, 9), ws = w + rr(0, 9), wm = w + rr(0, 9);
        int maskex = 1 << rr(0, 31); if (rnd() & 1) maskex |= (int)rnd();
        int notm = rnd() & 1;
        Buf src(ws * h + 16, it % 4), mask(wm * h + 16, 2), dstb(wd * h + 16, it % 4);
        std::vector<int>& dst = dstb.a;

        // ---- simple rect filters ----
#define RECT(NAME) run3(#NAME, S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(NAME)(src.a.data(), d.data(), w, h, ws, wd); }, dst);
#define MRECT(NAME) run3(#NAME, S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(NAME)(src.a.data(), d.data(), mask.a.data(), w, h, maskex, ws, wd, wm, notm); }, dst);
        RECT(PAINT) RECT(MOD_) RECT(MOD_2X) RECT(ADD_) RECT(ADD_2D) RECT(ALPHA_T) RECT(ALPHA_B) RECT(MAX_) RECT(MIN_)
        MRECT(MASK_PAINT) MRECT(MASK_MOD) MRECT(MASK_MOD_2X) MRECT(MASK_ADD) MRECT(MASK_ADD_2D) MRECT(MASK_ALPHA_T) MRECT(MASK_ALPHA_B) MRECT(MASK_MAX) MRECT(MASK_MIN)
#undef RECT
#undef MRECT
        {
            int k = rr(0, 255) | (rnd() & 1 ? (int)(rnd() & 0xffffff00) : 0);
            run3("BLEND", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(BLEND)(src.a.data(), d.data(), w, h, ws, wd, k); }, dst);
            run3("MASK_BLEND", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MASK_BLEND)(src.a.data(), d.data(), mask.a.data(), w, h, maskex, ws, wd, wm, notm, k); }, dst);
            int ms = rr(0, 7), md = rr(0, 7);   // MOVE_BYTE masks & 3 internally; MASK_ variant does NOT (original quirk) -> keep 0..3 for it
            run3("MOVE_BYTE", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MOVE_BYTE)(src.a.data(), d.data(), w, h, ws, wd, ms, md); }, dst);
            run3("MASK_MOVE_BYTE", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MASK_MOVE_BYTE)(src.a.data(), d.data(), mask.a.data(), w, h, maskex, ws, wd, wm, notm, ms & 3, md & 3); }, dst);
            int bs = (int)rnd(), bd = (int)rnd();
            run3("MOVE_BIT", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MOVE_BIT)(src.a.data(), d.data(), w, h, ws, wd, bs, bd); }, dst);
            run3("MASK_MOVE_BIT", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MASK_MOVE_BIT)(src.a.data(), d.data(), mask.a.data(), w, h, maskex, ws, wd, wm, notm, bs, bd); }, dst);
            int vmul = (int)rnd(), vadd = (int)rnd();
            run3("V_MUL_ADD", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(V_MUL_ADD)(src.a.data(), d.data(), w, h, ws, wd, vmul, vadd); }, dst);
            run3("MASK_V_MUL_ADD", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MASK_V_MUL_ADD)(src.a.data(), d.data(), mask.a.data(), w, h, maskex, ws, wd, wm, notm, vmul, vadd); }, dst);
            int c = (int)rnd();
            run3("CLEAR_C", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(CLEAR_C)(d.data(), w, h, wd, c); }, dst);
            run3("MASK_CLEAR_C", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MASK_CLEAR_C)(d.data(), mask.a.data(), w, h, maskex, wd, wm, c, notm); }, dst);
            run3("DPBM_", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(DPBM_)(src.a.data(), d.data(), w, h, c, ws, wd); }, dst);
            run3("MASK_DPBM", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MASK_DPBM)(src.a.data(), d.data(), mask.a.data(), w, h, c, maskex, ws, wd, wm, notm); }, dst);
            int csz = (int)dst.size() - rr(0, 9);
            run3("CLR_ALPHA", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(CLR_ALPHA)(d.data(), csz); }, dst);
            run3("MOVSD_", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MOVSD_)(src.a.data(), d.data(), w * h); }, dst);
            int ck = 0x00123456 | (int)(rnd() & 0xff000000);
            run3("ADD_COLOR_KEY", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(ADD_COLOR_KEY)(d.data(), w, h, ck); }, dst);

            // MASK_INTERSECT returns a count
            g_cur = "MASK_INTERSECT";
            int r0 = REF_MASK_INTERSECT(src.a.data(), dst.data(), w, h, ws, wd, maskex);
            int r1 = S.MASK_INTERSECT(src.a.data(), dst.data(), w, h, ws, wd, maskex);
            int r2 = A.MASK_INTERSECT(src.a.data(), dst.data(), w, h, ws, wd, maskex);
            check("sse2", &r0, &r1, 4); check("avx2", &r0, &r2, 4);
        }

        // ---- BPP_32TO24 ----
        {
            int stride = w * 3 + rr(0, 7);
            std::vector<byte> b0(stride * h + 16, 0xAA), b1 = b0, b2 = b0;
            std::vector<uint> s32(w * h); for (auto& v : s32) v = rnd();
            REF_BPP_32TO24(s32.data(), b0.data(), w, h, stride);
            S.BPP_32TO24(s32.data(), b1.data(), w, h, stride);
            A.BPP_32TO24(s32.data(), b2.data(), w, h, stride);
            g_cur = "BPP_32TO24";
            check("sse2", b0.data(), b1.data(), b0.size()); check("avx2", b0.data(), b2.data(), b0.size());
        }

        // ---- whole-image transforms (src w*h -> dst w*h) ----
        {
            std::vector<int> simg(w * h); for (auto& v : simg) v = (int)rnd();
            std::vector<int> dimg(w * h, 0);
#define TR(NAME) run3(#NAME, S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(NAME)(simg.data(), d.data(), w, h); }, dimg);
            TR(FLIP_X) TR(FLIP_Y) TR(FLIP_XY) TR(ROT_CW) TR(ROT_CCW) TR(FLIP_X_ROT_CCW) TR(FLIP_Y_ROT_CCW)
#undef TR
        }

        // ---- RESIZE ----
        {
            int sw = rr(1, big ? 400 : 90), sh = rr(1, big ? 300 : 60), dw = rr(1, big ? 400 : 90), dh = rr(1, big ? 300 : 60);
            if (dw > sw && dw == 1) dw = 2;   // original divides by (wd-1) when upscaling
            if (dh > sh && dh == 1) dh = 2;
            std::vector<int> simg(sw * sh); for (auto& v : simg) v = (int)rnd();
            std::vector<int> dimg(dw * dh, 0);
            run3("RESIZE", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(RESIZE)((uint*)simg.data(), (uint*)d.data(), sw, sh, dw, dh); }, dimg);
        }

        // ---- DRAW_ROT / DRAW_ROT_AA (same clipping logic as Sprite.DrawRotate) ----
        {
            int sw = rr(1, big ? 256 : 60), sh = rr(1, big ? 256 : 60);
            int DW = rr(8, big ? 400 : 120), DH = rr(8, big ? 400 : 120);
            std::vector<int> simg(sw * sh); for (auto& v : simg) v = (int)rnd();
            std::vector<int> dimg(DW * DH); for (auto& v : dimg) v = (int)rnd();
            int Sx = rr(-10, sw + 10), Sy = rr(-10, sh + 10), Dx = rr(-20, DW + 20), Dy = rr(-20, DH + 20);
            double ang = (rnd() % 62832) / 10000.0;
            int SinA = (int)(sin(ang) * 0x10000), CosA = (int)(cos(ang) * 0x10000);
            int ddx = (Dx << 16) + 0x8000, ddy = (Dy << 16) + 0x8000;
            int L = ddx - Sx * CosA - Sy * SinA, Rr = L, x;
            x = ddx - (Sx - sw) * CosA - Sy * SinA; if (L > x) L = x; if (Rr < x) Rr = x;
            x = ddx - Sx * CosA - (Sy - sh) * SinA; if (L > x) L = x; if (Rr < x) Rr = x;
            x = ddx - (Sx - sw) * CosA - (Sy - sh) * SinA; if (L > x) L = x; if (Rr < x) Rr = x;
            int T = ddy - Sy * CosA + Sx * SinA, B = T, y;
            y = ddy - (Sy - sh) * CosA + Sx * SinA; if (T > y) T = y; if (B < y) B = y;
            y = ddy - Sy * CosA + (Sx - sw) * SinA; if (T > y) T = y; if (B < y) B = y;
            y = ddy - (Sy - sh) * CosA + (Sx - sw) * SinA; if (T > y) T = y; if (B < y) B = y;
            L >>= 16; if (L < 0) L = 0;
            T >>= 16; if (T < 0) T = 0;
            Rr >>= 16; if (Rr >= DW) Rr = DW; else Rr++;
            B >>= 16; if (B >= DH) B = DH; else B++;
            if (L < Rr && T < B)
            {
                run3("DRAW_ROT", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(DRAW_ROT)(simg.data(), d.data(), L, T, Rr, B, Sx, Sy, Dx, Dy, sw, sh, DW, SinA, CosA); }, dimg);
                run3("DRAW_ROT_AA", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(DRAW_ROT_AA)(simg.data(), d.data(), L, T, Rr, B, Sx, Sy, Dx, Dy, sw, sh, DW, SinA, CosA); }, dimg);
            }
        }

        // ---- DRAW_DOTLINE (clipped endpoints inside the surface) ----
        {
            int W = rr(4, 100), H = rr(4, 100);
            std::vector<int> dimg(W * H); for (auto& v : dimg) v = (int)rnd();
            point p1 = { rr(0, W - 1), rr(0, H - 1) }, p2 = { rr(0, W - 1), rr(0, H - 1) };
            int step = rr(1, 5), col = (int)rnd(), xr = rnd() & 1;
            run3("DRAW_DOTLINE", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ point a = p1, b = p2; R(DRAW_DOTLINE)(d.data(), W, (SR2D_Point*)&a, (SR2D_Point*)&b, step, col, xr); }, dimg);
        }

        // ---- bump mapping ----
        {
            int lx = rr(-50, 120), ly = rr(-50, 80), lz = rr(-300, 300); if (lz == 0) lz = 7;
            int br = (int)(((rnd() % 4001) / 1000.0 - 2.0) * 0x100000);
            run3("DPBM_POINT", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(DPBM_POINT)(src.a.data(), d.data(), w, h, ws, wd, lx, ly, lz, br); }, dst);
            run3("MASK_DPBM_POINT", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MASK_DPBM_POINT)(src.a.data(), d.data(), mask.a.data(), maskex, w, h, ws, wd, wm, lx, ly, lz, br, notm); }, dst);

            int wc = rr(1, 64), hc = rr(1, 64);
            std::vector<int> cmap(wc * hc); for (auto& v : cmap) v = (int)rnd();
            run3("EBM_", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(EBM_)(src.a.data(), d.data(), cmap.data(), w, h, ws, wd, wc, hc); }, dst);
            run3("MASK_EBM", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MASK_EBM)((byte*)src.a.data(), d.data(), mask.a.data(), cmap.data(), maskex, w, h, ws, wd, wm, wc, hc, notm); }, dst);
            int xx = rr(0, 50), yy = rr(0, 50), hd = rr(1, 200);
            run3("EBM_EX", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(EBM_EX)(src.a.data(), d.data(), cmap.data(), w, h, ws, wd, wc, hc, xx, yy, hd); }, dst);
            run3("MASK_EBM_EX", S, A, [&](const sr2d_ops* o, std::vector<int>& d){ R(MASK_EBM_EX)((byte*)src.a.data(), d.data(), mask.a.data(), cmap.data(), maskex, w, h, ws, wd, wm, wc, hc, notm, xx, yy, hd); }, dst);
        }
    }
    printf("%d checks passed, %d failed\n", g_ok, g_fail);
    return g_fail ? 1 : 0;
}
