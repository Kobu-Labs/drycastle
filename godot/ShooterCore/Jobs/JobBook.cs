namespace Drycastle.ShooterCore.Jobs;

/// <summary>A Job that ended this tick, with the party as it was at that moment (on success, each of them who earns is paid).</summary>
public sealed record JobEnded(Job Job, IReadOnlyList<ushort> Members)
{
    public bool Succeeded => Job.State == JobState.Succeeded;
}

/// <summary>
/// Every running Job (ADR 0045, docs/cartel.md "Who runs a job", "Failure"): one per owner (a solo player or a Squad), as many owners as like at once, beside any Event. It steps
/// each Job's objective and timer, ends Jobs on a death, a quit, a round reset or when nobody is left, and holds the named things of the world Jobs have claimed (a drop point
/// id, a house), so two Jobs never share one. Who is in a party is <see cref="IJobParties"/>'s, read into each Job's <see cref="Job.Party"/> at its start and each tick; what a Job pays is
/// its giver's. Pure; the server owns one.
/// </summary>
public sealed class JobBook
{
    readonly Dictionary<string, IJobKind> _kinds = new(StringComparer.Ordinal);
    readonly List<Job> _running = new();
    readonly Dictionary<string, int> _reserved = new(StringComparer.Ordinal);
    int _lastId;

    public IReadOnlyList<Job> Running => _running;
    public IEnumerable<IJobKind> Kinds => _kinds.Values;

    /// <summary>Adds a kind of Job. Its id must be new.</summary>
    public void Register(IJobKind kind)
    {
        if (!_kinds.TryAdd(kind.Id, kind)) throw new InvalidOperationException($"two kinds of Job are named '{kind.Id}'");
    }

    public IJobKind? Kind(string id) => _kinds.GetValueOrDefault(id);

    /// <summary>The owner's running Job, or null.</summary>
    public Job? OfOwner(JobOwner owner)
    {
        foreach (var j in _running) if (j.Owner == owner) return j;
        return null;
    }

    /// <summary>The running Job whose party the player is in now, or null.</summary>
    public Job? Involving(ushort player, IJobParties parties)
    {
        foreach (var j in _running) if (parties.Has(j.Owner, player)) return j;
        return null;
    }

    /// <summary>The job claims a named thing of the world (<c>"cartel_drop:3"</c>). True when it holds it now (also when it already did); false when another Job does.</summary>
    public bool Reserve(Job job, string resource)
    {
        if (_reserved.TryGetValue(resource, out var holder)) return holder == job.Id;
        _reserved[resource] = job.Id;
        return true;
    }

    public bool IsReserved(string resource) => _reserved.ContainsKey(resource);

    /// <summary>What the job has claimed.</summary>
    public IReadOnlyList<string> ReservedBy(Job job) => _reserved.Where(kv => kv.Value == job.Id).Select(kv => kv.Key).ToList();

    /// <summary>
    /// Starts a Job for <paramref name="owner"/>, led by <paramref name="leader"/>, its party filled from <paramref name="parties"/> before the kind begins. The giver checked who may start
    /// what; this refuses only an owner with a running Job and a kind that cannot begin (its reason). Null with <paramref name="refusal"/> when nothing started.
    /// </summary>
    public Job? Start(string giver, IJobKind kind, int tier, JobOwner owner, ushort leader, IJobParties parties, out string? refusal)
    {
        refusal = null;
        if (OfOwner(owner) != null) { refusal = "A job is running already."; return null; }
        var job = new Job(NextId(), giver, kind, tier, owner, leader, kind.Seconds(tier));
        job.Party.Refresh(parties);
        var objective = kind.Begin(job, this, out refusal);
        if (objective == null)
        {
            Release(job);
            refusal ??= "The job cannot start now.";
            return null;
        }
        job.Objective = objective;
        _running.Add(job);
        return job;
    }

    /// <summary>
    /// One tick: each Job's leader is looked up (none left: it fails, <see cref="JobFailReason.Abandoned"/>) and its party refreshed (<see cref="Job.Party"/>), then its objective steps
    /// (met: success; lost: it fails), then its timer (run out: it fails). What ended is added to <paramref name="ended"/>.
    /// </summary>
    public void Tick(float dt, IJobParties parties, List<JobEnded> ended)
    {
        for (var i = _running.Count - 1; i >= 0; i--)
        {
            var job = _running[i];
            var leader = parties.LeaderOf(job.Owner);
            if (leader == 0) { End(job, JobState.Failed, JobFailReason.Abandoned, parties, ended); continue; }
            job.Leader = leader;
            job.Party.Refresh(parties);
            var step = job.Objective.Step(job, dt);
            if (step == JobStep.Succeeded) End(job, JobState.Succeeded, JobFailReason.None, parties, ended);
            else if (step == JobStep.Failed) End(job, JobState.Failed, JobFailReason.Objective, parties, ended);
            else if (job.Tick(dt)) End(job, JobState.Failed, JobFailReason.Timer, parties, ended);
        }
    }

    /// <summary>A player died: the Job they lead fails (the solo player, or the Squad's leader). Other members respawn and carry on.</summary>
    public void PlayerDied(ushort player, IJobParties parties, List<JobEnded> ended) => FailLedBy(player, JobFailReason.Death, parties, ended);

    /// <summary>A player quit the giver: the Job they lead fails.</summary>
    public void LeaderQuit(ushort player, IJobParties parties, List<JobEnded> ended) => FailLedBy(player, JobFailReason.LeaderQuit, parties, ended);

    /// <summary>
    /// An admin ends a running Job now (<c>/cartel end</c>, a test): won, it succeeds and pays as if its objective was met; else it fails as <see cref="JobFailReason.Admin"/>. False when
    /// the Job is not running.
    /// </summary>
    public bool EndByAdmin(Job job, bool won, IJobParties parties, List<JobEnded> ended)
    {
        if (!_running.Contains(job)) return false;
        End(job, won ? JobState.Succeeded : JobState.Failed, won ? JobFailReason.None : JobFailReason.Admin, parties, ended);
        return true;
    }

    /// <summary>Every Job fails (a round reset).</summary>
    public void FailAll(JobFailReason reason, IJobParties parties, List<JobEnded> ended)
    {
        for (var i = _running.Count - 1; i >= 0; i--) End(_running[i], JobState.Failed, reason, parties, ended);
    }

    void FailLedBy(ushort player, JobFailReason reason, IJobParties parties, List<JobEnded> ended)
    {
        for (var i = _running.Count - 1; i >= 0; i--)
            if (parties.LeaderOf(_running[i].Owner) == player) End(_running[i], JobState.Failed, reason, parties, ended);
    }

    void End(Job job, JobState end, JobFailReason reason, IJobParties parties, List<JobEnded> ended)
    {
        var members = parties.MembersOf(job.Owner).ToList(); // the party at this moment
        job.Finish(end, reason);
        _running.Remove(job);
        Release(job);
        job.Objective.End(job);
        ended.Add(new JobEnded(job, members));
    }

    void Release(Job job)
    {
        foreach (var r in ReservedBy(job)) _reserved.Remove(r);
    }

    int NextId()
    {
        _lastId = _lastId == int.MaxValue ? 1 : _lastId + 1;
        return _lastId;
    }
}
