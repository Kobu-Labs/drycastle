using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What an animal is doing; the low four bits of its replicated state byte (the clients pick the clip from it).</summary>
public enum AnimalState : byte { Idle = 0, Eat = 1, Walk = 2, Flee = 3, Dead = 4, Sit = 5, Bark = 6, Carried = 7, Thrown = 8, Down = 9, Vanish = 10 }

/// <summary>Animal world entity rules: the wire type id and the state byte the clients read.</summary>
public static class AnimalRules
{
    /// <summary>Wire id of the animal world entity type (1 corpse, 10 missile, 20 turret, 30 care package, 40 aircraft).</summary>
    public const byte TypeId = 50;

    /// <summary>State byte: bits 0-3 the <see cref="AnimalState"/>, bit 4 flips on every hit that did not kill (a client plays its hit reaction when the bit changes), bits 5-7 the size step (<see cref="SizeOfStep"/>; constant for the animal's life).</summary>
    public const byte StateMask = 15, HitToggle = 16, SizeShift = 5;

    /// <summary>Size steps the state byte carries (0 .. <see cref="SizeSteps"/> - 1), and the scale of the smallest and the largest.</summary>
    /// <summary>Metres between two replicated positions of an animal above which it was teleported (<see cref="Animal.Teleport"/>), not moved: the client's buffer starts over so it is not drawn sliding there. The fastest thing an animal does is a thrown body.</summary>
    public const float TeleportM = 40f;

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

    /// <summary>
    /// Whether an animal in this state stands where it is, and so is a body a player on foot cannot walk through: idle, eating, sitting, barking. One that walks or runs is not (the server and a
    /// client would see it in different places a moment apart, and the player would be stopped by a body that is no longer there); it stops or steers for a player itself.
    /// </summary>
    public static bool Stands(AnimalState state) => state is AnimalState.Idle or AnimalState.Eat or AnimalState.Sit or AnimalState.Bark or AnimalState.Vanish;

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

    /// <summary>
    /// Whether a player is near enough to <paramref name="at"/> for any client to be sent the animal (<see cref="WorldInterest.LeaveM"/>, ADR 0025).
    /// An unwatched animal that is standing, eating or walking is not simulated: it stays as it is until a player comes near again (#280).
    /// </summary>
    bool Watched(Vector3 at) => true;

    /// <summary>
    /// How far a ray at <paramref name="height"/> metres above the ground runs from <paramref name="from"/> (a point on the ground, the front of the animal)
    /// along <paramref name="dir"/> (flat, unit) before it meets something solid: a building, fence, placed solid, trunk, closed door or a vehicle's solid box
    /// (the rule row of <see cref="MoverKind.Animal"/>; players are not asked, see <see cref="PlayersNear"/>). Infinity when nothing is within <paramref name="maxM"/>.
    /// One physics ray: the animal asks rarely (<see cref="ClearPath"/>). The default world has nothing solid.
    /// </summary>
    float SolidAhead(Vector3 from, Vector3 dir, float maxM, float height) => float.PositiveInfinity;

    /// <summary>
    /// Whether an animal's body of <paramref name="radius"/> metres may be at (x, z): there is ground and nothing solid in the way (a spawn point or a
    /// wander target inside a building is not). The default is the ground alone.
    /// </summary>
    bool Fits(float x, float z, float radius) => GroundY(x, z) != null;

    /// <summary>The rectangle a rare species (<see cref="RareSpecies"/>) may spawn in: the map's box (ground axes, x east, z = -north). Null: the world has none, so no rare animal spawns.</summary>
    FlightBounds? MapBounds => null;

    /// <summary>
    /// Flat distance from (x, z) to the nearest building, metres; <see cref="float.PositiveInfinity"/> when none is within <paramref name="maxM"/> (a caller that wants "at least m from every building" asks with m).
    /// A point inside a building is 0. The default world has no buildings.
    /// </summary>
    float NearestBuildingM(float x, float z, float maxM) => float.PositiveInfinity;

    /// <summary>Appends the position of every living player on foot within <paramref name="radius"/> of <paramref name="at"/> (flat) to <paramref name="into"/>: the circles an animal stops for. A player is never pushed.</summary>
    void PlayersNear(Vector3 at, float radius, List<Vector3> into) { }
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
            "Animal.Behaviour", "What an animal is doing: stand, eat, wander, sit, bark, run from a threat or a shot (a vanishing species turns immune and is teleported instead), lie dead until the corpse is removed.", AnimalState.Idle)
        .Allow(AnimalState.Idle, AnimalState.Eat, AnimalState.Walk, AnimalState.Sit, AnimalState.Bark, AnimalState.Flee, AnimalState.Vanish, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Eat, AnimalState.Idle, AnimalState.Flee, AnimalState.Vanish, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Sit, AnimalState.Idle, AnimalState.Flee, AnimalState.Vanish, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Bark, AnimalState.Idle, AnimalState.Flee, AnimalState.Vanish, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Walk, AnimalState.Idle, AnimalState.Flee, AnimalState.Vanish, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Flee, AnimalState.Idle, AnimalState.Vanish, AnimalState.Carried, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Carried, AnimalState.Idle, AnimalState.Dead)
        .Allow(AnimalState.Thrown, AnimalState.Down, AnimalState.Dead)
        .Allow(AnimalState.Down, AnimalState.Flee, AnimalState.Idle, AnimalState.Vanish, AnimalState.Thrown, AnimalState.Dead)
        .Allow(AnimalState.Vanish, AnimalState.Idle)
        .Note(AnimalState.Idle, "Stands for a random while (clock: idle seconds), then Choose: sit or bark (species that opt in), eat, or walk to a spot near home.")
        .Note(AnimalState.Eat, "Grazes (clock: eat seconds), then stands.")
        .Note(AnimalState.Sit, "Sits for a random while (clock: sit seconds; the client plays sit-down, holds, stands up after), then stands. Species with a sit rule only.")
        .Note(AnimalState.Bark, "Barks once (clock: the bark clip's length), then stands. Species with a bark rule only.")
        .Note(AnimalState.Walk, "Walks to a wander target (clock: the time budget to arrive, then it gives up and stands).")
        .Note(AnimalState.Flee, "Runs from the threat or the shooter; no clock of its own (calm and alarm timers run beside). Calms down into Idle.")
        .Note(AnimalState.Carried, "A Thief of the Thieves event holds it (no clock: it is moved by the Thief, MoveCarried, and does nothing itself). Let go (Release) it stands; one put down outside its Pen walks home over open ground until it is inside again. Still a hit target: any hit that deals damage kills it.")
        .Note(AnimalState.Thrown, "A vehicle ran it over and it survived (Throw): it flies on the arc the vehicle gave it, then rolls on along the ground with part of that speed; no clock. Coming to rest puts it Down. Still a hit target; another vehicle only changes the arc.")
        .Note(AnimalState.Down, "Lies knocked down where it landed (clock: knockdown_seconds), then gets up: Flee from where the vehicle was (a species that does not flee stands). Run over again it is Thrown again.")
        .Note(AnimalState.Vanish, "A species with vanish_seconds is startled (threat, non-lethal hit, herd alarm, getting up after a run-over) and does not run: immune for vanish_seconds (clock; no damage, no throw, no carry, not a hit target), the client spirals rainbow round it, then the Vanished event lets the keeper pick a place at least vanish_min_m away and Teleport puts it there Idle. Only Idle follows; never Dead, Thrown or Carried. No startle for vanish_grace_seconds after it reappears.")
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
    float _bias;           // radians added to the heading to go round what blocked the way
    Countdown _steer;      // the window after a block in which a second block is not a new one (the turn is still under way); in a walk it also ends the detour
    ClearPath _clear;      // the straight run the last ray found free (or blocked): no ray while it holds (ADR 0028 cost)
    Countdown _recheck;    // how long a blocked answer is kept before the way is asked again
    readonly float _bodyW, _bodyL, _bodyAhead, _bodyH; // the Body box (variant's size, species' offset), metres
    IAnimalWorld? _open;   // a pet put down outside its pen walks home over this open ground; null = it stays in its own world
    Vector3 _target;       // the wander target
    Vector3? _arc;         // the velocity of the arc a vehicle threw it on (Thrown, or a body killed by the same hit); null on the ground
    float _floorY;         // the last ground height it flew over, to land on where there is none (water, the terrain's rim)
    bool _rolling;         // the thrown body has come down and rolls on along the ground (ThrownRoll); false in the air
    bool _hitToggle;
    Countdown _grace;      // after a vanish: no startle (no new vanish) until it runs out, so the animal is seen where it arrived

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
        (_bodyW, _bodyH, _bodyL, _bodyAhead) = (HitBox[0], HitBox[1], HitBox[2], Def.BodyAheadOf(variant) * Size); // the Body box grows with the animal, as its Hit shape does
        BodyReach = MathF.Sqrt(_bodyW * _bodyW + _bodyL * _bodyL) / 2 + MathF.Abs(_bodyAhead);
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
    /// <summary>Every animal of the match (set by <see cref="AnimalCrowd.Add"/>): who it runs into. Null: it meets no other animal.</summary>
    public AnimalCrowd? Crowd { get; internal set; }
    /// <summary>Metres from its position to the farthest point of its Body box: the cheap distance that rules out an animal that is far.</summary>
    public float BodyReach { get; }
    /// <summary>Whether another animal stops for this one: it stands on the ground. One that lies, flies, is held or is dead is no obstacle.</summary>
    public bool Obstructs => Ended == null && State is not (AnimalState.Dead or AnimalState.Thrown or AnimalState.Down or AnimalState.Carried);
    /// <summary>Whether it stands where it is (<see cref="AnimalRules.Stands"/>): then a player on foot cannot walk through its Body box.</summary>
    public bool Stands => Ended == null && AnimalRules.Stands(State);
    /// <summary>Its Body box now (ADR 0028): the footprint contact with a player or another animal is tested against.</summary>
    public BodyBox Body => BodyBox.Of(Position, Yaw, _bodyW, _bodyL, _bodyAhead);
    /// <summary>Radius of the sphere that asks the world whether it fits at a spot.</summary>
    public float BodyRadius => Def.BodyRadiusOf(Variant);
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

    /// <summary>
    /// Raised once when the clock of <see cref="AnimalState.Vanish"/> ends: whoever handles it picks a place and calls <see cref="Teleport"/> (the keeper). Unhandled, or when no place is found, the animal just stands
    /// where it is, calm for <see cref="AnimalDef.VanishGraceSeconds"/>.
    /// </summary>
    public event Action<Animal>? Vanished;

    /// <summary>Whether its species vanishes when startled instead of running (<see cref="AnimalDef.VanishSeconds"/> above 0).</summary>
    public bool Vanishes => Def.VanishSeconds > 0;

    /// <summary>Immune while it vanishes: it takes no damage, is not thrown or carried, and is no target of a shot or a vehicle.</summary>
    public bool Immune => State == AnimalState.Vanish;

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
        if (Ended != null || !Alive || !(amount > 0) || Immune) return;
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
        if (Vanishes) { TryVanish(); return; } // it does not run: it goes (if it is not still calm from its last vanish)
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
        _grace.Tick(dt);
        if (State == AnimalState.Carried) return; // held: the Thief moves it
        if (State == AnimalState.Thrown) { if (Fly(dt)) Go(AnimalState.Down, _file.KnockdownSeconds); return; }
        if (State == AnimalState.Down) { if (_fsm.Tick(dt)) GetUp(); return; }
        if (State == AnimalState.Vanish) { if (_fsm.Tick(dt)) FinishVanish(); return; } // runs on even unwatched: it must be somewhere else when seen again
        if (!_world.Watched(Position)) return; // no client holds it: nothing to show, so nothing to simulate (a corpse, a throw and a knockdown above run on)
        if (_open != null && _world is IPennedWorld pen && pen.Inside(Position.X, Position.Z)) _open = null; // back inside: its own world again

        var threat = Def.Flees ? _world.NearestThreat(Position, Def.FleeRadius) : null;
        if (_alarm.Running)
        {
            _alarm.Tick(dt);
            threat ??= _alarmFrom;
        }
        if (threat != null && Vanishes)
        {
            if (TryVanish()) return; // startled: gone for a while; still calm from the last vanish: the threat is ignored
        }
        else if (threat is { } t)
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
        if (Ended != null || !Alive || State is AnimalState.Carried or AnimalState.Vanish || Def.OneHit) return false;
        _arc = velocity;
        _rolling = false;
        _floorY = Position.Y;
        _alarmFrom = from;
        if (State != AnimalState.Thrown) Go(AnimalState.Thrown);
        if (Def.Flees) Herd?.Alarm(this, from);
        return true;
    }

    /// <summary>
    /// One step of the throw: the arc, then the roll along the ground with what is left of its speed (<see cref="ThrownRoll"/>). True once it lies (also when it was never
    /// thrown). Where there is no ground ahead (water, a wall, the terrain's rim) it goes no further that way: it drops straight down, or stops rolling.
    /// </summary>
    bool Fly(float dt)
    {
        if (_arc is not { } v) return true;
        if (!_rolling) v.Y -= MoveConsts.Gravity * dt;
        var next = Position + v * dt;
        var world = _open ?? _world;
        if (world.GroundY(next.X, next.Z) is { } ground) _floorY = ground;
        else { next.X = Position.X; next.Z = Position.Z; v.X = 0; v.Z = 0; }
        if (_rolling)
        {
            if (Position.Y - _floorY > ThrownRoll.LedgeM) { _rolling = false; _arc = v; Position = new Vector3(next.X, Position.Y, next.Z); return false; } // over an edge: it falls
            Position = new Vector3(next.X, _floorY, next.Z);
            if (!ThrownRoll.Roll(ref v, dt)) { _arc = v; return false; }
            _arc = null; _rolling = false;
            return true;
        }
        _arc = v;
        if (next.Y > _floorY) { Position = next; return false; }
        Position = new Vector3(next.X, _floorY, next.Z);
        if (v.Y > 0) return false; // the ground rose under it while it was still going up: it skims on
        _arc = ThrownRoll.Land(v); _rolling = true; // down: it rolls on with part of its speed (the next step lays it down when that is nothing)
        return false;
    }

    /// <summary>The knockdown is over: it runs from where the vehicle was (a species that does not flee stands).</summary>
    void GetUp()
    {
        if (!Def.Flees) { StandStill(); return; }
        if (Vanishes) { if (!TryVanish()) StandStill(); return; }
        _fleeFrom = _alarmFrom;
        _alarm.Start(_file.AlarmSeconds);
        _calm.Reset();
        Go(AnimalState.Flee);
    }

    // ---- carried (the Thieves event) ----

    /// <summary>A Thief takes it up: from any living state, it does nothing until <see cref="Release"/>. False when it is dead, gone or already held.</summary>
    public bool Carry()
    {
        if (Ended != null || !Alive || State is AnimalState.Carried or AnimalState.Vanish) return false;
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
        _steer.Cancel();
        _clear.Reset();
        _recheck.Cancel();
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

    /// <summary>
    /// A species with <see cref="Vanishes"/> startled: becomes immune and still for <see cref="AnimalDef.VanishSeconds"/> (<see cref="AnimalState.Vanish"/>). False (nothing happens) while it is still calm from its last vanish, or already vanishing.
    /// </summary>
    bool TryVanish()
    {
        if (!Vanishes || _grace.Running || State == AnimalState.Vanish) return false;
        _alarm.Cancel();
        _calm.Reset();
        Go(AnimalState.Vanish, Def.VanishSeconds);
        return true;
    }

    /// <summary>The vanish clock ran out: lets the keeper place it (<see cref="Vanished"/>, which calls <see cref="Teleport"/>); not placed, it stands where it is.</summary>
    void FinishVanish()
    {
        Vanished?.Invoke(this);
        if (State != AnimalState.Vanish) return;
        _grace.Start(Def.VanishGraceSeconds);
        StandStill();
    }

    /// <summary>
    /// The end of a vanish: it is at <paramref name="at"/> (on the ground there, facing <paramref name="yaw"/>), standing, calm for <see cref="AnimalDef.VanishGraceSeconds"/>, with no heading, target or alarm left from before.
    /// Only while it is vanishing; False otherwise (nothing moves).
    /// </summary>
    public bool Teleport(Vector3 at, float yaw)
    {
        if (Ended != null || State != AnimalState.Vanish) return false;
        Position = _world.GroundY(at.X, at.Z) is { } y ? new Vector3(at.X, y, at.Z) : at;
        Yaw = yaw;
        _open = null;
        _arc = null;
        _alarm.Cancel();
        _calm.Reset();
        _grace.Start(Def.VanishGraceSeconds);
        StandStill();
        return true;
    }

    /// <summary>Another animal of the herd took fright (<see cref="Herd.Alarm"/>): runs from the same spot for a while, without alarming the herd again.</summary>
    internal void Startle(Vector3 from)
    {
        if (Ended != null || !Alive || !Def.Flees || State is AnimalState.Flee or AnimalState.Vanish or AnimalState.Thrown or AnimalState.Down or AnimalState.Carried) return; // one in the air, knocked down or held is not startled out of it (it runs when it gets up)
        if (Vanishes) { TryVanish(); return; }
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
            if (_world.GroundY(x, z) is not { } y || !_world.Fits(x, z, BodyRadius)) continue; // no ground, or a wall in the way (a building, a fence)
            _target = new Vector3(x, y, z);
            Go(AnimalState.Walk, HorizontalDistance(Position, _target) / Def.WalkSpeed * 2 + 3); // twice the straight walk and a margin; then it gives up
            return;
        }
        StandStill(); // no spot has ground: stands on, with a fresh idle clock
    }

    void Walk(float dt)
    {
        if (_fsm.Tick(dt) || HorizontalDistance(Position, _target) < 0.6f) { if (_open != null) GoHome(); else StandStill(); return; }
        if (_steer.Tick(dt)) _bias = 0; // the detour is over: back to the line to the target
        switch (Step(TurretMath.YawTo(Position, _target) + _bias, Def.WalkSpeed, dt))
        {
            case Block.None: break;
            case Block.Solid when _steer.Ready: // a wall, a fence, a trunk: turn aside for a moment, once; blocked again within it, it stops
                _bias = _bias > 0 ? -WalkSteerRad : WalkSteerRad;
                _steer.Start(SteerSeconds);
                break;
            case Block.Solid when _steer.Running: break; // still turning away
            default: StandStill(); break; // no ground, or a player or an animal in the way: it waits where it is (and picks again)
        }
    }

    /// <summary>Walks to <see cref="Home"/> in a straight line over open ground (a pet that was carried out of its pen).</summary>
    void GoHome()
    {
        _target = _world.GroundY(Home.X, Home.Z) is { } y ? new Vector3(Home.X, y, Home.Z) : Home;
        _clear.Reset();
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
        _steer.Tick(dt);
        var block = Step(TurretMath.YawTo(Position, away) + _bias, Def.RunSpeed, dt);
        if (block == Block.None) return;
        if (block == Block.Ground) _bias = _bias >= 0 ? -FleeSteerRad : FleeSteerRad; // no ground there (water, a ledge): try round the other way, at once, as it always did
        else if (_steer.Ready)
        {
            // a wall, a player, an animal: turn aside, and each time it is still blocked once the turn has had its time, turn further the same way (a wall is slid along, not butted)
            _bias = Math.Clamp(_bias == 0 ? -FleeSteerRad : _bias - MathF.Sign(_bias) * FleeEscalateRad, -MaxBiasRad, MaxBiasRad);
            _steer.Start(FleeJudgeSeconds);
        }
    }

    /// <summary>Back to standing with a fresh idle clock (also when already standing: the clock restarts).</summary>
    void StandStill()
    {
        var seconds = Between(_file.IdleMinSeconds, _file.IdleMaxSeconds);
        if (!_fsm.Is(AnimalState.Idle)) Go(AnimalState.Idle, seconds);
        else _fsm.Restart(seconds);
    }

    /// <summary>Why a step did not happen (<see cref="Step"/>).</summary>
    enum Block : byte { None, Ground, Solid, Body }

    const float WalkSteerRad = 1.0f, FleeSteerRad = 1.3f, FleeEscalateRad = 0.5f, MaxBiasRad = 2.8f, SteerSeconds = 1.2f, FleeJudgeSeconds = 0.5f;
    /// <summary>The ray looks this far ahead at least, or this many seconds of the speed; and a wall closer than the step plus the gap stops it.</summary>
    const float LookMinM = 2.5f, LookSeconds = 0.5f, GapM = 0.15f, RecheckSeconds = 0.3f;
    /// <summary>A player's footprint for contact: the player capsule's radius.</summary>
    const float PlayerRadius = MoveConsts.CapsuleRadius;

    [ThreadStatic] static List<Vector3>? s_players;

    /// <summary>
    /// Turns towards <paramref name="want"/> and, once roughly facing it, walks one step forward. Blocked (and why) when the ground refuses it (no ground, a
    /// drop or climb bigger than <see cref="AnimalsFile.MaxStepM"/>), a player or another animal stands in the way (its Body box would touch
    /// theirs: <see cref="BodyBox"/>; a player is never pushed, the animal yields) or something solid is ahead (a ray, rarely: <see cref="ClearPath"/>).
    /// Turning in place is not blocked.
    /// </summary>
    Block Step(float want, float speed, float dt)
    {
        var turned = TurretMath.StepAngle(Yaw, want, Def.TurnRateDegPerS * MathF.PI / 180f * dt);
        if (_recheck.Running && turned != Yaw && SolidAlong(_open ?? _world, turned)) turned = Yaw; // just blocked by a wall: it does not swing its nose into it
        Yaw = turned;
        if (MathF.Abs(TurretMath.AngleDiff(Yaw, want)) > 1f) return Block.None;
        var f = Forward;
        var step = speed * dt;
        var x = Position.X + f.X * speed * dt; // (not f * step: the same sum, rounded the way it always was, keeps the golden trajectories)
        var z = Position.Z + f.Z * speed * dt;
        var world = _open ?? _world;
        if (world.GroundY(x, z) is not { } y || MathF.Abs(y - Position.Y) > _file.MaxStepM) return Block.Ground;
        if (BodyAhead(x, z)) return Block.Body;
        if (SolidAhead(world, step, speed, dt)) return Block.Solid;
        Position = new Vector3(x, y, z);
        return Block.None;
    }

    /// <summary>Whether something solid lies within the reach of its body from the origin along <paramref name="yaw"/>: where its nose would swing to. One ray, asked only while it is up against a wall.</summary>
    bool SolidAlong(IAnimalWorld world, float yaw)
    {
        var (s, c) = MathF.SinCos(yaw);
        var reach = _bodyAhead + _bodyL / 2 + 0.1f;
        return world.SolidAhead(Position, new Vector3(-s, 0, -c), reach, Math.Clamp(_bodyH * 0.6f, 0.25f, 0.8f)) < reach;
    }

    /// <summary>Whether the animal, one step on, would touch a player or another animal it is not already touching (or is closing in on).</summary>
    bool BodyAhead(float x, float z)
    {
        var to = BodyBox.Of(new Vector3(x, Position.Y, z), Yaw, _bodyW, _bodyL, _bodyAhead);
        var from = Body;
        if (Crowd?.Blocks(this, from, to) == true) return true;
        var near = s_players ??= new List<Vector3>();
        near.Clear();
        _world.PlayersNear(Position, BodyReach + PlayerRadius + 1f, near);
        foreach (var p in near)
        {
            var d = to.DistanceTo(p.X, p.Z);
            if (d < PlayerRadius && (from.DistanceTo(p.X, p.Z) >= PlayerRadius || d < from.DistanceTo(p.X, p.Z))) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether something solid lies within a step (and the gap) of the front of its body. Two rays (the front corners) a good way ahead, kept as the <see cref="ClearPath"/>
    /// they found free: the next steps along the same heading ask nobody.
    /// </summary>
    bool SolidAhead(IAnimalWorld world, float step, float speed, float dt)
    {
        if (_recheck.Tick(dt)) _clear.Reset();
        if (_clear.Covers(Position.X, Position.Z, Yaw, step)) return false;
        if (_clear.KnownBlocked(Position.X, Position.Z, Yaw)) return true;
        var look = MathF.Max(LookMinM, speed * LookSeconds);
        var f = Forward;
        var reach = _bodyAhead + _bodyL / 2; // from the front of the body, not the origin
        var (fx, fz) = (Position.X + f.X * reach, Position.Z + f.Z * reach);
        var (rx, rz) = (-f.Z * _bodyW / 2, f.X * _bodyW / 2); // half its width to its right: the two front corners, so a wall beside the nose stops it too
        var height = Math.Clamp(_bodyH * 0.6f, 0.25f, 0.8f);
        var d = MathF.Min(world.SolidAhead(new Vector3(fx + rx, Position.Y, fz + rz), f, look, height), world.SolidAhead(new Vector3(fx - rx, Position.Y, fz - rz), f, look, height));
        if (d < step + GapM) { _clear.SetBlocked(Position.X, Position.Z, Yaw); _recheck.Start(RecheckSeconds); return true; }
        _clear.Set(Position.X, Position.Z, Yaw, MathF.Min(d, look) - GapM);
        return false;
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
