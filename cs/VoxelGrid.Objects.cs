// VoxelGrid.Objects - merging grids and named "objects" inside a grid.
//
// Merging: Merge(other, ox, oy, oz, mode) pastes another grid into this one (clipped), Merged(...) returns a NEW grid big
// enough for both; Stacked(bottom, top) puts one grid on top of the other. Modes: Over (solid cells of the other win),
// Under (only into empty cells), Replace (the other's box replaces ours, empties included), Erase (its solid cells cut
// holes), Intersect (keep only what both have). The bool overload is overwrite / keep-existing.
//
// Objects: a VoxelObject is a name for a set of cells. Two kinds, chosen by cost:
//   * Material-tagged: the object IS the cells whose Voxel.Material equals a tag. Costs nothing (the byte is in the
//     voxel already), survives every edit including Resized / RotatedZ / Mirror / Translate / Merged, up to 255
//     objects per grid, but a cell can belong to one object only. FromObj creates these (ObjOptions.MaterialTag = 1
//     tags each `o` / `g` object 1-based) and DefineObjectsByMaterial() derives them from any grid.
//   * Selection-backed: an explicit VoxelSelection (1 byte per grid cell). Any shape, may overlap other objects,
//     but is only valid for the grid size it was made for - in-place Translate / Mirror move it along, the operations
//     that return a new grid (Resized, RotatedZ, Cropped, Extract) do not carry it over (Merged does).
//   Both kinds: Select(name), Hide/Show(name) (hidden cells are lifted out of the grid into the object and put back by
//   Show), Remove(name) (cells erased, definition kept or dropped), Recolor(name, ...), SetData(name, ...),
//   ObjectAt(x, y, z), Extract(name).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Sr2d64CSport
{
    /// <summary>How <see cref="VoxelGrid.Merge(VoxelGrid,int,int,int,VoxelMerge,string?)"/> combines the other grid's cells with ours.</summary>
    public enum VoxelMerge
    {
        /// <summary>Solid cells of the other grid overwrite ours; its empty cells change nothing.</summary>
        Over,
        /// <summary>Solid cells of the other grid fill only cells that are empty here (existing content wins).</summary>
        Under,
        /// <summary>The other grid's whole box replaces ours, empty cells included.</summary>
        Replace,
        /// <summary>Solid cells of the other grid erase ours (a cutter).</summary>
        Erase,
        /// <summary>Keep our cells only where the other grid is solid too (inside its box; outside its box everything is erased).</summary>
        Intersect,
    }

    /// <summary>A named set of cells in a <see cref="VoxelGrid"/> (see VoxelGrid.Objects.cs for the two kinds).</summary>
    public sealed class VoxelObject : IDisposable
    {
        public string Name;
        /// <summary>Material tag (1..255) when the object is the set of cells with that <see cref="Voxel.Material"/>; 0 = selection-backed.</summary>
        public byte Material;
        /// <summary>The cells when selection-backed (owned by the object; null for material-tagged objects).</summary>
        public VoxelSelection? Cells;
        /// <summary>Hidden objects had their cells lifted out of the grid (kept here) - Show puts them back.</summary>
        public bool Hidden => stash != null;
        public object? Tag;
        internal List<(int index, Voxel v)>? stash; internal int stashW, stashH, stashD;

        public VoxelObject(string name, byte material) { Name = name; Material = material; }
        public VoxelObject(string name, VoxelSelection cells) { Name = name; Cells = cells; }
        public bool IsMaterial => Material != 0;
        public override string ToString() => $"{Name} ({(IsMaterial ? $"material {Material}" : "selection")}{(Hidden ? ", hidden" : "")})";
        public void Dispose() { Cells?.Dispose(); Cells = null; stash = null; }
        internal VoxelObject CloneFor(VoxelGrid g)
        {
            var o = new VoxelObject(Name, Material) { Tag = Tag };
            if (Cells != null) o.Cells = Cells.SameSize(g) ? Cells.Clone() : null;
            if (stash != null) { o.stash = new List<(int, Voxel)>(stash); o.stashW = stashW; o.stashH = stashH; o.stashD = stashD; }
            return o;
        }
    }

    public unsafe partial class VoxelGrid
    {
        // ---- merging --------------------------------------------------------------------------------------------

        /// <summary>
        /// Pastes <paramref name="other"/> into this grid with its origin at (ox, oy, oz), clipped to our box. The other grid's
        /// objects come along (translated); <paramref name="asObject"/> additionally names the pasted cells as one object.
        /// </summary>
        public VoxelGrid Merge(VoxelGrid other, int ox, int oy, int oz, VoxelMerge mode = VoxelMerge.Over, string? asObject = null)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            using var selfCopy = ReferenceEquals(other, this) ? other.Clone() : null;   // merging a grid into itself: read from a snapshot (disposed on exit)
            if (selfCopy != null) other = selfCopy;
            VoxelSelection? mark = asObject != null ? new VoxelSelection(this) : null;
            if (mode == VoxelMerge.Intersect)
            {   // outside the other's box nothing survives
                for (int z = 0; z < d; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int sx = x - ox, sy = y - oy, sz = z - oz;
                    bool inside = (uint)sx < (uint)other.w && (uint)sy < (uint)other.h && (uint)sz < (uint)other.d;
                    if (!inside || other.vox[other.Index(sx, sy, sz)].IsEmpty) vox[Index(x, y, z)] = Voxel.Empty;
                    else if (mark != null) mark.p[Index(x, y, z)] = 1;
                }
            }
            else
            {
                for (int z = 0; z < other.d; z++)
                {
                    int tz = z + oz; if ((uint)tz >= (uint)d) continue;
                    for (int y = 0; y < other.h; y++)
                    {
                        int ty = y + oy; if ((uint)ty >= (uint)h) continue;
                        int x0 = Math.Max(0, -ox), x1 = Math.Min(other.w, w - ox);
                        if (x0 >= x1) continue;
                        Voxel* s = other.vox + other.Index(x0, y, z); Voxel* t = vox + Index(x0 + ox, ty, tz);
                        byte* mk = mark != null ? mark.p + Index(x0 + ox, ty, tz) : null;
                        int n = x1 - x0;
                        switch (mode)
                        {
                            case VoxelMerge.Over: for (int x = 0; x < n; x++) if (s[x].IsSolid) { t[x] = s[x]; if (mk != null) mk[x] = 1; } break;
                            case VoxelMerge.Under: for (int x = 0; x < n; x++) if (s[x].IsSolid && t[x].IsEmpty) { t[x] = s[x]; if (mk != null) mk[x] = 1; } break;
                            case VoxelMerge.Replace: Buffer.MemoryCopy(s, t, n * 8L, n * 8L); if (mk != null) for (int x = 0; x < n; x++) mk[x] = (byte)(s[x].IsSolid ? 1 : 0); break;
                            case VoxelMerge.Erase: for (int x = 0; x < n; x++) if (s[x].IsSolid) t[x] = Voxel.Empty; break;
                        }
                    }
                }
            }
            // carry the other grid's object definitions over (selection-backed ones translated into our box)
            if (mode != VoxelMerge.Erase && mode != VoxelMerge.Intersect)
                foreach (var o in other.Objects)
                {
                    if (o.Hidden) continue;
                    if (o.IsMaterial) { if (FindObject(o.Name) == null) Objects.Add(new VoxelObject(o.Name, o.Material) { Tag = o.Tag }); }
                    else if (o.Cells != null) { var moved = Translated(o.Cells, ox, oy, oz, w, h, d); if (moved.IsEmpty) moved.Dispose(); else Objects.Add(new VoxelObject(UniqueName(o.Name), moved) { Tag = o.Tag }); }
                }
            if (mark != null) { mark.Dirty(); if (mark.IsEmpty) mark.Dispose(); else DefineObject(asObject!, mark); }
            Invalidate(); return this;
        }
        /// <summary>Merge with a bool: <paramref name="overwrite"/> = the other grid's solid cells win (<see cref="VoxelMerge.Over"/>), false = ours stay (<see cref="VoxelMerge.Under"/>).</summary>
        public VoxelGrid Merge(VoxelGrid other, int ox, int oy, int oz, bool overwrite) => Merge(other, ox, oy, oz, overwrite ? VoxelMerge.Over : VoxelMerge.Under);
        /// <summary>Merge at the origin (both grids share (0,0,0)).</summary>
        public VoxelGrid Merge(VoxelGrid other, VoxelMerge mode = VoxelMerge.Over) => Merge(other, 0, 0, 0, mode);

        /// <summary>
        /// A NEW grid large enough for this grid and <paramref name="other"/> placed at (ox, oy, oz) (offsets may be negative:
        /// the result's origin moves so nothing is clipped), then merged with <paramref name="mode"/>. Objects of both come along.
        /// </summary>
        public VoxelGrid Merged(VoxelGrid other, int ox, int oy, int oz, VoxelMerge mode = VoxelMerge.Over, string? asObject = null)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            int x0 = Math.Min(0, ox), y0 = Math.Min(0, oy), z0 = Math.Min(0, oz);
            int x1 = Math.Max(w, ox + other.w), y1 = Math.Max(h, oy + other.h), z1 = Math.Max(d, oz + other.d);
            var g = new VoxelGrid(x1 - x0, y1 - y0, z1 - z0);
            g.Merge(this, -x0, -y0, -z0, VoxelMerge.Replace);
            g.LightReach = LightReach; g.SkyLight = SkyLight; g.SkyFromSides = SkyFromSides;
            return g.Merge(other, ox - x0, oy - y0, oz - z0, mode, asObject);
        }
        public VoxelGrid Merged(VoxelGrid other, int ox, int oy, int oz, bool overwrite) => Merged(other, ox, oy, oz, overwrite ? VoxelMerge.Over : VoxelMerge.Under);

        /// <summary>
        /// A NEW grid with <paramref name="top"/> standing on <paramref name="bottom"/> (top's z = 0 lands on bottom's highest
        /// solid layer + 1, or on bottom's Depth when <paramref name="onSolid"/> is false). <paramref name="centre"/> aligns the
        /// x/y centres, otherwise both start at 0. Both keep their objects (named with <paramref name="bottomName"/> / <paramref name="topName"/> when given).
        /// </summary>
        public static VoxelGrid Stacked(VoxelGrid bottom, VoxelGrid top, bool centre = true, bool onSolid = true, string? bottomName = null, string? topName = null)
        {
            if (bottom == null) throw new ArgumentNullException(nameof(bottom)); if (top == null) throw new ArgumentNullException(nameof(top));
            int zTop = bottom.d;
            if (onSolid) { var b = bottom.SolidBounds(); zTop = b.z1; }
            int ox = centre ? (bottom.w - top.w) / 2 : 0, oy = centre ? (bottom.h - top.h) / 2 : 0;
            var g = bottom.Merged(top, ox, oy, zTop, VoxelMerge.Over, topName);
            if (bottomName != null) { using var solid = bottom.SelectSolid(); g.DefineObject(bottomName, Translated(solid, Math.Max(0, -ox), Math.Max(0, -oy), 0, g.w, g.h, g.d)); }   // bottom's own cells (not the top's, which may sit inside its box)
            return g;
        }

        // ---- objects --------------------------------------------------------------------------------------------

        /// <summary>The named objects of this grid (see VoxelGrid.Objects.cs). Draw order is irrelevant; names are unique, case-insensitive.</summary>
        public List<VoxelObject> Objects { get; } = new List<VoxelObject>();

        /// <summary>Finds an object by name (case-insensitive), or null.</summary>
        public VoxelObject? FindObject(string name) { foreach (var o in Objects) if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) return o; return null; }
        /// <summary>The object names in definition order.</summary>
        public List<string> ObjectNames() { var l = new List<string>(Objects.Count); foreach (var o in Objects) l.Add(o.Name); return l; }
        string UniqueName(string name) { if (FindObject(name) == null) return name; for (int i = 2; ; i++) { string n = $"{name}#{i}"; if (FindObject(n) == null) return n; } }

        /// <summary>Names the cells with <see cref="Voxel.Material"/> == <paramref name="material"/> (1..255) as an object (no memory cost). Replaces an object of the same name.</summary>
        public VoxelObject DefineObject(string name, byte material)
        {
            if (material == 0) throw new ArgumentOutOfRangeException(nameof(material), "material tags are 1..255 (0 = untagged)");
            RemoveObjectDefinition(name);
            var o = new VoxelObject(name, material); Objects.Add(o); return o;
        }
        /// <summary>Names the cells of <paramref name="cells"/> as an object. The grid takes ownership of the selection (do not dispose it; Clone() it first if you keep using it).</summary>
        public VoxelObject DefineObject(string name, VoxelSelection cells)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells)); CheckSel(cells);
            RemoveObjectDefinition(name);
            var o = new VoxelObject(name, cells); Objects.Add(o); return o;
        }
        /// <summary>Names the solid cells inside a box as an object.</summary>
        public VoxelObject DefineObject(string name, int x0, int y0, int z0, int x1, int y1, int z1)
        {
            var s = new VoxelSelection(this).Box(x0, y0, z0, x1, y1, z1); using var solid = SelectSolid(); s.Combine(solid, SelectMode.Intersect);
            return DefineObject(name, s);
        }
        /// <summary>Tags the cells of <paramref name="cells"/> with a free material value (1..255, the lowest unused) and names them - the zero-memory kind of object. Returns null when all 255 tags are taken.</summary>
        public VoxelObject? DefineObjectByTag(string name, VoxelSelection cells)
        {
            CheckSel(cells);
            var used = new bool[256]; foreach (var o in Objects) if (o.IsMaterial) used[o.Material] = true;
            for (long i = 0; i < cells.cells; i++) if (vox[i].IsSolid) used[vox[i].Material] = true;
            int tag = -1; for (int t = 1; t < 256; t++) if (!used[t]) { tag = t; break; }
            if (tag < 0) return null;
            SetData(cells, material: (byte)tag);
            return DefineObject(name, (byte)tag);
        }
        /// <summary>
        /// One material-tagged object per distinct <see cref="Voxel.Material"/> value in use (0 = untagged is skipped).
        /// <paramref name="names"/>: name per tag (index = tag - 1), missing ones become "object N". Returns how many were defined.
        /// </summary>
        public int DefineObjectsByMaterial(IReadOnlyList<string>? names = null, bool keepExisting = false)
        {
            var used = new bool[256];
            for (long i = 0; i < cells; i++) if (vox[i].IsSolid) used[vox[i].Material] = true;
            int n = 0;
            for (int t = 1; t < 256; t++)
            {
                if (!used[t]) continue;
                string name = names != null && t - 1 < names.Count && !string.IsNullOrEmpty(names[t - 1]) ? names[t - 1] : $"object {t}";
                if (keepExisting && FindObject(name) != null) continue;
                DefineObject(UniqueName(name), (byte)t); n++;
            }
            return n;
        }
        /// <summary>Drops the definition only (the cells stay; a hidden object is shown first so nothing is lost).</summary>
        public bool RemoveObjectDefinition(string name)
        {
            var o = FindObject(name); if (o == null) return false;
            if (o.Hidden) Show(name);
            Objects.Remove(o); o.Dispose(); return true;
        }
        /// <summary>Renames an object.</summary>
        public bool RenameObject(string name, string newName) { var o = FindObject(name); if (o == null) return false; if (FindObject(newName) != null && FindObject(newName) != o) throw new ArgumentException("an object with that name exists"); o.Name = newName; return true; }
        /// <summary>Drops every object definition (cells untouched; hidden ones are shown first).</summary>
        public void ClearObjects() { foreach (var o in Objects.ToArray()) RemoveObjectDefinition(o.Name); }

        /// <summary>The object's cells as a selection (solid cells only): a fresh selection, or combined into <paramref name="into"/> with <paramref name="mode"/>. Hidden objects give their stashed cells.</summary>
        public VoxelSelection Select(string name, VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
        {
            var o = FindObject(name) ?? throw new KeyNotFoundException($"no object '{name}'");
            return SelectObject(o, into, mode);
        }
        public VoxelSelection SelectObject(VoxelObject o, VoxelSelection? into = null, SelectMode mode = SelectMode.Replace)
        {
            var s = new VoxelSelection(this);
            if (o.Hidden) { if (o.stashW == w && o.stashH == h && o.stashD == d) foreach (var (i, _) in o.stash!) s.p[i] = 1; }
            else if (o.IsMaterial) { byte t = o.Material; for (long i = 0; i < cells; i++) if (vox[i].IsSolid && vox[i].Material == t) s.p[i] = 1; }
            else if (o.Cells != null && o.Cells.SameSize(this)) { for (long i = 0; i < cells; i++) if (o.Cells.p[i] != 0 && vox[i].IsSolid) s.p[i] = 1; }
            s.Dirty();
            if (into == null) return s;
            CheckSel(into); into.Combine(s, mode); s.Dispose(); return into;
        }
        /// <summary>The object containing cell (x, y, z), or null. Selection-backed objects are checked first (they may overlap a tag).</summary>
        public VoxelObject? ObjectAt(int x, int y, int z)
        {
            if (!Contains(x, y, z)) return null; int i = Index(x, y, z);
            foreach (var o in Objects) if (!o.IsMaterial && !o.Hidden && o.Cells != null && o.Cells.SameSize(this) && o.Cells.p[i] != 0) return o;
            var v = vox[i]; if (v.IsEmpty || v.Material == 0) return null;
            foreach (var o in Objects) if (o.IsMaterial && !o.Hidden && o.Material == v.Material) return o;
            return null;
        }
        /// <summary>Name of the object at a cell, or null.</summary>
        public string? ObjectNameAt(int x, int y, int z) => ObjectAt(x, y, z)?.Name;
        /// <summary>Number of solid cells of the object (0 when unknown).</summary>
        public int ObjectCount(string name) { var o = FindObject(name); if (o == null) return 0; if (o.Hidden) return o.stash!.Count; using var s = SelectObject(o); return s.Count; }
        /// <summary>Bounds of the object's cells (exclusive max; zeros when empty).</summary>
        public (int x0, int y0, int z0, int x1, int y1, int z1) ObjectBounds(string name) { using var s = Select(name); return s.Bounds; }

        /// <summary>Lifts the object's cells out of the grid (they are kept in the object; <see cref="Show"/> restores them). Returns the number of cells hidden.</summary>
        public int Hide(string name)
        {
            var o = FindObject(name) ?? throw new KeyNotFoundException($"no object '{name}'");
            if (o.Hidden) return 0;
            using var s = SelectObject(o);
            var st = new List<(int, Voxel)>(s.Count);
            for (int i = 0; i < cells; i++) if (s.p[i] != 0) { st.Add((i, vox[i])); vox[i] = Voxel.Empty; }
            o.stash = st; o.stashW = w; o.stashH = h; o.stashD = d;
            Invalidate(); return st.Count;
        }
        /// <summary>Puts a hidden object's cells back (over whatever is there now). Returns cells restored, -1 when the grid was resized meanwhile (the stash is dropped).</summary>
        public int Show(string name)
        {
            var o = FindObject(name) ?? throw new KeyNotFoundException($"no object '{name}'");
            if (!o.Hidden) return 0;
            var st = o.stash!; o.stash = null;
            if (o.stashW != w || o.stashH != h || o.stashD != d) return -1;
            foreach (var (i, v) in st) vox[i] = v;
            Invalidate(); return st.Count;
        }
        /// <summary>Hide or show by flag.</summary>
        public int Hidden(string name, bool hidden) => hidden ? Hide(name) : Show(name);
        /// <summary>True when the object exists and is hidden.</summary>
        public bool IsHidden(string name) => FindObject(name)?.Hidden == true;

        /// <summary>Erases the object's cells for good. <paramref name="keepDefinition"/> = the (now empty) name stays, so cells can be added to it again.</summary>
        public int Remove(string name, bool keepDefinition = false)
        {
            var o = FindObject(name) ?? throw new KeyNotFoundException($"no object '{name}'");
            int n;
            if (o.Hidden) { n = o.stash!.Count; o.stash = null; }
            else { using var s = SelectObject(o); n = s.Count; Erase(s); }
            if (!keepDefinition) { Objects.Remove(o); o.Dispose(); }
            return n;
        }
        /// <summary>Copy of the object alone, cropped to its bounds (a new grid; the object is defined in it too).</summary>
        public VoxelGrid Extract(string name)
        {
            var o = FindObject(name) ?? throw new KeyNotFoundException($"no object '{name}'");
            bool wasHidden = o.Hidden; if (wasHidden) Show(name);
            using var s = SelectObject(o);
            var g = s.IsEmpty ? new VoxelGrid(1, 1, 1) : Extract(s);
            if (o.IsMaterial) g.DefineObject(o.Name, o.Material); else if (!s.IsEmpty) { var b = s.Bounds; g.DefineObject(o.Name, Translated(s, -b.x0, -b.y0, -b.z0, g.w, g.h, g.d)); }
            if (wasHidden) Hide(name);
            return g;
        }

        /// <summary>Paints every cell of the object one colour (emit / material / user data untouched).</summary>
        public int Recolor(string name, uint argb) { using var s = Select(name); Paint(s, argb); return s.Count; }
        /// <summary>Replaces one colour by another inside the object (per-channel <paramref name="tolerance"/>). Returns cells changed.</summary>
        public int Recolor(string name, uint from, uint to, int tolerance = 0)
        {
            using var s = Select(name); int n = 0;
            for (int i = 0; i < cells; i++) if (s.p[i] != 0 && Near(vox[i].Argb, from, tolerance)) { vox[i].Argb = (vox[i].Argb & 0xFF000000u) | (to & 0xFFFFFFu); n++; }
            if (n > 0) Invalidate(); return n;
        }
        /// <summary>Maps every colour of the object through <paramref name="map"/> (alpha is kept).</summary>
        public int Recolor(string name, Func<uint, uint> map)
        {
            using var s = Select(name); int n = 0;
            for (int i = 0; i < cells; i++) if (s.p[i] != 0) { uint c = (vox[i].Argb & 0xFF000000u) | (map(vox[i].Argb) & 0xFFFFFFu); if (c != vox[i].Argb) { vox[i].Argb = c; n++; } }
            if (n > 0) Invalidate(); return n;
        }
        /// <summary>Sets emit / material / user data on the object's cells (only the values given). Changing the material of a material-tagged object re-tags it.</summary>
        public int SetData(string name, byte? emit = null, byte? material = null, ushort? user = null)
        {
            var o = FindObject(name) ?? throw new KeyNotFoundException($"no object '{name}'");
            using var s = SelectObject(o); SetData(s, emit, material, user);
            if (material.HasValue && o.IsMaterial && material.Value != 0) o.Material = material.Value;
            return s.Count;
        }
        static bool Near(uint c, uint like, int tol)
        {
            if (tol <= 0) return (c & 0xFFFFFF) == (like & 0xFFFFFF);
            return Math.Abs((int)(c >> 16 & 255) - (int)(like >> 16 & 255)) <= tol && Math.Abs((int)(c >> 8 & 255) - (int)(like >> 8 & 255)) <= tol && Math.Abs((int)(c & 255) - (int)(like & 255)) <= tol;
        }

        // ---- keeping selection-backed objects in step with in-place edits ---------------------------------------
        internal static VoxelSelection Translated(VoxelSelection s, int dx, int dy, int dz, int nw, int nh, int nd)
        {
            var t = new VoxelSelection(nw, nh, nd);
            for (int z = 0; z < s.d; z++) { int tz = z + dz; if ((uint)tz >= (uint)nd) continue; for (int y = 0; y < s.h; y++) { int ty = y + dy; if ((uint)ty >= (uint)nh) continue; for (int x = 0; x < s.w; x++) { int tx = x + dx; if ((uint)tx >= (uint)nw) continue; t.p[t.Index(tx, ty, tz)] = s.p[s.Index(x, y, z)]; } } }
            t.Dirty(); return t;
        }
        void ObjectsTranslate(int dx, int dy, int dz)
        {
            foreach (var o in Objects)
            {
                if (o.Cells != null && o.Cells.SameSize(this)) { var t = Translated(o.Cells, dx, dy, dz, w, h, d); o.Cells.Dispose(); o.Cells = t; }
                if (o.stash != null && o.stashW == w && o.stashH == h && o.stashD == d)
                    for (int k = o.stash.Count - 1; k >= 0; k--) { var (x, y, z) = Coords(o.stash[k].index); x += dx; y += dy; z += dz; if (Contains(x, y, z)) o.stash[k] = (Index(x, y, z), o.stash[k].v); else o.stash.RemoveAt(k); }
            }
        }
        void ObjectsMirror(int axis)
        {
            foreach (var o in Objects)
            {
                if (o.Cells != null && o.Cells.SameSize(this))
                {
                    var t = new VoxelSelection(this);
                    for (int z = 0; z < d; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) t.p[t.Index(x, y, z)] = o.Cells.p[o.Cells.Index(axis == 0 ? w - 1 - x : x, axis == 1 ? h - 1 - y : y, axis == 2 ? d - 1 - z : z)];
                    t.Dirty(); o.Cells.Dispose(); o.Cells = t;
                }
                if (o.stash != null && o.stashW == w && o.stashH == h && o.stashD == d)
                    for (int k = 0; k < o.stash.Count; k++) { var (x, y, z) = Coords(o.stash[k].index); o.stash[k] = (Index(axis == 0 ? w - 1 - x : x, axis == 1 ? h - 1 - y : y, axis == 2 ? d - 1 - z : z), o.stash[k].v); }
            }
        }
        void ObjectsCloneTo(VoxelGrid g) { foreach (var o in Objects) g.Objects.Add(o.CloneFor(g)); }
        void ObjectsDispose() { foreach (var o in Objects) o.Dispose(); Objects.Clear(); }
    }
}
