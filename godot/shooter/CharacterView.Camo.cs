using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Camouflage on a character (ticket #140). The body gets the suit shader (<c>camo_suit.gdshader</c>) as a material
/// override; the foliage tufts are one MultiMesh per suit (one draw call, <c>camo_tuft.gdshader</c>), hidden beyond
/// <see cref="TuftHideDistance"/>; the face paint is a Decal projected onto the front of the head.
/// Tuft and face positions are in the rig's own units (before <see cref="Scale"/>), placed by eye from the KayKit figure.
/// Tufts sit at their bind-pose positions and do not follow the limb animation (a known limit).
/// </summary>
public sealed partial class CharacterView
{
    const string SuitShaderPath = "res://shaders/camo_suit.gdshader";
    const string TuftShaderPath = "res://shaders/camo_tuft.gdshader";

    /// <summary>Beyond this distance (m) the tufts are not drawn: the cheap LOD for distant players.</summary>
    const float TuftHideDistance = 45f;

    /// <summary>Most tufts per region at full density and three layers.</summary>
    const int TuftsPerRegionPerLayer = 14;

    static Shader? _suitShader, _tuftShader;
    Node3D? _camoRoot;

    /// <summary>Applies a player's look. A missing model (headless, primitive fallback) or a null data file does nothing.</summary>
    public void SetCamo(CamoSpec spec, CamoFile? data)
    {
        if (_model == null || data == null || spec.Validate(data) != null) return;
        _camoRoot?.QueueFree();
        _camoRoot = new Node3D { Name = "Camo" };
        _model.AddChild(_camoRoot);
        ApplySuit(spec, data);
        BuildTufts(spec, data);
        BuildFacePaint(spec, data);
    }

    void ApplySuit(CamoSpec spec, CamoFile data)
    {
        _suitShader ??= GD.Load<Shader>(SuitShaderPath);
        var mat = new ShaderMaterial { Shader = _suitShader };
        mat.SetShaderParameter("base_color", ToColor(spec.SuitBase));
        mat.SetShaderParameter("secondary_color", ToColor(spec.SuitSecondary));
        mat.SetShaderParameter("pattern", (int)spec.SuitPattern);
        mat.SetShaderParameter("pattern_scale", data.PatternScaleOf(spec.PatternScale));
        mat.SetShaderParameter("contrast", CamoSpec.Unit(spec.Contrast));
        // the held weapon hangs under the model too; leave its materials alone
        foreach (var mi in _model!.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            if (_held == null || !_held.IsAncestorOf(mi)) mi.MaterialOverride = mat;
    }

    void BuildTufts(CamoSpec spec, CamoFile data)
    {
        if (spec.TuftLayers == 0) return;
        var size = data.TuftSizeOf(spec.TuftSize);
        var layers = Math.Min(spec.TuftLayers, data.TuftMaxLayers);
        // one seed from the spec, so every client scatters the same tufts for the same player
        var rng = new Random(spec.Encode().Aggregate(17, (h, b) => h * 31 + b));
        var placements = new List<Transform3D>();
        foreach (var r in TuftRegions)
        {
            var count = (int)Math.Round(r.Density(spec) / 255f * TuftsPerRegionPerLayer * layers * r.Share);
            for (var i = 0; i < count; i++)
            {
                var at = r.Centre + new Vector3(
                    (float)(rng.NextDouble() * 2 - 1) * r.Spread.X,
                    (float)(rng.NextDouble() * 2 - 1) * r.Spread.Y,
                    (float)(rng.NextDouble() * 2 - 1) * r.Spread.Z);
                at.Y += size * 0.5f; // the quad is centred on its origin; its root sits on the body
                placements.Add(new Transform3D(Basis.FromEuler(new Vector3(0, (float)(rng.NextDouble() * Math.PI * 2), 0)), at));
            }
        }
        if (placements.Count == 0) return;

        var tuft = new QuadMesh { Size = new Vector2(size, size * 1.3f) };
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = tuft, InstanceCount = placements.Count };
        for (var i = 0; i < placements.Count; i++) mm.SetInstanceTransform(i, placements[i]);

        _tuftShader ??= GD.Load<Shader>(TuftShaderPath);
        var mat = new ShaderMaterial { Shader = _tuftShader };
        mat.SetShaderParameter("base_color", ToColor(spec.SuitBase));
        mat.SetShaderParameter("secondary_color", ToColor(spec.SuitSecondary));
        mat.SetShaderParameter("sway_amount", data.TuftSwayOf(spec.TuftSway));
        mat.SetShaderParameter("colour_variation", CamoSpec.Unit(spec.TuftColourVariation));

        _camoRoot!.AddChild(new MultiMeshInstance3D
        {
            Name = "Tufts", Multimesh = mm, MaterialOverride = mat,
            VisibilityRangeEnd = TuftHideDistance,
        });
    }

    void BuildFacePaint(CamoSpec spec, CamoFile data)
    {
        if (spec.FaceCoverage == 0) return;
        var tex = ImageTexture.CreateFromImage(FaceImage(spec));
        // Decal projects along its local -Y. Turned by -90 degrees about X it projects along +Z, the figure's front in model space.
        _camoRoot!.AddChild(new Decal
        {
            Name = "FacePaint",
            Position = FaceCentre + new Vector3(0, 0, 0.2f),
            RotationDegrees = new Vector3(-90, 0, 0),
            Size = new Vector3(0.3f, 0.4f, 0.3f),
            TextureAlbedo = tex,
        });
    }

    /// <summary>Where the face sits in the rig's units (head bone height, front of the head).</summary>
    static readonly Vector3 FaceCentre = new(0, 0.95f, 0.02f);

    /// <summary>Tuft regions in rig units: the hood, chest, the two arms and the two legs.</summary>
    static readonly TuftRegion[] TuftRegions = new[]
    {
        new TuftRegion(new Vector3(0, 1.0f, 0), new Vector3(0.14f, 0.08f, 0.14f), s => s.TuftHead, 1f),
        new TuftRegion(new Vector3(0, 0.65f, 0), new Vector3(0.22f, 0.2f, 0.12f), s => s.TuftChest, 1f),
        new TuftRegion(new Vector3(-0.35f, 0.6f, 0), new Vector3(0.1f, 0.25f, 0.1f), s => s.TuftArms, 0.5f),
        new TuftRegion(new Vector3(0.35f, 0.6f, 0), new Vector3(0.1f, 0.25f, 0.1f), s => s.TuftArms, 0.5f),
        new TuftRegion(new Vector3(-0.15f, 0.2f, 0), new Vector3(0.12f, 0.2f, 0.12f), s => s.TuftLegs, 0.5f),
        new TuftRegion(new Vector3(0.15f, 0.2f, 0), new Vector3(0.12f, 0.2f, 0.12f), s => s.TuftLegs, 0.5f),
    };

    /// <summary>One body region that gets tufts: where it is, how far it spreads, which density byte drives it, and its share of the count.</summary>
    sealed record TuftRegion(Vector3 Centre, Vector3 Spread, Func<CamoSpec, byte> Density, float Share);

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
