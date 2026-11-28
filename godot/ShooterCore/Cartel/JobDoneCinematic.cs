using System.Numerics;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// One camera stage of the "Job done" cinematic (cartel.json <c>job_done_cinematic.stages</c>): where the camera stands round its anchor and what it looks at. Offsets are in the frame of the
/// view's heading when the cinematic began (<c>back_m</c> behind it, <c>up_m</c> above the anchor), so the picture does not swing when the player moves the mouse. While the stage holds, the camera
/// drifts: it pushes in by <c>drift_push_m</c> and turns round the anchor by <c>drift_turn_deg</c>.
/// </summary>
public sealed class CinematicStageData
{
    public const string AnchorPlayer = "player", AnchorParty = "party", ClampPullIn = "pull_in", ClampRaise = "raise";

    public string Id { get; set; } = "";
    /// <summary><c>player</c>: the own body (or the vehicle they ride); <c>party</c>: the centre of the party's living players near them.</summary>
    public string Anchor { get; set; } = AnchorPlayer;
    /// <summary>Metres behind the heading, at the start of the hold.</summary>
    public float BackM { get; set; }
    /// <summary>Metres above the anchor (its feet, or the vehicle's origin).</summary>
    public float UpM { get; set; }
    /// <summary>Metres above the anchor the camera looks at.</summary>
    public float LookUpM { get; set; }
    /// <summary>How much nearer the camera comes over the whole hold (negative: backs off), metres.</summary>
    public float DriftPushM { get; set; }
    /// <summary>How far the camera turns round the anchor over the whole hold, degrees (positive: to the left).</summary>
    public float DriftTurnDeg { get; set; }
    /// <summary>
    /// What the client does when the ground or a building is in the way. <c>pull_in</c>: the camera comes in along the line to what it looks at (a low view behind the player);
    /// <c>raise</c>: it stays on its line and goes up over the roof (a high view). Both keep it out of terrain and above any roof over it.
    /// </summary>
    public string Clamp { get; set; } = ClampRaise;

    public bool PartyAnchored => Anchor == AnchorParty;
    public bool PullsIn => Clamp == ClampPullIn;

    /// <summary>
    /// The camera's place and the point it looks at, as offsets from the anchor (Godot axes: y up, yaw 0 faces -Z, positive yaw turns left), at <paramref name="hold"/> (0 to 1, the share of the
    /// stage's hold gone) with the view heading <paramref name="yaw"/> radians.
    /// </summary>
    public (Vector3 Eye, Vector3 Look) Offsets(float yaw, float hold)
    {
        var u = Math.Clamp(hold, 0f, 1f);
        var turned = yaw + DriftTurnDeg * u * MathF.PI / 180f;
        var back = BackM - DriftPushM * u;
        return (new Vector3(MathF.Sin(turned) * back, UpM, MathF.Cos(turned) * back), new Vector3(0, LookUpM, 0));
    }

    internal void Validate(string what)
    {
        static DataFileException Bad(string text) => new($"cartel.json: job_done_cinematic.{text}");
        if (string.IsNullOrWhiteSpace(Id) || Id == JobDoneTimeline.Eye) throw Bad($"{what}: needs an id (not \"{JobDoneTimeline.Eye}\")");
        if (Anchor is not (AnchorPlayer or AnchorParty)) throw Bad($"{what}: anchor is player or party");
        if (Clamp is not (ClampPullIn or ClampRaise)) throw Bad($"{what}: clamp is pull_in or raise");
        if (!float.IsFinite(BackM) || !float.IsFinite(UpM) || !float.IsFinite(LookUpM) || !float.IsFinite(DriftPushM) || !float.IsFinite(DriftTurnDeg)) throw Bad($"{what}: numbers must be finite");
        if (UpM < 0.5f) throw Bad($"{what}: up_m must be at least 0.5 (the camera is above what it looks at)");
        if (BackM < 0 || DriftPushM > BackM) throw Bad($"{what}: back_m is 0 or more and the push cannot take the camera past the anchor");
    }
}

/// <summary>One move of the cinematic and the hold after it (cartel.json <c>job_done_cinematic.legs</c>).</summary>
public sealed class CinematicLegData
{
    /// <summary>The stage the camera moves to (its id), or <c>eye</c>: back into the player's own view.</summary>
    public string To { get; set; } = JobDoneTimeline.Eye;
    /// <summary>How long the quick move takes, seconds. A flash and a whoosh go with it.</summary>
    public float MoveSeconds { get; set; } = 0.35f;
    /// <summary>How long the camera stays after arriving, drifting, seconds.</summary>
    public float HoldSeconds { get; set; }
}

/// <summary>What the cinematic is for a player who has reduce motion on, or rides something it cannot film (cartel.json <c>job_done_cinematic.reduced</c>): the banner and the music only, no camera, no flashes, no lock.</summary>
public sealed class JobDoneReducedData
{
    /// <summary>How long the banner and the music run, seconds.</summary>
    public float Seconds { get; set; } = 4f;
    /// <summary>The music starts to fade this many seconds in, and is out <c>music_fade_seconds</c> later.</summary>
    public float MusicFadeStartSeconds { get; set; } = 2.5f;
    public float MusicFadeSeconds { get; set; } = 1.5f;
}

/// <summary>
/// cartel.json <c>job_done_cinematic</c> (docs/cartel.md "Job done"): the numbers of the cinematic that plays on a Job's success: the camera's stages and legs, the flash, the music's fade, the skip. First
/// guesses, nothing here was played. The defaults are the shipped values.
/// </summary>
public sealed class JobDoneCinematicData
{
    /// <summary>How long the "Job done" banner shows from the start, seconds.</summary>
    public float BannerSeconds { get; set; } = 4f;
    /// <summary>The flash's opacity at its peak (0 to 1; kept low and soft), how long it takes to rise, and how long it lasts in all, seconds. At most 3 start in any second (<c>legs</c> are checked against it).</summary>
    public float FlashPeak { get; set; } = 0.5f;
    public float FlashRiseSeconds { get; set; } = 0.04f;
    public float FlashSeconds { get; set; } = 0.16f;
    /// <summary>The music comes up over this long at the start, seconds.</summary>
    public float MusicInSeconds { get; set; } = 0.3f;
    /// <summary>The music starts to fade this many seconds after the start, and is out <c>music_fade_seconds</c> later (it may outlast the camera's legs).</summary>
    public float MusicFadeStartSeconds { get; set; } = 9f;
    public float MusicFadeSeconds { get; set; } = 3f;
    /// <summary>A skip (a press of jump, fire, interact or escape) or damage ends the camera at once: the music then fades out over this long, and the controls stay off the first <c>skip_lock_seconds</c>.</summary>
    public float SkipFadeSeconds { get; set; } = 1.5f;
    public float SkipLockSeconds { get; set; } = 0.3f;
    /// <summary>Squad mates within this many metres of the player count for the party's centre.</summary>
    public float PartyRadiusM { get; set; } = 30f;
    /// <summary>How far above a roof or the ground the camera is kept, metres.</summary>
    public float ClearanceM { get; set; } = 2f;
    public List<CinematicStageData> Stages { get; set; } = new()
    {
        new() { Id = "behind", Anchor = CinematicStageData.AnchorPlayer, BackM = 6f, UpM = 3f, LookUpM = 1.2f, DriftPushM = 0.8f, DriftTurnDeg = 5f, Clamp = CinematicStageData.ClampPullIn },
        new() { Id = "above", Anchor = CinematicStageData.AnchorParty, BackM = 6f, UpM = 25f, LookUpM = 0f, DriftPushM = 2f, DriftTurnDeg = 6f },
        new() { Id = "high", Anchor = CinematicStageData.AnchorParty, BackM = 12f, UpM = 80f, LookUpM = 0f, DriftPushM = 5f, DriftTurnDeg = 8f },
    };
    public List<CinematicLegData> Legs { get; set; } = new()
    {
        new() { To = "behind", MoveSeconds = 0.35f, HoldSeconds = 1.5f },
        new() { To = "above", MoveSeconds = 0.35f, HoldSeconds = 2f },
        new() { To = "high", MoveSeconds = 0.35f, HoldSeconds = 2.5f },
        new() { To = "above", MoveSeconds = 0.35f, HoldSeconds = 0.6f },
        new() { To = "behind", MoveSeconds = 0.35f, HoldSeconds = 1.3f },
        new() { To = JobDoneTimeline.Eye, MoveSeconds = 0.35f, HoldSeconds = 0f },
    };
    public JobDoneReducedData Reduced { get; set; } = new();

    /// <summary>The flashes of one second never number more than this (photosensitivity).</summary>
    public const int MaxFlashesPerSecond = 3;

    internal JobDoneCinematicData Validate()
    {
        static DataFileException Bad(string text) => new($"cartel.json: job_done_cinematic.{text}");
        static bool Pos(float v) => float.IsFinite(v) && v > 0;
        if (!Pos(BannerSeconds)) throw Bad("banner_seconds must be more than 0");
        if (!(float.IsFinite(FlashPeak) && FlashPeak is > 0 and <= 0.6f)) throw Bad("flash_peak must be more than 0 and at most 0.6 (a soft flash)");
        if (!Pos(FlashSeconds) || !Pos(FlashRiseSeconds) || FlashRiseSeconds >= FlashSeconds || FlashSeconds > 0.25f) throw Bad("flash_rise_seconds must be more than 0 and under flash_seconds, which is at most 0.25");
        if (!float.IsFinite(MusicInSeconds) || MusicInSeconds < 0) throw Bad("music_in_seconds must be 0 or more");
        if (!float.IsFinite(MusicFadeStartSeconds) || MusicFadeStartSeconds < 0 || !Pos(MusicFadeSeconds)) throw Bad("music_fade_start_seconds is 0 or more and music_fade_seconds more than 0");
        if (!Pos(SkipFadeSeconds) || !float.IsFinite(SkipLockSeconds) || SkipLockSeconds < 0) throw Bad("skip_fade_seconds must be more than 0 and skip_lock_seconds 0 or more");
        if (!Pos(PartyRadiusM) || !float.IsFinite(ClearanceM) || ClearanceM < 0) throw Bad("party_radius_m must be more than 0 and clearance_m 0 or more");
        if (Stages.Count == 0 || Legs.Count == 0) throw Bad("stages and legs must each list at least one");
        var ids = new HashSet<string>();
        for (var i = 0; i < Stages.Count; i++)
        {
            Stages[i].Validate($"stages[{i}]");
            if (!ids.Add(Stages[i].Id)) throw Bad($"stages[{i}]: the id '{Stages[i].Id}' is used twice");
        }
        var minSpacing = 1f / MaxFlashesPerSecond;
        for (var i = 0; i < Legs.Count; i++)
        {
            var l = Legs[i];
            if (l.To != JobDoneTimeline.Eye && !ids.Contains(l.To)) throw Bad($"legs[{i}]: unknown stage '{l.To}'");
            if (!Pos(l.MoveSeconds) || !float.IsFinite(l.HoldSeconds) || l.HoldSeconds < 0) throw Bad($"legs[{i}]: move_seconds must be more than 0 and hold_seconds 0 or more");
            if (l.MoveSeconds + l.HoldSeconds < minSpacing - 1e-4f && i < Legs.Count - 1) throw Bad($"legs[{i}]: move and hold together must be at least {minSpacing:0.###} s (at most {MaxFlashesPerSecond} flashes a second)");
        }
        if (Legs[^1].To != JobDoneTimeline.Eye) throw Bad("the last leg must go back to the eye (the player's own view)");
        if (!Pos(Reduced.Seconds) || !float.IsFinite(Reduced.MusicFadeStartSeconds) || Reduced.MusicFadeStartSeconds < 0 || !Pos(Reduced.MusicFadeSeconds)) throw Bad("reduced: seconds and music_fade_seconds must be more than 0, music_fade_start_seconds 0 or more");
        return this;
    }
}

/// <summary>
/// Where the cinematic camera is at one moment (see <see cref="JobDoneTimeline.At"/>): moving from stage <see cref="From"/> to stage <see cref="To"/> (indexes of <see cref="JobDoneCinematicData.Stages"/>, -1 the
/// player's own eye), <see cref="Move"/> of the way (eased, 1 when arrived), and <see cref="Hold"/> of the way through the hold after it (0 while moving). <see cref="Leg"/> says which leg it is (a new one is a new whoosh).
/// </summary>
public readonly record struct CinematicFrame(int Leg, int From, int To, float Move, float Hold, bool Moving);

/// <summary>
/// The "Job done" cinematic as a function of time (docs/cartel.md "Job done"), pure: the legs laid end to end (a quick eased move with a flash, then a drifting hold), the music's level, and the flash. Built from
/// <see cref="JobDoneCinematicData"/>; <see cref="Reduce"/> is the same banner and music with no camera. The camera is the client's (<c>JobDoneClient</c>): it asks the frame and places the stages.
/// </summary>
public sealed class JobDoneTimeline
{
    /// <summary>The leg target (and stage name) for the player's own view.</summary>
    public const string Eye = "eye";

    readonly JobDoneCinematicData _d;
    readonly (float Start, float MoveEnd, float End, int From, int To)[] _legs;

    JobDoneTimeline(JobDoneCinematicData d, bool camera)
    {
        _d = d;
        HasCamera = camera;
        if (!camera) { _legs = Array.Empty<(float, float, float, int, int)>(); Seconds = d.Reduced.Seconds; MusicFadeStartSeconds = d.Reduced.MusicFadeStartSeconds; MusicFadeSeconds = d.Reduced.MusicFadeSeconds; return; }
        MusicFadeStartSeconds = d.MusicFadeStartSeconds; MusicFadeSeconds = d.MusicFadeSeconds;
        _legs = new (float, float, float, int, int)[d.Legs.Count];
        var t = 0f;
        var from = -1;
        for (var i = 0; i < _legs.Length; i++)
        {
            var l = d.Legs[i];
            var to = l.To == Eye ? -1 : d.Stages.FindIndex(s => s.Id == l.To);
            _legs[i] = (t, t + l.MoveSeconds, t + l.MoveSeconds + l.HoldSeconds, from, to);
            t += l.MoveSeconds + l.HoldSeconds;
            from = to;
        }
        Seconds = t;
    }

    /// <summary>The cinematic with its camera, flashes and lock.</summary>
    public static JobDoneTimeline Full(JobDoneCinematicData d) => new(d, true);
    /// <summary>The banner and the music alone (reduce motion, or a seat the camera cannot film).</summary>
    public static JobDoneTimeline Reduce(JobDoneCinematicData d) => new(d, false);

    public bool HasCamera { get; }
    /// <summary>How long the camera, the banner's run and the lock last (the banner itself: <see cref="BannerSeconds"/>).</summary>
    public float Seconds { get; }
    public float BannerSeconds => Math.Min(_d.BannerSeconds, Seconds);
    public float MusicFadeStartSeconds { get; }
    public float MusicFadeSeconds { get; }
    /// <summary>When the music is out (it can outlast <see cref="Seconds"/>).</summary>
    public float MusicEndSeconds => MusicFadeStartSeconds + MusicFadeSeconds;
    public int LegCount => _legs.Length;

    /// <summary>The quick move's easing: fast at first, settling (ease out, quartic).</summary>
    public static float Ease(float u)
    {
        var r = 1f - Math.Clamp(u, 0f, 1f);
        return 1f - r * r * r * r;
    }

    /// <summary>The camera at <paramref name="t"/> seconds in (past the end: arrived at the last stage). Meaningless without a camera.</summary>
    public CinematicFrame At(float t)
    {
        if (_legs.Length == 0) return new CinematicFrame(0, -1, -1, 1f, 0f, false);
        var i = 0;
        while (i < _legs.Length - 1 && t >= _legs[i].End) i++;
        var l = _legs[i];
        if (t < l.MoveEnd) return new CinematicFrame(i, l.From, l.To, Ease((t - l.Start) / (l.MoveEnd - l.Start)), 0f, true);
        var hold = l.End - l.MoveEnd;
        return new CinematicFrame(i, l.From, l.To, 1f, hold > 0 ? Math.Clamp((t - l.MoveEnd) / hold, 0f, 1f) : 1f, false);
    }

    /// <summary>The music's level at <paramref name="t"/> seconds from the start, 0 to 1: up over <see cref="JobDoneCinematicData.MusicInSeconds"/>, held, down over the fade (squared: it falls evenly to the ear).</summary>
    public float MusicGain(float t)
    {
        var rise = _d.MusicInSeconds > 0 ? Math.Clamp(t / _d.MusicInSeconds, 0f, 1f) : 1f;
        var fall = Math.Clamp((t - MusicFadeStartSeconds) / MusicFadeSeconds, 0f, 1f);
        return rise * (1f - fall) * (1f - fall);
    }

    /// <summary>One flash's opacity <paramref name="x"/> seconds after it started: up over the rise, then a smooth fall to nothing at <see cref="JobDoneCinematicData.FlashSeconds"/>; 0 before and after.</summary>
    public float FlashAfter(float x)
    {
        if (x < 0 || x >= _d.FlashSeconds) return 0f;
        if (x < _d.FlashRiseSeconds) return _d.FlashPeak * x / _d.FlashRiseSeconds;
        var u = (x - _d.FlashRiseSeconds) / (_d.FlashSeconds - _d.FlashRiseSeconds);
        return _d.FlashPeak * (1f - u * u * (3f - 2f * u));
    }

    /// <summary>The flash at <paramref name="t"/>: each leg's move starts one (its whoosh goes with it).</summary>
    public float FlashAt(float t) => HasCamera && SinceFlashStart(t) is var x && x < _d.FlashSeconds ? FlashAfter(x) : 0f;

    /// <summary>Seconds since the newest leg started at or before <paramref name="t"/> (a flash starts with it); infinity before the first or with no camera.</summary>
    public float SinceFlashStart(float t)
    {
        for (var i = _legs.Length - 1; i >= 0; i--) if (t >= _legs[i].Start) return t - _legs[i].Start;
        return float.PositiveInfinity;
    }

    /// <summary>The seconds the flashes start at (for the photosensitivity check).</summary>
    public IEnumerable<float> FlashStarts => _legs.Select(l => l.Start);
}

/// <summary>Where the party is: the centre of the players the cinematic should show.</summary>
public static class CinematicRules
{
    /// <summary>
    /// The mean of the player's position and those of their living squad mates within <paramref name="radiusM"/> of them (flat distance), or the player's own position when there are none. Mates that
    /// are far, dead or in no squad are left out, so the high view is on the group that stands together.
    /// </summary>
    public static Vector3 PartyCentre(Vector3 self, IEnumerable<Vector3> mates, float radiusM)
    {
        var sum = self;
        var n = 1;
        foreach (var m in mates)
        {
            if (new Vector2(m.X - self.X, m.Z - self.Z).Length() > radiusM) continue;
            sum += m; n++;
        }
        return sum / n;
    }
}
