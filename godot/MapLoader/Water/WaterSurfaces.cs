using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.MapLoader;

/// <summary>
/// The visible water: one mesh per water body at its surface height, a regular triangle grid the waves move.
/// Each body has its own material with its palette (<see cref="WaterPalettes"/>) and depth texture (the
/// body's depth grid from water.json); the shader (water_surface.gdshader) does the low-poly look per pixel: flat
/// depth bands, a hard foam strip at the bank and flat-shaded waves, lit by the scene's own sun. Built once when the
/// map loads (client only). Reduce motion freezes the waves where they are (<see cref="WaveClock"/> stays 0).
/// </summary>
public partial class WaterSurfaces : Node3D
{
    /// <summary>The shader is an embedded resource of this assembly (water_surface.gdshader beside this file), so the viewer and the shooter, which are separate Godot projects, draw the same water.</summary>
    const string ShaderResource = "water_surface.gdshader";
    /// <summary>Side of the surface mesh's cells (m): the triangles the waves move and the light shades flat.</summary>
    const float GridCell = 2.5f;

    readonly List<(ShaderMaterial Mat, WaterBody Body)> _surfaces = new();

    /// <summary>Whether reduce motion is on: freezes the waves and normal maps. The shooter points this at its settings; the viewer leaves it off.</summary>
    public static Func<bool> ReduceMotion { get; set; } = () => false;

    /// <summary>The clock the waves run on, in seconds: the shader (wave_time) and the boats' bob read the same one. Frozen at 0 with reduce motion.</summary>
    public static double WaveClock => ReduceMotion() ? 0.0 : Time.GetTicksMsec() / 1000.0;

    static Shader LoadShader()
    {
        var asm = typeof(WaterSurfaces).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ShaderResource, StringComparison.Ordinal))
                   ?? throw new FileNotFoundException($"embedded shader {ShaderResource} is missing from {asm.GetName().Name}");
        using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
        return new Shader { Code = reader.ReadToEnd() };
    }

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
        var shader = LoadShader();
        foreach (var body in water.Bodies)
        {
            var mesh = body.Grid != null ? GridMesh(body) : OutlineMesh(body);
            if (mesh == null) { GD.PrintErr($"water: body {body.Id} has no triangulable outline, not drawn"); continue; }
            var pal = WaterPalettes.For(body);
            // drawn before every other transparent thing, so ripple rings, splashes and glass over the water are not covered by it
            var mat = new ShaderMaterial { Shader = shader, RenderPriority = -1 };
            mat.SetShaderParameter("shallow_colour", pal.Shallow);
            mat.SetShaderParameter("deep_colour", pal.Deep);
            mat.SetShaderParameter("flow", pal.Flow);
            mat.SetShaderParameter("depth_tex", DepthTexture(body, out var origin, out var invSize));
            mat.SetShaderParameter("depth_origin", origin);
            mat.SetShaderParameter("depth_inv_size", invSize);
            mat.SetShaderParameter("wave_amp", WaterPalettes.WaveAmplitude(body));
            mat.SetShaderParameter("wave_full_m", WaterPalettes.WaveFullDepth);
            _surfaces.Add((mat, body));
            AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Name = $"Water{body.Id}", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 0.5f });
        }
    }

    public override void _Process(double delta)
    {
        var t = (float)WaveClock;
        foreach (var (mat, _) in _surfaces) mat.SetShaderParameter("wave_time", t);
    }

    static Vector2 ToGodot(System.Numerics.Vector2 p) => new(p.X, p.Y);

    /// <summary>The ring's points without the closing repeat of its first point (a duplicate breaks the triangulation).</summary>
    static Vector2[] RingPoints(System.Numerics.Vector2[] ring)
    {
        var n = ring.Length;
        if (n > 1 && ring[0] == ring[n - 1]) n--;
        var pts = new Vector2[n];
        for (var i = 0; i < n; i++) pts[i] = ToGodot(ring[i]);
        return pts;
    }

    /// <summary>
    /// The body's depth grid as a float texture (r: metres), one texel per cell, row 0 the southern row; the shader
    /// samples it by world position (<paramref name="origin"/> and <paramref name="invSize"/> map local metres to UV).
    /// A body without a grid (water.json version 1) gets one texel of its flat depth: no fade at the bank.
    /// </summary>
    static ImageTexture DepthTexture(WaterBody body, out Vector2 origin, out Vector2 invSize)
    {
        var g = body.Grid;
        var depths = g?.Depths ?? new[] { body.Depth };
        var bytes = new byte[depths.Length * sizeof(float)];
        Buffer.BlockCopy(depths, 0, bytes, 0, bytes.Length);
        origin = g == null ? Vector2.Zero : new Vector2(g.OriginX, g.OriginY);
        invSize = g == null ? Vector2.Zero : new Vector2(1f / (g.Cols * g.CellM), 1f / (g.Rows * g.CellM));
        return ImageTexture.CreateFromImage(Image.CreateFromData(g?.Cols ?? 1, g?.Rows ?? 1, false, Image.Format.Rf, bytes));
    }

    /// <summary>
    /// The body as a regular grid of <see cref="GridCell"/> squares, each two triangles, at its surface height: the
    /// vertices the shader's waves move. The grid is aligned to the world, and a cell is kept when a corner or its
    /// centre is in the body, so the mesh reaches a little past the outline; the shader does not draw that part (the
    /// depth is zero there). Null when no cell touches the body.
    /// </summary>
    static ArrayMesh? GridMesh(WaterBody body)
    {
        var i0 = Mathf.FloorToInt(body.MinX / GridCell);
        var j0 = Mathf.FloorToInt(body.MinZ / GridCell);
        var cols = Mathf.CeilToInt(body.MaxX / GridCell) - i0;
        var rows = Mathf.CeilToInt(body.MaxZ / GridCell) - j0;
        var stride = cols + 1;
        var inside = new bool[stride * (rows + 1)];
        for (var j = 0; j <= rows; j++)
            for (var i = 0; i <= cols; i++)
                inside[j * stride + i] = body.Contains((i0 + i) * GridCell, (j0 + j) * GridCell);
        var index = new int[inside.Length];
        Array.Fill(index, -1);
        var verts = new List<Vector3>();
        var idx = new List<int>();
        int Vertex(int i, int j)
        {
            ref var v = ref index[j * stride + i];
            if (v < 0) { v = verts.Count; verts.Add(new Vector3((i0 + i) * GridCell, body.SurfaceY, (j0 + j) * GridCell)); }
            return v;
        }
        for (var j = 0; j < rows; j++)
            for (var i = 0; i < cols; i++)
            {
                var k = j * stride + i;
                if (!(inside[k] || inside[k + 1] || inside[k + stride] || inside[k + stride + 1]
                      || body.Contains((i0 + i + 0.5f) * GridCell, (j0 + j + 0.5f) * GridCell))) continue;
                int a = Vertex(i, j), b = Vertex(i + 1, j), c = Vertex(i, j + 1), d = Vertex(i + 1, j + 1);
                // front faces look up (clockwise seen from above, x east and z south)
                idx.Add(a); idx.Add(b); idx.Add(c);
                idx.Add(b); idx.Add(d); idx.Add(c);
            }
        return idx.Count < 3 ? null : Build(verts.ToArray(), idx.ToArray());
    }

    /// <summary>
    /// For a body without a depth grid (water.json version 1): the outline triangulated, flat. A body with holes
    /// (islands) is Delaunay triangulated over its ring points and the triangles whose centre lies outside the body are
    /// dropped. Null when Godot cannot triangulate the outline.
    /// </summary>
    static ArrayMesh? OutlineMesh(WaterBody body)
    {
        Vector2[] pts;
        var idx = new List<int>();
        if (body.Rings.Length == 1)
        {
            pts = RingPoints(body.Rings[0]);
            idx.AddRange(Geometry2D.TriangulatePolygon(pts));
        }
        else
        {
            pts = body.Rings.SelectMany(RingPoints).ToArray();
            var tri = Geometry2D.TriangulateDelaunay(pts);
            for (var i = 0; i + 2 < tri.Length; i += 3)
            {
                var a = pts[tri[i]]; var b = pts[tri[i + 1]]; var c = pts[tri[i + 2]];
                if (!body.Contains((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f)) continue;
                idx.Add(tri[i]); idx.Add(tri[i + 1]); idx.Add(tri[i + 2]);
            }
        }
        if (idx.Count < 3) return null;
        // front faces look up: Godot's front is clockwise seen from the front, here seen from above (x east, z south)
        for (var i = 0; i + 2 < idx.Count; i += 3)
        {
            var a = pts[idx[i]]; var b = pts[idx[i + 1]]; var c = pts[idx[i + 2]];
            if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0f) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
        }
        return Build(pts.Select(q => new Vector3(q.X, body.SurfaceY, q.Y)).ToArray(), idx.ToArray());
    }

    static ArrayMesh Build(Vector3[] verts, int[] idx)
    {
        var normals = new Vector3[verts.Length];
        Array.Fill(normals, Vector3.Up);
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
