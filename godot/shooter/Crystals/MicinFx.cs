using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Micin (crystals.md): in memory of a childhood dog. Crystals in a white furry coat (<c>shaders/crystal_fur.gdshader</c>) with a light white glow and a soft white light. Quiet: no props, no particles,
/// no sound, nothing moves. Tunables: data/crystals.json "micin".
/// </summary>
public sealed partial class MicinFx : CrystalFx
{
    public override string Id => "micin";

    StandardMaterial3D _rock = null!;
    ShaderMaterial _crystal = null!;
    OmniLight3D _light = null!;

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.9f);
        _crystal = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_fur.gdshader") };
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#8a8780");
        _crystal.SetShaderParameter("fur", P.C("fur", "#f5f2ea"));
        _crystal.SetShaderParameter("fur_shadow", P.C("fur_shadow", "#bdb8ad"));
        _crystal.SetShaderParameter("strand_scale", P.F("strand_scale", 160f));
        _crystal.SetShaderParameter("strand_length", P.F("strand_length", 0.06f));
        _crystal.SetShaderParameter("strand_depth", P.F("strand_depth", 0.55f));
        _crystal.SetShaderParameter("tuft_scale", P.F("tuft_scale", 9f));
        _crystal.SetShaderParameter("fuzz", P.F("fuzz", 0.7f));
        _crystal.SetShaderParameter("fuzz_power", P.F("fuzz_power", 2f));
        _crystal.SetShaderParameter("glow", P.F("base_glow", 0.25f));
        _light.LightColor = P.C("light", "#ffffff");
        _light.LightEnergy = P.F("light_energy", 0.5f);
        _light.OmniRange = P.F("light_range", 2.2f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.7f, 0.5f)));
    }

    protected override void Tick(double dt) { }
}
