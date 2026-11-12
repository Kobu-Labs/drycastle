using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What an animal is doing; the low three bits of its replicated state byte (the clients pick the clip from it).</summary>
public enum AnimalState : byte { Idle = 0, Eat = 1, Walk = 2, Flee = 3, Dead = 4 }

/// <summary>Animal world entity rules: the wire type id and the state byte the clients read.</summary>
public static class AnimalRules
{
    /// <summary>Wire id of the animal world entity type (1 corpse, 10 missile, 20 turret, 30 care package, 40 aircraft).</summary>
    public const byte TypeId = 50;

    /// <summary>State byte: bits 0-2 the <see cref="AnimalState"/>, bit 3 flips on every hit that did not kill (a client plays its hit reaction when the bit changes).</summary>
    public const byte StateMask = 7, HitToggle = 8;

    public static AnimalState StateOf(byte state) => (AnimalState)(state & StateMask);
    public static bool HitToggled(byte state) => (state & HitToggle) != 0;

    /// <summary>The clips every animal of the pack has (the plain names; the same clips also exist under an "AnimalArmature|" prefix, which is not used).</summary>
    public const string ClipIdle = "Idle", ClipIdleAlt = "Idle_2", ClipEat = "Eating", ClipWalk = "Walk", ClipRun = "Gallop",
        ClipDeath = "Death", ClipHitLeft = "Idle_HitReact_Left", ClipHitRight = "Idle_HitReact_Right";

    /// <summary>Every clip the client plays, for the check that a model really has them.</summary>
    public static readonly string[] Clips = { ClipIdle, ClipIdleAlt, ClipEat, ClipWalk, ClipRun, ClipDeath, ClipHitLeft, ClipHitRight };

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "animal", HasHp: true));
    }
}

/// <summary>What an animal needs from the server: the ground under it, who frightens it, and where a shooter stands.</summary>
public interface IAnimalWorld
{
    /// <summary>The ground height at (x, z), or null where the animal must not go (no ground, water, a wall, too steep).</summary>
    float? GroundY(float x, float z);

    /// <summary>The nearest threat (a living player or a moving vehicle) within <paramref name="radius"/> of <paramref name="from"/>, or null.</summary>
    Vector3? NearestThreat(Vector3 from, float radius);

    /// <summary>Where a player stands now (the one who shot the animal), or null when they are gone.</summary>
    Vector3? PositionOf(ushort player);
}

/// <summary>
/// An animal (ambient wildlife or livestock). It stands, eats, wanders around the spot it spawned at, runs from threats
/// and from the shot that hurt it, and dies when its hit points run out: it then lies for
/// <see cref="AnimalsFile.CorpseSeconds"/> (its death clip plays) and is removed. Godot-free and deterministic for a seed.
/// A world entity of type <see cref="AnimalRules.TypeId"/>; the species index travels in the owner field of its state.
/// </summary>
public sealed class Animal : IWorldEntity
{
    readonly AnimalsFile _file;
    readonly IAnimalWorld _world;
    readonly Random _rng;

    float _timer;          // idle and eat: seconds left; walk: the time budget to arrive
    float _calm;           // seconds without a threat while fleeing
    float _alarm;          // seconds left of running from _alarmFrom (a shot that hurt it)
    Vector3 _alarmFrom;
    Vector3 _fleeFrom;
    float _bias;           // radians added to the flight heading to go round what blocked the way
    Vector3 _target;       // the wander target
    float _deadFor;
    bool _hitToggle;

    public Animal(AnimalsFile file, int species, Vector3 home, float yaw, IAnimalWorld world, int seed)
    {
        _file = file;
        Species = species;
        Def = file.Animals[species];
        _world = world;
        _rng = new Random(seed);
        Home = home;
        Position = _world.GroundY(home.X, home.Z) is { } y ? new Vector3(home.X, y, home.Z) : home;
        Yaw = yaw;
        Hp = Def.Hp;
        _timer = Between(file.IdleMinSeconds, file.IdleMaxSeconds);
    }

    public int Species { get; }
    public AnimalDef Def { get; }
    /// <summary>Where it spawned: the centre of its wandering.</summary>
    public Vector3 Home { get; }
    public Vector3 Position { get; private set; }
    /// <summary>Godot convention: 0 faces -Z.</summary>
    public float Yaw { get; private set; }
    public float Hp { get; private set; }
    public AnimalState State { get; private set; } = AnimalState.Idle;
    public bool Alive => State != AnimalState.Dead;
    /// <summary>The player whose hit killed it (0 until then, and for a death with no shooter).</summary>
    public ushort Killer { get; private set; }

    /// <summary>Raised once, when the hit that took the last hit points lands: the animal and the player who shot it. The server turns it into a kill credit.</summary>
    public event Action<Animal, ushort>? Died;

    public byte TypeId => AnimalRules.TypeId;
    /// <summary>Animals belong to no player; the species index is in the replicated state's owner field.</summary>
    public ushort Owner => 0;
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>The body box for hit tests: its centre, half the width, height and length, and the yaw it is turned by (the box's length lies along the heading).</summary>
    public (Vector3 Centre, Vector3 Half, float Yaw) Bounds =>
        (Position + Vector3.UnitY * (Def.HitBox[1] / 2), new Vector3(Def.HitBox[0], Def.HitBox[1], Def.HitBox[2]) / 2, Yaw);

    /// <summary>Applies damage (already scaled by the weapon, headshots and range). A dead animal takes none.</summary>
    public void OnDamage(float amount, ushort attacker)
    {
        if (Ended != null || !Alive || !(amount > 0)) return;
        Hp = MathF.Max(0, Hp - amount);
        if (Hp <= 0)
        {
            State = AnimalState.Dead;
            Killer = attacker;
            Died?.Invoke(this, attacker);
            return;
        }
        _hitToggle = !_hitToggle;
        // Run from where the shot came from; when the shooter is gone, from behind (the animal bolts the way it faces).
        _alarmFrom = _world.PositionOf(attacker) ?? Position - Forward * 5;
        _alarm = _file.AlarmSeconds;
        _fleeFrom = _alarmFrom;
        _calm = 0;
        Enter(AnimalState.Flee);
    }

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        if (!Alive)
        {
            _deadFor += dt;
            if (_deadFor >= _file.CorpseSeconds) Ended = WorldDespawnReason.Expired;
            return;
        }

        var threat = _world.NearestThreat(Position, Def.FleeRadius);
        if (_alarm > 0)
        {
            _alarm -= dt;
            threat ??= _alarmFrom;
        }
        if (threat is { } t)
        {
            _fleeFrom = t;
            _calm = 0;
            if (State != AnimalState.Flee) Enter(AnimalState.Flee);
        }

        switch (State)
        {
            case AnimalState.Flee: Flee(dt, threat is not null); break;
            case AnimalState.Walk: Walk(dt); break;
            default: Rest(dt); break;
        }
    }

    public WorldState Serialize()
    {
        var state = (byte)((byte)State | (_hitToggle ? AnimalRules.HitToggle : 0));
        // a living animal never shows 0 hp (a sliver of health rounds up)
        return WorldState.Make(Position, Yaw, 0, (ushort)Species, state, Alive ? MathF.Ceiling(Hp) : 0);
    }

    // ---- behaviour ----

    Vector3 Forward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));

    float Between(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    void Enter(AnimalState s)
    {
        State = s;
        _bias = 0;
    }

    /// <summary>Idle and eat: wait out the timer, then decide what to do next.</summary>
    void Rest(float dt)
    {
        _timer -= dt;
        if (_timer > 0) return;
        if (State == AnimalState.Eat)
        {
            Enter(AnimalState.Idle);
            _timer = Between(_file.IdleMinSeconds, _file.IdleMaxSeconds);
            return;
        }
        Choose();
    }

    /// <summary>After standing: walk back home when it strayed, otherwise eat or walk to a spot near home. Stands on when no spot has ground.</summary>
    void Choose()
    {
        var strayed = HorizontalDistance(Position, Home) > Def.WanderRadius;
        if (!strayed && _rng.NextDouble() < _file.EatChance)
        {
            Enter(AnimalState.Eat);
            _timer = Between(_file.EatMinSeconds, _file.EatMaxSeconds);
            return;
        }
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var angle = Between(0, MathF.Tau);
            var reach = strayed ? Between(0, Def.WanderRadius * 0.25f) : Between(_file.WanderMinM, Def.WanderRadius);
            var x = Home.X + MathF.Cos(angle) * reach;
            var z = Home.Z + MathF.Sin(angle) * reach;
            if (_world.GroundY(x, z) is not { } y) continue;
            _target = new Vector3(x, y, z);
            Enter(AnimalState.Walk);
            _timer = HorizontalDistance(Position, _target) / Def.WalkSpeed * 2 + 3; // twice the straight walk and a margin; then it gives up
            return;
        }
        _timer = Between(_file.IdleMinSeconds, _file.IdleMaxSeconds);
    }

    void Walk(float dt)
    {
        _timer -= dt;
        if (_timer <= 0 || HorizontalDistance(Position, _target) < 0.6f) { StandStill(); return; }
        if (!Step(TurretMath.YawTo(Position, _target), Def.WalkSpeed, dt)) StandStill();
    }

    void Flee(float dt, bool threatened)
    {
        if (!threatened)
        {
            _calm += dt;
            if (_calm >= _file.CalmSeconds) { StandStill(); return; }
        }
        var away = Position * 2 - _fleeFrom; // the point as far on the other side of the animal as the threat is on this one
        if (!Step(TurretMath.YawTo(Position, away) + _bias, Def.RunSpeed, dt))
            _bias = _bias >= 0 ? -1.3f : 1.3f; // blocked: try round the other way
    }

    void StandStill()
    {
        Enter(AnimalState.Idle);
        _timer = Between(_file.IdleMinSeconds, _file.IdleMaxSeconds);
    }

    /// <summary>
    /// Turns towards <paramref name="want"/> and, once roughly facing it, walks one step forward. False when the step is
    /// blocked (no ground there, or a drop or climb bigger than <see cref="AnimalsFile.MaxStepM"/>); turning in place is not blocked.
    /// </summary>
    bool Step(float want, float speed, float dt)
    {
        Yaw = TurretMath.StepAngle(Yaw, want, Def.TurnRateDegPerS * MathF.PI / 180f * dt);
        if (MathF.Abs(TurretMath.AngleDiff(Yaw, want)) > 1f) return true;
        var f = Forward;
        var x = Position.X + f.X * speed * dt;
        var z = Position.Z + f.Z * speed * dt;
        if (_world.GroundY(x, z) is not { } y || MathF.Abs(y - Position.Y) > _file.MaxStepM) return false;
        Position = new Vector3(x, y, z);
        return true;
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
