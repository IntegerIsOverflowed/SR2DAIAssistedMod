// fuzztest: hostile inputs for the new entry points (DRAW_WARP / DRAW_FX / DRAW_BLUR / DRAW_LINE2 / DRAW_POLY / FLOOD_MASK / FILL_MASK8 / LERP_MASK8):
// NaN / inf / 1e30 coordinates, degenerate quads, 1x1 sprites and canvases, extreme stage parameters.
// Checks: no crash, no out-of-bounds write (canary pads), nothing written outside the clip rect.
// Usage: fuzztest [iterations=6000] [seed=99]. Best run under -fsanitize=address,undefined (see Makefile: make asan).
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include <vector>
#include "../native/sr2d_ops.h"
static uint32_t rng=99; static uint32_t rnd(){rng=rng*1664525u+1013904223u;return rng>>8;}
static float pick(){ static const float bad[]={0.f,-0.f,1e-30f,1e30f,-1e30f,INFINITY,-INFINITY,NAN,65536.f,-65536.f,0.5f,3.f,1e9f}; int k=rnd()%20; if(k<13) return bad[k]; return (float)(int)(rnd()%400)-100+(rnd()%7)*0.125f; }
int main(int argc, char** argv){ int iters = argc > 1 ? atoi(argv[1]) : 6000; if (argc > 2) rng = (uint32_t)atoi(argv[2]); sr2d_ops O[2]; sr2d_fill_ops_sse2(O[0]); sr2d_fill_ops_avx2(O[1]);
 const int PAD=4096;
 for(int it=0;it<iters;it++){
  int sw=1+rnd()%9, sh=1+rnd()%9; if(rnd()%5==0){sw=1+rnd()%300; sh=1+rnd()%300;}
  int dw=1+rnd()%70, dh=1+rnd()%70;
  std::vector<int> src((size_t)sw*sh); for(auto&v:src) v=(int)(rnd()|(rnd()<<24));
  std::vector<int> dst((size_t)dw*dh+2*PAD, 0x5a5a5a5a); int* d=dst.data()+PAD;
  float q[8]; for(int i=0;i<8;i++) q[i]=pick();
  float poly[12]; int np=rnd()%3==0?3+rnd()%4:0; for(int i=0;i<12;i++) poly[i]=pick();
  int cl=rnd()%dw, ct=rnd()%dh, cr=cl+rnd()%(dw-cl+1), cb=ct+rnd()%(dh-ct+1); if(rnd()%10==0){cl=-5;cr=dw+5;} if(rnd()%10==0){ct=-5;cb=dh+5;}
  // clip must be inside the buffer for the kernel contract; our C# always passes lock rect within the sprite, so clamp here
  if(cl<0)cl=0; if(ct<0)ct=0; if(cr>dw)cr=dw; if(cb>dh)cb=dh;
  int op=rnd()%14, flags=rnd()%64, k=rnd()%300-20;
  if(rnd()%3==0) op=32+(int)(rnd()%27)|(int)((rnd()%2)<<9)|(int)((rnd()%2)<<10)|(int)((rnd()%260)<<16);   // a blend mode with random premul flags / opacity
  auto& A=O[rnd()&1];
  A.DRAW_WARP(src.data(),sw,sh,d,dw,cl,ct,cr,cb,q,np?poly:0,np,op,flags&7,k);
  SR2D_FxStage st[4]; int n=rnd()%4;
  for(int i=0;i<n;i++){ SR2D_FxStage s={}; s.kind=1+rnd()%7; s.flags=rnd()%0x800; for(int j=0;j<4;j++) s.i[j]=(int)(rnd()%2000)-1000; if(s.kind==1) s.i[0]=(int)(rnd()%80)-10; for(int j=0;j<8;j++) s.f[j]=pick(); if(s.kind==2){ s.map=src.data(); s.mw=sw; s.mh=sh; if(rnd()%8==0){s.mw=0;} } st[i]=s; }
  A.DRAW_FX(src.data(),sw,sh,d,dw,cl,ct,cr,cb,q,np?poly:0,np,op,flags,k,st,n);
  A.DRAW_BLUR(src.data(),sw,sh,d,dw,cl,ct,cr,cb,(int)(rnd()%200)-100,(int)(rnd()%200)-100,(int)(rnd()%80)-10,op,k,flags,(int)(rnd()%400)-50);
  A.DRAW_LINE2(d,dw,cl,ct,cr,cb,pick(),pick(),pick(),pick(),(int)rnd(),op,k,pick(),pick(),pick());
  { float xy[16]; for(int i=0;i<16;i++) xy[i]=pick(); int cnt[2]={3+(int)(rnd()%3),3}; A.DRAW_POLY(d,dw,cl,ct,cr,cb,xy,cnt,1+(int)(rnd()%2),(int)rnd(),op,k,flags&7); }
  { std::vector<uint8_t> m((size_t)dw*dh+2*PAD, 0x77); uint8_t* mp=m.data()+PAD; int bb[4];
    int fx=(int)(rnd()%(dw+40))-20, fy=(int)(rnd()%(dh+40))-20, tol=(int)(rnd()%400)-50; if(rnd()%10==0) tol=INT32_MAX; if(rnd()%10==0) tol=INT32_MIN;
    int ccl=cl,cct=ct,ccr=cr,ccb=cb; if(rnd()%6==0){ccl=-9;cct=-9;ccr=dw+9;ccb=dh+9;}      // FLOOD_MASK clamps its clip itself
    A.FLOOD_MASK(d,dw,dh,ccl,cct,ccr,ccb,fx,fy,(int)rnd(),tol,(int)(rnd()%64),mp,dw,rnd()&1?bb:0);
    for(int i=0;i<PAD;i++) if(m[i]!=0x77||m[PAD+(size_t)dw*dh+i]!=0x77){ printf("FLOOD_MASK OOB it %d\n",it); return 1; }
    for(int y=0;y<dh;y++)for(int x=0;x<dw;x++){ bool in=x>=cl&&x<cr&&y>=ct&&y<cb; if(!in && ccl==cl && mp[y*dw+x]!=0x77){ printf("FLOOD_MASK CLIP it %d\n",it); return 1; } }
    for(auto& v:m) if(v==0x77) v=(uint8_t)(rnd());                                              // arbitrary mask for the compositors
    A.FILL_MASK8(d,dw,cl,ct,cr,cb,mp,dw,(int)rnd(),op,k);
    if(cr>cl&&cb>ct){ int w=cr-cl<sw?cr-cl:sw, h=cb-ct<sh?cb-ct:sh; A.BLEND_MODE(src.data(),d+ct*dw+cl,w,h,sw,dw,op); std::vector<int> mk((size_t)sw*sh); for(auto&v:mk) v=(int)rnd(); A.MASK_BLEND_MODE(src.data(),d+ct*dw+cl,mk.data(),w,h,1<<(rnd()%32),sw,dw,sw,(int)(rnd()%2),op); }
    if(cr>cl&&cb>ct) A.LERP_MASK8(src.data(),d+ct*dw+cl,mp,cr-cl<sw?cr-cl:sw,cb-ct<sh?cb-ct:sh,sw,dw,dw,(int)(rnd()%3)); }
  for(int i=0;i<PAD;i++) if(dst[i]!=0x5a5a5a5a||dst[PAD+(size_t)dw*dh+i]!=0x5a5a5a5a){ printf("OUT OF BOUNDS WRITE it %d\n",it); return 1; }
  for(int y=0;y<dh;y++)for(int x=0;x<dw;x++){ bool in=x>=cl&&x<cr&&y>=ct&&y<cb; if(!in && d[y*dw+x]!=0x5a5a5a5a){ printf("CLIP VIOLATION it %d at %d,%d\n",it,x,y); return 1; } }
 }
 printf("fuzz: %d iterations ok\n", iters); return 0; }
