using System;
using System.Text.Json;
using Sr2d64CSport;

// Only the interop wrapper is compiled: probe it in a separate process from every fixture, so P/Invoke's
// resolved entry points / handles cannot mask a missing-DLL scenario after a preceding successful load.
static class Program
{
    static int Main(string[] args)
    {
        bool expected = args.Length == 0 || args[0] != "missing";
        bool available = SR2D.IsAvailable;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            available,
            path = SR2D.DllPath,
            error = SR2D.NativeLoadError,
            simd = available ? (int)SR2D.ActiveSimdLevel : -1
        }));
        return available == expected && (available || !string.IsNullOrWhiteSpace(SR2D.NativeLoadError)) ? 0 : 1;
    }
}

namespace Sr2d64CSport
{
    // ABI-size-correct placeholders for the interop signatures (the probe only calls SIMD_LEVEL).
    internal static class Effects
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 80)]
        internal struct Stage { }
    }
    // The dot-line P/Invoke's blittable parameter type; no WinForms / image-codec dependency is needed.
    public sealed class Sprite
    {
        internal struct Point { public int x; public int y; public Point(int x, int y) { this.x = x; this.y = y; } }
    }
}
