#include <windows.h>
#include <intrin.h>

#define byte unsigned char
#define uint unsigned int

#pragma comment(linker,"/MERGE:.rdata=.text")
#pragma comment(linker,"/ENTRY:DllMain")
//#pragma comment(linker,"/NODEFAULTLIB")

BOOL APIENTRY DllMain (HANDLE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
	switch (ul_reason_for_call)
	{
		case DLL_PROCESS_ATTACH:
			break;
		case DLL_THREAD_ATTACH:
			break;
		case DLL_THREAD_DETACH:
			break;
		case DLL_PROCESS_DETACH:
			break;
	}
	return TRUE;
}

extern "C" __declspec(dllexport) void __stdcall MOVSD_(int* src, int* dst, int dwcnt)
{
  __movsd((unsigned long*)dst, (unsigned long*)src, dwcnt);
}

extern "C" __declspec(dllexport) int __stdcall ARGB_(int a, int r, int g, int b)
{
  return (a << 24) | ((r & 0xff) << 16) | ((g & 0xff) << 8) | (b & 0xff);
}

extern "C" __declspec(dllexport) void __stdcall BPP_32TO24(uint* src, byte* dest, int w, int h, int stride)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      *(dest+y*stride+x*3)   = *(src+y*w+x) & 0xff;
      *(dest+y*stride+x*3+1) = (*(src+y*w+x)>>8) & 0xff;
      *(dest+y*stride+x*3+2) = (*(src+y*w+x)>>16) & 0xff;
    }
  }
}

extern "C" __declspec(dllexport) int __stdcall EXP2N(int n)
{
  return 1 << n;
}

extern "C" __declspec(dllexport) void __stdcall CLEAR_C(int* dst, int w, int h, int wd, int c)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      *(dst+y*wd+x) = c;
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_CLEAR_C(int* dest, int* mask, int w, int h, int maskex, int wd, int wm, int c, int notm)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
					dest[x + y * wd] = c;
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
					dest[x + y * wd] = c;
				}
			}
		}
	}
}

inline int Clamp(int v)
{
  return v * (int)(v > 0) - (int)(v > 255) * (v - 255);
}

inline void pMulAdd(byte* src, byte* dst, byte* vmul, byte* vadd)
{
  *(dst + 0) = (byte)Clamp(((*(src + 0) * *(vmul + 0)) >> 7) + *(vadd + 0) * 2 - 256);
  *(dst + 1) = (byte)Clamp(((*(src + 1) * *(vmul + 1)) >> 7) + *(vadd + 1) * 2 - 256);
  *(dst + 2) = (byte)Clamp(((*(src + 2) * *(vmul + 2)) >> 7) + *(vadd + 2) * 2 - 256);
  *(dst + 3) = (byte)Clamp(((*(src + 3) * *(vmul + 3)) >> 7) + *(vadd + 3) * 2 - 256);
}

extern "C" __declspec(dllexport) void __stdcall V_MUL_ADD(int* src, int* dst, int w, int h, int ws, int wd, int vmul, int vadd)
{
  for (int y=0; y<h; y++)
  {
    for (int x=0; x<w; x++)
    {
      pMulAdd((byte*)(src + y * ws + x), (byte*)(dst + y * wd + x) ,(byte*)&vmul, (byte*)&vadd);
    }
  }
}

extern "C" __declspec(dllexport) void __stdcall MASK_V_MUL_ADD(int* src, int* dst, int* mask, int w, int h, int maskex, int ws, int wd, int wm, int notm, int vmul, int vadd)
{
	if (notm == 0)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if ((*(mask + x + y * wm) & maskex) != 0)
				{
          pMulAdd((byte*)(src + y * ws + x), (byte*)(dst + y * wd + x) ,(byte*)&vmul, (byte*)&vadd);
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
          pMulAdd((byte*)(src + y * ws + x), (byte*)(dst + y * wd + x) ,(byte*)&vmul, (byte*)&vadd);
				}
			}
		}
	}
}

extern "C" __declspec(dllexport) int __stdcall MASK_INTERSECT(int* src, int* dst, int w, int h, int ws, int wd, int maskex)
{
	int fn = 0;
	for (int y = 0; y < h; y++)
	{
		for (int x = 0; x < w; x++)
		{
      fn += (int)((*(dst + y * wd + x) & *(src + y * ws + x) & maskex) != 0);
		}
	}
	return fn;
}

extern "C" __declspec(dllexport) void __stdcall CLR_ALPHA(int* dest, int size)
{
  for (int i = 0; i < size; i++)
  {
    dest[i]=dest[i] | 0xff000000;
  }
}
