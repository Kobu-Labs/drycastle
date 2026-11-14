using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The tactical map overlay (the M key): a full-screen, north-up map sheet of the whole map (see <see cref="TacticalMap"/>).
/// It lives under the HUD root, above the panels and the minimap but below the scoreboard, the post-match XP panel and the
/// chat, so the control-mode switch hides it with the rest of the HUD. It reads the same <see cref="MapMarkers"/> as the
/// minimap, which the match already feeds, so nothing extra is fed. Toggling does not pause anything; while it is open the shell frees the mouse (it asks <see cref="TacticalMapOpen"/>) and the legs keep working (<see cref="Player.MoveWithFreeMouse"/>). The waypoint it sets is drawn in the world by <see cref="WaypointMarker"/>.
/// While closed it is invisible and does not process, so it costs nothing per frame beyond one flag check.
/// </summary>
public partial class Hud
{
    TacticalMap _tacticalMap = null!;
    WaypointMarker _waypointMarker = null!;

    public bool TacticalMapOpen => _tacticalMap.IsOpen;

    /// <summary>The ground point (x, z) under the mouse on the open map, or null.</summary>
    public Vector2? TacticalMapGroundAtMouse() => _tacticalMap.GroundAtMouse();

    void BuildTacticalMap()
    {
        _waypointMarker = new WaypointMarker(_mapMarkers.Waypoint, () => _minimap.Base) { MouseFilter = Control.MouseFilterEnum.Ignore };
        _waypointMarker.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_waypointMarker);
        _tacticalMap = new TacticalMap(_mapMarkers, () => ChatOpen);
        _tacticalMap.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_tacticalMap);
        // the scoreboard (hold Tab) and the post-match XP panel stay above the map
        _root.MoveChild(_board, -1);
        _root.MoveChild(_xp, -1);
    }

    /// <summary>
    /// The map key: opens the map, or closes it when open. Does nothing (false) while the chat box is open, on the respawn
    /// screen, after the match ends and in a missile or turret view, where the HUD hides its overlays too.
    /// </summary>
    public bool ToggleTacticalMap()
    {
        if (ChatOpen) return false; // the key is typing
        if (_tacticalMap.IsOpen) { _tacticalMap.Close(); return true; }
        if (MapBlocked) return false;
        _equipment?.Close(); // the two overlays never show together
        _tacticalMap.TeleportHint = _sandboxOn;
        _tacticalMap.Open(_minimap.Base);
        return true;
    }

    /// <summary>Closes the map (Esc). True when it was open, so the Esc is used and Pause does not open.</summary>
    public bool CloseTacticalMap()
    {
        if (!_tacticalMap.IsOpen) return false;
        _tacticalMap.Close();
        return true;
    }

    bool MapBlocked => _dead || _phase == MatchPhase.Ending || ControlMode;

    /// <summary>Per frame: closes the map when death, the end of the match or a control session takes the screen. One flag check while closed.</summary>
    void UpdateTacticalMap()
    {
        if (_tacticalMap.IsOpen && MapBlocked) _tacticalMap.Close();
        if (_mapMarkers.HasPlayer) _mapMarkers.Waypoint.ClearIfReached(new System.Numerics.Vector2(_mapMarkers.Player.X, _mapMarkers.Player.Y));
    }
}
