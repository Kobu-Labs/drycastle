using Godot;
using Drycastle.Shooter.Maps;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// The minimap in the top left corner (under the control hint line) and what hangs off it: the killstreak row under it and the UAV panel to its right, so they move with its size.
/// Its size, opacity and zoom come from the settings (ticket #143): <see cref="Apply"/> runs once at the build and on every settings change (live preview).
/// </summary>
internal sealed class MinimapPanel
{
    public Minimap Map { get; }
    public StreakRow Streaks { get; }
    public UavPanel Uav { get; }

    /// <param name="markers">What the minimap and the tactical map both show.</param>
    public MinimapPanel(Control root, MapMarkers markers)
    {
        Map = HudUi.Place(new Minimap(markers), 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32, 224, 224);
        root.AddChild(Map);
        Streaks = new StreakRow(root);
        Uav = new UavPanel(root);
    }

    public void Apply()
    {
        var s = GameSettings.Current;
        var size = s.MinimapSize;
        HudUi.Place(Map, 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32, size, size);
        Map.CustomMinimumSize = new Vector2(size, size);
        Map.ZoomMetres = s.MinimapZoom;
        Map.Modulate = new Color(1, 1, 1, s.MinimapOpacity);
        Uav.PlaceBeside(size);
        Streaks.PlaceUnder(size);
    }
}
