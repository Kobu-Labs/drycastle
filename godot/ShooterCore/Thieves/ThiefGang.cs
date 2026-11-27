using System.Numerics;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Thieves;

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

/// <summary>Whose a gang is: a Raid of the Thieves event, or a crew of the cartel's Rival crew (GLOSSARY "Rival crew"). The Thieves event clears, credits and voices its Raids only.</summary>
public enum GangCrew : byte { Raid = 0, RivalCrew = 1 }

/// <summary>
/// The Gang members of one Raid (GLOSSARY "Raid", "Gang member"), pure. It spawns them as world entities, keeps the hit ids, the aggro list and the queue of routes
/// they wait for, and answers the hits on them. The Raid's van (ticket 03) sets <see cref="Post"/> and calls <see cref="Add"/>; the stealing loop (ticket 04) runs in the members through <see cref="Work"/>. The gang owns no clock: the server calls <see cref="Update"/> before the entities tick and
/// <see cref="ServeRoute"/> after.
/// <para>
/// Aggro is kept per Raid: a player who hit any member is fought by every member that can fight (Enforcers, the Gang boss, a Thief with free hands) for
/// <see cref="GangAggro.ForgetSeconds"/> after their last hit (each hit starts it again), or until they die or are further than <see cref="GangAggro.GiveUpM"/> from every living member.
/// When it lapses the members go back to what they did (Thieves steal, Enforcers and the boss guard).
/// </para>
/// <para>
/// A Rival crew (<see cref="GangCrew.RivalCrew"/>, #495) is one of these too, with its own config (rival_crew.json, <see cref="Cartel.RivalCrewFile.GangOf"/>) and a set of players it attacks on sight
/// (<see cref="HostileTo"/>): one of them within <see cref="GangSight.RadiusM"/> of a member that can see them joins the aggro list as if they had hit it. Everyone else is left alone until they hit a member.
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
    readonly Dictionary<ushort, float> _aggro = new(); // player -> seconds of aggro left
    readonly List<ushort> _scratch = new();
    long _seq;
    Vector3 _post;
    Countdown _spot; // the next look for the players attacked on sight

    public ThiefGang(GangConfig cfg, IGangWorld world, WorldEntityManager entities, GangHitIds ids, Vector3 post, int seed = 0, GangCrew crew = GangCrew.Raid)
    {
        _cfg = cfg; _world = world; _entities = entities; _ids = ids; _post = post; Crew = crew;
        _rng = new Random(seed);
        entities.Despawned += OnDespawned;
    }

    /// <summary>A Raid of the Thieves or a Rival crew.</summary>
    public GangCrew Crew { get; }

    /// <summary>The numbers its members run on.</summary>
    public GangConfig Config => _cfg;

    /// <summary>
    /// The players this gang attacks on sight (the Rival crew: a Job's players, given by its owner and asked every look, so it may change); null: nobody (a Raid fights only whoever hits it).
    /// </summary>
    public Func<ushort, bool>? HostileTo { get; set; }

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
    public IReadOnlyCollection<ushort> Aggro => _aggro.Keys;

    /// <summary>What this Raid's Thieves steal through (<see cref="RaidWork"/>); given to every member, those that join later too. Null: Thieves stand about.</summary>
    public IThiefWork? Work
    {
        get => _work;
        set { _work = value; foreach (var m in _members) m.Work = value; }
    }
    IThiefWork? _work;

    public bool Owns(ushort hitId) => _byHit.ContainsKey(hitId);

    // ---- spawning ----

    /// <summary>
    /// Puts one member in the world at <paramref name="at"/> (a point on the navigation mesh). Null when the world has no room (the entity cap, no free hit id).
    /// Ticket 03 calls it once per member at the van's side door.
    /// </summary>
    public GangMember? Add(GangMemberKind kind, Vector3 at, float yaw)
    {
        if (_ids.Allocate() is not { } hit) { Log?.Invoke("gang: every hit id is in use, no member spawned"); return null; }
        var member = new GangMember(_cfg, kind, _world, at, yaw, (ushort)_rng.Next(ushort.MaxValue + 1), hit, _rng.Next()) { Home = _post, Work = _work, Rival = Crew == GangCrew.RivalCrew };
        member.Died += OnDied;
        member.Boarded += OnBoarded;
        if (_entities.Spawn(member) is not { } entity) { member.Died -= OnDied; member.Boarded -= OnBoarded; _ids.Release(hit); return null; }
        _members.Add(member);
        _byHit[hit] = member;
        _entityOfHit[hit] = entity;
        _hitOfEntity[entity] = hit;
        return member;
    }

    /// <summary>Puts one member in the world posted at <paramref name="at"/>, facing <paramref name="facing"/> (<see cref="GangMember.Post"/>: a trap house defender, no navigation mesh needed). Null when the world has no room.</summary>
    public GangMember? AddPosted(GangMemberKind kind, Vector3 at, float facing)
    {
        var m = Add(kind, at, facing);
        m?.Post(at, facing);
        return m;
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

    /// <summary>A player hit a member (or ran one over): the Raid fights them for <see cref="GangAggro.ForgetSeconds"/> from now. Only a living player counts.</summary>
    public void NoteHit(ushort player)
    {
        foreach (var p in _world.Players)
            if (p.Id == player) { _aggro[player] = _cfg.Aggro.ForgetSeconds; return; }
    }

    /// <summary>
    /// Once a tick, before the members tick: the forget clock and the give-up rule, then who each member fights, and whom each Enforcer escorts. Everything about
    /// who is fought is decided here, from the players of this tick.
    /// </summary>
    public void Update(float dt = 1f / 60)
    {
        var players = _world.Players;
        if (_aggro.Count > 0)
        {
            _scratch.Clear();
            _scratch.AddRange(_aggro.Keys);
            foreach (var id in _scratch)
            {
                GangPlayer? p = null;
                foreach (var q in players) if (q.Id == id) { p = q; break; }
                var left = _aggro[id] - dt;
                if (left <= 0 || p is not { } player || NearestLiving(player.Feet) > _cfg.Aggro.GiveUpM) _aggro.Remove(id); // forgotten, died, left, or got away
                else _aggro[id] = left;
            }
        }
        if (HostileTo != null) Spot(dt, players);

        foreach (var m in _members)
        {
            if (!m.Alive) continue;
            m.SetTarget(_aggro.Count > 0 && m.CanFight ? NearestAggro(m.Position, players) : null);
        }
        AssignEscorts();
    }

    /// <summary>Every sight.check_seconds: each player attacked on sight whom a member that can fight sees (within the radius, a clear line from its eyes to their chest) is fought from now, as after a hit.</summary>
    void Spot(float dt, IReadOnlyList<GangPlayer> players)
    {
        _spot.Tick(dt);
        if (_spot.Running) return;
        _spot.Start(_cfg.Sight.CheckSeconds);
        foreach (var p in players)
            if (HostileTo!(p.Id) && SeenByAny(p)) _aggro[p.Id] = _cfg.Aggro.ForgetSeconds;
    }

    bool SeenByAny(GangPlayer p)
    {
        var s = _cfg.Sight;
        var chest = p.Feet + Vector3.UnitY * s.ChestM;
        foreach (var m in _members)
            if (m.CanFight && Flat(m.Position, p.Feet) <= s.RadiusM && _world.Clear(m.Position + Vector3.UnitY * s.EyeM, chest)) return true;
        return false;
    }

    GangPlayer? NearestAggro(Vector3 from, IReadOnlyList<GangPlayer> players)
    {
        GangPlayer? best = null;
        var bestD = float.PositiveInfinity;
        foreach (var p in players)
        {
            if (!_aggro.ContainsKey(p.Id)) continue;
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
    /// Takes the waiting route request that has waited longest and not gone out yet: it is marked as sent under <paramref name="ticket"/> (not offered again until it is answered or
    /// the worker is given up on) and its endpoints are returned. False when none waits. The living only: a dead member's request is not served.
    /// </summary>
    public bool BeginRoute(long ticket, out Vector3 from, out Vector3 to)
    {
        GangMember? best = null;
        foreach (var m in _members)
        {
            if (m.RouteRequest == null || m.RouteTicket != 0 || !m.Alive || m.Ended != null) continue;
            if (m.RequestSeq == 0) m.RequestSeq = ++_seq; // queued now: the order of the first sight is the order of the queue
            if (best == null || m.RequestSeq < best.RequestSeq) best = m;
        }
        if (best?.RouteRequest is not { } want) { from = to = default; return false; }
        best.RouteSent(ticket);
        from = want.From; to = want.To;
        return true;
    }

    /// <summary>
    /// Gives the worker's answer for <paramref name="ticket"/> (the points to walk, empty for none) to the member that asked. False, and nothing given, when no member holds the ticket
    /// any more (it was cleared or replaced, or the member is gone) or it died meanwhile.
    /// </summary>
    public bool DeliverRoute(long ticket, IReadOnlyList<Vector3> route)
    {
        foreach (var m in _members)
        {
            if (m.RouteTicket != ticket) continue;
            if (!m.Alive || m.Ended != null || m.RouteRequest == null) { m.RouteDropped(); return false; }
            m.SetRoute(route);
            return true;
        }
        return false;
    }

    /// <summary>Answers one waiting route request synchronously with <paramref name="route"/> (from, to): <see cref="BeginRoute"/> and <see cref="DeliverRoute"/> in one. True when one was served (tests; the server uses the two halves with the worker between).</summary>
    public bool ServeRoute(Func<Vector3, Vector3, IReadOnlyList<Vector3>> route)
    {
        var ticket = ++_syncTicket;
        if (!BeginRoute(ticket, out var from, out var to)) return false;
        return DeliverRoute(ticket, route(from, to));
    }
    long _syncTicket = long.MinValue / 2; // apart from the server's tickets

    /// <summary>Members waiting for a route that has not been sent yet.</summary>
    public int RoutesWaiting { get { var n = 0; foreach (var m in _members) if (m.RouteRequest != null && m.RouteTicket == 0) n++; return n; } }

    /// <summary>Members whose request is out with the worker.</summary>
    public int RoutesInFlight { get { var n = 0; foreach (var m in _members) if (m.RouteTicket != 0) n++; return n; } }

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

    void OnDied(GangMember m) => Killed?.Invoke(new GangKilled(m.HitId, m.Killer, m.Kind, m.RestPoint, m.Carried, m.CarriedId));

    void OnBoarded(GangMember m) => Boarded?.Invoke(m);

    void OnDespawned(WorldDespawned d)
    {
        if (!_hitOfEntity.Remove(d.Id, out var hit)) return;
        _entityOfHit.Remove(hit);
        if (_byHit.Remove(hit, out var m)) { _members.Remove(m); m.Died -= OnDied; m.Boarded -= OnBoarded; m.Remove(d.Reason); } // Remove lets go of the target it had claimed
        _ids.Release(hit);
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
    readonly List<GangMember> _all = new();
    float[] _pushX = new float[32], _pushZ = new float[32];

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

    /// <summary>A new, empty gang whose post is <paramref name="post"/>: a Raid on the roster's config, or (<paramref name="crew"/>) a Rival crew on its own (<paramref name="cfg"/>, rival_crew.json's).</summary>
    public ThiefGang NewGang(Vector3 post, GangConfig? cfg = null, GangCrew crew = GangCrew.Raid)
    {
        var gang = new ThiefGang(cfg ?? _cfg, _world, _entities, _ids, post, _rng.Next(), crew) { Log = Log, SourceOf = _sourceOf };
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

    /// <summary>Removes every gang and member.</summary>
    public void Clear()
    {
        foreach (var g in _gangs.ToList()) Remove(g);
    }

    /// <summary>Removes every gang of one crew and its members (the Thieves event clears its Raids, not a Job's Rival crew).</summary>
    public void Clear(GangCrew crew)
    {
        foreach (var g in _gangs.Where(g => g.Crew == crew).ToList()) Remove(g);
    }

    /// <summary>The gang a member's hit id belongs to (the dead lying included), or null.</summary>
    public ThiefGang? GangOf(ushort hitId)
    {
        foreach (var g in _gangs) if (g.Owns(hitId)) return g;
        return null;
    }

    /// <summary>Once a tick (<paramref name="dt"/> seconds long, 60 Hz by default) before the entities tick: every Raid decides who it fights, then the chasers of one player get their own spots and walking members are nudged apart.</summary>
    public void Update(float dt = 1f / 60)
    {
        foreach (var g in _gangs) g.Update(dt);
        AssignSlots();
        Separate(dt);
    }

    /// <summary>Each member with a target keeps its slot while the fight lasts; a new one takes the lowest slot of that player no other member (of any Raid) has.</summary>
    void AssignSlots()
    {
        _all.Clear();
        foreach (var g in _gangs) foreach (var m in g.Members) if (m.Alive) _all.Add(m);
        foreach (var m in _all)
        {
            if (m.Target is not { } t) continue;
            if (m.Slot >= 0 && m.SlotOf == t.Id) continue;
            var slot = 0;
            while (_all.Any(o => o != m && o.Target is { } ot && ot.Id == t.Id && o.Slot == slot && o.SlotOf == t.Id)) slot++;
            m.Slot = slot; m.SlotOf = t.Id;
        }
    }

    /// <summary>A pure push between members of any Raid (alive, walking about) closer than <see cref="GangConfig.SeparationM"/>: small, at most <see cref="GangConfig.SeparationMps"/> a second, along the line between them.</summary>
    void Separate(float dt)
    {
        var sep = _cfg.SeparationM; var cap = _cfg.SeparationMps * dt;
        if (!(cap > 0) || _all.Count < 2) return;
        var n = _all.Count;
        if (_pushX.Length < n) { _pushX = new float[n]; _pushZ = new float[n]; }
        Array.Clear(_pushX, 0, n); Array.Clear(_pushZ, 0, n);
        for (var i = 0; i < n; i++)
        {
            var a = _all[i];
            if (!a.Pushable) continue;
            for (var j = i + 1; j < n; j++)
            {
                var b = _all[j];
                if (!b.Pushable || MathF.Abs(a.Position.Y - b.Position.Y) > 2f) continue;
                var dx = b.Position.X - a.Position.X; var dz = b.Position.Z - a.Position.Z;
                var d = MathF.Sqrt(dx * dx + dz * dz);
                if (d >= sep) continue;
                if (d < 0.001f) { var ang = (i * 7 + j) * 2.3999632f; dx = MathF.Cos(ang); dz = MathF.Sin(ang); d = 1f; } // on the same spot: any way apart, the same one for the same pair
                var push = MathF.Min(sep - d, cap) * 0.5f;
                dx = dx / d * push; dz = dz / d * push;
                _pushX[i] -= dx; _pushZ[i] -= dz; _pushX[j] += dx; _pushZ[j] += dz;
            }
        }
        for (var i = 0; i < n; i++)
        {
            var len = MathF.Sqrt(_pushX[i] * _pushX[i] + _pushZ[i] * _pushZ[i]);
            if (len < 1e-5f) continue;
            var k = len > cap ? cap / len : 1f;
            _all[i].Nudge(_pushX[i] * k, _pushZ[i] * k);
        }
    }

    /// <summary>Takes the oldest waiting request of the next Raid that has one, the Raids in turn (see <see cref="ThiefGang.BeginRoute"/>).</summary>
    public bool BeginRoute(long ticket, out Vector3 from, out Vector3 to)
    {
        for (var k = 0; k < _gangs.Count; k++)
        {
            var g = _gangs[(_turn + k) % _gangs.Count];
            if (!g.BeginRoute(ticket, out from, out to)) continue;
            _turn = (_turn + k + 1) % _gangs.Count;
            return true;
        }
        from = to = default;
        return false;
    }

    /// <summary>Gives the answer to whichever member holds the ticket. False: nobody does (dropped).</summary>
    public bool DeliverRoute(long ticket, IReadOnlyList<Vector3> route)
    {
        foreach (var g in _gangs) if (g.DeliverRoute(ticket, route)) return true;
        return false;
    }

    /// <summary>Serves one route request in all the Raids synchronously, taking turns between them (tests). True when one was served.</summary>
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

    public bool AnyRouteWaiting { get { foreach (var g in _gangs) if (g.RoutesWaiting > 0) return true; return false; } }

    /// <summary>Requests of all the Raids that are out with the worker.</summary>
    public int RoutesInFlight { get { var n = 0; foreach (var g in _gangs) n += g.RoutesInFlight; return n; } }

    public void RecordPoses(uint tick) { foreach (var g in _gangs) g.RecordPoses(tick); }

    public void AddTargets(List<HitTarget> into, uint? view = null) { foreach (var g in _gangs) g.AddTargets(into, view); }

    public DamageResult Hit(ushort hitId, ushort attacker, byte weapon, float amount, KillCause cause)
    {
        foreach (var g in _gangs) if (g.Owns(hitId)) return g.Hit(hitId, attacker, weapon, amount, cause);
        return DamageResult.Ignored;
    }
}
