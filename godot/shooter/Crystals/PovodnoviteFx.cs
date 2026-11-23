using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Povodnovite (crystals.md): the flood stone, a raging river caught in a thin skin of glass. Churned blue-green water tears through the crystals, white with crests and swells (<c>shaders/crystal_water.gdshader</c>), a faint
/// aqua light. The rock is wet and dark with algae on its top, reeds with cattails grow on it. Spray bursts off the crystals, drops rain from their tips and undersides, mist hangs over it. Everything sits on the model;
/// the drops are a pure function of time. Tunables: data/crystals.json "povodnovite".
/// </summary>
public sealed partial class PovodnoviteFx : CrystalFx
{
    public override string Id => "povodnovite";

    const int MaxReeds = 20, MaxDrops = 12;

    ShaderMaterial _rock = null!, _crystal = null!;
    OmniLight3D _light = null!;
    readonly Node3D[] _reeds = new Node3D[MaxReeds];
    readonly MeshInstance3D[] _drops = new MeshInstance3D[MaxDrops];
    readonly List<Vector3> _dripFrom = new();
    StandardMaterial3D _dropMat = null!;
    CpuParticles3D _mist = null!, _spray = null!;

    protected override void Build()
    {
        _rock = MossRock();
        _crystal = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_water.gdshader") };
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);

        // a reed of unit height: a tuft of long blades, a stalk and a brown cattail
        var plant = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var blades = new[] { SmetiskomiteFx.TuftMesh(plant, 0, new Color(0.08f, 0.25f, 0.08f), new Color(0.45f, 0.62f, 0.25f)), SmetiskomiteFx.TuftMesh(plant, 1, new Color(0.08f, 0.25f, 0.08f), new Color(0.4f, 0.58f, 0.22f)) };
        var stalk = new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.016f, Height = 1f, RadialSegments = 5, Material = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.42f, 0.16f), Roughness = 0.9f } };
        var head = new CapsuleMesh { Radius = 0.04f, Height = 0.26f, RadialSegments = 8, Rings = 3, Material = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.17f, 0.08f), Roughness = 1f } };
        for (var i = 0; i < MaxReeds; i++)
        {
            var r = new Node3D { Name = $"Reed{i}" };
            AddChild(r);
            r.AddChild(new MeshInstance3D { Mesh = blades[i % 2], Scale = new Vector3(0.35f, 0.8f, 0.35f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            r.AddChild(new MeshInstance3D { Mesh = stalk, Position = new Vector3(0, 0.5f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            r.AddChild(new MeshInstance3D { Mesh = head, Position = new Vector3(0, 0.95f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            _reeds[i] = r;
        }

        _dropMat = new StandardMaterial3D { Roughness = 0.02f, EmissionEnabled = true };
        var ball = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 8, Rings = 4, Material = _dropMat };
        for (var i = 0; i < MaxDrops; i++)
        {
            _drops[i] = new MeshInstance3D { Name = $"Drop{i}", Mesh = ball, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_drops[i]);
        }

        _mist = Emitter(false);
        _mist.ScaleAmountCurve = new Curve();
        _mist.ScaleAmountCurve.AddPoint(new Vector2(0f, 0.4f));
        _mist.ScaleAmountCurve.AddPoint(new Vector2(1f, 2.4f));
        _spray = Emitter(false);
        _spray.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
    }

    static float H(double n)
    {
        var s = Math.Sin(n * 12.9898 + 2.9) * 43758.5453;
        return (float)(s - Math.Floor(s));
    }

    protected override void OnApply()
    {
        ApplyMossRock(_rock, "algae", "#2c3436", "#1d4a30", "#4f8a4a", 0.8f);
        _rock.SetShaderParameter("rough", P.F("rock_roughness", 0.3f)); // wet

        _crystal.SetShaderParameter("deep", P.C("deep", "#053a4d"));
        _crystal.SetShaderParameter("shallow", P.C("shallow", "#35b39e"));
        _crystal.SetShaderParameter("foam", P.C("foam", "#ebffff"));
        _crystal.SetShaderParameter("flow_speed", P.F("flow_speed", 0.9f));
        _crystal.SetShaderParameter("turbulence", P.F("turbulence", 0.6f));
        _crystal.SetShaderParameter("surge", P.F("surge", 0.3f));
        _crystal.SetShaderParameter("scale", P.F("water_scale", 7f));
        _crystal.SetShaderParameter("stretch", P.F("water_stretch", 0.4f));
        _crystal.SetShaderParameter("crest", P.F("crest", 0.9f));
        _crystal.SetShaderParameter("crest_width", P.F("crest_width", 0.09f));
        _crystal.SetShaderParameter("alpha", P.F("crystal_alpha", 0.72f));
        _crystal.SetShaderParameter("rim_alpha", P.F("rim_alpha", 0.3f));
        _crystal.SetShaderParameter("rim_power", P.F("rim_power", 2.5f));
        _crystal.SetShaderParameter("glow", P.F("base_glow", 0.25f));

        _light.LightColor = P.C("light", "#4fd6c8");
        _light.LightEnergy = P.F("light_energy", 0.4f);
        _light.OmniRange = P.F("light_range", 2f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.6f, 0.5f)));

        var rn = P.B("dress_reeds", true) && Info.RockUp.Count > 0 ? Math.Clamp(P.I("reed_count", 9), 0, MaxReeds) : 0;
        for (var i = 0; i < MaxReeds; i++)
        {
            _reeds[i].Visible = i < rn;
            if (i >= rn) continue;
            var s = RockSpot(P.I("reed_seed", 6) + i * 19)!.Value;
            var h = P.F("reed_height", 0.2f) * (0.6f + 0.8f * H(i * 1.7));
            _reeds[i].Transform = new Transform3D(OnSurface(s.N * 0.4f + Vector3.Up, i * 2.4f).Scaled(Vector3.One * h), s.P);
        }

        _dripFrom.Clear();
        foreach (var (p, n, _) in Info.Facets) if (n.Y < -0.2f) _dripFrom.Add(p + Lift);
        for (var i = 0; i < Info.Tips.Count; i++) _dripFrom.Add(Tip(i));
        var water = P.C("drop_color", "#8fe0e6");
        _dropMat.AlbedoColor = water;
        _dropMat.Emission = water;
        _dropMat.EmissionEnergyMultiplier = 0.3f;

        var mist = P.C("mist_color", "#cfeef0");
        Configure(_mist, P.I("mist_amount", 22), P.F("mist_lifetime", 3.5f), new Vector3(Box.GetCenter().X, Box.Size.Y * P.F("mist_height", 0.75f), Box.GetCenter().Z), 0.12f,
            P.F("mist_size", 0.06f), P.F("mist_speed", 0.07f), Vector3.Up, 35f, 0.01f, Ramp(new Color(mist, 0f), new Color(mist, P.F("mist_alpha", 0.2f)), new Color(mist, 0f)));

        // white water thrown off the crystals: bursts up and out, falls back
        var foam = P.C("foam", "#ebffff");
        _spray.EmissionBoxExtents = new Vector3(Box.Size.X * 0.3f, Box.Size.Y * 0.2f, Box.Size.Z * 0.3f);
        Configure(_spray, P.I("spray_amount", 40), P.F("spray_lifetime", 0.7f), new Vector3(Box.GetCenter().X, Box.Size.Y * P.F("spray_height", 0.6f), Box.GetCenter().Z), 0.01f,
            P.F("spray_size", 0.006f), P.F("spray_speed", 0.9f), new Vector3(0.6f, 1f, 0f), 55f, -3.5f, Ramp(new Color(foam, 0.9f), new Color(foam, 0.7f), new Color(foam, 0f)));
        _spray.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;

        SetLoop(P.S("stream_sound", ""), P.F("stream_db", -22f));
    }

    protected override void Tick(double dt)
    {
        // drops: each one swells where it hangs, falls, is gone at the ground, then hangs somewhere else
        var dn = _dripFrom.Count > 0 ? Math.Clamp(P.I("drop_count", 12), 0, MaxDrops) : 0;
        var size = P.F("drop_size", 0.006f);
        for (var j = 0; j < MaxDrops; j++)
        {
            var d = _drops[j];
            d.Visible = j < dn;
            if (j >= dn) continue;
            var period = P.F("drop_interval", 0.9f) * (0.6f + 0.8f * H(j * 3.3));
            var x = (Time + H(j * 7.9) * 50.0) / period;
            var round = Math.Floor(x);
            var t = (float)((x - round) * period);
            var from = _dripFrom[(int)(H(j * 13.1 + round * 5.7) * _dripFrom.Count) % _dripFrom.Count];
            var fall = Mathf.Sqrt(2f * Math.Max(from.Y, 0.01f) / 9.8f);
            var hang = period - fall;
            if (t < hang) { d.Transform = new Transform3D(Basis.FromScale(Vector3.One * size * (0.3f + 0.7f * t / hang)), from + Vector3.Down * size); continue; }
            var ft = t - hang;
            d.Transform = new Transform3D(Basis.FromScale(new Vector3(size * 0.8f, size * 1.3f, size * 0.8f)), new Vector3(from.X, Math.Max(from.Y - 4.9f * ft * ft, 0f), from.Z));
        }
    }
}
