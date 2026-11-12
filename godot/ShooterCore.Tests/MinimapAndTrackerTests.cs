using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Ticket #119: the client killstreak tracker (mirrors the server counter), roads.json and the minimap geometry.</summary>
public class MinimapAndTrackerTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly KillstreakFile Streaks = GameData.Load(DataDir).Killstreaks;

    static KillstreakTracker Armed(params string[] streaks)
    {
        var t = new KillstreakTracker(Streaks);
        t.Arm(streaks, 0f);
        return t;
    }

    [Fact]
    public void EmptyLoadoutSlotsAreEmptyAndCountKillsOnlyWhileArmed()
    {
        var t = Armed("uav", "");
        Assert.Equal(StreakSlotState.Progress, t.Slot(0).State);
        Assert.Equal(StreakSlotState.Empty, t.Slot(1).State);
        Assert.Equal(StreakSlotState.Empty, t.Slot(2).State);
        Assert.Equal(0, t.Slot(1).KillsRequired);
    }

    [Fact]
    public void CounterShowsProgressTowardTheThresholdAndCapsAtIt()
    {
        var t = Armed("uav");
        Assert.Equal(4, t.Slot(0).KillsRequired);
        t.OnKill(); t.OnKill();
        Assert.Equal(2, t.Slot(0).KillsDone);
        for (var i = 0; i < 9; i++) t.OnKill();
        Assert.Equal(4, t.Slot(0).KillsDone);
        Assert.Equal(11, t.Kills);
    }

    [Fact]
    public void EarnedSnapsCounterToTheServerAndMarksReady()
    {
        var t = Armed("uav");
        t.OnKill();
        t.OnEarned("uav", 4);
        Assert.Equal(4, t.Kills);
        Assert.Equal(StreakSlotState.Ready, t.Slot(0).State);
    }

    [Fact]
    public void UsedIsActiveUntilEndedThenSpent()
    {
        var t = Armed("uav");
        t.OnEarned("uav", 4);
        t.OnUsed("uav");
        Assert.Equal(StreakSlotState.Active, t.Slot(0).State);
        t.OnEnded("uav");
        Assert.Equal(StreakSlotState.Spent, t.Slot(0).State);
    }

    [Fact]
    public void DeathResetsCounterAndEveryStreakState()
    {
        var t = Armed("uav", "turret");
        t.OnEarned("uav", 4);
        t.OnUsed("uav");
        t.OnDeath();
        Assert.Equal(0, t.Kills);
        Assert.Equal(StreakSlotState.Progress, t.Slot(0).State);
        Assert.Equal(0, t.Slot(0).KillsDone);
        Assert.Equal(StreakSlotState.Progress, t.Slot(1).State);
    }

    [Fact]
    public void HotStreakLowersTheRequiredKillsByOneNeverBelowOne()
    {
        var t = new KillstreakTracker(Streaks);
        t.Arm(new[] { "uav", "moab", "" }, 1f);
        Assert.Equal(3, t.Slot(0).KillsRequired);
        Assert.Equal(23, t.Slot(1).KillsRequired);
    }

    [Fact]
    public void ArmStartsALifeWithoutTheOldState()
    {
        var t = Armed("uav");
        t.OnKill();
        t.OnEarned("uav", 4);
        t.Arm(new[] { "turret", "", "" }, 0f);
        Assert.Equal(0, t.Kills);
        Assert.Equal(StreakSlotState.Progress, t.Slot(0).State);
        Assert.Equal("turret", t.Slot(0).Id);
    }

    [Fact]
    public void UnknownStreakIdShowsAsEmpty()
    {
        var t = Armed("not_a_streak");
        Assert.Equal(StreakSlotState.Empty, t.Slot(0).State);
    }

    [Fact]
    public void RoadNetworkReadsEdgePolylinesInLocalMetres()
    {
        const string json = """
        {"version":1,"axes":"local metres, x east, y north","nodes":[],"edges":[
          {"id":"E-1","highway":"tertiary","width":6.5,"polyline":[[1,2],[3,4],[5,6]]},
          {"id":"E-2","highway":"track","polyline":[[0,0]]},
          {"id":"E-3","highway":"path","width":1.5,"polyline":[[-1,-1],[1,1]]}]}
        """;
        var roads = RoadNetwork.Parse(json);
        Assert.Equal(2, roads.Count);
        Assert.Equal(3, roads[0].Points.Length);
        Assert.Equal(new Vector2(3, 4), roads[0].Points[1]);
        Assert.Equal(6.5f, roads[0].WidthM);
        Assert.Equal("path", roads[1].Highway);
    }

    [Fact]
    public void RoadNetworkIsEmptyForMissingInput()
    {
        Assert.Empty(RoadNetwork.Parse(""));
        Assert.Empty(RoadNetwork.Parse("{\"version\":1}"));
    }

    [Fact]
    public void FacingNorthKeepsNorthUp()
    {
        Assert.Equal(0f, MinimapMath.RotationFor(0f), 5);
    }

    [Fact]
    public void ForwardMapsToScreenUpForAnyYaw()
    {
        foreach (var yaw in new[] { 0f, 0.7f, 1.9f, -2.4f, MathF.PI })
        {
            var forward = new Vector2(-MathF.Sin(yaw), -MathF.Cos(yaw));
            var up = MinimapMath.Rotate(forward, MinimapMath.RotationFor(yaw));
            Assert.Equal(0f, up.X, 4);
            Assert.Equal(-1f, up.Y, 4);
        }
    }

    [Fact]
    public void ScreenOffsetScalesWithZoom()
    {
        var px = MinimapMath.PxPerMetre(100f, 50f);
        Assert.Equal(2f, px, 5);
        var p = MinimapMath.ToScreen(new Vector2(0, 10), 0f, px);
        Assert.Equal(0f, p.X, 4);
        Assert.Equal(20f, p.Y, 4);
        Assert.Equal(0f, MinimapMath.PxPerMetre(100f, 0f));
    }

    [Fact]
    public void FarDotsClampToTheEdgeOfTheSquare()
    {
        var p = MinimapMath.ClampToSquare(new Vector2(500, -20), 100);
        Assert.Equal(new Vector2(100, -20), p);
    }

    [Fact]
    public void PingsShowUntilTheirExpiryThenDisappear()
    {
        var pings = new MinimapPings();
        Assert.Empty(pings.Positions(0));
        pings.Set(new[] { new Vector2(1, 2), new Vector2(3, 4) }, expiry: 10);
        Assert.Equal(2, pings.Positions(9.9).Count);
        Assert.True(pings.Active(9.9));
        Assert.Empty(pings.Positions(10));
        Assert.False(pings.Active(10));
    }

    [Fact]
    public void ANewPingSetReplacesTheOldOne()
    {
        var pings = new MinimapPings();
        pings.Set(new[] { new Vector2(1, 1), new Vector2(2, 2), new Vector2(3, 3) }, 60);
        pings.Set(new[] { new Vector2(9, 9) }, 70);
        Assert.Single(pings.Positions(5));
        Assert.Equal(new Vector2(9, 9), pings.Positions(5)[0]);
        pings.Clear();
        Assert.Empty(pings.Positions(5));
    }
}
