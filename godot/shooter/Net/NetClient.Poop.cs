using Godot;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter;

/// <summary>
/// Poop on the client: the world entities of type <see cref="PoopRules.TypeId"/> a pet leaves behind. One of the poop models of the private pack
/// (<see cref="PoopModels"/>, picked by the entity id so every client draws the same) where the server put it, or a small brown cube without the pack; it never
/// moves, nothing touches it. Freed the moment the server despawns it (expired, or its group kept too many).
/// </summary>
public partial class NetClient
{
    const float PoopSizeM = 0.14f;

    /// <summary>The poop models (private pack, built real size: dog about 12 cm, cat 7 cm). One is drawn per poop.</summary>
    static readonly string[] PoopModels =
    {
        "synty/glb/pets/poop/dogpoo_01.glb", "synty/glb/pets/poop/dogpoo_02.glb", "synty/glb/pets/poop/dogpoo_03.glb", "synty/glb/pets/poop/catpoo_01.glb",
    };

    static readonly StandardMaterial3D PoopMaterial = new() { AlbedoColor = new Color(0.30f, 0.19f, 0.09f), Roughness = 1f };

    readonly Dictionary<ushort, Node3D> _poops = new();

    /// <summary>Called once from <see cref="_Ready"/>: registers the poop type and follows its entities.</summary>
    void InitPoop()
    {
        PoopRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnPoopSpawned;
        WorldEntities.Despawned += OnPoopDespawned;
        WorldEntities.DormancyChanged += OnPoopDormancy;
    }

    /// <summary>Poop outside the interest range (ADR 0025) is hidden; one that comes back is shown where the keyframe puts it.</summary>
    void OnPoopDormancy(WorldReplica.Entity e)
    {
        if (e.Type != PoopRules.TypeId || !_poops.TryGetValue(e.Id, out var node)) return;
        node.Visible = !e.Dormant;
        if (e.Dormant) return;
        var p = e.State.Position;
        node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
        node.Rotation = new Vector3(0, e.State.YawRad, 0);
    }

    void OnPoopSpawned(WorldReplica.Entity e)
    {
        if (e.Type != PoopRules.TypeId) return;
        var p = e.State.Position;
        var node = new Node3D { Name = $"Poop{e.Id}" };
        if (ModelLib.Instance(PoopModels[e.Id % PoopModels.Length]) is { } model) node.AddChild(model);
        else
            node.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(PoopSizeM, PoopSizeM * 0.7f, PoopSizeM) },
                MaterialOverride = PoopMaterial,
                Position = new Vector3(0, PoopSizeM * 0.35f, 0),
            });
        Owned.AddChild(node);
        node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
        node.Rotation = new Vector3(0, e.State.YawRad, 0);
        node.Visible = !e.Dormant; // spawned outside the interest range: hidden until a keyframe shows it
        _poops[e.Id] = node;
    }

    void OnPoopDespawned(ushort id)
    {
        if (_poops.Remove(id, out var node)) node.QueueFree();
    }
}
