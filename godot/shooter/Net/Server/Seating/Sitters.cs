using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Server.Seating;

/// <summary>
/// The players who sit, in a church pew (the Holy Mass) or at the rozhlas dictation table: who sits where, what each was last told (<see cref="MassSeatState"/>), and how a sitter
/// is treated. The server does not simulate a sitter's body or weapons (they cannot fire), still takes their look, and stands them up on a press of interact or jump. They can be
/// shot: a sitter is a crouching figure on the floor under the seat, replicated as a crouch (there is no sit pose for the player character; a citizen has one only because of the
/// Synty clips). The client does not predict its body while seated (<see cref="EventKind.MassSeat"/>).
/// </summary>
internal sealed class Sitters
{
    sealed class Sitter
    {
        /// <summary>The pew; null for the one who sits at the rozhlas dictation table, who is not part of the Mass.</summary>
        public ChurchSeat? Seat;
        public Edge Jump = new(startHeld: true); // the jump that may still be held as they sit is not a stand-up
    }

    readonly ServerContext _ctx;
    readonly Action<ushort, bool> _stood;
    readonly Dictionary<ushort, Sitter> _sitters = new();
    readonly Dictionary<ushort, StateMachine<MassSeatState>> _told = new(); // what each player was last told (a player never told is None)

    /// <param name="stood">A sitter stood up (or is no longer in the event); the flag says they sat at the dictation table, not in a pew. Called before the player is told.</param>
    public Sitters(ServerContext ctx, Action<ushort, bool> stood) { _ctx = ctx; _stood = stood; }

    public bool IsSeated(ushort id) => _sitters.ContainsKey(id);

    /// <summary>Whether the player sits in a pew, not at the dictation table.</summary>
    public bool InPew(ushort id) => _sitters.TryGetValue(id, out var s) && s.Seat != null;

    /// <summary>The ids of those who sit in a pew (a copy, to be stood up while it is walked).</summary>
    public List<ushort> InPews() => _sitters.Where(kv => kv.Value.Seat != null).Select(kv => kv.Key).ToList();

    /// <summary>The ids of those who sit at all (a copy).</summary>
    public List<ushort> All() => _sitters.Keys.ToList();

    /// <summary>The body stance everyone sees and every shot hits: a sitter crouches on the floor under the seat.</summary>
    public Stance StanceOf(ServerClient c) => _sitters.ContainsKey(c.Id) ? Stance.Crouch : c.Body.State.Stance;

    /// <summary>What the player was last told about a seat (nothing yet: <see cref="MassSeatState.None"/>).</summary>
    public MassSeatState Told(ushort id) => _told.TryGetValue(id, out var m) ? m.Current : MassSeatState.None;

    /// <summary>Tells the player about a seat: can sit, seated (facing <paramref name="yaw"/>), or none.</summary>
    public void Tell(ServerClient c, MassSeatState state, float yaw)
    {
        if (!_told.TryGetValue(c.Id, out var told)) _told[c.Id] = told = new StateMachine<MassSeatState>(MassSeatRules.Spec);
        told.Enter(state); // checked against the spec; what is told is sent either way
        _ctx.SendEvent(c, EventKind.MassSeat, MassCodec.EncodeSeat(new MassSeatMsg(state, yaw)));
    }

    /// <summary>The player sits in the pew (their body is already put there).</summary>
    public void SitInPew(ServerClient c, ChurchSeat seat)
    {
        _sitters[c.Id] = new Sitter { Seat = seat };
        Tell(c, MassSeatState.Seated, seat.Yaw);
    }

    /// <summary>The player sits at the dictation table (a bot has no connection and is not told).</summary>
    public void SitAtTable(ServerClient c, float yaw)
    {
        _sitters[c.Id] = new Sitter();
        if (c.Peer != null) Tell(c, MassSeatState.Seated, yaw);
    }

    /// <summary>The player stands, or is no longer in the event (dead, left, the Mass is over).</summary>
    public void StandUp(ushort id)
    {
        if (!_sitters.Remove(id, out var was)) return;
        _stood(id, was.Seat == null);
        if (_ctx.Players.TryGetValue(id, out var c) && c.Peer != null) Tell(c, MassSeatState.None, 0);
    }

    /// <summary>Everyone at the dictation table except <paramref name="holder"/> stands up.</summary>
    public void StandUpAtTableExcept(ushort holder)
    {
        foreach (var (id, s) in _sitters.ToList()) if (s.Seat == null && id != holder) StandUp(id);
    }

    /// <summary>Everyone stands up.</summary>
    public void StandUpAll()
    {
        foreach (var id in _sitters.Keys.ToList()) StandUp(id);
    }

    /// <summary>The player no longer sits, with no word to them (the Mass blessed them and moved them to the door).</summary>
    public void Release(ushort id) => _sitters.Remove(id);

    /// <summary>The player left the server: what they were told is forgotten.</summary>
    public void Forget(ushort id) => _told.Remove(id);

    /// <summary>A seated player's input: they look around; interact or jump stands them up. No movement, no weapon. False when the player does not sit.</summary>
    public bool Input(ServerClient c, NetInput i, bool useEdge)
    {
        if (!_sitters.TryGetValue(c.Id, out var s)) return false;
        c.Body.Yaw = Quant.Yaw(i.Yaw);
        c.Body.Pitch = Quant.Pitch(i.Pitch);
        var jump = (i.Buttons & (ushort)MoveButtons.Jump) != 0;
        var jumpEdge = s.Jump.Pressed(jump);
        if (useEdge || jumpEdge) StandUp(c.Id);
        return true;
    }
}
