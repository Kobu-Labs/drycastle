namespace Suchohrad.ShooterCore;

/// <summary>
/// Server-side rules for a player's loadouts (ADR 0003: the server validates every submitted loadout against the data
/// files, a client cannot pick an item the data does not list). Pure functions, so the tests need no server.
/// </summary>
public static class LoadoutRules
{
    /// <summary>Longest item id the wire carries; anything longer cannot be a data-file id.</summary>
    public const int MaxItemIdBytes = 48;

    /// <summary>
    /// The loadout a guest (no persistence) gets in every slot: the first primary and the first secondary weapon in
    /// data-file order that need no unlock. Perk and killstreaks stay empty.
    /// </summary>
    public static Loadout DefaultLoadout(int slot, WeaponsFile weapons)
    {
        var primary = weapons.Weapons.FirstOrDefault(w => w.Slot == WeaponSlot.Primary && w.UnlockLevel == 0)?.Id ?? "";
        var secondary = weapons.Weapons.FirstOrDefault(w => w.Slot == WeaponSlot.Secondary && w.UnlockLevel == 0)?.Id ?? "";
        return new Loadout(slot, primary, secondary, "", "", "", "");
    }

    /// <summary>Null when the slot number is one of the five; otherwise the reason.</summary>
    public static string? ValidateSlot(int slot) =>
        slot is >= 0 and <= Loadout.MaxSlot ? null : $"loadout slot must be 0 to {Loadout.MaxSlot}, got {slot}";

    /// <summary>
    /// Checks one loadout for saving. Primary and secondary are required, perk and streaks may be empty. A weapon is
    /// usable when the player's level reaches its unlock level or the player owns it as an unlock. Perk and streak ids
    /// must be in the given sets (the perk and killstreak data files; empty until those tickets land, so only empty
    /// perk and streak slots pass). Returns null when valid, otherwise a reason the client can show.
    /// </summary>
    public static string? ValidateLoadout(Loadout l, WeaponsFile weapons, int level, IEnumerable<Unlock> unlocks,
        IReadOnlySet<string> perks, IReadOnlySet<string> streaks)
    {
        if (ValidateSlot(l.Slot) is { } slotErr) return slotErr;
        var owned = unlocks.Where(u => u.Kind == "weapon").Select(u => u.ItemId).ToHashSet();

        if (Weapon(l.PrimaryWeapon, WeaponSlot.Primary, "primary", weapons, level, owned) is { } p) return p;
        if (Weapon(l.SecondaryWeapon, WeaponSlot.Secondary, "secondary", weapons, level, owned) is { } s) return s;

        if (l.Perk != "" && (l.Perk.Length > MaxItemIdBytes || !perks.Contains(l.Perk))) return $"unknown perk \"{Short(l.Perk)}\"";
        foreach (var (id, n) in new[] { (l.Streak1, 1), (l.Streak2, 2), (l.Streak3, 3) })
            if (id != "" && (id.Length > MaxItemIdBytes || !streaks.Contains(id))) return $"unknown killstreak {n} \"{Short(id)}\"";
        return null;
    }

    static string? Weapon(string id, WeaponSlot slot, string label, WeaponsFile weapons, int level, HashSet<string> owned)
    {
        if (id == "") return $"the {label} weapon is required";
        if (id.Length > MaxItemIdBytes) return $"unknown {label} weapon \"{Short(id)}\"";
        var w = weapons.Find(id);
        if (w == null || w.Slot != slot) return $"unknown {label} weapon \"{Short(id)}\"";
        if (level < w.UnlockLevel && !owned.Contains(id)) return $"{w.Id} unlocks at level {w.UnlockLevel}";
        return null;
    }

    static string Short(string s) => s.Length <= 32 ? s : s[..32] + "...";
}
