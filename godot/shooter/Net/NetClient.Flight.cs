using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The utility helicopter on the client (ADR 0006). Every client tilts the model from the flight record of the snapshot (<see cref="FlightRecord"/>:
/// velocity, pitch, roll, rotor), interpolated like the vehicle. The pilot's own client does not wait for that: it steps the same
/// <see cref="FlightModel"/> with every input it sends (<see cref="FlightPrediction"/>, against the same colliders through a <see cref="FlightProbe"/>),
/// and when a snapshot acknowledges an input it compares and, if the server disagrees, takes the server's state and replays the inputs still on
/// their way. The player on foot is predicted the same way; this is the first vehicle that is. Between ticks the pilot's helicopter is drawn by
/// stepping a copy of the state the fraction of a tick the frame is ahead, with the keys as they are this frame, so it is smooth at any refresh
/// rate and answers the hand at once.
/// </summary>
public partial class NetClient
{
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
        public FlightState Shown;
    }

    readonly Dictionary<ushort, FlightView> _flights = new();
    Piloting? _pilot;
    FlightBounds? _mapFlightBounds;
    int _flightCorrections;

    /// <summary>How fast a correction's jump is faded out of the picture, per second.</summary>
    const float CorrectionFade = 10f;

    void OnFlightState(EntityRecord rec, uint tick)
    {
        var carId = FlightRecord.CarOf(rec.Id);
        if (!_flights.TryGetValue(carId, out var f)) _flights[carId] = f = new FlightView();
        f.Rec = rec;
        f.Buf.Push(tick, FlightRecord.Velocity(rec), FlightRecord.Roll(rec), FlightRecord.Pitch(rec));
    }

    VehicleDef? FlightDef(CarView v) => Vehicles != null && v.Kind < Vehicles.Vehicles.Count && Vehicles.Vehicles[v.Kind].Flight != null ? Vehicles.Vehicles[v.Kind] : null;

    /// <summary>
    /// After every snapshot: is the local player the pilot of a helicopter? Starting, the prediction takes the server's state as of the acknowledged
    /// input; flying, it is reconciled against it; leaving the seat (or the helicopter being gone) ends it, and the helicopter is drawn from the
    /// snapshots like everyone else's.
    /// </summary>
    void UpdatePiloting(ushort ack)
    {
        CarView? mine = null;
        if (_driving && !Dead)
            foreach (var v in _cars.Values)
                if (v.Driver == _myId && !v.Destroyed && FlightDef(v) != null) { mine = v; break; }
        if (mine == null || !_flights.TryGetValue(mine.Id, out var fv) || !_dec.States.TryGetValue(mine.Id, out var vehicle)) { StopPiloting(); return; }
        var server = FlightRecord.State(vehicle, fv.Rec);
        if (_pilot == null || _pilot.CarId != mine.Id)
        {
            StopPiloting();
            var def = FlightDef(mine)!;
            var probe = FlightProbe.Make(def, Water, _mapFlightBounds ??= FlightProbe.MapBounds(Map));
            Owned.AddChild(probe);
            if (_me != null) probe.Ignore(_me); // the body rides at the helicopter's origin: it is not an obstacle
            _pilot = new Piloting { CarId = mine.Id, Def = def, Probe = probe, Predict = new FlightPrediction(def, probe, server), Shown = server };
            // what was drawn until now is the interpolated past: fade from there to the predicted present
            _pilot.Smooth = mine.Node.GlobalPosition - new Vector3(server.Position.X, server.Position.Y, server.Position.Z);
            return;
        }
        var before = _pilot.Predict.State.Position;
        if (!_pilot.Predict.Reconcile(ack, server, 1f / Wire.TickRate)) return;
        _flightCorrections++;
        var moved = before - _pilot.Predict.State.Position;
        _pilot.Smooth += new Vector3(moved.X, moved.Y, moved.Z);
        TelemetryFlightCorrection(new Vector3(moved.X, moved.Y, moved.Z).Length());
    }

    void StopPiloting()
    {
        if (_pilot == null) return;
        if (GodotObject.IsInstanceValid(_pilot.Probe)) _pilot.Probe.QueueFree();
        _pilot = null;
    }

    /// <summary>The pilot's controls of one input, as the server will read them: hands off while a killstreak has the keys.</summary>
    FlightInput PilotInput(in NetInput ni) => FlightInput.FromNet(ni, handsOff: _controlling || _gunship != null);

    /// <summary>On the input tick: the prediction steps with the input that was just sent.</summary>
    void PredictFlight(in NetInput ni, float dt)
    {
        if (_pilot == null || Dead) return;
        _pilot.Predict.Step(ni.Seq, PilotInput(ni), dt);
    }

    /// <summary>
    /// Where a flying vehicle is drawn this frame. The helicopter the local player flies: the predicted state, stepped on by the part of a tick
    /// this frame is past the last one, plus what is left of the last correction. Any other: the snapshots' attitude at the render tick.
    /// </summary>
    void PoseFlight(CarView v, double rt, float dt, ref Vector3 p, ref Vector3 rot)
    {
        if (_pilot != null && _pilot.CarId == v.Id && _me != null)
        {
            var s = _pilot.Predict.State;
            var ahead = (float)Engine.GetPhysicsInterpolationFraction();
            if (ahead > 0.001f && !Dead)
            {
                var now = NetInput.From(0, _me.Sample(), 0, 0);
                FlightModel.Step(ref s, _pilot.Def, PilotInput(now), ahead / Wire.TickRate, _pilot.Probe);
            }
            _pilot.Shown = s;
            _pilot.Smooth *= Mathf.Exp(-CorrectionFade * dt);
            if (_pilot.Smooth.LengthSquared() > 400f) _pilot.Smooth = Vector3.Zero; // a teleport (a return to its origin), not a correction: do not glide there
            p = new Vector3(s.Position.X, s.Position.Y, s.Position.Z) + _pilot.Smooth;
            rot = new Vector3(s.Pitch, s.Yaw, s.Roll);
            return;
        }
        if (_flights.TryGetValue(v.Id, out var f) && f.Buf.At(rt) is { } a) rot = new Vector3(a.Pitch, rot.Y, a.Yaw);
    }

    /// <summary>The pilot's camera and the body's place for this frame.</summary>
    void ChaseFlight(CarView v, Vector3 p, Vector3 rot, float dt)
    {
        var def = FlightDef(v)!;
        var speed = _pilot != null && _pilot.CarId == v.Id ? _pilot.Shown.Velocity.Length() : v.Kmh / 3.6f;
        _me!.FlightChase(p, rot.Y, rot.X, rot.Z, speed / def.TopSpeed, def.Flight!.Camera, dt);
    }

    int _readoutKmh = -1, _readoutAlt = -1, _promptKind = -1;
    string _readoutText = "", _promptText = "";
    double _promptAt;

    /// <summary>The pilot's readout in the place of a car's speed: speed and height over the ground. The text is rebuilt only when a shown number changes.</summary>
    string FlightReadout(in FlightState s)
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
    string FlightPrompt(in FlightState s)
    {
        var def = _pilot?.Def.Flight;
        var kind = s.AtEdge ? 0 : s.Grounded && def != null && s.Rotor < def.LiftoffRotor ? 1 : s.Grounded ? 2 : 3;
        if (kind == _promptKind && Now - _promptAt < 1.0) return _promptText;
        _promptKind = kind; _promptAt = Now;
        return _promptText = kind switch
        {
            0 => Strings.T("hud.flight.edge"),
            1 => Strings.F("hud.flight.spool", InputBindings.Name("interact")),
            2 => Strings.F("hud.flight.takeoff", InputBindings.Name("jump"), InputBindings.Name("interact")),
            _ => Strings.F("hud.flight.fly", InputBindings.Name("jump"), InputBindings.Name("crouch"), InputBindings.Name("interact")),
        };
    }

    /// <summary>The flight state behind the HUD of whoever sits in <paramref name="v"/>: the pilot's prediction, or the newest snapshot.</summary>
    FlightState? FlightOf(CarView v)
    {
        if (_pilot != null && _pilot.CarId == v.Id) return _pilot.Shown;
        return _flights.TryGetValue(v.Id, out var f) && _dec.States.TryGetValue(v.Id, out var vehicle) ? FlightRecord.State(vehicle, f.Rec) : null;
    }
}
