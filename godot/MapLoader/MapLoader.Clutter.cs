using System.Text.Json;
using Godot;

namespace Suchohrad.MapLoader;

/// <summary>
/// Near-only ground clutter (tufts, pebbles, rocks, leaves, flowers): clutter.json (manifest <c>files.clutter</c>) lists instances per
/// chunk and kind, clutter_meshes.glb (<c>files.clutter_meshes</c>) has one small mesh node per kind. Each kind of a ~48 m cell becomes one
/// MultiMeshInstance3D with a visibility range (see <see cref="ClutterSettings"/>): no shadows, no collision, a flat colour material like the
/// rest of the map. A map without the files builds nothing, silently; a headless load (<see cref="CollisionOnly"/>) never builds it.
/// Rotation: the file's rot is counter-clockwise seen from above in map axes, which is a positive rotation about Godot's +Y axis (z = -north),
/// the same as the tree colliders (<c>Basis.Rotated(Up, Yaw)</c>) and placed objects (<c>RotationDegrees.Y</c>).
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>Clutter distance scale: 0 = off, 1 = default distances. Initialised from SUCHOHRAD_CLUTTER; set before Load.</summary>
    public static float ClutterScale { get; set; } = ClutterSettings.ParseScale(System.Environment.GetEnvironmentVariable(ClutterSettings.EnvVar));

    readonly List<MultiMeshInstance3D> _clutterNodes = new();
    readonly HashSet<string> _clutterWarned = new();

    /// <summary>Clutter nodes built by the last load (one per cell and kind).</summary>
    public IReadOnlyList<MultiMeshInstance3D> ClutterNodes => _clutterNodes;

    /// <summary>Frees the clutter nodes (a reload builds them again; the tree owns them, so a double build would duplicate).</summary>
    void FreeClutter()
    {
        foreach (var n in _clutterNodes)
            if (GodotObject.IsInstanceValid(n)) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
        _clutterNodes.Clear();
        Stats.ClutterInstances = 0;
    }

    void ClutterWarn(string key, string message)
    {
        if (_clutterWarned.Add(key)) Warn(message);
    }

    /// <summary>Builds the clutter, yielding the fraction done after each chunk's worth. <paramref name="manifest"/> is the manifest's root element.</summary>
    IEnumerable<float> BuildClutterSteps(string mapDir, JsonElement manifest)
    {
        FreeClutter();
        if (CollisionOnly || ClutterScale <= 0f) yield break;
        if (!manifest.TryGetProperty("files", out var files) || !files.TryGetProperty("clutter", out var cf) || cf.GetString() is not { } clutterFile)
            yield break;
        var path = Path.Combine(mapDir, clutterFile);
        if (!File.Exists(path)) { ClutterWarn("file", $"clutter: {clutterFile} is listed in the manifest but missing"); yield break; }

        ClutterDoc doc;
        try { doc = ClutterDoc.Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            ClutterWarn("parse", $"clutter: {e.Message}; no clutter drawn");
            yield break;
        }
        if (doc.Groups.Count == 0) yield break;

        var meshFile = files.TryGetProperty("clutter_meshes", out var mf) ? mf.GetString() : null;
        var meshes = meshFile == null ? new Dictionary<string, Mesh>() : LoadClutterMeshes(Path.Combine(mapDir, meshFile), doc.KindMesh);
        if (meshes.Count == 0) { ClutterWarn("meshes", "clutter: no meshes (files.clutter_meshes missing or empty); no clutter drawn"); yield break; }

        var done = 0;
        foreach (var g in doc.Groups)
        {
            if (++done % 8 == 0) yield return (float)done / doc.Groups.Count;
            if (!doc.KindMesh.TryGetValue(g.Kind, out var meshName) || !meshes.TryGetValue(meshName, out var mesh))
            {
                ClutterWarn("kind:" + g.Kind, $"clutter: no mesh for kind '{g.Kind}'");
                continue;
            }
            var parent = _chunkRoots.TryGetValue($"chunk_{g.ChunkX}_{g.ChunkY}", out var cr) ? cr : Root;
            var range = ClutterSettings.RangeOf(g.Kind) * ClutterScale + ClutterSettings.CellAllowanceM;
            foreach (var cell in ClutterSettings.Cells(g.Instances))
                _clutterNodes.Add(AddClutterCell(parent, g.Kind, mesh, cell, range));
        }
        if (_clutterNodes.Count > 0)
            GD.Print($"clutter: {Stats.ClutterInstances} instances in {_clutterNodes.Count} nodes, scale {ClutterScale}");
    }

    /// <summary>One MultiMesh for the instances of a cell: the node sits at their centroid and the instances are relative to it, so the
    /// visibility range (measured to the node) and the culling box (set by hand, the engine would have to walk every instance) are right.</summary>
    MultiMeshInstance3D AddClutterCell(Node3D parent, string kind, Mesh mesh, IReadOnlyList<ClutterInstance> cell, float rangeEnd)
    {
        var origin = Vector3.Zero;
        foreach (var i in cell) origin += ToGodot(i.X, i.Y, i.Z);
        origin /= cell.Count;

        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = cell.Count };
        var meshBox = mesh.GetAabb();
        var reach = Mathf.Max(meshBox.Size.Length(), 0.1f);
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue, float.MinValue);
        for (var k = 0; k < cell.Count; k++)
        {
            var i = cell[k];
            var local = ToGodot(i.X, i.Y, i.Z) - origin;
            var basis = Basis.Identity.Rotated(Vector3.Up, i.Rot).Scaled(new Vector3(i.Scale, i.Scale, i.Scale));
            mm.SetInstanceTransform(k, new Transform3D(basis, local));
            var r = reach * Mathf.Max(i.Scale, 0.01f);
            min = new Vector3(Mathf.Min(min.X, local.X - r), Mathf.Min(min.Y, local.Y - r), Mathf.Min(min.Z, local.Z - r));
            max = new Vector3(Mathf.Max(max.X, local.X + r), Mathf.Max(max.Y, local.Y + r), Mathf.Max(max.Z, local.Z + r));
        }
        mm.CustomAabb = new Aabb(min, max - min);
        var node = new MultiMeshInstance3D
        {
            Name = $"clutter_{kind}",
            Multimesh = mm,
            Position = origin,
            MaterialOverride = FlatMaterial(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityRangeEnd = rangeEnd,
            VisibilityRangeEndMargin = ClutterSettings.FadeMarginM,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        };
        parent.AddChild(node);
        Stats.ClutterInstances += cell.Count;
        return node;
    }

    /// <summary>The kind meshes of clutter_meshes.glb by mesh name: the MeshInstance3D named so (or whose mesh is). Transforms are ignored:
    /// the contract is Y-up, standing on y = 0, unit scale.</summary>
    Dictionary<string, Mesh> LoadClutterMeshes(string glbPath, IReadOnlyDictionary<string, string> kindMesh)
    {
        var result = new Dictionary<string, Mesh>();
        if (!File.Exists(glbPath)) return result;
        var gltf = new GltfDocument();
        var state = new GltfState();
        if (gltf.AppendFromFile(glbPath, state) != Error.Ok) return result;
        var scene = gltf.GenerateScene(state);
        if (scene == null) return result;
        var found = Descendants(scene).OfType<MeshInstance3D>().Where(m => m.Mesh != null).ToList();
        if (scene is MeshInstance3D self && self.Mesh != null) found.Add(self);
        foreach (var name in kindMesh.Values.Distinct())
        {
            var mi = found.FirstOrDefault(m => m.Name.ToString() == name) ?? found.FirstOrDefault(m => m.Mesh.ResourceName == name);
            if (mi != null) result[name] = mi.Mesh;
        }
        scene.Free(); // the Mesh resources are reference counted, the nodes are not needed
        return result;
    }
}
