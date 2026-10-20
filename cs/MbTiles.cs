using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Sr2d64CSport
{
    /// <summary>
    /// Read-only MBTiles access: a SQLite 3 file with a <c>tiles</c> table (zoom_level, tile_column, tile_row,
    /// tile_data) and a <c>metadata</c> table (name, value). Vector tiles (format <c>pbf</c>, usually gzip) and
    /// raster tiles (png / jpg / webp) both come back as raw bytes from <see cref="GetTile"/>.
    /// Coordinates are XYZ (north-up); the TMS row order stored in the file is converted here.
    /// No dependencies: the file is read with the SQLite on-disk format directly (see <see cref="SqliteReader"/>).
    /// </summary>
    public sealed class MbTiles : IDisposable
    {
        readonly SqliteReader _db;
        readonly object _lock = new();
        // tiles table: (z,x,y) XYZ -> where the tile_data cell lives
        readonly Dictionary<long, SqliteReader.CellLoc> _tiles = new();
        // map/images variant: (z,x,y) -> tile_id, tile_id -> cell of the image
        readonly Dictionary<long, string> _map = new();
        readonly Dictionary<string, SqliteReader.CellLoc> _images = new();
        readonly int _dataCol;

        /// <summary>Metadata: name, format ("pbf", "png", "jpg", "webp"), description, attribution, json (the vector layer list).</summary>
        public string Name { get; private set; } = "";
        public string Format { get; private set; } = "pbf";
        public string Description { get; private set; } = "";
        public string Attribution { get; private set; } = "";
        public string Json { get; private set; } = "";
        public int MinZoom { get; private set; }
        public int MaxZoom { get; private set; }
        /// <summary>Geographic bounds in degrees (metadata "bounds"; default the whole world).</summary>
        public double West { get; private set; } = -180;
        public double South { get; private set; } = -85.0511287798;
        public double East { get; private set; } = 180;
        public double North { get; private set; } = 85.0511287798;
        /// <summary>Number of tiles in the file.</summary>
        public int TileCount => _tiles.Count + _map.Count;
        /// <summary>Highest zoom that has at least one tile (the metadata maxzoom can be higher than the data).</summary>
        public int DataMaxZoom { get; private set; }

        MbTiles(SqliteReader db)
        {
            _db = db;
            var meta = db.FindTable("metadata");
            if (meta != null) ReadMetadata(meta);

            var tiles = db.FindTable("tiles");
            if (tiles != null)
            {
                int z = db.ColumnIndex(tiles, "zoom_level"), x = db.ColumnIndex(tiles, "tile_column");
                int y = db.ColumnIndex(tiles, "tile_row"), d = db.ColumnIndex(tiles, "tile_data");
                _dataCol = d;
                int need = Math.Max(z, Math.Max(x, y)) + 1;
                db.ForEachRow(tiles, need, (vals, loc) =>
                {
                    int zz = (int)(long)vals[z]!, xx = (int)(long)vals[x]!, tmsRow = (int)(long)vals[y]!;
                    int yy = (1 << zz) - 1 - tmsRow;
                    _tiles[Key(zz, xx, yy)] = loc;
                });
            }
            else
            {
                var map = db.FindTable("map");
                var images = db.FindTable("images");
                if (map == null || images == null)
                    throw new InvalidDataException("Not an MBTiles file: no 'tiles' table (and no 'map'/'images' pair).");
                int z = db.ColumnIndex(map, "zoom_level"), x = db.ColumnIndex(map, "tile_column");
                int y = db.ColumnIndex(map, "tile_row"), id = db.ColumnIndex(map, "tile_id");
                db.ForEachRow(map, Math.Max(Math.Max(z, x), Math.Max(y, id)) + 1, (vals, loc) =>
                {
                    int zz = (int)(long)vals[z]!, xx = (int)(long)vals[x]!, tmsRow = (int)(long)vals[y]!;
                    _map[Key(zz, xx, (1 << zz) - 1 - tmsRow)] = (string)vals[id]!;
                });
                int iid = db.ColumnIndex(images, "tile_id");
                _dataCol = db.ColumnIndex(images, "tile_data");
                db.ForEachRow(images, iid + 1, (vals, loc) => _images[(string)vals[iid]!] = loc);
            }

            DataMaxZoom = ComputeDataMaxZoom();
            if (metaMaxZoomMissing) MaxZoom = DataMaxZoom;
        }

        bool metaMaxZoomMissing = true;

        /// <summary>Opens an MBTiles file read-only (other programs may keep reading it).</summary>
        public static MbTiles Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try { return new MbTiles(new SqliteReader(fs)); }
            catch { fs.Dispose(); throw; }
        }

        /// <summary>Opens an MBTiles database held in memory (the stream must be seekable and stays owned by the caller).</summary>
        public static MbTiles Open(Stream seekable) => new MbTiles(new SqliteReader(seekable));

        /// <summary>True when the file starts with the SQLite 3 header (it does not check the tables).</summary>
        public static bool IsSqlite(byte[] head) => head.Length >= 16 && SqliteReader.IsHeader(head);

        public static bool IsSqliteFile(string path)
        {
            using var fs = File.OpenRead(path);
            var head = new byte[16];
            return fs.Read(head, 0, 16) == 16 && SqliteReader.IsHeader(head);
        }

        void ReadMetadata(SqliteReader.Table meta)
        {
            int n = _db.ColumnIndex(meta, "name"), v = _db.ColumnIndex(meta, "value");
            _db.ForEachRow(meta, Math.Max(n, v) + 1, (vals, _) =>
            {
                string k = vals[n] as string ?? "";
                string val = vals[v] as string ?? "";
                switch (k)
                {
                    case "name": Name = val; break;
                    case "format": Format = val.Trim().ToLowerInvariant(); break;
                    case "description": Description = val; break;
                    case "attribution": Attribution = val; break;
                    case "json": Json = val; break;
                    case "minzoom": if (int.TryParse(val, out int mn)) MinZoom = mn; break;
                    case "maxzoom": if (int.TryParse(val, out int mx)) { MaxZoom = mx; metaMaxZoomMissing = false; } break;
                    case "bounds":
                        var parts = val.Split(',');
                        if (parts.Length == 4 && TryD(parts[0], out double w) && TryD(parts[1], out double s) &&
                            TryD(parts[2], out double e) && TryD(parts[3], out double no))
                        { West = w; South = s; East = e; North = no; }
                        break;
                }
            });
        }

        static bool TryD(string s, out double d) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d);

        int ComputeDataMaxZoom()
        {
            int mz = 0;
            foreach (var k in _tiles.Keys) mz = Math.Max(mz, (int)(k >> 48));
            foreach (var k in _map.Keys) mz = Math.Max(mz, (int)(k >> 48));
            return mz;
        }

        static long Key(int z, int x, int y) => ((long)z << 48) | ((long)x << 24) | (uint)y;

        /// <summary>Every (z, x, y) that has a tile (XYZ).</summary>
        public IEnumerable<(int Z, int X, int Y)> Keys()
        {
            foreach (var k in _tiles.Keys) yield return ((int)(k >> 48), (int)((k >> 24) & 0xFFFFFF), (int)(k & 0xFFFFFF));
            foreach (var k in _map.Keys) yield return ((int)(k >> 48), (int)((k >> 24) & 0xFFFFFF), (int)(k & 0xFFFFFF));
        }

        /// <summary>
        /// Raw tile bytes for XYZ coordinates (gunzipped when the stored payload is gzip), or null when the file has no
        /// tile there. For vector tiles these are the protobuf bytes of the MVT (see <see cref="Mvt.Decode"/>).
        /// </summary>
        public byte[]? GetTile(int z, int x, int yXyz)
        {
            if (z < 0 || x < 0 || yXyz < 0 || z > 30) return null;
            long k = Key(z, x, yXyz);
            lock (_lock)
            {
                SqliteReader.CellLoc loc;
                if (_tiles.TryGetValue(k, out loc)) { }
                else if (_map.TryGetValue(k, out var id) && _images.TryGetValue(id, out var iloc)) loc = iloc;
                else return null;
                var row = _db.ReadRow(loc);
                if (row[_dataCol] is not byte[] data || data.Length == 0) return null;
                return Ungzip(data);
            }
        }

        /// <summary>Gunzips when the bytes start with the gzip magic; returns other data unchanged.</summary>
        public static byte[] Ungzip(byte[] data)
        {
            if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b) return data;
            using var src = new MemoryStream(data);
            using var gz = new GZipStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream(data.Length * 3);
            gz.CopyTo(dst);
            return dst.ToArray();
        }

        public void Dispose()
        {
            lock (_lock) _db.Dispose();
        }
    }

    /// <summary>
    /// Minimal read-only SQLite 3 reader: the file header, table schema (sqlite_schema), and the table B-trees
    /// (rowid tables, including overflow pages). Enough for MBTiles; index b-trees, WAL and writing are not supported.
    /// Reference: https://www.sqlite.org/fileformat.html
    /// </summary>
    internal sealed class SqliteReader : IDisposable
    {
        readonly Stream _s;
        public readonly int PageSize;
        public readonly int Usable;
        readonly Encoding _text;

        /// <summary>Position of a table cell in the file: page number and the cell's offset inside that page.</summary>
        public readonly struct CellLoc
        {
            public readonly uint Page;
            public readonly int Offset;
            public CellLoc(uint page, int offset) { Page = page; Offset = offset; }
        }

        public sealed class Table
        {
            public string Name = "";
            public uint Root;
            public string[] Columns = Array.Empty<string>();
        }

        static readonly byte[] Magic = Encoding.ASCII.GetBytes("SQLite format 3\0");

        public static bool IsHeader(byte[] head)
        {
            for (int i = 0; i < Magic.Length; i++) if (head[i] != Magic[i]) return false;
            return true;
        }

        public SqliteReader(Stream s)
        {
            _s = s;
            var h = new byte[100];
            ReadAt(0, h);
            if (!IsHeader(h)) throw new InvalidDataException("Not an SQLite 3 database.");
            int ps = BE16(h, 16);
            PageSize = ps == 1 ? 65536 : ps;
            if (PageSize < 512 || PageSize > 65536 || (PageSize & (PageSize - 1)) != 0)
                throw new InvalidDataException("Unsupported SQLite page size " + PageSize + ".");
            if (h[18] == 2 || h[19] == 2)
                throw new NotSupportedException("SQLite WAL-mode database. Convert it first: sqlite3 file.mbtiles \"PRAGMA journal_mode=DELETE;\"");
            Usable = PageSize - h[20];
            _text = BE32(h, 56) switch
            {
                2 => Encoding.Unicode,
                3 => Encoding.BigEndianUnicode,
                _ => new UTF8Encoding(false),
            };
            _tables = ReadSchema();
        }

        readonly List<Table> _tables;

        public Table? FindTable(string name)
        {
            foreach (var t in _tables)
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        public int ColumnIndex(Table t, string column)
        {
            for (int i = 0; i < t.Columns.Length; i++)
                if (string.Equals(t.Columns[i], column, StringComparison.OrdinalIgnoreCase)) return i;
            throw new InvalidDataException("Table '" + t.Name + "' has no column '" + column + "'.");
        }

        /// <summary>
        /// Calls <paramref name="visit"/> for every row of the table with the first <paramref name="columns"/> values
        /// decoded (the rest stay null). Only the cell's local bytes are read, so large blobs are not loaded.
        /// </summary>
        public void ForEachRow(Table t, int columns, Action<object?[], CellLoc> visit)
        {
            ForEachLeafCell(t.Root, (page, pg, off) =>
            {
                var cell = ParseLeafCell(pg, off);
                var local = new byte[cell.LocalSize];
                Buffer.BlockCopy(pg, cell.DataStart, local, 0, cell.LocalSize);
                visit(ParseRecord(local, columns), new CellLoc(page, off));
            });
        }

        /// <summary>Full decoded row (all columns, overflow pages included) for a cell found by <see cref="ForEachRow"/>.</summary>
        public object?[] ReadRow(CellLoc loc)
        {
            var pg = ReadPage(loc.Page);
            return ParseRecord(ReadCellPayload(pg, loc.Offset), int.MaxValue);
        }

        // ------------------------------------------------------------------ schema

        List<Table> ReadSchema()
        {
            var list = new List<Table>();
            ForEachLeafCell(1, (_, pg, off) =>
            {
                var v = ParseRecord(ReadCellPayload(pg, off), 5);
                if (v.Length < 5 || v[0] as string != "table") return;
                var t = new Table
                {
                    Name = v[2] as string ?? "",
                    Root = (uint)(long)(v[3] ?? 0L),
                    Columns = ColumnNames(v[4] as string ?? ""),
                };
                if (t.Root != 0) list.Add(t);
            });
            return list;
        }

        /// <summary>Column names from "CREATE TABLE name (a int, b blob, PRIMARY KEY(a))".</summary>
        static string[] ColumnNames(string sql)
        {
            int open = sql.IndexOf('('), close = sql.LastIndexOf(')');
            if (open < 0 || close <= open) return Array.Empty<string>();
            var names = new List<string>();
            int depth = 0, start = open + 1;
            for (int i = open + 1; i <= close; i++)
            {
                char c = i == close ? ',' : sql[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    string part = sql.Substring(start, i - start).Trim();
                    start = i + 1;
                    if (part.Length == 0) continue;
                    string first = part.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    string up = first.ToUpperInvariant();
                    if (up is "PRIMARY" or "UNIQUE" or "CHECK" or "CONSTRAINT" or "FOREIGN") continue;
                    names.Add(first.Trim('"', '`', '[', ']', '\''));
                }
            }
            return names.ToArray();
        }

        // ------------------------------------------------------------------ b-tree

        /// <summary>Visits every leaf cell of the rowid b-tree at <paramref name="root"/>: (page number, page bytes, cell offset).</summary>
        void ForEachLeafCell(uint root, Action<uint, byte[], int> visit)
        {
            var stack = new Stack<uint>();
            stack.Push(root);
            var seen = 0;
            while (stack.Count > 0)
            {
                if (++seen > 1_000_000) throw new InvalidDataException("B-tree is too large or cyclic.");
                uint pn = stack.Pop();
                var pg = ReadPage(pn);
                int h = pn == 1 ? 100 : 0;
                byte type = pg[h];
                int n = BE16(pg, h + 3);
                if (type == 0x0d)                       // leaf table
                {
                    for (int i = 0; i < n; i++) visit(pn, pg, BE16(pg, h + 8 + 2 * i));
                }
                else if (type == 0x05)                  // interior table: children, then the right-most child
                {
                    for (int i = n - 1; i >= 0; i--) stack.Push(BE32(pg, BE16(pg, h + 12 + 2 * i)));
                    stack.Push(BE32(pg, h + 8));
                }
                else throw new InvalidDataException("Unexpected b-tree page type 0x" + type.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + " on page " + pn + ".");
            }
        }

        struct Cell
        {
            public long PayloadSize;
            public long RowId;
            public int DataStart;
            public int LocalSize;
            public uint Overflow;
        }

        Cell ParseLeafCell(byte[] pg, int off)
        {
            int p = off;
            var c = new Cell();
            c.PayloadSize = Varint(pg, ref p);
            c.RowId = Varint(pg, ref p);
            c.DataStart = p;
            c.LocalSize = LocalSize(c.PayloadSize);
            c.Overflow = c.PayloadSize > c.LocalSize ? BE32(pg, p + c.LocalSize) : 0;
            return c;
        }

        /// <summary>Bytes of a payload kept on the b-tree page (the rest lives in overflow pages). Per the file format spec.</summary>
        int LocalSize(long payload)
        {
            int u = Usable, x = u - 35;
            if (payload <= x) return (int)payload;
            int m = (u - 12) * 32 / 255 - 23;
            int k = m + (int)((payload - m) % (u - 4));
            return k <= x ? k : m;
        }

        /// <summary>The whole payload of the leaf cell at <paramref name="off"/> (follows overflow pages).</summary>
        byte[] ReadCellPayload(byte[] pg, int off)
        {
            var c = ParseLeafCell(pg, off);
            var result = new byte[c.PayloadSize];
            Buffer.BlockCopy(pg, c.DataStart, result, 0, c.LocalSize);
            int copied = c.LocalSize;
            uint next = c.Overflow;
            int guard = 0;
            while (copied < result.Length)
            {
                if (next == 0 || ++guard > 10_000_000) throw new InvalidDataException("Broken overflow chain.");
                var op = ReadPage(next);
                next = BE32(op, 0);
                int n = (int)Math.Min(Usable - 4, result.Length - copied);
                Buffer.BlockCopy(op, 4, result, copied, n);
                copied += n;
            }
            return result;
        }

        // ------------------------------------------------------------------ records

        /// <summary>Decodes the first <paramref name="columns"/> values of a record (SQLite record format).</summary>
        object?[] ParseRecord(byte[] r, int columns)
        {
            int p = 0;
            long hdr = Varint(r, ref p);
            var types = new List<long>();
            while (p < hdr && types.Count < columns) types.Add(Varint(r, ref p));
            int q = (int)hdr;
            var vals = new object?[types.Count];
            for (int i = 0; i < types.Count; i++)
            {
                long t = types[i];
                switch (t)
                {
                    case 0: vals[i] = null; break;
                    case 1: vals[i] = (long)(sbyte)r[q]; q += 1; break;
                    case 2: vals[i] = (long)(short)BE16(r, q); q += 2; break;
                    case 3: vals[i] = (long)(((r[q] << 24) | (r[q + 1] << 16) | (r[q + 2] << 8)) >> 8); q += 3; break;
                    case 4: vals[i] = (long)(int)BE32(r, q); q += 4; break;
                    case 5: { long v = 0; for (int k = 0; k < 6; k++) v = (v << 8) | r[q + k]; if ((v & 0x800000000000L) != 0) v |= unchecked((long)0xFFFF000000000000UL); vals[i] = v; q += 6; break; }
                    case 6: vals[i] = (long)BE64(r, q); q += 8; break;
                    case 7: vals[i] = BitConverter.Int64BitsToDouble((long)BE64(r, q)); q += 8; break;
                    case 8: vals[i] = 0L; break;
                    case 9: vals[i] = 1L; break;
                    default:
                        if (t >= 12 && (t & 1) == 0)
                        {
                            int n = (int)((t - 12) / 2);
                            var blob = new byte[n];
                            Buffer.BlockCopy(r, q, blob, 0, n);
                            vals[i] = blob; q += n;
                        }
                        else if (t >= 13)
                        {
                            int n = (int)((t - 13) / 2);
                            vals[i] = _text.GetString(r, q, n); q += n;
                        }
                        else throw new InvalidDataException("Unsupported SQLite serial type " + t + ".");
                        break;
                }
            }
            return vals;
        }

        static long Varint(byte[] b, ref int p)
        {
            long v = 0;
            for (int i = 0; i < 8; i++)
            {
                byte c = b[p++];
                v = (v << 7) | (long)(c & 0x7f);
                if ((c & 0x80) == 0) return v;
            }
            return (v << 8) | b[p++];
        }

        // ------------------------------------------------------------------ pages and bytes

        byte[] ReadPage(uint pageNo)
        {
            if (pageNo == 0) throw new InvalidDataException("Page 0 requested.");
            var buf = new byte[PageSize];
            ReadAt((long)(pageNo - 1) * PageSize, buf);
            return buf;
        }

        void ReadAt(long offset, byte[] buf)
        {
            _s.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < buf.Length)
            {
                int r = _s.Read(buf, total, buf.Length - total);
                if (r <= 0) throw new InvalidDataException("Unexpected end of SQLite file.");
                total += r;
            }
        }

        static int BE16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        static uint BE32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        static ulong BE64(byte[] b, int o) => ((ulong)BE32(b, o) << 32) | BE32(b, o + 4);

        public void Dispose() => _s.Dispose();
    }
}
