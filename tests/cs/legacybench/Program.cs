using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Sr2d64CSport;                                  // the CURRENT C# bindings (cs/*.cs) + current SR2D64.dll

namespace LegacyBench
{
    /// <summary>
    /// ORIGINAL engine (legacy/original-engine/*.cpp built here as SR2DOLD64.dll) vs the CURRENT
    /// engine (native/, prebuilt SR2D64.dll) on identical sprite-drawing call sequences.
    /// Pure engine throughput: no window, no present, no GDI. Every call goes through that engine's
    /// own C# binding layer (legacybind/*.cs = verbatim copies of the original bindings; cs/*.cs = the
    /// current ones), and only through entry points BOTH SR2D.def files export.
    /// Timings vary ~25% run to run on this box: every number is the median of RUNS timed passes after
    /// a discarded warm-up, and the reported quantity is the ratio current/old (>1 = the new engine is
    /// slower for that call).
    /// </summary>
    internal static class Program
    {
        private const int TW = 1280, TH = 800;       // target surface
        private const int SW = 256, SH = 256;        // source sprite
        private const int DW = 512, DH = 512;        // scale target (RESIZE output)
        private const int RUNS = 5;                  // timed passes per case (median reported)
        private const int BG = unchecked((int)0xFF101418);

        private static int Reps = 200;               // default repetitions per timed pass
        private static readonly float[] Angles = { 0.2f, 0.7f, 1.1f, 1.9f, 2.6f, 3.4f, 4.1f, 5.2f };

        // ------------------------------------------------------------------ raw fallback imports
        // Used ONLY if the private original P/Invoke cannot be bound by reflection. Same export, same args.
        [DllImport("SR2DOLD64.dll", EntryPoint = "RESIZE", ExactSpelling = true)]
        private static extern void RawResizeOld(long pSrc, long pDest, int ws, int hs, int wd, int hd);
        [DllImport("SR2D64", EntryPoint = "RESIZE", ExactSpelling = true)]
        private static extern unsafe void RawResizeNew(int* pSrc, int* pDest, int ws, int hs, int wd, int hd);

        private delegate void ResizeOldDel(long pSrc, long pDest, int ws, int hs, int wd, int hd);
        private static ResizeOldDel? oldResize;
        private static string resizeRoute = "";

        private static void Main(string[] args)
        {
            foreach (var a in args)
            {
                if (a.StartsWith("--reps=")) int.TryParse(a[7..], out Reps);
                else if (a == "--quick") Reps = 20;
            }
            if (Reps < 1) Reps = 1;

            Console.WriteLine("legacybench - ORIGINAL SR2D engine (SR2DOLD64.dll) vs CURRENT engine (SR2D64.dll)");
            Console.WriteLine($"surface {TW}x{TH}, source {SW}x{SH}, scale output {DW}x{DH} | reps {Reps} | warm-up discarded | median of {RUNS} timed passes");
            Console.WriteLine();

            // ---- current engine must be loadable (the resolver demands the ABI handshake export)
            if (!SR2D.IsAvailable)
            {
                Console.WriteLine("FATAL: the current SR2D64 native library could not be loaded next to this exe.");
                Environment.Exit(1);
                return;
            }
            Console.WriteLine($"current engine : {SR2D.DllPath ?? "(default probing)"}  SIMD level {SR2D.ActiveSimdLevel}");

            // ---- original engine: prove SR2DOLD64.dll loads and its kernels actually run
            try
            {
                using var probe = new OrigEngine.Sprite(16, 16, OrigEngine.SR2D.Op.Paint);
                using var probeDst = new OrigEngine.Sprite(16, 16, OrigEngine.SR2D.Op.Paint);
                probe.SetPixel(3, 3, 0x12345678);
                probeDst.Draw(probe, 0, 0, OrigEngine.SR2D.Op.Paint);
                if (probeDst.GetPixel(3, 3) != 0x12345678)
                    throw new Exception("PAINT through the original bindings did not land the pixel");
                Console.WriteLine("original engine: SR2DOLD64.dll loaded, PAINT smoke-test ok");
            }
            catch (Exception e)
            {
                Console.WriteLine($"FATAL: the original engine cannot run: {e.GetType().Name}: {e.Message}");
                Console.WriteLine("       comparison cannot be made.");
                Environment.Exit(1);
                return;
            }

            // ---- bind the original RESIZE P/Invoke (declared private in the original Sprite.cs)
            BindOriginalResize();
            Console.WriteLine($"scale row route: {resizeRoute}");
            Console.WriteLine();

            // ---- build identical scenes on both engines
            var ctx = new Ctx();
            ctx.Build();

            var rows = new System.Collections.Generic.List<Row>();
            void Add(string name, string note, int reps, Action? old, Action? newAct, Target t = Target.Dst)
                => rows.Add(Run(name, note, reps, old, newAct, t, ctx));

            // 1) plain Draw, Paint op -> PAINT export (both .def files)
            Add("Draw Paint 256x256", "PAINT, each engine's Sprite.Draw()", Reps,
                () => { ctx.OldDst!.Draw(ctx.OldSrc!, 300, 200, OrigEngine.SR2D.Op.Paint); },
                () => { ctx.NewDst!.Draw(ctx.NewSrc!, 300, 200, SR2D.Op.Paint); });

            // 2) Draw with AlphaBlend -> ALPHA_B
            Add("Draw AlphaBlend 256x256", "ALPHA_B", Reps,
                () => { ctx.OldDst!.Draw(ctx.OldSrc!, 300, 200, OrigEngine.SR2D.Op.AlphaBlend); },
                () => { ctx.NewDst!.Draw(ctx.NewSrc!, 300, 200, SR2D.Op.AlphaBlend); });

            // 3) Draw with AlphaTest -> ALPHA_T (source carries real zero-alpha pixels)
            Add("Draw AlphaTest 256x256", "ALPHA_T", Reps,
                () => { ctx.OldDst!.Draw(ctx.OldSrc!, 300, 200, OrigEngine.SR2D.Op.AlphaTest); },
                () => { ctx.NewDst!.Draw(ctx.NewSrc!, 300, 200, SR2D.Op.AlphaTest); });

            // 4) rotated draw through the SAME kernel DRAW_ROT, identical 16.16 geometry math in both layers
            Add("Rotate 256x256 (DRAW_ROT)", "DRAW_ROT, same kernel + same args on both sides", Reps,
                () => { ctx.OldDst!.DrawRotate(ctx.OldSrc!, 40, 40, 600, 400, 0.8f); },
                () => { ctx.NewDst!.DrawRotate(ctx.NewSrc!, 40, 40, 600, 400, 0.8f, false, UseWarp: false); });

            // 4b) the same rotated draw through the NEW engine's default warp path (no original equivalent)
            Add("Rotate 256x256 (new warp)", "DRAW_WARP - current engine only, original has no warp", Reps,
                null!,
                () => { ctx.NewDst!.DrawRotate(ctx.NewSrc!, 40, 40, 600, 400, 0.8f, false, UseWarp: true); });

            // 5) scaled draw -> RESIZE, 256x256 -> 512x512, one call, identical args (output = the 512x512 surface)
            int scaleReps = Math.Max(1, Reps / 4);
            Add("Scale 256->512 (RESIZE)", "RESIZE, same kernel on both sides", scaleReps,
                () => OldResizeCall(ctx),
                () => NewResizeCall(ctx), Target.Big);

            // 6) large-area fill / clear -> CLEAR_C over the whole 1280x800 surface
            int clearReps = Math.Max(1, Reps / 2);
            Add("Clear 1280x800", "CLEAR_C (full-surface fill)", clearReps,
                () => { ctx.OldDst!.ClearBuffer(BG); },
                () => { ctx.NewDst!.ClearBuffer(BG); }, Target.Cleared);

            // 7) crossfade -> BLEND
            Add("Blend 256x256 k=128", "BLEND", Reps,
                () => { ctx.OldDst!.Blend(ctx.OldSrc!, 300, 200, 128); },
                () => { ctx.NewDst!.Blend(ctx.NewSrc!, 300, 200, 128); });

            // 8) a realistic frame: clear + a grid of paint draws + a few blends + a few rotations
            int frameReps = Math.Max(1, Reps / 10);
            Add("Frame sim 40 paint 8 blend 4 rot", "CLEAR_C+PAINT+BLEND+DRAW_ROT", frameReps,
                () => OldFrame(ctx),
                () => NewFrame(ctx));

            Print(rows);
        }

        // ------------------------------------------------------------------ the workloads
        private static void OldFrame(Ctx c)
        {
            c.OldDst!.ClearBuffer(BG);
            for (int i = 0; i < 40; i++)
                c.OldDst.Draw(c.OldSrc!, (i % 8) * 160, 40 + (i / 8) * 160, OrigEngine.SR2D.Op.Paint);
            for (int i = 0; i < 8; i++)
                c.OldDst.Blend(c.OldSrc!, 900 + (i % 2) * 40, 100 + i * 60, 128);
            for (int i = 0; i < 4; i++)
                c.OldDst.DrawRotate(c.OldSrc!, 32, 32, 400 + i * 90, 300 + i * 70, Angles[i]);
        }

        private static void NewFrame(Ctx c)
        {
            c.NewDst!.ClearBuffer(BG);
            for (int i = 0; i < 40; i++)
                c.NewDst.Draw(c.NewSrc!, (i % 8) * 160, 40 + (i / 8) * 160, SR2D.Op.Paint);
            for (int i = 0; i < 8; i++)
                c.NewDst.Blend(c.NewSrc!, 900 + (i % 2) * 40, 100 + i * 60, 128);
            for (int i = 0; i < 4; i++)
                c.NewDst.DrawRotate(c.NewSrc!, 32, 32, 400 + i * 90, 300 + i * 70, Angles[i], false, UseWarp: false);
        }

        private static void OldResizeCall(Ctx c)
        {
            if (oldResize != null) oldResize(c.OldSrc!.DataPTR(0, 0), c.OldBig!.DataPTR(0, 0), SW, SH, DW, DH);
            else RawResizeOld(c.OldSrc!.DataPTR(0, 0), c.OldBig!.DataPTR(0, 0), SW, SH, DW, DH);
        }

        private static unsafe void NewResizeCall(Ctx c)
        {
            if (resizeRoute.StartsWith("original binding")) SR2D.Native.Resize(c.NewSrc!.Ptr, c.NewBig!.Ptr, SW, SH, DW, DH);
            else RawResizeNew(c.NewSrc!.Ptr, c.NewBig!.Ptr, SW, SH, DW, DH);
        }

        private static void BindOriginalResize()
        {
            var mi = typeof(OrigEngine.Sprite).GetMethod("SResize", BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) { resizeRoute = "raw DllImport (original SResize not found)"; return; }
            try
            {
                oldResize = (ResizeOldDel)Delegate.CreateDelegate(typeof(ResizeOldDel), mi);
                resizeRoute = "original binding (private SR2D.SResize via reflection delegate, no boxing)";
            }
            catch (Exception e)
            {
                resizeRoute = $"raw DllImport (could not bind original SResize: {e.GetType().Name})";
            }
        }

        private enum Target { Dst, Big, Cleared }        // which surface a workload writes, for the pixel check

        private sealed class Row
        {
            public string Name = "", Note = "";
            public double OldUs, NewUs;
            public int Reps;
            public bool NewOnly;
            public long OldSum, NewSum;
            public long OldNonBg, NewNonBg;
            public bool Match;
        }

        private static Row Run(string name, string note, int reps, Action? oldAct, Action? newAct, Target t, Ctx c)
        {
            var r = new Row { Name = name, Note = note, Reps = reps, NewOnly = oldAct == null };

            // one untimed call, then checksum the surface the workload writes. Same hash on both engines
            // means both sides really performed the identical pixel work for that row.
            if (!r.NewOnly)
            {
                c.ResetDests(BG);
                oldAct!();
                Check(c, t, true, out r.OldSum, out r.OldNonBg);
                c.ResetDests(BG);
                newAct!();
                Check(c, t, false, out r.NewSum, out r.NewNonBg);
                r.Match = r.OldSum == r.NewSum;
            }
            else
            {
                c.ResetDests(BG);
                newAct!();
                Check(c, t, false, out r.NewSum, out r.NewNonBg);
            }

            if (!r.NewOnly)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                r.OldUs = Median(reps, oldAct!);
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            r.NewUs = Median(reps, newAct!);
            return r;
        }

        private static void Check(Ctx c, Target t, bool original, out long hash, out long nonBg)
        {
            switch (t)
            {
                case Target.Big when original: FrameCheck(c.OldBig!, out hash, out nonBg); return;
                case Target.Big: FrameCheck(c.NewBig!, out hash, out nonBg); return;
                case Target.Cleared when original: FrameCheck(c.OldDst!, out hash, out nonBg); return;
                case Target.Cleared: FrameCheck(c.NewDst!, out hash, out nonBg); return;
                case Target.Dst when original: FrameCheck(c.OldDst!, out hash, out nonBg); return;
                default: FrameCheck(c.NewDst!, out hash, out nonBg); return;
            }
        }

        private static double Median(int reps, Action act)
        {
            var sw = new Stopwatch();
            var s = new double[RUNS];
            for (int i = 0; i < reps; i++) act();                  // warm-up, discarded
            for (int r = 0; r < RUNS; r++)
            {
                sw.Restart();
                for (int i = 0; i < reps; i++) act();
                sw.Stop();
                s[r] = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
            }
            Array.Sort(s);
            return s[RUNS / 2];
        }

        // sample the whole surface cheaply; proves the call drew something and lets the two engines'
        // outputs be compared for the same kernel
        private static void FrameCheck(OrigEngine.Sprite s, out long hash, out long nonBg)
        {
            hash = 1469598103934665603L; nonBg = 0;
            for (int y = 0; y < s.Height; y += 7)
                for (int x = 0; x < s.Width; x += 7)
                {
                    int p = s.GetPixel(x, y);
                    if (p != BG) nonBg++;
                    hash = (hash ^ (uint)p) * 1099511628211L;
                }
        }
        private static void FrameCheck(Sprite s, out long hash, out long nonBg)
        {
            hash = 1469598103934665603L; nonBg = 0;
            for (int y = 0; y < s.Height; y += 7)
                for (int x = 0; x < s.Width; x += 7)
                {
                    int p = s.GetPixel(x, y);
                    if (p != BG) nonBg++;
                    hash = (hash ^ (uint)p) * 1099511628211L;
                }
        }

        private static string Num(double v, int width) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture).PadLeft(width);

        private static void Print(System.Collections.Generic.List<Row> rows)
        {
            Console.WriteLine("workload                             | pixel check (both engines)  | original us/call | current us/call | current/original");
            Console.WriteLine("-------------------------------------+---------------------------+------------------+-----------------+-----------------");
            foreach (var r in rows)
            {
                string match = r.NewOnly ? "new path only" : r.Match ? "identical pixels" : "DIFFERENT pixels";
                string o = r.NewOnly ? "             n/a" : Num(r.OldUs, 16);
                string ratio = r.NewOnly || r.OldUs <= 0 ? "        n/a   "
                    : (r.NewUs / r.OldUs).ToString("F2", System.Globalization.CultureInfo.InvariantCulture).PadLeft(14) + "x";
                Console.WriteLine($"{r.Name,-35} | {match,-25} | {o} | {Num(r.NewUs, 15)} | {ratio}");
            }
            Console.WriteLine();
            Console.WriteLine("ratio > 1 = the CURRENT engine is SLOWER for that call; < 1 = it is faster.");
            Console.WriteLine("Pixel sampling per row (stride 7 over the whole written surface; non-background sample count):");
            foreach (var r in rows)
                Console.WriteLine($"  {r.Name,-35} reps {r.Reps,4} | original {(r.NewOnly ? "  -  " : r.OldNonBg.ToString().PadLeft(5))} | current {r.NewNonBg,5} | {r.Note}");
            Console.WriteLine();
            Console.WriteLine("(a Cleared 1280x800 row legitimately samples 0 non-background pixels - writing BG *is* its work)");
            Console.WriteLine("NOTE both columns include that engine's own C# binding layer (clip math + P/Invoke transition);");
            Console.WriteLine("     the pixels written per call are identical on both sides unless the row says DIFFERENT pixels.");
        }

        // ------------------------------------------------------------------ scene
        private sealed class Ctx
        {
            public OrigEngine.Sprite? OldDst, OldSrc, OldBig;
            public Sprite? NewDst, NewSrc, NewBig;

            public void Build()
            {
                OldDst = new OrigEngine.Sprite(TW, TH, OrigEngine.SR2D.Op.Paint);
                OldSrc = new OrigEngine.Sprite(SW, SH, OrigEngine.SR2D.Op.Paint);
                OldBig = new OrigEngine.Sprite(DW, DH, OrigEngine.SR2D.Op.Paint);
                NewDst = new Sprite(TW, TH, SR2D.Op.Paint);
                NewSrc = new Sprite(SW, SH, SR2D.Op.Paint);
                NewBig = new Sprite(DW, DH, SR2D.Op.Paint);

                // identical content on both engines, including transparent pixels for ALPHA_T
                for (int y = 0; y < SH; y++)
                    for (int x = 0; x < SW; x++)
                    {
                        int a = ((x * 5 + y * 3) & 0xFF);
                        if ((x % 17 == 0) && (y % 13 == 0)) a = 0;            // real holes for the alpha test
                        int c = (a << 24) | ((x & 0xFF) << 16) | ((y & 0xFF) << 8) | ((x * y) & 0xFF);
                        OldSrc.SetPixel(x, y, c);
                        NewSrc.SetPixel(x, y, c);
                    }
                ResetDests(BG);
            }

            public void ResetDests(int c)
            {
                OldDst!.ClearBuffer(c); OldBig!.ClearBuffer(c);
                NewDst!.ClearBuffer(c); NewBig!.ClearBuffer(c);
            }
        }
    }
}
