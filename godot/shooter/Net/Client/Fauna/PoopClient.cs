using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Client.Fauna;

/// <summary>
/// Poop on the client: the world entities of type <see cref="PoopRules.TypeId"/> a pet leaves behind. One of the poop models of the private pack
/// (<see cref="Models"/>, picked by the entity id so every client draws the same) where the server put it, or a small brown cube without the pack; it never
/// moves, nothing touches it. Freed the moment the server despawns it (expired, or its group kept too many).
/// </summary>
public sealed class PoopClient
{
    const float SizeM = 0.14f;
    /// <summary>The poop models (private pack, built real size: dog about 12 cm, cat 7 cm). One is drawn per poop.</summary>
    static readonly string[] Models =
    {
        "synty/glb/pets/poop/dogpoo_01.glb", "synty/glb/pets/poop/dogpoo_02.glb", "synty/glb/pets/poop/dogpoo_03.glb", "synty/glb/pets/poop/catpoo_01.glb",
    };
    static readonly StandardMaterial3D Material = new() { AlbedoColor = new Color(0.30f, 0.19f, 0.09f), Roughness = 1f };

    readonly ClientContext _ctx;
    readonly Dictionary<ushort, Node3D> _poops = new();

    /// <summary>Registers the poop type and follows its entities.</summary>
    public PoopClient(ClientContext ctx)
    {
        _ctx = ctx;
        PoopRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
        ctx.WorldEntities.DormancyChanged += OnDormancy;
    }

    /// <summary>The poops' nodes went with <see cref="ClientContext.Owned"/>: forget them.</summary>
    public void Reset() => _poops.Clear();

    /// <summary>Poop outside the interest range (ADR 0025) is hidden; one that comes back is shown where the keyframe puts it.</summary>
    void OnDormancy(WorldReplica.Entity e)
    {
        if (e.Type != PoopRules.TypeId || !_poops.TryGetValue(e.Id, out var node)) return;
        node.Visible = !e.Dormant;
        if (e.Dormant) return;
        var p = e.State.Position;
        node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
        node.Rotation = new Vector3(0, e.State.YawRad, 0);
    }

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != PoopRules.TypeId) return;
        var p = e.State.Position;
        var node = new Node3D { Name = $"Poop{e.Id}" };
        if (ModelLib.Instance(Models[e.Id % Models.Length]) is { } model) node.AddChild(model);
        else
            node.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(SizeM, SizeM * 0.7f, SizeM) },
                MaterialOverride = Material,
                Position = new Vector3(0, SizeM * 0.35f, 0),
            });
        _ctx.Owned.AddChild(node);
        node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
        node.Rotation = new Vector3(0, e.State.YawRad, 0);
        node.Visible = !e.Dormant; // spawned outside the interest range: hidden until a keyframe shows it
        _poops[e.Id] = node;
    }

    void OnDespawned(ushort id)
    {
        if (_poops.Remove(id, out var node)) node.QueueFree();
    }
}
