using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class MovementTests
{
    const float Dt = 1f / 30f;

    /// <summary>One tick against a flat floor at y = 0 (and an optional drop-through to test falls).</summary>
    static float Tick(ref MoveState s, MoveInput i, float floorY = 0f)
    {
        MovementStep.Step(ref s, i, Dt);
        var p = s.Position + s.Velocity * Dt;
        var v = s.Velocity;
        var floor = false;
        if (p.Y <= floorY && v.Y <= 0) { p.Y = floorY; v.Y = 0; floor = true; }
        return MovementStep.PostMove(ref s, p, v, floor);
    }

    static MoveState Grounded() => new() { OnFloor = true };

    static float HSpeed(in MoveState s) => new Vector2(s.Velocity.X, s.Velocity.Z).Length();

    static MoveState Run(MoveInput i, int ticks = 30)
    {
        var s = Grounded();
        for (var k = 0; k < ticks; k++) Tick(ref s, i);
        return s;
    }

    [Fact]
    public void WalkReaches5() => Assert.Equal(5f, HSpeed(Run(new MoveInput { MoveY = 1 })), 2);

    [Fact]
    public void SprintForwardReaches8() =>
        Assert.Equal(8f, HSpeed(Run(new MoveInput { MoveY = 1, Buttons = MoveButtons.Sprint })), 2);

    [Fact]
    public void CrouchReaches2_5AndLowersCapsule()
    {
        var s = Run(new MoveInput { MoveY = 1, Buttons = MoveButtons.Crouch });
        Assert.Equal(2.5f, HSpeed(s), 2);
        Assert.Equal(Stance.Crouch, s.Stance);
        Assert.Equal(1.2f, MovementStep.CapsuleHeight(Stance.Crouch));
        Assert.Equal(1.8f, MovementStep.CapsuleHeight(Stance.Stand));
    }

    [Fact]
    public void SprintOnlyForward()
    {
        Assert.Equal(5f, HSpeed(Run(new MoveInput { MoveX = 1, Buttons = MoveButtons.Sprint })), 2);
        Assert.Equal(5f, HSpeed(Run(new MoveInput { MoveY = -1, Buttons = MoveButtons.Sprint })), 2);
    }

    [Theory]
    [InlineData(MoveButtons.Fire)]
    [InlineData(MoveButtons.Aim)]
    [InlineData(MoveButtons.Crouch)]
    public void SprintCancelled(MoveButtons also) =>
        Assert.True(HSpeed(Run(new MoveInput { MoveY = 1, Buttons = MoveButtons.Sprint | also })) < 5.01f);

    [Fact]
    public void DiagonalNotFaster() =>
        Assert.Equal(5f, HSpeed(Run(new MoveInput { MoveX = 1, MoveY = 1 })), 2);

    [Fact]
    public void SpeedScaleApplies() =>
        Assert.Equal(4.25f, HSpeed(Run(new MoveInput { MoveY = 1, SpeedScale = 0.85f })), 2);

    [Fact]
    public void YawRotatesDirection()
    {
        // yaw 0 faces -Z; a quarter turn left faces -X
        var s = Run(new MoveInput { MoveY = 1, Yaw = MathF.PI / 2 });
        Assert.True(s.Velocity.X < -4.9f);
        Assert.True(MathF.Abs(s.Velocity.Z) < 0.01f);
        s = Run(new MoveInput { MoveY = 1 });
        Assert.True(s.Velocity.Z < -4.9f);
    }

    [Fact]
    public void JumpPeaksAbout1_1m()
    {
        var s = Grounded();
        var peak = 0f;
        var ticks = 0;
        Tick(ref s, new MoveInput { Buttons = MoveButtons.Jump });
        peak = s.Position.Y;
        while (!s.OnFloor && ticks++ < 200) { Tick(ref s, default); peak = MathF.Max(peak, s.Position.Y); }
        Assert.InRange(peak, 0.95f, 1.25f);
        Assert.True(s.OnFloor);
    }

    [Fact]
    public void NoDoubleJump()
    {
        var s = Grounded();
        Tick(ref s, new MoveInput { Buttons = MoveButtons.Jump });
        for (var k = 0; k < 4; k++) Tick(ref s, new MoveInput { Buttons = MoveButtons.Jump });
        Assert.True(s.Position.Y < 1.3f);
        Assert.False(s.OnFloor);
    }

    [Fact]
    public void AirControlIs30Percent()
    {
        var air = new MoveState { OnFloor = false };
        var ground = Grounded();
        var i = new MoveInput { MoveY = 1 };
        MovementStep.Step(ref air, i, Dt);
        MovementStep.Step(ref ground, i, Dt);
        Assert.Equal(0.3f, HSpeed(air) / HSpeed(ground), 3);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(4f, 0f)]
    [InlineData(9f, 50f)]
    [InlineData(14f, 100f)]
    [InlineData(30f, 100f)]
    public void FallDamageCurve(float drop, float expected) => Assert.Equal(expected, MovementStep.FallDamage(drop), 3);

    [Fact]
    public void LandingAppliesFallDamage()
    {
        var s = new MoveState { Position = new Vector3(0, 9, 0), OnFloor = false, FallPeakY = 9 };
        float dmg = 0;
        for (var k = 0; k < 300 && !s.OnFloor; k++) dmg += Tick(ref s, default);
        Assert.InRange(dmg, 45f, 55f);
    }

    [Fact]
    public void JumpLandingNoDamage()
    {
        var s = Grounded();
        float dmg = Tick(ref s, new MoveInput { Buttons = MoveButtons.Jump });
        for (var k = 0; k < 200 && !s.OnFloor; k++) dmg += Tick(ref s, default);
        Assert.Equal(0f, dmg);
    }

    [Fact]
    public void CannotStandWhenHeadBlocked()
    {
        var s = Run(new MoveInput { Buttons = MoveButtons.Crouch }, 10);
        Assert.Equal(Stance.Crouch, s.Stance);
        s.HeadBlocked = true;
        for (var k = 0; k < 30; k++) Tick(ref s, default); // a second: well past the stand-up time, still blocked
        Assert.Equal(Stance.Crouch, s.Stance);
        s.HeadBlocked = false;
        for (var k = 0; k < 15; k++) Tick(ref s, default); // the 0.35 s stand-up completes
        Assert.Equal(Stance.Stand, s.Stance);
    }
}
