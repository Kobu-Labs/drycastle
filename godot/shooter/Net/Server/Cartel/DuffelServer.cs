using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The Duffels on the server (GLOSSARY "Duffel"; docs/cartel.md "Duffels"; duffels.json). The pure part is <see cref="DuffelBook"/> (world entity 62); this class feeds it the players: the interact key's
/// press picks one up (<see cref="TryPickUp"/>), the throw key (<see cref="MoveButtons.DuffelThrow"/>, one fresh press by an <see cref="Edge"/> over the inputs) throws it at once, a carrier's death,
/// leaving the server or the party or a seat drops it where they were, a carrier is refused a vehicle seat with a notice (<see cref="RefuseSeat"/>), and the body's <see cref="Characters.Player.Burden"/>
/// follows what it carries: set in one place, from the book's <see cref="DuffelBook.CarrierChanged"/>, whoever changed the carrier.
/// <para>
/// Seams for the other packages: a Job gives or places Duffels for its party with <see cref="Give"/> and <see cref="Book"/> (<see cref="DuffelBook.Place"/>) and takes them away with
/// <see cref="DuffelBook.RemoveParty"/> (its carriers are unburdened here, from <see cref="DuffelBook.CarrierChanged"/>), the Cartel van registers its cargo box with <see cref="DuffelBook.AddVolume"/>. The admin's <c>/duffel kind</c> gives one to the admin (a party of one).
/// </para>
/// </summary>
internal sealed class DuffelServer
{
    readonly ServerContext _ctx;
    readonly DuffelsFile _data;
    readonly Func<Vector3, ServerCar?> _enterableCar;
    readonly Func<NVec3, bool> _crateInReach;
    /// <summary>Where each carrier last stood on foot (the drop point when they leave or are put in a seat).</summary>
    readonly Dictionary<ushort, NVec3> _lastFeet = new();
    /// <summary>The carried Duffels of this tick (kept: the tick allocates nothing).</summary>
    readonly List<Duffel> _carried = new();
    /// <summary>How many humans were told a Duffel is in reach at the last tick (0 with no Duffel: the tick skips the roster).</summary>
    int _told;

    /// <param name="enterableCar">The vehicle the interact key would get a player at the point into (the seats').</param>
    /// <param name="crateInReach">A landed care package is in reach of the point: its hold owns the key before a Duffel (<see cref="ShooterCore.Hud.InteractArbiter"/>).</param>
    public DuffelServer(ServerContext ctx, DuffelsFile data, Func<WorldQuery> query, Func<Vector3, ServerCar?> enterableCar, Func<NVec3, bool> crateInReach)
    {
        _ctx = ctx; _data = data; _enterableCar = enterableCar; _crateInReach = crateInReach;
        Book = new DuffelBook(data, ctx.Match.Entities, new DuffelWorld(query));
        Book.CarrierChanged += OnCarrierChanged;
    }

    /// <summary>Every Duffel (the Job core and the Cartel van plug in here).</summary>
    public DuffelBook Book { get; }

    static NVec3 Feet(ServerClient c) => c.Body.GlobalPosition.ToNumerics();

    /// <summary>A Duffel of the party in this player's hands. Null when they carry one already (they are told) or are not alive.</summary>
    public Duffel? Give(DuffelKind kind, IReadOnlySet<ushort> party, ServerClient carrier)
    {
        if (!_ctx.IsAlive(carrier.Id)) return null;
        if (Book.CarriedBy(carrier.Id) != null) { _ctx.SendEvent(carrier, EventKind.DuffelNotice, DuffelCodec.EncodeNotice(DuffelNotice.CarryingOne)); return null; }
        return Book.Give(kind, party, carrier.Id, Feet(carrier));
    }

    /// <summary>
    /// The one place a body's burden follows what it carries: a new carrier is slowed and stands where they took it, one who carries none any more walks free. The throw key's edge keeps its
    /// last up/down through it: X held through the pick-up must be let go first, as the client counts it.
    /// </summary>
    void OnCarrierChanged(ushort id, Duffel? d)
    {
        if (d == null) _lastFeet.Remove(id);
        if (!_ctx.Players.TryGetValue(id, out var c)) return;
        c.Body.Burden = d != null ? _data.BurdenOf(d.Kind) : MoveBurden.None;
        if (d != null) _lastFeet[id] = Feet(c);
    }

    bool OnFoot(ServerClient c) => c.Car == null && _ctx.IsAlive(c.Id) && _ctx.Tick > c.HeldUntilTick;

    /// <summary>The lying Duffel this player's interact key would pick up now, or null (the key competition of the plantation asks it too).</summary>
    public Duffel? InReach(ServerClient c) =>
        Book.Count == 0 || c.IsBot || !OnFoot(c) || _crateInReach(Feet(c)) ? null : Book.Pickable(c.Id, Feet(c));

    /// <summary>The interact key's press on foot: picks up the Duffel in reach (before the Loot, the doors and the seats). True when it did.</summary>
    public bool TryPickUp(ServerClient c)
    {
        if (InReach(c) == null || !Book.PickUp(c.Id, Feet(c))) return false;
        var d = Book.CarriedBy(c.Id)!;
        GD.Print($"server: {c.Name} #{c.Id} picked up a {DuffelRules.NameOf(d.Kind)} duffel (id {d.Id})");
        return true;
    }

    /// <summary>The interact key would take a vehicle seat while the player carries a Duffel: refused, with a notice. True when it refused (the key is used up).</summary>
    public bool RefuseSeat(ServerClient c)
    {
        if (Book.CarriedBy(c.Id) == null || _enterableCar(c.Body.GlobalPosition) == null) return false;
        _ctx.SendEvent(c, EventKind.DuffelNotice, DuffelCodec.EncodeNotice(DuffelNotice.NoSeat));
        return true;
    }

    /// <summary>
    /// Every input of a living player on foot, carrier or not (the key's up/down is followed for all, as the client follows it): the throw key. A fresh press while carrying throws the Duffel at once from
    /// the eye along the look. A press with empty hands does nothing yet (it is the place a later press at an open cargo area takes a bag out).
    /// </summary>
    public void OnInput(ServerClient c, NetInput i)
    {
        if (!c.DuffelKey.Pressed((i.Buttons & (ushort)MoveButtons.DuffelThrow) != 0) || !OnFoot(c)) return;
        if (Book.CarriedBy(c.Id) == null) return;
        Book.Throw(c.Id, Feet(c) + new NVec3(0, MovementStep.EyeHeight(c.Body.State.Stance), 0), Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch));
    }

    /// <summary>
    /// Every tick, after the shots and blasts: carried Duffels stay with their carriers; a carrier who died drops it where they fell, one who left or was put in a seat where they last stood on foot,
    /// one who left the party (a squad mate who quit the squad: out of the Job's party from the next tick) at their feet, so a party player can take it on. Then each human is told the Duffel in reach when
    /// that changed.
    /// </summary>
    public void Tick()
    {
        if (Book.CarriedCount > 0) FollowCarriers();
        if (Book.Count == 0 && _told == 0) return;
        var told = 0;
        foreach (var c in _ctx.Roster)
        {
            if (c.IsBot || !c.Joined) continue;
            var now = InReach(c)?.Id ?? 0;
            if (now != 0) told++;
            if (now == c.DuffelReach) continue;
            c.DuffelReach = now;
            _ctx.SendEvent(c, EventKind.DuffelReach, DuffelCodec.EncodeReach(now));
        }
        _told = told;
    }

    void FollowCarriers()
    {
        Book.Carried(_carried);
        foreach (var d in _carried)
        {
            var id = d.Carrier;
            if (!_ctx.Players.TryGetValue(id, out var c)) { Book.Drop(id, _lastFeet.GetValueOrDefault(id, d.Position)); continue; }
            if (!_ctx.IsAlive(id)) { Book.Drop(id, Feet(c)); GD.Print($"server: {c.Name} #{id} died and dropped a duffel"); continue; }
            if (c.Car != null) { Book.Drop(id, _lastFeet.GetValueOrDefault(id, d.Position)); continue; }
            var feet = Feet(c);
            if (!d.MayTouch(id)) { Book.Drop(id, feet); GD.Print($"server: {c.Name} #{id} left the duffel's party and dropped it"); continue; }
            Book.Follow(id, feet);
            _lastFeet[id] = feet;
        }
        _carried.Clear();
    }

    /// <summary>A new round: the match cleared the entities (every carrier was unburdened as its Duffel went); nothing is in reach.</summary>
    public void RoundStart()
    {
        Book.Reset();
        _lastFeet.Clear();
        foreach (var c in _ctx.Roster)
        {
            if (c.DuffelReach == 0) continue;
            c.DuffelReach = 0;
            _ctx.SendEvent(c, EventKind.DuffelReach, DuffelCodec.EncodeReach(0));
        }
        _told = 0;
    }

    /// <summary>"/duffel kind" (TEST): a Duffel of that kind in the admin's hands, the admin a party of one.</summary>
    public void ChatCommand(ServerClient c, IReadOnlyList<string> args)
    {
        if (DuffelRules.Parse(args[0]) is not { } kind) { _ctx.ChatTo(c, ChatLineKind.Error, "The kinds are weed, cash and product."); return; }
        if (Book.CarriedBy(c.Id) != null) { _ctx.ChatTo(c, ChatLineKind.Error, "You carry a duffel already: throw it first."); return; }
        if (Give(kind, new HashSet<ushort> { c.Id }, c) is not { } d) { _ctx.ChatTo(c, ChatLineKind.Error, "No duffel: you are not alive, or the world is full."); return; }
        _ctx.ChatTo(c, ChatLineKind.Info, $"A {DuffelRules.NameOf(kind)} duffel is in your hands (id {d.Id}). Press the throw key to throw it.");
    }

    /// <summary>The world a Duffel meets: the Drop row of the World query (ADR 0028: terrain, buildings, trunks, doors; not vehicles, crowns or bodies).</summary>
    sealed class DuffelWorld : IDuffelWorld
    {
        readonly Func<WorldQuery> _query;

        public DuffelWorld(Func<WorldQuery> query) => _query = query;

        public bool Blocked(NVec3 a, NVec3 b, out NVec3 at, out NVec3 normal)
        {
            if (!_query().Blocked(MoverKind.Drop, new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), out var hit)) { at = default; normal = default; return false; }
            at = hit.Position.ToNumerics(); normal = hit.Normal.ToNumerics();
            return true;
        }

        public float? GroundY(NVec3 at, float up, float down) => _query().GroundY(MoverKind.Drop, new Vector3(at.X, at.Y, at.Z), up, down);
    }
}
