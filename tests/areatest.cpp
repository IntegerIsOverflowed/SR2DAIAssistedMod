// DRAW_WARP area prefilter (SR2D_WARP_AREA):
//   1. thin-line survival: a 2048x2048 image with 1-px lines every 64 px scaled to 256x256 must
//      keep every line visible (nearest/bilinear lose them, area keeps them all);
//   2. flag is a no-op when the shrink factor rounds to 1 (bit-identical output);
//   3. SSE2 == AVX2, and the box average matches a scalar reference for a pure integer downscale;
//   4. timings.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>
#include <chrono>
#include "../native/sr2d_ops.h"
static uint32_t rng = 777; static uint32_t rnd() { rng = rng * 1664525u + 1013904223u; return rng >> 8; }
int main()
{
    sr2d_ops S, A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
    int fails = 0;
    // 1. thin lines
    {
        int sw = 2048, sh = 2048, dw = 256, dh = 256; std::vector<int> src((size_t)sw * sh, (int)0xff000000);
        for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) if (x % 64 == 13 || y % 64 == 29) src[(size_t)y * sw + x] = (int)0xffffffff;
        float q[8] = { 0, 0, (float)dw, 0, (float)dw, (float)dh, 0, (float)dh };
        for (int fl = 0; fl < 4; fl++)
        {
            std::vector<int> d((size_t)dw * dh, 0);
            A.DRAW_WARP(src.data(), sw, sh, d.data(), dw, 0, 0, dw, dh, q, 0, 0, 1, fl, 0);
            // count visible vertical lines: columns whose mean brightness clearly exceeds the background
            int vis = 0; for (int x = 0; x < dw; x++) { long s = 0; for (int y = 0; y < dh; y++) s += d[(size_t)y * dw + x] & 255; if (s / dh > 8) vis++; }
            printf("2048->256 flags=%d (%s%s): %d of 32 vertical lines visible\n", fl, fl & 1 ? "bilinear" : "nearest", fl & 2 ? "+area" : "", vis);
            if ((fl & 2) && vis != 32) { printf("  area filter lost lines!\n"); fails++; }
        }
    }
    // 2./3. no-op at factor ~1, parity, reference
    for (int it = 0; it < 200; it++)
    {
        int sw = 8 + rnd() % 120, sh = 8 + rnd() % 120;
        std::vector<int> src((size_t)sw * sh); for (auto& v : src) v = (int)rnd();
        int k = 1 + rnd() % 5; bool exact = it % 2 == 0;
        int dw = exact ? sw / k : (int)(sw / (k + 0.3f)), dh = exact ? sh / k : (int)(sh / (k + 0.2f)); if (dw < 1) dw = 1; if (dh < 1) dh = 1;
        int DW = dw + 10, DH = dh + 10;
        float q[8] = { 5, 5, 5.f + dw, 5, 5.f + dw, 5.f + dh, 5, 5.f + dh };
        for (int fl = 2; fl < 4; fl++) for (int op : { 1, 3 })
        {
            std::vector<int> bg((size_t)DW * DH); for (auto& v : bg) v = (int)(rnd() | 0xff000000u);
            std::vector<int> d1 = bg, d2 = bg, d0 = bg;
            S.DRAW_WARP(src.data(), sw, sh, d1.data(), DW, 0, 0, DW, DH, q, 0, 0, op, fl, 0);
            A.DRAW_WARP(src.data(), sw, sh, d2.data(), DW, 0, 0, DW, DH, q, 0, 0, op, fl, 0);
            if (d1 != d2) { printf("it %d: SSE2 != AVX2 (fl %d op %d)\n", it, fl, op); fails++; }
            A.DRAW_WARP(src.data(), sw, sh, d0.data(), DW, 0, 0, DW, DH, q, 0, 0, op, fl & 1, 0);
            if (k == 1 && d0 != d2) { printf("it %d: area flag changed output at factor 1\n", it); fails++; }
            if (exact && k > 1 && sw % k == 0 && sh % k == 0 && op == 1)
            {   // reference: plain box average of k x k blocks, then Paint 1:1
                for (int y = 0; y < dh; y++) for (int x = 0; x < dw; x++)
                {
                    uint32_t s[4] = { 0, 0, 0, 0 };
                    for (int yy = 0; yy < k; yy++) for (int xx = 0; xx < k; xx++) { uint32_t p = (uint32_t)src[(size_t)(y * k + yy) * sw + x * k + xx]; for (int c = 0; c < 4; c++) s[c] += (p >> (c * 8)) & 255; }
                    uint32_t n = k * k, want = 0; for (int c = 0; c < 4; c++) want |= ((s[c] + n / 2) / n) << (c * 8);
                    uint32_t got = (uint32_t)d2[(size_t)(y + 5) * DW + x + 5];
                    if (got != want) { if (fails < 5) printf("it %d: ref mismatch at %d,%d: %08x vs %08x (k %d)\n", it, x, y, got, want, k); fails++; y = dh; break; }
                }
            }
        }
    }
    // 4. timings
    {
        int sw = 2048, sh = 2048; std::vector<int> src((size_t)sw * sh); for (auto& v : src) v = (int)rnd();
        int DW = 1920, DH = 1080; std::vector<int> d((size_t)DW * DH, 0);
        for (int ds : { 1024, 512, 256 }) for (int fl : { 1, 3 })
        {
            float q[8] = { 0, 0, (float)ds, 0, (float)ds, (float)ds, 0, (float)ds };
            A.DRAW_WARP(src.data(), sw, sh, d.data(), DW, 0, 0, DW, DH, q, 0, 0, 1, fl, 0);
            auto t0 = std::chrono::steady_clock::now(); int n = 10;
            for (int i = 0; i < n; i++) A.DRAW_WARP(src.data(), sw, sh, d.data(), DW, 0, 0, DW, DH, q, 0, 0, 1, fl, 0);
            printf("2048x2048 -> %dx%d %s: %.3f ms\n", ds, ds, fl & 2 ? "bilinear+area" : "bilinear", std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / n);
        }
    }
    printf("area: %d failures\n", fails);
    return fails ? 1 : 0;
}
