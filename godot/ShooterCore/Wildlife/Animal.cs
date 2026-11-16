using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What an animal is doing; the low four bits of its replicated state byte (the clients pick the clip from it).</summary>
public enum AnimalState : byte { Idle = 0, Eat = 1, Walk = 2, Flee = 3, Dead = 4, Sit = 5, Bark = 6, Carried = 7, Thrown = 8, Down = 9 }

/// <summary>Animal world entity rules: the wire type id and the state byte the clients read.</summary>
public static class AnimalRules
{
    /// <summary>Wire id of the animal world entity type (1 corpse, 10 missile, 20 turret, 30 care package, 40 aircraft).</summary>
    public const byte TypeId = 50;

    /// <summary>State byte: bits 0-3 the <see cref="AnimalState"/>, bit 4 flips on every hit that did not kill (a client plays its hit reaction when the bit changes), bits 5-7 the size step (<see cref="SizeOfStep"/>; constant for the animal's life).</summary>
    public const byte StateMask = 15, HitToggle = 16, SizeShift = 5;

    /// <summary>Size steps the state byte carries (0 .. <see cref="SizeSteps"/> - 1), and the scale of the smallest and the largest.</summary>
    public const int SizeSteps = 8;
    public const float MinSize = 0.7f, MaxSize = 1.4f;

    /// <summary>The weight roll of a new animal, 0..1: the mean of two uniform draws (a triangle), so it leans to the middle but the big and the small ones are not rare. Draws exactly two values from <paramref name="rng"/>.</summary>
    public static float RollSize(Random rng) => (float)((rng.NextDouble() + rng.NextDouble()) / 2.0);

    /// <summary>The size step of a roll: 0 .. <see cref="SizeSteps"/> - 1.</summary>
    public static int StepOf(float roll) => Math.Clamp((int)MathF.Round(Math.Clamp(roll, 0f, 1f) * (SizeSteps - 1)), 0, SizeSteps - 1);

    /// <summary>The scale (1 = the species' model and boxes as measured) of a size step.</summary>
    public static float SizeOfStep(int step) => MinSize + (MaxSize - MinSize) * Math.Clamp(step, 0, SizeSteps - 1) / (SizeSteps - 1);

    /// <summary>The size step in a state byte (what a client scales the model by, for a species with a weight).</summary>
    public static int SizeStepOf(byte state) => state >> SizeShift;

    /// <summary>The weight in kg of a roll within a species' range [min, max].</summary>
    public static float WeightOf(IReadOnlyList<float> range, float roll) => range[0] + (range[1] - range[0]) * Math.Clamp(roll, 0f, 1f);

    /// <summary>The owner field of an animal's replicated state: the species index in the low byte, the variant index (which model of the species) in the high byte.</summary>
    public static ushort Look(int species, int variant) => (ushort)(species | variant << 8);
    public static int SpeciesOf(ushort look) => look & 0xFF;
    public static int VariantOf(ushort look) => look >> 8;

    public static AnimalState StateOf(byte state) => (AnimalState)(state & StateMask);
    public static bool HitToggled(byte state) => (state & HitToggle) != 0;

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "animal", HasHp: true, Interest: true));
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
/// An <see cref="IAnimalWorld"/> that keeps its animal inside an area (<see cref="PennedWorld"/>): it can say whether a point is inside and give the open ground it is cut from. A pet that is put down outside
/// its area (a Thief let go of it) walks home on the open ground until it is inside again.
/// </summary>
public interface IPennedWorld : IAnimalWorld
{
    bool Inside(float x, float z);
    /// <summary>The same ground with no area: where a pet that was carried out of its pen may walk.</summary>
    IAnimalWorld Open { get; }
}

/// <summary>
/// An animal (ambient wildlife, livestock or a pet). It stands, eats, wanders around the spot it spawned at, runs from threats
/// and from the shot that hurt it (a species with <see cref="AnimalDef.Flees"/> off, like a pet, never runs), now and then sits or barks (species that opt in with
/// <see cref="AnimalDef.Sit"/>, <see cref="AnimalDef.Bark"/>), is thrown by a vehicle that runs it over (<see cref="Throw"/>: it flies, lies knocked down, gets up and runs), and dies when its hit points run out: it then lies for
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
        .Allow(AnimalState.Idle, AnimalState.Eat, AnimalState.Walk, AnimalState.Sit, AnimalState.Bark, AnimalState.Flee, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Eat, AnimalState.Idle, AnimalState.Flee, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Sit, AnimalState.Idle, AnimalState.Flee, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Bark, AnimalState.Idle, AnimalState.Flee, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Walk, AnimalState.Idle, AnimalState.Flee, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Flee, AnimalState.Idle, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Carried, AnimalState.Idle, AnimalState.Dead)
        .Allow(AnimalState.Thrown, AnimalState.Down, AnimalState.Dead)
        .Allow(AnimalState.Down, AnimalState.Flee, AnimalState.Idle, AnimalState.Thrown, AnimalState.Dead)
        .Note(AnimalState.Idle, "Stands for a random while (clock: idle seconds), then Choose: sit or bark (species that opt in), eat, or walk to a spot near home.")
        .Note(AnimalState.Eat, "Grazes (clock: eat seconds), then stands.")
        .Note(AnimalState.Sit, "Sits for a random while (clock: sit seconds; the client plays sit-down, holds, stands up after), then stands. Species with a sit rule only.")
        .Note(AnimalState.Bark, "Barks once (clock: the bark clip's length), then stands. Species with a bark rule only.")
        .Note(AnimalState.Walk, "Walks to a wander target (clock: the time budget to arrive, then it gives up and stands).")
        .Note(AnimalState.Flee, "Runs from the threat or the shooter; no clock of its own (calm and alarm timers run beside). Calms down into Idle.")
        .Note(AnimalState.Carried, "A Thief of the Thieves event holds it (no clock: it is moved by the Thief, MoveCarried, and does nothing itself). Let go (Release) it stands; one put down outside its Pen walks home over open ground until it is inside again. Still a hit target: any hit that deals damage kills it.")
        .Note(AnimalState.Thrown, "A vehicle ran it over and it survived (Throw): it flies on the arc the vehicle gave it; no clock. Landing puts it Down. Still a hit target; another vehicle only changes the arc.")
        .Note(AnimalState.Down, "Lies knocked down where it landed (clock: knockdown_seconds), then gets up: Flee from where the vehicle was (a species that does not flee stands). Run over again it is Thrown again.")
        .Note(AnimalState.Dead, "A body a vehicle threw flies on first. Lies for corpse_seconds (the clock's elapsed time), then the entity ends; a one-hit species' entity ends at once. Never left.")
        .Build();

    // The machine's clock is the behaviour timer: idle and eat count the stand/graze seconds down, walk its time budget to arrive, dead how long it has lain
    // (Elapsed). Flee has none. Entering a state and starting its clock is one call, so no state can inherit another's leftover.
    readonly StateMachine<AnimalState> _fsm = new(Spec);

    Lifetime _calm;        // seconds without a threat while fleeing (count-up; Age >= CalmSeconds ends the flight)
    // Seconds left of running from _alarmFrom (a shot that hurt it). Only ticks in the free-roaming part of Tick: it FREEZES while the animal is
    // Carried / Thrown / Down / Dead (those branches return before it), so a stunned animal still runs from the shot once it gets up.
    Countdown _alarm;
    Vector3 _alarmFrom;
    Vector3 _fleeFrom;
    float _bias;           // radians added to the flight heading to go round what blocked the way
    IAnimalWorld? _open;   // a pet put down outside its pen walks home over this open ground; null = it stays in its own world
    Vector3 _target;       // the wander target
    Vector3? _arc;         // the velocity of the arc a vehicle threw it on (Thrown, or a body killed by the same hit); null on the ground
    float _floorY;         // the last ground height it flew over, to land on where there is none (water, the terrain's rim)
    bool _hitToggle;

    /// <param name="sizeRoll">The size and weight roll, 0..1 (<see cref="AnimalRules.RollSize"/>); only a species with <see cref="AnimalDef.WeightKg"/> has a size.</param>
    public Animal(AnimalsFile file, int species, Vector3 home, float yaw, IAnimalWorld world, int seed, int variant = 0, float sizeRoll = 0.5f)
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
        if (Def.WeightKgOf(variant) is { } range)
        {
            SizeStep = AnimalRules.StepOf(sizeRoll);
            Size = AnimalRules.SizeOfStep(SizeStep);
            WeightKg = AnimalRules.WeightOf(range, sizeRoll);
        }
        var box = Def.HitBoxOf(variant);
        HitBox = Size == 1f ? box : new[] { box[0] * Size, box[1] * Size, box[2] * Size };
        // a bigger animal has more hit points, as its volume; a one-hit species has none to scale, and the hp byte of the wire holds 255
        MaxHp = Def.OneHit ? Def.Hp : MathF.Min(255, MathF.Round(Def.Hp * Size * Size * Size));
        Hp = MaxHp;
        _fsm.Restart(Between(file.IdleMinSeconds, file.IdleMaxSeconds));
    }

    public int Species { get; }
    /// <summary>Which model of the species it wears (0 the base model); see <see cref="AnimalDef.ModelOf"/>.</summary>
    public int Variant { get; }
    public AnimalDef Def { get; }
    /// <summary>Where it spawned: the centre of its wandering.</summary>
    public Vector3 Home { get; }
    /// <summary>The herd it belongs to (set by the keeper right after spawning): it wanders about the herd's anchor instead of its own home and the herd takes fright together. Null: it is on its own.</summary>
    public Herd? Herd { get; internal set; }
    public Vector3 Position { get; private set; }
    /// <summary>Godot convention: 0 faces -Z.</summary>
    public float Yaw { get; private set; }
    public float Hp { get; private set; }
    public AnimalState State => _fsm.Current;
    /// <summary>Seconds since the state began (or was begun again): how far into the state's clip a client is, less the delay it sees the animal with.</summary>
    public float StateSeconds => _fsm.Elapsed;
    public bool Alive => State != AnimalState.Dead;
    /// <summary>The player whose hit killed it (0 until then, and for a death with no shooter).</summary>
    public ushort Killer { get; private set; }

    /// <summary>Raised once, when the hit that took the last hit points lands: the animal and the player who shot it. The server turns it into a kill credit.</summary>
    public event Action<Animal, ushort>? Died;

    public byte TypeId => AnimalRules.TypeId;
    /// <summary>Animals belong to no player; the species index is in the replicated state's owner field.</summary>
    public ushort Owner => 0;
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>Its size step (0 .. 7, the state byte's bits 5-7) and the scale it stands for: 1 for a species with no weight.</summary>
    public int SizeStep { get; }
    public float Size { get; } = 1f;
    /// <summary>What it weighs, kg; 0 for a species with no weight. Rolled with the size and kept on the server (the hunt shot tells the shooter).</summary>
    public float WeightKg { get; }
    /// <summary>Its hit points at full health: the species' scaled by its size (a one-hit species: as they are).</summary>
    public float MaxHp { get; }

    /// <summary>The body box [width, height, length]: its variant's own, or the species', times its <see cref="Size"/>.</summary>
    public IReadOnlyList<float> HitBox { get; }

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
            if (Def.Flees) Herd?.Alarm(this, _world.PositionOf(attacker) ?? Position - Forward * 5); // the rest of the herd bolts from the shooter
            Died?.Invoke(this, attacker);
            return;
        }
        _hitToggle = !_hitToggle;
        if (!Def.Flees || State is AnimalState.Carried or AnimalState.Thrown or AnimalState.Down) return; // a pet shrugs it off: the hit reaction plays, no alarm, no flight
        // Run from where the shot came from; when the shooter is gone, from behind (the animal bolts the way it faces).
        _alarmFrom = _world.PositionOf(attacker) ?? Position - Forward * 5;
        _alarm.Start(_file.AlarmSeconds);
        _fleeFrom = _alarmFrom;
        _calm.Reset();
        StartFleeing(startOver: true);
    }

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        if (!Alive)
        {
            Fly(dt);
            _fsm.Tick(dt);
            if (_fsm.Elapsed >= _file.CorpseSeconds) Ended = WorldDespawnReason.Expired;
            return;
        }
        if (State == AnimalState.Carried) return; // held: the Thief moves it
        if (State == AnimalState.Thrown) { if (Fly(dt)) Go(AnimalState.Down, _file.KnockdownSeconds); return; }
        if (State == AnimalState.Down) { if (_fsm.Tick(dt)) GetUp(); return; }
        if (_open != null && _world is IPennedWorld pen && pen.Inside(Position.X, Position.Z)) _open = null; // back inside: its own world again

        var threat = Def.Flees ? _world.NearestThreat(Position, Def.FleeRadius) : null;
        if (_alarm.Running)
        {
            _alarm.Tick(dt);
            threat ??= _alarmFrom;
        }
        if (threat is { } t)
        {
            _fleeFrom = t;
            _calm.Reset();
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
        var state = (byte)((byte)State | (_hitToggle ? AnimalRules.HitToggle : 0) | SizeStep << AnimalRules.SizeShift);
        // a living animal never shows 0 hp (a sliver of health rounds up)
        return WorldState.Make(Position, Yaw, 0, AnimalRules.Look(Species, Variant), state, Alive ? MathF.Ceiling(Hp) : 0);
    }

    // ---- run over ----

    /// <summary>
    /// A vehicle ran it over: it leaves the ground at <paramref name="velocity"/> and flies (<see cref="AnimalState.Thrown"/>), then lies knocked down and
    /// gets up running from <paramref name="from"/> (where the vehicle was); its herd bolts at once. The damage is dealt apart (<see cref="OnDamage"/>):
    /// a body the hit kills keeps the arc. False for one that is dead, gone, held, or of a one-hit species (it has no body to throw).
    /// </summary>
    public bool Throw(Vector3 velocity, Vector3 from)
    {
        if (Ended != null || !Alive || State == AnimalState.Carried || Def.OneHit) return false;
        _arc = velocity;
        _floorY = Position.Y;
        _alarmFrom = from;
        if (State != AnimalState.Thrown) Go(AnimalState.Thrown);
        if (Def.Flees) Herd?.Alarm(this, from);
        return true;
    }

    /// <summary>One step of the arc. True once it is on the ground (also when it was never thrown). Where there is no ground under the arc it drops straight down.</summary>
    bool Fly(float dt)
    {
        if (_arc is not { } v) return true;
        v.Y -= MoveConsts.Gravity * dt;
        var next = Position + v * dt;
        var world = _open ?? _world;
        if (world.GroundY(next.X, next.Z) is { } ground) _floorY = ground;
        else { next.X = Position.X; next.Z = Position.Z; v.X = 0; v.Z = 0; }
        _arc = v;
        if (next.Y > _floorY) { Position = next; return false; }
        Position = new Vector3(next.X, _floorY, next.Z);
        if (v.Y > 0) return false; // the ground rose under it while it was still going up: it skims on
        _arc = null;
        return true;
    }

    /// <summary>The knockdown is over: it runs from where the vehicle was (a species that does not flee stands).</summary>
    void GetUp()
    {
        if (!Def.Flees) { StandStill(); return; }
        _fleeFrom = _alarmFrom;
        _alarm.Start(_file.AlarmSeconds);
        _calm.Reset();
        Go(AnimalState.Flee);
    }

    // ---- carried (the Thieves event) ----

    /// <summary>A Thief takes it up: from any living state, it does nothing until <see cref="Release"/>. False when it is dead, gone or already held.</summary>
    public bool Carry()
    {
        if (Ended != null || !Alive || State == AnimalState.Carried) return false;
        _open = null;
        Go(AnimalState.Carried);
        return true;
    }

    /// <summary>The Thief moves it: where it is held now and which way it faces. Nothing when it is not held.</summary>
    public void MoveCarried(Vector3 position, float yaw)
    {
        if (State != AnimalState.Carried) return;
        Position = position;
        Yaw = yaw;
    }

    /// <summary>
    /// Let go (the Thief was killed, or its Raid was cancelled): it stands where it is on the ground. Put down outside its pen (<see cref="IPennedWorld"/>) it walks straight home over open ground and is penned again once
    /// inside; a spot with no ground at all (water, a wall) sends it home at once. False when it was not held.
    /// </summary>
    public bool Release()
    {
        if (State != AnimalState.Carried || Ended != null) return false;
        var pen = _world as IPennedWorld;
        var ground = pen?.Open ?? _world;
        if (ground.GroundY(Position.X, Position.Z) is { } y)
        {
            Position = new Vector3(Position.X, y, Position.Z);
            if (pen != null && !pen.Inside(Position.X, Position.Z)) _open = ground;
        }
        else Position = _world.GroundY(Home.X, Home.Z) is { } hy ? new Vector3(Home.X, hy, Home.Z) : Home;
        StandStill();
        if (_open != null) GoHome();
        return true;
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
        if (State == AnimalState.Flee) return;
        Go(AnimalState.Flee);
        Herd?.Alarm(this, _fleeFrom); // a herd runs together: the others take fright from the same spot
    }

    /// <summary>Another animal of the herd took fright (<see cref="Herd.Alarm"/>): runs from the same spot for a while, without alarming the herd again.</summary>
    internal void Startle(Vector3 from)
    {
        if (Ended != null || !Alive || !Def.Flees || State is AnimalState.Flee or AnimalState.Thrown or AnimalState.Down or AnimalState.Carried) return; // one in the air, knocked down or held is not startled out of it (it runs when it gets up)
        _alarmFrom = from;
        _fleeFrom = from;
        _alarm.Start(_file.AlarmSeconds);
        _calm.Reset();
        Go(AnimalState.Flee);
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
        if (_open != null) { GoHome(); return; } // outside its pen: straight home first
        var centre = Herd?.Anchor ?? Home;
        var radius = Herd != null ? MathF.Max(Herd.Spread, _file.WanderMinM + 1) : Def.WanderRadius; // a herd member stays close to the herd, not to the species' whole range
        var strayed = HorizontalDistance(Position, centre) > radius;
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
            var reach = strayed ? Between(0, radius * 0.25f) : Between(_file.WanderMinM, radius);
            var x = centre.X + MathF.Cos(angle) * reach;
            var z = centre.Z + MathF.Sin(angle) * reach;
            if (_world.GroundY(x, z) is not { } y) continue;
            _target = new Vector3(x, y, z);
            Go(AnimalState.Walk, HorizontalDistance(Position, _target) / Def.WalkSpeed * 2 + 3); // twice the straight walk and a margin; then it gives up
            return;
        }
        StandStill(); // no spot has ground: stands on, with a fresh idle clock
    }

    void Walk(float dt)
    {
        if (_fsm.Tick(dt) || HorizontalDistance(Position, _target) < 0.6f) { if (_open != null) GoHome(); else StandStill(); return; }
        if (!Step(TurretMath.YawTo(Position, _target), Def.WalkSpeed, dt)) StandStill();
    }

    /// <summary>Walks to <see cref="Home"/> in a straight line over open ground (a pet that was carried out of its pen).</summary>
    void GoHome()
    {
        _target = _world.GroundY(Home.X, Home.Z) is { } y ? new Vector3(Home.X, y, Home.Z) : Home;
        if (_fsm.Is(AnimalState.Walk)) _fsm.Restart(HorizontalDistance(Position, _target) / Def.WalkSpeed * 2 + 3);
        else Go(AnimalState.Walk, HorizontalDistance(Position, _target) / Def.WalkSpeed * 2 + 3);
    }

    void RunAway(float dt, bool threatened)
    {
        if (!threatened)
        {
            if (_calm.Tick(dt, _file.CalmSeconds)) { StandStill(); return; }
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
        if ((_open ?? _world).GroundY(x, z) is not { } y || MathF.Abs(y - Position.Y) > _file.MaxStepM) return false;
        Position = new Vector3(x, y, z);
        return true;
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
