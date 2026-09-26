// SR2D - runtime dispatch table (filled by the SSE2 or AVX2 kernel set).
#pragma once
#include <stddef.h>
#include "sr2d_api.h"

struct sr2d_ops
{
#define X(ret, name, params, args) ret (*name) params;
    SR2D_OPS(X)
#undef X
};

void sr2d_fill_ops_sse2(sr2d_ops& o);
void sr2d_fill_ops_avx2(sr2d_ops& o);

// CRT-free allocation (the DLL is linked with /ENTRY:DllMain, so the C runtime
// heap is never initialised -> malloc must not be used inside the DLL).
void* sr2d_alloc(size_t bytes);
void  sr2d_free(void* p);
// cached large scratch block (see sr2d.cpp); *got receives the usable size
void* sr2d_scratch_acquire(size_t bytes, size_t* got);
void  sr2d_scratch_release(void* p, size_t bytes);
