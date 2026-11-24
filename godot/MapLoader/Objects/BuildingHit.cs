using Godot;

namespace Drycastle.MapLoader.Objects;

/// <summary>Where a ray meets a building: the point, the surface normal there (facing the ray's origin) and the parcel the building is on. Map space, Godot axes.</summary>
public readonly record struct BuildingHit(Vector3 Point, Vector3 Normal, string ParcelId, float Distance);
