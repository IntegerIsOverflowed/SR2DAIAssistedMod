// dumps a checksum of noise / turbulence outputs (used to prove the row-table optimisation is bit-exact)
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>
#include "../native/sr2d_ops.h"
static uint64_t fnv(const void* p, size_t n){ const uint8_t* b=(const uint8_t*)p; uint64_t h=1469598103934665603ull; for(size_t i=0;i<n;i++){h^=b[i];h*=1099511628211ull;} return h; }
int main(int argc,char**argv){ sr2d_ops O[2]; sr2d_fill_ops_sse2(O[0]); sr2d_fill_ops_avx2(O[1]);
 const int sw=131,sh=97; std::vector<int> src(sw*sh); uint32_t r=5; for(auto&v:src){r=r*1664525u+1013904223u; v=(int)r;}
 const float scales[]={3.f,5.5f,8.f,11.f,16.f,24.f,40.f,100.f}; const int types[]={SR2D_FX_NOISE,SR2D_FX_TURBULENCE};
 const int sflags[]={0,SR2D_FXF_SAMPLE_NEAREST,SR2D_FXF_SAMPLE_BICUBIC};
 uint64_t total=0;
 for(int lv=0;lv<2;lv++) for(int t=0;t<2;t++) for(float sc: scales) for(int sf: sflags) for(int post=0;post<2;post++){
   std::vector<int> dst(300*200,0); SR2D_FxStage st={}; st.kind=SR2D_FX_DISTORT; st.i[0]=types[t]; st.f[0]=sc; st.f[1]=7.5f; st.f[2]=0.37f; st.flags=sf;
   float q[8]={10.5f,7.25f,10.5f+sw*1.3f,7.25f,10.5f+sw*1.3f,7.25f+sh*1.3f,10.5f,7.25f+sh*1.3f};
   O[lv].DRAW_FX(src.data(),sw,sh,dst.data(),300,0,0,300,200,q,0,0,3,1|(post?SR2D_FX_POST:0),0,&st,1);
   uint64_t h=fnv(dst.data(),dst.size()*4); total=total*31+h;
   if(argc>1) printf("%d %d %g %d %d %016llx\n",lv,t,sc,sf,post,(unsigned long long)h);
 }
 printf("noise checksum %016llx\n",(unsigned long long)total); return 0; }
