// Makes the untouched original sources compile on GCC/Linux for differential testing.
#pragma once
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <alloca.h>
#include <emmintrin.h>
#define __declspec(x)
#define __stdcall
#define _int64 long long
#define _alloca alloca
static inline void __movsd(unsigned long* d, const unsigned long* s, size_t n) { memmove(d, s, n * 4); }
