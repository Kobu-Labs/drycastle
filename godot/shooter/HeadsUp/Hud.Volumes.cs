using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>The line of the F3 bounding-volume overlay (#479): which volume type is shown, under the sandbox badge. Text, ignores the mouse, hidden when the overlay is off.</summary>
public partial class Hud
{
    Label? _volumeLine;

    /// <summary>The mode line to show, or null to hide it (overlay off).</summary>
    public void SetVolumeMode(string? text)
    {
        if (text == null) { if (_volumeLine != null) _volumeLine.Visible = false; return; }
        if (_volumeLine == null)
        {
            _volumeLine = L(text, "HudSmall", HorizontalAlignment.Right);
            _root.AddChild(Place(_volumeLine, 1, 0, -ShooterTheme.Margin - 480, ShooterTheme.Margin + 64, 480, 24));
        }
        _volumeLine.Text = text; _volumeLine.Visible = true;
    }
}
