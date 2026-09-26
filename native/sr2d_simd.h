// SR2D - SIMD traits.  Thin wrappers so kernels can be written once and
// instantiated for SSE2 (128-bit) and AVX2 (256-bit).
//
// IMPORTANT: this header is included *inside* a per-flavour namespace by the
// kernel translation units. Nothing here may have external linkage, otherwise
// the linker could pick the AVX2 build of a helper for the SSE2 path.
#pragma once
// NOTE: no system includes here on purpose (see above). The including TU must
// have pulled in <immintrin.h>/<intrin.h> and <stdint.h> beforehand.

#if defined(_MSC_VER)
#  define SR2D_INLINE __forceinline
#else
#  define SR2D_INLINE inline __attribute__((always_inline))
#endif

// ---------------------------------------------------------------------------
// 128-bit helpers shared by both flavours (SSE2 baseline; SSE4.1 when available)
// ---------------------------------------------------------------------------
namespace x128
{
    static SR2D_INLINE __m128i mullo32(__m128i a, __m128i b)
    {
#if defined(__SSE4_1__) || defined(__AVX2__) || (defined(_MSC_VER) && defined(SR2D_HAVE_SSE41))
        return _mm_mullo_epi32(a, b);
#else
        __m128i e = _mm_mul_epu32(a, b);
        __m128i o = _mm_mul_epu32(_mm_srli_epi64(a, 32), _mm_srli_epi64(b, 32));
        return _mm_unpacklo_epi32(_mm_shuffle_epi32(e, _MM_SHUFFLE(0, 0, 2, 0)),
                                  _mm_shuffle_epi32(o, _MM_SHUFFLE(0, 0, 2, 0)));
#endif
    }
    // high 32 bits of unsigned 32x32 products, per lane
    static SR2D_INLINE __m128i mulhi_u32(__m128i a, __m128i b)
    {
        __m128i e = _mm_mul_epu32(a, b);
        __m128i o = _mm_mul_epu32(_mm_srli_epi64(a, 32), _mm_srli_epi64(b, 32));
        __m128i himask = _mm_set_epi32(-1, 0, -1, 0);
        return _mm_or_si128(_mm_srli_epi64(e, 32), _mm_and_si128(o, himask));
    }
    // zero-extend low 4 bytes to 4 dwords
    static SR2D_INLINE __m128i u8x4_to_u32(uint32_t pixel)
    {
        __m128i z = _mm_setzero_si128();
        __m128i v = _mm_cvtsi32_si128((int)pixel);
        return _mm_unpacklo_epi16(_mm_unpacklo_epi8(v, z), z);
    }
    static SR2D_INLINE uint32_t hsum32(__m128i v)
    {
        v = _mm_add_epi32(v, _mm_shuffle_epi32(v, _MM_SHUFFLE(1, 0, 3, 2)));
        v = _mm_add_epi32(v, _mm_shuffle_epi32(v, _MM_SHUFFLE(2, 3, 0, 1)));
        return (uint32_t)_mm_cvtsi128_si32(v);
    }
    // 4 dword lanes (each <= 255) -> packed BGRA dword
    static SR2D_INLINE uint32_t pack_u32x4(__m128i v)
    {
        __m128i p = _mm_packus_epi16(_mm_packs_epi32(v, v), v);
        return (uint32_t)_mm_cvtsi128_si32(p);
    }
}

// ---------------------------------------------------------------------------
// SSE2 flavour
// ---------------------------------------------------------------------------
struct VSSE2
{
    typedef __m128i T;
    enum { N = 4 };            // dword lanes
    enum { LEVEL = 1 };

    static SR2D_INLINE T loadu(const void* p)      { return _mm_loadu_si128((const __m128i*)p); }
    static SR2D_INLINE void storeu(void* p, T v)   { _mm_storeu_si128((__m128i*)p, v); }
    static SR2D_INLINE T set1_32(int v)            { return _mm_set1_epi32(v); }
    static SR2D_INLINE T set1_16(short v)          { return _mm_set1_epi16(v); }
    static SR2D_INLINE T set1_8(char v)            { return _mm_set1_epi8(v); }
    static SR2D_INLINE T zero()                    { return _mm_setzero_si128(); }

    static SR2D_INLINE T and_(T a, T b)            { return _mm_and_si128(a, b); }
    static SR2D_INLINE T or_(T a, T b)             { return _mm_or_si128(a, b); }
    static SR2D_INLINE T xor_(T a, T b)            { return _mm_xor_si128(a, b); }
    static SR2D_INLINE T andnot(T a, T b)          { return _mm_andnot_si128(a, b); } // ~a & b

    static SR2D_INLINE T add32(T a, T b)           { return _mm_add_epi32(a, b); }
    static SR2D_INLINE T sub32(T a, T b)           { return _mm_sub_epi32(a, b); }
    static SR2D_INLINE T cmpeq32(T a, T b)         { return _mm_cmpeq_epi32(a, b); }
    static SR2D_INLINE T cmpgt32(T a, T b)         { return _mm_cmpgt_epi32(a, b); }
    template<int I> static SR2D_INLINE T srai32(T a) { return _mm_srai_epi32(a, I); }
    template<int I> static SR2D_INLINE T srli32(T a) { return _mm_srli_epi32(a, I); }
    template<int I> static SR2D_INLINE T slli32(T a) { return _mm_slli_epi32(a, I); }
    static SR2D_INLINE T srl32v(T a, __m128i n)    { return _mm_srl_epi32(a, n); }
    static SR2D_INLINE T sll32v(T a, __m128i n)    { return _mm_sll_epi32(a, n); }
    static SR2D_INLINE T mullo32(T a, T b)         { return x128::mullo32(a, b); }
    static SR2D_INLINE T mulhi_u32(T a, T b)       { return x128::mulhi_u32(a, b); }
    static SR2D_INLINE T set1_64(long long v)      { return _mm_set1_epi64x(v); }
    // trunc(num / den) per int32 lane via double precision (exact, see kernels)
    static SR2D_INLINE T div_trunc_pd(T num, T den)
    {
        __m128d nlo = _mm_cvtepi32_pd(num), nhi = _mm_cvtepi32_pd(_mm_shuffle_epi32(num, _MM_SHUFFLE(1, 0, 3, 2)));
        __m128d dlo = _mm_cvtepi32_pd(den), dhi = _mm_cvtepi32_pd(_mm_shuffle_epi32(den, _MM_SHUFFLE(1, 0, 3, 2)));
        return _mm_unpacklo_epi64(_mm_cvttpd_epi32(_mm_div_pd(nlo, dlo)), _mm_cvttpd_epi32(_mm_div_pd(nhi, dhi)));
    }

    static SR2D_INLINE T sub8(T a, T b)            { return _mm_sub_epi8(a, b); }
    static SR2D_INLINE T subs_u8(T a, T b)         { return _mm_subs_epu8(a, b); }
    static SR2D_INLINE T cmpeq8(T a, T b)          { return _mm_cmpeq_epi8(a, b); }
    static SR2D_INLINE T adds_u8(T a, T b)         { return _mm_adds_epu8(a, b); }
    static SR2D_INLINE T max_u8(T a, T b)          { return _mm_max_epu8(a, b); }
    static SR2D_INLINE T min_u8(T a, T b)          { return _mm_min_epu8(a, b); }
    static SR2D_INLINE T avg_u8(T a, T b)          { return _mm_avg_epu8(a, b); }

    static SR2D_INLINE T lo8to16(T a)              { return _mm_unpacklo_epi8(a, _mm_setzero_si128()); }
    static SR2D_INLINE T hi8to16(T a)              { return _mm_unpackhi_epi8(a, _mm_setzero_si128()); }
    static SR2D_INLINE T packus16(T lo, T hi)      { return _mm_packus_epi16(lo, hi); }
    static SR2D_INLINE T add16(T a, T b)           { return _mm_add_epi16(a, b); }
    static SR2D_INLINE T sub16(T a, T b)           { return _mm_sub_epi16(a, b); }
    static SR2D_INLINE T mullo16(T a, T b)         { return _mm_mullo_epi16(a, b); }
    static SR2D_INLINE T min16(T a, T b)           { return _mm_min_epi16(a, b); }      // lanes 0..32767
    static SR2D_INLINE T max16(T a, T b)           { return _mm_max_epi16(a, b); }
    static SR2D_INLINE T cmpgt16(T a, T b)         { return _mm_cmpgt_epi16(a, b); }
    static SR2D_INLINE T subs_u16(T a, T b)        { return _mm_subs_epu16(a, b); }
    static SR2D_INLINE T adds_u16(T a, T b)        { return _mm_adds_epu16(a, b); }
    static SR2D_INLINE T madd16(T a, T b)          { return _mm_madd_epi16(a, b); }
    template<int I> static SR2D_INLINE T srli16(T a) { return _mm_srli_epi16(a, I); }
    template<int I> static SR2D_INLINE T srai16(T a) { return _mm_srai_epi16(a, I); }
    template<int I> static SR2D_INLINE T slli16(T a) { return _mm_slli_epi16(a, I); }

    static SR2D_INLINE T unpacklo32(T a, T b)      { return _mm_unpacklo_epi32(a, b); }
    static SR2D_INLINE T unpackhi32(T a, T b)      { return _mm_unpackhi_epi32(a, b); }
    static SR2D_INLINE T unpacklo64(T a, T b)      { return _mm_unpacklo_epi64(a, b); }
    template<int I> static SR2D_INLINE T shuffle32(T a) { return _mm_shuffle_epi32(a, I); }
    template<int I> static SR2D_INLINE T srli64(T a) { return _mm_srli_epi64(a, I); }

    // reverse dword order over the whole vector
    static SR2D_INLINE T reverse32(T a)            { return _mm_shuffle_epi32(a, _MM_SHUFFLE(0, 1, 2, 3)); }
    static SR2D_INLINE T select(T m, T a, T b)     { return _mm_or_si128(_mm_and_si128(m, a), _mm_andnot_si128(m, b)); }
    static SR2D_INLINE int movemask8(T a)          { return _mm_movemask_epi8(a); }
    static SR2D_INLINE uint32_t hsum32(T a)        { return x128::hsum32(a); }

    static SR2D_INLINE int lane0(T a)              { return _mm_cvtsi128_si32(a); }
    static SR2D_INLINE T max32(T a, T b)           { return select(_mm_cmpgt_epi32(a, b), a, b); }
    static SR2D_INLINE T min32(T a, T b)           { return select(_mm_cmpgt_epi32(a, b), b, a); }

    // ---- float lanes (used by the warp kernel) ----
    typedef __m128 F;
    static SR2D_INLINE F set1f(float v)            { return _mm_set1_ps(v); }
    static SR2D_INLINE F addf(F a, F b)            { return _mm_add_ps(a, b); }
    static SR2D_INLINE F subf(F a, F b)            { return _mm_sub_ps(a, b); }
    static SR2D_INLINE F mulf(F a, F b)            { return _mm_mul_ps(a, b); }
    static SR2D_INLINE F divf(F a, F b)            { return _mm_div_ps(a, b); }
    // NOTE: if a is NaN the result is b (Intel semantics) - relied upon for clamping
    static SR2D_INLINE F maxf(F a, F b)            { return _mm_max_ps(a, b); }
    static SR2D_INLINE F minf(F a, F b)            { return _mm_min_ps(a, b); }
    static SR2D_INLINE F cvt_i2f(T a)              { return _mm_cvtepi32_ps(a); }
    static SR2D_INLINE T cvtt_f2i(F a)             { return _mm_cvttps_epi32(a); }

    // ---- blur helpers: N u16 channel values <-> N u32 lanes, contiguous in memory ----
    static SR2D_INLINE T cvt_f2i_round(F a)            { return _mm_cvtps_epi32(a); }
    static SR2D_INLINE T load16x(const uint16_t* p)    { return _mm_unpacklo_epi16(_mm_loadl_epi64((const __m128i*)p), _mm_setzero_si128()); }
    static SR2D_INLINE void store16x(uint16_t* p, T v) { _mm_storel_epi64((__m128i*)p, _mm_packs_epi32(v, v)); }   // lanes 0..32767
    static SR2D_INLINE T mulhi_u16(T a, T b)           { return _mm_mulhi_epu16(a, b); }
    // lo8to16(v), hi8to16(v) of N packed pixels -> make the two u16 vectors contiguous in memory order (no-op on SSE)
    static SR2D_INLINE void order_unpacked(T&, T&) {}
    // inverse of the above: two contiguous u16 vectors -> N packed pixels
    static SR2D_INLINE T pack16_contig(T a, T b)       { return _mm_packus_epi16(a, b); }
    // broadcast word 3 of every 64-bit group (= the alpha of every [b g r a] u16 pixel)
    static SR2D_INLINE T bcast_a16(T a)                { return _mm_shufflehi_epi16(_mm_shufflelo_epi16(a, 0xFF), 0xFF); }
    // broadcast dword 3 of every 128-bit group (= the alpha of every [b g r a] u32 pixel)
    static SR2D_INLINE T bcast_a32(T a)                { return _mm_shuffle_epi32(a, 0xFF); }
    static SR2D_INLINE F selectf(T m, F a, F b)        { F fm = _mm_castsi128_ps(m); return _mm_or_ps(_mm_and_ps(fm, a), _mm_andnot_ps(fm, b)); }
    // 1/a with one Newton step (~22 bits): x = r * (2 - a*r)
    static SR2D_INLINE F rcpf_nr(F a)                  { F r = _mm_rcp_ps(a); return _mm_mul_ps(r, _mm_sub_ps(_mm_set1_ps(2.0f), _mm_mul_ps(a, r))); }
    // N bytes (= N/4 packed pixels, memory order) -> N u32 lanes
    static SR2D_INLINE T load8x(const void* p)         { T z = _mm_setzero_si128(); return _mm_unpacklo_epi16(_mm_unpacklo_epi8(_mm_cvtsi32_si128(*(const int*)p), z), z); }
    // N/4 pixels' channels from N/4 different rows (SSE: one row) <-> one vector of N u32 lanes
    static SR2D_INLINE T load16x2(const uint16_t* p0, const uint16_t*) { return load16x(p0); }
    static SR2D_INLINE void store16x2(uint16_t* p0, uint16_t*, T v)   { store16x(p0, v); }
    // N u32 lanes holding 8-bit channel values [b g r a]... -> N/4 packed pixels stored at d
    static SR2D_INLINE void store_px(int* d, T v)     { T p = _mm_packs_epi32(v, v); *d = _mm_cvtsi128_si32(_mm_packus_epi16(p, p)); }
    static SR2D_INLINE F floorf_(F a)              // |a| < 2^31 required
    {
        __m128 t = _mm_cvtepi32_ps(_mm_cvttps_epi32(a));
        return _mm_sub_ps(t, _mm_and_ps(_mm_cmpgt_ps(t, a), _mm_set1_ps(1.0f)));
    }
    // ---- effects helpers ----
    static SR2D_INLINE F castf(T a)                { return _mm_castsi128_ps(a); }
    static SR2D_INLINE T casti(F a)                { return _mm_castps_si128(a); }
    static SR2D_INLINE T cmpgtf(F a, F b)          { return _mm_castps_si128(_mm_cmpgt_ps(a, b)); }
    static SR2D_INLINE F sqrtf_(F a)               { return _mm_sqrt_ps(a); }
};

// ---------------------------------------------------------------------------
// AVX2 flavour (only compiled in the AVX2 translation unit)
// ---------------------------------------------------------------------------
#if SR2D_LEVEL >= 2
struct VAVX2
{
    typedef __m256i T;
    enum { N = 8 };
    enum { LEVEL = 2 };

    static SR2D_INLINE T loadu(const void* p)      { return _mm256_loadu_si256((const __m256i*)p); }
    static SR2D_INLINE void storeu(void* p, T v)   { _mm256_storeu_si256((__m256i*)p, v); }
    static SR2D_INLINE T set1_32(int v)            { return _mm256_set1_epi32(v); }
    static SR2D_INLINE T set1_16(short v)          { return _mm256_set1_epi16(v); }
    static SR2D_INLINE T set1_8(char v)            { return _mm256_set1_epi8(v); }
    static SR2D_INLINE T zero()                    { return _mm256_setzero_si256(); }

    static SR2D_INLINE T and_(T a, T b)            { return _mm256_and_si256(a, b); }
    static SR2D_INLINE T or_(T a, T b)             { return _mm256_or_si256(a, b); }
    static SR2D_INLINE T xor_(T a, T b)            { return _mm256_xor_si256(a, b); }
    static SR2D_INLINE T andnot(T a, T b)          { return _mm256_andnot_si256(a, b); }

    static SR2D_INLINE T add32(T a, T b)           { return _mm256_add_epi32(a, b); }
    static SR2D_INLINE T sub32(T a, T b)           { return _mm256_sub_epi32(a, b); }
    static SR2D_INLINE T cmpeq32(T a, T b)         { return _mm256_cmpeq_epi32(a, b); }
    static SR2D_INLINE T cmpgt32(T a, T b)         { return _mm256_cmpgt_epi32(a, b); }
    template<int I> static SR2D_INLINE T srai32(T a) { return _mm256_srai_epi32(a, I); }
    template<int I> static SR2D_INLINE T srli32(T a) { return _mm256_srli_epi32(a, I); }
    template<int I> static SR2D_INLINE T slli32(T a) { return _mm256_slli_epi32(a, I); }
    static SR2D_INLINE T srl32v(T a, __m128i n)    { return _mm256_srl_epi32(a, n); }
    static SR2D_INLINE T sll32v(T a, __m128i n)    { return _mm256_sll_epi32(a, n); }
    static SR2D_INLINE T mullo32(T a, T b)         { return _mm256_mullo_epi32(a, b); }
    static SR2D_INLINE T mulhi_u32(T a, T b)
    {
        __m256i e = _mm256_mul_epu32(a, b);
        __m256i o = _mm256_mul_epu32(_mm256_srli_epi64(a, 32), _mm256_srli_epi64(b, 32));
        return _mm256_blend_epi32(_mm256_srli_epi64(e, 32), o, 0xAA);
    }
    static SR2D_INLINE T set1_64(long long v)      { return _mm256_set1_epi64x(v); }
    static SR2D_INLINE T div_trunc_pd(T num, T den)
    {
        __m256d nlo = _mm256_cvtepi32_pd(_mm256_castsi256_si128(num)), nhi = _mm256_cvtepi32_pd(_mm256_extracti128_si256(num, 1));
        __m256d dlo = _mm256_cvtepi32_pd(_mm256_castsi256_si128(den)), dhi = _mm256_cvtepi32_pd(_mm256_extracti128_si256(den, 1));
        __m128i qlo = _mm256_cvttpd_epi32(_mm256_div_pd(nlo, dlo));
        __m128i qhi = _mm256_cvttpd_epi32(_mm256_div_pd(nhi, dhi));
        return _mm256_inserti128_si256(_mm256_castsi128_si256(qlo), qhi, 1);
    }

    static SR2D_INLINE T sub8(T a, T b)            { return _mm256_sub_epi8(a, b); }
    static SR2D_INLINE T subs_u8(T a, T b)         { return _mm256_subs_epu8(a, b); }
    static SR2D_INLINE T cmpeq8(T a, T b)          { return _mm256_cmpeq_epi8(a, b); }
    static SR2D_INLINE T adds_u8(T a, T b)         { return _mm256_adds_epu8(a, b); }
    static SR2D_INLINE T max_u8(T a, T b)          { return _mm256_max_epu8(a, b); }
    static SR2D_INLINE T min_u8(T a, T b)          { return _mm256_min_epu8(a, b); }
    static SR2D_INLINE T avg_u8(T a, T b)          { return _mm256_avg_epu8(a, b); }

    static SR2D_INLINE T lo8to16(T a)              { return _mm256_unpacklo_epi8(a, _mm256_setzero_si256()); }
    static SR2D_INLINE T hi8to16(T a)              { return _mm256_unpackhi_epi8(a, _mm256_setzero_si256()); }
    static SR2D_INLINE T packus16(T lo, T hi)      { return _mm256_packus_epi16(lo, hi); }
    static SR2D_INLINE T add16(T a, T b)           { return _mm256_add_epi16(a, b); }
    static SR2D_INLINE T sub16(T a, T b)           { return _mm256_sub_epi16(a, b); }
    static SR2D_INLINE T mullo16(T a, T b)         { return _mm256_mullo_epi16(a, b); }
    static SR2D_INLINE T min16(T a, T b)           { return _mm256_min_epi16(a, b); }
    static SR2D_INLINE T max16(T a, T b)           { return _mm256_max_epi16(a, b); }
    static SR2D_INLINE T cmpgt16(T a, T b)         { return _mm256_cmpgt_epi16(a, b); }
    static SR2D_INLINE T subs_u16(T a, T b)        { return _mm256_subs_epu16(a, b); }
    static SR2D_INLINE T adds_u16(T a, T b)        { return _mm256_adds_epu16(a, b); }
    static SR2D_INLINE T madd16(T a, T b)          { return _mm256_madd_epi16(a, b); }
    template<int I> static SR2D_INLINE T srli16(T a) { return _mm256_srli_epi16(a, I); }
    template<int I> static SR2D_INLINE T srai16(T a) { return _mm256_srai_epi16(a, I); }

    template<int I> static SR2D_INLINE T slli16(T a) { return _mm256_slli_epi16(a, I); }

    static SR2D_INLINE T unpacklo32(T a, T b)      { return _mm256_unpacklo_epi32(a, b); }
    static SR2D_INLINE T unpackhi32(T a, T b)      { return _mm256_unpackhi_epi32(a, b); }
    static SR2D_INLINE T unpacklo64(T a, T b)      { return _mm256_unpacklo_epi64(a, b); }
    template<int I> static SR2D_INLINE T shuffle32(T a) { return _mm256_shuffle_epi32(a, I); }
    template<int I> static SR2D_INLINE T srli64(T a) { return _mm256_srli_epi64(a, I); }

    static SR2D_INLINE T reverse32(T a)
    {
        return _mm256_permutevar8x32_epi32(a, _mm256_set_epi32(0, 1, 2, 3, 4, 5, 6, 7));
    }
    static SR2D_INLINE T select(T m, T a, T b)     { return _mm256_blendv_epi8(b, a, m); }
    static SR2D_INLINE int movemask8(T a)          { return _mm256_movemask_epi8(a); }
    static SR2D_INLINE uint32_t hsum32(T a)
    {
        return x128::hsum32(_mm_add_epi32(_mm256_castsi256_si128(a), _mm256_extracti128_si256(a, 1)));
    }

    static SR2D_INLINE int lane0(T a)              { return _mm_cvtsi128_si32(_mm256_castsi256_si128(a)); }
    static SR2D_INLINE T max32(T a, T b)           { return _mm256_max_epi32(a, b); }
    static SR2D_INLINE T min32(T a, T b)           { return _mm256_min_epi32(a, b); }

    typedef __m256 F;
    static SR2D_INLINE F set1f(float v)            { return _mm256_set1_ps(v); }
    static SR2D_INLINE F addf(F a, F b)            { return _mm256_add_ps(a, b); }
    static SR2D_INLINE F subf(F a, F b)            { return _mm256_sub_ps(a, b); }
    static SR2D_INLINE F mulf(F a, F b)            { return _mm256_mul_ps(a, b); }
    static SR2D_INLINE F divf(F a, F b)            { return _mm256_div_ps(a, b); }
    static SR2D_INLINE F maxf(F a, F b)            { return _mm256_max_ps(a, b); }
    static SR2D_INLINE F minf(F a, F b)            { return _mm256_min_ps(a, b); }
    static SR2D_INLINE F cvt_i2f(T a)              { return _mm256_cvtepi32_ps(a); }
    static SR2D_INLINE T cvtt_f2i(F a)             { return _mm256_cvttps_epi32(a); }

    // ---- blur helpers ----
    static SR2D_INLINE T cvt_f2i_round(F a)            { return _mm256_cvtps_epi32(a); }
    static SR2D_INLINE T load16x(const uint16_t* p)    { return _mm256_cvtepu16_epi32(_mm_loadu_si128((const __m128i*)p)); }
    static SR2D_INLINE void store16x(uint16_t* p, T v)
    {   // packs_epi32 works per 128-bit lane: [v0..3 v0..3 | v4..7 v4..7] -> take qwords 0 and 2
        T x = _mm256_permute4x64_epi64(_mm256_packs_epi32(v, v), 0x08);
        _mm_storeu_si128((__m128i*)p, _mm256_castsi256_si128(x));
    }
    static SR2D_INLINE T mulhi_u16(T a, T b)           { return _mm256_mulhi_epu16(a, b); }
    static SR2D_INLINE void order_unpacked(T& lo, T& hi)
    {   // unpacklo/hi are per lane: lo = [p0 p1 | p4 p5], hi = [p2 p3 | p6 p7] -> [p0..p3], [p4..p7]
        T a = _mm256_permute2x128_si256(lo, hi, 0x20), b = _mm256_permute2x128_si256(lo, hi, 0x31);
        lo = a; hi = b;
    }
    static SR2D_INLINE T pack16_contig(T a, T b)
    {   // packus per lane: [a.lo b.lo | a.hi b.hi] (qwords) -> a.lo a.hi b.lo b.hi
        return _mm256_permute4x64_epi64(_mm256_packus_epi16(a, b), _MM_SHUFFLE(3, 1, 2, 0));
    }
    static SR2D_INLINE T bcast_a16(T a)                { return _mm256_shufflehi_epi16(_mm256_shufflelo_epi16(a, 0xFF), 0xFF); }
    static SR2D_INLINE T bcast_a32(T a)                { return _mm256_shuffle_epi32(a, 0xFF); }
    static SR2D_INLINE F selectf(T m, F a, F b)        { return _mm256_blendv_ps(b, a, _mm256_castsi256_ps(m)); }
    static SR2D_INLINE F rcpf_nr(F a)                  { F r = _mm256_rcp_ps(a); return _mm256_mul_ps(r, _mm256_sub_ps(_mm256_set1_ps(2.0f), _mm256_mul_ps(a, r))); }
    static SR2D_INLINE T load8x(const void* p)         { return _mm256_cvtepu8_epi32(_mm_loadl_epi64((const __m128i*)p)); }
    static SR2D_INLINE T load16x2(const uint16_t* p0, const uint16_t* p1)
    { return _mm256_cvtepu16_epi32(_mm_unpacklo_epi64(_mm_loadl_epi64((const __m128i*)p0), _mm_loadl_epi64((const __m128i*)p1))); }
    static SR2D_INLINE void store16x2(uint16_t* p0, uint16_t* p1, T v)
    {
        T x = _mm256_packs_epi32(v, v);                 // lane0 = [v0..3 v0..3], lane1 = [v4..7 v4..7]
        _mm_storel_epi64((__m128i*)p0, _mm256_castsi256_si128(x));
        _mm_storel_epi64((__m128i*)p1, _mm256_extracti128_si256(x, 1));
    }
    static SR2D_INLINE void store_px(int* d, T v)
    {
        T p = _mm256_packs_epi32(v, v); p = _mm256_packus_epi16(p, p);   // lane0 dword0 = px0, lane1 dword0 = px1
        d[0] = _mm_cvtsi128_si32(_mm256_castsi256_si128(p));
        d[1] = _mm_cvtsi128_si32(_mm256_extracti128_si256(p, 1));
    }
    static SR2D_INLINE F floorf_(F a)              { return _mm256_floor_ps(a); }
    static SR2D_INLINE F castf(T a)                { return _mm256_castsi256_ps(a); }
    static SR2D_INLINE T casti(F a)                { return _mm256_castps_si256(a); }
    static SR2D_INLINE T cmpgtf(F a, F b)          { return _mm256_castps_si256(_mm256_cmp_ps(a, b, _CMP_GT_OQ)); }
    static SR2D_INLINE F sqrtf_(F a)               { return _mm256_sqrt_ps(a); }
};
#endif
