using System;
using System.Drawing;
using System.Numerics;

namespace Sr2d64CSport
{
    /// <summary>
    /// A general 2-D transform for drawing a picture: scale and rotation about a pivot, an offset,
    /// an arbitrary affine <see cref="Matrix"/> (skew, mirror, anything Matrix3x2 holds) and,
    /// optionally, a perspective <see cref="Quad"/>. Plus the <see cref="Filter"/> and
    /// <see cref="Opacity"/> to draw with. Used by <see cref="LayeredSprite.Transform"/> (the whole
    /// stack is drawn through it) and by <see cref="Sprite.DrawTransformed"/> for single sprites.
    ///
    /// Order (source pixel space, origin = the picture's top-left):
    ///   1. move the pivot to the origin (PivotX / PivotY, -1 = the picture centre)
    ///   2. ScaleX / ScaleY, then rotate by Angle (radians, clockwise on screen like DrawRotate2)
    ///   3. Matrix (affine, default identity) - still pivot-relative, so a mirror / skew acts in place
    ///   4. move the pivot back and add X / Y
    /// When <see cref="Quad"/> is set (4 points = where the picture's TL, TR, BR, BL corners go,
    /// in source space) it REPLACES steps 1-4: a perspective warp. The draw position (x, y) of the
    /// draw call is added last, so a stack drawn at (x, y) with Angle = 0.3 turns about its own
    /// pivot placed at (x + pivot).
    ///
    /// <see cref="Version"/> is bumped by every setter; a LayeredSprite does NOT recompose when
    /// its transform changes (the composite is transform-independent) - changing the angle every
    /// frame costs one warp, nothing else.
    /// </summary>
    public sealed class SpriteTransform
    {
        float x, y, scaleX = 1f, scaleY = 1f, angle, pivotX = -1f, pivotY = -1f, opacity = 1f;
        Matrix3x2 matrix = Matrix3x2.Identity;
        PointF[]? quad;
        SR2D.Filter filter = SR2D.Filter.Auto;

        /// <summary>Change counter (every setter).</summary>
        public int Version { get; private set; }
        void Bump() => Version++;

        /// <summary>Offset in destination pixels (added after scale / rotation).</summary>
        public float X { get => x; set { if (x != value) { x = value; Bump(); } } }
        public float Y { get => y; set { if (y != value) { y = value; Bump(); } } }
        /// <summary>Uniform scale (sets both). Negative values mirror.</summary>
        public float Scale { get => scaleX; set { if (scaleX != value || scaleY != value) { scaleX = scaleY = value; Bump(); } } }
        public float ScaleX { get => scaleX; set { if (scaleX != value) { scaleX = value; Bump(); } } }
        public float ScaleY { get => scaleY; set { if (scaleY != value) { scaleY = value; Bump(); } } }
        /// <summary>Rotation in radians, clockwise on screen (same sense as <see cref="Sprite.DrawRotate2"/> and <see cref="LayeredSprite.Layer.Angle"/>).</summary>
        public float Angle { get => angle; set { if (angle != value) { angle = value; Bump(); } } }
        /// <summary>The same rotation in degrees.</summary>
        public float AngleDegrees { get => angle * 180f / MathF.PI; set => Angle = value * MathF.PI / 180f; }
        /// <summary>Pivot of scale / rotation in source pixels; -1 (default) = the centre.</summary>
        public float PivotX { get => pivotX; set { if (pivotX != value) { pivotX = value; Bump(); } } }
        public float PivotY { get => pivotY; set { if (pivotY != value) { pivotY = value; Bump(); } } }
        /// <summary>Extra affine matrix applied after scale / rotation, in pivot-relative coordinates (skew: Matrix3x2.CreateSkew, mirror: CreateScale(-1, 1), ...).</summary>
        public Matrix3x2 Matrix { get => matrix; set { if (matrix != value) { matrix = value; Bump(); } } }
        /// <summary>
        /// Perspective: destination of the picture's TL, TR, BR, BL corners in source space (4 points), or
        /// null. When set, X / Y / Scale / Angle / Pivot / Matrix are ignored. The array is copied.
        /// </summary>
        public PointF[]? Quad
        {
            get => quad == null ? null : (PointF[])quad.Clone();
            set { if (value != null && value.Length != 4) throw new ArgumentException("Quad needs exactly 4 points", nameof(value)); quad = value == null ? null : (PointF[])value.Clone(); Bump(); }
        }
        /// <summary>Resampling filter for the warp (Auto = bicubic up / area down / nearest at 1:1).</summary>
        public SR2D.Filter Filter { get => filter; set { if (filter != value) { filter = value; Bump(); } } }
        /// <summary>0..1 multiplies the alpha at draw time (source untouched).</summary>
        public float Opacity { get => opacity; set { value = Math.Clamp(value, 0f, 1f); if (opacity != value) { opacity = value; Bump(); } } }

        /// <summary>True when drawing through this transform is the plain draw (no geometry change).</summary>
        public bool IsIdentity => quad == null && x == 0 && y == 0 && scaleX == 1 && scaleY == 1 && angle == 0 && matrix.IsIdentity;
        /// <summary>Identity geometry AND full opacity: the plain draw path can be used unchanged.</summary>
        public bool IsPlain => IsIdentity && opacity >= 1f;
        /// <summary>True when the geometry is affine (no perspective quad): <see cref="Unmap"/> is exact.</summary>
        public bool IsAffine => quad == null;

        /// <summary>Back to the identity (Opacity 1, Filter Auto).</summary>
        public SpriteTransform Reset()
        {
            x = y = angle = 0; scaleX = scaleY = 1; pivotX = pivotY = -1; opacity = 1; matrix = Matrix3x2.Identity; quad = null; filter = SR2D.Filter.Auto; Bump(); return this;
        }
        /// <summary>Copies every parameter from another transform.</summary>
        public SpriteTransform CopyFrom(SpriteTransform o)
        {
            x = o.x; y = o.y; scaleX = o.scaleX; scaleY = o.scaleY; angle = o.angle; pivotX = o.pivotX; pivotY = o.pivotY; opacity = o.opacity; matrix = o.matrix; quad = o.quad == null ? null : (PointF[])o.quad.Clone(); filter = o.filter; Bump(); return this;
        }
        public SpriteTransform Clone() => new SpriteTransform().CopyFrom(this);
        /// <summary>Fluent setters.</summary>
        public SpriteTransform Rotate(float Radians) { Angle = Radians; return this; }
        public SpriteTransform RotateDegrees(float Degrees) { AngleDegrees = Degrees; return this; }
        public SpriteTransform Scaled(float Factor) { Scale = Factor; return this; }
        public SpriteTransform Scaled(float FactorX, float FactorY) { ScaleX = FactorX; ScaleY = FactorY; return this; }
        public SpriteTransform Offset(float dx, float dy) { X = dx; Y = dy; return this; }
        public SpriteTransform Pivot(float px, float py) { PivotX = px; PivotY = py; return this; }
        public SpriteTransform Skew(float RadiansX, float RadiansY) { Matrix = Matrix3x2.CreateSkew(RadiansX, RadiansY); return this; }
        public SpriteTransform Mirror(bool Horizontal, bool Vertical = false) { Matrix = Matrix3x2.CreateScale(Horizontal ? -1f : 1f, Vertical ? -1f : 1f); return this; }
        public SpriteTransform Perspective(PointF tl, PointF tr, PointF br, PointF bl) { Quad = new[] { tl, tr, br, bl }; return this; }

        // ------------------------------------------------------------------ geometry
        /// <summary>The affine part as one matrix for a picture of the given size (source px -> transformed source px). Ignores <see cref="Quad"/>.</summary>
        public Matrix3x2 ToMatrix(int width, int height)
        {
            float px = pivotX < 0 ? width * 0.5f : pivotX, py = pivotY < 0 ? height * 0.5f : pivotY;
            var m = Matrix3x2.CreateTranslation(-px, -py) * Matrix3x2.CreateScale(scaleX, scaleY);
            if (angle != 0) m *= Matrix3x2.CreateRotation(angle);
            if (!matrix.IsIdentity) m *= matrix;
            m *= Matrix3x2.CreateTranslation(px + x, py + y);
            return m;
        }
        /// <summary>
        /// Where the four corners (TL, TR, BR, BL) of a width x height picture land, in source space
        /// (add the draw position for destination coordinates). The rotation arithmetic is the one
        /// <see cref="Sprite.DrawRotate2"/> uses, so a pure scale + rotate transform draws bit-identically to it.
        /// </summary>
        public void Corners(int width, int height, Span<PointF> q) => Corners(width, height, q, 0f, 0f);
        /// <summary>Same with the draw position folded in before the rotation (bit-identical to DrawRotate2's arithmetic).</summary>
        public void Corners(int width, int height, Span<PointF> q, float drawX, float drawY)
        {
            if (q.Length < 4) throw new ArgumentException("need room for 4 points", nameof(q));
            if (quad != null) { for (int i = 0; i < 4; i++) q[i] = new PointF(quad[i].X + drawX, quad[i].Y + drawY); return; }
            float px = pivotX < 0 ? width * 0.5f : pivotX, py = pivotY < 0 ? height * 0.5f : pivotY;
            float dw = width * scaleX, dh = height * scaleY, spx = px * scaleX, spy = py * scaleY;
            float ox = drawX + (px + x), oy = drawY + (py + y);
            if (angle == 0)
            {
                q[0] = new PointF(ox - spx, oy - spy); q[1] = new PointF(ox + (dw - spx), oy - spy);
                q[2] = new PointF(ox + (dw - spx), oy + (dh - spy)); q[3] = new PointF(ox - spx, oy + (dh - spy));
            }
            else
            {
                float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
                Span<float> cx = stackalloc float[4] { -spx, dw - spx, dw - spx, -spx };
                Span<float> cy = stackalloc float[4] { -spy, -spy, dh - spy, dh - spy };
                for (int i = 0; i < 4; i++) q[i] = new PointF(ox + cx[i] * c - cy[i] * s, oy + cx[i] * s + cy[i] * c);
            }
            if (!matrix.IsIdentity) for (int i = 0; i < 4; i++) { var v = Vector2.Transform(new Vector2(q[i].X - ox, q[i].Y - oy), matrix); q[i] = new PointF(v.X + ox, v.Y + oy); }   // pivot-relative
        }
        /// <summary>Bounding box of the transformed picture (source space; add the draw position).</summary>
        public RectangleF Bounds(int width, int height)
        {
            Span<PointF> q = stackalloc PointF[4]; Corners(width, height, q);
            float l = q[0].X, r = l, t = q[0].Y, b = t;
            for (int i = 1; i < 4; i++) { l = Math.Min(l, q[i].X); r = Math.Max(r, q[i].X); t = Math.Min(t, q[i].Y); b = Math.Max(b, q[i].Y); }
            return RectangleF.FromLTRB(l, t, r, b);
        }
        /// <summary>Source pixel -> transformed position (source space). Perspective quads use the projective map.</summary>
        public PointF Map(PointF p, int width, int height)
        {
            if (quad == null) { var v = Vector2.Transform(new Vector2(p.X, p.Y), ToMatrix(width, height)); return new PointF(v.X, v.Y); }
            var h = Homography.RectToQuad(width, height, quad);
            return h.Map(p);
        }
        /// <summary>Transformed position -> source pixel (the inverse of <see cref="Map"/>); NaN when the transform is singular.</summary>
        public PointF Unmap(PointF p, int width, int height)
        {
            if (quad == null)
            {
                if (!Matrix3x2.Invert(ToMatrix(width, height), out var inv)) return new PointF(float.NaN, float.NaN);
                var v = Vector2.Transform(new Vector2(p.X, p.Y), inv); return new PointF(v.X, v.Y);
            }
            return Homography.RectToQuad(width, height, quad).Unmap(p);
        }
        /// <summary>True when the transformed picture covers the point (source space); alpha is not looked at.</summary>
        public bool Contains(PointF p, int width, int height)
        {
            var s = Unmap(p, width, height);
            return s.X >= 0 && s.Y >= 0 && s.X < width && s.Y < height;
        }

        /// <summary>Projective map of the unit rectangle (0,0)-(w,h) onto a quad (TL, TR, BR, BL) and back.</summary>
        internal readonly struct Homography
        {
            readonly double a, b, c, d, e, f, g, h;   // x' = (a x + b y + c) / (g x + h y + 1), y' = (d x + e y + f) / (...)
            Homography(double a, double b, double c, double d, double e, double f, double g, double h) { this.a = a; this.b = b; this.c = c; this.d = d; this.e = e; this.f = f; this.g = g; this.h = h; }
            public static Homography RectToQuad(double w, double h, PointF[] q)
            {   // Heckbert: unit square -> quad, then pre-scale by 1/w, 1/h
                double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
                double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
                double A, B, C, D, E, F, G, H;
                if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9)
                {   // affine
                    A = x1 - x0; B = x2 - x1; C = x0; D = y1 - y0; E = y2 - y1; F = y0; G = 0; H = 0;
                }
                else
                {
                    double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
                    double den = dx1 * dy2 - dx2 * dy1; if (Math.Abs(den) < 1e-12) den = 1e-12;
                    G = (sx * dy2 - dx2 * sy) / den; H = (dx1 * sy - sx * dy1) / den;
                    A = x1 - x0 + G * x1; B = x3 - x0 + H * x3; C = x0; D = y1 - y0 + G * y1; E = y3 - y0 + H * y3; F = y0;
                }
                // unit u = x / w, v = y / h
                return new Homography(A / w, B / h, C, D / w, E / h, F, G / w, H / h);
            }
            public PointF Map(PointF p)
            {
                double den = g * p.X + h * p.Y + 1; if (Math.Abs(den) < 1e-12) den = 1e-12;
                return new PointF((float)((a * p.X + b * p.Y + c) / den), (float)((d * p.X + e * p.Y + f) / den));
            }
            public PointF Unmap(PointF p)
            {   // invert the 3x3 (adjugate)
                double i00 = e - f * h, i01 = c * h - b, i02 = b * f - c * e;
                double i10 = f * g - d, i11 = a - c * g, i12 = c * d - a * f;
                double i20 = d * h - e * g, i21 = b * g - a * h, i22 = a * e - b * d;
                double den = i20 * p.X + i21 * p.Y + i22; if (Math.Abs(den) < 1e-12) return new PointF(float.NaN, float.NaN);
                return new PointF((float)((i00 * p.X + i01 * p.Y + i02) / den), (float)((i10 * p.X + i11 * p.Y + i12) / den));
            }
        }
    }

    // ------------------------------------------------------------------------------ Sprite.DrawTransformed
    public unsafe partial class Sprite
    {
        [ThreadStatic] static Effects? transformFx;

        /// <summary>
        /// Draws <paramref name="Src"/> with its top-left at (x, y) through a <see cref="SpriteTransform"/>
        /// (scale / rotation about the pivot, matrix, perspective, opacity, filter), optionally through an
        /// effect chain. An identity transform at integer coordinates is the exact 1:1 draw.
        /// </summary>
        public void DrawTransformed(Sprite Src, float x, float y, SpriteTransform T, SR2D.Op Op = SR2D.Op.DefaultOp, Effects? Fx = null, int BlendFactor = 128)
        {
            if (T == null) throw new ArgumentNullException(nameof(T));
            if (Src.meWidth <= 0 || Src.meHeight <= 0) return;
            Effects? chain = Fx != null && Fx.Count > 0 ? Fx : null;
            if (T.Opacity < 1f)
            {
                var w = transformFx ??= new Effects();
                w.Clear(); if (chain != null) w.Append(chain); w.Opacity(T.Opacity); w.Post = chain != null && chain.Post;
                chain = w;
            }
            if (T.IsIdentity && x == (int)x && y == (int)y)
            {
                if (chain != null) DrawFx(Src, (int)x, (int)y, chain, Op, false, BlendFactor);
                else Draw(Src, (int)x, (int)y, Op);
                return;
            }
            Span<PointF> q = stackalloc PointF[4];
            T.Corners(Src.meWidth, Src.meHeight, q, x, y);
            var filter = SR2D.Resolve(T.Filter, q, Src.meWidth, Src.meHeight);
            if (chain != null) DrawFxQuad(Src, q, chain, default, Op, filter, false, BlendFactor);
            else DrawQuad(Src, q, default, Op, filter, BlendFactor);
        }
    }
}
