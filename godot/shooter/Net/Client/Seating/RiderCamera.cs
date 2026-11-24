using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Aviation;
using Drycastle.Shooter.Net.Client.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Client.Seating;

/// <summary>
/// The view of a seated rider (tickets #219, #339): every seat of every vehicle has a chase view and a first-person view from the seat's eye
/// (<see cref="SeatEye"/>, in the hull frame so the sway is in the view), and the vehicle view key switches between them. Seats that work a gun or a
/// screen keep the gun's own sight as their first-person view and ignore the key. The mode is a declared machine (<see cref="RiderViewMode"/>, "Rider.View"):
/// the choice is kept across seat switches and vehicles for the session. Nothing is sent and nobody else sees it. The driver's cockpit rig
/// (<see cref="CockpitRig"/>, hands and a turning wheel) is drawn on the driver's seat only, and only where the vehicle has one (hand-built, or measured from the
/// model's steering wheel or handlebars); any other seat sees the model's own interior. A killstreak's point of view takes the camera as before; the rig is put away for as
/// long as it lasts. The own figure seated in the vehicle (<see cref="SelfView"/>) and a sight seat's camera are here too.
/// </summary>
internal sealed class RiderCamera
{
    readonly ClientContext _ctx;
    readonly FlightClient _flight;
    readonly Action<CharacterView> _dressSelf;

    /// <summary>The local rider's view mode, kept for the session (<see cref="RiderViewMode.Leave"/> keeps the choice).</summary>
    readonly RiderViewMode _view = new();
    ushort _viewCarId;
    int _viewSeat = -1;
    CockpitRig? _rig;
    CarView? _rigCar;
    bool _selfViewerHides;
    string _viewHintBase = "", _viewHintText = "";
    bool _viewHintCockpit;
    double _viewHintAt;

    /// <param name="dressSelf">Dresses the own figure for the seat (the camouflage and what the player wears).</param>
    public RiderCamera(ClientContext ctx, FlightClient flight, Action<CharacterView> dressSelf)
    {
        _ctx = ctx; _flight = flight; _dressSelf = dressSelf;
    }

    /// <summary>The own character seated in the vehicle as a body view, a child of the vehicle's body; null on foot or in the cockpit view.</summary>
    public CharacterView? SelfView { get; private set; }

    /// <summary>A gun's or a screen's seat: its own first-person view is on.</summary>
    public bool SightView => _view.Current == RiderView.Sight;

    /// <summary>The seat of <paramref name="v"/> the local player sits in (the driver is the vehicle's record, the rest the seat state), or -1.</summary>
    public int OwnSeatIn(CarView v) => v.Driver == _ctx.MyId ? 0 : _ctx.Cars.Seats is { Mine: > 0 } st && st.CarId == v.Id ? st.Mine : -1;

    /// <summary>The driver's rig of <paramref name="v"/>: its hand-built one, else one measured from the model's steering wheel or handlebars (<see cref="CockpitRig.For(string, Node3D, Node3D, System.Numerics.Vector3)"/>).</summary>
    CockpitRig.Spec? CockpitOf(CarView v)
    {
        if (_ctx.Vehicles == null || v.Kind >= _ctx.Vehicles.Vehicles.Count) return null;
        var def = _ctx.Vehicles.Vehicles[v.Kind];
        return CockpitRig.For(def.Id, v.Node, GlassClient.HostOf(v.Node), SeatEye.For(def, 0));
    }

    /// <summary>Tells the view machine which seat of which vehicle the rider is in: boarding, or a switch within the vehicle.</summary>
    public void Track(CarView v, int seat)
    {
        if (_ctx.Vehicles == null || v.Kind >= _ctx.Vehicles.Vehicles.Count) return;
        var def = _ctx.Vehicles.Vehicles[v.Kind];
        if (_viewCarId != v.Id || _viewSeat < 0) _view.Board(def, seat);
        else if (_viewSeat != seat) _view.SwitchSeat(def, seat);
        _viewCarId = v.Id; _viewSeat = seat;
    }

    /// <summary>The rider is out of the vehicle: the choice stays, the vehicle and seat are forgotten.</summary>
    public void Leave()
    {
        if (_viewSeat < 0) return;
        _view.Leave(); _viewCarId = 0; _viewSeat = -1;
    }

    /// <summary>The vehicle view key while seated: switches chase and cockpit. True when the key was used (not in a gun's or a screen's seat).</summary>
    public bool HandleInput(InputEvent e)
    {
        if (!e.IsActionPressed("vehicle_view") || !_ctx.View.Mode.CanToggleCockpitView || Input.MouseMode != Input.MouseModeEnum.Captured) return false;
        return _ctx.Driving && !_ctx.Dead && _viewSeat >= 0 && _view.Toggle();
    }

    /// <summary>
    /// The input carries the view as the head's look (<see cref="MoveButtons.HeadLook"/>): seated in a first-person view, a passenger's own eyes or the
    /// driver's cockpit view, or a gun's sight (<see cref="RiderViewMode"/>). In the chase view the yaw is the camera's orbit, and a gun's rider aims instead, so the bit stays clear there.
    /// </summary>
    public bool HeadLookWanted()
    {
        if (!_ctx.Driving || _ctx.Dead || _ctx.Me == null || _ctx.View.OnTurret() || _ctx.View.Controlling() || !_ctx.View.Mode.ViewIsBody || _ctx.Cars.MySeat() == null) return false;
        return _view.FirstPerson;
    }

    /// <summary>
    /// The cockpit view of <paramref name="v"/> from <paramref name="seat"/> for this frame: the camera sits at the seat's eye on the hull; on the
    /// driver's seat of a vehicle with a rig the rig is on the vehicle (built on the first frame) and posed from the keys. False when the chase camera is
    /// wanted instead (the view is off, a killstreak has the camera, a headless client).
    /// </summary>
    public bool CockpitFrame(CarView v, int seat, Vector3 p, Vector3 rot, float dt)
    {
        if (_view.Current != RiderView.Cockpit || _ctx.Me == null || !ShotFx.Enabled || !_ctx.View.Mode.ViewIsBody || _ctx.Vehicles == null || v.Kind >= _ctx.Vehicles.Vehicles.Count) return false;
        var def = _ctx.Vehicles.Vehicles[v.Kind];
        var spec = seat == 0 ? CockpitOf(v) : null; // the driver's rig is the driver's: another seat sees the model's own interior
        if (spec == null) EndCockpit();
        else if (_rig == null || _rigCar != v || !GodotObject.IsInstanceValid(_rig))
        {
            EndCockpit();
            _rig = CockpitRig.Make(spec);
            GlassClient.HostOf(v.Node).AddChild(_rig); // on the body, in the vehicle's frame (docs/standards/car-design.md 0): the rig and the eye lean with the hull
            _rig.HideModel(v.Node, spec.Hide);
            spec.OnModel?.Invoke(_rig, v.Node);
            _rigCar = v;
        }
        if (_rig != null)
        {
            var input = _ctx.Me.Sample();
            var lift = ((input.Buttons & MoveButtons.Jump) != 0 ? 1 : 0) - ((input.Buttons & MoveButtons.Crouch) != 0 ? 1 : 0);
            _rig.Pose(input.MoveX, input.MoveY, lift, dt);
        }
        var flies = _flight.DefOf(v) != null;
        // a helicopter's view tilts with the airframe; with reduce motion it stays level and the rig tilts round it (a ground vehicle's is always level)
        var frame = flies && !GameSettings.Current.ReduceMotion ? v.Node.GlobalBasis : Basis.FromEuler(new Vector3(0, rot.Y, 0));
        var e = SeatEye.For(def, seat);
        _ctx.Me.Cockpit(p, GlassClient.HostOf(v.Node).GlobalTransform * new Vector3(e.X, e.Y, e.Z), frame, rot.Y, lookTurnsWithVehicle: !flies);
        ShowSelf(v, seat, viewerHides: true); // the own figure is not drawn for the viewer (the rig has the hands), its shadow stays
        return true;
    }

    /// <summary>The cockpit view is not on this frame: the rig goes and the model is drawn whole again.</summary>
    public void EndCockpit()
    {
        if (_rig == null) return;
        if (GodotObject.IsInstanceValid(_rig)) { _rig.RestoreModel(); _rig.QueueFree(); }
        _rig = null; _rigCar = null;
    }

    /// <summary>The driver's prompt with the view key after it: what the key switches to. Rebuilt when the prompt or the view changes, and once a second in case the key was rebound.</summary>
    public string WithViewHint(string prompt)
    {
        if (prompt == _viewHintBase && _viewHintCockpit == (_view.Current == RiderView.Cockpit) && _ctx.Now - _viewHintAt < 1.0) return _viewHintText;
        _viewHintBase = prompt; _viewHintCockpit = _view.Current == RiderView.Cockpit; _viewHintAt = _ctx.Now;
        return _viewHintText = prompt + "     " + Strings.F(_view.Current == RiderView.Cockpit ? "hud.vehicle.view.chase" : "hud.vehicle.view.cockpit", InputBindings.Name("vehicle_view"));
    }

    /// <summary>
    /// A rider of a sight seat (an armed seat, a gunner's the flak and the door gun do not use this, the Scud operator's: <see cref="RiderView.Sight"/>) sees from their seat: the camera sits at the
    /// seat's position on the moving vehicle and the mouse look is their own, as in first person. A chase or turret camera from the seat before ends here.
    /// The other seats' first-person view is <see cref="CockpitFrame"/>.
    /// </summary>
    public void RideSeat(CarView v, int seat)
    {
        if (_ctx.Vehicles == null || _ctx.Me == null) return;
        var seats = _ctx.Vehicles.Vehicles[v.Kind].Seats;
        if (seat < 0 || seat >= seats.Count || seats[seat].Offset.Length != 3) return;
        var o = seats[seat].Offset;
        _ctx.Me.EndChase(_ctx.Me.Yaw); // no-op unless a chase or turret camera was on
        var feet = v.SeatAnchor.GlobalTransform * new Vector3(o[0], o[1], o[2]); // on the body as it is drawn: the seat leans and bobs with it
        // a seat in the open is drawn and hit seated (or standing in the ring): the eye is that figure's, not a standing body's on the seat
        // (a covered seat that sets eye_height has the eye at that height over its feet: the Humvee's roof is under a standing eye)
        if (seats[seat].Exposed) _ctx.Me.SeatView(feet, SeatAimRules.EyeHeight(seats[seat]));
        else if (seats[seat].EyeHeight is { } eye) _ctx.Me.SeatView(feet, eye);
        else _ctx.Me.GlobalPosition = feet;
        if (seats[seat].Armed)
        {
            // the look is the aim, and it is held inside the seat's arc, which turns and tilts with the vehicle: the view cannot go where the shots cannot
            var rot = v.Node.Rotation;
            var aim = SeatAimRules.Aim(seats[seat].Aim, rot.Y, rot.X, rot.Z, _ctx.Me.Yaw, _ctx.Me.Pitch);
            _ctx.Me.Yaw = aim.ViewYaw; _ctx.Me.Pitch = aim.ViewPitch;
        }
        else _ctx.Me.Pitch = RiderPose.ClampPitch(_ctx.Me.Pitch); // a head only looks so far up and down: the view stops where the figure's neck does
        HideSelf();
    }

    /// <summary>
    /// The own character seated at <paramref name="seat"/> as a body view, a child of the vehicle's body so it moves with it. The chase camera sees it. In the
    /// cockpit view (<paramref name="viewerHides"/>) it is not drawn for the camera but still casts its shadow, so the rider is still on the ground in the sun.
    /// </summary>
    public void ShowSelf(CarView car, int seat, bool viewerHides)
    {
        if (_ctx.Vehicles == null) return;
        var seats = _ctx.Vehicles.Vehicles[car.Kind].Seats;
        var o = seat < seats.Count && seats[seat].Offset.Length == 3 ? seats[seat].Offset : new float[3];
        var anchor = car.SeatAnchor; // the body of a driven vehicle: the figure leans with it
        if (SelfView == null || SelfView.GetParent() != anchor || _selfViewerHides != viewerHides)
        {
            SelfView?.QueueFree();
            SelfView = CharacterView.Make();
            _dressSelf(SelfView);
            SelfView.SetStance(Stance.Crouch); // seated: there is no sitting animation, the crouch squash is the closest pose
            anchor.AddChild(SelfView);
            _selfViewerHides = viewerHides;
            if (viewerHides) ShadowsOnly(SelfView);
        }
        SelfView.Position = new Vector3(o[0], o[1], o[2]);
    }

    /// <summary>Every mesh under <paramref name="n"/> that casts a shadow keeps only that: the camera does not draw it.</summary>
    static void ShadowsOnly(Node n)
    {
        if (n is GeometryInstance3D g && g.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) g.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
        foreach (var c in n.GetChildren()) ShadowsOnly(c);
    }

    public void HideSelf()
    {
        if (SelfView == null) return;
        SelfView.QueueFree();
        SelfView = null;
    }
}
