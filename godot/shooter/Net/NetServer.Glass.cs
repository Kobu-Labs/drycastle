using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

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
        var exclude = ignore == null ? null : new Godot.Collections.Array<Rid>(PlayerRids()) { ignore.GetRid() };
        var n = N(from); var nd = N(dir);
        for (var pass = 0; ; pass++)
        {
            var q = PhysicsRayQueryParameters3D.Create(from + dir * off, from + dir * max);
            q.CollisionMask = CollisionLayers.Bullets;
            q.Exclude = exclude ?? PlayerRids();
            var r = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (r.Count == 0) { car = null; return float.PositiveInfinity; }
            car = r["collider"].As<GodotObject>() as ServerCar;
            var wall = from.DistanceTo(r["position"].AsVector3());
            if (car == null || car == solid || car.Destroyed || pass >= MaxGlassPasses) return wall;
            var panes = car.Def.GlassRects; var open = car.Def.OpenSideRects;
            if (panes.Count == 0 && open.Count == 0) return wall;
            var feet = N(car.GlobalPosition); var yaw = car.State.Yaw;
            var lo = GlassRules.ToVehicle(n, feet, yaw); var ld = GlassRules.DirToVehicle(nd, yaw);
            var way = GlassRules.Through(panes, open, lo, ld, wall, GlassData.VehicleReach);
            if (!way.Enters || way.EntryT > max) return wall; // the body where the ray met it, or the round's range ends before the pane
            if (way.EntryPane >= 0) (crossed ??= new()).Add(new GlassCross(car, way.EntryPane, way.EntryT));
            // no way out, or the round's range ends inside: the hull takes the round on its far side
            if (!way.Leaves || way.ExitT + 0.02f >= max)
                return MathF.Min(MathF.Max(VehicleBox.ExitDistance(lo, ld, car.HalfWidth, car.HalfLength, car.Def.Height), way.EntryT), max);
            if (way.ExitPane >= 0) (crossed ??= new()).Add(new GlassCross(car, way.ExitPane, way.ExitT));
            off = way.ExitT + 0.02f;
            exclude = new Godot.Collections.Array<Rid>(exclude ?? PlayerRids()) { car.GetRid() };
        }
    }

    /// <summary>The panes a bullet went through that lie nearer than <paramref name="before"/> (where it hit a player, else infinity) take <paramref name="damage"/> of their distance each.</summary>
    void DamageCrossed(List<GlassCross>? crossed, float before, Func<float, float> damage)
    {
        if (crossed == null) return;
        foreach (var x in crossed)
        {
            if (x.Dist >= before || x.Car.Destroyed) continue;
            var dmg = damage(x.Dist);
            if (dmg > 0 && _glass.Damage(new GlassPaneKey(x.Car.Id, (ushort)x.Pane), dmg, VehiclePaneHp(x.Car.Def, x.Pane))) _glassHits++;
        }
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
