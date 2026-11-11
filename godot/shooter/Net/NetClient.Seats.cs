using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The seat switch on the client: while seated in any vehicle the number keys (the weapon slot keys, the same bindings) take that seat, a small
/// top-down schematic in the bottom right shows the seats (<see cref="Hud.SetSeats"/>), and the camera follows the seat the player sits in. The server
/// says who sits where (<see cref="EventKind.SeatState"/>, sent to the riders when it changes) and moves the player on a request
/// (<see cref="EventKind.SeatSwitch"/>). A single player can drive, go to the turret and drive again.
/// </summary>
public partial class NetClient
{
    /// <summary>The seats of the vehicle the local player sits in: who is in each (0 = free) and which one is the player's (-1 until known).</summary>
    sealed record SeatView(ushort CarId, ushort[] Occupants, int Mine);

    SeatView? _seats;

    void InitSeats() => EventReceived += OnSeatEvent;

    void OnSeatEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.SeatState && SeatCodec.DecodeState(payload) is { } s)
            _seats = new SeatView(s.CarId, s.Occupants, Array.IndexOf(s.Occupants, _myId));
    }

    /// <summary>
    /// A number key while seated: asks the server for that seat. Not while a streak's point of view is flown (the weapon keys are its own), nor for the
    /// seat the player already has or one that is taken. The keys are the weapon slot actions, so a rebind moves both. True when the key was used.
    /// </summary>
    bool HandleSeatInput(InputEvent e)
    {
        if (!_driving || Dead || _gunship != null || _controlling || _seats is not { Mine: >= 0 } s || _peer == null) return false;
        if (Input.MouseMode != Input.MouseModeEnum.Captured || e is not InputEventKey { Echo: false }) return false;
        for (var n = 1; n <= s.Occupants.Length; n++)
        {
            if (!e.IsActionPressed($"slot_{n}")) continue;
            if (n - 1 != s.Mine && s.Occupants[n - 1] == 0)
                Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.SeatSwitch, SeatCodec.EncodeSwitch(n - 1))), (int)ENetPacketPeer.FlagReliable);
            return true; // the digit is the seat's while seated: it never changes the weapon
        }
        return false;
    }

    /// <summary>The local player is not in a vehicle any more: the schematic goes with them.</summary>
    void ClearSeats() => _seats = null;

    /// <summary>
    /// A rider who is not the driver (the chase camera) and not the flak gunner (the turret camera) sees from their seat: the camera sits at the seat's
    /// position on the moving vehicle and the mouse look is their own, as in first person. A chase or turret camera from the seat before ends here.
    /// </summary>
    void RideSeat(CarView v, int seat)
    {
        if (Vehicles == null || _me == null) return;
        var seats = Vehicles.Vehicles[v.Kind].Seats;
        if (seat < 0 || seat >= seats.Count || seats[seat].Offset.Length != 3) return;
        var o = seats[seat].Offset;
        _me.EndChase(_me.Yaw); // no-op unless a chase or turret camera was on
        _me.GlobalPosition = ToWorld(v, new Vector3(o[0], o[1], o[2]));
        HideSelfInCar();
    }

    /// <summary>The vehicle and occupants the schematic shows, or nothing when the local player is not seated (or the vehicle is not known yet).</summary>
    (VehicleDef Def, ushort[] Occupants, int Mine)? SeatPanel()
    {
        if (!_driving || _seats is not { } s || Vehicles == null || !_cars.TryGetValue(s.CarId, out var v) || v.Kind >= Vehicles.Vehicles.Count) return null;
        return (Vehicles.Vehicles[v.Kind], s.Occupants, s.Mine);
    }
}
