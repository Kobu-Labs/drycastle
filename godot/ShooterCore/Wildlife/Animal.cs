using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What an animal is doing; the low three bits of its replicated state byte (the clients pick the clip from it).</summary>
public enum AnimalState : byte { Idle = 0, Eat = 1, Walk = 2, Flee = 3, Dead = 4, Sit = 5, Bark = 6 }

/// <summary>Animal world entity rules: the wire type id and the state byte the clients read.</summary>
public static class AnimalRules
{
    /// <summary>Wire id of the animal world entity type (1 corpse, 10 missile, 20 turret, 30 care package, 40 aircraft).</summary>
    public const byte TypeId = 50;

    /// <summary>State byte: bits 0-2 the <see cref="AnimalState"/>, bit 3 flips on every hit that did not kill (a client plays its hit reaction when the bit changes).</summary>
    public const byte StateMask = 7, HitToggle = 8;

    /// <summary>The owner field of an animal's replicated state: the species index in the low byte, the variant index (which model of the species) in the high byte.</summary>
    public static ushort Look(int species, int variant) => (ushort)(species | variant << 8);
    public static int SpeciesOf(ushort look) => look & 0xFF;
    public static int VariantOf(ushort look) => look >> 8;

    public static AnimalState StateOf(byte state) => (AnimalState)(state & StateMask);
    public static bool HitToggled(byte state) => (state & HitToggle) != 0;

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
/// An animal (ambient wildlife, livestock or a pet). It stands, eats, wanders around the spot it spawned at, runs from threats
/// and from the shot that hurt it (a species with <see cref="AnimalDef.Flees"/> off, like a pet, never runs), now and then sits or barks (species that opt in with
/// <see cref="AnimalDef.Sit"/>, <see cref="AnimalDef.Bark"/>), and dies when its hit points run out: it then lies for
/// <see cref="AnimalsFile.CorpseSeconds"/> (its death clip plays) and is removed. A <see cref="AnimalDef.OneHit"/> species dies from any hit and is removed at once. Godot-free and deterministic for a seed.
/// A world entity of type <see cref="AnimalRules.TypeId"/>; the species index travels in the owner field of its state.
/// </summary>
public sealed class Animal : IWorldEntity
{
    readonly AnimalsFile _file;
    readonly IAnimalWorld _world;
    readonly Random _rng;

    /// <summary>Who may follow whom. Idle is the hub: an animal stands, then eats or walks, and goes back to standing; a threat (or a hit that did not kill) sends any living state to Flee; Flee only ends by calming down into Idle.</summary>
    public static readonly StateMachineSpec<AnimalState> Spec = StateMachineSpec<AnimalState>.Define(
            "Animal.Behaviour", "What an animal is doing: stand, eat, wander, sit, bark, run from a threat or a shot, lie dead until the corpse is removed.", AnimalState.Idle)
        .Allow(AnimalState.Idle, AnimalState.Eat, AnimalState.Walk, AnimalState.Sit, AnimalState.Bark, AnimalState.Flee, AnimalState.Dead)
        .Allow(AnimalState.Eat, AnimalState.Idle, AnimalState.Flee, AnimalState.Dead)
        .Allow(AnimalState.Sit, AnimalState.Idle, AnimalState.Flee, AnimalState.Dead)
        .Allow(AnimalState.Bark, AnimalState.Idle, AnimalState.Flee, AnimalState.Dead)
        .Allow(AnimalState.Walk, AnimalState.Idle, AnimalState.Flee, AnimalState.Dead)
        .Allow(AnimalState.Flee, AnimalState.Idle, AnimalState.Dead)
        .Note(AnimalState.Idle, "Stands for a random while (clock: idle seconds), then Choose: sit or bark (species that opt in), eat, or walk to a spot near home.")
        .Note(AnimalState.Eat, "Grazes (clock: eat seconds), then stands.")
        .Note(AnimalState.Sit, "Sits for a random while (clock: sit seconds; the client plays sit-down, holds, stands up after), then stands. Species with a sit rule only.")
        .Note(AnimalState.Bark, "Barks once (clock: the bark clip's length), then stands. Species with a bark rule only.")
        .Note(AnimalState.Walk, "Walks to a wander target (clock: the time budget to arrive, then it gives up and stands).")
        .Note(AnimalState.Flee, "Runs from the threat or the shooter; no clock of its own (calm and alarm timers run beside). Calms down into Idle.")
        .Note(AnimalState.Dead, "Lies for corpse_seconds (the clock's elapsed time), then the entity ends; a one-hit species' entity ends at once. Never left.")
        .Build();

    // The machine's clock is the behaviour timer: idle and eat count the stand/graze seconds down, walk its time budget to arrive, dead how long it has lain
    // (Elapsed). Flee has none. Entering a state and starting its clock is one call, so no state can inherit another's leftover.
    readonly StateMachine<AnimalState> _fsm = new(Spec);

    float _calm;           // seconds without a threat while fleeing
    float _alarm;          // seconds left of running from _alarmFrom (a shot that hurt it)
    Vector3 _alarmFrom;
    Vector3 _fleeFrom;
    float _bias;           // radians added to the flight heading to go round what blocked the way
    Vector3 _target;       // the wander target
    bool _hitToggle;

    public Animal(AnimalsFile file, int species, Vector3 home, float yaw, IAnimalWorld world, int seed, int variant = 0)
    {
        Variant = variant;
        _file = file;
        Species = species;
        Def = file.Animals[species];
        _world = world;
        _rng = new Random(seed);
        Home = home;
        Position = _world.GroundY(home.X, home.Z) is { } y ? new Vector3(home.X, y, home.Z) : home;
        Yaw = yaw;
        Hp = Def.Hp;
        _fsm.Restart(Between(file.IdleMinSeconds, file.IdleMaxSeconds));
    }

    public int Species { get; }
    /// <summary>Which model of the species it wears (0 the base model); see <see cref="AnimalDef.ModelOf"/>.</summary>
    public int Variant { get; }
    public AnimalDef Def { get; }
    /// <summary>Where it spawned: the centre of its wandering.</summary>
    public Vector3 Home { get; }
    public Vector3 Position { get; private set; }
    /// <summary>Godot convention: 0 faces -Z.</summary>
    public float Yaw { get; private set; }
    public float Hp { get; private set; }
    public AnimalState State => _fsm.Current;
    public bool Alive => State != AnimalState.Dead;
    /// <summary>The player whose hit killed it (0 until then, and for a death with no shooter).</summary>
    public ushort Killer { get; private set; }

    /// <summary>Raised once, when the hit that took the last hit points lands: the animal and the player who shot it. The server turns it into a kill credit.</summary>
    public event Action<Animal, ushort>? Died;

    public byte TypeId => AnimalRules.TypeId;
    /// <summary>Animals belong to no player; the species index is in the replicated state's owner field.</summary>
    public ushort Owner => 0;
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>The body box [width, height, length]: its variant's own, or the species'.</summary>
    public IReadOnlyList<float> HitBox => Def.HitBoxOf(Variant);

    /// <summary>The body box for hit tests: its centre, half the width, height and length, and the yaw it is turned by (the box's length lies along the heading).</summary>
    public (Vector3 Centre, Vector3 Half, float Yaw) Bounds
    {
        get
        {
            var b = HitBox;
            return (Position + Vector3.UnitY * (b[1] / 2), new Vector3(b[0], b[1], b[2]) / 2, Yaw);
        }
    }

    /// <summary>Applies damage (already scaled by the weapon, headshots and range). A dead animal takes none.</summary>
    public void OnDamage(float amount, ushort attacker)
    {
        if (Ended != null || !Alive || !(amount > 0)) return;
        Hp = Def.OneHit ? 0 : MathF.Max(0, Hp - amount);
        if (Hp <= 0)
        {
            _fsm.Enter(AnimalState.Dead);
            Killer = attacker;
            if (Def.OneHit) Ended = WorldDespawnReason.Destroyed; // no corpse: gone now, the clients show a puff where it stood
            Died?.Invoke(this, attacker);
            return;
        }
        _hitToggle = !_hitToggle;
        if (!Def.Flees) return; // a pet shrugs it off: the hit reaction plays, no alarm, no flight
        // Run from where the shot came from; when the shooter is gone, from behind (the animal bolts the way it faces).
        _alarmFrom = _world.PositionOf(attacker) ?? Position - Forward * 5;
        _alarm = _file.AlarmSeconds;
        _fleeFrom = _alarmFrom;
        _calm = 0;
        StartFleeing(startOver: true);
    }

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        if (!Alive)
        {
            _fsm.Tick(dt);
            if (_fsm.Elapsed >= _file.CorpseSeconds) Ended = WorldDespawnReason.Expired;
            return;
        }

        var threat = Def.Flees ? _world.NearestThreat(Position, Def.FleeRadius) : null;
        if (_alarm > 0)
        {
            _alarm -= dt;
            threat ??= _alarmFrom;
        }
        if (threat is { } t)
        {
            _fleeFrom = t;
            _calm = 0;
            StartFleeing(startOver: false);
        }

        switch (State)
        {
            case AnimalState.Flee: RunAway(dt, threat is not null); break;
            case AnimalState.Walk: Walk(dt); break;
            default: Rest(dt); break;
        }
    }

    public WorldState Serialize()
    {
        var state = (byte)((byte)State | (_hitToggle ? AnimalRules.HitToggle : 0));
        // a living animal never shows 0 hp (a sliver of health rounds up)
        return WorldState.Make(Position, Yaw, 0, AnimalRules.Look(Species, Variant), state, Alive ? MathF.Ceiling(Hp) : 0);
    }

    // ---- behaviour ----

    Vector3 Forward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));

    float Between(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    /// <summary>Goes to <paramref name="s"/> with its clock (<paramref name="seconds"/>) and a straight heading (<c>_bias</c> only matters while fleeing).</summary>
    void Go(AnimalState s, float seconds = float.PositiveInfinity)
    {
        _bias = 0;
        _fsm.Enter(s, seconds);
    }

    /// <summary>Starts running. A hit while already fleeing starts the heading over (<paramref name="startOver"/>); a threat seen while already fleeing changes nothing.</summary>
    void StartFleeing(bool startOver)
    {
        if (startOver) _bias = 0;
        if (State != AnimalState.Flee) Go(AnimalState.Flee);
    }

    /// <summary>Idle, eat, sit and bark: wait out the timer; standing then decides what to do next, anything else goes back to standing.</summary>
    void Rest(float dt)
    {
        if (!_fsm.Tick(dt)) return;
        if (State != AnimalState.Idle)
        {
            StandStill();
            return;
        }
        Choose();
    }

    /// <summary>True with chance <paramref name="p"/>; no chance (a species without the behaviour) draws nothing, so the others' random sequence does not change.</summary>
    bool Roll(float? p) => p > 0 && _rng.NextDouble() < p;

    /// <summary>After standing: walk back home when it strayed, otherwise sit, bark, eat or walk to a spot near home. Stands on when no spot has ground.</summary>
    void Choose()
    {
        var strayed = HorizontalDistance(Position, Home) > Def.WanderRadius;
        if (!strayed && Def.Sit is { } sit && Roll(sit.Chance))
        {
            Go(AnimalState.Sit, Between(sit.MinSeconds, sit.MaxSeconds));
            return;
        }
        if (!strayed && Def.Bark is { } bark && Roll(bark.Chance))
        {
            Go(AnimalState.Bark, bark.Seconds);
            return;
        }
        if (!strayed && _rng.NextDouble() < _file.EatChance)
        {
            Go(AnimalState.Eat, Between(_file.EatMinSeconds, _file.EatMaxSeconds));
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
            Go(AnimalState.Walk, HorizontalDistance(Position, _target) / Def.WalkSpeed * 2 + 3); // twice the straight walk and a margin; then it gives up
            return;
        }
        StandStill(); // no spot has ground: stands on, with a fresh idle clock
    }

    void Walk(float dt)
    {
        if (_fsm.Tick(dt) || HorizontalDistance(Position, _target) < 0.6f) { StandStill(); return; }
        if (!Step(TurretMath.YawTo(Position, _target), Def.WalkSpeed, dt)) StandStill();
    }

    void RunAway(float dt, bool threatened)
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

    /// <summary>Back to standing with a fresh idle clock (also when already standing: the clock restarts).</summary>
    void StandStill()
    {
        var seconds = Between(_file.IdleMinSeconds, _file.IdleMaxSeconds);
        if (!_fsm.Is(AnimalState.Idle)) Go(AnimalState.Idle, seconds);
        else _fsm.Restart(seconds);
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
