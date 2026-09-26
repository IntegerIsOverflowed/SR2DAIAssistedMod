using System;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            if (!Environment.Is64BitProcess)
            {
                MessageBox.Show("This application must run as a 64-bit process (SR2D64.dll is x64).", "SR2D");
                return;
            }
            ApplicationConfiguration.Initialize();      // visual styles, DPI mode (ApplicationHighDpiMode in the csproj), text rendering
            Application.Run(new MainForm());
        }
    }
}
