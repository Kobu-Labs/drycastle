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
    /// <summary>Beyond this many metres a speaker is silent.</summary>
    public float MaxDistance { get; set; } = 350f;
    /// <summary>How many speakers sound at once (the nearest to the listener).</summary>
    public int Voices { get; set; } = 3;
    /// <summary>Low-pass cut-off at the reference distance (almost no effect) and at the maximum range (a dull mumble); in between it falls by the same factor per doubling of distance.</summary>
    public float NearCutoffHz { get; set; } = 8000f;
    public float FarCutoffHz { get; set; } = 500f;

    public static RozhlasFile Parse(string json) => Validate(DataFile.Parse<RozhlasFile>(json, "rozhlas.json"));

    public static RozhlasFile Validate(RozhlasFile f)
    {
        if (f.PeriodS <= 0) throw new DataFileException("rozhlas.json: period_s must be positive");
        if (f.ReferenceDistance <= 0) throw new DataFileException("rozhlas.json: reference_distance must be positive");
        if (f.MaxDistance <= f.ReferenceDistance) throw new DataFileException("rozhlas.json: max_distance must exceed reference_distance");
        if (f.Voices < 1) throw new DataFileException("rozhlas.json: voices must be at least 1");
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

    /// <summary>Level of a speaker <paramref name="distance"/> metres from the listener: 0 dB inside the reference distance, 6 dB less per doubling beyond it, fading out to silence at the maximum.</summary>
    public static float GainDb(float distance, float reference, float max)
    {
        if (distance >= max) return SilentDb;
        if (distance <= reference) return 0f;
        var db = -20f * MathF.Log10(distance / reference);
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

    /// <summary>Indices of the (at most) <paramref name="count"/> speakers within <paramref name="max"/> metres, nearest first.</summary>
    public static int[] Nearest(IReadOnlyList<Vector3> speakers, Vector3 listener, int count, float max)
    {
        var max2 = max * max;
        var best = new List<(float D2, int I)>(count + 1);
        for (var i = 0; i < speakers.Count; i++)
        {
            var d2 = Vector3.DistanceSquared(speakers[i], listener);
            if (d2 >= max2) continue;
            if (best.Count == count && d2 >= best[^1].D2) continue;
            var at = best.FindIndex(b => d2 < b.D2);
            best.Insert(at < 0 ? best.Count : at, (d2, i));
            if (best.Count > count) best.RemoveAt(best.Count - 1);
        }
        return best.Select(b => b.I).ToArray();
    }

    /// <summary>
    /// Which speaker each voice carries next: a voice keeps its speaker while that is still wanted (so it does not restart
    /// when the listener moves), the others take the wanted speakers that have no voice yet; -1 is a free voice.
    /// </summary>
    public static int[] Assign(int[] current, int[] wanted)
    {
        var next = new int[current.Length];
        for (var v = 0; v < current.Length; v++) next[v] = Array.IndexOf(wanted, current[v]) >= 0 ? current[v] : -1;
        foreach (var w in wanted)
        {
            if (Array.IndexOf(next, w) >= 0) continue;
            var free = Array.IndexOf(next, -1);
            if (free < 0) break;
            next[free] = w;
        }
        return next;
    }

    static ulong Mix(ulong x)
    {
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    static double Hash01(long slot) => (Mix((ulong)slot) >> 11) / (double)(1UL << 53);
}
