using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The radar screen's pure part (#371): the arm carried on between messages, the trail's falloff, the blip fade over one revolution, the bearing as a direction, and the display's label cache.</summary>
public class RadarDisplayTests
{
    const float Tol = 1e-4f;

    static RadarSweepMsg Msg(float angle, float rev, RadarContact[]? swept = null, RadarBlip[]? friends = null) =>
        new(7, angle, rev, swept ?? Array.Empty<RadarContact>(), friends ?? Array.Empty<RadarBlip>());

    [Fact]
    public void The_arm_turns_clockwise_one_revolution_in_the_revolution_time_and_wraps()
    {
        Assert.Equal(MathF.PI / 2f, RadarScreenMath.Advance(0f, 6f, 1.5f), Tol);
        Assert.Equal(0f, RadarScreenMath.Advance(0f, 6f, 6f), Tol);
        Assert.Equal(MathF.PI / 2f, RadarScreenMath.Advance(MathF.PI * 1.5f, 6f, 3f), Tol);
        Assert.Equal(1f, RadarScreenMath.Advance(1f, 6f, -2f), Tol); // time does not run backwards
        Assert.Equal(1f, RadarScreenMath.Advance(1f, 0f, 2f), Tol);  // no revolution: it stays
    }

    [Theory]
    [InlineData(0f, -10f, 0f)]            // north (-Z)
    [InlineData(10f, 0f, MathF.PI / 2f)]  // east
    [InlineData(0f, 10f, MathF.PI)]       // south
    [InlineData(-10f, 0f, MathF.PI * 1.5f)] // west
    public void A_bearing_is_clockwise_from_north_like_the_servers_sweep(float dx, float dz, float expected)
    {
        Assert.Equal(expected, RadarScreenMath.Bearing(dx, dz), Tol);
        Assert.Equal(RadarSweep.Fraction(dx, dz) * MathF.Tau, RadarScreenMath.Bearing(dx, dz), Tol);
    }

    [Fact]
    public void A_bearing_is_a_direction_on_the_north_up_sheet()
    {
        var n = RadarScreenMath.Direction(0f);
        Assert.Equal(0f, n.X, Tol); Assert.Equal(-1f, n.Y, Tol);
        var e = RadarScreenMath.Direction(MathF.PI / 2f);
        Assert.Equal(1f, e.X, Tol); Assert.Equal(0f, e.Y, Tol);
        var s = RadarScreenMath.Direction(MathF.PI);
        Assert.Equal(0f, s.X, Tol); Assert.Equal(1f, s.Y, Tol);
    }

    [Fact]
    public void The_trail_is_full_on_the_arm_falls_off_behind_it_and_is_dark_ahead_of_it()
    {
        var arm = 1f;
        Assert.Equal(1f, RadarScreenMath.TrailAlpha(RadarScreenMath.Behind(arm, 1f)), Tol);
        var near = RadarScreenMath.TrailAlpha(RadarScreenMath.Behind(arm, 0.8f));
        var far = RadarScreenMath.TrailAlpha(RadarScreenMath.Behind(arm, 0.2f));
        Assert.InRange(near, far, 1f);
        Assert.True(far > 0f && far < near);
        Assert.Equal(0f, RadarScreenMath.TrailAlpha(RadarScreenMath.Behind(arm, arm - RadarScreenMath.TrailRadians)), Tol);
        Assert.Equal(0f, RadarScreenMath.TrailAlpha(RadarScreenMath.Behind(arm, 1.3f))); // ahead of the arm: nearly a full turn behind it
    }

    [Fact]
    public void A_blip_fades_linearly_over_one_revolution()
    {
        Assert.Equal(1f, RadarScreenMath.Fade(0f, 6f), Tol);
        Assert.Equal(0.5f, RadarScreenMath.Fade(3f, 6f), Tol);
        Assert.Equal(0f, RadarScreenMath.Fade(6f, 6f), Tol);
        Assert.Equal(0f, RadarScreenMath.Fade(9f, 6f), Tol);
        Assert.Equal(0f, RadarScreenMath.Fade(1f, 0f));
    }

    [Fact]
    public void Reach_leaves_the_rectangle_from_anywhere_in_it()
    {
        Assert.Equal(MathF.Sqrt(2) * 100f, RadarScreenMath.Reach(0f, 0f, 0f, 0f, 100f, 100f), Tol);
        Assert.Equal(MathF.Sqrt(2) * 50f, RadarScreenMath.Reach(50f, 50f, 0f, 0f, 100f, 100f), Tol);
        Assert.True(RadarScreenMath.Reach(-30f, 400f, 0f, 0f, 100f, 100f) > 400f); // an origin outside the window still reaches past it
    }

    [Fact]
    public void The_display_carries_the_arm_on_and_ages_the_blips_since_the_message()
    {
        var d = new RadarDisplay();
        Assert.False(d.Live(0));
        d.Apply(Msg(MathF.PI, 6f, new[] { new RadarContact(3, RadarKind.Player, 10f, 20f, 1f) }), now: 100.0, (x, z) => "A1");
        Assert.True(d.Live(100.5));
        Assert.Equal(MathF.PI + MathF.PI / 6f, d.Arm(100.5), Tol); // half a second is a twelfth of a turn
        var c = d.Contacts[0];
        Assert.Equal(1f - 1f / 6f, d.Alpha(c, 100.0), Tol);
        Assert.Equal(1f - 1.5f / 6f, d.Alpha(c, 100.5), Tol);
        Assert.Equal(0f, d.Alpha(c, 106.0), Tol);
    }

    [Fact]
    public void A_stale_sweep_stops_the_arm_and_is_not_live()
    {
        var d = new RadarDisplay();
        d.Apply(Msg(0f, 6f), 10.0, (x, z) => "");
        Assert.True(d.Live(10.0 + RadarDisplay.StaleSeconds));
        Assert.False(d.Live(10.0 + RadarDisplay.StaleSeconds + 0.1));
        Assert.Equal(d.Arm(10.0 + RadarDisplay.StaleSeconds), d.Arm(60.0), Tol);
    }

    [Fact]
    public void A_grid_reference_is_made_once_for_a_blip_that_stays_and_again_when_it_moves()
    {
        var d = new RadarDisplay();
        var calls = 0;
        string Ref(float x, float z) { calls++; return $"R{calls}"; }
        var found = new[] { new RadarContact(3, RadarKind.Player, 10f, 20f, 0f) };
        var mate = new[] { new RadarBlip(4, RadarKind.Vehicle, 1f, 2f) };
        d.Apply(Msg(0f, 6f, found, mate), 1.0, Ref);
        Assert.Equal(2, calls);
        var label = d.Contacts[0].Label;
        d.Apply(Msg(0.5f, 6f, new[] { new RadarContact(3, RadarKind.Player, 10f, 20f, 0.1f) }, new[] { new RadarBlip(4, RadarKind.Vehicle, 1f, 2f) }), 1.1, Ref);
        Assert.Equal(2, calls);
        Assert.Equal(label, d.Contacts[0].Label);
        d.Apply(Msg(1f, 6f, new[] { new RadarContact(3, RadarKind.Player, 30f, 20f, 0f) }, new[] { new RadarBlip(4, RadarKind.Vehicle, 1f, 2f) }), 1.2, Ref);
        Assert.Equal(3, calls); // only the one that moved
        Assert.Single(d.Friends);
    }

    [Fact]
    public void Clear_forgets_the_sweep()
    {
        var d = new RadarDisplay();
        d.Apply(Msg(1f, 6f, new[] { new RadarContact(3, RadarKind.Player, 1f, 1f, 0f) }), 1.0, (x, z) => "A1");
        d.Clear();
        Assert.Empty(d.Contacts);
        Assert.False(d.Live(1.0));
        Assert.Equal(0f, d.Arm(1.0));
    }
}
