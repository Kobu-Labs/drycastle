using System.Buffers.Binary;
using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Wire type and registration of the attack missile's world entity (killstreak "attack_missile").</summary>
public static class AttackMissileRules
{
    /// <summary>World entity type id of the missile (ids 1-9 are reserved for the other streaks; corpses use 1).</summary>
    public const byte TypeId = 10;

    /// <summary>Registers the type once (idempotent) so the snapshot codec knows it.</summary>
    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "attack_missile", HasHp: false));
    }
}

/// <summary>
/// Tuning of the attack missile from the "missile" block of its killstreaks.json entry. Speed and turn rate are kept in
/// SI units (m/s, rad/s); the file uses m/s and degrees. <see cref="AsWeapon"/> gives the splash the shared blast code uses.
/// </summary>
public sealed class MissileTuning
{
    public float SpeedMps { get; init; }
    public float TurnRateRad { get; init; }
    /// <summary>Hard safety cap on the flight, seconds. The ground ends the flight long before this.</summary>
    public float LifeSeconds { get; init; }
    /// <summary>Metres above the owner where the missile appears.</summary>
    public float SpawnHeight { get; init; }
    public float SpawnPitchRad { get; init; }
    /// <summary>The shallowest pitch the owner can steer to (negative, radians; the least steep dive, e.g. -10 deg).</summary>
    public float ShallowPitchRad { get; init; }
    /// <summary>The steepest pitch the owner can steer to (negative, radians; e.g. -80 deg, never straight down).</summary>
    public float SteepPitchRad { get; init; }
    public float Damage { get; init; }
    public float EdgeDamage { get; init; }
    public float FullDamageRadius { get; init; }
    public float SplashRadius { get; init; }
    public float SelfDamageMultiplier { get; init; }
    public float KnockbackSpeed { get; init; }

    /// <summary>The blast as a weapon: direct damage, splash falloff and knockback, for <see cref="ProjectileMath"/>.</summary>
    public WeaponDef AsWeapon() => new()
    {
        Id = AttackMissileStreak.StreakId,
        Damage = Damage,
        Splash = new SplashDef
        {
            Radius = SplashRadius,
            FullDamageRadius = FullDamageRadius,
            EdgeDamage = EdgeDamage,
            SelfDamageMultiplier = SelfDamageMultiplier,
            KnockbackSpeed = KnockbackSpeed,
        },
    };

    /// <summary>Validates the raw block of killstreaks.json. A missing or bad field throws with the file, streak and field named.</summary>
    internal static MissileTuning From(MissileRaw? r, string file, string where)
    {
        if (r is null) throw new DataFileException($"{file}: {where}: the \"missile\" block is required for the attack missile");
        float Pos(double? v, string field)
        {
            if (v is not { } x || !double.IsFinite(x) || x <= 0)
                throw new DataFileException($"{file}: {where}: field \"missile.{field}\" is required and must be more than 0");
            return (float)x;
        }
        float NonNeg(double? v, string field)
        {
            if (v is not { } x || !double.IsFinite(x) || x < 0)
                throw new DataFileException($"{file}: {where}: field \"missile.{field}\" is required and must be 0 or more");
            return (float)x;
        }
        double Dive(double? v, string field)
        {
            if (v is not { } x || !double.IsFinite(x) || x > -1 || x < -89)
                throw new DataFileException($"{file}: {where}: field \"missile.{field}\" is required and must be between -89 and -1 (negative is down)");
            return x;
        }
        var shallow = Dive(r.PitchMinDeg, "pitch_min_deg");
        var steep = Dive(r.PitchMaxDeg, "pitch_max_deg");
        if (steep >= shallow)
            throw new DataFileException($"{file}: {where}: field \"missile.pitch_max_deg\" (the steepest) must be below \"pitch_min_deg\" (the shallowest)");
        var pitch = Dive(r.SpawnPitchDeg, "spawn_pitch_deg");
        if (pitch > shallow || pitch < steep)
            throw new DataFileException($"{file}: {where}: field \"missile.spawn_pitch_deg\" must lie between \"pitch_max_deg\" and \"pitch_min_deg\"");
        var full = NonNeg(r.FullDamageRadius, "full_damage_radius");
        var radius = Pos(r.SplashRadius, "splash_radius");
        if (full >= radius) throw new DataFileException($"{file}: {where}: field \"missile.full_damage_radius\" must be less than \"splash_radius\"");
        return new MissileTuning
        {
            SpeedMps = Pos(r.SpeedMps, "speed_mps"),
            TurnRateRad = Pos(r.TurnRateDeg, "turn_rate_deg") * MathF.PI / 180f,
            LifeSeconds = Pos(r.LifeSeconds, "life_seconds"),
            SpawnHeight = Pos(r.SpawnHeight, "spawn_height"),
            SpawnPitchRad = (float)(pitch * Math.PI / 180.0),
            ShallowPitchRad = (float)(shallow * Math.PI / 180.0),
            SteepPitchRad = (float)(steep * Math.PI / 180.0),
            Damage = NonNeg(r.Damage, "damage"),
            EdgeDamage = NonNeg(r.EdgeDamage, "edge_damage"),
            FullDamageRadius = full,
            SplashRadius = radius,
            SelfDamageMultiplier = NonNeg(r.SelfDamageMultiplier, "self_damage_multiplier"),
            KnockbackSpeed = NonNeg(r.KnockbackSpeed, "knockback_speed"),
        };
    }

    internal sealed class MissileRaw
    {
        public double? SpeedMps { get; set; }
        public double? TurnRateDeg { get; set; }
        public double? LifeSeconds { get; set; }
        public double? SpawnHeight { get; set; }
        public double? SpawnPitchDeg { get; set; }
        public double? PitchMinDeg { get; set; }
        public double? PitchMaxDeg { get; set; }
        public double? Damage { get; set; }
        public double? EdgeDamage { get; set; }
        public double? FullDamageRadius { get; set; }
        public double? SplashRadius { get; set; }
        public double? SelfDamageMultiplier { get; set; }
        public double? KnockbackSpeed { get; set; }
    }
}

/// <summary>What the server needs to resolve a missile blast: who fired it, where, and what it struck first.</summary>
/// <param name="Id">The missile's world entity id.</param>
/// <param name="Owner">The player who controlled it (credited with the kills).</param>
/// <param name="Position">Blast centre, just in front of the surface it hit.</param>
/// <param name="Victim">The player struck directly, or null.</param>
/// <param name="Car">The vehicle struck directly (an opaque server handle), or null.</param>
public sealed record MissileImpact(ushort Id, ushort Owner, Vector3 Position, ushort? Victim, object? Car);

/// <summary>The blast as sent to every client (<see cref="EventKind.MissileBlast"/>).</summary>
public sealed record MissileBlastInfo(ushort Id, ushort Owner, Vector3 Position, float Radius);

/// <summary>Payload of <see cref="EventKind.MissileBlast"/>: id u16, owner u16, position 3 x f32, radius f32 (little endian).</summary>
public static class MissileCodec
{
    public const int Size = 2 + 2 + 12 + 4;

    public static byte[] EncodeBlast(ushort id, ushort owner, Vector3 pos, float radius)
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), owner);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), pos.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), pos.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), pos.Z);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), radius);
        return b;
    }

    /// <summary>Null when the payload is too short.</summary>
    public static MissileBlastInfo? DecodeBlast(ReadOnlySpan<byte> d)
    {
        if (d.Length < Size) return null;
        return new MissileBlastInfo(
            BinaryPrimitives.ReadUInt16LittleEndian(d),
            BinaryPrimitives.ReadUInt16LittleEndian(d[2..]),
            new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[4..]), BinaryPrimitives.ReadSingleLittleEndian(d[8..]), BinaryPrimitives.ReadSingleLittleEndian(d[12..])),
            BinaryPrimitives.ReadSingleLittleEndian(d[16..]));
    }
}

/// <summary>
/// The world the missile flies in. The server implements it (world and vehicle sweeps, the live players, the blast);
/// the tests use a fake.
/// </summary>
public interface IMissileWorld
{
    /// <summary>The match's entity manager (the missile is a world entity).</summary>
    WorldEntityManager Entities { get; }
    /// <summary>Distance along the ray to the world (infinity if none within <paramref name="len"/>); <paramref name="hitCar"/> is the vehicle struck, if the wall was one.</summary>
    float WorldDistance(Vector3 from, Vector3 dir, float len, out object? hitCar);
    /// <summary>Players that can be hit (alive, not the owner).</summary>
    IEnumerable<HitTarget> Targets();
    /// <summary>The owner's facing (yaw, radians), for the missile's first heading.</summary>
    float OwnerYaw(ushort owner);
    /// <summary>The missile hit something: the server applies the blast.</summary>
    void Detonate(MissileImpact impact);
}

/// <summary>
/// One attack missile in flight: a world entity that the owner steers. Its heading turns towards the owner's aim at
/// <see cref="MissileTuning.TurnRateRad"/> at most, and its pitch is kept inside the dive cone (shallow to steep limit), so
/// it always loses height and reaches the ground in a bounded time. It flies at constant speed and ends on impact (which
/// detonates) or when its life cap runs out (which does not). It has no hit points: the server's blast is its only damage.
/// </summary>
public sealed class AttackMissileEntity : IWorldEntity
{
    readonly MissileTuning _t;
    readonly IMissileWorld _world;
    float _wantYaw, _wantPitch, _age;

    public AttackMissileEntity(ushort owner, MissileTuning tuning, IMissileWorld world, Vector3 start, float yaw, float pitch)
    {
        Owner = owner; _t = tuning; _world = world;
        Position = start; Yaw = yaw; Pitch = ClampPitch(pitch);
        _wantYaw = yaw; _wantPitch = Pitch;
    }

    /// <summary>The manager's id, set once the missile is spawned.</summary>
    public ushort Id { get; set; }
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float Age => _age;
    public byte TypeId => AttackMissileRules.TypeId;
    public ushort Owner { get; }
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>The owner's aim (radians). The missile turns towards it at its turn rate.</summary>
    public void Aim(float yaw, float pitch)
    {
        _wantYaw = yaw;
        _wantPitch = ClampPitch(pitch);
    }

    /// <summary>The pitch stays inside the owner's dive cone: never level, never up, never steeper than the steepest limit.</summary>
    float ClampPitch(float pitch) => Math.Clamp(pitch, _t.SteepPitchRad, _t.ShallowPitchRad);

    public void Tick(float dt)
    {
        if (Ended != null) return;
        _age += dt;
        Steer(dt);
        var dir = HitMath.Forward(Yaw, Pitch);
        var len = _t.SpeedMps * dt;
        var wall = _world.WorldDistance(Position, dir, len, out var car);
        var sweep = ProjectileMath.Sweep(Position, dir, len, wall, _world.Targets(), Owner);
        if (sweep.Kind != ImpactKind.None)
        {
            // a world hit: the blast centre sits a hair in front of the surface, as for rockets
            var d = sweep.Kind == ImpactKind.World ? MathF.Max(0, sweep.Distance - 0.02f) : sweep.Distance;
            Position += dir * d;
            _world.Detonate(new MissileImpact(Id, Owner, Position, sweep.Kind == ImpactKind.Player ? sweep.Victim : null,
                sweep.Kind == ImpactKind.World ? car : null));
            Ended = WorldDespawnReason.Destroyed;
            return;
        }
        Position += dir * len;
        if (_age >= _t.LifeSeconds) Ended = WorldDespawnReason.Expired;
    }

    void Steer(float dt)
    {
        var max = _t.TurnRateRad * dt;
        Yaw = WrapAngle(Yaw + Math.Clamp(WrapAngle(_wantYaw - Yaw), -max, max));
        Pitch = ClampPitch(Pitch + Math.Clamp(_wantPitch - Pitch, -max, max));
    }

    static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= 2 * MathF.PI;
        while (a < -MathF.PI) a += 2 * MathF.PI;
        return a;
    }

    /// <summary>The missile has no hit points; damage is not its business (the blast is).</summary>
    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, Yaw, Pitch, Owner, 0, 0);
}

/// <summary>
/// The attack missile killstreak (killstreaks.json "attack_missile", a POV streak). Activation spawns the missile high
/// above the owner, hands the owner its control and ends when the missile ends (impact, life, or the owner's death, which
/// ends the streak and removes the missile).
/// </summary>
public sealed class AttackMissileStreak : IKillstreak
{
    public const string StreakId = "attack_missile";

    readonly IMissileWorld _world;
    readonly MissileTuning _t;

    public AttackMissileStreak(IMissileWorld world, MissileTuning tuning)
    {
        _world = world; _t = tuning;
    }

    public string Id => StreakId;

    /// <summary>Kills by the missile are its kills: they pay the streak award and do not advance the Streak counter.</summary>
    public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string> { StreakId };

    public bool Activate(KillstreakUse use)
    {
        var entities = _world.Entities;
        var start = new Vector3(use.Position.X, use.Position.Y + _t.SpawnHeight, use.Position.Z);
        var missile = new AttackMissileEntity(use.Owner, _t, _world, start, _world.OwnerYaw(use.Owner), _t.SpawnPitchRad);
        if (entities.Spawn(missile) is not { } id)
        {
            use.Log("no room for the missile (entity cap or type not registered)");
            return false;
        }
        missile.Id = id;
        if (!use.TakeControl(id, input => missile.Aim(Quant.Yaw(input.Yaw), Quant.Pitch(input.Pitch)), freezesBody: true))
        {
            entities.Despawn(id, WorldDespawnReason.Removed);
            return false;
        }

        // the missile's end is the streak's end; the streak's end removes the missile if it is still up
        void OnDespawned(WorldDespawned d)
        {
            if (d.Id == id) use.End(KillstreakEndReason.Finished);
        }
        entities.Despawned += OnDespawned;
        use.OnEnded(_ =>
        {
            entities.Despawned -= OnDespawned;
            entities.Despawn(id, WorldDespawnReason.Removed); // no-op when it already ended
        });
        return true;
    }
}
