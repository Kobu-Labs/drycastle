using Drycastle.Shooter.Crystals;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// The Mineral Log in the HUD (N): the second collection log, on the same top layer as the Hunter's Log (<see cref="AttachHuntersLog"/> makes the layer) and under the
/// same rules: it never shows with the Equipment screen, the tactical map or the Hunter's Log, the HUD closes it when death, the end of the match or a control session
/// takes the screen, the mouse mode is the shell's (it asks <see cref="MineralLogOpen"/>), and Esc goes through <see cref="BackMineralLog"/>.
/// </summary>
public partial class Hud
{
    MineralLogScreen? _mineralLog;

    public bool MineralLogOpen => _mineralLog?.IsOpen ?? false;

    /// <summary>After <see cref="AttachHuntersLog"/>: the two logs share its layer.</summary>
    public void AttachMineralLog()
    {
        if (_mineralLog != null || _logRoot == null) return;
        _mineralLog = new MineralLogScreen();
        _logRoot.AddChild(_mineralLog);
    }

    /// <summary>The Mineral Log key: opens it, or closes it when open. Does nothing (false) in the cases the Equipment key does nothing (<see cref="OverlayArbiter.Key"/>).</summary>
    public bool ToggleMineralLog(bool menuOpen = false, bool chooserOpen = false)
    {
        if (_mineralLog == null) return false;
        switch (OverlayArbiter.Key(Overlay.MineralLog, _mineralLog.IsOpen, Overlays(menuOpen, chooserOpen)))
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
        _tacticalMap.Close();
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

    /// <summary>Per frame: closes the screen when death, the end of the match or a control session takes the screen. One flag check while closed.</summary>
    void UpdateMineralLog()
    {
        if (_mineralLog is { IsOpen: true } && OverlayArbiter.ScreenTaken(Overlays())) CloseMineralLog();
    }
}
