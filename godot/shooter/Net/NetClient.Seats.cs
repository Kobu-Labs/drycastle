using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

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

    /// <summary>The seat the local player sits in, with its vehicle, once the server has said who sits where; null on foot or before that.</summary>
    (CarView Car, VehicleDef Def, VehicleSeatDef Seat, int Index)? MySeat()
    {
        if (!_driving || Dead || _seats is not { Mine: >= 0 } s || Vehicles == null || !_cars.TryGetValue(s.CarId, out var v) || v.Kind >= Vehicles.Vehicles.Count) return null;
        var def = Vehicles.Vehicles[v.Kind];
        return s.Mine < def.Seats.Count ? (v, def, def.Seats[s.Mine], s.Mine) : null;
    }

    /// <summary>
    /// The local player sits in an armed seat (ADR 0008): they keep their own weapons there, so the weapon in the view, its fire and reload
    /// prediction, the crosshair and the aim are on as they are on foot. The number keys stay the seats'; the mouse wheel changes the weapon.
    /// </summary>
    bool InArmedSeat => _gunship == null && !_controlling && MySeat() is { Seat.Armed: true };

    /// <summary>The speed of the vehicle the local player rides, m/s: what widens an armed rider's spread.</summary>
    static float RideSpeed(CarView v, DriveState? drawn) => drawn is { } d ? MathF.Abs(d.Speed) : v.Kmh / 3.6f;

    string _armedPrompt = "";
    double _armedPromptAt = -10;

    /// <summary>The armed rider's prompt line: the keys as they are bound (the weapon change is the mouse wheel, which has no binding). Rebuilt once a second in case one was rebound.</summary>
    string ArmedSeatPrompt()
    {
        if (Now - _armedPromptAt < 1.0) return _armedPrompt;
        _armedPromptAt = Now;
        return _armedPrompt = Strings.F("hud.armedseat.prompt", InputBindings.Name("fire"), InputBindings.Name("reload"), InputBindings.Name("interact"));
    }

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
        var feet = SeatAnchor(v).GlobalTransform * new Vector3(o[0], o[1], o[2]); // on the body as it is drawn: the seat leans and bobs with it
        // a seat in the open is drawn and hit seated (or standing in the ring): the eye is that figure's, not a standing body's on the seat
        if (seats[seat].Exposed) _me.SeatView(feet, SeatAimRules.EyeHeight(seats[seat])); else _me.GlobalPosition = feet;
        if (seats[seat].Armed)
        {
            // the look is the aim, and it is held inside the seat's arc, which turns and tilts with the vehicle: the view cannot go where the shots cannot
            var rot = v.Node.Rotation;
            var aim = SeatAimRules.Aim(seats[seat].Aim, rot.Y, rot.X, rot.Z, _me.Yaw, _me.Pitch);
            _me.Yaw = aim.ViewYaw; _me.Pitch = aim.ViewPitch;
        }
        HideSelfInCar();
    }

    /// <summary>The vehicle and occupants the schematic shows, or nothing when the local player is not seated (or the vehicle is not known yet).</summary>
    (VehicleDef Def, ushort[] Occupants, int Mine)? SeatPanel()
    {
        if (!_driving || _seats is not { } s || Vehicles == null || !_cars.TryGetValue(s.CarId, out var v) || v.Kind >= Vehicles.Vehicles.Count) return null;
        return (Vehicles.Vehicles[v.Kind], s.Occupants, s.Mine);
    }
}
