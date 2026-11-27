using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>A vehicle's box for a ram, map local (x east, y north, z up): its centre, unit axes and half sizes along them (x right, y up, z forward).</summary>
public readonly record struct RamBox(Vector3 Centre, Vector3 Right, Vector3 Up, Vector3 Fwd, Vector3 Half)
{
    /// <summary>An upright box over a footprint, from Godot's frame (y up, -z north; yaw 0 faces -z, positive turns left): the feet on the ground under the centre.</summary>
    public static RamBox FromGodot(Vector3 feet, float yaw, float halfWidth, float halfLength, float height)
    {
        var (s, c) = MathF.SinCos(yaw);
        return new RamBox(new Vector3(feet.X, -feet.Z, feet.Y + height / 2), new Vector3(c, s, 0), Vector3.UnitZ, new Vector3(-s, c, 0), new Vector3(halfWidth, height / 2, halfLength));
    }

    /// <summary>Godot's frame (y up, -z north) to map local.</summary>
    public static Vector3 ToMap(Vector3 godot) => new(godot.X, -godot.Z, godot.Y);
    /// <summary>Map local to Godot's frame.</summary>
    public static Vector3 ToGodot(Vector3 map) => new(map.X, map.Z, -map.Y);

    public float Extent(Vector3 dir) => Half.X * MathF.Abs(Vector3.Dot(dir, Right)) + Half.Y * MathF.Abs(Vector3.Dot(dir, Up)) + Half.Z * MathF.Abs(Vector3.Dot(dir, Fwd));
}

/// <summary>What a ram does: <paramref name="Through"/> the wall at <paramref name="SpeedKept"/> m/s, or stopped by it; <paramref name="SpeedLost"/> is what the crash rule reads.</summary>
public readonly record struct RamOutcome(bool Through, float SpeedKept, float SpeedLost);

/// <summary>
/// The momentum rule of a ram (a thin fence's, FenceDestructionServer): the momentum a vehicle loses to what it hits (mass times the speed it took)
/// against what breaking it costs. A hollow house is ploughed instead (<see cref="HousePlough"/>).
/// </summary>
public static class HouseRam
{
    /// <summary>
    /// The ram's outcome: through when the momentum (<paramref name="mass"/> times the <paramref name="impact"/> speed the wall took) is over the
    /// <paramref name="cost"/>; the vehicle then keeps <paramref name="speed"/> less cost / mass and loses the rest. Else it stops: the whole impact lost.
    /// </summary>
    public static RamOutcome Decide(float cost, float mass, float impact, float speed)
    {
        if (mass * impact <= cost) return new RamOutcome(false, 0, impact);
        var kept = SpeedThrough(speed, cost, mass);
        return new RamOutcome(true, kept, speed - kept);
    }

    /// <summary>The speed left after going through: <paramref name="speed"/> less what the hole took (<paramref name="cost"/> / <paramref name="mass"/>), never below 0.</summary>
    public static float SpeedThrough(float speed, float cost, float mass) => MathF.Max(0, speed - cost / MathF.Max(mass, 1));
}
