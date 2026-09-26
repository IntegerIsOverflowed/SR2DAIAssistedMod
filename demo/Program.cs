using System;
using System.IO;
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
                MessageBox.Show("SR2DDemo must run as a 64-bit process (SR2D64.dll is x64).", "SR2D Demo");
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            // Unhandled exceptions are logged to crash.log next to the exe and shown with the details pre-copied to the
            // clipboard, instead of the default Continue/Quit dialog. (A StackOverflowException is process-fatal and
            // cannot be caught - everything else, on any thread, lands here.)
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ShowCrash(e.Exception, terminating: false);          // UI thread
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject, System.Globalization.CultureInfo.InvariantCulture)), terminating: true);   // background threads: log only
            Application.Run(new MainForm());
        }

        /// <summary>Appends the exception to crash.log and shows it; the report is already on the clipboard.</summary>
        static void ShowCrash(Exception ex, bool terminating)
        {
            string log = Path.Combine(AppContext.BaseDirectory, "crash.log");
            try { File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(terminating ? "TERMINATING " : "")}{ex}\r\n\r\n"); } catch { /* best effort */ }
            string full = $"{ex.GetType().Name}: {ex.Message}{(terminating ? "\r\n(the process cannot continue)" : "")}\r\n\r\n{ex}";
            var lines = full.Split('\n');
            if (lines.Length > 40) full = string.Join("\n", lines, 0, 40) + $"\n... ({lines.Length - 40} more lines - in crash.log)";
            if (File.Exists(log)) full += $"\r\n\r\nlogged to:\r\n{log}";
            try { Clipboard.SetText(full); } catch { }
            MessageBox.Show(full, "SR2D Demo - unhandled exception", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
