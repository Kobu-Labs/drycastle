using Godot;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter;

/// <summary>
/// What a piece of Loot looks like (GLOSSARY "Loot"), client only and drawn from primitives (no private asset): <see cref="Carried"/> is the prop in a Thief's hands (origin where the hands meet,
/// in front of the chest, facing -Z like the Thief), <see cref="Lying"/> the prop on the ground (origin on the ground where it lies). A new kind of Loot is one more arm of each switch and its two
/// methods in its own file (<c>LootProp.Pet.cs</c>, <c>LootProp.Dish.cs</c>, <c>LootProp.Connector.cs</c> hold a stub each: a small coloured shape), so the three later tickets edit only their file.
/// </summary>
public static partial class LootProp
{
    /// <summary>
    /// thieves.json <c>gang.view.carry</c> (set by the client when the event data arrives): where a kind has a pack model (<see cref="CarryKind.Model"/>, the tire's and, if switched on, the connector's) it replaces the
    /// primitive shape; without the private pack or the file the shape is drawn as before. Null: shapes only.
    /// </summary>
    public static GangCarry? Config { get; set; }

    /// <summary>A pack model of the kind's carry data (<c>props/tire_01</c> in its atlas), or null.</summary>
    static Node3D? PackModel(LootKind kind) => Config?.Kinds.GetValueOrDefault(kind.ToString().ToLowerInvariant()) is { Model: not "" } k ? SyntyLib.Instance(k.Model, k.Atlas) : null;

    /// <summary>The prop a Thief carries in both hands. Its origin is the middle of the prop, facing -Z like the Thief; the view puts it at the member's chest (<see cref="CarryKind.PropAt"/>).</summary>
    public static Node3D Carried(LootKind kind) => kind switch
    {
        LootKind.Tire => CarriedTire(),
        LootKind.Pet => CarriedPet(),
        LootKind.Dish => CarriedDish(),
        LootKind.Connector => CarriedConnector(),
        _ => Stub(new Color(0.8f, 0.2f, 0.8f), 0.25f),
    };

    /// <summary>The prop of Loot that lies on the ground.</summary>
    public static Node3D Lying(LootKind kind) => kind switch
    {
        LootKind.Tire => LyingTire(),
        LootKind.Pet => LyingPet(),
        LootKind.Dish => LyingDish(),
        LootKind.Connector => LyingConnector(),
        _ => Stub(new Color(0.8f, 0.2f, 0.8f), 0.25f),
    };

    /// <summary>The word for a kind in the use prompt ("Hold E  send the tire back").</summary>
    public static string NameKey(LootKind kind) => "loot." + kind.ToString().ToLowerInvariant();

    /// <summary>A small coloured box standing for a kind whose prop is not made yet, with its base on the origin.</summary>
    internal static Node3D Stub(Color colour, float size)
    {
        var root = new Node3D { Name = "Stub" };
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One * size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = colour, Roughness = 0.9f },
            Position = new Vector3(0, size / 2, 0),
        });
        return root;
    }
}
