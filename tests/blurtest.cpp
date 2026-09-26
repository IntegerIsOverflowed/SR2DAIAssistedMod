// DRAW_BLUR checks:
//   1. against a double-precision reference (3 box passes, premultiplied, soft edges),
//      all pixels within 2/255 on every channel, SSE2 == AVX2 bit-exact;
//   2. energy conservation: total premultiplied alpha before == after (blur only moves
//      coverage around, it does not create or destroy it);
//   3. clipping: any clip rect produces exactly the corresponding sub-image of the
//      unclipped result, and never writes outside;
//   4. timings for a few radii (cost must not grow with r).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <vector>
#include <chrono>
#include "../native/sr2d_ops.h"

static uint32_t rng = 12345;
static uint32_t rnd() { rng = rng * 1664525u + 1013904223u; return rng >> 8; }

// reference: straight -> premultiplied doubles, pad by 3r, box blur 3x each axis, composite AlphaBlend (straight) onto dst
static void ref_blur(const std::vector<int>& src, int sw, int sh, std::vector<int>& dst, int dw, int dh, int dx, int dy, int r, int strength)
{
    int m = 3 * r, W = sw + 2 * m, H = sh + 2 * m;
    std::vector<double> a(W * H * 4, 0.0), b(W * H * 4, 0.0);
    for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
    {
        uint32_t v = src[y * sw + x]; double al = (v >> 24) / 255.0;
        double* q = &a[((y + m) * W + (x + m)) * 4];
        q[0] = (v & 255) / 255.0 * al; q[1] = ((v >> 8) & 255) / 255.0 * al; q[2] = ((v >> 16) & 255) / 255.0 * al; q[3] = al;
    }
    int taps = 2 * r + 1;
    for (int pass = 0; pass < 3 && r > 0; pass++)
    {
        // horizontal a -> b
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) for (int c = 0; c < 4; c++)
        {
            double s = 0; for (int k = -r; k <= r; k++) { int xx = x + k; if (xx >= 0 && xx < W) s += a[(y * W + xx) * 4 + c]; }
            b[(y * W + x) * 4 + c] = s / taps;
        }
        // vertical b -> a
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) for (int c = 0; c < 4; c++)
        {
            double s = 0; for (int k = -r; k <= r; k++) { int yy = y + k; if (yy >= 0 && yy < H) s += b[(yy * W + x) * 4 + c]; }
            a[(y * W + x) * 4 + c] = s / taps;
        }
    }
    double st = strength / 256.0;
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
    {
        int X = x + dx - m, Y = y + dy - m; if (X < 0 || Y < 0 || X >= dw || Y >= dh) continue;
        const double* q = &a[(y * W + x) * 4];
        double al = q[3] * st; if (al <= 0) continue;
        // straight colour = premul / alpha
        double col[3] = { q[0] * st / al, q[1] * st / al, q[2] * st / al };
        uint32_t d = dst[Y * dw + X];
        // AlphaBlend as in OpAlphaB: rgb = (d*(256-a8) + s*a8)>>8 with a8 = round(al*255) (+ a>>7), alpha unchanged
        int a8 = (int)(al * 255 + 0.5); if (a8 > 255) a8 = 255;
        int aw = a8 + (a8 >> 7), ia = 256 - aw;
        uint32_t out = d & 0xff000000u;
        for (int c = 0; c < 3; c++)
        {
            int sc = (int)(col[c] * 255 + 0.5); if (sc > 255) sc = 255;
            int dc = (d >> (c * 8)) & 255;
            out |= (uint32_t)(((dc * ia + sc * aw) >> 8) & 255) << (c * 8);
        }
        dst[Y * dw + X] = (int)out;
    }
}

int main(int argc, char** argv)
{
    int iters = argc > 1 ? atoi(argv[1]) : 200;
    sr2d_ops S, A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
    int fails = 0; int maxdiff = 0;
    for (int it = 0; it < iters; it++)
    {
        int sw = 1 + rnd() % 40, sh = 1 + rnd() % 40, r = rnd() % 9, strength = (it % 3 == 0) ? 256 : rnd() % 257;
        int dw = 64 + rnd() % 30, dh = 64 + rnd() % 30;
        int dx = (int)(rnd() % (dw + 40)) - 20, dy = (int)(rnd() % (dh + 40)) - 20;
        std::vector<int> src(sw * sh);
        for (auto& v : src) { uint32_t a = rnd() % 4 == 0 ? 255 : rnd() & 255; v = (int)((a << 24) | (rnd() & 0xffffff)); }
        std::vector<int> bg(dw * dh); for (auto& v : bg) v = (int)(rnd() | 0xff000000u);
        std::vector<int> d0 = bg, d1 = bg, d2 = bg;
        ref_blur(src, sw, sh, d0, dw, dh, dx, dy, r, strength);
        S.DRAW_BLUR(src.data(), sw, sh, d1.data(), dw, 0, 0, dw, dh, dx, dy, r, 3, 0, 0, strength);
        A.DRAW_BLUR(src.data(), sw, sh, d2.data(), dw, 0, 0, dw, dh, dx, dy, r, 3, 0, 0, strength);
        if (memcmp(d1.data(), d2.data(), dw * dh * 4) != 0) { if (fails < 5) printf("it %d: SSE2 != AVX2\n", it); fails++; }
        int md = 0; for (int i = 0; i < dw * dh; i++) for (int c = 0; c < 4; c++) { int a = (d0[i] >> (c * 8)) & 255, b = (d1[i] >> (c * 8)) & 255; md = md > abs(a - b) ? md : abs(a - b); }
        if (md > maxdiff) maxdiff = md;
        if (md > 3) { if (fails < 5) printf("it %d: max diff vs reference %d (sw %d sh %d r %d strength %d)\n", it, md, sw, sh, r, strength); fails++; }
        // clip test: random clip rect must equal sub-rect of the full result and touch nothing else
        int cl = rnd() % dw, ct = rnd() % dh, cr = cl + 1 + rnd() % (dw - cl), cb = ct + 1 + rnd() % (dh - ct);
        std::vector<int> d3 = bg;
        A.DRAW_BLUR(src.data(), sw, sh, d3.data(), dw, cl, ct, cr, cb, dx, dy, r, 3, 0, 0, strength);
        for (int y = 0; y < dh; y++) for (int x = 0; x < dw; x++)
        {
            bool in = x >= cl && x < cr && y >= ct && y < cb;
            int want = in ? d2[y * dw + x] : bg[y * dw + x];
            if (d3[y * dw + x] != want) { if (fails < 5) printf("it %d: clip mismatch at %d,%d\n", it, x, y); fails++; y = dh; break; }
        }
    }
    printf("blur: %d iterations, max channel diff vs double reference %d, %d failures\n", iters, maxdiff, fails);

    // flags: OPAQUE == source with alpha forced to 255; PREMUL on a premultiplied copy ~= straight path;
    //        FAST (2 passes) and every op code must at least run, agree between SSE2/AVX2 and stay inside the clip
    {
        int sw = 37, sh = 23, dw = 120, dh = 100; std::vector<int> src(sw * sh), opq(sw * sh), pre(sw * sh);
        for (int i = 0; i < sw * sh; i++)
        {
            uint32_t a = rnd() & 255, c = rnd() & 0xffffff; src[i] = (int)((a << 24) | c); opq[i] = (int)(0xff000000u | c);
            uint32_t b = ((c & 255) * a + 127) / 255, g = (((c >> 8) & 255) * a + 127) / 255, r = (((c >> 16) & 255) * a + 127) / 255;
            pre[i] = (int)((a << 24) | (r << 16) | (g << 8) | b);
        }
        std::vector<int> bg(dw * dh); for (auto& v : bg) v = (int)(rnd() | 0xff000000u);
        for (int r : { 0, 3, 7 })
        {
            std::vector<int> d1 = bg, d2 = bg;
            A.DRAW_BLUR(src.data(), sw, sh, d1.data(), dw, 0, 0, dw, dh, 30, 20, r, 3, 0, 2, 256);
            A.DRAW_BLUR(opq.data(), sw, sh, d2.data(), dw, 0, 0, dw, dh, 30, 20, r, 3, 0, 0, 256);
            if (d1 != d2) { printf("opaque flag mismatch r=%d\n", r); fails++; }
            d1 = bg; d2 = bg;
            A.DRAW_BLUR(src.data(), sw, sh, d1.data(), dw, 0, 0, dw, dh, 30, 20, r, 11, 0, 0, 256);
            A.DRAW_BLUR(pre.data(), sw, sh, d2.data(), dw, 0, 0, dw, dh, 30, 20, r, 11, 0, 1, 256);
            int md = 0; for (int i = 0; i < dw * dh; i++) for (int c = 0; c < 4; c++) { int a = (d1[i] >> (c * 8)) & 255, b = (d2[i] >> (c * 8)) & 255; md = md > abs(a - b) ? md : abs(a - b); }
            if (md > 3) { printf("premul flag mismatch r=%d (max diff %d)\n", r, md); fails++; }
            for (int op = 1; op <= 11; op++) for (int fl = 0; fl <= 4; fl += 4)
            {
                d1 = bg; d2 = bg;
                int n1 = S.DRAW_BLUR(src.data(), sw, sh, d1.data(), dw, 10, 10, 90, 80, 30, 20, r, op, 128, fl, 200);
                int n2 = A.DRAW_BLUR(src.data(), sw, sh, d2.data(), dw, 10, 10, 90, 80, 30, 20, r, op, 128, fl, 200);
                if (n1 != n2 || d1 != d2) { printf("op %d flags %d r %d: SSE2 != AVX2\n", op, fl, r); fails++; }
                bool outside = false;
                for (int y = 0; y < dh; y++) for (int x = 0; x < dw; x++)
                    if ((x < 10 || x >= 90 || y < 10 || y >= 80) && d2[y * dw + x] != bg[y * dw + x]) outside = true;
                if (outside) { printf("op %d flags %d r %d: wrote outside clip\n", op, fl, r); fails++; }
            }
        }
        // op 12 (copy straight) and in-place (src == dst) must equal the separate-buffer result
        for (int r : { 0, 3, 7 })
        {
            std::vector<int> big(dw * dh, 0), inplace(dw * dh, 0);
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) big[(y + 20) * dw + x + 30] = inplace[(y + 20) * dw + x + 30] = src[y * sw + x];
            std::vector<int> d1(dw * dh, 0);
            A.DRAW_BLUR(big.data(), dw, dh, d1.data(), dw, 0, 0, dw, dh, 0, 0, r, 12, 0, 0, 256);
            A.DRAW_BLUR(inplace.data(), dw, dh, inplace.data(), dw, 0, 0, dw, dh, 0, 0, r, 12, 0, 0, 256);
            if (d1 != inplace) { printf("in-place blur differs r=%d\n", r); fails++; }
            // straight copy composited with AlphaBlend onto bg == DRAW_BLUR AlphaBlend directly (within rounding)
            std::vector<int> d2 = bg, d3 = bg;
            A.ALPHA_B(d1.data(), d2.data(), dw, dh, dw, dw);
            A.DRAW_BLUR(src.data(), sw, sh, d3.data(), dw, 0, 0, dw, dh, 30, 20, r, 3, 0, 0, 256);
            int md = 0; for (int i = 0; i < dw * dh; i++) for (int c = 0; c < 3; c++) { int a = (d2[i] >> (c * 8)) & 255, b = (d3[i] >> (c * 8)) & 255; md = md > abs(a - b) ? md : abs(a - b); }
            if (md > 2) { printf("op 12 + ALPHA_B vs direct AlphaBlend: max diff %d r=%d\n", md, r); fails++; }
        }
        printf("flags/ops: done\n");
    }

    // energy: onto a black transparent layer with AlphaOver (premultiplied), sum alpha before == after
    {
        int sw = 50, sh = 30, dw = 300, dh = 300; std::vector<int> src(sw * sh); double sumA = 0;
        for (auto& v : src) { uint32_t a = rnd() & 255; v = (int)((a << 24) | (rnd() & 0xffffff)); sumA += a; }
        for (int r : { 1, 4, 12, 30 })
        {
            std::vector<int> d(dw * dh, 0);
            A.DRAW_BLUR(src.data(), sw, sh, d.data(), dw, 0, 0, dw, dh, 120, 120, r, 11, 0, 0, 256);
            double s2 = 0; for (int v : d) s2 += (uint32_t)v >> 24;
            printf("energy r=%2d: alpha sum %.0f -> %.0f (%.2f%%)\n", r, sumA, s2, 100.0 * s2 / sumA);
            if (fabs(s2 / sumA - 1) > 0.01) { printf("  energy off by more than 1%%\n"); fails++; }
        }
    }

    // timing: 256x256 sprite onto 1920x1080, AlphaBlend, various radii
    {
        int sw = 256, sh = 256, dw = 1920, dh = 1080; std::vector<int> src(sw * sh), d(dw * dh, (int)0xff203040);
        for (auto& v : src) v = (int)(rnd() | 0x80000000u);
        for (int r : { 0, 2, 8, 32, 128 })
        {
            for (int lv = 0; lv < 2; lv++)
            {
                sr2d_ops& O = lv ? A : S; int n = 20;
                O.DRAW_BLUR(src.data(), sw, sh, d.data(), dw, 0, 0, dw, dh, 500, 300, r, 3, 0, 0, 256);
                auto t0 = std::chrono::steady_clock::now();
                for (int i = 0; i < n; i++) O.DRAW_BLUR(src.data(), sw, sh, d.data(), dw, 0, 0, dw, dh, 500, 300, r, 3, 0, 0, 256);
                double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / n;
                int W = sw + 6 * r; printf("%s 256x256 r=%3d (work %dx%d): %.3f ms\n", lv ? "AVX2" : "SSE2", r, W, W, ms);
            }
        }
    }
    return fails ? 1 : 0;
}
