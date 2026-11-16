using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Hunter's Log in the HUD (#336, J): a side panel under the HUD root like the Equipment screen, above the panels but below the scoreboard. The match owner attaches it once
/// (<see cref="AttachHuntersLog"/>); the HUD closes it when death, the end of the match or a control session takes the screen. Nothing about it pauses the game: the mouse mode is the
/// shell's (it asks <see cref="HuntersLogOpen"/>), and Esc goes through <see cref="BackHuntersLog"/>. It never shows with the Equipment screen or the tactical map. While it is open the
/// kill feed moves left of it, so a record announcement is not drawn over the panel.
/// </summary>
public partial class Hud
{
    HuntersLogScreen? _huntersLog;
    bool _feedAside;

    public bool HuntersLogOpen => _huntersLog?.IsOpen ?? false;

    public void AttachHuntersLog(HuntLogEnv env)
    {
        if (_huntersLog != null) return;
        _huntersLog = new HuntersLogScreen(env);
        _root.AddChild(_huntersLog);
        _root.MoveChild(_board, -1);
        _root.MoveChild(_xp, -1);
    }

    /// <summary>The Hunter's Log key: opens it, or closes it when open. Does nothing (false) in the cases the Equipment key does nothing (<see cref="OverlayArbiter.Key"/>).</summary>
    public bool ToggleHuntersLog(bool menuOpen = false, bool chooserOpen = false)
    {
        if (_huntersLog == null) return false;
        switch (OverlayArbiter.Key(Overlay.HuntersLog, _huntersLog.IsOpen, Overlays(menuOpen, chooserOpen)))
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
        _tacticalMap.Close();
        _huntersLog.Open();
        SetFeedAside(true);
    }

    public void CloseHuntersLog()
    {
        if (_huntersLog is not { IsOpen: true }) return;
        _huntersLog.Close();
        SetFeedAside(false);
    }

    /// <summary>Esc: another player's log goes up to My Log, else the screen closes. True when it was open, so the Esc is used and Pause does not open.</summary>
    public bool BackHuntersLog()
    {
        if (_huntersLog is not { IsOpen: true }) return false;
        var open = _huntersLog.HandleBack();
        if (!_huntersLog.IsOpen) SetFeedAside(false);
        return open;
    }

    /// <summary>Per frame: closes the screen when death, the end of the match or a control session takes the screen. One flag check while closed.</summary>
    void UpdateHuntersLog()
    {
        if (_huntersLog is { IsOpen: true } && OverlayArbiter.ScreenTaken(Overlays())) CloseHuntersLog();
        if (_feedAside != HuntersLogOpen) SetFeedAside(HuntersLogOpen); // the screen's own close button closed it
    }

    /// <summary>The kill feed sits left of the Hunter's Log panel while it is open (the feed's right edge moves by the panel's share of the screen).</summary>
    void SetFeedAside(bool on)
    {
        _feedAside = on;
        ApplyFeedAside();
    }

    void ApplyFeedAside() => _feedRoot.Position = _feedAside ? new Vector2(-GetViewport().GetVisibleRect().Size.X * ShooterTheme.LogWidthShare, 0) : Vector2.Zero;
}
