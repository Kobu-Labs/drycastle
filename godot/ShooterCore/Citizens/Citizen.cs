using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What a citizen is doing: the replicated state byte. Walking is the way to the church door on the navigation mesh, Entering the scripted walk inside to the seat.</summary>
public enum CitizenState : byte { Walking = 0, Entering = 1, Seated = 2, Dead = 3, Kneeling = 4 }

/// <summary>Citizen world entity rules: the wire type, the state byte and the ids a citizen is hit under.</summary>
public static class CitizenRules
{
    /// <summary>Wire id of the citizen world entity type (50 is the animal).</summary>
    public const byte TypeId = 51;

    /// <summary>
    /// The id a citizen has as a hit target (the weapon paths know bodies by id): apart from players (from 1), vehicles (0x4000 up),
    /// rockets (0x8000 up) and target dummies (50000 up). It is not the id of the world entity.
    /// </summary>
    public const ushort FirstHitId = 40000, HitIdCount = 10000;

    public static bool IsHitId(ushort id) => id >= FirstHitId && id < FirstHitId + HitIdCount;

    public static CitizenState StateOf(byte state) => state <= (byte)CitizenState.Kneeling ? (CitizenState)state : CitizenState.Dead;

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "citizen", HasHp: true));
    }
}

/// <summary>
/// A citizen of the village on its way to Mass. It starts at a house door, walks the route the server found on the navigation mesh to the
/// church door, follows the church's scripted route to its seat and sits. Any damage kills it: it lies for
/// <see cref="CitizensConfig.CorpseSeconds"/> and is removed, and nothing replaces it. It never reacts to combat. Godot-free and
/// deterministic. A world entity of type <see cref="CitizenRules.TypeId"/>: the look (an opaque number the client turns into a body) travels in
/// the owner field of its state, the position is where its feet stand.
/// </summary>
public sealed class Citizen : IWorldEntity
{
    readonly CitizensConfig _cfg;
    readonly List<Vector3> _walk;
    readonly List<Vector3> _enter;
    readonly float _speed;
    int _next = 1;          // the next point of the route being walked
    float _deadFor;

    /// <param name="walk">The route from the house to the church door (at least its first point: where it spawns).</param>
    /// <param name="enter">The scripted route inside, ending at the feet position under the seat.</param>
    public Citizen(CitizensConfig cfg, int seatId, float seatYaw, IReadOnlyList<Vector3> walk, IReadOnlyList<Vector3> enter, ushort look, float speedFactor = 1f)
    {
        _cfg = cfg;
        SeatId = seatId;
        SeatYaw = seatYaw;
        _walk = walk.ToList();
        _enter = enter.ToList();
        Look = look;
        _speed = cfg.WalkSpeed * speedFactor;
        Position = _walk[0];
        State = CitizenState.Walking;
        if (_walk.Count < 2) BeginEntering();
        else Yaw = TurnTo(_walk[1], float.MaxValue);
    }

    public int SeatId { get; }
    public float SeatYaw { get; }
    public ushort Look { get; }
    public Vector3 Position { get; private set; }
    /// <summary>Godot convention: 0 faces -Z.</summary>
    public float Yaw { get; private set; }
    public CitizenState State { get; private set; }
    public bool Alive => State != CitizenState.Dead;
    public bool Seated => State is CitizenState.Seated or CitizenState.Kneeling;
    /// <summary>The place is a standing spot, not a pew: on arrival the citizen kneels (<see cref="CitizenState.Kneeling"/>) instead of sitting.</summary>
    public bool Stand { get; init; }
    /// <summary>The player whose hit killed it.</summary>
    public ushort Killer { get; private set; }
    /// <summary>Whether it was sitting when it was killed.</summary>
    public bool DiedSeated { get; private set; }
    /// <summary>The id the weapon paths know it by (<see cref="CitizenRules.FirstHitId"/> up); set by the crowd that spawned it.</summary>
    public ushort HitId { get; set; }

    /// <summary>Raised once, when the hit that kills it lands: the citizen and the player who shot it.</summary>
    public event Action<Citizen, ushort>? Died;

    public byte TypeId => CitizenRules.TypeId;
    public ushort Owner => 0;
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>How a hit sees the body: a sitter is a crouching figure on the floor below the seat.</summary>
    public Stance HitStance => Seated ? Stance.Crouch : Stance.Stand;

    /// <summary>Any damage kills a citizen. A dead one takes none.</summary>
    public void OnDamage(float amount, ushort attacker)
    {
        if (Ended != null || !Alive || !(amount > 0)) return;
        DiedSeated = Seated;
        State = CitizenState.Dead;
        Killer = attacker;
        Died?.Invoke(this, attacker);
    }

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        switch (State)
        {
            case CitizenState.Dead:
                _deadFor += dt;
                if (_deadFor >= _cfg.CorpseSeconds) Ended = WorldDespawnReason.Expired;
                break;
            case CitizenState.Walking:
                if (Follow(_walk, dt)) BeginEntering();
                break;
            case CitizenState.Entering:
                if (Follow(_enter, dt)) Sit();
                break;
        }
    }

    public WorldState Serialize() => WorldState.Make(Position, Yaw, 0, Look, (byte)State, Alive ? 1 : 0);

    // ---- behaviour ----

    void BeginEntering()
    {
        State = CitizenState.Entering;
        _next = 0; // the enter route starts at the door: the first leg is from where the walk ended
        if (_enter.Count == 0) Sit();
    }

    void Sit()
    {
        State = Stand ? CitizenState.Kneeling : CitizenState.Seated;
        if (_enter.Count > 0) Position = _enter[^1];
        Yaw = SeatYaw;
    }

    /// <summary>Walks <paramref name="route"/> from the current point at the citizen's speed, turning towards the leg. True when its end is reached.</summary>
    bool Follow(List<Vector3> route, float dt)
    {
        var budget = _speed * dt;
        var turn = _cfg.TurnRateDegPerS * MathF.PI / 180f * dt;
        while (_next < route.Count)
        {
            var to = route[_next];
            var d = Distance(Position, to);
            if (d > 0.01f) Yaw = TurnTo(to, turn);
            if (d <= budget) { Position = to; budget -= d; _next++; continue; }
            Position += (to - Position) * (budget / d);
            return false;
        }
        return true;
    }

    float TurnTo(Vector3 to, float maxStep) =>
        Distance(Position, to) < 0.01f ? Yaw : TurretMath.StepAngle(Yaw, TurretMath.YawTo(Position, to), maxStep);

    static float Distance(Vector3 a, Vector3 b) => Vector3.Distance(a, b);
}
