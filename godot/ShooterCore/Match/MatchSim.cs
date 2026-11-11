using System.Numerics;

namespace Suchohrad.ShooterCore;

public enum MatchPhase : byte { Waiting = 0, Running = 1, Ending = 2 }

public enum DamageResult { Ignored, Damaged, Killed }

/// <summary>Weapon ids for deaths that no weapon from weapons.json caused.</summary>
public static class SpecialWeapons
{
    /// <summary>A vehicle run over or blown up; <see cref="VehicleGun"/> is the jeep's roof gun.</summary>
    public const byte Drown = 251, Vehicle = 252, Debug = 253, Void = 254, Fall = 255;
    /// <summary>The jeep's roof gun (a gunner's rounds).</summary>
    public const byte VehicleGun = 250;
    /// <summary>The MOAB's blast (ticket #129): its kills are streak kills.</summary>
    public const byte Moab = 249;
    /// <summary>The attack missile's blast (killstreak "attack_missile"): the kill feed and the stats name it.</summary>
    public const byte Missile = 248;
    /// <summary>A sentry turret's shots (killstreak "turret"); its kills count as the streak's, under the weapon id turret_gun.</summary>
    public const byte Turret = 247;
    /// <summary>The AC-130's weapons (killstreak "ac130"): its kills are the streak's, whichever of its three weapons made them.</summary>
    public const byte Ac130 = 244;
}

public abstract record MatchEvent;
public sealed record PhaseChanged(MatchPhase Phase) : MatchEvent;
/// <summary>Roster or scores changed: broadcast a fresh scoreboard.</summary>
public sealed record ScoreboardChanged : MatchEvent;
public sealed record PlayerSpawned(ushort Id, Vector3 Position, float ProtectionSeconds) : MatchEvent;
/// <summary>How a kill happened beyond the weapon. Headshot is set by the caller's hit; Roadkill by a vehicle run-over.</summary>
public enum KillCause : byte { Standard = 0, Headshot = 1, Roadkill = 2 }

/// <summary>Killer == Victim for suicides.</summary>
public sealed record PlayerKilled(ushort Killer, ushort Victim, byte Weapon, KillCause Cause = KillCause.Standard) : MatchEvent;
public sealed record PlayerDied(ushort Victim, ushort Killer, float RespawnDelay) : MatchEvent;

public enum PlayerLife : byte { Unspawned, Alive, Dead }

public sealed class MatchPlayer
{
    public ushort Id;
    public string Name = "";
    public int Kills, Deaths;
    public PlayerLife Life;
    public float Health;
    public Vector3 Position;
    public double RespawnAt, ProtectedUntil;
}

/// <summary>
/// Free-for-all match state machine (waiting, running, ending). Godot-free and deterministic: time comes from the
/// injected clock (seconds), spawn randomness from the injected <see cref="Random"/>. The host calls
/// <see cref="Update"/> every tick and reacts to <see cref="Event"/>.
/// </summary>
public sealed class MatchSim
{
    readonly MatchConfig _cfg;
    readonly Func<double> _now;
    readonly IReadOnlyList<Vector3> _spawns;
    readonly Random _rng;
    readonly SortedDictionary<ushort, MatchPlayer> _players = new();
    double _roundStart, _endsAt;

    public MatchSim(MatchConfig cfg, Func<double> clock, IReadOnlyList<Vector3> spawnCandidates, Random? rng = null, WorldEntityRegistry? worldTypes = null)
    {
        _cfg = cfg; _now = clock; _spawns = spawnCandidates; _rng = rng ?? new Random();
        Entities = new WorldEntityManager(worldTypes);
    }

    /// <summary>World entities (turrets, missiles, ...) of this match. Cleared at each round start. The server ticks it.</summary>
    public WorldEntityManager Entities { get; }

    public MatchPhase Phase { get; private set; } = MatchPhase.Waiting;
    public IReadOnlyList<ushort> Winners { get; private set; } = Array.Empty<ushort>();
    public IReadOnlyCollection<MatchPlayer> Players => _players.Values;
    public MatchConfig Config => _cfg;
    public event Action<MatchEvent>? Event;

    public MatchPlayer? Get(ushort id) => _players.GetValueOrDefault(id);

    /// <summary>Seconds left: until the time limit while running, until the restart while ending, else 0.</summary>
    public float TimeRemaining => Phase switch
    {
        MatchPhase.Running => (float)Math.Max(0, _roundStart + _cfg.TimeLimit - _now()),
        MatchPhase.Ending => (float)Math.Max(0, _endsAt - _now()),
        _ => 0,
    };

    public bool IsProtected(ushort id) => _players.TryGetValue(id, out var p) && p.Life == PlayerLife.Alive && _now() < p.ProtectedUntil;

    public EntityState StateOf(ushort id)
    {
        if (!_players.TryGetValue(id, out var p) || p.Life != PlayerLife.Alive) return EntityState.Dead;
        return IsProtected(id) ? EntityState.Protected : EntityState.Alive;
    }

    /// <summary>Scoreboard order: kills descending, then deaths ascending, then id.</summary>
    public List<MatchPlayer> Ranking() =>
        _players.Values.OrderByDescending(p => p.Kills).ThenBy(p => p.Deaths).ThenBy(p => p.Id).ToList();

    public void SetPosition(ushort id, Vector3 pos) { if (_players.TryGetValue(id, out var p)) p.Position = pos; }

    public void AddPlayer(ushort id, string name)
    {
        _players[id] = new MatchPlayer { Id = id, Name = name };
        if (Phase == MatchPhase.Waiting) { StartIfReady(); }
        else if (Phase == MatchPhase.Running) Spawn(_players[id]);
        // while ending the newcomer waits for the restart
        Emit(new ScoreboardChanged());
    }

    public void RemovePlayer(ushort id)
    {
        if (!_players.Remove(id)) return;
        if (Phase != MatchPhase.Waiting && _players.Count < _cfg.MinPlayers) ToWaiting();
        Emit(new ScoreboardChanged());
    }

    /// <summary>The player fired a weapon: spawn protection ends early. True if it was active.</summary>
    public bool NotifyFired(ushort id)
    {
        if (!IsProtected(id)) return false;
        _players[id].ProtectedUntil = 0;
        return true;
    }

    /// <summary>
    /// Damage from <paramref name="attacker"/> (the victim itself or an unknown id for suicides and the world).
    /// Ignored outside a running round, on dead players and during spawn protection (unless
    /// <paramref name="ignoreProtection"/>, used for falling out of the world). Death scores the attacker +1 unless it is a suicide.
    /// </summary>
    public DamageResult ApplyDamage(ushort victim, ushort attacker, byte weapon, float amount, bool ignoreProtection = false, KillCause cause = KillCause.Standard)
    {
        if (Phase != MatchPhase.Running || amount <= 0) return DamageResult.Ignored;
        if (!_players.TryGetValue(victim, out var v) || v.Life != PlayerLife.Alive) return DamageResult.Ignored;
        if (!ignoreProtection && IsProtected(victim)) return DamageResult.Ignored;
        v.Health -= amount;
        if (v.Health > 0) return DamageResult.Damaged;

        v.Health = 0; v.Life = PlayerLife.Dead; v.Deaths++;
        v.RespawnAt = _now() + _cfg.RespawnDelay;
        var credited = attacker != victim && _players.TryGetValue(attacker, out var a) ? a : null;
        if (credited != null) credited.Kills++;
        var killer = credited?.Id ?? victim;
        Emit(new PlayerKilled(killer, victim, weapon, cause));
        Emit(new PlayerDied(victim, killer, _cfg.RespawnDelay));
        if (credited != null && credited.Kills >= _cfg.ScoreGoal) End();
        Emit(new ScoreboardChanged());
        return DamageResult.Killed;
    }

    public void Update()
    {
        var now = _now();
        switch (Phase)
        {
            case MatchPhase.Running:
                if (now >= _roundStart + _cfg.TimeLimit) { End(); Emit(new ScoreboardChanged()); break; }
                foreach (var p in _players.Values.ToList())
                    if (p.Life == PlayerLife.Dead && now >= p.RespawnAt) Spawn(p);
                break;
            case MatchPhase.Ending:
                if (now >= _endsAt) { StartRound(); Emit(new ScoreboardChanged()); }
                break;
        }
    }

    void StartIfReady() { if (_players.Count >= _cfg.MinPlayers && _players.Count > 0) { StartRound(); } }

    void StartRound()
    {
        foreach (var p in _players.Values) { p.Kills = 0; p.Deaths = 0; p.Life = PlayerLife.Unspawned; }
        Entities.Clear();
        Winners = Array.Empty<ushort>();
        _roundStart = _now();
        Phase = MatchPhase.Running;
        Emit(new PhaseChanged(Phase));
        foreach (var p in _players.Values.ToList()) Spawn(p);
    }

    void ToWaiting()
    {
        foreach (var p in _players.Values) { p.Kills = 0; p.Deaths = 0; p.Life = PlayerLife.Unspawned; }
        Winners = Array.Empty<ushort>();
        Phase = MatchPhase.Waiting;
        Emit(new PhaseChanged(Phase));
    }

    void End()
    {
        var top = Ranking();
        var winners = new List<ushort>();
        if (top.Count > 0)
            winners = top.Where(p => p.Kills == top[0].Kills && p.Deaths == top[0].Deaths).Select(p => p.Id).ToList();
        Winners = winners;
        Phase = MatchPhase.Ending;
        _endsAt = _now() + _cfg.EndScreenTime;
        Emit(new PhaseChanged(Phase));
    }

    void Spawn(MatchPlayer p)
    {
        var others = _players.Values.Where(o => o.Id != p.Id && o.Life == PlayerLife.Alive).Select(o => o.Position);
        p.Position = SpawnChooser.Choose(_spawns, others, _cfg.MinSpawnDistance, _rng);
        p.Life = PlayerLife.Alive;
        p.Health = MoveConsts.MaxHealth;
        p.ProtectedUntil = _now() + _cfg.SpawnProtection;
        Emit(new PlayerSpawned(p.Id, p.Position, _cfg.SpawnProtection));
    }

    void Emit(MatchEvent e) => Event?.Invoke(e);
}

public static class SpawnChooser
{
    /// <summary>
    /// A random candidate at least <paramref name="minDistance"/> from every living player; if none qualifies, the candidate
    /// whose nearest living player is farthest away. No candidates: the origin.
    /// </summary>
    public static Vector3 Choose(IReadOnlyList<Vector3> candidates, IEnumerable<Vector3> living, float minDistance, Random rng)
    {
        if (candidates.Count == 0) return Vector3.Zero;
        var l = living.ToList();
        float Nearest(Vector3 c) => l.Count == 0 ? float.PositiveInfinity : l.Min(o => Vector3.Distance(c, o));
        var ok = candidates.Where(c => Nearest(c) >= minDistance).ToList();
        if (ok.Count > 0) return ok[rng.Next(ok.Count)];
        return candidates.OrderByDescending(Nearest).First();
    }
}
