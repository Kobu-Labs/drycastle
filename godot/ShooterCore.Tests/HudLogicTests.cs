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
