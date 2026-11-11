using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// The player and dummy hit volume against the drawn model (measured from the GLB: 1.899 m tall, 1.175 m wide, 1.118 m deep).
/// Each pose is shot at the head, chest, feet, hand height, just beside and just above the body.
/// </summary>
public class PlayerHitBoxTests
{
    // the target stands at the origin of the feet, facing the shooter's side (yaw 0 faces -Z); shooters stand at z = 0
    static readonly Vector3 Feet = new(0, 0, -10);

    static HitTarget Target(Stance stance, float yaw = 0) => new(2, Feet, stance, yaw);

    /// <summary>Horizontal reach of the prone capsule's round end at a height above the ground (a circle of the hit radius centred on the lying axis).</summary>
    static float Reach(float height) => MathF.Sqrt(MathF.Pow(PlayerHitBox.Radius, 2) - MathF.Pow(PlayerHitBox.Radius - height, 2));

    static RayHit? Shoot(Vector3 from, Vector3 at, HitTarget t) =>
        HitMath.Raycast(from, Vector3.Normalize(at - from), 100, float.PositiveInfinity, new[] { t }, 1);

    [Fact]
    public void DrawnModelMatchesTheStandHeight()
    {
        Assert.Equal(PlayerHitBox.StandHeight, PlayerHitBox.ModelHeight * PlayerHitBox.DrawScale, 4);
        Assert.InRange(PlayerHitBox.Radius, 0.5f, 0.6f); // the drawn torso half width, not the 0.35 m movement capsule
        Assert.Equal(PlayerHitBox.ProneLift, PlayerHitBox.Radius, 4);
    }

    [Fact]
    public void StandingBodyHitsHeadChestFeetAndHandHeight()
    {
        var t = Target(Stance.Stand);
        var eye = new Vector3(0, 1.5f, 0);
        var head = Shoot(eye, Feet + new Vector3(0, 1.6f, 0), t);
        Assert.True(head is { Head: true });
        var chest = Shoot(eye, Feet + new Vector3(0, 1.0f, 0), t);
        Assert.True(chest is { Head: false });
        Assert.NotNull(Shoot(new Vector3(0, 0.15f, 0), Feet + new Vector3(0, 0.15f, 0), t));       // feet
        Assert.NotNull(Shoot(new Vector3(0.4f, 0.45f, 0), Feet + new Vector3(0.4f, 0.45f, 0), t)); // hand height, an arm out to the side
    }

    [Fact]
    public void StandingBodyMissesBesideAndAbove()
    {
        var t = Target(Stance.Stand);
        Assert.Null(Shoot(new Vector3(0.8f, 1.0f, 0), Feet + new Vector3(0.8f, 1.0f, 0), t)); // beside the drawn shoulder
        Assert.Null(Shoot(new Vector3(0, 2.2f, -5), Feet + new Vector3(0, 1.95f, 0), t));     // just over the top of the head
    }

    [Fact]
    public void CrouchingBodyHitsItsLowerShapeOnly()
    {
        var t = Target(Stance.Crouch);
        Assert.NotNull(Shoot(new Vector3(0, 0.8f, 0), Feet + new Vector3(0, 0.8f, 0), t));
        Assert.True(Shoot(new Vector3(0, 1.1f, 0), Feet + new Vector3(0, 0.95f, 0), t) is { Head: true });
        Assert.Null(Shoot(new Vector3(0, 1.6f, 0), Feet + new Vector3(0, 1.6f, 0), t));       // the standing head height is above a crouch
        Assert.Null(Shoot(new Vector3(0.8f, 0.6f, 0), Feet + new Vector3(0.8f, 0.6f, 0), t)); // beside
    }

    [Fact]
    public void ProneBodyLiesAlongTheFacingDirection()
    {
        var t = Target(Stance.Prone); // facing the shooter's -Z: the body runs from z -10.9 to -9.1, the head at the far end
        var low = Shoot(new Vector3(0, 0.3f, 0), Feet + new Vector3(0, 0.3f, 0), t);
        Assert.NotNull(low);                                                                    // ground level, at the body's middle
        // the lying body's rounded near end: 9.657 m (its axis end) less the horizontal reach of the cap at 0.3 m height
        Assert.Equal(9.657f - Reach(0.3f), low!.Value.Distance, 0.02f);
        Assert.NotNull(Shoot(new Vector3(0, 0.9f, 0), Feet + new Vector3(0, 0.9f, 0), t));      // the upper body from the side
        Assert.Null(Shoot(new Vector3(0, 2.0f, -5), Feet + new Vector3(0, 1.3f, 0), t));        // standing height, above the lying body
        Assert.Null(Shoot(new Vector3(0.9f, 0.3f, 0), Feet + new Vector3(0.9f, 0.3f, 0), t));   // beside
    }

    [Fact]
    public void ProneHeadIsAtTheFarEndAndHitsFromAbove()
    {
        var t = Target(Stance.Prone);
        var headCentre = HitMath.HeadCenter(t);
        Assert.Equal(Feet.Z - PlayerHitBox.ProneHeadOffset, headCentre.Z, 3); // forward of the feet, the far end from a shooter at z 0
        Assert.Equal(PlayerHitBox.ProneLift, headCentre.Y, 3);
        // straight down onto the head from above: the ray passes the head sphere
        Assert.True(Shoot(new Vector3(headCentre.X, 2f, headCentre.Z), headCentre - new Vector3(0, 0.1f, 0), t) is { Head: true });
    }

    [Fact]
    public void ProneBodyRotatesWithTheYaw()
    {
        // turned to face +X (yaw -pi/2): the body runs along X, so a shot along Z at the centre now clips the side of the body, not its length
        var t = Target(Stance.Prone, -MathF.PI / 2);
        var along = Shoot(new Vector3(0, 0.3f, 0), Feet + new Vector3(0, 0.3f, 0), t);
        Assert.NotNull(along);
        Assert.Equal(10f - Reach(0.3f), along!.Value.Distance, 0.02f);
        // from the end of the body along X: hit; a line 0.7 m off the body's centre line (beyond the 0.56 m radius): miss
        Assert.NotNull(Shoot(new Vector3(8f, 0.3f, -10), new Vector3(0, 0.3f, -10), t));
        Assert.Null(Shoot(new Vector3(8f, 0.3f, -9.3f), new Vector3(0, 0.3f, -9.3f), t));
    }

    [Fact]
    public void DistanceToBodyIsZeroInsideAndGrowsOutside()
    {
        var t = Target(Stance.Stand);
        Assert.Equal(0f, HitMath.DistanceToBody(Feet + new Vector3(0, 1f, 0), t), 4);
        Assert.Equal(0.3f, HitMath.DistanceToBody(Feet + new Vector3(PlayerHitBox.Radius + 0.3f, 1f, 0), t), 3);
        // the axis ends at 1.8 - 0.56 = 1.24 m; a point at 2.5 m is 1.26 m above that end, less the radius: 0.70 m
        Assert.Equal(0.7f, HitMath.DistanceToBody(Feet + new Vector3(0, 2.5f, 0), t), 3);
    }

    [Fact]
    public void DummyFacesTheCentreOfTheRing()
    {
        var spots = TargetPlacement.Plan(Vector3.Zero, 4, (x, z) => 0f);
        foreach (var s in spots)
        {
            var toCentre = Vector3.Normalize(-new Vector3(s.Feet.X, 0, s.Feet.Z));
            var facing = HitMath.Forward(s.Yaw, 0);
            Assert.True(Vector3.Dot(facing, toCentre) > 0.999f, $"spot at {s.Feet} faces {facing}, not the centre");
        }
    }

    [Fact]
    public void DummyPlantedOnRoofAndSkipsSteepGround()
    {
        // a roof at 4 m everywhere the ray meets it first (the dummy stands at the roof height), and a steep face (null) at x > 5 is rejected
        var spots = TargetPlacement.Plan(Vector3.Zero, 6, (x, z) => x > 5 ? null : 4f);
        Assert.Equal(6, spots.Count);
        Assert.All(spots, s => Assert.True(s.Feet.X <= 5));
        Assert.All(spots, s => Assert.Equal(4f, s.Feet.Y));
    }
}
