using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// The breath hold has no meter (the heartbeat and the exhale say how much is left, see <see cref="BreathCues"/>): the HUD only names the key,
/// under the crosshair, while scoped with a full breath that is not being held.
/// </summary>
internal sealed class BreathHint
{
    readonly Label _label;
    string? _key;

    public BreathHint(Control root)
    {
        const float w = 240;
        _label = HudUi.Place(HudUi.L("", "HudSmall", HorizontalAlignment.Center), 0.5f, 0.5f, -w / 2, 40, w, 24);
        _label.Visible = false;
        root.AddChild(_label);
    }

    /// <summary>Whether the weapon has a hold and is scoped, the breath left (0..1) and the phase.</summary>
    public void Update((bool Show, float Meter, BreathState.BreathPhase Phase) b)
    {
        var show = b.Show && b.Phase == BreathState.BreathPhase.Ready && b.Meter >= 0.999f;
        if (_label.Visible != show) _label.Visible = show;
        if (!show) return;
        // the words depend only on the bound key: text is built when it changes (#173, T3-032)
        var key = InputBindings.Name("sprint");
        if (key == _key) return;
        _key = key;
        HudUi.Set(_label, Strings.F("hud.breath.ready", key));
    }
}
