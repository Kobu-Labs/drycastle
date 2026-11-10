using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suchohrad.ShooterCore;

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

    public static T Parse<T>(string json, string what) where T : IVersioned
    {
        T? v;
        try { v = JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException e) { throw new DataFileException($"{what}: invalid JSON: {e.Message}", e); }
        if (v is null) throw new DataFileException($"{what}: empty file");
        if (v.Version < 1) throw new DataFileException($"{what}: missing or invalid \"version\"");
        if (v.Version > SupportedVersion)
            throw new DataFileException($"{what}: version {v.Version} is newer than supported version {SupportedVersion}");
        return v;
    }

    public static T Load<T>(string path) where T : IVersioned
    {
        if (!File.Exists(path)) throw new DataFileException($"{path}: file not found");
        return Parse<T>(File.ReadAllText(path), Path.GetFileName(path));
    }
}

public interface IVersioned { int Version { get; } }

public class DataFileException : Exception
{
    public DataFileException(string message, Exception? inner = null) : base(message, inner) { }
}
