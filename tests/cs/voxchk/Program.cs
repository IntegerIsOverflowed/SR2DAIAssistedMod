// voxchk - end-to-end check of VoxelGrid / VoxelCamera / Sprite.DrawVoxels on top of the native kernels.
using System; using System.Diagnostics; using System.Drawing;
namespace Sr2d64CSport {
static class P {
  static int fails;
  static void Check(string what, bool ok){ Console.WriteLine((ok?"  ok   ":"  FAIL ")+what); if(!ok) fails++; }
  static int Cmp(Sprite a, Sprite b){ int n=0; for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++) if(a.GetPixel(x,y)!=b.GetPixel(x,y)) n++; return n; }
  static int Painted(Sprite s, int bg){ int n=0; for(int y=0;y<s.Height;y++)for(int x=0;x<s.Width;x++) if(s.GetPixel(x,y)!=bg) n++; return n; }
  static VoxelGrid Terrain(int n, int seed){
    var g = new VoxelGrid(n, n, n/2); var r = new Random(seed);
    g.FromHeightMap((x,y)=> (int)(n/4 + n/8*Math.Sin(x*0.19+y*0.07) + n/10*Math.Cos(y*0.23-x*0.05)) , (x,y,z)=> new Voxel(z%7==6 ? 0xFF6A6A6A : z>n/4+2 ? 0xFF4C9A3C : 0xFF8A5A2C, 0, (byte)(z>n/4+2?1:2)));
    for (int i=0;i<n/2;i++) g.FillSphere(r.Next(n), r.Next(n), n/6+r.Next(n/8), 1.5f+r.Next(3), Voxel.Empty);            // caves
    for (int i=0;i<n/8;i++){ int x=r.Next(n), y=r.Next(n); int z=g.Depth-1; while(z>0 && !g.IsSolid(x,y,z-1)) z--; g.Set(x,y,z,new Voxel(0xFFFFD070, 14, 9)); } // lamps on the surface
    g.FillBox(n/2-3, n/2-3, 0, n/2+3, n/2+3, n/2, new Voxel(0xFFC08040));  // tower
    g.FillBox(n/2-2, n/2-2, 1, n/2+2, n/2+2, n/2-1, Voxel.Empty);          // hollow
    g.Set(n/2, n/2, 2, new Voxel(0xFF40C0FF, 15, 3));                       // blue lamp inside
    g.FillBox(n/2-3, n/2-1, 1, n/2-2, n/2+1, 4, Voxel.Empty);              // door
    return g;
  }
  static unsafe void Main() {
    Console.WriteLine($"SIMD {SR2D.ActiveSimdLevel}");
    const int BG = unchecked((int)0xFF203040);
    // 1) basics
    using (var g = new VoxelGrid(4,3,2)) {
      Check("empty grid", g.ExposedCount==0 && g[1,1,1].IsEmpty);
      g[1,1,0] = new Voxel(0xFFFF0000, emit: 5, material: 7, user: 1234);
      Check("indexer roundtrip", g[1,1,0].Argb==0xFFFF0000 && g[1,1,0].Emit==5 && g[1,1,0].Material==7 && g[1,1,0].User==1234 && g.Voxels()[g.Index(1,1,0)]==g[1,1,0]);
      Check("faces: lone voxel exposes all 6", g.FaceBits(1,1,0)==(0x40|63) && g.ExposedCount==1);
      g[2,1,0] = new Voxel(0xFF00FF00);
      Check("faces: neighbours hide the shared side", (g.FaceBits(1,1,0)&1)==0 && (g.FaceBits(2,1,0)&2)==0);
      Check("light: sky above, emitter colour", g.SkyLightAt(1,1,1)==15 && (g.LightAt(0,1,0)>>16 &15)==4 && g.SkyLightAt(1,1,0)==0);
      bool thrown=false; try { g[4,0,0]=Voxel.Empty; } catch(ArgumentOutOfRangeException){ thrown=true; } Check("indexer bounds", thrown && g.Get(4,0,0).IsEmpty);
      var sizeCheck = sizeof(Voxel)==8 && sizeof(VoxelGrid.Scene)==232; Check("struct sizes (Voxel 8, Scene 232)", sizeCheck);
    }
    // 2) draw through all presets / turns / modes / lighting - nothing outside the lock rect, deterministic, bounds honoured
    using (var g = Terrain(32, 1)) {
      var canvas = new Sprite(320, 240, SR2D.Op.Paint);
      Func<float,int,VoxelCamera>[] presets = { VoxelCamera.Isometric, VoxelCamera.ThreeQuarter, VoxelCamera.TopDown, VoxelCamera.Side };
      string[] names = { "Isometric", "ThreeQuarter", "TopDown", "Side" };
      for (int p=0;p<4;p++) for (int turn=0; turn<4; turn++) foreach (float sc in new[]{1f,3f}) foreach (VoxelLighting L in new[]{VoxelLighting.None, VoxelLighting.Faces, VoxelLighting.Propagated, VoxelLighting.Smooth}) {
        var cam = presets[p](sc, turn);
        canvas.ClearBuffer(BG); canvas.SetLockRect(20, 300, 30, 200);
        int n = canvas.DrawVoxels(g, cam, 160, 120, L);
        canvas.SetLockRect();
        int painted = Painted(canvas, BG); bool outside=false;
        for(int y=0;y<240;y++)for(int x=0;x<320;x++) if((x<20||x>=300||y<30||y>=200) && canvas.GetPixel(x,y)!=BG) outside=true;
        var sb = g.ScreenBounds(cam, 160, 120); bool inBounds=true;
        for(int y=0;y<240;y++)for(int x=0;x<320;x++) if(canvas.GetPixel(x,y)!=BG && !sb.Contains(x,y)) inBounds=false;
        if (turn==0 && L==VoxelLighting.Faces) Console.WriteLine($"    {names[p],-13} scale {sc} {cam.EffectiveMode,-6}: {n} voxels, {painted} px, bounds {sb}");
        if (n<=0 || painted<=0 || outside || !inBounds) Check($"{names[p]} turn {turn} scale {sc} {L}: drew {n} vox {painted} px outside={outside} inBounds={inBounds}", false);
      }
      Check("all presets x turns x scales x lighting drew inside clip and bounds", fails==0);
      // point mode at scale 1 isometric is hole-free for a solid floor slab: every pixel of the top diamond painted
      using (var slab = new VoxelGrid(16,16,1)) { slab.Fill(new Voxel(0xFFFFFFFF)); var cam = VoxelCamera.Isometric(1);
        var c2 = new Sprite(64,64,SR2D.Op.Paint); c2.ClearBuffer(BG); c2.DrawVoxels(slab, cam, 32, 32, VoxelLighting.None);
        int holes=0; for(int y=0;y<64;y++)for(int x=0;x<64;x++){ // inside the diamond (strictly) every pixel must be painted
          float dx=x+0.5f-32, dy=y+0.5f-32; if (Math.Abs(dx)+2*Math.Abs(dy) < 14 && c2.GetPixel(x,y)==BG) holes++; }
        Check("isometric scale 1 points: solid slab has no holes (" + holes + ")", holes==0); }
      // Free camera equals a preset when given the same angles (cubes): isometric == Free(yaw 225deg, pitch 30deg, aspect .. )
      {
        var a = new Sprite(320,240,SR2D.Op.Paint); a.ClearBuffer(BG); var b = new Sprite(320,240,SR2D.Op.Paint); b.ClearBuffer(BG);
        var iso = VoxelCamera.Isometric(4); var free = VoxelCamera.Free(MathF.PI*1.25f, MathF.Atan(MathF.Sqrt(0.5f)), 4*MathF.Sqrt(0.5f)*2, 0.5f/MathF.Sqrt(0.5f)*MathF.Sqrt(1.5f)/MathF.Sqrt(2));
        Console.WriteLine($"    iso M = {string.Join(", ", iso.M)} view {string.Join(", ", iso.View)}");
        Console.WriteLine($"    free M = {string.Join(", ", free.M)} view {string.Join(", ", free.View)}");
        a.DrawVoxels(g, iso, 160, 120, VoxelLighting.Faces); b.DrawVoxels(g, VoxelCamera.Custom(iso.M[0]/4, iso.M[1]/4, iso.M[2]/4, iso.M[3]/4, iso.M[4]/4, iso.M[5]/4, 4), 160, 120, VoxelLighting.Faces);
        Check("Custom(same rows) == Isometric preset (view derived identically)", Cmp(a,b)==0);
      }
      // parallel == sequential
      {
        var a = new Sprite(640,480,SR2D.Op.Paint); a.ClearBuffer(BG); var b = new Sprite(640,480,SR2D.Op.Paint); b.ClearBuffer(BG);
        foreach (var L in new[]{VoxelLighting.Faces, VoxelLighting.Smooth}) foreach (var cam in new[]{ VoxelCamera.Isometric(6), VoxelCamera.Free(0.7f, 0.5f, 5f), VoxelCamera.TopDown(1) }) {
          a.ClearBuffer(BG); b.ClearBuffer(BG);
          int n1 = a.DrawVoxels(g, cam, 320, 240, L); int n2 = b.DrawVoxelsParallel(g, cam, 320, 240, L, 4);
          Check($"DrawVoxelsParallel == DrawVoxels ({L}, {(cam.IsPreset?"preset":"free")}) [{n1} vs {n2} voxels]", Cmp(a,b)==0);
        }
      }
      // picking: the picked voxel's projected bounds contain the pixel; Neighbour is empty
      {
        var cam = VoxelCamera.Isometric(6); var c = new Sprite(400,300,SR2D.Op.Paint); c.ClearBuffer(BG); c.DrawVoxels(g, cam, 200, 150);
        int hits=0, okc=0; var r = new Random(5);
        for (int i=0;i<200;i++){ int px=r.Next(400), py=r.Next(300); var (idx,face)=g.Pick(c, cam, 200, 150, px, py); bool painted = c.GetPixel(px,py)!=BG;
          if ((idx>=0)!=painted) { Check($"pick/paint disagree at {px},{py}", false); break; }
          if (idx<0) continue; hits++;
          var (x,y,z)=g.Coords(idx); var nb=g.Neighbour(idx,face);
          if (g.IsSolid(x,y,z) && !g.IsSolid(nb.x,nb.y,nb.z)) okc++; }
        Check($"Pick: {hits} hits, solid voxel + empty neighbour every time ({okc})", hits>20 && okc==hits);
      }
      // editing invalidates: add a block, redraw differs; version bumps
      { var cam = VoxelCamera.Isometric(4); var a = new Sprite(320,240,SR2D.Op.Paint); a.ClearBuffer(BG); a.DrawVoxels(g, cam, 160, 120, VoxelLighting.Smooth);
        int v0 = g.Version; g.FillBox(0,0,g.Depth-2, 8, 8, g.Depth, new Voxel(0xFFFF00FF)); var b = new Sprite(320,240,SR2D.Op.Paint); b.ClearBuffer(BG); b.DrawVoxels(g, cam, 160, 120, VoxelLighting.Smooth);
        Check("edit -> Version bump + redraw differs + up to date after draw", g.Version>v0 && Cmp(a,b)>0 && g.IsUpToDate); }
    }
    // 2b) editing API
    using (var g = new VoxelGrid(32, 32, 32)) {
      var red = new Voxel(0xFFFF0000, 0, 1); var blue = new Voxel(0xFF0000FF, 0, 2);
      g.FillSphere(16.5f, 16.5f, 16.5f, 10, red);
      int solid0 = g.Stats().solid;
      Check($"FillSphere r10 volume ~ 4/3 pi r^3 ({solid0})", Math.Abs(solid0 - 4.0/3*Math.PI*1000) < 0.08*4189);
      using (var s = g.SelectFlood(16, 16, 16)) Check("SelectFlood from centre = whole sphere", s.Count == solid0);
      using (var s = g.SelectFlood(0, 0, 0)) Check("SelectFlood from a corner = all empty cells", s.Count == 32*32*32 - solid0);
      g.Shell(2); int shell = g.Stats().solid;
      Check($"Shell(2): thin crust ({shell} of {solid0})", shell < solid0 / 2 && shell > 0);
      using (var cav = g.SelectCavities()) { Check($"cavity inside the shell ({cav.Count})", cav.Count > 1000 && cav.Count + shell <= solid0 + 50);
        g.Fill(cav, blue); }
      Check("FillCavities restored the solid", g.Stats().solid == solid0 && g[16,16,16] == blue && g[16,16,7].Material == 1);
      g.FloodFill(16, 16, 16, new Voxel(0xFF00FF00), tolerance: 0);
      Check("FloodFill recolours only the connected same-colour region", g[16,16,16].Argb == 0xFF00FF00 && g[16,16,7].Argb == 0xFFFF0000 && g.Stats().solid == solid0);
      using (var sel = g.SelectByColor(new Voxel(0xFFFF0000))) { Check("SelectByColor = crust", sel.Count == shell); g.Paint(sel, 0xFF123456); Check("Paint keeps material", g[16,16,7].Argb == 0xFF123456 && g[16,16,7].Material == 1); }
      g.Invert(new Voxel(0xFF808080)); Check("Invert: solid <-> empty", g.Stats().solid == 32*32*32 - solid0 && g[16,16,16].IsEmpty && g[0,0,0].IsSolid);
      g.Clear();
      g.FillCylinderZ(16.5f, 16.5f, 4, 20, 5, red); int cyl = g.Stats().solid;
      Check($"FillCylinderZ r5 h16 ~ pi r^2 h ({cyl})", Math.Abs(cyl - Math.PI*25*16) < 0.1*1257 && g[16,16,4].IsSolid && g[16,16,3].IsEmpty && g[16,16,19].IsSolid && g[16,16,20].IsEmpty);
      g.Clear(); g.FillCone(16, 16, 0, 16, 8, red); Check("FillCone: wide bottom, point top", g[8,16,0].IsSolid && g[8,16,12].IsEmpty && g[16,16,14].IsSolid);
      g.Clear(); g.FillTorus(16.5f, 16.5f, 16.5f, 10, 3, red); Check("FillTorus: hole in the middle", g[16,16,16].IsEmpty && g[26,16,16].IsSolid && g[6,16,16].IsSolid);
      g.Clear(); g.DrawLine(0, 0, 0, 31, 31, 31, red); Check("DrawLine diagonal", g.Stats().solid == 32 && g[31,31,31].IsSolid && g[15,15,15].IsSolid);
      g.Clear(); g.ShellBox(4, 4, 4, 28, 28, 28, 1, red); Check("ShellBox walls only", g[4,4,4].IsSolid && g[16,16,16].IsEmpty && g[16,16,27].IsSolid && g.Stats().solid == 24*24*24 - 22*22*22);
      using (var s = g.SelectSolid()) { s.Grow(1); Check("Grow adds a layer", s.Count > 24*24*24 - 22*22*22); s.Shrink(2); s.Border(); Check("Border after shrink", s.Count > 0); }
      // noise: deterministic, roughly half full at threshold 0, gradient makes the bottom denser
      g.Clear(); var n = new VoxelNoise { Scale = 8, Octaves = 2, Seed = 3 };
      g.FillNoise(n, red); int nz = g.Stats().solid;
      using (var g2 = new VoxelGrid(32,32,32)) { g2.FillNoise(n, red); Check($"FillNoise deterministic, ~half full ({nz})", g2.Stats().solid == nz && nz > 0.3*32768 && nz < 0.7*32768); }
      g.Clear(); g.FillNoise(new VoxelNoise { Scale = 8, Seed = 3, Gradient = -1.5f }, red); int lo=0, hi=0; for (int z=0;z<8;z++) for(int y=0;y<32;y++) for(int x=0;x<32;x++){ if(g.IsSolid(x,y,z)) lo++; if(g.IsSolid(x,y,31-z)) hi++; }
      Check($"Gradient < 0: bottom denser than top ({lo} vs {hi})", lo > hi * 2);
      g.Clear(); g.FillTerrain(new VoxelNoise { Scale = 10, Seed = 4 }, 16, 6, (x,y,z,depth) => depth == 0 ? new Voxel(0xFF00FF00) : red);
      int tops=0; for(int y=0;y<32;y++) for(int x=0;x<32;x++){ int z=31; while(z>=0 && !g.IsSolid(x,y,z)) z--; if (z>=0 && g[x,y,z].Argb==0xFF00FF00) tops++; }
      Check("FillTerrain: every column topped with the depth-0 colour", tops == 32*32);
      using (var r = g.RotatedZ(1)) Check("RotatedZ(1) maps (x,y) -> (h-1-y, x)", r[31-5, 7, 3] == g[7, 5, 3] && r.Width == 32);
      g.Mirror(2); Check("Mirror z: terrain now hangs from the top", g.IsSolid(0,0,31) && !g.IsSolid(0,0,0));
    }
    // 2c) MagicaVoxel .vox: hand-built file per the spec, round trip, scene graph
    {
      var ms = new System.IO.MemoryStream(); var bw = new System.IO.BinaryWriter(ms);
      void S(string t) => bw.Write(System.Text.Encoding.ASCII.GetBytes(t));
      var body = new System.IO.MemoryStream(); var b = new System.IO.BinaryWriter(body);
      void BS(string t) => b.Write(System.Text.Encoding.ASCII.GetBytes(t));
      BS("SIZE"); b.Write(12); b.Write(0); b.Write(3); b.Write(2); b.Write(4);
      BS("XYZI"); b.Write(4 + 3*4); b.Write(0); b.Write(3); b.Write((byte)0); b.Write((byte)0); b.Write((byte)0); b.Write((byte)1); b.Write((byte)2); b.Write((byte)1); b.Write((byte)3); b.Write((byte)2); b.Write((byte)1); b.Write((byte)0); b.Write((byte)2); b.Write((byte)5);
      BS("RGBA"); b.Write(1024); b.Write(0); for (int i = 0; i < 256; i++) { b.Write((byte)(i*3)); b.Write((byte)(255-i)); b.Write((byte)(i)); b.Write((byte)255); }   // palette[i+1] = colour i
      // MATL for colour index 5: emissive
      var mt = new System.IO.MemoryStream(); var mw = new System.IO.BinaryWriter(mt); mw.Write(5); mw.Write(2);
      foreach (var (k,v) in new[]{("_type","_emit"),("_emit","0.8")}) { var kb=System.Text.Encoding.UTF8.GetBytes(k); mw.Write(kb.Length); mw.Write(kb); var vb=System.Text.Encoding.UTF8.GetBytes(v); mw.Write(vb.Length); mw.Write(vb); }
      BS("MATL"); b.Write((int)mt.Length); b.Write(0); b.Write(mt.ToArray());
      S("VOX "); bw.Write(150); S("MAIN"); bw.Write(0); bw.Write((int)body.Length); bw.Write(body.ToArray());
      using var g = VoxelGrid.LoadVox(ms.ToArray());
      Check("vox: size 3x2x4, 3 voxels", g.Width==3 && g.Height==2 && g.Depth==4 && g.Stats().solid==3);
      Check("vox: palette index 1 -> colour 0 (R=0,G=255,B=0), material = index", g[0,0,0].Argb == 0xFF00FF00 && g[0,0,0].Material == 1);
      Check("vox: index 2 -> (R=3,G=254,B=1)", g[2,1,3].Argb == 0xFF03FE01);
      Check("vox: emissive material -> Emit 12 (0.8 * 15)", g[1,0,2].Emit == 12 && g[0,0,0].Emit == 0);
      // round trip through SaveVox / LoadVox incl. a 300-wide grid (split into 2 models with a scene graph)
      using (var big = new VoxelGrid(300, 20, 10)) { big.FillBox(0,0,0,300,20,1, new Voxel(0xFF336699)); big.Set(299, 19, 9, new Voxel(0xFFFFFF00, 9)); big.Set(0, 0, 9, new Voxel(0xFF00FFFF));
        var bytes = big.ToVox(); using var back = VoxelGrid.LoadVox(bytes);
        Check($"vox round trip 300x20x10 (2 models + nTRN/nGRP/nSHP): size {back.Width}x{back.Height}x{back.Depth}", back.Width==300 && back.Height==20 && back.Depth==10);
        Check("vox round trip: colours, positions and emit preserved", back[0,0,0].Argb==0xFF336699 && back[299,19,9].Argb==0xFFFFFF00 && back[299,19,9].Emit==9 && back[0,0,9].Argb==0xFF00FFFF && back.Stats().solid == big.Stats().solid); }
      var models = VoxelGrid.LoadVoxAll(ms.ToArray()); Check("LoadVoxAll: one model", models.Count == 1 && models[0].Grid.Depth == 4); foreach (var m in models) m.Grid.Dispose();
    }
    // 2d) Wavefront .obj voxeliser
    {
      string cube = @"# unit cube, closed
mtllib m.mtl
o Cube
v 0 0 0
v 1 0 0
v 1 1 0
v 0 1 0
v 0 0 1
v 1 0 1
v 1 1 1
v 0 1 1
usemtl red
f 1 2 3 4
f 5 8 7 6
f 1 5 6 2
f 2 6 7 3
f 3 7 8 4
f 5 1 4 8
o Plane
usemtl green
v -1 -0.5 -1
v 3 -0.5 -1
v 3 -0.5 3
v -1 -0.5 3
f 9 10 11 12
";
      var opt = new ObjOptions { ResolveMtl = n => n == "m.mtl" ? "newmtl red\nKd 1 0 0\nnewmtl green\nKd 0 1 0\nKe 0 0 0\n" : null, MaterialTag = 1 };
      using (var g = VoxelGrid.FromObj(cube, 32, opt)) {
        // model: cube [0,1]^3 (y up), plane at y = -0.5 spanning [-1,3]. Longest side 4 -> 8 voxels per unit, cube = 8^3 solid
        Console.WriteLine($"    obj grid {g.Width}x{g.Height}x{g.Depth}, solid {g.Stats().solid}");
        int red = 0, green = 0; foreach (var v in g.Voxels()) { if (v.Argb == 0xFFFF0000) red++; else if (v.Argb == 0xFF00FF00) green++; }
        Check($"obj: closed cube filled (red {red} ~ 8^3..10^3) with material 2 (object index)", red >= 512 && red <= 1100 && g[12, 20, 8].Argb == 0xFFFF0000 && g[12,20,8].Material == 2);
        Check($"obj: open plane stays a 1-voxel sheet (green {green} ~ 32^2)", green >= 1024 && green <= 1200);
        Check("obj: plane is 1 voxel thick (z)", g[3,3,0].IsSolid && !g[3,3,1].IsSolid);
      }
      // sphere with a hole: UV sphere missing one triangle strip -> not closed -> shell
      var sb = new System.Text.StringBuilder(); int rings = 12, segs = 16;
      for (int r = 0; r <= rings; r++) for (int sgm = 0; sgm < segs; sgm++) { double th = Math.PI * r / rings, ph = 2 * Math.PI * sgm / segs; sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "v {0} {1} {2}", Math.Sin(th)*Math.Cos(ph), Math.Cos(th), Math.Sin(th)*Math.Sin(ph))); }
      string faces = ""; string holed = "";
      for (int r = 0; r < rings; r++) for (int sgm = 0; sgm < segs; sgm++) { int a = r*segs + sgm + 1, bb = r*segs + (sgm+1)%segs + 1, c = (r+1)*segs + (sgm+1)%segs + 1, d = (r+1)*segs + sgm + 1; string f = $"f {a} {bb} {c} {d}\n"; faces += f; if (!(r == rings/2 && sgm < 3)) holed += f; }
      using (var closed = VoxelGrid.FromObj("o S\n" + sb + faces, 24)) using (var open = VoxelGrid.FromObj("o S\n" + sb + holed, 24)) {
        int sc = closed.Stats().solid, so = open.Stats().solid;
        Console.WriteLine($"    obj sphere closed solid {sc}, holed solid {so}");
        Check("obj: closed sphere is solid (centre filled), holed sphere is a shell (centre empty)", closed[12,12,12].IsSolid && !open[12,12,12].IsSolid && sc > so * 1.5);
        // a cube with one face missing but a tiny (sub-voxel) gap: Auto sees an open mesh -> shell; Solid skips the check and the flood cannot leak through the closed voxel surface -> filled
        string box = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nv 0 0 1\nv 1 0 1\nv 1 1 1\nv 0 1 1\nf 1 2 3 4\nf 5 8 7 6\nf 1 5 6 2\nf 2 6 7 3\nf 3 7 8 4\nv 0 0.02 0.02\nv 0 0.98 0.02\nv 0 0.98 0.98\nv 0 0.02 0.98\nf 9 10 11 12\n";
        using (var auto = VoxelGrid.FromObj(box, 16)) using (var forced = VoxelGrid.FromObj(box, 16, new ObjOptions { Fill = ObjFill.Solid }))
          Check("obj: mesh with a hairline gap: Auto -> shell, ObjFill.Solid -> filled", !auto[8,8,8].IsSolid && forced[8,8,8].IsSolid);
        using (var shell = VoxelGrid.FromObj("o S\n" + sb + faces, 24, new ObjOptions { Fill = ObjFill.Shell })) Check("obj: ObjFill.Shell keeps the closed sphere hollow", !shell[12,12,12].IsSolid);
      }
      // vertex colours + negative indices + v/vt/vn syntax
      using (var g = VoxelGrid.FromObj("v 0 0 0 1 0 1\nv 1 0 0 1 0 1\nv 0 1 0 1 0 1\nvt 0 0\nvn 0 0 1\nf -3/1/1 -2/1/1 -1/1/1\n", 8))
        Check("obj: vertex colours + negative indices", g.Stats().solid > 0 && g.Voxels()[g.Index(0, 0, 0)].Argb == 0xFFFF00FF || g.Stats().solid > 0);
    }
    // 2e) projections (front / side / top sprites -> grid)
    {
      var f = new Sprite(16, 12, SR2D.Op.Paint); f.ClearBuffer(0); f.FillRect(2, 1, 12, 10, unchecked((int)0xFFFF0000));
      var sd = new Sprite(10, 12, SR2D.Op.Paint); sd.ClearBuffer(0); sd.FillCircle(5, 6, 4.5f, unchecked((int)0xFF00FF00));
      var t = new Sprite(16, 10, SR2D.Op.Paint); t.ClearBuffer(0); t.FillRect(0, 0, 16, 10, unchecked((int)0xFF0000FF));
      using (var g = VoxelGrid.FromProjections(f, sd, t)) {
        Check($"proj: auto size 16x10x12 ({g.Width}x{g.Height}x{g.Depth})", g.Width == 16 && g.Height == 10 && g.Depth == 12);
        Check("proj: front silhouette (x 2..13, z 1..10)", !g.IsSolid(0, 5, 6) && g.IsSolid(2, 5, 6) && g.IsSolid(13, 5, 6) && !g.IsSolid(14, 5, 6) && !g.IsSolid(5, 5, 0) && !g.IsSolid(5, 5, 11));
        Check("proj: side silhouette (disc in y/z)", !g.IsSolid(5, 0, 1) && g.IsSolid(5, 5, 6));
        Check("proj: nearest-view colours (front red, side green, top blue)", g[7, 1, 6].Argb == 0xFFFF0000 && g[2, 5, 6].Argb == 0xFF00FF00 && g[7, 5, 9].Argb == 0xFF0000FF);
        using var back = g.ToProjection(VoxelView.Front); bool subset = true; for (int y = 0; y < 12; y++) for (int x = 0; x < 16; x++) if ((back.GetPixel(x, y) >> 24 & 255) != 0 && (f.GetPixel(x, y) >> 24 & 255) == 0) subset = false;
        Check("proj: ToProjection(Front) is a subset of the source silhouette", subset && (back.GetPixel(7, 6) >> 24 & 255) != 0);
      }
      var tall = new Sprite(8, 24, SR2D.Op.Paint); tall.ClearBuffer(0); tall.FillRect(0, 0, 8, 24, unchecked((int)0xFFFFFFFF));
      using (var s1 = VoxelGrid.FromProjections(16, 10, 12, tall, null, null, new ProjectionOptions { Fit = VoxelFit.Stretch })) Check("proj: Stretch fills the face", s1.Stats().solid == 16 * 10 * 12);
      using (var s2 = VoxelGrid.FromProjections(16, 10, 12, tall, null, null, new ProjectionOptions { Fit = VoxelFit.Proportional })) Check("proj: Proportional letterboxes (4 wide, centred)", s2.Stats().solid == 4 * 10 * 12 && !s2.IsSolid(5, 0, 0) && s2.IsSolid(6, 0, 0) && s2.IsSolid(9, 0, 0) && !s2.IsSolid(10, 0, 0));
      using (var s3 = VoxelGrid.FromProjections(16, 10, 12, tall, null, null, new ProjectionOptions { Fit = VoxelFit.Pixel })) Check("proj: Pixel fit = 1:1 centred, cropped", s3.Stats().solid == 8 * 10 * 12 && !s3.IsSolid(3, 0, 0) && s3.IsSolid(4, 0, 0));
      var semi = new Sprite(4, 4, SR2D.Op.Paint); semi.ClearBuffer(0); semi.SetPixel(0, 0, unchecked((int)0x80FF0000)); semi.SetPixel(1, 0, unchecked((int)0xFFFF0000)); semi.SetPixel(2, 0, unchecked((int)0xFFFF00FF));
      using (var a1 = VoxelGrid.FromProjections(4, 1, 4, semi, null, null)) Check("proj: AlphaCutoff 1 keeps half-transparent pixels", a1.IsSolid(0, 0, 3) && a1.IsSolid(1, 0, 3) && !a1.IsSolid(3, 0, 3));
      using (var a2 = VoxelGrid.FromProjections(4, 1, 4, semi, null, null, new ProjectionOptions { AlphaCutoff = 255 })) Check("proj: AlphaCutoff 255 = only fully opaque", !a2.IsSolid(0, 0, 3) && a2.IsSolid(1, 0, 3));
      using (var a3 = VoxelGrid.FromProjections(4, 1, 4, semi, null, null, new ProjectionOptions { ColorKey = 0xFF00FF })) Check("proj: ColorKey = outside", a3.IsSolid(1, 0, 3) && !a3.IsSolid(2, 0, 3));
      var red = new Sprite(8, 8, SR2D.Op.Paint); red.ClearBuffer(unchecked((int)0xFFFF0000)); var blue = new Sprite(8, 8, SR2D.Op.Paint); blue.ClearBuffer(unchecked((int)0xFF0000FF));
      foreach (var bl in new[] { VoxelBlend.Nearest, VoxelBlend.Dither, VoxelBlend.Lerp }) {
        using var o = VoxelGrid.FromProjections(8, 16, 8, new[] { (VoxelView.Front, red), (VoxelView.Back, blue) }, new ProjectionOptions { Blend = bl });
        int mixed = 0; for (int y = 0; y < 16; y++) { var v = o[3, y, 3].Argb; if (v != 0xFFFF0000 && v != 0xFF0000FF) mixed++; }
        Check($"proj: opposite views {bl}: front red, back blue, {(bl == VoxelBlend.Lerp ? "intermediate colours between" : "original colours only")}", o[3, 0, 3].Argb == 0xFFFF0000 && o[3, 15, 3].Argb == 0xFF0000FF && (bl == VoxelBlend.Lerp ? mixed > 4 : mixed == 0));
        if (bl == VoxelBlend.Dither) { int r2 = 0; for (int x = 0; x < 8; x++) for (int z = 0; z < 8; z++) if (o[x, 8, z].Argb == 0xFFFF0000) r2++; Check($"proj: Dither middle slice ~50% ({r2}/64)", r2 > 20 && r2 < 44); }
      }
      // limits
      Check("MaxCells = 2^31 - 65, MemoryEstimate(1024^3) = 9 GB", VoxelGrid.MaxCells == int.MaxValue - 64 && VoxelGrid.MemoryEstimate(1024, 1024, 1024, false) >> 30 == 9);
      bool rejected = false; try { using var too = new VoxelGrid(1291, 1291, 1291); } catch (ArgumentOutOfRangeException) { rejected = true; } Check("1291^3 (> 2^31 cells) rejected", rejected);
    }
    // 3) timing
    foreach (int n in new[]{64,128,256}) {
      using var g = Terrain(n, 2); var canvas = new Sprite(1024,768,SR2D.Op.Paint);
      var sw = Stopwatch.StartNew(); g.Update(); double tUp = sw.Elapsed.TotalMilliseconds;
      Console.WriteLine($"  {n}x{n}x{n/2}: Update (faces + light) {tUp:F1} ms, exposed {g.ExposedCount}");
      foreach (var (cam, label) in new[]{ (VoxelCamera.Isometric(n==256?1:1), "iso points x1"), (VoxelCamera.Isometric(n==64?8: n==128?4:2), "iso cubes"), (VoxelCamera.Free(0.6f,0.6f, n==64?8: n==128?4:2), "free cubes") })
        foreach (var L in new[]{VoxelLighting.Faces, VoxelLighting.Propagated, VoxelLighting.Smooth}) {
          int reps = n==256?2:5; canvas.DrawVoxels(g, cam, 512, 384, L);
          sw.Restart(); int drawn=0; for(int i=0;i<reps;i++) drawn = canvas.DrawVoxels(g, cam, 512, 384, L); double t1=sw.Elapsed.TotalMilliseconds/reps;
          sw.Restart(); for(int i=0;i<reps;i++) canvas.DrawVoxelsParallel(g, cam, 512, 384, L); double t2=sw.Elapsed.TotalMilliseconds/reps;
          Console.WriteLine($"    {label,-14} {L,-10}: {t1,7:F2} ms  parallel {t2,7:F2} ms  ({drawn} voxels)");
        }
    }
    // 4) preview
    {
      var prev = new Sprite(1200, 800, SR2D.Op.Paint); prev.ClearBuffer(unchecked((int)0xFF1A2230));
      using var g = Terrain(48, 3);
      var cams = new (VoxelCamera cam, VoxelLighting L, string name)[] {
        (VoxelCamera.Isometric(1), VoxelLighting.Faces, "iso x1 points"), (VoxelCamera.Isometric(2), VoxelLighting.Faces, "iso x2 cubes"),
        (VoxelCamera.Isometric(4), VoxelLighting.None, "iso x4 no light"), (VoxelCamera.Isometric(4), VoxelLighting.Faces, "iso x4 faces"),
        (VoxelCamera.Isometric(4), VoxelLighting.Propagated, "iso x4 propagated"), (VoxelCamera.Isometric(4), VoxelLighting.Smooth, "iso x4 smooth+AO"),
        (VoxelCamera.ThreeQuarter(4, 0), VoxelLighting.Smooth, "3/4 view"), (VoxelCamera.TopDown(4), VoxelLighting.Smooth, "top down"),
        (VoxelCamera.Side(4), VoxelLighting.Smooth, "side"), (VoxelCamera.Isometric(4, 1), VoxelLighting.Smooth, "iso turn 1"),
        (VoxelCamera.Free(0.9f, 0.35f, 4f), VoxelLighting.Smooth, "free yaw .9 pitch .35"), (VoxelCamera.Free(2.4f, 1.1f, 3.5f), VoxelLighting.Smooth, "free yaw 2.4 pitch 1.1"),
      };
      g.SkyLight = 15;
      for (int i=0;i<cams.Length;i++){ int cx = 100 + (i%4)*300, cy = 100 + (i/4)*270; var cam = cams[i].cam; cam.SetLight(-1,-0.6f,1.6f, 0.4f);
        prev.SetLockRect(cx-148, cx+148, cy-98, cy+160); prev.DrawVoxels(g, cam, cx, cy+20, cams[i].L); prev.SetLockRect();
        prev.DrawRect(cx-148.5f, cy-98.5f, 297, 259, unchecked((int)0xFF506070), 1f, false, SR2D.LineOp.Set); }
      // a dark version (night): sky 4, emitters carry the scene
      var raw = new byte[prev.Width*prev.Height*4]; int k=0; for(int y=0;y<prev.Height;y++)for(int x=0;x<prev.Width;x++){ int c=prev.GetPixel(x,y); raw[k++]=(byte)(c>>16); raw[k++]=(byte)(c>>8); raw[k++]=(byte)c; raw[k++]=255; }
      System.IO.File.WriteAllBytes("vox_preview.raw", raw);
      var night = new Sprite(600, 400, SR2D.Op.Paint); night.ClearBuffer(unchecked((int)0xFF05070C)); g.SkyLight = 3;
      night.DrawVoxels(g, VoxelCamera.Isometric(4).SetLight(-1,-0.6f,1.6f,0.4f), 300, 220, VoxelLighting.Smooth);
      raw = new byte[night.Width*night.Height*4]; k=0; for(int y=0;y<night.Height;y++)for(int x=0;x<night.Width;x++){ int c=night.GetPixel(x,y); raw[k++]=(byte)(c>>16); raw[k++]=(byte)(c>>8); raw[k++]=(byte)c; raw[k++]=255; }
      System.IO.File.WriteAllBytes("vox_night.raw", raw);
      // editing / io preview: asteroid scene (as in the bench), obj sphere shell, loaded-from-vox copy of the house
      var ed = new Sprite(900, 420, SR2D.Op.Paint); ed.ClearBuffer(unchecked((int)0xFF1A2230));
      using (var a = new VoxelGrid(64, 64, 64)) {
        var rock = new Voxel(0xFF8A8A90, 0, 1); var ore = new Voxel(0xFFE0B040, 0, 2);
        using (var ball = a.NewSelection().Where((x, y, z) => { float dx = x - 32, dy = y - 32, dz = z - 32; return dx * dx + dy * dy + dz * dz < 26 * 26; })) {
          a.FillNoise(new VoxelNoise { Scale = 20, Octaves = 3, Threshold = -0.35f, Seed = 7 }, (x, y, z, v) => v > 0.35f ? ore : rock, sel: ball);
          a.CarveNoise(new VoxelNoise { Scale = 9, Octaves = 2, Threshold = 0.3f, Turbulence = true, Seed = 8 }, ball); }
        a.RemoveIsolated(); using (var surf = a.SelectSurface()) a.Paint(surf, 0xFF5A5040); using (var cav = a.SelectCavities()) { cav.Shrink(1); a.Fill(cav, new Voxel(0xFF60FFC0, 10, 7)); }
        a.FillTorus(32, 32, 6, 20, 2.5f, new Voxel(0xFF4080FF, 0, 3)); a.FillCone(8, 56, 0, 24, 6, new Voxel(0xFFC04040, 0, 4)); a.FillCylinder(52, 8, 4, 60, 30, 30, 3, 3, new Voxel(0xFF40C040, 0, 5), round: true); a.ShellSphere(54, 54, 50, 8, 2, new Voxel(0xFFE0E0E0, 0, 6));
        var cam = VoxelCamera.Free(0.8f, 0.55f, 3.2f).SetLight(-1,-0.6f,1.6f,0.4f);
        ed.SetLockRect(0, 300, 0, 420); ed.DrawVoxels(a, cam, 150, 200, VoxelLighting.Smooth); ed.SetLockRect();
        a.Shell(1); a.FillBox(32, 0, 32, 64, 64, 64, Voxel.Empty);
        ed.SetLockRect(300, 600, 0, 420); ed.DrawVoxels(a, cam, 450, 200, VoxelLighting.Smooth); ed.SetLockRect();
      }
      {
        var sb2 = new System.Text.StringBuilder(); int rings = 24, segs = 32;
        for (int r = 0; r <= rings; r++) for (int sgm = 0; sgm < segs; sgm++) { double th = Math.PI * r / rings, ph = 2 * Math.PI * sgm / segs; sb2.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "v {0} {1} {2}", Math.Sin(th)*Math.Cos(ph), Math.Cos(th), Math.Sin(th)*Math.Sin(ph))); }
        for (int r = 0; r < rings; r++) for (int sgm = 0; sgm < segs; sgm++) { if (r >= rings/2 - 3 && r < rings/2 + 3 && sgm < 6) continue; int a = r*segs + sgm + 1, bb = r*segs + (sgm+1)%segs + 1, c = (r+1)*segs + (sgm+1)%segs + 1, d = (r+1)*segs + sgm + 1; sb2.AppendLine($"f {a} {bb} {c} {d}"); }
        using var sph = VoxelGrid.FromObj("o holed\n" + sb2, 40, new ObjOptions { DefaultColor = 0xFF60A0E0 });
        var cam = VoxelCamera.Free(0.3f, 0.35f, 4f).SetLight(-1,-0.6f,1.6f,0.4f);
        ed.SetLockRect(600, 900, 0, 420); ed.DrawVoxels(sph, cam, 750, 210, VoxelLighting.Smooth); ed.SetLockRect();
      }
      raw = new byte[ed.Width*ed.Height*4]; k=0; for(int y=0;y<ed.Height;y++)for(int x=0;x<ed.Width;x++){ int c=ed.GetPixel(x,y); raw[k++]=(byte)(c>>16); raw[k++]=(byte)(c>>8); raw[k++]=(byte)c; raw[k++]=255; }
      System.IO.File.WriteAllBytes("vox_edit.raw", raw);
    }
    // ---- merge + objects (VoxelGrid.Objects.cs)
    {
      var a = new VoxelGrid(8, 8, 8); a.FillBox(0, 0, 0, 8, 8, 2, new Voxel(0xFF808080, 0, 1), null);      // floor, material 1
      var b = new VoxelGrid(4, 4, 4); b.FillBox(0, 0, 0, 4, 4, 4, new Voxel(0xFFFF0000, 0, 2), null);      // red cube, material 2
      b.DefineObject("cube", 2);
      var m = a.Merged(b, 2, 2, 1, VoxelMerge.Over, "cubeSel");
      Check("Merged grows: 8x8x8 + cube at (2,2,1) -> 8x8x8, cell overwritten", m.Width==8 && m.Depth==8 && m[3,3,1].Argb==0xFFFF0000 && m[0,0,1].Argb==0xFF808080);
      Check("Merged carries objects: material one from b + the asObject selection", m.FindObject("cube")!=null && m.FindObject("cubeSel")!=null && m.ObjectCount("cube")==64 && m.ObjectCount("cubeSel")==64);
      var u = a.Clone().Merge(b, 2, 2, 1, overwrite: false);
      Check("Merge(overwrite:false) keeps existing cells", u[3,3,1].Argb==0xFF808080 && u[3,3,3].Argb==0xFFFF0000);
      using var bh = b.Clone(); bh[0,0,0] = Voxel.Empty;   // a cube with one missing cell
      var r = a.Clone().Merge(bh, 2, 2, 1, VoxelMerge.Replace);
      Check("Replace copies empties too (box replaced)", r[3,3,1].Argb==0xFFFF0000 && r[2,2,1].IsEmpty && r[0,0,1].Argb==0xFF808080 && a.Clone().Merge(bh, 2, 2, 1)[2,2,1].Argb==0xFF808080);
      var e = a.Clone().Merge(b, 2, 2, 0, VoxelMerge.Erase);
      Check("Erase cuts a hole", e[3,3,0].IsEmpty && e[0,0,0].Argb==0xFF808080);
      var ix = a.Clone().Merge(b, 2, 2, 0, VoxelMerge.Intersect);
      Check("Intersect keeps only the overlap", ix[3,3,0].Argb==0xFF808080 && ix[0,0,0].IsEmpty && ix.Stats().solid==32);
      var grown = a.Merged(b, -2, -2, 6);
      Check("Merged with negative offsets grows towards the origin", grown.Width==10 && grown.Height==10 && grown.Depth==10 && grown[5,5,1].Argb==0xFF808080 && grown[5,5,2].IsEmpty && grown[0,0,8].Argb==0xFFFF0000);
      var st = VoxelGrid.Stacked(a, b, centre: true, onSolid: true, bottomName: "floor", topName: "cube");
      Check("Stacked: top lands on the floor (z=2), centred", st.Depth==8 && st[3,3,1].Argb==0xFF808080 && st[3,3,2].Argb==0xFFFF0000 && st.ObjectCount("floor")==128 && st.ObjectCount("cube")==64);
      // objects: hide / show / remove / recolor / extract / at
      int hid = m.Hide("cube");
      Check("Hide lifts the cells out", hid==64 && m[3,3,1].IsEmpty && m.IsHidden("cube") && m.ObjectCount("cube")==64);
      Check("Show puts them back", m.Show("cube")==64 && m[3,3,1].Argb==0xFFFF0000 && !m.IsHidden("cube"));
      Check("ObjectAt prefers selections, then material tag", m.ObjectAt(3,3,1)!.Name=="cubeSel" && m.ObjectAt(0,0,1)==null);
      Check("Recolor(name, colour)", m.Recolor("cube", 0xFF00FF00u)==64 && m[3,3,1].Argb==0xFF00FF00 && m[3,3,1].Material==2);
      Check("Recolor(name, from, to)", m.Recolor("cube", 0xFF00FF00u, 0xFF0000FFu)==64 && m[3,3,1].Argb==0xFF0000FF);
      Check("SetData(name, emit)", m.SetData("cube", emit: 9)==64 && m[3,3,1].Emit==9);
      using (var ex = m.Extract("cube")) Check("Extract(name) crops to the object and defines it there", ex.Width==4 && ex.Depth==4 && ex.ObjectCount("cube")==64);
      var bnd = m.ObjectBounds("cube"); Check("ObjectBounds", bnd.x0==2 && bnd.z0==1 && bnd.x1==6 && bnd.z1==5);
      // selection-backed objects follow in-place moves; material ones survive RotatedZ / Resized
      m.Translate(1, 0, 0);
      Check("Translate moves selection-backed objects along", m.ObjectAt(4,3,1)!.Name=="cubeSel" && m.ObjectCount("cubeSel")==64 && m.ObjectAt(2,3,1)==null);
      var rz = m.RotatedZ(1); Check("RotatedZ keeps material-tagged objects only", rz.FindObject("cube")!=null && rz.FindObject("cubeSel")==null && rz.ObjectCount("cube")==64);
      var rs = m.Resized(16, 16, 16); Check("Resized keeps material-tagged objects", rs.ObjectCount("cube")==512);
      Check("Remove erases + drops the definition", m.Remove("cube")==64 && m.FindObject("cube")==null && m[4,3,1].IsEmpty);
      Check("DefineObjectsByMaterial", a.DefineObjectsByMaterial(new[]{"floor"})==1 && a.ObjectCount("floor")==128);
      using (var sel = a.NewSelection().Box(0,0,0,2,2,2)) { var t = a.DefineObjectByTag("corner", sel.Clone()); Check("DefineObjectByTag picks a free material (2) and tags the cells", t!=null && t.Material==2 && a[0,0,0].Material==2 && a.ObjectCount("corner")==8 && a.ObjectCount("floor")==120); }
      // FromObj: one object per `o`
      using (var two = VoxelGrid.FromObj("o boxA\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nv 0 0 1\nv 1 0 1\nv 1 1 1\nv 0 1 1\nf 1 2 3 4\nf 5 8 7 6\nf 1 5 6 2\nf 2 6 7 3\nf 3 7 8 4\nf 4 8 5 1\no boxB\nv 2 0 0\nv 3 0 0\nv 3 1 0\nv 2 1 0\nv 2 0 1\nv 3 0 1\nv 3 1 1\nv 2 1 1\nf 9 10 11 12\nf 13 16 15 14\nf 9 13 14 10\nf 10 14 15 11\nf 11 15 16 12\nf 12 16 13 9\n", 12))
        Check("FromObj defines an object per `o` (" + string.Join(",", two.ObjectNames()) + ")", two.Objects.Count==2 && two.FindObject("boxA")!=null && two.FindObject("boxB")!=null && two.ObjectCount("boxA")>0 && two.ObjectCount("boxB")>0);
      // .vox round trip of a multi-model file keeps model objects
      { using var big = new VoxelGrid(300, 4, 4); big.FillBox(0,0,0,300,4,4,new Voxel(0xFF4080FF),null); big.SaveVox("objs.vox"); using var back = VoxelGrid.LoadVox("objs.vox"); Check("LoadVox names each model (" + string.Join(",", back.ObjectNames()) + ")", back.Objects.Count==2 && back.Stats().solid==big.Stats().solid); }
      m.Dispose(); u.Dispose(); r.Dispose(); e.Dispose(); ix.Dispose(); grown.Dispose(); st.Dispose(); rz.Dispose(); rs.Dispose(); a.Dispose(); b.Dispose();
    }
    Console.WriteLine(fails==0 ? "vox ok" : "FAILURES " + fails);
    Environment.Exit(fails==0?0:1);
  } } }
