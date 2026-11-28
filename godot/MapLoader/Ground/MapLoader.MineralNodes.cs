namespace Drycastle.MapLoader;

public sealed partial class MapLoader
{
    /// <summary>
    /// Contents of mineral_nodes.json, the static mineral nodes of the edits (empty when the map has none: the manifest lists it under <c>files.mineral_nodes</c> only
    /// when there is one); parsed by the server (<c>ShooterCore.Mining.MineralNodeFile</c>). A map without the file is not an error, it just has no static nodes.
    /// </summary>
    public string MineralNodesJson { get; private set; } = "";

    void ReadMineralNodes(string mapDir, System.Text.Json.JsonElement manifest)
    {
        MineralNodesJson = "";
        if (!manifest.TryGetProperty("files", out var listed) || !listed.TryGetProperty("mineral_nodes", out var name) || name.ValueKind != System.Text.Json.JsonValueKind.String) return;
        var path = Path.Combine(mapDir, name.GetString()!);
        if (File.Exists(path)) MineralNodesJson = File.ReadAllText(path);
    }
}
