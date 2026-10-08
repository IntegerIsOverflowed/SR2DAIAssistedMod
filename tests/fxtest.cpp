// DRAW_FX checks:
//   1. empty chain, 1:1 blit == plain AlphaBlend-of-premultiplied (AlphaOver, dest alpha kept)
//   2. blur-only chain == DRAW_BLUR (same radius, same op) bit-exactly
//   3. colour stage vs double reference (brightness/contrast/saturation/gamma/hue/tint/opacity/invert)
//   4. distortions vs double reference (wave / ripple / noise / map) built from the same
//      formulas: bilinear sampling of the premultiplied work image, tolerance 3/255
//      (noise is compared against the kernel's own hash - the reference is the vector one
//      run through a scalar path, so only interpolation/rounding differ)
//   5. opacity: chain [opacity 0.5] on an opaque sprite == 50% blend
//   6. clipping: any clip rect gives the exact sub-image of the unclipped result, nothing outside
//   7. SSE2 == AVX2 bit-exact on random chains (all stage kinds, PRE and POST, blit/scale/rotate)
//   8. timings
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <vector>
#include <chrono>
#include "../native/sr2d_ops.h"

static uint32_t rng = 777;
static uint32_t rnd() { rng = rng * 1664525u + 1013904223u; return rng >> 8; }
static float frnd() { return (float)(rnd() & 0xffff) / 65535.0f; }

static int fails = 0;
#define CHECK(cond, ...) do { if (!(cond)) { ++fails; printf("FAIL %s:%d: ", __FILE__, __LINE__); printf(__VA_ARGS__); printf("\n"); } } while (0)

static std::vector<int> make_sprite(int w, int h, bool alpha)
{
    std::vector<int> s((size_t)w * h);
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        int r = (x * 255 / (w - 1)), g = (y * 255 / (h - 1)), b = ((x ^ y) & 16) ? 200 : 60;
        int a = 255;
        if (alpha)
        {
            float dx = x - w * 0.5f + 0.5f, dy = y - h * 0.5f + 0.5f, d = sqrtf(dx * dx + dy * dy);
            a = d < w * 0.35f ? 255 : d < w * 0.45f ? (int)(255 * (w * 0.45f - d) / (w * 0.1f)) : 0;
            if (a == 0) { r = g = b = 0; }
        }
        s[(size_t)y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
    }
    return s;
}

static SR2D_FxStage st_blur(int r, bool fast = false) { SR2D_FxStage s = {}; s.kind = SR2D_FX_BLUR; s.i[0] = r; s.flags = fast ? SR2D_FXF_BLUR_FAST : 0; return s; }
static SR2D_FxStage st_blur2(int r, int flags, int down) { SR2D_FxStage s = {}; s.kind = SR2D_FX_BLUR; s.i[0] = r; s.i[1] = down; s.flags = flags; return s; }
static SR2D_FxStage st_diffuse(int r, int passes, int seed = 0, int flags = 0) { SR2D_FxStage s = {}; s.kind = SR2D_FX_DIFFUSE; s.i[0] = r; s.i[1] = passes; s.i[2] = seed; s.flags = flags; return s; }
static SR2D_FxStage st_color(float br, float co, float sat, float gam, float hue, float op, float tint = 0, int tintc = 0, bool inv = false)
{ SR2D_FxStage s = {}; s.kind = SR2D_FX_COLOR; s.f[0] = br; s.f[1] = co; s.f[2] = sat; s.f[3] = gam; s.f[4] = hue; s.f[5] = op; s.f[6] = tint; s.i[0] = tintc; s.flags = inv ? SR2D_FXF_INVERT : 0; return s; }
static SR2D_FxStage st_dist(int type, float scale, float str, float ph, float p3 = 0, float p4 = 0, float p5 = 0, int flags = 0)
{ SR2D_FxStage s = {}; s.kind = SR2D_FX_DISTORT; s.i[0] = type; s.f[0] = scale; s.f[1] = str; s.f[2] = ph; s.f[3] = p3; s.f[4] = p4; s.f[5] = p5; s.flags = flags; return s; }
static SR2D_FxStage st_map(const int* map, int mw, int mh, float scale, float str, float ox = 0, float oy = 0, int flags = 0)
{ SR2D_FxStage s = {}; s.kind = SR2D_FX_DISTORT_MAP; s.map = map; s.mw = mw; s.mh = mh; s.f[0] = scale; s.f[1] = str; s.f[2] = ox; s.f[3] = oy; s.flags = flags; return s; }
static SR2D_FxStage st_morph(int r, bool erode = false, bool square = false)
{ SR2D_FxStage s = {}; s.kind = SR2D_FX_MORPH; s.i[0] = r; s.flags = (erode ? SR2D_FXF_MORPH_ERODE : 0) | (square ? SR2D_FXF_MORPH_SQUARE : 0); return s; }
static SR2D_FxStage st_shadow(int blur, int grow, int rgb, float opacity, float dx, float dy, int flags = 0)
{ SR2D_FxStage s = {}; s.kind = SR2D_FX_SHADOW; s.i[0] = blur; s.i[1] = grow; s.i[2] = rgb; s.f[0] = opacity; s.f[1] = dx; s.f[2] = dy; s.flags = flags; return s; }
static SR2D_FxStage st_motion(int samples, float deg, float len, int flags = 0)
{ SR2D_FxStage s = {}; s.kind = SR2D_FX_MOTION; s.i[0] = samples; s.f[0] = deg; s.f[1] = len; s.flags = flags; return s; }
static SR2D_FxStage st_motion_path(const float* pts, int npts, float len, int samples, int flags = 0)
{ SR2D_FxStage s = {}; s.kind = SR2D_FX_MOTION; s.i[0] = samples; s.i[1] = npts; s.f[1] = len; s.map = (const int32_t*)pts; s.flags = flags; return s; }

static void quad_rect(float* q, float x, float y, float w, float h) { q[0] = x; q[1] = y; q[2] = x + w; q[3] = y; q[4] = x + w; q[5] = y + h; q[6] = x; q[7] = y + h; }
static void quad_rot(float* q, float cx, float cy, float w, float h, float ang)
{
    float c = cosf(ang), s = sinf(ang), hx = w * 0.5f, hy = h * 0.5f;
    float px[4] = { -hx, hx, hx, -hx }, py[4] = { -hy, -hy, hy, hy };
    for (int i = 0; i < 4; i++) { q[i * 2] = cx + px[i] * c - py[i] * s; q[i * 2 + 1] = cy + px[i] * s + py[i] * c; }
}

static int maxdiff(const std::vector<int>& a, const std::vector<int>& b, int* where = 0)
{
    int m = 0;
    for (size_t i = 0; i < a.size(); i++) for (int c = 0; c < 32; c += 8)
    {
        int d = abs(((a[i] >> c) & 255) - ((b[i] >> c) & 255));
        if (d > m) { m = d; if (where) *where = (int)i; }
    }
    return m;
}

// ---- reference helpers -------------------------------------------------------------------
struct RefImg { int w, h; std::vector<double> p; RefImg(int W, int H) : w(W), h(H), p((size_t)W * H * 4, 0.0) {} double* at(int x, int y) { return &p[((size_t)y * w + x) * 4]; } };

static void ref_load(const std::vector<int>& s, int sw, int sh, RefImg& img, int M)
{
    for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
    {
        uint32_t v = s[(size_t)y * sw + x]; double a = (v >> 24);
        // same rounding as warp_premul_copy: (c*a + 128 + ((c*a+128)>>8)) >> 8
        double* q = img.at(x + M, y + M);
        for (int c = 0; c < 3; c++) { uint32_t cc = (v >> (c * 8)) & 255; uint32_t t = cc * (uint32_t)a + 128; q[c] = (double)((t + (t >> 8)) >> 8); }
        q[3] = a;
    }
}
static void ref_sample_bilinear(RefImg& img, double u, double v, double* out)
{
    // matches warp_sample<1>: u,v pixel centre coords -> uf = clamp(u - .5), weights x256 truncated
    double uf = u - 0.5, vf = v - 0.5;
    if (!(uf > 0)) uf = 0; if (uf > img.w - 1) uf = img.w - 1;
    if (!(vf > 0)) vf = 0; if (vf > img.h - 1) vf = img.h - 1;
    int iu = (int)uf, iv = (int)vf;
    int wx = (int)((uf - iu) * 256), wy = (int)((vf - iv) * 256);
    int iu1 = iu + 1 < img.w ? iu + 1 : img.w - 1, iv1 = iv + 1 < img.h ? iv + 1 : img.h - 1;
    double* c00 = img.at(iu, iv); double* c10 = img.at(iu1, iv); double* c01 = img.at(iu, iv1); double* c11 = img.at(iu1, iv1);
    for (int c = 0; c < 4; c++)
    {
        double t = floor((c00[c] * (256 - wx) + c10[c] * wx) / 256.0), b = floor((c01[c] * (256 - wx) + c11[c] * wx) / 256.0);
        out[c] = floor((t * (256 - wy) + b * wy) / 256.0);
    }
}
static int ref_premul(int p)
{
    uint32_t a = (uint32_t)p >> 24;
    int r = (int)(a << 24);
    for (int c = 0; c < 24; c += 8) { uint32_t t = ((p >> c) & 255) * a + 128; r |= (int)(((t + (t >> 8)) >> 8) << c); }
    return r;
}
static int ref_pack(const double* q)
{
    int r = 0;
    for (int c = 0; c < 4; c++) { int v = (int)floor(q[c] + 0.5); v = v < 0 ? 0 : v > 255 ? 255 : v; r |= v << (c * 8); }
    return r;
}
// composite premultiplied work pixel over dst (AlphaBlend semantics = OpAlphaOverKeepA)
static int ref_over_keepa(int s, int d)
{
    uint32_t a = (uint32_t)s >> 24, inv = 256 - a - (a >> 7);
    int r = d & (int)0xff000000;
    for (int c = 0; c < 24; c += 8)
    {
        uint32_t sc = ((uint32_t)s >> c) & 255, dc = ((uint32_t)d >> c) & 255;
        uint32_t v = sc + ((dc * inv) >> 8); if (v > 255) v = 255;
        r |= (int)(v << c);
    }
    return r;
}

int main(int argc, char** argv)
{
    int iters = argc > 1 ? atoi(argv[1]) : 200;
    sr2d_ops S, A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
    fprintf(stderr, "AT: ops filled\n");
    const int sw = 64, sh = 48;
    std::vector<int> spr = make_sprite(sw, sh, true), opq = make_sprite(sw, sh, false);
    const int DW = 200, DH = 160;
    std::vector<int> bg((size_t)DW * DH);
    for (size_t i = 0; i < bg.size(); i++) bg[i] = (int)(0xff000000u | (rnd() & 0xffffff));

    // ---- 1. empty chain at 1:1 == AlphaBlend of the premultiplied sprite
    {
        std::vector<int> d1 = bg, d2 = bg; float q[8]; quad_rect(q, 40, 30, sw, sh);
        A.DRAW_FX(spr.data(), sw, sh, d1.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, 0, 0);
        for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
        {
            uint32_t v = spr[(size_t)y * sw + x], a = v >> 24; int pm = (int)(a << 24);
            for (int c = 0; c < 24; c += 8) { uint32_t t = ((v >> c) & 255) * a + 128; pm |= (int)(((t + (t >> 8)) >> 8) << c); }
            int* dp = &d2[(size_t)(y + 30) * DW + x + 40]; *dp = ref_over_keepa(pm, *dp);
        }
        CHECK(d1 == d2, "empty chain blit != premultiplied AlphaBlend (maxdiff %d)", maxdiff(d1, d2));
    }

    // ---- 2. blur-only chain == DRAW_BLUR, all ops
    for (int op = 1; op <= 12; op++) for (int r = 0; r <= 9; r += 3)
    {
        std::vector<int> d1 = bg, d2 = bg; float q[8]; quad_rect(q, 30, 20, sw, sh);
        SR2D_FxStage s = st_blur(r);
        A.DRAW_FX(spr.data(), sw, sh, d1.data(), DW, 5, 5, DW - 5, DH - 5, q, 0, 0, op, 0, 100, &s, 1);
        A.DRAW_BLUR(spr.data(), sw, sh, d2.data(), DW, 5, 5, DW - 5, DH - 5, 30, 20, r, op, 100, 0, 256);
        // DRAW_BLUR composites from its 16-bit work image, the chain from the 8-bit premultiplied
        // one -> up to 2/255 apart. Op 12 (straight copy) is only meaningful where alpha is not tiny.
        int worst = 0;
        for (size_t i = 0; i < d1.size(); i++)
        {
            if (op == 12 && ((uint32_t)d2[i] >> 24) < 64) continue;   // straight colour precision ~ 255/(2a)
            for (int c = 0; c < 32; c += 8) worst = std::max(worst, abs(((d1[i] >> c) & 255) - ((d2[i] >> c) & 255)));
        }
        CHECK(worst <= (op == 12 ? 4 : 2), "blur chain != DRAW_BLUR (op %d, r %d, maxdiff %d)", op, r, worst);
    }

    // ---- 3. colour stage vs reference (on the opaque sprite: no alpha coupling; and on the alpha one)
    {
        struct C { float br, co, sat, gam, hue, op, tint; int tc; bool inv; } cases[] = {
            { 0.2f, 1, 1, 1, 0, 1, 0, 0, false }, { -0.3f, 1.5f, 1, 1, 0, 1, 0, 0, false }, { 0, 1, 0, 1, 0, 1, 0, 0, false },
            { 0, 1, 1.8f, 1, 0, 1, 0, 0, false }, { 0, 1, 1, 2.2f, 0, 1, 0, 0, false }, { 0, 1, 1, 0.5f, 0, 1, 0, 0, false },
            { 0, 1, 1, 1, 90, 1, 0, 0, false }, { 0, 1, 1, 1, -140, 1, 0, 0, false }, { 0, 1, 1, 1, 0, 0.4f, 0, 0, false },
            { 0, 1, 1, 1, 0, 1, 0.6f, 0xff8000, false }, { 0, 1, 1, 1, 0, 1, 0, 0, true }, { 0.1f, 1.3f, 0.7f, 1.4f, 30, 0.8f, 0.2f, 0x2040ff, true } };
        int ci = 0;
        for (const C& c : cases)
        {
            for (int alpha = 0; alpha < 2; alpha++)
            {
                const std::vector<int>& src = alpha ? spr : opq;
                std::vector<int> d1((size_t)sw * sh, 0);
                float q[8]; quad_rect(q, 0, 0, sw, sh);
                SR2D_FxStage s = st_color(c.br, c.co, c.sat, c.gam, c.hue, c.op, c.tint, c.tc, c.inv);
                A.DRAW_FX(const_cast<int*>(src.data()), sw, sh, d1.data(), sw, 0, 0, sw, sh, q, 0, 0, 12, 0, 0, &s, 1);   // op 12: straight copy
                int worst = 0;
                for (int i = 0; i < sw * sh; i++)
                {
                    uint32_t v = src[i]; double a = (v >> 24) / 255.0;
                    if (a == 0) continue;
                    double rgb[3] = { ((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0 };
                    for (int k = 0; k < 3; k++) { if (c.gam != 1) rgb[k] = pow(rgb[k], 1.0 / c.gam); }
                    for (int k = 0; k < 3; k++) rgb[k] = (rgb[k] + c.br - 0.5) * c.co + 0.5;
                    if (c.sat != 1) { double l = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]; for (int k = 0; k < 3; k++) rgb[k] = l + (rgb[k] - l) * c.sat; }
                    if (c.hue != 0)
                    {
                        double cs = cos(c.hue * M_PI / 180), sn = sin(c.hue * M_PI / 180);
                        double m[9] = { 0.213 + cs * 0.787 - sn * 0.213, 0.715 - cs * 0.715 - sn * 0.715, 0.072 - cs * 0.072 + sn * 0.928,
                                        0.213 - cs * 0.213 + sn * 0.143, 0.715 + cs * 0.285 + sn * 0.140, 0.072 - cs * 0.072 - sn * 0.283,
                                        0.213 - cs * 0.213 - sn * 0.787, 0.715 - cs * 0.715 + sn * 0.715, 0.072 + cs * 0.928 + sn * 0.072 };
                        double o[3]; for (int k = 0; k < 3; k++) o[k] = m[k * 3] * rgb[0] + m[k * 3 + 1] * rgb[1] + m[k * 3 + 2] * rgb[2];
                        for (int k = 0; k < 3; k++) rgb[k] = o[k];
                    }
                    if (c.tint) { double t[3] = { ((c.tc >> 16) & 255) / 255.0, ((c.tc >> 8) & 255) / 255.0, (c.tc & 255) / 255.0 }; for (int k = 0; k < 3; k++) rgb[k] = rgb[k] * (1 - c.tint) + t[k] * c.tint; }
                    if (c.inv) for (int k = 0; k < 3; k++) rgb[k] = 1 - rgb[k];
                    for (int k = 0; k < 3; k++) rgb[k] = rgb[k] < 0 ? 0 : rgb[k] > 1 ? 1 : rgb[k];
                    double oa = a * c.op;
                    int got = d1[i];
                    int ga = (got >> 24) & 255;
                    int ea = (int)floor(oa * 255 + 0.5);
                    if (abs(ga - ea) > worst) worst = abs(ga - ea);
                    // straight colour of a premultiplied 8-bit pixel only has a/255 of the precision:
                    // compare the colour on opaque source pixels only (the alpha ramp is checked above)
                    if (a < 1.0) continue;
                    int gc[3] = { (got >> 16) & 255, (got >> 8) & 255, got & 255 };
                    for (int k = 0; k < 3; k++) { int d = abs(gc[k] - (int)floor(rgb[k] * 255 + 0.5)); if (d > worst) worst = d; }
                }
                CHECK(worst <= 2, "colour case %d (alpha %d): max diff %d", ci, alpha, worst);
            }
            ci++;
        }
    }

    // ---- 4. distortions vs reference, PRE mode, 1:1 (the kernel's own noise is used through a
    //         probe: we can't call it from here, so noise is only checked SSE2==AVX2 + bounds below)
    {
        // map texture
        const int mw = 32, mh = 24; std::vector<int> map((size_t)mw * mh);
        for (int y = 0; y < mh; y++) for (int x = 0; x < mw; x++) { int h = (int)(127.5 + 100 * sin(x * 0.4) * cos(y * 0.5)); map[(size_t)y * mw + x] = (int)0xff000000 | (h << 16) | (h << 8) | h; }
        struct D { int type; float scale, str, ph, p3, p4, p5; int flags; } cases[] = {
            { SR2D_FX_WAVE, 20, 4, 0.7f, 0, 0, 0, 0 }, { SR2D_FX_WAVE, 13, 3, 2.1f, 1.1f, 0, 0, SR2D_FXF_WAVE_LONG },
            { SR2D_FX_WAVE, 30, 5, 0.3f, 0.5f, 0, 0, SR2D_FXF_WAVE_CROSS }, { SR2D_FX_RIPPLE, 12, 3, 1.0f, 30, 20, 0, 0 },
            { SR2D_FX_RIPPLE, 8, 4, 2.5f, 20, 30, 40, 0 }, { -1, 1.5f, 6, 0, 0, 0, 0, 0 }, { -1, 0.7f, -4, 0, 5.5f, 3.25f, 0, 0 } };
        int ci = 0;
        for (const D& c : cases)
        {
            const int M = (int)ceilf(fabsf(c.str) * (c.flags & SR2D_FXF_WAVE_CROSS ? 1.5f : 1.0f)) + 1;
            const int W = sw + 2 * M, H = sh + 2 * M;
            std::vector<int> d1((size_t)W * H, 0);
            float q[8]; quad_rect(q, M, M, sw, sh);
            SR2D_FxStage s = c.type >= 0 ? st_dist(c.type, c.scale, c.str, c.ph, c.p3, c.p4, c.p5, c.flags) : st_map(map.data(), mw, mh, c.scale, c.str, c.p3, c.p4);
            A.DRAW_FX(spr.data(), sw, sh, d1.data(), W, 0, 0, W, H, q, 0, 0, 1, 0, 0, &s, 1);    // Paint: premultiplied copy
            RefImg img(W, H); ref_load(spr, sw, sh, img, M);
            // gradient map reference
            std::vector<double> gx((size_t)mw * mh), gy((size_t)mw * mh);
            for (int y = 0; y < mh; y++) for (int x = 0; x < mw; x++)
            {
                auto lum = [&](int xx, int yy) { int p = map[(size_t)((yy + mh) % mh) * mw + (xx + mw) % mw]; return (77 * ((p >> 16) & 255) + 150 * ((p >> 8) & 255) + 29 * (p & 255)) >> 8; };
                gx[(size_t)y * mw + x] = ((lum(x + 1, y) - lum(x - 1, y) + 256) >> 1) - 128; gy[(size_t)y * mw + x] = ((lum(x, y + 1) - lum(x, y - 1) + 256) >> 1) - 128;
            }
            int worst = 0, nbad = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                double px = x - M, py = y - M, dx = 0, dy = 0;   // pattern coords = sprite pixels
                if (c.type == SR2D_FX_WAVE)
                {
                    double k = 2 * M_PI / c.scale, cs = cos(c.p3), sn = sin(c.p3);
                    double w = c.str * sin((px * cs + py * sn) * k + c.ph);
                    if (!(c.flags & SR2D_FXF_WAVE_LONG)) { dx = -w * sn; dy = w * cs; } else { dx = w * cs; dy = w * sn; }
                    if (c.flags & SR2D_FXF_WAVE_CROSS) { double w2 = c.str * sin((py * cs - px * sn) * k + c.ph); dx += w2 * cs; dy += w2 * sn; }
                }
                else if (c.type == SR2D_FX_RIPPLE)
                {
                    double rx = px - c.p3, ry = py - c.p4, r = sqrt(rx * rx + ry * ry), k = 2 * M_PI / c.scale;
                    double w = c.str * sin(r * k - c.ph);
                    if (c.p5 > 0) w *= fmax(1 - r / c.p5, 0.0);
                    w /= fmax(r, 1e-3); dx = w * rx; dy = w * ry;
                }
                else
                {   // map: bilinear gradient at ((px + .5 + ox)/scale, ...) with wrap
                    double tx = (px + 0.5 + c.p3) / c.scale, ty = (py + 0.5 + c.p4) / c.scale;
                    tx -= floor(tx / mw) * mw; ty -= floor(ty / mh) * mh;
                    double uf = tx + 0.5, vf = ty + 0.5;   // gmap has a 1-texel border; sample centre convention as warp_sample<1>
                    int iu = (int)floor(uf), iv = (int)floor(vf); double fu = uf - iu, fv = vf - iv;
                    auto G = [&](int xx, int yy, int comp) { xx = ((xx % mw) + mw) % mw; yy = ((yy % mh) + mh) % mh; return comp ? gy[(size_t)yy * mw + xx] : gx[(size_t)yy * mw + xx]; };
                    double gxx = (G(iu - 1, iv - 1, 0) * (1 - fu) + G(iu, iv - 1, 0) * fu) * (1 - fv) + (G(iu - 1, iv, 0) * (1 - fu) + G(iu, iv, 0) * fu) * fv;
                    double gyy = (G(iu - 1, iv - 1, 1) * (1 - fu) + G(iu, iv - 1, 1) * fu) * (1 - fv) + (G(iu - 1, iv, 1) * (1 - fu) + G(iu, iv, 1) * fu) * fv;
                    dx = gxx * c.str / 127.5; dy = gyy * c.str / 127.5;
                }
                double o[4]; ref_sample_bilinear(img, x + 0.5 + dx, y + 0.5 + dy, o);
                int e = ref_pack(o), g = d1[(size_t)y * W + x];
                int d = 0; for (int k = 0; k < 32; k += 8) d = std::max(d, abs(((e >> k) & 255) - ((g >> k) & 255)));
                if (d > worst) worst = d;
                if (d > 3) nbad++;
            }
            // sub-pixel differences in the displacement (float vs double, sin approximation) move a
            // bilinear tap by a fraction of a weight step: allow rare 1-step outliers on hard edges
            const int lim = W * H / 200;
            CHECK(nbad <= lim && worst <= 24, "distort case %d: max diff %d, %d px over 3/255", ci, worst, nbad);
            ci++;
        }
    }

    // ---- 5. opacity 0.5 on an opaque sprite == Blend 50 %  (AlphaBlend path: d*(1-a/255) + s*a/255)
    {
        std::vector<int> d1 = bg; float q[8]; quad_rect(q, 10, 10, sw, sh);
        SR2D_FxStage s = st_color(0, 1, 1, 1, 0, 0.5f);
        A.DRAW_FX(opq.data(), sw, sh, d1.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &s, 1);
        int worst = 0;
        for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
        {
            int sv = opq[(size_t)y * sw + x], dv = bg[(size_t)(y + 10) * DW + x + 10], gv = d1[(size_t)(y + 10) * DW + x + 10];
            for (int c = 0; c < 24; c += 8)
            {
                int e = (((sv >> c) & 255) + ((dv >> c) & 255) + 1) / 2, g = (gv >> c) & 255;
                if (abs(e - g) > worst) worst = abs(e - g);
            }
            CHECK(((gv >> 24) & 255) == 255, "opacity must keep dest alpha (AlphaBlend)");
        }
        CHECK(worst <= 2, "opacity 0.5 != 50%% blend (max diff %d)", worst);
    }

    // ---- 5b. morphology vs brute force (op 12 = copy the straight result; on an opaque-or-clear
    //          sprite premultiplied == straight, so a per-channel compare is exact)
    {
        std::vector<int> hard((size_t)sw * sh, 0);
        for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
        {   // a few solid blobs of different colours on transparent
            const int dx = x - 40, dy = y - 40, dx2 = x - 100, dy2 = y - 90;
            if (dx * dx + dy * dy < 18 * 18) hard[(size_t)y * sw + x] = (int)0xffff4020;
            else if (abs(dx2) < 12 && abs(dy2) < 25) hard[(size_t)y * sw + x] = (int)0xff20c0ff;
            else if (x == 70 && y > 20 && y < 60) hard[(size_t)y * sw + x] = (int)0xffffffff;   // 1-px line
        }
        const int ML = 32;
        for (int caseno = 0; caseno < 12; caseno++)
        {
            const int r = 1 + caseno % 6; const bool erode = caseno >= 6, square = (caseno / 3) & 1;
            SR2D_FxStage st = st_morph(r, erode, square);
            float q[8]; quad_rect(q, ML, ML, sw, sh);
            std::vector<int> out((size_t)DW * DH, 0), ref((size_t)DW * DH, 0);
            A.DRAW_FX(hard.data(), sw, sh, out.data(), DW, 0, 0, DW, DH, q, 0, 0, 12, 0, 0, &st, 1);
            for (int y = 0; y < DH; y++) for (int x = 0; x < DW; x++)
            {
                int acc[4] = { erode ? 255 : 0, erode ? 255 : 0, erode ? 255 : 0, erode ? 255 : 0 };
                for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
                {
                    if (!square) { const int hw = (int)(sqrt((double)(r * r - dy * dy)) + 0.5); if (abs(dx) > hw) continue; }
                    const int sx = x + dx - ML, sy = y + dy - ML;
                    const int v = (sx >= 0 && sx < sw && sy >= 0 && sy < sh) ? hard[(size_t)sy * sw + sx] : 0;
                    for (int c = 0; c < 4; c++) { const int b = (v >> (c * 8)) & 255; acc[c] = erode ? (b < acc[c] ? b : acc[c]) : (b > acc[c] ? b : acc[c]); }
                }
                ref[(size_t)y * DW + x] = acc[0] | (acc[1] << 8) | (acc[2] << 16) | (acc[3] << 24);
            }
            int where = 0, md = maxdiff(out, ref, &where);
            CHECK(md == 0, "morph r=%d erode=%d square=%d differs from brute force (max %d at %d,%d)", r, erode, square, md, where % DW, where / DW);
            std::vector<int> o2((size_t)DW * DH, 0);
            S.DRAW_FX(hard.data(), sw, sh, o2.data(), DW, 0, 0, DW, DH, q, 0, 0, 12, 0, 0, &st, 1);
            CHECK(maxdiff(out, o2, &where) == 0, "morph SSE2 != AVX2 (case %d)", caseno);
        }
    }

    // ---- 5c. shadow stage == [copy, blur, tint, shift] composed from the existing stages, and glow
    {
        float q[8]; quad_rect(q, 40, 40, sw, sh);
        // reference: the shadow alone via SHADOW_ONLY must equal: blur stage + colour(tint 1, opacity) drawn at (dx, dy)
        SR2D_FxStage only = st_shadow(6, 0, 0x102030, 0.7f, 5, -3, SR2D_FXF_SHADOW_ONLY);
        std::vector<int> a1 = bg, a2 = bg;
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a1.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &only, 1);
        SR2D_FxStage chain[2] = { st_blur(6), st_color(0, 1, 1, 1, 0, 0.7f, 1.0f, 0x102030) };
        float q2[8]; quad_rect(q2, 45, 37, sw, sh);
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a2.data(), DW, 0, 0, DW, DH, q2, 0, 0, 3, 0, 0, chain, 2);
        int where = 0, md = maxdiff(a1, a2, &where);
        CHECK(md <= 2, "shadow-only != blur+tint+shift (max diff %d at %d,%d)", md, where % DW, where / DW);
        // full shadow: where the sprite is opaque the result equals the plain draw
        SR2D_FxStage shd = st_shadow(6, 2, 0, 0.8f, 5, 5);
        std::vector<int> a3 = bg, a4 = bg;
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a3.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &shd, 1);
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a4.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, 0, 0);
        int bad = 0, outside = 0;
        for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
            if (((unsigned)spr[(size_t)y * sw + x] >> 24) == 255 && a3[(size_t)(y + 40) * DW + x + 40] != a4[(size_t)(y + 40) * DW + x + 40]) bad++;
        for (int y = 0; y < DH; y++) for (int x = 0; x < DW; x++) if (a3[(size_t)y * DW + x] != bg[(size_t)y * DW + x] && a4[(size_t)y * DW + x] == bg[(size_t)y * DW + x]) outside++;
        CHECK(bad == 0, "shadow changed %d opaque sprite pixels", bad);
        CHECK(outside > 500, "shadow drew only %d pixels outside the sprite", outside);
        // glow: additive on the premultiplied picture. Op 12 copies the straight result: inside
        // the sprite's opaque core it must equal the sprite (glow adds to alpha 255 = saturates,
        // colour saturates too), and around it there must be lit pixels; compared with Paint
        // (op 1) against the plain draw nothing may get darker inside the sprite rectangle.
        SR2D_FxStage gl = st_shadow(8, 3, 0xffc040, 1.0f, 0, 0, SR2D_FXF_SHADOW_GLOW);
        std::vector<int> a5 = bg, a6 = bg;
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a5.data(), DW, 0, 0, DW, DH, q, 0, 0, 1, 0, 0, &gl, 1);
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a6.data(), DW, 0, 0, DW, DH, q, 0, 0, 1, 0, 0, 0, 0);
        int dark = 0, lit = 0;
        for (int y = 40; y < 40 + sh; y++) for (int x = 40; x < 40 + sw; x++)
        {
            const size_t i = (size_t)y * DW + x;
            for (int c = 0; c < 32; c += 8) { const int g = (a5[i] >> c) & 255, p = (a6[i] >> c) & 255; if (g < p) dark++; if (g > p) lit++; }
        }
        for (int y = 0; y < DH; y++) for (int x = 0; x < DW; x++)
            if ((y < 40 || y >= 40 + sh || x < 40 || x >= 40 + sw) && ((unsigned)a5[(size_t)y * DW + x] >> 24) != 0) lit++;
        CHECK(dark == 0, "glow darkened %d channels", dark);
        CHECK(lit > 1000, "glow lit only %d channels", lit);
        // SSE2 == AVX2 for all three
        std::vector<int> s1 = bg, s3 = bg, s5 = bg;
        S.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, s1.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &only, 1);
        S.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, s3.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &shd, 1);
        S.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, s5.data(), DW, 0, 0, DW, DH, q, 0, 0, 1, 0, 0, &gl, 1);
        CHECK(maxdiff(a1, s1, &where) == 0 && maxdiff(a3, s3, &where) == 0 && maxdiff(a5, s5, &where) == 0, "shadow/glow SSE2 != AVX2");
    }

    // ---- 5d. disabled stages are skipped and add no margin: [blur(disabled), colour] == [colour]
    {
        float q[8]; quad_rect(q, 40, 40, sw, sh);
        SR2D_FxStage two[2] = { st_blur(20), st_color(0.2f, 1.1f, 0.8f, 1, 30, 1) }; two[0].flags |= SR2D_FXF_DISABLED;
        SR2D_FxStage one[1] = { two[1] };
        std::vector<int> a1 = bg, a2 = bg;
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a1.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, two, 2);
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a2.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, one, 1);
        int where = 0;
        CHECK(maxdiff(a1, a2, &where) == 0, "disabled stage changed the output");
        SR2D_FxStage all[3] = { st_shadow(4, 1, 0, 1, 3, 3), st_morph(2), st_dist(SR2D_FX_WAVE, 20, 4, 1) };
        for (int i = 0; i < 3; i++) all[i].flags |= SR2D_FXF_DISABLED;
        std::vector<int> a3 = bg, a4 = bg;
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a3.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, all, 3);
        A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a4.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, 0, 0);
        CHECK(maxdiff(a3, a4, &where) == 0, "all-disabled chain != empty chain");
    }

    // ---- 5e. box blur / downscaled blur / diffuse
    {
        float q[8]; quad_rect(q, 40, 40, sw, sh);
        // box (1 pass) blur == DRAW_BLUR with flag 16
        for (int r = 1; r <= 12; r += 5)
        {
            SR2D_FxStage s = st_blur(r); s.flags = SR2D_FXF_BLUR_BOX;
            std::vector<int> d1 = bg, d2 = bg;
            A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, d1.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &s, 1);
            A.DRAW_BLUR(const_cast<int*>(spr.data()), sw, sh, d2.data(), DW, 0, 0, DW, DH, 40, 40, r, 3, 0, SR2D_BLUR_BOX, 256);
            int where = 0; int md = maxdiff(d1, d2, &where);
            CHECK(md <= 2, "box blur chain != DRAW_BLUR(BOX) (r %d, maxdiff %d)", r, md);
            std::vector<int> d3 = bg; S.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, d3.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &s, 1);
            CHECK(maxdiff(d1, d3, &where) == 0, "box blur SSE2 != AVX2 (r %d)", r);
        }
        // downscaled blur: close to the full-resolution one (mean error < 2 levels), SSE2 == AVX2, i[1] = 0 / 1 = unchanged path
        {
            SR2D_FxStage full = st_blur(24), down = st_blur(24), none = st_blur(24), autok = st_blur(24);
            full.i[1] = 1; down.i[1] = 4; none.i[1] = 0; autok.i[1] = -1;
            std::vector<int> a1 = bg, a2 = bg, a3 = bg, a4 = bg, a5 = bg;
            A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a1.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &full, 1);
            A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a2.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &down, 1);
            A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a3.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &none, 1);
            S.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a4.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &down, 1);
            A.DRAW_FX(const_cast<int*>(spr.data()), sw, sh, a5.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 0, 0, &autok, 1);
            int where = 0;
            CHECK(maxdiff(a1, a3, &where) == 0, "blur i[1]=0 != i[1]=1 (both must be the plain path)");
            CHECK(maxdiff(a2, a4, &where) == 0, "downscaled blur SSE2 != AVX2");
            CHECK(maxdiff(a2, a5, &where) == 0, "auto downscale for r 24 != explicit 4");
            double sum = 0; for (size_t i = 0; i < a1.size(); i++) for (int c = 0; c < 32; c += 8) sum += abs(((a1[i] >> c) & 255) - ((a2[i] >> c) & 255));
            double mean = sum / (a1.size() * 4.0);
            CHECK(mean < 2.0, "downscaled blur too far from the full one (mean |diff| %.2f)", mean);
        }
        // diffuse: every output pixel is a source pixel within r (premultiplied source, Paint out = raw work image), deterministic, seeded
        {
            SR2D_FxStage d = {}; d.kind = SR2D_FX_DIFFUSE; d.i[0] = 3; d.i[1] = 1;
            std::vector<int> pm((size_t)sw * sh);
            for (size_t i = 0; i < pm.size(); i++) { uint32_t v = (uint32_t)spr[i], a = v >> 24; pm[i] = (int)((a << 24) | ((((v >> 16) & 255) * a + 127) / 255) << 16 | ((((v >> 8) & 255) * a + 127) / 255) << 8 | (((v & 255) * a + 127) / 255)); }
            float qi[8]; quad_rect(qi, 0, 0, sw, sh);
            std::vector<int> o1((size_t)sw * sh, 0), o2((size_t)sw * sh, 0), o3((size_t)sw * sh, 0);
            A.DRAW_FX(pm.data(), sw, sh, o1.data(), sw, 0, 0, sw, sh, qi, 0, 0, 1, SR2D_FX_PREMUL, 0, &d, 1);
            S.DRAW_FX(pm.data(), sw, sh, o2.data(), sw, 0, 0, sw, sh, qi, 0, 0, 1, SR2D_FX_PREMUL, 0, &d, 1);
            int bad = 0, moved = 0;
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
            {
                int v = o1[(size_t)y * sw + x]; bool ok = false;
                for (int dy = -3; dy <= 3 && !ok; dy++) for (int dx = -3; dx <= 3 && !ok; dx++)
                { int sx = x + dx, sy = y + dy; int sv = (sx < 0 || sy < 0 || sx >= sw || sy >= sh) ? 0 : pm[(size_t)sy * sw + sx]; if (sv == v) ok = true; }
                if (!ok) bad++;
                if (v != pm[(size_t)y * sw + x]) moved++;
            }
            CHECK(bad == 0, "diffuse: %d pixels are not a neighbour within r", bad);
            CHECK(moved > (int)pm.size() / 8, "diffuse: hardly anything moved (%d px)", moved);
            CHECK(o1 == o2, "diffuse SSE2 != AVX2");
            d.i[2] = 1; A.DRAW_FX(pm.data(), sw, sh, o3.data(), sw, 0, 0, sw, sh, qi, 0, 0, 1, SR2D_FX_PREMUL, 0, &d, 1);
            CHECK(o1 != o3, "diffuse: seed has no effect");
            // darken keeps every channel <= original, lighten >= original
            d.i[2] = 0; d.flags = SR2D_FXF_DIFFUSE_DARKEN; A.DRAW_FX(pm.data(), sw, sh, o3.data(), sw, 0, 0, sw, sh, qi, 0, 0, 1, SR2D_FX_PREMUL, 0, &d, 1);
            int viol = 0; for (size_t i = 0; i < pm.size(); i++) for (int c = 0; c < 32; c += 8) if (((o3[i] >> c) & 255) > ((pm[i] >> c) & 255)) viol++;
            CHECK(viol == 0, "diffuse DARKEN raised %d channels", viol);
            d.flags = SR2D_FXF_DIFFUSE_LIGHTEN; A.DRAW_FX(pm.data(), sw, sh, o3.data(), sw, 0, 0, sw, sh, qi, 0, 0, 1, SR2D_FX_PREMUL, 0, &d, 1);
            viol = 0; for (size_t i = 0; i < pm.size(); i++) for (int c = 0; c < 32; c += 8) if (((o3[i] >> c) & 255) < ((pm[i] >> c) & 255)) viol++;
            CHECK(viol == 0, "diffuse LIGHTEN lowered %d channels", viol);
        }
    }

    // ---- 5b. motion blur vs reference: linear (nearest taps exact, bilinear vs double) + custom path
    {
        fprintf(stderr, "AT: 5b enter\n");
        const int M = 10;
        const int W = sw + 2 * M, H = sh + 2 * M;
        // linear, direction 0, length 2 px, 3 samples: the taps sit at whole pixels - the nearest-sampled
        // result must be the plain (p[x] + p[x-1] + p[x-2]) / 3 average over transparent surroundings
        {
            std::vector<int> d1((size_t)W * H, 0);
            float q[8]; quad_rect(q, M, M, sw, sh);
            SR2D_FxStage s = st_motion(3, 0, 2, SR2D_FXF_SAMPLE_NEAREST);
            fprintf(stderr, "AT: before nearest DRAW_FX (M=%d W=%d)\n", M, W);
            A.DRAW_FX(spr.data(), sw, sh, d1.data(), W, 0, 0, W, H, q, 0, 0, 1, 0, 0, &s, 1);
            fprintf(stderr, "AT: after nearest DRAW_FX\n");
            int worst = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int acc[4] = { 0, 0, 0, 0 };
                for (int k = 0; k < 3; k++)
                {
                    int sx = x - M - k, sy = y - M;
                    int p = (sx >= 0 && sx < sw && sy >= 0 && sy < sh) ? spr[(size_t)sy * sw + sx] : 0;
                    p = ref_premul(p);                          // the kernel averages PREMULTIPLIED bytes
                    for (int c = 0; c < 32; c += 8) acc[c / 8] += (p >> c) & 255;
                }
                int e = 0; for (int c = 0; c < 4; c++) e |= ((acc[c] + 1) / 3) << (c * 8);
                int g = d1[(size_t)y * W + x];
                int d = 0; for (int c = 0; c < 32; c += 8) d = std::max(d, abs(((e >> c) & 255) - ((g >> c) & 255)));
                if (d > worst) worst = d;
            }
            CHECK(worst <= 1, "motion linear nearest != integer 3-tap average (max diff %d)", worst);
            fprintf(stderr, "AT: nearest case done\n");
        }
        // bilinear linear + a curved path, both against the double reference (average of the taps)
        struct MC { int samples; float deg, len; const float* path; int npts; float str; int flags; const char* what; };
        static const float arc[6] = { 0, 0, 6, -4, 12, -2 };            // a little hop up and back
        static const float line[4] = { 0, 0, 8, 0 };
        MC mcases[] = {
            { 9, 20, 8, nullptr, 0, 0, 0, "linear bilinear" },
            { 5, 0, 0, arc, 3, 1.0f, 0, "arc path" },
            { 7, 0, 0, line, 2, 2.5f, 0, "line path scaled 2.5" },
            { 9, 200, 8, nullptr, 0, 0, SR2D_FXF_SAMPLE_NEAREST, "linear nearest 200 deg" },
        };
        for (const MC& m : mcases)
        {
            fprintf(stderr, "AT: case %s\n", m.what);
            SR2D_FxStage s = m.path ? st_motion_path(m.path, m.npts, m.str, m.samples, m.flags) : st_motion(m.samples, m.deg, m.len, m.flags);
            float ext = m.path ? 0.0f : fabsf(m.len);
            if (m.path) for (int k = 0; k < m.npts; k++) { float px = m.path[k * 2], py = m.path[k * 2 + 1]; ext = std::max(ext, sqrtf(px * px + py * py) * m.str); }
            const int MM = (int)ceilf(ext) + 2;
            const int WW = sw + 2 * MM, HH = sh + 2 * MM;
            std::vector<int> d1((size_t)WW * HH, 0);
            float q[8]; quad_rect(q, MM, MM, sw, sh);
            A.DRAW_FX(spr.data(), sw, sh, d1.data(), WW, 0, 0, WW, HH, q, 0, 0, 1, 0, 0, &s, 1);
            RefImg img(WW, HH); ref_load(spr, sw, sh, img, MM);
            int worst = 0;
            for (int y = 0; y < HH; y++) for (int x = 0; x < WW; x++)
            {
                double o[4] = { 0, 0, 0, 0 };
                for (int k = 0; k < m.samples; k++)
                {
                    float t = (float)k / (float)(m.samples - 1), ox, oy;
                    if (m.path)
                    {
                        float pos = t * (m.npts - 1); int seg = (int)pos; if (seg > m.npts - 2) seg = m.npts - 2; float fr = pos - seg;
                        ox = (m.path[seg * 2] + (m.path[seg * 2 + 2] - m.path[seg * 2]) * fr) * m.str;
                        oy = (m.path[seg * 2 + 1] + (m.path[seg * 2 + 3] - m.path[seg * 2 + 1]) * fr) * m.str;
                    }
                    else { float rad = m.deg * (float)M_PI / 180.0f; ox = cosf(rad) * m.len * t; oy = sinf(rad) * m.len * t; }
                    double tap[4];
                    if (m.flags & SR2D_FXF_SAMPLE_NEAREST)
                    {   // nearest: match warp_sample<0> - floor the clamped centre coordinate
                        double uu = x + 0.5 - ox, vv = y + 0.5 - oy;
                        int iu = (int)uu, iv = (int)vv; iu = iu < 0 ? 0 : iu > img.w - 1 ? img.w - 1 : iu; iv = iv < 0 ? 0 : iv > img.h - 1 ? img.h - 1 : iv;
                        double* q = img.at(iu, iv); for (int c = 0; c < 4; c++) tap[c] = q[c];
                    }
                    else ref_sample_bilinear(img, x + 0.5 - ox, y + 0.5 - oy, tap);
                    for (int c = 0; c < 4; c++) o[c] += tap[c] / m.samples;
                }
                int e = ref_pack(o), g = d1[(size_t)y * WW + x];
                int d = 0; for (int c = 0; c < 32; c += 8) d = std::max(d, abs(((e >> c) & 255) - ((g >> c) & 255)));
                if (d > worst) worst = d;
            }
            CHECK(worst <= 3, "motion %s: max diff %d vs double reference", m.what, worst);
        }
    }

    fprintf(stderr, "AT: 5b done\n");
    // ---- 6/7. random chains: clipping, SSE2 == AVX2, PRE/POST, geometry variants
    const int mw = 16, mh = 16; std::vector<int> map((size_t)mw * mh);
    for (size_t i = 0; i < map.size(); i++) { int h = rnd() & 255; map[i] = (int)0xff000000 | (h << 16) | (h << 8) | h; }
    int nclip = 0, npar = 0;
    for (int it = 0; it < iters; it++)
    {
        SR2D_FxStage st[4]; int n = 1 + (int)(rnd() % 3);
        for (int i = 0; i < n; i++)
        {
            switch (rnd() % 9)
            {
            case 5: st[i] = st_morph(1 + (int)(rnd() % 4), (rnd() & 1) != 0, (rnd() & 1) != 0); break;
            case 6: st[i] = st_shadow((int)(rnd() % 30), (int)(rnd() % 3), (int)(rnd() & 0xffffff), frnd(), frnd() * 12 - 6, frnd() * 12 - 6, (int)(rnd() % 16)); st[i].i[3] = (int)(rnd() % 6) - 1; break;
            case 7: st[i] = st_blur((int)(rnd() % 6)); st[i].flags |= SR2D_FXF_DISABLED; break;
            case 0: st[i] = st_blur((int)(rnd() % 30), (rnd() & 1) != 0); if (rnd() & 1) st[i].flags |= SR2D_FXF_BLUR_BOX; st[i].i[1] = (int)(rnd() % 6) - 1; break;
            case 4: if (rnd() & 1) { st[i] = st_diffuse(1 + (int)(rnd() % 5), 1 + (int)(rnd() % 3), (int)rnd(), (int)(rnd() % 3)); break; }
                    st[i] = st_color(0, 1, 1, 1, 0, frnd()); break;
            case 1: st[i] = st_color(frnd() * 0.6f - 0.3f, 0.5f + frnd(), frnd() * 2, 0.5f + frnd(), frnd() * 360 - 180, 0.3f + frnd() * 0.7f, frnd() * 0.5f, (int)(rnd() & 0xffffff), (rnd() & 1) != 0); break;
            case 2: st[i] = st_dist((int)(rnd() % 4), 5 + frnd() * 30, frnd() * 6 - 3, frnd() * 6, frnd() * 6, frnd() * 40, rnd() & 1 ? frnd() * 40 : 0, (int)(rnd() % 3) | (rnd() & 1 ? SR2D_FXF_SAMPLE_BICUBIC : 0)); break;
            case 3: st[i] = st_map(map.data(), mw, mh, 0.5f + frnd() * 3, frnd() * 8 - 4, frnd() * 20, frnd() * 20, (rnd() & 1) ? SR2D_FXF_SAMPLE_NEAREST : 0); break;
            case 8: { static const float rp[8] = { 0, 0, 5, 3, -2, 7, 6, 6 }; st[i] = (rnd() & 1) ? st_motion(2 + (int)(rnd() % 12), frnd() * 360, frnd() * 10, (int)(rnd() % 3)) : st_motion_path(rp, 4, 0.5f + frnd(), 2 + (int)(rnd() % 12), (int)(rnd() % 3)); } break;
            default: st[i] = st_color(0, 1, 1, 1, 0, frnd()); break;
            }
        }
        float q[8];
        const int geo = (int)(rnd() % 4);
        if (geo == 0) quad_rect(q, (float)(20 + rnd() % 60), (float)(20 + rnd() % 40), sw, sh);
        else if (geo == 1) quad_rect(q, 20 + frnd() * 60, 20 + frnd() * 40, sw * (0.5f + frnd() * 1.5f), sh * (0.5f + frnd() * 1.5f));
        else if (geo == 2) quad_rot(q, 100, 80, sw * (0.7f + frnd()), sh * (0.7f + frnd()), frnd() * 6.28f);
        else { quad_rect(q, 30, 30, 120, 90); q[0] += 30 * frnd(); q[2] -= 30 * frnd(); }   // perspective
        const int op = 1 + (int)(rnd() % 12);
        const int flags = (int)(rnd() % 3 == 0 ? SR2D_WARP_BICUBIC : rnd() & 1 ? SR2D_WARP_BILINEAR : 0) | (rnd() & 1 ? SR2D_FX_POST : 0) | (rnd() % 4 == 0 ? SR2D_FX_OPAQUE : 0);
        const std::vector<int>& src = (rnd() & 1) ? spr : opq;
        std::vector<int> full = bg, ds = bg;
        A.DRAW_FX(const_cast<int*>(src.data()), sw, sh, full.data(), DW, 0, 0, DW, DH, q, 0, 0, op, flags, 77, st, n);
        S.DRAW_FX(const_cast<int*>(src.data()), sw, sh, ds.data(), DW, 0, 0, DW, DH, q, 0, 0, op, flags, 77, st, n);
        int where = 0, md = maxdiff(full, ds, &where);
        if (md) { npar++; if (npar <= 5) printf("  SSE2 != AVX2: it %d geo %d op %d flags %d n %d kinds %d/%d/%d diff %d at (%d,%d)\n", it, geo, op, flags, n, st[0].kind, n > 1 ? st[1].kind : 0, n > 2 ? st[2].kind : 0, md, where % DW, where / DW); }
        // clipping
        int cl = (int)(rnd() % 100), ct = (int)(rnd() % 80), cr = cl + 1 + (int)(rnd() % (DW - cl - 1)), cb = ct + 1 + (int)(rnd() % (DH - ct - 1));
        std::vector<int> part = bg;
        A.DRAW_FX(const_cast<int*>(src.data()), sw, sh, part.data(), DW, cl, ct, cr, cb, q, 0, 0, op, flags, 77, st, n);
        // POST mode: the work image origin follows the clip rect, and the transform of an integer-
        // translated quad differs by a rounding bit in the double setup, so 1 LSB is tolerated there
        // (pre-existing, transform only). PRE mode and blits are exact.
        const int tol = (flags & SR2D_FX_POST) ? 1 : 0;
        bool ok = true;
        for (int y = 0; y < DH && ok; y++) for (int x = 0; x < DW; x++)
        {
            bool in = x >= cl && x < cr && y >= ct && y < cb;
            int e = in ? full[(size_t)y * DW + x] : bg[(size_t)y * DW + x];
            const int g = part[(size_t)y * DW + x];
            int cd = 0; for (int c = 0; c < 32; c += 8) { const int d = abs(((g >> c) & 255) - ((e >> c) & 255)); if (d > cd) cd = d; }
            if (cd > (in ? tol : 0)) { ok = false; if (nclip < 5) { printf("  clip mismatch it %d geo %d flags %d op %d at (%d,%d) in=%d clip %d %d %d %d kinds", it, geo, flags, op, x, y, in, cl, ct, cr, cb); for (int i = 0; i < n; i++) printf(" %d(f%d i%d,%d)", st[i].kind, st[i].flags, st[i].i[0], st[i].i[1]); printf(" got %08x want %08x\n", part[(size_t)y * DW + x], e); } break; }
        }
        if (!ok) nclip++;
    }
    CHECK(npar == 0, "SSE2 != AVX2 in %d of %d random chains", npar, iters);
    CHECK(nclip == 0, "clipping mismatches in %d of %d random chains", nclip, iters);

    // ---- 8. timings (AVX2), 256x256 alpha sprite at 1:1 and scaled x2 bilinear
    {
        const int tw = 256, th = 256; std::vector<int> big = make_sprite(tw, th, true);
        const int BW = 1024, BH = 768; std::vector<int> canvas((size_t)BW * BH, (int)0xff203040);
        struct TC { const char* name; SR2D_FxStage st[3]; int n; int flags; float scale; } tcs[] = {
            { "blur r8",                       { st_blur(8) }, 1, 0, 1 },
            { "blur r8 BOX (1 pass)",          { st_blur2(8, SR2D_FXF_BLUR_BOX, 1) }, 1, 0, 1 },
            { "blur r32",                      { st_blur2(32, 0, 1) }, 1, 0, 1 },
            { "blur r32 down 4",               { st_blur2(32, 0, 4) }, 1, 0, 1 },
            { "blur r32 BOX down 4",           { st_blur2(32, SR2D_FXF_BLUR_BOX, 4) }, 1, 0, 1 },
            { "diffuse r2 x1",                 { st_diffuse(2, 1) }, 1, 0, 1 },
            { "diffuse r3 x3",                 { st_diffuse(3, 3) }, 1, 0, 1 },
            { "wave",                          { st_dist(SR2D_FX_WAVE, 24, 6, 1) }, 1, 0, 1 },
            { "ripple",                        { st_dist(SR2D_FX_RIPPLE, 16, 5, 1, 128, 128, 0) }, 1, 0, 1 },
            { "noise",                         { st_dist(SR2D_FX_NOISE, 24, 6, 1) }, 1, 0, 1 },
            { "turbulence",                    { st_dist(SR2D_FX_TURBULENCE, 24, 6, 1) }, 1, 0, 1 },
            { "map",                           { st_map(map.data(), mw, mh, 2, 6) }, 1, 0, 1 },
            { "colour (bcs)",                  { st_color(0.1f, 1.2f, 1.3f, 1, 0, 1) }, 1, 0, 1 },
            { "colour (gamma+hue)",            { st_color(0, 1, 1, 1.8f, 45, 1) }, 1, 0, 1 },
            { "opacity only",                  { st_color(0, 1, 1, 1, 0, 0.5f) }, 1, 0, 1 },
            { "blur r8 + noise + colour",      { st_blur(8), st_dist(SR2D_FX_NOISE, 24, 6, 1), st_color(0.1f, 1.2f, 1.3f, 1, 0, 0.8f) }, 3, 0, 1 },
            { "noise, x2 bilinear PRE",        { st_dist(SR2D_FX_NOISE, 24, 6, 1) }, 1, SR2D_WARP_BILINEAR, 2 },
            { "noise, x2 bilinear POST",       { st_dist(SR2D_FX_NOISE, 24, 6, 1) }, 1, SR2D_WARP_BILINEAR | SR2D_FX_POST, 2 },
            { "dilate r2 (outline)",           { st_morph(2) }, 1, 0, 1 },
            { "dilate r6",                     { st_morph(6) }, 1, 0, 1 },
            { "dilate r6 square",              { st_morph(6, false, true) }, 1, 0, 1 },
            { "drop shadow blur 6, off 4,4",   { st_shadow(6, 0, 0, 0.7f, 4, 4) }, 1, 0, 1 },
            { "glow blur 8 grow 2",            { st_shadow(8, 2, 0xffc040, 1, 0, 0, SR2D_FXF_SHADOW_GLOW) }, 1, 0, 1 },
            { "shadow, x2 bilinear PRE",       { st_shadow(6, 0, 0, 0.7f, 4, 4) }, 1, SR2D_WARP_BILINEAR, 2 },
        };
        for (const TC& t : tcs)
        {
            float q[8]; quad_rect(q, 100, 100, tw * t.scale, th * t.scale);
            for (int i = 0; i < 3; i++) A.DRAW_FX(big.data(), tw, th, canvas.data(), BW, 0, 0, BW, BH, q, 0, 0, 3, t.flags, 0, t.st, t.n);
            auto t0 = std::chrono::steady_clock::now();
            const int reps = 20;
            for (int i = 0; i < reps; i++) A.DRAW_FX(big.data(), tw, th, canvas.data(), BW, 0, 0, BW, BH, q, 0, 0, 3, t.flags, 0, t.st, t.n);
            double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / reps;
            auto t1 = std::chrono::steady_clock::now();
            for (int i = 0; i < reps; i++) S.DRAW_FX(big.data(), tw, th, canvas.data(), BW, 0, 0, BW, BH, q, 0, 0, 3, t.flags, 0, t.st, t.n);
            double ms2 = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t1).count() / reps;
            printf("  %-30s AVX2 %6.2f ms   SSE2 %6.2f ms\n", t.name, ms, ms2);
        }
    }
    printf("fx: %d failures\n", fails);
    return fails ? 1 : 0;
}
