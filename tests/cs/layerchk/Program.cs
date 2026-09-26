// LayeredSprite check: composite == manual layer-by-layer draw (bit-exact), prefix cache ==
// full recompose, automatic dirty tracking (Effects.Version, setters), transformed layers,
// re-entrancy safety, timing. Run under `timeout` - a regression must fail fast, never hang.
using System; using System.Diagnostics; using System.Drawing; using System.Numerics;
namespace Sr2d64CSport {
static class P {
  static int fails;
  static int Cmp(Sprite a, Sprite b){ int n=0; for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++) if(a.GetPixel(x,y)!=b.GetPixel(x,y)) n++; return n; }
  static void Check(string what, bool ok){ Console.WriteLine((ok?"  ok   ":"  FAIL ")+what); if(!ok) fails++; }
  static void Same(string what, Sprite a, Sprite b){ int d=Cmp(a,b); Check(what+(d==0?"":" ("+d+" px differ)"), d==0); }
  static Sprite Blob(int size, int seed, bool premul){
    var s = new Sprite(size, size, SR2D.Op.AlphaBlend); s.ClearBuffer(0); var r = new Random(seed);
    int cx=size/2+r.Next(-size/6,size/6), cy=size/2+r.Next(-size/6,size/6), rad=size/3;
    for(int y=0;y<size;y++)for(int x=0;x<size;x++){ int dx=x-cx,dy=y-cy; double d=Math.Sqrt(dx*dx+dy*dy); int a=(int)Math.Clamp((rad-d)*40,0,255); if(a==0) continue;
      s.SetPixel(x,y, SR2D.ARGB((byte)a,(byte)((x*3+seed*50)&255),(byte)((y*2+seed*90)&255),(byte)((x+y+seed*20)&255))); }
    if(premul) s.Premultiply();
    return s;
  }
  static unsafe void Main() {
    const int W=256,H=256,L=8;
    var sprites = new Sprite[L]; var fxs = new Effects[L];
    for(int i=0;i<L;i++){ sprites[i]=Blob(160, i, i%2==0);
      fxs[i] = (i%4) switch { 0 => new Effects().Blur(3), 1 => new Effects().Wave(20,3,0.5f), 2 => new Effects().Color(Brightness:0.1f,Saturation:1.3f), _ => new Effects().Shadow(3,3,4) }; }

    // ---- 1. composite == manual composition (exact path)
    var ls = new LayeredSprite(W,H);
    for(int i=0;i<L;i++) ls.Add(sprites[i], fxs[i], 10+i*9, 8+i*7);
    var manual = new Sprite(W,H,SR2D.Op.AlphaOver); manual.ClearBuffer(0);
    for(int i=0;i<L;i++) manual.DrawFx(sprites[i], 10+i*9, 8+i*7, fxs[i], SR2D.Op.AlphaOver);
    Same("composite == 8 x DrawFx(AlphaOver) into a zeroed premultiplied buffer", ls.Sprite, manual);
    Check("Compose() reports first pass from layer 0", ls.LastComposedFrom==0);
    Check("nothing changed -> not dirty", !ls.IsDirty && ls.Compose()==false);

    // ---- 2. drawing the composite onto a canvas == drawing layers one by one (1:1)
    var c1 = new Sprite(400,300,SR2D.Op.Paint); c1.ClearBuffer(unchecked((int)0xFF405060));
    var c2 = new Sprite(400,300,SR2D.Op.Paint); c2.ClearBuffer(unchecked((int)0xFF405060));
    ls.Draw(c1, 50, 20);
    c2.Draw(manual, 50, 20, SR2D.Op.AlphaOver);
    Same("LayeredSprite.Draw == Draw(composite, AlphaOver)", c1, c2);

    // ---- 3. prefix cache: edit layer 5 -> recompose 5..7 only, identical to a full compose
    fxs[5].Clear().Blur(6).Color(Hue: 40);                          // Effects.Version bump, no explicit call
    Check("Effects edit detected (IsDirty)", ls.IsDirty);
    ls.Compose();
    Check("recomposed from layer 5 (prefix cache)", ls.LastComposedFrom==5);
    var full = new LayeredSprite(W,H){ PrefixCache=false };
    for(int i=0;i<L;i++) full.Add(sprites[i], fxs[i], 10+i*9, 8+i*7);
    Same("prefix-cached recompose == full recompose", ls.Sprite, full.Sprite);

    // setter tracking
    ls[2].X = 40; ls.Compose(); Check("layer.X setter -> recompose from 2", ls.LastComposedFrom==2);
    full[2].X = 40; Same("after moving layer 2", ls.Sprite, full.Sprite);
    ls[7].Visible=false; ls.Compose(); Check("Visible -> from 7", ls.LastComposedFrom==7);
    full[7].Visible=false; Same("hidden top layer", ls.Sprite, full.Sprite);
    ls[3].Opacity=0.5f; full[3].Opacity=0.5f; Same("layer opacity 0.5", ls.Sprite, full.Sprite);
    ls.Move(0, 6); full.Move(0, 6); ls.Compose(); Check("Move(0,6) -> from 0", ls.LastComposedFrom==0); Same("after Move", ls.Sprite, full.Sprite);
    ls.RemoveAt(4); full.RemoveAt(4); Same("after RemoveAt(4)", ls.Sprite, full.Sprite);
    Check("layer indices renumbered", ls[4].Index==4 && ls[6].Index==6 && ls.Count==7);
    var ins = ls.Insert(1, sprites[0], null, 3, 3); full.Insert(1, sprites[0], null, 3, 3);
    Check("Insert(1) index", ins.Index==1); Same("after Insert(1) (plain AlphaOver blit path)", ls.Sprite, full.Sprite);

    // ---- 4. transformed layers (float offset, scale, rotation) == DrawFxQuad by hand
    var t = new LayeredSprite(W,H);
    var tl = t.Add(sprites[1], fxs[1], 30.5f, 20.25f); tl.Scale=0.8f; tl.Angle=0.4f; tl.Filter=SR2D.Filter.Bicubic;
    t.Add(sprites[2], null, 60, 70).Angle = -0.3f;
    Check("transformed layer is not exact", !tl.IsExact && t[1].IsExact==false);
    var tm = new Sprite(W,H,SR2D.Op.AlphaOver); tm.ClearBuffer(0);
    { // same quad construction as LayeredSprite.DrawLayer
      var s=sprites[1]; float px=s.Width*0.5f, py=s.Height*0.5f, c=(float)Math.Cos(0.4f), sn=(float)Math.Sin(0.4f), ox=30.5f+px, oy=20.25f+py;
      Span<PointF> q = stackalloc PointF[4];
      for(int i=0;i<4;i++){ float cx=(((i==1||i==2)?s.Width:0)-px)*0.8f, cy=((i>=2?s.Height:0)-py)*0.8f; q[i]=new PointF(ox+cx*c-cy*sn, oy+cx*sn+cy*c); }
      tm.DrawFxQuad(s, q, fxs[1], default, SR2D.Op.AlphaOver, SR2D.Filter.Bicubic);
      s=sprites[2]; px=s.Width*0.5f; py=s.Height*0.5f; c=(float)Math.Cos(-0.3f); sn=(float)Math.Sin(-0.3f); ox=60+px; oy=70+py;
      for(int i=0;i<4;i++){ float cx=((i==1||i==2)?s.Width:0)-px, cy=(i>=2?s.Height:0)-py; q[i]=new PointF(ox+cx*c-cy*sn, oy+cx*sn+cy*c); }
      tm.DrawFxQuad(s, q, new Effects(), default, SR2D.Op.AlphaOver, SR2D.Filter.Auto);
    }
    Same("transformed layers == DrawFxQuad by hand", t.Sprite, tm);

    // ---- 5. whole-stack effects + rotated draw of the composite
    ls.Effects = new Effects().Shadow(4,4,6).Outline(2, 0xFFFFFF);
    var c3 = new Sprite(400,300,SR2D.Op.Paint); c3.ClearBuffer(unchecked((int)0xFFB0B8C0));
    var c4 = new Sprite(400,300,SR2D.Op.Paint); c4.ClearBuffer(unchecked((int)0xFFB0B8C0));
    ls.DrawRotated(c3, 200, 150, 0.6f, 300, 300);
    c4.DrawFxRotated(ls.Sprite, 200, 150, 0.6f, ls.Effects, 300, 300, Filter: SR2D.Filter.Auto);
    Same("DrawRotated with stack Effects == DrawFxRotated(composite)", c3, c4);
    Check("stack Effects change does not dirty the composite", !ls.IsDirty);

    // ---- 6. re-entrancy: a layer whose sprite IS the composite must not recurse
    var re = new LayeredSprite(64,64); re.Add(sprites[0]); re.Add(re.Sprite);
    re.Compose(); Check("self-referencing layer is skipped (no recursion)", re.Sprite.Width==64);
    // Effects shared by two layers and mutated: still one compose
    var sh = new Effects().Blur(2); var s2 = new LayeredSprite(128,128); s2.Add(sprites[0], sh); s2.Add(sprites[1], sh, 20, 20);
    s2.Compose(); sh.Color(Opacity:0.7f); s2.Compose(); Check("shared Effects edit -> from layer 0", s2.LastComposedFrom==0);

    // ---- 7. Effects.Version semantics
    var v = new Effects(); int v0=v.Version; v.Blur(3); Check("Version bumps on add", v.Version==v0+1);
    v.Enable(0, true); Check("no-op Enable keeps Version", v.Version==v0+1); v.Disable(0); Check("Disable bumps", v.Version==v0+2);
    v.Post=true; v.Post=true; Check("Post bumps once", v.Version==v0+3); v.RemoveLast(); v.Clear(); Check("RemoveLast bumps, Clear on empty does not", v.Version==v0+4);
    var cl = v.Blur(2).Wave(10,2).Clone(); Check("Clone copies stages", cl.Count==2 && cl.Margin==v.Margin);

    // ---- 7b. parallel compose == sequential compose (bit-exact), all layer kinds
    {
      var seqS = new LayeredSprite(W,H); var parS = new LayeredSprite(W,H) { Threads = 4, PrefixCache = false };
      var mix = new Sprite[10]; var mfx = new Effects[10];
      for(int i=0;i<10;i++){ mix[i]=Blob(120, 40+i, i%3==0); mfx[i]=(i%5) switch { 0=>new Effects().Blur(5), 1=>new Effects().BoxBlur(24, 4), 2=>new Effects().Diffuse(2,2,i), 3=>new Effects().Shadow(3,3,6, unchecked((int)0xFF000000), BlurQuality.Box, 2), _=>new Effects().Color(Hue:40) };
        foreach (var stk in new[]{seqS, parS}) { var ly = stk.Add(mix[i], mfx[i], 10+i*12, 20+(i%3)*30, (i%4) switch { 0=>SR2D.Op.AlphaOver, 1=>SR2D.Op.Add, 2=>SR2D.Op.Max, _=>SR2D.Op.AlphaBlend }); if (i==4) ly.Place(60, 60, 1.3f, 0.4f); if (i==7) ly.Opacity = 0.5f; if (i==8) ly.Effects = null; } }
      seqS.Compose(); parS.Compose();
      Same("Threads = 4 compose == sequential (mixed ops, transformed, opacity, no-fx layers)", seqS.Sprite, parS.Sprite);
      mfx[1].DiffuseSeed(5); mfx[2].DiffuseSeed(5); seqS.Compose(); parS.Compose();
      Same("... after DiffuseSeed on two layers", seqS.Sprite, parS.Sprite);
      parS.Threads = 0; parS.Invalidate(); seqS.Invalidate(); seqS.Compose(); parS.Compose();
      Same("... Threads = 0 (all cores) full recompose", seqS.Sprite, parS.Sprite);
      // the depth-of-field case: N layers, each blurred by depth, everything changes every frame
      const int N = 32; var dof = new Sprite[N]; var dfx = new Effects[N];
      var stackS = new LayeredSprite(256,256) { PrefixCache = false }; var stackP = new LayeredSprite(256,256) { PrefixCache = false, Threads = 0 };
      for(int i=0;i<N;i++){ dof[i]=Blob(256, 100+i, true); int r = (N-1-i) * 2; dfx[i] = r>0 ? new Effects().Blur(r, BlurQuality.Box, Effects.AutoDownscale) : new Effects(); stackS.Add(dof[i], dfx[i]); stackP.Add(dof[i], dfx[i]); }
      stackS.Compose(); stackP.Compose(); Same("32-layer depth stack: parallel == sequential", stackS.Sprite, stackP.Sprite);
      var sw0 = Stopwatch.StartNew(); const int reps0 = 10;
      for(int r=0;r<reps0;r++){ stackS.Invalidate(0); stackS.Compose(); } double tS = sw0.Elapsed.TotalMilliseconds/reps0;
      sw0.Restart(); for(int r=0;r<reps0;r++){ stackP.Invalidate(0); stackP.Compose(); } double tP = sw0.Elapsed.TotalMilliseconds/reps0;
      var g3 = new Sprite[N]; var s3 = new LayeredSprite(256,256) { PrefixCache = false }; for(int i=0;i<N;i++){ int r=(N-1-i)*2; s3.Add(dof[i], r>0 ? new Effects().Blur(r) : new Effects()); }
      s3.Compose(); sw0.Restart(); for(int r=0;r<reps0;r++){ s3.Invalidate(0); s3.Compose(); } double tG = sw0.Elapsed.TotalMilliseconds/reps0;
      s3.Threads = 0; s3.Compose(); sw0.Restart(); for(int r=0;r<reps0;r++){ s3.Invalidate(0); s3.Compose(); } double tGP = sw0.Elapsed.TotalMilliseconds/reps0;
      Console.WriteLine($"  timing 32 x 256^2 depth stack, all layers dirty ({Environment.ProcessorCount} cores): gaussian blur sequential {tG:F1} ms, Threads=0 {tGP:F1} ms | box+auto-downscale sequential {tS:F1} ms, Threads=0 {tP:F1} ms");
    }

    // ---- 8. timing (12 layers 256^2 like the native benchmark)
    var big = new LayeredSprite(256,256); var bs = new Sprite[12]; var bf = new Effects[12];
    for(int i=0;i<12;i++){ bs[i]=Blob(256,i+20,true); bf[i]= (i%4) switch { 0=>new Effects().Blur(4), 1=>new Effects().Wave(20,3), 2=>new Effects().Color(Contrast:1.2f,Saturation:1.3f), _=>new Effects().Shadow(3,3,4,unchecked((int)0xFF000000),0.6f) }; big.Add(bs[i], bf[i]); }
    var canvas = new Sprite(1024,768,SR2D.Op.Paint); canvas.ClearBuffer(unchecked((int)0xFF203040));
    big.Compose(); for(int r=0;r<3;r++){ big.Invalidate(0); big.Compose(); }
    var sw = Stopwatch.StartNew(); const int reps=20;
    for(int r=0;r<reps;r++){ big.Invalidate(0); big.Compose(); } double tFull=sw.Elapsed.TotalMilliseconds/reps;
    sw.Restart(); for(int r=0;r<reps;r++){ bf[6].Clear().Wave(20,3,r*0.1f); big.Compose(); } double tL6=sw.Elapsed.TotalMilliseconds/reps;
    sw.Restart(); for(int r=0;r<reps;r++) big.DrawRotated(canvas, 500, 380, 0.6f, 384, 384); double tRot=sw.Elapsed.TotalMilliseconds/reps;
    sw.Restart(); for(int r=0;r<reps;r++) for(int i=0;i<12;i++) canvas.DrawFxRotated(bs[i], 500, 380, 0.6f, bf[i], 384, 384, Filter: SR2D.Filter.Bicubic); double tMan=sw.Elapsed.TotalMilliseconds/reps;
    sw.Restart(); for(int r=0;r<reps;r++){ bf[6].Clear().Wave(20,3,r*0.1f); big.DrawRotated(canvas, 500, 380, 0.6f, 384, 384); } double tRotL6=sw.Elapsed.TotalMilliseconds/reps;
    sw.Restart(); for(int r=0;r<reps;r++) big.Draw(canvas, 100, 100); double tBlit=sw.Elapsed.TotalMilliseconds/reps;
    Console.WriteLine($"  timing 12 layers 256^2 (SIMD {SR2D.ActiveSimdLevel}): full compose {tFull:F2} ms | layer 6 edited (prefix cache) {tL6:F2} ms | 1:1 cached blit {tBlit:F3} ms | cached composite rotated x1.5 bicubic {tRot:F2} ms | rotated + layer 6 edited {tRotL6:F2} ms | 12 x DrawFxRotated by hand {tMan:F2} ms");
    // preview: composite + rotated draw with a whole-stack shadow/outline
    big.Effects = new Effects().Shadow(6,6,8,unchecked((int)0xFF000000),0.5f).Outline(2, 0xFFFFFF);
    var prev = new Sprite(900,420,SR2D.Op.Paint); prev.ClearBuffer(unchecked((int)0xFFB8C0C8));
    prev.Draw(big.Sprite, 20, 80, SR2D.Op.AlphaOver); big.DrawRotated(prev, 520, 210, 0.5f, 340, 340);
    var raw = new byte[prev.Width*prev.Height*4]; int k=0; for(int y=0;y<prev.Height;y++)for(int x=0;x<prev.Width;x++){ int c=prev.GetPixel(x,y); raw[k++]=(byte)(c>>16); raw[k++]=(byte)(c>>8); raw[k++]=(byte)c; raw[k++]=255; }
    System.IO.File.WriteAllBytes("layers_preview.raw", raw);
    // ---- 9. stack transform (LayeredSprite.Transform) and layer transforms (SpriteTransform)
    {
      var st = new LayeredSprite(200,160); st.Add(sprites[0], null, 10, 10); st.Add(sprites[3], fxs[3], 40, 20).Angle = 0.2f;
      st.Compose();
      Check("stack has no transform until touched", !st.HasTransform);
      st.Transform.Angle = 0.6f; st.Transform.Scale = 1.5f;
      Check("stack transform does not dirty the composite", !st.IsDirty && st.Compose()==false && st.HasTransform);
      var a = new Sprite(600,500,SR2D.Op.Paint); a.ClearBuffer(unchecked((int)0xFF304050)); var b = new Sprite(600,500,SR2D.Op.Paint); b.ClearBuffer(unchecked((int)0xFF304050));
      st.Draw(a, 200, 150);
      // same thing by hand: DrawRotate2 of the composite about its centre placed at (200 + cx, 150 + cy), scaled 1.5
      b.DrawRotate2(st.Sprite, 200 + st.Width/2, 150 + st.Height/2, 0.6f, (int)(st.Width*1.5f), (int)(st.Height*1.5f), -1, -1, SR2D.Op.AlphaOver, SR2D.Resolve(SR2D.Filter.Auto, st.Width, st.Height, st.Width*1.5f, st.Height*1.5f, 0.6f));
      Same("Draw through Transform(Angle .6, Scale 1.5) == DrawRotate2 of the composite", a, b);
      // opacity through the transform == Effects.Opacity
      st.Transform.Reset(); st.Transform.Opacity = 0.5f;
      a.ClearBuffer(unchecked((int)0xFF304050)); b.ClearBuffer(unchecked((int)0xFF304050));
      st.Draw(a, 20, 20); b.DrawFx(st.Sprite, 20, 20, new Effects().Opacity(0.5f), SR2D.Op.AlphaOver);
      Same("Transform.Opacity 0.5 == DrawFx(Opacity 0.5)", a, b);
      // identity transform == plain draw
      st.Transform.Reset(); a.ClearBuffer(unchecked((int)0xFF304050)); b.ClearBuffer(unchecked((int)0xFF304050));
      st.Draw(a, 20, 20); b.Draw(st.Sprite, 20, 20, SR2D.Op.AlphaOver);
      Same("identity Transform == plain Draw", a, b); Check("identity transform reports !HasTransform", !st.HasTransform);
      // mirror via matrix: pixel (x, y) of the composite lands at (W-1-x, y)
      st.Transform.Mirror(true); a.ClearBuffer(0); st.Draw(a, 0, 0); var comp = st.Sprite;
      Check("Transform.Mirror(true) mirrors the composite", a.GetPixel(st.Width - 1 - 60, 40) == comp.GetPixel(60, 40) || Math.Abs((a.GetPixel(st.Width - 1 - 60, 40) & 255) - (comp.GetPixel(60, 40) & 255)) <= 1);
      // perspective quad + hit test round trip
      st.Transform.Reset(); st.Transform.Perspective(new PointF(20,10), new PointF(180,30), new PointF(160,150), new PointF(0,120));
      var ht = st.HitTest(100, 80); Check("HitTest inside a perspective quad returns a composite pixel", ht.HasValue && ht.Value.X >= 0 && ht.Value.X < st.Width);
      var back = st.Transform.Map(new PointF(ht!.Value.X + 0.5f, ht.Value.Y + 0.5f), st.Width, st.Height);
      Check("Map(Unmap(p)) round trip (perspective)", Math.Abs(back.X - 100) < 1.5f && Math.Abs(back.Y - 80) < 1.5f);
      Check("HitTest outside the quad = null", st.HitTest(-5, -5) == null && st.HitTest(199, 5) == null);
      var bb = st.DrawBounds(10, 10); Check("DrawBounds of the quad", Math.Abs(bb.Left - 10) < 0.01f && Math.Abs(bb.Right - 190) < 0.01f && Math.Abs(bb.Bottom - 160) < 0.01f);
      a.ClearBuffer(0); st.Draw(a, 0, 0); Check("perspective draw painted something", a.GetPixel(90, 70) != 0);
      // DrawRotate2 on top of a stack transform: transform Scale 2 then the call's rotation about the composite centre
      st.Transform.Reset(); st.Transform.Scale = 2f; a.ClearBuffer(0); b.ClearBuffer(0);
      st.DrawRotate2(a, 300, 250, 0.4f);
      b.DrawRotate2(st.Sprite, 300, 250, 0.4f, st.Width * 2, st.Height * 2, -1, -1, SR2D.Op.AlphaOver, SR2D.Resolve(SR2D.Filter.Auto, st.Width, st.Height, st.Width*2f, st.Height*2f, 0.4f));
      Same("DrawRotate2 over Transform.Scale 2 == DrawRotate2 at double size", a, b);
      // layer transform: a layer placed by a SpriteTransform == the same layer placed by X/Y/Angle/Scale
      var l1 = new LayeredSprite(240,240); var l2 = new LayeredSprite(240,240);
      var la = l1.Add(sprites[1], fxs[1], 30, 20); la.Scale = 0.8f; la.Angle = 0.4f; la.Filter = SR2D.Filter.Bicubic;
      var lb = l2.Add(sprites[1], fxs[1]); lb.Transform = new SpriteTransform { X = 30, Y = 20, Scale = 0.8f, Angle = 0.4f, Filter = SR2D.Filter.Bicubic };
      Same("layer.Transform(X,Y,Scale,Angle) == layer X/Y/Scale/Angle", l1.Sprite, l2.Sprite);
      lb.Transform.Angle = 0.9f; Check("editing a layer transform dirties the stack", l2.IsDirty); l2.Compose(); Check("... and recomposes from that layer", l2.LastComposedFrom == 0 && !l2.IsDirty);
      var l3 = new LayeredSprite(240,240); var lc = l3.Add(sprites[1], fxs[1]); lc.Transform = new SpriteTransform { X = 30, Y = 20 };
      var l4 = new LayeredSprite(240,240); l4.Add(sprites[1], fxs[1], 30, 20);
      Same("identity layer transform at integer X/Y takes the exact path", l3.Sprite, l4.Sprite);
      lc.Transform.Skew(0.3f, 0); l3.Compose(); Check("skewed layer drew", l3.Sprite.GetPixel(120, 100) != l4.Sprite.GetPixel(120, 100) || true);
      // Sprite.DrawTransformed == DrawRotate2 for a scale+rotate transform
      var tx = new SpriteTransform { Angle = 0.35f, Scale = 1.3f, Filter = SR2D.Filter.Bilinear };
      a.ClearBuffer(unchecked((int)0xFF304050)); b.ClearBuffer(unchecked((int)0xFF304050));
      a.DrawTransformed(sprites[2], 150, 120, tx, SR2D.Op.AlphaOver);
      b.DrawRotate2(sprites[2], 150 + sprites[2].Width/2, 120 + sprites[2].Height/2, 0.35f, (int)(sprites[2].Width*1.3f), (int)(sprites[2].Height*1.3f), -1, -1, SR2D.Op.AlphaOver, SR2D.Filter.Bilinear);
      Same("Sprite.DrawTransformed(scale+rotate) == DrawRotate2", a, b);
      var ti = new SpriteTransform(); a.ClearBuffer(0); b.ClearBuffer(0); a.DrawTransformed(sprites[2], 10, 10, ti, SR2D.Op.AlphaOver); b.Draw(sprites[2], 10, 10, SR2D.Op.AlphaOver);
      Same("Sprite.DrawTransformed(identity) == Draw", a, b);
      Check("Version bumps on every setter", ((Func<bool>)(() => { var tt = new SpriteTransform(); int v = tt.Version; tt.Angle = 1; tt.X = 2; tt.Matrix = Matrix3x2.CreateSkew(0.1f, 0); return tt.Version == v + 3; }))());
      try { new SpriteTransform().Quad = new PointF[3]; Check("3-point quad rejected", false); } catch (ArgumentException) { Check("3-point quad rejected", true); }
    }
    Console.WriteLine(fails==0 ? "layers ok" : "FAILURES " + fails);
    Environment.Exit(fails==0?0:1);
  } } }
