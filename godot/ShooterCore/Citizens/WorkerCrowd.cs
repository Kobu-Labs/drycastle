using System.Numerics;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Citizens;

/// <summary>Tries each citizen hit handler in turn (the Mass crowd, the Urad workers): the hit goes to the one that knows the id.</summary>
public sealed class CitizenHitRouter : ICitizenHits
{
    readonly List<ICitizenHits> _handlers = new();

    public void Add(ICitizenHits handler) { if (!_handlers.Contains(handler)) _handlers.Add(handler); }

    public DamageResult Hit(ushort hitId, ushort attacker, float amount)
    {
        foreach (var h in _handlers)
            if (h.Hit(hitId, attacker, amount) is var r && r != DamageResult.Ignored) return r;
        return DamageResult.Ignored;
    }
}

/// <summary>
/// The workers of the Obecny Urad (<see cref="CitizenRole.Mayor"/>, <see cref="CitizenRole.Clerk"/>, <see cref="CitizenRole.Announcer"/>): citizen entities that exist only while a rozhlas detour is open.
/// <see cref="Spawn"/> puts them seated at the seats of urad.json (no walking, no navigation mesh, no church), <see cref="Clear"/> removes them all, corpses included. They are passive: they never
/// flee or move, any hit kills one, and a kill scores nothing. Kept apart from the Mass's <see cref="CitizenCrowd"/> (its own hit ids, its own list), so the Mass and a detour overlap without
/// touching each other. <see cref="Killed"/> reports a death (the announcer's is the hijack). The crowd owns no clock: the world entity manager ticks the entities.
/// </summary>
public sealed class WorkerCrowd : ICitizenHits
{
    readonly CitizensConfig _cfg;
    readonly WorldEntityManager _entities;
    readonly Random _rng;
    readonly Dictionary<ushort, Citizen> _byHit = new();
    readonly Dictionary<ushort, ushort> _entityOfHit = new();
    readonly Dictionary<ushort, ushort> _hitOfEntity = new();
    int _nextHit;

    public WorkerCrowd(CitizensConfig cfg, WorldEntityManager entities, int seed = 0)
    {
        _cfg = cfg; _entities = entities;
        _rng = new Random(seed);
        entities.Despawned += OnDespawned;
    }

    /// <summary>A worker was killed by a player's hit. Raised once per worker, inside the hit.</summary>
    public event Action<CitizenKilled>? Killed;

    /// <summary>Workers in the world, dead ones lying included.</summary>
    public int Count => _byHit.Count;
    public int AliveCount => _byHit.Values.Count(c => c.Alive);
    public IEnumerable<Citizen> Workers => _byHit.Values;
    public IEnumerable<Citizen> AliveOfRole(CitizenRole role) => _byHit.Values.Where(c => c.Role == role && c.Alive);

    RozhlasBroadcast? _broadcast;
    Func<IReadOnlyList<UradWorkerSpot>>? _spots;

    /// <summary>
    /// Ties the workers to the loudspeaker: they sit down at <paramref name="spots"/> (asked each time) when the phase goes Idle to OnAir, are all removed when it
    /// is Idle again, and the announcer's death is the hijack (<see cref="RozhlasBroadcast.AnnouncerKilled"/>, which only an OnAir broadcast accepts). Other deaths change nothing.
    /// </summary>
    public void Attach(RozhlasBroadcast broadcast, Func<IReadOnlyList<UradWorkerSpot>> spots)
    {
        _broadcast = broadcast; _spots = spots;
        broadcast.PhaseChanged += (from, to) =>
        {
            if (from == RozhlasPhase.Idle && to == RozhlasPhase.OnAir) Spawn(spots());
            else if (to == RozhlasPhase.Idle) Clear();
        };
        Killed += k => { if (k.Role == CitizenRole.Announcer) broadcast.AnnouncerKilled(); };
    }

    /// <summary>The world was cleared under a detour that is still open (a new round): the workers sit down again, fresh. Without a detour, none remain.</summary>
    public void Resync()
    {
        Clear();
        if (_broadcast is { DetourOpen: true } && _spots != null) Spawn(_spots());
    }

    /// <summary>Puts a fresh worker at every spot, seated (workers already there are removed first). Returns how many were spawned (fewer when the entity cap is reached).</summary>
    public int Spawn(IEnumerable<UradWorkerSpot> spots)
    {
        Clear();
        var n = 0;
        foreach (var s in spots)
        {
            // the look: bit 0 is the body (female), the rest is a number that picks the recolour
            var look = (ushort)((_rng.Next(1 << 15) << 1) | (s.Female ? 1 : 0));
            var feet = s.Pos - new Vector3(0, _cfg.SeatHeightM, 0); // the spot is on the seat (urad.json, like a church seat): the feet stand on the floor below it
            var citizen = new Citizen(_cfg, -1, s.Yaw, new[] { feet }, new[] { feet }, look) { Role = s.Role };
            citizen.Settle();
            if (NextHitId() is not { } hit) break;
            citizen.HitId = hit;
            citizen.Died += OnDied;
            if (_entities.Spawn(citizen) is not { } entity) { citizen.Died -= OnDied; break; }
            _byHit[hit] = citizen;
            _entityOfHit[hit] = entity;
            _hitOfEntity[entity] = hit;
            n++;
        }
        return n;
    }

    /// <summary>Removes every worker, the dead ones too.</summary>
    public void Clear()
    {
        foreach (var entity in _hitOfEntity.Keys.ToList()) _entities.Despawn(entity, WorldDespawnReason.Removed);
    }

    /// <summary>The living workers as hit targets: a sitter is a crouching figure on the floor below the seat, lifted to the pelvis as in the pews.</summary>
    public void AddTargets(List<HitTarget> into)
    {
        foreach (var c in _byHit.Values)
            if (c.Alive) into.Add(new HitTarget(c.HitId, c.Position + new Vector3(0, CitizenCrowd.SeatedHitLiftM, 0), c.HitStance, c.Yaw));
    }

    public DamageResult Hit(ushort hitId, ushort attacker, float amount)
    {
        if (!_byHit.TryGetValue(hitId, out var c) || !c.Alive || !(amount > 0)) return DamageResult.Ignored;
        _entities.Damage(_entityOfHit[hitId], amount, attacker);
        return DamageResult.Killed;
    }

    ushort? NextHitId()
    {
        for (var i = 0; i < CitizenRules.WorkerHitIdCount; i++)
        {
            _nextHit = (_nextHit + 1) % CitizenRules.WorkerHitIdCount;
            var id = (ushort)(CitizenRules.FirstWorkerHitId + _nextHit);
            if (!_byHit.ContainsKey(id)) return id;
        }
        return null;
    }

    void OnDied(Citizen c, ushort killer) => Killed?.Invoke(new CitizenKilled(c.HitId, killer, c.SeatId, c.Position, c.DiedSeated, c.Role));

    void OnDespawned(WorldDespawned d)
    {
        if (!_hitOfEntity.Remove(d.Id, out var hit)) return;
        _entityOfHit.Remove(hit);
        _byHit.Remove(hit);
    }
}
