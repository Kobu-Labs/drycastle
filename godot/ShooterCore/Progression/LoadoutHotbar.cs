namespace Suchohrad.ShooterCore;

/// <summary>
/// The hotbar a loadout gives a player: primary, secondary and knife, in that order (keys 1-3 and the next-weapon cycle).
/// Returns weapons.json indexes, which is what <see cref="PlayerWeapons"/> takes. A missing or unknown primary or secondary
/// falls back to the first unlock-0 weapon of that slot, so a player always has a hotbar.
/// </summary>
public static class LoadoutHotbar
{
    public static int[] Build(WeaponsFile weapons, Loadout? loadout)
    {
        var primary = Index(weapons, loadout?.PrimaryWeapon, WeaponSlot.Primary);
        if (primary < 0) primary = weapons.Weapons.FindIndex(w => w.Slot == WeaponSlot.Primary && w.UnlockLevel == 0);
        var secondary = Index(weapons, loadout?.SecondaryWeapon, WeaponSlot.Secondary);
        if (secondary < 0) secondary = weapons.Weapons.FindIndex(w => w.Slot == WeaponSlot.Secondary && w.UnlockLevel == 0);
        var knife = weapons.Weapons.FindIndex(w => w.Category == WeaponCategory.Melee);
        return new[] { primary, secondary, knife }.Where(i => i >= 0).ToArray();
    }

    /// <summary>
    /// Every weapon in weapons.json order. Only for the scripted smoke arenas (#133): the bots pick weapons by their
    /// weapons.json slot (the bazooka is slot 3), which a player's loadout hotbar no longer offers. Never used for players.
    /// </summary>
    public static int[] All(WeaponsFile weapons) => Enumerable.Range(0, weapons.Weapons.Count).ToArray();

    static int Index(WeaponsFile weapons, string? id, WeaponSlot slot) =>
        string.IsNullOrEmpty(id) ? -1 : weapons.Weapons.FindIndex(w => w.Id == id && w.Slot == slot);
}
