#define byte unsigned char
#include <intrin.h>

inline int UClamp(int v)
{
  int f = (int)(v > 255);
  return v * (1 - f) + (f << 8) - f;
}

inline void ByteLerp(byte* src, byte* dst, byte a)
{
  *dst = (*dst * (256 - a) + *src * a) >> 8;
}

extern "C" __declspec(dllexport) void __stdcall PAINT(int* src, int* dst, int w, int h, int ws, int wd)
{
  for (int y = 0; y < h; y++)
	{
    __movsd((unsigned long*)dst, (unsigned long*)src, w);
		dst += wd;
		src += ws;
	}
}

extern "C" __declspec(dllexport) void __stdcall MASK_PAINT(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          *(dst + y * wd + x) = *(src + y * ws + x);
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
          *(dst + y * wd + x) = *(src + y * ws + x);
				}
			}
		}
	}
}

inline void pMod(int* src, int* dst)
{
  byte* psrc = (byte*)src;
  byte* pdst = (byte*)dst;
  *(pdst + 0) = *(psrc + 0) * *(pdst + 0) >> 8;
  *(pdst + 1) = *(psrc + 1) * *(pdst + 1) >> 8;
  *(pdst + 2) = *(psrc + 2) * *(pdst + 2) >> 8;
  *(pdst + 3) = *(psrc + 3) * *(pdst + 3) >> 8;
}

extern "C" __declspec(dllexport) void __stdcall MOD_(int* src, int* dst, int w, int h, int ws, int wd)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      pMod (src + y * ws + x, dst + y * wd + x);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_MOD(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          pMod (src + y * ws + x, dst + y * wd + x);
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
          pMod (src + y * ws + x, dst + y * wd + x);
				}
			}
		}
	}
}

inline void pMod2X(int* src, int* dst)
{
  byte* psrc = (byte*)src;
  byte* pdst = (byte*)dst;
  *(pdst + 0) = (byte)UClamp(*(psrc + 0) * *(pdst + 0) >> 7);
  *(pdst + 1) = (byte)UClamp(*(psrc + 1) * *(pdst + 1) >> 7);
  *(pdst + 2) = (byte)UClamp(*(psrc + 2) * *(pdst + 2) >> 7);
  *(pdst + 3) = (byte)UClamp(*(psrc + 3) * *(pdst + 3) >> 7);
}

extern "C" __declspec(dllexport) void __stdcall MOD_2X(int* src, int* dst, int w, int h, int ws, int wd)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      pMod2X (src + y * ws + x, dst + y * wd + x);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_MOD_2X(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          pMod2X (src + y * ws + x, dst + y * wd + x);
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
          pMod2X (src + y * ws + x, dst + y * wd + x);
				}
			}
		}
	}
}

inline void pAdd(int* src, int* dst)
{
  byte* psrc = (byte*)src;
  byte* pdst = (byte*)dst;
  *(pdst + 0) = (byte)UClamp(*(psrc + 0) + *(pdst + 0));
  *(pdst + 1) = (byte)UClamp(*(psrc + 1) + *(pdst + 1));
  *(pdst + 2) = (byte)UClamp(*(psrc + 2) + *(pdst + 2));
  *(pdst + 3) = (byte)UClamp(*(psrc + 3) + *(pdst + 3));
}

extern "C" __declspec(dllexport) void __stdcall ADD_(int* src, int* dst, int w, int h, int ws, int wd) 
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      pAdd (src + y * ws + x, dst + y * wd + x);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_ADD(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          pAdd (src + y * ws + x, dst + y * wd + x);
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
          pAdd (src + y * ws + x, dst + y * wd + x);
				}
			}
		}
	}
}

inline void pAdd2D(int* src, int* dst)
{
  byte* psrc = (byte*)src;
  byte* pdst = (byte*)dst;
  *(pdst + 0) = (*(psrc + 0) + *(pdst + 0)) >> 1;
  *(pdst + 1) = (*(psrc + 1) + *(pdst + 1)) >> 1;
  *(pdst + 2) = (*(psrc + 2) + *(pdst + 2)) >> 1;
  *(pdst + 3) = (*(psrc + 3) + *(pdst + 3)) >> 1;
}

extern "C" __declspec(dllexport) void __stdcall ADD_2D(int* src, int* dst, int w, int h, int ws, int wd)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      pAdd2D (src + y * ws + x, dst + y * wd + x);
    }
  }
}
extern "C" __declspec(dllexport) void __stdcall MASK_ADD_2D(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          pAdd2D (src + y * ws + x, dst + y * wd + x);
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
          pAdd2D (src + y * ws + x, dst + y * wd + x);
				}
			}
		}
	}
}

extern "C" __declspec(dllexport) void __stdcall ALPHA_T(int* src, int* dst, int w, int h, int ws, int wd)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      int c = *(src + y * ws + x);
      if (c < 0)
      {
        *(dst + y * wd + x) = c;
      }
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_ALPHA_T(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          int c = *(src + y * ws + x);
          if (c < 0)
          {
            *(dst + y * wd + x) = c;
          }
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
          int c = *(src + y * ws + x);
          if (c < 0)
          {
            *(dst + y * wd + x) = c;
          }
				}
			}
		}
	}
}

extern "C" __declspec(dllexport) void __stdcall ALPHA_B(int* src, int* dst, int w, int h, int ws, int wd)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      byte a = *((byte*)(src + y * ws + x) + 3);
      ByteLerp ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0, a);
      ByteLerp ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1, a);
      ByteLerp ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2, a);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_ALPHA_B(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          byte a = *((byte*)(src + y * ws + x) + 3);
          ByteLerp ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0, a);
          ByteLerp ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1, a);
          ByteLerp ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2, a);
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
          byte a = *((byte*)(src + y * ws + x) + 3);
          ByteLerp ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0, a);
          ByteLerp ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1, a);
          ByteLerp ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2, a);
				}
			}
		}
	}
}

inline void ByteMax(byte* src, byte* dst)
{
  byte f = (byte)(*src > *dst);
  *dst = *dst * (1 - f) + *src * f;
}

extern "C" __declspec(dllexport) void __stdcall MAX_(int* src, int* dst, int w, int h, int ws, int wd)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      ByteMax ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0);
      ByteMax ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1);
      ByteMax ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2);
      ByteMax ((byte*)(src + y * ws + x) + 3, (byte*)(dst + y * wd + x) + 3);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_MAX(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          ByteMax ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0);
          ByteMax ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1);
          ByteMax ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2);
          ByteMax ((byte*)(src + y * ws + x) + 3, (byte*)(dst + y * wd + x) + 3);
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
          ByteMax ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0);
          ByteMax ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1);
          ByteMax ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2);
          ByteMax ((byte*)(src + y * ws + x) + 3, (byte*)(dst + y * wd + x) + 3);
				}
			}
		}
	}
}

inline void ByteMin(byte* src, byte* dst)
{
  byte f = (byte)(*src < *dst);
  *dst = *dst * (1 - f) + *src * f;
}

extern "C" __declspec(dllexport) void __stdcall MIN_(int* src, int* dst, int w, int h, int ws, int wd)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      ByteMin ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0);
      ByteMin ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1);
      ByteMin ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2);
      ByteMin ((byte*)(src + y * ws + x) + 3, (byte*)(dst + y * wd + x) + 3);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_MIN(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          ByteMin ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0);
          ByteMin ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1);
          ByteMin ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2);
          ByteMin ((byte*)(src + y * ws + x) + 3, (byte*)(dst + y * wd + x) + 3);
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
          ByteMin ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0);
          ByteMin ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1);
          ByteMin ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2);
          ByteMin ((byte*)(src + y * ws + x) + 3, (byte*)(dst + y * wd + x) + 3);
				}
			}
		}
	}
}

extern "C" __declspec(dllexport) void __stdcall MOVE_BYTE(int* src, int* dst, int w, int h, int ws, int wd, int movesrc, int movedst)
{
  movesrc &= 3;
  movedst &= 3;
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      *((byte*)(dst + y * wd + x) + movedst) = *((byte*)(src + y * ws + x) + movesrc);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_MOVE_BYTE(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int movesrc, int movedst)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          *((byte*)(dst + y * wd + x) + movedst) = *((byte*)(src + y * ws + x) + movesrc);
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
          *((byte*)(dst + y * wd + x) + movedst) = *((byte*)(src + y * ws + x) + movesrc);
				}
			}
		}
	}
}

extern "C" __declspec(dllexport) void __stdcall MOVE_BIT(int* src, int* dst, int w, int h, int ws, int wd, int movesrc, int movedst)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      *(dst + y * wd + x) |= (movedst * (int)((*(src + y * ws + x) & movesrc) != 0));
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_MOVE_BIT(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int movesrc, int movedst)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          *(dst + y * wd + x) |= (movedst * (int)((*(src + y * ws + x) & movesrc) != 0));
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
          *(dst + y * wd + x) |= (movedst * (int)((*(src + y * ws + x) & movesrc) != 0));
				}
			}
		}
	}
}

extern "C" __declspec(dllexport) void __stdcall BLEND(int* src, int* dst, int w, int h, int ws, int wd, int k)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      ByteLerp ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0, (byte)k);
      ByteLerp ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1, (byte)k);
      ByteLerp ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2, (byte)k);
      ByteLerp ((byte*)(src + y * ws + x) + 3, (byte*)(dst + y * wd + x) + 3, (byte)k);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_BLEND(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int k)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          ByteLerp ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0, (byte)k);
          ByteLerp ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1, (byte)k);
          ByteLerp ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2, (byte)k);
          ByteLerp ((byte*)(src + y * ws + x) + 3, (byte*)(dst + y * wd + x) + 3, (byte)k);
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
          ByteLerp ((byte*)(src + y * ws + x) + 0, (byte*)(dst + y * wd + x) + 0, (byte)k);
          ByteLerp ((byte*)(src + y * ws + x) + 1, (byte*)(dst + y * wd + x) + 1, (byte)k);
          ByteLerp ((byte*)(src + y * ws + x) + 2, (byte*)(dst + y * wd + x) + 2, (byte)k);
          ByteLerp ((byte*)(src + y * ws + x) + 3, (byte*)(dst + y * wd + x) + 3, (byte)k);
				}
			}
		}
	}
}
