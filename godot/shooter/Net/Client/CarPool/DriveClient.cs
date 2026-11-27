using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.OwnPlayer;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Waters;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.CarPool;

/// <summary>
/// The amphibious transport on the client (ADR 0010), as the utility helicopter is in <see cref="FlightClient"/>. Every client draws it from the drive
/// record of the snapshot (<see cref="DriveRecord"/>: its speeds, the slope it stands on, the steering, whether it floats), interpolated like the
/// vehicle. The driver's own client does not wait for that: it steps the same <see cref="DriveModel"/> with every input it sends
/// (<see cref="DrivePrediction"/>, against the same colliders through a <see cref="DriveProbe"/>), and when a snapshot acknowledges an input it
/// compares and, if the server disagrees, takes the server's state and replays the inputs still on their way. Between ticks the driver's vehicle is
/// drawn by stepping a copy of the state the fraction of a tick the frame is ahead, with the keys as they are this frame, so it is smooth at any
/// refresh rate and answers the hand at once. The weight of the body (squat, dive, lean, the bob afloat) is drawn on top by <see cref="DriveBody"/>.
/// </summary>
internal sealed class DriveClient
{
    readonly ClientContext _ctx;
    readonly Func<ushort, bool> _carThrown;
    readonly Action<CarView> _placeSolid;
    readonly Func<ushort, bool> _immobilised;

    /// <param name="carThrown">The Bigfoot holds or threw this vehicle: the server flies it and ignores the driver's input.</param>
    /// <param name="placeSolid">Puts a vehicle's solid box where its records say.</param>
    /// <param name="immobilised">A Scud truck's rack moves: the brakes are on, as the server steps it.</param>
    public DriveClient(ClientContext ctx, Func<ushort, bool> carThrown, Action<CarView> placeSolid, Func<ushort, bool> immobilised)
    {
        _ctx = ctx; _carThrown = carThrown; _placeSolid = placeSolid; _immobilised = immobilised;
    }

    /// <summary>A vehicle left the world.</summary>
    public void Remove(ushort carId) => _drives.Remove(carId);

    /// <summary>Leaving: the records and the prediction go (the probes are under the owned node and go with them).</summary>
    public void Clear() { _drives.Clear(); _drive = null; }

    /// <summary>The pitch and roll the latest drive record gives a driven vehicle's hull (its slope, not the springs' lean), or null when it has none.</summary>
    public (float Pitch, float Roll)? RecordTilt(ushort carId) =>
        _drives.TryGetValue(carId, out var d) ? (DriveRecord.Pitch(d.Rec), DriveRecord.Roll(d.Rec)) : null;

    /// <summary>How the body of a driven vehicle lies on its wheels as drawn last frame (the lean the guns and riders turn with), or null.</summary>
    public (float Pitch, float Roll)? BodyLean(ushort carId) =>
        _drives.TryGetValue(carId, out var d) ? (d.Pose.BodyPitch, d.Pose.BodyRoll) : null;

    /// <summary>The vehicle the local player drives, with its prediction's state; null when none is predicted.</summary>
    public (DriveState State, VehicleDef Def)? Predicted => _drive == null ? null : (_drive.Predict.State, _drive.Def);

    /// <summary>The articulation of the driver's own predicted trailer, as drawn this frame; null when <paramref name="tractorId"/> is not that vehicle or pulls none.</summary>
    public float? PredictedPhi(ushort tractorId) => _drive != null && _drive.CarId == tractorId && _drive.Predict.HasTrailer ? _drive.DrawnPhi : null;


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
        /// <summary>What a correction turned the picture by (yaw x, pitch y, roll z; radians), faded out the same way (I-rubber-banding).</summary>
        public Vector3 AngleFix;
        /// <summary>The trailer this client predicts with the vehicle (the one hitched to it; 0: none), its world view, and the articulation drawn this frame (the prediction stepped on by the part of a tick the frame is ahead).</summary>
        public ushort TrailerId;
        public TrailerProbe? TrailerWorld;
        public float DrawnPhi;
    }

    readonly Dictionary<ushort, DriveView> _drives = new();
    Driving? _drive;
    /// <summary>How many times the prediction took the server's state (the car loop's bot reads it).</summary>
    public int Corrections { get; private set; }
    /// <summary>
    /// The newest snapshot's acknowledgement and server state, waiting for the next physics tick (C-02): a reconcile replays the unacknowledged
    /// inputs with the steps the server runs, so it runs where they run. A newer snapshot replaces an older one that has not been applied yet.
    /// </summary>
    (ushort Ack, DriveState Server, float Phi)? _driveReconcile;

    public void OnState(EntityRecord rec, uint tick)
    {
        var carId = DriveRecord.CarOf(rec.Id);
        if (!_drives.TryGetValue(carId, out var d)) _drives[carId] = d = new DriveView();
        d.Rec = rec;
        d.Buf.Push(tick, rec.Position, DriveRecord.Roll(rec), DriveRecord.Pitch(rec));
        d.Keys.Push(tick, new NVec3(DriveRecord.Steer(rec), DriveRecord.Throttle(rec), 0), 0, 0);
        if (_ctx.Cars.TryGet(carId, out var car)) _placeSolid(car); // the hull's solid box lies on the slope the record says
    }

    public VehicleDef? DefOf(CarView v) => _ctx.Vehicles != null && v.Kind < _ctx.Vehicles.Vehicles.Count && _ctx.Vehicles.Vehicles[v.Kind].Drive != null ? _ctx.Vehicles.Vehicles[v.Kind] : null;

    /// <summary>
    /// After every snapshot: is the local player the driver of a predicted vehicle? Starting, the prediction takes the server's state as of the
    /// acknowledged input; driving, it is reconciled against it; leaving the seat (or the vehicle being gone) ends it, and the vehicle is drawn from
    /// the snapshots like everyone else's.
    /// </summary>
    public void Update(ushort ack)
    {
        CarView? mine = null;
        if (_ctx.Driving && !_ctx.Dead)
            foreach (var v in _ctx.Cars.All)
                if (v.Driver == _ctx.MyId && !v.Destroyed && DefOf(v) != null) { mine = v; break; }
        if (mine == null || !_drives.TryGetValue(mine.Id, out var dv) || !_ctx.Snapshots.States.TryGetValue(mine.Id, out var vehicle)) { Stop(); return; }
        if (_carThrown(mine.Id)) { Stop(); return; } // the Bigfoot threw it: the server flies it and ignores the driver's input, so it is drawn from the snapshots until it lands (#264)
        var server = DriveRecord.State(vehicle, dv.Rec);
        if (_drive == null || _drive.CarId != mine.Id)
        {
            Stop();
            var def = DefOf(mine)!;
            var probe = DriveProbe.Make(def, _ctx.Water);
            _ctx.Owned.AddChild(probe);
            _drive = new Driving { CarId = mine.Id, Def = def, Probe = probe, Predict = new DrivePrediction(def, probe, server) };
            // what was drawn until now is the interpolated past: fade from there to the predicted present
            _drive.Smooth = mine.Node.GlobalPosition - new Vector3(server.Position.X, server.Position.Y, server.Position.Z);
            SyncPredictedTrailer(mine, server, vehicle);
            return;
        }
        var phi = SyncPredictedTrailer(mine, server, vehicle);
        _driveReconcile = (ack, server, phi); // applied in the next physics tick (ReplayReconcile)
    }

    /// <summary>
    /// Keeps the driver's prediction in step with what the server says is on the hook (towing, ADR 0014): a trailer hitched to this vehicle is attached to the prediction (a copy of its body from the records'
    /// articulation, stepped after the vehicle with <see cref="TowModel"/>, against the world through its own probe), and one that is let go is detached. Returns the articulation the server says (the trailer's record
    /// yaw less the vehicle's), 0 with none.
    /// </summary>
    float SyncPredictedTrailer(CarView mine, DriveState server, EntityRecord vehicle)
    {
        var trailer = _ctx.Cars.TrailerOf(mine.Id);
        if (trailer == null || _ctx.Vehicles == null || _ctx.Vehicles.Vehicles[mine.Kind].Tow == null || !_ctx.Snapshots.States.TryGetValue(trailer.Id, out var rec))
        {
            if (_drive!.TrailerId != 0 || _drive.Predict.HasTrailer) { _drive.Predict.DetachTrailer(); _drive.TrailerId = 0; _drive.TrailerWorld?.QueueFree(); _drive.TrailerWorld = null; }
            return 0f;
        }
        var phi = FlightMath.Wrap(rec.YawRad - vehicle.YawRad);
        if (_drive!.TrailerId != trailer.Id || !_drive.Predict.HasTrailer)
        {
            _drive.TrailerWorld?.QueueFree();
            var tdef = _ctx.Vehicles.Vehicles[trailer.Kind];
            var probe = TrailerProbe.Make(tdef);
            _ctx.Owned.AddChild(probe);
            var g = _ctx.Cars.GeometryOf(mine.Kind, trailer.Kind);
            _drive.TrailerWorld = probe; _drive.TrailerId = trailer.Id;
            var body = TowModel.Pose(g, server.Position, server.Yaw, phi);
            body.Position.Y = rec.Position.Y; // the trailer's own ground (the server's), not the hitch's: the step searches the ground from it (#377)
            _drive.Predict.AttachTrailer(tdef, g, body, probe);
        }
        _drive.Predict.TrailerLoaded = trailer.Trailer?.Loaded ?? true; // a cart emptied under way weighs less from the next step on, as the server's does
        return phi;
    }

    /// <summary>
    /// The reconcile of the latest snapshot, run in the physics tick with the inputs' own steps (C-02, as the on-foot replay in ReplayReconcile). A
    /// correction moves the picture by the difference, faded out over a few frames.
    /// </summary>
    public void ReplayReconcile()
    {
        if (_driveReconcile is not { } r) return;
        _driveReconcile = null;
        if (_drive == null) return;
        var before = _drive.Predict.State.Position;
        var yawBefore = _drive.Predict.State.Yaw;
        var attBefore = new Vector3(_drive.Predict.State.Yaw, _drive.Predict.State.Pitch, _drive.Predict.State.Roll);
        if (!_drive.Predict.Reconcile(r.Ack, r.Server, 1f / Wire.TickRate, r.Phi)) return;
        Corrections++;
        _ctx.Reconciles++;
        var moved = before - _drive.Predict.State.Position;
        _ctx.Rub.AddVehicle(moved.Length(), FlightMath.Wrap(_drive.Predict.State.Yaw - yawBefore));
        _drive.Smooth += new Vector3(moved.X, moved.Y, moved.Z);
        _drive.AngleFix += Correction.AngleDelta(attBefore, _drive.Predict.State.Yaw, _drive.Predict.State.Pitch, _drive.Predict.State.Roll);
    }

    void Stop()
    {
        _driveReconcile = null;
        if (_drive == null) return;
        if (GodotObject.IsInstanceValid(_drive.Probe)) _drive.Probe.QueueFree();
        if (_drive.TrailerWorld != null && GodotObject.IsInstanceValid(_drive.TrailerWorld)) _drive.TrailerWorld.QueueFree();
        _drive = null;
    }

    /// <summary>The driver's controls of one input, as the server will read them: hands off while a killstreak has the keys.</summary>
    DriveInput DriverInput(in NetInput ni) =>
        _drive != null && _immobilised(_drive.CarId) ? DriveInput.Held // a Scud truck while its rack moves: the brakes are on, as the server steps it
        : DriveInput.FromNet(ni, handsOff: _ctx.View.Mode.RemoteViewOwnsScreen);

    /// <summary>On the input tick: the prediction steps with the input that was just sent.</summary>
    public void Predict(in NetInput ni, float dt)
    {
        if (_drive == null || _ctx.Dead) return;
        var input = DriverInput(ni);
        if (input.Throttle != _latKeys.Throttle || input.Steer != _latKeys.Steer || input.Handbrake != _latKeys.Handbrake)
        {
            _latKeys = input; _latSeq = ni.Seq; _latAt = _ctx.Now; // the first input with new keys: its drawn change is timed below
        }
        _drive.Probe.Houses = Drycastle.Shooter.Buildings.Destruction.HouseWorldNode.Current; // the houses it cuts its way through, guessed ahead of the server
        _drive.Probe.CarId = _drive.CarId;
        _drive.Predict.Step(ni.Seq, input, dt);
    }

    /// <summary>Input-to-visible latency of the predicted car (a log line, not on the wire): the input seq whose keys changed, and when it was sent.</summary>
    DriveInput _latKeys;
    int _latSeq = -1;
    double _latAt;
    float _latSpeed, _latYaw;

    /// <summary>
    /// Where a driven vehicle is drawn this frame, and how its body lies. The one the local player drives: the predicted state, stepped on by the
    /// part of a tick this frame is past the last one, plus what is left of the last correction. Any other: the snapshots at the render tick. Then
    /// the body's weight on top (<see cref="DriveBody"/>): the whole vehicle takes the slope and the water's motion, the hull leans over its wheels.
    /// </summary>
    public void Pose(CarView v, VehicleDef def, double rt, float dt, ref Vector3 p, ref Vector3 rot)
    {
        if (!_drives.TryGetValue(v.Id, out var view)) _drives[v.Id] = view = new DriveView();
        DriveState s;
        if (_drive != null && _drive.CarId == v.Id && _ctx.Me != null)
        {
            s = _drive.Predict.State;
            _drive.DrawnPhi = _drive.Predict.TrailerBody.Phi;
            var ahead = (float)Engine.GetPhysicsInterpolationFraction();
            if (ahead > 0.001f && !_ctx.Dead)
            {
                var now = NetInput.From(0, _ctx.Me.Sample(), 0, 0);
                var stepped = _drive.Predict.Ahead(DriverInput(now), ahead / Wire.TickRate); // the vehicle and, with a trailer on the hook, the trailer after it
                s = stepped.State; _drive.DrawnPhi = stepped.Trailer.Phi;
            }
            if (_latSeq >= 0 && (MathF.Abs(s.Speed - _latSpeed) > 1e-3f || MathF.Abs(FlightMath.Wrap(s.Yaw - _latYaw)) > 1e-4f))
            {
                GD.Print($"client {_ctx.PlayerName}: drive input seq {_latSeq} to first rendered change {(_ctx.Now - _latAt) * 1000:F1} ms");
                _latSeq = -1;
            }
            _latSpeed = s.Speed; _latYaw = s.Yaw;
            _drive.Smooth *= Mathf.Exp(-Correction.FadePerSecond * dt);
            if (_drive.Smooth.LengthSquared() > 400f) _drive.Smooth = Vector3.Zero; // a teleport (a return to its origin), not a correction: do not glide there
            p = new Vector3(s.Position.X, s.Position.Y, s.Position.Z) + _drive.Smooth;
            ApplyAngleFix(ref s, ref _drive.AngleFix, dt);
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

    /// <summary>Fades the turn of the last correction and adds what is left to the drawn attitude (the predictor's own state is not changed).</summary>
    static void ApplyAngleFix(ref DriveState s, ref Vector3 fix, float dt)
    {
        fix *= Mathf.Exp(-Correction.FadePerSecond * dt);
        if (fix.LengthSquared() < 1e-8f) fix = Vector3.Zero;
        s.Yaw += fix.X; s.Pitch += fix.Y; s.Roll += fix.Z;
    }

    /// <summary>The driver's camera and the body's place for this frame.</summary>
    public void Chase(CarView v, VehicleDef def, Vector3 p, Vector3 rot, float dt)
    {
        var view = _drives[v.Id];
        _ctx.Me!.DriveChase(p, rot.Y, rot.X + view.Pose.BodyPitch, rot.Z + view.Pose.BodyRoll, MathF.Abs(view.Shown.Speed) / def.TopSpeed, def.Drive!.Camera, dt);
    }

    string _drivePrompt = "";
    double _drivePromptAt = -10;
    int _drivePromptKind = -1;

    /// <summary>
    /// The prompt line of a rider of a driven vehicle who has neither a gun nor their weapons: the driver's keys as they are bound (the handbrake
    /// only on its wheels: afloat there is none), or just the way out. Rebuilt when the situation changes, and once a second in case a key was rebound.
    /// </summary>
    public string Prompt(CarView v, int seat)
    {
        var kind = seat == 0 && StateOf(v) is { Afloat: false } ? 0 : 1;
        if (kind == _drivePromptKind && _ctx.Now - _drivePromptAt < 1.0) return _drivePrompt;
        _drivePromptKind = kind; _drivePromptAt = _ctx.Now;
        return _drivePrompt = kind == 0 ? Strings.F("hud.transport.drive", InputBindings.Name("interact"), InputBindings.Name("jump")) : Strings.F("hud.transport.afloat", InputBindings.Name("interact"));
    }

    /// <summary>The drive state behind the HUD and the effects of <paramref name="v"/> as it was drawn this frame, or null before the first.</summary>
    public DriveState? StateOf(CarView v) => _drives.TryGetValue(v.Id, out var d) && d.Drawn ? d.Shown : null;

    /// <summary>The frame's body, wheels, propeller and water effects of one driven vehicle, as it was posed by <see cref="Pose"/>.</summary>
    public void StepFx(CarView v, VehicleDef def, Vector3 p, float dt)
    {
        if (v.Drive == null || !_drives.TryGetValue(v.Id, out var view) || !view.Drawn) return;
        var crossed = v.Drive.Step(def, view.Shown, view.Pose, Sfx.ListenerDistance(p), dt);
        if (crossed == 0) return;
        // going in: a big splash at the bow; coming out: a smaller one where the stern leaves the water
        var at = crossed > 0 ? v.Drive.Bow : v.Drive.Stern;
        var surface = _ctx.Water.SurfaceY(at.X, at.Z);
        if (float.IsNaN(surface)) surface = _ctx.Water.SurfaceY(p.X, p.Z);
        if (float.IsNaN(surface)) surface = p.Y + def.Drive!.Water.Draft;
        WaterFx.Splash(_ctx.Owned, new Vector3(at.X, surface, at.Z), crossed > 0 ? 2.6f : 1.3f);
    }
}
