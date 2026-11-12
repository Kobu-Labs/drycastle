using System.Numerics;

namespace Drycastle.ShooterCore;

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
    /// <summary>The attack helicopter's cannon (killstreak "attack_helicopter"): its kills are the streak's.</summary>
    public const byte AttackHelicopter = 246;
    /// <summary>The AC-130's weapons (killstreak "ac130"): its kills are the streak's, whichever of its three weapons made them.</summary>
    public const byte Ac130 = 244;
    /// <summary>The helicopter helper's rounds (killstreak "helicopter_helper"); its kills count as the streak's, under the weapon id helicopter_gun.</summary>
    public const byte Helper = 245;
    /// <summary>The flak truck's shells (a gunner's rounds): its kills are ordinary kills, not the streak's, like the jeep's roof gun.</summary>
    public const byte Flak = 243;
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
/// <summary>A target dummy (#148) was killed: no roster change, no round end.</summary>
public sealed record TargetKilled(ushort Killer, TargetDummies.Dummy Target, byte Weapon, KillCause Cause) : MatchEvent;

public enum PlayerLife : byte { Unspawned, Alive, Dead }

/// <summary>Team ids (the wire byte too). Two teams only; 0 means no team.</summary>
public static class Teams
{
    public const byte None = 0, A = 1, B = 2;
    public static byte Other(byte team) => team == A ? B : team == B ? A : None;
    /// <summary>Short label that is not a colour: "A" / "B" (empty for none). The HUD shows it next to names.</summary>
    public static string Tag(byte team) => team == A ? "A" : team == B ? "B" : "";
}

public sealed class MatchPlayer
{
    public ushort Id;
    public string Name = "";
    public int Kills, Deaths;
    /// <summary>Team deathmatch: <see cref="Teams.A"/> or <see cref="Teams.B"/>. <see cref="Teams.None"/> (0) in free-for-all and for sandbox players.</summary>
    public byte Team;
    /// <summary>Sandbox mode (#156): godmode, and no kills, score or round end. Ranked after everyone else.</summary>
    public bool Sandbox;
    /// <summary>A sandbox player who chose to take damage (the panel's switch): hurt and killed like anyone, still unscored. Meaningless without <see cref="Sandbox"/>.</summary>
    public bool Vulnerable;
    public PlayerLife Life;
    public float Health;
    public Vector3 Position;
    public double RespawnAt, ProtectedUntil;
}

/// <summary>
/// Match state machine (waiting, running, ending), free-for-all or team deathmatch (<see cref="MatchConfig.Mode"/>). Godot-free and deterministic: time comes from the
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
        _cfg = cfg; Mode = cfg.ParsedMode(); _now = clock; _spawns = spawnCandidates; _rng = rng ?? new Random();
        Entities = new WorldEntityManager(worldTypes);
    }

    /// <summary>World entities (turrets, missiles, ...) of this match. Cleared at each round start. The server ticks it.</summary>
    public WorldEntityManager Entities { get; }

    public MatchMode Mode { get; }
    public bool IsTeamMode => Mode == MatchMode.TeamDeathmatch;
    readonly int[] _teamScore = new int[3];
    /// <summary>Team deathmatch: kills of the team's members this round (team kills and suicides do not count). 0 for no team.</summary>
    public int TeamScore(byte team) => team is Teams.A or Teams.B ? _teamScore[team] : 0;

    /// <summary>Both on the same real team (never in free-for-all, never for the teamless).</summary>
    public bool AreFriends(ushort a, ushort b) =>
        IsTeamMode && a != b && _players.TryGetValue(a, out var pa) && _players.TryGetValue(b, out var pb) && pa.Team != Teams.None && pa.Team == pb.Team;

    public MatchPhase Phase { get; private set; } = MatchPhase.Waiting;
    public IReadOnlyList<ushort> Winners { get; private set; } = Array.Empty<ushort>();
    /// <summary>Team deathmatch: the team that won the finished round, <see cref="Teams.None"/> for a tie, in free-for-all and while no round has ended.</summary>
    public byte WinningTeam { get; private set; }
    public IReadOnlyCollection<MatchPlayer> Players => _players.Values;
    public MatchConfig Config => _cfg;
    /// <summary>The target dummies of this match (#148); empty until <see cref="SetTargets"/>. Not in <see cref="Players"/>.</summary>
    public TargetDummies Targets { get; private set; } = new(Array.Empty<TargetSpot>(), TargetDummies.RespawnDelay);

    public void SetTargets(IReadOnlyList<TargetSpot> spots) => Targets = new TargetDummies(spots, TargetDummies.RespawnDelay);
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

    /// <summary>
    /// Scoreboard order: kills descending, then deaths ascending, then id. In team deathmatch the leading team's block comes
    /// first (ties by team id), and the same order applies inside each team. Sandbox players are last either way.
    /// </summary>
    public List<MatchPlayer> Ranking() => IsTeamMode
        ? _players.Values.OrderBy(p => p.Sandbox).ThenByDescending(p => TeamScore(p.Team)).ThenBy(p => p.Team).ThenByDescending(p => p.Kills).ThenBy(p => p.Deaths).ThenBy(p => p.Id).ToList()
        : _players.Values.OrderBy(p => p.Sandbox).ThenByDescending(p => p.Kills).ThenBy(p => p.Deaths).ThenBy(p => p.Id).ToList();

    /// <summary>Team of the next joiner: the team with fewer (non-sandbox) players, then the lower score, then team A.</summary>
    byte SmallerTeam()
    {
        int a = 0, b = 0;
        foreach (var p in _players.Values) { if (p.Team == Teams.A) a++; else if (p.Team == Teams.B) b++; }
        if (a != b) return a < b ? Teams.A : Teams.B;
        return _teamScore[Teams.B] < _teamScore[Teams.A] ? Teams.B : Teams.A;
    }

    /// <summary>Round start: if the teams differ by two or more, the most recent joiners of the bigger team move over until they differ by at most one.</summary>
    void Rebalance()
    {
        while (true)
        {
            var a = _players.Values.Where(p => p.Team == Teams.A).ToList();
            var b = _players.Values.Where(p => p.Team == Teams.B).ToList();
            if (Math.Abs(a.Count - b.Count) < 2) return;
            var big = a.Count > b.Count ? a : b;
            big.OrderByDescending(p => p.Id).First().Team = Teams.Other(big[0].Team);
        }
    }

    /// <summary>Sandbox (#156): full health for a living player. False when the player is not alive.</summary>
    public bool Heal(ushort id)
    {
        if (!_players.TryGetValue(id, out var p) || p.Life != PlayerLife.Alive) return false;
        p.Health = MoveConsts.MaxHealth;
        return true;
    }

    /// <summary>Sandbox: whether this player takes damage. Off (the default) is godmode. False for anyone who is not a sandbox player.</summary>
    public bool SetVulnerable(ushort id, bool on)
    {
        if (!_players.TryGetValue(id, out var p) || !p.Sandbox) return false;
        p.Vulnerable = on;
        return true;
    }

    public void SetPosition(ushort id, Vector3 pos) { if (_players.TryGetValue(id, out var p)) p.Position = pos; }

    public void AddPlayer(ushort id, string name, bool sandbox = false)
    {
        _players[id] = new MatchPlayer { Id = id, Name = name, Sandbox = sandbox };
        if (IsTeamMode && !sandbox) _players[id].Team = SmallerTeam(); // auto-balance on join
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
        if (TargetDummies.IsDummyId(victim)) return ApplyTargetDamage(victim, attacker, weapon, amount, cause);
        if (Phase != MatchPhase.Running || amount <= 0) return DamageResult.Ignored;
        if (!_players.TryGetValue(victim, out var v) || v.Life != PlayerLife.Alive) return DamageResult.Ignored;
        if (v.Sandbox && !v.Vulnerable) return DamageResult.Ignored; // godmode (#156): nothing hurts a sandbox player, unless they switched damage on
        if (!_cfg.FriendlyFire && AreFriends(victim, attacker)) return DamageResult.Ignored; // teammates cannot hurt each other (self damage is not friendly fire)
        if (!ignoreProtection && IsProtected(victim)) return DamageResult.Ignored;
        v.Health -= amount;
        if (v.Health > 0) return DamageResult.Damaged;

        v.Health = 0; v.Life = PlayerLife.Dead; v.Deaths++;
        v.RespawnAt = _now() + _cfg.RespawnDelay;
        var credited = attacker != victim && _players.TryGetValue(attacker, out var a) ? a : null;
        // a sandbox shooter's kill is shown but counts for nothing: no score, no score goal (#156)
        // a team kill (friendly fire on) scores nothing for the shooter or the team; the death still counts
        var teamKill = credited != null && AreFriends(credited.Id, victim);
        if (credited is { Sandbox: false } && !teamKill) { credited.Kills++; if (IsTeamMode && credited.Team != Teams.None) _teamScore[credited.Team]++; }
        var killer = credited?.Id ?? victim;
        Emit(new PlayerKilled(killer, victim, weapon, cause));
        Emit(new PlayerDied(victim, killer, _cfg.RespawnDelay));
        if (IsTeamMode ? credited is { Sandbox: false, Team: not Teams.None } && !teamKill && _teamScore[credited.Team] >= _cfg.TeamScoreGoal
                       : credited is { Sandbox: false } && credited.Kills >= _cfg.ScoreGoal) End();
        Emit(new ScoreboardChanged());
        return DamageResult.Killed;
    }

    /// <summary>
    /// A target dummy died to <paramref name="killer"/> (#148): the shooter gets +1 kill on the scoreboard. The score goal
    /// does not end the round on a target kill, and nobody spawns or dies because of it. Ignored outside a running round
    /// or for an unknown shooter.
    /// </summary>
    public void CreditTargetKill(ushort killer, TargetDummies.Dummy target, byte weapon, KillCause cause = KillCause.Standard)
    {
        if (Phase != MatchPhase.Running || !_players.TryGetValue(killer, out var a)) return;
        if (!a.Sandbox) a.Kills++; // sandbox kills are shown, not scored (#156)
        Emit(new TargetKilled(killer, target, weapon, cause));
        Emit(new ScoreboardChanged());
    }

    /// <summary>A target dummy takes a hit from every weapon path. A kill credits the shooter (<see cref="CreditTargetKill"/>).</summary>
    DamageResult ApplyTargetDamage(ushort id, ushort attacker, byte weapon, float amount, KillCause cause)
    {
        if (Phase != MatchPhase.Running || amount <= 0) return DamageResult.Ignored;
        var r = Targets.Damage(id, amount, _now());
        if (r == DamageResult.Killed) CreditTargetKill(attacker, Targets.Get(id)!, weapon, cause);
        return r;
    }

    public void Update()
    {
        var now = _now();
        Targets.Update(now);
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
        if (IsTeamMode) Rebalance();
        Array.Clear(_teamScore); WinningTeam = Teams.None;
        foreach (var p in _players.Values) { p.Kills = 0; p.Deaths = 0; p.Life = PlayerLife.Unspawned; }
        Entities.Clear();
        Targets.ResetAll();
        Winners = Array.Empty<ushort>();
        _roundStart = _now();
        Phase = MatchPhase.Running;
        Emit(new PhaseChanged(Phase));
        foreach (var p in _players.Values.ToList()) Spawn(p);
    }

    void ToWaiting()
    {
        Array.Clear(_teamScore); WinningTeam = Teams.None;
        foreach (var p in _players.Values) { p.Kills = 0; p.Deaths = 0; p.Life = PlayerLife.Unspawned; }
        Winners = Array.Empty<ushort>();
        Phase = MatchPhase.Waiting;
        Emit(new PhaseChanged(Phase));
    }

    void End()
    {
        var top = Ranking().Where(p => !p.Sandbox).ToList(); // sandbox players never win (#156)
        var winners = new List<ushort>();
        WinningTeam = Teams.None;
        if (IsTeamMode)
        {
            // the team with the higher score wins and all its members with it; a tie or a scoreless round has no winner
            var win = _teamScore[Teams.A] > _teamScore[Teams.B] ? Teams.A : _teamScore[Teams.B] > _teamScore[Teams.A] ? Teams.B : Teams.None;
            WinningTeam = win;
            if (win != Teams.None) winners = top.Where(p => p.Team == win).Select(p => p.Id).ToList();
        }
        else if (top.Count > 0 && top[0].Kills > 0) // nobody wins a round without a kill
            winners = top.Where(p => p.Kills == top[0].Kills && p.Deaths == top[0].Deaths).Select(p => p.Id).ToList();
        Winners = winners;
        Phase = MatchPhase.Ending;
        _endsAt = _now() + _cfg.EndScreenTime;
        Emit(new PhaseChanged(Phase));
    }

    void Spawn(MatchPlayer p)
    {
        // team deathmatch: only enemies count, so a team may spawn together and never next to the other side
        var others = _players.Values.Where(o => o.Id != p.Id && o.Life == PlayerLife.Alive && (!IsTeamMode || p.Team == Teams.None || o.Team != p.Team)).Select(o => o.Position);
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
