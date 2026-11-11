using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// Shooter v2 registry content (#113): the sniper rifles and the three pistols in the shipped weapons.json, and their
/// shots-to-kill at 10 m and 60 m (100 hp body shot, no headshot), the numbers in the resolution comment.
/// </summary>
public class SniperAndPistolContentTests
{
    static readonly WeaponsFile File = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"));

    static readonly string[] Snipers = { "sniper", "m82", "awm", "svd" };
    static readonly string[] Pistols = { "pistol", "glock17", "deagle" };

    static WeaponDef Def(string id) => File.Find(id)!;

    [Fact]
    public void FourSniperRiflesArePrimaryNonAutoHitscanWithAScopeAndBreathHold()
    {
        Assert.Equal(Snipers.Length, File.Weapons.Count(w => w.Category == WeaponCategory.Sniper));
        foreach (var id in Snipers)
        {
            var w = Def(id);
            Assert.Equal(WeaponSlot.Primary, w.Slot);
            Assert.Equal(WeaponKind.Hitscan, w.Kind);
            Assert.False(w.FullAuto, id);
            Assert.True(w.ZoomFov is > 0 and < 60, id);
            Assert.True(w.Range >= 600, id);
            Assert.True(w.BreathHoldSeconds > 0 && w.BreathRecoverSeconds > 0 && w.SwayAmount > 0, id);
            Assert.True(w.MagazineSize <= 10, id);
        }
    }

    [Fact]
    public void SniperCategoryRecoilDefaultsApplyUnlessOverridden()
    {
        // the sniper block climbs 1.2 deg per shot and caps at 3; M82 and AWM override the climb, SVD overrides it too
        Assert.Equal(1.2f, Def("sniper").Recoil.VerticalPerShot);
        Assert.Equal(3f, Def("sniper").Recoil.MaxClimb);
        Assert.Equal(2.0f, Def("m82").Recoil.VerticalPerShot);
        Assert.Equal(0.9f, Def("svd").Recoil.VerticalPerShot);
    }

    [Fact]
    public void SnipersHaveDistinctRoles()
    {
        // M82: heavy, no falloff, small mag. AWM: one-shot body, slowest cycle, smallest mag. SVD: semi-auto marksman, the only sniper that falls off.
        Assert.Equal(1f, Def("m82").FalloffMultiplier);
        Assert.True(Def("awm").FireInterval > Def("svd").FireInterval && Def("awm").MagazineSize < Def("svd").MagazineSize);
        Assert.True(Def("svd").FalloffMultiplier < 1 && Def("svd").FalloffEnd > Def("svd").FalloffStart);
        Assert.Equal(1f, Def("sniper").FalloffMultiplier);
    }

    [Fact]
    public void ThreePistolsWithControlRateAndPowerRoles()
    {
        Assert.Equal(Pistols.Length, File.Weapons.Count(w => w.Category == WeaponCategory.Pistol));
        foreach (var id in Pistols)
        {
            var w = Def(id);
            Assert.Equal(WeaponSlot.Secondary, w.Slot);
            Assert.False(w.FullAuto, id);
            Assert.Equal(WeaponKind.Hitscan, w.Kind);
        }
        // rate: the fastest cycle and the biggest mag; power: the most damage per shot; control (the M1911, "pistol") the tightest spread
        Assert.Equal(Pistols.Min(id => Def(id).FireInterval), Def("glock17").FireInterval);
        Assert.True(Def("glock17").MagazineSize > Def("deagle").MagazineSize);
        Assert.Equal(Pistols.Max(id => Def(id).Damage), Def("deagle").Damage);
        Assert.Equal(Pistols.Min(id => Def(id).SpreadBase), Def("pistol").SpreadBase);
    }

    /// <summary>Shots to kill a body (100 hp) at 10 m and at 60 m.</summary>
    [Theory]
    [InlineData("m82", 1, 1)]
    [InlineData("awm", 1, 1)]
    [InlineData("svd", 2, 2)]
    [InlineData("deagle", 3, 3)]  // 45 dmg (was 60, a 2-shot kill at every range, which made it the best pistol everywhere)
    [InlineData("glock17", 6, 8)]
    [InlineData("pistol", 5, 6)]
    public void ShotsToKillTableMatchesTheData(string id, int shotsAt10, int shotsAt60)
    {
        var w = Def(id);
        Assert.Equal(shotsAt10, (int)Math.Ceiling(MoveConsts.MaxHealth / HitMath.HitscanDamage(w, 10, false)));
        Assert.Equal(shotsAt60, (int)Math.Ceiling(MoveConsts.MaxHealth / HitMath.HitscanDamage(w, 60, false)));
    }
}
