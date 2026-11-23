using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Hovnovite (crystals.md): the manure stone. Wet, shiny brown-green crystals (<c>shaders/crystal_veined.gdshader</c>) in a fresh dark pile with straw sticking out of it. No glow. Steam rises from it,
/// a yellow-green wisp of stink wavers above it, flies circle a tip. Tunables: data/crystals.json "hovnovite".
/// </summary>
public sealed partial class HovnoviteFx : CrystalFx
{
    public override string Id => "hovnovite";

    StandardMaterial3D _rock = null!;
    ShaderMaterial _crystal = null!;
    MeshInstance3D[] _straw = null!;
    FlySwarm _flies = null!;
    CpuParticles3D _steam = null!, _stink = null!;

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.35f);
        _crystal = Veined();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _straw = Tufts(30, new Color(0.45f, 0.34f, 0.1f), new Color(0.88f, 0.76f, 0.38f));
        _flies = new FlySwarm(this, Rng, 16);
        _steam = Emitter(false);
        Swell(_steam, 0.4f, 2.6f);
        _stink = Emitter(false);
        Swell(_stink, 0.5f, 1.8f);
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#3a2a14");
        _rock.Roughness = P.F("rock_roughness", 0.35f);
        ApplyVeined(_crystal, new[] { "#4a3a18", "#55501e", "#3f3312" }, "#2a200a", 0.5f, 0f, 0.12f);
        PlaceTufts(_straw, P.B("dress_straw", true) ? Math.Clamp(P.I("straw_count", 16), 0, _straw.Length) : 0, P.I("straw_seed", 7), P.F("straw_size", 0.07f), P.F("straw_flatten", 0.6f));

        var steam = P.C("steam_color", "#e8e4d8");
        Configure(_steam, P.I("steam_amount", 10), P.F("steam_lifetime", 3f), new Vector3(Box.GetCenter().X, Box.Size.Y * 0.3f, Box.GetCenter().Z), 0.2f,
            P.F("steam_size", 0.05f), 0.1f, Vector3.Up, 20f, 0.02f, Ramp(new Color(steam, 0f), new Color(steam, P.F("steam_alpha", 0.16f)), new Color(steam, 0f)));
        var stink = P.C("stink_color", "#a8b03a");
        Configure(_stink, P.I("stink_amount", 12), P.F("stink_lifetime", 2.6f), Vector3.Zero, 0.02f,
            P.F("stink_size", 0.022f), 0.14f, Vector3.Up, 6f, 0.01f, Ramp(new Color(stink, 0f), new Color(stink, P.F("stink_alpha", 0.3f)), new Color(stink, 0f)));
        SetLoop(P.S("fly_sound", ""), P.F("fly_db", -22f));
    }

    protected override void Tick(double dt)
    {
        _flies.Update(Time, Math.Clamp(P.I("flies_count", 8), 0, 16), P.F("fly_size", 0.007f), Tip(P.I("flies_island", 1)) + new Vector3(0, 0.05f, 0), P.F("flies_radius", 0.13f), P.F("flies_speed", 3.2f));
        // the wisp wavers: its source swings from side to side over the tallest tip, so the trail it leaves is a wave
        var tip = Tip(Info.Tips.Count > 0 ? Tallest(1)[0] : 0);
        var wave = P.F("stink_wave", 0.035f);
        _stink.Position = tip + new Vector3(Mathf.Sin((float)Time * 2.4f) * wave, 0.02f, Mathf.Cos((float)Time * 1.7f) * wave * 0.6f);
    }
}
