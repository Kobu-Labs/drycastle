using System.Numerics;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>
/// vehicles.json "cargo_volume" of a type (the Cartel van's bay, #495): an upright box in the vehicle frame (forward -Z, origin on the ground, as the seats and the hood) that takes loads
/// thrown or put into it (Duffels), and how many it takes. It is not drawn and stops nothing: what reaches it is the caller's rule (<see cref="CargoVolume.Contains"/>).
/// </summary>
public class VehicleCargoVolumeDef
{
    /// <summary>The box's centre [x, y, z] in the vehicle frame, metres.</summary>
    public float[] Centre { get; set; } = { 0, 0, 0 };
    /// <summary>The box's size [width (x), height (y), length (z)], metres.</summary>
    public float[] Size { get; set; } = { 1, 1, 1 };
    /// <summary>How many loads it holds at once (the loader counts them: a Heist's van bay).</summary>
    public int Capacity { get; set; } = 1;

    /// <summary>Null when the volume is well formed and inside the type's hit box (<paramref name="v"/>), else what is wrong.</summary>
    public string? Problem(VehicleDef v)
    {
        if (Centre is not { Length: 3 } || Size is not { Length: 3 } || Centre.Any(x => !float.IsFinite(x)) || Size.Any(x => !(x > 0) || !float.IsFinite(x))) return "centre is [x, y, z] and size [width, height, length], all positive";
        if (Capacity is < 1 or > 255) return "capacity is 1..255";
        const float eps = 0.002f;
        if (MathF.Abs(Centre[0]) + Size[0] / 2 > v.HalfWidth + eps || MathF.Abs(Centre[2]) + Size[2] / 2 > v.HalfLength + eps || Centre[1] - Size[1] / 2 < -eps || Centre[1] + Size[1] / 2 > v.Height + eps)
            return "the volume must lie inside the hit box (half_width, half_length, height)";
        return null;
    }
}

/// <summary>Where a vehicle's cargo volume is (<see cref="VehicleCargoVolumeDef"/>): the one test of "is this point in vehicle X's cargo".</summary>
public static class CargoVolume
{
    /// <summary>
    /// Whether the world point <paramref name="point"/> is inside the cargo volume <paramref name="v"/> of a vehicle whose frame has its origin at <paramref name="feet"/>, turned by
    /// <paramref name="yaw"/>, <paramref name="pitch"/> and <paramref name="roll"/> (<see cref="FlightMath.Rotate"/>: a driven vehicle's slope). <paramref name="margin"/> metres grow the box
    /// on every side (a throw that lands against the doors still counts); 0 is the bay itself.
    /// </summary>
    public static bool Contains(VehicleCargoVolumeDef v, Vector3 feet, float yaw, float pitch, float roll, Vector3 point, float margin = 0)
    {
        var p = FlightMath.Unrotate(point - feet, yaw, pitch, roll);
        return MathF.Abs(p.X - v.Centre[0]) <= v.Size[0] / 2 + margin
            && MathF.Abs(p.Y - v.Centre[1]) <= v.Size[1] / 2 + margin
            && MathF.Abs(p.Z - v.Centre[2]) <= v.Size[2] / 2 + margin;
    }

    /// <summary>The volume's centre in the world, for a vehicle placed as in <see cref="Contains"/> (where a load is drawn going in, where a marker points).</summary>
    public static Vector3 Centre(VehicleCargoVolumeDef v, Vector3 feet, float yaw, float pitch, float roll) =>
        feet + FlightMath.Rotate(new Vector3(v.Centre[0], v.Centre[1], v.Centre[2]), yaw, pitch, roll);
}
