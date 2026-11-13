using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Items on the ground on the client (issue #267, ADR 0018): the item's own model where a dropped item lies (a placeholder block for an item without
/// one), the pickup prompt within reach, and the interact key. A ground item is a world entity (type <see cref="GroundItemRules.TypeId"/>; its item and pickup count are in the replicated state), so it
/// appears, vanishes and reaches a joiner with the world channel. A tap of the interact key takes the nearest one in reach
/// (<see cref="RequestTakeGroundItem"/>); it owns the key when it is nearer than a corpse, and a care package in reach owns it before both. The
/// server validates reach and room and tells the result with the next Equipment event.
/// </summary>
public partial class NetClient
{
    readonly Dictionary<ushort, Node3D> _groundItems = new();

    /// <summary>The ground item within reach, from the last render, and how far it is (null when none, or when the player cannot take one).</summary>
    ushort? _groundNear;
    float _groundNearDist;
    /// <summary>The ground item in reach is nearer than any corpse in reach, so the interact key is its.</summary>
    bool _groundNearest;
    bool _groundUseWas;

    /// <summary>The nearest ground item in reach has the interact key: no care package in reach, none of the corpse chooser's.</summary>
    bool GroundItemOwnsKey => _groundNear != null && _groundNearest && _crateNear == null && !ChooserOpen;

    /// <summary>Called once from <see cref="_Ready"/>: registers the type and follows the ground item entities.</summary>
    void InitGroundItems()
    {
        GroundItemRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnGroundItemSpawned;
        WorldEntities.Despawned += OnGroundItemDespawned;
    }

    /// <summary>How far above the ground an item's lowest point rests, so its mesh does not z-fight the terrain.</summary>
    const float GroundLift = 0.02f;

    /// <summary>
    /// What an item on the ground looks like (the node is placed at the item's feet, so a model sits on y = 0): the item's own model (<see cref="ItemDef.Model"/>,
    /// the one a character wears), laid on the ground by its lowest point and centred on its footprint. The model is authored in the slot's frame (for headgear
    /// the origin is the middle of the head), so it is not placed by its origin. An item with no model, a model file that is missing and a headless run keep the
    /// placeholder: a small gold block standing on a corner.
    /// </summary>
    static Node3D MakeGroundItemVisual(ItemDef? item)
    {
        var node = new Node3D();
        if (item is { Model: not "" } && ModelLib.Instance(item.Model) is { } model && ModelBounds(model, Transform3D.Identity, true) is { } box)
        {
            model.Position = new Vector3(-(box.Position.X + box.Size.X / 2f), GroundLift - box.Position.Y, -(box.Position.Z + box.Size.Z / 2f));
            node.AddChild(model);
            return node;
        }
        node.AddChild(PlaceholderBlock());
        return node;
    }

    static MeshInstance3D PlaceholderBlock() => new()
    {
        Mesh = new BoxMesh { Size = new Vector3(0.28f, 0.28f, 0.28f) },
        MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.95f, 0.78f, 0.25f),
            EmissionEnabled = true,
            Emission = new Color(0.95f, 0.78f, 0.25f),
            EmissionEnergyMultiplier = 0.6f,
        },
        Position = new Vector3(0, 0.3f, 0),
        RotationDegrees = new Vector3(45, 45, 0), // stands on a corner
    };

    /// <summary>The box round every mesh under <paramref name="n"/> in the frame of the model's root (the root's own transform is left out when <paramref name="root"/>), or null when there is no mesh.</summary>
    static Aabb? ModelBounds(Node3D n, Transform3D parent, bool root)
    {
        var here = root ? Transform3D.Identity : parent * n.Transform;
        Aabb? box = n is MeshInstance3D { Mesh: not null } mi ? here * mi.GetAabb() : null;
        foreach (var c in n.GetChildren())
        {
            if (c is not Node3D child || ModelBounds(child, here, false) is not { } b) continue;
            box = box is { } have ? have.Merge(b) : b;
        }
        return box;
    }

    void OnGroundItemSpawned(WorldReplica.Entity e)
    {
        if (e.Type != GroundItemRules.TypeId) return;
        var node = MakeGroundItemVisual(ItemData.At(GroundItemRules.ItemOf(e.State)));
        node.Name = $"GroundItem{e.Id}";
        var p = e.State.Position;
        Owned.AddChild(node);
        node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
        _groundItems[e.Id] = node;
    }

    void OnGroundItemDespawned(ushort id)
    {
        if (!_groundItems.Remove(id, out var node)) return;
        node.QueueFree();
        if (_groundNear == id) { _groundNear = null; _groundNearest = false; }
    }

    /// <summary>Finds the ground item in reach and whether it is nearer than the corpse in reach.</summary>
    void RenderGroundItems()
    {
        _groundNear = null;
        _groundNearest = false;
        if (_me == null || Dead || _driving || _groundItems.Count == 0) return;
        var at = _me.GlobalPosition;
        var best = GroundItemRules.ReachMetres;
        foreach (var (id, node) in _groundItems)
        {
            var d = node.GlobalPosition.DistanceTo(at);
            if (d <= best) { _groundNear = id; _groundNearDist = best = d; }
        }
        if (_groundNear == null) return;
        var corpse = _near is { } c && _corpses.TryGetValue(c, out var v) ? v.Feet.DistanceTo(at) : float.MaxValue;
        _groundNearest = _groundNearDist <= corpse;
    }

    /// <summary>The prompt for the ground item that has the key; null otherwise.</summary>
    string? GroundItemPrompt()
    {
        if (!GroundItemOwnsKey || _groundNear is not { } id || !WorldEntities.Entities.TryGetValue(id, out var e)) return null;
        var def = ItemData.At(GroundItemRules.ItemOf(e.State));
        return def == null ? null : Strings.F("hud.ground.take", InputBindings.Name("interact"), ItemName(def));
    }

    /// <summary>A tap of the interact key at a ground item that has it asks the server to take it. Runs on the input tick.</summary>
    void TickGroundItems()
    {
        var use = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("interact");
        if (use && !_groundUseWas && GroundItemOwnsKey && _groundNear is { } id) RequestTakeGroundItem(id);
        _groundUseWas = use;
    }
}
