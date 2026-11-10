namespace Suchohrad.MapLoader;

/// <summary>
/// Shared runtime loader for the Suchohrad base map. Stub for now.
///
/// Future job:
/// - Load a chunk's glTF at runtime (exported by the map pipeline) into the scene.
/// - Find the nodes named parcel_&lt;ID&gt;_visual and parcel_&lt;ID&gt;_collision
///   inside a chunk, so the visual mesh and the physics collider of each parcel
///   can be handled separately.
/// - Support parcel replacement: a mod supplies a replacement scene for a parcel
///   (by stable parcel ID), and the loader swaps out that parcel's base contents
///   while keeping the boundary fixed.
///
/// Every mod and the map viewer consume this library, so it must not depend on
/// any one mod's code.
/// </summary>
public static class MapLoader
{
    /// <summary>Placeholder; returns 0 until chunk loading is implemented.</summary>
    public static int LoadedChunkCount => 0;
}
