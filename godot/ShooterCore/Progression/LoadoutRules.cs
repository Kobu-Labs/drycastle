namespace Suchohrad.ShooterCore;

/// <summary>
/// Server-side rules for a player's loadouts (ADR 0003: the server validates every submitted loadout against the data
/// files, a client cannot pick an item the data does not list). Pure functions, so the tests need no server.
/// </summary>
public static class LoadoutRules
{
    /// <summary>Longest item id the wire carries; anything longer cannot be a data-file id.</summary>
    public const int MaxItemIdBytes = 48;

    /// <summary>Longest loadout name, in characters, after trimming (ticket #145).</summary>
    public const int MaxNameChars = 24;

    /// <summary>The default name of a slot: "Class 1" .. "Class 5".</summary>
    public static string DefaultName(int slot) => $"Class {slot + 1}";

    /// <summary>
    /// A display name from a hello, made safe (#197): control characters and brackets removed (the scoreboard adds a "[SANDBOX]"
    /// tag, which a name must not fake), trimmed and cut to <see cref="MaxNameChars"/>. Empty when nothing is left.
    /// </summary>
    public static string SanitizePlayerName(string? raw)
    {
        var kept = new string((raw ?? "").Where(ch => !char.IsControl(ch) && ch != '[' && ch != ']').ToArray()).Trim();
        return kept.Length > MaxNameChars ? kept[..MaxNameChars].TrimEnd() : kept;
    }

    /// <summary>
    /// Checks a submitted loadout name. Returns the trimmed name, or null with <paramref name="error"/> set. Profanity is not
    /// filtered (the ticket says so); control characters and an empty name are refused.
    /// </summary>
    public static string? ValidateName(string? raw, out string? error)
    {
        error = null;
        var name = (raw ?? "").Trim();
        if (name.Length == 0) error = "a loadout name cannot be empty";
        else if (name.Length > MaxNameChars) error = $"a loadout name can be at most {MaxNameChars} characters";
        else if (name.Any(char.IsControl)) error = "a loadout name cannot contain control characters";
        return error == null ? name : null;
    }

    /// <summary>
    /// The loadout a new or guest player gets in every slot: the first unlock-0 primary and secondary weapon in data-file
    /// order, the first unlock-0 perk, and the starter killstreak (<see cref="KillstreakIds.StarterStreak"/>).
    /// </summary>
    public static Loadout DefaultLoadout(int slot, WeaponsFile weapons, PerksFile perks)
    {
        var primary = weapons.Weapons.FirstOrDefault(w => w.Slot == WeaponSlot.Primary && w.UnlockLevel == 0)?.Id ?? "";
        var secondary = weapons.Weapons.FirstOrDefault(w => w.Slot == WeaponSlot.Secondary && w.UnlockLevel == 0)?.Id ?? "";
        var perk = perks.Perks.FirstOrDefault(p => p.UnlockLevel == 0)?.Id ?? "";
        return new Loadout(slot, primary, secondary, perk, KillstreakIds.StarterStreak, "", "", DefaultName(slot));
    }

    /// <summary>
    /// The loadout a copy leaves in the destination slot: the source's items (the caller copies the camouflage too), under the
    /// destination's slot number and name. The copy is validated like any save before it is stored.
    /// </summary>
    public static Loadout CopyInto(Loadout source, Loadout destination) =>
        source with { Slot = destination.Slot, Name = destination.Name };

    /// <summary>Null when the slot number is one of the five; otherwise the reason.</summary>
    public static string? ValidateSlot(int slot) =>
        slot is >= 0 and <= Loadout.MaxSlot ? null : $"loadout slot must be 0 to {Loadout.MaxSlot}, got {slot}";

    /// <summary>
    /// Checks one loadout for saving (ADR 0003: every item must be in the data files and unlocked for the player).
    /// <list type="bullet">
    /// <item>Primary and secondary are required, of the right slot, and unlocked (level reached or owned).</item>
    /// <item>Perk and streaks may be empty. A perk must be known and unlocked; so must each streak, and no streak may repeat.</item>
    /// </list>
    /// <paramref name="perkLevels"/> and <paramref name="streakLevels"/> map item id to its unlock level. Returns null when
    /// valid, otherwise a reason the client can show.
    /// </summary>
    public static string? ValidateLoadout(Loadout l, WeaponsFile weapons, int level, IEnumerable<Unlock> unlocks,
        IReadOnlyDictionary<string, int> perkLevels, IReadOnlyDictionary<string, int> streakLevels)
    {
        if (ValidateSlot(l.Slot) is { } slotErr) return slotErr;
        var owned = unlocks.ToList();
        var ownedWeapons = owned.Where(u => u.Kind == "weapon").Select(u => u.ItemId).ToHashSet();

        if (Weapon(l.PrimaryWeapon, WeaponSlot.Primary, "primary", weapons, level, ownedWeapons) is { } p) return p;
        if (Weapon(l.SecondaryWeapon, WeaponSlot.Secondary, "secondary", weapons, level, ownedWeapons) is { } s) return s;

        if (l.Perk != "")
        {
            if (l.Perk.Length > MaxItemIdBytes || !perkLevels.TryGetValue(l.Perk, out var perkLevel))
                return $"unknown perk \"{Short(l.Perk)}\"";
            if (Locked(level, perkLevel, owned, "perk", l.Perk) is { } perkErr) return perkErr;
        }

        var seen = new HashSet<string>();
        foreach (var (id, n) in new[] { (l.Streak1, 1), (l.Streak2, 2), (l.Streak3, 3) })
        {
            if (id == "") continue;
            if (id.Length > MaxItemIdBytes || !streakLevels.TryGetValue(id, out var streakLevel))
                return $"unknown killstreak {n} \"{Short(id)}\"";
            if (!seen.Add(id)) return $"killstreak \"{Short(id)}\" is selected more than once";
            if (Locked(level, streakLevel, owned, "streak", id) is { } streakErr) return streakErr;
        }
        return null;
    }

    /// <summary>A perk or streak is usable at the level it unlocks at, or when the player owns it as an unlock row.</summary>
    static string? Locked(int level, int unlockLevel, IEnumerable<Unlock> owned, string kind, string id) =>
        level >= unlockLevel || owned.Any(u => u.Kind == kind && u.ItemId == id) ? null : $"{id} unlocks at level {unlockLevel}";

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
