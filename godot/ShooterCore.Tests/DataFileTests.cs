using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class DataFileTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    [Fact]
    public void ShippedFilesLoadWithTicketNumbers()
    {
        var d = GameData.Load(DataDir);
        Assert.Equal(25, d.Match.ScoreGoal);
        Assert.Equal(600, d.Match.TimeLimit);
        Assert.Equal(3, d.Match.RespawnDelay);
        Assert.Equal(3, d.Match.SpawnProtection);

        Assert.Equal(new[] { "pistol", "ak47", "bazooka", "knife", "sniper", "m4a1", "aug", "g36", "rpk", "m249", "m60", "pkm", "glock17", "deagle", "m82", "awm", "svd" }, d.Weapons.Weapons.Select(w => w.Id));
        Assert.Equal("pistol", d.Weapons.StartingWeapon);
        var ak = d.Weapons.Find("ak47")!;
        Assert.Equal(WeaponKind.Hitscan, ak.Kind);
        Assert.True(ak.FullAuto);
        Assert.Equal(30, ak.Damage);
        Assert.Equal(0.10f, ak.FireInterval);
        Assert.Equal(30, ak.MagazineSize);
        Assert.Equal(90, ak.ReserveAmmo);
        var bz = d.Weapons.Find("bazooka")!;
        Assert.Equal(WeaponKind.Projectile, bz.Kind);
        Assert.Equal(6, bz.Splash!.Radius);
        Assert.Equal(0.5f, bz.Splash.SelfDamageMultiplier);
        Assert.Equal(100, d.Weapons.Find("knife")!.BackstabDamage);

        Assert.Equal(8, d.Vehicles.RunOverMinSpeed);
        Assert.Equal(30, d.Vehicles.AbandonReturnTime);
        Assert.Contains(d.Vehicles.Vehicles, v => v.Id == "car");
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var m = DataFile.Parse<MatchConfig>("""{"version":1,"score_goal":10,"loadouts":{"x":1},"future":[1,2]}""", "match.json");
        Assert.Equal(10, m.ScoreGoal);
        Assert.Equal(600, m.TimeLimit); // default kept
    }

    [Fact]
    public void MissingVersionIsRejected()
    {
        Assert.Throws<DataFileException>(() => DataFile.Parse<MatchConfig>("""{"score_goal":10}""", "match.json"));
    }

    [Fact]
    public void NewerVersionIsRejected()
    {
        var e = Assert.Throws<DataFileException>(() => DataFile.Parse<MatchConfig>("""{"version":99}""", "match.json"));
        Assert.Contains("newer", e.Message);
    }

    [Fact]
    public void BadJsonAndMissingFileGiveDataFileException()
    {
        Assert.Throws<DataFileException>(() => DataFile.Parse<MatchConfig>("{nope", "match.json"));
        Assert.Throws<DataFileException>(() => MatchConfig.Load(Path.Combine(DataDir, "absent.json")));
    }

    [Fact]
    public void WeaponsValidation()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var p = Path.Combine(dir, "weapons.json");
        File.WriteAllText(p, """{"version":1,"starting_weapon":"nope","weapons":[{"id":"a","kind":"melee"}]}""");
        Assert.Throws<DataFileException>(() => WeaponsFile.Load(p));
        File.WriteAllText(p, """{"version":1,"starting_weapon":"a","weapons":[{"id":"a","kind":"melee"},{"id":"a","kind":"melee"}]}""");
        Assert.Throws<DataFileException>(() => WeaponsFile.Load(p));
        Directory.Delete(dir, true);
    }
}
