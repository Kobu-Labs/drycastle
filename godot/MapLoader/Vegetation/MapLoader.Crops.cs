using System.Text.Json;
using Godot;
using Drycastle.MapLoader.Trees;
using Drycastle.MapLoader.Vegetation;

namespace Drycastle.MapLoader;

/// <summary>
/// Crop fields (#328): crops.json (manifest <c>files.crops</c>; only maps with a crop patch) lists plant and artifact instances per chunk and kind
/// (rows x, y, z, rot, scale in map axes, exactly as clutter.json: <c>ToGodot</c>, rot a positive turn about Godot's +Y). Each kind is drawn with
/// <c>asset</c> (an asset id of assets/manifest.json, e.g. the generated rapeseed plant) or <c>synty</c> (model names of the private POLYGON Farm
/// pack, <c>pack:synty-farm/&lt;name&gt;</c>, several = variants picked per instance by a position hash). A model is flattened once into one
/// <see cref="ArrayMesh"/> with one surface per pack material (a prefab's parts, e.g. a tractor and its wheels, are merged), so a MultiMesh draws it with
/// the pack's own atlas material, shared. Each kind and variant of a ~48 m cell is one MultiMeshInstance3D with a visibility range
/// (<see cref="CropSettings"/>). Plants wear crop_plant.gdshader (their own material's look): over the outer band of their range each plant grows in at a distance of
/// its own, so the crop thins out towards the range instead of ending at a line of cells. Beyond it the field is its sheet
/// (MapLoader.CropSheet.cs): a picture of the crop over the planted ground, to the horizon.
/// </summary>
public sealed partial class MapLoader
{
    readonly List<GeometryInstance3D> _cropNodes = new();
    readonly Dictionary<string, Mesh?> _cropMeshes = new();        // asset id -> flattened model (null = not available)
    readonly Dictionary<string, StandardMaterial3D> _cropPlaceholderMaterials = new();
    readonly HashSet<string> _cropWarned = new();
    readonly Dictionary<Material, Material> _cropPlantMaterials = new(); // a plant model's material -> the same look with the grow-in
    readonly Dictionary<string, Shader> _cropPlantShaders = new();       // by cull mode

    /// <summary>Crop nodes built by the last load: one MultiMesh per cell, kind and variant.</summary>
    public IReadOnlyList<GeometryInstance3D> CropNodes => _cropNodes;

    void FreeCrops()
    {
        foreach (var n in _cropNodes)
            if (GodotObject.IsInstanceValid(n)) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
        _cropNodes.Clear();
        _cropCells.Clear();
        _cropMeshes.Clear();
        _cropPlantMaterials.Clear();
        FreeCropSheets();
        Stats.CropInstances = 0;
    }

    void CropWarn(string key, string message)
    {
        if (_cropWarned.Add(key)) Warn(message);
    }

    IEnumerable<float> BuildCropSteps(string mapDir, JsonElement manifest)
    {
        FreeCrops();
        if (CollisionOnly || ClutterScale <= 0f) yield break;
        if (!manifest.TryGetProperty("files", out var files) || !files.TryGetProperty("crops", out var e) || e.ValueKind != JsonValueKind.String || e.GetString() is not { } cropsFile)
            yield break;
        var path = Path.Combine(mapDir, cropsFile);
        if (!File.Exists(path)) { CropWarn("file", $"crops: {cropsFile} is listed in the manifest but missing"); yield break; }

        CropDoc doc;
        try { doc = CropDoc.Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            CropWarn("parse", $"crops: {ex.Message}; no crops drawn");
            yield break;
        }
        if (doc.Groups.Count == 0) yield break;

        var meshes = new Dictionary<string, List<Mesh>>();
        var sheets = new List<(Node3D Parent, CropGroup Group)>();
        var done = 0;
        foreach (var g in doc.Groups)
        {
            if (++done % 8 == 0) yield return (float)done / doc.Groups.Count;
            var spec = doc.Kinds[g.Kind];
            if (!meshes.TryGetValue(g.Kind, out var variants)) meshes[g.Kind] = variants = CropMeshesOf(g.Kind, spec);
            var parent = _chunkRoots.TryGetValue($"chunk_{g.ChunkX}_{g.ChunkY}", out var cr) ? cr : Root;
            var range = CropSettings.RangeOf(spec) * ClutterScale + ClutterSettings.CellAllowanceM;
            for (var v = 0; v < variants.Count; v++)
            {
                var mine = variants.Count == 1 ? g.Instances : g.Instances.Where(i => CropSettings.Variant(i, variants.Count) == v).ToList();
                foreach (var cell in ClutterSettings.Cells(mine))
                    _cropNodes.Add(AddCropCell(parent, g.Kind, variants.Count > 1 ? v : -1, variants[v], cell, range, spec.IsPlant));
            }
            if (spec.IsPlant) sheets.Add((parent, g));
        }
        BuildCropSheets(sheets);
        if (_cropNodes.Count > 0)
            GD.Print($"crops: {Stats.CropInstances} instances in {_cropNodes.Count} nodes, scale {ClutterScale}");
    }

    /// <summary>The meshes a kind is drawn with: its asset, else its Farm pack models that exist on this machine, else one placeholder.</summary>
    List<Mesh> CropMeshesOf(string kind, CropKind spec)
    {
        var found = new List<Mesh>();
        var ids = spec.Asset != null ? new List<string> { spec.Asset } : spec.Synty.Select(CropSettings.SyntyAsset).ToList();
        if (_assetManifest == null) ReadAssetManifest();
        var known = _assetManifest!;
        foreach (var id in ids)
        {
            if (!_cropMeshes.TryGetValue(id, out var mesh))
                _cropMeshes[id] = mesh = known.ContainsKey(id) && GetAssetTemplate(id, out _) is { } scene ? FlattenModel(scene, onGround: !spec.IsPlant, onSide: kind == "art_hay_bale_round") : null;
            if (mesh != null) found.Add(mesh);
        }
        if (found.Count == 0)
        {
            if (ids.Count > 0) CropWarn("pack:" + kind, $"crops: no model for '{kind}' ({string.Join(", ", ids)}); drawn as a placeholder (the Synty Farm pack is not installed)");
            found.Add(PlaceholderMesh(kind, spec));
        }
        if (spec.IsPlant)
        {
            _cropPaint[kind] = found.Select(PaintModelOf).ToList(); // before the materials are swapped: it reads their colours
            foreach (var mesh in found) GrowIn(mesh);
        }
        return found;
    }

    /// <summary>Swaps the materials of a plant's mesh (ours: flattened or a placeholder, never the asset's) for crop_plant.gdshader ones of the same look.</summary>
    void GrowIn(Mesh mesh)
    {
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            if (mesh.SurfaceGetMaterial(s) is BaseMaterial3D source)
                mesh.SurfaceSetMaterial(s, CropPlantMaterial(source));
    }

    Vector3 PlantCutParam() => new(CropSettings.PlantRangeM * ClutterScale, CropSettings.PlantFadeM * ClutterScale, CropSettings.PlantGrow);

    /// <summary>A plant material with the grow-in: the source's albedo, alpha scissor, cull mode and surface amounts, one per source material (shared by every cell).</summary>
    Material CropPlantMaterial(BaseMaterial3D source)
    {
        if (_cropPlantMaterials.TryGetValue(source, out var made)) return made;
        var cull = source.CullMode switch
        {
            BaseMaterial3D.CullModeEnum.Front => "cull_front",
            BaseMaterial3D.CullModeEnum.Disabled => "cull_disabled",
            _ => "cull_back",
        };
        if (!_cropPlantShaders.TryGetValue(cull, out var shader))
            _cropPlantShaders[cull] = shader = TreeCutShaders.Load("crop_plant.gdshader", cull);
        var mat = new ShaderMaterial { Shader = shader };
        if (source.AlbedoTexture is { } tex) mat.SetShaderParameter("albedo_tex", tex);
        mat.SetShaderParameter("albedo_colour", source.AlbedoColor);
        // a cut-out stays a cut-out; a blended one becomes one too (a field of blended plants would sort badly and cost a pass)
        mat.SetShaderParameter("scissor", source.Transparency switch
        {
            BaseMaterial3D.TransparencyEnum.Disabled => -1f,
            BaseMaterial3D.TransparencyEnum.AlphaScissor => source.AlphaScissorThreshold,
            _ => 0.5f,
        });
        mat.SetShaderParameter("roughness", source.Roughness);
        mat.SetShaderParameter("metallic", source.Metallic);
        mat.SetShaderParameter("specular", source.MetallicSpecular);
        mat.SetShaderParameter("vertex_colour", source.VertexColorUseAsAlbedo ? 1f : 0f);
        mat.SetShaderParameter("vertex_srgb", source.VertexColorIsSrgb ? 1f : 0f);
        mat.SetShaderParameter("crop_cut", PlantCutParam());
        return _cropPlantMaterials[source] = mat;
    }

    /// <summary>Every visible mesh of a model scene (not in the tree) in one ArrayMesh, relative to the scene root, one surface per material.
    /// Null when it holds no triangles. Collision bodies and shapes are ignored: crops never collide.</summary>
    /// <paramref name="onGround"/> (artifacts): a model centred on its origin (the pack's hay bales: half of it would be under the ground) is lifted to stand on y = 0; one whose origin is
    /// already at its foot (a pile) or that is meant to go into the ground (a scarecrow's post) is left. <paramref name="onSide"/>: such a model (the round bale, a cylinder along y) is laid on its side first, its axis along x.
    static ArrayMesh? FlattenModel(Node3D root, bool onGround = false, bool onSide = false)
    {
        var flat = new ModelFlattener();
        flat.Walk(root, Transform3D.Identity, true);
        var mesh = flat.Build();
        if (!onGround || mesh == null) return mesh;
        var box = mesh.GetAabb();
        if (Mathf.Abs(box.Position.Y + box.End.Y) > 0.1f * box.Size.Y) return mesh; // not centred
        var stand = onSide ? new Transform3D(new Basis(Vector3.Back, Mathf.Pi / 2f), new Vector3(0, -box.Position.X, 0)) : new Transform3D(Basis.Identity, new Vector3(0, -box.Position.Y, 0));
        var lifted = new ModelFlattener();
        lifted.Walk(root, stand, true);
        return lifted.Build();
    }

    /// <summary>Collects the surfaces of a scene's meshes (with their node transforms) per material.</summary>
    sealed class ModelFlattener
    {
        readonly Dictionary<Material, SurfaceTool> _tools = new();
        readonly List<Material> _order = new();

        void Add(Mesh mesh, Transform3D xf, Func<int, Material?> material)
        {
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var mat = material(s) ?? FlatMaterial();
                if (!_tools.TryGetValue(mat, out var st))
                {
                    st = new SurfaceTool();
                    st.Begin(Mesh.PrimitiveType.Triangles);
                    _tools[mat] = st;
                    _order.Add(mat);
                }
                st.AppendFrom(mesh, s, xf);
            }
        }

        public void Walk(Node3D n, Transform3D xf, bool isRoot)
        {
            if (!n.Visible) return;
            if (!isRoot) xf *= n.Transform;
            if (n is MeshInstance3D { Mesh: { } m } mi) Add(m, xf, s => mi.GetActiveMaterial(s));
            else if (n is ImporterMeshInstance3D { Mesh: { } im }) Add(im.GetMesh(), xf, s => im.GetSurfaceMaterial(s));
            foreach (var c in n.GetChildren())
                if (c is Node3D child) Walk(child, xf, false);
        }

        public ArrayMesh? Build()
        {
            if (_order.Count == 0) return null;
            var result = new ArrayMesh();
            foreach (var mat in _order)
            {
                _tools[mat].SetMaterial(mat);
                _tools[mat].Commit(result);
            }
            return result.GetSurfaceCount() > 0 ? result : null;
        }
    }

    Mesh PlaceholderMesh(string kind, CropKind spec)
    {
        var p = CropPlaceholder.For(kind, spec);
        if (!_cropPlaceholderMaterials.TryGetValue(p.Colour, out var mat))
            _cropPlaceholderMaterials[p.Colour] = mat = new StandardMaterial3D { AlbedoColor = Color.FromHtml(p.Colour), Roughness = 1f };
        PrimitiveMesh prim = p.Cone
            ? new CylinderMesh { TopRadius = 0f, BottomRadius = p.Width / 2f, Height = p.Height, RadialSegments = 5, Rings = 1 }
            : new BoxMesh { Size = new Vector3(p.Width, p.Height, p.Width) };
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.AppendFrom(prim, 0, new Transform3D(Basis.Identity, new Vector3(0f, p.Height / 2f, 0f))); // the primitive is centred: stand it on y = 0
        st.SetMaterial(mat);
        return st.Commit();
    }

    /// <summary>One MultiMesh for the instances of a cell: the node sits at their centroid, the instances are relative to it (visibility range and culling box right).
    /// No material override: the model keeps its own (shared) materials. No collision.</summary>
    MultiMeshInstance3D AddCropCell(Node3D parent, string kind, int variant, Mesh mesh, IReadOnlyList<ClutterInstance> cell, float rangeEnd, bool plant)
    {
        var origin = Vector3.Zero;
        foreach (var i in cell) origin += ToGodot(i.X, i.Y, i.Z);
        origin /= cell.Count;

        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = cell.Count };
        var reach = Mathf.Max(mesh.GetAabb().Size.Length(), 0.1f);
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
            Name = variant < 0 ? $"crop_{kind}" : $"crop_{kind}_{variant}",
            Multimesh = mm,
            Position = origin,
            CastShadow = plant ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On,
            VisibilityRangeEnd = rangeEnd,
            VisibilityRangeEndMargin = ClutterSettings.FadeMarginM,
            // plants are gone at their radius by their shader, one by one: the node's range only culls the cell, it must not fade it
            VisibilityRangeFadeMode = plant ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled : GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        };
        parent.AddChild(node);
        _cropCells.Add(new CropCell(mm, kind, plant, cell, new bool[cell.Count]));
        Stats.CropInstances += cell.Count;
        return node;
    }
}
