// SR2D - DRAW_FX: effect chain (blur / distortion / colour) with one composite.
// Included inside struct K<V> (sr2d_kernels.inl), after the warp and blur kernels.
//
//   work image  : premultiplied 8-bit BGRA, sprite (or its transformed image in POST
//                 mode) surrounded by a transparent margin M = sum of the stage margins
//                 (blur support, displacement amplitude). Stages run in order on it; the
//                 rectangle that can be non-transparent grows by each stage's margin, and
//                 only that rectangle is processed.
//   composite   : the work image goes through the ordinary warp (PRE mode: the quad is
//                 extended by M through the same projective map) or a clipped 1:1 blit,
//                 with the premultiplied flavours of the ops (as filtered DRAW_WARP does).
//
// All float math is lane-wise with plain add/mul/div/sqrt (no rcp/rsqrt, no FMA - see
// the note in sr2d_kernels_avx2.cpp), so SSE2 and AVX2 produce identical pixels.

// ------------------------------------------------------------ float helpers
static SR2D_INLINE typename V::F fx_absf(typename V::F a) { return V::castf(V::and_(V::casti(a), V::set1_32(0x7fffffff))); }
static SR2D_INLINE typename V::F fx_lerpf(typename V::F a, typename V::F b, typename V::F t) { return V::addf(a, V::mulf(V::subf(b, a), t)); }

// sin(x), any x with |x| < ~1e5, abs error < 1e-5 (range reduction + odd Taylor to x^9)
static SR2D_INLINE typename V::F fx_sin(typename V::F x)
{
    typedef typename V::F F;
    F q = V::mulf(x, V::set1f(0.15915494309189535f));
    q = V::subf(q, V::floorf_(V::addf(q, V::set1f(0.5f))));                    // [-0.5, 0.5)
    F t = V::mulf(q, V::set1f(6.283185307179586f));                            // [-pi, pi)
    F at = fx_absf(t);
    T big = V::cmpgtf(at, V::set1f(1.5707963267948966f));
    at = V::selectf(big, V::subf(V::set1f(3.141592653589793f), at), at);       // [0, pi/2]
    F t2 = V::mulf(at, at);
    F p = V::set1f(2.7557319223985893e-6f);
    p = V::addf(V::mulf(p, t2), V::set1f(-1.984126984126984e-4f));
    p = V::addf(V::mulf(p, t2), V::set1f(8.333333333333333e-3f));
    p = V::addf(V::mulf(p, t2), V::set1f(-0.16666666666666666f));
    p = V::addf(V::mulf(p, t2), V::set1f(1.0f));
    p = V::mulf(p, at);
    return V::castf(V::xor_(V::casti(p), V::and_(V::casti(t), V::set1_32((int)0x80000000))));
}

// log2(x) for x > 0 (normal floats), rel error ~1e-7 (cephes logf on [sqrt(.5), sqrt(2)))
static SR2D_INLINE typename V::F fx_log2(typename V::F x)
{
    typedef typename V::F F;
    T bits = V::casti(x);
    F e = V::cvt_i2f(V::sub32(V::template srli32<23>(bits), V::set1_32(127)));
    F m = V::castf(V::or_(V::and_(bits, V::set1_32(0x007fffff)), V::set1_32(0x3f800000)));   // [1, 2)
    T hi = V::cmpgtf(m, V::set1f(1.41421356f));
    m = V::selectf(hi, V::mulf(m, V::set1f(0.5f)), m);
    e = V::selectf(hi, V::addf(e, V::set1f(1.0f)), e);
    F f = V::subf(m, V::set1f(1.0f));                                           // [-0.293, 0.414)
    F z = V::mulf(f, f);
    F p = V::set1f(7.0376836292E-2f);
    p = V::addf(V::mulf(p, f), V::set1f(-1.1514610310E-1f));
    p = V::addf(V::mulf(p, f), V::set1f(1.1676998740E-1f));
    p = V::addf(V::mulf(p, f), V::set1f(-1.2420140846E-1f));
    p = V::addf(V::mulf(p, f), V::set1f(1.4249322787E-1f));
    p = V::addf(V::mulf(p, f), V::set1f(-1.6668057665E-1f));
    p = V::addf(V::mulf(p, f), V::set1f(2.0000714765E-1f));
    p = V::addf(V::mulf(p, f), V::set1f(-2.4999993993E-1f));
    p = V::addf(V::mulf(p, f), V::set1f(3.3333331174E-1f));
    p = V::mulf(V::mulf(p, f), z);
    F ln = V::addf(V::addf(f, V::mulf(z, V::set1f(-0.5f))), p);
    return V::addf(V::mulf(ln, V::set1f(1.4426950408889634f)), e);
}

// 2^x for x in [-126, 126] (cephes exp2f, rel error ~1e-7)
static SR2D_INLINE typename V::F fx_exp2(typename V::F x)
{
    typedef typename V::F F;
    x = V::minf(V::maxf(x, V::set1f(-126.0f)), V::set1f(126.0f));
    F n = V::floorf_(V::addf(x, V::set1f(0.5f)));
    F f = V::subf(x, n);                                                        // [-0.5, 0.5]
    F p = V::set1f(1.535336188319500E-4f);
    p = V::addf(V::mulf(p, f), V::set1f(1.339887440266574E-3f));
    p = V::addf(V::mulf(p, f), V::set1f(9.618437357674640E-3f));
    p = V::addf(V::mulf(p, f), V::set1f(5.550332471162809E-2f));
    p = V::addf(V::mulf(p, f), V::set1f(2.402264791363012E-1f));
    p = V::addf(V::mulf(p, f), V::set1f(6.931472028550421E-1f));
    p = V::addf(V::mulf(p, f), V::set1f(1.0f));
    T scale = V::template slli32<23>(V::add32(V::cvtt_f2i(n), V::set1_32(127)));
    return V::mulf(p, V::castf(scale));
}

// ------------------------------------------------------------ value noise (3-D, 2 outputs)
static SR2D_INLINE T fx_mix(T h)
{
    h = V::xor_(h, V::template srli32<13>(h));
    h = V::mullo32(h, V::set1_32(0x5bd1e995));
    return V::xor_(h, V::template srli32<15>(h));
}
static SR2D_INLINE typename V::F fx_h2f(T h)   // hash -> [-1, 1)
{ return V::subf(V::mulf(V::cvt_i2f(V::template srli32<8>(h)), V::set1f(2.0f / 16777216.0f)), V::set1f(1.0f)); }

static SR2D_INLINE typename V::F fx_fade(typename V::F t)   // 6t^5 - 15t^4 + 10t^3
{
    typedef typename V::F F;
    F t3 = V::mulf(V::mulf(t, t), t);
    return V::mulf(t3, V::addf(V::mulf(t, V::subf(V::mulf(t, V::set1f(6.0f)), V::set1f(15.0f))), V::set1f(10.0f)));
}

// two independent smooth noises in [-1, 1) at (px, py, pz); lattice period 2^32
static SR2D_INLINE void fx_vnoise2(typename V::F px, typename V::F py, typename V::F pz, typename V::F& n0, typename V::F& n1)
{
    typedef typename V::F F;
    F fx = V::floorf_(px), fy = V::floorf_(py), fz = V::floorf_(pz);
    T ix = V::cvtt_f2i(fx), iy = V::cvtt_f2i(fy), iz = V::cvtt_f2i(fz);
    F ux = fx_fade(V::subf(px, fx)), uy = fx_fade(V::subf(py, fy)), uz = fx_fade(V::subf(pz, fz));
    const T one = V::set1_32(1);
    T hx0 = V::mullo32(ix, V::set1_32((int)0x8da6b343)), hx1 = V::mullo32(V::add32(ix, one), V::set1_32((int)0x8da6b343));
    T hy0 = V::mullo32(iy, V::set1_32((int)0xd8163841)), hy1 = V::mullo32(V::add32(iy, one), V::set1_32((int)0xd8163841));
    T hz0 = V::mullo32(iz, V::set1_32((int)0xcb1ab31f)), hz1 = V::mullo32(V::add32(iz, one), V::set1_32((int)0xcb1ab31f));
    T c[8];
    c[0] = V::xor_(V::xor_(hx0, hy0), hz0); c[1] = V::xor_(V::xor_(hx1, hy0), hz0);
    c[2] = V::xor_(V::xor_(hx0, hy1), hz0); c[3] = V::xor_(V::xor_(hx1, hy1), hz0);
    c[4] = V::xor_(V::xor_(hx0, hy0), hz1); c[5] = V::xor_(V::xor_(hx1, hy0), hz1);
    c[6] = V::xor_(V::xor_(hx0, hy1), hz1); c[7] = V::xor_(V::xor_(hx1, hy1), hz1);
    const T seeds[2] = { V::set1_32(0x1b873593), V::set1_32((int)0x9e3779b9) };
    for (int s = 0; s < 2; ++s)
    {
        F v[8];
        for (int i = 0; i < 8; ++i) v[i] = fx_h2f(fx_mix(V::xor_(c[i], seeds[s])));
        F x00 = fx_lerpf(v[0], v[1], ux), x10 = fx_lerpf(v[2], v[3], ux), x01 = fx_lerpf(v[4], v[5], ux), x11 = fx_lerpf(v[6], v[7], ux);
        F r = fx_lerpf(fx_lerpf(x00, x10, uy), fx_lerpf(x01, x11, uy), uz);
        if (s == 0) n0 = r; else n1 = r;
    }
}

// ------------------------------------------------------------ displacement generators
// operator()(px, py) -> (dx, dy) in pixels; (px, py) = pattern coordinates (pixel units)
// non-finite / absurd parameters -> harmless values (NaN would poison every sampled pixel)
static SR2D_INLINE float fx_fin(float v, float def = 0.0f) { return (v > -1e9f && v < 1e9f) ? v : def; }

struct GenWave
{
    float cs, sn, k, ph, amp; int longit, cross;
    void begin_row(float, float, float) {}
    GenWave(const SR2D_FxStage& s)
    {
        const float lambda = s.f[0] > 0.01f ? fx_fin(s.f[0], 1e9f) : 0.01f;
        k = 6.283185307179586f / lambda; amp = fx_fin(s.f[1]); ph = fx_fin(s.f[2]);
        // cos/sin of the direction: scalar via the vector routine (keeps both flavours identical)
        SR2D_ALIGN(32) float tmp[8];
        typename V::F a = V::set1f(fx_fin(s.f[3]));
        V::storeu(tmp, V::casti(fx_sin(a))); sn = tmp[0];
        V::storeu(tmp, V::casti(fx_sin(V::addf(a, V::set1f(1.5707963267948966f))))); cs = tmp[0];
        longit = (s.flags & SR2D_FXF_WAVE_LONG) != 0; cross = (s.flags & SR2D_FXF_WAVE_CROSS) != 0;
    }
    SR2D_INLINE void operator()(typename V::F px, typename V::F py, typename V::F& dx, typename V::F& dy) const
    {
        typedef typename V::F F;
        const F vcs = V::set1f(cs), vsn = V::set1f(sn), vk = V::set1f(k), vph = V::set1f(ph), va = V::set1f(amp);
        F s = V::addf(V::mulf(px, vcs), V::mulf(py, vsn));
        F w = V::mulf(va, fx_sin(V::addf(V::mulf(s, vk), vph)));
        // across the travel direction (flag) or along it (compression)
        if (!longit) { dx = V::mulf(w, V::subf(V::set1f(0.0f), vsn)); dy = V::mulf(w, vcs); }
        else         { dx = V::mulf(w, vcs); dy = V::mulf(w, vsn); }
        if (cross)
        {
            F s2 = V::subf(V::mulf(py, vcs), V::mulf(px, vsn));
            F w2 = V::mulf(va, fx_sin(V::addf(V::mulf(s2, vk), vph)));
            if (!longit) { dx = V::addf(dx, V::mulf(w2, vcs)); dy = V::addf(dy, V::mulf(w2, vsn)); }
            else         { dx = V::subf(dx, V::mulf(w2, vsn)); dy = V::addf(dy, V::mulf(w2, vcs)); }
        }
    }
};
struct GenRipple
{
    float cx, cy, k, ph, amp, invR;
    void begin_row(float, float, float) {}
    GenRipple(const SR2D_FxStage& s)
    {
        const float lambda = s.f[0] > 0.01f ? fx_fin(s.f[0], 1e9f) : 0.01f;
        k = 6.283185307179586f / lambda; amp = fx_fin(s.f[1]); ph = fx_fin(s.f[2]); cx = fx_fin(s.f[3]); cy = fx_fin(s.f[4]);
        invR = s.f[5] > 0.0f && s.f[5] < 1e9f ? 1.0f / s.f[5] : 0.0f;
    }
    SR2D_INLINE void operator()(typename V::F px, typename V::F py, typename V::F& dx, typename V::F& dy) const
    {
        typedef typename V::F F;
        F rx = V::subf(px, V::set1f(cx)), ry = V::subf(py, V::set1f(cy));
        F r = V::sqrtf_(V::addf(V::mulf(rx, rx), V::mulf(ry, ry)));
        F inv = V::divf(V::set1f(1.0f), V::maxf(r, V::set1f(1e-3f)));
        F w = V::mulf(V::set1f(amp), fx_sin(V::subf(V::mulf(r, V::set1f(k)), V::set1f(ph))));
        if (invR > 0.0f) w = V::mulf(w, V::maxf(V::subf(V::set1f(1.0f), V::mulf(r, V::set1f(invR))), V::set1f(0.0f)));
        w = V::mulf(w, inv);
        dx = V::mulf(w, rx); dy = V::mulf(w, ry);
    }
};
struct GenNoise
{
    // Per row, the lattice y / z coordinates are constant, so the 8 hashed corner values of a
    // cell depend on the cell's x index only. When a cell is wider than a vector (scale > N-1
    // per octave), the row is pre-hashed into a table (cells x 8 floats, built with the very
    // same vector ops, so every value is bit-identical to the direct evaluation) and each
    // pixel only does the fade + 7 lerps per seed instead of 16 hashes. Bit-exact either way.
    float inv, amp, z; int octaves;
    float* tab; int tabcap;                 // scratch: octaves x tabcap cells x 8 floats (0 = no tables)
    struct Oct { float sc, offx, offy, offz; };
    static Oct oct(int o) { Oct t; if (o == 1) { t.sc = 2.0f; t.offx = 17.3f; t.offy = 9.1f; t.offz = 3.7f; } else if (o == 2) { t.sc = 4.0f; t.offx = 5.9f; t.offy = 31.7f; t.offz = 7.9f; } else { t.sc = 1.0f; t.offx = t.offy = t.offz = 0.0f; } return t; }
    // per-row state
    struct Row { int base, cells; bool on; typename V::F uy, uz; T hyz[4]; };   // hyz[k] = hy(yb) ^ hz(zb), k = zb*2+yb
    Row row[3];
    GenNoise(const SR2D_FxStage& s, int octs, float* t = 0, int cap = 0)
    {
        const float scale = s.f[0] > 0.01f ? fx_fin(s.f[0], 1e9f) : 0.01f;
        inv = 1.0f / scale; z = fx_fin(s.f[2]); octaves = octs; tab = t; tabcap = cap;
        amp = fx_fin(s.f[1]) * (octs == 1 ? 1.0f : 1.0f / 1.75f);    // 1 + 1/2 + 1/4: keep the peak at 'strength'
        for (int o = 0; o < 3; ++o) row[o].on = false;
    }
    // called once per image row: py = pattern y of the row, [pxa, pxb] = pattern x range of the row
    void begin_row(float py, float pxa, float pxb)
    {
        typedef typename V::F F;
        if (!tab) return;
        SR2D_ALIGN(32) int ti[8];
        for (int o = 0; o < octaves; ++o)
        {
            Row& r = row[o];
            const Oct oc = oct(o);
            r.on = false;
            const float step = (float)(N - 1) * inv * oc.sc;          // pattern span of one vector
            if (!(step < 0.999f)) continue;                            // lanes could cover 3+ cells
            // conservative cell range (bounds only - the per-lane index is computed exactly later)
            const float xa = pxa * inv * oc.sc + oc.offx, xb = pxb * inv * oc.sc + oc.offx;
            if (!(xa > -1e8f && xb < 1e8f)) continue;
            const int c0 = (int)(xa < 0 ? xa - 1 : xa) - 2, c1 = (int)(xb < 0 ? xb - 1 : xb) + 3;
            if (c1 - c0 + 1 > tabcap) continue;
            r.base = c0; r.cells = c1 - c0 + 1;
            // lattice y / z of this row, with the very same vector operations as operator()
            F y = V::mulf(V::set1f(py), V::set1f(inv)), pz = V::set1f(z);
            if (o > 0) { y = V::addf(V::mulf(y, V::set1f(oc.sc)), V::set1f(oc.offy)); pz = V::addf(pz, V::set1f(oc.offz)); }
            F fy = V::floorf_(y), fz = V::floorf_(pz);
            T iy = V::cvtt_f2i(fy), iz = V::cvtt_f2i(fz);
            r.uy = fx_fade(V::subf(y, fy)); r.uz = fx_fade(V::subf(pz, fz));
            const T one = V::set1_32(1);
            T hy0 = V::mullo32(iy, V::set1_32((int)0xd8163841)), hy1 = V::mullo32(V::add32(iy, one), V::set1_32((int)0xd8163841));
            T hz0 = V::mullo32(iz, V::set1_32((int)0xcb1ab31f)), hz1 = V::mullo32(V::add32(iz, one), V::set1_32((int)0xcb1ab31f));
            r.hyz[0] = V::xor_(hy0, hz0); r.hyz[1] = V::xor_(hy1, hz0); r.hyz[2] = V::xor_(hy0, hz1); r.hyz[3] = V::xor_(hy1, hz1);
            // table: cell c -> 8 floats, k = seed*4 + zb*2 + yb
            V::storeu(ti, r.hyz[0]); const int k0 = ti[0];
            V::storeu(ti, r.hyz[1]); const int k1 = ti[0];
            V::storeu(ti, r.hyz[2]); const int k2 = ti[0];
            V::storeu(ti, r.hyz[3]); const int k3 = ti[0];
            SR2D_ALIGN(32) int kv[8] = { k0 ^ 0x1b873593, k1 ^ 0x1b873593, k2 ^ 0x1b873593, k3 ^ 0x1b873593,
                                         k0 ^ (int)0x9e3779b9, k1 ^ (int)0x9e3779b9, k2 ^ (int)0x9e3779b9, k3 ^ (int)0x9e3779b9 };
            float* t = tab + (size_t)o * tabcap * 8;
            for (int c = 0; c < r.cells; ++c)
            {
                const T hx = V::mullo32(V::set1_32(c0 + c), V::set1_32((int)0x8da6b343));
#if SR2D_LEVEL >= 2
                V::storeu(t + c * 8, V::casti(fx_h2f(fx_mix(V::xor_(hx, V::loadu(kv))))));
#else
                V::storeu(t + c * 8, V::casti(fx_h2f(fx_mix(V::xor_(hx, V::loadu(kv))))));
                V::storeu(t + c * 8 + 4, V::casti(fx_h2f(fx_mix(V::xor_(hx, V::loadu(kv + 4))))));
#endif
            }
            r.on = true;
        }
    }
    // one octave: table path when the vector's lanes sit in at most two neighbouring cells
    SR2D_INLINE void noise_o(int o, typename V::F x, typename V::F y, typename V::F pz, typename V::F& n0, typename V::F& n1) const
    {
        typedef typename V::F F;
        const Row& r = row[o];
        if (r.on)
        {
            F fx = V::floorf_(x);
            T ix = V::cvtt_f2i(fx);
            SR2D_ALIGN(32) int ti[8];
            V::storeu(ti, ix);
            const int c = ti[0] - r.base, spread = ti[N - 1] - ti[0];
            if (spread >= 0 && spread <= 1 && c >= 0 && c + 2 < r.cells)
            {
                const F ux = fx_fade(V::subf(x, fx));
                const T m = V::cmpeq32(ix, V::set1_32(ti[0]));
                const float* t0 = tab + ((size_t)o * tabcap + c) * 8;
                const float* t1 = t0 + 8; const float* t2 = t0 + 16;
                for (int sd = 0; sd < 2; ++sd)
                {
                    F v[8];
                    for (int k = 0; k < 4; ++k)
                    {   // corners (x0, yb, zb) and (x1, yb, zb) for k = zb*2 + yb
                        const F a = V::set1f(t0[sd * 4 + k]), b = V::set1f(t1[sd * 4 + k]), d = V::set1f(t2[sd * 4 + k]);
                        v[k * 2] = V::selectf(m, a, b); v[k * 2 + 1] = V::selectf(m, b, d);
                    }
                    F x00 = fx_lerpf(v[0], v[1], ux), x10 = fx_lerpf(v[2], v[3], ux), x01 = fx_lerpf(v[4], v[5], ux), x11 = fx_lerpf(v[6], v[7], ux);
                    F res = fx_lerpf(fx_lerpf(x00, x10, r.uy), fx_lerpf(x01, x11, r.uy), r.uz);
                    if (sd == 0) n0 = res; else n1 = res;
                }
                return;
            }
        }
        fx_vnoise2(x, y, pz, n0, n1);
    }
    SR2D_INLINE void operator()(typename V::F px, typename V::F py, typename V::F& dx, typename V::F& dy) const
    {
        typedef typename V::F F;
        F n0, n1;
        F x = V::mulf(px, V::set1f(inv)), y = V::mulf(py, V::set1f(inv)), pz = V::set1f(z);
        noise_o(0, x, y, pz, n0, n1);
        if (octaves > 1)
        {
            F m0, m1;
            noise_o(1, V::addf(V::mulf(x, V::set1f(2.0f)), V::set1f(17.3f)), V::addf(V::mulf(y, V::set1f(2.0f)), V::set1f(9.1f)), V::addf(pz, V::set1f(3.7f)), m0, m1);
            n0 = V::addf(n0, V::mulf(m0, V::set1f(0.5f))); n1 = V::addf(n1, V::mulf(m1, V::set1f(0.5f)));
            noise_o(2, V::addf(V::mulf(x, V::set1f(4.0f)), V::set1f(5.9f)), V::addf(V::mulf(y, V::set1f(4.0f)), V::set1f(31.7f)), V::addf(pz, V::set1f(7.9f)), m0, m1);
            n0 = V::addf(n0, V::mulf(m0, V::set1f(0.25f))); n1 = V::addf(n1, V::mulf(m1, V::set1f(0.25f)));
        }
        dx = V::mulf(n0, V::set1f(amp)); dy = V::mulf(n1, V::set1f(amp));
    }
};
// gradient of a height texture: gmap = (mw+2) x (mh+2) BGRA, bytes B = dh/dx, G = dh/dy (128 = 0),
// wrapped border of one texel on every side so the bilinear tap never needs clamping.
// (Kept as one int per texel so the whole thing fits the 32-bit gathers.)
struct GenMap
{
    const int* g; int mw, mh; float inv, ox, oy, amp;
    void begin_row(float, float, float) {}
    GenMap(const SR2D_FxStage& s, const int* gmap)
    {
        g = gmap; mw = s.mw; mh = s.mh;
        const float scale = s.f[0] > 0.001f ? fx_fin(s.f[0], 1e9f) : 0.001f;
        inv = 1.0f / scale; ox = fx_fin(s.f[2]); oy = fx_fin(s.f[3]); amp = fx_fin(s.f[1]) / 127.5f;
    }
    SR2D_INLINE void operator()(typename V::F px, typename V::F py, typename V::F& dx, typename V::F& dy) const
    {
        typedef typename V::F F;
        F tx = V::mulf(V::addf(V::addf(px, V::set1f(0.5f)), V::set1f(ox)), V::set1f(inv));
        F ty = V::mulf(V::addf(V::addf(py, V::set1f(0.5f)), V::set1f(oy)), V::set1f(inv));
        const F fw = V::set1f((float)mw), fh = V::set1f((float)mh);
        tx = V::subf(tx, V::mulf(V::floorf_(V::mulf(tx, V::set1f(1.0f / (float)mw))), fw));   // [0, mw)
        ty = V::subf(ty, V::mulf(V::floorf_(V::mulf(ty, V::set1f(1.0f / (float)mh))), fh));
        tx = V::minf(V::maxf(tx, V::set1f(0.0f)), V::set1f((float)mw - 0.001f));            // fp guard
        ty = V::minf(V::maxf(ty, V::set1f(0.0f)), V::set1f((float)mh - 0.001f));
        // bilinear tap in float (the byte lerp of warp_sample would quantise the gradient to
        // ~1/128 of the strength, visible as jitter on hard edges); the +1 is the border texel
        F u = V::addf(tx, V::set1f(0.5f)), w = V::addf(ty, V::set1f(0.5f));
        T iu = V::cvtt_f2i(u), iv = V::cvtt_f2i(w);
        F fu = V::subf(u, V::cvt_i2f(iu)), fv = V::subf(w, V::cvt_i2f(iv));
        const T gw = V::set1_32(mw + 2), one = V::set1_32(1);
        T r0 = V::add32(V::mullo32(iv, gw), iu), r1 = V::add32(r0, gw);
        T v00 = gather32_s(g, r0), v10 = gather32_s(g, V::add32(r0, one));
        T v01 = gather32_s(g, r1), v11 = gather32_s(g, V::add32(r1, one));
        const T m8 = V::set1_32(255);
        F gx = fx_lerpf(fx_lerpf(V::cvt_i2f(V::and_(v00, m8)), V::cvt_i2f(V::and_(v10, m8)), fu),
                        fx_lerpf(V::cvt_i2f(V::and_(v01, m8)), V::cvt_i2f(V::and_(v11, m8)), fu), fv);
        F gy = fx_lerpf(fx_lerpf(V::cvt_i2f(V::and_(V::template srli32<8>(v00), m8)), V::cvt_i2f(V::and_(V::template srli32<8>(v10), m8)), fu),
                        fx_lerpf(V::cvt_i2f(V::and_(V::template srli32<8>(v01), m8)), V::cvt_i2f(V::and_(V::template srli32<8>(v11), m8)), fu), fv);
        dx = V::mulf(V::subf(gx, V::set1f(128.0f)), V::set1f(amp));
        dy = V::mulf(V::subf(gy, V::set1f(128.0f)), V::set1f(amp));
    }
};

// build the gradient map of a height texture (luminance or alpha)
static void fx_build_gmap(const int* map, int mw, int mh, bool alpha, int* g)
{
    const int GW = mw + 2;
    for (int y = -1; y <= mh; ++y)
    {
        const int yy = (y + mh) % mh, ym = (y - 1 + 2 * mh) % mh, yp = (y + 1) % mh;
        const int* r0 = map + (size_t)yy * mw; const int* rm = map + (size_t)ym * mw; const int* rp = map + (size_t)yp * mw;
        int* o = g + (size_t)(y + 1) * GW;
        for (int x = -1; x <= mw; ++x)
        {
            const int xx = (x + mw) % mw, xm = (x - 1 + 2 * mw) % mw, xp = (x + 1) % mw;
            int hl, hr, hu, hd;
            if (alpha) { hl = (int)((uint32_t)r0[xm] >> 24); hr = (int)((uint32_t)r0[xp] >> 24); hu = (int)((uint32_t)rm[xx] >> 24); hd = (int)((uint32_t)rp[xx] >> 24); }
            else
            {
#define SR2D_LUM(p) ((77 * (((p) >> 16) & 255) + 150 * (((p) >> 8) & 255) + 29 * ((p) & 255)) >> 8)
                hl = SR2D_LUM(r0[xm]); hr = SR2D_LUM(r0[xp]); hu = SR2D_LUM(rm[xx]); hd = SR2D_LUM(rp[xx]);
#undef SR2D_LUM
            }
            const int gx = (hr - hl + 256) >> 1, gy = (hd - hu + 256) >> 1;   // [0, 255]
            o[x + 1] = gx | (gy << 8);
        }
    }
}

// ------------------------------------------------------------ distortion pass
// out(x, y) = in sampled at (x + dx, y + dy) over the rectangle; pattern coords = (x + ox, y + oy)
template<int MODE, class Gen>
static void fx_distort(const int* in, int* out, int W, int H, int x0, int y0, int x1, int y1, float ox, float oy, Gen& gen)
{
    typedef typename V::F F;
    SR2D_ALIGN(32) int lane_i[8] = { 0, 1, 2, 3, 4, 5, 6, 7 };
    const F lanes = V::cvt_i2f(V::loadu(lane_i));
    const F half = V::set1f(0.5f), vox = V::set1f(ox), voy = V::set1f(oy);
    SR2D_ALIGN(32) int tmp[8];
    for (int y = y0; y < y1; ++y)
    {
        const F fy = V::set1f((float)y), py = V::addf(fy, voy), sy = V::addf(fy, half);
        gen.begin_row((float)y + oy, (float)x0 + ox, (float)(x1 - 1 + N) + ox);   // last vector may run past x1
        int* o = out + (size_t)y * W;
        for (int x = x0; x < x1; x += N)
        {
            F fx = V::addf(V::set1f((float)x), lanes);
            F dx, dy;
            gen(V::addf(fx, vox), py, dx, dy);
            T s = warp_sample<MODE>(in, W, H, V::addf(V::addf(fx, half), dx), V::addf(sy, dy));
            if (x + N <= x1) V::storeu(o + x, s);
            else { V::storeu(tmp, s); for (int i = 0; i < x1 - x; ++i) o[x + i] = tmp[i]; }
        }
    }
}

template<class Gen>
static void fx_distort_mode(int mode, const int* in, int* out, int W, int H, int x0, int y0, int x1, int y1, float ox, float oy, Gen gen)
{
    if (mode == 2)      fx_distort<2>(in, out, W, H, x0, y0, x1, y1, ox, oy, gen);
    else if (mode == 1) fx_distort<1>(in, out, W, H, x0, y0, x1, y1, ox, oy, gen);
    else                fx_distort<0>(in, out, W, H, x0, y0, x1, y1, ox, oy, gen);
}

// ------------------------------------------------------------ motion blur
// out(x, y) = the average of n taps of `in` along the trail: sample k at (x - offs[2k], y - offs[2k+1]).
// offs = n float pairs precomputed by the dispatch (straight direction or a walked polyline), sample 0
// sits ON the pixel, so the sharp picture is part of the average. Bytes are premultiplied - the
// average of premultiplied pixels is premultiplied again, so the trail fades correctly over transparency.
template<int MODE>
static void fx_motion(const int* in, int* out, int W, int H, int x0, int y0, int x1, int y1, const float* offs, int n)
{
    typedef typename V::F F;
    SR2D_ALIGN(32) int lane_i[8] = { 0, 1, 2, 3, 4, 5, 6, 7 };
    const F lanes = V::cvt_i2f(V::loadu(lane_i));
    const F half = V::set1f(0.5f), inv = V::set1f(1.0f / (float)n);
    const T m255 = V::set1_32(255);
    SR2D_ALIGN(32) int tmp[8];
    for (int y = y0; y < y1; ++y)
    {
        const F sy = V::addf(V::set1f((float)y), half);
        int* o = out + (size_t)y * W;
        for (int x = x0; x < x1; x += N)
        {
            const F fx = V::addf(V::set1f((float)x), lanes);
            // per-channel accumulators: summing PACKED bytes would carry 0x100 into the next channel
            T aa = V::zero(), rr = V::zero(), gg = V::zero(), bb = V::zero();
            for (int k = 0; k < n; ++k)
            {
                const T s = warp_sample<MODE>(in, W, H, V::subf(V::addf(fx, half), V::set1f(offs[k * 2])),
                                                               V::subf(sy, V::set1f(offs[k * 2 + 1])));
                aa = V::add32(aa, V::template srli32<24>(s));
                rr = V::add32(rr, V::and_(V::template srli32<16>(s), m255));
                gg = V::add32(gg, V::and_(V::template srli32<8>(s), m255));
                bb = V::add32(bb, V::and_(s, m255));
            }
            const T a = V::cvtt_f2i(V::addf(V::mulf(V::cvt_i2f(aa), inv), half));
            const T r = V::cvtt_f2i(V::addf(V::mulf(V::cvt_i2f(rr), inv), half));
            const T g = V::cvtt_f2i(V::addf(V::mulf(V::cvt_i2f(gg), inv), half));
            const T b = V::cvtt_f2i(V::addf(V::mulf(V::cvt_i2f(bb), inv), half));
            const T res = V::or_(V::or_(V::template slli32<24>(a), V::template slli32<16>(r)), V::or_(V::template slli32<8>(g), b));
            if (x + N <= x1) V::storeu(o + x, res);
            else { V::storeu(tmp, res); for (int i = 0; i < x1 - x; ++i) o[x + i] = tmp[i]; }
        }
    }
}

static SR2D_INLINE int fx_sample_mode(int flags)
{
    if (flags & SR2D_FXF_SAMPLE_BICUBIC) return 2;
    if (flags & SR2D_FXF_SAMPLE_NEAREST) return 0;
    return 1;
}
static SR2D_INLINE float fx_fabs(float v) { return v < 0 ? -v : v; }
static SR2D_INLINE int fx_ceil(float v) { int i = (int)v; return (float)i < v ? i + 1 : i; }

// ------------------------------------------------------------ colour stage
struct FxColor
{
    float m[9], off[3];      // straight-colour affine map, RGB order: C' = M C + off (units of full scale)
    float gamma, opacity;
    bool do_gamma, do_matrix, noop;
};
static void fx_mat_mul(const double* a, double* m, double* off)    // (m, off) <- a * (m, off)
{
    double r[9], ro[3];
    for (int i = 0; i < 3; ++i)
    {
        for (int j = 0; j < 3; ++j) r[i * 3 + j] = a[i * 3] * m[j] + a[i * 3 + 1] * m[3 + j] + a[i * 3 + 2] * m[6 + j];
        ro[i] = a[i * 3] * off[0] + a[i * 3 + 1] * off[1] + a[i * 3 + 2] * off[2];
    }
    for (int i = 0; i < 9; ++i) m[i] = r[i];
    for (int i = 0; i < 3; ++i) off[i] = ro[i];
}
static FxColor fx_color_setup(const SR2D_FxStage& s)
{
    FxColor c;
    double m[9] = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, off[3] = { 0, 0, 0 };
    const float bright = fx_fin(s.f[0]), contrast = fx_fin(s.f[1], 1.0f), sat = fx_fin(s.f[2], 1.0f), gamma = fx_fin(s.f[3], 1.0f), hue = fx_fin(s.f[4]), tint = fx_fin(s.f[6]);
    // brightness
    for (int i = 0; i < 3; ++i) off[i] += bright;
    // contrast around mid grey
    for (int i = 0; i < 9; ++i) m[i] *= contrast;
    for (int i = 0; i < 3; ++i) off[i] = off[i] * contrast + 0.5 * (1.0 - contrast);
    // saturation (Rec.601 luma)
    if (sat != 1.0f)
    {
        const double lr = 0.299, lg = 0.587, lb = 0.114, t = sat, u = 1.0 - sat;
        const double a[9] = { u * lr + t, u * lg, u * lb, u * lr, u * lg + t, u * lb, u * lr, u * lg, u * lb + t };
        fx_mat_mul(a, m, off);
    }
    // hue rotation (SVG feColorMatrix hueRotate)
    if (hue != 0.0f)
    {
        SR2D_ALIGN(32) float tmp[8];
        typename V::F ang = V::set1f(hue * 0.017453292519943295f);
        V::storeu(tmp, V::casti(fx_sin(ang))); const double sn = tmp[0];
        V::storeu(tmp, V::casti(fx_sin(V::addf(ang, V::set1f(1.5707963267948966f))))); const double cs = tmp[0];
        const double a[9] = {
            0.213 + cs * 0.787 - sn * 0.213, 0.715 - cs * 0.715 - sn * 0.715, 0.072 - cs * 0.072 + sn * 0.928,
            0.213 - cs * 0.213 + sn * 0.143, 0.715 + cs * 0.285 + sn * 0.140, 0.072 - cs * 0.072 - sn * 0.283,
            0.213 - cs * 0.213 - sn * 0.787, 0.715 - cs * 0.715 + sn * 0.715, 0.072 + cs * 0.928 + sn * 0.072 };
        fx_mat_mul(a, m, off);
    }
    // tint
    if (tint != 0.0f)
    {
        const double t = tint, u = 1.0 - tint;
        const double tr = ((s.i[0] >> 16) & 255) / 255.0, tg = ((s.i[0] >> 8) & 255) / 255.0, tb = (s.i[0] & 255) / 255.0;
        for (int i = 0; i < 9; ++i) m[i] *= u;
        off[0] = off[0] * u + t * tr; off[1] = off[1] * u + t * tg; off[2] = off[2] * u + t * tb;
    }
    // invert
    if (s.flags & SR2D_FXF_INVERT)
    {
        for (int i = 0; i < 9; ++i) m[i] = -m[i];
        for (int i = 0; i < 3; ++i) off[i] = 1.0 - off[i];
    }
    for (int i = 0; i < 9; ++i) c.m[i] = (float)m[i];
    for (int i = 0; i < 3; ++i) c.off[i] = (float)off[i];
    c.gamma = 1.0f / (gamma > 0.01f ? gamma : 0.01f);     // exponent: gamma > 1 brightens (Levels-style)
    c.opacity = !(s.f[5] > 0) ? 0.0f : s.f[5] > 1 ? 1.0f : s.f[5];    // NaN -> 0
    c.do_gamma = fx_fabs(c.gamma - 1.0f) > 1e-6f;
    bool ident = true;
    for (int i = 0; i < 9; ++i) if (fx_fabs(c.m[i] - ((i % 4) == 0 ? 1.0f : 0.0f)) > 1e-6f) ident = false;
    for (int i = 0; i < 3; ++i) if (fx_fabs(c.off[i]) > 1e-6f) ident = false;
    c.do_matrix = !ident;
    c.noop = !c.do_gamma && !c.do_matrix && c.opacity >= 1.0f;
    return c;
}

static void fx_color(int* img, int W, int x0, int y0, int x1, int y1, const FxColor& c)
{
    typedef typename V::F F;
    SR2D_ALIGN(32) int tmp[8];
    if (!c.do_gamma && !c.do_matrix)
    {   // opacity only: all four bytes * k / 256
        const int k = (int)(c.opacity * 256.0f + 0.5f);
        const T vk = V::set1_16((short)k), rnd = V::set1_16(128);
        for (int y = y0; y < y1; ++y)
        {
            int* p = img + (size_t)y * W;
            for (int x = x0; x < x1; x += N)
            {
                T v = x + N <= x1 ? V::loadu(p + x) : (memcpy(tmp, p + x, (size_t)(x1 - x) * 4), V::loadu(tmp));
                T lo = V::template srli16<8>(V::add16(V::mullo16(V::lo8to16(v), vk), rnd));
                T hi = V::template srli16<8>(V::add16(V::mullo16(V::hi8to16(v), vk), rnd));
                v = V::packus16(lo, hi);
                if (x + N <= x1) V::storeu(p + x, v);
                else { V::storeu(tmp, v); for (int i = 0; i < x1 - x; ++i) p[x + i] = tmp[i]; }
            }
        }
        return;
    }
    const T m255 = V::set1_32(255);
    const F zero = V::set1f(0.0f), one = V::set1f(1.0f);
    const F vop = V::set1f(c.opacity), vg = V::set1f(c.gamma);
    const F m00 = V::set1f(c.m[0]), m01 = V::set1f(c.m[1]), m02 = V::set1f(c.m[2]);
    const F m10 = V::set1f(c.m[3]), m11 = V::set1f(c.m[4]), m12 = V::set1f(c.m[5]);
    const F m20 = V::set1f(c.m[6]), m21 = V::set1f(c.m[7]), m22 = V::set1f(c.m[8]);
    const F o0 = V::set1f(c.off[0]), o1 = V::set1f(c.off[1]), o2 = V::set1f(c.off[2]);
    for (int y = y0; y < y1; ++y)
    {
        int* p = img + (size_t)y * W;
        for (int x = x0; x < x1; x += N)
        {
            T v = x + N <= x1 ? V::loadu(p + x) : (memcpy(tmp, p + x, (size_t)(x1 - x) * 4), V::loadu(tmp));
            F b = V::cvt_i2f(V::and_(v, m255));
            F g = V::cvt_i2f(V::and_(V::template srli32<8>(v), m255));
            F r = V::cvt_i2f(V::and_(V::template srli32<16>(v), m255));
            F a = V::cvt_i2f(V::template srli32<24>(v));
            if (c.do_gamma)
            {   // straight C = c / a in [0,1] -> C^(1/gamma) -> back
                F inva = V::divf(one, V::maxf(a, one));
                F lo = V::set1f(1e-7f);
                r = V::mulf(fx_exp2(V::mulf(vg, fx_log2(V::maxf(V::mulf(r, inva), lo)))), a);
                g = V::mulf(fx_exp2(V::mulf(vg, fx_log2(V::maxf(V::mulf(g, inva), lo)))), a);
                b = V::mulf(fx_exp2(V::mulf(vg, fx_log2(V::maxf(V::mulf(b, inva), lo)))), a);
            }
            if (c.do_matrix)
            {   // premultiplied: offsets scale with alpha; clamp to [0, a] keeps the pixel valid
                F r2 = V::addf(V::addf(V::mulf(m00, r), V::mulf(m01, g)), V::addf(V::mulf(m02, b), V::mulf(o0, a)));
                F g2 = V::addf(V::addf(V::mulf(m10, r), V::mulf(m11, g)), V::addf(V::mulf(m12, b), V::mulf(o1, a)));
                F b2 = V::addf(V::addf(V::mulf(m20, r), V::mulf(m21, g)), V::addf(V::mulf(m22, b), V::mulf(o2, a)));
                r = V::minf(V::maxf(r2, zero), a); g = V::minf(V::maxf(g2, zero), a); b = V::minf(V::maxf(b2, zero), a);
            }
            else { r = V::minf(V::maxf(r, zero), a); g = V::minf(V::maxf(g, zero), a); b = V::minf(V::maxf(b, zero), a); }
            if (c.opacity < 1.0f) { r = V::mulf(r, vop); g = V::mulf(g, vop); b = V::mulf(b, vop); a = V::mulf(a, vop); }
            T o = V::or_(V::or_(V::cvt_f2i_round(b), V::template slli32<8>(V::cvt_f2i_round(g))),
                         V::or_(V::template slli32<16>(V::cvt_f2i_round(r)), V::template slli32<24>(V::cvt_f2i_round(a))));
            if (x + N <= x1) V::storeu(p + x, o);
            else { V::storeu(tmp, o); for (int i = 0; i < x1 - x; ++i) p[x + i] = tmp[i]; }
        }
    }
}

// ------------------------------------------------------------ load / composite helpers
static SR2D_INLINE void fx_load_row(const int* s, int* d, int n, bool premul, bool opaque)
{
    if (opaque)
    {
        const T am = V::set1_32((int)0xff000000);
        int i = 0;
        for (; i + N <= n; i += N) V::storeu(d + i, V::or_(V::loadu(s + i), am));
        for (; i < n; ++i) d[i] = s[i] | (int)0xff000000;
    }
    else if (premul) copy_px(s, d, n);
    else warp_premul_copy(s, d, n);
}

template<class Op>
static void fx_blit(const int* work, int W, int H, int* dst, int dw, int cl, int ct, int cr, int cb, int ox, int oy, const Op& op)
{
    const long long ex = (long long)ox + W, ey = (long long)oy + H;      // no int overflow for far-away positions
    int x0 = ox > cl ? ox : cl, y0 = oy > ct ? oy : ct;
    int x1 = ex < cr ? (int)ex : cr, y1 = ey < cb ? (int)ey : cb;
    if (x0 >= x1 || y0 >= y1) return;
    rect(work + (size_t)(y0 - oy) * W + (x0 - ox), dst + (size_t)y0 * dw + x0, x1 - x0, y1 - y0, W, dw, op);
}

// premultiplied work image -> destination. quad == 0: 1:1 at (ox, oy) (no polygon).
template<class Op>
static void fx_out(const Op& op, int opcode, const int* work, int W, int H, int* dst, int dw, int cl, int ct, int cr, int cb,
                   int ox, int oy, const float* quad, const float* poly, int npoly, int flags)
{
    if (!quad) { fx_blit(work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, op); return; }
    warp_op(op, work, W, H, dst, dw, cl, ct, cr, cb, quad, poly, npoly, flags, opcode, true);
}
static void fx_composite(int opcode, int k, const int* work, int W, int H, int* dst, int dw, int cl, int ct, int cr, int cb,
                         int ox, int oy, const float* quad, const float* poly, int npoly, int flags)
{
    if (is_blend_mode(opcode)) { with_blend_mode(opcode | SR2D_OP_SRC_PREMUL, [&](const auto& f) { fx_out(f, 1, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); }); return; }
    switch (opcode & 0xff)
    {
    case 1:  fx_out(OpPaint(),          opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 2:  fx_out(OpAlphaTPremul(),   opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 4:  fx_out(OpAdd2D(),          opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 5:  fx_out(OpAdd(),            opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 6:  fx_out(OpMod(),            opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 7:  fx_out(OpMod2X(),          opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 8:  fx_out(OpMax(),            opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 9:  fx_out(OpMin(),            opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 10: fx_out(OpBlend(k),         opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 11: fx_out(OpAlphaOver(),      opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    case 12: fx_out(OpUnpremul(),       opcode, work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    default: fx_out(OpAlphaOverKeepA(), 3,      work, W, H, dst, dw, cl, ct, cr, cb, ox, oy, quad, poly, npoly, flags); break;
    }
}

// forward projective map of the quad, applied to the sprite rectangle grown by M pixels
static bool fx_extend_quad(const float* q, int sw, int sh, int M, float* out)
{
    const double x0 = q[0], y0 = q[1], x1 = q[2], y1 = q[3], x2 = q[4], y2 = q[5], x3 = q[6], y3 = q[7];
    const double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
    const double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
    double a, b, c, d, e, f, g = 0, h = 0, scale = 0;
    for (int i = 0; i < 8; ++i) { double v = q[i] < 0 ? -q[i] : q[i]; if (v > scale) scale = v; }
    const double eps = 1e-9 * (scale + 1.0);
    if ((dx3 > -eps && dx3 < eps) && (dy3 > -eps && dy3 < eps))
    { a = x1 - x0; b = x3 - x0; c = x0; d = y1 - y0; e = y3 - y0; f = y0; }
    else
    {
        const double den = dx1 * dy2 - dx2 * dy1;
        if (den > -1e-300 && den < 1e-300) return false;
        g = (dx3 * dy2 - dx2 * dy3) / den; h = (dx1 * dy3 - dx3 * dy1) / den;
        a = x1 - x0 + g * x1; b = x3 - x0 + h * x3; c = x0;
        d = y1 - y0 + g * y1; e = y3 - y0 + h * y3; f = y0;
    }
    const double mu = (double)M / sw, mv = (double)M / sh;
    const double uu[4] = { -mu, 1 + mu, 1 + mu, -mu }, vv[4] = { -mv, -mv, 1 + mv, 1 + mv };
    for (int i = 0; i < 4; ++i)
    {
        const double w = g * uu[i] + h * vv[i] + 1.0;
        if (w < 1e-6) return false;                        // extension crosses the horizon
        out[i * 2] = (float)((a * uu[i] + b * vv[i] + c) / w);
        out[i * 2 + 1] = (float)((d * uu[i] + e * vv[i] + f) / w);
    }
    return true;
}

// axis-aligned, unscaled quad at an integer position?
static bool fx_is_blit(const float* q, int sw, int sh, int& ix, int& iy)
{
    const float x = q[0], y = q[1];
    if (!(fx_fabs(x) < 1e7f) || !(fx_fabs(y) < 1e7f)) return false;     // NaN / huge: not a blit (the warp rejects it)
    const float rx = (float)(int)(x + (x < 0 ? -0.5f : 0.5f)), ry = (float)(int)(y + (y < 0 ? -0.5f : 0.5f));
    if (fx_fabs(x - rx) > 1e-3f || fx_fabs(y - ry) > 1e-3f) return false;
    const float ex = rx + (float)sw, ey = ry + (float)sh, t = 1e-3f;
    if (fx_fabs(q[2] - ex) > t || fx_fabs(q[3] - ry) > t || fx_fabs(q[4] - ex) > t || fx_fabs(q[5] - ey) > t ||
        fx_fabs(q[6] - rx) > t || fx_fabs(q[7] - ey) > t) return false;
    ix = (int)rx; iy = (int)ry;
    return true;
}

// ------------------------------------------------------------ morphology (dilate / erode)
// Per-channel max (min) of the premultiplied image over a disc (or square) of radius r.
// Alpha grows/shrinks and the colour follows it, so the result stays a valid premultiplied
// picture. The disc is the union of horizontal runs of half-width hw[dy] = floor(sqrt(r^2 -
// dy^2) + 0.5): out(x, y) = op over dy of rowmax_{hw[dy]}(in)(x, y + dy). Each rowmax is a
// van Herk / Gil-Werman two-sweep (3 byte-ops per pixel whatever the width), and the disc
// needs one per distinct half-width per source row, so the cost is ~O(r) per pixel - fine for
// outline-sized radii (1..8 px: 0.2-1 ms on a 256^2 sprite); the square is fully separable.
template<bool MAXOP> static SR2D_INLINE T fx_mm(T a, T b) { return MAXOP ? V::max_u8(a, b) : V::min_u8(a, b); }

// running max/min over the window [x-h, x+h] along a row of n pixels; outside = identity element
template<bool MAXOP>
static void fx_mm_row(const int* in, int* out, int n, int h, int* P, int* S)
{
    if (h <= 0) { if (out != in) memcpy(out, in, (size_t)n * sizeof(int)); return; }
    const int w = 2 * h + 1, ident = MAXOP ? 0 : -1;
    SR2D_ALIGN(32) int t[8];
    for (int b = 0; b < n; b += w)
    {
        const int e = b + w < n ? b + w : n;
        T acc = V::set1_32(ident);
        for (int i = b; i < e; ++i) { acc = fx_mm<MAXOP>(acc, V::set1_32(in[i])); V::storeu(t, acc); P[i] = t[0]; }
        acc = V::set1_32(ident);
        for (int i = e - 1; i >= b; --i) { acc = fx_mm<MAXOP>(acc, V::set1_32(in[i])); V::storeu(t, acc); S[i] = t[0]; }
    }
    SR2D_ALIGN(32) int a[8], c[8];
    for (int i = 0; i < n; i += N)
    {
        for (int l = 0; l < N; ++l)
        {   // beyond the row the image is transparent: max ignores it, min is pulled to 0
            const int x = i + l, xs = x - h, xp = x + h;
            a[l] = (x < n && xs >= 0) ? S[xs] : 0;
            c[l] = (x < n && xp < n) ? P[xp] : 0;
        }
        const T r = fx_mm<MAXOP>(V::loadu(a), V::loadu(c));
        if (i + N <= n) V::storeu(out + i, r); else { V::storeu(t, r); for (int l = 0; l < n - i; ++l) out[i + l] = t[l]; }
    }
}

// scalar per-pixel op for the row tails
template<bool MAXOP> static SR2D_INLINE int fx_mm1(int a, int b)
{
    SR2D_ALIGN(32) int t[8];
    V::storeu(t, fx_mm<MAXOP>(V::set1_32(a), V::set1_32(b)));
    return t[0];
}
// fold: o[i] = op(o[i], v[i]) for n pixels
template<bool MAXOP> static SR2D_INLINE void fx_mm_fold(int* o, const int* v, int n)
{
    int i = 0;
    for (; i + N <= n; i += N) V::storeu(o + i, fx_mm<MAXOP>(V::loadu(o + i), V::loadu(v + i)));
    for (; i < n; ++i) o[i] = fx_mm1<MAXOP>(o[i], v[i]);
}
static SR2D_INLINE int fx_isqrt_round(int v)      // floor(sqrt(v) + 0.5) without CRT math
{
    int r = 0;
    while ((r + 1) * (r + 1) <= v) ++r;              // r = floor(sqrt(v)), v <= 65536 -> <= 257 steps
    return (4 * v >= (2 * r + 1) * (2 * r + 1)) ? r + 1 : r;   // round: sqrt(v) >= r + 0.5 ?
}

// img (W x H) -> out, over [x0,x1) x [y0,y1) (already grown by r). scratch: 4 * max(W, H) ints.
template<bool MAXOP>
static void fx_morph(const int* img, int* out, int W, int H, int x0, int y0, int x1, int y1, int r, bool square, int* scratch)
{
    const int n = x1 - x0, hn = y1 - y0;
    if (n <= 0 || hn <= 0) return;
    const int L = W > H ? W : H;
    int* P = scratch; int* S = scratch + L; int* cin = scratch + 2 * L; int* cout_ = scratch + 3 * L;
    const int ident = MAXOP ? 0 : -1;
    if (square)
    {   // separable: rows into out, then columns in place
        for (int y = y0; y < y1; ++y) fx_mm_row<MAXOP>(img + (size_t)y * W + x0, out + (size_t)y * W + x0, n, r, P, S);
        for (int x = x0; x < x1; ++x)
        {
            for (int y = 0; y < hn; ++y) cin[y] = out[(size_t)(y0 + y) * W + x];
            fx_mm_row<MAXOP>(cin, cout_, hn, r, P, S);
            for (int y = 0; y < hn; ++y) out[(size_t)(y0 + y) * W + x] = cout_[y];
        }
        return;
    }
    // disc: out(y) = op over d of rowop_{hw[d]}(img(y +- d)); one rowop per (source row, distinct hw)
    int hw[257];
    for (int d = 0; d <= r; ++d) hw[d] = fx_isqrt_round(r * r - d * d);
    for (int y = y0; y < y1; ++y) { int* o = out + (size_t)y * W + x0; for (int i = 0; i < n; ++i) o[i] = ident; }
    for (int y = y0; y < y1; ++y)
    {
        const int* src = img + (size_t)y * W + x0;
        int last = -1;
        for (int d = 0; d <= r; ++d)
        {
            if (hw[d] != last) { fx_mm_row<MAXOP>(src, cin, n, hw[d], P, S); last = hw[d]; }
            if (y - d >= y0) fx_mm_fold<MAXOP>(out + (size_t)(y - d) * W + x0, cin, n);
            if (d && y + d < y1) fx_mm_fold<MAXOP>(out + (size_t)(y + d) * W + x0, cin, n);
        }
    }
}

// ------------------------------------------------------------ shadow / glow
// tint the alpha shape: every pixel -> (a, a*R, a*G, a*B) * opacity (stays premultiplied)
static void fx_shadow_tint(int* img, int W, int x0, int y0, int x1, int y1, int rgb, float opacity)
{
    const int op = (int)(opacity * 256.0f + 0.5f);
    const int R = (rgb >> 16) & 255, G = (rgb >> 8) & 255, B = rgb & 255;
    SR2D_ALIGN(32) short mul[16];      // per byte: B G R A, byte' = a * mul >> 8
    for (int i = 0; i < 4; ++i) { mul[i * 4] = (short)((B * op + 127) / 255); mul[i * 4 + 1] = (short)((G * op + 127) / 255); mul[i * 4 + 2] = (short)((R * op + 127) / 255); mul[i * 4 + 3] = (short)op; }
    for (int i = 8; i < 16; ++i) mul[i] = mul[i - 8];
    const T m16 = V::loadu(mul), rnd = V::set1_16(128);
    SR2D_ALIGN(32) int tmp[8];
    for (int y = y0; y < y1; ++y)
    {
        int* p = img + (size_t)y * W;
        for (int x = x0; x < x1; x += N)
        {
            T v = x + N <= x1 ? V::loadu(p + x) : (memcpy(tmp, p + x, (size_t)(x1 - x) * 4), V::loadu(tmp));
            T a = V::template srli32<24>(v);
            a = V::or_(a, V::template slli32<8>(a)); a = V::or_(a, V::template slli32<16>(a));       // alpha in all 4 bytes
            T lo = V::template srli16<8>(V::add16(V::mullo16(V::lo8to16(a), m16), rnd));
            T hi = V::template srli16<8>(V::add16(V::mullo16(V::hi8to16(a), m16), rnd));
            v = V::packus16(lo, hi);
            if (x + N <= x1) V::storeu(p + x, v);
            else { V::storeu(tmp, v); for (int i = 0; i < x1 - x; ++i) p[x + i] = tmp[i]; }
        }
    }
}
// dst = picture OVER the shadow shifted by (dx, dy)  |  GLOW: picture + shadow (saturating add)
// only: the picture is dropped (shadow / glow alone). rowbuf: W ints. dst may alias pic.
template<bool GLOW>
static void fx_shadow_merge(const int* pic, const int* sh, int* dst, int W, int H, int x0, int y0, int x1, int y1, int dx, int dy, bool only, int* rowbuf)
{
    const OpAlphaOver over; const OpAdd add;
    const int n = x1 - x0;
    SR2D_ALIGN(32) int ts[8], td[8];
    for (int y = y0; y < y1; ++y)
    {
        // shifted shadow row, zero where it does not exist
        const int sy = y - dy;
        memset(rowbuf, 0, (size_t)n * sizeof(int));
        if (sy >= 0 && sy < H)
        {
            int a = x0 - dx, b = x1 - dx;                 // source x range
            if (a < 0) a = 0;
            if (b > W) b = W;
            if (b > a) memcpy(rowbuf + (a + dx - x0), sh + (size_t)sy * W + a, (size_t)(b - a) * sizeof(int));
        }
        const int* p = pic + (size_t)y * W + x0; int* o = dst + (size_t)y * W + x0;
        int i = 0;
        for (; i + N <= n; i += N)
        {
            const T s = V::loadu(rowbuf + i), d = only ? V::set1_32(0) : V::loadu(p + i);
            V::storeu(o + i, GLOW ? add(s, d) : over(d, s));
        }
        if (i < n)
        {
            for (int l = 0; l < N; ++l) { ts[l] = i + l < n ? rowbuf[i + l] : 0; td[l] = (i + l < n && !only) ? p[i + l] : 0; }
            const T r = GLOW ? add(V::loadu(ts), V::loadu(td)) : over(V::loadu(td), V::loadu(ts));
            V::storeu(ts, r); for (int l = 0; l < n - i; ++l) o[i + l] = ts[l];
        }
    }
}

static SR2D_INLINE int fx_clampi(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

// blur pass flags of a BLUR / SHADOW stage in DRAW_BLUR terms: 4 = two passes, 16 = one
static SR2D_INLINE int fx_blur_passflags(const SR2D_FxStage& s)
{
    if (s.kind == SR2D_FX_BLUR) return (s.flags & SR2D_FXF_BLUR_BOX) ? 16 : (s.flags & SR2D_FXF_BLUR_FAST) ? 4 : 0;
    return (s.flags & SR2D_FXF_SHADOW_BOX) ? 16 : (s.flags & SR2D_FXF_SHADOW_FAST) ? 4 : 0;
}

// ------------------------------------------------------------ diffuse (Photoshop "Diffuse")
// Every output pixel takes the value of a pseudo-randomly chosen pixel within the Chebyshev
// radius r: out(x, y) = in(x + dx, y + dy), dx, dy in [-r, r]. The offsets come from a
// per-pixel integer hash (fx_mix of x, y, seed) so the pattern is deterministic, identical
// on SSE2 / AVX2, and animates when the seed changes. No wrap-around: a pick that leaves the
// picture (px0..px1 x py0..py1 inside the work image) folds back onto the outermost real pixel
// (reflect-101: -1 -> 1, -2 -> 2, n -> n-2), like Photoshop truncating its neighbourhood at the
// border - border pixels keep their value only through the ordinary 1/(2r+1)^2 self-pick, and
// the transparent surround is never picked. DARKEN / LIGHTEN keep the per-channel min / max of
// the original and the picked pixel. No premultiplied maths: a picked pixel is a valid
// premultiplied pixel as it is.
static void fx_fold_lut(int* lut, int n)
{   // reflect-101 fold of raw offsets [-64, n + 64) -> [0, n); narrow pictures only (n <= 64),
    // wide ones use the equivalent min(|t|, 2n-2-|t|) formula directly (their |t| <= 2n-2).
    if (n == 1) { for (int i = 0; i < 208; ++i) lut[i] = 0; return; }
    int* L = lut + 64; const int pp = 2 * n - 2;
    for (int i = -64; i < n + 64; ++i) { int f = i; while (f < 0 || f >= n) f = f < 0 ? -f : pp - f; L[i] = f; }
}
static void fx_diffuse(const int* in, int* out, int W, int /*H*/, int x0, int y0, int x1, int y1,
                       int px0, int py0, int px1, int py1, int r, uint32_t seed, int mode, int iox, int ioy)
{   // iox, ioy: world (sprite or screen) coordinate of work pixel (0, 0) - the pattern is hashed in world
    // coordinates so it does not depend on the clip rect / work image placement (DrawParallel bands, POST)
    // (H is unused: the hash mixes x / y world coordinates directly)
    SR2D_ALIGN(32) int lane_i[8] = { 0, 1, 2, 3, 4, 5, 6, 7 };
    SR2D_ALIGN(32) int xlut[208], ylut[208];
    SR2D_ALIGN(32) int tmp[8];
    const int nw = px1 - px0, nh = py1 - py0;
    if (nw <= 64) fx_fold_lut(xlut, nw);
    if (nh <= 64) fx_fold_lut(ylut, nh);
    const T lanes = V::loadu(lane_i);
    const T span = V::set1_32(2 * r + 1), vr = V::set1_32(r);
    const T vW = V::set1_32(W);
    const T vpx0 = V::set1_32(px0), vpy0 = V::set1_32(py0);
    const T xlo = V::set1_32(px0 - r), xhi = V::set1_32(px1 - 1 + r), ylo = V::set1_32(py0 - r), yhi = V::set1_32(py1 - 1 + r);
    const T xpp = V::set1_32(2 * nw - 2), ypp = V::set1_32(2 * nh - 2);
    const T kx = V::set1_32((int)0x9E3779B1u), ky = V::set1_32((int)0x85EBCA77u), ks = V::set1_32((int)(seed * 0xC2B2AE3Du + 0x27D4EB2Fu));
    const T m16 = V::set1_32(0xffff);
    for (int y = y0; y < y1; ++y)
    {
        const int* irow = in;
        int* o = out + (size_t)y * W;
        const T hy = V::add32(V::mullo32(V::set1_32(y + ioy), ky), ks);
        for (int x = x0; x < x1; x += N)
        {
            const T vx = V::add32(V::set1_32(x), lanes);
            T h = fx_mix(V::add32(V::mullo32(V::add32(vx, V::set1_32(iox)), kx), hy));
            // two independent 16-bit halves -> offsets in [0, 2r] via (h16 * span) >> 16 (no modulo bias to speak of)
            T dx = V::sub32(V::template srli32<16>(V::mullo32(V::and_(h, m16), span)), vr);
            T dy = V::sub32(V::template srli32<16>(V::mullo32(V::template srli32<16>(h), span)), vr);
            // fold out-of-picture picks back onto the outermost real pixel (no wrap, no clamp-to-border self bias);
            // the min/max pre-clamp is a no-op for real picks and only keeps hash noise lanes in LUT / formula range
            T tx = V::min32(V::max32(V::add32(vx, dx), xlo), xhi);
            tx = V::sub32(tx, vpx0);
            T ax = V::max32(tx, V::sub32(V::zero(), tx));   // |tx| (an AND with the sign mask is NOT abs: -1 & 0x7FFFFFFF = 0x7FFFFFFF)
            T sx = nw <= 64 ? gather32(xlut + 64, tx) : V::min32(ax, V::sub32(xpp, ax));
            T ty = V::min32(V::max32(V::add32(V::set1_32(y), dy), ylo), yhi);
            ty = V::sub32(ty, vpy0);
            T ay = V::max32(ty, V::sub32(V::zero(), ty));
            T sy = nh <= 64 ? gather32(ylut + 64, ty) : V::min32(ay, V::sub32(ypp, ay));
            T pick = gather32(irow, V::add32(V::mullo32(V::add32(sy, vpy0), vW), V::add32(sx, vpx0)));
            if (mode)
            {
                T cur = V::loadu(in + (size_t)y * W + x);           // over-read past x1 stays inside the row's slack (W >= x1)
                pick = mode == 1 ? V::min_u8(cur, pick) : V::max_u8(cur, pick);
            }
            if (x + N <= x1) V::storeu(o + x, pick);
            else { V::storeu(tmp, pick); for (int i = 0; i < x1 - x; ++i) o[x + i] = tmp[i]; }
        }
    }
}

// Blur downscale factor for a radius: 0 = automatic. The box average by k followed by a
// bilinear enlargement adds its own softening of ~k/2 px, negligible next to r once r >= 6k.
static SR2D_INLINE int fx_blur_down(int r, int k)
{
    if (k < 0) k = r >= 48 ? 8 : r >= 24 ? 4 : r >= 12 ? 2 : 1;   // -1 = automatic; 0 / 1 = none (old stages carry 0)
    return k <= 1 ? 1 : k <= 2 ? 2 : k <= 4 ? 4 : 8;
}
// Memory a downscaled blur needs on top of the work images: the small image, its blur planes and a shrink accumulator.
static size_t fx_blur_small_need(int W, int H, int r, int k, int passflags)
{
    if (k <= 1) return 0;
    const int sw2 = (W + k - 1) / k, sh2 = (H + k - 1) / k;
    const size_t smallBytes = ((size_t)sw2 * sh2 * sizeof(int) + 63) & ~(size_t)63;
    const size_t acc = ((size_t)W * 4 * sizeof(uint32_t) + 127) & ~(size_t)63;
    const BlurGeo g = blur_geo(sw2, sh2, 0, 0, sw2, sh2, 0, 0, r / k, 8 | passflags);
    return smallBytes + acc + (g.ok ? g.need : 0) + 64;
}
// Blur of [x0,x1)x[y0,y1) of img at 1/k resolution: shrink (premultiplied average), blur the
// small image in place (r/k), bilinearly enlarge back over the rectangle. The whole work image
// is shrunk (it is small anyway) so that block boundaries do not depend on the rectangle.
// The blocks are anchored to WORLD coordinates (multiples of k in sprite / screen space, iox, ioy
// = world coordinate of work pixel (0, 0)): the result then does not depend on where the work
// image happens to start (clip rect, DrawParallel bands, POST). The first sx0 / sy0 columns /
// rows of the work image are left alone - they are at least the blur support away from any
// content (the stage margin includes + k for exactly this), so their blurred value is 0 = what they hold.
static void fx_blur_downscaled(int* img, int W, int H, int x0, int y0, int x1, int y1, int r, int k, int passflags, uint8_t* mem, int iox, int ioy)
{
    const int sx0 = (k - ((iox % k) + k) % k) % k, sy0 = (k - ((ioy % k) + k) % k) % k;
    const int sw2 = (W - sx0 + k - 1) / k, sh2 = (H - sy0 + k - 1) / k;
    if (sw2 <= 0 || sh2 <= 0) return;
    int* smallImg = (int*)mem;
    const size_t small_b = ((size_t)((W + k - 1) / k) * ((H + k - 1) / k) * sizeof(int) + 63) & ~(size_t)63;   // as planned in fx_blur_small_need
    uint32_t* acc = (uint32_t*)(mem + small_b);
    const size_t acc_b = ((size_t)W * 4 * sizeof(uint32_t) + 127) & ~(size_t)63;
    uint8_t* bmem = mem + small_b + acc_b;
    warp_shrink(img + (size_t)sy0 * W + sx0, W - sx0, H - sy0, k, k, smallImg, sw2, sh2, acc, 0, W);   // premultiplied in, plain average = premultiplied out
    const int rs = r / k;
    if (rs > 0)
    {
        const BlurGeo g = blur_geo(sw2, sh2, 0, 0, sw2, sh2, 0, 0, rs, 8 | passflags);
        if (g.ok) blur_exec(g, smallImg, sw2, sh2, smallImg, sw2, 1, 0, 1 | passflags, 256, bmem);
    }
    // enlarge: the small image covers [sx0, sx0 + sw2*k) x [sy0, sy0 + sh2*k) of the work image
    const float q[8] = { (float)sx0, (float)sy0, (float)(sx0 + sw2 * k), (float)sy0, (float)(sx0 + sw2 * k), (float)(sy0 + sh2 * k), (float)sx0, (float)(sy0 + sh2 * k) };
    if (x0 < sx0) x0 = sx0;
    if (y0 < sy0) y0 = sy0;
    if (x0 < x1 && y0 < y1) warp_op(OpPaint(), smallImg, sw2, sh2, img, W, x0, y0, x1, y1, q, 0, 0, SR2D_WARP_BILINEAR, 1, true);
}
static SR2D_INLINE int fx_iabs(int v) { return v < 0 ? -v : v; }
static SR2D_INLINE int fx_shadow_shift(float v)           // offset in whole pixels, |v| <= 1024, NaN -> 0
{ if (!(v > -1024.0f)) v = v < 0 ? -1024.0f : 0.0f; if (v > 1024.0f) v = 1024.0f; return (int)(v < 0 ? v - 0.5f : v + 0.5f); }
// scalar sqrt through the vector op (this file keeps away from libm, everything stays flavour-identical)
static SR2D_INLINE float fx_sqrt1(float v)
{
    SR2D_ALIGN(32) float tmp[8];
    V::storeu(tmp, V::casti(V::sqrtf_(V::set1f(v))));
    return tmp[0];
}

// the largest distance a motion-blur trail reaches from a pixel (drives the work image margin)
static float fx_motion_extent(const SR2D_FxStage& s)
{
    if (s.i[1] >= 2 && s.map != nullptr)
    {
        const int pts = fx_clampi(s.i[1], 2, 32);
        const float* p = (const float*)s.map;
        const float str = fx_fabs(fx_fin(s.f[1]));
        float mx = 0;
        for (int k = 0; k < pts; ++k)
        {
            const float x = fx_fin(p[k * 2]), y = fx_fin(p[k * 2 + 1]);
            const float d = fx_sqrt1(x * x + y * y) * str;
            if (d > mx) mx = d;
        }
        return mx;
    }
    return fx_fabs(fx_fin(s.f[1]));
}

static int fx_margin(const SR2D_FxStage& s)
{
    if (s.flags & SR2D_FXF_DISABLED) return 0;
    switch (s.kind)
    {
    case SR2D_FX_BLUR:
    {
        const int r = s.i[0] < 0 ? 0 : s.i[0] > 512 ? 512 : s.i[0];
        const int passes = (s.flags & SR2D_FXF_BLUR_BOX) ? 1 : (s.flags & SR2D_FXF_BLUR_FAST) ? 2 : 3;
        const int k = fx_blur_down(r, fx_clampi(s.i[1], -1, 8));
        return r * passes + (k > 1 ? k : 0);                 // + one shrunk block for the bilinear enlargement
    }
    case SR2D_FX_DIFFUSE: return fx_clampi(s.i[0], 1, 64) * fx_clampi(s.i[1], 1, 32);
    case SR2D_FX_MORPH: return fx_clampi(s.i[0], 0, 256);   // erode too: it needs the transparent surroundings present
    case SR2D_FX_SHADOW:
    {
        const int br = fx_clampi(s.i[0], 0, 512);
        const int passes = (s.flags & SR2D_FXF_SHADOW_BOX) ? 1 : (s.flags & SR2D_FXF_SHADOW_FAST) ? 2 : 3;
        const int k = fx_blur_down(br, fx_clampi(s.i[3], -1, 8));
        const int r = br * passes + (k > 1 ? k : 0) + fx_clampi(s.i[1], 0, 256);
        const int ax = fx_iabs(fx_shadow_shift(s.f[1])), ay = fx_iabs(fx_shadow_shift(s.f[2]));
        return r + (ax > ay ? ax : ay);
    }
    case SR2D_FX_MOTION:
    {
        float amp = fx_motion_extent(s);
        if (!(amp <= 2048.0f)) amp = 2048.0f;          // also catches NaN / inf (comparison false)
        return fx_ceil(amp) + (fx_sample_mode(s.flags) == 2 ? 2 : 1);
    }
    case SR2D_FX_DISTORT:
    case SR2D_FX_DISTORT_MAP:
    {
        float amp = fx_fabs(s.f[1]);
        if (s.kind == SR2D_FX_DISTORT && s.i[0] == SR2D_FX_WAVE && (s.flags & SR2D_FXF_WAVE_CROSS)) amp *= 1.5f;
        if (!(amp <= 2048.0f)) amp = 2048.0f;          // also catches NaN / inf (comparison false)
        return fx_ceil(amp) + (fx_sample_mode(s.flags) == 2 ? 2 : 1);
    }
    default: return 0;
    }
}

// ------------------------------------------------------------ DRAW_FX
static int DRAW_FX(int* src, int sw, int sh, int* dst, int dw, int clipL, int clipT, int clipR, int clipB,
                   const float* quad, const float* poly, int npoly, int op, int flags, int k,
                   const SR2D_FxStage* stages, int nstages)
{
    if (!src || !dst || sw <= 0 || sh <= 0 || !quad || clipL >= clipR || clipT >= clipB) return 0;
    if (nstages < 0 || (nstages > 0 && !stages)) return 0;
    if (nstages > 64) nstages = 64;
    const bool src_premul = (flags & SR2D_FX_PREMUL) != 0, opaque = (flags & SR2D_FX_OPAQUE) != 0;
    const int wflags = flags & (SR2D_WARP_BILINEAR | SR2D_WARP_AREA | SR2D_WARP_BICUBIC);
    const int opcode = op <= 0 ? 1 : op;

    // ---- geometry
    int M = 0;
    for (int i = 0; i < nstages; ++i)
    {
        M += fx_margin(stages[i]);
        // only an ENABLED map stage may reject the draw - a disabled one is skipped like every other pass skips it
        if (!(stages[i].flags & SR2D_FXF_DISABLED) && stages[i].kind == SR2D_FX_DISTORT_MAP &&
            (!stages[i].map || stages[i].mw <= 0 || stages[i].mh <= 0)) return 0;
    }
    // The margin can never usefully exceed the clip rect's extent (+ the sprite) - beyond that
    // the padded area is invisible - and the whole work set must stay within a sane budget:
    // work images (1-2 x 4 B/px) + blur planes (2 x 8 B/px) = up to 24 B/px, so 16 Mpx ~ 384 MB max.
    {
        const long long ext = (long long)(clipR - clipL) + (clipB - clipT) + sw + sh;
        if (M > ext) M = (int)ext;
        if (M > 4096) M = 4096;
    }
    int bx = 0, by = 0;
    const bool blit = fx_is_blit(quad, sw, sh, bx, by) && npoly == 0;
    if (blit && ((long long)bx + sw + M <= clipL || (long long)by + sh + M <= clipT || (long long)bx - M >= clipR || (long long)by - M >= clipB)) return 0;
    bool post = (flags & SR2D_FX_POST) != 0 && !blit;
    float xquad[8];
    int W, H, ox, oy;                  // work image size and its origin on the destination (blit / POST)
    int lx0, ly0, lx1, ly1;            // rectangle of the work image holding the loaded picture
    if (blit || !post)
    {
        if (!blit && !post && M > 0 && !fx_extend_quad(quad, sw, sh, M, xquad)) post = true;   // perspective horizon: fall back
    }
    if (blit || !post)
    {
        W = sw + 2 * M; H = sh + 2 * M; ox = bx - M; oy = by - M;
        lx0 = M; ly0 = M; lx1 = M + sw; ly1 = M + sh;
        if (!blit && M == 0) for (int i = 0; i < 8; ++i) xquad[i] = quad[i];
    }
    else
    {   // POST: bounding box of the quad, limited to the clip rect grown by M
        float fx0 = quad[0], fx1 = quad[0], fy0 = quad[1], fy1 = quad[1];
        for (int i = 1; i < 4; ++i)
        {
            if (quad[i * 2] < fx0) fx0 = quad[i * 2];
            if (quad[i * 2] > fx1) fx1 = quad[i * 2];
            if (quad[i * 2 + 1] < fy0) fy0 = quad[i * 2 + 1];
            if (quad[i * 2 + 1] > fy1) fy1 = quad[i * 2 + 1];
        }
        // clamp in float first: NaN / inf / 1e30 corners must not reach the int conversion
        const float lo_x = (float)(clipL - M), lo_y = (float)(clipT - M), hi_x = (float)(clipR + M), hi_y = (float)(clipB + M);
        if (!(fx0 == fx0) || !(fx1 == fx1) || !(fy0 == fy0) || !(fy1 == fy1)) return 0;   // NaN anywhere: degenerate quad
        if (fx0 < lo_x) fx0 = lo_x;
        if (fy0 < lo_y) fy0 = lo_y;
        if (fx1 > hi_x) fx1 = hi_x;
        if (fy1 > hi_y) fy1 = hi_y;
        if (!(fx0 < fx1) || !(fy0 < fy1)) return 0;
        int qx0 = (int)fx0 - 1, qy0 = (int)fy0 - 1, qx1 = (int)fx1 + 2, qy1 = (int)fy1 + 2;
        if (qx0 < clipL - M) qx0 = clipL - M;
        if (qy0 < clipT - M) qy0 = clipT - M;
        if (qx1 > clipR + M) qx1 = clipR + M;
        if (qy1 > clipB + M) qy1 = clipB + M;
        if (qx0 >= qx1 || qy0 >= qy1) return 0;
        W = (qx1 - qx0) + 2 * M; H = (qy1 - qy0) + 2 * M; ox = qx0 - M; oy = qy0 - M;
        lx0 = M; ly0 = M; lx1 = W - M; ly1 = H - M;
    }
    // work image limit: 16 Mpx (a 4096 x 4096 work image; the 1080p full-frame case is 2 Mpx)
    if ((long long)W * H > (1LL << 24)) return 0;

    // ---- memory: work A [+ work B] [+ premultiplied source] [+ gradient maps] [+ blur]
    const size_t img = ((size_t)W * H * sizeof(int) + 8 * sizeof(int) + 63) & ~(size_t)63;
    bool need_b = false, need_c = false; size_t blur_need = 0, gmap_bytes = 0, misc_bytes = 0;
    for (int i = 0; i < nstages; ++i)
    {
        const SR2D_FxStage& s = stages[i];
        if (s.flags & SR2D_FXF_DISABLED) continue;
        if (s.kind == SR2D_FX_DISTORT || s.kind == SR2D_FX_DISTORT_MAP || s.kind == SR2D_FX_MORPH || s.kind == SR2D_FX_DIFFUSE || s.kind == SR2D_FX_MOTION) need_b = true;
        if (s.kind == SR2D_FX_SHADOW) { need_b = true; if (s.i[1] > 0) need_c = true; }
        if (s.kind == SR2D_FX_DISTORT_MAP) gmap_bytes += (((size_t)(s.mw + 2) * (s.mh + 2) * sizeof(int)) + 63) & ~(size_t)63;
        if (s.kind == SR2D_FX_BLUR || s.kind == SR2D_FX_SHADOW)
        {
            const int pf = fx_blur_passflags(s);
            const int r = fx_clampi(s.i[0], 0, 512), kd = fx_blur_down(r, fx_clampi(s.kind == SR2D_FX_BLUR ? s.i[1] : s.i[3], -1, 8));
            size_t nb;
            if (kd > 1) nb = fx_blur_small_need(W, H, r, kd, pf);
            else { const BlurGeo g = blur_geo(W, H, 0, 0, W, H, 0, 0, r, 8 | pf); nb = g.ok ? g.need : 0; }
            if (nb > blur_need) blur_need = nb;
        }
        if (s.kind == SR2D_FX_DISTORT && (s.i[0] == SR2D_FX_NOISE || s.i[0] == SR2D_FX_TURBULENCE))
        {   // noise row tables: 3 octaves x (cells of the widest row + 8) x 8 floats
            const size_t cells = (size_t)W / 1 + 16;              // upper bound: one cell per pixel + guard
            const size_t b = (3 * cells * 8 * sizeof(float) + 63) & ~(size_t)63;
            if (b > misc_bytes) misc_bytes = b;
        }
        if (s.kind == SR2D_FX_MORPH || s.kind == SR2D_FX_SHADOW)
        {
            const size_t b = ((size_t)4 * (W > H ? W : H) * sizeof(int) + 63) & ~(size_t)63;   // morph scratch / merge row
            if (b > misc_bytes) misc_bytes = b;
        }
    }
    const size_t pm_bytes = (post && !src_premul) || (post && opaque) ? (((size_t)sw * sh * sizeof(int)) + 63) & ~(size_t)63 : 0;
    const int nimg = need_c ? 3 : need_b ? 2 : 1;
    const size_t need = img * nimg + pm_bytes + gmap_bytes + blur_need + misc_bytes + 64;
    size_t got = 0;
    uint8_t* mem = (uint8_t*)sr2d_scratch_acquire(need, &got);
    if (!mem) return 0;
    int* A = (int*)mem;
    int* B = need_b ? (int*)(mem + img) : 0;
    int* C = need_c ? (int*)(mem + img * 2) : 0;
    uint8_t* rest = mem + img * nimg;
    int* pm = 0;
    if (pm_bytes) { pm = (int*)rest; rest += pm_bytes; }
    int* gmaps = (int*)rest; rest += gmap_bytes;
    uint8_t* misc = rest; rest += misc_bytes;
    uint8_t* bmem = rest;
    const int noise_cap = (int)(W + 16);

    // ---- load
    memset(A, 0, (size_t)W * H * sizeof(int));
    if (B) memset(B, 0, (size_t)W * H * sizeof(int));
    if (C) memset(C, 0, (size_t)W * H * sizeof(int));
    if (blit || !post)
    {
        for (int y = 0; y < sh; ++y) fx_load_row(src + (size_t)y * sw, A + (size_t)(y + M) * W + M, sw, src_premul, opaque);
    }
    else
    {
        const int* s = src;
        if (pm) { for (int y = 0; y < sh; ++y) fx_load_row(src + (size_t)y * sw, pm + (size_t)y * sw, sw, src_premul, opaque); s = pm; }
        float tq[8];
        for (int i = 0; i < 4; ++i) { tq[i * 2] = quad[i * 2] - (float)ox; tq[i * 2 + 1] = quad[i * 2 + 1] - (float)oy; }
        warp_op(OpPaint(), s, sw, sh, A, W, lx0, ly0, lx1, ly1, tq, 0, 0, wflags, 1, true);
    }

    // ---- stages
    int ax0 = lx0, ay0 = ly0, ax1 = lx1, ay1 = ly1;      // possibly non-transparent rectangle
    // pattern coordinates: sprite pixels (blit / PRE) or screen pixels (POST)
    const float pox = post ? (float)ox : (float)-M, poy = post ? (float)oy : (float)-M;
    const int iox = post ? ox : -M, ioy = post ? oy : -M;
    int* cur = A; int* oth = B;
    int* gm = gmaps;
    for (int i = 0; i < nstages; ++i)
    {
        const SR2D_FxStage& s = stages[i];
        if (s.flags & SR2D_FXF_DISABLED) continue;
        const int m = fx_margin(s);
        ax0 = ax0 - m > 0 ? ax0 - m : 0; ay0 = ay0 - m > 0 ? ay0 - m : 0;
        ax1 = ax1 + m < W ? ax1 + m : W; ay1 = ay1 + m < H ? ay1 + m : H;
        switch (s.kind)
        {
        case SR2D_FX_BLUR:
        {
            if (s.i[0] <= 0) break;
            const int pf = fx_blur_passflags(s);
            const int r = fx_clampi(s.i[0], 0, 512), kd = fx_blur_down(r, fx_clampi(s.i[1], -1, 8));
            if (kd > 1) { if (fx_blur_small_need(W, H, r, kd, pf) <= blur_need) fx_blur_downscaled(cur, W, H, ax0, ay0, ax1, ay1, r, kd, pf, bmem, iox, ioy); break; }
            const BlurGeo g = blur_geo(W, H, ax0, ay0, ax1, ay1, 0, 0, r, 8 | pf);
            if (!g.ok || g.need > blur_need) break;
            blur_exec(g, cur, W, H, cur, W, 1, 0, 1 | pf, 256, bmem);
            break;
        }
        case SR2D_FX_DIFFUSE:
        {
            const int r = fx_clampi(s.i[0], 1, 64), passes = fx_clampi(s.i[1], 1, 32);
            const int mode = (s.flags & SR2D_FXF_DIFFUSE_DARKEN) ? 1 : (s.flags & SR2D_FXF_DIFFUSE_LIGHTEN) ? 2 : 0;
            for (int pss = 0; pss < passes; ++pss)
            {
                fx_diffuse(cur, oth, W, H, ax0, ay0, ax1, ay1, lx0, ly0, lx1, ly1, r, (uint32_t)s.i[2] * 0x9E3779B9u + (uint32_t)pss * 0x632BE5ABu, mode, iox, ioy);
                int* t = cur; cur = oth; oth = t;
            }
            // the ring around the picture is context, never content: picks fold back into the picture now,
            // so restore the transparent surround exactly as it was loaded - later stages and the final
            // resample (POST quads reach into the ring, DrawFx onto a larger destination composites it) must
            // keep seeing 0 outside the picture
            for (int y = 0; y < ly0; ++y) memset(cur + (size_t)y * W, 0, (size_t)W * sizeof(int));
            for (int y = ly1; y < H; ++y) memset(cur + (size_t)y * W, 0, (size_t)W * sizeof(int));
            for (int y = ly0; y < ly1; ++y)
            {
                if (lx0 > 0) memset(cur + (size_t)y * W, 0, (size_t)lx0 * sizeof(int));
                if (lx1 < W) memset(cur + (size_t)y * W + lx1, 0, (size_t)(W - lx1) * sizeof(int));
            }
            break;
        }
        case SR2D_FX_DISTORT:
        {
            const int mode = fx_sample_mode(s.flags);
            switch (s.i[0])
            {
            case SR2D_FX_RIPPLE:     fx_distort_mode(mode, cur, oth, W, H, ax0, ay0, ax1, ay1, pox, poy, GenRipple(s)); break;
            case SR2D_FX_NOISE:      fx_distort_mode(mode, cur, oth, W, H, ax0, ay0, ax1, ay1, pox, poy, GenNoise(s, 1, (float*)misc, noise_cap)); break;
            case SR2D_FX_TURBULENCE: fx_distort_mode(mode, cur, oth, W, H, ax0, ay0, ax1, ay1, pox, poy, GenNoise(s, 3, (float*)misc, noise_cap)); break;
            default:                 fx_distort_mode(mode, cur, oth, W, H, ax0, ay0, ax1, ay1, pox, poy, GenWave(s)); break;
            }
            int* t = cur; cur = oth; oth = t;
            break;
        }
        case SR2D_FX_DISTORT_MAP:
        {
            fx_build_gmap(s.map, s.mw, s.mh, (s.flags & SR2D_FXF_MAP_ALPHA) != 0, gm);
            fx_distort_mode(fx_sample_mode(s.flags), cur, oth, W, H, ax0, ay0, ax1, ay1, pox, poy, GenMap(s, gm));
            gm += (((size_t)(s.mw + 2) * (s.mh + 2) * sizeof(int)) + 63) / 64 * 16;
            int* t = cur; cur = oth; oth = t;
            break;
        }
        case SR2D_FX_COLOR:
        {
            const FxColor c = fx_color_setup(s);
            if (!c.noop) fx_color(cur, W, ax0, ay0, ax1, ay1, c);
            break;
        }
        case SR2D_FX_MORPH:
        {
            const int r = fx_clampi(s.i[0], 0, 256);
            if (r <= 0) break;
            const bool sq = (s.flags & SR2D_FXF_MORPH_SQUARE) != 0;
            // the rectangle was grown by r above, so the transparent surroundings take part
            if (s.flags & SR2D_FXF_MORPH_ERODE) fx_morph<false>(cur, oth, W, H, ax0, ay0, ax1, ay1, r, sq, (int*)misc);
            else                                fx_morph<true>(cur, oth, W, H, ax0, ay0, ax1, ay1, r, sq, (int*)misc);
            int* t = cur; cur = oth; oth = t;
            break;
        }
        case SR2D_FX_SHADOW:
        {
            const int r = fx_clampi(s.i[0], 0, 512), grow = fx_clampi(s.i[1], 0, 256);
            const int dx = fx_shadow_shift(s.f[1]), dy = fx_shadow_shift(s.f[2]);
            const float opac = !(s.f[0] > 0) ? 0.0f : s.f[0] > 1 ? 1.0f : s.f[0];
            const bool glow = (s.flags & SR2D_FXF_SHADOW_GLOW) != 0, only = (s.flags & SR2D_FXF_SHADOW_ONLY) != 0;
            // shadow shape: copy of the picture (grown), blurred, tinted - built in 'oth' (grow needs C as a temp)
            int* shp = oth;
            const int gx0 = ax0, gy0 = ay0, gx1 = ax1, gy1 = ay1;        // already grown by the stage margin
            if (grow > 0)
            {
                fx_morph<true>(cur, C, W, H, gx0, gy0, gx1, gy1, grow, false, (int*)misc);
                shp = C;
                // rows of C outside the rectangle are still zero from the initial clear / earlier use? They are
                // only ever written inside [gx0,gx1)x[gy0,gy1) of the current chain, which grows monotonically.
            }
            else
            {
                for (int y = gy0; y < gy1; ++y) memcpy(shp + (size_t)y * W + gx0, cur + (size_t)y * W + gx0, (size_t)(gx1 - gx0) * sizeof(int));
            }
            if (r > 0)
            {
                const int pf = fx_blur_passflags(s), kd = fx_blur_down(r, fx_clampi(s.i[3], -1, 8));
                if (kd > 1) { if (fx_blur_small_need(W, H, r, kd, pf) <= blur_need) fx_blur_downscaled(shp, W, H, gx0, gy0, gx1, gy1, r, kd, pf, bmem, iox, ioy); }
                else
                {
                    const BlurGeo g = blur_geo(W, H, gx0, gy0, gx1, gy1, 0, 0, r, 8 | pf);
                    if (g.ok && g.need <= blur_need) blur_exec(g, shp, W, H, shp, W, 1, 0, 1 | pf, 256, bmem);
                }
            }
            fx_shadow_tint(shp, W, gx0, gy0, gx1, gy1, s.i[2], opac);
            // merge in place into 'cur' (the shifted read of shp never aliases cur)
            if (glow) fx_shadow_merge<true>(cur, shp, cur, W, H, ax0, ay0, ax1, ay1, dx, dy, only, (int*)misc);
            else      fx_shadow_merge<false>(cur, shp, cur, W, H, ax0, ay0, ax1, ay1, dx, dy, only, (int*)misc);
            // the shape buffer is scratch: clear what we used so the next stage sees a clean 'oth' / C
            for (int y = gy0; y < gy1; ++y) memset(shp + (size_t)y * W + gx0, 0, (size_t)(gx1 - gx0) * sizeof(int));
            break;
        }
        case SR2D_FX_MOTION:
        {
            const int n = fx_clampi(s.i[0], 2, 64);
            float offs[128];                                   // 64 samples x (dx, dy)
            if (s.i[1] >= 2 && s.map != nullptr)
            {   // polyline: walk it uniformly per segment, scaled by f[1] (the trail shape is the path)
                const int pts = fx_clampi(s.i[1], 2, 32);
                const float* p = (const float*)s.map;
                const float str = fx_fin(s.f[1]);
                for (int k = 0; k < n; ++k)
                {
                    const float t = (float)k * (pts - 1) / (float)(n - 1);
                    int seg = (int)t; if (seg > pts - 2) seg = pts - 2;
                    const float fr = t - seg;
                    offs[k * 2] = (p[seg * 2] + (p[seg * 2 + 2] - p[seg * 2]) * fr) * str;
                    offs[k * 2 + 1] = (p[seg * 2 + 1] + (p[seg * 2 + 3] - p[seg * 2 + 1]) * fr) * str;
                }
            }
            else
            {   // straight trail: direction in degrees (0 = +x, 90 = down), the last sample at f[1] px
                const float len = fx_fin(s.f[1]);
                // cos/sin of the direction: scalar via the vector routine (no libm, see GenWave)
                SR2D_ALIGN(32) float tr[8];
                const typename V::F a = V::set1f(fx_fin(s.f[0]) * (3.141592653589793f / 180.0f));
                V::storeu(tr, V::casti(fx_sin(V::addf(a, V::set1f(1.5707963267948966f)))));   // cos
                const float cs = tr[0];
                V::storeu(tr, V::casti(fx_sin(a)));                                           // sin
                const float sn = tr[0];
                const float dx = cs * len, dy = sn * len;
                for (int k = 0; k < n; ++k)
                {
                    const float t = n > 1 ? (float)k / (float)(n - 1) : 0.0f;
                    offs[k * 2] = dx * t; offs[k * 2 + 1] = dy * t;
                }
            }
            const int mode = fx_sample_mode(s.flags);
            if (mode == 2)      fx_motion<2>(cur, oth, W, H, ax0, ay0, ax1, ay1, offs, n);
            else if (mode == 1) fx_motion<1>(cur, oth, W, H, ax0, ay0, ax1, ay1, offs, n);
            else                fx_motion<0>(cur, oth, W, H, ax0, ay0, ax1, ay1, offs, n);
            int* t2 = cur; cur = oth; oth = t2;
            break;
        }
        default: break;
        }
    }

    // ---- composite
    if (blit)       fx_composite(opcode, k, cur, W, H, dst, dw, clipL, clipT, clipR, clipB, ox, oy, 0, 0, 0, 0);
    else if (!post) fx_composite(opcode, k, cur, W, H, dst, dw, clipL, clipT, clipR, clipB, 0, 0, xquad, poly, npoly, wflags);
    else if (npoly >= 3)
    {   // 1:1 through the warp so the polygon clip applies (nearest at integer offsets = exact copy)
        const float iq[8] = { (float)ox, (float)oy, (float)(ox + W), (float)oy, (float)(ox + W), (float)(oy + H), (float)ox, (float)(oy + H) };
        fx_composite(opcode, k, cur, W, H, dst, dw, clipL, clipT, clipR, clipB, 0, 0, iq, poly, npoly, 0);
    }
    else            fx_composite(opcode, k, cur, W, H, dst, dw, clipL, clipT, clipR, clipB, ox, oy, 0, 0, 0, 0);
    sr2d_scratch_release(mem, got);
    return 1;
}
