using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class LaddersTests
{
    const float Dt = 1f / 30f;

    // one ladder at the origin facing east (+x): the floor above is at y 0, the floor below at y -2.4
    static readonly Ladders One = new(new[] { new Ladder(new Vector2(0, 0), new Vector2(1, 0), -2.4f, 0f) });

    // yaw for a player facing the given ground direction (Godot: yaw 0 faces -Z)
    static float YawToward(Vector2 d) => MathF.Atan2(-d.X, -d.Y);

    static MoveInput Input(Vector3 from, MoveButtons b, float moveY = 0, float? yaw = null) =>
        new() { Buttons = b, MoveY = moveY, Yaw = yaw ?? YawToward(new Vector2(-from.X, -from.Z)), SpeedScale = 1 };

    static MoveState At(float x, float y, float z) => new() { Position = new Vector3(x, y, z), OnFloor = true };

    static MoveState Mounted(float y = -1f)
    {
        var s = At(1.2f, 0f, 0f);
        Assert.True(One.Climb(ref s, Input(s.Position, MoveButtons.Use), Dt));
        s.Position = new Vector3(0, y, 0); // already on, part way down
        return s;
    }

    [Fact]
    public void UsePressedNearTheTopAndLookingAtItGetsOn()
    {
        var s = At(1.2f, 0f, 0f);
        Assert.True(One.Climb(ref s, Input(s.Position, MoveButtons.Use), Dt));
        Assert.True(s.OnLadder);
        Assert.Equal(new Vector3(0, 0, 0), s.Position); // put on the rail
        Assert.True(s.Stance == Stance.Stand);
    }

    [Fact]
    public void NotInReachOrNotLookingOrNotPressedDoesNothing()
    {
        var far = At(3f, 0f, 0f);
        Assert.False(One.Climb(ref far, Input(far.Position, MoveButtons.Use), Dt));
        var away = At(1.2f, 0f, 0f);
        Assert.False(One.Climb(ref away, Input(away.Position, MoveButtons.Use, yaw: YawToward(new Vector2(1, 0))), Dt)); // looking away
        var idle = At(1.2f, 0f, 0f);
        Assert.False(One.Climb(ref idle, Input(idle.Position, MoveButtons.None), Dt));
        var low = At(1.2f, -1.5f, 0f); // below the top floor and above the bottom one: no ladder in reach at this height... but between the floors is in reach
        Assert.True(One.Climb(ref low, Input(low.Position, MoveButtons.Use), Dt));
        var way = At(1.2f, 3f, 0f); // a floor above the ladder altogether
        Assert.False(One.Climb(ref way, Input(way.Position, MoveButtons.Use), Dt));
    }

    [Fact]
    public void HoldingUseDoesNotToggleEveryTick()
    {
        var s = At(1.2f, 0f, 0f);
        var use = Input(s.Position, MoveButtons.Use);
        One.Climb(ref s, use, Dt);
        for (var k = 0; k < 10; k++) One.Climb(ref s, use, Dt); // still held
        Assert.True(s.OnLadder);
    }

    [Fact]
    public void ForwardClimbsBackDescendsAtClimbSpeed()
    {
        var s = Mounted(-1f);
        s.LadderArmed = true;
        One.Climb(ref s, Input(s.Position, MoveButtons.None, moveY: -1), Dt);
        Assert.Equal(-1f - Ladders.ClimbSpeed * Dt, s.Position.Y, 4);
        s.Position = new Vector3(0, -1f, 0);
        One.Climb(ref s, Input(s.Position, MoveButtons.None, moveY: 1), Dt);
        Assert.Equal(-1f + Ladders.ClimbSpeed * Dt, s.Position.Y, 4);
        Assert.Equal(0f, s.Position.X); // stays on the rail
    }

    [Fact]
    public void ReachingTheTopStepsOffOntoTheFloorBeyondTheEdge()
    {
        var s = Mounted(-0.01f);
        s.LadderArmed = true;
        Assert.True(One.Climb(ref s, Input(s.Position, MoveButtons.None, moveY: 1), Dt));
        Assert.False(s.OnLadder);
        Assert.True(s.OnFloor);
        Assert.Equal(new Vector3(Ladders.StepOffTop, 0f, 0f), s.Position);
    }

    [Fact]
    public void ReachingTheBottomStepsOffBackIntoTheRoom()
    {
        var s = Mounted(-2.39f);
        s.LadderArmed = true;
        One.Climb(ref s, Input(s.Position, MoveButtons.None, moveY: -1), Dt);
        Assert.False(s.OnLadder);
        Assert.Equal(new Vector3(-Ladders.StepOffBottom, -2.4f, 0f), s.Position);
    }

    [Fact]
    public void GettingOnAtTheTopWhileWalkingForwardDoesNotStepStraightOff()
    {
        var s = At(1.2f, 0f, 0f);
        var walk = Input(s.Position, MoveButtons.Use, moveY: 1);
        One.Climb(ref s, walk, Dt);
        One.Climb(ref s, Input(s.Position, MoveButtons.Use, moveY: 1), Dt); // forward still held: waits
        Assert.True(s.OnLadder);
        Assert.Equal(0f, s.Position.Y);
        One.Climb(ref s, Input(s.Position, MoveButtons.Use, moveY: 0), Dt); // let go of the keys: armed
        One.Climb(ref s, Input(s.Position, MoveButtons.Use, moveY: -1), Dt);
        Assert.True(s.Position.Y < 0);
    }

    [Fact]
    public void UseOrJumpLetsGoAndTheBodyFalls()
    {
        var s = Mounted(-1f);
        s.UseKeyDown = false;
        Assert.False(One.Climb(ref s, Input(s.Position, MoveButtons.Use), Dt)); // the walking step takes over
        Assert.False(s.OnLadder);
        Assert.False(s.OnFloor);
        Assert.Equal(-1f, s.FallPeakY);

        var j = Mounted(-1f);
        Assert.False(One.Climb(ref j, Input(j.Position, MoveButtons.Jump), Dt));
        Assert.False(j.OnLadder);
    }

    [Fact]
    public void ClientAndServerAgreeOnTheSameInputs()
    {
        var a = At(1.2f, 0f, 0f);
        var b = a;
        MoveInput[] script =
        {
            Input(a.Position, MoveButtons.Use), Input(a.Position, MoveButtons.Use), Input(a.Position, MoveButtons.None),
            Input(a.Position, MoveButtons.None, moveY: -1), Input(a.Position, MoveButtons.None, moveY: -1), Input(a.Position, MoveButtons.None, moveY: 1),
        };
        foreach (var i in script) { One.Climb(ref a, i, Dt); One.Climb(ref b, i, Dt); }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.OnLadder, b.OnLadder);
    }
}
