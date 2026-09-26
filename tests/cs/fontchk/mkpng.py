#!/usr/bin/env python3
# Writes reference PNGs with Pillow (every colour type, bit depth, interlace, tRNS) + raw RGBA truth next to them.
import os, sys, random
from PIL import Image
out = sys.argv[1] if len(sys.argv) > 1 else '/home/user/.cache/fontchk/ref'
os.makedirs(out, exist_ok=True); random.seed(3)
W, H = 37, 23
def save(name, img, **kw):
    img.save(os.path.join(out, name + '.png'), **kw)
    rgba = img.convert('RGBA').tobytes()
    open(os.path.join(out, name + '.rgba'), 'wb').write(rgba)
rgb = Image.new('RGB', (W, H)); rgb.putdata([(random.randrange(256), random.randrange(256), random.randrange(256)) for _ in range(W*H)])
rgba = Image.new('RGBA', (W, H)); rgba.putdata([(random.randrange(256), random.randrange(256), random.randrange(256), random.randrange(256)) for _ in range(W*H)])
L = Image.new('L', (W, H)); L.putdata([random.randrange(256) for _ in range(W*H)])
LA = Image.new('LA', (W, H)); LA.putdata([(random.randrange(256), random.randrange(256)) for _ in range(W*H)])
save('rgb8', rgb); save('rgba8', rgba); save('gray8', L); save('grayalpha8', LA)
save('rgb8_interlaced', rgb, interlace=1); save('rgba8_interlaced', rgba, interlace=1); save('gray8_interlaced', L, interlace=1)
save('pal8', rgb.quantize(200)); save('pal4', rgb.quantize(16), bits=4); save('pal2', rgb.quantize(4), bits=2); save('pal1', rgb.convert('1'))
save('pal8_interlaced', rgb.quantize(100), interlace=1)
# palette with transparency
p = rgba.quantize(64); save('pal_trns', p)
# colour-key transparency (RGB + tRNS)
k = rgb.copy(); px = k.load(); px[0, 0] = (1, 2, 3); px[5, 5] = (1, 2, 3)
k.save(os.path.join(out, 'rgb_trns.png'), transparency=(1, 2, 3))
t = rgb.convert('RGBA'); tp = t.load(); tp[0, 0] = (1, 2, 3, 0); tp[5, 5] = (1, 2, 3, 0); open(os.path.join(out, 'rgb_trns.rgba'), 'wb').write(t.tobytes())
# 16-bit grey
I16 = Image.new('I;16', (W, H)); I16.putdata([random.randrange(65536) for _ in range(W*H)])
I16.save(os.path.join(out, 'gray16.png'))
hi = bytes(v >> 8 for v in I16.getdata()); open(os.path.join(out, 'gray16.rgba'), 'wb').write(b''.join(bytes((g, g, g, 255)) for g in hi))
# low-bit greys
g4 = Image.new('L', (W, H)); g4.putdata([random.randrange(16) * 17 for _ in range(W*H)]); save('gray4', g4, bits=4)
g1 = Image.new('1', (W, H)); g1.putdata([random.randrange(2) for _ in range(W*H)]); save('gray1', g1)
print('wrote', len(os.listdir(out)) // 2, 'reference PNGs to', out)
