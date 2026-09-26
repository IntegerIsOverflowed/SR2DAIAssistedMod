// Blend modes (SR2D_BM_*): every mode against a scalar double reference of the PDF /
// Photoshop formulas, in both destination contexts (straight and premultiplied) and with
// source premultiplied or not, at several opacities; SSE2 == AVX2 bit-exact; the mode reaches
// every entry point that takes an op (BLEND_MODE, MASK_BLEND_MODE, DRAW_WARP nearest and
// bilinear, DRAW_POLY, DRAW_LINE2, FILL_MASK8, DRAW_BLUR, DRAW_FX) and matches BLEND_MODE
// where the geometry is the identity.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <vector>
#include "../native/sr2d_ops.h"

static uint32_t rng = 12345; static uint32_t rnd() { rng = rng * 1664525u + 1013904223u; return rng >> 8; }

// ---- reference (double, 0..1)
static double lum(double r, double g, double b) { return 0.3 * r + 0.59 * g + 0.11 * b; }
static void clipcolor(double& r, double& g, double& b)
{
    double l = lum(r, g, b), n = fmin(r, fmin(g, b)), x = fmax(r, fmax(g, b));
    if (n < 0) { r = l + (r - l) * l / (l - n); g = l + (g - l) * l / (l - n); b = l + (b - l) * l / (l - n); }
    if (x > 1) { r = l + (r - l) * (1 - l) / (x - l); g = l + (g - l) * (1 - l) / (x - l); b = l + (b - l) * (1 - l) / (x - l); }
}
static void setlum(double& r, double& g, double& b, double l) { double d = l - lum(r, g, b); r += d; g += d; b += d; clipcolor(r, g, b); }
static double sat(double r, double g, double b) { return fmax(r, fmax(g, b)) - fmin(r, fmin(g, b)); }
static void setsat(double& r, double& g, double& b, double s)
{
    double* c[3] = { &r, &g, &b };
    int mx = 0, mn = 0;
    for (int i = 1; i < 3; ++i) { if (*c[i] > *c[mx]) mx = i; if (*c[i] < *c[mn]) mn = i; }
    if (mx == mn) { r = g = b = 0; return; }
    int md = 3 - mx - mn;
    double cmax = *c[mx], cmin = *c[mn], cmid = *c[md];
    if (cmax > cmin) { *c[md] = (cmid - cmin) * s / (cmax - cmin); *c[mx] = s; } else { *c[md] = 0; *c[mx] = 0; }
    *c[mn] = 0;
}
static double sep(int mode, double s, double d)
{
    switch (mode)
    {
    case SR2D_BM_DARKEN: return fmin(s, d);
    case SR2D_BM_MULTIPLY: return s * d;
    case SR2D_BM_COLOR_BURN: return d >= 1 ? 1 : s <= 0 ? 0 : 1 - fmin(1, (1 - d) / s);
    case SR2D_BM_LINEAR_BURN: return fmax(0, s + d - 1);
    case SR2D_BM_LIGHTEN: return fmax(s, d);
    case SR2D_BM_SCREEN: return s + d - s * d;
    case SR2D_BM_COLOR_DODGE: return d <= 0 ? 0 : s >= 1 ? 1 : fmin(1, d / (1 - s));
    case SR2D_BM_LINEAR_DODGE: return fmin(1, s + d);
    case SR2D_BM_OVERLAY: return sep(SR2D_BM_HARD_LIGHT, d, s);
    case SR2D_BM_SOFT_LIGHT:
    {
        if (s <= 0.5) return d - (1 - 2 * s) * d * (1 - d);
        double D = d <= 0.25 ? ((16 * d - 12) * d + 4) * d : sqrt(d);
        return d + (2 * s - 1) * (D - d);
    }
    case SR2D_BM_HARD_LIGHT: return s <= 0.5 ? d * 2 * s : sep(SR2D_BM_SCREEN, 2 * s - 1, d);
    case SR2D_BM_VIVID_LIGHT: return s <= 0.5 ? sep(SR2D_BM_COLOR_BURN, 2 * s, d) : sep(SR2D_BM_COLOR_DODGE, 2 * s - 1, d);
    case SR2D_BM_LINEAR_LIGHT: return fmin(1, fmax(0, d + 2 * s - 1));
    case SR2D_BM_PIN_LIGHT: return s <= 0.5 ? fmin(d, 2 * s) : fmax(d, 2 * s - 1);
    case SR2D_BM_HARD_MIX: return s + d >= 1 ? 1 : 0;
    case SR2D_BM_DIFFERENCE: return fabs(s - d);
    case SR2D_BM_EXCLUSION: return s + d - 2 * s * d;
    case SR2D_BM_SUBTRACT: return fmax(0, d - s);
    case SR2D_BM_DIVIDE: return s <= 0 ? (d > 0 ? 1 : 0) : fmin(1, d / s);
    default: return s;
    }
}
// B(s, d) on straight colours (0..1) -> r,g,b
static void blendref(int mode, const double* s, const double* d, double* o)
{
    if (mode == SR2D_BM_DARKER_COLOR || mode == SR2D_BM_LIGHTER_COLOR)
    {
        double ss = s[0] + s[1] + s[2], ds = d[0] + d[1] + d[2];
        bool takeS = mode == SR2D_BM_DARKER_COLOR ? ds > ss : ss > ds;
        for (int i = 0; i < 3; ++i) o[i] = takeS ? s[i] : d[i];
        return;
    }
    if (mode >= SR2D_BM_HUE)
    {
        double r, g, b;
        switch (mode)
        {
        case SR2D_BM_HUE: r = s[0]; g = s[1]; b = s[2]; setsat(r, g, b, sat(d[0], d[1], d[2])); setlum(r, g, b, lum(d[0], d[1], d[2])); break;
        case SR2D_BM_SATURATION: r = d[0]; g = d[1]; b = d[2]; setsat(r, g, b, sat(s[0], s[1], s[2])); setlum(r, g, b, lum(d[0], d[1], d[2])); break;
        case SR2D_BM_COLOR: r = s[0]; g = s[1]; b = s[2]; setlum(r, g, b, lum(d[0], d[1], d[2])); break;
        default: r = d[0]; g = d[1]; b = d[2]; setlum(r, g, b, lum(s[0], s[1], s[2])); break;
        }
        o[0] = fmin(1, fmax(0, r)); o[1] = fmin(1, fmax(0, g)); o[2] = fmin(1, fmax(0, b));
        return;
    }
    for (int i = 0; i < 3; ++i) o[i] = sep(mode, s[i], d[i]);
}
// full pixel: sp / dp = premultiplied source / destination, opacity 0..256
static uint32_t refpx(int mode, uint32_t S, uint32_t D, bool sp, bool dp, int opacity)
{
    double s[3], d[3], as = (S >> 24) / 255.0, ad = (D >> 24) / 255.0;
    s[0] = ((S >> 16) & 255) / 255.0; s[1] = ((S >> 8) & 255) / 255.0; s[2] = (S & 255) / 255.0;
    d[0] = ((D >> 16) & 255) / 255.0; d[1] = ((D >> 8) & 255) / 255.0; d[2] = (D & 255) / 255.0;
    double cs[3], cd[3], Dp[3];
    for (int i = 0; i < 3; ++i) { cs[i] = sp && as > 0 ? fmin(1, s[i] / as) : s[i]; Dp[i] = d[i]; cd[i] = dp && ad > 0 ? fmin(1, d[i] / ad) : d[i]; }
    double w = as * (opacity / 256.0);
    double B[3]; blendref(mode, cs, cd, B);
    double o[3], oa;
    if (!dp) { for (int i = 0; i < 3; ++i) o[i] = Dp[i] + (B[i] - Dp[i]) * w; oa = ad; }
    else
    {
        for (int i = 0; i < 3; ++i) { double X = (1 - ad) * cs[i] + ad * B[i]; o[i] = Dp[i] * (1 - w) + w * X; }
        oa = w + ad - w * ad;
    }
    auto q = [](double v) { int i = (int)floor(v * 255 + 0.5); return (uint32_t)(i < 0 ? 0 : i > 255 ? 255 : i); };
    return (q(oa) << 24) | (q(o[0]) << 16) | (q(o[1]) << 8) | q(o[2]);
}
static int cdiff(uint32_t a, uint32_t b) { int m = 0; for (int sh = 0; sh < 32; sh += 8) { int d = (int)((a >> sh) & 255) - (int)((b >> sh) & 255); if (d < 0) d = -d; if (d > m) m = d; } return m; }
static uint32_t premul(uint32_t c)
{
    uint32_t a = c >> 24; auto m = [&](uint32_t v) { return (v * a + 127) / 255; };
    return (a << 24) | (m((c >> 16) & 255) << 16) | (m((c >> 8) & 255) << 8) | m(c & 255);
}

int main(int argc, char** argv)
{
    int iters = argc > 1 ? atoi(argv[1]) : 200;
    sr2d_ops S, A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
    int fails = 0;
    const int W = 64, H = 4;
    // ---- 1. formula check per mode / context, random + edge pixels
    for (int mode = SR2D_BM_FIRST; mode <= SR2D_BM_LAST; ++mode)
    {
        if (mode == SR2D_BM_DISSOLVE) continue;
        const int tolI = mode >= SR2D_BM_HUE ? 3 : (mode == SR2D_BM_SOFT_LIGHT || mode == SR2D_BM_COLOR_BURN || mode == SR2D_BM_COLOR_DODGE || mode == SR2D_BM_VIVID_LIGHT || mode == SR2D_BM_DIVIDE) ? 2 : 1;
        int worst = 0; long long tot = 0;
        for (int it = 0; it < iters; ++it)
        {
            const bool sp = it & 1, dp = (it >> 1) & 1;
            const int opa = (it % 5 == 0) ? 256 : (int)(rnd() % 257);
            std::vector<int> src(W * H), dst(W * H), d1, d2;
            for (int i = 0; i < W * H; ++i)
            {
                uint32_t s = rnd(), d = rnd();
                int e = i % 16;                              // edge values on every 16th pixel
                if (e == 0) s = 0xff000000u; else if (e == 1) s = 0xffffffffu; else if (e == 2) s = 0x00000000u; else if (e == 3) s |= 0xff000000u;
                if (e == 4) d = 0xff000000u; else if (e == 5) d = 0xffffffffu; else if (e == 6) d = 0x00000000u; else if (e == 7) d |= 0xff000000u;
                if (e == 8) { s = 0xff808080u; d = 0xff808080u; }
                if (sp) s = premul(s);
                if (dp) d = premul(d);
                src[i] = (int)s; dst[i] = (int)d;
            }
            d1 = dst; d2 = dst;
            const int op = mode | (sp ? SR2D_OP_SRC_PREMUL : 0) | (dp ? SR2D_OP_DST_PREMUL : 0) | ((opa + 1) << SR2D_OP_OPACITY_SHIFT);
            S.BLEND_MODE(src.data(), d1.data(), W, H, W, W, op);
            A.BLEND_MODE(src.data(), d2.data(), W, H, W, W, op);
            if (memcmp(d1.data(), d2.data(), W * H * 4) != 0) { if (fails++ < 10) printf("mode %d: SSE2 != AVX2\n", mode); }
            for (int i = 0; i < W * H; ++i)
            {
                uint32_t r = refpx(mode, (uint32_t)src[i], (uint32_t)dst[i], sp, dp, opa);
                int df = cdiff(r, (uint32_t)d1[i]); tot += df;
                if (df > worst) worst = df;
                if (df > tolI && fails++ < 20) printf("mode %d sp %d dp %d opa %d: s %08x d %08x -> got %08x ref %08x (diff %d)\n", mode, sp, dp, opa, src[i], dst[i], d1[i], r, df);
            }
        }
        printf("mode %2d: worst %d, mean %.3f\n", mode, worst, (double)tot / (iters * W * H));
    }
    // ---- 2. NORMAL at full opacity on a premultiplied destination == ALPHA_OVER (premultiplied source) exactly-ish
    {
        std::vector<int> src(W * H), a(W * H), b(W * H);
        for (int i = 0; i < W * H; ++i) { src[i] = (int)premul(rnd()); a[i] = b[i] = (int)premul(rnd()); }
        S.ALPHA_OVER(src.data(), a.data(), W, H, W, W);
        S.BLEND_MODE(src.data(), b.data(), W, H, W, W, SR2D_BM_NORMAL | SR2D_OP_SRC_PREMUL | SR2D_OP_DST_PREMUL);
        int worst = 0; for (int i = 0; i < W * H; ++i) worst = cdiff((uint32_t)a[i], (uint32_t)b[i]) > worst ? cdiff((uint32_t)a[i], (uint32_t)b[i]) : worst;
        printf("normal/premul vs ALPHA_OVER: worst diff %d\n", worst);
        if (worst > 2) { fails++; printf("FAIL: normal should match ALPHA_OVER\n"); }
        // NORMAL on straight dst == ALPHA_B
        for (int i = 0; i < W * H; ++i) { src[i] = (int)rnd(); a[i] = b[i] = (int)rnd(); }
        S.ALPHA_B(src.data(), a.data(), W, H, W, W);
        S.BLEND_MODE(src.data(), b.data(), W, H, W, W, SR2D_BM_NORMAL);
        worst = 0; for (int i = 0; i < W * H; ++i) worst = cdiff((uint32_t)a[i], (uint32_t)b[i]) > worst ? cdiff((uint32_t)a[i], (uint32_t)b[i]) : worst;
        printf("normal/straight vs ALPHA_B: worst diff %d\n", worst);
        if (worst > 2) { fails++; printf("FAIL: normal should match ALPHA_B\n"); }
    }
    // ---- 3. dissolve: fraction of source pixels ~ coverage, and only source or destination values
    {
        const int n = 256 * 64;
        std::vector<int> src(n, (int)0x80ff0000), dst(n, (int)0xff0000ff), d = dst;
        S.BLEND_MODE(src.data(), d.data(), 256, 64, 256, 256, SR2D_BM_DISSOLVE);
        int took = 0, bad = 0;
        for (int i = 0; i < n; ++i) { if (d[i] == (int)0xffff0000) took++; else if (d[i] != (int)0xff0000ff) bad++; }
        double frac = (double)took / n;
        printf("dissolve: %.3f of pixels took the source (expect ~0.502), %d foreign values\n", frac, bad);
        if (bad || frac < 0.45 || frac > 0.55) { fails++; printf("FAIL: dissolve\n"); }
    }
    // ---- 4. every entry point with an identity geometry equals BLEND_MODE
    for (int mode : { SR2D_BM_MULTIPLY, SR2D_BM_SCREEN, SR2D_BM_HUE, SR2D_BM_DIFFERENCE })
    for (int ctx = 0; ctx < 2; ++ctx)
    {
        const int SW = 24, SH = 20, DW = 40, DH = 36;
        const int opw = mode | (ctx ? SR2D_OP_DST_PREMUL : 0) | ((200 + 1) << SR2D_OP_OPACITY_SHIFT);
        std::vector<int> src(SW * SH), bg(DW * DH);
        for (auto& v : src) v = (int)rnd();
        for (auto& v : bg) { v = (int)rnd(); if (ctx) v = (int)premul((uint32_t)v); }
        // reference: BLEND_MODE at (5, 7)
        std::vector<int> ref = bg;
        S.BLEND_MODE(src.data(), ref.data() + 7 * DW + 5, SW, SH, SW, DW, opw);
        // warp nearest
        std::vector<int> d = bg; float q[8] = { 5, 7, 5 + SW, 7, 5 + SW, 7 + SH, 5, 7 + SH };
        S.DRAW_WARP(src.data(), SW, SH, d.data(), DW, 0, 0, DW, DH, q, 0, 0, opw, 0, 0);
        int worst = 0; for (int i = 0; i < DW * DH; ++i) { int c = cdiff((uint32_t)d[i], (uint32_t)ref[i]); if (c > worst) worst = c; }
        if (worst > 0) { fails++; printf("FAIL mode %d ctx %d: DRAW_WARP nearest differs from BLEND_MODE by %d\n", mode, ctx, worst); }
        // warp bilinear (identity: premultiplied round trip -> small differences allowed)
        d = bg; S.DRAW_WARP(src.data(), SW, SH, d.data(), DW, 0, 0, DW, DH, q, 0, 0, opw, SR2D_WARP_BILINEAR, 0);
        worst = 0; for (int i = 0; i < DW * DH; ++i) { int c = cdiff((uint32_t)d[i], (uint32_t)ref[i]); if (c > worst) worst = c; }
        if (worst > 4) { fails++; printf("FAIL mode %d ctx %d: DRAW_WARP bilinear differs from BLEND_MODE by %d\n", mode, ctx, worst); }
        // AVX2 warp == SSE2 warp
        std::vector<int> d2 = bg; A.DRAW_WARP(src.data(), SW, SH, d2.data(), DW, 0, 0, DW, DH, q, 0, 0, opw, SR2D_WARP_BILINEAR, 0);
        if (memcmp(d.data(), d2.data(), DW * DH * 4) != 0) { fails++; printf("FAIL mode %d: warp SSE2 != AVX2\n", mode); }
        // DRAW_FX with no stages
        d = bg; S.DRAW_FX(src.data(), SW, SH, d.data(), DW, 0, 0, DW, DH, q, 0, 0, opw, 0, 0, 0, 0);
        worst = 0; for (int i = 0; i < DW * DH; ++i) { int c = cdiff((uint32_t)d[i], (uint32_t)ref[i]); if (c > worst) worst = c; }
        if (worst > 2) { fails++; printf("FAIL mode %d ctx %d: DRAW_FX differs from BLEND_MODE by %d\n", mode, ctx, worst); }
        // DRAW_BLUR radius 0 == blit
        d = bg; S.DRAW_BLUR(src.data(), SW, SH, d.data(), DW, 0, 0, DW, DH, 5, 7, 0, opw, 0, 0, 256);
        worst = 0; for (int i = 0; i < DW * DH; ++i) { int c = cdiff((uint32_t)d[i], (uint32_t)ref[i]); if (c > worst) worst = c; }
        if (worst > 2) { fails++; printf("FAIL mode %d ctx %d: DRAW_BLUR r0 differs from BLEND_MODE by %d\n", mode, ctx, worst); }
        // constant colour: DRAW_POLY (no AA) rectangle == BLEND_MODE of a constant source
        const int col = (int)0xc0f04020;
        std::vector<int> csrc(SW * SH, col); ref = bg;
        S.BLEND_MODE(csrc.data(), ref.data() + 7 * DW + 5, SW, SH, SW, DW, opw);
        d = bg; float xy[8] = { 5, 7, 5 + SW, 7, 5 + SW, 7 + SH, 5, 7 + SH }; int cnt = 4;
        S.DRAW_POLY(d.data(), DW, 0, 0, DW, DH, xy, &cnt, 1, col, opw, 0, 0);
        worst = 0; for (int i = 0; i < DW * DH; ++i) { int c = cdiff((uint32_t)d[i], (uint32_t)ref[i]); if (c > worst) worst = c; }
        if (worst > 0) { fails++; printf("FAIL mode %d ctx %d: DRAW_POLY differs from BLEND_MODE by %d\n", mode, ctx, worst); }
        // FILL_MASK8 full mask
        std::vector<uint8_t> m(DW * DH, 0); for (int y = 7; y < 7 + SH; ++y) for (int x = 5; x < 5 + SW; ++x) m[y * DW + x] = 255;
        d = bg; S.FILL_MASK8(d.data(), DW, 0, 0, DW, DH, m.data(), DW, col, opw, 0);
        worst = 0; for (int i = 0; i < DW * DH; ++i) { int c = cdiff((uint32_t)d[i], (uint32_t)ref[i]); if (c > worst) worst = c; }
        if (worst > 0) { fails++; printf("FAIL mode %d ctx %d: FILL_MASK8 differs from BLEND_MODE by %d\n", mode, ctx, worst); }
        // DRAW_LINE2 horizontal line == one row
        d = bg; S.DRAW_LINE2(d.data(), DW, 0, 0, DW, DH, 5, 7, 5 + SW - 1, 7, col, opw, 0, 0, 0, 0);
        worst = 0; for (int x = 5; x < 5 + SW; ++x) { int c = cdiff((uint32_t)d[7 * DW + x], (uint32_t)ref[7 * DW + x]); if (c > worst) worst = c; }
        if (worst > 0) { fails++; printf("FAIL mode %d ctx %d: DRAW_LINE2 differs from BLEND_MODE by %d\n", mode, ctx, worst); }
        // MASK_BLEND_MODE with an all-ones mask
        std::vector<int> mk(SW * SH, 1); d = bg; ref = bg;
        S.BLEND_MODE(src.data(), ref.data() + 7 * DW + 5, SW, SH, SW, DW, opw);
        S.MASK_BLEND_MODE(src.data(), d.data() + 7 * DW + 5, mk.data(), SW, SH, 1, SW, DW, SW, 0, opw);
        if (memcmp(d.data(), ref.data(), DW * DH * 4) != 0) { fails++; printf("FAIL mode %d: MASK_BLEND_MODE differs\n", mode); }
    }
    // ---- 5. hostile: bad op codes are ignored, zero sizes
    {
        std::vector<int> a(16, 1), b(16, 2);
        S.BLEND_MODE(a.data(), b.data(), 4, 4, 4, 4, 5);          // not a blend mode -> untouched
        S.BLEND_MODE(a.data(), b.data(), 4, 4, 4, 4, 99);
        S.BLEND_MODE(a.data(), b.data(), 0, 0, 4, 4, SR2D_BM_MULTIPLY);
        S.BLEND_MODE(0, b.data(), 4, 4, 4, 4, SR2D_BM_MULTIPLY);
        for (int v : b) if (v != 2) { fails++; printf("FAIL: hostile BLEND_MODE wrote\n"); break; }
    }
    printf(fails ? "blendtest: %d FAILURES\n" : "blendtest: all passed\n", fails);
    return fails ? 1 : 0;
}
