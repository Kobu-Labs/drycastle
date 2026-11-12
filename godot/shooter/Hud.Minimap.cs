using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The minimap's size, opacity and zoom from the settings (ticket #143). The streak tracker sits under the minimap, so it
/// moves with its size; the UAV panel sits to its right. Runs once at the build and on every settings change (live preview).
/// </summary>
public partial class Hud
{
    Minimap _minimap = null!;
    /// <summary>What the minimap and the tactical map both show; the one place the match feeds (through <see cref="Minimap"/>'s setters).</summary>
    readonly MapMarkers _mapMarkers = new();

    /// <summary>The HUD's minimap: build it with the map, set the player each frame, feed enemy pings. The tactical map shares its data.</summary>
    public Minimap Minimap => _minimap;

    /// <summary>Minimap placement in the HUD: top-left, under the control hint line.</summary>
    void BuildMinimap()
    {
        _minimap = Place(new Minimap(_mapMarkers), 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32, 224, 224);
        _root.AddChild(_minimap);
    }

    void ApplyMinimapSettings()
    {
        var s = Settings.MinimapSize;
        Place(_minimap, 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32, s, s);
        _minimap.CustomMinimumSize = new Vector2(s, s);
        _minimap.ZoomMetres = Settings.MinimapZoom;
        _minimap.Modulate = new Color(1, 1, 1, Settings.MinimapOpacity);
        if (_uavPanel != null) Place(_uavPanel, 0, 0, ShooterTheme.Margin + s + ShooterTheme.Space, ShooterTheme.Margin + 32, 136, 64); // beside the minimap, whatever its size
        // the streak row is as wide as the minimap: its natural width is scaled down to fit (top left stays put)
        var natural = _streakPanel.GetCombinedMinimumSize();
        Place(_streakPanel, 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32 + s + ShooterTheme.Space, natural.X, natural.Y);
        var k = Math.Min(1f, s / Math.Max(1f, natural.X));
        _streakPanel.Scale = new Vector2(k, k);
    }
}
