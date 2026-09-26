using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>
    /// The "spiralling fractal" copy placement shared by the &lt;new&gt; tests: copy 1 fills the viewport; every further copy
    /// divides the NEXT part - the most recently created region - in half across its longer side. 2 = left | right,
    /// 3 = left | right-top over right-bottom, 4 = the right-bottom splits into two side by side, and so on: the split
    /// line spirals through the viewport and the older regions keep their size (exactly the reference screenshots).
    /// A split happens only while both halves stay &gt;= <see cref="MinHalf"/> px - <see cref="MaxRegions"/> is the hard
    /// copy cap, and the rectangles are meant to be precalculated once per viewport size (the tests cache them).
    /// </summary>
    internal static class FractalLayout
    {
        /// <summary>Smallest allowed half: a region is divided only while both halves stay at least this many pixels.</summary>
        public const int MinHalf = 16;

        /// <summary>How many regions the viewport can take before the next split would go below <see cref="MinHalf"/>.</summary>
        public static int MaxRegions(int w, int h)
        {
            int n = 1, rw = Math.Max(1, w), rh = Math.Max(1, h);
            while (Math.Max(rw, rh) >= 2 * MinHalf)
            {
                if (rw >= rh) { int half = rw / 2; if (half < MinHalf || rw - half < MinHalf) break; rw -= half; }
                else { int half = rh / 2; if (half < MinHalf || rh - half < MinHalf) break; rh -= half; }
                n++;
            }
            return n;
        }

        /// <summary>Partitions the viewport into exactly <paramref name="count"/> regions (capped at <see cref="MaxRegions"/>), oldest first, the newest last.</summary>
        public static List<Rectangle> Regions(int w, int h, int count)
        {
            var r = new List<Rectangle>();
            var cur = new Rectangle(0, 0, Math.Max(1, w), Math.Max(1, h));   // the newest region (the next one to be divided)
            for (int k = 1; k < Math.Max(1, count); k++)
            {
                if (Math.Max(cur.Width, cur.Height) < 2 * MinHalf) break;    // not dividable anymore
                Rectangle second;
                if (cur.Width >= cur.Height)
                {
                    int half = cur.Width / 2;
                    if (half < MinHalf || cur.Width - half < MinHalf) break;
                    r.Add(new Rectangle(cur.X, cur.Y, half, cur.Height));
                    second = new Rectangle(cur.X + half, cur.Y, cur.Width - half, cur.Height);
                }
                else
                {
                    int half = cur.Height / 2;
                    if (half < MinHalf || cur.Height - half < MinHalf) break;
                    r.Add(new Rectangle(cur.X, cur.Y, cur.Width, half));
                    second = new Rectangle(cur.X, cur.Y + half, cur.Width, cur.Height - half);
                }
                cur = second;                                                // the second half is the next part to divide
            }
            r.Add(cur);
            return r;
        }
    }

    /// <summary>State + control strip of the "MoveByte (channel swap) &lt;new&gt;" test: the combo selects which two channels the
    /// first copy swaps, the remaining copies switch channels randomly; the draw code folds Pair into its cache key.</summary>
    internal static class SwapDemo
    {
        public static readonly string[] Names = { "R <-> G", "R <-> B", "R <-> A", "G <-> B", "G <-> A", "B <-> A" };
        public static readonly SR2D.ColChannel[][] Pairs =
        {
            new[] { SR2D.ColChannel.ChRed, SR2D.ColChannel.ChGreen }, new[] { SR2D.ColChannel.ChRed, SR2D.ColChannel.ChBlue },
            new[] { SR2D.ColChannel.ChRed, SR2D.ColChannel.ChAlpha }, new[] { SR2D.ColChannel.ChGreen, SR2D.ColChannel.ChBlue },
            new[] { SR2D.ColChannel.ChGreen, SR2D.ColChannel.ChAlpha }, new[] { SR2D.ColChannel.ChBlue, SR2D.ColChannel.ChAlpha },
        };
        public static int Pair;                      // index into Pairs - used by the first copy
        public const int StripHeight = 40;
        static readonly SpriteCombo Combo = new();

        public static Control Build()
        {
            var strip = new SpriteStackPanel { Orientation = Orientation.Horizontal, Wrap = true, AutoSize = true, Dock = DockStyle.Fill, Gap = 8, Padding = new Padding(4, 2, 4, 2), Stretch = false };
            strip.Controls.Add(new SpriteLabel { Text = "Swap channels (1st copy; the others switch randomly):", AutoSize = false, Height = 24, Width = 262 });
            Combo.SetItems(Names, Pair); Combo.Height = 24; Combo.Width = 110;
            Combo.SelectedIndexChanged += (_, _) => Pair = Math.Max(0, Combo.SelectedIndex);
            strip.Controls.Add(Combo);
            return strip;
        }
    }
}
