using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>Everything a test may read: canvas, assets and the live slider values.</summary>
    /// <summary>Parameters a test can depend on; recorded by <see cref="Ctx"/> as they are read.</summary>
    [Flags]
    internal enum Param
    {
        None = 0, Mouse = 1, Z = 2, Angle = 4, Scale = 8, Blend = 16, Brite = 32, Count = 64,
        Smooth = 128, NotMask = 256, MaskBits = 512, Op = 1024, DotStep = 2048, Xor = 4096,
        /// <summary>The test reads <see cref="Ctx.Time"/> - the "Animate" check box matters (unticked = time stands still).</summary>
        Time = 8192,
        /// <summary>The test reads the orbit camera (<see cref="Ctx.Yaw"/> / Pitch / PanX / PanY): left drag = orbit, middle drag = pan, wheel = zoom.</summary>
        Camera = 16384,
        /// <summary>The test reads <see cref="Ctx.Grid"/> - the grid cell size in px (grid density of the block demos).</summary>
        Grid = 32768,
        /// <summary>The test reads <see cref="Ctx.Speed"/> - the animation speed factor of the animated demos.</summary>
        Speed = 65536,
        /// <summary>The test reads <see cref="Ctx.ShowGrid"/> - the grid-lines check box of the block demos.</summary>
        GridOn = 131072,
        /// <summary>The test reads <see cref="Ctx.OffsetX"/> - the X offset slider (the selection-tool scenario).</summary>
        OffsetX = 262144,
        /// <summary>The test reads <see cref="Ctx.OffsetY"/> - the Y offset slider (the selection-tool scenario).</summary>
        OffsetY = 524288,
    }

    internal sealed class Ctx
    {
        public Sprite Canvas = null!;
        /// <summary>Frame tests (DemoTest.Frame): the editable transform frame - draw your object through Frame.Transform at (Frame.OriginX, OriginY) and call Frame.Attach(w, h, ox, oy) every frame so the handles know where it is.</summary>
        public TransformFrame Frame = null!;
        public Sprite Temp = null!;            // scratch, same size as a sprite
        public Assets A = null!;
        public int LastInt;                    // e.g. MaskInterSector result, shown in the status bar
        /// <summary>Offset (px) of the grabbable checker backdrop: drag anywhere with the hand cursor (tests with <see cref="DemoTest.GrabBackdrop"/>). Deliberately NOT the Mouse parameter, so no follow-the-mouse check box appears.</summary>
        public int BackdropX, BackdropY;
        public string Note = "";
        /// <summary>
        /// Slow tests only: true while a control is being dragged - draw something cheap (a reduced model, points mode ...)
        /// instead of the full render; the bench runs the real frames once the controls have been idle for a moment.
        /// </summary>
        public bool Preview;
        /// <summary>
        /// Slow tests: report long work in phases. Progress(phase, fraction 0..1) - the bench shows the phase name in the
        /// status line and the fraction in the progress bar, presents the canvas when <paramref name="present"/> is true
        /// (so a half-finished render is visible) and pumps the message queue. Null when nobody listens.
        /// </summary>
        public Action<string, double, bool>? Progress;
        public void Report(string phase, double fraction, bool present = false) => Progress?.Invoke(phase, fraction, present);
        /// <summary>
        /// Text labels the bench draws with GDI after the frame was presented (not part of the timed work, not on the canvas):
        /// use for "which sprite / column is which". Cleared every frame.
        /// </summary>
        public readonly List<(int x, int y, string text)> Labels = new List<(int, int, string)>();
        public void Label(int x, int y, string text) { lock (Labels) Labels.Add((x, y, text)); }
        /// <summary>
        /// A line for the info panel in the top-left corner of the viewport (under the bench's own lines: fps, angle,
        /// pivot ...). Use instead of drawing status text at (8, 8) yourself. Cleared every frame.
        /// </summary>
        public readonly List<string> InfoLines = new List<string>();
        public void Info(string text) { lock (InfoLines) InfoLines.Add(text); }

        // Every parameter below is a property whose getter records the read in Used, so
        // the bench knows which sliders/check boxes matter for the running test without
        // any per-test annotation. Clones (DrawParallel bands) share the same tracker.
        sealed class Tracker { public int Bits; }
        Tracker used = new Tracker();
        public Param Used => (Param)System.Threading.Volatile.Read(ref used.Bits);
        public void ResetUsed() => used = new Tracker();
        void Touch(Param p) { if ((used.Bits & (int)p) == 0) System.Threading.Interlocked.Or(ref used.Bits, (int)p); }

        int x, y, z = 120, blend = 128, count = 1, maskBits = Assets.MaskCircle, dotStep;
        float angle, scale = 1f, scaleY = 1f, brite = 1f, time, yaw, pitch = 0.55f, panX, panY;
        bool smooth, notMask, xor;
        SR2D.Op op = SR2D.Op.Paint;

        /// <summary>Seconds of animation time. Stops advancing while the "Animate" check box is off.</summary>
        public float Time { get { Touch(Param.Time); return time; } set => time = value; }
        /// <summary>Orbit camera (voxel tests): yaw / pitch in radians changed by dragging with the left button, pan in pixels by the middle button.</summary>
        public float Yaw { get { Touch(Param.Camera); return yaw; } set => yaw = value; }
        public float Pitch { get { Touch(Param.Camera); return pitch; } set => pitch = value; }
        public float PanX { get { Touch(Param.Camera); return panX; } set => panX = value; }
        public float PanY { get { Touch(Param.Camera); return panY; } set => panY = value; }
        /// <summary>Vertical scale for resizable tests (dragging the top / bottom edge); equals <see cref="Scale"/> otherwise.</summary>
        public float ScaleY { get { Touch(Param.Scale); return scaleY; } set => scaleY = value; }
        /// <summary>Rotation pivot in SOURCE pixel units for the MouseRotates tests (right click on the sprite sets it; -1 = sprite centre).</summary>
        public int PivotX { get { Touch(Param.Mouse); return pivotX < 0 ? -1 : (int)pivotX; } set => pivotX = value; }
        public int PivotY { get { Touch(Param.Mouse); return pivotY < 0 ? -1 : (int)pivotY; } set => pivotY = value; }
        /// <summary>The pivot with fractions, in the units of the test's <see cref="DemoTest.ObjectSize"/> (vector test: picture units at Scale 1); -1 = centre.</summary>
        public float PivotXF { get { Touch(Param.Mouse); return pivotX; } set => pivotX = value; }
        public float PivotYF { get { Touch(Param.Mouse); return pivotY; } set => pivotY = value; }
        float pivotX = -1, pivotY = -1;

        /// <summary>The Grid slider: grid cell size in px for the grid demos (Move / Offset scrambles).</summary>
        public int Grid { get { Touch(Param.Grid); return grid; } set => grid = value; }
        /// <summary>The Speed slider: animation speed factor (px/s = Speed * 8) for the animated demos.</summary>
        public int Speed { get { Touch(Param.Speed); return speed; } set => speed = value; }
        /// <summary>PaintByMouse tests: the pen paints on the canvas while the left button is down, at PenX / PenY.</summary>
        public bool PenDown { get { Touch(Param.Mouse); return penDown; } set => penDown = value; }
        public int PenX { get { Touch(Param.Mouse); return penX; } set => penX = value; }
        public int PenY { get { Touch(Param.Mouse); return penY; } set => penY = value; }
        /// <summary>The "Grid lines" check box of the block demos (draw the grid over the board).</summary>
        public bool ShowGrid { get { Touch(Param.GridOn); return showGrid; } set => showGrid = value; }
        /// <summary>The "Offset X / Y" sliders of the selection-tool scenario (px applied per change, wrapped by the selection).</summary>
        public int OffsetX { get { Touch(Param.OffsetX); return offsetX; } set => offsetX = value; }
        public int OffsetY { get { Touch(Param.OffsetY); return offsetY; } set => offsetY = value; }
        int grid = 64, speed = 6, offsetX, offsetY; bool penDown, showGrid = true; int penX, penY;

        /// <summary>"Mouse"/light/object position on the canvas (drag with the left button).</summary>
        public int X { get { Touch(Param.Mouse); return x; } set => x = value; }
        public int Y { get { Touch(Param.Mouse); return y; } set => y = value; }
        public int Z { get { Touch(Param.Z); return z; } set => z = value; }                       // light height
        public float Angle { get { Touch(Param.Angle); return angle; } set => angle = value; }     // radians
        public float Scale { get { Touch(Param.Scale); return scale; } set => scale = value; }
        public int Blend { get { Touch(Param.Blend); return blend; } set => blend = value; }       // 0..255
        public float Brite { get { Touch(Param.Brite); return brite; } set => brite = value; }     // -2..2
        public int Count { get { Touch(Param.Count); return count; } set => count = value; }       // repetitions per frame
        public bool Smooth { get { Touch(Param.Smooth); return smooth; } set => smooth = value; }  // AA / bilinear
        public bool NotMask { get { Touch(Param.NotMask); return notMask; } set => notMask = value; }
        public int MaskBits { get { Touch(Param.MaskBits); return maskBits; } set => maskBits = value; }
        public SR2D.Op Op { get { Touch(Param.Op); return op; } set => op = value; }
        public int DotStep { get { Touch(Param.DotStep); return dotStep; } set => dotStep = value; }
        public bool Xor { get { Touch(Param.Xor); return xor; } set => xor = value; }

        public Ctx Clone() => (Ctx)MemberwiseClone();

        public int W => Canvas.Width;
        public int H => Canvas.Height;
        public int S => A.Size;
    }

    internal delegate void RenderFn(Ctx c);

    internal sealed class DemoTest
    {
        public string Group = "";
        public string Name = "";
        public string Desc = "";
        public bool NeedsWarp;                 // requires the new DRAW_WARP export
        public bool NeedsLine2;                // requires the new DRAW_LINE2 export
        public bool NeedsAlphaOver;
        public bool NeedsPoly;                 // requires DRAW_POLY (shape methods)
        public bool NeedsBlur;                 // requires DRAW_BLUR (Sprite.Effects.cs)
        public bool NeedsFx;                   // requires DRAW_FX (Effects.cs)
        public bool NeedsFlood;                // requires FLOOD_MASK / FILL_MASK8 / LERP_MASK8 (Selection.cs)
        public bool NeedsVoxel;                // requires VOXEL_FACES / VOXEL_LIGHT / VOXEL_RENDER (VoxelGrid.cs)
        public bool NeedsBlend;                // requires BLEND_MODE (Sprite.Blend.cs, blend-mode op codes)
        /// <summary>
        /// Test-specific captions for the shared controls (Param -> caption). The form shows these instead of the
        /// generic names while the test is selected, and greys out controls the test does not list. For Op the
        /// caption may list the meaning of each entry separated by '|' after a colon, e.g. "Camera: Paint=Isometric|...".
        /// </summary>
        public Dictionary<Param, string> Controls = new Dictionary<Param, string>();
        /// <summary>Start values of the controls for this test (slider value; check boxes 0/1; Op = entry index; MaskBits = bit mask). Restored by "Reset params". The bench remembers the user's changes per test.</summary>
        public Dictionary<Param, int> Defaults = new Dictionary<Param, int>();
        /// <summary>Slider ranges for this test (min, max); unlisted sliders keep the generic range.</summary>
        public Dictionary<Param, (int min, int max)> Ranges = new Dictionary<Param, (int, int)>();
        /// <summary>Entries shown in the Op selector instead of the blend-op names (the test still receives SR2D.Op index + 1).</summary>
        public string[]? OpNames;
        /// <summary>Names of the bit check boxes (up to 8) replacing circle / diamond / stripes / checker; null = the mask bits.</summary>
        public string[]? BitNames;
        /// <summary>Bit boxes behave as radio buttons (exactly one set).</summary>
        public bool BitsExclusive;
        /// <summary>Mouse on the canvas drives the orbit camera (Ctx.Yaw / Pitch / Pan / wheel = zoom) instead of the object position.</summary>
        public bool Camera;
        /// <summary>Camera tests: Op entry to switch to when the user starts orbiting while a preset is selected (-1 = none).</summary>
        public int FreeOpIndex = -1;
        /// <summary>The object rectangle (S x Scale by S x ScaleY around the position) can be resized by dragging its edges.</summary>
        public bool Resizable;
        /// <summary>The demo shows an editable transform frame (move / scale / stretch / rotate / Ctrl-corner perspective) that drives Ctx.Frame.Transform; the test draws through it.</summary>
        public bool Frame;
        /// <summary>With Animate off, dragging outside the object (or the wheel) sets the Angle slider = direction from the object centre to the mouse.</summary>
        public bool MouseRotates;
        /// <summary>MouseRotates tests: the test's positive angle turns counter-clockwise on screen (the original DrawRotate); the bench mirrors its angle / pivot maths accordingly.</summary>
        public bool AngleCCW;
        /// <summary>MouseRotates tests: extra rotation per second of Ctx.Time while Animate is on (the frame overlay follows it); 0 = the test ignores Time.</summary>
        public float AnimSpin = 0.5f;
        /// <summary>Size of the draggable object in its own units at Scale 1 (canvas pixels at x1.00). Null = a sprite-sized square. The frame / handles / pivot maths use it (vector import test: the picture's size).</summary>
        public Func<Ctx, SizeF>? ObjectSize;
        /// <summary>Drop-shadow tests: the object is stationary (canvas centre); dragging sets the Z slider (distance) and the Angle slider (direction) from the vector centre -> pointer.</summary>
        public bool ShadowByMouse;
        /// <summary>The whole canvas is the draggable object (a backdrop the test shifts by Ctx.X / Y - canvas centre): open hand everywhere, no pivot marker.</summary>
        public bool DragAnywhere;
        /// <summary>Grabbable checker backdrop: drag anywhere with the hand cursor slides it - without the "follow the mouse" check box (the offset lives in Ctx.BackdropX/Y, not in the Mouse parameter).</summary>
        public bool GrabBackdrop;
        /// <summary>The canvas is a drawing surface: the pen cursor shows and the LEFT button paints (Ctx.PenDown / PenX / PenY), like an image editor.</summary>
        public bool PaintByMouse;
        /// <summary>Uses the FractalLayout placement: the Copies slider is capped to the last viewport split that stays visible.</summary>
        public bool FractalCopies;
        /// <summary>Right click on the object sets the pivot (Ctx.PivotXF / YF) without the rotate handles - for the pivot overloads of non-rotating calls (DrawScaled).</summary>
        public bool PivotByClick;
        /// <summary>Slow tests: the test draws a cheap stand-in while controls move (Ctx.Preview) - the bench then renders preview frames continuously and the real frames once idle.</summary>
        public bool HasPreview;
        /// <summary>The file dialog allows several files (FilePath then holds them separated by '|').</summary>
        public bool FileMulti;
        /// <summary>Creates a WinForms control the bench docks above the canvas while this test is selected (null = none). Built once, kept.</summary>
        public Func<Control>? ControlStrip;
        /// <summary>Height of the strip row (the canvas row shrinks by it).</summary>
        public int StripHeight = ControlsDemo.StripHeight;
        /// <summary>Left click inside the object (object units) offered to the test first: return true to swallow it (no drag starts).</summary>
        public Func<Ctx, float, float, bool>? ObjectClick;
        /// <summary>Start position of the draggable object as a fraction of the canvas (-1 = keep the current one / centre); applied when the test is selected or reset.</summary>
        public float HomeX = -1, HomeY = -1;
        public DemoTest Also(Action<DemoTest> f) { f(this); return this; }
        /// <summary>Hand-written code sample for the "Code" view; null = the demo lifts this test's lambda (+ the helpers it calls) out of Tests.cs.</summary>
        public string? Code = null;
        /// <summary>The "Sprite size" setup combo is switched to this value when the test is selected (0 = leave what the user had).</summary>
        public int SpriteSize;
        /// <summary>An extra control for the TEST tab's settings (e.g. the curve editor of the Curve tangent); built once on select.</summary>
        public Func<System.Windows.Forms.Control>? SettingsControl;
        /// <summary>When the extra settings control is visible (evaluated per frame; null = always).</summary>
        public Func<Ctx, bool>? SettingsVisible;

        // fluent setup ----------------------------------------------------------------
        /// <summary>Captions: Ui(param, caption, param, caption, ...). Controls not listed are disabled for this test.</summary>
        public DemoTest Ui(params object[] kv) { for (int i = 0; i + 1 < kv.Length; i += 2) Controls[(Param)kv[i]] = (string)kv[i + 1]; return this; }
        public DemoTest With(Param p, int value) { Defaults[p] = value; return this; }
        public DemoTest With(Param p, bool value) { Defaults[p] = value ? 1 : 0; return this; }
        public DemoTest Range(Param p, int min, int max) { Ranges[p] = (min, max); return this; }
        public DemoTest Ops(params string[] names) { OpNames = names; return this; }
        public DemoTest Bits(bool exclusive, params string[] names) { BitNames = names; BitsExclusive = exclusive; return this; }
        /// <summary>
        /// Slow (non-real-time) test: the bench renders this many frames after the test is selected or a parameter
        /// changes, then stops and shows the exact total / per-frame time and the equivalent fps instead of looping.
        /// 0 = normal continuous rendering.
        /// </summary>
        public int SlowFrames;
        /// <summary>Slow tests: parameters that trigger a re-run when they change (others are ignored while frozen).</summary>
        public Param SlowRerunOn = Param.Mouse | Param.Count | Param.Angle | Param.Scale | Param.Op | Param.MaskBits | Param.Smooth | Param.NotMask | Param.Xor | Param.Camera | Param.Z | Param.Brite;
        /// <summary>Called on the UI thread when the user picks a file for this test (tests that load .vox / .obj); null = no file button.</summary>
        public string? FileFilter;
        public string? FilePath;               // last chosen file (set by the form)
        public bool Available => !(NeedsWarp && !Caps.HasWarp) && !(NeedsLine2 && !Caps.HasLine2) && !(NeedsAlphaOver && !Caps.HasAlphaOver) && !(NeedsPoly && !Caps.HasPoly) && !(NeedsBlur && !Caps.HasBlur) && !(NeedsFx && !Caps.HasFx) && !(NeedsFlood && !Caps.HasFlood) && !(NeedsVoxel && !Caps.HasVoxel) && !(NeedsBlend && !Caps.HasBlendMode);
        public bool ClearsItself;              // test does its own background clear
        /// <summary>Suite-only correctness check: runs once per suite pass after the timed frames, on a
        /// deterministic frame. Returns null = pass, a string = the failure (logged, counted as FAILED).</summary>
        public Func<Ctx, string?>? Check;
        public Param Used;                     // parameters this test has read so far (for highlighting)
        public RenderFn Run = _ => { };
        public override string ToString() => Name;
    }

    internal static class Tests
    {
        public const string GOriginal = "Original API";
        public const string GMask = "Original API - masked";
        public const string GBump = "Original API - bump mapping";
        public const string GXform = "Original API - transforms";
        public const string GScene = "Scenes (user test code)";
        public const string GNew = "New API (native kernels)";
        public const string GShapes = "Shapes (Sprite.Shapes.cs)";
        public const string GText = "Text (Sprite.Text.cs, SpriteFont.cs)";
        public const string GEdit = "Editing (Sprite.Edit.cs, Selection.cs)";
        public const string GLayers = "Layers (LayeredSprite.cs)";
        public const string GEffects = "Effects (Sprite.Effects.cs)";
        public const string GBlend = "Blend modes (Sprite.Blend.cs)";
        public const string GFiles = "Files (Png.cs, WebP.cs, Vector.*.cs)";
        public const string GVoxel = "Voxels (VoxelGrid.cs)";
        public const string GControls = "Controls (SpriteControls*.cs)";
        public const string GCompare = "Old vs new";

        static TextCache? textCache;                 // the SpriteFont test's cached text bitmaps (lives for the process)
        static SR2D.LineOp ShapeOp(Ctx c) => c.Xor ? SR2D.LineOp.Xor : c.Op switch
        {
            SR2D.Op.AlphaBlend => SR2D.LineOp.AlphaBlend, SR2D.Op.AlphaOver => SR2D.LineOp.AlphaOver, SR2D.Op.Add => SR2D.LineOp.Add,
            SR2D.Op.Max => SR2D.LineOp.Max, SR2D.Op.Min => SR2D.LineOp.Min, SR2D.Op.Add2D => SR2D.LineOp.Blend, _ => SR2D.LineOp.Set
        };

        static readonly Random Rng = new Random(1234);

        public static List<DemoTest> All()
        {
            var L = new List<DemoTest>();
            DemoTest T(string g, string n, string d, RenderFn f, bool warp = false, bool clears = false, bool needsLine2 = false, bool needsOver = false, bool needsPoly = false, bool needsBlur = false, bool needsFx = false, bool needsFlood = false, bool needsVoxel = false, bool needsBlend = false, Func<Ctx, string?>? check = null)
            {
                var t = new DemoTest { Group = g, Name = n, Desc = d, Run = f, NeedsWarp = warp, NeedsLine2 = needsLine2, NeedsAlphaOver = needsOver, NeedsPoly = needsPoly, NeedsBlur = needsBlur, NeedsFx = needsFx, NeedsFlood = needsFlood, NeedsVoxel = needsVoxel, NeedsBlend = needsBlend, ClearsItself = clears };
                t.Check = check;
                L.Add(t); return t;
            }
            // captions for the shared controls while a test is selected: UI(param, caption, param, caption, ...)
            static Dictionary<Param, string> UI(params object[] kv) { var d = new Dictionary<Param, string>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(Param)kv[i]] = (string)kv[i + 1]; return d; }

            // ======================================================= Original API
            T(GOriginal, "Output paths: raw blit vs SpriteBox", "How much does the NEW way of getting pixels onto the window cost against the ORIGINAL one? The engine always presented by taking the control's DC with GetDC, blitting the sprite into it and calling ReleaseDC; SpriteBox is the newer output control (its own back buffer, Present() or WM_PAINT, plus stretch / zoom). The strip above the canvas shows the SAME scene four ways side by side, each with its own stopwatch, and each pane writes its per-frame draw + blit milliseconds into its own picture: 1 = a stock PictureBox driven the original way (GetDC -> Sprite.PaintToDevice(HandleRef) -> ReleaseDC), 2 = a SpriteBox driven by RedrawNow() (the Render event paints its Surface, then Present() blits it: the game-loop route, no message round trip), 3 = a SpriteBox driven the way a WinForms app drives it (Redraw() -> the WM_PAINT message -> the Render event -> the blit; its 'blit' figure is the paint total minus the draw), 4 = a SpriteBox in SizeMode Zoom at 200 % with the bilinear filter, i.e. the same surface resampled on every present. The ratios (box.RedrawNow / box.WM_PAINT / box zoom2 against the raw blit) are in the info panel and on the strip. 'Copies' sets how many Sprite.Draw calls the scene makes, 'GDI DIB buffers' switches every buffer between a DIB section (one BitBlt per present) and a plain buffer (SetDIBitsToDevice) - the same switch for all four panes, so they stay comparable. The button opens a borderless FULLSCREEN plain Form that runs ONE route alone, unthrottled, with the real frame rate on screen (Tab cycles the route, Left / Right change Copies, G switches the buffers, a click or Esc closes it and the bench resumes - while it is open this window renders nothing).",
                c => OutputDemo.Canvas(c))
                .Also(t => { t.ControlStrip = OutputDemo.Build; t.StripHeight = OutputDemo.StripHeight; t.ClearsItself = true; });
            T(GOriginal, "Draw (Op selector)", "Sprite.Draw with the selected Op: Count copies laid out as a centred block with a small gap between them (the block grows until it fills the canvas). Uses the alpha sprite for AlphaBlend / AlphaTest, otherwise the colour sprite. The backdrop is a dark alpha checker so the ops that mix with the background (Add / Mul / Max / Min / AlphaBlend) show what they do; DRAG the checkered backdrop with the hand cursor (the sprites stay) to slide a lighter or darker cell under a sprite.",
                c => { Checker(c, c.BackdropX, c.BackdropY); SpacedGrid(c, c.S / 8, (x, y) => c.Canvas.Draw(SrcFor(c, c.Op), x, y, c.Op)); })
                .Ui(Param.Count, "Copies (centred block, filling the canvas)", Param.Op, "Blend op").Also(t => t.GrabBackdrop = true);
            var fDrawN = new RegionCache();
            T(GOriginal, "Draw (Op selector) <new>", "The same Sprite.Draw with the selected Op, but the copies fill the whole viewport as a 'spiralling fractal': copy 1 fills the viewport; every further copy splits one largest existing region in half across its longer side (2 = left | right, 3 = left | right-top over right-bottom, 4 = quarters, and so on). Every region shows its own copy of the sprite, PRESCALED and PRECROPPED once per layout change into a ready sprite - a frame costs one plain Draw per copy, nothing is cropped or scaled per frame. Uses the alpha sprite for AlphaBlend / AlphaTest, otherwise the colour sprite; the dark alpha checker behind the tiles can be DRAGGED with the hand cursor, so the ops that mix with the background show what they do over light and dark cells. The rectangles are precalculated once per viewport size and the Copies slider is capped at the last split that stays visible.",
                c =>
                {
                    var regs = FractalLayout.Regions(c.W, c.H, Math.Max(1, c.Count));
                    Checker(c, c.BackdropX, c.BackdropY);
                    fDrawN.Ensure(((long)Math.Max(1, c.Count) << 48) ^ ((long)c.W << 32) ^ (uint)c.H ^ ((long)c.Op << 4) ^ ((long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.A) << 56), regs, i => Prescaled(SrcFor(c, c.Op), regs[i]));
                    for (int i = 0; i < regs.Count; i++) c.Canvas.Draw(fDrawN.Sprites[i], regs[i].X, regs[i].Y, c.Op);
                })
                .Ui(Param.Count, "Copies (fractal partition of the viewport; capped where the next tile would be too small)", Param.Op, "Blend op").Also(t => { t.GrabBackdrop = true; t.FractalCopies = true; });
            var fOpsN = new RegionCache();
            T(GOriginal, "Draw - all 9 ops", "One copy of each blend op, labelled: Paint, AlphaTest, AlphaBlend, Add2D, Add, Mul, Mul2X, Max, Min. The nine tiles fill the viewport with the 'spiralling fractal' partition (one region per op, each sprite prescaled + precropped to its region once per layout), the caption sits inside its tile at a scale that fits. Copies stack on top of each other in every tile. The dark alpha checker behind them can be DRAGGED with the hand cursor (grasp any empty spot) - slide a light or a dark cell under the tiles and compare how each op reacts.",
                c =>
                {
                    var ops = new[] { SR2D.Op.Paint, SR2D.Op.AlphaTest, SR2D.Op.AlphaBlend, SR2D.Op.Add2D, SR2D.Op.Add, SR2D.Op.Mul, SR2D.Op.Mul2X, SR2D.Op.Max, SR2D.Op.Min };
                    var regs = FractalLayout.Regions(c.W, c.H, 9);                    // one region per op, the same fractal as the <new> tests
                    Checker(c, c.BackdropX, c.BackdropY);
                    fOpsN.Ensure(((long)c.W << 32) ^ (uint)c.H ^ ((long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.A) << 56), regs, i => Prescaled(SrcFor(c, ops[i]), regs[i]));
                    int n = Math.Max(1, c.Count);
                    for (int i = 0; i < 9; i++)
                    {
                        for (int k = 0; k < n; k++) c.Canvas.Draw(fOpsN.Sprites[i], regs[i].X, regs[i].Y, ops[i]);   // copies stack on top of each other
                        OpLabel(c, regs[i], ops[i].ToString());
                    }
                }).Ui(Param.Count, "Copies stacked on top of each other in every tile (the op applied again - Add brightens, Mul darkens ...)").Also(t => t.GrabBackdrop = true);
            T(GOriginal, "Draw - clipped (moving)", "Clipping test: the lock rect is inset half a sprite from the canvas edge (the thin frame) and Count sprites travel along that frame, so every one is partly outside it. How far outside breathes slowly between 15 % and 85 % of the sprite - never fully in, never fully gone - so the left / right / top / bottom clip paths and the corner cases are all exercised continuously, whatever the sprite size. The path is a rounded rectangle (corner radius = half a sprite): position and outward normal are continuous, so the motion never jumps at a corner.",
                c =>
                {
                    int m = c.S / 2;                                                    // frame inset: half a sprite
                    int L = m, T = m, R = Math.Max(m + 1, c.W - m), B = Math.Max(m + 1, c.H - m);
                    c.Canvas.SetLockRect(L, R, T, B);
                    float r = Math.Min(c.S / 2f, Math.Min(R - L, B - T) / 2f);          // corner radius of the path
                    float sw = (R - L) - 2 * r, sh = (B - T) - 2 * r, q = MathF.PI / 2 * r;   // straight lengths, quarter-arc length
                    float per = 2 * (sw + sh) + 4 * q;
                    for (int k = 0; k < c.Count; k++)
                    {
                        float t = ((c.Time * 80f + k * per / c.Count) % per + per) % per;     // arc length along the path (px)
                        // walk the 8 pieces: top edge, TR arc, right edge, BR arc, bottom edge, BL arc, left edge, TL arc
                        float px, py, nx, ny;
                        if (t < sw) { px = L + r + t; py = T; nx = 0; ny = -1; }
                        else if ((t -= sw) < q) { float a = -MathF.PI / 2 + t / r; nx = MathF.Cos(a); ny = MathF.Sin(a); px = R - r + nx * r; py = T + r + ny * r; }
                        else if ((t -= q) < sh) { px = R; py = T + r + t; nx = 1; ny = 0; }
                        else if ((t -= sh) < q) { float a = t / r; nx = MathF.Cos(a); ny = MathF.Sin(a); px = R - r + nx * r; py = B - r + ny * r; }
                        else if ((t -= q) < sw) { px = R - r - t; py = B; nx = 0; ny = 1; }
                        else if ((t -= sw) < q) { float a = MathF.PI / 2 + t / r; nx = MathF.Cos(a); ny = MathF.Sin(a); px = L + r + nx * r; py = B - r + ny * r; }
                        else if ((t -= q) < sh) { px = L; py = B - r - t; nx = -1; ny = 0; }
                        else { float a = MathF.PI + (t - sh) / r; nx = MathF.Cos(a); ny = MathF.Sin(a); px = L + r + nx * r; py = T + r + ny * r; }
                        float outside = 0.5f + 0.35f * MathF.Sin(c.Time * 0.6f + k * 1.3f);  // 0.15 .. 0.85 of the sprite beyond the frame
                        int x = (int)MathF.Round(px + nx * (outside - 0.5f) * c.S) - c.S / 2, y = (int)MathF.Round(py + ny * (outside - 0.5f) * c.S) - c.S / 2;
                        c.Canvas.Draw(SrcFor(c, c.Op), x, y, c.Op);
                    }
                    c.Canvas.SetLockRect();
                    int fc = unchecked((int)0xFFE0B040);
                    c.Canvas.DrawLine(L, T, R - 1, T, fc); c.Canvas.DrawLine(R - 1, T, R - 1, B - 1, fc); c.Canvas.DrawLine(R - 1, B - 1, L, B - 1, fc); c.Canvas.DrawLine(L, B - 1, L, T, fc);
                }).Ui(Param.Count, "Sprites travelling along the frame", Param.Op, "Blend op", Param.Time, "Animate: movement + breathing").With(Param.Count, 10);
            T(GOriginal, "Blend (factor slider)", "Sprite.Blend - crossfade of the colour sprite over the background by BlendFactor (0 = background, 255 = sprite). Count copies CASCADE over each other (each one a step down-right, the stack centred) so every copy blends over the previous ones and the overlap shows the factor accumulating; the alpha checker behind them can be DRAGGED with the hand cursor to see the mix against a light and a dark cell.",
                c => { Checker(c, c.BackdropX, c.BackdropY); Cascade(c, (x, y) => c.Canvas.Blend(c.A.Color, x, y, c.Blend)); })
                .Ui(Param.Blend, "Blend factor (0 = background .. 255 = sprite)", Param.Count, "Copies (cascading over each other)").With(Param.Count, 10).Also(t => t.GrabBackdrop = true);
            T(GOriginal, "MulAddS2X", "Per-channel multiply/add: Mul from Brightness slider, Add from Blend slider. Both are neutral at the middle of their travel (Mul x1.00, Add +0), so the start values are off-neutral - otherwise this test is a plain copy and looks exactly like Sprite.Draw.",
                c =>
                {
                    int m = (int)Math.Clamp(c.Brite * 128, 0, 255); int mul = SR2D.ARGB((byte)m, (byte)m, (byte)m, (byte)m);
                    int add = SR2D.ARGB((byte)c.Blend, (byte)c.Blend, (byte)c.Blend, (byte)c.Blend);
                    SpacedGrid(c, 0, (x, y) => c.Canvas.MulAddS2X(c.A.Color, x, y, mul, add));   // centred, no mouse (drag the hand cursor instead)
                }).With(Param.Brite, 55).With(Param.Blend, 175);
            var fMulAddN = new RegionCache();
            T(GOriginal, "MulAddS2X <new>", "Per-channel multiply/add (Mul from Brightness, Add from Blend) with the fractal copy placement of 'Draw (Op selector) <new>': the copies partition the viewport, every region drawn from its own prescaled + precropped sprite - one MulAddS2X per copy per frame, no per-frame scaling. Count = 1 fills the whole viewport with one big image.",
                c =>
                {
                    int m = (int)Math.Clamp(c.Brite * 128, 0, 255); int mul = SR2D.ARGB((byte)m, (byte)m, (byte)m, (byte)m);
                    int add = SR2D.ARGB((byte)c.Blend, (byte)c.Blend, (byte)c.Blend, (byte)c.Blend);
                    var regs = FractalLayout.Regions(c.W, c.H, Math.Max(1, c.Count));
                    fMulAddN.Ensure(((long)Math.Max(1, c.Count) << 48) ^ ((long)c.W << 32) ^ (uint)c.H ^ ((long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.A) << 56), regs, i => Prescaled(c.A.Color, regs[i]));
                    for (int i = 0; i < regs.Count; i++) c.Canvas.MulAddS2X(fMulAddN.Sprites[i], regs[i].X, regs[i].Y, mul, add);
                })
                .Ui(Param.Count, "Copies (fractal partition of the viewport; capped where the next tile would be too small)", Param.Brite, "Mul (Brightness x 128 per channel; 100 = x1.00, no change)", Param.Blend, "Add (0..255 per channel; 128 = +0, no change)").With(Param.Brite, 55).With(Param.Blend, 175).Also(t => t.FractalCopies = true);
            T(GOriginal, "MoveByte (channel swap)", "Copies channels: red->blue, green->red, blue->green of the colour sprite onto the canvas.",
                c => SpacedGrid(c, 0, (x, y) =>                                       // centred, no mouse (drag the hand cursor instead)
                {
                    c.Canvas.MoveByte(c.A.Color, x, y, SR2D.ColChannel.ChRed, SR2D.ColChannel.ChBlue);
                    c.Canvas.MoveByte(c.A.Color, x, y, SR2D.ColChannel.ChGreen, SR2D.ColChannel.ChRed);
                    c.Canvas.MoveByte(c.A.Color, x, y, SR2D.ColChannel.ChBlue, SR2D.ColChannel.ChGreen);
                }));
            var fSwapN = new RegionCache();
            T(GOriginal, "MoveByte (channel swap) <new>", "Channel swap with the fractal copy placement: the copies partition the viewport and every region shows the sprite with two channels exchanged. The combo in the strip picks the pair for the first copy (R <-> G, R <-> B, R <-> A, G <-> B, G <-> A, B <-> A); the other copies switch channels randomly (re-shuffled whenever the count, the pair or the viewport changes). All swaps are PRECOMPUTED per region - prescaled, precropped, channels already exchanged - so a frame is one plain Draw per copy.",
                c =>
                {
                    var regs = FractalLayout.Regions(c.W, c.H, Math.Max(1, c.Count));
                    fSwapN.Ensure(((long)Math.Max(1, c.Count) << 48) ^ ((long)c.W << 32) ^ (uint)c.H ^ ((long)SwapDemo.Pair << 56) ^ ((long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.A) << 4), regs,
                        i =>
                        {
                            var t = Prescaled(c.A.Color, regs[i]);
                            var p = i == 0 ? SwapDemo.Pairs[SwapDemo.Pair] : SwapDemo.Pairs[new Random(20260925 + i * 7919).Next(SwapDemo.Pairs.Length)];   // stable random per copy, re-shuffled per rebuild
                            var d = ChannelSwapped(t, p[0], p[1]);
                            t.Dispose(); return d;
                        });
                    for (int i = 0; i < regs.Count; i++) c.Canvas.Draw(fSwapN.Sprites[i], regs[i].X, regs[i].Y, SR2D.Op.Paint);
                })
                .Ui(Param.Count, "Copies (fractal partition, random swaps from the 2nd on; capped where the next tile would be too small)")
                .Also(t => { t.ControlStrip = SwapDemo.Build; t.StripHeight = SwapDemo.StripHeight; t.FractalCopies = true; });
            T(GOriginal, "MoveBit (mask -> colour)", "MoveBit(mask, x, y, bit, colour) ORs a colour into the canvas wherever ONE mask bit is set, so it is called once per layer with its own colour: blue circle (bit 0), amber diamond (bit 1), green stripes (bit 2), purple checker (bit 3). Where two layers overlap the colours OR together - that is the operation, not a bug. 'Bits' picks which layers take part; with a single bit you see exactly that layer's shape.",
                c =>
                {
                    int[] layerCol = { 0x2060C0, 0xC0A020, 0x20A060, 0x8040C0 };
                    Grid(c, (x, y) => { for (int b = 0; b < 4; b++) if ((c.MaskBits & (1 << b)) != 0) c.Canvas.MoveBit(c.A.Mask, x, y, 1 << b, layerCol[b]); });
                }, clears: true).Ui(Param.MaskBits, "Mask bits (layers) to move:", Param.Count, "Sprites per side").With(Param.MaskBits, 15);
            T(GOriginal, "ClearBuffer / ClearRect", "Fills the whole canvas then Count*8 random rectangles (ClearRect takes an x/y range, so the rectangles are Count*8 - the Count slider is the work per frame).", c =>
            {
                c.Canvas.ClearBuffer(SR2D.ARGB(255, 20, 30, 50));
                var r = new Random(7);
                for (int k = 0; k < c.Count * 8; k++)
                {
                    int x = r.Next(c.W), y = r.Next(c.H);
                    c.Canvas.ClearRect(x, x + r.Next(c.S), y, y + r.Next(c.S), r.Next() | unchecked((int)0xFF000000));
                }
            }, clears: true);
            T(GOriginal, "DrawLine (dot step / xor)", "Fan of Count*64 clipped lines from the mouse position; DotStep and XOR from the panel. DotStep 0 = a solid line; 1..8 lights one pixel every DotStep+1 steps along the MAJOR axis (the original rasteriser - that is why diagonals look sparser than horizontals; 'DrawLine2' dashes are measured along the line instead), so the slider starts above 0 - at 0 every line is solid and the dot stepping has nothing to show.", c =>
            {
                int n = c.Count * 64;
                for (int i = 0; i < n; i++)
                {
                    float a = i * MathF.PI * 2 / n + c.Time * 0.2f;
                    int col = SR2D.ARGB(255, (byte)(128 + 127 * MathF.Sin(a)), (byte)(128 + 127 * MathF.Sin(a + 2)), (byte)(128 + 127 * MathF.Sin(a + 4)));
                    c.Canvas.DrawLine(c.X, c.Y, (int)(c.X + MathF.Cos(a) * c.W), (int)(c.Y + MathF.Sin(a) * c.H), col, c.DotStep, c.Xor);
                }
            }).Ui(Param.DotStep, "Dot step (0 = solid, 1..8 = dotted)", Param.Xor, "XOR the dots instead of painting them", Param.Count, "Lines / 64", Param.Time, "Animate: the fan turns").With(Param.DotStep, 3);
            T(GOriginal, "DrawRotate (Smooth = AA, right click = pivot)", "Original fixed-point rotation: source pixel (Sx, Sy) = the PIVOT lands on the canvas position and the sprite turns around it; 'Smooth' toggles DRAW_ROT_AA. Angle = slider; with 'Animate' ticked the time is added (continuous spin). With Animate OFF turn it with the mouse: drag anywhere outside the sprite and it points at the cursor, or use the wheel (5 degrees per notch). RIGHT CLICK on the sprite moves the pivot to that source pixel (the marker shows it; the sprite stays where it is); right click outside the sprite resets the pivot to the centre. The pivot cross-hair is an overlay drawn after the timed frame.", c =>
            {
                int px = c.PivotX < 0 ? c.S / 2 : c.PivotX, py = c.PivotY < 0 ? c.S / 2 : c.PivotY;
                for (int k = 0; k < c.Count; k++)                                     // left: yours - angle from the slider / the rotate handles
                    c.Canvas.DrawRotate(c.A.Color, px, py, c.X + k * 40, c.Y + k * 40, c.Angle, c.Smooth);
                SelfSpinner(c, (x, y, a) => c.Canvas.DrawRotate(c.A.Color, c.S / 2, c.S / 2, x, y, a, c.Smooth));
            }).Ui(Param.Count, "Copies (each 40 px further down-right)", Param.Angle, "Angle (deg) - or drag around the edges of the sprite / wheel", Param.Smooth, "Anti-aliased edges (DRAW_ROT_AA)", Param.Time, "Animate: the right-hand sprite spins with time", Param.Mouse, "Drag the sprite; drag around its edges = rotate; right click = pivot");
            L[L.Count - 1].MouseRotates = true; L[L.Count - 1].AngleCCW = true; L[L.Count - 1].AnimSpin = 0f; L[L.Count - 1].HomeX = 0.3f; L[L.Count - 1].HomeY = 0.5f;
            T(GOriginal, "TileDraw", "Tiles the 64x64 tile over the canvas with a scrolling offset (Sx/Sy from time).", c =>
            {
                int ox = (int)(c.Time * 60), oy = (int)(c.Time * 37);
                for (int k = 0; k < c.Count; k++) c.Canvas.TileDraw(c.A.Tile, 0, 0, c.W, c.H, ox, oy, c.Op == SR2D.Op.Paint ? SR2D.Op.Paint : c.Op);
            }, clears: true).Ui(Param.Count, "Repetitions per frame (the same full-canvas tiling Count times - timing only)", Param.Op, "Blend op of the tiles", Param.Time, "Animate: scroll the offset with time");
            T(GOriginal, "MaskInterSector - pixel-exact collision test (drag the PLAYER into the WALL)", "ORIGINAL API (MASK_INTERSECT kernel, unchanged semantics, now SIMD). What it is: a QUERY, not a drawing call. n = world.MaskInterSector(player, x, y, bits) returns how many pixels of 'player' placed at (x, y) overlap a pixel of 'world' where BOTH pixels have at least one of the bits in 'bits' set: count of ((world & player & bits) != 0). n == 0 means no collision; a bigger n means a deeper overlap. It works on MASK sprites: ordinary 32-bit sprites whose pixel VALUES are bit flags, not colours (bit 0 = 'solid', bit 1 = 'water', bit 2 = 'damage zone' ... one image holds every layer). Scene: a static WALL (left) and a PLAYER (drag it) - both built from the bench mask: bit 0 = round body (circle), bit 1 = diamond, bit 2 = diagonal stripes, bit 3 = checker. 'Bits that take part' picks which layers the test looks at; each selected layer is drawn in its own colour on both shapes, layers not selected are shown as thin grey outlines to remind you they are ignored. The pixels the function counted are painted RED, the verdict (HIT / no contact) and the count are printed in big letters. Try: circle only -> the two round bodies must touch; checker only -> collision wherever light squares of both meet; several bits -> ANY selected layer colliding counts (the test is an OR over the selected bits).", c =>
            {
                c.Canvas.ClearBuffer(unchecked((int)0xFF101418));
                int S = c.S, bits = c.MaskBits == 0 ? 1 : c.MaskBits;
                int wx = c.W / 2 - S - S / 2, wy = c.H / 2 - S / 2;                        // the wall: left of centre, fixed
                int px = c.X - S / 2, py = c.Y - S / 2;                                    // the player follows the mouse
                // 1. the WORLD is a separate collision map (a sprite of canvas size holding only bit values: 0 = nothing,
                //    the wall's mask pixels where the wall is) - NOT the picture. Querying the display canvas would be wrong:
                //    its background colour 0x101418 has bit 3 set, so 'checker' would collide with empty space everywhere.
                Sprite world = MaskWorld(c.W, c.H);
                world.ClearBuffer(0);
                world.Draw(c.A.Mask, wx, wy, SR2D.Op.Paint);
                // 2. the call under test (timed: Count repetitions)
                int n = 0;
                for (int k = 0; k < c.Count; k++) n = world.MaskInterSector(c.A.Mask, px, py, bits);
                c.LastInt = n;
                // 3. visualisation (not part of the original function): layer colours, ignored layers as grey dots
                c.Canvas.Draw(c.A.Mask, wx, wy, SR2D.Op.Paint);                              // raw bit values (0..15 = nearly black) as the base of the wall
                int[] layerCol = { 0x2060C0, 0xC0A020, 0x20A060, 0x8040C0 };               // circle, diamond, stripes, checker
                string[] layerName = { "circle (bit 0)", "diamond (bit 1)", "stripes (bit 2)", "checker (bit 3)" };
                for (int b = 0; b < 4; b++)
                    if ((bits & (1 << b)) != 0) c.Canvas.MoveBit(c.A.Mask, wx, wy, 1 << b, layerCol[b] & 0x707070);   // wall: dim layer colour
                // ignored layers: grey dots on the wall only (the player shows only what counts)
                for (int b = 0; b < 4; b++)
                    if ((bits & (1 << b)) == 0) c.Canvas.MoveBit(c.A.Mask, wx, wy, 1 << b, 0x1C1C1C);
                // the player is drawn OVER the wall: MoveBit ORs its colour into whatever is there, so where the two overlap
                // the wall's dim colour would shine through (it looked like a translucent player). Punch the player's footprint
                // back to the background first (MaskClearBuffer = dest := colour where the mask has one of the bits), then OR.
                c.Canvas.MaskClearBuffer(c.A.Mask, px, py, unchecked((int)0xFF101418), bits);
                for (int b = 0; b < 4; b++)
                    if ((bits & (1 << b)) != 0) c.Canvas.MoveBit(c.A.Mask, px, py, 1 << b, layerCol[b]);               // player: bright, opaque
                // 4. the counted pixels in red: AND of the two copies (in a scratch), then the selected bits painted
                if (tand == null || tand.Width != S) { tand?.Dispose(); tand = new Sprite(S, S); }
                var a = c.A.Mask.Pixels; var t = tand.Pixels;
                for (int y = 0; y < S; y++) for (int x = 0; x < S; x++) { int ox = x + px - wx, oy = y + py - wy; int o = (uint)ox < (uint)S && (uint)oy < (uint)S ? a[oy * S + ox] : 0; t[y * S + x] = a[y * S + x] & o; }
                c.Canvas.ClearAlpha();                                                      // raw bit values have alpha 0: make the canvas opaque again
                c.Canvas.MaskClearBuffer(tand, px, py, unchecked((int)0xFFFF2020), bits);  // solid red where the selected bits of (world & player) are set
                // 5. frames + captions
                c.Canvas.DrawRect(wx - 1, wy - 1, S + 2, S + 2, unchecked((int)0xFF505860));
                c.Canvas.DrawRect(px - 1, py - 1, S + 2, S + 2, unchecked((int)(n > 0 ? 0xFFFF4040 : 0xFF40C060)));
                c.Canvas.DrawText(wx, wy - 3, "WALL (static)", unchecked((int)0xFFC0C8D0), 0, TextAnchor.BottomLeft);
                c.Canvas.DrawText(px, py - 3, "PLAYER (drag me)", unchecked((int)(n > 0 ? 0xFFFF8080 : 0xFF80E0A0)), 0, TextAnchor.BottomLeft);
                string verdict = n > 0 ? $"HIT: {n} px overlap" : "no contact (0)";
                c.Canvas.DrawText(c.W / 2, 8, verdict, unchecked((int)(n > 0 ? 0xFFFF5050 : 0xFF60E080)), unchecked((int)0xFF000000), TextAnchor.TopCenter, 3);
                string layers = ""; for (int b = 0; b < 4; b++) if ((bits & (1 << b)) != 0) layers += (layers == "" ? "" : " OR ") + layerName[b];
                c.Canvas.DrawText(c.W / 2, 44, $"world.MaskInterSector(player, x, y, bits = 0x{bits:X}) counts pixels where BOTH have {layers}", unchecked((int)0xFFC0C8D0), unchecked((int)0xFF000000), TextAnchor.TopCenter);
                c.Canvas.DrawText(c.W / 2, 58, "red = the pixels it counted   |   colours = the layers being tested   |   grey dots on the wall = layers switched off (ignored)", unchecked((int)0xFF8890A0), unchecked((int)0xFF000000), TextAnchor.TopCenter);
                c.Note = $"overlap = {n} px (bits 0x{bits:X}: {layers})";
            }, clears: true).Ui(Param.Mouse, "Drag the player", Param.MaskBits, "Bits (layers) that take part in the test:", Param.Count, "Repetitions of the query (timing only)").With(Param.MaskBits, 1);
            T(GOriginal, "ClearAlpha (forces alpha = 255) + managed SetPixel/GetPixel plasma", "Two things: managed per-pixel access (a plasma painted with SetPixel / GetPixel over a Count*64x64 area of the canvas), and ClearAlpha. Despite the name it does NOT zero the alpha - it ORs 0xFF000000 into every pixel of the sprite, i.e. it makes the whole sprite opaque (not clipped by the lock rect, rgb untouched). The pair on the checker shows both sides: a scratch whose only opaque part is one sprite-sized patch (the rest is ARGB 0,0,0,0), drawn over the light/dark checker BEFORE (alpha 0 there, so AlphaBlend contributes nothing and the checker stays) and AFTER ClearAlpha (every pixel opaque, so those same pixels become solid black).", c =>
            {
                int n = 64 * (int)Math.Sqrt(c.Count);
                Checker(c, 0, 0);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float v = MathF.Sin(x * 0.1f + c.Time) + MathF.Sin(y * 0.13f - c.Time) + MathF.Sin((x + y) * 0.05f);
                        int g = (int)(128 + 60 * v);
                        c.Canvas.SetPixel(c.X + x, c.Y + y, SR2D.ARGB(255, (byte)g, (byte)(255 - g), (byte)(g / 2)) ^ (c.Canvas.GetPixel(c.X + x, c.Y + y) & 0x0F0F0F));
                    }
                Sprite t = c.Temp;
                t.ClearBuffer(0);                                  // the scratch is shared with other tests: start clean
                t.Draw(c.A.Color, 0, 0, SR2D.Op.Paint);            // one opaque sprite-sized patch; the rest stays ARGB 0,0,0,0
                int gap = 16, ty = (c.H - t.Height) / 2;
                int x0 = (c.W - 2 * t.Width - gap) / 2, x1 = x0 + t.Width + gap;
                c.Canvas.Draw(t, x0, ty, SR2D.Op.AlphaBlend);      // alpha 0 in the three empty quadrants: AlphaBlend adds nothing, the checker stays
                t.ClearAlpha();                                    // alpha |= 0xFF everywhere, rgb untouched: those empty pixels are now opaque BLACK
                c.Canvas.Draw(t, x1, ty, SR2D.Op.AlphaBlend);
                c.Label(x0, ty - 16, "before: alpha 0 where the scratch is empty");
                c.Label(x1, ty - 16, "after ClearAlpha: every pixel opaque");
            }).Ui(Param.Count, "Plasma area (x 64*64 px)", Param.Mouse, "Drag the plasma patch", Param.Time, "Animate: the plasma moves");

            // ================================================= Original - masked
            T(GMask, "MaskDraw (Op selector)", "Sprite.MaskDraw through the selected mask bit(s); NotMask inverts.",
                c => Grid(c, (x, y) => c.Canvas.MaskDraw(SrcFor(c, c.Op), c.A.Mask, x, y, x, y, c.MaskBits, c.NotMask, c.Op)));
            T(GMask, "MaskDraw - moving mask", "The canvas is tiled with the dimmed colour sprite (untimed backdrop), then Count sprites are drawn through the MASK, which is dragged with the mouse: MaskDraw takes separate positions for the sprite (Sx, Sy) and the mask (MaskX, MaskY) and draws their intersection (3-way clipping: canvas, sprite, mask). Where the mask is, the full-brightness sprite / alpha / keyed sprite shows through its shape.",
                c =>
                {
                    // backdrop: the colour sprite at half brightness everywhere - not part of what the test measures, but
                    // without it the moving mask is invisible (sprite drawn on sprite).
                    // S2X convention: mul 128 = x1.00, add 128 = +0, so half brightness is mul 64 with the NEUTRAL add
                    // (an add of 0 is -256 per byte: the whole backdrop would collapse to black).
                    int dim = SR2D.ARGB(128, 64, 64, 64), neutral = SR2D.ARGB(128, 128, 128, 128);
                    for (int y = 0; y < c.H; y += c.S) for (int x = 0; x < c.W; x += c.S) c.Canvas.MulAddS2X(c.A.Color, x, y, dim, neutral);
                    FixedGrid(c, (x, y) => c.Canvas.MaskDraw(SrcFor(c, c.Op), c.A.Mask, x, y, c.X - c.S / 2, c.Y - c.S / 2, c.MaskBits, c.NotMask, c.Op));
                }).Ui(Param.Mouse, "Drag the mask", Param.Count, "Sprites the mask is tested against (grid from the top-left)", Param.MaskBits, "Mask shape", Param.NotMask, "Invert the mask", Param.Op, "Blend op (AlphaBlend / AlphaTest pick the alpha / keyed sprite)").With(Param.Count, 16);
            T(GMask, "MaskBlend", "Masked crossfade by BlendFactor.",
                c => Grid(c, (x, y) => c.Canvas.MaskBlend(c.A.Color, c.A.Mask, x, y, x, y, c.Blend, c.MaskBits, c.NotMask)));
            T(GMask, "MaskClearBuffer", "Fills the masked area with a colour.",
                c => Grid(c, (x, y) => c.Canvas.MaskClearBuffer(c.A.Mask, x, y, SR2D.ARGB(255, 200, 120, 30), c.MaskBits, c.NotMask)));
            T(GMask, "MaskMulAddS2X", "Masked multiply/add (Brightness = Mul x128, Blend = Add). The neutral point of both sliders (Mul x1.00 / Add 128) would make this a plain MaskDraw, so the start values are off-neutral.", c =>
            {
                int m = (int)Math.Clamp(c.Brite * 128, 0, 255); int mul = SR2D.ARGB((byte)m, (byte)m, (byte)m, (byte)m);
                int add = SR2D.ARGB((byte)c.Blend, (byte)c.Blend, (byte)c.Blend, (byte)c.Blend);
                Grid(c, (x, y) => c.Canvas.MaskMulAddS2X(c.A.Color, c.A.Mask, x, y, x, y, mul, add, c.MaskBits, c.NotMask));
            }).With(Param.Brite, 55).With(Param.Blend, 175);
            T(GMask, "MaskMoveByte / MaskMoveBit", "Masked channel copy (red->green) and bit copy.", c => Grid(c, (x, y) =>
            {
                c.Canvas.MaskMoveByte(c.A.Color, c.A.Mask, x, y, x, y, SR2D.ColChannel.ChRed, SR2D.ColChannel.ChGreen, c.MaskBits, c.NotMask);
                c.Canvas.MaskMoveBit(c.A.Mask, c.A.Mask, x, y, x, y, Assets.MaskStripes, 0x0000C0, c.MaskBits, c.NotMask);
            }));

            // ================================================ Original - bump
            // Bump tests: sprites are stationary (grid from the top-left); the MOUSE is the light.
            // Directional light = direction from the canvas centre to the mouse; point light = mouse position.
            var bumpUi = new object[] { Param.Mouse, "Light follows the mouse (untick: drag the light, right click = jump)", Param.Z, "Light height Z (px)", Param.Brite, "Brightness (x0.01)", Param.Count, "Sprites drawn (grid, centred on the canvas)" };
            T(GBump, "DrawDPBM directional", "Sprites in a grid centred on the canvas; light direction = vector from the canvas centre to the light (the mouse), height Z, Brightness slider (the kernel clamps it to 0..1, so the slider is capped at 100 - above that nothing gets brighter). A faint frame marks the light.",
                c => CentredGrid(c, (x, y) => c.Canvas.DrawDPBM(c.A.Normal, x, y, c.X - c.W / 2, c.Y - c.H / 2, c.Z, c.Brite, false))).Ui(bumpUi).With(Param.Mouse, true).Range(Param.Brite, 0, 100);
            T(GBump, "DrawDPBM point light", "Sprites centred; point light at the mouse (DPBM_POINT), height Z, Brightness slider (clamped to 0..1 by the kernel).",
                c => CentredGrid(c, (x, y) => c.Canvas.DrawDPBM(c.A.Normal, x, y, c.X, c.Y, c.Z, c.Brite, true))).Ui(bumpUi).With(Param.Mouse, true).Range(Param.Brite, 0, 100);
            T(GBump, "DrawDPBM + Mul2X colour", "Point-light bump then the colour sprite multiplied on top - the classic 2-pass look. Light at the mouse. Brightness is clamped to 0..1.", c => CentredGrid(c, (x, y) =>
            {
                c.Canvas.DrawDPBM(c.A.Normal, x, y, c.X, c.Y, c.Z, c.Brite, true);
                c.Canvas.Draw(c.A.Color, x, y, SR2D.Op.Mul2X);
            })).Ui(bumpUi).With(Param.Mouse, true).Range(Param.Brite, 0, 100);
            T(GBump, "MaskDrawDPBM (dir / point = Smooth)", "Masked bump mapping, sprites centred, light at the mouse; 'Smooth' switches to the point-light variant. Brightness is clamped to 0..1.",
                c => CentredGrid(c, (x, y) => c.Canvas.MaskDrawDPBM(c.A.Normal, c.A.Mask, x, y, x, y, c.MaskBits, c.Smooth ? c.X : c.X - c.W / 2, c.Smooth ? c.Y : c.Y - c.H / 2, c.Z, c.Brite, c.NotMask, c.Smooth)))
                .Ui(bumpUi).Ui(Param.Smooth, "Point light (else directional)", Param.MaskBits, "Mask bits:", Param.NotMask, "NotMask (invert the mask)").With(Param.Mouse, true).Range(Param.Brite, 0, 100);
            T(GBump, "DrawEBM (environment map)", "Environment-mapped bump ('fake chrome'): for every pixel the normal's (x, y) is used as a texture coordinate into the ENVIRONMENT image (sky gradient, horizon, sun) - flat areas show the centre of the environment, slopes show sky or ground. The function itself has no light position, so to make it interactive the bench moves the environment: the mouse shifts the environment image (a 256x256 TileDraw into a scratch sprite, ~free), which is what 'the sun moving around a chrome object' looks like; with Animate the sun also orbits with time. The Brightness slider scales the environment (MulAddS2X on the scratch). 'Smooth' = DestSpace variant: the lookup is additionally offset by the canvas position, so the reflection sweeps across the sprite grid instead of repeating per sprite.",
                c => { var env = ShiftedEnv(c); CentredGrid(c, (x, y) => c.Canvas.DrawEBM(c.A.Normal, env, x, y, c.Smooth)); })
                .Ui(Param.Mouse, "Environment (sun position) follows the mouse", Param.Brite, "Environment brightness (x0.01: 100 = unchanged, 0 = black, 200 = x2)", Param.Smooth, "DestSpace lookup (reflection sweeps across the grid)", Param.Count, "Sprites drawn (grid, centred)", Param.Time, "Animate: the sun orbits").With(Param.Mouse, true).Range(Param.Brite, 0, 200);
            T(GBump, "DrawEBM + Mul2X colour", "Same lookup, then the colour sprite multiplied on top: metallic bricks. Mouse = environment / sun position, Brightness = environment intensity.",
                c => { var env = ShiftedEnv(c); CentredGrid(c, (x, y) => { c.Canvas.DrawEBM(c.A.Normal, env, x, y, c.Smooth); c.Canvas.Draw(c.A.Color, x, y, SR2D.Op.Mul2X); }); })
                .Ui(Param.Mouse, "Environment (sun position) follows the mouse", Param.Brite, "Environment brightness (x0.01)", Param.Smooth, "DestSpace lookup", Param.Count, "Sprites drawn (grid, centred)", Param.Time, "Animate: the sun orbits").With(Param.Mouse, true);

            // ============================================= Original - transforms
            T(GXform, "new Sprite(src, Transform)  [Op box = the transform]", "Constructs a transformed copy every frame (allocation + the selected transform) and draws it centred on the canvas (the original, untransformed sprite is shown small in the corner for comparison). The Op box picks the transform: FlipX, FlipY, FlipXY, RotCW, RotCCW, FlipX+RotCCW, FlipY+RotCCW (the RESIZE / RESIZE+ROT tests below take the Scale slider on top).", c =>
            {
                var tr = c.Op switch
                {
                    SR2D.Op.AlphaTest => SR2D.Transform.FlipY, SR2D.Op.AlphaBlend => SR2D.Transform.FlipXY, SR2D.Op.Add2D => SR2D.Transform.RotCW,
                    SR2D.Op.Add => SR2D.Transform.RotCCW, SR2D.Op.Mul => SR2D.Transform.FlipXRotCCW, SR2D.Op.Mul2X => SR2D.Transform.FlipYRotCCW, _ => SR2D.Transform.FlipX
                };
                for (int k = 0; k < c.Count; k++)
                {
                    using var s = new Sprite(c.A.Color, tr);
                    c.Canvas.Draw(s, (c.W - s.Width) / 2 + k * 16, (c.H - s.Height) / 2 + k * 16, SR2D.Op.Paint);
                }
                if (Caps.HasWarp) { c.Canvas.DrawScaled(c.A.Color, 8, 8, c.S / 2, c.S / 2, SR2D.Op.Paint, SR2D.Filter.Area); c.Label(8, c.S / 2 + 10, "original"); }
                c.Note = "new Sprite(src, " + tr + ")";
            }).Ui(Param.Count, "Copies per frame (each 16 px further down-right)", Param.Op, "The Transform passed to the constructor").Ops("FlipX", "FlipY", "FlipXY", "RotCW", "RotCCW", "FlipX + RotCCW", "FlipY + RotCCW");
            T(GXform, "new Sprite(src, None, W*Scale, H*Scale)  [RESIZE]", "Area-average / bilinear resize by the Scale slider, every frame; the result is drawn centred on the canvas (each further copy 16 px down-right).", c =>
            {
                int w = Math.Max(2, (int)(c.S * c.Scale)), h = Math.Max(2, (int)(c.S * c.Scale));
                for (int k = 0; k < c.Count; k++)
                {
                    using var s = new Sprite(c.A.Color, SR2D.Transform.None, w, h);
                    c.Canvas.Draw(s, (c.W - s.Width) / 2 + k * 16, (c.H - s.Height) / 2 + k * 16, SR2D.Op.Paint);
                }
            }).Ui(Param.Scale, "Scale (x0.05 .. x4)", Param.Count, "Copies per frame").With(Param.Scale, 150);
            T(GXform, "new Sprite(src, RotCW, W*Scale, H*0.6*Scale)  [RESIZE+ROT]", "Resize to W x 0.6 W and rotate 90 degrees clockwise in one constructor (temporary buffer path); the result is drawn centred on the canvas.", c =>
            {
                int w = Math.Max(2, (int)(c.S * c.Scale)), h = Math.Max(2, (int)(c.S * c.Scale * 0.6f));
                for (int k = 0; k < c.Count; k++)
                {
                    using var s = new Sprite(c.A.Color, SR2D.Transform.RotCW, w, h);
                    c.Canvas.Draw(s, (c.W - s.Width) / 2 + k * 16, (c.H - s.Height) / 2 + k * 16, SR2D.Op.Paint);
                }
            }).Ui(Param.Scale, "Scale (x0.05 .. x4)", Param.Count, "Copies per frame");
            T(GXform, "ToBitmap round-trip", "Sprite -> GDI+ Bitmap -> Sprite (LockBits copy both ways), Count times.", c =>
            {
                for (int k = 0; k < c.Count; k++)
                {
                    using Bitmap b = c.A.Color.ToBitmap;
                    using var s = new Sprite(b);
                    c.Canvas.Draw(s, (c.W - s.Width) / 2 + k * 8, (c.H - s.Height) / 2 + k * 8, SR2D.Op.Paint);
                }
            }).Ui(Param.Count, "Round trips per frame");
            T(GEdit, "In-place editors (Sprite.Edit.cs): Flip / Rotate90 / Rotate(deg) / Shift / Trim / Resize / colour", "The sprite edits ITSELF - no second sprite, no Transform constructor: a working copy of the alpha sprite is taken once per frame, then Count edits from the list below run on it in turn (the Op box picks the list) and the result is drawn centred. Flips = flips and lossless quarter turns (FlipX, FlipY, Rotate180, RotateCW / CCW - the size swaps and swaps back); Rotate = Rotate(Angle) any angle in place (Bicubic; 'Smooth' off = Nearest), Grow when 'NotMask' is ticked (the sprite gets bigger instead of cutting corners); Scroll / Shift = Scroll(wrap) by the mouse offset then Shift with a fill; Expand / Trim = Expand(Scale*16, colour) then Trim back to the content; Scale / Resize = Scale, then Resize back (two resamples); Colour: Invert / Grayscale / RotateHue(Angle) / AdjustColor(Brite) / Fade(Blend); Apply = Apply(Effects blur+shadow) in place; Region = a region edit: SetLockRect on the middle, FlipX + Rotate(Angle) inside it only; Selection = Selection.Rotate90 + FlipX driving a Fill.",
                c =>
                {
                    using var s = new Sprite(c.A.Alpha);
                    int n = Math.Max(1, c.Count); float deg = c.Angle * 180f / MathF.PI;
                    var f = c.Smooth ? SR2D.Filter.Bicubic : SR2D.Filter.Nearest;
                    switch (c.Op)
                    {
                        case SR2D.Op.Paint: for (int k = 0; k < n; k++) switch (k % 5) { case 0: s.FlipX(); break; case 1: s.FlipY(); break; case 2: s.Rotate180(); break; case 3: s.RotateCW(); break; default: s.RotateCCW(); break; } c.Note = "flips + quarter turns, lossless"; break;
                        case SR2D.Op.AlphaTest: for (int k = 0; k < n; k++) s.Rotate(deg / n, f, Grow: c.NotMask); c.Note = $"Rotate({deg / n:0.#}) x {n}" + (c.NotMask ? " with Grow" : ""); break;
                        case SR2D.Op.AlphaBlend: s.Scroll(c.X - c.W / 2, c.Y - c.H / 2); s.Shift(8, 8, false, unchecked((int)0x60FF0000)); c.Note = "Scroll(mouse offset) + Shift(8, 8, fill)"; break;
                        case SR2D.Op.Add2D: { int m = (int)(c.Scale * 16); s.Expand(m, unchecked((int)0x400000FF)); var b = s.ContentBounds(64); s.Trim(0, 64); c.Note = $"Expand({m}) -> {b} -> Trim"; break; }
                        case SR2D.Op.Add: { int w0 = s.Width; for (int k = 0; k < n; k++) { s.Scale(Math.Max(0.05f, c.Scale), f); s.Resize(w0, w0, f); } c.Note = $"Scale({c.Scale:0.##}) + Resize back x {n}"; break; }
                        case SR2D.Op.Mul: s.Invert().Invert().Grayscale().RotateHue(deg).AdjustColor(Brightness: c.Brite * 0.25f).Fade(c.Blend / 255f); c.Note = "Invert x2, Grayscale, RotateHue, AdjustColor, Fade"; break;
                        case SR2D.Op.Mul2X: s.Expand(24); s.Apply(new Effects().Blur(Math.Max(0, (int)(c.Scale * 4))).Shadow(6, 6, 6, unchecked((int)0xFF000000), 0.7f)); c.Note = "Expand(24) + Apply(Blur + Shadow)"; break;
                        case SR2D.Op.Max: s.SetLockRect(s.Width / 4, s.Width * 3 / 4, s.Height / 4, s.Height * 3 / 4); s.FlipX(); s.Rotate(deg, f); s.SetLockRect(); c.Note = "lock rect = middle quarter: FlipX + Rotate inside only"; break;
                        default: { using var sel = new Selection(s); sel.Ellipse(s.Width * 0.3f, s.Height * 0.5f, s.Width * 0.2f, s.Height * 0.45f); sel.Rotate90(c.Count).FlipX(); s.Fill(sel, unchecked((int)0xA0FF4000), SR2D.LineOp.AlphaBlend); c.Note = $"Selection.Rotate90({c.Count}).FlipX() + Fill"; break; }
                    }
                    Checker(c, 0, 0);
                    c.Canvas.Draw(s, (c.W - s.Width) / 2, (c.H - s.Height) / 2, SR2D.Op.AlphaBlend);
                    c.Label(4, 4, $"{s.Width} x {s.Height}");
                }, warp: true, needsFx: true, needsFlood: true, clears: true).Ui(Param.Op, "Edit list", Param.Count, "Edits per frame", Param.Angle, "Rotate angle / hue", Param.Scale, "Scale / margin / blur", Param.Smooth, "Bicubic (off = Nearest)", Param.NotMask, "Rotate: Grow", Param.Blend, "Fade", Param.Brite, "Brightness", Param.Mouse, "Scroll offset (Scroll / Shift list)").Ops("Flips + quarter turns", "Rotate(degrees)", "Scroll / Shift", "Expand / Trim", "Scale / Resize", "Colour (Invert, Grayscale, Hue, Adjust, Fade)", "Apply(Effects)", "Region edit (lock rect)", "Selection.Rotate90 / FlipX").With(Param.Op, 1).With(Param.Count, 1).With(Param.Angle, 35);   // the list opens on Rotate: at the generic Angle 0 it would be Rotate(0 deg) = nothing happens

            // ======================================================= Scenes
            T(GScene, "Scene A: 4x DPBM point + 4x Mul2X (user's PointLite branch)", "Exactly the user's first Render() block: four point-light bumps on a 2x2 grid of S x S quadrants (S = the sprite size in the strip) plus the colour sprite multiplied over each one. Count = the WHOLE block repeated per frame (timing) - it is not a copy placement, and because Mul2X is inside the loop every extra pass multiplies the colour in again, so raising Count darkens the scene.", c =>
            {
                for (int k = 0; k < c.Count; k++)
                {
                    c.Canvas.DrawDPBM(c.A.Normal, 0, 0, c.X, c.Y, c.Z, 1, true);
                    c.Canvas.DrawDPBM(c.A.Normal, c.S, 0, c.X, c.Y, c.Z, 1, true);
                    c.Canvas.DrawDPBM(c.A.Normal, 0, c.S, c.X, c.Y, c.Z, 1, true);
                    c.Canvas.DrawDPBM(c.A.Normal, c.S, c.S, c.X, c.Y, c.Z, 1, true);
                    c.Canvas.Draw(c.A.Color, 0, 0, SR2D.Op.Mul2X);
                    c.Canvas.Draw(c.A.Color, c.S, 0, SR2D.Op.Mul2X);
                    c.Canvas.Draw(c.A.Color, 0, c.S, SR2D.Op.Mul2X);
                    c.Canvas.Draw(c.A.Color, c.S, c.S, SR2D.Op.Mul2X);
                }
            }, clears: true).Ui(Param.Mouse, "Point light (the mouse)", Param.Z, "Light height Z (px)", Param.Count, "Whole block repeats per frame (each one multiplies again - timing)");
            T(GScene, "Scene B: DPBM dir + Mul + TileDraw (user's else branch)", "Exactly the user's second Render() block: the directional bump into a scratch the size of one sprite, the colour sprite multiplied into it, then that cell tiled over the whole canvas. The cell is its own S x S sprite, NOT a CreateView of the 2S x 2S Temp: a view is only a WRITE clip - Draw / TileDraw read the source's full meWidth x meHeight extent, so tiling a view tiles the whole scratch. And the bump kernel writes d = 0x10101 * dot(normal, light), i.e. it leaves the ALPHA byte at 0, so the tiled Paint blit would clear the canvas to transparent - ClearAlpha() (alpha |= 0xFF, see that test) makes the cell opaque again. Count = the whole block repeated per frame (timing only - the cell is rebuilt every pass).", c =>
            {
                if (tcell == null || tcell.Width != c.S) { tcell?.Dispose(); tcell = new Sprite(c.S, c.S); }
                for (int k = 0; k < c.Count; k++)
                {
                    tcell.DrawDPBM(c.A.Normal, 0, 0, c.X - c.S, c.Y - c.S, c.Z, 1);
                    tcell.Draw(c.A.Color, 0, 0, SR2D.Op.Mul);
                    tcell.ClearAlpha();                                                 // the bump left alpha 0: without this the tile below paints the whole canvas transparent
                    c.Canvas.TileDraw(tcell, 0, 0, c.W, c.H);
                }
            }, clears: true).Ui(Param.Mouse, "Light direction (the mouse, around the cell centre)", Param.Z, "Light height Z (px)", Param.Count, "Whole block repeats per frame (each one multiplies again - timing)");
            T(GScene, "Scene C: particles (AlphaBlend x Count*50)", "Count*50 alpha-blended glow sprites, moving - a typical particle workload.", c =>
            {
                int n = c.Count * 50;
                var r = new Random(99);
                for (int i = 0; i < n; i++)
                {
                    float ph = (float)r.NextDouble() * 6.28f, sp = 0.3f + (float)r.NextDouble();
                    int x = (int)(c.W / 2 + MathF.Cos(ph + c.Time * sp) * c.W * 0.45f * (float)r.NextDouble()) - c.S / 8;
                    int y = (int)(c.H / 2 + MathF.Sin(ph * 1.3f + c.Time * sp) * c.H * 0.45f) - c.S / 8;
                    c.Canvas.Draw(c.A.Alpha, x, y, SR2D.Op.AlphaBlend);
                }
            });

            // ======================================================= New API
            var t = T(GNew, "DrawScaled (Scale slider or drag the edges, right click = pivot, Smooth = bilinear)", "Free non-uniform scaling with the pivot overload: DrawScaled(src, x, y, scaleX, scaleY, pivotX, pivotY) puts the source pixel (pivotX, pivotY) at (x, y) and scales around it. Drag the sprite to move it; drag one of its EDGES or CORNERS (the overlay shows handles) to stretch it - left/right edges change the width (= the Scale slider), top/bottom the height, a corner both; hold Shift while dragging a side to keep it square. The mouse wheel scales uniformly. RIGHT CLICK on the sprite makes that source pixel the pivot (the cross): scaling then grows away from it, e.g. a corner pivot keeps that corner fixed; right click outside resets it to the centre. Negative scale flips around the pivot. Op = blend op (alpha sprite for AlphaBlend / AlphaTest), Blend = factor for Op.Blend.", c =>
            {
                if (c.NotMask)                       // benchmark mode: full-canvas blit (the old "DrawScaled - fill canvas" test)
                {
                    for (int k = 0; k < c.Count; k++) c.Canvas.DrawScaled(SrcFor(c, c.Op), 0, 0, c.W, c.H, c.Op, Filt(c), c.Blend);
                    c.Note = $"fill canvas {c.W} x {c.H} px (NotMask), {c.Count} per frame";
                    return;
                }
                int w = (int)(c.S * c.Scale), h = (int)(c.S * c.ScaleY);
                for (int k = 0; k < c.Count; k++)
                    c.Canvas.DrawScaled(SrcFor(c, c.Op), c.X + k * 10, c.Y + k * 10, c.Scale, c.ScaleY, c.PivotXF, c.PivotYF, c.Op, Filt(c), c.Blend);
                c.Note = $"{w} x {h} px, pivot {(c.PivotXF < 0 ? "centre" : $"{c.PivotXF:0},{c.PivotYF:0}")}";
            }, warp: true).Ui(Param.Scale, "Width scale (x0.01) - height by dragging the top / bottom edge", Param.Smooth, "Bilinear (else nearest)", Param.Op, "Blend op", Param.Blend, "Blend factor (Op.Blend)", Param.Count, "Copies per frame", Param.NotMask, "Benchmark: stretch the sprite over the whole canvas", Param.Mouse, "Drag = move, edges = resize, right click = pivot").With(Param.Scale, 150);
            t.Resizable = true; t.PivotByClick = true;
            T(GNew, "Downscale filters: Nearest | Bilinear | Area | BilinearArea", "A 2048x2048 test card (1-px grid, dense 1-px lines, 1-px checker) drawn 4 times side by side at Scale/4 of the canvas height. Nearest and Bilinear drop lines as soon as the factor exceeds 2x; the Area filters keep everything (they average the source by the integer shrink factor first).", c =>
            {
                Sprite s = Card();
                int size = (int)(c.H * c.Scale * 0.25f); if (size < 8) size = 8;
                int gap = 8, x = (c.W - 4 * size - 3 * gap) / 2, y = (c.H - size) / 2;
                SR2D.Filter[] f = { SR2D.Filter.Nearest, SR2D.Filter.Bilinear, SR2D.Filter.Area, SR2D.Filter.BilinearArea };
                for (int i = 0; i < 4; i++) { for (int k = 0; k < c.Count; k++) c.Canvas.DrawScaled(s, x + i * (size + gap), y, size, size, SR2D.Op.Paint, f[i]); c.Label(x + i * (size + gap) + 2, y + 2, f[i].ToString()); }
                c.Note = $"2048 -> {size} px = 1:{2048f / size:0.0}";
            }, warp: true).Ui(Param.Scale, "Output size: canvas height x Scale / 4 (x0.01)", Param.Count, "Repetitions (timing only)");
            T(GNew, "Upscale filters: Nearest | Bilinear | Bicubic (x8 crop, drag to scroll)", "A 64x64 crop of the colour sprite scaled x8 three ways side by side (Scale slider multiplies). Bilinear shows the tent-filter softness and diamond artefacts, Bicubic keeps edges crisper. DRAG anywhere with the left button to scroll the three results together (they are drawn through a clipped view of the canvas, so only the visible part costs). Bottom-left: the source crop enlarged x2 with plain pixel replication, and a frame marking the part of the crop that is on screen.", c =>
            {
                Sprite s = c.A.Color;
                int crop = 64; if (crop > s.Width) crop = s.Width;
                using Sprite v = s.Clone(new Rectangle((s.Width - crop) / 2, (s.Height - crop) / 2, crop, crop));   // Clone = a real crop; a CreateView would still blit the whole sprite
                int size = (int)(crop * 8 * c.Scale); if (size < 8) size = 8;
                int gap = 8, colW = (c.W - 2 * gap) / 3, top = 96, thumb = 128, panelH = c.H - top - thumb - 40;
                // scroll: the mouse position is an offset from the canvas centre; the results follow it (clamped so
                // that something always stays on screen) - the same offset for all three, so they stay comparable
                int sx = Math.Clamp(c.X - c.W / 2, Math.Min(0, colW - size), Math.Max(0, colW - size));
                int sy = Math.Clamp(c.Y - c.H / 2, Math.Min(0, panelH - size), Math.Max(0, panelH - size));
                int ox = size <= colW ? (colW - size) / 2 : sx, oy = size <= panelH ? (panelH - size) / 2 : sy;
                SR2D.Filter[] f = { SR2D.Filter.Nearest, SR2D.Filter.Bilinear, SR2D.Filter.Bicubic };
                for (int i = 0; i < 3; i++)
                {
                    int px = i * (colW + gap);
                    using var panel = c.Canvas.CreateView(new Rectangle(px, top, colW, panelH));     // clip: the scaled image may be far larger than the column
                    for (int k = 0; k < c.Count; k++) panel.DrawScaled(v, ox, oy, size, size, SR2D.Op.Paint, f[i]);
                    c.Canvas.DrawText(px + colW / 2, top - 22, f[i].ToString(), unchecked((int)0xFFFFFFFF), unchecked((int)0xFF000000), TextAnchor.TopCenter, 2);   // below the info panel
                    c.Canvas.DrawRect(px - 1, top - 1, colW + 2, panelH + 2, unchecked((int)0xFF404850));
                }
                // the source crop, bottom-left, enlarged x2 by pixel replication (so its pixels are visible but honest), frame = part shown in a column
                int tx = 8, ty = c.H - thumb - 8;
                c.Canvas.DrawText(tx, ty - 4, $"source crop {crop}x{crop} px (shown x2)  ->  {size}x{size} px in each column", unchecked((int)0xFFC0C8D0), unchecked((int)0xFF000000), TextAnchor.BottomLeft);
                c.Canvas.FillRect(tx - 2, ty - 2, thumb + 4, thumb + 4, unchecked((int)0xFF000000));
                c.Canvas.DrawScaled(v, tx, ty, thumb, thumb, SR2D.Op.Paint, SR2D.Filter.Nearest);
                float k2 = (float)thumb / size;
                c.Canvas.DrawRect(tx + (-ox) * k2, ty + (-oy) * k2, Math.Min(colW, size) * k2, Math.Min(panelH, size) * k2, unchecked((int)0xFFFFE040));
                c.Note = $"64 -> {size} px = x{size / 64f:0.0}{(size > colW || size > panelH ? " - drag to scroll" : "")}";
            }, warp: true).Ui(Param.Scale, "Enlargement: x8 x Scale (x0.01)", Param.Count, "Repetitions (timing only)", Param.Mouse, "Drag anywhere to scroll the enlarged images (when they do not fit)").With(Param.Scale, 150).Also(t => t.DragAnywhere = true);
            T(GNew, "Filter.Auto: Nearest at 1:1, Bicubic up / rotated, BicubicArea down (Scale, Angle)", "Filter.Auto picks the filter from the transform: ~1:1 and unrotated -> Nearest (an exact copy, no resampling cost); enlarged or rotated -> Bicubic; shrunk to half or less -> BicubicArea (box-average first, so 1-px details survive); anything else -> Bilinear. LEFT: the 2048x2048 TEST CARD (a grey field with a white 64-px grid, a block of dense 1-px lines bottom-right, a 1-px yellow checker top-right and red dots - it is the same card the 'Downscale filters' test uses) drawn at Scale/4 of the canvas height, i.e. shrunk about 1:10 at Scale 1 - what you see as 'a grid' is that card; Auto resolves to BicubicArea and keeps the fine lines. RIGHT: the colour sprite at Scale x, rotated by Angle - at Scale 1 / Angle 0 Auto resolves to Nearest (exact 1:1 copy), to Bicubic as soon as it is enlarged or rotated. The labels show what Auto resolved to for each side.", c =>
            {
                Sprite card = Card();
                int size = (int)(c.H * c.Scale * 0.25f); if (size < 8) size = 8;
                int y = (c.H - size) / 2;
                for (int k = 0; k < c.Count; k++) c.Canvas.DrawScaled(card, 16, y, size, size, SR2D.Op.Paint, SR2D.Filter.Auto);
                int w = (int)(c.S * c.Scale), h = w;
                for (int k = 0; k < c.Count; k++) c.Canvas.DrawRotate2(c.A.Color, c.W * 3 / 4, c.H / 2, c.Angle, w, h, Op: SR2D.Op.Paint, Filter: SR2D.Filter.Auto);
                var left = SR2D.Resolve(SR2D.Filter.Auto, card.Width, card.Height, size, size);
                var right = SR2D.Resolve(SR2D.Filter.Auto, c.A.Color.Width, c.A.Color.Height, w, h, c.Angle);
                c.Label(18, y + 2, $"test card 2048 -> {size} px: Auto = {left}"); c.Label(c.W * 3 / 4 - w / 2, c.H / 2 - h / 2 - 16, $"sprite x{c.Scale:0.00}, {c.Angle * 180 / MathF.PI:0} deg: Auto = {right}");
                c.Note = "Auto -> left: " + left + "   right: " + right;
            }, warp: true).Ui(Param.Scale, "Scale (x0.01): card size = canvas height x Scale / 4, sprite = Scale x", Param.Angle, "Sprite rotation (deg)", Param.Count, "Repetitions (timing only)");
            T(GNew, "DrawRotateShear - lossless 3-shear rotation (right click = pivot)", "Rotation by three shears (Paeth 1986): rows are shifted, then columns, then rows again. Every source pixel is copied exactly once, so nothing is dropped or doubled and there is no resampling blur - the texture stays pixel-sharp; the outline is a staircase. Angles are reduced to -45..45 degrees by exact quarter turns first. Op selector = how the rotated pixels are combined (AlphaTest / AlphaBlend pick the keyed / alpha sprite); runs in managed code (~0.4 ms for 256x256 - scratch buffers are the tight bounding box and are reused between frames). Compare with DrawRotate (nearest: holes and doubled pixels at some angles) and DrawRotate2 bilinear (soft).", c =>
            {
                for (int k = 0; k < c.Count; k++)                                     // left: yours - angle from the slider / the rotate handles
                    c.Canvas.DrawRotateShear(SrcFor(c, c.Op), c.X + k * 40, c.Y + k * 40, c.Angle, c.PivotX, c.PivotY, c.Op, c.Blend);
                SelfSpinner(c, (x, y, a) => c.Canvas.DrawRotateShear(SrcFor(c, c.Op), x, y, a, -1, -1, c.Op, c.Blend));
            }).Ui(Param.Count, "Copies (each 40 px further down-right)", Param.Angle, "Angle (deg) - or drag around the edges of the sprite / wheel", Param.Op, "Blend op (AlphaBlend / AlphaTest pick the alpha / keyed sprite)", Param.Blend, "Blend factor (Op = Blend)", Param.Time, "Animate: the right-hand sprite spins with time", Param.Mouse, "Drag the sprite; drag around its edges = rotate; right click = pivot");
            L[L.Count - 1].MouseRotates = true; L[L.Count - 1].AnimSpin = 0f; L[L.Count - 1].HomeX = 0.3f; L[L.Count - 1].HomeY = 0.5f;

            T(GNew, "DrawRotate2 (Angle + Scale, Smooth = bilinear, right click = pivot)", "Rotation with scaling around a pivot given in source pixels (the pivot lands on the canvas position); compare with 'DrawRotate' in the original group. Animate off = turn it with the mouse (drag outside the sprite / wheel). RIGHT CLICK on the sprite sets the pivot to that source pixel, right click outside resets it to the centre; the overlay marks it.", c =>
            {
                int w = (int)(c.S * c.Scale), h = (int)(c.S * c.Scale);
                for (int k = 0; k < c.Count; k++)                                     // left: yours - angle from the slider / the rotate handles
                    c.Canvas.DrawRotate2(SrcFor(c, c.Op), c.X + k * 40, c.Y + k * 40, c.Angle, w, h, c.PivotX, c.PivotY, c.Op, Filt(c), c.Blend);
                SelfSpinner(c, (x, y, a) => c.Canvas.DrawRotate2(SrcFor(c, c.Op), x, y, a, w, h, -1, -1, c.Op, Filt(c), c.Blend));
            }, warp: true).Ui(Param.Count, "Copies (each 40 px further down-right)", Param.Angle, "Angle (deg) - or drag around the edges of the sprite / wheel", Param.Scale, "Scale (x0.01)", Param.Smooth, "Bilinear filter", Param.Op, "Blend op", Param.Blend, "Blend factor (Op = Blend)", Param.Time, "Animate: the right-hand sprite spins with time", Param.Mouse, "Drag the sprite; drag around its edges = rotate; right click = pivot");
            L[L.Count - 1].MouseRotates = true; L[L.Count - 1].AnimSpin = 0f; L[L.Count - 1].HomeX = 0.3f; L[L.Count - 1].HomeY = 0.5f;
            T(GNew, "DrawQuad - perspective", "Sprite mapped onto a quad whose top edge is pinched by the Scale slider and swings with time.", c =>
            {
                Span<PointF> q = stackalloc PointF[4];
                float cx = c.X, cy = c.Y, hw = c.S * 0.7f, hh = c.S * 0.7f;
                float pinch = 0.15f + 0.8f * Math.Clamp(c.Scale / 3f, 0f, 1f);
                float sw = MathF.Sin(c.Time) * 0.4f;
                q[0] = new PointF(cx - hw * pinch + sw * hw, cy - hh);
                q[1] = new PointF(cx + hw * pinch + sw * hw, cy - hh);
                q[2] = new PointF(cx + hw, cy + hh);
                q[3] = new PointF(cx - hw, cy + hh);
                for (int k = 0; k < c.Count; k++) c.Canvas.DrawQuad(SrcFor(c, c.Op), q, default, c.Op, Filt(c), c.Blend);
            }, warp: true);
            T(GNew, "DrawQuad - spinning cube face", "Four affine quads forming a rotating 'card' (angle from slider + time).", c =>
            {
                Span<PointF> q = stackalloc PointF[4];
                for (int k = 0; k < c.Count; k++)
                {
                    float a = c.Angle + c.Time + k * 0.3f, r = c.S * 0.6f;
                    float ca = MathF.Cos(a), sa = MathF.Sin(a);
                    float sk = MathF.Sin(c.Time * 0.7f) * 0.6f;                 // shear -> affine path
                    q[0] = new PointF(c.X + (-r) * ca - (-r) * sa + sk * r, c.Y + (-r) * sa + (-r) * ca);
                    q[1] = new PointF(c.X + (r) * ca - (-r) * sa + sk * r, c.Y + (r) * sa + (-r) * ca);
                    q[2] = new PointF(c.X + (r) * ca - (r) * sa - sk * r, c.Y + (r) * sa + (r) * ca);
                    q[3] = new PointF(c.X + (-r) * ca - (r) * sa - sk * r, c.Y + (-r) * sa + (r) * ca);
                    c.Canvas.DrawQuad(SrcFor(c, c.Op), q, default, c.Op, Filt(c), c.Blend);
                }
            }, warp: true);
            T(GNew, "DrawInPolygon (star, 10 pts)", "Sprite scaled to the star's bounding box and clipped to its outline; star size from Scale.", c =>
            {
                Span<PointF> poly = stackalloc PointF[10];
                float R = c.S * 0.6f * c.Scale, r = R * 0.45f;
                for (int i = 0; i < 10; i++)
                {
                    float a = i * MathF.PI / 5 - MathF.PI / 2 + c.Time * 0.3f, rr = (i & 1) == 0 ? R : r;
                    poly[i] = new PointF(c.X + MathF.Cos(a) * rr, c.Y + MathF.Sin(a) * rr);
                }
                for (int k = 0; k < c.Count; k++) c.Canvas.DrawInPolygon(SrcFor(c, c.Op), poly, c.Op, Filt(c), c.Blend);
            }, warp: true);
            T(GNew, "DrawQuad + clip polygon (rotated, hex-clipped)", "Rotated quad (affine) additionally clipped to a hexagon around the mouse.", c =>
            {
                Span<PointF> q = stackalloc PointF[4];
                Span<PointF> hex = stackalloc PointF[6];
                float a = c.Angle + c.Time * 0.5f, ca = MathF.Cos(a), sa = MathF.Sin(a), r = c.S * 0.7f;
                q[0] = new PointF(c.X + (-r) * ca - (-r) * sa, c.Y + (-r) * sa + (-r) * ca);
                q[1] = new PointF(c.X + (r) * ca - (-r) * sa, c.Y + (r) * sa + (-r) * ca);
                q[2] = new PointF(c.X + (r) * ca - (r) * sa, c.Y + (r) * sa + (r) * ca);
                q[3] = new PointF(c.X + (-r) * ca - (r) * sa, c.Y + (-r) * sa + (r) * ca);
                for (int i = 0; i < 6; i++) hex[i] = new PointF(c.X + MathF.Cos(i * MathF.PI / 3) * r * 0.9f * c.Scale, c.Y + MathF.Sin(i * MathF.PI / 3) * r * 0.9f * c.Scale);
                for (int k = 0; k < c.Count; k++) c.Canvas.DrawQuad(SrcFor(c, c.Op), q, hex, c.Op, Filt(c), c.Blend);
            }, warp: true);

            T(GNew, "DrawLine2 - fan (dashes along the line)", "Same fan as the original DrawLine test but with DrawLine2: dash pattern from the dot-step slider (dot = gap = step*2 px, measured ALONG the line so every direction looks equally dense), animated phase. The dot-step slider starts at 3 - at 0 the dash length is 0 and DrawLine2 draws solid lines, exactly like the original test, with nothing to compare. Op selector: Paint=Set, AlphaBlend, Add, Max, Min; XOR checkbox.", c =>
            {
                int n = c.Count * 64;
                float dash = c.DotStep * 2f;
                var op = c.Xor ? SR2D.LineOp.Xor : c.Op switch { SR2D.Op.AlphaBlend => SR2D.LineOp.AlphaBlend, SR2D.Op.Add => SR2D.LineOp.Add, SR2D.Op.Max => SR2D.LineOp.Max, SR2D.Op.Min => SR2D.LineOp.Min, _ => SR2D.LineOp.Set };
                for (int i = 0; i < n; i++)
                {
                    float a = i * MathF.PI * 2 / n + c.Time * 0.2f;
                    int col = SR2D.ARGB(160, (byte)(128 + 127 * MathF.Sin(a)), (byte)(128 + 127 * MathF.Sin(a + 2)), (byte)(128 + 127 * MathF.Sin(a + 4)));
                    c.Canvas.DrawLine2(c.X, c.Y, c.X + MathF.Cos(a) * c.W, c.Y + MathF.Sin(a) * c.H, col, op, dash, dash, c.Time * 30f);
                }
            }, warp: true, needsLine2: true).Ui(Param.DotStep, "Dash length x2 px (also the gap; 0 = solid)", Param.Xor, "XOR the dashes", Param.Op, "Line op", Param.Count, "Lines / 64", Param.Time, "Animate: the fan turns and the dash phase marches").With(Param.DotStep, 3);
            T(GNew, "DrawPolyline2 - marching ants", "Dashed closed polyline (star around the mouse) with the phase animated; the pattern is continuous across corners.", c =>
            {
                Span<PointF> poly = stackalloc PointF[10];
                float R = c.S * 0.6f * c.Scale, r = R * 0.45f;
                for (int i = 0; i < 10; i++)
                {
                    float a = i * MathF.PI / 5 - MathF.PI / 2 + c.Time * 0.3f, rr = (i & 1) == 0 ? R : r;
                    poly[i] = new PointF(c.X + MathF.Cos(a) * rr, c.Y + MathF.Sin(a) * rr);
                }
                for (int k = 0; k < c.Count; k++)
                    c.Canvas.DrawPolyline2(poly, unchecked((int)0xFFFFFFFF), true, SR2D.LineOp.Set, 6, 6, -c.Time * 40f);
            }, warp: true, needsLine2: true);

            // ===================================================== Shapes (Sprite.Shapes.cs)
            T(GShapes, "Polyline - width / AA / op", "Star polyline around the mouse. Width = Scale*8 px (1 = hairline), Smooth = anti-aliasing, Op selector: Paint=Set, AlphaBlend (colour alpha 160), Add, Max, Min; XOR checkbox. Count repeats the stroke.", c =>
            {
                Span<PointF> poly = stackalloc PointF[10];
                float R = c.S * 0.7f, r = R * 0.45f;
                for (int i = 0; i < 10; i++) { float a = i * MathF.PI / 5 - MathF.PI / 2 + c.Time * 0.3f, rr = (i & 1) == 0 ? R : r; poly[i] = new PointF(c.X + MathF.Cos(a) * rr, c.Y + MathF.Sin(a) * rr); }
                var op = ShapeOp(c);
                int col = SR2D.ARGB(160, 255, 220, 60);
                for (int k = 0; k < c.Count; k++) c.Canvas.DrawPolyline(poly, col, MathF.Max(1f, c.Scale * 8f), c.Smooth, true, op, true, c.Blend);
            }, needsPoly: true, needsLine2: true);
            T(GText, "DrawText - pixel font (scale / weight / colour / ops)", "Sprite.DrawText: the built-in 5x7 PixelFont drawn with ClearRect runs (Set) or FillRect + LineOp. Scale slider = pixel size (x0.01, clamped to 1..8), Blend = stroke weight (0..255 -> 0..3 extra px) AND the BlendFactor handed to DrawText, Brite = letter spacing (x4 px, clamped -2..8), Op selector: Set / AlphaBlend (alpha 160) / Xor / Add; Smooth = draw a background box; Count = how many lines (timing). Drag = anchor point (TextAnchor.Center); the label shows what the frame cost.", c =>
            {
                int scale = Math.Clamp((int)MathF.Round(c.Scale), 1, 8), weight = Math.Clamp(c.Blend / 64, 0, 3), spacing = Math.Clamp((int)MathF.Round(c.Brite * 4), -2, 8);
                var op = c.Xor ? SR2D.LineOp.Xor : c.Op switch { SR2D.Op.AlphaBlend => SR2D.LineOp.AlphaBlend, SR2D.Op.Add => SR2D.LineOp.Add, SR2D.Op.Blend => SR2D.LineOp.Blend, _ => SR2D.LineOp.Set };
                int col = op == SR2D.LineOp.AlphaBlend ? SR2D.ARGB(160, 255, 230, 90) : SR2D.ARGB(255, 255, 230, 90);
                int bg = c.Smooth ? SR2D.ARGB(255, 30, 40, 80) : 0;
                string line = "The quick brown fox jumps over the lazy dog 0123456789 !?%&()[]{}<>=+-*/";
                var size = Sprite.MeasureText(line, scale, weight, spacing);
                int lh = size.Height + 4 * scale;
                for (int i = 0; i < c.Count; i++)
                    c.Canvas.DrawText(c.X, c.Y + (i - (c.Count - 1) / 2) * lh, i % 3 == 0 ? line : i % 3 == 1 ? line.ToUpperInvariant() : line.ToLowerInvariant(), col, bg, scale, weight, spacing, op, c.Blend, TextAnchor.Center);
                // a few fixed samples in the corner: every scale 1..4, regular / bold, the anchors
                int y0 = 44;                                                      // below the bench's info panel
                for (int sc = 1; sc <= 4; sc++) { c.Canvas.DrawText(8, y0, $"scale {sc}", SR2D.ARGB(255, 200, 220, 255), 0, sc); c.Canvas.DrawText(8 + Sprite.MeasureText($"scale {sc}", sc).Width + 12 * sc, y0, "bold", SR2D.ARGB(255, 255, 160, 120), 0, sc, sc); y0 += (7 + 3) * sc; }
                c.Canvas.DrawText(c.W - 8, 8, "TopRight anchor", SR2D.ARGB(255, 255, 255, 255), SR2D.ARGB(255, 60, 20, 20), 1, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopRight);
                c.Canvas.DrawText(c.W - 8, c.H - 8, "BottomRight, weight 1, spacing 2", SR2D.ARGB(255, 255, 255, 255), SR2D.ARGB(255, 20, 60, 20), 1, 1, 2, SR2D.LineOp.Set, 128, TextAnchor.BottomRight);
                c.Canvas.DrawText(8, c.H - 8, "multi\nline\ntext", SR2D.ARGB(255, 120, 255, 160), 0, 2, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.BottomLeft);
                c.Note = $"{c.Count} line(s) x {line.Length} chars, scale {scale}, weight {weight}, spacing {spacing}, {op}";
            }, needsPoly: true, needsLine2: true).Ui(Param.Mouse, "Drag the text block (anchor = its centre)", Param.Scale, "Pixel size (x0.01, rounded to 1..8)", Param.Blend, "Weight (0..255 -> 0..3 extra px; it is ALSO the BlendFactor - with Op = Blend the text fades out as it thins)", Param.Brite, "Letter spacing (x4 px, -2..8)", Param.Op, "Op: Paint=Set | AlphaBlend | Add | Blend (others = Set)", Param.Xor, "XOR text", Param.Smooth, "Background box", Param.Count, "Lines drawn").With(Param.Scale, 200).With(Param.Blend, 96).With(Param.Brite, 0).With(Param.Count, 3).Range(Param.Scale, 100, 800);

            T(GControls, "SpriteKnob / SpriteSlider (strip above the canvas)", "Ordinary WinForms controls (cs/SpriteControls.cs, derived from SpriteBox, usable on any form / designer) drawn with SR2D. SpriteKnob is modular - three independent choices: DragMode (Angular = the knob faces the pointer; Endless = press to jump, then keep circling, Turns turns for the whole range), Gauge (Arc = 270-degree C; Circle = one full turn; Rings = one ring per turn; Spiral = a real spiral with Turns coils lit along its length; None) and Pointer (Bounded = it stops at the ends; Infinite = a jog wheel: the pointer spins for ever, only the value and the gauge stop). The Offset knob has all of them switchable next to it. Sizes: everything scales with the control Size; TextScale picks the font size explicitly (Blend knob = 2), and 0 (the default) is the smallest pixel-font size - which is what the tiny knobs use. The strip is two rows with a scroll bar when the window is too narrow for them; the canvas below is shortened to make room. Sliders: horizontal (Scale, with a native TrackBar bound to the same value - compare a quick drag) and vertical (Brite). A slider or an Angular knob takes its value from the absolute pointer position on every mouse message; an Endless / Infinite knob and a wheel work off relative angle / pixel deltas. Either way the control repaints synchronously, so a quick drag never lags. Wheel (Shift = fine), arrows / PgUp / PgDn / Home / End, double click = reset value (on a wheel double click opens the edit field instead - Ctrl + double click resets it). SpriteWheel (cs/SpriteControls.Wheel.cs) is a range control whose spin buttons are replaced by a draggable drum: vertical (Blur, Edit = Beside keeps an SR2D numeric field next to it to type / paste into) and horizontal (Hue: WrapAround 0..360 and WrapMouse - when the pointer reaches the SCREEN edge it is put back at the opposite edge, so the drum turns for ever; the second Hue wheel has WrapMouse off and a text box that opens on a plain click). CommitOnRelease (all): the thumb / knob lifts while you drag, a ghost marks the committed value, release drops it and applies the value once; Escape cancels. The canvas shows the sprite driven by the controls.", c =>
            {
                float sc = (float)ControlsDemo.ScalePct / 100f;
                int w = (int)(c.S * sc), h = (int)(c.S * sc);
                float ang = (float)ControlsDemo.AngleDeg * MathF.PI / 180f;
                int m = (int)Math.Clamp(ControlsDemo.Brite * 1.28, 0, 255), mul = SR2D.ARGB((byte)m, (byte)m, (byte)m, (byte)m);
                c.Temp.ClearBuffer(0);
                // the scratch is 2S x 2S and the sprite is S x S, so tile it 2x2 - a single copy would leave three
                // empty quadrants, and Op.Blend ignores alpha: those quadrants would paint flat grey over the disc.
                for (int ty = 0; ty < 2; ty++) for (int tx = 0; tx < 2; tx++)
                    c.Temp.MulAddS2X(c.A.Color, tx * c.S, ty * c.S, mul, SR2D.ARGB(128, 128, 128, 128));   // add 128 per byte = +0; an add of 0 would be -256 and collapse the sprite to nothing
                int cx = c.W / 2 + (int)ControlsDemo.Offset, cy = c.H / 2;
                // the wheels: a hue-tinted backdrop disc (Hue wheel, wraps) and a blur level (Level wheel) on the sprite
                int tint = LightsDemo.HueToArgb(ControlsDemo.Hue);
                c.Canvas.FillCircle(cx, cy, Math.Max(w, h) * 0.62f, SR2D.ARGB(255, (byte)((tint >> 16 & 255) / 3), (byte)((tint >> 8 & 255) / 3), (byte)((tint & 255) / 3)), SR2D.LineOp.Set, true);
                int lv = (int)Math.Clamp(ControlsDemo.Level, 1, 8);
                if (lv > 1) c.Temp.Blur((lv - 1) * 2, true);
                c.Canvas.DrawRotate2(c.Temp, cx, cy, ang, w, h, -1, -1, SR2D.Op.Blend, c.Smooth ? SR2D.Filter.Bilinear : SR2D.Filter.Nearest, (int)ControlsDemo.Blend);
                c.Info($"angle {ControlsDemo.AngleDeg:0}  offset {ControlsDemo.Offset:0}  scale {ControlsDemo.ScalePct:0}%  blend {ControlsDemo.Blend:0}  brite {ControlsDemo.Brite:0}%  hue {ControlsDemo.Hue:0}  blur level {lv}");
                c.Info($"{ControlsDemo.Events} ValueChanged events so far, last from: {ControlsDemo.LastSource}");
                c.Info("Offset knob = " + ControlsDemo.OffsetMode);
                c.Note = "controls strip above the canvas";
            }, warp: true, needsPoly: true, needsLine2: true).Ui(Param.Smooth, "Bilinear filter for the rotated sprite").Also(t => t.ControlStrip = ControlsDemo.Build);

            T(GControls, "SpriteButton / SpriteToggle / SpriteRadio / SpriteProgress", "The rest of the control set (cs/SpriteControls.Buttons.cs), same look as the knob: SpriteButton (Rounded / Pill / Square / Round shape, Accented = primary), SpriteToggle in four styles (Switch = sliding pill, Ellipse = circle inside an ellipse, Rocker = two-part tipping button with I / O, CheckBox = tick box), SpriteRadio (exclusive within the parent + GroupName - two groups here) and SpriteProgress (Horizontal / Vertical / Ring; continuous, Segments = LED bar, Marquee = indeterminate). Start runs a fake 8-second job that drives every progress bar (the sprite fades in with it, the marquee ring spins while it runs), Pause / Resume and Reset do what they say, Step adds 10 %. The toggles switch spin / bilinear / backdrop / grid on the canvas, the radios pick the draw Op and the size. Note that the Paint op has no opacity of its own, so while the job is running the 'Paint' radio quietly draws through Blend instead - that is the only way it can fade in. Keyboard: Tab between the controls, Space / Enter presses, arrows do nothing on these (they are for the knobs). Everything repaints synchronously on the mouse message, like the knobs.", c =>
            {
                double p = ControlsDemo.Progress / 100.0;
                float sc = ControlsDemo.SizeChoice switch { 0 => 0.5f, 2 => 1.4f, _ => 1f };
                // small viewport in the corner: the strip's toggles act on what it shows
                int vw = Math.Min(470, Math.Max(80, c.W - 24)), vh = Math.Min(310, Math.Max(60, c.H - 24));
                int vx = 12, vy = 12;
                int w = (int)(vh * 0.62f * sc), h = w;
                float ang = ControlsDemo.Spin ? c.Time * 0.9f : 0f;
                int cx = vx + vw / 2, cy = vy + vh / 2;
                c.Canvas.FillRect(vx, vy, vw, vh, unchecked((int)0xFF101418));
                if (ControlsDemo.Grid) for (int g = 32; g < Math.Max(vw, vh); g += 32) { if (g < vw) c.Canvas.DrawLine(vx + g, vy, vx + g, vy + vh - 1, unchecked((int)0xFF303840)); if (g < vh) c.Canvas.DrawLine(vx, vy + g, vx + vw - 1, vy + g, unchecked((int)0xFF303840)); }
                if (ControlsDemo.Backdrop) { float r = w * 0.8f; c.Canvas.FillRect(cx - r, cy - r, 2 * r, 2 * r, unchecked((int)0xFFD8DCE0), SR2D.LineOp.Set, true); }
                var op = ControlsDemo.OpChoice switch { 1 => SR2D.Op.AlphaBlend, 2 => SR2D.Op.Add, 3 => SR2D.Op.Blend, _ => SR2D.Op.Paint };
                var src = op == SR2D.Op.AlphaBlend ? c.A.Alpha : c.A.Color;
                bool started = ControlsDemo.Running || ControlsDemo.Progress > 0;
                int blend = started ? (int)Math.Round(255 * p) : 255;                // the job fades the sprite in; idle = fully visible
                if (op == SR2D.Op.Paint && blend < 255) { op = SR2D.Op.Blend; }     // "Paint" fades in via Blend until the job is done
                c.Canvas.DrawRotate2(src, cx, cy, ang, w, h, -1, -1, op, ControlsDemo.Bilinear ? SR2D.Filter.Bilinear : SR2D.Filter.Nearest, blend);
                c.Canvas.DrawRect(vx + 0.5f, vy + 0.5f, vw - 1, vh - 1, unchecked((int)0xFF707880), 1f, false, SR2D.LineOp.AlphaBlend);   // the viewport rim
                c.Info($"job {ControlsDemo.Progress:0}% {(ControlsDemo.Running ? "running" : ControlsDemo.Progress >= 100 ? "done" : ControlsDemo.Progress > 0 ? "paused" : "idle")}  op {op}  size {sc:0.0}x  spin {ControlsDemo.Spin}  bilinear {ControlsDemo.Bilinear}");
                c.Info($"{ControlsDemo.Events} control events so far, last: {ControlsDemo.LastSource}");
                c.Note = "corner viewport; the strip's buttons / toggles / radios drive it";
            }, warp: true, needsPoly: true, needsLine2: true).Also(t => { t.ControlStrip = ControlsDemo.BuildButtons; t.StripHeight = ControlsDemo.ButtonsStripHeight; });

            T(GControls, "SpriteColorPicker / SpriteColorDialog (colour, on its own test)", "The colour controls on their own test: LEFT - SpriteColorPicker (cs/SpriteControls.Color.cs) as a plain control, the same HSV area + strip + alpha slider + hex box + six-scheme numeric column the dialog uses, as a Value / ValueChanged control for any form; the canvas shows a big swatch of its colour. ON THE CANVAS - the embedded SpriteColorDialog beside the corner viewport: OK paints the viewport's background (instant, no modal loop - it stays open while the picture answers), Cancel reverts. The dialog's preview doubles as the eyedropper: press it, sweep anywhere on the screen, release - one click (or the release after a sweep) takes that pixel's colour and restores the cursor; Esc or right click cancels.", c =>
            {
                // corner viewport painted by the dialog's OK (ViewBack), the strip's picker colour as a swatch beside it
                int vw = Math.Min(470, Math.Max(80, c.W - 24)), vh = Math.Min(310, Math.Max(60, c.H - 24));
                int vx = 12, vy = 12;
                c.Canvas.FillRect(vx, vy, vw, vh, ColorDemo.ViewBack);
                c.Canvas.DrawRect(vx + 0.5f, vy + 0.5f, vw - 1, vh - 1, unchecked((int)0xFF707880), 1f, false, SR2D.LineOp.AlphaBlend);
                var pc = Color.FromArgb(ColorDemo.PickerArgb);
                // The swatch sits right of the embedded dialog, wherever it actually is. The old fixed offset from the
                // viewport (vx + vw + 280) put its left edge at 762 while the dialog spans 498..774, so the first 12 px
                // of the swatch were painted under it - and a narrower canvas buried the whole thing.
                int sx = ColorDemo.ColorOverlay is { Visible: true } ov ? Math.Max(vx + vw + 16, ov.Right + 8) : vx + vw + 300;
                int sw = Math.Min(220, Math.Max(8, c.W - sx - 12)), sh = Math.Min(220, Math.Max(40, c.H - 24));
                c.Canvas.FillRect(sx, vy, sw, sh, ColorDemo.PickerArgb, SR2D.LineOp.Set, true);
                c.Canvas.DrawRect(sx + 0.5f, vy + 0.5f, sw - 1, sh - 1, unchecked((int)0xFF707880), 1f, false, SR2D.LineOp.AlphaBlend);
                c.Info($"picker #{ColorDemo.PickerArgb & 0xFFFFFF:X6}  alpha {(ColorDemo.PickerArgb >> 24) & 255}  hue {Math.Round(pc.GetHue())}  viewport #{ColorDemo.ViewBack & 0xFFFFFF:X6}");
                c.Info($"{ColorDemo.Edits} colour events so far, last: {ColorDemo.LastAction}");
                c.Note = "the dialog (beside the viewport) paints the viewport's background on OK; the strip's picker drives the swatch";
            }, needsPoly: true).Also(t => { t.ControlStrip = ColorDemo.Build; t.StripHeight = ColorDemo.StripHeight; });

            T(GControls, "Discrete values: Values / PowersOfTwo / notches / labels", "The snapping mode of the range controls (cs/SpriteControls.cs): 'Snap+Step 2' is the arithmetic grid (Snap + Step = every 2), 'Powers of two' is PowersOfTwo (the powers of two inside Minimum..Maximum - the list follows range changes), 'Predetermined' is an explicit Values list (0 0.5 1.5 3 4 - sorted and de-duplicated automatically, EVEN spacing: the thumb sits between the entries, not at their numeric position), the Gear KNOB shows the same on a rotary control with ticks and numbers around the body, and the Numeric snaps to the powers of two as well (buttons / arrows / wheel / typing all pick the nearest entry; the buttons step one ENTRY at a time, never value + Step, which would get stuck between two entries). ShowNotches draws a tick per value, NotchLabels prints it (marks that would overlap the numbers are skipped). Every input snaps to the nearest entry: drags, wheel, keys, typed text, code. The 'Predetermined' slider has CommitOnRelease on, so dragging it previews the snapped thumb and only applies the value when you let go. The caption below shows the last value each control delivered.", c =>
            {
                c.Canvas.ClearBuffer(unchecked((int)0xFF14181E));
                c.Canvas.DrawText(20, 20, $"last: {DiscreteDemo.Last}", unchecked((int)0xFFE0E0E0), 0, TextAnchor.TopLeft, 2);
                c.Canvas.DrawText(20, 70, $"snap2 {DiscreteDemo.Steps:0.##}   pow2 {DiscreteDemo.Pow2:0}   list {DiscreteDemo.List:0.###}   gear {DiscreteDemo.Num:0.##}   numeric {DiscreteDemo.NumV:0}", unchecked((int)0xFF9FC0E0), 0, TextAnchor.TopLeft, 2);
                c.Canvas.DrawText(20, 120, "wheel / arrows / buttons step ONE list entry - drags and typing snap to the NEAREST entry", unchecked((int)0xFF808090), 0, TextAnchor.TopLeft);
                c.Canvas.DrawText(20, 150, $"changes: {ControlsDemo.Events}", unchecked((int)0xFF808090), 0, TextAnchor.TopLeft);
            }, clears: true)
                .Also(m => m.ControlStrip = () => DiscreteDemo.Build()).Also(m => m.StripHeight = DiscreteDemo.StripHeight);
T(GControls, "SpriteLabel / GroupBox / Tabs / TextBox / Numeric / Combo / ListBox / LED", "The form furniture in the same look (cs/SpriteControls.Static.cs + SpriteControls.Input.cs), arranged as a small settings form on the strip: SpriteTabControl (three pages; Left / Right keys, the wheel over the strip), SpriteGroupBox with a check box in its caption (unchecking disables everything inside - the 'Enable group' switch on the right is bound to it both ways), SpriteLabel in five styles (Plain / Heading with a rule / Muted / Readout / Badge, AutoSize like a Label, word wrap on the About page), SpriteTextBox (caret, mouse + Shift selection, double click = word, Ctrl+A/C/X/V, Home / End, Ctrl+arrows, placeholder, PasswordChar; Enter or focus loss commits, Escape reverts), SpriteNumeric (spin buttons with auto-repeat, Up / Down / PgUp / PgDn, wheel, Shift = a tenth, a Unit suffix, typed garbage is rejected), SpriteCombo (opens an SR2D menu; Up / Down and the wheel change it without opening, first letter jumps), SpriteListBox (SR2D scroll bar, multi selection with Shift / Ctrl, check boxes, Ctrl+A, first-letter search, double click / Enter = ItemActivated), SpriteLed (round / square / bar; lit = glowing, Blink, Clickable = a tiny toggle), SpriteSeparator, SpritePanel (Sunken / Raised / Outline / Flat). Containers hand their face colour to the SpriteControls inside (a knob on a sunken panel needs no BackColor). The canvas draws Copies sprites in the chosen Layout with the caption, tinted by the Hue when the LED is on, framed when the group is enabled, with the checked list items as tags.", c =>
            {
                int n = Math.Max(1, FormDemo.Copies), gap = FormDemo.Spacing, w = c.S, h = c.S;
                int cx = c.W / 2, cy = c.H / 2;
                bool tint = FormDemo.TintOn;
                if (tint) fx.Clear().Color(Tint: 0.55f, TintColor: LightsDemo.HueToArgb(FormDemo.Tint));
                if (FormDemo.Shadow) { if (tint) fx.ShadowAt(45, 8, 6, unchecked((int)0xFF000000), 0.6f); else fx.Clear().ShadowAt(45, 8, 6, unchecked((int)0xFF000000), 0.6f); }
                bool useFx = tint || FormDemo.Shadow;
                for (int i = 0; i < n; i++)
                {
                    int x, y;
                    switch (FormDemo.Layout)
                    {
                        case 1: x = cx - w / 2; y = cy - (n * (h + gap) - gap) / 2 + i * (h + gap); break;
                        case 2: x = cx - (n * (w / 2 + gap) - gap) / 2 + i * (w / 2 + gap); y = cy - (n * (h / 2 + gap) - gap) / 2 + i * (h / 2 + gap); break;
                        case 3: { double a = i * Math.PI * 2 / n - Math.PI / 2; float r = Math.Max(w, 40) * 0.9f + gap * 2; x = (int)(cx + r * Math.Cos(a)) - w / 2; y = (int)(cy + r * Math.Sin(a)) - h / 2; break; }
                        default: x = cx - (n * (w + gap) - gap) / 2 + i * (w + gap); y = cy - h / 2; break;
                    }
                    if (useFx) c.Canvas.DrawFx(c.A.Alpha, x, y, fx, SR2D.Op.AlphaBlend); else c.Canvas.Draw(c.A.Alpha, x, y, SR2D.Op.AlphaBlend);
                    if (FormDemo.Frame) c.Canvas.DrawRect(x - 2.5f, y - 2.5f, w + 5, h + 5, unchecked((int)0xFFE8ECF0), 1f, true);
                    if (FormDemo.Caption.Length > 0) c.Canvas.DrawText(x + w / 2, y + h + 6, FormDemo.Caption, unchecked((int)0xFFFFFFFF), unchecked((int)0xFF202428), TextAnchor.TopCenter, 2);
                }
                int tx = 12, ty = c.H - 40;
                foreach (var tag in FormDemo.Picked) { var r = c.Canvas.DrawText(tx, ty, tag, unchecked((int)0xFFFFFFFF), unchecked((int)0xFF3399FF), TextAnchor.TopLeft, 2); tx += r.Width + 8; if (tx > c.W - 80) { tx = 12; ty -= r.Height + 4; } }
                c.Info($"caption '{FormDemo.Caption}'  copies {n}  spacing {gap}  layout {FormDemo.Layout}  tint {(tint ? FormDemo.Tint.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "off")}  frame {FormDemo.Frame}  shadow {FormDemo.Shadow}");
                c.Info($"list: {FormDemo.Status}; {FormDemo.Picked.Count} checked; {FormDemo.Events} control events so far, last: {FormDemo.LastSource}");
                c.Note = "controls strip above the canvas";
            }, needsFx: true).Also(t => { t.ControlStrip = FormDemo.Build; t.StripHeight = FormDemo.StripHeight; });

            T(GControls, "SpriteBox SizeMode: zoom / pan / scroll bars / navigation", "SpriteBox with a SizeMode (cs/SpriteBox.View.cs): the Surface is ImageSize (1600 x 1200 here, drawn by the Render handler exactly as before) and is placed like a PictureBox image - CenterImage (1:1), StretchImage, Zoom (fit, borders), Fill (cover, clipped), FitWidth, FitHeight - times a Zoom multiplier, moved by Pan. Only the visible pixels are ever resampled (a 64x zoom costs the same as 1x; shrinking below 1/2 uses a cached box-averaged copy), and the composed screen is cached until the view or the picture changes. Navigation like Photoshop: the plain cursor is a magnifier (drag left / right = scrubby zoom about the pressed point, click = in, Alt + click = out), Space or the middle button = hand, release while moving = the image keeps sliding (Inertia, Friction), free pan may push up to Overscroll of the image out of the view, wheel scrolls (Shift sideways, Ctrl zooms), right click = context menu (modes, zoom, reset), SR2D scroll bars (SpriteScrollBar) appear when the image is larger than the view - and, in the Free pan mode this test starts in, always, because overscroll widens the pan range past the picture. ImageAt(clientPoint) maps the mouse to the image pixel (extrapolated outside, with an Inside flag) - shown below the strip while you hover. None = the classic SpriteBox (surface = client area).", c =>
            {
                c.Info(ViewDemo.State);
                c.Info(ViewDemo.Hover.Length > 0 ? ViewDemo.Hover : "hover the picture for the image pixel under the mouse");
                c.Info($"picture rendered {ViewDemo.Frames} time(s) (the Render handler runs only when the picture is dirty - not per zoom / pan)");
                c.Note = "SpriteBox in the strip above";
            }).Also(t => { t.ControlStrip = ViewDemo.Build; t.StripHeight = ViewDemo.StripHeight; });

            T(GControls, "VoxelBox: a SpriteBox viewport onto a VoxelGrid", "VoxelBox (cs/SpriteBox.Voxel.cs) is a SpriteBox that shows a VoxelGrid you attach in code (box.Grid = grid) with the camera / lighting / fade machinery of the voxel tests, all driven from the control itself: left drag orbits the free camera (presets pan), middle button or Space + left pans, wheel zooms about the pointer (whole pixels per voxel in the pixel-art presets), arrows / + - / Home / F / I / N on the keyboard, and the RIGHT BUTTON opens an SR2D-drawn settings menu (SpriteMenu, cs/SpriteControls.Menu.cs - no native ContextMenuStrip): camera presets, voxel mode, lighting tiers with sky / lamp / reach sliders, depth fade, zoom, parallel render, preview while dragging, info, axes. Check / radio / slider rows keep the menu open; a click outside closes it without being swallowed. While the mouse button is down a cheap preview is drawn (propagated light and AO off, and once the last full frame took over 40 ms or the grid passes 128^3 it drops to unlit 1-px Points) and the full picture follows on release - so a 256 x 256 x 128 terrain still orbits fluidly. The strip on the right picks the scene, and with 'Edit' the left click adds a voxel on the face you hit (Shift = remove) through the VoxelClick event.", c =>
            {
                c.Info(VoxelDemo.State);
                c.Info(VoxelDemo.Hit.Length > 0 ? VoxelDemo.Hit : "right click the voxel view for its settings menu; hover for the voxel under the mouse");
                c.Note = "VoxelBox in the strip above";
            }).Also(t => { t.ControlStrip = VoxelDemo.Build; t.StripHeight = VoxelDemo.StripHeight; });

            T(GShapes, "Curves: DrawCurve through points (Smooth = AA)", "The same 8 points drawn as a polyline (grey) and as DrawCurve (spline THROUGH the points, no control points needed). Scale slider = Tension (x0.05..1: 0.05 = round, 1 = straight), width from Blend/32 px, Op selector, XOR. Drag the whole figure; Count repeats it.", c =>
            {
                Span<PointF> p = stackalloc PointF[8];
                for (int i = 0; i < 8; i++) p[i] = new PointF(c.X - 280 + i * 80 + MathF.Sin(c.Time * 0.7f + i) * 20, c.Y + MathF.Sin(i * 1.9f + c.Time * 0.5f) * (c.S * 0.5f));
                float tension = Math.Clamp(c.Scale - 0.05f, 0f, 1f);
                float w = MathF.Max(1f, c.Blend / 32f);
                var op = ShapeOp(c);
                for (int k = 0; k < c.Count; k++)
                {
                    c.Canvas.DrawPolyline(p, unchecked((int)0xFF606060));
                    foreach (var q in p) c.Canvas.FillCircle(q.X, q.Y, 3, unchecked((int)0xFF909090), SR2D.LineOp.Set, c.Smooth);
                    c.Canvas.DrawCurve(p, SR2D.ARGB(200, 255, 210, 60), w, c.Smooth, false, tension, op, true);
                }
                c.Note = $"tension {tension:F2}, width {w:F1}";
            }, needsPoly: true, needsLine2: true).Ui(Param.Scale, "Tension (slider / 100 - 0.05): 5 = the roundest spline, 105 and above = the straight polyline", Param.Blend, "Stroke width (Blend / 32 px)", Param.Smooth, "Anti-aliased", Param.Op, "Line op", Param.Xor, "XOR lines", Param.Count, "Repeats of the figure", Param.Mouse, "Drag the figure", Param.Time, "Animate: the points drift").With(Param.Scale, 20);   // the generic Scale 1.00 would be tension 0.95 = a straight polyline: the spline would look like the grey reference
            T(GShapes, "Curves: FillCurve / closed blob + dashed DrawCurve2", "Closed spline through 7 points around the object: FillCurve (AlphaBlend, Smooth = AA) + a white DrawCurve outline + DrawCurve2 marching-ants hairline (phase animated), then the grey 1-px DrawPolygon outline = the straight-edged reference (what the same 7 points look like without any spline). Scale = Tension (5 = round, 105 and above = straight - it starts round, the generic Scale 1.00 would be tension 0.95 and the blob would sit on its own reference).", c =>
            {
                Span<PointF> p = stackalloc PointF[7];
                float R = c.S * 0.8f;
                for (int i = 0; i < 7; i++) { float a = i * MathF.PI * 2 / 7, rr = R * (0.55f + 0.45f * MathF.Sin(i * 2.3f + c.Time * 0.8f)); p[i] = new PointF(c.X + MathF.Cos(a) * rr, c.Y + MathF.Sin(a) * rr); }
                float tension = Math.Clamp(c.Scale - 0.05f, 0f, 1f);
                for (int k = 0; k < c.Count; k++)
                {
                    c.Canvas.FillCurve(p, SR2D.ARGB(120, 80, 160, 255), SR2D.LineOp.AlphaBlend, c.Smooth, tension);
                    c.Canvas.DrawCurve(p, unchecked((int)0xFFFFFFFF), 2f, c.Smooth, true, tension);
                    c.Canvas.DrawCurve2(p, unchecked((int)0xFF000000), true, tension, SR2D.LineOp.Set, 6, 6, -c.Time * 40f);
                    c.Canvas.DrawPolygon(p, unchecked((int)0xFF505050));
                }
            }, needsPoly: true, needsLine2: true).Ui(Param.Scale, "Tension (slider / 100 - 0.05): 5 = round, 105+ = straight", Param.Smooth, "Anti-aliased", Param.Count, "Repeats", Param.Mouse, "Drag the blob", Param.Time, "Animate: the points breathe").With(Param.Scale, 20);
            T(GShapes, "Curves: PathBuilder (Bézier / arcs / smooth / holes)", "Explicit control: a PathBuilder with CurveTo / SmoothTo (SVG-style S: continuous tangent, only the second control point given) / ArcTo / SmoothThrough, stroked with DrawPath (width, round caps) and DrawPath2 (dashes). Right: FillPath of RoundRect + Circle with EvenOdd = hole, a pie by ArcAround, a SmoothPolygon blob. Control points shown in grey; drag moves the first curve's control point.", c =>
            {
                float ox = 40, oy = c.H * 0.5f;
                var pb = new Sprite.PathBuilder();
                pb.MoveTo(ox, oy).CurveTo(c.X, c.Y, ox + 180, oy + 120, ox + 240, oy).SmoothTo(ox + 360, oy + 100, ox + 420, oy)
                  .SmoothQuadTo(ox + 520, oy).ArcTo(60, 40, 0, false, true, ox + 640, oy).LineTo(ox + 700, oy + 40)
                  .SmoothThrough(stackalloc PointF[] { new PointF(ox + 760, oy - 40), new PointF(ox + 820, oy + 60), new PointF(ox + 870, oy) });
                var fill = new Sprite.PathBuilder();
                float rx = c.W - 300, ry = 40;
                fill.RoundRect(rx, ry, 250, 120, 30).Circle(rx + 125, ry + 60, 35);
                var pie = new Sprite.PathBuilder();
                pie.MoveTo(rx + 60, ry + 250).ArcAround(rx + 60, ry + 250, 60, -90, 40 + 300 * (0.5f + 0.5f * MathF.Sin(c.Time))).Close();
                var blob = new Sprite.PathBuilder();
                blob.SmoothPolygon(stackalloc PointF[] { new PointF(rx + 160, ry + 200), new PointF(rx + 230, ry + 190), new PointF(rx + 250, ry + 260), new PointF(rx + 200, ry + 300), new PointF(rx + 140, ry + 270) });
                ReadOnlySpan<PointF> guide = stackalloc PointF[] { new PointF(ox, oy), new PointF(c.X, c.Y), new PointF(ox + 180, oy + 120), new PointF(ox + 240, oy) };   // once, outside the loop (CA2014)
                for (int k = 0; k < c.Count; k++)
                {
                    c.Canvas.DrawPolyline(guide, unchecked((int)0xFF606060));
                    c.Canvas.DrawPath(pb, SR2D.ARGB(255, 90, 230, 120), 3f, c.Smooth, SR2D.LineOp.Set, true);
                    c.Canvas.DrawPath2(pb, unchecked((int)0xFFFF5050), SR2D.LineOp.Set, 6, 6, -c.Time * 30f);
                    c.Canvas.FillPath(fill, unchecked((int)0xFF3060A0), SR2D.LineOp.Set, c.Smooth, true);
                    c.Canvas.DrawPath(fill, unchecked((int)0xFFFFFFFF), 2f, c.Smooth);
                    c.Canvas.FillPath(pie, unchecked((int)0xFFE0A030), SR2D.LineOp.Set, c.Smooth);
                    c.Canvas.FillPath(blob, unchecked((int)0xFF60C060), SR2D.LineOp.Set, c.Smooth);
                }
            }, needsPoly: true, needsLine2: true);
            T(GShapes, "Stroker: caps / joins / miter limit / dashes (Sprite.Stroke.cs)", "StrokePolyline / StrokePath / StrokeRect with a StrokeStyle: proper joins (Miter, Round, Bevel) and caps (Butt, Round, Square), miter limit, dash arrays with offset, each pixel touched once (no double blending at the joins even with alpha). Width = Scale*24 px, Angle bends the zig-zag, Z = miter limit (x0.1), Blend = the BlendFactor the AlphaBlend op uses, Smooth = AA, Op selector as for the other shapes. Drag the star. Compare with DrawPolyline (old, joins overlap).", c =>
            {
                float w = MathF.Max(1f, c.Scale * 24f), lim = MathF.Max(1f, c.Z * 0.1f);
                var op = ShapeOp(c); int col = SR2D.ARGB(op == SR2D.LineOp.AlphaBlend ? (byte)150 : (byte)255, 255, 220, 60);
                // three zig-zags, one per join, the corner angle from the Angle knob
                float ox = 40, oy = 60, span = (c.W - 80) / 3f, dy = 90 + 70 * MathF.Sin(c.Angle);
                Span<PointF> zig = stackalloc PointF[6];
                var joins = new[] { LineJoin.Miter, LineJoin.Round, LineJoin.Bevel }; var caps = new[] { LineCap.Butt, LineCap.Round, LineCap.Square };
                for (int j = 0; j < 3; j++)
                {
                    float x0 = ox + j * span + 20, step = (span - 40) / 5;
                    for (int i = 0; i < 6; i++) zig[i] = new PointF(x0 + i * step, oy + ((i & 1) == 0 ? 0 : dy));
                    var st = new StrokeStyle(w, caps[j], joins[j], lim);
                    for (int k = 0; k < c.Count; k++) c.Canvas.StrokePolyline(zig, col, st, c.Smooth, false, op, c.Blend);
                    c.Canvas.DrawText((int)x0, (int)(oy + dy + w / 2 + 8), $"{joins[j]} / {caps[j]}", unchecked((int)0xFFC0D0FF), 0, 1);
                }
                // dashed star around the mouse (closed, round caps, animated offset) + a dotted ring + a rounded rectangle
                Span<PointF> star = stackalloc PointF[10];
                float R = c.S * 0.55f, r = R * 0.45f;
                for (int i = 0; i < 10; i++) { float a = i * MathF.PI / 5 - MathF.PI / 2, rr = (i & 1) == 0 ? R : r; star[i] = new PointF(c.X + MathF.Cos(a) * rr, c.Y + MathF.Sin(a) * rr); }
                var dashed = new StrokeStyle(MathF.Max(1f, w * 0.4f), LineCap.Round, LineJoin.Round, lim, new[] { w, w * 0.8f }, -c.Time * 40f);
                var ring = new Sprite.PathBuilder(); ring.Circle(c.X, c.Y, R + w);
                for (int k = 0; k < c.Count; k++)
                {
                    c.Canvas.StrokePolygon(star, col, dashed, c.Smooth, op, c.Blend);
                    c.Canvas.StrokePath(ring, SR2D.ARGB(255, 120, 200, 255), StrokeStyle.Dotted(MathF.Max(2f, w * 0.3f)), c.Smooth, op, c.Blend);
                    c.Canvas.StrokeRect(c.X - R - 2 * w, c.Y - R - 2 * w, 2 * (R + 2 * w), 2 * (R + 2 * w), SR2D.ARGB(255, 255, 120, 120), new StrokeStyle(MathF.Max(1f, w * 0.25f), LineCap.Butt, LineJoin.Round), c.Smooth, op, c.Blend);
                }
                // the old DrawPolyline for comparison (bottom left): overlapping segment rectangles at the corners
                Span<PointF> old = stackalloc PointF[4]; for (int i = 0; i < 4; i++) old[i] = new PointF(40 + i * 60, c.H - 40 - ((i & 1) == 0 ? 0 : 50));
                c.Canvas.DrawPolyline(old, SR2D.ARGB(150, 255, 220, 60), w, c.Smooth, false, SR2D.LineOp.AlphaBlend);
                c.Canvas.DrawText(40, c.H - 24, "DrawPolyline (old)", unchecked((int)0xFF808080), 0, 1);
                for (int i = 0; i < 4; i++) old[i].X += 260;
                c.Canvas.StrokePolyline(old, SR2D.ARGB(150, 255, 220, 60), new StrokeStyle(w, LineCap.Butt, LineJoin.Miter, lim), c.Smooth, false, SR2D.LineOp.AlphaBlend);
                c.Canvas.DrawText(300, c.H - 24, "StrokePolyline (new, single blend per pixel)", unchecked((int)0xFF808080), 0, 1);
                c.Note = $"width {w:0.#} px, miter limit {lim:0.#}, {op}";
            }, needsPoly: true, needsLine2: true).Ui(Param.Mouse, "Drag the star", Param.Scale, "Stroke width (x24 px)", Param.Angle, "Corner angle of the zig-zags", Param.Z, "Miter limit (x0.1; below ~2 the miters turn into bevels)", Param.Blend, "BlendFactor of the strokes (only used by the AlphaBlend op)", Param.Smooth, "Anti-aliasing", Param.Op, "Op: Paint=Set | AlphaBlend (alpha 150) | Add | Max | Min", Param.Count, "Repetitions (timing)", Param.Time, "Dash offset animation").With(Param.Z, 120).With(Param.Op, 2);
            T(GText, "SpriteFont: TrueType / OpenType text (SpriteFont.cs, no GDI+)", "Sprite.DrawString with a SpriteFont (the engine's own TrueType / CFF / TTC parser + rasteriser, glyph bitmap cache, kerning, sub-pixel positioning, word wrap, alignment, outline and gradient text along a transform). The strip above picks the family / face, the fake bold (em) and your own paragraph; TextCache ticked draws the paragraph from a cached bitmap (SpriteFont.Render + TextCache, one blit per frame) instead of laying it out every frame. Scale = font size (x40 px), Brite = fake italic, Angle rotates the path text, Smooth toggles sub-pixel positions (4 vs 1). Drag the paragraph block. Op selector: Set / AlphaBlend (alpha 180, and the only op the TextCache path uses - the cached bitmaps are premultiplied, so with Set / Add / Max / Min the text stays live and the note says so; the test opens on AlphaBlend) / Add / Max / Min.", c =>
            {
                var f = FontDemo.Get(); if (f == null) { c.Canvas.DrawText(20, 60, "no TrueType font found on this system (SpriteFont.Installed)", unchecked((int)0xFFFF8080), 0, 2); return; }
                float size = MathF.Max(6f, c.Scale * 40f);
                f.FakeBold = (float)FontDemo.Bold; f.FakeItalic = MathF.Max(0f, c.Brite * 0.2f); f.SubPixelPositions = c.Smooth ? 4 : 1;
                var op = ShapeOp(c); int col = SR2D.ARGB(op == SR2D.LineOp.AlphaBlend ? (byte)180 : (byte)255, 255, 240, 200);
                string para = FontDemo.Text;
                bool cached = FontDemo.UseCache && op == SR2D.LineOp.AlphaBlend;          // the cache blits premultiplied bitmaps: alpha-over only (Set / Add / Max / Min stay live)
                var cache = textCache ??= new TextCache();
                float wrapW = MathF.Min(c.W - 40, c.S * 1.6f);
                for (int k = 0; k < c.Count; k++)
                {
                    string head = $"{f.FamilyName} {f.StyleName}  {size:0.#} px  ({f.GlyphCount} glyphs, glyph cache {f.CacheBytes / 1024} KB, text cache {cache.Count} bitmaps {cache.Bytes / 1024} KB, {cache.Hits} hits / {cache.Misses} misses)";
                    c.Canvas.DrawString(20, 44, head, f, size * 0.6f, unchecked((int)0xFFC0D0FF), TextAnchor.TopLeft, op, c.Blend);   // the stats line changes every frame: always live
                    var box = cached ? cache.DrawStringBlock(c.Canvas, c.X, c.Y, para, f, size, col, wrapW, 1, TextAnchor.Center, 1.2f)
                                     : c.Canvas.DrawStringBlock(c.X, c.Y, para, f, size, col, wrapW, 1, TextAnchor.Center, 1.2f, op, c.Blend);
                    c.Canvas.StrokeRect(box, unchecked((int)0xFF506080), new StrokeStyle(1f), true, SR2D.LineOp.Set);
                    // outline + gradient text on a rotated transform (vector path route: DrawStringPath / DrawStringOutline)
                    var rot = System.Numerics.Matrix3x2.CreateRotation(c.Angle, new System.Numerics.Vector2(c.W * 0.5f, c.H - 80));
                    c.Canvas.DrawStringPath(c.W * 0.5f - f.Measure("Gradient", size * 1.6f) * 0.5f, c.H - 80, "Gradient", f, size * 1.6f, SpriteGradient.Linear(0, 0, 1, 0, unchecked((int)0xFFFF6030), unchecked((int)0xFF3399FF), unchecked((int)0xFFFFE060)), rot, c.Smooth);
                    c.Canvas.DrawStringOutline(c.W * 0.5f + f.Measure("Gradient", size * 1.6f) * 0.55f, c.H - 80, "Outline", f, size * 1.6f, unchecked((int)0xFFFFFFFF), StrokeStyle.Round(MathF.Max(1f, size * 0.04f)), rot, SR2D.LineOp.AlphaBlend, c.Smooth);
                }
                // the sizes ladder on the right: 8..40 px, the same string (cached when the cache is on: 8 bitmaps, reused every frame)
                float y = 44; foreach (int px in new[] { 8, 10, 12, 14, 18, 24, 32, 40 })
                {
                    string t = $"{px} px Hamburgefonstiv";
                    if (cached) cache.DrawString(c.Canvas, c.W - 20, y, t, f, px, unchecked((int)0xFFE0E0E0), TextAnchor.TopRight); else c.Canvas.DrawString(c.W - 20, y, t, f, px, unchecked((int)0xFFE0E0E0), TextAnchor.TopRight);
                    y += px * 1.3f + 2;
                }
                if (cache.Bytes > 24L << 20) cache.Clear();                                      // dragging / resizing makes many one-off bitmaps; keep the demo's footprint small
                c.Note = $"{size:0.#} px, bold {f.FakeBold:0.###} em, italic {f.FakeItalic:0.##}, sub-pixel {f.SubPixelPositions}, {op}{(cached ? ", TextCache" : FontDemo.UseCache ? ", TextCache off (needs the AlphaBlend op)" : "")}";
            }, needsPoly: true, needsLine2: true).Ui(Param.Mouse, "Drag the paragraph (anchor = its centre)", Param.Scale, "Font size (x40 px)", Param.Brite, "Fake italic (shear, 0 = upright)", Param.Angle, "Rotation of the gradient / outline text", Param.Smooth, "Sub-pixel glyph positions (4) vs whole pixels (1)", Param.Op, "Op: Paint=Set | AlphaBlend (alpha 180, the TextCache path) | Add | Max | Min", Param.Count, "Repetitions (timing)", Param.Blend, "BlendFactor of the live text (only used by the AlphaBlend op)").Also(t => { t.ControlStrip = FontDemo.Build; t.StripHeight = FontDemo.StripHeight; }).With(Param.Op, 2);
            T(GFiles, "PNG codec: ToPng -> FromPng round trip (Png.cs, no GDI+)", "Sprite.ToPng() encodes with the managed PNG encoder (auto colour type: RGBA / RGB / grey / palette, adaptive filters), Sprite.FromPng decodes it back; Count round trips per frame - compare the timing with 'ToBitmap round-trip'. The zlib level comes from the Blend slider as Blend x 10 / 256 (so 0..25 -> 0 = stored uncompressed, 26..51 -> 1 = fastest, 52..77 -> 2 = the encoder default, up to 231..255 -> 9 = smallest); the byte count and the level are in the info line. 'Smooth' ticked encodes the alpha asset (RGBA) instead of the opaque colour asset (RGB).", c =>
            {
                var src = c.Smooth ? c.A.Alpha : c.A.Color; int level = Math.Clamp(c.Blend * 10 / 256, 0, 9);
                byte[] png = Array.Empty<byte>(); Sprite? back = null;
                var sw = System.Diagnostics.Stopwatch.StartNew(); double tEnc = 0, tDec = 0;
                for (int k = 0; k < c.Count; k++)
                {
                    long t0 = sw.ElapsedTicks; png = src.ToPng(PngColor.Auto, level); long t1 = sw.ElapsedTicks;
                    back?.Dispose(); back = Sprite.FromPng(png); tEnc += t1 - t0; tDec += sw.ElapsedTicks - t1;
                }
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                c.Canvas.Draw(src, c.W / 2 - src.Width - 20, (c.H - src.Height) / 2, SR2D.Op.AlphaBlend);
                if (back != null) { c.Canvas.Draw(back, c.W / 2 + 20, (c.H - back.Height) / 2, SR2D.Op.AlphaBlend); back.Dispose(); }
                var info = Png.GetInfo(png);
                c.Info($"{png.Length:N0} bytes, {info}, zlib level {level}; encode {tEnc * ms / c.Count:0.00} ms, decode {tDec * ms / c.Count:0.00} ms per {src.Width}x{src.Height}");
                c.Canvas.DrawText(c.W / 2 - src.Width - 20, (c.H - src.Height) / 2 - 14, "source", unchecked((int)0xFFC0D0FF), 0, 1);
                c.Canvas.DrawText(c.W / 2 + 20, (c.H - src.Height) / 2 - 14, "decoded PNG", unchecked((int)0xFFC0D0FF), 0, 1);
                c.Note = $"{png.Length / 1024} KB {info?.ColorType} {info?.BitDepth}-bit";
            }).Ui(Param.Count, "Round trips per frame", Param.Blend, "zlib level (x10/256: 0 = store, 1 = fastest, 2 = default, 3+ = smallest)", Param.Smooth, "Encode the alpha asset (RGBA) instead of the colour asset (RGB)");
            T(GShapes, "FillPolygon - star (even-odd vs non-zero)", "Left: non-zero winding (solid star). Right: even-odd (pentagon hole). Smooth = AA. Op selector applies (AlphaBlend uses alpha 128).", c =>
            {
                Span<PointF> star = stackalloc PointF[5];
                var op = ShapeOp(c);
                for (int side = 0; side < 2; side++)
                {
                    float cx = c.W * (side == 0 ? 0.25f : 0.75f), R = c.S * 0.9f * c.Scale;
                    for (int i = 0; i < 5; i++) { float a = i * MathF.PI * 4 / 5 - MathF.PI / 2 + c.Time * 0.2f; star[i] = new PointF(cx + MathF.Cos(a) * R, c.Y + MathF.Sin(a) * R); }
                    for (int k = 0; k < c.Count; k++) c.Canvas.FillPolygon(star, SR2D.ARGB(128, 80, 200, 255), op, c.Smooth, side == 1, c.Blend);
                }
            }, needsPoly: true);
            T(GShapes, "Rectangles / brackets", "Count*16 rectangles: filled (alpha), border (Width = Scale*4), and a bracket (corner marks) around each. Smooth = AA; float positions when AA so sub-pixel motion is visible.", c =>
            {
                var op = c.Op == SR2D.Op.Paint ? SR2D.LineOp.AlphaBlend : ShapeOp(c);
                int n = c.Count * 16; float w = MathF.Max(1f, c.Scale * 4f);
                for (int i = 0; i < n; i++)
                {
                    float t = c.Time * 0.5f + i * 0.7f;
                    float x = c.W * 0.5f + MathF.Cos(t) * c.W * 0.35f - c.S * 0.5f, y = c.H * 0.5f + MathF.Sin(t * 1.3f) * c.H * 0.35f - c.S * 0.4f;
                    if (!c.Smooth) { x = MathF.Floor(x); y = MathF.Floor(y); }
                    c.Canvas.FillRect(x + 6, y + 6, c.S - 12, c.S * 0.8f - 12, SR2D.ARGB(96, 255, 128, 0), op, c.Smooth, c.Blend);
                    c.Canvas.DrawRect(x + 6, y + 6, c.S - 12, c.S * 0.8f - 12, SR2D.ARGB(255, 255, 255, 255), w, c.Smooth, op, c.Blend);
                    c.Canvas.DrawBracket(x, y, c.S, c.S * 0.8f, SR2D.ARGB(255, 0, 255, 128), c.S * 0.25f, w, SR2D.Corners.All, c.Smooth, op, c.Blend);
                }
            }, needsPoly: true, needsLine2: true);
            T(GShapes, "Ellipses / circles", "Count*8 filled + outlined ellipses (outline Width = Scale*6). Smooth = AA. Op selector (AlphaBlend uses alpha 140).", c =>
            {
                var op = c.Op == SR2D.Op.Paint ? SR2D.LineOp.AlphaBlend : ShapeOp(c);
                int n = c.Count * 8; float w = MathF.Max(1f, c.Scale * 6f);
                for (int i = 0; i < n; i++)
                {
                    float t = c.Time * 0.4f + i * 0.9f, rx = c.S * (0.3f + 0.2f * MathF.Sin(t * 2)), ry = c.S * 0.35f;
                    float cx = c.W * 0.5f + MathF.Cos(t) * c.W * 0.35f, cy = c.H * 0.5f + MathF.Sin(t * 0.7f) * c.H * 0.35f;
                    c.Canvas.FillEllipse(cx, cy, rx, ry, SR2D.ARGB(140, 60, 120, 255), op, c.Smooth, c.Blend);
                    c.Canvas.DrawEllipse(cx, cy, rx + w, ry + w, SR2D.ARGB(255, 255, 255, 255), w, c.Smooth, op, c.Blend);
                }
            }, needsPoly: true, needsLine2: true);
            T(GShapes, "Arrows", "Count*24 arrows from the mouse; Width = Scale*3, filled heads on even, open heads on odd arrows. Smooth = AA. Op selector (alpha 200).", c =>
            {
                var op = c.Op == SR2D.Op.Paint ? SR2D.LineOp.AlphaBlend : ShapeOp(c);
                int n = c.Count * 24; float w = MathF.Max(1f, c.Scale * 3f);
                for (int i = 0; i < n; i++)
                {
                    float a = i * MathF.PI * 2 / n + c.Time * 0.15f, len = c.S * (1.2f + 0.5f * MathF.Sin(c.Time + i));
                    int col = SR2D.ARGB(200, (byte)(128 + 127 * MathF.Sin(a)), (byte)(128 + 127 * MathF.Sin(a + 2)), (byte)(128 + 127 * MathF.Sin(a + 4)));
                    c.Canvas.DrawArrow(c.X, c.Y, c.X + MathF.Cos(a) * len, c.Y + MathF.Sin(a) * len, col, w, 0, 0, (i & 1) == 0, c.Smooth, op, c.Blend);
                }
            }, needsPoly: true, needsLine2: true);
            T(GFiles, "Vector import: SVG / EPS / PDF -> VectorImage.Draw (button 'Open file...')", "VectorImage.Load(file) parses SVG (incl. .svgz), EPS / PostScript and PDF (page = DotStep) into a resolution-independent shape list, then img.Draw(canvas, x, y, scaleX, scaleY, angle) renders it through DRAW_POLY (AA fills / strokes, gradients, clips). Built-in sample = a small SVG when no file was chosen. Drag to move; drag outside = rotate (or Angle slider); Scale = zoom (x0.01), drag the top / bottom edge = squish (ScaleY); Smooth = anti-aliasing; Blend = opacity; Brite < 0 = wireframe (strokes only); Count = copies (timing); XOR = draw the fitted whole image at the left instead of the transformed one. The note shows the parser warnings (text, raster images and patterns are not imported).", c =>
            {
                var t = L.Find(x => x.Name.StartsWith("Vector import", StringComparison.Ordinal))!;
                var (img, info) = EnsureVector(t.FilePath, c.DotStep);
                RecolorDemo.SetImage(img);                                 // the strip above edits THIS container (Recolor / SwapColors)
                var o = new VectorRenderOptions { AA = c.Smooth, Opacity = c.Blend / 255f, Strokes = true, Fills = c.Brite >= 0 };
                var osz = VectorObjectSize(c, img);                        // picture size on the canvas at Scale 1 (longer side = 1.5 x sprite)
                float fit = osz.Width / MathF.Max(1e-3f, img.Width);
                float pfx = c.PivotXF < 0 ? 0.5f : c.PivotXF / osz.Width, pfy = c.PivotYF < 0 ? 0.5f : c.PivotYF / osz.Height;   // pivot as view-box fractions
                bool cached = c.NotMask;                                   // NotMask box = draw through the raster cache (VectorSprite)
                var vs = img.Cached; vs.AA = o.AA; vs.Opacity = o.Opacity; vs.Fills = o.Fills;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int shapes = 0;
                for (int k = 0; k < c.Count; k++)
                {
                    if (c.Xor) { if (cached) vs.DrawFit(c.Canvas, new RectangleF(10, 10, c.W * 0.5f - 20, c.H - 20)); else img.DrawFit(c.Canvas, new RectangleF(10, 10, c.W * 0.5f - 20, c.H - 20), true, o); }
                    else if (cached) vs.Draw(c.Canvas, c.X + k * 6, c.Y + k * 6, fit * c.Scale, fit * c.ScaleY, c.Angle * 180f / MathF.PI, pfx, pfy);
                    else img.Draw(c.Canvas, c.X + k * 6, c.Y + k * 6, fit * c.Scale, fit * c.ScaleY, c.Angle * 180f / MathF.PI, o, pfx, pfy);
                    shapes += img.Shapes.Count;
                }
                double ms = sw.Elapsed.TotalMilliseconds;
                string how = cached ? $"VectorSprite cache: {vs.Rasterizations} rasterizations, {vs.CacheHits} blits" : "direct (tick NotMask for the raster cache)";
                string warn = img.Warnings.Count == 0 ? "" : " | " + string.Join("; ", img.Warnings.GetRange(0, Math.Min(3, img.Warnings.Count))) + (img.Warnings.Count > 3 ? $" (+{img.Warnings.Count - 3})" : "");
                c.Note = $"{info} | {img.Shapes.Count} shapes, {img.Width:0}x{img.Height:0} units{(img.PageCount > 1 ? $", page {Math.Clamp(c.DotStep, 0, img.PageCount - 1) + 1}/{img.PageCount}" : "")} | {ms:F2} ms | {how}{warn}";
                if (RecolorDemo.Status.Length > 0 || RecolorDemo.Edits > 0) c.Info($"recolor: {RecolorDemo.Status}   ({RecolorDemo.Edits} edit(s), image Version {img.Version}, palette {img.Palette(true).Count} colours)");
                if (RecolorDemo.PickMode != 0) c.Info(RecolorDemo.PickMode == 1 ? "PICK: click the picture for colour A" : "PICK: click the picture for colour B");
            }, needsPoly: true).Ui(Param.Scale, "Zoom (x0.01; the longer side = 1.5 x sprite size at x1.00); drag the top / bottom edge to squish", Param.Angle, "Rotation (deg) - or drag outside the image", Param.Smooth, "Anti-aliased edges", Param.Blend, "Opacity", Param.Brite, "< 0: strokes only (wireframe)", Param.Count, "Copies per frame", Param.DotStep, "PDF page (0-based)", Param.Xor, "Fit the whole image into the left half instead", Param.NotMask, "Raster cache (VectorSprite): rasterise once, blit until the zoom / angle / content changes", Param.Mouse, "Drag = move; drag outside = rotate; edges / corners = resize (Shift = keep aspect); right click = pivot").Range(Param.DotStep, 0, 32).With(Param.Blend, 255).With(Param.Smooth, true);
            L[L.Count - 1].MouseRotates = true; L[L.Count - 1].Resizable = true; L[L.Count - 1].AnimSpin = 0f;
            L[L.Count - 1].ObjectSize = c => { var vt = L.Find(x => x.Name.StartsWith("Vector import", StringComparison.Ordinal))!; var (vi, _) = EnsureVector(vt.FilePath, c.DotStep); return VectorObjectSize(c, vi); };
            L[L.Count - 1].ControlStrip = RecolorDemo.Build; L[L.Count - 1].StripHeight = RecolorDemo.StripHeight;
            // pick mode: a click on the picture (object units = picture units x fit) picks the colour under it
            L[L.Count - 1].ObjectClick = (c, ox, oy) =>
            {
                if (RecolorDemo.PickMode == 0) return false;
                var vt = L.Find(x => x.Name.StartsWith("Vector import", StringComparison.Ordinal))!; var (vi, _) = EnsureVector(vt.FilePath, c.DotStep);
                var osz = VectorObjectSize(c, vi); float fit = osz.Width / MathF.Max(1e-3f, vi.Width);
                RecolorDemo.PickAt(new PointF(vi.ViewBox.X + ox / fit, vi.ViewBox.Y + oy / fit));
                return true;
            };
            L[L.Count - 1].FileFilter = "Vector files|*.svg;*.svgz;*.eps;*.ps;*.ai;*.pdf|SVG (*.svg, *.svgz)|*.svg;*.svgz|PostScript / EPS (*.eps, *.ps, *.ai)|*.eps;*.ps;*.ai|PDF (*.pdf)|*.pdf|All files|*.*";

            // ===================================================== Animated SVG (SMIL)
            T(GFiles, "Animated SVG (SMIL + CSS): FrameAt / SeekToTime (button 'Open file...')", "VectorImage keeps the animation of a file while importing: SMIL tracks (<animate> of the d attribute, <animateTransform>, <animateMotion>, <set>) AND CSS animations (@keyframes of transform / opacity / stroke-dashoffset with animation-* timing, transform-box / transform-origin - the style AI-generated game art uses). SeekToTime(t) applies every track at time t (paths morph per command, transforms interpolate per function - a 0 to 360 deg spin sweeps), FrameAt(t) returns an independent frame copy, Duration / FrameCount / FrameRate / GetFrame(i) iterate the loop (FrameCount = Duration * 30 fps here). Built-in sample = a small loop when no file was chosen. 'Animate' plays the loop (the clock runs while the check box is ticked; off = frame 0); the playback rate is Speed / 6 (6 = real time). Drag to move; drag outside = rotate; Scale = zoom; Smooth = anti-aliased edges; Blend = opacity; Brite < 0 = wireframe. ClipViewport is on here: decoration the file parks beyond its canvas stays out, like in a browser. The draw goes through the layer compositor (img.Cached): content whose chain only moves is rasterised once per zoom and blitted with the chain's current matrix (slow dollies re-rasterise only when the linear part drifts); morphs / opacity / dashes stay live. Tick 'Precompose' to bake the whole loop into bitmaps once. Playback then evaluates no vector tracks: one bitmap draw per tick. First use and changes to internal resolution / fps / paint settings require a synchronous bake (can take a few seconds). Display zoom / rotation / skew only transform bitmaps and never rebake. The film budget is in MiB; if full-size frames do not fit, smaller bitmaps are baked and enlarged, without silently lowering fps. Film width fixes the internal bitmap grid independently of container size (0 = SVG intrinsic); a higher grid than the display enables area-filtered supersampling. Film fps defaults to 60 here (0 = file FrameRate, usually 30; up to 240 for high-refresh displays). Live vectors sample every render; a 30 fps film holds each sample for 33 ms, so image resolution alone cannot fix temporal choppiness. The note reports actual fps, bitmap size, resolution percentage, memory and bake time. The note shows the track count, the seek + draw cost and the cache stats.", c =>
            {
                var t = L.Find(x => x.Name.StartsWith("Animated SVG", StringComparison.Ordinal))!;
                var (img, info) = EnsureAnimVector(t.FilePath);
                var o = new VectorRenderOptions { AA = c.Smooth, Opacity = c.Blend / 255f, Strokes = true, Fills = c.Brite >= 0 };
                var osz = VectorObjectSize(c, img);                        // picture size on the canvas at Scale 1 (longer side = 1.5 x sprite)
                float fit = osz.Width / MathF.Max(1e-3f, img.Width);
                float pfx = c.PivotXF < 0 ? 0.5f : c.PivotXF / osz.Width, pfy = c.PivotYF < 0 ? 0.5f : c.PivotYF / osz.Height;
                double tt = img.Duration <= 0 ? 0 : (c.Time * Math.Max(0, c.Speed) / 6.0) % img.Duration;   // Animate off = 0 = frame 0
                var vs = img.Cached;
                vs.AA = o.AA; vs.Strokes = o.Strokes; vs.Fills = o.Fills; vs.Opacity = o.Opacity;
                vs.Precomposed = c.Xor;                    // checkbox = bitmap loop instead of the live layer compositor
                vs.MaxFilmPixels = (long)c.Count * 1024 * 1024 / 4;  // the demo budget slider is MiB (ARGB = 4 bytes / pixel)
                vs.FilmFrameRate = c.DotStep;              // 60 by default here; 0 = img.FrameRate; never silently lowered
                vs.FilmResolution = new Size(c.Z, 0);      // internal width (height follows SVG aspect); NOT the displayed scale
                vs.SubPixel = c.Smooth;                    // filtered fractional placement; no pixel snapping while dragging
                var sw = System.Diagnostics.Stopwatch.StartNew();
                vs.DrawAt(c.Canvas, tt, c.X, c.Y, fit * c.Scale, fit * c.ScaleY, c.Angle * 180f / MathF.PI, pfx, pfy);
                double ms = sw.Elapsed.TotalMilliseconds;
                string warn = img.Warnings.Count == 0 ? "" : " | " + string.Join("; ", img.Warnings.GetRange(0, Math.Min(3, img.Warnings.Count))) + (img.Warnings.Count > 3 ? $" (+{img.Warnings.Count - 3})" : "");
                int playedFrames = vs.Precomposed && vs.FilmActive ? vs.FilmFrames : img.FrameCount;
                int playedIndex = vs.Precomposed && vs.FilmActive && img.Duration > 0
                    ? Math.Clamp((int)Math.Floor(tt * playedFrames / img.Duration + 1e-9), 0, playedFrames - 1) + 1
                    : (int)(tt * img.FrameRate) + 1;
                c.Note = $"{info} | t = {tt:0.###} / {img.Duration:0.###} s = frame {playedIndex}/{playedFrames} | {img.Tracks.Count} tracks, {img.Shapes.Count} shapes | frame {ms:F2} ms | {vs.LayerSummary}{warn}";
            }, needsPoly: true).Ui(Param.Scale, "Zoom (x0.01; the longer side = 1.5 x sprite size at x1.00); drag the top / bottom edge to squish", Param.Time, "Animate: play the loop (off = stand at frame 0)", Param.Speed, "Playback rate = Speed / 6 (6 = real time, 12 = double speed)", Param.Smooth, "Anti-aliased edges", Param.Blend, "Opacity", Param.Brite, "< 0: strokes only (wireframe)", Param.Xor, "Precompose (first draw bakes the loop; off releases the bitmaps)", Param.Count, "Film budget (MiB; smaller bitmaps if necessary, never fewer fps)", Param.DotStep, "Film fps (default 60; 0 = file rate; higher = smoother motion / more memory)", Param.Z, "Internal film width (px; 0 = SVG intrinsic; fixed while resizing the container)", Param.Mouse, "Drag = move; drag outside = rotate; edges / corners = resize (Shift = keep aspect); right click = pivot").Range(Param.Count, 16, 1024).Range(Param.DotStep, 0, 240).Range(Param.Z, 0, 4096).With(Param.Count, 256).With(Param.DotStep, 60).With(Param.Z, 512).With(Param.Xor, false).With(Param.Smooth, true).With(Param.Speed, 6);
            L[L.Count - 1].MouseRotates = true; L[L.Count - 1].Resizable = true; L[L.Count - 1].AnimSpin = 0f;
            L[L.Count - 1].ObjectSize = c => { var vt = L.Find(x => x.Name.StartsWith("Animated SVG", StringComparison.Ordinal))!; var (vi, _) = EnsureAnimVector(vt.FilePath); return VectorObjectSize(c, vi); };
            L[L.Count - 1].FileFilter = "Animated SVG|*.svg;*.svgz|SVG (*.svg, *.svgz)|*.svg;*.svgz|All files|*.*";

            // ===================================================== Effects (blur)
            // radius = Scale slider * 8 (x1.00 -> 8 px, x4 -> 32 px), strength = Blend slider
            static int BlurR(Ctx c) => (int)MathF.Round(c.Scale * 8f);
            T(GEffects, "DrawBlurred (Op selector, Scale = radius, Blend = strength)", "Sprite.DrawBlurred: the sprite drawn through a Gaussian-like blur whose soft edge runs 3*radius beyond the sprite rect. Radius = Scale*8 px, opacity = Blend factor; 'Smooth' off = Fast (2-pass) mode. Cost does not depend on the radius, only on the padded area.", c =>
            {
                int r = BlurR(c);
                Grid(c, (x, y) => c.Canvas.DrawBlurred(SrcFor(c, c.Op), x, y, r, c.Op, c.Blend + (c.Blend >> 7), Opaque: c.Op != SR2D.Op.AlphaBlend && c.Op != SR2D.Op.AlphaOver, Fast: !c.Smooth));
            }, needsBlur: true);
            T(GEffects, "Drop shadow: by hand (DrawBlurred)  vs  Effects.ShadowAt (one stage)", "The same Photoshop-style drop shadow of the KEYED sprite (a hard-edged shape, so the shadow reads clearly), side by side. LEFT - the old recipe: a black copy carrying the shape's alpha is drawn blurred by DrawBlurred (radius Scale*8, Op.AlphaBlend, opacity Blend), offset by Distance px (Z slider) in direction Angle, then the sharp sprite on top (Draw, AlphaTest). RIGHT - the effects pipeline: ONE ShadowAt stage (blur Scale*4 px, spread 'Smooth' ? 0 : 2 px) on the same sprite - the shadow is generated, blurred, offset and composited under the sprite inside the DrawFx call, and any stage added after it (a distortion, a colour step) would apply to sprite and shadow together. NotMask = ShadowOnlyAt on the right half (all shadows of a scene first, then the sprites; the left half ignores it). The MOUSE moves the shadow of both halves: dragging sets Distance and Angle from the vector between the CANVAS CENTRE (the seam between the two halves - the red arrow drawn there is that vector) and the pointer (the light comes from the opposite side). Light backdrop so the shadow shows.",
                c =>
                {
                    LightBackdrop(c);
                    int r = BlurR(c), rFx = Math.Max(1, r / 2);
                    Sprite sh = Shadow(c.A.Keyed);       // black copy carrying the keyed sprite's shape (cached)
                    int ox = (int)MathF.Round(MathF.Cos(c.Angle) * c.Z), oy = (int)MathF.Round(MathF.Sin(c.Angle) * c.Z);
                    int half = c.W / 2, x0 = half / 2 - c.S / 2, y0 = c.H / 2 - c.S / 2, x1 = half + half / 2 - c.S / 2;
                    c.Canvas.DrawBlurred(sh, x0 + ox, y0 + oy, r, SR2D.Op.AlphaBlend, c.Blend);
                    c.Canvas.Draw(c.A.Keyed, x0, y0, SR2D.Op.AlphaTest);
                    float deg = c.Angle * 180 / MathF.PI;
                    fx.Clear();
                    if (c.NotMask) fx.ShadowOnlyAt(deg, c.Z, rFx, unchecked((int)0xFF000000), c.Blend / 255f, c.Smooth ? 0 : 2);
                    else fx.ShadowAt(deg, c.Z, rFx, unchecked((int)0xFF000000), c.Blend / 255f, c.Smooth ? 0 : 2);
                    c.Canvas.DrawFx(c.A.Keyed, x1, y0, fx, SR2D.Op.AlphaTest);
                    c.Label(8, 4, "by hand: DrawBlurred"); c.Label(half + 8, 4, "Effects.ShadowAt");
                    c.Note = $"distance {c.Z} px, angle {deg:0} deg, blur {r} / {rFx} px, opacity {c.Blend}{(c.NotMask ? ", shadow only (right)" : "")}";
                }, needsBlur: true, needsFx: true, clears: true).Ui(Param.Z, "Distance (px)", Param.Angle, "Direction (deg)", Param.Scale, "Blur radius (x8 px left, x4 right)", Param.Blend, "Shadow opacity", Param.Smooth, "Right: no spread (off = +2 px)", Param.NotMask, "Right half: shadow only (ShadowOnlyAt)", Param.Mouse, "Drag = shadow distance + direction").With(Param.Z, 12).With(Param.Angle, 45).With(Param.Scale, 50).With(Param.Blend, 160).Range(Param.Z, 0, 200);
            L[L.Count - 1].ShadowByMouse = true;
            T(GEffects, "Glow: by hand (DrawBlurred, Op.Add)  vs  Effects.Glow (one stage)", "The same additive gold glow two ways. LEFT - the old recipe: DrawBlurred(colour sprite, radius Scale*8, Op.Add, Opaque) under the sharp colour sprite (Paint, offset a quarter sprite down-right); its spread is the blur radius, so Brite does nothing there. RIGHT - the effects pipeline: ONE Glow stage (radius Scale*4 px, spread Brite*3 px, intensity Blend/255 with an animated pulse) around the alpha sprite, drawn with DrawFx - the glow is generated from the sprite's shape inside the call. 'Smooth' off = Fast (2-pass) blur on both sides.",
                c =>
                {
                    int r = BlurR(c), rFx = Math.Max(1, r / 2);
                    int half = c.W / 2, x0 = half / 2 - c.S / 2, y0 = c.H / 2 - c.S / 2, x1 = half + half / 2 - c.S / 2;
                    c.Canvas.DrawBlurred(c.A.Color, x0, y0, r, SR2D.Op.Add, c.Blend + (c.Blend >> 7), Opaque: true);
                    c.Canvas.Draw(c.A.Color, x0 + c.S / 4, y0 + c.S / 4, SR2D.Op.Paint);
                    float pulse = 0.75f + 0.25f * (float)Math.Sin(c.Time * 4);
                    fx.Clear().Glow(rFx, unchecked((int)0xFFFFD700), c.Blend / 255f * pulse, Math.Max(0, (int)(c.Brite * 3)), Fast: !c.Smooth);
                    c.Canvas.DrawFx(c.A.Alpha, x1, y0, fx, SR2D.Op.AlphaBlend);
                    c.Label(8, 4, "by hand: DrawBlurred + Add"); c.Label(half + 8, 4, "Effects.Glow");
                    c.Note = $"blur {r} / {rFx} px, intensity {c.Blend}, spread {Math.Max(0, (int)(c.Brite * 3))} px (right)";
                }, needsBlur: true, needsFx: true).Ui(Param.Scale, "Blur radius (x8 px left, x4 right)", Param.Blend, "Glow intensity", Param.Brite, "Spread of Effects.Glow (x3 px)", Param.Smooth, "Fast blur (off = 2-pass)", Param.Time, "Animate: the right glow pulses");
            // ---- effect chains (DRAW_FX). The chain is rebuilt every frame (cheap: a list of structs).
            // One Effects object PER THREAD (see Fx below): under DrawParallel every band runs this
            // callback concurrently, and mutating one shared chain from several threads is a race
            // (bands would see each other's half-built stage lists - visible as flicker).
            T(GEffects, "Effects: Wave distortion (Scale = wavelength, Blend = strength, animated)", "new Effects().Wave(wavelength Scale*16, strength Blend/16 px, phase = time*4). 'Smooth' off = Longitudinal (compression) wave; Angle = travel direction. Drawn 1:1 with the Op selector.", c =>
            {
                fx.Clear().Wave(c.Scale * 16f, c.Blend / 16f, c.Time * 4f, c.Angle, Longitudinal: !c.Smooth);
                Grid(c, (x, y) => c.Canvas.DrawFx(SrcFor(c, c.Op), x, y, fx, c.Op, Opaque: c.Op != SR2D.Op.AlphaBlend && c.Op != SR2D.Op.AlphaOver));
            }, needsFx: true);
            T(GEffects, "Effects: Ripple from the cursor (Scale = wavelength, Blend = strength)", "Effects.Ripple centred on the mouse, falloff Scale*100 px, animated phase. The colour picture is enlarged to the canvas height in the middle of the canvas with DrawFxScaled + Post mode: the stages then run AFTER the scale, at screen resolution, so one continuous ring system sits under the cursor and stays put while the picture moves. Count = extra copies of the alpha sprite in a row along the top edge (timing): a plain 1:1 DrawFx ignores Post and reads the ripple centre in SPRITE pixels, so each copy is given the centre minus its own position - the rings of all copies still land on the same screen point as the big picture's.", c =>
            {
                fx.Clear().Ripple(c.Scale * 12f, c.Blend / 12f, c.Time * 6f, c.X, c.Y, c.Scale * 100f);
                fx.Post = true;
                int big = Math.Min(c.H - 16, c.W - 16);
                c.Canvas.DrawFxScaled(c.A.Color, (c.W - big) / 2, (c.H - big) / 2, big, big, fx, SR2D.Op.Paint, Opaque: true);
                int per = Math.Max(1, c.W / (c.S + 4));
                for (int i = 1; i < c.Count; i++)
                {   // 1:1 draws ignore Post: shift the centre into this copy's sprite frame so the ring stays on the same pixel
                    int x = 4 + ((i - 1) % per) * (c.S + 4), y = 4;
                    fx.Clear().Ripple(c.Scale * 12f, c.Blend / 12f, c.Time * 6f, c.X - x, c.Y - y, c.Scale * 100f);
                    c.Canvas.DrawFx(c.A.Alpha, x, y, fx, SR2D.Op.AlphaBlend);
                }
            }, needsFx: true, clears: false).Ui(Param.Mouse, "Ripple centre (follow the mouse or drag)", Param.Scale, "Wavelength x12 px, falloff x100 px", Param.Blend, "Strength (Blend / 12 px)", Param.Count, "1 = the big picture only; more = the 1:1 row along the top (timing)", Param.Time, "Animate: the rings travel outwards");
            T(GEffects, "Effects: Noise | Turbulence wobble (Scale = feature size, Blend = strength)", "Left: Effects.Noise, right: Effects.Turbulence (3 octaves), both animated by phase = time*0.5, feature size Scale*24 px, strength Blend/16 px. 'Smooth' off = nearest sampling inside the distortion.", c =>
            {
                var samp = c.Smooth ? SR2D.Filter.Bilinear : SR2D.Filter.Nearest;
                fx.Clear().Noise(c.Scale * 24f, c.Blend / 16f, c.Time * 0.5f, samp);
                c.Canvas.DrawFx(c.A.Alpha, c.X - c.S, c.Y - c.S / 2, fx, SR2D.Op.AlphaBlend);
                fx.Clear().Turbulence(c.Scale * 24f, c.Blend / 16f, c.Time * 0.5f, samp);
                c.Canvas.DrawFx(c.A.Alpha, c.X + 8, c.Y - c.S / 2, fx, SR2D.Op.AlphaBlend);
            }, needsFx: true);
            T(GEffects, "Effects: DistortMap (tile sprite as height map, scrolling)", "Effects.DistortMap(tile, scale Scale, strength Blend/16 px) with the map scrolled by time - a refraction / glass-block look. The tile's luminance is the height; the gradient pushes pixels.", c =>
            {
                fx.Clear().DistortMap(c.A.Tile, c.Scale, c.Blend / 16f, c.Time * 20f, c.Time * 12f);
                Grid(c, (x, y) => c.Canvas.DrawFx(SrcFor(c, c.Op), x, y, fx, c.Op, Opaque: c.Op != SR2D.Op.AlphaBlend && c.Op != SR2D.Op.AlphaOver));
            }, needsFx: true);
            T(GEffects, "Effects: colour (Brite = brightness, Scale = contrast, Angle = hue, Blend = opacity)", "One Effects.Color stage: brightness Brite/2 (ADDITIVE -1..+1, so 0 = unchanged and 200 = a white-out), contrast Scale, hue rotation Angle, opacity Blend/255; 'Smooth' off = greyscale; NotMask = invert. Everything is done in one pass at draw time - the sprite is untouched. The start values show the colour half of the stage (Saturation 1, hue 60 deg, neutral brightness and contrast, full opacity) - with the generic Smooth off the picture is a greyscale copy and the hue slider looks dead.", c =>
            {
                fx.Clear().Color(Brightness: c.Brite * 0.5f, Contrast: c.Scale, Saturation: c.Smooth ? 1f : 0f, Hue: c.Angle * 57.29578f, Opacity: c.Blend / 255f, Invert: c.NotMask);
                Grid(c, (x, y) => c.Canvas.DrawFx(SrcFor(c, c.Op), x, y, fx, c.Op, Opaque: c.Op != SR2D.Op.AlphaBlend && c.Op != SR2D.Op.AlphaOver));
            }, needsFx: true).Ui(Param.Brite, "Brightness Brite/2, additive -1..+1 (0 = unchanged)", Param.Scale, "Contrast (x0.01, 100 = unchanged)", Param.Angle, "Hue rotation (deg)", Param.Blend, "Opacity (Blend / 255)", Param.Smooth, "Keep the colour (off = greyscale)", Param.NotMask, "Invert", Param.Op, "Blend op").With(Param.Smooth, true).With(Param.Angle, 60).With(Param.Brite, 0).With(Param.Blend, 255);
            T(GEffects, "Effects: motion blur (Op = mode: straight / curve / echo / taps)", "Four modes (the Op selector), all on the Lenna photo (Assets.Color): STRAIGHT - MotionBlur(Angle degrees, Scale px): the picture averaged along a straight trail, the last tap Scale px away in the Angle direction (0 = right, 90 = down; up to 400 px). CURVE - MotionBlurPath over the trail curve EDITED IN THE STRIP ABOVE: drag the points (a click on empty space adds, right click selects one and opens its menu, double click switches the tangent type), the curve bends the trail perpendicular to the Angle direction, the Blend slider sets how much of the bend applies (0 = straight trail), Scale = trail length - the trail follows the curve, Angle rotates the whole thing. The strip opens on an S-bend so the curve mode differs from the straight one at once; RESET puts the trail back on the flat centre line (then CURVE looks exactly like STRAIGHT). Both of those RESAMPLE every pixel of the sprite-plus-trail image once per tap, which is what makes them exact and slow: fine for an editor, for baking the blur into a sprite, not for a frame loop. ECHO - the feedback version (MotionEcho): no per-frame resampling, a persistent accumulator fades by Persistence (Blend / 255 * 0.97) and the new frame is drawn over it, so the cost per frame is two whole-surface passes at ANY trail length; tick Animate, because a still object just saturates. TAPS - Sprite.DrawMotionTaps: the same curve trail, covered by as many whole copies of the sprite as the Taps slider says and AVERAGED in one reused surface, so the work is sprite-sized instead of trail-sized and the picture is deterministic (a still object and a screenshot look right, at any frame rate, which the echo is not). The info line prints the pixels each way touches for one sprite; the real answer is the FPS / render-ms line the demo writes under the info text - tick Animate and switch CURVE to TAPS to watch that figure fall (measured here for a 256 px sprite on a 400 px trail: 220 - 290 ms resampled against 1.4 ms for 8 taps).", c =>
            {
                float deg = c.Angle * 180f / MathF.PI;        // the bench hands over radians, both blur APIs take degrees
                float len = Math.Max(1f, c.Scale * 100f);     // the Scale slider is the trail length in px (5..400)
                if ((int)c.Op == 3)
                {   // real-time echo: object layer into a scratch, the echo accumulates, the accumulator goes to the canvas.
                    // Bands (Parallel) render the same frame: the Step is keyed on the animation time so it runs once.
                    lock (echoLock)
                    {
                        if (mecho == null || mecho.Accumulator.Width != c.W || mecho.Accumulator.Height != c.H) { mecho?.Dispose(); mecho = new MotionEcho(c.W, c.H); }
                        if (esrc == null || esrc.Width != c.W || esrc.Height != c.H) { esrc?.Dispose(); esrc = new Sprite(c.W, c.H); }
                        mecho.Persistence = c.Blend / 255f * 0.97f;
                        esrc.ClearBuffer(0);
                        int w = Math.Clamp((int)(c.S * 0.25f * c.Scale), 8, Math.Min(c.W, c.H) / 2);   // echo: Scale is the object size
                        float x = c.W / 2 + MathF.Sin(c.Time * 1.7f) * c.W * 0.32f, y = c.H / 2 + MathF.Sin(c.Time * 2.3f) * c.H * 0.27f;
                        esrc.DrawRotate2(c.A.Color, (int)x, (int)y, c.Time * 1.1f, w, w, -1, -1, SR2D.Op.Paint);
                        if (c.Time != lastEchoTime) { lastEchoTime = c.Time; mecho.Step(esrc); }
                        c.Canvas.Draw(mecho.Accumulator, 0, 0, SR2D.Op.AlphaBlend);
                        c.Info($"echo persistence {mecho.Persistence:0.00} (Blend)  object {w} px at {x:0},{y:0}  trail = feedback history");
                        c.Note = "tick Animate - the echo accumulates frames; a still object saturates to a still picture";
                    }
                    return;
                }
                if ((int)c.Op == 4)
                {   // Real-time multi-tap (Sprite.DrawMotionTaps): the sprite is copied Taps times along the
                    // SAME curve trail the curve mode uses and the copies are averaged in one reused surface.
                    // The numbers are the pixels each algorithm touches for ONE sprite - the resampled blur
                    // re-samples the whole sprite-plus-trail image per tap, the taps work is sprite-sized - and
                    // they are what the two modes cost differently, which is why this one runs at frame rate.
                    int taps = Math.Clamp((int)c.Grid, 2, 32);
                    var path = MotionDemo.Path(deg, len, c.Blend / 255f);
                    int pminX = 0, pmaxX = 0, pminY = 0, pmaxY = 0;
                    for (int k = 0; k < path.Length; k++)
                    {
                        pminX = Math.Min(pminX, (int)path[k].X); pmaxX = Math.Max(pmaxX, (int)path[k].X);
                        pminY = Math.Min(pminY, (int)path[k].Y); pmaxY = Math.Max(pmaxY, (int)path[k].Y);
                    }
                    long accArea = (long)(c.S + pmaxX - pminX) * (c.S + pmaxY - pminY);       // the average surface
                    long tapWork = (taps + 3L) * c.S * c.S + accArea;                          // blits, scaling, composite
                    fx.Clear().MotionBlur(deg, len);
                    int m = fx.Margin;
                    long blurTaps = Math.Clamp((int)MathF.Ceiling(len) + 1, 2, 32);
                    long resampled = blurTaps * (long)(c.S + 2 * m) * (c.S + 2 * m);
                    Grid(c, (x, y) => c.Canvas.DrawMotionTaps(c.A.Color, x, y, path, taps));
                    c.Info($"{taps} taps over a {c.S} px sprite = {tapWork / 1000}k px touched; the resampled blur needs {blurTaps} taps x {(c.S + 2 * m)}² = {resampled / 1000}k px ({resampled / Math.Max(1, tapWork):0.0}x more) - same trail, same curve, but see the note");
                    c.Note = "a tap is a whole copy: few taps on a long trail show separate ghosts, not a smooth sweep";
                    return;
                }
                if ((int)c.Op == 2)
                {   // the trail follows the curve edited in the strip; Angle rotates the whole path, Blend applies the bend
                    fx.Clear().MotionBlurPath(MotionDemo.Path(deg, len, c.Blend / 255f), 1f);
                    c.Info($"curve trail {len:0} px, direction {deg:0} deg, bend {c.Blend / 255f * 100:0}% - edit the curve in the strip above");
                }
                else
                {   // straight trail: Angle = direction, Scale = length in px (the old 32 px cap is gone)
                    fx.Clear().MotionBlur(deg, len);
                    c.Info($"straight trail {len:0} px towards {deg:0} deg (0 = right, 90 = down)");
                }
                Grid(c, (x, y) => c.Canvas.DrawFx(c.A.Color, x, y, fx, SR2D.Op.AlphaBlend));
            }, needsFx: true).Ui(Param.Op, "Motion mode (the editor strip ABOVE the canvas bends the CURVE and TAPS trail)", Param.Angle, "Trail direction in degrees (straight / curve / taps)", Param.Scale, "Trail length px (straight / curve / taps) / object size (echo)", Param.Blend, "Curve bend % (curve / taps) / echo persistence (echo)", Param.Grid, "Taps in the trail (taps mode: 8 .. 32, above 32 the engine stops)").Ops("Straight", "Curve", "Echo", "Taps").With(Param.Op, 1).With(Param.Scale, 48).With(Param.Blend, 230).With(Param.Grid, 8).Also(t => { t.ControlStrip = MotionDemo.Build; t.StripHeight = MotionDemo.StripHeight; });
            T(GEffects, "Effects: outline (Scale = thickness, Angle = hue) | Dilate / Erode", "Three columns, Count rows. Left: the plain alpha sprite. Middle: Effects.Outline(thickness Scale*3 px, colour from Angle) - a solid border around the shape, sprite on top. Right: 'Smooth' on = Dilate(Scale*3) (the shape grows, colours spread outward), off = Erode(Scale*3) (the shape shrinks, thin parts vanish). Drag moves the grid.", c =>
            {
                int t = Math.Max(1, (int)MathF.Round(c.Scale * 3));
                int col = SR2D.ARGB(255, (byte)(127 + 127 * Math.Sin(c.Angle)), (byte)(127 + 127 * Math.Sin(c.Angle + 2.1)), (byte)(127 + 127 * Math.Sin(c.Angle + 4.2)));
                fx.Clear().Outline(t, col);
                var fx2 = tfx2 ??= new Effects(); fx2.Clear(); if (c.Smooth) fx2.Dilate(t); else fx2.Erode(t);
                int colw = c.S + 2 * t + 16, rows = Math.Max(1, Math.Min(c.Count, (c.H + c.S) / (c.S + 16) + 1));
                int x0 = c.X - c.S / 2, y0 = c.Y - c.S / 2;
                for (int i = 0; i < rows; i++)
                {
                    int y = y0 + i * (c.S + 16);
                    if (y >= c.H) break;
                    c.Canvas.Draw(c.A.Alpha, x0, y, SR2D.Op.AlphaBlend);
                    c.Canvas.DrawFx(c.A.Alpha, x0 + colw, y, fx, SR2D.Op.AlphaBlend);
                    c.Canvas.DrawFx(c.A.Alpha, x0 + 2 * colw, y, fx2, SR2D.Op.AlphaBlend);
                }
            }, needsFx: true);
            T(GEffects, "Effects: one chain, stages toggled at run time (Enable / Disable)", "The chain Shadow -> Blur -> Wave -> Colour is built every frame (a list of structs, cheap); every second a different subset of it is enabled with fx.Enable(i, bool) - the stage list never changes, a disabled stage costs nothing and adds no margin, so the frame grows and shrinks with the subset. Stage 0 (shadow) always stays on so the picture is never empty. Blend = opacity of the colour stage. 'Smooth' forces all four stages on (the whole chain, the expensive frame).", c =>
            {
                fx.Clear().Shadow(6, 6, 6).Blur(3).Wave(24, 4, c.Time * 4f).Color(Opacity: c.Blend / 255f);
                int phase = ((int)c.Time) & 15;
                var en = new bool[4];
                for (int i = 0; i < 4; i++) en[i] = c.Smooth || i == 0 || ((phase >> i) & 1) != 0;
                for (int i = 0; i < 4; i++) fx.Enable(i, en[i]);
                string on = ""; for (int i = 0; i < 4; i++) on += en[i] ? "SBWC"[i] : "-";
                c.Note = $"second {phase} - stages on: {on}  (S shadow, B blur, W wave, C colour)";
                Grid(c, (x, y) => c.Canvas.DrawFx(c.A.Alpha, x, y, fx, SR2D.Op.AlphaBlend));
            }, needsFx: true).Ui(Param.Blend, "Colour stage opacity (Blend / 255)", Param.Smooth, "Force all four stages on (off = a different subset each second)", Param.Time, "Animate: the subset advances with time");
            T(GEffects, "Effects: DrawTransparent (Blend = opacity)", "Sprite.DrawTransparent(src, x, y, Blend/255): the sprite drawn with a global transparency, alpha channel untouched. Count copies.", c =>
            {
                Grid(c, (x, y) => c.Canvas.DrawTransparent(SrcFor(c, c.Op), x, y, c.Blend / 255f, c.Op));
            }, needsFx: true);
            T(GEffects, "Effects: chain Blur -> Noise -> Colour, rotated + scaled (PRE / POST)", "One call: Effects().Blur(Scale*4).Noise(20, 4, time).Color(saturation 1.5, opacity Blend). Drawn with DrawFxRotated (Angle, Scale) - 'Smooth' on = Post mode (effects at screen resolution: blur/noise in screen px, pattern fixed to the screen), off = Pre (effects on the sprite, then transformed).", c =>
            {
                fx.Clear().Blur((int)(c.Scale * 4)).Noise(20f, 4f, c.Time * 0.7f).Color(Saturation: 1.5f, Opacity: c.Blend / 255f);
                fx.Post = c.Smooth;
                int w = (int)(c.S * c.Scale), h = (int)(c.S * c.Scale);
                for (int i = 0; i < c.Count; i++) c.Canvas.DrawFxRotated(c.A.Alpha, c.X + i * 7, c.Y + i * 5, c.Angle, fx, w, h, Filter: SR2D.Filter.Bilinear);
            }, needsFx: true, warp: true);
            T(GEdit, "FloodFill / Selection: click to bucket-fill (Blend = tolerance, Smooth = soft edge, NotMask = global, XOR = diagonal)", "Scene of flat shapes over a gradient. Every frame: FloodFill at the mouse with tolerance Blend/4 (0..63; the background is a gradient of ~1 level per 5 rows, so a tolerance of t selects a band of ~5t rows around the click - and above ~15 it also squeezes through the anti-aliased notch where two lines cross), colour from Angle, Op selector (Paint = Set, AlphaBlend, Add...). 'Smooth' = Soft (anti-aliased edge on gradients), 'NotMask' = Contiguous off (replace the colour everywhere), 'XOR lines' = Diagonal (8-connected: the region may continue across a pixel corner). Then the same region as a Selection: canvas.Apply(sel, hue shift) inside it and sel.Draw(canvas, time) - blue tint + marching ants along the edge (edge pixels cached until the selection changes). Count = number of fills per frame (the note sums their pixels). Note that the BLEND slider does double duty here: Blend/4 is the tolerance AND Blend is the BlendFactor the fill is mixed with when Op = AlphaBlend / Blend.", c =>
            {
                Sprite scene;
                lock (sceneLock)
                {   // the scene must match the canvas: rebuild it when the window (and so the canvas) was resized
                    if (tscene == null || tscene.Width != c.W || tscene.Height != c.H) { tscene?.Dispose(); tscene = BuildScene(c.W, c.H); }
                    scene = tscene;
                }
                c.Canvas.Draw(scene, 0, 0, SR2D.Op.Paint);
                int col = SR2D.ARGB(c.Op == SR2D.Op.AlphaBlend ? (byte)160 : (byte)255, (byte)(127 + 127 * Math.Sin(c.Angle)), (byte)(127 + 127 * Math.Sin(c.Angle + 2.1)), (byte)(127 + 127 * Math.Sin(c.Angle + 4.2)));
                var op = c.Op switch { SR2D.Op.AlphaBlend => SR2D.LineOp.AlphaBlend, SR2D.Op.Add => SR2D.LineOp.Add, SR2D.Op.Max => SR2D.LineOp.Max, SR2D.Op.Min => SR2D.LineOp.Min, SR2D.Op.Blend => SR2D.LineOp.Blend, _ => SR2D.LineOp.Set };
                int n = 0;
                int tol = c.Blend / 4;
                for (int k = 0; k < c.Count; k++) n += c.Canvas.FloodFill(c.X, c.Y, col, tol, !c.NotMask, op, Diagonal: c.Xor, Soft: c.Smooth, BlendFactor: c.Blend);
                var sel = tsel; if (sel == null || sel.Width != c.W || sel.Height != c.H) { sel?.Dispose(); sel = tsel = new Selection(c.W, c.H); }
                int m = sel.Wand(scene, c.X, c.Y, tol, Contiguous: !c.NotMask, Diagonal: c.Xor, Soft: c.Smooth);
                var b = sel.Bounds;
                if (m > 0)
                {
                    c.Canvas.Apply(sel, v => v.DrawFx(scene, 0, 0, tfxSel ??= new Effects().Color(Hue: 120, Saturation: 1.4f), SR2D.Op.Paint));
                    sel.Draw(c.Canvas, c.Time * 24, Tint: 0x4000A0FF);          // tint + marching ants along the real edge
                }
                c.Note = $"filled {n} px, tolerance {tol}{(c.Xor ? ", 8-connected" : "")}, bounds {b.Width}x{b.Height} at {b.X},{b.Y}, edge {sel.EdgeCount} px";
            }, needsPoly: true, needsFx: true, needsFlood: true, clears: true);
            T(GEdit, "Move: grid shuffle + pen paint (Sprite.Move)", "Lenna (512, centred) is cut into a Grid x Grid block puzzle and ONE block is erased. Every step: a RANDOM block adjacent to the hole is selected (never the block that just moved in) and ANIMATED into the hole with Sprite.Move - the Photoshop move tool inside one sprite: the selected pixels travel, the vacated area turns transparent, anything pushed off the board is clipped. Move again moves the fresh paint: PAINT OVER THE PICTURE with the LEFT button (pen cursor) - the paint sticks to the board and every block carries what it picks up; paint landing in the hole is erased by the next move-in. The OP BOX picks the motion TANGENT of the move (the 3ds Max tangent model of cs/Animation.cs; CURVE = the curve editor on the strip above - drag its points, the curve shapes the speed). The little graph in the canvas's top-right draws the shaping with a dot at the current time. Grid slider = block size (8 16 32 64 128 256 px, powers of two only), 'Grid lines' toggles the grid, Speed slider = the move SPEED in px/s (x8 - a block of 64 px crosses at Speed 8 in about one second), Animate ticked = blocks move.", c => MoveDemo.Puzzle(c), clears: true)
                .Also(m => m.PaintByMouse = true).Also(m => m.SpriteSize = 512).Also(m => m.SettingsControl = () => TangentTools.BuildEditor()).Also(m => m.SettingsVisible = c => Math.Clamp((int)c.Op - 1, 0, 7) == 7)
                .Ui(Param.Grid, "Block size (px)", Param.Speed, "Speed (x8 px/s)", Param.Op, "Tangent", Param.GridOn, "Grid lines")
                .Ops("Smooth", "Linear", "Step", "Fast", "Slow", "Spline", "Auto", "Curve")
                .With(Param.Grid, 64).With(Param.Speed, 6).With(Param.Op, 1)
                .Range(Param.Grid, 8, 256).Range(Param.Speed, 1, 100);
            T(GEdit, "Offset: grid scramble (Sprite.Offset)", "Lenna (512, centred) is gridded. Every so often a random FULL strip is picked (Grid slider = strip width, a power of two) and Sprite.Offset shifts it ALONG ITSELF: the content wraps around the strip's bounding box, exactly like Photoshop Filter > Other > Offset (nothing is ever cleared, the pixels just redistribute inside the selection). A strip is NEVER the previous one (other axis or other index), and the shift distance is snapped to the grid but capped by the picture size (a full wrap would be a no-op). The OP BOX picks the motion TANGENT of the shift (CURVE = the curve editor in the Move test's strip). The pen paints over the picture. 'Grid lines' toggles the grid, Speed slider = px/s (x8), Animate ticked = strips shift.", c => OffsetDemo.Scramble(c), clears: true)
                .Also(m => m.PaintByMouse = true).Also(m => m.SpriteSize = 512).Also(m => m.SettingsControl = () => TangentTools.BuildEditor()).Also(m => m.SettingsVisible = c => Math.Clamp((int)c.Op - 1, 0, 7) == 7)
                .Ui(Param.Op, "Tangent", Param.Grid, "Strip width (px)", Param.Speed, "Speed (x8 px/s)", Param.GridOn, "Grid lines")
                .Ops("Smooth", "Linear", "Step", "Fast", "Slow", "Spline", "Auto", "Curve")
                .With(Param.Grid, 64).With(Param.Speed, 6).With(Param.Op, 1).With(Param.GridOn, true)
                .Range(Param.Grid, 8, 256).Range(Param.Speed, 1, 100);
            T(GEdit, "Offset: selection tool (Sprite.Offset)", "A Photoshop-style selection tool on top of Lenna (512, centred): the buttons above the canvas pick RECTANGLE / ELLIPSE / LASSO / PEN (the active one is accented). With a shape tool the LEFT button draws a NEW selection (each shape replaces the old one, like a fresh marquee); with the PEN it paints on the picture instead - selecting and painting never happen at once. Push the selection around with the OFFSET X / OFFSET Y sliders (the content follows the slider 1:1 - Sprite.Offset redistributes the pixels inside the selection); 'Wrap rows / cols' routes the content inside the selected rows / columns instead of around the selection's bounding box (for non-rectangular selections nothing jumps across a gap). Clear selection = an empty canvas (no selection). The selection shows as marching ants with a faint tint.", c => OffsetDemo.Selection(c), clears: true)
                .Also(m => m.PaintByMouse = true).Also(m => m.SpriteSize = 512).Also(m => m.ControlStrip = () => OffsetTools.Strip()).Also(m => m.StripHeight = 40)
                .Ui(Param.OffsetX, "Offset X (px)", Param.OffsetY, "Offset Y (px)", Param.NotMask, "Wrap rows / cols")
                .With(Param.OffsetX, 0).With(Param.OffsetY, 0).With(Param.NotMask, false)
                .Range(Param.OffsetX, -256, 256).Range(Param.OffsetY, -256, 256);
            T(GEdit, "Offset: full scramble (Sprite.Offset, parallel)", "Lenna (512, centred) scrambles CONTINUOUSLY: all 512 single-pixel lines - rows AND columns in random order, each with a random position, direction and shift amount - rotate endlessly (a line that finishes picks a new random shift at once, no pauses). The WORKERS slider sets how many lines advance at once: the lines of the current window really do run IN PARALLEL (Parallel.For) and each of them is a DISTINCT line index, so no line is ever touched twice in one frame - but a row and a column cross at a pixel, so which side of the crossing wins depends on how the workers land: the picture is not identical for every worker count (watch the texture of the motion change as you move the slider). The OP BOX picks the tangent shaping of every line's motion (CURVE = the curve editor in the Move test's strip). The pen paints over the picture. Speed slider = px/s (x8), Animate ticked = the scramble runs.", c => OffsetDemo.Chaos(c), clears: true)
                .Also(m => m.PaintByMouse = true).Also(m => m.SpriteSize = 512).Also(m => m.SettingsControl = () => TangentTools.BuildEditor()).Also(m => m.SettingsVisible = c => Math.Clamp((int)c.Op - 1, 0, 7) == 7)
                .Ui(Param.Op, "Tangent", Param.Count, "Workers (lines at once)", Param.Speed, "Speed (x8 px/s)")
                .Ops("Smooth", "Linear", "Step", "Fast", "Slow", "Spline", "Auto", "Curve")
                .With(Param.Op, 1).With(Param.Count, 8).With(Param.Speed, 6)
                .Range(Param.Speed, 1, 100);
            T(GEffects, "Depth-of-field slice stack: Count x 256^2 slices, blur by depth, redrawn EVERY frame, composed and scaled to the canvas (Op = quality, Smooth = threads, NotMask = diffuse)", "Your cellular-automaton case: Count (1..64) slice sprites of 256x256 are cleared and redrawn every frame (moving blobs + flicker), each one blurred by its depth (bottom slice radius 40, top 0), composed bottom-to-top into one 256x256 premultiplied composite by a LayeredSprite (PrefixCache off - everything changes), which is then drawn ONCE onto the canvas scaled to 256 * Scale * 2 px (bilinear) at the mouse. Op selector = blur quality: Paint = Gaussian at full resolution (3 box passes; the old Blur(r)), AlphaTest = Fast 2-pass + automatic downscale, anything else = Box 1-pass + automatic downscale (Blur(r, BlurQuality.Box, Effects.AutoDownscale) - the cheapest, and for a depth blur visually the same). 'Smooth' = LayeredSprite.Threads = 0 (one worker per core; the per-layer effects run in parallel, the composite stays bit-identical). 'NotMask' = also run a Diffuse(2, 1, seed = frame) over the whole composite when drawing (Photoshop-style grain, animated). The caption shows compose vs draw time so you can see where the frame goes.", c =>
            {
                int n = Math.Clamp(c.Count, 1, 64);
                int q = c.Op == SR2D.Op.Paint ? 0 : c.Op == SR2D.Op.AlphaTest ? 1 : 2;
                lock (dofLock)
                {
                    EnsureDof(n, q, c.Smooth);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    DrawDofFrame(c, n);
                    double tDraw = sw.Elapsed.TotalMilliseconds; sw.Restart();
                    tdof!.Compose();
                    double tCompose = sw.Elapsed.TotalMilliseconds; sw.Restart();
                    int size = Math.Max(16, (int)(256 * c.Scale * 2));
                    if (c.NotMask)
                    {
                        var d = tdofDiffuse ??= new Effects().Diffuse(2, 1);
                        d.DiffuseSeed((int)(c.Time * 30));
                        c.Canvas.DrawFxScaled(tdof.Sprite, c.X - size / 2, c.Y - size / 2, size, size, d, SR2D.Op.AlphaOver, SR2D.Filter.Bilinear);
                    }
                    else tdof.DrawScaled(c.Canvas, c.X - size / 2, c.Y - size / 2, size, size, SR2D.Filter.Bilinear);
                    double tBlit = sw.Elapsed.TotalMilliseconds;
                    string qn = q == 0 ? "gaussian full-res" : q == 1 ? "fast + downscale" : "box + downscale";
                    c.Note = $"{n} slices: draw slices {tDraw:F2} ms | compose ({qn}{(c.Smooth ? ", " + Environment.ProcessorCount + " threads" : "")}) {tCompose:F2} ms | scale to {size}px{(c.NotMask ? " + diffuse" : "")} {tBlit:F2} ms";
                }
            }, needsFx: true, needsPoly: true).Controls = UI(Param.Count, "Slices (layers) redrawn and composed per frame", Param.Op, "Blur quality: Paint=Gaussian full-res | AlphaTest=Fast + auto downscale | other=Box + auto downscale",
                Param.Scale, "Composite size on screen: 256 px x Scale x 2", Param.Mouse, "Drag: composite position", Param.Smooth, "LayeredSprite.Threads = 0 (one worker per core)", Param.NotMask, "Diffuse(2, 1) grain over the composite, animated");
            T(GLayers, "LayeredSprite: 12 layers, one effect each, composed once, drawn rotated (Angle, Scale; Smooth = animate a layer)", "A LayeredSprite of 12 alpha layers (blur / wave / colour / shadow effects, integer offsets) composes itself into ONE premultiplied sprite and re-composes only what changed: 'Smooth' on animates the wave of layer 6 every frame (prefix cache -> layers 6..11 redrawn), off = nothing changes (one blit). The composite is drawn Count x with DrawRotate2 (Angle, Scale, Filter.Auto) through a whole-stack Shadow. NotMask = the same 12 layers drawn one by one with DrawFxRotated for comparison (the per-layer effects are identical; the whole-stack Shadow of the composite is missing there, because the manual path has no composite to shadow - and it is many times slower when rotated/scaled). Caption shows what the last Compose redrew.", c =>
            {
                var ls = Layers(c);
                ls[11].Transform = null;                    // the stack is cached between tests: the Transform test can leave a per-layer transform here
                if (c.Smooth) ls[6].Effects!.Clear().Wave(18f, 4f, c.Time * 4f);
                int w = (int)(ls.Width * c.Scale), h = (int)(ls.Height * c.Scale);
                if (c.NotMask)
                {
                    for (int k = 0; k < c.Count; k++)
                        for (int i = 0; i < ls.Count; i++)
                        {
                            var L = ls[i];
                            // same placement as the layer inside the stack, rotated about the stack centre
                            float px = ls.Width * 0.5f - L.X, py = ls.Height * 0.5f - L.Y;
                            c.Canvas.DrawFxRotated(L.Sprite, c.X + k * 24, c.Y + k * 24, c.Angle, L.Effects!, (int)(L.Sprite.Width * c.Scale), (int)(L.Sprite.Height * c.Scale), px, py, L.Op, SR2D.Filter.Auto);
                        }
                    c.Note = "12 x DrawFxRotated per copy (no composite)";
                }
                else
                {
                    ls.Compose();
                    c.Note = ls.LastComposedFrom < 0 ? "Compose: nothing changed (cached)" : "Compose: redrew layers " + ls.LastComposedFrom + ".." + (ls.Count - 1);
                    for (int k = 0; k < c.Count; k++) ls.DrawRotate2(c.Canvas, c.X + k * 24, c.Y + k * 24, c.Angle, w, h);
                }
            }, needsFx: true, warp: true).With(Param.Angle, 32).With(Param.Scale, 120);   // off-neutral: at x1.00 / 0 deg the stack is drawn exactly like the un-rotated 'LayeredSprite.Transform' test
            T(GLayers, "LayeredSprite.Transform: the whole stack through one editable transform frame (drag inside = move, corners = scale, edge dots = stretch, outside = rotate, Ctrl+corner = perspective)", "The same 12-layer stack; its geometry lives in ls.Transform, which the frame around it edits like an image editor: drag inside to move, a corner handle to scale about the opposite corner (Shift = keep aspect), an edge dot to stretch one axis, the ring outside to rotate about the pivot (Shift = 15 degree steps), Ctrl + a corner to pull that corner alone (perspective quad), right click to reset. The Op box adds an extra on top: skew, mirror, a layer with its own SpriteTransform inside the stack, Transform.Opacity from Blend. Changing the transform never recomposes (note: 'nothing changed') - one warp per draw; only 'Animate layer 6' recomposes, from layer 6. HitTest maps the mouse back to a composite pixel (label). Count copies (offset).",
                c =>
                {
                    var ls = Layers(c);
                    if (c.Smooth) ls[6].Effects!.Clear().Wave(18f, 4f, c.Time * 4f);
                    // the frame is the transform: attach it to the stack's size at the canvas centre and copy its state into the stack
                    int ox = c.W / 2 - ls.Width / 2, oy = c.H / 2 - ls.Height / 2;
                    c.Frame.Attach(ls.Width, ls.Height, ox, oy);
                    var T = ls.Transform; T.CopyFrom(c.Frame.Transform); T.Filter = SR2D.Filter.Auto;
                    ls[11].Transform = null;
                    switch (c.Op)
                    {
                        case SR2D.Op.AlphaTest: T.Matrix = System.Numerics.Matrix3x2.CreateSkew(0.4f, 0f); c.Note = "+ Skew(0.4, 0)"; break;
                        case SR2D.Op.AlphaBlend: T.Matrix = System.Numerics.Matrix3x2.CreateScale(-1f, 1f); c.Note = "+ Mirror"; break;
                        case SR2D.Op.Add2D: ls[11].Transform = new SpriteTransform { X = ls[11].X, Y = ls[11].Y, Angle = c.Time }.Skew(0.5f, 0f); c.Note = "layer 11 has its own SpriteTransform (skew + spin) - recomposes from layer 11 only"; break;
                        case SR2D.Op.Add: T.Opacity = c.Blend / 255f; c.Note = $"+ Transform.Opacity {c.Blend / 255f:0.00}"; break;
                        default: c.Note = "frame -> ls.Transform"; break;
                    }
                    ls.Compose();
                    c.Note += ls.LastComposedFrom < 0 ? " | compose: nothing changed" : " | compose: redrew layers " + ls.LastComposedFrom + "..";
                    for (int k = 0; k < c.Count; k++) ls.Draw(c.Canvas, ox + k * 24, oy + k * 24);
                    var hit = ls.HitTest(c.X, c.Y, ox, oy);
                    c.Info(c.Frame.Describe());
                    c.Info(hit.HasValue ? $"mouse -> composite pixel {hit.Value.X}, {hit.Value.Y}" : "mouse outside the transformed stack");
                }, needsFx: true, warp: true).Ui(Param.Op, "Extra on top of the frame", Param.Blend, "Transform.Opacity (Opacity list)", Param.Count, "Copies", Param.Mouse, "Frame: drag / handles / ring; right click = reset", Param.Smooth, "Animate layer 6 (recomposes from 6)").Ops("Frame only", "+ Skew", "+ Mirror", "Layer 11: own transform", "+ Opacity").Also(t => t.Frame = true);
            T(GEffects, "Effects: heat haze over the whole scene (screen-space Turbulence on one blit)", "A heat haze is a SCREEN-space effect: one continuous displacement field over the finished picture. So the grid of colour sprites is rendered once into a full-canvas scratch (cached - every parallel band only reads it) and that scratch is blitted back through Effects.Turbulence in a single DrawFx call: the shimmer slides over the sprites instead of wobbling each sprite on its own. Sprite-local DrawFx cannot do this - Effects.Post is ignored for a plain 1:1 draw (Effects.cs: 'Ignored for plain 1:1 draws'), so the pattern would restart on every sprite. Strength = Blend/24 px, feature size = Scale*16 px, phase = time*0.6. 'Smooth' = bilinear sampling of the displacement (off = nearest, blockier).", c =>
            {
                lock (hazeLock)
                {   // static scene: rebuilt only when the canvas size or the asset set changed
                    if (thaze == null || !ReferenceEquals(hazeOf, c.A) || thaze.Width != c.W || thaze.Height != c.H)
                    {
                        thaze?.Dispose(); thaze = new Sprite(c.W, c.H); hazeOf = c.A;
                        for (int y = 0; y + c.S <= c.H; y += c.S) for (int x = 0; x + c.S <= c.W; x += c.S) thaze.Draw(c.A.Color, x, y, SR2D.Op.Paint);
                    }
                }
                fx.Clear().Turbulence(c.Scale * 16f, c.Blend / 24f, c.Time * 0.6f, c.Smooth ? SR2D.Filter.Bilinear : SR2D.Filter.Nearest);
                c.Canvas.DrawFx(thaze, 0, 0, fx, SR2D.Op.Paint, Opaque: true);
            }, needsFx: true, clears: true).Ui(Param.Scale, "Feature size (x16 px)", Param.Blend, "Strength (Blend / 24 px)", Param.Smooth, "Bilinear displacement (off = nearest)", Param.Time, "Animate: the shimmer travels").With(Param.Smooth, true).With(Param.Blend, 200);

            T(GEffects, "Blur in place (canvas.Blur)", "Draws the Count x grid of colour sprites, then blurs the whole canvas in place with Sprite.Blur(radius). Full-frame cost at 1080p: ~5-6 ms per pass set (AVX2).", c =>
            {
                Grid(c, (x, y) => c.Canvas.Draw(c.A.Color, x, y, SR2D.Op.Paint));
                c.Canvas.Blur(BlurR(c), !c.Smooth);
            }, needsBlur: true).Ui(Param.Scale, "Blur radius (x8 px)", Param.Smooth, "Fast (2-pass) blur", Param.Count, "Sprites (timing)").With(Param.Scale, 150);
            T(GEffects, "Blurred backdrop behind a sprite (frosted glass)", "A rectangle of the canvas is copied to the Temp scratch, blurred IN PLACE with Sprite.Blur - clipped to the panel, which is exactly what an acrylic panel needs (no ToBlurred margin bookkeeping) - then lightened with MulAddS2X and drawn back under the alpha sprite. Radius = Scale*8 px, 'Smooth' off = the Fast 2-pass blur.", c =>
            {
                int r = BlurR(c);
                // background: tiles
                for (int y = 0; y < c.H; y += c.A.Tile.Height) for (int x = 0; x < c.W; x += c.A.Tile.Width) c.Canvas.Draw(c.A.Tile, x, y, SR2D.Op.Paint);
                Sprite panel = c.Temp;                                                  // 2S x 2S scratch = the panel
                int pw = panel.Width, ph = panel.Height, px = c.X - pw / 2, py = c.Y - ph / 2;
                panel.Draw(c.Canvas, -px, -py, SR2D.Op.Paint);                          // grab the panel area
                panel.ClearAlpha();
                panel.Blur(r, !c.Smooth);                                               // frost it (clipped to the panel: exactly what a panel needs)
                panel.MulAddS2X(panel, 0, 0, SR2D.ARGB(96, 96, 96, 96), SR2D.ARGB(160, 160, 160, 160));   // per byte x0.75 + 64: frosted, and the alpha byte stays 255
                c.Canvas.Draw(panel, px, py, SR2D.Op.Paint);
                if (Caps.HasPoly) c.Canvas.DrawRect(px + 0.5f, py + 0.5f, pw - 1, ph - 1, SR2D.ARGB(255, 255, 255, 255), 1f, false, SR2D.LineOp.AlphaBlend);
                c.Canvas.Draw(c.A.Alpha, c.X - c.A.Alpha.Width / 2, c.Y - c.A.Alpha.Height / 2, SR2D.Op.AlphaBlend);
            }, needsBlur: true, clears: true);

            // ---- blend modes (Sprite.Blend.cs) --------------------------------------------------------
            var modeNames = Enum.GetNames<SR2D.BlendMode>();
            T(GBlend, "All 27 modes (Blend = opacity, drag the backdrop)", "Every editor blend mode (Photoshop names) at once: the colour sprite through each mode over the light / dark alpha checker, labelled. The BLEND slider is the layer opacity (0..255 -> 0..1): the mode acts on the colour, the source alpha times the opacity is the coverage, so every mode is automatically 'mixed with alpha blend' like a layer in an editor. DRAG anywhere to move the checker under the sprites - see how each mode reacts to a light and a dark backdrop. Hue / Saturation / Color / Luminosity are the PDF non-separable formulas; Dissolve is the random-pixel one.",
                c =>
                {
                    Checker(c, c.X - c.W / 2, c.Y - c.H / 2);
                    float op = c.Blend / 255f; int i = 0;
                    SpacedGrid(c, c.S / 8, modeNames.Length, (x, y) =>
                    {
                        var m = (SR2D.BlendMode)(SR2D.BlendModeFirst + i);
                        for (int k = 0; k < c.Count; k++) c.Canvas.DrawBlend(c.A.Color, x, y, m, op);
                        c.Label(x + 2, y + 2, modeNames[i++]);
                    });
                }, needsBlend: true).Ui(Param.Blend, "Opacity (0..255 = 0..1)", Param.Count, "Repetitions per frame (timing only)", Param.Mouse, "Drag anywhere = move the checker backdrop").With(Param.Blend, 255).Also(t => t.DragAnywhere = true);
            T(GBlend, "One mode (Op selector) - alpha sprite over the photo (Blend = opacity)", "The mode from the OP box applied to the alpha sprite (soft edge) drawn Count times in a cascade over the colour picture: the soft edge shows the coverage rule (alpha x opacity), the picture underneath shows the formula. 'Smooth' switches the source to the opaque colour sprite. Same call as the previous test (Sprite.DrawBlend); through the Op enum it is just canvas.Draw(src, x, y, SR2D.Op.Multiply) - every draw call (DrawScaled, DrawRotate2, DrawQuad, DrawFx, layers, shapes with a LineOp) takes the modes.",
                c =>
                {
                    c.Canvas.DrawScaled(c.A.Color, 0, 0, c.W, c.H, SR2D.Op.Paint, SR2D.Filter.Auto);     // backdrop: the picture scaled to the canvas
                    var m = (SR2D.BlendMode)(SR2D.BlendModeFirst + Math.Clamp((int)c.Op - 1, 0, modeNames.Length - 1));
                    float op = c.Blend / 255f;
                    Sprite src = c.Smooth ? c.A.Color : c.A.Alpha;
                    Cascade(c, (x, y) => c.Canvas.DrawBlend(src, x, y, m, op));
                    c.Note = m + " at " + (int)(op * 100) + " %";
                }, needsBlend: true, warp: true, clears: true).Ui(Param.Op, "Blend mode", Param.Blend, "Opacity (0..255 = 0..1)", Param.Count, "Copies (cascading)", Param.Smooth, "Opaque colour sprite instead of the alpha sprite").Ops(modeNames).With(Param.Blend, 255).With(Param.Op, 3);
            T(GBlend, "Modes on transforms and shapes (Op selector, Angle, Scale)", "The same mode through the other entry points: left = DrawBlendRotated (DRAW_WARP, bilinear - the sampling is done premultiplied so the edges do not fringe), middle = DrawBlendFx with a Blur(3) effect chain (DRAW_FX), right = FillRoundRect + DrawWideLine + DrawText with the mode as LineOp (DRAW_POLY / DRAW_LINE2). All over the colour picture.",
                c =>
                {
                    c.Canvas.DrawScaled(c.A.Color, 0, 0, c.W, c.H, SR2D.Op.Paint, SR2D.Filter.Auto);
                    var m = (SR2D.BlendMode)(SR2D.BlendModeFirst + Math.Clamp((int)c.Op - 1, 0, modeNames.Length - 1));
                    float op = c.Blend / 255f;
                    int cx1 = c.W / 4, cx2 = c.W / 2, cx3 = c.W * 3 / 4, cy = c.H / 2, sz = Math.Max(8, (int)(c.S * c.Scale));
                    c.Canvas.DrawBlendRotated(c.A.Alpha, cx1, cy, c.Angle, m, op, sz, sz, -1, -1, SR2D.Filter.Bilinear);
                    c.Label(cx1 - sz / 2, cy + sz / 2 + 4, "DrawBlendRotated");
                    fx.Clear().Blur(3);
                    c.Canvas.DrawBlendFx(c.A.Alpha, cx2 - c.S / 2, cy - c.S / 2, fx, m, op);
                    c.Label(cx2 - c.S / 2, cy + c.S / 2 + 4, "DrawBlendFx (Blur 3)");
                    var lop = m.ToLineOp(op);
                    int col = unchecked((int)0xFFFF9020), col2 = unchecked((int)0xC020C0FF);
                    c.Canvas.FillRoundRect(cx3 - c.S / 2, cy - c.S / 2, c.S, c.S, c.S / 6f, col, lop, true);
                    c.Canvas.DrawWideLine(cx3 - c.S / 2, cy + c.S / 2 + 12, cx3 + c.S / 2, cy - c.S / 2 - 12, col2, MathF.Max(2f, c.S / 12f), true, lop);
                    c.Canvas.DrawText(cx3, cy, "TEXT", unchecked((int)0xFFFFFFFF), 0, Math.Max(1, c.S / 32), 0, 0, lop, 128, TextAnchor.Center);
                    c.Label(cx3 - c.S / 2, cy + c.S / 2 + 4, "FillRoundRect / DrawWideLine / DrawText (LineOp)");
                    c.Note = m + " at " + (int)(op * 100) + " %";
                }, needsBlend: true, needsFx: true, needsPoly: true, warp: true, clears: true).Ui(Param.Op, "Blend mode", Param.Blend, "Opacity (0..255 = 0..1)", Param.Angle, "Rotation of the left sprite", Param.Scale, "Size of the left sprite").Ops(modeNames).With(Param.Blend, 255).With(Param.Op, 13);
            T(GLayers, "LayeredSprite with blend-mode layers (Op selector = mode of the top layer, Blend = its opacity, drag it)", "A stack: the colour picture, a Multiply layer (dark vignette), a Screen layer (light streak), a Color layer (tint) and a top layer (the alpha sprite) with the mode from the OP box and the opacity from the BLEND slider - Layer.BlendMode / Layer.Opacity; the opacity is folded into the op word, so it costs nothing (no Effects stage). The composite is premultiplied (AlphaOver): the modes accumulate the alpha correctly on it. Compose caches, so only the layers from the first changed one are redrawn (drag the top layer: only it is redrawn).",
                c =>
                {
                    var ls = BlendLayers(c);
                    var m = (SR2D.BlendMode)(SR2D.BlendModeFirst + Math.Clamp((int)c.Op - 1, 0, modeNames.Length - 1));
                    var top = ls[ls.Count - 1];
                    top.BlendMode = m; top.Opacity = c.Blend / 255f;
                    top.X = (c.X * ls.Width / Math.Max(1, c.W)) - top.Sprite.Width / 2; top.Y = (c.Y * ls.Height / Math.Max(1, c.H)) - top.Sprite.Height / 2;
                    ls.Compose();
                    c.Note = (ls.LastComposedFrom < 0 ? "Compose: cached" : "Compose: redrew layers " + ls.LastComposedFrom + "..") + " - top layer " + m;
                    ls.DrawScaled(c.Canvas, 0, 0, c.W, c.H);
                }, needsBlend: true, needsFx: true, warp: true, clears: true).Ui(Param.Op, "Mode of the top layer", Param.Blend, "Opacity of the top layer", Param.Mouse, "Drag = move the top layer").Ops(modeNames).With(Param.Blend, 255).With(Param.Op, 9);

            // ===================================================== Voxels (VoxelGrid.cs)
            // Shared control layout of the voxel tests (see DemoTest.Controls / Ops / Bits): Op = camera (presets or free orbit),
            // Scale = zoom, mouse = orbit camera (left drag = yaw / pitch, middle drag = pan, wheel = zoom), bits = lighting tier
            // (radio buttons), Smooth = points mode, NotMask = night, Brite = light energy, Z = sky light level, Xor = parallel bands.
            string[] camOps = { "Free orbit (drag the canvas)", "Isometric (turn = Angle)", "3/4 view (turn = Angle)", "Top-down (turn = Angle)", "Side (turn = Angle)" };
            string[] lightTiers = { "None (flat colours)", "Faces (per-face shade)", "Propagated (sky + lamps)", "Smooth + AO" };
            var voxUI = new object[] { Param.Op, "Camera", Param.Scale, "Zoom: pixels per voxel = Scale x 4 (x1 with a preset = 1 pixel per voxel); mouse wheel over the canvas",
                Param.Angle, "Presets: quarter turns (0/90/180/270); free orbit: yaw offset added to the mouse orbit",
                Param.Camera, "Orbit: drag = yaw / pitch, middle drag = pan, right click = recentre",
                Param.MaskBits, "Lighting tier:",
                Param.Smooth, "Points mode (1 px per voxel) instead of cubes",
                Param.NotMask, "Night: sky light drops to the 'Sky light' slider value and the lamps carry the scene",
                Param.Z, "Sky light at night (0..15; day is always 15)",
                Param.Brite, "Lamp energy x0.01: multiplies the light curve for the LAMP channels only (sky / ambient stays as it is) - raise it when the lamps are too weak at night",
                Param.DotStep, "Depth fade: 0 off, 1 height (dark towards the bottom), 2 depth (dark away from the camera), 3 both, 4 depth as FOG (fades to the sky colour). Works with every tier, also 'None'",
                Param.Blend, "Depth fade floor: brightness at the far / bottom end (x/255; 0 = black, 255 = no fade)",
                Param.Xor, "DrawVoxelsParallel (bands across the cores)" };
            static VoxelCamera VoxCam(Ctx c, float scale, out string name)
            {
                int turn = (int)MathF.Round(c.Angle / (MathF.PI / 2)) & 3;
                VoxelCamera cam;
                switch ((int)c.Op)
                {
                    case 2: cam = VoxelCamera.Isometric(scale, turn); name = "Isometric turn " + turn; break;
                    case 3: cam = VoxelCamera.ThreeQuarter(scale, turn); name = "3/4 view turn " + turn; break;
                    case 4: cam = VoxelCamera.TopDown(scale, turn); name = "Top-down turn " + turn; break;
                    case 5: cam = VoxelCamera.Side(scale, turn); name = "Side turn " + turn; break;
                    default: { float yaw = c.Yaw + c.Angle, pitch = c.Pitch; cam = VoxelCamera.Free(yaw, pitch, scale); name = $"Free yaw {yaw * 180 / MathF.PI:F0} pitch {pitch * 180 / MathF.PI:F0}"; break; }
                }
                if (c.Smooth) cam.Mode = VoxelMode.Points;
                cam.SetLight(-1f, -0.6f, 1.6f, 0.4f);
                VoxEnergy(c, cam);
                VoxFadeSetup(c, cam);
                return cam;
            }
            // depth fade from the DotStep (mode) and Blend (floor) controls - the cheapest lighting: a per-voxel multiply
            // after the tier, along grid z and / or along the view direction; mode 4 fades towards the background colour (fog)
            static void VoxFadeSetup(Ctx c, VoxelCamera cam)
            {
                int mode = c.DotStep;
                cam.Fade = mode == 1 ? VoxelFade.Height : mode == 2 ? VoxelFade.Depth : mode == 3 ? VoxelFade.Height | VoxelFade.Depth : mode >= 4 ? VoxelFade.Depth | VoxelFade.Fog : VoxelFade.None;
                cam.FadeMin = c.Blend / 255f;
                cam.FadeGamma = 1f;
                cam.FadeColor = c.NotMask ? 0x05070C : 0x101418;      // = VoxBackground
            }
            static string VoxFadeName(Ctx c) => c.DotStep switch { 1 => $"fade height {c.Blend / 255f:0.00}", 2 => $"fade depth {c.Blend / 255f:0.00}", 3 => $"fade height+depth {c.Blend / 255f:0.00}", >= 4 => $"fog {c.Blend / 255f:0.00}", _ => "no fade" };
            // lamp energy: the Brite slider scales the light curve for the lamp channels only (VoxelCamera.LampEnergy) - what a lamp
            // light level 0..15 is worth on screen; values above 1 let lamps over-drive their surroundings, the kernel clamps per
            // channel. The sky (ambient) keeps the plain curve (SkyEnergy 1); its LEVEL at night comes from the Z slider.
            static void VoxEnergy(Ctx c, VoxelCamera cam)
            {
                for (int i = 0; i < 16; i++) cam.LightCurve[i] = 0.05f + 0.95f * MathF.Pow(0.8f, 15 - i);
                cam.LampEnergy = MathF.Max(0.05f, c.Brite);
                cam.SkyEnergy = 1f;
            }
            static int VoxSky(Ctx c) => c.NotMask ? Math.Clamp(c.Z, 0, 15) : 15;
            static VoxelLighting VoxLight(Ctx c) { int mb = c.MaskBits; return (mb & 8) != 0 ? VoxelLighting.Smooth : (mb & 4) != 0 ? VoxelLighting.Propagated : (mb & 2) != 0 ? VoxelLighting.Faces : (mb & 1) != 0 ? VoxelLighting.None : VoxelLighting.Faces; }
            static void VoxBackground(Ctx c) => c.Canvas.ClearBuffer(c.NotMask ? SR2D.ARGB(255, 5, 7, 12) : SR2D.ARGB(255, 0x10, 0x14, 0x18));
            DemoTest VoxTest(DemoTest t)
            {
                t.Ui(voxUI).Ops(camOps).Bits(true, lightTiers).Camera = true; t.FreeOpIndex = 0;
                t.With(Param.MaskBits, 8).With(Param.Brite, 100).With(Param.Z, 6).Range(Param.Z, 0, 15).Range(Param.Brite, 10, 600).Range(Param.Scale, 5, 800);
                t.With(Param.DotStep, 0).With(Param.Blend, 50).Range(Param.DotStep, 0, 4);
                return t;
            }

            VoxTest(T(GVoxel, "Voxel terrain (Count = grid size, camera / zoom / lighting from the controls)", "A procedurally generated terrain (Count x 8 voxels per side, half as deep: noise heightmap, caves carved with 3-D noise, a hollow tower with a blue lamp inside and an orange beacon on its roof, lamps of different colours and strengths scattered on the surface, a fire pit) drawn once per frame with Sprite.DrawVoxels. Camera: free orbit by default - drag the canvas to turn / tilt, middle-drag to pan, wheel to zoom - or a pixel-art preset from the Op selector. Lighting tier: None / Faces / Propagated (sky + lamps) / Smooth + ambient occlusion. Night lowers the sky light to the 'Sky light' slider; 'Lamp energy' scales what a lamp light level is worth on screen (the sky is not touched). The caption shows voxels drawn and the draw time; VoxelGrid.Update (faces + light propagation, done once per edit) is timed when the grid is rebuilt (Count changed).", c =>
            {
                int side = Math.Clamp(c.Count * 8, 8, 512);
                var g = EnsureVoxels(side, 0, out double tUp);
                var L = VoxLight(c);
                g.SkyLight = VoxSky(c);
                var cam = VoxCam(c, MathF.Max(0.25f, c.Scale * 4f), out string camName);
                cam.Anchor = VoxelAnchor.Center;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (L >= VoxelLighting.Propagated) g.Update(); else g.UpdateFaces();
                tUp += sw.Elapsed.TotalMilliseconds; sw.Restart();
                VoxBackground(c);
                int drawn = c.Xor ? c.Canvas.DrawVoxelsParallel(g, cam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, L) : c.Canvas.DrawVoxels(g, cam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, L);
                c.Note = $"{side}x{side}x{side / 2}, {g.ExposedCount} exposed, {drawn} drawn | {camName}, x{cam.Scale:0.#}, {cam.EffectiveMode}, {L}, {VoxFadeName(c)}, sky {g.SkyLight}, energy x{c.Brite:0.00} | draw {sw.Elapsed.TotalMilliseconds:F2} ms | last Update {tUp:F1} ms";
            }, needsVoxel: true, clears: true)).Ui(Param.Count, "Grid size: Count x 8 voxels per side (8 .. 512)").With(Param.Count, 12).With(Param.Scale, 150);

            VoxTest(T(GVoxel, "Voxel sprite: a house model drawn Count times (camera / zoom / lighting from the controls)", "Sprite-style use: a 24x24x24 model (house with lit windows, a lamp post, a glowing chimney, a porch light and a warm light inside) drawn Count times in a grid across the canvas (one copy = centred) with the selected camera, zoom and lighting tier - the per-draw cost of small grids, i.e. what an animated voxel object costs. At night (sky light from the slider) the windows, the lamp post and the chimney light the scene; raise 'Lamp energy' if you want them to over-drive. Points mode (Smooth) shows the same model as pixel art. The model is built with the editing API (FillBox / ShellBox / FillCylinder / FillCone / Set) - see VoxelGrid.Edit.cs.", c =>
            {
                var g = EnsureVoxelHouse();
                var L = VoxLight(c); g.SkyLight = VoxSky(c);
                var cam = VoxCam(c, MathF.Max(0.5f, c.Scale * 4f), out string camName);
                cam.Anchor = VoxelAnchor.Center;
                var b = g.ScreenBounds(cam, 0, 0);
                int n = c.Count, cellW = b.Width + 8, cellH = b.Height + 8;
                int cols = Math.Max(1, Math.Min(n, c.W / Math.Max(1, cellW))), rows = (n + cols - 1) / cols;
                int ox = (c.W - cols * cellW) / 2 - b.Left + 4 + (int)c.PanX, oy = (c.H - rows * cellH) / 2 - b.Top + 4 + (int)c.PanY;
                VoxBackground(c);
                int drawn = 0;
                for (int i = 0; i < n; i++) drawn += c.Canvas.DrawVoxels(g, cam, ox + (i % cols) * cellW, oy + (i / cols) * cellH, L);
                c.Note = $"{n} x {drawn / Math.Max(1, n)} voxels drawn | {camName}, x{cam.Scale:0.#}, {cam.EffectiveMode}, {L}, {VoxFadeName(c)}, sky {g.SkyLight} | model {g.Width}^3, {g.ExposedCount} exposed";
            }, needsVoxel: true, clears: true)).Ui(Param.Count, "Copies drawn per frame").With(Param.Scale, 300);

            VoxTest(T(GVoxel, "Voxel lights: 3 lamps in 3 rooms (colour / strength / reach from the strip above the canvas)", "How the propagated light works, on a small scene you can read: a 48 x 20 x 12 building with three rooms, one lamp in each, doorways between the rooms and to the outside, the front wall and the roof cut away so you look in. The strip above the canvas has, per lamp: a Hue knob (the lamp COLOUR = the voxel colour of the emitter), a Strength knob (Emit 0..15 = the light level at the source; the light loses one level per empty cell at reach 15, so a lamp of strength 8 lights 8 cells around it) and an on/off switch; plus a 'Light reach' slider (VoxelGrid.LightReach: how far a level-15 light travels before it is gone - 15 cells is the Minecraft rule; up to 120 makes the fall-off flat and the light spill through the doorways far into the other rooms - the propagation cost grows with the lit volume, the caption shows it), 'Sky' (the ambient level poured in from above; 0 = the lamps alone) and 'Lamp energy' (the on-screen multiplier for the lamp channels only). The number under the caption is VoxelGrid.Update (faces + light propagation), redone whenever a lamp or the reach changes. Tier 'Propagated' shows the raw per-cell levels, 'Smooth' interpolates them across the faces and adds ambient occlusion.", c =>
            {
                var (g, tUpd) = EnsureVoxelRooms();
                var L = VoxLight(c);
                if (L < VoxelLighting.Propagated) L = VoxelLighting.Propagated;
                var cam = VoxCam(c, MathF.Max(0.5f, c.Scale * 4f), out string camName);
                cam.LampEnergy = (float)LightsDemo.LampEnergy; cam.SkyEnergy = 1f;
                VoxBackground(c);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int drawn = c.Canvas.DrawVoxels(g, cam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, L);
                double tDraw = sw.Elapsed.TotalMilliseconds;
                c.Info($"reach {g.EffectiveLightReach} cells  sky {g.SkyLight}  lamp energy x{LightsDemo.LampEnergy:0.00}  |  {LightsDemo.Describe()}");
                c.Info($"VoxelGrid.Update (faces + light) {tUpd:F2} ms  |  draw {tDraw:F2} ms, {drawn} voxels, {L}");
                c.Note = $"{camName} | {L} | update {tUpd:F2} ms, draw {tDraw:F2} ms";
            }, needsVoxel: true, clears: true)).With(Param.Scale, 250).With(Param.MaskBits, 8).With(Param.NotMask, true).With(Param.Z, 2)
                .Also(t => { t.ControlStrip = LightsDemo.Build; t.StripHeight = LightsDemo.StripHeight; t.Controls.Remove(Param.NotMask); t.Controls.Remove(Param.Z); t.Controls.Remove(Param.Brite); });

            VoxTest(T(GVoxel, "Voxel editing: procedural noise + selections + solids (Count = seed)", "Shows the editing API on a 64^3 grid rebuilt when Count (the seed) changes: FillNoise (3-D fractal noise, density gradient) makes an asteroid, CarveNoise tunnels through it, SelectSurface + Paint recolours the crust, SelectCavities + Fill lights the enclosed pockets with emitters, a torus, a cone, a capsule and a hollow shell sphere are added with the solid primitives, and Shell / Invert / 3-D flood fill are demonstrated on the right-hand pieces. The 'Variant' selector picks what is shown: the model, Shell(1) of it (cut open), Invert(), or Hollow(2) with the front quarter removed so you can see inside. Camera: free orbit (drag), Animate = slow auto-rotation.", c =>
            {
                int seed = c.Count;
                var (g, tBuild, desc) = EnsureVoxelEdit(seed, (SR2D.Op)Math.Clamp((int)c.Op, 1, 4));
                var L = VoxLight(c); g.SkyLight = VoxSky(c);
                float yaw = c.Yaw + c.Angle + c.Time * 0.3f;
                var cam = VoxelCamera.Free(yaw, c.Pitch, MathF.Max(0.5f, c.Scale * 4f)); cam.SetLight(-1f, -0.6f, 1.6f, 0.4f); VoxEnergy(c, cam);
                if (c.Smooth) cam.Mode = VoxelMode.Points;
                VoxBackground(c);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int drawn = c.Canvas.DrawVoxels(g, cam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, L);
                c.Note = $"{desc} | {drawn} drawn, {L} | draw {sw.Elapsed.TotalMilliseconds:F2} ms | build (edit ops) {tBuild:F1} ms";
            }, needsVoxel: true, clears: true)).Ui(Param.Count, "Noise seed (rebuilds the model)", Param.Op, "Variant", Param.Time, "Animate: slow auto-rotation")
                .Ops("Model", "Shell(1) - cut open", "Invert()", "Hollow(2), front quarter cut away").With(Param.Scale, 150);
            L[L.Count - 1].FreeOpIndex = -1;

            // ---- projections: default shapes, own sprites, per-view check boxes
            string[] projViews = { "Front", "Back", "Left", "Right", "Top", "Bottom" };
            VoxTest(T(GVoxel, "Voxels from projections: 2-D sprites for the sides -> model (views by check box, 'Open file...' for your own sprites)", "Builds a voxel model from 2-D sprites used as projections (VoxelGrid.FromProjections, cs/VoxelGrid.Projections.cs): a cell is solid where EVERY enabled view is opaque at its projection, and its colour comes from the nearest view; where two opposite views both reach a cell they are blended (Blend selector). The six check boxes enable the six views; the SHAPE selector picks the built-in sprite set: Ball (a disc on every side -> the intersection of three cylinders, a 'Steinmetz solid' with rounded edges: silhouettes alone cannot make a true sphere), Box (squares, differently coloured per side), Cylinder (circle top + bottom, rectangles around -> a can), 'Square top, round bottom' (the front / side are trapezoids and the top is a square while the bottom is a circle - the model morphs from one to the other because the silhouettes are intersected and the colours blend between the two ends), and the rocket from before. 'Open file...' lets you pick up to six PNGs of your own: they are assigned to Front, Back, Left, Right, Top, Bottom in the order chosen (fewer files -> fewer views, name a file front/back/left/right/top/bottom to pin it to a side). Every source sprite is shown at the top of the canvas with the side it is mapped to. Fit: proportional (letterboxed) or stretched to the face. A typical model needs only three views - two adjacent sides and the top: with only Front + Left + Top the back / right / bottom take the colour of the nearer visible view.", c =>
            {
                int n = Math.Clamp(c.Count * 8, 8, 256);
                var t = L.Find(x => x.Name.StartsWith("Voxels from projections", StringComparison.Ordinal))!;
                int shape = Math.Clamp((int)c.Op - 1, 0, 4);
                int views = c.MaskBits == 0 ? 1 : c.MaskBits;
                var blend = c.DotStep switch { 1 => VoxelBlend.Lerp, 2 => VoxelBlend.Nearest, 3 => VoxelBlend.SurfaceOnly, _ => VoxelBlend.Dither };
                var (g, src, tBuild, desc) = EnsureVoxelProj(n, shape, views, c.Smooth, c.Xor, blend, t.FilePath);
                var Lt = VoxLightBits(c.Blend);
                var cam = VoxelCamera.Free(c.Yaw + c.Angle + c.Time * 0.4f, c.Pitch, MathF.Max(0.5f, c.Scale * 4f * 64f / n)); cam.SetLight(-1f, -0.6f, 1.6f, 0.4f); VoxEnergy(c, cam);
                g.SkyLight = VoxSky(c);
                VoxBackground(c);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int drawn = c.Canvas.DrawVoxels(g, cam, c.W / 2 + c.PanX, c.H / 2 + 24 + c.PanY, Lt);
                double tDraw = sw.Elapsed.TotalMilliseconds;
                // the source sprites along the top edge, each with the side it is mapped to
                int px = 8, py = 8;
                for (int i = 0; i < 6; i++)
                {
                    if (src[i] == null || (views >> i & 1) == 0) continue;
                    int sw2 = Math.Min(src[i]!.Width, 96), sh2 = Math.Min(src[i]!.Height, 96);
                    if (sw2 == src[i]!.Width && sh2 == src[i]!.Height) c.Canvas.Draw(src[i]!, px, py, SR2D.Op.AlphaBlend); else c.Canvas.DrawScaled(src[i]!, px, py, sw2, sh2, SR2D.Op.AlphaBlend, SR2D.Filter.Area);
                    c.Canvas.DrawRect(px - 1, py - 1, sw2 + 2, sh2 + 2, unchecked((int)0xFF808080));
                    c.Label(px, py + sh2 + 2, projViews[i] + $" {src[i]!.Width}x{src[i]!.Height}");
                    px += sw2 + 14;
                }
                c.Note = $"{n}^3 from {desc} | {g.Stats().solid} solid, {drawn} drawn, {Lt} | build {tBuild:F1} ms | draw {tDraw:F2} ms";
            }, needsVoxel: true, clears: true)).Ui(Param.Count, "Grid size: Count x 8 voxels per side (8 .. 256)", Param.Scale, "Zoom: pixels per voxel = Scale x 4 x 64 / grid size (so the model keeps its on-screen size as Count changes)", Param.Op, "Shape (built-in sprite set; a loaded file set replaces it)", Param.MaskBits, "Views used (rebuilds the model):",
                Param.DotStep, "Blend of opposite views: 0=Dither 1=Lerp 2=Nearest 3=SurfaceOnly", Param.Blend, "Lighting tier: 0=None 1=Faces 2=Propagated 3=Smooth+AO",
                Param.Smooth, "Fit: proportional (checked) or stretched to the face", Param.Xor, "Cut the model open (front quarter removed) to see the interior blend", Param.Time, "Animate: slow auto-rotation")
                .Ops("Ball (3 discs)", "Box", "Cylinder", "Square top, round bottom", "Rocket").Bits(false, projViews).Range(Param.DotStep, 0, 3).Range(Param.Blend, 0, 3)
                .With(Param.MaskBits, 1 | 4 | 16).With(Param.Op, 3).With(Param.Blend, 3).With(Param.Count, 6).With(Param.Scale, 150);
            L[L.Count - 1].FreeOpIndex = -1;
            L[L.Count - 1].FileFilter = "Images|*.png;*.bmp;*.jpg;*.jpeg;*.gif|All files|*.*";
            L[L.Count - 1].FileMulti = true;
            L[L.Count - 1].Controls.Remove(Param.Camera); L[L.Count - 1].Controls[Param.Camera] = "Orbit: drag = yaw / pitch, middle drag = pan";

            VoxTest(T(GVoxel, "BIG voxel grid: Count x 128 per side (up to 1024^3 = 1 G voxels), NOT real-time - preview while you move, one full frame at rest, 'Measure fps' for timing", "A really large grid: Count x 128 voxels per side, cubic. 8 = 1024x1024x1024 = 1,073,741,824 cells = 9 GB for voxels + faces (the C# side allocates native memory; the test refuses when the estimate exceeds 85% of the available RAM). Filled procedurally by a 2-D noise heightmap (a column loop, no 3-D noise, so the fill takes seconds not minutes): sea, beaches, grass, rock, snow, a few strong lamps on posts (one per ~40 cells, each with its own pool of light) and a lit village on the flattest plateau. The bench does NOT loop this test: while you drag the camera or move a slider it draws a PREVIEW (a 1/4 or 1/8 resolution copy of the grid, built once - a few milliseconds), and half a second after the controls come to rest it renders ONE full frame and freezes. With 'Show progress' on, the status line names the phase (noise / columns / faces / light / render), the bar follows, and the render arrives in slabs far-to-near on screen (DrawVoxelsProgressive); with it off, the frame is one plain DrawVoxels call - compare the two with 'Measure fps', which renders 5 full frames back to back and reports the steady ms/frame and the equivalent fps in the status bar and the log. Lighting: Faces by default (no light buffer); pick Propagated or Smooth for the lamps - that adds 4 bytes per cell and a light pass (SIMD sweeps: ~0.1 s at 256^3, ~1 s at 512^3, ~10 s at 1024^3). Camera: free orbit (drag / wheel) or a preset; the same night / sky / energy controls as the other voxel tests.", c =>
            {
                int n = Math.Clamp(c.Count, 1, 8) * 128;
                var L2 = VoxLight(c);
                bool light = L2 >= VoxelLighting.Propagated;
                long need = VoxelGrid.MemoryEstimate(n, n, n, light);       // 9 B/voxel + 4 B light; the propagation needs no cell-sized scratch any more
                bool have = tvoxBig != null && tvoxBig.Width == n;          // already allocated: only the light buffer may still be needed
                long extra = have ? (light ? (long)n * n * n * 4 : 0) : need;
                var (availRam, totalRam) = AvailableRam();
                if (extra > availRam * 0.85)
                {
                    c.Canvas.ClearBuffer(unchecked((int)0xFF301010));
                    c.Note = $"REFUSED: {n}^3 needs about {need / (1 << 20)} MB ({(light ? "with" : "without")} light); available RAM {availRam / (1 << 20)} MB of {totalRam / (1 << 20)} MB - lower Count or pick the Faces tier";
                    return;
                }
                var (g, tBuild, tUpdate) = EnsureVoxelBig(n, light, VoxSky(c), c);
                var cam = VoxCam(c, MathF.Max(0.02f, c.Scale * 512f / n), out string camName);
                VoxBackground(c);
                if (c.Preview)
                {
                    // reduced copy (every k-th column, k = 4 or 8) in points mode: ~1/16 - 1/64 of the work, same camera framing
                    var (pg, k) = EnsureVoxelBigPreview(g);
                    var pcam = cam.WithScale(cam.Scale * k); pcam.Mode = c.Smooth ? VoxelMode.Points : VoxelMode.Cubes;
                    pg.SkyLight = g.SkyLight;
                    var swp = System.Diagnostics.Stopwatch.StartNew();
                    int pd = c.Canvas.DrawVoxels(pg, pcam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, L2);
                    c.Note = $"preview 1/{k}: {pd} voxels, {swp.Elapsed.TotalMilliseconds:F1} ms | {camName} | full grid {n}^3";
                    return;
                }
                // the full render in 16 slab chunks, far to near: the picture grows on screen and the bar shows the real
                // render progress (DrawVoxelsProgressive; painter's order is per slab, so chunks compose exactly)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int steps = n >= 512 ? 24 : 12;
                int drawn;
                if (c.Progress != null)                       // progress shown: chunked far-to-near, each chunk presented
                    drawn = c.Canvas.DrawVoxelsProgressive(g, cam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, L2, steps,
                        (done, total, sofar) => { c.Report($"render {done}/{total} slabs, {sofar / 1000} k voxels", (double)done / total, present: done < total); return true; }, parallel: c.Xor);
                else                                          // progress off: the one plain call a game would make
                    drawn = c.Xor ? c.Canvas.DrawVoxelsParallel(g, cam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, L2) : c.Canvas.DrawVoxels(g, cam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, L2);
                c.Note = $"{n}^3 = {(long)n * n * n / 1_000_000} M cells, {need / (1 << 20)} MB | {g.ExposedCount / 1000} k exposed, {drawn / 1000} k drawn | build {tBuild / 1000:F1} s, faces{(light ? " + light" : "")} {tUpdate / 1000:F2} s | this frame {sw.Elapsed.TotalMilliseconds:F0} ms | {camName}, {cam.EffectiveMode}, {L2}, {VoxFadeName(c)}{(c.Xor ? ", parallel" : "")}";
            }, needsVoxel: true, clears: true)).Ui(Param.Count, "Grid side = Count x 128 voxels (1..8 -> 128^3 .. 1024^3)", Param.Scale, "Zoom (pixels per voxel = Scale x 512 / side); wheel over the canvas")
                .With(Param.MaskBits, 2).With(Param.Count, 2).With(Param.Scale, 120);
            L[L.Count - 1].SlowFrames = 5;
            L[L.Count - 1].HasPreview = true;

            VoxTest(T(GVoxel, "Load a MagicaVoxel .vox or a Wavefront .obj (button 'Open file...' in the panel) and draw it with the camera controls", "Loads the file chosen with the 'Open file...' button. MagicaVoxel .vox: all models placed by the scene graph, palette colours, emissive materials (MATL _emit -> Voxel.Emit). Wavefront .obj: voxelised at Count x 32 voxels along the longest side; colours come from the .mtl referenced by 'mtllib' (Kd per material, per face) or from per-vertex colours ('v x y z r g b'); materials with a bright Ke (self-illumination) become emitters (ObjOptions.EmissiveStrength, default 12) - the built-in sample below shows exactly that: a pyramid on a slab with a glowing top from an in-memory .mtl. Closed meshes are filled (Fill selector: Auto / Solid / Shell). Without a file the built-in sample is generated (the house saved to .vox and loaded back + the obj text) so the test also runs in the suite. Camera / zoom / lighting as in the other voxel tests; the note shows load time, grid size, voxel and emitter counts.", c =>
            {
                var t = L.Find(x => x.Name.StartsWith("Load a MagicaVoxel", StringComparison.Ordinal))!;
                var (g, info, tLoad) = EnsureVoxelFile(t.FilePath, Math.Clamp(c.Count, 1, 32) * 32, c.DotStep);
                var Lt = VoxLight(c); g.SkyLight = VoxSky(c);
                var cam = VoxCam(c, MathF.Max(0.25f, c.Scale * 4f), out string camName);
                cam.Anchor = VoxelAnchor.Center;
                VoxBackground(c);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int drawn = c.Xor ? c.Canvas.DrawVoxelsParallel(g, cam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, Lt) : c.Canvas.DrawVoxels(g, cam, c.W / 2 + c.PanX, c.H / 2 + c.PanY, Lt);
                var st = g.Stats();
                c.Note = $"{info} | {g.Width}x{g.Height}x{g.Depth}, {st.solid} solid, {st.emitters} emitters, {drawn} drawn | load {tLoad:F1} ms | {camName}, x{cam.Scale:0.#}, {cam.EffectiveMode}, {Lt}, {VoxFadeName(c)} | draw {sw.Elapsed.TotalMilliseconds:F2} ms";
            }, needsVoxel: true, clears: true)).Ui(Param.Count, ".obj resolution: Count x 32 voxels along the longest side", Param.DotStep, ".obj fill: 0=Auto 1=Solid 2=Shell (rebuilds)").Range(Param.DotStep, 0, 2).With(Param.Scale, 200).With(Param.Count, 2);
            L[L.Count - 1].FileFilter = "Voxel / mesh files|*.vox;*.obj|MagicaVoxel (*.vox)|*.vox|Wavefront OBJ (*.obj)|*.obj|All files|*.*";

            // ===================================================== Old vs new
            T(GCompare, "Lines: DrawLine (old)  vs  PreciseDots  vs  DrawLine2 (new)", "The same fan three ways: LEFT original DrawLine with DotStep (a fixed number of dots along the major axis, so diagonals look sparser); MIDDLE the same call with PreciseDots: true (dots spaced evenly along the line); RIGHT DrawLine2 (dash = gap = DotStep px, also along the line). The difference only exists while the dot step is above 0 - at 0 all three draw solid lines and the panes are identical, so the slider starts at 3. Move the mouse to slide the three fan centres (each pane clips its own third; the angles themselves are fixed); 'XOR lines' flips all three to XOR.", c =>
            {
                int n = c.Count * 32, third = c.W / 3;
                for (int i = 0; i < n; i++)
                {
                    float a = i * MathF.PI * 2 / n;
                    int col = SR2D.ARGB(255, (byte)(128 + 127 * MathF.Sin(a)), (byte)(128 + 127 * MathF.Sin(a + 2)), (byte)(128 + 127 * MathF.Sin(a + 4)));
                    float rad = c.H * 0.45f;
                    int lx = third / 2 + (c.X - c.W / 2) / 4, ly = c.Y;
                    int ex = (int)(lx + MathF.Cos(a) * rad), ey = (int)(ly + MathF.Sin(a) * rad);
                    c.Canvas.SetLockRect(0, third, 0, c.H);
                    c.Canvas.DrawLine(lx, ly, ex, ey, col, c.DotStep, c.Xor);
                    c.Canvas.SetLockRect(third, 2 * third, 0, c.H);
                    c.Canvas.DrawLine(lx + third, ly, ex + third, ey, col, c.DotStep, c.Xor, PreciseDots: true);
                    c.Canvas.SetLockRect(2 * third, c.W, 0, c.H);
                    if (Caps.HasLine2) c.Canvas.DrawLine2(lx + 2 * third, ly, ex + 2 * third, ey, col, c.Xor ? SR2D.LineOp.Xor : SR2D.LineOp.Set, c.DotStep, c.DotStep);
                }
                c.Canvas.SetLockRect();
                c.Canvas.DrawLine(third, 0, third, c.H - 1, unchecked((int)0xFF808080));
                c.Canvas.DrawLine(2 * third, 0, 2 * third, c.H - 1, unchecked((int)0xFF808080));
            }, needsLine2: true, check: c => ThreeFansDrawn(c)).Ui(Param.DotStep, "Dot / dash step (px) - 0 makes all three panes identical solid lines", Param.Xor, "XOR lines", Param.Count, "Lines (x32)").With(Param.DotStep, 3);
            T(GCompare, "Layer: AlphaBlend (old)  vs  AlphaOver (new) on a transparent layer", "Both halves draw into a TRANSPARENT (all-zero) layer, then the layer is composited over a checkerboard. Top: glow sprites (Draw with AlphaBlend vs a premultiplied sprite with AlphaOver) - left the soft edges turn black (they blend towards the layer's black rgb), right they stay correct. Bottom: shapes (12 soft discs + a ring) written with LineOp.AlphaBlend vs LineOp.AlphaOver - the same effect on fills and strokes (folded in from the old 'Layer: shapes with AlphaOver' test).", c =>
            {
                // checkerboard background
                for (int y = 0; y < c.H; y += 32) for (int x = 0; x < c.W; x += 32)
                    c.Canvas.ClearRect(x, x + 32, y, y + 32, ((x / 32 + y / 32) & 1) == 0 ? unchecked((int)0xFF9090A0) : unchecked((int)0xFF606070));
                int half = c.W / 2;
                using var layer = new Sprite(half, c.H);           // zeroed = fully transparent
                using var pre = new Sprite(c.A.Alpha); pre.Premultiply();
                for (int side = 0; side < 2; side++)
                {
                    layer.ClearBuffer(0);
                    var op = side == 0 ? SR2D.Op.AlphaBlend : SR2D.Op.AlphaOver;
                    var spr = side == 0 ? c.A.Alpha : pre;
                    var r = new Random(5);
                    for (int i = 0; i < c.Count * 12; i++)
                    {
                        float a = (float)r.NextDouble() * 6.28f + c.Time * 0.4f;
                        layer.Draw(spr, (int)(half / 2 + MathF.Cos(a) * half * 0.26f) - c.S / 2 + (c.X - c.W / 2) / 4, (int)(c.H / 2 + MathF.Sin(a * 1.3f) * c.H * 0.22f) - c.S / 2, op);
                    }
                    // bottom row: shapes through the same layer - LineOp.AlphaBlend vs LineOp.AlphaOver
                    var lop = side == 0 ? SR2D.LineOp.AlphaBlend : SR2D.LineOp.AlphaOver;
                    float orb = half * 0.12f, cy = c.H * 0.87f;
                    for (int i = 0; i < 12; i++)
                    {
                        float a = i * MathF.PI / 6 + c.Time * 0.3f;
                        layer.FillCircle(half / 2f + MathF.Cos(a) * orb, cy + MathF.Sin(a) * orb, half * 0.035f, SR2D.ARGB(150, (byte)(128 + 127 * MathF.Sin(a)), 200, 80), lop, true);
                    }
                    layer.DrawCircle(half / 2f, cy, orb * 1.3f, unchecked((int)0xFFFFFFFF), 2f, true, lop);
                    c.Canvas.Draw(layer, side * half, 0, SR2D.Op.AlphaOver);   // composite the layer (premultiplied by construction)
                }
                c.Canvas.DrawLine(half, 0, half, c.H - 1, unchecked((int)0xFFFFFFFF));
            }, clears: true, needsOver: true, check: c =>
            {
                // both halves must composite the layer over the checkerboard substantially (the two sides are
                // SUPPOSED to differ - AlphaBlend darkens the glow's soft edges, that is the point of the test)
                var px = c.Canvas.Pixels; int half = c.W / 2; int changedL = 0, changedR = 0;
                for (int y = 0; y < c.H; y++) for (int x = 0; x < half; x++)
                {
                    int want = ((x / 32 + y / 32) & 1) == 0 ? unchecked((int)0xFF9090A0) : unchecked((int)0xFF606070);   // the bare checkerboard
                    if (px[y * c.W + x] != want) changedL++;
                    int wantR = ((x / 32 + y / 32) & 1) == 0 ? unchecked((int)0xFF9090A0) : unchecked((int)0xFF606070);
                    if (px[y * c.W + half + x] != wantR) changedR++;
                }
                int area = c.H * half;
                if (changedL < area / 20) return $"the AlphaBlend side barely changed the checkerboard ({changedL}/{area})";
                if (changedR < area / 20) return $"the AlphaOver side barely changed the checkerboard ({changedR}/{area})";
                return null;
            });
            T(GCompare, "Rotate: DrawRotate (old)  vs  DrawRotate2 (new)  vs  DrawRotateShear (lossless)", "Left: original DrawRotate (DRAW_ROT kernels). Middle: DrawRotate2 (DRAW_WARP). Right: DrawRotateShear (three shears, every source pixel exactly once, pixel-sharp, staircase outline). Same angle, same source, all three turning the same way (the original's angle is counter-clockwise, so it gets -Angle); 'Smooth' = AA / bilinear on the first two (the shear version never interpolates). Labels under each.", c =>
            {
                float a = c.Angle + c.Time * 0.5f;
                int y = c.H / 2, x0 = c.W / 6, x1 = c.W / 2, x2 = c.W * 5 / 6;
                for (int k = 0; k < c.Count; k++)
                {
                    c.Canvas.DrawRotate(c.A.Color, c.S / 2, c.S / 2, x0, y, -a, c.Smooth);          // -a: the original's angle is counter-clockwise
                    if (Caps.HasWarp) c.Canvas.DrawRotate2(c.A.Color, x1, y, a, 0, 0, -1, -1, SR2D.Op.Paint, Filt(c));
                    c.Canvas.DrawRotateShear(c.A.Color, x2, y, a, -1, -1, SR2D.Op.Paint);
                }
                int ly = Math.Min(c.H - 12, y + c.S * 3 / 4);
                c.Label(x0 - 40, ly, "DrawRotate"); c.Label(x1 - 44, ly, "DrawRotate2"); c.Label(x2 - 60, ly, "DrawRotateShear");
            }, warp: true).Ui(Param.Count, "Repetitions", Param.Angle, "Angle (deg)", Param.Smooth, "AA / bilinear (old, new)", Param.Time, "Animate: spin");
            T(GCompare, "Rotate: DrawRotate  original kernel  vs  UseWarp: true", "The SAME DrawRotate call (same arguments, same direction) - left through the original DRAW_ROT / DRAW_ROT_AA kernels, right with UseWarp: true (routed through DRAW_WARP, 1.3-2x faster). The pictures should be indistinguishable; nearest differs in ~0.05 % of edge pixels, bilinear by a few /255 at feature edges.", c =>
            {
                float a = c.Angle + c.Time * 0.5f;
                for (int k = 0; k < c.Count; k++)
                {
                    c.Canvas.DrawRotate(c.A.Color, c.S / 2, c.S / 2, c.W / 4, c.H / 2, a, c.Smooth, UseWarp: false);
                    if (Caps.HasWarp) c.Canvas.DrawRotate(c.A.Color, c.S / 2, c.S / 2, c.W * 3 / 4, c.H / 2, a, c.Smooth, UseWarp: true);
                }
            }, warp: true, check: c =>
            {
                // DRAW_ROT and its DRAW_WARP routing must agree: the description pins ~0.05 % differing edge
                // pixels for nearest. A wider gap means one of the two kernels drifted.
                var px = c.Canvas.Pixels; int half = c.W / 2; long diff = 0, drawn = 0;
                for (int y = 0; y < c.H; y++) for (int x = 0; x < half; x++)
                {
                    int l = px[y * c.W + x], r = px[y * c.W + (c.W - 1 - x)];
                    int lb = l >>> 24, rb = r >>> 24;
                    if ((lb > 16) != (rb > 16)) { diff++; continue; }
                    if (lb > 16)
                    {
                        drawn++;
                        if ((Math.Abs((l >>> 16 & 255) - (r >>> 16 & 255)) + Math.Abs((l >>> 8 & 255) - (r >>> 8 & 255)) + Math.Abs((l & 255) - (r & 255))) > 24) diff++;
                    }
                }
                if (drawn < 100) return "the rotation drew almost nothing";
                double frac = diff / (double)drawn;
                double limit = c.Smooth ? 0.10 : 0.01;
                return frac <= limit ? null : $"DRAW_ROT vs UseWarp differ on {frac:0.###} of the drawn pixels (limit {limit:0.###}, smooth={c.Smooth})";
            });
            T(GCompare, "Scale: RESIZE ctor (old)  vs  DrawScaled (new)", "Left: new Sprite(src, None, w, h) + Draw - the old way: the native RESIZE builds a NEW sprite (area average when it shrinks), so every copy allocates one. Right: no allocation, DrawScaled onto the canvas with 'Smooth' on = Bilinear / off = Nearest. Scale slider = the output size (x0.01 of the sprite); it starts at 150% because at the generic 100% w = the sprite size, neither side resamples anything and the two halves are two identical exact copies - nothing to compare. Count = copies per frame (the left allocates a sprite for each one).", c =>
            {
                int w = Math.Max(2, (int)(c.S * c.Scale)), h = w;
                for (int k = 0; k < c.Count; k++)
                {
                    using var s = new Sprite(c.A.Color, SR2D.Transform.None, w, h);
                    c.Canvas.Draw(s, c.W / 4 - w / 2, c.H / 2 - h / 2, SR2D.Op.Paint);
                    if (Caps.HasWarp) c.Canvas.DrawScaled(c.A.Color, c.W * 3 / 4 - w / 2, c.H / 2 - h / 2, w, h, SR2D.Op.Paint, Filt(c));
                }
            }, warp: true).Ui(Param.Scale, "Output size (x0.01 of the sprite; 100 = no resize at all)", Param.Smooth, "Right: Bilinear (off = Nearest)", Param.Count, "Copies per frame (the left allocates one sprite each)").With(Param.Scale, 150).With(Param.Smooth, true);

            // the list shows one header per group: order by group (stable, so the order inside a group is the order above)
            var order = new[] { GOriginal, GMask, GBump, GXform, GScene, GNew, GShapes, GText, GEdit, GLayers, GEffects, GBlend, GFiles, GVoxel, GControls, GCompare };
            return L.OrderBy(t => { int i = Array.IndexOf(order, t.Group); return i < 0 ? order.Length : i; }).ToList();
        }

        // ------------------------------------------------------------ helpers
        static SR2D.Filter Filt(Ctx c) => c.Smooth ? SR2D.Filter.Bilinear : SR2D.Filter.Nearest;

        // 2048x2048 test card for the downscale filters: 1-px grid lines, thin text-like strokes, fine checker
        static Sprite? card;
        static Sprite Card()
        {
            if (card != null) return card;
            var s = new Sprite(2048, 2048, SR2D.Op.Paint);
            s.ClearBuffer(unchecked((int)0xFF203040));
            for (int i = 0; i < 2048; i += 64) { s.ClearRect(i, i + 1, 0, 2048, unchecked((int)0xFFFFFFFF)); s.ClearRect(0, 2048, i, i + 1, unchecked((int)0xFFFFFFFF)); }
            for (int i = 0; i < 2048; i += 16) s.ClearRect(i, i + 1, 1024, 2048, unchecked((int)0xFF80C0FF));     // dense 1-px lines, lower half
            for (int y = 0; y < 1024; y += 2) for (int x = 1024; x < 2048; x += 2) s.SetPixel(x, y, unchecked((int)0xFFFFFF00)); // 1-px checker, top-right
            for (int i = 0; i < 40; i++) s.FillCircle(200 + i * 20, 300 + (i % 5) * 60, 4 + i % 3, unchecked((int)0xFFFF4040), SR2D.LineOp.Set, true);
            card = s; return s;
        }

        // black sprite with the alpha of `src` (for drop shadows); rebuilt when the asset changes
        static Sprite? shadow; static Sprite? shadowOf; static readonly object shadowLock = new object();
        // light backdrop so black shadows are visible (the canvas clear is near-black). ClearRect clips
        // to the lock rect, so under DrawParallel every band paints just its own part of it.
        static void LightBackdrop(Ctx c) => c.Canvas.ClearRect(0, c.W, 0, c.H, unchecked((int)0xFFB8C0C8));

        // per-thread effect chain for the "Effects:" tests (DrawParallel runs them on several threads)
        // flood-fill test scene + per-thread selection
        static Sprite? tscene; static readonly object sceneLock = new object();
        [ThreadStatic] static Selection? tsel;
        [ThreadStatic] static Effects? tfxSel;
        static Sprite BuildScene(int w, int h)
        {
            var s = new Sprite(w, h, SR2D.Op.Paint);
            for (int y = 0; y < h; y++) s.ClearRect(0, w, y, y + 1, SR2D.ARGB(255, (byte)(40 + 60 * y / h), (byte)(60 + 80 * y / h), (byte)(90 + 120 * y / h)));
            var r = new Random(5);
            for (int i = 0; i < 14; i++)
            {
                int col = SR2D.ARGB(255, (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256));
                float x = r.Next(w), y = r.Next(h), rad = 30 + r.Next(90);
                if ((i & 1) == 0) s.FillCircle(x, y, rad, col, SR2D.LineOp.Set, true); else s.FillRect(x - rad, y - rad * 0.6f, rad * 2, rad * 1.2f, col, SR2D.LineOp.Set, true);
            }
            for (int i = 0; i < 6; i++) s.DrawLine2(r.Next(w), 0, r.Next(w), h, unchecked((int)0xFF101010));
            return s;
        }
        // per-thread layered sprite for the "LayeredSprite" test (composes on the calling thread)
        [ThreadStatic] static LayeredSprite? tlayers; [ThreadStatic] static Assets? tlayersOf;
        // the stack references the asset sprites: rebuild it when the demo replaced them (Sprite size / test picture combo) - otherwise
        // it keeps drawing disposed (empty) sprites and layers "disappear"
        static LayeredSprite Layers(Ctx c)
        {
            if (tlayers == null || !ReferenceEquals(tlayersOf, c.A)) { tlayers?.Dispose(); tlayers = BuildLayers(c); tlayersOf = c.A; }
            return tlayers;
        }
        // depth-of-field stack (voxel slices): built once per (layer count, quality)
        // ---- voxel bench state ----------------------------------------------------------------
        static VoxelLighting VoxLightBits(int tier) => (VoxelLighting)Math.Clamp(tier, 0, 3);
        static VoxelGrid? tvox; static int tvoxSide = -1, tvoxSeed = -1; static double tvoxUpdate; static VoxelGrid? tvoxHouse;
        static VoxelGrid? tvoxRooms; static string tvoxRoomsKey = ""; static double tvoxRoomsUpd;

        /// <summary>The lights demo scene: three rooms in a row, a lamp in each (state from <see cref="LightsDemo"/>), rebuilt when a lamp / the reach / the sky changed.</summary>
        static (VoxelGrid g, double updateMs) EnsureVoxelRooms()
        {
            string key = LightsDemo.Key();
            if (tvoxRooms != null && tvoxRoomsKey == key) return (tvoxRooms, tvoxRoomsUpd);
            const int W = 48, H = 20, D = 12;
            var g = tvoxRooms ?? new VoxelGrid(W, H, D);
            g.Clear();
            var floor = new Voxel(0xFF8A8078); var wall = new Voxel(0xFFC8C0B0); var inner = new Voxel(0xFFB0A898);
            g.FillBox(0, 0, 0, W, H, 1, floor);                                          // floor slab z = 0
            g.FillBox(0, 0, 1, W, H, 2, new Voxel(0xFF9A9088));                          // walkable floor
            g.FillBox(0, H - 1, 2, W, H, D, wall);                                       // back wall (y = H-1)
            g.FillBox(0, 0, 2, 1, H, D, wall); g.FillBox(W - 1, 0, 2, W, H, D, wall);    // end walls
            g.FillBox(16, 0, 2, 17, H, D, inner); g.FillBox(32, 0, 2, 33, H, D, inner);  // two partitions -> three rooms
            g.FillBox(16, 8, 2, 17, 12, 7, Voxel.Empty); g.FillBox(32, 8, 2, 33, 12, 7, Voxel.Empty);   // doorways between the rooms
            g.FillBox(0, 8, 2, 1, 12, 7, Voxel.Empty);                                   // door to the outside (left end)
            g.FillBox(0, 0, 2, W, 1, 4, new Voxel(0xFF706860));                          // low front kerb (the front wall is cut away)
            // roof: only the back half, so the camera sees in and the sky comes in from the front
            g.FillBox(0, H / 2, D - 1, W, H, D, wall);
            // some furniture to catch the light: a table + a pillar per room
            for (int r = 0; r < 3; r++) { int x0 = r * 16 + 4; g.FillBox(x0, 4, 2, x0 + 5, 7, 4, new Voxel(0xFF6A5040)); g.FillBox(x0 + 8, 14, 2, x0 + 10, 16, D - 1, new Voxel(0xFF787068)); }
            for (int r = 0; r < 3; r++)
            {
                var lamp = LightsDemo.Lamps[r];
                int cx = r * 16 + 8, cy = 10, cz = 5;                                    // 3 cells above the floor
                g.FillBox(cx, cy, cz + 1, cx + 1, cy + 1, D - 1, new Voxel(0xFF404040));   // cord from the ceiling
                g.Set(cx, cy, cz, new Voxel(lamp.Color, lamp.On ? (byte)lamp.Strength : (byte)0, 9));
            }
            g.LightReach = LightsDemo.Reach; g.SkyLight = LightsDemo.Sky; g.SkyFromSides = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            g.Update();
            tvoxRoomsUpd = sw.Elapsed.TotalMilliseconds;
            tvoxRooms = g; tvoxRoomsKey = key;
            return (g, tvoxRoomsUpd);
        }
        internal static VoxelGrid EnsureVoxels(int side, int seed, out double lastUpdateMs)
        {
            if (tvox != null && tvoxSide == side && tvoxSeed == seed) { lastUpdateMs = tvoxUpdate; return tvox; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (tvox == null || tvoxSide != side) { tvox?.Dispose(); tvox = new VoxelGrid(side, side, Math.Max(2, side / 2)); tvoxSide = side; }
            var g = tvox; int n = side, d = g.Depth; var r = new Random(seed);
            var terrainNoise = new VoxelNoise { Scale = n / 3f, Octaves = 4, Persistence = 0.5f, Seed = 11 + seed };
            g.FillTerrain(terrainNoise, d * 0.55f, d * 0.35f, (x, y, z, depthBelow) => new Voxel(depthBelow == 0 ? 0xFF4C9A3C : depthBelow < 4 ? 0xFF8A5A2C : (z % 7 == 6 ? 0xFF6A6A6A : 0xFF7A7A7A), 0, (byte)(depthBelow == 0 ? 1 : 2)));
            if (n >= 16)
            {
                g.CarveNoise(new VoxelNoise { Scale = n / 5f, Octaves = 2, Threshold = 0.42f, Turbulence = false, Seed = 5 + seed, Gradient = -0.6f });   // caves, denser below
                // lamps of different colours and strengths on the surface: warm street lamps (14), dim orange candles (8),
                // cold blue-white strong ones (15), a few green and red ones - so the propagated light shows colour mixing
                Voxel[] lamps = { new Voxel(0xFFFFD070, 14, 9), new Voxel(0xFFFFA040, 8, 9), new Voxel(0xFFC0E0FF, 15, 9), new Voxel(0xFF60FF80, 11, 9), new Voxel(0xFFFF5050, 12, 9), new Voxel(0xFFFFFFFF, 6, 9) };
                int li = 0;
                g.Scatter(0.004f, lamps[0], seed + 3, (x, y, z) => { bool ok = !g.IsSolid(x, y, z) && z > 0 && g.IsSolid(x, y, z - 1); if (ok) li++; return ok; });
                g.Scatter(0.0025f, lamps[1], seed + 4); g.Scatter(0.0012f, lamps[2], seed + 5); g.Scatter(0.0008f, lamps[3], seed + 6); g.Scatter(0.0008f, lamps[4], seed + 7); g.Scatter(0.002f, lamps[5], seed + 8);
                g.FillBox(n / 2 - 3, n / 2 - 3, 0, n / 2 + 3, n / 2 + 3, d, new Voxel(0xFFC08040));            // the tower
                g.FillBox(n / 2 - 2, n / 2 - 2, 1, n / 2 + 2, n / 2 + 2, d - 1, Voxel.Empty);
                g.Set(n / 2, n / 2, 2, new Voxel(0xFF40C0FF, 15, 3));
                g.FillBox(n / 2 - 3, n / 2 - 1, 1, n / 2 - 2, n / 2 + 1, 4, Voxel.Empty);
                g.FillBox(n / 2 - 1, n / 2 - 1, d - 1, n / 2 + 1, n / 2 + 1, d, new Voxel(0xFFFF6020, 15, 9)); // beacon on top of the tower
                // fire pit: a ring of stones with embers of varying strength at a fixed spot
                int fx = n / 4, fy = n / 4, fz = 0; for (int z = d - 1; z > 0; z--) if (g.IsSolid(fx, fy, z)) { fz = z + 1; break; }
                if (fz > 0 && fz < d - 2) { g.FillCylinderZ(fx + 0.5f, fy + 0.5f, fz, fz + 1, 2.6f, new Voxel(0xFF505050)); g.FillCylinderZ(fx + 0.5f, fy + 0.5f, fz, fz + 1, 1.6f, new Voxel(0xFFFF8020, 13, 9)); g.Set(fx, fy, fz, new Voxel(0xFFFFE080, 15, 9)); g.Set(fx + 1, fy, fz, new Voxel(0xFFFF4010, 10, 9)); }
            }
            g.Update();
            tvoxUpdate = lastUpdateMs = sw.Elapsed.TotalMilliseconds;
            tvoxSeed = seed;
            return g;
        }
        internal static VoxelGrid EnsureVoxelHouse()
        {
            if (tvoxHouse != null) return tvoxHouse;
            var g = new VoxelGrid(24, 24, 24);
            var wall = new Voxel(0xFFD8C8A0); var roof = new Voxel(0xFFA03828); var lamp = new Voxel(0xFFFFD070, 15, 9);
            g.FillBox(0, 0, 0, 24, 24, 1, new Voxel(0xFF4C9A3C));                       // lawn
            g.ShellBox(4, 6, 1, 20, 20, 10, 1, wall);                                   // walls (hollow box)
            for (int k = 0; k < 8; k++) g.FillBox(3 + k, 5 + k, 10 + k, 21 - k, 21 - k, 11 + k, roof);   // roof pyramid
            g.FillBox(11, 6, 1, 13, 7, 5, Voxel.Empty);                                 // door (south wall, y = 6)
            g.FillBox(6, 6, 4, 9, 7, 7, Voxel.Empty); g.FillBox(15, 6, 4, 18, 7, 7, Voxel.Empty);   // windows
            g.FillBox(4, 11, 4, 5, 15, 7, Voxel.Empty); g.FillBox(19, 11, 4, 20, 15, 7, Voxel.Empty);  // side windows
            g.FillBox(7, 9, 3, 8, 10, 4, lamp); g.FillBox(16, 17, 3, 17, 18, 4, lamp);   // lamps inside
            g.FillBox(11, 11, 8, 13, 13, 9, new Voxel(0xFFFFE8C0, 15, 9));              // ceiling light in the middle of the house (lights every window)
            g.Set(7, 6, 7, new Voxel(0xFFFFD070, 13, 9)); g.Set(16, 6, 7, new Voxel(0xFFFFD070, 13, 9));   // lamps right behind the front windows
            g.Set(4, 13, 6, new Voxel(0xFF80C0FF, 12, 9)); g.Set(19, 13, 6, new Voxel(0xFF80C0FF, 12, 9)); // cold light behind the side windows
            g.Set(13, 5, 6, new Voxel(0xFFFFF0B0, 14, 9));                              // porch light over the door
            g.FillBox(17, 17, 10, 19, 19, 20, new Voxel(0xFF807060));                 // chimney
            g.Set(18, 18, 20, new Voxel(0xFFFF8030, 12, 9));                            // glowing top
            g.FillCylinderZ(2.5f, 2.5f, 1, 8, 0.5f, new Voxel(0xFF505050));             // lamp post
            g.Set(2, 2, 8, new Voxel(0xFFFFF0B0, 15, 9));
            g.Set(21, 21, 1, new Voxel(0xFF60FF80, 10, 9));                             // a green garden lamp at the back
            g.FillCone(21.5f, 2.5f, 1, 9, 2.5f, new Voxel(0xFF2C7A2C));                 // a tree
            g.FillCylinderZ(21.5f, 2.5f, 1, 3, 0.5f, new Voxel(0xFF6A4A2A));
            return tvoxHouse = g;
        }
        // ---- projections -----------------------------------------------------------------------
        static VoxelGrid? tvoxProj; static Sprite?[] tvoxProjSrc = new Sprite?[6]; static string tvoxProjKey = ""; static double tvoxProjMs; static string tvoxProjDesc = "";
        static int tvoxProjShape = -1; static string? tvoxProjFiles;
        static readonly int[] SideColours = { unchecked((int)0xFFE05050), unchecked((int)0xFF50A0E0), unchecked((int)0xFF60C060), unchecked((int)0xFFE0C040), unchecked((int)0xFFE0E0E0), unchecked((int)0xFF9060C0) };   // front back left right top bottom
        /// <summary>Built-in projection sprite sets (Front, Back, Left, Right, Top, Bottom), 64x64 each, one colour per side so you can see which view painted what.</summary>
        static Sprite?[] ProjShapes(int shape)
        {
            var r = new Sprite?[6];
            for (int i = 0; i < 6; i++)
            {
                var sp = new Sprite(64, 64, SR2D.Op.Paint); sp.ClearBuffer(0);
                int col = SideColours[i], dark = SR2D.ARGB(255, (byte)(((col >> 16) & 255) * 3 / 5), (byte)(((col >> 8) & 255) * 3 / 5), (byte)((col & 255) * 3 / 5));
                bool vertical = i < 4;   // side views: x across, y = height; top / bottom: x, y = north-south
                switch (shape)
                {
                    case 0:  // sphere: a disc everywhere, shaded rim so the sides differ
                        sp.FillCircle(32, 32, 30, col, AA: false); sp.FillCircle(32, 32, 24, dark, AA: false); sp.FillCircle(32, 32, 12, col, AA: false); break;
                    case 1:  // box: a square with a darker frame and a letter-like mark per side
                        sp.FillRect(2, 2, 60, 60, dark); sp.FillRect(8, 8, 48, 48, col); sp.FillRect(20 + i * 3, 20, 8, 24, dark); break;
                    case 2:  // cylinder (a can): rectangles around, discs top and bottom
                        if (vertical) { sp.FillRect(8, 2, 48, 60, col); sp.FillRect(8, 22, 48, 20, dark); } else { sp.FillCircle(32, 32, 24, col, AA: false); sp.FillCircle(32, 32, 10, dark, AA: false); }
                        break;
                    case 3:  // square top, round bottom: sides are trapezoids (wide top, narrower bottom); top = square, bottom = disc
                        if (vertical) sp.FillPolygon(new[] { new PointF(2, 2), new PointF(62, 2), new PointF(50, 62), new PointF(14, 62) }, col);
                        else if (i == 4) sp.FillRect(2, 2, 60, 60, col);
                        else sp.FillCircle(32, 32, 18, col, AA: false);
                        if (vertical) sp.FillRect(28, 2, 8, 60, dark);
                        break;
                }
                r[i] = sp;
            }
            if (shape == 4)
            {
                foreach (var sp in r) sp?.Dispose();
                r = new Sprite?[6];
                // the little rocket: front, back, side (used for left and right), top, bottom (nozzle)
                var f = new Sprite(48, 40, SR2D.Op.Paint); f.ClearBuffer(0);
                f.FillEllipse(24, 22, 9, 17, unchecked((int)0xFFE0E0E8), AA: false);
                f.FillPolygon(new[] { new PointF(15, 30), new PointF(3, 39), new PointF(17, 39) }, unchecked((int)0xFFE04040));
                f.FillPolygon(new[] { new PointF(33, 30), new PointF(45, 39), new PointF(31, 39) }, unchecked((int)0xFFE04040));
                f.FillPolygon(new[] { new PointF(24, 3), new PointF(16, 14), new PointF(32, 14) }, unchecked((int)0xFFE04040));
                f.FillCircle(24, 22, 4, unchecked((int)0xFF3060C0)); f.FillCircle(24, 22, 2, unchecked((int)0xFFFFFFFF));
                var b = new Sprite(48, 40, SR2D.Op.Paint); b.ClearBuffer(0);
                b.FillEllipse(24, 22, 9, 17, unchecked((int)0xFF404048), AA: false);
                b.FillPolygon(new[] { new PointF(15, 30), new PointF(3, 39), new PointF(17, 39) }, unchecked((int)0xFF802020));
                b.FillPolygon(new[] { new PointF(33, 30), new PointF(45, 39), new PointF(31, 39) }, unchecked((int)0xFF802020));
                b.FillPolygon(new[] { new PointF(24, 3), new PointF(16, 14), new PointF(32, 14) }, unchecked((int)0xFF802020));
                b.FillRect(22, 8, 4, 28, unchecked((int)0xFFFFD040)); b.FillRect(18, 24, 12, 8, unchecked((int)0xFF202020));
                var sd = new Sprite(40, 40, SR2D.Op.Paint); sd.ClearBuffer(0);
                sd.FillEllipse(20, 22, 9, 17, unchecked((int)0xFFC8C8D0), AA: false);
                sd.FillPolygon(new[] { new PointF(20, 3), new PointF(12, 14), new PointF(28, 14) }, unchecked((int)0xFFE04040));
                sd.FillPolygon(new[] { new PointF(20, 30), new PointF(20, 39), new PointF(34, 39), new PointF(28, 30) }, unchecked((int)0xFFE04040));
                var sd2 = new Sprite(sd, SR2D.Transform.FlipX);
                var t = new Sprite(48, 40, SR2D.Op.Paint); t.ClearBuffer(0);
                t.FillCircle(24, 20, 9, unchecked((int)0xFFE04040), AA: false);
                t.FillPolygon(new[] { new PointF(24, 20), new PointF(3, 38), new PointF(10, 39) }, unchecked((int)0xFFE04040));
                t.FillPolygon(new[] { new PointF(24, 20), new PointF(45, 38), new PointF(38, 39) }, unchecked((int)0xFFE04040));
                t.FillPolygon(new[] { new PointF(24, 20), new PointF(20, 39), new PointF(28, 39) }, unchecked((int)0xFFE04040));
                var bo = new Sprite(48, 40, SR2D.Op.Paint); bo.ClearBuffer(0);
                bo.FillCircle(24, 20, 9, unchecked((int)0xFF606068), AA: false); bo.FillCircle(24, 20, 5, unchecked((int)0xFFFF8020), AA: false);
                bo.FillPolygon(new[] { new PointF(24, 20), new PointF(3, 2), new PointF(10, 1) }, unchecked((int)0xFFE04040));
                bo.FillPolygon(new[] { new PointF(24, 20), new PointF(45, 2), new PointF(38, 1) }, unchecked((int)0xFFE04040));
                bo.FillPolygon(new[] { new PointF(24, 20), new PointF(20, 1), new PointF(28, 1) }, unchecked((int)0xFFE04040));
                r = new Sprite?[] { f, b, sd, sd2, t, bo };
            }
            return r;
        }
        /// <summary>Load up to six images as Front, Back, Left, Right, Top, Bottom: by file name when it contains a side name, else in order.</summary>
        static Sprite?[] ProjFiles(string files)
        {
            var r = new Sprite?[6]; var rest = new List<Sprite>();
            foreach (var f in files.Split('|'))
            {
                if (!File.Exists(f)) continue;
                Sprite sp; try { sp = new Sprite(f); } catch { continue; }
                string nm = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                int side = nm.Contains("front") ? 0 : nm.Contains("back") ? 1 : nm.Contains("left") ? 2 : nm.Contains("right") ? 3 : nm.Contains("top") ? 4 : nm.Contains("bottom") || nm.Contains("bot") ? 5 : -1;
                if (side >= 0 && r[side] == null) r[side] = sp; else rest.Add(sp);
            }
            int k = 0; foreach (var sp in rest) { while (k < 6 && r[k] != null) k++; if (k >= 6) { sp.Dispose(); continue; } r[k++] = sp; }
            return r;
        }
        static (VoxelGrid g, Sprite?[] src, double buildMs, string desc) EnsureVoxelProj(int n, int shape, int views, bool proportional, bool cut, VoxelBlend blend, string? files)
        {
            if (tvoxProjShape != shape || tvoxProjFiles != files || tvoxProjSrc[0] == null && tvoxProjSrc[4] == null)
            {
                foreach (var sp in tvoxProjSrc) sp?.Dispose();
                tvoxProjSrc = files != null ? ProjFiles(files) : ProjShapes(shape);
                if (files != null && Array.TrueForAll(tvoxProjSrc, x => x == null)) tvoxProjSrc = ProjShapes(shape);
                tvoxProjShape = shape; tvoxProjFiles = files; tvoxProjKey = "";
            }
            string key = $"{n}/{views}/{proportional}/{cut}/{blend}";
            if (tvoxProj != null && tvoxProjKey == key) return (tvoxProj, tvoxProjSrc, tvoxProjMs, tvoxProjDesc);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            tvoxProj?.Dispose();
            var opt = new ProjectionOptions { Fit = proportional ? VoxelFit.Proportional : VoxelFit.Stretch, Filter = SR2D.Filter.Nearest, Blend = blend, InteriorColor = blend == VoxelBlend.SurfaceOnly ? 0xFF606060 : null };
            var list = new List<(VoxelView, Sprite)>(); var names = new List<string>();
            for (int i = 0; i < 6; i++) if ((views >> i & 1) != 0 && tvoxProjSrc[i] != null) { list.Add(((VoxelView)i, tvoxProjSrc[i]!)); names.Add(((VoxelView)i).ToString().ToLowerInvariant()); }
            var g = VoxelGrid.FromProjections(n, n, n, list, opt);
            if (cut) g.FillBox(n / 2, 0, 0, n, n / 2, n, Voxel.Empty);
            g.Update();
            string desc = (names.Count == 0 ? "no views (empty)" : string.Join(" + ", names)) + ", " + blend + (proportional ? ", proportional" : ", stretched") + (files != null ? " [files]" : "");
            tvoxProj = g; tvoxProjKey = key; tvoxProjMs = sw.Elapsed.TotalMilliseconds; tvoxProjDesc = desc;
            return (g, tvoxProjSrc, tvoxProjMs, desc);
        }

        // ---- big grid ------------------------------------------------------------------------
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MemStatusEx { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemStatusEx b);
        /// <summary>(available, total) physical memory in bytes; falls back to the GC's view where the Win32 call is unavailable.</summary>
        static (long avail, long total) AvailableRam()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var m = new MemStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemStatusEx>() };
                    if (GlobalMemoryStatusEx(ref m)) return ((long)m.AvailPhys, (long)m.TotalPhys);
                }
            }
            catch { }
            var gi = GC.GetGCMemoryInfo();
            long total = gi.TotalAvailableMemoryBytes;
            long avail = total;
            try { foreach (var line in File.ReadLines("/proc/meminfo")) if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) { avail = long.Parse(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture) * 1024; break; } } catch { }
            return (Math.Min(avail, total), total);
        }
        static VoxelGrid? tvoxBig; static string tvoxBigKey = ""; static double tvoxBigBuild, tvoxBigUpdate;
        static (VoxelGrid g, double buildMs, double updateMs) EnsureVoxelBig(int n, bool light, int sky, Ctx c)
        {
            string key = $"{n}/{light}/{sky}";
            if (tvoxBig != null && tvoxBigKey == key) return (tvoxBig, tvoxBigBuild, tvoxBigUpdate);
            if (tvoxBig == null || tvoxBig.Width != n)
            {
                tvoxBig?.Dispose(); tvoxBig = null;
                GC.Collect();
                c.Report($"allocating {n}^3", 0);
                var sw0 = System.Diagnostics.Stopwatch.StartNew();
                var g0 = new VoxelGrid(n, n, n);
                // 1) heightmap: 2-D fractal noise sampled once per column (parallel rows)
                var noise = new VoxelNoise { Scale = n / 4f, Octaves = 5, Persistence = 0.55f, Seed = 21 };
                var ridge = new VoxelNoise { Scale = n / 9f, Octaves = 2, Turbulence = true, Seed = 7 };
                var grass = new Voxel(0xFF4C9A3C, 0, 1); var dirt = new Voxel(0xFF8A5A2C, 0, 2); var rock = new Voxel(0xFF7A7A7A, 0, 3); var snow = new Voxel(0xFFF0F4FF, 0, 4); var water = new Voxel(0xFF3060C0, 0, 5);
                Voxel[] lamps = { new Voxel(0xFFFFD070, 14, 9), new Voxel(0xFFFFA040, 9, 9), new Voxel(0xFFC0E0FF, 15, 9), new Voxel(0xFF60FF80, 11, 9), new Voxel(0xFFFF5050, 12, 9) };
                int sea = (int)(n * 0.30f), snowLine = (int)(n * 0.62f);
                var hgt = new int[n * n];
                c.Report($"heightmap noise {n}x{n}", 0.05);
                System.Threading.Tasks.Parallel.For(0, n, y => { for (int x = 0; x < n; x++) hgt[y * n + x] = Math.Clamp((int)(n * 0.35f + n * 0.30f * noise.Sample(x, y, 0.5f) - n * 0.12f * ridge.Sample(x, y, 0.5f)), 1, n - 2); });
                // column fill in 8 y-bands so the bar moves during the longest build phase
                c.Report("filling columns", 0.15);
                // 2) column fill: 3-4 bands per column (rock / dirt / surface / water), one delegate call per column, the
                //    bulk is plain 8-byte stores - FromColumns, parallel rows. (~0.6 s for 512^3, ~5 s for 1024^3)
                VoxelGrid.ColumnFiller fill = (x, y, b) =>
                {
                    int top = hgt[y * n + x];
                    Voxel surf = top > snowLine ? snow : top <= sea + 2 ? dirt : grass;
                    int k = 0;
                    b[k++] = (Math.Max(0, top - 4), rock); b[k++] = (top - 1, dirt); b[k++] = (top, surf);
                    if (top < sea) b[k++] = (sea, water);
                    return k;
                };
                const int bandsN = 8;                        // in 8 row bands so the bar moves during the longest build phase
                for (int bnd = 0; bnd < bandsN; bnd++)
                {
                    g0.FromColumns(4, fill, n * bnd / bandsN, n * (bnd + 1) / bandsN);
                    c.Report($"filling columns {bnd + 1}/{bandsN}", 0.15 + 0.4 * (bnd + 1) / bandsN);
                }
                c.Report("placing lamps + village", 0.55);
                // a few strong lamps on the land (one per ~40x40 cells, so each one has its own visible pool of light) ...
                int lampStep = Math.Max(32, n / 8);                         // 128^3: 4x4 lamps, 512^3: 8x8 - each pool (radius 15 cells) stands alone
                for (int y = lampStep / 2; y < n; y += lampStep) for (int x = lampStep / 2 + (y / lampStep % 2) * lampStep / 2; x < n; x += lampStep)
                {
                    int t = hgt[y * n + x]; if (t <= sea || t + 4 >= n) continue;
                    // a short post with the lamp on top: the pool spreads over the ground instead of being buried in it
                    g0.FillBox(x, y, t, x, y, t + 1, new Voxel(0xFF404040));
                    g0.Set(x, y, t + 2, lamps[(x / lampStep + y / lampStep) % lamps.Length]);
                }
                // ... and a lit village (little houses with a lamp inside and a beacon) on the flattest high spot found on a coarse search
                int bx = n / 2, by = n / 2, bestVar = int.MaxValue;
                for (int y = 8; y < n - 8; y += Math.Max(1, n / 32)) for (int x = 8; x < n - 8; x += Math.Max(1, n / 32))
                {
                    int t = hgt[y * n + x]; if (t <= sea + 2 || t > snowLine) continue;
                    int v = 0; for (int dy = -6; dy <= 6; dy += 3) for (int dx = -6; dx <= 6; dx += 3) v += Math.Abs(hgt[(y + dy) * n + x + dx] - t);
                    if (v < bestVar) { bestVar = v; bx = x; by = y; }
                }
                var wall = new Voxel(0xFFD8C8A0); var roof = new Voxel(0xFFA03828);
                for (int hy = -1; hy <= 1; hy++) for (int hx = -1; hx <= 1; hx++)
                {
                    int cx = bx + hx * 6, cy = by + hy * 6, t = hgt[cy * n + cx]; if (t + 6 >= n) continue;
                    g0.ShellBox(cx - 2, cy - 2, t, cx + 2, cy + 2, t + 3, 1, wall); g0.FillBox(cx - 2, cy - 2, t + 3, cx + 2, cy + 2, t + 4, roof);
                    g0.FillBox(cx, cy - 2, t, cx + 1, cy - 1, t + 2, Voxel.Empty);                    // door
                    g0.Set(cx, cy, t + 1, new Voxel(0xFFFFE0A0, 15, 9));                              // lamp inside
                }
                { int t = hgt[by * n + bx]; if (t + 12 < n) { g0.FillCylinderZ(bx + 0.5f, by + 0.5f, t, t + 10, 0.6f, new Voxel(0xFF505050)); g0.Set(bx, by, t + 10, new Voxel(0xFFFF4040, 15, 9)); } }
                tvoxBigBuild = sw0.Elapsed.TotalMilliseconds;
                tvoxBigPrev?.Dispose(); tvoxBigPrev = null;
                tvoxBig = g0;
            }
            tvoxBig.SkyLight = sky;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // faces (one SIMD pass, ~0.25 s for 512^3) and, for the lit tiers, the light propagation (~2 s for 512^3):
            // both are single native calls, so the bar can only jump between them, not move inside them
            c.Report("faces (visibility)", 0.6);
            tvoxBig.UpdateFaces();
            if (light) { c.Report("light propagation (one native pass, no finer progress)", 0.7); tvoxBig.Update(); }
            tvoxBigUpdate = sw.Elapsed.TotalMilliseconds;
            tvoxBigKey = key;
            c.Report("rendering", 0.8);
            return (tvoxBig, tvoxBigBuild, tvoxBigUpdate);
        }

        // reduced copy of the big grid for the interactive preview: every k-th column, height / k, built once per grid
        static VoxelGrid? tvoxBigPrev; static int tvoxBigPrevK = 1;
        static (VoxelGrid g, int k) EnsureVoxelBigPreview(VoxelGrid g)
        {
            if (tvoxBigPrev != null) return (tvoxBigPrev, tvoxBigPrevK);
            int k = g.Width >= 512 ? 8 : 4, n = g.Width / k;
            var p = new VoxelGrid(n, n, n);
            System.Threading.Tasks.Parallel.For(0, n, y =>
            {
                for (int x = 0; x < n; x++) for (int z = 0; z < n; z++)
                {
                    // the centre column of the k x k block, topmost solid of the k cells wins (keeps the surface closed at the coarse scale)
                    int cx = x * k + k / 2, cy = y * k + k / 2;
                    for (int dz = k - 1; dz >= 0; dz--) { var v = g.Get(cx, cy, z * k + dz); if (v.IsSolid) { p.Set(x, y, z, v); break; } }
                }
            });
            p.Update();
            tvoxBigPrev = p; tvoxBigPrevK = k;
            return (p, k);
        }

        // ---- files ---------------------------------------------------------------------------
        static VectorImage? tvec; static string tvecKey = "", tvecInfo = "";
        const string SampleSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 200'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='#ffcc33'/><stop offset='1' stop-color='#ff3366'/></linearGradient><radialGradient id='r'><stop offset='0' stop-color='#fff'/><stop offset='1' stop-color='#3366ff'/></radialGradient></defs>"
            + "<rect x='10' y='10' width='180' height='180' rx='24' fill='url(#g)' stroke='#402' stroke-width='4'/><circle cx='70' cy='70' r='40' fill='url(#r)' fill-opacity='0.9'/>"
            + "<path d='M40 150 C 70 100, 110 190, 160 130' fill='none' stroke='#fff' stroke-width='8' stroke-linecap='round' stroke-dasharray='20 10'/>"
            + "<polygon points='150,30 165,70 205,70 172,95 185,135 150,110 115,135 128,95 95,70 135,70' fill='#33aa55' stroke='#062' stroke-width='3' stroke-linejoin='round' transform='translate(-10 5) scale(0.8) rotate(10 150 80)'/>"
            + "<ellipse cx='100' cy='170' rx='60' ry='14' fill='#202020' fill-opacity='0.35'/><path d='M30 120 h40 v-20 h-40 z M40 110 h20 v0' fill='#ffffff' fill-rule='evenodd' opacity='0.7'/></svg>";
        /// <summary>The vector picture's size on the canvas at Scale 1: the longer side = 1.5 x the sprite size.</summary>
        static SizeF VectorObjectSize(Ctx c, VectorImage img)
        {
            float fit = c.S * 1.5f / MathF.Max(1e-3f, MathF.Max(img.Width, img.Height));
            return new SizeF(MathF.Max(1f, img.Width * fit), MathF.Max(1f, img.Height * fit));
        }
        static (VectorImage img, string info) EnsureVector(string? path, int page)
        {
            string key = $"{path}/{page}";
            if (tvec != null && tvecKey == key) return (tvec, tvecInfo);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            VectorImage img; string info;
            try
            {
                if (path != null && File.Exists(path)) { img = VectorImage.Load(path, page); info = $"{Path.GetFileName(path)} ({img.Format}, load {sw.Elapsed.TotalMilliseconds:F1} ms)"; }
                else { img = VectorImage.FromSvg(SampleSvg); info = "built-in sample SVG (open an .svg / .eps / .pdf with the 'Open file...' button)"; }
            }
            catch (Exception ex) { img = VectorImage.FromSvg(SampleSvg); info = $"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message} - showing the sample"; }
            tvec = img; tvecKey = key; tvecInfo = info; return (img, info);
        }
        static VectorImage? tanim; static string tanimKey = "", tanimInfo = "";
        /// <summary>Release the animated-vector rasters / film when the demo closes (or before replacing the file).</summary>
        internal static void ReleaseAnimatedVectorCache() { tanim?.Cached.Dispose(); tanim = null; tanimKey = ""; tanimInfo = ""; }
        // built-in animated sample: only the track kinds the reader supports (d morph, animateTransform, animateMotion, alpha mask)
        const string AnimSampleSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 240 120'>"
            + "<rect width='240' height='120' fill='#1c2836'/>"
            + "<mask id='w' maskUnits='userSpaceOnUse' maskContentUnits='userSpaceOnUse' mask-type='alpha'><rect x='8' y='8' width='150' height='104' fill='#ffffff'>"
            + "<animateTransform attributeName='transform' type='translate' values='0 0; 74 0; 0 0' keyTimes='0;0.5;1' dur='3s' repeatCount='indefinite'/></rect></mask>"
            + "<g mask='url(#w)'><rect x='8' y='8' width='224' height='104' fill='#22405e'/>"
            + "<circle cx='45' cy='60' r='20' fill='#ffcc33'><animateTransform attributeName='transform' type='translate' values='-25 0; 145 0; -25 0' keyTimes='0;0.5;1' dur='3s' repeatCount='indefinite'/></circle></g>"
            + "<path fill='#e05555' d='M190,30 L215,45 L190,60 Z'><animate attributeName='d' values='M190,30 L215,45 L190,60 Z; M185,25 L220,60 L190,68 Z; M190,30 L215,45 L190,60 Z' keyTimes='0;0.5;1' dur='1.5s' repeatCount='indefinite'/></path>"
            + "<circle r='6' fill='#40c080'><animateMotion path='M15,100 C60,70 120,120 160,95' dur='3s' repeatCount='indefinite'/></circle>"
            + "<rect x='205' y='80' width='12' height='12' fill='#ff00ff'><animateTransform attributeName='transform' type='translate' values='0 0; 0 -30' keyTimes='0;0.5' calcMode='discrete' dur='2s' repeatCount='indefinite'/></rect></svg>";
        /// <summary>The animated SVG of the 'Animated SVG' test (cached; falls back to the built-in sample on error).</summary>
        static (VectorImage img, string info) EnsureAnimVector(string? path)
        {
            string key = path ?? "";
            if (tanim != null && tanimKey == key) return (tanim, tanimInfo);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            VectorImage img; string info;
            try
            {
                if (path != null && File.Exists(path)) { img = VectorImage.Load(path); info = $"{Path.GetFileName(path)} ({img.Format}, load {sw.Elapsed.TotalMilliseconds:F1} ms)"; }
                else { img = VectorImage.FromSvg(AnimSampleSvg); info = $"built-in sample ({img.Tracks.Count} tracks, {img.Duration:0.###} s loop) - open an animated .svg with 'Open file...'"; }
            }
            catch (Exception ex) { img = VectorImage.FromSvg(AnimSampleSvg); info = $"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message} - showing the sample"; }
            img.ClipViewport = true;   // scenes (the AI-made ones especially) park dust / parallax beyond the canvas; the demo shows the container's rectangle only
            tanim?.Cached.Dispose();
            tanim = img; tanimKey = key; tanimInfo = info; return (img, info);
        }
        static VoxelGrid? tvoxFile; static string tvoxFileKey = ""; static string tvoxFileInfo = ""; static double tvoxFileMs;
        static (VoxelGrid g, string info, double loadMs) EnsureVoxelFile(string? path, int objRes, int fill)
        {
            string key = $"{path}/{objRes}/{fill}";
            var fillMode = fill == 1 ? ObjFill.Solid : fill == 2 ? ObjFill.Shell : ObjFill.Auto;
            if (tvoxFile != null && tvoxFileKey == key) return (tvoxFile, tvoxFileInfo, tvoxFileMs);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            VoxelGrid g; string info;
            try
            {
                if (path != null && File.Exists(path) && path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                {
                    var opt = new ObjOptions { Fill = fillMode };
                    g = VoxelGrid.FromObj(path, objRes, opt);
                    string mtl = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", Path.GetFileNameWithoutExtension(path) + ".mtl");
                    info = $"{Path.GetFileName(path)} (.obj, {opt.Fill}, res {objRes}; colours from mtllib / vertex colours{(File.Exists(mtl) ? "" : "")})";
                }
                else if (path != null && File.Exists(path))
                {
                    g = VoxelGrid.LoadVox(path); info = $"{Path.GetFileName(path)} (.vox)";
                }
                else
                {
                    // built-in sample: the house through a .vox round trip + a small obj text (a pyramid on a slab) voxelised
                    var house = EnsureVoxelHouse(); byte[] vox = house.ToVox();
                    var back = VoxelGrid.LoadVox(vox);
                    // obj text WITH materials: the slab is grey stone, the pyramid sandstone, its tip a self-illuminating material (Ke) -> emitter
                    string mtlText = "newmtl stone\nKd 0.45 0.45 0.5\nnewmtl sand\nKd 0.82 0.63 0.38\nnewmtl glow\nKd 1 0.6 0.2\nKe 1 0.6 0.2\n";
                    string obj = "mtllib sample.mtl\no slab\nusemtl stone\nv -1 0 -1\nv 1 0 -1\nv 1 0 1\nv -1 0 1\nv -1 0.2 -1\nv 1 0.2 -1\nv 1 0.2 1\nv -1 0.2 1\nf 1 4 3 2\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n"
                               + "o pyramid\nusemtl sand\nv -0.6 0.2 -0.6\nv 0.6 0.2 -0.6\nv 0.6 0.2 0.6\nv -0.6 0.2 0.6\nv 0 1.1 0\nf 9 12 11 10\nf 9 10 13\nf 10 11 13\nf 11 12 13\nf 12 9 13\n"
                               + "o tip\nusemtl glow\nv -0.15 1.05 -0.15\nv 0.15 1.05 -0.15\nv 0.15 1.05 0.15\nv -0.15 1.05 0.15\nv 0 1.4 0\nf 14 17 16 15\nf 14 15 18\nf 15 16 18\nf 16 17 18\nf 17 14 18\n";
                    var mesh = VoxelGrid.FromObj(obj, Math.Max(8, objRes / 4), new ObjOptions { DefaultColor = 0xFFD0A060, Fill = fillMode, ResolveMtl = _ => mtlText });
                    g = new VoxelGrid(back.Width + mesh.Width + 4, Math.Max(back.Height, mesh.Height), Math.Max(back.Depth, mesh.Depth));
                    g.Paste(back, 0, 0, 0); g.Paste(mesh, back.Width + 4, 0, 0);
                    back.Dispose(); mesh.Dispose();
                    info = $"built-in sample: house via .vox round trip ({vox.Length} bytes) + obj text pyramid with .mtl colours and a Ke (glowing) tip (res {Math.Max(8, objRes / 4)}) - use 'Open file...' for your own";
                }
            }
            catch (Exception ex)
            {
                g = new VoxelGrid(8, 8, 8); g.FillBox(0, 0, 0, 8, 8, 8, new Voxel(0xFFFF4040));
                info = "FAILED: " + ex.Message;
            }
            tvoxFile?.Dispose(); tvoxFile = g; tvoxFileKey = key; tvoxFileInfo = info; tvoxFileMs = sw.Elapsed.TotalMilliseconds;
            return (g, info, tvoxFileMs);
        }

        static VoxelGrid? tvoxEdit; static int tvoxEditSeed = -1; static SR2D.Op tvoxEditOp; static double tvoxEditMs; static string tvoxEditDesc = "";
        static (VoxelGrid g, double buildMs, string desc) EnsureVoxelEdit(int seed, SR2D.Op op)
        {
            if (tvoxEdit != null && tvoxEditSeed == seed && tvoxEditOp == op) return (tvoxEdit, tvoxEditMs, tvoxEditDesc);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            tvoxEdit?.Dispose();
            const int N = 64; var g = new VoxelGrid(N, N, N);
            var rock = new Voxel(0xFF8A8A90, 0, 1); var ore = new Voxel(0xFFE0B040, 0, 2); var crust = 0xFF5A5040u;
            // asteroid: noise inside a sphere
            using (var ball = g.NewSelection().Where((x, y, z) => { float dx = x - 32, dy = y - 32, dz = z - 32; return dx * dx + dy * dy + dz * dz < 26 * 26; }))
            {
                g.FillNoise(new VoxelNoise { Scale = 20, Octaves = 3, Threshold = -0.35f, Seed = seed }, (x, y, z, v) => v > 0.35f ? ore : rock, sel: ball);
                g.CarveNoise(new VoxelNoise { Scale = 9, Octaves = 2, Threshold = 0.3f, Turbulence = true, Seed = seed + 1 }, ball);   // tunnels
            }
            g.RemoveIsolated();
            using (var surf = g.SelectSurface()) g.Paint(surf, crust);
            using (var cav = g.SelectCavities()) { cav.Shrink(1); g.Fill(cav, new Voxel(0xFF60FFC0, 10, 7)); }   // glowing pockets
            // primitives around it
            g.FillTorus(32, 32, 6, 20, 2.5f, new Voxel(0xFF4080FF, 0, 3));
            g.FillCone(8, 56, 0, 24, 6, new Voxel(0xFFC04040, 0, 4));
            g.FillCylinder(52, 8, 4, 60, 30, 30, 3, 3, new Voxel(0xFF40C040, 0, 5), round: true);
            g.ShellSphere(54, 54, 50, 8, 2, new Voxel(0xFFE0E0E0, 0, 6));
            g.DrawLine(2, 2, 2, 20, 30, 60, new Voxel(0xFFFF80FF, 6, 8), radius: 1f);
            g.FloodFill(8, 56, 0, new Voxel(0xFFFF6060, 0, 4));   // recolour the cone by flood (same colour region)
            string desc = "asteroid: noise + carve + surface paint + lit cavities; torus, cone, capsule, shell sphere, thick line";
            switch (op)
            {
                case SR2D.Op.AlphaTest: g.Shell(1); g.FillBox(32, 0, 32, 64, 64, 64, Voxel.Empty); desc = "Shell(1) of the model, upper-right quarter cut away"; break;
                case SR2D.Op.AlphaBlend: g.Invert(new Voxel(0xFF9090A0, 0, 1)); g.FillBox(32, 0, 0, 64, 32, 64, Voxel.Empty); desc = "Invert(): the empty space became solid (grey), quarter cut away"; break;
                case SR2D.Op.Add2D: g.Hollow(2); g.FillBox(32, 0, 0, 64, 32, 64, Voxel.Empty); desc = "Hollow(2), quarter cut away"; break;
            }
            g.Update();
            tvoxEdit = g; tvoxEditSeed = seed; tvoxEditOp = op; tvoxEditMs = sw.Elapsed.TotalMilliseconds; tvoxEditDesc = desc;
            return (g, tvoxEditMs, desc);
        }

        static LayeredSprite? tdof; static Sprite[]? tdofLayers; static int tdofN, tdofQ; static readonly object dofLock = new object();
        static Effects? tdofDiffuse;
        static void EnsureDof(int n, int q, bool threads)
        {
            if (tdof != null && tdofN == n && tdofQ == q) { tdof.Threads = threads ? 0 : 1; return; }
            tdof?.Dispose(); if (tdofLayers != null) foreach (var l in tdofLayers) l.Dispose();
            tdofN = n; tdofQ = q;
            tdofLayers = new Sprite[n];
            tdof = new LayeredSprite(256, 256) { PrefixCache = false, Threads = threads ? 0 : 1 };
            for (int i = 0; i < n; i++)
            {
                var l = tdofLayers[i] = new Sprite(256, 256, SR2D.Op.AlphaOver); l.ClearBuffer(0);
                float depth = n <= 1 ? 0 : (float)(n - 1 - i) / (n - 1);            // 1 = bottom (far), 0 = top (near)
                int r = (int)(depth * 40);
                Effects fx = q switch
                {
                    0 => r > 0 ? new Effects().Blur(r) : new Effects(),                                                // gaussian, full res (the old way)
                    1 => r > 0 ? new Effects().Blur(r, BlurQuality.Fast, Effects.AutoDownscale) : new Effects(),        // 2-pass + auto downscale
                    _ => r > 0 ? new Effects().Blur(r, BlurQuality.Box, Effects.AutoDownscale) : new Effects(),         // box + auto downscale (cheapest)
                };
                tdof.Add(l, fx);
            }
        }
        // one frame of "cellular automaton" slices: a moving blob of voxels per layer, depth-coloured
        static void DrawDofFrame(Ctx c, int n)
        {
            var layers = tdofLayers!;
            float t = c.Time;
            for (int i = 0; i < n; i++)
            {
                var l = layers[i]; l.ClearBuffer(0);
                float z = n <= 1 ? 0 : (float)i / (n - 1);
                // a few live cells per slice: circles moving on a helix through the stack, colour by depth
                byte g = (byte)(60 + 195 * z), b = (byte)(255 - 160 * z);
                int col = SR2D.ARGB(255, (byte)(120 + 100 * MathF.Sin(z * 6.28f + t)), g, b);
                for (int k = 0; k < 6; k++)
                {
                    float a = t * (0.6f + k * 0.11f) + z * 9f + k * 1.05f;
                    float x = 128 + 80 * MathF.Cos(a) * (0.4f + 0.6f * z), y = 128 + 80 * MathF.Sin(a * 1.3f);
                    l.FillCircle(x, y, 6 + 10 * (1 - z), col, SR2D.LineOp.Set, true);
                }
                // cellular "dust": deterministic per slice + frame, so it flickers like an automaton
                uint h = (uint)(i * 7919 + (int)(t * 12));
                for (int k = 0; k < 40; k++) { h = h * 1664525u + 1013904223u; int x = (int)(h >> 24), y = (int)((h >> 16) & 255); l.SetPixel(x, y, col); l.SetPixel(x + 1, y, col); l.SetPixel(x, y + 1, col); l.SetPixel(x + 1, y + 1, col); }
            }
            tdof!.Invalidate(0);
        }
        [ThreadStatic] static LayeredSprite? tblend; [ThreadStatic] static Assets? tblendOf;
        [ThreadStatic] static List<Sprite>? tblendSrc;   // the stack does NOT own its source sprites - release them with it (they leaked per rebuild before)
        static LayeredSprite BlendLayers(Ctx c)
        {
            if (tblend == null || !ReferenceEquals(tblendOf, c.A) || tblend.Width != Math.Max(64, c.W / 2) || tblend.Height != Math.Max(64, c.H / 2))
            {
                tblend?.Dispose();
                if (tblendSrc != null) foreach (var s in tblendSrc) s.Dispose();
                tblend = BuildBlendLayers(c, out tblendSrc); tblendOf = c.A;
            }
            return tblend;
        }
        /// <summary>Stack for the blend-mode layer test: picture, Multiply vignette, Screen streak, Color tint, and the interactive top layer (alpha sprite).</summary>
        static LayeredSprite BuildBlendLayers(Ctx c, out List<Sprite> owned)
        {
            int w = Math.Max(64, c.W / 2), h = Math.Max(64, c.H / 2);
            var ls = new LayeredSprite(w, h);
            owned = new List<Sprite>();
            var pic = new Sprite(w, h); pic.DrawScaled(c.A.Color, 0, 0, w, h, SR2D.Op.Paint, SR2D.Filter.Auto); owned.Add(pic);
            ls.Add(pic, null, 0, 0, SR2D.Op.Paint);
            var vig = new Sprite(w, h); vig.ClearBuffer(0); owned.Add(vig);
            vig.FillRect(0, 0, w, h, SpriteGradient.Radial(w / 2f, h / 2f, MathF.Max(w, h) * 0.7f, false, unchecked((int)0xFFFFFFFF), unchecked((int)0xFF202020)));
            ls.Add(vig, null, 0, 0, SR2D.Op.Multiply);
            var streak = new Sprite(w, h); streak.ClearBuffer(0); owned.Add(streak);
            streak.FillRect(0, 0, w, h, SpriteGradient.Linear(0, 0, w, h, false, 0, unchecked((int)0xFFFFE0A0), 0));
            ls.Add(streak, null, 0, 0, SR2D.Op.Screen).Opacity = 0.7f;
            var tint = new Sprite(w, h); tint.ClearBuffer(unchecked((int)0xFF3060C0)); owned.Add(tint);
            ls.Add(tint, null, 0, 0, SR2D.Op.Color).Opacity = 0.35f;
            ls.Add(c.A.Alpha, null, 0, 0, SR2D.Op.Overlay);      // a SHARED asset - not owned here
            return ls;
        }
        /// <summary>Suite check helper: every third of the Lines old-vs-new test must have drawn a real fan.</summary>
        static string? ThreeFansDrawn(Ctx c)
        {
            var px = c.Canvas.Pixels; int third = c.W / 3;
            for (int pane = 0; pane < 3; pane++)
            {
                int lit = 0;
                for (int y = 0; y < c.H; y++) for (int x = pane * third; x < (pane + 1) * third; x++)
                    if ((px[y * c.W + x] >>> 24) > 32) lit++;
                if (lit < 1000) return $"pane {pane + 1} of 3 drew almost nothing ({lit} lit px) - the fan collapsed";
            }
            return null;
        }
        static LayeredSprite BuildLayers(Ctx c)
        {
            int n = c.S + 96;
            var ls = new LayeredSprite(n, n);
            var src = new[] { c.A.Alpha, c.A.Color, c.A.Keyed };
            for (int i = 0; i < 12; i++)
            {
                var fx = (i % 4) switch
                {
                    0 => new Effects().Blur(3),
                    1 => new Effects().Wave(18f, 4f),
                    2 => new Effects().Color(Brightness: 0.05f * (i - 6), Saturation: 1.3f, Hue: i * 30f),
                    _ => new Effects().Shadow(3, 3, 4, unchecked((int)0xFF000000), 0.5f),
                };
                var s = src[i % 3];
                // integer offsets on a circle -> every layer takes the exact 1:1 path
                ls.Add(s, fx, n / 2 + (int)(36 * Math.Cos(i * 0.5236)) - s.Width / 2, n / 2 + (int)(36 * Math.Sin(i * 0.5236)) - s.Height / 2,
                       s == c.A.Keyed ? SR2D.Op.AlphaTest : SR2D.Op.AlphaOver);
            }
            ls.Effects = new Effects().Shadow(6, 6, 8, unchecked((int)0xFF000000), 0.5f);
            return ls;
        }
        [ThreadStatic] static Effects? tfx, tfx2;
        static Effects fx => tfx ??= new Effects();
        // the heat haze test renders its static scene into a full-canvas scratch so the displacement is screen-space
        static Sprite? thaze; static Assets? hazeOf; static readonly object hazeLock = new object();
        // the real-time motion echo of the effects test (shared across frames; Step is keyed on the animation time)
        static MotionEcho? mecho; static Sprite? esrc; static float lastEchoTime = -1;
        static readonly object echoLock = new();

        static Sprite Shadow(Sprite src)
        {
            lock (shadowLock)
            {
                if (!ReferenceEquals(shadowOf, src) || shadow == null || shadow.Width != src.Width || shadow.Height != src.Height)
                {
                    shadow?.Dispose();
                    shadow = new Sprite(src.Width, src.Height, SR2D.Op.AlphaBlend);
                    shadow.MoveByte(src, 0, 0, SR2D.ColChannel.ChAlpha, SR2D.ColChannel.ChAlpha);
                    shadowOf = src;
                }
                return shadow;
            }
        }

        static Sprite SrcFor(Ctx c, SR2D.Op op) => op switch
        {
            SR2D.Op.AlphaBlend => c.A.Alpha,
            SR2D.Op.AlphaTest => c.A.Keyed,
            _ => c.A.Color
        };

        /// <summary>
        /// Lays out Count copies of a sprite-sized cell: the first one centred under the
        /// mouse, the rest continuing to the right and wrapping downwards.
        /// </summary>
        static void Grid(Ctx c, Action<int, int> draw)
        {
            int x0 = c.X - c.S / 2, y0 = c.Y - c.S / 2;
            // Columns per row = every cell whose rectangle overlaps the canvas, counted from
            // the real x0 (which may be negative when the sprite hangs over the left edge).
            // The earlier version measured from max(0, x0), which dropped the partially
            // visible last column whenever x0 < 0 - that is what made the row-end sprite
            // vanish while dragging.  Rows below the first use the same column count, so a
            // sprite half outside the right border is drawn (and clipped) in every row.
            int per = Math.Max(1, (c.W - x0 + c.S - 1) / c.S);
            for (int k = 0; k < c.Count; k++)
            {
                int x = x0 + (k % per) * c.S, y = y0 + (k / per) * c.S;
                if (y >= c.H) break;               // fully below the canvas: nothing to draw
                draw(x, y);
            }
        }

        /// <summary>
        /// Count sprite-sized cells with <paramref name="gap"/> px between them as one block centred on the canvas: as many
        /// columns as fit, rows below, the whole block centred (one sprite = dead centre).
        /// </summary>
        static void SpacedGrid(Ctx c, int gap, Action<int, int> draw) => SpacedGrid(c, gap, Math.Max(1, c.Count), draw);
        static void SpacedGrid(Ctx c, int gap, int n, Action<int, int> draw)
        {
            int cell = c.S + gap;
            int per = Math.Max(1, Math.Min(n, (c.W + gap) / cell)), rows = (n + per - 1) / per;
            int x0 = (c.W - (per * cell - gap)) / 2, y0 = (c.H - (rows * cell - gap)) / 2;
            for (int k = 0; k < n; k++) draw(x0 + (k % per) * cell, y0 + (k / per) * cell);
        }
        /// <summary>
        /// Count sprite-sized cells cascading diagonally (each one a step down-right of the previous, overlapping it), the
        /// whole stack centred on the canvas. The step shrinks when the stack would not fit (S/6 px at most, 4 px at least).
        /// </summary>
        static void Cascade(Ctx c, Action<int, int> draw)
        {
            int n = Math.Max(1, c.Count);
            int step = Math.Max(4, Math.Min(c.S / 6, n > 1 ? Math.Min((c.W - c.S) / (n - 1), (c.H - c.S) / (n - 1)) : c.S / 6));
            int x0 = (c.W - c.S - (n - 1) * step) / 2, y0 = (c.H - c.S - (n - 1) * step) / 2;
            for (int k = 0; k < n; k++) draw(x0 + k * step, y0 + k * step);
        }
        /// <summary>
        /// Prescaled + precropped fill for one fractal region: the largest centred crop of <paramref name="src"/> with the
        /// region's aspect ratio, scaled to exactly the region size. Built once per layout change - drawing it per frame is
        /// a single plain Draw of a ready sprite (that is the point: no per-frame crop + scale).
        /// </summary>
        static Sprite Prescaled(Sprite src, Rectangle r)
        {
            int cw, ch;                                                                       // crop size in source pixels (the region's aspect)
            if ((long)r.Width * src.Height <= (long)r.Height * src.Width) { ch = src.Height; cw = Math.Max(1, (int)((long)r.Width * src.Height / Math.Max(1, r.Height))); }
            else { cw = src.Width; ch = Math.Max(1, (int)((long)r.Height * src.Width / Math.Max(1, r.Width))); }
            cw = Math.Min(cw, src.Width); ch = Math.Min(ch, src.Height);
            var crop = new Sprite(cw, ch);
            crop.Draw(src, (src.Width - cw) / 2, (src.Height - ch) / 2, SR2D.Op.Paint);       // clipped: the offset shifts the wanted crop into view
            var fill = new Sprite(r.Width, r.Height);
            fill.DrawRotate2(crop, r.Width / 2, r.Height / 2, 0f, r.Width, r.Height, -1, -1, SR2D.Op.Paint);
            crop.Dispose();
            return fill;
        }

        /// <summary>Exchanges two channels of a freshly built sprite. Both MoveByte calls read from the pristine <paramref name="src"/> -
        /// swapping in place would chain the second swap through the already-swapped byte (R->G then G->R would undo itself).</summary>
        static Sprite ChannelSwapped(Sprite src, SR2D.ColChannel a, SR2D.ColChannel b)
        {
            var dst = new Sprite(src.Width, src.Height);
            dst.Draw(src, 0, 0, SR2D.Op.Paint);
            dst.MoveByte(src, 0, 0, a, b);
            dst.MoveByte(src, 0, 0, b, a);
            return dst;
        }

        /// <summary>The per-region sprite cache of the &lt;new&gt; tests: rebuilt (old sprites disposed) whenever the key changes -
        /// copy count, viewport size, selected channels or the asset set. Region sprites are plain copies, so a frame is one
        /// Draw per copy and nothing is cropped or scaled per frame.</summary>
        sealed class RegionCache
        {
            long _key = long.MinValue; readonly object _gate = new();
            public readonly List<Sprite> Sprites = new();
            public void Ensure(long key, List<Rectangle> regs, Func<int, Sprite> build)
            {
                if (Match(key, regs.Count)) return;
                lock (_gate)
                {
                    if (Match(key, regs.Count)) return;
                    var fresh = new List<Sprite>(regs.Count);
                    for (int i = 0; i < regs.Count; i++) fresh.Add(build(i));
                    var old = new List<Sprite>(Sprites);
                    Sprites.Clear(); Sprites.AddRange(fresh); _key = key;
                    foreach (var sp in old) sp.Dispose();
                }
            }
            bool Match(long key, int n) => _key == key && Sprites.Count == n;
        }

        /// <summary>Tile caption INSIDE its fractal region (bottom-left: never collides with the info panel in the top-left or with the
        /// neighbouring tiles), at the largest pixel-font scale that fits the region width - full name where it fits, a short form
        /// for narrow regions, clipped letters as a last resort (labels used to be cut off at small viewport sizes).</summary>
        static void OpLabel(Ctx c, Rectangle r, string name)
        {
            string shortName = name switch { "AlphaTest" => "ATst", "AlphaBlend" => "ABld", "Add2D" => "Add2", "Mul2X" => "Mul2", "Paint" => "Pnt", _ => name.Length > 4 ? name[..4] : name };
            string text = r.Width >= name.Length * 6 + 8 ? name : shortName;
            if (r.Width < text.Length * 6 + 8) text = text[..Math.Max(1, Math.Min(text.Length, (r.Width - 8) / 6))];
            int scale = r.Width >= text.Length * 12 + 12 && r.Height >= 34 ? 2 : 1;
            c.Canvas.DrawText(r.X + 3, r.Bottom - 3, text, unchecked((int)0xFFFFFFFF), unchecked((int)0x90000000), TextAnchor.BottomLeft, scale);
        }

        /// <summary>Alpha-style checker over the whole canvas (cells = S/4 px, mid / dark grey), shifted by (ox, oy) px - one TileDraw of a cached 2x2-cell tile.</summary>
        [ThreadStatic] static Sprite? tchecker; [ThreadStatic] static int tcheckerCell;
        static void Checker(Ctx c, int ox, int oy)
        {
            int cell = Math.Max(4, c.S / 4);
            if (tchecker == null || tcheckerCell != cell)
            {
                tchecker?.Dispose(); tchecker = new Sprite(cell * 2, cell * 2); tcheckerCell = cell;
                tchecker.ClearBuffer(unchecked((int)0xFF5C5C60));
                tchecker.ClearRect(0, cell, 0, cell, unchecked((int)0xFF38383C)); tchecker.ClearRect(cell, cell * 2, cell, cell * 2, unchecked((int)0xFF38383C));
            }
            c.Canvas.TileDraw(tchecker, 0, 0, c.W, c.H, ox, oy, SR2D.Op.Paint);
        }
        /// <summary>
        /// Rotation tests: the second, self-spinning sprite at the right of the viewport (3/4 W, centre height), turning
        /// with Time around its own centre - the counterpart of the interactive one at the left. Labelled.
        /// </summary>
        static void SelfSpinner(Ctx c, Action<int, int, float> draw)
        {
            int x = c.W * 3 / 4, y = c.H / 2;
            draw(x, y, c.Time * 0.8f);
            c.Label(x - 70, y + (int)(c.S * 0.72f) + 8, "spins by itself (Time), pivot = centre");
        }
        /// <summary>Bump-map layout: sprites stay put (grid from the top-left), the light follows the mouse.</summary>
        static void FixedGrid(Ctx c, Action<int, int> draw)
        {
            int per = Math.Max(1, c.W / c.S);
            for (int k = 0; k < c.Count; k++) draw((k % per) * c.S, (k / per) * c.S);
        }
        /// <summary>Like FixedGrid but the block of Count cells is centred on the canvas (one sprite = dead centre); the mouse is free for the light.</summary>
        static void CentredGrid(Ctx c, Action<int, int> draw)
        {
            int n = Math.Max(1, c.Count), per = Math.Max(1, Math.Min(n, c.W / c.S)), rows = (n + per - 1) / per;
            int x0 = (c.W - per * c.S) / 2, y0 = (c.H - rows * c.S) / 2;
            for (int k = 0; k < n; k++) draw(x0 + (k % per) * c.S, y0 + (k / per) * c.S);
        }
        [ThreadStatic] static Sprite? tand;   // MaskInterSector visualisation scratch
        [ThreadStatic] static Sprite? tcell;  // Scene B's tile cell (a real S x S sprite: a CreateView would not clip a blit)
        [ThreadStatic] static Sprite? tworld; // MaskInterSector collision map (canvas-sized, bit values only)
        static Sprite MaskWorld(int w, int h) { if (tworld == null || tworld.Width != w || tworld.Height != h) { tworld?.Dispose(); tworld = new Sprite(w, h); } return tworld; }
        // environment image for the EBM tests shifted by the mouse (sun position) and scaled by Brite - a 256x256 TileDraw, ~free
        [ThreadStatic] static Sprite? tenv;
        static Sprite ShiftedEnv(Ctx c)
        {
            var e = c.A.Env;
            if (tenv == null || tenv.Width != e.Width || tenv.Height != e.Height) { tenv?.Dispose(); tenv = new Sprite(e.Width, e.Height); }
            float t = c.Time * 0.5f;
            int ox = (int)((c.X - c.W / 2) * 0.5f + MathF.Cos(t) * 40), oy = (int)((c.Y - c.H / 2) * 0.5f + MathF.Sin(t) * 40);
            tenv.TileDraw(e, 0, 0, e.Width, e.Height, ox, oy);
            int m = (int)Math.Clamp(c.Brite * 128, 0, 255);       // S2X: mul 128 = x1.00, so Brite 100 = the environment unchanged
            // add 128 = +0: an add of 0 is MINUS 256 per byte, which would punch the whole environment to black
            tenv.MulAddS2X(tenv, 0, 0, SR2D.ARGB(128, (byte)m, (byte)m, (byte)m), SR2D.ARGB(128, 128, 128, 128));
            return tenv;
        }
    }

    /// <summary>
    /// Strip of the motion blur test: the trail curve editor (SpriteCurveEditor over <see cref="TrailCurve"/>).
    /// The curve bends the trail perpendicular to its direction: x walks the trail (0 = first tap, 1 = last),
    /// y is the bend in ± half lengths around the centre line (the default is the flat centre line).
    /// </summary>
    internal static class MotionDemo
    {
        public const int StripHeight = 216;
        /// <summary>The trail curve the motion test samples (x along the trail 0..1, y = bend, 0.5 = the centre line).</summary>
        public static readonly Curve TrailCurve = Flat();
        static MotionDemo()
        {   // the CURVE mode has to bend from the first frame: over the flat centre line the path IS the straight
            // trail (perp = 0 for every tap), so the demo would show nothing until the strip is edited by hand.
            TrailCurve.Set(new[] { new PointF(0f, 0.5f), new PointF(0.32f, 0.1f), new PointF(0.66f, 0.9f), new PointF(1f, 0.5f) });
        }
        /// <summary>The WinForms strip (built in demo/ControlsDemo.cs as MotionStrip; the headless runner stubs it).</summary>
        public static Control Build() => MotionStrip.Build();

        public static Curve Flat()
        {
            var c = new Curve { Clamp01 = false };
            c.Reset();
            c.Move(0, 0f, 0.5f); c.Move(1, 1f, 0.5f);           // the diagonal -> the flat centre line
            return c;
        }

        /// <summary>
        /// The MotionBlurPath points for the current curve: <paramref name="length"/> px along the
        /// <paramref name="angleDeg"/> direction, bent perpendicular by the curve (the y excursion is
        /// scaled by <paramref name="bend"/>, 0 = a straight trail, 1 = the curve at full ± half length).
        /// </summary>
        public static System.Drawing.PointF[] Path(float angleDeg, float length, float bend, int points = 24)
        {
            var pts = new System.Drawing.PointF[points];
            float a = angleDeg * MathF.PI / 180f, ca = MathF.Cos(a), sa = MathF.Sin(a);
            for (int k = 0; k < points; k++)
            {
                float t = (float)k / (points - 1);
                float v = (float)TrailCurve.Evaluate(t) - 0.5f;      // 0 = the centre line
                float along = t * length, perp = v * 2f * bend * length;
                pts[k] = new System.Drawing.PointF(along * ca - perp * sa, along * sa + perp * ca);
            }
            return pts;
        }

    }

    /// <summary>The demo's text face: the first installed family found from a preference list (Segoe UI on Windows, DejaVu Sans on Linux, ...), loaded once.</summary>
    internal static class DemoFont
    {
        static SpriteFont? font; static bool tried;
        public static SpriteFont? Get()
        {
            if (tried) return font; tried = true;
            foreach (var fam in new[] { "Segoe UI", "Arial", "Verdana", "Tahoma", "DejaVu Sans", "Liberation Sans", "Noto Sans", "Helvetica" })
            {
                try { font = SpriteFont.Installed(fam); } catch { font = null; }
                if (font != null) return font;
            }
            try { var fams = SpriteFont.SystemFamilies(); if (fams.Count > 0) font = SpriteFont.Installed(fams[0]); } catch { font = null; }
            return font;
        }
    }
}
