using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Server side of glass panes (ADR 0007, docs/glass.md). The server owns every pane's hit points (<see cref="GlassPanes"/>): the windows
/// of the base map's buildings (windows.json) and the panes of the vehicle types (vehicles.json "glass"). A bullet that stopped on a wall
/// or a hull damages the pane it crossed there, a blast damages the panes in its radius, and the changes of a tick go out as one reliable
/// event per pane. A joiner gets every pane that is not whole. Neither kind of pane has a collider of its own: a building's wall behind
/// its pane stays solid, and a shattered vehicle pane only lets shots through to the riders (glass.json shattered_exposes_riders).
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
            _mapPaneRects = _mapPanes.Select(p => p.Rect).ToList();
            if (_mapPanes.Count > 0) GD.Print($"server: {_mapPanes.Count} building panes ({_mapPanes.Count(p => p.Breakable)} breakable)");
            return _mapPanes;
        }
    }

    float VehiclePaneHp(VehicleDef def, int index) => def.Glass[index].Hp ?? GlassData.VehiclePaneHp;

    /// <summary>
    /// A bullet, pellet or gun round that hit no player stopped <paramref name="wall"/> metres along the ray, on <paramref name="car"/> or
    /// (null) on the world: the pane it crossed there takes <paramref name="damage"/>. A shattered or unbreakable pane takes nothing.
    /// </summary>
    void ShotGlass(ServerCar? car, Vector3 from, Vector3 dir, float wall, float damage)
    {
        if (float.IsInfinity(wall) || damage <= 0) return;
        if (car != null)
        {
            if (car.Destroyed || car.Def.Glass.Count == 0) return;
            var i = VehiclePaneCrossed(car, from, dir, wall);
            if (i >= 0 && _glass.Damage(new GlassPaneKey(car.Id, (ushort)i), damage, VehiclePaneHp(car.Def, i))) _glassHits++;
            return;
        }
        var panes = MapPanes;
        if (panes.Count == 0) return;
        var b = GlassRules.FirstCrossed(_mapPaneRects!, N(from), N(dir), wall - 0.1f, wall + GlassData.BuildingReach, k => !panes[k].Breakable);
        if (b >= 0 && _glass.Damage(new GlassPaneKey(GlassPaneKey.Map, (ushort)b), damage, GlassData.BuildingPaneHp)) _glassHits++;
    }

    /// <summary>The pane of <paramref name="car"/> a ray that stopped on its hull <paramref name="wall"/> metres out crosses first, or -1.</summary>
    int VehiclePaneCrossed(ServerCar car, Vector3 from, Vector3 dir, float wall)
    {
        var feet = N(car.GlobalPosition); var yaw = car.State.Yaw;
        return GlassRules.FirstCrossed(car.Def.GlassRects, GlassRules.ToVehicle(N(from), feet, yaw), GlassRules.DirToVehicle(N(dir), yaw), wall - 0.1f, wall + GlassData.VehicleReach);
    }

    /// <summary>
    /// A rider of <paramref name="hull"/> the ray reaches through a shattered pane (the nearest one whose body it crosses), or null: the
    /// pane it crosses stands, the vehicle has no glass there, nobody sits behind it, or glass.json keeps riders behind the hull.
    /// </summary>
    HitTarget? RiderBehindShatteredGlass(ServerCar hull, Vector3 from, Vector3 dir, float max, IReadOnlyList<HitTarget> targets)
    {
        if (!GlassData.ShatteredExposesRiders || hull.Def.Glass.Count == 0) return null;
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * max);
        q.CollisionMask = CollisionLayers.Bullets;
        q.Exclude = PlayerRids();
        var r = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (r.Count == 0 || r["collider"].As<GodotObject>() != hull) return null;
        var pane = VehiclePaneCrossed(hull, from, dir, from.DistanceTo(r["position"].AsVector3()));
        if (pane < 0 || !_glass.IsShattered(new GlassPaneKey(hull.Id, (ushort)pane))) return null;
        var riders = new List<HitTarget>();
        foreach (var t in targets) if (hull.Seat.SeatOf(t.Id) >= 0) riders.Add(t);
        if (riders.Count == 0) return null;
        // no shooter to leave out here: GunnerCover refuses a shooter who is the target himself
        if (HitMath.Raycast(N(from), N(dir), max, float.PositiveInfinity, riders, ushort.MaxValue) is not { } hit) return null;
        foreach (var t in riders) if (t.Id == hit.Id) return t;
        return null;
    }

    /// <summary>A blast at <paramref name="at"/> with <paramref name="radius"/>: every pane in it takes the blast damage of glass.json by its distance.</summary>
    void BlastGlass(Vector3 at, float radius)
    {
        if (radius <= 0 || GlassData.BlastDamage <= 0) return;
        var c = N(at);
        var panes = MapPanes;
        for (var i = 0; i < panes.Count; i++)
        {
            if (!panes[i].Breakable) continue;
            var dmg = GlassRules.BlastDamage(NVec3.Distance(c, panes[i].Rect.Centre), radius, GlassData.BlastDamage);
            if (dmg > 0 && _glass.Damage(new GlassPaneKey(GlassPaneKey.Map, (ushort)i), dmg, GlassData.BuildingPaneHp)) _glassHits++;
        }
        foreach (var car in _cars)
        {
            if (car.Destroyed || car.Def.Glass.Count == 0) continue;
            var feet = N(car.GlobalPosition);
            if (NVec3.Distance(c, feet) > radius + car.HalfLength + car.Def.Height) continue;
            var rects = car.Def.GlassRects;
            for (var i = 0; i < rects.Count; i++)
            {
                var dmg = GlassRules.BlastDamage(NVec3.Distance(c, GlassRules.FromVehicle(rects[i].Centre, feet, car.State.Yaw)), radius, GlassData.BlastDamage);
                if (dmg > 0 && _glass.Damage(new GlassPaneKey(car.Id, (ushort)i), dmg, VehiclePaneHp(car.Def, i))) _glassHits++;
            }
        }
    }

    /// <summary>Sends the panes that changed this tick, one reliable event each (called once at the end of the tick).</summary>
    void FlushGlass()
    {
        foreach (var s in _glass.TakeChanges())
        {
            if (s.Shattered) _glassShattered++;
            BroadcastEvent(EventKind.GlassPane, GlassCodec.EncodePane(s));
        }
    }

    /// <summary>The joiner's catch-up: every pane that is not whole.</summary>
    void SendGlassOnJoin(Client c)
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
        if (GlassData.RestoreOnRoundStart && _glass.RestoreAll()) BroadcastEvent(EventKind.GlassSync, GlassCodec.EncodeSync(GlassCodec.Everything, Array.Empty<GlassPaneState>()));
    }
}
