using Godot;
using Drycastle.Shooter.Effects;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp.Vision;

/// <summary>
/// Dust in the eyes (#345): one full-screen procedural layer (<c>shaders/dust_veil.gdshader</c>) under every other HUD element, so the HUD stays readable
/// while the world behind it is hidden; mouse filter ignore. One knob: <see cref="Set"/> with how much of the view is lost, 0..1
/// (<see cref="Drycastle.ShooterCore.Effects.DustField.Veil"/>). It is the world's dust seen from inside, not a UI cue: the blast's sound and shake come with it.
/// The dust holds still under reduce motion. Written when a value changes; nothing is built or allocated per frame.
/// </summary>
internal sealed class DustVeil
{
    static readonly StringName IntensityParam = "intensity", DriftParam = "drift", ColourParam = "dust_color";

    readonly ColorRect _rect;
    readonly ShaderMaterial? _material;
    float _shown = -1f, _night = -1f;
    bool _still;

    public DustVeil(Control root)
    {
        _rect = new ColorRect { Color = DustFx.Day, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        if (GD.Load<Shader>("res://shaders/dust_veil.gdshader") is { } shader) _rect.Material = _material = new ShaderMaterial { Shader = shader };
        root.AddChild(_rect);
    }

    /// <summary>How much of the view the dust takes, 0 (off, the layer is hidden) to 1; <paramref name="night"/> is the night factor, 0 at full day.</summary>
    public void Set(float intensity, float night)
    {
        intensity = Mathf.Clamp(intensity, 0f, 1f);
        var on = intensity > 0.004f;
        if (_rect.Visible != on) _rect.Visible = on;
        if (!on) { _shown = -1f; return; }
        var still = GameSettings.Current.ReduceMotion;
        if (!Mathf.IsEqualApprox(_night, night, 0.02f))
        {
            _night = night;
            var c = DustFx.Day.Lerp(DustFx.Night, Mathf.Clamp(night, 0f, 1f));
            if (_material != null) _material.SetShaderParameter(ColourParam, c); else _rect.Color = c;
        }
        if (_material == null) { _rect.Modulate = new Color(1, 1, 1, intensity); return; } // the shader is missing: a flat veil
        if (still != _still || _shown < 0f) { _still = still; _material.SetShaderParameter(DriftParam, still ? 0f : 1f); }
        if (Mathf.IsEqualApprox(_shown, intensity, 0.002f)) return;
        _shown = intensity;
        _material.SetShaderParameter(IntensityParam, intensity);
    }
}
