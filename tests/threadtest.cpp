// threadtest: N threads draw concurrently into disjoint bands of one canvas (the DrawParallel
// pattern) using every export that takes scratch memory (DRAW_BLUR, DRAW_FX, DRAW_WARP area /
// premul paths) with wildly different scratch sizes, so the shared scratch slot is contended.
// Checks: results are identical to a single-threaded run, canary pads untouched.
// Best run under -fsanitize=address or -fsanitize=thread (see Makefile: make tsan).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>
#include <thread>
#include <atomic>
#include "../native/sr2d_ops.h"

static void job(const sr2d_ops& A, int* d, int dw, int y0, int y1, const int* src, int sw, int sh, int seed)
{
    uint32_t r = (uint32_t)seed * 2654435761u + 12345u;
    auto rnd = [&]() { r = r * 1664525u + 1013904223u; return r >> 8; };
    for (int k = 0; k < 40; ++k)
    {
        int rad = (int)(rnd() % 4 == 0 ? 40 + rnd() % 40 : rnd() % 12);      // tiny .. huge scratch
        int x = (int)(rnd() % dw) - sw / 2, y = y0 + (int)(rnd() % (y1 - y0)) - sh / 2;
        A.DRAW_BLUR(const_cast<int*>(src), sw, sh, d, dw, 0, y0, dw, y1, x, y, rad, 3, 128, 1, 256);
        SR2D_FxStage st[2] = {};
        st[0].kind = 1; st[0].i[0] = (int)(rnd() % 20); st[1].kind = 3; st[1].i[0] = (int)(rnd() % 4); st[1].f[0] = 20; st[1].f[1] = 6; st[1].f[2] = (float)k;
        float q[8] = { (float)x, (float)y, (float)(x + sw * 1.5f), (float)y, (float)(x + sw * 1.5f), (float)(y + sh * 1.5f), (float)x, (float)(y + sh * 1.5f) };
        A.DRAW_FX(const_cast<int*>(src), sw, sh, d, dw, 0, y0, dw, y1, q, 0, 0, 3, (rnd() & 1) ? 8 | 1 : 1, 128, st, 2);
        float q2[8] = { (float)x, (float)y, (float)(x + sw / 3), (float)y, (float)(x + sw / 3), (float)(y + sh / 3), (float)x, (float)(y + sh / 3) };
        A.DRAW_WARP(const_cast<int*>(src), sw, sh, d, dw, 0, y0, dw, y1, q2, 0, 0, 3, 3, 128);   // area filter -> shrink scratch
    }
}

int main(int argc, char** argv)
{
    int rounds = argc > 1 ? atoi(argv[1]) : 20;
    sr2d_ops O[2]; sr2d_fill_ops_sse2(O[0]); sr2d_fill_ops_avx2(O[1]);
    const int sw = 160, sh = 160, dw = 1280, dh = 720, PAD = 4096, T = 8;
    std::vector<int> src((size_t)sw * sh);
    { uint32_t r = 7; for (auto& v : src) { r = r * 1664525u + 1013904223u; v = (int)r; } }
    int fails = 0;
    for (int round = 0; round < rounds; ++round)
    {
        const sr2d_ops& A = O[round & 1];
        std::vector<int> ref((size_t)dw * dh + 2 * PAD, 0x5a5a5a5a), par = ref;
        for (int t = 0; t < T; ++t) job(A, ref.data() + PAD, dw, dh * t / T, dh * (t + 1) / T, src.data(), sw, sh, round * 100 + t);
        std::vector<std::thread> th;
        for (int t = 0; t < T; ++t) th.emplace_back([&, t] { job(A, par.data() + PAD, dw, dh * t / T, dh * (t + 1) / T, src.data(), sw, sh, round * 100 + t); });
        for (auto& x : th) x.join();
        if (memcmp(ref.data(), par.data(), ref.size() * sizeof(int)) != 0) { printf("round %d: parallel result differs from sequential\n", round); ++fails; }
        for (int i = 0; i < PAD; ++i) if (par[i] != 0x5a5a5a5a || par[PAD + (size_t)dw * dh + i] != 0x5a5a5a5a) { printf("round %d: canary hit\n", round); ++fails; break; }
    }
    printf("threads: %d rounds x %d threads, %d failures\n", rounds, T, fails);
    return fails ? 1 : 0;
}
