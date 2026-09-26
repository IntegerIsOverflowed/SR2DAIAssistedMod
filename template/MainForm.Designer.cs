namespace Sr2d64CSport
{
    partial class MainForm
    {
        /// <summary>Required designer variable.</summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>Clean up any resources being used.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify the contents of this method with the code editor.
        /// (The SR2D controls carry the designer attributes, so the WinForms designer can open this form and
        /// you can drop more of them from the toolbox - they appear there once the project has been built.)
        /// </summary>
        private void InitializeComponent()
        {
            canvas = new SpriteBox();
            sidePanel = new SpritePanel();
            title = new SpriteLabel();
            angleKnob = new SpriteKnob();
            sizeSlider = new SpriteSlider();
            smoothToggle = new SpriteToggle();
            resetButton = new SpriteButton();
            statusLabel = new SpriteLabel();
            sidePanel.SuspendLayout();
            SuspendLayout();
            // 
            // canvas
            // 
            canvas.BackColor = System.Drawing.Color.FromArgb(32, 36, 40);
            canvas.Dock = System.Windows.Forms.DockStyle.Fill;
            canvas.Location = new System.Drawing.Point(0, 0);
            canvas.Name = "canvas";
            canvas.Size = new System.Drawing.Size(724, 461);
            canvas.TabIndex = 0;
            canvas.Render += canvas_Render;
            // 
            // sidePanel
            // 
            sidePanel.Controls.Add(title);
            sidePanel.Controls.Add(angleKnob);
            sidePanel.Controls.Add(sizeSlider);
            sidePanel.Controls.Add(smoothToggle);
            sidePanel.Controls.Add(resetButton);
            sidePanel.Controls.Add(statusLabel);
            sidePanel.Dock = System.Windows.Forms.DockStyle.Right;
            sidePanel.Location = new System.Drawing.Point(724, 0);
            sidePanel.Name = "sidePanel";
            sidePanel.Padding = new System.Windows.Forms.Padding(10);
            sidePanel.Size = new System.Drawing.Size(240, 461);
            sidePanel.Style = PanelStyle.Flat;
            sidePanel.TabIndex = 1;
            // 
            // title
            // 
            title.AutoSize = false;
            title.Location = new System.Drawing.Point(12, 12);
            title.Name = "title";
            title.Size = new System.Drawing.Size(216, 18);
            title.Style = LabelStyle.Heading;
            title.TabIndex = 0;
            title.Text = "SR2D app";
            // 
            // angleKnob
            // 
            angleKnob.Location = new System.Drawing.Point(12, 40);
            angleKnob.Maximum = 360D;
            angleKnob.Name = "angleKnob";
            angleKnob.Size = new System.Drawing.Size(96, 112);
            angleKnob.TabIndex = 1;
            angleKnob.Text = "Angle";
            angleKnob.Unit = "\u00B0";
            angleKnob.Value = 30D;
            angleKnob.ValueChanged += Controls_Changed;
            // 
            // sizeSlider
            // 
            sizeSlider.Location = new System.Drawing.Point(12, 160);
            sizeSlider.Maximum = 300D;
            sizeSlider.Minimum = 20D;
            sizeSlider.Name = "sizeSlider";
            sizeSlider.Size = new System.Drawing.Size(216, 44);
            sizeSlider.TabIndex = 2;
            sizeSlider.Text = "Size";
            sizeSlider.Unit = " px";
            sizeSlider.Value = 160D;
            sizeSlider.ValueChanged += Controls_Changed;
            // 
            // smoothToggle
            // 
            smoothToggle.Checked = true;
            smoothToggle.Location = new System.Drawing.Point(12, 212);
            smoothToggle.Name = "smoothToggle";
            smoothToggle.Size = new System.Drawing.Size(216, 28);
            smoothToggle.TabIndex = 3;
            smoothToggle.Text = "Bilinear filter";
            smoothToggle.CheckedChanged += Controls_Changed;
            // 
            // resetButton
            // 
            resetButton.Location = new System.Drawing.Point(12, 250);
            resetButton.Name = "resetButton";
            resetButton.Size = new System.Drawing.Size(216, 34);
            resetButton.TabIndex = 4;
            resetButton.Text = "Reset";
            resetButton.Click += ResetButton_Click;
            // 
            // statusLabel
            // 
            statusLabel.AutoSize = false;
            statusLabel.Location = new System.Drawing.Point(12, 292);
            statusLabel.Name = "statusLabel";
            statusLabel.Size = new System.Drawing.Size(216, 22);
            statusLabel.Style = LabelStyle.Readout;
            statusLabel.TabIndex = 5;
            statusLabel.Text = "";
            // 
            // MainForm
            // 
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
            BackColor = System.Drawing.Color.FromArgb(32, 36, 40);
            ClientSize = new System.Drawing.Size(964, 461);
            Controls.Add(canvas);
            Controls.Add(sidePanel);
            MinimumSize = new System.Drawing.Size(600, 400);
            Name = "MainForm";
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            Text = "SR2D app";
            sidePanel.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private SpriteBox canvas;
        private SpritePanel sidePanel;
        private SpriteLabel title;
        private SpriteKnob angleKnob;
        private SpriteSlider sizeSlider;
        private SpriteToggle smoothToggle;
        private SpriteButton resetButton;
        private SpriteLabel statusLabel;
    }
}
