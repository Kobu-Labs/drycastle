using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.RozhlasFeed;
using Xunit;

namespace Drycastle.ShooterCore.Tests.RozhlasFeed;

/// <summary>Slice 4 of the rozhlas detour: the combat mix's mapping and filters, the synthesised hijack sounds, the last-10-s static, the flood cap, the sample bank.</summary>
public class RozhlasCombatTests
{
    static readonly SoundsFile Table = SoundsFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "sounds.json")));

    [Theory]
    [InlineData(CombatKind.Shot, "ak47", "shot.ak47")]
    [InlineData(CombatKind.Shot, "knife", "shot.knife")]
    [InlineData(CombatKind.Shot, "", "shot.")] // the truck's roof gun: the client plays the table's "shot." too
    [InlineData(CombatKind.Launch, "rpg", "launch.rpg")]
    [InlineData(CombatKind.Explosion, "rpg", "explosion.rpg")]
    [InlineData(CombatKind.VehicleExplosion, "", "explosion.vehicle")]
    [InlineData(CombatKind.Death, "", "death")]
    public void EventsMapToTheClientsSoundIds(CombatKind kind, string weapon, string id)
    {
        Assert.Equal(id, RozhlasCombat.SoundId(kind, weapon));
        Assert.NotNull(Table.Find(id));
    }

    [Fact]
    public void EveryPreloadedIdIsInTheTableAndFootstepsAreNot()
    {
        var ids = RozhlasCombat.PreloadIds(Table).ToList();
        Assert.Contains("shot.ak47", ids); Assert.Contains("death", ids); Assert.Contains("explosion.rpg", ids);
        Assert.DoesNotContain(ids, i => i.StartsWith("footstep") || i.StartsWith("reload") || i.StartsWith("switch") || i.StartsWith("hit"));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)] // outside the Urad
    [InlineData(false, true, false)] // mic not live
    [InlineData(false, false, false)]
    public void OnlyInsideTheUradWhileTheMicIsLive(bool live, bool inside, bool mixed)
    {
        var c = new RozhlasCombat(_ => new short[] { 1, 2, 3 }, _ => { }, () => 0);
        Assert.Equal(mixed, c.Event(CombatKind.Shot, "ak47", live, inside));
        Assert.Equal(mixed ? 1 : 0, c.Mixed);
    }

    [Fact]
    public void SameSoundIsGatedAndAMissingSampleIsSilent()
    {
        double t = 0; var got = new List<short[]>();
        var c = new RozhlasCombat(id => id == "shot.ak47" ? new short[] { 5 } : null, got.Add, () => t);
        Assert.True(c.Event(CombatKind.Shot, "ak47", true, true));
        Assert.False(c.Event(CombatKind.Shot, "ak47", true, true)); // inside the gap
        t = RozhlasCombat.MinGapS + 0.001;
        Assert.True(c.Event(CombatKind.Shot, "ak47", true, true));
        Assert.False(c.Event(CombatKind.Death, "", true, true)); // no sample: not rebroadcast
        Assert.Equal(2, got.Count);
    }

    [Fact]
    public void MixerCapsOneShotsAndKeepsTheNewestAudible()
    {
        var m = new RozhlasFeedMixer(maxShots: 4);
        for (var i = 1; i <= 10; i++) m.Mix(new short[] { (short)(i * 100), (short)(i * 100) });
        Assert.Equal(4, m.ActiveShots);
        var b = new short[2]; m.Pull(b);
        Assert.Equal(7 * 100 + 8 * 100 + 9 * 100 + 10 * 100, b[0]); // the four newest
    }

    [Fact]
    public void SynthesisedSoundsAreAudibleBoundedAndDeterministic()
    {
        foreach (var (pcm, minS, maxS) in new[] { (RozhlasSynth.Thump, 0.2, 0.6), (RozhlasSynth.Squeal, 0.8, 1.3), (RozhlasSynth.Click, 0.005, 0.03) })
        {
            Assert.InRange(pcm.Length / (double)RozhlasFeedMixer.Rate, minS, maxS);
            var peak = pcm.Max(s => Math.Abs((int)s));
            Assert.InRange(peak, 8000, 32766); // not silent, not clipping
        }
        Assert.Equal(RozhlasSynth.Squeal, RozhlasSynth.Squeal);
    }

    [Fact]
    public void SampleBankLoadsOffThreadAppliesPitchAndGainAndLogsFailuresOnce()
    {
        var logs = new List<string>();
        var bank = new RozhlasSampleBank(Table, f => f, f => f.Contains("ak47") ? Enumerable.Repeat((short)1000, 1600).ToArray() : throw new IOException("nope"), logs.Add);
        Assert.Null(bank.Get("shot.ak47")); // not blocking: first ask only starts the load
        bank.Preload(new[] { "shot.sniper", "death" });
        SpinWait.SpinUntil(() => bank.Pending == 0, 5000);
        var ak = bank.Get("shot.ak47")!;
        Assert.Equal(1600, ak.Length);
        Assert.Equal((short)Math.Round(1000 * Math.Pow(10, -3 / 20.0)), ak[0]); // volume_db -3 baked in
        var sniper = bank.Get("shot.sniper")!; // pitch 0.7: longer
        Assert.True(sniper.Length > 1600);
        Assert.Null(bank.Get("death"));
        Assert.Single(logs, l => l.Contains("death.ogg"));
    }

    [Fact]
    public void FfmpegDecodesARealSoundToNonSilentMono16k()
    {
        var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets"));
        var file = Path.Combine(assets, Table.Find("shot.ak47")!.Files[0]);
        if (!File.Exists(file)) return;
        short[] pcm;
        try { pcm = FfmpegPcm.Decode(file); }
        catch (System.ComponentModel.Win32Exception) { return; } // no ffmpeg on this machine
        Assert.InRange(pcm.Length / (double)RozhlasFeedMixer.Rate, 0.5, 2.5);
        Assert.True(pcm.Max(s => Math.Abs((int)s)) > 1000);
    }
}
