// dispatchtest: the exported dispatch path (sr2d.cpp) under contention. 8 threads start together and
// race the very first export call (lazy table fill), then keep drawing through the exports while one
// thread flips SR2D_SET_SIMD_LEVEL between SSE2 / AVX2 / auto. Checks: no crash, every call sees a
// complete table (results identical to a quiet single-threaded run), level always 1 or 2.
// Best under -fsanitize=thread (make tsan) or address (make asan).
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <vector>
#include <thread>
#include <atomic>
#include <stdint.h>
extern "C" {
int  SR2D_SIMD_LEVEL();
int  SR2D_SET_SIMD_LEVEL(int);
void DRAW_LINE2(int* dst, int dw, int clipL, int clipT, int clipR, int clipB, float x0, float y0, float x1, float y1, int col, int op, int k, float dotlen, float gaplen, float phase);
void CLEAR_C(int* dst, int w, int h, int wd, int c);
int  DRAW_BLUR(int* src, int sw, int sh, int* dst, int dw, int clipL, int clipT, int clipR, int clipB, int dx, int dy, int r, int op, int k, int flags, int strength);
}
static const int W = 128, H = 96;
static void work(std::vector<int>& d, const std::vector<int>& src, int seed)
{
    for (int i = 0; i < 40; ++i)
    {
        int x = (seed * 7 + i * 13) % W, y = (seed * 3 + i * 5) % H;
        CLEAR_C(d.data() + (y / 2) * W + x / 2, 20, 10, W, (int)(0xFF000000u | (uint32_t)(seed * 0x1234567 + i)));
        DRAW_LINE2(d.data(), W, 0, 0, W, H, (float)x, (float)y, (float)(W - x), (float)(H - y), 0xFFFFFFFF, 3, 128, 0, 0, 0);
        DRAW_BLUR(const_cast<int*>(src.data()), W, H, d.data(), W, 0, 0, W, H, 0, 0, 2 + (i % 5), 3, 128, 0, 200);
    }
}
int main(int argc, char** argv)
{
    int rounds = argc > 1 ? atoi(argv[1]) : 4;
    std::vector<int> src((size_t)W * H); for (size_t i = 0; i < src.size(); ++i) src[i] = (int)(0xFF000000u | (uint32_t)(i * 2654435761u));
    int fails = 0;
    for (int r = 0; r < rounds; ++r)
    {
        std::atomic<int> go{0}; std::atomic<int> bad{0};
        std::vector<std::vector<int>> out(8, std::vector<int>((size_t)W * H, 0));
        std::vector<std::thread> th;
        for (int t = 0; t < 8; ++t) th.emplace_back([&, t]
        {
            while (!go.load()) {}
            if (t == 7) { for (int i = 0; i < 300; ++i) { int lv = SR2D_SET_SIMD_LEVEL(i % 3); if (lv != 1 && lv != 2) bad++; } SR2D_SET_SIMD_LEVEL(0); }
            else { work(out[t], src, t); int lv = SR2D_SIMD_LEVEL(); if (lv != 1 && lv != 2) bad++; }
        });
        go = 1; for (auto& x : th) x.join();
        SR2D_SET_SIMD_LEVEL(0);
        for (int t = 0; t < 7; ++t) { std::vector<int> ref((size_t)W * H, 0); work(ref, src, t); if (memcmp(ref.data(), out[t].data(), ref.size() * 4) != 0) { printf("round %d thread %d: result differs\n", r, t); fails++; } }
        if (bad) { printf("round %d: %d bad levels\n", r, bad.load()); fails++; }
    }
    printf("dispatch: %d rounds x 8 threads, level %d, %d failures\n", rounds, SR2D_SIMD_LEVEL(), fails);
    return fails ? 1 : 0;
}
