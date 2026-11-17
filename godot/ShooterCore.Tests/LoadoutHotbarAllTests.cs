using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Balance and integration pass (#133): the smoke-arena hotbar holds every weapon, a player's hotbar stays three slots.</summary>
public class LoadoutHotbarAllTests
{
    static readonly WeaponsFile Weapons = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"));

    [Fact]
    public void AllIsEveryWeaponInFileOrderSoTheRpgKeepsSlotThree()
    {
        var all = LoadoutHotbar.All(Weapons);
        Assert.Equal(Enumerable.Range(0, Weapons.Weapons.Count), all);
        Assert.Equal("rpg", Weapons.Weapons[all[2]].Id);  // the rpg smoke and bot select weapons.json slot 3
    }

    [Fact]
    public void APlayerHotbarIsPrimarySecondaryKnifeGrenadeAndHasNoRpg()
    {
        var hotbar = LoadoutHotbar.Build(Weapons, new Loadout(0, "", "ak47", "pistol", "", "", ""));
        Assert.Equal(4, hotbar.Length);
        Assert.DoesNotContain(Weapons.Weapons.FindIndex(w => w.Id == "rpg"), hotbar);
    }
}
