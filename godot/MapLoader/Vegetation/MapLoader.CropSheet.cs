using Godot;
using Drycastle.MapLoader.Trees;
using Drycastle.MapLoader.Vegetation;

namespace Drycastle.MapLoader;

/// <summary>
/// The far look of crop fields (#367): no plant is drawn beyond the plants' range (<see cref="CropSettings.PlantRangeM"/>), yet a field must look planted to the
/// horizon. Per chunk and plant kind one flat mesh, the sheet, lies over the planted ground (cells of <see cref="CropSettings.SheetCellM"/> that hold plants,
/// <see cref="CropSheetGrid"/>), a little above it, with a tiling picture of the crop seen from above. The picture is painted here at load, on the CPU, from the
/// kind's own plant models scattered at the field's own density (<see cref="PaintSheet"/>), so near the plants' range it shows the very plants that stand there
/// and far away its mipmaps turn into a tint of their colour. crop_sheet.gdshader blends it in over the band where the plants thin out. Cosmetic: no collision.
/// A harvester's swath and crushed tracks do not show on it.
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>One triangle of a plant model as the painter sees it: its corners (model frame, y up) and its colour (linear).</summary>
    readonly record struct PaintTri(Vector3 A, Vector3 B, Vector3 C, Vector3 Colour);

    sealed record PaintModel(PaintTri[] Tris, float Height);

    readonly Dictionary<string, List<PaintModel>> _cropPaint = new(); // plant kind -> its models, read before their materials are swapped
    readonly List<MeshInstance3D> _cropSheets = new();

    /// <summary>The sheets built by the last load: one per chunk and plant kind.</summary>
    public IReadOnlyList<MeshInstance3D> CropSheets => _cropSheets;

    void FreeCropSheets()
    {
        foreach (var n in _cropSheets)
            if (GodotObject.IsInstanceValid(n)) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
        _cropSheets.Clear();
        _cropPaint.Clear();
    }

    /// <summary>A plant mesh as triangles with a colour each: the material's albedo, times its texture at the triangle's middle, times the vertex colours where the material uses them.</summary>
    static PaintModel PaintModelOf(Mesh mesh)
    {
        var tris = new List<PaintTri>();
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is not BaseMaterial3D mat) continue;
            var arrays = mesh.SurfaceGetArrays(s);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var index = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.PackedInt32Array ? arrays[(int)Mesh.ArrayType.Index].AsInt32Array() : Array.Empty<int>();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.PackedVector2Array ? arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array() : null;
            var cols = mat.VertexColorUseAsAlbedo && arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.PackedColorArray ? arrays[(int)Mesh.ArrayType.Color].AsColorArray() : null;
            Image? img = null;
            if (uvs != null && mat.AlbedoTexture?.GetImage() is { } source && !source.IsEmpty())
            {
                img = source;
                if (img.IsCompressed() && img.Decompress() != Error.Ok) img = null;
            }
            var albedo = mat.AlbedoColor.SrgbToLinear();
            var count = index.Length > 0 ? index.Length : verts.Length;
            for (var k = 0; k + 2 < count; k += 3)
            {
                int a = index.Length > 0 ? index[k] : k, b = index.Length > 0 ? index[k + 1] : k + 1, c = index.Length > 0 ? index[k + 2] : k + 2;
                var colour = new Vector3(albedo.R, albedo.G, albedo.B);
                if (img != null)
                {
                    var uv = (uvs![a] + uvs[b] + uvs[c]) / 3f;
                    var px = img.GetPixel(Mathf.PosMod((int)(uv.X * img.GetWidth()), img.GetWidth()), Mathf.PosMod((int)(uv.Y * img.GetHeight()), img.GetHeight())).SrgbToLinear();
                    colour *= new Vector3(px.R, px.G, px.B);
                }
                if (cols != null && c < cols.Length)
                {
                    var vc = new Color((cols[a].R + cols[b].R + cols[c].R) / 3f, (cols[a].G + cols[b].G + cols[c].G) / 3f, (cols[a].B + cols[b].B + cols[c].B) / 3f);
                    if (mat.VertexColorIsSrgb) vc = vc.SrgbToLinear();
                    colour *= new Vector3(vc.R, vc.G, vc.B);
                }
                tris.Add(new PaintTri(verts[a], verts[b], verts[c], colour));
            }
        }
        return new PaintModel(tris.ToArray(), Mathf.Max(mesh.GetAabb().End.Y, 0.05f));
    }

    /// <summary>
    /// The picture of a crop seen from straight above: <paramref name="perM2"/> plants per square metre of the models, each turned and sized by chance, on a square of
    /// <see cref="CropSettings.SheetTileM"/> that repeats (a plant over the edge comes back in on the other side). The highest triangle wins a pixel; lower parts are
    /// darker, as in a standing crop. Alpha is where a plant is; the colour of the gaps is the plants' mean, so the mipmaps do not darken. Painted at twice the size and halved.
    /// </summary>
    static ImageTexture PaintSheet(List<PaintModel> models, float perM2, int seed)
    {
        var n = CropSettings.SheetPx * 2;
        var tileM = CropSettings.SheetTileM;
        var pxPerM = n / tileM;
        var top = new float[n * n];
        Array.Fill(top, float.MinValue);
        var rgb = new Vector3[n * n];
        var tallest = models.Max(m => m.Height) * 1.25f;
        var rng = new Random(seed);
        var plants = Math.Max(1, (int)MathF.Round(perM2 * tileM * tileM));
        for (var p = 0; p < plants; p++)
        {
            var model = models[rng.Next(models.Count)];
            float ox = (float)rng.NextDouble() * tileM, oz = (float)rng.NextDouble() * tileM;
            var turn = (float)rng.NextDouble() * Mathf.Tau;
            var size = 0.85f + (float)rng.NextDouble() * 0.4f;
            float cos = Mathf.Cos(turn) * size, sin = Mathf.Sin(turn) * size;
            foreach (var t in model.Tris)
            {
                // to pixels: x, z on the ground, y the height
                Vector3 At(Vector3 v) => new(((v.X * cos + v.Z * sin) + ox) * pxPerM, v.Y * size, ((-v.X * sin + v.Z * cos) + oz) * pxPerM);
                Vector3 a = At(t.A), b = At(t.B), c = At(t.C);
                var area = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
                if (Mathf.Abs(area) < 1e-6f) continue;
                int x0 = Mathf.FloorToInt(Mathf.Min(a.X, Mathf.Min(b.X, c.X))), x1 = Mathf.CeilToInt(Mathf.Max(a.X, Mathf.Max(b.X, c.X)));
                int z0 = Mathf.FloorToInt(Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z))), z1 = Mathf.CeilToInt(Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z)));
                for (var z = z0; z <= z1; z++)
                    for (var x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, pz = z + 0.5f;
                        var wa = ((b.X - px) * (c.Z - pz) - (b.Z - pz) * (c.X - px)) / area;
                        var wb = ((c.X - px) * (a.Z - pz) - (c.Z - pz) * (a.X - px)) / area;
                        var wc = 1f - wa - wb;
                        if (wa < 0f || wb < 0f || wc < 0f) continue;
                        var h = wa * a.Y + wb * b.Y + wc * c.Y;
                        var at = Mathf.PosMod(z, n) * n + Mathf.PosMod(x, n);
                        if (h <= top[at]) continue;
                        top[at] = h;
                        rgb[at] = t.Colour * Mathf.Lerp(0.65f, 1f, Mathf.Clamp(h / tallest, 0f, 1f));
                    }
            }
        }

        var mean = Vector3.Zero; var covered = 0;
        for (var i = 0; i < top.Length; i++)
            if (top[i] > float.MinValue) { mean += rgb[i]; covered++; }
        mean = covered > 0 ? mean / covered : new Vector3(0.2f, 0.3f, 0.1f);
        var bytes = new byte[n * n * 4];
        for (var i = 0; i < top.Length; i++)
        {
            var on = top[i] > float.MinValue;
            var c = new Color((on ? rgb[i] : mean).X, (on ? rgb[i] : mean).Y, (on ? rgb[i] : mean).Z).LinearToSrgb();
            bytes[i * 4] = (byte)Mathf.Clamp(Mathf.RoundToInt(c.R * 255f), 0, 255);
            bytes[i * 4 + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(c.G * 255f), 0, 255);
            bytes[i * 4 + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(c.B * 255f), 0, 255);
            bytes[i * 4 + 3] = on ? (byte)255 : (byte)0;
        }
        var img = Image.CreateFromData(n, n, false, Image.Format.Rgba8, bytes);
        img.ShrinkX2();
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Builds the sheet of every chunk and plant kind: one picture and material per kind, one mesh per chunk.</summary>
    void BuildCropSheets(List<(Node3D Parent, CropGroup Group)> groups)
    {
        if (groups.Count == 0) return;
        var cellM = CropSettings.SheetCellM;
        var grids = groups.Select(g => (g.Parent, g.Group, Grid: new CropSheetGrid(g.Group.Instances, cellM))).ToList();
        var materials = new Dictionary<string, (ShaderMaterial Material, float Full, float LiftM)>();
        Shader? shader = null;
        foreach (var kind in grids.Select(g => g.Group.Kind).Distinct())
        {
            if (!_cropPaint.TryGetValue(kind, out var models) || models.Count == 0 || models.All(m => m.Tris.Length == 0)) continue;
            var full = CropSheetGrid.Full(grids.Where(g => g.Group.Kind == kind).SelectMany(g => g.Grid.Cells.Values).Select(c => c.Count));
            if (full <= 0f) continue;
            shader ??= TreeCutShaders.Load("crop_sheet.gdshader");
            var mat = new ShaderMaterial { Shader = shader, RenderPriority = -1 }; // under the other blended things (water, glass, smoke)
            mat.SetShaderParameter("albedo_tex", PaintSheet(models.Where(m => m.Tris.Length > 0).ToList(), full / (cellM * cellM), StableSeed(kind)));
            mat.SetShaderParameter("crop_cut", PlantCutParam());
            materials[kind] = (mat, full, Mathf.Min(models.Average(m => m.Height) * CropSettings.SheetLift, CropSettings.SheetLiftMaxM));
        }

        foreach (var (parent, group, grid) in grids)
        {
            if (!materials.TryGetValue(group.Kind, out var look)) continue;
            var first = grid.Cells.Keys.First();
            var origin = ToGodot(first.X * cellM, first.Y * cellM, grid.Cells[first].ZSum / grid.Cells[first].Count);
            // the picture repeats every tile: count the texture from a tile corner near the chunk, so the coordinates stay small
            float u0 = Mathf.Floor(first.X * cellM / CropSettings.SheetTileM) * CropSettings.SheetTileM, v0 = Mathf.Floor(first.Y * cellM / CropSettings.SheetTileM) * CropSettings.SheetTileM;
            var corners = new Dictionary<(int, int), int>();
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var cols = new List<Color>(); var index = new List<int>();
            int Corner(int x, int y)
            {
                if (corners.TryGetValue((x, y), out var at)) return at;
                var (z, share) = grid.Corner(x, y, look.Full);
                verts.Add(ToGodot(x * cellM, y * cellM, z + look.LiftM) - origin);
                uvs.Add(new Vector2((x * cellM - u0) / CropSettings.SheetTileM, (y * cellM - v0) / CropSettings.SheetTileM));
                cols.Add(new Color(1f, 1f, 1f, share));
                return corners[(x, y)] = verts.Count - 1;
            }
            foreach (var (x, y) in grid.Cells.Keys)
            {
                int a = Corner(x, y), b = Corner(x + 1, y), c = Corner(x + 1, y + 1), d = Corner(x, y + 1);
                index.Add(a); index.Add(b); index.Add(c);
                index.Add(a); index.Add(c); index.Add(d);
            }
            var normals = new Vector3[verts.Count];
            Array.Fill(normals, Vector3.Up);
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = cols.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = index.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, look.Material);
            var node = new MeshInstance3D
            {
                Name = $"crop_{group.Kind}_sheet",
                Mesh = mesh,
                Position = origin,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            parent.AddChild(node);
            _cropSheets.Add(node);
        }
    }

    /// <summary>A seed from a kind's name that is the same on every run (string hashes are not).</summary>
    static int StableSeed(string kind)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var ch in kind) h = (h ^ ch) * 16777619u;
            return (int)h;
        }
    }
}
