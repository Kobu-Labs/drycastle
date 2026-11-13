using System.Text.Json;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The furniture of the church: the <c>props</c> of church.json (pews, altar, lectern, holy water) as Synty models (<see cref="SyntyLib"/>), each with
/// a box to walk into. The boxes are made on the server too; the models only where something is drawn. A pose is the middle of the prop on the
/// floor and the compass bearing it faces (a pew faces the way its sitters look).
/// </summary>
public partial class ChurchProps : Node3D
{
    bool _draw;

    // the furniture is hidden while the doors are shut and nobody is inside (ChurchInterior); its boxes stay (hiding a node does not turn its collision off)
    public override void _Ready()
    {
        if (!_draw) return;
        Visible = ChurchInterior.Visible;
        ChurchInterior.Changed += OnInterior;
    }

    public override void _ExitTree() => ChurchInterior.Changed -= OnInterior;

    void OnInterior(bool visible) => Visible = visible;

    /// <summary>Model, atlas, box (x across, y up, z deep, metres) and where the model's own origin sits across its width (a pew's is at one end).</summary>
    static readonly Dictionary<string, (string Model, string Atlas, Vector3 Box, float OriginX)> Kinds = new()
    {
        ["pew"] = ("pew_01", "glb/atlas/dark_fantasy_01_A.png", new Vector3(2.0f, 0.95f, 0.2f), -1.0f), // the backrest only: a row stays wide enough to walk into
        ["altar"] = ("altar_table_01", "glb/atlas/dark_fantasy_01_A.png", new Vector3(2.03f, 0.86f, 0.9f), 0f),
        ["holy_water"] = ("holy_water_01", "glb/atlas/dark_fantasy_01_A.png", new Vector3(1.09f, 1.31f, 1.06f), 0f),
        ["lectern"] = ("church_lectern_01", "glb/atlas/fantasy_kingdom_01_A.png", new Vector3(0.55f, 1.14f, 0.55f), 0f),
    };

    /// <summary>The props of the church.json in <paramref name="mapDir"/>, or null when the map has none.</summary>
    public static ChurchProps? Create(string mapDir, bool draw)
    {
        var path = Path.Combine(mapDir, "church.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("props", out var props)) return null;
        var node = new ChurchProps { Name = "ChurchProps", _draw = draw };
        foreach (var p in props.EnumerateArray())
        {
            var kind = p.GetProperty("kind").GetString() ?? "";
            if (!Kinds.TryGetValue(kind, out var k)) throw new DataFileException($"church.json: unknown prop kind '{kind}'");
            var pos = p.GetProperty("pos");
            var pivot = new Node3D
            {
                Position = new Vector3(pos[0].GetSingle(), pos[2].GetSingle(), -pos[1].GetSingle()),
                Rotation = new Vector3(0, Mathf.Pi - Mathf.DegToRad(p.GetProperty("yaw").GetSingle()), 0), // the models face +Z
            };
            var body = new StaticBody3D { CollisionLayer = CollisionLayers.World, Position = new Vector3(0, k.Box.Y / 2, kind == "pew" ? -0.15f : 0) };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = k.Box } });
            pivot.AddChild(body);
            if (draw && SyntyLib.Instance(k.Model, k.Atlas) is { } model)
            {
                model.Position = new Vector3(k.OriginX, 0, 0);
                pivot.AddChild(model);
            }
            node.AddChild(pivot);
        }
        return node;
    }
}
