using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Dust in the eyes (#345): one full-screen procedural layer (<c>shaders/dust_veil.gdshader</c>) under every other HUD element, so the HUD stays readable
/// while the world behind it is hidden; mouse filter ignore. One knob: <see cref="SetDust"/> with how much of the view is lost, 0..1
/// (<see cref="Drycastle.ShooterCore.Effects.DustField.Veil"/>). It is the world's dust seen from inside, not a UI cue: the blast's sound and shake come with it.
/// The dust holds still under reduce motion. Written when a value changes; nothing is built or allocated per frame.
/// </summary>
public partial class Hud
{
    static readonly StringName DustIntensity = "intensity", DustDrift = "drift", DustColour = "dust_color";
    ColorRect _dust = null!;
    ShaderMaterial? _dustMat;
    float _dustShown = -1f, _dustNight = -1f;
    bool _dustStill;

    void BuildDust()
    {
        _dust = new ColorRect { Color = DustFx.Day, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _dust.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        if (GD.Load<Shader>("res://shaders/dust_veil.gdshader") is { } shader) _dust.Material = _dustMat = new ShaderMaterial { Shader = shader };
        _root.AddChild(_dust);
    }

    /// <summary>How much of the view the dust takes, 0 (off, the layer is hidden) to 1; <paramref name="night"/> is the night factor, 0 at full day.</summary>
    public void SetDust(float intensity, float night)
    {
        intensity = Mathf.Clamp(intensity, 0f, 1f);
        var on = intensity > 0.004f;
        if (_dust.Visible != on) _dust.Visible = on;
        if (!on) { _dustShown = -1f; return; }
        var still = GameSettings.Current.ReduceMotion;
        if (!Mathf.IsEqualApprox(_dustNight, night, 0.02f))
        {
            _dustNight = night;
            var c = DustFx.Day.Lerp(DustFx.Night, Mathf.Clamp(night, 0f, 1f));
            if (_dustMat != null) _dustMat.SetShaderParameter(DustColour, c); else _dust.Color = c;
        }
        if (_dustMat == null) { _dust.Modulate = new Color(1, 1, 1, intensity); return; } // the shader is missing: a flat veil
        if (still != _dustStill || _dustShown < 0f) { _dustStill = still; _dustMat.SetShaderParameter(DustDrift, still ? 0f : 1f); }
        if (Mathf.IsEqualApprox(_dustShown, intensity, 0.002f)) return;
        _dustShown = intensity;
        _dustMat.SetShaderParameter(DustIntensity, intensity);
    }
}
