using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// Shooter v2 registry content (#111): the machine gun and assault rifle categories in the shipped weapons.json.
/// The ttk table below is the one in the resolution comment: shots to kill a body (100 hp, no headshot) at 10/30/60 m
/// and the time from the first shot to the killing shot at the weapon's fire interval.
/// </summary>
public class WeaponRegistryContentTests
{
    static readonly WeaponsFile File = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"));

    static readonly string[] MachineGuns = { "m249", "rpk", "m60", "pkm" };
    static readonly string[] AssaultRifles = { "ak47", "m4a1", "aug", "g36" };
    static readonly string[] NewWeapons = { "m4a1", "aug", "g36", "rpk", "m249", "m60", "pkm" };

    static WeaponDef Def(string id) => File.Find(id)!;

    [Fact]
    public void EachCategoryHasFourPrimaryFullAutoHitscanRepresentatives()
    {
        Assert.Equal(MachineGuns.Length, File.Weapons.Count(w => w.Category == WeaponCategory.MachineGun));
        Assert.Equal(AssaultRifles.Length, File.Weapons.Count(w => w.Category == WeaponCategory.AssaultRifle));
        foreach (var id in MachineGuns.Concat(AssaultRifles))
        {
            var w = Def(id);
            Assert.Equal(WeaponSlot.Primary, w.Slot);
            Assert.Equal(WeaponKind.Hitscan, w.Kind);
            Assert.True(w.FullAuto, id);
        }
    }

    [Fact]
    public void OnlyTheStarterIsFreeAndNewWeaponsSpreadAcrossLevels()
    {
        Assert.Equal(0, Def("ak47").UnlockLevel);
        foreach (var id in NewWeapons)
            Assert.InRange(Def(id).UnlockLevel, 1, 12);
        // Spread, not bunched: the new weapons unlock on at least five different levels.
        Assert.True(NewWeapons.Select(id => Def(id).UnlockLevel).Distinct().Count() >= 5);
    }

    [Fact]
    public void MachineGunsSlowThePlayerHoldLotsOfAmmoAndReloadSlowerThanRifles()
    {
        var slowestRifle = AssaultRifles.Min(id => Def(id).MoveSpeedMultiplier);
        var longestRifleReload = AssaultRifles.Max(id => Def(id).ReloadTime);
        foreach (var id in MachineGuns)
        {
            var w = Def(id);
            Assert.True(w.MoveSpeedMultiplier < slowestRifle, id);
            Assert.True(w.MagazineSize >= 75 && w.ReserveAmmo >= 150, id);
            Assert.True(w.ReloadTime > longestRifleReload, id);
        }
    }

    [Fact]
    public void RecoilCategoryDefaultsApplyAndPerWeaponOverridesDiffer()
    {
        // Machine guns climb under sustained fire; rifles inherit the assault rifle block unless they override it.
        foreach (var id in MachineGuns) Assert.True(Def(id).Recoil.MaxClimb > 0, id);
        foreach (var id in AssaultRifles) Assert.True(Def(id).Recoil.VerticalPerShot > 0, id);

        var ak = Def("ak47").Recoil;
        var m4 = Def("m4a1").Recoil;
        Assert.True(ak.VerticalPerShot > m4.VerticalPerShot);
        Assert.NotEmpty(ak.Pattern);
        Assert.Empty(m4.Pattern);
        Assert.Equal(0.5f, m4.VerticalPerShot);          // own override (AK 0.9 kicks harder)
        Assert.Equal(10f, m4.MaxClimb);                  // inherited from the assault_rifle block
        Assert.Equal(9f, Def("aug").Recoil.MaxClimb);    // own override
    }

    /// <summary>Expected (shots, ttk seconds) at 10, 30 and 60 m for each weapon. Keep in step with the resolution comment.</summary>
    static readonly Dictionary<string, (int Shots, float Ttk)[]> Ttk = new()
    {
        ["ak47"] = new[] { (4, 0.30f), (4, 0.30f), (4, 0.30f) },
        ["m4a1"] = new[] { (4, 0.26f), (4, 0.26f), (5, 0.34f) },
        ["aug"] = new[] { (4, 0.26f), (4, 0.26f), (5, 0.34f) },
        ["g36"] = new[] { (5, 0.32f), (5, 0.32f), (5, 0.32f) },
        ["rpk"] = new[] { (4, 0.30f), (4, 0.30f), (5, 0.40f) },
        ["m249"] = new[] { (5, 0.32f), (5, 0.32f), (6, 0.40f) },
        ["m60"] = new[] { (4, 0.33f), (4, 0.33f), (5, 0.44f) },
        ["pkm"] = new[] { (4, 0.28f), (4, 0.28f), (5, 0.37f) },
    };

    [Fact]
    public void TtkTableMatchesTheData()
    {
        float[] distances = { 10f, 30f, 60f };
        foreach (var (id, expected) in Ttk)
        {
            var w = Def(id);
            for (var i = 0; i < distances.Length; i++)
            {
                var dmg = HitMath.HitscanDamage(w, distances[i], false);
                var shots = (int)Math.Ceiling(MoveConsts.MaxHealth / dmg);
                var ttk = (shots - 1) * w.FireInterval;
                Assert.True(shots == expected[i].Shots && Math.Abs(ttk - expected[i].Ttk) < 0.005f,
                    $"{id} at {distances[i]} m: got {shots} shots / {ttk:0.000} s, table says {expected[i].Shots} / {expected[i].Ttk}");
            }
        }
    }
}
