using Godot;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter.Net.Server.Careers;

/// <summary>
/// Progression on the server (ticket #117): the active loadout applied at each spawn (perk and hotbar), XP from match events written through the progression store, the level-up and
/// XP summary events, and the lifetime stats. Guests earn XP in memory only. A bot and a sandbox player score nothing (<see cref="Scores"/>). Everything here runs on the tick.
/// </summary>
internal sealed class ProgressionServer
{
    readonly ServerContext _ctx;
    readonly IProgressionStore? _store;
    readonly LevelsFile _levels;
    readonly WeaponsFile _weapons;
    readonly PerksFile _perks;
    readonly bool _fullHotbar;
    readonly Action<ServerClient, Loadout> _arm;
    readonly MatchXp _xp;

    /// <param name="store">The progression store; null = every player is a guest.</param>
    /// <param name="fullHotbar">Dev servers (smoke arenas, --debug-cmds) keep every weapon on the hotbar: the scripted bots and --fx-demo pick by weapons.json slot, e.g. the rpg at slot 3 (#133).</param>
    /// <param name="arm">Hands the loadout's killstreaks to the streak rules.</param>
    public ProgressionServer(ServerContext ctx, IProgressionStore? store, LevelsFile levels, WeaponsFile weapons, PerksFile perks, bool fullHotbar, Action<ServerClient, Loadout> arm)
    {
        _ctx = ctx; _store = store; _levels = levels; _weapons = weapons; _perks = perks; _fullHotbar = fullHotbar; _arm = arm;
        _xp = new MatchXp(levels.Awards);
    }

    /// <summary>The active loadout becomes the player's perk and hotbar. Runs at join and at every spawn, so a change made while alive applies from the next spawn.</summary>
    public void ApplyLoadout(ServerClient c)
    {
        var p = c.Profile!;
        var l = p.Loadouts[p.Active];
        c.Perks = PerkModifiers.Of(_perks.Find(l.Perk));
        // A public server (no debug commands) gets the loadout hotbar.
        c.W = new PlayerWeapons(_weapons, c.Perks, _fullHotbar || c.Sandbox ? LoadoutHotbar.All(_weapons) : LoadoutHotbar.Build(_weapons, l))
        {
            Unlimited = c.Sandbox, // sandbox (#156): unlimited ammo, the whole hotbar
        };
        _arm(c, l);
    }

    /// <summary>Scores (XP, stats, the score) count for every player except a sandbox one and a bot (#214). Unknown ids count as normal.</summary>
    public bool Scores(ushort id) => _ctx.Match.Get(id) is not { Sandbox: true } && !(_ctx.Players.TryGetValue(id, out var c) && c.IsBot);

    /// <summary>A new round: the XP book starts over.</summary>
    public void ResetXp() => _xp.Reset();

    /// <summary>A new round: nobody's summary carries the last round's lines.</summary>
    public void ClearSummaries()
    {
        foreach (var c in _ctx.Connections) c.RoundXp.Clear();
    }

    /// <summary>End of round: the win and top-three awards, one match counted per joined player, and each player's XP summary.</summary>
    public void RoundEnded()
    {
        var match = _ctx.Match;
        Award(_xp.OnMatchEnd(match.Winners, match.Ranking().Where(p => !p.Sandbox).Select(p => p.Id).ToList()));
        foreach (var c in _ctx.Connections.Where(c => c.Joined)) // human players: a bot has no stats and no summary
        {
            var p = c.Profile!;
            if (p.Persist) _ = _store!.Stats.RecordMatchAsync(p.DbId);
            var summary = new XpSummaryInfo(p.Xp, p.Level, c.RoundXp.Select(g => new XpLine(g.Reason, g.Amount)).ToList());
            _ctx.Send(c, new EventMsg((byte)EventKind.XpSummary, ProgressionEvents.EncodeXpSummary(summary)));
        }
    }

    /// <summary>Adds each grant to its player: the XP total (written through the store for a persistent profile), the round summary, and a reliable level-up event when the level rises.</summary>
    public void Award(IEnumerable<XpGrant> grants)
    {
        foreach (var g in grants)
        {
            if (!_ctx.Players.TryGetValue(g.Player, out var c) || c.IsBot || c.Profile is not { } p) continue; // a bot's share (a round placing) goes to nobody
            p.Xp += g.Amount;
            c.RoundXp.Add(g);
            if (p.Persist) _ = _store!.Profiles.AddXpAsync(p.DbId, g.Amount);
            var level = _levels.Curve.LevelForXp(p.Xp);
            if (level <= p.Level) continue;
            p.Level = level;
            _ctx.Send(c, new EventMsg((byte)EventKind.LevelUp, ProgressionEvents.EncodeLevelUp(new LevelUpInfo(level, p.Xp))));
            GD.Print($"server: {c.Name} reached level {level}");
        }
    }

    /// <summary>A kill that scores: the killer's XP (kill, streak, assists).</summary>
    public void Kill(KillFacts facts) => Award(_xp.OnKill(facts, _ctx.Now));

    /// <summary>A hit on a scoring victim by a scoring attacker: an assist if the victim dies within the window.</summary>
    public void NoteDamage(ushort victim, ushort attacker)
    {
        if (Scores(attacker) && Scores(victim)) _xp.NoteDamage(victim, attacker, _ctx.Now);
    }

    /// <summary>Kill and roadkill counters. Suicides and kills by no player are not counted.</summary>
    public void RecordKill(PlayerKilled k)
    {
        if (k.Killer == k.Victim || !Scores(k.Victim) || _store == null || !_ctx.Players.TryGetValue(k.Killer, out var c) || !c.Profile!.Persist || c.Sandbox) return;
        _ = _store.Stats.RecordKillAsync(c.Profile.DbId, WeaponKey(k.Weapon), k.Cause == KillCause.Roadkill);
    }

    public void RecordDeath(ushort victim)
    {
        // a sandbox player who switched damage on can die; like the rest of sandbox mode it writes nothing
        if (_store != null && _ctx.Players.TryGetValue(victim, out var c) && c.Profile!.Persist && !c.Sandbox) _ = _store.Stats.RecordDeathAsync(c.Profile.DbId);
    }

    /// <summary>The stats key of a weapon byte: its weapons.json id, or a name for the special causes (vehicle, debug, void, fall).</summary>
    public string WeaponKey(byte weapon) =>
        weapon < _weapons.Weapons.Count ? _weapons.Weapons[weapon].Id : weapon == SpecialWeapons.Vehicle ? "vehicle" : weapon == SpecialWeapons.VehicleGun ? "vehicle_gun" : weapon == SpecialWeapons.Flak ? "flak" : weapon == SpecialWeapons.Moab ? MoabStreak.OwnedWeapon : weapon == SpecialWeapons.Scud ? "scud" : weapon == SpecialWeapons.Missile ? AttackMissileStreak.StreakId : weapon == SpecialWeapons.Turret ? TurretStreak.WeaponId : weapon == SpecialWeapons.AttackHelicopter ? AttackHelicopterStreak.StreakId : weapon == SpecialWeapons.Ac130 ? Ac130Streak.StreakId : weapon == SpecialWeapons.Helper ? HelicopterHelperStreak.WeaponId : "other";
}
