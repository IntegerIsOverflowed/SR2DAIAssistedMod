// SR2D demo - the two selection-scoped move showcases:
// MoveDemo   "grid shuffle": Lenna is cut into a grid, one cell erased; every step a RANDOM neighbour of the hole is
//            selected and ANIMATED into it with Sprite.Move (the Photoshop move tool) - the motion follows the
//            TANGENT picked in the Op box (Track.EaseAt, cs/Animation.cs; "Curve" = the SpriteCurveEditor on the
//            strip), drawn as a little progress graph in the canvas's top-right (the info area).
// OffsetDemo three Offset showcases, one test each:
//   Scramble  - random strips shift along themselves with Sprite.Offset (wrap = the strip's bounding box, like
//               Photoshop Filter > Other > Offset), a strip never repeats back to back, the distance is snapped to
//               the grid but capped by the picture size, the Op box picks the tangent.
//   Selection - a Photoshop-style selection tool (rectangle / ellipse / lasso buttons + a PEN button + clear);
//               the shape tools only select, the pen only paints. The Offset X / Y sliders push the selection
//               around 1:1 ("Wrap rows / cols" = the spans wrap instead of the bounding box).
//   Chaos     - all 512 single-pixel lines (rows AND columns, random positions, random directions, random amounts)
//               rotate continuously - the Workers slider sets how many lines advance at once (they really do run in
//               parallel), the Op box picks the tangent shaping, no pauses: a finished line starts anew at once.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace Sr2d64CSport
{
    /// <summary>Shared grid maths and painting for the two block demos: the cell edge is a POWER OF TWO (8 16 32 64 128 256), so it always divides the 512 px board evenly - no leftover pixels.</summary>
    internal static class MoveGrid
    {
        internal const int BoardSize = 512;
        /// <summary>The cell edge for a Grid slider value: snapped to the nearest power of two (the slider already offers only those).</summary>
        public static int CellOf(int slider)
        {
            int v = Math.Clamp(slider, 8, BoardSize), p = 8;
            while (p < v) p <<= 1;                                              // first power of two >= v
            return p > 8 && v - (p >> 1) < p - v ? p >> 1 : p;                  // nearest one (8 stays 8)
        }
        public static int ColsOf(int cell) => BoardSize / cell;
        /// <summary>The semi-transparent gray grid (the "Grid lines" check box toggles it).</summary>
        public static void DrawLines(Ctx c, Sprite canvas, int bx, int by, int cell, int cols)
        {
            if (!c.ShowGrid) return;
            var gridCol = unchecked((int)0x50808080);
            for (int i = 1; i < cols; i++)
            {
                canvas.FillRect(bx + i * cell, by, 1, BoardSize, gridCol);
                canvas.FillRect(bx, by + i * cell, BoardSize, 1, gridCol);
            }
        }
        /// <summary>The shared pen painting (board-space circles along the pointer path).</summary>
        public static void Paint(Ctx c, Sprite board, int bx, int by)
        {
            float px = c.PenX - bx, py = c.PenY - by;
            if (!c.PenDown) { penWasDown = false; return; }
            const int ink = unchecked((int)0xFFFF3050);
            const float r = 3f;
            if (!penWasDown) board.FillCircle(px, py, r, ink);
            else
            {
                float dx = px - penPrevX, dy = py - penPrevY;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                int n = Math.Max(1, (int)(len / 1.5f));
                for (int i = 0; i <= n; i++) board.FillCircle(penPrevX + dx * i / n, penPrevY + dy * i / n, r, ink);
            }
            penWasDown = true; penPrevX = px; penPrevY = py;
        }
        static bool penWasDown; static float penPrevX, penPrevY;

        /// <summary>How the demos shape motion: a tangent mode or, at index 7, the user's curve from the strip editor.</summary>
        public static double Shape(int modeIndex, float u)
        {
            u = Math.Clamp(u, 0f, 1f);
            if (modeIndex == 7) return TangentTools.Motion.Evaluate(u);         // the SpriteCurveEditor on the strip
            return Track.EaseAt((TangentMode)Math.Clamp(modeIndex, 0, 6), u);
        }
        /// <summary>The tangent progress graph, in the canvas's top-right (the info area - never over the sprite).</summary>
        public static void TangentGraph(Sprite canvas, int modeIndex, float u)
        {
            const int w = 78, h = 58;
            int x = canvas.Width - w - 3, y = 34;                              // under the info lines (never overlapped by them)
            canvas.FillRect(x, y, w, h, unchecked((int)0xC0101418), SR2D.LineOp.AlphaOver);
            canvas.DrawRect(x, y, w, h, unchecked((int)0x60FFFFFF));
            var pts = new PointF[25];
            for (int i = 0; i < pts.Length; i++)
            {
                float t = i / (pts.Length - 1f);
                float p = Math.Clamp((float)Shape(modeIndex, t), 0f, 1.5f);
                pts[i] = new PointF(x + 4 + t * (w - 8), y + h - 5 - p * (h - 10) / 1.5f);
            }
            canvas.DrawPolyline(pts, unchecked((int)0xFF40C0FF), 1.5f, true, false, SR2D.LineOp.AlphaOver, true);
            float cu = Math.Clamp(u, 0f, 1f), cp = Math.Clamp((float)Shape(modeIndex, cu), 0f, 1.5f);
            canvas.FillCircle(x + 4 + cu * (w - 8), y + h - 5 - cp * (h - 10) / 1.5f, 2.5f, unchecked((int)0xFFFFC040), SR2D.LineOp.AlphaOver);
        }
    }

    /// <summary>The "grid shuffle" state machine (Selection.Move + tangent-shaped motion + pen painting).</summary>
    internal static class MoveDemo
    {
        static Sprite? board;
        static Selection? sel;
        static object? lastAsset;                                              // the Assets.Color reference the board was built from
        static int cell, cols, holeX, holeY, lastMoved = -1;                   // grid positions; lastMoved = block that just moved in
        static Rectangle from;                                                 // the block being animated: its start rect
        static int moveDir;                                                    // and the sign of its move along the axis
        static bool vertical;
        static int stepped;                                                    // px already moved this step
        static float moveT;                                                    // seconds since the move began (the tangent shapes it)
        static bool moving;
        static int blocks;
        static double lastTime = -1;
        static readonly Random rng = new Random(20260927);

        /// <summary>Draws the shuffled board (and advances the animation): a neighbour of the hole moves in, shaped by the selected tangent.</summary>
        public static void Puzzle(Ctx c)
        {
            int want = MoveGrid.CellOf(c.Grid);
            if (board == null || cell != want || !ReferenceEquals(c.A.Color, lastAsset)) { cell = want; Rebuild(c); lastTime = -1; }
            float dt = 0;
            if (c.Time > 0) { if (lastTime >= 0) dt = Math.Clamp((float)(c.Time - lastTime), 0f, 0.1f); lastTime = c.Time; }
            int tangent = Math.Clamp((int)c.Op - 1, 0, 7);                     // the Op box lists the tangents + Curve

            MoveGrid.Paint(c, board!, (c.W - MoveGrid.BoardSize) / 2, (c.H - MoveGrid.BoardSize) / 2);
            if (!moving && dt > 0) StartMove();
            float dur = Math.Max(0.06f, cell / (float)(Math.Max(1, c.Speed) * 8));
            if (moving) Step(dt, tangent, dur);

            var canvas = c.Canvas;
            canvas.ClearBuffer(unchecked((int)0xFF14181E));
            int bx = (c.W - MoveGrid.BoardSize) / 2, by = (c.H - MoveGrid.BoardSize) / 2;
            canvas.Draw(board!, bx, by, SR2D.Op.AlphaOver);                    // the hole shows the dark canvas through it
            MoveGrid.DrawLines(c, canvas, bx, by, cell, cols);
            canvas.DrawRect(bx + holeX * cell + 1, by + holeY * cell + 1, cell - 2, cell - 2, unchecked((int)0xFFFFC040), 2);   // the hole
            if (moving)
            {   // the block currently on its way into the hole
                var cur = Current();
                canvas.DrawRect(bx + cur.X + 1, by + cur.Y + 1, cur.Width - 2, cur.Height - 2, unchecked((int)0xFF40C0FF), 2);
            }
            MoveGrid.TangentGraph(canvas, tangent, moving ? moveT / dur : 0);
            c.Note = $"block {cell}px ({cols}x{cols}, powers of two), hole {holeX},{holeY}, {blocks} blocks moved, tangent {(TangentMode)Math.Clamp(tangent, 0, 6)}{(tangent == 7 ? " (curve)" : "")} - paint with the LEFT button (pen)";
        }

        static void Rebuild(Ctx c)
        {   // fresh Lenna (at the fixed 512 board size, whatever the "Sprite size" combo has), a fresh hole, no animation
            lastAsset = c.A.Color;
            board?.Dispose(); board = new Sprite(MoveGrid.BoardSize, MoveGrid.BoardSize, SR2D.Op.AlphaOver);
            board.DrawScaled(c.A.Color, 0, 0, MoveGrid.BoardSize, MoveGrid.BoardSize, SR2D.Op.Paint, SR2D.Filter.Bilinear);
            sel?.Dispose(); sel = new Selection(MoveGrid.BoardSize, MoveGrid.BoardSize);
            cols = MoveGrid.ColsOf(cell); holeX = rng.Next(cols); holeY = rng.Next(cols);
            sel.Rect(holeX * cell, holeY * cell, cell, cell);
            board.Fill(sel, 0);
            lastMoved = -1; moving = false; blocks = 0;
        }
        static void StartMove()
        {   // pick a random neighbour of the hole - never the block that just moved in (unless it is the only one left)
            Span<(int x, int y)> nb = stackalloc (int, int)[4];
            int n = 0;
            if (holeX > 0) nb[n++] = (holeX - 1, holeY);
            if (holeX < cols - 1) nb[n++] = (holeX + 1, holeY);
            if (holeY > 0) nb[n++] = (holeX, holeY - 1);
            if (holeY < cols - 1) nb[n++] = (holeX, holeY + 1);
            int pick = 0;
            if (n > (lastMoved >= 0 ? 1 : 0))
            {
                do { pick = rng.Next(n); } while (nb[pick].y * cols + nb[pick].x == lastMoved);
            }
            (int sx, int sy) = nb[pick];
            from = new Rectangle(sx * cell, sy * cell, cell, cell);
            vertical = sx == holeX;
            moveDir = vertical ? Math.Sign(holeY * cell - from.Y) : Math.Sign(holeX * cell - from.X);
            stepped = 0; moveT = 0; moving = true;
        }
        static Rectangle Current() => vertical ? new Rectangle(from.X, from.Y + moveDir * stepped, cell, cell)
                                        : new Rectangle(from.X + moveDir * stepped, from.Y, cell, cell);
        static void Step(float dt, int tangent, float dur)
        {   // the tangent shapes the motion: the block travels towards progress(cell) of the eased time, in whole pixels
            moveT += dt;
            float u = Math.Clamp(moveT / dur, 0f, 1f);
            int target = (int)MathF.Round((float)MoveGrid.Shape(tangent, u) * cell);
            int step = target - stepped;
            if (step != 0)                                                     // negative = an overshooting curve moves the block back (like the 3ds Max tangents)
            {
                var cur = Current();
                sel!.Rect(cur);
                board!.Move(sel, vertical ? 0 : moveDir * step, vertical ? moveDir * step : 0);
                stepped += step;
            }
            if (u >= 1f && stepped >= cell)
            {   // the block now sits in the old hole; the hole is where it came from
                lastMoved = holeY * cols + holeX;
                holeX = from.X / cell; holeY = from.Y / cell;
                moving = false; blocks++;
            }
        }
    }

    /// <summary>The three Offset showcases (one test each): Scramble / Selection / Chaos.</summary>
    internal static class OffsetDemo
    {
        static Sprite? board;
        static Selection? sel, toolSel;                                        // sel = the strip mask; toolSel = the user's selection
        static object? lastAsset;
        static int cell, cols;
        static double lastTime = -1;
        static readonly Random rng = new Random(20260928);
        static readonly object gate = new object();

        static Sprite Board(Ctx c)
        {   // the shared 512 board, rebuilt when the grid or the asset changed
            int want = MoveGrid.CellOf(c.Grid);
            if (board == null || cell != want || !ReferenceEquals(c.A.Color, lastAsset))
            {
                cell = want;
                lastAsset = c.A.Color;
                board?.Dispose(); board = new Sprite(MoveGrid.BoardSize, MoveGrid.BoardSize, SR2D.Op.AlphaOver);
                board.DrawScaled(c.A.Color, 0, 0, MoveGrid.BoardSize, MoveGrid.BoardSize, SR2D.Op.Paint, SR2D.Filter.Bilinear);
                sel?.Dispose(); sel = new Selection(MoveGrid.BoardSize, MoveGrid.BoardSize);
                toolSel?.Dispose(); toolSel = null; draggingSelection = false; lasso.Clear();
                cols = MoveGrid.ColsOf(cell);
                ResetStrips();
            }
            return board!;
        }

        // ---- scenario: grid scramble -------------------------------------------------------
        static Rectangle strip;
        static bool vertical;
        static int dir, stripTarget, stripDone, lastAxis = -1, lastIndex = -1;
        static float pause, moveT;

        /// <summary>Random strips shift along themselves (tangent-shaped, never the previous strip, snapped to the grid, capped by the picture).</summary>
        public static void Scramble(Ctx c)
        {
            var b = Board(c);
            float dt = Delta(c);
            int tangent = Math.Clamp((int)c.Op - 1, 0, 7);
            int bx = (c.W - MoveGrid.BoardSize) / 2, by = (c.H - MoveGrid.BoardSize) / 2;
            MoveGrid.Paint(c, b, bx, by);
            if (pause > 0) pause -= dt;
            else if (stripTarget == 0 && dt > 0) Pick();
            if (stripTarget > 0 && dt > 0)
            {
                moveT += dt;
                float dur = Math.Max(0.08f, stripTarget / (float)(Math.Max(1, c.Speed) * 8));
                float u = Math.Clamp(moveT / dur, 0f, 1f);
                int want = (int)MathF.Round((float)MoveGrid.Shape(tangent, u) * stripTarget);
                int step = want - stripDone;                                   // signed: an overshooting curve shifts back (the wrap keeps it sane)
                if (step != 0)
                {
                    sel!.Rect(strip);
                    b.Offset(sel, vertical ? 0 : dir * step, vertical ? dir * step : 0, SelOffsetWrap.BoundingBox);
                    stripDone += step;
                }
                if (u >= 1f) { stripTarget = 0; stripDone = 0; moveT = 0; pause = 0.12f + (float)rng.NextDouble() * 0.35f; }   // signed steps: the end condition is time, not the counter
            }
            var canvas = c.Canvas;
            canvas.ClearBuffer(unchecked((int)0xFF14181E));
            canvas.Draw(b, bx, by, SR2D.Op.AlphaOver);
            MoveGrid.DrawLines(c, canvas, bx, by, cell, cols);
            if (stripTarget > 0) canvas.DrawRect(bx + strip.X + 1, by + strip.Y + 1, strip.Width - 2, strip.Height - 2, unchecked((int)0xFF40E0FF), 2);
            MoveGrid.TangentGraph(canvas, tangent, stripTarget > 0 ? moveT / Math.Max(0.08f, stripTarget / (float)(Math.Max(1, c.Speed) * 8)) : 0);
            c.Note = $"strip {strip.Width}x{strip.Height} at {strip.X},{strip.Y} shifted {stripDone}/{stripTarget}px ({(vertical ? "up-down" : "left-right")}), cell {cell}px - next strip never repeats the previous";
        }
        static void Pick()
        {   // a random full strip, never the previous one; the shift is snapped to the grid but capped by the picture size
            int axis = -1, index = -1;
            do
            {
                axis = rng.Next(2);
                index = rng.Next(cols);
            }
            while (cols > 1 && axis == lastAxis && index == lastIndex);
            lastAxis = axis; lastIndex = index;
            vertical = axis == 0;
            strip = vertical ? new Rectangle(index * cell, 0, cell, MoveGrid.BoardSize) : new Rectangle(0, index * cell, MoveGrid.BoardSize, cell);
            dir = rng.Next(2) == 0 ? 1 : -1;
            stripTarget = cell * (1 + rng.Next(Math.Max(1, cols - 1)));
            stripDone = 0; moveT = 0;
        }
        static void ResetStrips() { strip = new Rectangle(0, 0, MoveGrid.BoardSize, cell); vertical = false; dir = 1; stripTarget = stripDone = 0; lastAxis = -1; lastIndex = -1; pause = 0.4f; moveT = 0; }

        // ---- scenario: selection tool ------------------------------------------------------
        static readonly List<PointF> lasso = new();
        static Point anchor, dragCur;
        static bool draggingSelection;
        static int appliedX, appliedY;                                         // the last offset the sliders were applied to
        static int tool;                                                       // 0 rect, 1 ellipse, 2 lasso, 3 pen

        /// <summary>The selection tool: the shape buttons select, the pen button paints, the sliders offset the selection.</summary>
        public static void Selection(Ctx c)
        {
            var b = Board(c);
            Delta(c);
            int bx = (c.W - MoveGrid.BoardSize) / 2, by = (c.H - MoveGrid.BoardSize) / 2;
            if (tool == 3) MoveGrid.Paint(c, b, bx, by);                       // only the pen paints - the shape tools just select

            if (tool != 3 && c.PenDown && !draggingSelection)
            {
                draggingSelection = true;
                anchor = dragCur = new Point(Math.Clamp(c.PenX - bx, 0, MoveGrid.BoardSize - 1), Math.Clamp(c.PenY - by, 0, MoveGrid.BoardSize - 1));
                lasso.Clear(); lasso.Add(anchor);
            }
            else if (tool != 3 && c.PenDown && draggingSelection)
            {
                dragCur = new Point(Math.Clamp(c.PenX - bx, 0, MoveGrid.BoardSize - 1), Math.Clamp(c.PenY - by, 0, MoveGrid.BoardSize - 1));
                if (lasso.Count == 0 || Math.Abs(dragCur.X - lasso[^1].X) + Math.Abs(dragCur.Y - lasso[^1].Y) >= 2) lasso.Add(dragCur);
            }
            else if (!c.PenDown && draggingSelection)
            {   // commit: the new shape REPLACES the selection (like a fresh marquee in Photoshop)
                draggingSelection = false;
                toolSel ??= new Selection(MoveGrid.BoardSize, MoveGrid.BoardSize);
                int x0 = Math.Min(anchor.X, dragCur.X), y0 = Math.Min(anchor.Y, dragCur.Y);
                int w = Math.Abs(dragCur.X - anchor.X), h = Math.Abs(dragCur.Y - anchor.Y);
                if (tool == 2 && lasso.Count >= 3) toolSel.Polygon(lasso.ToArray(), SelectMode.Replace, AA: true);
                else if (tool == 1 && w > 1 && h > 1) toolSel.Ellipse(x0 + w / 2f, y0 + h / 2f, w / 2f, h / 2f, SelectMode.Replace, AA: true);
                else if (w > 0 && h > 0) toolSel.Rect(x0, y0, w, h);
                appliedX = c.OffsetX; appliedY = c.OffsetY;                    // the sliders apply from where the selection is now
            }

            if (toolSel is { IsEmpty: false } && (c.OffsetX != appliedX || c.OffsetY != appliedY))
            {
                var wrap = c.NotMask ? SelOffsetWrap.Spans : SelOffsetWrap.BoundingBox;
                b.Offset(toolSel, c.OffsetX - appliedX, c.OffsetY - appliedY, wrap);
                appliedX = c.OffsetX; appliedY = c.OffsetY;
            }

            var canvas = c.Canvas;
            canvas.ClearBuffer(unchecked((int)0xFF14181E));
            canvas.Draw(b, bx, by, SR2D.Op.AlphaOver);
            toolSel?.Draw(canvas, c.Time * 24, Tint: 0x20FFFFFF, Ants: true, OffsetX: bx, OffsetY: by);   // faint tint + marching ants (on the CLEARED canvas - no accumulation)
            DrawSelectionPreview(canvas, bx, by);
            c.Note = $"selection tool: {(tool == 3 ? "PEN - paint on the board" : $"draw a {ToolName()} with the LEFT button")}, push it with the Offset X / Y sliders ({c.OffsetX},{c.OffsetY}), wrap = {(c.NotMask ? "rows / cols (spans)" : "bounding box")}";
        }
        static string ToolName() => tool switch { 1 => "ellipse", 2 => "lasso", _ => "rectangle" };
        static void DrawSelectionPreview(Sprite canvas, int bx, int by)
        {
            if (!draggingSelection) return;
            int x0 = bx + Math.Min(anchor.X, dragCur.X), y0 = by + Math.Min(anchor.Y, dragCur.Y);
            int w = Math.Abs(dragCur.X - anchor.X), h = Math.Abs(dragCur.Y - anchor.Y);
            if (tool == 2 && lasso.Count >= 2)
            {
                var pts = new PointF[lasso.Count];
                for (int i = 0; i < lasso.Count; i++) pts[i] = new PointF(bx + lasso[i].X, by + lasso[i].Y);
                canvas.DrawPolyline(pts, unchecked((int)0xFFFFFF80), 1.5f, true, false, SR2D.LineOp.AlphaOver, true);
            }
            else if (tool == 1 && w > 0 && h > 0) canvas.DrawEllipse(x0 + w / 2f, y0 + h / 2f, w / 2f, h / 2f, unchecked((int)0xFFFFFF80), 1.5f, true);
            else if (w > 0 && h > 0) canvas.DrawRect(x0, y0, w, h, unchecked((int)0xFFFFFF80), 1.5f);
        }

        // ---- scenario: chaos (continuous full scramble) -------------------------------------
        const int Lines = MoveGrid.BoardSize;
        static readonly int[] target = new int[Lines], done2 = new int[Lines];
        static readonly float[] moveT2 = new float[Lines];
        static readonly bool[] verticalLine = new bool[Lines];
        static readonly int[] dirLine = new int[Lines];
        static int cursor;                                                     // the round-robin window start
        // a pre-generated random pool: the parallel body must not touch the shared Random
        static readonly bool[] poolVert = new bool[4096];
        static readonly int[] poolDir = new int[4096], poolTarget = new int[4096];
        static int poolCursor = -1;
        static float durPerLine;                                               // seconds per 512 px at the current Speed

        static void FillPool()
        {
            lock (gate)
            {
                for (int i = 0; i < poolVert.Length; i++)
                {
                    poolVert[i] = rng.Next(2) == 0;
                    poolDir[i] = rng.Next(2) == 0 ? 1 : -1;
                    poolTarget[i] = 64 + rng.Next(Lines - 64);                 // enough travel to be visible, never a degenerate no-op
                }
                poolCursor = -1;
            }
        }
        static int NextPool()
        {
            int i = Interlocked.Increment(ref poolCursor);
            return i & (poolVert.Length - 1);
        }
        static void ResetLine(int i)
        {
            int p = NextPool();
            verticalLine[i] = poolVert[p];
            dirLine[i] = poolDir[p];
            target[i] = poolTarget[p];
            done2[i] = 0; moveT2[i] = 0;
        }
        static void ResetStrips2()
        {
            for (int i = 0; i < Lines; i++) ResetLine(i);
            cursor = 0;
        }

        /// <summary>Continuous full scramble: every single-pixel line (rows AND columns) rotates by its own random amount, endlessly.</summary>
        public static unsafe void Chaos(Ctx c)
        {
            var b = Board(c);
            float dt = Delta(c);
            int tangent = Math.Clamp((int)c.Op - 1, 0, 7);
            int workers = Math.Clamp(c.Count, 1, 64);
            if (poolCursor < 0) FillPool();
            if (done2[0] == 0 && target[0] == 0) ResetStrips2();
            int step = (int)MathF.Round(Math.Max(1, c.Speed) * 8f * Math.Max(dt, 0f));
            if (step > 0)
            {
                durPerLine = Math.Max(0.08f, Lines / (float)(Math.Max(1, c.Speed) * 8));
                int* p = b.PixelPtr;
                int stride = MoveGrid.BoardSize;
                int cur = cursor; cursor = (cursor + workers) % Lines;         // this frame's window: `workers` distinct lines, in parallel
                float dtl = dt;
                int tan = tangent, stp = step;
                Parallel.For(0, workers, k =>
                {
                    int i = (cur + k) % Lines;
                    moveT2[i] += dtl;
                    float u = Math.Clamp(moveT2[i] / durPerLine, 0f, 1f);
                    int want = (int)MathF.Round((float)MoveGrid.Shape(tan, u) * target[i]);
                    int s = Math.Clamp(want - done2[i], 0, target[i] - done2[i]);
                    if (s <= 0) return;
                    if (verticalLine[i]) RotateCol(p, stride, i, dirLine[i] * s);
                    else RotateRow(p, stride, i, dirLine[i] * s);
                    done2[i] += s;
                    if (done2[i] >= target[i]) ResetLine(i);                   // continuous: a finished line starts anew at once
                });
            }
            int moving = 0;
            for (int i = 0; i < Lines; i++) if (done2[i] < target[i]) moving++;
            var canvas = c.Canvas;
            canvas.ClearBuffer(unchecked((int)0xFF14181E));
            int bx = (c.W - MoveGrid.BoardSize) / 2, by = (c.H - MoveGrid.BoardSize) / 2;
            canvas.Draw(b, bx, by, SR2D.Op.AlphaOver);
            MoveGrid.TangentGraph(canvas, tangent, durPerLine > 0 ? Math.Clamp(moveT2[cursor] / durPerLine, 0f, 1f) : 0);
            c.Note = $"full scramble: {Lines} single-px lines (rows + columns, random) rotate endlessly, {workers} at a time IN PARALLEL, {moving} in flight, tangent {(TangentMode)Math.Clamp(tangent, 0, 6)}{(tangent == 7 ? " (curve)" : "")}";
        }

        static unsafe void Reverse(int* row, int i, int j)
        {
            while (i < j) { (row[i], row[j]) = (row[j], row[i]); i++; j--; }
        }
        static unsafe void RotateRow(int* basePtr, int stride, int y, int s)
        {
            int len = MoveGrid.BoardSize;
            s %= len; if (s < 0) s += len;
            if (s == 0) return;
            int* row = basePtr + (long)y * stride;
            Reverse(row, 0, s - 1);
            Reverse(row, s, len - 1);
            Reverse(row, 0, len - 1);
        }
        static unsafe void RotateCol(int* basePtr, int stride, int x, int s)
        {
            int len = MoveGrid.BoardSize;
            s %= len; if (s < 0) s += len;
            if (s == 0) return;
            for (int pass = 0; pass < 3; pass++)
            {
                int i = pass == 0 ? 0 : pass == 1 ? s : 0;
                int j = pass == 0 ? s - 1 : pass == 1 ? len - 1 : len - 1;
                while (i < j)
                {
                    int* a = basePtr + (long)i * stride + x, b2 = basePtr + (long)j * stride + x;
                    (*a, *b2) = (*b2, *a);
                    i++; j--;
                }
            }
        }

        // ---- shared -----------------------------------------------------------------------
        static float Delta(Ctx c)
        {
            float dt = 0;
            if (c.Time > 0) { if (lastTime >= 0) dt = Math.Clamp((float)(c.Time - lastTime), 0f, 0.1f); lastTime = c.Time; }
            return dt;
        }
        public static void SetTool(int t) { tool = t; OffsetTools.SyncAccent(t); }
        /// <summary>The "Reset picture" button: throws the board away - the next frame rebuilds a fresh Lenna (no paint, no offsets, no selection).</summary>
        public static void ResetBoard()
        {
            lock (gate)
            {
                board?.Dispose(); board = null; lastAsset = null;
                ClearSelection();
            }
        }
        public static void ClearSelection()
        {
            toolSel?.Clear();
            toolSel?.Dispose(); toolSel = null;
        }
    }
}
