using System;
using System.Drawing;
using System.Windows.Forms;

namespace Sr2d64CSport
{
    /// <summary>
    /// Editor-style transform frame for the demo (tests with <c>Frame = true</c>): the frame IS the
    /// object's <see cref="SpriteTransform"/>, edited with the mouse like in an image editor:
    ///   * drag inside              -> move (X / Y)
    ///   * drag a corner handle     -> scale about the opposite corner (Shift = keep aspect)
    ///   * drag an edge midpoint    -> stretch that axis only
    ///   * drag the ring outside    -> rotate about the pivot (Shift = 15° steps)
    ///   * Ctrl + drag a corner     -> perspective: that corner alone moves (switches to a Quad)
    ///   * right click              -> reset the transform
    /// The test reads <see cref="Ctx.Frame"/> (this object's <see cref="Transform"/>) and draws with it;
    /// the demo draws the handles after the frame. All maths is in source space of a
    /// <see cref="Width"/> x <see cref="Height"/> picture whose top-left is drawn at
    /// (<see cref="OriginX"/>, <see cref="OriginY"/>) on the canvas.
    /// </summary>
    internal sealed class TransformFrame
    {
        public readonly SpriteTransform Transform = new SpriteTransform();
        public int Width, Height;                 // of the picture the transform applies to
        public float OriginX, OriginY;            // canvas position the picture's (0,0) is drawn at
        public int Version => Transform.Version;

        public enum Part { None, Inside, Corner, Edge, Rotate }
        public Part Hover { get; private set; }
        public int HoverIndex { get; private set; }   // corner 0..3 (TL, TR, BR, BL) / edge 0..3 (top, right, bottom, left)
        public bool Dragging => drag != Part.None;

        Part drag; int dragIndex; bool dragPerspective;
        PointF dragStart; float startX, startY, startSX, startSY, startAngle; PointF[]? startQuad; PointF startAnchorSrc; double rotStart, rotTurned;

        /// <summary>Attach to a picture size + draw origin (keeps the transform).</summary>
        public void Attach(int width, int height, float originX, float originY) { Width = width; Height = height; OriginX = originX; OriginY = originY; }
        public void Reset() { Transform.Reset(); drag = Part.None; }

        // ---- geometry helpers (canvas space)
        public void Corners(Span<PointF> q) { Transform.Corners(Width, Height, q, OriginX, OriginY); }
        public PointF Corner(int i) { Span<PointF> q = stackalloc PointF[4]; Corners(q); return q[i]; }
        public PointF EdgeMid(int i) { Span<PointF> q = stackalloc PointF[4]; Corners(q); var a = q[i]; var b = q[(i + 1) & 3]; return new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2); }
        public PointF PivotOnCanvas()
        {
            float px = Transform.PivotX < 0 ? Width * 0.5f : Transform.PivotX, py = Transform.PivotY < 0 ? Height * 0.5f : Transform.PivotY;
            var m = Transform.Map(new PointF(px, py), Width, Height); return new PointF(m.X + OriginX, m.Y + OriginY);
        }
        /// <summary>Canvas point -> source pixel through the inverse transform (NaN when singular).</summary>
        public PointF ToSource(float cx, float cy) => Transform.Unmap(new PointF(cx - OriginX, cy - OriginY), Width, Height);
        static float Dist(PointF a, float x, float y) { float dx = a.X - x, dy = a.Y - y; return MathF.Sqrt(dx * dx + dy * dy); }

        /// <summary>What is under the pointer (handles win over the interior, the interior over the rotate ring).</summary>
        public Part HitTest(float x, float y, out int index)
        {
            index = -1;
            if (Width <= 0 || Height <= 0) return Part.None;
            for (int i = 0; i < 4; i++) if (Dist(Corner(i), x, y) <= 7f) { index = i; return Part.Corner; }
            for (int i = 0; i < 4; i++) if (Dist(EdgeMid(i), x, y) <= 7f) { index = i; return Part.Edge; }
            var s = ToSource(x, y);
            if (!float.IsNaN(s.X) && s.X >= 0 && s.Y >= 0 && s.X < Width && s.Y < Height) return Part.Inside;
            // rotate ring: within ~28 canvas px outside the frame (distance to the nearest edge segment)
            Span<PointF> q = stackalloc PointF[4]; Corners(q); float best = float.MaxValue;
            for (int i = 0; i < 4; i++) best = MathF.Min(best, SegDist(q[i], q[(i + 1) & 3], x, y));
            return best <= 28f ? Part.Rotate : Part.None;
        }
        static float SegDist(PointF a, PointF b, float x, float y)
        {
            float vx = b.X - a.X, vy = b.Y - a.Y, l2 = vx * vx + vy * vy; if (l2 < 1e-6f) return Dist(a, x, y);
            float t = Math.Clamp(((x - a.X) * vx + (y - a.Y) * vy) / l2, 0f, 1f);
            return Dist(new PointF(a.X + t * vx, a.Y + t * vy), x, y);
        }
        public void UpdateHover(float x, float y) { if (drag != Part.None) return; Hover = HitTest(x, y, out int i); HoverIndex = i; }
        public Cursor CursorFor(Part p, int index)
        {
            switch (p)
            {
                case Part.Inside: return drag == Part.Inside ? SpriteCursors.HandGrab : SpriteCursors.HandOpen;
                case Part.Rotate: return SpriteCursors.Rotate;
                case Part.Corner: case Part.Edge:
                {   // direction of the handle from the pivot on screen -> a resize arrow
                    var h = p == Part.Corner ? Corner(index) : EdgeMid(index); var c = PivotOnCanvas();
                    int oct = (int)MathF.Round(MathF.Atan2(h.Y - c.Y, h.X - c.X) / (MathF.PI / 4)) & 3;
                    return oct switch { 0 => Cursors.SizeWE, 1 => Cursors.SizeNWSE, 2 => Cursors.SizeNS, _ => Cursors.SizeNESW };
                }
                default: return Cursors.Default;
            }
        }

        // ---- mouse
        /// <summary>Left button down. Returns true when the frame took the press.</summary>
        public bool MouseDown(float x, float y, Keys modifiers)
        {
            var p = HitTest(x, y, out int i); if (p == Part.None) return false;
            drag = p; dragIndex = i; dragStart = new PointF(x, y); dragPerspective = p == Part.Corner && (modifiers & Keys.Control) != 0;
            startX = Transform.X; startY = Transform.Y; startSX = Transform.ScaleX; startSY = Transform.ScaleY; startAngle = Transform.Angle; startQuad = Transform.Quad;
            if (p == Part.Rotate) { var c = PivotOnCanvas(); rotStart = Math.Atan2(y - c.Y, x - c.X); rotTurned = 0; }
            if ((p == Part.Corner || p == Part.Edge) && !dragPerspective && Transform.Quad != null) { Transform.Quad = null; startQuad = null; startSX = Transform.ScaleX; startSY = Transform.ScaleY; }   // scaling a perspective frame: back to affine
            if (p == Part.Corner && !dragPerspective) startAnchorSrc = new PointF(((i == 0 || i == 3) ? Width : 0), (i < 2 ? Height : 0));   // opposite corner (source px) stays put
            if (p == Part.Edge && !dragPerspective) startAnchorSrc = i switch { 0 => new PointF(Width * 0.5f, Height), 1 => new PointF(0, Height * 0.5f), 2 => new PointF(Width * 0.5f, 0), _ => new PointF(Width, Height * 0.5f) };
            Hover = p; HoverIndex = i;
            return true;
        }
        public void MouseMove(float x, float y, Keys modifiers)
        {
            if (drag == Part.None) { UpdateHover(x, y); return; }
            float dx = x - dragStart.X, dy = y - dragStart.Y;
            switch (drag)
            {
                case Part.Inside:
                    Transform.X = startX + dx; Transform.Y = startY + dy; break;
                case Part.Rotate:
                {
                    var c = PivotOnCanvas(); double a = Math.Atan2(y - c.Y, x - c.X), d = a - rotStart;
                    while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
                    rotTurned += d; rotStart = a;
                    double v = startAngle + rotTurned;
                    if ((modifiers & Keys.Shift) != 0) v = Math.Round(v / (Math.PI / 12)) * (Math.PI / 12);
                    Transform.Angle = (float)v; break;
                }
                case Part.Corner when dragPerspective:
                {   // move one corner alone: the other three stay where they are on screen
                    Span<PointF> q = stackalloc PointF[4];
                    if (startQuad != null) for (int k = 0; k < 4; k++) q[k] = startQuad[k];
                    else { var t = new SpriteTransform().CopyFrom(Transform); t.Quad = null; t.X = startX; t.Y = startY; t.ScaleX = startSX; t.ScaleY = startSY; t.Angle = startAngle; t.Corners(Width, Height, q); }
                    q[dragIndex] = new PointF(q[dragIndex].X + dx, q[dragIndex].Y + dy);
                    Transform.Quad = q.ToArray(); break;
                }
                case Part.Corner:
                case Part.Edge:
                {   // scale about the anchor: the anchor's canvas position must not move
                    var anchorBefore = MapWith(startAnchorSrc, startX, startY, startSX, startSY);
                    // handle position in the unrotated, unscaled frame -> new scale = (mouse offset from anchor, unrotated) / (handle - anchor)
                    float c = MathF.Cos(startAngle), sn = MathF.Sin(startAngle);
                    float mx = x - anchorBefore.X, my = y - anchorBefore.Y;
                    float ux = mx * c + my * sn, uy = -mx * sn + my * c;                  // unrotated offset from the anchor, canvas px
                    PointF hSrc = drag == Part.Corner ? new PointF(dragIndex == 1 || dragIndex == 2 ? Width : 0, dragIndex >= 2 ? Height : 0)
                                                     : dragIndex switch { 0 => new PointF(Width * 0.5f, 0), 1 => new PointF(Width, Height * 0.5f), 2 => new PointF(Width * 0.5f, Height), _ => new PointF(0, Height * 0.5f) };
                    float hx = hSrc.X - startAnchorSrc.X, hy = hSrc.Y - startAnchorSrc.Y;  // handle - anchor in source px
                    float sx = startSX, sy = startSY;
                    if (MathF.Abs(hx) > 1e-3f) sx = ux / hx; if (MathF.Abs(hy) > 1e-3f) sy = uy / hy;
                    if (drag == Part.Corner && (modifiers & Keys.Shift) != 0) { float k = MathF.Max(MathF.Abs(sx / startSX), MathF.Abs(sy / startSY)); sx = MathF.Sign(sx) * MathF.Abs(startSX) * k; sy = MathF.Sign(sy) * MathF.Abs(startSY) * k; }
                    if (MathF.Abs(sx) < 0.02f) sx = MathF.Sign(sx == 0 ? 1 : sx) * 0.02f; if (MathF.Abs(sy) < 0.02f) sy = MathF.Sign(sy == 0 ? 1 : sy) * 0.02f;
                    Transform.ScaleX = sx; Transform.ScaleY = sy;
                    // keep the anchor in place: shift X / Y by the anchor's displacement
                    var anchorAfter = MapWith(startAnchorSrc, startX, startY, sx, sy);
                    Transform.X = startX + (anchorBefore.X - anchorAfter.X); Transform.Y = startY + (anchorBefore.Y - anchorAfter.Y);
                    break;
                }
            }
        }
        public void MouseUp() { drag = Part.None; }
        // where a source point lands with the given X / Y / scale and the start angle (canvas space)
        PointF MapWith(PointF src, float tx, float ty, float sx, float sy)
        {
            var t = new SpriteTransform { X = tx, Y = ty, ScaleX = sx, ScaleY = sy, Angle = startAngle, PivotX = Transform.PivotX, PivotY = Transform.PivotY, Matrix = Transform.Matrix };
            var m = t.Map(src, Width, Height); return new PointF(m.X + OriginX, m.Y + OriginY);
        }

        // ---- drawing (into the canvas, after the test's frame)
        public void Draw(Sprite canvas)
        {
            if (Width <= 0 || Height <= 0) return;
            Span<PointF> q = stackalloc PointF[4]; Corners(q);
            int line = drag != Part.None ? unchecked((int)0xFFFFD040) : unchecked((int)0xB0FFFFFF), dark = unchecked((int)0x80000000), white = unchecked((int)0xFFFFFFFF), hot = unchecked((int)0xFFFFD040);
            for (int i = 0; i < 4; i++) { var a = q[i]; var b = q[(i + 1) & 3]; canvas.DrawWideLine(a.X, a.Y, b.X, b.Y, dark, 3f, true, SR2D.LineOp.AlphaBlend); canvas.DrawWideLine(a.X, a.Y, b.X, b.Y, line, 1f, true, SR2D.LineOp.AlphaBlend); }
            for (int i = 0; i < 4; i++)
            {
                var c = q[i]; bool h = (Hover == Part.Corner && HoverIndex == i);
                canvas.FillRect(c.X - 5, c.Y - 5, 10, 10, dark, SR2D.LineOp.AlphaBlend, true); canvas.FillRect(c.X - 4, c.Y - 4, 8, 8, h ? hot : white, SR2D.LineOp.Set, true);
                var m = EdgeMid(i); bool he = (Hover == Part.Edge && HoverIndex == i);
                canvas.FillCircle(m.X, m.Y, 5f, dark, SR2D.LineOp.AlphaBlend, true); canvas.FillCircle(m.X, m.Y, 4f, he ? hot : white, SR2D.LineOp.Set, true);
            }
            var p = PivotOnCanvas();
            canvas.DrawCircle(p.X, p.Y, 6f, dark, 3f, true, SR2D.LineOp.AlphaBlend); canvas.DrawCircle(p.X, p.Y, 6f, Hover == Part.Rotate ? hot : white, 1f, true, SR2D.LineOp.AlphaBlend);
            canvas.DrawLine((int)p.X - 9, (int)p.Y, (int)p.X + 9, (int)p.Y, white, 1); canvas.DrawLine((int)p.X, (int)p.Y - 9, (int)p.X, (int)p.Y + 9, white, 1);
        }
        public string Describe()
        {
            var T = Transform;
            if (T.Quad != null) return "perspective quad (Ctrl+corner moved it; drag a handle without Ctrl to go back to affine)";
            return $"X {T.X:0.#} Y {T.Y:0.#}  scale {T.ScaleX:0.###} x {T.ScaleY:0.###}  angle {T.AngleDegrees:0.#}°{(T.Matrix.IsIdentity ? "" : "  + matrix")}";
        }
    }
}
