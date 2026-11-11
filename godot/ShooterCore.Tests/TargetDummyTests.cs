using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class TargetDummyTests
{
    static Func<float, float, float?> FlatGround => (x, z) => 0f;

    [Fact]
    public void PlanPutsTargetsOnTheRingsInTheRequestedCount()
    {
        var spots = TargetPlacement.Plan(Vector3.Zero, 24, FlatGround);
        Assert.Equal(24, spots.Count);
        foreach (var s in spots)
        {
            var d = MathF.Sqrt(s.Feet.X * s.Feet.X + s.Feet.Z * s.Feet.Z);
            Assert.Equal(s.RingMetres, d, 0.01f);
            Assert.Contains(s.RingMetres, TargetPlacement.RingsMetres);
        }
        // Each ring gets an equal share (6 each for 24).
        foreach (var ring in TargetPlacement.RingsMetres)
            Assert.Equal(6, spots.Count(s => s.RingMetres == ring));
    }

    [Fact]
    public void PlanCyclesStandCrouchProne()
    {
        var spots = TargetPlacement.Plan(Vector3.Zero, 6, FlatGround);
        Assert.Equal(new[] { Stance.Stand, Stance.Crouch, Stance.Prone, Stance.Stand, Stance.Crouch, Stance.Prone }, spots.Select(s => s.Pose));
    }

    [Fact]
    public void PlanSkipsPositionsWithoutGroundAndRejectedSpots()
    {
        // No ground on the 25 m ring: those candidates are dropped, the others still fill.
        float? NoGroundOnOuterRing(float x, float z) => MathF.Sqrt(x * x + z * z) > 20 && MathF.Sqrt(x * x + z * z) < 30 ? null : 0f;
        var spots = TargetPlacement.Plan(Vector3.Zero, 24, NoGroundOnOuterRing);
        Assert.DoesNotContain(spots, s => s.RingMetres == 25f);
        Assert.Equal(24, spots.Count); // 8 sectors on each of the 3 remaining rings

        var rejected = TargetPlacement.Plan(Vector3.Zero, 24, FlatGround, s => s.RingMetres != 100f);
        Assert.DoesNotContain(rejected, s => s.RingMetres == 100f);
    }

    [Fact]
    public void PlanIsDeterministicAndStopsAtZero()
    {
        var a = TargetPlacement.Plan(new Vector3(5, 0, 5), 12, FlatGround);
        var b = TargetPlacement.Plan(new Vector3(5, 0, 5), 12, FlatGround);
        Assert.Equal(a, b);
        Assert.Empty(TargetPlacement.Plan(Vector3.Zero, 0, FlatGround));
    }

    [Fact]
    public void DummiesAreNamedAndHaveFakeIdsAndFullHealth()
    {
        var spots = TargetPlacement.Plan(Vector3.Zero, 8, FlatGround);
        var t = new TargetDummies(spots, 3f);
        Assert.Equal("Target 07", t.All[6].Name);
        Assert.Equal(TargetDummies.FirstId + 6, t.All[6].Id);
        Assert.All(t.All, d => Assert.Equal(100f, d.Health));
        Assert.All(t.All, d => Assert.True(TargetDummies.IsDummyId(d.Id)));
        Assert.False(TargetDummies.IsDummyId(1));
    }

    [Fact]
    public void KillThenRespawnAfterDelayOnTheSameSpot()
    {
        var spots = TargetPlacement.Plan(Vector3.Zero, 2, FlatGround);
        var t = new TargetDummies(spots, 3f);
        var id = t.All[0].Id;
        Assert.Equal(DamageResult.Damaged, t.Damage(id, 60, 10));
        Assert.Equal(DamageResult.Killed, t.Damage(id, 60, 10));
        Assert.False(t.IsHittable(id));
        Assert.Equal(DamageResult.Ignored, t.Damage(id, 60, 10.5)); // dead: no more hits
        Assert.Empty(t.Update(12.9));                              // respawn timer not up
        Assert.Equal(new[] { id }, t.Update(13.0));
        Assert.True(t.IsHittable(id));
        Assert.Equal(100f, t.Get(id)!.Health);
        Assert.Equal(spots[0].Feet, t.Get(id)!.Spot.Feet);
    }

    [Fact]
    public void UnknownIdsAreIgnored()
    {
        var t = new TargetDummies(TargetPlacement.Plan(Vector3.Zero, 1, FlatGround), 3f);
        Assert.Equal(DamageResult.Ignored, t.Damage(7, 500, 0));
        Assert.Equal(DamageResult.Ignored, t.Damage(TargetDummies.FirstId, 0, 0)); // zero damage
    }

    static MatchSim Sim(List<MatchEvent> events, MatchConfig? cfg = null)
    {
        var m = new MatchSim(cfg ?? new MatchConfig(), () => 0d, new[] { Vector3.Zero, new Vector3(100, 0, 0) }, new Random(1));
        m.Event += events.Add;
        return m;
    }

    [Fact]
    public void TargetKillCountsForTheShooterButNotTheRoster()
    {
        var ev = new List<MatchEvent>();
        var m = Sim(ev, new MatchConfig { ScoreGoal = 1, MinPlayers = 1 });
        m.AddPlayer(1, "shooter");
        Assert.Equal(MatchPhase.Running, m.Phase);
        var t = new TargetDummies(TargetPlacement.Plan(Vector3.Zero, 1, FlatGround), 3f);
        m.CreditTargetKill(1, t.All[0], 0, KillCause.Headshot);
        Assert.Equal(1, m.Get(1)!.Kills);
        Assert.Equal(MatchPhase.Running, m.Phase);           // score goal of 1 does not end the round on a target
        Assert.Single(m.Players);                             // dummies are not players
        Assert.Contains(ev, e => e is TargetKilled { Target.Name: "Target 01" });
    }

    [Fact]
    public void TargetKillsAreIgnoredOutsideARunningRound()
    {
        var ev = new List<MatchEvent>();
        var m = Sim(ev, new MatchConfig { MinPlayers = 2 });
        m.AddPlayer(1, "shooter");
        Assert.Equal(MatchPhase.Waiting, m.Phase);            // one player: a target is not the second player
        var t = new TargetDummies(TargetPlacement.Plan(Vector3.Zero, 1, FlatGround), 3f);
        m.CreditTargetKill(1, t.All[0], 0);
        Assert.Equal(0, m.Get(1)!.Kills);
    }

    [Fact]
    public void TargetKillForUnknownShooterIsIgnored()
    {
        var ev = new List<MatchEvent>();
        var m = Sim(ev);
        m.AddPlayer(1, "a");
        var t = new TargetDummies(TargetPlacement.Plan(Vector3.Zero, 1, FlatGround), 3f);
        m.CreditTargetKill(9, t.All[0], 0);
        Assert.Equal(0, m.Get(1)!.Kills);
    }

    [Fact]
    public void MatchConfigDefaultsKeepTargetsOff()
    {
        var c = new MatchConfig();
        Assert.Equal(0, c.Targets);
        Assert.True(c.TargetsAwardXp);
    }
}
