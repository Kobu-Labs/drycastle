using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Server-side vehicle (car, motorcycle or bicycle, the def sets the footprint): a kinematic CharacterBody3D driven by <see cref="CarModel"/> (arcade planar model: speed along the facing, yaw rate
/// from steering) with the physics move providing wall collision, slopes and gravity. Chosen over VehicleBody3D (Jolt) because
/// it is deterministic, cannot flip or jitter, and needs no per-wheel tuning. Layer 2: players and bullets (mask 1) pass through it;
/// run-over is an overlap test in the server. Mask 1 = world only (players are excluded per body with collision exceptions).
/// </summary>
public partial class ServerCar : CharacterBody3D
{
    public float HalfWidth = 1.1f, HalfLength = 2.15f;
    /// <summary>Collision box height; its bottom is the origin (the vehicle origin is the ground contact, like the models and <see cref="CarFx"/>).</summary>
    public const float BoxHeight = 1.2f;

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

    public override void _Ready()
    {
        CollisionLayer = 2; CollisionMask = 1;
        Hp = Def.Hp;
        Seat = new VehicleSeat(Def.Seats.Count);
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(HalfWidth * 2, BoxHeight, HalfLength * 2) }, Position = new Vector3(0, BoxHeight / 2, 0) });
        FloorSnapLength = 0.4f;
        FloorMaxAngle = Mathf.DegToRad(50);
        SetPhysicsProcess(false); // stepped by the server
        Reset();
    }

    public Vector3 Forward => new(-Mathf.Sin(State.Yaw), 0, -Mathf.Cos(State.Yaw));
    public bool AtOrigin => GlobalPosition.DistanceTo(OriginPos) < 0.5f;

    /// <summary>Gone from the world after the blast: no collision, no driving; comes back through <see cref="Reset"/>.</summary>
    public void Destroy()
    {
        Destroyed = true; DetonateAt = -1;
        CollisionLayer = 0;
        Velocity = Vector3.Zero; State.Speed = 0;
    }

    /// <summary>Back to the origin. <paramref name="repair"/> restores the HP too: not on an abandon return (a smoking vehicle stays damaged).</summary>
    public void Reset(bool repair = true)
    {
        if (repair || Destroyed)
        {
            Hp = Def.Hp; LastAttacker = 0; LastWeapon = 0; DetonateAt = -1;
            Destroyed = false; CollisionLayer = 2;
            Gun = Def.Gun is { } g ? new VehicleGun(g) : null;
        }
        GlobalPosition = OriginPos;
        State = new CarState { Yaw = OriginYaw, Speed = 0 };
        Rotation = new Vector3(0, OriginYaw, 0);
        Velocity = Vector3.Zero;
        Abandon.Reset();
        HitCooldown.Clear();
        Lights = false;
    }

    public void Step(VehicleDef def, float throttle, float steer, bool handbrake, float dt)
    {
        CarModel.Step(ref State, def, throttle, steer, handbrake, dt);
        var fwd = Forward;
        var vy = IsOnFloor() ? -1f : Velocity.Y - 20f * dt;
        Rotation = new Vector3(0, State.Yaw, 0);
        Velocity = fwd * State.Speed + new Vector3(0, vy, 0);
        MoveAndSlide();
        // a wall or a slope takes speed away: feed the real speed back
        var real = new Vector3(Velocity.X, 0, Velocity.Z).Dot(fwd);
        LastImpact = Mathf.Abs(State.Speed) - Mathf.Abs(real);
        if (Mathf.Abs(real) < Mathf.Abs(State.Speed)) State.Speed = real;
    }
}
