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
        Assert.All(ChatCommands.All.Where(c => c.Name != "fps"), c => Assert.False(c.ClientSide));

    [Fact]
    public void ReadoutSamplesTwiceASecond()
    {
        var r = new FpsReadout();
        Assert.Equal("60 FPS", r.Sample(10.0, 60));
        Assert.Null(r.Sample(10.2, 144));
        Assert.Null(r.Sample(10.49, 144));
        Assert.Equal("144 FPS", r.Sample(10.5, 144));
    }

    [Fact]
    public void ResetSamplesAtOnce()
    {
        var r = new FpsReadout();
        r.Sample(1.0, 60);
        r.Reset();
        Assert.Equal("75 FPS", r.Sample(1.1, 75));
    }

    [Theory]
    [InlineData(59.6, "60 FPS")]
    [InlineData(-3, "0 FPS")]
    [InlineData(5000, "999 FPS")]
    public void FormatRoundsAndClamps(double fps, string text) => Assert.Equal(text, FpsReadout.Format(fps));
}
