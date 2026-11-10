using Godot;

namespace Suchohrad.MapLoader;

/// <summary>A parcel of the base map (from parcels.json).</summary>
public sealed class ParcelInfo
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string CadastralRef { get; init; } = "";
    /// <summary>Chunk index (ix, iy).</summary>
    public Vector2I Chunk { get; init; }
    public IReadOnlyList<string> BuildingIds { get; init; } = Array.Empty<string>();
    /// <summary>Anchor in map metres: x east, y north.</summary>
    public Vector2 AnchorMap { get; init; }
    /// <summary>Anchor in Godot axes (x east, z = -north); y is 0 until placed on terrain.</summary>
    public Vector3 AnchorXZ => new(AnchorMap.X, 0f, -AnchorMap.Y);
    /// <summary>True when the chunk glTF has parcel_ID_visual / parcel_ID_collision nodes for it.</summary>
    public bool HasModel { get; internal set; }
}

/// <summary>A spawn candidate (from spawns.json).</summary>
public sealed class SpawnPoint
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Parcel { get; init; } = "";
    public string Highway { get; init; } = "";
    public Vector2I Chunk { get; init; }
    /// <summary>Position in Godot axes: x east, y elevation, z = -north.</summary>
    public Vector3 Position { get; init; }
}

/// <summary>Counters filled while loading.</summary>
public sealed class LoadStats
{
    public int ChunksLoaded;
    public int CollidersBuilt;
    public int Triangles;
    public double LoadSeconds;
}
