using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// A vehicle's solid box for on-foot movement (#217): a static body with the vehicle's hit volume on <see cref="CollisionLayers.VehicleSolid"/>.
/// The server keeps one for each <see cref="ServerCar"/> and every client one for each vehicle it knows, both placed from the numbers of the
/// vehicle's snapshot record, so the predicted move and the authoritative one are stopped by the same box. No mask: it only blocks
/// <see cref="Player"/>s, and no ray queries its layer (bullets hit the vehicle's own body on the server, the hit box maths on the client).
/// </summary>
public static class VehicleCollider
{
    /// <summary>The box as a child of <paramref name="parent"/> (which must be in the tree). It does not move with the parent: <see cref="Place"/> puts it in the world.</summary>
    public static StaticBody3D Make(Node parent, VehicleDef def)
    {
        var hw = def.HalfWidth; var hl = def.HalfLength; var h = def.Height; // the hit volume, as ServerCar
        var body = new StaticBody3D { CollisionLayer = CollisionLayers.VehicleSolid, CollisionMask = 0, Name = "VehicleCollider", TopLevel = true };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(hw * 2, h, hl * 2) }, Position = new Vector3(0, h / 2, 0) });
        parent.AddChild(body);
        return body;
    }

    /// <summary>Puts the box at a vehicle's place. The physics space has it at once (not at the end of the frame), so a move made right after this call meets it there.</summary>
    public static void Place(StaticBody3D body, Vector3 pos, float yaw, bool solid)
    {
        body.GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), pos);
        body.CollisionLayer = solid ? CollisionLayers.VehicleSolid : 0;
        body.ForceUpdateTransform();
    }
}
