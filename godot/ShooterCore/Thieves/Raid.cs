using System.Numerics;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>
/// One Raid (GLOSSARY "Raid"): a <see cref="RaidVan"/> and the <see cref="ThiefGang"/> that rides in it, pure. The van drives the path of its <see cref="RaidPlan"/>; the Raid lets the crew out
/// one by one at the side door when it opens (<see cref="VanConfig.CrewStaggerSeconds"/>), calls the living crew back when the van is to leave, sends the crew fleeing when the van is
/// destroyed (or leaves without them) and tells the world when it is over. It owns no clock beyond the stagger: the server ticks it after the entities tick.
/// </summary>
public sealed class Raid : IRaidCrew
{
    readonly VanConfig _cfg;
    readonly Func<Vector3, Vector3>? _snap;
    readonly Func<Vector3, Vector3> _edgeFor;
    readonly Queue<GangMemberKind> _queue = new();
    readonly LootBook? _loot;
    readonly List<LootOrigin> _loaded = new();
    Countdown _stagger;
    Edge _over;   // the end of the Raid, announced once (Over never goes back)

    /// <param name="loot">The event's Loot: with it the Thieves of the gang steal and load the van (<see cref="RaidWork"/>); without, they stand about.</param>
    public Raid(RaidVan van, ThiefGang gang, VanConfig cfg, GangConfig gangCfg, GangRaidCounts crew, RaidPlan plan, Func<Vector3, Vector3>? snap, Func<Vector3, Vector3> edgeFor, LootBook? loot = null)
        : this(van, gang, cfg, plan, Order(crew), snap, edgeFor, loot) { }

    /// <summary>
    /// A Raid whose crew gets out in <paramref name="order"/>.
    /// </summary>
    public Raid(RaidVan van, ThiefGang gang, VanConfig cfg, RaidPlan plan, IEnumerable<GangMemberKind> order, Func<Vector3, Vector3>? snap, Func<Vector3, Vector3> edgeFor, LootBook? loot)
    {
        Van = van; Gang = gang; _cfg = cfg; Plan = plan; _snap = snap; _edgeFor = edgeFor; _loot = loot;
        foreach (var k in order) _queue.Enqueue(k);
        van.Crew = this;
        van.StateChanged += OnVanState;
        if (loot != null) gang.Work = new RaidWork(loot, this);
        gang.Boarded += OnBoarded;
        if (van.State != RaidVanState.Arriving) OnVanState(RaidVanState.Arriving, van.State); // a van put at its stop (the fallback) is already opening
    }

    public RaidVan Van { get; }
    public ThiefGang Gang { get; }
    public RaidPlan Plan { get; }
    /// <summary>The entity id of the van (set by the roster).</summary>
    public ushort VanEntity { get; set; }
    /// <summary>Crew members that got back aboard.</summary>
    public int Boarded { get; private set; }

    /// <summary>The Loot loaded in the van, in the order it was tossed in. Comes back to its places when the van is destroyed; stays with a van that drove off.</summary>
    public IReadOnlyList<LootOrigin> Loaded => _loaded;

    /// <summary>A Thief tosses a piece of Loot into the van (the van counts it). False when the van takes no more.</summary>
    public bool LoadIn(LootOrigin piece)
    {
        if (!Van.LoadLoot()) return false;
        _loaded.Add(piece);
        _loot?.Loaded(piece);
        return true;
    }

    /// <summary>A member got back aboard: what it still carried goes into the van (the door is open while the crew boards); a van that cannot take it sends it back to its place.</summary>
    void OnBoarded(GangMember m)
    {
        Boarded++;
        if (m.CarriedOrigin is { } piece && !LoadIn(piece)) _loot?.Return(piece);
    }

    public int StillToSpawn => _queue.Count;
    public int Afoot => Gang.Afoot;
    /// <summary>The crew sitting in the van: not out yet, or back aboard.</summary>
    public int Inside => _queue.Count + Boarded;

    /// <summary>Over: its van was destroyed or is gone (drove off, or the event removed it). What is stolen stays stolen until the round resets.</summary>
    public bool Over => Van.State == RaidVanState.Destroyed || Van.Ended != null;

    /// <summary>Raised once, when the Raid becomes <see cref="Over"/>.</summary>
    public event Action<Raid>? Ended;
    /// <summary>Raised when the van is destroyed with Loot in it: how many went back to their places (the Raid returns them through the <see cref="LootBook"/> before it raises this).</summary>
    public event Action<Raid, int>? LootReturned;

    /// <summary>The order the crew gets out: an Enforcer, the Gang boss, then Thieves and Enforcers alternating.</summary>
    public static List<GangMemberKind> Order(GangRaidCounts crew)
    {
        var thieves = crew.Thieves; var enforcers = crew.Enforcers; var bosses = crew.GangBosses;
        var list = new List<GangMemberKind>();
        if (enforcers > 0) { list.Add(GangMemberKind.Enforcer); enforcers--; }
        for (; bosses > 0; bosses--) list.Add(GangMemberKind.GangBoss);
        while (thieves > 0 || enforcers > 0)
        {
            if (thieves > 0) { list.Add(GangMemberKind.Thief); thieves--; }
            if (enforcers > 0) { list.Add(GangMemberKind.Enforcer); enforcers--; }
        }
        return list;
    }

    void OnVanState(RaidVanState from, RaidVanState to)
    {
        switch (to)
        {
            case RaidVanState.Opening:
                Gang.Post = Van.DoorPoint;
                _stagger.Start(0); // the first one steps out as the door starts to open
                break;
            case RaidVanState.Boarding:
                _queue.Clear(); // whoever is not out yet stays inside
                Gang.BoardAll(Van.DoorPoint);
                break;
            case RaidVanState.Closing:
                _queue.Clear();
                break;
            case RaidVanState.Leaving: // whoever the van did not wait for is left behind and runs
                Gang.FleeAll(_edgeFor(Van.Position));
                break;
            case RaidVanState.Destroyed:
                _queue.Clear();
                Gang.FleeAll(_edgeFor(Van.Position));
                var back = Van.LootReturnAll();
                _loot?.ReturnAll(_loaded); // the Loot loaded goes back to its places (a van that drives off keeps it)
                _loaded.Clear();
                LootReturned?.Invoke(this, back);
                break;
        }
    }

    /// <summary>One tick, after the entities tick: the crew gets out while the door is open, and the end of the Raid is announced once.</summary>
    public void Tick(float dt)
    {
        if (_queue.Count > 0 && Van.State is RaidVanState.Opening or RaidVanState.Parked)
        {
            _stagger.Tick(dt);
            if (_stagger.Ready)
            {
                SpawnNext();
                _stagger.Start(_cfg.CrewStaggerSeconds);
            }
        }
        if (_over.Pressed(Over)) Ended?.Invoke(this);
    }

    void SpawnNext()
    {
        var kind = _queue.Dequeue();
        var door = Van.DoorPoint;
        var at = _snap?.Invoke(door) ?? door;
        var away = new Vector2(door.X - Van.Position.X, door.Z - Van.Position.Z);
        Gang.Add(kind, at, away.LengthSquared() > 1e-6f ? RoadPath.Yaw(away) : Van.Yaw);
    }
}

/// <summary>
/// The Raids of the Thieves event (GLOSSARY "Raid"): every <see cref="Raid"/> in the world, behind one hit router for the vans (the match hands the van hit ids to it), their hit volumes, pose
/// rings and the stops used so far. Pure; the server launches Raids with a plan and ticks it. Gangs belong to the <see cref="GangRoster"/>, which keeps the one set of member hit ids and the
/// route ration.
/// </summary>
public sealed class RaidRoster : IGangHits
{
    readonly ThievesFile _cfg;
    readonly IGangWorld _world;
    readonly WorldEntityManager _entities;
    readonly GangRoster _gangs;
    readonly List<Raid> _raids = new();
    readonly List<Vector2> _used = new();
    readonly Random _rng;

    public RaidRoster(ThievesFile cfg, IGangWorld world, WorldEntityManager entities, GangRoster gangs, int seed = 0)
    {
        _cfg = cfg; _world = world; _entities = entities; _gangs = gangs;
        _rng = new Random(seed);
        entities.Despawned += OnDespawned;
    }

    public Action<string>? Log { get; set; }

    /// <summary>The event's Loot: every Raid launched from now on has Thieves that steal it. Null: they stand about.</summary>
    public LootBook? Loot { get; set; }

    /// <summary>What kind of hit a weapon byte is (<see cref="BigfootRules.SourceOf"/> needs the weapon list, which the server has): the default counts everything as a bullet.</summary>
    public Func<byte, HitSource> SourceOf { get; set; } = _ => HitSource.Bullet;

    /// <summary>A Raid ended (its van was destroyed or is gone).</summary>
    public event Action<Raid>? RaidOver;
    /// <summary>A van was destroyed with Loot in it: how many (ticket 04 returns them).</summary>
    public event Action<Raid, int>? LootReturned;
    /// <summary>A van honked.</summary>
    public event Action<Raid>? Honked;
    /// <summary>A Raid's van and gang exist (just launched).</summary>
    public event Action<Raid>? Launched;

    public IReadOnlyList<Raid> Raids => _raids;
    /// <summary>The Thieves' Raids not over: the vans in the village (a destroyed one is over, its wreck lies on). Another crew's Raids are not counted.</summary>
    public int Active => _raids.Count(r => !r.Over && r.Gang.Crew == GangCrew.Raid);
    /// <summary>Where vans stopped this event: no other van stops near (<see cref="VanConfig.UsedApartM"/>).</summary>
    public IReadOnlyList<Vector2> UsedStops => _used;

    /// <summary>
    /// Puts a Raid in the world: the van at <paramref name="start"/> (the first point of the plan's path, or the stop for a plan with none) and its crew, queued to get out when the door opens. Null
    /// when the world has no room (the entity cap, no free slot).
    /// </summary>
    public Raid? Launch(RaidPlan plan, RoadPath exit, Vector3 start, GangRaidCounts crew, Func<Vector3, Vector3>? snap, Func<Vector3, Vector3> edgeFor, Func<float, float, float>? ground) =>
        Launch(plan, exit, start, Raid.Order(crew), null, _cfg.Van, snap, edgeFor, ground);

    /// <summary>
    /// Puts a Raid in the world as <see cref="Launch(RaidPlan, RoadPath, Vector3, GangRaidCounts, Func{Vector3, Vector3}?, Func{Vector3, Vector3}, Func{float, float, float}?)"/>, with the crew in
    /// <paramref name="order"/>, in <paramref name="gang"/> (null: a new Thieves gang), on the van numbers <paramref name="van"/>.
    /// A Raid of another crew steals nothing and leaves no used stop for the Thieves. Null when the world has no room.
    /// </summary>
    public Raid? Launch(RaidPlan plan, RoadPath exit, Vector3 start, IEnumerable<GangMemberKind> order, ThiefGang? gang, VanConfig van, Func<Vector3, Vector3>? snap, Func<Vector3, Vector3> edgeFor,
        Func<float, float, float>? ground)
    {
        var slot = FreeSlot();
        if (slot < 0) { Log?.Invoke("raid: every van slot is in use, no Raid launched"); return null; }
        var rv = new RaidVan(van, _world, plan.In, start, plan.In.IsEmpty ? plan.Yaw : plan.In.YawAt(0), slot, plan.DoorSide, _rng.Next())
        {
            Ground = ground,
            Exit = exit,
            Song = PickSong(),
        };
        if (_entities.Spawn(rv) is not { } entity) { Log?.Invoke("raid: the van could not be spawned (entity cap)"); return null; }
        gang ??= _gangs.NewGang(start);
        var thieves = gang.Crew == GangCrew.Raid;
        var raid = new Raid(rv, gang, van, plan, order, snap, edgeFor, thieves ? Loot : null) { VanEntity = entity };
        raid.Ended += r => RaidOver?.Invoke(r);
        raid.LootReturned += (r, n) => LootReturned?.Invoke(r, n);
        rv.Honked += _ => Honked?.Invoke(raid);
        _raids.Add(raid);
        if (thieves) _used.Add(plan.StopAt);
        Launched?.Invoke(raid);
        return raid;
    }

    /// <summary>A song number no other van in the village plays, when there is one.</summary>
    int PickSong()
    {
        var taken = _raids.Where(r => !r.Over).Select(r => r.Van.Song).ToHashSet();
        for (var i = 0; i < 8; i++)
        {
            var n = _rng.Next(RaidVanRules.SongCount);
            if (!taken.Contains(n)) return n;
        }
        return _rng.Next(RaidVanRules.SongCount);
    }

    int FreeSlot()
    {
        for (var s = 0; s < RaidVanRules.MaxVans; s++)
            if (_raids.All(r => r.Van.Slot != s)) return s;
        return -1;
    }

    /// <summary>One tick, after the entities tick: every Raid; Raids that are over, whose van is gone and whose crew has gone too are forgotten.</summary>
    public void Tick(float dt)
    {
        foreach (var r in _raids.ToList())
        {
            r.Tick(dt);
            if (r.Over && r.Van.Ended != null && r.Gang.Count == 0) { _gangs.Remove(r.Gang); _raids.Remove(r); }
        }
    }

    // ---- hits ----

    /// <summary>The Raid whose van has the hit id <paramref name="hitId"/> (<see cref="RaidVanRules.IsHitId"/>), or null.</summary>
    public Raid? RaidOf(ushort hitId) => RaidVanRules.IsHitId(hitId) ? _raids.FirstOrDefault(r => r.Van.Slot == RaidVanRules.SlotOf(hitId)) : null;

    /// <summary>
    /// A weapon hit a van's capsule (<see cref="MatchSim.ApplyDamage"/> routes the ids of the range here): a bullet counts 1 (a headshot is a body hit), an explosive or a vehicle weapon its
    /// weight; ramming counts nothing. The Raid fights the attacker from now on. Ignored for a destroyed van.
    /// </summary>
    public DamageResult Hit(ushort hitId, ushort attacker, byte weapon, float amount, KillCause cause)
    {
        if (!RaidVanRules.IsHitId(hitId) || !(amount > 0) || cause == KillCause.Roadkill) return DamageResult.Ignored;
        var raid = RaidOf(hitId);
        if (raid == null || !raid.Van.Alive || raid.Van.Ended != null) return DamageResult.Ignored;
        var source = SourceOf(weapon);
        var weight = GangRules.Weight(_cfg.Van.Hit, source, false);
        if (!(weight > 0)) return DamageResult.Ignored;
        var killed = raid.Van.Hit(attacker, weight, source == HitSource.Explosive);
        raid.Gang.NoteHit(attacker); // shooting the van is interfering with the Raid
        return killed ? DamageResult.Killed : DamageResult.Damaged;
    }

    /// <summary>The living vans as hit targets (two capsules each). With a <paramref name="view"/> tick, where the shooter's screen showed them (ADR 0013: a van drives, so it is rewound like the Bigfoot, from its own pose ring).</summary>
    public void AddTargets(List<HitTarget> into, uint? view = null)
    {
        foreach (var r in _raids)
        {
            var van = r.Van;
            if (!van.Alive || van.Ended != null) continue;
            var centre = view is { } v && LagComp.TryTargetAt(van.Poses, v, van.BornTick, out var past) ? past : van.HitCentre;
            var (front, rear) = RaidVanRules.Capsules(centre, _cfg.Van.HitOffsetM);
            into.Add(front); into.Add(rear);
        }
    }

    /// <summary>End of tick: the pose every van ends it with, for the rewind of a shot.</summary>
    public void RecordPoses(uint tick)
    {
        foreach (var r in _raids)
        {
            var van = r.Van;
            if (van.BornTick == 0) van.BornTick = tick;
            van.Poses.Record(tick, van.Alive && van.Ended == null, van.HitCentre);
        }
    }

    // ---- ending ----

    /// <summary>Removes every Raid of the Thieves at once: vans, crews, the dead too (the event was ended or cancelled, or a round began). The stops used are forgotten. A Heist's Assaults stay: their Job removes them.</summary>
    public void Clear()
    {
        foreach (var r in _raids.Where(r => r.Gang.Crew == GangCrew.Raid).ToList()) Remove(r);
        _used.Clear();
    }

    /// <summary>Removes one Raid now: its van, its crew, the dead too. Removing it twice does nothing.</summary>
    public void Remove(Raid r)
    {
        if (!_raids.Remove(r)) return;
        r.Van.Remove();
        _entities.Despawn(r.VanEntity, WorldDespawnReason.Removed);
        _gangs.Remove(r.Gang);
    }

    void OnDespawned(WorldDespawned d)
    {
        foreach (var r in _raids)
            if (r.VanEntity == d.Id) { r.Van.Remove(d.Reason); break; }
    }
}
