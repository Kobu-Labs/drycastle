using System.Numerics;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Turrets;

namespace Drycastle.ShooterCore.Citizens;

/// <summary>What a citizen is doing: the replicated state byte. Walking is the way to the church door on the navigation mesh, Entering the scripted walk inside to the seat.</summary>
/// <remarks>Standing is the pastor and monks at their spot, or a citizen on its feet after the Mass; Clapping is the Mass done and the body cheering (the pastor flexes). Fleeing is the sprint back to the spawn point (the demon appeared); it ends in removal there.</remarks>
public enum CitizenState : byte { Walking = 0, Entering = 1, Seated = 2, Dead = 3, Kneeling = 4, Standing = 5, Clapping = 6, Fleeing = 7 }

/// <summary>
/// Who a citizen entity is. It travels in the entity state's hit-point byte (<c>role * 2 + alive</c>, so up to 127 roles fit). Monk and Pastor belong to the Holy Mass;
/// Mayor, Clerk and Announcer are the workers of the Obecny Urad while a rozhlas detour is open (<see cref="WorkerCrowd"/>): they sit at their spots, passive.
/// </summary>
public enum CitizenRole : byte { Citizen = 0, Monk = 1, Pastor = 2, Mayor = 3, Clerk = 4, Announcer = 5 }

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

    /// <summary>The last <see cref="WorkerHitIdCount"/> ids of the range belong to the Urad workers (<see cref="WorkerCrowd"/>); the Mass crowd cycles through the rest.</summary>
    public const int WorkerHitIdCount = 100;
    public const int CrowdHitIdCount = HitIdCount - WorkerHitIdCount;
    public const ushort FirstWorkerHitId = FirstHitId + CrowdHitIdCount;

    public static bool IsHitId(ushort id) => id >= FirstHitId && id < FirstHitId + HitIdCount;

    /// <summary>A worker of the Obecny Urad (mayor, clerk, announcer): seated at a desk, never walks or flees, belongs to the detour and not to the Mass.</summary>
    public static bool IsWorker(CitizenRole role) => role is CitizenRole.Mayor or CitizenRole.Clerk or CitizenRole.Announcer;

    public static CitizenState StateOf(byte state) => state <= (byte)CitizenState.Fleeing ? (CitizenState)state : CitizenState.Dead;

    public static void Register(WorldEntityRegistry registry)
    {
        registry.RegisterOnce(new WorldEntityDef(TypeId, "citizen", HasHp: true));
    }
}

/// <summary>
/// A citizen of the village on its way to Mass. It starts at a house door, walks the route the server found on the navigation mesh to the
/// church door, follows the church's scripted route to its seat and sits. Any damage kills it: it lies for
/// <see cref="CitizensConfig.CorpseSeconds"/> and is removed, and nothing replaces it (a killed monk or pastor lies until the event is cleared). It never reacts to combat. Godot-free and
/// deterministic. A world entity of type <see cref="CitizenRules.TypeId"/>: the look (an opaque number the client turns into a body) travels in
/// the owner field of its state, the position is where its feet stand.
/// </summary>
public sealed class Citizen : IWorldEntity
{
    /// <summary>
    /// What a citizen can go from and to. The route cursor (<c>_next</c>) belongs to the route state a citizen is in (Walking, Entering, Fleeing), not to the
    /// machine: it is reset by hand where a route starts. <see cref="Role"/> picks which state the arrival ends in (Seated, Kneeling or Standing), so the
    /// table below is the union over the roles.
    /// </summary>
    public static readonly StateMachineSpec<CitizenState> Spec = StateMachineSpec<CitizenState>.Define("Citizen.State", "What a citizen of the Holy Mass is doing; the replicated state byte.", CitizenState.Walking)
        .Allow(CitizenState.Walking, CitizenState.Entering, CitizenState.Fleeing, CitizenState.Dead)
        .Allow(CitizenState.Entering, CitizenState.Seated, CitizenState.Kneeling, CitizenState.Standing, CitizenState.Fleeing, CitizenState.Dead)
        .Allow(CitizenState.Seated, CitizenState.Clapping, CitizenState.Standing, CitizenState.Fleeing, CitizenState.Dead)
        .Allow(CitizenState.Kneeling, CitizenState.Clapping, CitizenState.Standing, CitizenState.Fleeing, CitizenState.Dead)
        .Allow(CitizenState.Standing, CitizenState.Clapping, CitizenState.Fleeing, CitizenState.Dead)
        .Allow(CitizenState.Clapping, CitizenState.Standing, CitizenState.Fleeing, CitizenState.Dead)
        .Allow(CitizenState.Fleeing, CitizenState.Dead)
        .Note(CitizenState.Walking, "On the navigation-mesh route from the house to the church door.")
        .Note(CitizenState.Entering, "On the scripted route inside the church to the seat.")
        .Note(CitizenState.Seated, "In a pew (a citizen) or at a desk (an Urad worker, spawned in place); the client plays the sit pose.")
        .Note(CitizenState.Kneeling, "At a standing spot (a citizen).")
        .Note(CitizenState.Standing, "The pastor or a monk at their spot, or a citizen on its feet after the Mass.")
        .Note(CitizenState.Clapping, "The Mass is done and the body cheers (the pastor flexes).")
        .Note(CitizenState.Fleeing, "Sprinting home because the demon came; ends in removal at the spawn point (the entity ends, no state follows).")
        .Note(CitizenState.Dead, "Lies where it fell: a citizen's body is removed after the corpse time, the pastor's, a monk's and an Urad worker's lie until the event is cleared or the detour ends. Any hit kills in one.")
        .Build();

    readonly StateMachine<CitizenState> _fsm = new(Spec);
    readonly CitizensConfig _cfg;
    readonly List<Vector3> _walk;
    readonly List<Vector3> _enter;
    readonly float _speed;
    readonly float _factor;
    List<Vector3> _flee = new();
    int _next = 1;          // the next point of the route being walked (reset where a route starts: BeginEntering, Flee)

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
        _factor = speedFactor;
        _speed = cfg.WalkSpeed * speedFactor;
        Position = _walk[0];
        if (_walk.Count < 2) BeginEntering();
        else Yaw = TurnTo(_walk[1], float.MaxValue);
    }

    public int SeatId { get; }
    public float SeatYaw { get; }
    public ushort Look { get; }
    public Vector3 Position { get; private set; }
    /// <summary>Godot convention: 0 faces -Z.</summary>
    public float Yaw { get; private set; }
    public CitizenState State => _fsm.Current;
    public bool Alive => State != CitizenState.Dead;
    public bool Seated => State is CitizenState.Seated or CitizenState.Kneeling;
    /// <summary>Pastor and monks are citizen entities that never sit: they walk to a spot and stand there. A worker of the Urad sits at its spot.</summary>
    public CitizenRole Role { get; init; }
    /// <summary>What it asks of the world as it walks the navigation route: a trunk, a door closed now or a parked vehicle in the way (<see cref="WalkGuard"/>). Null: it walks the route as it is.</summary>
    public IWalkerGround? Ground { get; init; }
    WalkGuard _guard;
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
        _fsm.Enter(CitizenState.Dead);
        Killer = attacker;
        Died?.Invoke(this, attacker);
    }

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        switch (State)
        {
            case CitizenState.Dead:
                // a killed monk or pastor lies until the event is over (the crowd clears it); a citizen's body goes after the corpse time
                if (Role != CitizenRole.Citizen) break;
                _fsm.Tick(dt); // Elapsed is the time it has lain: summed exactly as the old _deadFor was
                if (_fsm.Elapsed >= _cfg.CorpseSeconds) Ended = WorldDespawnReason.Expired;
                break;
            case CitizenState.Walking:
                _guard.Tick(dt);
                if (Follow(_walk, dt, guard: true)) BeginEntering();
                break;
            case CitizenState.Entering:
                if (Follow(_enter, dt)) Sit();
                break;
            case CitizenState.Fleeing:
                _guard.Tick(dt);
                if (Follow(_flee, dt, _cfg.FleeSpeed * _factor, guard: true)) Ended = WorldDespawnReason.Removed; // home: gone
                break;
        }
    }

    public WorldState Serialize() => WorldState.Make(Position, Yaw, 0, Look, (byte)State, (byte)Role * 2 + (Alive ? 1 : 0));

    /// <summary>A one-point walk is already there: stand (or sit) now, before the first state goes out.</summary>
    public void Settle() { if (State is CitizenState.Walking or CitizenState.Entering && _walk.Count < 2) Sit(); }

    /// <summary>The role in an entity state's hit-point byte.</summary>
    public static CitizenRole RoleOf(byte hp) => (CitizenRole)Math.Min(hp >> 1, (int)CitizenRole.Announcer);

    /// <summary>The Mass is done: a living citizen on its feet or in its seat takes this state (standing up from the pew is the client's: it follows the state).</summary>
    public void Finish(bool clap)
    {
        if (!Alive || State is CitizenState.Walking or CitizenState.Entering or CitizenState.Fleeing) return;
        _fsm.Enter(clap ? CitizenState.Clapping : CitizenState.Standing);
    }

    /// <summary>
    /// The demon is here: a living citizen gets up and sprints home along the way it came (the scripted route out of the church, then the walk
    /// route backwards to its spawn point, where it is removed). The pastor and monks stay.
    /// </summary>
    public void Flee()
    {
        if (!Alive || Role != CitizenRole.Citizen || State == CitizenState.Fleeing) return;
        var route = new List<Vector3> { Position };
        if (State == CitizenState.Walking)
            for (var i = _next - 1; i >= 0; i--) route.Add(_walk[i]);
        else
        {
            var from = State == CitizenState.Entering ? _next - 1 : _enter.Count - 1; // Entering: the points already passed; in place: all of them
            for (var i = from; i >= 0; i--) route.Add(_enter[i]);
            for (var i = _walk.Count - 1; i >= 0; i--) route.Add(_walk[i]);
        }
        _flee = route;
        _next = 0;
        _guard.Reset(); // the detour round something on the way in is over: it runs home
        _fsm.Enter(CitizenState.Fleeing);
    }

    // ---- behaviour ----

    void BeginEntering()
    {
        _fsm.Enter(CitizenState.Entering);
        _next = 0; // the enter route starts at the door: the first leg is from where the walk ended
        if (_enter.Count == 0) Sit();
    }

    void Sit()
    {
        _fsm.Enter(Role is CitizenRole.Monk or CitizenRole.Pastor ? CitizenState.Standing : Stand ? CitizenState.Kneeling : CitizenState.Seated); // a worker sits at its desk
        if (_enter.Count > 0) Position = _enter[^1];
        Yaw = SeatYaw;
    }

    /// <summary>Walks <paramref name="route"/> from the current point at the citizen's speed, turning towards the leg. True when its end is reached.</summary>
    bool Follow(List<Vector3> route, float dt, float speed = 0, bool guard = false)
    {
        var budget = (speed > 0 ? speed : _speed) * dt;
        var turn = _cfg.TurnRateDegPerS * MathF.PI / 180f * dt;
        while (_next < route.Count)
        {
            var leg = route[_next];
            // a route on the navigation mesh is checked for what the mesh does not hold (trunk, closed door, parked vehicle): the point to head for is the leg's end or a point beside it, null when it stands
            if ((guard ? _guard.Head(Position, leg, budget, Ground) : leg) is not { } to) return false;
            var d = Distance(Position, to);
            if (d > 0.01f) Yaw = TurnTo(to, turn);
            if (d <= budget) { Position = to; budget -= d; if (to == leg) _next++; continue; }
            Position += (to - Position) * (budget / d);
            return false;
        }
        return true;
    }

    float TurnTo(Vector3 to, float maxStep) =>
        Distance(Position, to) < 0.01f ? Yaw : TurretMath.StepAngle(Yaw, TurretMath.YawTo(Position, to), maxStep);

    static float Distance(Vector3 a, Vector3 b) => Vector3.Distance(a, b);
}
