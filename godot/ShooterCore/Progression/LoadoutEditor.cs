namespace Drycastle.ShooterCore;

/// <summary>The six pickers of one loadout, in the order the editor shows them.</summary>
public enum LoadoutPicker { Primary, Secondary, Perk, Streak1, Streak2, Streak3 }

/// <summary>
/// One row of a picker. <see cref="Id"/> is the data-file id ("" for "none" on perk and streaks); <see cref="Group"/> is a
/// category key the screen turns into a heading; <see cref="Unlocked"/> false rows are shown but cannot be picked.
/// </summary>
public sealed record PickerOption(string Id, string Group, int UnlockLevel, bool Unlocked);

/// <summary>One stat of a weapon: its raw value and its share of the largest value in the same weapon slot (0..1, for the bar).</summary>
public readonly record struct WeaponStat(string Key, float Value, float Share);

/// <summary>
/// Godot-free rules behind the loadout editor (ticket #118): what each picker lists and whether a row is locked, and the
/// stat bars of a weapon. Locking follows <see cref="LoadoutRules"/> (a level reached or an owned unlock row).
/// </summary>
public static class LoadoutEditor
{
    public const string StatDamage = "damage", StatRate = "rate", StatMagazine = "magazine", StatMobility = "mobility";
    public const string StatRange = "range", StatRecoil = "recoil";
    public static readonly string[] StatKeys = { StatDamage, StatRate, StatMagazine, StatMobility };
    public static readonly string[] CompareKeys = { StatDamage, StatRate, StatRange, StatRecoil, StatMobility };

    /// <summary>The rows of one picker: "none" first (perk and streaks), then grouped by category, each group by unlock level then id.</summary>
    public static IReadOnlyList<PickerOption> Options(LoadoutPicker pick, WeaponsFile weapons, PerksFile perks,
        IReadOnlyDictionary<string, int> streakLevels, int level, IEnumerable<Unlock> unlocks)
    {
        var owned = unlocks.ToList();
        var result = new List<PickerOption>();
        switch (pick)
        {
            case LoadoutPicker.Primary:
            case LoadoutPicker.Secondary:
                var slot = pick == LoadoutPicker.Primary ? WeaponSlot.Primary : WeaponSlot.Secondary;
                foreach (var w in weapons.Weapons.Where(w => w.Slot == slot).OrderBy(w => w.Category).ThenBy(w => w.UnlockLevel).ThenBy(w => w.Id))
                    result.Add(new PickerOption(w.Id, w.Category.ToString(), w.UnlockLevel, Unlocked(level, w.UnlockLevel, owned, "weapon", w.Id)));
                break;
            case LoadoutPicker.Perk:
                result.Add(new PickerOption("", "", 0, true));
                foreach (var p in perks.Perks.OrderBy(p => p.UnlockLevel).ThenBy(p => p.Id))
                    result.Add(new PickerOption(p.Id, "Perk", p.UnlockLevel, Unlocked(level, p.UnlockLevel, owned, "perk", p.Id)));
                break;
            default:
                result.Add(new PickerOption("", "", 0, true));
                foreach (var (id, need) in streakLevels.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key))
                    result.Add(new PickerOption(id, "Killstreak", need, Unlocked(level, need, owned, "streak", id)));
                break;
        }
        return result;
    }

    /// <summary>Same rule as <see cref="LoadoutRules"/>: the level reached, or an owned unlock row.</summary>
    public static bool Unlocked(int level, int unlockLevel, IEnumerable<Unlock> owned, string kind, string id) =>
        level >= unlockLevel || owned.Any(u => u.Kind == kind && u.ItemId == id);

    /// <summary>
    /// The four stat bars of a weapon. Each value is compared with the largest value of the same slot, so the best weapon
    /// in a slot fills its bar. Fire rate is shots per second; mobility is the run-speed multiplier.
    /// </summary>
    public static IReadOnlyList<WeaponStat> Stats(WeaponDef w, WeaponsFile weapons)
    {
        var same = weapons.Weapons.Where(x => x.Slot == w.Slot).ToList();
        return new[]
        {
            Stat(StatDamage, w.Damage, same.Max(x => x.Damage)),
            Stat(StatRate, RateOf(w), same.Max(RateOf)),
            Stat(StatMagazine, w.MagazineSize, same.Max(x => x.MagazineSize)),
            Stat(StatMobility, w.MoveSpeedMultiplier, same.Max(x => x.MoveSpeedMultiplier)),
        };
    }

    /// <summary>
    /// The five bars of the picker's compare panel (ticket #147): damage, fire rate, range, recoil and mobility. Each share is
    /// relative to the best weapon of the same slot, so a fuller bar is better everywhere. Recoil is inverted: the steadiest
    /// weapon in the slot (the least vertical recoil per shot) fills its bar, and its value is that recoil per shot.
    /// </summary>
    public static IReadOnlyList<WeaponStat> CompareStats(WeaponDef w, WeaponsFile weapons)
    {
        var same = weapons.Weapons.Where(x => x.Slot == w.Slot).ToList();
        var steadiest = same.Min(x => x.Recoil.VerticalPerShot);
        var recoil = w.Recoil.VerticalPerShot;
        var recoilShare = recoil > 0 ? Math.Clamp(steadiest / recoil, 0f, 1f) : 1f;
        return new[]
        {
            Stat(StatDamage, w.Damage, same.Max(x => x.Damage)),
            Stat(StatRate, RateOf(w), same.Max(RateOf)),
            Stat(StatRange, w.Range, same.Max(x => x.Range)),
            new WeaponStat(StatRecoil, recoil, recoilShare),
            Stat(StatMobility, w.MoveSpeedMultiplier, same.Max(x => x.MoveSpeedMultiplier)),
        };
    }

    static float RateOf(WeaponDef w) => w.FireInterval > 0 ? 1f / w.FireInterval : 0f;

    static WeaponStat Stat(string key, float value, float max) =>
        new(key, value, max > 0 ? Math.Clamp(value / max, 0f, 1f) : 0f);

    /// <summary>Clamps a remembered tab index to the five slots.</summary>
    public static int ClampTab(int tab) => Math.Clamp(tab, 0, Loadout.MaxSlot);
}
