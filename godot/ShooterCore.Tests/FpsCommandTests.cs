using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>/fps (#340): a client-side command anyone may type, and the counter's text.</summary>
public class FpsCommandTests
{
    static readonly ChatContext Admin = new(true, Array.Empty<string>());
    static readonly ChatContext Guest = new(false, Array.Empty<string>());

    [Fact]
    public void FpsIsClientSideAndForEveryone()
    {
        foreach (var ctx in new[] { Admin, Guest })
        {
            var p = ChatCommands.Parse("/fps", ctx);
            Assert.True(p.Ok);
            Assert.True(p.Command!.ClientSide);
            Assert.Contains(ChatCommands.Visible(ctx), c => c.Name == "fps");
        }
    }

    [Fact]
    public void FpsTakesNoArguments() => Assert.False(ChatCommands.Parse("/fps on", Guest).Ok);

    [Fact]
    public void OtherCommandsStayOnTheServer() =>
        Assert.All(ChatCommands.All.Where(c => c.Name is not ("fps" or "night")), c => Assert.False(c.ClientSide));

    [Fact]
    public void ReadoutSamplesTwiceASecond()
    {
        var r = new FpsReadout();
        Assert.Equal(new FpsReadout.Reading("60 FPS", FpsReadout.NoTps), r.Sample(10.0, 60, 0));
        Assert.Null(r.Sample(10.2, 144, 6));
        Assert.Null(r.Sample(10.49, 144, 9));
        Assert.Equal(new FpsReadout.Reading("144 FPS", "30 TPS"), r.Sample(10.5, 144, 15));
    }

    [Fact]
    public void TpsIsSnapshotsPerSecondOverTheWindow()
    {
        var r = new FpsReadout();
        r.Sample(1.0, 60, 100);
        Assert.Equal("30 TPS", r.Sample(1.5, 60, 115).Value.Tps);  // 15 snapshots in 0.5 s
        Assert.Equal("0 TPS", r.Sample(2.0, 60, 115).Value.Tps);      // a stall: nothing arrived
    }

    [Fact]
    public void TpsIsNotShownUntilASecondSample()
    {
        var r = new FpsReadout();
        Assert.Equal(FpsReadout.NoTps, r.Sample(1.0, 60, 50).Value.Tps);
    }

    [Fact]
    public void ResetRestartsTheTpsWindow()
    {
        var r = new FpsReadout();
        r.Sample(1.0, 60, 10);
        r.Reset();
        Assert.Equal(FpsReadout.NoTps, r.Sample(1.1, 75, 40).Value.Tps);
    }

    [Fact]
    public void ResetSamplesAtOnce()
    {
        var r = new FpsReadout();
        r.Sample(1.0, 60, 0);
        r.Reset();
        Assert.Equal("75 FPS", r.Sample(1.1, 75, 0).Value.Fps);
    }

    [Theory]
    [InlineData(59.6, "60 FPS")]
    [InlineData(-3, "0 FPS")]
    [InlineData(5000, "999 FPS")]
    public void FormatRoundsAndClamps(double fps, string text) => Assert.Equal(text, FpsReadout.Format(fps));

    [Theory]
    [InlineData(29.6, "30 TPS")]
    [InlineData(-1, "0 TPS")]
    [InlineData(5000, "999 TPS")]
    public void TpsFormatRoundsAndClamps(double tps, string text) => Assert.Equal(text, FpsReadout.FormatTps(tps));
}
