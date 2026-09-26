#include <malloc.h>

#define byte unsigned char
#define uint unsigned int

extern "C" __declspec(dllexport) void __stdcall ADD_COLOR_KEY(int* src, int w, int h, int cKey)
{
	int k = cKey & 0xffffff;
	for (int y = 0; y < h; y++)
	{
		for (int x = 0; x < w; x++)
		{
			int c = src[y * w + x] & 0xffffff;
			if (c == k) src[y * w + x] = c;
		}
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall DRAW_ROT_AA(int* src, int* dest, int L, int T, int R, int B, int sx, int sy, int dx, int dy, int sw, int sh, int dw, int SinA, int CosA)
{
	int ssx = (sx << 16) - (dx - L) * CosA;// - 0x8000;
	int ssy = (sy << 16) - (dx - L) * SinA;// - 0x8000;
	int addy = T * dw;
	T -= dy;
	B -= dy;
	for (int y = T; y < B; y++)
	{
		int xx = ssx - y * SinA;
		int yy = ssy + y * CosA;
		for (int x = L; x < R; x++)
		{
			int ix = xx >> 16;
			int iy = yy >> 16;
			if ((ix >= -1) && (ix < sw) && (iy >= -1) && (iy < sh))
			{
				int tx = xx & 0xFFFF;
				int ty = yy & 0xFFFF;
				int c11, c21, c12, c22;

				if ((ix == -1) || (iy == -1))
					c11 = dest[addy + x];
				else
          c11 = src[sw * iy + ix];

				if ((ix == sw - 1) || (iy == -1))
					c21 = dest[addy + x];
				else
          c21 = src[sw * iy + ix + 1];

				if ((ix == -1) || (iy == sh - 1))
					c12 = dest[addy + x];
				else
          c12 = src[sw * (iy + 1) + ix];

				if ((ix == sw - 1) || (iy == sh - 1))
					c22 = dest[addy + x];
				else
          c22 = src[sw * (iy + 1) + ix + 1];

        *((byte*)&dest[addy + x] + 0) =((*((byte*)&c11 + 0) * (0x10000 - tx) >> 12) * (0x10000 - ty) +
                                        (*((byte*)&c21 + 0) *            tx  >> 12) * (0x10000 - ty) +
                                        (*((byte*)&c12 + 0) * (0x10000 - tx) >> 12) *            ty  +
                                        (*((byte*)&c22 + 0) *            tx  >> 12) *            ty) >> 20;

        *((byte*)&dest[addy + x] + 1) =((*((byte*)&c11 + 1) * (0x10000 - tx) >> 12) * (0x10000 - ty) +
                                        (*((byte*)&c21 + 1) *            tx  >> 12) * (0x10000 - ty) +
                                        (*((byte*)&c12 + 1) * (0x10000 - tx) >> 12) *            ty  +
                                        (*((byte*)&c22 + 1) *            tx  >> 12) *            ty) >> 20;

        *((byte*)&dest[addy + x] + 2) =((*((byte*)&c11 + 2) * (0x10000 - tx) >> 12) * (0x10000 - ty) +
                                        (*((byte*)&c21 + 2) *            tx  >> 12) * (0x10000 - ty) +
                                        (*((byte*)&c12 + 2) * (0x10000 - tx) >> 12) *            ty  +
                                        (*((byte*)&c22 + 2) *            tx  >> 12) *            ty) >> 20;

        *((byte*)&dest[addy + x] + 3) =((*((byte*)&c11 + 3) * (0x10000 - tx) >> 12) * (0x10000 - ty) +
                                        (*((byte*)&c21 + 3) *            tx  >> 12) * (0x10000 - ty) +
                                        (*((byte*)&c12 + 3) * (0x10000 - tx) >> 12) *            ty  +
                                        (*((byte*)&c22 + 3) *            tx  >> 12) *            ty) >> 20;
			}
      xx += CosA;
      yy += SinA;
		}
		addy += dw;
	}
}

extern "C" __declspec(dllexport) void __stdcall DRAW_ROT(int* src, int* dest, int L, int T, int R, int B, int sx, int sy, int dx, int dy, int sw, int sh, int dw, int SinA, int CosA)
{
	int ssx = (sx << 16) - (dx - L) * CosA;
	int ssy = (sy << 16) - (dx - L) * SinA;
	int ssw = sw << 16;
	int ssh = sh << 16;
	int addy = T * dw;
	T -= dy;
	B -= dy;
	for (int y = T; y < B; y++)
	{
		int xx = ssx - y * SinA;
		int yy = ssy + y * CosA;
		for (int x = L; x < R; x++)
		{
			if ((xx >= 0) && (xx < ssw) && (yy >= 0) && (yy < ssh))
			{
				dest[addy + x] = src[sw * (yy >> 16) + (xx >> 16)];
			}
      xx += CosA;
      yy += SinA;
		}
		addy += dw;
	}
}

extern "C" __declspec(dllexport) void __stdcall FLIP_Y_ROT_CCW(int* src, int* dest, int w, int h)
{
	for (int y = 0; y < h; y++)
	{
		for (int x = 0; x < w; x++)
			dest[(w - x - 1) * h + h - y - 1] = src[y * w + x];
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall FLIP_X_ROT_CCW(int* src, int* dest, int w, int h)
{
	for (int y = 0; y < h; y++)
	{
		for (int x = 0; x < w; x++)
			dest[x * h + y] = src[y * w + x];
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall ROT_CW(int* src, int* dest, int w, int h)
{
	for (int y = 0; y < h; y++)
	{
		for (int x = 0; x < w; x++)
			dest[(w - x - 1) * h + y] = src[y * w + x];
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall ROT_CCW(int* src, int* dest, int w, int h)
{
	for (int y = 0; y < h; y++)
	{
		for (int x = 0; x < w; x++)
			dest[x * h + h - y - 1] = src[y * w + x];
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall FLIP_XY(int* src, int* dest, int w, int h)
{
	for (int y = 0; y < h * w; y += w)
	{
		for (int x = 0; x < w; x++)
			dest[y + x] = src[h * w - y - x - 1];
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall FLIP_Y(int* src, int* dest, int w, int h)
{
	for (int y = 0; y < h * w; y += w)
	{
		for (int x = 0; x < w; x++)
			dest[y + x] = src[(h - 1) * w - y + x];
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall FLIP_X(int* src, int* dest, int w, int h)
{
	for (int y = 0; y < h * w; y += w)
	{
		for (int x = 0; x < w; x++)
			dest[y + x] = src[y + w - x - 1];
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall RESIZE(uint* src, uint* dest, uint ws, uint hs, uint wd, uint hd)
{
	uint xx, yy;
	uint x, y;
	uint ixx, iyy;
	uint ix, iy;
	uint cx, cy;
	uint cxy, cc;
	uint pin, pout, p;
	uint b, g, r, a;
	uint *ikx, *iky;
	uint *kx, *ky;

	cx = (ws - 1) / wd + 2;
  cy = (hs - 1) / hd + 2;
  x = cx * wd;
  y = cy * hd;

	ikx = (uint *)_alloca(x * 4);
	iky = (uint *)_alloca(y * 4);
	kx = (uint *)_alloca(x * 4);
	ky = (uint *)_alloca(y * 4);

  for (xx = 0; xx < x; xx++)
	{
		ikx[xx] = 0;
		kx[xx] = 0;
	}

  for (yy = 0; yy < y; yy++)
	{
		iky[yy] = 0;
		ky[yy] = 0;
	}

  if (ws >= wd)
	{
    cxy = ws;
		pin = 0; pout = 1; p = 0;
		for(;;)
		{
      cc = pout * ws - pin * wd;
      if (cc >= wd)
			{
        kx[p] = wd;
			}
      else
			{
        kx[p] = cc;
        ikx[p] = pin;
        p = pout * cx;
        pout += 1;
        kx[p] = wd - cc;
			}
      ikx[p] = pin;
      pin += 1;
      if (pin >= ws) break;
      p += 1;
		}
	}
	else
	{
    cxy = wd;
    for (x = 0; x < wd; x++)
		{
      p = x * 2;
			kx[p + 1] = x * (ws - 1) % (wd - 1);
      ikx[p] = x * (ws - 1) / (wd - 1);
      kx[p] = wd - kx[p + 1];
      ikx[p + 1] = (ikx[p] + 1) % ws;
		}
	}

  if (hs >= hd)
	{
    cxy *= hs;
		pin = 0; pout = 1; p = 0;
		for(;;)
		{
      cc = pout * hs - pin * hd;
      if (cc >= hd)
			{
        ky[p] = hd;
			}
      else
			{
        ky[p] = cc;
        iky[p] = pin;
        p = pout * cy;
        pout += 1;
        ky[p] = hd - cc;
			}
      iky[p] = pin;
      pin += 1;
      if (pin >= hs) break;
      p += 1;
		}
	}
	else
	{
    cxy *= hd;
    for (y = 0; y < hd; y++)
		{
      p = y * 2;
      ky[p + 1] = y * (hs - 1) % (hd - 1);
      iky[p] = y * (hs - 1) / (hd - 1);
      ky[p] = hd - ky[p + 1];
      iky[p + 1] = (iky[p] + 1) % hs;
		}
	}

  iyy = 0;
  for (yy = 0; yy < hd; yy++)
	{
    ixx = 0;
		for (xx = 0; xx < wd; xx++)
		{
			b = g = r = a = 0;
      iy = iyy;
			for (y = 1; y <= cy; y++)
			{
        ix = ixx;
				for (x = 1; x <= cx; x++)
				{
					cc = src[ikx[ix] + iky[iy] * ws];
					b += (cc & 0xff) * kx[ix] * ky[iy];
					g += ((cc & 0xff00) >> 8) * kx[ix] * ky[iy];
					r += ((cc & 0xff0000) >> 16) * kx[ix] * ky[iy];
					a += ((cc & 0xff000000) >> 24) * kx[ix] * ky[iy];
          ix += 1;
				}
        iy += 1;
			}
			dest[xx + yy * wd] = b / cxy
												+ (g / cxy << 8)
												+ (r / cxy << 16)
												+ (a / cxy << 24);
      ixx += cx;
		}
    iyy += cy;
	}
	return;
}
