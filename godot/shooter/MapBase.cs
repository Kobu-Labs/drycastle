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
/// The map data both the minimap and the tactical map draw: the square ground extent that covers every parcel and road, the
/// parcel colours rasterised into a mipmapped terrain texture, and the roads as polylines. One instance per loaded map
/// (<see cref="For"/>), so the two views share the roads and the textures: a texture is built once per side and kept, and a
/// second join on the same map does not rasterise again (#170, T2-19). Positions are ground (x, z) in metres, the game's
/// axes (north is -z); texel (u, v) is ground (x, z) relative to <see cref="Origin"/>.
/// </summary>
public sealed class MapBase
{
    const float PadMetres = 10f;
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

    MapBase(Drycastle.MapLoader.MapLoader map) => _map = map;

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
        var box = default(Rect2);
        var any = false;
        void Grow(Rect2 r) { box = any ? box.Merge(r) : r; any = true; }
        foreach (var p in _map.Parcels.Values) Grow(p.Bounds);
        foreach (var r in roads)
        {
            var g = new Vector2[r.Points.Length];
            for (var i = 0; i < g.Length; i++) g[i] = new Vector2(r.Points[i].X, -r.Points[i].Y); // local north-up to ground z
            var bounds = new Rect2(g[0], Vector2.Zero);
            foreach (var q in g) bounds = bounds.Expand(q);
            Grow(bounds);
            _roads.Add(new MapRoad { Ground = g, Screen = new Vector2[g.Length], Bounds = bounds, WidthM = r.WidthM, Colour = RoadColour(r.Highway) });
        }
        if (!any) return false;
        SideMetres = Math.Max(box.Size.X, box.Size.Y) + 2 * PadMetres;
        Origin = box.GetCenter() - new Vector2(SideMetres / 2, SideMetres / 2);
        return true;
    }

    /// <summary>The terrain texture of <paramref name="side"/> texels, rasterised on first request and then kept.</summary>
    public ImageTexture Texture(int side)
    {
        if (_textures.TryGetValue(side, out var tex)) return tex;
        if (_textures.Count >= MaxTextures) _textures.Clear();
        tex = Rasterise(side);
        _textures[side] = tex;
        return tex;
    }

    static byte[] Rgba(Color c) => new[] { (byte)Math.Round(c.R * 255f), (byte)Math.Round(c.G * 255f), (byte)Math.Round(c.B * 255f), (byte)Math.Round(c.A * 255f) };

    /// <summary>Rasterises the parcel fills into a square of <paramref name="side"/> texels, with mipmaps. Each row takes its edge crossings once (scanline fill), and the image is built from one byte buffer.</summary>
    ImageTexture Rasterise(int side)
    {
        var metresPerTexel = SideMetres / side;
        var px = new byte[side * side * 4];
        var terrain = Rgba(Terrain);
        for (var i = 0; i < px.Length; i += 4) Buffer.BlockCopy(terrain, 0, px, i, 4);
        foreach (var parcel in _map.Parcels.Values)
            Fill(px, side, metresPerTexel, Rgba(ParcelColour(parcel.Kind)), parcel.Rings.Select(r => r.Select(v => new NVector2(v.X, v.Y)).ToArray()).ToList(), parcel.Bounds);
        foreach (var region in _map.RegionPaints)
        {
            var box = new Rect2(region.Ring[0], Vector2.Zero);
            foreach (var v in region.Ring) box = box.Expand(v);
            Fill(px, side, metresPerTexel, Rgba(region.Colour), new List<NVector2[]> { region.Ring.Select(v => new NVector2(v.X, v.Y)).ToArray() }, box);
        }
        using var img = Image.CreateFromData(side, side, false, Image.Format.Rgba8, px);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

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
