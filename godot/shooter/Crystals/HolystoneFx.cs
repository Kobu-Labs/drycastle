using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Holystone (crystals.md): white-gold translucent crystals (<c>shaders/crystal_quartz.gdshader</c>) with a slow warm glow and light, a thin golden halo over the tallest tip, a faint shaft of light from
/// above, dust motes floating in it and a couple of lit candles on the rock. Tunables: data/crystals.json "holystone".
/// </summary>
public sealed partial class HolystoneFx : CrystalFx
{
    public override string Id => "holystone";

    const int MaxCandles = 4;

    StandardMaterial3D _rock = null!, _haloMat = null!, _shaftMat = null!, _flameMat = null!;
    ShaderMaterial _crystal = null!;
    OmniLight3D _light = null!;
    MeshInstance3D _halo = null!, _shaft = null!;
    readonly Node3D[] _candles = new Node3D[MaxCandles];
    readonly MeshInstance3D[] _flames = new MeshInstance3D[MaxCandles];
    readonly OmniLight3D[] _candleLights = new OmniLight3D[MaxCandles];
    CpuParticles3D _motes = null!;

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.85f);
        _crystal = Quartz();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);
        _haloMat = new StandardMaterial3D { EmissionEnabled = true, Roughness = 0.3f, Metallic = 0.6f };
        _halo = new MeshInstance3D { Name = "Halo", Mesh = new TorusMesh { InnerRadius = 0.88f, OuterRadius = 1f, Rings = 32, RingSegments = 8, Material = _haloMat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_halo);
        _shaftMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _shaft = new MeshInstance3D { Name = "LightShaft", Mesh = new CylinderMesh { TopRadius = 0.45f, BottomRadius = 1f, Height = 1f, RadialSegments = 16, Rings = 1, CapTop = false, CapBottom = false, Material = _shaftMat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_shaft);

        var wax = new StandardMaterial3D { AlbedoColor = new Color(0.93f, 0.9f, 0.8f), Roughness = 0.6f };
        _flameMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.8f, 0.35f), EmissionEnabled = true, Emission = new Color(1f, 0.7f, 0.25f), EmissionEnergyMultiplier = 3f };
        for (var i = 0; i < MaxCandles; i++)
        {
            // a candle of unit height standing on the origin
            var c = new Node3D { Name = $"Candle{i}" };
            AddChild(c);
            c.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.18f, Height = 1f, RadialSegments = 10, Material = wax }, Position = new Vector3(0, 0.5f, 0) });
            _flames[i] = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4, Material = _flameMat }, Position = new Vector3(0, 1.22f, 0), Scale = new Vector3(0.2f, 0.42f, 0.2f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            c.AddChild(_flames[i]);
            _candleLights[i] = new OmniLight3D { ShadowEnabled = false, Position = new Vector3(0, 1.3f, 0), LightColor = new Color(1f, 0.72f, 0.35f) };
            c.AddChild(_candleLights[i]);
            _candles[i] = c;
        }
        _motes = Emitter(true);
        _motes.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#b8b0a0");
        ApplyQuartz(_crystal, new[] { "#fff3c4", "#ffe08a", "#fffdf2" }, "#ffffff");
        _crystal.SetShaderParameter("glow_opacity", P.F("glow_opacity", 0.15f));
        _light.LightColor = P.C("light", "#ffe6a8");
        _light.OmniRange = P.F("light_range", 2.4f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.7f, 0.5f)));

        var gold = P.C("halo_color", "#ffd76a");
        _haloMat.AlbedoColor = gold;
        _haloMat.Emission = gold;
        _haloMat.EmissionEnergyMultiplier = P.F("halo_energy", 2.5f);
        _halo.Visible = P.B("dress_halo", true);
        var tall = Info.Tips.Count > 0 ? Tallest(1)[0] : 0;
        _halo.Transform = new Transform3D(Basis.FromScale(Vector3.One * P.F("halo_radius", 0.09f)), Tip(tall) + new Vector3(0, P.F("halo_height", 0.07f), 0));

        _shaft.Visible = P.B("dress_shaft", true);
        _shaftMat.AlbedoColor = new Color(P.C("shaft_color", "#fff0c0"), P.F("shaft_alpha", 0.05f));
        var top = Box.Size.Y + P.F("shaft_above", 0.5f);
        var r = Math.Max(Box.Size.X, Box.Size.Z) * 0.5f * P.F("shaft_width", 0.8f);
        _shaft.Transform = new Transform3D(Basis.FromScale(new Vector3(r, top, r)), new Vector3(Box.GetCenter().X, top * 0.5f, Box.GetCenter().Z));

        var cn = P.B("dress_candles", true) && Info.RockUp.Count > 0 ? Math.Clamp(P.I("candle_count", 2), 0, MaxCandles) : 0;
        for (var i = 0; i < MaxCandles; i++)
        {
            _candles[i].Visible = i < cn;
            if (i >= cn) continue;
            var s = FlatRock(P.I("candle_seed", 3) + i * 41)!.Value;
            var h = P.F("candle_height", 0.06f) * (0.7f + 0.5f * Hash01(i * 2.9));
            _candles[i].Transform = new Transform3D(Basis.FromScale(Vector3.One * h), s.P);
            _candleLights[i].OmniRange = 0.5f / h; // the candle is scaled: about half a metre of reach
        }

        var mote = P.C("mote_color", "#ffe9a0");
        _motes.EmissionBoxExtents = new Vector3(Box.Size.X * 0.4f, Box.Size.Y * 0.5f, Box.Size.Z * 0.4f);
        Configure(_motes, P.I("mote_amount", 16), P.F("mote_lifetime", 5f), new Vector3(Box.GetCenter().X, Box.Size.Y * 0.6f, Box.GetCenter().Z), 0.01f,
            P.F("mote_size", 0.003f), 0.02f, Vector3.Up, 90f, 0.005f, Ramp(new Color(mote, 0f), new Color(mote, 0.9f), new Color(mote, 0f)));
        _motes.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
        SetLoop(P.S("choir_sound", ""), P.F("choir_db", -22f));
    }

    protected override void Tick(double dt)
    {
        var breathe = 1f + P.F("breathe", 0.25f) * Mathf.Sin((float)Time * P.F("breathe_speed", 0.6f));
        _crystal.SetShaderParameter("glow", P.F("base_glow", 0.9f) * breathe);
        _light.LightEnergy = P.F("light_energy", 0.9f) * breathe;
        for (var i = 0; i < MaxCandles; i++)
        {
            if (!_candles[i].Visible) continue;
            var f = DemonFx.Flicker(Time, 1.3f + i * 2.7f, 0.7f, 1.2f);
            _flames[i].Scale = new Vector3(0.2f, 0.42f * f, 0.2f);
            _candleLights[i].LightEnergy = P.F("candle_light", 0.35f) * f;
        }
    }
}
