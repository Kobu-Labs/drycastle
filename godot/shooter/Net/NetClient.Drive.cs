using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The amphibious transport on the client (ADR 0010), as the utility helicopter is in NetClient.Flight.cs. Every client draws it from the drive
/// record of the snapshot (<see cref="DriveRecord"/>: its speeds, the slope it stands on, the steering, whether it floats), interpolated like the
/// vehicle. The driver's own client does not wait for that: it steps the same <see cref="DriveModel"/> with every input it sends
/// (<see cref="DrivePrediction"/>, against the same colliders through a <see cref="DriveProbe"/>), and when a snapshot acknowledges an input it
/// compares and, if the server disagrees, takes the server's state and replays the inputs still on their way. Between ticks the driver's vehicle is
/// drawn by stepping a copy of the state the fraction of a tick the frame is ahead, with the keys as they are this frame, so it is smooth at any
/// refresh rate and answers the hand at once. The weight of the body (squat, dive, lean, the bob afloat) is drawn on top by <see cref="DriveBody"/>.
/// </summary>
public partial class NetClient
{
    /// <summary>The newest drive record of a driven vehicle, its interpolation buffers, and what is drawn of it.</summary>
    sealed class DriveView
    {
        /// <summary>Position = the speeds (sideways, vertical, forward), yaw = the roll, pitch = the pitch.</summary>
        public readonly Interpolator Buf = new();
        /// <summary>Position = (steering, throttle, 0).</summary>
        public readonly Interpolator Keys = new();
        public EntityRecord Rec;
        /// <summary>The state drawn last frame (the driver's prediction, or the snapshots at the render tick), and the body's springs.</summary>
        public DriveState Shown;
        public DriveBodyState Body;
        public DriveBodyPose Pose;
        public float YawSeen;
        public bool Drawn;
    }

    /// <summary>The vehicle this client drives: its prediction, the probe it drives through, and what a correction left to fade out.</summary>
    sealed class Driving
    {
        public ushort CarId;
        public VehicleDef Def = null!;
        public DriveProbe Probe = null!;
        public DrivePrediction Predict = null!;
        /// <summary>What a correction moved the picture by, faded out over a few frames so the vehicle does not jump.</summary>
        public Vector3 Smooth;
    }

    readonly Dictionary<ushort, DriveView> _drives = new();
    Driving? _drive;
    int _driveCorrections;

    void OnDriveState(EntityRecord rec, uint tick)
    {
        var carId = DriveRecord.CarOf(rec.Id);
        if (!_drives.TryGetValue(carId, out var d)) _drives[carId] = d = new DriveView();
        d.Rec = rec;
        d.Buf.Push(tick, rec.Position, DriveRecord.Roll(rec), DriveRecord.Pitch(rec));
        d.Keys.Push(tick, new NVec3(DriveRecord.Steer(rec), DriveRecord.Throttle(rec), 0), 0, 0);
    }

    VehicleDef? DriveDef(CarView v) => Vehicles != null && v.Kind < Vehicles.Vehicles.Count && Vehicles.Vehicles[v.Kind].Drive != null ? Vehicles.Vehicles[v.Kind] : null;

    /// <summary>
    /// After every snapshot: is the local player the driver of a predicted vehicle? Starting, the prediction takes the server's state as of the
    /// acknowledged input; driving, it is reconciled against it; leaving the seat (or the vehicle being gone) ends it, and the vehicle is drawn from
    /// the snapshots like everyone else's.
    /// </summary>
    void UpdateDriving(ushort ack)
    {
        CarView? mine = null;
        if (_driving && !Dead)
            foreach (var v in _cars.Values)
                if (v.Driver == _myId && !v.Destroyed && DriveDef(v) != null) { mine = v; break; }
        if (mine == null || !_drives.TryGetValue(mine.Id, out var dv) || !_dec.States.TryGetValue(mine.Id, out var vehicle)) { StopDriving(); return; }
        var server = DriveRecord.State(vehicle, dv.Rec);
        if (_drive == null || _drive.CarId != mine.Id)
        {
            StopDriving();
            var def = DriveDef(mine)!;
            var probe = DriveProbe.Make(def, Water);
            Owned.AddChild(probe);
            if (_me != null) probe.Ignore(_me); // the body rides at the vehicle's origin: it is not an obstacle
            _drive = new Driving { CarId = mine.Id, Def = def, Probe = probe, Predict = new DrivePrediction(def, probe, server) };
            // what was drawn until now is the interpolated past: fade from there to the predicted present
            _drive.Smooth = mine.Node.GlobalPosition - new Vector3(server.Position.X, server.Position.Y, server.Position.Z);
            return;
        }
        var before = _drive.Predict.State.Position;
        if (!_drive.Predict.Reconcile(ack, server, 1f / Wire.TickRate)) return;
        _driveCorrections++;
        var moved = before - _drive.Predict.State.Position;
        _drive.Smooth += new Vector3(moved.X, moved.Y, moved.Z);
    }

    void StopDriving()
    {
        if (_drive == null) return;
        if (GodotObject.IsInstanceValid(_drive.Probe)) _drive.Probe.QueueFree();
        _drive = null;
    }

    /// <summary>The driver's controls of one input, as the server will read them: hands off while a killstreak has the keys.</summary>
    DriveInput DriverInput(in NetInput ni) => DriveInput.FromNet(ni, handsOff: _controlling || _gunship != null);

    /// <summary>On the input tick: the prediction steps with the input that was just sent.</summary>
    void PredictDrive(in NetInput ni, float dt)
    {
        if (_drive == null || Dead) return;
        _drive.Predict.Step(ni.Seq, DriverInput(ni), dt);
    }

    /// <summary>
    /// Where a driven vehicle is drawn this frame, and how its body lies. The one the local player drives: the predicted state, stepped on by the
    /// part of a tick this frame is past the last one, plus what is left of the last correction. Any other: the snapshots at the render tick. Then
    /// the body's weight on top (<see cref="DriveBody"/>): the whole vehicle takes the slope and the water's motion, the hull leans over its wheels.
    /// </summary>
    void PoseDrive(CarView v, VehicleDef def, double rt, float dt, ref Vector3 p, ref Vector3 rot)
    {
        if (!_drives.TryGetValue(v.Id, out var view)) _drives[v.Id] = view = new DriveView();
        DriveState s;
        if (_drive != null && _drive.CarId == v.Id && _me != null)
        {
            s = _drive.Predict.State;
            var ahead = (float)Engine.GetPhysicsInterpolationFraction();
            if (ahead > 0.001f && !Dead)
            {
                var now = NetInput.From(0, _me.Sample(), 0, 0);
                DriveModel.Step(ref s, _drive.Def, DriverInput(now), ahead / Wire.TickRate, _drive.Probe);
            }
            _drive.Smooth *= Mathf.Exp(-CorrectionFade * dt);
            if (_drive.Smooth.LengthSquared() > 400f) _drive.Smooth = Vector3.Zero; // a teleport (a return to its origin), not a correction: do not glide there
            p = new Vector3(s.Position.X, s.Position.Y, s.Position.Z) + _drive.Smooth;
        }
        else
        {
            // the snapshots: the vehicle record's place and heading (already in p and rot), the drive record's speeds and slope
            s = new DriveState { Position = new NVec3(p.X, p.Y, p.Z), Yaw = rot.Y, Afloat = DriveRecord.Afloat(view.Rec), Airborne = DriveRecord.Airborne(view.Rec) };
            if (view.Buf.At(rt) is { } a) { s.Slip = a.Pos.X; s.VerticalSpeed = a.Pos.Y; s.Speed = a.Pos.Z; s.Roll = a.Yaw; s.Pitch = a.Pitch; }
            if (view.Keys.At(rt) is { } k) { s.Steer = k.Pos.X; s.Throttle = k.Pos.Y; }
            // the yaw rate is not on the wire: it is how fast the drawn heading turns
            var turned = view.Drawn && dt > 1e-5f ? FlightMath.Wrap(rot.Y - view.YawSeen) / dt : 0f;
            s.YawRate = view.Shown.YawRate + (Mathf.Clamp(turned, -4f, 4f) - view.Shown.YawRate) * (1f - Mathf.Exp(-dt / 0.1f));
        }
        view.YawSeen = rot.Y; view.Drawn = true;
        view.Shown = s;
        view.Pose = DriveBody.Step(ref view.Body, def, new DriveBodyInput(s.Speed, s.YawRate, s.Throttle, s.Afloat, s.Pitch, s.Roll), dt);
        p.Y += view.Pose.Lift;
        rot = new Vector3(view.Pose.Pitch, s.Yaw, view.Pose.Roll);
    }

    /// <summary>The driver's camera and the body's place for this frame.</summary>
    void ChaseDrive(CarView v, VehicleDef def, Vector3 p, Vector3 rot, float dt)
    {
        var view = _drives[v.Id];
        _me!.DriveChase(p, rot.Y, rot.X + view.Pose.BodyPitch, rot.Z + view.Pose.BodyRoll, MathF.Abs(view.Shown.Speed) / def.TopSpeed, def.Drive!.Camera, dt);
    }

    string _drivePrompt = "";
    double _drivePromptAt = -10;
    int _drivePromptKind = -1;

    /// <summary>
    /// The prompt line of a rider of a driven vehicle who has neither a gun nor their weapons: the driver's keys as they are bound (the handbrake
    /// only on its wheels: afloat there is none), or just the way out. Rebuilt when the situation changes, and once a second in case a key was rebound.
    /// </summary>
    string DrivePrompt(CarView v, int seat)
    {
        var kind = seat == 0 && DriveOf(v) is { Afloat: false } ? 0 : 1;
        if (kind == _drivePromptKind && Now - _drivePromptAt < 1.0) return _drivePrompt;
        _drivePromptKind = kind; _drivePromptAt = Now;
        return _drivePrompt = kind == 0 ? Strings.F("hud.transport.drive", InputBindings.Name("interact"), InputBindings.Name("jump")) : Strings.F("hud.transport.afloat", InputBindings.Name("interact"));
    }

    /// <summary>The drive state behind the HUD and the effects of <paramref name="v"/> as it was drawn this frame, or null before the first.</summary>
    DriveState? DriveOf(CarView v) => _drives.TryGetValue(v.Id, out var d) && d.Drawn ? d.Shown : null;
}
