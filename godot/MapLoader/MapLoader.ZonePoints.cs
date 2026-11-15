using System.Text.Json;

namespace Drycastle.MapLoader;

public sealed partial class MapLoader
{
    /// <summary>
    /// Contents of zone_points.json, the baked candidate points of the zone kinds (empty when the map has none: the manifest lists
    /// it under <c>files.zone_points</c> only when some zone has usable ground); parsed by the server
    /// (<c>ShooterCore.ZonePoints</c>). A map without the file is not an error, it just has no points.
    /// </summary>
    public string ZonePointsJson { get; private set; } = "";

    void ReadZonePoints(string mapDir, JsonElement manifest)
    {
        ZonePointsJson = "";
        if (!manifest.TryGetProperty("files", out var listed) || !listed.TryGetProperty("zone_points", out var name)) return;
        var path = name.ValueKind == JsonValueKind.String ? Path.Combine(mapDir, name.GetString()!) : "";
        if (path.Length > 0 && File.Exists(path)) ZonePointsJson = File.ReadAllText(path);
    }
}
