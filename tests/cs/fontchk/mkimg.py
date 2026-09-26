#!/usr/bin/env python3
# Writes reference BMP / GIF / TGA / JPEG files with Pillow + raw RGBA truth next to them (for ImageCodec checks).
import os, sys, random, struct, warnings
warnings.simplefilter('ignore')
from PIL import Image
out = sys.argv[1] if len(sys.argv) > 1 else '/home/user/.cache/fontchk/img'
os.makedirs(out, exist_ok=True); random.seed(5)
W, H = 41, 19
def truth(name, img): open(os.path.join(out, name + '.rgba'), 'wb').write(img.convert('RGBA').tobytes())
rgb = Image.new('RGB', (W, H)); rgb.putdata([(random.randrange(256), random.randrange(256), random.randrange(256)) for _ in range(W*H)])
rgba = Image.new('RGBA', (W, H)); rgba.putdata([(random.randrange(256), random.randrange(256), random.randrange(256), random.randrange(256)) for _ in range(W*H)])
L = Image.new('L', (W, H)); L.putdata([random.randrange(256) for _ in range(W*H)])
# BMP: 24-bit, 32-bit (Pillow writes BGRA with alpha for RGBA), 8-bit palette, 1-bit, RLE8 (hand-made), 16-bit 565 (hand-made), top-down 24 (hand-made)
rgb.save(os.path.join(out, 'bmp24.bmp')); truth('bmp24', rgb)
rgba.save(os.path.join(out, 'bmp32.bmp')); truth('bmp32', rgba)
p = rgb.quantize(200); p.save(os.path.join(out, 'bmp8.bmp')); truth('bmp8', p)
b1 = rgb.convert('1'); b1.save(os.path.join(out, 'bmp1.bmp')); truth('bmp1', b1)
def bmp(name, w, h, bpp, comp, pixels, pal=None, topdown=False, masks=None):
    hdr = 40 + (12 if masks else 0)
    paln = len(pal) if pal else 0
    off = 14 + hdr + paln * 4
    data = pixels
    f = b'BM' + struct.pack('<IHHI', off + len(data), 0, 0, off)
    f += struct.pack('<IiiHHIIiiII', 40, w, -h if topdown else h, 1, bpp, comp, len(data), 2835, 2835, paln, 0)
    if masks: f += struct.pack('<III', *masks)
    if pal: f += b''.join(struct.pack('<BBBB', c[2], c[1], c[0], 0) for c in pal)
    open(os.path.join(out, name + '.bmp'), 'wb').write(f + data)
# top-down 24
rows = []
for y in range(H):
    row = b''.join(bytes((b, g, r)) for (r, g, b) in [rgb.getpixel((x, y)) for x in range(W)]); row += b'\0' * ((4 - len(row) % 4) % 4); rows.append(row)
bmp('bmp24_topdown', W, H, 24, 0, b''.join(rows), topdown=True); truth('bmp24_topdown', rgb)
# 16-bit 565 bitfields, bottom-up
rows = []
for y in reversed(range(H)):
    row = b''.join(struct.pack('<H', ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)) for (r, g, b) in [rgb.getpixel((x, y)) for x in range(W)]); row += b'\0' * ((4 - len(row) % 4) % 4); rows.append(row)
bmp('bmp16_565', W, H, 16, 3, b''.join(rows), masks=(0xF800, 0x07E0, 0x001F))
t = Image.new('RGB', (W, H)); t.putdata([(((r >> 3) << 3) | (r >> 5), ((g >> 2) << 2) | (g >> 6), ((b >> 3) << 3) | (b >> 5)) for (r, g, b) in rgb.getdata()]); truth('bmp16_565', t)
# RLE8: encode the palette image row by row, bottom-up, with runs + absolute runs
pal = (p.getpalette() or []) + [0]*768; palc = [tuple(pal[i*3:i*3+3]) for i in range(256)]
idx = list(p.getdata()); enc = b''
for y in reversed(range(H)):
    row = idx[y*W:(y+1)*W]; x = 0
    while x < W:
        n = 1
        while x + n < W and row[x+n] == row[x] and n < 255: n += 1
        if n >= 2 or x % 7 == 0: enc += bytes((n, row[x])); x += n
        else:
            m = 1
            while x + m < W and m < 6 and (x + m + 1 >= W or row[x+m+1] != row[x+m]): m += 1
            if m < 3: enc += b''.join(bytes((1, row[x+i])) for i in range(m))
            else: enc += bytes((0, m)) + bytes(row[x:x+m]) + (b'\0' if m % 2 else b'')
            x += m
    enc += b'\0\0'
enc += b'\0\1'
bmp('bmp8_rle', W, H, 8, 1, enc, pal=palc); truth('bmp8_rle', p)
# GIF: palette, transparency, interlaced
p.save(os.path.join(out, 'gif8.gif')); truth('gif8', p)
pt = rgba.quantize(64); pt.save(os.path.join(out, 'gif_trns.gif'), transparency=3)
tt = pt.convert('RGB').convert('RGBA'); tp = tt.load(); pi = pt.load()
for y in range(H):
    for x in range(W):
        if pi[x, y] == 3: tp[x, y] = (0, 0, 0, 0)
truth('gif_trns', tt)
p.save(os.path.join(out, 'gif8_interlaced.gif'), interlace=True); truth('gif8_interlaced', p)
# TGA: 24 / 32 / grey, RLE and raw, both origins
rgb.save(os.path.join(out, 'tga24.tga')); truth('tga24', rgb)
rgba.save(os.path.join(out, 'tga32.tga')); truth('tga32', rgba)
L.save(os.path.join(out, 'tga8.tga')); truth('tga8', L)
rgb.save(os.path.join(out, 'tga24_rle.tga'), rle=True); truth('tga24_rle', rgb)
rgba.save(os.path.join(out, 'tga32_rle.tga'), rle=True); truth('tga32_rle', rgba)
rgb.save(os.path.join(out, 'tga24_topleft.tga'), orientation=1); truth('tga24_topleft', rgb)
# JPEG: baseline + progressive + grey (truth = Pillow's own decode, compared with a tolerance)
smooth = Image.new('RGB', (64, 48)); smooth.putdata([((x*4) & 255, (y*5) & 255, ((x+y)*3) & 255) for y in range(48) for x in range(64)])
smooth.save(os.path.join(out, 'jpg_base.jpg'), quality=90); truth('jpg_base', Image.open(os.path.join(out, 'jpg_base.jpg')))
smooth.save(os.path.join(out, 'jpg_prog.jpg'), quality=85, progressive=True); truth('jpg_prog', Image.open(os.path.join(out, 'jpg_prog.jpg')))
smooth.convert('L').save(os.path.join(out, 'jpg_gray.jpg'), quality=90); truth('jpg_gray', Image.open(os.path.join(out, 'jpg_gray.jpg')))
smooth.save(os.path.join(out, 'jpg_444.jpg'), quality=95, subsampling=0); truth('jpg_444', Image.open(os.path.join(out, 'jpg_444.jpg')))
print('wrote', len([f for f in os.listdir(out) if not f.endswith('.rgba')]), 'reference images to', out)
