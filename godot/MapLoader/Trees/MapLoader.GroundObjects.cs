using Godot;
using Drycastle.ShooterCore.Colliders;

using Drycastle.MapLoader.Core;

namespace Drycastle.MapLoader;

/// <summary>
/// Collision of the forest's ground objects (ground_objects.json, map 0.13.0+): stumps, boulders, root plates and the earth mounds are solid
/// for players and shots (world layer); fallen logs are not (#359: drawn only), built from the pure <see cref="GroundObjectColliders"/>, so the server and the client agree. One
/// static body per chunk and surface ("wood" or "rock") holds the boxes, like the tree trunks. A map without the file has none.
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>Boxes built for ground objects (0 on a map without ground_objects.json).</summary>
    public int GroundObjectSolids { get; private set; }

    /// <summary>Ground object colliders in batches: yields the fraction done after each batch so a loading screen can redraw.</summary>
    IEnumerable<float> BuildGroundObjectColliderSteps(string mapDir, System.Text.Json.JsonElement manifest)
    {
        const int Batch = 500;
        GroundObjectSolids = 0;
        if (!manifest.TryGetProperty("files", out var files) || !files.TryGetProperty("ground_objects", out var name) || name.GetString() is not { } file) yield break;
        var path = Path.Combine(mapDir, file);
        if (!File.Exists(path)) yield break;
        var tp = LoadTimers.Start();
        var objects = GroundObjectColliders.Parse(File.ReadAllText(path), out var model);
        LoadTimers.Lap("groundobj.parse", tp);
        var bodies = new Dictionary<(int, int, string), StaticBody3D>();
        var n = 0;
        foreach (var o in objects)
        {
            if (n++ % Batch == Batch - 1) yield return 0.5f * n / objects.Count;
            foreach (var part in GroundObjectColliders.Parts(o, model))
            {
                var key = (o.ChunkX, o.ChunkY, GroundObjectColliders.Surface(o.Kind));
                if (!bodies.TryGetValue(key, out var body)) bodies[key] = body = NewSolidBody("GroundObjects", part.Layer, key.Item3);
                AddSolid(body, part);
                GroundObjectSolids++;
            }
        }
        // filled while detached, attached once each (see BuildTreeColliderSteps)
        var attached = 0;
        foreach (var ((cx, cy, _), body) in bodies)
        {
            (_chunkRoots.TryGetValue($"chunk_{cx}_{cy}", out var cr) ? cr : Root).AddChild(body);
            yield return 0.5f + 0.5f * ++attached / bodies.Count; // one body a step: attaching one with its shapes costs milliseconds
        }
    }
}
