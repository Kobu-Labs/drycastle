using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// What a client draws a Companion doing: the state byte of its replicated state. A kind picks one each tick (<see cref="Companion.Pose"/>); the client plays the clip of it and
/// needs no code per kind. Add a pose only for a new clip; what a Companion does is its kind's own state machine, never this.
/// </summary>
public enum CompanionPose : byte
{
    /// <summary>Stands (clip Idle).</summary>
    Idle = 0,
    Walk = 1,
    Run = 2,
    /// <summary>Sits: sit-down, hold; stands up when it ends (as an Animal's Sit).</summary>
    Sit = 3,
    /// <summary>Flies forward (clip Cruise).</summary>
    Fly = 4,
    /// <summary>Hangs in the air (a flyer's Idle clip).</summary>
    Hover = 5,
}

/// <summary>Companion world entity rules (GLOSSARY "Companion"): the wire type and what its replicated state means.</summary>
public static class CompanionRules
{
    /// <summary>Wire id of the Companion world entity type (50 animal, 51 citizen, 52 demon, 53 poop, 54 flock, 55-57 the Thieves).</summary>
    public const byte TypeId = 58;

    /// <summary>The kind of the unlock row that says a Profile owns a Companion (<see cref="Unlock"/>; the item id is the Companion's id).</summary>
    public const string UnlockKind = "companion";

    /// <summary>Metres between two replicated positions above which a Companion was put beside its owner (<see cref="Companion.Recall"/>), not moved: the client's buffer starts over so it is not drawn sliding there.</summary>
    public const float TeleportM = 6f;

    /// <summary>How far from its owner a Companion appears, and is put back, metres.</summary>
    public const float BesideM = 1.5f;

    public static CompanionPose PoseOf(byte state) => (CompanionPose)state;

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "companion", HasHp: false, Interest: true));
    }

    /// <summary>
    /// A spot on the ground beside <paramref name="owner"/>: <see cref="BesideM"/> away, behind and to the left first, then round the owner until a body of <paramref name="radius"/> fits.
    /// The owner's own position when nothing fits (a Companion blocks nobody, so standing in its owner is harmless).
    /// </summary>
    public static Vector3 Beside(in CompanionOwner owner, IAnimalWorld world, float radius)
    {
        for (var i = 0; i < 8; i++)
        {
            var a = owner.Yaw + MathF.PI * 0.75f + i * MathF.Tau / 8; // 0 faces -Z: +3/4 pi is behind and to the left
            var x = owner.Position.X - MathF.Sin(a) * BesideM;
            var z = owner.Position.Z - MathF.Cos(a) * BesideM;
            if (world.GroundY(x, z) is { } y && MathF.Abs(y - owner.Position.Y) < 1.5f && world.Fits(x, z, radius)) return new Vector3(x, y, z);
        }
        return owner.Position;
    }
}

/// <summary>An enemy of a Companion's owner, as the world reports them (<see cref="ICompanionWorld.EnemiesNear"/>).</summary>
public readonly record struct CompanionSighting(ushort Player, Vector3 Position);

/// <summary>What a Companion's world can also say besides the ground (<see cref="IAnimalWorld"/>): who its owner's enemies are. The server's world implements it; a world that does not has no enemies.</summary>
public interface ICompanionWorld : IAnimalWorld
{
    /// <summary>Appends every living enemy of <paramref name="owner"/> (not the owner, not a teammate) within <paramref name="radius"/> metres (flat) of <paramref name="at"/>.</summary>
    void EnemiesNear(ushort owner, Vector3 at, float radius, List<CompanionSighting> into);
}

/// <summary>The owner as the Companion sees them this tick: where they stand (their feet) and which way they face (0 faces -Z).</summary>
public readonly record struct CompanionOwner(Vector3 Position, float Yaw);

/// <summary>Everything a kind is given to make one Companion (<see cref="CompanionKinds"/>).</summary>
/// <param name="DefIndex">Its index in companions.json: what the wire carries.</param>
/// <param name="Species">The species index in animals.json, and <paramref name="Variant"/> the model of it.</param>
/// <param name="Player">The owner's player id.</param>
/// <param name="At">The owner now: it appears beside them.</param>
public sealed record CompanionSpawn(CompanionDef Def, int DefIndex, AnimalDef Animal, int Species, int Variant, ushort Player, CompanionOwner At, IAnimalWorld World, int Seed);

/// <summary>
/// A Companion (GLOSSARY "Companion"): an animal that belongs to one player and stays with them. This class is only what every Companion shares: the world entity (type
/// <see cref="CompanionRules.TypeId"/>), its owner, its place and pose, and a few ways to move. WHAT it does (follow at heel, circle its owner, fetch) is the concrete kind's
/// (<see cref="HeelCompanion"/>, <see cref="WingCompanion"/>, <see cref="ScoutCompanion"/>): a kind has its own states, its own <see cref="StateMachineSpec{TState}"/> and its own numbers in the
/// <c>rules</c> of companions.json, and decides everything in <see cref="Think"/>. A new kind is a new class and one line in <see cref="CompanionKinds"/>; nothing here,
/// in the keeper, on the wire or on the client changes unless it needs a clip no <see cref="CompanionPose"/> names.
/// <para>
/// Nothing hurts it, nothing is stopped by it, and no weapon is tested against it. When and where it exists is the <see cref="CompanionKeeper"/>'s: it is there while its
/// owner is alive and on foot. Godot-free and deterministic for a seed.
/// </para>
/// </summary>
public abstract class Companion : IWorldEntity
{
    readonly int _defIndex;
    readonly float _maxStep;

    protected Companion(CompanionSpawn s)
    {
        Def = s.Def;
        _defIndex = s.DefIndex;
        Animal = s.Animal;
        Species = s.Species;
        Variant = s.Variant;
        Player = s.Player;
        World = s.World;
        Rng = new Random(s.Seed);
        Master = s.At;
        _maxStep = 0.8f;
        Position = CompanionRules.Beside(s.At, World, BodyRadius);
        Yaw = s.At.Yaw;
    }

    public CompanionDef Def { get; }
    /// <summary>The species it looks like, and which model of it.</summary>
    public AnimalDef Animal { get; }
    public int Species { get; }
    public int Variant { get; }
    /// <summary>The owner's player id.</summary>
    public ushort Player { get; }

    public byte TypeId => CompanionRules.TypeId;
    public ushort Owner => Player;
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>The model's origin: the feet of one that walks, the body of one that flies.</summary>
    public Vector3 Position { get; protected set; }
    /// <summary>Godot convention: 0 faces -Z.</summary>
    public float Yaw { get; protected set; }
    /// <summary>Radians, 0 level, negative nose down (a flyer).</summary>
    public float Pitch { get; protected set; }
    /// <summary>What the clients draw it doing.</summary>
    public CompanionPose Pose { get; protected set; }

    /// <summary>The owner as last told (<see cref="Lead"/>): one tick old when <see cref="Think"/> runs.</summary>
    protected CompanionOwner Master { get; private set; }
    protected IAnimalWorld World { get; }
    protected Random Rng { get; }
    protected float BodyRadius => Animal.BodyRadiusOf(Variant);

    /// <summary>
    /// The players it has marked for its owner now (ids; empty for a kind that marks nobody): the server tells the owner, whose client draws them through walls.
    /// The same list object while nothing changed is not promised: compare the ids.
    /// </summary>
    public virtual IReadOnlyList<ushort> Marks => Array.Empty<ushort>();

    /// <summary>The enemies of its owner within <paramref name="radius"/> metres (flat) of <paramref name="at"/>, into <paramref name="into"/>; none in a world that cannot say (<see cref="ICompanionWorld"/>).</summary>
    protected void EnemiesNear(Vector3 at, float radius, List<CompanionSighting> into)
    {
        into.Clear();
        (World as ICompanionWorld)?.EnemiesNear(Player, at, radius, into);
    }

    /// <summary>The keeper tells it where its owner is, once a tick.</summary>
    public void Lead(in CompanionOwner owner) => Master = owner;

    /// <summary>It goes (its owner died, took a seat or dropped it): the entity ends and the clients show a puff.</summary>
    public void Dismiss() => Ended ??= WorldDespawnReason.Destroyed;

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        Think(dt);
    }

    /// <summary>One tick of the kind's own rules: move, turn, pick the <see cref="Pose"/>.</summary>
    protected abstract void Think(float dt);

    /// <summary>The name of the kind's current state, for logs and tests.</summary>
    public abstract string StateName { get; }

    /// <summary>Nothing hurts a Companion.</summary>
    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, Yaw, Pitch, (ushort)_defIndex, (byte)Pose, 0);

    // ---- what a kind moves with ----

    /// <summary>Flat metres to the owner.</summary>
    protected float OwnerDistance => Flat(Position, Master.Position);

    protected float Between(float a, float b) => a + (float)Rng.NextDouble() * (b - a);

    protected static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Put beside the owner at once, on the ground, facing as they do (it fell too far behind, or cannot get there). The clients do not draw the way there (<see cref="CompanionRules.TeleportM"/>).</summary>
    protected void Recall()
    {
        Position = CompanionRules.Beside(Master, World, BodyRadius);
        Yaw = Master.Yaw;
        Pitch = 0;
    }

    /// <summary>
    /// One step over the ground towards <paramref name="target"/>: turns at the species' turn rate and, once roughly facing it, walks. False when the step did not happen: no ground
    /// there (water, the map's rim), a drop or climb too big, or something solid in the way (a wall, a fence, a trunk, a parked vehicle; one ray). Players and animals do not stop it.
    /// </summary>
    protected bool StepTo(Vector3 target, float speed, float dt)
    {
        var want = TurretMath.YawTo(Position, target);
        Yaw = TurretMath.StepAngle(Yaw, want, Animal.TurnRateDegPerS * MathF.PI / 180f * dt);
        if (MathF.Abs(TurretMath.AngleDiff(Yaw, want)) > 1f) return true; // still turning: not blocked
        var dir = new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        var step = MathF.Min(speed * dt, Flat(Position, target));
        var x = Position.X + dir.X * step;
        var z = Position.Z + dir.Z * step;
        if (World.GroundY(x, z) is not { } y || MathF.Abs(y - Position.Y) > _maxStep) return false;
        if (World.SolidAhead(Position, dir, step + BodyRadius + 0.15f, 0.3f) < step + BodyRadius + 0.15f) return false;
        Position = new Vector3(x, y, z);
        return true;
    }

    /// <summary>
    /// One step through the air towards <paramref name="target"/> at <paramref name="speed"/>, never lower than <paramref name="clearanceM"/> over the ground; faces the way it goes
    /// (turning at the species' rate) when it moves at all. Nothing stops a flyer. True when it is there.
    /// </summary>
    protected bool FlyTo(Vector3 target, float speed, float dt, float clearanceM)
    {
        var to = target - Position;
        var d = to.Length();
        var step = speed * dt;
        var next = d <= step ? target : Position + to / d * step;
        if (World.GroundY(next.X, next.Z) is { } ground && next.Y < ground + clearanceM) next.Y = ground + clearanceM;
        if (Flat(Position, next) > 0.01f) Yaw = TurretMath.StepAngle(Yaw, TurretMath.YawTo(Position, next), Animal.TurnRateDegPerS * MathF.PI / 180f * dt);
        Position = next;
        return d <= step;
    }
}

/// <summary>One kind of Companion: how to make one, and how to check a companions.json entry of it (its <c>rules</c>).</summary>
public sealed record CompanionKind(string Name, Func<CompanionSpawn, Companion> Create, Action<CompanionDef> Check);

/// <summary>
/// The kinds of Companion by name (the <c>kind</c> of a companions.json entry). A kind is a class derived from <see cref="Companion"/> that holds its own rules; it is listed
/// here and nowhere else.
/// </summary>
public static class CompanionKinds
{
    static readonly Dictionary<string, CompanionKind> s_kinds = new(StringComparer.Ordinal);

    static CompanionKinds()
    {
        Register(HeelCompanion.Kind);
        Register(WingCompanion.Kind);
        Register(ScoutCompanion.Kind);
    }

    public static void Register(CompanionKind kind) => s_kinds[kind.Name] = kind;

    public static CompanionKind? Find(string name) => s_kinds.GetValueOrDefault(name);

    public static IEnumerable<string> Names => s_kinds.Keys.OrderBy(k => k, StringComparer.Ordinal);
}
