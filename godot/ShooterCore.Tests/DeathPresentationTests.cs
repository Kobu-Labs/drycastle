using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class DeathPresentationTests
{
    static DeathPlan FallPlan(float delay = 5) => DeathPresentation.Plan(ClientMode.OnFoot, false, false, false, delay, 1);

    [Fact]
    public void On_foot_falls_with_a_share_of_the_respawn_delay_clamped()
    {
        Assert.Equal(DeathStyle.Fall, FallPlan().Style);
        Assert.Equal(0.8f, FallPlan(5).FallSeconds);
        Assert.Equal(0.45f, FallPlan(1.5f).FallSeconds, 3);
        Assert.Equal(0.25f, FallPlan(0.2f).FallSeconds);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(99)] [InlineData(-7)]
    public void Roll_is_70_to_90_degrees(int seed)
    {
        var deg = MathF.Abs(DeathPresentation.RollFor(seed)) * 180f / MathF.PI;
        Assert.InRange(deg, 69.99f, 90.01f);
    }

    [Theory]
    [InlineData(ClientMode.OnFoot)] [InlineData(ClientMode.Rising)] [InlineData(ClientMode.Pinned)]
    public void On_foot_modes_fall_including_a_pin(ClientMode before) =>
        Assert.Equal(DeathStyle.Fall, DeathPresentation.Plan(before, false, false, false, 5, 1).Style);

    [Theory]
    [InlineData(ClientMode.Seated)] [InlineData(ClientMode.Turret)] [InlineData(ClientMode.Missile)] [InlineData(ClientMode.Held)] [InlineData(ClientMode.MassSeated)]
    public void Other_cameras_get_blood_and_scrim_only(ClientMode before)
    {
        var p = DeathPresentation.Plan(before, false, false, false, 5, 1);
        Assert.Equal(DeathStyle.Blood, p.Style);
        Assert.Equal(0f, p.RollRadians);
        Assert.True(p.Blood > 0);
    }

    [Fact]
    public void Under_water_blood_only_and_drowning_a_plain_fade()
    {
        Assert.Equal(DeathStyle.Blood, DeathPresentation.Plan(ClientMode.OnFoot, true, false, false, 5, 1).Style);
        var d = DeathPresentation.Plan(ClientMode.OnFoot, false, true, false, 5, 1);
        Assert.Equal(DeathStyle.Fade, d.Style);
        Assert.Equal(0f, d.Blood);
        Assert.Equal(0f, d.FallSeconds);
    }

    [Fact]
    public void Reduce_motion_never_moves_the_camera_and_halves_the_blood()
    {
        var calm = DeathPresentation.Plan(ClientMode.OnFoot, false, false, true, 5, 1);
        var full = FallPlan();
        Assert.Equal(DeathStyle.Blood, calm.Style);
        Assert.True(calm.Blood < full.Blood);
        Assert.True(calm.ScrimFadeSeconds < full.ScrimFadeSeconds);
    }

    [Fact]
    public void Falls_then_lies_and_the_scrim_waits_for_the_lying()
    {
        var d = new DeathPresentation();
        Assert.False(d.Active);
        d.Begin(FallPlan(5));
        Assert.True(d.Falling);
        Assert.True(d.OwnsCamera);
        d.Tick(0.4f);
        Assert.True(d.Falling);
        Assert.Equal(0.25f, d.Drop, 3);
        Assert.Equal(0f, d.ScrimNow);
        Assert.InRange(d.BloodNow, 0.49f, 0.51f);
        d.Tick(0.45f);
        Assert.True(d.Lying);
        Assert.Equal(1f, d.Drop);
        Assert.Equal(1f, d.BloodNow);
        Assert.Equal(0f, d.ScrimNow);
        d.Tick(0.125f);
        Assert.Equal(0.5f, d.ScrimNow, 3);
        d.Tick(5);
        Assert.Equal(1f, d.ScrimNow);
        Assert.True(d.Lying);
    }

    [Fact]
    public void Respawn_mid_fall_ends_it_and_a_new_death_starts_clean()
    {
        var d = new DeathPresentation();
        d.Begin(FallPlan());
        d.Tick(0.3f);
        d.End();
        Assert.False(d.Active);
        Assert.Equal(0f, d.Drop);
        Assert.Equal(0f, d.BloodNow);
        Assert.Equal(0f, d.ScrimNow);
        d.Tick(1); // nothing shown: nothing happens
        Assert.False(d.Active);
        d.Begin(FallPlan());
        Assert.True(d.Falling);
        Assert.Equal(0f, d.Drop);
    }

    [Fact]
    public void Respawn_while_lying_and_a_second_death_while_lying_are_legal()
    {
        var d = new DeathPresentation();
        d.Begin(FallPlan(1));
        d.Tick(1);
        Assert.True(d.Lying);
        d.Begin(FallPlan(1)); // a second death without a spawn between: starts over, no illegal edge
        Assert.True(d.Falling);
        d.Tick(1);
        d.End();
        Assert.False(d.Active);
    }

    [Fact]
    public void Death_while_pounced_is_a_fall_like_any_other()
    {
        var d = new DeathPresentation();
        d.Begin(DeathPresentation.Plan(ClientMode.Pinned, false, false, false, 4, 7));
        Assert.True(d.OwnsCamera);
        d.Tick(2);
        Assert.True(d.Lying);
        Assert.Equal(1f, d.Drop);
    }

    [Fact]
    public void A_plain_fade_is_lying_at_the_first_tick_with_no_blood()
    {
        var d = new DeathPresentation();
        d.Begin(DeathPresentation.Plan(ClientMode.OnFoot, false, true, false, 5, 1));
        Assert.Equal(0f, d.BloodNow);
        Assert.False(d.OwnsCamera);
        d.Tick(0.016f);
        Assert.True(d.Lying);
        d.Tick(0.016f);
        Assert.True(d.ScrimNow > 0);
    }

    [Fact]
    public void Blood_style_waits_a_beat_before_the_scrim()
    {
        var d = new DeathPresentation();
        d.Begin(DeathPresentation.Plan(ClientMode.Seated, false, false, false, 5, 1));
        Assert.False(d.OwnsCamera);
        d.Tick(0.1f);
        Assert.Equal(0f, d.ScrimNow);
        Assert.True(d.BloodNow > 0);
        d.Tick(0.3f);
        Assert.True(d.Lying);
    }
}
