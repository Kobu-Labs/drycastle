using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;

namespace Drycastle.Shooter.Buildings;

/// <summary>
/// The Trap houses' props (#496, ADR 0046) in the world, on the server and every client: the hollow houses' floor plans (interiors.json of the map) and trap_house.json, and per dressed
/// house a node of its props. A solid prop is a box on the World layer (players and bullets stop on it) on the server too; where something is drawn, the Synty model (the private Gang
/// Warfare and Military packs, <see cref="SyntyLib"/>), or the box itself without the pack, and graffiti as text on the wall. The server's keeper (<c>TrapHouseServer</c>) says what is
/// dressed; a client follows its events. One per map: <see cref="Current"/>.
/// </summary>
public partial class TrapHousesNode : Node3D
{
    static readonly StandardMaterial3D Fallback = new() { AlbedoColor = new Color(0.42f, 0.4f, 0.36f), Roughness = 0.9f };

    public static TrapHousesNode? Current { get; private set; }

    /// <summary>The hollow houses with a floor plan, by building id.</summary>
    public IReadOnlyDictionary<string, HouseFloorPlan> Plans { get; private set; } = new Dictionary<string, HouseFloorPlan>();
    /// <summary>The hollow houses with a floor plan in file order (the order of interiors.json).</summary>
    public IReadOnlyList<HouseFloorPlan> Houses { get; private set; } = Array.Empty<HouseFloorPlan>();
    public TrapHouseFile Data { get; private set; } = null!;

    bool _draw;
    readonly Dictionary<string, Node3D> _dressed = new();
    /// <summary>Each dressed house's safe prop (its pivot: +Z is its front), for the Drill.</summary>
    readonly Dictionary<string, Node3D> _safes = new();

    /// <summary>The Trap house props of the map in <paramref name="mapDir"/>, or null when it has no hollow house with a floor plan, or a file is broken (logged).</summary>
    public static TrapHousesNode? Create(string mapDir, bool draw)
    {
        var path = Path.Combine(mapDir, "interiors.json");
        if (!File.Exists(path)) return null;
        try
        {
            var houses = HouseFloorPlan.Parse(File.ReadAllText(path)).Where(h => h.Rooms.Length > 0).ToList();
            if (houses.Count == 0) return null;
            var text = Godot.FileAccess.Open($"res://data/{TrapHouseFile.FileName}", Godot.FileAccess.ModeFlags.Read)?.GetAsText()
                ?? throw new DataFileException($"{TrapHouseFile.FileName}: cannot open ({Godot.FileAccess.GetOpenError()})");
            return new TrapHousesNode
            {
                Name = "TrapHouses", _draw = draw, Data = TrapHouseFile.Parse(text),
                Houses = houses, Plans = houses.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First()),
            };
        }
        catch (DataFileException e)
        {
            GD.PrintErr($"{e.Message}; no Trap houses on this map");
            return null;
        }
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    public bool IsDressed(string house) => _dressed.ContainsKey(house);

    public IEnumerable<string> Dressed => _dressed.Keys;

    /// <summary>House <paramref name="house"/>'s safe prop (a pivot at its foot, +Z its front; its model under it), or null. A new dressing makes a new one.</summary>
    public Node3D? SafeOf(string house) => _safes.TryGetValue(house, out var s) && IsInstanceValid(s) ? s : null;

    /// <summary>House <paramref name="house"/> stands dressed with <paramref name="props"/> (what it had before goes). A kind this file does not know is skipped.</summary>
    public void Show(string house, IReadOnlyList<TrapProp> props)
    {
        Hide(house);
        var root = new Node3D { Name = "Trap_" + house };
        var safe = Data.KindOf(Data.Safe.Prop);
        foreach (var p in props)
        {
            if (p.Kind >= Data.Props.Count) continue;
            var node = Prop(Data.Props[p.Kind], p);
            root.AddChild(node);
            if (p.Kind == safe) _safes[house] = node;
        }
        AddChild(root);
        _dressed[house] = root;
    }

    /// <summary>House <paramref name="house"/>'s props are taken away; false when it had none.</summary>
    public bool Hide(string house)
    {
        _safes.Remove(house);
        if (!_dressed.Remove(house, out var root)) return false;
        RemoveChild(root);
        root.QueueFree();
        return true;
    }

    /// <summary>Every dressed house stripped (a client leaving its server, a round reset).</summary>
    public void HideAll()
    {
        foreach (var house in _dressed.Keys.ToList()) Hide(house);
    }

    Node3D Prop(TrapPropKind k, TrapProp p)
    {
        var pivot = new Node3D
        {
            Position = new Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z),
            Rotation = new Vector3(0, p.Yaw + Mathf.Pi, 0), // the models face +Z; a prop's yaw is the way its front faces
        };
        var size = new Vector3(k.Across, k.Up, k.Depth);
        if (k.Solid)
        {
            var body = new StaticBody3D { CollisionLayer = CollisionLayers.World, CollisionMask = 0, Position = new Vector3(0, k.Up / 2, 0) };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            pivot.AddChild(body);
        }
        if (!_draw) return pivot;
        if (k.Tags.Count > 0)
        {
            pivot.AddChild(Tag(k, k.Tags[p.Variant % k.Tags.Count]));
            return pivot;
        }
        var model = k.Models.Count > 0 ? SyntyLib.Instance(k.Models[p.Variant % k.Models.Count], k.Atlas) : null;
        if (model == null) // no pack: the box is the prop
        {
            pivot.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = Fallback, Position = new Vector3(0, k.Up / 2, 0) });
            return pivot;
        }
        pivot.AddChild(model);
        foreach (var e in k.Extras)
            if (SyntyLib.Instance(e.Model, k.Atlas) is { } extra)
            {
                extra.Position = new Vector3(e.At[0], e.At[1], e.At[2]);
                extra.Rotation = new Vector3(0, Mathf.DegToRad(e.YawDeg), 0);
                pivot.AddChild(extra);
            }
        return pivot;
    }

    /// <summary>A graffiti: its text sprayed on the wall, as wide as the prop (and no taller), readable from the room.</summary>
    static Label3D Tag(TrapPropKind k, TrapTag tag)
    {
        const int fontSize = 64;
        var chars = Math.Max(1, tag.Text.Length);
        var pixel = MathF.Min(k.Across / (chars * fontSize * 0.6f), k.Up / fontSize);
        return new Label3D
        {
            Text = tag.Text, FontSize = fontSize, PixelSize = pixel, Modulate = Color.FromHtml(tag.Colour), OutlineSize = 0,
            Shaded = true, DoubleSided = false, Billboard = BaseMaterial3D.BillboardModeEnum.Disabled, Position = new Vector3(0, 0, 0.01f),
        };
    }
}
