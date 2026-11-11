using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Progression on the server (ticket #117): the active loadout applied at each spawn (perk and hotbar), XP from match
/// events written through the progression store, the level-up and XP summary events, and the lifetime stats. Guests earn
/// XP in memory only. Everything here runs on the tick.
/// </summary>
public partial class NetServer
{
    Dictionary<string, int>? _perkLevels;
    IReadOnlyDictionary<string, int> PerkLevels => _perkLevels ??= Perks.Perks.ToDictionary(p => p.Id, p => p.UnlockLevel);

    /// <summary>The active loadout becomes the player's perk and hotbar. Runs at join and at every spawn, so a change made while alive applies from the next spawn.</summary>
    void ApplyLoadout(Client c)
    {
        var p = c.Profile!;
        var l = p.Loadouts[p.Active];
        c.Perks = PerkModifiers.Of(Perks.Find(l.Perk));
        // Dev servers (smoke arenas, --debug-cmds) keep every weapon on the hotbar: the scripted bots and --fx-demo pick
        // by weapons.json slot, e.g. the bazooka at slot 3 (#133). A public server (no debug commands) gets the loadout hotbar.
        c.W = new PlayerWeapons(Weapons, c.Perks, ArenaMode || DebugCommands ? LoadoutHotbar.All(Weapons) : LoadoutHotbar.Build(Weapons, l));
        StreakArm(c, l);
    }

    void RoundStarted()
    {
        StreakRoundReset();
        _xp.Reset();
        foreach (var c in _clients.Values) c.RoundXp.Clear();
    }

    /// <summary>End of round: the win and top-three awards, one match counted per joined player, and each player's XP summary.</summary>
    void RoundEnded()
    {
        StreakRoundReset();
        AwardXp(_xp.OnMatchEnd(_match.Winners, _match.Ranking().Select(p => p.Id).ToList()));
        foreach (var c in _clients.Values.Where(c => c.Joined))
        {
            var p = c.Profile!;
            if (p.Persist) _ = Progression!.Stats.RecordMatchAsync(p.DbId);
            var summary = new XpSummaryInfo(p.Xp, p.Level, c.RoundXp.Select(g => new XpLine(g.Reason, g.Amount)).ToList());
            Send(c, new EventMsg((byte)EventKind.XpSummary, ProgressionEvents.EncodeXpSummary(summary)));
        }
    }

    /// <summary>Adds each grant to its player: the XP total (written through the store for a persistent profile), the round summary, and a reliable level-up event when the level rises.</summary>
    void AwardXp(IEnumerable<XpGrant> grants)
    {
        foreach (var g in grants)
        {
            if (!_byId.TryGetValue(g.Player, out var c) || c.Profile is not { } p) continue;
            p.Xp += g.Amount;
            c.RoundXp.Add(g);
            if (p.Persist) _ = Progression!.Profiles.AddXpAsync(p.DbId, g.Amount);
            var level = Levels.Curve.LevelForXp(p.Xp);
            if (level <= p.Level) continue;
            p.Level = level;
            Send(c, new EventMsg((byte)EventKind.LevelUp, ProgressionEvents.EncodeLevelUp(new LevelUpInfo(level, p.Xp))));
            GD.Print($"server: {c.Name} reached level {level}");
        }
    }

    /// <summary>Kill and roadkill counters. Suicides and kills by no player are not counted.</summary>
    void RecordKill(PlayerKilled k)
    {
        if (k.Killer == k.Victim || Progression == null || !_byId.TryGetValue(k.Killer, out var c) || !c.Profile!.Persist) return;
        _ = Progression.Stats.RecordKillAsync(c.Profile.DbId, WeaponKey(k.Weapon), k.Cause == KillCause.Roadkill);
    }

    void RecordDeath(ushort victim)
    {
        if (Progression != null && _byId.TryGetValue(victim, out var c) && c.Profile!.Persist) _ = Progression.Stats.RecordDeathAsync(c.Profile.DbId);
    }

    /// <summary>The stats key of a weapon byte: its weapons.json id, or a name for the special causes (vehicle, debug, void, fall).</summary>
    string WeaponKey(byte weapon) =>
        weapon < Weapons.Weapons.Count ? Weapons.Weapons[weapon].Id : weapon == SpecialWeapons.Vehicle ? "vehicle" : weapon == SpecialWeapons.Moab ? MoabStreak.OwnedWeapon : weapon == SpecialWeapons.Missile ? AttackMissileStreak.StreakId : weapon == SpecialWeapons.Turret ? TurretStreak.WeaponId : weapon == SpecialWeapons.AttackHelicopter ? AttackHelicopterStreak.StreakId : weapon == SpecialWeapons.Ac130 ? Ac130Streak.StreakId : weapon == SpecialWeapons.Helper ? HelicopterHelperStreak.WeaponId : "other";
}
