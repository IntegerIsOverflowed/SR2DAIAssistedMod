using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

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
        /// <summary>Hairline outline: the stroke is one device pixel at every zoom (the width is ignored).</summary>
        public readonly bool Hairline;
        public MbPaint(int fill, int stroke = 0, float strokeWidth = 1f, int rank = 0, bool hairline = false)
        {
            Fill = fill; Stroke = stroke; StrokeWidth = strokeWidth; Rank = rank; Hairline = hairline;
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
        /// <summary>
        /// Names as vector text: road names follow the road line (reads left to right), place names are straight at the
        /// point. Sizes are in image units, not scaled by zoom, so the text keeps a readable size at every level.
        /// </summary>
        public bool Labels = true;
        /// <summary>
        /// Threads that decode and build the tiles: 0 = all processors, 1 = one thread. The picture is the same at any count.
        /// The style rule is called from several threads at once, so a rule that is not thread-safe needs 1.
        /// </summary>
        public int Threads = 0;
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
                    return point ? null : new MbPaint(unchecked((int)0xFFD9D0C9), unchecked((int)0xFFBAAC9C), 0.5f, 20, hairline: true);
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
            img.IsMap = true;                          // a map: the map level of detail applies (see MapLayerMinPx)
            img.MapLayerMinPx["building"] = 2.5f;     // a building whose larger side is under 2.5 px on screen is left out
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

            // The raw tiles are read first, in order, from the one reader. Decoding and building their shapes is the expensive
            // part, and each tile is independent, so tiles are built in parallel. The parts are joined in tile order, so the
            // picture is the same at any thread count.
            int cols = x1 - x0 + 1, count = cols * (y1 - y0 + 1);
            var raw = new byte[]?[count];
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                    raw[(ty - y0) * cols + (tx - x0)] = mb.GetTile(zoom, tx, ty);
            var parts = new (List<(int Rank, VectorShape Shape)> Shapes, List<string> Warnings)[count];
            void BuildTile(int idx)
            {
                var shapes = new List<(int Rank, VectorShape Shape)>();
                var warn = new List<string>();
                parts[idx] = (shapes, warn);
                var bytes = raw[idx];
                if (bytes == null) return;
                int tx = x0 + idx % cols, ty = y0 + idx / cols;
                List<MvtLayer> layers;
                try { layers = Mvt.Decode(bytes); }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                {
                    warn.Add("MBTiles: tile " + zoom + "/" + tx + "/" + ty + " could not be decoded.");
                    return;
                }
                float ox = (float)((tx - x0) * TileSize), oy = (float)((ty - y0) * TileSize);
                var labelNames = new HashSet<string>();      // one label per name and tile (no repeats along a long road)
                foreach (var layer in layers)
                {
                    float k = (float)(TileSize / layer.Extent);
                    string ln = string.Intern(layer.Name);    // the map layer of each shape (one shared string, not one per shape)
                    foreach (var f in layer.Features)
                    {
                        var paint = style(layer.Name, f, zoom);
                        if (paint == null) continue;
                        int before = shapes.Count;
                        AddFeature(shapes, f, paint.Value, ox, oy, k, zs, view.SeamWidth);
                        for (int j = before; j < shapes.Count; j++)
                        {
                            shapes[j].Shape.Layer = ln;
                            shapes[j].Shape.Hairline = paint.Value.Hairline;
                        }
                    }
                    if (view.Labels)
                        foreach (var f in layer.Features)
                            if (layer.Name == "transportation_name" || layer.Name == "place")
                                AddLabel(shapes, layer.Name, f, ox, oy, k, zoom, labelNames);
                }
            }
            int threads = view.Threads <= 0 ? Environment.ProcessorCount : view.Threads;
            if (threads == 1) for (int i = 0; i < count; i++) BuildTile(i);
            else Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = threads }, BuildTile);
            foreach (var (shapes, warn) in parts)
            {
                drawn.AddRange(shapes);
                img.Warnings.AddRange(warn);
            }

            // LINQ OrderBy is stable: features keep their order inside one rank, so roads stay above land across tile borders
            foreach (var d in drawn.OrderBy(d => d.Rank)) img.Shapes.Add(d.Shape);
            if (!view.Labels) img.Warnings.Add("MBTiles: labels are off (geometry only).");
            else img.Warnings.Add("MBTiles: road and place names are drawn as vector text; icons are not drawn.");
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

        // Labels are drawn above everything (the rank is larger than any paint rank).
        const int LabelRank = int.MaxValue - 1;

        /// <summary>Road name size (image units) and the first zoom it appears at: main roads first, minor roads when zoomed in.</summary>
        static float RoadLabelSize(string cls, int zoom, out bool show)
        {
            switch (cls)
            {
                case "motorway": case "trunk": case "primary": show = zoom >= 12; return 10f;
                case "secondary": case "tertiary": show = zoom >= 13; return 9f;
                default: show = zoom >= 14; return 8f;
            }
        }

        /// <summary>Place name size (image units) for a place class at a zoom; 0 = not shown at this zoom.</summary>
        static float PlaceLabelSize(string cls, int zoom)
        {
            switch (cls)
            {
                case "city": return zoom >= 4 ? 16f : 0f;
                case "town": return zoom >= 8 ? 13f : 0f;
                case "village": case "suburb": return zoom >= 11 ? 11f : 0f;
                case "hamlet": case "neighbourhood": case "isolated_dwelling": return zoom >= 13 ? 10f : 0f;
                default: return 0f;
            }
        }

        static void AddLabel(List<(int Rank, VectorShape Shape)> into, string layer, MvtFeature f, float ox, float oy, float k, int zoom,
                             HashSet<string> namesInTile)
        {
            string? name = f.Name;
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Normalize(System.Text.NormalizationForm.FormC).Trim();
            if (!namesInTile.Add(layer + "|" + name)) return;

            if (layer == "transportation_name" && f.GeomType == 2)
            {
                float size = RoadLabelSize(f.Class, zoom, out bool show);
                if (!show) return;
                float textWidth = VectorText.Width(name, size);
                foreach (var line in f.Geoms)
                {
                    if (line.Count < 2) continue;
                    var pts = new List<PointF>(line.Count);
                    float length = 0f;
                    for (int i = 0; i < line.Count; i++)
                    {
                        pts.Add(new PointF(ox + line[i].X * k, oy + line[i].Y * k));
                        if (i > 0) length += Dist(pts[i - 1], pts[i]);
                    }
                    if (length < textWidth * 1.2f + 8f) continue;          // too short for the name: no label
                    float dx = pts[pts.Count - 1].X - pts[0].X, dy = pts[pts.Count - 1].Y - pts[0].Y;
                    // reading left to right (vertical roads: bottom to top); the text stays above the line
                    if (dx < 0 || (Math.Abs(dx) < 1e-3f && dy > 0)) pts.Reverse();
                    var label = VectorText.OnPath(name, pts, size, new VectorTextPathOptions { Align = VectorTextAlign.Center, Above = true });
                    foreach (var s in label.Shapes) into.Add((LabelRank, s));
                }
            }
            else if (layer == "place" && f.GeomType == 1)
            {
                float size = PlaceLabelSize(f.Class, zoom);
                if (size <= 0f) return;
                float textWidth = VectorText.Width(name, size);
                foreach (var pt in f.Geoms)
                {
                    if (pt.Count == 0) continue;
                    float x = ox + pt[0].X * k, y = oy + pt[0].Y * k;
                    // straight text: a horizontal path through the point, text centred on it
                    float py = y + size * 0.5f;
                    var pts = new List<PointF> { new PointF(x - textWidth * 0.5f - 2f, py), new PointF(x + textWidth * 0.5f + 2f, py) };
                    var label = VectorText.OnPath(name, pts, size, new VectorTextPathOptions { Align = VectorTextAlign.Center, Above = true });
                    foreach (var s in label.Shapes) into.Add((LabelRank, s));
                }
            }
        }

        static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

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
