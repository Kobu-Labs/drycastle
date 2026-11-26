using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Fsm;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class DebrisTests
{
    static readonly DebrisConfig Debris = new();
    static readonly FractureConfig Frac = new();

    static void Near(Vector3 want, Vector3 have) => Assert.True(Vector3.Distance(want, have) < 1e-3f, $"want {want}, have {have}");

    [Fact]
    public void A_body_frame_is_the_house_yaw_about_up_and_matches_the_grid()
    {
        var g = TestHouses.Box(12, 10, 14, origin: new Vector3(100, 50, 3), yaw: 0.7f);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, g.Yaw);
        foreach (var p in new[] { Vector3.Zero, new Vector3(3, 4, 5), new Vector3(11.5f, 0.5f, 7) })
        {
            Near(DebrisRules.GodotOf(g.ToMap(p)), DebrisRules.GodotOf(g.Origin) + Vector3.Transform(DebrisRules.LocalOf(p, g.Cell), turn));
            Near(p, DebrisRules.GridOf(DebrisRules.LocalOf(p, g.Cell), g.Cell));
        }
    }

    [Fact]
    public void Breaks_only_past_the_grace_over_the_threshold_and_while_it_may_still_split()
    {
        Assert.False(DebrisRules.Breaks(0, 100, 0.05f, 50f, Debris, Frac));
        Assert.False(DebrisRules.Breaks(0, 100, 1f, Debris.BreakImpulse - 0.1f, Debris, Frac));
        Assert.True(DebrisRules.Breaks(0, 100, 1f, Debris.BreakImpulse + 0.1f, Debris, Frac));
        Assert.True(DebrisRules.Breaks(1, 2 * Frac.CellsPerPiece, 1f, 10f, Debris, Frac));
        Assert.False(DebrisRules.Breaks(1, 2 * Frac.CellsPerPiece - 1, 1f, 10f, Debris, Frac));
        Assert.False(DebrisRules.Breaks(1, 500, 1f, 10f, Debris with { SecondBreak = false }, Frac));
        Assert.False(DebrisRules.Breaks(2, 500, 1f, 10f, Debris, Frac));
    }

    [Fact]
    public void The_dust_group_is_small_or_the_disconnected_last_one()
    {
        var g = TestHouses.Box(8, 8, 8);
        int[] line = [g.IndexOf(1, 1, 1), g.IndexOf(2, 1, 1), g.IndexOf(3, 1, 1), g.IndexOf(4, 1, 1)];
        int[] apart = [g.IndexOf(1, 1, 1), g.IndexOf(2, 1, 1), g.IndexOf(5, 5, 5), g.IndexOf(6, 5, 5)];
        Assert.False(DebrisRules.IsDust(g, line, 0, 2, Frac));
        Assert.False(DebrisRules.IsDust(g, line, 1, 2, Frac));
        Assert.True(DebrisRules.IsDust(g, apart, 1, 2, Frac));
        Assert.True(DebrisRules.IsDust(g, line[..3], 0, 2, Frac));
    }

    [Fact]
    public void The_budget_gives_bodies_to_the_largest_pieces()
    {
        List<int> bodies = new(), dust = new();
        DebrisRules.Budget([5, 40, 12, 40], 2, bodies, dust);
        Assert.Equal([1, 3], bodies);
        Assert.Equal([2, 0], dust);
        DebrisRules.Budget([5, 40], -3, bodies, dust);
        Assert.Empty(bodies);
    }

    [Fact]
    public void A_piece_keeps_its_parents_velocity_and_is_kicked_outward()
    {
        var v = DebrisRules.PieceVelocity(new Vector3(0, -5, 0), Vector3.Zero, new Vector3(2, 0, 0), Vector3.UnitY);
        Near(new Vector3(DebrisRules.KickMps, -5 + DebrisRules.KickUpMps, 0), v);
        var spun = DebrisRules.PieceVelocity(Vector3.Zero, new Vector3(0, 1, 0), new Vector3(1, 0, 0), Vector3.UnitY);
        Assert.True(spun.Z < -0.9f); // w x r = (0,1,0) x (1,0,0) = (0,0,-1): turning ccw seen from above, a piece east of the centre moves north (-z)
    }

    [Fact]
    public void Stay_rubble_over_the_cap_evicts_the_oldest_and_keeps_the_newest()
    {
        var ledger = new RubbleLedger();
        var evicted = new List<long>();
        ledger.Add(1, 400, 1000, evicted); Assert.Empty(evicted);
        ledger.Add(2, 400, 1000, evicted); Assert.Empty(evicted);
        ledger.Add(3, 400, 1000, evicted); Assert.Equal([1L], evicted);
        ledger.Add(4, 5000, 1000, evicted); Assert.Equal([2L, 3L], evicted);
        Assert.Equal(5000, ledger.Cells);
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void The_machine_walks_both_lives_under_strict()
    {
        var m = new StateMachine<DebrisPhase>(DebrisMachine.Spec);
        Assert.True(m.Enter(DebrisPhase.Broken));
        Assert.True(m.Enter(DebrisPhase.Resting, Debris.FadeAfterS));
        Assert.True(m.Tick(Debris.FadeAfterS));
        Assert.True(m.Enter(DebrisPhase.Fading, Debris.FadeS));
        var whole = new StateMachine<DebrisPhase>(DebrisMachine.Spec);
        Assert.True(whole.Enter(DebrisPhase.Resting));
        Assert.Equal(["Fading"], DebrisMachine.Spec.Terminal);
        Assert.Throws<InvalidOperationException>(() => m.Enter(DebrisPhase.Falling));
    }
}
