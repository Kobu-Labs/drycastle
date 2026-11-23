using Godot;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The seat switch on the client: while seated in any vehicle the number keys (the weapon slot keys, the same bindings) take that seat, a small
/// top-down schematic in the bottom right shows the seats (<see cref="Hud.SetSeats"/>), and the camera follows the seat the player sits in. The server
/// says who sits where (<see cref="EventKind.SeatState"/>, sent to the riders when it changes) and moves the player on a request
/// (<see cref="EventKind.SeatSwitch"/>). A single player can drive, go to the turret and drive again.
/// </summary>
public partial class NetClient
{
    void InitSeats() => EventReceived += OnSeatEvent;

    void OnSeatEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.SeatState && SeatCodec.DecodeState(payload) is { } s)
            Ctx.Cars.Seats = new SeatView(s.CarId, s.Occupants, Array.IndexOf(s.Occupants, Ctx.MyId));
    }

    /// <summary>
    /// A number key while seated: asks the server for that seat. Not while a streak's point of view is flown (the weapon keys are its own), nor for the
    /// seat the player already has or one that is taken. The keys are the weapon slot actions, so a rebind moves both. True when the key was used.
    /// </summary>
    bool HandleSeatInput(InputEvent e)
    {
        if (!Mode.SeatKeysLive || Ctx.Cars.Seats is not { Mine: >= 0 } s || _peer == null) return false;
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

    /// <summary>
    /// The input carries the view as the head's look (<see cref="MoveButtons.HeadLook"/>): seated in a first-person view, a passenger's own eyes or the
    /// driver's cockpit view, or a gun's sight (<see cref="RiderViewMode"/>). In the chase view the yaw is the camera's orbit, and a gun's rider aims instead, so the bit stays clear there.
    /// </summary>
    bool HeadLookWanted()
    {
        if (!Ctx.Driving || Dead || Ctx.Me == null || _gunship != null || _controlling || !Mode.ViewIsBody || Ctx.Cars.MySeat() == null) return false;
        return _view.FirstPerson;
    }

    /// <summary>The speed of the vehicle the local player rides, m/s: what widens an armed rider's spread.</summary>
    static float RideSpeed(CarView v, DriveState? drawn) => drawn is { } d ? MathF.Abs(d.Speed) : v.Kmh / 3.6f;

    string _armedPrompt = "";
    double _armedPromptAt = -10;

    /// <summary>The armed rider's prompt line: the keys as they are bound (the weapon change is the mouse wheel, which has no binding). Rebuilt once a second in case one was rebound.</summary>
    string ArmedSeatPrompt()
    {
        if (Ctx.Now - _armedPromptAt < 1.0) return _armedPrompt;
        _armedPromptAt = Ctx.Now;
        return _armedPrompt = Strings.F("hud.armedseat.prompt", InputBindings.Name("fire"), InputBindings.Name("reload"), InputBindings.Name("interact"));
    }

    /// <summary>
    /// A rider of a sight seat (an armed seat, a gunner's the flak and the door gun do not use this, the Scud operator's: <see cref="RiderView.Sight"/>) sees from their seat: the camera sits at the
    /// seat's position on the moving vehicle and the mouse look is their own, as in first person. A chase or turret camera from the seat before ends here.
    /// The other seats' first-person view is <see cref="CockpitFrame"/>.
    /// </summary>
    void RideSeat(CarView v, int seat)
    {
        if (Vehicles == null || Ctx.Me == null) return;
        var seats = Vehicles.Vehicles[v.Kind].Seats;
        if (seat < 0 || seat >= seats.Count || seats[seat].Offset.Length != 3) return;
        var o = seats[seat].Offset;
        Ctx.Me.EndChase(Ctx.Me.Yaw); // no-op unless a chase or turret camera was on
        var feet = SeatAnchor(v).GlobalTransform * new Vector3(o[0], o[1], o[2]); // on the body as it is drawn: the seat leans and bobs with it
        // a seat in the open is drawn and hit seated (or standing in the ring): the eye is that figure's, not a standing body's on the seat
        // (a covered seat that sets eye_height has the eye at that height over its feet: the Humvee's roof is under a standing eye)
        if (seats[seat].Exposed) Ctx.Me.SeatView(feet, SeatAimRules.EyeHeight(seats[seat]));
        else if (seats[seat].EyeHeight is { } eye) Ctx.Me.SeatView(feet, eye);
        else Ctx.Me.GlobalPosition = feet;
        if (seats[seat].Armed)
        {
            // the look is the aim, and it is held inside the seat's arc, which turns and tilts with the vehicle: the view cannot go where the shots cannot
            var rot = v.Node.Rotation;
            var aim = SeatAimRules.Aim(seats[seat].Aim, rot.Y, rot.X, rot.Z, Ctx.Me.Yaw, Ctx.Me.Pitch);
            Ctx.Me.Yaw = aim.ViewYaw; Ctx.Me.Pitch = aim.ViewPitch;
        }
        else Ctx.Me.Pitch = RiderPose.ClampPitch(Ctx.Me.Pitch); // a head only looks so far up and down: the view stops where the figure's neck does
        HideSelfInCar();
    }
}
