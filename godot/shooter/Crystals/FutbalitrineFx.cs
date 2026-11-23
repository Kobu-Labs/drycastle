using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Futbalitrine (crystals.md): the football stone. Opaque crystals in football leather: black pentagons and white hexagons with seams, projected from the middle of every crystal
/// (<c>shaders/crystal_football.gdshader</c>). No glow, no light. The top of the rock is turf with grass tufts, and a lone ball lies on it. A whistle blows now and then. Tunables: data/crystals.json "futbalitrine".
/// </summary>
public sealed partial class FutbalitrineFx : CrystalFx
{
    public override string Id => "futbalitrine";

    const int MaxGrass = 30;

    ShaderMaterial _rock = null!, _crystal = null!, _ballMat = null!;
    MeshInstance3D _ball = null!;
    readonly MeshInstance3D[] _grass = new MeshInstance3D[MaxGrass];
    double _whistleAt = 8;

    protected override void Build()
    {
        _rock = MossRock();
        var shader = GD.Load<Shader>("res://shaders/crystal_football.gdshader");
        _crystal = new ShaderMaterial { Shader = shader };
        _ballMat = new ShaderMaterial { Shader = shader };
        _ballMat.SetShaderParameter("ball", true);
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        var centers = new Vector3[10];
        for (var i = 0; i < 10; i++) centers[i] = i < Info.Boxes.Count ? Info.Boxes[i].GetCenter() : Vector3.Zero;
        _crystal.SetShaderParameter("centers", centers);

        _ball = new MeshInstance3D { Name = "Ball", Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 24, Rings = 12, Material = _ballMat } };
        AddChild(_ball);
        var plant = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var tufts = new[] { SmetiskomiteFx.TuftMesh(plant, 0, new Color(0.06f, 0.3f, 0.06f), new Color(0.3f, 0.7f, 0.2f)), SmetiskomiteFx.TuftMesh(plant, 1, new Color(0.06f, 0.3f, 0.06f), new Color(0.36f, 0.75f, 0.22f)) };
        for (var i = 0; i < MaxGrass; i++)
        {
            _grass[i] = new MeshInstance3D { Name = $"Tuft{i}", Mesh = tufts[i % 2], CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_grass[i]);
        }
    }

    protected override void OnApply()
    {
        ApplyMossRock(_rock, "turf", "#5a5348", "#1f6b22", "#4fa83a");
        foreach (var m in new[] { _crystal, _ballMat })
        {
            m.SetShaderParameter("white", P.C("white", "#eeeee6"));
            m.SetShaderParameter("black", P.C("black", "#0b0b0d"));
            m.SetShaderParameter("seam_color", P.C("seam_color", "#3c3c3c"));
            m.SetShaderParameter("seam", P.F("seam", 0.012f));
            m.SetShaderParameter("rough", P.F("crystal_roughness", 0.3f));
        }
        _crystal.SetShaderParameter("squash", P.F("squash", 0.55f));

        // the ball on the flattest face of the rock the seed finds
        (Vector3 P, Vector3 N)? flat = null;
        for (var k = 0; k < 40; k++)
            if (RockSpot(P.I("ball_seed", 4) + k * 11) is { } s && (flat is null || s.N.Y > flat.Value.N.Y)) flat = s;
        _ball.Visible = P.B("dress_ball", true) && flat is not null;
        if (flat is { } f)
        {
            var r = P.F("ball_radius", 0.045f);
            _ball.Transform = new Transform3D(Basis.FromEuler(new Vector3(0.4f, P.F("ball_yaw", 0.8f), 0.2f)).Scaled(Vector3.One * r), f.P + f.N * r);
        }

        var gn = P.B("dress_grass", true) && Info.RockUp.Count > 0 ? Math.Clamp(P.I("grass_count", 12), 0, MaxGrass) : 0;
        for (var i = 0; i < MaxGrass; i++)
        {
            _grass[i].Visible = i < gn;
            if (i >= gn) continue;
            var s = RockSpot(P.I("grass_seed", 9) + i * 17)!.Value;
            var h = P.F("grass_height", 0.05f) * (0.7f + 0.6f * (float)((i * 0.618) % 1.0));
            _grass[i].Transform = new Transform3D(OnSurface(s.N, i * 2.4f).Scaled(Vector3.One * h), s.P);
        }
        SetLoop(P.S("crowd_sound", ""), P.F("crowd_db", -26f));
    }

    protected override void Tick(double dt)
    {
        if (Time < _whistleAt) return;
        _whistleAt = Time + Rand(P.F("whistle_interval_min", 15f), P.F("whistle_interval_max", 40f));
        Sound(P.S("whistle_sound", ""), Box.GetCenter(), P.F("whistle_db", -16f));
    }
}
