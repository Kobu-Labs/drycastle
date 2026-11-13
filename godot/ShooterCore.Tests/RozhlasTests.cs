using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>The village loudspeaker: the clock-driven schedule and the distance-based volume.</summary>
public class RozhlasTests
{
    [Fact]
    public void FullLevelNearTheSpeakerAndQuieterWithEveryDoubling()
    {
        Assert.Equal(0f, RozhlasRules.GainDb(2f, 6f, 350f));
        Assert.Equal(0f, RozhlasRules.GainDb(6f, 6f, 350f));
        Assert.Equal(-6.02f, RozhlasRules.GainDb(12f, 6f, 350f), 0.01f);
        Assert.Equal(-12.04f, RozhlasRules.GainDb(24f, 6f, 350f), 0.01f);
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
    public void NearestPicksTheClosestSpeakersInRange()
    {
        var s = new List<Vector3> { new(100, 0, 0), new(10, 0, 0), new(500, 0, 0), new(30, 0, 0), new(60, 0, 0) };
        Assert.Equal(new[] { 1, 3 }, RozhlasRules.Nearest(s, Vector3.Zero, 2, 350f));
        Assert.Equal(new[] { 1, 3, 4, 0 }, RozhlasRules.Nearest(s, Vector3.Zero, 8, 350f)); // 500 m is out of range
        Assert.Empty(RozhlasRules.Nearest(s, new Vector3(2000, 0, 0), 3, 350f));
    }

    [Fact]
    public void AVoiceKeepsItsSpeakerWhileItIsStillWanted()
    {
        Assert.Equal(new[] { 7, 4, 9 }, RozhlasRules.Assign(new[] { 7, 4, -1 }, new[] { 4, 7, 9 }));
        // 7 drops out of the wanted set: its voice takes the new speaker, 4 stays put
        Assert.Equal(new[] { 9, 4, -1 }, RozhlasRules.Assign(new[] { 7, 4, -1 }, new[] { 4, 9 }));
        Assert.Equal(new[] { -1, -1 }, RozhlasRules.Assign(new[] { 3, 5 }, Array.Empty<int>()));
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
        Assert.Throws<DataFileException>(() => RozhlasFile.Parse("""{ "version": 1, "voices": 0 }"""));
    }
}
