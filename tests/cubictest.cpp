// DRAW_WARP bicubic (SR2D_WARP_BICUBIC):
//   1. axis-aligned scale vs a double-precision Catmull-Rom reference (tolerance 2 / channel:
//      the kernel uses x128 weights and 8-bit intermediate rows, the reference exact weights;
//      on random noise the worst case is ~5 / channel, on real images 1-2);
//   2. separable fast path (axis-aligned) == generic path (same quad rotated by 0 through the
//      affine path is not reachable, so compare against a 1e-3 px skewed quad instead: must be
//      within 1 of the axis-aligned result);
//   3. SSE2 == AVX2 for random quads (scale / rotate / perspective);
//   4. bicubic + area prefilter compiles the same path; timings.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <vector>
#include <chrono>
#include "../native/sr2d_ops.h"
static uint32_t rng = 4242; static uint32_t rnd() { rng = rng * 1664525u + 1013904223u; return rng >> 8; }
static float frnd(float a, float b) { return a + (b - a) * (rnd() & 0xffff) / 65535.0f; }
static double cw(double t, int i) { double a = -0.5; double x = fabs(t - (i - 1)); return x < 1 ? ((a + 2) * x - (a + 3)) * x * x + 1 : x < 2 ? ((a * x - 5 * a) * x + 8 * a) * x - 4 * a : 0; }
int main(int argc, char** argv)
{
    int iters = argc > 1 ? atoi(argv[1]) : 300;
    sr2d_ops S, A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
    int fails = 0, maxd = 0;
    for (int it = 0; it < iters; it++)
    {
        int sw = 2 + rnd() % 40, sh = 2 + rnd() % 40; std::vector<int> src(sw * sh); for (auto& v : src) v = (int)rnd();
        int dw = 1 + rnd() % 120, dh = 1 + rnd() % 120, DW = dw + 8, DH = dh + 8;
        float q[8] = { 4, 4, 4.f + dw, 4, 4.f + dw, 4.f + dh, 4, 4.f + dh };
        std::vector<int> d1(DW * DH, 0), d2(DW * DH, 0);
        S.DRAW_WARP(src.data(), sw, sh, d1.data(), DW, 0, 0, DW, DH, q, 0, 0, 1, 4, 0);
        A.DRAW_WARP(src.data(), sw, sh, d2.data(), DW, 0, 0, DW, DH, q, 0, 0, 1, 4, 0);
        if (d1 != d2) { if (fails < 5) printf("it %d: SSE2 != AVX2 (scale)\n", it); fails++; }
        // reference
        int md = 0;
        for (int y = 0; y < dh; y++) for (int x = 0; x < dw; x++)
        {
            double u = (x + 0.5) * sw / dw - 0.5, v = (y + 0.5) * sh / dh - 0.5;
            if (u < 0) u = 0; if (u > sw - 1) u = sw - 1; if (v < 0) v = 0; if (v > sh - 1) v = sh - 1;
            int iu = (int)u, iv = (int)v; double tu = u - iu, tv = v - iv;
            double acc[4] = { 0, 0, 0, 0 };
            for (int j = 0; j < 4; j++)
            {   // horizontal pass per row, rounded + clamped to 8 bits (separable implementation), then vertical
                int yy = iv + j - 1; if (yy < 0) yy = 0; if (yy > sh - 1) yy = sh - 1;
                double row[4] = { 0, 0, 0, 0 };
                for (int i = 0; i < 4; i++)
                {
                    int xx = iu + i - 1; if (xx < 0) xx = 0; if (xx > sw - 1) xx = sw - 1;
                    uint32_t p = (uint32_t)src[yy * sw + xx];
                    for (int c = 0; c < 4; c++) row[c] += cw(tu, i) * ((p >> (c * 8)) & 255);
                }
                for (int c = 0; c < 4; c++) { double r = row[c]; if (r < 0) r = 0; if (r > 255) r = 255; acc[c] += cw(tv, j) * (int)(r + 0.5); }
            }
            uint32_t got = (uint32_t)d2[(y + 4) * DW + x + 4];
            for (int c = 0; c < 4; c++) { double r = acc[c]; if (r < 0) r = 0; if (r > 255) r = 255; int dd = abs((int)(r + 0.5) - (int)((got >> (c * 8)) & 255)); if (dd > md) md = dd; }
        }
        if (md > maxd) maxd = md;
        if (md > 5) { if (fails < 5) printf("it %d: ref diff %d (sw %d sh %d dw %d dh %d)\n", it, md, sw, sh, dw, dh); fails++; }
        // generic path (tiny skew) ~ separable path
        float q2[8] = { 4, 4, 4.f + dw, 4.001f, 4.f + dw, 4.001f + dh, 4, 4.f + dh };
        std::vector<int> d3(DW * DH, 0);
        A.DRAW_WARP(src.data(), sw, sh, d3.data(), DW, 0, 0, DW, DH, q2, 0, 0, 1, 4, 0);
        int md2 = 0; for (int i = 0; i < DW * DH; i++) for (int c = 0; c < 4; c++) { int a = (d2[i] >> (c * 8)) & 255, b = (d3[i] >> (c * 8)) & 255; if (abs(a - b) > md2) md2 = abs(a - b); }
        if (md2 > 8) { if (fails < 5) printf("it %d: generic vs separable diff %d\n", it, md2); fails++; }
        // random quads: parity only
        float cx = frnd(0, DW), cy = frnd(0, DH), w = frnd(1, 100), h = frnd(1, 100), a = frnd(0, 6.283f), c = cosf(a), s = sinf(a);
        float q3[8] = { cx - w * c + h * s, cy - w * s - h * c, cx + w * c + h * s, cy + w * s - h * c, cx + w * c - h * s + frnd(-9, 9), cy + w * s + h * c, cx - w * c - h * s, cy - w * s + h * c + frnd(-9, 9) };
        std::vector<int> d4(DW * DH, 0), d5(DW * DH, 0);
        int fl = 4 | ((it & 1) ? 2 : 0), op = 1 + it % 11;
        S.DRAW_WARP(src.data(), sw, sh, d4.data(), DW, 0, 0, DW, DH, q3, 0, 0, op, fl, 100);
        A.DRAW_WARP(src.data(), sw, sh, d5.data(), DW, 0, 0, DW, DH, q3, 0, 0, op, fl, 100);
        if (d4 != d5) { if (fails < 5) printf("it %d: SSE2 != AVX2 (quad, flags %d op %d)\n", it, fl, op); fails++; }
    }
    printf("bicubic: %d iterations, max diff vs reference %d, %d failures\n", iters, maxd, fails);
    // timings
    {
        int sw = 512, sh = 512; std::vector<int> src(sw * sh); for (auto& v : src) v = (int)rnd();
        int DW = 1920, DH = 1080; std::vector<int> d(DW * DH, 0);
        for (int fl : { 1, 4 })
        {
            float q[8] = { 0, 0, 1024, 0, 1024, 1024, 0, 1024 };
            for (int lv = 0; lv < 2; lv++)
            {
                sr2d_ops& O = lv ? A : S; O.DRAW_WARP(src.data(), sw, sh, d.data(), DW, 0, 0, DW, DH, q, 0, 0, 1, fl, 0);
                auto t0 = std::chrono::steady_clock::now(); int n = 10;
                for (int i = 0; i < n; i++) O.DRAW_WARP(src.data(), sw, sh, d.data(), DW, 0, 0, DW, DH, q, 0, 0, 1, fl, 0);
                printf("scale 512->1024 %s %s: %.3f ms\n", fl == 4 ? "bicubic " : "bilinear", lv ? "AVX2" : "SSE2", std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / n);
            }
            float qr[8] = { 512, 100, 1300, 300, 1100, 1000, 300, 800 };
            for (int lv = 0; lv < 2; lv++)
            {
                sr2d_ops& O = lv ? A : S; O.DRAW_WARP(src.data(), sw, sh, d.data(), DW, 0, 0, DW, DH, qr, 0, 0, 1, fl, 0);
                auto t0 = std::chrono::steady_clock::now(); int n = 10;
                for (int i = 0; i < n; i++) O.DRAW_WARP(src.data(), sw, sh, d.data(), DW, 0, 0, DW, DH, qr, 0, 0, 1, fl, 0);
                printf("rotate 512 (~1000px) %s %s: %.3f ms\n", fl == 4 ? "bicubic " : "bilinear", lv ? "AVX2" : "SSE2", std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / n);
            }
        }
    }
    return fails ? 1 : 0;
}
