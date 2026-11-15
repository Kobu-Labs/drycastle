using System.Buffers.Binary;
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
    /// <summary>The length in seconds of each of <see cref="Files"/> (same order), recorded by scripts/make_rozhlas.py (<c>--lengths-only</c> after a hand edit). The server has no audio engine, so it schedules with these; empty or missing entries fall back to <see cref="DefaultLengthS"/>.</summary>
    public List<double> LengthsS { get; set; } = new();
    /// <summary>The length assumed for a broadcast whose length is not recorded in <see cref="LengthsS"/>.</summary>
    public double DefaultLengthS { get; set; } = 180;
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

    /// <summary>How long broadcast <paramref name="variant"/> lasts, in seconds.</summary>
    public double LengthOf(int variant) => variant >= 0 && variant < LengthsS.Count && LengthsS[variant] > 0 ? LengthsS[variant] : DefaultLengthS;

    /// <summary>The longest broadcast: the window a slot reserves (<see cref="RozhlasRules.OnAir"/>).</summary>
    public double LongestS => Files.Count == 0 ? 0 : Enumerable.Range(0, Files.Count).Max(LengthOf);

    public static RozhlasFile Parse(string json) => Validate(DataFile.Parse<RozhlasFile>(json, "rozhlas.json"));

    public static RozhlasFile Validate(RozhlasFile f)
    {
        if (f.PeriodS <= 0) throw new DataFileException("rozhlas.json: period_s must be positive");
        if (f.DefaultLengthS <= 0) throw new DataFileException("rozhlas.json: default_length_s must be positive");
        if (f.LengthsS.Any(l => l <= 0)) throw new DataFileException("rozhlas.json: lengths_s entries must be positive");
        if (f.LengthsS.Count > f.Files.Count) throw new DataFileException("rozhlas.json: lengths_s has more entries than files");
        if (f.ReferenceDistance <= 0) throw new DataFileException("rozhlas.json: reference_distance must be positive");
        if (f.RolloffDb < 0) throw new DataFileException("rozhlas.json: rolloff_db must not be negative");
        if (f.MaxDistance <= f.ReferenceDistance) throw new DataFileException("rozhlas.json: max_distance must exceed reference_distance");
        if (f.FarCutoffHz <= 0 || f.NearCutoffHz < f.FarCutoffHz) throw new DataFileException("rozhlas.json: cut-offs must satisfy 0 < far_cutoff_hz <= near_cutoff_hz");
        return f;
    }
}

/// <summary>
/// Where the village loudspeaker is. Idle: nothing detour-related runs. OnAir: a detour broadcast plays (a scheduled slot or an admin's /event rozhlas); the Obecny Urad is open,
/// the mic is live. Hijacked: the announcer was killed; the announcement is cut and the mic stays live for exactly <see cref="RozhlasBroadcast.HijackSeconds"/> from the kill.
/// </summary>
public enum RozhlasPhase : byte { Idle = 0, OnAir = 1, Hijacked = 2 }

/// <summary>What the clients are told about the loudspeaker: the phase, which broadcast is playing and how far into it (OnAir only), and the seconds this phase has left.</summary>
public readonly record struct RozhlasMsg(RozhlasPhase Phase, int Variant, float OffsetS, float LeftS);

/// <summary><see cref="EventKind.RozhlasState"/>: u8 phase, u8 variant (255 none), f32 offset into the audio, f32 seconds left in the phase.</summary>
public static class RozhlasCodec
{
    public const int Size = 10;

    public static byte[] Encode(RozhlasMsg m)
    {
        var b = new byte[Size];
        b[0] = (byte)m.Phase;
        b[1] = m.Variant is >= 0 and < 255 ? (byte)m.Variant : (byte)255;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(2), m.OffsetS);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(6), m.LeftS);
        return b;
    }

    public static RozhlasMsg? Decode(byte[] p)
    {
        if (p.Length != Size || p[0] > (byte)RozhlasPhase.Hijacked) return null;
        var off = BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(2));
        var left = BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(6));
        if (!float.IsFinite(off) || !float.IsFinite(left)) return null;
        return new RozhlasMsg((RozhlasPhase)p[0], p[1] == 255 ? -1 : p[1], off, left);
    }
}

/// <summary>
/// The server's loudspeaker: the <see cref="RozhlasPhase"/> machine and the schedule that starts it. Godot-free; the server calls <see cref="Update"/> every tick with its own wall clock
/// and sends every change to the clients (<see cref="PhaseChanged"/>). Not an EventBook event: other events run beside it.
/// <para>
/// Sources: a scheduled slot (<see cref="RozhlasRules.OnAir"/>, decided here on the server's clock) and an admin's /event rozhlas (<see cref="StartNow"/>) are detour broadcasts and move the machine.
/// Intel and event announcements over the megaphones are not: they never touch it, and one that plays during Hijacked plays over it and does not end it.
/// </para>
/// </summary>
public sealed class RozhlasBroadcast
{
    /// <summary>The hijack lasts exactly this long from the kill, replacing whatever was left of the broadcast; never refreshed.</summary>
    public const float HijackSeconds = 180f;

    /// <summary>
    /// The clock: OnAir counts down the rest of the recording, Hijacked counts down <see cref="HijackSeconds"/>. Both end in Idle.
    /// </summary>
    public static readonly StateMachineSpec<RozhlasPhase> PhaseSpec = StateMachineSpec<RozhlasPhase>.Define("Rozhlas.Phase", "The village loudspeaker: a detour broadcast, and the hijack after the announcer is killed.", RozhlasPhase.Idle)
        .Allow(RozhlasPhase.Idle, RozhlasPhase.OnAir)
        .Allow(RozhlasPhase.OnAir, RozhlasPhase.Idle, RozhlasPhase.Hijacked)
        .Allow(RozhlasPhase.Hijacked, RozhlasPhase.Idle)
        .Note(RozhlasPhase.Idle, "Nothing runs; the Urad is sealed. A scheduled slot (every PeriodS) or an admin's /event rozhlas starts a broadcast.")
        .Note(RozhlasPhase.OnAir, "A detour broadcast plays for the length of its recording (the Urad is open, the mic is live). Ends by itself, or on AnnouncerKilled.")
        .Note(RozhlasPhase.Hijacked, "The announcer was killed: the announcement is cut and the mic stays live for exactly HijackSeconds from the kill. One per broadcast, never refreshed.")
        .Build();

    readonly RozhlasFile _cfg;
    readonly StateMachine<RozhlasPhase> _fsm = new(PhaseSpec);
    long _handledSlot = long.MinValue; // the slot whose scheduled broadcast was started (or skipped), so it does not start twice
    double _startOffset;               // seconds into the audio when the current broadcast began

    public RozhlasBroadcast(RozhlasFile cfg)
    {
        _cfg = cfg;
        _fsm.Changed += (from, to) => PhaseChanged?.Invoke(from, to);
    }

    public RozhlasPhase Phase => _fsm.Current;
    public bool Hijacked => _fsm.Is(RozhlasPhase.Hijacked);
    /// <summary>A detour broadcast is running (OnAir or Hijacked): the Urad is open.</summary>
    public bool DetourOpen => !_fsm.Is(RozhlasPhase.Idle);
    /// <summary>The microphone feeds the megaphones (OnAir or Hijacked). The live feed (voice, combat sounds) runs exactly while this is true.</summary>
    public bool MicLive => DetourOpen;
    /// <summary>Which baked broadcast plays (OnAir), else -1.</summary>
    public int Variant { get; private set; } = -1;
    /// <summary>Seconds into the audio now (OnAir), else 0.</summary>
    public float OffsetS => _fsm.Is(RozhlasPhase.OnAir) ? (float)(_startOffset + _fsm.Elapsed) : 0f;
    /// <summary>Seconds left in this phase (0 when Idle).</summary>
    public float LeftS => DetourOpen ? Math.Max(0f, _fsm.Left) : 0f;

    /// <summary>The phase changed: (from, to). Fired after the machine moved, so <see cref="Phase"/>, <see cref="Variant"/> and <see cref="OffsetS"/> are already the new ones.</summary>
    public event Action<RozhlasPhase, RozhlasPhase>? PhaseChanged;

    /// <summary>What a client is told now (also the join catch-up).</summary>
    public RozhlasMsg Message => new(Phase, Variant, OffsetS, LeftS);

    /// <summary>
    /// Advances the clock by <paramref name="dt"/> and, when Idle, starts the scheduled broadcast if one is due at <paramref name="unixSeconds"/> (the server's wall clock).
    /// A server that starts mid-window joins the broadcast at its offset, as the clients used to.
    /// </summary>
    public void Update(float dt, double unixSeconds)
    {
        if (DetourOpen && _fsm.Tick(dt)) { Variant = -1; _fsm.Enter(RozhlasPhase.Idle); }
        if (!_fsm.Is(RozhlasPhase.Idle)) return;
        if (RozhlasRules.OnAir(unixSeconds, _cfg.PeriodS, _cfg.LongestS, _cfg.Files.Count) is not { } air || air.Slot == _handledSlot) return;
        _handledSlot = air.Slot;
        if (air.Offset < _cfg.LengthOf(air.Variant)) Begin(air.Variant, air.Offset); // this variant is shorter than the slot's window: nothing this slot
    }

    /// <summary>
    /// An admin's /event rozhlas: starts a detour broadcast from its beginning. Not while one is on (OnAir or Hijacked). A broadcast started inside a slot's window uses that slot up.
    /// </summary>
    public bool StartNow(double unixSeconds, int variant)
    {
        if (DetourOpen || _cfg.Files.Count == 0) return false;
        if (RozhlasRules.OnAir(unixSeconds, _cfg.PeriodS, _cfg.LongestS, _cfg.Files.Count) is { } air) _handledSlot = air.Slot;
        Begin(Math.Abs(variant % _cfg.Files.Count), 0); // any number picks a broadcast (the caller passes a random one)
        return true;
    }

    /// <summary>
    /// The announcer was killed: OnAir becomes Hijacked for exactly <see cref="HijackSeconds"/>, replacing what was left of the broadcast. Returns true when that happened;
    /// Idle (no detour) and an already hijacked mic ignore it (one hijack per broadcast, never refreshed).
    /// </summary>
    public bool AnnouncerKilled()
    {
        if (!_fsm.Is(RozhlasPhase.OnAir)) return false;
        Variant = -1;
        return _fsm.Enter(RozhlasPhase.Hijacked, HijackSeconds);
    }

    void Begin(int variant, double offset)
    {
        Variant = variant; _startOffset = offset;
        _fsm.Enter(RozhlasPhase.OnAir, (float)(_cfg.LengthOf(variant) - offset));
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
