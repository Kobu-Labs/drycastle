using Drycastle.ShooterCore.Wildlife;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Wildlife;

public class FlyerLookTests
{
    [Theory]
    [InlineData(AnimalState.Flying, false, AnimalClip.Cruise)]
    [InlineData(AnimalState.Flying, true, AnimalClip.Idle)]
    [InlineData(AnimalState.Falling, false, AnimalClip.Fall)]
    [InlineData(AnimalState.Crashed, false, AnimalClip.Perched)]
    [InlineData(AnimalState.Burning, false, AnimalClip.Perched)]
    [InlineData(AnimalState.Dead, false, AnimalClip.Perched)]
    public void Clip_of_a_state(AnimalState state, bool hover, AnimalClip clip) => Assert.Equal(clip, FlyerLook.ClipOf(state, hover));

    [Fact]
    public void A_ground_state_has_no_flyer_clip() => Assert.Null(FlyerLook.ClipOf(AnimalState.Walk, false));

    [Fact]
    public void Every_flyer_clip_is_one_the_species_needs()
    {
        var needed = AnimalClips.Needed(new AnimalDef { Flies = true }).ToHashSet();
        foreach (var s in new[] { AnimalState.Flying, AnimalState.Falling, AnimalState.Crashed, AnimalState.Burning, AnimalState.Dead })
            foreach (var hover in new[] { false, true })
                Assert.Contains(FlyerLook.ClipOf(s, hover)!.Value, needed);
    }

    [Fact]
    public void Bank_leans_into_the_turn_and_is_capped()
    {
        Assert.Equal(0f, FlyerLook.Bank(0));
        Assert.True(FlyerLook.Bank(0.04f) > 0 && FlyerLook.Bank(0.04f) < FlyerLook.MaxBank);
        Assert.Equal(-FlyerLook.Bank(0.04f), FlyerLook.Bank(-0.04f));
        Assert.Equal(FlyerLook.MaxBank, FlyerLook.Bank(10));
    }

    [Fact]
    public void Angle_delta_takes_the_short_way()
    {
        Assert.Equal(0.2f, FlyerLook.AngleDelta(MathF.PI - 0.1f, -MathF.PI + 0.1f), 4);
        Assert.Equal(-0.2f, FlyerLook.AngleDelta(-MathF.PI + 0.1f, MathF.PI - 0.1f), 4);
    }

    [Fact]
    public void Approach_moves_toward_the_target_without_passing_it()
    {
        var x = 0f;
        for (var i = 0; i < 100; i++) { x = FlyerLook.Approach(x, 1f, 5f, 0.016f); Assert.InRange(x, 0f, 1f); }
        Assert.True(x > 0.5f);
    }
}
