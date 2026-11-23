using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Intel;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Vehicles;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Scuds;

/// <summary>One bunker of the map and the cart that stands for it.</summary>
internal sealed class ScudBunkerSlot
{
    public required string BunkerId;
    public required NVec2 Centre;
    public required float Radius, FloorY;
    public float? Door;
    public readonly ScudCartSlot Slot = new();
    public ServerCar? Cart;
}

/// <summary>
/// The Scud event on the server (docs/events/scud.md, GLOSSARY "Scud event"): <c>/event scud</c> hides a Scud truck at a baked point of a <c>scud</c> zone (or, on a map with none, on open ground away from
/// the players, <see cref="ScudPlaces.PickSpot"/>), puts a loaded rocket cart beside each bunker, and announces a top secret weapon on the intel board with a SEARCH AREA (a circle the truck is somewhere inside, whose
/// centre is the truck plus a random vector, <see cref="ScudSearch"/>). The first human to board the truck wins it (<see cref="ScudEventRun"/>): XP, the entry and the circle withdrawn, the event slot free; the
/// truck stays an ordinary vehicle until it is destroyed and the carts at the bunkers (replaced after a cooldown, <see cref="ScudCartSlot"/>) with it. Nobody boards it in time, or it is destroyed unfound: the
/// truck and the carts go and nothing is paid. A new round ends all of it (the truck never returns, ADR 0033), as the harvester's job ends.
/// </summary>
internal sealed class ScudEventServer
{
    readonly ServerContext _ctx;
    readonly ScudFile _scud;
    readonly Func<VehiclesFile?> _vehicles;
    readonly EventBook _events;
    readonly IntelServer _intel;
    readonly Random _rng;
    readonly Fleet _fleet;
    readonly ScudPlaces _places;
    readonly ScudServer _trucks;
    readonly Func<NVec3, float, bool, ServerCar> _spawnCart;
    readonly Func<int> _removeCarts;
    readonly Action<ServerClient, bool> _leaveSeat;
    readonly Func<ushort, bool> _scores;
    readonly Action<IEnumerable<XpGrant>> _awardXp;

    readonly ScudEventRun _run = new();
    ServerCar? _truck;
    readonly List<ScudBunkerSlot> _slots = new();

    /// <param name="spawnCart">Stands a rocket cart at a place, loaded or not (<c>NetServer.SpawnRocketCart</c>).</param>
    /// <param name="removeCarts">Removes every rocket cart from the world for good; how many there were (<c>NetServer.RemoveRocketCarts</c>).</param>
    /// <param name="leaveSeat">A player stops being seated; the flag puts the body on a free side of the vehicle (<c>NetServer.LeaveSeat</c>).</param>
    /// <param name="scores">Whether the player earns XP (a bot and a sandbox player do not); <paramref name="awardXp"/> pays it.</param>
    public ScudEventServer(ServerContext ctx, ScudFile scud, Func<VehiclesFile?> vehicles, EventBook events, IntelServer intel, Random rng, Fleet fleet, ScudPlaces places, ScudServer trucks,
        Func<NVec3, float, bool, ServerCar> spawnCart, Func<int> removeCarts, Action<ServerClient, bool> leaveSeat, Func<ushort, bool> scores, Action<IEnumerable<XpGrant>> awardXp)
    {
        _ctx = ctx; _scud = scud; _vehicles = vehicles; _events = events; _intel = intel; _rng = rng; _fleet = fleet; _places = places; _trucks = trucks;
        _spawnCart = spawnCart; _removeCarts = removeCarts; _leaveSeat = leaveSeat; _scores = scores; _awardXp = awardXp;
    }

    // ---- the bunkers' carts ----

    /// <summary>Puts a loaded cart at the bunker: the first of <see cref="ScudCartSpots"/> with ground, free of solids and of other vehicles. False when none fits (the slot tries again later).</summary>
    bool TryPlaceCart(ScudBunkerSlot b)
    {
        var def = _vehicles()?.Vehicles.FirstOrDefault(v => v.Trailer is { HasCargo: true });
        if (def == null) return false;
        if (b.Cart is { Destroyed: false, Retired: false, Tractor: null } old && old.Cargo is { Loaded: false }) old.Retire(); // the emptied cart this one replaces: gone, unless somebody is towing it
        foreach (var spot in ScudCartSpots.Candidates(b.Centre, b.Radius, b.Door, (float)_scud.Event.CartStandoffM, def.HalfLength))
        {
            if (_places.StandingHeight(spot.Origin.X, spot.Origin.Y, spot.Yaw, def.HalfWidth, def.HalfLength) is not { } y) continue;
            b.Cart = _spawnCart(new NVec3(spot.Origin.X, y, spot.Origin.Y), spot.Yaw, true);
            GD.Print($"server: Scud event: a loaded rocket cart {b.Cart.Id} stands at bunker {b.BunkerId} ({spot.Origin.X:F1},{spot.Origin.Y:F1})");
            return true;
        }
        GD.PrintErr($"server: Scud event: no free place for a rocket cart at bunker {b.BunkerId}; it tries again in {ScudCartSlot.RetrySeconds:F0} s");
        return false;
    }

    void TickSlots(float dt)
    {
        var cooldown = (float)_scud.Event.CartCooldownSeconds;
        foreach (var b in _slots)
        {
            var loaded = b.Cart is { Destroyed: false, Retired: false } c && c.Cargo is { Loaded: true } && !c.Life.Doomed;
            if (b.Slot.Tick(dt, loaded, cooldown) == CartSlotAction.Place) b.Slot.Placed(TryPlaceCart(b));
        }
    }

    // ---- the lifecycle ----

    /// <summary>"/event scud": the truck is hidden, the carts stand at the bunkers and the intel board names the search area. The answer to the admin says where the truck really is (the only place that does).</summary>
    public void Start(ServerClient admin)
    {
        if (_events.WhyNotStart(EventBook.Scud) is { } busy) { _ctx.ChatTo(admin, ChatLineKind.Error, busy); return; }
        if (_run.TruckOut) { _ctx.ChatTo(admin, ChatLineKind.Error, "A Scud truck from the last event is still out. It has to be destroyed first."); return; }
        if (_ctx.Match.Phase != MatchPhase.Running) { _ctx.ChatTo(admin, ChatLineKind.Error, "No round is running."); return; }
        var vehicles = _vehicles();
        var def = vehicles?.Vehicles.FirstOrDefault(v => v.Launcher != null);
        if (def == null || vehicles!.Vehicles.All(v => v.Trailer is not { HasCargo: true })) { _ctx.ChatTo(admin, ChatLineKind.Error, "vehicles.json has no Scud truck or rocket cart."); return; }
        if (_places.PickSpot(def) is not { } spot) { _ctx.ChatTo(admin, ChatLineKind.Error, "No free ground far enough from the players was found for the Scud truck."); return; }
        var truck = _trucks.SpawnTruck(spot.Pos, spot.Yaw);
        if (truck == null) { _ctx.ChatTo(admin, ChatLineKind.Error, "The Scud truck could not be spawned."); return; }

        var sheet = _places.Sheet();
        var extent = sheet?.Side ?? 3000f;
        var radius = ScudSearch.RadiusM(_scud.Event.SearchRadiusCells, extent);
        var area = ScudSearch.Make(new NVec2(spot.Pos.X, spot.Pos.Z), radius, _rng);
        _truck = truck;
        _run.Begin(area, (float)(_scud.Event.DespawnMinutes * 60.0));
        _events.Start(EventBook.Scud, _ctx.Now);
        _intel.Announce(ScudEventRun.Intel(_ctx.Now, area));
        _ctx.ChatToAll(ChatLineKind.System, "", $"{admin.Name} started the Scud event. A top secret weapon has been located: read the intel board at the base.");

        _slots.Clear();
        _slots.AddRange(_places.Bunkers());
        foreach (var b in _slots) b.Slot.Open(TryPlaceCart(b));

        var grid = sheet is { } s ? TacticalMapMath.ReferenceOf(area.Centre, s.Origin.X, s.Origin.Y + s.Side, s.Side) : $"{area.Centre.X:0},{area.Centre.Y:0}";
        var cells = sheet != null ? ScudSearch.Cells(radius, extent) : _scud.Event.SearchRadiusCells;
        _ctx.ChatTo(admin, ChatLineKind.Info, $"Scud event: search area centred on {grid}, radius {cells} cells ({radius:0} m). The truck is really at x {spot.Pos.X:0}, z {spot.Pos.Z:0} (" + (spot.FromZone ? "a baked scud zone point" : "FALLBACK: open ground, the map has no scud zone") + $"); {_slots.Count} bunker cart(s). Nobody else is told.");
        GD.Print($"server: Scud event started by {admin.Name} #{admin.Id}: truck {truck.Id} at ({spot.Pos.X:F1},{spot.Pos.Y:F1},{spot.Pos.Z:F1}), {(spot.FromZone ? "scud zone point" : "FALLBACK spawn point")}, area centre ({area.Centre.X:F0},{area.Centre.Y:F0}) radius {radius:F0} m, {_slots.Count} bunker carts, despawns in {_scud.Event.DespawnMinutes:F0} min");
    }

    /// <summary>"/event current end": the truck counts as found by the caller, who is paid.</summary>
    public void End(ServerClient c)
    {
        if (!_run.Searching) { _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} ended the Scud event as a win.");
        if (_run.Win(c.Id)) OnFound(c.Id, c.Name);
    }

    /// <summary>"/event current cancel": the truck and the carts are removed, nobody is paid.</summary>
    public void Cancel(ServerClient c)
    {
        if (!_run.Searching) { _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} cancelled the Scud event.");
        _run.Despawn();
        Finish($"cancelled by {c.Name}", retireTruck: true);
    }

    /// <summary>Once a tick: who boards the truck, the time, the truck's end, and the bunkers' carts.</summary>
    public void Tick(float dt)
    {
        if (!_run.TruckOut) return;
        var truck = _truck;
        var alive = truck is { Destroyed: false, Retired: false };
        var boarder = alive ? FirstHumanAboard(truck!) : (ushort)0;
        switch (_run.Tick(dt, boarder, alive))
        {
            case ScudEventOutcome.Found: OnFound(boarder, _ctx.Players.TryGetValue(boarder, out var f) ? f.Name : "Somebody"); break;
            case ScudEventOutcome.TimedOut:
                _ctx.ChatToAll(ChatLineKind.System, "", "The top secret weapon was moved out of the area. Nobody found it.");
                Finish("nobody boarded it in time", retireTruck: true);
                break;
            case ScudEventOutcome.TruckLost:
                if (_events.Current?.Kind == EventBook.Scud) _ctx.ChatToAll(ChatLineKind.System, "", "The top secret weapon was destroyed before anyone found it.");
                else _ctx.ChatToAll(ChatLineKind.System, "", "The Scud truck is destroyed. The rocket carts at the bunkers are gone.");
                Finish("the truck was destroyed", retireTruck: false);
                break;
        }
        if (_run.TruckOut) TickSlots(dt);
    }

    ushort FirstHumanAboard(ServerCar truck)
    {
        for (var s = 0; s < truck.Seat.Count; s++)
            if (_fleet.OccupantAt(truck, s) is { IsBot: false } o) return o.Id;
        return 0;
    }

    /// <summary>The first player aboard (or the admin's end): the reward, the entry and the circle gone, the event slot free. The truck and the carts stay.</summary>
    void OnFound(ushort finder, string name)
    {
        _events.End(EventBook.Scud, EventPhase.Won, _ctx.Now);
        _intel.Withdraw(ScudEventRun.IntelId);
        var xp = (int)_scud.Event.XpReward;
        if (finder != 0 && _ctx.Players.ContainsKey(finder) && _scores(finder) && xp > 0) _awardXp(new List<XpGrant> { new(finder, xp, XpReason.EventWin) });
        _ctx.ChatToAll(ChatLineKind.System, "", $"Event won: {name} found the Scud truck! It stays out until it is destroyed, and so do the rocket carts at the bunkers.");
        if (_ctx.Players.TryGetValue(finder, out var c)) _ctx.SendEvent(c, EventKind.ScudNotice, ScudCodec.Encode(ScudNoticeCode.EventFound));
        GD.Print($"server: Scud event won by #{finder} {name}: +{xp} XP");
    }

    /// <summary>The truck is out of the event (despawned, cancelled or destroyed): the intel entry and the carts go, the slot is free, the truck is retired when it is still there.</summary>
    void Finish(string why, bool retireTruck)
    {
        _events.End(EventBook.Scud, EventPhase.Despawned, _ctx.Now);
        _intel.Withdraw(ScudEventRun.IntelId);
        foreach (var b in _slots) b.Slot.Close();
        _removeCarts();
        if (retireTruck && _truck is { Destroyed: false } t)
        {
            foreach (var o in _fleet.Occupants(t)) _leaveSeat(o, true);
            t.Seat.Clear();
            t.Retire();
        }
        _truck = null;
        GD.Print($"server: Scud event over: {why}");
    }

    /// <summary>A new round: the truck and the carts are retired with the world (<see cref="ScudServer.RoundReset"/>, which runs after this), the event is over with no reward and the board is cleared of it.</summary>
    public void RoundReset()
    {
        if (!_run.TruckOut) return;
        var searching = _run.Searching;
        _run.Despawn();
        if (searching) { _events.End(EventBook.Scud, EventPhase.Despawned, _ctx.Now); _intel.Withdraw(ScudEventRun.IntelId); }
        foreach (var b in _slots) b.Slot.Close();
        _slots.Clear();
        _truck = null;
        GD.Print("server: Scud event ended by the new round");
    }
}
