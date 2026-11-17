using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Server-side vehicle (any type in vehicles.json: the def sets the footprint and medium; water craft float, see <see cref="Afloat"/>): a kinematic CharacterBody3D driven by <see cref="CarModel"/> (arcade planar model: speed along the facing, yaw rate
/// from steering) with the physics move providing wall collision, slopes and gravity. Chosen over VehicleBody3D (Jolt) because
/// it is deterministic, cannot flip or jitter, and needs no per-wheel tuning. Layer 2: bullets and rockets stop on it. Mask = the world only,
/// so it drives over the players (their bodies are on their own layer); run-over is an overlap test in the server.
/// It does not stop a player on foot either (layer 2 is not in the player's mask): that is the job of its solid box, a static body of
/// its own (<see cref="PlaceSolid"/>, #217).
/// </summary>
public partial class ServerCar : CharacterBody3D
{
    /// <summary>What a vehicle's body and ground rays stop on: the ground vehicle row of the rule table (the world; not the players, which have a layer of their own and are run over by an overlap test).</summary>
    static readonly uint WorldMask = CollisionRules.StaticMask(MoverKind.GroundVehicle);
    /// <summary>What its own sweep stops on: the world, and the demon's body (the ground vehicle row Collides with it).</summary>
    static readonly uint SweepMask = CollisionRules.SweepMask(MoverKind.GroundVehicle);
    public float HalfWidth = 1.1f, HalfLength = 2.15f;
    /// <summary>Where the car was at the start of the tick (set by the server before the step): the run-over test sweeps from here to the end.</summary>
    public Vector3 PrevPos;
    /// <summary>Origin height for a vehicle standing on the real ground: the highest ground under the footprint centre and corners, so the box starts flush instead of inside the terrain. Null when any sample has no ground (a hole) or the footprint is steeper than 0.8 m.</summary>
    public static float? RestHeight(PhysicsDirectSpaceState3D space, Vector3 pos, float yaw, float halfWidth, float halfLength, float hint)
    {
        float? hi = null, lo = null;
        var fwd = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var right = new Vector3(fwd.Z * -1, 0, fwd.X);
        for (var ia = -8; ia <= 8; ia++)
        for (var ib = -3; ib <= 3; ib++)
        {
            var p = pos + fwd * (ia / 8f * halfLength) + right * (ib / 3f * halfWidth);
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(p.X, hint + 3f, p.Z), new Vector3(p.X, hint - 8f, p.Z));
            q.CollisionMask = WorldMask;
            var r = space.IntersectRay(q);
            if (r.Count == 0) return null;
            var y = r["position"].AsVector3().Y;
            hi = hi == null ? y : Mathf.Max(hi.Value, y);
            lo = lo == null ? y : Mathf.Min(lo.Value, y);
        }
        return hi!.Value - lo!.Value > 0.8f ? null : hi;
    }

    public ushort Id;
    /// <summary>Type: index into vehicles.json and its def (car and motorcycle share this class).</summary>
    public int Kind;
    public VehicleDef Def = null!;
    /// <summary>Weapon byte of the entity record: type index and colour variant (<see cref="VehicleRecord.Pack"/>).</summary>
    public byte Colour;
    public Vector3 OriginPos;
    public float OriginYaw;
    /// <summary>Seats from the def (seat 0 the driver); built in <see cref="_Ready"/>.</summary>
    public VehicleSeat Seat { get; private set; } = null!;
    /// <summary>The gunner seat's gun, with its own magazine and reserve; refilled by a full <see cref="Reset"/>. Null when the type has none.</summary>
    public VehicleGun? Gun { get; private set; }
    /// <summary>A flak truck's turret: its yaw against the chassis (it turns with the vehicle when nobody aims it), the barrels' elevation, and which barrel fires next. See <see cref="FlakRules"/>.</summary>
    public float TurretLocalYaw, GunPitch;
    public int FlakBarrel;
    /// <summary>The seat occupants last sent to the riders (<see cref="SeatCodec"/>); null until the first send. A change is sent again.</summary>
    public ushort[]? SeatsSent;
    /// <summary>Headlights on (replicated as one bit, <see cref="VehicleRecord.Make"/>): set when a driver gets in at night, toggled by the H key, off when empty.</summary>
    public bool Lights;
    public CarState State;
    public readonly Dictionary<ushort, double> HitCooldown = new();

    /// <summary>The hit points and the life of the vehicle (alive, doomed with a blast scheduled, destroyed until it returns): the pure machine in <see cref="VehicleLifecycle"/>. Built in <see cref="_Ready"/>.</summary>
    public VehicleLifecycle Life { get; private set; } = null!;
    /// <summary>Hidden hit points (ticket #103): only the stage derived from them ever reaches clients.</summary>
    public float Hp => Life.Hp;
    /// <summary>Who dealt the last damage (0 = nobody) and with which weapon byte: credited for the blast.</summary>
    public ushort LastAttacker;
    public byte LastWeapon;
    /// <summary>Blown up and gone from the world until it returns (<see cref="VehicleLife.Destroyed"/>).</summary>
    public bool Destroyed => Life.Destroyed;
    /// <summary>Counts every <see cref="Reset"/>: the lag compensation starts a new pose history when it changes (ADR 0013, B-02).</summary>
    public int Lives;
    public int LivesSeen;
    /// <summary>The tick the current life began (a reset): no rewind goes before it.</summary>
    public uint LifeStart;
    /// <summary>This vehicle's poses at the end of recent ticks, for rewinding a shot (ADR 0013, B-02).</summary>
    public readonly VehiclePoseRing Poses = new();
    /// <summary>Speed lost to a wall in the last <see cref="Step"/> (m/s), for crash damage.</summary>
    public float LastImpact;

    /// <summary>A flying type (vehicles.json medium air): stepped by <see cref="StepFlight"/>, never by <see cref="Step"/>.</summary>
    public bool Flies => Def.Flight != null;
    /// <summary>The flight state of a flying type (ADR 0006): the server's is the true one, the pilot's client predicts its own.</summary>
    public FlightState Flight;
    /// <summary>The flying type's view of the solid world; null for everything that drives or floats.</summary>
    public FlightProbe? Probe;
    /// <summary>Server tick of the last flight step (a pilot's inputs step it one for one; a tick with none is filled in by the vehicle step).</summary>
    public uint FlightTick;
    /// <summary>The hardest impact of the flight steps since the vehicle step last read it, m/s.</summary>
    public float FlightImpact;
    /// <summary>Set by the server before the node enters the tree: the map's water and flyable rectangle for the probe.</summary>
    public Water FlightWater = Water.Empty;
    public FlightBounds FlightBounds = FlightBounds.None;

    /// <summary>A type its driver's client predicts (vehicles.json drive, the amphibious transport, ADR 0010): stepped by <see cref="StepDrive"/>, never by <see cref="Step"/>.</summary>
    public bool Drives => Def.Drive != null;
    /// <summary>The drive state of such a type: the server's is the true one, the driver's client predicts its own.</summary>
    public DriveState Drive;
    /// <summary>Its view of the solid world; null for every other type.</summary>
    public DriveProbe? DriveWorld;
    /// <summary>Server tick of the last step a driver's input made (the inputs step it one for one); the vehicle step fills in when they stop arriving.</summary>
    public uint DriveTick;
    /// <summary>The hardest impact of the drive steps since the vehicle step last read it, m/s.</summary>
    public float DriveImpact;
    /// <summary>Where it stood when the last vehicle step ended: the run-over test sweeps from here, since a driver's inputs move it before the next one.</summary>
    public Vector3 TickEndPos;

    public override void _Ready()
    {
        CollisionLayer = 2; CollisionMask = SweepMask;
        if (IsTrailer) ReadyTrailer(); // before the life: a loaded trailer's hit points are its load's
        Life = new VehicleLifecycle(Cargo?.MaxHp(Def) ?? Def.Hp);
        Seat = new VehicleSeat(Def.Seats.Count); // a trailer has none
        // the hit volume (VehicleBox): ground to the top of the body; a type with a hull list has a shape for each of its boxes (the body itself stays upright, it turns with the yaw only)
        if (Def.Tilts)
            foreach (var b in Def.HullBoxes) AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(b.HalfWidth * 2, b.Y1 - b.Y0, b.HalfLength * 2) }, Position = new Vector3(b.Cx, (b.Y0 + b.Y1) / 2, b.Cz) });
        else AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(HalfWidth * 2, Def.Height, HalfLength * 2) }, Position = new Vector3(0, Def.Height / 2, 0) });
        FloorSnapLength = 0.4f;
        FloorMaxAngle = Mathf.DegToRad(50);
        SetPhysicsProcess(false); // stepped by the server
        if (Flies) { Probe = FlightProbe.Make(Def, FlightWater, FlightBounds); AddChild(Probe); }
        _solid = VehicleCollider.Make(this, Def);
        if (Drives) { DriveWorld = DriveProbe.Make(Def, FlightWater); AddChild(DriveWorld); }
        Reset();
        PlaceSolid();
    }

    StaticBody3D _solid = null!;
    (int X, int Y, int Z, ushort Yaw, bool Solid, short Pitch, ushort Roll)? _solidAt;

    /// <summary>
    /// Puts the box that stops on-foot players (<see cref="VehicleCollider"/>) where the vehicle is now, gone while it is destroyed. The server calls it
    /// for every vehicle before it moves the players of a tick. The place is rounded as the snapshot record rounds it, so the box is the very one the
    /// clients build from that record and predict against.
    /// </summary>
    public void PlaceSolid()
    {
        var p = GlobalPosition;
        var tilt = Def.Tilts && Flies; // a hull that tilts turns with the flight state, rounded as its flight record is (FlightRecord.AsSent)
        (int X, int Y, int Z, ushort Yaw, bool Solid, short Pitch, ushort Roll) at = (Quant.Pos(p.X), Quant.Pos(p.Y), Quant.Pos(p.Z), Quant.Yaw(State.Yaw), !Destroyed,
            tilt ? Quant.Pitch(Flight.Pitch) : (short)0, tilt ? Quant.Yaw(Flight.Roll) : (ushort)0);
        if (at == _solidAt) return;
        _solidAt = at;
        var place = new Vector3(Quant.Pos(at.X), Quant.Pos(at.Y), Quant.Pos(at.Z));
        if (tilt) VehicleCollider.Place(_solid, place, Quant.Yaw(at.Yaw), Quant.Pitch(at.Pitch), FlightMath.Wrap(Quant.Yaw(at.Roll)), at.Solid);
        else VehicleCollider.Place(_solid, place, Quant.Yaw(at.Yaw), at.Solid);
    }

    /// <summary>Raises the origin onto the ground under it when that is above it (a bank rising out of the water).</summary>
    void LiftToGround()
    {
        var q = PhysicsRayQueryParameters3D.Create(new Vector3(GlobalPosition.X, GlobalPosition.Y + 3f, GlobalPosition.Z), new Vector3(GlobalPosition.X, GlobalPosition.Y - 8f, GlobalPosition.Z));
        q.CollisionMask = WorldMask;
        var r = GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (r.Count == 0) return;
        var g = r["position"].AsVector3().Y + 0.02f;
        if (g > GlobalPosition.Y) GlobalPosition = new Vector3(GlobalPosition.X, g, GlobalPosition.Z);
    }

    public Vector3 Forward => new(-Mathf.Sin(State.Yaw), 0, -Mathf.Cos(State.Yaw));

    /// <summary>Gone from the world after the blast: no collision, no driving; comes back through <see cref="Reset"/> once the server's clock reaches <paramref name="returnAt"/>.</summary>
    public void Destroy(double returnAt)
    {
        DropLinks(); // a destroyed tractor lets its trailer go, a destroyed trailer its tractor
        Life.Destroy(returnAt); Asleep = false; RestCount = 0;
        CollisionLayer = 0;
        Velocity = Vector3.Zero; State.Speed = 0;
        Flight.Velocity = System.Numerics.Vector3.Zero;
        Drive.Speed = 0; Drive.Slip = 0; Drive.VerticalSpeed = 0; Drive.YawRate = 0;
    }

    /// <summary>Back to the origin. <paramref name="repair"/> restores the HP too: not on a fall out of the world (a smoking vehicle stays damaged), but a doomed or destroyed one always comes back repaired (<see cref="VehicleLifecycle.Reset"/>).</summary>
    public void Reset(bool repair = true)
    {
        if (Retired) return; // taken out of the world for good: nothing brings it back
        DropLinks(); // a tractor that returns lets its trailer go
        if (IsTrailer) ResetTrailer(repair);
        Asleep = false; RestCount = 0;
        Lives++; // a new life for the lag compensation's pose history (NetServer.SyncCarLife)
        if (Life.Reset(repair))
        {
            LastAttacker = 0; LastWeapon = 0;
            CollisionLayer = 2;
            Gun = Def.Gun is { } g ? new VehicleGun(g) : Def.Flak is { } fk ? new VehicleGun(fk.AsGun()) : null;
            TurretLocalYaw = 0; FlakBarrel = 0;
            GunPitch = Def.Flak is { } f0 ? FlakRules.ClampPitch(f0, 0) : 0;
        }
        GlobalPosition = OriginPos;
        State = new CarState { Yaw = OriginYaw, Speed = 0 };
        Rotation = new Vector3(0, OriginYaw, 0);
        Velocity = Vector3.Zero;
        if (Flies) ResetFlight();
        if (Drives) ResetDrive();
        TickEndPos = GlobalPosition;
        HitCooldown.Clear();
        Lights = false;
    }

    /// <summary>A flying type back on its skids at the origin, rotor stopped. The origin is put on the ground under it when that is within a few metres (a sandbox spawn on a slope).</summary>
    void ResetFlight()
    {
        var at = new System.Numerics.Vector3(OriginPos.X, OriginPos.Y, OriginPos.Z);
        var ground = Probe!.GroundY(at + System.Numerics.Vector3.UnitY * 3f);
        if (!float.IsNegativeInfinity(ground) && MathF.Abs(ground - at.Y) < 5f) at.Y = ground + 0.02f;
        Flight = new FlightState { Position = at, Yaw = FlightMath.Wrap(OriginYaw), Grounded = true };
        FlightImpact = 0;
        OriginPos = new Vector3(at.X, at.Y, at.Z); // where it stands is its origin: a destroyed vehicle comes back here
        GlobalPosition = OriginPos;
    }

    /// <summary>
    /// One flight step (<see cref="FlightModel"/>): from a pilot's input, or the stabiliser's when nobody flies. The node follows the state; the hit box
    /// stays upright (it turns with the yaw only; the hull boxes of a type that has them are turned by the attitude where they are asked, from the flight state).
    /// </summary>
    public void StepFlight(in FlightInput input, float dt, uint tick)
    {
        FlightModel.Step(ref Flight, Def, input, dt, Probe!);
        FlightTick = tick;
        FlightImpact = MathF.Max(FlightImpact, Flight.Impact);
        GlobalPosition = new Vector3(Flight.Position.X, Flight.Position.Y, Flight.Position.Z);
        Rotation = new Vector3(0, Flight.Yaw, 0);
        State.Yaw = Flight.Yaw;
        State.Speed = Flight.Velocity.Length();
        Velocity = new Vector3(Flight.Velocity.X, Flight.Velocity.Y, Flight.Velocity.Z);
    }

    /// <summary>What of this vehicle other vehicles touch (#253): the hit box and, for a helicopter, the rotor disc, where the vehicle is now.</summary>
    public ContactShape ContactShape()
    {
        var p = Flies ? Flight.Position : new System.Numerics.Vector3(GlobalPosition.X, GlobalPosition.Y, GlobalPosition.Z);
        var f = Def.Flight;
        var tilt = Def.Tilts && Flies;
        return new ContactShape(p, State.Yaw, HalfWidth, HalfLength, Def.Height, f?.RotorRadius ?? 0, f?.RotorHeight ?? 0, f?.RotorThickness ?? 0,
            tilt ? Flight.Pitch : 0, tilt ? Flight.Roll : 0, Def.Tilts ? Def.HullBoxes : null);
    }

    /// <summary>This vehicle as one side of a bump: its mass (a type with none counts as a car; a trailer weighs what its load makes it) and the velocity it is moving with.</summary>
    public BumpBody BumpBody() => new(Def.Trailer is { } tr ? tr.MassFor(Cargo?.Loaded ?? true) : Def.Mass > 0 ? Def.Mass : 1200f, Flies ? Flight.Velocity : new System.Numerics.Vector3(Forward.X, 0, Forward.Z) * State.Speed, Flies);

    /// <summary>
    /// Applies a bump (<see cref="VehicleContact.Bump"/>): moves the vehicle apart and changes its velocity. A ground vehicle is moved through the
    /// world (it stops at a wall) and keeps only the part of the change along its facing, as the arcade model has no sideways speed; a helicopter
    /// moves freely (the flight probe finds what it ended up in on its next step) and, standing on its skids, is shoved without taking speed.
    /// </summary>
    public void ApplyBump(System.Numerics.Vector3 move, System.Numerics.Vector3 deltaV)
    {
        if (IsTrailer) { BumpTrailer(move, deltaV); return; }
        Wake();
        if (Flies)
        {
            Flight.Position += move;
            if (!Flight.Grounded) Flight.Velocity += deltaV;
            GlobalPosition = new Vector3(Flight.Position.X, Flight.Position.Y, Flight.Position.Z);
            Velocity = new Vector3(Flight.Velocity.X, Flight.Velocity.Y, Flight.Velocity.Z);
            return;
        }
        if (move.X != 0 || move.Z != 0) MoveAndCollide(new Vector3(move.X, 0, move.Z));
        var fwd = Forward;
        State.Speed += deltaV.X * fwd.X + deltaV.Z * fwd.Z;
        if (Drives)
        {
            // a driven type's state is the drive state (FollowDrive rewrites the node and State.Speed from it): the bump goes there too
            Drive.Position = new System.Numerics.Vector3(GlobalPosition.X, Drive.Position.Y, GlobalPosition.Z);
            Drive.Speed = State.Speed;
        }
    }

    /// <summary>A driven type back at its origin, at rest: standing on the ground under its wheels (lying on its slope), or afloat at its waterline.</summary>
    void ResetDrive()
    {
        Drive = new DriveState { Position = new System.Numerics.Vector3(OriginPos.X, OriginPos.Y, OriginPos.Z), Yaw = FlightMath.Wrap(OriginYaw) };
        DriveModel.Settle(ref Drive, Def, DriveWorld!);
        DriveImpact = 0;
        OriginPos = new Vector3(Drive.Position.X, Drive.Position.Y, Drive.Position.Z); // where it stands is its origin: a destroyed vehicle comes back here
        FollowDrive();
    }

    /// <summary>The node, the planar state the other rules read (yaw, speed, afloat) and the physics velocity follow the drive state. The hit box stays upright.</summary>
    void FollowDrive()
    {
        GlobalPosition = new Vector3(Drive.Position.X, Drive.Position.Y, Drive.Position.Z);
        Rotation = new Vector3(0, Drive.Yaw, 0);
        State.Yaw = Drive.Yaw;
        State.Speed = Drive.Speed;
        State.YawRate = Drive.YawRate;
        Afloat = Drive.Afloat;
        var v = Drive.Velocity;
        Velocity = new Vector3(v.X, v.Y, v.Z);
    }

    /// <summary>
    /// One drive step (<see cref="DriveModel"/>): from the driver's input, or hands off when nobody drives. The node follows the state; the hit box
    /// stays upright (it turns with the yaw only), as the clients' bullet-hole box does.
    /// </summary>
    public void StepDrive(in DriveInput input, float dt)
    {
        var before = Drive;
        DriveModel.Step(ref Drive, Def, Immobilised ? DriveInput.Held : input, dt, DriveWorld!, TowEffectNow());
        if (Towing != null && !StepTowed(dt)) TowModel.Hold(ref Drive, before); // the trailer is blocked: the tractor stops where it was
        DriveImpact = MathF.Max(DriveImpact, Drive.Impact);
        FollowDrive();
    }

    /// <summary>The brakes are on and the driver's keys ignored (a Scud truck while its rack moves): every drive step is <see cref="DriveInput.Held"/>. The driver's client does the same from the launcher's state.</summary>
    public bool Immobilised;

    /// <summary>Over a water body (the amphibious transport): the origin rides its surface and gravity does not apply.</summary>
    public bool Afloat;

    /// <summary>
    /// The Bigfoot's throw (issue #264) while the vehicle flies: a scripted arc that nobody steers and nothing damages. The server moves the vehicle along
    /// it (<see cref="PlaceThrown"/>) instead of stepping it, and ignores its driver's input, until it lands (<see cref="EndThrow"/>). Null otherwise.
    /// </summary>
    public VehicleThrow? Thrown;

    /// <summary>Puts the thrown vehicle at <paramref name="pos"/> turned to <paramref name="yaw"/>, flying at <paramref name="verticalSpeed"/>. No speed along the ground, no steering, no contact damage.</summary>
    public void PlaceThrown(Vector3 pos, float yaw, float verticalSpeed)
    {
        if (Drives)
        {
            Drive.Position = new System.Numerics.Vector3(pos.X, pos.Y, pos.Z);
            Drive.Yaw = FlightMath.Wrap(yaw);
            Drive.Speed = 0; Drive.Slip = 0; Drive.YawRate = 0; Drive.Throttle = 0; Drive.Steer = 0; Drive.Pitch = 0; Drive.Roll = 0; Drive.Sink = 0; Drive.Afloat = false;
            Drive.VerticalSpeed = verticalSpeed; Drive.Airborne = true; Drive.Impact = 0;
            DriveImpact = 0;
            FollowDrive();
            return;
        }
        if (Flies)
        {
            // a helicopter in its hands or in the air: level, still, off the ground, its rotor as it was; the clients draw it from this state
            Flight.Position = new System.Numerics.Vector3(pos.X, pos.Y, pos.Z);
            Flight.Velocity = new System.Numerics.Vector3(0, verticalSpeed, 0);
            Flight.Yaw = FlightMath.Wrap(yaw); Flight.Pitch = 0; Flight.Roll = 0;
            Flight.YawRate = 0; Flight.PitchRate = 0; Flight.RollRate = 0;
            Flight.Grounded = false; Flight.Impact = 0;
            FlightImpact = 0;
        }
        GlobalPosition = pos;
        State.Yaw = FlightMath.Wrap(yaw); State.Speed = 0; State.YawRate = 0;
        Rotation = new Vector3(0, State.Yaw, 0);
        Velocity = Vector3.Zero;
        LastImpact = 0;
    }

    /// <summary>The throw is over: the vehicle stands where it landed, at rest, with the damage of the throw none (the crash rules read nothing from it).</summary>
    public void EndThrow(Vector3 landing)
    {
        Thrown = null;
        PlaceThrown(landing, State.Yaw, 0);
        if (Drives)
        {
            DriveModel.Settle(ref Drive, Def, DriveWorld!);
            DriveTick = 0;
            FollowDrive();
        }
        if (Flies) { Flight.Velocity = System.Numerics.Vector3.Zero; Flight.Grounded = true; Flight.Agl = 0; Flight.Impact = 0; FlightImpact = 0; } // set down, undamaged
        TickEndPos = GlobalPosition;
        LastImpact = 0; DriveImpact = 0;
        Wake();
    }

    /// <summary>
    /// A parked car (no driver) rests once a driverless step leaves it where it stood (<see cref="ParkedRules"/>): the server then skips it until
    /// <see cref="Wake"/> (a rider, a hit, a bump, a blast, a reset).
    /// </summary>
    public bool Asleep;
    /// <summary>Consecutive resting driverless steps so far (<see cref="ParkedRules"/> hysteresis).</summary>
    public int RestCount;

    /// <summary>Puts a parked car back into the step (it was disturbed).</summary>
    public void Wake() { Asleep = false; RestCount = 0; }

    /// <summary>One driverless step (throttle, steer and handbrake zero), then whether the car now rests (<see cref="Asleep"/>).</summary>
    public void StepParked(float dt, Water water)
    {
        var before = GlobalPosition;
        Step(Def, 0, 0, false, dt, water);
        var moved = before.DistanceTo(GlobalPosition);
        RestCount = ParkedRules.NextRestCount(RestCount, ParkedRules.IsAtRest(moved, State.Speed, State.YawRate, Afloat || IsOnFloor()));
        Asleep = ParkedRules.Sleeps(RestCount);
    }

    /// <summary>
    /// One driverless flight step (the stabiliser's), then whether the helicopter now rests on the ground (<see cref="Asleep"/>, <see cref="FlightRest"/>).
    /// A helicopter with a rider never rests: somebody may take the pilot seat.
    /// </summary>
    public void StepFlightUnpiloted(float dt, uint tick)
    {
        var before = Flight;
        StepFlight(FlightInput.Pilotless(Seat.Occupied), dt, tick);
        RestCount = ParkedRules.NextRestCount(RestCount, FlightRest.IsAtRest(before, Flight) && !Seat.Occupied);
        Asleep = ParkedRules.Sleeps(RestCount);
    }

    /// <summary>
    /// One hands-off drive step, then whether the vehicle now rests (<see cref="Asleep"/>, <see cref="DriveRest"/>). With a rider it never rests.
    /// </summary>
    public void StepDriveUnpiloted(float dt)
    {
        var before = Drive;
        StepDrive(DriveInput.None, dt);
        RestCount = ParkedRules.NextRestCount(RestCount, DriveRest.IsAtRest(before, Drive) && !Seat.Occupied);
        Asleep = ParkedRules.Sleeps(RestCount);
    }

    public void Step(VehicleDef def, float throttle, float steer, bool handbrake, float dt, Water water)
    {
        var body = def.CanFloat ? water.BodyAt(GlobalPosition.X, GlobalPosition.Z) : null;
        var wasAfloat = Afloat;
        Afloat = body != null;
        var placeBefore = GlobalPosition; var yawBefore = State.Yaw;
        CarModel.Step(ref State, def, throttle, steer, handbrake, dt, Afloat, TowEffectNow());
        var fwd = Forward;
        Rotation = new Vector3(0, State.Yaw, 0);
        if (Afloat)
        {
            GlobalPosition = new Vector3(GlobalPosition.X, body!.SurfaceY, GlobalPosition.Z);
            Velocity = fwd * State.Speed;
        }
        else
        {
            var vy = IsOnFloor() ? -1f : Velocity.Y - 20f * dt;
            if (wasAfloat) LiftToGround(); // climbing out of the water onto a bank higher than the surface
            Velocity = fwd * State.Speed + new Vector3(0, vy, 0);
        }
        MoveAndSlide();
        // a wall or a slope takes speed away: feed the real speed back
        var real = new Vector3(Velocity.X, 0, Velocity.Z).Dot(fwd);
        LastImpact = Mathf.Abs(State.Speed) - Mathf.Abs(real);
        if (Mathf.Abs(real) < Mathf.Abs(State.Speed)) State.Speed = real;
        if (Towing != null && !StepTowed(dt))
        {
            // the trailer is blocked (a wall, a tree behind it): the tractor stops where it was, with the crash's speed loss, as a wall stops it
            LastImpact = Mathf.Max(LastImpact, Mathf.Abs(State.Speed));
            GlobalPosition = placeBefore; State.Yaw = yawBefore; State.Speed = 0; State.YawRate = 0;
            Rotation = new Vector3(0, yawBefore, 0); Velocity = Vector3.Zero;
        }
    }
}
