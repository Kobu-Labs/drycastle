using System.Numerics;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.ShooterCore.Match;

/// <summary>Where the match is. The machine is <see cref="MatchSim.PhaseSpec"/>; the values travel in the MatchState event, so they never change.</summary>
public enum MatchPhase : byte { Waiting = 0, Running = 1, Ending = 2 }

/// <summary>Saved: a lethal hit was negated (a blessing, <see cref="BlessingRules"/>); the victim is at full health and the shooter has no kill.</summary>
public enum DamageResult { Ignored, Damaged, Killed, Saved }

/// <summary>Weapon ids for deaths that no weapon from weapons.json caused.</summary>
public static class SpecialWeapons
{
    /// <summary>A vehicle run over or blown up; <see cref="VehicleGun"/> is the vehicle machine gun (the Humvee's, the ring gun's, the door gun's).</summary>
    public const byte Vehicle = 252, Debug = 253, Void = 254, Fall = 255;
    /// <summary>The vehicle machine gun (a gunner's rounds).</summary>
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
    /// <summary>The flak truck's shells (a gunner's rounds): its kills are ordinary kills, not the streak's, like the vehicle machine gun.</summary>
    public const byte Flak = 243;
    /// <summary>What a Gang member does to a player (a fist or a pipe, Thieves event; a Gunman's round, the Rival crew): the kill feed names it.</summary>
    public const byte Gang = 240;
    /// <summary>What the Bigfoot does to a player (its logs, shockwave and launch, issue #264): the kill feed names it.</summary>
    public const byte Bigfoot = 241;
    /// <summary>Burning (the devil's curse, <see cref="BurnSet"/>): a kill is credited to the curse owner and named "Burning" in the feed.</summary>
    public const byte Burn = 242;
    /// <summary>The player's own Respawn action (#284, <see cref="MatchSim.ForceDeath"/>): the kill feed names it.</summary>
    public const byte Redeploy = 239;
    /// <summary>The harvester's reel (#331): a player drawn into it; the kill feed names it. A run-over by the harvester is a plain Vehicle kill.</summary>
    public const byte Harvester = 238;
    /// <summary>The Scud missile's blast (GLOSSARY "Scud blast"): ordinary kills credited to the player who placed the lock; the kill feed names it.</summary>
    public const byte Scud = 237;
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
/// <summary>An admin moved a player to the other team (#312), the player alive or dead: the host drops what ties them to the old side (vehicle, turrets, streak progress). A living player is respawned at once, so a PlayerSpawned follows.</summary>
public sealed record PlayerTeamChanged(ushort Id, byte From, byte To) : MatchEvent;
public sealed record PlayerDied(ushort Victim, ushort Killer, float RespawnDelay) : MatchEvent;
/// <summary>A target dummy (#148) was killed: no roster change, no round end.</summary>
public sealed record TargetKilled(ushort Killer, TargetDummies.Dummy Target, byte Weapon, KillCause Cause) : MatchEvent;

/// <summary>Whether a player is in the world. The machine is <see cref="MatchPlayer.LifeSpec"/>; <see cref="MatchSim.StateOf"/> turns it (with protection) into the wire's <see cref="EntityState"/>.</summary>
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
    /// <summary>Team deathmatch: <see cref="Teams.A"/> or <see cref="Teams.B"/>. <see cref="Teams.None"/> (0) in free-for-all, for sandbox players and, in team deathmatch, for a player an admin took out of the round (#312).</summary>
    public byte Team;
    /// <summary>The squad (#379, <see cref="MatchSim.CreateSquad"/>): an opt-in tag inside the team, 0 for none. Bots and the teamless never have one; it clears with a team change.</summary>
    public byte Squad;
    /// <summary>A bot: the balance moves bots before humans.</summary>
    public bool Bot;
    /// <summary>Sandbox mode (#156): godmode, and no kills, score or round end. Ranked after everyone else.</summary>
    public bool Sandbox;
    /// <summary>A sandbox player who chose to take damage (the panel's switch): hurt and killed like anyone, still unscored. Meaningless without <see cref="Sandbox"/>.</summary>
    public bool Vulnerable;
    /// <summary>The player's life machine (<see cref="LifeSpec"/>). Only the match changes it.</summary>
    public PlayerLife Life => _life.Current;
    readonly StateMachine<PlayerLife> _life = new(LifeSpec);
    internal void SetLife(PlayerLife next) => _life.Enter(next);

    /// <summary>
    /// Alive from a spawn until a lethal hit, dead until the respawn delay is over, then alive again. A round start or a drop to waiting puts everyone back to
    /// unspawned (the match spawns them again at once when a round starts). Protection, invulnerability and being held are timers beside the life, not states of it.
    /// </summary>
    public static readonly StateMachineSpec<PlayerLife> LifeSpec = StateMachineSpec<PlayerLife>.Define("Match.PlayerLife", "Whether a player is in the world: unspawned, alive or dead awaiting the respawn.", PlayerLife.Unspawned)
        .Allow(PlayerLife.Unspawned, PlayerLife.Alive)
        .Allow(PlayerLife.Alive, PlayerLife.Dead, PlayerLife.Unspawned)
        .Allow(PlayerLife.Dead, PlayerLife.Alive, PlayerLife.Unspawned)
        .Note(PlayerLife.Unspawned, "Not in the world: before the first spawn, or between a round reset and the spawn that follows it. Hits are ignored.")
        .Note(PlayerLife.Alive, "In the world with health. Spawn protection (ProtectedUntil), a blessing's invulnerability and the demon's hold are timers beside this state.")
        .Note(PlayerLife.Dead, "Awaiting RespawnAt (the respawn delay after the lethal hit).")
        .Build();
    public float Health;
    public Vector3 Position;
    public double RespawnAt, ProtectedUntil;
    /// <summary>Invulnerable until this time after a blessing saved the player; unlike spawn protection, firing does not end it.</summary>
    public double InvulnerableUntil;
    /// <summary>Held down by the demon's pounce: nothing hurts, kills or heals them while it lasts (<see cref="MatchSim.SetHeld"/>).</summary>
    public bool Held;
}

/// <summary>
/// Match state machine (waiting, running, ending), free-for-all or team deathmatch (<see cref="MatchConfig.Mode"/>). Godot-free and deterministic: time comes from the
/// injected clock (seconds), spawn randomness from the injected <see cref="Random"/>. The host calls
/// <see cref="Update"/> every tick and reacts to <see cref="Event"/>.
/// </summary>
public sealed partial class MatchSim
{
    readonly MatchConfig _cfg;
    readonly Func<double> _now;
    readonly IReadOnlyList<Vector3> _spawns;
    readonly Random _rng;
    readonly SortedDictionary<ushort, MatchPlayer> _players = new();
    /// <summary>
    /// What can follow what: a round starts from waiting or the end screen (and an admin's restart starts a fresh one even while running, which announces
    /// <see cref="PhaseChanged"/> again without a transition); it ends by the time limit or the score goal; too few players drop any phase back to waiting.
    /// </summary>
    public static readonly StateMachineSpec<MatchPhase> PhaseSpec = StateMachineSpec<MatchPhase>.Define("Match.Phase", "The match loop: waiting for players, a running round, the end screen.", MatchPhase.Waiting)
        .Allow(MatchPhase.Waiting, MatchPhase.Running)
        .Allow(MatchPhase.Running, MatchPhase.Ending, MatchPhase.Waiting)
        .Allow(MatchPhase.Ending, MatchPhase.Running, MatchPhase.Waiting)
        .Note(MatchPhase.Waiting, "Fewer than MinPlayers (nobody, in the shipped match.json). Scores and lives are reset; the next join or restart starts a round.")
        .Note(MatchPhase.Running, "Time limit counts down from the round start. Left by the time limit or score goal (Ending), too few players (Waiting). An admin restart re-enters it with a fresh round.")
        .Note(MatchPhase.Ending, "End screen for EndScreenTime; hits are ignored. Then a new round starts by itself.")
        .Build();
    readonly StateMachine<MatchPhase> _phase = new(PhaseSpec);
    /// <summary>The round started and the end screen's end on the injected clock (absolute seconds, compared with <c>now &gt;=</c> each <see cref="Update"/>, so no countdown to tick).</summary>
    double _roundStart, _endsAt;

    public MatchSim(MatchConfig cfg, Func<double> clock, IReadOnlyList<Vector3> spawnCandidates, Random? rng = null, WorldEntityRegistry? worldTypes = null)
    {
        _cfg = cfg; Mode = cfg.ParsedMode(); _now = clock; _spawns = spawnCandidates; _rng = rng ?? new Random();
        Entities = new WorldEntityManager(worldTypes);
    }

    /// <summary>World entities (turrets, missiles, ...) of this match. Cleared at each round start. The server ticks it.</summary>
    public WorldEntityManager Entities { get; }

    public MatchMode Mode { get; private set; }
    public bool IsTeamMode => Mode == MatchMode.TeamDeathmatch;
    readonly int[] _teamScore = new int[3];
    /// <summary>Team deathmatch: kills of the team's members this round (team kills and suicides do not count). 0 for no team.</summary>
    public int TeamScore(byte team) => team is Teams.A or Teams.B ? _teamScore[team] : 0;

    /// <summary>Both on the same real team (never in free-for-all, never for the teamless).</summary>
    public bool AreFriends(ushort a, ushort b) =>
        IsTeamMode && a != b && _players.TryGetValue(a, out var pa) && _players.TryGetValue(b, out var pb) && pa.Team != Teams.None && pa.Team == pb.Team;

    /// <summary>
    /// A hit by <paramref name="attacker"/> on a vehicle is dropped (#338): friendly fire is off and a teammate sits in any seat. An empty vehicle is never
    /// protected; the attacker's own seat does not protect it (self damage is not friendly fire). Occupant 0 = empty seat. Attacker 0 (crash, water) is never blocked.
    /// </summary>
    public bool VehicleHitBlocked(ushort attacker, IEnumerable<ushort> occupants)
    {
        if (attacker == 0 || _cfg.FriendlyFire) return false;
        foreach (var o in occupants) if (o != 0 && AreFriends(attacker, o)) return true;
        return false;
    }

    public MatchPhase Phase => _phase.Current;
    public IReadOnlyList<ushort> Winners { get; private set; } = Array.Empty<ushort>();
    /// <summary>Team deathmatch: the team that won the finished round, <see cref="Teams.None"/> for a tie, in free-for-all and while no round has ended.</summary>
    public byte WinningTeam { get; private set; }
    public IReadOnlyCollection<MatchPlayer> Players => _players.Values;
    public MatchConfig Config => _cfg;
    /// <summary>The target dummies of this match (#148); empty until <see cref="SetTargets"/>. Not in <see cref="Players"/>.</summary>
    public TargetDummies Targets { get; private set; } = new(Array.Empty<TargetSpot>(), TargetDummies.RespawnDelay);

    /// <summary>The citizens of the Holy Mass, set by the server; a hit on a victim id in their range goes to them (no score, no kill feed).</summary>
    public ICitizenHits? Citizens { get; set; }

    /// <summary>The pets (property groups' animals), set by the server; a hit on a victim id in their range goes to them (no score, no kill feed).</summary>
    public IAnimalHits? Pets { get; set; }
    /// <summary>The wild animals (ids from <see cref="WildlifeRules.FirstHitId"/>); null: none.</summary>
    public IAnimalHits? Wildlife { get; set; }

    /// <summary>The Gang members of the Thieves event, set by the server; a hit on a victim id in their range goes to them (a Hit count, no score, no kill feed).</summary>
    public IGangHits? Gang { get; set; }

    /// <summary>The Raid vans of the Thieves event, set by the server; a hit on a victim id in their range (<see cref="RaidVanRules.IsHitId"/>) goes to them (a Hit count, no score).</summary>
    public IGangHits? Vans { get; set; }

    public void SetTargets(IReadOnlyList<TargetSpot> spots) => Targets = new TargetDummies(spots, TargetDummies.RespawnDelay);
    public event Action<MatchEvent>? Event;

    /// <summary>Asked when a hit would kill (not for falls out of the world or the MOAB, which ignore protection): true negates it once (the host removes the blessing).</summary>
    public Func<ushort, bool>? SaveFromDeath { get; set; }

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

    /// <summary>Team deathmatch player with no team (#312): taken out of the round by an admin, not spawned until put on a team. Sandbox players have no team and play.</summary>
    bool Benched(MatchPlayer p) => IsTeamMode && !p.Sandbox && p.Team == Teams.None;

    /// <summary>
    /// Evens the teams out: while they differ by two or more, one player of the bigger team moves over; bots go before humans, the most recent joiner first.
    /// Unassigned players stay unassigned. <paramref name="quiet"/> (the round start, where nobody is in the world) moves without respawns. Returns who moved.
    /// </summary>
    List<MatchPlayer> Rebalance(bool quiet = false)
    {
        var moved = new List<MatchPlayer>();
        while (true)
        {
            var a = _players.Values.Where(p => p.Team == Teams.A).ToList();
            var b = _players.Values.Where(p => p.Team == Teams.B).ToList();
            if (Math.Abs(a.Count - b.Count) < 2) return moved;
            var big = a.Count > b.Count ? a : b;
            var pick = big.OrderByDescending(p => p.Bot).ThenByDescending(p => p.Id).First();
            Move(pick, Teams.Other(pick.Team), quiet);
            moved.Add(pick);
        }
    }

    /// <summary>
    /// Puts a player on a team, or (<see cref="Teams.None"/>) out of the round. One in the world leaves their old side: the host is told. A living player moved to a team respawns
    /// at once (at the new side's spawn); one moved out dies without a death on the record and waits; an unassigned one put on a team spawns. A dead player with a respawn timer keeps it.
    /// Scores stay where they were earned.
    /// </summary>
    void Move(MatchPlayer p, byte team, bool quiet = false)
    {
        var from = p.Team;
        if (from == team) return;
        p.Team = team;
        DropSquad(p); // a squad is a tag inside one team
        if (quiet) return;
        var running = Phase == MatchPhase.Running;
        if (p.Life == PlayerLife.Unspawned && !running) return; // not in the world and no round on: nothing to tell
        Emit(new PlayerTeamChanged(p.Id, from, team));
        if (!running) return;
        if (team == Teams.None)
        {
            if (p.Life == PlayerLife.Alive) p.SetLife(PlayerLife.Dead);
            p.Health = 0; p.RespawnAt = double.PositiveInfinity; // waits for an admin
            return;
        }
        if (p.Life == PlayerLife.Alive) p.SetLife(PlayerLife.Unspawned);
        if (p.Life == PlayerLife.Unspawned || double.IsPositiveInfinity(p.RespawnAt)) Spawn(p); // alive: respawn on the new side; unassigned: in at last (a timed respawn stays as it is)
    }

    /// <summary>
    /// An admin puts a player on a team or, with <see cref="Teams.None"/>, out of the round (#312). Team deathmatch only; false for an unknown or sandbox player, or outside
    /// team deathmatch. Kills and deaths stay with the player, the team score with the old team.
    /// </summary>
    public bool SetTeam(ushort id, byte team)
    {
        if (!IsTeamMode || team > Teams.B || !_players.TryGetValue(id, out var p) || p.Sandbox) return false;
        Move(p, team);
        Emit(new ScoreboardChanged());
        return true;
    }

    /// <summary>Every bot goes to <paramref name="team"/>. Returns how many moved; null outside team deathmatch or for a team that is not A or B.</summary>
    public int? MoveBots(byte team)
    {
        if (!IsTeamMode || team is not (Teams.A or Teams.B)) return null;
        var moved = 0;
        foreach (var p in _players.Values.Where(p => p.Bot && !p.Sandbox && p.Team != team).ToList()) { Move(p, team); moved++; }
        Emit(new ScoreboardChanged());
        return moved;
    }

    /// <summary>Shuffle: every player on a team is dealt at random into two even teams (the unassigned stay out). False outside team deathmatch.</summary>
    public bool ShuffleTeams()
    {
        if (!IsTeamMode) return false;
        var all = _players.Values.Where(p => !p.Sandbox && p.Team != Teams.None).OrderBy(_ => _rng.Next()).ToList();
        for (var i = 0; i < all.Count; i++) Move(all[i], i % 2 == 0 ? Teams.A : Teams.B);
        Emit(new ScoreboardChanged());
        return true;
    }

    /// <summary>Balance: the round-start balance now, mid-round. Returns who moved; null outside team deathmatch.</summary>
    public IReadOnlyList<MatchPlayer>? BalanceTeams()
    {
        if (!IsTeamMode) return null;
        var moved = Rebalance();
        if (moved.Count > 0) Emit(new ScoreboardChanged());
        return moved;
    }

    /// <summary>Sandbox (#156): full health for a living player. False when the player is not alive.</summary>
    public bool Heal(ushort id)
    {
        if (!_players.TryGetValue(id, out var p) || p.Life != PlayerLife.Alive || p.Held) return false;
        p.Health = MoveConsts.MaxHealth;
        return true;
    }

    /// <summary>The demon's pounce holds a player (or lets go): while held nothing damages them, not even the world (a fall, the void), and a heal does nothing. False when the player is not here.</summary>
    public bool SetHeld(ushort id, bool held)
    {
        if (!_players.TryGetValue(id, out var p)) return false;
        p.Held = held;
        return true;
    }

    public bool IsHeld(ushort id) => _players.TryGetValue(id, out var p) && p.Held;

    /// <summary>Sets a living player's health to exactly <paramref name="health"/> (the pounce leaves them at a few): above or below what it was. False when they are not alive.</summary>
    public bool SetHealth(ushort id, float health)
    {
        if (!_players.TryGetValue(id, out var p) || p.Life != PlayerLife.Alive) return false;
        p.Health = Math.Clamp(health, 1f, MoveConsts.MaxHealth);
        return true;
    }

    /// <summary>Sandbox: whether this player takes damage. Off (the default) is godmode. False for anyone who is not a sandbox player.</summary>
    public bool SetVulnerable(ushort id, bool on)
    {
        if (!_players.TryGetValue(id, out var p) || !p.Sandbox) return false;
        p.Vulnerable = on;
        return true;
    }

    /// <summary>
    /// Sandbox, changed while playing (an admin's /sandbox): godmode and unscored on, or normal play back. A sandbox player has no
    /// team; leaving sandbox in team deathmatch joins the smaller team. False when the player is unknown or already in that state.
    /// </summary>
    public bool SetSandbox(ushort id, bool on)
    {
        if (!_players.TryGetValue(id, out var p) || p.Sandbox == on) return false;
        p.Sandbox = on;
        p.Vulnerable = false;
        DropSquad(p);
        if (IsTeamMode) p.Team = on ? Teams.None : SmallerTeam();
        Emit(new ScoreboardChanged());
        return true;
    }

    public void SetPosition(ushort id, Vector3 pos) { if (_players.TryGetValue(id, out var p)) p.Position = pos; }

    public void AddPlayer(ushort id, string name, bool sandbox = false, bool bot = false)
    {
        if (_players.TryGetValue(id, out var old)) DropSquad(old);
        _players[id] = new MatchPlayer { Id = id, Name = name, Sandbox = sandbox, Bot = bot };
        if (IsTeamMode && !sandbox) _players[id].Team = SmallerTeam(); // auto-balance on join
        if (Phase == MatchPhase.Waiting) { StartIfReady(); }
        else if (Phase == MatchPhase.Running) Spawn(_players[id]);
        // while ending the newcomer waits for the restart
        Emit(new ScoreboardChanged());
    }

    public void RemovePlayer(ushort id)
    {
        if (!_players.TryGetValue(id, out var gone)) return;
        DropSquad(gone);
        _players.Remove(id);
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

    /// <summary>The facts the damage gates ask about one hit on <paramref name="v"/> (null: not in the match).</summary>
    DamageFacts FactsOf(MatchPlayer? v, ushort victim, ushort attacker, float amount, bool ignoreProtection)
    {
        var now = _now();
        var alive = v is { Life: PlayerLife.Alive };
        return new DamageFacts(Phase == MatchPhase.Running, amount, alive,
            Held: alive && v!.Held,
            Godmode: alive && v!.Sandbox && !v.Vulnerable,
            FriendlyFireBlocked: alive && !_cfg.FriendlyFire && AreFriends(victim, attacker), // teammates cannot hurt each other (self damage is not friendly fire)
            SpawnProtected: alive && now < v!.ProtectedUntil,
            Invulnerable: alive && now < v!.InvulnerableUntil, // just saved by a blessing
            IgnoreProtection: ignoreProtection);
    }

    /// <summary>
    /// Damage from <paramref name="attacker"/> (the victim itself or an unknown id for suicides and the world).
    /// Ignored by the first <see cref="DamageGate"/> that applies (outside a running round, on dead players, a held or godmode victim, a teammate, spawn protection
    /// and a blessing's invulnerability unless <paramref name="ignoreProtection"/>, used for falling out of the world). Death scores the attacker +1 unless it is a suicide.
    /// </summary>
    public DamageResult ApplyDamage(ushort victim, ushort attacker, byte weapon, float amount, bool ignoreProtection = false, KillCause cause = KillCause.Standard)
    {
        if (CitizenRules.IsHitId(victim)) return Phase == MatchPhase.Running && Citizens != null ? Citizens.Hit(victim, attacker, amount) : DamageResult.Ignored;
        if (PetRules.IsHitId(victim)) return Phase == MatchPhase.Running && Pets != null ? Pets.Hit(victim, attacker, amount) : DamageResult.Ignored;
        if (WildlifeRules.IsHitId(victim)) return Phase == MatchPhase.Running && Wildlife != null ? Wildlife.Hit(victim, attacker, amount) : DamageResult.Ignored;
        if (RaidVanRules.IsHitId(victim)) return Phase == MatchPhase.Running && Vans != null ? Vans.Hit(victim, attacker, weapon, amount, cause) : DamageResult.Ignored;
        if (GangRules.IsHitId(victim)) return Phase == MatchPhase.Running && Gang != null ? Gang.Hit(victim, attacker, weapon, amount, cause) : DamageResult.Ignored;
        if (TargetDummies.IsDummyId(victim)) return ApplyTargetDamage(victim, attacker, weapon, amount, cause);
        _players.TryGetValue(victim, out var found);
        if (DamageGates.FirstBlock(FactsOf(found, victim, attacker, amount, ignoreProtection)) != null) return DamageResult.Ignored; // the ladder and its order: DamageGates
        var v = found!; // a gate lets only a living victim through
        v.Health -= amount;
        if (v.Health > 0) return DamageResult.Damaged;
        if (!ignoreProtection && SaveFromDeath?.Invoke(victim) == true)
        {
            v.Health = MoveConsts.MaxHealth;
            v.InvulnerableUntil = _now() + BlessingRules.InvulnerableS;
            return DamageResult.Saved;
        }

        Die(v, victim, attacker, weapon, cause);
        return DamageResult.Killed;
    }

    /// <summary>The lethal part of a hit on a living player: health 0, the death counted, the respawn timer, the credit, the events, the score goal. Shared by <see cref="ApplyDamage"/> and <see cref="ForceDeath"/>.</summary>
    void Die(MatchPlayer v, ushort victim, ushort attacker, byte weapon, KillCause cause)
    {
        v.Health = 0; v.SetLife(PlayerLife.Dead); v.Deaths++;
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
    }

    /// <summary>
    /// The player's own Respawn (#284): whether <see cref="ForceDeath"/> would take effect now. A round must be running and the player alive and not
    /// held (the demon's pounce, the Bigfoot's grip). No damage gate applies: godmode and spawn protection do not stop it.
    /// </summary>
    public bool CanForceDeath(ushort id) => Phase == MatchPhase.Running && _players.TryGetValue(id, out var p) && p.Life == PlayerLife.Alive && !p.Held;

    /// <summary>
    /// Kills a living player by their own choice, not by damage (so no gate blocks it), as a suicide: death +1, no kill or team point, the kill feed
    /// names only the victim, and the normal respawn delay. False when <see cref="CanForceDeath"/> says no.
    /// </summary>
    public bool ForceDeath(ushort id)
    {
        if (!CanForceDeath(id)) return false;
        Die(_players[id], id, id, SpecialWeapons.Redeploy, KillCause.Standard);
        return true;
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

    /// <summary>
    /// An admin's restart: a fresh round now, whatever the phase, optionally in another <see cref="MatchMode"/>. Scores, lives and world entities
    /// are reset like at any round start. A mode change regroups everyone: free-for-all drops the teams, team deathmatch deals the
    /// non-sandbox players alternately into A and B. With fewer than <see cref="MatchConfig.MinPlayers"/> players the match waits, in the new mode.
    /// </summary>
    public void Restart(MatchMode? mode = null)
    {
        if (mode is { } m && m != Mode)
        {
            Mode = m;
            ClearSquads();
            foreach (var p in _players.Values) { p.Team = Teams.None; } // a mode change regroups everyone
            if (IsTeamMode) foreach (var p in _players.Values.Where(p => !p.Sandbox)) p.Team = SmallerTeam();
        }
        if (_players.Count >= _cfg.MinPlayers && _players.Count > 0) StartRound();
        else ToWaiting();
        Emit(new ScoreboardChanged());
    }

    void StartIfReady() { if (_players.Count >= _cfg.MinPlayers && _players.Count > 0) { StartRound(); } }

    void StartRound()
    {
        Array.Clear(_teamScore); WinningTeam = Teams.None;
        foreach (var p in _players.Values) { p.Kills = 0; p.Deaths = 0; p.SetLife(PlayerLife.Unspawned); }
        if (IsTeamMode) Rebalance(quiet: true); // after the reset: everyone is unspawned, so a move here needs no respawn
        Entities.Clear();
        Targets.ResetAll();
        Winners = Array.Empty<ushort>();
        _roundStart = _now();
        _phase.Enter(MatchPhase.Running);
        Emit(new PhaseChanged(Phase));
        foreach (var p in _players.Values.ToList()) Spawn(p);
    }

    void ToWaiting()
    {
        Array.Clear(_teamScore); WinningTeam = Teams.None;
        foreach (var p in _players.Values) { p.Kills = 0; p.Deaths = 0; p.SetLife(PlayerLife.Unspawned); }
        Winners = Array.Empty<ushort>();
        Entities.Clear(); // nothing of the round outlives it, as at a round start
        _phase.Enter(MatchPhase.Waiting);
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
        _phase.Enter(MatchPhase.Ending);
        _endsAt = _now() + _cfg.EndScreenTime;
        Emit(new PhaseChanged(Phase));
    }

    void Spawn(MatchPlayer p)
    {
        if (Benched(p)) return; // out of the round until an admin puts them on a team (#312)
        // team deathmatch: only enemies count, so a team may spawn together and never next to the other side
        var others = _players.Values.Where(o => o.Id != p.Id && o.Life == PlayerLife.Alive && (!IsTeamMode || p.Team == Teams.None || o.Team != p.Team)).Select(o => o.Position);
        p.Position = SpawnChooser.Choose(_spawns, others, _cfg.MinSpawnDistance, _rng);
        p.SetLife(PlayerLife.Alive);
        p.Health = MoveConsts.MaxHealth;
        p.InvulnerableUntil = 0; p.Held = false;
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
        // plain loops over lists kept between calls: a spawn asks every candidate about every living player (700 x 64 on the village map), and a round start asks for everyone at once
        var l = t_living ??= new List<Vector3>(); l.Clear(); l.AddRange(living);
        var nearest = t_nearest ??= new List<float>(); nearest.Clear();
        var ok = t_ok ??= new List<int>(); ok.Clear();
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var d2 = float.PositiveInfinity;
            foreach (var o in l) d2 = MathF.Min(d2, Vector3.DistanceSquared(c, o));
            var d = MathF.Sqrt(d2); // the distance to the nearest: the root of the least square is the least root
            nearest.Add(d);
            if (d >= minDistance) ok.Add(i);
        }
        if (ok.Count > 0) return candidates[ok[rng.Next(ok.Count)]];
        var best = 0;
        for (var i = 1; i < nearest.Count; i++) if (nearest[i] > nearest[best]) best = i; // the first of the farthest, as a stable sort gives
        return candidates[best];
    }

    [ThreadStatic] static List<Vector3>? t_living;
    [ThreadStatic] static List<float>? t_nearest;
    [ThreadStatic] static List<int>? t_ok;
}
