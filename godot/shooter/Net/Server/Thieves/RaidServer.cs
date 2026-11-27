using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>
/// The Raids (the vans of the Thieves event's convoy) on the server (GLOSSARY "Raid", "Raid van"; ADR 0021; docs/events/thieves/03). A Raid is a <see cref="RaidVan"/> (world entity 55) and its
/// <see cref="ThiefGang"/>; <see cref="ThievesEvent"/> says when the next van of the wave is due and <see cref="RaidLauncher"/> launches it. The van is not a vehicle: it rams players
/// (<see cref="Contacts"/>), shoves vehicles aside (<see cref="ShoveCars"/>), is solid for players while it stands (<see cref="PlaceSolids"/>) and is a hit target of two capsules
/// (<see cref="AddTargets"/>), rewound from its own pose ring like the Bigfoot's (ADR 0013, <see cref="RecordPoses"/>).
/// </summary>
internal sealed class RaidServer
{
    /// <summary>Mass of a van as one side of a bump: nothing moves it.</summary>
    const float VanMass = 1e7f;

    readonly ServerContext _ctx;
    readonly ThievesFile _data;
    readonly Node3D _world;
    readonly Fleet _fleet;
    readonly Func<VehiclesFile?> _vehicles;
    readonly Action<RunOverSweep, Func<ushort, bool>?> _runOver;
    readonly Action<ServerCar, float, ushort> _crash;
    readonly RaidLauncher _launcher;
    readonly Dictionary<RaidVan, VanBody> _bodies = new();

    /// <summary>What the server keeps of one van besides the entity: the box players are stopped by, and who it ran over lately (the cooldown of a run-over, as a car's).</summary>
    sealed class VanBody
    {
        public StaticBody3D Solid = null!;
        public bool WasSolid;
        public readonly Dictionary<ushort, double> HitCooldown = new();
    }

    /// <param name="runOver">The vehicles' run-over rule: sweeps a footprint over the live targets (the van's own crew is spared).</param>
    /// <param name="crash">The one crash rule: a vehicle that lost speed to a bump.</param>
    public RaidServer(ServerContext ctx, ThievesFile data, ThievesEvent wave, GangServer gangs, Drycastle.MapLoader.MapLoader? map, Node3D world, Fleet fleet, Func<VehiclesFile?> vehicles,
        Func<BotNav?> nav, Func<FlightBounds> bounds, Func<NVec2?> churchDoorway, Random rng, Action<RunOverSweep, Func<ushort, bool>?> runOver, Action<ServerCar, float, ushort> crash)
    {
        _ctx = ctx; _data = data; _world = world; _fleet = fleet; _vehicles = vehicles; _runOver = runOver; _crash = crash;
        RaidVanRules.Register(WorldEntityRegistry.Shared);
        Roster = new RaidRoster(data, gangs, ctx.Match.Entities, gangs.Roster, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}") };
        Roster.SourceOf = gangs.Roster.SourceOf;
        ctx.Match.Vans = Roster;
        Roster.Honked += r => ctx.Broadcast(EventKind.ThievesFx, ThievesFxCodec.Encode(new ThievesFxMsg(ThievesFxKind.Honk, r.Van.Position + NVec3.UnitY, r.VanEntity)));
        Roster.RaidOver += r => GD.Print($"server: Thieves Raid over: its van was {(r.Van.State == RaidVanState.Destroyed ? "destroyed" : "driven off")}, {r.Boarded} of the crew boarded");
        Roster.LootReturned += (r, n) => { if (n > 0) GD.Print($"server: Thieves: {n} Loot of a destroyed van goes back to its places"); };
        _launcher = new RaidLauncher(ctx, data, wave, Roster, map, nav, bounds, churchDoorway, rng);
    }

    public RaidRoster Roster { get; }

    /// <summary>The admin who started the event: a van with no way in is parked near them.</summary>
    public ushort Admin { set => _launcher.Admin = value; }

    public bool NoRoads => _launcher.NoRoads;

    /// <summary>The roads a van may use (a Heist puts its Cartel van on the spot nearest the Contact).</summary>
    public RoadGraph Roads => _launcher.Roads;

    /// <summary>A Heist's Assault: see <see cref="RaidLauncher.LaunchAssault"/>.</summary>
    public Raid? LaunchAssault(NVec2 target, IReadOnlyList<NVec2> used, IReadOnlyList<GangMemberKind> order, ThiefGang gang, NVec3 post) => _launcher.LaunchAssault(target, used, order, gang, post);

    /// <summary>Removes one Raid (a Heist's Assault when its Job ends): its van, its crew, the dead too.</summary>
    public void Remove(Raid raid) => Roster.Remove(raid);

    public int Active => Roster.Active;

    /// <summary>The next van of the wave is due; <paramref name="places"/> are the spots something can be stolen at.</summary>
    public void Launch(Func<List<NVec2>> places) => _launcher.Launch(places);

    /// <summary>Once a tick after the entities tick: the Raids (the crew gets out, the end of a Raid is seen).</summary>
    public void Tick(float dt) => Roster.Tick(dt);

    /// <summary>The event ended or a new round began: every Raid goes, vans, crews and the dead too; the boxes are freed on the next tick.</summary>
    public void Clear() => Roster.Clear();

    /// <summary>A vehicle ran over a member of a Raid's crew: a van has no player to credit, the member is simply killed (a roadkill kills outright).</summary>
    public void RoadkillCrew(ushort hitId)
    {
        foreach (var raid in Roster.Raids) raid.Gang.Hit(hitId, 0, SpecialWeapons.Vehicle, 1, KillCause.Roadkill);
    }

    /// <summary>
    /// The van rams what its box sweeps over this tick (it never brakes): everything its row of the rule table runs over (players on foot as a car does, by its speed and mass, a roadkill thrown ahead;
    /// the Gang members of other Raids, citizens, workers, animals and dummies). Its own crew is not run over.
    /// </summary>
    public void Contacts()
    {
        if (_ctx.Match.Phase != MatchPhase.Running) return;
        foreach (var raid in Roster.Raids)
        {
            var van = raid.Van;
            if (!van.Alive || van.Ended != null || van.Speed < 0.3f || !_bodies.TryGetValue(van, out var body)) continue;
            var vehicles = _vehicles();
            var dmg = vehicles != null ? RunOver.Damage(vehicles, van.Speed, _data.Van.RamMassScale) : _data.Van.RamFallbackDamage;
            if (dmg <= 0) continue;
            var vc = _data.Van;
            var crew = raid.Gang.Members;
            _runOver(new RunOverSweep(MoverKind.RaidVan, van.PrevPosition, van.Position, van.Yaw, vc.HalfWidthM, vc.HalfLengthM, van.Forward, van.Speed, dmg, RaidVanRules.HitIdOf(van.Slot, 0), body.HitCooldown),
                id => { for (var i = 0; i < crew.Count; i++) if (crew[i].HitId == id) return true; return false; }); // its own crew is not run over
        }
    }

    /// <summary>
    /// The van's box against every vehicle (called from the vehicles' contact pass): a vehicle that overlaps it is shoved out, by the van's heading when it drives (it never brakes)
    /// and just out of the way when it stands (it is solid for vehicles too). A shoved car takes crash damage by the speed it lost, as in a bump between cars.
    /// </summary>
    public void ShoveCars()
    {
        if (Roster.Raids.Count == 0 || _vehicles() is not { } vehicles) return;
        foreach (var raid in Roster.Raids)
        {
            var van = raid.Van;
            if (van.Ended != null) continue;
            var shape = van.Shape;
            var body = new BumpBody(VanMass, van.Velocity, false);
            foreach (var car in _fleet.Cars)
            {
                if (car.Destroyed || car.Thrown != null) continue;
                var p = car.GlobalPosition;
                if (MathF.Abs(p.X - van.Position.X) > Fleet.ContactBroadRange || MathF.Abs(p.Z - van.Position.Z) > Fleet.ContactBroadRange) continue;
                if (VehicleContact.Test(shape, car.ContactShape(), car.Flies) is not { } hit) continue;
                var bump = VehicleContact.Bump(hit, body, car.BumpBody(), vehicles.BumpRestitution);
                car.ApplyBump(bump.MoveB, bump.DeltaVB);
                _crash(car, bump.SpeedLossB, 0);
            }
        }
    }

    /// <summary>
    /// Before the players move: the box each van that stands is solid with (the clients build the same one from the van's state, so a predicted move and the server's are stopped by the
    /// same box), and any player the van stopped on is put out of it. Boxes of vans that are gone are freed.
    /// </summary>
    public void PlaceSolids()
    {
        if (_bodies.Count > 0)
            foreach (var van in _bodies.Keys.ToList())
                if (van.Ended != null || !Roster.Raids.Any(r => r.Van == van)) { _bodies[van].Solid.QueueFree(); _bodies.Remove(van); }
        foreach (var raid in Roster.Raids)
        {
            var van = raid.Van;
            if (van.Ended != null) continue;
            var vc = _data.Van;
            if (!_bodies.TryGetValue(van, out var body)) _bodies[van] = body = new VanBody { Solid = VehicleCollider.Make(_world, vc.HalfWidthM, vc.HalfLengthM, vc.HeightM) };
            var p = van.Position;
            var at = new Vector3(Quant.Pos(Quant.Pos(p.X)), Quant.Pos(Quant.Pos(p.Y)), Quant.Pos(Quant.Pos(p.Z)));
            VehicleCollider.Place(body.Solid, at, Quant.Yaw(Quant.Yaw(van.Yaw)), van.Solid);
            if (van.Solid && !body.WasSolid) PushOut(van);
            body.WasSolid = van.Solid;
        }
    }

    /// <summary>A van just stopped: whoever is inside its box walks out through the nearest side (they would be stuck in its solid box).</summary>
    void PushOut(RaidVan van)
    {
        var vc = _data.Van;
        foreach (var c in _ctx.Players.Values)
        {
            if (c.Car != null) continue;
            var feet = c.Body.GlobalPosition.ToNumerics();
            var to = RaidVanRules.PushOut(van.Position, van.Yaw, vc.HalfWidthM, vc.HalfLengthM, feet, 0.7f);
            if (to == feet) continue;
            c.Body.Teleport(new Vector3(to.X, to.Y, to.Z));
            _ctx.Match.SetPosition(c.Id, to);
        }
    }

    /// <summary>The living vans as hit targets (two capsules each), only while damage can apply to them. With a view tick: where the shooter saw them.</summary>
    public void AddTargets(List<HitTarget> into, uint? view = null)
    {
        if (_ctx.Match.Phase == MatchPhase.Running) Roster.AddTargets(into, view);
    }

    public void RecordPoses(uint tick) => Roster.RecordPoses(tick);
}
