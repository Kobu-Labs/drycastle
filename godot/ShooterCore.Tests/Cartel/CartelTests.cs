using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Cartel's profile side (#494, ADR 0046): cartel.json, the rank curve, membership, who earns, who may start a Job, and the wire.</summary>
public class CartelTests
{
    static readonly CartelFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Cartel;

    public CartelTests() => StateMachineDiagnostics.Strict = true;

    [Fact]
    public void TheShippedFileHasTheFiveRanksAndATierPerRank()
    {
        Assert.Equal(new[] { "Runner", "Dealer", "Sicario", "Capo", "Patrón" }, Shipped.Ranks.Select(r => r.Name));
        Assert.Equal(5, Shipped.Tiers.Count);
        Assert.Equal(Shipped.Ranks.Select(r => r.Respect), CartelFile.Default.Ranks.Select(r => r.Respect)); // the code's defaults are the shipped numbers
        Assert.Equal(Shipped.Tiers.Select(t => t.Respect), CartelFile.Default.Tiers.Select(t => t.Respect));
        _ = new TestJobKind(Shipped); // its section checks out
    }

    [Fact]
    public void RankFollowsRespectAndTiersOpenWithRank()
    {
        var d = CartelFile.Default;
        Assert.Equal(0, d.RankFor(0));
        Assert.Equal(0, d.RankFor(d.Ranks[1].Respect - 1));
        Assert.Equal(1, d.RankFor(d.Ranks[1].Respect));
        Assert.Equal(4, d.RankFor(int.MaxValue));
        Assert.True(d.TierOpen(1, 0));
        Assert.False(d.TierOpen(2, 0));
        Assert.True(d.TierOpen(2, 1));
        Assert.False(d.TierOpen(0, 4));
        Assert.False(d.TierOpen(6, 4));
        Assert.Equal(1, d.HighestTier(0));
        Assert.Equal(5, d.HighestTier(4));
        Assert.Equal(d.Tiers[2].Respect, d.RespectFor(3));
        Assert.Equal(0, d.RespectFor(9));
    }

    [Theory]
    [InlineData("""{"version":1,"ranks":[{"id":"a","name":"A","respect":5}]}""", "first rank")]
    [InlineData("""{"version":1,"ranks":[{"id":"a","name":"A","respect":0},{"id":"b","name":"B","respect":0}],"tiers":[{"tier":1,"rank":"a","respect":1}]}""", "more than the rank before")]
    [InlineData("""{"version":1,"ranks":[{"id":"a","name":"A","respect":0}],"tiers":[{"tier":2,"rank":"a","respect":1}]}""", "without a gap")]
    [InlineData("""{"version":1,"ranks":[{"id":"a","name":"A","respect":0}],"tiers":[{"tier":1,"rank":"z","respect":1}]}""", "unknown rank")]
    [InlineData("""{"version":1,"ranks":[{"id":"a","name":"A","respect":0}],"tiers":[{"tier":1,"rank":"a","respect":0}]}""", "more than 0")]
    public void ABadFileIsRefusedWithTheField(string json, string says)
    {
        var e = Assert.Throws<DataFileException>(() => CartelFile.Parse(json));
        Assert.Contains(says, e.Message);
    }

    [Fact]
    public void TheTestKindNeedsATimerPerTier()
    {
        var bad = CartelFile.Parse("""{"version":1,"jobs":{"test":{"seconds":[10,20]}}}""");
        Assert.Throws<DataFileException>(() => new TestJobKind(bad));
        var kind = new TestJobKind(CartelFile.Parse("""{"version":1,"jobs":{"test":{"seconds":[10,20,30,40,50],"succeed_seconds":3}}}"""));
        Assert.Equal(30, kind.Seconds(3));
    }

    [Fact]
    public void MembershipJoinsQuitsAndRejoinsWithRespectFrozenMeanwhile()
    {
        var d = CartelFile.Default;
        var s = new CartelStanding();
        Assert.Equal(CartelMembership.Outsider, s.Membership);
        Assert.Equal(0, s.AddRespect(100, d).Added); // an outsider earns nothing
        Assert.True(s.Join());
        Assert.False(s.Join());
        var change = s.AddRespect(d.Ranks[1].Respect, d);
        Assert.True(change.RankedUp);
        Assert.Equal((0, 1), (change.Before, change.After));
        Assert.False(s.AddRespect(1, d).RankedUp);
        Assert.True(s.Quit());
        Assert.False(s.Quit());
        Assert.Equal(0, s.AddRespect(500, d).Added); // frozen
        Assert.Equal(d.Ranks[1].Respect + 1, s.Respect);
        Assert.True(s.Join());
        Assert.Equal(1, s.Rank(d));
        Assert.Equal(new CartelRecord(CartelMembership.Member, d.Ranks[1].Respect + 1), s.Record);
        Assert.Equal(int.MaxValue - s.Respect, s.AddRespect(int.MaxValue, d).Added); // stops at the top
    }

    [Fact]
    public void AStoredStandingLoadsAndAnUnknownByteIsAnOutsider()
    {
        var s = CartelStanding.From(new CartelRecord(CartelMembership.Quit, 4000));
        Assert.Equal(CartelMembership.Quit, s.Membership);
        Assert.Equal(2, s.Rank(CartelFile.Default)); // Sicario
        Assert.Equal(CartelMembership.Outsider, CartelStanding.From(new CartelRecord((CartelMembership)9, -5)).Membership);
        Assert.Equal(0, CartelStanding.From(new CartelRecord(CartelMembership.Member, -5)).Respect);
    }

    [Fact]
    public void BotsAndGuestsCannotJoinAndOnlyStoredMembersOutOfSandboxEarn()
    {
        var member = new CartelStanding();
        member.Join();
        Assert.Equal(CartelJoinRefusal.Bot, CartelRules.CanJoin(true, true, new CartelStanding()));
        Assert.Equal(CartelJoinRefusal.Guest, CartelRules.CanJoin(false, false, new CartelStanding()));
        Assert.Equal(CartelJoinRefusal.AlreadyMember, CartelRules.CanJoin(false, true, member));
        Assert.Equal(CartelJoinRefusal.None, CartelRules.CanJoin(false, true, new CartelStanding()));
        Assert.True(CartelRules.Earns(false, true, false, member));
        Assert.False(CartelRules.Earns(false, true, true, member)); // sandbox
        Assert.False(CartelRules.Earns(false, false, false, member)); // guest
        Assert.False(CartelRules.Earns(true, true, false, member)); // bot
        Assert.False(CartelRules.Earns(false, true, false, new CartelStanding())); // not in the Cartel
    }

    static JobStartFacts Ok => new(Bot: false, Member: true, IsLeader: true, KindKnown: true, Tier: 1, Rank: 0, RoundRunning: true, Alive: true, AtContact: true, OwnerBusy: false, MateBusy: false);

    [Fact]
    public void StartingAJobChecksInOrder()
    {
        var d = CartelFile.Default;
        Assert.Equal(JobStartRefusal.None, CartelRules.CanStart(d, Ok));
        Assert.Equal(JobStartRefusal.Bot, CartelRules.CanStart(d, Ok with { Bot = true, Member = false }));
        Assert.Equal(JobStartRefusal.NotMember, CartelRules.CanStart(d, Ok with { Member = false, IsLeader = false }));
        Assert.Equal(JobStartRefusal.NotLeader, CartelRules.CanStart(d, Ok with { IsLeader = false }));
        Assert.Equal(JobStartRefusal.UnknownKind, CartelRules.CanStart(d, Ok with { KindKnown = false }));
        Assert.Equal(JobStartRefusal.UnknownTier, CartelRules.CanStart(d, Ok with { Tier = 6 }));
        Assert.Equal(JobStartRefusal.TierLocked, CartelRules.CanStart(d, Ok with { Tier = 2 }));
        Assert.Equal(JobStartRefusal.None, CartelRules.CanStart(d, Ok with { Tier = 2, Rank = 1 }));
        Assert.Equal(JobStartRefusal.None, CartelRules.CanStart(d, Ok with { Tier = 5, AtContact = false, IgnoreRank = true })); // the admin's test command
        Assert.Equal(JobStartRefusal.NoRound, CartelRules.CanStart(d, Ok with { RoundRunning = false }));
        Assert.Equal(JobStartRefusal.Dead, CartelRules.CanStart(d, Ok with { Alive = false }));
        Assert.Equal(JobStartRefusal.FarFromContact, CartelRules.CanStart(d, Ok with { AtContact = false }));
        Assert.Equal(JobStartRefusal.OwnerBusy, CartelRules.CanStart(d, Ok with { OwnerBusy = true, MateBusy = true }));
        Assert.Equal(JobStartRefusal.MateBusy, CartelRules.CanStart(d, Ok with { MateBusy = true }));
        Assert.Equal("Tier 2 needs the rank Dealer.", CartelRules.Words(JobStartRefusal.TierLocked, d, 2));
        Assert.Equal("Your leader starts jobs.", CartelRules.Words(JobStartRefusal.NotLeader, d, 1));
    }

    [Fact]
    public void TheWireRoundTripsAndRefusesBadPayloads()
    {
        var req = new CartelRequest(CartelAction.StartJob, "delivery", 3);
        Assert.True(CartelCodec.TryDecodeRequest(CartelCodec.EncodeRequest(req), out var req2));
        Assert.Equal(req, req2);
        Assert.True(CartelCodec.TryDecodeRequest(CartelCodec.EncodeRequest(new CartelRequest(CartelAction.Quit)), out var quit));
        Assert.Equal(CartelAction.Quit, quit.Action);
        Assert.False(CartelCodec.TryDecodeRequest(new byte[] { 9, 0, 0 }, out _));
        Assert.False(CartelCodec.TryDecodeRequest(new byte[] { 3, 1, 5, 1 }, out _)); // the kind is cut short
        Assert.Throws<ArgumentException>(() => CartelCodec.EncodeRequest(new CartelRequest(CartelAction.StartJob, new string('x', 40), 1)));

        var st = new CartelStatusMsg(CartelMembership.Member, 123456, 2, CartelStatusFlags.Sandbox | CartelStatusFlags.RankedUp);
        Assert.True(CartelCodec.TryDecodeStatus(CartelCodec.EncodeStatus(st), out var st2));
        Assert.Equal(st, st2);
        Assert.False(CartelCodec.TryDecodeStatus(new byte[6], out _));

        var job = new CartelJobMsg(77, JobState.Failed, JobFailReason.Death, "test", 4, 513, 12.5f, 0, CartelJobFlags.NotMember);
        Assert.True(CartelCodec.TryDecodeJob(CartelCodec.EncodeJob(job), out var job2));
        Assert.Equal(job, job2);
        Assert.True(CartelCodec.TryDecodeJob(CartelCodec.EncodeJob(CartelJobMsg.None), out var none));
        Assert.Equal(0, none.JobId);
        Assert.False(CartelCodec.TryDecodeJob(new byte[10], out _));

        Assert.True(CartelCodec.TryDecodeRankUp(CartelCodec.EncodeRankUp(300, 4), out var who, out var rank));
        Assert.Equal((300, 4), (who, (int)rank));

        var guest = job with { Flags = CartelJobFlags.Guest | CartelJobFlags.Leader };
        Assert.True(CartelCodec.TryDecodeJob(CartelCodec.EncodeJob(guest), out var guest2));
        Assert.Equal(guest, guest2);

        var no = new CartelRefusalMsg(CartelAction.StartJob, (byte)JobStartRefusal.CannotBegin, "No drop point is free now: another job holds every one.");
        Assert.True(CartelCodec.TryDecodeRefusal(CartelCodec.EncodeRefusal(no), out var no2));
        Assert.Equal(no, no2);
        Assert.False(CartelCodec.TryDecodeRefusal(new byte[] { 9, 0, 0 }, out _));
        Assert.False(CartelCodec.TryDecodeRefusal(new byte[] { 1, 0, 4, 65 }, out _)); // the words are cut short
    }

    [Fact]
    public void EveryReasonHasOneKeyAndOneText()
    {
        Assert.Equal("Time ran out", CartelRules.Words(JobFailReason.Timer));
        Assert.Equal("cartel.fail.timer", CartelWords.Fail.Key(JobFailReason.Timer));
        Assert.Equal("Tiers go from 1 to 5.", CartelRules.Words(JobStartRefusal.UnknownTier, CartelFile.Default, 9));
        Assert.Equal(CartelWords.Join.Text(CartelJoinRefusal.Guest), CartelRules.Words(CartelJoinRefusal.Guest));
        foreach (var r in Enum.GetValues<JobFailReason>()) Assert.NotEmpty(CartelWords.Fail.Text(r));
        foreach (var r in Enum.GetValues<JobStartRefusal>().Where(r => r is not (JobStartRefusal.None or JobStartRefusal.CannotBegin)))
            Assert.NotEqual(CartelWords.Start.Key(JobStartRefusal.None), CartelWords.Start.Key(r)); // its own words, not the fallback's
        var keys = CartelWords.Entries.Select(e => e.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count()); // one key, one text: the client registers them all
        Assert.Equal(NoRespectReason.Guest, CartelRules.NoRespect(false, stored: false, sandbox: true, null));
        Assert.Equal(NoRespectReason.Sandbox, CartelRules.NoRespect(false, true, sandbox: true, new CartelStanding()));
    }

    [Fact]
    public void The_admins_job_commands_take_win_or_fail_and_whole_seconds()
    {
        Assert.True(CartelAdmin.ParseEnd("win"));
        Assert.False(CartelAdmin.ParseEnd(" FAIL "));
        Assert.Null(CartelAdmin.ParseEnd("lose"));
        Assert.Equal(90, CartelAdmin.ParseSeconds("90"));
        Assert.Equal(CartelAdmin.MaxTimerSeconds, CartelAdmin.ParseSeconds(CartelAdmin.MaxTimerSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Null(CartelAdmin.ParseSeconds("0"));
        Assert.Null(CartelAdmin.ParseSeconds("-5"));
        Assert.Null(CartelAdmin.ParseSeconds("1.5"));
        Assert.Null(CartelAdmin.ParseSeconds((CartelAdmin.MaxTimerSeconds + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var admin = new ShooterCore.Chat.ChatContext(true, Array.Empty<string>());
        foreach (var line in new[] { "/cartel end win", "/cartel time 60", "/cartel assault", "/cartel goto" }) Assert.True(ShooterCore.Chat.ChatCommands.Parse(line, admin).Ok, line);
        Assert.Equal(new[] { "end", "fail" }, ShooterCore.Chat.ChatCommands.Parse("/cartel END fail", admin).Args);
        Assert.False(ShooterCore.Chat.ChatCommands.Parse("/cartel finish", admin).Ok);
    }
}
