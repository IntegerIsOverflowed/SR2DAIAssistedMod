using System;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application. (Exactly the Visual Studio "Windows Forms App"
        ///  template - the form it starts is Form1, which derives from SR2D's SpriteForm.)
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
