using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suchohrad.LauncherCore;

/// <summary>One downloadable archive of a release (game zip or map zip).</summary>
public sealed record FileRef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

/// <summary>The map archive: its own release (<c>map-*</c>), referenced by id so most game updates skip the 48 MB download.</summary>
public sealed record MapRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("file")] FileRef File);

/// <summary>One platform's game build inside manifest.json.</summary>
public sealed record PlatformBuild(
    [property: JsonPropertyName("exe")] string Exe,
    [property: JsonPropertyName("game")] FileRef Game);

/// <summary>manifest.json attached to every game release by CI (scripts/make_manifest.py).</summary>
public sealed record Manifest(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("map")] MapRef Map,
    [property: JsonPropertyName("platforms")] Dictionary<string, PlatformBuild>? Platforms = null)
{
    public const int SupportedSchema = 2;
    public const string Windows = "windows", Linux = "linux";

    /// <summary>The platform this launcher runs on, or null when no game build exists for it.</summary>
    public static string? CurrentPlatform() => OperatingSystem.IsWindows() ? Windows : OperatingSystem.IsLinux() ? Linux : null;

    /// <summary>The game archive for the chosen platform (set by <see cref="Parse"/>).</summary>
    [JsonIgnore] public FileRef Game { get; init; } = null!;
    /// <summary>Path of the game executable inside the archive (set by <see cref="Parse"/>).</summary>
    [JsonIgnore] public string Exe { get; init; } = "";

    public static Manifest Parse(string json, string? platform = null)
    {
        var raw = JsonSerializer.Deserialize<Manifest>(json) ?? throw new FormatException("empty manifest");
        platform ??= CurrentPlatform() ?? throw new FormatException("this operating system has no game build");
        if (raw.Schema > SupportedSchema) throw new FormatException($"manifest schema {raw.Schema} is newer than this launcher supports ({SupportedSchema}); update the launcher");
        if (raw.Platforms is null || !raw.Platforms.TryGetValue(platform, out var build) || build is null)
            throw new FormatException($"this release has no {platform} build");
        var m = new Manifest(raw.Schema, raw.Version, raw.Channel, raw.Commit, raw.Protocol, raw.Map, raw.Platforms) { Game = build.Game, Exe = build.Exe ?? "" };
        if (string.IsNullOrWhiteSpace(m.Version) || m.Game is null || m.Map is null || m.Map.File is null || string.IsNullOrWhiteSpace(m.Exe))
            throw new FormatException("manifest is missing required fields");
        foreach (var f in new[] { m.Game, m.Map.File })
            if (f is null || string.IsNullOrWhiteSpace(f.Url) || string.IsNullOrWhiteSpace(f.Sha256) || string.IsNullOrWhiteSpace(f.Name) || f.Size < 0)
                throw new FormatException("manifest file entry is incomplete");
        if (m.Exe.Contains("..") || Path.IsPathRooted(m.Exe)) throw new FormatException("manifest exe path is not relative");
        return m;
    }
}

/// <summary>What is installed (installed.json next to the game folder).</summary>
public sealed record InstalledState(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("mapId")] string MapId,
    [property: JsonPropertyName("exe")] string Exe,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("protocol")] int Protocol);
