using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Server-side vehicle (any type in vehicles.json: the def sets the footprint and medium; water craft float, see <see cref="Afloat"/>): a kinematic CharacterBody3D driven by <see cref="CarModel"/> (arcade planar model: speed along the facing, yaw rate
/// from steering) with the physics move providing wall collision, slopes and gravity. Chosen over VehicleBody3D (Jolt) because
/// it is deterministic, cannot flip or jitter, and needs no per-wheel tuning. Layer 2: bullets and rockets stop on it. Mask 1 = world only,
/// and players are excluded per body with collision exceptions, so it drives over them; run-over is an overlap test in the server.
/// An exception works both ways, so this body does not stop a player on foot either: that is the job of its solid box, a static body of
/// its own (<see cref="PlaceSolid"/>, #217).
/// </summary>
public partial class ServerCar : CharacterBody3D
{
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
            q.CollisionMask = 1;
            var r = space.IntersectRay(q);
            if (r.Count == 0) return null;
            var y = r["position"].AsVector3().Y;
            hi = hi == null ? y : Mathf.Max(hi.Value, y);
            lo = lo == null ? y : Mathf.Min(lo.Value, y);
        }
        return hi!.Value - lo!.Value > 0.8f ? null : hi;
    }

    public ushort Id;
    /// <summary>Type: index into vehicles.json and its def (car, motorcycle, bicycle share this class).</summary>
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
    public readonly AbandonTimer Abandon = new();
    public CarState State;
    public readonly Dictionary<ushort, double> HitCooldown = new();

    /// <summary>Hidden hit points (ticket #103): only the stage derived from them ever reaches clients.</summary>
    public float Hp;
    /// <summary>Who dealt the last damage (0 = nobody) and with which weapon byte: credited for the blast.</summary>
    public ushort LastAttacker;
    public byte LastWeapon;
    /// <summary>Server time at which the vehicle blows up (it ran out of HP), or negative.</summary>
    public double DetonateAt = -1;
    public bool Destroyed;
    public double ReturnAt;
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
        CollisionLayer = 2; CollisionMask = 1;
        Hp = Def.Hp;
        Seat = new VehicleSeat(Def.Seats.Count);
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(HalfWidth * 2, Def.Height, HalfLength * 2) }, Position = new Vector3(0, Def.Height / 2, 0) }); // the hit volume (VehicleBox): ground to the top of the body
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
    (int X, int Y, int Z, ushort Yaw, bool Solid)? _solidAt;

    /// <summary>
    /// Puts the box that stops on-foot players (<see cref="VehicleCollider"/>) where the vehicle is now, gone while it is destroyed. The server calls it
    /// for every vehicle before it moves the players of a tick. The place is rounded as the snapshot record rounds it, so the box is the very one the
    /// clients build from that record and predict against.
    /// </summary>
    public void PlaceSolid()
    {
        var p = GlobalPosition;
        (int X, int Y, int Z, ushort Yaw, bool Solid) at = (Quant.Pos(p.X), Quant.Pos(p.Y), Quant.Pos(p.Z), Quant.Yaw(State.Yaw), !Destroyed);
        if (at == _solidAt) return;
        _solidAt = at;
        VehicleCollider.Place(_solid, new Vector3(Quant.Pos(at.X), Quant.Pos(at.Y), Quant.Pos(at.Z)), Quant.Yaw(at.Yaw), at.Solid);
    }

    /// <summary>Raises the origin onto the ground under it when that is above it (a bank rising out of the water).</summary>
    void LiftToGround()
    {
        var q = PhysicsRayQueryParameters3D.Create(new Vector3(GlobalPosition.X, GlobalPosition.Y + 3f, GlobalPosition.Z), new Vector3(GlobalPosition.X, GlobalPosition.Y - 8f, GlobalPosition.Z));
        q.CollisionMask = 1;
        var r = GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (r.Count == 0) return;
        var g = r["position"].AsVector3().Y + 0.02f;
        if (g > GlobalPosition.Y) GlobalPosition = new Vector3(GlobalPosition.X, g, GlobalPosition.Z);
    }

    public Vector3 Forward => new(-Mathf.Sin(State.Yaw), 0, -Mathf.Cos(State.Yaw));
    public bool AtOrigin => GlobalPosition.DistanceTo(OriginPos) < 0.5f;

    /// <summary>Gone from the world after the blast: no collision, no driving; comes back through <see cref="Reset"/>.</summary>
    public void Destroy()
    {
        Destroyed = true; DetonateAt = -1;
        CollisionLayer = 0;
        Velocity = Vector3.Zero; State.Speed = 0;
        Flight.Velocity = System.Numerics.Vector3.Zero;
        Drive.Speed = 0; Drive.Slip = 0; Drive.VerticalSpeed = 0; Drive.YawRate = 0;
    }

    /// <summary>Back to the origin. <paramref name="repair"/> restores the HP too: not on an abandon return (a smoking vehicle stays damaged).</summary>
    public void Reset(bool repair = true)
    {
        if (repair || Destroyed)
        {
            Hp = Def.Hp; LastAttacker = 0; LastWeapon = 0; DetonateAt = -1;
            Destroyed = false; CollisionLayer = 2;
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
        Abandon.Reset();
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
        OriginPos = new Vector3(at.X, at.Y, at.Z); // where it stands is its origin: the abandon timer measures from here
        GlobalPosition = OriginPos;
    }

    /// <summary>
    /// One flight step (<see cref="FlightModel"/>): from a pilot's input, or the stabiliser's when nobody flies. The node follows the state; the hit box
    /// stays upright (it turns with the yaw only), as the clients' bullet-hole box does.
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
        return new ContactShape(p, State.Yaw, HalfWidth, HalfLength, Def.Height, f?.RotorRadius ?? 0, f?.RotorHeight ?? 0, f?.RotorThickness ?? 0);
    }

    /// <summary>This vehicle as one side of a bump: its mass (a type with none counts as a car) and the velocity it is moving with.</summary>
    public BumpBody BumpBody() => new(Def.Mass > 0 ? Def.Mass : 1200f, Flies ? Flight.Velocity : new System.Numerics.Vector3(Forward.X, 0, Forward.Z) * State.Speed, Flies);

    /// <summary>
    /// Applies a bump (<see cref="VehicleContact.Bump"/>): moves the vehicle apart and changes its velocity. A ground vehicle is moved through the
    /// world (it stops at a wall) and keeps only the part of the change along its facing, as the arcade model has no sideways speed; a helicopter
    /// moves freely (the flight probe finds what it ended up in on its next step) and, standing on its skids, is shoved without taking speed.
    /// </summary>
    public void ApplyBump(System.Numerics.Vector3 move, System.Numerics.Vector3 deltaV)
    {
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
        OriginPos = new Vector3(Drive.Position.X, Drive.Position.Y, Drive.Position.Z); // where it stands is its origin: the abandon timer measures from here
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
        DriveModel.Step(ref Drive, Def, input, dt, DriveWorld!);
        DriveImpact = MathF.Max(DriveImpact, Drive.Impact);
        FollowDrive();
    }

    /// <summary>Over a water body (amphibious or boat): the origin rides its surface and gravity does not apply.</summary>
    public bool Afloat;

    public void Step(VehicleDef def, float throttle, float steer, bool handbrake, float dt, Water water)
    {
        var body = def.CanFloat ? water.BodyAt(GlobalPosition.X, GlobalPosition.Z) : null;
        var wasAfloat = Afloat;
        Afloat = body != null;
        CarModel.Step(ref State, def, throttle, steer, handbrake, dt, Afloat);
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
    }
}
