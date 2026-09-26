#include <malloc.h>

#define byte unsigned char
#define uint unsigned int

inline int Clamp(int v)
{
  return v * (int)(v > 0) - (int)(v > 255) * (v - 255);
}

extern "C" __declspec(dllexport) void __stdcall MASK_DPBM_POINT(int* src, int* dst, int* mask, int maskex, int w, int h, int ws, int wd, int wm, int lx, int ly, int lz, int br, int notm)
{
	int c;
	int r, g, b;
	int ulz;
	if (lz < 0)
		ulz = -lz;
	else
		ulz = lz;

	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
					r = lx - x;
					g = ly - y;
					b = ulz * br / (r * r + g * g + ulz * ulz);
					r = r * b;
					g = g * b;
					b = lz * b;

					c = (b * ((src[x + y * ws] & 0xff) - 0x80) + g * (((src[x + y * ws] & 0xff00) >> 8) - 0x80) + r * (((src[x + y * ws] & 0xff0000) >> 16) - 0x80)) >> 19;
					if (c < 0)
						c = 0;
					else if(c > 255)
						c = 0xffffff;
					else
						c = c * 0x10101;
					dst[x + y * wd] = c;
				}
			}
		}
	}
	else
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) == 0)
				{
					r = lx - x;
					g = ly - y;
					b = ulz * br / (r * r + g * g + ulz * ulz);
					r = r * b;
					g = g * b;
					b = lz * b;

					c = (b * ((src[x + y * ws] & 0xff) - 0x80) + g * (((src[x + y * ws] & 0xff00) >> 8) - 0x80) + r * (((src[x + y * ws] & 0xff0000) >> 16) - 0x80)) >> 19;
					if (c < 0)
						c = 0;
					else if(c > 255)
						c = 0xffffff;
					else
						c = c * 0x10101;
					dst[x + y * wd] = c;
				}
			}
		}
	}
}

extern "C" __declspec(dllexport) void __stdcall DPBM_POINT(int* src, int* dst, int w, int h, int ws, int wd, int lx, int ly, int lz, int br)
{
	int c;
	int r, g, b;
	int ulz;
	int *tabl;
	if (lz < 0)
		ulz = -lz;
	else
		ulz = lz;

	tabl = (int *)_alloca(2048 * 4);
	for (int x = 0; x < 1024; x++)					tabl[x] = 0;
	for (int x = 1024; x < 1024 + 256; x++)	tabl[x] = (x - 1024) * 0x10101;
	for (int x = 1024 + 256; x < 2048; x++)	tabl[x] = 0xffffff;

	for (int y = 0; y < h; y++)
	{
		for (int x = 0; x < w; x++)
		{
      r = lx - x;
      g = ly - y;
      b = ulz * br / (r * r + g * g + ulz * ulz);
      r = r * b;
      g = g * b;
      b = lz * b;

			c = (b * ((src[x + y * ws] & 0xff) - 0x80) + g * (((src[x + y * ws] & 0xff00) >> 8) - 0x80) + r * (((src[x + y * ws] & 0xff0000) >> 16) - 0x80)) >> 19;

			dst[x + y * wd] = tabl[c + 1024];
		}
	}
}

extern "C" __declspec(dllexport) void __stdcall MASK_EBM(byte* src, int* dst, int* mask, int* cmap, int maskex, int w, int h, int ws, int wd, int wm, int wc, int hc, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
					int g = *(src + (x + y * ws) * 4 + 1);
					int r = *(src + (x + y * ws) * 4 + 2);
					*(dst + x + y * wd) = *(cmap + (r * wc >> 8) + (g * hc >> 8) * wc);
				}
			}
		}
	}
	else
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) == 0)
				{
					int g = *(src + (x + y * ws) * 4 + 1);
					int r = *(src + (x + y * ws) * 4 + 2);
					*(dst + x + y * wd) = *(cmap + (r * wc >> 8) + (g * hc >> 8) * wc);
				}
			}
		}
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall MASK_EBM_EX(byte* src, int* dst, int* mask, int* cmap, int maskex, int w, int h, int ws, int wd, int wm, int wc, int hc, int notm, int xx, int yy, int hd)
{
	int dg = 0x1000000 / hd;
	int dr = 0x1000000 / wd;
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
					int g = (int)*((byte*)src + (x + y * ws) * 4 + 1) + (((y + yy) * dg) >> 16) - 128;
					int r = (int)*((byte*)src + (x + y * ws) * 4 + 2) + (((x + xx) * dr) >> 16) - 128;
					*(dst + x + y * wd) = *(cmap + ((r & 255) * wc >> 8) + ((g & 255) * hc >> 8) * wc);
				}
			}
		}
	}
	else
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) == 0)
				{
					int g = (int)*((byte*)src + (x + y * ws) * 4 + 1) + (((y + yy) * dg) >> 16) - 128;
					int r = (int)*((byte*)src + (x + y * ws) * 4 + 2) + (((x + xx) * dr) >> 16) - 128;
					*(dst + x + y * wd) = *(cmap + ((r & 255) * wc >> 8) + ((g & 255) * hc >> 8) * wc);
				}
			}
		}
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall EBM_(int* src, int* dst, int* cmap, int w, int h, int ws, int wd, int wc, int hc)
{
	for (int y = 0; y < h; y++)
	{
		for (int x = 0; x < w; x++)
		{
			int g = *((byte*)src + (x + y * ws) * 4 + 1);
			int r = *((byte*)src + (x + y * ws) * 4 + 2);
			*(dst + x + y * wd) = *(cmap + (r * wc >> 8) + (g * hc >> 8) * wc);
		}
	}
	return;
}

extern "C" __declspec(dllexport) void __stdcall EBM_EX(int* src, int* dst, int* cmap, int w, int h, int ws, int wd, int wc, int hc, int xx, int yy, int hd)
{
	int dg = 0x1000000 / hd;
	int dr = 0x1000000 / wd;
	for (int y = 0; y < h; y++)
	{
		for (int x = 0; x < w; x++)
		{
			int g = (((y + yy) * dg) >> 16) + (int)*((byte*)src + (x + y * ws) * 4 + 1) - 128;
			int r = (((x + xx) * dr) >> 16) + (int)*((byte*)src + (x + y * ws) * 4 + 2) - 128;
			*(dst + x + y * wd) = *(cmap + ((r & 255) * wc >> 8) + ((g & 255) * hc >> 8) * wc);
		}
	}
	return;
}

inline void pDPBM(byte* src, int* dst, byte* c)
{
  *dst = 0x10101 * Clamp(((*(src + 0) - 128) * (*(c + 0) - 128) + (*(src + 1) - 128) * (*(c + 1) - 128) + (*(src + 2) - 128) * (*(c + 2) - 128)) >> 6);
}

extern "C" __declspec(dllexport) void __stdcall DPBM_(int* src, int* dst, int w, int h, int c, int ws, int wd)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      pDPBM ((byte*)(src + y * ws + x), dst + y * wd + x, (byte*)&c);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_DPBM(int* src, int* dst, int* mask, int w, int h, int c, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          pDPBM ((byte*)(src + y * ws + x), dst + y * wd + x, (byte*)&c);
				}
			}
		}
	}
	else
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) == 0)
				{
          pDPBM ((byte*)(src + y * ws + x), dst + y * wd + x, (byte*)&c);
				}
			}
		}
	}
}
