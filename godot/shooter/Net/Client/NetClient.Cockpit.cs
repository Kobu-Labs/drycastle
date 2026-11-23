using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The view of a seated rider (tickets #219, #339): every seat of every vehicle has a chase view and a first-person view from the seat's eye
/// (<see cref="SeatEye"/>, in the hull frame so the sway is in the view), and the vehicle view key switches between them. Seats that work a gun or a
/// screen keep the gun's own sight as their first-person view and ignore the key. The mode is a declared machine (<see cref="RiderViewMode"/>, "Rider.View"):
/// the choice is kept across seat switches and vehicles for the session. Nothing is sent and nobody else sees it. The driver's cockpit rig
/// (<see cref="CockpitRig"/>, hands and a turning wheel) is drawn on the driver's seat only, and only where the vehicle has one (hand-built, or measured from the
/// model's steering wheel or handlebars); any other seat sees the
/// model's own interior. A killstreak's point of view takes the camera as before; the rig is put away for as long as it lasts.
/// </summary>
public partial class NetClient
{
    /// <summary>The local rider's view mode, kept for the session (<see cref="RiderViewMode.Leave"/> keeps the choice).</summary>
    readonly RiderViewMode _view = new();
    ushort _viewCarId;
    int _viewSeat = -1;
    CockpitRig? _rig;
    CarView? _rigCar;

    /// <summary>The driver's rig of <paramref name="v"/>: its hand-built one, else one measured from the model's steering wheel or handlebars (<see cref="CockpitRig.For(string, Node3D, Node3D, System.Numerics.Vector3)"/>).</summary>
    CockpitRig.Spec? CockpitOf(CarView v)
    {
        if (Vehicles == null || v.Kind >= Vehicles.Vehicles.Count) return null;
        var def = Vehicles.Vehicles[v.Kind];
        return CockpitRig.For(def.Id, v.Node, GlassHost(v.Node), SeatEye.For(def, 0));
    }

    /// <summary>The seat of <paramref name="v"/> the local player sits in (the driver is the vehicle's record, the rest the seat state), or -1.</summary>
    int OwnSeatIn(CarView v) => v.Driver == _myId ? 0 : _seats is { Mine: > 0 } st && st.CarId == v.Id ? st.Mine : -1;

    /// <summary>Tells the view machine which seat of which vehicle the rider is in: boarding, or a switch within the vehicle.</summary>
    void TrackView(CarView v, int seat)
    {
        if (Vehicles == null || v.Kind >= Vehicles.Vehicles.Count) return;
        var def = Vehicles.Vehicles[v.Kind];
        if (_viewCarId != v.Id || _viewSeat < 0) _view.Board(def, seat);
        else if (_viewSeat != seat) _view.SwitchSeat(def, seat);
        _viewCarId = v.Id; _viewSeat = seat;
    }

    /// <summary>The rider is out of the vehicle: the choice stays, the vehicle and seat are forgotten.</summary>
    void LeaveView()
    {
        if (_viewSeat < 0) return;
        _view.Leave(); _viewCarId = 0; _viewSeat = -1;
    }

    /// <summary>The vehicle view key while seated: switches chase and cockpit. True when the key was used (not in a gun's or a screen's seat).</summary>
    bool HandleViewInput(InputEvent e)
    {
        if (!e.IsActionPressed("vehicle_view") || !Mode.CanToggleCockpitView || Input.MouseMode != Input.MouseModeEnum.Captured) return false;
        return _driving && !Dead && _viewSeat >= 0 && _view.Toggle();
    }

    /// <summary>
    /// The cockpit view of <paramref name="v"/> from <paramref name="seat"/> for this frame: the camera sits at the seat's eye on the hull; on the
    /// driver's seat of a vehicle with a rig the rig is on the vehicle (built on the first frame) and posed from the keys. False when the chase camera is
    /// wanted instead (the view is off, a killstreak has the camera, a headless client).
    /// </summary>
    bool CockpitFrame(CarView v, int seat, Vector3 p, Vector3 rot, float dt)
    {
        if (_view.Current != RiderView.Cockpit || _me == null || !ShotFx.Enabled || !Mode.ViewIsBody || Vehicles == null || v.Kind >= Vehicles.Vehicles.Count) return false;
        var def = Vehicles.Vehicles[v.Kind];
        var spec = seat == 0 ? CockpitOf(v) : null; // the driver's rig is the driver's: another seat sees the model's own interior
        if (spec == null) EndCockpit();
        else if (_rig == null || _rigCar != v || !IsInstanceValid(_rig))
        {
            EndCockpit();
            _rig = CockpitRig.Make(spec);
            GlassHost(v.Node).AddChild(_rig); // on the body, in the vehicle's frame (docs/standards/car-design.md 0): the rig and the eye lean with the hull
            _rig.HideModel(v.Node, spec.Hide);
            spec.OnModel?.Invoke(_rig, v.Node);
            _rigCar = v;
        }
        if (_rig != null)
        {
            var input = _me.Sample();
            var lift = ((input.Buttons & MoveButtons.Jump) != 0 ? 1 : 0) - ((input.Buttons & MoveButtons.Crouch) != 0 ? 1 : 0);
            _rig.Pose(input.MoveX, input.MoveY, lift, dt);
        }
        var flies = FlightDef(v) != null;
        // a helicopter's view tilts with the airframe; with reduce motion it stays level and the rig tilts round it (a ground vehicle's is always level)
        var frame = flies && !GameSettings.Current.ReduceMotion ? v.Node.GlobalBasis : Basis.FromEuler(new Vector3(0, rot.Y, 0));
        var e = SeatEye.For(def, seat);
        _me.Cockpit(p, GlassHost(v.Node).GlobalTransform * new Vector3(e.X, e.Y, e.Z), frame, rot.Y, lookTurnsWithVehicle: !flies);
        ShowSelfInCar(v, seat, viewerHides: true); // the own figure is not drawn for the viewer (the rig has the hands), its shadow stays
        return true;
    }

    /// <summary>The cockpit view is not on this frame: the rig goes and the model is drawn whole again.</summary>
    void EndCockpit()
    {
        if (_rig == null) return;
        if (IsInstanceValid(_rig)) { _rig.RestoreModel(); _rig.QueueFree(); }
        _rig = null; _rigCar = null;
    }

    string _viewHintBase = "", _viewHintText = "";
    bool _viewHintCockpit;
    double _viewHintAt;

    /// <summary>The driver's prompt with the view key after it: what the key switches to. Rebuilt when the prompt or the view changes, and once a second in case the key was rebound.</summary>
    string WithViewHint(string prompt)
    {
        if (prompt == _viewHintBase && _viewHintCockpit == (_view.Current == RiderView.Cockpit) && Now - _viewHintAt < 1.0) return _viewHintText;
        _viewHintBase = prompt; _viewHintCockpit = _view.Current == RiderView.Cockpit; _viewHintAt = Now;
        return _viewHintText = prompt + "     " + Strings.F(_view.Current == RiderView.Cockpit ? "hud.vehicle.view.chase" : "hud.vehicle.view.cockpit", InputBindings.Name("vehicle_view"));
    }
}
