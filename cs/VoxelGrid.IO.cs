// VoxelGrid file I/O - no third-party code:
//
//   MagicaVoxel .vox   VoxelGrid.LoadVox(path) / LoadVoxAll(path) / SaveVox(path)
//                      RIFF-style binary: 'VOX ' + version, MAIN { PACK? (SIZE XYZI)* RGBA? nTRN/nGRP/nSHP* MATL* }.
//                      Every model is read; the scene graph (nTRN translations / rotations, MagicaVoxel 0.99+)
//                      is applied when present so multi-part files land where the editor shows them. The
//                      MATL chunk gives emitters: materials of type _emit set Voxel.Emit from their emission /
//                      flux; the palette index goes into Voxel.Material. Axes match ours (x, y, z up).
//
//   Wavefront .obj     VoxelGrid.FromObj(path or text, resolution, options)
//                      Text parser: v / vn / vt / f (any polygon, negative indices, 'v/vt/vn' forms), o / g,
//                      usemtl + mtllib -> Kd colours (map_Kd is ignored, no image decoding here). Triangles are
//                      rasterised conservatively into the grid (surface voxels); then, per object, if the mesh is
//                      closed (every edge shared by exactly two triangles) the interior is filled by parity /
//                      cavity flood, otherwise (planes, open shells, meshes with holes) it stays a shell.
//                      ObjFill.Auto / Solid / Shell override the decision. Vertex colours (the 'v x y z r g b'
//                      extension) are used when there is no material.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Sr2d64CSport
{
    /// <summary>How <see cref="VoxelGrid.FromObj(string, int, ObjOptions?)"/> fills each object.</summary>
    public enum ObjFill
    {
        /// <summary>Closed (watertight) objects become solids, open ones (planes, shells with holes) stay surfaces.</summary>
        Auto,
        /// <summary>Always fill the interior (cavities enclosed by the surface voxels), even if the mesh is not watertight.</summary>
        Solid,
        /// <summary>Surface voxels only.</summary>
        Shell,
    }

    /// <summary>Options for the .obj voxeliser.</summary>
    public sealed class ObjOptions
    {
        public ObjFill Fill = ObjFill.Auto;
        /// <summary>Colour used when the file has no material / vertex colour.</summary>
        public uint DefaultColor = 0xFFC0C0C0;
        /// <summary>Keep the model's aspect ratio (resolution applies to the longest side). False = stretch to resolution^3.</summary>
        public bool KeepAspect = true;
        /// <summary>Which model axis is "up" in the file (most .obj exporters: Y). It becomes our z.</summary>
        public ObjUp Up = ObjUp.Y;
        /// <summary>Material byte per object: 0 = none, 1 = object index (1-based), 2 = material index.</summary>
        public int MaterialTag = 1;
        /// <summary>Define a named <see cref="VoxelObject"/> per `o` / `g` object (MaterialTag 1) or per material (MaterialTag 2) in the grid's Objects list.</summary>
        public bool Objects = true;
        /// <summary>Extra empty cells around the model.</summary>
        public int Margin = 0;
        /// <summary>Emit strength for materials whose Ke (emissive) is bright, 0 = ignore Ke.</summary>
        public byte EmissiveStrength = 12;
        /// <summary>Optional: mtl file text by name (when the .obj is given as text and cannot resolve mtllib from disk).</summary>
        public Func<string, string?>? ResolveMtl = null;
    }
    public enum ObjUp { Y, Z }

    public sealed unsafe partial class VoxelGrid
    {
        // =============================================================== MagicaVoxel .vox
        static readonly uint[] VoxDefaultPalette = BuildDefaultPalette();
        static uint[] BuildDefaultPalette()
        {
            // the standard MagicaVoxel default palette (index 0 unused), generated: 6x6x6 colour cube descending + greys
            var p = new uint[256];
            uint[] lv = { 0xff, 0xcc, 0x99, 0x66, 0x33, 0x00 };
            int i = 1;
            for (int b = 0; b < 6; b++) for (int g = 0; g < 6; g++) for (int r = 0; r < 6; r++)
            {
                if (r == 5 && g == 5 && b == 5) continue;                 // black is index 0 territory
                p[i++] = 0xFF000000u | (lv[r] << 16) | (lv[g] << 8) | lv[b];
            }
            uint[] ramp = { 0xee, 0xdd, 0xbb, 0xaa, 0x88, 0x77, 0x55, 0x44, 0x22, 0x11 };
            foreach (var v in ramp) p[i++] = 0xFF000000u | v;             // blues
            foreach (var v in ramp) p[i++] = 0xFF000000u | (v << 8);      // greens
            foreach (var v in ramp) p[i++] = 0xFF000000u | (v << 16);     // reds
            foreach (var v in ramp) p[i++] = 0xFF000000u | (v << 16) | (v << 8) | v;   // greys
            return p;
        }

        /// <summary>One model of a .vox file with its placement in the scene.</summary>
        public sealed class VoxModel { public VoxelGrid Grid = null!; public int X, Y, Z; public string Name = ""; }

        /// <summary>
        /// Load a MagicaVoxel file as ONE grid: all models are placed by the file's scene graph and merged
        /// (the grid is the union's bounding box). Colours from the palette, palette index in Material, emissive materials in Emit.
        /// </summary>
        public static VoxelGrid LoadVox(string path) => LoadVox(File.ReadAllBytes(path));
        public static VoxelGrid LoadVox(byte[] data)
        {
            var models = LoadVoxAll(data);
            if (models.Count == 0) throw new InvalidDataException("no model in .vox file");
            if (models.Count == 1 && models[0].X == 0 && models[0].Y == 0 && models[0].Z == 0) return models[0].Grid;
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var m in models) { x0 = Math.Min(x0, m.X); y0 = Math.Min(y0, m.Y); z0 = Math.Min(z0, m.Z); x1 = Math.Max(x1, m.X + m.Grid.Width); y1 = Math.Max(y1, m.Y + m.Grid.Height); z1 = Math.Max(z1, m.Z + m.Grid.Depth); }
            var g = new VoxelGrid(x1 - x0, y1 - y0, z1 - z0);
            int k = 0;
            foreach (var m in models) { k++; g.Merge(m.Grid, m.X - x0, m.Y - y0, m.Z - z0, VoxelMerge.Over, string.IsNullOrEmpty(m.Name) ? $"model {k}" : m.Name); m.Grid.Dispose(); }
            return g;
        }

        /// <summary>All models of a .vox file, each as its own grid with its scene position (bottom-left-back corner).</summary>
        public static List<VoxModel> LoadVoxAll(byte[] data)
        {
            if (data.Length < 20 || data[0] != 'V' || data[1] != 'O' || data[2] != 'X' || data[3] != ' ') throw new InvalidDataException("not a MagicaVoxel .vox file");
            var sizes = new List<(int, int, int)>(); var voxels = new List<byte[]>();
            uint[] palette = (uint[])VoxDefaultPalette.Clone();
            var emit = new byte[256];
            var trn = new Dictionary<int, (int child, int x, int y, int z, int rot, string name)>();
            var grp = new Dictionary<int, int[]>();
            var shp = new Dictionary<int, int[]>();
            int pos = 8;
            void Chunk(int end)
            {
                while (pos + 12 <= end)
                {
                    string id = Encoding.ASCII.GetString(data, pos, 4); int n = BitConverter.ToInt32(data, pos + 4), m = BitConverter.ToInt32(data, pos + 8);
                    int body = pos + 12, next = body + n + m;
                    if (n < 0 || m < 0 || next > data.Length) throw new InvalidDataException("corrupt .vox chunk " + id);
                    int q = body;
                    switch (id)
                    {
                        case "MAIN": pos = body + n; Chunk(next); pos = next; continue;
                        case "SIZE": sizes.Add((BitConverter.ToInt32(data, q), BitConverter.ToInt32(data, q + 4), BitConverter.ToInt32(data, q + 8))); break;
                        case "XYZI":
                        {   // the count is file data: it must fit the chunk that carries it (a hostile count used to allocate cnt*4 bytes unchecked)
                            int cnt = BitConverter.ToInt32(data, q);
                            if (cnt < 0 || (long)cnt * 4 > n - 4) throw new InvalidDataException($"corrupt .vox XYZI: {cnt} voxels do not fit a {n}-byte chunk");
                            var v = new byte[cnt * 4]; Array.Copy(data, q + 4, v, 0, Math.Min(v.Length, n - 4)); voxels.Add(v); break;
                        }
                        case "RGBA": for (int i = 0; i < 255 && q + i * 4 + 3 < body + n; i++) { byte r = data[q + i * 4], gg = data[q + i * 4 + 1], b = data[q + i * 4 + 2], a = data[q + i * 4 + 3]; palette[i + 1] = (uint)a << 24 | (uint)r << 16 | (uint)gg << 8 | b; } break;
                        case "MATL":
                            {
                                int mid = BitConverter.ToInt32(data, q); q += 4; var dict = ReadDict(data, ref q);
                                if (mid >= 0 && mid < 256 && dict.TryGetValue("_type", out var ty) && ty == "_emit")
                                {
                                    float e = dict.TryGetValue("_emit", out var es) && float.TryParse(es, NumberStyles.Float, CultureInfo.InvariantCulture, out var ev) ? ev : 1f;
                                    float flux = dict.TryGetValue("_flux", out var fs) && float.TryParse(fs, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv) ? fv : 0f;
                                    emit[mid] = (byte)Math.Clamp((int)MathF.Round(e * 15f * (1f + flux * 0.5f)), 1, 15);
                                }
                                break;
                            }
                        case "nTRN":
                            {
                                int nid = BitConverter.ToInt32(data, q); q += 4; var attr = ReadDict(data, ref q);
                                int child = BitConverter.ToInt32(data, q); q += 4; q += 4; /* reserved */ q += 4; /* layer */ int nf = BitConverter.ToInt32(data, q); q += 4;
                                if (nf < 0 || q + 4L * nf > data.Length) throw new InvalidDataException($"corrupt .vox nTRN: {nf} frames do not fit the chunk");
                                int tx = 0, ty2 = 0, tz = 0, rot = 4;   // rot byte 4 = identity
                                for (int f = 0; f < nf; f++)
                                {
                                    var fr = ReadDict(data, ref q);
                                    if (f == 0)
                                    {
                                        if (fr.TryGetValue("_t", out var t)) { var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (parts.Length == 3) { _ = int.TryParse(parts[0], out tx); _ = int.TryParse(parts[1], out ty2); _ = int.TryParse(parts[2], out tz); } }
                                        if (fr.TryGetValue("_r", out var r) && int.TryParse(r, out var rv)) rot = rv;
                                    }
                                }
                                trn[nid] = (child, tx, ty2, tz, rot, attr.TryGetValue("_name", out var nm) ? nm : "");
                                break;
                            }
                        case "nGRP": { int nid = BitConverter.ToInt32(data, q); q += 4; ReadDict(data, ref q); int nc = BitConverter.ToInt32(data, q); q += 4; if (nc < 0 || q + 4L * nc > data.Length) throw new InvalidDataException($"corrupt .vox nGRP: {nc} children do not fit the chunk"); var ch = new int[nc]; for (int i = 0; i < nc; i++) { ch[i] = BitConverter.ToInt32(data, q); q += 4; } grp[nid] = ch; break; }
                        case "nSHP": { int nid = BitConverter.ToInt32(data, q); q += 4; ReadDict(data, ref q); int nm2 = BitConverter.ToInt32(data, q); q += 4; if (nm2 < 0 || q + 4L * nm2 > data.Length) throw new InvalidDataException($"corrupt .vox nSHP: {nm2} models do not fit the chunk"); var ms = new int[nm2]; for (int i = 0; i < nm2; i++) { ms[i] = BitConverter.ToInt32(data, q); q += 4; ReadDict(data, ref q); } shp[nid] = ms; break; }
                        default: break;   // PACK, LAYR, rOBJ, rCAM, NOTE, IMAP ... not needed
                    }
                    pos = next;
                }
            }
            Chunk(data.Length);

            var result = new List<VoxModel>();
            VoxelGrid Build(int mi, int rot, out int cx, out int cy, out int cz)
            {
                var (sx, sy, sz) = sizes[mi];
                // rotation: apply the 3x3 signed permutation from the _r byte to the model's cells and size
                int[,] R = RotMatrix(rot);
                int[] dim = { sx, sy, sz }; int[] ndim = new int[3];
                for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) if (R[r, c] != 0) ndim[r] = dim[c];
                var g = new VoxelGrid(Math.Max(1, ndim[0]), Math.Max(1, ndim[1]), Math.Max(1, ndim[2]));
                var v = voxels[mi];
                for (int i = 0; i + 3 < v.Length; i += 4)
                {
                    int[] pIn = { v[i], v[i + 1], v[i + 2] }; int[] pOut = new int[3];
                    for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) if (R[r, c] != 0) pOut[r] = R[r, c] > 0 ? pIn[c] : dim[c] - 1 - pIn[c];
                    int ci = v[i + 3];
                    if (g.Contains(pOut[0], pOut[1], pOut[2])) g.vox[g.Index(pOut[0], pOut[1], pOut[2])] = new Voxel(palette[ci] | 0xFF000000u, emit[ci], (byte)ci);
                }
                g.Invalidate();
                // MagicaVoxel translations refer to the model centre (floor of size/2)
                cx = ndim[0] / 2; cy = ndim[1] / 2; cz = ndim[2] / 2;
                return g;
            }
            if (trn.Count > 0 && trn.ContainsKey(0))
            {
                // walk the scene graph: root is transform node 0
                var stack = new Stack<(int node, int x, int y, int z, string name)>(); stack.Push((0, 0, 0, 0, ""));
                var seen = new HashSet<int>();
                while (stack.Count > 0)
                {
                    var (node, x, y, z, name) = stack.Pop();
                    if (!seen.Add(node)) continue;
                    if (trn.TryGetValue(node, out var t)) { stack.Push((t.child, x + t.x, y + t.y, z + t.z, t.name.Length > 0 ? t.name : name)); RotForChild[t.child] = t.rot; }
                    else if (grp.TryGetValue(node, out var ch)) { foreach (var c in ch) stack.Push((c, x, y, z, name)); }
                    else if (shp.TryGetValue(node, out var ms))
                    {
                        int rot = RotForChild.TryGetValue(node, out var rr) ? rr : 4;
                        foreach (var mi in ms) if (mi >= 0 && mi < sizes.Count && mi < voxels.Count)
                        {
                            var g = Build(mi, rot, out int cx, out int cy, out int cz);
                            result.Add(new VoxModel { Grid = g, X = x - cx, Y = y - cy, Z = z - cz, Name = name });
                        }
                    }
                }
                RotForChild.Clear();
            }
            if (result.Count == 0)
                for (int mi = 0; mi < Math.Min(sizes.Count, voxels.Count); mi++) result.Add(new VoxModel { Grid = Build(mi, 4, out _, out _, out _), Name = "model" + mi });
            return result;
        }
        [ThreadStatic] static Dictionary<int, int>? rotForChild;
        static Dictionary<int, int> RotForChild => rotForChild ??= new Dictionary<int, int>();

        static int[,] RotMatrix(int r)
        {
            // MagicaVoxel _r: bits 0-1 index of the non-zero entry in row 0, bits 2-3 in row 1, bits 4-6 signs of rows 0..2
            int i0 = r & 3, i1 = (r >> 2) & 3, i2 = 3 - i0 - i1;
            if (i0 == i1 || i0 > 2 || i1 > 2 || i2 < 0 || i2 > 2) { i0 = 0; i1 = 1; i2 = 2; r = 4; }
            var M = new int[3, 3];
            M[0, i0] = (r & 16) != 0 ? -1 : 1; M[1, i1] = (r & 32) != 0 ? -1 : 1; M[2, i2] = (r & 64) != 0 ? -1 : 1;
            return M;
        }
        static Dictionary<string, string> ReadDict(byte[] d, ref int q)
        {
            var dict = new Dictionary<string, string>();
            if (q + 4 > d.Length) throw new InvalidDataException("corrupt .vox: truncated dictionary");
            int n = BitConverter.ToInt32(d, q); q += 4;
            if (n < 0) throw new InvalidDataException("corrupt .vox: negative dictionary length");
            for (int i = 0; i < n; i++)
            {   // the length read IS the length consumed: a clamped decode with a raw advance could move the
                // cursor backwards (an endless loop) or past the buffer (an exception out of the parser)
                if (q + 4 > d.Length) throw new InvalidDataException("corrupt .vox: truncated dictionary");
                int kl = BitConverter.ToInt32(d, q); q += 4;
                if (kl < 0 || q + kl > d.Length) throw new InvalidDataException("corrupt .vox: bad dictionary key length");
                string k = Encoding.UTF8.GetString(d, q, kl); q += kl;
                if (q + 4 > d.Length) throw new InvalidDataException("corrupt .vox: truncated dictionary");
                int vl = BitConverter.ToInt32(d, q); q += 4;
                if (vl < 0 || q + vl > d.Length) throw new InvalidDataException("corrupt .vox: bad dictionary value length");
                string v = Encoding.UTF8.GetString(d, q, vl); q += vl;
                dict[k] = v;
            }
            return dict;
        }

        /// <summary>
        /// Save as MagicaVoxel .vox (one model, up to 256 per side - larger grids are split into a grid of models with a
        /// scene graph so MagicaVoxel opens them in place). Colours are quantised to a 255-entry palette (exact when the
        /// grid uses at most 255 colours); emitters get an _emit material.
        /// </summary>
        public void SaveVox(string path) => File.WriteAllBytes(path, ToVox());
        public byte[] ToVox()
        {
            // palette: distinct colours in first-seen order, then median-cut-free fallback: nearest of the used set (cap 255)
            var index = new Dictionary<uint, byte>(); var pal = new List<uint>(); var emitOf = new Dictionary<byte, byte>();
            var solids = new List<(int x, int y, int z, byte ci)>();
            for (int z = 0; z < d; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                var v = vox[Index(x, y, z)]; if (v.IsEmpty) continue;
                uint ck = v.Argb | 0xFF000000u;
                if (!index.TryGetValue(ck, out var ci))
                {
                    if (pal.Count < 255) { pal.Add(ck); ci = (byte)pal.Count; index[ck] = ci; }
                    else ci = Nearest(pal, ck);
                }
                if (v.Emit > 0 && !emitOf.ContainsKey(ci)) emitOf[ci] = v.Emit;
                solids.Add((x, y, z, ci));
            }
            var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
            void Str(string s) => bw.Write(Encoding.ASCII.GetBytes(s));
            // models: split into <= 256 chunks
            int nx = (w + 255) / 256, ny = (h + 255) / 256, nz = (d + 255) / 256;
            var parts = new List<(int ox, int oy, int oz, int sx, int sy, int sz, List<(int, int, int, byte)> v)>();
            for (int kz = 0; kz < nz; kz++) for (int ky = 0; ky < ny; ky++) for (int kx = 0; kx < nx; kx++)
                parts.Add((kx * 256, ky * 256, kz * 256, Math.Min(256, w - kx * 256), Math.Min(256, h - ky * 256), Math.Min(256, d - kz * 256), new List<(int, int, int, byte)>()));
            foreach (var s in solids) { int pi = (s.z / 256 * ny + s.y / 256) * nx + s.x / 256; parts[pi].v.Add((s.x - parts[pi].ox, s.y - parts[pi].oy, s.z - parts[pi].oz, s.ci)); }
            var body = new MemoryStream(); var b = new BinaryWriter(body);
            void BStr(string s) => b.Write(Encoding.ASCII.GetBytes(s));
            if (parts.Count > 1) { BStr("PACK"); b.Write(4); b.Write(0); b.Write(parts.Count); }
            foreach (var p in parts)
            {
                BStr("SIZE"); b.Write(12); b.Write(0); b.Write(p.sx); b.Write(p.sy); b.Write(p.sz);
                BStr("XYZI"); b.Write(4 + p.v.Count * 4); b.Write(0); b.Write(p.v.Count);
                foreach (var (x, y, z, ci) in p.v) { b.Write((byte)x); b.Write((byte)y); b.Write((byte)z); b.Write(ci); }
            }
            if (parts.Count > 1)
            {
                // scene graph: nTRN 0 -> nGRP 1 -> (nTRN, nSHP) per part
                int nid = 0;
                void TrnChunk(int id, int child, int tx, int ty, int tz)
                {
                    var t = new MemoryStream(); var tw = new BinaryWriter(t);
                    tw.Write(id); WDict(tw); tw.Write(child); tw.Write(-1); tw.Write(0); tw.Write(1); WDict(tw, ("_t", $"{tx} {ty} {tz}"));
                    BStr("nTRN"); b.Write((int)t.Length); b.Write(0); b.Write(t.ToArray());
                }
                void WDict(BinaryWriter tw, params (string, string)[] kv) { tw.Write(kv.Length); foreach (var (k, v) in kv) { var kb = Encoding.UTF8.GetBytes(k); tw.Write(kb.Length); tw.Write(kb); var vb = Encoding.UTF8.GetBytes(v); tw.Write(vb.Length); tw.Write(vb); } }
                TrnChunk(nid++, 1, 0, 0, 0);
                { var t = new MemoryStream(); var tw = new BinaryWriter(t); tw.Write(nid++); WDict(tw); tw.Write(parts.Count); for (int i = 0; i < parts.Count; i++) tw.Write(2 + i * 2); BStr("nGRP"); b.Write((int)t.Length); b.Write(0); b.Write(t.ToArray()); }
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    TrnChunk(nid++, nid, p.ox + p.sx / 2, p.oy + p.sy / 2, p.oz + p.sz / 2);
                    var t = new MemoryStream(); var tw = new BinaryWriter(t); tw.Write(nid++); WDict(tw); tw.Write(1); tw.Write(i); WDict(tw);
                    BStr("nSHP"); b.Write((int)t.Length); b.Write(0); b.Write(t.ToArray());
                }
            }
            BStr("RGBA"); b.Write(1024); b.Write(0);
            for (int i = 0; i < 256; i++) { uint c = i < pal.Count ? pal[i] : 0xFF000000u; b.Write((byte)(c >> 16)); b.Write((byte)(c >> 8)); b.Write((byte)c); b.Write((byte)255); }
            foreach (var kv in emitOf)
            {
                var t = new MemoryStream(); var tw = new BinaryWriter(t); tw.Write((int)kv.Key);
                tw.Write(3); foreach (var (k, v) in new[] { ("_type", "_emit"), ("_emit", (kv.Value / 15f).ToString("0.###", CultureInfo.InvariantCulture)), ("_flux", "0") }) { var kb = Encoding.UTF8.GetBytes(k); tw.Write(kb.Length); tw.Write(kb); var vb = Encoding.UTF8.GetBytes(v); tw.Write(vb.Length); tw.Write(vb); }
                BStr("MATL"); b.Write((int)t.Length); b.Write(0); b.Write(t.ToArray());
            }
            Str("VOX "); bw.Write(150); Str("MAIN"); bw.Write(0); bw.Write((int)body.Length); bw.Write(body.ToArray());
            return ms.ToArray();
        }
        static byte Nearest(List<uint> pal, uint c)
        {
            int best = 0, bd = int.MaxValue; int r = (int)(c >> 16) & 255, g = (int)(c >> 8) & 255, b = (int)c & 255;
            for (int i = 0; i < pal.Count; i++) { int dr = r - ((int)(pal[i] >> 16) & 255), dg = g - ((int)(pal[i] >> 8) & 255), db = b - ((int)pal[i] & 255); int dd = dr * dr + dg * dg + db * db; if (dd < bd) { bd = dd; best = i; } }
            return (byte)(best + 1);
        }

        // =============================================================== Wavefront .obj
        sealed class ObjMesh
        {
            public List<float[]> V = new List<float[]>(); public List<uint?> VC = new List<uint?>();
            public List<(int a, int b, int c, int obj, int mat)> Tris = new List<(int, int, int, int, int)>();
            public List<string> Objects = new List<string>(); public List<string> MatNames = new List<string>();
            public Dictionary<string, (uint kd, float ke)> Materials = new Dictionary<string, (uint, float)>();
        }

        /// <summary>Voxelise a Wavefront .obj (file path, or the file's text) into a grid whose longest side is <paramref name="resolution"/>.</summary>
        public static VoxelGrid FromObj(string pathOrText, int resolution = 64, ObjOptions? opt = null)
        {
            opt ??= new ObjOptions();
            string text; string? dir = null;
            if (pathOrText.IndexOf('\n') < 0 && File.Exists(pathOrText)) { text = File.ReadAllText(pathOrText); dir = Path.GetDirectoryName(Path.GetFullPath(pathOrText)); }
            else text = pathOrText;
            var mesh = ParseObj(text, name => { if (opt.ResolveMtl != null) { var t = opt.ResolveMtl(name); if (t != null) return t; } if (dir != null) { var p = Path.Combine(dir, name); if (File.Exists(p)) return File.ReadAllText(p); } return null; });
            return Voxelise(mesh, resolution, opt);
        }

        static ObjMesh ParseObj(string text, Func<string, string?> loadMtl)
        {
            var m = new ObjMesh(); int curObj = -1, curMat = -1; var inv = CultureInfo.InvariantCulture;
            m.Objects.Add("default"); curObj = 0;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    case "v":
                        if (p.Length >= 4) { m.V.Add(new[] { F(p[1]), F(p[2]), F(p[3]) }); m.VC.Add(p.Length >= 7 ? (uint?)(0xFF000000u | (uint)(Math.Clamp(F(p[4]), 0, 1) * 255) << 16 | (uint)(Math.Clamp(F(p[5]), 0, 1) * 255) << 8 | (uint)(Math.Clamp(F(p[6]), 0, 1) * 255)) : null); }
                        break;
                    case "o": case "g": { string name = p.Length > 1 ? string.Join(' ', p, 1, p.Length - 1) : "group"; int i = m.Objects.IndexOf(name); if (i < 0) { m.Objects.Add(name); i = m.Objects.Count - 1; } curObj = i; break; }
                    case "usemtl": { string name = p.Length > 1 ? p[1] : ""; int i = m.MatNames.IndexOf(name); if (i < 0) { m.MatNames.Add(name); i = m.MatNames.Count - 1; } curMat = i; break; }
                    case "mtllib":
                        for (int k = 1; k < p.Length; k++) { var t = loadMtl(p[k]); if (t != null) ParseMtl(t, m); }
                        break;
                    case "f":
                        {
                            var idx = new List<int>();
                            for (int k = 1; k < p.Length; k++)
                            {
                                var s = p[k]; int slash = s.IndexOf('/'); if (slash >= 0) s = s.Substring(0, slash);
                                if (!int.TryParse(s, NumberStyles.Integer, inv, out int vi)) continue;
                                vi = vi < 0 ? m.V.Count + vi : vi - 1;
                                if (vi >= 0 && vi < m.V.Count) idx.Add(vi);
                            }
                            for (int k = 1; k + 1 < idx.Count; k++) m.Tris.Add((idx[0], idx[k], idx[k + 1], curObj, curMat));   // fan
                            break;
                        }
                }
            }
            return m;
            float F(string s) => float.TryParse(s, NumberStyles.Float, inv, out var f) ? f : 0f;
        }
        static void ParseMtl(string text, ObjMesh m)
        {
            string? cur = null; uint kd = 0xFFC0C0C0; float ke = 0; var inv = CultureInfo.InvariantCulture;
            void Flush() { if (cur != null) m.Materials[cur] = (kd, ke); }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p[0] == "newmtl") { Flush(); cur = p.Length > 1 ? p[1] : ""; kd = 0xFFC0C0C0; ke = 0; }
                else if (p[0] == "Kd" && p.Length >= 4) kd = 0xFF000000u | (uint)(Math.Clamp(Fl(p[1]), 0, 1) * 255) << 16 | (uint)(Math.Clamp(Fl(p[2]), 0, 1) * 255) << 8 | (uint)(Math.Clamp(Fl(p[3]), 0, 1) * 255);
                else if (p[0] == "Ke" && p.Length >= 4) ke = MathF.Max(Fl(p[1]), MathF.Max(Fl(p[2]), Fl(p[3])));
            }
            Flush();
            float Fl(string s) => float.TryParse(s, NumberStyles.Float, inv, out var f) ? f : 0f;
        }

        static VoxelGrid Voxelise(ObjMesh m, int res, ObjOptions opt)
        {
            if (m.V.Count == 0 || m.Tris.Count == 0) return new VoxelGrid(1, 1, 1);
            // model space -> our axes: (x, y, z) with Up = Y becomes (x, -z, y) [z up, y north]
            float[] Map(float[] v) => opt.Up == ObjUp.Y ? new[] { v[0], -v[2], v[1] } : new[] { v[0], v[1], v[2] };
            var P = new float[m.V.Count][]; for (int i = 0; i < P.Length; i++) P[i] = Map(m.V[i]);
            float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
            foreach (var v in P) for (int k = 0; k < 3; k++) { lo[k] = MathF.Min(lo[k], v[k]); hi[k] = MathF.Max(hi[k], v[k]); }
            float[] ext = { hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2] };
            float longest = MathF.Max(ext[0], MathF.Max(ext[1], ext[2])); if (longest <= 0) longest = 1;
            int mg = Math.Max(0, opt.Margin); res = Math.Max(1, res);
            float[] scale = new float[3]; int[] dim = new int[3];
            for (int k = 0; k < 3; k++)
            {
                float s = opt.KeepAspect ? res / longest : res / MathF.Max(ext[k], 1e-6f);
                dim[k] = Math.Max(1, (int)MathF.Ceiling(ext[k] * s - 1e-4f)) + 2 * mg; scale[k] = s;
            }
            var g = new VoxelGrid(dim[0], dim[1], dim[2]);
            // per object: rasterise triangles into a temporary selection, decide closedness, fill, then stamp colours
            var byObj = new Dictionary<int, List<int>>();
            for (int t = 0; t < m.Tris.Count; t++) { int o = m.Tris[t].obj; if (!byObj.TryGetValue(o, out var l)) byObj[o] = l = new List<int>(); l.Add(t); }
            var owner = new int[g.Count]; Array.Fill(owner, -1);   // triangle that painted a surface cell (for colour)
            foreach (var kv in byObj)
            {
                using var surf = new VoxelSelection(g);
                foreach (var t in kv.Value) RasterTri(g, surf, P, m.Tris[t], scale, lo, mg, owner, t);
                bool fill = opt.Fill == ObjFill.Solid || (opt.Fill == ObjFill.Auto && IsClosed(m, kv.Value));
                if (fill)
                {
                    // interior = cells not reachable from the outside without crossing the surface
                    using var tmpGrid = new VoxelGrid(g.Width, g.Height, g.Depth);
                    tmpGrid.Fill(surf, new Voxel(0xFF000000));
                    using var cav = tmpGrid.SelectCavities();
                    surf.Combine(cav, SelectMode.Add);
                }
                // colours: surface cells take the triangle that painted them (per-face material / vertex colour),
                // interior cells the object's first material
                int firstTri = kv.Value[0];
                byte* sp = surf.p;
                for (int i = 0; i < g.Count; i++) if (sp[i] != 0)
                {
                    int tri = owner[i] >= 0 ? owner[i] : firstTri;
                    var (c, em) = ObjColor(m, tri, opt);
                    byte mt = (byte)(opt.MaterialTag == 1 ? Math.Min(255, kv.Key + 1) : opt.MaterialTag == 2 ? Math.Min(255, m.Tris[tri].mat + 1) : 0);
                    if (g.vox[i].IsEmpty || owner[i] >= 0) g.vox[i] = new Voxel(c, em, mt);
                }
                Array.Fill(owner, -1);
            }
            if (opt.Objects && opt.MaterialTag != 0) g.DefineObjectsByMaterial(opt.MaterialTag == 1 ? m.Objects : m.MatNames);
            g.Invalidate();
            return g;
        }
        static (uint colour, byte emit) ObjColor(ObjMesh m, int tri, ObjOptions opt)
        {
            var t = m.Tris[tri];
            if (t.mat >= 0 && t.mat < m.MatNames.Count && m.Materials.TryGetValue(m.MatNames[t.mat], out var mm))
                return (mm.kd, mm.ke > 0.5f && opt.EmissiveStrength > 0 ? opt.EmissiveStrength : (byte)0);
            var vc = m.VC[t.a] ?? m.VC[t.b] ?? m.VC[t.c];
            return (vc ?? opt.DefaultColor, 0);
        }
        // closed = every undirected edge is used by exactly two triangles
        // closed = every undirected edge is shared by exactly two triangles. Vertices are welded by position first so
        // meshes with duplicated seam / pole vertices (UV spheres, exported per-face vertices) still count as closed.
        static bool IsClosed(ObjMesh m, List<int> tris)
        {
            float ext = 0; foreach (var v in m.V) ext = MathF.Max(ext, MathF.Max(MathF.Abs(v[0]), MathF.Max(MathF.Abs(v[1]), MathF.Abs(v[2]))));
            float q = 1e5f / MathF.Max(ext, 1e-20f);   // weld tolerance: 1e-5 of the model's extent (pole vertices of a UV sphere differ by ~1e-16)
            var canon = new Dictionary<(long, long, long), int>(); var map = new int[m.V.Count];
            for (int i = 0; i < m.V.Count; i++) { var k = ((long)MathF.Round(m.V[i][0] * q), (long)MathF.Round(m.V[i][1] * q), (long)MathF.Round(m.V[i][2] * q)); if (!canon.TryGetValue(k, out int c)) canon[k] = c = i; map[i] = c; }
            var edges = new Dictionary<long, int>();
            void E(int a, int b) { long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a; edges[k] = edges.TryGetValue(k, out var n) ? n + 1 : 1; }
            foreach (var ti in tris)
            {
                var t = m.Tris[ti]; int a = map[t.a], b = map[t.b], c = map[t.c];
                if (a == b || b == c || a == c) continue;   // degenerate (pole fans)
                E(a, b); E(b, c); E(c, a);
            }
            if (edges.Count == 0) return false;
            foreach (var n in edges.Values) if (n != 2) return false;
            return true;
        }
        // conservative triangle voxelisation: every cell whose box the triangle touches (separating-axis test)
        static void RasterTri(VoxelGrid g, VoxelSelection surf, float[][] P, (int a, int b, int c, int obj, int mat) t, float[] scale, float[] lo, int mg, int[] owner, int ti)
        {
            float[] A = ToGrid(g, P[t.a], scale, lo, mg), B = ToGrid(g, P[t.b], scale, lo, mg), C = ToGrid(g, P[t.c], scale, lo, mg);
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(A[0], MathF.Min(B[0], C[0])))), x1 = Math.Min(g.Width - 1, (int)MathF.Floor(MathF.Max(A[0], MathF.Max(B[0], C[0]))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(A[1], MathF.Min(B[1], C[1])))), y1 = Math.Min(g.Height - 1, (int)MathF.Floor(MathF.Max(A[1], MathF.Max(B[1], C[1]))));
            int z0 = Math.Max(0, (int)MathF.Floor(MathF.Min(A[2], MathF.Min(B[2], C[2])))), z1 = Math.Min(g.Depth - 1, (int)MathF.Floor(MathF.Max(A[2], MathF.Max(B[2], C[2]))));
            for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                if (TriBox(A, B, C, x + 0.5f, y + 0.5f, z + 0.5f, 0.5f)) { int i = g.Index(x, y, z); surf.p[i] = 1; owner[i] = ti; }
            }
            surf.Dirty();
        }
        // model -> grid coordinates; pulled a hair inside the grid so faces on the bounding box (grid coordinate 0 or dim)
        // land in the first / last cell instead of on its outer boundary
        static float[] ToGrid(VoxelGrid g, float[] v, float[] s, float[] lo, int mg) => new[] {
            Math.Clamp((v[0] - lo[0]) * s[0] + mg, 1e-3f, g.Width - 1e-3f), Math.Clamp((v[1] - lo[1]) * s[1] + mg, 1e-3f, g.Height - 1e-3f), Math.Clamp((v[2] - lo[2]) * s[2] + mg, 1e-3f, g.Depth - 1e-3f) };
        // Akenine-Moller triangle / axis-aligned box overlap
        static bool TriBox(float[] A, float[] B, float[] C, float cx, float cy, float cz, float hs)
        {
            float v0x = A[0] - cx, v0y = A[1] - cy, v0z = A[2] - cz, v1x = B[0] - cx, v1y = B[1] - cy, v1z = B[2] - cz, v2x = C[0] - cx, v2y = C[1] - cy, v2z = C[2] - cz;
            float e0x = v1x - v0x, e0y = v1y - v0y, e0z = v1z - v0z, e1x = v2x - v1x, e1y = v2y - v1y, e1z = v2z - v1z, e2x = v0x - v2x, e2y = v0y - v2y, e2z = v0z - v2z;
            // 9 cross-product axes
            if (!Axis(e0z, -e0y, v0y, v0z, v2y, v2z, hs) || !Axis(e1z, -e1y, v0y, v0z, v2y, v2z, hs) || !Axis(e2z, -e2y, v0y, v0z, v1y, v1z, hs)) return false;
            if (!Axis(-e0z, e0x, v0x, v0z, v2x, v2z, hs) || !Axis(-e1z, e1x, v0x, v0z, v2x, v2z, hs) || !Axis(-e2z, e2x, v0x, v0z, v1x, v1z, hs)) return false;
            if (!Axis(e0y, -e0x, v1x, v1y, v2x, v2y, hs) || !Axis(e1y, -e1x, v0x, v0y, v1x, v1y, hs) || !Axis(e2y, -e2x, v0x, v0y, v1x, v1y, hs)) return false;
            // box axes
            if (MathF.Min(v0x, MathF.Min(v1x, v2x)) > hs || MathF.Max(v0x, MathF.Max(v1x, v2x)) < -hs) return false;
            if (MathF.Min(v0y, MathF.Min(v1y, v2y)) > hs || MathF.Max(v0y, MathF.Max(v1y, v2y)) < -hs) return false;
            if (MathF.Min(v0z, MathF.Min(v1z, v2z)) > hs || MathF.Max(v0z, MathF.Max(v1z, v2z)) < -hs) return false;
            // triangle plane
            float nx = e0y * e1z - e0z * e1y, ny = e0z * e1x - e0x * e1z, nz = e0x * e1y - e0y * e1x;
            float r = hs * (MathF.Abs(nx) + MathF.Abs(ny) + MathF.Abs(nz)), s = nx * v0x + ny * v0y + nz * v0z;
            return MathF.Abs(s) <= r;
        }
        static bool Axis(float a, float b, float p0a, float p0b, float p1a, float p1b, float hs)
        {
            float p0 = a * p0a + b * p0b, p1 = a * p1a + b * p1b, rad = hs * (MathF.Abs(a) + MathF.Abs(b));
            return !(MathF.Min(p0, p1) > rad || MathF.Max(p0, p1) < -rad);
        }
    }
}
