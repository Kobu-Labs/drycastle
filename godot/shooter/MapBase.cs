using Godot;
using Drycastle.ShooterCore;
using Vector2 = Godot.Vector2;
using NVector2 = System.Numerics.Vector2;

namespace Drycastle.Shooter;

/// <summary>A road as geometry: ground points (x, z), a scratch buffer for its screen points (reused every draw), its bounds, width and colour.</summary>
public sealed class MapRoad
{
    public Vector2[] Ground = Array.Empty<Vector2>();
    public Vector2[] Screen = Array.Empty<Vector2>();
    public Rect2 Bounds;
    public float WidthM;
    public Color Colour;
}

/// <summary>
/// The map data both the minimap and the tactical map draw: the square ground extent that covers every parcel and road (or, for the tactical map's <see cref="World"/>, the whole terrain), the
/// parcel colours rasterised into a mipmapped terrain texture (with the map's top-down render over it when the map folder has one), and the roads as polylines. One instance per loaded map
/// (<see cref="For"/>), so the two views share the roads and the textures: a texture is built once per side and kept, and a
/// second join on the same map does not rasterise again (#170, T2-19). Positions are ground (x, z) in metres, the game's
/// axes (north is -z); texel (u, v) is ground (x, z) relative to <see cref="Origin"/>.
/// </summary>
public sealed class MapBase
{
    /// <summary>Terrain textures kept per map. The views hold their own reference, so dropping an entry from the cache never frees a texture in use.</summary>
    const int MaxTextures = 3;

    static Drycastle.MapLoader.MapLoader? _cachedFor;
    static MapBase? _cached;

    readonly Drycastle.MapLoader.MapLoader _map;
    readonly Dictionary<int, ImageTexture> _textures = new();
    readonly List<MapRoad> _roads = new();
    readonly List<float> _crossings = new();
    readonly List<(int From, int To)> _spans = new();

    /// <summary>Side of the square extent, metres.</summary>
    public float SideMetres { get; private set; }
    /// <summary>Ground (x, z) of the extent's north-west corner (texel 0, 0).</summary>
    public Vector2 Origin { get; private set; }
    /// <summary>Ground z of the south edge (north is -z).</summary>
    public float SouthZ => Origin.Y + SideMetres;
    public IReadOnlyList<MapRoad> Roads => _roads;

    readonly bool _world;
    MapBase? _worldTwin;
    readonly Dictionary<int, Task<Image?>> _building = new();
    readonly object _topDownLock = new();

    MapBase(Drycastle.MapLoader.MapLoader map, bool world = false) { _map = map; _world = world; }

    /// <summary>
    /// The base of the whole world for the full-screen tactical map: the same data over the square that covers the whole terrain
    /// (the world is the map outline, a polygon, far bigger than the parcels' box that the minimap and the Intel screen show), with
    /// the top-down picture over every pixel it has. This instance itself when it already is one, or when the map has no terrain.
    /// </summary>
    public MapBase World
    {
        get
        {
            if (_world) return this;
            if (_worldTwin == null)
            {
                var w = new MapBase(_map, true);
                _worldTwin = w.Parse() ? w : this;
            }
            return _worldTwin;
        }
    }

    /// <summary>The shared base of <paramref name="map"/>, or null when the map is missing or has no parcels or roads (the views then show markers only).</summary>
    public static MapBase? For(Drycastle.MapLoader.MapLoader? map)
    {
        if (map == null) return null;
        if (ReferenceEquals(_cachedFor, map)) return _cached;
        var b = new MapBase(map);
        _cached = b.Parse() ? b : null;
        _cachedFor = map;
        return _cached;
    }

    bool Parse()
    {
        var roads = RoadNetwork.Parse(_map.RoadsJson);
        // the extent is the parcels' box only: the parcels are the village polygon, while the road network and the terrain reach far past it
        var box = default(Rect2);
        var any = false;
        foreach (var p in _map.Parcels.Values) { box = any ? box.Merge(p.Bounds) : p.Bounds; any = true; }
        if (!any) return false;
        if (_world)
            foreach (var mi in _map.TerrainVisuals)
            {
                if (!GodotObject.IsInstanceValid(mi) || mi.Mesh is not { } mesh) continue;
                var a = (mi.IsInsideTree() ? mi.GlobalTransform : mi.Transform) * mesh.GetAabb();
                box = box.Merge(new Rect2(a.Position.X, a.Position.Z, a.Size.X, a.Size.Z));
            }
        var sheet = TacticalMapMath.Sheet(box.Position.X, box.Position.Y, box.End.X, box.End.Y); // the same sheet the server measures the Scud event's search area on
        SideMetres = sheet.Side;
        Origin = new Vector2(sheet.Origin.X, sheet.Origin.Y);
        var extent = new Rect2(Origin, new Vector2(SideMetres, SideMetres));
        foreach (var r in roads)
        {
            var g = new Vector2[r.Points.Length];
            for (var i = 0; i < g.Length; i++) g[i] = new Vector2(r.Points[i].X, -r.Points[i].Y); // local north-up to ground z
            foreach (var part in ClipPolyline(g, extent).SelectMany(c => _world && _map.TerrainBox != null ? ClipToTerrain(c) : new List<Vector2[]> { c }))
            {
                var bounds = new Rect2(part[0], Vector2.Zero);
                foreach (var q in part) bounds = bounds.Expand(q);
                _roads.Add(new MapRoad { Ground = part, Screen = new Vector2[part.Length], Bounds = bounds, WidthM = r.WidthM, Colour = RoadColour(r.Highway) });
            }
        }
        return true;
    }

    /// <summary>The pieces of <paramref name="line"/> inside <paramref name="r"/> (a road leaves the extent and may come back; each re-entry starts a new piece). Segments are cut at the rectangle's edge.</summary>
    static List<Vector2[]> ClipPolyline(Vector2[] line, Rect2 r)
    {
        var parts = new List<Vector2[]>();
        var cur = new List<Vector2>();
        void Flush() { if (cur.Count >= 2) parts.Add(cur.ToArray()); cur.Clear(); }
        for (var i = 0; i + 1 < line.Length; i++)
        {
            if (!ClipSegment(line[i], line[i + 1], r, out var a, out var b)) { Flush(); continue; }
            if (cur.Count > 0 && cur[^1].DistanceSquaredTo(a) > 1e-6f) Flush();
            if (cur.Count == 0) cur.Add(a);
            cur.Add(b);
        }
        Flush();
        return parts;
    }

    /// <summary>
    /// The pieces of <paramref name="line"/> that lie on the terrain (the world sheet only): the road network reaches far past the terrain's
    /// outline, and a road nobody can reach is not drawn. Each segment is walked in steps of at most <see cref="TerrainStep"/> metres and a
    /// crossing of the outline is narrowed down by bisection.
    /// </summary>
    List<Vector2[]> ClipToTerrain(Vector2[] line)
    {
        bool On(Vector2 p) => !float.IsNaN(_map.GroundHeight(p.X, p.Y));
        Vector2 Crossing(Vector2 on, Vector2 off)
        {
            for (var k = 0; k < 6; k++) { var m = (on + off) / 2; if (On(m)) on = m; else off = m; }
            return on;
        }
        var parts = new List<Vector2[]>();
        var cur = new List<Vector2>();
        void Flush() { if (cur.Count >= 2) parts.Add(cur.ToArray()); cur.Clear(); }
        var prev = line[0];
        var prevOn = On(prev);
        if (prevOn) cur.Add(prev);
        for (var i = 0; i + 1 < line.Length; i++)
        {
            var steps = Math.Max(1, (int)MathF.Ceiling(line[i].DistanceTo(line[i + 1]) / TerrainStep));
            for (var k = 1; k <= steps; k++)
            {
                var p = line[i].Lerp(line[i + 1], (float)k / steps);
                var on = On(p);
                if (on && !prevOn) cur.Add(Crossing(p, prev));
                else if (!on && prevOn) { cur.Add(Crossing(prev, p)); Flush(); }
                if (on && k == steps) cur.Add(p); // the line's own vertices are kept; the samples between them are not
                prev = p; prevOn = on;
            }
        }
        Flush();
        return parts;
    }
    const float TerrainStep = 8f;

    /// <summary>Liang-Barsky: the part of segment p0-p1 inside <paramref name="r"/>; false when none.</summary>
    static bool ClipSegment(Vector2 p0, Vector2 p1, Rect2 r, out Vector2 a, out Vector2 b)
    {
        float t0 = 0f, t1 = 1f, dx = p1.X - p0.X, dy = p1.Y - p0.Y;
        bool Edge(float p, float q)
        {
            if (p == 0f) return q >= 0f;
            var t = q / p;
            if (p < 0f) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        a = b = default;
        if (!(Edge(-dx, p0.X - r.Position.X) && Edge(dx, r.End.X - p0.X) && Edge(-dy, p0.Y - r.Position.Y) && Edge(dy, r.End.Y - p0.Y))) return false;
        a = new Vector2(p0.X + t0 * dx, p0.Y + t0 * dy);
        b = new Vector2(p0.X + t1 * dx, p0.Y + t1 * dy);
        return true;
    }

    /// <summary>Terrain height at ground (x, z), the map's mean where the terrain has no triangle.</summary>
    public float HeightAt(float x, float z) => _map.GroundHeightOrMean(x, z);

    /// <summary>The terrain texture of <paramref name="side"/> texels, rasterised on first request and then kept.</summary>
    public ImageTexture Texture(int side)
    {
        if (_textures.TryGetValue(side, out var tex)) return tex;
        if (_textures.Count >= MaxTextures) _textures.Clear();
        tex = Rasterise(side);
        _textures[side] = tex;
        return tex;
    }

    /// <summary>
    /// A texture near <paramref name="wanted"/> texels (within <see cref="MinimapView.NeedsRebuild"/>'s tolerance), built at <paramref name="built"/> texels, or null while it is being made. The world sheet with a top-down picture
    /// is rasterised on a worker thread (decoding and resizing the picture would stall a frame for a second), so the caller asks again next frame; every other base makes it at once. Starts a build when none is near.
    /// </summary>
    public ImageTexture? TryTexture(int wanted, out int built)
    {
        foreach (var (k, t) in _textures)
            if (!MinimapView.NeedsRebuild(k, wanted)) { built = k; return t; }
        built = wanted;
        if (!_world || !HasTopDownFiles) return Texture(wanted);
        foreach (var (k, task) in _building.ToList())
        {
            if (MinimapView.NeedsRebuild(k, wanted)) continue;
            if (!task.IsCompleted) { built = k; return null; } // on its way
            _building.Remove(k);
            built = k;
            var img = task.IsCompletedSuccessfully ? task.Result : null;
            if (img == null) return Texture(k); // the picture was unreadable: the terrain colours, made here
            if (_textures.Count >= MaxTextures) _textures.Clear();
            return _textures[k] = ImageTexture.CreateFromImage(img);
        }
        _building[wanted] = Task.Run(() => RasteriseWorld(wanted));
        return null;
    }

    /// <summary>Starts building the world sheet's texture near <paramref name="wanted"/> texels without waiting for it (the map is not open yet).</summary>
    public void Prewarm(int wanted) => TryTexture(wanted, out _);

    bool HasTopDownFiles => File.Exists(Path.Combine(_map.MapDir, TopDownImage.PngName)) && File.Exists(Path.Combine(_map.MapDir, TopDownImage.JsonName));

    /// <summary>The world sheet from the top-down picture alone (it holds the terrain colours too), on a worker thread: no scene object is touched. Null when the picture cannot be read.</summary>
    Image? RasteriseWorld(int side)
    {
        try
        {
            var px = new byte[side * side * 4];
            var terrain = Rgba(Terrain);
            for (var i = 0; i < px.Length; i += 4) Buffer.BlockCopy(terrain, 0, px, i, 4);
            if (TopDownSource() is null) return null;
            PaintTopDown(px, null, side, SideMetres / side);
            var img = Image.CreateFromData(side, side, false, Image.Format.Rgba8, px);
            img.GenerateMipmaps();
            return img;
        }
        catch (Exception e) { GD.PushWarning($"tactical map: could not build the world sheet ({e.Message})"); return null; }
    }

    static byte[] Rgba(Color c) => new[] { (byte)Math.Round(c.R * 255f), (byte)Math.Round(c.G * 255f), (byte)Math.Round(c.B * 255f), (byte)Math.Round(c.A * 255f) };

    /// <summary>Rasterises the parcel fills into a square of <paramref name="side"/> texels, with mipmaps. Each row takes its edge crossings once (scanline fill), and the image is built from one byte buffer.</summary>
    ImageTexture Rasterise(int side)
    {
        var metresPerTexel = SideMetres / side;
        var px = new byte[side * side * 4];
        var terrain = Rgba(Terrain);
        for (var i = 0; i < px.Length; i += 4) Buffer.BlockCopy(terrain, 0, px, i, 4);
        // the real ground colour from the terrain mesh wherever the map has terrain; the default shows where it is cut off. A map with no terrain mesh falls back to flat parcel colours.
        if (!PaintTerrain(px, side, metresPerTexel))
        {
            foreach (var parcel in _map.Parcels.Values)
                Fill(px, side, metresPerTexel, Rgba(ParcelColour(parcel.Kind)), parcel.Rings.Select(r => r.Select(v => new NVector2(v.X, v.Y)).ToArray()).ToList(), parcel.Bounds);
        }
        foreach (var patch in _map.PatchPaints)
        {
            var box = new Rect2(patch.Ring[0], Vector2.Zero);
            foreach (var v in patch.Ring) box = box.Expand(v);
            Fill(px, side, metresPerTexel, Rgba(patch.Colour), new List<NVector2[]> { patch.Ring.Select(v => new NVector2(v.X, v.Y)).ToArray() }, box);
        }
        var inside = ParcelMask(side, metresPerTexel);
        for (var i = 0; i < px.Length; i += 4)
            if (!_world && inside[i + 3] == 0) Buffer.BlockCopy(terrain, 0, px, i, 4); // the world sheet keeps the terrain's own colours past the parcels
        PaintTopDown(px, null, side, metresPerTexel); // the picture over every texel it has, as on the tactical map
        using var img = Image.CreateFromData(side, side, false, Image.Format.Rgba8, px);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>RGBA mask of <paramref name="side"/> texels: alpha 255 inside a parcel. The ground outside every parcel is not part of the game: it takes the plain terrain colour, so the terrain mesh and the patch paints that reach past the village polygon do not show.</summary>
    byte[] ParcelMask(int side, float metresPerTexel)
    {
        var inside = new byte[side * side * 4];
        foreach (var parcel in _map.Parcels.Values)
            Fill(inside, side, metresPerTexel, new byte[] { 255, 255, 255, 255 }, parcel.Rings.Select(r => r.Select(v => new NVector2(v.X, v.Y)).ToArray()).ToList(), parcel.Bounds);
        return inside;
    }

    /// <summary>
    /// The map's top-down render (<c>tactical-map.png</c> + sidecar in the map folder, <see cref="TopDownImage"/>) over every texel
    /// inside a parcel (every texel when <paramref name="inside"/> is null): buildings, roofs and props as the world looks from above, which the terrain colours do not have. Resampled
    /// to this texture's scale, alpha-blended (the picture is transparent outside the terrain). Nothing happens when the map has
    /// no picture or it cannot be read: the terrain colours stay.
    /// </summary>
    void PaintTopDown(byte[] px, byte[]? inside, int side, float metresPerTexel)
    {
        var src = TopDownSource();
        if (src is not { } s) return;
        var (picture, ext) = s;
        var (x, y, w, h) = ext.Placement(Origin.X, Origin.Y, SideMetres, side);
        var rw = Math.Max(1, (int)Math.Round(w));
        var rh = Math.Max(1, (int)Math.Round(h));
        if ((long)rw * rh > 64L * 1024 * 1024) return; // not a picture the sheet should hold
        using var scaled = (Image)picture.Duplicate();
        scaled.Resize(rw, rh, rw < picture.GetWidth() ? Image.Interpolation.Lanczos : Image.Interpolation.Bilinear);
        var data = scaled.GetData();
        var ox = (int)Math.Round(x);
        var oy = (int)Math.Round(y);
        for (var v = Math.Max(0, oy); v < Math.Min(side, oy + rh); v++)
            for (var u = Math.Max(0, ox); u < Math.Min(side, ox + rw); u++)
            {
                var o = (v * side + u) * 4;
                if (inside != null && inside[o + 3] == 0) continue;
                var q = ((v - oy) * rw + (u - ox)) * 4;
                int a = data[q + 3];
                if (a == 0) continue;
                for (var c = 0; c < 3; c++) px[o + c] = (byte)((data[q + c] * a + px[o + c] * (255 - a) + 127) / 255);
            }
    }

    Image? _topDown;
    TopDownImage? _topDownExtent;
    bool _topDownLoaded;

    /// <summary>The top-down picture (RGBA8) and its extent, read once from the map folder; null when the map has none (older maps) or it is unreadable.</summary>
    (Image Picture, TopDownImage Extent)? TopDownSource()
    {
        lock (_topDownLock) return TopDownSourceLocked();
    }

    (Image Picture, TopDownImage Extent)? TopDownSourceLocked()
    {
        if (!_topDownLoaded)
        {
            _topDownLoaded = true;
            try
            {
                var png = Path.Combine(_map.MapDir, TopDownImage.PngName);
                var json = Path.Combine(_map.MapDir, TopDownImage.JsonName);
                if (File.Exists(png) && File.Exists(json) && TopDownImage.Parse(File.ReadAllText(json)) is { } ext)
                {
                    var img = Image.LoadFromFile(png);
                    if (img != null && img.GetWidth() == ext.WidthPx && img.GetHeight() == ext.HeightPx)
                    {
                        if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
                        _topDown = img;
                        _topDownExtent = ext;
                    }
                    else GD.PushWarning($"tactical map: {png} does not match {json}; using the terrain colours");
                }
            }
            catch (Exception e) { GD.PushWarning($"tactical map: top-down picture unreadable ({e.Message}); using the terrain colours"); }
        }
        return _topDown != null && _topDownExtent is { } t ? (_topDown, t) : null;
    }

    /// <summary>Rasterises the terrain meshes' triangles with their interpolated vertex colours (top-down, so each texel is the ground colour at that point). False when there is no coloured terrain.</summary>
    bool PaintTerrain(byte[] px, int side, float metresPerTexel)
    {
        var any = false;
        foreach (var mi in _map.TerrainVisuals)
        {
            if (!GodotObject.IsInstanceValid(mi) || mi.Mesh is not { } mesh) continue;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var arrays = mesh.SurfaceGetArrays(s);
                var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var col = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
                var idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                if (col.Length != v.Length) continue;
                var n = idx.Length > 0 ? idx.Length : v.Length;
                for (var t = 0; t + 2 < n; t += 3)
                {
                    var a = idx.Length > 0 ? idx[t] : t;
                    var b = idx.Length > 0 ? idx[t + 1] : t + 1;
                    var c = idx.Length > 0 ? idx[t + 2] : t + 2;
                    Triangle(px, side, metresPerTexel, v[a], v[b], v[c], col[a], col[b], col[c]);
                }
                any = true;
            }
        }
        return any;
    }

    /// <summary>One triangle (ground x, z from the vertices' X and Z) into the texel grid: every texel centre inside it takes the barycentric blend of the three colours.</summary>
    void Triangle(byte[] px, int side, float mpt, Vector3 a, Vector3 b, Vector3 c, Color ca, Color cb, Color cc)
    {
        float ax = (a.X - Origin.X) / mpt, ay = (a.Z - Origin.Y) / mpt;
        float bx = (b.X - Origin.X) / mpt, by = (b.Z - Origin.Y) / mpt;
        float cx = (c.X - Origin.X) / mpt, cy = (c.Z - Origin.Y) / mpt;
        var d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
        if (Math.Abs(d) < 1e-9f) return;
        var u0 = Math.Max(0, (int)Math.Ceiling(Math.Min(ax, Math.Min(bx, cx)) - 0.5f));
        var u1 = Math.Min(side - 1, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx)) - 0.5f));
        var v0 = Math.Max(0, (int)Math.Ceiling(Math.Min(ay, Math.Min(by, cy)) - 0.5f));
        var v1 = Math.Min(side - 1, (int)Math.Floor(Math.Max(ay, Math.Max(by, cy)) - 0.5f));
        const float e = -1e-4f;
        for (var v = v0; v <= v1; v++)
            for (var u = u0; u <= u1; u++)
            {
                float x = u + 0.5f, y = v + 0.5f;
                var l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d;
                var l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d;
                var l3 = 1f - l1 - l2;
                if (l1 < e || l2 < e || l3 < e) continue;
                var o = (v * side + u) * 4;
                px[o] = ToByte(l1 * ca.R + l2 * cb.R + l3 * cc.R);
                px[o + 1] = ToByte(l1 * ca.G + l2 * cb.G + l3 * cc.G);
                px[o + 2] = ToByte(l1 * ca.B + l2 * cb.B + l3 * cc.B);
                px[o + 3] = 255;
            }
    }

    static byte ToByte(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

    void Fill(byte[] px, int side, float metresPerTexel, byte[] colour, List<NVector2[]> rings, Rect2 b)
    {
        var u0 = Math.Clamp((int)Math.Floor((b.Position.X - Origin.X) / metresPerTexel), 0, side - 1);
        var u1 = Math.Clamp((int)Math.Ceiling((b.End.X - Origin.X) / metresPerTexel), 0, side - 1);
        var v0 = Math.Clamp((int)Math.Floor((b.Position.Y - Origin.Y) / metresPerTexel), 0, side - 1);
        var v1 = Math.Clamp((int)Math.Ceiling((b.End.Y - Origin.Y) / metresPerTexel), 0, side - 1);
        for (var v = v0; v <= v1; v++)
        {
            ScanlineFill.Crossings(rings, Origin.Y + (v + 0.5f) * metresPerTexel, _crossings);
            ScanlineFill.Spans(_crossings, Origin.X, metresPerTexel, u0, u1, _spans);
            foreach (var (from, to) in _spans)
                for (var u = from; u <= to; u++) Buffer.BlockCopy(colour, 0, px, (v * side + u) * 4, 4);
        }
    }

    public static readonly Color Terrain = new(0.20f, 0.23f, 0.19f);

    public static Color ParcelColour(string kind) => kind switch
    {
        "building_plot" => new Color(0.56f, 0.54f, 0.50f),
        "garden" => new Color(0.30f, 0.42f, 0.27f),
        "forest" => new Color(0.16f, 0.28f, 0.18f),
        "water" => new Color(0.22f, 0.36f, 0.52f),
        "road" => new Color(0.30f, 0.30f, 0.31f),
        _ => new Color(0.26f, 0.28f, 0.25f),
    };

    public static Color RoadColour(string highway) => highway switch
    {
        "path" or "footway" or "track" => new Color(0.58f, 0.48f, 0.34f),
        "residential" => new Color(0.82f, 0.82f, 0.80f),
        _ => new Color(0.90f, 0.84f, 0.55f),
    };
}
