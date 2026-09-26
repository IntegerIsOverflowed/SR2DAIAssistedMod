// SR2D - AVX2 kernel set.  Compile this file with /arch:AVX2 (MSVC) or
// -mavx2 -mfma (GCC/Clang). It is only *called* when the CPU supports AVX2.
#if defined(_MSC_VER)
#  include <intrin.h>
#else
#  include <immintrin.h>
#endif
#include <stdint.h>
#include <string.h>
#include <stddef.h>

#include "sr2d_ops.h"

// The float paths (DRAW_WARP, DRAW_POLY edge stepping) must produce identical
// pixels in every kernel set, so fused multiply-add contraction is disabled
// explicitly for this translation unit:
//   * clang / clang-cl: contraction is ON by default (also under /fp:precise) and
//     /arch:AVX2 enables FMA -> without this pragma the AVX2 kernels would differ
//     from the SSE2 ones by 1 ulp in places (verified: SSE2 != AVX2 in polytest).
//   * MSVC: VS2022 /fp:precise already means contraction off; the pragma keeps
//     older toolsets honest.
//   * GCC: no pragma exists; the Makefile passes -ffp-contract=off.
#if defined(__clang__)
#  pragma clang fp contract(off)
#elif defined(_MSC_VER)
#  pragma fp_contract(off)
#endif

#define SR2D_LEVEL 2
namespace sr2d_avx2
{
#include "sr2d_simd.h"
#include "sr2d_kernels.inl"
    typedef K<VAVX2> Kern;
}

void sr2d_fill_ops_avx2(sr2d_ops& o)
{
#define X(ret, name, params, args) o.name = &sr2d_avx2::Kern::name;
    SR2D_OPS(X)
#undef X
}
