namespace Suchohrad.ShooterCore;

/// <summary>sounds.json: the event-to-sound table. Ranges come from the sound plan (#78); files are relative to the asset library.</summary>
public class SoundsFile : IVersioned
{
    public static readonly string[] ValidBuses = { "SFX", "UI", "Music" };

    public int Version { get; set; }
    public List<SoundDef> Sounds { get; set; } = new();

    Dictionary<string, SoundDef>? _byId;

    public static SoundsFile Load(string path) => Validate(DataFile.Load<SoundsFile>(path));

    public static SoundsFile Parse(string json) => Validate(DataFile.Parse<SoundsFile>(json, "sounds.json"));

    public static SoundsFile Validate(SoundsFile f)
    {
        var seen = new HashSet<string>();
        foreach (var s in f.Sounds)
        {
            if (string.IsNullOrWhiteSpace(s.Id)) throw new DataFileException("sounds.json: a sound has no id");
            if (!seen.Add(s.Id)) throw new DataFileException($"sounds.json: duplicate id {s.Id}");
            if (s.Files.Count == 0) throw new DataFileException($"sounds.json: {s.Id} has no files");
            if (!ValidBuses.Contains(s.Bus)) throw new DataFileException($"sounds.json: {s.Id} has unknown bus {s.Bus} (SFX, UI or Music)");
            if (s.MaxDistance < 0) throw new DataFileException($"sounds.json: {s.Id} max_distance is negative");
            if (s.MinGap < 0) throw new DataFileException($"sounds.json: {s.Id} min_gap is negative");
            if (s.Pitch <= 0) throw new DataFileException($"sounds.json: {s.Id} pitch must be positive");
            if (s.PitchVariance < 0 || s.PitchVariance >= 1) throw new DataFileException($"sounds.json: {s.Id} pitch_variance must be in [0, 1)");
        }
        return f;
    }

    /// <summary>The sound for an event id, or null when the table has none (callers stay silent rather than crash).</summary>
    public SoundDef? Find(string id)
    {
        _byId ??= Sounds.ToDictionary(s => s.Id);
        return _byId.GetValueOrDefault(id);
    }
}

public class SoundDef
{
    public string Id { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public string Bus { get; set; } = "SFX";
    public float VolumeDb { get; set; }
    public float Pitch { get; set; } = 1f;
    /// <summary>Random pitch spread per play: the pitch scale varies by +/- this fraction.</summary>
    public float PitchVariance { get; set; }
    /// <summary>Audible range in metres when played in 3D; 0 means the sound is never positional.</summary>
    public float MaxDistance { get; set; }
    public bool Loop { get; set; }
    /// <summary>Voice priority when the pool is full: a new sound steals the lowest-priority (then oldest) voice, and is dropped when every playing voice outranks it.</summary>
    public int Priority { get; set; }
    /// <summary>Minimum seconds between two plays of this event (0 = none); rapid repeats inside the gap are dropped so e.g. impacts do not starve weapon sounds.</summary>
    public float MinGap { get; set; }

    public bool Positional => MaxDistance > 0;
}

/// <summary>Pure rules behind the audio system (testable without Godot).</summary>
public static class SoundRules
{
    public const float WalkStride = 1.9f;
    public const float MinStepSpeed = 1.0f;
    public const float EngineIdlePitch = 0.6f, EngineTopPitch = 2.0f, EngineTopKmh = 100f;

    /// <summary>Whether a source <paramref name="distance"/> metres away is within the sound's audible range (always for non-positional sounds).</summary>
    public static bool Audible(SoundDef s, float distance) => !s.Positional || distance <= s.MaxDistance;

    /// <summary>Godot's unit size for the sound: the distance at which the attenuation has fallen to half, so a source at max range is about 21 dB down before the cut-off.</summary>
    public static float UnitSize(float maxDistance) => Math.Max(1f, maxDistance / 10f);

    /// <summary>Pitch scale for one play; <paramref name="r"/> in [0, 1] picks the spot inside the variance (0.5 is the base pitch).</summary>
    public static float PitchFor(SoundDef s, float r) => s.Pitch * (1f + s.PitchVariance * (2f * Math.Clamp(r, 0f, 1f) - 1f));

    /// <summary>Variant index in [0, count) that differs from <paramref name="last"/> when there is more than one.</summary>
    public static int PickVariant(int count, int last, float r)
    {
        if (count <= 1) return 0;
        var i = Math.Min(count - 1, (int)(Math.Clamp(r, 0f, 0.9999f) * (count - 1)));
        return i >= last && last >= 0 ? i + 1 : i;
    }

    /// <summary>Advances the stride accumulator by <paramref name="speed"/> * <paramref name="dt"/> while grounded and returns true when a footstep is due.</summary>
    public static bool FootstepDue(ref float acc, float speed, float dt, bool grounded, float stride = WalkStride)
    {
        if (!grounded || speed < MinStepSpeed) { acc = Math.Min(acc, stride * 0.5f); return false; }
        acc += speed * dt;
        if (acc < stride) return false;
        acc -= stride;
        return true;
    }

    /// <summary>Engine loop pitch from road speed in km/h: idle at standstill, rising linearly to the top pitch at <see cref="EngineTopKmh"/>.</summary>
    public static float EnginePitch(float kmh) => EngineIdlePitch + (EngineTopPitch - EngineIdlePitch) * Math.Clamp(Math.Abs(kmh) / EngineTopKmh, 0f, 1f);

    /// <summary>Index of the voice to (re)use: a free one, else the lowest priority then oldest; -1 when every playing voice outranks <paramref name="priority"/>.</summary>
    public static int PickVoice(int count, Func<int, bool> playing, Func<int, int> priorityOf, Func<int, ulong> startedOf, int priority)
    {
        var best = -1;
        for (var i = 0; i < count; i++)
        {
            if (!playing(i)) return i;
            if (best < 0 || priorityOf(i) < priorityOf(best) || (priorityOf(i) == priorityOf(best) && startedOf(i) < startedOf(best))) best = i;
        }
        return best >= 0 && priorityOf(best) > priority ? -1 : best;
    }

    /// <summary>The event id for a per-weapon sound, e.g. <c>Event("shot", "ak47")</c> is <c>shot.ak47</c>.</summary>
    public static string Event(string kind, string weaponId) => kind + "." + weaponId;
}
