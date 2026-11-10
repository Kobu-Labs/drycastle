using System.Globalization;
using System.Text.Json;
using Godot;

/// <summary>Parsed "--key value" / "--flag" user args.</summary>
public class Config
{
    readonly Dictionary<string, string> _o = new();

    public static Config Parse(string[] a)
    {
        var c = new Config();
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            var k = a[i][2..];
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) c._o[k] = a[++i];
            else c._o[k] = "true";
        }
        return c;
    }

    public string Str(string k, string d = "") => _o.TryGetValue(k, out var v) ? v : d;
    public int Int(string k, int d) => _o.TryGetValue(k, out var v) && int.TryParse(v, out var r) ? r : d;
    public float Float(string k, float d) => _o.TryGetValue(k, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : d;
    public bool Flag(string k) => _o.ContainsKey(k);
}

/// <summary>Role driven by the Spike root node.</summary>
public interface IRole
{
    void Physics(double dt);
    void Frame(double dt);
}

/// <summary>Constants and wire quantisation shared by both approaches.</summary>
public static class Net
{
    public const int Hz = 30;
    public const float PosUnit = 128f; // quantised position unit = 1/128 m, ushort per axis (512 m span)

    public static ushort QPos(float v, float min) =>
        (ushort)Mathf.Clamp(Mathf.RoundToInt((v - min) * PosUnit), 0, 65535);
    public static float DPos(int q, float min) => min + q / PosUnit;

    public static ushort QYaw(float rad)
    {
        var t = Mathf.PosMod(rad, Mathf.Tau) / Mathf.Tau;
        return (ushort)(Mathf.RoundToInt(t * 65536f) & 0xFFFF);
    }
    public static float DYaw(int q) => q * Mathf.Tau / 65536f;

    public static sbyte QVel(float v) => (sbyte)Mathf.Clamp(Mathf.RoundToInt(v * 8f), -127, 127);
    public static float DVel(int q) => q / 8f;

    // Reliable control codes (both approaches).
    public const int CtlWelcome = 1, CtlGo = 2, CtlStop = 3;
}

public static class Stats
{
    public static double Pct(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var idx = Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    public static Dictionary<string, object> Summary(List<double> v)
    {
        var s = new List<double>(v); s.Sort();
        return new()
        {
            ["count"] = s.Count,
            ["mean"] = s.Count > 0 ? s.Average() : 0.0,
            ["p50"] = s.Count > 0 ? Pct(s, 50) : 0.0,
            ["p95"] = s.Count > 0 ? Pct(s, 95) : 0.0,
            ["p99"] = s.Count > 0 ? Pct(s, 99) : 0.0,
            ["max"] = s.Count > 0 ? s[^1] : 0.0,
        };
    }
}

/// <summary>Process CPU time and memory from /proc (Linux).</summary>
public static class Proc
{
    public static double CpuSeconds()
    {
        try
        {
            var s = File.ReadAllText("/proc/self/stat");
            var rest = s[(s.LastIndexOf(')') + 2)..].Split(' ');
            return (double.Parse(rest[11]) + double.Parse(rest[12])) / 100.0; // utime+stime, CLK_TCK=100
        }
        catch { return 0; }
    }

    public static double PeakRssMb()
    {
        try
        {
            foreach (var l in File.ReadLines("/proc/self/status"))
                if (l.StartsWith("VmHWM:")) return double.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024.0;
        }
        catch { }
        return 0;
    }

    public static double Now() => Time.GetUnixTimeFromSystem();

    public static void WriteJson(string path, object o)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Window of measurement: CPU, wall time and ENet byte counters between Begin and End.</summary>
public class Window
{
    double _cpu0, _t0;
    public double Cpu, Wall, Sent, Recv, SentPackets, RecvPackets;

    public void Begin(ENetConnection? host)
    {
        _cpu0 = Proc.CpuSeconds(); _t0 = Proc.Now();
        if (host != null)
        {   // reset counters
            host.PopStatistic(ENetConnection.HostStatistic.SentData);
            host.PopStatistic(ENetConnection.HostStatistic.ReceivedData);
            host.PopStatistic(ENetConnection.HostStatistic.SentPackets);
            host.PopStatistic(ENetConnection.HostStatistic.ReceivedPackets);
        }
    }

    public void End(ENetConnection? host)
    {
        Cpu = Proc.CpuSeconds() - _cpu0; Wall = Proc.Now() - _t0;
        if (host != null)
        {
            Sent = host.PopStatistic(ENetConnection.HostStatistic.SentData);
            Recv = host.PopStatistic(ENetConnection.HostStatistic.ReceivedData);
            SentPackets = host.PopStatistic(ENetConnection.HostStatistic.SentPackets);
            RecvPackets = host.PopStatistic(ENetConnection.HostStatistic.ReceivedPackets);
        }
    }

    public Dictionary<string, object> ToDict() => new()
    {
        ["window_s"] = Wall, ["cpu_pct"] = Wall > 0 ? Cpu / Wall * 100.0 : 0.0,
        ["sent_bytes"] = Sent, ["recv_bytes"] = Recv, ["sent_packets"] = SentPackets, ["recv_packets"] = RecvPackets,
        ["sent_Bps"] = Sent / Math.Max(Wall, 1e-9), ["recv_Bps"] = Recv / Math.Max(Wall, 1e-9),
        ["peak_rss_mb"] = Proc.PeakRssMb(),
    };
}
