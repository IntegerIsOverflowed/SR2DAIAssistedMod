// Regression checks for the reviewed kernel defects, so they can never come back quietly.
// Like the rest of the suite, every check runs on BOTH dispatch tables (SSE2 and AVX2):
//   * OpBlend's blend factor domain is 0..256 INCLUSIVE - k = 256 must be "full source"
//     (it used to be masked to 0 and blended nothing), k > 256 must clamp, not wrap.
//   * DRAW_LINE2 must never write outside the clip rect for DEGENERATE (zero-length)
//     segments at half-pixel coordinates (the +/-0.5 Liang-Barsky slack used to let the
//     major endpoint round one pixel past the buffer - a heap write).
//   * A sub-pixel dot+gap dash pattern must degrade to solid, not spin the enumeration.
//   * SR2D_ABI_VERSION() reports the header's SR2D_ABI (the managed resolver refuses a mismatch).
#include <stdio.h>
#include <string.h>
#include "../native/sr2d_ops.h"

extern "C" int SR2D_ABI_VERSION(void);

static int bad = 0;
static void chk(int ok, const char* what) { printf("  %s %s\n", ok ? "ok  " : "FAIL", what); if (!ok) bad++; }

static const int W = 64, H = 64;

typedef void BLEND_FN(int* src, int* dst, int w, int h, int ws, int wd, int k);
typedef void LINE_FN(int* dst, int dw, int clipL, int clipT, int clipR, int clipB,
                     float x0, float y0, float x1, float y1, int col, int op, int k,
                     float dotlen, float gaplen, float phase);

static void run_on(const char* name, BLEND_FN* BLEND, LINE_FN* DRAW_LINE2)
{
    // ---- the blend factor domain
    {
        int src[W * H], dst[W * H];
        for (int i = 0; i < W * H; i++) { src[i] = (int)0x8037A0FF; dst[i] = (int)0xFF00FF00; }
        BLEND(src, dst, W, H, W, W, 256);
        int same = 1; for (int i = 0; i < W * H; i++) if (dst[i] != src[i]) same = 0;
        chk(same, "BLEND k=256 replaces the destination with the source (0..256 is inclusive)");
        for (int i = 0; i < W * H; i++) dst[i] = (int)0xFF00FF00;
        BLEND(src, dst, W, H, W, W, 400);
        int clamped = 1; for (int i = 0; i < W * H; i++) if (dst[i] != src[i]) clamped = 0;
        chk(clamped, "BLEND k=400 clamps to k=256 (no wrap to a small factor)");
        for (int i = 0; i < W * H; i++) dst[i] = (int)0x12345678;
        BLEND(src, dst, W, H, W, W, 0);
        int kept = 1; for (int i = 0; i < W * H; i++) if (dst[i] != (int)0x12345678) kept = 0;
        chk(kept, "BLEND k=0 keeps the destination");
    }
    // ---- degenerate segments at half-pixel coordinates must not write outside the buffer
    {
        int* buf = new int[W * H + 2];
        int* dst = buf + 1;                       // one guard int on each side of the surface
        const int GUARD = (int)0xEFEFEFEF;
        auto probe = [&]() { return buf[0] == GUARD && buf[W * H + 1] == GUARD; };
        memset(buf, 0xEF, (W * H + 2) * sizeof(int));
        DRAW_LINE2(dst, W, 0, 0, W, H, (float)W - 0.5f, (float)H - 0.5f, (float)W - 0.5f, (float)H - 0.5f, (int)0xFF000000, 1, 0, 0.f, 0.f, 0.f);
        chk(probe(), "degenerate point (W-0.5, H-0.5) stays inside the buffer");
        memset(buf, 0xEF, (W * H + 2) * sizeof(int));
        DRAW_LINE2(dst, W, 0, 0, W, H, -0.5f, 0.f, -0.5f, 0.f, (int)0xFF000000, 1, 0, 0.f, 0.f, 0.f);
        chk(probe(), "degenerate point (-0.5, 0) stays inside the buffer");
        memset(buf, 0xEF, (W * H + 2) * sizeof(int));
        DRAW_LINE2(dst, W, 0, 0, W, H, (float)W - 0.5f, 0.f, (float)W - 0.5f, 0.f, (int)0xFF000000, 1, 0, 0.f, 0.f, 0.f);
        chk(probe(), "degenerate point (W-0.5, 0) stays inside the buffer");
        memset(buf, 0xEF, (W * H + 2) * sizeof(int));
        int before = 0; for (int i = 0; i < W * H; i++) if (dst[i] != GUARD) before++;
        DRAW_LINE2(dst, W, 0, 0, W, H, 2, 2, 20, 20, (int)0xFF000000, 1, 0, 0.f, 0.f, 0.f);
        int lit = 0; for (int i = 0; i < W * H; i++) if (dst[i] != GUARD) lit++;
        chk(lit > before && probe(), "an ordinary segment still draws and keeps the guards intact");
        delete[] buf;
    }
    // ---- a sub-pixel dot+gap pattern must terminate and degrade to solid
    {
        int solid[W * H], dots[W * H];
        memset(solid, 0, sizeof(solid));
        DRAW_LINE2(solid, W, 0, 0, W, H, 0, 0, 60, 60, (int)0xFF00FF00, 1, 0, 0.f, 0.f, 0.f);
        memset(dots, 0, sizeof(dots));
        DRAW_LINE2(dots, W, 0, 0, W, H, 0, 0, 60, 60, (int)0xFF00FF00, 1, 0, 1e-30f, 1e-30f, 0.f);
        int a = 0, b = 0; for (int i = 0; i < W * H; i++) { if (solid[i]) a++; if (dots[i]) b++; }
        chk(b == a, "a 1e-30 dot+gap pattern draws as a solid line (no hang, no empty draw)");
    }
    (void)name;
}

int main()
{
    sr2d_ops sse2, avx2;
    sr2d_fill_ops_sse2(sse2);
    sr2d_fill_ops_avx2(avx2);
    run_on("SSE2", sse2.BLEND, sse2.DRAW_LINE2);
    run_on("AVX2", avx2.BLEND, avx2.DRAW_LINE2);
    chk(SR2D_ABI_VERSION() == SR2D_ABI, "SR2D_ABI_VERSION() matches the header's SR2D_ABI");
    printf(bad == 0 ? "regchk: all checks passed\n" : "regchk: %d FAILED\n", bad);
    return bad;
}
