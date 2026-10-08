// SR2D - DLL entry point, CPU detection, runtime dispatch and exported wrappers.
//
// The exported C ABI is identical to the original SR2D.dll (names, calling
// convention, argument order). Two extra diagnostics exports were added:
//   int  SR2D_SIMD_LEVEL()          -> 1 = SSE2 kernels active, 2 = AVX2 kernels active
//   int  SR2D_SET_SIMD_LEVEL(int)   -> force a level (0 = auto); returns the level in use
//
// The DLL is linked with /ENTRY:DllMain (no CRT start-up), therefore:
//   * no static constructors are relied upon (the dispatch table is filled
//     from DllMain and, defensively, lazily on first use);
//   * no malloc/free - RESIZE scratch memory comes from the process heap.

#if defined(_WIN32)
#  include <windows.h>
#  include <intrin.h>
#  define SR2D_EXPORT extern "C" __declspec(dllexport)
#  define SR2D_CALL   __stdcall
#else
#  include <stdlib.h>
#  include <cpuid.h>
#  include <immintrin.h>
#  define SR2D_EXPORT extern "C" __attribute__((visibility("default")))
#  define SR2D_CALL
#endif

#include "sr2d_ops.h"

#if defined(_MSC_VER)
#pragma comment(linker, "/MERGE:.rdata=.text")
#pragma comment(linker, "/ENTRY:DllMain")
#endif

// ---------------------------------------------------------------------------
// memory
// ---------------------------------------------------------------------------
void* sr2d_alloc(size_t bytes)
{
#if defined(_WIN32)
    return HeapAlloc(GetProcessHeap(), 0, bytes);
#else
    return malloc(bytes);
#endif
}
void sr2d_free(void* p)
{
#if defined(_WIN32)
    if (p) HeapFree(GetProcessHeap(), 0, p);
#else
    free(p);
#endif
}

// Scratch cache for the big per-call work buffers (DRAW_BLUR / DRAW_FX: up to tens of
// MB at full-frame sizes). A fresh large allocation is served by the OS as untouched
// pages, and faulting them in costs more than the blur itself (measured: ~20 ms for
// 33 MB). Blocks are therefore kept between calls in a small lock-free POOL: acquire()
// takes a block that is big enough, release() puts it back.
//
//   * slot 0 holds one block of any size up to 96 MB (the full-frame case);
//   * slots 1..15 hold blocks of up to 8 MB each (a 256..512 px layer's work set is
//     1..8 MB), so up to 16 threads composing layers concurrently (LayeredSprite with
//     Threads > 1, DrawParallel) all run without a single allocation in steady state.
//     Bounded: at most 96 + 15 * 8 MB are ever retained, and only if that many threads
//     really did use them.
//
// Thread safety: a slot is a single pointer swapped atomically; the block's capacity
// lives in a header inside the block, so pointer and size are always published together
// (two separate globals would let a concurrent release overwrite the size between
// another thread's release and a third thread's acquire). Inspecting a block means
// taking it; a block that turns out too small is swapped back (if the slot was refilled
// meanwhile, the displaced one is freed). The header is 64 bytes so the user pointer
// keeps the allocator's 16-byte alignment (the kernels only need 16).
static const size_t kScratchHdr = 64;
static const int    kScratchSlots = 16;
static void* volatile g_scratch_slots[kScratchSlots];             // header pointers, or 0
static const size_t kScratchKeepMax0 = (size_t)96 << 20;         // slot 0
static const size_t kScratchKeepMaxN = (size_t)8 << 20;          // slots 1..15

static inline void* xchg_ptr(void* volatile* slot, void* v)
{
#if defined(_WIN32)
    return InterlockedExchangePointer((PVOID volatile*)slot, v);
#else
    return __atomic_exchange_n(slot, v, __ATOMIC_ACQ_REL);
#endif
}
static inline void* load_ptr(void* volatile* slot)
{
#if defined(_WIN32)
    return *slot;
#else
    return __atomic_load_n(slot, __ATOMIC_ACQUIRE);
#endif
}

static inline void scratch_put_back(int i, uint8_t* hdr)
{
    void* old = xchg_ptr(&g_scratch_slots[i], hdr);
    if (old) sr2d_free(old);
}

void* sr2d_scratch_acquire(size_t bytes, size_t* got)
{
    const bool isSmall = bytes <= kScratchKeepMaxN;
    // small requests look in the small slots first (15 -> 1) so they do not steal the
    // full-frame block of slot 0 from a big user; big requests can only be in slot 0
    const int first = isSmall ? kScratchSlots - 1 : 0, step = isSmall ? -1 : 1;
    for (int i = first; i >= 0 && i < kScratchSlots; i += step)
    {
        if (!load_ptr(&g_scratch_slots[i])) continue;
        uint8_t* hdr = (uint8_t*)xchg_ptr(&g_scratch_slots[i], 0);
        if (!hdr) continue;
        const size_t have = *(const size_t*)hdr;
        if (have >= bytes) { *got = have; return hdr + kScratchHdr; }
        scratch_put_back(i, hdr);
        if (!isSmall) break;
    }
    uint8_t* hdr = (uint8_t*)sr2d_alloc(bytes + kScratchHdr);
    if (!hdr) { *got = 0; return 0; }
    *(size_t*)hdr = bytes;
    *got = bytes;
    return hdr + kScratchHdr;
}

void sr2d_scratch_release(void* p, size_t /*bytes*/)
{
    if (!p) return;
    uint8_t* hdr = (uint8_t*)p - kScratchHdr;
    const size_t size = *(const size_t*)hdr;
    if (size > kScratchKeepMax0) { sr2d_free(hdr); return; }
    if (size <= kScratchKeepMaxN)
    {   // an empty small slot, if there is one
        for (int i = kScratchSlots - 1; i >= 1; --i)
        {
            if (load_ptr(&g_scratch_slots[i])) continue;
            void* old = xchg_ptr(&g_scratch_slots[i], hdr);
            if (!old) return;
            hdr = (uint8_t*)old;                 // raced with another release: carry the displaced block on
        }
    }
    // slot 0: the larger block wins (a full-frame block must not be evicted by a small one)
    void* old = xchg_ptr(&g_scratch_slots[0], hdr);
    if (!old) return;
    if (*(const size_t*)old > size) { void* o2 = xchg_ptr(&g_scratch_slots[0], old); if (o2) sr2d_free(o2); }
    else sr2d_free(old);
}

// ---------------------------------------------------------------------------
// CPU feature detection
// ---------------------------------------------------------------------------
static void cpuid_ex(int leaf, int sub, int out[4])
{
#if defined(_MSC_VER)
    __cpuidex(out, leaf, sub);
#else
    unsigned a, b, c, d;
    __cpuid_count(leaf, sub, a, b, c, d);
    out[0] = (int)a; out[1] = (int)b; out[2] = (int)c; out[3] = (int)d;
#endif
}

static unsigned long long xgetbv0()
{
#if defined(_MSC_VER)
    return _xgetbv(0);
#else
    unsigned eax, edx;
    __asm__ volatile("xgetbv" : "=a"(eax), "=d"(edx) : "c"(0));
    return ((unsigned long long)edx << 32) | eax;
#endif
}

static int detect_level()
{
    int r[4];
    cpuid_ex(0, 0, r);
    const int max_leaf = r[0];
    cpuid_ex(1, 0, r);
    const bool sse2   = (r[3] & (1 << 26)) != 0;
    const bool osxsave= (r[2] & (1 << 27)) != 0;
    const bool avx    = (r[2] & (1 << 28)) != 0;
    if (!sse2) return SR2D_SIMD_SSE2;                       // x64 always has SSE2; x86 build assumes it
    if (max_leaf >= 7 && osxsave && avx)
    {
        const unsigned long long xcr0 = xgetbv0();
        if ((xcr0 & 6) == 6)                                // XMM + YMM state enabled by the OS
        {
            cpuid_ex(7, 0, r);
            if (r[1] & (1 << 5)) return SR2D_SIMD_AVX2;     // AVX2
        }
    }
    return SR2D_SIMD_SSE2;
}

// ---------------------------------------------------------------------------
// dispatch table
// ---------------------------------------------------------------------------
// Two complete tables are filled exactly once (one-time guard) and the active one is
// published by a single atomic pointer swap. Readers therefore never see a half-filled
// or half-switched table, even when the first call comes from several threads at once
// (the .so has no DllMain; a host may also bypass it) or when SR2D_SET_SIMD_LEVEL is
// called while other threads are drawing.
static sr2d_ops g_tab_sse2, g_tab_avx2;
static const sr2d_ops* volatile g_cur = 0;   // the active table, or 0 before init
static volatile long g_level = 0;            // level of g_cur
static volatile long g_tab_state = 0;        // 0 = not filled, 1 = being filled, 2 = filled

static inline long cas_long(volatile long* p, long expect, long desired)
{
#if defined(_WIN32)
    return InterlockedCompareExchange(p, desired, expect);
#else
    __atomic_compare_exchange_n(p, &expect, desired, false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
    return expect;
#endif
}
static inline long load_long(volatile long* p)
{
#if defined(_WIN32)
    return InterlockedCompareExchange(p, 0, 0);
#else
    return __atomic_load_n(p, __ATOMIC_ACQUIRE);
#endif
}
static inline void store_long(volatile long* p, long v)
{
#if defined(_WIN32)
    InterlockedExchange(p, v);
#else
    __atomic_store_n(p, v, __ATOMIC_RELEASE);
#endif
}

static void fill_tables_once()
{
    if (load_long(&g_tab_state) == 2) return;
    if (cas_long(&g_tab_state, 0, 1) == 0)
    {
        sr2d_fill_ops_sse2(g_tab_sse2);
        if (detect_level() >= SR2D_SIMD_AVX2) sr2d_fill_ops_avx2(g_tab_avx2);
        else g_tab_avx2 = g_tab_sse2;                    // no AVX2 on this CPU: both names mean SSE2
        store_long(&g_tab_state, 2);
        return;
    }
    while (load_long(&g_tab_state) != 2) _mm_pause();   // another thread is filling: wait for it
}

static void sr2d_init(int forced)
{
    fill_tables_once();
    int lvl = forced > 0 ? forced : detect_level();
    if (lvl > SR2D_SIMD_AVX2) lvl = SR2D_SIMD_AVX2;      // unknown/higher levels -> best we have
    const sr2d_ops* t;
    if (lvl >= SR2D_SIMD_AVX2 && detect_level() >= SR2D_SIMD_AVX2) t = &g_tab_avx2;
    else { lvl = SR2D_SIMD_SSE2; t = &g_tab_sse2; }
    store_long(&g_level, lvl);                           // level first, table second: a reader that sees the new
    xchg_ptr((void* volatile*)&g_cur, (void*)t);         // table reports the matching level (level is informational only)
}

static inline const sr2d_ops& ops()
{
    const sr2d_ops* t = (const sr2d_ops*)load_ptr((void* volatile*)&g_cur);
    if (!t) { sr2d_init(0); t = (const sr2d_ops*)load_ptr((void* volatile*)&g_cur); }
    return *t;
}

SR2D_EXPORT int SR2D_CALL SR2D_ABI_VERSION() { return SR2D_ABI; }   // ABI handshake (see sr2d_api.h)
SR2D_EXPORT int SR2D_CALL SR2D_SIMD_LEVEL()
{
    return ops(), (int)load_long(&g_level);
}

SR2D_EXPORT int SR2D_CALL SR2D_SET_SIMD_LEVEL(int level)
{
    sr2d_init(level);
    return (int)load_long(&g_level);
}

// ---------------------------------------------------------------------------
// exported wrappers (generated from the X-macro list)
// ---------------------------------------------------------------------------
#define X(ret, name, params, args) \
    SR2D_EXPORT ret SR2D_CALL name params { return ops().name args; }
SR2D_OPS(X)
#undef X

// tiny helpers that never touched pixels - kept for ABI compatibility
SR2D_EXPORT int SR2D_CALL ARGB_(int a, int r, int g, int b)
{
    return (a << 24) | ((r & 0xff) << 16) | ((g & 0xff) << 8) | (b & 0xff);
}

SR2D_EXPORT int SR2D_CALL EXP2N(int n)
{
    return 1 << n;
}

// ---------------------------------------------------------------------------
// DllMain
// ---------------------------------------------------------------------------
#if !defined(_WIN32)
__attribute__((constructor)) static void sr2d_so_init() { sr2d_init(0); }   // the .so has no DllMain: fill the tables before any thread can call in
#endif

#if defined(_WIN32)
BOOL APIENTRY DllMain(HANDLE hModule, DWORD reason, LPVOID /*lpReserved*/)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls((HMODULE)hModule);   // we have no per-thread state
        sr2d_init(0);
    }
    return TRUE;
}
#endif
