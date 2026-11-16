using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Blood on the screen (#283): one full-screen procedural layer (<c>shaders/blood.gdshader</c>) above the respawn scrim; mouse filter ignore, so it never eats a click or a key.
/// One knob: <see cref="SetBlood"/> with an intensity 0..1. The death screen drives it; a low-health vignette (#189) can use the same layer with a small value. It is a colour effect only, never the sole cue:
/// the death is also the camera falling and the scrim with its text. Written when the value changes; nothing is built or allocated per frame.
/// </summary>
public partial class Hud
{
    static readonly StringName BloodIntensity = "intensity", BloodSeed = "seed";
    ColorRect _blood = null!;
    ShaderMaterial? _bloodMat;
    float _bloodShown;
    int _bloodSeed;

    void BuildBlood()
    {
        _blood = new ColorRect { Color = Colors.White, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _blood.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        if (GD.Load<Shader>("res://shaders/blood.gdshader") is { } shader) _blood.Material = _bloodMat = new ShaderMaterial { Shader = shader };
        _root.AddChild(_blood);
    }

    /// <summary>The blood's intensity, 0 (off, the layer is hidden) to 1 (the death screen's full spatter); <paramref name="seed"/> picks the pattern.</summary>
    public void SetBlood(float intensity, int seed = 0)
    {
        if (_bloodMat == null) return;
        intensity = Mathf.Clamp(intensity, 0f, 1f);
        var on = intensity > 0.002f;
        if (_blood.Visible != on) _blood.Visible = on;
        if (!on) { _bloodShown = 0; return; }
        if (Mathf.IsEqualApprox(_bloodShown, intensity, 0.002f) && seed == _bloodSeed) return;
        _bloodShown = intensity; _bloodSeed = seed;
        _bloodMat.SetShaderParameter(BloodIntensity, intensity);
        _bloodMat.SetShaderParameter(BloodSeed, (float)(seed % 997));
    }
}
