using Godot;
using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Rewind;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Panes;

/// <summary>A pane a bullet went through: the vehicle, the pane and the metres along the ray.</summary>
internal readonly record struct GlassCross(ServerCar Car, int Pane, float Dist);

/// <summary>
/// Server side of glass panes (ADR 0007, docs/glass.md). The server owns every pane's hit points (<see cref="GlassPanes"/>): the windows
/// of the base map's buildings (windows.json) and the panes of the vehicle types (vehicles.json "glass"). A bullet that stopped on a wall
/// or a hull damages the pane it crossed there, a blast damages the panes in its radius, and the changes of a tick go out as one reliable
/// event per pane. A joiner gets every pane that is not whole. Neither kind of pane has a collider of its own: a building's wall behind
/// its pane stays solid, and a bullet goes through a vehicle's panes (<see cref="ShotWorld.BulletHit"/>) and hits what is inside and beyond, damaging every pane it crossed.
/// </summary>
internal sealed class GlassServer
{
    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly PoseRewind _rewind;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly GlassConfig _data;
    readonly GlassPanes _glass = new();
    List<MapGlassPane>? _mapPanes;
    List<GlassPaneRect>? _mapPaneRects;

    public GlassServer(ServerContext ctx, Fleet fleet, PoseRewind rewind, Drycastle.MapLoader.MapLoader? map, GlassConfig data)
    {
        _ctx = ctx; _fleet = fleet; _rewind = rewind; _map = map; _data = data;
    }

    /// <summary>How far past a hull a pane of a vehicle still counts as crossed by the ray (glass.json).</summary>
    public float VehicleReach => _data.VehicleReach;

    public bool IsShattered(ServerCar car, int pane) => _glass.IsShattered(new GlassPaneKey(car.Id, (ushort)pane));

    /// <summary>The building panes of the map, in wire order. Empty without a map or a windows.json; a broken file is logged and counts as none.</summary>
    List<MapGlassPane> MapPanes
    {
        get
        {
            if (_mapPanes != null) return _mapPanes;
            try { _mapPanes = MapGlass.Parse(_map?.WindowsJson ?? ""); }
            catch (DataFileException e) { GD.PrintErr($"server: {e.Message}; the map's windows do not break"); _mapPanes = new(); }
            if (MechanicGarage.Current is { } garage) MechanicGarageSite.AddPane(_mapPanes, garage.FloorY); // the mechanic garage's plate (#320)
            _mapPaneRects = _mapPanes.Select(p => p.Rect).ToList();
            if (_mapPanes.Count > 0) GD.Print($"server: {_mapPanes.Count} building panes ({_mapPanes.Count(p => p.Breakable)} breakable)");
            return _mapPanes;
        }
    }

    float VehiclePaneHp(VehicleDef def, int index) => def.Glass[index].Hp ?? _data.PaneHp(def.Glass[index].Kind);

    // ---- what hits glass ----

    /// <summary>
    /// A bullet, pellet or gun round that hit no player stopped <paramref name="wall"/> metres along the ray, on <paramref name="car"/> or
    /// (null) on the world: the pane it crossed there takes <paramref name="damage"/>. A shattered or unbreakable pane takes nothing.
    /// </summary>
    public void Shot(ServerCar? car, Vector3 from, Vector3 dir, float wall, float damage)
    {
        if (float.IsInfinity(wall) || damage <= 0) return;
        if (car != null)
        {
            car.Wake(); // a parked car is stepped again after a shot at it (its glass, and whatever the hit does to it)
            if (car.Destroyed || car.Def.Glass.Count == 0) return;
            var i = VehiclePaneCrossed(car, from, dir, wall);
            if (i >= 0) _glass.Damage(new GlassPaneKey(car.Id, (ushort)i), damage, VehiclePaneHp(car.Def, i));
            return;
        }
        var panes = MapPanes;
        if (panes.Count == 0) return;
        var b = GlassRules.FirstCrossed(_mapPaneRects!, from.ToNumerics(), dir.ToNumerics(), wall - 0.1f, wall + _data.BuildingReach, k => !panes[k].Breakable);
        if (b >= 0) _glass.Damage(new GlassPaneKey(GlassPaneKey.Map, (ushort)b), damage, panes[b].Hp ?? _data.BuildingPaneHp);
    }

    /// <summary>The pane of <paramref name="car"/> a ray that stopped on its hull <paramref name="wall"/> metres out crosses first, or -1.</summary>
    int VehiclePaneCrossed(ServerCar car, Vector3 from, Vector3 dir, float wall)
    {
        var pose = _rewind.PoseOf(car); // the panes where the hull was at the shot's view, as the hit was (B-02)
        var (pitch, roll) = car.Def.TiltOf(pose.Pitch, pose.Roll);
        return GlassRules.FirstCrossed(car.Def.GlassRects, GlassRules.ToVehicle(from.ToNumerics(), pose.Feet, pose.Yaw, pitch, roll), GlassRules.DirToVehicle(dir.ToNumerics(), pose.Yaw, pitch, roll), wall - 0.1f, wall + _data.VehicleReach);
    }

    /// <summary>The panes a bullet went through that lie nearer than <paramref name="before"/> (where it hit a player, else infinity) take <paramref name="damage"/> of their distance each.</summary>
    public void DamageCrossed(List<GlassCross>? crossed, float before, Func<float, float> damage)
    {
        if (crossed == null) return;
        foreach (var x in crossed)
        {
            x.Car.Wake(); // a shot through its glass wakes a parked vehicle, as Shot does
            if (x.Dist >= before || x.Car.Destroyed) continue;
            var dmg = damage(x.Dist);
            if (dmg > 0) _glass.Damage(new GlassPaneKey(x.Car.Id, (ushort)x.Pane), dmg, VehiclePaneHp(x.Car.Def, x.Pane));
        }
    }

    /// <summary>A blast at <paramref name="at"/> with <paramref name="radius"/>: every pane in it takes the blast damage of glass.json by its distance.</summary>
    public void Blast(Vector3 at, float radius, float? centreDamage = null)
    {
        var blastDamage = centreDamage ?? _data.BlastDamage; // the Scud blast passes a damage far above any pane's hit points, so every pane in its radius shatters
        if (radius <= 0 || blastDamage <= 0) return;
        var c = at.ToNumerics();
        var panes = MapPanes;
        for (var i = 0; i < panes.Count; i++)
        {
            if (!panes[i].Breakable) continue;
            var dmg = GlassRules.BlastDamage(NVec3.Distance(c, panes[i].Rect.Centre), radius, blastDamage);
            if (dmg > 0) _glass.Damage(new GlassPaneKey(GlassPaneKey.Map, (ushort)i), dmg, panes[i].Hp ?? _data.BuildingPaneHp);
        }
        foreach (var car in _fleet.Cars) BlastCar(car, c, radius, blastDamage);
    }

    void BlastCar(ServerCar car, NVec3 c, float radius, float blastDamage)
    {
        if (car.Destroyed || car.Def.Glass.Count == 0) return;
        var feet = car.HullFeet;
        if (NVec3.Distance(c, feet) > radius + MathF.Max(car.HalfLength + car.Def.Height, car.Def.BroadReach)) return;
        car.Wake();
        var rects = car.Def.GlassRects;
        var (_, attPitch, attRoll) = Fleet.AttitudeOf(car);
        var (tiltPitch, tiltRoll) = car.Def.TiltOf(attPitch, attRoll);
        for (var i = 0; i < rects.Count; i++)
        {
            var dmg = GlassRules.BlastDamage(NVec3.Distance(c, GlassRules.FromVehicle(rects[i].Centre, feet, car.State.Yaw, tiltPitch, tiltRoll)), radius, blastDamage);
            if (dmg > 0) _glass.Damage(new GlassPaneKey(car.Id, (ushort)i), dmg, VehiclePaneHp(car.Def, i));
        }
    }

    /// <summary>
    /// A collision took <paramref name="speedLoss"/> m/s from <paramref name="car"/> (a wall, a tree, a landing, another vehicle, #431): from
    /// glass.json crash_crack_speed its standing panes crack, from crash_shatter_speed they all shatter, whatever their kind.
    /// </summary>
    public void Crash(ServerCar car, float speedLoss)
    {
        if (car.Destroyed || car.Def.Glass.Count == 0) return;
        var crash = _data.Crash(speedLoss);
        if (crash == GlassCrash.None) return;
        for (var i = 0; i < car.Def.Glass.Count; i++)
        {
            var key = new GlassPaneKey(car.Id, (ushort)i);
            var hp = VehiclePaneHp(car.Def, i);
            if (crash == GlassCrash.Shatter) _glass.Damage(key, hp, hp); else _glass.CrackTo(key, _data.CrashCrackFraction, hp);
        }
    }

    // ---- what the clients are told ----

    /// <summary>Sends the panes that changed this tick, one reliable event each (called once at the end of the tick).</summary>
    public void Flush()
    {
        var mapPane = false;
        foreach (var s in _glass.TakeChanges())
        {
            mapPane |= s.Key.Owner == GlassPaneKey.Map;
            _ctx.Broadcast(EventKind.GlassPane, GlassCodec.EncodePane(s));
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
    public void SendOnJoin(ServerClient c)
    {
        var damaged = _glass.Damaged();
        if (damaged.Count > 0) _ctx.SendEvent(c, EventKind.GlassSync, GlassCodec.EncodeSync(GlassCodec.Everything, damaged));
    }

    /// <summary>A vehicle came back repaired (after its destruction, or with a new round): its panes are whole again.</summary>
    public void RestoreCar(ServerCar car)
    {
        if (_glass.Restore(car.Id)) _ctx.Broadcast(EventKind.GlassSync, GlassCodec.EncodeSync(car.Id, Array.Empty<GlassPaneState>()));
    }

    /// <summary>A new round: every pane is whole again (glass.json restore_on_round_start).</summary>
    public void RestoreForRound()
    {
        if (_data.RestoreOnRoundStart && _glass.RestoreAll())
        {
            _ctx.Broadcast(EventKind.GlassSync, GlassCodec.EncodeSync(GlassCodec.Everything, Array.Empty<GlassPaneState>()));
            SyncMechanicGarage();
        }
    }

}
