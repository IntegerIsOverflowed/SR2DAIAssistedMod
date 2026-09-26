// SR2D - flood fill / selection kernels (included inside struct K, after sr2d_fx.inl).
//
// The three entry points work on an 8-bit COVERAGE MASK (one byte per pixel, 0 = not
// selected, 255 = fully selected, in between = soft edge), which is what an editor's
// "selection" is:
//
//   FLOOD_MASK  image + seed (or reference colour) + tolerance  -> mask      (magic wand)
//   FILL_MASK8  colour -> image through a mask, LineOp compositors           (bucket fill)
//   LERP_MASK8  dst = lerp(dst, src, mask)                                   (commit / paste through a selection)
//
// A flood fill is FLOOD_MASK + FILL_MASK8 on a scratch mask. Doing it in two passes (rather
// than writing the colour while scanning) keeps the comparison source stable, which is
// what makes tolerance + any blend op work, and it costs almost nothing: the fill pass is a
// run-length walk over the mask that mostly skips 32 bytes at a time.
//
// Match rule: max over the channels of |p - ref| <= tol (Chebyshev, like GIMP / Paint.NET).
// Alpha is one of the channels unless SR2D_FLOOD_IGNORE_ALPHA.

#define SR2D_FLOOD_GLOBAL        1   // every matching pixel in the clip rect (not only the connected region)
#define SR2D_FLOOD_IGNORE_ALPHA  2   // compare RGB only
#define SR2D_FLOOD_DIAGONAL      4   // 8-connected (regions may continue across a corner)
#define SR2D_FLOOD_SOFT          8   // coverage falls off from 255 (diff <= tol/2) to ~1 (diff == tol)
#define SR2D_FLOOD_REF_COLOR    16   // compare with 'ref' instead of the pixel under the seed

    static SR2D_INLINE int ctz32(uint32_t v)
    {
#if defined(_MSC_VER)
        unsigned long i; _BitScanForward(&i, v); return (int)i;
#else
        return __builtin_ctz(v);
#endif
    }

    struct FloodMatch
    {
        T refv, tolv, chmask; uint32_t ref, cm; int tol;
        FloodMatch(uint32_t r, int t, bool ignore_alpha)
            : refv(V::set1_32((int)r)), tolv(V::set1_8((char)(t > 255 ? 255 : t))),
              chmask(V::set1_32(ignore_alpha ? 0x00ffffff : (int)0xffffffffu)),
              ref(r), cm(ignore_alpha ? 0x00ffffffu : 0xffffffffu), tol(t > 255 ? 255 : t) {}
        // max channel difference (scalar)
        SR2D_INLINE int diff(uint32_t p) const
        {
            int m = 0;
            uint32_t a = p & cm, b = ref & cm;
            for (int sh = 0; sh < 32; sh += 8) { int d = (int)((a >> sh) & 255) - (int)((b >> sh) & 255); if (d < 0) d = -d; if (d > m) m = d; }
            return m;
        }
        SR2D_INLINE bool one(uint32_t p) const { return diff(p) <= tol; }
        // all-ones dword per matching pixel
        SR2D_INLINE T vec(T p) const
        {
            T d = V::and_(V::or_(V::subs_u8(p, refv), V::subs_u8(refv, p)), chmask);   // per-byte |p - ref|
            return V::cmpeq32(V::subs_u8(d, tolv), V::zero());                            // no byte above tol
        }
    };

    // length of the run of pixels starting at x (< xend) that match and are not yet marked
    static SR2D_INLINE int flood_run(const int* row, const uint8_t* mrow, int x, int xend, const FloodMatch& M)
    {
        const int x0 = x;
        const uint32_t all = (N * 4 >= 32) ? 0xffffffffu : ((1u << (N * 4)) - 1u);
        for (; x + N <= xend; x += N)
        {
            T ok = V::and_(M.vec(V::loadu(row + x)), V::cmpeq32(V::load8x(mrow + x), V::zero()));
            uint32_t bad = ~(uint32_t)V::movemask8(ok) & all;
            if (bad) return x + (ctz32(bad) >> 2) - x0;
        }
        for (; x < xend; ++x) if (mrow[x] || !M.one((uint32_t)row[x])) break;
        return x - x0;
    }

    struct FloodSpan { int x1, x2, y, dy; };

    struct FloodStack
    {
        FloodSpan* p; int n, cap; FloodSpan inl[256]; bool oom;
        FloodStack() : p(inl), n(0), cap(256), oom(false) {}
        ~FloodStack() { if (p != inl) sr2d_free(p); }
        SR2D_INLINE void push(int x1, int x2, int y, int dy)
        {
            if (n == cap)
            {
                int nc = cap * 2;
                FloodSpan* q = (FloodSpan*)sr2d_alloc(sizeof(FloodSpan) * (size_t)nc);
                if (!q) { oom = true; return; }
                memcpy(q, p, sizeof(FloodSpan) * (size_t)n);
                if (p != inl) sr2d_free(p);
                p = q; cap = nc;
            }
            FloodSpan& s = p[n++]; s.x1 = x1; s.x2 = x2; s.y = y; s.dy = dy;
        }
    };

    // FLOOD_MASK: writes the coverage mask of the region around (x, y) into mask (stride mw
    // bytes; the clip rect part is overwritten, the rest untouched). Returns the number of
    // selected pixels; bbox[4] (optional) receives L, T, R, B (exclusive; R <= L when empty).
    static int FLOOD_MASK(const int* src, int sw, int sh, int clipL, int clipT, int clipR, int clipB,
                          int x, int y, int ref, int tol, int flags, uint8_t* mask, int mw, int* bbox)
    {
        if (bbox) { bbox[0] = bbox[1] = 0; bbox[2] = bbox[3] = 0; }
        if (!src || !mask || sw <= 0 || sh <= 0) return 0;
        if (clipL < 0) clipL = 0;
        if (clipT < 0) clipT = 0;
        if (clipR > sw) clipR = sw;
        if (clipB > sh) clipB = sh;
        if (clipL >= clipR || clipT >= clipB) return 0;
        for (int yy = clipT; yy < clipB; ++yy) memset(mask + (size_t)yy * mw + clipL, 0, (size_t)(clipR - clipL));
        const bool global = (flags & SR2D_FLOOD_GLOBAL) != 0;
        if (!global && (x < clipL || x >= clipR || y < clipT || y >= clipB)) return 0;
        if (tol < 0) tol = 0;
        uint32_t rc = (flags & SR2D_FLOOD_REF_COLOR) ? (uint32_t)ref
                    : (x >= 0 && x < sw && y >= 0 && y < sh) ? (uint32_t)src[(size_t)y * sw + x] : (uint32_t)ref;
        const FloodMatch M(rc, tol, (flags & SR2D_FLOOD_IGNORE_ALPHA) != 0);
        int count = 0, bl = clipR, bt = clipB, br = clipL, bb = clipT;

        if (global)
        {
            const uint32_t all = (N * 4 >= 32) ? 0xffffffffu : ((1u << (N * 4)) - 1u);
            for (int yy = clipT; yy < clipB; ++yy)
            {
                const int* row = src + (size_t)yy * sw; uint8_t* mrow = mask + (size_t)yy * mw;
                int first = clipR, last = clipL - 1, xx = clipL;
                for (; xx + N <= clipR; xx += N)
                {
                    uint32_t m = (uint32_t)V::movemask8(M.vec(V::loadu(row + xx))) & all & 0x11111111u;
                    if (!m) continue;
                    for (int i = 0; i < N; ++i) if ((m >> (4 * i)) & 1u) { mrow[xx + i] = 255; ++count; last = xx + i; if (first > last) first = last; }
                }
                for (; xx < clipR; ++xx) if (M.one((uint32_t)row[xx])) { mrow[xx] = 255; ++count; last = xx; if (first > last) first = last; }
                if (last >= first) { if (first < bl) bl = first; if (last + 1 > br) br = last + 1; if (yy < bt) bt = yy; bb = yy + 1; }
            }
        }
        else
        {
            const int ext = (flags & SR2D_FLOOD_DIAGONAL) ? 1 : 0;
            if (!M.one((uint32_t)src[(size_t)y * sw + x])) return 0;   // seed must match (matters with REF_COLOR)
            FloodStack st;
            st.push(x, x, y, 1);
            st.push(x, x, y - 1, -1);
            while (st.n && !st.oom)
            {
                FloodSpan s = st.p[--st.n];
                int yy = s.y; if (yy < clipT || yy >= clipB) continue;
                const int* row = src + (size_t)yy * sw; uint8_t* mrow = mask + (size_t)yy * mw;
                int x1 = s.x1 < clipL ? clipL : s.x1, x2 = s.x2 >= clipR ? clipR - 1 : s.x2;
                if (x1 > x2) continue;
                // the parent span (row yy - dy) selected exactly [s.x1 + ext, s.x2 - ext]; anything
                // this row reaches outside that range must be pushed back towards the parent row
                const int pL = s.x1 + ext, pR = s.x2 - ext;
                int xa = x1;
                if (!mrow[xa] && M.one((uint32_t)row[xa]))
                {   // extend to the left of the parent range
                    while (xa > clipL && !mrow[xa - 1] && M.one((uint32_t)row[xa - 1])) --xa;
                    if (xa < x1) { memset(mrow + xa, 255, (size_t)(x1 - xa)); count += x1 - xa; }
                }
                bool any = false;
                while (x1 <= x2)
                {
                    int run = flood_run(row, mrow, x1, clipR, M);
                    if (run > 0)
                    {
                        memset(mrow + x1, 255, (size_t)run); count += run; any = true;
                        const int xe = x1 + run;                    // this span is [xa, xe)
                        st.push(xa - ext, xe - 1 + ext, yy + s.dy, s.dy);
                        if (xa - ext < pL) st.push(xa - ext, pL - 1, yy - s.dy, -s.dy);
                        if (xe - 1 + ext > pR) st.push(pR + 1, xe - 1 + ext, yy - s.dy, -s.dy);
                        if (xa < bl) bl = xa;
                        if (xe > br) br = xe;
                        x1 = xe;
                    }
                    ++x1;
                    while (x1 <= x2 && (mrow[x1] || !M.one((uint32_t)row[x1]))) ++x1;
                    xa = x1;
                }
                if (any) { if (yy < bt) bt = yy; if (yy + 1 > bb) bb = yy + 1; }
            }
            if (st.oom) return -1;
        }
        if (count == 0) return 0;

        if ((flags & SR2D_FLOOD_SOFT) && tol > 0)
        {   // linear falloff over the upper half of the tolerance band; never drops to 0 (stays selected)
            const int h = tol >> 1, span = tol - h + 1;
            for (int yy = bt; yy < bb; ++yy)
            {
                const int* row = src + (size_t)yy * sw; uint8_t* mrow = mask + (size_t)yy * mw;
                for (int xx = bl; xx < br; ++xx)
                {
                    if (!mrow[xx]) continue;
                    int d = M.diff((uint32_t)row[xx]);
                    if (d > h) { int c = 255 - (d - h) * 255 / span; mrow[xx] = (uint8_t)(c < 1 ? 1 : c); }
                }
            }
        }
        if (bbox) { bbox[0] = bl; bbox[1] = bt; bbox[2] = br; bbox[3] = bb; }
        return count;
    }

    // ---- FILL_MASK8: colour through a coverage mask. op = LineOp codes (1 Set, 2 Xor,
    // 3 AlphaBlend, 4 Blend(k), 5 Add, 6 Max, 7 Min, 8 AlphaOver). Returns pixels touched.
    template<class Cmp>
    static int mask8_fill(int* dst, int dw, int cl, int ct, int cr, int cb, const uint8_t* mask, int mw, const Cmp& cmp)
    {
        const int B = N * 4;                                   // mask bytes per vector
        const T z = V::zero(), full = V::set1_8((char)-1);
        const uint32_t all = (B >= 32) ? 0xffffffffu : ((1u << B) - 1u);
        int touched = 0;
        for (int y = ct; y < cb; ++y)
        {
            int* d = dst + (size_t)y * dw; const uint8_t* m = mask + (size_t)y * mw;
            int x = cl;
            while (x < cr)
            {
                // skip unselected pixels, a vector at a time
                for (; x + B <= cr; x += B) if (((uint32_t)V::movemask8(V::cmpeq8(V::loadu(m + x), z)) & all) != all) break;
                while (x < cr && m[x] == 0) ++x;
                if (x >= cr) break;
                const int x0 = x; const uint8_t v = m[x];
                if (v == 255) for (; x + B <= cr; x += B) if (((uint32_t)V::movemask8(V::cmpeq8(V::loadu(m + x), full)) & all) != all) break;
                while (x < cr && m[x] == v) ++x;
                cmp.run(d + x0, x - x0, v + (v >> 7));
                touched += x - x0;
            }
        }
        return touched;
    }

    static int FILL_MASK8(int* dst, int dw, int clipL, int clipT, int clipR, int clipB, const uint8_t* mask, int mw, int col, int op, int k)
    {
        if (!dst || !mask || clipL >= clipR || clipT >= clipB) return 0;
        const uint32_t c = (uint32_t)col; const int a = (int)(c >> 24);
        if (is_blend_mode(op)) { int r = 0; if (a) with_blend_mode(op & ~SR2D_OP_SRC_PREMUL, [&](const auto& f) { r = mask8_fill(dst, dw, clipL, clipT, clipR, clipB, mask, mw, make_cmp_mode(c, f)); }); return r; }
        switch (op & 0xff)
        {
        case 2: return mask8_fill(dst, dw, clipL, clipT, clipR, clipB, mask, mw, CmpXor(c));
        case 3: return a ? mask8_fill(dst, dw, clipL, clipT, clipR, clipB, mask, mw, CmpConst<true>(c, a + (a >> 7))) : 0;
        case 4: return mask8_fill(dst, dw, clipL, clipT, clipR, clipB, mask, mw, CmpConst<false>(c, k < 0 ? 0 : k > 256 ? 256 : k));
        case 5: return mask8_fill(dst, dw, clipL, clipT, clipR, clipB, mask, mw, CmpFn<OpAdd, &K::sc_add>(c));
        case 6: return mask8_fill(dst, dw, clipL, clipT, clipR, clipB, mask, mw, CmpFn<OpMax, &K::sc_max>(c));
        case 7: return mask8_fill(dst, dw, clipL, clipT, clipR, clipB, mask, mw, CmpFn<OpMin, &K::sc_min>(c));
        case 8: return a ? mask8_fill(dst, dw, clipL, clipT, clipR, clipB, mask, mw, CmpConst<false>(c | 0xff000000u, a + (a >> 7))) : 0;
        default: return mask8_fill(dst, dw, clipL, clipT, clipR, clipB, mask, mw, CmpConst<false>(c, 256));
        }
    }

    // ---- LERP_MASK8: dst = dst + (src - dst) * m/255 per pixel (m = 255 -> exactly src,
    // m = 0 -> untouched). invert != 0 uses 255 - m. All four bytes. Used to commit an edit
    // through a selection (draw anything into a copy, then blend it back where selected).
    static void LERP_MASK8(const int* src, int* dst, const uint8_t* mask, int w, int h, int ws, int wd, int wm, int invert)
    {
        if (!src || !dst || !mask || w <= 0 || h <= 0) return;
        const T z = V::zero(), c256 = V::set1_16(256), c255 = V::set1_32(255);
        const uint32_t all = (N * 4 >= 32) ? 0xffffffffu : ((1u << (N * 4)) - 1u);
        for (int y = 0; y < h; ++y, src += ws, dst += wd, mask += wm)
        {
            int x = 0;
            for (; x + N <= w; x += N)
            {
                T mb = V::load8x(mask + x);                                  // N mask bytes -> dword lanes
                if (invert) mb = V::sub32(c255, mb);
                // whole-vector shortcuts: all 0 -> skip, all 255 -> copy
                uint32_t bits = (uint32_t)V::movemask8(V::cmpeq32(mb, z)) & all;
                if (bits == all) continue;
                bits = (uint32_t)V::movemask8(V::cmpeq32(mb, c255)) & all;
                if (bits == all) { V::storeu(dst + x, V::loadu(src + x)); continue; }
                T wv = V::add32(mb, V::template srli32<7>(mb));               // 0..256
                wv = V::or_(wv, V::template slli32<16>(wv));                 // [w, w] 16-bit lanes per dword
                T wlo = V::unpacklo32(wv, wv), whi = V::unpackhi32(wv, wv);  // per pixel: 4 x 16-bit weights
                T s = V::loadu(src + x), d = V::loadu(dst + x);
                T lo = V::add16(V::mullo16(V::lo8to16(d), V::sub16(c256, wlo)), V::mullo16(V::lo8to16(s), wlo));
                T hi = V::add16(V::mullo16(V::hi8to16(d), V::sub16(c256, whi)), V::mullo16(V::hi8to16(s), whi));
                V::storeu(dst + x, V::packus16(V::template srli16<8>(lo), V::template srli16<8>(hi)));
            }
            for (; x < w; ++x)
            {
                int m = mask[x]; if (invert) m = 255 - m;
                if (m == 0) continue;
                if (m == 255) { dst[x] = src[x]; continue; }
                dst[x] = (int)lerp8s((uint32_t)dst[x], (uint32_t)src[x], m + (m >> 7));
            }
        }
    }
