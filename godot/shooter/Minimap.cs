using Godot;
using Suchohrad.ShooterCore;
using Vector2 = Godot.Vector2;
using NVector2 = System.Numerics.Vector2;

namespace Suchohrad.Shooter;

/// <summary>
/// Top-down minimap (ticket #119, quality and orientation in #150). Two layers:
/// - Terrain and parcel fills: one mipmapped texture per map, sized from the on-screen scale (UI scale times device pixel
///   ratio), rebuilt only when that scale changes by a lot. Sampled with linear filtering, so it is not blocky when zoomed.
/// - Roads: anti-aliased polylines drawn each frame in screen space, culled to the view, so they stay crisp at any scale.
/// Dots: the player arrow (fixed at the centre), vehicles (squares), enemy pings (circles, the UAV streak's layer, expiring
/// on their own), and a north marker "N" on the rim. The view turns to the player's view direction (see
/// <see cref="MinimapView"/>), eased along the shorter arc.
/// Positions are ground (x, z) in metres, the same axes as the game's world (north is -z).
/// </summary>
public sealed partial class Minimap : Control
{
    /// <summary>Texture side limits in texels. The side follows the on-screen scale inside these bounds.</summary>
    public const int MinTextureSide = 512, MaxTextureSide = 2048;
    /// <summary>How fast the shown yaw eases to the view yaw, per second (about 0.15 s to settle).</summary>
    public const float FollowRate = 14f;
    const float PadMetres = 10f;
    const float ArrowHalf = 9f;

    /// <summary>Half the view's width in metres (zoom). A smaller value zooms in.</summary>
    public float ZoomMetres { get; set; } = 90f;

    ImageTexture? _base;
    Suchohrad.MapLoader.MapLoader? _map;
    int _side;               // texture side in texels (0 = not built)
    float _sideMetres;       // the map's extent covered by the texture, metres
    Vector2 _origin;         // ground (x, z) of texel (0, 0)
    float _metresPerTexel = 1f;
    Vector2 _player;
    float _yaw;              // target: the view yaw
    float _shownYaw;         // eased towards _yaw
    bool _hasPlayer;
    readonly List<NVector2> _vehicles = new();
    readonly MinimapPings _pings = new();
    readonly List<RoadDraw> _roads = new();
    static readonly Vector2[] Arrow = { new(0, -ArrowHalf), new(ArrowHalf * 0.8f, ArrowHalf), new(0, ArrowHalf * 0.4f), new(-ArrowHalf * 0.8f, ArrowHalf) };

    /// <summary>A road drawn as geometry: ground points, its own screen scratch buffer (reused every frame), and its bounds.</summary>
    sealed class RoadDraw
    {
        public Vector2[] Ground = Array.Empty<Vector2>();
        public Vector2[] Screen = Array.Empty<Vector2>();
        public Rect2 Bounds;
        public float WidthM;
        public Color Colour;
    }

    public Minimap()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        CustomMinimumSize = new Vector2(224, 224);
    }

    /// <summary>
    /// Stores the map and prepares the roads. The terrain texture is built on the first frame, once the on-screen size is known.
    /// Returns false when the map has no parcels or roads (the minimap then shows the player only).
    /// </summary>
    public bool Build(Suchohrad.MapLoader.MapLoader? map)
    {
        _base = null;
        _side = 0;
        _map = map;
        _roads.Clear();
        if (map == null) return false;
        var roads = RoadNetwork.Parse(map.RoadsJson);
        var box = default(Rect2);
        var any = false;
        void Grow(Rect2 r) { box = any ? box.Merge(r) : r; any = true; }
        foreach (var p in map.Parcels.Values) Grow(p.Bounds);
        foreach (var r in roads)
        {
            var g = new Vector2[r.Points.Length];
            for (var i = 0; i < g.Length; i++) g[i] = new Vector2(r.Points[i].X, -r.Points[i].Y); // local north-up to ground z
            var bounds = new Rect2(g[0], Vector2.Zero);
            foreach (var q in g) bounds = bounds.Expand(q);
            Grow(bounds);
            _roads.Add(new RoadDraw { Ground = g, Screen = new Vector2[g.Length], Bounds = bounds, WidthM = r.WidthM, Colour = RoadColour(r.Highway) });
        }
        if (!any) return false;

        _sideMetres = Math.Max(box.Size.X, box.Size.Y) + 2 * PadMetres;
        _origin = box.GetCenter() - new Vector2(_sideMetres / 2, _sideMetres / 2);
        return true;
    }

    /// <summary>The player's ground position (x, z) and view yaw, in the game's convention. Call every frame.</summary>
    public void SetPlayer(Vector2 groundXZ, float viewYaw)
    {
        _player = groundXZ;
        _yaw = viewYaw;
        if (!_hasPlayer) _shownYaw = viewYaw; // first frame: no sweep from zero
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

    /// <summary>Screen pixels per metre at the minimap's current size, UI scale and device pixel ratio.</summary>
    float DevicePxPerMetre()
    {
        var localPxPerMetre = Math.Min(Size.X, Size.Y) / 2f / ZoomMetres;
        return localPxPerMetre * Math.Max(0.01f, GetGlobalTransformWithCanvas().Scale.X);
    }

    /// <summary>Rebuilds the terrain texture when the on-screen scale asks for a different resolution (rare: resize, zoom, UI scale).</summary>
    void EnsureTexture()
    {
        if (_map == null || Size.X <= 0 || _sideMetres <= 0) return;
        var wanted = MinimapView.TextureSide(_sideMetres, DevicePxPerMetre(), MinTextureSide, MaxTextureSide);
        if (MinimapView.NeedsRebuild(_side, wanted)) Rasterise(wanted);
    }

    /// <summary>Rasterises the parcel fills into a square of <paramref name="side"/> texels, with mipmaps.</summary>
    void Rasterise(int side)
    {
        _side = side;
        _metresPerTexel = _sideMetres / side;
        var img = Image.CreateEmpty(side, side, false, Image.Format.Rgba8);
        img.Fill(Terrain);
        foreach (var parcel in _map!.Parcels.Values)
        {
            var colour = ParcelColour(parcel.Kind);
            var b = parcel.Bounds;
            var u0 = Math.Clamp((int)Math.Floor((b.Position.X - _origin.X) / _metresPerTexel), 0, side - 1);
            var u1 = Math.Clamp((int)Math.Ceiling((b.End.X - _origin.X) / _metresPerTexel), 0, side - 1);
            var v0 = Math.Clamp((int)Math.Floor((b.Position.Y - _origin.Y) / _metresPerTexel), 0, side - 1);
            var v1 = Math.Clamp((int)Math.Ceiling((b.End.Y - _origin.Y) / _metresPerTexel), 0, side - 1);
            for (var v = v0; v <= v1; v++)
                for (var u = u0; u <= u1; u++)
                    if (parcel.Contains(TexelCentre(u, v))) img.SetPixel(u, v, colour);
        }
        img.GenerateMipmaps();
        _base = ImageTexture.CreateFromImage(img);
    }

    public override void _Process(double delta)
    {
        if (_hasPlayer) _shownYaw = MinimapView.Follow(_shownYaw, _yaw, (float)delta, FollowRate);
        EnsureTexture();
        QueueRedraw();
    }

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
            var theta = MinimapMath.RotationFor(_shownYaw);
            var pxPerMetre = MinimapMath.PxPerMetre(half, ZoomMetres);
            if (_base is { } tex && _side > 0)
            {
                var k = pxPerMetre * _metresPerTexel;
                var playerTexel = (_player - _origin) / _metresPerTexel;
                var extent = _side * k;
                DrawSetTransform(centre, theta, Vector2.One);
                DrawTextureRect(tex, new Rect2(-playerTexel * k, new Vector2(extent, extent)), false);
                DrawSetTransform(Vector2.Zero);
            }
            DrawRoads(centre, theta, pxPerMetre, half);
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
            DrawNorth(centre, theta, half);
        }
        DrawSetTransform(centre);
        DrawColoredPolygon(Arrow, ShooterTheme.Text);
        DrawSetTransform(Vector2.Zero);
        DrawRect(new Rect2(Vector2.Zero, size), ShooterTheme.Line, false, ShooterTheme.Border);
    }

    /// <summary>Anti-aliased roads in screen space. Roads outside the view are skipped; each road reuses its own buffer.</summary>
    void DrawRoads(Vector2 centre, float theta, float pxPerMetre, float half)
    {
        var reach = ZoomMetres * 1.5f; // the view's corner is ZoomMetres * sqrt(2) from the player
        var view = new Rect2(_player - new Vector2(reach, reach), new Vector2(reach * 2, reach * 2));
        var player = new NVector2(_player.X, _player.Y);
        for (var i = 0; i < _roads.Count; i++)
        {
            var road = _roads[i];
            if (!road.Bounds.Intersects(view)) continue;
            var ground = road.Ground;
            for (var p = 0; p < ground.Length; p++)
            {
                var d = new NVector2(ground[p].X, ground[p].Y) - player;
                var s = MinimapMath.ToScreen(d, theta, pxPerMetre);
                road.Screen[p] = centre + new Vector2(s.X, s.Y);
            }
            DrawPolyline(road.Screen, road.Colour, Math.Max(1f, road.WidthM * pxPerMetre), true);
        }
    }

    /// <summary>The "N" marker on the rim, at ground north (-z) in the rotated view.</summary>
    void DrawNorth(Vector2 centre, float theta, float half)
    {
        var dir = MinimapMath.Rotate(new NVector2(0, -1), theta);
        var at = centre + new Vector2(dir.X, dir.Y) * (half - 14f);
        DrawString(ThemeDB.FallbackFont, at + new Vector2(-5, 6), "N", HorizontalAlignment.Left, -1, 16, ShooterTheme.Text);
    }

    /// <summary>Screen offset from the centre of a ground point, clamped into the square of half-size <paramref name="inner"/>.</summary>
    Vector2 Screen(NVector2 worldXZ, float theta, float pxPerMetre, float inner)
    {
        var d = worldXZ - new NVector2(_player.X, _player.Y);
        var p = MinimapMath.ClampToSquare(MinimapMath.ToScreen(d, theta, pxPerMetre), inner);
        return new Vector2(p.X, p.Y);
    }

    Vector2 TexelCentre(int u, int v) => _origin + new Vector2((u + 0.5f) * _metresPerTexel, (v + 0.5f) * _metresPerTexel);

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
