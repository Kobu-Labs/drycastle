using Godot;

namespace Drycastle.MapLoader;

/// <summary>
/// The game's chunk visuals as a few meshes per chunk instead of one node and one draw call per parcel, props, road and patch piece (#441): the static
/// meshes of a chunk file with the same material are concatenated, per <see cref="MergeCellM"/> square of the chunk so frustum culling and the shadow passes
/// still drop the parts that are far from the view. The map files stay as they are; this runs at load, and only when <see cref="MergeChunkMeshes"/> is set
/// (the shooter's client: the viewer and the editor look the parcel, props and patch nodes up by name and hide or swap them).
/// Left alone: the terrain (read back by the minimap and tactical map), the window panes (the church and the urad clear their glass by name), the collision
/// meshes and anything with children, skin or more than one surface.
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>Merge the static chunk meshes per material at load (see the file comment). Off by default; set before Load. A headless load draws nothing, so it never merges.</summary>
    public bool MergeChunkMeshes { get; set; }

    /// <summary>Side (metres) of the square a merged mesh covers: a quarter of a chunk. Smaller means more draws, larger means more off-screen triangles drawn.</summary>
    public const float MergeCellM = 125f;

    /// <summary>Chunk visual nodes folded into merged meshes by the last load, and the merged meshes made from them.</summary>
    public int MergedSources { get; private set; }
    public int MergedMeshes { get; private set; }

    // the arrays a merged mesh carries; a surface with any other (bones, custom data) is not merged
    const int MergeArrays = (1 << (int)Mesh.ArrayType.Vertex) | (1 << (int)Mesh.ArrayType.Normal) | (1 << (int)Mesh.ArrayType.Tangent) | (1 << (int)Mesh.ArrayType.Color)
        | (1 << (int)Mesh.ArrayType.TexUV) | (1 << (int)Mesh.ArrayType.TexUV2) | (1 << (int)Mesh.ArrayType.Index);

    readonly record struct MergeKey(ulong Material, int Format, GeometryInstance3D.ShadowCastingSetting Shadow, int Cx, int Cz);

    /// <summary>Merges the mergeable visuals of one loaded chunk (the glTF <paramref name="scene"/> under <paramref name="chunk"/>); the merged nodes are children of the chunk.</summary>
    void MergeChunkVisuals(Node3D chunk, Node3D scene)
    {
        var groups = new Dictionary<MergeKey, List<(MeshInstance3D Node, Transform3D Xf)>>();
        foreach (var mi in Descendants(scene).OfType<MeshInstance3D>().ToList())
        {
            if (mi.Mesh is not ArrayMesh mesh || mi.MaterialOverride is not { } mat || mi.GetChildCount() > 0 || !mi.Visible || mi.Skin != null) continue;
            var name = mi.Name.ToString();
            if (name == "terrain" || name.StartsWith("window_") || name.EndsWith("_collision")) continue;
            if (mesh.GetSurfaceCount() != 1 || mesh.SurfaceGetPrimitiveType(0) != Mesh.PrimitiveType.Triangles) continue;
            var format = (int)mesh.SurfaceGetFormat(0);
            // the format's array bits are the ArrayType bits; anything outside the set above (bones, weights, custom data) stays as it is
            if ((format & ~MergeArrays & ((1 << (int)Mesh.ArrayType.Max) - 1)) != 0) continue;
            var xf = mi.Transform;
            for (var p = mi.GetParent() as Node3D; p != null && p != chunk; p = p.GetParent() as Node3D) xf = p.Transform * xf;
            var centre = xf * mesh.GetAabb().GetCenter();
            var key = new MergeKey(mat.GetInstanceId(), format & MergeArrays, mi.CastShadow,
                (int)Mathf.Floor(centre.X / MergeCellM), (int)Mathf.Floor(centre.Z / MergeCellM));
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add((mi, xf));
        }

        var merged = new HashSet<Node>();
        foreach (var (key, list) in groups)
        {
            if (list.Count < 2) continue;
            var node = BuildMergedChunkMesh(list, key);
            if (node == null) continue;
            chunk.AddChild(node);
            foreach (var (src, _) in list) merged.Add(src);
            MergedMeshes++;
        }
        if (merged.Count == 0) return;
        MergedSources += merged.Count;
        // the loader's lookups must not hold the folded nodes (the shooter never reads them; a later call is then a no-op, not a freed-node access)
        foreach (var k in _propsVisuals.Where(kv => merged.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _propsVisuals.Remove(k);
        foreach (var l in _propsTexVisuals.Values) l.RemoveAll(merged.Contains);
        foreach (var l in _bakedPatches.Values) l.RemoveAll(merged.Contains);
        foreach (var k in _visuals.Where(kv => merged.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _visuals.Remove(k);
        foreach (var src in merged)
        {
            var mi = (MeshInstance3D)src;
            var mesh = mi.Mesh;
            mi.GetParent()?.RemoveChild(mi);
            mi.Free();
            if (mesh is ArrayMesh) mesh.Dispose(); // the GLB's copy of the vertices is not needed: drop the wrapper's reference now rather than at the next collection
        }
    }

    MeshInstance3D? BuildMergedChunkMesh(List<(MeshInstance3D Node, Transform3D Xf)> list, MergeKey key)
    {
        var format = key.Format;
        bool Has(Mesh.ArrayType t) => (format & (1 << (int)t)) != 0;
        int vertices = 0, indices = 0;
        var parts = new List<(Godot.Collections.Array Arrays, Transform3D Xf, int Count)>(list.Count);
        foreach (var (mi, xf) in list)
        {
            var arrays = mi.Mesh.SurfaceGetArrays(0);
            var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (v.Length == 0) continue;
            parts.Add((arrays, xf, v.Length));
            vertices += v.Length;
            indices += arrays[(int)Mesh.ArrayType.Index].AsInt32Array().Length;
        }
        if (parts.Count < 2) return null;

        var pos = new Vector3[vertices];
        var norm = Has(Mesh.ArrayType.Normal) ? new Vector3[vertices] : null;
        var tan = Has(Mesh.ArrayType.Tangent) ? new float[vertices * 4] : null;
        var col = Has(Mesh.ArrayType.Color) ? new Color[vertices] : null;
        var uv = Has(Mesh.ArrayType.TexUV) ? new Vector2[vertices] : null;
        var uv2 = Has(Mesh.ArrayType.TexUV2) ? new Vector2[vertices] : null;
        var idx = Has(Mesh.ArrayType.Index) ? new int[indices] : null;
        int vo = 0, io = 0;
        foreach (var (arrays, xf, count) in parts)
        {
            var identity = xf.IsEqualApprox(Transform3D.Identity);
            var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (identity) Array.Copy(v, 0, pos, vo, count);
            else for (var i = 0; i < count; i++) pos[vo + i] = xf * v[i];
            if (norm != null)
            {
                var n = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var nb = xf.Basis.Inverse().Transposed();
                if (identity) Array.Copy(n, 0, norm, vo, count);
                else for (var i = 0; i < count; i++) norm[vo + i] = (nb * n[i]).Normalized();
            }
            if (tan != null)
            {
                var t = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
                for (var i = 0; i < count; i++)
                {
                    var d = identity ? new Vector3(t[i * 4], t[i * 4 + 1], t[i * 4 + 2]) : (xf.Basis * new Vector3(t[i * 4], t[i * 4 + 1], t[i * 4 + 2])).Normalized();
                    tan[(vo + i) * 4] = d.X; tan[(vo + i) * 4 + 1] = d.Y; tan[(vo + i) * 4 + 2] = d.Z; tan[(vo + i) * 4 + 3] = t[i * 4 + 3];
                }
            }
            if (col != null) Array.Copy(arrays[(int)Mesh.ArrayType.Color].AsColorArray(), 0, col, vo, count);
            if (uv != null) Array.Copy(arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array(), 0, uv, vo, count);
            if (uv2 != null) Array.Copy(arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array(), 0, uv2, vo, count);
            if (idx != null)
            {
                var src = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                // a mirroring transform flips the winding
                var flip = xf.Basis.Determinant() < 0;
                for (var i = 0; i < src.Length; i++)
                {
                    var j = flip ? i - i % 3 + (2 - i % 3) : i;
                    idx[io + i] = src[j] + vo;
                }
                io += src.Length;
            }
            vo += count;
        }

        using var merged = new Godot.Collections.Array();
        merged.Resize((int)Mesh.ArrayType.Max);
        merged[(int)Mesh.ArrayType.Vertex] = pos;
        if (norm != null) merged[(int)Mesh.ArrayType.Normal] = norm;
        if (tan != null) merged[(int)Mesh.ArrayType.Tangent] = tan;
        if (col != null) merged[(int)Mesh.ArrayType.Color] = col;
        if (uv != null) merged[(int)Mesh.ArrayType.TexUV] = uv;
        if (uv2 != null) merged[(int)Mesh.ArrayType.TexUV2] = uv2;
        if (idx != null) merged[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, merged);
        var first = list[0].Node;
        return new MeshInstance3D
        {
            Name = $"merged_{key.Cx}_{key.Cz}",
            Mesh = mesh,
            MaterialOverride = first.MaterialOverride,
            CastShadow = first.CastShadow,
        };
    }
}
