using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Whether the "Job done" banner is up (machine <c>Cartel.JobDone</c>).</summary>
public enum JobDoneState : byte { Idle, Showing }

/// <summary>
/// The second line of the "Job done" banner: the Respect this player earned ("+N Respect"), or why they earned none (a guest, not in the Cartel, sandbox: <see cref="Why"/>), or nothing at all
/// (<see cref="Hidden"/>: a member who earns, paid 0).
/// </summary>
public readonly record struct JobDoneDetail(int Respect, NoRespectReason? Why)
{
    public bool Earned => Respect > 0;
    public bool Hidden => Respect <= 0 && Why == null;
}

/// <summary>
/// The "Job done" banner and the camera pull-back that goes with it (docs/cartel.md "Job done"), pure: the party's players are sent the Job's end (<see cref="CartelJobMsg"/>); when a Job
/// they ran succeeds, the banner shows for <see cref="Seconds"/> with the Respect that player was paid, a jingle plays, and the camera pulls back and up from the body (<see cref="Pull"/>:
/// 0 at the eye, 1 fully out; it eases out over <see cref="EaseSeconds"/>, holds, and eases back over the last <see cref="EaseSeconds"/>). Each Job announces once, however often its end message is
/// repeated. The client owns one; the HUD draws the banner and the camera hook reads <see cref="Pull"/>.
/// </summary>
public sealed class JobDone
{
    /// <summary>How long the banner and the camera move last.</summary>
    public const float Seconds = 4f;
    /// <summary>The camera eases out over this long at the start and back over this long at the end.</summary>
    public const float EaseSeconds = 1.2f;

    public static readonly StateMachineSpec<JobDoneState> Spec = StateMachineSpec<JobDoneState>
        .Define("Cartel.JobDone", "The \"Job done\" banner of a succeeded Cartel Job (with its jingle and the camera's pull-back) for the party's players: up for about 4 s, then gone.", JobDoneState.Idle)
        .Allow(JobDoneState.Idle, JobDoneState.Showing)
        .Allow(JobDoneState.Showing, JobDoneState.Idle, JobDoneState.Showing)
        .Note(JobDoneState.Idle, "No banner; the camera is the player's own.")
        .Note(JobDoneState.Showing, "The banner shows the Respect (or why none); the clock runs the banner's seconds and sets how far the camera is pulled back (out, hold, back). A Job that succeeds while it shows replaces it.")
        .Build();

    readonly StateMachine<JobDoneState> _fsm = new(Spec);
    /// <summary>The last Job announced (its end message may come again; a Job is announced once).</summary>
    Change<int> _announced;

    public bool Showing => _fsm.Is(JobDoneState.Showing);
    /// <summary>What the banner's second line says now (while <see cref="Showing"/>).</summary>
    public JobDoneDetail Detail { get; private set; }
    /// <summary>Goes up with each banner: the HUD rewrites its text only then.</summary>
    public int Revision { get; private set; }

    /// <summary>The Job message: a success not announced yet starts the banner. True when it did.</summary>
    public bool OnJob(CartelJobMsg m)
    {
        if (m.JobId == 0 || m.State != JobState.Succeeded || !_announced.Update(m.JobId)) return false;
        Detail = DetailOf(m);
        Revision++;
        if (!_fsm.Enter(JobDoneState.Showing, Seconds)) _fsm.Restart(Seconds);
        return true;
    }

    /// <summary>One frame: the banner's clock.</summary>
    public void Tick(float dt)
    {
        if (Showing && _fsm.Tick(dt)) _fsm.Enter(JobDoneState.Idle);
    }

    /// <summary>The connection ended: no banner, and a Job of a later connection is announced again.</summary>
    public void Clear()
    {
        _fsm.Reset(JobDoneState.Idle);
        _announced.Reset();
        Detail = default;
        Revision++;
    }

    /// <summary>Seconds the banner has been up (0 when it is not).</summary>
    public float Elapsed => Showing ? _fsm.Elapsed : 0f;

    /// <summary>How far the camera is pulled back now, 0 to 1 (0 when no banner is up).</summary>
    public float Pull => Showing ? PullAt(_fsm.Elapsed) : 0f;

    /// <summary>The pull-back at <paramref name="elapsed"/> seconds into the banner: eased out, held, eased back (smoothstep both ways).</summary>
    public static float PullAt(float elapsed, float total = Seconds, float ease = EaseSeconds)
    {
        if (elapsed <= 0 || elapsed >= total) return 0f;
        var t = MathF.Min(MathF.Min(elapsed, total - elapsed) / MathF.Max(ease, 1e-3f), 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>The banner's second line for this end message: the Respect paid; else why the receiver earns none; else nothing.</summary>
    public static JobDoneDetail DetailOf(CartelJobMsg m) => new(Math.Max(0, m.RespectPaid), m.RespectPaid > 0 ? null : WhyNoRespect(m.Flags));

    /// <summary>Why the receiver earns nothing, from the Job message's flags; null when they earn.</summary>
    public static NoRespectReason? WhyNoRespect(CartelJobFlags f) =>
        f.HasFlag(CartelJobFlags.Guest) ? NoRespectReason.Guest : f.HasFlag(CartelJobFlags.NotMember) ? NoRespectReason.NotMember
        : f.HasFlag(CartelJobFlags.Earns) ? null : NoRespectReason.Sandbox;
}
