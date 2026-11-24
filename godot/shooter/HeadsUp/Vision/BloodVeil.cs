using Godot;

namespace Drycastle.Shooter.HeadsUp.Vision;

/// <summary>
/// Blood on the screen (#283): one full-screen procedural layer (<c>shaders/blood.gdshader</c>) above the respawn scrim; mouse filter ignore, so it never eats a click or a key.
/// One knob: <see cref="Set"/> with an intensity 0..1. The death screen drives it; a low-health vignette (#189) can use the same layer with a small value. It is a colour effect only, never the sole cue:
/// the death is also the camera falling and the scrim with its text. Written when the value changes; nothing is built or allocated per frame.
/// </summary>
internal sealed class BloodVeil
{
    static readonly StringName IntensityParam = "intensity", SeedParam = "seed";

    readonly ColorRect _rect;
    readonly ShaderMaterial? _material;
    float _shown;
    int _seed;

    public BloodVeil(Control root)
    {
        _rect = new ColorRect { Color = Colors.White, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        if (GD.Load<Shader>("res://shaders/blood.gdshader") is { } shader) _rect.Material = _material = new ShaderMaterial { Shader = shader };
        root.AddChild(_rect);
    }

    /// <summary>The blood's intensity, 0 (off, the layer is hidden) to 1 (the death screen's full spatter); <paramref name="seed"/> picks the pattern.</summary>
    public void Set(float intensity, int seed)
    {
        if (_material == null) return;
        intensity = Mathf.Clamp(intensity, 0f, 1f);
        var on = intensity > 0.002f;
        if (_rect.Visible != on) _rect.Visible = on;
        if (!on) { _shown = 0; return; }
        if (Mathf.IsEqualApprox(_shown, intensity, 0.002f) && seed == _seed) return;
        _shown = intensity; _seed = seed;
        _material.SetShaderParameter(IntensityParam, intensity);
        _material.SetShaderParameter(SeedParam, (float)(seed % 997));
    }
}
