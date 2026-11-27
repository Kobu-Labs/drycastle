using System.Numerics;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Weapons;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>The kinds of Gang member (GLOSSARY "Gang member"): the Thieves' three, and the Rival crew's Gunman, who fights at range (GLOSSARY "Gunman"). Travels in bits 1-2 of the hit-point byte of the entity state (full: a fifth kind needs bit 7).</summary>
public enum GangMemberKind : byte { Thief = 0, Enforcer = 1, GangBoss = 2, Gunman = 3 }

/// <summary>
/// What a Gang member is doing: the replicated state byte. 8 is the van's (ticket 03: Board); 9 to 16 are the stealing states (ticket 04: Seek, Approach, Steal, Haul, Load and the three climb
/// states); 17 and 18 are ticket 05's; 19 to 22 are the electrocution (ticket 07: Shocked, Falling, Down, GetUp); 23 to 25 the Rival crew's (#495, #496: a Gunman's Aim and Fire, a posted member's Posted). A new state goes after the last one. The client treats an unknown value as standing.
/// </summary>
public enum GangMemberState : byte
{
    Idle = 0, Walk = 1, Guard = 2, Chase = 3, Windup = 4, Swing = 5, Flee = 6, Dead = 7, Board = 8,
    Seek = 9, Approach = 10, Steal = 11, Haul = 12, Load = 13, ClimbUp = 14, WorkAloft = 15, ClimbDown = 16,
    HopIn = 17, HopOut = 18,
    Shocked = 19, Falling = 20, Down = 21, GetUp = 22,
    Aim = 23, Fire = 24, Posted = 25,
}

/// <summary>A living player as the Gang members see them: where their feet are and whether they stand on the ground (a rider in a vehicle cannot be struck).</summary>
public readonly record struct GangPlayer(ushort Id, Vector3 Feet, bool OnFoot);

/// <summary>A member waits for a route on the navigation mesh. The gang hands the oldest request to the route worker (<see cref="ThiefGang.BeginRoute"/>) and gives it the answer when it comes back (<see cref="ThiefGang.DeliverRoute"/>).</summary>
public readonly record struct GangRouteRequest(Vector3 From, Vector3 To);

/// <summary>A member was killed: which, by whom, where it lay (on the ground, also when it died aloft), what it carried (see <see cref="GangRules.PackHp"/>) and that piece's id in its source's numbers.</summary>
public readonly record struct GangKilled(ushort HitId, ushort Killer, GangMemberKind Kind, Vector3 Where, byte Carried, int CarriedId = 0);

/// <summary>What the members need from the server: the players they fight, and the hand that hurts one.</summary>
public interface IGangWorld
{
    /// <summary>The living players now (on foot or riding), humans and bots alike; refreshed once a tick before the members tick.</summary>
    IReadOnlyList<GangPlayer> Players { get; }

    /// <summary>The player takes <paramref name="damage"/> from the member with this hit id (<see cref="GangRules.FirstHitId"/> up).</summary>
    void Hurt(ushort player, float damage, ushort memberHitId);

    /// <summary>The nearest walkable point to <paramref name="p"/> (the navigation mesh); the point itself when the world cannot tell. Used for the spots chasers take round a player.</summary>
    Vector3 Snap(Vector3 p) => p;

    /// <summary>What a walking member asks of the world on its way (a trunk, a closed door, a parked vehicle: <see cref="WalkGuard"/>); null: it walks its route as it is.</summary>
    IWalkerGround? Walk => null;

    /// <summary>True when nothing a bullet stops on (walls, cover, a vehicle's hull) lies between the two points: a member's line of sight. The world that cannot tell sees everything.</summary>
    bool Clear(Vector3 from, Vector3 to) => true;

    /// <summary>A Gunman's round (or a shotgun's fan of pellets): the world resolves it against the walls and the players on server time (no rewind), hurts whoever it hits and shows it to everyone.</summary>
    void Shoot(GangShot shot) { }
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
    public const byte TypeId = 57; // 54 is the wildlife Flock

    /// <summary>
    /// The id a member has as a hit target: between the turret records (0x7000 up) and the Raid vans (0x73E0), below the wild animals (0x7400) and the pets (0x7800), clear of players, vehicles, citizens (40000 up),
    /// the demon, target dummies (50000 up) and the Bigfoot. Not the id of the world entity.
    /// </summary>
    public const ushort FirstHitId = 0x7100, HitIdCount = 0x2E0; // 0x7100..0x73DF: under the Raid vans (0x73E0) and the wild animals (0x7400)

    /// <summary>The most members of one Raid.</summary>
    public const int MaxCrew = 24;

    public static bool IsHitId(ushort id) => id >= FirstHitId && id < FirstHitId + HitIdCount;

    // The hit-point byte: bit 0 alive, bits 1-2 kind (all four values used), bits 3-5 what is carried (0 nothing, 1 and up the Loot kind plus one: ticket 04), bit 6 speaker (ticket 09),
    // bit 7 the Rival crew (#495: the client draws it apart from the Thieves, and its Gang boss, the Rival dealer, without the Gold chain).
    const int AliveBit = 1, KindShift = 1, KindMask = 3, CarriedShift = 3, CarriedMask = 7, SpeakerBit = 64, RivalBit = 128;

    public static byte PackHp(GangMemberKind kind, bool alive, byte carried, bool speaker = false, bool rival = false) =>
        (byte)((alive ? AliveBit : 0) | (((int)kind & KindMask) << KindShift) | ((carried & CarriedMask) << CarriedShift) | (speaker ? SpeakerBit : 0) | (rival ? RivalBit : 0));

    public static bool AliveOf(byte hp) => (hp & AliveBit) != 0;
    public static GangMemberKind KindOf(byte hp) => (GangMemberKind)Math.Min((hp >> KindShift) & KindMask, (int)GangMemberKind.Gunman);
    public static byte CarriedOf(byte hp) => (byte)((hp >> CarriedShift) & CarriedMask);
    public static bool SpeakerOf(byte hp) => (hp & SpeakerBit) != 0;
    public static bool RivalOf(byte hp) => (hp & RivalBit) != 0;

    /// <summary>A state byte as a state; a value this build does not know stands.</summary>
    public static GangMemberState StateOf(byte state) => state <= (byte)GangMemberState.Posted ? (GangMemberState)state : GangMemberState.Idle;

    public static void Register(WorldEntityRegistry registry)
    {
        registry.RegisterOnce(new WorldEntityDef(TypeId, "gang member", HasHp: true));
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
/// <see cref="GangRules.PackHp"/> (kind, alive, what it carries, the speaker, the Rival crew), position = where its feet stand.
/// </para>
/// </summary>
public sealed partial class GangMember : IWorldEntity
{
    /// <summary>
    /// The member's states and the edges between them. A Thief idles and now and then walks off; an Enforcer or the Gang boss guards (follows a Thief or stands at the
    /// post); whoever is given a target chases it, winds up and swings, and goes back; Flee runs for the map edge and ends there. The route cursor belongs to the
    /// state that follows a route (Walk, Guard, Chase, Flee), not to the machine. Stealing states come with ticket 04.
    /// </summary>
    public static readonly StateMachineSpec<GangMemberState> Spec = StateMachineSpec<GangMemberState>
        .Define("GangMember.State", "What a Gang member of a Raid is doing; the replicated state byte.", GangMemberState.Idle)
        .Allow(GangMemberState.Idle, GangMemberState.Walk, GangMemberState.Guard, GangMemberState.Chase, GangMemberState.Seek, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Walk, GangMemberState.Idle, GangMemberState.Chase, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Guard, GangMemberState.Idle, GangMemberState.Chase, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Chase, GangMemberState.Windup, GangMemberState.Idle, GangMemberState.Guard, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Windup, GangMemberState.Swing, GangMemberState.Idle, GangMemberState.Guard, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Swing, GangMemberState.Chase, GangMemberState.Idle, GangMemberState.Guard, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Seek, GangMemberState.Approach, GangMemberState.Chase, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Approach, GangMemberState.Steal, GangMemberState.ClimbUp, GangMemberState.Seek, GangMemberState.Chase, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Steal, GangMemberState.Haul, GangMemberState.Seek, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Approach, GangMemberState.HopIn)
        .Allow(GangMemberState.HopIn, GangMemberState.Steal, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Steal, GangMemberState.HopOut)
        .Allow(GangMemberState.HopOut, GangMemberState.Haul, GangMemberState.Seek, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.ClimbUp, GangMemberState.WorkAloft, GangMemberState.ClimbDown, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.WorkAloft, GangMemberState.ClimbDown, GangMemberState.Shocked, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Shocked, GangMemberState.Falling, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Falling, GangMemberState.Down, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Down, GangMemberState.GetUp, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.GetUp, GangMemberState.ClimbUp, GangMemberState.Seek, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.ClimbDown, GangMemberState.Haul, GangMemberState.Seek, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Haul, GangMemberState.Load, GangMemberState.Seek, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Load, GangMemberState.Seek, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Board, GangMemberState.Flee)
        // the Rival crew (#495, #496): a Gunman aims and fires instead of winding up; a posted member holds its spot
        .Allow(GangMemberState.Guard, GangMemberState.Aim, GangMemberState.Posted)
        .Allow(GangMemberState.Idle, GangMemberState.Aim, GangMemberState.Posted)
        .Allow(GangMemberState.Chase, GangMemberState.Aim)
        .Allow(GangMemberState.Aim, GangMemberState.Fire, GangMemberState.Chase, GangMemberState.Posted, GangMemberState.Guard, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Fire, GangMemberState.Aim, GangMemberState.Chase, GangMemberState.Posted, GangMemberState.Guard, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Posted, GangMemberState.Aim, GangMemberState.Windup, GangMemberState.Flee, GangMemberState.Board)
        .Allow(GangMemberState.Windup, GangMemberState.Posted)
        .Allow(GangMemberState.Swing, GangMemberState.Posted)
        .FromAnywhere(GangMemberState.Dead)
        .Note(GangMemberState.Idle, "A Thief standing (clock: a random time, then it walks to a point near the post); a Thief that has Loot work (its Raid gave it) goes straight on to Seek instead.")
        .Note(GangMemberState.Walk, "A Thief on a route to a point near the post (clock: the time budget to arrive, then it stands again).")
        .Note(GangMemberState.Guard, "An Enforcer follows the Thief it escorts, or stands at the post; the Gang boss stands at the post. Never steals.")
        .Note(GangMemberState.Chase, "Runs at its own spot on a ring round the player the Raid is fighting (a route from the navigation mesh, asked for at most every repath_seconds); winds up once within reach. Ends (back to Idle or Guard) when the Raid forgets the player.")
        .Note(GangMemberState.Windup, "The swing's telegraph (clock: windup_seconds): a player can step out of reach. Ends in Swing, or in a stand if the target is gone.")
        .Note(GangMemberState.Swing, "The blow lands at its start, only if the target is still within reach; then recovers (clock: interval minus windup) and chases or stands again.")
        .Note(GangMemberState.Flee, "Runs for the map edge on a route (clock: flee_seconds); the entity ends there (no state follows).")
        .Note(GangMemberState.Board, "Called back by the van: runs to its side door on a route (never fights) and ends there, counted as boarded. Flee when the van is destroyed or leaves without it.")
        .Note(GangMemberState.Seek, "A Thief looks for a target: the nearest free one of a kind chosen by weight near the van's door, one Thief per target (clock: it asks again after seek_retry_seconds when nothing is free or the van takes no more). A Thief with free hands still joins a fight.")
        .Note(GangMemberState.Approach, "Walks a route to the target's work spot (clock: approach_give_up_seconds; a target that is gone, a lost route or the clock ends it and the target is avoided for a while). Arrived: Steal, or ClimbUp when the target needs a climb.")
        .Note(GangMemberState.Steal, "Stands at the target, a still target (clock: the kind's steal_seconds, or pickup_seconds for Loot that lies). When the clock runs out the Thief has it and it is gone from its place; a target that goes meanwhile ends it. Landed inside a fence (HopIn) it first walks straight to the pet and the clock starts within grab_m of it. A Working state.")
        .Note(GangMemberState.HopIn, "Hops over the fence of the pet's garden: an arc from the point outside the fence to the point inside it, off the navigation mesh (clock: hop_seconds). A Working state; then Steal, where it walks to the pet.")
        .Note(GangMemberState.HopOut, "Back out of the garden with the pet (or with nothing when the pet went): walks straight to where it landed (no clock), then the arc out over the fence (clock: hop_seconds), then Haul with the pet. A Working state.")
        .Note(GangMemberState.ClimbUp, "Climbs straight up from the climb's base to its top at climb_speed, off the navigation mesh (no clock: the distance). A Working state; shot dead here it falls to the ground.")
        .Note(GangMemberState.WorkAloft, "Works at the top of the climb (clock: the kind's steal_seconds), then has the thing. A Working state; shot dead here it falls to the ground.")
        .Note(GangMemberState.ClimbDown, "Climbs straight down to the base at climb_speed, then hauls what it has (or seeks again when it has nothing). A Working state; shot dead here it falls to the ground.")
        .Note(GangMemberState.Shocked, "Electrocuted at the top of a climb (the kind's hazard rolled when the Thief started to work aloft; clock: the shock's seconds): held in place, beams and sound on the clients. Costs no hits. Ends in Falling; shot dead here it falls and lies.")
        .Note(GangMemberState.Falling, "Falls from the top to the base's height under gravity (no clock: the height), off the navigation mesh. Ends in Down; shot dead here it keeps falling and lies.")
        .Note(GangMemberState.Down, "Lies knocked down (clock: the shock's down seconds); a lying hit volume, normal hits count. Ends in GetUp.")
        .Note(GangMemberState.GetUp, "Gets up (clock: the shock's get-up seconds), then goes for the same target again (ClimbUp), or looks for another when it is gone (Seek). All four are Working states: the Thief does not fight.")
        .Note(GangMemberState.Haul, "Carries the Loot to the van's door, faster than it approached (clock: haul_give_up_seconds, then it drops the Loot where it stands). A hit that does not kill it drops the Loot where it stands too (gang.hit_drops_loot; Load, ClimbDown and HopOut as well) and it seeks again, with free hands, so it joins the fight. A Working state; the client attaches the prop from the hp byte.")
        .Note(GangMemberState.Load, "Tosses the Loot into the van at the door (clock: load_seconds): counted in the van, the Thief seeks again. A van that cannot take it any more gets it dropped at the Thief's feet.")
        .Note(GangMemberState.Aim, "A Gunman with its target in sight (a clear line from its eyes to the target's chest, looked at every sight.check_seconds) and within gun.range_m: stands, turns to it (clock: gun.aim_seconds, the telegraph), then Fire. Sight lost: Chase, or Posted for a posted member.")
        .Note(GangMemberState.Fire, "The burst of its weapon (rival_crew.json gun.weapons): the first round leaves at the start, the rest every round_gap_seconds, each only while the target is in sight; the aim error turns each round off its line. Then Aim again, or Chase / Posted when the target is out of sight.")
        .Note(GangMemberState.Posted, "Holds a given spot and facing (a trap house defender: no navigation mesh indoors). Never walks or is pushed; turns to a target, shoots it in sight (Aim, Fire) or, melee, strikes it in reach (Windup, Swing); with none it turns back to its facing.")
        .Note(GangMemberState.Dead, "Lies for corpse_seconds (the clock's elapsed time), then the entity ends. Reached from any state; never left.")
        .Build();

    const float DirectM = 3f;       // a chaser this near its target with no route walks straight at it
    const float ReachDy = 2.2f;     // a blow reaches no higher or lower than this
    const float SlotRing = 0.75f;   // a chaser's spot on the ring round its target: this fraction of the melee reach from it (inside the 0.9 a blow starts at)
    const float SlotArriveM = 0.3f; // at its spot when this near
    const float SlotStrikeM = 1f;   // it strikes from within this of its spot
    const float SlotRefreshM = 0.75f; // its spot is worked out again when the target moved this far
    const float GoldenAngle = 2.3999632f; // slots spread evenly whatever their number, and a slot's angle never changes when others come and go

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
    WalkGuard _guard;               // the detour round what the navigation mesh does not hold (trunk, closed door, parked vehicle)
    Vector3 _slotPoint, _slotFor;   // the spot on the ring round the target, and where the target stood when it was worked out
    bool _slotValid;

    public GangMember(GangConfig cfg, GangMemberKind kind, IGangWorld world, Vector3 position, float yaw, ushort look, ushort hitId, int seed)
    {
        _d = cfg; _k = cfg.Of(kind); _world = world; _rng = new Random(seed);
        Kind = kind; Position = Home = position; Yaw = yaw; Look = look; HitId = hitId;
        if (kind != GangMemberKind.Thief) _fsm.Enter(GangMemberState.Guard);
        else _fsm.Restart(IdleSeconds());
        Weapon = _k.Gun is { Weapons.Count: > 0 } gun ? GangLook.Gun(gun, look) : null;
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
    /// <summary>Carries a speaker (a song plays from it until it dies, then it drops as an Item). Rolled by <see cref="GangAudio"/> when the server first sees the member; bit 6 of the hp byte.</summary>
    public bool Speaker { get; set; }
    /// <summary>A member of a Rival crew (<see cref="GangCrew.RivalCrew"/>, set by its gang): bit 7 of the hp byte, so the client draws it apart from the Thieves.</summary>
    public bool Rival { get; init; }
    /// <summary>What it carries: 0 nothing, 1 and up the Loot kind plus one (<see cref="LootKinds.CarriedOf"/>). Travels in the hp byte. A killed member keeps what it carried (the Loot lies where it fell).</summary>
    public byte Carried { get; set; }
    /// <summary>Hands not free: working or carrying Loot. A busy Thief does not join a fight.</summary>
    public bool Busy => Carried != 0 || Working;
    /// <summary>Enforcers and the Gang boss fight when the Raid does; a Thief with free hands joins.</summary>
    public bool CanFight => Alive && State != GangMemberState.Flee && State != GangMemberState.Board && (Kind != GangMemberKind.Thief || !Busy);
    /// <summary>The player it fights now, or null.</summary>
    public GangPlayer? Target => _target;
    /// <summary>Its place among the members fighting the same player (0 up, kept while the fight lasts; -1 none): the angle of its spot on the ring round them. Set by <see cref="GangRoster"/>.</summary>
    public int Slot { get; set; } = -1;
    /// <summary>The player <see cref="Slot"/> was given for.</summary>
    public ushort SlotOf { get; set; }
    /// <summary>The spot it heads for when chasing: on the ring round the target (its feet when it has no slot or the spot cannot be used).</summary>
    public Vector3? SlotPoint => _target is { } t && Slot >= 0 && _slotValid ? _slotPoint : _target?.Feet;
    /// <summary>Walking about or fighting (not at work, not down or aloft, alive): a push from a neighbour may move it.</summary>
    public bool Pushable => _post == null && State is GangMemberState.Idle or GangMemberState.Walk or GangMemberState.Guard or GangMemberState.Chase or GangMemberState.Windup or GangMemberState.Swing or GangMemberState.Seek or GangMemberState.Approach or GangMemberState.Haul or GangMemberState.Board or GangMemberState.Flee or GangMemberState.Aim or GangMemberState.Fire;
    /// <summary>Moves it a little (<see cref="GangRoster"/>'s separation). Only for <see cref="Pushable"/> members.</summary>
    public void Nudge(float dx, float dz) { if (Pushable && Ended == null) Position = new Vector3(Position.X + dx, Position.Y, Position.Z + dz); }
    /// <summary>The weighted hits taken, and the Hit count that kills it (GLOSSARY "Hit count").</summary>
    public float HitsTaken => _taken;
    public float HitsNeeded => _k.Hits;
    /// <summary>The player whose hit killed it (0 when something else did).</summary>
    public ushort Killer { get; private set; }
    /// <summary>The route it is waiting for (set by the member, cleared by <see cref="SetRoute"/>) and the place in the gang's queue (0: not queued yet; set by the gang).</summary>
    public GangRouteRequest? RouteRequest { get; private set; }
    public long RequestSeq { get; set; }
    /// <summary>The ticket of the request while the route worker has it (0: not sent). An answer with another ticket is stale: the request was cleared or replaced since.</summary>
    public long RouteTicket { get; private set; }
    Countdown _routeLost;
    /// <summary>Seconds after which a request the worker never answered is offered again.</summary>
    public const float RouteLostSeconds = 5f;

    /// <summary>The request went to the route worker under <paramref name="ticket"/>; it is not offered again while that is out.</summary>
    public void RouteSent(long ticket) { RouteTicket = ticket; _routeLost.Start(RouteLostSeconds); }

    /// <summary>The worker's answer is no use (the member died or no longer asks for it): forgets the ticket.</summary>
    public void RouteDropped() { RouteTicket = 0; _routeLost.Cancel(); }
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
    public HitTarget HitVolume => new(HitId, Position, State == GangMemberState.Down ? Stance.Prone : Stance.Stand, Yaw, HeadGuarded: false, Scale: _k.HitScale);

    public WorldState Serialize() => WorldState.Make(Position, Yaw, 0, Look, (byte)State, GangRules.PackHp(Kind, Alive, Carried, Speaker && Alive, Rival));

    // ---- hits ----

    /// <summary>A hit landed. <paramref name="weight"/> is what it counts (<see cref="GangRules.Weight"/>). True when this hit killed it. A dead member takes nothing.</summary>
    public bool Hit(ushort attacker, float weight)
    {
        if (Ended != null || !Alive || !(weight > 0)) return false;
        _taken += weight;
        if (_taken < _k.Hits) { DropOnHit(); return false; }
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
    public void Remove(WorldDespawnReason reason = WorldDespawnReason.Removed) { ReleaseJob(); Ended ??= reason; }

    void Die(ushort killer)
    {
        _groundY = Aloft ? _base.Y : HopGroundY();
        ReleaseJob();
        if (Work != null && CarriedOrigin is { } lost) Work.Drop(lost, new Vector3(Position.X, _groundY, Position.Z)); // the Loot lies where it fell
        Killer = killer;
        _taken = MathF.Max(_taken, _k.Hits);
        _target = null;
        ClearRoute();
        _fsm.Enter(GangMemberState.Dead);
        Died?.Invoke(this);
    }

    // ---- orders from the gang ----

    /// <summary>Who to fight now (null: nobody). Refreshed every tick by the gang with where the player stands.</summary>
    public void SetTarget(GangPlayer? target)
    {
        _target = Alive ? target : null;
        if (_target == null) { Slot = -1; _slotValid = false; }
    }

    /// <summary>Runs for <paramref name="edge"/> (a point at the map edge) on a route; the entity ends when it arrives. Not for the dead.</summary>
    public void Flee(Vector3 edge)
    {
        if (!Alive || Ended != null || State == GangMemberState.Flee) return;
        _target = null;
        _post = null; // it leaves its spot
        ReleaseJob();
        LeaveAloft();
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
        _post = null;
        ReleaseJob();
        LeaveAloft();
        _door = door;
        ClearRoute();
        _fsm.Enter(GangMemberState.Board);
        Request(door);
    }

    /// <summary>The route the gang found for the request (empty: none). A route replaces the one being followed.</summary>
    public void SetRoute(IReadOnlyList<Vector3> route)
    {
        RouteRequest = null; RequestSeq = 0; RouteDropped();
        _route.Clear(); _next = 0; _guard.Reset();
        if (route.Count < 2) { RouteFailed(); return; }
        _route.AddRange(route);
        // the answer came after some ticks: when the member has run past the first point, it goes on from the next
        if (route.Count > 2 && Flat(route[1], Position) < Flat(route[1], route[0])) _next = 1;
    }

    void RouteFailed()
    {
        _repath.Start(_d.RepathSeconds * 2); // do not ask again at once
        if (State == GangMemberState.Flee) Ended = WorldDespawnReason.Removed; // nowhere to run on foot: gone
        else if (State == GangMemberState.Walk) Settle();
        else if (State == GangMemberState.Approach) GiveUpJob(); // no way to the target: another one
        // a boarding member with no route asks again after the pause (TickBoard)
    }

    // ---- the tick ----

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        _repath.Tick(dt);
        if (_routeLost.Tick(dt)) RouteTicket = 0; // the worker never answered: ask again
        TickAvoid(dt);
        if (Carried != 0 && Alive && Work != null && CarriedOrigin is { } carrying) Work.Carrying(carrying, Position, Yaw); // a piece that travels with the Thief in the world follows it
        switch (State)
        {
            case GangMemberState.Dead:
                _fsm.Tick(dt); // Elapsed is the time it has lain
                FallStep(dt);
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
                if (_fsm.Tick(dt)) { if (_target != null && CanFight) _fsm.Enter(_post != null ? GangMemberState.Posted : GangMemberState.Chase); else Settle(); }
                return;
        }
        if (_target is { } t && CanFight)
        {
            if (Ranged) { TickGunFight(t, dt); return; } // a Gunman shoots from where it sees the target (GangMember.Gun)
            if (_post != null) { TickPostedMelee(t, dt); return; } // a posted member never leaves its spot
            if (_fsm.Enter(GangMemberState.Chase)) { ClearRoute(); ReleaseJob(); } // a route (or a request) for the walk it was on leads nowhere now, nor does the target it was going for
            TickChase(t, dt);
            return;
        }
        if (State is GangMemberState.Chase or GangMemberState.Aim or GangMemberState.Fire) Settle(); // the target is gone: back to the post
        switch (State)
        {
            case GangMemberState.Idle: TickIdle(dt); break;
            case GangMemberState.Walk: TickWalk(dt); break;
            case GangMemberState.Guard: TickGuard(dt); break;
            case GangMemberState.Posted: TickPosted(dt); break;
            default: TickStealing(dt); break;
        }
    }

    void TickIdle(float dt)
    {
        if (Kind == GangMemberKind.Thief && Work != null) { EnterSeek(); return; } // a Thief with work to do looks for it at once
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
        var goal = ChaseGoal(t, reach, out var slotted);
        // in reach, and at its own spot when it has one (so they surround instead of all striking from the side they came)
        if (d <= reach * 0.9f && (!slotted || Flat(Position, goal) <= SlotStrikeM || d <= reach * 0.5f) && MathF.Abs(t.Feet.Y - Position.Y) <= ReachDy && t.OnFoot)
        {
            ClearRoute();
            Face(t.Feet, dt);
            _fsm.Enter(GangMemberState.Windup, _k.Melee.WindupSeconds);
            return;
        }
        if (GoTo(goal, dt, _k.RunSpeed, slotted ? SlotArriveM : reach * 0.8f)) return;
        if (_route.Count == 0 && RouteRequest == null && Flat(Position, goal) <= DirectM) Step(goal, dt, _k.RunSpeed); // no route and nearly there: straight at it
    }

    /// <summary>
    /// Where a chaser heads: its spot on a ring (<see cref="SlotRing"/> of the reach) round the target, at an angle by its slot from the way the Raid came (<see cref="Home"/>), so several chasers
    /// surround the player instead of queueing on one path. Worked out again when the target moved. The spot goes through the world's snap; one that ends too far from the target to strike from is not used.
    /// </summary>
    Vector3 ChaseGoal(GangPlayer t, float reach, out bool slotted)
    {
        slotted = false;
        if (Slot < 0) return t.Feet;
        if (!_slotValid || Flat(t.Feet, _slotFor) > SlotRefreshM)
        {
            _slotFor = t.Feet;
            var away = MathF.Atan2(Home.Z - t.Feet.Z, Home.X - t.Feet.X);
            var a = away + Slot * GoldenAngle;
            var raw = new Vector3(t.Feet.X + MathF.Cos(a) * reach * SlotRing, t.Feet.Y, t.Feet.Z + MathF.Sin(a) * reach * SlotRing);
            var snapped = _world.Snap(raw);
            _slotPoint = Flat(snapped, t.Feet) <= reach * 0.9f && MathF.Abs(snapped.Y - t.Feet.Y) <= ReachDy ? snapped : t.Feet;
            _slotValid = true;
        }
        slotted = Flat(_slotPoint, t.Feet) > 0.01f;
        return _slotPoint;
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

    /// <summary>Stands: a Thief idles, an Enforcer, a Gunman or the Gang boss guards, a posted member holds its spot.</summary>
    void Settle()
    {
        ClearRoute();
        if (_post != null) { _fsm.Enter(GangMemberState.Posted); return; } // a posted member stands at its spot
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

    void Request(Vector3 to) { RouteRequest = new GangRouteRequest(Position, to); RequestSeq = 0; RouteDropped(); }

    void ClearRoute() { _guard.Reset(); _route.Clear(); _next = 0; RouteRequest = null; RequestSeq = 0; RouteDropped(); }

    /// <summary>Walks the route from the current point, turning towards each leg. True when its end is reached.</summary>
    bool Follow(float dt, float speed)
    {
        var budget = speed * dt;
        var turn = _d.TurnRateDegPerS * MathF.PI / 180f * dt;
        _guard.Tick(dt);
        while (_next < _route.Count)
        {
            var leg = _route[_next];
            // the route is the mesh's: what it does not hold (trunk, closed door, parked vehicle) is gone round by a point beside the leg, or waited at (WalkGuard)
            if (_guard.Head(Position, leg, budget, _world.Walk) is not { } to) return false;
            var d = Vector3.Distance(Position, to);
            if (d > 0.01f) Yaw = TurretMath.StepAngle(Yaw, TurretMath.YawTo(Position, to), turn);
            if (d <= budget) { Position = to; budget -= d; if (to == leg) _next++; continue; }
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
}
