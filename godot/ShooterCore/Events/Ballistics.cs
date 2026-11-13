using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Scripted arcs (issue #264): the logs the Bigfoot throws, the vehicles it throws and the player it launches fly under the same gravity as the players
/// (<see cref="MoveConsts.Gravity"/>). A flight is a closed formula of the time since the start, so the server needs no integration and a test can
/// check where it lands.
/// </summary>
public static class Ballistics
{
    public const float Gravity = MoveConsts.Gravity;

    /// <summary>The velocity that takes a body from <paramref name="from"/> to <paramref name="to"/> in <paramref name="seconds"/> under gravity.</summary>
    public static Vector3 LaunchVelocity(Vector3 from, Vector3 to, float seconds, float gravity = Gravity)
    {
        var t = MathF.Max(seconds, 0.05f);
        return new Vector3((to.X - from.X) / t, (to.Y - from.Y) / t + 0.5f * gravity * t, (to.Z - from.Z) / t);
    }

    /// <summary>Where a body launched from <paramref name="from"/> with <paramref name="velocity"/> is after <paramref name="t"/> seconds.</summary>
    public static Vector3 PositionAt(Vector3 from, Vector3 velocity, float t, float gravity = Gravity) =>
        from + velocity * t - new Vector3(0, 0.5f * gravity * t * t, 0);

    /// <summary>The velocity after <paramref name="t"/> seconds.</summary>
    public static Vector3 VelocityAt(Vector3 velocity, float t, float gravity = Gravity) => velocity - new Vector3(0, gravity * t, 0);
}

/// <summary>
/// A vehicle thrown by the Bigfoot: a scripted arc from where it stood to the chosen landing, turning about its vertical axis meanwhile. It is
/// over after <see cref="Seconds"/>, at exactly the landing. Nothing about it is damage: the vehicle and what is aboard take none from the throw.
/// </summary>
public sealed class VehicleThrow
{
    public VehicleThrow(Vector3 from, float yaw, Vector3 landing, float seconds, float spinRadPerS)
    {
        From = from; Yaw0 = yaw; Landing = landing; Seconds = MathF.Max(seconds, 0.1f); SpinRadPerS = spinRadPerS;
        Velocity = Ballistics.LaunchVelocity(from, landing, Seconds);
    }

    public Vector3 From { get; }
    public Vector3 Landing { get; }
    public Vector3 Velocity { get; }
    public float Yaw0 { get; }
    public float Seconds { get; }
    public float SpinRadPerS { get; }
    public float Elapsed { get; private set; }
    public bool Done => Elapsed >= Seconds;

    /// <summary>Advances the flight by <paramref name="dt"/> and returns where the vehicle is now (the landing once it is over).</summary>
    public Vector3 Step(float dt)
    {
        Elapsed = MathF.Min(Seconds, Elapsed + MathF.Max(0, dt));
        return Position;
    }

    public Vector3 Position => Done ? Landing : Ballistics.PositionAt(From, Velocity, Elapsed);

    /// <summary>The heading now: it spins once started and stops at the landing with the turn it has made.</summary>
    public float Yaw => Yaw0 + SpinRadPerS * Elapsed;

    /// <summary>The vertical speed now (m/s), what a client's driven-vehicle state carries as it flies.</summary>
    public float VerticalSpeed => Done ? 0 : Ballistics.VelocityAt(Velocity, Elapsed).Y;
}

/// <summary>A log the Bigfoot hurls (world entity type <see cref="BigfootRules.TrunkTypeId"/>): an arc, no homing, hurting the first player it meets.</summary>
public sealed class TrunkEntity : IWorldEntity
{
    readonly BigfootFile _data;
    readonly IBigfootWorld _world;
    readonly Random _rng;
    readonly Vector3 _from, _velocity;
    float _age;
    Vector3 _position;

    public TrunkEntity(BigfootFile data, IBigfootWorld world, Vector3 from, Vector3 velocity, int seed)
    {
        _data = data; _world = world; _from = from; _velocity = velocity; _position = from; _rng = new Random(seed);
        Yaw = MathF.Atan2(-velocity.X, -velocity.Z);
    }

    public byte TypeId => BigfootRules.TrunkTypeId;
    public ushort Owner => 0;
    public WorldDespawnReason? Ended { get; private set; }
    public Vector3 Position => _position;
    public float Yaw { get; }

    public void OnDamage(float amount, ushort attacker) { }

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        _age += dt;
        var before = _position;
        _position = Ballistics.PositionAt(_from, _velocity, _age);
        // the first player the log's path crosses this tick takes it: a hit ends the flight
        foreach (var p in _world.Players)
        {
            if (p.InVehicle) continue;
            var body = new Vector3(p.Feet.X, p.Feet.Y + MoveConsts.CapsuleHeight * 0.5f, p.Feet.Z);
            if (DistanceToSegment(body, before, _position) > _data.Trunk.RadiusM + PlayerHitBox.Radius) continue;
            if (MathF.Abs(body.Y - _position.Y) > MoveConsts.CapsuleHeight * 0.5f + _data.Trunk.RadiusM) continue;
            _world.Hurt(p.Id, _data.RollDamage(_rng), BigfootHurt.Trunk);
            Ended = WorldDespawnReason.Destroyed;
            return;
        }
        if (_world.GroundY(_position.X, _position.Z, _position.Y) is { } g && _position.Y <= g) { _position = new Vector3(_position.X, g, _position.Z); Ended = WorldDespawnReason.Expired; return; }
        if (_age >= _data.Trunk.LifeSeconds) Ended = WorldDespawnReason.Expired;
    }

    /// <summary>The position and the heading of the throw; nothing else is replicated (a client tumbles the log by itself).</summary>
    public WorldState Serialize() => WorldState.Make(_position, Yaw, 0, 0, 0, 0);

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared();
        var s = len2 > 1e-8f ? Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector3.Distance(p, a + ab * s);
    }
}
