using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Kulturite (crystals.md): the stone of the culture house. Pastel iridescent crystals (<c>shaders/crystal_iridescent.gdshader</c>) with a soft warm light, on a rock base that is a parquet floor
/// (<c>shaders/crystal_rock_parquet.gdshader</c>). No props. Tunables: data/crystals.json "kulturite".
/// </summary>
public sealed partial class KulturiteFx : CrystalFx
{
    public override string Id => "kulturite";

    ShaderMaterial _crystal = null!, _rock = null!;
    OmniLight3D _light = null!;

    protected override void Build()
    {
        _rock = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_rock_parquet.gdshader") };
        _crystal = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_iridescent.gdshader") };
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);
    }

    protected override void OnApply()
    {
        _rock.SetShaderParameter("wood", P.C("wood", "#9e6633"));
        _rock.SetShaderParameter("wood_dark", P.C("wood_dark", "#663d1c"));
        _rock.SetShaderParameter("seam_color", P.C("parquet_seam_color", "#1f120a"));
        _rock.SetShaderParameter("square", P.F("parquet_square", 0.09f));
        _rock.SetShaderParameter("planks", (float)P.I("parquet_planks", 4));
        _rock.SetShaderParameter("seam", P.F("parquet_seam", 0.035f));
        _rock.SetShaderParameter("rough", P.F("rock_roughness", 0.35f));
        _crystal.SetShaderParameter("saturation", P.F("saturation", 0.45f));
        _crystal.SetShaderParameter("shift", P.F("hue_shift", 1.2f));
        _crystal.SetShaderParameter("band", P.F("hue_band", 1.5f));
        _crystal.SetShaderParameter("drift", P.F("hue_drift", 0.03f));
        _crystal.SetShaderParameter("alpha", P.F("crystal_alpha", 0.6f));
        _crystal.SetShaderParameter("rim_alpha", P.F("rim_alpha", 0.35f));
        _crystal.SetShaderParameter("rim_power", P.F("rim_power", 2.5f));
        _crystal.SetShaderParameter("glow", P.F("base_glow", 0.5f));
        _crystal.SetShaderParameter("glow_opacity", P.F("glow_opacity", 0.15f));
        _crystal.SetShaderParameter("rough", P.F("crystal_roughness", 0.06f));
        _light.LightColor = P.C("light", "#ffe9b0");
        _light.LightEnergy = P.F("light_energy", 0.7f);
        _light.OmniRange = P.F("light_range", 2.2f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.7f, 0.5f)));
        SetLoop(P.S("music_sound", ""), P.F("music_db", -22f));
    }

    protected override void Tick(double dt) { }
}
