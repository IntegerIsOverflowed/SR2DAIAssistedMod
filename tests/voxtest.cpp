// voxtest - checks the voxel kernels (VOXEL_FACES / VOXEL_LIGHT / VOXEL_RENDER):
//   * FACES and LIGHT against naive references (exact)
//   * RENDER in cube mode against an independent per-pixel ray caster (voxel + face of every
//     pixel must agree, ties within eps of a lattice edge are skipped)
//   * RENDER in point mode against a brute-force depth test
//   * lighting tiers 1..3 with neutral tables must reproduce tier 0 (structure check)
//   * SSE2 and AVX2 kernel sets must be bit-identical
//   * hostile scenes (NaN matrices, zero view, absurd sizes) must not crash
// Usage: voxtest [iterations] [bench 0/1]
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <vector>
#include <chrono>
#include "../native/sr2d_ops.h"

static uint32_t rng = 12345; static uint32_t rnd() { rng = rng * 1664525u + 1013904223u; return rng >> 8; }
static float frnd() { return (float)(rnd() & 0xffff) / 65535.f; }
static int fails = 0;
#define CHECK(c, ...) do { if (!(c)) { if (fails < 30) { printf("FAIL %s:%d: ", __FILE__, __LINE__); printf(__VA_ARGS__); printf("\n"); } ++fails; } } while (0)

struct Grid
{
    int w, h, d; std::vector<SR2D_Voxel> v; std::vector<uint8_t> faces; std::vector<uint32_t> light;
    int idx(int x, int y, int z) const { return (z * h + y) * w + x; }
    bool solid(int x, int y, int z) const { return x >= 0 && y >= 0 && z >= 0 && x < w && y < h && z < d && (v[idx(x, y, z)].argb >> 24) != 0; }
};

static void make_grid(Grid& g, int w, int h, int d, int style)
{
    g.w = w; g.h = h; g.d = d; g.v.assign((size_t)w * h * d, SR2D_Voxel{ 0, 0, 0, 0 }); g.faces.assign(g.v.size(), 0); g.light.assign(g.v.size(), 0);
    if (style == 0)  // random noise
    {
        int dens = 5 + rnd() % 60;
        for (auto& c : g.v) if ((int)(rnd() % 100) < dens) c.argb = 0xff000000u | (rnd() & 0xffffff);
    }
    else if (style == 1 || style == 3)  // terrain + caves
    {
        for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x)
        {
            float hh = 0.3f + 0.25f * sinf(x * 0.37f + y * 0.11f) + 0.2f * cosf(y * 0.29f - x * 0.07f) + 0.1f * frnd();
            int top = (int)(hh * d); if (top < 1) top = 1; if (top > d) top = d;
            for (int z = 0; z < top; ++z)
            {
                uint32_t c = z == top - 1 ? 0xff40a040u : z > top - 4 ? 0xff805020u : 0xff808080u;
                g.v[g.idx(x, y, z)].argb = c;
            }
        }
        for (int k = 0; k < (int)(w * h * d / 400) + 1; ++k)   // caves
        {
            int cx = rnd() % w, cy = rnd() % h, cz = rnd() % d, r = 1 + rnd() % 3;
            for (int z = cz - r; z <= cz + r; ++z) for (int y = cy - r; y <= cy + r; ++y) for (int x = cx - r; x <= cx + r; ++x)
                if (x >= 0 && y >= 0 && z >= 0 && x < w && y < h && z < d && (x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz) <= r * r) g.v[g.idx(x, y, z)].argb = 0;
        }
    }
    else  // solid box with a hole
    {
        for (auto& c : g.v) c.argb = 0xffc0c0c0u;
        if (w > 2 && h > 2 && d > 2) for (int z = 1; z < d - 1; ++z) g.v[g.idx(w / 2, h / 2, z)].argb = 0;
    }
    // emitters
    for (int k = 0; k < 1 + (int)(g.v.size() / 300); ++k) { auto& c = g.v[rnd() % g.v.size()]; c.emit = (uint8_t)(rnd() % 16); if (style == 3) c.argb = 0xffffd080u; }
    for (auto& c : g.v) { c.mat = (uint8_t)rnd(); c.user = (uint16_t)rnd(); }
}

// ---- references ------------------------------------------------------------------------
static void ref_faces(const Grid& g, std::vector<uint8_t>& out)
{
    out.assign(g.v.size(), 0);
    for (int z = 0; z < g.d; ++z) for (int y = 0; y < g.h; ++y) for (int x = 0; x < g.w; ++x)
    {
        if (!g.solid(x, y, z)) continue;
        int b = 0x40;
        if (!g.solid(x + 1, y, z)) b |= 1; if (!g.solid(x - 1, y, z)) b |= 2;
        if (!g.solid(x, y + 1, z)) b |= 4; if (!g.solid(x, y - 1, z)) b |= 8;
        if (!g.solid(x, y, z + 1)) b |= 16; if (!g.solid(x, y, z - 1)) b |= 32;
        out[g.idx(x, y, z)] = (uint8_t)b;
    }
}
// light levels are stored in 1/8 steps (byte = level * 8); 'dec' = loss per cell in those units (8 = one level)
static uint32_t emission(const SR2D_Voxel& v)
{
    if (!v.emit) return 0; uint32_t e = (v.emit > 15 ? 15 : v.emit) * 8;
    uint32_t r = (((v.argb >> 16) & 255) * e + 127) / 255, g = (((v.argb >> 8) & 255) * e + 127) / 255, b = ((v.argb & 255) * e + 127) / 255;
    return r << 16 | g << 8 | b;
}
static uint32_t bmax(uint32_t a, uint32_t b) { uint32_t r = 0; for (int s = 0; s < 32; s += 8) { uint32_t x = (a >> s) & 255, y = (b >> s) & 255; r |= (x > y ? x : y) << s; } return r; }
static uint32_t bdec(uint32_t a, uint32_t dec) { uint32_t r = 0; for (int s = 0; s < 32; s += 8) { uint32_t x = (a >> s) & 255; r |= (x > dec ? x - dec : 0) << s; } return r; }
static void ref_light(const Grid& g, int sky, int flags, std::vector<uint32_t>& L)
{
    const int sides = flags & 1; uint32_t dec = (uint32_t)(flags >> 8) & 255; if (!dec) dec = 8; if (dec > 120) dec = 120;
    sky *= 8;
    std::vector<uint32_t> seed(g.v.size(), 0);
    for (int y = 0; y < g.h; ++y) for (int x = 0; x < g.w; ++x)
    {
        bool open = true;
        for (int z = g.d - 1; z >= 0; --z)
        {
            int i = g.idx(x, y, z); uint32_t s = emission(g.v[i]);
            if (g.solid(x, y, z)) open = false;
            else
            {
                if (open) s |= (uint32_t)sky << 24;
                if (sides && (uint32_t)sky > dec && (x == 0 || y == 0 || x == g.w - 1 || y == g.h - 1)) s = bmax(s, (uint32_t)(sky - dec) << 24);
            }
            seed[i] = s;
        }
    }
    L = seed;
    for (int it = 0; it < 1024; ++it)
    {
        bool ch = false;
        for (int z = 0; z < g.d; ++z) for (int y = 0; y < g.h; ++y) for (int x = 0; x < g.w; ++x)
        {
            if (g.solid(x, y, z)) continue;
            int i = g.idx(x, y, z); uint32_t m = seed[i];
            const int nb[6][3] = { {1,0,0},{-1,0,0},{0,1,0},{0,-1,0},{0,0,1},{0,0,-1} };
            for (auto& n : nb)
            {
                int nx = x + n[0], ny = y + n[1], nz = z + n[2];
                if (nx < 0 || ny < 0 || nz < 0 || nx >= g.w || ny >= g.h || nz >= g.d) continue;
                m = bmax(m, bdec(L[g.idx(nx, ny, nz)], dec));
            }
            if (m != L[i]) { L[i] = m; ch = true; }
        }
        if (!ch) break;
    }
}

// camera: view v (unit), rows r/u ; m = [sx*r ; sy*u]
struct Cam { float m[6], ox, oy; float v[3]; };
static void normalize(float* a) { float l = sqrtf(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); a[0] /= l; a[1] /= l; a[2] /= l; }
static void cross(const float* a, const float* b, float* o) { o[0] = a[1] * b[2] - a[2] * b[1]; o[1] = a[2] * b[0] - a[0] * b[2]; o[2] = a[0] * b[1] - a[1] * b[0]; }
static Cam random_cam(const Grid& g, int W, int H, bool points)
{
    Cam c;
    int kind = rnd() % 4;
    float yaw, pitch;
    if (kind == 0) { yaw = (float)(rnd() % 4) * 1.5707963f + 0.7853982f; pitch = 0.6154797f; }      // isometric
    else if (kind == 1) { yaw = (float)(rnd() % 4) * 1.5707963f + 0.7853982f; pitch = 0.4636476f; } // 2:1
    else if (kind == 2) { yaw = (float)(rnd() % 4) * 1.5707963f; pitch = (rnd() & 1) ? 1.5707963f : 0.f; } // top / side
    else { yaw = frnd() * 6.2831853f; pitch = -1.4f + frnd() * 2.8f; }
    c.v[0] = cosf(pitch) * cosf(yaw); c.v[1] = cosf(pitch) * sinf(yaw); c.v[2] = -sinf(pitch);
    float up[3] = { 0, 0, 1 }; float r[3], u[3];
    cross(c.v, up, r);
    if (fabsf(r[0]) + fabsf(r[1]) + fabsf(r[2]) < 1e-4f) { r[0] = 1; r[1] = 0; r[2] = 0; }
    normalize(r); cross(r, c.v, u); normalize(u);
    float s = points ? 1.f : 1.5f + frnd() * 12.f;
    float sy = s * (kind == 1 ? 0.5f : 1.f) * (0.7f + frnd() * 0.6f);
    if (points) { s = 1.f; sy = 1.f; }
    // small jitter kills exact half-pixel ties (they are legal but make the reference ambiguous)
    c.m[0] = s * r[0]; c.m[1] = s * r[1]; c.m[2] = s * r[2];
    c.m[3] = -sy * u[0]; c.m[4] = -sy * u[1]; c.m[5] = -sy * u[2];
    // centre the grid
    float cx = 0, cy = 0; float gc[3] = { g.w * 0.5f, g.h * 0.5f, g.d * 0.5f };
    cx = c.m[0] * gc[0] + c.m[1] * gc[1] + c.m[2] * gc[2]; cy = c.m[3] * gc[0] + c.m[4] * gc[1] + c.m[5] * gc[2];
    c.ox = W * 0.5f - cx + (frnd() - 0.5f) * 20.f + 0.013f; c.oy = H * 0.5f - cy + (frnd() - 0.5f) * 20.f + 0.017f;
    return c;
}
static void fill_scene(SR2D_VoxelScene& S, const Grid& g, const Cam& c, int lighting, int mode)
{
    memset(&S, 0, sizeof S);
    S.vox = g.v.data(); S.faces = g.faces.data(); S.light = g.light.data(); S.gw = g.w; S.gh = g.h; S.gd = g.d;
    S.lighting = lighting; S.mode = mode;
    memcpy(S.m, c.m, sizeof S.m); S.ox = c.ox; S.oy = c.oy; memcpy(S.view, c.v, sizeof S.view);
    static const float sh[6] = { 0.8f, 0.6f, 0.7f, 0.5f, 1.0f, 0.4f }; memcpy(S.shade, sh, sizeof sh);
    S.ao[0] = 1.f; S.ao[1] = 0.8f; S.ao[2] = 0.65f; S.ao[3] = 0.5f;
    for (int i = 0; i < 16; ++i) S.lut[i] = powf(0.8f, (float)(15 - i));
    S.skyColor = 0xffffff; S.outside = 120u << 24;   // 15 levels in 1/8 steps
}

// ---- ray cast reference (cube mode) --------------------------------------------------------
// returns voxel index or -1; face in *face; -2 = ambiguous (ray within eps of a lattice edge)
static int ray_pick(const Grid& g, const Cam& c, double px, double py, int* face)
{
    // p0 = M^T (M M^T)^-1 s
    double m[6]; for (int i = 0; i < 6; ++i) m[i] = c.m[i];
    double a = m[0] * m[0] + m[1] * m[1] + m[2] * m[2], b = m[0] * m[3] + m[1] * m[4] + m[2] * m[5], d = m[3] * m[3] + m[4] * m[4] + m[5] * m[5];
    double det = a * d - b * b; if (fabs(det) < 1e-18) return -2;
    double sx = px - c.ox, sy = py - c.oy;
    double k0 = (d * sx - b * sy) / det, k1 = (-b * sx + a * sy) / det;
    double p[3] = { m[0] * k0 + m[3] * k1, m[1] * k0 + m[4] * k1, m[2] * k0 + m[5] * k1 };
    double v[3] = { c.v[0], c.v[1], c.v[2] };
    const double eps = 2e-3;
    // slab entry
    double tn = -1e30, tf = 1e30; int axis = -1; double lim[3] = { (double)g.w, (double)g.h, (double)g.d };
    double tn2 = -1e30;   // second largest entry t (tie detection)
    for (int i = 0; i < 3; ++i)
    {
        if (fabs(v[i]) < 1e-12) { if (p[i] < 0 || p[i] >= lim[i]) return -1; if (p[i] < eps || p[i] > lim[i] - eps) return -2; continue; }
        double t0 = (0 - p[i]) / v[i], t1 = (lim[i] - p[i]) / v[i]; if (t0 > t1) { double t = t0; t0 = t1; t1 = t; }
        if (t0 > tn) { tn2 = tn; tn = t0; axis = i; } else if (t0 > tn2) tn2 = t0;
        if (t1 < tf) tf = t1;
    }
    if (tn >= tf - eps) return tn > tf ? -1 : -2;
    if (tn - tn2 < eps) return -2;
    double q[3]; for (int i = 0; i < 3; ++i) q[i] = p[i] + tn * v[i];
    int cell[3]; for (int i = 0; i < 3; ++i) { cell[i] = (int)floor(q[i]); if (cell[i] < 0) cell[i] = 0; if (cell[i] >= (int)lim[i]) cell[i] = (int)lim[i] - 1; }
    cell[axis] = v[axis] > 0 ? 0 : (int)lim[axis] - 1;
    // near an edge of the entry face?
    for (int i = 0; i < 3; ++i) if (i != axis) { double fr = q[i] - floor(q[i]); if (fr < eps || fr > 1 - eps) return -2; }
    int step[3]; double tmax[3], tdelta[3];
    for (int i = 0; i < 3; ++i)
    {
        if (fabs(v[i]) < 1e-12) { step[i] = 0; tmax[i] = 1e30; tdelta[i] = 1e30; continue; }
        step[i] = v[i] > 0 ? 1 : -1;
        double nextb = v[i] > 0 ? cell[i] + 1 : cell[i];
        tmax[i] = (nextb - p[i]) / v[i]; tdelta[i] = fabs(1.0 / v[i]);
    }
    int lastaxis = axis;
    for (int guard = 0; guard < 4 * (g.w + g.h + g.d); ++guard)
    {
        if (g.solid(cell[0], cell[1], cell[2])) { *face = lastaxis * 2 + (v[lastaxis] > 0 ? 1 : 0); return g.idx(cell[0], cell[1], cell[2]); }
        int ax = 0; if (tmax[1] < tmax[ax]) ax = 1; if (tmax[2] < tmax[ax]) ax = 2;
        for (int i = 0; i < 3; ++i) if (i != ax && fabs(tmax[i] - tmax[ax]) < eps) return -2;
        cell[ax] += step[ax]; if (cell[ax] < 0 || cell[ax] >= (int)lim[ax]) return -1;
        tmax[ax] += tdelta[ax]; lastaxis = ax;
    }
    return -1;
}

int main(int argc, char** argv)
{
    int iters = argc > 1 ? atoi(argv[1]) : 300;
    int bench = argc > 2 ? atoi(argv[2]) : 1;
    sr2d_ops O[2]; sr2d_fill_ops_sse2(O[0]); sr2d_fill_ops_avx2(O[1]);
    long long ambig = 0, checked = 0, pointsChecked = 0, hitRay = 0, hitPt = 0;

    for (int it = 0; it < iters; ++it)
    {
        Grid g; int style = rnd() % 4;
        int w = 1 + rnd() % 20, h = 1 + rnd() % 20, d = 1 + rnd() % 14;
        if (it % 11 == 0) { w = 1 + rnd() % 70; h = 1 + rnd() % 70; d = 1 + rnd() % 40; }
        make_grid(g, w, h, d, style);
        std::vector<uint8_t> rf; ref_faces(g, rf);
        int ne = O[it & 1].VOXEL_FACES(g.v.data(), w, h, d, g.faces.data());
        CHECK(memcmp(rf.data(), g.faces.data(), rf.size()) == 0, "faces mismatch %dx%dx%d", w, h, d);
        int cntref = 0; for (auto f : rf) cntref += (f & 63) != 0; CHECK(ne == cntref, "faces count %d vs %d", ne, cntref);

        int sky = rnd() % 16, sides = (int)(rnd() & 1);
        switch (rnd() % 5) { case 0: sides |= 4 << 8; break; case 1: sides |= 2 << 8; break; case 2: sides |= 1 << 8; break; case 3: sides |= (int)(1 + rnd() % 40) << 8; break; default: break; }   // reach: 30 / 60 / 120 cells / random / default
        std::vector<uint32_t> rl; ref_light(g, sky, sides, rl);
        int nl = O[it & 1].VOXEL_LIGHT(g.v.data(), g.faces.data(), w, h, d, g.light.data(), sky, sides);
        CHECK(nl >= 0, "light oom");
        int bad = 0; for (size_t i = 0; i < rl.size(); ++i) if (rl[i] != g.light[i]) { if (!bad) CHECK(false, "light mismatch at %zu: %08x vs %08x (grid %dx%dx%d sky %d)", i, rl[i], g.light[i], w, h, d, sky); ++bad; }
        // both kernel sets identical
        { std::vector<uint32_t> l2(rl.size()); O[(it + 1) & 1].VOXEL_LIGHT(g.v.data(), g.faces.data(), w, h, d, l2.data(), sky, sides); CHECK(l2 == g.light, "light sse2/avx2 differ"); }

        int W = 40 + rnd() % 200, H = 40 + rnd() % 160;
        int cl = rnd() % 8, ct = rnd() % 8, cr = W - rnd() % 8, cb = H - rnd() % 8;
        std::vector<int> img0((size_t)W * H), img1((size_t)W * H), pk0((size_t)W * H), pk1((size_t)W * H);
        for (int mode = 0; mode < 2; ++mode)
        {
            Cam cam = random_cam(g, W, H, mode == 0);
            for (int lighting = 0; lighting < 4; ++lighting)
            {
                SR2D_VoxelScene S; fill_scene(S, g, cam, lighting, mode);
                for (int k = 0; k < 2; ++k)
                {
                    std::vector<int>& img = k ? img1 : img0; std::vector<int>& pk = k ? pk1 : pk0;
                    std::fill(img.begin(), img.end(), 0x11223344); std::fill(pk.begin(), pk.end(), -1);
                    O[k].VOXEL_RENDER(&S, img.data(), W, cl, ct, cr, cb, pk.data());
                }
                CHECK(img0 == img1 && pk0 == pk1, "sse2/avx2 differ mode %d lighting %d", mode, lighting);
                // clip respected
                for (int y = 0; y < H; ++y) for (int x = 0; x < W; ++x)
                    if (x < cl || x >= cr || y < ct || y >= cb) { CHECK(img0[(size_t)y * W + x] == 0x11223344, "clip violated at %d,%d", x, y); goto nextpix; }
                    else if (pk0[(size_t)y * W + x] >= 0) { CHECK((img0[(size_t)y * W + x] >> 24 & 255) == 255, "alpha"); }
                    else CHECK(img0[(size_t)y * W + x] == 0x11223344, "pixel written without pick at %d,%d", x, y);
                nextpix:;
                if (lighting == 0)
                {
                    if (mode == 1)
                    {
                        // ray cast reference
                        for (int y = ct; y < cb; ++y) for (int x = cl; x < cr; ++x)
                        {
                            int face = -1; int r = ray_pick(g, cam, x + 0.5, y + 0.5, &face);
                            if (r == -2) { ++ambig; continue; }
                            ++checked;
                            int p = pk0[(size_t)y * W + x]; int pi = p >= 0 ? p >> 3 : -1, pf = p >= 0 ? p & 7 : -1;
                            if (pi != r || (r >= 0 && pf != face))
                            {
                                CHECK(false, "raycast mismatch it %d px %d,%d: ref vox %d face %d, got vox %d face %d (grid %dx%dx%d style %d)", it, x, y, r, face, pi, pf, w, h, d, style);
                                goto nextmode;
                            }
                            if (r >= 0) { ++hitRay; CHECK((uint32_t)img0[(size_t)y * W + x] == (g.v[r].argb | 0xff000000u), "colour mismatch"); }
                        }
                    }
                    else
                    {
                        // point reference: nearest exposed voxel per pixel
                        std::vector<float> depth((size_t)W * H, 1e30f); std::vector<int> ref((size_t)W * H, -1);
                        float hx = 0.5f * (cam.m[0] + cam.m[1] + cam.m[2]), hy = 0.5f * (cam.m[3] + cam.m[4] + cam.m[5]);
                        int vis = 0; for (int f = 0; f < 6; ++f) { static const int n[6][3] = { {1,0,0},{-1,0,0},{0,1,0},{0,-1,0},{0,0,1},{0,0,-1} }; if (cam.v[0] * n[f][0] + cam.v[1] * n[f][1] + cam.v[2] * n[f][2] < -1e-6f) vis |= 1 << f; }
                        for (int z = 0; z < d; ++z) for (int yy = 0; yy < h; ++yy) for (int xx = 0; xx < w; ++xx)
                        {
                            int i = g.idx(xx, yy, z); if (!(g.faces[i] & vis)) continue;
                            float sx = ((cam.m[0] * xx + cam.m[1] * yy) + cam.m[2] * z) + cam.ox, sy = ((cam.m[3] * xx + cam.m[4] * yy) + cam.m[5] * z) + cam.oy;
                            int px = (int)floorf(sx + hx), py = (int)floorf(sy + hy);
                            if (px < cl || px >= cr || py < ct || py >= cb) continue;
                            float dep = cam.v[0] * xx + cam.v[1] * yy + cam.v[2] * z;
                            if (dep < depth[(size_t)py * W + px]) { depth[(size_t)py * W + px] = dep; ref[(size_t)py * W + px] = i; }
                        }
                        for (int y = ct; y < cb; ++y) for (int x = cl; x < cr; ++x)
                        {
                            int p = pk0[(size_t)y * W + x]; int pi = p >= 0 ? p >> 3 : -1; int r = ref[(size_t)y * W + x];
                            ++pointsChecked; hitPt += r >= 0;
                            if (pi == r) continue;
                            bool ok = false;
                            if (pi >= 0 && r >= 0)
                            {
                                int xx = pi % w, yy = (pi / w) % h, z = pi / (w * h);
                                float dep = cam.v[0] * xx + cam.v[1] * yy + cam.v[2] * z;
                                ok = fabsf(dep - depth[(size_t)y * W + x]) < 1e-4f;
                            }
                            if (!ok) { CHECK(false, "point mismatch it %d px %d,%d ref %d got %d", it, x, y, r, pi); goto nextmode; }
                        }
                    }
                }
            }
            // neutral tables: tiers 1..3 == tier 0
            {
                std::vector<int> base((size_t)W * H, 0), t((size_t)W * H, 0);
                SR2D_VoxelScene S; fill_scene(S, g, cam, 0, mode);
                O[1].VOXEL_RENDER(&S, base.data(), W, cl, ct, cr, cb, nullptr);
                for (int lighting = 1; lighting <= 3; ++lighting)
                {
                    fill_scene(S, g, cam, lighting, mode);
                    for (int i = 0; i < 6; ++i) S.shade[i] = 1.f; for (int i = 0; i < 4; ++i) S.ao[i] = 1.f; for (int i = 0; i < 16; ++i) S.lut[i] = 1.f;
                    std::fill(t.begin(), t.end(), 0);
                    O[1].VOXEL_RENDER(&S, t.data(), W, cl, ct, cr, cb, nullptr);
                    CHECK(t == base, "neutral lighting %d != none (mode %d)", lighting, mode);
                }
            }
        nextmode:;
        }
    }
    printf("voxtest: %d iterations, raycast pixels checked %lld (%lld hit a voxel, ambiguous skipped %lld = %.2f%%), point pixels %lld (%lld hits)\n",
        iters, checked, hitRay, ambig, 100.0 * ambig / (double)(checked + ambig + 1), pointsChecked, hitPt);

    // ---- hostile input --------------------------------------------------------------------
    {
        Grid g; make_grid(g, 9, 7, 5, 1); O[1].VOXEL_FACES(g.v.data(), 9, 7, 5, g.faces.data()); O[1].VOXEL_LIGHT(g.v.data(), g.faces.data(), 9, 7, 5, g.light.data(), 15, 1);
        std::vector<int> img(64 * 64);
        float nan = 0.f; nan = 0.f / nan;
        const float bad[] = { nan, 1e30f, -1e30f, 0.f, 1e-30f, 3.f, -7.5f };
        for (int it = 0; it < 4000; ++it)
        {
            SR2D_VoxelScene S; fill_scene(S, g, random_cam(g, 64, 64, false), rnd() % 6 - 1, rnd() % 3 - 1);
            for (int i = 0; i < 6; ++i) if (rnd() % 3 == 0) S.m[i] = bad[rnd() % 7];
            if (rnd() % 3 == 0) S.ox = bad[rnd() % 7]; if (rnd() % 3 == 0) S.oy = bad[rnd() % 7];
            for (int i = 0; i < 3; ++i) if (rnd() % 3 == 0) S.view[i] = bad[rnd() % 7];
            for (int i = 0; i < 6; ++i) if (rnd() % 4 == 0) S.shade[i] = bad[rnd() % 7];
            for (int i = 0; i < 16; ++i) if (rnd() % 4 == 0) S.lut[i] = bad[rnd() % 7];
            for (int i = 0; i < 4; ++i) if (rnd() % 4 == 0) S.ao[i] = bad[rnd() % 7];
            if (rnd() % 5 == 0) S.light = nullptr;
            S.outside = rnd(); S.skyColor = (int)rnd(); S.flags = rnd() % 16;
            S.fadeMin = bad[rnd() % 7]; S.fadeGamma = bad[rnd() % 7]; S.fadeColor = (int)rnd();
            S.zFrom = (int)(rnd() % 12) - 3; S.zTo = (int)(rnd() % 12) - 3;
            int cl = rnd() % 70 - 3, ct = rnd() % 70 - 3, cr = cl + rnd() % 70, cb = ct + rnd() % 70;
            if (cl < 0) cl = 0; if (ct < 0) ct = 0; if (cr > 64) cr = 64; if (cb > 64) cb = 64;
            O[it & 1].VOXEL_RENDER(&S, img.data(), 64, cl, ct, cr, cb, nullptr);
        }
        // ---- v2 fields: slab ranges compose to the whole picture; depth fade matches a scalar model
        {
            std::vector<int> whole(96 * 96), part(96 * 96);
            for (int it = 0; it < 60; ++it)
            {
                Grid g2; make_grid(g2, 6 + rnd() % 14, 6 + rnd() % 14, 4 + rnd() % 10, rnd() % 4);
                O[1].VOXEL_FACES(g2.v.data(), g2.w, g2.h, g2.d, g2.faces.data()); O[1].VOXEL_LIGHT(g2.v.data(), g2.faces.data(), g2.w, g2.h, g2.d, g2.light.data(), 15, 1);
                const int mode = rnd() % 2, lighting = rnd() % 4;
                SR2D_VoxelScene S2; fill_scene(S2, g2, random_cam(g2, 96, 96, mode == 0), lighting, mode);
                S2.flags |= (rnd() % 2 ? SR2D_VOX_FADE_Z : 0) | (rnd() % 2 ? SR2D_VOX_FADE_VIEW : 0) | (rnd() % 2 ? SR2D_VOX_FADE_COLOR : 0);
                S2.fadeMin = (rnd() % 100) / 100.f; S2.fadeGamma = 0.3f + (rnd() % 30) / 10.f; S2.fadeColor = (int)(rnd() & 0xffffff);
                std::fill(whole.begin(), whole.end(), 0x11223344); std::fill(part.begin(), part.end(), 0x11223344);
                int nw = O[it & 1].VOXEL_RENDER(&S2, whole.data(), 96, 0, 0, 96, 96, nullptr);
                // the same in 1..4 slab chunks, painter's order (ascending zi = far to near)
                int np = 0, chunks = 1 + rnd() % 4;
                for (int c = 0; c < chunks; ++c)
                {
                    S2.zFrom = g2.d * c / chunks; S2.zTo = g2.d * (c + 1) / chunks;
                    if (S2.zTo == 0) continue;
                    np += O[(it + c) & 1].VOXEL_RENDER(&S2, part.data(), 96, 0, 0, 96, 96, nullptr);
                }
                CHECK(nw == np && whole == part, "slab-range rendering differs from the whole (mode %d lighting %d chunks %d)", mode, lighting, chunks);
                // fade model on flat lighting, points mode: pixel = fade(voxel colour) of the voxel at that pixel
                if (lighting == 0)
                {
                    S2.zFrom = S2.zTo = 0;
                    std::vector<int> pk(96 * 96, -1), img(96 * 96, 0x11223344);
                    O[1].VOXEL_RENDER(&S2, img.data(), 96, 0, 0, 96, 96, pk.data());
                    // camera unit vector and the depth range over the 8 corners
                    double vl = sqrt((double)S2.view[0] * S2.view[0] + (double)S2.view[1] * S2.view[1] + (double)S2.view[2] * S2.view[2]);
                    double n0 = S2.view[0] / vl, n1 = S2.view[1] / vl, n2 = S2.view[2] / vl, dmin = 1e30, dmax = -1e30;
                    for (int c = 0; c < 8; ++c) { double dd = n0 * (c & 1 ? g2.w : 0) + n1 * (c & 2 ? g2.h : 0) + n2 * (c & 4 ? g2.d : 0); dmin = dd < dmin ? dd : dmin; dmax = dd > dmax ? dd : dmax; }
                    int worst = 0;
                    for (int i = 0; i < 96 * 96; ++i)
                    {
                        if (pk[i] < 0) continue;
                        int vi = pk[i] >> 3;   // small grids: pick = index << 3 | face
                        int z = vi / (g2.w * g2.h), y = (vi / g2.w) % g2.h, x = vi % g2.w;
                        double t = 1;
                        if (S2.flags & SR2D_VOX_FADE_Z) t *= g2.d > 1 ? S2.fadeMin + (1 - S2.fadeMin) * z / (double)(g2.d - 1) : 1;
                        if (S2.flags & SR2D_VOX_FADE_VIEW) { double dep = n0 * (x + .5) + n1 * (y + .5) + n2 * (z + .5); double r = dmax - dmin > 1e-6 ? dmax - dmin : 1; double tt = S2.fadeMin + (1 - S2.fadeMin) * (dmax - dep) / r; t *= tt < 0 ? 0 : tt > 1 ? 1 : tt; }
                        if (t < 0) t = 0; if (t > 1) t = 1;
                        t = pow(t, (double)S2.fadeGamma);
                        uint32_t c = g2.v[vi].argb;
                        for (int sh = 0; sh <= 16; sh += 8)
                        {
                            double ch = (c >> sh) & 255, fog = (S2.fadeColor >> sh) & 255;
                            double e = (S2.flags & SR2D_VOX_FADE_COLOR) ? ch * t + fog * (1 - t) : ch * t;
                            int got = (img[i] >> sh) & 255, dlt = abs(got - (int)(e + .5)); worst = dlt > worst ? dlt : worst;
                        }
                    }
                    CHECK(worst <= 2, "fade model off by %d (flags %d min %.2f gamma %.2f)", worst, S2.flags, S2.fadeMin, S2.fadeGamma);
                }
            }
        }
        // degenerate sizes
        SR2D_VoxelScene S; fill_scene(S, g, random_cam(g, 64, 64, false), 3, 1);
        S.gw = 0; CHECK(O[1].VOXEL_RENDER(&S, img.data(), 64, 0, 0, 64, 64, nullptr) == 0, "gw 0");
        S.gw = 100000; S.gh = 100000; S.gd = 100000; CHECK(O[1].VOXEL_RENDER(&S, img.data(), 64, 0, 0, 64, 64, nullptr) == 0, "huge");
        CHECK(O[1].VOXEL_FACES(g.v.data(), 0, 1, 1, g.faces.data()) == 0, "faces 0");
        CHECK(O[1].VOXEL_LIGHT(nullptr, g.faces.data(), 1, 1, 1, g.light.data(), 15, 0) == 0, "light null");
        printf("hostile input: ok\n");
    }

    // ---- VOXEL_FLOOD vs reference BFS ---------------------------------------------------
    {
        int floodChecks = 0;
        for (int it = 0; it < 150; ++it)
        {
            int w = 1 + rnd() % 20, h = 1 + rnd() % 20, d = 1 + rnd() % 20;
            Grid g; make_grid(g, w, h, d, rnd() % 4);
            // few colours so tolerance / material matching has something to merge
            for (auto& v : g.v) if (v.argb >> 24) { uint32_t c = rnd() % 4; v.argb = 0xff000000u | (c * 0x50) << 16 | (c * 0x30) << 8 | (0x80 - c * 0x20); v.mat = (uint8_t)(rnd() % 3); }
            size_t n = g.v.size();
            int flags = (rnd() % 3 == 0 ? SR2D_VOXF_GLOBAL : 0) | (rnd() & 1 ? SR2D_VOXF_DIAGONAL : 0) | (rnd() % 4 == 0 ? SR2D_VOXF_REF : 0) | (rnd() % 4 == 0 ? SR2D_VOXF_OUTSIDE : 0) | (rnd() % 3 == 0 ? SR2D_VOXF_MATERIAL : 0);
            int tol = rnd() % 3 == 0 ? (int)(rnd() % 0x60) : 0;
            int sx = rnd() % w, sy = rnd() % h, sz = rnd() % d;
            uint32_t ref = rnd() & 1 ? 0u : (0xff000000u | (rnd() % 4 * 0x50) << 16 | 0x80); int refMat = rnd() % 3;
            std::vector<uint8_t> mask(n, 0xcd), rm(n, 0);
            int got = O[it & 1].VOXEL_FLOOD(g.v.data(), w, h, d, sx, sy, sz, ref, refMat, tol, flags, mask.data());
            // reference
            SR2D_Voxel seed = (flags & SR2D_VOXF_REF) ? SR2D_Voxel{ ref, 0, (uint8_t)refMat, 0 } : g.v[g.idx(sx, sy, sz)];
            bool seedSolid = (seed.argb >> 24) != 0;
            auto match = [&](const SR2D_Voxel& v) {
                bool s = (v.argb >> 24) != 0; if (s != seedSolid) return false; if (!s) return true;
                if (flags & SR2D_VOXF_MATERIAL) return v.mat == seed.mat;
                int m = 0; for (int sh = 0; sh < 24; sh += 8) { int dd = (int)((v.argb >> sh) & 255) - (int)((seed.argb >> sh) & 255); if (dd < 0) dd = -dd; if (dd > m) m = dd; }
                return m <= tol; };
            std::vector<int> q;
            auto push = [&](int x, int y, int z) { int i = g.idx(x, y, z); if (!rm[i] && match(g.v[i])) { rm[i] = 1; q.push_back(i); } };
            if (flags & SR2D_VOXF_GLOBAL) { for (size_t i = 0; i < n; ++i) if (match(g.v[i])) rm[i] = 1; }
            else
            {
                if (flags & SR2D_VOXF_OUTSIDE) { for (int z = 0; z < d; ++z) for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) if (x == 0 || y == 0 || z == 0 || x == w - 1 || y == h - 1 || z == d - 1) push(x, y, z); }
                else push(sx, sy, sz);
                for (size_t qi = 0; qi < q.size(); ++qi)
                {
                    int i = q[qi], x = i % w, y = (i / w) % h, z = i / (w * h);
                    for (int dz = -1; dz <= 1; ++dz) for (int dy = -1; dy <= 1; ++dy) for (int dx = -1; dx <= 1; ++dx)
                    {
                        if (!dx && !dy && !dz) continue;
                        if (!(flags & SR2D_VOXF_DIAGONAL) && (dx != 0) + (dy != 0) + (dz != 0) != 1) continue;
                        int nx = x + dx, ny = y + dy, nz = z + dz; if (nx < 0 || ny < 0 || nz < 0 || nx >= w || ny >= h || nz >= d) continue;
                        push(nx, ny, nz);
                    }
                }
            }
            int cnt = 0; for (auto m : rm) cnt += m;
            CHECK(got == cnt, "flood count %d vs ref %d (grid %dx%dx%d flags %d tol %d)", got, cnt, w, h, d, flags, tol);
            CHECK(mask == rm, "flood mask mismatch (grid %dx%dx%d flags %d tol %d)", w, h, d, flags, tol);
            { std::vector<uint8_t> m2(n); O[(it + 1) & 1].VOXEL_FLOOD(g.v.data(), w, h, d, sx, sy, sz, ref, refMat, tol, flags, m2.data()); CHECK(m2 == mask, "flood sse2/avx2 differ"); }
            ++floodChecks;
        }
        // hollow box: outside air vs cavity
        {
            int n = 9; Grid g; g.w = g.h = g.d = n; g.v.assign((size_t)n * n * n, SR2D_Voxel{ 0, 0, 0, 0 });
            for (int z = 2; z < 7; ++z) for (int y = 2; y < 7; ++y) for (int x = 2; x < 7; ++x) if (x == 2 || y == 2 || z == 2 || x == 6 || y == 6 || z == 6) g.v[g.idx(x, y, z)].argb = 0xff808080u;
            std::vector<uint8_t> m(g.v.size());
            int outside = O[1].VOXEL_FLOOD(g.v.data(), n, n, n, 0, 0, 0, 0, 0, 0, SR2D_VOXF_OUTSIDE | SR2D_VOXF_REF, m.data());
            CHECK(outside == n * n * n - 125, "outside air %d", outside);
            CHECK(m[g.idx(4, 4, 4)] == 0 && m[g.idx(0, 0, 0)] == 1, "cavity not selected as outside");
            int cav = O[1].VOXEL_FLOOD(g.v.data(), n, n, n, 4, 4, 4, 0, 0, 0, 0, m.data());
            CHECK(cav == 27 && m[g.idx(4, 4, 4)] == 1 && m[g.idx(0, 0, 0)] == 0, "cavity %d", cav);
            int shell = O[1].VOXEL_FLOOD(g.v.data(), n, n, n, 2, 2, 2, 0, 0, 0, 0, m.data());
            CHECK(shell == 125 - 27, "shell %d", shell);
            // hostile
            CHECK(O[1].VOXEL_FLOOD(g.v.data(), n, n, n, -1, 0, 0, 0, 0, 0, 0, m.data()) == 0, "seed outside");
            CHECK(O[1].VOXEL_FLOOD(g.v.data(), n, n, n, -1, 0, 0, 0, 0, 0, SR2D_VOXF_OUTSIDE, m.data()) == 0, "seed outside (OUTSIDE without REF)");
            CHECK(O[1].VOXEL_FLOOD(g.v.data(), n, n, n, 99, 99, 99, 0, 0, 0, SR2D_VOXF_OUTSIDE | SR2D_VOXF_REF, m.data()) == n * n * n - 125, "REF ignores the seed position");
            CHECK(O[1].VOXEL_FLOOD(g.v.data(), 0, n, n, 0, 0, 0, 0, 0, 0, 0, m.data()) == 0, "gw 0");
            CHECK(O[1].VOXEL_FLOOD(nullptr, n, n, n, 0, 0, 0, 0, 0, 0, 0, m.data()) == 0, "null");
        }
        printf("VOXEL_FLOOD: %d random grids + cavity box ok\n", floodChecks);
    }

    // ---- timing -----------------------------------------------------------------------
    if (bench)
    {
        const int sizes[] = { 64, 128, 256 };
        for (int si = 0; si < 3; ++si)
        {
            int n = sizes[si]; Grid g; make_grid(g, n, n, n / 2, 3);
            auto t0 = std::chrono::steady_clock::now();
            int ne = O[1].VOXEL_FACES(g.v.data(), n, n, n / 2, g.faces.data());
            auto t1 = std::chrono::steady_clock::now();
            int nl = O[1].VOXEL_LIGHT(g.v.data(), g.faces.data(), n, n, n / 2, g.light.data(), 15, 1);
            auto t2 = std::chrono::steady_clock::now();
            printf("grid %dx%dx%d: faces %.1f ms (%d exposed)  light %.1f ms (%d cells)\n", n, n, n / 2,
                std::chrono::duration<double, std::milli>(t1 - t0).count(), ne, std::chrono::duration<double, std::milli>(t2 - t1).count(), nl);
            int W = 1024, H = 768; std::vector<int> img((size_t)W * H);
            for (int mode = 0; mode < 2; ++mode) for (int lighting = 0; lighting < 4; ++lighting) for (int k = 0; k < 2; ++k)
            {
                Cam c; memset(&c, 0, sizeof c);
                float yaw = 0.7853982f, pitch = 0.6154797f; c.v[0] = cosf(pitch) * cosf(yaw); c.v[1] = cosf(pitch) * sinf(yaw); c.v[2] = -sinf(pitch);
                float up[3] = { 0, 0, 1 }, r[3], u[3]; cross(c.v, up, r); normalize(r); cross(r, c.v, u); normalize(u);
                float s = mode ? (n == 64 ? 8.f : n == 128 ? 4.f : 2.f) : (n == 256 ? 2.f : 1.f);
                c.m[0] = s * r[0]; c.m[1] = s * r[1]; c.m[2] = s * r[2]; c.m[3] = -s * u[0]; c.m[4] = -s * u[1]; c.m[5] = -s * u[2];
                float gc[3] = { n * 0.5f, n * 0.5f, n * 0.25f };
                c.ox = W * 0.5f - (c.m[0] * gc[0] + c.m[1] * gc[1] + c.m[2] * gc[2]); c.oy = H * 0.5f - (c.m[3] * gc[0] + c.m[4] * gc[1] + c.m[5] * gc[2]);
                SR2D_VoxelScene S; fill_scene(S, g, c, lighting, mode);
                int reps = n == 256 ? 3 : 8; int drawn = 0;
                O[k].VOXEL_RENDER(&S, img.data(), W, 0, 0, W, H, nullptr);
                auto a = std::chrono::steady_clock::now();
                for (int i = 0; i < reps; ++i) drawn = O[k].VOXEL_RENDER(&S, img.data(), W, 0, 0, W, H, nullptr);
                auto b = std::chrono::steady_clock::now();
                printf("  %s scale %.0f lighting %d %s: %.2f ms (%d voxels drawn)\n", mode ? "cubes " : "points", s, lighting, k ? "avx2" : "sse2",
                    std::chrono::duration<double, std::milli>(b - a).count() / reps, drawn);
            }
        }
    }
    printf(fails ? "voxtest: %d FAILURES\n" : "voxtest: all passed\n", fails);
    return fails ? 1 : 0;
}
