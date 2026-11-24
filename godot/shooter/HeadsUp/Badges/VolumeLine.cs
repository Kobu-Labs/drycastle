using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp.Badges;

/// <summary>The line of the F3 bounding-volume overlay (#479): which volume type is shown, under the sandbox badge. Text, ignores the mouse, hidden when the overlay is off.</summary>
internal sealed class VolumeLine
{
    readonly Control _root;
    Label? _line;

    public VolumeLine(Control root) => _root = root;

    /// <summary>The mode line to show, or null to hide it (overlay off).</summary>
    public void Set(string? text)
    {
        if (text == null) { if (_line != null) _line.Visible = false; return; }
        if (_line == null)
        {
            _line = HudUi.L(text, "HudSmall", HorizontalAlignment.Right);
            _root.AddChild(HudUi.Place(_line, 1, 0, -ShooterTheme.Margin - 480, ShooterTheme.Margin + 64, 480, 24));
        }
        _line.Text = text; _line.Visible = true;
    }
}
