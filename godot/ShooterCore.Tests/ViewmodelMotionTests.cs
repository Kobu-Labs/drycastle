using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>First-person weapon motion (#259): bob, look lag, landing dip, sprint pose, strafe lean, idle breath, the aim damping and Reduce Motion.</summary>
public class ViewmodelMotionTests
{
    const float Dt = 1f / 60f;

    static ViewmodelInput Standing() => new() { Active = true, Grounded = true, Stance = Stance.Stand };
    static ViewmodelInput Walking(float speed = 5f) => Standing() with { Speed = speed };
    static ViewmodelInput Airborne(float vy = -2f) => new() { Active = true, Falling = true, VerticalSpeed = vy, Stance = Stance.Stand };

    /// <summary>Steps <paramref name="seconds"/> at 60 fps with <paramref name="i"/>, calling <paramref name="each"/> after every step.</summary>
    static void Run(ViewmodelMotion m, in ViewmodelInput i, float seconds, Action<ViewmodelMotion>? each = null)
    {
        for (var t = 0f; t < seconds - 1e-4f; t += Dt) { m.Step(Dt, i); each?.Invoke(m); }
    }

    [Fact]
    public void StandingStillOnlyBreathesUnderThreeMillimetres()
    {
        var m = new ViewmodelMotion();
        float maxX = 0, maxY = 0, maxRot = 0;
        Run(m, Standing(), 20f, mm =>
        {
            maxX = Math.Max(maxX, Math.Abs(mm.Position.X)); maxY = Math.Max(maxY, Math.Abs(mm.Position.Y));
            maxRot = Math.Max(maxRot, Math.Abs(mm.Rotation.X) + Math.Abs(mm.Rotation.Y) + Math.Abs(mm.Rotation.Z));
        });
        Assert.InRange(maxX, 0.0015f, 0.003f);   // it does breathe...
        Assert.InRange(maxY, 0.002f, 0.003f);    // ...by under 3 mm
        Assert.Equal(0f, maxRot);
        Assert.Equal(0f, m.CameraDrop);
        Assert.Equal(0f, m.Position.Z);
    }

    [Fact]
    public void AimingZeroesEverythingButTheKick()
    {
        var m = new ViewmodelMotion();
        var busy = Walking(8f) with { Sprinting = true, StrafeSpeed = 4f, LookYaw = 0.3f, LookPitch = 0.2f, Aim = 1f };
        Run(m, busy, 1f);
        // a landing while aimed, too
        Run(m, Airborne(-9f) with { Aim = 1f }, 0.3f);
        Run(m, busy with { StrafeSpeed = 0 }, 0.5f, mm =>
        {
            Assert.Equal(System.Numerics.Vector3.Zero, mm.Position);
            Assert.Equal(System.Numerics.Vector3.Zero, mm.Rotation);
        });
    }

    [Fact]
    public void TheOffsetsFadeOutWithTheAimAmount()
    {
        var hip = new ViewmodelMotion(); var half = new ViewmodelMotion();
        var walk = Walking(8f) with { Sprinting = true };
        Run(hip, walk, 1f); Run(half, walk with { Aim = 0.5f }, 1f);
        Assert.True(hip.Position.Length() > 0.05f);
        Assert.Equal(hip.Position.Length() * 0.5f, half.Position.Length(), 3);
    }

    [Fact]
    public void BobSwingsSideToSideAndDipsTwiceAcrossTwoSteps()
    {
        var m = new ViewmodelMotion();
        Run(m, Walking(5f), 0.5f);                       // eased in
        float maxX = 0, minY = 0, maxY = 0; var dips = 0; var prev = 0f; var falling = false;
        Run(m, Walking(5f), 4f, mm =>
        {
            maxX = Math.Max(maxX, Math.Abs(mm.Position.X)); minY = Math.Min(minY, mm.Position.Y); maxY = Math.Max(maxY, mm.Position.Y);
            var up = mm.Position.Y > prev; // a dip is a turn from going down to going up, below zero
            if (falling && up && prev < -0.004f) dips++;
            falling = !up; prev = mm.Position.Y;
        });
        Assert.InRange(maxX, 0.0115f, 0.0121f);          // the walk amplitude
        Assert.InRange(minY, -0.0061f, -0.0055f);        // half of it, down
        Assert.InRange(maxY, 0.0055f, 0.0061f);
        // 5 m/s over 4 s is 20 m: 10.5 strides of 1.9 m, one dip each
        Assert.InRange(dips, 10, 11);
    }

    [Fact]
    public void SprintBobIsWiderThanWalkBob()
    {
        float Peak(float speed)
        {
            var m = new ViewmodelMotion(); float peak = 0;
            Run(m, Walking(speed), 1f);
            Run(m, Walking(speed), 2f, mm => peak = Math.Max(peak, Math.Abs(mm.Position.X)));
            return peak;
        }
        Assert.InRange(Peak(MoveConsts.SprintSpeed), 0.0195f, 0.0201f);
        Assert.True(Peak(MoveConsts.SprintSpeed) > Peak(MoveConsts.WalkSpeed));
        Assert.Equal(ViewmodelMotion.WalkBob / 2, ViewmodelMotion.BobAmplitude(2.5f), 5);
        Assert.Equal(0f, ViewmodelMotion.BobAmplitude(0f));
    }

    /// <summary>
    /// Plays the own footstep sound the way <c>NetClient.SfxOwnMovement</c> does (<see cref="SoundRules.FootstepDue"/> on the 30 Hz tick) next to the 60 fps
    /// bob, which is handed the sound's accumulator. Returns how many footsteps sounded after <paramref name="settle"/> seconds and how many of them
    /// found the gun in the lower part of a dip.
    /// </summary>
    static (int Steps, int Low) Footsteps(ViewmodelMotion m, ref float acc, float speed, float seconds, float settle, bool grounded = true)
    {
        int steps = 0, low = 0, frame = 0;
        var amp = ViewmodelMotion.BobAmplitude(speed);
        for (var t = 0f; t < seconds; t += Dt, frame++)
        {
            var i = (grounded ? Walking(speed) : Airborne() with { Speed = speed }) with { StepAcc = acc, HasStepAcc = true };
            m.Step(Dt, i);
            if (frame % 2 == 0 && SoundRules.FootstepDue(ref acc, speed, 1f / 30f, grounded) && t > settle)
            {
                steps++;
                if (m.Position.Y < -0.5f * amp * 0.5f) low++; // below half the dip's depth: within about 0.5 rad of its bottom
            }
        }
        return (steps, low);
    }

    [Fact]
    public void TheDipLandsOnEachFootstepSound()
    {
        var m = new ViewmodelMotion(); var acc = 0f;
        var (steps, low) = Footsteps(m, ref acc, 6f, 10f, 0.6f);
        Assert.InRange(steps, 28, 33);
        Assert.Equal(steps, low);
    }

    [Fact]
    public void TheDipStaysOnTheSoundAfterAStopAndAStart()
    {
        // stopping and starting again clamps the sound's accumulator (SoundRules.FootstepDue), which moves its steps by up to half a stride
        var m = new ViewmodelMotion(); var acc = 0f;
        Footsteps(m, ref acc, 5f, 3.3f, 99f);                        // stops half way through a stride
        for (var t = 0f; t < 0.4f; t += Dt) { m.Step(Dt, Standing() with { StepAcc = acc, HasStepAcc = true }); SoundRules.FootstepDue(ref acc, 0f, Dt, true); }
        Assert.True(acc <= SoundRules.WalkStride * 0.5f + 1e-4f);
        var (steps, low) = Footsteps(m, ref acc, 5f, 6f, 2.5f);      // pulled round within a couple of seconds
        Assert.InRange(steps, 8, 10);
        Assert.Equal(steps, low);
    }

    [Fact]
    public void TheFirstStepAfterStandingAlsoDips()
    {
        var m = new ViewmodelMotion(); var acc = 0.6f;               // wherever the sound's accumulator was left
        Run(m, Standing() with { StepAcc = acc, HasStepAcc = true }, 1f);
        var (steps, low) = Footsteps(m, ref acc, 5f, 1.5f, 0f);
        Assert.InRange(steps, 3, 4);                                 // 7.5 m from 0.6 m into a stride: 1.3, 3.2, 5.1, 7.0
        Assert.Equal(steps, low);
    }

    [Fact]
    public void WithoutTheSoundsAccumulatorTheBobJustCountsDistance()
    {
        // sounds off (or a build without them): the accumulator never moves, and must not hold the bob still
        var m = new ViewmodelMotion(); var dips = 0; var prev = 0f; var falling = false;
        Run(m, Walking(5f), 1f);
        Run(m, Walking(5f), 3.8f, mm => { var up = mm.Position.Y > prev; if (falling && up && prev < -0.004f) dips++; falling = !up; prev = mm.Position.Y; });
        Assert.InRange(dips, 9, 11);                                 // 19 m at one dip per 1.9 m
    }

    [Fact]
    public void NoBobInTheAirOrProneOrWhenSlow()
    {
        foreach (var i in new[] { Airborne() with { Speed = 5f }, Walking(5f) with { Stance = Stance.Prone }, Walking(0.5f) })
        {
            var m = new ViewmodelMotion();
            Run(m, Walking(5f), 1f);                     // bobbing...
            Run(m, i, 0.3f);                             // ...then not, after the ease out (0.15 s)
            float maxX = 0;
            Run(m, i, 2f, mm => maxX = Math.Max(maxX, Math.Abs(mm.Position.X)));
            Assert.True(maxX <= ViewmodelMotion.IdleX + 1e-5f, $"x {maxX}"); // at most the idle breath
        }
    }

    [Fact]
    public void BobFadesInAndOutRatherThanSnapping()
    {
        var m = new ViewmodelMotion();
        Run(m, Standing(), 1f);
        m.Step(Dt, Walking(5f));
        Assert.True(Math.Abs(m.Position.X) < 0.004f);     // not at full swing on the first frame
        Run(m, Walking(5f), 1f);
        float before = 0;
        Run(m, Standing(), 0.05f, mm => before = Math.Max(before, Math.Abs(mm.Position.X)));
        Assert.True(before > 0f);
        Run(m, Standing(), 0.2f);
        Assert.True(Math.Abs(m.Position.X) <= ViewmodelMotion.IdleX + 1e-5f);
    }

    [Fact]
    public void LandingDipPeaksThenReturns()
    {
        var m = new ViewmodelMotion();
        Run(m, Standing(), 0.5f);
        Run(m, Airborne(-8f), 0.3f);
        float peak = 0, peakAt = 0, cam = 0, t = 0;
        // grounded again from here: the dip is measured against the breath it rides on, so compare with a still twin
        var still = new ViewmodelMotion();
        Run(still, Standing(), 0.8f);
        for (; t < 1f; t += Dt)
        {
            m.Step(Dt, Standing()); still.Step(Dt, Standing());
            var d = still.Position.Y - m.Position.Y; // downward
            if (d > peak) { peak = d; peakAt = t; }
            cam = Math.Max(cam, m.CameraDrop);
        }
        Assert.InRange(peak, 0.05f, 0.062f);              // min(0.06, 8 * 0.008)
        Assert.InRange(peakAt, 0.02f, 0.12f);            // it sinks first, about 1 / 18 s
        Assert.InRange(cam, 0.03f, ViewmodelMotion.LandMax * ViewmodelMotion.CameraShare + 1e-4f);
        Assert.True(still.Position.Y - m.Position.Y < 0.001f);   // and is back inside a second
        Assert.True(m.CameraDrop < 0.0005f);
    }

    [Fact]
    public void ADeeperFallDipsMoreUpToTheCap()
    {
        float Peak(float fall)
        {
            var m = new ViewmodelMotion(); float peak = 0;
            Run(m, Airborne(-fall), 0.3f);
            Run(m, Walking(0.2f) with { Speed = 0 }, 0.6f, mm => peak = Math.Max(peak, -mm.Position.Y + ViewmodelMotion.IdleY));
            return peak;
        }
        Assert.True(Peak(5f) < Peak(7f));
        Assert.InRange(Peak(7f), 0.05f, 0.062f);
        Assert.InRange(Peak(30f), 0.05f, 0.0625f);        // capped
    }

    [Fact]
    public void ABumpOrWaterOrALadderIsNoLanding()
    {
        var bump = new ViewmodelMotion();
        Run(bump, Airborne(-2f), 0.1f); Run(bump, Standing(), 0.5f, mm => Assert.Equal(0f, mm.CameraDrop));
        // fast through water or on a ladder (neither falling nor grounded), then on the ground: not a fall
        var wet = new ViewmodelMotion();
        Run(wet, Airborne(-12f), 0.2f);
        Run(wet, new ViewmodelInput { Active = true, VerticalSpeed = 0.5f }, 1f);   // swimming or climbing
        Run(wet, Standing(), 0.5f, mm => Assert.Equal(0f, mm.CameraDrop));
    }

    [Fact]
    public void LookLagTrailsTheViewAndSettlesWithinAThirdOfASecond()
    {
        var m = new ViewmodelMotion();
        Run(m, Standing(), 1f);
        var rest = m.Position;
        m.Step(Dt, Standing() with { LookYaw = -0.2f });          // turned right (yaw down): the gun lags to the left
        Assert.True(m.Position.X < rest.X - 0.008f);
        Assert.True(m.Rotation.Z < 0f);                           // the ticket's roll, same sign as the turn
        var peakX = rest.X - m.Position.X;
        Run(m, Standing(), 0.3f);
        Assert.True(Math.Abs(m.Position.X - ViewmodelMotion.IdleX * MathF.Sin(ViewmodelMotion.IdleRateX * 1.36f)) < peakX * 0.06f + 1e-4f);
        Assert.True(Math.Abs(m.Rotation.Z) < 0.003f);
    }

    [Fact]
    public void LookLagDirectionsAndClamp()
    {
        var left = new ViewmodelMotion(); left.Step(Dt, Standing() with { LookYaw = 0.1f });
        Assert.True(left.Position.X > 0.004f);                    // turning left: the gun trails to the right
        var up = new ViewmodelMotion(); up.Step(Dt, Standing() with { LookPitch = 0.1f });
        Assert.True(up.Position.Y < -0.004f);                     // looking up: it trails down
        var down = new ViewmodelMotion(); down.Step(Dt, Standing() with { LookPitch = -0.1f });
        Assert.True(down.Position.Y > 0.004f);
        var flick = new ViewmodelMotion();
        for (var n = 0; n < 30; n++) flick.Step(Dt, Standing() with { LookYaw = 0.5f, LookPitch = 0.5f });
        Assert.InRange(flick.Position.X, 0.029f, 0.0301f + ViewmodelMotion.IdleX);
        Assert.True(flick.Position.Y > -0.0301f - ViewmodelMotion.IdleY);
        Assert.InRange(flick.Rotation.Z, 0.049f, 0.0501f);
    }

    [Fact]
    public void TheSameTurnGivesTheSameLagAtAnyFrameRate()
    {
        float Lag(float fps)
        {
            var m = new ViewmodelMotion(); var dt = 1f / fps;
            for (var t = 0f; t < 1f - 1e-4f; t += dt) m.Step(dt, Standing() with { LookYaw = 1.5f * dt }); // 1.5 rad/s for a second
            return m.Position.X - 0.0f;
        }
        Assert.Equal(Lag(30f), Lag(144f), 3);                      // millimetres apart (the idle breath differs a hair)
    }

    [Fact]
    public void SprintPoseEasesInAndBackOut()
    {
        var m = new ViewmodelMotion();
        var sprint = Walking(8f) with { Sprinting = true };
        Run(m, sprint, 0.05f);
        var early = m.Rotation.Y;
        Assert.InRange(early, 0.001f, ViewmodelMotion.SprintYaw * 0.5f);   // on its way
        Run(m, sprint, 0.5f);
        Assert.Equal(ViewmodelMotion.SprintYaw, m.Rotation.Y, 4);          // inward: the muzzle toward the centre of the view
        Assert.Equal(ViewmodelMotion.SprintRoll, m.Rotation.Z, 2);
        Assert.InRange(m.Position.Y, ViewmodelMotion.SprintOffset.Y - 0.03f, ViewmodelMotion.SprintOffset.Y + 0.03f);   // lowered
        Assert.True(m.Position.X < 0 && m.Position.Z > 0);                 // in toward the middle and back toward the player
        Run(m, Walking(8f), 0.14f);
        Assert.Equal(0f, m.Rotation.Y, 4);                                 // gone within the 0.12 s way back
        Assert.Equal(0f, new ViewmodelMotion().Rotation.Y);
    }

    [Fact]
    public void NoSprintPoseAgainstAWallOrWhileCrouched()
    {
        foreach (var i in new[] { Walking(0.2f) with { Sprinting = true }, Walking(2.5f) with { Sprinting = true, Stance = Stance.Crouch } })
        {
            var m = new ViewmodelMotion();
            Run(m, i, 0.6f);
            Assert.Equal(0f, m.Rotation.Y);
        }
    }

    [Fact]
    public void StrafingLeansAgainstTheSidewaysSpeed()
    {
        var right = new ViewmodelMotion(); var left = new ViewmodelMotion(); var fwd = new ViewmodelMotion();
        Run(right, Walking(5f) with { StrafeSpeed = 5f }, 1f); Run(left, Walking(5f) with { StrafeSpeed = -5f }, 1f); Run(fwd, Walking(5f), 1f);
        Assert.Equal(0f, fwd.Rotation.Z, 4);
        Assert.Equal(ViewmodelMotion.LeanRoll, right.Rotation.Z, 3);
        Assert.Equal(-ViewmodelMotion.LeanRoll, left.Rotation.Z, 3);
    }

    [Fact]
    public void ReduceMotionKeepsTheStateCuesOnly()
    {
        var m = new ViewmodelMotion();
        var busy = Walking(5f) with { StrafeSpeed = 5f, LookYaw = 0.2f, LookPitch = 0.1f, ReduceMotion = true };
        Run(m, busy, 0.5f);
        Run(m, busy with { LookYaw = 0, LookPitch = 0 }, 1f, mm =>
        {
            Assert.Equal(System.Numerics.Vector3.Zero, mm.Position);   // no bob, lag, lean or idle
            Assert.Equal(System.Numerics.Vector3.Zero, mm.Rotation);
        });
        var idle = new ViewmodelMotion();
        Run(idle, Standing() with { ReduceMotion = true }, 3f, mm => Assert.Equal(System.Numerics.Vector3.Zero, mm.Position));
        // the sprint pose stays
        var sprint = new ViewmodelMotion();
        Run(sprint, Walking(8f) with { Sprinting = true, ReduceMotion = true }, 0.5f);
        Assert.Equal(ViewmodelMotion.SprintYaw, sprint.Rotation.Y, 4);
        // the landing: a small dip of the gun, none of the eye
        float normalPeak = 0, calmPeak = 0, calmCam = 0;
        var a = new ViewmodelMotion(); var b = new ViewmodelMotion();
        Run(a, Airborne(-8f), 0.3f); Run(b, Airborne(-8f) with { ReduceMotion = true }, 0.3f);
        Run(a, Standing() with { Speed = 0 }, 0.5f, mm => normalPeak = Math.Max(normalPeak, -mm.Position.Y));
        Run(b, Standing() with { ReduceMotion = true }, 0.5f, mm => { calmPeak = Math.Max(calmPeak, -mm.Position.Y); calmCam = Math.Max(calmCam, mm.CameraDrop); });
        Assert.InRange(calmPeak, 0.01f, normalPeak * 0.5f);
        Assert.Equal(0f, calmCam);
    }

    [Fact]
    public void AnInactiveFrameResetsSoNothingStalePlaysOnRespawn()
    {
        var m = new ViewmodelMotion();
        Run(m, Airborne(-9f), 0.3f);
        m.Step(Dt, Standing() with { LookYaw = 0.3f });             // landed: dipping and lagging
        Assert.True(m.CameraDrop > 0f || m.Position.Y < 0f);
        m.Step(Dt, default);                                         // hidden (dead, scoped, seated)
        Assert.Equal(System.Numerics.Vector3.Zero, m.Position);
        Assert.Equal(System.Numerics.Vector3.Zero, m.Rotation);
        Assert.Equal(0f, m.CameraDrop);
        // shown again on the ground: no landing from before, no lag, and a fall that started while hidden is not carried in
        m.Step(Dt, Standing());
        Assert.True(Math.Abs(m.Position.X) < 0.001f && m.CameraDrop == 0f);
        Run(m, Standing(), 0.5f, mm => Assert.Equal(0f, mm.CameraDrop));
    }

    [Fact]
    public void AHitchDoesNotFlingTheSprings()
    {
        var m = new ViewmodelMotion();
        Run(m, Airborne(-9f), 0.3f);
        m.Step(5f, Standing() with { LookYaw = 3f });
        Assert.True(m.Position.Length() < 0.1f);
        Assert.True(m.CameraDrop <= ViewmodelMotion.LandMax);
    }
}
