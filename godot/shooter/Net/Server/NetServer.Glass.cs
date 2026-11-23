using Godot;
using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Server side of glass panes (ADR 0007, docs/glass.md). The server owns every pane's hit points (<see cref="GlassPanes"/>): the windows
/// of the base map's buildings (windows.json) and the panes of the vehicle types (vehicles.json "glass"). A bullet that stopped on a wall
/// or a hull damages the pane it crossed there, a blast damages the panes in its radius, and the changes of a tick go out as one reliable
/// event per pane. A joiner gets every pane that is not whole. Neither kind of pane has a collider of its own: a building's wall behind
/// its pane stays solid, and a bullet goes through a vehicle's panes (<see cref="BulletHit"/>) and hits what is inside and beyond, damaging every pane it crossed.
/// </summary>
public partial class NetServer
{
    /// <summary>glass.json; the defaults when the host sets none.</summary>
    public GlassConfig GlassData = new();

    readonly GlassPanes _glass = new();
    List<MapGlassPane>? _mapPanes;
    List<GlassPaneRect>? _mapPaneRects;
    int _glassHits, _glassShattered;

    /// <summary>The building panes of the map, in wire order. Empty without a map or a windows.json; a broken file is logged and counts as none.</summary>
    List<MapGlassPane> MapPanes
    {
        get
        {
            if (_mapPanes != null) return _mapPanes;
            try { _mapPanes = MapGlass.Parse(Map?.WindowsJson ?? ""); }
            catch (DataFileException e) { GD.PrintErr($"server: {e.Message}; the map's windows do not break"); _mapPanes = new(); }
            if (MechanicGarage.Current is { } garage) MechanicGarageSite.AddPane(_mapPanes, garage.FloorY); // the mechanic garage's plate (#320)
            _mapPaneRects = _mapPanes.Select(p => p.Rect).ToList();
            if (_mapPanes.Count > 0) GD.Print($"server: {_mapPanes.Count} building panes ({_mapPanes.Count(p => p.Breakable)} breakable)");
            return _mapPanes;
        }
    }

    float VehiclePaneHp(VehicleDef def, int index) => def.Glass[index].Hp ?? GlassData.PaneHp(def.Glass[index].Kind);

    /// <summary>
    /// A bullet, pellet or gun round that hit no player stopped <paramref name="wall"/> metres along the ray, on <paramref name="car"/> or
    /// (null) on the world: the pane it crossed there takes <paramref name="damage"/>. A shattered or unbreakable pane takes nothing.
    /// </summary>
    void ShotGlass(ServerCar? car, Vector3 from, Vector3 dir, float wall, float damage)
    {
        if (float.IsInfinity(wall) || damage <= 0) return;
        if (car != null)
        {
            car.Wake(); // a parked car is stepped again after a shot at it (its glass, and whatever the hit does to it)
            if (car.Destroyed || car.Def.Glass.Count == 0) return;
            var i = VehiclePaneCrossed(car, from, dir, wall);
            if (i >= 0 && _glass.Damage(new GlassPaneKey(car.Id, (ushort)i), damage, VehiclePaneHp(car.Def, i))) _glassHits++;
            return;
        }
        var panes = MapPanes;
        if (panes.Count == 0) return;
        var b = GlassRules.FirstCrossed(_mapPaneRects!, N(from), N(dir), wall - 0.1f, wall + GlassData.BuildingReach, k => !panes[k].Breakable);
        if (b >= 0 && _glass.Damage(new GlassPaneKey(GlassPaneKey.Map, (ushort)b), damage, panes[b].Hp ?? GlassData.BuildingPaneHp)) _glassHits++;
    }

    /// <summary>The pane of <paramref name="car"/> a ray that stopped on its hull <paramref name="wall"/> metres out crosses first, or -1.</summary>
    int VehiclePaneCrossed(ServerCar car, Vector3 from, Vector3 dir, float wall)
    {
        var pose = PoseOf(car); // the panes where the hull was at the shot's view, as the hit was (B-02)
        var (pitch, roll) = car.Def.TiltOf(pose.Pitch, pose.Roll);
        return GlassRules.FirstCrossed(car.Def.GlassRects, GlassRules.ToVehicle(N(from), pose.Feet, pose.Yaw, pitch, roll), GlassRules.DirToVehicle(N(dir), pose.Yaw, pitch, roll), wall - 0.1f, wall + GlassData.VehicleReach);
    }

    /// <summary>A pane a bullet went through: the vehicle, the pane and the metres along the ray.</summary>
    readonly record struct GlassCross(ServerCar Car, int Pane, float Dist);

    /// <summary>Vehicles one bullet may pass through (by a pane or an open side in, and one out).</summary>
    const int MaxGlassPasses = 6;

    /// <summary>
    /// As <see cref="WorldHit"/> for a bullet, pellet or gun round: a vehicle's glass and its open sides do not stop it. A ray that reaches a
    /// vehicle through one of its panes (whole or shattered) or open sides (vehicles.json open_sides: the helicopter's cabin) goes on inside,
    /// so a rider is open to it, and leaves through the next pane or open side it crosses on that vehicle, then on to whatever is beyond
    /// (another window, a player, a wall). With no way out, the hull stops it on its far side (where the ray leaves the hit box), so every rider on the way is open to it; a ray that comes in by neither
    /// is stopped where it met the hull (<see cref="GlassRules.Through"/>). Every pane passed is added to <paramref name="crossed"/> (made
    /// when the first pane is passed, so a shot that meets no glass allocates nothing) for <see cref="DamageCrossed"/>.
    /// <paramref name="ignore"/> is a vehicle the ray does not meet at all (the door gunner's own), <paramref name="solid"/> one whose glass
    /// stops it as its hull does (the roof gunner's own). Returns the distance of the stop, and the vehicle it stopped on (null: the world or nothing).
    /// </summary>
    float BulletHit(Vector3 from, Vector3 dir, float max, out ServerCar? car, ref List<GlassCross>? crossed, ServerCar? ignore = null, ServerCar? solid = null)
    {
        var off = 0f;
        List<ServerCar>? passed = null; // the vehicles this ray has gone through (left out from here on)
        var n = N(from); var nd = N(dir);
        for (var pass = 0; ; pass++)
        {
            // the walls, and the hulls of the vehicles at the shot's view (B-02): the nearest of them, from where the ray is now
            var o = from + dir * off;
            var wallD = WallDistance(o, dir, max - off);
            var hull = NearestHull(o, dir, max - off, ignore, null, passed, out var hullD, out var pose);
            if (hull == null || hullD >= wallD) { car = null; return off + wallD; }
            car = hull;
            var wall = off + hullD;
            if (car == solid || pass >= MaxGlassPasses) return wall;
            var (pitch, roll) = car.Def.TiltOf(pose.Pitch, pose.Roll);
            var lo = GlassRules.ToVehicle(n, pose.Feet, pose.Yaw, pitch, roll); var ld = GlassRules.DirToVehicle(nd, pose.Yaw, pitch, roll);
            var hit = car; // (an out parameter cannot be captured)
            // the pane kinds decide (ADR 0035): a reinforced pane that holds stops the round, a normal one lets it through; the pane it stopped on
            // takes its damage as any pane a round stopped on (ShotGlass), so it is not put in the crossed list
            var way = PaneRules.Pass(car.Def, i => _glass.IsShattered(new GlassPaneKey(hit.Id, (ushort)i)), lo, ld, wall, GlassData.VehicleReach, max);
            if (way.Kind == HullPassKind.Hull) return wall;
            if (way.EntryPane >= 0) (crossed ??= new()).Add(new GlassCross(car, way.EntryPane, way.EntryT)); // (-1: an open side, nothing to damage)
            if (way.Kind != HullPassKind.Through) return way.T;
            if (way.ExitPane >= 0) (crossed ??= new()).Add(new GlassCross(car, way.ExitPane, way.ExitT));
            off = way.ExitT + 0.02f;
            (passed ??= new()).Add(car);
        }
    }

    /// <summary>
    /// Takes out of <paramref name="targets"/> the riders of armoured seats (ADR 0035): no bullet, pellet or mounted-gun round reaches them. Their hit
    /// targets stay in the lists blasts read (<see cref="LiveTargets"/>), which hurt them as every body in range.
    /// </summary>
    void DropArmoured(List<HitTarget> targets) => targets.RemoveAll(IsArmouredRider);

    bool IsArmouredRider(HitTarget t) =>
        _byId.TryGetValue(t.Id, out var o) && o.Car is { } car && o.CarSeat >= 0 && o.CarSeat < car.Def.Seats.Count && !RiderRules.BulletsReach(car.Def, o.CarSeat);

    /// <summary>The panes a bullet went through that lie nearer than <paramref name="before"/> (where it hit a player, else infinity) take <paramref name="damage"/> of their distance each.</summary>
    void DamageCrossed(List<GlassCross>? crossed, float before, Func<float, float> damage)
    {
        if (crossed == null) return;
        foreach (var x in crossed)
        {
            x.Car.Wake(); // a shot through its glass wakes a parked vehicle, as ShotGlass does
            if (x.Dist >= before || x.Car.Destroyed) continue;
            var dmg = damage(x.Dist);
            if (dmg > 0 && _glass.Damage(new GlassPaneKey(x.Car.Id, (ushort)x.Pane), dmg, VehiclePaneHp(x.Car.Def, x.Pane))) _glassHits++;
        }
    }

    /// <summary>A blast at <paramref name="at"/> with <paramref name="radius"/>: every pane in it takes the blast damage of glass.json by its distance.</summary>
    void BlastGlass(Vector3 at, float radius, float? centreDamage = null)
    {
        var blastDamage = centreDamage ?? GlassData.BlastDamage; // the Scud blast passes a damage far above any pane's hit points, so every pane in its radius shatters
        if (radius <= 0 || blastDamage <= 0) return;
        var c = N(at);
        var panes = MapPanes;
        for (var i = 0; i < panes.Count; i++)
        {
            if (!panes[i].Breakable) continue;
            var dmg = GlassRules.BlastDamage(NVec3.Distance(c, panes[i].Rect.Centre), radius, blastDamage);
            if (dmg > 0 && _glass.Damage(new GlassPaneKey(GlassPaneKey.Map, (ushort)i), dmg, panes[i].Hp ?? GlassData.BuildingPaneHp)) _glassHits++;
        }
        foreach (var car in _fleet.Cars)
        {
            if (car.Destroyed || car.Def.Glass.Count == 0) continue;
            var feet = car.HullFeet;
            if (NVec3.Distance(c, feet) > radius + MathF.Max(car.HalfLength + car.Def.Height, car.Def.BroadReach)) continue;
            car.Wake();
            var rects = car.Def.GlassRects;
            var (_, attPitch, attRoll) = AttitudeOf(car);
            var (tiltPitch, tiltRoll) = car.Def.TiltOf(attPitch, attRoll);
            for (var i = 0; i < rects.Count; i++)
            {
                var dmg = GlassRules.BlastDamage(NVec3.Distance(c, GlassRules.FromVehicle(rects[i].Centre, feet, car.State.Yaw, tiltPitch, tiltRoll)), radius, blastDamage);
                if (dmg > 0 && _glass.Damage(new GlassPaneKey(car.Id, (ushort)i), dmg, VehiclePaneHp(car.Def, i))) _glassHits++;
            }
        }
    }

    /// <summary>
    /// A collision took <paramref name="speedLoss"/> m/s from <paramref name="car"/> (a wall, a tree, a landing, another vehicle, #431): from
    /// glass.json crash_crack_speed its standing panes crack, from crash_shatter_speed they all shatter, whatever their kind.
    /// </summary>
    void CrashGlass(ServerCar car, float speedLoss)
    {
        if (car.Destroyed || car.Def.Glass.Count == 0) return;
        var crash = GlassData.Crash(speedLoss);
        if (crash == GlassCrash.None) return;
        for (var i = 0; i < car.Def.Glass.Count; i++)
        {
            var key = new GlassPaneKey(car.Id, (ushort)i);
            var hp = VehiclePaneHp(car.Def, i);
            if (crash == GlassCrash.Shatter ? _glass.Damage(key, hp, hp) : _glass.CrackTo(key, GlassData.CrashCrackFraction, hp)) _glassHits++;
        }
    }

    /// <summary>Sends the panes that changed this tick, one reliable event each (called once at the end of the tick).</summary>
    void FlushGlass()
    {
        var mapPane = false;
        foreach (var s in _glass.TakeChanges())
        {
            if (s.Shattered) _glassShattered++;
            mapPane |= s.Key.Owner == GlassPaneKey.Map;
            BroadcastEvent(EventKind.GlassPane, GlassCodec.EncodePane(s));
        }
        if (mapPane) SyncMechanicGarage();
    }

    /// <summary>The mechanic garage's plate (a map pane) is solid while its pane stands and gone once it has shattered (#320): the box follows the pane's state.</summary>
    void SyncMechanicGarage()
    {
        if (MechanicGarage.Current is not { } garage) return;
        var i = MapPanes.FindIndex(p => p.Node == MechanicGarageSite.PlateNode);
        if (i >= 0) garage.SetPlate(!_glass.IsShattered(new GlassPaneKey(GlassPaneKey.Map, (ushort)i)), animate: false);
    }

    /// <summary>The joiner's catch-up: every pane that is not whole.</summary>
    void SendGlassOnJoin(ServerClient c)
    {
        var damaged = _glass.Damaged();
        if (damaged.Count > 0) Send(c, new EventMsg((byte)EventKind.GlassSync, GlassCodec.EncodeSync(GlassCodec.Everything, damaged)));
    }

    /// <summary>A vehicle came back repaired (after its destruction, or with a new round): its panes are whole again.</summary>
    void RestoreVehicleGlass(ServerCar car)
    {
        if (_glass.Restore(car.Id)) BroadcastEvent(EventKind.GlassSync, GlassCodec.EncodeSync(car.Id, Array.Empty<GlassPaneState>()));
    }

    /// <summary>A new round: every pane is whole again (glass.json restore_on_round_start).</summary>
    void RestoreGlassForRound()
    {
        if (GlassData.RestoreOnRoundStart && _glass.RestoreAll())
        {
            BroadcastEvent(EventKind.GlassSync, GlassCodec.EncodeSync(GlassCodec.Everything, Array.Empty<GlassPaneState>()));
            SyncMechanicGarage();
        }
    }
}
