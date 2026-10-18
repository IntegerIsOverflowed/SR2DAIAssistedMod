using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Sr2d64CSport
{
    /// <summary>
    /// How one MBTiles feature is painted. Colours are ARGB (0 = none). Widths are in pixels at zoom 14; the importer
    /// scales them to the zoom it loads (never below 0.6 px). Rank orders the drawing across all tiles: lower ranks are
    /// painted first (land, then water, then roads). A line with both <see cref="Fill"/> and <see cref="Stroke"/> is a
    /// road: the stroke is a casing drawn 1.6 px wider, and the fill is the body on top (drawn at Rank + 1).
    /// </summary>
    public readonly struct MbPaint
    {
        public readonly int Fill, Stroke;
        public readonly float StrokeWidth;
        public readonly int Rank;
        public MbPaint(int fill, int stroke = 0, float strokeWidth = 1f, int rank = 0)
        {
            Fill = fill; Stroke = stroke; StrokeWidth = strokeWidth; Rank = rank;
        }
    }

    /// <summary>Decides how one feature is painted; return null to leave the feature out.</summary>
    public delegate MbPaint? MbTilesStyleRule(string layer, MvtFeature feature, int zoom);

    /// <summary>What part of an MBTiles file to load into a <see cref="VectorImage"/>.</summary>
    public sealed class MbTilesView
    {
        /// <summary>Zoom level to load. -1 = the highest zoom the file has tiles for.</summary>
        public int Zoom = -1;
        /// <summary>Area in degrees. NaN = the whole file (the metadata bounds).</summary>
        public double West = double.NaN, South = double.NaN, East = double.NaN, North = double.NaN;
        /// <summary>Painting rule. Null = <see cref="MbTilesStyle.Default"/>.</summary>
        public MbTilesStyleRule? Style;
        /// <summary>
        /// Width of the hairline stroke drawn under filled polygons. Tiles cut polygons at their edges, and
        /// antialiasing leaves a faint light seam where two pieces meet; this covers it. 0 turns it off.
        /// </summary>
        public float SeamWidth = 0.6f;
    }

    /// <summary>Default colours and widths for the OpenMapTiles schema that tilemaker writes.</summary>
    public static class MbTilesStyle
    {
        /// <summary>Background behind the map (the map view should clear to this before drawing).</summary>
        public static readonly int Background = unchecked((int)0xFFF2EFE9);

        /// <summary>Paints the common OpenMapTiles layers. Labels and icons are not drawn (the importer says so in its warnings).</summary>
        public static MbPaint? Default(string layer, MvtFeature f, int zoom)
        {
            bool line = f.GeomType == 2, point = f.GeomType == 1;
            string cls = f.Class;
            switch (layer)
            {
                case "landcover":
                    return point ? null : new MbPaint(LandcoverColour(cls), 0, 0, 1);
                case "park":
                    return point ? null : new MbPaint(unchecked((int)0xFFCBF0C8), 0, 0, 2);
                case "landuse":
                    return point ? null : new MbPaint(LanduseColour(cls), 0, 0, 3);
                case "water":
                    return point ? null : new MbPaint(unchecked((int)0xFFAAD3DF), 0, 0, 10);
                case "waterway":
                    return line ? new MbPaint(0, unchecked((int)0xFFA0C8E0), cls is "river" or "canal" ? 2.2f : 1.0f, 11) : null;
                case "aeroway":
                    if (line) return new MbPaint(0, unchecked((int)0xFFE0E0E8), 3.0f, 15);
                    return point ? null : new MbPaint(unchecked((int)0xFFE8E8EC), 0, 0, 15);
                case "building":
                    return point ? null : new MbPaint(unchecked((int)0xFFD9D0C9), unchecked((int)0xFFBAAC9C), 0.5f, 20);
                case "transportation":
                    return line ? RoadPaint(cls) : null;
                case "boundary":
                    return line ? new MbPaint(0, unchecked((int)0xFF9E7AB0), 1.2f, 40) : null;
                case "place":
                    // settlement dots only; the names need the label pass
                    return point ? new MbPaint(unchecked((int)0xFF6E6A62), 0, 2.2f, 50) : null;
                default:
                    return null;
            }
        }

        static int LandcoverColour(string cls) => cls switch
        {
            "wood" or "forest" => unchecked((int)0xFFC8E6C0),
            "grass" or "grassland" or "meadow" => unchecked((int)0xFFD4EFC1),
            "farmland" or "farm" => unchecked((int)0xFFEFEAD8),
            "wetland" or "marsh" => unchecked((int)0xFFD2E5CE),
            "sand" or "beach" => unchecked((int)0xFFF5E9C6),
            "ice" or "glacier" => unchecked((int)0xFFF8FCFF),
            _ => unchecked((int)0xFFE6EFD9),
        };

        static int LanduseColour(string cls) => cls switch
        {
            "residential" => unchecked((int)0xFFE8E0D5),
            "industrial" => unchecked((int)0xFFE4DDE3),
            "commercial" or "retail" => unchecked((int)0xFFF0DAD5),
            "cemetery" => unchecked((int)0xFFD6E4CE),
            "railway" => unchecked((int)0xFFE6E2DE),
            _ => unchecked((int)0xFFEDE8DF),
        };

        static MbPaint? RoadPaint(string cls)
        {
            const int white = unchecked((int)0xFFFFFFFF), casing = unchecked((int)0xFFAAA29A);
            switch (cls)
            {
                case "motorway": return new MbPaint(unchecked((int)0xFFE892A2), casing, 5.0f, 30);
                case "trunk": return new MbPaint(unchecked((int)0xFFF9B29C), casing, 4.4f, 30);
                case "primary": return new MbPaint(unchecked((int)0xFFFCD6A4), casing, 4.0f, 30);
                case "secondary": return new MbPaint(unchecked((int)0xFFF7FABF), casing, 3.4f, 30);
                case "tertiary": return new MbPaint(white, casing, 3.0f, 30);
                case "motorway_link": case "trunk_link": case "primary_link": case "secondary_link": case "tertiary_link":
                    return new MbPaint(white, casing, 2.4f, 30);
                case "minor": case "unclassified": case "residential": case "living_street": case "pedestrian":
                    return new MbPaint(white, casing, 2.4f, 30);
                case "service": return new MbPaint(white, casing, 1.6f, 30);
                case "path": case "footway": case "steps": case "track": case "cycleway": case "bridleway":
                    return new MbPaint(unchecked((int)0xFFB8A99A), 0, 0.9f, 31);
                case "rail": case "transit": case "subway": case "tram": case "light_rail":
                    return new MbPaint(unchecked((int)0xFF8A8A8A), 0, 1.0f, 31);
                default: return null;
            }
        }
    }

    internal sealed partial class VectorImage
    {
        const double TileSize = 256.0;
        const float CasingExtra = 1.6f;      // zoom-14 px added to a road's width for its casing

        /// <summary>
        /// Loads vector tiles (MBTiles, format pbf) as a container: one path per feature, placed in world pixels of the
        /// chosen zoom (256 px per tile). <see cref="ViewBox"/> is the tile-aligned rectangle that covers the requested
        /// area; <see cref="VisibleArea"/> is the requested area itself. Draw with <c>DrawFit</c> or <c>Rasterize</c>.
        /// </summary>
        public static VectorImage FromMbTiles(string path, MbTilesView? view = null)
        {
            using var mb = MbTiles.Open(path);
            return FromMbTiles(mb, view);
        }

        /// <summary>Same as <see cref="FromMbTiles(string, MbTilesView)"/> for an already opened file.</summary>
        public static VectorImage FromMbTiles(MbTiles mb, MbTilesView? view = null)
        {
            view ??= new MbTilesView();
            if (!string.Equals(mb.Format, "pbf", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("MBTiles format '" + mb.Format + "' is raster. Only vector tiles (pbf) load as a vector image.");

            int zoom = view.Zoom < 0 ? mb.DataMaxZoom : Math.Clamp(view.Zoom, 0, 30);
            double w = double.IsNaN(view.West) ? mb.West : view.West;
            double s = double.IsNaN(view.South) ? mb.South : view.South;
            double e = double.IsNaN(view.East) ? mb.East : view.East;
            double n = double.IsNaN(view.North) ? mb.North : view.North;
            if (e <= w || n <= s) throw new ArgumentException("The MBTiles view area is empty.");

            int tiles = 1 << zoom;
            double wx0 = LonToWorld(w, zoom), wx1 = LonToWorld(e, zoom);
            double wy0 = LatToWorld(n, zoom), wy1 = LatToWorld(s, zoom);   // north is the top edge
            int x0 = Math.Clamp((int)Math.Floor(wx0 / TileSize), 0, tiles - 1);
            int x1 = Math.Clamp((int)Math.Floor((wx1 - 1e-9) / TileSize), 0, tiles - 1);
            int y0 = Math.Clamp((int)Math.Floor(wy0 / TileSize), 0, tiles - 1);
            int y1 = Math.Clamp((int)Math.Floor((wy1 - 1e-9) / TileSize), 0, tiles - 1);

            var style = view.Style ?? MbTilesStyle.Default;
            float zs = (float)Math.Pow(2, zoom - 14);
            var img = new VectorImage { Format = VectorFormat.MbTiles, Title = mb.Name };
            float width = (x1 - x0 + 1) * (float)TileSize, height = (y1 - y0 + 1) * (float)TileSize;
            img.ViewBox = new RectangleF(0, 0, width, height);
            img.Width = width;
            img.Height = height;
            img.VisibleArea = new RectangleF((float)(wx0 - x0 * TileSize), (float)(wy0 - y0 * TileSize),
                (float)(wx1 - wx0), (float)(wy1 - wy0));

            var drawn = new List<(int Rank, VectorShape Shape)>();
            // a background under everything: an area with no tile is otherwise transparent, and a viewer shows it as black
            var bg = new VectorPath();
            bg.MoveTo(0, 0); bg.LineTo(width, 0); bg.LineTo(width, height); bg.LineTo(0, height); bg.Close();
            drawn.Add((int.MinValue, new VectorShape(bg, new VectorColor(MbTilesStyle.Background), null, 0f)));
            for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
            {
                byte[]? raw = mb.GetTile(zoom, tx, ty);
                if (raw == null) continue;
                List<MvtLayer> layers;
                try { layers = Mvt.Decode(raw); }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                {
                    img.Warnings.Add("MBTiles: tile " + zoom + "/" + tx + "/" + ty + " could not be decoded.");
                    continue;
                }
                float ox = (float)((tx - x0) * TileSize), oy = (float)((ty - y0) * TileSize);
                foreach (var layer in layers)
                {
                    float k = (float)(TileSize / layer.Extent);
                    foreach (var f in layer.Features)
                    {
                        var paint = style(layer.Name, f, zoom);
                        if (paint == null) continue;
                        AddFeature(drawn, f, paint.Value, ox, oy, k, zs, view.SeamWidth);
                    }
                }
            }

            // LINQ OrderBy is stable: features keep their order inside one rank, so roads stay above land across tile borders
            foreach (var d in drawn.OrderBy(d => d.Rank)) img.Shapes.Add(d.Shape);
            img.Warnings.Add("MBTiles: labels and icons are not drawn (geometry only).");
            img.Touch();
            return img;
        }

        static void AddFeature(List<(int Rank, VectorShape Shape)> into, MvtFeature f, MbPaint p,
                               float ox, float oy, float k, float zs, float seam)
        {
            float W(float w) => Math.Max(0.6f, w * zs);
            if (f.GeomType == 3)                              // polygon: outer rings clockwise, holes counter-clockwise
            {
                var path = new VectorPath();
                foreach (var ring in f.Geoms)
                {
                    if (ring.Count < 4) continue;             // three corners plus the repeated first point
                    path.MoveTo(ox + ring[0].X * k, oy + ring[0].Y * k);
                    for (int i = 1; i < ring.Count; i++) path.LineTo(ox + ring[i].X * k, oy + ring[i].Y * k);
                    path.Close();
                }
                if (path.IsEmpty) return;
                if (p.Fill != 0)
                {
                    if (p.Stroke != 0)
                        into.Add((p.Rank, new VectorShape(path, new VectorColor(p.Fill), new VectorColor(p.Stroke), W(p.StrokeWidth))));
                    else if (seam > 0)
                        into.Add((p.Rank, new VectorShape(path, new VectorColor(p.Fill), new VectorColor(p.Fill), seam)));
                    else
                        into.Add((p.Rank, new VectorShape(path, new VectorColor(p.Fill), null, 0f)));
                }
                else if (p.Stroke != 0)
                    into.Add((p.Rank, new VectorShape(path, null, new VectorColor(p.Stroke), W(p.StrokeWidth))));
                return;
            }
            if (f.GeomType == 2)                              // lines
            {
                var path = new VectorPath();
                foreach (var line in f.Geoms)
                {
                    if (line.Count < 2) continue;
                    path.MoveTo(ox + line[0].X * k, oy + line[0].Y * k);
                    for (int i = 1; i < line.Count; i++) path.LineTo(ox + line[i].X * k, oy + line[i].Y * k);
                }
                if (path.IsEmpty) return;
                if (p.Fill != 0 && p.Stroke != 0)             // road: casing, then the body on top
                {
                    into.Add((p.Rank, new VectorShape(path, null, new VectorColor(p.Stroke), W(p.StrokeWidth + CasingExtra))));
                    into.Add((p.Rank + 1, new VectorShape(path.Clone(), null, new VectorColor(p.Fill), W(p.StrokeWidth))));
                }
                else
                {
                    int col = p.Stroke != 0 ? p.Stroke : p.Fill;
                    if (col != 0) into.Add((p.Rank, new VectorShape(path, null, new VectorColor(col), W(p.StrokeWidth))));
                }
                return;
            }
            if (f.GeomType == 1 && p.Fill != 0)               // points: dots
            {
                var path = new VectorPath();
                foreach (var pt in f.Geoms)
                    if (pt.Count > 0) path.Circle(ox + pt[0].X * k, oy + pt[0].Y * k, Math.Max(1.5f, p.StrokeWidth * zs));
                if (!path.IsEmpty) into.Add((p.Rank, new VectorShape(path, new VectorColor(p.Fill), null, 0f)));
            }
        }

        // Web-Mercator world pixels at zoom z (256 px per tile).
        static double LonToWorld(double lon, int z) => (lon + 180.0) / 360.0 * TileSize * (1 << z);

        static double LatToWorld(double lat, int z)
        {
            lat = Math.Clamp(lat, -85.0511287798, 85.0511287798);
            double r = lat * Math.PI / 180.0;
            return (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * TileSize * (1 << z);
        }
    }
}
