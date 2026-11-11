using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Dev mode "everything unlocked" (issue #149): the gate override, the reported rows and the showcase kits.</summary>
public class DevUnlocksTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static IReadOnlyDictionary<string, int> Streaks => Shipped.Killstreaks.UnlockLevels;
    static readonly Profile NewPlayer = new(1, "token", "new", 0, 1, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void ReleaseDefaultIsOff()
    {
        Assert.False(DevUnlocks.Enabled(false, null));
        Assert.False(DevUnlocks.Enabled(false, ""));
        Assert.False(DevUnlocks.Enabled(false, "0"));
        Assert.False(DevUnlocks.Enabled(false, "yes"));
    }

    [Fact]
    public void FlagOrEnvironmentTurnsItOn()
    {
        Assert.True(DevUnlocks.Enabled(true, null));
        Assert.True(DevUnlocks.Enabled(false, "1"));
        Assert.True(DevUnlocks.Enabled(false, " TRUE "));
    }

    [Fact]
    public void LevelGateIsOverriddenOnlyForKnownItems()
    {
        var awm = Shipped.Weapons.Weapons.First(w => w.Id == "awm");
        Assert.True(awm.UnlockLevel > 1);
        Assert.False(Shipped.Levels.IsUnlocked(NewPlayer, UnlockKind.Weapon, "awm"));
        Assert.True(Shipped.Levels.IsUnlocked(NewPlayer, UnlockKind.Weapon, "awm", unlockAll: true));
        Assert.False(Shipped.Levels.IsUnlocked(NewPlayer, UnlockKind.Weapon, "no_such_gun", unlockAll: true));
        Assert.False(Shipped.Levels.IsUnlocked(NewPlayer, UnlockKind.Perk, "no_such_perk", unlockAll: true));
    }

    [Fact]
    public void ExpandReportsEveryItemOnceAndLeavesOwnedRowsAlone()
    {
        var owned = new List<Unlock> { new("weapon", "awm") };
        var before = owned.Count;

        var all = DevUnlocks.Expand(owned, Shipped.Weapons, Shipped.Perks, Streaks);

        Assert.Equal(before, owned.Count);
        Assert.Contains(new Unlock("weapon", "awm"), all);
        Assert.Contains(new Unlock("weapon", "ak47"), all);
        Assert.Contains(new Unlock("perk", "ghost"), all);
        Assert.Contains(new Unlock("streak", "moab"), all);
        Assert.Contains(DevUnlocks.Marker, all);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.DoesNotContain(new Unlock("weapon", "bazooka"), all);
        Assert.DoesNotContain(new Unlock("weapon", "knife"), all);
        Assert.True(DevUnlocks.IsMarked(all));
        Assert.False(DevUnlocks.IsMarked(owned));
    }

    [Fact]
    public void ShowcaseKitsValidateAtLevelOneWithEverythingUnlocked()
    {
        var perkLevels = Shipped.Perks.Perks.ToDictionary(p => p.Id, p => p.UnlockLevel);
        var all = DevUnlocks.Expand(Array.Empty<Unlock>(), Shipped.Weapons, Shipped.Perks, Streaks);

        for (var slot = 0; slot < Loadout.SlotCount; slot++)
        {
            var kit = DevUnlocks.DevKit(slot);
            Assert.Equal(slot, kit.Slot);
            Assert.Null(LoadoutRules.ValidateLoadout(kit, Shipped.Weapons, 1, all, perkLevels, Streaks));
        }
    }

    [Fact]
    public void ShowcaseKitsAreRejectedWithoutTheOverride()
    {
        // the dev kit uses guns and streaks above level 1, so the release rules refuse it at level 1 with no rows
        var perkLevels = Shipped.Perks.Perks.ToDictionary(p => p.Id, p => p.UnlockLevel);
        Assert.NotNull(LoadoutRules.ValidateLoadout(DevUnlocks.DevKit(1), Shipped.Weapons, 1, Array.Empty<Unlock>(), perkLevels, Streaks));
    }

    [Fact]
    public void DevKitRejectsASlotOutsideTheFive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DevUnlocks.DevKit(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DevUnlocks.DevKit(Loadout.SlotCount));
    }
}
