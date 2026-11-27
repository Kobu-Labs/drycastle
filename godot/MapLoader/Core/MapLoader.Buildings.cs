using System.Text.Json;
using Godot;

namespace Drycastle.MapLoader;

/// <summary>
/// Live swap of the buildings of some parcels for those of a <c>building-run</c> fragment (the viewer's photo-to-building loop, pipeline
/// edits/building_run.py): the baked nodes of the parcel are hidden (parcel_&lt;ID&gt;_visual / _collision, its house_&lt;bid&gt;_* nodes and window_&lt;bid&gt;_* panes), the
/// fragment's nodes are added with colliders and the same material treatment as at load, and the registries (<c>_visuals</c>, <c>_shapes</c>, <c>_houseNodes</c>,
/// HasModel, Stats.Triangles) point at the live nodes. A repeat replaces the live nodes; <see cref="RevertBuildings"/> brings the baked ones back. A parcel that
/// a mod replaced stays replaced: its live building is hidden and not collided with. Footprints and the ground under them are not part of it.
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>One parcel's live buildings and what they stand in for: the baked nodes that were hidden (kept to be shown again).</summary>
    sealed class LiveBuildings
    {
        public Node3D? Container;
        public int AddedTriangles;
        public Node3D? BakedVisual;
        public List<CollisionShape3D>? BakedShapes;
        public bool BakedHadModel;
        public readonly Dictionary<string, HouseNode> BakedHouses = new();
        public readonly List<(Node3D Node, int Triangles)> HiddenVisuals = new(); // visuals (parcel, house, windows) with their triangles, counted out of Stats
        public readonly HashSet<string> Buildings = new();
    }

    readonly Dictionary<string, LiveBuildings> _liveBuildings = new();

    /// <summary>The parcels whose buildings are swapped for a fragment's.</summary>
    public IReadOnlyCollection<string> LiveBuildingParcels => _liveBuildings.Keys;

    /// <summary>
    /// Apply a <c>building-run</c> fragment (its JSON text; the GLB it names is read first, so a bad file leaves the map as it was).
    /// Throws when a parcel is unknown or the GLB cannot be read. Main thread.
    /// </summary>
    public BuildingResult ApplyBuildingFragment(string fragmentJson)
    {
        string glbPath;
        List<string> parcels;
        Dictionary<string, List<string>> buildings = new();
        using (var doc = JsonDocument.Parse(fragmentJson))
        {
            var r = doc.RootElement;
            parcels = r.GetProperty("parcels").EnumerateArray().Select(e => e.GetString()!).ToList();
            if (r.TryGetProperty("buildings", out var b))
                foreach (var kv in b.EnumerateObject()) buildings[kv.Name] = kv.Value.EnumerateArray().Select(e => e.GetString()!).ToList();
            glbPath = r.TryGetProperty("glb", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()! : "";
        }
        foreach (var id in parcels)
            if (!_parcels.ContainsKey(id)) throw new KeyNotFoundException($"unknown parcel {id}");
        Node3D? scene = glbPath != "" && parcels.Count > 0 ? LoadGltf(glbPath) : null;

        GeometryChanged();
        var owner = new Dictionary<string, string>(); // building id -> parcel
        foreach (var (pid, bids) in buildings) foreach (var bid in bids) owner[bid] = pid;
        var byParcel = parcels.ToDictionary(p => p, _ => new List<(MeshInstance3D mi, string name)>());
        if (scene != null)
        {
            foreach (var mi in AllMeshes(scene).ToList())
            {
                var name = mi.Name.ToString();
                if (mi.Mesh == null) continue;
                if (BuildingNodeParcel(name, owner) is { } pid && byParcel.TryGetValue(pid, out var list)) list.Add((mi, name));
                else Warn($"building fragment GLB has node {name}, which is not of its parcels; ignored");
            }
        }
        int nodes = 0, triangles = 0;
        foreach (var id in parcels)
        {
            var (n, t) = SwapBuildings(id, buildings.GetValueOrDefault(id) ?? new(), byParcel[id]);
            nodes += n;
            triangles += t;
        }
        scene?.Free();
        return new BuildingResult(parcels, nodes, triangles);
    }

    /// <summary>The parcel a node of a fragment GLB belongs to: parcel_&lt;ID&gt;_*, house_&lt;bid&gt;_* or window_&lt;bid&gt;_*; null for any other.</summary>
    static string? BuildingNodeParcel(string name, Dictionary<string, string> parcelOfBuilding)
    {
        var (baseName, _) = SplitTexNode(name);
        if (name.StartsWith("parcel_") && (name.EndsWith("_collision") || baseName.EndsWith("_visual")))
            return name.EndsWith("_collision") ? name["parcel_".Length..^"_collision".Length] : baseName["parcel_".Length..^"_visual".Length];
        var house = HouseId(baseName, HouseVisual) ?? HouseId(name, HouseCollision) ?? WindowBuildingId(name);
        return house != null && parcelOfBuilding.TryGetValue(house, out var p) ? p : null;
    }

    /// <summary>The building id of a window pane node (window_&lt;bid&gt;_&lt;opening&gt;); null for any other node. Building ids hold no underscore.</summary>
    static string? WindowBuildingId(string name)
    {
        const string prefix = "window_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = name.IndexOf('_', prefix.Length);
        return end > prefix.Length ? name[prefix.Length..end] : null;
    }

    (int Nodes, int Triangles) SwapBuildings(string id, IReadOnlyList<string> bids, List<(MeshInstance3D mi, string name)> nodes)
    {
        if (_liveBuildings.TryGetValue(id, out var live)) DropLiveBuildings(id, live);
        else _liveBuildings[id] = live = HideBakedBuildings(id, bids);
        foreach (var b in bids) live.Buildings.Add(b);

        var container = new Node3D { Name = $"dyn_buildings_{id}" };
        Root.AddChild(container);
        live.Container = container;
        int count = 0;
        foreach (var (mi, name) in nodes)
        {
            var xf = mi.Transform;
            for (var p = mi.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D) xf = p.Transform * xf;
            mi.GetParent().RemoveChild(mi);
            if (CollisionOnly && !name.EndsWith("_collision")) { mi.Free(); continue; }
            container.AddChild(mi);
            mi.Transform = xf;
            count++;
            if (name.EndsWith("_collision")) { BuildCollider(mi, name); continue; } // registers _shapes[id] or the house's collider
            ApplyMaterial(mi, name);
            RegisterVisual(mi, name); // _visuals[id] + HasModel for the parcel's, _houseNodes for a house
            live.AddedTriangles += TriangleCount(mi.Mesh);
        }
        Stats.Triangles += live.AddedTriangles;
        var info = _parcels[id];
        info.HasModel = _visuals.ContainsKey(id);
        if (IsReplaced(id)) // a mod's scene stands on the parcel: the building under it stays out of the way
        {
            if (_visuals.TryGetValue(id, out var vis)) vis.Visible = false;
            if (_shapes.TryGetValue(id, out var shapes)) foreach (var s in shapes) s.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
        }
        return (count, live.AddedTriangles);
    }

    /// <summary>Hides the baked building nodes of a parcel and takes them out of the registries; returns what was hidden.</summary>
    LiveBuildings HideBakedBuildings(string id, IReadOnlyList<string> bids)
    {
        var live = new LiveBuildings { BakedHadModel = _parcels[id].HasModel };
        void Hide(Node3D? node)
        {
            if (node == null || !GodotObject.IsInstanceValid(node)) return;
            node.Visible = false;
            live.HiddenVisuals.Add((node, node is MeshInstance3D { Mesh: { } m } ? TriangleCount(m) : 0));
        }
        if (_visuals.Remove(id, out var visual)) { live.BakedVisual = visual; Hide(visual); }
        if (_shapes.Remove(id, out var shapes))
        {
            live.BakedShapes = shapes;
            foreach (var s in shapes) if (GodotObject.IsInstanceValid(s)) s.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
        }
        foreach (var bid in bids)
        {
            if (!_houseNodes.Remove(bid, out var house)) continue;
            live.BakedHouses[bid] = house;
            Hide(house.Visual);
            foreach (var t in house.Textured) Hide(t);
            if (house.Collision != null && GodotObject.IsInstanceValid(house.Collision)) house.Collision.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
        }
        // the panes are not registered: they are the chunk's window_<bid>_* meshes
        var set = bids.ToHashSet();
        if (_chunkRoots.TryGetValue($"chunk_{_parcels[id].Chunk.X}_{_parcels[id].Chunk.Y}", out var chunk))
            foreach (var mi in Descendants(chunk).OfType<MeshInstance3D>().ToList())
                if (WindowBuildingId(mi.Name.ToString()) is { } bid && set.Contains(bid)) Hide(mi);
        Stats.Triangles -= live.HiddenVisuals.Sum(h => h.Triangles);
        return live;
    }

    /// <summary>Frees the live nodes of a parcel and takes them out of the registries (what was hidden stays hidden).</summary>
    void DropLiveBuildings(string id, LiveBuildings live)
    {
        GeometryChanged();
        if (live.Container != null && GodotObject.IsInstanceValid(live.Container))
        {
            live.Container.GetParent()?.RemoveChild(live.Container);
            live.Container.QueueFree();
        }
        live.Container = null;
        Stats.Triangles -= live.AddedTriangles;
        live.AddedTriangles = 0;
        _visuals.Remove(id);
        _shapes.Remove(id);
        foreach (var bid in live.Buildings) _houseNodes.Remove(bid);
        _parcels[id].HasModel = false;
    }

    /// <summary>Undo <see cref="ApplyBuildingFragment"/> for one parcel: drop its live buildings, show the baked ones again. A parcel without live buildings: nothing happens.</summary>
    public void RevertBuildings(string id)
    {
        if (!_liveBuildings.Remove(id, out var live)) return;
        DropLiveBuildings(id, live);
        var replaced = IsReplaced(id);
        foreach (var (node, triangles) in live.HiddenVisuals)
        {
            if (!GodotObject.IsInstanceValid(node)) continue;
            node.Visible = node != live.BakedVisual || !replaced;
            Stats.Triangles += triangles;
        }
        if (live.BakedVisual != null && GodotObject.IsInstanceValid(live.BakedVisual)) _visuals[id] = live.BakedVisual;
        if (live.BakedShapes != null)
        {
            _shapes[id] = live.BakedShapes;
            foreach (var s in live.BakedShapes) if (GodotObject.IsInstanceValid(s)) s.SetDeferred(CollisionShape3D.PropertyName.Disabled, replaced);
        }
        _parcels[id].HasModel = live.BakedHadModel;
        foreach (var (bid, house) in live.BakedHouses)
        {
            _houseNodes[bid] = house;
            if (house.Collision != null && GodotObject.IsInstanceValid(house.Collision)) house.Collision.SetDeferred(CollisionShape3D.PropertyName.Disabled, false);
        }
    }

    /// <summary>Revert the live buildings of every parcel of a chunk (its re-exported file brings the same edits, baked).</summary>
    public void RevertBuildingsInChunk(Vector2I chunk)
    {
        foreach (var id in _liveBuildings.Keys.Where(k => _parcels[k].Chunk == chunk).ToList()) RevertBuildings(id);
    }
}

/// <summary>What <see cref="MapLoader.ApplyBuildingFragment"/> did: the parcels, the nodes added (visuals, colliders, panes) and their visual triangles.</summary>
public sealed record BuildingResult(IReadOnlyList<string> Parcels, int Nodes, int Triangles);
