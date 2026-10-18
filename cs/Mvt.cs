using System;
using System.Collections.Generic;
using System.Text;

namespace Sr2d64CSport
{
    /// <summary>One decoded Mapbox Vector Tile (MVT) feature. Geometry stays in tile units (0 .. <see cref="MvtLayer.Extent"/>).</summary>
    public sealed class MvtFeature
    {
        /// <summary>1 = point, 2 = line, 3 = polygon (MVT GeomType); 0 = unknown.</summary>
        public int GeomType;
        public readonly Dictionary<string, object?> Props = new();
        /// <summary>
        /// Points, polylines or rings, one list per sub-geometry. Polygon rings are closed (the first point is repeated).
        /// </summary>
        public readonly List<List<(float X, float Y)>> Geoms = new();

        public string? Str(string key) => Props.TryGetValue(key, out var v) ? v as string : null;
        /// <summary>A numeric attribute (int, uint, float or double), or null when absent or not a number.</summary>
        public double? Num(string key)
        {
            if (!Props.TryGetValue(key, out var v)) return null;
            return v switch
            {
                long l => (double?)l,
                ulong u => (double?)u,
                double d => (double?)d,
                float f => (double?)f,
                _ => null,
            };
        }
        /// <summary>The feature's "class" attribute (for OpenMapTiles data: road class, landuse class, ...), or "".</summary>
        public string Class => Str("class") ?? "";
        /// <summary>Best label: tilemaker writes the local name into "name:latin"; also "name", "name:ru" and "ref".</summary>
        public string? Name => Str("name") ?? Str("name:latin") ?? Str("name:ru") ?? Str("ref");
    }

    /// <summary>One MVT layer (for example "water", "transportation", "building").</summary>
    public sealed class MvtLayer
    {
        public string Name = "";
        /// <summary>Tile coordinate range: 4096 for tilemaker / OpenMapTiles data.</summary>
        public uint Extent = 4096;
        public readonly List<MvtFeature> Features = new();
    }

    /// <summary>
    /// Minimal Mapbox Vector Tile reader: plain protobuf wire format, no dependencies. Gzip-compressed tiles (the usual
    /// MBTiles case) are gunzipped first. Malformed data stops the decode at the bad point; what was read is kept.
    /// Spec: https://github.com/mapbox/vector-tile-spec
    /// </summary>
    public static class Mvt
    {
        /// <summary>Decodes the layers of one tile (raw or gzip bytes).</summary>
        public static List<MvtLayer> Decode(byte[] data)
        {
            data = MbTiles.Ungzip(data);
            var layers = new List<MvtLayer>();
            try
            {
                int pos = 0;
                while (pos < data.Length)
                {
                    ulong key = ReadVarint(data, ref pos);
                    int field = (int)(key >> 3), wire = (int)(key & 7);
                    if (field == 3 && wire == 2)
                    {
                        int len = Len(data, ref pos);
                        layers.Add(DecodeLayer(data, pos, pos + len));
                        pos += len;
                    }
                    else Skip(data, ref pos, wire);
                }
            }
            catch (IndexOutOfRangeException) { }
            catch (ArgumentException) { }
            return layers;
        }

        static MvtLayer DecodeLayer(byte[] d, int pos, int end)
        {
            var layer = new MvtLayer();
            var keys = new List<string>();
            var values = new List<object?>();
            var featureSpans = new List<(int start, int len)>();

            while (pos < end)
            {
                ulong key = ReadVarint(d, ref pos);
                int field = (int)(key >> 3), wire = (int)(key & 7);
                switch (field)
                {
                    case 1 when wire == 2: layer.Name = ReadString(d, ref pos); break;
                    case 2 when wire == 2:
                        int len = Len(d, ref pos);
                        featureSpans.Add((pos, len));
                        pos += len;
                        break;
                    case 3 when wire == 2: keys.Add(ReadString(d, ref pos)); break;
                    case 4 when wire == 2:
                        int vlen = Len(d, ref pos);
                        values.Add(DecodeValue(d, pos, pos + vlen));
                        pos += vlen;
                        break;
                    case 5 when wire == 0: layer.Extent = (uint)ReadVarint(d, ref pos); break;
                    default: Skip(d, ref pos, wire); break;
                }
            }
            if (layer.Extent == 0) layer.Extent = 4096;

            foreach (var (start, len) in featureSpans)
                layer.Features.Add(DecodeFeature(d, start, start + len, keys, values));
            return layer;
        }

        static MvtFeature DecodeFeature(byte[] d, int pos, int end, List<string> keys, List<object?> values)
        {
            var f = new MvtFeature();
            var tags = new List<uint>();
            var geom = new List<uint>();
            while (pos < end)
            {
                ulong key = ReadVarint(d, ref pos);
                int field = (int)(key >> 3), wire = (int)(key & 7);
                switch (field)
                {
                    case 1: ReadVarint(d, ref pos); break;                  // feature id: not needed
                    case 2:
                        if (wire == 2) ReadPacked(d, ref pos, tags);
                        else if (wire == 0) tags.Add((uint)ReadVarint(d, ref pos));
                        else Skip(d, ref pos, wire);
                        break;
                    case 3 when wire == 0: f.GeomType = (int)ReadVarint(d, ref pos); break;
                    case 4:
                        if (wire == 2) ReadPacked(d, ref pos, geom);
                        else if (wire == 0) geom.Add((uint)ReadVarint(d, ref pos));
                        else Skip(d, ref pos, wire);
                        break;
                    default: Skip(d, ref pos, wire); break;
                }
            }

            for (int i = 0; i + 1 < tags.Count; i += 2)
            {
                int ki = (int)tags[i], vi = (int)tags[i + 1];
                if (ki < keys.Count && vi < values.Count) f.Props[keys[ki]] = values[vi];
            }

            DecodeGeometry(geom, f.Geoms);
            return f;
        }

        /// <summary>MVT geometry commands: MoveTo (1), LineTo (2), ClosePath (7); parameters are zigzag deltas.</summary>
        static void DecodeGeometry(List<uint> g, List<List<(float X, float Y)>> outGeoms)
        {
            int i = 0, x = 0, y = 0;
            List<(float X, float Y)>? cur = null;
            while (i < g.Count)
            {
                uint cmdInt = g[i++];
                int cmd = (int)(cmdInt & 0x7), count = (int)(cmdInt >> 3);
                if (cmd == 7)
                {
                    if (cur != null && cur.Count > 0) cur.Add(cur[0]);   // close the ring
                    continue;
                }
                for (int c = 0; c < count && i + 1 < g.Count; c++)
                {
                    x += ZigZag(g[i++]);
                    y += ZigZag(g[i++]);
                    if (cmd == 1)
                    {
                        cur = new List<(float X, float Y)> { (x, y) };
                        outGeoms.Add(cur);
                    }
                    else if (cmd == 2 && cur != null) cur.Add((x, y));
                }
            }
        }

        static object? DecodeValue(byte[] d, int pos, int end)
        {
            object? result = null;
            while (pos < end)
            {
                ulong key = ReadVarint(d, ref pos);
                int field = (int)(key >> 3), wire = (int)(key & 7);
                switch (field)
                {
                    case 1: result = ReadString(d, ref pos); break;                                   // string
                    case 2: result = BitConverter.Int32BitsToSingle((int)ReadFixed32(d, ref pos)); break; // float
                    case 3: result = BitConverter.Int64BitsToDouble((long)ReadFixed64(d, ref pos)); break; // double
                    case 4: result = (long)ReadVarint(d, ref pos); break;                             // int64
                    case 5: result = ReadVarint(d, ref pos); break;                                   // uint64
                    case 6:                                                                           // sint64
                        {
                            ulong uv = ReadVarint(d, ref pos);
                            result = (long)(uv >> 1) ^ -(long)(uv & 1);
                            break;
                        }
                    case 7: result = ReadVarint(d, ref pos) != 0; break;                              // bool
                    default: Skip(d, ref pos, wire); break;
                }
            }
            return result;
        }

        // ---- protobuf primitives -------------------------------------------------

        static int ZigZag(uint n) => (int)(n >> 1) ^ -(int)(n & 1);

        static int Len(byte[] d, ref int pos)
        {
            ulong n = ReadVarint(d, ref pos);
            if (n > (ulong)(d.Length - pos)) throw new ArgumentException("Length past the end of the tile.");
            return (int)n;
        }

        static ulong ReadVarint(byte[] d, ref int pos)
        {
            ulong v = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (pos >= d.Length) throw new ArgumentException("Truncated varint.");
                byte b = d[pos++];
                v |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) break;
            }
            return v;
        }

        static uint ReadFixed32(byte[] d, ref int pos)
        {
            if (pos + 4 > d.Length) throw new ArgumentException("Truncated fixed32.");
            uint v = (uint)(d[pos] | d[pos + 1] << 8 | d[pos + 2] << 16 | d[pos + 3] << 24);
            pos += 4;
            return v;
        }

        static ulong ReadFixed64(byte[] d, ref int pos)
        {
            ulong lo = ReadFixed32(d, ref pos), hi = ReadFixed32(d, ref pos);
            return lo | hi << 32;
        }

        static string ReadString(byte[] d, ref int pos)
        {
            int len = Len(d, ref pos);
            string s = Encoding.UTF8.GetString(d, pos, len);
            pos += len;
            return s;
        }

        static void ReadPacked(byte[] d, ref int pos, List<uint> into)
        {
            int len = Len(d, ref pos);
            int end = pos + len;
            while (pos < end) into.Add((uint)ReadVarint(d, ref pos));
        }

        static void Skip(byte[] d, ref int pos, int wire)
        {
            switch (wire)
            {
                case 0: ReadVarint(d, ref pos); break;
                case 1: pos += 8; break;
                case 2: { int n = Len(d, ref pos); pos += n; break; }
                case 5: pos += 4; break;
                default: throw new ArgumentException("Unknown protobuf wire type " + wire + ".");
            }
        }
    }
}
