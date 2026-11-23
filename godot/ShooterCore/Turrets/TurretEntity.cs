using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Turrets;

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
    /// <summary>The coarse life of the turret: a placing ghost, then armed. <see cref="Phase"/> derives it from <see cref="Mode"/>.</summary>
    public enum Stage : byte { Placing, Armed }

    /// <summary>What the turret is doing. Armed is split by whether it has a target and where it is in the acquire delay: Idle, Acquiring, Aiming (turns, and fires when on target and the cooldown allows).</summary>
    public enum State : byte { Placing, Idle, Acquiring, Aiming }

    /// <summary>Who may follow whom. A target change (a new pick, a lost target) is checked every tick, so the armed states move between each other freely except Placing, which only goes to Idle once.</summary>
    public static readonly StateMachineSpec<State> Spec = StateMachineSpec<State>.Define(
            "Turret.State", "A sentry turret: the placing ghost, then armed and idle, acquiring a target (the outplay delay), or aiming and firing.", State.Placing)
        .Allow(State.Placing, State.Idle)
        .Allow(State.Idle, State.Acquiring, State.Aiming)
        .Allow(State.Acquiring, State.Aiming, State.Idle)
        .Allow(State.Aiming, State.Acquiring, State.Idle)
        .Note(State.Placing, "A ghost that follows the owner's aim; Place arms it when the spot is valid. Only the owner sees it.")
        .Note(State.Idle, "Armed, no target: scans for enemies at the repick rate.")
        .Note(State.Acquiring, "Has a target and turns towards it, but holds fire for acquire_delay_s (the outplay window; the machine's clock). A different target restarts the delay.")
        .Note(State.Aiming, "Past the delay: turns, and shoots once within the aim tolerance and off cooldown. Idle again when the target is lost; Idle goes straight here when the delay is 0.")
        .Build();

    readonly StateMachine<State> _fsm = new(Spec);
    readonly TurretDef _def;
    readonly ITurretWorld _world;

    /// <summary>How often the turret re-picks its target across all candidates (about 5 Hz). Between picks only the held target is checked.</summary>
    const float RepickIntervalS = 0.2f;

    ushort? _target;
    Countdown _cooldown, _flash, _repick; // between shots; the muzzle flash after one; to the next full re-pick
    Lifetime _life;                       // since placement

    public TurretEntity(TurretDef def, ushort owner, ITurretWorld world)
    {
        _def = def;
        Owner = owner;
        _world = world;
        Hp = def.Hp;
    }

    public Stage Phase => _fsm.Is(State.Placing) ? Stage.Placing : Stage.Armed;
    /// <summary>What the armed turret is doing (or Placing).</summary>
    public State Mode => _fsm.Current;
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
        if (!_fsm.Is(State.Placing)) return;
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
        if (!_fsm.Is(State.Placing) || !CanPlace) return false;
        _fsm.Enter(State.Idle);
        Hp = _def.Hp;
        _life.Reset();
        return true;
    }

    /// <summary>Applies damage (already scaled by the attacker's perk). An armed turret at 0 hp ends as <see cref="WorldDespawnReason.Destroyed"/>.</summary>
    public void OnDamage(float amount, ushort attacker)
    {
        if (_fsm.Is(State.Placing) || Ended != null) return;
        Hp = MathF.Max(0, Hp - amount);
        if (Hp <= 0) Ended = WorldDespawnReason.Destroyed;
    }

    public void Tick(float dt)
    {
        if (_fsm.Is(State.Placing) || Ended != null) return;
        if (_life.Tick(dt, _def.LifetimeSeconds)) { Ended = WorldDespawnReason.Expired; return; }
        _cooldown.Tick(dt);
        _flash.Tick(dt);
        _repick.Tick(dt);

        var muzzle = Position + Vector3.UnitY * TurretRules.MuzzleHeightM;
        var enemies = _world.Enemies(Owner);
        // The held target is checked every tick (one sight ray); the sight ray for every candidate runs only at RepickIntervalS,
        // or at once when the held target is lost. A re-pick that lands on the same target keeps the acquire delay running.
        var held = _target is { } heldId ? Find(enemies, heldId) : null;
        if (held is { } h && !InSight(muzzle, h.Aim)) held = null;
        if (_repick.Ready || (_target != null && held == null))
        {
            _repick.Start(RepickIntervalS);
            var pick = TurretMath.PickTarget(muzzle, enemies, _def.SightRangeM, Clear);
            if (pick != _target)
            {
                _target = pick;
                Retarget(pick != null);
            }
            held = pick is { } p ? Find(enemies, p) : null;
        }
        if (_target is not { } id || held is not { } target) return;

        var aim = target.Aim;
        var want = TurretMath.YawTo(Position, aim);
        Yaw = TurretMath.StepAngle(Yaw, want, Rad(_def.TurnRateDegPerS) * dt);
        if (_fsm.Is(State.Acquiring))
        {
            if (_fsm.Tick(dt)) _fsm.Enter(State.Aiming); // the delay is over: fire from the next tick on
            return;
        }
        if (MathF.Abs(TurretMath.AngleDiff(Yaw, want)) > Rad(_def.AimToleranceDeg)) return;
        if (_cooldown.Running) return;

        _cooldown.Start(_def.FireIntervalS);
        _flash.Start(0.12f);
        _world.Fire(Owner, id, _def.DamagePerShot);
    }

    public WorldState Serialize()
    {
        byte state = _fsm.Is(State.Placing)
            ? (CanPlace ? TurretRules.Valid : TurretRules.Invalid)
            : (byte)(TurretRules.Armed | (_flash.Running ? TurretRules.Flash : 0));
        var hpByte = _def.Hp > 0 ? Hp / _def.Hp * 255f : 0f;
        return WorldState.Make(Position, Yaw, 0, Owner, state, hpByte);
    }

    /// <summary>The target just changed: none goes Idle; a target starts the acquire delay (again, when the turret already had another), or goes straight to Aiming when the delay is 0.</summary>
    void Retarget(bool hasTarget)
    {
        if (!hasTarget) { _fsm.Enter(State.Idle); return; }
        if (_def.AcquireDelayS <= 0) { _fsm.Enter(State.Aiming); return; }
        if (!_fsm.Enter(State.Acquiring, _def.AcquireDelayS)) _fsm.Restart(_def.AcquireDelayS);
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
