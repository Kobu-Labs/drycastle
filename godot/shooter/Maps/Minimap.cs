using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Minimap;
using NVector2 = System.Numerics.Vector2;
using Vector2 = Godot.Vector2;

namespace Drycastle.Shooter.Maps;

/// <summary>
/// Top-down minimap (ticket #119, quality and orientation in #150). Two layers, both from the shared <see cref="MapBase"/>:
/// - Terrain and parcel fills: one mipmapped texture per map, sized from the on-screen scale (UI scale times device pixel
///   ratio), rebuilt only when that scale changes by a lot. Sampled with linear filtering, so it is not blocky when zoomed.
/// - Roads: anti-aliased polylines drawn each frame in screen space, culled to the view, so they stay crisp at any scale.
/// Dots: the player arrow (fixed at the centre), vehicles (squares), enemy pings (circles, the UAV streak's layer, expiring
/// on their own), and a north marker "N" on the rim. The view turns to the player's view direction (see
/// <see cref="MinimapView"/>), eased along the shorter arc.
/// Positions are ground (x, z) in metres, the same axes as the game's world (north is -z). The player, vehicles, friends and
/// pings live in the shared <see cref="MapMarkers"/> that the tactical map reads as well.
/// </summary>
public sealed partial class Minimap : Control
{
    /// <summary>Texture side limits in texels. The side follows the on-screen scale inside these bounds.</summary>
    public const int MinTextureSide = 512, MaxTextureSide = 2048;
    /// <summary>How fast the shown yaw eases to the view yaw, per second (about 0.15 s to settle).</summary>
    public const float FollowRate = 14f;
    const float ArrowHalf = 9f;
    readonly List<GunfireBlip> _blipScratch = new();

    /// <summary>Half the view's width in metres (zoom). A smaller value zooms in.</summary>
    public float ZoomMetres { get; set; } = 90f;

    readonly MapMarkers _markers;
    MapBase? _map;
    ImageTexture? _base;
    int _side;               // texture side in texels (0 = not built)
    float _metresPerTexel = 1f;
    float _shownYaw;         // eased towards the markers' view yaw
    /// <summary>Team deathmatch: pings use the enemy colour (orange) and friends are drawn, as green circles.</summary>
    public bool TeamColours { get => _markers.TeamColours; set => _markers.TeamColours = value; }
    static readonly Vector2[] Arrow = { new(0, -ArrowHalf), new(ArrowHalf * 0.8f, ArrowHalf), new(0, ArrowHalf * 0.4f), new(-ArrowHalf * 0.8f, ArrowHalf) };

    /// <summary>The shared map data (null before <see cref="Build"/> or without a map); the tactical map draws from the same base.</summary>
    public MapBase? Base => _map;

    /// <summary>The vehicle tracks both maps draw (the client feeds them from its crop crush).</summary>
    public MapTrails Trails => _markers.Trails;

    /// <param name="markers">The shared markers; the HUD gives the same instance to the tactical map.</param>
    public Minimap(MapMarkers markers)
    {
        _markers = markers;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        CustomMinimumSize = new Vector2(224, 224);
    }

    /// <summary>
    /// Takes the shared map base. The terrain texture is built on the first frame, once the on-screen size is known.
    /// Returns false when the map has no parcels or roads (the minimap then shows the player only).
    /// </summary>
    public bool Build(Drycastle.MapLoader.MapLoader? map)
    {
        _base = null;
        _side = 0;
        _map = MapBase.For(map);
        return _map != null;
    }

    /// <summary>The player's ground position (x, z) and view yaw, in the game's convention. Call every frame.</summary>
    public void SetPlayer(Vector2 groundXZ, float viewYaw)
    {
        if (_markers.SetPlayer(groundXZ, viewYaw)) _shownYaw = viewYaw; // first frame: no sweep from zero
    }

    /// <summary>Vehicle positions (ground x, z) shown as squares. Copied, so the caller may reuse its list.</summary>
    public void SetVehicleDots(IReadOnlyList<Vector2> groundXZ) => _markers.SetVehicles(groundXZ);

    /// <summary>Teammates' positions (ground x, z), drawn in team deathmatch only. Copied, so the caller may reuse its list.</summary>
    public void SetFriendDots(IReadOnlyList<Vector2> groundXZ) => _markers.SetFriends(groundXZ);

    /// <summary>Squad mates (#379): their positions (ground x, z) and names, drawn blue with the name in team deathmatch. Copied.</summary>
    public void SetSquadDots(IReadOnlyList<(Vector2 At, string Name)> mates) => _markers.SetSquad(mates);

    /// <summary>
    /// Shows enemy pings (circles) until <paramref name="expiry"/> on the Time.GetTicksMsec()/1000 clock. A new call replaces
    /// the previous set. This is the UAV streak's layer.
    /// </summary>
    public void SetEnemyPings(IReadOnlyList<Vector2> groundXZ, double expiry) => _markers.SetPings(groundXZ, expiry);

    /// <summary>Gives the map the gunfire blips tracker (see <see cref="GunfireBlips"/>); the tactical map draws the same ones.</summary>
    public void SetBlips(GunfireBlips? blips) => _markers.Blips = blips;

    public void ClearEnemyPings() => _markers.Pings.Clear();

    /// <summary>Screen pixels per metre at the minimap's current size, UI scale and device pixel ratio.</summary>
    float DevicePxPerMetre()
    {
        var localPxPerMetre = Math.Min(Size.X, Size.Y) / 2f / ZoomMetres;
        return localPxPerMetre * Math.Max(0.01f, GetGlobalTransformWithCanvas().Scale.X);
    }

    /// <summary>Takes a new terrain texture from the shared base when the on-screen scale asks for a different resolution (rare: resize, zoom, UI scale).</summary>
    void EnsureTexture()
    {
        if (_map == null || Size.X <= 0) return;
        var wanted = MinimapView.TextureSide(_map.SideMetres, DevicePxPerMetre(), MinTextureSide, MaxTextureSide);
        if (!MinimapView.NeedsRebuild(_side, wanted)) return;
        _side = wanted;
        _metresPerTexel = _map.SideMetres / wanted;
        _base = _map.Texture(wanted);
    }

    public override void _Process(double delta)
    {
        if (_markers.HasPlayer) _shownYaw = MinimapView.Follow(_shownYaw, _markers.Yaw, (float)delta, FollowRate);
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
        if (_markers.HasPlayer)
        {
            var theta = MinimapMath.RotationFor(_shownYaw);
            var pxPerMetre = MinimapMath.PxPerMetre(half, ZoomMetres);
            if (_base is { } tex && _side > 0)
            {
                var k = pxPerMetre * _metresPerTexel;
                var playerTexel = (_markers.Player - _map!.Origin) / _metresPerTexel;
                var extent = _side * k;
                DrawSetTransform(centre, theta, Vector2.One);
                DrawTextureRect(tex, new Rect2(-playerTexel * k, new Vector2(extent, extent)), false);
                DrawSetTransform(Vector2.Zero);
            }
            DrawRoads(centre, theta, pxPerMetre, half);
            DrawTrails(centre, theta, pxPerMetre, half);
            var inner = half - 6f;
            for (var i = 0; i < _markers.Vehicles.Count; i++)
            {
                var p = centre + Screen(_markers.Vehicles[i], theta, pxPerMetre, inner);
                DrawRect(new Rect2(p - new Vector2(4, 4), new Vector2(8, 8)), ShooterTheme.Warn);
            }
            if (TeamColours)
                for (var i = 0; i < _markers.Friends.Count; i++) // teammates: green discs, larger than the enemy pings
                {
                    var p = centre + Screen(_markers.Friends[i], theta, pxPerMetre, inner);
                    DrawCircle(p, 7f, ShooterTheme.Outline);
                    DrawCircle(p, 5f, ShooterTheme.Good);
                }
            if (TeamColours)
                for (var i = 0; i < _markers.Squad.Count; i++) // squad mates: blue discs in a pale ring, with their names
                {
                    var p = centre + Screen(_markers.Squad[i].At, theta, pxPerMetre, inner);
                    MapGlyphs.Squad(this, p, _markers.Squad[i].Name, 1f);
                }
            var pings = _markers.Pings.Positions(now);
            for (var i = 0; i < pings.Count; i++)
            {
                var p = centre + Screen(pings[i], theta, pxPerMetre, inner);
                DrawCircle(p, 6f, ShooterTheme.Outline);
                DrawCircle(p, 4f, TeamColours ? ShooterTheme.Enemy : ShooterTheme.Danger);
            }
            if (_markers.Blips is { } blips)
            {
                blips.Collect(now, _blipScratch);
                var still = GameSettings.Current.ReduceMotion;
                foreach (var b in _blipScratch)
                    MapGlyphs.Blip(this, centre + Screen(b.Position, theta, pxPerMetre, inner), b, still, 0.7f);
            }
            foreach (var strike in _markers.Strikes.Live(now)) MapGlyphs.Strike(this, centre + Screen(strike.At, theta, pxPerMetre, inner), strike.RadiusM * pxPerMetre);
            if (_markers.Waypoint.At is { } wp) MapGlyphs.Waypoint(this, centre + Screen(wp, theta, pxPerMetre, inner), 0.8f);
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
        if (_map == null) return;
        var reach = ZoomMetres * 1.5f; // the view's corner is ZoomMetres * sqrt(2) from the player
        var view = new Rect2(_markers.Player - new Vector2(reach, reach), new Vector2(reach * 2, reach * 2));
        var player = new NVector2(_markers.Player.X, _markers.Player.Y);
        var roads = _map.Roads;
        for (var i = 0; i < roads.Count; i++)
        {
            var road = roads[i];
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

    /// <summary>Vehicle tracks as thin lines over the roads: all casings first, then the fills, culled to the view; nothing is allocated. Points past the square view are drawn as they are (the control clips).</summary>
    void DrawTrails(Vector2 centre, float theta, float pxPerMetre, float half)
    {
        var trails = _markers.Trails;
        if (trails.Trails.Count == 0 && trails.Dots.Count == 0) return;
        var reach = ZoomMetres * 1.5f;
        var lo = new NVector2(_markers.Player.X - reach, _markers.Player.Y - reach);
        var hi = new NVector2(_markers.Player.X + reach, _markers.Player.Y + reach);
        var player = new NVector2(_markers.Player.X, _markers.Player.Y);
        for (var pass = 0; pass < 2; pass++)
        {
            var colour = pass == 0 ? ShooterTheme.Outline : ShooterTheme.MapTrail;
            var width = pass == 0 ? ShooterTheme.MapTrailCasing : ShooterTheme.MapTrailWidth;
            foreach (var t in trails.Trails)
            {
                if (t.Points.Count < 2 || t.Max.X < lo.X || t.Min.X > hi.X || t.Max.Y < lo.Y || t.Min.Y > hi.Y) continue;
                var prev = ToScreen(t.Points[0] - player, theta, pxPerMetre, centre);
                for (var i = 1; i < t.Points.Count; i++)
                {
                    var next = ToScreen(t.Points[i] - player, theta, pxPerMetre, centre);
                    DrawLine(prev, next, colour, width, true);
                    prev = next;
                }
            }
            foreach (var d in trails.Dots)
            {
                if (d.X < lo.X || d.X > hi.X || d.Y < lo.Y || d.Y > hi.Y) continue;
                DrawCircle(ToScreen(d - player, theta, pxPerMetre, centre), width * 0.5f, colour);
            }
        }
    }

    static Vector2 ToScreen(NVector2 delta, float theta, float pxPerMetre, Vector2 centre)
    {
        var s = MinimapMath.ToScreen(delta, theta, pxPerMetre);
        return centre + new Vector2(s.X, s.Y);
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
        var d = worldXZ - new NVector2(_markers.Player.X, _markers.Player.Y);
        var p = MinimapMath.ClampToSquare(MinimapMath.ToScreen(d, theta, pxPerMetre), inner);
        return new Vector2(p.X, p.Y);
    }

    static readonly Color Backdrop = new(0.05f, 0.06f, 0.08f, 0.85f);
}
