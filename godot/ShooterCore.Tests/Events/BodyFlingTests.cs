using Drycastle.ShooterCore.Events;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Events;

/// <summary>The ragdoll phase of a player the Bigfoot holds and throws (#360).</summary>
public class BodyFlingTests
{
    const float Dt = 1f / 30f;

    static BodyFlingTracker Flying()
    {
        var f = new BodyFlingTracker();
        f.Grabbed(); f.Flung();
        return f;
    }

    [Fact]
    public void Grab_Throw_Land_StandUp()
    {
        var f = new BodyFlingTracker();
        Assert.False(f.Active);
        f.Grabbed();
        Assert.Equal(BodyFling.Held, f.Phase);
        f.Flung();
        Assert.Equal(BodyFling.Flying, f.Phase);
        for (var i = 0; i < 30; i++) f.Tick(Dt, 15f, false);
        Assert.Equal(BodyFling.Flying, f.Phase); // fast: still in the air
        for (var i = 0; i < 3; i++) f.Tick(Dt, 0.5f, true);
        Assert.Equal(BodyFling.Lying, f.Phase);
        for (var t = 0f; t < BodyFlingTracker.LieSeconds - 3 * Dt; t += Dt) f.Tick(Dt, 0f, true);
        Assert.Equal(BodyFling.Lying, f.Phase); // limp on the ground first
        for (var i = 0; i < 6; i++) f.Tick(Dt, 0f, true);
        Assert.Equal(BodyFling.Landing, f.Phase);
        f.StoodUp();
        Assert.Equal(BodyFling.None, f.Phase);
    }

    [Fact]
    public void Slow_apex_start_is_not_a_landing()
    {
        var f = Flying();
        f.Tick(Dt, 0f, true); // the first frame of the throw: the guard time has not run
        Assert.Equal(BodyFling.Flying, f.Phase);
    }

    [Fact]
    public void Flight_has_a_cap()
    {
        var f = Flying();
        for (var t = 0f; t < BodyFlingTracker.MaxAirSeconds + 0.2f; t += Dt) f.Tick(Dt, 20f, false);
        Assert.Equal(BodyFling.Lying, f.Phase);
    }

    [Fact]
    public void Slow_apex_in_the_air_is_not_a_landing()
    {
        var f = Flying();
        for (var t = 0f; t < 0.5f; t += Dt) f.Tick(Dt, 0.3f, false); // a steep throw hangs at the top, slow but high
        Assert.Equal(BodyFling.Flying, f.Phase);
        f.Tick(Dt, 8f, false); // and falls again
        for (var t = 0f; t < 0.5f; t += Dt) f.Tick(Dt, 0.3f, false);
        Assert.Equal(BodyFling.Flying, f.Phase);
        f.Tick(Dt, 0.3f, true);
        Assert.Equal(BodyFling.Lying, f.Phase);
    }

    [Fact]
    public void Slow_for_long_without_a_ground_reading_still_lands()
    {
        var f = Flying();
        for (var t = 0f; t < BodyFlingTracker.SettleSeconds + 0.2f; t += Dt) f.Tick(Dt, 0f, false); // on a roof the terrain height cannot tell
        Assert.Equal(BodyFling.Lying, f.Phase);
    }

    [Fact]
    public void Release_without_a_throw_ends_it()
    {
        var f = new BodyFlingTracker();
        f.Grabbed(); f.Ended();
        Assert.Equal(BodyFling.None, f.Phase);
        f.Flung(); // only a held body is thrown
        Assert.Equal(BodyFling.None, f.Phase);
    }

    [Fact]
    public void Death_mid_flight_ends_it()
    {
        var f = Flying();
        f.Ended();
        Assert.False(f.Active);
    }

    [Fact]
    public void Grabbed_again_while_flying_or_landing()
    {
        var f = Flying();
        f.Grabbed();
        Assert.Equal(BodyFling.Held, f.Phase);
        f.Flung();
        for (var i = 0; i < 20; i++) f.Tick(Dt, 0f, true);
        Assert.Equal(BodyFling.Lying, f.Phase);
        f.Grabbed();
        Assert.Equal(BodyFling.Held, f.Phase);
    }
}
