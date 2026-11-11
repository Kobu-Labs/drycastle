using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Server-side car: a kinematic CharacterBody3D driven by <see cref="CarModel"/> (arcade planar model: speed along the facing, yaw rate
/// from steering) with the physics move providing wall collision, slopes and gravity. Chosen over VehicleBody3D (Jolt) because
/// it is deterministic, cannot flip or jitter, and needs no per-wheel tuning. Layer 2: players and bullets (mask 1) pass through it;
/// run-over is an overlap test in the server. Mask 1 = world only (players are excluded per body with collision exceptions).
/// </summary>
public partial class ServerCar : CharacterBody3D
{
    public const float HalfWidth = 1.1f, HalfLength = 2.15f;

    public ushort Id;
    public byte Colour;
    public Vector3 OriginPos;
    public float OriginYaw;
    public readonly VehicleSeat Seat = new();
    public readonly AbandonTimer Abandon = new();
    public CarState State;
    public readonly Dictionary<ushort, double> HitCooldown = new();

    public override void _Ready()
    {
        CollisionLayer = 2; CollisionMask = 1;
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(HalfWidth * 2, 1.2f, HalfLength * 2) }, Position = new Vector3(0, 0.75f, 0) });
        FloorSnapLength = 0.4f;
        FloorMaxAngle = Mathf.DegToRad(50);
        SetPhysicsProcess(false); // stepped by the server
        Reset();
    }

    public Vector3 Forward => new(-Mathf.Sin(State.Yaw), 0, -Mathf.Cos(State.Yaw));
    public bool AtOrigin => GlobalPosition.DistanceTo(OriginPos) < 0.5f;

    public void Reset()
    {
        GlobalPosition = OriginPos;
        State = new CarState { Yaw = OriginYaw, Speed = 0 };
        Rotation = new Vector3(0, OriginYaw, 0);
        Velocity = Vector3.Zero;
        Abandon.Reset();
        HitCooldown.Clear();
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
        if (Mathf.Abs(real) < Mathf.Abs(State.Speed)) State.Speed = real;
    }
}
