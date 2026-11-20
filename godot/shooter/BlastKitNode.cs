using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The root of a pooled blast kit (<see cref="RocketFx"/>): drives the flash sphere, the fireball and the light from its own process while the blast
/// plays and hides the kit when it is over, so a blast makes no tween or callback. Idle kits do not process.
/// </summary>
public partial class BlastKitNode : Node3D
{
    const float FlashTime = 0.12f, FireballTime = 0.35f, FireballDelay = 0.1f, LightTime = 0.35f, Life = 2.2f, LightEnergy = 10f;

    MeshInstance3D _flash = null!, _fireball = null!;
    OmniLight3D _light = null!;
    float _r, _age;
    bool _lit;

    public override void _Ready() => SetProcess(false);

    public void Bind(MeshInstance3D flash, MeshInstance3D fireball, OmniLight3D light) { _flash = flash; _fireball = fireball; _light = light; }

    /// <summary>Starts (or restarts, taking over a blast still playing) the blast of radius <paramref name="r"/>.</summary>
    public void Play(float r, bool light)
    {
        _r = r; _lit = light; _age = 0;
        _light.LightEnergy = LightEnergy;
        Apply();
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;
        if (_age >= Life) { Visible = false; SetProcess(false); return; }
        Apply();
    }

    void Apply()
    {
        var f = Mathf.Clamp(_age / FlashTime, 0f, 1f);
        _flash.Scale = Vector3.One * _r * Mathf.Lerp(0.15f, 0.5f, f);
        _flash.Transparency = f;
        var s = Mathf.Clamp(_age / FireballTime, 0f, 1f);
        _fireball.Scale = Vector3.One * _r * Mathf.Lerp(0.2f, 0.55f, s);
        _fireball.Transparency = Mathf.Clamp((_age - FireballDelay) / FireballTime, 0f, 1f);
        if (_lit) _light.LightEnergy = LightEnergy * (1f - Mathf.Clamp(_age / LightTime, 0f, 1f));
    }
}
