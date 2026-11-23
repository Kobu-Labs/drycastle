using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Movement;

namespace Drycastle.ShooterCore.Waters;

/// <summary>Water constants (starting values, to be playtested). Swimming is at the surface only (ADR 0023).</summary>
public static class WaterConsts
{
    /// <summary>Column depth above which the player swims (below it: wading).</summary>
    public const float SwimDepth = 1.1f;
    /// <summary>Swim speed (m/s). Sprint is off in deep water; the speed is the only swim penalty.</summary>
    public const float SwimSpeed = 3f;
    /// <summary>Depth at the ankles: wading is full speed up to here (the ease starts above it).</summary>
    public const float AnkleDepth = 0.2f;
    /// <summary>Depth at which the wading speed scale reaches <see cref="WadeSpeedFloor"/>; it blends into the swim speed by <see cref="SwimDepth"/>.</summary>
    public const float WadeDepth = 0.9f;
    /// <summary>Wading speed share of the land speed at <see cref="WadeDepth"/>.</summary>
    public const float WadeSpeedFloor = 0.6f;
    /// <summary>Eyes this far above the surface while swimming (m). The feet hang <c>MoveConsts.EyeHeight - this</c> under it.</summary>
    public const float SwimEyesAboveSurface = 0.4f;
    /// <summary>Eases the feet to their swim height: vertical speed per metre off the height (1/s).</summary>
    public const float SwimSettleRate = 8f;
    /// <summary>Cap on the vertical speed that eases the feet to the swim height (m/s).</summary>
    public const float SwimSettleMaxSpeed = 4f;
    /// <summary>Jump within this distance (m) of the shoreline of a deep body hops out onto the bank.</summary>
    public const float ShoreHopDistance = 1.5f;
    /// <summary>Upward speed of the hop out of the water (m/s): peaks about 1.5 m above the float height.</summary>
    public const float SwimHopSpeed = 7.9f;
}

/// <summary>
/// The depth of a water body on a grid (water.json version 2): cell centres at Origin + (i + 0.5) * CellM in local
/// metres (x east, y north), row 0 the southern row, 0 outside the outline. Depth is bilinear between cell centres.
/// </summary>
public sealed class DepthGrid
{
    public float OriginX { get; }
    public float OriginY { get; }
    public float CellM { get; }
    public int Cols { get; }
    public int Rows { get; }
    /// <summary>Row-major depths (m), <see cref="Rows"/> times <see cref="Cols"/> of them.</summary>
    public float[] Depths { get; }
    /// <summary>The deepest cell of the grid (m).</summary>
    public float MaxDepth { get; }

    public DepthGrid(float originX, float originY, float cellM, int cols, int rows, float[] depths)
    {
        if (cellM <= 0 || cols <= 0 || rows <= 0 || depths.Length != cols * rows)
            throw new ArgumentException("depth grid needs a positive cell size and rows * cols depths");
        OriginX = originX; OriginY = originY; CellM = cellM; Cols = cols; Rows = rows; Depths = depths;
        MaxDepth = depths.Max();
    }

    /// <summary>Depth (m) at local metres (x east, y north), bilinear between cell centres; 0 beyond the grid.</summary>
    public float DepthAt(float x, float y)
    {
        var gx = (x - OriginX) / CellM - 0.5f;
        var gy = (y - OriginY) / CellM - 0.5f;
        var i0 = (int)MathF.Floor(gx);
        var j0 = (int)MathF.Floor(gy);
        var fx = gx - i0;
        var fy = gy - j0;
        return At(j0, i0) * (1 - fx) * (1 - fy) + At(j0, i0 + 1) * fx * (1 - fy)
             + At(j0 + 1, i0) * (1 - fx) * fy + At(j0 + 1, i0 + 1) * fx * fy;
    }

    float At(int j, int i) => (uint)i < (uint)Cols && (uint)j < (uint)Rows ? Depths[j * Cols + i] : 0f;
}

/// <summary>
/// One water body: a water parcel or region of the base map. Rings are in Godot ground axes (x east, y = -north), outer
/// ring first then holes. The surface is flat at <see cref="SurfaceY"/>. The bed is <see cref="Grid"/> (version 2: the depth
/// varies over the body) or, without one (version 1), flat at <see cref="BedY"/>. <see cref="BedY"/> is always the lowest bed.
/// </summary>
public sealed class WaterBody
{
    public required string Id { get; init; }
    public required Vector2[][] Rings { get; init; }
    public required float SurfaceY { get; init; }
    public required float BedY { get; init; }
    /// <summary>The depth grid of version 2 water.json; null for a flat bed (version 1).</summary>
    public DepthGrid? Grid { get; init; }
    public float MinX { get; init; }
    public float MinZ { get; init; }
    public float MaxX { get; init; }
    public float MaxZ { get; init; }

    /// <summary>The deepest point of the body (surface minus the lowest bed).</summary>
    public float Depth => Math.Max(0f, SurfaceY - BedY);

    /// <summary>
    /// Water depth at ground point (x, z): the grid's depth there (bilinear) when the body has a grid, else the flat
    /// <see cref="Depth"/>. The grid is in local axes (y = -z).
    /// </summary>
    public float DepthAt(float x, float z) => Grid is null ? Depth : Grid.DepthAt(x, -z);

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

    /// <summary>Water depth at (x, z) (the bed's depth there); 0 when the point is not over water.</summary>
    public float Depth(float x, float z) => BodyAt(x, z)?.DepthAt(x, z) ?? 0f;

    /// <summary>
    /// Water depth for feet at <paramref name="feet"/>: the column depth, but only when the feet are below the
    /// surface. A player standing on a bridge over the lake, or in the air above it, gets 0.
    /// </summary>
    public float DepthAt(Vector3 feet)
    {
        var b = BodyAt(feet.X, feet.Z);
        return b is not null && feet.Y < b.SurfaceY ? b.DepthAt(feet.X, feet.Z) : 0f;
    }

    /// <summary>
    /// Fill the water fields of a movement state for the feet at <c>s.Position</c> (call before the step).
    /// <see cref="MoveState.WaterDepth"/> is the depth at the feet; <see cref="MoveState.ShoreDistance"/> is the distance
    /// to the body's shoreline, filled only in deep water (it decides whether a jump hops out).
    /// </summary>
    public void Sample(ref MoveState s)
    {
        var feet = s.Position;
        var b = BodyAt(feet.X, feet.Z);
        if (b is null || feet.Y >= b.SurfaceY)
        {
            s.WaterDepth = 0f;
            s.WaterSurfaceY = float.NaN;
            s.ShoreDistance = float.MaxValue;
            return;
        }
        s.WaterDepth = b.DepthAt(feet.X, feet.Z);
        s.WaterSurfaceY = b.SurfaceY;
        s.ShoreDistance = b.Depth >= WaterConsts.SwimDepth ? b.ShoreDistance(feet.X, feet.Z) : float.MaxValue;
    }

    /// <summary>
    /// Parse water.json. Version 1: <c>{"version": 1, "bodies": [{"id", "polygon": {"coordinates": rings}, "surface_y", "bed_y"}]}</c>,
    /// a flat bed per body. Version 2 adds per body <c>"depth": {"origin", "cell_m", "cols", "rows", "cm"}</c> (see
    /// <see cref="DepthGrid"/>): the bed varies over the body, and <c>bed_y</c> is its lowest point. A version 2 body
    /// without a depth grid keeps the flat bed. Polygon rings are local map metres (x east, y north) and are converted to
    /// Godot ground axes here.
    /// </summary>
    public static Water Parse(string json, string name = "water.json")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new DataFileException($"{name}: not JSON: {e.Message}", e); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number
                || v.GetInt32() is not (1 or 2))
                throw new DataFileException($"{name}: unsupported or missing version (expected 1 or 2)");
            var version = v.GetInt32();
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
                DepthGrid? grid = null;
                if (version == 2 && b.TryGetProperty("depth", out var depth))
                    grid = ReadDepth(depth, id, name);
                list.Add(new WaterBody
                {
                    Id = id,
                    Rings = rings,
                    SurfaceY = b.GetProperty("surface_y").GetSingle(),
                    BedY = b.GetProperty("bed_y").GetSingle(),
                    Grid = grid,
                    MinX = all.Min(p => p.X), MaxX = all.Max(p => p.X),
                    MinZ = all.Min(p => p.Y), MaxZ = all.Max(p => p.Y),
                });
            }
            return new Water(list);
        }
    }

    /// <summary>A body's depth grid: the origin, cell size, size, and little-endian uint16 centimetres in base64.</summary>
    static DepthGrid ReadDepth(JsonElement d, string id, string name)
    {
        var origin = d.GetProperty("origin").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var cell = d.GetProperty("cell_m").GetSingle();
        var cols = d.GetProperty("cols").GetInt32();
        var rows = d.GetProperty("rows").GetInt32();
        if (origin.Length != 2 || cell <= 0 || cols <= 0 || rows <= 0)
            throw new DataFileException($"{name}: body {id} has a bad depth grid");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(d.GetProperty("cm").GetString() ?? ""); }
        catch (FormatException e) { throw new DataFileException($"{name}: body {id} depth is not base64", e); }
        if (bytes.Length != cols * rows * 2)
            throw new DataFileException($"{name}: body {id} depth has {bytes.Length} bytes, expected {cols * rows * 2}");
        var depths = new float[cols * rows];
        for (var k = 0; k < depths.Length; k++)
            depths[k] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2 * k)) / 100f;
        return new DepthGrid(origin[0], origin[1], cell, cols, rows, depths);
    }
}
