using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where the "Job done" cinematic is (machine <c>Cartel.JobDone</c>).</summary>
public enum JobDoneState : byte { Idle, Playing, Skipped, Fading }

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
/// The "Job done" banner and the cinematic that goes with it (docs/cartel.md "Job done"), pure: the party's players are sent the Job's end (<see cref="CartelJobMsg"/>); when a Job they ran succeeds, the
/// banner shows with the Respect that player was paid and the camera leaves the body in quick flashed moves out to a high view, then cuts back (<see cref="JobDoneTimeline"/>, numbers in cartel.json
/// <c>job_done_cinematic</c>), about 10 s, while the player's own music plays under it and fades out. During it the controls are off (<see cref="InputLocked"/>: the body stands, a driver brakes); a press of jump, fire,
/// interact or escape (<see cref="Interrupt"/>), damage (<see cref="ObserveHealth"/>) or death ends the camera at once with a flash back, and the music fades out faster. With reduce motion, or on a
/// seat the camera cannot film, only the banner and the music run (no camera, no flashes, no lock). Each Job announces once, however often its end message is repeated. The client owns one; the HUD draws the banner
/// and the flash, <c>JobDoneClient</c> the camera and the music.
/// </summary>
public sealed class JobDone
{
    public static readonly StateMachineSpec<JobDoneState> Spec = StateMachineSpec<JobDoneState>
        .Define("Cartel.JobDone", "The \"Job done\" cinematic of a succeeded Cartel Job for the party's players: the banner, the flashed camera moves out and the cut back, their own music; about 10 s, or at once on a skip, damage or death.", JobDoneState.Idle)
        .Allow(JobDoneState.Idle, JobDoneState.Playing)
        .Allow(JobDoneState.Playing, JobDoneState.Playing, JobDoneState.Skipped, JobDoneState.Fading, JobDoneState.Idle)
        .Allow(JobDoneState.Skipped, JobDoneState.Idle, JobDoneState.Playing)
        .Allow(JobDoneState.Fading, JobDoneState.Idle, JobDoneState.Playing)
        .Note(JobDoneState.Idle, "Nothing plays; the camera and the controls are the player's own.")
        .Note(JobDoneState.Playing, "The banner shows the Respect (or why none); the clock runs the timeline: the camera's legs (a flash at each move), the music with its fade; the controls are off. A Job that succeeds while it plays starts it over.")
        .Note(JobDoneState.Skipped, "Ended at once by a skip key, damage or death: the camera is the player's again, a flash covers the cut, the music fades out over about 1.5 s, the controls come back after a moment.")
        .Note(JobDoneState.Fading, "The camera's part is over (the view is the player's own, the controls are theirs); the music still fades out for the last seconds of its fade.")
        .Build();

    readonly JobDoneCinematicData _data;
    readonly StateMachine<JobDoneState> _fsm = new(Spec);
    JobDoneTimeline _timeline;
    /// <summary>The last Job announced (its end message may come again; a Job is announced once).</summary>
    Change<int> _announced;
    /// <summary>The music's level when a skip ended the camera, and when the skip's flash may start (not within a third of a second of the leg's own flash).</summary>
    float _skipGain, _skipFlashDelay;
    /// <summary>The player's health as last seen (NaN: not yet).</summary>
    float _health = float.NaN;

    public JobDone() : this(new JobDoneCinematicData()) { }
    public JobDone(JobDoneCinematicData data)
    {
        _data = data;
        _timeline = JobDoneTimeline.Reduce(data);
    }

    /// <summary>The cinematic is playing (the banner, the camera if there is one, the music).</summary>
    public bool Playing => _fsm.Is(JobDoneState.Playing);
    /// <summary>The banner is up: from the start for <see cref="JobDoneTimeline.BannerSeconds"/>.</summary>
    public bool Showing => Playing && _fsm.Elapsed < _timeline.BannerSeconds;
    /// <summary>The banner's seconds this run.</summary>
    public float BannerSeconds => _timeline.BannerSeconds;
    /// <summary>What the banner's second line says now (while <see cref="Showing"/>).</summary>
    public JobDoneDetail Detail { get; private set; }
    /// <summary>Goes up with each banner: the HUD rewrites its text only then.</summary>
    public int Revision { get; private set; }
    /// <summary>How many cinematics have started (a client sees a new one by this changing).</summary>
    public int Starts { get; private set; }

    /// <summary>The camera leaves the body: playing, with a camera.</summary>
    public bool Camera => Playing && _timeline.HasCamera;
    /// <summary>
    /// The controls are off: the body's movement and weapon inputs are not sent (a driver's become the brake), while the camera plays and for <see cref="JobDoneCinematicData.SkipLockSeconds"/> after a skip.
    /// </summary>
    public bool InputLocked => Camera || (_fsm.Is(JobDoneState.Skipped) && _fsm.Elapsed < _data.SkipLockSeconds);
    /// <summary>Where the camera is now (see <see cref="CinematicFrame"/>); null when it is not out.</summary>
    public CinematicFrame? Frame => Camera ? _timeline.At(_fsm.Elapsed) : null;

    /// <summary>Seconds into the playing (0 when not playing).</summary>
    public float Elapsed => Playing ? _fsm.Elapsed : 0f;

    /// <summary>The flash's opacity now, 0 to <see cref="JobDoneCinematicData.FlashPeak"/>: one at each camera move, one covering the cut back after a skip. Never with reduce motion (no camera, no flash).</summary>
    public float Flash => _fsm.Current switch
    {
        JobDoneState.Playing => _timeline.FlashAt(_fsm.Elapsed),
        JobDoneState.Skipped => _timeline.FlashAfter(_fsm.Elapsed - _skipFlashDelay),
        _ => 0f,
    };

    /// <summary>The music's level now, 0 (silent or stopped) to 1.</summary>
    public float MusicGain => _fsm.Current switch
    {
        JobDoneState.Playing => _timeline.MusicGain(_fsm.Elapsed),
        JobDoneState.Fading => _timeline.MusicGain(_timeline.Seconds + _fsm.Elapsed),
        JobDoneState.Skipped => _skipGain * Squared(1f - Math.Clamp(_fsm.Elapsed / _data.SkipFadeSeconds, 0f, 1f)),
        _ => 0f,
    };

    /// <summary>The music is on (playing, or fading out): the client keeps its voice going.</summary>
    public bool MusicOn => !_fsm.Is(JobDoneState.Idle);

    static float Squared(float v) => v * v;

    /// <summary>
    /// The Job message: a success not announced yet starts the cinematic. True when it did. <paramref name="camera"/>: the camera and the lock are on (the player is on foot or in a road vehicle and has not
    /// asked for reduced motion); off, only the banner and the music play.
    /// </summary>
    public bool OnJob(CartelJobMsg m, bool camera = true)
    {
        if (m.JobId == 0 || m.State != JobState.Succeeded || !_announced.Update(m.JobId)) return false;
        Detail = DetailOf(m);
        Revision++;
        Starts++;
        _timeline = camera ? JobDoneTimeline.Full(_data) : JobDoneTimeline.Reduce(_data);
        if (!_fsm.Enter(JobDoneState.Playing, _timeline.Seconds)) _fsm.Restart(_timeline.Seconds);
        return true;
    }

    /// <summary>One frame: the clock.</summary>
    public void Tick(float dt)
    {
        switch (_fsm.Current)
        {
            case JobDoneState.Playing when _fsm.Tick(dt):
                var rest = _timeline.MusicEndSeconds - _timeline.Seconds;
                if (rest > 0) _fsm.Enter(JobDoneState.Fading, rest); else _fsm.Enter(JobDoneState.Idle);
                break;
            case JobDoneState.Skipped or JobDoneState.Fading when _fsm.Tick(dt):
                _fsm.Enter(JobDoneState.Idle);
                break;
        }
    }

    /// <summary>
    /// A skip: jump, fire, interact or escape was pressed, or the player died. Ends the camera at once (the view is theirs again, a flash covers the cut) and fades the music out. True when it did; nothing
    /// to end without a camera (reduce motion: the banner just runs).
    /// </summary>
    public bool Interrupt()
    {
        if (!Camera) return false;
        _skipGain = MusicGain;
        var since = _timeline.SinceFlashStart(_fsm.Elapsed);
        _skipFlashDelay = Math.Max(0f, 1f / JobDoneCinematicData.MaxFlashesPerSecond - since); // never a second flash within a third of a second of the leg's own
        _fsm.Enter(JobDoneState.Skipped, Math.Max(_data.SkipFadeSeconds, _skipFlashDelay + _data.FlashSeconds));
        return true;
    }

    /// <summary>The player's health this frame: damage taken (a drop) ends the camera like a skip. The first sight only sets the level.</summary>
    public void ObserveHealth(float health)
    {
        var hurt = !float.IsNaN(_health) && health < _health - 0.001f;
        _health = health;
        if (hurt) Interrupt();
    }

    /// <summary>The connection ended: nothing plays, and a Job of a later connection is announced again.</summary>
    public void Clear()
    {
        _fsm.Reset(JobDoneState.Idle);
        _announced.Reset();
        _health = float.NaN;
        Detail = default;
        Revision++;
    }

    /// <summary>The banner's second line for this end message: the Respect paid; else why the receiver earns none; else nothing.</summary>
    public static JobDoneDetail DetailOf(CartelJobMsg m) => new(Math.Max(0, m.RespectPaid), m.RespectPaid > 0 ? null : WhyNoRespect(m.Flags));

    /// <summary>Why the receiver earns nothing, from the Job message's flags; null when they earn.</summary>
    public static NoRespectReason? WhyNoRespect(CartelJobFlags f) =>
        f.HasFlag(CartelJobFlags.Guest) ? NoRespectReason.Guest : f.HasFlag(CartelJobFlags.NotMember) ? NoRespectReason.NotMember
        : f.HasFlag(CartelJobFlags.Earns) ? null : NoRespectReason.Sandbox;
}
