using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>The three kinds of Gang member (GLOSSARY "Gang member"). Travels in bits 1-2 of the hit-point byte of the entity state.</summary>
public enum GangMemberKind : byte { Thief = 0, Enforcer = 1, GangBoss = 2 }

/// <summary>
/// What a Gang member is doing: the replicated state byte. Values 9 and up are free for the stealing states (ticket 04: Seek, Approach, Steal, Haul, Load);
/// 8 is the van's (ticket 03: Board). The client treats an unknown value as standing.
/// </summary>
public enum GangMemberState : byte { Idle = 0, Walk = 1, Guard = 2, Chase = 3, Windup = 4, Swing = 5, Flee = 6, Dead = 7, Board = 8 }

/// <summary>A living player as the Gang members see them: where their feet are and whether they stand on the ground (a rider in a vehicle cannot be struck).</summary>
public readonly record struct GangPlayer(ushort Id, Vector3 Feet, bool OnFoot);

/// <summary>A member waits for a route on the navigation mesh. The gang serves one request a tick, the oldest first (<see cref="ThiefGang.ServeRoute"/>).</summary>
public readonly record struct GangRouteRequest(Vector3 From, Vector3 To);

/// <summary>A member was killed: which, by whom, where it lay, what it carried (see <see cref="GangRules.PackHp"/>).</summary>
public readonly record struct GangKilled(ushort HitId, ushort Killer, GangMemberKind Kind, Vector3 Where, byte Carried);

/// <summary>What the members need from the server: the players they fight, and the hand that hurts one.</summary>
public interface IGangWorld
{
    /// <summary>The living players now (on foot or riding), humans and bots alike; refreshed once a tick before the members tick.</summary>
    IReadOnlyList<GangPlayer> Players { get; }

    /// <summary>The player takes <paramref name="damage"/> from the member with this hit id (<see cref="GangRules.FirstHitId"/> up).</summary>
    void Hurt(ushort player, float damage, ushort memberHitId);
}

/// <summary>What a weapon hit on a Gang member (the match calls it for a victim id in the members' range).</summary>
public interface IGangHits
{
    /// <summary><paramref name="cause"/> Headshot counts the headshot weight, Roadkill kills outright; <paramref name="weapon"/> says explosive or vehicle weapon (<see cref="BigfootRules.SourceOf"/>).</summary>
    DamageResult Hit(ushort hitId, ushort attacker, byte weapon, float amount, KillCause cause);
}

/// <summary>Rules of the Gang member entity that need no world: the wire type, the hit ids, the hit-point byte and the weight of a hit.</summary>
public static class GangRules
{
    /// <summary>Wire id of the Gang member world entity type (53 is the poop).</summary>
    public const byte TypeId = 54;

    /// <summary>
    /// The id a member has as a hit target: between the turret records (0x7000 up) and the pets (0x7800 up), clear of players, vehicles, citizens (40000 up),
    /// the demon, target dummies (50000 up) and the Bigfoot. Not the id of the world entity.
    /// </summary>
    public const ushort FirstHitId = 0x7400, HitIdCount = 0x400;

    /// <summary>The most members of one Raid.</summary>
    public const int MaxCrew = 24;

    public static bool IsHitId(ushort id) => id >= FirstHitId && id < FirstHitId + HitIdCount;

    // The hit-point byte: bit 0 alive, bits 1-2 kind, bits 3-5 what is carried (0 nothing, 1 and up the Loot kind plus one: ticket 04), bits 6-7 free.
    const int AliveBit = 1, KindShift = 1, KindMask = 3, CarriedShift = 3, CarriedMask = 7;

    public static byte PackHp(GangMemberKind kind, bool alive, byte carried) =>
        (byte)((alive ? AliveBit : 0) | (((int)kind & KindMask) << KindShift) | ((carried & CarriedMask) << CarriedShift));

    public static bool AliveOf(byte hp) => (hp & AliveBit) != 0;
    public static GangMemberKind KindOf(byte hp) => (GangMemberKind)Math.Min((hp >> KindShift) & KindMask, (int)GangMemberKind.GangBoss);
    public static byte CarriedOf(byte hp) => (byte)((hp >> CarriedShift) & CarriedMask);

    /// <summary>A state byte as a state; a value this build does not know stands.</summary>
    public static GangMemberState StateOf(byte state) => state <= (byte)GangMemberState.Board ? (GangMemberState)state : GangMemberState.Idle;

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "gang member", HasHp: true));
    }

    /// <summary>What a hit counts, by what made it (the Bigfoot's rules with a headshot worth <see cref="GangHitWeights.HeadshotWeight"/>). Ramming has no weight: it kills outright.</summary>
    public static float Weight(GangHitWeights w, HitSource source, bool head) => source switch
    {
        HitSource.Bullet => head ? w.HeadshotWeight : 1f,
        HitSource.Explosive => w.ExplosiveWeight,
        HitSource.VehicleWeapon => w.VehicleWeaponWeight,
        _ => 0f,
    };
}

/// <summary>
/// A person of a Raid (GLOSSARY "Gang member"): a world entity (type <see cref="GangRules.TypeId"/>) with its own brain and a Hit count, not a Citizen and not a bot.
/// Godot-free and deterministic. <see cref="ThiefGang"/> owns it: it feeds the target (who the Raid fights) and serves the routes the member asks for
/// (<see cref="RouteRequest"/>, <see cref="SetRoute"/>); the server feeds the players through <see cref="IGangWorld"/>. It never attacks anyone the gang does not give it.
/// <para>
/// Layout of the entity state: owner = the look (an opaque number the client turns into a body), state byte = <see cref="GangMemberState"/>, hp byte =
/// <see cref="GangRules.PackHp"/> (kind, alive, what it carries), position = where its feet stand.
/// </para>
/// </summary>
public sealed class GangMember : IWorldEntity
{
    /// <summary>
    /// The member's states and the edges between them. A Thief idles and now and then walks off; an Enforcer or the Gang boss guards (follows a Thief or stands at the
    /// post); whoever is given a target chases it, winds up and swings, and goes back; Flee runs for the map edge and ends there. The route cursor belongs to the
    /// state that follows a route (Walk, Guard, Chase, Flee), not to the machine. Stealing states come with ticket 04.
    /// </summary>
    public static readonly StateMachineSpec<GangMemberState> Spec = StateMachineSpec<GangMemberState>
        .Define("GangMember.State", "What a Gang member of a Raid is doing; the replicated state byte.", GangMemberState.Idle)
        .Allow(GangMemberState.Idle, GangMemberState.Walk, GangMemberState.Guard, GangMemberState.Chase, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Walk, GangMemberState.Idle, GangMemberState.Chase, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Guard, GangMemberState.Idle, GangMemberState.Chase, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Chase, GangMemberState.Windup, GangMemberState.Idle, GangMemberState.Guard, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Windup, GangMemberState.Swing, GangMemberState.Idle, GangMemberState.Guard, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Swing, GangMemberState.Chase, GangMemberState.Idle, GangMemberState.Guard, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Board, GangMemberState.Flee)
        .FromAnywhere(GangMemberState.Dead)
        .Note(GangMemberState.Idle, "A Thief standing (clock: a random time, then it walks to a point near the post). Steals nothing until ticket 04.")
        .Note(GangMemberState.Walk, "A Thief on a route to a point near the post (clock: the time budget to arrive, then it stands again).")
        .Note(GangMemberState.Guard, "An Enforcer follows the Thief it escorts, or stands at the post; the Gang boss stands at the post. Never steals.")
        .Note(GangMemberState.Chase, "Runs at the player the Raid is fighting (a route from the navigation mesh, asked for at most every repath_seconds); winds up once within reach.")
        .Note(GangMemberState.Windup, "The swing's telegraph (clock: windup_seconds): a player can step out of reach. Ends in Swing, or in a stand if the target is gone.")
        .Note(GangMemberState.Swing, "The blow lands at its start, only if the target is still within reach; then recovers (clock: interval minus windup) and chases or stands again.")
        .Note(GangMemberState.Flee, "Runs for the map edge on a route (clock: flee_seconds); the entity ends there (no state follows).")
        .Note(GangMemberState.Board, "Called back by the van: runs to its side door on a route (never fights) and ends there, counted as boarded. Flee when the van is destroyed or leaves without it.")
        .Note(GangMemberState.Dead, "Lies for corpse_seconds (the clock's elapsed time), then the entity ends. Reached from any state; never left.")
        .Build();

    const float DirectM = 3f;       // a chaser this near its target with no route walks straight at it
    const float ReachDy = 2.2f;     // a blow reaches no higher or lower than this

    readonly GangConfig _d;
    readonly GangKindConfig _k;
    readonly IGangWorld _world;
    readonly Random _rng;
    readonly StateMachine<GangMemberState> _fsm = new(Spec);
    readonly List<Vector3> _route = new();
    int _next;                      // the next point of the route being followed
    Countdown _repath;
    GangPlayer? _target;
    Vector3 _edge;
    Vector3 _door;
    float _taken;                   // the hit count taken so far, weighted

    public GangMember(GangConfig cfg, GangMemberKind kind, IGangWorld world, Vector3 position, float yaw, ushort look, ushort hitId, int seed)
    {
        _d = cfg; _k = cfg.Of(kind); _world = world; _rng = new Random(seed);
        Kind = kind; Position = Home = position; Yaw = yaw; Look = look; HitId = hitId;
        if (kind != GangMemberKind.Thief) _fsm.Enter(GangMemberState.Guard);
        else _fsm.Restart(IdleSeconds());
    }

    public GangMemberKind Kind { get; }
    public ushort Look { get; }
    /// <summary>The id the weapon paths know it by (<see cref="GangRules.FirstHitId"/> up); set by the gang that spawned it.</summary>
    public ushort HitId { get; }
    public Vector3 Position { get; private set; }
    /// <summary>Godot convention: 0 faces -Z.</summary>
    public float Yaw { get; private set; }
    /// <summary>The post: where the Raid's van stands (ticket 03), for now where the crew appeared. A Thief wanders near it, a Gang boss and an Enforcer without a Thief to escort stand at it.</summary>
    public Vector3 Home { get; set; }
    public GangMemberState State => _fsm.Current;
    public bool Alive => State != GangMemberState.Dead;
    /// <summary>The Thief an Enforcer escorts (set by the gang).</summary>
    public GangMember? Escort { get; set; }
    /// <summary>What it carries: 0 nothing, 1 and up the Loot kind plus one (ticket 04). Travels in the hp byte.</summary>
    public byte Carried { get; set; }
    /// <summary>Stealing or hauling: a player crowding it is in the way. HOOK for ticket 04 (Steal and Haul states); false until they exist.</summary>
    public bool Working => false;
    /// <summary>Hands not free: working or carrying Loot. A busy Thief does not join a fight.</summary>
    public bool Busy => Carried != 0 || Working;
    /// <summary>Enforcers and the Gang boss fight when the Raid does; a Thief with free hands joins.</summary>
    public bool CanFight => Alive && State != GangMemberState.Flee && State != GangMemberState.Board && (Kind != GangMemberKind.Thief || !Busy);
    /// <summary>The player it fights now, or null.</summary>
    public GangPlayer? Target => _target;
    /// <summary>The weighted hits taken, and the Hit count that kills it (GLOSSARY "Hit count").</summary>
    public float HitsTaken => _taken;
    public float HitsNeeded => _k.Hits;
    /// <summary>The player whose hit killed it (0 when something else did).</summary>
    public ushort Killer { get; private set; }
    /// <summary>The route it is waiting for (set by the member, cleared by <see cref="SetRoute"/>) and the place in the gang's queue (0: not queued yet; set by the gang).</summary>
    public GangRouteRequest? RouteRequest { get; private set; }
    public long RequestSeq { get; set; }
    /// <summary>The route points it follows now (read only: tests and logs).</summary>
    public IReadOnlyList<Vector3> Route => _route;
    /// <summary>The tick it was first seen by the pose recorder, and the poses of recent ticks, for the rewind of a shot (ADR 0013).</summary>
    public uint BornTick { get; set; }
    public PoseRing Poses { get; } = new();

    /// <summary>Raised once, when the hit that kills it lands.</summary>
    public event Action<GangMember>? Died;
    /// <summary>Raised once, when it reaches the van's door (the entity ends there).</summary>
    public event Action<GangMember>? Boarded;

    public byte TypeId => GangRules.TypeId;
    public ushort Owner => 0;
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>The standing hit volume the shots test against: the player's capsule at the kind's scale (a headshot is a head hit).</summary>
    public HitTarget HitVolume => new(HitId, Position, Stance.Stand, Yaw, HeadGuarded: false, Scale: _k.HitScale);

    public WorldState Serialize() => WorldState.Make(Position, Yaw, 0, Look, (byte)State, GangRules.PackHp(Kind, Alive, Carried));

    // ---- hits ----

    /// <summary>A hit landed. <paramref name="weight"/> is what it counts (<see cref="GangRules.Weight"/>). True when this hit killed it. A dead member takes nothing.</summary>
    public bool Hit(ushort attacker, float weight)
    {
        if (Ended != null || !Alive || !(weight > 0)) return false;
        _taken += weight;
        if (_taken < _k.Hits) return false;
        Die(attacker);
        return true;
    }

    public void OnDamage(float amount, ushort attacker) => Hit(attacker, 1);

    /// <summary>Kills it at once (run over by a vehicle). False when it was already dead.</summary>
    public bool Kill(ushort killer)
    {
        if (Ended != null || !Alive) return false;
        Die(killer);
        return true;
    }

    /// <summary>Ends the entity where it is, without a death clip (boarded the van, ticket 03; the event cleared).</summary>
    public void Remove(WorldDespawnReason reason = WorldDespawnReason.Removed) => Ended ??= reason;

    void Die(ushort killer)
    {
        Killer = killer;
        _taken = MathF.Max(_taken, _k.Hits);
        _target = null;
        ClearRoute();
        _fsm.Enter(GangMemberState.Dead);
        Died?.Invoke(this);
    }

    // ---- orders from the gang ----

    /// <summary>Who to fight now (null: nobody). Refreshed every tick by the gang with where the player stands.</summary>
    public void SetTarget(GangPlayer? target) => _target = Alive ? target : null;

    /// <summary>Runs for <paramref name="edge"/> (a point at the map edge) on a route; the entity ends when it arrives. Not for the dead.</summary>
    public void Flee(Vector3 edge)
    {
        if (!Alive || Ended != null || State == GangMemberState.Flee) return;
        _target = null;
        _edge = edge;
        ClearRoute();
        _fsm.Enter(GangMemberState.Flee, _d.FleeSeconds);
        Request(edge);
    }

    /// <summary>
    /// The van calls it back: it drops what it was doing (a fight too), runs to <paramref name="door"/> and ends there as boarded. Not for the dead, the fleeing or one that already boards.
    /// </summary>
    public void Board(Vector3 door)
    {
        if (!Alive || Ended != null || State is GangMemberState.Flee or GangMemberState.Board) return;
        _target = null;
        _door = door;
        ClearRoute();
        _fsm.Enter(GangMemberState.Board);
        Request(door);
    }

    /// <summary>The route the gang found for the request (empty: none). A route replaces the one being followed.</summary>
    public void SetRoute(IReadOnlyList<Vector3> route)
    {
        RouteRequest = null; RequestSeq = 0;
        _route.Clear(); _next = 0;
        if (route.Count < 2) { RouteFailed(); return; }
        _route.AddRange(route);
    }

    void RouteFailed()
    {
        _repath.Start(_d.RepathSeconds * 2); // do not ask again at once
        if (State == GangMemberState.Flee) Ended = WorldDespawnReason.Removed; // nowhere to run on foot: gone
        else if (State == GangMemberState.Walk) Settle();
        // a boarding member with no route asks again after the pause (TickBoard)
    }

    // ---- the tick ----

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        _repath.Tick(dt);
        switch (State)
        {
            case GangMemberState.Dead:
                _fsm.Tick(dt); // Elapsed is the time it has lain
                if (_fsm.Elapsed >= _d.CorpseSeconds) Ended = WorldDespawnReason.Expired;
                return;
            case GangMemberState.Flee:
                TickFlee(dt);
                return;
            case GangMemberState.Board:
                TickBoard(dt);
                return;
            case GangMemberState.Windup:
                if (_target is not { } wt || !CanFight) { Settle(); return; } // the target is gone: no blow
                Face(wt.Feet, dt);
                if (_fsm.Tick(dt)) Strike();
                return;
            case GangMemberState.Swing:
                if (_fsm.Tick(dt)) { if (_target != null && CanFight) _fsm.Enter(GangMemberState.Chase); else Settle(); }
                return;
        }
        if (_target is { } t && CanFight)
        {
            if (_fsm.Enter(GangMemberState.Chase)) ClearRoute(); // a route (or a request) for the walk it was on leads nowhere now
            TickChase(t, dt);
            return;
        }
        if (State == GangMemberState.Chase) Settle(); // the target is gone: back to the post
        switch (State)
        {
            case GangMemberState.Idle: TickIdle(dt); break;
            case GangMemberState.Walk: TickWalk(dt); break;
            case GangMemberState.Guard: TickGuard(dt); break;
        }
    }

    void TickIdle(float dt)
    {
        if (!_fsm.Tick(dt)) return;
        if (Kind != GangMemberKind.Thief) { _fsm.Enter(GangMemberState.Guard); return; }
        var a = (float)_rng.NextDouble() * MathF.Tau;
        var r = _d.WanderM * MathF.Sqrt((float)_rng.NextDouble());
        var to = new Vector3(Home.X + MathF.Cos(a) * r, Home.Y, Home.Z + MathF.Sin(a) * r);
        ClearRoute();
        _fsm.Enter(GangMemberState.Walk, _d.WalkGiveUpSeconds);
        Request(to);
    }

    void TickWalk(float dt)
    {
        if (_fsm.Tick(dt)) { Settle(); return; } // not there in time
        if (_next < _route.Count) { if (Follow(dt, _k.WalkSpeed)) Settle(); }
        else if (RouteRequest == null) Settle(); // no route came
    }

    void TickGuard(float dt)
    {
        var escort = Escort is { Alive: true } e ? e : null;
        var goal = escort?.Position ?? Home;
        var stop = escort != null ? _d.GuardFollowM : 1.5f;
        var far = Flat(Position, goal) > 2 * stop;
        if (GoTo(goal, dt, far ? _k.RunSpeed : _k.WalkSpeed, stop) && escort != null) Face(escort.Position, dt);
    }

    void TickChase(GangPlayer t, float dt)
    {
        var d = Flat(Position, t.Feet);
        var reach = _k.Melee.ReachM;
        if (d <= reach * 0.9f && MathF.Abs(t.Feet.Y - Position.Y) <= ReachDy && t.OnFoot)
        {
            ClearRoute();
            Face(t.Feet, dt);
            _fsm.Enter(GangMemberState.Windup, _k.Melee.WindupSeconds);
            return;
        }
        if (GoTo(t.Feet, dt, _k.RunSpeed, reach * 0.8f)) return;
        if (_route.Count == 0 && RouteRequest == null && d <= DirectM) Step(t.Feet, dt, _k.RunSpeed); // no route and nearly there: straight at it
    }

    void TickFlee(float dt)
    {
        if (_fsm.Tick(dt)) { Ended = WorldDespawnReason.Removed; return; } // out of time: gone where it stands
        if (Flat(Position, _edge) <= _d.EdgeArriveM) { Ended = WorldDespawnReason.Removed; return; }
        if (_next < _route.Count && Follow(dt, _k.RunSpeed)) Ended = WorldDespawnReason.Removed; // the end of the route is the edge, or the nearest it can be reached
    }

    void TickBoard(float dt)
    {
        var reach = _d.BoardReachM;
        if (GoTo(_door, dt, _k.RunSpeed, reach)) { Arrive(); return; }
        if (_route.Count == 0 && RouteRequest == null && Flat(Position, _door) <= DirectM) { Step(_door, dt, _k.RunSpeed); if (Flat(Position, _door) <= reach) Arrive(); } // no route and nearly there: straight in
    }

    void Arrive()
    {
        if (Ended != null) return;
        Ended = WorldDespawnReason.Removed;
        Boarded?.Invoke(this);
    }

    void Strike()
    {
        _fsm.Enter(GangMemberState.Swing, _k.Melee.IntervalSeconds - _k.Melee.WindupSeconds);
        if (_target is { OnFoot: true } t && Flat(Position, t.Feet) <= _k.Melee.ReachM && MathF.Abs(t.Feet.Y - Position.Y) <= ReachDy)
            _world.Hurt(t.Id, _k.Melee.Damage, HitId); // only if it is still in reach: a player who stepped out of the windup is not hit
    }

    /// <summary>Stands: a Thief idles, an Enforcer or the Gang boss guards.</summary>
    void Settle()
    {
        ClearRoute();
        if (Kind == GangMemberKind.Thief) _fsm.Enter(GangMemberState.Idle, IdleSeconds());
        else _fsm.Enter(GangMemberState.Guard);
    }

    float IdleSeconds() => _d.IdleMinSeconds + (float)_rng.NextDouble() * (_d.IdleMaxSeconds - _d.IdleMinSeconds);

    // ---- movement ----

    /// <summary>
    /// Goes towards <paramref name="goal"/> on a route; true when within <paramref name="stop"/> of it (standing). Asks for a route when it has none or its end is
    /// not where the goal is now, at most every <see cref="GangConfig.RepathSeconds"/>; walks the one it has meanwhile.
    /// </summary>
    bool GoTo(Vector3 goal, float dt, float speed, float stop)
    {
        if (Flat(Position, goal) <= stop) { ClearRoute(); return true; }
        var routeToGoal = _route.Count > 0 && Flat(_route[^1], goal) <= _d.RepathM;
        if (!routeToGoal && RouteRequest == null && _repath.Ready) { Request(goal); _repath.Start(_d.RepathSeconds); }
        if (_next < _route.Count) Follow(dt, speed);
        return false;
    }

    void Request(Vector3 to) { RouteRequest = new GangRouteRequest(Position, to); RequestSeq = 0; }

    void ClearRoute() { _route.Clear(); _next = 0; RouteRequest = null; RequestSeq = 0; }

    /// <summary>Walks the route from the current point, turning towards each leg. True when its end is reached.</summary>
    bool Follow(float dt, float speed)
    {
        var budget = speed * dt;
        var turn = _d.TurnRateDegPerS * MathF.PI / 180f * dt;
        while (_next < _route.Count)
        {
            var to = _route[_next];
            var d = Vector3.Distance(Position, to);
            if (d > 0.01f) Yaw = TurretMath.StepAngle(Yaw, TurretMath.YawTo(Position, to), turn);
            if (d <= budget) { Position = to; budget -= d; _next++; continue; }
            Position += (to - Position) * (budget / d);
            return false;
        }
        return true;
    }

    /// <summary>A straight step at a point within a few metres (the last of a chase: the route's end is the point nearest the mesh, not the player).</summary>
    void Step(Vector3 to, float dt, float speed)
    {
        var d = Flat(Position, to);
        if (d < 0.01f) return;
        var step = MathF.Min(speed * dt, d);
        var dy = to.Y - Position.Y;
        Position = new Vector3(Position.X + (to.X - Position.X) / d * step, MathF.Abs(dy) <= 1.2f ? to.Y : Position.Y, Position.Z + (to.Z - Position.Z) / d * step);
        Face(to, dt);
    }

    void Face(Vector3 to, float dt)
    {
        if (Flat(Position, to) < 0.05f) return;
        Yaw = TurretMath.StepAngle(Yaw, TurretMath.YawTo(Position, to), _d.TurnRateDegPerS * MathF.PI / 180f * dt);
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
