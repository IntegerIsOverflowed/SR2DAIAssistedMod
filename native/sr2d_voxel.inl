// SR2D - voxel kernels (included inside struct K, after sr2d_select.inl).
//
//   VOXEL_FACES   grid -> per-cell "solid + exposed sides" byte      (once per edit)
//   VOXEL_LIGHT   grid -> per-cell packed light (sky, r, g, b 0..15) (once per edit, optional)
//   VOXEL_RENDER  grid + faces (+ light) -> pixels                   (per frame)
//
// Grid layout: index = (z * gh + y) * gw + x; x east, y north, z up. A voxel is a unit cube
// [x, x+1) x [y, y+1) x [z, z+1). Alpha 0 = empty, anything else = opaque.
//
// Projection: orthographic, sx = m0 x + m1 y + m2 z + ox, sy = m3 x + m4 y + m5 z + oy.
// 'view' is the direction the camera looks along in grid space; a face is visible when its
// normal points against it.
//
// Ordering: the grid is walked far-to-near per axis (descending where view > 0, ascending
// where < 0), z outer, y middle, x inner. For unit cubes on an integer lattice under an
// orthographic projection this lexicographic order IS a correct painter's order: if cube B
// hides part of cube A then B - A has the sign of the view direction on every non-zero
// component (proof: b - a = t v with t > 0, a, b inside the cubes, so each integer component
// of B - A is t v_i plus something in [-1, 1]). Hence no depth buffer is needed and only the
// exposed, camera-facing faces of surface voxels are ever rasterised; interior voxels and
// hidden sides cost nothing (they are skipped 16 / 32 cells at a time with a SIMD test of
// the face bytes). Rows and slabs whose projection misses the clip rect are skipped, and the
// x range of each row is clipped analytically, so a zoomed-in view costs only what is seen.
//
// Cube mode rasterises each face as a parallelogram with the pixel-centre rule and half-open
// [left, right) / [top, bottom) spans. Both faces sharing an edge compute the same edge from
// the same two lattice points (never from a translated copy), so the result is watertight:
// no cracks and no double-drawn pixels between neighbours.
//
// Lighting tiers (scene.lighting):
//   0  none                : colour as stored
//   1  faces               : x shade[face]
//   2  propagated light    : x max(lut[sky] * skyTint, lampLut[emissive rgb]) of the cell in front
//                            (or the voxel's own light if brighter, so emitters glow fully)
//   3  smooth light + AO   : 2, but sampled per face corner from the 2 x 2 cells in front,
//                            interpolated across the face (Gouraud), times ao[occluders]
//                            (Minecraft's rule: 3 when both edge neighbours are solid).
// Point mode (1 voxel -> 1 pixel) blends the brightness of the visible exposed faces weighted
// by |view . normal|, i.e. a top-lit pixel-art look without any hidden-surface work.

// face tables: 0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z
struct VoxFaceDef { signed char n[3], c0[3], u[3], w[3]; };
static const VoxFaceDef& vox_facedef(int f)
{
    static const VoxFaceDef F[6] = {
        { { 1, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } },
        { {-1, 0, 0 }, { 0, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } },
        { { 0, 1, 0 }, { 0, 1, 0 }, { 1, 0, 0 }, { 0, 0, 1 } },
        { { 0,-1, 0 }, { 0, 0, 0 }, { 1, 0, 0 }, { 0, 0, 1 } },
        { { 0, 0, 1 }, { 0, 0, 1 }, { 1, 0, 0 }, { 0, 1, 0 } },
        { { 0, 0,-1 }, { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 } } };
    return F[f];
}

static SR2D_INLINE int vox_floor(float f) { int i = (int)f; return f < (float)i ? i - 1 : i; }
static SR2D_INLINE int vox_ceil(float f)  { int i = (int)f; return f > (float)i ? i + 1 : i; }
static SR2D_INLINE float vox_clampf(float v, float lo, float hi) { return !(v >= lo) ? lo : v > hi ? hi : v; }   // NaN -> lo
// x^g for x in [0,1], g > 0, without the CRT: exponent / mantissa split, quartic log2 on [1,2), cubic 2^f on
// [0,1) (least-squares fits, max abs error 5e-4 on the result - below 1/255 after the multiply). g == 1 is
// short-circuited by the caller.
static SR2D_INLINE float vox_powf(float x, float g)
{
    if (!(x > 0.f)) return 0.f;
    if (x >= 1.f) return 1.f;
    union { float f; int32_t i; } u; u.f = x;
    const int e = ((u.i >> 23) & 255) - 127;
    u.i = (u.i & 0x007fffff) | 0x3f800000;                  // mantissa in [1, 2)
    const float m = u.f;
    const float l2 = -2.4967738f + (4.0283728f + (-2.0810602f + (0.62881573f - 0.079150366f * m) * m) * m) * m;
    const float y = ((float)e + l2) * g;                    // log2(x) * g, <= 0
    if (y < -126.f) return 0.f;
    float fl = (float)(int)y; if (y < fl) fl -= 1.f;
    const float fr = y - fl;                                // [0, 1)
    const float p = 0.99981196f + fr * (0.69683858f + fr * (0.22412644f + fr * 0.07901994f));   // 2^fr
    u.f = p; u.i += (int32_t)fl << 23;
    return u.f;
}
static SR2D_INLINE int popcnt32(uint32_t v)
{
    v = v - ((v >> 1) & 0x55555555u);
    v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
    return (int)((((v + (v >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24);
}
static SR2D_INLINE int vox_msb(uint32_t v)
{
#if defined(_MSC_VER)
    unsigned long i; _BitScanReverse(&i, v); return (int)i;
#else
    return 31 - __builtin_clz(v);
#endif
}

// ---- VOXEL_FACES ------------------------------------------------------------------------
// Two passes, both vectorised on the byte array:
//   1) solid flag: faces[i] = 0x40 if alpha(vox[i]) != 0  (loads 8-byte voxels, tests the top byte of argb)
//   2) exposed sides: for a row of NB cells load the row and its 6 neighbours (x +- 1 are unaligned loads of the
//      same row; y +- 1 and z +- 1 are other rows) and build the bits with byte compares. Grid borders count as
//      empty. Interior cells (no exposed side) stay 0x40; empty cells stay 0. The return value counts cells with
//      any exposed bit, summed from a per-row popcount of the "has bits" mask.
static int VOXEL_FACES(const void* voxp, int gw, int gh, int gd, uint8_t* faces)
{
    const SR2D_Voxel* vox = (const SR2D_Voxel*)voxp;
    if (!vox || !faces || gw <= 0 || gh <= 0 || gd <= 0) return 0;
    const long long cellsll = (long long)gw * gh * gd;
    if (cellsll > 0x7fffffff) return 0;
    const int cells = (int)cellsll, slab = gw * gh;
    const int NB = N * 4;                                   // bytes per vector (16 / 32)

    // pass 1: solid flags (alpha != 0 -> 0x40). Sequential 8-byte reads; the compiler vectorises the branch-free form.
    {
        const uint32_t* a = (const uint32_t*)vox;           // argb at even dwords
        for (int i = 0; i < cells; ++i) faces[i] = (uint8_t)(((a[(size_t)i * 2] >> 24) != 0) << 6);
    }

    // pass 2: neighbours. In-place is safe: every neighbour read is masked with 0x40, the one bit no update changes.
    const T solid = V::set1_8(0x40), zero = V::zero();
    const T bPX = V::set1_8(1), bMX = V::set1_8(2), bPY = V::set1_8(4), bMY = V::set1_8(8), bPZ = V::set1_8(16), bMZ = V::set1_8(32);
    int exposed = 0;
    for (int z = 0; z < gd; ++z)
        for (int y = 0; y < gh; ++y)
        {
            uint8_t* row = faces + (size_t)(z * gh + y) * gw;
            const uint8_t* rowYm = y > 0 ? row - gw : 0;
            const uint8_t* rowYp = y + 1 < gh ? row + gw : 0;
            const uint8_t* rowZm = z > 0 ? row - slab : 0;
            const uint8_t* rowZp = z + 1 < gd ? row + slab : 0;
            // vector body: cells [x, x + NB) with 1 <= x and x + NB <= gw - 1, so the x +- 1 loads stay inside the row
            int x = 1, xv1 = 1;
            for (; x + NB <= gw - 1; x += NB)
            {
                const T c = V::and_(V::loadu(row + x), solid);
                const T cs = V::cmpeq8(c, solid);           // solid lanes = 0xff
                if (!V::movemask8(cs)) continue;            // whole chunk empty: nothing to write
                T bits = V::and_(V::cmpeq8(V::and_(V::loadu(row + x + 1), solid), zero), bPX);
                bits = V::or_(bits, V::and_(V::cmpeq8(V::and_(V::loadu(row + x - 1), solid), zero), bMX));
                bits = V::or_(bits, rowYp ? V::and_(V::cmpeq8(V::and_(V::loadu(rowYp + x), solid), zero), bPY) : bPY);
                bits = V::or_(bits, rowYm ? V::and_(V::cmpeq8(V::and_(V::loadu(rowYm + x), solid), zero), bMY) : bMY);
                bits = V::or_(bits, rowZp ? V::and_(V::cmpeq8(V::and_(V::loadu(rowZp + x), solid), zero), bPZ) : bPZ);
                bits = V::or_(bits, rowZm ? V::and_(V::cmpeq8(V::and_(V::loadu(rowZm + x), solid), zero), bMZ) : bMZ);
                bits = V::and_(bits, cs);                   // only solid cells get bits
                V::storeu(row + x, V::or_(c, bits));
                exposed += popcnt32((uint32_t)V::movemask8(V::andnot(V::cmpeq8(bits, zero), cs)));
            }
            xv1 = x;                                        // first cell not covered by the vector body
            // scalar: cell 0 and the tail [xv1, gw)
            for (int k = 0; k < gw; k = (k == 0) ? xv1 : k + 1)
            {
                if (row[k] & 0x40)
                {
                    int b = 0x40;
                    if (k + 1 >= gw || !(row[k + 1] & 0x40)) b |= 1;
                    if (k == 0 || !(row[k - 1] & 0x40)) b |= 2;
                    if (!rowYp || !(rowYp[k] & 0x40)) b |= 4;
                    if (!rowYm || !(rowYm[k] & 0x40)) b |= 8;
                    if (!rowZp || !(rowZp[k] & 0x40)) b |= 16;
                    if (!rowZm || !(rowZm[k] & 0x40)) b |= 32;
                    row[k] = (uint8_t)b;
                    exposed += (b & 63) != 0;
                }
            }
        }
    return exposed;
}

// ---- VOXEL_LIGHT ------------------------------------------------------------------------
// packed light: sky << 24 | r << 16 | g << 8 | b, every byte a light level in FIXED POINT 1/8 steps: 0..120 = level
// 0..15 (SR2D_VOXL_ONE per level, bit 7 always clear so the SWAR helpers work). The level drops by "dec" per cell
// (flags bits 8..15, default SR2D_VOXL_ONE = one level per cell = the Minecraft rule, reach 15 cells for a full
// lamp; dec 4 -> 30 cells, 2 -> 60, 1 -> 120 cells). Readers divide by 8 (VoxelGrid.LightAt does).
#define SR2D_VOXL_MASK 0x7F7F7F7Fu
#define SR2D_VOXL_ONE  8u
#define SR2D_VOXL_MAXL (15u * SR2D_VOXL_ONE)
static SR2D_INLINE uint32_t vox_sub(uint32_t L, uint32_t dd)       // saturating per-byte subtract of a constant (dd = dec * 0x01010101, bytes < 128)
{
    const uint32_t t = (L | 0x80808080u) - dd;                     // bit 7 survives where byte >= dec (dec < 128: no borrow between bytes)
    const uint32_t m = ((t >> 7) & 0x01010101u) * 0xFFu;
    return t & m & 0x7F7F7F7Fu;
}
static SR2D_INLINE uint32_t vox_max(uint32_t a, uint32_t b)        // per-byte max (bytes < 128)
{
    uint32_t ge = (((a | 0x80808080u) - b) >> 7) & 0x01010101u;    // 1 where a >= b
    uint32_t m = ge * 0xFFu;
    return (a & m) | (b & ~m);
}
static SR2D_INLINE uint32_t vox_emission(const SR2D_Voxel& v)
{
    if (!v.emit) return 0;
    const uint32_t e = (v.emit > 15 ? 15 : v.emit) * SR2D_VOXL_ONE;   // channel = colour * emit / 255, in 1/8 levels (max 120)
    const uint32_t r = ((v.argb >> 16) & 255) * e + 127, g = ((v.argb >> 8) & 255) * e + 127, b = (v.argb & 255) * e + 127;
    return ((r * 0x8081u) >> 23) << 16 | ((g * 0x8081u) >> 23) << 8 | ((b * 0x8081u) >> 23);
}

// The propagation is a max-plus distance transform (L = max(seed, max over the 6 neighbours of L - 1), solid
// cells keep their seed and block), whose fixed point is unique - so instead of the old queue BFS (a pointer chase
// with a division per cell) it runs as alternating forward / backward Gauss-Seidel row sweeps in 128-bit SIMD:
// saturating byte subtract = "-1 on all four channels", byte max = the merge, 4 cells per step, the in-row
// neighbour handled by a 3-step masked lane scan so light never jumps over a solid cell. Converges in a handful
// of sweeps (2 in open space, one more per obstacle turn, never more than 15 - the light range). Same code for
// both kernel levels: it is memory bound and 128-bit ops keep SSE2 / AVX2 bit-identical for free.
static SR2D_INLINE __m128i voxl_open_mask(const uint8_t* f)        // 4 face bytes -> dword lanes 0xffffffff where NOT solid
{
    __m128i v = _mm_cvtsi32_si128(*(const int32_t*)f);
    v = _mm_unpacklo_epi8(v, _mm_setzero_si128()); v = _mm_unpacklo_epi16(v, _mm_setzero_si128());
    return _mm_cmpeq_epi32(_mm_and_si128(v, _mm_set1_epi32(0x40)), _mm_setzero_si128());
}
static SR2D_INLINE __m128i voxl_sel(__m128i m, __m128i a, __m128i b) { return _mm_or_si128(_mm_and_si128(m, a), _mm_andnot_si128(m, b)); }

// one Gauss-Seidel step over a row; returns true when any cell changed
static bool voxl_row(uint32_t* row, const uint8_t* frow, const uint32_t* rym, const uint32_t* ryp, const uint32_t* rzm, const uint32_t* rzp, int gw, bool fwd, uint32_t dec)
{
    const __m128i one = _mm_set1_epi8((char)dec), zero = _mm_setzero_si128();
    const uint32_t dd = dec * 0x01010101u;
    const int gwv = gw & ~3;
    __m128i chg = zero; uint32_t chgs = 0;
    if (fwd)
    {
        __m128i carry = zero;                                       // lane 0 <- updated lane 3 of the previous group
        int x = 0;
        for (; x < gwv; x += 4)
        {
            const __m128i old = _mm_loadu_si128((const __m128i*)(row + x));
            const __m128i om = voxl_open_mask(frow + x);
            __m128i v = old;
            if (rym) v = _mm_max_epu8(v, _mm_subs_epu8(_mm_loadu_si128((const __m128i*)(rym + x)), one));
            if (ryp) v = _mm_max_epu8(v, _mm_subs_epu8(_mm_loadu_si128((const __m128i*)(ryp + x)), one));
            if (rzm) v = _mm_max_epu8(v, _mm_subs_epu8(_mm_loadu_si128((const __m128i*)(rzm + x)), one));
            if (rzp) v = _mm_max_epu8(v, _mm_subs_epu8(_mm_loadu_si128((const __m128i*)(rzp + x)), one));
            // x + 1 neighbour (old values): lanes 1..3 shifted down, lane 3 <- row[x + 4]
            __m128i nx = _mm_srli_si128(old, 4);
            if (x + 4 < gw) nx = _mm_or_si128(nx, _mm_slli_si128(_mm_cvtsi32_si128((int)row[x + 4]), 12));
            v = _mm_max_epu8(v, _mm_subs_epu8(nx, one));
            v = voxl_sel(om, v, old);
            // x - 1 neighbour: carry into lane 0, then three masked lane steps (blocked by solid lanes)
            v = voxl_sel(om, _mm_max_epu8(v, _mm_subs_epu8(carry, one)), v);
            v = voxl_sel(om, _mm_max_epu8(v, _mm_subs_epu8(_mm_slli_si128(v, 4), one)), v);
            v = voxl_sel(om, _mm_max_epu8(v, _mm_subs_epu8(_mm_slli_si128(v, 4), one)), v);
            v = voxl_sel(om, _mm_max_epu8(v, _mm_subs_epu8(_mm_slli_si128(v, 4), one)), v);
            _mm_storeu_si128((__m128i*)(row + x), v);
            chg = _mm_or_si128(chg, _mm_xor_si128(v, old));
            carry = _mm_srli_si128(v, 12);
        }
        uint32_t prev = x > 0 ? row[x - 1] : 0;
        for (; x < gw; ++x)
        {
            const uint32_t old = row[x]; uint32_t v = old;
            if (!(frow[x] & 0x40))
            {
                v = vox_max(v, vox_sub(prev, dd));
                if (x + 1 < gw) v = vox_max(v, vox_sub(row[x + 1], dd));
                if (rym) v = vox_max(v, vox_sub(rym[x], dd));
                if (ryp) v = vox_max(v, vox_sub(ryp[x], dd));
                if (rzm) v = vox_max(v, vox_sub(rzm[x], dd));
                if (rzp) v = vox_max(v, vox_sub(rzp[x], dd));
                row[x] = v;
            }
            chgs |= v ^ old; prev = v;
        }
    }
    else
    {
        // mirrored: scalar tail first (from the row end down), then groups from high x to low
        uint32_t prev = 0;                                          // updated value of cell x + 1
        for (int x = gw - 1; x >= gwv; --x)
        {
            const uint32_t old = row[x]; uint32_t v = old;
            if (!(frow[x] & 0x40))
            {
                v = vox_max(v, vox_sub(prev, dd));
                if (x > 0) v = vox_max(v, vox_sub(row[x - 1], dd));
                if (rym) v = vox_max(v, vox_sub(rym[x], dd));
                if (ryp) v = vox_max(v, vox_sub(ryp[x], dd));
                if (rzm) v = vox_max(v, vox_sub(rzm[x], dd));
                if (rzp) v = vox_max(v, vox_sub(rzp[x], dd));
                row[x] = v;
            }
            chgs |= v ^ old; prev = v;
        }
        __m128i carry = _mm_slli_si128(_mm_cvtsi32_si128((int)prev), 12);   // lane 3 <- updated lane 0 of the group above
        for (int x = gwv - 4; x >= 0; x -= 4)
        {
            const __m128i old = _mm_loadu_si128((const __m128i*)(row + x));
            const __m128i om = voxl_open_mask(frow + x);
            __m128i v = old;
            if (rym) v = _mm_max_epu8(v, _mm_subs_epu8(_mm_loadu_si128((const __m128i*)(rym + x)), one));
            if (ryp) v = _mm_max_epu8(v, _mm_subs_epu8(_mm_loadu_si128((const __m128i*)(ryp + x)), one));
            if (rzm) v = _mm_max_epu8(v, _mm_subs_epu8(_mm_loadu_si128((const __m128i*)(rzm + x)), one));
            if (rzp) v = _mm_max_epu8(v, _mm_subs_epu8(_mm_loadu_si128((const __m128i*)(rzp + x)), one));
            // x - 1 neighbour (old values): lanes 0..2 shifted up, lane 0 <- row[x - 1]
            __m128i px = _mm_slli_si128(old, 4);
            if (x > 0) px = _mm_or_si128(px, _mm_cvtsi32_si128((int)row[x - 1]));
            v = _mm_max_epu8(v, _mm_subs_epu8(px, one));
            v = voxl_sel(om, v, old);
            // x + 1 neighbour: carry into lane 3, then three masked lane steps downwards
            v = voxl_sel(om, _mm_max_epu8(v, _mm_subs_epu8(carry, one)), v);
            v = voxl_sel(om, _mm_max_epu8(v, _mm_subs_epu8(_mm_srli_si128(v, 4), one)), v);
            v = voxl_sel(om, _mm_max_epu8(v, _mm_subs_epu8(_mm_srli_si128(v, 4), one)), v);
            v = voxl_sel(om, _mm_max_epu8(v, _mm_subs_epu8(_mm_srli_si128(v, 4), one)), v);
            _mm_storeu_si128((__m128i*)(row + x), v);
            chg = _mm_or_si128(chg, _mm_xor_si128(v, old));
            carry = _mm_slli_si128(v, 12);
        }
    }
    return chgs != 0 || _mm_movemask_epi8(_mm_cmpeq_epi32(chg, zero)) != 0xffff;
}

static int VOXEL_LIGHT(const void* voxp, const uint8_t* faces, int gw, int gh, int gd, uint32_t* light, int skyLevel, int flags)
{
    const SR2D_Voxel* vox = (const SR2D_Voxel*)voxp;
    if (!vox || !faces || !light || gw <= 0 || gh <= 0 || gd <= 0) return 0;
    const long long cellsll = (long long)gw * gh * gd;
    if (cellsll > 0x7fffffbf) return 0;
    const int slab = gw * gh, rows = gh * gd;
    skyLevel = skyLevel < 0 ? 0 : skyLevel > 15 ? 15 : skyLevel;
    uint32_t dec = (uint32_t)(flags >> 8) & 255;                    // level loss per cell in 1/8 levels; 0 = default (one level)
    if (dec == 0) dec = SR2D_VOXL_ONE; else if (dec > SR2D_VOXL_MAXL) dec = SR2D_VOXL_MAXL;

    // scratch: open-column flags (one slab) + two row-dirty maps (padded by one row on each side, no edge tests)
    size_t got = 0;
    uint8_t* mem = (uint8_t*)sr2d_scratch_acquire((size_t)slab + 2 * ((size_t)rows + 2 * gh + 2) + 64, &got);
    if (!mem) return -1;
    uint8_t* open = mem;
    uint8_t* dirtyA = mem + slab + gh + 1;                          // dirtyA[r] valid for r in [-gh-1, rows+gh]
    uint8_t* dirtyB = dirtyA + rows + 2 * gh + 2;
    memset(mem, 0, (size_t)slab + 2 * ((size_t)rows + 2 * gh + 2));

    // 1) seeds: emitters (any cell with emit > 0, solid or not) and sky columns, top slab down
    for (int i = 0; i < slab; ++i) open[i] = 1;
    const uint32_t skyL = (uint32_t)skyLevel * SR2D_VOXL_ONE;
    const uint32_t sky = skyL << 24, sideSky = skyL > dec ? (skyL - dec) << 24 : 0;
    const bool sides = (flags & 1) && skyLevel > 0;
    for (int z = gd - 1; z >= 0; --z)
        for (int y = 0; y < gh; ++y)
        {
            const int base = z * slab + y * gw;
            const bool edgeRow = sides && (y == 0 || y == gh - 1);
            for (int x = 0; x < gw; ++x)
            {
                const int i = base + x;
                uint32_t L = vox_emission(vox[i]);
                if (faces[i] & 0x40) open[y * gw + x] = 0;
                else
                {
                    if (open[y * gw + x]) L |= sky;
                    if (edgeRow || (sides && (x == 0 || x == gw - 1))) L = vox_max(L, sideSky);
                }
                light[i] = L;
            }
        }

    // 2) alternating sweeps over the rows; a row is revisited only while it or a neighbouring row still changes
    //    (prev = changes of the previous sweep, cur = changes earlier in this sweep, i.e. Gauss-Seidel order)
    uint8_t* prev = dirtyA; uint8_t* cur = dirtyB;
    for (int r = 0; r < rows; ++r) prev[r] = 1;
    for (int sweep = 0; sweep < 256; ++sweep)
    {
        const bool fwd = (sweep & 1) == 0;
        bool any = false;
        for (int zz = 0; zz < gd; ++zz)
            for (int yy = 0; yy < gh; ++yy)
            {
                const int z = fwd ? zz : gd - 1 - zz, y = fwd ? yy : gh - 1 - yy, r = z * gh + y;
                if (!(prev[r] | prev[r - 1] | prev[r + 1] | prev[r - gh] | prev[r + gh] | cur[r - 1] | cur[r + 1] | cur[r - gh] | cur[r + gh])) continue;
                uint32_t* row = light + (size_t)r * gw;
                const bool ch = voxl_row(row, faces + (size_t)r * gw, y > 0 ? row - gw : 0, y + 1 < gh ? row + gw : 0, z > 0 ? row - slab : 0, z + 1 < gd ? row + slab : 0, gw, fwd, dec);
                cur[r] = ch; any |= ch;
            }
        if (!any) break;
        uint8_t* t = prev; prev = cur; cur = t;
        memset(cur, 0, (size_t)rows);
    }
    sr2d_scratch_release(mem, got);

    // 3) lit cell count (the return value)
    int count = 0;
    {
        const __m128i zero = _mm_setzero_si128();
        const int cells = (int)cellsll; int i = 0;
        for (; i + 4 <= cells; i += 4) count += 4 - popcnt32((uint32_t)_mm_movemask_ps(_mm_castsi128_ps(_mm_cmpeq_epi32(_mm_loadu_si128((const __m128i*)(light + i)), zero))));
        for (; i < cells; ++i) count += light[i] != 0;
    }
    return count;
}

// ---- VOXEL_RENDER -----------------------------------------------------------------------
struct VoxCtx
{
    const SR2D_VoxelScene* S;
    int* dst; int dw; int clipL, clipT, clipR, clipB; int* pick;
    int gw, gh, gd, slab; long long cells; int pickShift;
    int visMask;            // face bits that face the camera
    float wpt[6];           // point-mode weights per face (|view . n| normalised)
    float shade[6], ao[4], lut[16], lamp[16], tintR, tintG, tintB;
    float hx, hy;           // projection of (0.5, 0.5, 0.5) - the voxel centre offset
    float ex0, ex1, ey0, ey1;   // cube footprint relative to proj(x, y, z) (with slack)
    uint32_t outside;
    int drawn;
    // depth fade (SR2D_VOX_FADE_*): factor = fadeMin + (1 - fadeMin) * t^gamma, t = 1 near / top .. 0 far / bottom
    int fadeMode;           // 0 none, else the flag bits
    float fzA, fzB;         // t_z = fzA * z + fzB
    float fvA[3], fvB;      // t_v = fvA . (x, y, z) + fvB
    float fadeMin, fadeGamma, fogR, fogG, fogB;
};

// per-voxel fade factor (cheap: two dot products; the pow only when gamma != 1)
static SR2D_INLINE float vox_fade(const VoxCtx& C, int x, int y, int z)
{
    float t = 1.f;
    if (C.fadeMode & SR2D_VOX_FADE_Z) t *= vox_clampf(C.fzA * (float)z + C.fzB, 0.f, 1.f);
    if (C.fadeMode & SR2D_VOX_FADE_VIEW) t *= vox_clampf(C.fvA[0] * (float)x + C.fvA[1] * (float)y + C.fvA[2] * (float)z + C.fvB, 0.f, 1.f);
    if (C.fadeGamma != 1.f) t = vox_powf(t, C.fadeGamma);
    return t;
}
// apply the fade to a final colour: towards black, or towards the fog colour
static SR2D_INLINE uint32_t vox_fade_col(const VoxCtx& C, uint32_t c, float t)
{
    if (t >= 1.f) return c;
    float r = (float)((c >> 16) & 255), g = (float)((c >> 8) & 255), b = (float)(c & 255);
    if (C.fadeMode & SR2D_VOX_FADE_COLOR) { const float u = 1.f - t; r = r * t + C.fogR * u; g = g * t + C.fogG * u; b = b * t + C.fogB * u; }
    else { r *= t; g *= t; b *= t; }
    int ri = (int)(r + 0.5f), gi = (int)(g + 0.5f), bi = (int)(b + 0.5f);
    ri = ri > 255 ? 255 : ri; gi = gi > 255 ? 255 : gi; bi = bi > 255 ? 255 : bi;
    return (c & 0xff000000u) | ((uint32_t)ri << 16) | ((uint32_t)gi << 8) | (uint32_t)bi;
}

static SR2D_INLINE void vox_proj(const SR2D_VoxelScene& S, int x, int y, int z, float& sx, float& sy)
{
    const float fx = (float)x, fy = (float)y, fz = (float)z;
    sx = ((S.m[0] * fx + S.m[1] * fy) + S.m[2] * fz) + S.ox;
    sy = ((S.m[3] * fx + S.m[4] * fy) + S.m[5] * fz) + S.oy;
}
static SR2D_INLINE bool vox_inside(const VoxCtx& C, int x, int y, int z)
{ return (unsigned)x < (unsigned)C.gw && (unsigned)y < (unsigned)C.gh && (unsigned)z < (unsigned)C.gd; }
static SR2D_INLINE bool vox_solid(const VoxCtx& C, int x, int y, int z)
{ return vox_inside(C, x, y, z) && (C.S->faces[((size_t)z * C.gh + y) * C.gw + x] & 0x40); }
static SR2D_INLINE uint32_t vox_light_at(const VoxCtx& C, int x, int y, int z)
{ return vox_inside(C, x, y, z) ? (C.S->light[((size_t)z * C.gh + y) * C.gw + x] & SR2D_VOXL_MASK) : C.outside; }
static SR2D_INLINE float vox_lut(const float* lut, float v)
{
    v = vox_clampf(v, 0.f, 15.f);
    const int i = (int)v; const float t = v - (float)i;
    return i >= 15 ? lut[15] : lut[i] + (lut[i + 1] - lut[i]) * t;
}
// brightness triple from light levels (floats 0..15) for a face
static SR2D_INLINE void vox_bright(const VoxCtx& C, int f, float sky, float r, float g, float b, float k, float* out)
{
    const float ls = vox_lut(C.lut, sky);
    float br = ls * C.tintR, bg = ls * C.tintG, bb = ls * C.tintB;
    const float er = vox_lut(C.lamp, r), eg = vox_lut(C.lamp, g), eb = vox_lut(C.lamp, b);
    if (er > br) br = er;
    if (eg > bg) bg = eg;
    if (eb > bb) bb = eb;
    const float s = C.shade[f] * k;
    out[0] = br * s; out[1] = bg * s; out[2] = bb * s;
}
static SR2D_INLINE uint32_t vox_mulcol(uint32_t c, const float* B, uint32_t a)
{
    int r = (int)(((c >> 16) & 255) * B[0] + 0.5f), g = (int)(((c >> 8) & 255) * B[1] + 0.5f), b = (int)((c & 255) * B[2] + 0.5f);
    r = r > 255 ? 255 : r; g = g > 255 ? 255 : g; b = b > 255 ? 255 : b;
    return a | ((uint32_t)r << 16) | ((uint32_t)g << 8) | (uint32_t)b;
}
// lighting tier 2: light of the cell in front of face f (or own emission when brighter)
static SR2D_INLINE void vox_face_bright2(const VoxCtx& C, int x, int y, int z, int f, uint32_t own, float* B)
{
    const VoxFaceDef& F = vox_facedef(f);
    const uint32_t L = vox_max(vox_light_at(C, x + F.n[0], y + F.n[1], z + F.n[2]), own);
    const float q = 1.f / (float)SR2D_VOXL_ONE;
    vox_bright(C, f, (float)(L >> 24) * q, (float)((L >> 16) & 127) * q, (float)((L >> 8) & 127) * q, (float)(L & 127) * q, 1.f, B);
}
// lighting tier 3: brightness at face corner (a, b) from the 2 x 2 cells in front + AO
static void vox_vertex_bright(const VoxCtx& C, int x, int y, int z, int f, int a, int b, uint32_t own, float* B)
{
    const VoxFaceDef& F = vox_facedef(f);
    const int fx = x + F.n[0], fy = y + F.n[1], fz = z + F.n[2];
    const int su = a ? 1 : -1, sw = b ? 1 : -1;
    float sky = 0, r = 0, g = 0, bl = 0; int cnt = 0;
    for (int j = 0; j < 2; ++j)
        for (int i = 0; i < 2; ++i)
        {
            const int du = (i ? su : 0), dv = (j ? sw : 0);
            const int cx = fx + du * F.u[0] + dv * F.w[0], cy = fy + du * F.u[1] + dv * F.w[1], cz = fz + du * F.u[2] + dv * F.w[2];
            uint32_t L;
            if (!vox_inside(C, cx, cy, cz)) L = C.outside;
            else
            {
                const size_t idx = ((size_t)cz * C.gh + cy) * C.gw + cx;
                L = C.S->light[idx] & SR2D_VOXL_MASK;
                if ((C.S->faces[idx] & 0x40) && !L) continue;     // dark solid: occluder, no light
            }
            if (i == 0 && j == 0) L = vox_max(L, own);
            sky += (float)(L >> 24); r += (float)((L >> 16) & 127); g += (float)((L >> 8) & 127); bl += (float)(L & 127);
            ++cnt;
        }
    const float inv = cnt ? 1.f / ((float)cnt * (float)SR2D_VOXL_ONE) : 0.f;
    const bool s1 = vox_solid(C, fx + su * F.u[0], fy + su * F.u[1], fz + su * F.u[2]);
    const bool s2 = vox_solid(C, fx + sw * F.w[0], fy + sw * F.w[1], fz + sw * F.w[2]);
    int occ;
    if (s1 && s2) occ = 3;
    else occ = (int)s1 + (int)s2 + (int)vox_solid(C, fx + su * F.u[0] + sw * F.w[0], fy + su * F.u[1] + sw * F.w[1], fz + su * F.u[2] + sw * F.w[2]);
    vox_bright(C, f, sky * inv, r * inv, g * inv, bl * inv, C.ao[occ], B);
}

// parallelogram P(a, b) = V00 + a U + b W, rasterised with the pixel-centre rule
struct VoxQuad { float x[4], y[4]; };   // 0: (0,0) 1: (1,0) 2: (1,1) 3: (0,1)

static void vox_face_quad(const VoxCtx& C, int x, int y, int z, int f, VoxQuad& q)
{
    const VoxFaceDef& F = vox_facedef(f);
    const int bx = x + F.c0[0], by = y + F.c0[1], bz = z + F.c0[2];
    vox_proj(*C.S, bx, by, bz, q.x[0], q.y[0]);
    vox_proj(*C.S, bx + F.u[0], by + F.u[1], bz + F.u[2], q.x[1], q.y[1]);
    vox_proj(*C.S, bx + F.u[0] + F.w[0], by + F.u[1] + F.w[1], bz + F.u[2] + F.w[2], q.x[2], q.y[2]);
    vox_proj(*C.S, bx + F.w[0], by + F.w[1], bz + F.w[2], q.x[3], q.y[3]);
}

// one scanline of the quad: [xl, xr) at pixel-centre yc; false when the row misses
static SR2D_INLINE bool vox_span(const VoxQuad& q, float yc, float& xl, float& xr)
{
    static const int E[4][2] = { { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 } };
    xl = 1e30f; xr = -1e30f; int hits = 0;
    for (int e = 0; e < 4; ++e)
    {
        int i0 = E[e][0], i1 = E[e][1];
        // canonical endpoint order (lower y first, then lower x) -> shared edges give identical x
        if (q.y[i1] < q.y[i0] || (q.y[i1] == q.y[i0] && q.x[i1] < q.x[i0])) { int t = i0; i0 = i1; i1 = t; }
        const float y0 = q.y[i0], y1 = q.y[i1];
        if (!(y0 <= yc && yc < y1)) continue;
        const float xx = q.x[i0] + (yc - y0) * ((q.x[i1] - q.x[i0]) / (y1 - y0));
        if (xx < xl) xl = xx;
        if (xx > xr) xr = xx;
        ++hits;
    }
    return hits >= 2;
}

// flat-shaded face
static void vox_fill_face(VoxCtx& C, const VoxQuad& q, uint32_t col, int pickv)
{
    float ymin = q.y[0], ymax = q.y[0];
    for (int i = 1; i < 4; ++i) { ymin = q.y[i] < ymin ? q.y[i] : ymin; ymax = q.y[i] > ymax ? q.y[i] : ymax; }
    int y0 = vox_ceil(ymin - 0.5f), y1 = vox_ceil(ymax - 0.5f);           // rows [y0, y1)
    y0 = y0 < C.clipT ? C.clipT : y0; y1 = y1 > C.clipB ? C.clipB : y1;
    for (int y = y0; y < y1; ++y)
    {
        float xl, xr;
        if (!vox_span(q, (float)y + 0.5f, xl, xr)) continue;
        int x0 = vox_ceil(xl - 0.5f), x1 = vox_ceil(xr - 0.5f);
        x0 = x0 < C.clipL ? C.clipL : x0; x1 = x1 > C.clipR ? C.clipR : x1;
        if (x0 >= x1) continue;
        int* d = C.dst + (size_t)y * C.dw;
        for (int x = x0; x < x1; ++x) d[x] = (int)col;
        if (C.pick) { int* p = C.pick + (size_t)y * C.dw; for (int x = x0; x < x1; ++x) p[x] = pickv; }
    }
}

// Gouraud face: B[4][3] brightness at corners (0,0) (1,0) (1,1) (0,1)
static void vox_fill_face_smooth(VoxCtx& C, const VoxQuad& q, uint32_t col, uint32_t a, const float B[4][3], int pickv, float fade)
{
    float ymin = q.y[0], ymax = q.y[0];
    for (int i = 1; i < 4; ++i) { ymin = q.y[i] < ymin ? q.y[i] : ymin; ymax = q.y[i] > ymax ? q.y[i] : ymax; }
    int y0 = vox_ceil(ymin - 0.5f), y1 = vox_ceil(ymax - 0.5f);
    y0 = y0 < C.clipT ? C.clipT : y0; y1 = y1 > C.clipB ? C.clipB : y1;
    if (y0 >= y1) return;
    // inverse of [U W] : (a, b) from screen offset
    const float ux = q.x[1] - q.x[0], uy = q.y[1] - q.y[0], wx = q.x[3] - q.x[0], wy = q.y[3] - q.y[0];
    const float det = ux * wy - uy * wx;
    if (det == 0.f) return;
    const float id = 1.f / det;
    const float iax = wy * id, iay = -wx * id, ibx = -uy * id, iby = ux * id;   // a = iax dx + iay dy, b = ibx dx + iby dy
    const float cr = (float)((col >> 16) & 255), cg = (float)((col >> 8) & 255), cb = (float)(col & 255);
    for (int y = y0; y < y1; ++y)
    {
        const float yc = (float)y + 0.5f;
        float xl, xr;
        if (!vox_span(q, yc, xl, xr)) continue;
        int x0 = vox_ceil(xl - 0.5f), x1 = vox_ceil(xr - 0.5f);
        x0 = x0 < C.clipL ? C.clipL : x0; x1 = x1 > C.clipR ? C.clipR : x1;
        if (x0 >= x1) continue;
        int* d = C.dst + (size_t)y * C.dw;
        const float dy = yc - q.y[0];
        float dx = (float)x0 + 0.5f - q.x[0];
        float pa = iax * dx + iay * dy, pb = ibx * dx + iby * dy;
        for (int x = x0; x < x1; ++x, pa += iax, pb += ibx)
        {
            const float ta = vox_clampf(pa, 0.f, 1.f), tb = vox_clampf(pb, 0.f, 1.f);
            const float w00 = (1.f - ta) * (1.f - tb), w10 = ta * (1.f - tb), w11 = ta * tb, w01 = (1.f - ta) * tb;
            const float br = B[0][0] * w00 + B[1][0] * w10 + B[2][0] * w11 + B[3][0] * w01;
            const float bg = B[0][1] * w00 + B[1][1] * w10 + B[2][1] * w11 + B[3][1] * w01;
            const float bb = B[0][2] * w00 + B[1][2] * w10 + B[2][2] * w11 + B[3][2] * w01;
            int r = (int)(cr * br + 0.5f), g = (int)(cg * bg + 0.5f), b = (int)(cb * bb + 0.5f);
            r = r > 255 ? 255 : r; g = g > 255 ? 255 : g; b = b > 255 ? 255 : b;
            uint32_t pc = a | ((uint32_t)r << 16) | ((uint32_t)g << 8) | (uint32_t)b;
            if (fade < 1.f) pc = vox_fade_col(C, pc, fade);
            d[x] = (int)pc;
        }
        if (C.pick) { int* p = C.pick + (size_t)y * C.dw; for (int x = x0; x < x1; ++x) p[x] = pickv; }
    }
}

static void vox_draw_voxel(VoxCtx& C, int x, int y, int z, int idx, int fbits)
{
    const SR2D_VoxelScene& S = *C.S;
    const SR2D_Voxel& v = S.vox[idx];
    const uint32_t a = (S.flags & SR2D_VOX_KEEP_ALPHA) ? (v.argb & 0xff000000u) : 0xff000000u;
    const uint32_t own = (S.lighting >= 2) ? (S.light[idx] & SR2D_VOXL_MASK) : 0;
    const int lighting = S.lighting;
    ++C.drawn;

    if (S.mode == 0)
    {
        // one pixel: blend of the visible exposed faces
        float sx, sy; vox_proj(S, x, y, z, sx, sy);
        const int px = vox_floor(sx + C.hx), py = vox_floor(sy + C.hy);
        if (px < C.clipL || px >= C.clipR || py < C.clipT || py >= C.clipB) return;
        float B[3] = { 0, 0, 0 }, wsum = 0; int fbest = -1; float wbest = -1;
        for (int f = 0; f < 6; ++f)
        {
            if (!(fbits & (1 << f))) continue;
            const float w = C.wpt[f];
            if (w > wbest) { wbest = w; fbest = f; }
            float Bf[3];
            if (lighting <= 0) { Bf[0] = Bf[1] = Bf[2] = 1.f; }
            else if (lighting == 1) { Bf[0] = Bf[1] = Bf[2] = C.shade[f]; }
            else if (lighting == 2) vox_face_bright2(C, x, y, z, f, own, Bf);
            else
            {
                float Bv[3]; Bf[0] = Bf[1] = Bf[2] = 0.f;
                for (int c = 0; c < 4; ++c)
                {
                    vox_vertex_bright(C, x, y, z, f, c == 1 || c == 2, c >= 2, own, Bv);
                    Bf[0] += Bv[0] * 0.25f; Bf[1] += Bv[1] * 0.25f; Bf[2] += Bv[2] * 0.25f;
                }
            }
            B[0] += Bf[0] * w; B[1] += Bf[1] * w; B[2] += Bf[2] * w; wsum += w;
        }
        if (wsum <= 0.f) return;
        const float inv = 1.f / wsum; B[0] *= inv; B[1] *= inv; B[2] *= inv;
        uint32_t pc = vox_mulcol(v.argb, B, a);
        if (C.fadeMode) pc = vox_fade_col(C, pc, vox_fade(C, x, y, z));
        C.dst[(size_t)py * C.dw + px] = (int)pc;
        if (C.pick) C.pick[(size_t)py * C.dw + px] = (idx << C.pickShift) | (C.pickShift ? fbest : 0);
        return;
    }

    const float fade = C.fadeMode ? vox_fade(C, x, y, z) : 1.f;
    for (int f = 0; f < 6; ++f)
    {
        if (!(fbits & (1 << f))) continue;
        VoxQuad q; vox_face_quad(C, x, y, z, f, q);
        const int pickv = (idx << C.pickShift) | (C.pickShift ? f : 0);
        if (lighting >= 3)
        {
            float B[4][3];
            vox_vertex_bright(C, x, y, z, f, 0, 0, own, B[0]);
            vox_vertex_bright(C, x, y, z, f, 1, 0, own, B[1]);
            vox_vertex_bright(C, x, y, z, f, 1, 1, own, B[2]);
            vox_vertex_bright(C, x, y, z, f, 0, 1, own, B[3]);
            vox_fill_face_smooth(C, q, v.argb, a, B, pickv, fade);
        }
        else
        {
            float B[3];
            if (lighting <= 0) { B[0] = B[1] = B[2] = 1.f; }
            else if (lighting == 1) { B[0] = B[1] = B[2] = C.shade[f]; }
            else vox_face_bright2(C, x, y, z, f, own, B);
            uint32_t pc = vox_mulcol(v.argb, B, a);
            if (fade < 1.f) pc = vox_fade_col(C, pc, fade);
            vox_fill_face(C, q, pc, pickv);
        }
    }
}

// x range [x0, x1] of a row (n cells) whose footprint touches [lo, hi) given s(x) = m x + c + [e0, e1]
static SR2D_INLINE bool vox_range(float m, float c, float e0, float e1, float lo, float hi, int n, int& x0, int& x1)
{
    // need m x + c + e1 >= lo  and  m x + c + e0 < hi
    if (m > -1e-9f && m < 1e-9f) { return c + e1 >= lo && c + e0 < hi ? (x0 = 0, x1 = n - 1, true) : false; }
    float a = (lo - c - e1) / m, b = (hi - c - e0) / m;
    if (a > b) { float t = a; a = b; b = t; }
    a = vox_clampf(a, -1.f, (float)n + 1.f); b = vox_clampf(b, -1.f, (float)n + 1.f);
    int lo_ = vox_floor(a), hi_ = vox_ceil(b);
    lo_ = lo_ < 0 ? 0 : lo_; hi_ = hi_ > n - 1 ? n - 1 : hi_;
    x0 = lo_; x1 = hi_;
    return lo_ <= hi_;
}

static int VOXEL_RENDER(const SR2D_VoxelScene* scene, int* dst, int dw, int clipL, int clipT, int clipR, int clipB, int* pick)
{
    if (!scene || !dst || !scene->vox || !scene->faces) return 0;
    const SR2D_VoxelScene& S = *scene;
    if (S.gw <= 0 || S.gh <= 0 || S.gd <= 0 || clipL >= clipR || clipT >= clipB) return 0;
    const long long cells = (long long)S.gw * S.gh * S.gd;
    if (cells > 0x7fffffff) return 0;
    for (int i = 0; i < 6; ++i) if (!(S.m[i] > -1e9f && S.m[i] < 1e9f)) return 0;   // NaN / absurd
    if (!(S.ox > -1e9f && S.ox < 1e9f) || !(S.oy > -1e9f && S.oy < 1e9f)) return 0;

    VoxCtx C;
    C.S = &S; C.dst = dst; C.dw = dw; C.clipL = clipL; C.clipT = clipT; C.clipR = clipR; C.clipB = clipB; C.pick = pick;
    C.gw = S.gw; C.gh = S.gh; C.gd = S.gd; C.slab = S.gw * S.gh; C.cells = cells; C.pickShift = cells < (1 << 28) ? 3 : 0;
    C.drawn = 0; C.outside = S.outside & SR2D_VOXL_MASK;
    int lighting = S.lighting < 0 ? 0 : S.lighting > 3 ? 3 : S.lighting;
    if (lighting >= 2 && !S.light) lighting = 1;
    SR2D_VoxelScene Sl = S; Sl.lighting = lighting; Sl.mode = S.mode ? 1 : 0; C.S = &Sl;
    for (int i = 0; i < 6; ++i) C.shade[i] = vox_clampf(S.shade[i], 0.f, 8.f);
    for (int i = 0; i < 4; ++i) C.ao[i] = vox_clampf(S.ao[i], 0.f, 8.f);
    for (int i = 0; i < 16; ++i) C.lut[i] = vox_clampf(S.lut[i], 0.f, 8.f);
    for (int i = 0; i < 16; ++i) C.lamp[i] = S.lampLut ? vox_clampf(S.lampLut[i], 0.f, 8.f) : C.lut[i];   // v3: separate lamp curve
    C.tintR = (float)((S.skyColor >> 16) & 255) * (1.f / 255.f);
    C.tintG = (float)((S.skyColor >> 8) & 255) * (1.f / 255.f);
    C.tintB = (float)(S.skyColor & 255) * (1.f / 255.f);

    // visible faces + point weights from the view direction
    float v[3] = { vox_clampf(S.view[0], -1e6f, 1e6f), vox_clampf(S.view[1], -1e6f, 1e6f), vox_clampf(S.view[2], -1e6f, 1e6f) };
    float vl = v[0] * v[0] + v[1] * v[1] + v[2] * v[2];
    if (!(vl > 0.f)) return 0;
    C.visMask = 0;
    for (int f = 0; f < 6; ++f)
    {
        const VoxFaceDef& F = vox_facedef(f);
        const float d = v[0] * F.n[0] + v[1] * F.n[1] + v[2] * F.n[2];
        C.wpt[f] = d < 0.f ? -d : 0.f;
        if (d < -1e-6f * vl) C.visMask |= 1 << f;
    }
    if (!C.visMask) return 0;

    // footprint of a cube relative to proj(x, y, z), plus the point-mode centre offset
    C.hx = 0.5f * (S.m[0] + S.m[1] + S.m[2]); C.hy = 0.5f * (S.m[3] + S.m[4] + S.m[5]);
    C.ex0 = C.ex1 = C.ey0 = C.ey1 = 0.f;
    if (S.mode)
    {
        for (int c = 1; c < 8; ++c)
        {
            const float px = (c & 1 ? S.m[0] : 0.f) + (c & 2 ? S.m[1] : 0.f) + (c & 4 ? S.m[2] : 0.f);
            const float py = (c & 1 ? S.m[3] : 0.f) + (c & 2 ? S.m[4] : 0.f) + (c & 4 ? S.m[5] : 0.f);
            C.ex0 = px < C.ex0 ? px : C.ex0; C.ex1 = px > C.ex1 ? px : C.ex1;
            C.ey0 = py < C.ey0 ? py : C.ey0; C.ey1 = py > C.ey1 ? py : C.ey1;
        }
    }
    else { C.ex0 = C.ey0 = -1.f; C.ex1 = C.ey1 = 1.f; C.ex0 += C.hx; C.ex1 += C.hx; C.ey0 += C.hy; C.ey1 += C.hy; }
    C.ex0 -= 1.f; C.ex1 += 1.f; C.ey0 -= 1.f; C.ey1 += 1.f;   // slack for rounding
    const float fL = (float)clipL, fT = (float)clipT, fR = (float)clipR, fB = (float)clipB;

    // walk order: far to near on every axis
    const int dx = v[0] > 0.f ? -1 : 1, dy = v[1] > 0.f ? -1 : 1, dz = v[2] > 0.f ? -1 : 1;
    const int vis = C.visMask;
    const T vmask = V::set1_8((char)vis), zero = V::zero();
    const int NB = N * 4;   // bytes per vector

    // depth fade
    C.fadeMode = S.flags & (SR2D_VOX_FADE_Z | SR2D_VOX_FADE_VIEW | SR2D_VOX_FADE_COLOR);
    if (!(C.fadeMode & (SR2D_VOX_FADE_Z | SR2D_VOX_FADE_VIEW))) C.fadeMode = 0;
    C.fadeMin = vox_clampf(S.fadeMin, 0.f, 1.f); C.fadeGamma = S.fadeGamma > 0.f ? S.fadeGamma : 1.f;
    C.fogR = (float)((S.fadeColor >> 16) & 255); C.fogG = (float)((S.fadeColor >> 8) & 255); C.fogB = (float)(S.fadeColor & 255);
    C.fzA = C.fzB = C.fvB = 0.f; C.fvA[0] = C.fvA[1] = C.fvA[2] = 0.f;
    if (C.fadeMode)
    {
        const float span = 1.f - C.fadeMin;
        if (C.fadeMode & SR2D_VOX_FADE_Z)
        {   // t = 1 at z = gd - 1, fadeMin at z = 0 (linear in z, gamma applied to the combined t)
            const float zs = S.gd > 1 ? 1.f / (float)(S.gd - 1) : 0.f;
            C.fzA = span * zs; C.fzB = C.fadeMin;
        }
        if (C.fadeMode & SR2D_VOX_FADE_VIEW)
        {   // depth = v . p (v normalised); nearest grid corner -> 1, farthest -> fadeMin
            const float il = 1.f / _mm_cvtss_f32(_mm_sqrt_ss(_mm_set_ss(vl)));
            const float n0 = v[0] * il, n1 = v[1] * il, n2 = v[2] * il;
            float dmin = 1e30f, dmax = -1e30f;
            for (int c = 0; c < 8; ++c)
            {
                const float dd = n0 * (c & 1 ? (float)S.gw : 0.f) + n1 * (c & 2 ? (float)S.gh : 0.f) + n2 * (c & 4 ? (float)S.gd : 0.f);
                dmin = dd < dmin ? dd : dmin; dmax = dd > dmax ? dd : dmax;
            }
            const float range = dmax - dmin > 1e-6f ? dmax - dmin : 1.f;
            // t = fadeMin + span * (dmax - depth) / range, depth of the voxel centre
            const float k = -span / range;
            C.fvA[0] = k * n0; C.fvA[1] = k * n1; C.fvA[2] = k * n2;
            C.fvB = C.fadeMin + span * dmax / range + k * 0.5f * (n0 + n1 + n2);
        }
    }

    // slab range (progressive rendering): [z0, z1) in grid z, walked in painter's order
    int zr0 = 0, zr1 = S.gd;
    if (S.zTo > 0) { zr0 = S.zFrom < 0 ? 0 : S.zFrom; zr1 = S.zTo > S.gd ? S.gd : S.zTo; if (zr0 >= zr1) return 0; }

    for (int zi = zr0; zi < zr1; ++zi)
    {
        const int z = dz > 0 ? zi : S.gd - 1 - zi;
        // slab bounding box test
        {
            float bx0 = 1e30f, bx1 = -1e30f, by0 = 1e30f, by1 = -1e30f;
            for (int c = 0; c < 4; ++c)
            {
                float sx, sy; vox_proj(S, c & 1 ? S.gw - 1 : 0, c & 2 ? S.gh - 1 : 0, z, sx, sy);
                bx0 = sx < bx0 ? sx : bx0; bx1 = sx > bx1 ? sx : bx1; by0 = sy < by0 ? sy : by0; by1 = sy > by1 ? sy : by1;
            }
            if (bx1 + C.ex1 < fL || bx0 + C.ex0 >= fR || by1 + C.ey1 < fT || by0 + C.ey0 >= fB) continue;
        }
        for (int yi = 0; yi < S.gh; ++yi)
        {
            const int y = dy > 0 ? yi : S.gh - 1 - yi;
            float cx, cy; vox_proj(S, 0, y, z, cx, cy);
            int ax0, ax1, bx0, bx1;
            if (!vox_range(S.m[0], cx, C.ex0, C.ex1, fL, fR, S.gw, ax0, ax1)) continue;
            if (!vox_range(S.m[3], cy, C.ey0, C.ey1, fT, fB, S.gw, bx0, bx1)) continue;
            const int x0 = ax0 > bx0 ? ax0 : bx0, x1 = ax1 < bx1 ? ax1 : bx1;
            if (x0 > x1) continue;
            const int rowIdx = (z * S.gh + y) * S.gw;
            const uint8_t* frow = S.faces + rowIdx;
            if (dx > 0)
            {
                int x = x0;
                while (x <= x1)
                {
                    if (x + NB <= x1 + 1)
                    {
                        uint32_t bits = ~(uint32_t)V::movemask8(V::cmpeq8(V::and_(V::loadu(frow + x), vmask), zero));
                        if (NB < 32) bits &= (1u << NB) - 1u;
                        if (!bits) { x += NB; continue; }
                        while (bits)
                        {
                            const int b = ctz32(bits); bits &= bits - 1u;
                            vox_draw_voxel(C, x + b, y, z, rowIdx + x + b, frow[x + b] & vis);
                        }
                        x += NB; continue;
                    }
                    const int fb = frow[x] & vis;
                    if (fb) vox_draw_voxel(C, x, y, z, rowIdx + x, fb);
                    ++x;
                }
            }
            else
            {
                int x = x1;
                while (x >= x0)
                {
                    if (x - NB + 1 >= x0)
                    {
                        const int xs = x - NB + 1;
                        uint32_t bits = ~(uint32_t)V::movemask8(V::cmpeq8(V::and_(V::loadu(frow + xs), vmask), zero));
                        if (NB < 32) bits &= (1u << NB) - 1u;
                        if (!bits) { x -= NB; continue; }
                        while (bits)
                        {
                            const int b = vox_msb(bits); bits &= ~(1u << b);
                            vox_draw_voxel(C, xs + b, y, z, rowIdx + xs + b, frow[xs + b] & vis);
                        }
                        x -= NB; continue;
                    }
                    const int fb = frow[x] & vis;
                    if (fb) vox_draw_voxel(C, x, y, z, rowIdx + x, fb);
                    --x;
                }
            }
        }
    }
    return C.drawn;
}

// ---- VOXEL_FLOOD ------------------------------------------------------------------------
struct VoxMatch
{
    uint32_t ref; int mat, tol; bool empty, byMat;
    VoxMatch(const SR2D_Voxel& v, int t, bool bm) : ref(v.argb), mat(v.mat), tol(t), empty((v.argb >> 24) == 0), byMat(bm) {}
    VoxMatch(uint32_t a, int m, int t, bool bm) : ref(a), mat(m), tol(t), empty((a >> 24) == 0), byMat(bm) {}
    SR2D_INLINE bool operator()(const SR2D_Voxel& v) const
    {
        const bool e = (v.argb >> 24) == 0;
        if (e != empty) return false;
        if (e) return true;
        if (byMat) return v.mat == mat;
        int m = 0;
        for (int sh = 0; sh < 32; sh += 8) { int d = (int)((v.argb >> sh) & 255) - (int)((ref >> sh) & 255); d = d < 0 ? -d : d; m = d > m ? d : m; }
        return m <= tol;
    }
};

static int VOXEL_FLOOD(const void* voxp, int gw, int gh, int gd, int x, int y, int z, uint32_t ref, int refMat, int tol, int flags, uint8_t* mask)
{
    const SR2D_Voxel* vox = (const SR2D_Voxel*)voxp;
    if (!vox || !mask || gw <= 0 || gh <= 0 || gd <= 0) return 0;
    const long long cellsll = (long long)gw * gh * gd;
    if (cellsll > 0x7fffffff) return 0;
    const int cells = (int)cellsll, slab = gw * gh;
    for (int i = 0; i < cells; ++i) mask[i] = 0;
    const bool outside = (flags & SR2D_VOXF_OUTSIDE) != 0, global = (flags & SR2D_VOXF_GLOBAL) != 0;
    const bool useRef = (flags & SR2D_VOXF_REF) != 0;
    // the seed position is needed for a plain flood (start cell) and, without REF, as the reference voxel
    // (also for GLOBAL / OUTSIDE); it must then lie inside the grid
    if ((!useRef || (!outside && !global)) && ((unsigned)x >= (unsigned)gw || (unsigned)y >= (unsigned)gh || (unsigned)z >= (unsigned)gd)) return 0;
    if (useRef && (outside || global)) { x = 0; y = 0; z = 0; }
    const VoxMatch M = useRef ? VoxMatch(ref, refMat, tol < 0 ? 0 : tol, (flags & SR2D_VOXF_MATERIAL) != 0)
                              : VoxMatch(vox[((size_t)z * gh + y) * gw + x], tol < 0 ? 0 : tol, (flags & SR2D_VOXF_MATERIAL) != 0);
    int count = 0;
    if (global)
    {
        for (int i = 0; i < cells; ++i) if (M(vox[i])) { mask[i] = 1; ++count; }
        return count;
    }
    size_t got = 0;
    int* queue = (int*)sr2d_scratch_acquire((size_t)cells * sizeof(int) + 64, &got);
    if (!queue) return -1;
    int head = 0, tail = 0;
    // a cell enters the queue exactly once (mask is the visited set), so a plain array suffices
    auto push = [&](int i) { if (!mask[i] && M(vox[i])) { mask[i] = 1; queue[tail++] = i; ++count; } };
    if (outside)
    {
        for (int zz = 0; zz < gd; ++zz) for (int yy = 0; yy < gh; ++yy)
        {
            const bool edgeYZ = yy == 0 || yy == gh - 1 || zz == 0 || zz == gd - 1;
            const int base = (zz * gh + yy) * gw;
            if (edgeYZ) { for (int xx = 0; xx < gw; ++xx) push(base + xx); }
            else { push(base); push(base + gw - 1); }
        }
    }
    else push(((size_t)z * gh + y) * gw + (size_t)x);
    const bool diag = (flags & SR2D_VOXF_DIAGONAL) != 0;
    const UDiv32 dslab = UDiv32::make((uint32_t)slab), dw = UDiv32::make((uint32_t)gw);
    while (head < tail)
    {
        const int i = queue[head++];
        const int cz = (int)dslab((uint32_t)i), rem = i - cz * slab, cy = (int)dw((uint32_t)rem), cx = rem - cy * gw;
        if (!diag)
        {
            if (cx + 1 < gw) push(i + 1);
            if (cx > 0)      push(i - 1);
            if (cy + 1 < gh) push(i + gw);
            if (cy > 0)      push(i - gw);
            if (cz + 1 < gd) push(i + slab);
            if (cz > 0)      push(i - slab);
        }
        else
        {
            for (int dz = -1; dz <= 1; ++dz)
            {
                const int nz = cz + dz; if ((unsigned)nz >= (unsigned)gd) continue;
                for (int dy = -1; dy <= 1; ++dy)
                {
                    const int ny = cy + dy; if ((unsigned)ny >= (unsigned)gh) continue;
                    const int rowb = (nz * gh + ny) * gw;
                    if (cx > 0) push(rowb + cx - 1);
                    if (dz | dy) push(rowb + cx);
                    if (cx + 1 < gw) push(rowb + cx + 1);
                }
            }
        }
    }
    sr2d_scratch_release(queue, got);
    return count;
}
