using Godot;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Server.Parish;

/// <summary>
/// The Holy Mass on the server (docs/events/README.md, "Holy Mass"): <see cref="HolyMassEvent"/> runs the timeline, the pastor and the monks and the
/// seats; this class puts it in the world. <c>/event mass</c> starts it, <c>/event current end</c> concludes it as if it was completed.
/// <para>
/// A seated player: the server does not simulate their body or their weapons (they cannot fire), still takes their look, and stands them up on a press of
/// interact or jump. They can be shot: a sitter is a crouching figure on the floor under the seat, replicated as a crouch (there is no sit pose for the
/// player character; a citizen has one only because of the Synty clips). The client does not predict its body while seated (<see cref="EventKind.MassSeat"/>).
/// </para>
/// <para>
/// What follows the Mass: the church doors (<see cref="ChurchServer.FollowMass"/>), the demon when the pastor turns (<see cref="DemonServer.Begin"/>), the blessing (<see cref="StatusServer.GrantBlessing"/>).
/// </para>
/// </summary>
internal sealed class MassServer
{
    readonly ServerContext _ctx;
    readonly HolyMassFile _holyMass;
    readonly CitizenServer _citizens;
    readonly ChurchServer _church;
    readonly DemonServer _demon;
    readonly Sitters _sitters;
    readonly StatusServer _status;
    readonly EventBook _events;
    readonly Func<ServerClient, bool> _ensureNav;
    bool _dirty;
    bool _won; // the demon was killed: the event is a win although the Mass itself was void
    MassPhase _was; // the phase before the one just announced: a Mass that ran to Completed is a win, anything else is cancelled

    /// <param name="ensureNav">The navigation mesh, built when first needed (the server pauses for a few seconds); false when it could not be built.</param>
    public MassServer(ServerContext ctx, HolyMassFile holyMass, CitizenServer citizens, ChurchServer church, DemonServer demon, Sitters sitters, StatusServer status, EventBook events, Func<ServerClient, bool> ensureNav)
    {
        _ctx = ctx; _holyMass = holyMass; _citizens = citizens; _church = church; _demon = demon; _sitters = sitters; _status = status; _events = events; _ensureNav = ensureNav;
    }

    /// <summary>The Holy Mass event; null until the first <c>/event mass</c>.</summary>
    public HolyMassEvent? Event { get; private set; }

    /// <summary>The demon was killed: the event is a win although the Mass itself was void.</summary>
    public void MarkWon() => _won = true;

    /// <summary>The round is over: the Holy Mass does not outlive it.</summary>
    public void RoundEnd() => Event?.Forget();

    /// <summary>A sitter stood up: the Mass forgets the seat.</summary>
    public void Stood(ushort id) => Event?.Stand(id);

    /// <summary>A free seat in reach of <paramref name="at"/> while the Mass runs (the key press would sit there).</summary>
    public bool SeatNear(System.Numerics.Vector3 at) => Event is { Running: true } m && m.SeatNear(at) != null;

    /// <summary>Why the Mass cannot be set up (no church, no navigation), or null; builds the navigation the first time.</summary>
    public string? Ensure(ServerClient c)
    {
        if (_citizens.Ensure() is { } why) return why;
        if (!_ensureNav(c)) return "The Mass needs the navigation mesh, which failed to build. See the server log.";
        if (Event != null) return null;
        var mass = Event = new HolyMassEvent(_holyMass.Mass, _citizens.Church!, _citizens.Crowd!);
        mass.MassPhaseChanged += p =>
        {
            GD.Print($"server: holy mass: {p}");
            // the Holy Mass and the demon that follows it are one event of the book (so the Bigfoot cannot start beside it): booked by the first phase change, closed when it is Idle again
            if (p != MassPhase.Idle) _events.Start(EventBook.Mass, _ctx.Now);
            else { _events.End(EventBook.Mass, _won || _was == MassPhase.Completed ? EventPhase.Won : EventPhase.Despawned, _ctx.Now); _won = false; }
            _was = p;
            if (p == MassPhase.Idle) { foreach (var id in _sitters.InPews()) _sitters.StandUp(id); _demon.Clear(); } // not the one at the rozhlas set
            _church.FollowMass(p); // open for the event, slammed shut when the pastor turns, shut again afterwards once nobody is left inside
        };
        mass.PastorTurns += () => { GD.Print($"server: holy mass: the pastor turns after {mass.PastorKills} kills"); _demon.Begin(); };
        mass.Completed += OnCompleted;
        mass.Changed += () => _dirty = true;
        mass.Sinned += k => GD.Print($"server: holy mass: #{k.Killer} is a sinner (killed a {k.Role.ToString().ToLowerInvariant()})");
        return null;
    }

    /// <summary>"/event mass": the gather starts, the bells ring.</summary>
    public void Chat(ServerClient c)
    {
        if (_events.Current is { Kind: not EventBook.Mass } && _events.WhyNotStart(EventBook.Mass) is { } busy) { _ctx.ChatTo(c, ChatLineKind.Error, busy); return; }
        if (Ensure(c) is { } why) { _ctx.ChatTo(c, ChatLineKind.Error, why); return; }
        var mass = Event!;
        if (mass.WhyNot() is { } no) { _ctx.ChatTo(c, ChatLineKind.Error, no); return; }
        var sent = mass.Start();
        if (sent == 0) GD.PrintErr("server: holy mass: no citizen could leave (no house in range or no route); the pastor and monks stand alone");
        _dirty = false;
        SendState(bells: true);
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} started the Holy Mass.");
        _ctx.ChatTo(c, ChatLineKind.Info, $"Holy Mass: {sent} citizens walk to the church, the Mass begins {_holyMass.Mass.StartDelaySeconds:0} s after the last one has sat down (at the latest in {_holyMass.Mass.BellsLeadSeconds + _holyMass.Mass.GatherSeconds:0} s) and lasts {_holyMass.Mass.MassSeconds:0} s. /event current end concludes it.");
    }

    /// <summary>"/event current end" while the Mass runs: concludes it as if it was completed. Done once more, it removes what is left.</summary>
    public void End(ServerClient c)
    {
        if (Event is not { Running: true } mass) { _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        var was = mass.Phase;
        mass.Conclude();
        _ctx.ChatTo(c, ChatLineKind.Info, was is MassPhase.Gather or MassPhase.Mass ? $"{c.Name} concluded the Holy Mass." : $"{c.Name} ended the Holy Mass.");
    }

    MassStateMsg StateNow(bool bells)
    {
        var m = Event;
        var spot = m?.PastorSpot?.Pos ?? default;
        return new MassStateMsg(m?.Phase ?? MassPhase.Idle, m?.Chanting ?? false, (byte)Math.Min(m?.PastorKills ?? 0, 255), new System.Numerics.Vector3(spot.X, spot.Y + 1.5f, spot.Z), bells);
    }

    void SendState(bool bells = false) => _ctx.Broadcast(EventKind.MassState, MassCodec.EncodeState(StateNow(bells)));

    /// <summary>Join: the Mass as it is, when one runs.</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (Event is { Running: true }) _ctx.SendEvent(c, EventKind.MassState, MassCodec.EncodeState(StateNow(false)));
    }

    /// <summary>Once a tick: the doors' deferred close, the event's clock, the demon, the changes the clients show, and every player's seat.</summary>
    public void Tick(float dt)
    {
        _church.Tick();
        if (Event is not { } mass) return;
        mass.Update(dt);
        _demon.Tick(dt);
        _demon.BodyTick();
        if (_dirty) { _dirty = false; SendState(); }
        foreach (var c in _ctx.Players.Values)
        {
            if (c.Peer == null) continue; // bots do not sit
            if (_sitters.IsSeated(c.Id))
            {
                if (!_sitters.InPew(c.Id)) continue; // at the rozhlas set: the set's own rules stand them up
                if (!_ctx.IsAlive(c.Id) || c.Car != null || mass.SeatOf(c.Id) == null) _sitters.StandUp(c.Id);
                continue;
            }
            var want = mass.Running && c.Car == null && _ctx.IsAlive(c.Id) && mass.SeatNear(c.Body.GlobalPosition.ToNumerics()) != null ? MassSeatState.CanSit : MassSeatState.None;
            if (_sitters.Told(c.Id) != want) _sitters.Tell(c, want, 0);
        }
    }

    /// <summary>A press of interact: sits the player in a free seat in reach. False when there is none (the press is for something else).</summary>
    public bool Sit(ServerClient c)
    {
        if (Event is not { Running: true } mass) return false;
        var seat = mass.TrySit(c.Id, c.Body.GlobalPosition.ToNumerics());
        if (seat == null) return false;
        var feet = Church.FeetOf(seat, _holyMass.Citizens.SeatHeightM);
        c.Body.Teleport(new Vector3(feet.X, feet.Y + 0.02f, feet.Z));
        c.Body.Yaw = seat.Yaw;
        c.Inputs.Clear();
        _ctx.Match.SetPosition(c.Id, feet);
        _sitters.SitInPew(c, seat);
        GD.Print($"server: {c.Name} sat in seat {seat.Id}");
        return true;
    }

    /// <summary>The Mass completed: each blessed player is put at the church door, facing it, told, and handed to <see cref="StatusServer.GrantBlessing"/>.</summary>
    void OnCompleted(IReadOnlyList<ushort> blessed)
    {
        GD.Print($"server: holy mass completed, blessed: {(blessed.Count == 0 ? "nobody" : string.Join(", ", blessed.Select(b => "#" + b)))}");
        var exit = _citizens.Church?.Exit;
        foreach (var id in blessed)
        {
            if (!_ctx.Players.TryGetValue(id, out var c) || !_ctx.IsAlive(id)) continue;
            _sitters.Release(id);
            if (exit != null)
            {
                c.Body.Teleport(new Vector3(exit.Pos.X, exit.Pos.Y + 0.1f, exit.Pos.Z));
                c.Body.Yaw = exit.Yaw;
                c.Inputs.Clear();
                _ctx.Match.SetPosition(id, exit.Pos);
            }
            if (c.Peer != null)
            {
                _sitters.Tell(c, MassSeatState.None, 0);
                _ctx.SendEvent(c, EventKind.MassBlessing, MassCodec.EncodeBlessing(exit?.Yaw ?? c.Body.Yaw));
            }
            _status.GrantBlessing(id);
        }
    }
}
