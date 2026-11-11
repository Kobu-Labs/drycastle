using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class LoadoutEditorTests
{
    static readonly string PerksPath = Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/perks.json");
    static readonly string WeaponsPath = Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json");
    static readonly PerksFile Perks = PerksFile.Load(PerksPath);
    static readonly WeaponsFile Weapons = WeaponsFile.Load(WeaponsPath);

    [Fact]
    public void PrimaryOptionsLockAboveTheLevelAndUnlockOwnedRows()
    {
        var sniper = Weapons.Weapons.First(x => x.Slot == WeaponSlot.Primary && x.UnlockLevel > 0);
        var locked = LoadoutEditor.Options(LoadoutPicker.Primary, Weapons, Perks, KillstreakIds.Placeholder, 0, Array.Empty<Unlock>());
        Assert.False(locked.First(o => o.Id == sniper.Id).Unlocked);
        var owned = LoadoutEditor.Options(LoadoutPicker.Primary, Weapons, Perks, KillstreakIds.Placeholder, 0,
            new[] { new Unlock("weapon", sniper.Id) });
        Assert.True(owned.First(o => o.Id == sniper.Id).Unlocked);
        Assert.All(locked, o => Assert.Equal(WeaponSlot.Primary, Weapons.Find(o.Id)!.Slot));
    }

    [Fact]
    public void SecondaryOptionsAreOnlyPistols()
    {
        var options = LoadoutEditor.Options(LoadoutPicker.Secondary, Weapons, Perks, KillstreakIds.Placeholder, 99, Array.Empty<Unlock>());
        Assert.NotEmpty(options);
        Assert.All(options, o => Assert.Equal(WeaponSlot.Secondary, Weapons.Find(o.Id)!.Slot));
    }

    [Fact]
    public void PerkAndStreakPickersStartWithNone()
    {
        var perk = LoadoutEditor.Options(LoadoutPicker.Perk, Weapons, Perks, KillstreakIds.Placeholder, 1, Array.Empty<Unlock>());
        var streak = LoadoutEditor.Options(LoadoutPicker.Streak1, Weapons, Perks, KillstreakIds.Placeholder, 1, Array.Empty<Unlock>());
        Assert.Equal("", perk[0].Id);
        Assert.Equal("", streak[0].Id);
        Assert.True(perk[0].Unlocked && streak[0].Unlocked);
    }

    [Fact]
    public void PerkLockUsesTheLevelAndOwnedRows()
    {
        var high = Perks.Perks.First(p => p.UnlockLevel > 1);
        var before = LoadoutEditor.Options(LoadoutPicker.Perk, Weapons, Perks, KillstreakIds.Placeholder, 1, Array.Empty<Unlock>());
        Assert.False(before.First(o => o.Id == high.Id).Unlocked);
        var after = LoadoutEditor.Options(LoadoutPicker.Perk, Weapons, Perks, KillstreakIds.Placeholder, high.UnlockLevel, Array.Empty<Unlock>());
        Assert.True(after.First(o => o.Id == high.Id).Unlocked);
        var owned = LoadoutEditor.Options(LoadoutPicker.Perk, Weapons, Perks, KillstreakIds.Placeholder, 1,
            new[] { new Unlock("perk", high.Id) });
        Assert.True(owned.First(o => o.Id == high.Id).Unlocked);
    }

    [Fact]
    public void StreakOptionsCoverEveryPlaceholderIdOnce()
    {
        var streaks = LoadoutEditor.Options(LoadoutPicker.Streak2, Weapons, Perks, KillstreakIds.Placeholder, 0, Array.Empty<Unlock>())
            .Where(o => o.Id != "").Select(o => o.Id).ToList();
        Assert.Equal(KillstreakIds.Placeholder.Count, streaks.Count);
        Assert.Equal(streaks.Count, streaks.Distinct().Count());
    }

    [Fact]
    public void WeaponStatsShareIsOneForTheBestWeaponInTheSlot()
    {
        foreach (var slot in new[] { WeaponSlot.Primary, WeaponSlot.Secondary })
        {
            var all = Weapons.Weapons.Where(x => x.Slot == slot).ToList();
            foreach (var stat in LoadoutEditor.StatKeys)
            {
                var shares = all.Select(x => LoadoutEditor.Stats(x, Weapons).First(s => s.Key == stat).Share).ToList();
                Assert.All(shares, s => Assert.InRange(s, 0f, 1f));
                Assert.Equal(1f, shares.Max(), 3);
            }
        }
    }

    [Fact]
    public void FireRateIsShotsPerSecond()
    {
        var pistol = Weapons.Find("pistol")!;
        var rate = LoadoutEditor.Stats(pistol, Weapons).First(s => s.Key == LoadoutEditor.StatRate).Value;
        Assert.Equal(1f / pistol.FireInterval, rate, 3);
    }

    [Fact]
    public void ClampTabKeepsTheIndexInsideTheFiveSlots()
    {
        Assert.Equal(0, LoadoutEditor.ClampTab(-3));
        Assert.Equal(Loadout.MaxSlot, LoadoutEditor.ClampTab(9));
        Assert.Equal(2, LoadoutEditor.ClampTab(2));
    }
}
