using System.Numerics;

namespace Drycastle.ShooterCore.Buildings;

/// <summary>
/// An entrance the two-state doors fill (the church's, the Urad's), Godot-free, in Godot axes (x east, y up, z = -north; a yaw is a Godot yaw, 0 faces -Z, positive turns left).
/// <see cref="Pos"/> is the middle of the opening on the floor, <see cref="Yaw"/> points out of the building. The rule that keeps a closing door from shutting through a body is here
/// (<see cref="PushOut"/>), as is where somebody who must leave stands afterwards (<see cref="Outside"/>).
/// </summary>
public sealed record Doorway(Vector3 Pos, float Yaw, float Width, float Height)
{
    /// <summary>How deep the closed doors' collision box is (the doorway runs through a 0.4 m wall).</summary>
    public const float Depth = 0.3f;

    /// <summary>Unit vector out of the building through the doorway (a node with <see cref="Yaw"/> has its -Z pointing out).</summary>
    public Vector3 Out => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
    /// <summary>Unit vector across the doorway, to the right of someone standing outside and facing in (the local +X of a node with <see cref="Yaw"/>).</summary>
    public Vector3 Right => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));

    /// <summary>Whether a body (a capsule of <paramref name="radius"/>) standing at <paramref name="feet"/> is in the way of the closed doors.</summary>
    public bool Overlaps(Vector3 feet, float radius, float height = 1.8f)
    {
        var d = feet - Pos;
        return MathF.Abs(Vector3.Dot(d, Right)) < Width / 2 + radius && MathF.Abs(Vector3.Dot(d, Out)) < Depth / 2 + radius
            && feet.Y + height > Pos.Y && feet.Y < Pos.Y + Height;
    }

    /// <summary>
    /// Where a body standing at <paramref name="feet"/> must be moved so the closing doors do not shut through it, or null when it is clear of them. It moves along the
    /// doorway's depth only, to the nearer side (outside on a tie).
    /// </summary>
    public Vector3? PushOut(Vector3 feet, float radius, float height = 1.8f)
    {
        if (!Overlaps(feet, radius, height)) return null;
        var depth = Vector3.Dot(feet - Pos, Out);
        var target = (depth >= 0 ? 1f : -1f) * (Depth / 2 + radius + 0.05f);
        return feet + Out * (target - depth);
    }

    /// <summary>The point <paramref name="metres"/> out from the middle of the doorway, on the doorway's floor height.</summary>
    public Vector3 Outside(float metres) => Pos + Out * metres;
}
