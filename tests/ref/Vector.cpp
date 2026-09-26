struct point
{
  int x;
  int y;
};

inline int abs(int x)
{
  int f = (int)(x >= 0);
  return x * f + ((x ^ 0xffffffff) - 1) * (1 - f);
}

extern "C" __declspec(dllexport) void __stdcall DRAW_DOTLINE(int* dest, int w, point* p1, point* p2, int dotstep, int col, int xor)
{
  point* p;
  int x, y, d;
  p1->x = (p1->x<<16) + 32767;
  p1->y = (p1->y<<16) + 32767;
  p2->x = (p2->x<<16) + 32767;
  p2->y = (p2->y<<16) + 32767;
  if (abs(p2->x - p1->x) > abs(p2->y - p1->y))
  {
    if(p1->x > p2->x){p=p1; p1=p2; p2=p;}
    x = p1->x>>16;
    y = p1->y;
    d = ((int)((((_int64)(p2->y - p1->y))<<16)/(p2->x - p1->x))) * dotstep;
    if (xor == 0)
    {
      while(x <= p2->x>>16)
      {
        *(dest+x+(y>>16)*w) = col;
        x += dotstep;
        y += d;
      }
    }
    else
    {
      while(x <= p2->x>>16)
      {
        *(dest+x+(y>>16)*w) ^= col;
        x += dotstep;
        y += d;
      }
    }
  }
  else
  {
    if (p2->y == p1->y) return;
    if(p1->y > p2->y){p=p1; p1=p2; p2=p;}
    x = p1->x;
    y = p1->y>>16;
    d = ((int)((((_int64)(p2->x - p1->x))<<16)/(p2->y - p1->y))) * dotstep;
    if (xor == 0)
    {
      while(y <= p2->y>>16)
      {
        *(dest+(x>>16)+y*w) = col;
        x += d;
        y += dotstep;
      }
    }
    else
    {
      while(y <= p2->y>>16)
      {
        *(dest+(x>>16)+y*w) ^= col;
        x += d;
        y += dotstep;
      }
    }
  }
  return;
}
