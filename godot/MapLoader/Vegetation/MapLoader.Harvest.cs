using System.Text.Json;
using Godot;
using Drycastle.MapLoader.Vegetation;

namespace Drycastle.MapLoader;

/// <summary>
/// The harvester event's side of the crop fields (#331). The server reads the positions of the fields' own hay bales (<see cref="CropBales"/>, from crops.json, also on a headless load) so a shot can set
/// one alight. The client cuts the crop where the harvester has been (<see cref="CropCut"/>: plants in the swath are squashed to stubble, no plant is ever sent) and takes a burnt bale out of the field
/// (<see cref="CropHideArtifact"/>). All cosmetic, like the crops themselves.
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>One MultiMesh of the crop layer with what a cut needs: its instances (map rows, so world = <c>ToGodot</c>) and which are cut already.</summary>
    sealed record CropCell(MultiMesh Mesh, string Kind, bool Plant, IReadOnlyList<ClutterInstance> Rows, bool[] Cut)
    {
        /// <summary>The cell's rows in game x, z (a swath that misses this box skips the cell).</summary>
        public readonly Vector2 Min = new(Rows.Min(r => r.X), Rows.Min(r => -r.Y)), Max = new(Rows.Max(r => r.X), Rows.Max(r => -r.Y));
        /// <summary>Plants a vehicle laid flat (<see cref="MapLoader.CropCrush"/>); a cut one (<see cref="Cut"/>, stubble) is never crushed, and a cut overrides a crush.</summary>
        public readonly bool[] Crushed = new bool[Rows.Count];
    }

    readonly List<CropCell> _cropCells = new();

    /// <summary>The hay bales of the crop fields as game positions (x east, y up, z south): crops.json's <c>art_hay_bale*</c> rows. Empty without crops.json.</summary>
    public IReadOnlyList<Vector3> CropBales => _cropBales;
    readonly List<Vector3> _cropBales = new();

    /// <summary>Height a cut plant keeps (a fraction of its own): the stubble.</summary>
    public const float StubbleScale = 0.12f;

    void ReadCropBales(string mapDir, JsonElement manifest)
    {
        _cropBales.Clear();
        if (!manifest.TryGetProperty("files", out var files) || !files.TryGetProperty("crops", out var e) || e.ValueKind != JsonValueKind.String) return;
        var path = Path.Combine(mapDir, e.GetString()!);
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("chunks", out var chunks)) return;
            foreach (var chunk in chunks.EnumerateObject())
                foreach (var kind in chunk.Value.EnumerateObject())
                {
                    if (!kind.Name.StartsWith("art_hay_bale", StringComparison.Ordinal)) continue;
                    foreach (var row in kind.Value.EnumerateArray())
                        if (row.GetArrayLength() >= 3) _cropBales.Add(ToGodot(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle()));
                }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { Warn($"crops: bales not read: {ex.Message}"); }
    }

    /// <summary>
    /// Squashes the crop plants within <paramref name="halfWidth"/> of the segment <paramref name="a"/>-<paramref name="b"/> (game x, z) to stubble. Each plant is cut once. Returns how many it cut.
    /// The cells near the segment are looked at, not the whole field.
    /// </summary>
    public int CropCut(Vector2 a, Vector2 b, float halfWidth)
    {
        var cut = 0;
        var min = new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)) - Vector2.One * halfWidth;
        var max = new Vector2(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y)) + Vector2.One * halfWidth;
        foreach (var cell in _cropCells)
        {
            if (!cell.Plant || cell.Max.X < min.X || cell.Min.X > max.X || cell.Max.Y < min.Y || cell.Min.Y > max.Y || !GodotObject.IsInstanceValid(cell.Mesh)) continue;
            var rows = cell.Rows;
            for (var k = 0; k < rows.Count; k++)
            {
                if (cell.Cut[k]) continue;
                var r = rows[k];
                var x = r.X; var z = -r.Y; // map y north is game -z
                if (x < min.X || x > max.X || z < min.Y || z > max.Y) continue;
                if (DistanceToSegment(new Vector2(x, z), a, b) > halfWidth) continue;
                cell.Cut[k] = true;
                var basis = Basis.Identity.Rotated(Vector3.Up, r.Rot).Scaled(new Vector3(r.Scale, r.Scale * StubbleScale, r.Scale));
                var local = cell.Mesh.GetInstanceTransform(k).Origin;
                cell.Mesh.SetInstanceTransform(k, new Transform3D(basis, local));
                cut++;
            }
        }
        return cut;
    }

    /// <summary>Takes the crop artifacts whose kind starts with <paramref name="kindPrefix"/> within <paramref name="radius"/> of <paramref name="at"/> (game x, z) out of the field (a burnt bale). True when one was there.</summary>
    public bool CropHideArtifact(Vector3 at, float radius, string kindPrefix = "art_hay_bale")
    {
        var found = false;
        foreach (var cell in _cropCells)
        {
            if (cell.Plant || !cell.Kind.StartsWith(kindPrefix, StringComparison.Ordinal) || !GodotObject.IsInstanceValid(cell.Mesh)) continue;
            for (var k = 0; k < cell.Rows.Count; k++)
            {
                if (cell.Cut[k]) continue;
                var r = cell.Rows[k];
                if (new Vector2(r.X - at.X, -r.Y - at.Z).Length() > radius) continue;
                cell.Cut[k] = true;
                cell.Mesh.SetInstanceTransform(k, new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.001f), cell.Mesh.GetInstanceTransform(k).Origin));
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// Lays the plants in <paramref name="cells"/> (cell coordinates: game metres / <paramref name="cellM"/>) flat (#328): tilted over and squashed, not removed (a tall crop like corn lies down). Each plant once; a plant the
    /// harvester cut stays stubble. Only the MultiMesh cells the batch reaches are looked at. Returns how many plants it laid down. Cosmetic, no collision.
    /// </summary>
    public int CropCrush(HashSet<(int X, int Z)> cells, float cellM)
    {
        if (cells.Count == 0) return 0;
        int x0 = int.MaxValue, x1 = int.MinValue, z0 = int.MaxValue, z1 = int.MinValue;
        foreach (var (cx, cz) in cells) { x0 = Math.Min(x0, cx); x1 = Math.Max(x1, cx); z0 = Math.Min(z0, cz); z1 = Math.Max(z1, cz); }
        var min = new Vector2(x0 * cellM, z0 * cellM); var max = new Vector2((x1 + 1) * cellM, (z1 + 1) * cellM);
        var laid = 0;
        foreach (var cell in _cropCells)
        {
            if (!cell.Plant || cell.Max.X < min.X || cell.Min.X > max.X || cell.Max.Y < min.Y || cell.Min.Y > max.Y || !GodotObject.IsInstanceValid(cell.Mesh)) continue;
            var rows = cell.Rows;
            for (var k = 0; k < rows.Count; k++)
            {
                if (cell.Cut[k] || cell.Crushed[k]) continue;
                var r = rows[k];
                var x = r.X; var z = -r.Y;
                if (x < min.X || x > max.X || z < min.Y || z > max.Y) continue;
                if (!cells.Contains(((int)Mathf.Floor(x / cellM), (int)Mathf.Floor(z / cellM)))) continue;
                cell.Crushed[k] = true;
                var axis = new Vector3(Mathf.Cos(r.Rot * 7.3f), 0, Mathf.Sin(r.Rot * 7.3f)); // each leans its own way
                var basis = new Basis(axis, 1.25f) * Basis.Identity.Rotated(Vector3.Up, r.Rot).Scaled(new Vector3(r.Scale, r.Scale * 0.45f, r.Scale));
                cell.Mesh.SetInstanceTransform(k, new Transform3D(basis, cell.Mesh.GetInstanceTransform(k).Origin));
                laid++;
            }
        }
        return laid;
    }

    /// <summary>A new round: every plant that was cut or crushed stands again and the ground marks go (the server forgot them too). Artifacts (bales) are not touched.</summary>
    public void CropReset()
    {
        foreach (var cell in _cropCells)
        {
            if (!cell.Plant || !GodotObject.IsInstanceValid(cell.Mesh)) continue;
            for (var k = 0; k < cell.Rows.Count; k++)
            {
                if (!cell.Cut[k] && !cell.Crushed[k]) continue;
                var r = cell.Rows[k];
                cell.Cut[k] = false; cell.Crushed[k] = false;
                cell.Mesh.SetInstanceTransform(k, new Transform3D(Basis.Identity.Rotated(Vector3.Up, r.Rot).Scaled(new Vector3(r.Scale, r.Scale, r.Scale)), cell.Mesh.GetInstanceTransform(k).Origin));
            }
        }
        foreach (var chunk in _markChunks.Values)
            if (chunk.Node != null && GodotObject.IsInstanceValid(chunk.Node)) chunk.Node.QueueFree();
        _markChunks.Clear();
    }

    // ---- the flattened-ground strips (seen from beyond the plants' range) ----

    /// <summary>Cells per side of one mark chunk: a chunk is one draped mesh, rebuilt when a cell is added to it.</summary>
    const int MarkChunkCells = 32;
    /// <summary>The marks float this far over the terrain (patches float 0.02, roads 0.05): over the field's own ground, under the plants' feet.</summary>
    const float MarkLiftM = 0.06f;

    sealed class MarkChunk
    {
        public readonly HashSet<(int X, int Z)> Cells = new();
        public MeshInstance3D? Node;
        public bool Dirty;
    }

    readonly Dictionary<(int X, int Z), MarkChunk> _markChunks = new();
    /// <summary>Terrain heights at the lattice points (corners and cell middles, in half cells) so neighbouring cells share their corners exactly: no step, no gap.</summary>
    readonly Dictionary<(int X, int Z), float> _markHeights = new();
    Node3D? _markRoot;
    ShaderMaterial? _markMaterial;

    /// <summary>
    /// A darker patch on the ground for each crushed cell (<paramref name="centres"/>: game x, z of the cell's middle), so the mark reads from far beyond the plants' visibility range. The cells of a 32 m chunk are one mesh that
    /// follows the terrain: every cell is its own quad (four triangles round the middle's height) on corners shared with its neighbours, with no overlap, so an intersection is no darker and nothing leaves a gap. The
    /// material pulls the mark a hair toward the camera so the ground does not win the depth test at a distance. Returns how many cells it shows.
    /// </summary>
    public int CropMarkGround(IReadOnlyList<Vector2> centres, float cellM)
    {
        if (centres.Count == 0) return MarkCellCount();
        foreach (var c in centres)
        {
            var cell = ((int)Mathf.Floor(c.X / cellM), (int)Mathf.Floor(c.Y / cellM));
            var key = (FloorDiv(cell.Item1, MarkChunkCells), FloorDiv(cell.Item2, MarkChunkCells));
            if (!_markChunks.TryGetValue(key, out var chunk)) _markChunks[key] = chunk = new MarkChunk();
            if (chunk.Cells.Add(cell)) chunk.Dirty = true;
        }
        foreach (var chunk in _markChunks.Values)
            if (chunk.Dirty) RebuildMarkChunk(chunk, cellM);
        return MarkCellCount();
    }

    int MarkCellCount() { var n = 0; foreach (var c in _markChunks.Values) n += c.Cells.Count; return n; }

    static int FloorDiv(int a, int b) => (int)Mathf.Floor((float)a / b);

    float MarkHeight(int hx, int hz, float cellM)
    {
        if (_markHeights.TryGetValue((hx, hz), out var h)) return h;
        float x = hx * cellM * 0.5f, z = hz * cellM * 0.5f;
        h = GroundHeight(x, z);
        if (float.IsNaN(h)) h = GroundHeightOrMean(x, z);
        return _markHeights[(hx, hz)] = h + MarkLiftM;
    }

    void RebuildMarkChunk(MarkChunk chunk, float cellM)
    {
        chunk.Dirty = false;
        var verts = new List<Vector3>(chunk.Cells.Count * 5);
        var idx = new List<int>(chunk.Cells.Count * 12);
        foreach (var (cx, cz) in chunk.Cells)
        {
            // half-cell lattice: corners at even, the middle at odd
            int x0 = cx * 2, z0 = cz * 2;
            float xa = cx * cellM, xb = (cx + 1) * cellM, za = cz * cellM, zb = (cz + 1) * cellM;
            var b = verts.Count;
            verts.Add(new Vector3(xa, MarkHeight(x0, z0, cellM), za));
            verts.Add(new Vector3(xb, MarkHeight(x0 + 2, z0, cellM), za));
            verts.Add(new Vector3(xb, MarkHeight(x0 + 2, z0 + 2, cellM), zb));
            verts.Add(new Vector3(xa, MarkHeight(x0, z0 + 2, cellM), zb));
            verts.Add(new Vector3((xa + xb) * 0.5f, MarkHeight(x0 + 1, z0 + 1, cellM), (za + zb) * 0.5f));
            idx.AddRange(new[] { b, b + 4, b + 1, b + 1, b + 4, b + 2, b + 2, b + 4, b + 3, b + 3, b + 4, b });
        }
        var normals = new Vector3[verts.Count];
        System.Array.Fill(normals, Vector3.Up);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, MarkMaterial());
        if (chunk.Node == null || !GodotObject.IsInstanceValid(chunk.Node))
        {
            if (_markRoot == null || !GodotObject.IsInstanceValid(_markRoot)) { _markRoot = new Node3D { Name = "crop_crush_marks" }; Root.AddChild(_markRoot); }
            chunk.Node = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _markRoot.AddChild(chunk.Node);
        }
        chunk.Node.Mesh = mesh;
    }

    ShaderMaterial MarkMaterial()
    {
        if (_markMaterial != null) return _markMaterial;
        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode blend_mix, depth_draw_never, cull_disabled, skip_vertex_transform;
                uniform vec4 tint : source_color = vec4(0.16, 0.14, 0.07, 0.55);
                void vertex() {
                    vec3 v = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    // toward the camera by a sliver that grows with distance: the ground never wins the depth test, the plants and cars still do
                    v -= normalize(v) * (0.04 + length(v) * 0.003);
                    VERTEX = v;
                    NORMAL = normalize((MODELVIEW_NORMAL_MATRIX * NORMAL));
                }
                void fragment() {
                    ALBEDO = tint.rgb;
                    ALPHA = tint.a;
                    ROUGHNESS = 1.0;
                }
                """,
        };
        return _markMaterial = new ShaderMaterial { Shader = shader, RenderPriority = 1 };
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var l2 = ab.LengthSquared();
        var t = l2 < 1e-9f ? 0f : Mathf.Clamp((p - a).Dot(ab) / l2, 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }
}
