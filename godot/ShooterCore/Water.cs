using System.Numerics;
using System.Text.Json;

namespace Suchohrad.ShooterCore;

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

    public IReadOnlyList<WaterBody> Bodies { get; }

    public Water(IReadOnlyList<WaterBody> bodies) => Bodies = bodies;

    /// <summary>The body with the highest surface containing (x, z), or null when the point is dry.</summary>
    public WaterBody? BodyAt(float x, float z)
    {
        WaterBody? best = null;
        foreach (var b in Bodies)
            if ((best is null || b.SurfaceY > best.SurfaceY) && b.Contains(x, z)) best = b;
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
        s.HeadUnder = feet.Y + MovementStep.EyeHeight(s.Crouching) < b.SurfaceY;
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
