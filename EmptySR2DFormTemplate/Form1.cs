using System;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>
    /// The blank form of a new project - nothing on it, exactly what Visual Studio generates.
    /// It derives from <see cref="SpriteForm"/> (cs\SpriteForm.cs), so the window already comes with
    /// the SR2D-drawn title bar (colour bars, icon, caption, minimise / maximise / close) and the
    /// border; drop SR2D controls on it, or draw on it with a SpriteBox.
    ///
    /// PUBLIC, like the class Visual Studio generates: the WinForms designer will not open a form whose
    /// root class is internal, and the property grid only shows the chrome knobs because SpriteForm is
    /// public too. Right-click the file in Solution Explorer -> "View Designer" (F7 cycles the two views).
    ///
    /// Set <c>Text</c> to change the caption, <c>ShowStripes = false</c> to drop the colour bars,
    /// <c>SetTitleTags(new TitleTag("extra", color))</c> to add texts after the caption.
    /// </summary>
    public partial class Form1 : SpriteForm
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
  
        }
    }
}
