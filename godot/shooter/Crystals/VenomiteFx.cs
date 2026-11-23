using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Venomite (crystals.md): neon toxic green translucent crystals full of liquid: a slow swirl with bubbles rising through it (<c>shaders/crystal_water.gdshader</c>, <c>bubbles</c>), a green light. Venom
/// drips off the tips, a sizzling puddle with a little smoke sits on the rock among patches of dead grass. Tunables: data/crystals.json "venomite".
/// </summary>
public sealed partial class VenomiteFx : CrystalFx
{
    public override string Id => "venomite";

    StandardMaterial3D _rock = null!, _venom = null!;
    ShaderMaterial _crystal = null!;
    OmniLight3D _light = null!;
    DripSet _drips = null!;
    List<Vector3> _from = new();
    MeshInstance3D _puddle = null!;
    MeshInstance3D[] _grass = null!;
    CpuParticles3D _smoke = null!;

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.9f);
        _crystal = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_water.gdshader") };
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);
        _venom = new StandardMaterial3D { EmissionEnabled = true, Roughness = 0.05f };
        _puddle = Disc("VenomPuddle", _venom);
        _drips = new DripSet(this, _venom, 6);
        _grass = Tufts(20, new Color(0.25f, 0.2f, 0.08f), new Color(0.62f, 0.56f, 0.3f));
        _smoke = Emitter(false);
        Swell(_smoke, 0.4f, 2f);
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#3a4030");
        _crystal.SetShaderParameter("deep", P.C("deep", "#0a5a10"));
        _crystal.SetShaderParameter("shallow", P.C("shallow", "#7dff3a"));
        _crystal.SetShaderParameter("foam", P.C("bubble_color", "#e6ffb0"));
        _crystal.SetShaderParameter("flow_speed", P.F("swirl_speed", 0.08f));
        _crystal.SetShaderParameter("turbulence", P.F("turbulence", 0.9f));
        _crystal.SetShaderParameter("surge", 0f);
        _crystal.SetShaderParameter("scale", P.F("liquid_scale", 5f));
        _crystal.SetShaderParameter("stretch", 1f);
        _crystal.SetShaderParameter("crest", P.F("crest", 0.5f));
        _crystal.SetShaderParameter("crest_width", P.F("crest_width", 0.03f));
        _crystal.SetShaderParameter("bubbles", P.F("bubbles", 1f));
        _crystal.SetShaderParameter("bubble_scale", P.F("bubble_scale", 26f));
        _crystal.SetShaderParameter("bubble_speed", P.F("bubble_speed", 0.12f));
        _crystal.SetShaderParameter("alpha", P.F("crystal_alpha", 0.6f));
        _crystal.SetShaderParameter("rim_alpha", P.F("rim_alpha", 0.35f));
        _crystal.SetShaderParameter("glow", P.F("base_glow", 1.2f));
        _light.LightColor = P.C("light", "#6dff2a");
        _light.LightEnergy = P.F("light_energy", 1f);
        _light.OmniRange = P.F("light_range", 2.4f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.6f, 0.5f)));

        var venom = P.C("venom_color", "#7dff3a");
        _venom.AlbedoColor = venom;
        _venom.Emission = venom;
        _venom.EmissionEnergyMultiplier = P.F("venom_energy", 1.5f);
        var spot = FlatRock(P.I("puddle_seed", 6));
        _puddle.Visible = P.B("dress_puddle", true) && spot is not null;
        var at = spot?.P ?? Box.GetCenter();
        if (_puddle.Visible) { var r = P.F("puddle_radius", 0.06f); PlaceDisc(_puddle, spot!.Value, r, r * 0.75f, 0.9f); }
        var smoke = P.C("smoke_color", "#c9e8b0");
        Configure(_smoke, _puddle.Visible ? P.I("smoke_amount", 6) : 0, P.F("smoke_lifetime", 1.8f), at + new Vector3(0, 0.01f, 0), 0.03f,
            P.F("smoke_size", 0.02f), 0.08f, Vector3.Up, 20f, 0.02f, Ramp(new Color(smoke, 0f), new Color(smoke, 0.3f), new Color(smoke, 0f)));
        PlaceTufts(_grass, P.B("dress_grass", true) ? Math.Clamp(P.I("grass_count", 8), 0, _grass.Length) : 0, P.I("grass_seed", 4), P.F("grass_height", 0.05f), 0.6f);
        _from = DripPoints(false);
        SetLoop(P.S("hiss_sound", ""), P.F("hiss_db", -22f));
    }

    protected override void Tick(double dt) =>
        _drips.Update(Time, _from, Math.Clamp(P.I("drip_count", 3), 0, 6), P.F("drip_interval", 3f), P.F("drip_size", 0.007f));
}
