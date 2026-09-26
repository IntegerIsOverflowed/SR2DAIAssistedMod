// LayeredSprite - a stack of sprites (each with its own effect chain, placement and blend op)
// that composes itself into ONE premultiplied sprite, and re-composes only what changed.
//
// Why it is faster than drawing the layers one by one:
//   * the layers are composed at 1:1 (exact blits, cheapest effects) and the composite is
//     warped ONCE - 12 rotated+scaled effect layers cost ~8 ms drawn individually and ~2 ms
//     as a cached composite plus one bicubic rotate (see README "LayeredSprite");
//   * layers that did not change are not redrawn at all: a prefix cache keeps the composite
//     "after layer k" for every k, so editing layer 6 of 12 re-runs layers 6..11 only, and a
//     frame where nothing changed costs one blit;
//   * effects that belong to the whole stack (one shadow / outline / glow / opacity for the
//     assembled character instead of one per layer) run once, on the composite.
//
// Change tracking is automatic: every Layer property setter, every Effects builder call
// (Effects.Version) and Add/Insert/Remove/Move mark the stack dirty from the lowest affected
// layer. The one thing that cannot be seen is drawing INTO a layer's Sprite - call
// Invalidate(layerIndex) (or layer.Invalidate()) after that.
//
// Threading: same rule as Sprite - one owner. Compose() must not run concurrently with
// itself or with reads of the composite; call Compose() before a DrawParallel and only READ
// (draw) the composite inside the bands.
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Sr2d64CSport
{
    internal sealed unsafe class LayeredSprite : IDisposable
    {
        // ------------------------------------------------------------------ Layer
        public sealed class Layer
        {
            internal LayeredSprite? owner;
            internal int index;                     // position in the stack (kept current by the owner)
            internal int seenFxVersion = -1;        // Effects.Version at the last compose
            internal Effects? seenFx;               // Effects instance at the last compose
            internal int seenXfVersion = -1;        // Transform.Version at the last compose
            internal SpriteTransform? seenXf;       // Transform instance at the last compose
            SpriteTransform? transform;
            internal Effects? withOpacity;          // private chain = Effects + Opacity stage (only when Opacity < 1)

            Sprite sprite; Effects? effects; SR2D.Op op; bool visible = true, opaque;
            float x, y, scaleX = 1, scaleY = 1, angle, pivotX = -1, pivotY = -1, opacity = 1;
            SR2D.Filter filter = SR2D.Filter.Auto;

            internal Layer(Sprite s) { sprite = s; }

            /// <summary>The layer image (not owned; you may share one sprite between layers or stacks).</summary>
            public Sprite Sprite { get => sprite; set { if (!ReferenceEquals(sprite, value)) { sprite = value ?? throw new ArgumentNullException(nameof(value)); Touch(); } } }
            /// <summary>Effect chain of this layer (null = none). Builder calls on it are noticed automatically.</summary>
            public Effects? Effects { get => effects; set { if (!ReferenceEquals(effects, value)) { effects = value; Touch(); } } }
            /// <summary>Blend op used when the layer is put onto the composite. DefaultOp = the sprite's own op. AlphaBlend sprites are premultiplied on the fly and composited with AlphaOver (no halos). Editor blend modes (<see cref="SR2D.Op.Multiply"/> ... or <see cref="BlendMode"/>) are accepted too.</summary>
            public SR2D.Op Op { get => op; set { if (op != value) { op = value; Touch(); } } }
            /// <summary>
            /// The layer's editor blend mode (Photoshop names). Reading gives Normal for the classic
            /// ops; writing sets <see cref="Op"/> to the mode. <see cref="Opacity"/> applies as usual
            /// and costs nothing extra for blend modes (it is folded into the op).
            /// </summary>
            public SR2D.BlendMode BlendMode { get => SR2D.ModeOf(op); set => Op = value.ToOp(); }
            /// <summary>Hidden layers cost nothing.</summary>
            public bool Visible { get => visible; set { if (visible != value) { visible = value; Touch(); } } }
            /// <summary>Treat the layer's alpha as 255 (e.g. a 24-bit image with garbage alpha drawn with Paint).</summary>
            public bool Opaque { get => opaque; set { if (opaque != value) { opaque = value; Touch(); } } }
            /// <summary>Top-left position of the (untransformed) layer inside the stack, in pixels. Integer values with Scale 1 / Angle 0 take the exact blit path.</summary>
            public float X { get => x; set { if (x != value) { x = value; Touch(); } } }
            public float Y { get => y; set { if (y != value) { y = value; Touch(); } } }
            /// <summary>Uniform scale (sets both ScaleX and ScaleY). 1 = none.</summary>
            /// <summary>
            /// Optional general transform of this layer (skew, mirror, perspective quad, its own filter /
            /// opacity...). When set it REPLACES X / Y / Scale / Angle / Pivot for placement (its own X / Y are
            /// the offset inside the stack). Edits through the transform's setters are tracked (Version).
            /// </summary>
            public SpriteTransform? Transform { get => transform; set { if (!ReferenceEquals(transform, value)) { transform = value; Touch(); } } }
            public float Scale { get => scaleX; set { if (scaleX != value || scaleY != value) { scaleX = scaleY = value; Touch(); } } }
            public float ScaleX { get => scaleX; set { if (scaleX != value) { scaleX = value; Touch(); } } }
            public float ScaleY { get => scaleY; set { if (scaleY != value) { scaleY = value; Touch(); } } }
            /// <summary>Rotation in radians around the pivot. 0 = none.</summary>
            public float Angle { get => angle; set { if (angle != value) { angle = value; Touch(); } } }
            /// <summary>Pivot of Scale/Angle in layer pixels; negative (default) = the layer's centre.</summary>
            public float PivotX { get => pivotX; set { if (pivotX != value) { pivotX = value; Touch(); } } }
            public float PivotY { get => pivotY; set { if (pivotY != value) { pivotY = value; Touch(); } } }
            /// <summary>Layer opacity 0..1 (an Opacity colour stage appended to the chain; free when 1).</summary>
            public float Opacity { get => opacity; set { if (opacity != value) { opacity = value; Touch(); } } }
            /// <summary>Resampling filter for a transformed layer (ignored on the exact path). Default Auto.</summary>
            public SR2D.Filter Filter { get => filter; set { if (filter != value) { filter = value; Touch(); } } }

            /// <summary>Set several placement values at once (one invalidation).</summary>
            public Layer Place(float X, float Y, float Scale = 1, float Angle = 0) { x = X; y = Y; scaleX = scaleY = Scale; angle = Angle; Touch(); return this; }
            /// <summary>Call after drawing into <see cref="Sprite"/> (pixel edits are not tracked).</summary>
            public void Invalidate() => Touch();
            /// <summary>Index of this layer in its stack (-1 when removed).</summary>
            public int Index => owner == null ? -1 : index;

            /// <summary>True when the layer is put on the composite with an exact 1:1 blit (no resampling).</summary>
            public bool IsExact => transform == null ? angle == 0 && scaleX == 1 && scaleY == 1 && x == (int)x && y == (int)y
                                                     : transform.IsIdentity && transform.X == (int)transform.X && transform.Y == (int)transform.Y;
            /// <summary>Integer placement of an exact layer (X / Y, or the transform's X / Y).</summary>
            internal int ExactX => (int)(transform?.X ?? x);
            internal int ExactY => (int)(transform?.Y ?? y);
            /// <summary>Resampling filter actually used (the transform's when set).</summary>
            internal SR2D.Filter EffectiveFilter => transform != null && filter == SR2D.Filter.Auto ? transform.Filter : filter;
            /// <summary>Opacity actually used (layer opacity x transform opacity).</summary>
            internal float EffectiveOpacity => transform == null ? opacity : opacity * transform.Opacity;

            void Touch() => owner?.Touch(index);
        }

        // ------------------------------------------------------------------ state
        readonly List<Layer> layers = new List<Layer>();
        readonly Sprite composite;                       // premultiplied, Op = AlphaOver
        readonly List<Sprite?> prefix = new List<Sprite?>();   // prefix[i] = composite after layers 0..i (null = not allocated / invalid)
        readonly List<bool> prefixValid = new List<bool>();
        int dirtyFrom;                                   // lowest layer that must be redrawn (Count = clean)
        bool composing;                                  // re-entrancy guard
        bool disposed;
        Sprite[]? scratch;                               // per-worker pre-render targets (Threads > 1), composite-sized
        Rectangle[]? scratchUsed;                        // what each scratch holds from the last batch (cleared before reuse)
        bool[]? batchPre; Rectangle[]? batchRect;        // per batch slot: pre-rendered? / rectangle it touches

        /// <summary>Creates an empty stack of the given size. The composite is a normal premultiplied sprite (Op AlphaOver).</summary>
        public LayeredSprite(int Width, int Height)
        {
            if (Width <= 0 || Height <= 0) throw new ArgumentOutOfRangeException(nameof(Width));
            composite = new Sprite(Width, Height, SR2D.Op.AlphaOver);
            composite.ClearBuffer(0);
        }

        public int Width => composite.Width;
        public int Height => composite.Height;
        public int Count => layers.Count;
        public Layer this[int Index] => layers[Index];
        public IReadOnlyList<Layer> Layers => layers;

        /// <summary>
        /// Effects applied to the WHOLE stack when it is drawn with the Draw* methods of this
        /// class (one shadow / outline / glow / opacity for the assembled image). Null = none.
        /// Not part of the composite, so changing it never triggers a re-compose.
        /// </summary>
        public Effects? Effects { get; set; }

        SpriteTransform? transform;
        /// <summary>
        /// General transform of the WHOLE stack: every Draw* call of this class draws the composite
        /// through it (scale / rotation about a pivot, offset, an affine <see cref="SpriteTransform.Matrix"/>
        /// for skew / mirror, or a perspective <see cref="SpriteTransform.Quad"/>; plus its Filter and
        /// Opacity). Created on first access; <see cref="HasTransform"/> tells whether it exists.
        /// Like <see cref="Effects"/> it is not part of the composite: changing it never
        /// recomposes - the stack is warped once per draw whatever the transform says. Geometry
        /// given to DrawScaled / DrawRotate2 / DrawQuad is applied ON TOP of it (the transformed stack
        /// is what those calls place). Distortions of the whole stack are effects: put a Wave /
        /// Ripple / Turbulence / DistortMap stage in <see cref="Effects"/>.
        /// </summary>
        public SpriteTransform Transform => transform ??= new SpriteTransform();
        /// <summary>True once <see cref="Transform"/> has been created and is not the identity at full opacity.</summary>
        public bool HasTransform => transform != null && !transform.IsPlain;
        /// <summary>Removes the stack transform (back to plain draws).</summary>
        public void ClearTransform() => transform = null;

        /// <summary>
        /// Keep a copy of the composite after every layer (Width*Height*4 bytes per layer, one
        /// memcpy each) so that a change to layer k only redraws layers k..Count-1. Default on.
        /// Off = every change redraws the whole stack (still one warp per frame).
        /// </summary>
        public bool PrefixCache
        {
            get => prefixCache;
            set { if (prefixCache != value) { prefixCache = value; if (!value) DropPrefixes(); } }
        }
        bool prefixCache = true;

        /// <summary>
        /// Worker threads for <see cref="Compose"/>. 1 (default) = sequential, bit-identical to
        /// before. 0 = one per core, N = N. With more than one thread the dirty layers are
        /// processed in batches of that size: every layer of a batch runs its effect chain into
        /// its own scratch image concurrently (that is where the time goes - a blur costs 0.3–1.3 ms
        /// per 256² layer), then the batch is composited in order, so the result is the same as the
        /// sequential one to the bit (the compositing ops are applied to the very same pixels).
        /// Layers that need no effects (plain blits) are not worth a thread and are simply drawn
        /// in order; so are Paint / AlphaTest / Mul / Min / Blend layers, whose ops are not
        /// neutral for transparent pixels. Scratch memory: up to 4 x <c>Threads</c> composite-sized
        /// sprites (capped at 64 MB), allocated once. Turn <see cref="PrefixCache"/> off when every
        /// layer changes every frame (it would only add a memcpy per layer). Worth it when the
        /// per-layer effects cost more than ~0.3 ms each (a Gaussian blur of a 256² layer is 0.65 ms,
        /// a box + downscaled one 0.2 ms - for those, cores are better spent on DrawParallel of the
        /// final canvas).
        /// </summary>
        public int Threads { get => threads; set => threads = value < 0 ? 1 : value; }
        int threads = 1;

        /// <summary>True when the next <see cref="Compose"/> will redraw something.</summary>
        public bool IsDirty => FirstChanged() < layers.Count;

        /// <summary>Layer index the last <see cref="Compose"/> started from; -1 when it found nothing to do.</summary>
        public int LastComposedFrom { get; private set; } = -1;

        // ------------------------------------------------------------------ edit the stack
        /// <summary>Appends a layer on top. Returns it for further configuration.</summary>
        public Layer Add(Sprite Sprite, Effects? Effects = null, float X = 0, float Y = 0, SR2D.Op Op = SR2D.Op.DefaultOp)
            => Insert(layers.Count, Sprite, Effects, X, Y, Op);

        /// <summary>Inserts a layer at <paramref name="Index"/> (0 = bottom).</summary>
        public Layer Insert(int Index, Sprite Sprite, Effects? Effects = null, float X = 0, float Y = 0, SR2D.Op Op = SR2D.Op.DefaultOp)
        {
            if (Sprite == null) throw new ArgumentNullException(nameof(Sprite));
            if ((uint)Index > (uint)layers.Count) throw new ArgumentOutOfRangeException(nameof(Index));
            var L = new Layer(Sprite) { Effects = Effects, X = X, Y = Y, Op = Op };   // not owned yet: setters do not invalidate
            layers.Insert(Index, L);
            prefix.Insert(Index, null); prefixValid.Insert(Index, false);
            Reindex(Index);
            L.owner = this;
            Touch(Index);
            return L;
        }

        public void RemoveAt(int Index)
        {
            var L = layers[Index];
            L.owner = null;
            layers.RemoveAt(Index);
            prefix[Index]?.Dispose(); prefix.RemoveAt(Index); prefixValid.RemoveAt(Index);
            Reindex(Index);
            Touch(Index);
        }
        public bool Remove(Layer L) { int i = layers.IndexOf(L); if (i < 0) return false; RemoveAt(i); return true; }
        public void Clear() { for (int i = layers.Count - 1; i >= 0; i--) RemoveAt(i); }

        /// <summary>Moves a layer to another position (e.g. bring to front: Move(i, Count - 1)).</summary>
        public void Move(int From, int To)
        {
            if ((uint)From >= (uint)layers.Count) throw new ArgumentOutOfRangeException(nameof(From));
            if ((uint)To >= (uint)layers.Count) throw new ArgumentOutOfRangeException(nameof(To));
            if (From == To) return;
            var L = layers[From]; layers.RemoveAt(From); layers.Insert(To, L);
            int lo = Math.Min(From, To);
            Reindex(lo);
            Touch(lo);
        }

        /// <summary>Marks layers <paramref name="From"/>.. as changed (after drawing into a layer sprite, or changing a DistortMap's pixels).</summary>
        public void Invalidate(int From = 0) => Touch(Math.Max(0, From));

        void Reindex(int from) { for (int i = from; i < layers.Count; i++) layers[i].index = i; }
        internal void Touch(int index) { if (index < dirtyFrom) dirtyFrom = index; }
        void DropPrefixes() { for (int i = 0; i < prefix.Count; i++) { prefix[i]?.Dispose(); prefix[i] = null; prefixValid[i] = false; } }

        // ------------------------------------------------------------------ compose
        /// <summary>
        /// The composed image: premultiplied, Op AlphaOver, usable with every draw call
        /// (Draw, DrawRotate2, DrawQuad, DrawFx..., DrawParallel bands). Re-composes first if
        /// anything changed. Do not draw into it.
        /// </summary>
        public Sprite Sprite { get { Compose(); return composite; } }

        /// <summary>
        /// Redraws the changed layers (all layers from the lowest changed one). Returns true
        /// when something was redrawn. Called automatically by <see cref="Sprite"/> and the
        /// Draw* helpers; call it yourself to compose ahead of time (e.g. before DrawParallel).
        /// </summary>
        public bool Compose()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LayeredSprite));
            if (composing) return false;                 // never re-enter (a layer sprite that is this composite, a callback, ...)
            composing = true;
            try
            {
                int n = layers.Count;
                int first = FirstChanged();
                if (first >= n) { LastComposedFrom = -1; return false; }

                // restore the composite "after layer first-1" or start from transparent
                if (first > 0 && prefixCache && prefixValid[first - 1] && prefix[first - 1] != null) Copy(prefix[first - 1]!, composite);
                else { first = 0; composite.ClearBuffer(0); }

                int T = threads == 0 ? Environment.ProcessorCount : threads;
                if (T > 64) T = 64;
                // batch = a few layers per thread (fewer fork/join points), bounded by 64 MB of scratch
                int B = T <= 1 ? 1 : (int)Math.Clamp(Math.Min(4L * T, (64L << 20) / Math.Max(1L, 4L * composite.Width * composite.Height)), T, 64);
                for (int i = first; i < n; )
                {
                    if (T <= 1 || n - i < 2) { DrawLayer(layers[i]); Composed(i, n); i++; continue; }

                    // ---- a batch of up to B layers: effects in parallel into the scratches, then composite in order
                    int b = Math.Min(B, n - i);
                    EnsureScratch(B);
                    var pre = batchPre!; var rect = batchRect!;
                    int npre = 0;
                    for (int j = 0; j < b; j++) { pre[j] = WantsPreRender(layers[i + j]); if (pre[j]) npre++; }
                    if (npre >= 2)
                    {
                        int i0 = i;
                        System.Threading.Tasks.Parallel.For(0, b, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = T }, j =>
                        {
                            if (pre[j]) rect[j] = PreRender(layers[i0 + j], j);
                        });
                    }
                    for (int j = 0; j < b; j++)
                    {
                        Layer L = layers[i + j];
                        if (npre >= 2 && pre[j]) Blit(L, j, rect[j]);
                        else DrawLayer(L);
                        Composed(i + j, n);
                    }
                    i += b;
                }
                dirtyFrom = n;
                LastComposedFrom = first;
                return true;
            }
            finally { composing = false; }
        }

        void Composed(int i, int n)
        {
            Layer L = layers[i];
            L.seenFx = L.Effects; L.seenFxVersion = L.Effects?.Version ?? -1;
            L.seenXf = L.Transform; L.seenXfVersion = L.Transform?.Version ?? -1;
            if (prefixCache && i < n - 1) StorePrefix(composite, i);   // field, never the lazy property
            else prefixValid[i] = false;
        }

        // ---- parallel compose helpers -------------------------------------------------
        void EnsureScratch(int T)
        {
            if (scratch != null && scratch.Length >= T) return;
            DropScratch();
            scratch = new Sprite[T]; scratchUsed = new Rectangle[T];
            for (int j = 0; j < T; j++) { scratch[j] = new Sprite(composite.Width, composite.Height, SR2D.Op.AlphaOver); scratch[j].ClearBuffer(0); }
            batchPre = new bool[T]; batchRect = new Rectangle[T];
        }
        void DropScratch()
        {
            if (scratch == null) return;
            foreach (var s in scratch) s.Dispose();
            scratch = null; scratchUsed = null; batchPre = null; batchRect = null;
        }

        // Resolved drawing parameters of a layer, shared by the sequential and the parallel path.
        bool Resolve(Layer L, out Sprite s, out SR2D.Op op, out bool opaque, out Effects? fx)
        {
            s = L.Sprite; op = default; opaque = false; fx = null;
            if (!L.Visible || s.Width <= 0 || s.Height <= 0 || ReferenceEquals(s, composite)) return false;
            op = L.Op == SR2D.Op.DefaultOp ? s.Op : L.Op;
            // straight-alpha blending onto a transparent premultiplied buffer must go through
            // AlphaOver (DrawFx premultiplies a non-AlphaOver source on the fly)
            if (op == SR2D.Op.AlphaBlend) op = SR2D.Op.AlphaOver;
            opaque = L.Opaque || op == SR2D.Op.Paint;          // Paint = the whole rectangle, alpha 255
            fx = L.Effects;
            float opac = L.EffectiveOpacity;
            if (SR2D.IsBlendMode(op))
            {   // blend modes carry their own opacity: no Effects stage needed
                op = SR2D.WithOpacity(op, Math.Max(0f, Math.Min(1f, opac)));
                L.withOpacity = null;
                return true;
            }
            if (opac < 1f)
            {
                var w = L.withOpacity ??= new Effects();
                w.Clear(); if (fx != null) w.Append(fx); w.Opacity(Math.Max(0f, opac));
                w.Post = fx != null && fx.Post;
                fx = w;
            }
            else L.withOpacity = null;
            return true;
        }
        // exact layers without effects take the plain-blit path in DrawLayer
        static bool IsPlainBlit(Layer L, Sprite s, SR2D.Op op, Effects? fx)
            => L.IsExact && (fx == null || fx.Count == 0) && !L.Opaque && op != SR2D.Op.Paint && op != SR2D.Op.AlphaTest && (op != SR2D.Op.AlphaOver || s.Premultiplied);

        // A layer is pre-rendered when its effects are the expensive part and its op is neutral
        // for transparent pixels (so compositing the padded scratch equals compositing the work
        // image directly): AlphaOver, Add, Add2D, Max.
        bool WantsPreRender(Layer L)
        {
            if (!Resolve(L, out var s, out var op, out _, out var fx)) return false;
            if (IsPlainBlit(L, s, op, fx)) return false;
            return op == SR2D.Op.AlphaOver || op == SR2D.Op.Add || op == SR2D.Op.Add2D || op == SR2D.Op.Max || SR2D.IsBlendMode(op);   // blend modes: alpha 0 = no coverage
        }

        // Effects of layer L into scratch[j] (Paint = verbatim premultiplied copy of the work image); returns the rectangle it touched.
        Rectangle PreRender(Layer L, int j)
        {
            Sprite t = scratch![j];
            Rectangle used = scratchUsed![j];
            if (!used.IsEmpty) { t.SetLockRect(used); t.ClearBuffer(0); t.SetLockRect(); }
            if (!Resolve(L, out var s, out _, out var opaque, out var fx)) { scratchUsed[j] = Rectangle.Empty; return Rectangle.Empty; }
            Effects chain = fx ?? Empty;
            int M = chain.Margin;
            Rectangle r;
            if (L.IsExact)
            {
                int x = L.ExactX, y = L.ExactY;
                r = Rectangle.Intersect(new Rectangle(x - M, y - M, s.Width + 2 * M, s.Height + 2 * M), new Rectangle(0, 0, t.Width, t.Height));
                if (!r.IsEmpty) t.DrawFx(s, x, y, chain, SR2D.Op.Paint, opaque);
            }
            else
            {
                Span<PointF> q = stackalloc PointF[4];
                LayerQuad(L, s, q);
                float x0 = q[0].X, x1 = q[0].X, y0 = q[0].Y, y1 = q[0].Y;
                for (int i = 1; i < 4; i++) { x0 = Math.Min(x0, q[i].X); x1 = Math.Max(x1, q[i].X); y0 = Math.Min(y0, q[i].Y); y1 = Math.Max(y1, q[i].Y); }
                float sc = L.Transform != null ? Math.Max((x1 - x0) / Math.Max(1, s.Width), (y1 - y0) / Math.Max(1, s.Height)) : Math.Max(Math.Abs(L.ScaleX), Math.Abs(L.ScaleY));
                float grow = chain.Post ? M : M * sc;
                grow += 3;                                         // filter footprint
                r = Rectangle.Intersect(Rectangle.FromLTRB((int)Math.Floor(x0 - grow), (int)Math.Floor(y0 - grow), (int)Math.Ceiling(x1 + grow), (int)Math.Ceiling(y1 + grow)),
                                        new Rectangle(0, 0, t.Width, t.Height));
                if (!r.IsEmpty) t.DrawFxQuad(s, q, chain, default, SR2D.Op.Paint, L.EffectiveFilter, opaque);
            }
            scratchUsed[j] = r;
            return r;
        }

        // composite <- scratch[j] with the layer's op, restricted to the rectangle the pre-render touched
        void Blit(Layer L, int j, Rectangle r)
        {
            if (r.IsEmpty) return;
            Resolve(L, out _, out var op, out _, out _);
            composite.SetLockRect(r);
            composite.Draw(scratch![j], 0, 0, op);
            composite.SetLockRect();
        }

        static void LayerQuad(Layer L, Sprite s, Span<PointF> q)
        {
            if (L.Transform != null) { L.Transform.Corners(s.Width, s.Height, q); return; }   // its X / Y are inside Corners
            float px = L.PivotX < 0 ? s.Width * 0.5f : L.PivotX, py = L.PivotY < 0 ? s.Height * 0.5f : L.PivotY;
            float c = (float)Math.Cos(L.Angle), sn = (float)Math.Sin(L.Angle);
            float ox = L.X + px, oy = L.Y + py;                      // pivot stays at its untransformed place
            for (int i = 0; i < 4; i++)
            {
                float cx = ((i == 1 || i == 2) ? s.Width : 0) - px, cy = (i >= 2 ? s.Height : 0) - py;
                cx *= L.ScaleX; cy *= L.ScaleY;
                q[i] = new PointF(ox + cx * c - cy * sn, oy + cx * sn + cy * c);
            }
        }

        int FirstChanged()
        {
            int first = dirtyFrom, n = layers.Count;
            if (first > n) first = n;
            for (int i = 0; i < first; i++)              // effects edited through their builders since last compose?
            {
                Layer L = layers[i];
                Effects? fx = L.Effects;
                if (!ReferenceEquals(fx, L.seenFx) || (fx != null && fx.Version != L.seenFxVersion)) { first = i; break; }
                SpriteTransform? xf = L.Transform;
                if (!ReferenceEquals(xf, L.seenXf) || (xf != null && xf.Version != L.seenXfVersion)) { first = i; break; }
            }
            if (first < dirtyFrom) dirtyFrom = first;
            return first;
        }

        void StorePrefix(Sprite from, int i)
        {
            Sprite? p = prefix[i];
            if (p == null) { p = new Sprite(from.Width, from.Height, SR2D.Op.AlphaOver); prefix[i] = p; }
            Copy(from, p);
            prefixValid[i] = true;
        }

        static void Copy(Sprite from, Sprite to)
        {
            long bytes = (long)from.Width * from.Height * 4;
            Buffer.MemoryCopy(from.PixelPtr, to.PixelPtr, bytes, bytes);
        }

        void DrawLayer(Layer L)
        {
            if (!Resolve(L, out var s, out var op, out var opaque, out var fx)) return;
            if (L.IsExact)
            {
                int x = L.ExactX, y = L.ExactY;
                if (IsPlainBlit(L, s, op, fx))
                {
                    composite.Draw(s, x, y, op);                     // exact blit (AlphaOver, Add, Mul, ...)
                    return;
                }
                // Paint / AlphaTest / a straight-alpha source under AlphaOver need the
                // premultiplying DrawFx path; everything else is a plain blit
                composite.DrawFx(s, x, y, fx ?? Empty, op, opaque);
                return;
            }
            // transformed layer: one warp of this layer onto the composite
            Span<PointF> q = stackalloc PointF[4];
            LayerQuad(L, s, q);
            composite.DrawFxQuad(s, q, fx ?? Empty, default, op, L.EffectiveFilter, opaque);
        }

        [ThreadStatic] static Effects? empty;
        static Effects Empty => empty ??= new Effects();

        // ------------------------------------------------------------------ draw the stack
        [ThreadStatic] static Effects? withOpacityFx;
        // the effect chain to draw with: Effects (+ the transform's opacity as a last stage)
        Effects? DrawChain()
        {
            Effects? fx = Effects != null && Effects.Count > 0 ? Effects : null;
            float o = transform?.Opacity ?? 1f;
            if (o >= 1f) return fx;
            var w = withOpacityFx ??= new Effects();
            w.Clear(); if (fx != null) w.Append(fx); w.Opacity(o); w.Post = fx != null && fx.Post;
            return w;
        }
        // corners of the (transformed) stack in composite space: TL, TR, BR, BL
        void StackCorners(Span<PointF> q, float dx = 0, float dy = 0)
        {
            if (transform != null && !transform.IsIdentity) transform.Corners(composite.Width, composite.Height, q, dx, dy);
            else { q[0] = new PointF(dx, dy); q[1] = new PointF(dx + composite.Width, dy); q[2] = new PointF(dx + composite.Width, dy + composite.Height); q[3] = new PointF(dx, dy + composite.Height); }
        }
        SR2D.Filter Pick(SR2D.Filter f) => f == SR2D.Filter.Auto && transform != null ? transform.Filter : f;
        void DrawCorners(Sprite Target, Sprite s, ReadOnlySpan<PointF> q, Effects? fx, SR2D.Filter Filter)
        {
            var f = SR2D.Resolve(Pick(Filter), q, s.Width, s.Height);
            if (fx != null) Target.DrawFxQuad(s, q, fx, default, SR2D.Op.AlphaOver, f);
            else Target.DrawQuad(s, q, default, SR2D.Op.AlphaOver, f);
        }

        /// <summary>Draws the composite at (x, y) on <paramref name="Target"/> (AlphaOver), through <see cref="Transform"/> and <see cref="Effects"/> when set.</summary>
        public void Draw(Sprite Target, int x, int y)
        {
            Sprite s = Sprite;
            var fx = DrawChain();
            if (transform == null || transform.IsIdentity)
            {
                if (fx != null) Target.DrawFx(s, x, y, fx, SR2D.Op.AlphaOver);
                else Target.Draw(s, x, y, SR2D.Op.AlphaOver);
                return;
            }
            Span<PointF> q = stackalloc PointF[4];
            StackCorners(q, x, y);
            DrawCorners(Target, s, q, fx, SR2D.Filter.Auto);
        }
        /// <summary>Same with a fractional position.</summary>
        public void Draw(Sprite Target, float x, float y)
        {
            if (x == (int)x && y == (int)y) { Draw(Target, (int)x, (int)y); return; }
            Sprite s = Sprite;
            Span<PointF> q = stackalloc PointF[4];
            StackCorners(q, x, y);
            DrawCorners(Target, s, q, DrawChain(), SR2D.Filter.Auto);
        }

        /// <summary>Draws the (transformed) stack scaled so that the composite's rectangle becomes <paramref name="DestWidth"/> x <paramref name="DestHeight"/> at (x, y).</summary>
        public void DrawScaled(Sprite Target, int x, int y, int DestWidth, int DestHeight, SR2D.Filter Filter = SR2D.Filter.Auto)
        {
            Sprite s = Sprite;
            var fx = DrawChain();
            if (transform == null || transform.IsIdentity)
            {
                if (fx != null) Target.DrawFxScaled(s, x, y, DestWidth, DestHeight, fx, SR2D.Op.AlphaOver, Filter);
                else Target.DrawScaled(s, x, y, DestWidth, DestHeight, SR2D.Op.AlphaOver, Filter);
                return;
            }
            Span<PointF> q = stackalloc PointF[4];
            StackCorners(q);
            float sx = (float)DestWidth / s.Width, sy = (float)DestHeight / s.Height;
            for (int i = 0; i < 4; i++) q[i] = new PointF(x + q[i].X * sx, y + q[i].Y * sy);
            DrawCorners(Target, s, q, fx, Filter);
        }

        /// <summary>Draws the (transformed) stack rotated by <paramref name="Angle"/> (radians) around its pivot placed at (x, y); DestWidth/Height 0 = unscaled.</summary>
        public void DrawRotate2(Sprite Target, int x, int y, float Angle, int DestWidth = 0, int DestHeight = 0,
                                float PivotX = -1, float PivotY = -1, SR2D.Filter Filter = SR2D.Filter.Auto)
        {
            Sprite s = Sprite;
            var fx = DrawChain();
            if (transform == null || transform.IsIdentity)
            {
                if (fx != null) Target.DrawFxRotated(s, x, y, Angle, fx, DestWidth, DestHeight, PivotX, PivotY, SR2D.Op.AlphaOver, Filter);
                else Target.DrawRotate2(s, x, y, Angle, DestWidth, DestHeight, PivotX, PivotY, SR2D.Op.AlphaOver, Filter);
                return;
            }
            if (DestWidth == 0) DestWidth = s.Width;
            if (DestHeight == 0) DestHeight = s.Height;
            if (PivotX < 0) PivotX = s.Width * 0.5f;
            if (PivotY < 0) PivotY = s.Height * 0.5f;
            float sx = (float)DestWidth / s.Width, sy = (float)DestHeight / s.Height;
            float c = (float)Math.Cos(Angle), sn = (float)Math.Sin(Angle);
            Span<PointF> q = stackalloc PointF[4];
            StackCorners(q);
            for (int i = 0; i < 4; i++)
            {   // same arithmetic as DrawRotate2: pivot-relative, scaled, rotated, placed at (x, y)
                float cx = (q[i].X - PivotX) * sx, cy = (q[i].Y - PivotY) * sy;
                q[i] = new PointF(x + cx * c - cy * sn, y + cx * sn + cy * c);
            }
            DrawCorners(Target, s, q, fx, Filter);
        }

        /// <summary>Old name of <see cref="DrawRotate2"/>.</summary>
        [Obsolete("Renamed to DrawRotate2.")]
        public void DrawRotated(Sprite Target, int x, int y, float Angle, int DestWidth = 0, int DestHeight = 0, float PivotX = -1, float PivotY = -1, SR2D.Filter Filter = SR2D.Filter.Auto)
            => DrawRotate2(Target, x, y, Angle, DestWidth, DestHeight, PivotX, PivotY, Filter);

        /// <summary>Draws the (transformed) stack onto an arbitrary quad (TL, TR, BR, BL = where the composite's corners go).</summary>
        public void DrawQuad(Sprite Target, ReadOnlySpan<PointF> Quad, SR2D.Filter Filter = SR2D.Filter.Auto)
        {
            if (Quad.Length != 4) throw new ArgumentException("Quad must contain exactly 4 points.", nameof(Quad));
            Sprite s = Sprite;
            var fx = DrawChain();
            if (transform == null || transform.IsIdentity)
            {
                if (fx != null) Target.DrawFxQuad(s, Quad, fx, default, SR2D.Op.AlphaOver, Filter);
                else Target.DrawQuad(s, Quad, default, SR2D.Op.AlphaOver, Filter);
                return;
            }
            // the call's quad is a projective map of the composite rectangle: push the transformed corners through it
            var H = SpriteTransform.Homography.RectToQuad(s.Width, s.Height, Quad.ToArray());
            Span<PointF> q = stackalloc PointF[4];
            StackCorners(q);
            for (int i = 0; i < 4; i++) q[i] = H.Map(q[i]);
            DrawCorners(Target, s, q, fx, Filter);
        }

        /// <summary>Draws the stack through an explicit transform (ignores <see cref="Transform"/>) at (x, y).</summary>
        public void DrawTransformed(Sprite Target, float x, float y, SpriteTransform T)
        {
            Sprite s = Sprite;
            Target.DrawTransformed(s, x, y, T, SR2D.Op.AlphaOver, Effects != null && Effects.Count > 0 ? Effects : null);
        }

        /// <summary>Bounding box of the stack as it will be drawn at (x, y) (transform applied; effect margins not included).</summary>
        public RectangleF DrawBounds(float x = 0, float y = 0)
        {
            Span<PointF> q = stackalloc PointF[4];
            StackCorners(q);
            float l = q[0].X, r = l, t = q[0].Y, b = t;
            for (int i = 1; i < 4; i++) { l = Math.Min(l, q[i].X); r = Math.Max(r, q[i].X); t = Math.Min(t, q[i].Y); b = Math.Max(b, q[i].Y); }
            return RectangleF.FromLTRB(l + x, t + y, r + x, b + y);
        }
        /// <summary>Composite pixel under a point of the drawn stack (drawn at (x, y) with Draw): the inverse transform; null outside.</summary>
        public Point? HitTest(float px, float py, float x = 0, float y = 0)
        {
            var p = new PointF(px - x, py - y);
            if (transform != null && !transform.IsIdentity) p = transform.Unmap(p, composite.Width, composite.Height);
            if (float.IsNaN(p.X) || p.X < 0 || p.Y < 0 || p.X >= composite.Width || p.Y >= composite.Height) return null;
            return new Point((int)p.X, (int)p.Y);
        }

        /// <summary>Frees the composite and the prefix cache (layer sprites are not owned).</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var L in layers) L.owner = null;
            layers.Clear();
            DropPrefixes();
            DropScratch();
            composite.Dispose();
        }
    }
}
