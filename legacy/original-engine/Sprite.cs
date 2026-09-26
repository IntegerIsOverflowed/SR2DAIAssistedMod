using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    [SuppressMessage("Style", "IDE1006:Naming rule violation", Justification = "Reason for suppressing the message.")]
    internal class Sprite : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }
        private struct Point
        {
            public int x;
            public int y;
        }

        #region DLL_IMPORTS
        [DllImport("gdi32")]
        private static extern int SetDIBitsToDevice(HandleRef hDC, int x, int y, int Dx, int Dy, int SrcX, int SrcY, int Scan, int NumScans, Int64 pBits, ref BITMAPINFO BitsInfo, int wUsage);

        [DllImport("SR2D64", EntryPoint = "MOVSD_")]
        private static extern void MovsD(Int64 pSrc, Int64 pDest, int Length);

        [DllImport("SR2D64", EntryPoint = "CLEAR_C")]
        private static extern void SClearC(Int64 pDest, int W, int H, int WD, int c);

        [DllImport("SR2D64", EntryPoint = "V_MUL_ADD")]
        private static extern void SMulAdd(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD, int VMul, int VAdd);

        [DllImport("SR2D64", EntryPoint = "MASK_V_MUL_ADD")]
        private static extern void SMaskMulAdd(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask, int VMul, int VAdd);

        [DllImport("SR2D64", EntryPoint = "MASK_CLEAR_C")]
        private static extern void SMaskClearC(Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WD, int WM, int c, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_INTERSECT")]
        private static extern int SMaskIS(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD, int MaskEx);

        [DllImport("SR2D64", EntryPoint = "RESIZE")]
        private static extern void SResize(Int64 pSrc, Int64 pDest, int WS, int HS, int WD, int HD);

        [DllImport("SR2D64", EntryPoint = "FLIP_X")]
        private static extern void SFlipX(Int64 pSrc, Int64 pDest, int W, int H);

        [DllImport("SR2D64", EntryPoint = "FLIP_Y")]
        private static extern void SFlipY(Int64 pSrc, Int64 pDest, int W, int H);

        [DllImport("SR2D64", EntryPoint = "FLIP_XY")]
        private static extern void SFlipXY(Int64 pSrc, Int64 pDest, int W, int H);

        [DllImport("SR2D64", EntryPoint = "ROT_CW")]
        private static extern void SRotCW(Int64 pSrc, Int64 pDest, int WS, int HS);

        [DllImport("SR2D64", EntryPoint = "ROT_CCW")]
        private static extern void SRotCCW(Int64 pSrc, Int64 pDest, int WS, int HS);

        [DllImport("SR2D64", EntryPoint = "FLIP_X_ROT_CCW")]
        private static extern void SFlipXRotCCW(Int64 pSrc, Int64 pDest, int WS, int HS);

        [DllImport("SR2D64", EntryPoint = "FLIP_Y_ROT_CCW")]
        private static extern void SFlipYRotCCW(Int64 pSrc, Int64 pDest, int WS, int HS);

        [DllImport("SR2D64", EntryPoint = "DRAW_ROT")]
        private static extern void SDrawRot(Int64 pSrc, Int64 pDest, int L, int T, int W, int H, int Sx, int Sy, int Dx, int Dy, int sw, int sh, int dw, int SinA, int CosA);

        [DllImport("SR2D64", EntryPoint = "DRAW_ROT_AA")]
        private static extern void SDrawRotAA(Int64 pSrc, Int64 pDest, int L, int T, int W, int H, int Sx, int Sy, int Dx, int Dy, int sw, int sh, int dw, int SinA, int CosA);

        [DllImport("SR2D64", EntryPoint = "ADD_COLOR_KEY")]
        private static extern void SAddColorKey(Int64 pSrc, int W, int H, int cKey);

        [DllImport("SR2D64", EntryPoint = "BPP_32TO24")]
        private static extern void Bpp32to24(ref int Src, ref byte Dest, int Width, int Height, int Stride);

        [DllImport("SR2D64", EntryPoint = "DRAW_DOTLINE")]
        private static extern void SDrawDotLine(ref int Dest, int W, ref Point p1, ref Point p2, int DotStep, int c, int IsXor);

        [DllImport("SR2D64", EntryPoint = "CLR_ALPHA")]
        private static extern void SClearA(ref int Dest, int Size);

        [DllImport("SR2D64", EntryPoint = "PAINT")]
        private static extern void SPaint(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "MOD_2X")]
        private static extern void SMul2X(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "ADD_2D")]
        private static extern void SAdd2D(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "ADD_")]
        private static extern void SAdd(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "MOD_")]
        private static extern void SMul(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "ALPHA_T")]
        private static extern void SAlphaTest(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "ALPHA_B")]
        private static extern void SAlphaBlend(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "MOVE_BYTE")]
        private static extern void SMoveByte(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD, int MoveSrc, int MoveDest);

        [DllImport("SR2D64", EntryPoint = "MOVE_BIT")]
        private static extern void SMoveBit(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD, int MoveSrc, int MoveDest);

        [DllImport("SR2D64", EntryPoint = "MAX_")]
        private static extern void SMax(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "MIN_")]
        private static extern void SMin(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "BLEND")]
        private static extern void SBlend(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD, int k);

        [DllImport("SR2D64", EntryPoint = "MASK_PAINT")]
        private static extern void SMaskPaint(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_MOD_2X")]
        private static extern void SMaskMul2X(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_ADD_2D")]
        private static extern void SMaskAdd2D(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_ADD")]
        private static extern void SMaskAdd(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_MOD")]
        private static extern void SMaskMul(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_ALPHA_T")]
        private static extern void SMaskAlphaTest(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_ALPHA_B")]
        private static extern void SMaskAlphaBlend(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_MOVE_BYTE")]
        private static extern void SMaskMoveByte(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask, int MoveSrc, int MoveDest);

        [DllImport("SR2D64", EntryPoint = "MASK_MOVE_BIT")]
        private static extern void SMaskMoveBit(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask, int MoveSrc, int MoveDest);

        [DllImport("SR2D64", EntryPoint = "MASK_MAX")]
        private static extern void SMaskMax(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_MIN")]
        private static extern void SMaskMin(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_BLEND")]
        private static extern void SMaskBlend(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int MaskEx, int WS, int WD, int WM, int NotMask, int k);

        [DllImport("SR2D64", EntryPoint = "EBM_")]
        private static extern void SDrawEBM(Int64 pBMap, Int64 pDest, Int64 pLMap, int W, int H, int WB, int WD, int WL, int HL);

        [DllImport("SR2D64", EntryPoint = "EBM_EX")]
        private static extern void SDrawEBMEx(Int64 pBMap, Int64 pDest, Int64 pLMap, int W, int H, int WB, int WD, int WL, int HL, int xx, int yy, int HD);

        [DllImport("SR2D64", EntryPoint = "DPBM_")]
        private static extern void SDrawDPBM(Int64 pBMap, Int64 pDest, int W, int H, int c, int WS, int WD);

        [DllImport("SR2D64", EntryPoint = "DPBM_POINT")]
        private static extern void SDrawDPBMPoint(Int64 pSrc, Int64 pDest, int W, int H, int WS, int WD, int Lx, int Ly, int Lz, int Br);

        [DllImport("SR2D64", EntryPoint = "MASK_DPBM_POINT")]
        private static extern void SMaskDrawDPBMPoint(Int64 pSrc, Int64 pDest, Int64 pMask, int MaskEx, int W, int H, int WS, int WD, int WM, int Lx, int Ly, int Lz, int Br, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_DPBM")]
        private static extern void SMaskDrawDPBM(Int64 pSrc, Int64 pDest, Int64 pMask, int W, int H, int c, int MaskEx, int WS, int WD, int WM, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_EBM")]
        private static extern void SMaskDrawEBM(Int64 pSrc, Int64 pDest, Int64 pMask, Int64 pLMap, int MaskEx, int W, int H, int WS, int WD, int WM, int WL, int HL, int NotMask);

        [DllImport("SR2D64", EntryPoint = "MASK_EBM_EX")]
        private static extern void SMaskDrawEBMEx(Int64 pSrc, Int64 pDest, Int64 pMask, Int64 pLMap, int MaskEx, int W, int H, int WS, int WD, int WM, int WL, int HL, int NotMask, int xx, int yy, int HD);
        #endregion

        private int meWidth, meHeight;
        private SR2D.Op meOp;
        private int meTop, meLeft, meRight, meBottom;
        private int[] cBuf = Array.Empty<int>();
        private BITMAPINFO bi32BitInfo;
        private GCHandle GCH;
        private Int64 PTR;

        private bool TransformCoordAbs(int L2, int R2, int T2, int B2, int W2, ref int W, ref int H, ref Int64 P1, ref Int64 P2)
        {
            int L = ((0 >= L2) ? 0 : L2);
            int R = ((meWidth <= R2) ? meWidth : R2);
            if (L >= R)
                return false;
            int T = ((0 >= T2) ? 0 : T2);
            int B = ((meHeight <= B2) ? meHeight : B2);
            if (T >= B)
                return false;

            W = R - L;
            H = B - T;

            P1 = (T * meWidth + L) << 2;
            P2 = ((T - T2) * W2 + L - L2) << 2;
            return true;
        }

        private bool TransformCoord2(int L2, int R2, int T2, int B2, int W2, ref int W, ref int H, ref Int64 P1, ref Int64 P2)
        {
            int L = ((meLeft >= L2) ? meLeft : L2);
            int R = ((meRight <= R2) ? meRight : R2);
            if (L >= R)
                return false;
            int T = ((meTop >= T2) ? meTop : T2);
            int B = ((meBottom <= B2) ? meBottom : B2);
            if (T >= B)
                return false;

            W = R - L;
            H = B - T;

            P1 = ((T - meTop) * meWidth + L - meLeft) << 2;
            P2 = ((T - T2) * W2 + L - L2) << 2;
            return true;
        }

        private bool TransformCoord3(int L2, int R2, int T2, int B2, int L3, int R3, int T3, int B3, int W2, int W3, ref int W, ref int H, ref Int64 P1, ref Int64 P2, ref Int64 P3)
        {
            int L = ((meLeft >= L2) ? meLeft : L2);
            if (L < L3)
                L = L3;
            int R = ((meRight <= R2) ? meRight : R2);
            if (R > R3)
                R = R3;
            if (L >= R)
                return false;
            int T = ((meTop >= T2) ? meTop : T2);
            if (T < T3)
                T = T3;
            int B = ((meBottom <= B2) ? meBottom : B2);
            if (B > B3)
                B = B3;
            if (T >= B)
                return false;

            W = R - L;
            H = B - T;

            P1 = ((T - meTop) * meWidth + L - meLeft) << 2;
            P2 = ((T - T2) * W2 + L - L2) << 2;
            P3 = ((T - T3) * W3 + L - L3) << 2;
            return true;
        }

        public void ClearBuffer(int c)
        {
            if (meRight <= meLeft || meBottom <= meTop)
                return;
            SClearC(DataPTR(meLeft, meTop), meRight-meLeft, meBottom-meTop, meWidth, c);
        }

        public void MaskClearBuffer(Sprite SrcMask, int MaskX, int MaskY, int c, int Mask, bool NotMask = false)
        {
            Int64 Mp = 0, Dp = 0;
            int W = 0, H = 0;

            if (!TransformCoord2(MaskX, MaskX +SrcMask.Width, MaskY, MaskY+SrcMask.Height, SrcMask.Width, ref W, ref H, ref Dp, ref Mp))
                return;

            Dp += DataPTR(meLeft, meTop);
            Mp += SrcMask.PTR;

            SMaskClearC(Dp, Mp, W, H, Mask, meWidth, SrcMask.Width, c, Convert.ToInt32(NotMask));
        }

        public void ClearRect(int PLeft, int PRight, int Top, int Bottom, int c)
        {
            if (PRight <= PLeft || Bottom <= Top) { return; }
            if (PLeft < meLeft) { PLeft = meLeft; }
            if (PRight > meRight) { PRight = meRight; }
            if (Top < meTop) { Top = meTop; }
            if (Bottom > meBottom) { Bottom = meBottom; }

            SClearC(DataPTR(PLeft, Top), PRight - PLeft, Bottom - Top, meWidth, c);
        }

        public void DrawLine(int lx1, int ly1, int lx2, int ly2, int c, int DotStep = 0, bool IsXor = false)
        {
            Point p1, p2;
            int L, R, T, B;
            float k;

            p1.x = lx1;
            p1.y = ly1;
            p2.x = lx2;
            p2.y = ly2;
            L = meLeft;
            R = meRight - 1;
            T = meTop;
            B = meBottom - 1;

            if (p1.y > p2.y) { Swap(ref p1, ref p2); }
            if (p1.y > B || p2.y < T) { return; }
            if (p1.y != p2.y)
            {
                k = (float)(p2.x - p1.x) / (p2.y - p1.y);
                if (p1.y < T)
                {
                    p1.x = (int)(p2.x + (T - p2.y) * k);
                    p1.y = T;
                }
                if (p2.y > B)
                {
                    p2.x = (int)(p1.x + (B - p1.y) * k);
                    p2.y = B;
                }
            }

            if (p1.x > p2.x) { Swap(ref p1, ref p2); }
            if (p1.x > R || p2.x < L) { return; }
            if (p1.x != p2.x)
            {
                k = (float)(p2.y - p1.y) / (p2.x - p1.x);
                if (p1.x < L)
                {
                    p1.y = (int)(p2.y + (L - p2.x) * k);
                    p1.x = L;
                }
                if (p2.x > R)
                {
                    p2.y = (int)(p1.y + (R - p1.x) * k);
                    p2.x = R;
                }
            }

            SDrawDotLine(ref cBuf[0], meWidth, ref p1, ref p2, Math.Abs(DotStep) + 1, c, Convert.ToInt32(IsXor));
        }
        private static void Swap(ref Point p1, ref Point p2)
        {
            Point p;

            p = p1;
            p1 = p2;
            p2 = p;
        }

        public void DrawRotate(Sprite Src, int Sx, int Sy, int Dx, int Dy, float Angle, bool AA = false)
        {
            int sw, sh;
            int ddx, ddy;
            int SinA, CosA;
            int T, L, R, B;
            int x, y;

            if (meRight <= meLeft || meBottom <= meTop) { return; }
            sw = Src.Width;
            sh = Src.Height;
            ddx = (Dx << 16) + 0x8000;
            ddy = (Dy << 16) + 0x8000;
            SinA = (int)(Math.Sin(Angle) * 0x10000);
            CosA = (int)(Math.Cos(Angle) * 0x10000);
            L = ddx - Sx * CosA - Sy * SinA;
            R = L;
            x = ddx - (Sx - sw) * CosA - Sy * SinA;
            if (L > x) { L = x; }
            if (R < x) { R = x; }
            x = ddx - Sx * CosA - (Sy - sh) * SinA;
            if (L > x) { L = x; }
            if (R < x) { R = x; }
            x = ddx - (Sx - sw) * CosA - (Sy - sh) *  SinA;
            if (L > x) { L = x; }
            if (R < x) { R = x; }

            T = ddy - Sy * CosA + Sx *  SinA;
            B = T;
            y = ddy - (Sy - sh) * CosA + Sx * SinA;
            if (T > y) { T = y; }
            if (B < y) { B = y; }
            y = ddy - Sy * CosA + (Sx - sw) * SinA;
            if (T > y) { T = y; }
            if (B < y) { B = y; }
            y = ddy - (Sy - sh) * CosA + (Sx - sw) * SinA;
            if (T > y) { T = y; }
            if (B < y) { B = y; }

            L >>= 16;
            if (L < meLeft) { L = meLeft; }
            T >>= 16;
            if (T < meTop) { T= meTop; }
            R >>= 16;
            if (R >= meRight) { R = meRight; } else { R++; }
            B >>= 16;
            if (B >= meBottom) { B = meBottom; } else { B++; };
            if (AA)
            {
                SDrawRotAA(Src.PTR, PTR, L, T, R, B, Sx, Sy, Dx, Dy, sw, sh, meWidth, SinA, CosA);
            }
            else
            {
                SDrawRot(Src.PTR, PTR, L, T, R, B, Sx, Sy, Dx, Dy, sw, sh, meWidth, SinA, CosA);
            }
        }

        public void DrawDPBM(Sprite Src, int Sx, int Sy, int Lx, int Ly, int Lz, float Brite = 1, bool PointLite = false)
        {
            Int64 Sp = 0, Dp = 0;
            int W = 0, H = 0;

            if (!TransformCoord2(Sx, Sx + Src.Width, Sy, Sy + Src.Height, Src.Width, ref W, ref H, ref Dp, ref Sp)) { return; }

            Dp += DataPTR(meLeft, meTop);
            Sp += Src.PTR;

            if (PointLite)
            {
                if (meLeft < Sx) { Lx -= Sx; } else { Lx -= meLeft; }
                if (meTop < Sy) { Ly -= Sy; } else { Ly -= meTop; }
                if (Brite < -2) { Brite = -2; } else if (Brite > 2) { Brite = 2; }
                SDrawDPBMPoint(Sp, Dp, W, H, Src.Width, meWidth, Lx, Ly, Lz, (int)(Brite * 0x100000));
            }
            else
            {
                SDrawDPBM(Sp, Dp, W, H, ColFromLite(Lx, Ly, Lz, Brite), Src.Width, meWidth);
            }
        }

        public void MaskDrawDPBM(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, int Mask, int Lx, int Ly, int Lz, float Brite = 1, bool NotMask = false, bool PointLite = false)
        {
            Int64 Sp = 0, Dp = 0, Mp = 0;
            int W = 0, H = 0;

            if (!TransformCoord3(Sx, Sx + Src.Width, Sy, Sy + Src.Height, MaskX, MaskX + SrcMask.Width, MaskY, MaskY + SrcMask.Height, Src.Width, SrcMask.Width, ref W, ref H, ref Dp, ref Sp, ref Mp)) { return; }

            Dp += PTR;
            Sp += Src.PTR;
            Mp += SrcMask.PTR;

            if (PointLite)
            {
                if (meLeft < Sx)
                    if (MaskX < Sx) { Lx -= Sx; } else { Lx -= MaskX; }
                else
                {
                    if (MaskX < meLeft) { Lx -= meLeft; } else { Lx -= MaskX; }
                }

                if (meTop < Sy)
                {
                    if (MaskY < Sy) { Ly -= Sy; } else { Ly -= MaskY; }
                }
                else
                {
                    if (MaskY < meTop) { Ly -= meTop; } else { Ly -= MaskY; }
                }

                if (Brite < -2) { Brite = -2; } else if (Brite > 2) { Brite = 2; }

                SMaskDrawDPBMPoint(Sp, Dp, Mp, Mask, W, H, Src.Width, meWidth, SrcMask.Width, Lx, Ly, Lz, (int)(Brite * 0x100000), Convert.ToInt32(NotMask));
            }
            else
            {
                SMaskDrawDPBM(Sp, Dp, Mp, W, H, ColFromLite(Lx, Ly, Lz, Brite), Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask));
            }
        }

        public void Draw(Sprite Src, int Sx, int Sy, SR2D.Op Op = SR2D.Op.DefaultOp)
        {
            Int64 Sp = 0, Dp = 0;
            int W = 0, H = 0;

            if (!TransformCoord2(Sx, Sx + Src.Width, Sy, Sy + Src.Height, Src.Width, ref W, ref H, ref Dp, ref Sp)) { return; }

            Dp += DataPTR(meLeft, meTop);
            Sp += Src.PTR;

            if (Op == SR2D.Op.DefaultOp) { Op = Src.Op; }

            switch (Op)
            {
                case SR2D.Op.Paint: { SPaint(Sp, Dp, W, H, Src.Width, meWidth); break; }
                case SR2D.Op.AlphaTest: { SAlphaTest(Sp, Dp, W, H, Src.Width, meWidth); break; }
                case SR2D.Op.AlphaBlend: { SAlphaBlend(Sp, Dp, W, H, Src.Width, meWidth); break; }
                case SR2D.Op.Add2D: { SAdd2D(Sp, Dp, W, H, Src.Width, meWidth); break; }
                case SR2D.Op.Add: { SAdd(Sp, Dp, W, H, Src.Width, meWidth); break; }
                case SR2D.Op.Mul2X: { SMul2X(Sp, Dp, W, H, Src.Width, meWidth); break; }
                case SR2D.Op.Mul: { SMul(Sp, Dp, W, H, Src.Width, meWidth); break; }
                case SR2D.Op.Max: { SMax(Sp, Dp, W, H, Src.Width, meWidth); break; }
                case SR2D.Op.Min: { SMin(Sp, Dp, W, H, Src.Width, meWidth); break; }
            }
        }

        public void MaskDraw(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, int Mask, bool NotMask = false, SR2D.Op Op = SR2D.Op.DefaultOp)
        {
            Int64 Sp = 0, Dp = 0, Mp = 0;
            int W = 0, H = 0;

            if (!TransformCoord3(Sx, Sx + Src.Width, Sy, Sy + Src.Height, MaskX, MaskX + SrcMask.Width, MaskY, MaskY + SrcMask.Height, Src.Width, SrcMask.Width, ref W, ref H, ref Dp, ref Sp, ref Mp)) { return; }

            Dp += PTR;
            Sp += Src.PTR;
            Mp += SrcMask.PTR;

            if (Op == SR2D.Op.DefaultOp) { Op = Src.Op; }

            switch (Op)
            {
                case SR2D.Op.Paint: { SMaskPaint(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask)); break; }
                case SR2D.Op.Mul2X: { SMaskMul2X(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask)); break; }
                case SR2D.Op.Add2D: { SMaskAdd2D(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask)); break; }
                case SR2D.Op.Add: { SMaskAdd(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask)); break; }
                case SR2D.Op.Mul: { SMaskMul(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask)); break; }
                case SR2D.Op.AlphaTest: { SMaskAlphaTest(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask)); break; }
                case SR2D.Op.AlphaBlend: { SMaskAlphaBlend(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask)); break; }
                case SR2D.Op.Max: { SMaskMax(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask)); break; }
                case SR2D.Op.Min: { SMaskMin(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask)); break; }
            }
        }

        public void Blend(Sprite Src, int Sx, int Sy, int BlendFactor)
        {
            Int64 Sp = 0, Dp = 0;
            int W = 0, H = 0;

            if (!TransformCoord2(Sx, Sx + Src.Width, Sy, Sy + Src.Height, Src.Width, ref W, ref H, ref Dp, ref Sp)) { return; }

            Dp += DataPTR(meLeft, meTop);
            Sp += Src.PTR;

            SBlend(Sp, Dp, W, H, Src.Width, meWidth, BlendFactor);
        }

        public void MaskBlend(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, int BlendFactor, int Mask, bool NotMask = false)
        {
            Int64 Sp = 0, Dp = 0, Mp = 0;
            int W = 0, H = 0;

            if (!TransformCoord3(Sx, Sx + Src.Width, Sy, Sy + Src.Height, MaskX, MaskX + SrcMask.Width, MaskY, MaskY + SrcMask.Height, Src.Width, SrcMask.Width, ref W, ref H, ref Dp, ref Sp, ref Mp)) { return; }

            Dp += PTR;
            Sp += Src.PTR;
            Mp += SrcMask.PTR;

            SMaskBlend(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask), BlendFactor);
        }

        public int MaskInterSector(Sprite Src, int Sx, int Sy, int Mask)
        {
            Int64 Sp = 0, Dp = 0;
            int W = 0, H = 0;

            if (!TransformCoordAbs(Sx, Sx + Src.Width, Sy, Sy + Src.Height, Src.Width, ref W, ref H, ref Dp, ref Sp)) { return 0; }

            Dp += PTR;
            Sp += Src.PTR;

            return SMaskIS(Sp, Dp, W, H, Src.Width, meWidth, Mask);
        }

        public void DrawEBM(Sprite SRCBump, Sprite SRCCol, int Sx, int Sy, bool DestSpace = false)
        {
            Int64 Sp = 0, Dp = 0, Cp = 0;
            int W = 0, H = 0;

            if (!TransformCoord2(Sx, Sx+ SRCBump.Width, Sy, Sy + SRCBump.Height, SRCBump.Width, ref W, ref H, ref Dp, ref Sp)) { return; }

            Dp += PTR;
            Sp += SRCBump.PTR;
            Cp = SRCCol.PTR;

            if (DestSpace)
            {
                if (Sx < 0) { Sx = 0; }
                if (Sy < 0) { Sy = 0; }

                SDrawEBMEx(Sp, Dp, Cp, W, H, SRCBump.Width, meWidth, SRCCol.Width, SRCCol.Height, Sx, Sy, meHeight);
            }
            else
            {
                SDrawEBM(Sp, Dp, Cp, W, H, SRCBump.Width, meWidth, SRCCol.Width, SRCCol.Height);
            }
        }

        public void MulAddS2X(Sprite Src, int Sx, int Sy, int Mul, int Add)
        {
            Int64 Sp = 0, Dp = 0;
            int W = 0, H = 0;

            if (!TransformCoord2(Sx, Sx + Src.Width, Sy, Sy + Src.Height, Src.Width, ref W, ref H, ref Dp, ref Sp)) { return; }

            Dp += DataPTR(meLeft, meTop);
            Sp += Src.PTR;

            SMulAdd(Sp, Dp, W, H, Src.Width, meWidth, Mul, Add);
        }

        public void MaskMulAddS2X(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, int Mul, int Add, int Mask, bool NotMask = false)
        {
            Int64 Sp = 0, Dp = 0, Mp = 0;
            int W = 0, H = 0;

            if (!TransformCoord3(Sx, Sx + Src.Width, Sy, Sy + Src.Height, MaskX, MaskX + SrcMask.Width, MaskY, MaskY + SrcMask.Height, Src.Width, SrcMask.Width, ref W, ref H, ref Dp, ref Sp, ref Mp)) { return; }

            Dp += PTR;
            Sp += Src.PTR;
            Mp += SrcMask.PTR;

            SMaskMulAdd(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, -Convert.ToInt32(NotMask), Mul, Add);
        }

        public void MoveByte(Sprite Src, int Sx, int Sy, SR2D.ColChannel chSrc, SR2D.ColChannel chDest)
        {
            Int64 Sp = 0, Dp = 0;
            int W = 0, H = 0;

            if (!TransformCoord2(Sx, Sx + Src.Width, Sy, Sy + Src.Height, Src.Width, ref W, ref H, ref Dp, ref Sp)) { return; }

            Dp += DataPTR(meLeft, meTop);
            Sp += Src.PTR;

            SMoveByte(Sp, Dp, W, H, Src.Width, meWidth, (int)chSrc, (int)chDest);
        }

        public void MoveBit(Sprite Src, int Sx, int Sy, int mbSrc, int mbDest)
        {
            Int64 Sp = 0, Dp = 0;
            int W = 0, H = 0;

            if (!TransformCoord2(Sx, Sx + Src.Width, Sy, Sy + Src.Height, Src.Width, ref W, ref H, ref Dp, ref Sp)) { return; }

            Dp += DataPTR(meLeft, meTop);
            Sp += Src.PTR;

            SMoveBit(Sp, Dp, W, H, Src.Width, meWidth, mbSrc, mbDest);
        }

        public void MaskMoveByte(Sprite Src, Sprite SrcMask, int Sx, int Sy, int MaskX, int MaskY, SR2D.ColChannel chSrc, SR2D.ColChannel chDest, int Mask, bool NotMask = false)
        {
            Int64 Sp = 0, Dp = 0, Mp = 0;
            int W = 0, H = 0;

            if (!TransformCoord3(Sx, Sx + Src.Width, Sy, Sy + Src.Height, MaskX, MaskX + SrcMask.Width, MaskY, MaskY + SrcMask.Height, Src.Width, SrcMask.Width, ref W, ref H, ref Dp, ref Sp, ref Mp)) { return; }

            Dp += PTR;
            Sp += Src.PTR;
            Mp += SrcMask.PTR;

            SMaskMoveByte(Sp, Dp, Mp, W, H, Mask, Src.Width, meWidth, SrcMask.Width, Convert.ToInt32(NotMask), (int)chSrc, (int)chDest);
        }

        public void TileDraw(Sprite Src, int Dx, int Dy, int W, int H, int Sx = 0, int Sy = 0, SR2D.Op Op = SR2D.Op.DefaultOp)
        {
            int meT, meL, meR, meB;
            int sw, x, y, sh;

            meL = meLeft;
            meR = meRight;
            meT = meTop;
            meB = meBottom;

            do
            {
                if (Dx > meLeft) if (Dx < meRight) meLeft = Dx; else break;
                if (Dy > meTop) if (Dy < meBottom) meTop = Dy; else break;
                if (Dx + W <= meLeft) break; else if (Dx + W < meRight) meRight = Dx + W;
                if (Dy + H <= meTop) break; else if (Dy + H < meBottom) meBottom = Dy + H;
                sh = Src.Height;
                sw = Src.Width;
                Sx -= (int)Math.Floor((double)(Sx + sw) / sw) * sw;
                Sy -= (int)Math.Floor((double)(Sy + sh) / sh) * sh;

                for (y = Dy + Sy; y < meBottom; y += sh)
                    for (x = Dx + Sx; x < meRight; x += sw)
                        Draw(Src, x, y, Op);
            } while (false);

            meLeft = meL;
            meRight = meR;
            meTop = meT;
            meBottom = meB;
        }

        private void Init(int W = 0, int H = 0, SR2D.Op Op = SR2D.Op.DefaultOp)
        {
            if (Op != SR2D.Op.DefaultOp) { meOp = Op; }
            if (W <= 0 & H <= 0) { return; }
            if (W == meWidth & H == meHeight) { return; }
            if (W > 0) { meWidth = W; }
            if (H > 0) { meHeight = H; }
            SetLockRect(0, meWidth, 0, meHeight);
            if (GCH.IsAllocated) { GCH.Free(); }
            cBuf = new int[meWidth * meHeight];
            GCH = GCHandle.Alloc(cBuf, GCHandleType.Pinned);
            PTR = GCH.AddrOfPinnedObject().ToInt64();
            bi32BitInfo.bmiHeader.biBitCount = 32;
            bi32BitInfo.bmiHeader.biPlanes = 1;
            bi32BitInfo.bmiHeader.biSize = 40;
            bi32BitInfo.bmiHeader.biWidth = meWidth;
            bi32BitInfo.bmiHeader.biHeight = -meHeight;
            bi32BitInfo.bmiHeader.biSizeImage = (meWidth * meHeight) << 2;
        }

        private void LoadFromBitmap(Bitmap Bmp, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            Bitmap BmpDest;
            bool f = false;

            if (W < 1) { W = Bmp.Width; }
            if (H < 1) { H = Bmp.Height; }
            if (Bmp.PixelFormat == PixelFormat.Format32bppArgb)
            {
                BmpDest = Bmp;
            }
            else
            {
                BmpDest = new Bitmap(Bmp.Width, Bmp.Height, PixelFormat.Format32bppArgb);
                using Graphics G = Graphics.FromImage(BmpDest);
                G.DrawImage(Bmp, 0, 0, Bmp.Width, Bmp.Height);
                f = true;
            }

            if (Trans == SR2D.Transform.None && W == Bmp.Width && H == Bmp.Height)
            {
                Init(W, H);
                System.Drawing.GraphicsUnit temppageUnit1 = GraphicsUnit.Pixel;
                BitmapData BMD = BmpDest.LockBits(Rectangle.Round(BmpDest.GetBounds(ref temppageUnit1)), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(BMD.Scan0, cBuf, 0, W * H);
                BmpDest.UnlockBits(BMD);
            }
            else
            {
                Sprite? Spr = new Sprite(BmpDest);
                LoadFromSprite(Spr, Trans, W, H, ColorKey);
                Spr = null;
            }
            if (f) { BmpDest.Dispose(); }
            if (ColorKey >= 0)
            {
                AddColorKey(ColorKey);
                Op = SR2D.Op.AlphaTest;
            }
        }

        private void LoadFromSprite(Sprite Src, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            bool fResize = false;
            int[] Ar;
            int D;

            if (W <= 0) { W = Src.Width; } else if (W != Src.Width) { fResize = true; }
            if (H <= 0) { H = Src.Height; } else if (H != Src.Height) { fResize = true; }
            if (fResize)
            {
                Init(W, H, Src.Op);
                if (Trans == SR2D.Transform.None)
                {
                    SResize(Src.PTR, PTR, Src.Width, Src.Height, meWidth, meHeight);
                }
                else if (W * H > Src.Width * Src.Height)
                {
                    Ar = new int[Src.Width * Src.Height];
                    GCHandle ArH = GCHandle.Alloc(Ar, GCHandleType.Pinned);
                    try
                    {
                        Int64 ArPtr = ArH.AddrOfPinnedObject().ToInt64();
                        W = Src.Width;
                        H = Src.Height;
                        Transform(Src.PTR, ArPtr, W, H, Trans);
                        if (Trans > SR2D.Transform.FlipXY)
                        {
                            D = W;
                            W = H;
                            H = D;
                        }
                        SResize(ArPtr, PTR, W, H, meWidth, meHeight);
                    }
                    finally
                    {
                        ArH.Free();
                    }
                }
                else
                {
                    Ar = new int[W * H];
                    GCHandle ArH = GCHandle.Alloc(Ar, GCHandleType.Pinned);
                    try
                    {
                        Int64 ArPtr = ArH.AddrOfPinnedObject().ToInt64();
                        if (Trans > SR2D.Transform.FlipXY)
                        {
                            D = W;
                            W = H;
                            H = D;
                        }
                        SResize(Src.PTR, ArPtr, Src.Width, Src.Height, W, H);
                        Transform(ArPtr, PTR, W, H, Trans);
                    }
                    finally
                    {
                        ArH.Free();
                    }
                }
            }
            else
            {
                if (Trans > SR2D.Transform.FlipXY)
                {
                    D = W;
                    W = H;
                    H = D;
                }
                Init(W, H, Src.Op);
                Transform(Src.PTR, PTR, W, H, Trans);
            }
            if (ColorKey >= 0)
            {
                AddColorKey(ColorKey);
                Op = SR2D.Op.AlphaTest;
            }
        }

        public void LoadFromFile(string FileName, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
        {
            using Bitmap Bmp = (Bitmap)Bitmap.FromFile(FileName);
            LoadFromBitmap(Bmp, Trans, W, H, ColorKey);
        }

        public void SaveToFile(string FileName, ImageFormat Format, bool WithAlpha = false)
        {
            Bitmap Bmp;
            if (WithAlpha)
            {
                Bmp = new Bitmap(meWidth, meHeight, meWidth << 2, PixelFormat.Format32bppArgb, (nint)PTR);
            }
            else
            {
                Bmp = new Bitmap(meWidth, meHeight, meWidth << 2, PixelFormat.Format32bppRgb, (nint)PTR);
            }
            Bmp.Save(FileName, Format);
        }

        private void Transform(Int64 pSrc, Int64 pDest, int W, int H, SR2D.Transform Trans)
        {
            switch (Trans)
            {
                case SR2D.Transform.FlipX: SFlipX(pSrc, pDest, W, H); break;
                case SR2D.Transform.FlipY: SFlipY(pSrc, pDest, W, H); break;
                case SR2D.Transform.FlipXY: SFlipXY(pSrc, pDest, W, H); break;
                case SR2D.Transform.RotCCW: SRotCCW(pSrc, pDest, W, H); break;
                case SR2D.Transform.RotCW: SRotCW(pSrc, pDest, W, H); break;
                case SR2D.Transform.FlipXRotCCW: SFlipXRotCCW(pSrc, pDest, W, H); break;
                case SR2D.Transform.FlipYRotCCW: SFlipYRotCCW(pSrc, pDest, W, H); break;
                default: MovsD(pSrc, pDest, (W * H)); break;
            }
        }

        public void SetLockRect(int LRLeft = -1, int LRRight = -1, int LRTop = -1, int LRBottom = -1)
        {
            if (LRLeft >= 0 & LRLeft < meWidth) { meLeft = LRLeft; } else { meLeft = 0; }
            if (LRRight > LRLeft & LRRight <= meWidth) { meRight = LRRight; } else { meRight = meWidth; }
            if (LRTop >= 0 & LRTop < meHeight) { meTop = LRTop; } else { meTop = 0; }
            if (LRBottom > LRTop & LRBottom <= meHeight) { meBottom = LRBottom; } else { meBottom = meHeight; }
        }

        public int Width { get { return meWidth; } }
        public int Height { get { return meHeight; } }

        public SR2D.Op Op { get { return meOp; } set { if (value != SR2D.Op.DefaultOp) { meOp = value; } } }

        public Int64 DataPTR(int x, int y)
        {
            return PTR + (x + y * meWidth << 2);
        }

        public void PaintToDevice(HandleRef hDC)
        {
            _=SetDIBitsToDevice(hDC, 0, 0, meWidth, meHeight, 0, 0, 0, meHeight, PTR, ref bi32BitInfo, 0);
        }

        public void AddColorKey(int ColorKey)
        {
            SAddColorKey(PTR, meWidth, meHeight, ColorKey);
        }

        public void SetPixel(int x, int y, int c)
        {
            if (x < meLeft || x>= meRight || y < meTop || y >= meBottom) { return; }
            cBuf[x + y * meWidth] = c;
        }

        public int GetPixel(int x, int y)
        {
            if (x < 0 || x >= meWidth || y < 0 || y >= meHeight) { return 0; }
            return cBuf[x + y * meWidth];
        }

        private static int ColFromLite(int x, int y, int z, float Brite)
        {
            int R, G, B;
            float k;

            if (Brite < 0) { Brite=0; }
            if (Brite > 1) { Brite=1; }
            k = 127.4999f * Brite / (float)Math.Sqrt(x * x + y * y + z * z);

            R = (int)(x * k); R += 128;
            G = (int)(y * k); G += 128;
            B = (int)(z * k); B += 128;

            return (R << 16) + (G << 8) + B;
        }

        public Bitmap ToBitmap
        {
            get
            {
                GraphicsUnit tempUnit = GraphicsUnit.Pixel;
                Bitmap BmpDest = new Bitmap(meWidth, meHeight, PixelFormat.Format32bppArgb);
                BitmapData BMD = BmpDest.LockBits(Rectangle.Round(BmpDest.GetBounds(ref tempUnit)), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(cBuf, 0, BMD.Scan0, meWidth * meHeight);
                BmpDest.UnlockBits(BMD);
                return BmpDest;
            }
        }

        #region New Constructor
        public Sprite(Bitmap Bmp, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
            : base() => LoadFromBitmap(Bmp, Trans, W, H, ColorKey);

        public Sprite(Sprite Src, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
            : base() => LoadFromSprite(Src, Trans, W, H, ColorKey);

        public Sprite(string FileName, SR2D.Transform Trans = SR2D.Transform.None, int W = 0, int H = 0, int ColorKey = -1)
            : base() => LoadFromFile(FileName, Trans, W, H, ColorKey);

        public Sprite(int W, int H, SR2D.Op Op = SR2D.Op.Paint)
            : base()
        {
            if (Op == SR2D.Op.DefaultOp) { Op = SR2D.Op.Paint; }
            Init(W, H, Op);
        }
        #endregion

        public void Dispose()
        {
            if (GCH.IsAllocated)
                GCH.Free();
            GC.SuppressFinalize(this);
        }

        ~Sprite()
        {
            if (GCH.IsAllocated)
                GCH.Free();
        }
    }
}
