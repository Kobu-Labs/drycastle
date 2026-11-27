using System.Text.Json;
using Godot;

namespace Drycastle.MapLoader;

/// <summary>
/// The hollow houses' own nodes (docs/house-destruction.md Contract 2): house_&lt;bid&gt;_visual and house_&lt;bid&gt;_collision, taken out of their parcel's merged
/// meshes by the pipeline. Kept by building id for house destruction (the shader swap, the voxel collider); the collision is also an ordinary map collider
/// (nav bake, WorldQuery). The parcel_ rules (and the chunk reload, which swaps only parcel_ and window_ nodes) leave them alone.
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>One hollow house's nodes. Visual is null on a headless load (freed) or when the chunk has none.</summary>
    public sealed class HouseNode
    {
        public HouseNode(string id) { Id = id; }
        public string Id { get; }
        public MeshInstance3D? Visual { get; internal set; }
        /// <summary>Its textured faces (house_&lt;bid&gt;_visual_tex_&lt;texture&gt;, fence walls): siblings of <see cref="Visual"/> drawn with a library texture.</summary>
        public List<MeshInstance3D> Textured { get; } = new();
        public CollisionShape3D? Collision { get; internal set; }
    }

    readonly Dictionary<string, HouseNode> _houseNodes = new();

    /// <summary>The hollow houses' nodes by building id (interiors.json "id", house_voxels.bin id); filled by the chunk load.</summary>
    public IReadOnlyDictionary<string, HouseNode> HouseNodes => _houseNodes;

    const string HousePrefix = "house_", HouseVisual = "_visual", HouseCollision = "_collision";

    /// <summary>The building id of a house node name (house_&lt;bid&gt;&lt;suffix&gt;); null for any other node.</summary>
    public static string? HouseId(string name, string suffix)
        => name.Length > HousePrefix.Length + suffix.Length && name.StartsWith(HousePrefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[HousePrefix.Length..^suffix.Length] : null;

    HouseNode HouseOf(string id)
    {
        if (!_houseNodes.TryGetValue(id, out var h)) _houseNodes[id] = h = new HouseNode(id);
        return h;
    }

    /// <summary>Registers a house visual (house_&lt;bid&gt;_visual) or a textured sibling (house_&lt;bid&gt;_visual_tex_&lt;texture&gt;); false for any other node.</summary>
    bool RegisterHouseVisual(MeshInstance3D mi, string name)
    {
        var (baseName, texture) = SplitTexNode(name);
        if (HouseId(baseName, HouseVisual) is not { } id) return false;
        if (texture == null) HouseOf(id).Visual = mi;
        else HouseOf(id).Textured.Add(mi);
        return true;
    }

    /// <summary>Registers a house collider (house_&lt;bid&gt;_collision) built by BuildCollider; false for any other node.</summary>
    bool RegisterHouseCollision(CollisionShape3D cs, string name)
    {
        if (HouseId(name, HouseCollision) is not { } id) return false;
        HouseOf(id).Collision = cs;
        return true;
    }

    /// <summary>house_voxels.bin of the loaded map (manifest files.house_voxels), or null when the map has none (no destruction).</summary>
    public string? HouseVoxelsPath()
    {
        if (_mapDir == "") return null;
        var manifest = Path.Combine(_mapDir, "manifest.json");
        if (!File.Exists(manifest)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
        if (!doc.RootElement.TryGetProperty("files", out var files) || !files.TryGetProperty("house_voxels", out var hv) || hv.GetString() is not { } rel) return null;
        var path = Path.Combine(_mapDir, rel);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The shared flat vertex-colour map material (the look a house shader must match, and the cut faces' material).</summary>
    public static StandardMaterial3D SharedFlatMaterial => FlatMaterial();

    /// <summary>A house collider was changed at runtime (house destruction): cached ray results of the old geometry are stale.</summary>
    public void HouseGeometryChanged() => GeometryChanged();
}
