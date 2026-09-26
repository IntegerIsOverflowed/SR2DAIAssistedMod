// DRAW_WARP: scalar model vs SSE2 vs AVX2 (exactness + visual sanity), plus timing.
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <math.h>
#include <vector>
#include <chrono>
#include "../native/sr2d_ops.h"

static uint64_t rs = 0x1234567887654321ull;
static uint32_t rnd() { rs ^= rs << 13; rs ^= rs >> 7; rs ^= rs << 17; return (uint32_t)(rs >> 11); }
static float frnd(float lo, float hi) { return lo + (hi - lo) * (rnd() % 100000) / 100000.0f; }

// --- scalar model (same maths as the kernel, float32) ---
struct Hm { float A,B,C,D,E,F,G,H,I; bool proj, ok; };
static Hm make(const float* q, int sw, int sh)
{
    Hm r; r.ok=false; r.proj=false;
    double x0=q[0],y0=q[1],x1=q[2],y1=q[3],x2=q[4],y2=q[5],x3=q[6],y3=q[7];
    double dx1=x1-x2,dx2=x3-x2,dx3=x0-x1+x2-x3,dy1=y1-y2,dy2=y3-y2,dy3=y0-y1+y2-y3;
    double a,b,c,d,e,f,g=0,h=0, scale=0;
    for(int i=0;i<8;++i){double v=fabs(q[i]); if(v>scale)scale=v;}
    double eps=1e-9*(scale+1);
    if(fabs(dx3)<eps&&fabs(dy3)<eps){a=x1-x0;b=x3-x0;c=x0;d=y1-y0;e=y3-y0;f=y0;}
    else{double den=dx1*dy2-dx2*dy1; if(fabs(den)<1e-300)return r; g=(dx3*dy2-dx2*dy3)/den; h=(dx1*dy3-dx3*dy1)/den;
         a=x1-x0+g*x1;b=x3-x0+h*x3;c=x0;d=y1-y0+g*y1;e=y3-y0+h*y3;f=y0;r.proj=true;}
    double A00=e-f*h,A01=c*h-b,A02=b*f-c*e,A10=f*g-d,A11=a-c*g,A12=c*d-a*f,A20=d*h-e*g,A21=b*g-a*h,A22=a*e-b*d;
    if(!r.proj){ if(fabs(A22)<1e-300)return r; A00/=A22;A01/=A22;A02/=A22;A10/=A22;A11/=A22;A12/=A22;A20=A21=0;A22=1;}
    else{ double mx=(x0+x1+x2+x3)*.25,my=(y0+y1+y2+y3)*.25; double dm=A20*mx+A21*my+A22; if(fabs(dm)<1e-300)return r;
          A00/=dm;A01/=dm;A02/=dm;A10/=dm;A11/=dm;A12/=dm;A20/=dm;A21/=dm;A22/=dm;}
    r.A=(float)(A00*sw);r.B=(float)(A01*sw);r.C=(float)(A02*sw);r.D=(float)(A10*sh);r.E=(float)(A11*sh);r.F=(float)(A12*sh);
    r.G=(float)A20;r.H=(float)A21;r.I=(float)A22;r.ok=true;return r;
}
static bool inside(const float* p, int n, double x, double y)
{
    bool in=false;
    for(int i=0,j=n-1;i<n;j=i++){ double yi=p[i*2+1],yj=p[j*2+1];
        if((yi<=y)!=(yj<=y)){ double xi=p[i*2],xj=p[j*2]; double cx=xi+(y-yi)*(xj-xi)/(yj-yi); if(x<cx) in=!in; } }
    return in;
}
static int lerp8(int a,int b,int w){ int r=0; for(int i=0;i<4;++i){int aa=(a>>(8*i))&255,bb=(b>>(8*i))&255; r|=(((aa*(256-w)+bb*w)>>8)&255)<<(8*i);} return r; }
static void ref_warp(const int* src,int sw,int sh,int* dst,int dw,int cl,int ct,int cr,int cb,const float* quad,const float* poly,int np,int bil)
{
    Hm h=make(quad,sw,sh); if(!h.ok) return;
    for(int y=ct;y<cb;++y) for(int x=cl;x<cr;++x)
    {
        double xc=x+.5,yc=y+.5;
        if(!inside(quad,4,xc,yc)) continue;
        if(poly&&np>=3&&!inside(poly,np,xc,yc)) continue;
        float X=(float)x+((float)0+0.5f);
        float rowU=(float)((double)h.B*yc+h.C), rowV=(float)((double)h.E*yc+h.F), rowW=(float)((double)h.H*yc+h.I);
        float u=h.A*X+rowU, v=h.D*X+rowV;
        if(h.proj){ float w=h.G*X+rowW; u=u/w; v=v/w; }
        int c;
        if(!bil){ float uu=fminf(fmaxf(u,0.f),(float)(sw-1)), vv=fminf(fmaxf(v,0.f),(float)(sh-1)); if(uu!=uu)uu=0; if(vv!=vv)vv=0; c=src[(int)vv*sw+(int)uu]; }
        else{ float uf=fminf(fmaxf(u-.5f,0.f),(float)(sw-1)), vf=fminf(fmaxf(v-.5f,0.f),(float)(sh-1)); if(uf!=uf)uf=0; if(vf!=vf)vf=0;
              int iu0=(int)uf,iv0=(int)vf; int wx=(int)((uf-(float)iu0)*256.f), wy=(int)((vf-(float)iv0)*256.f);
              int iu1=iu0+1<sw-1?iu0+1:sw-1, iv1=iv0+1<sh-1?iv0+1:sh-1;
              c=lerp8(lerp8(src[iv0*sw+iu0],src[iv0*sw+iu1],wx),lerp8(src[iv1*sw+iu0],src[iv1*sw+iu1],wx),wy); }
        dst[y*dw+x]=c; // Paint
    }
}

int main(int argc,char**argv)
{
    int iters=argc>1?atoi(argv[1]):400;
    sr2d_ops S,A; sr2d_fill_ops_sse2(S); sr2d_fill_ops_avx2(A);
    int fails=0, checks=0; long badpx=0, totpx=0;
    for(int it=0;it<iters;++it)
    {
        int sw=1+rnd()%128, sh=1+rnd()%128, DW=16+rnd()%200, DH=16+rnd()%200;
        std::vector<int> src(sw*sh); for(auto&v:src)v=(int)rnd();
        std::vector<int> d0(DW*DH); for(auto&v:d0)v=(int)rnd(); std::vector<int> d1=d0,d2=d0;
        float q[8];
        int kind=it%4;
        if(kind==0){ float x=frnd(-50,DW),y=frnd(-50,DH),w=frnd(1,200),h=frnd(1,200); q[0]=x;q[1]=y;q[2]=x+w;q[3]=y;q[4]=x+w;q[5]=y+h;q[6]=x;q[7]=y+h; }
        else if(kind==1){ float cx=frnd(0,DW),cy=frnd(0,DH),w=frnd(1,200),h=frnd(1,200),a=frnd(0,6.283f); float c=cosf(a),s=sinf(a);
            float px[4]={-w/2,w/2,w/2,-w/2},py[4]={-h/2,-h/2,h/2,h/2}; for(int i=0;i<4;++i){q[i*2]=cx+px[i]*c-py[i]*s;q[i*2+1]=cy+px[i]*s+py[i]*c;} }
        else { // random convex-ish quad
            float cx=frnd(0,DW),cy=frnd(0,DH); for(int i=0;i<4;++i){ float a=i*1.5708f+frnd(-.5f,.5f), r=frnd(5,150); q[i*2]=cx+cosf(a)*r; q[i*2+1]=cy+sinf(a)*r; } }
        float poly[16]; int np=0; const float* pp=0;
        if(rnd()%3==0){ np=3+rnd()%6; float cx=frnd(0,DW),cy=frnd(0,DH); for(int i=0;i<np;++i){float a=6.283f*i/np+frnd(-.3f,.3f),r=frnd(5,150);poly[i*2]=cx+cosf(a)*r;poly[i*2+1]=cy+sinf(a)*r;} pp=poly; }
        int bil=rnd()&1;
        int cl=rnd()%8, ct=rnd()%8, cr=DW-rnd()%8, cb=DH-rnd()%8;
        ref_warp(src.data(),sw,sh,d0.data(),DW,cl,ct,cr,cb,q,pp,np,bil);
        S.DRAW_WARP(src.data(),sw,sh,d1.data(),DW,cl,ct,cr,cb,q,pp,np,1,bil,0);
        A.DRAW_WARP(src.data(),sw,sh,d2.data(),DW,cl,ct,cr,cb,q,pp,np,1,bil,0);
        // also make sure nothing outside the clip rect was touched
        long b1=0,b2=0; for(size_t i=0;i<d0.size();++i){ b1+=d0[i]!=d1[i]; b2+=d0[i]!=d2[i]; }
        totpx+=d0.size()*2; badpx+=b1+b2; checks+=2;
        if(b1){ ++fails; if(fails<10) printf("SSE2 diff it=%d kind=%d bil=%d px=%ld/%zu proj=%d\n",it,kind,bil,b1,d0.size(),make(q,sw,sh).proj); }
        if(b2){ ++fails; if(fails<10) printf("AVX2 diff it=%d kind=%d bil=%d px=%ld/%zu proj=%d\n",it,kind,bil,b2,d0.size(),make(q,sw,sh).proj); }
    }
    printf("warp: %d checks, %d with differences, %ld / %ld pixels differ (%.5f%%)\n",checks,fails,badpx,totpx,100.0*badpx/totpx);

    // ---- timing: 512x512 sprite -> 1024x1024 canvas, various transforms
    {
        const int W=512,H=512,CW=1024,CH=1024;
        std::vector<int> s(W*H),d(CW*CH); for(auto&v:s)v=(int)rnd();
        auto now=[]{return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();};
        auto tm=[&](const char*name,const float*q,int op,int fl){ 
            for(int lv=0;lv<2;++lv){ sr2d_ops&o=lv?A:S; o.DRAW_WARP(s.data(),W,H,d.data(),CW,0,0,CW,CH,q,0,0,op,fl,128);
                double best=1e9; for(int k=0;k<3;++k){double t0=now(); for(int i=0;i<10;++i)o.DRAW_WARP(s.data(),W,H,d.data(),CW,0,0,CW,CH,q,0,0,op,fl,128); double t=(now()-t0)/10; if(t<best)best=t;}
                printf("%-34s %s %8.1f us\n",name,lv?"AVX2":"SSE2",best*1e6);} };
        float scale[8]={0,0,1024,0,1024,1024,0,1024};
        float rot[8]; { float c=cosf(.6f),sn=sinf(.6f); float px[4]={-256,256,256,-256},py[4]={-256,-256,256,256}; for(int i=0;i<4;++i){rot[i*2]=512+px[i]*c-py[i]*sn;rot[i*2+1]=512+px[i]*sn+py[i]*c;} }
        float persp[8]={100,50,924,200,800,1000,50,900};
        tm("scale 512->1024 nearest Paint",scale,1,0);
        tm("scale 512->1024 bilinear Paint",scale,1,1);
        tm("scale 512->1024 bilinear AlphaBlend",scale,3,1);
        tm("rotate 512 nearest Paint",rot,1,0);
        tm("rotate 512 bilinear Paint",rot,1,1);
        tm("perspective quad bilinear Paint",persp,1,1);
    }
    return fails?1:0;
}
