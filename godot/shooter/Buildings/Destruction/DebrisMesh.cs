using Godot;
using Drycastle.ShooterCore.Buildings.Destruction;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Buildings.Destruction;

/// <summary>The triangles of one debris piece in its body's local frame (metres, <see cref="DebrisRules.LocalOf"/> minus the centroid), unindexed, 6 vertices a face.</summary>
public sealed class DebrisArrays
{
    public required Vector3[] Vertices { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Color[] Colours { get; init; }
}

/// <summary>
/// Builds the look and the collider of a debris piece (WP-G) from the house mesher: every face of its cells (<see cref="HouseMesher.Surface(HouseGrid, ReadOnlySpan{int}, List{Quad})"/>)
/// with the palette's vertex colours, and its greedy box cover (<see cref="HouseMesher.Boxes"/>) when that is at most <see cref="DebrisRules.MaxBoxes"/> boxes, else one box
/// over its bounds. Runs when a part falls or breaks, never per frame.
/// </summary>
public static class DebrisMesh
{
    static StandardMaterial3D? _material;

    /// <summary>The flat vertex-colour look of the map (the same as MapLoader's flat material), shared by every piece and rubble mesh.</summary>
    public static StandardMaterial3D Material => _material ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        AlbedoColor = Colors.White,
        Roughness = 1f,
        Metallic = 0f,
        MetallicSpecular = 0f,
    };

    public static Vector3 Gd(NVec3 v) => new(v.X, v.Y, v.Z);
    public static NVec3 Nv(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The faces of <paramref name="cells"/> around <paramref name="centroidGrid"/>; <paramref name="quads"/> is scratch.</summary>
    public static DebrisArrays Faces(HouseGrid g, int[] cells, NVec3 centroidGrid, List<Quad> quads)
    {
        quads.Clear();
        HouseMesher.Surface(g, cells, quads);
        var n = quads.Count * 6;
        var v = new Vector3[n];
        var nn = new Vector3[n];
        var c = new Color[n];
        var pal = g.Palette.Span;
        var cell = g.Cell;
        var origin = DebrisRules.LocalOf(centroidGrid, cell);
        Vector3 L(NVec3 p) => Gd(DebrisRules.LocalOf(p, cell) - origin);
        var k = 0;
        foreach (var q in quads)
        {
            Vector3 a = L(q.Corner), b = L(q.Corner + q.U), cc = L(q.Corner + q.U + q.V), d = L(q.Corner + q.V);
            var normal = Gd(DebrisRules.LocalOf(q.Normal, 1f));
            if (normal.LengthSquared() > 0) normal = normal.Normalized();
            var e = q.Palette < pal.Length ? pal[q.Palette] : default;
            var col = Color.Color8(e.R, e.G, e.B);
            // Godot's front faces are clockwise seen from the front: cross(b - a, c - a) points away from the normal
            var ccw = NVec3.Dot(NVec3.Cross(q.U, q.V), q.Normal) > 0;
            Span<Vector3> t = ccw ? [a, cc, b, a, d, cc] : [a, b, cc, a, cc, d];
            foreach (var p in t) { v[k] = p; nn[k] = normal; c[k] = col; k++; }
        }
        return new DebrisArrays { Vertices = v, Normals = nn, Colours = c };
    }

    /// <summary>A one-surface mesh of the triangles, in <see cref="Material"/>.</summary>
    public static ArrayMesh Mesh(DebrisArrays a) => Mesh(a.Vertices, a.Normals, a.Colours);

    public static ArrayMesh Mesh(Vector3[] v, Vector3[] n, Color[] c)
    {
        var mesh = new ArrayMesh();
        if (v.Length == 0) return mesh;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = v;
        arrays[(int)Godot.Mesh.ArrayType.Normal] = n;
        arrays[(int)Godot.Mesh.ArrayType.Color] = c;
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, Material);
        return mesh;
    }

    /// <summary>
    /// Adds the collider of <paramref name="cells"/> to <paramref name="body"/>, in the same local frame as <see cref="Faces"/>. The box cover is taken over a small grid
    /// holding only the piece (the mesher covers a house's live cells). <paramref name="boxes"/> is scratch.
    /// </summary>
    public static void Collider(CollisionObject3D body, HouseGrid g, int[] cells, NVec3 centroidGrid, List<(NVec3 Min, NVec3 Max)> boxes)
    {
        var (x0, y0, z0, x1, y1, z1) = DebrisRules.Bounds(g, cells);
        int sx = x1 - x0, sy = y1 - y0, sz = z1 - z0;
        var src = g.Cells.Span;
        var sub = new byte[sx * sy * sz];
        foreach (var i in cells)
        {
            var (x, y, z) = g.Coords(i);
            sub[(x - x0) + sx * ((y - y0) + sy * (z - z0))] = src[i];
        }
        boxes.Clear();
        HouseMesher.Boxes(new HouseCells(new HouseGrid(0, g.Id, NVec3.Zero, 0, g.Cell, sx, sy, sz, g.Palette, sub)), boxes);
        var offset = new NVec3(x0, y0, z0);
        if (boxes.Count is 0 or > DebrisRules.MaxBoxes)
        {
            AddBox(body, offset, new NVec3(x1, y1, z1), centroidGrid, g.Cell);
            return;
        }
        foreach (var (min, max) in boxes) AddBox(body, min + offset, max + offset, centroidGrid, g.Cell);
    }

    static void AddBox(CollisionObject3D body, NVec3 minGrid, NVec3 maxGrid, NVec3 centroidGrid, float cell)
    {
        var a = DebrisRules.LocalOf(minGrid, cell);
        var b = DebrisRules.LocalOf(maxGrid, cell);
        var lo = NVec3.Min(a, b);
        var hi = NVec3.Max(a, b);
        var size = NVec3.Max(hi - lo, new NVec3(0.05f));
        var centre = (lo + hi) * 0.5f - DebrisRules.LocalOf(centroidGrid, cell);
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Gd(size) }, Position = Gd(centre) });
    }
}
