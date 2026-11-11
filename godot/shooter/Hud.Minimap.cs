using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The minimap's size, opacity and zoom from the settings (ticket #143). The streak tracker sits under the minimap, so it
/// moves with its size; the UAV panel sits to its right. Runs once at the build and on every settings change (live preview).
/// </summary>
public partial class Hud
{
    void ApplyMinimapSettings()
    {
        var s = Settings.MinimapSize;
        Place(_minimap, 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32, s, s);
        _minimap.CustomMinimumSize = new Vector2(s, s);
        _minimap.ZoomMetres = Settings.MinimapZoom;
        _minimap.Modulate = new Color(1, 1, 1, Settings.MinimapOpacity);
        if (_uavPanel != null) Place(_uavPanel, 0, 0, ShooterTheme.Margin + s + ShooterTheme.Space, ShooterTheme.Margin + 32, 136, 64); // beside the minimap, whatever its size
        Place(_streakPanel, 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32 + s + ShooterTheme.Space, 304, 232);
    }
}
