using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Client side of vehicles (#91): car entities are drawn interpolated about 100 ms in the past like remote players, including the
/// driver's own car (no prediction in v1; the one exception is the utility helicopter, which its pilot's client predicts, ADR 0006 and
/// NetClient.Flight.cs). While driving the body follows the car, a chase camera replaces the first-person one,
/// and the HUD shows the speed and the exit prompt; near a free car it shows the enter prompt.
/// </summary>
public partial class NetClient
{
    sealed class CarView
    {
        public Node3D Node = null!;
        public ushort Id;
        /// <summary>A flak truck's moving nodes (null for every other type, or a model without them).</summary>
        public FlakNodes? Flak;
        /// <summary>A door gun's moving nodes (the utility helicopter; null for every other type, or a model without them).</summary>
        public DoorGunNodes? DoorGun;
        /// <summary>A flying vehicle's rotors, their sound and the downwash (null for everything that drives, or a model without rotors).</summary>
        public RotorFx? Rotor;
        public readonly Interpolator Buf = new();
        public ushort Driver;
        public int Kind;
        public NVec3 Pos;
        public float Yaw, Kmh;
        public uint LastTick;
        public LoopVoice? Snd;
        /// <summary>Damage stage 0..3 from the record, whether the vehicle is destroyed, and whether its blast was already drawn (event or state, whichever came first).</summary>
        public int Stage;
        public bool Destroyed, Exploded;
        public double ExplodedAt;
        public VehicleFx.Damage? Smoke;
        /// <summary>Headlights on (the record's bit), braking (speed fell this snapshot) and the lamps (null: the type has none).</summary>
        public bool Lights, Braking;
        public CarLights? Lamps;
        /// <summary>The solid box predicted on-foot movement collides with (#217), at the last snapshot's place: the server's own box is built from the same numbers.</summary>
        public StaticBody3D? Solid;
    }

    readonly Dictionary<ushort, CarView> _cars = new();
    bool _driving;
    float _carYaw;
    /// <summary>Car smoke role: "a" drives, "b" stands in the way.</summary>
    public string CarBot = "";
    /// <summary>vehicles.json (type per snapshot record, visuals and the smoke's expected run-over damage).</summary>
    public VehiclesFile? Vehicles;

    void OnVehicleState(ushort id, EntityRecord rec, uint tick)
    {
        if (Vehicles == null) return; // never let a missing table abort the rest of the snapshot
        if (!_cars.TryGetValue(id, out var v))
        {
            v = new CarView { Id = id, Node = CarFx.Make(Owned, Vehicles!.Vehicles[Math.Min(VehicleRecord.Kind(rec), Vehicles.Vehicles.Count - 1)], VehicleRecord.Colour(rec)), Kind = Math.Min(VehicleRecord.Kind(rec), Vehicles.Vehicles.Count - 1), Pos = rec.Position, Yaw = rec.YawRad };
            if (ShotFx.Enabled && Vehicles.Vehicles[v.Kind].Flak != null) v.Flak = FlakNodes.Find(v.Node);
            if (ShotFx.Enabled && Vehicles.Vehicles[v.Kind].DoorGun is { } doorGun) v.DoorGun = DoorGunNodes.Find(v.Node, doorGun);
            if (ShotFx.Enabled && Vehicles.Vehicles[v.Kind].Flight != null) v.Rotor = RotorFx.Find(v.Node);
            _cars[id] = v;
            ShowGlassOf(id); // panes whose state arrived before the vehicle was drawn (ADR 0007)
        }
        v.Solid ??= VehicleCollider.Make(Owned, Vehicles.Vehicles[v.Kind]);
        v.Lamps ??= ShotFx.Enabled ? CarLights.Attach(v.Node, Vehicles!.Vehicles[v.Kind]) : null;
        v.Driver = VehicleRecord.Driver(rec);
        v.Lights = VehicleRecord.Lights(rec);
        SetVehicleDamage(v, VehicleRecord.Stage(rec), rec.State == EntityState.Dead);
        if (tick > v.LastTick && v.LastTick != 0)
        {
            var d = NVec3.Distance(rec.Position, v.Pos) / ((tick - v.LastTick) / (float)Wire.TickRate);
            var before = v.Kmh;
            v.Kmh += (d * 3.6f - v.Kmh) * 0.3f;
            v.Braking = before - v.Kmh > 0.5f; // the record has no brake bit: a clear fall in speed reads as braking
        }
        v.LastTick = tick; v.Pos = rec.Position; v.Yaw = rec.YawRad;
        v.Buf.Push(tick, rec.Position, rec.YawRad, 0);
        VehicleCollider.Place(v.Solid, new Vector3(rec.Position.X, rec.Position.Y, rec.Position.Z), rec.YawRad, !v.Destroyed);
    }

    /// <summary>Applies the stage and destroyed flag of a record: smoke stage emitters; a destroyed vehicle disappears (its blast is drawn here if the event has not arrived), a returned one shows again.</summary>
    void SetVehicleDamage(CarView v, int stage, bool destroyed)
    {
        v.Stage = stage;
        if (destroyed)
        {
            if (!v.Destroyed) { v.Destroyed = true; if (!v.Exploded) ExplodeVehicle(v, new Vector3(v.Pos.X, v.Pos.Y + 0.8f, v.Pos.Z), Vehicles?.Explosion.Splash.Radius ?? 9f); }
            if (v.Smoke != null) { v.Smoke.QueueFree(); v.Smoke = null; }
            return;
        }
        // the blast event is reliable and can overtake a snapshot that still shows the old damage stage: ignore those for a second, or they would bring the smoke back on a gone vehicle
        if (v.Exploded && Now - v.ExplodedAt < 1.0) return;
        // a live record after the blast means the server returned it (30 s later): it is shown again, and only then
        if (v.Destroyed) { v.Destroyed = false; v.Exploded = false; v.Node.Visible = true; }
        if (!ShotFx.Enabled) return;
        if (stage > 0 && v.Smoke == null && Vehicles != null) v.Smoke = VehicleFx.Damage.Make(v.Node, Vehicles.Vehicles[v.Kind]);
        v.Smoke?.Set(stage);
    }

    /// <summary>The blast of one vehicle: effect, sound, camera shake, and the vehicle is gone (engine loop and smoke stop).</summary>
    void ExplodeVehicle(CarView v, Vector3 at, float radius)
    {
        v.Exploded = true;
        v.Snd?.Release(); v.Snd = null;
        v.Rotor?.Stop();
        v.ExplodedAt = Now;
        if (v.Smoke != null) { v.Smoke.Stop(); v.Smoke.QueueFree(); v.Smoke = null; } // freed with its particles, not just stopped: nothing of the smoke outlives the vehicle
        v.Destroyed = true; // the wreck is gone until the server returns it: a live record must not bring it back or its smoke
        v.Node.Visible = false;
        VehicleFx.Explode(Owned, at, radius);
        if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at);
        ShakeFor(at, radius);
    }

    void OnVehicleExplosion(VehicleExplosionMsg m)
    {
        if (KeepEventLogs) _vehicleExplosionLog.Add((Now, m));
        if (_cars.TryGetValue(m.Id, out var v) && !v.Exploded) ExplodeVehicle(v, new Vector3(m.Position.X, m.Position.Y, m.Position.Z), m.Radius);
    }

    readonly List<(double T, VehicleExplosionMsg M)> _vehicleExplosionLog = new();

    readonly List<(float D, int I, CarView V)> _beamCands = new();
    readonly HashSet<CarView> _beams = new();

    /// <summary>The nearest lit vehicles that get real beams (ties keep the order of the car list). Reuses its buffers: no LINQ per frame (#173, T3-031).</summary>
    HashSet<CarView> PickBeams()
    {
        _beamCands.Clear();
        var i = 0;
        foreach (var c in _cars.Values)
        {
            if (c.Lights && !c.Destroyed && c.Lamps != null) _beamCands.Add((Sfx.ListenerDistance(new Vector3(c.Pos.X, c.Pos.Y, c.Pos.Z)), i, c));
            i++;
        }
        _beamCands.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.I.CompareTo(b.I));
        _beams.Clear();
        for (var k = 0; k < _beamCands.Count && k < CarLights.MaxBeams; k++) _beams.Add(_beamCands[k].V);
        return _beams;
    }

    void RenderCars()
    {
        if (_lastTick == 0) return;
        var rt = _lastTick + (Time.GetTicksMsec() / 1000.0 - _lastArrival) * Wire.TickRate - InterpDelayTicks;
        float? kmh = null;
        // real spot lights go to the nearest lit vehicles only (CarLights.MaxBeams); the others show their cones and lenses
        var beams = PickBeams();
        var flakGunner = false;
        FlightState? flown = null; // the helicopter the local player flies, for the pilot's readout and prompt
        var doorGunner = false;    // the local player is at a door gun: its camera and ammo readout are on
        var chased = false;        // the local player flies: the helicopter's chase camera (or its cockpit view) is on this frame
        var cockpit = false;       // the local player drives in the cockpit view this frame
        var hasCockpit = false;    // the local player drives a vehicle that has a cockpit view: the prompt names the view key
        var dt = (float)GetProcessDeltaTime();
        foreach (var v in _cars.Values)
        {
            v.Lamps?.Set(v.Lights && !v.Destroyed, v.Braking, beams.Contains(v));
            if (v.Buf.At(rt) is not { } s) continue;
            var p = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
            var rot = new Vector3(0, s.Yaw, 0);
            var flies = FlightDef(v) != null;
            if (flies) PoseFlight(v, rt, dt, ref p, ref rot); // a helicopter pitches and rolls; the one this player flies is drawn from its prediction
            v.Node.GlobalPosition = p;
            v.Node.Rotation = rot;
            if (v.Destroyed) continue;
            var flakMine = v.Flak != null && RenderFlak(v, s, rt, dt);
            if (v.DoorGun != null && RenderDoorGun(v, rot, rt, dt)) { flakMine = true; doorGunner = true; } // the gun camera, as at the flak
            if (flakMine) flakGunner = true;
            v.Smoke?.Near(Sfx.ListenerDistance(p) < 90f); // far vehicles do not smoke: particle budget
            if (flies) StepFlightFx(v, FlightDef(v)!, p, rot, dt); // rotors, their sound, the downwash
            else SfxCar(v, p);
            if (v.Driver == _myId && _driving && _me != null)
            {
                _carYaw = rot.Y;
                hasCockpit = CockpitOf(v) != null;
                if (CockpitFrame(v, p, rot, dt)) cockpit = true; // the view from the seat, in the place of the chase camera
                else
                {
                    if (flies) ChaseFlight(v, p, rot, dt); else _me.Chase(p, s.Yaw, Vehicles != null && v.Kind < Vehicles.Vehicles.Count ? Vehicles.Vehicles[v.Kind] : null, dt);
                    ShowSelfInCar(v);
                }
                if (flies) { flown = FlightOf(v); chased = true; }
                kmh = v.Kmh;
            }
            else if (!flakMine && _driving && _me != null && _seats is { Mine: > 0 } st && st.CarId == v.Id) { RideSeat(v, st.Mine); kmh = v.Kmh; } // another seat: first person from it, the speed shown as the driver's is
        }
        if (!_driving) HideSelfInCar();
        if (!cockpit) EndCockpit();
        if (!flakGunner && _flakCamera && _gunship == null) { _flakCamera = false; if (!chased) _me?.EndChase(_me.Yaw); } // the gun camera ends where the look pointed (a switch to the pilot's seat has started the chase camera already)
        if (!_driving && _gunship == null) _me?.EndChase(_carYaw); // a turret's camera is ended by EndGunship
        _hud?.SetSeats(SeatPanel());
        if (flakGunner) _hud?.SetVehicleReadout(doorGunner ? DoorGunReadout() : FlakReadout());
        else if (flown is { } fr) _hud?.SetVehicleReadout(FlightReadout(fr));
        else _hud?.SetVehicleSpeed(_driving ? kmh ?? 0f : null);
        string? prompt = null;
        if (_driving) prompt = doorGunner ? DoorGunPrompt() : flown is { } fp ? FlightPrompt(fp) : Strings.F("hud.vehicle.exit", InputBindings.Name("interact"));
        if (hasCockpit && prompt != null && _gunship == null && !_controlling) prompt = WithViewHint(prompt);
        else if (_me != null && !Dead && NearestFreeCar(_me.GlobalPosition) is { } free) prompt = Strings.F(FlightDef(free) != null ? "hud.flight.enter" : "hud.vehicle.enter", InputBindings.Name("interact"));
        _hud?.SetPrompt(TurretPrompt() ?? prompt ?? CratePrompt() ?? CorpsePrompt()); // a placing turret owns the prompt
    }

    CharacterView? _selfView;

    /// <summary>The chase camera sees your own character: a body view seated at the driver's seat, a child of the car so it moves with it.</summary>
    void ShowSelfInCar(CarView car)
    {
        if (Vehicles == null) return;
        var seats = Vehicles.Vehicles[car.Kind].Seats;
        var o = seats.Count > 0 && seats[0].Offset.Length == 3 ? seats[0].Offset : new float[3];
        if (_selfView == null || _selfView.GetParent() != car.Node)
        {
            _selfView?.QueueFree();
            _selfView = CharacterView.Make();
            if (Appearances.TryGetValue(_myId, out var look)) _selfView.SetCamo(look, CamoData);
            _selfView.SetStance(Stance.Crouch); // seated: there is no sitting animation, the crouch squash is the closest pose
            car.Node.AddChild(_selfView);
        }
        _selfView.Position = new Vector3(o[0], o[1], o[2]);
    }

    void HideSelfInCar()
    {
        if (_selfView == null) return;
        _selfView.QueueFree();
        _selfView = null;
    }

    static NVec3 NV(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// The nearest vehicle the ray (<paramref name="from"/>, <paramref name="dir"/>) enters within <paramref name="maxDistance"/>, on the
    /// vehicle's hit box (<see cref="VehicleBox"/>, the server's own collider) at its latest snapshot: the position the server resolved the shot against.
    /// </summary>
    bool VehicleAlong(Vector3 from, Vector3 dir, float maxDistance, out CarView car, out VehicleBoxHit hit)
    {
        car = null!; hit = default;
        if (Vehicles == null) return false;
        var best = float.MaxValue;
        foreach (var v in _cars.Values)
        {
            if (v.Destroyed || v.Kind >= Vehicles.Vehicles.Count) continue;
            var def = Vehicles.Vehicles[v.Kind];
            if (VehicleBox.Ray(NV(from), NV(dir), maxDistance, v.Pos, v.Yaw, def.HalfWidth, def.HalfLength, def.Height) is not { } h || h.Distance >= best) continue;
            best = h.Distance; hit = h; car = v;
        }
        return car != null;
    }

    /// <summary>A point in a vehicle's node frame (as <see cref="VehicleBoxHit"/>) in world space, where the car is drawn now.</summary>
    static Vector3 ToWorld(CarView v, Vector3 local) => v.Node.GlobalTransform * local;

    /// <summary>
    /// Bullet hole for a hitscan shot that the server stopped at <paramref name="end"/>: on the vehicle the ray enters at the end (a car
    /// is hit at its hit box, and the hole rides its node), otherwise on the world.
    /// </summary>
    void ShotHole(Vector3 from, Vector3 end)
    {
        var d = end - from;
        var len = d.Length();
        if (len < 0.05f) return;
        if (VehicleAlong(from, d / len, len + 0.5f, out var car, out var hit) && hit.Distance >= len - 0.5f)
        {
            BulletHoles.PlaceOn(Owned, car.Node, G(hit.Local), G(hit.LocalNormal));
            return;
        }
        BulletHoles.PlaceFromShot(Owned, from, end);
    }

    CarView? NearestFreeCar(Vector3 p)
    {
        CarView? best = null; var bd = float.MaxValue;
        if (Vehicles == null) return null;
        foreach (var v in _cars.Values)
        {
            if (v.Driver != 0 || v.Destroyed) continue;
            var def = Vehicles.Vehicles[v.Kind];
            var np = new NVec3(p.X, p.Y, p.Z);
            if (!VehicleReach.CanEnter(v.Pos, v.Yaw, def.HalfWidth, def.HalfLength, np, Vehicles.EnterRadiusOf(def))) continue; // as the server: within the radius of the footprint, not one hovering overhead
            var d = VehicleReach.DistanceToFootprint(v.Pos, v.Yaw, def.HalfWidth, def.HalfLength, np);
            if (d <= bd) { best = v; bd = d; }
        }
        return best;
    }
}
