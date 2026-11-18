using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Drycastle.ShooterCore;

/// <summary>
/// Scoped timers of the client's own work in a frame (the "client: perf scopes" log line and the client dashboard): how long the named parts
/// took, summed over the report period. A scope is a timestamp at each end and an array add. The scopes do not overlap (so they stack) and do not cover the
/// engine's share of a frame (rendering, physics, audio): that is the frame time less their sum.
/// </summary>
public static class ClientProf
{
    public enum S
    {
        /// <summary>Receiving: packets read and applied (snapshots decoded, events handled).</summary>
        Net,
        /// <summary>Remote players, corpses, ground items, care packages, turrets.</summary>
        Players,
        /// <summary>Animals, flocks, citizens, the Gang, the Bigfoot, the demon.</summary>
        Creatures,
        /// <summary>Cars, the harvester, aircraft.</summary>
        Vehicles,
        /// <summary>Rockets, missiles, the Scud, the lock-on.</summary>
        Projectiles,
        /// <summary>The player's own view: water, viewmodel, shake, dust, death.</summary>
        View,
        Hud,
        /// <summary>The physics step: input, prediction of the own body and vehicle, sending the input.</summary>
        Predict,
        /// <summary>Replaying inputs after the server corrected the player (inside the physics step, not in <see cref="Predict"/>).</summary>
        Reconcile,
        /// <summary>Player snapshots decoded and applied (taken out of <see cref="Net"/>, as the next two are: Net is what is left, the socket and the rest).</summary>
        NetSnapshot,
        /// <summary>World snapshots (animals, vehicles, corpses, items) decoded and applied.</summary>
        NetWorld,
        /// <summary>Reliable messages and effects decoded and handled: shots, hits, kills, spawns ("client: perf events" names the kinds).</summary>
        NetEvents,
        Count,
    }

    static readonly string[] Names = { "net", "players", "creatures", "vehicles", "projectiles", "view", "hud", "predict", "reconcile", "net_snapshot", "net_world", "net_events" };
    const int N = (int)S.Count;
    static readonly long[] Win = new long[N];

    public static int ScopeCount => N;
    public static string Name(int scope) => scope >= 0 && scope < N ? Names[scope] : $"scope{scope}";

    public static long Begin() => Stopwatch.GetTimestamp();

    /// <summary>End of a scope that began at <paramref name="t0"/>.</summary>
    public static void End(S s, long t0) => Win[(int)s] += Stopwatch.GetTimestamp() - t0;

    /// <summary>Ends a scope and starts the next: returns the timestamp the next <see cref="Lap"/> or <see cref="End"/> takes.</summary>
    public static long Lap(S s, long t0)
    {
        var now = Stopwatch.GetTimestamp();
        Win[(int)s] += now - t0;
        return now;
    }

    /// <summary>The time since <paramref name="t0"/> belongs to <paramref name="to"/> and not to <paramref name="from"/>, the scope it runs inside. Returns its length in stopwatch ticks.</summary>
    public static long Move(S from, S to, long t0)
    {
        var d = Stopwatch.GetTimestamp() - t0;
        Win[(int)to] += d; Win[(int)from] -= d;
        return d;
    }

    /// <summary>The period's scopes in milliseconds per second of play (10 ms/s is 1 % of a core) into <paramref name="msPerSecond"/>; the next period starts.</summary>
    public static void Take(double seconds, Span<float> msPerSecond)
    {
        seconds = Math.Max(seconds, 1e-3);
        for (var i = 0; i < N && i < msPerSecond.Length; i++) msPerSecond[i] = (float)(Win[i] * 1000.0 / Stopwatch.Frequency / seconds);
        Array.Clear(Win);
    }
}

/// <summary>The frame times of a report period, in buckets of a quarter millisecond up to 250 ms: percentiles without keeping every frame, no allocation.</summary>
public sealed class FrameTimes
{
    const double BucketMs = 0.25;
    readonly int[] _buckets = new int[1001];

    public int Count { get; private set; }
    public double MaxMs { get; private set; }
    public double SumMs { get; private set; }
    public int Over33, Over50, Over100;

    public void Add(double ms)
    {
        if (!(ms >= 0)) return;
        _buckets[Math.Min(_buckets.Length - 1, (int)(ms / BucketMs))]++;
        Count++; SumMs += ms;
        if (ms > MaxMs) MaxMs = ms;
        if (ms > 33.4) Over33++;
        if (ms > 50) Over50++;
        if (ms > 100) Over100++;
    }

    /// <summary>The frame time <paramref name="p"/> percent of the frames stayed under (the upper edge of its bucket; the slowest frame for the last bucket).</summary>
    public double Percentile(double p)
    {
        if (Count == 0) return 0;
        var want = Math.Max(1, (int)Math.Ceiling(p / 100 * Count));
        var seen = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            seen += _buckets[i];
            if (seen >= want) return i == _buckets.Length - 1 ? MaxMs : Math.Min(MaxMs, (i + 1) * BucketMs);
        }
        return MaxMs;
    }

    public void Reset()
    {
        Array.Clear(_buckets);
        Count = 0; MaxMs = 0; SumMs = 0; Over33 = Over50 = Over100 = 0;
    }
}

/// <summary>
/// A client's performance numbers of one report period, sent to the server over the game connection (<see cref="EventKind.ClientReport"/>, every
/// <see cref="PeriodSeconds"/>), which hands them to the metrics store labelled with the player (docs/monitoring.md). The payload: u8 version,
/// u8 count + that many f32 (<see cref="Fields"/>, in order), u8 count + that many f32 (the <see cref="ClientProf"/> scopes, ms per second),
/// u8 length + UTF-8 (the graphics adapter), u8 length + UTF-8 (the operating system). New fields and scopes are added at the end only: an
/// older server names the ones it knows and drops the rest, a newer server gets fewer from an older client.
/// </summary>
public static class ClientReport
{
    public const byte Version = 1;
    public const double PeriodSeconds = 10;
    const int MaxValues = 96, MaxText = 64;

    /// <summary>The values, in wire order. Counts are per period unless the name says per second.</summary>
    public static readonly string[] Fields =
    {
        "fps", "frame_p50_ms", "frame_p99_ms", "frame_max_ms", "frames_over_33ms", "frames_over_50ms", "frames_over_100ms",
        "process_peak_ms", "physics_peak_ms", "render_cpu_ms", "render_gpu_ms", "draws", "prims_k", "objects", "nodes",
        "video_mem_mb", "rss_mb", "gc_heap_mb", "alloc_mb_s", "gc_gen0", "gc_gen1", "gc_gen2", "gc_pause_ms",
        "rtt_ms", "rtt_var_ms", "loss_pct", "snaps_per_s", "snap_gap_max_ms", "tick_skips", "reconciles", "pred_err_max_m",
        "in_kb_s", "out_kb_s", "remotes", "world_entities", "res_w", "res_h", "driving",
    };

    public static int Index(string field) => Array.IndexOf(Fields, field);

    public static byte[] Encode(ReadOnlySpan<float> values, ReadOnlySpan<float> scopes, string adapter, string os)
    {
        var nv = Math.Min(values.Length, MaxValues);
        var ns = Math.Min(scopes.Length, MaxValues);
        var a = Text(adapter);
        var o = Text(os);
        var d = new byte[1 + 1 + nv * 4 + 1 + ns * 4 + 1 + a.Length + 1 + o.Length];
        var at = 0;
        d[at++] = Version;
        d[at++] = (byte)nv;
        for (var i = 0; i < nv; i++, at += 4) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at), values[i]);
        d[at++] = (byte)ns;
        for (var i = 0; i < ns; i++, at += 4) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at), scopes[i]);
        d[at++] = (byte)a.Length; a.CopyTo(d, at); at += a.Length;
        d[at++] = (byte)o.Length; o.CopyTo(d, at);
        return d;
    }

    /// <summary>False for anything that is not a whole report of this version; numbers that are not finite come out as 0.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> d, out float[] values, out float[] scopes, out string adapter, out string os)
    {
        values = scopes = Array.Empty<float>(); adapter = os = "";
        var at = 0;
        if (d.Length < 2 || d[at++] != Version) return false;
        if (!Floats(d, ref at, out values) || !Floats(d, ref at, out scopes)) return false;
        return Str(d, ref at, out adapter) && Str(d, ref at, out os) && at == d.Length;
    }

    static bool Floats(ReadOnlySpan<byte> d, ref int at, out float[] xs)
    {
        xs = Array.Empty<float>();
        if (at >= d.Length) return false;
        var n = d[at++];
        if (n > MaxValues || at + n * 4 > d.Length) return false;
        xs = new float[n];
        for (var i = 0; i < n; i++, at += 4)
        {
            var v = BinaryPrimitives.ReadSingleLittleEndian(d[at..]);
            xs[i] = float.IsFinite(v) ? v : 0;
        }
        return true;
    }

    static bool Str(ReadOnlySpan<byte> d, ref int at, out string s)
    {
        s = "";
        if (at >= d.Length) return false;
        var n = d[at++];
        if (n > MaxText || at + n > d.Length) return false;
        s = Encoding.UTF8.GetString(d.Slice(at, n));
        at += n;
        return true;
    }

    static byte[] Text(string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        if (b.Length <= MaxText) return b;
        var n = MaxText;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--; // not through the middle of a character
        return b[..n];
    }

    /// <summary>
    /// A short name of a graphics adapter for a label: "NVIDIA GeForce RTX 3060 Laptop GPU" is "RTX_3060_Laptop", "AMD Radeon RX 6600 XT (RADV NAVI23)" is
    /// "Radeon_RX_6600_XT", "Intel(R) UHD Graphics (TGL GT1)" is "UHD". Letters, digits and underscores, at most 32.
    /// </summary>
    public static string AdapterLabel(string adapter)
    {
        var s = (adapter ?? "").Replace("(R)", "").Replace("(TM)", "");
        var paren = s.IndexOf('(');
        if (paren > 0) s = s[..paren];
        var sb = new StringBuilder();
        foreach (var word in s.Split(new[] { ' ', '/', '-' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word is "NVIDIA" or "GeForce" or "AMD" or "Intel" or "GPU" or "Graphics" or "Corporation") continue;
            if (sb.Length > 0) sb.Append('_');
            foreach (var ch in word) if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
        }
        if (sb.Length == 0) return "unknown";
        return sb.Length > 32 ? sb.ToString(0, 32) : sb.ToString();
    }

    /// <summary>The report as <c>name=value</c> text, the body of the "client: perf" log line.</summary>
    public static string Line(ReadOnlySpan<float> values)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < values.Length && i < Fields.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Fields[i]).Append('=').Append(values[i].ToString("0.###", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>The scopes as <c>name=ms_per_second</c> text, the body of the "client: perf scopes" log line.</summary>
    public static string ScopeLine(ReadOnlySpan<float> scopes)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < scopes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(ClientProf.Name(i)).Append('=').Append(scopes[i].ToString("0.##", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
