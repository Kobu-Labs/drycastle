using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>Water constants from the "swimming and submerging" ticket (starting values, to be playtested).</summary>
public static class WaterConsts
{
    /// <summary>Column depth above which the player swims (below it: wading).</summary>
    public const float SwimDepth = 1.1f;
    /// <summary>Wading (shallow water, feet in water) walks at this share of normal speed.</summary>
    public const float WadeSpeedScale = 0.6f;
    public const float SwimSpeed = 3f;
    public const float SwimSprintSpeed = 4.5f;
    /// <summary>Vertical swim speed: jump rises, crouch dives (m/s).</summary>
    public const float SwimVerticalSpeed = 2f;
    /// <summary>How fast the vertical swim speed eases toward its target (m/s per s).</summary>
    public const float SwimVerticalAccel = 20f;
}

/// <summary>
/// One water body: a water parcel of the base map. Rings are in Godot ground axes (x east, y = -north), outer ring
/// first then holes. The surface is flat at <see cref="SurfaceY"/>; the bed is flat at <see cref="BedY"/> (the lowest
/// terrain sampled inside the body), so depth is the same everywhere in the body (an approximation).
/// </summary>
public sealed class WaterBody
{
    public required string Id { get; init; }
    public required Vector2[][] Rings { get; init; }
    public required float SurfaceY { get; init; }
    public required float BedY { get; init; }
    public float MinX { get; init; }
    public float MinZ { get; init; }
    public float MaxX { get; init; }
    public float MaxZ { get; init; }

    public float Depth => Math.Max(0f, SurfaceY - BedY);

    /// <summary>Area of the outer ring, m² (the shoelace formula; holes are not taken off).</summary>
    public float Area
    {
        get
        {
            var r = Rings[0];
            double a = 0;
            for (int i = 0, j = r.Length - 1; i < r.Length; j = i++) a += (double)r[j].X * r[i].Y - (double)r[i].X * r[j].Y;
            return (float)Math.Abs(a / 2);
        }
    }

    /// <summary>Distance (m) from (x, z) to the nearest outline of this body (outer ring or a hole). Sign-free: inside or outside.</summary>
    public float ShoreDistance(float x, float z)
    {
        var best = float.MaxValue;
        var p = new Vector2(x, z);
        foreach (var ring in Rings)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                best = MathF.Min(best, SegmentDistance(p, ring[j], ring[i]));
        return best;
    }

    /// <summary>Distance (m) from (x, z) to the bounding box of this body; 0 inside it.</summary>
    public float BoxDistance(float x, float z)
    {
        var dx = MathF.Max(MathF.Max(MinX - x, 0f), x - MaxX);
        var dz = MathF.Max(MathF.Max(MinZ - z, 0f), z - MaxZ);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    internal static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var d = b - a;
        var len2 = d.LengthSquared();
        var t = len2 <= 0 ? 0f : Math.Clamp(Vector2.Dot(p - a, d) / len2, 0f, 1f);
        return Vector2.Distance(p, a + d * t);
    }

    /// <summary>Even-odd point in polygon over all rings, for a ground point (x, z).</summary>
    public bool Contains(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;
        var inside = false;
        foreach (var ring in Rings)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                if ((a.Y > z) != (b.Y > z) && x < (b.X - a.X) * (z - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
        return inside;
    }
}

/// <summary>
/// The water of the current map (water.json), Godot-free and scene-free so prediction and the server share one answer.
/// Queries take Godot ground coordinates: x east, z = -north (the same axes as the movement state).
/// </summary>
public sealed class Water
{
    public static readonly Water Empty = new(Array.Empty<WaterBody>());

    /// <summary>Stored as an array: the queries run twice per player per tick and an array loop does not box an enumerator.</summary>
    readonly WaterBody[] _bodies;

    public IReadOnlyList<WaterBody> Bodies => _bodies;

    public Water(IReadOnlyList<WaterBody> bodies) => _bodies = bodies.ToArray();

    /// <summary>The body with the highest surface containing (x, z), or null when the point is dry.</summary>
    public WaterBody? BodyAt(float x, float z)
    {
        WaterBody? best = null;
        var bodies = _bodies;
        for (var i = 0; i < bodies.Length; i++)
        {
            var b = bodies[i];
            if ((best is null || b.SurfaceY > best.SurfaceY) && b.Contains(x, z)) best = b;
        }
        return best;
    }

    /// <summary>Distance (m) from (x, z) to the nearest shoreline of any body; float.MaxValue with no water.</summary>
    public float ShoreDistance(float x, float z)
    {
        var best = float.MaxValue;
        var bodies = _bodies;
        for (var i = 0; i < bodies.Length; i++)
        {
            var b = bodies[i];
            if (b.BoxDistance(x, z) >= best) continue; // cannot beat the best so far
            best = MathF.Min(best, b.ShoreDistance(x, z));
        }
        return best;
    }

    /// <summary>Surface height of the water at (x, z), or NaN when the point is not over water.</summary>
    public float SurfaceY(float x, float z) => BodyAt(x, z)?.SurfaceY ?? float.NaN;

    /// <summary>Water depth (surface minus bed) at (x, z); 0 when the point is not over water.</summary>
    public float Depth(float x, float z) => BodyAt(x, z)?.Depth ?? 0f;

    /// <summary>
    /// Water depth for feet at <paramref name="feet"/>: the column depth, but only when the feet are below the
    /// surface. A player standing on a bridge over the lake, or in the air above it, gets 0.
    /// </summary>
    public float DepthAt(Vector3 feet)
    {
        var b = BodyAt(feet.X, feet.Z);
        return b is not null && feet.Y < b.SurfaceY ? b.Depth : 0f;
    }

    /// <summary>
    /// Fill the water fields of a movement state for the feet at <c>s.Position</c> (call before the step).
    /// <see cref="MoveState.WaterDepth"/> is the depth at the feet; <see cref="MoveState.HeadUnder"/> is set when the
    /// eyes are below the surface.
    /// </summary>
    public void Sample(ref MoveState s)
    {
        var feet = s.Position;
        var b = BodyAt(feet.X, feet.Z);
        if (b is null || feet.Y >= b.SurfaceY)
        {
            s.WaterDepth = 0f;
            s.WaterSurfaceY = float.NaN;
            s.HeadUnder = false;
            return;
        }
        s.WaterDepth = b.Depth;
        s.WaterSurfaceY = b.SurfaceY;
        s.HeadUnder = feet.Y + MovementStep.EyeHeight(s.Stance) < b.SurfaceY;
    }

    /// <summary>
    /// Parse water.json: <c>{"version": 1, "bodies": [{"id", "polygon": {"coordinates": rings}, "surface_y", "bed_y"}]}</c>.
    /// Polygon rings are local map metres (x east, y north) and are converted to Godot ground axes here.
    /// </summary>
    public static Water Parse(string json, string name = "water.json")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new DataFileException($"{name}: not JSON: {e.Message}", e); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
                throw new DataFileException($"{name}: unsupported or missing version (expected 1)");
            if (!root.TryGetProperty("bodies", out var bodies) || bodies.ValueKind != JsonValueKind.Array)
                throw new DataFileException($"{name}: missing bodies array");
            var list = new List<WaterBody>();
            foreach (var b in bodies.EnumerateArray())
            {
                var id = b.GetProperty("id").GetString() ?? throw new DataFileException($"{name}: body without id");
                var rings = b.GetProperty("polygon").GetProperty("coordinates").EnumerateArray()
                    .Select(r => r.EnumerateArray().Select(c => new Vector2(c[0].GetSingle(), -c[1].GetSingle())).ToArray())
                    .ToArray();
                if (rings.Length == 0 || rings[0].Length < 3) throw new DataFileException($"{name}: body {id} has no outer ring");
                var all = rings.SelectMany(r => r).ToArray();
                list.Add(new WaterBody
                {
                    Id = id,
                    Rings = rings,
                    SurfaceY = b.GetProperty("surface_y").GetSingle(),
                    BedY = b.GetProperty("bed_y").GetSingle(),
                    MinX = all.Min(p => p.X), MaxX = all.Max(p => p.X),
                    MinZ = all.Min(p => p.Y), MaxZ = all.Max(p => p.Y),
                });
            }
            return new Water(list);
        }
    }
}

/// <summary>Breath (air) and drowning. Air is tracked as seconds spent since the head last surfaced.</summary>
public static class Breath
{
    /// <summary>Seconds of air with the head under water.</summary>
    public const float Capacity = 20f;
    /// <summary>Surfacing takes air back this many times faster than it is spent (a full refill takes Capacity / this, 4 s).</summary>
    public const float RefillRate = 5f;
    /// <summary>Drowning damage per second once the air is gone.</summary>
    public const float DrowningDamagePerSecond = 12f;

    /// <summary>Seconds of air left (Capacity while dry).</summary>
    public static float AirLeft(in MoveState s) => Math.Max(0f, Capacity - s.AirSpent);

    /// <summary>
    /// Advance the air by one tick using <see cref="MoveState.HeadUnder"/> (set by <see cref="Water.Sample"/> this tick).
    /// Returns the drowning damage for the tick (0 while air is left).
    /// </summary>
    public static float Tick(ref MoveState s, float dt)
    {
        if (!s.HeadUnder)
        {
            s.AirSpent = Math.Max(0f, s.AirSpent - RefillRate * dt);
            return 0f;
        }
        var hadAir = s.AirSpent < Capacity;
        s.AirSpent = Math.Min(Capacity, s.AirSpent + dt);
        return hadAir ? 0f : DrowningDamagePerSecond * dt;
    }
}

/// <summary>
/// Where boats (and the amphibious transport on the water) are placed at match start. Pure: candidates are grid points inside
/// water bodies, in a band of shoreline distances so the hull is clear of the bank and close enough to walk to.
/// Too little water (no candidate in the band) falls back to the interior of the largest body, so a boat still spawns.
/// </summary>
public static class WaterSpawns
{
    /// <summary>Boat candidates lie this far (m) from the shoreline or more, so a boat's box (about 2.6 m half length) stays off the bank.</summary>
    public const float ShoreMinDistance = 4f;
    /// <summary>...and no further than this from it.</summary>
    public const float ShoreMaxDistance = 15f;
    /// <summary>Grid spacing of the candidate sampling, m.</summary>
    public const float Grid = 3f;
    /// <summary>A body shallower than this (m) gives no shore candidates: a boat would touch the bed.</summary>
    public const float MinDepth = 1.5f;

    /// <summary>A placement point: ground x and z (Godot axes) and the water surface height the origin rides.</summary>
    public readonly record struct Spot(float X, float Z, float SurfaceY);

    /// <summary>Shore-band candidates over every body at least <see cref="MinDepth"/> deep.</summary>
    public static List<Spot> ShoreCandidates(Water water)
    {
        var list = new List<Spot>();
        foreach (var b in water.Bodies)
        {
            if (b.Depth < MinDepth) continue;
            foreach (var p in Grid2D(b))
            {
                var d = b.ShoreDistance(p.X, p.Y);
                if (d >= ShoreMinDistance && d <= ShoreMaxDistance) list.Add(new Spot(p.X, p.Y, b.SurfaceY));
            }
        }
        return list;
    }

    /// <summary>
    /// The fallback when there are too few shore candidates: the largest body (by area) that is at least <see cref="MinDepth"/> deep,
    /// all its grid points, furthest from the shore first (the band does not apply: a small pond still gets its middle). Empty with no water at all.
    /// </summary>
    public static List<Spot> FallbackCandidates(Water water)
    {
        var body = water.Bodies.Where(b => b.Depth >= MinDepth).OrderByDescending(b => b.Area).FirstOrDefault()
                   ?? water.Bodies.OrderByDescending(b => b.Area).FirstOrDefault();
        if (body == null) return new List<Spot>();
        return Grid2D(body)
            .Select(p => (P: p, D: body.ShoreDistance(p.X, p.Y)))
            .OrderByDescending(x => x.D)
            .Select(x => new Spot(x.P.X, x.P.Y, body.SurfaceY))
            .ToList();
    }

    static IEnumerable<Vector2> Grid2D(WaterBody b)
    {
        for (var x = b.MinX + Grid / 2; x < b.MaxX; x += Grid)
            for (var z = b.MinZ + Grid / 2; z < b.MaxZ; z += Grid)
                if (b.Contains(x, z)) yield return new Vector2(x, z);
    }
}
