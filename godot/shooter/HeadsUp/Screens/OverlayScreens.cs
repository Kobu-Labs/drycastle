using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Crystals;
using Drycastle.Shooter.HeadsUp.Badges;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Items;
using Drycastle.Shooter.Maps;
using Drycastle.Shooter.Net.Client;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter.HeadsUp.Screens;

/// <summary>
/// The full-screen overlays of the HUD: the tactical map (M), the Equipment screen (F, with the Companion picker of #488), the Hunter's Log (J) and the Mineral Log (N). They never show
/// together, so opening one closes the others; death, the end of the match and a control session close them all (<see cref="OverlayArbiter"/>). None pauses the game: the mouse mode
/// is the shell's (it asks the Open properties), and Esc goes through the Close and Back methods.
/// The map and the Equipment screen live under the HUD root, above the panels but below the scoreboard and the XP panel (the controls given as <c>onTop</c> are moved back above them);
/// the two logs sit on a root of their own after the kill feed's (same theme and scale), so they are drawn above every HUD panel and the kill feed, and a control session that hides the panels does not
/// hide them (it closes them itself).
/// </summary>
internal sealed class OverlayScreens
{
    readonly Control _root;
    readonly Node _layer;
    readonly Minimap _minimap;
    readonly MapMarkers _markers;
    readonly SandboxBadge _sandbox;
    readonly Func<bool, bool, OverlayContext> _overlays;
    readonly Control[] _onTop;
    readonly TacticalMap _map;
    EquipmentScreen? _equipment;
    HuntersLogScreen? _huntersLog;
    MineralLogScreen? _mineralLog;
    Control? _logRoot;

    /// <param name="layer">The HUD layer, which gets the logs' root.</param>
    /// <param name="markers">What the minimap and the tactical map both show.</param>
    /// <param name="overlays">The HUD's state for the arbiter, given whether a menu and the corpse chooser are up.</param>
    /// <param name="onTop">Stay above the screens added to the root (the scoreboard, the XP panel).</param>
    public OverlayScreens(Control root, Node layer, Minimap minimap, MapMarkers markers, SandboxBadge sandbox, Func<bool, bool, OverlayContext> overlays, params Control[] onTop)
    {
        _root = root; _layer = layer; _minimap = minimap; _markers = markers; _sandbox = sandbox; _overlays = overlays; _onTop = onTop;
        var waypoint = new WaypointMarker(markers.Waypoint, () => minimap.Base) { MouseFilter = Control.MouseFilterEnum.Ignore };
        waypoint.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(waypoint);
        _map = new TacticalMap(markers, () => overlays(false, false).ChatOpen);
        _map.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_map);
        KeepOnTop();
    }

    void KeepOnTop()
    {
        foreach (var c in _onTop) _root.MoveChild(c, -1);
    }

    /// <summary>The roots that sit beside the panels root follow the same resolution and UI scale.</summary>
    public void Fit()
    {
        if (_logRoot != null) HudUi.Fit(_logRoot);
    }

    // ---- tactical map ----

    public bool TacticalMapOpen => _map.IsOpen;

    /// <summary>Sandbox: a middle click on the open map picked this ground point (x, z) to teleport to.</summary>
    public Action<Vector2>? TacticalMapTeleport { set => _map.TeleportTo = value; }

    /// <summary>Starts making the tactical map's picture in the background (the map is loaded, the first M press should not stall a frame).</summary>
    public void PrewarmTacticalMap(Vector2 viewport) => _map.Prewarm(_minimap.Base?.World, viewport);

    /// <summary>
    /// The map key: opens the map, or closes it when open. Does nothing (false) while a menu is up, while the chat box is open (the key is typing), on the
    /// respawn screen, after the match ends and in a missile or turret view, where the HUD hides its overlays too (<see cref="OverlayArbiter.Key"/>).
    /// </summary>
    public bool ToggleTacticalMap(bool menuOpen)
    {
        switch (OverlayArbiter.Key(Overlay.TacticalMap, _map.IsOpen, _overlays(menuOpen, false)))
        {
            case OverlayKeyVerdict.Close: _map.Close(); return true;
            case OverlayKeyVerdict.Open:
                _equipment?.Close(); // the overlays never show together
                CloseHuntersLog();
                CloseMineralLog();
                _map.TeleportHint = _sandbox.On;
                _map.Open(_minimap.Base?.World);
                return true;
            default: return false;
        }
    }

    /// <summary>Closes the map (Esc). True when it was open, so the Esc is used and Pause does not open.</summary>
    public bool CloseTacticalMap()
    {
        if (!_map.IsOpen) return false;
        _map.Close();
        return true;
    }

    // ---- equipment ----

    public bool EquipmentOpen => _equipment?.IsOpen ?? false;

    /// <summary>Builds the screen for the local player's equipment (once per match).</summary>
    public void AttachEquipment(NetClient client)
    {
        if (_equipment != null) return;
        _equipment = new EquipmentScreen(client);
        _root.AddChild(_equipment);
        KeepOnTop();
    }

    /// <summary>
    /// The equipment key: opens the screen, or closes it when open. Does nothing (false) while a menu or the corpse chooser is up, while the chat box is open,
    /// on the respawn screen, after the match ends and in a missile or turret view (<see cref="OverlayArbiter.Key"/>).
    /// </summary>
    public bool ToggleEquipment(bool menuOpen, bool chooserOpen)
    {
        if (_equipment == null) return false;
        switch (OverlayArbiter.Key(Overlay.Equipment, _equipment.IsOpen, _overlays(menuOpen, chooserOpen)))
        {
            case OverlayKeyVerdict.Close: _equipment.Close(); return true;
            case OverlayKeyVerdict.Open: _map.Close(); CloseHuntersLog(); CloseMineralLog(); _equipment.Open(); return true;
            default: return false;
        }
    }

    /// <summary>Closes the screen (Esc). True when it was open, so the Esc is used and Pause does not open.</summary>
    public bool CloseEquipment()
    {
        if (_equipment is not { IsOpen: true }) return false;
        _equipment.Close();
        Sfx.Ui("ui.back");
        return true;
    }

    // ---- Hunter's Log ----

    public bool HuntersLogOpen => _huntersLog?.IsOpen ?? false;

    public void AttachHuntersLog(HuntLogEnv env)
    {
        if (_huntersLog != null) return;
        _huntersLog = new HuntersLogScreen(env);
        // beside the panels root like the feed, after it: drawn over the feed and the panels, and a control session that hides the root does not hide it (it closes the log itself)
        _logRoot = new Control { Theme = _root.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_logRoot);
        Fit();
        _logRoot.AddChild(_huntersLog);
    }

    /// <summary>The Hunter's Log key: opens it, or closes it when open. Does nothing (false) in the cases the Equipment key does nothing.</summary>
    public bool ToggleHuntersLog(bool menuOpen, bool chooserOpen)
    {
        if (_huntersLog == null) return false;
        switch (OverlayArbiter.Key(Overlay.HuntersLog, _huntersLog.IsOpen, _overlays(menuOpen, chooserOpen)))
        {
            case OverlayKeyVerdict.Close: CloseHuntersLog(); return true;
            case OverlayKeyVerdict.Open: OpenHuntersLog(); return true;
            default: return false;
        }
    }

    /// <summary>Opens it (also from the pause menu), closing the other overlays: they never show together.</summary>
    public void OpenHuntersLog()
    {
        if (_huntersLog == null || _huntersLog.IsOpen) return;
        _equipment?.Close();
        _map.Close();
        _mineralLog?.Close();
        _huntersLog.Open();
    }

    public void CloseHuntersLog()
    {
        if (_huntersLog is not { IsOpen: true }) return;
        _huntersLog.Close();
    }

    /// <summary>Esc: another player's log goes up to My Log, else the screen closes. True when it was open, so the Esc is used and Pause does not open.</summary>
    public bool BackHuntersLog()
    {
        if (_huntersLog is not { IsOpen: true }) return false;
        return _huntersLog.HandleBack();
    }

    // ---- Mineral Log ----

    public bool MineralLogOpen => _mineralLog?.IsOpen ?? false;

    /// <summary>After <see cref="AttachHuntersLog"/>: the two logs share its layer.</summary>
    public void AttachMineralLog()
    {
        if (_mineralLog != null || _logRoot == null) return;
        _mineralLog = new MineralLogScreen();
        _logRoot.AddChild(_mineralLog);
    }

    /// <summary>The Mineral Log key: opens it, or closes it when open. Does nothing (false) in the cases the Equipment key does nothing.</summary>
    public bool ToggleMineralLog(bool menuOpen, bool chooserOpen)
    {
        if (_mineralLog == null) return false;
        switch (OverlayArbiter.Key(Overlay.MineralLog, _mineralLog.IsOpen, _overlays(menuOpen, chooserOpen)))
        {
            case OverlayKeyVerdict.Close: CloseMineralLog(); return true;
            case OverlayKeyVerdict.Open: OpenMineralLog(); return true;
            default: return false;
        }
    }

    /// <summary>The player's finds from the server (mineral id to the place among its finders): the log's cards turn over.</summary>
    public void SetMineralFinds(IReadOnlyDictionary<string, int> ranks) => _mineralLog?.SetFinds(ranks);

    /// <summary>Opens it (also from the pause menu), closing the other overlays: they never show together.</summary>
    public void OpenMineralLog()
    {
        if (_mineralLog == null || _mineralLog.IsOpen) return;
        _equipment?.Close();
        _map.Close();
        _huntersLog?.Close();
        _mineralLog.Open();
    }

    public void CloseMineralLog()
    {
        if (_mineralLog is not { IsOpen: true }) return;
        _mineralLog.Close();
    }

    /// <summary>Esc closes it. True when it was open, so the Esc is used and Pause does not open.</summary>
    public bool BackMineralLog() => _mineralLog is { IsOpen: true } log && log.HandleBack();

    // ---- per frame ----

    /// <summary>Closes what is open when death, the end of the match or a control session takes the screen, and drops a waypoint the player reached. One flag check per closed screen.</summary>
    public void Tick()
    {
        if (_map.IsOpen && OverlayArbiter.ScreenTaken(_overlays(false, false))) _map.Close();
        if (_markers.HasPlayer) _markers.Waypoint.ClearIfReached(new System.Numerics.Vector2(_markers.Player.X, _markers.Player.Y));
        if (_equipment is { IsOpen: true } && OverlayArbiter.ScreenTaken(_overlays(false, false))) _equipment.Close();
        if (_huntersLog is { IsOpen: true } && OverlayArbiter.ScreenTaken(_overlays(false, false))) CloseHuntersLog();
        if (_mineralLog is { IsOpen: true } && OverlayArbiter.ScreenTaken(_overlays(false, false))) CloseMineralLog();
    }
}
