using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Demon;

namespace Drycastle.Shooter.Demon;

/// <summary>
/// The demon's Body shape in the physics world (ADR 0028, GLOSSARY "Body shape"): one static capsule on <see cref="CollisionLayers.Demon"/>, the player's scaled as the demon is
/// (<see cref="DemonRules.BodyCapsule"/>). The server moves it to the demon's pose every tick and the client to the newest pose it has been sent, so a player's predicted move and the server's
/// are stopped by the same capsule; a ground vehicle's sweep stops on it as well. Rays never ask for the layer (a shot meets the demon by its Hit shape), and nothing pushes it.
/// </summary>
public sealed partial class DemonBlocker : StaticBody3D
{
    readonly CapsuleShape3D _shape = new();

    public DemonBlocker()
    {
        Name = "DemonBlocker";
        CollisionLayer = 0; CollisionMask = 0;
        AddChild(new CollisionShape3D { Shape = _shape });
    }

    /// <summary>Stands the capsule where the demon is (<paramref name="surface"/>, <paramref name="normal"/> as the entity gives them) when its state is a body, takes it away when not.</summary>
    public void Place(DemonState state, System.Numerics.Vector3 surface, System.Numerics.Vector3 normal, float bodyHeightM, float scale)
    {
        if (!DemonRules.Blocks(state)) { CollisionLayer = 0; return; }
        var (centre, radius, height) = DemonRules.BodyCapsule(surface, normal, bodyHeightM, scale);
        if (!Mathf.IsEqualApprox(_shape.Radius, radius) || !Mathf.IsEqualApprox(_shape.Height, height)) { _shape.Radius = radius; _shape.Height = height; }
        var at = new Vector3(centre.X, centre.Y, centre.Z);
        if (!IsInsideTree() || !GlobalPosition.IsEqualApprox(at)) GlobalPosition = at;
        CollisionLayer = CollisionLayers.Demon;
    }
}
