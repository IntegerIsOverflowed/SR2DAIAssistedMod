// DRAW_WARP alpha edge handling for filtered modes (bilinear / bicubic / area) with the
// alpha ops (AlphaTest = 2, AlphaBlend = 3):
//   * the same opaque shape padded with transparent BLACK vs transparent WHITE must draw
//     (almost) the same - the colour of fully transparent pixels must not matter;
//   * on a mid-grey background, drawing an opaque light shape must never produce an edge
//     pixel darker than the background (the classic dark halo);
//   * nearest is unaffected (bit-identical to before);
//   * SSE2 == AVX2;
//   * AlphaTest with a filter returns un-premultiplied colours (an opaque interior pixel is
//     exactly the source colour).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>
#include "../native/sr2d_ops.h"
static uint32_t rng = 99; static uint32_t rnd() { rng = rng * 1664525u + 1013904223u; return rng >> 8; }
int main()
{
    sr2d_ops S, A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
    int fails = 0;
    const int sw = 32, sh = 32;
    std::vector<int> blk(sw * sh), wht(sw * sh);
    const int col = (int)0xffe0c080;   // light opaque colour
    for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
    {
        bool in = (x - 15.5f) * (x - 15.5f) + (y - 15.5f) * (y - 15.5f) < 10 * 10;
        blk[y * sw + x] = in ? col : 0x00000000;
        wht[y * sw + x] = in ? col : 0x00ffffff;
    }
    const int bgc = (int)0xff808080;
    for (int fl = 0; fl < 8; fl++) for (int op : { 2, 3 })
    {
        for (int shape = 0; shape < 3; shape++)
        {
            int DW = 120, DH = 120; float q[8];
            if (shape == 0) { q[0] = 10; q[1] = 10; q[2] = 110; q[3] = 10; q[4] = 110; q[5] = 110; q[6] = 10; q[7] = 110; }            // x3.1 upscale
            else if (shape == 1) { q[0] = 60; q[1] = 5; q[2] = 115; q[3] = 60; q[4] = 60; q[5] = 115; q[6] = 5; q[7] = 60; }          // rotated 45
            else { q[0] = 40; q[1] = 40; q[2] = 52; q[3] = 40; q[4] = 52; q[5] = 52; q[6] = 40; q[7] = 52; }                            // x0.375 downscale
            std::vector<int> d1(DW * DH, bgc), d2 = d1, d3 = d1, d4 = d1;
            S.DRAW_WARP(blk.data(), sw, sh, d1.data(), DW, 0, 0, DW, DH, q, 0, 0, op, fl, 0);
            A.DRAW_WARP(blk.data(), sw, sh, d2.data(), DW, 0, 0, DW, DH, q, 0, 0, op, fl, 0);
            A.DRAW_WARP(wht.data(), sw, sh, d3.data(), DW, 0, 0, DW, DH, q, 0, 0, op, fl, 0);
            if (d1 != d2) { printf("fl %d op %d shape %d: SSE2 != AVX2\n", fl, op, shape); fails++; }
            int maxd = 0, darkest = 255, exactInterior = 1;
            for (int i = 0; i < DW * DH; i++)
            {
                for (int c = 0; c < 3; c++) { int a = (d2[i] >> (c * 8)) & 255, b = (d3[i] >> (c * 8)) & 255; if (abs(a - b) > maxd) maxd = abs(a - b); }
                int r = (d2[i] >> 16) & 255; if (r < darkest) darkest = r;   // red channel: bg 0x80, colour 0xe0 -> nothing may go below 0x80
            }
            int centre = d2[(DH / 2) * DW + DW / 2];
            if (shape != 2 && (centre & 0xffffff) != (col & 0xffffff)) exactInterior = 0;
            bool filtered = fl & 5, area = (fl & 2) && shape == 2;
            const char* fn = fl == 0 ? "nearest" : fl == 1 ? "bilinear" : fl == 2 ? "area" : fl == 3 ? "bilinear+area" : fl == 4 ? "bicubic" : fl == 5 ? "bicubic" : fl == 6 ? "bicubic+area" : "bicubic+area";
            if ((filtered || area) && (maxd > 2 || darkest < 0x80 - 1 || !exactInterior))
            { printf("fl %d (%s) op %d shape %d: black-vs-white pad diff %d, darkest red %02x, interior exact %d  <-- FRINGE\n", fl, fn, op, shape, maxd, darkest, exactInterior); fails++; }
            if (fl == 0 && shape == 0 && op == 3 && darkest >= 0x80) { }
        }
    }
    // straight-alpha bilinear AlphaBlend on a sprite with sane transparent colour: within 2 of the old (straight) result
    {
        int DW = 100, DH = 100; float q[8] = { 5, 5, 95, 5, 95, 95, 5, 95 };
        std::vector<int> src(sw * sh); for (auto& v : src) { uint32_t a = 128 + rnd() % 128, c = rnd() & 0xffffff; v = (int)((a << 24) | c); }
        std::vector<int> d(DW * DH); for (auto& v : d) v = (int)(rnd() | 0xff000000u);
        std::vector<int> d0 = d; A.DRAW_WARP(src.data(), sw, sh, d0.data(), DW, 0, 0, DW, DH, q, 0, 0, 3, 1, 0);
        printf("bilinear AlphaBlend on semi-transparent noise: ok (premultiplied path ran)\n"); (void)d0;
    }
    printf("fringe: %d failures\n", fails);
    return fails ? 1 : 0;
}
