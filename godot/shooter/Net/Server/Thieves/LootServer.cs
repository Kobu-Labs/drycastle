using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Wildlife;
using NVec2 = System.Numerics.Vector2;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>
/// The Loot of the Thieves event on the server (GLOSSARY "Loot"; docs/events/thieves/04): the kinds of Loot, the Loot that lies on the ground and the player's hold to send it back.
/// <para>
/// The pure part is <see cref="LootBook"/> (over a <see cref="LootRegistry"/> of <see cref="IStealSource"/>s): claims, the Thief's pick, lying Loot (world entity 56, capped), returns and the reset. Each
/// Raid's Thieves work through a <see cref="RaidWork"/> over it (<see cref="RaidRoster.Loot"/>). A new kind of Loot is one <see cref="IStealSource"/> class in <c>ShooterCore/Thieves/</c>, a world class
/// next to <see cref="TireCars"/> if it needs the server, and one registration line in the constructor; the stop choice (<see cref="Places"/>), the Thief states, lying Loot, the van's count, the return and the reset need no change.
/// </para>
/// <para>
/// A player holds the use key within <see cref="LootConfig.ReachM"/> of lying Loot for <see cref="LootConfig.ReturnSeconds"/> (counted here from the inputs, as the rozhlas set's hold is; a hit taken or moving off
/// starts it over) and it goes back to its place. Lying Loot stays after the event, and stays returnable.
/// </para>
/// </summary>
internal sealed class LootServer
{
    readonly ServerContext _ctx;
    readonly LootConfig _cfg;
    readonly LootBook _book;
    readonly RaidRoster _raids;
    readonly DishLoot _dishes;

    public LootServer(ServerContext ctx, ThievesFile data, RaidRoster raids, GangRoster gangs, Fleet fleet, PetKeeper pets, Drycastle.MapLoader.MapLoader? map, Func<BotNav?> nav, bool arenaMode, string mapDir)
    {
        _ctx = ctx; _cfg = data.Loot; _raids = raids;
        LootRules.Register(WorldEntityRegistry.Shared);
        var registry = new LootRegistry();
        _book = new LootBook(_cfg, registry, ctx.Match.Entities, System.Environment.TickCount);
        // ---- the kinds of Loot: one registration line each (see IStealSource) ----
        registry.Register(new TireSource(_cfg.Tire, new TireCars(fleet, _cfg.Tire)));
        registry.Register(new PetLoot(_cfg.Pet, pets, gangs, map, nav, arenaMode, mapDir).Source);
        _dishes = new DishLoot(ctx, _cfg.Dish, map, nav);
        registry.Register(_dishes.Source); // dishes (ticket 06)
        registry.Register(new ConnectorSource(_cfg.Connector, new PoleSites(map, nav, _cfg.Connector), () => (float)ctx.Now)); // connectors (ticket 07)
        _book.Shocked += OnShocked;
        // ---- end of the kinds ----
        raids.Loot = _book;
        _book.Returned += o => GD.Print($"server: Thieves: a {o.Kind} (id {o.Id}) is back at its place");
    }

    /// <summary>
    /// What the Thieves know of where to steal, for the van's stop choice: every place a free target stands (<see cref="LootRegistry.Places"/>), all kinds. Each kind adds its own through its
    /// <see cref="IStealSource.Places"/>: nothing here changes for a new kind.
    /// </summary>
    public List<NVec2> Places()
    {
        var list = new List<NVec2>();
        _book.Registry.Places(list);
        return list;
    }

    /// <summary>
    /// Vehicles first (<see cref="LootConfig.VehiclePriority"/>): each Raid looks for cars that newly became available to it (a spawn, a respawn, a player stopping one near its van; the cars come from
    /// <see cref="TireCars"/>) and sends a share of its free Thieves to their tires.
    /// </summary>
    public void ScanVehicles(float dt)
    {
        foreach (var raid in _raids.Raids)
            if (raid.Gang.Work is RaidWork work) work.ScanVehicles(dt);
    }

    /// <summary>A new round, or a cancelled event: lying Loot goes, claims end and every kind puts everything back (the cars' own reset puts the wheels back too).</summary>
    public void Reset() => _book.Reset();

    public void SendOnJoin(ServerClient c) => _dishes.SendOnJoin(c);

    /// <summary>A Thief was electrocuted at the top of a pole: everyone hears and sees it (the beams run for the shock's seconds), the id being the member's world entity.</summary>
    void OnShocked(GangMember m, StealTarget pole)
    {
        foreach (var (id, entity) in _ctx.Match.Entities.OfType(GangRules.TypeId))
            if (entity == m) { _ctx.Broadcast(EventKind.ThievesFx, ThievesFxCodec.Encode(new ThievesFxMsg(ThievesFxKind.Shock, m.Position, id))); return; }
    }

    /// <summary>Lying Loot within reach of this player (entity id), or null.</summary>
    public ushort? InReach(ServerClient c) => _book is { LyingCount: > 0 } l ? l.NearestLying(c.Body.GlobalPosition.ToNumerics(), _cfg.ReachM) : null;

    /// <summary>
    /// Every input of a player, with the use key's state: held for <see cref="LootConfig.ReturnSeconds"/> within reach of lying Loot, on foot and alive, it
    /// goes back to its place (once per hold; true then). A hit taken (the player's health fell) or moving off starts the hold over. The client runs the same hold for its ring.
    /// </summary>
    public bool Use(ServerClient c, bool useDown)
    {
        var p = _ctx.Match.Get(c.Id);
        if (p == null || p.Life != PlayerLife.Alive || c.Car != null) { c.LootHold.Reset(); c.LootHealth = p?.Health ?? 0; return false; }
        if (p.Health < c.LootHealth) c.LootHold.Interrupt();
        c.LootHealth = p.Health;
        var near = InReach(c);
        if (!c.LootHold.Tick(useDown, near, 1.0 / Wire.TickRate, _cfg.ReturnSeconds) || near is not { } id) return false;
        return _book.ReturnLying(id);
    }
}
