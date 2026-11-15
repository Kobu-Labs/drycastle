using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Obecny Urad's door on the server: open exactly while a rozhlas detour is open (<see cref="RozhlasBroadcast.DetourOpen"/>: OnAir or Hijacked), shut when the loudspeaker is Idle. The state is
/// that phase, so there is no machine of its own: <see cref="RozhlasPhaseChanged"/> moves the box (<see cref="UradDoorNode"/>, on the World layer in the doorway of urad.json, made with the map) and
/// tells everyone (<see cref="EventKind.UradDoor"/>, also to a joiner while it is open). When it shuts, nobody stays locked in for the 10 minutes to the next broadcast: every living player in an
/// interior room or in the doorway is put <see cref="Urad.EvictOutsideM"/> outside it (<see cref="Urad.EvictionPoint"/>). No urad.json, no door: nothing here does anything.
/// </summary>
public partial class NetServer
{
    bool _uradDoorOpen;

    /// <summary>Whether the Urad door is open (false without a door).</summary>
    public bool UradDoorOpen => _uradDoorOpen;

    /// <summary>Called once from <see cref="EnsureRozhlas"/>: the door follows the loudspeaker's phase.</summary>
    void EnsureRozhlasDoor() =>
        RozhlasPhaseChanged += (_, to) => SetUradDoor(to != RozhlasPhase.Idle);

    /// <summary>The ONE place the door moves in the world: the box first (nobody can walk into the doorway behind the move), then whoever is inside, then every client. Only a change does anything.</summary>
    void SetUradDoor(bool open)
    {
        if (open == _uradDoorOpen || EnsureUrad() == null) return;
        _uradDoorOpen = open;
        UradDoorNode.Current?.SetOpen(open, animate: false);
        if (!open) EvictFromUrad();
        BroadcastEvent(EventKind.UradDoor, UradDoorCodec.Encode(open));
        GD.Print($"server: Urad door {(open ? "open" : "closed")}");
    }

    /// <summary>The joiner's catch-up: the door is shut by default, so only an open door is told.</summary>
    void SendUradDoorOnJoin(Client c)
    {
        if (_uradDoorOpen) Send(c, new EventMsg((byte)EventKind.UradDoor, UradDoorCodec.Encode(true)));
    }

    /// <summary>The door shut: whoever is in the Urad or its doorway stands outside it (a ride in a vehicle is left alone).</summary>
    void EvictFromUrad()
    {
        if (EnsureUrad() is not { } urad) return;
        foreach (var c in _byId.Values)
        {
            if (!c.Joined || c.Car != null || _match.Get(c.Id)?.Life != PlayerLife.Alive) continue;
            var p = c.Body.GlobalPosition;
            if (urad.EvictionPoint(new NVec3(p.X, p.Y, p.Z), MoveConsts.CapsuleRadius) is not { } to) continue;
            c.Body.Teleport(V(to));
            c.Inputs.Clear();
            _match.SetPosition(c.Id, to);
            GD.Print($"server: {c.Name} #{c.Id} shown out of the Urad");
        }
    }
}
