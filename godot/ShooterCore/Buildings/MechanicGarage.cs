using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Glass;

namespace Drycastle.ShooterCore.Buildings;

/// <summary>
/// The mechanic's garage on the garden plots by the road E-0101 (sketch L1; ticket #320): a plain OSB shed, 6.6 x 8 m, its long wall to the road. The Golf Mk2 stands inside
/// behind one breakable OSB plate in that wall. Godot-free: the layout in the garage's own frame, which the game (<c>MechanicGarage</c>) builds from and which the tests hold
/// against vehicles.json and the glass panes.
/// <para>
/// The garage frame: origin on the floor at the middle of the footprint, +X to the south (along the road, to the right of someone looking out of the plate),
/// +Z to the west (into the building), -Z out to the road, +Y up. Yaw <see cref="YawDeg"/> turns it into the world (game convention: 0 faces -Z, positive turns left).
/// </para>
/// </summary>
public static class MechanicGarageSite
{
    /// <summary>The node name of the plate's pane (it is the pane's sort key in the map's pane list: see <see cref="AddPane"/>).</summary>
    public const string PlateNode = "mechanic_garage_plate";
    /// <summary>Hit points of the plate: sturdier than a window (glass.json building_pane_hp), a few shotgun blasts or a rocket.</summary>
    public const float PlateHp = 240f;

    /// <summary>Outer footprint: along the road (X) and across it (Z), and the thickness of a wall.</summary>
    public const float Width = 6.6f, Depth = 8f, Wall = 0.15f;
    /// <summary>The inner half extents (the room is 6.3 x 7.7 m).</summary>
    public const float HalfX = Width / 2 - Wall, HalfZ = Depth / 2 - Wall;
    /// <summary>The roof's underside: high at the road wall, low at the back wall (a mono-pitch roof draining to the back).</summary>
    public const float RoofHigh = 3.9f, RoofLow = 3.3f;
    /// <summary>The roof slab and the sheet's overhang past the walls.</summary>
    public const float RoofThickness = 0.14f, Overhang = 0.35f;

    /// <summary>The plate in the road wall: centred on the Golf's lane, wide enough for the Golf (2.25 m) to drive out, from the floor up. The Golf stands against the north wall (-X), 0.45 m off it.</summary>
    public const float PlateX = -HalfX + 1.45f, PlateWidth = 2.7f, PlateHeight = 2.5f;
    /// <summary>The Golf's place: nose <see cref="GolfNoseGap"/> m from the road wall's inner face (it is 5.35 m long, its origin in the middle).</summary>
    public const float GolfX = PlateX, GolfNoseGap = 1.3f, GolfHalfLength = 2.68f;
    public const float GolfZ = -HalfZ + GolfNoseGap + GolfHalfLength;

    /// <summary>Where the garage's middle is on the ground (world x, z, metres) and how it is turned: out through the plate is -Z in the garage, east-south-east in the world.</summary>
    public static readonly Vector2 Centre = new(32.6f, 15.35f);
    public const float YawDeg = -103.7f;
    public static float YawRad => YawDeg * MathF.PI / 180f;

    /// <summary>A point of the garage frame in the world, <paramref name="y"/> up from <paramref name="floorY"/>.</summary>
    public static Vector3 ToWorld(float x, float z, float y = 0, float floorY = 0)
    {
        var (s, c) = MathF.SinCos(YawRad);
        return new Vector3(Centre.X + x * c + z * s, floorY + y, Centre.Y - x * s + z * c);
    }

    /// <summary>A direction of the garage frame in the world (ground plane).</summary>
    public static Vector3 DirToWorld(float x, float z)
    {
        var (s, c) = MathF.SinCos(YawRad);
        return new Vector3(x * c + z * s, 0, -x * s + z * c);
    }

    /// <summary>The Golf's world place (x, z) and heading in degrees: nose out of the plate.</summary>
    public static (float X, float Z, float YawDeg) GolfSpot
    {
        get { var p = ToWorld(GolfX, GolfZ); return (p.X, p.Z, YawDeg); }
    }

    /// <summary>
    /// The floor height: a hair over the highest ground under the footprint (the terrain is almost flat there, 0.3 m at most), so the slab hides it and the rooms are level.
    /// <paramref name="ground"/> is the map's terrain height at (x, z), NaN where there is none.
    /// </summary>
    public static float FloorY(Func<float, float, float> ground)
    {
        var top = float.NaN;
        for (var ix = -1; ix <= 1; ix++)
        for (var iz = -1; iz <= 1; iz++)
        {
            var p = ToWorld(ix * Width / 2, iz * Depth / 2);
            var h = ground(p.X, p.Z);
            if (!float.IsNaN(h)) top = float.IsNaN(top) ? h : MathF.Max(top, h);
        }
        return float.IsNaN(top) ? float.NaN : top + 0.02f;
    }

    /// <summary>The plate's glass rectangle: on its outer face, facing out to the road.</summary>
    public static GlassPaneRect PlateRect(float floorY) =>
        GlassPaneRect.Make(ToWorld(PlateX, -Depth / 2, PlateHeight / 2, floorY), DirToWorld(0, -1), PlateWidth, PlateHeight);

    /// <summary>
    /// Adds the plate to the map's panes (the same call on the server and on every client, so the wire index agrees) and puts the list back in the wire order (by node name, ordinal).
    /// </summary>
    public static void AddPane(List<MapGlassPane> panes, float floorY)
    {
        if (float.IsNaN(floorY) || panes.Any(p => p.Node == PlateNode)) return;
        panes.Add(new MapGlassPane(PlateNode, PlateRect(floorY), Breakable: true, Hp: PlateHp));
        panes.Sort((a, b) => string.CompareOrdinal(a.Node, b.Node));
    }

    /// <summary>True when the point (garage frame, ground plane) is inside the room.</summary>
    public static bool Inside(float x, float z) => MathF.Abs(x) < HalfX && MathF.Abs(z) < HalfZ;

    /// <summary>True when the world point (x, z) is on the footprint, grown by <paramref name="margin"/> metres: the baked trees there lose their colliders.</summary>
    public static bool Covers(float worldX, float worldZ, float margin)
    {
        var (s, c) = MathF.SinCos(YawRad);
        float dx = worldX - Centre.X, dz = worldZ - Centre.Y;
        float x = dx * c - dz * s, z = dx * s + dz * c; // the world point in the garage frame (the inverse of ToWorld)
        return MathF.Abs(x) <= Width / 2 + margin && MathF.Abs(z) <= Depth / 2 + margin;
    }

    /// <summary>The roof's underside at depth <paramref name="z"/> of the garage frame.</summary>
    public static float RoofAt(float z) => RoofHigh + (RoofLow - RoofHigh) * (z + Depth / 2) / Depth;

    /// <summary>The room's lamps along the ceiling: garage-frame x, z of each fixture (the first is over the Golf).</summary>
    public static readonly (float X, float Z)[] Lamps = { (PlateX, 0.1f), (1.1f, 0.1f) };
}

/// <summary>What the room's lights are doing: shut in the dark, just opened to the daylight, flickering, or on.</summary>
public enum GarageLightPhase : byte { Sealed, Dark, Flicker, On }

/// <summary>
/// The look of the garage's interior from the moment the plate breaks (ticket #320): the room is dark, then the roof lights flicker without lighting anything
/// (only the fixtures' tubes flash), then they switch on fully. Client only, nothing is sent: each client starts it when it sees the plate shatter, and a client that
/// joins after it broke sees the lights on. Pure and Godot-free: the game reads <see cref="Glow"/> (the tubes), <see cref="Level"/> (the real lights) and
/// <see cref="Veil"/> (how dark the room looks through the opening).
/// </summary>
public sealed class MechanicGarageLights
{
    public static readonly StateMachineSpec<GarageLightPhase> Spec = StateMachineSpec<GarageLightPhase>
        .Define("MechanicGarage.Lights", "The garage interior's lights after the plate breaks: dark, then a flicker that lights nothing, then fully on.", GarageLightPhase.Sealed)
        .Allow(GarageLightPhase.Sealed, GarageLightPhase.Dark, GarageLightPhase.On)
        .Allow(GarageLightPhase.Dark, GarageLightPhase.Flicker, GarageLightPhase.On, GarageLightPhase.Sealed)
        .Allow(GarageLightPhase.Flicker, GarageLightPhase.On, GarageLightPhase.Sealed)
        .Allow(GarageLightPhase.On, GarageLightPhase.Sealed)
        .Note(GarageLightPhase.Sealed, "The plate stands (the start state): the lights are off and nothing of the room can be seen.")
        .Note(GarageLightPhase.Dark, "The plate has just broken: the room is dark.")
        .Note(GarageLightPhase.Flicker, "The tubes of the roof lights flash a few times; the real lights stay off, so the Golf is not lit.")
        .Note(GarageLightPhase.On, "The lights are on: they come up over a moment, or at once for a client that arrived later.")
        .Build();

    public const float DarkSeconds = 0.5f, FlickerSeconds = 1.3f, RampSeconds = 0.5f;

    /// <summary>The flashes of the tubes in the flicker: (start, length) in seconds into it.</summary>
    static readonly (float Start, float Length)[] Bursts =
        { (0.1f, 0.06f), (0.3f, 0.12f), (0.5f, 0.04f), (0.58f, 0.04f), (0.8f, 0.18f), (1.05f, 0.25f) };

    readonly StateMachine<GarageLightPhase> _fsm = new(Spec);
    float _level;

    public GarageLightPhase Phase => _fsm.Current;
    /// <summary>The real lights, 0..1.</summary>
    public float Level => _level;
    /// <summary>The emissive tubes of the fixtures, 0 or 1 (flashing in the flicker).</summary>
    public float Glow => _fsm.Current switch
    {
        GarageLightPhase.On => 1f,
        GarageLightPhase.Flicker => InBurst(_fsm.Elapsed) ? 1f : 0f,
        _ => 0f,
    };
    /// <summary>The darkness the opening shows: 1 sealed, 0.96 while the room is dark or flickering, fading out as the lights come up.</summary>
    public float Veil => _fsm.Current == GarageLightPhase.Sealed ? 1f : 0.96f * (1f - _level);

    static bool InBurst(float t)
    {
        foreach (var (start, length) in Bursts) if (t >= start && t < start + length) return true;
        return false;
    }

    /// <summary>The plate broke (<paramref name="animate"/>: just now, so the sequence plays) or stands again (a restored pane).</summary>
    public void PlateBroken(bool broken, bool animate)
    {
        if (!broken) { _fsm.Reset(GarageLightPhase.Sealed); _level = 0; return; }
        if (!_fsm.Is(GarageLightPhase.Sealed)) return;
        if (animate) _fsm.Enter(GarageLightPhase.Dark, DarkSeconds);
        else { _fsm.Reset(GarageLightPhase.On); _level = 1; }
    }

    public void Update(float dt)
    {
        switch (_fsm.Current)
        {
            case GarageLightPhase.Dark:
                if (_fsm.Tick(dt)) _fsm.Enter(GarageLightPhase.Flicker, FlickerSeconds);
                break;
            case GarageLightPhase.Flicker:
                if (_fsm.Tick(dt)) _fsm.Enter(GarageLightPhase.On);
                break;
            case GarageLightPhase.On:
                _fsm.Tick(dt);
                _level = MathF.Min(1f, _level + dt / RampSeconds);
                break;
        }
    }
}
