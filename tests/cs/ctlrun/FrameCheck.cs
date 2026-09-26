// TransformFrame (demo/TransformFrame.cs) editor-interaction check: move / corner scale (anchor = opposite corner,
// Shift = aspect) / edge stretch (rotated too) / rotate ring (Shift snaps) / Ctrl-corner perspective / hit tests.
using System; using System.Drawing; using System.Windows.Forms; using Sr2d64CSport;
namespace Sr2d64CSport {
static class FrameCheck {
  static int fails; static void Check(string w, bool ok) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + w); if (!ok) fails++; }
  static bool Near(PointF a, PointF b, float t = 0.6f) => Math.Abs(a.X - b.X) < t && Math.Abs(a.Y - b.Y) < t;
  public static int Run() {
    var f = new TransformFrame(); f.Attach(200, 100, 300, 200);
    // move
    var c0 = f.Corner(0); Check("hit inside", f.HitTest(400, 250, out _) == TransformFrame.Part.Inside);
    f.MouseDown(400, 250, Keys.None); f.MouseMove(430, 260, Keys.None); f.MouseUp();
    Check("move by (30,10)", f.Transform.X == 30 && f.Transform.Y == 10 && Near(f.Corner(0), new PointF(c0.X + 30, c0.Y + 10)));
    f.Reset();
    // corner BR drag: TL must stay, BR follows the mouse
    var tl = f.Corner(0); var br = f.Corner(2); Check("hit BR corner", f.HitTest(br.X, br.Y, out int ci) == TransformFrame.Part.Corner && ci == 2);
    f.MouseDown(br.X, br.Y, Keys.None); f.MouseMove(br.X + 100, br.Y + 50, Keys.None); f.MouseUp();
    Check("BR drag: TL anchored", Near(f.Corner(0), tl)); Check("BR drag: BR at the mouse", Near(f.Corner(2), new PointF(br.X + 100, br.Y + 50)));
    Check("BR drag: scale 1.5 x 1.5", Math.Abs(f.Transform.ScaleX - 1.5f) < 1e-3 && Math.Abs(f.Transform.ScaleY - 1.5f) < 1e-3);
    // shift keeps aspect
    f.Reset(); br = f.Corner(2); f.MouseDown(br.X, br.Y, Keys.None); f.MouseMove(br.X + 100, br.Y, Keys.Shift); f.MouseUp();
    Check("Shift corner: uniform", Math.Abs(f.Transform.ScaleX - f.Transform.ScaleY) < 1e-3 && Math.Abs(f.Transform.ScaleX - 1.5f) < 1e-3);
    // edge: right midpoint stretches X only, left edge anchored
    f.Reset(); var lm = f.EdgeMid(3); var rm = f.EdgeMid(1); f.MouseDown(rm.X, rm.Y, Keys.None); f.MouseMove(rm.X + 50, rm.Y + 30, Keys.None); f.MouseUp();
    Check("right edge: X only, 1.25", Math.Abs(f.Transform.ScaleX - 1.25f) < 1e-3 && f.Transform.ScaleY == 1f && Near(f.EdgeMid(3), lm));
    // top edge with a rotated frame: anchor = bottom mid stays
    f.Reset(); f.Transform.Angle = 0.5f; var bm = f.EdgeMid(2); var tm = f.EdgeMid(0);
    f.MouseDown(tm.X, tm.Y, Keys.None); f.MouseMove(tm.X - 20, tm.Y - 40, Keys.None); f.MouseUp();
    Check("rotated top edge: bottom mid anchored", Near(f.EdgeMid(2), bm)); Check("rotated top edge: X unchanged", f.Transform.ScaleX == 1f && f.Transform.ScaleY > 1f);
    // rotate ring: 90 degrees around the pivot
    f.Reset(); var pv = f.PivotOnCanvas(); float rx = f.Corner(1).X + 15, ry = pv.Y;
    Check("hit rotate ring", f.HitTest(rx, ry, out _) == TransformFrame.Part.Rotate);
    f.MouseDown(rx, ry, Keys.None); f.MouseMove(pv.X, pv.Y + (rx - pv.X), Keys.None); f.MouseUp();
    Check("rotate ring: +90 deg", Math.Abs(f.Transform.AngleDegrees - 90) < 0.5f && Near(f.PivotOnCanvas(), pv));
    // shift snaps
    f.Reset(); pv = f.PivotOnCanvas(); f.MouseDown(rx, ry, Keys.None); f.MouseMove(pv.X + 100, pv.Y + 40, Keys.Shift); f.MouseUp();
    Check("Shift rotate snaps to 15 deg", Math.Abs(f.Transform.AngleDegrees % 15) < 0.01f || Math.Abs(f.Transform.AngleDegrees % 15 - 15) < 0.01f);
    // ctrl corner: perspective; other corners fixed
    f.Reset(); var c1 = f.Corner(1); var c2 = f.Corner(2); var c3 = f.Corner(3); tl = f.Corner(0);
    f.MouseDown(tl.X, tl.Y, Keys.Control); f.MouseMove(tl.X + 30, tl.Y + 20, Keys.Control); f.MouseUp();
    Check("Ctrl corner: quad", f.Transform.Quad != null && Near(f.Corner(1), c1) && Near(f.Corner(2), c2) && Near(f.Corner(3), c3) && Near(f.Corner(0), new PointF(tl.X + 30, tl.Y + 20)));
    // then a plain corner drag goes back to affine
    var b2 = f.Corner(2); f.MouseDown(b2.X, b2.Y, Keys.None); f.MouseMove(b2.X + 10, b2.Y, Keys.None); f.MouseUp(); Check("plain drag after perspective: affine again", f.Transform.Quad == null);
    // hit test through inverse mapping
    f.Reset(); f.Transform.Angle = 0.7f; var mid = f.PivotOnCanvas(); var src = f.ToSource(mid.X, mid.Y); Check("ToSource(pivot) = centre", Math.Abs(src.X - 100) < 0.01f && Math.Abs(src.Y - 50) < 0.01f);
    Check("outside = None or Rotate", f.HitTest(10, 10, out _) == TransformFrame.Part.None);
    // draws without throwing (headless: SpriteCursors falls back to stock cursors)
    using var cv = new Sprite(800, 600); cv.ClearBuffer(unchecked((int)0xFF202020)); f.Draw(cv); Check("draw ok", cv.GetPixel((int)f.Corner(0).X, (int)f.Corner(0).Y) != unchecked((int)0xFF202020));
    Console.WriteLine(fails == 0 ? "framechk: all ok" : $"framechk: {fails} failures"); return fails;
  }
}
}
