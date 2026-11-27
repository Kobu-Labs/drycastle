using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Match;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Jobs;

/// <summary>The generic Job core (#494, ADR 0045): one Job per owner, many at once, reservations, the timer, the failures, the party at the moment it ends.</summary>
public class JobBookTests
{
    public JobBookTests() => StateMachineDiagnostics.Strict = true;

    /// <summary>Parties by hand: squads as lists, the first the leader; a player in no squad is solo.</summary>
    sealed class Parties : IJobParties
    {
        public readonly Dictionary<byte, List<ushort>> Squads = new();
        public readonly HashSet<ushort> Present = new();
        public JobOwner OwnerOf(ushort p) => Squads.FirstOrDefault(kv => kv.Value.Contains(p)) is { Value: not null } kv ? JobOwner.Squad(kv.Key) : JobOwner.Solo(p);
        public ushort LeaderOf(JobOwner o) => MembersOf(o) is { Count: > 0 } m ? m[0] : (ushort)0;
        public IReadOnlyList<ushort> MembersOf(JobOwner o) => o.Kind == JobOwnerKind.Solo
            ? (Present.Contains(o.Id) ? new[] { o.Id } : Array.Empty<ushort>())
            : Squads.TryGetValue((byte)o.Id, out var m) ? m.ToList() : new List<ushort>();
    }

    /// <summary>A kind that claims a named spot, then runs until told (its step is whatever <see cref="Next"/> says).</summary>
    sealed class SpotKind(string spot, float seconds = 60) : IJobKind
    {
        public JobStep Next = JobStep.Running;
        public int Ended;
        public string Id => "spot";
        public string Name => "Spot";
        public float Seconds(int tier) => seconds;
        public IJobObjective? Begin(Job job, JobBook book, out string? refusal)
        {
            refusal = null;
            if (!book.Reserve(job, spot)) { refusal = "taken"; return null; }
            return new Objective(this);
        }
        sealed class Objective(SpotKind k) : IJobObjective
        {
            public JobStep Step(Job job, float dt) => k.Next;
            public int BonusRespect => 30;
            public void End(Job job) => k.Ended++;
        }
    }

    static (JobBook Book, Parties Parties, List<JobEnded> Ended) Setup(params IJobKind[] kinds)
    {
        var book = new JobBook();
        foreach (var k in kinds) book.Register(k);
        var parties = new Parties();
        for (ushort i = 1; i <= 9; i++) parties.Present.Add(i);
        return (book, parties, new List<JobEnded>());
    }

    static Job Start(JobBook book, IJobParties parties, IJobKind kind, ushort leader, int tier = 1) =>
        book.Start(CartelRules.Giver, kind, tier, parties.OwnerOf(leader), leader, out var why) ?? throw new Xunit.Sdk.XunitException(why);

    [Fact]
    public void OneJobPerOwnerButManyOwnersAtOnce()
    {
        var test = new TestJobKind(CartelFile.Default);
        var (book, parties, _) = Setup(test);
        parties.Squads[1] = new() { 3, 4 };
        var a = Start(book, parties, test, 1);
        Assert.Null(book.Start(CartelRules.Giver, test, 1, JobOwner.Solo(1), 1, out var why));
        Assert.NotNull(why);
        var b = Start(book, parties, test, 2);
        var c = Start(book, parties, test, 3);
        Assert.Equal(3, book.Running.Count);
        Assert.Equal(JobOwner.Squad(1), c.Owner);
        Assert.Same(c, book.Involving(4, parties));
        Assert.Same(a, book.OfOwner(JobOwner.Solo(1)));
        Assert.NotEqual(a.Id, b.Id);
        Assert.True(a.Active);
        Assert.Equal(test.Seconds(1), a.SecondsLeft);
    }

    [Fact]
    public void TwoJobsNeverShareAReservedThingAndTheEndReleasesIt()
    {
        var kind = new SpotKind("cartel_drop:3");
        var (book, parties, ended) = Setup(kind);
        var a = Start(book, parties, kind, 1);
        Assert.True(book.IsReserved("cartel_drop:3"));
        Assert.Null(book.Start(CartelRules.Giver, kind, 1, JobOwner.Solo(2), 2, out var why));
        Assert.Equal("taken", why);
        Assert.Single(book.Running);
        Assert.Equal(new[] { "cartel_drop:3" }, book.ReservedBy(a));
        book.PlayerDied(1, parties, ended);
        Assert.False(book.IsReserved("cartel_drop:3"));
        Assert.Equal(1, kind.Ended); // its things are removed
        Assert.NotNull(Start(book, parties, kind, 2));
    }

    [Fact]
    public void TheTestKindSucceedsAfterItsTimeAndTheSquadAtThatMomentIsTheParty()
    {
        var test = new TestJobKind(CartelFile.Parse("""{"version":1,"jobs":{"test":{"seconds":[10,10,10,10,10],"succeed_seconds":1}}}"""));
        var (book, parties, ended) = Setup(test);
        parties.Squads[5] = new() { 1, 2 };
        var job = Start(book, parties, test, 1);
        for (var i = 0; i < 20; i++) book.Tick(1 / 30f, parties, ended);
        Assert.Empty(ended);
        parties.Squads[5].Add(3); // joined midway: paid
        parties.Squads[5].Remove(2); // left: not
        for (var i = 0; i < 20 && ended.Count == 0; i++) book.Tick(1 / 30f, parties, ended);
        var end = Assert.Single(ended);
        Assert.True(end.Succeeded);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(new ushort[] { 1, 3 }, end.Members);
        Assert.Empty(book.Running);
        Assert.Equal(CartelFile.Default.RespectFor(1), CartelRules.Reward(CartelFile.Default, job));
    }

    [Fact]
    public void TheTimerFailsItAndTheObjectiveCanEndIt()
    {
        var kind = new SpotKind("a", seconds: 1);
        var (book, parties, ended) = Setup(kind);
        var job = Start(book, parties, kind, 1);
        for (var i = 0; i < 31; i++) book.Tick(1 / 30f, parties, ended);
        Assert.Equal(JobFailReason.Timer, Assert.Single(ended).Job.Reason);
        Assert.Equal(JobState.Failed, job.State);

        ended.Clear();
        var lost = Start(book, parties, kind, 1);
        kind.Next = JobStep.Failed;
        book.Tick(0.1f, parties, ended);
        Assert.Equal(JobFailReason.Objective, lost.Reason);

        ended.Clear();
        kind.Next = JobStep.Succeeded;
        var won = Start(book, parties, kind, 1, tier: 2);
        book.Tick(0.1f, parties, ended);
        Assert.True(Assert.Single(ended).Succeeded);
        Assert.Equal(CartelFile.Default.RespectFor(2) + 30, CartelRules.Reward(CartelFile.Default, won)); // the tier's and the objective's bonus
    }

    [Fact]
    public void InASquadOnlyTheLeadersDeathOrQuitFailsIt()
    {
        var kind = new SpotKind("a");
        var (book, parties, ended) = Setup(kind);
        parties.Squads[1] = new() { 1, 2, 3 };
        var job = Start(book, parties, kind, 1);
        book.PlayerDied(2, parties, ended);
        book.LeaderQuit(3, parties, ended);
        Assert.Empty(ended);
        book.LeaderQuit(1, parties, ended);
        Assert.Equal(JobFailReason.LeaderQuit, job.Reason);

        ended.Clear();
        parties.Squads[1].Remove(1); // the leader left: the next takes over, the job goes with them
        var again = Start(book, parties, kind, 2);
        book.Tick(0.1f, parties, ended);
        Assert.Equal(2, again.Leader);
        parties.Squads[1] = new() { 3, 2 }; // a new leader (the seam's order)
        book.Tick(0.1f, parties, ended);
        Assert.Equal(3, again.Leader);
        book.PlayerDied(2, parties, ended);
        Assert.Empty(ended);
        book.PlayerDied(3, parties, ended);
        Assert.Equal(JobFailReason.Death, again.Reason);
    }

    [Fact]
    public void ARoundResetFailsEveryJobAndAnOwnerWithNobodyLeftAbandonsIt()
    {
        var test = new TestJobKind(CartelFile.Default);
        var (book, parties, ended) = Setup(test);
        Start(book, parties, test, 1);
        Start(book, parties, test, 2);
        book.FailAll(JobFailReason.RoundReset, parties, ended);
        Assert.Equal(2, ended.Count);
        Assert.All(ended, e => Assert.Equal(JobFailReason.RoundReset, e.Job.Reason));
        Assert.Empty(book.Running);

        ended.Clear();
        var job = Start(book, parties, test, 4);
        parties.Present.Remove(4); // left the server
        book.Tick(0.1f, parties, ended);
        Assert.Equal(JobFailReason.Abandoned, job.Reason);
        Assert.Empty(Assert.Single(ended).Members);
    }

    [Fact]
    public void TheMatchMakesFfaSoloAndATdmSquadThePartyLedByItsLeader()
    {
        double t = 0;
        var ffa = new MatchSim(new MatchConfig { Mode = "ffa" }, () => t, new[] { Vector3.Zero }, new Random(1));
        ffa.AddPlayer(1, "a"); ffa.AddPlayer(2, "b");
        var p = new MatchJobParties(ffa);
        Assert.Equal(JobOwner.Solo(1), p.OwnerOf(1));
        Assert.Equal(1, p.LeaderOf(JobOwner.Solo(1)));
        Assert.Equal(new ushort[] { 2 }, p.MembersOf(JobOwner.Solo(2)));
        ffa.RemovePlayer(2);
        Assert.Equal(0, p.LeaderOf(JobOwner.Solo(2)));

        var tdm = new MatchSim(new MatchConfig { Mode = "tdm" }, () => t, new[] { Vector3.Zero, new Vector3(100, 0, 0) }, new Random(1));
        for (ushort i = 1; i <= 4; i++) { tdm.AddPlayer(i, $"p{i}"); tdm.SetTeam(i, Teams.A); }
        Assert.Equal(SquadResult.Ok, tdm.CreateSquad(3, "x", out var squad));
        Assert.Equal(SquadResult.Ok, tdm.JoinSquad(1, squad));
        var q = new MatchJobParties(tdm);
        Assert.Equal(JobOwner.Squad(squad), q.OwnerOf(1));
        Assert.Equal(JobOwner.Solo(2), q.OwnerOf(2)); // in no squad: alone
        Assert.Equal(new ushort[] { 3, 1 }, q.MembersOf(JobOwner.Squad(squad))); // longest-standing first
        Assert.Equal(3, q.LeaderOf(JobOwner.Squad(squad))); // the creator leads
        tdm.LeaveSquad(3);
        Assert.Equal(1, q.LeaderOf(JobOwner.Squad(squad))); // then the longest-standing member
        tdm.LeaveSquad(1);
        Assert.Equal(0, q.LeaderOf(JobOwner.Squad(squad)));
        Assert.Empty(q.MembersOf(JobOwner.Squad(squad)));
    }

    [Fact]
    public void ASoloPlayerWhoJoinsASquadLeavesTheSoloJobForTheSquads()
    {
        double t = 0;
        var tdm = new MatchSim(new MatchConfig { Mode = "tdm" }, () => t, new[] { Vector3.Zero, new Vector3(100, 0, 0) }, new Random(1));
        for (ushort i = 1; i <= 3; i++) { tdm.AddPlayer(i, $"p{i}"); tdm.SetTeam(i, Teams.A); }
        Assert.Equal(SquadResult.Ok, tdm.CreateSquad(3, "s", out var squad));
        var parties = new MatchJobParties(tdm);
        var kind = new SpotKind("a");
        var other = new SpotKind("b");
        var book = new JobBook();
        book.Register(kind);
        var ended = new List<JobEnded>();
        var solo = Start(book, parties, kind, 1);
        Assert.Equal(JobOwner.Solo(1), solo.Owner);
        var squadJob = book.Start(CartelRules.Giver, other, 1, parties.OwnerOf(3), 3, out var why) ?? throw new Xunit.Sdk.XunitException(why);
        Assert.True(parties.Has(solo.Owner, 1));
        Assert.False(parties.Has(squadJob.Owner, 1));

        Assert.Equal(SquadResult.Ok, tdm.JoinSquad(1, squad));
        Assert.False(parties.Has(solo.Owner, 1)); // one party at most
        Assert.True(parties.Has(squadJob.Owner, 1));
        Assert.Equal(0, parties.LeaderOf(solo.Owner));
        Assert.Empty(parties.MembersOf(solo.Owner));
        book.Tick(0.1f, parties, ended);
        var end = Assert.Single(ended);
        Assert.Same(solo, end.Job);
        Assert.Equal(JobFailReason.Abandoned, solo.Reason);
        Assert.Same(squadJob, book.Involving(1, parties));
        Assert.True(squadJob.Active);
    }
}
