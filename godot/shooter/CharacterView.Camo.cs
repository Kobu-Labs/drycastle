using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// A character's look (tickets #140, #141 and the character layer): the body model, skin tone, headgear, camouflage suit, foliage and face paint.
/// The models are flat-colour surfaces named by material, so the look works per surface (<see cref="CamoBody"/> says which material is the suit,
/// the gear, the headgear or the hair): the suit surfaces get the suit shader (<c>camo_suit.gdshader</c>), the gear a dark tint of the suit colour,
/// <c>Skin</c> and <c>Face</c> the skin tone, the headgear stays as modelled, takes the suit pattern or is hidden. The foliage tufts are scattered on
/// the real surface of the suit, gear and headgear (so they fit every body) and ride on the bones, one MultiMesh per bone
/// (<c>camo_tuft.gdshader</c>), hidden beyond <see cref="TuftHideDistance"/>; the face paint is a Decal on the face plate.
/// Everything is derived from the spec (the scatter is seeded by it), so every client draws the same player the same way.
/// </summary>
public sealed partial class CharacterView
{
    public const string SuitShaderPath = "res://shaders/camo_suit.gdshader";
    public const string TuftShaderPath = "res://shaders/camo_tuft.gdshader";

    /// <summary>Beyond this distance (m) the tufts are not drawn: the cheap LOD for distant players.</summary>
    const float TuftHideDistance = 45f;

    /// <summary>Most tufts per body region at full density and one layer.</summary>
    const int TuftsPerRegionPerLayer = 24;

    /// <summary>Skin vertices below this height (rig units, bind pose) are the boots: they keep a dark share of the skin tone instead of the whole tone.</summary>
    const float BootTop = 0.2f;

    static Shader? _suitShader, _tuftShader, _hiddenShader;
    static readonly Dictionary<string, ArrayMesh> PreparedMeshes = new();

    CamoBody? _bodyDef;
    Dictionary<int, Transform3D> _bind = new();
    readonly List<Node> _camoNodes = new();

    /// <summary>Applies a player's look. A missing model (headless, primitive fallback) or a null data file does nothing; a look camo.json rejects leaves the previous one.</summary>
    public void SetCamo(CamoSpec spec, CamoFile? data)
    {
        if (data == null || spec.Validate(data) != null) return;
        var body = data.Bodies[spec.Body];
        if (body.Model != _modelPath && !Build(body.Model) && _model == null) return;
        if (_model == null || _mesh == null || _skeleton == null) return;
        _bodyDef = body;
        ClearCamo();
        ApplySurfaces(spec, data, body);
        BuildTufts(spec, data, body);
        BuildFacePaint(spec);
    }

    void ClearCamo()
    {
        foreach (var n in _camoNodes) if (GodotObject.IsInstanceValid(n)) n.QueueFree();
        _camoNodes.Clear();
        if (_mesh != null && GodotObject.IsInstanceValid(_mesh))
            for (var i = 0; i < _mesh.GetSurfaceOverrideMaterialCount(); i++) _mesh.SetSurfaceOverrideMaterial(i, null);
    }

    /// <summary>The surface name (its material's name, kept from the import) of surface <paramref name="i"/>.</summary>
    static string SurfaceName(ArrayMesh m, int i) => m.SurfaceGetName(i);

    /// <summary>
    /// Replaces the imported mesh with a copy whose skin surface carries vertex colours (white, and a dark grey for the boots), cached per model.
    /// Surface names and materials are kept, so surfaces can be found by material name. Skin weights are copied as they are.
    /// </summary>
    static void PrepareMesh(MeshInstance3D mi, string path)
    {
        if (mi.Mesh is not Mesh src) return;
        if (!PreparedMeshes.TryGetValue(path, out var prepared))
        {
            prepared = new ArrayMesh();
            for (var i = 0; i < src.GetSurfaceCount(); i++)
            {
                var mat = src.SurfaceGetMaterial(i);
                var name = mat?.ResourceName ?? "";
                var arrays = src.SurfaceGetArrays(i);
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                if (name == "Skin")
                {
                    var colours = new Color[verts.Length];
                    for (var k = 0; k < verts.Length; k++) colours[k] = verts[k].Y < BootTop ? new Color(0.16f, 0.16f, 0.16f) : Colors.White;
                    arrays[(int)Mesh.ArrayType.Color] = colours;
                }
                var flags = (Mesh.ArrayFormat)0;
                if (arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil && verts.Length > 0 && arrays[(int)Mesh.ArrayType.Bones].AsInt32Array().Length == verts.Length * 8)
                    flags |= Mesh.ArrayFormat.FlagUse8BoneWeights;
                prepared.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
                var idx = prepared.GetSurfaceCount() - 1;
                prepared.SurfaceSetMaterial(idx, mat);
                prepared.SurfaceSetName(idx, name);
            }
            PreparedMeshes[path] = prepared;
        }
        mi.Mesh = prepared;
    }

    void ApplySurfaces(CamoSpec spec, CamoFile data, CamoBody body)
    {
        var mesh = (ArrayMesh)_mesh!.Mesh;
        var tone = ToColor(data.SkinTones[spec.SkinTone].Colour);
        var suit = SuitMaterial(spec, data);
        var gearColour = ToColor(spec.SuitSecondary) * 0.55f;
        gearColour.A = 1f;
        var gear = new StandardMaterial3D { AlbedoColor = gearColour, Roughness = 0.9f };
        var skin = new StandardMaterial3D { AlbedoColor = tone, VertexColorUseAsAlbedo = true, Roughness = 0.85f };
        var face = new StandardMaterial3D { AlbedoColor = tone, Roughness = 0.85f };
        for (var i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            var name = SurfaceName(mesh, i);
            Material? m = null;
            if (name == "Skin") m = skin;
            else if (name == "Face") m = face;
            else if (body.Suit.Contains(name)) m = suit;
            else if (body.Gear.Contains(name)) m = gear;
            else if (body.Headgear.Contains(name)) m = spec.Headgear switch { 1 => suit, 2 => HiddenMaterial(), _ => null };
            _mesh.SetSurfaceOverrideMaterial(i, m);
        }
    }

    static ShaderMaterial SuitMaterial(CamoSpec spec, CamoFile data)
    {
        _suitShader ??= GD.Load<Shader>(SuitShaderPath);
        var mat = new ShaderMaterial { Shader = _suitShader };
        mat.SetShaderParameter("base_color", ToColor(spec.SuitBase));
        mat.SetShaderParameter("secondary_color", ToColor(spec.SuitSecondary));
        mat.SetShaderParameter("pattern", (int)spec.SuitPattern);
        mat.SetShaderParameter("pattern_scale", data.PatternScaleOf(spec.PatternScale) * Scale); // the shader works in rig units, the data in metres
        mat.SetShaderParameter("contrast", CamoSpec.Unit(spec.Contrast));
        return mat;
    }

    static ShaderMaterial HiddenMaterial()
    {
        _hiddenShader ??= new Shader { Code = "shader_type spatial;\nrender_mode unshaded;\nvoid fragment() { discard; }\n" };
        return new ShaderMaterial { Shader = _hiddenShader };
    }

    // ---- tufts ----

    /// <summary>One triangle of a tuft-bearing surface: bind-pose corners and normal, the bone that carries it, and its body region (0 head, 1 chest, 2 arms, 3 legs).</summary>
    readonly record struct Tri(Vector3 A, Vector3 B, Vector3 C, Vector3 N, int Bone, int Region, float Area);

    /// <summary>The bone a tuft rides on and its body region, for the bone that skins a triangle. (-1, -1) for bones that carry no tufts.</summary>
    (string Attach, int Region) TuftBone(string bone) => bone switch
    {
        "Head" or "Neck" => ("Head", 0),
        "Torso" or "Abdomen" or "Hips" or "Shoulder.L" or "Shoulder.R" => ("Torso", 1),
        "UpperArm.L" or "UpperArm.R" => (bone, 2),
        "LowerArm.L" or "LowerArm.R" => (bone, 2),
        "Fist.L" => ("LowerArm.L", 2),
        "Fist.R" => ("LowerArm.R", 2),
        "UpperLeg.L" or "UpperLeg.R" => (bone, 3),
        "LowerLeg.L" or "LowerLeg.R" => (bone, 3),
        "Foot.L" => ("LowerLeg.L", 3),
        "Foot.R" => ("LowerLeg.R", 3),
        _ => ("", -1),
    };

    List<Tri> CollectTriangles(CamoSpec spec, CamoBody body, out Dictionary<int, string> attachOf)
    {
        var mesh = (ArrayMesh)_mesh!.Mesh;
        var skin = _mesh.Skin!;
        var sk = _skeleton!;
        attachOf = new Dictionary<int, string>();
        var tris = new List<Tri>();
        var jointBone = new int[skin.GetBindCount()];
        for (var j = 0; j < jointBone.Length; j++)
        {
            var b = skin.GetBindBone(j);
            jointBone[j] = b >= 0 ? b : sk.FindBone(skin.GetBindName(j));
        }
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var name = SurfaceName(mesh, s);
            var carries = body.Suit.Contains(name) || body.Gear.Contains(name) || (body.Headgear.Contains(name) && spec.Headgear != 2);
            if (!carries) continue;
            var arrays = mesh.SurfaceGetArrays(s);
            var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var n = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            var index = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            if (v.Length == 0 || bones.Length < v.Length * 4 || n.Length != v.Length) continue;
            var per = bones.Length / v.Length;
            int Dominant(int vi)
            {
                var best = 0; var bw = -1f;
                for (var k = 0; k < per; k++)
                    if (weights[vi * per + k] > bw) { bw = weights[vi * per + k]; best = bones[vi * per + k]; }
                return best >= 0 && best < jointBone.Length ? jointBone[best] : -1;
            }
            for (var t = 0; t + 2 < index.Length; t += 3)
            {
                int i0 = index[t], i1 = index[t + 1], i2 = index[t + 2];
                var bone = Dominant(i0);
                if (bone < 0) continue;
                var (attach, region) = TuftBone(sk.GetBoneName(bone));
                if (region < 0) continue;
                var attachBone = sk.FindBone(attach);
                if (attachBone < 0 || !_bind.ContainsKey(attachBone)) continue;
                attachOf[attachBone] = attach;
                var area = (v[i1] - v[i0]).Cross(v[i2] - v[i0]).Length() * 0.5f;
                if (area < 1e-6f) continue;
                tris.Add(new Tri(v[i0], v[i1], v[i2], (n[i0] + n[i1] + n[i2]).Normalized(), attachBone, region, area));
            }
        }
        return tris;
    }

    void BuildTufts(CamoSpec spec, CamoFile data, CamoBody body)
    {
        if (spec.TuftLayers == 0) return;
        var tris = CollectTriangles(spec, body, out var attachOf);
        if (tris.Count == 0) return;
        var layers = Math.Min(spec.TuftLayers, data.TuftMaxLayers);
        var size = data.TuftSizeOf(spec.TuftSize) / Scale; // rig units
        // one seed from the spec, so every client scatters the same tufts for the same player
        var rng = new Random(spec.Encode().Aggregate(17, (h, b) => h * 31 + b));
        var perBone = new Dictionary<int, List<Transform3D>>();
        var density = new Func<CamoSpec, byte>[] { s => s.TuftHead, s => s.TuftChest, s => s.TuftArms, s => s.TuftLegs };
        for (var region = 0; region < 4; region++)
        {
            var pool = tris.Where(t => t.Region == region).ToList();
            var count = (int)Math.Round(density[region](spec) / 255f * TuftsPerRegionPerLayer * layers);
            if (pool.Count == 0 || count == 0) continue;
            var cumulative = new float[pool.Count];
            var total = 0f;
            for (var i = 0; i < pool.Count; i++) { total += pool[i].Area; cumulative[i] = total; }
            for (var k = 0; k < count; k++)
            {
                var pick = (float)rng.NextDouble() * total;
                var at = Array.BinarySearch(cumulative, pick);
                var tri = pool[Math.Min(at < 0 ? ~at : at, pool.Count - 1)];
                // a uniform point on the triangle
                float r1 = MathF.Sqrt((float)rng.NextDouble()), r2 = (float)rng.NextDouble();
                var p = tri.A * (1 - r1) + tri.B * (r1 * (1 - r2)) + tri.C * (r1 * r2);
                var toBone = _bind[tri.Bone].AffineInverse();
                var normal = (toBone.Basis * tri.N).Normalized();
                var basis = new Basis(new Quaternion(Vector3.Up, normal)) * new Basis(Vector3.Up, (float)(rng.NextDouble() * Math.PI * 2));
                var origin = toBone * p + normal * (size * 0.65f);
                if (!perBone.TryGetValue(tri.Bone, out var list)) perBone[tri.Bone] = list = new List<Transform3D>();
                list.Add(new Transform3D(basis, origin));
            }
        }
        if (perBone.Count == 0) return;

        _tuftShader ??= GD.Load<Shader>(TuftShaderPath);
        var mat = new ShaderMaterial { Shader = _tuftShader };
        mat.SetShaderParameter("base_color", ToColor(spec.SuitBase));
        mat.SetShaderParameter("secondary_color", ToColor(spec.SuitSecondary));
        mat.SetShaderParameter("sway_amount", data.TuftSwayOf(spec.TuftSway) / Scale);
        mat.SetShaderParameter("colour_variation", CamoSpec.Unit(spec.TuftColourVariation));
        var quad = new QuadMesh { Size = new Vector2(size, size * 1.3f) };
        foreach (var (bone, placements) in perBone)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = quad, InstanceCount = placements.Count };
            for (var i = 0; i < placements.Count; i++) mm.SetInstanceTransform(i, placements[i]);
            var carrier = new BoneAttachment3D { Name = "Tufts_" + attachOf[bone].Replace('.', '_'), BoneName = attachOf[bone] };
            carrier.AddChild(new MultiMeshInstance3D { Name = "Tufts", Multimesh = mm, MaterialOverride = mat, VisibilityRangeEnd = TuftHideDistance });
            _skeleton!.AddChild(carrier);
            _camoNodes.Add(carrier);
        }
    }

    // ---- face ----

    /// <summary>Bounds (bind pose) of the Face plate, or null when the body has none.</summary>
    Aabb? FacePlate()
    {
        if (_mesh?.Mesh is not ArrayMesh mesh) return null;
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (SurfaceName(mesh, s) != "Face") continue;
            var v = mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (v.Length == 0) return null;
            var box = new Aabb(v[0], Vector3.Zero);
            foreach (var p in v) box = box.Expand(p);
            return box;
        }
        return null;
    }

    /// <summary>Two dark eyes on the face plate (the plate is blank in the model). They ride on the head bone and stay for the life of the model.</summary>
    void BuildEyes(Skeleton3D sk, MeshInstance3D mi)
    {
        _bind = BindPoses(sk, mi.Skin!);
        var head = sk.FindBone("Head");
        if (FacePlate() is not { } plate || head < 0 || !_bind.ContainsKey(head)) return;
        var eye = new BoxMesh { Size = new Vector3(plate.Size.X * 0.11f, plate.Size.Y * 0.32f, 0.02f) };
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.04f, 0.04f, 0.05f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var toHead = _bind[head].AffineInverse();
        var carrier = new BoneAttachment3D { Name = "Eyes", BoneName = "Head" };
        foreach (var side in new[] { -1f, 1f })
        {
            var at = new Vector3(plate.GetCenter().X + side * plate.Size.X * 0.2f, plate.GetCenter().Y + plate.Size.Y * 0.08f, plate.End.Z + 0.005f);
            carrier.AddChild(new MeshInstance3D { Mesh = eye, MaterialOverride = dark, Transform = toHead * new Transform3D(Basis.Identity, at) });
        }
        sk.AddChild(carrier);
    }

    void BuildFacePaint(CamoSpec spec)
    {
        if (spec.FaceCoverage == 0 || _skeleton == null) return;
        var head = _skeleton.FindBone("Head");
        if (FacePlate() is not { } plate || head < 0 || !_bind.ContainsKey(head)) return;
        var tex = ImageTexture.CreateFromImage(FaceImage(spec));
        // a Decal projects along its local -Y: turned 90 degrees about X it projects along -Z, from the front of the face towards the back of the head
        var inBind = new Transform3D(Basis.FromEuler(new Vector3(Mathf.Pi / 2f, 0, 0)), new Vector3(plate.GetCenter().X, plate.GetCenter().Y, plate.GetCenter().Z + 0.05f));
        var carrier = new BoneAttachment3D { Name = "FacePaint", BoneName = "Head" };
        carrier.AddChild(new Decal
        {
            Name = "Decal",
            Transform = _bind[head].AffineInverse() * inBind,
            Size = new Vector3(plate.Size.X * 1.05f, 0.2f, plate.Size.Y * 1.05f),
            TextureAlbedo = tex,
        });
        _skeleton.AddChild(carrier);
        _camoNodes.Add(carrier);
    }

    static Color ToColor(Rgb c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>The face paint texture: three colours in a pattern (camo.json face_patterns order: bars, streaks, dots), faded out past the ellipse of the face.</summary>
    static Image FaceImage(CamoSpec spec)
    {
        const int n = 64;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        var a = ToColor(spec.FaceColourA);
        var b = ToColor(spec.FaceColourB);
        var c = ToColor(spec.FaceColourC);
        var cover = CamoSpec.Unit(spec.FaceCoverage);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var u = x / (float)(n - 1);
            var v = y / (float)(n - 1);
            var dx = (u - 0.5f) * 2f;
            var dy = (v - 0.5f) * 2f;
            var r = MathF.Sqrt(dx * dx + dy * dy);
            var alpha = cover * Math.Clamp((1f - r) * 8f, 0f, 1f);
            var colour = spec.FacePattern switch
            {
                1 => Streak(u, v) switch { < 0.4f => a, < 0.7f => b, _ => c },
                2 => CellDistance(u, v) < 0.3f ? c : b,
                _ => Fract(v * 6f) < 0.5f ? a : b,
            };
            img.SetPixel(x, y, new Color(colour.R, colour.G, colour.B, alpha));
        }
        return img;
    }

    /// <summary>A diagonal, wavy band coordinate in 0..1 (streaks).</summary>
    static float Streak(float u, float v) => Fract(u * 3f + v * 5f + 0.2f * MathF.Sin(v * 12f));

    /// <summary>Distance from the centre of the 1/6-size cell that (u, v) falls in, 0 to about 0.7 (dots).</summary>
    static float CellDistance(float u, float v)
    {
        var cu = Fract(u * 6f) - 0.5f;
        var cv = Fract(v * 6f) - 0.5f;
        return MathF.Sqrt(cu * cu + cv * cv);
    }

    static float Fract(float x) => x - MathF.Floor(x);
}
