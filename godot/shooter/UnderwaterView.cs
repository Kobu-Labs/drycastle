using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The underwater view: while the camera is below a water surface, a full-screen overlay (underwater.gdshader) tints,
/// hazes and softens the picture, and the SFX bus is low-passed so gunshots and the world sound muffled. Sits under the
/// HUD (layer 5; HUD 10, menus 20) so the HUD stays sharp. Costs nothing above the water: the overlay is hidden.
/// The tint follows the day/night cycle (night factor from <see cref="DayNightCycle.Current"/>).
/// </summary>
public partial class UnderwaterView : CanvasLayer
{
    const string ShaderPath = "res://shaders/underwater.gdshader";

    /// <summary>The map's water (set before the node enters the tree); Water.Empty on a dry map does nothing.</summary>
    public Water Water { get; set; } = Water.Empty;

    readonly ColorRect _rect = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    readonly ShaderMaterial _mat = new();
    int _lowPassIdx = -1, _lowPassEffect = -1;
    float _amount;

    /// <summary>The overlay strength now (0 above the surface, 1 fully under).</summary>
    public float Amount => _amount;

    public override void _Ready()
    {
        Layer = 5;
        _mat.Shader = GD.Load<Shader>(ShaderPath);
        _rect.Material = _mat;
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_rect);
        if (DisplayServer.GetName() == "headless" || Water.Bodies.Count == 0) { SetProcess(false); return; }
        AddLowPass();
    }

    public override void _ExitTree()
    {
        if (_lowPassIdx >= 0 && _lowPassEffect >= 0) AudioServer.SetBusEffectEnabled(_lowPassIdx, _lowPassEffect, false);
    }

    /// <summary>One low-pass on the SFX bus, added once and then switched and tuned (no per-frame allocation).</summary>
    void AddLowPass()
    {
        var idx = AudioServer.GetBusIndex("SFX");
        if (idx < 0) return;
        var count = AudioServer.GetBusEffectCount(idx);
        for (var i = 0; i < count && _lowPassEffect < 0; i++)
            if (AudioServer.GetBusEffect(idx, i) is AudioEffectLowPassFilter) _lowPassEffect = i;
        if (_lowPassEffect < 0)
        {
            AudioServer.AddBusEffect(idx, new AudioEffectLowPassFilter { CutoffHz = UnderwaterFx.OpenCutoffHz });
            _lowPassEffect = count;
        }
        _lowPassIdx = idx;
        AudioServer.SetBusEffectEnabled(idx, _lowPassEffect, false);
    }

    public override void _Process(double delta)
    {
        var amount = UnderwaterFx.Amount(DepthBelowSurface());
        _amount = amount;
        var night = DayNightCycle.Current?.NightFactor ?? 0f;
        var tint = UnderwaterFx.Tint(night);
        _rect.Visible = amount > 0.001f;
        if (_rect.Visible)
        {
            _mat.SetShaderParameter("amount", amount);
            _mat.SetShaderParameter("tint", new Vector3(tint.X, tint.Y, tint.Z));
        }
        if (_lowPassIdx >= 0 && _lowPassEffect >= 0)
        {
            AudioServer.SetBusEffectEnabled(_lowPassIdx, _lowPassEffect, amount > 0.001f);
            if (amount > 0.001f) (AudioServer.GetBusEffect(_lowPassIdx, _lowPassEffect) as AudioEffectLowPassFilter)!.CutoffHz = UnderwaterFx.CutoffHz(amount);
        }
    }

    /// <summary>How far the camera is below the surface of the water it is in (0 when in air or over dry ground).</summary>
    float DepthBelowSurface()
    {
        var cam = GetViewport()?.GetCamera3D();
        if (cam == null) return 0f;
        var p = cam.GlobalPosition;
        var body = Water.BodyAt(p.X, p.Z); // the Water queries take Godot ground axes (x, z)
        return body is null ? 0f : body.SurfaceY - p.Y;
    }
}
