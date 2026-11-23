using System.Text.Json;
using System.Text.RegularExpressions;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.RozhlasFeed;

/// <summary>
/// rozhlas_voice.json: the announcement's voice chain (band limit, clipping, bit crush, wobble, echo ...), as DATA that both the offline bake
/// (scripts/sounds/make_rozhlas.py, <c>voice_chain</c>) and the live feed (<see cref="RozhlasFeedMixer"/> output piped through ffmpeg) build their ffmpeg filter chain from, so they cannot drift:
/// the file lists the filters in order with <c>{name}</c> placeholders; a placeholder becomes the literal text of <c>params[name]</c> (as written in the file), <c>{taps}</c> three
/// echo delays in ms, each random inside its <c>echo_taps_ms</c> range. The builder below and <c>voice_chain</c> in the script are the same algorithm; change both or neither.
/// Pure (no Godot, no process): the tests build the chain from the real file.
/// </summary>
public sealed partial class RozhlasVoice
{
    /// <summary>The live feed's mono PCM rate, in and out: Opus-friendly, and the chain resamples to its own 5 kHz inside anyway.</summary>
    public const int FeedRate = 16000;

    RozhlasVoice(IReadOnlyDictionary<string, string> p, IReadOnlyList<string> chain, IReadOnlyList<(int Lo, int Hi)> taps)
    {
        Params = p; Filters = chain; TapRangesMs = taps;
    }

    /// <summary>Parameter name to its literal text in the file (<c>6.0</c> stays <c>6.0</c>, <c>18</c> stays <c>18</c>).</summary>
    public IReadOnlyDictionary<string, string> Params { get; }
    /// <summary>The filter templates, in the order they run.</summary>
    public IReadOnlyList<string> Filters { get; }
    /// <summary>The range (ms, inclusive) each echo tap is picked from.</summary>
    public IReadOnlyList<(int Lo, int Hi)> TapRangesMs { get; }

    [GeneratedRegex(@"\{[a-z_]+\}")]
    private static partial Regex Placeholder();

    public static RozhlasVoice Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var ver) || ver.GetInt32() != 1) throw new DataFileException("rozhlas_voice.json: version must be 1");
            var p = new Dictionary<string, string>();
            foreach (var kv in root.GetProperty("params").EnumerateObject()) p[kv.Name] = kv.Value.GetRawText();
            var chain = root.GetProperty("chain").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            var taps = root.GetProperty("echo_taps_ms").EnumerateArray().Select(r => (Lo: r[0].GetInt32(), Hi: r[1].GetInt32())).ToList();
            var v = new RozhlasVoice(p, chain, taps);
            if (chain.Count == 0) throw new DataFileException("rozhlas_voice.json: chain is empty");
            if (taps.Any(t => t.Lo < 0 || t.Hi < t.Lo)) throw new DataFileException("rozhlas_voice.json: echo_taps_ms needs [lo, hi] ranges with 0 <= lo <= hi");
            if (!p.TryGetValue("noise", out var noise) || !double.TryParse(noise, System.Globalization.CultureInfo.InvariantCulture, out var n)) throw new DataFileException("rozhlas_voice.json: params.noise is missing");
            if (n != 0) throw new DataFileException("rozhlas_voice.json: params.noise must be 0 for the live feed (hiss is baked only)");
            v.BuildChain(taps.Select(t => t.Lo).ToArray()); // every placeholder must resolve
            return v;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            throw new DataFileException($"rozhlas_voice.json: {e.Message}", e);
        }
    }

    /// <summary>Echo delays for one variant: one per range, picked uniformly (inclusive). The bake and each live phase pick once.</summary>
    public int[] PickTaps(Random rng) => TapRangesMs.Select(r => rng.Next(r.Lo, r.Hi + 1)).ToArray();

    /// <summary>The comma-joined filter chain with the given echo taps. Same text as <c>voice_chain</c> in scripts/sounds/make_rozhlas.py for the same taps.</summary>
    public string BuildChain(IReadOnlyList<int> tapsMs)
    {
        var taps = string.Join("|", tapsMs.Select(t => t.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var parts = new List<string>();
        foreach (var tpl in Filters)
        {
            var f = tpl.Replace("{taps}", taps);
            foreach (var kv in Params) f = f.Replace("{" + kv.Key + "}", kv.Value);
            if (Placeholder().IsMatch(f)) throw new DataFileException($"rozhlas_voice.json: unresolved placeholder in \"{tpl}\"");
            parts.Add(f);
        }
        return string.Join(",", parts);
    }

    /// <summary>The ffmpeg -filter_complex graph of the live feed: the bake's own prefix (<c>aformat=channel_layouts=mono</c>), the chain, then the output rate (the bake ends with aresample too).</summary>
    public static string FilterGraph(string chain, int outRate = FeedRate) =>
        $"[0:a]aformat=channel_layouts=mono,{chain}[s];[s]aresample={outRate}[o]";

    /// <summary>
    /// ffmpeg arguments for the live feed: raw s16le mono in on stdin, the graph, raw s16le mono out on stdout, with no probing and a flush after every packet
    /// (the delay itself is enforced by <see cref="RozhlasFeedSchedule"/>, not by these flags).
    /// </summary>
    public static IReadOnlyList<string> LiveArgs(string graph, int rate = FeedRate) => new[]
    {
        "-hide_banner", "-loglevel", "error", "-nostats",
        "-probesize", "32", "-analyzeduration", "0", // raw PCM needs no probing. NOT -fflags nobuffer: it makes ffmpeg drop the first samples (measured: up to 0.1 s at the start of a live pipe), which would break the sample-exact delay
        "-f", "s16le", "-ar", rate.ToString(), "-ac", "1", "-i", "pipe:0",
        "-filter_complex", graph, "-map", "[o]",
        "-f", "s16le", "-ar", rate.ToString(), "-ac", "1", "-flush_packets", "1", "pipe:1",
    };
}
