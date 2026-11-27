using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Drill on a Trap house's safe (#496): trap_house.json drill, the machine (set, run, jams, fix, done), who may hold, the jams' moments, the cash spots and the wire.</summary>
public class DrillTests
{
    static readonly TrapHouseFile File = TrapHouseFile.Load(Path.Combine(AppContext.BaseDirectory, "data", TrapHouseFile.FileName));
    static TrapDrillData Data => File.Drill;
    static readonly HashSet<ushort> Party = new() { 3, 7 };

    static Drill Make(float run = 10, params float[] jams) => new("w1", 1, Party, run, jams);

    /// <summary>Ticks until something happens or the time runs out; the seconds it took.</summary>
    static (DrillStep Step, float Seconds) RunUntil(Drill d, float max, float dt = 0.05f)
    {
        var t = 0f;
        while (t < max) { t += dt; var s = d.Tick(dt); if (s != DrillStep.None) return (s, t); }
        return (DrillStep.None, t);
    }

    [Fact]
    public void The_file_has_a_drill_by_tier()
    {
        Assert.True(DrillRules.RunSeconds(Data, 1) > 0);
        Assert.True(DrillRules.RunSeconds(Data, 2) >= DrillRules.RunSeconds(Data, 1));
        Assert.Equal(DrillRules.RunSeconds(Data, Data.RunSeconds.Count), DrillRules.RunSeconds(Data, 99)); // past the list: the last
        Assert.Equal(DrillRules.RunSeconds(Data, 1), DrillRules.RunSeconds(Data, 0));
        Assert.True(DrillRules.CashDuffels(Data, 1) >= 1);
        Assert.InRange(Data.JamsMin, 1, Data.JamsMax);
        Assert.Equal(3, Data.JamsMax);
        Assert.True(DrillRules.HoldSeconds(Data, DrillHold.Fix) > 0 && DrillRules.HoldSeconds(Data, DrillHold.Set) > 0);
    }

    [Fact]
    public void A_drill_waits_to_be_set_then_runs_its_time()
    {
        var d = Make(10);
        Assert.Equal(DrillState.Armed, d.State);
        Assert.Equal(DrillHold.Set, d.Hold);
        Assert.Equal(DrillStep.None, RunUntil(d, 30).Step); // armed: nothing moves
        Assert.Equal(0, d.Progress);
        Assert.True(d.Work());
        Assert.Equal(DrillState.Running, d.State);
        Assert.Equal(DrillHold.None, d.Hold);
        Assert.False(d.Work()); // a hold on a running drill does nothing
        var (step, secs) = RunUntil(d, 20);
        Assert.Equal(DrillStep.Done, step);
        Assert.Equal(10, secs, 1);
        Assert.Equal(DrillState.Done, d.State);
        Assert.Equal(1f, d.Fraction);
        Assert.Equal(DrillStep.None, d.Tick(1));
    }

    [Fact]
    public void It_jams_at_its_moments_and_stays_jammed_until_fixed()
    {
        var d = Make(10, 3, 7);
        d.Work();
        var (step, secs) = RunUntil(d, 20);
        Assert.Equal(DrillStep.Jammed, step);
        Assert.Equal(3, secs, 1);
        Assert.Equal(3f, d.Progress);
        Assert.Equal(DrillHold.Fix, d.Hold);
        Assert.Equal(DrillStep.None, RunUntil(d, 60).Step); // jammed: it waits
        Assert.Equal(3f, d.Progress);
        Assert.True(d.Work());
        (step, secs) = RunUntil(d, 20);
        Assert.Equal(DrillStep.Jammed, step);
        Assert.Equal(4, secs, 1);
        Assert.Equal(2, d.Jammed);
        d.Work();
        (step, secs) = RunUntil(d, 20);
        Assert.Equal(DrillStep.Done, step);
        Assert.Equal(3, secs, 1);
        Assert.Equal(0, d.SecondsLeft);
    }

    [Fact]
    public void The_jams_are_one_to_three_inside_the_window_rising_and_apart()
    {
        var counts = new HashSet<int>();
        for (var seed = 0; seed < 200; seed++)
        {
            var at = DrillRules.JamsAt(Data, 100, new Random(seed));
            counts.Add(at.Length);
            Assert.InRange(at.Length, Data.JamsMin, Data.JamsMax);
            for (var i = 0; i < at.Length; i++)
            {
                Assert.InRange(at[i], 100 * Data.JamWindow[0], 100 * Data.JamWindow[1]);
                if (i > 0) Assert.True(at[i] - at[i - 1] > 100 * (Data.JamWindow[1] - Data.JamWindow[0]) / at.Length * 0.29f);
            }
        }
        Assert.Equal(new HashSet<int> { 1, 2, 3 }, counts);
    }

    [Fact]
    public void Only_a_party_player_on_foot_in_reach_with_nothing_else_in_reach_holds()
    {
        var d = Make(10, 5);
        Assert.Equal(DrillHold.Set, DrillRules.HoldOf(d, 3, true, true, KeyCompetitor.None));
        Assert.Equal(DrillHold.None, DrillRules.HoldOf(d, 4, true, true, KeyCompetitor.None)); // not of the party
        Assert.Equal(DrillHold.None, DrillRules.HoldOf(d, 3, false, true, KeyCompetitor.None));
        Assert.Equal(DrillHold.None, DrillRules.HoldOf(d, 3, true, false, KeyCompetitor.None));
        Assert.Equal(DrillHold.None, DrillRules.HoldOf(d, 3, true, true, KeyCompetitor.Duffel)); // a Duffel wins the key
        d.Work();
        Assert.Equal(DrillHold.None, DrillRules.HoldOf(d, 3, true, true, KeyCompetitor.None)); // running: nothing to hold for
        RunUntil(d, 20);
        Assert.Equal(DrillHold.Fix, DrillRules.HoldOf(d, 7, true, true, KeyCompetitor.None));

        var stand = new Vector3(10, 2, 10);
        Assert.True(DrillRules.InReach(Data, stand, stand + new Vector3(Data.ReachM * 0.9f, 0.2f, 0)));
        Assert.False(DrillRules.InReach(Data, stand, stand + new Vector3(Data.ReachM * 1.1f, 0, 0)));
        Assert.False(DrillRules.InReach(Data, stand, stand + new Vector3(0, Data.ReachUpM + 0.5f, 0))); // the floor above
    }

    [Fact]
    public void The_cash_lies_in_a_row_in_front_of_the_safe()
    {
        var safe = new SafeSpot(new Vector3(5, 1, 5), 0, new Vector3(5, 1, 4.3f), "r2"); // yaw 0: its door faces -Z
        var spots = DrillRules.CashSpots(Data, safe, 0.6f, 3);
        Assert.Equal(3, spots.Length);
        foreach (var s in spots)
        {
            Assert.Equal(5 - 0.3f - Data.DuffelOutM, s.Z, 3);
            Assert.Equal(1f, s.Y);
        }
        Assert.Equal(5f, spots[1].X, 3);
        Assert.Equal(Data.DuffelSpreadM, MathF.Abs(spots[2].X - spots[1].X), 3);
    }

    [Fact]
    public void The_drill_goes_over_the_wire()
    {
        var m = new DrillMsg("w12", DrillState.Jammed, true, 31.5f, 105);
        Assert.Equal(m, DrillCodec.DecodeState(DrillCodec.EncodeState(m)));
        Assert.Equal(m with { Party = false, State = DrillState.Done }, DrillCodec.DecodeState(DrillCodec.EncodeState(m with { Party = false, State = DrillState.Done })));
        Assert.Null(DrillCodec.DecodeState(new byte[] { 3, 0x77 }));
        var bad = DrillCodec.EncodeState(m); bad[4] = 9;
        Assert.Null(DrillCodec.DecodeState(bad));
        Assert.Equal("w12", DrillCodec.DecodeCleared(DrillCodec.EncodeCleared("w12")));
        Assert.Equal((DrillHold.Fix, "w12"), DrillCodec.DecodeReach(DrillCodec.EncodeReach(DrillHold.Fix, "w12")));
        Assert.Equal((DrillHold.None, ""), DrillCodec.DecodeReach(DrillCodec.EncodeReach(DrillHold.None, "w12")));
        Assert.Null(DrillCodec.DecodeReach(new byte[] { 5, 0 }));
        Assert.Equal(62, (int)EventKind.DrillState);
        Assert.Equal(63, (int)EventKind.DrillCleared);
        Assert.Equal(64, (int)EventKind.DrillReach);
    }
}
