namespace Suchohrad.ShooterCore;

/// <summary>
/// Placeholder killstreak registry until killstreaks.json exists (the killstreak framework ticket replaces it). The ids are
/// the killstreak table in mods/shooter/v2/killstreaks.md; each maps to its unlock level. No levels.json entry gives a
/// streak a level yet, so all are 0 (open to every player). The server validates loadout streaks against this list.
/// </summary>
public static class KillstreakIds
{
    /// <summary>The streak every starting kit carries (Level 0 kit: one primary, one pistol, one perk, UAV).</summary>
    public const string StarterStreak = "uav";

    public static readonly IReadOnlyDictionary<string, int> Placeholder = new Dictionary<string, int>
    {
        ["uav"] = 0,
        ["attack_missile"] = 0,
        ["turret"] = 0,
        ["care_package"] = 0,
        ["attack_helicopter"] = 0,
        ["helicopter_helper"] = 0,
        ["ac130"] = 0,
        ["moab"] = 0,
    };
}
