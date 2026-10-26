using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Sr2d64CSport
{
    /// <summary>
    /// Opt-in diagnostics for the window-state freeze: start the demo with SR2D_TRACE=&lt;file&gt; and every window
    /// message, Idle-loop pass, canvas rebuild and blocked frame is appended to that file. A watchdog thread notes
    /// how long the UI thread goes without answering a message, and prints the state the UI thread last reported.
    /// With the variable unset every entry point is a single bool test.
    /// </summary>
    static class Trace
    {
        public static bool On;
        static readonly object Gate = new object();
        static StreamWriter? file;
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static long seq;

        /// <summary>Message counters collected between two <see cref="Pump"/> lines.</summary>
        static readonly Dictionary<int, int> Counts = new Dictionary<int, int>();

        public static void Open(string path)
        {
            lock (Gate)
            {
                if (On) return;
                try
                {
                    file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                    On = true;
                    Line("open pid=" + Environment.ProcessId + " uiThread=" + Environment.CurrentManagedThreadId);
                }
                catch { On = false; }
            }
        }

        /// <summary>Counts a message instead of logging it: the noisy ones go through here.</summary>
        public static void Count(int msg)
        {
            if (!On) return;
            lock (Gate) Counts[msg] = Counts.TryGetValue(msg, out int c) ? c + 1 : 1;
        }

        /// <summary>One line: elapsed ms, sequence number, then the caller's text.</summary>
        public static void Line(string what)
        {
            if (!On) return;
            long n = Interlocked.Increment(ref seq);
            string line = Clock.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms #" + n + "  " + what;
            lock (Gate) { try { file?.WriteLine(line); } catch { } }
        }

        /// <summary>Writes the counted messages as one compact line and resets the counters.</summary>
        public static void Pump(string why)
        {
            if (!On) return;
            lock (Gate)
            {
                if (Counts.Count == 0) { Line("pump " + why); return; }
                var sb = new StringBuilder("pump ").Append(why).Append(" msgs:");
                foreach (var kv in Counts) sb.Append(' ').Append(Name(kv.Key)).Append('=').Append(kv.Value);
                Counts.Clear();
                Line(sb.ToString());
            }
        }

        /// <summary>
        /// Watches the UI thread from the pool: every 500 ms it gives the window 200 ms to answer WM_NULL. A miss
        /// is a "silent" line; the first miss starts a stall that is reported with its length and the UI state text
        /// the form keeps up to date (<paramref name="state"/> is read from the pool, so it must be a snapshot).
        /// </summary>
        public static void StartWatchdog(IntPtr hwnd, Func<string> state)
        {
            if (!On) return;
            var timer = new Timer(_ =>
            {
                IntPtr r;
                bool reply = SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out r) != IntPtr.Zero;
                lock (Gate)
                {
                    if (reply) { if (stallStart != 0) { Line("WATCHDOG ui thread answered again after " + (Clock.ElapsedMilliseconds - stallStart) + "ms"); stallStart = 0; } return; }
                    if (stallStart == 0) { stallStart = Clock.ElapsedMilliseconds; Line("WATCHDOG no reply from the UI thread"); }
                    else if ((Clock.ElapsedMilliseconds - stallStart) % 2000 < 500) Line("WATCHDOG stalled " + (Clock.ElapsedMilliseconds - stallStart) + "ms hungFlag=" + IsHungAppWindow(hwnd));
                    try { Line("WATCHDOG state: " + state()); } catch (Exception ex) { Line("WATCHDOG state failed: " + ex.Message); }
                }
            }, null, 2000, 500);
            GC.KeepAlive(timer);
        }

        static long stallStart;

        const uint SMTO_ABORTIFHUNG = 0x2;
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr res);
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);

        public static string Msg(int m) => Name(m);

        static string Name(int m) => m switch
        {
            0x0001 => "CREATE", 0x0002 => "DESTROY", 0x0003 => "MOVE", 0x0005 => "SIZE", 0x0006 => "ACTIVATE",
            0x000C => "SETREDRAW", 0x000D => "SETTEXT", 0x000E => "GETTEXT", 0x000F => "PAINT", 0x0010 => "CLOSE", 0x0012 => "QUIT",
            0x0013 => "QUERYOPEN", 0x0014 => "ERASEBKGND", 0x0018 => "SHOWWINDOW", 0x0020 => "SETCURSOR",
            0x0021 => "MOUSEACTIVATE", 0x0024 => "GETMINMAXINFO", 0x0046 => "WINDOWPOSCHANGING", 0x0047 => "WINDOWPOSCHANGED",
            0x0051 => "INPUTLANGCHANGE", 0x007C => "STYLECHANGING", 0x007D => "STYLECHANGED", 0x007E => "DISPLAYCHANGE",
            0x0083 => "NCCALCSIZE", 0x0084 => "NCHITTEST",
            0x0085 => "NCPAINT", 0x0086 => "NCACTIVATE", 0x00A0 => "NCMOUSEMOVE", 0x00A1 => "NCLBUTTONDOWN",
            0x00A2 => "NCLBUTTONUP", 0x00A3 => "NCLBUTTONDBLCLK", 0x00A5 => "NCRBUTTONUP", 0x0100 => "KEYDOWN",
            0x0112 => "SYSCOMMAND", 0x0113 => "TIMER", 0x0200 => "MOUSEMOVE", 0x0201 => "LBUTTONDOWN",
            0x0202 => "LBUTTONUP", 0x0203 => "LBUTTONDBLCLK", 0x0210 => "PARENTNOTIFY", 0x0214 => "SIZING",
            0x0215 => "CAPTURECHANGED", 0x0216 => "MOVING", 0x0231 => "ENTERSIZEMOVE", 0x0232 => "EXITSIZEMOVE",
            0x0230 => "SYSCOLORCHANGE", 0x02E0 => "DPICHANGED", 0x02B1 => "DWMCOMPOSITIONCHANGED",
            _ => "0x" + m.ToString("X4", CultureInfo.InvariantCulture),
        };
    }
}
