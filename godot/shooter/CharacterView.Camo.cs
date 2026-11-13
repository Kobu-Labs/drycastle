using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A character's look (tickets #140, #141 and the character layer): the body model, skin tone, headgear, camouflage suit, foliage and face paint.
/// The models are flat-colour surfaces named by material, so the look works per surface (<see cref="CamoBody"/> says which material is the suit,
/// the gear, the headgear or the hair): the suit surfaces get the suit shader (<c>camo_suit.gdshader</c>), the gear a dark tint of the suit colour,
/// <c>Skin</c> and <c>Face</c> the skin tone, the headgear stays as modelled, takes the suit pattern or is hidden. The foliage tufts are scattered on
/// the real surface of the suit, gear, headgear and hair (so they fit every body) and ride on the bones, one MultiMesh per bone
/// (<c>camo_tuft.gdshader</c>), hidden beyond <see cref="TuftHideDistance"/>; the face paint is a Decal on the face plate.
/// Everything is derived from the spec and the model (the scatter is a fixed sequence per body region, see <see cref="BuildTufts"/>),
/// so every client draws the same player the same way.
/// </summary>
public sealed partial class CharacterView
{
    public const string SuitShaderPath = "res://shaders/camo_suit.gdshader";
    public const string TuftShaderPath = "res://shaders/camo_tuft.gdshader";

    /// <summary>Beyond this distance (m) the tufts are not drawn: the cheap LOD for distant players.</summary>
    const float TuftHideDistance = 45f;

    /// <summary>Tufts per square metre of a body region at full density, and the most one region ever gets.</summary>
    const float TuftsPerSquareMetre = 230f;
    const int MaxTuftsPerRegion = 150;

    /// <summary>
    /// How one vegetation look (camo.json <c>vegetation</c> order: grass, fern, bush) is built: the card's width as a share of its length,
    /// its length as a share of the tuft length, how far it hangs towards the ground instead of standing on the surface (0 stands, 1 hangs),
    /// and how far the cards of one tuft fan apart. The card's outline is cut by <c>camo_tuft.gdshader</c> (uniform <c>shape</c>).
    /// </summary>
    readonly record struct TuftLook(float Width, float Length, float Droop, float Fan);
    static readonly TuftLook[] TuftLooks =
    {
        new(0.16f, 1.0f, 0.5f, 0.35f),  // grass: long thin blades that hang
        new(0.5f, 0.9f, 0.7f, 0.5f),    // fern: broad toothed fronds that droop
        new(0.75f, 0.55f, 0.1f, 0.8f),  // bush: short round leaves that stand out from the body
    };

    /// <summary>Skin vertices below this height (rig units, bind pose) are the boots: they keep a dark share of the skin tone instead of the whole tone.</summary>
    const float BootTop = 0.2f;

    static Shader? _suitShader, _tuftShader, _hiddenShader;
    static readonly Dictionary<string, ArrayMesh> PreparedMeshes = new();

    CamoBody? _bodyDef;
    Dictionary<int, Transform3D> _bind = new();
    readonly List<Node> _camoNodes = new();
    CamoSpec? _shownSpec;
    CamoFile? _shownData;

    /// <summary>Applies a player's look. A missing model (headless, primitive fallback) or a null data file does nothing; a look camo.json rejects leaves the previous one.</summary>
    public void SetCamo(CamoSpec spec, CamoFile? data)
    {
        if (data == null || spec.Validate(data) != null) return;
        var body = data.Bodies[spec.Body];
        if (body.Model != _modelPath && !Build(body.Model) && _model == null) return;
        if (_model == null || _mesh == null || _skeleton == null) return;
        if (_shownSpec == spec && ReferenceEquals(_shownData, data)) return; // already drawn (the loadout screen asks again on every sync)
        _bodyDef = body;
        DrawCamo(spec, data, body);
    }

    void DrawCamo(CamoSpec spec, CamoFile data, CamoBody body)
    {
        ClearCamo();
        _shownSpec = spec; _shownData = data;
        ApplySurfaces(spec, data, body);
        BuildTufts(spec, data, body);
        BuildFacePaint(spec);
    }

    /// <summary>Draws the look again with what is worn now (<see cref="SetWorn"/>): a headgear Item hides the helmet and hair and takes the head's foliage and face paint away.</summary>
    void ReapplyCamo()
    {
        if (_shownSpec is { } spec && _shownData is { } data && _bodyDef is { } body && _mesh != null) DrawCamo(spec, data, body);
    }

    void ClearCamo()
    {
        _shownSpec = null; _shownData = null;
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
            else if (body.Headgear.Contains(name)) m = HeadCovered ? HiddenMaterial() : spec.Headgear switch { 1 => suit, 2 => HiddenMaterial(), _ => null };
            else if (HeadCovered && body.Hair.Contains(name)) m = HiddenMaterial(); // a worn headgear Item covers the hair too
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

    /// <summary>One triangle of a tuft-bearing surface: bind-pose corners and normal, and the bone that carries it.</summary>
    readonly record struct Tri(Vector3 A, Vector3 B, Vector3 C, Vector3 N, int Bone);

    /// <summary>The tuft-bearing triangles of one body region (0 head, 1 chest, 2 arms, 3 legs), with the running area for an area-weighted pick.</summary>
    sealed class TuftRegion
    {
        public readonly List<Tri> Tris = new();
        public readonly List<float> Cumulative = new();
        /// <summary>Total area in rig units squared.</summary>
        public float Area;
    }

    /// <summary>The regions of a model, by model path and by whether the scalp (no helmet shown) or the helmet carries the head's tufts. The mesh is shared, so this is found once.</summary>
    static readonly Dictionary<(string Model, bool Bare), (TuftRegion[] Regions, Dictionary<int, string> AttachOf)> TuftRegions = new();

    /// <summary>The bone a tuft rides on and its body region, for the bone that skins a triangle. ("", -1) for bones that carry no tufts.</summary>
    static (string Attach, int Region) TuftBone(string bone) => bone switch
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

    /// <summary>
    /// The triangles that carry tufts, by region: the suit, the gear, the hair and the helmet when it is shown. With no helmet shown the
    /// scalp carries the head's tufts (the skin of the head that does not face forward, so the face stays clear): every body can grow a hood.
    /// </summary>
    (TuftRegion[] Regions, Dictionary<int, string> AttachOf) CollectTriangles(CamoSpec spec, CamoBody body)
    {
        var bare = body.Headgear.Count == 0 || spec.Headgear == 2;
        if (TuftRegions.TryGetValue((_modelPath, bare), out var cached)) return cached;
        var mesh = (ArrayMesh)_mesh!.Mesh;
        var skin = _mesh.Skin!;
        var sk = _skeleton!;
        var attachOf = new Dictionary<int, string>();
        var regions = new[] { new TuftRegion(), new TuftRegion(), new TuftRegion(), new TuftRegion() };
        var jointBone = new int[skin.GetBindCount()];
        for (var j = 0; j < jointBone.Length; j++)
        {
            var b = skin.GetBindBone(j);
            jointBone[j] = b >= 0 ? b : sk.FindBone(skin.GetBindName(j));
        }
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var name = SurfaceName(mesh, s);
            var scalp = name == "Skin" && bare;
            var carries = scalp || body.Suit.Contains(name) || body.Gear.Contains(name) || body.Hair.Contains(name) || (body.Headgear.Contains(name) && !bare);
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
                var boneName = sk.GetBoneName(bone);
                var (attach, region) = TuftBone(boneName);
                if (region < 0) continue;
                var normal = (n[i0] + n[i1] + n[i2]).Normalized();
                // the skin carries tufts only on the scalp: the head bone, and not the side the face is on (+Z in the model)
                if (scalp && (boneName != "Head" || normal.Z > 0.3f)) continue;
                var attachBone = sk.FindBone(attach);
                if (attachBone < 0 || !_bind.ContainsKey(attachBone)) continue;
                var area = (v[i1] - v[i0]).Cross(v[i2] - v[i0]).Length() * 0.5f;
                if (area < 1e-6f) continue;
                attachOf[attachBone] = attach;
                var r = regions[region];
                r.Area += area;
                r.Tris.Add(new Tri(v[i0], v[i1], v[i2], normal, attachBone));
                r.Cumulative.Add(r.Area);
            }
        }
        return TuftRegions[(_modelPath, bare)] = (regions, attachOf);
    }

    /// <summary>
    /// Scatters the foliage. Every control changes one thing, and nothing else moves when it does:
    /// the density of a region is how many tufts it has (per square metre of that region, so a head and a chest look equally covered),
    /// the layers are how many cards each tuft has (layers + 1: wisps to thick clumps), the size is the card length, the vegetation is the card's
    /// shape and how it hangs. Tuft <c>i</c> of a region always sits at the same place with the same cards (its own seeded sequence),
    /// so raising a density adds tufts to the ones already there, and a colour or size change leaves every tuft where it was.
    /// </summary>
    void BuildTufts(CamoSpec spec, CamoFile data, CamoBody body)
    {
        if (spec.TuftLayers == 0) return;
        var (regions, attachOf) = CollectTriangles(spec, body);
        var cards = Math.Min(spec.TuftLayers, data.TuftMaxLayers) + 1;
        var look = TuftLooks[spec.Vegetation < TuftLooks.Length ? spec.Vegetation : 0];
        var length = data.TuftSizeOf(spec.TuftSize) / Scale * look.Length; // rig units
        var perBone = new Dictionary<int, List<Transform3D>>();
        var density = new[] { spec.TuftHead, spec.TuftChest, spec.TuftArms, spec.TuftLegs };
        for (var region = 0; region < regions.Length; region++)
        {
            if (region == 0 && HeadCovered) continue; // the head is under a worn Item: foliage there would sit inside it
            var pool = regions[region];
            var areaM2 = pool.Area * Scale * Scale;
            var count = Math.Min(MaxTuftsPerRegion, (int)Math.Round(density[region] / 255f * areaM2 * TuftsPerSquareMetre));
            if (pool.Tris.Count == 0 || count == 0) continue;
            for (var k = 0; k < count; k++)
            {
                var rng = new Random(region * 100003 + k); // tuft k of this region: the same draws whatever the look
                var at = pool.Cumulative.BinarySearch((float)rng.NextDouble() * pool.Area);
                var tri = pool.Tris[Math.Min(at < 0 ? ~at : at, pool.Tris.Count - 1)];
                // a uniform point on the triangle
                float r1 = MathF.Sqrt((float)rng.NextDouble()), r2 = (float)rng.NextDouble();
                var p = tri.A * (1 - r1) + tri.B * (r1 * (1 - r2)) + tri.C * (r1 * r2);
                var toBone = _bind[tri.Bone].AffineInverse();
                var origin = toBone * p;
                var normal = (toBone.Basis * tri.N).Normalized();
                var down = (toBone.Basis * Vector3.Down).Normalized();
                if (!perBone.TryGetValue(tri.Bone, out var list)) perBone[tri.Bone] = list = new List<Transform3D>();
                for (var c = 0; c < cards; c++)
                {
                    var jitter = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 2f * look.Fan;
                    var spin = (float)(rng.NextDouble() * Math.PI * 2);
                    var grow = 0.7f + 0.6f * (float)rng.NextDouble();
                    // the card stands on its root: its length runs along y, from the surface outwards and down
                    var y = (normal * (1f - look.Droop) + down * look.Droop + jitter).Normalized();
                    var outward = y.Dot(normal);
                    if (outward < 0.1f) y = (y - normal * outward + normal * 0.25f).Normalized(); // never into the body: lie along the surface, tilted out
                    var x = y.Cross(MathF.Abs(y.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized().Rotated(y, spin);
                    var basis = new Basis(x, y, x.Cross(y)).Scaled(Vector3.One * grow);
                    list.Add(new Transform3D(basis, origin));
                }
            }
        }
        if (perBone.Count == 0) return;

        _tuftShader ??= GD.Load<Shader>(TuftShaderPath);
        var mat = new ShaderMaterial { Shader = _tuftShader };
        mat.SetShaderParameter("base_color", ToColor(spec.SuitBase));
        mat.SetShaderParameter("secondary_color", ToColor(spec.SuitSecondary));
        mat.SetShaderParameter("sway_amount", data.TuftSwayOf(spec.TuftSway) / Scale);
        mat.SetShaderParameter("colour_variation", CamoSpec.Unit(spec.TuftColourVariation));
        mat.SetShaderParameter("shape", spec.Vegetation < TuftLooks.Length ? (int)spec.Vegetation : 0);
        // the quad's bottom edge is at the instance origin, so a card grows from the body
        var quad = new QuadMesh { Size = new Vector2(length * look.Width, length), CenterOffset = new Vector3(0, length * 0.5f, 0) };
        foreach (var (bone, placements) in perBone)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = quad, InstanceCount = placements.Count };
            for (var i = 0; i < placements.Count; i++) mm.SetInstanceTransform(i, placements[i]);
            var carrier = new BoneAttachment3D { Name = "Tufts_" + attachOf[bone].Replace('.', '_'), BoneName = attachOf[bone] };
            carrier.AddChild(new MultiMeshInstance3D
            {
                Name = "Tufts", Multimesh = mm, MaterialOverride = mat, VisibilityRangeEnd = TuftHideDistance,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, // hundreds of small cards: not worth a shadow pass
            });
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
        if (spec.FaceCoverage == 0 || _skeleton == null || HeadCovered) return; // a worn headgear Item covers the face (and a decal would paint on the item)
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

    /// <summary>
    /// The face paint texture. All three colours appear in every pattern (camo.json face_patterns order: bars, streaks, dots).
    /// The coverage is how much of the face is painted: the bars, streaks and dots grow from thin marks to a fully painted face.
    /// The paint fades out past the ellipse of the face.
    /// </summary>
    static Image FaceImage(CamoSpec spec)
    {
        const int n = 128;
        const float soft = 0.06f; // edge softness of a mark, in the mark's own 0..1 width
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        var colours = new[] { ToColor(spec.FaceColourA), ToColor(spec.FaceColourB), ToColor(spec.FaceColourC) };
        var cover = CamoSpec.Unit(spec.FaceCoverage);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var u = x / (float)(n - 1);
            var v = y / (float)(n - 1);
            var dx = (u - 0.5f) * 2f;
            var dy = (v - 0.5f) * 2f;
            var edge = Math.Clamp((1f - MathF.Sqrt(dx * dx + dy * dy)) * 8f, 0f, 1f);
            // which mark the pixel belongs to, and how far it is from the middle of that mark (0 middle, 1 where the next mark starts)
            int mark; float off;
            switch (spec.FacePattern)
            {
                case 1: // streaks: diagonal wavy bands
                {
                    var band = u * 2.5f + v * 4.5f + 0.25f * MathF.Sin(v * 11f);
                    mark = (int)MathF.Floor(band);
                    off = MathF.Abs(Fract(band) - 0.5f) * 2f;
                    break;
                }
                case 2: // dots: a staggered grid of round spots
                {
                    var row = (int)MathF.Floor(v * 6f);
                    var cu = u * 6f + (row % 2) * 0.5f;
                    mark = (int)MathF.Floor(cu) + row * 2;
                    var px = Fract(cu) - 0.5f;
                    var py = Fract(v * 6f) - 0.5f;
                    off = MathF.Sqrt(px * px + py * py) / 0.7072f;
                    break;
                }
                default: // bars: level stripes across the face
                {
                    var band = v * 5f;
                    mark = (int)MathF.Floor(band);
                    off = MathF.Abs(Fract(band) - 0.5f) * 2f;
                    break;
                }
            }
            var painted = Math.Clamp((cover * (1f + soft) - off) / soft, 0f, 1f);
            var colour = colours[((mark % 3) + 3) % 3];
            img.SetPixel(x, y, new Color(colour.R, colour.G, colour.B, 0.95f * painted * edge));
        }
        return img;
    }

    static float Fract(float x) => x - MathF.Floor(x);
}
