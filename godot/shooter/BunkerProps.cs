using System.Text.Json;
using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;

namespace Drycastle.Shooter;

/// <summary>
/// The furniture of the bunkers: the <c>props</c> of every bunker in bunkers.json (beds, crates, shelves, the command post's table; ADR 0009 §7) as Synty POLYGON War
/// models (<see cref="SyntyLib"/>, the private pack; <c>atlas</c> names its texture), each with the collision boxes the file gives it. The boxes are made on the server
/// too; the models only where something is drawn. A pose is the prop's origin (the middle of its footprint, on the floor) and the compass bearing its front faces;
/// <c>scale</c> is the model's own (x, y, z). Like <see cref="UradProps"/> the file says everything, and without the pack the boxes themselves are drawn.
/// </summary>
public partial class BunkerProps : Node3D
{
    static readonly StandardMaterial3D Fallback = new() { AlbedoColor = new Color(0.36f, 0.38f, 0.3f), Roughness = 0.9f };

    /// <summary>The props of the bunkers.json in <paramref name="mapDir"/>, or null when the map has none.</summary>
    public static BunkerProps? Create(string mapDir, bool draw)
    {
        var path = Path.Combine(mapDir, "bunkers.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("bunkers", out var bunkers) || bunkers.ValueKind != JsonValueKind.Array) return null;
        var node = new BunkerProps { Name = "BunkerProps" };
        foreach (var bunker in bunkers.EnumerateArray())
        {
            if (!bunker.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Array) continue;
            var atlas = bunker.TryGetProperty("atlas", out var a) ? a.GetString() ?? "" : "";
            foreach (var p in props.EnumerateArray()) node.AddChild(Prop(p, atlas, draw));
        }
        return node.GetChildCount() > 0 ? node : null;
    }

    static Node3D Prop(JsonElement p, string atlas, bool draw)
    {
        var model = p.GetProperty("model").GetString() ?? "";
        var pos = p.GetProperty("pos");
        if (pos.GetArrayLength() != 3) throw new DataFileException($"bunkers.json: prop '{model}': pos is [x, y, z]");
        var pivot = new Node3D
        {
            Position = new Vector3(pos[0].GetSingle(), pos[2].GetSingle(), -pos[1].GetSingle()),
            Rotation = new Vector3(0, Mathf.Pi - Mathf.DegToRad(p.GetProperty("yaw").GetSingle()), 0), // the models face +Z
        };
        var own = p.TryGetProperty("atlas", out var pa) ? pa.GetString() ?? atlas : atlas; // a poster wears its own sheet
        var drawn = draw && SyntyLib.Instance(model, own) is { } instance ? instance : null;
        if (drawn != null)
        {
            if (p.TryGetProperty("scale", out var sc) && sc.GetArrayLength() == 3) drawn.Scale = new Vector3(sc[0].GetSingle(), sc[1].GetSingle(), sc[2].GetSingle());
            pivot.AddChild(drawn);
        }
        if (p.TryGetProperty("boxes", out var boxes))
            foreach (var b in boxes.EnumerateArray())
            {
                if (b.GetArrayLength() != 6) throw new DataFileException($"bunkers.json: prop '{model}': a box is [x, y, z, size x, size y, size z]");
                var size = new Vector3(b[3].GetSingle(), b[4].GetSingle(), b[5].GetSingle());
                var body = new StaticBody3D { CollisionLayer = CollisionLayers.World, Position = new Vector3(b[0].GetSingle(), b[1].GetSingle(), b[2].GetSingle()) };
                body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
                if (draw && drawn == null) body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = Fallback }); // no pack: the box is the furniture
                pivot.AddChild(body);
            }
        return pivot;
    }
}
