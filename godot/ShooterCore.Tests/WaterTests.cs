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

    /// <summary>The feet of a floating body on the 10 m lake: the surface less 1.25 m (eyes 0.4 m above it).</summary>
    const float FloatFeetY = 8.75f;

    static Water Lake() => Water.Parse(Json);

    static MoveInput In(MoveButtons b = MoveButtons.None, float mx = 0, float my = 0) =>
        new() { Buttons = b, MoveX = mx, MoveY = my, SpeedScale = 1 };

    /// <summary>Feet at (x, feetY, z) in Godot axes, state sampled from the water like the client does each tick.</summary>
    static MoveState At(Water w, float x, float feetY, float z)
    {
        var s = new MoveState { Position = new Vector3(x, feetY, z), OnFloor = false, FallPeakY = feetY };
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
    public void SampleFlagsWadingAndSwimmingAndTheShoreDistance()
    {
        var w = Lake();
        // shallow pond: depth 0.8 m is wading, not swimming, and the shore distance is not needed
        var pond = At(w, 105, 9.5f, -5);
        Assert.Equal(0.8f, pond.WaterDepth, 3);
        Assert.False(MovementStep.IsSwimming(pond));
        Assert.Equal(float.MaxValue, pond.ShoreDistance);

        // deep lake, feet at 9 m: swimming; the lake's shore is 1 m away from x = 1
        var floating = At(w, 1, 9f, -20);
        Assert.True(MovementStep.IsSwimming(floating));
        Assert.Equal(1f, floating.ShoreDistance, 3);

        // in the middle of the lake the shore is 20 m away
        Assert.Equal(20f, At(w, 20, 9f, -20).ShoreDistance, 3);
    }

    [Fact]
    public void WadingSpeedFollowsTheDepthCurve()
    {
        var w = Lake();
        var dry = new MoveState { OnFloor = true, Position = new Vector3(60, 0, -20) };
        var wading = new MoveState { OnFloor = true, Position = new Vector3(105, 9.5f, -5) };
        // 15 ticks: the speed is capped within 5 ticks and the walker stays inside the 10 m pond (depth 0.8 m)
        for (var k = 0; k < 15; k++) { Tick(ref dry, w, In(MoveButtons.None, 0, 1)); Tick(ref wading, w, In(MoveButtons.None, 0, 1)); }
        var dryH = new Vector2(dry.Velocity.X, dry.Velocity.Z).Length();
        var wetH = new Vector2(wading.Velocity.X, wading.Velocity.Z).Length();
        Assert.Equal(Wading.Speed(MoveConsts.WalkSpeed, 0.8f), wetH, 2);
        Assert.True(wetH < dryH && wetH > WaterConsts.SwimSpeed);
    }

    [Fact]
    public void SwimSpeedIsTheSwimSpeedWithOrWithoutSprint()
    {
        var w = Lake();
        var swim = At(w, 20, 9f, -20);
        for (var k = 0; k < 60; k++) Tick(ref swim, w, In(MoveButtons.None, 0, 1));
        Assert.Equal(WaterConsts.SwimSpeed, new Vector2(swim.Velocity.X, swim.Velocity.Z).Length(), 2);

        // sprint is off in deep water: the same speed
        var sprint = At(w, 20, 9f, -20);
        for (var k = 0; k < 60; k++) Tick(ref sprint, w, In(MoveButtons.Sprint, 0, 1));
        Assert.Equal(WaterConsts.SwimSpeed, new Vector2(sprint.Velocity.X, sprint.Velocity.Z).Length(), 2);
        Assert.False(sprint.WasSprinting);
    }

    [Fact]
    public void SwimmingFloatsWithEyesFourTenthsAboveTheSurface()
    {
        var w = Lake();
        var s = At(w, 20, 9f, -20);
        for (var k = 0; k < 60; k++) Tick(ref s, w, In());
        Assert.Equal(FloatFeetY, s.Position.Y, 3);
        Assert.Equal(0f, s.Velocity.Y, 3);
        Assert.Equal(WaterConsts.SwimEyesAboveSurface, s.Position.Y + MovementStep.EyeHeight(s.Stance) - 10f, 3);
    }

    [Fact]
    public void NoVerticalInputWhileSwimming()
    {
        var w = Lake();
        // holding jump or crouch changes nothing: the body floats at the same height, with no vertical speed
        foreach (var held in new[] { MoveButtons.Jump, MoveButtons.Crouch, MoveButtons.Jump | MoveButtons.Crouch })
        {
            var s = At(w, 20, FloatFeetY, -20);
            for (var k = 0; k < 60; k++) Tick(ref s, w, In(held, 0, 1));
            Assert.Equal(FloatFeetY, s.Position.Y, 3);
            Assert.Equal(0f, s.Velocity.Y, 3);
            Assert.Equal(Stance.Stand, s.Stance);
        }
    }

    [Fact]
    public void CrouchInDeepWaterStandsUpAndSwimsAtSwimSpeed()
    {
        var w = Lake();
        var s = At(w, 20, 9f, -20);
        for (var k = 0; k < 60; k++) Tick(ref s, w, In(MoveButtons.Crouch, 0, 1));
        Assert.Equal(Stance.Stand, s.Stance);
        Assert.Equal(WaterConsts.SwimSpeed, new Vector2(s.Velocity.X, s.Velocity.Z).Length(), 2);
    }

    [Fact]
    public void JumpNearTheBankHopsOutOfTheWater()
    {
        var w = Lake();
        var s = At(w, 1, FloatFeetY, -20); // 1 m from the west shore
        Tick(ref s, w, In(MoveButtons.Jump));
        Assert.True(s.Hopping);
        Assert.True(s.Velocity.Y > 0f);

        var peak = s.Position.Y;
        for (var k = 0; k < 60; k++)
        {
            Tick(ref s, w, In());
            peak = MathF.Max(peak, s.Position.Y);
        }
        Assert.True(peak > 10.1f, $"the hop clears the 10 m surface, peak {peak}");
        Assert.False(s.Hopping);
    }

    [Fact]
    public void JumpFarFromTheBankDoesNothingInDeepWater()
    {
        var w = Lake();
        var s = At(w, 20, FloatFeetY, -20); // 20 m from every shore
        Tick(ref s, w, In(MoveButtons.Jump));
        Assert.False(s.Hopping);
        Assert.Equal(0f, s.Velocity.Y, 3);
        Assert.Equal(FloatFeetY, s.Position.Y, 3);
    }

    [Fact]
    public void HoldingJumpFromBeforeDoesNotHop()
    {
        var w = Lake();
        var s = At(w, 1, FloatFeetY, -20);
        s.JumpKeyDown = true; // the key was already down: no press edge
        Tick(ref s, w, In(MoveButtons.Jump));
        Assert.False(s.Hopping);
    }

    [Fact]
    public void LandingInDeepWaterDoesNoFallDamageAndShallowLandingStillHurts()
    {
        var s = new MoveState { Position = new Vector3(20, 14, -20), OnFloor = false, FallPeakY = 14 };
        // the bed is 8 m and the surface 10 m: landing on the bed in 2 m of water
        var deep = MovementStep.PostMove(ref s, new Vector3(20, 8, -20), Vector3.Zero, true, landingWaterDepth: 2f);
        Assert.Equal(0f, deep);

        var dry = new MoveState { Position = new Vector3(60, 14, -20), OnFloor = false, FallPeakY = 14 };
        var dryDamage = MovementStep.PostMove(ref dry, new Vector3(60, 8, -20), Vector3.Zero, true);
        Assert.True(dryDamage > 0f);

        var shallow = new MoveState { Position = new Vector3(105, 14, -5), OnFloor = false, FallPeakY = 14 };
        var shallowDamage = MovementStep.PostMove(ref shallow, new Vector3(105, 9.2f, -5), Vector3.Zero, true, landingWaterDepth: 0.5f);
        Assert.InRange(shallowDamage, 0.01f, dryDamage - 0.01f);
    }

    [Fact]
    public void FloatingResetsTheFallSoAHopOutCarriesNoOldFall()
    {
        var w = Lake();
        var s = At(w, 1, FloatFeetY, -20);
        s.FallPeakY = 14f; // an old fall from the air, before the water
        MovementStep.PostMove(ref s, new Vector3(1, FloatFeetY, -20), Vector3.Zero, false, w.DepthAt(new Vector3(1, FloatFeetY, -20)));
        Assert.Equal(FloatFeetY, s.FallPeakY, 3);
    }

    [Fact]
    public void ProneAndSprintAreOffInDeepWater()
    {
        var w = Lake();
        var s = At(w, 20, FloatFeetY, -20);
        s.ProneLatched = true;
        Tick(ref s, w, In(MoveButtons.Sprint | MoveButtons.Prone, 0, 1));
        Assert.False(s.ProneLatched);
        Assert.False(s.WasSprinting);
    }
}
