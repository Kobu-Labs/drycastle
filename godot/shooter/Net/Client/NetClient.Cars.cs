using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Client side of vehicles (#91): car entities are drawn interpolated about 100 ms in the past like remote players, including the
/// driver's own car (no prediction in v1; the one exception is the utility helicopter, which its pilot's client predicts, ADR 0006 and
/// NetClient.Flight.cs). While driving the body follows the car, a chase camera replaces the first-person one,
/// and the HUD shows the speed and the exit prompt; near a free car it shows the enter prompt.
/// </summary>
public partial class NetClient
{
    /// <summary>A ground vehicle whose wheels the generic animator turns: not predicted (DriveNodes), not flying, and not the Scud or a trailer (their own, see car-design section 3).</summary>
    static bool GenericWheels(VehicleDef d) => d.Drive == null && d.Flight == null && d.Trailer == null && d.Launcher == null;

    float _carYaw;
    public VehiclesFile? Vehicles { get => Ctx.Vehicles; set => Ctx.Vehicles = value; }

    void OnVehicleState(ushort id, EntityRecord rec, uint tick)
    {
        if (Vehicles == null) return; // never let a missing table abort the rest of the snapshot
        if (!Ctx.Cars.TryGet(id, out var v))
        {
            v = new CarView { Id = id, Node = CarFx.Make(Ctx.Owned, Vehicles!.Vehicles[Math.Min(VehicleRecord.Kind(rec), Vehicles.Vehicles.Count - 1)], VehicleRecord.Colour(rec)), Kind = Math.Min(VehicleRecord.Kind(rec), Vehicles.Vehicles.Count - 1), Pos = rec.Position, Yaw = rec.YawRad };
            if (ShotFx.Enabled && Vehicles.Vehicles[v.Kind].Flak != null) v.Flak = FlakNodes.Find(v.Node);
            if (ShotFx.Enabled && Vehicles.Vehicles[v.Kind].DoorGun is { } doorGun) v.DoorGun = DoorGunNodes.Find(v.Node, doorGun);
            if (ShotFx.Enabled && Vehicles.Vehicles[v.Kind].Flight != null) v.Rotor = RotorFx.Find(v.Node);
            if (ShotFx.Enabled && Vehicles.Vehicles[v.Kind].Drive != null) v.Drive = DriveNodes.Find(v.Node, Vehicles.Vehicles[v.Kind]);
            if (ShotFx.Enabled) v.Wheels = CarView.FindWheels(v.Node);
            if (ShotFx.Enabled && GenericWheels(Vehicles.Vehicles[v.Kind])) v.Roller = VehicleWheels.Find(v.Node);
            if (Vehicles.Vehicles[v.Kind].Trailer != null) v.Trailer = MakeTrailerView(v);
            Ctx.Cars.Add(v);
            _glass.ShowOf(id); // panes whose state arrived before the vehicle was drawn (ADR 0007)
        }
        v.Solid ??= VehicleCollider.Make(Ctx.Owned, Vehicles.Vehicles[v.Kind]);
        v.Lamps ??= ShotFx.Enabled ? CarLights.Attach(v.Node, Vehicles!.Vehicles[v.Kind]) : null;
        if (v.Trailer != null) { OnTrailerRecord(v, rec); v.Driver = 0; } // a trailer's pitch field is its tractor's id, not a driver's (protocol 67)
        else v.Driver = VehicleRecord.Driver(rec);
        v.Lights = VehicleRecord.Lights(rec);
        v.Braking = v.Trailer == null && VehicleRecord.Braking(rec); // the server's brakes bit, not a guess from the speed (#409)
        SetVehicleDamage(v, VehicleRecord.Stage(rec), rec.State == EntityState.Dead);
        v.ApplyWheels(VehicleRecord.Wheels(rec)); // the wheels the Thieves took
        if (tick > v.LastTick && v.LastTick != 0)
        {
            var d = NVec3.Distance(rec.Position, v.Pos) / ((tick - v.LastTick) / (float)Wire.TickRate);
            v.Kmh += (d * 3.6f - v.Kmh) * 0.3f;
        }
        v.LastTick = tick; v.Pos = rec.Position; v.Yaw = rec.YawRad;
        v.Buf.Push(tick, rec.Position, rec.YawRad, v.Trailer != null ? VehicleRecord.Tractor(rec) : 0); // a trailer's pitch slot carries its tractor's id, so it is drawn hitched exactly while the sample at the render tick says so (RenderTrailers)
        RefreshHull(v);
        PlaceCarSolid(v);
    }

    /// <summary>The pitch and roll a vehicle's hull is turned by on this client: the latest flight record's, the trailer's ground fit, or the latest drive record's slope; else none.</summary>
    (float Pitch, float Roll) HullTiltOf(CarView v, VehicleDef def) =>
        !def.Tilts ? (0f, 0f)
        : v.Trailer is { Hull: { } h } ? (h.Pitch, h.Roll) // a trailer's own ground fit (RefreshHull), the server's number
        : _flights.TryGetValue(v.Id, out var f) ? (FlightRecord.Pitch(f.Rec), FlightRecord.Roll(f.Rec))
        : _drives.TryGetValue(v.Id, out var d) && !CarThrown(v.Id) ? (DriveRecord.Pitch(d.Rec), DriveRecord.Roll(d.Rec)) // a driven vehicle's slope, the latest drive record's (the server's Drive.Pitch/Roll); not the springs' lean
        : (0f, 0f); // (a thrown one is level, as on the server)

    /// <summary>Where a vehicle's hull frame has its origin: the record's place, or a trailer's origin turned about its axle (<see cref="HullTiltOf"/>).</summary>
    static NVec3 HullFeetOf(CarView v) => v.Trailer is { Hull: { } h } ? h.Origin : v.Pos;

    /// <summary>Puts a vehicle's solid box where its latest records say: the server builds its own from the same numbers (<c>ServerCar.PlaceSolid</c>).</summary>
    void PlaceCarSolid(CarView v)
    {
        if (v.Solid == null) return;
        var f = HullFeetOf(v);
        var p = new Vector3(f.X, f.Y, f.Z);
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
        if (v.Exploded && Ctx.Now - v.ExplodedAt < 1.0) return;
        // a live record after the blast means the server returned it (30 s later): it is shown again, and only then
        if (v.Destroyed) { v.Destroyed = false; v.Exploded = false; v.Node.Visible = true; }
        if (!ShotFx.Enabled) return;
        if (stage > 0 && v.Smoke == null && Vehicles != null) v.Smoke = VehicleFx.Damage.Make(GlassClient.HostOf(v.Node), Vehicles.Vehicles[v.Kind]); // under the hull (in its rest frame): the smoke sways with the body
        v.Smoke?.Set(stage);
    }

    /// <summary>The blast of one vehicle: effect, sound, camera shake, and the vehicle is gone (engine loop and smoke stop).</summary>
    void ExplodeVehicle(CarView v, Vector3 at, float radius)
    {
        v.Exploded = true;
        v.Snd?.Release(); v.Snd = null; v.Engine?.Release();
        v.Rotor?.Stop();
        v.Drive?.Stop();
        v.ExplodedAt = Ctx.Now;
        if (v.Smoke != null) { v.Smoke.Stop(); v.Smoke.QueueFree(); v.Smoke = null; } // freed with its particles, not just stopped: nothing of the smoke outlives the vehicle
        v.Destroyed = true; // the wreck is gone until the server returns it: a live record must not bring it back or its smoke
        v.Node.Visible = false;
        VehicleFx.Explode(Ctx.Owned, at, radius);
        _blastDust.BlastFelt(at, radius, "explosion.vehicle");
    }

    void OnVehicleExplosion(VehicleExplosionMsg m)
    {
        if (KeepEventLogs) _logs.VehicleExplosions.Add((Ctx.Now, m));
        if (Ctx.Cars.TryGet(m.Id, out var v) && !v.Exploded) ExplodeVehicle(v, new Vector3(m.Position.X, m.Position.Y, m.Position.Z), m.Radius);
    }


    readonly List<(float D, int I, CarView V)> _beamCands = new();
    readonly HashSet<CarView> _beams = new();

    /// <summary>The nearest lit vehicles that get real beams (ties keep the order of the car list). Reuses its buffers: no LINQ per frame (#173, T3-031).</summary>
    HashSet<CarView> PickBeams()
    {
        _beamCands.Clear();
        var i = 0;
        foreach (var c in Ctx.Cars.All)
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
        if (Ctx.LastTick == 0) return;
        var rt = RenderTickNow();
        float? kmh = null;
        // real spot lights go to the nearest lit vehicles only (CarLights.MaxBeams); the others show their cones and lenses
        var beams = PickBeams();
        var flakGunner = false;
        FlightState? flown = null; // the helicopter the local player flies, for the pilot's readout and prompt
        var doorGunner = false;    // the local player is at a door gun: its camera and ammo readout are on
        var chased = false;        // the local player flies: the helicopter's chase camera (or its cockpit view) is on this frame
        var cockpit = false;       // the local player sees from the seat's eye this frame
        var hasCockpit = false;    // the local player's seat has a switchable view: the prompt names the view key
        CarView? driven = null;    // the vehicle the local player drives (for the unhitch hint while it tows)
        var dt = (float)GetProcessDeltaTime();
        foreach (var v in Ctx.Cars.All)
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
            var seat = Ctx.Driving && Ctx.Me != null ? OwnSeatIn(v) : -1; // the seat the local player has in this vehicle
            if (seat >= 0) TrackView(v, seat);
            if (seat == 0 || seat > 0 && !flakMine)
            {
                if (seat == 0) { _carYaw = rot.Y; driven = v; }
                if (_view.Current == RiderView.Sight) RideSeat(v, seat); // a gun's or a screen's seat: its own first-person view
                else
                {
                    hasCockpit = true; // the prompt names the view key
                    if (CockpitFrame(v, seat, p, rot, dt)) cockpit = true; // the view from the seat's eye, in the place of the chase camera
                    else
                    {
                        if (flies) ChaseFlight(v, p, rot, dt);
                        else if (drive != null) ChaseDrive(v, drive, p, rot, dt);
                        else Ctx.Me!.Chase(p, s.Yaw, Vehicles != null && v.Kind < Vehicles.Vehicles.Count ? Vehicles.Vehicles[v.Kind] : null, dt);
                        ShowSelfInCar(v, seat, viewerHides: false);
                    }
                    if (seat == 0 && flies) { flown = FlightOf(v); chased = true; }
                    else if (drive != null && !cockpit) chased = true;
                }
                kmh = DriveOf(v) is { } ds ? MathF.Abs(ds.Speed) * 3.6f : v.Kmh; // a predicted vehicle's speed now, not a tenth of a second ago; a passenger reads it as the driver does
            }
        }
        RenderTrailers(rt, dt); // every trailer, from its tractor as drawn just now (the hitch, the articulation, the ground fit, the wheels)
        SeatRiders(); // the riders of every vehicle, on their seats as the vehicles are drawn this frame (ADR 0026)
        if (!Ctx.Driving) { HideSelfInCar(); LeaveView(); }
        if (!cockpit) EndCockpit();
        if (!flakGunner && _flakCamera && _gunship == null) { _flakCamera = false; if (!chased) Ctx.Me?.EndChase(Ctx.Me.Yaw); } // the gun camera ends where the look pointed (a switch to the pilot's seat has started the chase camera already)
        if (!Ctx.Driving && _gunship == null) Ctx.Me?.EndChase(_carYaw); // a turret's camera is ended by EndGunship
        Ctx.Hud?.SetSeats(Ctx.Cars.SeatPanel());
        if (flakGunner) Ctx.Hud?.SetVehicleReadout(doorGunner ? DoorGunReadout() : FlakReadout());
        else if (flown is { } fr) Ctx.Hud?.SetVehicleReadout(FlightReadout(fr));
        else Ctx.Hud?.SetVehicleSpeed(Ctx.Driving ? kmh ?? 0f : null);
        string? prompt = null;
        if (Ctx.Driving) prompt = doorGunner ? DoorGunPrompt() : flown is { } fp ? FlightPrompt(fp) : InArmedSeat ? ArmedSeatPrompt() : Ctx.Cars.MySeat() is { Def.Drive: not null } ride ? DrivePrompt(ride.Car, ride.Index) : Strings.F("hud.vehicle.exit", InputBindings.Name("interact"));
        if (driven != null && prompt != null && TowHint(driven) is { } towHint) prompt += "     " + towHint; // a trailer on the hook: the key that unhitches it
        if (Ctx.Driving && !Dead) prompt = ScudPrompt(prompt) is { Length: > 0 } scudLine ? scudLine : prompt; // a Scud truck's seat: the launcher's line first
        if (hasCockpit && prompt != null && Mode.ViewIsBody) prompt = WithViewHint(prompt);
        else if (Ctx.Me != null && !Dead && NearestFreeCar(Ctx.Me.GlobalPosition) is { } free) prompt = Strings.F(FlightDef(free) != null ? "hud.flight.enter" : "hud.vehicle.enter", InputBindings.Name("interact"));
        Ctx.Hud?.SetPrompt(LadderPrompt() ?? TurretPrompt() ?? _thieves.Loot.Prompt() ?? _doors.GarageDoorPrompt(Ctx.Driving) ?? ScudLoadPrompt(prompt) ?? prompt ?? _mass.Prompt() ?? _rozhlas.Prompt() ?? _minerals.Prompt() ?? CratePrompt() ?? GroundItemPrompt() ?? CorpsePrompt() ?? LadderReachPrompt()); // a placing turret owns the prompt
    }

    /// <summary>On a ladder: the keys that work there. The prompt that owns the line (nothing else is in reach on a rung).</summary>
    string? LadderPrompt() => Ctx.Me is { State.OnLadder: true } && !Dead
        ? Strings.F("hud.ladder.release", InputBindings.Name("interact"), InputBindings.Name("jump"), InputBindings.Name("move_forward"), InputBindings.Name("move_back"))
        : null;

    /// <summary>A ladder in reach (the same test as getting on): the key that gets on. Last in the chain, as the interact key's lowest-priority target after the loot.</summary>
    string? LadderReachPrompt() => Ctx.Me != null && !Dead && !Ctx.Driving && Ctx.Me.Ladders.Count > 0 && Ctx.Me.Ladders.Reach(Ctx.Me.State.Position, Ctx.Me.Yaw) >= 0
        ? Strings.F("hud.ladder.climb", InputBindings.Name("interact"))
        : null;

    CharacterView? _selfView;
    bool _selfViewerHides;

    /// <summary>
    /// The own character seated at <paramref name="seat"/> as a body view, a child of the vehicle's body so it moves with it. The chase camera sees it. In the
    /// cockpit view (<paramref name="viewerHides"/>) it is not drawn for the camera but still casts its shadow, so the rider is still on the ground in the sun.
    /// </summary>
    void ShowSelfInCar(CarView car, int seat, bool viewerHides)
    {
        if (Vehicles == null) return;
        var seats = Vehicles.Vehicles[car.Kind].Seats;
        var o = seat < seats.Count && seats[seat].Offset.Length == 3 ? seats[seat].Offset : new float[3];
        var anchor = SeatAnchor(car); // the body of a driven vehicle: the figure leans with it
        if (_selfView == null || _selfView.GetParent() != anchor || _selfViewerHides != viewerHides)
        {
            _selfView?.QueueFree();
            _selfView = CharacterView.Make();
            Camo.Dress(_selfView, Ctx.MyId);
            DressPlayer(_selfView, Ctx.MyId); // the headgear shows in the seat too (#266)
            _selfView.SetStance(Stance.Crouch); // seated: there is no sitting animation, the crouch squash is the closest pose
            anchor.AddChild(_selfView);
            _selfViewerHides = viewerHides;
            if (viewerHides) ShadowsOnly(_selfView);
        }
        _selfView.Position = new Vector3(o[0], o[1], o[2]);
    }

    /// <summary>Every mesh under <paramref name="n"/> that casts a shadow keeps only that: the camera does not draw it.</summary>
    static void ShadowsOnly(Node n)
    {
        if (n is GeometryInstance3D g && g.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) g.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
        foreach (var c in n.GetChildren()) ShadowsOnly(c);
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
        foreach (var v in Ctx.Cars.All)
        {
            if (v.Destroyed || v == skip || v.Kind >= Vehicles.Vehicles.Count) continue;
            var def = Vehicles.Vehicles[v.Kind];
            var (pitch, roll) = HullTiltOf(v, def);
            if (VehicleBox.Ray(NV(from), NV(dir), maxDistance, HullFeetOf(v), v.Yaw, pitch, roll, def) is not { } h || h.Distance >= best) continue;
            if (def.GlassRects.Count > 0 || def.OpenSideRects.Count > 0)
            {
                var lo = GlassRules.ToVehicle(NV(from), HullFeetOf(v), v.Yaw, pitch, roll); var ld = GlassRules.DirToVehicle(NV(dir), v.Yaw, pitch, roll);
                var way = GlassRules.Through(def.GlassRects, def.OpenSideRects, lo, ld, h.Distance, _glass.Data.VehicleReach);
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
            BulletHoles.PlaceOn(Ctx.Owned, GlassClient.HostOf(car.Node), G(hit.Local), G(hit.LocalNormal));
            return;
        }
        BulletHoles.PlaceFromShot(Ctx.Owned, from, end);
    }

    CarView? NearestFreeCar(Vector3 p)
    {
        CarView? best = null; var bd = float.MaxValue;
        if (Vehicles == null) return null;
        foreach (var v in Ctx.Cars.All)
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
