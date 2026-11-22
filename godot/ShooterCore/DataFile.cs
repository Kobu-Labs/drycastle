using System.Text.Json;
using System.Text.Json.Serialization;

namespace Drycastle.ShooterCore;

/// <summary>
/// Shared JSON rules for the shooter data files (weapons.json, match.json, vehicles.json): snake_case keys,
/// a mandatory top-level "version", unknown fields ignored so later versions can add fields without a format break.
/// </summary>
public static class DataFile
{
    public const int SupportedVersion = 1;

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>"supported" is the newest version this file's schema understands; files that bump independently pass their own.</summary>
    public static T Parse<T>(string json, string what, int supported = SupportedVersion) where T : IVersioned
    {
        T? v;
        try { v = JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException e) { throw new DataFileException($"{what}: invalid JSON: {e.Message}", e); }
        if (v is null) throw new DataFileException($"{what}: empty file");
        if (v.Version < 1) throw new DataFileException($"{what}: missing or invalid \"version\"");
        if (v.Version > supported)
            throw new DataFileException($"{what}: version {v.Version} is newer than supported version {supported}");
        return v;
    }

    /// <summary>
    /// One object inside a data file read as <typeparamref name="T"/> by the same rules (snake_case keys, unknown fields ignored): the part of an entry only its own code knows
    /// (a Companion kind's <c>rules</c>). An absent or null section is a <typeparamref name="T"/> of defaults.
    /// </summary>
    public static T Section<T>(JsonElement section, string what) where T : new()
    {
        if (section.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new T();
        if (section.ValueKind != JsonValueKind.Object) throw new DataFileException($"{what}: must be an object");
        try { return section.Deserialize<T>(Options) ?? new T(); }
        catch (JsonException e) { throw new DataFileException($"{what}: {e.Message}", e); }
    }

    public static T Load<T>(string path, int supported = SupportedVersion) where T : IVersioned
    {
        if (!File.Exists(path)) throw new DataFileException($"{path}: file not found");
        return Parse<T>(File.ReadAllText(path), Path.GetFileName(path), supported);
    }
}

public interface IVersioned { int Version { get; } }

public class DataFileException : Exception
{
    public DataFileException(string message, Exception? inner = null) : base(message, inner) { }
}
