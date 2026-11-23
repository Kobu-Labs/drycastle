using System.Text.Json;
using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;

namespace Drycastle.Shooter;

/// <summary>
/// The furniture of the Obecny Urad: the <c>props</c> of urad.json (desks, chairs, cabinets, plants; ADR 0020) as Synty models (<see cref="SyntyLib"/>, the private
/// SIMPLE Office pack, <c>atlas</c> names its texture), each with the collision boxes the file gives it. The boxes are made on the server too; the models only where
/// something is drawn. A pose is the prop's origin (the middle of its footprint, on whatever it stands on) and the compass bearing its front faces.
/// The file says everything (model, pose, boxes): a new prop needs no code. Without the pack the boxes themselves are drawn, so a desk is never missing.
/// </summary>
public partial class UradProps : Node3D
{
    static readonly StandardMaterial3D Fallback = new() { AlbedoColor = new Color(0.5f, 0.36f, 0.24f), Roughness = 0.9f };

    /// <summary>The props of the urad.json in <paramref name="mapDir"/>, or null when the map has none.</summary>
    public static UradProps? Create(string mapDir, bool draw)
    {
        var path = Path.Combine(mapDir, "urad.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Array) return null;
        var atlas = doc.RootElement.TryGetProperty("atlas", out var a) ? a.GetString() ?? "" : "";
        var node = new UradProps { Name = "UradProps" };
        foreach (var p in props.EnumerateArray())
        {
            var model = p.GetProperty("model").GetString() ?? "";
            var pos = p.GetProperty("pos");
            if (pos.GetArrayLength() != 3) throw new DataFileException($"urad.json: prop '{model}': pos is [x, y, z]");
            var pivot = new Node3D
            {
                Position = new Vector3(pos[0].GetSingle(), pos[2].GetSingle(), -pos[1].GetSingle()),
                Rotation = new Vector3(0, Mathf.Pi - Mathf.DegToRad(p.GetProperty("yaw").GetSingle()), 0), // the models face +Z
            };
            var drawn = draw && SyntyLib.Instance(model, atlas) is { } instance ? instance : null;
            if (drawn != null) pivot.AddChild(drawn);
            if (p.TryGetProperty("boxes", out var boxes))
                foreach (var b in boxes.EnumerateArray())
                {
                    if (b.GetArrayLength() != 6) throw new DataFileException($"urad.json: prop '{model}': a box is [x, y, z, size x, size y, size z]");
                    var size = new Vector3(b[3].GetSingle(), b[4].GetSingle(), b[5].GetSingle());
                    var body = new StaticBody3D { CollisionLayer = CollisionLayers.World, Position = new Vector3(b[0].GetSingle(), b[1].GetSingle(), b[2].GetSingle()) };
                    body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
                    if (draw && drawn == null) body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = Fallback }); // no pack: the box is the furniture
                    pivot.AddChild(body);
                }
            node.AddChild(pivot);
        }
        return node;
    }
}
