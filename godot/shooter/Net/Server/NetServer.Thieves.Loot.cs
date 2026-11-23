using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The Loot of the Thieves event on the server (GLOSSARY "Loot"; docs/events/thieves/04): the kinds of Loot, the Loot that lies on the ground and the player's hold to send it back.
/// <para>
/// The pure part is <see cref="LootBook"/> (over a <see cref="LootRegistry"/> of <see cref="IStealSource"/>s): claims, the Thief's pick, lying Loot (world entity 56, capped), returns and the reset. Each
/// Raid's Thieves work through a <see cref="RaidWork"/> over it (<see cref="RaidRoster.Loot"/>). A new kind of Loot is one <see cref="IStealSource"/> class in <c>ShooterCore/Thieves/</c> and one line in
/// <see cref="InitLoot"/>; the stop choice (<see cref="RaidTargetPoints"/>), the Thief states, lying Loot, the van's count, the return and the reset need no change.
/// </para>
/// <para>
/// The tire target's cars are <see cref="ServerTires"/>: the wheel bits live in the car's <see cref="VehicleLifecycle"/> and travel in the vehicle record. A player holds the use key within
/// <see cref="LootConfig.ReachM"/> of lying Loot for <see cref="LootConfig.ReturnSeconds"/> (counted here from the inputs, as the rozhlas set's hold is; a hit taken or moving off starts it over) and it goes
/// back to its place; the player is credited for the event's XP while the event runs (<see cref="ThievesCredit.NoteLootReturned"/>). Lying Loot stays after the event, and stays returnable.
/// </para>
/// </summary>
public partial class NetServer
{
    LootBook? _loot;

    /// <summary>The Loot of the Thieves event (null until <c>_Ready</c>).</summary>
    public LootBook? Loot => _loot;

    /// <summary>The cars the tire target steals from (<see cref="TireLootConfig.VehicleIds"/>): the server's vehicles as the pure source sees them.</summary>
    sealed class ServerTires : ITireCars
    {
        readonly NetServer _s;
        public ServerTires(NetServer server) => _s = server;

        TireLootConfig Cfg => _s.ThievesData.Loot.Tire;

        TireCar? Of(ServerCar car)
        {
            if (!Cfg.VehicleIds.Contains(car.Def.Id)) return null;
            var speed = MathF.Abs(car.Drives ? car.Drive.Speed : car.State.Speed);
            var atRest = !car.Destroyed && !car.Life.Doomed && car.Thrown == null && !car.Seat.Occupied && speed <= Cfg.RestSpeedMps;
            return new TireCar(car.Id, N(car.GlobalPosition), car.State.Yaw, atRest, car.Life.WheelsOff);
        }

        public IEnumerable<TireCar> Cars()
        {
            foreach (var car in _s._cars)
                if (Of(car) is { } t) yield return t;
        }

        ServerCar? Find(int carId) => carId - VehicleRecord.FirstId is var i && i >= 0 && i < _s._cars.Count && _s._cars[i].Id == carId ? _s._cars[i] : null;

        public bool TryGet(int carId, out TireCar car)
        {
            car = default;
            if (Find(carId) is not { } c || Of(c) is not { } t) return false;
            car = t;
            return true;
        }

        public void SetWheel(int carId, int wheel, bool off)
        {
            if (Find(carId) is not { } c) return;
            if (off) c.Life.TakeWheel(wheel); else c.Life.ReturnWheel(wheel);
        }

        public void RestoreAll()
        {
            foreach (var car in _s._cars)
                for (var w = 0; w < VehicleLifecycle.Wheels; w++) car.Life.ReturnWheel(w);
        }
    }

    /// <summary>Called once from <see cref="InitThieves"/> (after the Raids exist): the entity type, the book, the kinds of Loot.</summary>
    void InitLoot()
    {
        LootRules.Register(WorldEntityRegistry.Shared);
        var registry = new LootRegistry();
        _loot = new LootBook(ThievesData.Loot, registry, _match.Entities, System.Environment.TickCount);
        // ---- the kinds of Loot: one registration line each (see IStealSource) ----
        registry.Register(new TireSource(ThievesData.Loot.Tire, new ServerTires(this)));
        registry.Register(MakePetSource());
        registry.Register(MakeDishSource()); // dishes (ticket 06)
        RegisterConnectorLoot(registry); // connectors (ticket 07): NetServer.Thieves.Loot.Connector.cs
        // ---- end of the kinds ----
        _raids!.Loot = _loot;
        _loot.Returned += o => GD.Print($"server: Thieves: a {o.Kind} (id {o.Id}) is back at its place");
    }

    /// <summary>
    /// What the Thieves know of where to steal, for the van's stop choice: every place a free target stands (<see cref="LootRegistry.Places"/>), all kinds. Each kind adds its own through its
    /// <see cref="IStealSource.Places"/>: nothing here changes for a new kind.
    /// </summary>
    List<System.Numerics.Vector2> LootPlaces()
    {
        var list = new List<System.Numerics.Vector2>();
        _loot?.Registry.Places(list);
        return list;
    }

    /// <summary>
    /// Vehicles first (<see cref="LootConfig.VehiclePriority"/>): each Raid looks for cars that newly became available to it (a spawn, a respawn, a player stopping one near its van; the cars come from
    /// <see cref="ServerTires"/>) and sends a share of its free Thieves to their tires.
    /// </summary>
    void LootVehiclesTick(float dt)
    {
        if (_loot == null || _raids == null) return;
        foreach (var raid in _raids.Raids)
            if (raid.Gang.Work is RaidWork work) work.ScanVehicles(dt);
    }

    /// <summary>A new round, or a cancelled event: lying Loot goes, claims end and every kind puts everything back (the cars' own reset puts the wheels back too).</summary>
    void ResetLoot() => _loot?.Reset();

    /// <summary>Lying Loot within reach of this player (entity id), or null.</summary>
    ushort? LootInReach(Client c) => _loot is { LyingCount: > 0 } l ? l.NearestLying(N(c.Body.GlobalPosition), ThievesData.Loot.ReachM) : null;

    /// <summary>
    /// Every input of a player, with the use key's state (next to <see cref="RozhlasSetUse"/>): held for <see cref="LootConfig.ReturnSeconds"/> within reach of lying Loot, on foot and alive, it
    /// goes back to its place (once per hold). A hit taken (the player's health fell) or moving off starts the hold over. The client runs the same hold for its ring.
    /// </summary>
    void LootUse(Client c, bool useDown)
    {
        if (_loot == null) return;
        var p = _match.Get(c.Id);
        if (p == null || p.Life != PlayerLife.Alive || c.Car != null) { c.LootHold.Reset(); c.LootHealth = p?.Health ?? 0; return; }
        if (p.Health < c.LootHealth) c.LootHold.Interrupt();
        c.LootHealth = p.Health;
        var near = LootInReach(c);
        if (!c.LootHold.Tick(useDown, near, 1.0 / Wire.TickRate, ThievesData.Loot.ReturnSeconds) || near is not { } id) return;
        if (!_loot.ReturnLying(id)) return;
        if (_thieves is { Running: true }) Credit.NoteLootReturned(c.Id);
        GD.Print($"server: {c.Name} #{c.Id} sent Loot back");
    }
}
