using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Hunter's Log in the HUD (#336, J): a centred panel on its own top layer (a root of its own after the kill feed's, same theme and scale), so it is drawn above every HUD
/// panel, the scoreboard and the kill feed, which stays where it is. The match owner attaches it once (<see cref="AttachHuntersLog"/>); the HUD closes it when death, the end of the
/// match or a control session takes the screen. Nothing about it pauses the game: the mouse mode is the shell's (it asks <see cref="HuntersLogOpen"/>), and Esc goes through
/// <see cref="BackHuntersLog"/>. It never shows with the Equipment screen or the tactical map.
/// </summary>
public partial class Hud
{
    HuntersLogScreen? _huntersLog;
    Control _logRoot = null!;

    public bool HuntersLogOpen => _huntersLog?.IsOpen ?? false;

    public void AttachHuntersLog(HuntLogEnv env)
    {
        if (_huntersLog != null) return;
        _huntersLog = new HuntersLogScreen(env);
        // beside _root like the feed, after it: drawn over the feed and the panels, and a control session that hides _root does not hide it (it closes the log itself)
        _logRoot = new Control { Theme = _root.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_logRoot);
        FitOverlayRoots();
        _logRoot.AddChild(_huntersLog);
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

    /// <summary>Per frame: closes the screen when death, the end of the match or a control session takes the screen. One flag check while closed.</summary>
    void UpdateHuntersLog()
    {
        if (_huntersLog is { IsOpen: true } && OverlayArbiter.ScreenTaken(Overlays())) CloseHuntersLog();
    }
}
