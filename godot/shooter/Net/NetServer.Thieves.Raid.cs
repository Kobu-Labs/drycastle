using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Raids (the vans of the Thieves event's convoy) on the server (GLOSSARY "Raid", "Raid van"; ADR 0021; docs/events/thieves/03). A Raid is a <see cref="RaidVan"/> (world entity 55) and its
/// <see cref="ThiefGang"/>; <see cref="ThievesEvent"/> says when the next van of the wave is due and this file launches it: a road in from the map edge, the stop with the most targets (the road
/// graph of roads.json, <see cref="RoadGraph"/>), the way out. The van is not a vehicle: it rams players (<see cref="RaidContacts"/>), shoves vehicles aside
/// (<see cref="ShoveCarsByVans"/>), is solid for players while it stands (<see cref="PlaceVanSolids"/>) and is a hit target of two capsules (<see cref="AddVanTargets"/>), rewound from its own
/// pose ring like the Bigfoot's (ADR 0013, <see cref="CaptureVanPoses"/>). Everything it needs from the world is a function: the ground height, the navigation mesh, the cars.
/// </summary>
public partial class NetServer
{
    RaidRoster? _raids;
    RoadGraph? _raidRoads;
    ushort _thievesAdmin;
    readonly Dictionary<RaidVan, VanBody> _vanBodies = new();

    /// <summary>What the server keeps of one van besides the entity: the box players are stopped by, and who it ran over lately (the cooldown of a run-over, as a car's).</summary>
    sealed class VanBody
    {
        public StaticBody3D Solid = null!;
        public bool WasSolid;
        public readonly Dictionary<ushort, double> HitCooldown = new();
    }

    /// <summary>The Raids of the Thieves event (null until <c>_Ready</c>). Ticket 04 reads their vans (<see cref="RaidVan.LoadLoot"/>, <see cref="RaidVan.DoorPoint"/>) and gangs.</summary>
    public RaidRoster? Raids => _raids;

    /// <summary>Mass of a van as one side of a bump: nothing moves it.</summary>
    const float VanMass = 1e7f;

    /// <summary>Called once from <see cref="InitGangs"/>: the van's entity type, the roster behind the match's hit routing, its events.</summary>
    void InitRaids()
    {
        RaidVanRules.Register(WorldEntityRegistry.Shared);
        _raids = new RaidRoster(ThievesData, new GangWorld(this), _match.Entities, _gangs!, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}") };
        _raids.SourceOf = _gangs!.SourceOf;
        _match.Vans = _raids;
        _raids.Honked += r => BroadcastEvent(EventKind.ThievesFx, ThievesFxCodec.Encode(new ThievesFxMsg(ThievesFxKind.Honk, r.Van.Position + NVec3.UnitY, r.VanEntity)));
        _raids.RaidOver += r => GD.Print($"server: Thieves Raid over: its van was {(r.Van.State == RaidVanState.Destroyed ? "destroyed" : "driven off")}, {r.Boarded} of the crew boarded");
        _raids.LootReturned += (r, n) => LootReturnAll(r, n);
    }

    /// <summary>A van was destroyed with <paramref name="loaded"/> Loot in it: the Raid has put each piece back at its place (<see cref="Raid.LootReturned"/>, through the <see cref="LootBook"/>). Only a log line here.</summary>
    void LootReturnAll(Raid raid, int loaded)
    {
        if (loaded > 0) GD.Print($"server: Thieves: {loaded} Loot of a destroyed van goes back to its places");
    }

    /// <summary>The road graph of the map's roads.json, cut to the roads a van may use (nothing is read as a graph before the first Raid).</summary>
    RoadGraph RaidRoads => _raidRoads ??= Map != null ? RoadGraph.Parse(Map.RoadsJson, ThievesData.Van.MinRoadWidthM) : RoadGraph.Empty;

    /// <summary>The fixed routes (<c>raids.routes</c>) resolved on the road graph, once: the ones this map cannot drive are logged and left out. Empty: the automatic choice.</summary>
    List<ResolvedRoute> RaidRouteList => _raidRoutes ??= RaidRoutes.Resolve(RaidRouteRoads, RaidRoads, ThievesData.Raids.Routes, ThievesData.Raids.RouteSnapMaxM, s => GD.Print($"server: {s}"));
    List<ResolvedRoute>? _raidRoutes;

    /// <summary>Where a van parks near: <c>raids.park_centre</c>, else the church's doorway (church.json), else null (the end of its route).</summary>
    NVec2? RaidParkCentre()
    {
        if (ThievesData.Raids.ParkCentre is { } c) return new NVec2(c[0], c[1]);
        return MapChurch is { } church ? new NVec2(church.DoorwayPos.X, church.DoorwayPos.Z) : null;
    }

    /// <summary>The roads the fixed routes are driven over: every road wide enough (<c>raids.route_min_road_width_m</c>, 0: footpaths too).</summary>
    RoadGraph RaidRouteRoads => _raidRouteRoads ??= Map != null ? RoadGraph.Parse(Map.RoadsJson, ThievesData.Raids.RouteMinRoadWidthM) : RoadGraph.Empty;
    RoadGraph? _raidRouteRoads;

    /// <summary>The map's extent as the Raids see it (the terrain's box, as for a helicopter: where a road crosses it, a van comes in). Infinite on both sides without a map: no road crosses it.</summary>
    (NVec2 Min, NVec2 Max) RaidExtent()
    {
        var b = MapFlightBounds;
        return (new NVec2(b.MinX, b.MinZ), new NVec2(b.MaxX, b.MaxZ));
    }

    float RaidGround(float x, float z) => Map == null ? float.NaN : Map.GroundHeight(x, z);

    /// <summary>Humans in the match (the crew grows with them); at least one.</summary>
    int RaidHumans() => Math.Max(1, _byId.Values.Count(c => !c.IsBot));

    /// <summary>The places something can be stolen at, for the stop choice (<see cref="RaidPlanner"/>): the registry of Loot kinds says (<see cref="LootPlaces"/>; a new kind adds nothing here).</summary>
    List<NVec2> RaidTargetPoints() => LootPlaces();

    /// <summary>A point to bring a fallback van near: the admin who started the event, else any human, else the map's middle.</summary>
    NVec2 RaidNearPoint()
    {
        if (_thievesAdmin != 0 && _byId.TryGetValue(_thievesAdmin, out var admin)) return new NVec2(admin.Body.GlobalPosition.X, admin.Body.GlobalPosition.Z);
        foreach (var c in _byId.Values)
            if (!c.IsBot) return new NVec2(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Z);
        var (min, max) = RaidExtent();
        return float.IsFinite(min.X) && float.IsFinite(max.X) ? (min + max) / 2 : NVec2.Zero;
    }

    /// <summary>Whether a van can stop at a road spot: the crew steps out on the navigation mesh beside a side door. Returns the side (1 right, -1 left) that reaches the mesh, or null.</summary>
    int? AcceptRaidStop(NVec2 at, float yaw)
    {
        if (_nav == null) return null;
        var y = RaidGround(at.X, at.Y);
        if (float.IsNaN(y)) return null;
        var vc = ThievesData.Van;
        var fwd = new NVec2(-MathF.Sin(yaw), -MathF.Cos(yaw));
        var right = new NVec2(MathF.Cos(yaw), -MathF.Sin(yaw));
        foreach (var side in new[] { 1, -1 })
        {
            var door = at + right * (side * (vc.HalfWidthM + vc.DoorOutM)) + fwd * vc.DoorAlongM;
            var snap = _nav.Snap(new Vector3(door.X, y, door.Y));
            if (MathF.Abs(snap.X - door.X) <= 3f && MathF.Abs(snap.Z - door.Y) <= 3f && MathF.Abs(snap.Y - y) <= 4f) return side;
        }
        return null;
    }

    /// <summary>The route the first van of the wave took: the others follow it (null: the automatic way in, or no wave yet).</summary>
    ResolvedRoute? _waveRoute;

    /// <summary>
    /// The next van of the wave is due (<see cref="ThievesEvent.Tick"/>). The first picks one of the fixed routes (<see cref="RaidRoutes"/>), every other van of the wave drives the same one in a line and
    /// parks at a stop of its own near the church (not within <c>used_apart_m</c> of the others'), so they split in the village; all leave the same way back. With no usable route the old automatic
    /// choice: a road in from the map edge to the stop with the most targets. When the first van finds no way in at all it is put parked at a road point near the admin instead (fallback); a later
    /// van that finds no stop of its own, or no room in the world, is dropped: the wave is one van smaller (<see cref="ThievesEvent.Shrink"/>) and the log says so.
    /// </summary>
    void LaunchRaid()
    {
        if (_raids == null || _nav == null || _thieves == null) return;
        var vc = ThievesData.Van;
        var roads = RaidRoads;
        var (min, max) = RaidExtent();
        var n = _thieves.Launched; var of = _thieves.Total;
        var first = n == 1;
        if (first) _waveRoute = null;
        var players = _byId.Values.Where(c => !c.IsBot && _match.Get(c.Id)?.Life == PlayerLife.Alive).Select(c => new NVec2(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Z)).ToList();
        RaidPlan? plan = null;
        var routes = first ? RaidRouteList : _waveRoute != null ? new List<ResolvedRoute> { _waveRoute } : new List<ResolvedRoute>();
        if (routes.Count > 0)
        {
            plan = RaidRoutes.Plan(roads, routes, RaidParkCentre(), _raids.UsedStops, vc, ThievesData.Raids, _rng, AcceptRaidStop);
            if (plan == null) GD.Print($"server: Thieves van {n}/{of}: no {(first ? "fixed route has" : "stop left on the wave's route")} a stop the crew can step out at{(first ? ", the automatic way in is used" : "")}");
            else if (first) _waveRoute = routes.FirstOrDefault(r => r.Name == plan.RouteName);
        }
        else if (first && ThievesData.Raids.Routes.Count > 0) GD.Print($"server: Thieves van {n}/{of}: no fixed route is usable on this map, the automatic way in is used");
        if (plan == null && (first || _waveRoute == null)) plan = RaidPlanner.Plan(roads, min, max, RaidTargetPoints(), _raids.UsedStops, players, vc, _rng, AcceptRaidStop);
        if (plan == null && first)
        {
            GD.Print($"server: Thieves van {n}/{of}: no way in from the map edge ({(roads.IsEmpty ? "no road graph" : "no entry point or no reachable stop")}), the van is parked near the admin instead (fallback)");
            plan = RaidPlanner.Fallback(roads, RaidNearPoint(), 25f, _rng, AcceptRaidStop);
        }
        if (plan == null) { ShrinkWave($"no stop of its own was found{(first ? " (no road with the navigation mesh beside it)" : "")}"); return; }
        var exit = plan.Out ?? RaidPlanner.Exit(roads, plan, min, max, vc);
        var start0 = plan.In.IsEmpty ? plan.StopAt : plan.In.Start;
        var y = Map?.GroundHeightOrMean(start0.X, start0.Y) ?? 0f; // the terrain is a ragged patch inside its box, so the edge may lie past it: the mean height then, and the van takes the ground's as soon as it has one
        var start = new NVec3(start0.X, y, start0.Y);
        var crew = ThievesData.Raids.CrewFor(ThievesData.Gang.Raid, RaidHumans());
        var raid = _raids.Launch(plan, exit, start, crew, p => N(_nav!.Snap(new Vector3(p.X, p.Y, p.Z))), RaidFleePoint, RaidGround);
        if (raid == null) { ShrinkWave("the world has no room for it"); return; }
        GD.Print($"server: Thieves van {n}/{of}: {(plan.Fallback ? "parked (fallback)" : plan.RouteName != null ? $"on route '{plan.RouteName}' from ({start0.X:F0},{start0.Y:F0}), {plan.In.Length:F0} m of road" : $"in from ({start0.X:F0},{start0.Y:F0}), {plan.In.Length:F0} m of road")} to the stop at ({plan.StopAt.X:F0},{plan.StopAt.Y:F0}) with {plan.Targets} targets near, crew of {crew.Size}");
    }

    /// <summary>The van that was due could not be launched: the wave loses it (the event is won with the vans it has).</summary>
    void ShrinkWave(string why)
    {
        GD.PrintErr($"server: Thieves van {_thieves!.Launched}/{_thieves.Total}: {why}, the wave is {_thieves.Total - 1} van(s) instead of {_thieves.Total}");
        _thieves.Shrink();
    }

    /// <summary>Where a crew that flees heads for: the nearest point of the map's edge (a point far to the east without a map).</summary>
    NVec3 RaidFleePoint(NVec3 from)
    {
        var (min, max) = RaidExtent();
        return float.IsFinite(min.X) && float.IsFinite(max.X) ? GangPlacement.NearestEdge(from, min, max) : from + new NVec3(1000, 0, 0);
    }

    // ---- the tick ----

    /// <summary>Once a tick after the entities tick: the Raids (the crew gets out, the end of a Raid is seen) and the vans' contact with the world.</summary>
    void RaidsTick(float dt)
    {
        if (_raids == null) return;
        _raids.Tick(dt);
        LootVehiclesTick(dt);
        RaidContacts();
    }

    /// <summary>
    /// The van rams what its box sweeps over this tick (it never brakes): everything its row of the rule table runs over (<see cref="RunOverTargets"/>: players on foot as a car does,
    /// <see cref="RunOver"/> by its speed and mass, a roadkill thrown ahead; the Gang members of other Raids, citizens, workers, animals and dummies). Its own crew is not run over.
    /// </summary>
    void RaidContacts()
    {
        if (_raids == null || _match.Phase != MatchPhase.Running) return;
        foreach (var raid in _raids.Raids)
        {
            var van = raid.Van;
            if (!van.Alive || van.Ended != null || van.Speed < 0.3f || !_vanBodies.TryGetValue(van, out var body)) continue;
            var dmg = Vehicles != null ? RunOver.Damage(Vehicles, van.Speed, ThievesData.Van.RamMassScale) : ThievesData.Van.RamFallbackDamage;
            if (dmg <= 0) continue;
            var vc = ThievesData.Van;
            var crew = raid.Gang.Members;
            RunOverTargets(new RunOverSweep(MoverKind.RaidVan, van.PrevPosition, van.Position, van.Yaw, vc.HalfWidthM, vc.HalfLengthM, van.Forward, van.Speed, dmg, RaidVanRules.HitIdOf(van.Slot, 0), body.HitCooldown),
                spare: id => { for (var i = 0; i < crew.Count; i++) if (crew[i].HitId == id) return true; return false; }); // its own crew is not run over
        }
    }

    /// <summary>
    /// The van's box against every vehicle (called from <see cref="ResolveVehicleContacts"/>): a vehicle that overlaps it is shoved out, by the van's heading when it drives (it never brakes)
    /// and just out of the way when it stands (it is solid for vehicles too). A shoved car takes crash damage by the speed it lost, as in a bump between cars.
    /// </summary>
    void ShoveCarsByVans()
    {
        if (_raids == null || _raids.Raids.Count == 0 || Vehicles == null) return;
        foreach (var raid in _raids.Raids)
        {
            var van = raid.Van;
            if (van.Ended != null) continue;
            var shape = van.Shape;
            var body = new BumpBody(VanMass, van.Velocity, false);
            foreach (var car in _cars)
            {
                if (car.Destroyed || car.Thrown != null) continue;
                var p = car.GlobalPosition;
                if (MathF.Abs(p.X - van.Position.X) > ContactBroadRange || MathF.Abs(p.Z - van.Position.Z) > ContactBroadRange) continue;
                if (VehicleContact.Test(shape, car.ContactShape(), car.Flies) is not { } hit) continue;
                var bump = VehicleContact.Bump(hit, body, car.BumpBody(), Vehicles.BumpRestitution);
                car.ApplyBump(bump.MoveB, bump.DeltaVB);
                Crash(car, bump.SpeedLossB, 0);
            }
        }
    }

    /// <summary>
    /// Before the players move: the box each van that stands is solid with (the clients build the same one from the van's state, so a predicted move and the server's are stopped by the
    /// same box), and any player the van stopped on is put out of it. Boxes of vans that are gone are freed.
    /// </summary>
    void PlaceVanSolids()
    {
        if (_raids == null) return;
        if (_vanBodies.Count > 0)
            foreach (var van in _vanBodies.Keys.ToList())
                if (van.Ended != null || !_raids.Raids.Any(r => r.Van == van)) { _vanBodies[van].Solid.QueueFree(); _vanBodies.Remove(van); }
        foreach (var raid in _raids.Raids)
        {
            var van = raid.Van;
            if (van.Ended != null) continue;
            var vc = ThievesData.Van;
            if (!_vanBodies.TryGetValue(van, out var body)) _vanBodies[van] = body = new VanBody { Solid = VehicleCollider.Make(World, vc.HalfWidthM, vc.HalfLengthM, vc.HeightM) };
            var p = van.Position;
            var at = new Vector3(Quant.Pos(Quant.Pos(p.X)), Quant.Pos(Quant.Pos(p.Y)), Quant.Pos(Quant.Pos(p.Z)));
            VehicleCollider.Place(body.Solid, at, Quant.Yaw(Quant.Yaw(van.Yaw)), van.Solid);
            if (van.Solid && !body.WasSolid) PushOutOfVan(van);
            body.WasSolid = van.Solid;
        }
    }

    /// <summary>A van just stopped: whoever is inside its box walks out through the nearest side (they would be stuck in its solid box).</summary>
    void PushOutOfVan(RaidVan van)
    {
        var vc = ThievesData.Van;
        foreach (var c in _byId.Values)
        {
            if (c.Car != null) continue;
            var feet = N(c.Body.GlobalPosition);
            var to = RaidVanRules.PushOut(van.Position, van.Yaw, vc.HalfWidthM, vc.HalfLengthM, feet, 0.7f);
            if (to == feet) continue;
            c.Body.Teleport(new Vector3(to.X, to.Y, to.Z));
            _match.SetPosition(c.Id, to);
        }
    }

    // ---- hit targets ----

    /// <summary>The living vans as hit targets (two capsules each), only while damage can apply to them. With a view tick: where the shooter saw them.</summary>
    void AddVanTargets(List<HitTarget> into, uint? view = null)
    {
        if (_raids != null && _match.Phase == MatchPhase.Running) _raids.AddTargets(into, view);
    }

    void CaptureVanPoses() => _raids?.RecordPoses(_tick);

    /// <summary>The event ended or a new round began: every Raid goes, vans, crews and the dead too; the boxes are freed on the next tick.</summary>
    void ClearRaids() => _raids?.Clear();
}
