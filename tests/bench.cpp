// Benchmark: original scalar code vs new SSE2 / AVX2 kernels.
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <math.h>
#include <vector>
#include <chrono>
#include "../native/sr2d_ops.h"

typedef unsigned char byte; typedef unsigned int uint;
extern "C" {
void REF_PAINT(int*, int*, int, int, int, int);
void REF_MASK_PAINT(int*, int*, int*, int, int, int, int, int, int, int);
void REF_ALPHA_T(int*, int*, int, int, int, int);
void REF_ALPHA_B(int*, int*, int, int, int, int);
void REF_MASK_ALPHA_B(int*, int*, int*, int, int, int, int, int, int, int);
void REF_ADD_(int*, int*, int, int, int, int);
void REF_MOD_(int*, int*, int, int, int, int);
void REF_MOD_2X(int*, int*, int, int, int, int);
void REF_BLEND(int*, int*, int, int, int, int, int);
void REF_V_MUL_ADD(int*, int*, int, int, int, int, int, int);
int  REF_MASK_INTERSECT(int*, int*, int, int, int, int, int);
void REF_CLEAR_C(int*, int, int, int, int);
void REF_DPBM_(int*, int*, int, int, int, int, int);
void REF_DPBM_POINT(int*, int*, int, int, int, int, int, int, int, int);
void REF_EBM_(int*, int*, int*, int, int, int, int, int, int);
void REF_RESIZE(uint*, uint*, uint, uint, uint, uint);
void REF_ROT_CW(int*, int*, int, int);
void REF_FLIP_X(int*, int*, int, int);
void REF_DRAW_ROT(int*, int*, int, int, int, int, int, int, int, int, int, int, int, int, int);
void REF_DRAW_ROT_AA(int*, int*, int, int, int, int, int, int, int, int, int, int, int, int, int);
void REF_BPP_32TO24(uint*, byte*, int, int, int);
}

static double now() { return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count(); }

template<class F> static double timeit(F f, int reps)
{
    f(); // warm
    double best = 1e9;
    for (int k = 0; k < 3; ++k)
    {
        double t0 = now();
        for (int i = 0; i < reps; ++i) f();
        double t = (now() - t0) / reps;
        if (t < best) best = t;
    }
    return best;
}

static void line(const char* name, double r, double s, double a)
{
    printf("%-16s %9.1f us %9.1f us (x%4.1f) %9.1f us (x%4.1f)\n", name, r * 1e6, s * 1e6, r / s, a * 1e6, r / a);
}

int main()
{
    sr2d_ops S, A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
    const int W = 512, H = 512, DW = 1920, DH = 1080;        // 512x512 sprite onto a 1080p surface
    std::vector<int> src(W * H), mask(W * H), dst(DW * DH), cmap(256 * 256), dst2(DW * DH);
    uint32_t x = 12345; auto rnd = [&]{ x ^= x << 13; x ^= x >> 17; x ^= x << 5; return x; };
    for (auto& v : src) v = (int)rnd(); for (auto& v : mask) v = (int)(rnd() & 0x10101); for (auto& v : dst) v = (int)rnd(); for (auto& v : cmap) v = (int)rnd();
    int* s = src.data(); int* m = mask.data(); int* d = dst.data() + 100 * DW + 100; int* c = cmap.data();
    const int reps = 50;
    printf("%-16s %12s %20s %20s\n", "512x512 blit", "original", "SSE2", "AVX2");
    line("PAINT",        timeit([&]{ REF_PAINT(s, d, W, H, W, DW); }, reps),        timeit([&]{ S.PAINT(s, d, W, H, W, DW); }, reps),        timeit([&]{ A.PAINT(s, d, W, H, W, DW); }, reps));
    line("MASK_PAINT",   timeit([&]{ REF_MASK_PAINT(s, d, m, W, H, 1, W, DW, W, 0); }, reps), timeit([&]{ S.MASK_PAINT(s, d, m, W, H, 1, W, DW, W, 0); }, reps), timeit([&]{ A.MASK_PAINT(s, d, m, W, H, 1, W, DW, W, 0); }, reps));
    line("ALPHA_T",      timeit([&]{ REF_ALPHA_T(s, d, W, H, W, DW); }, reps),      timeit([&]{ S.ALPHA_T(s, d, W, H, W, DW); }, reps),      timeit([&]{ A.ALPHA_T(s, d, W, H, W, DW); }, reps));
    line("ALPHA_B",      timeit([&]{ REF_ALPHA_B(s, d, W, H, W, DW); }, reps),      timeit([&]{ S.ALPHA_B(s, d, W, H, W, DW); }, reps),      timeit([&]{ A.ALPHA_B(s, d, W, H, W, DW); }, reps));
    line("MASK_ALPHA_B", timeit([&]{ REF_MASK_ALPHA_B(s, d, m, W, H, 1, W, DW, W, 0); }, reps), timeit([&]{ S.MASK_ALPHA_B(s, d, m, W, H, 1, W, DW, W, 0); }, reps), timeit([&]{ A.MASK_ALPHA_B(s, d, m, W, H, 1, W, DW, W, 0); }, reps));
    line("ADD_",         timeit([&]{ REF_ADD_(s, d, W, H, W, DW); }, reps),         timeit([&]{ S.ADD_(s, d, W, H, W, DW); }, reps),         timeit([&]{ A.ADD_(s, d, W, H, W, DW); }, reps));
    line("MOD_",         timeit([&]{ REF_MOD_(s, d, W, H, W, DW); }, reps),         timeit([&]{ S.MOD_(s, d, W, H, W, DW); }, reps),         timeit([&]{ A.MOD_(s, d, W, H, W, DW); }, reps));
    line("MOD_2X",       timeit([&]{ REF_MOD_2X(s, d, W, H, W, DW); }, reps),       timeit([&]{ S.MOD_2X(s, d, W, H, W, DW); }, reps),       timeit([&]{ A.MOD_2X(s, d, W, H, W, DW); }, reps));
    line("BLEND",        timeit([&]{ REF_BLEND(s, d, W, H, W, DW, 77); }, reps),    timeit([&]{ S.BLEND(s, d, W, H, W, DW, 77); }, reps),    timeit([&]{ A.BLEND(s, d, W, H, W, DW, 77); }, reps));
    line("V_MUL_ADD",    timeit([&]{ REF_V_MUL_ADD(s, d, W, H, W, DW, 0x80808080, 0x70809000); }, reps), timeit([&]{ S.V_MUL_ADD(s, d, W, H, W, DW, 0x80808080, 0x70809000); }, reps), timeit([&]{ A.V_MUL_ADD(s, d, W, H, W, DW, 0x80808080, 0x70809000); }, reps));
    line("MASK_INTERSECT",timeit([&]{ REF_MASK_INTERSECT(s, d, W, H, W, DW, 1); }, reps), timeit([&]{ S.MASK_INTERSECT(s, d, W, H, W, DW, 1); }, reps), timeit([&]{ A.MASK_INTERSECT(s, d, W, H, W, DW, 1); }, reps));
    line("CLEAR_C 1080p",timeit([&]{ REF_CLEAR_C(dst.data(), DW, DH, DW, 5); }, reps), timeit([&]{ S.CLEAR_C(dst.data(), DW, DH, DW, 5); }, reps), timeit([&]{ A.CLEAR_C(dst.data(), DW, DH, DW, 5); }, reps));
    line("DPBM_",        timeit([&]{ REF_DPBM_(s, d, W, H, 0x8090a0, W, DW); }, reps), timeit([&]{ S.DPBM_(s, d, W, H, 0x8090a0, W, DW); }, reps), timeit([&]{ A.DPBM_(s, d, W, H, 0x8090a0, W, DW); }, reps));
    line("DPBM_POINT",   timeit([&]{ REF_DPBM_POINT(s, d, W, H, W, DW, 200, 200, 100, 0x100000); }, reps), timeit([&]{ S.DPBM_POINT(s, d, W, H, W, DW, 200, 200, 100, 0x100000); }, reps), timeit([&]{ A.DPBM_POINT(s, d, W, H, W, DW, 200, 200, 100, 0x100000); }, reps));
    line("EBM_",         timeit([&]{ REF_EBM_(s, d, c, W, H, W, DW, 256, 256); }, reps), timeit([&]{ S.EBM_(s, d, c, W, H, W, DW, 256, 256); }, reps), timeit([&]{ A.EBM_(s, d, c, W, H, W, DW, 256, 256); }, reps));
    line("ROT_CW",       timeit([&]{ REF_ROT_CW(s, dst2.data(), W, H); }, reps),  timeit([&]{ S.ROT_CW(s, dst2.data(), W, H); }, reps),  timeit([&]{ A.ROT_CW(s, dst2.data(), W, H); }, reps));
    line("FLIP_X",       timeit([&]{ REF_FLIP_X(s, dst2.data(), W, H); }, reps),  timeit([&]{ S.FLIP_X(s, dst2.data(), W, H); }, reps),  timeit([&]{ A.FLIP_X(s, dst2.data(), W, H); }, reps));
    line("RESIZE 512>300",timeit([&]{ REF_RESIZE((uint*)s, (uint*)dst2.data(), W, H, 300, 300); }, 10), timeit([&]{ S.RESIZE((uint*)s, (uint*)dst2.data(), W, H, 300, 300); }, 10), timeit([&]{ A.RESIZE((uint*)s, (uint*)dst2.data(), W, H, 300, 300); }, 10));
    line("RESIZE 512>1000",timeit([&]{ REF_RESIZE((uint*)s, (uint*)dst2.data(), W, H, 1000, 1000); }, 10), timeit([&]{ S.RESIZE((uint*)s, (uint*)dst2.data(), W, H, 1000, 1000); }, 10), timeit([&]{ A.RESIZE((uint*)s, (uint*)dst2.data(), W, H, 1000, 1000); }, 10));
    {
        double ang = 0.7; int SinA = (int)(sin(ang) * 65536), CosA = (int)(cos(ang) * 65536);
        int L = 100, T = 100, R = 900, B = 900;
        line("DRAW_ROT",   timeit([&]{ REF_DRAW_ROT(s, dst.data(), L, T, R, B, 256, 256, 500, 500, W, H, DW, SinA, CosA); }, reps), timeit([&]{ S.DRAW_ROT(s, dst.data(), L, T, R, B, 256, 256, 500, 500, W, H, DW, SinA, CosA); }, reps), timeit([&]{ A.DRAW_ROT(s, dst.data(), L, T, R, B, 256, 256, 500, 500, W, H, DW, SinA, CosA); }, reps));
        line("DRAW_ROT_AA",timeit([&]{ REF_DRAW_ROT_AA(s, dst.data(), L, T, R, B, 256, 256, 500, 500, W, H, DW, SinA, CosA); }, reps), timeit([&]{ S.DRAW_ROT_AA(s, dst.data(), L, T, R, B, 256, 256, 500, 500, W, H, DW, SinA, CosA); }, reps), timeit([&]{ A.DRAW_ROT_AA(s, dst.data(), L, T, R, B, 256, 256, 500, 500, W, H, DW, SinA, CosA); }, reps));
    }
    {
        std::vector<byte> b24(DW * 3 * DH);
        line("BPP_32TO24 1080p", timeit([&]{ REF_BPP_32TO24((uint*)dst.data(), b24.data(), DW, DH, DW * 3); }, reps), timeit([&]{ S.BPP_32TO24((uint*)dst.data(), b24.data(), DW, DH, DW * 3); }, reps), timeit([&]{ A.BPP_32TO24((uint*)dst.data(), b24.data(), DW, DH, DW * 3); }, reps));
    }
    return 0;
}
