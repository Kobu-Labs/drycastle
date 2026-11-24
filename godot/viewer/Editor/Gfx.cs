using Godot;
using System.Collections.Generic;

namespace Drycastle.Viewer.Editor;

/// <summary>Small drawing helpers the editor tools share: flat unlit materials and hand-built meshes.</summary>
public static class Gfx
{
    public static StandardMaterial3D Unshaded(Color c, bool noDepth)
        => new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = noDepth };

    public static ArrayMesh Strip(List<Vector3> verts, List<int> idx, Mesh.PrimitiveType type = Mesh.PrimitiveType.Triangles)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(type, arrays);
        return mesh;
    }

    /// <summary>A flat ribbon of the given width per edge, split every 2 m so it follows the terrain; no collision.</summary>
    public static ArrayMesh Ribbon(Drycastle.MapLoader.MapLoader map, List<(Vector2 a, Vector2 b)> segs, float lift, float width)
    {
        var verts = new List<Vector3>();
        var idx = new List<int>();
        var half = width / 2f;
        Vector3 P(Vector2 v, Vector2 side) { var q = v + side; return new Vector3(q.X, map.GroundHeightOrMean(v.X, v.Y) + lift, q.Y); }
        foreach (var (a0, b0) in segs)
        {
            var dir = (b0 - a0).Normalized();
            var a = a0 - dir * half; var b = b0 + dir * half; // cover the corner joins
            var side = new Vector2(-dir.Y, dir.X) * half;
            var n = Mathf.Max(1, (int)Mathf.Ceil(a.DistanceTo(b) / 2f));
            for (var i = 0; i < n; i++)
            {
                var p = a.Lerp(b, (float)i / n); var q = a.Lerp(b, (float)(i + 1) / n);
                var k = verts.Count;
                verts.Add(P(p, side)); verts.Add(P(p, -side)); verts.Add(P(q, -side)); verts.Add(P(q, side));
                idx.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
            }
        }
        return Strip(verts, idx);
    }

    /// <summary>The node and every MeshInstance3D below it.</summary>
    public static IEnumerable<MeshInstance3D> Meshes(Node n)
    {
        if (n is MeshInstance3D m) yield return m;
        foreach (var c in n.GetChildren())
            foreach (var x in Meshes(c)) yield return x;
    }
}
