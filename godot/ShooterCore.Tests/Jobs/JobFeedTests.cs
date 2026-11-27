using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Jobs;

/// <summary>Who is told what about the running Jobs (<see cref="JobFeed"/>): joiners midway, leavers, a new leader, changed flags, the marker's resend distance.</summary>
public class JobFeedTests
{
    public JobFeedTests() => StateMachineDiagnostics.Strict = true;

    sealed class Parties : IJobParties
    {
        public readonly Dictionary<byte, List<ushort>> Squads = new();
        public JobOwner OwnerOf(ushort p) => Squads.FirstOrDefault(kv => kv.Value.Contains(p)) is { Value: not null } kv ? JobOwner.Squad(kv.Key) : JobOwner.Solo(p);
        public ushort LeaderOf(JobOwner o) => MembersOf(o) is { Count: > 0 } m ? m[0] : (ushort)0;
        public IReadOnlyList<ushort> MembersOf(JobOwner o) => o.Kind == JobOwnerKind.Solo
            ? (Squads.Values.Any(s => s.Contains(o.Id)) ? Array.Empty<ushort>() : new[] { o.Id })
            : Squads.TryGetValue((byte)o.Id, out var m) ? m.ToList() : new List<ushort>();
    }

    /// <summary>A kind whose objective line and marker are whatever the test sets.</summary>
    sealed class Said(string id) : IJobKind
    {
        public string Line = "Go";
        public Vector3? At = Vector3.Zero;
        public string Id => id;
        public string Name => id;
        public float Seconds(int tier) => 600;
        public IJobObjective? Begin(Job job, JobBook book, out string? refusal) { refusal = null; return new Run(this); }
        sealed class Run(Said k) : IJobObjective
        {
            public JobStep Step(Job job, float dt) => JobStep.Running;
            public string Objective(Job job) => k.Line;
            public Vector3? Marker(Job job) => k.At;
        }
    }

    sealed class Rig
    {
        public readonly JobBook Book = new();
        public readonly Parties Parties = new();
        public readonly JobFeed Feed = new(5f);
        public readonly List<ushort> Receivers = new() { 1, 2, 3, 4 };
        public readonly Dictionary<ushort, byte> Flags = new();
        public readonly List<JobFeedMessage> Out = new();
        readonly List<JobEnded> _ended = new();

        public Job Start(IJobKind kind, ushort leader) =>
            Book.Start(CartelRules.Giver, kind, 1, Parties.OwnerOf(leader), leader, out var why) ?? throw new Xunit.Sdk.XunitException(why);

        /// <summary>One tick of the book, then the feed; what it asked for.</summary>
        public List<JobFeedMessage> Tick()
        {
            Book.Tick(0.1f, Parties, _ended);
            Out.Clear();
            Feed.Tick(Book.Running, Parties, Receivers, (job, p) => (byte)(Flags.GetValueOrDefault(p) | (job.Leader == p ? 4 : 0)), Out);
            return Out;
        }
    }

    static IEnumerable<ushort> To(IEnumerable<JobFeedMessage> m, JobFeedKind kind) => m.Where(x => x.Kind == kind).Select(x => x.To).OrderBy(x => x);

    [Fact]
    public void TheStartTellsThePartyThenNothingWhileNothingChanges()
    {
        var r = new Rig();
        r.Parties.Squads[1] = new() { 1, 2 };
        var job = r.Start(new Said("a"), 1);
        var first = r.Tick();
        Assert.Equal(new ushort[] { 1, 2 }, To(first, JobFeedKind.Job));
        Assert.Equal(new ushort[] { 1, 2 }, To(first, JobFeedKind.Objective));
        Assert.All(first, m => Assert.Same(job, m.Job));
        Assert.Equal(4, first.Single(m => m.To == 1 && m.Kind == JobFeedKind.Job).Flags); // the leader bit
        Assert.Empty(r.Tick());
    }

    [Fact]
    public void AMemberWhoJoinsMidwayGetsTheJobAndItsObjective()
    {
        var r = new Rig();
        r.Parties.Squads[1] = new() { 1, 2 };
        var kind = new Said("a") { Line = "Take it", At = new Vector3(10, 0, 0) };
        r.Start(kind, 1);
        r.Tick();
        r.Parties.Squads[1].Add(3);
        var m = r.Tick();
        Assert.Equal(new ushort[] { 3 }, To(m, JobFeedKind.Job));
        var o = Assert.Single(m, x => x.Kind == JobFeedKind.Objective);
        Assert.Equal((3, "Take it", new Vector3(10, 0, 0)), (o.To, o.Line, o.Marker!.Value));
    }

    [Fact]
    public void ALeaverIsToldNoJobUnlessAnotherJobHasThem()
    {
        var r = new Rig();
        r.Parties.Squads[1] = new() { 1, 2, 3 };
        r.Parties.Squads[2] = new() { 4 };
        r.Start(new Said("a"), 1);
        r.Start(new Said("b"), 4);
        r.Tick();
        r.Parties.Squads[1].Remove(2); // alone now, no job
        var m = r.Tick();
        Assert.Equal(new ushort[] { 2 }, To(m, JobFeedKind.NoJob));
        r.Parties.Squads[1].Remove(3);
        r.Parties.Squads[2].Add(3); // to the other squad's job
        m = r.Tick();
        Assert.Empty(To(m, JobFeedKind.NoJob));
        Assert.Equal(new ushort[] { 3 }, To(m, JobFeedKind.Job));
        Assert.Equal("b", m.Single(x => x.Kind == JobFeedKind.Job).Job!.Kind.Id);
        r.Receivers.Remove(4); // left the server: nobody to tell
        Assert.Empty(r.Tick());
    }

    [Fact]
    public void ANewLeaderOrChangedFlagsSendTheJobAgain()
    {
        var r = new Rig();
        r.Parties.Squads[1] = new() { 1, 2, 3 };
        var job = r.Start(new Said("a"), 1);
        r.Tick();
        r.Parties.Squads[1].Remove(1); // the leader left: 2 leads
        var m = r.Tick();
        Assert.Equal(2, job.Leader);
        Assert.Equal(new ushort[] { 2, 3 }, To(m, JobFeedKind.Job));
        Assert.Equal(new ushort[] { 1 }, To(m, JobFeedKind.NoJob));
        Assert.Empty(To(m, JobFeedKind.Objective));
        r.Flags[3] = 1; // 3 joined the Cartel midway: earns now
        m = r.Tick();
        var again = Assert.Single(m);
        Assert.Equal((JobFeedKind.Job, (ushort)3, (byte)1), (again.Kind, again.To, again.Flags));
    }

    [Fact]
    public void TheObjectiveGoesAgainOnANewLineOrAMarkerThatMovedFarEnough()
    {
        var r = new Rig();
        var kind = new Said("a") { At = Vector3.Zero };
        r.Start(kind, 1);
        r.Tick();
        kind.At = new Vector3(4, 0, 0); // within the 5 m resend distance
        Assert.Empty(r.Tick());
        kind.At = new Vector3(6, 0, 0); // 6 m from what was sent
        var o = Assert.Single(r.Tick());
        Assert.Equal((JobFeedKind.Objective, new Vector3(6, 0, 0)), (o.Kind, o.Marker!.Value));
        kind.At = null; // gone
        Assert.Null(Assert.Single(r.Tick()).Marker);
        kind.Line = "Now this";
        Assert.Equal("Now this", Assert.Single(r.Tick()).Line);
    }

    [Fact]
    public void AForgottenJobIsToldAfreshIfItRunsOn()
    {
        var r = new Rig();
        var job = r.Start(new Said("a"), 1);
        r.Tick();
        r.Feed.Forget(job.Id);
        Assert.Equal(2, r.Tick().Count); // the Job and its objective again
    }
}
