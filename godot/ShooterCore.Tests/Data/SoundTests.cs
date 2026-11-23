using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Tests.Weapons;
using Drycastle.ShooterCore.Weapons;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Data;

public class SoundTests
{
    static SoundsFile Real() => SoundsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "sounds.json"));

    [Theory]
    [InlineData("shot.pistol", 120f)]
    [InlineData("shot.ak47", 200f)]
    [InlineData("launch.rpg", 250f)]
    [InlineData("explosion.rpg", 300f)]
    [InlineData("footstep", 20f)]
    [InlineData("shot.knife", 15f)]
    [InlineData("vehicle.engine", 80f)]
    public void RealTableHasThePlanRanges(string id, float range) => Assert.Equal(range, Real().Find(id)!.MaxDistance);

    [Fact]
    public void EveryWeaponHasShotOrLaunchAndEveryUiEventExists()
    {
        var t = Real();
        var w = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"));
        foreach (var d in w.Weapons)
        {
            Assert.NotNull(t.Find(d.Kind == WeaponKind.Projectile ? SoundRules.Event("launch", d.Id) : SoundRules.Event("shot", d.Id)));
            if (d.Kind != WeaponKind.Melee) { Assert.NotNull(t.Find(SoundRules.Event("reload", d.Id))); Assert.NotNull(t.Find(SoundRules.Event("dry", d.Id))); }
        }
        foreach (var id in new[] { "ui.click", "ui.hover", "ui.accept", "ui.back", "ui.error", "hit.tick", "kill.confirm", "death", "round.start", "round.end", "music.menu", "vehicle.engine", "footstep" })
            Assert.NotNull(t.Find(id));
        Assert.All(t.Sounds.Where(s => s.Id.StartsWith("ui.")), s => Assert.Equal("UI", s.Bus));
        Assert.Equal("Music", t.Find("music.menu")!.Bus);
    }

    [Fact]
    public void ValidationRejectsBadEntries()
    {
        Assert.Throws<DataFileException>(() => SoundsFile.Parse("""{"version":1,"sounds":[{"id":"a","files":[],"bus":"SFX"}]}"""));
        Assert.Throws<DataFileException>(() => SoundsFile.Parse("""{"version":1,"sounds":[{"id":"a","files":["x"],"bus":"Master"}]}"""));
        Assert.Throws<DataFileException>(() => SoundsFile.Parse("""{"version":1,"sounds":[{"id":"a","files":["x"]},{"id":"a","files":["y"]}]}"""));
        Assert.Null(SoundsFile.Parse("""{"version":1,"sounds":[{"id":"a","files":["x"]}]}""").Find("b"));
    }

    [Fact]
    public void RangeCutsOffAtMaxDistanceAndNonPositionalIsAlwaysAudible()
    {
        var s = new SoundDef { MaxDistance = 120 };
        Assert.True(SoundRules.Audible(s, 120));
        Assert.False(SoundRules.Audible(s, 120.1f));
        Assert.True(SoundRules.Audible(new SoundDef(), 10000));
        Assert.Equal(12f, SoundRules.UnitSize(120));
        Assert.Equal(1f, SoundRules.UnitSize(5));
    }

    [Fact]
    public void PitchStaysInsideTheVariance()
    {
        var s = new SoundDef { Pitch = 2f, PitchVariance = 0.1f };
        Assert.Equal(2f, SoundRules.PitchFor(s, 0.5f), 4);
        Assert.Equal(1.8f, SoundRules.PitchFor(s, 0f), 4);
        Assert.Equal(2.2f, SoundRules.PitchFor(s, 1f), 4);
    }

    [Fact]
    public void FirstFoundPlaysTheFirstFileThatExistsAndOtherwiseEveryOne()
    {
        var t = SoundsFile.Parse("""{"version":1,"sounds":[{"id":"a","files":["private/a.ogg","gen/a.ogg"],"first_found":true,"bus":"SFX"},{"id":"b","files":["x.ogg","y.ogg"],"bus":"SFX"}]}""");
        Assert.Equal(new[] { "private/a.ogg" }, t.Find("a")!.Usable(_ => true));            // the private take wins when it is installed
        Assert.Equal(new[] { "gen/a.ogg" }, t.Find("a")!.Usable(f => f.StartsWith("gen/"))); // the generated fallback without it
        Assert.Empty(t.Find("a")!.Usable(_ => false));
        Assert.Equal(new[] { "x.ogg", "y.ogg" }, t.Find("b")!.Usable(_ => true));           // without the flag the files are variants
    }

    [Fact]
    public void TheHarvesterPopFallsBackToTheGeneratedFileInTheRepo()
    {
        var pop = Real().Find("harvester.pop")!;
        Assert.True(pop.FirstFound);
        Assert.StartsWith("audio/harvester/", pop.Files[0]); // the private clip first (assets-private, not in the repo)
        var assets = WeaponSoundAssetTests.AssetsDir();
        Assert.True(File.Exists(Path.Combine(assets, pop.Files[^1])), pop.Files[^1]); // the fallback is a repo file
    }

    [Fact]
    public void VariantNeverRepeatsTheLastOne()
    {
        for (var count = 2; count <= 6; count++)
            for (var last = 0; last < count; last++)
                for (var k = 0; k <= 20; k++)
                {
                    var v = SoundRules.PickVariant(count, last, k / 20f);
                    Assert.InRange(v, 0, count - 1);
                    Assert.NotEqual(last, v);
                }
        Assert.Equal(0, SoundRules.PickVariant(1, 0, 0.7f));
    }

    [Fact]
    public void FootstepsFollowDistanceCoveredOnTheGround()
    {
        var acc = 0f; var steps = 0;
        for (var i = 0; i < 300; i++) if (SoundRules.FootstepDue(ref acc, 6f, 1f / 30f, true)) steps++;   // 10 s at 6 m/s = 60 m
        Assert.InRange(steps, 30, 32);
        acc = 0; steps = 0;
        for (var i = 0; i < 300; i++) if (SoundRules.FootstepDue(ref acc, 6f, 1f / 30f, false)) steps++;  // airborne
        Assert.Equal(0, steps);
        for (var i = 0; i < 300; i++) if (SoundRules.FootstepDue(ref acc, 0.2f, 1f / 30f, true)) steps++; // standing
        Assert.Equal(0, steps);
    }

    [Fact]
    public void EnginePitchRisesWithSpeed()
    {
        Assert.Equal(SoundRules.EngineIdlePitch, SoundRules.EnginePitch(0), 4);
        Assert.Equal(SoundRules.EngineTopPitch, SoundRules.EnginePitch(500), 4);
        Assert.True(SoundRules.EnginePitch(50) > SoundRules.EnginePitch(20));
        Assert.Equal(SoundRules.EnginePitch(30), SoundRules.EnginePitch(-30)); // reversing
    }

    [Fact]
    public void PickVoice_prefers_free_then_lowest_priority_then_oldest()
    {
        var playing = new[] { true, true, true };
        var pr = new[] { 3, -1, -1 };
        var started = new ulong[] { 1, 5, 2 };
        Assert.Equal(2, SoundRules.PickVoice(3, i => playing[i], i => pr[i], i => started[i], 0));
        playing[1] = false;
        Assert.Equal(1, SoundRules.PickVoice(3, i => playing[i], i => pr[i], i => started[i], 0));
        playing[1] = true; pr = new[] { 3, 3, 3 };
        Assert.Equal(-1, SoundRules.PickVoice(3, i => playing[i], i => pr[i], i => started[i], -1)); // low priority is dropped
        Assert.Equal(0, SoundRules.PickVoice(3, i => playing[i], i => pr[i], i => started[i], 3));
    }
}
