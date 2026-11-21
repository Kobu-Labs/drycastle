using Godot;

namespace Drycastle.Shooter;

/// <summary>What <see cref="CrystalAssets.MakeBody"/> found in the crystal rock mesh: where its crystals end and which faces props can sit on.</summary>
public sealed class CrystalInfo
{
    /// <summary>Highest point of each crystal of the rock (model space).</summary>
    public readonly List<Vector3> Tips = new();
    /// <summary>Bounds of each crystal (model space).</summary>
    public readonly List<Aabb> Boxes = new();
    /// <summary>Points on the up-facing faces of the rock base (position, outward normal; model space).</summary>
    public readonly List<(Vector3 P, Vector3 N)> RockUp = new();
    /// <summary>One point on every face of the rock base (position, outward normal; model space).</summary>
    public readonly List<(Vector3 P, Vector3 N)> RockFaces = new();
    /// <summary>One point on every face of the crystals (position, outward normal, crystal index; model space).</summary>
    public readonly List<(Vector3 P, Vector3 N, int Island)> Facets = new();
}

/// <summary>
/// The crystal rock model and its atlas (private Synty pack, built by scripts/build_dungeon_realms_crystals.sh into assets-private/synty/glb/dungeon-realms/). Surface 0 of the model is the rock,
/// surface 1 the crystal. Without the pack (or without a display) a stand-in of the same two surfaces is built so the effects can still be tuned.
/// </summary>
public static class CrystalAssets
{
    /// <summary>What <see cref="MakeBody"/> found in the mesh it built last (the same for every crystal, the model is shared).</summary>
    public static CrystalInfo Info { get; private set; } = new();

    const string ModelId = "dungeon-realms/SM_Prop_Crystal_Rock_A_01";
    const string AtlasRel = "synty/glb/dungeon-realms/tex/Dungeons_2_Texture_01_A.png";

    static Texture2D? _atlas;
    static bool _atlasTried;

    /// <summary>The 2048 px atlas of the pack (null without it).</summary>
    public static Texture2D? Atlas()
    {
        if (_atlasTried) return _atlas;
        _atlasTried = true;
        var path = Path.Combine(GamePaths.PrivateAssets(), AtlasRel);
        if (!File.Exists(path)) { GD.PrintErr($"crystal atlas not found: {path} (stand-in colours)"); return null; }
        var img = Image.LoadFromFile(path);
        if (img == null) return null;
        if (img.GetWidth() > 1024) img.Resize(1024, 1024, Image.Interpolation.Bilinear);
        img.GenerateMipmaps();
        return _atlas = ImageTexture.CreateFromImage(img);
    }

    /// <summary>The rock + crystal mesh as a node with two surfaces (0 rock, 1 crystal), no materials worn yet.</summary>
    public static MeshInstance3D MakeBody()
    {
        var model = SyntyLib.Model(ModelId);
        var mi = model?.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().FirstOrDefault();
        if (mi?.Mesh is { } mesh && mesh.GetSurfaceCount() >= 2)
        {
            var body = new MeshInstance3D { Name = "Body", Mesh = Analyse(mesh) };
            model!.Free();
            return body;
        }
        model?.Free();
        GD.PrintErr("crystal model not available: building the stand-in");
        return new MeshInstance3D { Name = "Body", Mesh = Analyse(StandIn()) };
    }

    /// <summary>
    /// Finds the separate crystals of surface 1 (triangles that share a point form one crystal), writes each vertex's crystal number into UV2.x (UV2.y = how many) so a shader can tint every crystal on its own,
    /// and collects the tips and surface points that props, arcs and flies are placed on (<see cref="Info"/>). Surface 0 (the rock) is kept as it is.
    /// </summary>
    static ArrayMesh Analyse(Mesh src)
    {
        var info = new CrystalInfo();
        var rebuilt = new ArrayMesh();
        for (var s = 0; s < src.GetSurfaceCount(); s++)
        {
            var arr = src.SurfaceGetArrays(s);
            var v = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var nv = arr[(int)Mesh.ArrayType.Normal];
            var normals = nv.VariantType == Variant.Type.Nil ? null : nv.AsVector3Array();
            var iv = arr[(int)Mesh.ArrayType.Index];
            var idx = iv.VariantType == Variant.Type.Nil ? Enumerable.Range(0, v.Length).ToArray() : iv.AsInt32Array();

            Vector3 TriNormal(int a, int b, int c)
            {
                if (normals != null) return (normals[a] + normals[b] + normals[c]).Normalized();
                return (v[c] - v[a]).Cross(v[b] - v[a]).Normalized(); // Godot's front faces wind clockwise
            }

            if (s == 0)
            {
                for (var t = 0; t + 2 < idx.Length; t += 3)
                {
                    var n = TriNormal(idx[t], idx[t + 1], idx[t + 2]);
                    var mid = (v[idx[t]] + v[idx[t + 1]] + v[idx[t + 2]]) / 3f;
                    info.RockFaces.Add((mid, n));
                    if (n.Y > 0.4f) info.RockUp.Add((mid, n));
                }
                rebuilt.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
                continue;
            }

            // weld by position, then union the three points of every triangle
            var weld = new Dictionary<Vector3I, int>();
            var point = new int[v.Length];
            for (var i = 0; i < v.Length; i++)
            {
                var key = new Vector3I(Mathf.RoundToInt(v[i].X * 1000f), Mathf.RoundToInt(v[i].Y * 1000f), Mathf.RoundToInt(v[i].Z * 1000f));
                if (!weld.TryGetValue(key, out var id)) weld[key] = id = weld.Count;
                point[i] = id;
            }
            var parent = Enumerable.Range(0, weld.Count).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (var t = 0; t + 2 < idx.Length; t += 3)
            {
                var r0 = Find(point[idx[t]]);
                parent[Find(point[idx[t + 1]])] = r0;
                parent[Find(point[idx[t + 2]])] = r0;
            }
            var island = new Dictionary<int, int>();
            var of = new int[v.Length];
            for (var i = 0; i < v.Length; i++)
            {
                var r = Find(point[i]);
                if (!island.TryGetValue(r, out var n)) { island[r] = n = island.Count; info.Tips.Add(v[i]); info.Boxes.Add(new Aabb(v[i], Vector3.Zero)); }
                of[i] = n;
                if (v[i].Y > info.Tips[n].Y) info.Tips[n] = v[i];
                info.Boxes[n] = info.Boxes[n].Expand(v[i]);
            }
            var uv2 = new Vector2[v.Length];
            for (var i = 0; i < v.Length; i++) uv2[i] = new Vector2(of[i], island.Count);
            for (var t = 0; t + 2 < idx.Length; t += 3)
                info.Facets.Add(((v[idx[t]] + v[idx[t + 1]] + v[idx[t + 2]]) / 3f, TriNormal(idx[t], idx[t + 1], idx[t + 2]), of[idx[t]]));
            arr[(int)Mesh.ArrayType.TexUV2] = uv2;
            rebuilt.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        }
        Info = info;
        return rebuilt;
    }

    /// <summary>The rock of the pack in a tint (white = the atlas as drawn).</summary>
    public static StandardMaterial3D Rock(Color tint, float roughness) => new()
    {
        AlbedoTexture = Atlas(), AlbedoColor = tint, Roughness = roughness,
    };

    static ArrayMesh StandIn()
    {
        var rock = new SurfaceTool();
        rock.AppendFrom(new SphereMesh { Radius = 0.4f, Height = 0.5f, RadialSegments = 8, Rings = 4 }, 0, new Transform3D(Basis.Identity, new Vector3(0, 0.2f, 0)));
        var mesh = rock.Commit();
        var crystals = new SurfaceTool();
        var tilts = new (float x, float z, float h, float tx, float tz)[] { (0f, 0f, 1.1f, 0f, 0f), (0.18f, 0.08f, 0.7f, 18f, -12f), (-0.17f, -0.05f, 0.6f, -15f, 10f), (0.04f, -0.18f, 0.5f, 8f, 20f) };
        foreach (var (x, z, h, tx, tz) in tilts)
        {
            var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(tx), 0, Mathf.DegToRad(tz)));
            crystals.AppendFrom(new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.1f, Height = h, RadialSegments = 6 }, 0, new Transform3D(basis, new Vector3(x, 0.3f + h * 0.5f, z)));
        }
        crystals.Commit(mesh);
        return mesh;
    }
}
