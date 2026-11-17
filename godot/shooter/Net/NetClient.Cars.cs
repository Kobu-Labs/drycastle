using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

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
        /// <summary>A driven vehicle's moving nodes and water effects (the amphibious transport; null for every other type, or a model without them).</summary>
        public DriveNodes? Drive;
        /// <summary>The generic wheel animation of a ground vehicle that is neither predicted nor has its own (null otherwise, or a model without wheels).</summary>
        public VehicleWheels? Roller;
        public readonly Interpolator Buf = new();
        /// <summary>The vehicle's visible parts (<see cref="CarFx.VisualName"/>): a boat's wave bob and tilt move this, not the root.</summary>
        public Node3D? Visual;
        /// <summary>Next time (s) a wake ring is due while the vehicle moves on the water.</summary>
        public double RippleDue;
        public ushort Driver;
        public int Kind;
        public NVec3 Pos;
        public float Yaw, Kmh;
        /// <summary>The yaw of the newest interpolated snapshot sample at the render tick, before the drawn pose replaces it: the hull's yaw the riders' snapshot views are taken against (<see cref="RiderPose.Figure"/>).</summary>
        public float SnapYaw;
        public uint LastTick;
        public LoopVoice? Snd;
        /// <summary>The layered engine sounds of a type with a vehicles.json "sounds" set (null otherwise, and until driven).</summary>
        public VehicleEngineSound? Engine;
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
        /// <summary>The model's wheel nodes (front left, front right, back left, back right; null entries where it has none, the array null on a headless client) and the wheels the Thieves took, as last applied (NetClient.Thieves.Loot.cs).</summary>
        public Node3D?[]? Wheels;
        public byte WheelsOff;
        /// <summary>A trailer's tractor, load and moving nodes (null for every type that is not a trailer): it is drawn from its tractor, <see cref="RenderTrailers"/>.</summary>
        public TrailerView? Trailer;
    }

    /// <summary>A ground vehicle whose wheels the generic animator turns: not predicted (DriveNodes), not flying, and not the Scud or a trailer (their own, see car-design section 3).</summary>
    static bool GenericWheels(VehicleDef d) => d.Drive == null && d.Flight == null && d.Trailer == null && d.Launcher == null;

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
            if (ShotFx.Enabled && Vehicles.Vehicles[v.Kind].Drive != null) v.Drive = DriveNodes.Find(v.Node);
            if (ShotFx.Enabled) v.Wheels = FindWheels(v.Node);
            if (ShotFx.Enabled && GenericWheels(Vehicles.Vehicles[v.Kind])) v.Roller = VehicleWheels.Find(v.Node);
            if (Vehicles.Vehicles[v.Kind].Trailer != null) v.Trailer = MakeTrailerView(v);
            _cars[id] = v;
            ShowGlassOf(id); // panes whose state arrived before the vehicle was drawn (ADR 0007)
        }
        v.Solid ??= VehicleCollider.Make(Owned, Vehicles.Vehicles[v.Kind]);
        v.Lamps ??= ShotFx.Enabled ? CarLights.Attach(v.Node, Vehicles!.Vehicles[v.Kind]) : null;
        if (v.Trailer != null) { OnTrailerRecord(v, rec); v.Driver = 0; } // a trailer's pitch field is its tractor's id, not a driver's (protocol 67)
        else v.Driver = VehicleRecord.Driver(rec);
        v.Lights = VehicleRecord.Lights(rec);
        SetVehicleDamage(v, VehicleRecord.Stage(rec), rec.State == EntityState.Dead);
        ApplyWheels(v, VehicleRecord.Wheels(rec)); // the wheels the Thieves took
        if (tick > v.LastTick && v.LastTick != 0)
        {
            var d = NVec3.Distance(rec.Position, v.Pos) / ((tick - v.LastTick) / (float)Wire.TickRate);
            var before = v.Kmh;
            v.Kmh += (d * 3.6f - v.Kmh) * 0.3f;
            v.Braking = before - v.Kmh > 0.5f; // the record has no brake bit: a clear fall in speed reads as braking
        }
        v.LastTick = tick; v.Pos = rec.Position; v.Yaw = rec.YawRad;
        v.Buf.Push(tick, rec.Position, rec.YawRad, v.Trailer != null ? VehicleRecord.Tractor(rec) : 0); // a trailer's pitch slot carries its tractor's id, so it is drawn hitched exactly while the sample at the render tick says so (RenderTrailers)
        PlaceCarSolid(v);
    }

    /// <summary>The pitch and roll a vehicle's hull is turned by on this client: the latest flight record's for a type with a hull list (<see cref="VehicleDef.Tilts"/>), else none (the one upright box turns with the yaw).</summary>
    (float Pitch, float Roll) HullTiltOf(CarView v, VehicleDef def) =>
        def.Tilts && _flights.TryGetValue(v.Id, out var f) ? (FlightRecord.Pitch(f.Rec), FlightRecord.Roll(f.Rec)) : (0f, 0f);

    /// <summary>Puts a vehicle's solid box where its latest records say: the server builds its own from the same numbers (<c>ServerCar.PlaceSolid</c>).</summary>
    void PlaceCarSolid(CarView v)
    {
        if (v.Solid == null) return;
        var p = new Vector3(v.Pos.X, v.Pos.Y, v.Pos.Z);
        var def = Vehicles!.Vehicles[v.Kind];
        if (def.Tilts) { var (pitch, roll) = HullTiltOf(v, def); VehicleCollider.Place(v.Solid, p, v.Yaw, pitch, roll, !v.Destroyed); }
        else VehicleCollider.Place(v.Solid, p, v.Yaw, !v.Destroyed);
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
        v.Snd?.Release(); v.Snd = null; v.Engine?.Release();
        v.Rotor?.Stop();
        v.Drive?.Stop();
        v.ExplodedAt = Now;
        if (v.Smoke != null) { v.Smoke.Stop(); v.Smoke.QueueFree(); v.Smoke = null; } // freed with its particles, not just stopped: nothing of the smoke outlives the vehicle
        v.Destroyed = true; // the wreck is gone until the server returns it: a live record must not bring it back or its smoke
        v.Node.Visible = false;
        VehicleFx.Explode(Owned, at, radius);
        BlastFelt(at, radius, "explosion.vehicle");
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
        var rt = RenderTickNow();
        float? kmh = null;
        // real spot lights go to the nearest lit vehicles only (CarLights.MaxBeams); the others show their cones and lenses
        var beams = PickBeams();
        var flakGunner = false;
        FlightState? flown = null; // the helicopter the local player flies, for the pilot's readout and prompt
        var doorGunner = false;    // the local player is at a door gun: its camera and ammo readout are on
        var chased = false;        // the local player flies: the helicopter's chase camera (or its cockpit view) is on this frame
        var cockpit = false;       // the local player drives in the cockpit view this frame
        var hasCockpit = false;    // the local player drives a vehicle that has a cockpit view: the prompt names the view key
        CarView? driven = null;    // the vehicle the local player drives (for the unhitch hint while it tows)
        var dt = (float)GetProcessDeltaTime();
        foreach (var v in _cars.Values)
        {
            if (v.Trailer != null) continue; // a trailer is drawn after the tractors, from the tractor as it is drawn (RenderTrailers)
            v.Lamps?.Set(v.Lights && !v.Destroyed, v.Braking, beams.Contains(v), DriveOf(v) is { Speed: < -0.3f }); // reverse lamps: the drawn signed speed
            if (v.Buf.At(rt) is not { } s) continue;
            var p = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
            var rot = new Vector3(0, s.Yaw, 0);
            v.SnapYaw = s.Yaw;
            var flies = FlightDef(v) != null;
            var drive = flies ? null : DriveDef(v);
            if (flies) PoseFlight(v, rt, dt, ref p, ref rot); // a helicopter pitches and rolls; the one this player flies is drawn from its prediction
            if (drive != null) PoseDrive(v, drive, rt, dt, ref p, ref rot); // an amphibious transport lies on the slope and bobs; the one this player drives is drawn from its prediction
            v.Node.GlobalPosition = p;
            v.Node.Rotation = rot;
            WaterCraft(v, p);
            if (v.Destroyed) continue;
            var flakMine = v.Flak != null && RenderFlak(v, s, rt, dt);
            // the gun stands on the body: on a driven vehicle that is the slope plus the body's own lean over its wheels
            var gunRot = drive != null && _drives.TryGetValue(v.Id, out var lean) ? rot + new Vector3(lean.Pose.BodyPitch, 0, lean.Pose.BodyRoll) : rot;
            if (v.DoorGun != null && RenderDoorGun(v, gunRot, rt, dt)) { flakMine = true; doorGunner = true; } // the gun camera, as at the flak
            if (flakMine) flakGunner = true;
            v.Smoke?.Near(Sfx.ListenerDistance(p) < 90f); // far vehicles do not smoke: particle budget
            if (flies) StepFlightFx(v, FlightDef(v)!, p, rot, dt); // rotors, their sound, the downwash
            else SfxCar(v, p, dt);
            if (drive != null) StepDriveFx(v, drive, p, dt); // the body's lean, wheels, propeller, wake and spray
            else v.Roller?.Step(p, rot.Y, dt); // wheels of a vehicle that is not predicted: from its drawn motion
            if (v.Driver == _myId && _driving && _me != null)
            {
                _carYaw = rot.Y; driven = v;
                hasCockpit = CockpitOf(v) != null;
                if (CockpitFrame(v, p, rot, dt)) cockpit = true; // the view from the seat, in the place of the chase camera
                else
                {
                    if (flies) ChaseFlight(v, p, rot, dt);
                    else if (drive != null) ChaseDrive(v, drive, p, rot, dt);
                    else _me.Chase(p, s.Yaw, Vehicles != null && v.Kind < Vehicles.Vehicles.Count ? Vehicles.Vehicles[v.Kind] : null, dt);
                    ShowSelfInCar(v);
                }
                if (flies) { flown = FlightOf(v); chased = true; }
                else if (drive != null && !cockpit) chased = true;
                kmh = DriveOf(v) is { } ds ? MathF.Abs(ds.Speed) * 3.6f : v.Kmh; // the driver of a predicted vehicle reads its speed now, not a tenth of a second ago
            }
            else if (!flakMine && _driving && _me != null && _seats is { Mine: > 0 } st && st.CarId == v.Id) { RideSeat(v, st.Mine); kmh = DriveOf(v) is { } rs ? MathF.Abs(rs.Speed) * 3.6f : v.Kmh; } // another seat: first person from it, the speed shown as the driver's is
        }
        RenderTrailers(rt, dt); // every trailer, from its tractor as drawn just now (the hitch, the articulation, the ground fit, the wheels)
        SeatRiders(); // the riders of every vehicle, on their seats as the vehicles are drawn this frame (ADR 0026)
        if (!_driving) HideSelfInCar();
        if (!cockpit) EndCockpit();
        if (!flakGunner && _flakCamera && _gunship == null) { _flakCamera = false; if (!chased) _me?.EndChase(_me.Yaw); } // the gun camera ends where the look pointed (a switch to the pilot's seat has started the chase camera already)
        if (!_driving && _gunship == null) _me?.EndChase(_carYaw); // a turret's camera is ended by EndGunship
        _hud?.SetSeats(SeatPanel());
        if (flakGunner) _hud?.SetVehicleReadout(doorGunner ? DoorGunReadout() : FlakReadout());
        else if (flown is { } fr) _hud?.SetVehicleReadout(FlightReadout(fr));
        else _hud?.SetVehicleSpeed(_driving ? kmh ?? 0f : null);
        string? prompt = null;
        if (_driving) prompt = doorGunner ? DoorGunPrompt() : flown is { } fp ? FlightPrompt(fp) : InArmedSeat ? ArmedSeatPrompt() : MySeat() is { Def.Drive: not null } ride ? DrivePrompt(ride.Car, ride.Index) : Strings.F("hud.vehicle.exit", InputBindings.Name("interact"));
        if (driven != null && prompt != null && TowHint(driven) is { } towHint) prompt += "     " + towHint; // a trailer on the hook: the key that unhitches it
        if (_driving && !Dead) prompt = ScudPrompt(prompt) is { Length: > 0 } scudLine ? scudLine : prompt; // a Scud truck's seat: the launcher's line first
        if (hasCockpit && prompt != null && Mode.ViewIsBody) prompt = WithViewHint(prompt);
        else if (_me != null && !Dead && NearestFreeCar(_me.GlobalPosition) is { } free) prompt = Strings.F(FlightDef(free) != null ? "hud.flight.enter" : "hud.vehicle.enter", InputBindings.Name("interact"));
        _hud?.SetPrompt(TurretPrompt() ?? LootPrompt() ?? GarageDoorPrompt() ?? ScudLoadPrompt(prompt) ?? prompt ?? MassPrompt() ?? RozhlasSetPrompt() ?? CratePrompt() ?? GroundItemPrompt() ?? CorpsePrompt()); // a placing turret owns the prompt
    }

    CharacterView? _selfView;

    /// <summary>The chase camera sees your own character: a body view seated at the driver's seat, a child of the car so it moves with it.</summary>
    void ShowSelfInCar(CarView car)
    {
        if (Vehicles == null) return;
        var seats = Vehicles.Vehicles[car.Kind].Seats;
        var o = seats.Count > 0 && seats[0].Offset.Length == 3 ? seats[0].Offset : new float[3];
        var anchor = SeatAnchor(car); // the body of a driven vehicle: the figure leans with it
        if (_selfView == null || _selfView.GetParent() != anchor)
        {
            _selfView?.QueueFree();
            _selfView = CharacterView.Make();
            if (Appearances.TryGetValue(_myId, out var look)) _selfView.SetCamo(look, CamoData);
            DressPlayer(_selfView, _myId); // the headgear shows in the seat too (#266)
            _selfView.SetStance(Stance.Crouch); // seated: there is no sitting animation, the crouch squash is the closest pose
            anchor.AddChild(_selfView);
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
    /// As on the server (NetServer.BulletHit, docs/glass.md) a ray that goes in by a glass pane or an open side and out by another is not stopped
    /// by that vehicle, and one that goes in and finds no way out is stopped on the far side of the hit box, not where it met it.
    /// <paramref name="dir"/> is a unit vector.
    /// </summary>
    /// <param name="skip">A vehicle the ray passes through: the one the local player fires from, in an armed seat (the server leaves that hull out too).</param>
    bool VehicleAlong(Vector3 from, Vector3 dir, float maxDistance, out CarView car, out VehicleBoxHit hit, CarView? skip = null)
    {
        car = null!; hit = default;
        if (Vehicles == null) return false;
        var best = float.MaxValue;
        foreach (var v in _cars.Values)
        {
            if (v.Destroyed || v == skip || v.Kind >= Vehicles.Vehicles.Count) continue;
            var def = Vehicles.Vehicles[v.Kind];
            var (pitch, roll) = HullTiltOf(v, def);
            if (VehicleBox.Ray(NV(from), NV(dir), maxDistance, v.Pos, v.Yaw, pitch, roll, def) is not { } h || h.Distance >= best) continue;
            if (def.GlassRects.Count > 0 || def.OpenSideRects.Count > 0)
            {
                var lo = GlassRules.ToVehicle(NV(from), v.Pos, v.Yaw, pitch, roll); var ld = GlassRules.DirToVehicle(NV(dir), v.Yaw, pitch, roll);
                var way = GlassRules.Through(def.GlassRects, def.OpenSideRects, lo, ld, h.Distance, GlassData.VehicleReach);
                if (way.Enters)
                {
                    if (way.Leaves) continue; // through the glass and out again
                    var far = MathF.Max(VehicleBox.ExitDistance(lo, ld, def), way.EntryT);
                    if (far > maxDistance || far >= best) continue;
                    h = new VehicleBoxHit(far, lo + ld * far, -ld); // inside, on the far side, facing back along the ray
                }
            }
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
            if (def.Seats.Count == 0) continue; // a trailer has no seat to take: nothing to enter
            var np = new NVec3(p.X, p.Y, p.Z);
            if (!VehicleReach.CanEnter(v.Pos, v.Yaw, def.HalfWidth, def.HalfLength, np, Vehicles.EnterRadiusOf(def))) continue; // as the server: within the radius of the footprint, not one hovering overhead
            var d = VehicleReach.DistanceToFootprint(v.Pos, v.Yaw, def.HalfWidth, def.HalfLength, np);
            if (d <= bd) { best = v; bd = d; }
        }
        return best;
    }
}
