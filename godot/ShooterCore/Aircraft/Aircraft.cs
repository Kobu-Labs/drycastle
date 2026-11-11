using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Wire state of an aircraft, carried in <see cref="WorldState.State"/>: bit 7 is the kind index (into
/// killstreaks.json "aircraft"), bits 0-6 the bank angle, quantised over +-<see cref="MaxBankDeg"/>.
/// </summary>
public static class AircraftState
{
    /// <summary>Largest bank the wire can carry, degrees.</summary>
    public const float MaxBankDeg = 60f;
    const float Steps = 63.5f;

    public static byte Pack(int kind, float bankRad)
    {
        var deg = Math.Clamp(bankRad * 180f / MathF.PI, -MaxBankDeg, MaxBankDeg);
        var q = (int)MathF.Round(deg / MaxBankDeg * Steps + Steps);
        return (byte)(((kind & 1) << 7) | Math.Clamp(q, 0, 127));
    }

    public static (int Kind, float BankRad) Unpack(byte s)
    {
        var deg = ((s & 0x7F) - Steps) / Steps * MaxBankDeg;
        return (s >> 7, deg * MathF.PI / 180f);
    }
}

/// <summary>
/// An aircraft flying a circle over the map (killstreak helicopters and gunships). It is a world entity: its position, yaw
/// and bank are replicated, and its kind is in the state byte. Aircraft are not shootable in v2 (anti-air is v3), so
/// <see cref="OnDamage"/> does nothing. The circle centre and phase are chosen by the streak that spawns it.
/// </summary>
public sealed class Aircraft : IWorldEntity
{
    /// <summary>Wire id of the aircraft world entity type.</summary>
    public const byte WireType = 40;
    const float Gravity = 9.81f;

    readonly AircraftKindDef _def;
    readonly int _kind;
    readonly Vector3 _centre;
    readonly float _direction;
    readonly float _angularRate;
    readonly float _lifetime;
    readonly float _phase;
    float _age;

    /// <param name="centreOnGround">Circle centre; its Y is the ground under it. The aircraft flies <see cref="AircraftKindDef.AltitudeM"/> above.</param>
    /// <param name="phaseRad">Angle on the circle at spawn (0 = east of the centre).</param>
    /// <param name="clockwise">Direction of travel seen from above.</param>
    /// <param name="owner">Player id that controls it (0 when none).</param>
    /// <param name="lifetimeS">Overrides the kind's lifetime; null keeps it.</param>
    public Aircraft(AircraftKindDef def, int kindIndex, Vector3 centreOnGround, float phaseRad, bool clockwise, ushort owner, float? lifetimeS = null)
    {
        _def = def;
        _kind = kindIndex;
        _centre = centreOnGround;
        _phase = phaseRad;
        _direction = clockwise ? -1f : 1f;
        _angularRate = def.SpeedMps / def.RadiusM;
        _lifetime = lifetimeS ?? def.LifetimeS;
        Owner = owner;
    }

    /// <summary>Registers the aircraft type once (idempotent). Hp is not sent: aircraft cannot be shot down in v2.</summary>
    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(WireType) == null) registry.Register(new WorldEntityDef(WireType, "aircraft", HasHp: false));
    }

    public byte TypeId => WireType;
    public ushort Owner { get; }
    public WorldDespawnReason? Ended { get; private set; }
    public AircraftKindDef Def => _def;
    public int Kind => _kind;
    /// <summary>Height of the ground under the circle centre (the plane the gunship's aim points are on, #160).</summary>
    public float GroundY => _centre.Y;
    /// <summary>Seconds flown since spawn.</summary>
    public float Age => _age;

    float Angle => _phase + _direction * _angularRate * _age;

    /// <summary>Current position: on the circle, <see cref="AircraftKindDef.AltitudeM"/> above the ground under the centre.</summary>
    public Vector3 Position
    {
        get
        {
            var a = Angle;
            return new Vector3(_centre.X + _def.RadiusM * MathF.Cos(a), _centre.Y + _def.AltitudeM, _centre.Z + _def.RadiusM * MathF.Sin(a));
        }
    }

    /// <summary>Heading in the engine's yaw convention (forward is -Z at yaw 0, as <see cref="HitMath.Forward"/>): along the circle.</summary>
    public float YawRad
    {
        get
        {
            var a = Angle;
            // tangent of the circle in the direction of travel
            var dx = -_direction * MathF.Sin(a);
            var dz = _direction * MathF.Cos(a);
            return MathF.Atan2(-dx, -dz);
        }
    }

    /// <summary>
    /// Bank toward the centre of the turn: atan(v^2 / (r g)), clamped to the kind's maximum. Positive is the right wing down.
    /// Only the model uses it (it is on the wire for the client).
    /// </summary>
    public float BankRad
    {
        get
        {
            var v = _def.SpeedMps;
            var raw = MathF.Atan(v * v / (_def.RadiusM * Gravity));
            var max = _def.MaxBankDeg * MathF.PI / 180f;
            // the centre lies to the right of a clockwise traveller (seen from above)
            return _direction * -MathF.Min(raw, max);
        }
    }

    public void Tick(float dt)
    {
        _age += dt;
        if (_age >= _lifetime) Ended = WorldDespawnReason.Expired;
    }

    /// <summary>Aircraft take no damage in v2.</summary>
    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize()
    {
        var p = Position;
        return WorldState.Make(p, YawRad, 0, Owner, AircraftState.Pack(_kind, BankRad), 0);
    }
}
