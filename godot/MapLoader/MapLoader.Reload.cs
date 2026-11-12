using Godot;

namespace Suchohrad.MapLoader;

/// <summary>Runtime swap of the buildings of one chunk for those of a re-exported chunk file (hot reload of the map).</summary>
public sealed partial class MapLoader
{
    /// <summary>The map folder given to Load (an OS path); empty before Load.</summary>
    public string MapDir => _mapDir;

    /// <summary>Building nodes of a chunk: parcel_&lt;ID&gt;_visual, parcel_&lt;ID&gt;_collision and the window_* panes. Props are not.</summary>
    static bool IsBuildingNode(string name)
        => name.StartsWith("window_")
           || (name.StartsWith("parcel_")
               && (name.EndsWith("_visual") || (name.EndsWith("_collision") && !name.EndsWith("_props_collision"))));

    /// <summary>
    /// Swap the buildings of a loaded chunk for those in a re-exported chunk file: every parcel_&lt;ID&gt;_visual,
    /// parcel_&lt;ID&gt;_collision and window_* node of the chunk is freed and replaced by the file's. Everything else
    /// baked into the chunk (terrain, roads, fences, trees, regions) stays as loaded. A parcel replaced with
    /// <see cref="ReplaceParcel"/> stays replaced. Returns the number of parcels that have a building afterwards.
    /// Throws when the chunk was never loaded or the file cannot be read; the chunk is then unchanged.
    /// </summary>
    public int ReloadChunkBuildings(string glbPath, Vector2I index)
    {
        var key = $"chunk_{index.X}_{index.Y}";
        if (!_chunkRoots.TryGetValue(key, out var chunk))
            throw new KeyNotFoundException($"chunk {index.X},{index.Y} is not loaded");
        var doc = new GltfDocument();
        var state = new GltfState();
        var err = doc.AppendFromFile(glbPath, state);
        if (err != Error.Ok) throw new InvalidDataException($"glTF load failed ({err}): {glbPath}");
        var scene = doc.GenerateScene(state) as Node3D
            ?? throw new InvalidDataException($"glTF has no Node3D root: {glbPath}");

        // out with the old: the loaded building nodes of this chunk and what the loader remembers of them
        foreach (var old in Descendants(chunk).OfType<MeshInstance3D>().Where(m => IsBuildingNode(m.Name.ToString())).ToList())
        {
            if (!GodotObject.IsInstanceValid(old)) continue;
            var name = old.Name.ToString();
            if (name.EndsWith("_visual"))
            {
                var id = name["parcel_".Length..^"_visual".Length];
                if (old.Mesh != null) Stats.Triangles -= TriangleCount(old.Mesh);
                _visuals.Remove(id);
                if (_parcels.TryGetValue(id, out var info)) info.HasModel = false;
            }
            else if (name.EndsWith("_collision"))
                _shapes.Remove(name["parcel_".Length..^"_collision".Length]);
            old.GetParent().RemoveChild(old);
            old.QueueFree();
        }

        // in with the new, keeping each node where the file puts it
        var buildings = 0;
        foreach (var mi in Descendants(scene).OfType<MeshInstance3D>().Where(m => IsBuildingNode(m.Name.ToString())).ToList())
        {
            var name = mi.Name.ToString();
            var xf = mi.Transform;
            for (var p = mi.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D) xf = p.Transform * xf;
            mi.GetParent().RemoveChild(mi);
            if (mi.Mesh == null || (CollisionOnly && !name.EndsWith("_collision"))) { mi.Free(); continue; }
            chunk.AddChild(mi);
            mi.Transform = xf;
            if (name.EndsWith("_collision"))
            {
                BuildCollider(mi, name);
                continue;
            }
            ApplyMaterial(mi, name);
            if (!name.EndsWith("_visual")) continue; // a window pane
            Stats.Triangles += TriangleCount(mi.Mesh);
            var id = name["parcel_".Length..^"_visual".Length];
            _visuals[id] = mi;
            if (_parcels.TryGetValue(id, out var info)) info.HasModel = true;
            buildings++;
        }
        scene.Free();

        // a parcel swapped for a mod's scene keeps showing that scene
        foreach (var id in _replacements.Keys)
        {
            if (_visuals.TryGetValue(id, out var vis) && vis.GetParent() == chunk) vis.Visible = false;
            if (_shapes.TryGetValue(id, out var shapes))
                foreach (var s in shapes) s.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
        }
        GeometryChanged();
        return buildings;
    }
}
