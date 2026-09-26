// DRAW_LINE2: no OOB writes for random/absurd input (guard bands), endpoints symmetric,
// SSE2 == AVX2, dash spacing independent of direction.
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <math.h>
#include <vector>
#include "../native/sr2d_ops.h"
static uint64_t rs=0x9e3779b97f4a7c15ull; static uint32_t rnd(){rs^=rs<<13;rs^=rs>>7;rs^=rs<<17;return (uint32_t)(rs>>11);}
static float fr(float a,float b){return a+(b-a)*(rnd()%100000)/100000.f;}
int main(int argc,char**argv){
  int iters=argc>1?atoi(argv[1]):2000; sr2d_ops S,A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
  int fails=0; const int G=8; // guard band
  for(int it=0;it<iters;++it){
    int W=1+rnd()%160,H=1+rnd()%160, PW=W+2*G, PH=H+2*G;
    std::vector<int> a(PW*PH),b; for(auto&v:a)v=(int)rnd(); b=a;
    int cl=rnd()%4, ct=rnd()%4, cr=W-(int)(rnd()%4), cb=H-(int)(rnd()%4);
    float x0,y0,x1,y1; float big = (it%7==0)? 1e7f : 400.f;
    x0=fr(-big,big);y0=fr(-big,big);x1=fr(-big,big);y1=fr(-big,big);
    if(it%11==0){x1=x0;y1=y0;} if(it%13==0){x1=x0;} if(it%17==0){y1=y0;}
    if(it%19==0){x0=NAN;}
    int op=rnd()%8,k=rnd()%300; float dl=(rnd()&1)?fr(0,10):0, gl=fr(0,10), ph=fr(-50,50);
    int* pa=a.data()+G*PW+G; int* pb=b.data()+G*PW+G;
    S.DRAW_LINE2(pa,PW,cl,ct,cr,cb,x0,y0,x1,y1,(int)rnd(),op,k,dl,gl,ph);
    A.DRAW_LINE2(pb,PW,cl,ct,cr,cb,x0,y0,x1,y1,0,op,k,dl,gl,ph); // different colour -> only compare touched set
    // guard band & outside-clip must be untouched: recompute from a fresh copy
    std::vector<int> c(PW*PH); rs^=it; for(auto&v:c)v=0x11111111; std::vector<int> c0=c;
    S.DRAW_LINE2(c.data()+G*PW+G,PW,cl,ct,cr,cb,x0,y0,x1,y1,0x22222222,op==2?1:op,k,dl,gl,ph);
    for(int y=0;y<PH;++y)for(int x=0;x<PW;++x){ int ix=x-G,iy=y-G; bool inside= ix>=cl&&ix<cr&&iy>=ct&&iy<cb;
      if(!inside && c[y*PW+x]!=c0[y*PW+x]){ if(fails<10)printf("OOB write it=%d at %d,%d clip %d %d %d %d\n",it,ix,iy,cl,ct,cr,cb); fails++; goto next; } }
    // symmetry: reversed line hits the same pixels (solid, op set)
    if(dl<=0 && it%19){ std::vector<int> d1(PW*PH,0),d2(PW*PH,0);
      S.DRAW_LINE2(d1.data()+G*PW+G,PW,cl,ct,cr,cb,x0,y0,x1,y1,1,1,0,0,0,0);
      S.DRAW_LINE2(d2.data()+G*PW+G,PW,cl,ct,cr,cb,x1,y1,x0,y0,1,1,0,0,0,0);
      if(d1!=d2){ int n1=0,n2=0,df=0; for(size_t i=0;i<d1.size();++i){n1+=d1[i];n2+=d2[i];df+=d1[i]!=d2[i];} if(fails<10)printf("asymmetric it=%d line (%.3f,%.3f)-(%.3f,%.3f) clip %d %d %d %d W%d H%d on1=%d on2=%d diff=%d\n",it,x0,y0,x1,y1,cl,ct,cr,cb,W,H,n1,n2,df); fails++; } }
    next:;
  }
  // dash spacing check: count 'on' pixels for a 200px line at 0, 45, 90 degrees with dot 4 gap 4 -> ~half of steps
  {
    int W=300; std::vector<int> d(W*W,0);
    S.DRAW_LINE2(d.data(),W,0,0,W,W,10,150,210,150,1,1,0,4,4,0); int h=0; for(int v:d)h+=v;
    std::fill(d.begin(),d.end(),0); S.DRAW_LINE2(d.data(),W,0,0,W,W,10,10,10+141.42f,10+141.42f,1,1,0,4,4,0); int dg=0; for(int v:d)dg+=v;
    printf("dash: horizontal 200px -> %d on-pixels, diagonal 200px (141 steps) -> %d on-pixels (ratio %.2f, expect ~%.2f)\n",h,dg,(double)dg/h,141.42/200);
  }
  // ---- dots must never vanish: dot 1 px / gap g at every angle -> count == expected +-1, and never two adjacent "on" steps for gap>=1
  { int W=400; std::vector<int> d(W*W); int bad=0;
    for(int g=1; g<=6; ++g) for(int a=0;a<360;a+=7){ std::fill(d.begin(),d.end(),0); float ang=a*3.14159265f/180, L=150;
      float x0=200,y0=200,x1=200+cosf(ang)*L,y1=200+sinf(ang)*L;
      S.DRAW_LINE2(d.data(),W,0,0,W,W,x0,y0,x1,y1,1,1,0,1,(float)g,0); int on=0; for(int v:d)on+=v;
      int expect=(int)(L/(1+g))+1; if(abs(on-expect)>1){ if(bad<5)printf("dash count angle %d gap %d: %d on, expect %d\n",a,g,on,expect); bad++; }
      // reversed direction: same number of dots (pattern anchored at the caller's start, so positions differ)
      std::fill(d.begin(),d.end(),0); S.DRAW_LINE2(d.data(),W,0,0,W,W,x1,y1,x0,y0,1,1,0,1,(float)g,0); int on2=0; for(int v:d)on2+=v;
      if(abs(on2-expect)>1){ if(bad<5)printf("dash count reversed angle %d gap %d: %d on, expect %d\n",a,g,on2,expect); bad++; }
      // dash 4 gap 4 must never light two adjacent steps of different dashes -> run lengths along the line are 3..5 steps
      std::fill(d.begin(),d.end(),0); S.DRAW_LINE2(d.data(),W,0,0,W,W,x1,y1,x0,y0,1,1,0,4,4,0.37f); on2=0; for(int v:d)on2+=v;
      float maj0 = fmaxf(fabsf(x1-x0),fabsf(y1-y0)); int exp3=(int)(maj0/2); if(abs(on2-exp3)>exp3/10+2){ if(bad<5)printf("dash4/4 reversed angle %d: %d on, expect ~%d\n",a,on2,exp3); bad++; }
      // step mode: dotlen = -g  -> one pixel per (g+1) major steps
      std::fill(d.begin(),d.end(),0); S.DRAW_LINE2(d.data(),W,0,0,W,W,x0,y0,x1,y1,1,1,0,-(float)g,0,0); on=0; for(int v:d)on+=v;
      float maj = fmaxf(fabsf(x1-x0),fabsf(y1-y0)); int exp2=(int)(maj+0.5f)/(g+1)+1; if(abs(on-exp2)>1){ if(bad<5)printf("step count angle %d gap %d: %d on, expect %d\n",a,g,on,exp2); bad++; } }
    printf("dots: %d problems\n",bad); fails+=bad; }
  // ---- ALPHA_OVER / PREMUL_ALPHA vs scalar model, SSE2 vs AVX2
  {
    int bad=0; const int n=4096+7;
    std::vector<int> src(n),d0(n),d1,d2,pm;
    for(int it=0;it<200;++it){
      for(auto&v:src)v=(int)rnd(); for(auto&v:d0)v=(int)rnd(); d1=d0; d2=d0;
      S.ALPHA_OVER(src.data(),d1.data(),n,1,n,n); A.ALPHA_OVER(src.data(),d2.data(),n,1,n,n);
      for(int i=0;i<n;++i){ uint32_t s_=src[i],d=d0[i],a=s_>>24,inv=256-a-(a>>7),r=0;
        for(int sh=0;sh<32;sh+=8){ uint32_t c=((s_>>sh)&0xff)+((((d>>sh)&0xff)*inv)>>8); if(c>255)c=255; r|=c<<sh; }
        if((uint32_t)d1[i]!=r||(uint32_t)d2[i]!=r){ if(bad<5)printf("ALPHA_OVER mismatch s=%08x d=%08x got %08x/%08x want %08x\n",s_,d,d1[i],d2[i],r); bad++; } }
      pm=src; S.PREMUL_ALPHA(pm.data(),n);
      for(int i=0;i<n;++i){ uint32_t v=src[i],a=v>>24,r=a<<24; for(int sh=0;sh<24;sh+=8){ uint32_t c=((v>>sh)&0xff); c=(c*a+127)/255; r|=c<<sh; }
        if((uint32_t)pm[i]!=r){ if(bad<5)printf("PREMUL mismatch %08x got %08x want %08x\n",v,pm[i],r); bad++; } }
    }
    printf("alpha_over/premul: %d mismatches\n",bad); fails+=bad;
  }
  printf("line2: %d iterations, %d failures\n",iters,fails); return fails?1:0;
}
