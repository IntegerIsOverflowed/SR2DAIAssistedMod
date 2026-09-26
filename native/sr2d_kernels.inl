// SR2D - SIMD kernels.  Included from sr2d_kernels_sse2.cpp / sr2d_kernels_avx2.cpp
// *inside* a flavour namespace, after <immintrin.h>/<stdint.h>/<string.h>.
//
// All kernels are bit-exact re-implementations of the original scalar code.
// Template parameter V is one of the vector traits from sr2d_simd.h.
//
// Conventions (same as the original DLL):
//   w, h        : rectangle size in pixels
//   ws, wd, wm  : row pitch (in pixels) of src / dst / mask
//   maskex      : bit mask tested against mask pixels
//   notm        : 0 -> operate where (mask & maskex) != 0, otherwise where == 0

#if defined(_MSC_VER)
#  define SR2D_ALIGN(n) __declspec(align(n))
#else
#  define SR2D_ALIGN(n) __attribute__((aligned(n)))
#endif

// ----------------------------------------------------------------------------
// exact unsigned 32-bit division by a runtime constant (Granlund-Montgomery,
// "branch-free" variant as used by libdivide). Exact for every n in [0, 2^32).
// ----------------------------------------------------------------------------
struct UDiv32
{
    uint32_t magic;
    int      shift;   // final shift
    int      pow2;    // != 0 -> plain shift by 'shift'

    static UDiv32 make(uint32_t d)
    {
        UDiv32 r;
        int l = 31;
        while (l > 0 && !((d >> l) & 1u)) --l;       // floor(log2 d)
        if ((d & (d - 1)) == 0) { r.magic = 0; r.shift = l; r.pow2 = 1; return r; }
        uint64_t num = (uint64_t)1 << (32 + l);
        uint32_t m   = (uint32_t)(num / d);
        uint32_t rem = (uint32_t)(num - (uint64_t)m * d);
        m += m;
        uint32_t twice_rem = rem + rem;
        if (twice_rem >= d || twice_rem < rem) m += 1;
        r.magic = 1 + m;
        r.shift = l;
        r.pow2  = 0;
        return r;
    }
    SR2D_INLINE uint32_t operator()(uint32_t n) const
    {
        if (pow2) return n >> shift;
        uint32_t q = (uint32_t)(((uint64_t)n * magic) >> 32);
        uint32_t t = ((n - q) >> 1) + q;
        return t >> shift;
    }
};

template<class V>
struct K
{
    typedef typename V::T T;
    enum { N = V::N };

    // ------------------------------------------------------------------ utils
    static SR2D_INLINE void copy_px(const int* s, int* d, int n)
    {
        int i = 0;
        for (; i + N <= n; i += N) V::storeu(d + i, V::loadu(s + i));
        for (; i < n; ++i) d[i] = s[i];
    }

    // 8-bit lane logical shift right by 1
    static SR2D_INLINE T srli8_1(T a) { return V::and_(V::template srli16<1>(a), V::set1_8(0x7f)); }

    // exact q = n / d for u32 vectors (d given as UDiv32)
    static SR2D_INLINE T udiv(T n, const UDiv32& dv, T magic, __m128i sh)
    {
        if (dv.pow2) return V::srl32v(n, sh);
        T q = V::mulhi_u32(n, magic);
        T t = V::add32(V::template srli32<1>(V::sub32(n, q)), q);
        return V::srl32v(t, sh);
    }

    // ------------------------------------------------------------- row engine
    template<class Op>
    static SR2D_INLINE void row(const int* s, int* d, int w, const Op& op)
    {
        int x = 0;
        for (; x + N <= w; x += N)
            V::storeu(d + x, op(V::loadu(s + x), V::loadu(d + x)));
        if (x < w)
        {
            SR2D_ALIGN(32) int ts[8] = { 0 }; SR2D_ALIGN(32) int td[8] = { 0 };
            const int n = w - x;
            for (int i = 0; i < n; ++i) { ts[i] = s[x + i]; td[i] = d[x + i]; }
            V::storeu(td, op(V::loadu(ts), V::loadu(td)));
            for (int i = 0; i < n; ++i) d[x + i] = td[i];
        }
    }

    template<class Op>
    static SR2D_INLINE void row_masked(const int* s, int* d, const int* m, int w, T maskex, T flip, const Op& op)
    {
        int x = 0;
        for (; x + N <= w; x += N)
        {
            T dv  = V::loadu(d + x);
            T sel = V::xor_(V::cmpeq32(V::and_(V::loadu(m + x), maskex), V::zero()), flip);
            V::storeu(d + x, V::select(sel, op(V::loadu(s + x), dv), dv));
        }
        if (x < w)
        {
            SR2D_ALIGN(32) int ts[8] = { 0 }; SR2D_ALIGN(32) int td[8] = { 0 }; SR2D_ALIGN(32) int tm[8] = { 0 };
            const int n = w - x;
            for (int i = 0; i < n; ++i) { ts[i] = s[x + i]; td[i] = d[x + i]; tm[i] = m[x + i]; }
            T dv  = V::loadu(td);
            T sel = V::xor_(V::cmpeq32(V::and_(V::loadu(tm), maskex), V::zero()), flip);
            V::storeu(td, V::select(sel, op(V::loadu(ts), dv), dv));
            for (int i = 0; i < n; ++i) d[x + i] = td[i];
        }
    }

    template<class Op>
    static void rect(const int* s, int* d, int w, int h, int ws, int wd, const Op& op)
    {
        if (w <= 0) return;
        for (int y = 0; y < h; ++y, s += ws, d += wd) row(s, d, w, op);
    }

    template<class Op>
    static void rect_masked(const int* s, int* d, const int* m, int w, int h, int maskex, int ws, int wd, int wm, int notm, const Op& op)
    {
        if (w <= 0) return;
        const T mx   = V::set1_32(maskex);
        const T flip = notm ? V::zero() : V::set1_32(-1);
        for (int y = 0; y < h; ++y, s += ws, d += wd, m += wm) row_masked(s, d, m, w, mx, flip, op);
    }

    // ------------------------------------------------------------ pixel ops
    struct OpPaint { SR2D_INLINE T operator()(T s, T) const { return s; } };
    struct OpConst { T c; explicit OpConst(int v) : c(V::set1_32(v)) {} SR2D_INLINE T operator()(T, T) const { return c; } };

    struct OpMod
    {   // d = (s * d) >> 8 per byte
        SR2D_INLINE T operator()(T s, T d) const
        {
            T lo = V::template srli16<8>(V::mullo16(V::lo8to16(s), V::lo8to16(d)));
            T hi = V::template srli16<8>(V::mullo16(V::hi8to16(s), V::hi8to16(d)));
            return V::packus16(lo, hi);
        }
    };
    struct OpMod2X
    {   // d = min(255, (s * d) >> 7)
        SR2D_INLINE T operator()(T s, T d) const
        {
            T lo = V::template srli16<7>(V::mullo16(V::lo8to16(s), V::lo8to16(d)));
            T hi = V::template srli16<7>(V::mullo16(V::hi8to16(s), V::hi8to16(d)));
            return V::packus16(lo, hi);
        }
    };
    struct OpAdd   { SR2D_INLINE T operator()(T s, T d) const { return V::adds_u8(s, d); } };
    struct OpAdd2D { SR2D_INLINE T operator()(T s, T d) const { return V::add32(V::and_(s, d), srli8_1(V::xor_(s, d))); } };
    struct OpMax   { SR2D_INLINE T operator()(T s, T d) const { return V::max_u8(s, d); } };
    struct OpMin   { SR2D_INLINE T operator()(T s, T d) const { return V::min_u8(s, d); } };
    struct OpAlphaT{ SR2D_INLINE T operator()(T s, T d) const { return V::select(V::template srai32<31>(s), s, d); } };

    struct OpAlphaB
    {   // rgb: d = (d*(256-a) + s*a) >> 8, a = src alpha; alpha byte of d unchanged
        T lanemask;
        OpAlphaB() : lanemask(V::set1_64(0x0000FFFFFFFFFFFFLL)) {}
        SR2D_INLINE T operator()(T s, T d) const
        {
            T a   = V::template srli32<24>(s);
            a     = V::or_(a, V::template slli32<16>(a));            // 16-bit lanes [a, a] per pixel dword
            T wlo = V::and_(V::unpacklo32(a, a), lanemask);          // [a,a,a,0] per pixel
            T whi = V::and_(V::unpackhi32(a, a), lanemask);
            T c256 = V::set1_16(256);
            T lo = V::add16(V::mullo16(V::lo8to16(d), V::sub16(c256, wlo)), V::mullo16(V::lo8to16(s), wlo));
            T hi = V::add16(V::mullo16(V::hi8to16(d), V::sub16(c256, whi)), V::mullo16(V::hi8to16(s), whi));
            return V::packus16(V::template srli16<8>(lo), V::template srli16<8>(hi));
        }
    };
    struct OpAlphaOver
    {   // premultiplied source-over, all 4 bytes: d = s + (d * inv) >> 8, inv = 256 - a - (a >> 7)
        // (inv is 0 for a = 255 and 256 for a = 0, so opaque source replaces, transparent leaves)
        T lanes8;
        OpAlphaOver() : lanes8(V::set1_16(256)) {}
        SR2D_INLINE T operator()(T s, T d) const
        {
            T a   = V::template srli32<24>(s);                          // 0..255 per dword
            T inv = V::sub32(V::sub32(V::set1_32(256), a), V::template srli32<7>(a));
            inv   = V::or_(inv, V::template slli32<16>(inv));           // [inv, inv] 16-bit lanes
            T wlo = V::unpacklo32(inv, inv), whi = V::unpackhi32(inv, inv);
            T lo  = V::template srli16<8>(V::mullo16(V::lo8to16(d), wlo));
            T hi  = V::template srli16<8>(V::mullo16(V::hi8to16(d), whi));
            return V::adds_u8(s, V::packus16(lo, hi));
        }
    };
    // Variants used by DRAW_WARP when it samples a PREMULTIPLIED copy of the source (filtered
    // modes): compositing straight colours that were interpolated across transparent pixels
    // would drag their (usually black) RGB into the edge; premultiplied sampling can't.
    struct OpAlphaOverKeepA
    {   // AlphaBlend semantics on premultiplied s: rgb = s + d * (1 - a), destination alpha unchanged
        T amask;
        OpAlphaOverKeepA() : amask(V::set1_32((int)0xff000000)) {}
        SR2D_INLINE T operator()(T s, T d) const
        {
            T a   = V::template srli32<24>(s);
            T inv = V::sub32(V::sub32(V::set1_32(256), a), V::template srli32<7>(a));
            inv   = V::or_(inv, V::template slli32<16>(inv));
            T wlo = V::unpacklo32(inv, inv), whi = V::unpackhi32(inv, inv);
            T lo  = V::template srli16<8>(V::mullo16(V::lo8to16(d), wlo));
            T hi  = V::template srli16<8>(V::mullo16(V::hi8to16(d), whi));
            T r   = V::adds_u8(s, V::packus16(lo, hi));
            return V::select(amask, d, r);
        }
    };
    // premultiplied -> straight: c' = min(255, round(c * 255 / a)), alpha kept
    static SR2D_INLINE T unpremul_px(T s)
    {
        typedef typename V::F F;
        // per byte with 16-bit math: 255/a as 8.8 fixed point per pixel via float rcp
        T a = V::template srli32<24>(s);
        F fa = V::maxf(V::cvt_i2f(a), V::set1f(1.0f));
        T k = V::cvt_f2i_round(V::mulf(V::set1f(255.0f * 256.0f), V::rcpf_nr(fa)));   // 255*256/a, <= 65280 -> fits u16
        k = V::min32(k, V::set1_32(65535));
        T k16 = V::or_(V::and_(k, V::set1_32(0xffff)), V::template slli32<16>(k));
        T klo = V::unpacklo32(k16, k16), khi = V::unpackhi32(k16, k16);
        // (c * k + 128) >> 8 with c <= a: c*k <= a * 255*256/a = 65280, + 128 still fits u16 (saturating add for safety)
        T clo = V::lo8to16(s), chi = V::hi8to16(s);
        const T half = V::set1_16(128);
        T rlo = V::template srli16<8>(V::adds_u16(V::mullo16(clo, klo), half));
        T rhi = V::template srli16<8>(V::adds_u16(V::mullo16(chi, khi), half));
        T c = V::packus16(rlo, rhi);
        return V::select(V::set1_32((int)0xff000000), s, c);                   // keep the alpha
    }
    struct OpAlphaTPremul
    {   // AlphaTest on premultiplied s: where a >= 128 write the un-premultiplied colour with its alpha
        SR2D_INLINE T operator()(T s, T d) const { return V::select(V::template srai32<31>(s), unpremul_px(s), d); }
    };
    struct OpUnpremul
    {   // copy the straight-alpha version of a premultiplied s
        SR2D_INLINE T operator()(T s, T) const { return unpremul_px(s); }
    };
    struct OpBlend
    {   // all 4 bytes: d = (d*(256-k) + s*k) >> 8
        T wk, winv;
        explicit OpBlend(int k) : wk(V::set1_16((short)(k & 255))), winv(V::set1_16((short)(256 - (k & 255)))) {}
        SR2D_INLINE T operator()(T s, T d) const
        {
            T lo = V::add16(V::mullo16(V::lo8to16(d), winv), V::mullo16(V::lo8to16(s), wk));
            T hi = V::add16(V::mullo16(V::hi8to16(d), winv), V::mullo16(V::hi8to16(s), wk));
            return V::packus16(V::template srli16<8>(lo), V::template srli16<8>(hi));
        }
    };
    // ------------------------------------------------------------ blend modes (Photoshop / PDF style)
    // Codes 32..57 (SR2D_BM_*) in the low byte of every 'op' argument. The rest of the op word:
    //   bits 16..24  opacity + 1 (0 = not given = full), bit 9 (SR2D_OP_DST_PREMUL) destination is
    //   premultiplied, bit 10 (SR2D_OP_SRC_PREMUL) source is premultiplied.
    // Model = an editor layer: effective source coverage w = srcAlpha * opacity; per channel
    //   straight dst : out = d + (B(s, d) - d) * w          (destination alpha untouched, like AlphaBlend)
    //   premul dst   : out = D * (1 - w) + w * ((1 - ad) * s + ad * B(s, d/ad)),  ao = w + ad - w * ad
    //                  (PDF 11.3.8 union compositing; transparent backdrop shows the source itself)
    // Work is done on 32-bit channel planes (b, g, r, a as separate vectors of N lanes): every mode,
    // separable or not, integer or float, is then the same code for SSE2 and AVX2.
    static SR2D_INLINE T bm_div255(T x)                       // round(x / 255), x in 0..65535 (exact)
    {
        T y = V::add32(x, V::set1_32(128));
        return V::template srli32<8>(V::add32(y, V::template srli32<8>(y)));
    }
    static SR2D_INLINE T bm_mul(T a, T b) { return bm_div255(V::mullo32(a, b)); }
    static SR2D_INLINE T bm_clamp(T v) { return V::min32(V::max32(v, V::zero()), V::set1_32(255)); }
    static SR2D_INLINE T bm_idiv(T num, T den)                // trunc(num / den) for den >= 1 (float, exact for these ranges)
    {
        typedef typename V::F F;
        F q = V::divf(V::cvt_i2f(num), V::cvt_i2f(V::max32(den, V::set1_32(1))));
        return V::cvtt_f2i(q);
    }
    static SR2D_INLINE T bm_screen(T s, T d) { return V::sub32(V::add32(s, d), bm_mul(s, d)); }
    static SR2D_INLINE T bm_burn(T s, T d)
    {   // d == 255 -> 255; s == 0 -> 0; else 255 - min(255, (255 - d) * 255 / s)
        const T c255 = V::set1_32(255), z = V::zero();
        T q = V::min32(bm_idiv(V::mullo32(V::sub32(c255, d), c255), s), c255);
        T r = V::sub32(c255, q);
        r = V::select(V::cmpeq32(s, z), z, r);
        return V::select(V::cmpeq32(d, c255), c255, r);
    }
    static SR2D_INLINE T bm_dodge(T s, T d)
    {   // d == 0 -> 0; s == 255 -> 255; else min(255, d * 255 / (255 - s))
        const T c255 = V::set1_32(255), z = V::zero();
        T q = V::min32(bm_idiv(V::mullo32(d, c255), V::sub32(c255, s)), c255);
        q = V::select(V::cmpeq32(s, c255), c255, q);
        return V::select(V::cmpeq32(d, z), z, q);
    }
    static SR2D_INLINE T bm_hardlight(T s, T d)
    {   // s <= 127: multiply(d, 2s)  else screen(d, 2s - 255)
        T s2 = V::add32(s, s);
        T lo = bm_mul(d, s2), hi = bm_screen(d, V::sub32(s2, V::set1_32(255)));
        return V::select(V::cmpgt32(s, V::set1_32(127)), hi, lo);
    }
    static SR2D_INLINE T bm_softlight(T s, T d)
    {   // PDF: cs <= .5: cb - (1-2cs) cb (1-cb)   else cb + (2cs-1) (D(cb) - cb), D = cb <= .25 ? ((16cb-12)cb+4)cb : sqrt(cb)
        typedef typename V::F F;
        const F k = V::set1f(1.0f / 255.0f);
        F cs = V::mulf(V::cvt_i2f(s), k), cb = V::mulf(V::cvt_i2f(d), k), one = V::set1f(1.0f);
        F cs2 = V::addf(cs, cs);
        F lo = V::subf(cb, V::mulf(V::mulf(V::subf(one, cs2), cb), V::subf(one, cb)));
        F dpoly = V::mulf(V::addf(V::mulf(V::subf(V::mulf(V::set1f(16.0f), cb), V::set1f(12.0f)), cb), V::set1f(4.0f)), cb);
        F dd = V::selectf(V::cmpgtf(cb, V::set1f(0.25f)), V::sqrtf_(cb), dpoly);
        F hi = V::addf(cb, V::mulf(V::subf(cs2, one), V::subf(dd, cb)));
        F r = V::selectf(V::cmpgtf(cs, V::set1f(0.5f)), hi, lo);
        return bm_clamp(V::cvt_f2i_round(V::mulf(r, V::set1f(255.0f))));
    }
    static SR2D_INLINE T bm_vivid(T s, T d)
    {   // s <= 127: burn(2s, d)  else dodge(2s - 255, d)
        T s2 = V::add32(s, s);
        return V::select(V::cmpgt32(s, V::set1_32(127)), bm_dodge(V::sub32(s2, V::set1_32(255)), d), bm_burn(s2, d));
    }
    static SR2D_INLINE T bm_pin(T s, T d)
    {   // s <= 127: min(d, 2s)  else max(d, 2s - 255)
        T s2 = V::add32(s, s);
        return V::select(V::cmpgt32(s, V::set1_32(127)), V::max32(d, V::sub32(s2, V::set1_32(255))), V::min32(d, s2));
    }
    static SR2D_INLINE typename V::F bm_lum_f(typename V::F r, typename V::F g, typename V::F b)
    { return V::addf(V::addf(V::mulf(r, V::set1f(0.3f)), V::mulf(g, V::set1f(0.59f))), V::mulf(b, V::set1f(0.11f))); }
    // PDF ClipColor: keep the colour inside 0..255 while preserving its luminosity
    static SR2D_INLINE void bm_clipcolor(typename V::F& r, typename V::F& g, typename V::F& b)
    {
        typedef typename V::F F;
        F l = bm_lum_f(r, g, b), n = V::minf(r, V::minf(g, b)), x = V::maxf(r, V::maxf(g, b));
        F zero = V::set1f(0.0f), c255 = V::set1f(255.0f), tiny = V::set1f(1e-6f);
        T under = V::cmpgtf(zero, n), over = V::cmpgtf(x, c255);
        F ku = V::divf(l, V::maxf(V::subf(l, n), tiny));                       // (c - l) * l / (l - n)
        F ko = V::divf(V::subf(c255, l), V::maxf(V::subf(x, l), tiny));        // (c - l) * (255 - l) / (x - l)
        F ru = V::addf(l, V::mulf(V::subf(r, l), ku)), gu = V::addf(l, V::mulf(V::subf(g, l), ku)), bu = V::addf(l, V::mulf(V::subf(b, l), ku));
        r = V::selectf(under, ru, r); g = V::selectf(under, gu, g); b = V::selectf(under, bu, b);
        l = bm_lum_f(r, g, b);   // unchanged in exact arithmetic; recomputed to keep the second step consistent
        F ro = V::addf(l, V::mulf(V::subf(r, l), ko)), go = V::addf(l, V::mulf(V::subf(g, l), ko)), bo = V::addf(l, V::mulf(V::subf(b, l), ko));
        r = V::selectf(over, ro, r); g = V::selectf(over, go, g); b = V::selectf(over, bo, b);
    }
    static SR2D_INLINE void bm_setlum(typename V::F& r, typename V::F& g, typename V::F& b, typename V::F l)
    {
        typename V::F d = V::subf(l, bm_lum_f(r, g, b));
        r = V::addf(r, d); g = V::addf(g, d); b = V::addf(b, d);
        bm_clipcolor(r, g, b);
    }
    // PDF SetSat: max channel -> s, min -> 0, mid -> (mid - min) * s / (max - min); grey -> 0
    static SR2D_INLINE void bm_setsat(typename V::F& r, typename V::F& g, typename V::F& b, typename V::F sat)
    {
        typedef typename V::F F;
        F mx = V::maxf(r, V::maxf(g, b)), mn = V::minf(r, V::minf(g, b));
        F range = V::subf(mx, mn), zero = V::set1f(0.0f);
        T has = V::cmpgtf(range, zero);
        F k = V::divf(sat, V::maxf(range, V::set1f(1e-6f)));
        // per channel: c == mx -> sat, c == mn -> 0, else (c - mn) * k   (ties: the max wins, then the min)
        F rr = V::selectf(V::cmpgtf(r, V::subf(mx, V::set1f(1e-6f))), sat, V::selectf(V::cmpgtf(V::addf(mn, V::set1f(1e-6f)), r), zero, V::mulf(V::subf(r, mn), k)));
        F gg = V::selectf(V::cmpgtf(g, V::subf(mx, V::set1f(1e-6f))), sat, V::selectf(V::cmpgtf(V::addf(mn, V::set1f(1e-6f)), g), zero, V::mulf(V::subf(g, mn), k)));
        F bb = V::selectf(V::cmpgtf(b, V::subf(mx, V::set1f(1e-6f))), sat, V::selectf(V::cmpgtf(V::addf(mn, V::set1f(1e-6f)), b), zero, V::mulf(V::subf(b, mn), k)));
        r = V::selectf(has, rr, zero); g = V::selectf(has, gg, zero); b = V::selectf(has, bb, zero);
    }
    struct Planes { T b, g, r; };
    // B(s, d) for the non-separable modes (float, 0..255)
    static SR2D_INLINE Planes bm_nonsep(int mode, const Planes& s, const Planes& d)
    {
        typedef typename V::F F;
        F sr = V::cvt_i2f(s.r), sg = V::cvt_i2f(s.g), sb = V::cvt_i2f(s.b);
        F dr = V::cvt_i2f(d.r), dg = V::cvt_i2f(d.g), db = V::cvt_i2f(d.b);
        F r, g, b;
        switch (mode)
        {
        case SR2D_BM_HUE:        r = sr; g = sg; b = sb; bm_setsat(r, g, b, V::subf(V::maxf(dr, V::maxf(dg, db)), V::minf(dr, V::minf(dg, db)))); bm_setlum(r, g, b, bm_lum_f(dr, dg, db)); break;
        case SR2D_BM_SATURATION: r = dr; g = dg; b = db; bm_setsat(r, g, b, V::subf(V::maxf(sr, V::maxf(sg, sb)), V::minf(sr, V::minf(sg, sb)))); bm_setlum(r, g, b, bm_lum_f(dr, dg, db)); break;
        case SR2D_BM_COLOR:      r = sr; g = sg; b = sb; bm_setlum(r, g, b, bm_lum_f(dr, dg, db)); break;
        default:                 r = dr; g = dg; b = db; bm_setlum(r, g, b, bm_lum_f(sr, sg, sb)); break;   // LUMINOSITY
        }
        Planes o;
        o.r = bm_clamp(V::cvt_f2i_round(r)); o.g = bm_clamp(V::cvt_f2i_round(g)); o.b = bm_clamp(V::cvt_f2i_round(b));
        return o;
    }
#define SR2D_BM_SEP(FN) { o.b = FN(s.b, d.b); o.g = FN(s.g, d.g); o.r = FN(s.r, d.r); break; }
    static SR2D_INLINE T bm_darken(T s, T d)   { return V::min32(s, d); }
    static SR2D_INLINE T bm_lighten(T s, T d)  { return V::max32(s, d); }
    static SR2D_INLINE T bm_multiply(T s, T d) { return bm_mul(s, d); }
    static SR2D_INLINE T bm_linburn(T s, T d)  { return V::max32(V::sub32(V::add32(s, d), V::set1_32(255)), V::zero()); }
    static SR2D_INLINE T bm_lindodge(T s, T d) { return V::min32(V::add32(s, d), V::set1_32(255)); }
    static SR2D_INLINE T bm_overlay(T s, T d)  { return bm_hardlight(d, s); }
    static SR2D_INLINE T bm_linlight(T s, T d) { return bm_clamp(V::sub32(V::add32(d, V::add32(s, s)), V::set1_32(255))); }
    static SR2D_INLINE T bm_hardmix(T s, T d)  { return V::andnot(V::cmpgt32(V::set1_32(255), V::add32(s, d)), V::set1_32(255)); }   // s + d >= 255 -> 255 else 0
    static SR2D_INLINE T bm_difference(T s, T d) { return V::sub32(V::max32(s, d), V::min32(s, d)); }
    static SR2D_INLINE T bm_exclusion(T s, T d) { T m = bm_mul(s, d); return V::sub32(V::sub32(V::add32(s, d), m), m); }
    static SR2D_INLINE T bm_subtract(T s, T d) { return V::max32(V::sub32(d, s), V::zero()); }
    static SR2D_INLINE T bm_divide(T s, T d)
    {   // s == 0 -> 255 (d > 0) / 0 (d == 0, Photoshop); else min(255, d * 255 / s)
        const T c255 = V::set1_32(255), z = V::zero();
        T q = V::min32(bm_idiv(V::mullo32(d, c255), s), c255);
        T s0 = V::select(V::cmpeq32(d, z), z, c255);
        return V::select(V::cmpeq32(s, z), s0, q);
    }
    static SR2D_INLINE Planes bm_blend(int mode, const Planes& s, const Planes& d)
    {
        Planes o;
        switch (mode)
        {
        case SR2D_BM_DARKEN:        SR2D_BM_SEP(bm_darken)
        case SR2D_BM_MULTIPLY:      SR2D_BM_SEP(bm_multiply)
        case SR2D_BM_COLOR_BURN:    SR2D_BM_SEP(bm_burn)
        case SR2D_BM_LINEAR_BURN:   SR2D_BM_SEP(bm_linburn)
        case SR2D_BM_LIGHTEN:       SR2D_BM_SEP(bm_lighten)
        case SR2D_BM_SCREEN:        SR2D_BM_SEP(bm_screen)
        case SR2D_BM_COLOR_DODGE:   SR2D_BM_SEP(bm_dodge)
        case SR2D_BM_LINEAR_DODGE:  SR2D_BM_SEP(bm_lindodge)
        case SR2D_BM_OVERLAY:       SR2D_BM_SEP(bm_overlay)
        case SR2D_BM_SOFT_LIGHT:    SR2D_BM_SEP(bm_softlight)
        case SR2D_BM_HARD_LIGHT:    SR2D_BM_SEP(bm_hardlight)
        case SR2D_BM_VIVID_LIGHT:   SR2D_BM_SEP(bm_vivid)
        case SR2D_BM_LINEAR_LIGHT:  SR2D_BM_SEP(bm_linlight)
        case SR2D_BM_PIN_LIGHT:     SR2D_BM_SEP(bm_pin)
        case SR2D_BM_HARD_MIX:      SR2D_BM_SEP(bm_hardmix)
        case SR2D_BM_DIFFERENCE:    SR2D_BM_SEP(bm_difference)
        case SR2D_BM_EXCLUSION:     SR2D_BM_SEP(bm_exclusion)
        case SR2D_BM_SUBTRACT:      SR2D_BM_SEP(bm_subtract)
        case SR2D_BM_DIVIDE:        SR2D_BM_SEP(bm_divide)
        case SR2D_BM_DARKER_COLOR: case SR2D_BM_LIGHTER_COLOR:
        {   // Photoshop: compare the sum of the channels, take the whole colour
            T ss = V::add32(V::add32(s.r, s.g), s.b), ds = V::add32(V::add32(d.r, d.g), d.b);
            T takeS = mode == SR2D_BM_DARKER_COLOR ? V::cmpgt32(ds, ss) : V::cmpgt32(ss, ds);
            o.r = V::select(takeS, s.r, d.r); o.g = V::select(takeS, s.g, d.g); o.b = V::select(takeS, s.b, d.b);
            break;
        }
        case SR2D_BM_HUE: case SR2D_BM_SATURATION: case SR2D_BM_COLOR: case SR2D_BM_LUMINOSITY:
            o = bm_nonsep(mode, s, d); break;
        default: o = s; break;   // NORMAL (DISSOLVE is handled before the blend)
        }
        return o;
    }
#undef SR2D_BM_SEP
    // c * 255 / a rounded, clamped to 255 (a = 0 -> c itself, which is 0 for valid premultiplied data)
    static SR2D_INLINE T bm_unpremul(T c, T a)
    {
        typedef typename V::F F;
        F q = V::mulf(V::cvt_i2f(V::mullo32(c, V::set1_32(255))), V::rcpf_nr(V::cvt_i2f(V::max32(a, V::set1_32(1)))));
        return V::min32(V::cvt_f2i_round(q), V::set1_32(255));
    }
    template<bool SP, bool DP>
    struct OpBlendMode
    {
        int mode; T opa; mutable T rng;
        explicit OpBlendMode(int op)
        {
            mode = op & 0xff;
            int o = (op >> 16) & 0x1ff; o = o == 0 ? 256 : o - 1;   // 0 = not given -> 256
            opa = V::set1_32(o);
            SR2D_ALIGN(32) unsigned seed[8];
            for (int i = 0; i < 8; ++i) seed[i] = 0x9E3779B9u * (unsigned)(i + 1) ^ 0x7F4A7C15u;
            rng = V::loadu(seed);
        }
        SR2D_INLINE T operator()(T sv, T dv) const
        {
            const T m8 = V::set1_32(255), c255 = m8, z = V::zero();
            Planes s, d;
            s.b = V::and_(sv, m8); s.g = V::and_(V::template srli32<8>(sv), m8); s.r = V::and_(V::template srli32<16>(sv), m8);
            T as = V::template srli32<24>(sv);
            d.b = V::and_(dv, m8); d.g = V::and_(V::template srli32<8>(dv), m8); d.r = V::and_(V::template srli32<16>(dv), m8);
            T ad = V::template srli32<24>(dv);
            if (SP) { s.b = bm_unpremul(s.b, as); s.g = bm_unpremul(s.g, as); s.r = bm_unpremul(s.r, as); }
            Planes cd = d;
            if (DP) { cd.b = bm_unpremul(d.b, ad); cd.g = bm_unpremul(d.g, ad); cd.r = bm_unpremul(d.r, ad); }
            T w = V::template srli32<8>(V::add32(V::mullo32(as, opa), V::set1_32(128)));   // round(as * opa / 256): 0..255 effective coverage
            if (mode == SR2D_BM_DISSOLVE)
            {   // xorshift32 per lane; the pixel is the source (opaque) with probability w / 255
                T x = rng; x = V::xor_(x, V::template slli32<13>(x)); x = V::xor_(x, V::template srli32<17>(x)); x = V::xor_(x, V::template slli32<5>(x)); rng = x;
                T take = V::cmpgt32(w, V::template srli32<24>(x));
                T sc = V::or_(V::or_(s.b, V::template slli32<8>(s.g)), V::or_(V::template slli32<16>(s.r), V::set1_32((int)0xff000000)));
                if (!DP) sc = V::or_(V::and_(sc, m8), V::or_(V::and_(sc, V::set1_32(0xffff00)), V::and_(dv, V::set1_32((int)0xff000000))));
                return V::select(take, sc, dv);
            }
            Planes b = bm_blend(mode, s, cd);
            T ob, og, orr, oa;
            if (!DP)
            {   // out = d + (B - d) * w / 255, alpha untouched
                T iw = V::sub32(c255, w);
                ob = bm_div255(V::add32(V::mullo32(d.b, iw), V::mullo32(b.b, w)));
                og = bm_div255(V::add32(V::mullo32(d.g, iw), V::mullo32(b.g, w)));
                orr = bm_div255(V::add32(V::mullo32(d.r, iw), V::mullo32(b.r, w)));
                oa = ad;
            }
            else
            {   // X = (1 - ad) s + ad B ; out = D (1 - w) + w X ; ao = w + ad - w ad
                T iad = V::sub32(c255, ad), iw = V::sub32(c255, w);
                T xb = bm_div255(V::add32(V::mullo32(iad, s.b), V::mullo32(ad, b.b)));
                T xg = bm_div255(V::add32(V::mullo32(iad, s.g), V::mullo32(ad, b.g)));
                T xr = bm_div255(V::add32(V::mullo32(iad, s.r), V::mullo32(ad, b.r)));
                ob = bm_div255(V::add32(V::mullo32(d.b, iw), V::mullo32(xb, w)));
                og = bm_div255(V::add32(V::mullo32(d.g, iw), V::mullo32(xg, w)));
                orr = bm_div255(V::add32(V::mullo32(d.r, iw), V::mullo32(xr, w)));
                oa = V::sub32(V::add32(w, ad), bm_mul(w, ad));
                (void)z;
            }
            return V::or_(V::or_(ob, V::template slli32<8>(og)), V::or_(V::template slli32<16>(orr), V::template slli32<24>(oa)));
        }
    };
    static SR2D_INLINE bool is_blend_mode(int op) { const int c = op & 0xff; return c >= SR2D_BM_FIRST && c <= SR2D_BM_LAST; }
    // one pixel through the vector functor (line pixels, tails)
    template<class Op>
    static SR2D_INLINE int op_px(const Op& op, int s, int d)
    {
        SR2D_ALIGN(32) int ts[8] = { s, s, s, s, s, s, s, s }; SR2D_ALIGN(32) int td[8] = { d, d, d, d, d, d, d, d };
        return V::lane0(op(V::loadu(ts), V::loadu(td)));
    }
    // dispatch helper: calls f(functor) with the right SP/DP variant for the op word
    template<class F>
    static SR2D_INLINE void with_blend_mode(int op, const F& f)
    {
        const bool sp = (op & SR2D_OP_SRC_PREMUL) != 0, dp = (op & SR2D_OP_DST_PREMUL) != 0;
        if (sp) { if (dp) f(OpBlendMode<true, true>(op)); else f(OpBlendMode<true, false>(op)); }
        else    { if (dp) f(OpBlendMode<false, true>(op)); else f(OpBlendMode<false, false>(op)); }
    }

    struct OpMulAdd
    {   // per byte: d = clamp(((s * m) >> 7) + 2*a - 256)
        T m16, a16;
        OpMulAdd(int vmul, int vadd)
        {
            m16 = V::lo8to16(V::set1_32(vmul));
            a16 = V::sub16(V::template slli16<1>(V::lo8to16(V::set1_32(vadd))), V::set1_16(256));
        }
        SR2D_INLINE T operator()(T s, T) const
        {
            T lo = V::add16(V::template srli16<7>(V::mullo16(V::lo8to16(s), m16)), a16);
            T hi = V::add16(V::template srli16<7>(V::mullo16(V::hi8to16(s), m16)), a16);
            return V::packus16(lo, hi);
        }
    };
    struct OpMoveByte
    {
        __m128i cs, cd; T bmask, dmask;
        OpMoveByte(int msrc, int mdst)
        {
            msrc &= 3; mdst &= 3;
            cs = _mm_cvtsi32_si128(msrc * 8); cd = _mm_cvtsi32_si128(mdst * 8);
            bmask = V::set1_32(0xff); dmask = V::set1_32(0xff << (mdst * 8));
        }
        SR2D_INLINE T operator()(T s, T d) const
        {
            T v = V::sll32v(V::and_(V::srl32v(s, cs), bmask), cd);
            return V::or_(V::andnot(dmask, d), v);
        }
    };
    struct OpMoveBit
    {   // d |= mdst * ((s & msrc) != 0)
        T msrc, mdst;
        OpMoveBit(int a, int b) : msrc(V::set1_32(a)), mdst(V::set1_32(b)) {}
        SR2D_INLINE T operator()(T s, T d) const
        {
            T nz = V::cmpeq32(V::and_(s, msrc), V::zero());          // -1 where zero
            return V::or_(d, V::andnot(nz, mdst));
        }
    };
    struct OpDPBM
    {   // d = 0x10101 * clamp(dot(s.bgr - 128, c.bgr - 128) >> 6)
        T wlo, c128;
        explicit OpDPBM(int c)
        {
            int b = (c & 0xff) - 128, g = ((c >> 8) & 0xff) - 128, r = ((c >> 16) & 0xff) - 128;
            SR2D_ALIGN(32) short w[16];
            for (int i = 0; i < 16; i += 4) { w[i] = (short)b; w[i + 1] = (short)g; w[i + 2] = (short)r; w[i + 3] = 0; }
            wlo = V::loadu(w);
            c128 = V::set1_16(128);
        }
        SR2D_INLINE T operator()(T s, T) const
        {
            T lo = V::madd16(V::sub16(V::lo8to16(s), c128), wlo);   // per pixel: [B*wb+G*wg, R*wr]
            T hi = V::madd16(V::sub16(V::hi8to16(s), c128), wlo);
            lo = V::add32(lo, V::template srli64<32>(lo));           // even lanes = dot
            hi = V::add32(hi, V::template srli64<32>(hi));
            T dot = V::unpacklo64(V::template shuffle32<_MM_SHUFFLE(2, 0, 2, 0)>(lo),
                                  V::template shuffle32<_MM_SHUFFLE(2, 0, 2, 0)>(hi));
            dot = V::template srai32<6>(dot);
            dot = V::andnot(V::template srai32<31>(dot), dot);       // < 0 -> 0
            T over = V::cmpgt32(dot, V::set1_32(255));
            dot = V::select(over, V::set1_32(255), dot);
            return V::or_(V::or_(dot, V::template slli32<8>(dot)), V::template slli32<16>(dot));
        }
    };

    // =================================================================== misc
    static void MOVSD_(int* src, int* dst, int cnt)
    {   // forward dword copy (rep movsd semantics)
        int i = 0;
        for (; i + N <= cnt; i += N) V::storeu(dst + i, V::loadu(src + i));
        for (; i < cnt; ++i) dst[i] = src[i];
    }

    static void BPP_32TO24(sr2d_uint* src, sr2d_byte* dest, int w, int h, int stride)
    {
        for (int y = 0; y < h; ++y, src += w, dest += stride)
        {
            int x = 0;
            sr2d_byte* d = dest;
            for (; x + 4 <= w; x += 4, d += 12)
            {
                uint32_t p0 = src[x], p1 = src[x + 1], p2 = src[x + 2], p3 = src[x + 3];
                uint32_t d0 = (p0 & 0xffffffu) | (p1 << 24);
                uint32_t d1 = ((p1 >> 8) & 0xffffu) | (p2 << 16);
                uint32_t d2 = ((p2 >> 16) & 0xffu) | (p3 << 8);
                memcpy(d, &d0, 4); memcpy(d + 4, &d1, 4); memcpy(d + 8, &d2, 4);
            }
            for (; x < w; ++x, d += 3)
            {
                uint32_t p = src[x];
                d[0] = (sr2d_byte)p; d[1] = (sr2d_byte)(p >> 8); d[2] = (sr2d_byte)(p >> 16);
            }
        }
    }

    static void CLEAR_C(int* dst, int w, int h, int wd, int c)
    {
        if (w <= 0) return;
        const T cv = V::set1_32(c);
        for (int y = 0; y < h; ++y, dst += wd)
        {
            int x = 0;
            for (; x + N <= w; x += N) V::storeu(dst + x, cv);
            for (; x < w; ++x) dst[x] = c;
        }
    }

    static void MASK_CLEAR_C(int* dest, int* mask, int w, int h, int maskex, int wd, int wm, int c, int notm)
    {
        rect_masked(dest, dest, mask, w, h, maskex, wd, wd, wm, notm, OpConst(c));
    }

    static void V_MUL_ADD(int* src, int* dst, int w, int h, int ws, int wd, int vmul, int vadd)
    {
        rect(src, dst, w, h, ws, wd, OpMulAdd(vmul, vadd));
    }
    static void MASK_V_MUL_ADD(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int vmul, int vadd)
    {
        rect_masked(src, dst, mask, w, h, maskex, ws, wd, wm, notm, OpMulAdd(vmul, vadd));
    }

    static int MASK_INTERSECT(int* src, int* dst, int w, int h, int ws, int wd, int maskex)
    {
        if (w <= 0 || h <= 0) return 0;
        const T mx = V::set1_32(maskex);
        int zeros = 0;
        for (int y = 0; y < h; ++y, src += ws, dst += wd)
        {
            T acc = V::zero();
            int x = 0;
            for (; x + N <= w; x += N)
                acc = V::sub32(acc, V::cmpeq32(V::and_(V::and_(V::loadu(src + x), V::loadu(dst + x)), mx), V::zero()));
            zeros += (int)V::hsum32(acc);
            for (; x < w; ++x) zeros += (int)((src[x] & dst[x] & maskex) == 0);
        }
        return w * h - zeros;
    }

    static void CLR_ALPHA(int* dest, int size)
    {
        const T a = V::set1_32((int)0xff000000);
        int i = 0;
        for (; i + N <= size; i += N) V::storeu(dest + i, V::or_(V::loadu(dest + i), a));
        for (; i < size; ++i) dest[i] |= (int)0xff000000;
    }

    // The original abs() is off by two for negative inputs (-x-2). It only
    // influences the major-axis choice for exact 45-degree lines, but we keep
    // it so that dot phases are identical to the original.
    static SR2D_INLINE int abs_q(int x) { return x >= 0 ? x : (-x - 2); }

    static void DRAW_DOTLINE(int* dest, int w, SR2D_Point* p1, SR2D_Point* p2, int dotstep, int col, int isxor)
    {
        SR2D_Point* p;
        int x, y, d;
        // (original quirk kept: the points are modified in place; unsigned shifts = same bits, no UB for negatives)
        p1->x = (int)((unsigned)p1->x << 16) + 32767;
        p1->y = (int)((unsigned)p1->y << 16) + 32767;
        p2->x = (int)((unsigned)p2->x << 16) + 32767;
        p2->y = (int)((unsigned)p2->y << 16) + 32767;
        if (abs_q(p2->x - p1->x) > abs_q(p2->y - p1->y))
        {
            if (p1->x > p2->x) { p = p1; p1 = p2; p2 = p; }
            x = p1->x >> 16;
            y = p1->y;
            d = ((int)(((int64_t)(p2->y - p1->y) * 65536) / (p2->x - p1->x))) * dotstep;
            const int xe = p2->x >> 16;
            if (isxor == 0) { while (x <= xe) { dest[x + (y >> 16) * w]  = col; x += dotstep; y += d; } }
            else            { while (x <= xe) { dest[x + (y >> 16) * w] ^= col; x += dotstep; y += d; } }
        }
        else
        {
            if (p2->y == p1->y) return;
            if (p1->y > p2->y) { p = p1; p1 = p2; p2 = p; }
            x = p1->x;
            y = p1->y >> 16;
            d = ((int)(((int64_t)(p2->x - p1->x) * 65536) / (p2->y - p1->y))) * dotstep;
            const int ye = p2->y >> 16;
            if (isxor == 0) { while (y <= ye) { dest[(x >> 16) + y * w]  = col; x += d; y += dotstep; } }
            else            { while (y <= ye) { dest[(x >> 16) + y * w] ^= col; x += d; y += dotstep; } }
        }
    }

    // ================================================================ filters
#define SR2D_FILTER(NAME, MNAME, OPEXPR) \
    static void NAME(int* src, int* dst, int w, int h, int ws, int wd) { rect(src, dst, w, h, ws, wd, OPEXPR); } \
    static void MNAME(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm) \
    { rect_masked(src, dst, mask, w, h, maskex, ws, wd, wm, notm, OPEXPR); }

    static void PAINT(int* src, int* dst, int w, int h, int ws, int wd)
    {
        if (w <= 0) return;
        for (int y = 0; y < h; ++y, src += ws, dst += wd) copy_px(src, dst, w);
    }
    static void MASK_PAINT(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
    { rect_masked(src, dst, mask, w, h, maskex, ws, wd, wm, notm, OpPaint()); }

    SR2D_FILTER(MOD_,    MASK_MOD,     OpMod())
    SR2D_FILTER(MOD_2X,  MASK_MOD_2X,  OpMod2X())
    SR2D_FILTER(ADD_,    MASK_ADD,     OpAdd())
    SR2D_FILTER(ADD_2D,  MASK_ADD_2D,  OpAdd2D())
    SR2D_FILTER(ALPHA_T, MASK_ALPHA_T, OpAlphaT())
    SR2D_FILTER(ALPHA_B, MASK_ALPHA_B, OpAlphaB())
    SR2D_FILTER(ALPHA_OVER, MASK_ALPHA_OVER, OpAlphaOver())

    static void PREMUL_ALPHA(int* p, int n)
    {   // rgb = (rgb * a + 127) / 255 per byte, alpha unchanged; exact division via *257 >> 16
        for (int i = 0; i < n; ++i)
        {
            const uint32_t v = (uint32_t)p[i], a = v >> 24;
            if (a == 255) continue;
            if (a == 0) { p[i] = 0; continue; }
            uint32_t r = 0;
            for (int sh = 0; sh < 24; sh += 8)
            {
                uint32_t c = ((v >> sh) & 0xff) * a + 127;
                c = (c * 0x8081u) >> 23;                 // == c / 255 exactly for c < 65536
                r |= c << sh;
            }
            p[i] = (int)(r | (a << 24));
        }
    }
    SR2D_FILTER(MAX_,    MASK_MAX,     OpMax())
    SR2D_FILTER(MIN_,    MASK_MIN,     OpMin())
#undef SR2D_FILTER

    static void MOVE_BYTE(int* src, int* dst, int w, int h, int ws, int wd, int movesrc, int movedst)
    { rect(src, dst, w, h, ws, wd, OpMoveByte(movesrc, movedst)); }
    static void MASK_MOVE_BYTE(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int movesrc, int movedst)
    { rect_masked(src, dst, mask, w, h, maskex, ws, wd, wm, notm, OpMoveByte(movesrc, movedst)); }

    static void MOVE_BIT(int* src, int* dst, int w, int h, int ws, int wd, int movesrc, int movedst)
    { rect(src, dst, w, h, ws, wd, OpMoveBit(movesrc, movedst)); }
    static void MASK_MOVE_BIT(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int movesrc, int movedst)
    { rect_masked(src, dst, mask, w, h, maskex, ws, wd, wm, notm, OpMoveBit(movesrc, movedst)); }

    static void BLEND(int* src, int* dst, int w, int h, int ws, int wd, int k)
    { rect(src, dst, w, h, ws, wd, OpBlend(k)); }
    static void MASK_BLEND(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int k)
    { rect_masked(src, dst, mask, w, h, maskex, ws, wd, wm, notm, OpBlend(k)); }
    static void BLEND_MODE(int* src, int* dst, int w, int h, int ws, int wd, int op)
    {
        if (!src || !dst || !is_blend_mode(op)) return;
        with_blend_mode(op, [&](const auto& f) { rect(src, dst, w, h, ws, wd, f); });
    }
    static void MASK_BLEND_MODE(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int op)
    {
        if (!src || !dst || !mask || !is_blend_mode(op)) return;
        with_blend_mode(op, [&](const auto& f) { rect_masked(src, dst, mask, w, h, maskex, ws, wd, wm, notm, f); });
    }

    // ============================================================= transforms
    static void ADD_COLOR_KEY(int* src, int w, int h, int cKey)
    {   // pixels whose RGB == key get alpha cleared
        const int n = w * h;
        const T key = V::set1_32(cKey & 0xffffff), rgb = V::set1_32(0xffffff), amask = V::set1_32((int)0xff000000);
        int i = 0;
        for (; i + N <= n; i += N)
        {
            T p  = V::loadu(src + i);
            T eq = V::cmpeq32(V::and_(p, rgb), key);
            V::storeu(src + i, V::andnot(V::and_(eq, amask), p));
        }
        for (; i < n; ++i) { int c = src[i] & 0xffffff; if (c == (cKey & 0xffffff)) src[i] = c; }
    }

    static void DRAW_ROT(int* src, int* dest, int L, int T_, int R, int B, int sx, int sy, int dx, int dy, int sw, int sh, int dw, int SinA, int CosA)
    {
        int ssx = (int)((unsigned)sx << 16) - (dx - L) * CosA;    // unsigned shift: same bits, no UB for negative sx
        int ssy = (int)((unsigned)sy << 16) - (dx - L) * SinA;
        const int ssw = sw << 16;
        const int ssh = sh << 16;
        if (ssw <= 0 || ssh <= 0) return;                 // original never draws in this case
        int addy = T_ * dw;
        T_ -= dy;
        B  -= dy;
#if SR2D_LEVEL >= 2
        const __m256i lane = _mm256_set_epi32(7, 6, 5, 4, 3, 2, 1, 0);
        const __m256i cosl = _mm256_mullo_epi32(lane, _mm256_set1_epi32(CosA));
        const __m256i sinl = _mm256_mullo_epi32(lane, _mm256_set1_epi32(SinA));
        const __m256i cos8 = _mm256_set1_epi32(CosA * 8), sin8 = _mm256_set1_epi32(SinA * 8);
        const __m256i vssw = _mm256_set1_epi32(ssw), vssh = _mm256_set1_epi32(ssh), vsw = _mm256_set1_epi32(sw), m1 = _mm256_set1_epi32(-1);
#endif
        for (int y = T_; y < B; ++y, addy += dw)
        {
            int xx = ssx - y * SinA;
            int yy = ssy + y * CosA;
            int* drow = dest + addy;
            int x = L;
#if SR2D_LEVEL >= 2
            __m256i vx = _mm256_add_epi32(_mm256_set1_epi32(xx), cosl);
            __m256i vy = _mm256_add_epi32(_mm256_set1_epi32(yy), sinl);
            for (; x + 8 <= R; x += 8)
            {
                __m256i inx = _mm256_and_si256(_mm256_cmpgt_epi32(vssw, vx), _mm256_cmpgt_epi32(vx, m1));
                __m256i iny = _mm256_and_si256(_mm256_cmpgt_epi32(vssh, vy), _mm256_cmpgt_epi32(vy, m1));
                __m256i ok  = _mm256_and_si256(inx, iny);
                __m256i idx = _mm256_add_epi32(_mm256_mullo_epi32(_mm256_srai_epi32(vy, 16), vsw), _mm256_srai_epi32(vx, 16));
                __m256i old = _mm256_loadu_si256((const __m256i*)(drow + x));
                __m256i v   = _mm256_mask_i32gather_epi32(old, src, idx, ok, 4);
                _mm256_storeu_si256((__m256i*)(drow + x), v);
                vx = _mm256_add_epi32(vx, cos8);
                vy = _mm256_add_epi32(vy, sin8);
            }
            xx = _mm_cvtsi128_si32(_mm256_castsi256_si128(vx));
            yy = _mm_cvtsi128_si32(_mm256_castsi256_si128(vy));
#endif
            for (; x < R; ++x)
            {
                if ((unsigned)xx < (unsigned)ssw && (unsigned)yy < (unsigned)ssh)
                    drow[x] = src[sw * (yy >> 16) + (xx >> 16)];
                xx += CosA;
                yy += SinA;
            }
        }
    }

    static SR2D_INLINE int bilerp_px(const int* src, const int* drow, int x, int xx, int yy, int sw, int sh)
    {
        const int ix = xx >> 16;
        const int iy = yy >> 16;
        if (ix < -1 || ix >= sw || iy < -1 || iy >= sh) return drow[x];
        const int tx = xx & 0xFFFF;
        const int ty = yy & 0xFFFF;
        const int dpx = drow[x];
        const int base = sw * iy + ix;
        const int c11 = (ix == -1 || iy == -1)         ? dpx : src[base];
        const int c21 = (ix == sw - 1 || iy == -1)     ? dpx : src[base + 1];
        const int c12 = (ix == -1 || iy == sh - 1)     ? dpx : src[base + sw];
        const int c22 = (ix == sw - 1 || iy == sh - 1) ? dpx : src[base + sw + 1];

        const __m128i wx0 = _mm_set1_epi32(0x10000 - tx), wx1 = _mm_set1_epi32(tx);
        const __m128i wy0 = _mm_set1_epi32(0x10000 - ty), wy1 = _mm_set1_epi32(ty);
        __m128i a = _mm_srli_epi32(x128::mullo32(x128::u8x4_to_u32((uint32_t)c11), wx0), 12);
        __m128i b = _mm_srli_epi32(x128::mullo32(x128::u8x4_to_u32((uint32_t)c21), wx1), 12);
        __m128i c = _mm_srli_epi32(x128::mullo32(x128::u8x4_to_u32((uint32_t)c12), wx0), 12);
        __m128i d = _mm_srli_epi32(x128::mullo32(x128::u8x4_to_u32((uint32_t)c22), wx1), 12);
        __m128i s = _mm_add_epi32(x128::mullo32(_mm_add_epi32(a, b), wy0), x128::mullo32(_mm_add_epi32(c, d), wy1));
        return (int)x128::pack_u32x4(_mm_srli_epi32(s, 20));
    }

#if SR2D_LEVEL >= 2
    // one colour channel of the 8-pixel bilinear filter (bit-exact with the scalar formula)
    static SR2D_INLINE __m256i bilerp_ch(__m256i c11, __m256i c21, __m256i c12, __m256i c22, int shift,
                                         __m256i wx0, __m256i wx1, __m256i wy0, __m256i wy1)
    {
        const __m256i ff = _mm256_set1_epi32(0xff);
        __m256i k11 = _mm256_and_si256(_mm256_srlv_epi32(c11, _mm256_set1_epi32(shift)), ff);
        __m256i k21 = _mm256_and_si256(_mm256_srlv_epi32(c21, _mm256_set1_epi32(shift)), ff);
        __m256i k12 = _mm256_and_si256(_mm256_srlv_epi32(c12, _mm256_set1_epi32(shift)), ff);
        __m256i k22 = _mm256_and_si256(_mm256_srlv_epi32(c22, _mm256_set1_epi32(shift)), ff);
        __m256i a = _mm256_add_epi32(_mm256_srli_epi32(_mm256_mullo_epi32(k11, wx0), 12), _mm256_srli_epi32(_mm256_mullo_epi32(k21, wx1), 12));
        __m256i b = _mm256_add_epi32(_mm256_srli_epi32(_mm256_mullo_epi32(k12, wx0), 12), _mm256_srli_epi32(_mm256_mullo_epi32(k22, wx1), 12));
        return _mm256_srli_epi32(_mm256_add_epi32(_mm256_mullo_epi32(a, wy0), _mm256_mullo_epi32(b, wy1)), 20);
    }
#endif

    static void DRAW_ROT_AA(int* src, int* dest, int L, int T_, int R, int B, int sx, int sy, int dx, int dy, int sw, int sh, int dw, int SinA, int CosA)
    {
        int ssx = (int)((unsigned)sx << 16) - (dx - L) * CosA;    // unsigned shift: same bits, no UB for negative sx
        int ssy = (int)((unsigned)sy << 16) - (dx - L) * SinA;
        int addy = T_ * dw;
        T_ -= dy;
        B  -= dy;
#if SR2D_LEVEL >= 2
        const __m256i lane = _mm256_set_epi32(7, 6, 5, 4, 3, 2, 1, 0);
        const __m256i cosl = _mm256_mullo_epi32(lane, _mm256_set1_epi32(CosA));
        const __m256i sinl = _mm256_mullo_epi32(lane, _mm256_set1_epi32(SinA));
        const __m256i cos8 = _mm256_set1_epi32(CosA * 8), sin8 = _mm256_set1_epi32(SinA * 8);
        const __m256i m1 = _mm256_set1_epi32(-1), m2 = _mm256_set1_epi32(-2);
        const __m256i vsw1 = _mm256_set1_epi32(sw - 1), vsh1 = _mm256_set1_epi32(sh - 1);
        const __m256i vsw = _mm256_set1_epi32(sw), vsh = _mm256_set1_epi32(sh);
        const __m256i lo16 = _mm256_set1_epi32(0xffff), one16 = _mm256_set1_epi32(0x10000);
#endif
        for (int y = T_; y < B; ++y, addy += dw)
        {
            int xx = ssx - y * SinA;
            int yy = ssy + y * CosA;
            int* drow = dest + addy;
            int x = L;
#if SR2D_LEVEL >= 2
            __m256i vx = _mm256_add_epi32(_mm256_set1_epi32(xx), cosl);
            __m256i vy = _mm256_add_epi32(_mm256_set1_epi32(yy), sinl);
            for (; x + 8 <= R; x += 8, vx = _mm256_add_epi32(vx, cos8), vy = _mm256_add_epi32(vy, sin8))
            {
                __m256i ix = _mm256_srai_epi32(vx, 16), iy = _mm256_srai_epi32(vy, 16);
                // interior: 0 <= ix <= sw-2 && 0 <= iy <= sh-2  -> all four taps inside src
                __m256i inter = _mm256_and_si256(_mm256_and_si256(_mm256_cmpgt_epi32(ix, m1), _mm256_cmpgt_epi32(vsw1, ix)),
                                                 _mm256_and_si256(_mm256_cmpgt_epi32(iy, m1), _mm256_cmpgt_epi32(vsh1, iy)));
                const int mi = _mm256_movemask_epi8(inter);
                if (mi == -1)
                {
                    __m256i idx = _mm256_add_epi32(_mm256_mullo_epi32(iy, vsw), ix);
                    __m256i c11 = _mm256_i32gather_epi32(src, idx, 4);
                    __m256i c21 = _mm256_i32gather_epi32(src, _mm256_add_epi32(idx, _mm256_set1_epi32(1)), 4);
                    __m256i c12 = _mm256_i32gather_epi32(src, _mm256_add_epi32(idx, vsw), 4);
                    __m256i c22 = _mm256_i32gather_epi32(src, _mm256_add_epi32(idx, _mm256_add_epi32(vsw, _mm256_set1_epi32(1))), 4);
                    __m256i wx1 = _mm256_and_si256(vx, lo16), wx0 = _mm256_sub_epi32(one16, wx1);
                    __m256i wy1 = _mm256_and_si256(vy, lo16), wy0 = _mm256_sub_epi32(one16, wy1);
                    __m256i o = bilerp_ch(c11, c21, c12, c22, 0, wx0, wx1, wy0, wy1);
                    o = _mm256_or_si256(o, _mm256_slli_epi32(bilerp_ch(c11, c21, c12, c22, 8, wx0, wx1, wy0, wy1), 8));
                    o = _mm256_or_si256(o, _mm256_slli_epi32(bilerp_ch(c11, c21, c12, c22, 16, wx0, wx1, wy0, wy1), 16));
                    o = _mm256_or_si256(o, _mm256_slli_epi32(bilerp_ch(c11, c21, c12, c22, 24, wx0, wx1, wy0, wy1), 24));
                    _mm256_storeu_si256((__m256i*)(drow + x), o);
                    continue;
                }
                // anything touching the sprite at all?  -1 <= ix <= sw-1 && -1 <= iy <= sh-1
                __m256i any = _mm256_and_si256(_mm256_and_si256(_mm256_cmpgt_epi32(ix, m2), _mm256_cmpgt_epi32(vsw, ix)),
                                               _mm256_and_si256(_mm256_cmpgt_epi32(iy, m2), _mm256_cmpgt_epi32(vsh, iy)));
                if (_mm256_movemask_epi8(any) == 0) continue;
                SR2D_ALIGN(32) int tx[8]; SR2D_ALIGN(32) int ty[8];
                _mm256_store_si256((__m256i*)tx, vx); _mm256_store_si256((__m256i*)ty, vy);
                for (int i = 0; i < 8; ++i) drow[x + i] = bilerp_px(src, drow, x + i, tx[i], ty[i], sw, sh);
            }
            xx = _mm_cvtsi128_si32(_mm256_castsi256_si128(vx));
            yy = _mm_cvtsi128_si32(_mm256_castsi256_si128(vy));
#endif
            for (; x < R; ++x, xx += CosA, yy += SinA)
                drow[x] = bilerp_px(src, drow, x, xx, yy, sw, sh);
        }
    }

    // dst[(FX ? w-1-x : x)*h + (FY ? h-1-y : y)] = src[y*w + x]   (4x4 SIMD transposes, tiled)
    template<bool FX, bool FY>
    static void rot_generic(const int* src, int* dst, int w, int h)
    {
        const int TILE = 32;
        const int w4 = w & ~3, h4 = h & ~3;
        for (int ty = 0; ty < h4; ty += TILE)
        {
            const int ty1 = (ty + TILE < h4) ? ty + TILE : h4;
            for (int tx = 0; tx < w4; tx += TILE)
            {
                const int tx1 = (tx + TILE < w4) ? tx + TILE : w4;
                for (int y = ty; y < ty1; y += 4)
                {
                    const int* s = src + (size_t)y * w;
                    const int dcol = FY ? (h - 4 - y) : y;
                    for (int x = tx; x < tx1; x += 4)
                    {
                        __m128i r0 = _mm_loadu_si128((const __m128i*)(s + x));
                        __m128i r1 = _mm_loadu_si128((const __m128i*)(s + x + w));
                        __m128i r2 = _mm_loadu_si128((const __m128i*)(s + x + 2 * w));
                        __m128i r3 = _mm_loadu_si128((const __m128i*)(s + x + 3 * w));
                        __m128i t0 = _mm_unpacklo_epi32(r0, r1), t1 = _mm_unpackhi_epi32(r0, r1);
                        __m128i t2 = _mm_unpacklo_epi32(r2, r3), t3 = _mm_unpackhi_epi32(r2, r3);
                        __m128i c0 = _mm_unpacklo_epi64(t0, t2), c1 = _mm_unpackhi_epi64(t0, t2);
                        __m128i c2 = _mm_unpacklo_epi64(t1, t3), c3 = _mm_unpackhi_epi64(t1, t3);
                        if (FY)
                        {
                            c0 = _mm_shuffle_epi32(c0, _MM_SHUFFLE(0, 1, 2, 3)); c1 = _mm_shuffle_epi32(c1, _MM_SHUFFLE(0, 1, 2, 3));
                            c2 = _mm_shuffle_epi32(c2, _MM_SHUFFLE(0, 1, 2, 3)); c3 = _mm_shuffle_epi32(c3, _MM_SHUFFLE(0, 1, 2, 3));
                        }
                        int* d0 = dst + (size_t)(FX ? (w - 1 - x) : x) * h + dcol;
                        const ptrdiff_t step = FX ? -(ptrdiff_t)h : (ptrdiff_t)h;
                        _mm_storeu_si128((__m128i*)(d0), c0);
                        _mm_storeu_si128((__m128i*)(d0 + step), c1);
                        _mm_storeu_si128((__m128i*)(d0 + 2 * step), c2);
                        _mm_storeu_si128((__m128i*)(d0 + 3 * step), c3);
                    }
                }
            }
        }
        // remainders
        for (int y = 0; y < h; ++y)
        {
            const int x0 = (y < h4) ? w4 : 0;
            const int dy_ = FY ? (h - 1 - y) : y;
            const int* s = src + (size_t)y * w;
            for (int x = x0; x < w; ++x)
                dst[(size_t)(FX ? (w - 1 - x) : x) * h + dy_] = s[x];
        }
    }
    static void ROT_CW(int* src, int* dest, int w, int h)         { rot_generic<true,  false>(src, dest, w, h); }
    static void ROT_CCW(int* src, int* dest, int w, int h)        { rot_generic<false, true >(src, dest, w, h); }
    static void FLIP_X_ROT_CCW(int* src, int* dest, int w, int h) { rot_generic<false, false>(src, dest, w, h); }
    static void FLIP_Y_ROT_CCW(int* src, int* dest, int w, int h) { rot_generic<true,  true >(src, dest, w, h); }

    static SR2D_INLINE void reverse_row(const int* s, int* d, int n)
    {   // d[i] = s[n-1-i]
        int i = 0;
        for (; i + N <= n; i += N) V::storeu(d + i, V::reverse32(V::loadu(s + n - N - i)));
        for (; i < n; ++i) d[i] = s[n - 1 - i];
    }
    static void FLIP_X(int* src, int* dest, int w, int h)
    {
        for (int y = 0; y < h; ++y, src += w, dest += w) reverse_row(src, dest, w);
    }
    static void FLIP_Y(int* src, int* dest, int w, int h)
    {
        const int* s = src + (size_t)(h - 1) * w;
        for (int y = 0; y < h; ++y, s -= w, dest += w) copy_px(s, dest, w);
    }
    static void FLIP_XY(int* src, int* dest, int w, int h)
    {
        reverse_row(src, dest, w * h);
    }

    // area-average / linear resize, identical arithmetic to the original but
    // separable (horizontal pass cached per source row) and SIMD.
    static void RESIZE(sr2d_uint* src, sr2d_uint* dest, sr2d_uint ws, sr2d_uint hs, sr2d_uint wd, sr2d_uint hd)
    {
        if (ws == 0 || hs == 0 || wd == 0 || hd == 0) return;
        const uint32_t cx = (ws - 1) / wd + 2;
        const uint32_t cy = (hs - 1) / hd + 2;
        const uint32_t nx = cx * wd, ny = cy * hd;

        const size_t bytes = (size_t)(nx * 2 + ny * 2 + wd + hd) * sizeof(uint32_t)   // tables + counts
                           + (size_t)wd * 4 * sizeof(uint32_t) * 3                   // 2 cached H rows + accumulator
                           + 64;
        uint32_t* mem = (uint32_t*)sr2d_alloc(bytes);
        if (!mem) return;
        uint32_t* ikx = mem;
        uint32_t* kx  = ikx + nx;
        uint32_t* iky = kx + nx;
        uint32_t* ky  = iky + ny;
        uint32_t* cntx = ky + ny;
        uint32_t* cnty = cntx + wd;
        uint32_t* H0  = (uint32_t*)(((uintptr_t)(cnty + hd) + 31) & ~(uintptr_t)31);
        uint32_t* H1  = H0 + (size_t)wd * 4;
        uint32_t* acc = H1 + (size_t)wd * 4;
        memset(mem, 0, (size_t)(nx * 2 + ny * 2 + wd + hd) * sizeof(uint32_t));

        uint32_t cxy;
        // ---- x tables (verbatim algorithm) ----
        if (ws >= wd)
        {
            cxy = ws;
            uint32_t pin = 0, pout = 1, p = 0;
            for (;;)
            {
                uint32_t cc = pout * ws - pin * wd;
                if (cc >= wd) kx[p] = wd;
                else { kx[p] = cc; ikx[p] = pin; p = pout * cx; pout += 1; kx[p] = wd - cc; }
                ikx[p] = pin;
                pin += 1;
                if (pin >= ws) break;
                p += 1;
            }
        }
        else
        {
            cxy = wd;
            for (uint32_t x = 0; x < wd; ++x)
            {
                uint32_t p = x * 2;
                kx[p + 1]  = x * (ws - 1) % (wd - 1);
                ikx[p]     = x * (ws - 1) / (wd - 1);
                kx[p]      = wd - kx[p + 1];
                ikx[p + 1] = (ikx[p] + 1) % ws;
            }
        }
        // ---- y tables ----
        if (hs >= hd)
        {
            cxy *= hs;
            uint32_t pin = 0, pout = 1, p = 0;
            for (;;)
            {
                uint32_t cc = pout * hs - pin * hd;
                if (cc >= hd) ky[p] = hd;
                else { ky[p] = cc; iky[p] = pin; p = pout * cy; pout += 1; ky[p] = hd - cc; }
                iky[p] = pin;
                pin += 1;
                if (pin >= hs) break;
                p += 1;
            }
        }
        else
        {
            cxy *= hd;
            for (uint32_t y = 0; y < hd; ++y)
            {
                uint32_t p = y * 2;
                ky[p + 1]  = y * (hs - 1) % (hd - 1);
                iky[p]     = y * (hs - 1) / (hd - 1);
                ky[p]      = hd - ky[p + 1];
                iky[p + 1] = (iky[p] + 1) % hs;
            }
        }
        // non-zero taps form a prefix of each slot group -> count them once
        for (uint32_t x = 0; x < wd; ++x) { uint32_t c = 0; while (c < cx && kx[x * cx + c] != 0) ++c; cntx[x] = c; }
        for (uint32_t y = 0; y < hd; ++y) { uint32_t c = 0; while (c < cy && ky[y * cy + c] != 0) ++c; cnty[y] = c; }

        const bool small_k = wd < 32768;              // kx fits a signed 16-bit lane -> pmaddwd path
        const UDiv32 dv = UDiv32::make(cxy);
        const T magic = V::set1_32((int)dv.magic);
        const __m128i shv = _mm_cvtsi32_si128(dv.shift);
        const __m128i shifts = _mm_set_epi32(1 << 24, 1 << 16, 1 << 8, 1);

        uint32_t cached[2] = { 0xffffffffu, 0xffffffffu };
        uint32_t* Hbuf[2]  = { H0, H1 };
        int next = 0;

        const uint32_t rowlen = wd * 4;
        for (uint32_t yy = 0; yy < hd; ++yy)
        {
            const uint32_t* tky  = ky  + yy * cy;
            const uint32_t* tiky = iky + yy * cy;
            const uint32_t nt = cnty[yy];
            for (uint32_t t = 0; t < nt; ++t)
            {
                const uint32_t sy = tiky[t];
                const uint32_t* H;
                if (cached[0] == sy) H = Hbuf[0];
                else if (cached[1] == sy) H = Hbuf[1];
                else
                {   // horizontal pass for source row sy
                    uint32_t* Hn = Hbuf[next]; cached[next] = sy; next ^= 1;
                    const sr2d_uint* srow = src + (size_t)sy * ws;
                    if (small_k)
                    {
                        for (uint32_t xx = 0; xx < wd; ++xx)
                        {
                            const uint32_t* tk = kx + xx * cx; const uint32_t* ti = ikx + xx * cx;
                            const uint32_t n = cntx[xx];
                            __m128i a = _mm_setzero_si128();
                            for (uint32_t i = 0; i < n; ++i)
                                a = _mm_add_epi32(a, _mm_madd_epi16(x128::u8x4_to_u32(srow[ti[i]]), _mm_set1_epi32((int)tk[i])));
                            _mm_store_si128((__m128i*)(Hn + xx * 4), a);
                        }
                    }
                    else
                    {
                        for (uint32_t xx = 0; xx < wd; ++xx)
                        {
                            const uint32_t* tk = kx + xx * cx; const uint32_t* ti = ikx + xx * cx;
                            const uint32_t n = cntx[xx];
                            __m128i a = _mm_setzero_si128();
                            for (uint32_t i = 0; i < n; ++i)
                                a = _mm_add_epi32(a, x128::mullo32(x128::u8x4_to_u32(srow[ti[i]]), _mm_set1_epi32((int)tk[i])));
                            _mm_store_si128((__m128i*)(Hn + xx * 4), a);
                        }
                    }
                    H = Hn;
                }
                // vertical accumulate: acc (+)= H * ky
                const T kv = V::set1_32((int)tky[t]);
                uint32_t i = 0;
                if (t == 0)
                {
                    for (; i + N <= rowlen; i += N) V::storeu(acc + i, V::mullo32(V::loadu(H + i), kv));
                    for (; i < rowlen; ++i) acc[i] = H[i] * tky[t];
                }
                else
                {
                    for (; i + N <= rowlen; i += N) V::storeu(acc + i, V::add32(V::loadu(acc + i), V::mullo32(V::loadu(H + i), kv)));
                    for (; i < rowlen; ++i) acc[i] += H[i] * tky[t];
                }
            }
            if (nt == 0) memset(acc, 0, rowlen * sizeof(uint32_t));
            // divide + pack
            {
                uint32_t i = 0;
                for (; i + N <= rowlen; i += N) V::storeu(acc + i, udiv(V::loadu(acc + i), dv, magic, shv));
                for (; i < rowlen; ++i) acc[i] = dv(acc[i]);
            }
            sr2d_uint* drow = dest + (size_t)yy * wd;
            for (uint32_t xx = 0; xx < wd; ++xx)
            {
                __m128i q = _mm_load_si128((const __m128i*)(acc + xx * 4));
                drow[xx] = x128::hsum32(x128::mullo32(q, shifts));     // b + (g<<8) + (r<<16) + (a<<24)
            }
        }
        sr2d_free(mem);
    }

    // =================================================================== bump
    // trunc(num / den) for int32 lanes. Double division is correctly rounded and
    // |num| < 2^53, so truncating the double quotient equals C integer division.
    static SR2D_INLINE T div_trunc(T num, T den) { return V::div_trunc_pd(num, den); }

    template<bool MASKED>
    static void dpbm_point(int* src, int* dst, int* mask, int maskex, int w, int h, int ws, int wd, int wm, int lx, int ly, int lz, int br, int notm)
    {
        if (w <= 0) return;
        const int ulz = lz < 0 ? -lz : lz;
        const int num = ulz * br;
        const int zz  = ulz * ulz;
        SR2D_ALIGN(32) int lane_[8] = { 0, 1, 2, 3, 4, 5, 6, 7 };
        const T lane = V::loadu(lane_);
        const T vnum = V::set1_32(num), vlz = V::set1_32(lz), v128 = V::set1_32(128), v255 = V::set1_32(255), vff = V::set1_32(0xff);
        const T mx = V::set1_32(maskex);
        const T flip = notm ? V::zero() : V::set1_32(-1);
        for (int y = 0; y < h; ++y, src += ws, dst += wd, mask += (MASKED ? wm : 0))
        {
            const int g = ly - y;
            const T vgz = V::set1_32(g * g + zz);
            const T vg  = V::set1_32(g);
            int x = 0;
            for (;;)
            {
                SR2D_ALIGN(32) int ts[8] = { 0 }; SR2D_ALIGN(32) int td[8] = { 0 }; SR2D_ALIGN(32) int tm[8] = { 0 };
                const int *ps, *pm; int* pd; int n;
                if (x + N <= w) { ps = src + x; pd = dst + x; pm = mask + x; n = N; }
                else if (x < w)
                {
                    n = w - x;
                    for (int i = 0; i < n; ++i) { ts[i] = src[x + i]; td[i] = dst[x + i]; if (MASKED) tm[i] = mask[x + i]; }
                    ps = ts; pd = td; pm = tm;
                }
                else break;

                T s  = V::loadu(ps);
                T r  = V::sub32(V::set1_32(lx - x), lane);                    // lx - (x+i)
                T den = V::add32(V::mullo32(r, r), vgz);
                T b  = div_trunc(vnum, den);
                T rb = V::mullo32(r, b), gb = V::mullo32(vg, b), bb = V::mullo32(vlz, b);
                T cb = V::sub32(V::and_(s, vff), v128);
                T cg = V::sub32(V::and_(V::template srli32<8>(s), vff), v128);
                T cr = V::sub32(V::and_(V::template srli32<16>(s), vff), v128);
                T c  = V::add32(V::add32(V::mullo32(bb, cb), V::mullo32(gb, cg)), V::mullo32(rb, cr));
                c = V::template srai32<19>(c);
                T neg = V::template srai32<31>(c);
                T over = V::cmpgt32(c, v255);
                c = V::or_(V::or_(c, V::template slli32<8>(c)), V::template slli32<16>(c));
                c = V::select(over, V::set1_32(0xffffff), c);
                c = V::andnot(neg, c);
                if (MASKED)
                {
                    T dv  = V::loadu(pd);
                    T sel = V::xor_(V::cmpeq32(V::and_(V::loadu(pm), mx), V::zero()), flip);
                    c = V::select(sel, c, dv);
                }
                V::storeu(pd, c);
                if (n < N) { for (int i = 0; i < n; ++i) dst[x + i] = td[i]; break; }
                x += N;
            }
        }
    }
    static void DPBM_POINT(int* src, int* dst, int w, int h, int ws, int wd, int lx, int ly, int lz, int br)
    { dpbm_point<false>(src, dst, 0, 0, w, h, ws, wd, 0, lx, ly, lz, br, 0); }
    static void MASK_DPBM_POINT(int* src, int* dst, int* mask, int maskex, int w, int h, int ws, int wd, int wm, int lx, int ly, int lz, int br, int notm)
    { dpbm_point<true>(src, dst, mask, maskex, w, h, ws, wd, wm, lx, ly, lz, br, notm); }

    static SR2D_INLINE T gather32(const int* table, T idx)
    {
#if SR2D_LEVEL >= 2
        return _mm256_i32gather_epi32(table, idx, 4);
#else
        SR2D_ALIGN(16) int t[4];
        _mm_store_si128((__m128i*)t, idx);
        return _mm_set_epi32(table[t[3]], table[t[2]], table[t[1]], table[t[0]]);
#endif
    }

    // scalar-load variant: for kernels that need many gathers per pixel (bicubic: 16), the
    // hardware gather is slower than 8 plain loads + inserts on every AVX2 CPU so far
    static SR2D_INLINE T gather32_s(const int* table, T idx)
    {
        SR2D_ALIGN(32) int t[8];
        V::storeu(t, idx);
#if SR2D_LEVEL >= 2
        return _mm256_setr_epi32(table[t[0]], table[t[1]], table[t[2]], table[t[3]], table[t[4]], table[t[5]], table[t[6]], table[t[7]]);
#else
        return _mm_setr_epi32(table[t[0]], table[t[1]], table[t[2]], table[t[3]]);
#endif
    }

    template<bool MASKED, bool EX>
    static void ebm(const int* src, int* dst, const int* mask, const int* cmap, int maskex, int w, int h, int ws, int wd, int wm, int wc, int hc, int notm, int xx, int yy, int hd)
    {
        if (w <= 0) return;
        const int dg = (EX && hd != 0) ? 0x1000000 / hd : 0;
        const int dr = (EX && wd != 0) ? 0x1000000 / wd : 0;
        SR2D_ALIGN(32) int lane_[8] = { 0, 1, 2, 3, 4, 5, 6, 7 };
        const T lane = V::loadu(lane_);
        const T vff = V::set1_32(0xff), vwc = V::set1_32(wc), vhc = V::set1_32(hc), vdr = V::set1_32(dr), v128 = V::set1_32(128);
        const T mx = V::set1_32(maskex);
        const T flip = notm ? V::zero() : V::set1_32(-1);
        for (int y = 0; y < h; ++y, src += ws, dst += wd, mask += (MASKED ? wm : 0))
        {
            const int gofs = EX ? ((((y + yy) * dg) >> 16) - 128) : 0;
            const T vgofs = V::set1_32(gofs);
            int x = 0;
            for (;;)
            {
                SR2D_ALIGN(32) int ts[8] = { 0 }; SR2D_ALIGN(32) int td[8] = { 0 }; SR2D_ALIGN(32) int tm[8] = { 0 };
                const int *ps, *pm; int* pd; int n;
                if (x + N <= w) { ps = src + x; pd = dst + x; pm = mask + x; n = N; }
                else if (x < w)
                {
                    n = w - x;
                    for (int i = 0; i < n; ++i) { ts[i] = src[x + i]; td[i] = dst[x + i]; if (MASKED) tm[i] = mask[x + i]; }
                    ps = ts; pd = td; pm = tm;
                }
                else break;

                T s = V::loadu(ps);
                T g = V::and_(V::template srli32<8>(s), vff);
                T r = V::and_(V::template srli32<16>(s), vff);
                if (EX)
                {
                    T xofs = V::template srai32<16>(V::mullo32(V::add32(V::set1_32(x + xx), lane), vdr));
                    r = V::and_(V::sub32(V::add32(r, xofs), v128), vff);
                    g = V::and_(V::add32(g, vgofs), vff);
                }
                T idx = V::add32(V::template srai32<8>(V::mullo32(r, vwc)),
                                 V::mullo32(V::template srai32<8>(V::mullo32(g, vhc)), vwc));
                T c = gather32(cmap, idx);
                if (MASKED)
                {
                    T dv  = V::loadu(pd);
                    T sel = V::xor_(V::cmpeq32(V::and_(V::loadu(pm), mx), V::zero()), flip);
                    c = V::select(sel, c, dv);
                }
                V::storeu(pd, c);
                if (n < N) { for (int i = 0; i < n; ++i) dst[x + i] = td[i]; break; }
                x += N;
            }
        }
    }
    static void EBM_(int* src, int* dst, int* cmap, int w, int h, int ws, int wd, int wc, int hc)
    { ebm<false, false>(src, dst, 0, cmap, 0, w, h, ws, wd, 0, wc, hc, 0, 0, 0, 1); }
    static void EBM_EX(int* src, int* dst, int* cmap, int w, int h, int ws, int wd, int wc, int hc, int xx, int yy, int hd)
    { ebm<false, true>(src, dst, 0, cmap, 0, w, h, ws, wd, 0, wc, hc, 0, xx, yy, hd); }
    static void MASK_EBM(sr2d_byte* src, int* dst, int* mask, int* cmap, int maskex, int w, int h, int ws, int wd, int wm, int wc, int hc, int notm)
    { ebm<true, false>((const int*)src, dst, mask, cmap, maskex, w, h, ws, wd, wm, wc, hc, notm, 0, 0, 1); }
    static void MASK_EBM_EX(sr2d_byte* src, int* dst, int* mask, int* cmap, int maskex, int w, int h, int ws, int wd, int wm, int wc, int hc, int notm, int xx, int yy, int hd)
    { ebm<true, true>((const int*)src, dst, mask, cmap, maskex, w, h, ws, wd, wm, wc, hc, notm, xx, yy, hd); }

    // ============================================================ free warp
    // Draws src (sw x sh) into an arbitrary destination quad (projective or
    // affine), optionally clipped by a polygon (even-odd), with nearest or
    // bilinear sampling and any of the standard blend ops.
    //
    // Coverage rule: a destination pixel is drawn when its centre (x+.5, y+.5)
    // lies inside the quad (and inside the polygon, if given). Source lookups
    // are clamped to the sprite edge.
    struct Homography
    {
        float A, B, C, D, E, F, G, H, I;   // src = ((A X + B Y + C), (D X + E Y + F)) / (G X + H Y + I)
        bool  proj;
        bool  ok;
    };

    static Homography make_homography(const float* q, int sw, int sh)
    {
        Homography r; r.ok = false; r.proj = false;
        const double x0 = q[0], y0 = q[1], x1 = q[2], y1 = q[3], x2 = q[4], y2 = q[5], x3 = q[6], y3 = q[7];
        // forward map unit square -> quad : M = [a b c; d e f; g h 1]
        const double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
        const double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
        double a, b, c, d, e, f, g = 0, h = 0;
        double scale = 0;
        for (int i = 0; i < 8; ++i) { double v = q[i] < 0 ? -q[i] : q[i]; if (v > scale) scale = v; }
        const double eps = 1e-9 * (scale + 1.0);
        if ((dx3 > -eps && dx3 < eps) && (dy3 > -eps && dy3 < eps))
        {
            a = x1 - x0; b = x3 - x0; c = x0;
            d = y1 - y0; e = y3 - y0; f = y0;
        }
        else
        {
            const double den = dx1 * dy2 - dx2 * dy1;
            if (den > -1e-300 && den < 1e-300) return r;
            g = (dx3 * dy2 - dx2 * dy3) / den;
            h = (dx1 * dy3 - dx3 * dy1) / den;
            a = x1 - x0 + g * x1; b = x3 - x0 + h * x3; c = x0;
            d = y1 - y0 + g * y1; e = y3 - y0 + h * y3; f = y0;
            r.proj = true;
        }
        // adjugate (inverse up to scale)
        double A00 = e - f * h, A01 = c * h - b, A02 = b * f - c * e;
        double A10 = f * g - d, A11 = a - c * g, A12 = c * d - a * f;
        double A20 = d * h - e * g, A21 = b * g - a * h, A22 = a * e - b * d;
        if (!r.proj)
        {
            if (A22 > -1e-300 && A22 < 1e-300) return r;
            A00 /= A22; A01 /= A22; A02 /= A22; A10 /= A22; A11 /= A22; A12 /= A22; A20 = A21 = 0; A22 = 1;
        }
        else
        {
            // normalise so the denominator is 1 at the quad centre (keeps floats well scaled)
            const double mx = (x0 + x1 + x2 + x3) * 0.25, my = (y0 + y1 + y2 + y3) * 0.25;
            const double dm = A20 * mx + A21 * my + A22;
            if (dm > -1e-300 && dm < 1e-300) return r;
            A00 /= dm; A01 /= dm; A02 /= dm; A10 /= dm; A11 /= dm; A12 /= dm; A20 /= dm; A21 /= dm; A22 /= dm;
        }
        r.A = (float)(A00 * sw); r.B = (float)(A01 * sw); r.C = (float)(A02 * sw);
        r.D = (float)(A10 * sh); r.E = (float)(A11 * sh); r.F = (float)(A12 * sh);
        r.G = (float)A20; r.H = (float)A21; r.I = (float)A22;
        r.ok = true;
        return r;
    }

    // crossings of the horizontal line yc with a closed polygon; returns count (sorted ascending)
    static int scan_crossings(const float* p, int n, double yc, double* out)
    {
        int cnt = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            const double yi = p[i * 2 + 1], yj = p[j * 2 + 1];
            if ((yi <= yc) != (yj <= yc))
            {
                const double xi = p[i * 2], xj = p[j * 2];
                const double x = xi + (yc - yi) * (xj - xi) / (yj - yi);
                int k = cnt++;
                while (k > 0 && out[k - 1] > x) { out[k] = out[k - 1]; --k; }
                out[k] = x;
            }
        }
        return cnt;
    }

    // a*(256-w) + b*w >> 8 per byte, w = per-pixel weight 0..256 in 32-bit lanes
    static SR2D_INLINE T lerp_px(T a, T b, T w)
    {
        T w16 = V::or_(w, V::template slli32<16>(w));
        T wlo = V::unpacklo32(w16, w16), whi = V::unpackhi32(w16, w16);
        T c256 = V::set1_16(256);
        T lo = V::add16(V::mullo16(V::lo8to16(a), V::sub16(c256, wlo)), V::mullo16(V::lo8to16(b), wlo));
        T hi = V::add16(V::mullo16(V::hi8to16(a), V::sub16(c256, whi)), V::mullo16(V::hi8to16(b), whi));
        return V::packus16(V::template srli16<8>(lo), V::template srli16<8>(hi));
    }

    // ---- bicubic (Catmull-Rom, a = -0.5) -------------------------------------------------
    // Weights for the 4 taps at offsets -1, 0, +1, +2 from the integer sample, as signed
    // fixed point x128 (sum == 128 exactly: w0 is the remainder).
    //   w(-1) = -0.5 t^3 +     t^2 - 0.5 t        (in [-0.0741, 0])
    //   w( 0) =  1.5 t^3 - 2.5 t^2 + 1            (in [0, 1])
    //   w(+1) = -1.5 t^3 + 2   t^2 + 0.5 t        (in [0, 1])
    //   w(+2) =  0.5 t^3 - 0.5 t^2                (in [-0.0741, 0])
    // (A pmaddubsw variant with x64 byte weights was tried: no faster - the cost is in the
    //  gathers - and twice the rounding error, so both kernel sets use 16-bit multiplies.)
    static SR2D_INLINE void cubic_weights(typename V::F t, T& w0, T& w1, T& w2, T& w3)
    {
        typedef typename V::F F;
        const F t2 = V::mulf(t, t), t3 = V::mulf(t2, t), f128 = V::set1f(128.0f);
        F f1 = V::addf(V::subf(V::mulf(V::set1f(1.5f), t3), V::mulf(V::set1f(2.5f), t2)), V::set1f(1.0f));
        F f2 = V::addf(V::subf(V::mulf(V::set1f(2.0f), t2), V::mulf(V::set1f(1.5f), t3)), V::mulf(V::set1f(0.5f), t));
        F f3 = V::mulf(V::set1f(0.5f), V::subf(t3, t2));
        w1 = V::cvt_f2i_round(V::mulf(f1, f128));
        w2 = V::cvt_f2i_round(V::mulf(f2, f128));
        w3 = V::cvt_f2i_round(V::mulf(f3, f128));
        w0 = V::sub32(V::sub32(V::sub32(V::set1_32(128), w1), w2), w3);
    }
    static SR2D_INLINE void cubic_weights_scalar(float t, int w[4])
    {
        const float t2 = t * t, t3 = t2 * t;
        const float f1 = 1.5f * t3 - 2.5f * t2 + 1.0f, f2 = 2.0f * t2 - 1.5f * t3 + 0.5f * t, f3 = 0.5f * (t3 - t2);
        // round-to-nearest-even like cvtps_epi32 so scalar and vector weights agree
        w[1] = (int)_mm_cvtss_si32(_mm_set_ss(f1 * 128.0f));
        w[2] = (int)_mm_cvtss_si32(_mm_set_ss(f2 * 128.0f));
        w[3] = (int)_mm_cvtss_si32(_mm_set_ss(f3 * 128.0f));
        w[0] = 128 - w[1] - w[2] - w[3];
    }
    // Pre-expanded per-pixel weights (one 16-bit weight per byte lane, lo / hi halves as
    // lo8to16 / hi8to16 split them). The exact 4-tap sum lies in [-0.125, 1.125] * 255 * 128 =
    // [-4080, 36720] (w(-1) + w(+2) = -t(1-t)/2 >= -1/8), a span below 2^16, so it is formed in
    // wrapping 16-bit lanes; (sum + 4096 + 64) is then a true unsigned value, >> 7, - 32, and
    // packus clamps the overshoot (negative -> 0, > 255 -> 255).
    struct CubicW
    {
        T l[4], h[4];
        static SR2D_INLINE void expand(T w, T& lo, T& hi)
        {
            T w16 = V::or_(V::and_(w, V::set1_32(0xffff)), V::template slli32<16>(w));
            lo = V::unpacklo32(w16, w16); hi = V::unpackhi32(w16, w16);
        }
        SR2D_INLINE void set(T w0, T w1, T w2, T w3) { expand(w0, l[0], h[0]); expand(w1, l[1], h[1]); expand(w2, l[2], h[2]); expand(w3, l[3], h[3]); }
        SR2D_INLINE T apply(T c0, T c1, T c2, T c3) const
        {
            const T off = V::set1_16(4096 + 64), back = V::set1_16(4096 >> 7);
            T lo = V::add16(V::add16(V::mullo16(V::lo8to16(c0), l[0]), V::mullo16(V::lo8to16(c1), l[1])),
                            V::add16(V::mullo16(V::lo8to16(c2), l[2]), V::mullo16(V::lo8to16(c3), l[3])));
            T hi = V::add16(V::add16(V::mullo16(V::hi8to16(c0), h[0]), V::mullo16(V::hi8to16(c1), h[1])),
                            V::add16(V::mullo16(V::hi8to16(c2), h[2]), V::mullo16(V::hi8to16(c3), h[3])));
            lo = V::sub16(V::template srli16<7>(V::add16(lo, off)), back);
            hi = V::sub16(V::template srli16<7>(V::add16(hi, off)), back);
            return V::packus16(lo, hi);
        }
    };

    template<int MODE>   // 0 nearest, 1 bilinear, 2 bicubic
    static SR2D_INLINE T warp_sample(const int* src, int sw, int sh, typename V::F u, typename V::F v)
    {
        typedef typename V::F F;
        const F zero = V::set1f(0.0f);
        const F maxu = V::set1f((float)(sw - 1)), maxv = V::set1f((float)(sh - 1));
        const T vsw = V::set1_32(sw);
        if (MODE == 0)
        {
            // maxf(NaN, 0) -> 0 : out-of-range / degenerate lanes are clamped safely
            T iu = V::cvtt_f2i(V::minf(V::maxf(u, zero), maxu));
            T iv = V::cvtt_f2i(V::minf(V::maxf(v, zero), maxv));
            return gather32(src, V::add32(V::mullo32(iv, vsw), iu));
        }
        else if (MODE == 2)
        {
            const F half = V::set1f(0.5f);
            F uf = V::minf(V::maxf(V::subf(u, half), zero), maxu);
            F vf = V::minf(V::maxf(V::subf(v, half), zero), maxv);
            T iu1 = V::cvtt_f2i(uf), iv1 = V::cvtt_f2i(vf);
            T wx0, wx1, wx2, wx3, wy0, wy1, wy2, wy3;
            cubic_weights(V::subf(uf, V::cvt_i2f(iu1)), wx0, wx1, wx2, wx3);
            cubic_weights(V::subf(vf, V::cvt_i2f(iv1)), wy0, wy1, wy2, wy3);
            CubicW cx, cy; cx.set(wx0, wx1, wx2, wx3); cy.set(wy0, wy1, wy2, wy3);
            const T one = V::set1_32(1), mu = V::set1_32(sw - 1), mv = V::set1_32(sh - 1);
            T iu0 = V::max32(V::sub32(iu1, one), V::zero()), iu2 = V::min32(V::add32(iu1, one), mu), iu3 = V::min32(V::add32(iu2, one), mu);
            T iv0 = V::max32(V::sub32(iv1, one), V::zero()), iv2 = V::min32(V::add32(iv1, one), mv), iv3 = V::min32(V::add32(iv2, one), mv);
            T rows[4] = { V::mullo32(iv0, vsw), V::mullo32(iv1, vsw), V::mullo32(iv2, vsw), V::mullo32(iv3, vsw) };
            T r[4];
            for (int i = 0; i < 4; ++i)
                r[i] = cx.apply(gather32_s(src, V::add32(rows[i], iu0)), gather32_s(src, V::add32(rows[i], iu1)),
                                gather32_s(src, V::add32(rows[i], iu2)), gather32_s(src, V::add32(rows[i], iu3)));
            return cy.apply(r[0], r[1], r[2], r[3]);
        }
        else
        {
            const F half = V::set1f(0.5f), f256 = V::set1f(256.0f);
            F uf = V::minf(V::maxf(V::subf(u, half), zero), maxu);
            F vf = V::minf(V::maxf(V::subf(v, half), zero), maxv);
            T iu0 = V::cvtt_f2i(uf), iv0 = V::cvtt_f2i(vf);
            T wx = V::cvtt_f2i(V::mulf(V::subf(uf, V::cvt_i2f(iu0)), f256));
            T wy = V::cvtt_f2i(V::mulf(V::subf(vf, V::cvt_i2f(iv0)), f256));
            T iu1 = V::min32(V::add32(iu0, V::set1_32(1)), V::set1_32(sw - 1));
            T iv1 = V::min32(V::add32(iv0, V::set1_32(1)), V::set1_32(sh - 1));
            T r0 = V::mullo32(iv0, vsw), r1 = V::mullo32(iv1, vsw);
            T c00 = gather32_s(src, V::add32(r0, iu0)), c10 = gather32_s(src, V::add32(r0, iu1));
            T c01 = gather32_s(src, V::add32(r1, iu0)), c11 = gather32_s(src, V::add32(r1, iu1));
            return lerp_px(lerp_px(c00, c10, wx), lerp_px(c01, c11, wx), wy);
        }
    }

    template<class Op, int MODE>
    static void warp_impl(const Op& op, const int* src, int sw, int sh, int* dst, int dw,
                          int clipL, int clipT, int clipR, int clipB,
                          const float* quad, const float* poly, int npoly)
    {
        typedef typename V::F F;
        const bool BILINEAR = MODE == 1, BICUBIC = MODE == 2;
        const int NR = MODE == 0 ? 1 : MODE == 1 ? 2 : 4;             // cached rows
        const int CI = MODE == 0 ? 1 : MODE == 1 ? 3 : 8;             // ints per column entry
        const Homography Hm = make_homography(quad, sw, sh);
        if (!Hm.ok) return;

        // vertical extent of the quad (and polygon) -> row range
        double ymin = quad[1], ymax = quad[1];
        for (int i = 1; i < 4; ++i) { if (quad[i * 2 + 1] < ymin) ymin = quad[i * 2 + 1]; if (quad[i * 2 + 1] > ymax) ymax = quad[i * 2 + 1]; }
        if (poly && npoly >= 3)
        {
            double pmin = poly[1], pmax = poly[1];
            for (int i = 1; i < npoly; ++i) { if (poly[i * 2 + 1] < pmin) pmin = poly[i * 2 + 1]; if (poly[i * 2 + 1] > pmax) pmax = poly[i * 2 + 1]; }
            if (pmin > ymin) ymin = pmin;
            if (pmax < ymax) ymax = pmax;
        }
        else poly = 0;
        if (ymin < clipT) ymin = clipT;
        if (ymax > clipB) ymax = clipB;
        int y0 = (int)ceil_d(ymin - 0.5), y1 = (int)ceil_d(ymax - 0.5);
        if (y0 < clipT) y0 = clipT;
        if (y1 > clipB) y1 = clipB;
        if (y0 >= y1 || clipL >= clipR) return;

        double  qx[8];
        double  stackx[64];
        double* px = stackx;
        if (poly && npoly + 2 > 64) { px = (double*)sr2d_alloc(sizeof(double) * (npoly + 2)); if (!px) return; }

        SR2D_ALIGN(32) int lane_[8] = { 0, 1, 2, 3, 4, 5, 6, 7 };
        const F lanef = V::addf(V::cvt_i2f(V::loadu(lane_)), V::set1f(0.5f));
        const F vA = V::set1f(Hm.A), vD = V::set1f(Hm.D), vG = V::set1f(Hm.G);

        // ---- separable fast path: axis-aligned scale (u depends on x only, v on y only)
        // Produces exactly the same pixels as the generic path below.
        const bool axis = !Hm.proj && Hm.B == 0.0f && Hm.D == 0.0f;
        int*  cidx = 0;        // per column: nearest -> iu ; bilinear -> iu0, iu1, wx
        int*  rowbuf = 0;      // cached resampled source rows (1 for nearest, 2 for bilinear)
        int   cached[4] = { -1, -1, -1, -1 };
        // horizontal extent actually touched: quad x-range (and polygon) clamped to the clip
        // rect - with an axis-aligned quad every drawn pixel lies inside it. The generic path
        // below only indexes the tables through (x - clipL) for pixels inside the quad, so
        // shrinking the clip window here changes nothing but the table size.
        if (axis)
        {
            double xmin = quad[0], xmax = quad[0];
            for (int i = 1; i < 4; ++i) { if (quad[i * 2] < xmin) xmin = quad[i * 2]; if (quad[i * 2] > xmax) xmax = quad[i * 2]; }
            int xl = (int)ceil_d(xmin - 0.5) - 1, xr = (int)ceil_d(xmax - 0.5) + 1;
            if (xl > clipL) clipL = xl;
            if (xr < clipR) clipR = xr;
            if (clipL >= clipR) { if (px != stackx) sr2d_free(px); return; }
        }
        const int cw = clipR - clipL;
        if (axis)
        {
            const size_t nb = (size_t)cw * sizeof(int) * CI + (size_t)cw * sizeof(int) * NR + 64;
            cidx = (int*)sr2d_alloc(nb);
            if (cidx)
            {
                rowbuf = cidx + (size_t)cw * CI;
                for (int x = clipL; x < clipR; ++x)
                {
                    const float X = (float)x + 0.5f;
                    float u = Hm.A * X + Hm.C;                       // == vA*X + rowU with B == 0
                    if (MODE == 0)
                    {
                        u = u > 0.0f ? u : 0.0f; u = u < (float)(sw - 1) ? u : (float)(sw - 1);
                        cidx[x - clipL] = (int)u;
                    }
                    else if (BICUBIC)
                    {   // 4 column indices + 4 weights (x256, signed)
                        float uf = u - 0.5f;
                        uf = uf > 0.0f ? uf : 0.0f; uf = uf < (float)(sw - 1) ? uf : (float)(sw - 1);
                        const int i1 = (int)uf;
                        int* t = cidx + (x - clipL) * 8;
                        t[0] = i1 > 0 ? i1 - 1 : 0; t[1] = i1; t[2] = i1 + 1 < sw - 1 ? i1 + 1 : sw - 1; t[3] = i1 + 2 < sw - 1 ? i1 + 2 : sw - 1;
                        cubic_weights_scalar(uf - (float)i1, t + 4);
                    }
                    else
                    {
                        float uf = u - 0.5f;
                        uf = uf > 0.0f ? uf : 0.0f; uf = uf < (float)(sw - 1) ? uf : (float)(sw - 1);
                        const int iu0 = (int)uf;
                        cidx[(x - clipL) * 3 + 0] = iu0;
                        cidx[(x - clipL) * 3 + 1] = iu0 + 1 < sw - 1 ? iu0 + 1 : sw - 1;
                        cidx[(x - clipL) * 3 + 2] = (int)((uf - (float)iu0) * 256.0f);
                    }
                }
            }
        }

        for (int y = y0; y < y1; ++y)
        {
            const double yc = y + 0.5;
            const int nq = scan_crossings(quad, 4, yc, qx);
            if (nq < 2) continue;
            int np = 2;
            if (poly) { np = scan_crossings(poly, npoly, yc, px); if (np < 2) continue; }
            else { px[0] = clipL - 1.0; px[1] = clipR + 1.0; }

            // per-row constants (double -> float once per row)
            const F rowU = V::set1f((float)((double)Hm.B * yc + Hm.C));
            const F rowV = V::set1f((float)((double)Hm.E * yc + Hm.F));
            const F rowW = V::set1f((float)((double)Hm.H * yc + Hm.I));
            int* drow = dst + (ptrdiff_t)y * dw;

            // separable path: resolve the source row(s) for this destination row once
            const int* R0 = 0; const int* R1 = 0; const int* R2 = 0; const int* R3 = 0;
            T vwy = V::zero(); CubicW cwy;
            if (cidx)
            {
                float v = (float)((double)Hm.E * yc + Hm.F);         // == rowV
                int rows[4]; int nrows;
                if (MODE == 0)
                {
                    v = v > 0.0f ? v : 0.0f; v = v < (float)(sh - 1) ? v : (float)(sh - 1);
                    rows[0] = (int)v; nrows = 1;
                }
                else
                {
                    float vf = v - 0.5f;
                    vf = vf > 0.0f ? vf : 0.0f; vf = vf < (float)(sh - 1) ? vf : (float)(sh - 1);
                    const int iv1 = (int)vf;
                    if (BILINEAR)
                    {
                        rows[0] = iv1; rows[1] = iv1 + 1 < sh - 1 ? iv1 + 1 : sh - 1; nrows = 2;
                        vwy = V::set1_32((int)((vf - (float)iv1) * 256.0f));
                    }
                    else
                    {
                        rows[0] = iv1 > 0 ? iv1 - 1 : 0; rows[1] = iv1; rows[2] = iv1 + 1 < sh - 1 ? iv1 + 1 : sh - 1; rows[3] = iv1 + 2 < sh - 1 ? iv1 + 2 : sh - 1; nrows = 4;
                        int w[4]; cubic_weights_scalar(vf - (float)iv1, w);
                        cwy.set(V::set1_32(w[0]), V::set1_32(w[1]), V::set1_32(w[2]), V::set1_32(w[3]));
                    }
                }
                // resolve each needed source row: reuse a cached slot or resample into a free one
                const int* R[4] = { 0, 0, 0, 0 };
                bool inuse[4] = { false, false, false, false };
                for (int r = 0; r < nrows; ++r)
                    for (int sl = 0; sl < NR; ++sl) if (cached[sl] == rows[r]) { R[r] = rowbuf + (ptrdiff_t)sl * cw; inuse[sl] = true; break; }
                for (int r = 0; r < nrows; ++r)
                {
                    if (R[r]) continue;
                    int slot = 0; while (inuse[slot]) ++slot;        // nrows <= NR, so a free slot exists
                    inuse[slot] = true;
                    int* hb = rowbuf + (ptrdiff_t)slot * cw;
                    const int* srow = src + (ptrdiff_t)rows[r] * sw;
                    if (MODE == 0)
                        for (int i = 0; i < cw; ++i) hb[i] = srow[cidx[i]];
                    else if (BILINEAR)
                    {
                        int i = 0;
                        for (; i + N <= cw; i += N)
                        {
                            SR2D_ALIGN(32) int c0[8]; SR2D_ALIGN(32) int c1[8]; SR2D_ALIGN(32) int wx[8];
                            for (int l = 0; l < N; ++l) { const int* t = cidx + (i + l) * 3; c0[l] = srow[t[0]]; c1[l] = srow[t[1]]; wx[l] = t[2]; }
                            V::storeu(hb + i, lerp_px(V::loadu(c0), V::loadu(c1), V::loadu(wx)));
                        }
                        if (i < cw)
                        {
                            SR2D_ALIGN(32) int c0[8] = { 0 }; SR2D_ALIGN(32) int c1[8] = { 0 }; SR2D_ALIGN(32) int wx[8] = { 0 }; SR2D_ALIGN(32) int o[8];
                            for (int l = 0; i + l < cw; ++l) { const int* t = cidx + (i + l) * 3; c0[l] = srow[t[0]]; c1[l] = srow[t[1]]; wx[l] = t[2]; }
                            V::storeu(o, lerp_px(V::loadu(c0), V::loadu(c1), V::loadu(wx)));
                            for (int l = 0; i + l < cw; ++l) hb[i + l] = o[l];
                        }
                    }
                    else
                    {
                        int i = 0;
                        for (; i < cw; i += N)
                        {
                            const int n = cw - i < N ? cw - i : N;
                            SR2D_ALIGN(32) int c[4][8] = { { 0 }, { 0 }, { 0 }, { 0 } }; SR2D_ALIGN(32) int w[4][8] = { { 0 }, { 0 }, { 0 }, { 0 } };
                            for (int l = 0; l < n; ++l) { const int* t = cidx + (i + l) * 8; for (int k = 0; k < 4; ++k) { c[k][l] = srow[t[k]]; w[k][l] = t[4 + k]; } }
                            CubicW cwx; cwx.set(V::loadu(w[0]), V::loadu(w[1]), V::loadu(w[2]), V::loadu(w[3]));
                            T o = cwx.apply(V::loadu(c[0]), V::loadu(c[1]), V::loadu(c[2]), V::loadu(c[3]));
                            if (n == N) V::storeu(hb + i, o);
                            else { SR2D_ALIGN(32) int t8[8]; V::storeu(t8, o); for (int l = 0; l < n; ++l) hb[i + l] = t8[l]; }
                        }
                    }
                    cached[slot] = rows[r];
                    R[r] = hb;
                }
                R0 = R[0]; R1 = R[1]; R2 = R[2]; R3 = R[3];
            }

            for (int a = 0; a + 1 < nq; a += 2)
            {
                for (int b = 0; b + 1 < np; b += 2)
                {
                    double xa = qx[a] > px[b] ? qx[a] : px[b];
                    double xb = qx[a + 1] < px[b + 1] ? qx[a + 1] : px[b + 1];
                    if (xa < clipL - 1.0) xa = clipL - 1.0;
                    if (xb > clipR + 1.0) xb = clipR + 1.0;
                    int xs = (int)ceil_d(xa - 0.5), xe = (int)ceil_d(xb - 0.5);
                    if (xs < clipL) xs = clipL;
                    if (xe > clipR) xe = clipR;
                    if (R0)
                    {   // separable: stream from the cached row(s)
                        for (int x = xs; x < xe; x += N)
                        {
                            const int n = (xe - x < N) ? (xe - x) : N;
                            const int o = x - clipL;
                            if (n == N)
                            {
                                T sv = BICUBIC  ? cwy.apply(V::loadu(R0 + o), V::loadu(R1 + o), V::loadu(R2 + o), V::loadu(R3 + o))
                                     : BILINEAR ? lerp_px(V::loadu(R0 + o), V::loadu(R1 + o), vwy) : V::loadu(R0 + o);
                                V::storeu(drow + x, op(sv, V::loadu(drow + x)));
                            }
                            else
                            {
                                SR2D_ALIGN(32) int td[8] = { 0 }; SR2D_ALIGN(32) int t0[8] = { 0 }; SR2D_ALIGN(32) int t1[8] = { 0 }; SR2D_ALIGN(32) int t2[8] = { 0 }; SR2D_ALIGN(32) int t3[8] = { 0 };
                                for (int i = 0; i < n; ++i) { td[i] = drow[x + i]; t0[i] = R0[o + i]; if (MODE >= 1) t1[i] = R1[o + i]; if (BICUBIC) { t2[i] = R2[o + i]; t3[i] = R3[o + i]; } }
                                T sv = BICUBIC  ? cwy.apply(V::loadu(t0), V::loadu(t1), V::loadu(t2), V::loadu(t3))
                                     : BILINEAR ? lerp_px(V::loadu(t0), V::loadu(t1), vwy) : V::loadu(t0);
                                V::storeu(td, op(sv, V::loadu(td)));
                                for (int i = 0; i < n; ++i) drow[x + i] = td[i];
                            }
                        }
                        continue;
                    }
                    for (int x = xs; x < xe; x += N)
                    {
                        const int n = (xe - x < N) ? (xe - x) : N;
                        F X = V::addf(V::set1f((float)x), lanef);
                        F u = V::addf(V::mulf(vA, X), rowU);
                        F v = V::addf(V::mulf(vD, X), rowV);
                        if (Hm.proj)
                        {
                            F w = V::addf(V::mulf(vG, X), rowW);
                            u = V::divf(u, w);
                            v = V::divf(v, w);
                        }
                        T s = warp_sample<MODE>(src, sw, sh, u, v);
                        if (n == N)
                        {
                            V::storeu(drow + x, op(s, V::loadu(drow + x)));
                        }
                        else
                        {
                            SR2D_ALIGN(32) int td[8] = { 0 };
                            for (int i = 0; i < n; ++i) td[i] = drow[x + i];
                            V::storeu(td, op(s, V::loadu(td)));
                            for (int i = 0; i < n; ++i) drow[x + i] = td[i];
                        }
                    }
                }
            }
        }
        if (px != stackx) sr2d_free(px);
        if (cidx) sr2d_free(cidx);
    }

    static SR2D_INLINE double ceil_d(double v)
    {
        // Clamp first so the int conversion is always defined, even for absurd
        // (or NaN) quad/polygon coordinates coming from the caller. Anything
        // beyond +-1e9 px is far outside any clip rect and behaves as "empty".
        if (v > 1.0e9) v = 1.0e9;
        if (!(v > -1.0e9)) v = -1.0e9;          // also catches NaN
        int i = (int)v;
        return (double)(v > (double)i ? i + 1 : i);
    }

    // ---- area prefilter (SR2D_WARP_AREA): box-average src by (kx, ky) into dst (sw2 x sh2).
    // weighted = alpha-weighted colour average (for ops that read alpha as coverage), else plain.
    // Edge blocks average over the pixels they actually cover.
    // mode: 0 plain average, 1 alpha-weighted colour (straight result), 2 premultiplied result (c*a/255 averaged)
    static void warp_shrink(const int* src, int sw, int sh, int kx, int ky, int* out, int sw2, int sh2, uint32_t* acc, int mode, int stride = 0)
    {
        if (stride <= 0) stride = sw;                  // row pitch of src (a sub-image of a wider picture may be shrunk)
        const bool weighted = mode != 0;
        SR2D_ALIGN(32) const int m3a[8] = { 0, 0, 0, -1, 0, 0, 0, -1 };
        const T m3 = V::loadu(m3a);
        const int px_per_vec = N / 4;
        for (int by = 0; by < sh2; ++by)
        {
            const int y0 = by * ky, y1 = y0 + ky < sh ? y0 + ky : sh;
            // vertical accumulation of rows y0..y1 into acc (4 u32 per source column)
            for (int y = y0; y < y1; ++y)
            {
                const int* row = src + (ptrdiff_t)y * stride;
                int x = 0;
                if (y == y0)
                {
                    for (; x + px_per_vec <= sw; x += px_per_vec)
                    {
                        T v = V::load8x(row + x);
                        if (weighted) v = V::select(m3, v, V::mullo32(v, V::bcast_a32(v)));
                        V::storeu(acc + x * 4, v);
                    }
                }
                else
                {
                    for (; x + px_per_vec <= sw; x += px_per_vec)
                    {
                        T v = V::load8x(row + x);
                        if (weighted) v = V::select(m3, v, V::mullo32(v, V::bcast_a32(v)));
                        V::storeu(acc + x * 4, V::add32(V::loadu(acc + x * 4), v));
                    }
                }
                for (; x < sw; ++x)
                {
                    const uint32_t p = (uint32_t)row[x], a = p >> 24;
                    uint32_t c[4] = { p & 255u, (p >> 8) & 255u, (p >> 16) & 255u, a };
                    if (weighted) { c[0] *= a; c[1] *= a; c[2] *= a; }
                    uint32_t* q = acc + x * 4;
                    if (y == y0) { q[0] = c[0]; q[1] = c[1]; q[2] = c[2]; q[3] = c[3]; }
                    else         { q[0] += c[0]; q[1] += c[1]; q[2] += c[2]; q[3] += c[3]; }
                }
            }
            const int ny = y1 - y0;
            int* o = out + (ptrdiff_t)by * sw2;
            // horizontal reduction: sum kx accumulator columns, divide with floats (no integer divides)
            const __m128 half = _mm_set1_ps(0.5f), one = _mm_set1_ps(1.0f);
            const __m128 invn_full = _mm_set1_ps(1.0f / (float)(kx * ny));
            const int full = sw / kx;                      // blocks with all kx columns
            for (int bx = 0; bx < sw2; ++bx)
            {
                const int x0 = bx * kx, x1 = bx < full ? x0 + kx : sw;
                __m128i s = _mm_loadu_si128((const __m128i*)(acc + x0 * 4));
                for (int x = x0 + 1; x < x1; ++x) s = _mm_add_epi32(s, _mm_loadu_si128((const __m128i*)(acc + x * 4)));
                const __m128 fs = _mm_cvtepi32_ps(s);
                __m128 r;
                if (!weighted)
                    r = _mm_mul_ps(fs, bx < full ? invn_full : _mm_set1_ps(1.0f / (float)((x1 - x0) * ny)));
                else if (mode == 2)
                {   // premultiplied: colour sums are c*a -> / (n*255), alpha / n
                    const float invn = bx < full ? 1.0f / (float)(kx * ny) : 1.0f / (float)((x1 - x0) * ny);
                    r = _mm_mul_ps(fs, _mm_setr_ps(invn / 255.0f, invn / 255.0f, invn / 255.0f, invn));
                }
                else
                {   // colour /= alpha sum, alpha /= n ; alpha sum 0 -> colour 0 (fs is 0 there anyway)
                    const __m128 asum = _mm_max_ps(_mm_shuffle_ps(fs, fs, 0xFF), one);
                    const __m128 n = bx < full ? _mm_set1_ps((float)(kx * ny)) : _mm_set1_ps((float)((x1 - x0) * ny));
                    const __m128 den = _mm_shuffle_ps(_mm_unpacklo_ps(asum, asum), _mm_unpacklo_ps(asum, n), 0x84);  // [asum asum asum n]
                    r = _mm_div_ps(fs, den);
                }
                const __m128i ri = _mm_cvttps_epi32(_mm_add_ps(r, half));
                o[bx] = _mm_cvtsi128_si32(_mm_packus_epi16(_mm_packs_epi32(ri, ri), ri));
            }
        }
    }

    // straight -> premultiplied copy (rgb = (rgb * a + 127) / 255 exactly, alpha kept)
    static void warp_premul_copy(const int* src, int* dst, int n)
    {
        const T amask = V::set1_32((int)0xff000000);
        const T lane_a = V::set1_64(0x0000FFFFFFFFFFFFLL);   // colour lanes of [b g r a] 16-bit groups
        int i = 0;
        for (; i + N <= n; i += N)
        {
            T p = V::loadu(src + i);
            T a = V::template srli32<24>(p);
            a = V::or_(a, V::template slli32<16>(a));
            T alo = V::unpacklo32(a, a), ahi = V::unpackhi32(a, a);
            // x = c*a + 128 ; c*a/255 rounded = (x + (x >> 8)) >> 8   (exact for c, a <= 255)
            T xlo = V::add16(V::mullo16(V::lo8to16(p), alo), V::set1_16(128));
            T xhi = V::add16(V::mullo16(V::hi8to16(p), ahi), V::set1_16(128));
            xlo = V::template srli16<8>(V::add16(xlo, V::template srli16<8>(xlo)));
            xhi = V::template srli16<8>(V::add16(xhi, V::template srli16<8>(xhi)));
            T c = V::packus16(V::and_(xlo, lane_a), V::and_(xhi, lane_a));
            V::storeu(dst + i, V::or_(c, V::and_(p, amask)));
        }
        for (; i < n; ++i)
        {
            const uint32_t v = (uint32_t)src[i], a = v >> 24; uint32_t r = a << 24;
            for (int sh = 0; sh < 24; sh += 8) { uint32_t x = ((v >> sh) & 0xff) * a + 128; r |= ((x + (x >> 8)) >> 8) << sh; }
            dst[i] = (int)r;
        }
    }

    // source pixels per destination pixel along each source axis (lengths of the u and v gradients)
    static SR2D_INLINE void warp_footprint(const Homography& Hm, const float* quad, float& fx, float& fy)
    {
        if (!Hm.proj)
        {
            fx = _mm_cvtss_f32(_mm_sqrt_ss(_mm_set_ss(Hm.A * Hm.A + Hm.B * Hm.B)));
            fy = _mm_cvtss_f32(_mm_sqrt_ss(_mm_set_ss(Hm.D * Hm.D + Hm.E * Hm.E)));
            return;
        }
        // projective: the footprint varies; use the smallest one over the quad corners + centre
        // (conservative: never blurrier than plain sampling would be, still helps the far part)
        fx = fy = 1e30f;
        float cx = 0, cy = 0;
        for (int i = 0; i < 4; ++i) { cx += quad[i * 2] * 0.25f; cy += quad[i * 2 + 1] * 0.25f; }
        for (int i = 0; i < 5; ++i)
        {
            // pull the corners slightly toward the centre so the derivative is taken inside the quad
            const float X = i < 4 ? quad[i * 2] * 0.9f + cx * 0.1f : cx, Y = i < 4 ? quad[i * 2 + 1] * 0.9f + cy * 0.1f : cy;
            const float w = Hm.G * X + Hm.H * Y + Hm.I;
            if (!(w > 1e-12f)) continue;
            const float u = (Hm.A * X + Hm.B * Y + Hm.C) / w, v = (Hm.D * X + Hm.E * Y + Hm.F) / w;
            const float dux = (Hm.A - u * Hm.G) / w, duy = (Hm.B - u * Hm.H) / w;
            const float dvx = (Hm.D - v * Hm.G) / w, dvy = (Hm.E - v * Hm.H) / w;
            const float lx = _mm_cvtss_f32(_mm_sqrt_ss(_mm_set_ss(dux * dux + duy * duy)));
            const float ly = _mm_cvtss_f32(_mm_sqrt_ss(_mm_set_ss(dvx * dvx + dvy * dvy)));
            if (lx < fx) fx = lx;
            if (ly < fy) fy = ly;
        }
        if (fx > 1e29f) fx = 1.0f;
        if (fy > 1e29f) fy = 1.0f;
    }

    template<class Op>
    static SR2D_INLINE void warp_mode(const Op& op, const int* src, int sw, int sh, int* dst, int dw,
                                      int cl, int ct, int cr, int cb, const float* quad, const float* poly, int npoly, int flags)
    {
        if (flags & SR2D_WARP_BICUBIC)       warp_impl<Op, 2>(op, src, sw, sh, dst, dw, cl, ct, cr, cb, quad, poly, npoly);
        else if (flags & SR2D_WARP_BILINEAR) warp_impl<Op, 1>(op, src, sw, sh, dst, dw, cl, ct, cr, cb, quad, poly, npoly);
        else                                 warp_impl<Op, 0>(op, src, sw, sh, dst, dw, cl, ct, cr, cb, quad, poly, npoly);
    }

    // dispatch on the (possibly replaced) op: AlphaTest / AlphaBlend sample a premultiplied
    // image whenever source pixels get mixed (filtered sampling or area prefilter)
    template<class Op>
    static SR2D_INLINE void warp_mode_p(const Op& op, int opcode, bool premul, const int* src, int sw, int sh, int* dst, int dw,
                                        int cl, int ct, int cr, int cb, const float* quad, const float* poly, int npoly, int flags)
    {
        if (!premul)        warp_mode(op, src, sw, sh, dst, dw, cl, ct, cr, cb, quad, poly, npoly, flags);
        else if (opcode == 2) warp_mode(OpAlphaTPremul(),   src, sw, sh, dst, dw, cl, ct, cr, cb, quad, poly, npoly, flags);
        else                  warp_mode(OpAlphaOverKeepA(), src, sw, sh, dst, dw, cl, ct, cr, cb, quad, poly, npoly, flags);
    }

    // src_premul: the source is already premultiplied (DRAW_FX work images) - alpha ops then
    // always take the source-over path and the area average is a plain one.
    template<class Op>
    static SR2D_INLINE void warp_op(const Op& op, const int* src, int sw, int sh, int* dst, int dw,
                                    int cl, int ct, int cr, int cb, const float* quad, const float* poly, int npoly, int flags, int opcode,
                                    bool src_premul = false)
    {
        const bool alphaop = opcode == 2 || opcode == 3;
        const bool filtered = (flags & (SR2D_WARP_BILINEAR | SR2D_WARP_BICUBIC)) != 0;
        if (flags & SR2D_WARP_AREA)
        {
            const Homography Hm = make_homography(quad, sw, sh);
            if (!Hm.ok) return;
            float fx, fy; warp_footprint(Hm, quad, fx, fy);
            // shrink factor = footprint rounded: the shrunk image is then sampled at ~1:1
            // (step in [1 - 1/2k, 1 + 1/2k)), which is the steadiest bilinear can be
            int kx = (int)(fx + 0.5f), ky = (int)(fy + 0.5f);
            if (kx < 1) kx = 1;
            if (ky < 1) ky = 1;
            if (kx > sw) kx = sw;
            if (ky > sh) ky = sh;
            if (kx > 1 || ky > 1)
            {
                const int sw2 = (sw + kx - 1) / kx, sh2 = (sh + ky - 1) / ky;
                const size_t need = (size_t)sw2 * sh2 * sizeof(int) + (size_t)sw * 4 * sizeof(uint32_t) + 128;
                size_t got = 0;
                uint8_t* mem = (uint8_t*)sr2d_scratch_acquire(need, &got);
                if (!mem) return;
                int* smallImg = (int*)mem;
                uint32_t* acc = (uint32_t*)(mem + ((size_t)sw2 * sh2 * sizeof(int) + 31) / 32 * 32);
                warp_shrink(src, sw, sh, kx, ky, smallImg, sw2, sh2, acc, (alphaop && !src_premul) ? 2 : 0);   // alpha ops: premultiplied average
                // the quad is unchanged: make_homography scales by the (new) source size
                warp_mode_p(op, opcode, alphaop, smallImg, sw2, sh2, dst, dw, cl, ct, cr, cb, quad, poly, npoly, flags);
                sr2d_scratch_release(mem, got);
                return;
            }
        }
        if (alphaop && src_premul) { warp_mode_p(op, opcode, true, src, sw, sh, dst, dw, cl, ct, cr, cb, quad, poly, npoly, flags); return; }
        if (alphaop && filtered)
        {   // premultiplied copy of the source (~0.1 ms per 512x512), then AlphaOver-style compositing
            size_t got = 0;
            int* pm = (int*)sr2d_scratch_acquire((size_t)sw * sh * sizeof(int) + 64, &got);
            if (!pm) return;
            warp_premul_copy(src, pm, sw * sh);
            warp_mode_p(op, opcode, true, pm, sw, sh, dst, dw, cl, ct, cr, cb, quad, poly, npoly, flags);
            sr2d_scratch_release(pm, got);
            return;
        }
        warp_mode(op, src, sw, sh, dst, dw, cl, ct, cr, cb, quad, poly, npoly, flags);
    }

    static void DRAW_WARP(int* src, int sw, int sh, int* dst, int dw, int clipL, int clipT, int clipR, int clipB,
                          const float* quad, const float* poly, int npoly, int op, int flags, int k)
    {
        if (sw <= 0 || sh <= 0 || !quad) return;
        if (is_blend_mode(op))
        {   // filtered sampling mixes source pixels: sample a premultiplied copy (no halo) and tell the functor
            const bool filtered = (flags & (SR2D_WARP_BILINEAR | SR2D_WARP_BICUBIC | SR2D_WARP_AREA)) != 0;
            const bool sp = (op & SR2D_OP_SRC_PREMUL) != 0;
            if (!filtered || sp) { with_blend_mode(op, [&](const auto& f) { warp_op(f, src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, 1, sp); }); return; }
            size_t got = 0;
            int* pm = (int*)sr2d_scratch_acquire((size_t)sw * sh * sizeof(int) + 64, &got);
            if (!pm) return;
            warp_premul_copy(src, pm, sw * sh);
            with_blend_mode(op | SR2D_OP_SRC_PREMUL, [&](const auto& f) { warp_op(f, pm, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, 1, true); });
            sr2d_scratch_release(pm, got);
            return;
        }
        switch (op & 0xff)
        {
        case 2:  warp_op(OpAlphaT(),  src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        case 3:  warp_op(OpAlphaB(),  src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        case 4:  warp_op(OpAdd2D(),   src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        case 5:  warp_op(OpAdd(),     src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        case 6:  warp_op(OpMod(),     src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        case 7:  warp_op(OpMod2X(),   src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        case 8:  warp_op(OpMax(),     src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        case 9:  warp_op(OpMin(),     src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        case 10: warp_op(OpBlend(k),  src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        case 11: warp_op(OpAlphaOver(), src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        default: warp_op(OpPaint(),   src, sw, sh, dst, dw, clipL, clipT, clipR, clipB, quad, poly, npoly, flags, op); break;
        }
    }

    // ================================================================ line 2
    // Scalar by nature (one pixel per step); the win over DRAW_DOTLINE is
    // correctness, not SIMD: in-kernel clipping, symmetric endpoints, dash pattern
    // measured along the line, and blend ops.
    // d = lerp(d, s, wt/256) on all four bytes, wt in 0..256 (256 -> exactly s)
    static SR2D_INLINE uint32_t lerp8s(uint32_t d, uint32_t s, int wt)
    {
        const uint32_t iw = (uint32_t)(256 - wt), w = (uint32_t)wt;
        const uint32_t rb = (((d & 0x00ff00ffu) * iw + (s & 0x00ff00ffu) * w) >> 8) & 0x00ff00ffu;
        const uint32_t ag = (((d >> 8) & 0x00ff00ffu) * iw + ((s >> 8) & 0x00ff00ffu) * w) & 0xff00ff00u;
        return rb | ag;
    }

    static SR2D_INLINE int line_px(int op, int k, int s, int d)
    {
        switch (op & 0xff)
        {
        case 2: return d ^ s;
        case 8:
        {   // source-over with a straight-alpha colour: rgb -> col, alpha -> 255, weighted by col.a
            const int a = (unsigned)s >> 24; if (a == 0) return d;
            return (int)lerp8s((uint32_t)d, (uint32_t)s | 0xff000000u, a + (a >> 7));
        }
        case 3:
        {   // AlphaBlend with the colour's alpha
            const int a = (unsigned)s >> 24; if (a == 0) return d; if (a == 255) return s;
            const int ia = 256 - a - (a >> 7);   // 255 -> 0
            int r = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                const int sc = (s >> sh) & 0xff, dc = (d >> sh) & 0xff;
                r |= (((sc * (a + (a >> 7)) + dc * ia) >> 8) & 0xff) << sh;
            }
            return r;
        }
        case 4:
        {   // Blend by k (0..256)
            if (k <= 0) return d;
            if (k >= 256) return s;
            int r = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                const int sc = (s >> sh) & 0xff, dc = (d >> sh) & 0xff;
                r |= (((sc * k + dc * (256 - k)) >> 8) & 0xff) << sh;
            }
            return r;
        }
        case 5:
        {   // saturating add per byte
            int r = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                int v = ((s >> sh) & 0xff) + ((d >> sh) & 0xff); if (v > 255) v = 255;
                r |= v << sh;
            }
            return r;
        }
        case 6:
        {
            int r = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                const int sc = (s >> sh) & 0xff, dc = (d >> sh) & 0xff;
                r |= (sc > dc ? sc : dc) << sh;
            }
            return r;
        }
        case 7:
        {
            int r = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                const int sc = (s >> sh) & 0xff, dc = (d >> sh) & 0xff;
                r |= (sc < dc ? sc : dc) << sh;
            }
            return r;
        }
        default:
            if (is_blend_mode(op))
            {   // one pixel through the vector functor; Dissolve gets a fresh random per pixel
                static thread_local uint32_t ctr = 0x2545F491u;
                ctr = ctr * 1664525u + 1013904223u;
                int r = d; with_blend_mode(op, [&](auto f) { f.rng = V::set1_32((int)ctr); r = op_px(f, s, d); }); return r;
            }
            return s;
        }
    }

    // Inner walk of DRAW_LINE2. The clipped start/end pixels are inside the clip rect and
    // the minor coordinate is a monotone 16.16 ramp between two in-rect values, so only
    // the minor axis can leave the rect by rounding at the very ends -> a single clamp
    // per pixel. Template on the op so the solid Set/XOR paths compile to a bare loop.
    //   dst + maj*majStep + min*minStep addresses a pixel (majStep = 1 or dw).
    template<int OP>
    static SR2D_INLINE void line_put(int* d, int col, int k)
    {
        // OP 9 = a blend mode: the full op word travels in k (bits 0..15 mode + flags, bits 16.. opacity)
        if (OP == 1) *d = col; else if (OP == 2) *d ^= col; else if (OP == 9) *d = line_px(k, 0, col, *d); else *d = line_px(OP, k, col, *d);
    }
    template<int OP>
    static void line_walk(int* dst, ptrdiff_t majStep, ptrdiff_t minStep,
                          int maj0, int64_t minFx, int64_t dmin, int n, int minLo, int minHi,
                          int col, int k, int skip,
                          int mode, int stepGap, int cnt0, double P0, double dpat, double dotlen, double period)
    {
        int* base = dst + (ptrdiff_t)maj0 * majStep;
        if (mode == 0)
        {   // solid
            for (int i = 0; i <= n; ++i, minFx += dmin)
            {
                int m = (int)((minFx + 32768) >> 16);
                if (m < minLo) m = minLo; else if (m > minHi) m = minHi;
                if (i != skip) line_put<OP>(base + (ptrdiff_t)i * majStep + (ptrdiff_t)m * minStep, col, k);
            }
            return;
        }
        if (mode == 1)
        {   // "dot step": one pixel every (stepGap + 1) major steps, counter anchored by the caller
            int cnt = cnt0;
            for (int i = 0; i <= n; ++i, minFx += dmin)
            {
                int m = (int)((minFx + 32768) >> 16);
                if (m < minLo) m = minLo; else if (m > minHi) m = minHi;
                const bool on = cnt == 0; if (++cnt > stepGap) cnt = 0;
                if (on && i != skip) line_put<OP>(base + (ptrdiff_t)i * majStep + (ptrdiff_t)m * minStep, col, k);
            }
            return;
        }
        // mode 2: dash pattern measured along the line. Pattern coordinate of step i is
        // P(i) = P0 + i*dpat; dash j covers [j*period, j*period + dotlen). Instead of
        // sampling the pattern per pixel (which loses dashes shorter than one step on
        // diagonals) we enumerate the dashes and light the steps they cover, at least
        // one step per dash -> dots never disappear, never double.
        // Work with an increasing pattern coordinate Q(i) = Q0 + i*ad. For dpat < 0 the walk
        // runs against the pattern: with Q = -P, "P mod period < dotlen" becomes
        // "Q in (j*period - dotlen, j*period]", i.e. the dash windows are mirrored and
        // half-open on the other side.
        const bool mir = dpat < 0;
        const double ad = mir ? -dpat : dpat;
        const double Q0 = mir ? -P0 : P0;
        const double off = mir ? period - dotlen : 0.0;   // dash j covers [j*period+off, j*period+off+dotlen) resp. (.., ..] when mirrored
        auto flr = [](double v) -> long long { long long t = (long long)v; return (double)t > v ? t - 1 : t; };
        auto cil = [](double v) -> long long { long long t = (long long)v; return (double)t < v ? t + 1 : t; };
        const double Qmax = Q0 + n * ad;
        long long j0 = flr((Q0 - off - dotlen) / period), j1 = flr((Qmax - off) / period) + 1;
        int prevEnd = -1;
        for (long long j = j0; j <= j1; ++j)
        {
            const double ds = j * period + off, de = ds + dotlen;
            const double ta = (ds - Q0) / ad, tb = (de - Q0) / ad;
            long long ia = mir ? flr(ta) + 1 : cil(ta);          // first step inside the dash
            long long ib = mir ? flr(tb) + 1 : cil(tb);          // one past the last step inside
            if (ib <= ia) ib = ia + 1;              // dash shorter than a step: nearest step
            if (ia <= prevEnd) ia = prevEnd + 1;    // never light a step twice
            if (ia < 0) ia = 0;
            if (ib > n + 1) ib = n + 1;
            if (ib <= ia) continue;
            prevEnd = (int)ib - 1;
            int64_t mf = minFx + dmin * ia;
            for (long long i = ia; i < ib; ++i, mf += dmin)
            {
                int m = (int)((mf + 32768) >> 16);
                if (m < minLo) m = minLo; else if (m > minHi) m = minHi;
                if ((int)i != skip) line_put<OP>(base + (ptrdiff_t)i * majStep + (ptrdiff_t)m * minStep, col, k);
            }
        }
    }

    static void DRAW_LINE2(int* dst, int dw, int clipL, int clipT, int clipR, int clipB,
                           float x0, float y0, float x1, float y1, int col, int op, int k,
                           float dotlen, float gaplen, float phase)
    {
        if (clipL >= clipR || clipT >= clipB) return;
        // reject NaN / absurd input outright
        if (!(x0 > -1e9f && x0 < 1e9f && y0 > -1e9f && y0 < 1e9f && x1 > -1e9f && x1 < 1e9f && y1 > -1e9f && y1 < 1e9f)) return;
        if (!(dotlen > -1e6f && dotlen < 1e6f && gaplen > -1e6f && gaplen < 1e6f && phase > -1e9f && phase < 1e9f)) return;

        // Liang-Barsky against the pixel-centre rectangle [L, R-1] x [T, B-1]
        const double fx0 = x0, fy0 = y0, fdx = (double)x1 - x0, fdy = (double)y1 - y0;
        const double L = clipL, R = clipR - 1, T = clipT, B = clipB - 1;
        double t0 = 0.0, t1 = 1.0;
        {
            const double p[4] = { -fdx, fdx, -fdy, fdy };
            const double q[4] = { fx0 - L, R - fx0, fy0 - T, B - fy0 };
            for (int i = 0; i < 4; ++i)
            {
                if (p[i] == 0.0) { if (q[i] < -0.5) return; }        // parallel and outside (half-pixel slack)
                else
                {
                    const double t = q[i] / p[i];
                    if (p[i] < 0) { if (t > t1) return; if (t > t0) t0 = t; }
                    else          { if (t < t0) return; if (t < t1) t1 = t; }
                }
            }
        }
        // dash modes: dotlen > 0 && gaplen > 0 -> pattern measured along the line (pixels);
        //             dotlen < 0             -> "dot step": 1 pixel on, (-dotlen) major steps off
        const int  stepGap = dotlen < 0.0f ? (int)(-dotlen + 0.5f) : 0;
        const int  mode = stepGap > 0 ? 1 : (dotlen > 0.0f && gaplen > 0.0f) ? 2 : 0;
        const double period = mode == 2 ? (double)dotlen + (double)gaplen : 1.0;

        double sx = fx0 + fdx * t0, sy = fy0 + fdy * t0;           // clipped start
        double ex = fx0 + fdx * t1, ey = fy0 + fdy * t1;           // clipped end
        const double adx = fdx < 0 ? -fdx : fdx, ady = fdy < 0 ? -fdy : fdy;
        // Always walk in +major direction so A->B and B->A light exactly the same
        // pixels (accumulated rounding is then identical). The dash pattern is still
        // measured from the caller's first point: 'rev' flips its direction.
        const bool rev = (adx >= ady) ? (fdx < 0) : (fdy < 0);
        const double ox = rev ? (double)x1 : fx0, oy = rev ? (double)y1 : fy0;   // walk origin (unclipped)
        if (rev) { double t; t = sx; sx = ex; ex = t; t = sy; sy = ey; ey = t; }
        const double wdx = rev ? -fdx : fdx, wdy = rev ? -fdy : fdy;              // walk direction (major > 0)
        // op bit 8: do not light the pixel of the caller's END point (only meaningful when
        // that end is not clipped away) so consecutive polyline segments never touch a
        // shared vertex twice (matters for XOR / alpha ops).
        const bool skipEnd = (op & 0x100) != 0 && t1 >= 1.0;

        // major-axis DDA in 16.16 fixed point, starting/ending on the pixel that contains each endpoint
        auto rnd = [](double v) -> int { return (int)(v + (v >= 0 ? 0.5 : -0.5)); };
        auto fx  = [](double v) -> int64_t { return (int64_t)(v * 65536.0 + (v >= 0 ? 0.5 : -0.5)); };
        const bool horiz = adx >= ady;
        const int ms = horiz ? rnd(sx) : rnd(sy), me = horiz ? rnd(ex) : rnd(ey);
        const int n = me - ms; if (n < 0) return;
        const double amaj = horiz ? adx : ady;
        const double omaj = horiz ? ox : oy;
        const double slope = amaj > 0 ? (horiz ? wdy / wdx : wdx / wdy) : 0.0;
        const int64_t minFx = fx((horiz ? oy : ox) + (ms - omaj) * slope);
        const int64_t dmin  = fx(slope);
        const int skip = skipEnd ? (rev ? 0 : n) : -1;
        const ptrdiff_t majStep = horiz ? 1 : dw, minStep = horiz ? dw : 1;
        const int minLo = horiz ? clipT : clipL, minHi = horiz ? clipB - 1 : clipR - 1;

        // pattern set-up
        double P0 = 0.0, dpat = 0.0; int cnt0 = 0;
        if (mode == 2)
        {
            const double len = _mm_cvtsd_f64(_mm_sqrt_sd(_mm_setzero_pd(), _mm_set_sd(fdx * fdx + fdy * fdy)));   // no CRT
            const double dper = amaj > 0 ? len / amaj : 0.0;                    // line pixels per major step
            const double d0 = (ms - omaj) * dper;                               // distance of step 0 from the walk origin
            P0 = (rev ? len - d0 : d0) - (double)phase;                         // measured from the caller's start
            dpat = rev ? -dper : dper;
        }
        else if (mode == 1)
        {   // counter anchored at the caller's lower-major endpoint (like the original, but unclipped)
            const int startMaj = rnd(omaj);
            int off = (ms - startMaj) % (stepGap + 1); if (off < 0) off += stepGap + 1;
            cnt0 = off == 0 ? 0 : stepGap + 1 - off;                            // steps until the next "on"
            cnt0 = (stepGap + 1 - cnt0) % (stepGap + 1);                        // -> counter value at step 0
        }
        if (is_blend_mode(op)) { line_walk<9>(dst, majStep, minStep, ms, minFx, dmin, n, minLo, minHi, col, op & ~0x100, skip, mode, stepGap, cnt0, P0, dpat, dotlen, period); return; }
        switch (op & 0xff)
        {
        case 2:  line_walk<2>(dst, majStep, minStep, ms, minFx, dmin, n, minLo, minHi, col, k, skip, mode, stepGap, cnt0, P0, dpat, dotlen, period); break;
        case 3:  line_walk<3>(dst, majStep, minStep, ms, minFx, dmin, n, minLo, minHi, col, k, skip, mode, stepGap, cnt0, P0, dpat, dotlen, period); break;
        case 4:  line_walk<4>(dst, majStep, minStep, ms, minFx, dmin, n, minLo, minHi, col, k, skip, mode, stepGap, cnt0, P0, dpat, dotlen, period); break;
        case 5:  line_walk<5>(dst, majStep, minStep, ms, minFx, dmin, n, minLo, minHi, col, k, skip, mode, stepGap, cnt0, P0, dpat, dotlen, period); break;
        case 6:  line_walk<6>(dst, majStep, minStep, ms, minFx, dmin, n, minLo, minHi, col, k, skip, mode, stepGap, cnt0, P0, dpat, dotlen, period); break;
        case 7:  line_walk<7>(dst, majStep, minStep, ms, minFx, dmin, n, minLo, minHi, col, k, skip, mode, stepGap, cnt0, P0, dpat, dotlen, period); break;
        case 8:  line_walk<8>(dst, majStep, minStep, ms, minFx, dmin, n, minLo, minHi, col, k, skip, mode, stepGap, cnt0, P0, dpat, dotlen, period); break;
        default: line_walk<1>(dst, majStep, minStep, ms, minFx, dmin, n, minLo, minHi, col, k, skip, mode, stepGap, cnt0, P0, dpat, dotlen, period); break;
        }
    }

    // ================================================================ polygon
    // DRAW_POLY: scanline rasteriser for one or more closed contours.
    //   * winding: non-zero (default) or even-odd (flag 2). Overlapping contours of
    //     one call are therefore a true UNION: a thick polyline built from segment
    //     quads + join discs blends every pixel exactly once (no seams with alpha ops).
    //   * AA (flag 1): S sub-scanlines per pixel row (4, or 16 with flag 4) with
    //     exact horizontal coverage at span ends. Coverage is accumulated as a
    //     difference array, so the cost per row is O(active edges * S + touched width)
    //     and interior runs are composited with full-width SIMD, only edge pixels
    //     take the weighted path.
    //   * op: same set as DRAW_LINE2 (1 set, 2 xor, 3 alphablend, 4 blend k, 5 add,
    //     6 max, 7 min, 8 alpha-over with straight colour). Coverage scales the
    //     effective weight; XOR ignores coverage (applied where coverage >= 50 %).
    //   * clipping happens per span, vertices may be anywhere (|v| < 1e9).
    struct PEdge { float x0, y0, y1, slope; int dir; };

    // vector lerp of all four bytes with a constant weight (16-bit lanes wv, iv = 256 - wv)
    static SR2D_INLINE T lerp16(T d, T s, T wv, T iv)
    {
        T lo = V::add16(V::mullo16(V::lo8to16(d), iv), V::mullo16(V::lo8to16(s), wv));
        T hi = V::add16(V::mullo16(V::hi8to16(d), iv), V::mullo16(V::hi8to16(s), wv));
        return V::packus16(V::template srli16<8>(lo), V::template srli16<8>(hi));
    }

    // constant-source compositor: Set / AlphaBlend / Blend(k) / AlphaOver
    template<bool KeepAlpha>
    struct CmpConst
    {
        uint32_t s; int a8; T sv, amask;
        CmpConst(uint32_t col, int scale) : s(col), a8(scale), sv(V::set1_32((int)col)), amask(V::set1_32((int)0xff000000u)) {}
        SR2D_INLINE void run(int* d, int n, int w) const
        {
            const int wt = (a8 * w) >> 8; if (wt <= 0) return;
            int x = 0;
            if (wt >= 256 && !KeepAlpha)
            {
                for (; x + N <= n; x += N) V::storeu(d + x, sv);
                for (; x < n; ++x) d[x] = (int)s;
                return;
            }
            const T wv = V::set1_16((short)wt), iv = V::set1_16((short)(256 - wt));
            for (; x + N <= n; x += N)
            {
                T dv = V::loadu(d + x), r = lerp16(dv, sv, wv, iv);
                if (KeepAlpha) r = V::select(amask, dv, r);
                V::storeu(d + x, r);
            }
            for (; x < n; ++x)
            {
                const uint32_t dv = (uint32_t)d[x]; uint32_t r = lerp8s(dv, s, wt);
                if (KeepAlpha) r = (r & 0x00ffffffu) | (dv & 0xff000000u);
                d[x] = (int)r;
            }
        }
    };
    struct CmpXor
    {
        uint32_t s; T sv;
        explicit CmpXor(uint32_t col) : s(col), sv(V::set1_32((int)col)) {}
        SR2D_INLINE void run(int* d, int n, int w) const
        {
            if (w < 128) return;
            int x = 0;
            for (; x + N <= n; x += N) V::storeu(d + x, V::xor_(V::loadu(d + x), sv));
            for (; x < n; ++x) d[x] ^= (int)s;
        }
    };
    // Add / Max / Min: source = op(col, d), then weighted by coverage
    static SR2D_INLINE uint32_t sc_add(uint32_t s, uint32_t d)
    { uint32_t r = 0; for (int sh = 0; sh < 32; sh += 8) { uint32_t v = ((s >> sh) & 0xff) + ((d >> sh) & 0xff); if (v > 255) v = 255; r |= v << sh; } return r; }
    static SR2D_INLINE uint32_t sc_max(uint32_t s, uint32_t d)
    { uint32_t r = 0; for (int sh = 0; sh < 32; sh += 8) { uint32_t a = (s >> sh) & 0xff, b = (d >> sh) & 0xff; r |= (a > b ? a : b) << sh; } return r; }
    static SR2D_INLINE uint32_t sc_min(uint32_t s, uint32_t d)
    { uint32_t r = 0; for (int sh = 0; sh < 32; sh += 8) { uint32_t a = (s >> sh) & 0xff, b = (d >> sh) & 0xff; r |= (a < b ? a : b) << sh; } return r; }
    template<class Op, uint32_t (*SC)(uint32_t, uint32_t)>
    struct CmpFn
    {
        uint32_t s; T sv; Op op;
        explicit CmpFn(uint32_t col) : s(col), sv(V::set1_32((int)col)) {}
        SR2D_INLINE void run(int* d, int n, int w) const
        {
            if (w <= 0) return;
            int x = 0;
            const T wv = V::set1_16((short)w), iv = V::set1_16((short)(256 - w));
            for (; x + N <= n; x += N)
            {
                T dv = V::loadu(d + x), r = op(sv, dv);
                if (w < 256) r = lerp16(dv, r, wv, iv);
                V::storeu(d + x, r);
            }
            for (; x < n; ++x)
            {
                const uint32_t dv = (uint32_t)d[x]; uint32_t r = SC(s, dv);
                if (w < 256) r = lerp8s(dv, r, w);
                d[x] = (int)r;
            }
        }
    };

    // constant colour through a blend-mode functor; coverage w scales the colour's alpha
    // (alpha * w is what the functor sees, so AA edges and opacity compose in one lerp)
    template<class Op>
    struct CmpMode
    {
        uint32_t s; Op op;
        CmpMode(uint32_t col, const Op& o) : s(col), op(o) {}
        SR2D_INLINE void run(int* d, int n, int w) const
        {
            if (w <= 0) return;
            uint32_t a = s >> 24; a = (a * (uint32_t)w) >> 8; if (a == 0) return;
            const T sv = V::set1_32((int)((s & 0x00ffffffu) | (a << 24)));
            int x = 0;
            for (; x + N <= n; x += N) V::storeu(d + x, op(sv, V::loadu(d + x)));
            if (x < n)
            {
                SR2D_ALIGN(32) int td[8];
                const int m = n - x;
                for (int i = 0; i < m; ++i) td[i] = d[x + i];
                V::storeu(td, op(sv, V::loadu(td)));
                for (int i = 0; i < m; ++i) d[x + i] = td[i];
            }
        }
    };
    template<class Op> static SR2D_INLINE CmpMode<Op> make_cmp_mode(uint32_t c, const Op& o) { return CmpMode<Op>(c, o); }

    template<class Cmp>
    static void poly_raster(int* dst, int dw, int clipL, int clipT, int clipR, int clipB,
                            const float* xy, const int* counts, int ncont, int flags, const Cmp& cmp)
    {
        // ---- build edge list
        int total = 0;
        for (int c = 0; c < ncont; ++c) { if (counts[c] < 0) return; total += counts[c]; }
        if (total < 3) return;
        for (int i = 0; i < total * 2; ++i) { const float v = xy[i]; if (!(v > -1e9f && v < 1e9f)) return; }

        const bool aa = (flags & 1) != 0, evenodd = (flags & 2) != 0;
        const int  S  = !aa ? 1 : (flags & 4) ? 16 : 4;
        const int  sh = !aa ? 0 : (flags & 4) ? 4 : 2;
        // per edge: PEdge + active/cx/cd slots + (AA) 2*S event slots per row
        const size_t perEdge = sizeof(PEdge) + 3 * sizeof(int) + (aa ? 2 * S * sizeof(int) : 0);
        SR2D_ALIGN(16) unsigned char stackbuf[64 * (sizeof(PEdge) + 35 * sizeof(int))];
        const size_t need = (size_t)total * perEdge + 16;
        unsigned char* mem = need <= sizeof(stackbuf) ? stackbuf : (unsigned char*)sr2d_alloc(need);
        if (!mem) return;
        PEdge* E = (PEdge*)mem;
        int* active = (int*)(E + total);       // indices of active edges
        int* cx     = active + total;          // crossing x (24.8) for the current sub-scanline
        int* cd     = cx + total;              // crossing direction
        int* ev     = cd + total;              // AA: touched accumulator indices of the current row
        int ne = 0; float ymin = 1e30f, ymax = -1e30f;
        {
            const float* p = xy;
            for (int c = 0; c < ncont; ++c)
            {
                const int n = counts[c];
                if (n >= 3)
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        float ax = p[j * 2], ay = p[j * 2 + 1], bx = p[i * 2], by = p[i * 2 + 1];
                        if (ay == by) continue;
                        int dir = 1;
                        if (ay > by) { float t; t = ax; ax = bx; bx = t; t = ay; ay = by; by = t; dir = -1; }
                        PEdge& e = E[ne++];
                        e.x0 = ax; e.y0 = ay; e.y1 = by; e.slope = (bx - ax) / (by - ay); e.dir = dir;
                        if (ay < ymin) ymin = ay;
                        if (by > ymax) ymax = by;
                    }
                p += n * 2;
            }
        }
        if (ne < 2) { if (mem != stackbuf) sr2d_free(mem); return; }
        // sort by y0 (shell sort: no CRT, fine for the sizes involved)
        for (int gap = ne / 2; gap > 0; gap /= 2)
            for (int i = gap; i < ne; ++i)
            {
                PEdge t = E[i]; int j = i;
                while (j >= gap && E[j - gap].y0 > t.y0) { E[j] = E[j - gap]; j -= gap; }
                E[j] = t;
            }

        const float sstep = 1.0f / (float)S, soff = 0.5f * sstep;

        // row range
        int yA = (int)ymin; if ((float)yA > ymin) --yA;           // floor
        int yB = (int)ymax; if ((float)yB < ymax) ++yB;           // ceil
        if (yA < clipT) yA = clipT;
        if (yB > clipB - 1) yB = clipB - 1;
        if (yA > yB) { if (mem != stackbuf) sr2d_free(mem); return; }

        // coverage accumulator (difference array) for the AA path
        const int accw = clipR - clipL + 2;
        SR2D_ALIGN(32) int accstack[4096];
        int* acc = 0;
        if (aa)
        {
            acc = accw <= 4096 ? accstack : (int*)sr2d_alloc((size_t)accw * sizeof(int));
            if (!acc) { if (mem != stackbuf) sr2d_free(mem); return; }
            for (int i = 0; i < accw; ++i) acc[i] = 0;
        }
        const float fL = (float)clipL, fR = (float)clipR;
        int nact = 0, nextE = 0;

        for (int y = yA; y <= yB; ++y)
        {
            int* drow = dst + (ptrdiff_t)y * dw;
            int nev = 0;
            for (int s = 0; s < S; ++s)
            {
                const float ys = (float)y + soff + sstep * (float)s;
                // retire finished edges, admit new ones (E sorted by y0, ys increases)
                for (int i = 0; i < nact; )
                {
                    if (E[active[i]].y1 <= ys) active[i] = active[--nact]; else ++i;
                }
                while (nextE < ne && E[nextE].y0 <= ys) { if (E[nextE].y1 > ys) active[nact++] = nextE; ++nextE; }
                if (nact < 2) continue;
                // crossings, sorted by x (insertion sort, nact is small)
                int nc = 0;
                for (int i = 0; i < nact; ++i)
                {
                    const PEdge& e = E[active[i]];
                    float xf = e.x0 + (ys - e.y0) * e.slope;
                    if (xf < fL) xf = fL; else if (xf > fR) xf = fR;
                    const int xi = (int)(xf * 256.0f);
                    int j = nc++;
                    while (j > 0 && cx[j - 1] > xi) { cx[j] = cx[j - 1]; cd[j] = cd[j - 1]; --j; }
                    cx[j] = xi; cd[j] = e.dir;
                }
                // spans
                int wind = 0, xa = 0;
                for (int i = 0; i < nc; ++i)
                {
                    const bool inA = evenodd ? (wind & 1) != 0 : wind != 0;
                    wind += cd[i];
                    const bool inB = evenodd ? (wind & 1) != 0 : wind != 0;
                    if (!inA && inB) xa = cx[i];
                    else if (inA && !inB)
                    {
                        const int xb = cx[i];
                        if (xb <= xa) continue;
                        if (!aa)
                        {   // pixel centre rule: x in [ceil(xa - .5), ceil(xb - .5))
                            const int px0 = (xa + 127) >> 8, px1 = (xb + 127) >> 8;
                            if (px1 > px0) cmp.run(drow + px0, px1 - px0, 256);
                        }
                        else
                        {
                            const int ia = (xa >> 8) - clipL, fa = xa & 255, ib = (xb >> 8) - clipL, fb = xb & 255;
                            if (ia == ib) { acc[ia] += xb - xa; acc[ia + 1] -= xb - xa; }
                            else { acc[ia] += 256 - fa; acc[ia + 1] += fa; acc[ib] += fb - 256; acc[ib + 1] -= fb; }
                            ev[nev++] = ia; ev[nev++] = ib;      // (ia+1, ib+1 are implied neighbours)
                        }
                    }
                }
            }
            if (aa && nev)
            {
                // sort touched indices (small insertion sort), then walk the difference
                // array only where it is non-zero: coverage is constant between events,
                // so interior runs go to the compositor as one full-width SIMD call.
                for (int i = 1; i < nev; ++i)
                {
                    const int t = ev[i]; int j = i;
                    while (j > 0 && ev[j - 1] > t) { ev[j] = ev[j - 1]; --j; }
                    ev[j] = t;
                }
                int cov = 0, i = 0, x = ev[0];
                while (x < accw - 1)
                {
                    // absorb all events at x and x+1 (each span end touches x and x+1)
                    cov += acc[x]; acc[x] = 0;
                    while (i < nev && ev[i] <= x) ++i;
                    int xn = (i < nev) ? ev[i] : accw - 1;         // next event index
                    if (xn > x + 1)
                    {   // x+1 may still hold the "+fa" / "-fb" half of an event at x
                        int w = cov >> sh; if (w > 256) w = 256;
                        if (w > 0) { int n = 1, px = clipL + x; if (px + n > clipR) n = clipR - px; if (n > 0) cmp.run(drow + px, n, w); }
                        x = x + 1; cov += acc[x]; acc[x] = 0;
                        w = cov >> sh; if (w > 256) w = 256;
                        const int len = xn - x;
                        if (w > 0) { int n = len, px = clipL + x; if (px + n > clipR) n = clipR - px; if (n > 0) cmp.run(drow + px, n, w); }
                        x = xn;
                    }
                    else
                    {
                        int w = cov >> sh; if (w > 256) w = 256;
                        if (w > 0) { int px = clipL + x; if (px < clipR) cmp.run(drow + px, 1, w); }
                        ++x;
                    }
                }
                acc[accw - 1] = 0;
            }
            if (nextE >= ne && nact == 0) break;
        }
        if (acc && acc != accstack) sr2d_free(acc);
        if (mem != stackbuf) sr2d_free(mem);
    }

    static void DRAW_POLY(int* dst, int dw, int clipL, int clipT, int clipR, int clipB,
                          const float* xy, const int* counts, int ncont, int col, int op, int k, int flags)
    {
        if (clipL >= clipR || clipT >= clipB || !xy || !counts || ncont <= 0) return;
        const uint32_t c = (uint32_t)col; const int a = (int)(c >> 24);
        if (is_blend_mode(op)) { if (a) with_blend_mode(op & ~SR2D_OP_SRC_PREMUL, [&](const auto& f) { poly_raster(dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, flags, make_cmp_mode(c, f)); }); return; }
        switch (op & 0xff)
        {
        case 2: poly_raster(dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, flags, CmpXor(c)); break;
        case 3: if (a) poly_raster(dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, flags, CmpConst<true>(c, a + (a >> 7))); break;
        case 4: poly_raster(dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, flags, CmpConst<false>(c, k < 0 ? 0 : k > 256 ? 256 : k)); break;
        case 5: poly_raster(dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, flags, CmpFn<OpAdd, &K::sc_add>(c)); break;
        case 6: poly_raster(dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, flags, CmpFn<OpMax, &K::sc_max>(c)); break;
        case 7: poly_raster(dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, flags, CmpFn<OpMin, &K::sc_min>(c)); break;
        case 8: if (a) poly_raster(dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, flags, CmpConst<false>(c | 0xff000000u, a + (a >> 7))); break;
        default: poly_raster(dst, dw, clipL, clipT, clipR, clipB, xy, counts, ncont, flags, CmpConst<false>(c, 256)); break;
        }
    }


    // ================================================================ blur
    // DRAW_BLUR: Gaussian-like blur of a whole sprite, composited onto dst with soft
    // (not clipped) edges.
    //
    //   * Work image = sprite + margin m = passes*r on every side (r = box radius), so the
    //     blur runs OUT of the sprite: outside it the source is transparent black, and since
    //     everything is done PREMULTIPLIED the fade-out is a clean alpha ramp (no dark fringe).
    //   * Gaussian approximated by three box blurs of radius r (sigma ~ r): each is a
    //     running sum, so the cost is O(pixels) and INDEPENDENT of the radius.
    //     Box blurs along x and y commute, so all horizontal passes run first, on one row
    //     at a time while it sits in L1, then the vertical passes stream the plane.
    //   * Channels live in u16 x128 fixed point between passes (7 fractional bits, every
    //     value < 2^15). Work layout is the natural [b g r a] per pixel, so the horizontal
    //     running sum is a 4-lane add per pixel (two rows at once on AVX2) and the vertical
    //     one is a plain contiguous vector add.
    //   * Composite: only the intersection of work image and clip rect is touched, with the
    //     requested op. AlphaTest/AlphaBlend get the un-premultiplied (straight) result, all
    //     other ops the alpha-weighted one (see step 3). `strength` (0..256) scales it.
    //
    //   src, sw, sh          : sprite; flags bit0 = already premultiplied, bit1 = treat as opaque
    //   dst, dw, clip*       : destination and lock rect
    //   dx, dy               : where the sprite's top-left goes (blur extends passes*r beyond)
    //   r                    : box radius (0 = no blur, just the composite), max 512
    //   op, k                : SR2D.Op (1 Paint 2 AlphaTest 3 AlphaBlend 4 Add2D 5 Add 6 Mul 7 Mul2X 8 Max 9 Min 10 Blend 11 AlphaOver), k for Blend
    //                          12 = copy the STRAIGHT-alpha result (Paint copies the premultiplied one) - for in-place blurs
    //   src may alias dst: the whole source is read into the work image before anything is written.
    //   flags                : 1 premultiplied source, 2 opaque source, 4 two passes instead of three (cheaper, boxier)
    //   strength             : 0..256 opacity multiplier
    //   Returns the number of destination pixels written (0 = fully clipped).

    // ---- horizontal box pass on N/4 rows at once (SSE2: 1 row, AVX2: 2 rows); in != out
    static void blur_h_rows(const uint16_t* in, uint16_t* out, int W, int L, int r, float inv)
    {
        typedef typename V::F F;
        const F finv = V::set1f(inv);
        const uint16_t* i1 = in + L;  uint16_t* o1 = out + L;     // second row (AVX2 only; SSE2 ignores it)
        T acc = V::zero();
        int lim = r < W - 1 ? r : W - 1;
        for (int x = 0; x <= lim; ++x) acc = V::add32(acc, V::load16x2(in + x * 4, i1 + x * 4));
        for (int x = 0; x < W; ++x)
        {
            V::store16x2(out + x * 4, o1 + x * 4, V::cvt_f2i_round(V::mulf(V::cvt_i2f(acc), finv)));
            const int xa = x + r + 1, xs = x - r;
            if (xa < W)  acc = V::add32(acc, V::load16x2(in + xa * 4, i1 + xa * 4));
            if (xs >= 0) acc = V::sub32(acc, V::load16x2(in + xs * 4, i1 + xs * 4));
        }
    }

    // ---- vertical box pass over the columns [c0, c1) of the plane: contiguous running sums,
    //      N lanes at a time. Called per column strip so that all passes of a strip stay in L2.
    static void blur_v(const uint16_t* in, uint16_t* out, int L, int c0, int c1, int H, int r, float inv, uint32_t* acc)
    {
        typedef typename V::F F;
        const F finv = V::set1f(inv);
        int c = c0;
        for (; c + N <= c1; c += N) V::storeu(acc + c, V::zero());
        for (; c < c1; ++c) acc[c] = 0;
        const int lim = r < H - 1 ? r : H - 1;
        for (int y = 0; y <= lim; ++y)
        {
            const uint16_t* row = in + (size_t)y * L; c = c0;
            for (; c + N <= c1; c += N) V::storeu(acc + c, V::add32(V::loadu(acc + c), V::load16x(row + c)));
            for (; c < c1; ++c) acc[c] += row[c];
        }
        for (int y = 0; y < H; ++y)
        {
            uint16_t* o = out + (size_t)y * L;
            const int ya = y + r + 1, ys = y - r;
            const uint16_t* ra = ya < H ? in + (size_t)ya * L : 0;
            const uint16_t* rs = ys >= 0 ? in + (size_t)ys * L : 0;
            c = c0;
            if (ra && rs)
                for (; c + N <= c1; c += N)
                {
                    T a = V::loadu(acc + c);
                    V::store16x(o + c, V::cvt_f2i_round(V::mulf(V::cvt_i2f(a), finv)));
                    a = V::sub32(V::add32(a, V::load16x(ra + c)), V::load16x(rs + c));
                    V::storeu(acc + c, a);
                }
            else
                for (; c + N <= c1; c += N)
                {
                    T a = V::loadu(acc + c);
                    V::store16x(o + c, V::cvt_f2i_round(V::mulf(V::cvt_i2f(a), finv)));
                    if (ra) a = V::add32(a, V::load16x(ra + c));
                    if (rs) a = V::sub32(a, V::load16x(rs + c));
                    V::storeu(acc + c, a);
                }
            for (; c < c1; ++c)
            {
                uint32_t a = acc[c];
                o[c] = (uint16_t)(int)((float)a * inv + 0.5f);
                if (ra) a += ra[c];
                if (rs) a -= rs[c];
                acc[c] = a;
            }
        }
    }

    // ---- load one work row: n source pixels -> premultiplied u16 x128 (vectorised, N px per step)
    static void blur_load_row(const int* s, uint16_t* q, int n, bool premul, bool opaque)
    {
        const T amask = V::set1_64((long long)0xFFFF000000000000ULL);   // alpha word of every pixel
        const T k128_255 = V::set1_16((short)32896);                    // 128/255 in 0.16
        const T opq = V::set1_16((short)255);
        int x = 0;
        for (; x + N <= n; x += N)
        {
            T p = V::loadu(s + x);
            T lo = V::lo8to16(p), hi = V::hi8to16(p);                    // [b g r a] u16 per pixel
            V::order_unpacked(lo, hi);
            T* two[2] = { &lo, &hi };
            for (int h = 0; h < 2; ++h)
            {
                T v = *two[h];
                T res;
                if (opaque) v = V::select(amask, opq, v);               // alpha := 255, then as straight
                if (premul) res = V::template slli16<7>(v);
                else
                {   // c*a*128/255 for the colour words, a*128 for the alpha word
                    T a = V::bcast_a16(v);
                    T ca = V::mulhi_u16(V::mullo16(v, a), k128_255);
                    res = V::select(amask, V::template slli16<7>(v), ca);
                }
                V::storeu(q + x * 4 + h * (N * 2), res);
            }
        }
        for (; x < n; ++x)
        {
            const uint32_t v = (uint32_t)s[x];
            uint32_t a = opaque ? 255u : v >> 24, b = v & 255u, g = (v >> 8) & 255u, rr = (v >> 16) & 255u;
            uint16_t* o = q + x * 4;
            if (!premul) { b = (b * a * 32896u) >> 16; g = (g * a * 32896u) >> 16; rr = (rr * a * 32896u) >> 16; }
            else { b <<= 7; g <<= 7; rr <<= 7; }
            o[0] = (uint16_t)b; o[1] = (uint16_t)g; o[2] = (uint16_t)rr; o[3] = (uint16_t)(a << 7);
        }
    }

    // ---- composite: N/4 work pixels (u16 x128, premultiplied) -> N/4 packed 8-bit pixels at out
    //      straight = un-premultiply (c*255/a); strength 0..256 (scale = strength < 256)
    static SR2D_INLINE void blur_out_px(const uint16_t* q, int* out, T vstrength, T m3, bool scale, bool straight)
    {
        typedef typename V::F F;
        T v = V::load16x(q);                                            // N u32 lanes = N/4 pixels [b g r a]
        if (scale) v = V::template srli32<8>(V::mullo32(v, vstrength));
        T o;
        if (straight)
        {   // colour lanes: v*255/a  alpha lane: a/128   (one divide for both, m3 = alpha lane mask)
            F fv = V::cvt_i2f(v), fa = V::cvt_i2f(V::bcast_a32(v));
            F num = V::selectf(m3, fv, V::mulf(fv, V::set1f(255.0f)));
            F den = V::selectf(m3, V::set1f(128.0f), V::maxf(fa, V::set1f(1.0f)));
            o = V::cvt_f2i_round(V::mulf(num, V::rcpf_nr(den)));
        }
        else o = V::template srli32<7>(V::add32(v, V::set1_32(64)));
        V::store_px(out, V::min32(o, V::set1_32(255)));
    }

    template<class Op>
    static void blur_composite(const uint16_t* work, int L, int* dst, int dw,
                               int x0, int y0, int x1, int y1, int ox, int oy, int strength, bool straight, const Op& op)
    {
        SR2D_ALIGN(32) int tmp[8];
        SR2D_ALIGN(32) const int m3a[8] = { 0, 0, 0, -1, 0, 0, 0, -1 };
        const T m3 = V::loadu(m3a);
        const int w = x1 - x0;
        const bool scale = strength < 256;
        const T vstr = V::set1_32(strength);
        for (int y = y0; y < y1; ++y)
        {
            const uint16_t* src = work + (size_t)(y - oy) * L + (size_t)(x0 - ox) * 4;
            int* d = dst + (size_t)y * dw + x0;
            int x = 0;
            for (; x + N <= w; x += N)
            {
                for (int i = 0; i < N; i += N / 4) blur_out_px(src + (x + i) * 4, tmp + i, vstr, m3, scale, straight);
                V::storeu(d + x, op(V::loadu(tmp), V::loadu(d + x)));
            }
            if (x < w)
            {   // tail: the work buffer may be over-read here (it has slack), the destination is masked
                SR2D_ALIGN(32) int td[8];
                const int n = w - x;
                for (int i = 0; i < N; i += N / 4) blur_out_px(src + (x + i) * 4, tmp + i, vstr, m3, scale, straight);
                for (int i = 0; i < n; ++i) td[i] = d[x + i];
                V::storeu(td, op(V::loadu(tmp), V::loadu(td)));
                for (int i = 0; i < n; ++i) d[x + i] = td[i];
            }
        }
    }

    struct BlurGeo
    {
        int r, passes, m, ox, oy, x0, y0, x1, y1, wx0, wx1, wy0, wy1, WW, HH, L, HHp;
        size_t plane, need;
        bool ok;
    };
    static BlurGeo blur_geo(int sw, int sh, int clipL, int clipT, int clipR, int clipB, int dx, int dy, int r, int flags)
    {
        BlurGeo g; g.ok = false;
        if (sw <= 0 || sh <= 0 || clipL >= clipR || clipT >= clipB) return g;
        g.r = r = r < 0 ? 0 : r > 512 ? 512 : r;
        g.passes = (flags & 16) ? 1 : (flags & 4) ? 2 : 3;   // 16 = single box pass (DRAW_FX BLUR_BOX)
        const int m = g.m = r * g.passes;                 // support of the combined kernel
        const int W16 = sw + 2 * m, H16 = sh + 2 * m;     // full work image
        const int ox = g.ox = dx - m, oy = g.oy = dy - m; // its origin on the destination
        const int x0 = g.x0 = ox > clipL ? ox : clipL, y0 = g.y0 = oy > clipT ? oy : clipT;
        const int x1 = g.x1 = ox + W16 < clipR ? ox + W16 : clipR, y1 = g.y1 = oy + H16 < clipB ? oy + H16 : clipB;
        if (x0 >= x1 || y0 >= y1) return g;
        // only rows/columns within m of the visible part can influence it
        g.wx0 = (x0 - ox) - m > 0 ? (x0 - ox) - m : 0; g.wx1 = (x1 - ox) + m < W16 ? (x1 - ox) + m : W16;
        g.wy0 = (y0 - oy) - m > 0 ? (y0 - oy) - m : 0; g.wy1 = (y1 - oy) + m < H16 ? (y1 - oy) + m : H16;
        if (flags & 8)
        {   // in-image mode (DRAW_FX): the source already contains its transparent margin, so
            // rows/columns outside the sprite are all zero and contribute nothing - and the
            // visible part is the sprite itself. Restrict the work image to the sprite: the
            // running sums then only read what exists (a blur of r 512 on a 2 Mpx work image
            // would otherwise allocate a 3x oversized plane pair).
            if (g.wx0 < m) g.wx0 = m;
            if (g.wy0 < m) g.wy0 = m;
            if (g.wx1 > m + sw) g.wx1 = m + sw;
            if (g.wy1 > m + sh) g.wy1 = m + sh;
        }
        g.WW = g.wx1 - g.wx0; g.HH = g.wy1 - g.wy0;
        g.L = g.WW * 4;                                              // u16 per row
        g.HHp = g.HH + (g.HH & 1);                                   // even row count (pairs on AVX2)
        g.plane = (size_t)g.L * g.HHp * sizeof(uint16_t) + 64;       // + slack for the vector tails
        g.need = g.plane * 2 + (size_t)(g.L + 8) * sizeof(uint32_t) + 64;
        g.ok = true;
        return g;
    }

    static int DRAW_BLUR(int* src, int sw, int sh, int* dst, int dw, int clipL, int clipT, int clipR, int clipB,
                         int dx, int dy, int r, int op, int k, int flags, int strength)
    {
        if (!src || !dst) return 0;
        flags &= 7 | 16;                  // public bits only (bit 3 is the DRAW_FX in-image mode); 16 = one box pass
        const BlurGeo g = blur_geo(sw, sh, clipL, clipT, clipR, clipB, dx, dy, r, flags);
        if (!g.ok) return 0;
        size_t got = 0;
        uint8_t* mem = (uint8_t*)sr2d_scratch_acquire(g.need, &got);
        if (!mem) return 0;
        const int n = blur_exec(g, src, sw, sh, dst, dw, op, k, flags, strength, mem);
        sr2d_scratch_release(mem, got);
        return n;
    }

    // the blur proper on caller-provided memory (>= g.need bytes)
    static int blur_exec(const BlurGeo& g, const int* src, int sw, int sh, int* dst, int dw, int op, int k, int flags, int strength, uint8_t* mem)
    {
        strength = strength < 0 ? 0 : strength > 256 ? 256 : strength;
        const int r = g.r, passes = g.passes, m = g.m, ox = g.ox, oy = g.oy, x0 = g.x0, y0 = g.y0, x1 = g.x1, y1 = g.y1;
        const int wx0 = g.wx0, wx1 = g.wx1, wy0 = g.wy0, WW = g.WW, HH = g.HH, L = g.L, HHp = g.HHp;
        const size_t plane = g.plane;
        uint16_t* A = (uint16_t*)mem;
        uint16_t* B = (uint16_t*)(mem + plane);
        uint32_t* acc = (uint32_t*)(mem + plane * 2);

        // ---- 1. load into A: premultiplied u16 x128, zero outside the sprite
        {
            const bool premul = (flags & 1) != 0, opaque = (flags & 2) != 0;
            const int sxa = wx0 - m > 0 ? wx0 - m : 0, sxb = wx1 - m < sw ? wx1 - m : sw;   // source columns present
            const int lead = (sxa - (wx0 - m)) * 4, body = (sxb - sxa) * 4;
            for (int y = 0; y < HHp; ++y)
            {
                uint16_t* p = A + (size_t)y * L;
                const int sy = y + wy0 - m;
                if (sy < 0 || sy >= sh || body <= 0 || y >= HH) { memset(p, 0, (size_t)L * sizeof(uint16_t)); continue; }
                if (lead > 0) memset(p, 0, (size_t)lead * sizeof(uint16_t));
                blur_load_row(src + (size_t)sy * sw + sxa, p + lead, sxb - sxa, premul, opaque);
                if (lead + body < L) memset(p + lead + body, 0, (size_t)(L - lead - body) * sizeof(uint16_t));
            }
        }

        // ---- 2. passes: all horizontal (row-local, stays in L1), then all vertical
        uint16_t* cur = A; uint16_t* oth = B;
        if (r > 0)
        {
            const float inv = 1.0f / (float)(2 * r + 1);
            const int step = N / 4;                                   // rows per call
            for (int y = 0; y < HHp; y += step)
            {
                uint16_t* rin = cur + (size_t)y * L; uint16_t* rout = oth + (size_t)y * L;
                for (int pss = 0; pss < passes; ++pss) { blur_h_rows(rin, rout, WW, L, r, inv); uint16_t* t = rin; rin = rout; rout = t; }
            }
            if (passes & 1) { uint16_t* t = cur; cur = oth; oth = t; }
            // vertical: column strips (4096 u16 = 1024 px wide) so the three passes of a strip
            // run on a cache/TLB-friendly footprint instead of streaming the whole plane three
            // times (measured ~25 % faster than full-width passes on a 1080p work image)
            const int strip = 4096;
            for (int c0 = 0; c0 < L; c0 += strip)
            {
                const int c1 = c0 + strip < L ? c0 + strip : L;
                uint16_t* a = cur; uint16_t* b = oth;
                for (int pss = 0; pss < passes; ++pss) { blur_v(a, b, L, c0, c1, HH, r, inv, acc); uint16_t* t = a; a = b; b = t; }
            }
            if (passes & 1) { uint16_t* t = cur; cur = oth; oth = t; }
        }

        // ---- 3. composite the visible part
        // Ops that look at the source alpha (AlphaTest, AlphaBlend) get straight colour
        // (c/a), everything else gets the alpha-weighted (premultiplied) colour: those ops
        // ignore alpha, so the fade-out of the blur has to be in the colour itself (fading
        // to black = neutral for Add/Add2D/Max, "glow" for Paint). AlphaOver is premultiplied
        // by definition.
        const int oxl = ox + wx0, oyl = oy + wy0;
        if (is_blend_mode(op)) { with_blend_mode(op | SR2D_OP_SRC_PREMUL, [&](const auto& f) { blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, f); }); return (x1 - x0) * (y1 - y0); }
        switch (op & 0xff)
        {
        case 1:  blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, OpPaint()); break;
        case 2:  blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, true,  OpAlphaT()); break;
        case 4:  blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, OpAdd2D()); break;
        case 5:  blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, OpAdd()); break;
        case 6:  blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, OpMod()); break;
        case 7:  blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, OpMod2X()); break;
        case 8:  blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, OpMax()); break;
        case 9:  blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, OpMin()); break;
        case 10: blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, OpBlend(k)); break;
        case 11: blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, false, OpAlphaOver()); break;
        case 12: blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, true,  OpPaint()); break;     // copy, straight alpha
        default: blur_composite(cur, L, dst, dw, x0, y0, x1, y1, oxl, oyl, strength, true,  OpAlphaB()); break;   // 3: AlphaBlend
        }
        return (x1 - x0) * (y1 - y0);
    }

    static void DPBM_(int* src, int* dst, int w, int h, int c, int ws, int wd)
    { rect(src, dst, w, h, ws, wd, OpDPBM(c)); }
    static void MASK_DPBM(int* src, int* dst, int* mask, int w, int h, int c, int maskex, int ws, int wd, int wm, int notm)
    { rect_masked(src, dst, mask, w, h, maskex, ws, wd, wm, notm, OpDPBM(c)); }

#include "sr2d_fx.inl"
#include "sr2d_select.inl"
#include "sr2d_voxel.inl"
};
