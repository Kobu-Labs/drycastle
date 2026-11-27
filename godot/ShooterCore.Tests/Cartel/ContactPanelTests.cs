using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Plantation;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The player side of Jobs (#494): the objective message, the HUD tracker, the Contact panel's model and the Contact's claim on the interact key.</summary>
public class ContactPanelTests
{
    static readonly CartelFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Cartel;

    public ContactPanelTests() => StateMachineDiagnostics.Strict = true;

    static CartelJobMsg Active(int id, float left, CartelJobFlags flags = CartelJobFlags.Leader) => new(id, JobState.Active, JobFailReason.None, "test", 2, 7, left, 0, flags);

    [Fact]
    public void TheObjectiveRoundTripsWithAndWithoutAMarker()
    {
        var with = new CartelObjectiveMsg(42, "Take the duffel to the drop point", new Vector3(1.5f, -2f, 300f));
        Assert.True(CartelCodec.TryDecodeObjective(CartelCodec.EncodeObjective(with), out var back));
        Assert.Equal(with, back);
        var without = new CartelObjectiveMsg(3, "", null);
        Assert.True(CartelCodec.TryDecodeObjective(CartelCodec.EncodeObjective(without), out var back2));
        Assert.Equal(without, back2);
        Assert.False(CartelCodec.TryDecodeObjective(new byte[17], out _));
        var bad = CartelCodec.EncodeObjective(with);
        bad[4] = 2;
        Assert.False(CartelCodec.TryDecodeObjective(bad, out _));
    }

    [Fact]
    public void ALongLineIsCutAtACharacterBoundary()
    {
        var line = new string('é', 200); // 400 bytes of UTF-8
        Assert.True(CartelCodec.TryDecodeObjective(CartelCodec.EncodeObjective(new CartelObjectiveMsg(1, line, null)), out var o));
        Assert.True(o.Line.Length is > 0 and <= CartelCodec.MaxLineBytes / 2);
        Assert.All(o.Line, c => Assert.Equal('é', c));
    }

    [Fact]
    public void TheTrackerRunsShowsTheObjectiveAndCountsDown()
    {
        var t = new CartelTracker(5f);
        Assert.Equal(TrackerPhase.Hidden, t.Phase);
        t.OnObjective(new CartelObjectiveMsg(9, "Go", new Vector3(1, 2, 3))); // before its Job: kept
        t.OnJob(Active(9, 100f));
        Assert.Equal(TrackerPhase.Running, t.Phase);
        Assert.Equal("Go", t.Line);
        Assert.Equal(new Vector3(1, 2, 3), t.Marker);
        t.Tick(10f);
        Assert.Equal(90f, t.SecondsLeft, 3);
        var rev = t.Revision;
        t.OnObjective(new CartelObjectiveMsg(9, "Drop it", null));
        Assert.True(t.Revision > rev);
        Assert.Null(t.Marker);
        t.OnJob(Active(9, 80f)); // a party change: the clock follows the server, the line stays
        Assert.Equal(80f, t.SecondsLeft, 3);
        Assert.Equal("Drop it", t.Line);
        Assert.Equal("1:20", CartelTracker.Clock(t.SecondsLeft));
        Assert.Equal("0:01", CartelTracker.Clock(0.2f));
    }

    [Fact]
    public void TheEndCardStaysItsSecondsThenHides()
    {
        var t = new CartelTracker(5f);
        t.OnJob(Active(4, 60f));
        t.OnObjective(new CartelObjectiveMsg(4, "Go", Vector3.One));
        t.OnJob(new CartelJobMsg(4, JobState.Succeeded, JobFailReason.None, "test", 2, 7, 0, 200, CartelJobFlags.Earns));
        Assert.Equal(TrackerPhase.Ended, t.Phase);
        Assert.Null(t.Marker);
        Assert.Equal(200, t.Job.RespectPaid);
        t.OnJob(CartelJobMsg.None); // leaving the party after the end keeps the card
        Assert.Equal(TrackerPhase.Ended, t.Phase);
        t.Tick(4f);
        Assert.Equal(TrackerPhase.Ended, t.Phase);
        t.Tick(1.5f);
        Assert.Equal(TrackerPhase.Hidden, t.Phase);
        t.OnJob(new CartelJobMsg(5, JobState.Failed, JobFailReason.Death, "test", 1, 7, 0, 0, CartelJobFlags.None));
        Assert.Equal(TrackerPhase.Ended, t.Phase);
        t.OnJob(Active(6, 30f)); // a new Job replaces the card
        Assert.Equal(TrackerPhase.Running, t.Phase);
        Assert.Equal("", t.Line);
    }

    [Fact]
    public void LeavingThePartyMidwayHidesTheTracker()
    {
        var t = new CartelTracker(5f);
        t.OnJob(Active(2, 60f, CartelJobFlags.NotMember));
        t.OnJob(CartelJobMsg.None);
        Assert.Equal(TrackerPhase.Hidden, t.Phase);
        Assert.Null(t.Marker);
        t.OnJob(Active(3, 60f));
        t.Clear();
        Assert.Equal(TrackerPhase.Hidden, t.Phase);
        Assert.Equal(0, t.Job.JobId);
    }

    [Fact]
    public void AnOutsiderIsOfferedJoinAndAMemberTheRankTheBarAndTheJobs()
    {
        var outsider = ContactPanelModel.Build(Shipped, new CartelStatusMsg(CartelMembership.Outsider, 0, 0, CartelStatusFlags.Guest), admin: false, leader: true, jobRunning: false, roundRunning: true);
        Assert.False(outsider.IsMember);
        Assert.True(outsider.Guest);
        Assert.Equal(ContactStartBlock.NotMember, outsider.StartBlock);

        var respect = 1500; // a Dealer, halfway to Sicario (1000 to 3000)
        var m = ContactPanelModel.Build(Shipped, new CartelStatusMsg(CartelMembership.Member, respect, (byte)Shipped.RankFor(respect), CartelStatusFlags.None), false, true, false, true);
        Assert.Equal("Dealer", m.RankName);
        Assert.Equal("Sicario", m.NextRankName);
        Assert.Equal(1500, m.RespectToNext);
        Assert.Equal(0.25f, m.BarFraction, 3);
        Assert.Equal(ContactStartBlock.None, m.StartBlock);
        Assert.DoesNotContain(m.Jobs, j => j.Kind == TestJobKind.KindId); // only admins see the test kind
        var admin = ContactPanelModel.Build(Shipped, new CartelStatusMsg(CartelMembership.Member, respect, 1, CartelStatusFlags.None), true, true, false, true);
        var test = Assert.Single(admin.Jobs, j => j.Kind == TestJobKind.KindId);
        Assert.Equal("Test job", test.Name);
        Assert.Equal(new[] { true, true, false, false, false }, test.Tiers.Select(t => t.Open));
        Assert.Equal("Sicario", test.Tiers[2].NeedsRank);
        Assert.Equal((TestJobKind.KindId, 2), admin.FirstChoice);

        var top = ContactPanelModel.Build(Shipped, new CartelStatusMsg(CartelMembership.Member, 20000, 4, CartelStatusFlags.None), false, true, false, true);
        Assert.Null(top.NextRankName);
        Assert.Equal(1f, top.BarFraction);
    }

    [Fact]
    public void StartIsTheLeadersWithARoundAndNoJobRunning()
    {
        var s = new CartelStatusMsg(CartelMembership.Member, 0, 0, CartelStatusFlags.None);
        Assert.Equal(ContactStartBlock.Loading, ContactPanelModel.Build(Shipped, null, false, true, false, true).StartBlock);
        Assert.Equal(ContactStartBlock.NotLeader, ContactPanelModel.Build(Shipped, s, false, leader: false, false, true).StartBlock);
        Assert.Equal(ContactStartBlock.NoRound, ContactPanelModel.Build(Shipped, s, false, true, false, roundRunning: false).StartBlock);
        Assert.Equal(ContactStartBlock.JobRunning, ContactPanelModel.Build(Shipped, s, false, true, jobRunning: true, true).StartBlock);
        Assert.Equal(ContactStartBlock.NotMember, ContactPanelModel.Build(Shipped, s with { Membership = CartelMembership.Quit }, false, true, false, true).StartBlock);
    }

    [Fact]
    public void NearTheContactThePlantationHoldDoesNotCount()
    {
        Assert.True(PlantationRules.HoldCounts(true, true, true, KeyCompetitor.None));
        Assert.False(PlantationRules.HoldCounts(true, true, true, KeyCompetitor.Contact));
    }

    [Fact]
    public void TheContactPanelIsAnOverlayLikeTheLogs()
    {
        var menu = new OverlayContext(false, false, false, false, MenuOpen: true, ChooserOpen: false);
        var chooser = new OverlayContext(false, false, false, false, false, ChooserOpen: true);
        var dead = new OverlayContext(false, Dead: true, false, false, false, false);
        var free = new OverlayContext(false, false, false, false, false, false);
        Assert.True(OverlayArbiter.YieldsToMenu(Overlay.ContactPanel, menu));
        Assert.True(OverlayArbiter.YieldsToMenu(Overlay.ContactPanel, chooser));
        Assert.Equal(OverlayKeyVerdict.Ignore, OverlayArbiter.Key(Overlay.ContactPanel, false, dead));
        Assert.Equal(OverlayKeyVerdict.Open, OverlayArbiter.Key(Overlay.ContactPanel, false, free));
        Assert.True(new MouseOwners(false, false, false, false, false, ContactPanelOpen: true).WantsFreeMouse);
    }

    [Fact]
    public void TheShippedMarkerStepIsTheDefault() => Assert.Equal(CartelFile.Default.MarkerResendM, Shipped.MarkerResendM);
}
