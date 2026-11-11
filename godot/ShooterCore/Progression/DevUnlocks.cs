namespace Suchohrad.ShooterCore;

/// <summary>
/// Dev mode "everything unlocked" (issue #149, <c>--unlock-all</c> or <c>SUCHOHRAD_UNLOCK_ALL=1</c>). Off by default, so an
/// exported release server gates every item as before. When on, every weapon (not the special ones), perk and killstreak
/// counts as unlocked for loadout validation and for the profile the server reports, the starter loadouts are the showcase
/// kits of <see cref="DevKit"/>, and the profile carries <see cref="Marker"/> so the client can say so on screen. XP and
/// levels still accrue. The fake unlocks are never written to the database: <see cref="Expand"/> builds a new list and the
/// owned rows are left as they were.
/// </summary>
public static class DevUnlocks
{
    public const string EnvVar = "SUCHOHRAD_UNLOCK_ALL";
    public const string MarkerKind = "dev", MarkerId = "unlock_all";

    /// <summary>A row that is only ever sent to clients (never stored, never a data-file item). Loadout rules ignore its kind.</summary>
    public static readonly Unlock Marker = new(MarkerKind, MarkerId);

    /// <summary>The flag, or the environment value "1" or "true" (any case, surrounding spaces ignored). Anything else is off.</summary>
    public static bool Enabled(bool flag, string? env) =>
        flag || env?.Trim().ToLowerInvariant() is "1" or "true";

    /// <summary>True when a profile's unlock list carries the dev marker (the client shows the "all unlocked" indicator).</summary>
    public static bool IsMarked(IEnumerable<Unlock> unlocks) => unlocks.Any(u => u.Kind == MarkerKind && u.ItemId == MarkerId);

    /// <summary>
    /// The unlock rows a dev-mode profile reports: the owned rows, then every non-special weapon, every perk, every killstreak
    /// and the marker, without duplicates. <paramref name="owned"/> is not changed.
    /// </summary>
    public static IReadOnlyList<Unlock> Expand(IEnumerable<Unlock> owned, WeaponsFile weapons, PerksFile perks,
        IReadOnlyDictionary<string, int> streakLevels)
    {
        var all = owned.ToList();
        all.AddRange(weapons.Weapons.Where(w => w.Slot != WeaponSlot.Special).Select(w => new Unlock("weapon", w.Id)));
        all.AddRange(perks.Perks.Select(p => new Unlock("perk", p.Id)));
        all.AddRange(streakLevels.Keys.Select(id => new Unlock("streak", id)));
        all.Add(Marker);
        return all.DistinctBy(u => (u.Kind, u.ItemId)).ToList();
    }

    /// <summary>
    /// The starter loadout of dev mode for one slot (issue #149): five classes that showcase the variety, so each can be tried
    /// at once. Every id is in the shipped data files (checked by the tests).
    /// </summary>
    public static Loadout DevKit(int slot)
    {
        if (slot is < 0 or > Loadout.MaxSlot) throw new ArgumentOutOfRangeException(nameof(slot));
        return slot switch
        {
            // each arm names its own slot number, so the kit always sits in the slot it is for
            // assault rifle, pistol, Fleet Foot, UAV, sentry turret, care package
            0 => new Loadout(0, "ak47", "pistol", "fleet_foot", "uav", "turret", "care_package"),
            // sniper, pistol, Steady Lung, UAV, attack missile, sentry turret
            1 => new Loadout(1, "sniper", "pistol", "steady_lung", "uav", "attack_missile", "turret"),
            // pump shotgun, Glock, Quick Hands, UAV, care package, attack missile
            2 => new Loadout(2, "remington870", "glock17", "quick_hands", "uav", "care_package", "attack_missile"),
            // SMG, Desert Eagle, Swift Swap, UAV, attack helicopter, helicopter helper
            3 => new Loadout(3, "mp5", "deagle", "swift_swap", "uav", "attack_helicopter", "helicopter_helper"),
            // LMG with the big streaks, Hot Streak so they come sooner
            _ => new Loadout(4, "m249", "deagle", "hot_streak", "attack_helicopter", "ac130", "moab"),
        };
    }
}
