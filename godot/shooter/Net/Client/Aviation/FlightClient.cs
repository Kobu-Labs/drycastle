using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.CarPool;
using Drycastle.Shooter.Net.Client.OwnPlayer;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Client.Aviation;

/// <summary>
/// The utility helicopter on the client (ADR 0006). Every client tilts the model from the flight record of the snapshot (<see cref="FlightRecord"/>:
/// velocity, pitch, roll, rotor), interpolated like the vehicle. The pilot's own client does not wait for that: it steps the same
/// <see cref="FlightModel"/> with every input it sends (<see cref="FlightPrediction"/>, against the same colliders through a <see cref="FlightProbe"/>),
/// and when a snapshot acknowledges an input it compares and, if the server disagrees, takes the server's state and replays the inputs still on
/// their way. The player on foot is predicted the same way; this is the first vehicle that is. Between ticks the pilot's helicopter is drawn by
/// stepping a copy of the state the fraction of a tick the frame is ahead, with the keys as they are this frame, so it is smooth at any refresh
/// rate and answers the hand at once.
/// The rotors turn at the rotor speed of the flight record (they spool up when a pilot sits down and run down on the ground without one), a little faster and louder
/// under load (climbing, tilted hard); at speed a faint disc stands in for the blur of the blades; the generated <see cref="RotorSound"/> follows the same two numbers; and
/// in a low hover the downwash throws up dust, or spray over a water body. All of it is the client's own: nothing is on the wire beyond the flight record.
/// </summary>
internal sealed class FlightClient
{
    readonly ClientContext _ctx;
    readonly Func<ushort, bool> _carThrown;
    readonly Action<CarView> _placeSolid;
    readonly Action<float> _onCorrection;

    /// <param name="carThrown">The Bigfoot holds or threw this vehicle: the server places it and ignores the pilot.</param>
    /// <param name="placeSolid">Puts a vehicle's solid box where its records say.</param>
    /// <param name="onCorrection">Told the length of each correction of the pilot's prediction (the session log).</param>
    public FlightClient(ClientContext ctx, Func<ushort, bool> carThrown, Action<CarView> placeSolid, Action<float> onCorrection)
    {
        _ctx = ctx; _carThrown = carThrown; _placeSolid = placeSolid; _onCorrection = onCorrection;
    }

    /// <summary>A vehicle left the world.</summary>
    public void Remove(ushort carId) => _flights.Remove(carId);

    /// <summary>Leaving: the records and the prediction go (the probe is under the owned node and goes with it).</summary>
    public void Clear() { _flights.Clear(); _pilot = null; }

    /// <summary>The pitch and roll the latest flight record gives a vehicle's hull, or null when it has none.</summary>
    public (float Pitch, float Roll)? RecordTilt(ushort carId) =>
        _flights.TryGetValue(carId, out var f) ? (FlightRecord.Pitch(f.Rec), FlightRecord.Roll(f.Rec)) : null;

    /// <summary>The newest flight record of a flying vehicle and its interpolation buffer (position = velocity, yaw = roll, pitch = pitch), by vehicle id.</summary>
    sealed class FlightView
    {
        public readonly Interpolator Buf = new();
        public EntityRecord Rec;
    }

    /// <summary>The helicopter this client flies: its prediction, the probe it flies through, and what was drawn last.</summary>
    sealed class Piloting
    {
        public ushort CarId;
        public VehicleDef Def = null!;
        public FlightProbe Probe = null!;
        public FlightPrediction Predict = null!;
        /// <summary>What a correction moved the picture by, faded out over a few frames so the helicopter does not jump.</summary>
        public Vector3 Smooth;
        /// <summary>What a correction turned the picture by (yaw x, pitch y, roll z; radians), faded out the same way (I-rubber-banding).</summary>
        public Vector3 AngleFix;
        public FlightState Shown;
    }

    readonly Dictionary<ushort, FlightView> _flights = new();
    Piloting? _pilot;
    FlightBounds? _mapFlightBounds;

    public void OnState(EntityRecord rec, uint tick)
    {
        var carId = FlightRecord.CarOf(rec.Id);
        if (!_flights.TryGetValue(carId, out var f)) _flights[carId] = f = new FlightView();
        f.Rec = rec;
        f.Buf.Push(tick, FlightRecord.Velocity(rec), FlightRecord.Roll(rec), FlightRecord.Pitch(rec));
        if (_ctx.Cars.TryGet(carId, out var car) && _ctx.Vehicles != null && car.Kind < _ctx.Vehicles.Vehicles.Count && _ctx.Vehicles.Vehicles[car.Kind].Tilts) _placeSolid(car); // the hull's solid box turns with the record
    }

    public VehicleDef? DefOf(CarView v) => _ctx.Vehicles != null && v.Kind < _ctx.Vehicles.Vehicles.Count && _ctx.Vehicles.Vehicles[v.Kind].Flight != null ? _ctx.Vehicles.Vehicles[v.Kind] : null;

    /// <summary>
    /// After every snapshot: is the local player the pilot of a helicopter? Starting, the prediction takes the server's state as of the acknowledged
    /// input; flying, it is reconciled against it; leaving the seat (or the helicopter being gone) ends it, and the helicopter is drawn from the
    /// snapshots like everyone else's.
    /// </summary>
    public void Update(ushort ack)
    {
        CarView? mine = null;
        if (_ctx.Driving && !_ctx.Dead)
            foreach (var v in _ctx.Cars.All)
                if (v.Driver == _ctx.MyId && !v.Destroyed && DefOf(v) != null) { mine = v; break; }
        if (mine == null || !_flights.TryGetValue(mine.Id, out var fv) || !_ctx.Snapshots.States.TryGetValue(mine.Id, out var vehicle)) { Stop(); return; }
        if (_carThrown(mine.Id)) { Stop(); return; } // in the Bigfoot's hands or thrown: the server places it and ignores the pilot, so it is drawn from the snapshots
        var server = FlightRecord.State(vehicle, fv.Rec);
        if (_pilot == null || _pilot.CarId != mine.Id)
        {
            Stop();
            var def = DefOf(mine)!;
            var probe = FlightProbe.Make(def, _ctx.Water, _mapFlightBounds ??= FlightProbe.MapBounds(_ctx.Map));
            _ctx.Owned.AddChild(probe);
            _pilot = new Piloting { CarId = mine.Id, Def = def, Probe = probe, Predict = new FlightPrediction(def, probe, server), Shown = server };
            // what was drawn until now is the interpolated past: fade from there to the predicted present
            _pilot.Smooth = mine.Node.GlobalPosition - new Vector3(server.Position.X, server.Position.Y, server.Position.Z);
            return;
        }
        var before = _pilot.Predict.State.Position;
        var yawBefore = _pilot.Predict.State.Yaw;
        var attBefore = new Vector3(_pilot.Predict.State.Yaw, _pilot.Predict.State.Pitch, _pilot.Predict.State.Roll);
        if (!_pilot.Predict.Reconcile(ack, server, 1f / Wire.TickRate)) return;
                var moved = before - _pilot.Predict.State.Position;
        _ctx.Rub.AddVehicle(moved.Length(), FlightMath.Wrap(_pilot.Predict.State.Yaw - yawBefore));
        _pilot.Smooth += new Vector3(moved.X, moved.Y, moved.Z);
        _pilot.AngleFix += Correction.AngleDelta(attBefore, _pilot.Predict.State.Yaw, _pilot.Predict.State.Pitch, _pilot.Predict.State.Roll);
        _onCorrection(new Vector3(moved.X, moved.Y, moved.Z).Length());
    }

    void Stop()
    {
        if (_pilot == null) return;
        if (GodotObject.IsInstanceValid(_pilot.Probe)) _pilot.Probe.QueueFree();
        _pilot = null;
    }

    /// <summary>The pilot's controls of one input, as the server will read them: hands off while a killstreak has the keys.</summary>
    FlightInput PilotInput(in NetInput ni) => FlightInput.FromNet(ni, handsOff: _ctx.View.Mode.RemoteViewOwnsScreen);

    /// <summary>On the input tick: the prediction steps with the input that was just sent.</summary>
    public void Predict(in NetInput ni, float dt)
    {
        if (_pilot == null || _ctx.Dead) return;
        _pilot.Predict.Step(ni.Seq, PilotInput(ni), dt);
    }

    /// <summary>
    /// Where a flying vehicle is drawn this frame. The helicopter the local player flies: the predicted state, stepped on by the part of a tick
    /// this frame is past the last one, plus what is left of the last correction. Any other: the snapshots' attitude at the render tick.
    /// </summary>
    public void Pose(CarView v, double rt, float dt, ref Vector3 p, ref Vector3 rot)
    {
        if (_pilot != null && _pilot.CarId == v.Id && _ctx.Me != null)
        {
            var s = _pilot.Predict.State;
            var ahead = (float)Engine.GetPhysicsInterpolationFraction();
            if (ahead > 0.001f && !_ctx.Dead)
            {
                var now = NetInput.From(0, _ctx.Me.Sample(), 0, 0);
                FlightModel.Step(ref s, _pilot.Def, PilotInput(now), ahead / Wire.TickRate, _pilot.Probe);
            }
            _pilot.Smooth *= Mathf.Exp(-Correction.FadePerSecond * dt);
            if (_pilot.Smooth.LengthSquared() > 400f) _pilot.Smooth = Vector3.Zero; // a teleport (a return to its origin), not a correction: do not glide there
            p = new Vector3(s.Position.X, s.Position.Y, s.Position.Z) + _pilot.Smooth;
            _pilot.AngleFix *= Mathf.Exp(-Correction.FadePerSecond * dt);
            if (_pilot.AngleFix.LengthSquared() < 1e-8f) _pilot.AngleFix = Vector3.Zero;
            s.Yaw += _pilot.AngleFix.X; s.Pitch += _pilot.AngleFix.Y; s.Roll += _pilot.AngleFix.Z; // a correction's turn, faded out (not in the predictor's state)
            _pilot.Shown = s;
            rot = new Vector3(s.Pitch, s.Yaw, s.Roll);
            return;
        }
        if (_flights.TryGetValue(v.Id, out var f) && f.Buf.At(rt) is { } a) rot = new Vector3(a.Pitch, rot.Y, a.Yaw);
    }

    /// <summary>The pilot's camera and the body's place for this frame.</summary>
    public void Chase(CarView v, Vector3 p, Vector3 rot, float dt)
    {
        var def = DefOf(v)!;
        var speed = _pilot != null && _pilot.CarId == v.Id ? _pilot.Shown.Velocity.Length() : v.Kmh / 3.6f;
        _ctx.Me!.FlightChase(p, rot.Y, rot.X, rot.Z, speed / def.TopSpeed, def.Flight!.Camera, dt);
    }

    int _readoutKmh = -1, _readoutAlt = -1, _promptKind = -1;
    string _readoutText = "", _promptText = "";
    double _promptAt;

    /// <summary>The pilot's readout in the place of a car's speed: speed and height over the ground. The text is rebuilt only when a shown number changes.</summary>
    public string Readout(in FlightState s)
    {
        var kmh = (int)MathF.Round(s.Velocity.Length() * 3.6f);
        var alt = float.IsPositiveInfinity(s.Agl) ? -2 : (int)MathF.Round(s.Agl);
        if (kmh == _readoutKmh && alt == _readoutAlt) return _readoutText;
        _readoutKmh = kmh; _readoutAlt = alt;
        return _readoutText = Strings.F("hud.flight.readout", kmh, alt < 0 ? "--" : alt);
    }

    /// <summary>
    /// The pilot's prompt line: what the keys do now (they follow the bindings), or what holds the helicopter back. Words, never colour.
    /// Rebuilt when the situation changes, and once a second in case a key was rebound.
    /// </summary>
    public string Prompt(in FlightState s)
    {
        var def = _pilot?.Def.Flight;
        var kind = s.AtEdge ? 0 : s.Grounded && def != null && s.Rotor < def.LiftoffRotor ? 1 : s.Grounded ? 2 : 3;
        if (kind == _promptKind && _ctx.Now - _promptAt < 1.0) return _promptText;
        _promptKind = kind; _promptAt = _ctx.Now;
        return _promptText = kind switch
        {
            0 => Strings.T("hud.flight.edge"),
            1 => Strings.F("hud.flight.spool", InputBindings.Name("interact")),
            2 => Strings.F("hud.flight.takeoff", InputBindings.Name("jump"), InputBindings.Name("interact")),
            _ => Strings.F("hud.flight.fly", InputBindings.Name("jump"), InputBindings.Name("crouch"), InputBindings.Name("interact")),
        };
    }

    /// <summary>The flight state behind the HUD of whoever sits in <paramref name="v"/>: the pilot's prediction, or the newest snapshot.</summary>
    public FlightState? StateOf(CarView v)
    {
        if (_pilot != null && _pilot.CarId == v.Id) return _pilot.Shown;
        return _flights.TryGetValue(v.Id, out var f) && _ctx.Snapshots.States.TryGetValue(v.Id, out var vehicle) ? FlightRecord.State(vehicle, f.Rec) : null;
    }


    Godot.Collections.Array<Rid>? _washExclude;

    /// <summary>The frame's rotor, sound and downwash of one flying vehicle, drawn at <paramref name="p"/> with attitude <paramref name="rot"/>.</summary>
    public void StepFx(CarView v, VehicleDef def, Vector3 p, Vector3 rot, float dt)
    {
        if (v.Rotor == null || StateOf(v) is not { } s) return;
        var f = def.Flight!;
        var tilt = Mathf.Max(Mathf.Abs(rot.X), Mathf.Abs(rot.Z)) / (f.MaxPitchDeg * FlightMath.Deg2Rad);
        var load = Mathf.Clamp(0.45f + 0.45f * s.Velocity.Y / f.ClimbRate + 0.3f * tilt, 0f, 1f);
        var distance = Sfx.ListenerDistance(p);
        (Vector3, float, bool)? wash = null;
        if (s.Rotor > 0.6f && distance < RotorFx.WashRange && !GameSettings.Current.ReduceMotion && _ctx.World != null)
        {
            // what is under it: the ground (or a roof), or the surface of a water body above that
            var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up, p + Vector3.Down * (RotorFx.WashHeight + 1f));
            q.CollisionMask = CollisionLayers.World;
            if (_ctx.Me != null) q.Exclude = _washExclude ??= new Godot.Collections.Array<Rid> { _ctx.Me.GetRid() };
            var hit = _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(q);
            var surface = _ctx.Water.BodyAt(p.X, p.Z)?.SurfaceY;
            float? under = hit.Count > 0 ? hit["position"].AsVector3().Y : null;
            var wet = surface is { } sy && (under == null || sy > under);
            if (wet) under = surface;
            if (under is { } y && p.Y - y < RotorFx.WashHeight)
                wash = (new Vector3(p.X, y, p.Z), s.Rotor * (1f - Mathf.Clamp((p.Y - y) / RotorFx.WashHeight, 0f, 1f)), wet);
        }
        v.Rotor.Step(dt, s.Rotor, load, distance, wash);
    }
}
