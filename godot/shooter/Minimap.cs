using Godot;
using Suchohrad.ShooterCore;
using Vector2 = Godot.Vector2;
using NVector2 = System.Numerics.Vector2;

namespace Suchohrad.Shooter;

/// <summary>
/// Top-down minimap (ticket #119). The base texture is rasterised once per map from the client's own map data (parcels as
/// land-use fills, roads.json as lines) and cached; each frame only the view transform, the player arrow and a few dots are
/// drawn, with no per-frame allocations. The view turns with the player so their facing is up.
/// Dots: the player arrow (fixed at the centre), vehicles (squares, <see cref="SetVehicleDots"/>) and enemy pings (circles,
/// <see cref="SetEnemyPings"/>, the hook the UAV streak uses; pings expire on their own).
/// Positions are ground (x, z) in metres, the same axes as the game's world (z = -north).
/// </summary>
public sealed partial class Minimap : Control
{
    /// <summary>Texels across the map's longer side (plus a margin). About 2 m per texel on the village map.</summary>
    public const int TextureSize = 512;
    const float PadMetres = 10f;
    const float ArrowHalf = 9f;

    /// <summary>Half the view's width in metres (zoom). A smaller value zooms in.</summary>
    public float ZoomMetres { get; set; } = 90f;

    ImageTexture? _base;
    Vector2 _origin;   // ground (x, z) of texel (0, 0)
    float _metresPerTexel = 1f;
    Vector2 _player;
    float _yaw;
    bool _hasPlayer;
    readonly List<NVector2> _vehicles = new();
    readonly MinimapPings _pings = new();
    static readonly Vector2[] Arrow = { new(0, -ArrowHalf), new(ArrowHalf * 0.8f, ArrowHalf), new(0, ArrowHalf * 0.4f), new(-ArrowHalf * 0.8f, ArrowHalf) };

    public Minimap()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        CustomMinimumSize = new Vector2(224, 224);
    }

    /// <summary>
    /// Builds the base texture from the map's parcels and roads. Call once per loaded map; returns false when the map has
    /// neither (the minimap then shows the player only). Cost: one pass over the parcels' bounding boxes and the road lines.
    /// </summary>
    public bool Build(Suchohrad.MapLoader.MapLoader? map)
    {
        _base = null;
        if (map == null) return false;
        var roads = RoadNetwork.Parse(map.RoadsJson);
        var roadPolylines = new List<Vector2[]>(roads.Count);
        var box = default(Rect2);
        var any = false;
        void Grow(Rect2 r) { box = any ? box.Merge(r) : r; any = true; }
        foreach (var p in map.Parcels.Values) Grow(p.Bounds);
        foreach (var r in roads)
        {
            var g = new Vector2[r.Points.Length];
            for (var i = 0; i < g.Length; i++) g[i] = new Vector2(r.Points[i].X, -r.Points[i].Y); // local north-up to ground z
            foreach (var q in g) Grow(new Rect2(q, Vector2.Zero));
            roadPolylines.Add(g);
        }
        if (!any) return false;

        var side = Math.Max(box.Size.X, box.Size.Y) + 2 * PadMetres;
        _origin = box.GetCenter() - new Vector2(side / 2, side / 2);
        _metresPerTexel = side / TextureSize;
        var img = Image.CreateEmpty(TextureSize, TextureSize, false, Image.Format.Rgba8);
        img.Fill(Terrain);

        foreach (var parcel in map.Parcels.Values)
        {
            var colour = ParcelColour(parcel.Kind);
            var b = parcel.Bounds;
            var u0 = Math.Clamp((int)Math.Floor((b.Position.X - _origin.X) / _metresPerTexel), 0, TextureSize - 1);
            var u1 = Math.Clamp((int)Math.Ceiling((b.End.X - _origin.X) / _metresPerTexel), 0, TextureSize - 1);
            var v0 = Math.Clamp((int)Math.Floor((b.Position.Y - _origin.Y) / _metresPerTexel), 0, TextureSize - 1);
            var v1 = Math.Clamp((int)Math.Ceiling((b.End.Y - _origin.Y) / _metresPerTexel), 0, TextureSize - 1);
            for (var v = v0; v <= v1; v++)
                for (var u = u0; u <= u1; u++)
                    if (parcel.Contains(TexelCentre(u, v))) img.SetPixel(u, v, colour);
        }

        for (var i = 0; i < roads.Count; i++)
        {
            var radius = Math.Max(1f, roads[i].WidthM / _metresPerTexel / 2f);
            var colour = RoadColour(roads[i].Highway);
            var pts = roadPolylines[i];
            for (var s = 0; s + 1 < pts.Length; s++) StampSegment(img, pts[s], pts[s + 1], radius, colour);
        }

        _base = ImageTexture.CreateFromImage(img);
        return true;
    }

    /// <summary>The player's ground position (x, z) and yaw, in the game's convention. Call every frame.</summary>
    public void SetPlayer(Vector2 groundXZ, float yaw)
    {
        _player = groundXZ;
        _yaw = yaw;
        _hasPlayer = true;
    }

    /// <summary>Vehicle positions (ground x, z) shown as squares. Copied, so the caller may reuse its list.</summary>
    public void SetVehicleDots(IReadOnlyList<Vector2> groundXZ)
    {
        _vehicles.Clear();
        for (var i = 0; i < groundXZ.Count; i++) _vehicles.Add(new NVector2(groundXZ[i].X, groundXZ[i].Y));
    }

    /// <summary>
    /// Shows enemy pings (circles) until <paramref name="expiry"/> on the Time.GetTicksMsec()/1000 clock. A new call replaces
    /// the previous set. This is the UAV streak's layer.
    /// </summary>
    public void SetEnemyPings(IReadOnlyList<Vector2> groundXZ, double expiry)
    {
        var copy = new NVector2[groundXZ.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = new NVector2(groundXZ[i].X, groundXZ[i].Y);
        _pings.Set(copy, expiry);
    }

    public void ClearEnemyPings() => _pings.Clear();

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Backdrop);
        var half = Math.Min(size.X, size.Y) / 2f;
        if (half <= 0) return;
        var centre = size / 2f;
        var now = Time.GetTicksMsec() / 1000.0;
        if (_hasPlayer)
        {
            var theta = MinimapMath.RotationFor(_yaw);
            var pxPerMetre = MinimapMath.PxPerMetre(half, ZoomMetres);
            if (_base is { } tex)
            {
                var k = pxPerMetre * _metresPerTexel;
                var playerTexel = (_player - _origin) / _metresPerTexel;
                var extent = TextureSize * k;
                DrawSetTransform(centre, theta, Vector2.One);
                DrawTextureRect(tex, new Rect2(-playerTexel * k, new Vector2(extent, extent)), false);
                DrawSetTransform(Vector2.Zero);
            }
            var inner = half - 6f;
            for (var i = 0; i < _vehicles.Count; i++)
            {
                var p = centre + Screen(_vehicles[i], theta, pxPerMetre, inner);
                DrawRect(new Rect2(p - new Vector2(4, 4), new Vector2(8, 8)), ShooterTheme.Warn);
            }
            var pings = _pings.Positions(now);
            for (var i = 0; i < pings.Count; i++)
            {
                var p = centre + Screen(pings[i], theta, pxPerMetre, inner);
                DrawCircle(p, 6f, ShooterTheme.Outline);
                DrawCircle(p, 4f, ShooterTheme.Danger);
            }
        }
        DrawSetTransform(centre);
        DrawColoredPolygon(Arrow, ShooterTheme.Text);
        DrawSetTransform(Vector2.Zero);
        DrawRect(new Rect2(Vector2.Zero, size), ShooterTheme.Line, false, ShooterTheme.Border);
    }

    /// <summary>Screen offset from the centre of a ground point, clamped into the square of half-size <paramref name="inner"/>.</summary>
    Vector2 Screen(NVector2 worldXZ, float theta, float pxPerMetre, float inner)
    {
        var d = worldXZ - new NVector2(_player.X, _player.Y);
        var p = MinimapMath.ClampToSquare(MinimapMath.ToScreen(d, theta, pxPerMetre), inner);
        return new Vector2(p.X, p.Y);
    }

    Vector2 TexelCentre(int u, int v) => _origin + new Vector2((u + 0.5f) * _metresPerTexel, (v + 0.5f) * _metresPerTexel);

    void StampSegment(Image img, Vector2 a, Vector2 b, float radius, Color colour)
    {
        var ta = (a - _origin) / _metresPerTexel;
        var tb = (b - _origin) / _metresPerTexel;
        var steps = Math.Max(1, (int)Math.Ceiling(ta.DistanceTo(tb) * 2f));
        var r = (int)Math.Ceiling(radius);
        for (var s = 0; s <= steps; s++)
        {
            var p = ta.Lerp(tb, s / (float)steps);
            var cx = (int)Math.Round(p.X);
            var cy = (int)Math.Round(p.Y);
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius) continue;
                    var x = cx + dx; var y = cy + dy;
                    if (x >= 0 && y >= 0 && x < TextureSize && y < TextureSize) img.SetPixel(x, y, colour);
                }
        }
    }

    static readonly Color Backdrop = new(0.05f, 0.06f, 0.08f, 0.85f);
    static readonly Color Terrain = new(0.20f, 0.23f, 0.19f);

    static Color ParcelColour(string kind) => kind switch
    {
        "building_plot" => new Color(0.56f, 0.54f, 0.50f),
        "garden" => new Color(0.30f, 0.42f, 0.27f),
        "forest" => new Color(0.16f, 0.28f, 0.18f),
        "water" => new Color(0.22f, 0.36f, 0.52f),
        "road" => new Color(0.30f, 0.30f, 0.31f),
        _ => new Color(0.26f, 0.28f, 0.25f),
    };

    static Color RoadColour(string highway) => highway switch
    {
        "path" or "footway" or "track" => new Color(0.58f, 0.48f, 0.34f),
        "residential" => new Color(0.82f, 0.82f, 0.80f),
        _ => new Color(0.90f, 0.84f, 0.55f),
    };
}

/// <summary>Minimap placement in the HUD: top-left, under the control hint line.</summary>
public partial class Hud
{
    Minimap _minimap = null!;

    /// <summary>The HUD's minimap: build it with the map, set the player each frame, feed enemy pings.</summary>
    public Minimap Minimap => _minimap;

    void BuildMinimap()
    {
        _minimap = Place(new Minimap(), 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32, 224, 224);
        _root.AddChild(_minimap);
    }
}
