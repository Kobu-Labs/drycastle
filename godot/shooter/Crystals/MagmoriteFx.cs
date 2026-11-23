using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Magmorite (crystals.md): black basalt crystals with glowing orange cracks that creep and pulse, ember specks (<c>shaders/crystal_veined.gdshader</c>), an orange light. Lava oozes from the tips, a small
/// lava puddle cools on the scorched rock, thin dark smoke rises. No flames (that is pheonixite). Tunables: data/crystals.json "magmorite".
/// </summary>
public sealed partial class MagmoriteFx : CrystalFx
{
    public override string Id => "magmorite";

    StandardMaterial3D _rock = null!, _lava = null!;
    ShaderMaterial _crystal = null!;
    OmniLight3D _light = null!;
    DripSet _drips = null!;
    List<Vector3> _from = new();
    MeshInstance3D _puddle = null!;
    CpuParticles3D _smoke = null!;

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.95f);
        _crystal = Veined();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);
        _lava = new StandardMaterial3D { EmissionEnabled = true, Roughness = 0.4f };
        _puddle = Disc("LavaPuddle", _lava);
        _drips = new DripSet(this, _lava, 5);
        _smoke = Emitter(false);
        Swell(_smoke, 0.4f, 2.6f);
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#221c1a");
        ApplyVeined(_crystal, new[] { "#14110f", "#1b1512" }, "#ff6a10", 1f, 5f, 0.6f, 0f, 0.15f, "#ff8a30");
        _crystal.SetShaderParameter("vein_width", P.F("vein_width", 0.07f));
        _crystal.SetShaderParameter("vein_scroll", P.F("vein_scroll", 0.05f));
        _crystal.SetShaderParameter("speck_glow", P.F("speck_glow", 2f));
        _light.LightColor = P.C("light", "#ff6a1a");
        _light.OmniRange = P.F("light_range", 2.4f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.6f, 0.5f)));
        var lava = P.C("lava_color", "#ff5a10");
        _lava.AlbedoColor = lava.Darkened(0.5f);
        _lava.Emission = lava;
        _puddle.Visible = P.B("dress_puddle", true) && FlatRock(P.I("puddle_seed", 3)) is not null;
        if (_puddle.Visible) { var r = P.F("puddle_radius", 0.07f); PlaceDisc(_puddle, FlatRock(P.I("puddle_seed", 3))!.Value, r, r * 0.7f, 0.5f); }
        _from = DripPoints(false);
        var smoke = P.C("smoke_color", "#1c1a19");
        Configure(_smoke, P.I("smoke_amount", 10), P.F("smoke_lifetime", 3.5f), new Vector3(Box.GetCenter().X, Box.Size.Y * 0.8f, Box.GetCenter().Z), 0.1f,
            P.F("smoke_size", 0.04f), P.F("smoke_speed", 0.12f), Vector3.Up, 18f, 0.02f, Ramp(new Color(smoke, 0f), new Color(smoke, P.F("smoke_alpha", 0.35f)), new Color(smoke, 0f)));
        SetLoop(P.S("rumble_sound", ""), P.F("rumble_db", -22f));
    }

    protected override void Tick(double dt)
    {
        var pulse = 1f + P.F("pulse", 0.3f) * Mathf.Sin((float)Time * P.F("pulse_speed", 1.3f));
        _crystal.SetShaderParameter("pulse", pulse);
        _light.LightEnergy = P.F("light_energy", 1.2f) * pulse * DemonFx.Flicker(Time, 3.1f, 0.9f, 1.1f);
        _lava.EmissionEnergyMultiplier = P.F("lava_energy", 3f) * (0.75f + 0.25f * Mathf.Sin((float)Time * 0.7f));
        _drips.Update(Time, _from, Math.Clamp(P.I("drip_count", 3), 0, 5), P.F("drip_interval", 5f), P.F("drip_size", 0.009f), P.F("ooze_speed", 0.12f));
    }
}
