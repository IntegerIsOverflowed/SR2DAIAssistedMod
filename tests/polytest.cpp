// DRAW_POLY: exactness of non-AA integer rects, AA coverage, union semantics, no OOB, SSE2 == AVX2.
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <math.h>
#include <vector>
#include <algorithm>
#include <chrono>
#include "../native/sr2d_ops.h"
static uint64_t rs=0x9e3779b97f4a7c15ull; static uint32_t rnd(){rs^=rs<<13;rs^=rs>>7;rs^=rs<<17;return (uint32_t)(rs>>11);}
static float fr(float a,float b){return a+(b-a)*(rnd()%100000)/100000.f;}
int main(int argc,char**argv){
  int iters=argc>1?atoi(argv[1]):1000; sr2d_ops S,A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
  int fails=0;
  // 1. integer rectangle, non-AA, op set == CLEAR_C
  for(int it=0;it<200;++it){
    int W=64,H=64; std::vector<int> a(W*H,0x11111111),b=a;
    int x0=rnd()%70-3,y0=rnd()%70-3,x1=x0+rnd()%40,y1=y0+rnd()%40;
    float xy[8]={(float)x0,(float)y0,(float)x1,(float)y0,(float)x1,(float)y1,(float)x0,(float)y1}; int cnt=4;
    S.DRAW_POLY(a.data(),W,0,0,W,H,xy,&cnt,1,0x22222222,1,0,0);
    int cl=x0<0?0:x0, ct=y0<0?0:y0, cr=x1>W?W:x1, cb=y1>H?H:y1;
    if(cr>cl&&cb>ct) S.CLEAR_C(b.data()+ct*W+cl,cr-cl,cb-ct,W,0x22222222);
    if(a!=b){ if(fails<5)printf("rect mismatch it=%d (%d,%d)-(%d,%d)\n",it,x0,y0,x1,y1); fails++; }
  }
  // 1b. integer rectangle with AA must be identical to non-AA (full coverage inside, none outside)
  for(int it=0;it<200;++it){
    int W=64,H=64; std::vector<int> a(W*H,0),b=a;
    int x0=rnd()%70-3,y0=rnd()%70-3,x1=x0+rnd()%40,y1=y0+rnd()%40;
    float xy[8]={(float)x0,(float)y0,(float)x1,(float)y0,(float)x1,(float)y1,(float)x0,(float)y1}; int cnt=4;
    S.DRAW_POLY(a.data(),W,0,0,W,H,xy,&cnt,1,(int)0xFFFFFFFF,1,0,1|(it&1?4:0));
    S.DRAW_POLY(b.data(),W,0,0,W,H,xy,&cnt,1,(int)0xFFFFFFFF,1,0,0);
    if(a!=b){ if(fails<5)printf("aa int-rect mismatch it=%d\n",it); fails++; }
  }
  // 1c. random convex polygons (HQ AA): total coverage == shoelace area (within 0.5%), clipped-away case too
  for(int it=0;it<300;++it){
    int W=128,H=128; std::vector<int> a(W*H,0);
    int n=3+rnd()%9; std::vector<float> xy; float cx_=fr(20,108),cy_=fr(20,108),r=fr(2,20);
    std::vector<float> ang(n); for(auto&v:ang)v=fr(0,6.2831853f); std::sort(ang.begin(),ang.end());
    for(int i=0;i<n;++i){xy.push_back(cx_+r*cosf(ang[i]));xy.push_back(cy_+r*sinf(ang[i]));}
    double area=0; for(int i=0,j=n-1;i<n;j=i++) area+=(double)xy[j*2]*xy[i*2+1]-(double)xy[i*2]*xy[j*2+1]; area=fabs(area)/2;
    S.DRAW_POLY(a.data(),W,0,0,W,H,xy.data(),&n,1,(int)0xFFFFFFFF,1,0,1|4);
    double sum=0; for(int v:a)sum+=(v&0xff)/255.0;
    if(fabs(sum-area)>0.02*area+0.6){ if(fails<5)printf("convex area it=%d n=%d cov=%.2f area=%.2f\n",it,n,sum,area); fails++; }
  }
  // 2. AA coverage: rect x 10.5..20.25, y 5.25..9 -> column 10 = 50%, col 20 = 25%, row 5 = 75%
  { int W=32,H=16; std::vector<int> a(W*H,0);
    float xy[8]={10.5f,5.25f,20.25f,5.25f,20.25f,9,10.5f,9}; int cnt=4;
    S.DRAW_POLY(a.data(),W,0,0,W,H,xy,&cnt,1,(int)0xFFFFFFFF,1,0,1);
    auto px=[&](int x,int y){return a[y*W+x]&0xff;};
    printf("aa rect: (10,6)=%d expect ~128, (20,6)=%d expect ~64, (15,5)=%d expect ~192, (15,6)=%d expect 255, (15,8)=%d expect 255, (15,9)=%d expect 0\n",px(10,6),px(20,6),px(15,5),px(15,6),px(15,8),px(15,9));
    if(abs(px(10,6)-128)>2||abs(px(20,6)-64)>2||abs(px(15,5)-192)>2||px(15,6)!=255||px(15,9)!=0) fails++;
    // HQ AA (flag 4) on a slanted edge: sum of coverage ~= area
    std::vector<int> c(W*H,0); float tri[6]={2,2,30,2,2,14}; int c3=3;
    S.DRAW_POLY(c.data(),W,0,0,W,H,tri,&c3,1,(int)0xFFFFFFFF,1,0,1|4);
    double sum=0; for(int v:c)sum+=(v&0xff)/255.0; printf("aa triangle: coverage sum %.2f, area %.2f\n",sum,0.5*28*12);
    if(fabs(sum-168)>1.0) fails++;
  }
  // 3. union: two overlapping rects in one call with alpha blend == one big rect blended once
  { int W=40,H=20; std::vector<int> a(W*H,(int)0xFF404040),b=a;
    float xy[16]={2,2,30,2,30,18,2,18,  10,2,38,2,38,18,10,18}; int cnt[2]={4,4};
    S.DRAW_POLY(a.data(),W,0,0,W,H,xy,cnt,2,(int)0x80FF0000,3,0,1);
    float one[8]={2,2,38,2,38,18,2,18}; int c4=4;
    S.DRAW_POLY(b.data(),W,0,0,W,H,one,&c4,1,(int)0x80FF0000,3,0,1);
    if(a!=b){ printf("union mismatch (non-zero winding)\n"); fails++; }
    // even-odd: the overlap must be empty
    std::vector<int> c(W*H,0); S.DRAW_POLY(c.data(),W,0,0,W,H,xy,cnt,2,(int)0xFFFFFFFF,1,0,2);
    if(c[10*W+20]!=0||c[10*W+5]!=(int)0xFFFFFFFF||c[10*W+35]!=(int)0xFFFFFFFF){ printf("even-odd mismatch\n"); fails++; }
  }
  // 4. random polygons: no OOB, SSE2 == AVX2, all ops/flags
  const int G=8;
  for(int it=0;it<iters;++it){
    int W=1+rnd()%120,H=1+rnd()%120,PW=W+2*G,PH=H+2*G;
    std::vector<int> a(PW*PH); for(auto&v:a)v=(int)rnd(); std::vector<int> b=a,a0=a;
    int cl=rnd()%4,ct=rnd()%4,cr=W-(int)(rnd()%4),cb=H-(int)(rnd()%4);
    int nc=1+rnd()%3; std::vector<int> cnt(nc); std::vector<float> xy; float big=(it%9==0)?1e6f:200.f;
    for(int c=0;c<nc;++c){ cnt[c]=3+rnd()%7; for(int i=0;i<cnt[c];++i){xy.push_back(fr(-big,big));xy.push_back(fr(-big,big));} }
    if(it%23==0) xy[0]=NAN;
    int op=1+rnd()%8,k=rnd()%300,flags=rnd()%8,col=(int)rnd();
    S.DRAW_POLY(a.data()+G*PW+G,PW,cl,ct,cr,cb,xy.data(),cnt.data(),nc,col,op,k,flags);
    A.DRAW_POLY(b.data()+G*PW+G,PW,cl,ct,cr,cb,xy.data(),cnt.data(),nc,col,op,k,flags);
    if(a!=b){ if(fails<5)printf("sse2!=avx2 it=%d op=%d flags=%d\n",it,op,flags); fails++; }
    for(int y=0;y<PH;++y)for(int x=0;x<PW;++x){int ix=x-G,iy=y-G; bool in=ix>=cl&&ix<cr&&iy>=ct&&iy<cb; if(!in&&a[y*PW+x]!=a0[y*PW+x]){ if(fails<5)printf("OOB it=%d at %d,%d\n",it,ix,iy); fails++; y=PH; break; }}
  }
  // 5. DRAW_LINE2 skip-end flag: closed square with XOR, every pixel exactly once
  { int W=20,H=20; std::vector<int> a(W*H,0); float p[8]={2,2,15,2,15,15,2,15};
    for(int i=0;i<4;++i) S.DRAW_LINE2(a.data(),W,0,0,W,H,p[i*2],p[i*2+1],p[((i+1)%4)*2],p[((i+1)%4)*2+1],1,2|0x100,0,0,0,0);
    int on=0; for(int v:a)on+=v; printf("xor square perimeter: %d on-pixels, expect %d\n",on,4*13); if(on!=52) fails++; }
  // 6. timing
  { int W=1024,H=1024; std::vector<int> a(W*H,0); std::vector<float> circ; int n=64; for(int i=0;i<n;++i){float t=(float)i/n*6.2831853f; circ.push_back(512+400*cosf(t)); circ.push_back(512+400*sinf(t));}
    for(int mode=0;mode<4;++mode){ int op=mode<2?1:3, fl=(mode&1); auto t0=std::chrono::steady_clock::now(); int reps=20;
      for(int r=0;r<reps;++r) A.DRAW_POLY(a.data(),W,0,0,W,H,circ.data(),&n,1,(int)0x80FF8040,op,0,fl);
      double ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-t0).count()/reps;
      printf("circle r=400 (%.0f kpx) %s %s: %.3f ms\n",3.14159*400*400/1000,op==1?"set":"alphablend",fl?"AA":"noAA",ms); }
  }
  printf("poly: %d failures\n",fails); return fails?1:0;
}
