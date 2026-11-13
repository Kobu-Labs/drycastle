using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Channel layout (A-03), the map build in Hello (E-06) and the empty-tick heartbeat (A-05).</summary>
public class NetChannelTests
{
    [Theory]
    [InlineData(EventKind.Shot)]
    [InlineData(EventKind.PelletFan)]
    [InlineData(EventKind.ImpactFx)]
    [InlineData(EventKind.GunshipShots)]
    [InlineData(EventKind.GunshipImpacts)]
    public void LossyEffectsAreUnreliable(EventKind kind) => Assert.True(Wire.IsEffect(kind));

    [Theory]
    [InlineData(EventKind.Hit)]
    [InlineData(EventKind.Kill)]
    [InlineData(EventKind.Death)]
    [InlineData(EventKind.Spawn)]
    [InlineData(EventKind.Explosion)]
    [InlineData(EventKind.Launch)]
    [InlineData(EventKind.MatchState)]
    [InlineData(EventKind.GunshipBlasts)] // carries the gunner's hit markers: state, not an effect
    public void StateEventsStayReliable(EventKind kind) => Assert.False(Wire.IsEffect(kind));

    [Fact]
    public void ThreeChannelsWithEffectsOnTheirOwn()
    {
        Assert.Equal(3, Wire.Channels);
        Assert.Equal(0, Wire.ChanReliable);
        Assert.Equal(1, Wire.ChanData);
        Assert.Equal(2, Wire.ChanEffects);
        Assert.True(Wire.ChanEffects < Wire.Channels);
    }

    [Fact]
    public void HelloCarriesTheMapBuildHash()
    {
        var hello = new HelloMsg(Wire.ProtocolVersion, "alice", "tok", HelloFlags.Sandbox, 0x0123456789ABCDEFUL);
        Assert.Equal(hello, Wire.DecodeReliable(Wire.Encode(hello)));
    }

    [Fact]
    public void HelloWithoutAHashDecodesAsZero()
    {
        // a hello that ends after the flags (before v25) has no hash: it decodes as unknown, and the version check rejects it first
        var full = Wire.Encode(new HelloMsg(Wire.ProtocolVersion, "bob", "", 0, 42UL));
        var old = full[..^8];
        Assert.Equal(0UL, Assert.IsType<HelloMsg>(Wire.DecodeReliable(old)).MapHash);
    }

    [Fact]
    public void FnvHashIsStableAndSensitive()
    {
        var a = Wire.Fnv1a64("{\"map_version\":\"0.10.0-village\"}"u8);
        Assert.Equal(a, Wire.Fnv1a64("{\"map_version\":\"0.10.0-village\"}"u8));
        Assert.NotEqual(a, Wire.Fnv1a64("{\"map_version\":\"0.10.0-village \"}"u8));
        Assert.Equal(14695981039346656037UL, Wire.Fnv1a64(ReadOnlySpan<byte>.Empty));
    }

}
