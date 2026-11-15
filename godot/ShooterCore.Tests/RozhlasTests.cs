using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The village loudspeaker: the clock-driven schedule and the distance-based volume.</summary>
public class RozhlasTests
{
    [Fact]
    public void FullLevelNearTheSpeakerAndQuieterWithEveryDoubling()
    {
        Assert.Equal(0f, RozhlasRules.GainDb(2f, 6f, 350f));
        Assert.Equal(0f, RozhlasRules.GainDb(6f, 6f, 350f));
        Assert.Equal(-6f, RozhlasRules.GainDb(12f, 6f, 350f), 0.01f);
        Assert.Equal(-12f, RozhlasRules.GainDb(24f, 6f, 350f), 0.01f);
        Assert.Equal(-6f, RozhlasRules.GainDb(24f, 6f, 350f, 3f), 0.01f); // a gentler roll-off: 3 dB per doubling
    }

    [Fact]
    public void VolumeNeverRisesWithDistanceAndIsSilentAtTheRange()
    {
        var last = 1f;
        for (var d = 0f; d <= 360f; d += 1f)
        {
            var g = RozhlasRules.GainDb(d, 6f, 350f);
            Assert.True(g <= last + 1e-4f, $"gain rose at {d} m");
            last = g;
        }
        Assert.Equal(RozhlasRules.SilentDb, RozhlasRules.GainDb(350f, 6f, 350f));
        Assert.True(RozhlasRules.GainDb(349f, 6f, 350f) < -50f); // already almost gone just inside the range
    }

    [Fact]
    public void FarSpeakersAreDuller()
    {
        Assert.Equal(8000f, RozhlasRules.CutoffHz(3f, 6f, 350f, 8000f, 500f));
        Assert.Equal(500f, RozhlasRules.CutoffHz(350f, 6f, 350f, 8000f, 500f));
        var last = float.MaxValue;
        for (var d = 0f; d <= 360f; d += 1f)
        {
            var c = RozhlasRules.CutoffHz(d, 6f, 350f, 8000f, 500f);
            Assert.True(c <= last + 1e-3f, $"cut-off rose at {d} m");
            last = c;
        }
        // the same factor per doubling: 6 -> 12 -> 24 m
        var a = RozhlasRules.CutoffHz(12f, 6f, 350f, 8000f, 500f);
        var b = RozhlasRules.CutoffHz(24f, 6f, 350f, 8000f, 500f);
        Assert.Equal(8000f / a, a / b, 0.01f);
        Assert.Throws<DataFileException>(() => RozhlasFile.Parse("""{ "version": 1, "near_cutoff_hz": 300, "far_cutoff_hz": 600 }"""));
    }

    [Fact]
    public void LoudnessFollowsTheNearestSpeakerOnly()
    {
        var s = new List<Vector3> { new(100, 0, 0), new(10, 0, 0), new(500, 0, 0) };
        Assert.Equal(10f, RozhlasRules.NearestDistance(s, Vector3.Zero), 3);
        Assert.Equal(40f, RozhlasRules.NearestDistance(s, new Vector3(50, 0, 0)), 3); // 10 is 40 away, 100 is 50 away
        Assert.Equal(float.PositiveInfinity, RozhlasRules.NearestDistance(Array.Empty<Vector3>(), Vector3.Zero));
        // a second speaker beside the first adds nothing: the level is the same with or without it
        var pair = new List<Vector3> { new(10, 0, 0), new(10.5f, 0, 0) };
        Assert.Equal(RozhlasRules.NearestDistance(new List<Vector3> { new(10, 0, 0) }, Vector3.Zero), RozhlasRules.NearestDistance(pair, Vector3.Zero), 3);
    }

    [Fact]
    public void ExactlyOneBroadcastPerSlotAndItFitsInsideIt()
    {
        const double period = 600, length = 70;
        for (var slot = 100L; slot < 120; slot++)
        {
            double? first = null, last = null;
            for (var t = slot * period; t < (slot + 1) * period; t += 0.5)
            {
                if (RozhlasRules.OnAir(t, period, length, 2) is not { } b) continue;
                Assert.Equal(slot, b.Slot);
                first ??= t - b.Offset;
                last = t;
            }
            Assert.NotNull(first);
            Assert.InRange(last!.Value - first!.Value, length - 1, length);
        }
    }

    [Fact]
    public void TheMomentAndTheVariantDependOnTheSlotAloneSoEveryClientAgrees()
    {
        var a = RozhlasRules.OnAir(1_700_000_030, 600, 70, 3);
        var b = RozhlasRules.OnAir(1_700_000_030, 600, 70, 3);
        Assert.Equal(a, b);
        var starts = Enumerable.Range(0, 40).Select(i => i * 600.0).Select(t0 =>
        {
            for (var t = t0; t < t0 + 600; t += 1) if (RozhlasRules.OnAir(t, 600, 70, 1) is { } x) return t - x.Offset - t0;
            return -1.0;
        }).ToList();
        Assert.True(starts.Distinct().Count() > 20, "start moments should vary between slots");
    }

    [Fact]
    public void ABroadcastLongerThanTheSlotFillsIt()
    {
        var b = RozhlasRules.OnAir(1230.0, 600, 900, 1);
        Assert.NotNull(b);
        Assert.Equal(30.0, b!.Value.Offset, 3);
        Assert.Null(RozhlasRules.OnAir(1230.0, 600, 0, 1));
    }

    [Fact]
    public void TheFileIsValidated()
    {
        var f = RozhlasFile.Parse("""{ "version": 1, "files": ["x.ogg"], "period_s": 300, "max_distance": 200, "reference_distance": 5 }""");
        Assert.Equal(300, f.PeriodS);
        Assert.Throws<DataFileException>(() => RozhlasFile.Parse("""{ "version": 1, "reference_distance": 50, "max_distance": 20 }"""));
    }

    // ---- server-owned schedule and the phase machine ----

    const double IdleClock = 1e12; // a unix second at which Cfg's schedule has nothing on air

    static RozhlasFile Cfg(params double[] lengths) => new() { Files = lengths.Select((_, i) => $"r{i}.ogg").ToList(), LengthsS = lengths.ToList(), PeriodS = 600 };

    /// <summary>The unix second the scheduled broadcast of this slot begins (found through the public rule).</summary>
    static double StartOf(RozhlasFile cfg, long slot)
    {
        for (var t = slot * cfg.PeriodS; t < (slot + 1) * cfg.PeriodS; t += 0.25)
            if (RozhlasRules.OnAir(t, cfg.PeriodS, cfg.LongestS, cfg.Files.Count) is { } a) return t - a.Offset;
        throw new InvalidOperationException("no broadcast in slot");
    }

    [Fact]
    public void LengthsComeFromTheFileWithADefaultWhenAbsent()
    {
        var f = RozhlasFile.Parse("""{ "version": 1, "files": ["a.ogg", "b.ogg"], "lengths_s": [100.5], "default_length_s": 90 }""");
        Assert.Equal(100.5, f.LengthOf(0));
        Assert.Equal(90, f.LengthOf(1)); // not recorded
        Assert.Equal(100.5, f.LongestS);
        Assert.Equal(180, RozhlasFile.Parse("""{ "version": 1, "files": ["a.ogg"] }""").LengthOf(0));
        Assert.Throws<DataFileException>(() => RozhlasFile.Parse("""{ "version": 1, "files": ["a.ogg"], "lengths_s": [10, 20] }"""));
    }

    [Fact]
    public void ABroadcastEndsOnItsOwnAndTheMicGoesDead()
    {
        var r = new RozhlasBroadcast(Cfg(100));
        var seen = new List<(RozhlasPhase, RozhlasPhase)>();
        r.PhaseChanged += (a, b) => seen.Add((a, b));
        Assert.True(r.StartNow(0, 0));
        Assert.True(r.MicLive && r.DetourOpen && r.Phase == RozhlasPhase.OnAir);
        Assert.False(r.StartNow(0, 0)); // not while one is on
        for (var i = 0; i < 99; i++) r.Update(1f, IdleClock); // the unix clock only matters for the schedule: use a moment with nothing scheduled
        Assert.Equal(RozhlasPhase.OnAir, r.Phase);
        Assert.Equal(99f, r.OffsetS, 0.01f);
        r.Update(1.01f, IdleClock);
        Assert.Equal(RozhlasPhase.Idle, r.Phase);
        Assert.False(r.MicLive);
        Assert.Equal(new[] { (RozhlasPhase.Idle, RozhlasPhase.OnAir), (RozhlasPhase.OnAir, RozhlasPhase.Idle) }, seen);
    }

    [Fact]
    public void TheHijackLastsExactlyThreeMinutesFromTheKillReplacingWhatWasLeft()
    {
        var r = new RozhlasBroadcast(Cfg(175));
        Assert.False(r.AnnouncerKilled()); // nothing on air: nothing to kill
        r.StartNow(0, 0);
        for (var i = 0; i < 170; i++) r.Update(1f, 0); // 5 s of the broadcast are left
        Assert.True(r.AnnouncerKilled());
        Assert.Equal(RozhlasPhase.Hijacked, r.Phase);
        Assert.Equal(-1, r.Variant);
        Assert.Equal(180f, r.LeftS, 0.001f);
        for (var i = 0; i < 100; i++) r.Update(1f, 0);
        Assert.False(r.AnnouncerKilled()); // one hijack per broadcast, never refreshed
        for (var i = 0; i < 79; i++) r.Update(1f, 0);
        Assert.Equal(RozhlasPhase.Hijacked, r.Phase); // 179 s in
        Assert.True(r.MicLive);
        r.Update(1f, 0);
        Assert.Equal(RozhlasPhase.Idle, r.Phase); // 180 s: over, though the broadcast itself would have ended 175 s after its start
        Assert.False(r.AnnouncerKilled());
    }

    [Fact]
    public void TheServerClockStartsTheScheduledBroadcastOnceAndAJoinerGetsTheOffset()
    {
        var cfg = Cfg(100, 80);
        var start = StartOf(cfg, 7_000);
        var r = new RozhlasBroadcast(cfg);
        r.Update(0.05f, start - 5);
        Assert.Equal(RozhlasPhase.Idle, r.Phase);
        var air = RozhlasRules.OnAir(start + 10, cfg.PeriodS, cfg.LongestS, 2)!.Value;
        r.Update(0.05f, start + 10); // the slot began 10 s ago: the broadcast joins at its offset
        Assert.True(10 < cfg.LengthOf(air.Variant));
        Assert.Equal(RozhlasPhase.OnAir, r.Phase);
        Assert.Equal(air.Variant, r.Variant);
        Assert.Equal(10f, r.OffsetS, 0.2f);
        var msg = RozhlasCodec.Decode(RozhlasCodec.Encode(r.Message))!.Value; // what a late joiner is sent
        Assert.Equal(r.Message, msg);
        for (var t = 0f; t < 200f && r.Phase == RozhlasPhase.OnAir; t += 1) r.Update(1f, start + 10 + t);
        Assert.Equal(RozhlasPhase.Idle, r.Phase);
        r.Update(1f, start + cfg.LongestS - 0.5); // same slot, still inside its window: it is not started again
        Assert.Equal(RozhlasPhase.Idle, r.Phase);
    }

    [Fact]
    public void AnAdminBroadcastUsesUpTheSlotItWasStartedIn()
    {
        var cfg = Cfg(100);
        var start = StartOf(cfg, 8_000);
        var r = new RozhlasBroadcast(cfg);
        Assert.True(r.StartNow(start + 20, 0));
        for (var i = 0; i < 100; i++) r.Update(1f, start + 20 + i);
        Assert.Equal(RozhlasPhase.Idle, r.Phase);
        r.Update(1f, start + 60); // the scheduled one of this slot would have been on air now: it already was (the admin's)
        Assert.Equal(RozhlasPhase.Idle, r.Phase);
    }

    [Fact]
    public void ThePhaseIsDeclaredAndOnlyTheDeclaredEdgesExist()
    {
        var s = RozhlasBroadcast.PhaseSpec;
        Assert.True(s.Allows(RozhlasPhase.Idle, RozhlasPhase.OnAir));
        Assert.True(s.Allows(RozhlasPhase.OnAir, RozhlasPhase.Hijacked));
        Assert.True(s.Allows(RozhlasPhase.Hijacked, RozhlasPhase.Idle));
        Assert.False(s.Allows(RozhlasPhase.Idle, RozhlasPhase.Hijacked));
        Assert.False(s.Allows(RozhlasPhase.Hijacked, RozhlasPhase.OnAir));
        Assert.Null(RozhlasCodec.Decode(new byte[3]));
    }
}
