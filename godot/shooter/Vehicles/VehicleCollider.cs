using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Vehicles;

/// <summary>
/// A vehicle's solid box for on-foot movement (#217): a static body with the vehicle's hit volume on <see cref="CollisionLayers.VehicleSolid"/> (its Body shape, ADR 0028).
/// The server keeps one for each <see cref="ServerCar"/> and every client one for each vehicle it knows, both placed from the numbers of the
/// vehicle's snapshot record, so the predicted move and the authoritative one are stopped by the same box. No mask: it only blocks
/// <see cref="Player"/>s, and no ray queries its layer (bullets hit the vehicle's own body on the server, the hit box maths on the client).
/// A type with a hull list (<see cref="VehicleDef.Hull"/>) has one shape for each box, and <see cref="Place(StaticBody3D, Vector3, float, float, float, bool)"/> tilts them with its pitch and roll.
/// </summary>
public static class VehicleCollider
{
    /// <summary>The box as a child of <paramref name="parent"/> (which must be in the tree). It does not move with the parent: <see cref="Place(StaticBody3D, Vector3, float, bool)"/> puts it in the world.</summary>
    public static StaticBody3D Make(Node parent, VehicleDef def) => Make(parent, def.HullBoxes);

    /// <summary>A box of these half sizes and this height (ground to the top): the vehicle's hit volume, or the Raid van's (ADR 0021).</summary>
    public static StaticBody3D Make(Node parent, float halfWidth, float halfLength, float height) => Make(parent, new[] { HullBox.Upright(halfWidth, halfLength, height) });

    /// <summary>One shape for each box, in the node frame of the body.</summary>
    public static StaticBody3D Make(Node parent, HullBox[] boxes)
    {
        var body = new StaticBody3D { CollisionLayer = CollisionLayers.VehicleSolid, CollisionMask = 0, Name = "VehicleCollider", TopLevel = true };
        foreach (var b in boxes)
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(b.HalfWidth * 2, b.Y1 - b.Y0, b.HalfLength * 2) }, Position = new Vector3(b.Cx, (b.Y0 + b.Y1) / 2, b.Cz) });
        parent.AddChild(body);
        return body;
    }

    /// <summary>Puts the box at a vehicle's place. The physics space has it at once (not at the end of the frame), so a move made right after this call meets it there.</summary>
    public static void Place(StaticBody3D body, Vector3 pos, float yaw, bool solid) => Place(body, new Transform3D(new Basis(Vector3.Up, yaw), pos), solid);

    /// <summary>As <see cref="Place(StaticBody3D, Vector3, float, bool)"/>, turned by the whole attitude (a tilting hull): yaw, then pitch, then roll, as the vehicle is drawn.</summary>
    public static void Place(StaticBody3D body, Vector3 pos, float yaw, float pitch, float roll, bool solid) =>
        Place(body, new Transform3D(Basis.FromEuler(new Vector3(pitch, yaw, roll)), pos), solid);

    static void Place(StaticBody3D body, Transform3D at, bool solid)
    {
        body.GlobalTransform = at;
        body.CollisionLayer = solid ? CollisionLayers.VehicleSolid : 0;
        body.ForceUpdateTransform();
    }
}
