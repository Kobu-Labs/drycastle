using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Churches;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Wildlife;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Scuds;

/// <summary>
/// Where the Scud event puts things: the tactical map's sheet the search area is measured on, the ground a truck or a cart may stand on (real ground under the whole footprint, nothing solid on it, no vehicle near), the spot the
/// truck is hidden at, and the map's bunkers with the way each one's doorway faces.
/// </summary>
internal sealed class ScudPlaces
{
    readonly ServerContext _ctx;
    readonly ScudFile _scud;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Node3D _world;
    readonly Fleet _fleet;
    readonly Random _rng;
    readonly List<NVec3> _spawns;
    readonly Func<Shelters> _shelters;
    readonly Func<string, NVec3?> _pickEventSpawn;
    List<RoadLine>? _roads;

    /// <param name="spawns">The player spawn points (the fallback where a map has no scud zone).</param>
    /// <param name="pickEventSpawn">A random baked point of a zone kind (<c>NetServer.PickEventSpawn</c>).</param>
    public ScudPlaces(ServerContext ctx, ScudFile scud, Drycastle.MapLoader.MapLoader? map, Node3D world, Fleet fleet, Random rng, List<NVec3> spawns, Func<Shelters> shelters, Func<string, NVec3?> pickEventSpawn)
    {
        _ctx = ctx; _scud = scud; _map = map; _world = world; _fleet = fleet; _rng = rng; _spawns = spawns; _shelters = shelters; _pickEventSpawn = pickEventSpawn;
    }

    static float Dist2D(NVec3 a, NVec3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>The tactical map's world sheet as the clients draw it (<see cref="TacticalMapMath.Sheet"/> over the parcels' box and the terrain's): origin, side, south z. Null without a map.</summary>
    public (NVec2 Origin, float Side)? Sheet()
    {
        if (_map == null) return null;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        void Merge(float x0, float z0, float x1, float z1) { minX = MathF.Min(minX, x0); minZ = MathF.Min(minZ, z0); maxX = MathF.Max(maxX, x1); maxZ = MathF.Max(maxZ, z1); }
        foreach (var p in _map.Parcels.Values) Merge(p.Bounds.Position.X, p.Bounds.Position.Y, p.Bounds.End.X, p.Bounds.End.Y);
        if (_map.TerrainBox is { } t) Merge(t.Position.X, t.Position.Z, t.End.X, t.End.Z);
        if (minX > maxX) return null;
        return TacticalMapMath.Sheet(minX, minZ, maxX, maxZ);
    }

    /// <summary>Whether a vehicle-sized box of the ground vehicle's world (buildings, trees, rocks, closed doors) is free where it would stand, grown by the data's clearance.</summary>
    bool FootprintClear(Vector3 ground, float yaw, float halfW, float halfL)
    {
        var margin = (float)_scud.Event.SpotClearanceM;
        const float lift = 0.7f, height = 3.0f; // over the ground's bumps, up past the roof
        var shape = new BoxShape3D { Size = new Vector3((halfW + margin) * 2, height, (halfL + margin) * 2) };
        var q = new PhysicsShapeQueryParameters3D
        {
            Shape = shape, CollisionMask = CollisionRules.StaticMask(MoverKind.GroundVehicle),
            Transform = new Transform3D(Basis.FromEuler(new Vector3(0, yaw, 0)), new Vector3(ground.X, ground.Y + lift + height / 2, ground.Z)),
        };
        return _world.GetWorld3D().DirectSpaceState.IntersectShape(q, 1).Count == 0;
    }

    /// <summary>The origin height for a vehicle of this size at (x, z) facing <paramref name="yaw"/>: on real ground under the whole footprint, with nothing solid on it and no vehicle within a metre of it. Null otherwise.</summary>
    public float? StandingHeight(float x, float z, float yaw, float halfW, float halfL)
    {
        var terrain = _map?.GroundHeight(x, z) ?? float.NaN;
        if (float.IsNaN(terrain)) return null;
        var space = _world.GetWorld3D().DirectSpaceState;
        if (ServerCar.RestHeight(space, new Vector3(x, terrain, z), yaw, halfW, halfL, terrain) is not { } y) return null;
        if (!FootprintClear(new Vector3(x, y, z), yaw, halfW, halfL)) return null;
        var box = new BodyBox(x, z, halfW, halfL, yaw);
        foreach (var other in _fleet.Cars)
            if (!other.Destroyed && !other.Retired && ScudReloadRules.Gap(box, ScudLoading.BoxOf(other)) < 1f) return null;
        return y;
    }

    /// <summary>
    /// Where the truck stands: a random baked point of a <c>scud</c> zone, else (a map with none drawn) a random player spawn point, which is open ground in the village; at least
    /// <see cref="ScudEventData.MinPlayerDistanceM"/> from every human player, facing along the nearest road (any way when none is near), with its footprint clear of solids. Null when none of a few dozen tries fits.
    /// </summary>
    public (NVec3 Pos, float Yaw, bool FromZone)? PickSpot(VehicleDef def)
    {
        var players = _ctx.Players.Values.Where(o => !o.IsBot && _ctx.IsAlive(o.Id)).Select(o => new NVec3(o.Body.GlobalPosition.X, o.Body.GlobalPosition.Y, o.Body.GlobalPosition.Z)).ToList();
        var minD = (float)_scud.Event.MinPlayerDistanceM;
        for (var tries = 0; tries < 80; tries++)
        {
            var zone = tries < 40 ? _pickEventSpawn(EventBook.Scud) : null;
            NVec3 p;
            if (zone is { } z) p = z;
            else if (_spawns.Count > 0) p = _spawns[_rng.Next(_spawns.Count)];
            else break;
            if (players.Any(q => Dist2D(q, p) < minD)) continue;
            _roads ??= _map != null ? RoadNetwork.Parse(_map.RoadsJson) : new List<RoadLine>();
            var yaw = RoadHeading.YawAt(_roads, p.X, p.Z) ?? (float)(_rng.NextDouble() * MathF.Tau - MathF.PI);
            if (StandingHeight(p.X, p.Z, yaw, def.HalfWidth, def.HalfLength) is not { } y) continue;
            return (new NVec3(p.X, y, p.Z), yaw, zone != null);
        }
        return null;
    }

    /// <summary>The map's bunkers as the carts see them: the middle and the reach of the ground floor's room, and the bearing of its doorway when a probe finds one (null when none).</summary>
    public List<ScudBunkerSlot> Bunkers()
    {
        var list = new List<ScudBunkerSlot>();
        foreach (var room in _shelters().Rooms.Where(r => r.Level == 0 && r.BunkerId != "" && r.BunkerId != ChurchShell.RoomId && r.Ring.Length >= 3))
        {
            if (list.Any(b => b.BunkerId == room.BunkerId)) continue;
            var centre = NVec2.Zero;
            foreach (var v in room.Ring) centre += v;
            centre /= room.Ring.Length;
            var radius = room.Ring.Max(v => NVec2.Distance(v, centre)) + 0.5f;
            list.Add(new ScudBunkerSlot { BunkerId = room.BunkerId, Centre = centre, Radius = radius, FloorY = room.Y0, Door = DoorBearing(centre, radius, room.Y0) });
        }
        return list;
    }

    /// <summary>
    /// The way a bunker's doorway faces, from the physics world (bunkers.json does not say): rays from the middle of the ground floor at three heights, in 24 directions; the direction whose neighbours are also free
    /// (open all the way out past the footprint) is the doorway. Firing slits are too small to be open at all three heights. Null when every direction is walled in.
    /// </summary>
    float? DoorBearing(NVec2 centre, float radius, float floorY)
    {
        const int n = 24;
        var space = _world.GetWorld3D().DirectSpaceState;
        var mask = CollisionRules.StaticMask(MoverKind.GroundVehicle);
        var free = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var a = i * MathF.Tau / n;
            var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var open = true;
            foreach (var h in new[] { 0.5f, 1.1f, 1.7f })
            {
                var from = new Vector3(centre.X, floorY + h, centre.Y);
                var q = PhysicsRayQueryParameters3D.Create(from, from + dir * (radius + 8f));
                q.CollisionMask = mask;
                if (space.IntersectRay(q).Count > 0) { open = false; break; }
            }
            free[i] = open;
        }
        var best = -1; var bestScore = 0;
        for (var i = 0; i < n; i++)
        {
            if (!free[i]) continue;
            var score = 1 + (free[(i + 1) % n] ? 1 : 0) + (free[(i + n - 1) % n] ? 1 : 0);
            if (score > bestScore) { best = i; bestScore = score; }
        }
        return best < 0 ? null : best * MathF.Tau / n;
    }
}
