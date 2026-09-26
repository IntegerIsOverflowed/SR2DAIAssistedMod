// floodtest: FLOOD_MASK vs a plain BFS reference (4- and 8-connected, tolerance, global,
// ignore-alpha, soft edge), SSE2 == AVX2, FILL_MASK8 vs scalar compositing, LERP_MASK8 vs
// reference, out-of-bounds / clip fuzz with canary pads, timings.
// Usage: floodtest [iterations=400]
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>
#include <chrono>
#include "../native/sr2d_ops.h"
static uint32_t rng = 7; static uint32_t rnd() { rng = rng * 1664525u + 1013904223u; return rng >> 8; }
static int chdiff(uint32_t a, uint32_t b, bool ia) { int m = 0; for (int sh = 0; sh < (ia ? 24 : 32); sh += 8) { int d = (int)((a >> sh) & 255) - (int)((b >> sh) & 255); if (d < 0) d = -d; if (d > m) m = d; } return m; }
// reference: BFS
static int ref_flood(const std::vector<int>& img, int w, int h, int cl, int ct, int cr, int cb, int x, int y, int ref, int tol, int flags, std::vector<uint8_t>& m)
{
    m.assign((size_t)w * h, 0);
    bool ia = flags & 2, diag = flags & 4, global = flags & 1;
    if (cl < 0) cl = 0; if (ct < 0) ct = 0; if (cr > w) cr = w; if (cb > h) cb = h;
    if (cl >= cr || ct >= cb) return 0;
    uint32_t rc = (flags & 16) ? (uint32_t)ref : (x >= 0 && x < w && y >= 0 && y < h) ? (uint32_t)img[(size_t)y * w + x] : (uint32_t)ref;
    int n = 0;
    if (global) { for (int yy = ct; yy < cb; ++yy) for (int xx = cl; xx < cr; ++xx) if (chdiff((uint32_t)img[(size_t)yy * w + xx], rc, ia) <= tol) { m[(size_t)yy * w + xx] = 255; ++n; } }
    else
    {
        if (x < cl || x >= cr || y < ct || y >= cb) return 0;
        if (chdiff((uint32_t)img[(size_t)y * w + x], rc, ia) > tol) return 0;
        std::vector<int> q; q.push_back(y * w + x); m[(size_t)y * w + x] = 255; n = 1;
        static const int dx8[8] = { 1,-1,0,0,1,1,-1,-1 }, dy8[8] = { 0,0,1,-1,1,-1,1,-1 };
        for (size_t i = 0; i < q.size(); ++i)
        {
            int px = q[i] % w, py = q[i] / w;
            for (int k = 0; k < (diag ? 8 : 4); ++k)
            {
                int nx = px + dx8[k], ny = py + dy8[k];
                if (nx < cl || nx >= cr || ny < ct || ny >= cb) continue;
                size_t idx = (size_t)ny * w + nx; if (m[idx]) continue;
                if (chdiff((uint32_t)img[idx], rc, ia) <= tol) { m[idx] = 255; q.push_back((int)idx); ++n; }
            }
        }
    }
    if ((flags & 8) && tol > 0)
    {
        int hh = tol >> 1, span = tol - hh + 1;
        for (size_t i = 0; i < m.size(); ++i) if (m[i]) { int d = chdiff((uint32_t)img[i], rc, ia); if (d > hh) { int c = 255 - (d - hh) * 255 / span; m[i] = (uint8_t)(c < 1 ? 1 : c); } }
    }
    return n;
}
static uint32_t lerp8(uint32_t d, uint32_t s, int wt) { uint32_t r = 0; for (int sh = 0; sh < 32; sh += 8) { uint32_t a = (d >> sh) & 255, b = (s >> sh) & 255; r |= (((a * (256 - wt) + b * wt) >> 8) & 255) << sh; } return r; }

int main(int argc, char** argv)
{
    int iters = argc > 1 ? atoi(argv[1]) : 400;
    sr2d_ops O[2]; sr2d_fill_ops_sse2(O[0]); sr2d_fill_ops_avx2(O[1]);
    int fails = 0;
    const int PAD = 1024;
    for (int it = 0; it < iters; ++it)
    {
        int w = 1 + rnd() % 90, h = 1 + rnd() % 60; if (it % 7 == 0) { w = 1 + rnd() % 400; h = 1 + rnd() % 200; }
        std::vector<int> img((size_t)w * h);
        int style = rnd() % 4;
        for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x)
        {
            uint32_t c;
            if (style == 0) c = (rnd() % 3) * 0x404040u | 0xff000000u;                                    // 3 flat colours, noisy shapes
            else if (style == 1) c = ((x / 5 + y / 5) & 1) ? 0xff2040ffu : 0xffff8000u;                   // checker (many small regions)
            else if (style == 2) c = 0xff000000u | ((uint32_t)(x * 255 / (w > 1 ? w - 1 : 1)) << 16) | ((uint32_t)(y * 255 / (h > 1 ? h - 1 : 1)) << 8);  // gradient (tolerance bands)
            else c = rnd() | (rnd() << 24);                                                               // noise
            if (style != 3 && rnd() % 23 == 0) c ^= 0x00101010u;                                          // slight noise inside flat areas
            img[(size_t)y * w + x] = (int)c;
        }
        int cl = 0, ct = 0, cr = w, cb = h;
        if (rnd() % 3 == 0) { cl = rnd() % w; ct = rnd() % h; cr = cl + 1 + rnd() % (w - cl); cb = ct + 1 + rnd() % (h - ct); }
        int x = (int)(rnd() % (w + 4)) - 2, y = (int)(rnd() % (h + 4)) - 2;
        int tol = rnd() % 5 == 0 ? 0 : rnd() % 200; if (rnd() % 20 == 0) tol = 100000;
        int flags = rnd() % 32; int ref = (int)(rnd() | (rnd() << 24));
        std::vector<uint8_t> mref; int nref = ref_flood(img, w, h, cl, ct, cr, cb, x, y, ref, tol, flags, mref);
        for (int lvl = 0; lvl < 2; ++lvl)
        {
            std::vector<uint8_t> m((size_t)w * h + 2 * PAD, 0x77); uint8_t* mp = m.data() + PAD;
            int bbox[4];
            int n = O[lvl].FLOOD_MASK(img.data(), w, h, cl, ct, cr, cb, x, y, ref, tol, flags, mp, w, bbox);
            if (n != nref) { printf("it %d lvl %d: count %d != ref %d (flags %d tol %d)\n", it, lvl, n, nref, flags, tol); ++fails; break; }
            int bad = 0, L = w, T = h, R = 0, B = 0;
            for (int yy = 0; yy < h; ++yy) for (int xx = 0; xx < w; ++xx)
            {
                bool in = xx >= cl && xx < cr && yy >= ct && yy < cb;
                uint8_t got = mp[(size_t)yy * w + xx];
                if (!in) { if (got != 0x77) ++bad; continue; }
                if (got != mref[(size_t)yy * w + xx]) ++bad;
                if (got) { if (xx < L) L = xx; if (xx + 1 > R) R = xx + 1; if (yy < T) T = yy; if (yy + 1 > B) B = yy + 1; }
            }
            for (int i = 0; i < PAD; ++i) if (m[i] != 0x77 || m[PAD + (size_t)w * h + i] != 0x77) { ++bad; break; }
            if (bad) { printf("it %d lvl %d: %d mask pixels differ (flags %d tol %d %dx%d seed %d,%d clip %d %d %d %d)\n", it, lvl, bad, flags, tol, w, h, x, y, cl, ct, cr, cb); ++fails; break; }
            if (n > 0 && (bbox[0] != L || bbox[1] != T || bbox[2] != R || bbox[3] != B)) { printf("it %d lvl %d: bbox %d %d %d %d != %d %d %d %d\n", it, lvl, bbox[0], bbox[1], bbox[2], bbox[3], L, T, R, B); ++fails; break; }

            // FILL_MASK8 vs scalar reference for the same mask, every op
            int op = 1 + rnd() % 8, k = rnd() % 300 - 20, col = (int)(rnd() | (rnd() << 24));
            std::vector<int> d1(img.size() + 2 * PAD, 0x5a5a5a5a), d2 = img;
            for (size_t i = 0; i < img.size(); ++i) d1[PAD + i] = img[i];
            int* dp = d1.data() + PAD;
            int touched = O[lvl].FILL_MASK8(dp, w, cl, ct, cr, cb, mp, w, col, op, k);
            int a = (int)((uint32_t)col >> 24), kk = k < 0 ? 0 : k > 256 ? 256 : k, tref = 0;
            for (int yy = ct; yy < cb; ++yy) for (int xx = cl; xx < cr; ++xx)
            {
                int v = mp[(size_t)yy * w + xx]; if (!v) continue; ++tref;
                int cov = v + (v >> 7); uint32_t dv = (uint32_t)d2[(size_t)yy * w + xx], r = dv, c = (uint32_t)col;
                switch (op)
                {
                case 1: r = lerp8(dv, c, cov); if (cov >= 256) r = c; break;
                case 2: r = cov >= 128 ? dv ^ c : dv; break;
                case 3: { int wt = ((a + (a >> 7)) * cov) >> 8; if (wt > 0) r = (lerp8(dv, c, wt) & 0x00ffffffu) | (dv & 0xff000000u); break; }
                case 4: { int wt = (kk * cov) >> 8; if (wt > 0) r = wt >= 256 ? c : lerp8(dv, c, wt); break; }
                case 5: case 6: case 7: { uint32_t s = 0; for (int sh = 0; sh < 32; sh += 8) { uint32_t p = (c >> sh) & 255, q = (dv >> sh) & 255, o = op == 5 ? (p + q > 255 ? 255 : p + q) : op == 6 ? (p > q ? p : q) : (p < q ? p : q); s |= o << sh; } r = cov < 256 ? lerp8(dv, s, cov) : s; break; }
                case 8: { int wt = ((a + (a >> 7)) * cov) >> 8; if (wt > 0) r = wt >= 256 ? (c | 0xff000000u) : lerp8(dv, c | 0xff000000u, wt); break; }
                }
                d2[(size_t)yy * w + xx] = (int)r;
            }
            int fbad = 0; for (size_t i = 0; i < img.size(); ++i) if (dp[i] != d2[i]) ++fbad;
            for (int i = 0; i < PAD; ++i) if (d1[i] != 0x5a5a5a5a || d1[PAD + img.size() + i] != 0x5a5a5a5a) { ++fbad; break; }
            if (fbad || touched != tref) { printf("it %d lvl %d: FILL_MASK8 op %d: %d px differ, touched %d ref %d\n", it, lvl, op, fbad, touched, tref); ++fails; break; }

            // LERP_MASK8 vs reference (whole image, random invert)
            std::vector<int> src(img.size()); for (auto& v : src) v = (int)(rnd() | (rnd() << 24));
            std::vector<int> l1 = img, l2 = img; int inv = rnd() & 1;
            O[lvl].LERP_MASK8(src.data(), l1.data(), mp, w, h, w, w, w, inv);
            for (size_t i = 0; i < img.size(); ++i) { int mv = mp[i]; if (inv) mv = 255 - mv; if (!mv) continue; l2[i] = mv == 255 ? src[i] : (int)lerp8((uint32_t)img[i], (uint32_t)src[i], mv + (mv >> 7)); }
            int lbad = 0; for (size_t i = 0; i < img.size(); ++i) if (l1[i] != l2[i]) ++lbad;
            if (lbad) { printf("it %d lvl %d: LERP_MASK8 %d px differ\n", it, lvl, lbad); ++fails; break; }
        }
        if (fails > 5) break;
    }
    // timings: 1920x1080, one big region with holes, tol 20
    {
        const int W = 1920, H = 1080; std::vector<int> img((size_t)W * H);
        for (int y = 0; y < H; ++y) for (int x = 0; x < W; ++x) { bool hole = ((x / 40) % 3 == 1) && ((y / 40) % 3 == 1); img[(size_t)y * W + x] = hole ? (int)0xff000000 : (int)(0xff808080u + (rnd() % 9) * 0x010101u); }
        std::vector<uint8_t> m((size_t)W * H);
        for (int lvl = 0; lvl < 2; ++lvl)
        {
            auto t0 = std::chrono::steady_clock::now(); int n = 0; const int reps = 5;
            for (int r = 0; r < reps; ++r) n = O[lvl].FLOOD_MASK(img.data(), W, H, 0, 0, W, H, 5, 5, 0, 20, 0, m.data(), W, 0);
            double tf = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / reps;
            t0 = std::chrono::steady_clock::now();
            for (int r = 0; r < reps; ++r) O[lvl].FILL_MASK8(img.data(), W, 0, 0, W, H, m.data(), W, (int)0x80ff4000, 3, 128);
            double tfill = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / reps;
            t0 = std::chrono::steady_clock::now();
            for (int r = 0; r < reps; ++r) O[lvl].FLOOD_MASK(img.data(), W, H, 0, 0, W, H, 5, 5, 0, 20, 1, m.data(), W, 0);
            double tg = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / reps;
            std::vector<int> cp = img;
            t0 = std::chrono::steady_clock::now();
            for (int r = 0; r < reps; ++r) O[lvl].LERP_MASK8(cp.data(), img.data(), m.data(), W, H, W, W, W, 0);
            double tl = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / reps;
            printf("  %s 1920x1080: flood (contiguous, %d px, tol 20) %.2f ms | global %.2f ms | FILL_MASK8 AlphaBlend %.2f ms | LERP_MASK8 %.2f ms\n", lvl ? "AVX2" : "SSE2", n, tf, tg, tfill, tl);
        }
    }
    printf("flood: %d failures\n", fails);
    return fails ? 1 : 0;
}
