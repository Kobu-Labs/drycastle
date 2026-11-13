using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Holy Mass on the server (docs/events/README.md, "Holy Mass"): <see cref="HolyMassEvent"/> runs the timeline, the pastor and the monks and the
/// seats; this file puts it in the world. <c>/event mass</c> starts it, <c>/event current end</c> concludes it as if it was completed.
/// <para>
/// A seated player: the server does not simulate their body or their weapons (they cannot fire), still takes their look, and stands them up on a press of
/// interact or jump. They can be shot: a sitter is a crouching figure on the floor under the seat, replicated as a crouch (there is no sit pose for the
/// player character; a citizen has one only because of the Synty clips). The client does not predict its body while seated (<see cref="EventKind.MassSeat"/>).
/// </para>
/// <para>
/// Hooks for other tickets: <see cref="MassPhaseChanged"/> (the doors), <see cref="PastorTurns"/> (the demon), <see cref="GrantBlessing"/> (the status system).
/// </para>
/// </summary>
public partial class NetServer
{
    HolyMassEvent? _mass;
    bool _massDirty;

    sealed class Sitter
    {
        public ChurchSeat Seat = null!;
        public bool JumpHeld = true; // the jump that may still be held as they sit is not a stand-up
    }

    readonly Dictionary<ushort, Sitter> _sitters = new();
    readonly Dictionary<ushort, MassSeatState> _seatHint = new(); // what each player was last told

    /// <summary>The Holy Mass event; null until the first <c>/event mass</c>.</summary>
    public HolyMassEvent? Mass => _mass;

    /// <summary>The Mass changed phase (Gather, Mass, Completed, Void, and Idle at the end). The church doors follow this.</summary>
    public event Action<MassPhase>? MassPhaseChanged;

    /// <summary>The pastor was killed the third time: he turns into the demon and the Mass is void. HOOK for the demon ticket; the event stays until <c>/event current end</c>.</summary>
    public event Action? PastorTurns;

    /// <summary>Why the Mass cannot be set up (no church, no navigation), or null; builds the navigation the first time.</summary>
    string? EnsureMass(Client c)
    {
        if (EnsureCitizens() is { } why) return why;
        if (_nav == null && !_navTried)
        {
            ChatTo(c, ChatLineKind.Info, "Building the navigation for the first time. The server pauses for a few seconds.");
            _host.Flush();
            BuildBotNav(force: true);
        }
        if (_nav == null) return "The Mass needs the navigation mesh, which failed to build. See the server log.";
        if (_mass != null) return null;
        _mass = new HolyMassEvent(HolyMass.Mass, _church!, _crowd!);
        _mass.MassPhaseChanged += p =>
        {
            GD.Print($"server: holy mass: {p}");
            if (p == MassPhase.Idle) { foreach (var id in _sitters.Keys.ToList()) MassStandUp(id); DemonClear(); }
            // the doors: open for the event, slammed shut when the pastor turns, and shut again afterwards once nobody is left inside
            if (p == MassPhase.Gather) { _closeWhenEmpty = false; ChurchDoors.Open(); }
            else if (p == MassPhase.Void) ChurchDoors.Close();
            else if (p == MassPhase.Idle) _closeWhenEmpty = true;
            MassPhaseChanged?.Invoke(p);
        };
        _mass.PastorTurns += () => { GD.Print($"server: holy mass: the pastor turns after {_mass.PastorKills} kills"); PastorTurns?.Invoke(); };
        _mass.Completed += OnMassCompleted;
        _mass.Changed += () => _massDirty = true;
        _mass.Sinned += k => GD.Print($"server: holy mass: #{k.Killer} is a sinner (killed a {k.Role.ToString().ToLowerInvariant()})");
        return null;
    }

    /// <summary>"/event mass": the gather starts, the bells ring.</summary>
    void ChatMass(Client c)
    {
        if (EnsureMass(c) is { } why) { ChatTo(c, ChatLineKind.Error, why); return; }
        if (_mass!.WhyNot() is { } no) { ChatTo(c, ChatLineKind.Error, no); return; }
        var sent = _mass.Start();
        if (sent == 0) GD.PrintErr("server: holy mass: no citizen could leave (no house in range or no route); the pastor and monks stand alone");
        _massDirty = false;
        SendMassState(bells: true);
        ChatToAll(ChatLineKind.System, "", $"{c.Name} started the Holy Mass.");
        ChatTo(c, ChatLineKind.Info, $"Holy Mass: {sent} citizens walk to the church, the Mass begins in {HolyMass.Mass.GatherSeconds:0} s and lasts {HolyMass.Mass.MassSeconds:0} s. /event current end concludes it.");
    }

    /// <summary>"/event current end": concludes the running event as if it was completed. Done once more, it removes what is left.</summary>
    void ChatCurrentEnd(Client c)
    {
        if (_mass is not { Running: true }) { ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        var was = _mass.Phase;
        _mass.Conclude();
        ChatToAll(ChatLineKind.System, "", was is MassPhase.Gather or MassPhase.Mass ? $"{c.Name} concluded the Holy Mass." : $"{c.Name} ended the Holy Mass.");
    }

    MassStateMsg MassStateNow(bool bells)
    {
        var m = _mass;
        var spot = m?.PastorSpot?.Pos ?? default;
        return new MassStateMsg(m?.Phase ?? MassPhase.Idle, m?.Chanting ?? false, (byte)Math.Min(m?.PastorKills ?? 0, 255), N(new Vector3(spot.X, spot.Y + 1.5f, spot.Z)), bells);
    }

    void SendMassState(bool bells = false) => BroadcastEvent(EventKind.MassState, MassCodec.EncodeState(MassStateNow(bells)));

    /// <summary>Join: the Mass as it is, when one runs.</summary>
    void SendMassOnJoin(Client c)
    {
        if (_mass is { Running: true }) Send(c, new EventMsg((byte)EventKind.MassState, MassCodec.EncodeState(MassStateNow(false))));
    }

    /// <summary>Once a tick: the event's clock, the changes the clients show, and every player's seat.</summary>
    bool _closeWhenEmpty;

    void MassTick(float dt)
    {
        if (_closeWhenEmpty && ChurchDoors.PlayersInside().Count == 0) { _closeWhenEmpty = false; ChurchDoors.Close(); }
        if (_mass == null) return;
        _mass.Update(dt);
        DemonTick(dt);
        if (_massDirty) { _massDirty = false; SendMassState(); }
        foreach (var c in _byId.Values)
        {
            if (c.Peer == null) continue; // bots do not sit
            if (_sitters.ContainsKey(c.Id))
            {
                if (_match.Get(c.Id)?.Life != PlayerLife.Alive || c.Car != null || _mass.SeatOf(c.Id) == null) MassStandUp(c.Id);
                continue;
            }
            var want = _mass.Running && c.Car == null && _match.Get(c.Id)?.Life == PlayerLife.Alive && _mass.SeatNear(N(c.Body.GlobalPosition)) != null ? MassSeatState.CanSit : MassSeatState.None;
            if (_seatHint.GetValueOrDefault(c.Id) != want) SendSeat(c, want, 0);
        }
    }

    void SendSeat(Client c, MassSeatState state, float yaw)
    {
        _seatHint[c.Id] = state;
        Send(c, new EventMsg((byte)EventKind.MassSeat, MassCodec.EncodeSeat(new MassSeatMsg(state, yaw))));
    }

    /// <summary>A press of interact: sits the player in a free seat in reach. False when there is none (the press is for something else).</summary>
    bool MassSit(Client c)
    {
        if (_mass is not { Running: true }) return false;
        var seat = _mass.TrySit(c.Id, N(c.Body.GlobalPosition));
        if (seat == null) return false;
        var feet = Church.FeetOf(seat, HolyMass.Citizens.SeatHeightM);
        c.Body.Teleport(new Vector3(feet.X, feet.Y + 0.02f, feet.Z));
        c.Body.Yaw = seat.Yaw;
        c.Inputs.Clear();
        _match.SetPosition(c.Id, feet);
        _sitters[c.Id] = new Sitter { Seat = seat };
        SendSeat(c, MassSeatState.Seated, seat.Yaw);
        GD.Print($"server: {c.Name} sat in seat {seat.Id}");
        return true;
    }

    /// <summary>The player stands, or is no longer in the event (dead, left, the Mass is over).</summary>
    void MassStandUp(ushort id)
    {
        if (!_sitters.Remove(id)) return;
        _mass?.Stand(id);
        if (_byId.TryGetValue(id, out var c) && c.Peer != null) SendSeat(c, MassSeatState.None, 0);
    }

    /// <summary>A seated player's input: they look around; interact or jump stands them up. No movement, no weapon.</summary>
    void SitterInput(Client c, Sitter s, NetInput i, bool useEdge)
    {
        c.Body.Yaw = Quant.Yaw(i.Yaw);
        c.Body.Pitch = Quant.Pitch(i.Pitch);
        var jump = (i.Buttons & (ushort)MoveButtons.Jump) != 0;
        var jumpEdge = jump && !s.JumpHeld;
        s.JumpHeld = jump;
        if (useEdge || jumpEdge) MassStandUp(c.Id);
    }

    /// <summary>The body stance everyone sees and every shot hits: a sitter crouches on the floor under the seat.</summary>
    Stance BodyStanceOf(Client c) => _sitters.ContainsKey(c.Id) ? Stance.Crouch : c.Body.State.Stance;

    /// <summary>The Mass completed: each blessed player is put at the church door, facing it, told, and handed to <see cref="GrantBlessing"/>.</summary>
    void OnMassCompleted(IReadOnlyList<ushort> blessed)
    {
        GD.Print($"server: holy mass completed, blessed: {(blessed.Count == 0 ? "nobody" : string.Join(", ", blessed.Select(b => "#" + b)))}");
        var exit = _church?.Exit;
        foreach (var id in blessed)
        {
            if (!_byId.TryGetValue(id, out var c) || _match.Get(id)?.Life != PlayerLife.Alive) continue;
            _sitters.Remove(id);
            if (exit != null)
            {
                c.Body.Teleport(new Vector3(exit.Pos.X, exit.Pos.Y + 0.1f, exit.Pos.Z));
                c.Body.Yaw = exit.Yaw;
                c.Inputs.Clear();
                _match.SetPosition(id, exit.Pos);
            }
            if (c.Peer != null)
            {
                SendSeat(c, MassSeatState.None, 0);
                Send(c, new EventMsg((byte)EventKind.MassBlessing, MassCodec.EncodeBlessing(exit?.Yaw ?? c.Body.Yaw)));
            }
            GrantBlessing(id);
        }
    }
}
