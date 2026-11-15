using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The anchor maths of the intel room on a placed object (ADR 0017, amended): where the screen of <see cref="IntelRoomFile"/>
/// ends up in the map, given how the object stands (its position, its turn about the vertical axis and its scale, all from the
/// map's objects.json). Godot-free; ground axes as everywhere (x east, y = z = -north), the turn as Godot's Y rotation: positive
/// is counter-clockwise seen from above, so the object's +x goes to (cos, -sin) and its +z to (sin, cos) on the ground.
/// </summary>
public static class IntelRoom
{
    /// <summary>An object's own ground vector (x, z) as it is on the map, for an object turned by <paramref name="yawDeg"/>.</summary>
    public static Vector2 Rotate(Vector2 local, float yawDeg)
    {
        var rad = yawDeg * MathF.PI / 180f;
        var (sin, cos) = MathF.SinCos(rad);
        return new Vector2(local.X * cos + local.Y * sin, -local.X * sin + local.Y * cos);
    }

    /// <summary>
    /// The anchor of the screen for the object <paramref name="hostId"/> standing at <paramref name="origin"/> (ground x, z) and
    /// <paramref name="y"/> (its origin's height), turned <paramref name="yawDeg"/> and scaled by <paramref name="scale"/> (the
    /// offsets of the room scale with it, the screen's size does not).
    /// </summary>
    public static IntelAnchor Place(string hostId, Vector2 origin, float y, float yawDeg, float scale, IntelRoomFile room)
    {
        if (scale <= 0) scale = 1f;
        var at = origin + Rotate(new Vector2(room.At[0], room.At[1]), yawDeg) * scale;
        var facing = Vector2.Normalize(Rotate(new Vector2(room.Facing[0], room.Facing[1]), yawDeg));
        var y0 = y + (room.FloorM + room.BottomM) * scale;
        return new IntelAnchor(hostId, at, facing, y0, y0 + room.HeightM, room.WidthM);
    }
}
