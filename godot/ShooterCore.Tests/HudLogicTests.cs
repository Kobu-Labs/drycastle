using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class HudLogicTests
{
    [Fact]
    public void FeedKeepsLastFiveAndExpiresAfterSixSeconds()
    {
        var t = new List<double> { 0, 1, 2, 3, 4, 5, 6 };
        Assert.Equal((2, 5), HudLogic.FeedWindow(t, 6.5));
        Assert.Equal((4, 3), HudLogic.FeedWindow(t, 9.5)); // 0..3 expired (age >= 6)
        Assert.Equal((7, 0), HudLogic.FeedWindow(t, 13));
        Assert.Equal((0, 0), HudLogic.FeedWindow(new List<double>(), 5));
    }

    [Fact]
    public void ScreenScaleFollowsHeightAndUiSetting()
    {
        Assert.Equal(1f, HudLogic.ScreenScale(1080, 1f), 3);
        Assert.Equal(2f, HudLogic.ScreenScale(2160, 1f), 3);
        Assert.Equal(1.5f, HudLogic.ScreenScale(1080, 1.5f), 3);
        Assert.Equal(0.8f, HudLogic.ScreenScale(480, 1f), 3); // never under the floor
    }

    [Fact]
    public void ScoreShowsLeaderOrLeading()
    {
        var rows = new[] { new ScoreRow(1, "me", 3, 0, 10), new ScoreRow(2, "bob", 7, 1, 20), new ScoreRow(3, "cy", 5, 2, 30) };
        var s = HudLogic.Score(rows, 1);
        Assert.Equal(new ScoreLine(3, "bob", 7, false), s);
        Assert.True(HudLogic.Score(rows, 2).IAmLeader);
        Assert.True(HudLogic.Score(new[] { new ScoreRow(1, "me", 0, 0, 0) }, 1).IAmLeader);
        Assert.True(HudLogic.Score(Array.Empty<ScoreRow>(), 1).IAmLeader);
        var tie = new[] { new ScoreRow(1, "me", 4, 0, 0), new ScoreRow(2, "bob", 4, 0, 0) };
        Assert.True(HudLogic.Score(tie, 1).IAmLeader);
    }

    [Fact]
    public void ScoreboardOrder()
    {
        var rows = new[] { new ScoreRow(3, "c", 2, 1, 0), new ScoreRow(1, "a", 2, 0, 0), new ScoreRow(2, "b", 5, 9, 0) };
        Assert.Equal(new ushort[] { 2, 1, 3 }, HudLogic.Sort(rows).Select(r => r.Id));
    }

    [Fact]
    public void TimeText()
    {
        Assert.Equal("5:00", HudLogic.TimeText(300f));
        Assert.Equal("0:01", HudLogic.TimeText(0.2f));
        Assert.Equal("0:00", HudLogic.TimeText(-3f));
        Assert.Equal("1:05", HudLogic.TimeText(64.1f));
        Assert.Equal(3, HudLogic.CeilSeconds(2.1));
    }

    [Fact]
    public void CrosshairGapGrowsWithSpreadAndHasFloor()
    {
        var a = HudLogic.CrosshairGap(0.4f, 90, 1080, 6);
        var b = HudLogic.CrosshairGap(4f, 90, 1080, 6);
        Assert.Equal(6f, a); // 0.4 deg is 3.8 px: floor wins
        Assert.InRange(b, 37f, 38.5f); // tan(4 deg) * 540
        Assert.True(HudLogic.CrosshairGap(4f, 90, 2160, 6) > 2 * b - 1);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f, -10f, 0f)]                  // in front (-Z)
    [InlineData(0f, 0f, 0f, 10f, 0f, MathF.PI / 2)]         // right (+X)
    [InlineData(0f, 0f, 0f, -10f, 0f, -MathF.PI / 2)]       // left
    [InlineData(MathF.PI / 2, 0f, 0f, -10f, 0f, 0f)]        // yaw 90 left: facing -X, source at -X is ahead
    public void DamageAngleIsRelativeToView(float yaw, float vx, float vz, float sx, float sz, float want)
        => Assert.Equal(want, HudLogic.DamageAngle(vx, vz, yaw, sx, sz), 3);

    [Fact]
    public void DamageAngleBehindIsPi()
        => Assert.Equal(MathF.PI, MathF.Abs(HudLogic.DamageAngle(0, 0, 0, 0, 10)), 3);

    [Fact]
    public void UiScaleClamps()
    {
        Assert.Equal(0.75f, HudLogic.ClampUiScale(0.1f));
        Assert.Equal(1.5f, HudLogic.ClampUiScale(9));
        Assert.Equal(1f, HudLogic.ClampUiScale(float.NaN));
    }

    [Fact]
    public void SpreadGrowsPerShotCapsAndRecovers()
    {
        var w = new WeaponDef { SpreadBase = 1, SpreadPerShot = 1.5f, SpreadMax = 4, SpreadRecovery = 0.6f };
        var s = new SpreadEstimator();
        Assert.Equal(1f, s.Degrees(w, 0));
        s.Shot(w, 1); s.Shot(w, 1.1);
        Assert.InRange(s.Degrees(w, 1.1), 3.4f, 3.6f);
        s.Shot(w, 1.2); s.Shot(w, 1.3);
        Assert.Equal(4f, s.Degrees(w, 1.3), 3);
        Assert.Equal(1f, s.Degrees(w, 3), 3);
    }
}

public class StatePaletteTests
{
    // CIE76 colour difference between two sRGB triples
    static double DeltaE(StatePalette.Rgb a, StatePalette.Rgb b)
    {
        static (double L, double A, double B) Lab(StatePalette.Rgb c)
        {
            static double Lin(double u) => u <= 0.04045 ? u / 12.92 : Math.Pow((u + 0.055) / 1.055, 2.4);
            static double F(double v) => v > 0.008856 ? Math.Cbrt(v) : 7.787 * v + 16.0 / 116;
            double r = Lin(c.R), g = Lin(c.G), bl = Lin(c.B);
            var x = F((0.4124 * r + 0.3576 * g + 0.1805 * bl) / 0.95047);
            var y = F(0.2126 * r + 0.7152 * g + 0.0722 * bl);
            var z = F((0.0193 * r + 0.1192 * g + 0.9505 * bl) / 1.08883);
            return (116 * y - 16, 500 * (x - y), 200 * (y - z));
        }
        var (l1, a1, b1) = Lab(a); var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    [Fact]
    public void ColorBlindStateColoursAreAllDistinct()
    {
        var set = new[] { StatePalette.Accent, StatePalette.Good(true), StatePalette.Warn(true), StatePalette.Danger(true) };
        for (var i = 0; i < set.Length; i++)
            for (var j = i + 1; j < set.Length; j++)
                Assert.True(DeltaE(set[i], set[j]) >= 40, $"colour-blind colours {i} and {j} are too close ({DeltaE(set[i], set[j]):F1})");
    }

    [Fact]
    public void DefaultStateColoursAreDistinct()
    {
        var set = new[] { StatePalette.Good(false), StatePalette.Warn(false), StatePalette.Danger(false) };
        for (var i = 0; i < set.Length; i++)
            for (var j = i + 1; j < set.Length; j++)
                Assert.True(DeltaE(set[i], set[j]) >= 20);
    }
}
