using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Hands out the hit ids of the Gang members (<see cref="GangRules.FirstHitId"/> up) so no two living members share one, whichever Raid they belong to.</summary>
public sealed class GangHitIds
{
    readonly HashSet<ushort> _used = new();
    int _next;

    /// <summary>The next free id, or null when every one is in use.</summary>
    public ushort? Allocate()
    {
        for (var i = 0; i < GangRules.HitIdCount; i++)
        {
            _next = (_next + 1) % GangRules.HitIdCount;
            var id = (ushort)(GangRules.FirstHitId + _next);
            if (_used.Add(id)) return id;
        }
        return null;
    }

    public void Release(ushort id) => _used.Remove(id);

    public int InUse => _used.Count;
}

/// <summary>
/// The Gang members of one Raid (GLOSSARY "Raid", "Gang member"), pure. It spawns them as world entities, keeps the hit ids, the aggro list and the queue of routes
/// they wait for, and answers the hits on them. The Raid's van (ticket 03) sets <see cref="Post"/> and calls <see cref="Add"/>; the stealing loop (ticket 04) reads
/// <see cref="Members"/> and <see cref="GangMember.Working"/>. The gang owns no clock: the server calls <see cref="Update"/> before the entities tick and
/// <see cref="ServeRoute"/> after.
/// <para>
/// Aggro is kept per Raid: a player who hit any member, or who stands within <see cref="GangAggro.CrowdM"/> of a working Thief, is fought by every member that
/// can fight (Enforcers, the Gang boss, a Thief with free hands) until they die or are further than <see cref="GangAggro.GiveUpM"/> from every living member.
/// </para>
/// </summary>
public sealed class ThiefGang : IGangHits, IDisposable
{
    readonly GangConfig _cfg;
    readonly IGangWorld _world;
    readonly WorldEntityManager _entities;
    readonly GangHitIds _ids;
    readonly Random _rng;
    readonly List<GangMember> _members = new();
    readonly Dictionary<ushort, GangMember> _byHit = new();
    readonly Dictionary<ushort, ushort> _entityOfHit = new();
    readonly Dictionary<ushort, ushort> _hitOfEntity = new();
    readonly HashSet<ushort> _aggro = new();
    readonly List<ushort> _scratch = new();
    long _seq;
    Vector3 _post;

    public ThiefGang(GangConfig cfg, IGangWorld world, WorldEntityManager entities, GangHitIds ids, Vector3 post, int seed = 0)
    {
        _cfg = cfg; _world = world; _entities = entities; _ids = ids; _post = post;
        _rng = new Random(seed);
        entities.Despawned += OnDespawned;
    }

    /// <summary>Raised once per member killed by a hit (not by the end of the event).</summary>
    public event Action<GangKilled>? Killed;

    /// <summary>Raised once per member that reached the van's door (it ended there: ticket 03).</summary>
    public event Action<GangMember>? Boarded;

    /// <summary>Error log for members that could not spawn (the entity cap, no free hit id). Null means silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>What kind of hit a weapon byte is (<see cref="BigfootRules.SourceOf"/> needs the weapon list, which the server has): the default counts everything as a bullet.</summary>
    public Func<byte, HitSource> SourceOf { get; set; } = _ => HitSource.Bullet;

    /// <summary>The members in the world, dead ones lying included.</summary>
    public IReadOnlyList<GangMember> Members => _members;
    public int Count => _members.Count;
    public int AliveCount => _members.Count(m => m.Alive);
    /// <summary>Members alive and not fleeing: the ones the van waits for (a member that boarded is gone from the list).</summary>
    public int Afoot => _members.Count(m => m.Alive && m.State != GangMemberState.Flee);

    /// <summary>The van's place (ticket 03: its side door); every member's <see cref="GangMember.Home"/>.</summary>
    public Vector3 Post
    {
        get => _post;
        set { _post = value; foreach (var m in _members) m.Home = value; }
    }

    /// <summary>The players this Raid fights now.</summary>
    public IReadOnlyCollection<ushort> Aggro => _aggro;

    public bool Owns(ushort hitId) => _byHit.ContainsKey(hitId);

    // ---- spawning ----

    /// <summary>
    /// Puts one member in the world at <paramref name="at"/> (a point on the navigation mesh). Null when the world has no room (the entity cap, no free hit id).
    /// Ticket 03 calls it once per member at the van's side door.
    /// </summary>
    public GangMember? Add(GangMemberKind kind, Vector3 at, float yaw)
    {
        if (_ids.Allocate() is not { } hit) { Log?.Invoke("gang: every hit id is in use, no member spawned"); return null; }
        var member = new GangMember(_cfg, kind, _world, at, yaw, (ushort)_rng.Next(ushort.MaxValue + 1), hit, _rng.Next()) { Home = _post };
        member.Died += OnDied;
        member.Boarded += OnBoarded;
        if (_entities.Spawn(member) is not { } entity) { member.Died -= OnDied; member.Boarded -= OnBoarded; _ids.Release(hit); return null; }
        _members.Add(member);
        _byHit[hit] = member;
        _entityOfHit[hit] = entity;
        _hitOfEntity[entity] = hit;
        return member;
    }

    /// <summary>
    /// The whole crew of <see cref="GangConfig.Raid"/> at once, on a ring of <see cref="GangSpawn.SpreadM"/> round <paramref name="centre"/> (the Gang boss in the middle). Each
    /// point goes through <paramref name="snap"/> (onto the navigation mesh) and falls back to the centre when the snapped one is further than the spread from it.
    /// Returns how many were spawned.
    /// </summary>
    public int SpawnCrew(Vector3 centre, Func<Vector3, Vector3>? snap = null)
    {
        _post = centre;
        var kinds = new List<GangMemberKind>();
        kinds.AddRange(Enumerable.Repeat(GangMemberKind.GangBoss, _cfg.Raid.GangBosses));
        kinds.AddRange(Enumerable.Repeat(GangMemberKind.Enforcer, _cfg.Raid.Enforcers));
        kinds.AddRange(Enumerable.Repeat(GangMemberKind.Thief, _cfg.Raid.Thieves));
        var spread = _cfg.Spawn.SpreadM;
        var ring = Math.Max(1, kinds.Count - 1);
        var turn = (float)_rng.NextDouble() * MathF.Tau;
        var n = 0;
        for (var i = 0; i < kinds.Count; i++)
        {
            var at = centre;
            if (i > 0)
            {
                var a = turn + MathF.Tau * (i - 1) / ring;
                at = new Vector3(centre.X + MathF.Cos(a) * spread, centre.Y, centre.Z + MathF.Sin(a) * spread);
                if (snap != null)
                {
                    var snapped = snap(at);
                    at = Flat(snapped, centre) <= spread + 1f && MathF.Abs(snapped.Y - centre.Y) <= 4f ? snapped : centre;
                }
            }
            var yaw = (float)_rng.NextDouble() * MathF.Tau - MathF.PI;
            if (Add(kinds[i], at, yaw) != null) n++;
        }
        return n;
    }

    // ---- the tick ----

    /// <summary>A player hit a member (or ran one over): the Raid fights them from now on. Only a living player counts.</summary>
    public void NoteHit(ushort player)
    {
        foreach (var p in _world.Players)
            if (p.Id == player) { _aggro.Add(player); return; }
    }

    /// <summary>
    /// Once a tick, before the members tick: the crowding rule, the give-up rule, who each member fights, and whom each Enforcer escorts. Everything about
    /// who is fought is decided here, from the players of this tick.
    /// </summary>
    public void Update()
    {
        var players = _world.Players;
        foreach (var m in _members)
            if (m.Alive && m.Kind == GangMemberKind.Thief && m.Working)
                foreach (var p in players)
                    if (Flat(p.Feet, m.Position) <= _cfg.Aggro.CrowdM) _aggro.Add(p.Id); // in the way of a working Thief

        if (_aggro.Count > 0)
        {
            _scratch.Clear();
            _scratch.AddRange(_aggro);
            foreach (var id in _scratch)
            {
                GangPlayer? p = null;
                foreach (var q in players) if (q.Id == id) { p = q; break; }
                if (p is not { } player || NearestLiving(player.Feet) > _cfg.Aggro.GiveUpM) _aggro.Remove(id); // died, left, or got away
            }
        }

        foreach (var m in _members)
        {
            if (!m.Alive) continue;
            m.SetTarget(_aggro.Count > 0 && m.CanFight ? NearestAggro(m.Position, players) : null);
        }
        AssignEscorts();
    }

    GangPlayer? NearestAggro(Vector3 from, IReadOnlyList<GangPlayer> players)
    {
        GangPlayer? best = null;
        var bestD = float.PositiveInfinity;
        foreach (var p in players)
        {
            if (!_aggro.Contains(p.Id)) continue;
            var d = Flat(p.Feet, from);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    float NearestLiving(Vector3 from)
    {
        var best = float.PositiveInfinity;
        foreach (var m in _members)
            if (m.Alive && m.State != GangMemberState.Flee) best = MathF.Min(best, Flat(m.Position, from));
        return best;
    }

    /// <summary>Each Enforcer without a living Thief to escort gets the Thief with the fewest escorts (none when no Thief lives: it stands at the post).</summary>
    void AssignEscorts()
    {
        foreach (var e in _members)
        {
            if (e.Kind != GangMemberKind.Enforcer || !e.Alive || e.Escort is { Alive: true }) continue;
            GangMember? pick = null;
            var least = int.MaxValue;
            foreach (var t in _members)
            {
                if (t.Kind != GangMemberKind.Thief || !t.Alive) continue;
                var n = 0;
                foreach (var o in _members) if (o.Escort == t) n++;
                if (n < least) { least = n; pick = t; }
            }
            e.Escort = pick;
        }
    }

    /// <summary>
    /// Answers one waiting route request, the one that has waited longest, with <paramref name="route"/> (from, to: the points to walk, empty for none). True when one was
    /// served: the server counts it against the tick's ration (<see cref="GangRoster.ServeRoute"/>; the bots' routes cost the same milliseconds).
    /// </summary>
    public bool ServeRoute(Func<Vector3, Vector3, IReadOnlyList<Vector3>> route)
    {
        GangMember? best = null;
        foreach (var m in _members)
        {
            if (m.RouteRequest == null) continue;
            if (m.RequestSeq == 0) m.RequestSeq = ++_seq; // queued now: the order of the first sight is the order of the queue
            if (best == null || m.RequestSeq < best.RequestSeq) best = m;
        }
        if (best?.RouteRequest is not { } want) return false;
        best.SetRoute(route(want.From, want.To));
        return true;
    }

    /// <summary>Members waiting for a route.</summary>
    public int RoutesWaiting => _members.Count(m => m.RouteRequest != null);

    // ---- hits ----

    /// <summary>
    /// A weapon hit a member (<see cref="MatchSim.ApplyDamage"/> routes the ids of the range here). It counts the weight of its source (a bullet 1, a headshot 2, an
    /// explosive or a vehicle weapon a data weight); ramming (cause Roadkill) kills outright. The attacker is fought from now on. Ignored when the hit has no weight or the member is dead.
    /// </summary>
    public DamageResult Hit(ushort hitId, ushort attacker, byte weapon, float amount, KillCause cause)
    {
        if (!_byHit.TryGetValue(hitId, out var m) || !m.Alive || !(amount > 0)) return DamageResult.Ignored;
        bool killed;
        if (cause == KillCause.Roadkill) killed = m.Kill(attacker);
        else
        {
            var weight = GangRules.Weight(_cfg.Hits, SourceOf(weapon), cause == KillCause.Headshot);
            if (!(weight > 0)) return DamageResult.Ignored;
            killed = m.Hit(attacker, weight);
        }
        NoteHit(attacker);
        return killed ? DamageResult.Killed : DamageResult.Damaged;
    }

    /// <summary>The living members as hit targets (each at its scale). With a <paramref name="view"/> tick, where the shooter's screen showed them (they run, so they are rewound like the Bigfoot: ADR 0013).</summary>
    public void AddTargets(List<HitTarget> into, uint? view = null)
    {
        foreach (var m in _members)
        {
            if (!m.Alive) continue;
            var now = m.HitVolume;
            into.Add(view is { } v && LagComp.TryTargetAt(m.Poses, v, m.BornTick, out var past) ? past : now);
        }
    }

    /// <summary>End of tick: the pose every member ends it with, for the rewind of a shot.</summary>
    public void RecordPoses(uint tick)
    {
        foreach (var m in _members)
        {
            if (m.BornTick == 0) m.BornTick = tick;
            m.Poses.Record(tick, m.Alive, m.HitVolume);
        }
    }

    // ---- ending ----

    /// <summary>The van calls the crew back: every living member that is not fleeing runs to <paramref name="door"/> and boards (ticket 03). The aggro list is dropped: nobody fights while boarding.</summary>
    public void BoardAll(Vector3 door)
    {
        Post = door;
        foreach (var m in _members) m.Board(door);
        _aggro.Clear();
    }

    /// <summary>Every living member runs for <paramref name="edge"/> and ends there (the van was destroyed, ticket 03).</summary>
    public void FleeAll(Vector3 edge)
    {
        foreach (var m in _members) m.Flee(edge);
        _aggro.Clear();
    }

    /// <summary>Removes every member at once, the dead too (the event ended or was cancelled).</summary>
    public void Clear()
    {
        foreach (var entity in _hitOfEntity.Keys.ToList()) _entities.Despawn(entity, WorldDespawnReason.Removed);
        _aggro.Clear();
    }

    public void Dispose() => _entities.Despawned -= OnDespawned;

    void OnDied(GangMember m) => Killed?.Invoke(new GangKilled(m.HitId, m.Killer, m.Kind, m.Position, m.Carried));

    void OnBoarded(GangMember m) => Boarded?.Invoke(m);

    void OnDespawned(WorldDespawned d)
    {
        if (!_hitOfEntity.Remove(d.Id, out var hit)) return;
        _entityOfHit.Remove(hit);
        if (_byHit.Remove(hit, out var m)) { _members.Remove(m); m.Died -= OnDied; m.Boarded -= OnBoarded; }
        _ids.Release(hit);
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>
/// The Raids of the Thieves event: every <see cref="ThiefGang"/> in the world, behind one hit router (the match hands the hit ids of the Gang members to it), one set
/// of hit ids and one route ration. Ticket 03 makes a gang per van; the dev fallback of ticket 02 makes one.
/// </summary>
public sealed class GangRoster : IGangHits
{
    readonly GangConfig _cfg;
    readonly IGangWorld _world;
    readonly WorldEntityManager _entities;
    readonly GangHitIds _ids = new();
    readonly List<ThiefGang> _gangs = new();
    readonly Random _rng;
    int _turn;

    public GangRoster(GangConfig cfg, IGangWorld world, WorldEntityManager entities, int seed = 0)
    {
        _cfg = cfg; _world = world; _entities = entities;
        _rng = new Random(seed);
    }

    /// <summary>A member was killed by a hit, in any Raid.</summary>
    public event Action<GangKilled>? Killed;

    public Action<string>? Log { get; set; }

    /// <summary>What kind of hit a weapon byte is; given to every gang (existing and new).</summary>
    public Func<byte, HitSource> SourceOf
    {
        get => _sourceOf;
        set { _sourceOf = value; foreach (var g in _gangs) g.SourceOf = value; }
    }
    Func<byte, HitSource> _sourceOf = _ => HitSource.Bullet;

    public IReadOnlyList<ThiefGang> Gangs => _gangs;
    public int MemberCount => _gangs.Sum(g => g.Count);

    /// <summary>A new, empty Raid whose post is <paramref name="post"/>.</summary>
    public ThiefGang NewGang(Vector3 post)
    {
        var gang = new ThiefGang(_cfg, _world, _entities, _ids, post, _rng.Next()) { Log = Log, SourceOf = _sourceOf };
        gang.Killed += k => Killed?.Invoke(k);
        _gangs.Add(gang);
        return gang;
    }

    /// <summary>Removes a Raid and every member it still has.</summary>
    public void Remove(ThiefGang gang)
    {
        gang.Clear();
        gang.Dispose();
        _gangs.Remove(gang);
    }

    /// <summary>Removes every Raid and member.</summary>
    public void Clear()
    {
        foreach (var g in _gangs.ToList()) Remove(g);
    }

    public void Update() { foreach (var g in _gangs) g.Update(); }

    /// <summary>Serves one route request in all the Raids, taking turns between them. True when one was served.</summary>
    public bool ServeRoute(Func<Vector3, Vector3, IReadOnlyList<Vector3>> route)
    {
        for (var k = 0; k < _gangs.Count; k++)
        {
            var g = _gangs[(_turn + k) % _gangs.Count];
            if (!g.ServeRoute(route)) continue;
            _turn = (_turn + k + 1) % _gangs.Count;
            return true;
        }
        return false;
    }

    public bool AnyRouteWaiting => _gangs.Any(g => g.RoutesWaiting > 0);

    public void RecordPoses(uint tick) { foreach (var g in _gangs) g.RecordPoses(tick); }

    public void AddTargets(List<HitTarget> into, uint? view = null) { foreach (var g in _gangs) g.AddTargets(into, view); }

    public DamageResult Hit(ushort hitId, ushort attacker, byte weapon, float amount, KillCause cause)
    {
        foreach (var g in _gangs) if (g.Owns(hitId)) return g.Hit(hitId, attacker, weapon, amount, cause);
        return DamageResult.Ignored;
    }
}
