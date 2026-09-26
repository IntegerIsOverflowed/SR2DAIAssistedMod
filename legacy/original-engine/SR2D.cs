using System;
using System.Runtime.InteropServices;


namespace Sr2d64CSport
{
    internal static class SR2D
    {
        public enum Op : int
        {
            DefaultOp,
            Paint,
            AlphaTest,
            AlphaBlend,
            Add2D,
            Add,
            Mul,
            Mul2X,
            Max,
            Min,
            Blend
        }
        public enum ColChannel : int
        {
            ChBlue = 0,
            ChGreen = 1,
            ChRed = 2,
            ChAlpha = 3
        }
        public enum Transform : int
        {
            None = 0,
            FlipX = 1,
            FlipY = 2,
            FlipXY = 3,
            RotCCW = 4,
            RotCW = 5,
            FlipXRotCCW = 6,
            FlipYRotCCW = 7,
            FlipXRotCW = 7,
            FlipYRotCW = 6
        }
        [DllImport("SR2D64", EntryPoint = "ARGB_")]
        public extern static int ARGB(byte A, byte R, byte G, byte B);
        [DllImport("SR2D64", EntryPoint = "EXP2N")]
        public extern static int BitMask(int i);
    }
}
