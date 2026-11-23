using Godot;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.RozhlasFeed;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The rozhlas set on the server (slice 5): <see cref="RozhlasSet"/> decides who is on the mic, this file feeds it the facts (the loudspeaker's phase, deaths, disconnects, the holder's
/// position and seat), tells the clients (<see cref="EventKind.RozhlasSet"/>, also to a joiner) and relays the holder's upstream voice (<see cref="Wire.TRozhlasMic"/>) into
/// <see cref="RozhlasLiveFeed"/>. Everything after the write (voice chain, 2 s delay, broadcast) is the live feed's.
/// <para>
/// A player takes the set by sitting down in the dead announcer's chair: holding the interact key at the dictation table for <see cref="RozhlasSet.TakeHoldSeconds"/> seats them
/// (the church's sitting: the body is held, interact or jump stands up) and only a seated player holds the set; standing up, by any cause, lets go of it (<see cref="RozhlasSetUse"/>; the server counts the hold, the
/// client shows the prompt and the ring from the table position this file sends). The admin's <c>/rozhlas take</c> and <c>/rozhlas release</c> skip the hold; <c>/rozhlas goto</c> stands the admin at the table.
/// </para>
/// </summary>
public partial class NetServer
{
    RozhlasSet? _rozhlasSet;
    readonly RozhlasMicReceiver _micIn = new();

    /// <summary>The set: who is on the mic (null until rozhlas.json is read). <see cref="TakeRozhlasSet"/> is what a finished hold of E asks.</summary>
    public RozhlasSet? RozhlasSet => EnsureRozhlas() == null ? null : _rozhlasSet;

    /// <summary>Called by <see cref="EnsureRozhlas"/> once the loudspeaker exists.</summary>
    void EnsureRozhlasSet()
    {
        if (_rozhlasSet != null) return;
        _rozhlasSet = new RozhlasSet();
        _rozhlasSet.HolderChanged += holder =>
        {
            _micIn.Reset(); // a new holder (or none) starts the upstream clean; what was already written to the feed drains on (no flush)
            RozhlasSeat(holder); // the holder sits at the table, whoever sat there before stands
            BroadcastEvent(EventKind.RozhlasSet, RozhlasSetCodec.Encode(holder, _rozhlasSet.Table));
            GD.Print(holder == RozhlasSet.None ? "server: rozhlas: the set is free" : $"server: rozhlas: {NameOf(holder)} #{holder} holds the set");
        };
        RozhlasPhaseChanged += (_, to) =>
        {
            if (to != RozhlasPhase.Hijacked) { _rozhlasSet?.Clear(); return; } // the hijack ended: the mic is not theirs any more
            if (_rozhlasSet == null || UradTable is not { } t) return;
            _rozhlasSet.Table = N(t); // the dictation table (urad.json; read here, when the map is surely up): the 2 m rule is on
            BroadcastEvent(EventKind.RozhlasSet, RozhlasSetCodec.Encode(RozhlasSet.None, _rozhlasSet.Table)); // the set is free and here: the clients show the hold prompt at it
        };
    }

    /// <summary>The set changed hands: the new holder is sat in the announcer's chair (as a church sitter: held body, no weapon), the one before stands up.</summary>
    void RozhlasSeat(ushort holder)
    {
        foreach (var (id, s) in _sitters.ToList())
            if (s.Seat == null && id != holder) MassStandUp(id);
        if (holder == RozhlasSet.None || _sitters.ContainsKey(holder) || !_byId.TryGetValue(holder, out var c) || EnsureUrad() is not { } urad) return;
        var seat = urad.AnnouncerSeat;
        var feet = seat.Pos - new System.Numerics.Vector3(0, HolyMass.Citizens.SeatHeightM, 0);
        c.Body.Teleport(new Vector3(feet.X, feet.Y + 0.02f, feet.Z));
        c.Body.Yaw = seat.Yaw;
        c.Inputs.Clear();
        _match.SetPosition(c.Id, feet);
        _sitters[c.Id] = new Sitter();
        if (c.Peer != null) SendSeat(c, MassSeatState.Seated, seat.Yaw);
    }

    /// <summary>A player stood up from the dictation table (interact, jump, or anything that stands a sitter up): the set is free.</summary>
    void RozhlasSetStood(ushort id) => _rozhlasSet?.Release(id);

    string NameOf(ushort id) => _byId.TryGetValue(id, out var c) ? c.Name : "?";

    /// <summary>A player's request for the set (the dictation table's hold-E, and the admin's <c>/rozhlas take</c>): the rules of <see cref="RozhlasSet.Take"/> with the player's real position and seat.</summary>
    public RozhlasTake TakeRozhlasSet(ushort player)
    {
        if (EnsureRozhlas() is not { } r || _rozhlasSet is not { } set || !_byId.TryGetValue(player, out var c)) return RozhlasTake.NotHijacked;
        var res = set.Take(player, r.Hijacked, N(c.Body.GlobalPosition), c.Car != null);
        GD.Print($"server: rozhlas: {c.Name} #{player} asks for the set: {res}" + (set.Table is { } t ? $" ({System.Numerics.Vector3.Distance(t, N(c.Body.GlobalPosition)):0.0} m from the table)" : ""));
        return res;
    }

    /// <summary>
    /// Every input of a player, with the interact key's state: held for <see cref="RozhlasSet.TakeHoldSeconds"/> at the dictation table, during a hijack, with the set free, takes it
    /// (once per hold). Silent otherwise (E means many things elsewhere). The client runs the same hold for its prompt and ring.
    /// </summary>
    void RozhlasSetUse(Client c, bool useDown)
    {
        var here = _rozhlasSet is { } set && RozhlasSet.CanTakeHere(_rozhlasBroadcast.Value is { Hijacked: true }, set.IsHeld, set.Table, N(c.Body.GlobalPosition), c.Car != null);
        if (c.SetHold.Tick(useDown, here, 1.0 / Wire.TickRate, RozhlasSet.TakeHoldSeconds)) TakeRozhlasSet(c.Id);
    }

    /// <summary>Every tick: the rules that end a hold by themselves (hijack over, a vehicle seat, walked away from the table).</summary>
    void RozhlasSetTick()
    {
        if (_rozhlasSet is not { IsHeld: true } set || _rozhlasBroadcast.Value is not { } r) return;
        if (!_byId.TryGetValue(set.Holder, out var h)) { set.Clear(); return; }
        set.Check(r.Hijacked, N(h.Body.GlobalPosition), h.Car != null);
    }

    /// <summary>The holder died (any death): they let go. The feed is not flushed, so their last words still go out.</summary>
    void RozhlasSetDeath(ushort victim) => _rozhlasSet?.Release(victim);

    /// <summary>The holder left the server.</summary>
    void RozhlasSetLeave(ushort id) => _rozhlasSet?.Release(id);

    /// <summary>Join during a hijack: who holds the set (nobody, maybe) and where the table is.</summary>
    void SendRozhlasSetOnJoin(Client c)
    {
        if (_rozhlasSet is { } set && (set.IsHeld || _rozhlasBroadcast.Value is { Hijacked: true })) Send(c, new EventMsg((byte)EventKind.RozhlasSet, RozhlasSetCodec.Encode(set.Holder, set.Table)));
    }

    /// <summary>
    /// One upstream mic frame. Kept only from the current holder while the mic is hijacked; everything else is dropped without a word (a stale frame from a player who just let go,
    /// a client that is not the holder, a malformed packet). Decoded and written into the live feed.
    /// </summary>
    void OnRozhlasMic(Client c, byte[] d)
    {
        if (_rozhlasSet is not { } set || !set.IsHolder(c.Id) || _rozhlasBroadcast.Value is not { Hijacked: true }) return;
        if (!RozhlasMicWire.TryDecode(d, out var seq, out var opus)) return;
        var feed = _liveFeed.Value;
        if (feed == null) return;
        _micIn.Receive(seq, opus, pcm => feed.Write(pcm));
    }

    /// <summary>"/rozhlas take" and "/rozhlas release": the admin's way around holding E at the dictation table; "/rozhlas goto" stands them at it. Returns false for any other argument.</summary>
    bool ChatRozhlasSet(Client c, string action)
    {
        if (action == "goto")
        {
            if (UradTable is not { } t) { ChatTo(c, ChatLineKind.Error, "The dictation table has no position (urad.json could not be read)."); return true; }
            c.Body.Teleport(t + new Vector3(0.9f, 0.1f, 0f)); // within the 2 m reach
            ChatTo(c, ChatLineKind.Info, "You stand at the dictation table.");
            return true;
        }
        if (action is not ("take" or "release")) return false;
        if (EnsureRozhlas() == null || _rozhlasSet is not { } set) { ChatTo(c, ChatLineKind.Error, "The rozhlas has no data (rozhlas.json could not be read)."); return true; }
        if (action == "release")
        {
            if (!set.IsHeld) { ChatTo(c, ChatLineKind.Error, "Nobody holds the set."); return true; }
            var was = set.Holder;
            set.Clear();
            ChatToAll(ChatLineKind.System, "", $"{c.Name} took the set from {NameOf(was)}.");
            return true;
        }
        switch (TakeRozhlasSet(c.Id))
        {
            case RozhlasTake.Taken: ChatTo(c, ChatLineKind.Info, "You hold the set. Hold the talk key to speak."); break;
            case RozhlasTake.AlreadyYours: ChatTo(c, ChatLineKind.Info, "You already hold the set."); break;
            case RozhlasTake.NotHijacked: ChatTo(c, ChatLineKind.Error, "The set can be taken only while the mic is hijacked. /event rozhlas, then /rozhlas hijack."); break;
            case RozhlasTake.Occupied: ChatTo(c, ChatLineKind.Error, $"{NameOf(set.Holder)} holds the set. /rozhlas release lets it go."); break;
            case RozhlasTake.TooFar: ChatTo(c, ChatLineKind.Error, "Too far from the dictation table."); break;
            case RozhlasTake.InVehicle: ChatTo(c, ChatLineKind.Error, "Get out of the vehicle first."); break;
        }
        return true;
    }
}
