using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Poop on the client: the world entities of type <see cref="PoopRules.TypeId"/> a pet leaves behind. A small brown cube where the server put it; it never
/// moves, nothing touches it. Freed the moment the server despawns it (expired, or its group kept too many).
/// </summary>
public partial class NetClient
{
    const float PoopSizeM = 0.14f;

    static readonly StandardMaterial3D PoopMaterial = new() { AlbedoColor = new Color(0.30f, 0.19f, 0.09f), Roughness = 1f };

    readonly Dictionary<ushort, Node3D> _poops = new();

    /// <summary>Called once from <see cref="_Ready"/>: registers the poop type and follows its entities.</summary>
    void InitPoop()
    {
        PoopRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnPoopSpawned;
        WorldEntities.Despawned += OnPoopDespawned;
    }

    void OnPoopSpawned(WorldReplica.Entity e)
    {
        if (e.Type != PoopRules.TypeId) return;
        var p = e.State.Position;
        var node = new Node3D { Name = $"Poop{e.Id}" };
        node.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(PoopSizeM, PoopSizeM * 0.7f, PoopSizeM) },
            MaterialOverride = PoopMaterial,
            Position = new Vector3(0, PoopSizeM * 0.35f, 0),
        });
        Owned.AddChild(node);
        node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
        node.Rotation = new Vector3(0, e.State.YawRad, 0);
        _poops[e.Id] = node;
    }

    void OnPoopDespawned(ushort id)
    {
        if (_poops.Remove(id, out var node)) node.QueueFree();
    }
}
