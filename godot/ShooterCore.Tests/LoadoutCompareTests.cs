using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Ticket #147: the picker's compare panel (five bars, each a share of the best weapon in its slot, recoil inverted).</summary>
public class LoadoutCompareTests
{
    static readonly string WeaponsPath = Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json");
    static readonly WeaponsFile Weapons = WeaponsFile.Load(WeaponsPath);

    [Fact]
    public void CompareHasTheFiveBarsInOrder()
    {
        var stats = LoadoutEditor.CompareStats(Weapons.Weapons.First(w => w.Slot == WeaponSlot.Primary), Weapons);
        Assert.Equal(LoadoutEditor.CompareKeys, stats.Select(s => s.Key).ToArray());
    }

    [Fact]
    public void EverySharePlusTheValuesStayInRange()
    {
        foreach (var w in Weapons.Weapons)
            foreach (var s in LoadoutEditor.CompareStats(w, Weapons))
            {
                Assert.InRange(s.Share, 0f, 1f);
                Assert.True(float.IsFinite(s.Value));
            }
    }

    [Fact]
    public void TheBestDamageInASlotFillsItsBar()
    {
        foreach (var slot in new[] { WeaponSlot.Primary, WeaponSlot.Secondary })
        {
            var best = Weapons.Weapons.Where(w => w.Slot == slot).OrderByDescending(w => w.Damage).First();
            var share = LoadoutEditor.CompareStats(best, Weapons).First(s => s.Key == LoadoutEditor.StatDamage).Share;
            Assert.Equal(1f, share, 4);
        }
    }

    [Fact]
    public void TheSteadiestWeaponInASlotFillsTheRecoilBar()
    {
        var steadiest = Weapons.Weapons.Where(w => w.Slot == WeaponSlot.Primary).OrderBy(w => w.Recoil.VerticalPerShot).First();
        var share = LoadoutEditor.CompareStats(steadiest, Weapons).First(s => s.Key == LoadoutEditor.StatRecoil).Share;
        Assert.Equal(1f, share, 4);
    }
}
