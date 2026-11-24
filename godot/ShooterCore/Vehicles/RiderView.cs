using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>How a seated local rider sees the world (#339). Per rider, this client only: nothing is sent.</summary>
public enum RiderView
{
    /// <summary>Third person, the vehicle's chase camera, the own figure drawn on the seat.</summary>
    Chase,
    /// <summary>First person from the seat's eye (<see cref="SeatEye"/>), free look with the head.</summary>
    Cockpit,
    /// <summary>A gun's or a screen's own first-person view (door gun, flak, ring gun, an armed seat, the Scud operator's): it is that seat's cockpit, not switchable.</summary>
    Sight,
}

/// <summary>What the view of a seat can be.</summary>
public enum SeatViewKind
{
    /// <summary>Switchable; a chosen mode is kept (a driver's seat, a passenger's behind the hull).</summary>
    Covered,
    /// <summary>Switchable; the rider sits in the open (ADR 0012), so the chase view is the default on entering.</summary>
    Open,
    /// <summary>Not switchable: the seat works a gun or a screen and looks through its sight.</summary>
    Sight,
}

/// <summary>
/// The view mode of the local rider as a declared machine ("Rider.View", docs/state-machines.md), and the rules for what it does on entering, on
/// a seat switch and on leaving. The mode is the player's own choice with the vehicle view key, kept across seats and vehicles for the session:
/// <list type="bullet">
/// <item><b>Boarding</b> a <see cref="SeatViewKind.Sight"/> seat: <see cref="RiderView.Sight"/>. An <see cref="SeatViewKind.Open"/> seat: <see cref="RiderView.Chase"/>.
/// A covered seat: the last choice; with none yet the driver's seat is Chase (as before #339) and a passenger's Cockpit (as before).</item>
/// <item><b>Seat switch</b> keeps the mode. Into a sight seat: Sight (the choice is remembered). Out of one: back to the choice (Chase in the open).</item>
/// <item><b>Toggle</b> flips Chase and Cockpit and is the choice; a Sight seat ignores it.</item>
/// <item><b>Leaving</b> the vehicle keeps only the choice.</item>
/// </list>
/// </summary>
public sealed class RiderViewMode
{
    public static readonly StateMachineSpec<RiderView> Spec = StateMachineSpec<RiderView>.Define("Rider.View",
            "How the local rider of a vehicle seat sees: chase camera, first person from the seat, or the gun's own sight.", RiderView.Chase)
        .Allow(RiderView.Chase, RiderView.Cockpit, RiderView.Sight)
        .Allow(RiderView.Cockpit, RiderView.Chase, RiderView.Sight)
        .Allow(RiderView.Sight, RiderView.Chase, RiderView.Cockpit)
        .Note(RiderView.Chase, "the vehicle's chase camera; the figure is drawn on the seat. The view key goes to Cockpit.")
        .Note(RiderView.Cockpit, "first person from the seat's eye in the hull frame, the own figure hidden for the viewer (its shadow stays); the driver's rig only on the driver's seat. The view key goes to Chase.")
        .Note(RiderView.Sight, "an armed, gunner or operator seat: the gun's or the screen's own first-person view; the view key does nothing. A seat switch leaves it for the last choice.")
        .Build();

    readonly StateMachine<RiderView> _fsm = new(Spec);
    RiderView? _choice;

    public RiderView Current => _fsm.Current;
    /// <summary>The view key works in this seat.</summary>
    public bool Switchable => !_fsm.Is(RiderView.Sight);
    /// <summary>The view is first person (head look): the cockpit, or a sight.</summary>
    public bool FirstPerson => !_fsm.Is(RiderView.Chase);

    /// <summary>What a seat's view can be: a gunner's, an armed seat and the Scud operator's are sights; a seat in the open is Open; the rest Covered.</summary>
    public static SeatViewKind KindOf(VehicleDef v, int seat)
    {
        if (seat < 0 || seat >= v.Seats.Count) return SeatViewKind.Covered;
        var s = v.Seats[seat];
        if (s.Armed || s.Role == "gunner" || v.Launcher is { } l && l.OperatorSeat == seat) return SeatViewKind.Sight;
        return v.SeatExposed(seat) ? SeatViewKind.Open : SeatViewKind.Covered;
    }

    RiderView Wanted(SeatViewKind kind, int seat) => kind switch
    {
        SeatViewKind.Sight => RiderView.Sight,
        SeatViewKind.Open => RiderView.Chase,
        _ => _choice ?? (seat == 0 ? RiderView.Chase : RiderView.Cockpit),
    };

    /// <summary>The rider takes a seat of a vehicle they were not in.</summary>
    public void Board(VehicleDef v, int seat) => _fsm.Enter(Wanted(KindOf(v, seat), seat));

    /// <summary>The rider moves to another seat of the same vehicle: the mode stays, except into and out of a sight.</summary>
    public void SwitchSeat(VehicleDef v, int seat)
    {
        var kind = KindOf(v, seat);
        if (kind == SeatViewKind.Sight) _fsm.Enter(RiderView.Sight);
        else if (_fsm.Is(RiderView.Sight)) _fsm.Enter(kind == SeatViewKind.Open ? _choice ?? RiderView.Chase : Wanted(kind, seat));
    }

    /// <summary>The view key. False in a sight seat (nothing changes).</summary>
    public bool Toggle()
    {
        if (!Switchable) return false;
        _choice = _fsm.Is(RiderView.Chase) ? RiderView.Cockpit : RiderView.Chase;
        _fsm.Enter(_choice.Value);
        return true;
    }

    /// <summary>The rider is out of the vehicle: the choice is kept for the next one, the machine rests in its first state.</summary>
    public void Leave() => _fsm.Reset(RiderView.Chase);
}
