using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Turret world entity rules: wire type id and the state byte the clients read.</summary>
public static class TurretRules
{
    /// <summary>Wire id of the turret world entity type (corpses use 1; 20 is the first free id for the streaks).</summary>
    public const byte TypeId = 20;

    /// <summary>Muzzle height above the feet, metres.</summary>
    public const float MuzzleHeightM = 1.2f;

    /// <summary>
    /// State byte. Bits 0-1: 0 = placing and invalid, 1 = placing and valid, 2 = armed. Bit 2: the barrel is firing (muzzle flash).
    /// Only the owner ever sees a placing ghost; other players are not sent a render for it.
    /// </summary>
    public const byte Invalid = 0, Valid = 1, Armed = 2, Flash = 4;

    public static bool IsGhost(byte state) => (state & 3) < Armed;
    public static bool IsGhostValid(byte state) => (state & 3) == Valid;
    public static bool IsArmed(byte state) => (state & 3) == Armed;
    public static bool IsFlashing(byte state) => (state & Flash) != 0;

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "turret", HasHp: true));
    }
}

/// <summary>What a turret needs from the server: aiming, geometry and damage. The server implements it; tests fake it.</summary>
public interface ITurretWorld
{
    /// <summary>The owner's feet and eye point, or null when the owner is not on foot (dead, driving, gone).</summary>
    (Vector3 Feet, Vector3 Eye)? Pose(ushort owner);

    /// <summary>The first world surface along the ray (players and dead bodies excluded), or null.</summary>
    GroundHit? WorldRay(Vector3 from, Vector3 dir, float maxDistance);

    /// <summary>Live enemies of the owner. The owner is never among them.</summary>
    IReadOnlyList<TurretCandidate> Enemies(ushort owner);

    /// <summary>Applies one of the turret's shots to a player, credited to the owner.</summary>
    void Fire(ushort owner, ushort target, float damage);
}

/// <summary>
/// The sentry turret. Starts as a placing ghost that follows the owner's aim (<see cref="Track"/>); <see cref="Place"/> arms
/// it when the spot is valid. Armed, it turns towards the nearest visible enemy at a limited rate, waits
/// <see cref="TurretDef.AcquireDelayS"/> after each new target, then fires until the target is gone or it dies.
/// </summary>
public sealed class TurretEntity : IWorldEntity
{
    public enum Stage : byte { Placing, Armed }

    readonly TurretDef _def;
    readonly ITurretWorld _world;

    /// <summary>How often the turret re-picks its target across all candidates (about 5 Hz). Between picks only the held target is checked.</summary>
    const float RepickIntervalS = 0.2f;

    ushort? _target;
    float _acquire, _cooldown, _flash, _age, _repick;

    public TurretEntity(TurretDef def, ushort owner, ITurretWorld world)
    {
        _def = def;
        Owner = owner;
        _world = world;
        Hp = def.Hp;
    }

    public Stage Phase { get; private set; } = Stage.Placing;
    /// <summary>True while placing and the aim gives a valid spot.</summary>
    public bool CanPlace { get; private set; }
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Hp { get; private set; }
    /// <summary>The player the turret is shooting at, or null.</summary>
    public ushort? Target => _target;

    public byte TypeId => TurretRules.TypeId;
    public ushort Owner { get; }
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>
    /// Moves the ghost to where the owner aims: the ground the aim ray hits, facing the owner's yaw. Only while placing.
    /// </summary>
    public void Track(Vector3 feet, Vector3 eye, float yaw, float pitch)
    {
        if (Phase != Stage.Placing) return;
        var dir = HitMath.Forward(yaw, pitch);
        var hit = _world.WorldRay(eye, dir, _def.PlacementProbeM);
        var (check, point) = TurretMath.Evaluate(feet, hit, _def);
        Position = point;
        Yaw = yaw;
        CanPlace = check == PlacementCheck.Valid;
    }

    /// <summary>Arms the turret where the ghost stands. False unless it is placing and the spot is valid.</summary>
    public bool Place()
    {
        if (Phase != Stage.Placing || !CanPlace) return false;
        Phase = Stage.Armed;
        Hp = _def.Hp;
        _age = 0;
        return true;
    }

    /// <summary>Applies damage (already scaled by the attacker's perk). An armed turret at 0 hp ends as <see cref="WorldDespawnReason.Destroyed"/>.</summary>
    public void OnDamage(float amount, ushort attacker)
    {
        if (Phase != Stage.Armed || Ended != null) return;
        Hp = MathF.Max(0, Hp - amount);
        if (Hp <= 0) Ended = WorldDespawnReason.Destroyed;
    }

    public void Tick(float dt)
    {
        if (Phase != Stage.Armed || Ended != null) return;
        _age += dt;
        if (_age >= _def.LifetimeSeconds) { Ended = WorldDespawnReason.Expired; return; }
        _cooldown -= dt;
        _flash -= dt;
        _repick -= dt;

        var muzzle = Position + Vector3.UnitY * TurretRules.MuzzleHeightM;
        var enemies = _world.Enemies(Owner);
        // The held target is checked every tick (one sight ray); the sight ray for every candidate runs only at RepickIntervalS,
        // or at once when the held target is lost. A re-pick that lands on the same target keeps the acquire delay running.
        var held = _target is { } heldId ? Find(enemies, heldId) : null;
        if (held is { } h && !InSight(muzzle, h.Aim)) held = null;
        if (_repick <= 0 || (_target != null && held == null))
        {
            _repick = RepickIntervalS;
            var pick = TurretMath.PickTarget(muzzle, enemies, _def.SightRangeM, Clear);
            if (pick != _target)
            {
                _target = pick;
                _acquire = pick != null ? _def.AcquireDelayS : 0f;
            }
            held = pick is { } p ? Find(enemies, p) : null;
        }
        if (_target is not { } id || held is not { } target) return;

        var aim = target.Aim;
        var want = TurretMath.YawTo(Position, aim);
        Yaw = TurretMath.StepAngle(Yaw, want, Rad(_def.TurnRateDegPerS) * dt);
        if (_acquire > 0) { _acquire -= dt; return; }
        if (MathF.Abs(TurretMath.AngleDiff(Yaw, want)) > Rad(_def.AimToleranceDeg)) return;
        if (_cooldown > 0) return;

        _cooldown = _def.FireIntervalS;
        _flash = 0.12f;
        _world.Fire(Owner, id, _def.DamagePerShot);
    }

    public WorldState Serialize()
    {
        byte state = Phase == Stage.Placing
            ? (CanPlace ? TurretRules.Valid : TurretRules.Invalid)
            : (byte)(TurretRules.Armed | (_flash > 0 ? TurretRules.Flash : 0));
        var hpByte = _def.Hp > 0 ? Hp / _def.Hp * 255f : 0f;
        return WorldState.Make(Position, Yaw, 0, Owner, state, hpByte);
    }

    static TurretCandidate? Find(IReadOnlyList<TurretCandidate> enemies, ushort id)
    {
        foreach (var c in enemies) if (c.Id == id) return c;
        return null;
    }

    bool InSight(Vector3 muzzle, Vector3 aim) => Vector3.Distance(muzzle, aim) <= _def.SightRangeM && Clear(muzzle, aim);

    /// <summary>True when no world surface lies between the two points (a small margin keeps the end point itself clear).</summary>
    bool Clear(Vector3 from, Vector3 to)
    {
        var d = Vector3.Distance(from, to);
        if (d <= 0) return true;
        var hit = _world.WorldRay(from, (to - from) / d, d);
        return hit is not { } h || Vector3.Distance(from, h.Point) >= d - 0.05f;
    }

    static float Rad(float deg) => deg * MathF.PI / 180f;
}
