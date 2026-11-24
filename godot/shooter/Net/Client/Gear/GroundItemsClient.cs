using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Gear;

/// <summary>
/// Items on the ground on the client (issue #267, ADR 0018): the item's own model where a dropped item lies (a placeholder block for an item without
/// one), the pickup prompt within reach, and the interact key. A ground item is a world entity (type <see cref="GroundItemRules.TypeId"/>; its item and pickup count are in the replicated state), so it
/// appears, vanishes and reaches a joiner with the world channel. A tap of the interact key takes the nearest one in reach
/// (<see cref="ItemsClient.RequestTakeGroundItem"/>); it owns the key when it is nearer than a corpse, and a care package in reach owns it before both. The
/// server validates reach and room and tells the result with the next Equipment event.
/// </summary>
internal sealed class GroundItemsClient
{
    readonly ClientContext _ctx;
    readonly ItemsClient _items;
    readonly ItemsFile _data;
    readonly InteractTargets _targets;
    readonly CorpsesClient _corpses;
    readonly Dictionary<ushort, Node3D> _nodes = new();
    Edge _use; // the interact key's press: a tap takes

    public GroundItemsClient(ClientContext ctx, ItemsClient items, ItemsFile data, InteractTargets targets, CorpsesClient corpses)
    {
        _ctx = ctx; _items = items; _data = data; _targets = targets; _corpses = corpses;
        GroundItemRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
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
        if (item is { Model: not "" } && ItemModels.Instance(item) is { } model && ItemModels.Bounds(model, Transform3D.Identity, true) is { } box)
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

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != GroundItemRules.TypeId) return;
        var node = MakeGroundItemVisual(_data.At(GroundItemRules.ItemOf(e.State)));
        node.Name = $"GroundItem{e.Id}";
        var p = e.State.Position;
        _ctx.Owned.AddChild(node);
        node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
        _nodes[e.Id] = node;
    }

    void OnDespawned(ushort id)
    {
        if (!_nodes.Remove(id, out var node)) return;
        node.QueueFree();
        if (_targets.GroundItem == id) { _targets.GroundItem = null; _targets.GroundItemNearest = false; }
    }

    /// <summary>Finds the ground item in reach and whether it is nearer than the corpse in reach.</summary>
    public void Render()
    {
        _targets.GroundItem = null;
        _targets.GroundItemNearest = false;
        if (_ctx.Me == null || _ctx.Dead || _ctx.Driving || _nodes.Count == 0) return;
        var at = _ctx.Me.GlobalPosition;
        var best = GroundItemRules.ReachMetres;
        foreach (var (id, node) in _nodes)
        {
            var d = node.GlobalPosition.DistanceTo(at);
            if (d <= best) { _targets.GroundItem = id; _targets.GroundItemDistance = best = d; }
        }
        if (_targets.GroundItem == null) return;
        var corpse = _targets.Corpse is { } c && _corpses.FeetOf(c) is { } feet ? feet.DistanceTo(at) : float.MaxValue;
        _targets.GroundItemNearest = _targets.GroundItemDistance <= corpse;
    }

    /// <summary>The prompt for the ground item that has the key; null otherwise.</summary>
    public string? Prompt()
    {
        if (_targets.Owner != InteractOwner.GroundItem || _targets.GroundItem is not { } id || !_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) return null;
        var def = _data.At(GroundItemRules.ItemOf(e.State));
        return def == null ? null : Strings.F("hud.ground.take", InputBindings.Name("interact"), _items.ItemName(def));
    }

    /// <summary>A tap of the interact key at a ground item that has it asks the server to take it. Runs on the input tick.</summary>
    public void Tick()
    {
        if (_use.Pressed(_ctx.InteractHeld) && _targets.Owner == InteractOwner.GroundItem && _targets.GroundItem is { } id) _items.RequestTakeGroundItem(id);
    }
}
