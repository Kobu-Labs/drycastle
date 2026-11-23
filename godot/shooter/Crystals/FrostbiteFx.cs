using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Frostbite (crystals.md): the frozen stone. Icy blue-white translucent crystals rimed with frost on the faces that look up (<c>shaders/crystal_quartz.gdshader</c>, <c>frost</c>), a soft cyan glow and light,
/// a crust of snow on the top of the rock (<c>shaders/crystal_rock_moss.gdshader</c> in white), icicles hanging from the undersides of the leaning crystals and from the sides of the rock, a frozen puddle on
/// a flat of the rock. Cold mist spills off the rock, a few snowflakes fall around it, glints flash on the facets. Everything sits on the model (crystals.md design rules); glints are a pure function of time.
/// Tunables: data/crystals.json "frostbite".
/// </summary>
public sealed partial class FrostbiteFx : CrystalFx
{
    public override string Id => "frostbite";

    const int MaxIcicles = 40, MaxGlints = 16;

    ShaderMaterial _rock = null!, _crystal = null!;
    StandardMaterial3D _ice = null!, _puddleMat = null!, _glintMat = null!;
    OmniLight3D _light = null!;
    MeshInstance3D _puddle = null!;
    readonly MeshInstance3D[] _icicles = new MeshInstance3D[MaxIcicles];
    readonly MeshInstance3D[] _glints = new MeshInstance3D[MaxGlints];
    CpuParticles3D _mist = null!, _snow = null!;

    protected override void Build()
    {
        _rock = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_rock_moss.gdshader") };
        if (CrystalAssets.Atlas() is { } atlas) { _rock.SetShaderParameter("atlas", atlas); _rock.SetShaderParameter("use_atlas", true); }
        _crystal = Quartz();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);

        _ice = new StandardMaterial3D { Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.05f, EmissionEnabled = true };
        var cone = new CylinderMesh { TopRadius = 1f, BottomRadius = 0f, Height = 1f, RadialSegments = 6, Rings = 1, Material = _ice };
        for (var i = 0; i < MaxIcicles; i++)
        {
            _icicles[i] = new MeshInstance3D { Name = $"Icicle{i}", Mesh = cone, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_icicles[i]);
        }

        _puddleMat = new StandardMaterial3D { Roughness = 0.03f, Metallic = 0.3f };
        _puddle = new MeshInstance3D { Name = "FrozenPuddle", Mesh = new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 0.003f, RadialSegments = 18, Material = _puddleMat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_puddle);

        _glintMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true, AlbedoTexture = Star(),
        };
        var quad = new QuadMesh { Size = Vector2.One, Material = _glintMat };
        for (var i = 0; i < MaxGlints; i++)
        {
            _glints[i] = new MeshInstance3D { Name = $"Glint{i}", Mesh = quad, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_glints[i]);
        }

        _mist = Emitter(false);
        _mist.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
        _mist.ScaleAmountCurve = new Curve();
        _mist.ScaleAmountCurve.AddPoint(new Vector2(0f, 0.5f));
        _mist.ScaleAmountCurve.AddPoint(new Vector2(1f, 2.2f));
        _snow = Emitter(false);
    }

    /// <summary>A four-point star with a soft core (the glint).</summary>
    static ImageTexture Star()
    {
        const int n = 48;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                float u = Math.Abs((x + 0.5f) / n * 2f - 1f), v = Math.Abs((y + 0.5f) / n * 2f - 1f);
                var ray = Math.Max(0f, 1f - u) * Math.Max(0f, 1f - v * 14f) + Math.Max(0f, 1f - v) * Math.Max(0f, 1f - u * 14f);
                var core = Math.Max(0f, 1f - Mathf.Sqrt(u * u + v * v) * 3f);
                var a = Mathf.Clamp(ray * ray + core * core, 0f, 1f);
                img.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        return ImageTexture.CreateFromImage(img);
    }

    static float H(double n)
    {
        var s = Math.Sin(n * 12.9898 + 1.7) * 43758.5453;
        return (float)(s - Math.Floor(s));
    }

    float _rockTop;

    protected override void OnApply()
    {
        _rock.SetShaderParameter("tint", P.C("rock_tint", "#56616e"));
        _rock.SetShaderParameter("rough", P.F("rock_roughness", 0.8f));
        _rock.SetShaderParameter("moss", P.C("snow_color", "#cfe2f2"));
        _rock.SetShaderParameter("moss_light", P.C("snow_light", "#ffffff"));
        _rock.SetShaderParameter("moss_amount", P.F("snow_amount", 1f));
        _rock.SetShaderParameter("moss_slope", P.F("snow_slope", 0.3f));
        _rock.SetShaderParameter("moss_scale", P.F("snow_scale", 10f));

        ApplyQuartz(_crystal, new[] { "#a9e4ff", "#d9f3ff", "#8fd2f5" }, "#ffffff");
        _crystal.SetShaderParameter("glow", P.F("base_glow", 0.7f));
        _crystal.SetShaderParameter("glow_opacity", P.F("glow_opacity", 0.15f));
        _crystal.SetShaderParameter("frost", P.F("frost", 0.8f));
        _crystal.SetShaderParameter("frost_slope", P.F("frost_slope", 0.25f));

        _light.LightColor = P.C("light", "#7fe3ff");
        _light.OmniRange = P.F("light_range", 2.2f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.7f, 0.5f)));

        _rockTop = Info.RockUp.Count > 0 ? Info.RockUp.Max(r => r.P.Y) + Lift.Y : Box.Size.Y * 0.3f;
        var seed = P.I("seed", 5);

        // icicles: straight down from faces that look down or sideways, high enough to have room under them
        var ice = P.C("icicle_color", "#cfeeff");
        _ice.AlbedoColor = new Color(ice, P.F("icicle_alpha", 0.75f));
        _ice.Emission = ice;
        _ice.EmissionEnergyMultiplier = P.F("icicle_glow", 0.25f);
        var spots = new List<Vector3>();
        foreach (var (p, n, _) in Info.Facets) if (n.Y < -0.25f) spots.Add(p + Lift);
        foreach (var (p, n) in Info.RockFaces) if (n.Y < 0.05f && p.Y + Lift.Y > 0.1f) spots.Add(p + Lift + n * -0.002f);
        var want = P.B("dress_icicles", true) && spots.Count > 0 ? Math.Clamp(P.I("icicle_count", 16), 0, MaxIcicles) : 0;
        var len = P.F("icicle_length", 0.09f);
        var rad = P.F("icicle_radius", 0.009f);
        for (var i = 0; i < MaxIcicles; i++)
        {
            var ic = _icicles[i];
            ic.Visible = i < want;
            if (i >= want) continue;
            var p = Pick(spots, seed + i * 37);
            var l = Math.Min(len * (0.4f + 1.1f * H(i * 2.7 + seed)), p.Y - 0.01f);
            if (l < 0.015f) { ic.Visible = false; continue; }
            var r = rad * (0.7f + 0.6f * H(i * 4.1)) * Mathf.Clamp(l / len + 0.3f, 0.5f, 1.2f);
            ic.Transform = new Transform3D(Basis.FromScale(new Vector3(r, l, r)), p + new Vector3(0, 0.004f - l * 0.5f, 0));
        }

        // frozen puddle on the flattest rock face the seed finds
        (Vector3 P, Vector3 N)? flat = null;
        for (var k = 0; k < 40; k++)
            if (RockSpot(P.I("puddle_seed", 3) + k * 11) is { } s && (flat is null || s.N.Y > flat.Value.N.Y)) flat = s;
        _puddle.Visible = P.B("dress_puddle", true) && flat is not null;
        if (flat is { } f)
        {
            var pr = P.F("puddle_radius", 0.07f);
            _puddleMat.AlbedoColor = P.C("puddle_color", "#9fd4ee");
            _puddle.Transform = new Transform3D(OnSurface(f.N, 0.7f) * Basis.FromScale(new Vector3(pr, 1f, pr * 0.7f)), f.P + f.N * 0.003f);
        }

        var gn = Info.Facets.Count > 0 ? Math.Clamp(P.I("glint_count", 8), 0, MaxGlints) : 0;
        for (var i = 0; i < MaxGlints; i++) if (i >= gn) _glints[i].Visible = false;
        _glintMat.AlbedoColor = P.C("glint_color", "#e8fbff");

        var mist = P.C("mist_color", "#d5ecf7");
        var ma = P.F("mist_alpha", 0.16f);
        _mist.EmissionBoxExtents = new Vector3(Box.Size.X * 0.42f, 0.02f, Box.Size.Z * 0.42f);
        Configure(_mist, P.I("mist_amount", 18), P.F("mist_lifetime", 4f), new Vector3(Box.GetCenter().X, _rockTop * P.F("mist_height", 0.7f), Box.GetCenter().Z), 0.01f,
            P.F("mist_size", 0.07f), P.F("mist_speed", 0.05f), Vector3.Down, 85f, -0.03f, Ramp(new Color(mist, 0f), new Color(mist, ma), new Color(mist, 0f)));
        _mist.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;

        var white = P.C("snow_flake_color", "#ffffff");
        Configure(_snow, P.I("flake_amount", 14), P.F("flake_lifetime", 6f), new Vector3(Box.GetCenter().X, Box.Size.Y + 0.15f, Box.GetCenter().Z), P.F("flake_radius", 0.4f),
            P.F("flake_size", 0.004f), P.F("flake_speed", 0.06f), Vector3.Down, 40f, -0.12f, Ramp(new Color(white, 0f), new Color(white, 0.9f), new Color(white, 0f)));

        SetLoop(P.S("wind_sound", ""), P.F("wind_db", -24f));
    }

    protected override void Tick(double dt)
    {
        _light.LightEnergy = P.F("light_energy", 0.8f) * (1f + P.F("light_shimmer", 0.08f) * Mathf.Sin((float)Time * 1.7f));

        // glints: each one flashes on a facet, waits, flashes on another
        var gn = Info.Facets.Count > 0 ? Math.Clamp(P.I("glint_count", 8), 0, MaxGlints) : 0;
        var size = P.F("glint_size", 0.06f);
        var flash = Math.Max(P.F("glint_flash", 0.5f), 0.05f);
        for (var j = 0; j < gn; j++)
        {
            var period = flash + P.F("glint_interval", 2.5f) * (0.4f + 1.2f * H(j * 3.3));
            var x = (Time + H(j * 7.9) * 50.0) / period;
            var round = Math.Floor(x);
            var t = (float)((x - round) * period);
            _glints[j].Visible = t < flash;
            if (t >= flash) continue;
            var (p, n, _) = Info.Facets[(int)(H(j * 13.1 + round * 5.7) * Info.Facets.Count) % Info.Facets.Count];
            var s = Mathf.Sin(Mathf.Pi * t / flash);
            _glints[j].Transform = new Transform3D(Basis.FromScale(Vector3.One * size * s * s * (0.6f + 0.8f * H(j + round))), p + Lift + n * 0.006f);
        }
    }
}
