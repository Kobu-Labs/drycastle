using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What the crowd needs from the server: the houses that send citizens, and walkable routes on the navigation mesh.</summary>
public interface ICitizenWorld
{
    /// <summary>The spawn points of the houses between <paramref name="minM"/> and <paramref name="radiusM"/> (ground distance) of <paramref name="around"/>, each on the navigation mesh.</summary>
    IReadOnlyList<Vector3> HouseDoors(Vector3 around, float minM, float radiusM);

    /// <summary>The points to walk from <paramref name="from"/> to <paramref name="to"/>, the first on the mesh near <paramref name="from"/>. Empty when there is no route.</summary>
    IReadOnlyList<Vector3> Route(Vector3 from, Vector3 to);
}

/// <summary>A citizen was killed: who, which seat it was heading for, where it lay, and whether it was already sitting in it.</summary>
public readonly record struct CitizenKilled(ushort HitId, ushort Killer, int SeatId, Vector3 Where, bool WasSeated, CitizenRole Role = CitizenRole.Citizen);

/// <summary>What a weapon hit on a citizen (the match calls it for a victim id in the citizens' range).</summary>
public interface ICitizenHits
{
    DamageResult Hit(ushort hitId, ushort attacker, float amount);
}

/// <summary>
/// Sends citizens to church: the server side of the Holy Mass crowd. <see cref="Spawn(IReadOnlyList{int}, float, float)"/> takes the seats to fill, the
/// time to spread the leaving houses over and the radius around the church door to take houses from; one citizen leaves for each seat, at its
/// own moment. A seat gets at most one citizen (a seat in use, waiting or unknown is skipped), and no more than
/// <see cref="CitizensConfig.MaxCount"/> are alive or waiting at once. <see cref="Clear"/> removes them all. A killed citizen is reported
/// by <see cref="Killed"/> (the event's "sinner" rule reads it). The crowd owns no clock: the server calls <see cref="Update"/> every tick.
/// </summary>
public sealed class CitizenCrowd : ICitizenHits
{
    sealed record Pending(double At, int SeatId, IReadOnlyList<Vector3> Doors);

    readonly CitizensConfig _cfg;
    readonly Church _church;
    readonly ICitizenWorld _world;
    readonly WorldEntityManager _entities;
    readonly Random _rng;
    readonly List<Pending> _pending = new();
    readonly HashSet<int> _taken = new();                       // seats of waiting and living citizens
    readonly HashSet<int> _reserved = new();                    // seats kept for players: no citizen is sent to them
    readonly Dictionary<ushort, Citizen> _byHit = new();
    readonly Dictionary<ushort, ushort> _entityOfHit = new();
    readonly Dictionary<ushort, ushort> _hitOfEntity = new();
    double _clock;
    int _nextHit;

    public CitizenCrowd(CitizensConfig cfg, Church church, ICitizenWorld world, WorldEntityManager entities, int seed = 0)
    {
        _cfg = cfg; _church = church; _world = world; _entities = entities;
        _rng = new Random(seed);
        entities.Despawned += OnDespawned;
    }

    /// <summary>A citizen was killed by a player's hit. Raised once per citizen, inside the hit.</summary>
    public event Action<CitizenKilled>? Killed;

    /// <summary>Error log for citizens that could not leave (no route, the entity cap). Null means silent.</summary>
    public Action<string>? Log { get; set; }

    public Church Church => _church;
    /// <summary>Citizens in the world, dead ones lying included.</summary>
    public int Count => _byHit.Count;
    public int Waiting => _pending.Count;
    /// <summary>Citizens of the pews (not the pastor and monks) in the world or waiting, the number the config's limit applies to.</summary>
    int CitizenCount => _byHit.Values.Count(c => c.Role == CitizenRole.Citizen);
    public int AliveCount => _byHit.Values.Count(c => c.Alive);
    public int SeatedCount => _byHit.Values.Count(c => c.Seated);
    /// <summary>Seats that have no citizen alive or on the way.</summary>
    public IEnumerable<int> FreeSeats => _church.Seats.Where(s => IsSeatFree(s.Id)).Select(s => s.Id);

    /// <summary>No citizen is on its way to, in or lying at the seat, and it is not kept for a player.</summary>
    public bool IsSeatFree(int seatId) => !_taken.Contains(seatId) && !_reserved.Contains(seatId);

    /// <summary>Keeps a seat for players (no citizen is sent to it from now on). False when a citizen has it.</summary>
    public bool Reserve(int seatId) => !_taken.Contains(seatId) && _reserved.Add(seatId);

    public void Unreserve(int seatId) => _reserved.Remove(seatId);

    /// <summary>The Mass is done: every living citizen in place stands, a share of them clapping (<paramref name="clapFraction"/>). The pastor and monks too.</summary>
    public void Finish(float clapFraction)
    {
        foreach (var c in _byHit.Values) c.Finish(c.Role == CitizenRole.Pastor || _rng.NextDouble() < clapFraction);
    }

    /// <summary>The living pastor or monks, for the event.</summary>
    public IEnumerable<Citizen> AliveOfRole(CitizenRole role) => _byHit.Values.Where(c => c.Role == role && c.Alive);

    /// <summary>
    /// Puts the pastor or a monk in the world: it stands at <paramref name="walk"/>'s last point after walking it (one point: stands there at once).
    /// Hit like a citizen (one hit). Null when the entity cap is reached.
    /// </summary>
    public Citizen? SpawnRole(CitizenRole role, IReadOnlyList<Vector3> walk, float yaw)
    {
        var citizen = new Citizen(_cfg, -1, yaw, walk, new[] { walk[^1] }, (ushort)_rng.Next(ushort.MaxValue + 1), 1f) { Role = role };
        citizen.Settle();
        var hit = NextHitId();
        citizen.HitId = hit;
        citizen.Died += OnDied;
        if (_entities.Spawn(citizen) is not { } entity) { citizen.Died -= OnDied; return null; }
        _byHit[hit] = citizen;
        _entityOfHit[hit] = entity;
        _hitOfEntity[entity] = hit;
        return citizen;
    }

    /// <summary>
    /// Sends one citizen to each seat of <paramref name="seatIds"/> that is free, spread evenly over <paramref name="staggerSeconds"/> from houses between
    /// the config's minimum and <paramref name="houseRadiusM"/> of the church door. Returns how many were accepted (0 when there are no houses).
    /// </summary>
    public int Spawn(IReadOnlyList<int> seatIds, float staggerSeconds, float houseRadiusM)
    {
        var take = new List<int>();
        foreach (var id in seatIds)
        {
            if (_pending.Count + CitizenCount + take.Count >= _cfg.MaxCount) break;
            if (_church.Seat(id) == null || !IsSeatFree(id) || take.Contains(id)) continue;
            take.Add(id);
        }
        if (take.Count == 0) return 0;
        var doors = _world.HouseDoors(_church.DoorPos, _cfg.HouseMinM, houseRadiusM);
        if (doors.Count == 0) { Log?.Invoke($"citizens: no house within {houseRadiusM:0} m of the church door, nobody leaves"); return 0; }
        for (var i = 0; i < take.Count; i++)
        {
            _taken.Add(take[i]);
            _pending.Add(new Pending(_clock + staggerSeconds * i / take.Count, take[i], doors));
        }
        return take.Count;
    }

    /// <summary>Sends <paramref name="count"/> citizens to free seats, picked at random (the dev command). The config's stagger time and house radius unless given.</summary>
    public int Spawn(int count, float? staggerSeconds = null, float? houseRadiusM = null)
    {
        var free = FreeSeats.OrderBy(_ => _rng.Next()).Take(Math.Max(0, count)).ToList();
        return Spawn(free, staggerSeconds ?? _cfg.StaggerSeconds, houseRadiusM ?? _cfg.HouseRadiusM);
    }

    /// <summary>Removes every citizen and cancels those still waiting to leave.</summary>
    public void Clear()
    {
        _pending.Clear();
        foreach (var entity in _hitOfEntity.Keys.ToList()) _entities.Despawn(entity, WorldDespawnReason.Removed);
        _taken.Clear();
        _reserved.Clear();
    }

    /// <summary>Lets the citizens whose time has come leave their houses. The entities are ticked by the world entity manager.</summary>
    public void Update(float dt)
    {
        _clock += dt;
        while (_pending.Count > 0 && _pending[0].At <= _clock)
        {
            var p = _pending[0];
            _pending.RemoveAt(0);
            if (!Leave(p)) _taken.Remove(p.SeatId);
        }
    }

    bool Leave(Pending p)
    {
        var seat = _church.Seat(p.SeatId)!;
        var door = p.Doors[_rng.Next(p.Doors.Count)];
        var route = _world.Route(door, _church.DoorPos);
        if (route.Count == 0) { Log?.Invoke($"citizens: no route from a house at ({door.X:0}, {door.Z:0}) to the church door, seat {seat.Id} stays empty"); return false; }
        var look = (ushort)_rng.Next(ushort.MaxValue + 1);
        var factor = 1f + (float)(_rng.NextDouble() * 2 - 1) * _cfg.SpeedJitter;
        var citizen = new Citizen(_cfg, seat.Id, seat.Yaw, route, _church.RouteTo(seat, _cfg.SeatHeightM), look, factor) { Stand = seat.Stand };
        var hit = NextHitId();
        citizen.HitId = hit;
        citizen.Died += OnDied;
        if (_entities.Spawn(citizen) is not { } entity) { citizen.Died -= OnDied; return false; }
        _byHit[hit] = citizen;
        _entityOfHit[hit] = entity;
        _hitOfEntity[entity] = hit;
        return true;
    }

    ushort NextHitId()
    {
        do { _nextHit = (_nextHit + 1) % CitizenRules.HitIdCount; }
        while (_byHit.ContainsKey((ushort)(CitizenRules.FirstHitId + _nextHit)));
        return (ushort)(CitizenRules.FirstHitId + _nextHit);
    }

    void OnDied(Citizen c, ushort killer) => Killed?.Invoke(new CitizenKilled(c.HitId, killer, c.SeatId, c.Position, c.DiedSeated, c.Role));

    void OnDespawned(WorldDespawned d)
    {
        if (!_hitOfEntity.Remove(d.Id, out var hit)) return;
        _entityOfHit.Remove(hit);
        if (_byHit.Remove(hit, out var c)) _taken.Remove(c.SeatId);
    }

    public DamageResult Hit(ushort hitId, ushort attacker, float amount)
    {
        if (!_byHit.TryGetValue(hitId, out var c) || !c.Alive || !(amount > 0)) return DamageResult.Ignored;
        _entities.Damage(_entityOfHit[hitId], amount, attacker);
        return DamageResult.Killed;
    }

    /// <summary>Adds the living citizens as hit targets: a walker upright where it stands, a sitter crouching on the floor under its seat.</summary>
    public void AddTargets(List<HitTarget> into)
    {
        foreach (var c in _byHit.Values)
            if (c.Alive) into.Add(new HitTarget(c.HitId, c.Position, c.HitStance, c.Yaw));
    }
}
