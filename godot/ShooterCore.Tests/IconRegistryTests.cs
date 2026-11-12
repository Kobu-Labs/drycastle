using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class IconRegistryTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>godot/shooter/ui/icons in the repository, found by walking up from the test output folder.</summary>
    static string IconsDir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "godot", "shooter", "ui", "icons");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("godot/shooter/ui/icons not found above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>The on-disk file behind a res:// icon path.</summary>
    static string OnDisk(string resPath) => Path.Combine(IconsDir, resPath.Substring(IconRegistry.Root.Length));

    [Fact]
    public void EveryWeaponPerkAndStreakInTheShippedDataHasAnIconFile()
    {
        var data = GameData.Load(DataDir);
        var registry = new IconRegistry(data);
        var missing = new List<string>();

        foreach (var w in data.Weapons.Weapons)
        {
            if (!File.Exists(OnDisk(IconRegistry.PathFor(IconKind.Weapon, w.Id)))) missing.Add("weapon " + w.Id);
        }
        foreach (var p in data.Perks.Perks)
        {
            if (!File.Exists(OnDisk(IconRegistry.PathFor(IconKind.Perk, p.Id)))) missing.Add("perk " + p.Id);
        }
        foreach (var s in data.Killstreaks.Streaks)
        {
            if (!File.Exists(OnDisk(IconRegistry.PathFor(IconKind.Streak, s.Id)))) missing.Add("streak " + s.Id);
        }
        foreach (var c in data.Weapons.Weapons.Select(w => IconRegistry.CategoryId(w.Category)).OfType<string>().Distinct())
        {
            if (!File.Exists(OnDisk(IconRegistry.PathFor(IconKind.Category, c)))) missing.Add("category " + c);
        }

        Assert.True(missing.Count == 0, "no icon file for: " + string.Join(", ", missing));
        Assert.True(File.Exists(OnDisk(IconRegistry.Placeholder)), "placeholder icon missing");
    }

    [Fact]
    public void EveryDataIdIsKnownToTheRegistry()
    {
        var data = GameData.Load(DataDir);
        var registry = new IconRegistry(data);
        foreach (var w in data.Weapons.Weapons) Assert.True(registry.Knows(IconKind.Weapon, w.Id), w.Id);
        foreach (var p in data.Perks.Perks) Assert.True(registry.Knows(IconKind.Perk, p.Id), p.Id);
        foreach (var s in data.Killstreaks.Streaks) Assert.True(registry.Knows(IconKind.Streak, s.Id), s.Id);
    }

    [Fact]
    public void UnknownIdResolvesToThePlaceholder()
    {
        var registry = new IconRegistry(["ak47"], ["assault_rifle"], ["ghost"], ["uav"]);
        Assert.Equal(IconRegistry.Placeholder, registry.Resolve(IconKind.Weapon, "not_a_gun"));
        Assert.Equal(IconRegistry.Placeholder, registry.Resolve(IconKind.Perk, "uav"), ignoreCase: false);
        Assert.Equal(IconRegistry.Placeholder, registry.Resolve(IconKind.Category, null));
    }

    [Fact]
    public void KnownIdResolvesToItsFileUnlessTheFileIsMissing()
    {
        var registry = new IconRegistry(["ak47"], ["assault_rifle"], ["ghost"], ["uav"]);
        Assert.Equal("res://ui/icons/weapons/ak47.png", registry.Resolve(IconKind.Weapon, "ak47"));
        Assert.Equal("res://ui/icons/streaks/uav.png", registry.Resolve(IconKind.Streak, "uav", _ => true));
        Assert.Equal(IconRegistry.Placeholder, registry.Resolve(IconKind.Streak, "uav", _ => false));
    }

    [Theory]
    [InlineData(WeaponCategory.Pistol, "pistol")]
    [InlineData(WeaponCategory.MachineGun, "machine_gun")]
    [InlineData(WeaponCategory.AssaultRifle, "assault_rifle")]
    [InlineData(WeaponCategory.Smg, "smg")]
    [InlineData(WeaponCategory.Shotgun, "shotgun")]
    [InlineData(WeaponCategory.Sniper, "sniper")]
    public void CategoryBadgeIdsMatchTheFileNames(WeaponCategory category, string id)
    {
        Assert.Equal(id, IconRegistry.CategoryId(category));
    }

    [Theory]
    [InlineData(WeaponCategory.Launcher)]
    [InlineData(WeaponCategory.Melee)]
    public void CategoriesWithoutABadgeHaveNoId(WeaponCategory category)
    {
        Assert.Null(IconRegistry.CategoryId(category));
    }
}
