using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.RozhlasFeed;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.VillageRadio;

/// <summary>What the Holy Mass gives the rozhlas set: the holder sits at the dictation table the way a churchgoer sits in a pew (held body, no weapon), so the two share the sitters.</summary>
/// <param name="IsSeated">Whether the player sits anywhere (a pew or the table).</param>
/// <param name="StandUpFromTable">Stands up everyone who sits at the dictation table except the given holder.</param>
/// <param name="SeatAtTable">Sits the player at the dictation table, facing the given yaw, and tells them.</param>
internal sealed record TableSeating(Func<ushort, bool> IsSeated, Action<ushort> StandUpFromTable, Action<ServerClient, float> SeatAtTable);

/// <summary>
/// The rozhlas set on the server: <see cref="RozhlasSet"/> decides who is on the mic, this class feeds it the facts (the loudspeaker's phase, deaths, disconnects, the holder's
/// position and seat), tells the clients (<see cref="EventKind.RozhlasSet"/>, also to a joiner) and relays the holder's upstream voice (<see cref="Wire.TRozhlasMic"/>) into
/// the live feed. Everything after the write (voice chain, 2 s delay, broadcast) is the live feed's.
/// <para>
/// A player takes the set by sitting down in the dead announcer's chair: holding the interact key at the dictation table for <see cref="RozhlasSet.TakeHoldSeconds"/> seats them
/// (the church's sitting: the body is held, interact or jump stands up) and only a seated player holds the set; standing up, by any cause, lets go of it (<see cref="Use"/>; the server counts the hold, the
/// client shows the prompt and the ring from the table position this class sends). The admin's <c>/rozhlas take</c> and <c>/rozhlas release</c> skip the hold; <c>/rozhlas goto</c> stands the admin at the table.
/// </para>
/// </summary>
internal sealed class RozhlasSetServer
{
    readonly ServerContext _ctx;
    readonly RozhlasServer _owner;
    readonly UradServer _urad;
    readonly RozhlasFeedServer _feed;
    readonly TableSeating _seating;
    readonly HolyMassFile _holyMass;
    readonly RozhlasMicReceiver _micIn = new();

    RozhlasSet? _set;

    public RozhlasSetServer(ServerContext ctx, RozhlasServer owner, UradServer urad, RozhlasFeedServer feed, TableSeating seating, HolyMassFile holyMass)
    {
        _ctx = ctx; _owner = owner; _urad = urad; _feed = feed; _seating = seating; _holyMass = holyMass;
    }

    /// <summary>Called once the loudspeaker exists.</summary>
    public void Start()
    {
        if (_set != null) return;
        var set = _set = new RozhlasSet();
        set.HolderChanged += holder =>
        {
            _micIn.Reset(); // a new holder (or none) starts the upstream clean; what was already written to the feed drains on (no flush)
            Seat(holder); // the holder sits at the table, whoever sat there before stands
            _ctx.Broadcast(EventKind.RozhlasSet, RozhlasSetCodec.Encode(holder, set.Table));
            GD.Print(holder == RozhlasSet.None ? "server: rozhlas: the set is free" : $"server: rozhlas: {_ctx.NameOf(holder)} #{holder} holds the set");
        };
        _owner.PhaseChanged += (_, to) =>
        {
            if (to != RozhlasPhase.Hijacked) { set.Clear(); return; } // the hijack ended: the mic is not theirs any more
            if (_urad.Table is not { } t) return;
            set.Table = t.ToNumerics(); // the dictation table (urad.json; read here, when the map is surely up): the 2 m rule is on
            _ctx.Broadcast(EventKind.RozhlasSet, RozhlasSetCodec.Encode(RozhlasSet.None, set.Table)); // the set is free and here: the clients show the hold prompt at it
        };
    }

    /// <summary>The set changed hands: the new holder is sat in the announcer's chair (as a church sitter: held body, no weapon), the one before stands up.</summary>
    void Seat(ushort holder)
    {
        _seating.StandUpFromTable(holder);
        if (holder == RozhlasSet.None || _seating.IsSeated(holder) || !_ctx.Players.TryGetValue(holder, out var c) || _urad.Urad is not { } urad) return;
        var seat = urad.AnnouncerSeat;
        var feet = seat.Pos - new NVec3(0, _holyMass.Citizens.SeatHeightM, 0);
        c.Body.Teleport(new Vector3(feet.X, feet.Y + 0.02f, feet.Z));
        c.Body.Yaw = seat.Yaw;
        c.Inputs.Clear();
        _ctx.Match.SetPosition(c.Id, feet);
        _seating.SeatAtTable(c, seat.Yaw);
    }

    /// <summary>A player stood up from the dictation table (interact, jump, or anything that stands a sitter up): the set is free.</summary>
    public void Stood(ushort id) => _set?.Release(id);

    /// <summary>The holder died (any death) or left the server: they let go. The feed is not flushed, so their last words still go out.</summary>
    public void Release(ushort id) => _set?.Release(id);

    /// <summary>Whether a press of the interact key at <paramref name="at"/> would take the set now (a hijack, the set free, within reach of the table, on foot).</summary>
    public bool CanTakeAt(NVec3 at, bool inVehicle) =>
        _set is { } set && RozhlasSet.CanTakeHere(_owner.Loudspeaker is { Hijacked: true }, set.IsHeld, set.Table, at, inVehicle);

    /// <summary>A player's request for the set (the dictation table's hold-E, and the admin's <c>/rozhlas take</c>): the rules of <see cref="RozhlasSet.Take"/> with the player's real position and seat.</summary>
    RozhlasTake Take(ushort player)
    {
        if (_owner.Loudspeaker is not { } r || _set is not { } set || !_ctx.Players.TryGetValue(player, out var c)) return RozhlasTake.NotHijacked;
        var at = c.Body.GlobalPosition.ToNumerics();
        var res = set.Take(player, r.Hijacked, at, c.Car != null);
        GD.Print($"server: rozhlas: {c.Name} #{player} asks for the set: {res}" + (set.Table is { } t ? $" ({NVec3.Distance(t, at):0.0} m from the table)" : ""));
        return res;
    }

    /// <summary>
    /// Every input of a player, with the interact key's state: held for <see cref="RozhlasSet.TakeHoldSeconds"/> at the dictation table, during a hijack, with the set free, takes it
    /// (once per hold). Silent otherwise (E means many things elsewhere). The client runs the same hold for its prompt and ring.
    /// </summary>
    public void Use(ServerClient c, bool useDown)
    {
        var here = CanTakeAt(c.Body.GlobalPosition.ToNumerics(), c.Car != null);
        if (c.SetHold.Tick(useDown, here, 1.0 / Wire.TickRate, RozhlasSet.TakeHoldSeconds)) Take(c.Id);
    }

    /// <summary>Every tick: the rules that end a hold by themselves (hijack over, a vehicle seat, walked away from the table).</summary>
    public void Tick()
    {
        if (_set is not { IsHeld: true } set || _owner.Loudspeaker is not { } r) return;
        if (!_ctx.Players.TryGetValue(set.Holder, out var h)) { set.Clear(); return; }
        set.Check(r.Hijacked, h.Body.GlobalPosition.ToNumerics(), h.Car != null);
    }

    /// <summary>Join during a hijack: who holds the set (nobody, maybe) and where the table is.</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (_set is { } set && (set.IsHeld || _owner.Loudspeaker is { Hijacked: true })) _ctx.SendEvent(c, EventKind.RozhlasSet, RozhlasSetCodec.Encode(set.Holder, set.Table));
    }

    /// <summary>
    /// One upstream mic frame. Kept only from the current holder while the mic is hijacked; everything else is dropped without a word (a stale frame from a player who just let go,
    /// a client that is not the holder, a malformed packet). Decoded and written into the live feed.
    /// </summary>
    public void OnMic(ServerClient c, byte[] d)
    {
        if (_set is not { } set || !set.IsHolder(c.Id) || _owner.Loudspeaker is not { Hijacked: true }) return;
        if (!RozhlasMicWire.TryDecode(d, out var seq, out var opus)) return;
        var feed = _feed.MadeFeed;
        if (feed == null) return;
        _micIn.Receive(seq, opus, pcm => feed.Write(pcm));
    }

    /// <summary>"/rozhlas take" and "/rozhlas release": the admin's way around holding E at the dictation table; "/rozhlas goto" stands them at it. Returns false for any other argument.</summary>
    public bool Chat(ServerClient c, string action)
    {
        if (action == "goto")
        {
            if (_urad.Table is not { } t) { _ctx.ChatTo(c, ChatLineKind.Error, "The dictation table has no position (urad.json could not be read)."); return true; }
            c.Body.Teleport(t + new Vector3(0.9f, 0.1f, 0f)); // within the 2 m reach
            _ctx.ChatTo(c, ChatLineKind.Info, "You stand at the dictation table.");
            return true;
        }
        if (action is not ("take" or "release")) return false;
        if (_owner.Loudspeaker == null || _set is not { } set) { _ctx.ChatTo(c, ChatLineKind.Error, "The rozhlas has no data (rozhlas.json could not be read)."); return true; }
        if (action == "release")
        {
            if (!set.IsHeld) { _ctx.ChatTo(c, ChatLineKind.Error, "Nobody holds the set."); return true; }
            var was = set.Holder;
            set.Clear();
            _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} took the set from {_ctx.NameOf(was)}.");
            return true;
        }
        switch (Take(c.Id))
        {
            case RozhlasTake.Taken: _ctx.ChatTo(c, ChatLineKind.Info, "You hold the set. Hold the talk key to speak."); break;
            case RozhlasTake.AlreadyYours: _ctx.ChatTo(c, ChatLineKind.Info, "You already hold the set."); break;
            case RozhlasTake.NotHijacked: _ctx.ChatTo(c, ChatLineKind.Error, "The set can be taken only while the mic is hijacked. /event rozhlas, then /rozhlas hijack."); break;
            case RozhlasTake.Occupied: _ctx.ChatTo(c, ChatLineKind.Error, $"{_ctx.NameOf(set.Holder)} holds the set. /rozhlas release lets it go."); break;
            case RozhlasTake.TooFar: _ctx.ChatTo(c, ChatLineKind.Error, "Too far from the dictation table."); break;
            case RozhlasTake.InVehicle: _ctx.ChatTo(c, ChatLineKind.Error, "Get out of the vehicle first."); break;
        }
        return true;
    }
}
