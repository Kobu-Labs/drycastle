using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The Duffels on the server (GLOSSARY "Duffel"; docs/cartel.md "Duffels"; duffels.json). The pure part is <see cref="DuffelBook"/> (world entity 62); this class feeds it the players: the interact key's
/// press picks one up (<see cref="TryPickUp"/>), the throw key (<see cref="MoveButtons.DuffelThrow"/>, counted from the inputs by <see cref="DuffelThrowKey"/>) throws or drops it, a carrier's death,
/// leaving or a seat drops it where they were, a carrier is refused a vehicle seat with a notice (<see cref="RefuseSeat"/>), and the body's <see cref="Characters.Player.Burden"/> follows what it carries.
/// <para>
/// Seams for the other packages: a Job gives or places Duffels for its party with <see cref="Give"/> and <see cref="Book"/> (<see cref="DuffelBook.Place"/>, <see cref="DuffelBook.RemoveParty"/>), the
/// Cartel van registers its cargo box with <see cref="DuffelBook.AddVolume"/>. The admin's <c>/duffel kind</c> gives one to the admin (a party of one).
/// </para>
/// </summary>
internal sealed class DuffelServer
{
    readonly ServerContext _ctx;
    readonly DuffelsFile _data;
    readonly Func<Vector3, ServerCar?> _enterableCar;
    readonly Func<NVec3, bool> _crateInReach;
    readonly Dictionary<ushort, DuffelThrowKey> _keys = new();
    /// <summary>The Duffel id each human was last told is in reach (absent: 0).</summary>
    readonly Dictionary<ushort, ushort> _reachSent = new();
    /// <summary>Where each carrier last stood on foot (the drop point when they leave or are put in a seat).</summary>
    readonly Dictionary<ushort, NVec3> _lastFeet = new();

    /// <param name="enterableCar">The vehicle the interact key would get a player at the point into (the seats').</param>
    /// <param name="crateInReach">A landed care package is in reach of the point: its hold owns the key before a Duffel (<see cref="ShooterCore.Hud.InteractArbiter"/>).</param>
    public DuffelServer(ServerContext ctx, DuffelsFile data, Func<WorldQuery> query, Func<Vector3, ServerCar?> enterableCar, Func<NVec3, bool> crateInReach)
    {
        _ctx = ctx; _data = data; _enterableCar = enterableCar; _crateInReach = crateInReach;
        Book = new DuffelBook(data, ctx.Match.Entities, new DuffelWorld(query));
        Book.Loaded += (d, _) => GD.Print($"server: a {DuffelRules.NameOf(d.Kind)} duffel (id {d.Id}) is loaded");
    }

    /// <summary>Every Duffel (the Job core and the Cartel van plug in here).</summary>
    public DuffelBook Book { get; }

    static NVec3 Feet(ServerClient c) => c.Body.GlobalPosition.ToNumerics();

    /// <summary>A Duffel of the party in this player's hands. Null when they carry one already (they are told) or are not alive.</summary>
    public Duffel? Give(DuffelKind kind, IReadOnlySet<ushort> party, ServerClient carrier)
    {
        if (!_ctx.IsAlive(carrier.Id)) return null;
        if (Book.CarriedBy(carrier.Id) != null) { _ctx.SendEvent(carrier, EventKind.DuffelNotice, DuffelCodec.EncodeNotice(DuffelNotice.CarryingOne)); return null; }
        var d = Book.Give(kind, party, carrier.Id, Feet(carrier));
        if (d != null) Carry(carrier, d);
        return d;
    }

    void Carry(ServerClient c, Duffel d)
    {
        c.Body.Burden = _data.BurdenOf(d.Kind);
        _lastFeet[c.Id] = Feet(c);
        _keys[c.Id] = default;
    }

    void Unburden(ushort id)
    {
        if (_ctx.Players.TryGetValue(id, out var c)) c.Body.Burden = MoveBurden.None;
        _lastFeet.Remove(id);
        _keys.Remove(id);
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
        Carry(c, d);
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

    /// <summary>Every input of a living player on foot: the throw key. A tap puts the Duffel down at the feet, a charged release throws it from the eye along the look.</summary>
    public void OnInput(ServerClient c, NetInput i)
    {
        var carrying = Book.CarriedBy(c.Id) != null;
        if (!carrying && !_keys.ContainsKey(c.Id)) return;
        var key = _keys.GetValueOrDefault(c.Id);
        var release = key.Tick((i.Buttons & (ushort)MoveButtons.DuffelThrow) != 0, carrying && OnFoot(c), 1f / Wire.TickRate, _data, out var charge);
        _keys[c.Id] = key;
        if (release == DuffelRelease.None) return;
        var feet = Feet(c);
        var done = release == DuffelRelease.Drop
            ? Book.Drop(c.Id, feet)
            : Book.Throw(c.Id, feet + new NVec3(0, MovementStep.EyeHeight(c.Body.State.Stance), 0), Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch), charge);
        if (done) Unburden(c.Id);
    }

    /// <summary>
    /// Every tick, after the shots and blasts: carried Duffels stay with their carriers; a carrier who died drops it where they fell, one who left or was put in a seat where they last stood on foot.
    /// Then each human is told the Duffel in reach when that changed.
    /// </summary>
    public void Tick()
    {
        foreach (var d in Book.Carried())
        {
            var id = d.Carrier;
            if (!_ctx.Players.TryGetValue(id, out var c)) { Book.Drop(id, _lastFeet.GetValueOrDefault(id, d.Position)); Unburden(id); continue; }
            if (!_ctx.IsAlive(id)) { Book.Drop(id, Feet(c)); Unburden(id); GD.Print($"server: {c.Name} #{id} died and dropped a duffel"); continue; }
            if (c.Car != null) { Book.Drop(id, _lastFeet.GetValueOrDefault(id, d.Position)); Unburden(id); continue; }
            var feet = Feet(c);
            Book.Follow(id, feet);
            _lastFeet[id] = feet;
        }
        if (Book.Count == 0 && _reachSent.Count == 0) return;
        foreach (var c in _ctx.Roster)
        {
            if (c.IsBot || !c.Joined) continue;
            var now = InReach(c)?.Id ?? 0;
            if (now == _reachSent.GetValueOrDefault(c.Id)) continue;
            if (now == 0) _reachSent.Remove(c.Id); else _reachSent[c.Id] = now;
            _ctx.SendEvent(c, EventKind.DuffelReach, DuffelCodec.EncodeReach(now));
        }
        foreach (var gone in _reachSent.Keys.Where(id => !_ctx.Players.ContainsKey(id)).ToList()) _reachSent.Remove(gone);
    }

    /// <summary>A new round: the match cleared the entities; every body is unburdened and nothing is in reach.</summary>
    public void RoundStart()
    {
        foreach (var d in Book.Carried()) Unburden(d.Carrier);
        Book.Reset();
        _keys.Clear(); _lastFeet.Clear();
        foreach (var c in _ctx.Roster) c.Body.Burden = MoveBurden.None;
        foreach (var (id, _) in _reachSent) if (_ctx.Players.TryGetValue(id, out var c)) _ctx.SendEvent(c, EventKind.DuffelReach, DuffelCodec.EncodeReach(0));
        _reachSent.Clear();
    }

    /// <summary>"/duffel kind" (TEST): a Duffel of that kind in the admin's hands, the admin a party of one.</summary>
    public void ChatCommand(ServerClient c, IReadOnlyList<string> args)
    {
        if (DuffelRules.Parse(args[0]) is not { } kind) { _ctx.ChatTo(c, ChatLineKind.Error, "The kinds are weed, cash and product."); return; }
        if (Book.CarriedBy(c.Id) != null) { _ctx.ChatTo(c, ChatLineKind.Error, "You carry a duffel already: throw it or drop it first."); return; }
        if (Give(kind, new HashSet<ushort> { c.Id }, c) is not { } d) { _ctx.ChatTo(c, ChatLineKind.Error, "No duffel: you are not alive, or the world is full."); return; }
        _ctx.ChatTo(c, ChatLineKind.Info, $"A {DuffelRules.NameOf(kind)} duffel is in your hands (id {d.Id}). Hold the throw key to throw it, tap it to put it down.");
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
