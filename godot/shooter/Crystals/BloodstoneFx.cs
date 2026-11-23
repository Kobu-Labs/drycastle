using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Bloodstone (crystals.md): opaque deep crimson crystals with darker veins and a wet shine (<c>shaders/crystal_veined.gdshader</c>). The glow and a soft red light beat like a heart (two thumps a beat,
/// about 60 a minute). Now and then a drop of blood falls from a tip; dried splatters lie on the rock. Tunables: data/crystals.json "bloodstone".
/// </summary>
public sealed partial class BloodstoneFx : CrystalFx
{
    public override string Id => "bloodstone";

    const int MaxSplat = 8;

    StandardMaterial3D _rock = null!, _dried = null!, _wet = null!;
    ShaderMaterial _crystal = null!;
    OmniLight3D _light = null!;
    DripSet _drips = null!;
    List<Vector3> _from = new();
    readonly MeshInstance3D[] _splats = new MeshInstance3D[MaxSplat];

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.85f);
        _crystal = Veined();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);
        _dried = new StandardMaterial3D { Roughness = 0.8f };
        _wet = new StandardMaterial3D { Roughness = 0.05f };
        for (var i = 0; i < MaxSplat; i++) _splats[i] = Disc($"Splatter{i}", _dried);
        _drips = new DripSet(this, _wet, 4);
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#3a2626");
        ApplyVeined(_crystal, new[] { "#7a0612", "#5c040e", "#8f0a18" }, "#1e0205", 1f, 0f, 0.12f);
        _light.LightColor = P.C("light", "#ff2030");
        _light.OmniRange = P.F("light_range", 2.2f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.6f, 0.5f)));
        _dried.AlbedoColor = P.C("splatter_color", "#3a0508");
        _wet.AlbedoColor = P.C("blood_color", "#8a0410");
        var n = Info.RockUp.Count > 0 ? Math.Clamp(P.I("splatter_count", 5), 0, MaxSplat) : 0;
        for (var i = 0; i < MaxSplat; i++)
        {
            _splats[i].Visible = i < n;
            if (i >= n) continue;
            var r = P.F("splatter_radius", 0.05f) * (0.4f + 0.8f * Hash01(i * 2.1));
            PlaceDisc(_splats[i], RockSpot(P.I("splatter_seed", 8) + i * 29)!.Value, r, r * (0.5f + 0.5f * Hash01(i * 5.3)), i * 1.7f);
        }
        _from = DripPoints(false);
        SetLoop(P.S("heartbeat_sound", ""), P.F("heartbeat_db", -20f));
    }

    protected override void Tick(double dt)
    {
        var f = (float)(Time * P.F("bpm", 60f) / 60.0 % 1.0);
        var beat = Mathf.Exp(-f * 9f) + (f > 0.28f ? 0.6f * Mathf.Exp(-(f - 0.28f) * 11f) : 0f);
        _crystal.SetShaderParameter("glow", P.F("base_glow", 0.3f) + beat * P.F("glow_pulse", 1.2f));
        _light.LightEnergy = P.F("light_base", 0.35f) + beat * P.F("light_pulse", 1.1f);
        _drips.Update(Time, _from, Math.Clamp(P.I("drip_count", 2), 0, 4), P.F("drip_interval", 6f), P.F("drip_size", 0.007f));
    }
}
