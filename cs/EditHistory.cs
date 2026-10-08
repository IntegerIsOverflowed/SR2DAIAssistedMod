using System;
using System.Collections.Generic;
using System.Drawing;

namespace Sr2d64CSport
{
    /// <summary>
    /// Rect-based undo/redo for in-place edits. Call <see cref="Record(Sprite, Rectangle)"/> (or the
    /// <see cref="VoxelGrid"/> overload) BEFORE a verb mutates the target: it copies the affected pixels
    /// away, and <see cref="Undo(Sprite)"/> pastes them back, <see cref="Redo(Sprite)"/> re-applies the
    /// newer state. Only the affected rectangle is stored, so snapshotting a small area costs nothing
    /// regardless of the sprite size. Entries are independent snapshots on one timeline: undoing walks
    /// back the newest entry first, a new <see cref="Record(Sprite, Rectangle)"/> after an undo discards
    /// the redo tail (like every editor). A memory cap (default 64 MB) drops the oldest entries first.
    /// The history stores pixel data only - it does not keep the target alive; <see cref="Undo(Sprite)"/>
    /// applies to whatever sprite is passed and checks the dimensions match.
    /// </summary>
    internal sealed class EditHistory
    {
        /// <summary>Creates a history with the given memory cap in bytes (snapshots are dropped oldest-first when it is exceeded).</summary>
        // the class is internal (like the whole wrapper surface); the members stay public for the object-initialiser syntax
        public EditHistory(int memoryCapBytes = 64 << 20) { MemoryCap = Math.Max(4096, memoryCapBytes); }

        /// <summary>Snapshot bytes the history may keep (both undo and redo sides). Shrinking it drops the oldest entries until it fits.</summary>
        public int MemoryCap { get; set; }

        /// <summary>Bytes currently held by the snapshots (undo + redo).</summary>
        public long MemoryBytes { get; private set; }

        /// <summary>How many steps <see cref="Undo(Sprite)"/> can take.</summary>
        public int UndoCount => _undo.Count;
        /// <summary>How many steps <see cref="Redo(Sprite)"/> can take.</summary>
        public int RedoCount => _redo.Count;
        /// <summary>True when <see cref="Undo(Sprite)"/> would restore something.</summary>
        public bool CanUndo => _undo.Count > 0;
        /// <summary>True when <see cref="Redo(Sprite)"/> would re-apply something.</summary>
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Forgets everything (both directions).</summary>
        public void Clear() { _undo.Clear(); _redo.Clear(); MemoryBytes = 0; }

        long EntryBytes(Entry e) => e.Bytes + (e.After != null ? e.AfterBytes : 0L);
        /// <summary>Drops the redo tail, subtracting what those entries were charged (they keep their captured After state).</summary>
        void ClearRedo()
        {
            foreach (var e in _redo) MemoryBytes -= EntryBytes(e);
            _redo.Clear();
        }
        [System.Diagnostics.Conditional("DEBUG")]
        void CheckBytes()
        {   // the account must always equal the sum of the per-entry charges - a drift would silently
            // evict valid history or let it grow past the cap
            long sum = 0; foreach (var e in _undo) sum += EntryBytes(e); foreach (var e in _redo) sum += EntryBytes(e);
            System.Diagnostics.Debug.Assert(sum == MemoryBytes, $"EditHistory byte accounting drifted: tracked {MemoryBytes}, actual {sum}");
        }

        readonly List<Entry> _undo = new(), _redo = new();

        /// <summary>
        /// Snapshots the current pixels of <paramref name="rect"/> (clipped to the sprite). Call it before the verb runs.
        /// Returns the clipped rectangle actually recorded, or <see cref="Rectangle.Empty"/> when there is nothing to record.
        /// Discards the redo tail.
        /// </summary>
        public Rectangle Record(Sprite target, Rectangle rect)
        {
            ArgumentNullException.ThrowIfNull(target);
            rect = Rectangle.Intersect(rect, new Rectangle(0, 0, target.Width, target.Height));
            if (rect.IsEmpty) return Rectangle.Empty;
            var before = new int[rect.Width * rect.Height];
            CopyOut(target, rect, before);
            ClearRedo();
            var e = Entry.SpriteEntry(rect, before, target.Width, target.Height);
            _undo.Add(e); MemoryBytes += before.Length * 4L;
            Trim(); CheckBytes();
            return rect;
        }

        /// <summary>
        /// Snapshots the current voxels of the box [<paramref name="x0"/>..<paramref name="x1"/>) x [<paramref name="y0"/>..<paramref name="y1"/>)
        /// x [<paramref name="z0"/>..<paramref name="z1"/>) (clipped to the grid). Call it before the verb runs. Discards the redo tail.
        /// Returns true when something was recorded.
        /// </summary>
        public bool Record(VoxelGrid target, int x0, int y0, int z0, int x1, int y1, int z1)
        {
            ArgumentNullException.ThrowIfNull(target);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); z0 = Math.Max(0, z0);
            x1 = Math.Min(target.Width, x1); y1 = Math.Min(target.Height, y1); z1 = Math.Min(target.Depth, z1);
            if (x1 <= x0 || y1 <= y0 || z1 <= z0) return false;
            var before = new Voxel[(x1 - x0) * (y1 - y0) * (z1 - z0)];
            int k = 0;
            for (int z = z0; z < z1; z++) for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) before[k++] = target[x, y, z];
            ClearRedo();
            var e = Entry.VoxelEntry(new Rectangle(x0, y0, x1 - x0, y1 - y0), z0, z1 - z0, before, target.Width, target.Height, target.Depth);
            _undo.Add(e); MemoryBytes += before.Length * VoxelBytes;
            Trim(); CheckBytes();
            return true;
        }

        static readonly int VoxelBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<Voxel>();

        /// <summary>Restores the newest snapshot into <paramref name="target"/> (dimensions are checked). Returns false when there is nothing to undo.</summary>
        public bool Undo(Sprite target)
        {
            if (_undo.Count == 0 || target == null) return false;
            var e = _undo[^1];
            if (e.IsVoxels || e.W != target.Width || e.H != target.Height) return false;
            var r = e.Rect;
            var after = new int[r.Width * r.Height];
            CopyOut(target, r, after);
            e.After = after; MemoryBytes += after.Length * 4L;   // the entry is now charged Before + After, in EITHER list
            CopyIn(target, r, (int[])e.Before);
            _undo.RemoveAt(_undo.Count - 1); _redo.Add(e);       // moving between the lists does not change the charge
            Trim(); CheckBytes();
            return true;
        }

        /// <summary>Re-applies the newest undone edit to <paramref name="target"/> (dimensions are checked). Returns false when there is nothing to redo.</summary>
        public bool Redo(Sprite target)
        {
            if (_redo.Count == 0 || target == null) return false;
            var e = _redo[^1];
            if (e.IsVoxels || e.After is not int[] px || e.W != target.Width || e.H != target.Height) return false;
            CopyIn(target, e.Rect, px);
            _redo.RemoveAt(_redo.Count - 1); _undo.Add(e);       // the charge stays Before + After wherever the entry lives
            Trim(); CheckBytes();
            return true;
        }

        /// <summary>Restores the newest voxel snapshot into <paramref name="target"/> (dimensions are checked). Returns false when there is nothing to undo.</summary>
        public bool Undo(VoxelGrid target)
        {
            if (_undo.Count == 0 || target == null) return false;
            var e = _undo[^1];
            if (!e.IsVoxels || e.W != target.Width || e.H != target.Height || e.D != target.Depth) return false;
            var after = new Voxel[e.Rect.Width * e.Rect.Height * e.DepthVox];
            int k = 0, x1 = e.Rect.X + e.Rect.Width, y1 = e.Rect.Y + e.Rect.Height;   // capture the current (edited) state for the redo
            for (int z = e.Z0; z < e.Z0 + e.DepthVox; z++)
                for (int y = e.Rect.Y; y < y1; y++)
                    for (int x = e.Rect.X; x < x1; x++) after[k++] = target[x, y, z];
            e.After = after; MemoryBytes += after.Length * VoxelBytes;
            CopyVoxIn(target, e, (Voxel[])e.Before);
            _undo.RemoveAt(_undo.Count - 1); _redo.Add(e);
            Trim(); CheckBytes();
            return true;
        }

        /// <summary>Re-applies the newest undone voxel edit to <paramref name="target"/> (dimensions are checked). Returns false when there is nothing to redo.</summary>
        public bool Redo(VoxelGrid target)
        {
            if (_redo.Count == 0 || target == null) return false;
            var e = _redo[^1];
            if (!e.IsVoxels || e.After is not Voxel[] vx || e.W != target.Width || e.H != target.Height || e.D != target.Depth) return false;
            CopyVoxIn(target, e, vx);
            _redo.RemoveAt(_redo.Count - 1); _undo.Add(e);
            Trim(); CheckBytes();
            return true;
        }

        /// <summary>Drops the oldest entries (redo side first, then undo) until the history fits the cap.</summary>
        void Trim()
        {
            while (MemoryBytes > MemoryCap && _redo.Count + _undo.Count > 0)
            {
                var list = _redo.Count > 0 ? _redo : _undo;
                var old = list[0];
                list.RemoveAt(0);
                MemoryBytes -= EntryBytes(old);                  // exactly the entry's full charge (Before + After)
            }
            CheckBytes();
        }

        static void CopyOut(Sprite s, Rectangle r, int[] dst)
        {
            var px = s.Pixels;
            int k = 0;
            for (int y = r.Y; y < r.Bottom; y++) for (int x = r.X; x < r.Right; x++) dst[k++] = px[y * s.Width + x];
        }
        static void CopyIn(Sprite s, Rectangle r, int[] src)
        {
            var px = s.Pixels;
            int k = 0;
            for (int y = r.Y; y < r.Bottom; y++) for (int x = r.X; x < r.Right; x++) px[y * s.Width + x] = src[k++];
        }
        static void CopyVoxIn(VoxelGrid g, Entry e, Voxel[] src)
        {
            int k = 0;
            int x1 = e.Rect.X + e.Rect.Width, y1 = e.Rect.Y + e.Rect.Height;
            for (int z = e.Z0; z < e.Z0 + e.DepthVox; z++)
                for (int y = e.Rect.Y; y < y1; y++)
                    for (int x = e.Rect.X; x < x1; x++) g[x, y, z] = src[k++];
        }

        sealed class Entry
        {
            public Rectangle Rect; public int Z0, DepthVox;      // sprite: Rect only; voxels: Rect.XY = x0/y0, Rect.WH = sizes, Z0..Z0+DepthVox
            public bool IsVoxels; public int W, H, D;            // target dimensions at record time (undo validates)
            public object Before = null!; public object? After;  // int[] (sprite) or Voxel[] (grid); After is captured on the first undo
            public long Bytes => IsVoxels ? ((Voxel[])Before).Length * (long)VoxelBytes : ((int[])Before).Length * 4L;
            public long AfterBytes => IsVoxels ? ((Voxel[])After!).Length * (long)VoxelBytes : ((int[])After!).Length * 4L;

            public static Entry SpriteEntry(Rectangle r, int[] before, int w, int h) => new() { Rect = r, IsVoxels = false, Before = before, W = w, H = h };
            public static Entry VoxelEntry(Rectangle boxXYWH, int z0, int depth, Voxel[] before, int w, int h, int d)
                => new() { Rect = boxXYWH, Z0 = z0, DepthVox = depth, IsVoxels = true, Before = before, W = w, H = h, D = d };
        }
    }
}
