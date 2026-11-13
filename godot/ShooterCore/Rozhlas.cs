using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// rozhlas.json: the village loudspeaker broadcast (a song, a muffled announcement, a clear OPAKUJEM, the announcement again),
/// baked into one file by scripts/make_rozhlas.py. Files are relative to the asset library; the game stays silent while they are missing.
/// </summary>
public class RozhlasFile : IVersioned
{
    public int Version { get; set; }
    /// <summary>The baked broadcasts; one is picked per slot.</summary>
    public List<string> Files { get; set; } = new();
    /// <summary>One broadcast every this many seconds, at a random moment inside each slot.</summary>
    public double PeriodS { get; set; } = 600;
    /// <summary>Base level of every speaker, before distance.</summary>
    public float VolumeDb { get; set; }
    /// <summary>Within this many metres of a speaker it is at full level.</summary>
    public float ReferenceDistance { get; set; } = 6f;
    /// <summary>How many dB quieter it gets per doubling of the distance beyond the reference distance: 6 is the open-air law, less keeps it audible further away.</summary>
    public float RolloffDb { get; set; } = 6f;
    /// <summary>Beyond this many metres a speaker is silent.</summary>
    public float MaxDistance { get; set; } = 350f;
    /// <summary>Low-pass cut-off at the reference distance (almost no effect) and at the maximum range (a dull mumble); in between it falls by the same factor per doubling of distance.</summary>
    public float NearCutoffHz { get; set; } = 8000f;
    public float FarCutoffHz { get; set; } = 500f;

    public static RozhlasFile Parse(string json) => Validate(DataFile.Parse<RozhlasFile>(json, "rozhlas.json"));

    public static RozhlasFile Validate(RozhlasFile f)
    {
        if (f.PeriodS <= 0) throw new DataFileException("rozhlas.json: period_s must be positive");
        if (f.ReferenceDistance <= 0) throw new DataFileException("rozhlas.json: reference_distance must be positive");
        if (f.RolloffDb < 0) throw new DataFileException("rozhlas.json: rolloff_db must not be negative");
        if (f.MaxDistance <= f.ReferenceDistance) throw new DataFileException("rozhlas.json: max_distance must exceed reference_distance");
        if (f.FarCutoffHz <= 0 || f.NearCutoffHz < f.FarCutoffHz) throw new DataFileException("rozhlas.json: cut-offs must satisfy 0 < far_cutoff_hz <= near_cutoff_hz");
        return f;
    }
}

/// <summary>The broadcast that is on the air at a moment: which file, and how far into it.</summary>
public readonly record struct RozhlasOnAir(long Slot, int Variant, double Offset);

/// <summary>Pure rules behind the village loudspeaker (testable without Godot).</summary>
public static class RozhlasRules
{
    /// <summary>Gain below this is treated as silence.</summary>
    public const float SilentDb = -80f;
    /// <summary>The level drops this much further, linearly, over the last quarter of the range, so the speaker fades out rather than being cut.</summary>
    const float FadeDb = 30f;

    /// <summary>
    /// The broadcast on the air at <paramref name="unixSeconds"/>, or null. Time is cut into slots of <paramref name="period"/>
    /// seconds; each slot holds one broadcast of <paramref name="length"/> seconds, starting at a moment fixed by the slot number.
    /// It depends on the clock alone, so every client (and the server) agrees without sending anything.
    /// </summary>
    public static RozhlasOnAir? OnAir(double unixSeconds, double period, double length, int variants)
    {
        if (period <= 0 || length <= 0 || variants < 1) return null;
        var slot = (long)Math.Floor(unixSeconds / period);
        var room = Math.Max(0.0, period - length);
        var start = slot * period + Hash01(slot) * room;
        var offset = unixSeconds - start;
        if (offset < 0 || offset >= length) return null;
        return new RozhlasOnAir(slot, (int)(Mix((ulong)slot ^ 0x9E3779B97F4A7C15UL) % (ulong)variants), offset);
    }

    /// <summary>Level of a speaker <paramref name="distance"/> metres from the listener: 0 dB inside the reference distance, <paramref name="rolloffDb"/> dB less per doubling beyond it (6 by default), fading out to silence at the maximum.</summary>
    public static float GainDb(float distance, float reference, float max, float rolloffDb = 6f)
    {
        if (distance >= max) return SilentDb;
        if (distance <= reference) return 0f;
        var db = -rolloffDb * MathF.Log2(distance / reference);
        var fadeFrom = max * 0.75f;
        if (distance > fadeFrom) db -= FadeDb * (distance - fadeFrom) / (max - fadeFrom);
        return Math.Max(SilentDb, db);
    }

    /// <summary>
    /// Low-pass cut-off of a speaker <paramref name="distance"/> metres away: <paramref name="near"/> inside the reference distance,
    /// <paramref name="far"/> at the maximum, falling by the same factor per doubling in between, so the far speakers get duller.
    /// </summary>
    public static float CutoffHz(float distance, float reference, float max, float near, float far)
    {
        if (distance <= reference) return near;
        if (distance >= max) return far;
        var t = MathF.Log(distance / reference) / MathF.Log(max / reference);
        return near * MathF.Pow(far / near, t);
    }

    /// <summary>Distance in metres from the listener to the nearest speaker (infinity with no speakers). The broadcast is one shared recording; only this distance sets how loud and how dull it is.</summary>
    public static float NearestDistance(IReadOnlyList<Vector3> speakers, Vector3 listener)
    {
        var best = float.PositiveInfinity;
        for (var i = 0; i < speakers.Count; i++) best = MathF.Min(best, Vector3.DistanceSquared(speakers[i], listener));
        return MathF.Sqrt(best);
    }

    static ulong Mix(ulong x)
    {
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    static double Hash01(long slot) => (Mix((ulong)slot) >> 11) / (double)(1UL << 53);
}
