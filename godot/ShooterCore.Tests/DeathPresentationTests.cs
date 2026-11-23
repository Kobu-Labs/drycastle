using System.Linq;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class DeathPresentationTests
{
    [Fact]
    public void BlastDeathOnFootIsThrownWithoutBlood()
    {
        var p = DeathPresentation.Plan(ClientMode.OnFoot, false, false, false, 5, 1, DeathFlight.Blast);
        Assert.Equal(DeathStyle.Thrown, p.Style);
        Assert.Equal(0f, p.Blood);
        Assert.Equal(0f, p.BuckleSeconds);
        Assert.True(p.FallSeconds > 0);
    }

    [Fact]
    public void RunOverDeathIsThrownWithBlood()
    {
        var p = DeathPresentation.Plan(ClientMode.OnFoot, false, false, false, 5, 1, DeathFlight.RunOver);
        Assert.Equal(DeathStyle.Thrown, p.Style);
        Assert.Equal(DeathPresentation.FullBlood, p.Blood);
    }

    [Fact]
    public void ThrownDeathOnlyWhereTheCameraIsTheBodys()
    {
        Assert.Equal(DeathStyle.Blood, DeathPresentation.Plan(ClientMode.Seated, false, false, false, 5, 1, DeathFlight.Blast).Style);
        Assert.Equal(DeathStyle.Blood, DeathPresentation.Plan(ClientMode.OnFoot, false, false, true, 5, 1, DeathFlight.Blast).Style); // reduce motion
        Assert.Equal(DeathStyle.Blood, DeathPresentation.Plan(ClientMode.OnFoot, true, false, false, 5, 1, DeathFlight.Blast).Style);  // under water
    }

    [Fact]
    public void ThrownDeathKeepsTheCorpseShownAndTheCameraStill()
    {
        var d = new DeathPresentation();
        d.Begin(DeathPresentation.Plan(ClientMode.OnFoot, false, false, false, 5, 1, DeathFlight.Blast));
        Assert.True(d.OwnsCamera);
        Assert.False(d.HidesOwnCorpse);
        Assert.Equal(DeathPose.None, d.Pose);
        Assert.Equal(0f, d.BloodNow);
        Assert.True(d.Falling);
        d.Tick(5f);
        Assert.True(d.Lying);
        Assert.Equal(0f, d.BloodNow);
        Assert.Equal(DeathPose.None, d.Pose);
    }

    [Fact]
    public void FallDeathStillHidesTheCorpse()
    {
        var d = new DeathPresentation();
        d.Begin(FallPlan());
        Assert.True(d.HidesOwnCorpse);
    }

    [Fact]
    public void PanBlendIsFrameRateIndependent()
    {
        var one = 1f - DeathPresentation.PanBlend(1f / 30f);
        var two = (1f - DeathPresentation.PanBlend(1f / 60f)) * (1f - DeathPresentation.PanBlend(1f / 60f));
        Assert.Equal(one, two, 4);
        Assert.Equal(0f, DeathPresentation.PanBlend(0f));
    }

    [Fact]
    public void ThrownAimStraightAboveKeepsYawAndClampsPitch()
    {
        var (yaw, pitch) = DeathPresentation.ThrownAim(0.05f, 8f, -0.05f, 0.7f);
        Assert.Equal(0.7f, yaw);
        Assert.Equal(DeathPresentation.ThrownMaxPitch, pitch, 4);
        Assert.Equal(-DeathPresentation.ThrownMaxPitch, DeathPresentation.ThrownAim(0, -8f, 0, 0).Pitch, 4);
    }

    [Fact]
    public void ThrownAimTurnsToTheBodyAlongMinusZ()
    {
        Assert.Equal(0f, DeathPresentation.ThrownAim(0, 0, -5f, 1f).Yaw, 4);          // ahead
        Assert.Equal(MathF.PI / 2, DeathPresentation.ThrownAim(-5f, 0, 0, 0).Yaw, 4); // to the left (-X)
        Assert.Equal(MathF.PI / 4, DeathPresentation.ThrownAim(0, 3f, -3f, 0).Pitch, 4);
    }

    [Fact]
    public void PanBlendTurnsQuickerTheFurtherOffTheBody()
    {
        Assert.Equal(DeathPresentation.PanBlend(0.05f), DeathPresentation.PanBlend(0.05f, 0f), 5);
        Assert.True(DeathPresentation.PanBlend(0.05f, 1.5f) > DeathPresentation.PanBlend(0.05f, 0.2f));
        Assert.Equal(DeathPresentation.PanBlend(0.05f, 1.6f), DeathPresentation.PanBlend(0.05f, 3f), 5); // boost caps
    }

    static DeathPlan FallPlan(float delay = 5) => DeathPresentation.Plan(ClientMode.OnFoot, false, false, false, delay, 1);

    [Fact]
    public void On_foot_falls_with_a_share_of_the_respawn_delay_clamped()
    {
        Assert.Equal(DeathStyle.Fall, FallPlan().Style);
        Assert.Equal(1.6f, Total(FallPlan(10)), 3);
        Assert.Equal(1.5f, Total(FallPlan(3)), 3);
        Assert.Equal(0.9f, Total(FallPlan(0.2f)), 3);
    }

    static float Total(DeathPlan p) => p.BuckleSeconds + p.FallSeconds;

    [Fact]
    public void Every_kind_of_fall_comes_up_and_the_forward_one_kneels_longer()
    {
        var kinds = Enumerable.Range(0, 64).Select(DeathPresentation.KindFor).Distinct().ToList();
        Assert.Equal(4, kinds.Count);
        var fwd = Enumerable.Range(0, 64).First(i => DeathPresentation.KindFor(i) == FallKind.Forward);
        var back = Enumerable.Range(0, 64).First(i => DeathPresentation.KindFor(i) == FallKind.Backward);
        var pf = DeathPresentation.Plan(ClientMode.OnFoot, false, false, false, 5, fwd);
        var pb = DeathPresentation.Plan(ClientMode.OnFoot, false, false, false, 5, back);
        Assert.True(pf.BuckleSeconds > pb.BuckleSeconds);
        Assert.Equal(Total(pf), Total(pb), 3);
    }

    [Theory]
    [InlineData(FallKind.Forward)] [InlineData(FallKind.Backward)] [InlineData(FallKind.Left)] [InlineData(FallKind.Right)]
    public void The_buckle_stops_short_of_the_ground_and_the_fall_lands(FallKind k)
    {
        var buckle = DeathPresentation.BucklePose(k, 1f);
        Assert.InRange(buckle.Drop, 0.25f, 0.5f);
        Assert.Equal(1f, DeathPresentation.FallPose(k, 1f).Drop);
        Assert.Equal(buckle.Drop, DeathPresentation.FallPose(k, 0f).Drop, 3); // the two phases join without a jump
        Assert.Equal(buckle.Pitch, DeathPresentation.FallPose(k, 0f).Pitch, 3);
        Assert.Equal(buckle.Roll, DeathPresentation.FallPose(k, 0f).Roll, 3);
    }

    [Fact]
    public void Each_kind_ends_in_its_own_direction()
    {
        Assert.True(DeathPresentation.FallPose(FallKind.Forward, 1f).Pitch < -1f);   // looks at the ground
        Assert.True(DeathPresentation.FallPose(FallKind.Backward, 1f).Pitch > 1f);   // looks at the sky
        Assert.True(DeathPresentation.FallPose(FallKind.Left, 1f).Roll > 1.2f);
        Assert.True(DeathPresentation.FallPose(FallKind.Right, 1f).Roll < -1.2f);
        Assert.True(DeathPresentation.FallPose(FallKind.Forward, 1f).PushM > 0);
        Assert.True(DeathPresentation.FallPose(FallKind.Backward, 1f).PushM < 0);
    }

    [Fact]
    public void Landing_bounces_and_settles()
    {
        var hit = DeathPresentation.LyingPose(FallKind.Left, 0.05f);
        var later = DeathPresentation.LyingPose(FallKind.Left, 1f);
        Assert.True(hit.BounceM > 0.02f);
        Assert.True(later.BounceM < 0.001f);
        Assert.Equal(1f, later.Drop);
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
        Assert.Equal(0f, p.BuckleSeconds);
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
    public void Buckles_falls_then_lies_and_the_scrim_waits_for_the_lying()
    {
        var d = new DeathPresentation();
        Assert.False(d.Active);
        var plan = FallPlan(3);
        d.Begin(plan);
        Assert.True(d.Buckling);
        Assert.True(d.OwnsCamera);
        Assert.InRange(d.BloodNow, 0.01f, plan.Blood * 0.3f);
        d.Tick(plan.BuckleSeconds + 0.01f);
        Assert.True(d.Falling);
        Assert.InRange(d.Drop, 0.25f, 0.6f);
        Assert.Equal(0f, d.ScrimNow);
        d.Tick(plan.FallSeconds);
        Assert.True(d.Lying);
        Assert.Equal(plan.Blood, d.BloodNow);
        Assert.Equal(0f, d.ScrimNow); // the impact settles first
        d.Tick(DeathPresentation.ImpactSeconds * 0.4f + plan.ScrimFadeSeconds / 2);
        Assert.InRange(d.ScrimNow, 0.4f, 0.6f);
        d.Tick(5);
        Assert.Equal(1f, d.ScrimNow);
        Assert.True(d.Lying);
    }

    [Fact]
    public void Blood_only_grows_and_stays_light()
    {
        var d = new DeathPresentation();
        var plan = FallPlan(3);
        d.Begin(plan);
        var last = 0f;
        for (var i = 0; i < 200; i++) { d.Tick(0.01f); Assert.True(d.BloodNow >= last - 1e-5f); last = d.BloodNow; }
        Assert.True(plan.Blood <= 0.4f);
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
        Assert.True(d.Buckling);
        Assert.Equal(0f, d.Drop);
    }

    [Fact]
    public void Respawn_while_lying_and_a_second_death_while_lying_are_legal()
    {
        var d = new DeathPresentation();
        d.Begin(FallPlan(1));
        d.Tick(3);
        Assert.True(d.Lying);
        d.Begin(FallPlan(1)); // a second death without a spawn between: starts over, no illegal edge
        Assert.True(d.Buckling);
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
        d.Tick(5);
        Assert.True(d.Lying);
        Assert.Equal(1f, d.Pose.Drop);
    }

    [Fact]
    public void A_plain_fade_is_lying_at_the_first_tick_with_no_blood()
    {
        var d = new DeathPresentation();
        d.Begin(DeathPresentation.Plan(ClientMode.OnFoot, false, true, false, 5, 1));
        Assert.Equal(0f, d.BloodNow);
        Assert.False(d.OwnsCamera);
        Assert.True(d.Lying); // both beats are zero: straight to Lying
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
        Assert.Equal(DeathPose.None, d.Pose);
    }
}
