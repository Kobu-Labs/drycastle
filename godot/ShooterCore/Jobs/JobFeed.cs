using System.Numerics;

namespace Drycastle.ShooterCore.Jobs;

/// <summary>What <see cref="JobFeed"/> asks the server to send one player.</summary>
public enum JobFeedKind : byte
{
    /// <summary>The player's Job as it is now, with their flags (they joined the party, its leader changed, or their flags did).</summary>
    Job,
    /// <summary>"No job": they left the party and no other Job has them.</summary>
    NoJob,
    /// <summary>The Job's objective line and marker (they changed, or the player joined the party).</summary>
    Objective,
}

/// <summary>One message <see cref="JobFeed"/> asks for: to whom, of which Job, with the receiver's flags (<see cref="JobFeedKind.Job"/>) or the line and marker (<see cref="JobFeedKind.Objective"/>).</summary>
public readonly record struct JobFeedMessage(JobFeedKind Kind, ushort To, Job? Job, byte Flags, string Line, Vector3? Marker);

/// <summary>
/// Who is told what about the running Jobs (docs/cartel.md "Contact": the tracker, the marker): each tick it compares every Job's party, leader, the receivers' flags and the
/// objective with what each party member was last told, and asks for what changed: a member who joins midway gets the Job and its objective; one who leaves gets "no job" unless
/// another Job has them now; a new leader or a member's changed flags (they joined the Cartel midway) send the Job again; a new line or a marker that came, went or moved more than
/// the resend distance sends the objective to the whole party. A Job's end is the server's to tell (with what it paid); <see cref="Forget"/> drops it here. Only the receivers
/// asked about are told (the humans: a bot reads nothing). Pure; allocation-free while nothing changes (<see cref="IJobParties.Has"/>, a reused outbox).
/// </summary>
public sealed class JobFeed
{
    /// <summary>What one Job's party was told: the leader, each member's flags (and the tick they were last seen in it), the objective sent.</summary>
    sealed class Told
    {
        public ushort Leader;
        public readonly Dictionary<ushort, (byte Flags, int Seen)> Members = new();
        public bool Sent;
        public string Line = "";
        public Vector3? Marker;
    }

    readonly float _markerResendM;
    readonly Dictionary<int, Told> _told = new();
    int _stamp;

    /// <param name="markerResendM">How far the marker moves before it is sent again (cartel.json <c>marker_resend_m</c>).</param>
    public JobFeed(float markerResendM) => _markerResendM = markerResendM;

    /// <summary>
    /// One tick: adds to <paramref name="outbox"/> what each of <paramref name="receivers"/> must be told about <paramref name="running"/>. <paramref name="flagsOf"/> gives the
    /// receiver's flags for a Job (the leader bit among them); a change sends the Job to them again.
    /// </summary>
    public void Tick(IReadOnlyList<Job> running, IJobParties parties, IReadOnlyList<ushort> receivers, Func<Job, ushort, byte> flagsOf, List<JobFeedMessage> outbox)
    {
        _stamp++;
        for (var j = 0; j < running.Count; j++)
        {
            var job = running[j];
            if (!_told.TryGetValue(job.Id, out var t)) _told[job.Id] = t = new Told();
            var leaderChanged = t.Leader != job.Leader;
            t.Leader = job.Leader;
            var line = job.Objective.Objective(job) ?? "";
            var marker = job.Objective.Marker(job);
            var objectiveChanged = !t.Sent || t.Line != line || !SameMarker(t.Marker, marker);
            if (objectiveChanged) { t.Sent = true; t.Line = line; t.Marker = marker; }
            for (var i = 0; i < receivers.Count; i++)
            {
                var r = receivers[i];
                if (!parties.Has(job.Owner, r))
                {
                    if (t.Members.Remove(r) && !InAnother(running, job, parties, r)) outbox.Add(new JobFeedMessage(JobFeedKind.NoJob, r, null, 0, "", null));
                    continue;
                }
                var flags = flagsOf(job, r);
                var fresh = !t.Members.TryGetValue(r, out var was);
                t.Members[r] = (flags, _stamp);
                if (fresh || leaderChanged || was.Flags != flags) outbox.Add(new JobFeedMessage(JobFeedKind.Job, r, job, flags, "", null));
                if (fresh || objectiveChanged) outbox.Add(new JobFeedMessage(JobFeedKind.Objective, r, job, flags, t.Line, t.Marker));
            }
            foreach (var (id, m) in t.Members) if (m.Seen != _stamp) t.Members.Remove(id); // no longer a receiver (left the server): nobody to tell
        }
    }

    /// <summary>The Job ended (the server told its party, with what it paid): what was told of it goes.</summary>
    public void Forget(int jobId) => _told.Remove(jobId);

    /// <summary>The server ended or reset: nothing was told.</summary>
    public void Clear() => _told.Clear();

    static bool InAnother(IReadOnlyList<Job> running, Job except, IJobParties parties, ushort player)
    {
        for (var j = 0; j < running.Count; j++)
            if (!ReferenceEquals(running[j], except) && parties.Has(running[j].Owner, player)) return true;
        return false;
    }

    bool SameMarker(Vector3? a, Vector3? b) => a is { } x ? b is { } y && Vector3.Distance(x, y) <= _markerResendM : b == null;
}
