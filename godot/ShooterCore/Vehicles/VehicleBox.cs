using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// A ray hit on a vehicle's hit box, in the vehicle's own frame: the node frame of the vehicle (origin on the ground under its centre,
/// x right, y up, z back, forward is -Z). <see cref="Local"/> and <see cref="LocalNormal"/> let a client pin a decal to the body, so it
/// rides the vehicle as it moves.
/// </summary>
public readonly record struct VehicleBoxHit(float Distance, Vector3 Local, Vector3 LocalNormal);

/// <summary>
/// One box of a vehicle's hull in the vehicle's node frame (origin on the ground under its centre, x right, y up, z back, forward is -Z), metres: centred
/// (<see cref="Cx"/>, <see cref="Cz"/>) on the ground plane, <see cref="HalfWidth"/> either side across and <see cref="HalfLength"/> along, from height
/// <see cref="Y0"/> to <see cref="Y1"/>. vehicles.json <c>hull</c> lists them as [cx, cz, half_width, half_length, y0, y1].
/// </summary>
public readonly record struct HullBox(float Cx, float Cz, float HalfWidth, float HalfLength, float Y0, float Y1)
{
    public Vector3 Min => new(Cx - HalfWidth, Y0, Cz - HalfLength);
    public Vector3 Max => new(Cx + HalfWidth, Y1, Cz + HalfLength);
    /// <summary>The single upright box a vehicle without a <c>hull</c> list has: over its footprint, from the ground to <paramref name="height"/>.</summary>
    public static HullBox Upright(float halfWidth, float halfLength, float height) => new(0, 0, halfWidth, halfLength, 0, height);
}

/// <summary>
/// The hit volume of a vehicle (also its Body shape, ADR 0028): an upright box over its footprint, from the ground (y 0) to <c>height</c>, with the half extents of
/// vehicles.json. The server's physics box and the client's bullet holes both use these numbers, so a ray that the server stops on a car
/// is the same ray the client draws the hole on. A vehicle with a <c>hull</c> list (the utility helicopter: cabin, tail boom and fin) has several boxes instead, in its
/// node frame, and they tilt with its pitch and roll as well as its yaw (the overloads taking <see cref="HullBox"/>es).
/// </summary>
public static class VehicleBox
{
    /// <summary>The ray against a vehicle's hit volume, whichever it has: the upright box (yaw only) or the tilted boxes of its <see cref="VehicleDef.Hull"/>.</summary>
    public static VehicleBoxHit? Ray(Vector3 origin, Vector3 dir, float maxDistance, Vector3 feet, float yaw, float pitch, float roll, VehicleDef def) =>
        def.Tilts ? Ray(origin, dir, maxDistance, feet, yaw, pitch, roll, def.HullBoxes) : Ray(origin, dir, maxDistance, feet, yaw, def.HalfWidth, def.HalfLength, def.Height);

    /// <summary>Where a ray in the node frame leaves a vehicle's hit volume (<see cref="ExitDistance(Vector3, Vector3, float, float, float)"/>, or the run of its hull's boxes).</summary>
    public static float ExitDistance(Vector3 origin, Vector3 dir, VehicleDef def) =>
        def.Tilts ? ExitDistance(origin, dir, def.HullBoxes) : ExitDistance(origin, dir, def.HalfWidth, def.HalfLength, def.Height);

    /// <summary>
    /// <see cref="Ray(Vector3, Vector3, float, Vector3, float, float, float, float)"/> for several boxes in the node frame: the vehicle's attitude (<paramref name="yaw"/>, <paramref name="pitch"/>, <paramref name="roll"/>, as
    /// <see cref="FlightMath.Rotate"/>) turns the boxes. The nearest entry of any box; null when the origin is inside one (it sits in the vehicle), or the ray misses them all.
    /// The hit and its normal are in the node frame.
    /// </summary>
    public static VehicleBoxHit? Ray(Vector3 origin, Vector3 dir, float maxDistance, Vector3 feet, float yaw, float pitch, float roll, ReadOnlySpan<HullBox> boxes)
    {
        var len = dir.Length();
        if (len < 1e-6f) return null;
        dir /= len;
        return RayLocal(FlightMath.Unrotate(origin - feet, yaw, pitch, roll), FlightMath.Unrotate(dir, yaw, pitch, roll), maxDistance, boxes);
    }

    /// <summary><see cref="Ray(Vector3, Vector3, float, Vector3, float, float, float, ReadOnlySpan{HullBox})"/> for a ray already in the node frame (<paramref name="dir"/> a unit vector).</summary>
    public static VehicleBoxHit? RayLocal(Vector3 lo, Vector3 ld, float maxDistance, ReadOnlySpan<HullBox> boxes)
    {
        foreach (var b in boxes)
            if (lo.X >= b.Cx - b.HalfWidth && lo.X <= b.Cx + b.HalfWidth && lo.Y >= b.Y0 && lo.Y <= b.Y1 && lo.Z >= b.Cz - b.HalfLength && lo.Z <= b.Cz + b.HalfLength) return null; // inside the hull
        VehicleBoxHit? best = null;
        foreach (var b in boxes)
            if (Slab(lo, ld, b.Min, b.Max, out var tEnter, out _, out var axis, out var sign) && tEnter >= 0 && tEnter <= maxDistance && (best == null || tEnter < best.Value.Distance))
                best = new VehicleBoxHit(tEnter, lo + ld * tEnter, axis switch { 0 => new Vector3(sign, 0, 0), 1 => new Vector3(0, sign, 0), _ => new Vector3(0, 0, sign) });
        return best;
    }

    /// <summary>
    /// Metres along a ray (in the node frame, <paramref name="dir"/> a unit vector) that goes into the hull at its first box to where it leaves the hull: the end of the run of boxes the ray
    /// crosses one after another (boxes that touch or overlap along the ray are one volume). For a ray that misses every box the number means nothing.
    /// </summary>
    public static float ExitDistance(Vector3 origin, Vector3 dir, ReadOnlySpan<HullBox> boxes)
    {
        float start = float.PositiveInfinity, end = float.NegativeInfinity;
        for (var pass = 0; pass <= boxes.Length; pass++) // grow the first run to every box that begins inside it
        {
            var grew = false;
            foreach (var b in boxes)
            {
                if (!Slab(origin, dir, b.Min, b.Max, out var t0, out var t1, out _, out _) || t1 < 0) continue;
                if (float.IsPositiveInfinity(start)) { start = t0; end = t1; grew = true; continue; }
                if (t0 < start && t1 >= start - 1e-3f) { start = t0; grew = true; }
                if (t0 <= end + 1e-3f && t1 > end) { end = t1; grew = true; }
            }
            if (!grew) break;
        }
        return end;
    }

    /// <summary>Distance from a point in the node frame to the nearest box of the hull (0 inside one).</summary>
    public static float DistanceToHull(Vector3 local, ReadOnlySpan<HullBox> boxes)
    {
        var best = float.PositiveInfinity;
        foreach (var b in boxes)
        {
            var dx = MathF.Max(MathF.Max(b.Cx - b.HalfWidth - local.X, local.X - (b.Cx + b.HalfWidth)), 0);
            var dy = MathF.Max(MathF.Max(b.Y0 - local.Y, local.Y - b.Y1), 0);
            var dz = MathF.Max(MathF.Max(b.Cz - b.HalfLength - local.Z, local.Z - (b.Cz + b.HalfLength)), 0);
            best = MathF.Min(best, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        return best;
    }

    /// <summary>The ray against one axis-aligned box: the distances it enters and leaves, the axis (0 x, 1 y, 2 z) and the sign of the normal of the face it enters by. False on a miss.</summary>
    static bool Slab(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float tEnter, out float tExit, out int enterAxis, out float enterSign)
    {
        tEnter = float.NegativeInfinity; tExit = float.PositiveInfinity; enterAxis = -1; enterSign = 0;
        for (var axis = 0; axis < 3; axis++)
        {
            var p = Get(o, axis); var dd = Get(d, axis); var lmin = Get(min, axis); var lmax = Get(max, axis);
            if (MathF.Abs(dd) < 1e-7f)
            {
                if (p < lmin || p > lmax) return false; // parallel to this slab and outside it
                continue;
            }
            var t1 = (lmin - p) / dd; var t2 = (lmax - p) / dd;
            var near = MathF.Min(t1, t2); var far = MathF.Max(t1, t2);
            if (near > tEnter) { tEnter = near; enterAxis = axis; enterSign = dd > 0 ? -1f : 1f; }
            tExit = MathF.Min(tExit, far);
            if (tEnter > tExit) return false;
        }
        return tExit >= 0 && enterAxis >= 0;
    }

    /// <summary>
    /// Metres from <paramref name="origin"/> along <paramref name="dir"/> (any length; only its direction counts) to where the ray first
    /// enters the box, or null: a miss, a hit beyond <paramref name="maxDistance"/>, or an origin already inside the box (as a physics
    /// ray reports nothing from inside a shape).
    /// </summary>
    public static VehicleBoxHit? Ray(Vector3 origin, Vector3 dir, float maxDistance, Vector3 feet, float yaw, float halfWidth, float halfLength, float height)
    {
        var len = dir.Length();
        if (len < 1e-6f) return null;
        dir /= len;
        // node frame: x = right, z = back (the vehicle's local +Z), as Godot's yaw rotation maps them
        var s = MathF.Sin(yaw); var c = MathF.Cos(yaw);
        var right = new Vector3(c, 0, -s);
        var back = new Vector3(s, 0, c);
        var o = origin - feet;
        var lo = new Vector3(Vector3.Dot(o, right), o.Y, Vector3.Dot(o, back));
        var ld = new Vector3(Vector3.Dot(dir, right), dir.Y, Vector3.Dot(dir, back));
        var min = new Vector3(-halfWidth, 0, -halfLength);
        var max = new Vector3(halfWidth, height, halfLength);

        var tEnter = float.NegativeInfinity; var tExit = float.PositiveInfinity;
        var enterAxis = -1; var enterSign = 0f;
        for (var axis = 0; axis < 3; axis++)
        {
            var p = Get(lo, axis); var d = Get(ld, axis); var lmin = Get(min, axis); var lmax = Get(max, axis);
            if (MathF.Abs(d) < 1e-7f)
            {
                if (p < lmin || p > lmax) return null; // parallel to this slab and outside it
                continue;
            }
            var t1 = (lmin - p) / d; var t2 = (lmax - p) / d;
            var near = MathF.Min(t1, t2); var far = MathF.Max(t1, t2);
            if (near > tEnter) { tEnter = near; enterAxis = axis; enterSign = d > 0 ? -1f : 1f; }
            tExit = MathF.Min(tExit, far);
            if (tEnter > tExit) return null;
        }
        if (tExit < 0 || enterAxis < 0 || tEnter < 0) return null; // behind the origin, or the origin is inside the box
        if (tEnter > maxDistance) return null;

        var local = lo + ld * tEnter;
        var ln = enterAxis switch { 0 => new Vector3(enterSign, 0, 0), 1 => new Vector3(0, enterSign, 0), _ => new Vector3(0, 0, enterSign) };
        return new VehicleBoxHit(tEnter, local, ln);
    }

    /// <summary>
    /// Metres along a ray that crosses the box to where it leaves it. The ray is in the box's own frame (<see cref="GlassRules.ToVehicle"/>)
    /// and <paramref name="dir"/> is a unit vector. For a ray that misses the box the number means nothing.
    /// </summary>
    public static float ExitDistance(Vector3 origin, Vector3 dir, float halfWidth, float halfLength, float height)
    {
        var min = new Vector3(-halfWidth, 0, -halfLength);
        var max = new Vector3(halfWidth, height, halfLength);
        var tExit = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var d = Get(dir, axis);
            if (MathF.Abs(d) < 1e-7f) continue;
            var p = Get(origin, axis);
            tExit = MathF.Min(tExit, MathF.Max((Get(min, axis) - p) / d, (Get(max, axis) - p) / d));
        }
        return tExit;
    }

    static float Get(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
