using System;
namespace System.Windows.Forms { public class Control { public bool Visible; public int Right; } }
namespace Sr2d64CSport {
  // the controls strip needs WinForms controls; the headless runner only needs the values
  internal static class TangentTools { public static Curve Motion = new(); public static System.Windows.Forms.Control BuildEditor() => new System.Windows.Forms.Control(); }
  internal static class OffsetTools { public static System.Windows.Forms.Control Strip() => new System.Windows.Forms.Control(); public static void SyncAccent(int tool) { } }
  internal static class DiscreteDemo { public static double Steps, Pow2, List, Num, NumV = 32; public static string Last = "-"; public const int StripHeight = 132; public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); }
  internal static class MotionStrip { public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); }
  internal static class ColorDemo { public const int StripHeight = 210; public static int ViewBack = unchecked((int)0xFF101418); public static int PickerArgb = unchecked((int)0xFF40A0FF); public static string LastAction = "none"; public static int Edits; public static System.Windows.Forms.Control? ColorOverlay; public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); }
  internal static class ControlsDemo { public static double AngleDeg, Offset, ScalePct = 100, Blend = 128, Brite = 100; public static int Events; public static string LastSource = "", OffsetMode = ""; public static double Hue = 200, Level = 1; public const int StripHeight = 318, ButtonsStripHeight = 330; public static double Progress; public static bool Running, Spin = true, Bilinear = true, Backdrop, Grid; public static int OpChoice, SizeChoice = 1; public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); public static System.Windows.Forms.Control BuildButtons() => new System.Windows.Forms.Control(); }
  internal static class SwapDemo
  {   // Tests.cs reads Pair / Pairs / Names; the strip lives in the WinForms shell - keep values in sync with demo/FractalLayout.cs
    public static readonly string[] Names = { "R <-> G", "R <-> B", "R <-> A", "G <-> B", "G <-> A", "B <-> A" };
    public static readonly SR2D.ColChannel[][] Pairs =
    {
      new[] { SR2D.ColChannel.ChRed, SR2D.ColChannel.ChGreen }, new[] { SR2D.ColChannel.ChRed, SR2D.ColChannel.ChBlue },
      new[] { SR2D.ColChannel.ChRed, SR2D.ColChannel.ChAlpha }, new[] { SR2D.ColChannel.ChGreen, SR2D.ColChannel.ChBlue },
      new[] { SR2D.ColChannel.ChGreen, SR2D.ColChannel.ChAlpha }, new[] { SR2D.ColChannel.ChBlue, SR2D.ColChannel.ChAlpha },
    };
    public static int Pair;
    public const int StripHeight = 96; public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control();
  }
  internal static class RecolorDemo { public const int StripHeight = 96; public static VectorImage? Image; public static int PickMode, Edits; public static string Status = ""; public static void SetImage(VectorImage img) { Image = img; } public static void PickAt(System.Drawing.PointF p) { } public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); }
  internal static class ViewDemo { public const int StripHeight = 560; public static string State = "", Hover = ""; public static int Frames; public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); }
  internal static class FormDemo { public static string Caption = "SR2D"; public static int Copies = 3, Spacing = 24, Layout = 0; public static double Tint = 200; public static bool TintOn, Frame = true, Shadow; public static readonly System.Collections.Generic.List<string> Picked = new(); public static string Status = "idle"; public static int Events; public static string LastSource = ""; public const int StripHeight = 300; public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); }
  internal static class FontDemo { public const int StripHeight = 118; public static string Text = "The quick brown fox jumps over the lazy dog. 0123456789 fi fl AV To"; public static double Bold; public static bool UseCache = true; public static SpriteFont? Get() => DemoFont.Get(); public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); }
  internal static class VoxelDemo { public const int StripHeight = 560; public static string State = "", Hit = ""; public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); }
  /// <summary>Headless stand-in for demo/OutputDemo.cs: the four output routes need real windows (a PictureBox,
  /// SpriteBoxes, a fullscreen Form), so headlessly only the bench SCENE is drawn on the canvas - the same gradient
  /// and the same orbiting sprites, drawn the same way - which keeps the test body exercising Sprite.Draw.</summary>
  internal static class OutputDemo {
    public const int StripHeight = 292;
    public static int Copies = 12;
    public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control();
    static Sprite? col, alpha;
    public static void Canvas(Ctx c)
    {
      var t = c.Canvas; int w = t.Width, h = t.Height;
      if (w < 8 || h < 8) return;
      if (col == null) { col = Resized(c.A.Color, 64); alpha = Resized(c.A.Alpha, 64); }
      var s = col; var al = alpha!;
      t.ClearBuffer(unchecked((int)0xFF101418));
      for (int y = 0; y < h; y += 2) { int v = 0x16 + (y * 0x2E) / h; t.FillRect(0, y, w, 2, unchecked((int)(0xFF000000u | ((uint)v << 16) | ((uint)(v >> 1) << 8) | 0x24))); }
      float cx = w * 0.5f, cy = h * 0.5f + 6f, rx = w * 0.34f, ry = h * 0.30f;
      int n = Math.Max(1, Copies) * 3;
      for (int i = 0; i < n; i++)
      {
        double a = c.Time * (0.55 + 0.09 * (i % 5)) + i * (Math.PI * 2 / n);
        int x = (int)(cx + (float)Math.Cos(a) * rx) - s!.Width / 2;
        int y = (int)(cy + (float)Math.Sin(a * 1.7) * ry) - s.Height / 2;
        if ((i & 3) == 3) t.Draw(al, x, y, SR2D.Op.AlphaBlend); else t.Draw(s, x, y, SR2D.Op.Paint);
      }
      t.DrawRect(0, 0, w - 1, h - 1, unchecked((int)0xFF485868));
      c.Info("headless benchrun: the four output panes need real windows - only the bench scene is drawn here");
    }
    static Sprite Resized(Sprite src, int edge) => src.Width == edge && src.Height == edge ? Clone(src) : new Sprite(src, SR2D.Transform.None, edge, edge);
    static Sprite Clone(Sprite src) { var z = new Sprite(src.Width, src.Height); src.Pixels.CopyTo(z.Pixels); return z; }
  }
  internal static class LightsDemo {
    public sealed class Lamp { public double Hue; public int Strength = 15; public bool On = true; public int Color => HueToArgb(Hue); }
    public static readonly Lamp[] Lamps = { new Lamp { Hue = 40 }, new Lamp { Hue = 200 }, new Lamp { Hue = 320 } };
    public static int Reach = 15, Sky = 2; public static double LampEnergy = 1.5; public const int StripHeight = 190;
    public static string Key() => $"{Reach}|{Sky}|" + string.Join("|", System.Array.ConvertAll(Lamps, l => $"{l.Hue:0}:{l.Strength}:{l.On}"));
    public static string Describe() => string.Join("  ", System.Array.ConvertAll(Lamps, l => l.On ? $"hue {l.Hue:0} emit {l.Strength}" : "off"));
    public static int HueToArgb(double hue) { if (hue >= 359.5) return unchecked((int)0xFFFFF4E0); double h = hue / 60.0; int i = (int)System.Math.Floor(h) % 6; double f = h - System.Math.Floor(h); double q = 1 - f, t = f; double r, g, b; switch (i) { case 0: r = 1; g = t; b = 0; break; case 1: r = q; g = 1; b = 0; break; case 2: r = 0; g = 1; b = t; break; case 3: r = 0; g = q; b = 1; break; case 4: r = t; g = 0; b = 1; break; default: r = 1; g = 0; b = q; break; } r = 0.2 + 0.8 * r; g = 0.2 + 0.8 * g; b = 0.2 + 0.8 * b; return SR2D.ARGB(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255)); }
    public static System.Windows.Forms.Control Build() => new System.Windows.Forms.Control(); }
  // verbatim copy of demo/FractalLayout.Regions (that file's SwapDemo half needs the WinForms controls,
  // which the headless runner stubs out) - keep in sync with the original
  internal static class FractalLayout
  {
    public const int MinHalf = 16;
    public static System.Collections.Generic.List<System.Drawing.Rectangle> Regions(int w, int h, int count)
    {
      var r = new System.Collections.Generic.List<System.Drawing.Rectangle>();
      var cur = new System.Drawing.Rectangle(0, 0, Math.Max(1, w), Math.Max(1, h));
      for (int k = 1; k < Math.Max(1, count); k++)
      {
        if (Math.Max(cur.Width, cur.Height) < 2 * MinHalf) break;
        System.Drawing.Rectangle second;
        if (cur.Width >= cur.Height)
        {
          int half = cur.Width / 2;
          if (half < MinHalf || cur.Width - half < MinHalf) break;
          r.Add(new System.Drawing.Rectangle(cur.X, cur.Y, half, cur.Height));
          second = new System.Drawing.Rectangle(cur.X + half, cur.Y, cur.Width - half, cur.Height);
        }
        else
        {
          int half = cur.Height / 2;
          if (half < MinHalf || cur.Height - half < MinHalf) break;
          r.Add(new System.Drawing.Rectangle(cur.X, cur.Y, cur.Width, half));
          second = new System.Drawing.Rectangle(cur.X, cur.Y + half, cur.Width, cur.Height - half);
        }
        cur = second;
      }
      r.Add(cur);
      return r;
    }
  }
}
namespace Sr2d64CSport
{
  /// <summary>Headless stand-in for demo/TransformFrame.cs (which needs WinForms cursors): the transform without the handles.</summary>
  internal sealed class TransformFrame
  {
    public readonly Sr2d64CSport.SpriteTransform Transform = new Sr2d64CSport.SpriteTransform();
    public int Width, Height; public float OriginX, OriginY;
    public void Attach(int width, int height, float originX, float originY) { Width = width; Height = height; OriginX = originX; OriginY = originY; }
    public string Describe() => Transform.ToString() ?? "";
  }
}
