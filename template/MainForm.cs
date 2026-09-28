using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>
    /// Starter form: a SpriteBox canvas that fills the window and a few SR2D controls on the right that drive it.
    /// The layout lives in MainForm.Designer.cs (designer-compatible); the drawing lives here in <see cref="canvas_Render"/>.
    ///
    /// The pattern to keep:
    ///   * draw ONLY inside the SpriteBox.Render handler (it hands you the back buffer, already the client size),
    ///   * when a value changes, call canvas.Redraw() - the box repaints once, on the next paint message
    ///     (RedrawNow() if you need the picture updated before the call returns, e.g. while dragging),
    ///   * keep the sprites you draw in fields; load / build them once,
    ///   * and release those fields in Dispose (MainForm.Designer.cs) when the form goes away.
    /// </summary>
    public partial class MainForm : Form
    {
        Sprite? _sprite;                 // the picture we spin around (built procedurally here; new Sprite("file.png") loads one)
                                         // IDisposable fields like this one: release them in Dispose (MainForm.Designer.cs) - CA2213 watches there
        readonly Stopwatch _clock = Stopwatch.StartNew();

        public MainForm()
        {
            InitializeComponent();          // wires canvas.Render += canvas_Render (the designer's Events tab did that)
            BuildSprite();
        }

        // ------------------------------------------------------------------ the picture
        void BuildSprite()
        {
            // a 128 x 128 sprite with transparency: draw with Op.AlphaBlend / AlphaOver onto a zeroed buffer
            _sprite?.Dispose();
            _sprite = new Sprite(128, 128, SR2D.Op.AlphaOver);
            _sprite.ClearBuffer(0);
            _sprite.FillCircle(64, 64, 60, unchecked((int)0xFF3399FF), SR2D.LineOp.Set, true);
            _sprite.FillCircle(64, 64, 44, unchecked((int)0xFF202428), SR2D.LineOp.Set, true);
            _sprite.FillRect(58, 10, 12, 54, unchecked((int)0xFFE8ECF0), SR2D.LineOp.Set, true);
            _sprite.DrawText(64, 92, "SR2D", unchecked((int)0xFFFFFFFF), 0, TextAnchor.Center, 2);
            _sprite.Premultiply();       // AlphaOver expects premultiplied pixels
        }

        // ------------------------------------------------------------------ drawing
        // The Render handler: the designer generated this stub (Properties > Events > Render, double-click),
        // e.Surface is the back buffer at the client size. Draw the whole scene into it every time.
        void canvas_Render(object? sender, RenderEventArgs e)
        {
            Sprite s = e.Surface;
            s.ClearBuffer(unchecked((int)0xFF202428));
            // a faint grid so you can see the canvas size
            int grid = unchecked((int)0xFF2A3036);
            for (int x = 0; x < s.Width; x += 32) s.FillRect(x, 0, 1, s.Height, grid);
            for (int y = 0; y < s.Height; y += 32) s.FillRect(0, y, s.Width, 1, grid);

            if (_sprite == null) return;
            int size = (int)sizeSlider.Value;
            float angle = (float)(angleKnob.Value * Math.PI / 180);
            var filter = smoothToggle.Checked ? SR2D.Filter.Bilinear : SR2D.Filter.Nearest;
            // rotated + scaled about the sprite centre (pivot -1 = centre), placed at the canvas centre
            s.DrawRotate2(_sprite, s.Width / 2, s.Height / 2, angle, size, size, -1, -1, SR2D.Op.AlphaOver, filter);

            statusLabel.Text = $"{s.Width} x {s.Height}  {angleKnob.Value:0}\u00B0  {size} px";
        }

        // ------------------------------------------------------------------ events (wired in the designer file)
        void Controls_Changed(object? sender, EventArgs e) => canvas.Redraw();

        void ResetButton_Click(object? sender, EventArgs e)
        {
            angleKnob.Value = 30; sizeSlider.Value = 160; smoothToggle.Checked = true;
            canvas.Redraw();
        }
    }
}
