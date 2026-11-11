using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// Shooter v2 weapon sounds (#114): every weapon's events point at files that ship in the asset folder, and the
/// categories that have a dedicated firearm take do not fall back to the AK placeholder.
/// </summary>
public class WeaponSoundAssetTests
{
    static readonly SoundsFile Sounds = SoundsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "sounds.json"));
    static readonly WeaponsFile Weapons = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"));

    static string AssetsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "assets");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new InvalidOperationException("assets/manifest.json not found above the test output folder");
    }

    static IEnumerable<SoundDef> WeaponEvents(WeaponDef d)
    {
        var kinds = d.Kind == WeaponKind.Projectile ? new[] { "launch" } : new[] { "shot" };
        if (d.Kind != WeaponKind.Melee) kinds = kinds.Concat(new[] { "reload", "dry" }).ToArray();
        foreach (var k in kinds)
            if (Sounds.Find(SoundRules.Event(k, d.Id)) is { } s) yield return s;
    }

    [Fact]
    public void EveryWeaponEventFileExistsInTheAssetFolder()
    {
        var assets = AssetsDir();
        foreach (var d in Weapons.Weapons)
            foreach (var s in WeaponEvents(d))
                foreach (var f in s.Files)
                    Assert.True(File.Exists(Path.Combine(assets, f)), $"{s.Id}: missing {f}");
    }

    [Theory]
    [InlineData(WeaponCategory.Shotgun)]
    [InlineData(WeaponCategory.Smg)]
    [InlineData(WeaponCategory.Sniper)]
    [InlineData(WeaponCategory.Pistol)]
    public void ShotgunSmgSniperAndPistolShotsHaveTheirOwnSound(WeaponCategory category)
    {
        // The AK take is only the placeholder for the machine guns and the v1 sniper; these categories have dedicated takes.
        foreach (var d in Weapons.Weapons.Where(w => w.Category == category && w.Id is not ("pistol" or "sniper")))
        {
            var shot = Sounds.Find(SoundRules.Event("shot", d.Id));
            Assert.NotNull(shot);
            Assert.DoesNotContain("free-firearm-sound-library/ak47.ogg", shot!.Files);
        }
    }

    [Theory]
    [InlineData("m4a1", "free-firearm-sound-library/ar15.ogg")]
    [InlineData("aug", "free-firearm-sound-library/ar15.ogg")]
    [InlineData("g36", "free-firearm-sound-library/ar15.ogg")]
    [InlineData("mp5", "free-firearm-sound-library/ppsh.ogg")]
    [InlineData("remington870", "free-firearm-sound-library/model12.ogg")]
    [InlineData("glock17", "free-firearm-sound-library/walther_ppq.ogg")]
    [InlineData("svd", "free-firearm-sound-library/sks.ogg")]
    public void NamedWeaponsUseTheirTake(string id, string file) =>
        Assert.Equal(file, Sounds.Find(SoundRules.Event("shot", id))!.Files.Single());

    [Fact]
    public void MachineGunsSayWhichShareTheRifleTake()
    {
        // The pack has no machine gun take: they share the AK take, pitched, so they are at least distinguishable by pitch.
        var mgs = Weapons.Weapons.Where(w => w.Category == WeaponCategory.MachineGun).Select(w => w.Id).ToArray();
        Assert.NotEmpty(mgs);
        foreach (var id in mgs)
            Assert.Equal("free-firearm-sound-library/ak47.ogg", Sounds.Find(SoundRules.Event("shot", id))!.Files.Single());
    }
}
