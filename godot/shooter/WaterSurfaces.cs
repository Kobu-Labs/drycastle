using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The visible water: one flat triangulated mesh per water body at its surface height, drawn two-sided with the
/// underside tinted. The shader (water_surface.gdshader) adds a cheap ripple, fades the edge at the shore from the
/// scene depth and turns the ripple off with reduce motion. Built once when the map loads (client only).
/// Holes in a body (islands) are not cut out: the outer ring is filled. See the resolution note on #142.
/// </summary>
public partial class WaterSurfaces : Node3D
{
    public const string ShaderPath = "res://shaders/water_surface.gdshader";

    readonly ShaderMaterial _mat = new();

    /// <summary>Adds the surfaces of <paramref name="water"/> under <paramref name="parent"/>. Nothing is built for a dry map or headless runs.</summary>
    public static WaterSurfaces? Attach(Node parent, Water water)
    {
        if (water.Bodies.Count == 0 || DisplayServer.GetName() == "headless") return null;
        var node = new WaterSurfaces { Name = "WaterSurfaces" };
        parent.AddChild(node);
        node.Build(water);
        return node;
    }

    void Build(Water water)
    {
        _mat.Shader = GD.Load<Shader>(ShaderPath);
        ApplyMotion();
        GameSettings.Changed += ApplyMotion;
        foreach (var body in water.Bodies)
        {
            var mesh = MeshFor(body);
            if (mesh == null) { GD.PrintErr($"water: body {body.Id} has no triangulable outline, not drawn"); continue; }
            AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = _mat, Name = $"Water{body.Id}", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
    }

    public override void _ExitTree() => GameSettings.Changed -= ApplyMotion;

    void ApplyMotion() => _mat.SetShaderParameter("ripple_amp", GameSettings.Current.ReduceMotion ? 0f : 0.05f);

    /// <summary>A flat fan-triangulated mesh of the body's outer ring at its surface height. Null when Godot cannot triangulate the outline.</summary>
    static ArrayMesh? MeshFor(WaterBody body)
    {
        var ring = body.Rings[0];
        var outline = new Vector2[ring.Length];
        for (var i = 0; i < ring.Length; i++) outline[i] = new Vector2(ring[i].X, ring[i].Y);
        var idx = Geometry2D.TriangulatePolygon(outline);
        if (idx.Length < 3) return null;
        var verts = new Vector3[ring.Length];
        var normals = new Vector3[ring.Length];
        for (var i = 0; i < ring.Length; i++)
        {
            verts[i] = new Vector3(ring[i].X, body.SurfaceY, ring[i].Y);
            normals[i] = Vector3.Up;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
