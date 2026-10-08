// opbench: mimics the demo's "Draw (Op selector)" frame - a tiled Paint checker over a 1920x1080 canvas
// plus a grid of 64x64 op blits - to A/B engine build flags and kernel changes (the fps regression hunt).
#include <cstdio>
#include <cstdlib>
#include <chrono>
#include <cstring>
#include <cstdint>
extern "C" {
void PAINT(int* s, int* d, int w, int h, int ws, int wd);
void ALPHA_B(int* s, int* d, int w, int h, int ws, int wd);
}
#include "../../native/sr2d_api.h"

static long long now_ns() {
    using namespace std::chrono;
    return duration_cast<nanoseconds>(steady_clock::now().time_since_epoch()).count();
}

int main() {
    const int W = 1920, H = 1080;
    auto* dst = (int*)aligned_alloc(64, (size_t)W * H * 4);
    auto* srcA = (int*)aligned_alloc(64, (size_t)64 * 64 * 4);   // colour sprite (Paint)
    auto* srcB = (int*)aligned_alloc(64, (size_t)128 * 128 * 4); // checker tile (Paint)
    auto* srcC = (int*)aligned_alloc(64, (size_t)64 * 64 * 4);   // alpha sprite (AlphaBlend)
    for (int i = 0; i < W * H; i++) dst[i] = 0xFF202020;
    for (int i = 0; i < 64 * 64; i++) { srcA[i] = 0xFF604080; srcC[i] = 0x8030C060; }
    for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
        srcB[y * 128 + x] = ((x / 64 + y / 64) & 1) ? 0xFF38383C : 0xFF5C5C60;

    // (ABI check skipped in the bench harness)

    // one demo frame: tile 128x128 checker over the canvas (15x9 = 135 Paint blits, most edge-clipped)
    // + 20x12 grid of 64x64 blits with the demo's default-ish ops (Paint and AlphaBlend halves)
    auto frame = [&] {
        for (int y = 0; y < H; y += 128)
            for (int x = 0; x < W; x += 128) {
                int w = x + 128 > W ? W - x : 128, h = y + 128 > H ? H - y : 128;
                PAINT(srcB, dst + y * W + x, w, h, 128, W);
            }
        for (int y = 0; y + 64 <= H; y += 90)
            for (int x = 0; x + 64 <= W; x += 96)
                PAINT(srcA, dst + y * W + x, 64, 64, 64, W);
        for (int y = 0; y + 64 <= H; y += 90)               // the AlphaBlend half of the grid (alpha sprite)
            for (int x = 0; x + 64 <= W; x += 96)
                ALPHA_B(srcC, dst + y * W + x, 64, 64, 64, W);
    };

    // warmup + measure
    for (int i = 0; i < 30; i++) frame();
    const int FRAMES = 300;
    auto t0 = now_ns();
    for (int i = 0; i < FRAMES; i++) frame();
    auto t1 = now_ns();
    double us = (t1 - t0) / 1000.0 / FRAMES;
    printf("frame: %.1f us  (~%.0f fps equivalent)\n", us, 1e6 / us);
    printf("checksum: %d\n", dst[W * H / 2 + W / 2]);
    return 0;
}
