using Xunit;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Tests.Net;

public class HeadLampTests
{
    [Theory]
    [InlineData(false, true, true, true, true)]   // pressed on foot, alive: on
    [InlineData(true, true, true, true, false)]   // pressed again: off
    [InlineData(true, false, true, true, true)]   // no press: stays
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, false, true, false)] // in a vehicle H is the car's lights
    [InlineData(true, false, false, true, false)] // getting in switches it off
    [InlineData(true, false, true, false, false)] // death switches it off
    [InlineData(false, true, true, false, false)] // dead: the press does nothing
    public void The_key_toggles_on_foot_and_alive_only(bool on, bool edge, bool onFoot, bool alive, bool expected) =>
        Assert.Equal(expected, HeadLamp.Next(on, edge, onFoot, alive));

    [Theory]
    [InlineData((ushort)0, false)]
    [InlineData((ushort)7, true)]
    [InlineData(ushort.MaxValue, true)]
    public void The_state_event_round_trips(ushort player, bool on)
    {
        Assert.True(HeadLamp.TryDecode(HeadLamp.Encode(player, on), out var p, out var o));
        Assert.Equal((player, on), (p, o));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 0 })]
    [InlineData(new byte[] { 1, 0, 2 })]
    [InlineData(new byte[] { 1, 0, 1, 0 })]
    public void Malformed_state_events_are_refused(byte[] payload) => Assert.False(HeadLamp.TryDecode(payload, out _, out _));

    [Fact]
    public void The_state_is_event_kind_31_and_a_reliable_state_not_an_effect()
    {
        Assert.Equal(31, (int)EventKind.HeadLampState);
        Assert.False(Wire.IsEffect(EventKind.HeadLampState));
        Assert.True(Wire.ProtocolVersion >= 76);
        var back = Assert.IsType<EventMsg>(Wire.DecodeReliable(Wire.Encode(new EventMsg((byte)EventKind.HeadLampState, HeadLamp.Encode(3, true)))));
        Assert.True(HeadLamp.TryDecode(back.Payload, out var p, out var on));
        Assert.Equal((3, true), (p, on));
    }

    [Fact]
    public void The_beam_is_a_soft_edged_cone_within_the_light_budget()
    {
        Assert.Equal(3, HeadLamp.MaxBeams);
        Assert.InRange(HeadLamp.RangeM, 40f, 60f);
        Assert.InRange(HeadLamp.AngleDeg, 20f, 60f);
        Assert.InRange(HeadLamp.AngleAttenuation, 0.1f, 1f); // above 1 the border turns into a hard rim
        Assert.InRange(HeadLamp.Energy, 3f, 20f);
    }
}
