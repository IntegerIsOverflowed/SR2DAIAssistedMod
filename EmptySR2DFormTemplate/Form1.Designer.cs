namespace Sr2d64CSport
{
    // 'public' has to be repeated here: C# rejects a partial class whose declarations disagree about
    // accessibility (CS0262), and the WinForms designer needs the root class to be public anyway.
    public partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            // base.Dispose releases the SpriteForm chrome (its title bar surface and the window menu)
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            SuspendLayout();
            // 
            // Form1
            // 
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(46, 52, 60);
            ClientSize = new Size(721, 511);
            Font = new Font("Segoe UI Historic", 9F, FontStyle.Regular, GraphicsUnit.Point, 0);
            MinimumSize = new Size(235, 87);
            Name = "Form1";
            ShowCloseButton = true;
            ShowMaximizeButton = true;
            ShowMinimizeButton = true;
            StartPosition = FormStartPosition.CenterScreen;
            StripeBarCount = 0;
            StripeBarExtra = 1;
            StripeBarWidth = 12;
            Text = " Test form...";
            TitleDarkenMode = TitleDarkenMode.BehindTitle;
            TitleDarkenExtraWidth = 5;
            TitleDarkenExtraHeight = 2;
            TitleDarkenFeatherWidth = 3;
            TitleDarkenFeatherHeight = 3;
            TitleDarkenOpacity = 60;
            Load += Form1_Load;
            ResumeLayout(false);
        }

        #endregion
    }
}
