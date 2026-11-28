using System.Numerics;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>
/// vehicles.json "cargo_volume" of a type (the Cartel van's bay, #495): an upright box in the vehicle frame (forward -Z, origin on the ground, as the seats and the hood) that takes loads
/// thrown or put into it (Duffels), and how many it takes. It is not drawn and stops nothing: what reaches it is the caller's rule (<see cref="CargoVolume.Contains"/>).
/// The Delivery rework (docs/cartel.md "Cargo vehicles") gave every cargo type its own: the truck's open bed (<see cref="FromTop"/>), the car's and the Golf's boot, the van's bay. Beside the box it says where
/// each loaded Duffel is drawn (<see cref="Slots"/>), where a player stands to open, load and take out (<see cref="Access"/>) and what swings open (<see cref="Doors"/>). The Duffels' side of it is <c>VehicleCargo</c> (ShooterCore/Cartel).
/// </summary>
public class VehicleCargoVolumeDef
{
    /// <summary>The box's centre [x, y, z] in the vehicle frame, metres.</summary>
    public float[] Centre { get; set; } = { 0, 0, 0 };
    /// <summary>The box's size [width (x), height (y), length (z)], metres.</summary>
    public float[] Size { get; set; } = { 1, 1, 1 };
    /// <summary>How many loads it holds at once, 1..15 (a loaded Duffel's slot rides in four bits of its entity state).</summary>
    public int Capacity { get; set; } = 1;
    /// <summary>
    /// True for an open bed (the truck): a load enters only coming down through the box's top face (the boards stop what comes from the side, it falls beside). False: it enters from any side the caller lets
    /// through (a closed boot or bay: its doors, <see cref="Doors"/>).
    /// </summary>
    public bool FromTop { get; set; }
    /// <summary>
    /// Where each loaded Duffel is drawn, one per load that fits (empty: not drawn): [x, y, z, yaw_deg] in the vehicle frame, (x, y, z) the centre of the Duffel's base resting on the floor, the yaw about Y turning its
    /// long side (the prop's x). Measured from the model: inside the box, on its floor, clear of each other (VehicleContractTests).
    /// </summary>
    public List<float[]> Slots { get; set; } = new();
    /// <summary>[x, y, z] on the ground just behind the cargo end, vehicle frame: where a player stands to open, load and take out (<see cref="AccessRadiusM"/> round it). Null: no such place.</summary>
    public float[]? Access { get; set; }
    /// <summary>How far from <see cref="Access"/> a player still opens, loads and takes out, metres.</summary>
    public float AccessRadiusM { get; set; } = 1.5f;
    /// <summary>What the doors are called in the prompt ("boot", "hatch", "rear doors": "Open the boot"); the cargo area's name when empty.</summary>
    public string Label { get; set; } = "";
    /// <summary>The most loads a type can hold: the slot index is four bits of a loaded Duffel's entity state.</summary>
    public const int MaxCapacity = 15;
    /// <summary>The nodes of the model that swing open (a boot lid, a hatch, a van's rear doors; the client's <c>CargoDoorsView</c>). Empty: the cargo area is always open (the truck's bed).</summary>
    public List<VehicleCargoDoorDef> Doors { get; set; } = new();

    /// <summary>The slot's place and turn (<see cref="Slots"/>), or null when the type has fewer.</summary>
    public CargoSlot? Slot(int i) => i >= 0 && i < Slots.Count && Slots[i] is { Length: 4 } s ? new CargoSlot(new Vector3(s[0], s[1], s[2]), s[3]) : null;

    /// <summary>Null when the volume is well formed and inside the type's hit box (<paramref name="v"/>), else what is wrong.</summary>
    public string? Problem(VehicleDef v)
    {
        if (Centre is not { Length: 3 } || Size is not { Length: 3 } || Centre.Any(x => !float.IsFinite(x)) || Size.Any(x => !(x > 0) || !float.IsFinite(x))) return "centre is [x, y, z] and size [width, height, length], all positive";
        if (Capacity is < 1 or > MaxCapacity) return $"capacity is 1..{MaxCapacity}";
        const float eps = 0.002f;
        if (MathF.Abs(Centre[0]) + Size[0] / 2 > v.HalfWidth + eps || MathF.Abs(Centre[2]) + Size[2] / 2 > v.HalfLength + eps || Centre[1] - Size[1] / 2 < -eps || Centre[1] + Size[1] / 2 > v.Height + eps)
            return "the volume must lie inside the hit box (half_width, half_length, height)";
        if (Slots.Count != 0 && Slots.Count != Capacity) return $"slots: {Slots.Count} for a capacity of {Capacity} (one [x, y, z, yaw_deg] per load, or none)";
        for (var i = 0; i < Slots.Count; i++)
        {
            if (Slots[i] is not { Length: 4 } s || s.Any(x => !float.IsFinite(x))) return $"slots[{i}] is [x, y, z, yaw_deg]";
            if (MathF.Abs(s[0] - Centre[0]) > Size[0] / 2 + eps || MathF.Abs(s[2] - Centre[2]) > Size[2] / 2 + eps || s[1] < Centre[1] - Size[1] / 2 - eps || s[1] > Centre[1] + Size[1] / 2 + eps)
                return $"slots[{i}] lies outside the volume";
        }
        if (Access != null && (Access.Length != 3 || Access.Any(x => !float.IsFinite(x)))) return "access is [x, y, z]";
        if (!(AccessRadiusM > 0) || !float.IsFinite(AccessRadiusM)) return "access_radius_m must be positive";
        foreach (var d in Doors) if (d.Problem() is { } door) return "doors: " + door;
        return null;
    }
}

/// <summary>One swinging cargo door of a type's model (<see cref="VehicleCargoVolumeDef.Doors"/>): the node, the axis it turns about, how far it opens.</summary>
public class VehicleCargoDoorDef
{
    /// <summary>The model node (found by name under the hull): its origin is on the hinge line.</summary>
    public string Node { get; set; } = "";
    /// <summary>The axis in the node's own frame (x right, y up, -z forward). Its sign is chosen so that a positive <see cref="OpenDeg"/> opens the door outward or up.</summary>
    public float[] Axis { get; set; } = { 1, 0, 0 };
    /// <summary>How far the door turns open, degrees, 1..170.</summary>
    public float OpenDeg { get; set; } = 80;

    /// <summary>Null when the door is well formed, else what is wrong.</summary>
    public string? Problem()
    {
        if (Node.Length == 0) return "a door needs a node";
        if (Axis is not { Length: 3 } || Axis.Any(x => !float.IsFinite(x)) || Axis.All(x => x == 0)) return $"{Node}: axis is [x, y, z], not all zero";
        if (!(OpenDeg is >= 1 and <= 170)) return $"{Node}: open_deg is 1..170";
        return null;
    }
}

/// <summary>A place a loaded Duffel is drawn (<see cref="VehicleCargoVolumeDef.Slots"/>): the centre of its base on the floor, vehicle frame, and its turn about Y in degrees.</summary>
public readonly record struct CargoSlot(Vector3 Position, float YawDeg);

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

    /// <summary>
    /// Where the segment from <paramref name="a"/> to <paramref name="b"/> (the vehicle's own frame: the world segment unrotated about its feet) first meets the box grown by <paramref name="margin"/>: the entry
    /// point and the way in. A segment that starts inside enters at its start. <paramref name="fromTop"/> is true when it comes down through the top face (or starts inside and moves down). The returned
    /// <paramref name="normal"/> is the horizontal way out a refused load is put aside by: the face entered from the side, or, for a load that meets the top (or the floor), the nearest side face (the point
    /// moves onto it), so a refused load never hangs on the top of the box.
    /// </summary>
    public static bool Enter(VehicleCargoVolumeDef v, Vector3 a, Vector3 b, float margin, out Vector3 at, out Vector3 normal, out bool fromTop)
    {
        var centre = new Vector3(v.Centre[0], v.Centre[1], v.Centre[2]);
        var half = new Vector3(v.Size[0], v.Size[1], v.Size[2]) / 2 + new Vector3(margin);
        var rel = a - centre;
        var d = b - a;
        float t = 0; var axis = -1; var sign = 0f;
        if (!(MathF.Abs(rel.X) <= half.X && MathF.Abs(rel.Y) <= half.Y && MathF.Abs(rel.Z) <= half.Z))
        {
            float near = float.NegativeInfinity, far = float.PositiveInfinity;
            for (var i = 0; i < 3; i++)
            {
                var p = Axis(rel, i); var dd = Axis(d, i); var h = Axis(half, i);
                if (MathF.Abs(dd) < 1e-9f) { if (MathF.Abs(p) > h) { at = default; normal = default; fromTop = false; return false; } continue; }
                var t0 = (-h - p) / dd; var t1 = (h - p) / dd;
                var enter = MathF.Min(t0, t1); var leave = MathF.Max(t0, t1);
                if (enter > near) { near = enter; axis = i; sign = dd > 0 ? -1f : 1f; }
                far = MathF.Min(far, leave);
            }
            if (near > far || near > 1f || far < 0f) { at = default; normal = default; fromTop = false; return false; }
            t = MathF.Max(near, 0f);
        }
        var local = a + d * t;
        var r = local - centre;
        fromTop = axis == 1 ? sign > 0 : axis < 0 && d.Y < 0;
        if (axis == 0 || axis == 2)
        {
            normal = axis == 0 ? new Vector3(sign, 0, 0) : new Vector3(0, 0, sign);
        }
        else
        {
            // the top or the floor (or inside already): out by the nearest side
            var toX = half.X - MathF.Abs(r.X); var toZ = half.Z - MathF.Abs(r.Z);
            if (toX <= toZ) { var s = r.X < 0 ? -1f : 1f; normal = new Vector3(s, 0, 0); local.X = centre.X + s * half.X; }
            else { var s = r.Z < 0 ? -1f : 1f; normal = new Vector3(0, 0, s); local.Z = centre.Z + s * half.Z; }
        }
        at = local;
        return true;
    }

    static float Axis(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    /// <summary>
    /// Whether a player standing at the world point <paramref name="point"/> is within the type's access radius of its access point (a vehicle placed as in <see cref="Contains"/>): the flat distance,
    /// the feet within <see cref="AccessBandM"/> of the access point's height. Null when the type has no access point, else the flat distance in metres (the nearest vehicle wins).
    /// </summary>
    public static float? AccessDistance(VehicleCargoVolumeDef v, Vector3 feet, float yaw, float pitch, float roll, Vector3 point)
    {
        if (v.Access is not { Length: 3 } a) return null;
        var at = feet + FlightMath.Rotate(new Vector3(a[0], a[1], a[2]), yaw, pitch, roll);
        if (MathF.Abs(point.Y - at.Y) > AccessBandM) return null;
        var d = MathF.Sqrt((point.X - at.X) * (point.X - at.X) + (point.Z - at.Z) * (point.Z - at.Z));
        return d <= v.AccessRadiusM ? d : null;
    }

    /// <summary>How far above or below the access point (metres) a player's feet still count as standing there: not on a roof, not in a pit.</summary>
    public const float AccessBandM = 2f;

    /// <summary>The volume's centre in the world, for a vehicle placed as in <see cref="Contains"/> (where a load is drawn going in, where a marker points).</summary>
    public static Vector3 Centre(VehicleCargoVolumeDef v, Vector3 feet, float yaw, float pitch, float roll) =>
        feet + FlightMath.Rotate(new Vector3(v.Centre[0], v.Centre[1], v.Centre[2]), yaw, pitch, roll);
}
