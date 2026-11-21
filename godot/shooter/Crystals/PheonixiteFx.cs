using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Pheonixite (crystals.md): left behind when a pheonix dies. The crystal is a thin skin of glass with fire burning inside (<c>shaders/crystal_fire.gdshader</c>), on scorched rock; a flickering red
/// OmniLight, embers rising from the tips, a little ash off the rock and a fire crackle. Tunables: data/crystals.json "pheonixite".
/// </summary>
public sealed partial class PheonixiteFx : CrystalFx
{
    public override string Id => "pheonixite";

    StandardMaterial3D _rock = null!;
    ShaderMaterial _fire = null!;
    OmniLight3D _light = null!;
    CpuParticles3D _embers = null!, _ash = null!;

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.9f);
        _fire = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_fire.gdshader") };
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _fire);
        _light = new OmniLight3D { Name = "FireLight", ShadowEnabled = false };
        AddChild(_light);
        _embers = Emitter(true);
        _ash = Emitter(false);
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#3b2c27");
        _rock.Roughness = P.F("rock_roughness", 0.9f);
        var ember = P.F("rock_ember", 0f);
        _rock.EmissionEnabled = ember > 0f;
        _rock.Emission = P.C("mid", "#ff7410");
        _rock.EmissionEnergyMultiplier = ember;

        _fire.SetShaderParameter("core_color", P.C("core", "#ff1f00"));
        _fire.SetShaderParameter("mid_color", P.C("mid", "#ff7410"));
        _fire.SetShaderParameter("tip_color", P.C("tip", "#ffd466"));
        _fire.SetShaderParameter("glass_tint", P.C("glass_tint", "#1f0503"));
        _fire.SetShaderParameter("speed", P.F("fire_speed", 0.9f));
        _fire.SetShaderParameter("noise_scale", P.F("fire_scale", 7f));
        _fire.SetShaderParameter("energy", P.F("fire_energy", 3.2f));
        _fire.SetShaderParameter("cooling", P.F("fire_cooling", 0.55f));
        _fire.SetShaderParameter("depth_step", P.F("fire_depth_step", 0.035f));
        _fire.SetShaderParameter("glass_alpha", P.F("glass_alpha", 0.55f));
        _fire.SetShaderParameter("rim_power", P.F("rim_power", 2.5f));
        _fire.SetShaderParameter("rim_energy", P.F("rim_energy", 0.8f));
        _fire.SetShaderParameter("y_min", MeshBox.Position.Y);
        _fire.SetShaderParameter("y_max", MeshBox.End.Y);

        _light.LightColor = P.C("light", "#ff5a1a");
        _light.OmniRange = P.F("light_range", 2.5f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.7f, 0.5f)));

        var emberColor = P.C("ember_color", "#ff9a30");
        Configure(_embers, P.I("ember_amount", 14), P.F("ember_lifetime", 1.6f), At(P.V("ember_at", new Vector3(0.5f, 0.75f, 0.5f))), P.F("ember_radius", 0.12f),
            P.F("ember_size", 0.008f), P.F("ember_speed", 0.25f), Vector3.Up, 30f, 0.15f,
            Ramp(emberColor, new Color(emberColor, 0.8f), new Color(emberColor.R * 0.5f, 0f, 0f, 0f)));
        var ashColor = P.C("ash_color", "#8a8580");
        Configure(_ash, P.I("ash_amount", 8), P.F("ash_lifetime", 3f), At(P.V("ash_at", new Vector3(0.5f, 0.2f, 0.5f))), P.F("ash_radius", 0.25f),
            P.F("ash_size", 0.01f), P.F("ash_speed", 0.08f), Vector3.Up, 50f, 0.05f,
            Ramp(new Color(ashColor, 0f), new Color(ashColor, 0.6f), new Color(ashColor, 0f)));

        SetLoop(P.S("loop_sound", ""), P.F("loop_db", -18f));
    }

    protected override void Tick(double dt)
    {
        _light.LightEnergy = DemonFx.Flicker(Time, 3.1f, P.F("flicker_low", 0.6f), P.F("flicker_high", 1.35f)) * P.F("light_energy", 1.6f);
    }
}
