using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The client's copy of a vehicle's solid box (#217): a static body on the vehicle layer at the last snapshot position, so the predicted
/// on-foot move is stopped by the same box the server's <see cref="ServerCar"/> stops the authoritative one with. Layer 2, no mask:
/// it only blocks <see cref="Player"/>s (bullets on the client never query the vehicle layer).
/// </summary>
public static class VehicleCollider
{
    public static StaticBody3D Make(Node parent, VehicleDef def)
    {
        var hw = def.HalfWidth; var hl = def.HalfLength; var h = def.Height; // the hit volume, as ServerCar
        var body = new StaticBody3D { CollisionLayer = CollisionLayers.Vehicles, CollisionMask = 0, Name = "VehicleCollider" };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(hw * 2, h, hl * 2) }, Position = new Vector3(0, h / 2, 0) });
        parent.AddChild(body);
        return body;
    }

    public static void Place(StaticBody3D body, Vector3 pos, float yaw, bool solid)
    {
        body.GlobalPosition = pos;
        body.Rotation = new Vector3(0, yaw, 0);
        body.CollisionLayer = solid ? CollisionLayers.Vehicles : 0;
    }
}
