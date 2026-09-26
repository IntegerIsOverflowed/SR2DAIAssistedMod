using System; using System.Drawing;
namespace Sr2d64CSport {
static class P {
  static int Cmp(Sprite a, Sprite b){ int n=0; for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++) if(a.GetPixel(x,y)!=b.GetPixel(x,y)) n++; return n; }
  static unsafe void Main() {
    Console.WriteLine("1:1        -> " + SR2D.Resolve(SR2D.Filter.Auto, 100, 80, 100, 80));
    Console.WriteLine("x2         -> " + SR2D.Resolve(SR2D.Filter.Auto, 100, 80, 200, 160));
    Console.WriteLine("x1.3       -> " + SR2D.Resolve(SR2D.Filter.Auto, 100, 80, 130, 104));
    Console.WriteLine("x0.75      -> " + SR2D.Resolve(SR2D.Filter.Auto, 100, 80, 75, 60));
    Console.WriteLine("x0.5       -> " + SR2D.Resolve(SR2D.Filter.Auto, 100, 80, 50, 40));
    Console.WriteLine("x0.2       -> " + SR2D.Resolve(SR2D.Filter.Auto, 100, 80, 20, 16));
    Console.WriteLine("1:1 rot30  -> " + SR2D.Resolve(SR2D.Filter.Auto, 100, 80, 100, 80, 0.5236f));
    Console.WriteLine("x0.3 rot30 -> " + SR2D.Resolve(SR2D.Filter.Auto, 100, 80, 30, 24, 0.5236f));
    Console.WriteLine("x2 by x0.4 -> " + SR2D.Resolve(SR2D.Filter.Auto, 100, 80, 200, 32));
    Console.WriteLine("Bilinear passthrough -> " + SR2D.Resolve(SR2D.Filter.Bilinear, 100, 80, 20, 16));
    var src = new Sprite(128, 96, SR2D.Op.Paint); var r = new Random(3); for(int y=0;y<96;y++)for(int x=0;x<128;x++) src.SetPixel(x,y, r.Next()|unchecked((int)0xff000000));
    (int w,int h,float ang, SR2D.Filter expect)[] cases = { (128,96,0,SR2D.Filter.Nearest), (256,192,0,SR2D.Filter.Bicubic), (96,72,0,SR2D.Filter.Bilinear), (40,30,0,SR2D.Filter.BicubicArea), (128,96,0.7f,SR2D.Filter.Bicubic) };
    int fails=0;
    foreach (var c in cases) {
      var a = new Sprite(400, 300, SR2D.Op.Paint); a.ClearBuffer(0x11223344); var b = new Sprite(400, 300, SR2D.Op.Paint); b.ClearBuffer(0x11223344);
      a.DrawRotated(src, 200, 150, c.ang, c.w, c.h, Op: SR2D.Op.Paint, Filter: SR2D.Filter.Auto);
      b.DrawRotated(src, 200, 150, c.ang, c.w, c.h, Op: SR2D.Op.Paint, Filter: c.expect);
      int d = Cmp(a,b); Console.WriteLine($"draw {c.w}x{c.h} rot {c.ang}: Auto == {c.expect}: {(d==0?"identical":d+" px differ")}"); if(d!=0) fails++;
      var fx = new Effects().Shadow(3,3,4, 0xFF000000 == 0 ? 0 : unchecked((int)0xFF000000)).Outline(2, 0xFFFFFF).Glow(4, 0x40C0FF, 0.5f);
      var e = new Sprite(400, 300, SR2D.Op.Paint); e.ClearBuffer(0x11223344); var f = new Sprite(400, 300, SR2D.Op.Paint); f.ClearBuffer(0x11223344);
      e.DrawFxRotated(src, 200, 150, c.ang, fx, c.w, c.h, Filter: SR2D.Filter.Auto); f.DrawFxRotated(src, 200, 150, c.ang, fx, c.w, c.h, Filter: c.expect);
      d = Cmp(e,f); Console.WriteLine($"  DrawFx same case: {(d==0?"identical":d+" px differ")}"); if(d!=0) fails++;
    }
    // int-colour vs Drawing.Color overloads give the same stages
    var g1 = new Sprite(200,200,SR2D.Op.Paint); g1.ClearBuffer(0); var g2 = new Sprite(200,200,SR2D.Op.Paint); g2.ClearBuffer(0);
    g1.DrawFx(src, 30, 40, new Effects().Shadow(4,4,5, unchecked((int)0xFF203040), 0.7f, 1).Glow(6, 0xFFD700, 0.8f).Outline(2, 0xFFFFFF).Tint(0xFF0000, 0.3f));
    g2.DrawFx(src, 30, 40, new Effects().Shadow(4,4,5, Color.FromArgb(0x20,0x30,0x40), 0.7f, 1).Glow(6, Color.Gold, 0.8f).Outline(2, Color.White).Tint(Color.Red, 0.3f));
    int dd = Cmp(g1,g2); Console.WriteLine("int ARGB vs Drawing.Color overloads: " + (dd==0?"identical":dd+" px differ")); if(dd!=0) fails++;
    Console.WriteLine(fails==0 ? "auto ok" : "FAILURES " + fails);
  } } }
