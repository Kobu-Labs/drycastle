using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class WaterTests
{
    const float Dt = 1f / 30f;

    /// <summary>
    /// A 40 m square lake in local metres (x east, y north) from (0,0) to (40,40): surface 10.0 m, bed 8.0 m, so depth 2 m.
    /// Godot ground axes are x east, z = -north, so the body covers x 0..40 and z -40..0.
    /// A second, shallow pond (0.8 m deep) sits at x 100..110.
    /// </summary>
    const string Json = """
    {
      "version": 1,
      "bodies": [
        { "id": "P-0001", "polygon": { "type": "Polygon", "coordinates": [[[0,0],[40,0],[40,40],[0,40],[0,0]]] }, "surface_y": 10.0, "bed_y": 8.0 },
        { "id": "P-0002", "polygon": { "type": "Polygon", "coordinates": [[[100,0],[110,0],[110,10],[100,10],[100,0]]] }, "surface_y": 10.0, "bed_y": 9.2 }
      ]
    }
    """;

    static Water Lake() => Water.Parse(Json);

    static MoveInput In(MoveButtons b = MoveButtons.None, float mx = 0, float my = 0) =>
        new() { Buttons = b, MoveX = mx, MoveY = my, SpeedScale = 1 };

    /// <summary>Feet at (x, feetY, z) in Godot axes, state sampled from the water like the client does each tick.</summary>
    static MoveState At(Water w, float x, float feetY, float z, bool crouching = false)
    {
        var s = new MoveState { Position = new Vector3(x, feetY, z), OnFloor = false, Stance = crouching ? Stance.Crouch : Stance.Stand, FallPeakY = feetY };
        w.Sample(ref s);
        return s;
    }

    /// <summary>One movement tick with no collision (feet move by velocity), then the post-move bookkeeping.</summary>
    static float Tick(ref MoveState s, Water w, MoveInput i)
    {
        w.Sample(ref s);
        MovementStep.Step(ref s, i, Dt);
        var p = s.Position + s.Velocity * Dt;
        return MovementStep.PostMove(ref s, p, s.Velocity, false, w.DepthAt(p));
    }

    [Fact]
    public void ParseReadsBodiesAndConvertsRingsToGodotAxes()
    {
        var w = Lake();
        Assert.Equal(2, w.Bodies.Count);
        Assert.Equal(new Vector2(40, -40), w.Bodies[0].Rings[0][2]); // north 40 becomes z -40
        Assert.Equal(2f, w.Bodies[0].Depth, 3);
    }

    [Fact]
    public void ParseRejectsWrongVersionAndMissingBodies()
    {
        Assert.Throws<DataFileException>(() => Water.Parse("""{"version": 2, "bodies": []}"""));
        Assert.Throws<DataFileException>(() => Water.Parse("""{"version": 1}"""));
        Assert.Throws<DataFileException>(() => Water.Parse("not json"));
    }

    [Fact]
    public void DepthAndSurfaceQueriesAreZeroAndNaNOnDryGround()
    {
        var w = Lake();
        Assert.Equal(2f, w.Depth(20, -20), 3);
        Assert.Equal(10f, w.SurfaceY(20, -20), 3);
        Assert.Equal(0f, w.Depth(60, -20), 3);
        Assert.True(float.IsNaN(w.SurfaceY(60, -20)));
        Assert.Null(w.BodyAt(-1, -20));
    }

    [Fact]
    public void EmptyWaterIsDry()
    {
        Assert.Equal(0f, Water.Empty.Depth(0, 0));
        var s = new MoveState { Position = new Vector3(0, 0, 0) };
        Water.Empty.Sample(ref s);
        Assert.Equal(0f, s.WaterDepth);
        Assert.False(s.HeadUnder);
    }

    [Fact]
    public void FeetAboveTheSurfaceAreNotInTheWater()
    {
        var w = Lake();
        // over the lake, feet in the air: no depth, so a bridge or a jump above the water is dry
        Assert.Equal(0f, w.DepthAt(new Vector3(20, 10.5f, -20)));
        Assert.Equal(2f, w.DepthAt(new Vector3(20, 9f, -20)));
    }

    [Fact]
    public void SampleFlagsWadingSwimmingAndHeadUnder()
    {
        var w = Lake();
        // shallow pond: depth 0.8 m is wading, not swimming
        var pond = At(w, 105, 9.5f, -5);
        Assert.Equal(0.8f, pond.WaterDepth, 3);
        Assert.False(MovementStep.IsSwimming(pond));

        // deep lake, feet at 9 m: swimming, head (9 + 1.65) is 10.65 above the 10 m surface, so not under
        var floating = At(w, 20, 9f, -20);
        Assert.True(MovementStep.IsSwimming(floating));
        Assert.False(floating.HeadUnder);

        // feet at 8.2 m: head at 9.85 is under the 10 m surface
        var under = At(w, 20, 8.2f, -20);
        Assert.True(under.HeadUnder);

        // crouching lowers the eyes to 1.05 m: feet at 8.9 m put the head under only when crouched (9.95 < 10)
        Assert.False(At(w, 20, 8.9f, -20).HeadUnder);
        Assert.True(At(w, 20, 8.9f, -20, crouching: true).HeadUnder);
    }

    [Fact]
    public void WadingWalksSlowerThanDryLand()
    {
        var w = Lake();
        var dry = new MoveState { OnFloor = true, Position = new Vector3(60, 0, -20) };
        var wading = new MoveState { OnFloor = true, Position = new Vector3(105, 9.5f, -5) };
        // 15 ticks: the speed is capped within 5 ticks and the walker stays inside the 10 m pond
        for (var k = 0; k < 15; k++) { Tick(ref dry, w, In(MoveButtons.None, 0, 1)); Tick(ref wading, w, In(MoveButtons.None, 0, 1)); }
        var dryH = new Vector2(dry.Velocity.X, dry.Velocity.Z).Length();
        var wetH = new Vector2(wading.Velocity.X, wading.Velocity.Z).Length();
        Assert.Equal(WaterConsts.WadeSpeedScale * dryH, wetH, 2);
    }

    [Fact]
    public void SwimSpeedIsCappedAtSwimAndSprintSwim()
    {
        var w = Lake();
        var swim = At(w, 20, 9f, -20);
        for (var k = 0; k < 60; k++) Tick(ref swim, w, In(MoveButtons.None, 0, 1));
        Assert.Equal(WaterConsts.SwimSpeed, new Vector2(swim.Velocity.X, swim.Velocity.Z).Length(), 2);

        var sprint = At(w, 20, 9f, -20);
        for (var k = 0; k < 60; k++) Tick(ref sprint, w, In(MoveButtons.Sprint, 0, 1));
        Assert.Equal(WaterConsts.SwimSprintSpeed, new Vector2(sprint.Velocity.X, sprint.Velocity.Z).Length(), 2);
    }

    [Fact]
    public void SwimmingHasNoGravity()
    {
        var w = Lake();
        var s = At(w, 20, 9f, -20);
        for (var k = 0; k < 60; k++) Tick(ref s, w, In());
        Assert.Equal(0f, s.Velocity.Y, 3);
        Assert.Equal(9f, s.Position.Y, 2);
    }

    [Fact]
    public void JumpRisesAndCrouchDivesWhileSwimming()
    {
        var w = Lake();
        var up = At(w, 20, 9f, -20);
        // a third of a second: the feet are still under the 10 m surface, so the swim (not gravity) drives the rise
        for (var k = 0; k < 10; k++) Tick(ref up, w, In(MoveButtons.Jump));
        Assert.True(up.Position.Y > 9.3f);
        Assert.True(up.Velocity.Y > 0f);

        var down = At(w, 20, 9.5f, -20);
        for (var k = 0; k < 30; k++) Tick(ref down, w, In(MoveButtons.Crouch));
        Assert.True(down.Position.Y < 9.0f);
        Assert.Equal(Stance.Crouch, down.Stance);
        Assert.True(down.Velocity.Y < 0f);
    }

    [Fact]
    public void CrouchInDeepWaterDivesAtSwimSpeedNotCrouchWalk()
    {
        var w = Lake();
        var s = At(w, 20, 9f, -20);
        for (var k = 0; k < 60; k++) Tick(ref s, w, In(MoveButtons.Crouch, 0, 1));
        Assert.Equal(WaterConsts.SwimSpeed, new Vector2(s.Velocity.X, s.Velocity.Z).Length(), 2);
    }

    [Fact]
    public void LandingInWaterDeepEnoughDoesNoFallDamage()
    {
        var s = new MoveState { Position = new Vector3(20, 14, -20), OnFloor = false, FallPeakY = 14 };
        // the bed is 8 m and the surface 10 m: landing on the bed in 2 m of water
        var dmg = MovementStep.PostMove(ref s, new Vector3(20, 8, -20), Vector3.Zero, true, landingWaterDepth: 2f);
        Assert.Equal(0f, dmg);

        var dry = new MoveState { Position = new Vector3(60, 14, -20), OnFloor = false, FallPeakY = 14 };
        Assert.True(MovementStep.PostMove(ref dry, new Vector3(60, 8, -20), Vector3.Zero, true) > 0f);
    }

    [Fact]
    public void BreathLastsTwentySecondsThenDrowns()
    {
        var w = Lake();
        var s = At(w, 20, 8.2f, -20); // head under
        var damage = 0f;
        for (var k = 0; k < 19 * 30; k++) damage += Breath.Tick(ref s, Dt); // 19 s: air still left
        Assert.Equal(0f, damage);
        Assert.Equal(1f, Breath.AirLeft(s), 1);

        for (var k = 0; k < 30; k++) damage += Breath.Tick(ref s, Dt);      // to 20 s and past it
        Assert.Equal(0f, Breath.AirLeft(s), 2);

        var drown = 0f;
        for (var k = 0; k < 30; k++) drown += Breath.Tick(ref s, Dt);       // one second without air
        Assert.Equal(Breath.DrowningDamagePerSecond, drown, 2);
    }

    [Fact]
    public void SurfacingRefillsBreathQuickly()
    {
        var s = new MoveState { HeadUnder = true };
        for (var k = 0; k < 15 * 30; k++) Breath.Tick(ref s, Dt); // 15 s under: 5 s of air left
        Assert.Equal(5f, Breath.AirLeft(s), 1);
        s.HeadUnder = false;
        for (var k = 0; k < 30; k++) Breath.Tick(ref s, Dt);      // 1 s above: refills 5 s
        Assert.Equal(10f, Breath.AirLeft(s), 1);
        for (var k = 0; k < 3 * 30; k++) Breath.Tick(ref s, Dt);
        Assert.Equal(Breath.Capacity, Breath.AirLeft(s), 3);      // full after about 4 s above water in total
    }

    [Fact]
    public void BreathHudTextAndLowState()
    {
        Assert.Equal("12.3 s", HudLogic.BreathText(12.34f));
        Assert.Equal("0.0 s", HudLogic.BreathText(-0.2f));
        Assert.True(HudLogic.BreathLow(5f));
        Assert.False(HudLogic.BreathLow(5.1f));
    }

    [Fact]
    public void DryPlayerHasNoDrowningDamage()
    {
        var s = new MoveState();
        for (var k = 0; k < 600; k++) Assert.Equal(0f, Breath.Tick(ref s, Dt));
        Assert.Equal(0f, s.AirSpent);
    }
}
