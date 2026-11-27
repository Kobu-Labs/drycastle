using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Status;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Harvest's fill feedback (docs/cartel.md "Harvest"): what the hold does for the prompt and ring, when everyone sees the filler bend, and the wire forms.</summary>
public class HarvestFillTests
{
    [Fact]
    public void TheHoldFillsUnlessTheVanIsNotThereADuffelIsCarriedOrTheMaximumIsFilled()
    {
        Assert.Equal(FillReach.Ready, HarvestFillRules.Reach(HeistPhase.AtObjective, 2, 5, false));
        Assert.Equal(FillReach.Ready, HarvestFillRules.Reach(HeistPhase.ToDrop, 2, 5, false)); // back for more: the van left with the minimum, the filling goes on
        Assert.Equal(FillReach.VanFirst, HarvestFillRules.Reach(HeistPhase.ToObjective, 0, 5, false));
        Assert.Equal(FillReach.Carrying, HarvestFillRules.Reach(HeistPhase.AtObjective, 2, 5, true));
        Assert.Equal(FillReach.Full, HarvestFillRules.Reach(HeistPhase.AtObjective, 5, 5, false));
    }

    [Fact]
    public void TheVanComesBeforeTheHandsAndTheHandsBeforeTheMaximum()
    {
        Assert.Equal(FillReach.VanFirst, HarvestFillRules.Reach(HeistPhase.ToObjective, 5, 5, true));
        Assert.Equal(FillReach.Carrying, HarvestFillRules.Reach(HeistPhase.AtObjective, 5, 5, true)); // the last one is in the hands: throw it first
    }

    [Fact]
    public void TheFillerIsDrawnBendingOnlyWhileAReadyHoldHasRunLongEnough()
    {
        Assert.False(HarvestFillRules.Shows(FillReach.Ready, true, 0.1, 0.3)); // a tap shows nothing
        Assert.True(HarvestFillRules.Shows(FillReach.Ready, true, 0.3, 0.3));
        Assert.False(HarvestFillRules.Shows(FillReach.Ready, false, 2.9, 0.3)); // the hold is sent or idle
        Assert.False(HarvestFillRules.Shows(FillReach.Carrying, true, 2, 0.3));
        Assert.False(HarvestFillRules.Shows(FillReach.Off, true, 2, 0.3)); // outside a Harvest the High hold stays silent
    }

    [Fact]
    public void TheFillMessageRoundTripsAndRefusesAnUnknownReach()
    {
        foreach (var r in Enum.GetValues<FillReach>())
        {
            Assert.True(HarvestFillRules.TryDecode(HarvestFillRules.Encode(r, 7), out var back, out var max));
            Assert.Equal((r, 7), (back, max));
        }
        Assert.False(HarvestFillRules.TryDecode(new byte[] { 9, 1 }, out _, out _));
        Assert.False(HarvestFillRules.TryDecode(new byte[] { 1 }, out _, out _));
    }

    [Fact]
    public void FillingIsStatusBit3AndTheEventAndProtocolAreTheTicketsOwn()
    {
        var s = new PlayerStatuses();
        Assert.True(s.Grant(StatusKind.Filling));
        Assert.Equal((byte)8, s.Mask);
        Assert.False(s.Grant(StatusKind.Filling));
        Assert.True(s.Remove(StatusKind.Filling));
        s.Grant(StatusKind.Filling);
        s.OnDeath();
        Assert.False(s.Has(StatusKind.Filling)); // death ends it with every status
        Assert.Equal((byte)46, (byte)EventKind.HarvestFill);
        var all = Enum.GetValues<EventKind>().Select(k => (byte)k).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.True(Wire.ProtocolVersion >= 93);
    }

    [Fact]
    public void TheShippedFillShowDelayIsBelowTheHoldAndTheFileRejectsNonsense()
    {
        var shipped = DataFile.Section<HarvestData>(GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Cartel.JobSection("harvest"), "jobs.harvest");
        shipped.Validate("jobs.harvest", 5);
        Assert.Equal(0.3f, shipped.FillShowAfterSeconds);
        Assert.True(shipped.FillShowAfterSeconds < shipped.FillHoldSeconds);
        var bad = new HarvestData { FillShowAfterSeconds = 3 }; // not below fill_hold_seconds (3)
        Assert.Throws<DataFileException>(() => bad.Validate("jobs.harvest", 5));
        bad = new HarvestData { FillShowAfterSeconds = -1 };
        Assert.Throws<DataFileException>(() => bad.Validate("jobs.harvest", 5));
        new HarvestData { FillShowAfterSeconds = 0 }.Validate("jobs.harvest", 5);
    }
}
