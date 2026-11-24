using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>The vehicle readout above the ammo: the speed, or a text in its place (the flak gunner's ammo or a reload word). Hidden when there is none.</summary>
internal sealed class SpeedReadout
{
    readonly Label _label;

    public SpeedReadout(Control root)
    {
        _label = HudUi.Place(HudUi.L("", "HudBig", HorizontalAlignment.Right), 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin - 104, 320, 40);
        _label.Visible = false;
        root.AddChild(_label);
    }

    /// <summary>Speed in km/h; null hides it.</summary>
    public void SetSpeed(float? kmh)
    {
        _label.Visible = kmh != null;
        if (kmh != null) HudUi.Set(_label, Strings.F("hud.vehicle.speed", (int)Math.Round(kmh.Value)));
    }

    /// <summary>The readout as text; null hides it. A change of text only.</summary>
    public void SetText(string? text)
    {
        _label.Visible = text != null;
        if (text != null) HudUi.Set(_label, text);
    }
}
