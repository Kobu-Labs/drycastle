using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The visible water: one mesh per water body at its surface height, drawn two-sided (the underside darker). Each body
/// has its own material with its palette (<see cref="WaterPalettes"/>) and wave amplitude; the shader
/// (water_surface.gdshader) does the depth colour, shore foam, refraction, reflection and the waves. The mesh is a
/// triangulated grid inside the outline (a 2.5 m cell), so the waves have vertices to move. Built once when the map
/// loads (client only). Holes in a body (islands) are cut out of the grid. Reduce motion freezes the waves and the
/// normal maps (<see cref="WaveClock"/> stays 0, the amplitude 0).
/// </summary>
public partial class WaterSurfaces : Node3D
{
    public const string ShaderPath = "res://shaders/water_surface.gdshader";
    /// <summary>Grid spacing of the surface mesh (m): the waves are about 1.5 m and longer, so this is enough.</summary>
    const float GridCell = 2.5f;

    readonly List<(ShaderMaterial Mat, WaterBody Body)> _surfaces = new();

    /// <summary>The clock the waves run on, in seconds: the shader (wave_time) and the boats' bob read the same one. Frozen at 0 with reduce motion.</summary>
    public static double WaveClock => GameSettings.Current.ReduceMotion ? 0.0 : Time.GetTicksMsec() / 1000.0;

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
        var shader = GD.Load<Shader>(ShaderPath);
        var noise = WaterNoiseTexture.Get();
        foreach (var body in water.Bodies)
        {
            var mesh = MeshFor(body);
            if (mesh == null) { GD.PrintErr($"water: body {body.Id} has no triangulable outline, not drawn"); continue; }
            var pal = WaterPalettes.For(body);
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("shallow_colour", pal.Shallow);
            mat.SetShaderParameter("deep_colour", pal.Deep);
            mat.SetShaderParameter("murk_density", pal.MurkDensity);
            mat.SetShaderParameter("flow", pal.Flow);
            mat.SetShaderParameter("ripple_tex", noise);
            mat.SetShaderParameter("shore_fade_m", UnderwaterFx.ShoreFadeMetres);
            _surfaces.Add((mat, body));
            AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Name = $"Water{body.Id}", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        ApplyMotion();
        GameSettings.Changed += ApplyMotion;
    }

    public override void _ExitTree() => GameSettings.Changed -= ApplyMotion;

    public override void _Process(double delta)
    {
        var t = (float)WaveClock;
        foreach (var (mat, _) in _surfaces) mat.SetShaderParameter("wave_time", t);
    }

    /// <summary>Wave amplitude per body (0 with reduce motion, so the surfaces are flat and still).</summary>
    void ApplyMotion()
    {
        foreach (var (mat, body) in _surfaces) mat.SetShaderParameter("wave_amp", WaterPalettes.WaveAmplitude(body));
    }

    /// <summary>The ring's points without the closing repeat of its first point (a duplicate breaks the triangulation).</summary>
    static void AddRingPoints(List<Vector2> into, Vector2[] ring)
    {
        var n = ring.Length;
        if (n > 1 && ring[0] == ring[n - 1]) n--;
        for (var i = 0; i < n; i++) into.Add(ring[i]);
    }

    /// <summary>
    /// A grid mesh of the body at its surface height: the outline and the holes, plus interior grid points, Delaunay
    /// triangulated; triangles whose centre lies outside the body are dropped. Falls back to a fan of the outer ring
    /// when that gives nothing. Null when Godot cannot triangulate the outline.
    /// </summary>
    static ArrayMesh? MeshFor(WaterBody body)
    {
        var points = new List<Vector2>();
        foreach (var ring in body.Rings) AddRingPoints(points, ring);
        for (var x = body.MinX + GridCell / 2; x < body.MaxX; x += GridCell)
            for (var z = body.MinZ + GridCell / 2; z < body.MaxZ; z += GridCell)
                if (body.Contains(x, z) && body.ShoreDistance(x, z) > GridCell * 0.5f) points.Add(new Vector2(x, z));
        var pts = points.ToArray();
        var idx = new List<int>();
        var tri = Geometry2D.TriangulateDelaunay(pts);
        for (var i = 0; i + 2 < tri.Length; i += 3)
        {
            var a = pts[tri[i]]; var b = pts[tri[i + 1]]; var c = pts[tri[i + 2]];
            if (Mathf.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) < 1e-6f) continue; // degenerate
            if (!body.Contains((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f)) continue;
            idx.Add(tri[i]); idx.Add(tri[i + 1]); idx.Add(tri[i + 2]);
        }
        if (idx.Count < 3)
        {
            var ring = body.Rings[0];
            pts = ring;
            idx.Clear();
            idx.AddRange(Geometry2D.TriangulatePolygon(ring));
            if (idx.Count < 3) return null;
        }
        var verts = new Vector3[pts.Length];
        var normals = new Vector3[pts.Length];
        for (var i = 0; i < pts.Length; i++)
        {
            verts[i] = new Vector3(pts[i].X, body.SurfaceY, pts[i].Y);
            normals[i] = Vector3.Up;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
