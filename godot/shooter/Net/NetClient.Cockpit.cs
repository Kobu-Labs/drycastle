using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The cockpit view (prototype, ticket #219): the driver of a vehicle that has a cockpit rig (<see cref="CockpitRig"/>: the bus, the flak
/// truck, the utility helicopter) switches between the chase camera and a view from the driver's seat with the vehicle view key. The view
/// is this client's own: nothing is sent, and nobody else sees the rig. The choice lasts for the session, so the next vehicle is entered in
/// the view the last one was left in. A killstreak's point of view takes the camera as before; the rig is put away for as long as it lasts.
/// </summary>
public partial class NetClient
{
    /// <summary>The driver's view: false the chase camera, true the cockpit (where the vehicle has one).</summary>
    bool _cockpitView;
    CockpitRig? _rig;
    CarView? _rigCar;

    /// <summary>The vehicle the local player drives, or null (on foot, in another seat).</summary>
    CarView? DrivenCar()
    {
        if (!_driving) return null;
        foreach (var v in _cars.Values)
            if (v.Driver == _myId && !v.Destroyed) return v;
        return null;
    }

    CockpitRig.Spec? CockpitOf(CarView v) => Vehicles != null && v.Kind < Vehicles.Vehicles.Count ? CockpitRig.For(Vehicles.Vehicles[v.Kind].Id) : null;

    /// <summary>The vehicle view key while driving a vehicle with a cockpit: switches the view. True when the key was used.</summary>
    bool HandleViewInput(InputEvent e)
    {
        if (!e.IsActionPressed("vehicle_view") || Dead || _gunship != null || _controlling || Input.MouseMode != Input.MouseModeEnum.Captured) return false;
        if (DrivenCar() is not { } v || CockpitOf(v) == null) return false;
        _cockpitView = !_cockpitView;
        return true;
    }

    /// <summary>
    /// The driver's cockpit view of <paramref name="v"/> for this frame: the rig is on the vehicle (built on the first frame), posed from the
    /// keys, and the camera sits at its eye point. False when the chase camera is wanted instead (the view is off, the vehicle has no
    /// cockpit, a killstreak has the camera, a headless client).
    /// </summary>
    bool CockpitFrame(CarView v, Vector3 p, Vector3 rot, float dt)
    {
        if (!_cockpitView || _me == null || !ShotFx.Enabled || _gunship != null || _controlling || CockpitOf(v) is not { } spec) return false;
        if (_rig == null || _rigCar != v || !IsInstanceValid(_rig))
        {
            EndCockpit();
            _rig = CockpitRig.Make(spec);
            v.Node.AddChild(_rig);
            _rig.HideModel(v.Node, spec.Hide);
            _rigCar = v;
        }
        var input = _me.Sample();
        var lift = ((input.Buttons & MoveButtons.Jump) != 0 ? 1 : 0) - ((input.Buttons & MoveButtons.Crouch) != 0 ? 1 : 0);
        _rig.Pose(input.MoveX, input.MoveY, lift, dt);
        var flies = FlightDef(v) != null;
        // a helicopter's view tilts with the airframe; with reduce motion it stays level and the rig tilts round it
        var frame = flies && !GameSettings.Current.ReduceMotion ? v.Node.GlobalBasis : Basis.FromEuler(new Vector3(0, rot.Y, 0));
        _me.Cockpit(p, v.Node.GlobalTransform * spec.Eye, frame, rot.Y, lookTurnsWithVehicle: !flies);
        HideSelfInCar(); // the rig has the hands; the seated body would be in the camera
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
        if (prompt == _viewHintBase && _viewHintCockpit == _cockpitView && Now - _viewHintAt < 1.0) return _viewHintText;
        _viewHintBase = prompt; _viewHintCockpit = _cockpitView; _viewHintAt = Now;
        return _viewHintText = prompt + "     " + Strings.F(_cockpitView ? "hud.vehicle.view.chase" : "hud.vehicle.view.cockpit", InputBindings.Name("vehicle_view"));
    }
}
