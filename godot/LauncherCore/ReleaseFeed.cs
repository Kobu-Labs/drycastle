using System.Text.Json;

namespace Drycastle.LauncherCore;

public static class Channels
{
    public const string Stable = "stable", Dev = "dev";
    public static readonly string[] All = { Stable, Dev };
    public static string Normalize(string? c) => c == Dev ? Dev : Stable;
}

public sealed record ReleaseInfo(string Tag, bool Prerelease, DateTimeOffset Published, string? ManifestUrl);

/// <summary>Finds the newest game release of a channel through the (unauthenticated) GitHub releases API.</summary>
public sealed class ReleaseFeed
{
    public const string DefaultRepo = "Kobu-Labs/drycastle";
    readonly HttpClient _http;
    readonly string _repo;

    public ReleaseFeed(HttpClient http, string? repo = null)
    {
        _http = http;
        _repo = repo ?? Environment.GetEnvironmentVariable("DRYCASTLE_REPO") ?? DefaultRepo;
    }

    /// <summary>Feed calls use a short timeout so a dead network never blocks Play for long; downloads get a long one.</summary>
    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var c = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("drycastle-launcher");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Stable: newest non-prerelease. Dev: newest of any. Releases without a manifest.json asset (map releases) never count.</summary>
    public static ReleaseInfo? Pick(IEnumerable<ReleaseInfo> releases, string channel) =>
        releases.Where(r => r.ManifestUrl != null && (Channels.Normalize(channel) == Channels.Dev || !r.Prerelease))
                .OrderByDescending(r => r.Published).FirstOrDefault();

    public static List<ReleaseInfo> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        var list = new List<ReleaseInfo>();
        foreach (var r in items)
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            string? url = null;
            if (r.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (a.GetProperty("name").GetString() == "manifest.json") url = a.GetProperty("browser_download_url").GetString();
            var published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : DateTimeOffset.MinValue;
            list.Add(new ReleaseInfo(r.GetProperty("tag_name").GetString() ?? "", r.GetProperty("prerelease").GetBoolean(), published, url));
        }
        return list;
    }

    public async Task<Manifest?> LatestAsync(string channel, CancellationToken ct, string? platform = null)
    {
        // Stable asks GitHub for "latest" (newest non-prerelease), so map and dev releases can never push it out of a list window.
        string url = Channels.Normalize(channel) == Channels.Stable
            ? $"https://api.github.com/repos/{_repo}/releases/latest"
            : $"https://api.github.com/repos/{_repo}/releases?per_page=100";
        string json;
        try { json = await _http.GetStringAsync(url, ct); }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        var pick = Pick(ParseReleases(json), channel);
        if (pick?.ManifestUrl is null) return null;
        return Manifest.Parse(await _http.GetStringAsync(pick.ManifestUrl, ct), platform);
    }
}
