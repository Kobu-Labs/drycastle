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

    /// <summary>
    /// Contents of zone_polygons.json, the outlines of the zones of the kinds the server asks "is a player inside" of (the plantation); parsed by the server
    /// (<c>ShooterCore.ZonePolygons</c>). Listed under <c>files.zone_polygons</c> only on a map that has such a zone: without it the feature is off.
    /// </summary>
    public string ZonePolygonsJson { get; private set; } = "";

    void ReadZonePoints(string mapDir, JsonElement manifest)
    {
        ZonePolygonsJson = "";
        if (manifest.TryGetProperty("files", out var files) && files.TryGetProperty("zone_polygons", out var polys) && polys.ValueKind == JsonValueKind.String)
        {
            var polyPath = Path.Combine(mapDir, polys.GetString()!);
            if (File.Exists(polyPath)) ZonePolygonsJson = File.ReadAllText(polyPath);
        }
        ZonePointsJson = "";
        if (!manifest.TryGetProperty("files", out var listed) || !listed.TryGetProperty("zone_points", out var name)) return;
        var path = name.ValueKind == JsonValueKind.String ? Path.Combine(mapDir, name.GetString()!) : "";
        if (path.Length > 0 && File.Exists(path)) ZonePointsJson = File.ReadAllText(path);
    }
}
