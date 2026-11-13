using System.Text.Json;

namespace Drycastle.MapLoader;

public sealed partial class MapLoader
{
    /// <summary>
    /// Contents of attribute_points.json, the baked candidate points of the runtime attributes (empty when the map has none:
    /// the manifest lists it under <c>files.attribute_points</c> only when an area carries one); parsed by the server
    /// (<c>ShooterCore.AttributePoints</c>). A map without the file is not an error, it just has no points.
    /// </summary>
    public string AttributePointsJson { get; private set; } = "";

    void ReadAttributePoints(string mapDir, JsonElement manifest)
    {
        AttributePointsJson = "";
        if (!manifest.TryGetProperty("files", out var listed) || !listed.TryGetProperty("attribute_points", out var name)) return;
        var path = name.ValueKind == JsonValueKind.String ? Path.Combine(mapDir, name.GetString()!) : "";
        if (path.Length > 0 && File.Exists(path)) AttributePointsJson = File.ReadAllText(path);
    }
}
